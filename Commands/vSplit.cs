using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native interactive split-at-picked-points command ported from splitAt.py.
/// </summary>
public sealed class vSplit : Command
{
  private const string OptionsSectionName = "vSplit";
  private const string GripsKey = "grips";

  public override string EnglishName => "vSplit";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var showGrips = LoadPersistedGrips();

    var targets = GetTargetCurves(doc);
    if (targets == null || targets.Count == 0)
      return Result.Cancel;

    var completed = CollectSplitPoints(doc, targets, showGrips);
    if (!completed)
    {
      RestoreTargetGrips(doc, targets);
      SelectExistingTargets(doc, targets);
      return Result.Cancel;
    }

    var newIds = ApplySplits(doc, targets);
    if (newIds.Count > 0)
      RhinoApp.WriteLine($"vSplit: split {newIds.Count} curve piece{(newIds.Count == 1 ? "" : "s")}.");

    return Result.Success;
  }

  private static bool LoadPersistedGrips()
  {
    return ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(section, GripsKey, out var grips) ? grips : true);
  }

  private static void SavePersistedOptions(bool showGrips)
  {
    _ = ToolsOptionStore.Update(OptionsSectionName, section => section[GripsKey] = showGrips);
  }

  private static double ModelTolerance(RhinoDoc doc) => Math.Max(doc.ModelAbsoluteTolerance, 1e-6);

  private static double ParameterTolerance(Curve curve)
  {
    var domain = curve.Domain;
    return Math.Max(Math.Abs(domain.T1 - domain.T0) * 1e-9, 1e-10);
  }

  private static double PointHitTolerance(RhinoDoc doc, Curve curve)
  {
    var length = 0.0;
    try { length = curve.GetLength(); }
    catch { /* ignored */ }

    return Math.Max(Math.Max(ModelTolerance(doc) * 4.0, length * 1e-8), 1e-6);
  }

  private static List<SplitTarget>? GetTargetCurves(RhinoDoc doc)
  {
    var go = new GetObject();
    go.SetCommandPrompt("Select curves to split");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(false);

    var result = go.GetMultiple(1, 0);
    if (result != GetResult.Object || go.CommandResult() != Result.Success)
      return null;

    var targets = new List<SplitTarget>();
    var seen = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      var rhObj = objRef?.Object();
      var curve = objRef?.Curve();
      if (rhObj == null || curve == null || !seen.Add(rhObj.Id))
        continue;

      var duplicate = curve.DuplicateCurve();
      if (duplicate == null)
        continue;

      targets.Add(new SplitTarget(
        rhObj.Id,
        duplicate,
        rhObj.Attributes?.Duplicate(),
        rhObj.GripsOn));
    }

    if (targets.Count == 0)
    {
      RhinoApp.WriteLine("vSplit: no curves selected.");
      return null;
    }

    return targets;
  }

  private static bool AddUniqueParameter(RhinoDoc doc, SplitTarget target, double parameter, bool reportSkipped)
  {
    var curve = target.Curve;
    var domain = curve.Domain;
    var tol = ParameterTolerance(curve);

    if (!curve.IsClosed &&
        (Math.Abs(parameter - domain.T0) <= tol || Math.Abs(parameter - domain.T1) <= tol))
    {
      if (reportSkipped)
        RhinoApp.WriteLine("vSplit: skipped endpoint split point.");
      return false;
    }

    foreach (var existing in target.Parameters)
    {
      if (Math.Abs(existing - parameter) <= tol)
      {
        if (reportSkipped)
          RhinoApp.WriteLine("vSplit: skipped duplicate split point.");
        return false;
      }
    }

    target.Parameters.Add(parameter);
    return true;
  }

  private static bool RemoveParameter(SplitTarget target, double parameter)
  {
    var tol = ParameterTolerance(target.Curve);
    for (var i = 0; i < target.Parameters.Count; i++)
    {
      if (Math.Abs(target.Parameters[i] - parameter) <= tol)
      {
        target.Parameters.RemoveAt(i);
        return true;
      }
    }

    return false;
  }

  private static List<SplitPointItem> RemoveExistingPointsNearTargets(RhinoDoc doc, IEnumerable<SplitTarget> targets, Point3d point)
  {
    var removed = new List<SplitPointItem>();
    foreach (var target in targets)
    {
      foreach (var parameter in target.Parameters.ToList())
      {
        var splitPoint = target.Curve.PointAt(parameter);
        if (point.DistanceTo(splitPoint) <= PointHitTolerance(doc, target.Curve) &&
            RemoveParameter(target, parameter))
        {
          removed.Add(new SplitPointItem(target, parameter));
        }
      }
    }

    return removed;
  }

  private static void SelectExistingTargets(RhinoDoc doc, IEnumerable<SplitTarget> targets)
  {
    doc.Objects.UnselectAll();
    foreach (var target in targets)
    {
      var rhObj = doc.Objects.FindId(target.ObjectId);
      rhObj?.Select(true);
    }
    doc.Views.Redraw();
  }

  private static void SelectFinalObjects(RhinoDoc doc, IEnumerable<SplitTarget> targets, IEnumerable<Guid> newIds)
  {
    doc.Objects.UnselectAll();

    foreach (var id in newIds)
      doc.Objects.FindId(id)?.Select(true);

    foreach (var target in targets)
      doc.Objects.FindId(target.ObjectId)?.Select(true);

    doc.Views.Redraw();
  }

  private static void SetTargetGrips(RhinoDoc doc, IEnumerable<SplitTarget> targets, bool enabled)
  {
    foreach (var target in targets)
    {
      var rhObj = doc.Objects.FindId(target.ObjectId);
      if (rhObj != null)
        rhObj.GripsOn = enabled;
    }
    doc.Views.Redraw();
  }

  private static void RestoreTargetGrips(RhinoDoc doc, IEnumerable<SplitTarget> targets)
  {
    foreach (var target in targets)
    {
      var rhObj = doc.Objects.FindId(target.ObjectId);
      if (rhObj != null)
        rhObj.GripsOn = target.InitialGrips;
    }
    doc.Views.Redraw();
  }

  private static (List<SplitPointItem> Added, bool PointWasOnTarget) AddPointToTargets(
    RhinoDoc doc,
    IEnumerable<SplitTarget> targets,
    Point3d point)
  {
    var candidates = new List<(SplitTarget Target, double Parameter, double Distance)>();
    (SplitTarget Target, double Parameter, double Distance)? best = null;

    foreach (var target in targets)
    {
      if (!target.Curve.ClosestPoint(point, out var parameter))
        continue;

      var curvePoint = target.Curve.PointAt(parameter);
      var distance = point.DistanceTo(curvePoint);
      var item = (target, parameter, distance);

      if (best == null || distance < best.Value.Distance)
        best = item;

      if (distance <= PointHitTolerance(doc, target.Curve))
        candidates.Add(item);
    }

    if (candidates.Count == 0 && best.HasValue &&
        best.Value.Distance <= PointHitTolerance(doc, best.Value.Target.Curve))
    {
      candidates.Add(best.Value);
    }

    var action = new List<SplitPointItem>();
    foreach (var (target, parameter, _) in candidates)
    {
      if (AddUniqueParameter(doc, target, parameter, reportSkipped: false))
        action.Add(new SplitPointItem(target, parameter));
    }

    return (action, candidates.Count > 0);
  }

  private static void UndoSplitPointAction(RhinoDoc doc, SplitAction? action)
  {
    if (action == null)
      return;

    if (action.Kind == SplitActionKind.Add)
    {
      foreach (var item in action.Items)
        RemoveParameter(item.Target, item.Parameter);
    }
    else
    {
      foreach (var item in action.Items)
        AddUniqueParameter(doc, item.Target, item.Parameter, reportSkipped: false);
    }
  }

  private static SplitAction? RedoSplitPointAction(RhinoDoc doc, SplitAction? action)
  {
    if (action == null)
      return null;

    if (action.Kind == SplitActionKind.Add)
    {
      var restored = new List<SplitPointItem>();
      foreach (var item in action.Items)
      {
        if (AddUniqueParameter(doc, item.Target, item.Parameter, reportSkipped: false))
          restored.Add(item);
      }
      return restored.Count > 0 ? new SplitAction(SplitActionKind.Add, restored) : null;
    }

    var removed = new List<SplitPointItem>();
    foreach (var item in action.Items)
    {
      if (RemoveParameter(item.Target, item.Parameter))
        removed.Add(item);
    }
    return removed.Count > 0 ? new SplitAction(SplitActionKind.Remove, removed) : null;
  }

  private static bool CollectSplitPoints(RhinoDoc doc, List<SplitTarget> targets, bool showGrips)
  {
    var conduit = new SplitPointConduit(targets)
    {
      Enabled = true
    };

    SelectExistingTargets(doc, targets);

    SetTargetGrips(doc, targets, showGrips);

    var undoStack = new Stack<SplitAction>();
    var redoStack = new Stack<SplitAction>();

    try
    {
      while (true)
      {
        var gp = new GetPoint();
        gp.SetCommandPrompt("Point to split at. Press Enter when done");
        gp.AcceptNothing(true);

        var gripsOption = new OptionToggle(showGrips, "Hide", "Show");
        gp.AddOptionToggle("Grips", ref gripsOption);
        var undoOptionIndex = undoStack.Count > 0 ? gp.AddOption("Undo") : -1;
        var redoOptionIndex = redoStack.Count > 0 ? gp.AddOption("Redo") : -1;

        var result = gp.Get();
        if (gp.CommandResult() != Result.Success)
          return false;

        if (result == GetResult.Nothing)
          return true;

        if (result == GetResult.Option)
        {
          var selectedOption = gp.OptionIndex();
          if (undoStack.Count > 0 && selectedOption == undoOptionIndex)
          {
            var action = undoStack.Pop();
            UndoSplitPointAction(doc, action);
            redoStack.Push(action);
            doc.Views.Redraw();
            RhinoApp.WriteLine("vSplit: undo split point.");
            continue;
          }

          if (redoStack.Count > 0 && selectedOption == redoOptionIndex)
          {
            var action = redoStack.Pop();
            var restored = RedoSplitPointAction(doc, action);
            if (restored != null)
              undoStack.Push(restored);
            doc.Views.Redraw();
            RhinoApp.WriteLine("vSplit: redo split point.");
            continue;
          }

          showGrips = gripsOption.CurrentValue;
          SavePersistedOptions(showGrips);
          SetTargetGrips(doc, targets, showGrips);
          continue;
        }

        if (result != GetResult.Point)
          return false;

        var point = gp.Point();
        if (!point.IsValid)
        {
          RhinoApp.WriteLine("vSplit: could not read split point.");
          continue;
        }

        var removed = RemoveExistingPointsNearTargets(doc, targets, point);
        if (removed.Count > 0)
        {
          undoStack.Push(new SplitAction(SplitActionKind.Remove, removed));
          redoStack.Clear();
          RhinoApp.WriteLine("vSplit: removed split point.");
          doc.Views.Redraw();
          continue;
        }

        var (added, pointWasOnTarget) = AddPointToTargets(doc, targets, point);
        if (added.Count == 0)
        {
          RhinoApp.WriteLine(pointWasOnTarget
            ? "vSplit: no new split point added."
            : "vSplit: snap or click near one of the selected curves.");
          continue;
        }

        undoStack.Push(new SplitAction(SplitActionKind.Add, added));
        redoStack.Clear();
        RhinoApp.WriteLine("vSplit: added split point.");
        doc.Views.Redraw();
      }
    }
    finally
    {
      conduit.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static bool ValidPiece(RhinoDoc doc, Curve? piece)
  {
    if (piece == null || !piece.IsValid)
      return false;

    try { return piece.GetLength() > ModelTolerance(doc); }
    catch { return true; }
  }

  private static List<Curve> SplitCurve(RhinoDoc doc, SplitTarget target)
  {
    if (target.Parameters.Count == 0)
      return new List<Curve>();

    var parameters = target.Parameters.OrderBy(p => p).ToArray();
    var pieces = target.Curve.Split(parameters);
    if (pieces == null || pieces.Length < 1)
      return new List<Curve>();

    return pieces
      .Where(piece => ValidPiece(doc, piece))
      .Select(piece => piece.DuplicateCurve())
      .Where(piece => piece != null)
      .Cast<Curve>()
      .ToList();
  }

  private static List<Guid> AddSplitPieces(RhinoDoc doc, SplitTarget target, IEnumerable<Curve> pieces)
  {
    var newIds = new List<Guid>();
    foreach (var piece in pieces)
    {
      var objectId = target.Attributes != null
        ? doc.Objects.AddCurve(piece, target.Attributes.Duplicate())
        : doc.Objects.AddCurve(piece);

      if (objectId == Guid.Empty)
        continue;

      var rhObj = doc.Objects.FindId(objectId);
      if (rhObj != null)
        rhObj.GripsOn = target.InitialGrips;

      newIds.Add(objectId);
    }

    return newIds;
  }

  private static List<Guid> ApplySplits(RhinoDoc doc, List<SplitTarget> targets)
  {
    var splitTargets = targets.Where(target => target.Parameters.Count > 0).ToList();
    if (splitTargets.Count == 0)
    {
      RhinoApp.WriteLine("vSplit: no split points were added.");
      RestoreTargetGrips(doc, targets);
      SelectExistingTargets(doc, targets);
      return new List<Guid>();
    }

    var undoRecord = doc.BeginUndoRecord("vSplit");
    var newIds = new List<Guid>();

    try
    {
      foreach (var target in splitTargets)
      {
        var pieces = SplitCurve(doc, target);
        if (pieces.Count < 1)
        {
          RhinoApp.WriteLine("vSplit: could not split one curve.");
          continue;
        }

        var addedIds = AddSplitPieces(doc, target, pieces);
        foreach (var piece in pieces)
          piece.Dispose();

        if (addedIds.Count < 1)
        {
          RhinoApp.WriteLine("vSplit: could not add split pieces for one curve.");
          continue;
        }

        doc.Objects.Delete(target.ObjectId, true);
        newIds.AddRange(addedIds);
      }
    }
    finally
    {
      if (undoRecord > 0)
        doc.EndUndoRecord(undoRecord);
    }

    RestoreTargetGrips(doc, targets);
    SelectFinalObjects(doc, targets, newIds);
    return newIds;
  }

  private sealed class SplitTarget
  {
    public SplitTarget(Guid objectId, Curve curve, ObjectAttributes? attributes, bool initialGrips)
    {
      ObjectId = objectId;
      Curve = curve;
      Attributes = attributes;
      InitialGrips = initialGrips;
    }

    public Guid ObjectId { get; }
    public Curve Curve { get; }
    public ObjectAttributes? Attributes { get; }
    public bool InitialGrips { get; }
    public List<double> Parameters { get; } = new();
  }

  private readonly record struct SplitPointItem(SplitTarget Target, double Parameter);

  private enum SplitActionKind
  {
    Add,
    Remove
  }

  private sealed record SplitAction(SplitActionKind Kind, List<SplitPointItem> Items);

  private sealed class SplitPointConduit : DisplayConduit
  {
    private readonly List<SplitTarget> _targets;

    public SplitPointConduit(List<SplitTarget> targets)
    {
      _targets = targets;
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      e.Display.EnableDepthTesting(false);
      e.Display.EnableDepthWriting(false);
      try
      {
        foreach (var target in _targets)
        {
          foreach (var parameter in target.Parameters)
          {
            e.Display.DrawPoint(
              target.Curve.PointAt(parameter),
              PointStyle.RoundSimple,
              3,
              Color.Red);
          }
        }
      }
      finally
      {
        e.Display.EnableDepthWriting(true);
        e.Display.EnableDepthTesting(true);
      }
    }
  }
}
