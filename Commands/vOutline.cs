using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>Native view-dependent silhouettes grouped by their source part.</summary>
[CommandStyle(Style.ScriptRunner)]
public sealed class vOutline : vToolsCommand
{
  // Defaults and customizable constants
  private const bool DefaultParts = true; // true groups each source part separately; false groups all generated outlines together.
  private const bool DefaultInternalEdges = false; // true includes interior edges; false requests silhouettes without internal edges.
  private const bool DefaultFlatten = false; // true projects output to the active construction plane; false preserves native 3D silhouettes.
  private const bool DefaultCleanup = false; // true simplifies output and removes overlaps at document tolerance; false preserves curve structure and overlaps.
  private const bool DefaultJoin = false; // true joins touching output curves within each source part; false leaves separate native curves.
  private const int MaximumCleanupPasses = 100; // Maximum overlap split/delete passes per source; positive integer guarding against stalled cleanup.
  private const string DefaultLayer = CurrentLayer; // Current, Inherit, or a document layer's full path.
  private const string FallbackGroupName = "Outline"; // Group name for unnamed sources or a combined output with different source names.
  private const int FirstDuplicateSuffix = 2; // First numeric suffix for a group name already present in the document.
  private const int ProgressNameLimit = 64; // Maximum source-name characters shown in the single-line progress prompt.
  private const SilhouetteType OutlineTypes = SilhouetteType.Projecting | SilhouetteType.TangentProjects |
    SilhouetteType.Tangent | SilhouetteType.Crease | SilhouetteType.Boundary; // Native silhouette features; excludes seams and smooth interior edges.
  private const SilhouetteType InternalEdgeTypes = SilhouetteType.NonSilhouetteCrease; // Additional native interior creases when InternalEdges is enabled.

  private const string CurrentLayer = "Current";
  private const string InheritLayer = "Inherit";
  private const string SectionName = "vOutline";
  private static bool _parts = DefaultParts;
  private static bool _internalEdges = DefaultInternalEdges;
  private static bool _flatten = DefaultFlatten;
  private static bool _cleanup = DefaultCleanup;
  private static bool _join = DefaultJoin;
  private static string _layer = DefaultLayer;

  public override string EnglishName => "vOutline";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var originalSelection = doc.Objects.GetSelectedObjects(false, false)
      .Select(obj => obj.Id).ToArray();
    var result = SelectSources(doc, mode, out var sources);
    if (result != Result.Success)
      return result;
    return CreateOutlines(doc, sources, originalSelection);
  }

  private Result CreateOutlines(RhinoDoc doc, RhinoObject[] sources, Guid[] originalSelection)
  {
    var created = new List<Guid>();
    var groups = new List<int>();
    var completed = false;
    var promptBefore = RhinoApp.CommandPrompt;
    using var cancellation = new CancellationTokenSource();
    EventHandler cancel = (_, _) => cancellation.Cancel();
    RhinoApp.EscapeKeyPressed += cancel;
    try
    {
      var outputBySource = new List<(RhinoObject Source, List<Guid> Ids)>();
      var plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      for (var index = 0; index < sources.Length; index++)
      {
        var source = sources[index];
        SetOutlinePrompt(FormatProgress(index, sources.Length, source.Attributes.Name ?? string.Empty), true);
        RhinoApp.Wait();
        cancellation.Token.ThrowIfCancellationRequested();
        var layerIndex = ResolveLayer(doc, source);
        if (layerIndex < 0)
        {
          RhinoApp.WriteLine($"vOutline: output layer '{_layer}' was not found.");
          return Result.Failure;
        }

        var ids = new List<Guid>();
        try
        {
          var result = CreateSilhouette(doc, source.Id, _internalEdges, ids, cancellation.Token);
          if (result != Result.Success && result != Result.Nothing)
            return result;
        }
        finally
        {
          created.AddRange(ids);
        }
        PrepareOutlines(doc, ids, created, plane);
        foreach (var id in ids)
        {
          var obj = doc.Objects.FindId(id);
          if (obj == null)
            throw new InvalidOperationException("A generated outline is no longer available.");
          var attributes = obj.Attributes.Duplicate();
          attributes.RemoveFromAllGroups();
          attributes.LayerIndex = layerIndex;
          if (!doc.Objects.ModifyAttributes(id, attributes, true))
            throw new InvalidOperationException("Could not assign an outline's output layer.");
        }
        if (ids.Count > 0)
          outputBySource.Add((source, ids));
      }
      cancellation.Token.ThrowIfCancellationRequested();

      created.RemoveAll(id => doc.Objects.FindId(id) == null);
      if (created.Count == 0)
      {
        RhinoApp.WriteLine("vOutline: no outlines were created from the selected objects.");
        return Result.Nothing;
      }

      if (_parts)
      {
        foreach (var output in outputBySource)
          AddOutputGroup(doc, SourceName(output.Source), output.Ids, groups);
      }
      else
      {
        var names = outputBySource.Select(output => SourceName(output.Source))
          .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AddOutputGroup(doc, names.Length == 1 ? names[0] : FallbackGroupName, created, groups);
      }

      DuplicateCommandSupport.SelectCreated(doc, created);
      completed = true;
      SetOutlinePrompt(FormatProgress(sources.Length, sources.Length, string.Empty), true);
      RhinoApp.WriteLine($"vOutline: created {created.Count} curves in {groups.Count} groups; sources retained.");
      Log.Write(EnglishName, $"sources={sources.Length} curves={created.Count} groups={groups.Count} parts={_parts} internalEdges={_internalEdges} flatten={_flatten} cleanup={_cleanup} join={_join} layer={_layer}");
      return Result.Success;
    }
    catch (OperationCanceledException)
    {
      return Result.Cancel;
    }
    catch (Exception ex)
    {
      Log.Write(EnglishName, $"Outline creation failed: {ex}");
      RhinoApp.WriteLine($"vOutline: {ex.Message}");
      return Result.Failure;
    }
    finally
    {
      RhinoApp.EscapeKeyPressed -= cancel;
      if (!completed)
      {
        foreach (var id in created)
          doc.Objects.Delete(id, true);
        foreach (var index in groups)
          doc.Groups.Delete(index);
        DuplicateCommandSupport.SelectCreated(doc, originalSelection);
      }
      SetOutlinePrompt(promptBefore, false);
    }
  }

  private static Result SelectSources(RhinoDoc doc, RunMode mode, out RhinoObject[] sources)
  {
    sources = [];
    using var getter = new GetObject();
    getter.SetCommandPrompt("Select objects to outline");
    getter.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh;
    getter.SubObjectSelect = false;
    getter.GroupSelect = true;
    getter.EnableTransparentCommands(true);
    getter.EnablePreSelect(true, true);
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;
    getter.AcceptNothing(true);
    while (true)
    {
      getter.ClearCommandOptions();
      var parts = new OptionToggle(_parts, "No", "Yes");
      var internalEdges = new OptionToggle(_internalEdges, "No", "Yes");
      var flatten = new OptionToggle(_flatten, "No", "Yes");
      var cleanup = new OptionToggle(_cleanup, "No", "Yes");
      var join = new OptionToggle(_join, "No", "Yes");
      getter.AddOptionToggle("Parts", ref parts);
      getter.AddOptionToggle("InternalEdges", ref internalEdges);
      getter.AddOptionToggle("Flatten", ref flatten);
      getter.AddOptionToggle("Cleanup", ref cleanup);
      getter.AddOptionToggle("Join", ref join);
      var layerIndex = getter.AddOption("Layer", _layer);
      var getResult = getter.GetMultiple(1, 0);
      if (parts.CurrentValue != _parts || internalEdges.CurrentValue != _internalEdges ||
          flatten.CurrentValue != _flatten || cleanup.CurrentValue != _cleanup || join.CurrentValue != _join)
      {
        _parts = parts.CurrentValue;
        _internalEdges = internalEdges.CurrentValue;
        _flatten = flatten.CurrentValue;
        _cleanup = cleanup.CurrentValue;
        _join = join.CurrentValue;
        SaveOptions();
      }
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      if (getResult == GetResult.Option)
      {
        if (getter.Option()?.Index == layerIndex &&
            LayerSelector.TrySelect(doc, _layer, CurrentLayer, "vOutline output layer", mode,
              allowNewLayer: false,
              new[] { new LayerSelector.SpecialChoice(InheritLayer, "Inherit source layer") },
              out var selected))
        {
          _layer = NormalizeLayer(selected);
          SaveOptions();
        }
        continue;
      }
      if (getResult == GetResult.Object && getter.ObjectsWerePreselected)
      {
        getter.EnablePreSelect(false, true);
        continue;
      }
      if (getResult is not (GetResult.Object or GetResult.Nothing))
        return Result.Cancel;
      sources = getter.Objects().Select(reference => reference.Object())
        .Where(obj => obj != null).DistinctBy(obj => obj.Id).ToArray();
      return sources.Length > 0 ? Result.Success : Result.Nothing;
    }
  }

  private static void PrepareOutlines(RhinoDoc doc, List<Guid> ids, List<Guid> created, Plane plane)
  {
    if (!_flatten && !_cleanup && !_join)
      return;
    var tolerance = doc.ModelAbsoluteTolerance;
    foreach (var id in ids.ToArray())
    {
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not Curve source)
        throw new InvalidOperationException("A generated outline is no longer available.");
      using var curve = _flatten ? Curve.ProjectToPlane(source, plane) : source.DuplicateCurve();
      if (_flatten && (curve == null || !curve.IsValid))
      {
        // Silhouette edges perpendicular to the construction plane collapse to points.
        if (source.GetBoundingBox(Transform.PlanarProjection(plane)).Diagonal.Length > tolerance)
          throw new InvalidOperationException("Could not flatten an outline onto the construction plane.");
        DeleteOutline(doc, id);
        ids.Remove(id);
        continue;
      }
      if (curve == null || !curve.IsValid)
        throw new InvalidOperationException("Native Silhouette produced an invalid outline.");
      if (_cleanup)
      {
        curve.RemoveShortSegments(tolerance);
        if (curve.GetLength() <= tolerance)
        {
          DeleteOutline(doc, id);
          ids.Remove(id);
          continue;
        }
      }
      using var simplified = _cleanup
        ? curve.Simplify(CurveSimplifyOptions.All, tolerance, doc.ModelAngleToleranceRadians)
        : null;
      if ((_flatten || _cleanup) && !doc.Objects.Replace(id, simplified ?? curve))
        throw new InvalidOperationException("Could not update a processed outline.");
    }

    if (_cleanup)
      RemoveOverlaps(doc, ids, created, tolerance);
    if (!_join || ids.Count < 2)
      return;

    var objects = ids.Select(id => doc.Objects.FindId(id) ??
      throw new InvalidOperationException("An outline to join is no longer available.")).ToArray();
    var joined = Curve.JoinCurves(objects.Select(obj => (Curve)obj.Geometry), tolerance, preserveDirection: false);
    if (joined == null || joined.Length == 0)
      throw new InvalidOperationException("Could not join the generated outlines.");
    try
    {
      if (joined.Length == ids.Count)
        return;
      var joinedIds = new List<Guid>();
      foreach (var curve in joined)
      {
        using var simplified = _cleanup
          ? curve.Simplify(CurveSimplifyOptions.All, tolerance, doc.ModelAngleToleranceRadians)
          : null;
        var id = doc.Objects.AddCurve(simplified ?? curve, objects[0].Attributes.Duplicate());
        if (id == Guid.Empty)
          throw new InvalidOperationException("Could not add a joined outline.");
        created.Add(id);
        joinedIds.Add(id);
      }
      foreach (var id in ids)
        DeleteOutline(doc, id);
      ids.Clear();
      ids.AddRange(joinedIds);
    }
    finally
    {
      foreach (var curve in joined)
        curve.Dispose();
    }
  }

  private static void RemoveOverlaps(RhinoDoc doc, List<Guid> ids, List<Guid> created, double tolerance)
  {
    for (var pass = 0; pass < MaximumCleanupPasses; pass++)
    {
      var objects = ids.Select(id => doc.Objects.FindId(id))
        .Where(obj => obj?.Geometry is Curve).Cast<RhinoObject>().ToArray();
      var overlaps = OverlapFinder.Find(objects, tolerance, objects);
      if (overlaps.CoveredObjectIds.Count == 0 && overlaps.PartialOverlapSpans.Count == 0)
        return;
      var pieces = OverlapSegmentProcessor.Materialize(doc, overlaps.PartialOverlapSpans,
        overlaps.CoveredObjectIds, tolerance);
      var newIds = pieces.ResultObjectIds.Except(ids).ToArray();
      created.AddRange(newIds);
      ids.AddRange(newIds);
      var remove = overlaps.CoveredObjectIds.Union(pieces.OverlapObjectIds).ToHashSet();
      if (remove.Count == 0)
        throw new InvalidOperationException("Could not resolve an overlapping outline segment.");
      foreach (var id in remove)
        DeleteOutline(doc, id);
      ids.RemoveAll(remove.Contains);
    }
    throw new InvalidOperationException("Outline overlap cleanup exceeded its pass limit.");
  }

  private static void DeleteOutline(RhinoDoc doc, Guid id)
  {
    if (!doc.Objects.Delete(id, true))
      throw new InvalidOperationException("Could not remove a generated outline.");
  }

  private static Result CreateSilhouette(RhinoDoc doc, Guid sourceId, bool internalEdges, List<Guid> ids,
    CancellationToken cancellationToken)
  {
    var source = doc.Objects.FindId(sourceId) ??
      throw new InvalidOperationException("A source object is no longer available.");
    var viewport = doc.Views.ActiveView?.ActiveViewport ??
      throw new InvalidOperationException("No active viewport is available.");
    using var view = new ViewportInfo(viewport);
    // Native Silhouette treats extrusions as breps so cap edges are split at silhouette crossings.
    using var brep = source.Geometry switch
    {
      Extrusion extrusion => extrusion.ToBrep(),
      Surface surface => surface.ToBrep(),
      _ => null
    };
    var types = OutlineTypes | (internalEdges ? InternalEdgeTypes : SilhouetteType.None);
    var silhouettes = Silhouette.Compute(brep ?? source.Geometry, types, view,
      doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians, null, cancellationToken);
    RhinoApp.Wait();
    cancellationToken.ThrowIfCancellationRequested();
    if (silhouettes == null)
      throw new InvalidOperationException("Silhouette calculation failed.");
    foreach (var silhouette in silhouettes)
    {
      using var curve = silhouette.Curve;
      cancellationToken.ThrowIfCancellationRequested();
      if (curve == null)
        continue; // Projected points have no output curve, matching native Silhouette.
      if (!curve.IsValid)
        throw new InvalidOperationException("Silhouette calculation produced an invalid outline.");
      var id = doc.Objects.AddCurve(curve);
      if (id == Guid.Empty)
        throw new InvalidOperationException("Could not add a generated outline.");
      ids.Add(id);
    }
    return ids.Count > 0 ? Result.Success : Result.Nothing;
  }

  private static string FormatProgress(int completed, int total, string name)
  {
    var percent = total > 0 ? 100.0 * completed / total : 100.0;
    var count = completed.ToString(CultureInfo.CurrentCulture);
    var label = name.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
    if (label.Length > ProgressNameLimit) label = label[..(ProgressNameLimit - 3)] + "...";
    var progress = $"vOutline: {count}/{total} ({percent:0}%)";
    return label.Length == 0 ? progress : $"{progress} [{label}]";
  }

  private static void SetOutlinePrompt(string prompt, bool progress)
  {
    var echo = AppearanceSettings.EchoPromptsToHistoryWindow;
    try
    {
      AppearanceSettings.EchoPromptsToHistoryWindow = false;
      if (progress) RhinoApp.SetCommandPromptMessage(prompt);
      else RhinoApp.SetCommandPrompt(prompt);
    }
    finally { AppearanceSettings.EchoPromptsToHistoryWindow = echo; }
  }

  private static int ResolveLayer(RhinoDoc doc, RhinoObject source) =>
    string.Equals(_layer, InheritLayer, StringComparison.OrdinalIgnoreCase)
      ? source.Attributes.LayerIndex
      : string.Equals(_layer, CurrentLayer, StringComparison.OrdinalIgnoreCase)
        ? doc.Layers.CurrentLayerIndex
        : doc.Layers.FindByFullPath(_layer, -1);

  private static string SourceName(RhinoObject source) =>
    string.IsNullOrWhiteSpace(source.Attributes.Name) ? FallbackGroupName : source.Attributes.Name.Trim();

  private static void AddOutputGroup(RhinoDoc doc, string name, IEnumerable<Guid> ids, List<int> groups)
  {
    var members = ids.Distinct().ToArray();
    if (members.Length < 2)
      return;
    var candidate = name;
    for (var suffix = FirstDuplicateSuffix; doc.Groups.FindName(candidate) != null; suffix++)
      candidate = $"{name} ({suffix})";
    var index = doc.Groups.Add(candidate, members);
    if (index < 0)
      throw new InvalidOperationException("Could not group the generated outlines.");
    groups.Add(index);
  }

  private static string NormalizeLayer(string value) =>
    string.IsNullOrWhiteSpace(value) || LayerSelector.IsCurrentLayerValue(value, CurrentLayer)
      ? DefaultLayer : value.Trim();

  private static void LoadOptions()
  {
    (_parts, _internalEdges, _layer) = ToolsOptionStore.Read(SectionName, section =>
      (ToolsOptionStore.TryGetBool(section, "parts", out var parts) ? parts : DefaultParts,
       ToolsOptionStore.TryGetBool(section, "internalEdges", out var edges) ? edges : DefaultInternalEdges,
       ToolsOptionStore.TryGetString(section, "layer", out var layer) ? NormalizeLayer(layer) : DefaultLayer));
    (_flatten, _cleanup, _join) = ToolsOptionStore.Read(SectionName, section =>
      (ToolsOptionStore.TryGetBool(section, "flatten", out var flatten) ? flatten : DefaultFlatten,
       ToolsOptionStore.TryGetBool(section, "cleanup", out var cleanup) ? cleanup : DefaultCleanup,
       ToolsOptionStore.TryGetBool(section, "join", out var join) ? join : DefaultJoin));
  }

  private static void SaveOptions()
  {
    if (!ToolsOptionStore.Update(SectionName, section =>
        {
          section["parts"] = _parts;
          section["internalEdges"] = _internalEdges;
          section["flatten"] = _flatten;
          section["cleanup"] = _cleanup;
          section["join"] = _join;
          section["layer"] = _layer;
        }))
      Log.Write(SectionName, $"Could not save options: {ToolsOptionStore.LastError}");
  }
}
