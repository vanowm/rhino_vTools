using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>Transparently toggles perspective/plan, toggles projection, or activates a standard view.</summary>
[CommandStyle(Style.Transparent | Style.NotUndoable)]
public sealed class vTogglePerspectivePlan : vToolsCommand
{
  // Defaults and view conventions
  private const ViewMode DefaultMode = ViewMode.Toggle; // Toggle follows the current tab; Projection toggles its projection only; named modes force a view.
  private const string PerspectiveViewName = "Perspective"; // View-tab name matched after trimming, without case sensitivity.
  private const double PerspectiveLensLength = 50.0; // Positive focal length in millimeters when this viewport has no saved valid perspective lens.
  private const bool SymmetricFrustum = true; // true centers the projection frustum; false allows an asymmetric frustum.
  private const bool UpdateConstructionPlane = true; // true aligns the CPlane when setting a standard/plan view; false preserves the current CPlane.
  private static readonly Plane DefaultPlanPlane = Plane.WorldXY; // World XY fallback if the viewport has no valid construction plane.

  private const string Tag = "vTogglePerspectivePlan";
  private static readonly ConditionalWeakTable<RhinoDoc, Dictionary<Guid, double>> PerspectiveLenses = new();
  private static readonly ConditionalWeakTable<RhinoDoc, HashSet<Guid>> PlanFirstViews = new();

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var requested = DefaultMode;
    if (mode == RunMode.Scripted)
    {
      var result = GetRequestedMode(out requested);
      if (result != Result.Success)
        return result;
    }

    try
    {
      var current = doc.Views.ActiveView;
      var view = current;
      if (requested != ViewMode.Projection)
      {
        var views = doc.Views.GetStandardRhinoViews() ?? [];
        view = requested is ViewMode.Toggle or ViewMode.Plan
          ? FindView(views, current, PerspectiveViewName) ??
            views.FirstOrDefault(candidate => candidate.ActiveViewport.IsPerspectiveProjection) ?? current
          : FindView(views, current, requested.ToString()) ?? current;
      }
      var viewport = view?.ActiveViewport;
      if (view == null || viewport == null)
      {
        Log.Write(Tag, $"no viewport available; requested={requested}");
        RhinoApp.WriteLine($"{Tag}: no viewport available.");
        return Result.Nothing;
      }

      var planFirstViews = PlanFirstViews.GetOrCreateValue(doc);
      var resolved = requested == ViewMode.Toggle
        ? MatchesView(current, PerspectiveViewName) && !viewport.IsPerspectiveProjection &&
          viewport.IsPlanView && !planFirstViews.Contains(viewport.Id)
          ? ViewMode.Perspective
          : ViewMode.Plan
        : requested;
      var existingStandardView = requested is not (ViewMode.Toggle or ViewMode.Projection or ViewMode.Plan or ViewMode.Perspective) &&
                                 MatchesView(view, requested.ToString());

      if (view != current)
        doc.Views.ActiveView = view;
      var applied = existingStandardView || ApplyProjection(doc, viewport, resolved);
      if (!applied)
      {
        Log.Write(Tag, $"view change failed; requested={requested} resolved={resolved} view={viewport.Name}");
        RhinoApp.WriteLine($"{Tag}: could not switch to {resolved}.");
        return Result.Failure;
      }

      if (requested == ViewMode.Projection)
        planFirstViews.Add(viewport.Id);
      else
        planFirstViews.Remove(viewport.Id);

      view.Redraw();
      Log.Write(Tag,
        $"requested={requested} resolved={resolved} view={viewport.Name} existing_tab={existingStandardView} perspective={viewport.IsPerspectiveProjection} plan={viewport.IsPlanView}" +
        (viewport.IsPerspectiveProjection ? $" lens_mm={viewport.Camera35mmLensLength:G17}" : ""));
      RhinoApp.WriteLine(requested == ViewMode.Projection
        ? $"Projection: {(viewport.IsPerspectiveProjection ? "Perspective" : "Parallel")}"
        : $"Mode: {resolved}");
      return Result.Success;
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"view change failed: {ex}");
      RhinoApp.WriteLine($"{Tag}: {ex.Message}");
      return Result.Failure;
    }
  }

  private static Result GetRequestedMode(out ViewMode requested)
  {
    requested = DefaultMode;
    using var getter = new GetOption();
    getter.SetCommandPrompt("View mode");
    getter.SetCommandPromptDefault(DefaultMode.ToString());
    getter.AcceptNothing(true);
    var options = new Dictionary<int, ViewMode>();
    foreach (var choice in Enum.GetValues<ViewMode>())
      options[getter.AddOption(choice.ToString())] = choice;

    var result = getter.Get();
    if (getter.CommandResult() != Result.Success)
      return getter.CommandResult();
    if (result == GetResult.Nothing)
      return Result.Success;
    if (result == GetResult.Option && options.TryGetValue(getter.Option().Index, out requested))
      return Result.Success;

    return Result.Failure;
  }

  private static RhinoView? FindView(IEnumerable<RhinoView> views, RhinoView? current, string name) =>
    MatchesView(current, name) ? current : views.FirstOrDefault(view => MatchesView(view, name));

  private static bool MatchesView(RhinoView? view, string name) =>
    string.Equals(view?.ActiveViewport?.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase);

  private static bool ApplyProjection(RhinoDoc doc, RhinoViewport viewport, ViewMode mode)
  {
    var lenses = PerspectiveLenses.GetOrCreateValue(doc);
    // Parallel projection changes the frustum, so its derived lens is not the perspective lens.
    if (viewport.IsPerspectiveProjection)
    {
      var currentLens = viewport.Camera35mmLensLength;
      if (double.IsFinite(currentLens) && currentLens > 0.0)
        lenses[viewport.Id] = currentLens;
    }
    var lensLength = lenses.TryGetValue(viewport.Id, out var savedLens) ? savedLens : PerspectiveLensLength;

    if (mode == ViewMode.Projection)
    {
      if (viewport.IsPerspectiveProjection)
        return viewport.ChangeToParallelProjection(SymmetricFrustum);

      return viewport.ChangeToPerspectiveProjection(SymmetricFrustum, lensLength);
    }
    if (mode == ViewMode.Perspective)
      return viewport.ChangeToPerspectiveProjection(SymmetricFrustum, lensLength);
    if (mode == ViewMode.Plan)
    {
      var plane = viewport.GetConstructionPlane()?.Plane ?? DefaultPlanPlane;
      if (!plane.IsValid)
        plane = DefaultPlanPlane;
      return viewport.ChangeToParallelProjection(SymmetricFrustum) &&
             viewport.SetToPlanView(plane.Origin, plane.XAxis, plane.YAxis, UpdateConstructionPlane);
    }

    var projection = mode switch
    {
      ViewMode.Top => DefinedViewportProjection.Top,
      ViewMode.Bottom => DefinedViewportProjection.Bottom,
      ViewMode.Front => DefinedViewportProjection.Front,
      ViewMode.Back => DefinedViewportProjection.Back,
      ViewMode.Left => DefinedViewportProjection.Left,
      ViewMode.Right => DefinedViewportProjection.Right,
      _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    return viewport.SetProjection(projection, null, UpdateConstructionPlane);
  }

  private enum ViewMode
  {
    Toggle,
    Projection,
    Plan,
    Perspective,
    Top,
    Bottom,
    Front,
    Back,
    Left,
    Right
  }
}
