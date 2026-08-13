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
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Previews the result of moving preselected edit-point or control-point grips,
/// or otherwise the cursor-nearest endpoint control point of each selected
/// open curve, before forwarding those exact grips to -SetPt.
///
/// Workflow:
///   1. Select curves, starting with any pre-selected curves, and freely
///      add or remove curves before accepting the selection.
///   2. Any preselected grip overrides endpoint detection for its
///      curve; otherwise the endpoint nearest the viewport cursor is used.
///      The resulting curves preview at a target that follows the cursor.
///   3. Grips are turned on and the identified grips are selected.
///   4. Control is transferred to -SetPt with the defaults
///      XSet=Yes YSet=Yes ZSet=Yes Alignment=World Copy=No.
///   5. After a successful SetPt, the used grips remain visible and selected
///      so Rhino displays the gumball.
/// </summary>
public sealed class vSetPt : Command
{
  private enum PreselectedGripType
  {
    ControlPoint,
    EditPoint
  }

  private readonly record struct PreselectedGrip(
    int GripIndex,
    PreselectedGripType Type,
    int[] ControlPointIndices,
    double CurveParameter,
    Point3d Point);

  private readonly record struct PendingCurvePick(
    Guid Id,
    bool IsStart,
    PreselectedGrip[] Grips,
    bool GripsWereOn);

  private static bool _restartingAfterDelegate;
  private static EventHandler? _pendingIdleHandler;
  private static PendingCurvePick[]? _pendingGripPicks;
  private static uint _pendingDocSerial;

  private const string Tag = "vSetPt";
  private const string OptionsSectionName = "vSetPt";
  private const string PreviewKey = "preview";
  private static bool _showPreview = true;

  public override string EnglishName => Tag;

  private static void LoadPersistedOptions()
  {
    _showPreview = ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(
        section, PreviewKey, out var preview) ? preview : true);
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section => section[PreviewKey] = _showPreview);
  }

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating to -SetPt — registers vSetPt
    // as the repeatable last command without running anything visible.
    if (_restartingAfterDelegate)
    {
      _restartingAfterDelegate = false;
      return Result.Success;
    }

    CancelPending();
    Log.Write(Tag, "--- run start ---");
    LoadPersistedOptions();
    var preselectedGrips = CapturePreselectedGrips(doc);

    // Accept pre-selected curves or prompt for selection.
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select curves");
    go.GeometryFilter  = ObjectType.Curve;
    go.GroupSelect     = false;
    go.SubObjectSelect = false;
    go.EnablePreSelect(true, true);
    go.AlreadySelectedObjectSelect = true;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);

    var previewToggle = new OptionToggle(_showPreview, "Off", "On");
    go.AddOptionToggle("Preview", ref previewToggle);
    var preview = new EndpointPreviewConduit { Enabled = _showPreview };
    var cursorTracker = new EndpointCursorCallback(
      doc, preview, preselectedGrips) { Enabled = true };
    var preselectedWaitingForConfirmation = false;

    EventHandler<RhinoObjectSelectionEventArgs> onSelectionChanged = (_, e) =>
    {
      if (e.Document == doc)
        cursorTracker.QueueSelectionRefresh();
    };

    EventHandler<RhinoObjectSelectionEventArgs> onObjectsDeselected = (_, e) =>
    {
      if (e.Document != doc)
        return;

      cursorTracker.RemovePreselectedOverrides(e.RhinoObjects);
      cursorTracker.QueueSelectionRefresh();
    };

    RhinoDoc.SelectObjects   += onSelectionChanged;
    RhinoDoc.DeselectObjects += onObjectsDeselected;
    cursorTracker.InitializeFromCurrentCursor();

    try
    {
      while (true)
      {
        var getResult = go.GetMultiple(1, 0);
        cursorTracker.RefreshFromSelection();

        if (go.CommandResult() != Result.Success)
        {
          Log.Write(Tag, "selection cancelled");
          return go.CommandResult();
        }

        if (getResult == GetResult.Option)
        {
          var showPreview = previewToggle.CurrentValue;
          if (_showPreview != showPreview)
          {
            _showPreview = showPreview;
            SavePersistedOptions();
            cursorTracker.SetPreviewEnabled(showPreview);
          }
          continue;
        }

        if (getResult == GetResult.Object &&
            go.ObjectsWerePreselected &&
            !preselectedWaitingForConfirmation)
        {
          preselectedWaitingForConfirmation = true;
          go.EnablePreSelect(false, true);
          continue;
        }

        if (getResult == GetResult.Object || getResult == GetResult.Nothing)
          break;
      }
    }
    finally
    {
      RhinoDoc.SelectObjects   -= onSelectionChanged;
      RhinoDoc.DeselectObjects -= onObjectsDeselected;
      cursorTracker.Enabled = false;
      cursorTracker.Dispose();
      preview.Enabled = false;
      doc.Views.Redraw();
    }

    var curveData = new List<(Guid id, Curve c)>();
    var originalGripStates = new Dictionary<Guid, bool>();
    var seenIds = new HashSet<Guid>();
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj?.Geometry is Curve c && !c.IsClosed && seenIds.Add(obj.Id))
      {
        curveData.Add((obj.Id, c));
        originalGripStates[obj.Id] = obj.GripsOn;
      }
    }

    Log.Write(Tag, $"  open curves: {curveData.Count}");

    if (curveData.Count < 2)
    {
      RhinoApp.WriteLine("vSetPt: select at least 2 open curves.");
      return Result.Nothing;
    }

    var cursorPicks = cursorTracker.SnapshotPicks();
    var picks = new List<PendingCurvePick>();
    for (int i = 0; i < curveData.Count; i++)
    {
      var id = curveData[i].id;
      var chooseStart = cursorPicks.TryGetValue(id, out var previewPick)
        ? previewPick
        : cursorTracker.TryChooseStart(curveData[i].c, out var fallbackPick)
          ? fallbackPick
          : true;

      var grips = preselectedGrips.TryGetValue(id, out var selectedGrips)
        ? selectedGrips
        : Array.Empty<PreselectedGrip>();
      Log.Write(Tag, grips.Length > 0
        ? $"  curve[{i}] preselected grips={grips.Length}"
        : $"  curve[{i}] cursor pick={(chooseStart ? "start" : "end")}");
      picks.Add(new PendingCurvePick(
        id, chooseStart, grips, originalGripStates[id]));
    }

    if (picks.Count == 0)
    {
      RhinoApp.WriteLine("vSetPt: no open curves to process.");
      return Result.Nothing;
    }

    Log.Write(Tag, $"  grip picks: {picks.Count}");

    _pendingGripPicks   = picks.ToArray();
    _pendingDocSerial   = doc.RuntimeSerialNumber;
    _pendingIdleHandler = OnIdleLaunch;
    RhinoApp.Idle      += _pendingIdleHandler;
    return Result.Success;
  }

  private sealed class EndpointPreviewConduit : DisplayConduit
  {
    private readonly List<Curve> _curves = new();

    public void SetCurves(IEnumerable<Curve> curves)
    {
      _curves.Clear();
      _curves.AddRange(curves);
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      foreach (var curve in _curves)
        PreviewDisplay.DrawCurve(e.Display, curve, Color.Cyan);
    }
  }

  private sealed class EndpointCursorCallback : MouseCallback, IDisposable
  {
    private const int PreviewIntervalMilliseconds = 24;
    private const int SelectionDebounceMilliseconds = 24;

    private readonly RhinoDoc _doc;
    private readonly EndpointPreviewConduit _preview;
    private readonly Dictionary<Guid, PreselectedGrip[]>
      _preselectedGrips;
    private readonly Dictionary<Guid, (bool IsStart, Point3d Point)> _picks = new();
    private readonly Dictionary<Guid, NurbsCurve> _previewSources = new();
    private readonly System.Windows.Forms.Timer _previewTimer;
    private readonly System.Windows.Forms.Timer _selectionTimer;
    private RhinoView? _view;
    private Point2d _cursor;
    private bool _hasCursor;
    private long _lastPreviewMilliseconds;

    public EndpointCursorCallback(
      RhinoDoc doc,
      EndpointPreviewConduit preview,
      Dictionary<Guid, PreselectedGrip[]> preselectedGrips)
    {
      _doc = doc;
      _preview = preview;
      _preselectedGrips = preselectedGrips;

      _previewTimer = new System.Windows.Forms.Timer
      {
        Interval = PreviewIntervalMilliseconds
      };
      _previewTimer.Tick += (_, _) =>
      {
        _previewTimer.Stop();
        RefreshPreviewNow();
      };

      _selectionTimer = new System.Windows.Forms.Timer
      {
        Interval = SelectionDebounceMilliseconds
      };
      _selectionTimer.Tick += (_, _) =>
      {
        _selectionTimer.Stop();
        RefreshFromSelection();
      };
    }

    public void InitializeFromCurrentCursor()
    {
      var view = _doc.Views.ActiveView;
      if (view == null)
        return;

      var client = view.ScreenToClient(System.Windows.Forms.Cursor.Position);
      _view = view;
      _cursor = new Point2d(client.X, client.Y);
      _hasCursor = true;
      RefreshFromSelection();
    }

    public void QueueSelectionRefresh()
    {
      _selectionTimer.Stop();
      _selectionTimer.Start();
    }

    public void RemovePreselectedOverrides(
      IEnumerable<RhinoObject>? deselectedObjects)
    {
      if (deselectedObjects == null)
        return;

      var removed = 0;
      foreach (var obj in deselectedObjects)
      {
        var ownerId = obj is GripObject grip ? grip.OwnerId : obj.Id;
        if (ownerId == Guid.Empty || !_preselectedGrips.Remove(ownerId))
          continue;

        _picks.Remove(ownerId);
        removed++;
      }

      if (removed > 0)
        Log.Write(Tag, $"  cleared preselected grip overrides: {removed}");
    }

    public void SetPreviewEnabled(bool enabled)
    {
      _preview.Enabled = enabled;
      _previewTimer.Stop();
      if (enabled)
        RefreshFromSelection();
      else
        _view?.Redraw();
    }

    public Dictionary<Guid, bool> SnapshotPicks()
    {
      return _picks.ToDictionary(pair => pair.Key, pair => pair.Value.IsStart);
    }

    public bool TryChooseStart(Curve curve, out bool chooseStart)
    {
      chooseStart = true;
      if (!_hasCursor || _view?.ActiveViewport == null)
        return false;

      var viewport = _view.ActiveViewport;
      var start = viewport.WorldToClient(curve.PointAtStart);
      var end = viewport.WorldToClient(curve.PointAtEnd);
      var startDx = start.X - _cursor.X;
      var startDy = start.Y - _cursor.Y;
      var endDx = end.X - _cursor.X;
      var endDy = end.Y - _cursor.Y;
      chooseStart =
        (startDx * startDx) + (startDy * startDy) <=
        (endDx * endDx) + (endDy * endDy);
      return true;
    }

    public void RefreshFromSelection()
    {
      _selectionTimer.Stop();
      if (!_hasCursor || _view?.ActiveViewport == null)
        return;

      var next = new Dictionary<Guid, (bool IsStart, Point3d Point)>();
      var nextSources = new Dictionary<Guid, NurbsCurve>();
      foreach (var obj in _doc.Objects.GetSelectedObjects(false, false))
      {
        if (obj?.Geometry is not Curve curve || curve.IsClosed)
          continue;

        try
        {
          if (_preview.Enabled)
          {
            if (!_previewSources.TryGetValue(obj.Id, out var previewSource))
              previewSource = curve.ToNurbsCurve();
            if (previewSource != null)
              nextSources[obj.Id] = previewSource;
          }

          if (_preselectedGrips.TryGetValue(obj.Id, out var grips) &&
              grips.Length > 0)
          {
            next[obj.Id] = (false, grips[0].Point);
            continue;
          }

          if (_picks.TryGetValue(obj.Id, out var existingPick))
          {
            next[obj.Id] = existingPick;
            continue;
          }

          if (!TryChooseStart(curve, out var chooseStart))
            continue;

          next[obj.Id] = (
            chooseStart,
            chooseStart ? curve.PointAtStart : curve.PointAtEnd);
        }
        catch
        {
        }
      }

      _picks.Clear();
      foreach (var pair in next)
        _picks[pair.Key] = pair.Value;

      _previewSources.Clear();
      foreach (var pair in nextSources)
        _previewSources[pair.Key] = pair.Value;

      RefreshPreviewNow();
    }

    private void QueuePreviewRefresh()
    {
      if (!_preview.Enabled)
        return;

      var elapsed = System.Environment.TickCount64 - _lastPreviewMilliseconds;
      if (elapsed >= PreviewIntervalMilliseconds)
      {
        _previewTimer.Stop();
        RefreshPreviewNow();
        return;
      }

      if (!_previewTimer.Enabled)
      {
        _previewTimer.Interval = Math.Max(
          1, PreviewIntervalMilliseconds - (int)Math.Max(0, elapsed));
        _previewTimer.Start();
      }
    }

    private void RefreshPreviewNow()
    {
      if (!_preview.Enabled || !_hasCursor || _view?.ActiveViewport == null)
        return;

      _lastPreviewMilliseconds = System.Environment.TickCount64;
      var viewport = _view.ActiveViewport;

      var previews = new List<Curve>(_picks.Count);
      if (_picks.Count > 0)
      {
        var x = 0.0;
        var y = 0.0;
        var z = 0.0;
        var anchorCount = 0;
        foreach (var pair in _picks)
        {
          if (_preselectedGrips.TryGetValue(pair.Key, out var grips) &&
              grips.Length > 0)
          {
            foreach (var grip in grips)
            {
              x += grip.Point.X;
              y += grip.Point.Y;
              z += grip.Point.Z;
              anchorCount++;
            }
          }
          else
          {
            x += pair.Value.Point.X;
            y += pair.Value.Point.Y;
            z += pair.Value.Point.Z;
            anchorCount++;
          }
        }

        if (anchorCount == 0)
          return;

        var anchor = new Point3d(
          x / anchorCount,
          y / anchorCount,
          z / anchorCount);
        var target = anchor;
        var cursorRay = viewport.ClientToWorld(_cursor);
        var cursorPlane = new Plane(anchor, viewport.CameraDirection);
        if (Rhino.Geometry.Intersect.Intersection.LinePlane(
              cursorRay, cursorPlane, out var rayParameter))
        {
          target = cursorRay.PointAt(rayParameter);
        }

        foreach (var pair in _picks)
        {
          if (!_previewSources.TryGetValue(pair.Key, out var previewSource))
            continue;

          _preselectedGrips.TryGetValue(pair.Key, out var selectedGrips);
          var result = CreateSetPtPreview(
            previewSource, pair.Value.IsStart, selectedGrips, target);
          if (result != null)
            previews.Add(result);
        }
      }

      _preview.SetCurves(previews);
      _view.Redraw();
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      if (e.View != null)
        SetCursor(e.View, e.ViewportPoint.X, e.ViewportPoint.Y);

      base.OnMouseMove(e);
    }

    private void SetCursor(RhinoView view, double x, double y)
    {
      _view = view;
      _cursor = new Point2d(x, y);
      _hasCursor = true;
      QueuePreviewRefresh();
    }

    public void Dispose()
    {
      _previewTimer.Stop();
      _selectionTimer.Stop();
      _previewTimer.Dispose();
      _selectionTimer.Dispose();
    }
  }

  private static Curve? CreateSetPtPreview(
    NurbsCurve curve,
    bool isStart,
    IReadOnlyList<PreselectedGrip>? selectedGrips,
    Point3d target)
  {
    var result = curve.DuplicateCurve() as NurbsCurve;
    if (result == null || result.Points.Count == 0)
      return null;

    if (selectedGrips is { Count: > 0 })
    {
      var changed = false;
      var selectedEditPoints = selectedGrips
        .Where(grip => grip.Type == PreselectedGripType.EditPoint)
        .ToArray();
      if (selectedEditPoints.Length > 0)
      {
        var editPoints = result.GrevillePoints(false);
        if (editPoints == null || editPoints.Count == 0)
          return null;
        var editParameters = result.GrevilleParameters();
        var parametersMatch = editParameters.Length == editPoints.Count;
        var changedIndices = new HashSet<int>();

        foreach (var selectedPoint in selectedEditPoints)
        {
          var bestIndex = -1;
          var bestDistance = double.MaxValue;
          for (var index = 0; index < editPoints.Count; index++)
          {
            var distance = parametersMatch
              ? Math.Abs(editParameters[index] - selectedPoint.CurveParameter)
              : editPoints[index].DistanceTo(selectedPoint.Point);
            if (distance >= bestDistance)
              continue;

            bestDistance = distance;
            bestIndex = index;
          }

          if (bestIndex >= 0 && changedIndices.Add(bestIndex))
            editPoints[bestIndex] = target;
        }

        if (changedIndices.Count == 0 || !result.SetGrevillePoints(editPoints))
          return null;

        changed = true;
      }

      var changedControlPointIndices = new HashSet<int>();
      foreach (var selectedGrip in selectedGrips.Where(
                 grip => grip.Type == PreselectedGripType.ControlPoint))
      {
        var addedIndex = false;
        foreach (var index in selectedGrip.ControlPointIndices)
        {
          if (index < 0 || index >= result.Points.Count)
            continue;

          changedControlPointIndices.Add(index);
          addedIndex = true;
        }

        if (!addedIndex &&
            selectedGrip.GripIndex >= 0 &&
            selectedGrip.GripIndex < result.Points.Count)
        {
          changedControlPointIndices.Add(selectedGrip.GripIndex);
        }
      }

      foreach (var index in changedControlPointIndices)
      {
        var selectedControlPoint = result.Points[index];
        changed |= result.Points.SetPoint(
          index, target, selectedControlPoint.Weight);
      }

      return changed ? result : null;
    }

    var endpointIndex = isStart ? 0 : result.Points.Count - 1;
    var endpointControlPoint = result.Points[endpointIndex];
    return result.Points.SetPoint(
        endpointIndex, target, endpointControlPoint.Weight)
      ? result
      : null;
  }

  private static Dictionary<Guid, PreselectedGrip[]>
    CapturePreselectedGrips(RhinoDoc doc)
  {
    var gripsByOwner = new Dictionary<Guid, List<PreselectedGrip>>();
    var capturedGrips = new List<GripObject>();
    var selected = doc.Objects.GetSelectedObjects(false, true).ToList();

    foreach (var selectedObject in selected)
    {
      if (selectedObject is not GripObject grip)
        continue;

      try
      {
        var owner = doc.Objects.FindId(grip.OwnerId);
        if (owner?.Geometry is not Curve curve || curve.IsClosed)
          continue;

        var hasControlPointIndices = TryGetControlPointIndices(
          grip, out var controlPointIndices);
        var hasCurveParameter = TryGetCurveParameter(
          grip, out var curveParameter);
        if (!hasControlPointIndices && !hasCurveParameter)
          continue;

        var gripType = ResolveGripType(
          doc, owner, curve, grip, hasControlPointIndices, hasCurveParameter);
        if (!gripsByOwner.TryGetValue(grip.OwnerId, out var ownerGrips))
        {
          ownerGrips = new List<PreselectedGrip>();
          gripsByOwner.Add(grip.OwnerId, ownerGrips);
        }

        if (ownerGrips.Any(selectedGrip => selectedGrip.GripIndex == grip.Index))
          continue;

        ownerGrips.Add(new PreselectedGrip(
          grip.Index,
          gripType,
          controlPointIndices,
          curveParameter,
          grip.CurrentLocation));
        capturedGrips.Add(grip);
      }
      catch
      {
      }
    }

    foreach (var grip in capturedGrips)
      grip.Select(false);

    foreach (var ownerId in gripsByOwner.Keys)
      doc.Objects.FindId(ownerId)?.Select(true);

    var result = gripsByOwner.ToDictionary(
      pair => pair.Key,
      pair => pair.Value.ToArray());
    Log.Write(Tag,
      $"  preselected grips: {result.Values.Sum(grips => grips.Length)}" +
      $" on {result.Count} curve(s)");
    return result;
  }

  private static bool TryGetControlPointIndices(
    GripObject grip,
    out int[] indices)
  {
    try
    {
      if (grip.GetCurveCVIndices(out var foundIndices) > 0 &&
          foundIndices != null &&
          foundIndices.Length > 0)
      {
        indices = foundIndices.ToArray();
        return true;
      }
    }
    catch
    {
    }

    indices = Array.Empty<int>();
    return false;
  }

  private static bool TryGetCurveParameter(
    GripObject grip,
    out double curveParameter)
  {
    try
    {
      return grip.GetCurveParameters(out curveParameter);
    }
    catch
    {
      curveParameter = 0.0;
      return false;
    }
  }

  private static PreselectedGripType ResolveGripType(
    RhinoDoc doc,
    RhinoObject owner,
    Curve curve,
    GripObject selectedGrip,
    bool isControlPoint,
    bool isEditPoint)
  {
    if (isControlPoint && !isEditPoint)
      return PreselectedGripType.ControlPoint;
    if (isEditPoint && !isControlPoint)
      return PreselectedGripType.EditPoint;

    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    try
    {
      var visibleGrips = owner.GetGrips() ?? Array.Empty<GripObject>();
      foreach (var grip in visibleGrips)
      {
        var gripIsControlPoint = TryGetControlPointIndices(grip, out _);
        var gripIsEditPoint = TryGetCurveParameter(grip, out _);
        if (gripIsControlPoint && !gripIsEditPoint)
          return PreselectedGripType.ControlPoint;
        if (gripIsEditPoint && !gripIsControlPoint)
          return PreselectedGripType.EditPoint;
      }

      if (visibleGrips.Length > 0)
      {
        foreach (var grip in visibleGrips)
        {
          if (!curve.ClosestPoint(grip.CurrentLocation, out var gripParameter) ||
              curve.PointAt(gripParameter).DistanceTo(grip.CurrentLocation) > tolerance)
          {
            return PreselectedGripType.ControlPoint;
          }
        }

        return PreselectedGripType.EditPoint;
      }
    }
    catch
    {
    }

    var resolved = PreselectedGripType.ControlPoint;
    if (curve.ClosestPoint(selectedGrip.CurrentLocation, out var parameter))
    {
      if (curve.PointAt(parameter).DistanceTo(selectedGrip.CurrentLocation) <= tolerance)
        resolved = PreselectedGripType.EditPoint;
    }

    Log.Write(Tag,
      $"  ambiguous preselected grip: {owner.Id} index={selectedGrip.Index}" +
      $" resolved={resolved}");
    return resolved;
  }

  private static void CancelPending()
  {
    if (_pendingIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingIdleHandler;
      _pendingIdleHandler = null;
    }
    _pendingGripPicks = null;
    _pendingDocSerial = 0u;
  }

  private static void OnIdleLaunch(object? sender, EventArgs e)
  {
    // Remove the handler and capture pending data before anything else.
    if (_pendingIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingIdleHandler;
      _pendingIdleHandler = null;
    }

    var picks     = _pendingGripPicks;
    var docSerial = _pendingDocSerial;
    _pendingGripPicks = null;
    _pendingDocSerial = 0u;

    if (picks == null || picks.Length == 0) return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != docSerial) return;

    UnselectObjectsAndGrips(doc);
    EnableEditPointsForHiddenEndpointPicks(doc, picks);

    // Enable grips for each target curve and select the requested grips.
    int selectedCount = 0;
    foreach (var pick in picks)
    {
      var id = pick.Id;
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;

      // Preserve an existing edit-point mode. Use control points only when
      // neither edit-point nor control-point grips could be made visible.
      var grips = obj.GetGrips();
      if (grips == null || grips.Length == 0)
      {
        obj.GripsOn = true;
        obj.CommitChanges();
        grips = obj.GetGrips();
      }

      if (grips == null || grips.Length == 0) continue;

      var curve = obj.Geometry as Curve;
      if (curve == null) continue;

      if (pick.Grips.Length > 0)
      {
        var selectedGripIndices = new HashSet<int>();
        foreach (var selectedPoint in pick.Grips)
        {
          var exactGrip = FindGripForPreselectedPoint(
            grips, selectedPoint, out var bestDistance);

          if (exactGrip == null || !selectedGripIndices.Add(exactGrip.Index))
            continue;

          exactGrip.Select(true);
          selectedCount++;
          Log.Write(Tag,
            $"  preselected {selectedPoint.Type} grip restored:" +
            $" {id} index={exactGrip.Index}" +
            $" gripDist={bestDistance:G4}");
        }

        continue;
      }

      var targetPt = pick.IsStart ? curve.PointAtStart : curve.PointAtEnd;
      GripObject? endpointGrip = null;
      double bestD = double.MaxValue;
      foreach (var grip in grips)
      {
        var d = grip.CurrentLocation.DistanceTo(targetPt);
        if (d < bestD) { bestD = d; endpointGrip = grip; }
      }
      if (endpointGrip == null) continue;
      endpointGrip.Select(true);
      selectedCount++;

      Log.Write(Tag,
        $"  grip selected: {id} {(pick.IsStart ? "start" : "end")} gripDist={bestD:G4}");
    }

    doc.Views.Redraw();

    if (selectedCount == 0)
    {
      Log.Write(Tag, "  no grips could be selected");
      RhinoApp.WriteLine("vSetPt: failed to select control-point grips.");
      UnselectObjectsAndGrips(doc);
      RestoreGripStates(doc, picks);
      doc.Views.Redraw();
      return;
    }

    Log.Write(Tag, $"  launching -SetPt with {selectedCount} grip(s) selected");

    // Snapshot endpoints before SetPt; RunScript result is unreliable in Rhino 9 (true even on Escape).
    var endpointsBefore = picks.ToDictionary(
      p => p.Id,
      p => { var c = doc.Objects.FindId(p.Id)?.Geometry as Curve; return c == null ? Point3d.Unset : (p.IsStart ? c.PointAtStart : c.PointAtEnd); });

    Result? setPtResult = null;
    EventHandler<CommandEventArgs> onSetPtEnded = (_, e) =>
    {
      if (e.Document == doc &&
          string.Equals(e.CommandEnglishName, "SetPt", StringComparison.OrdinalIgnoreCase))
      {
        setPtResult = e.CommandResult;
      }
    };

    Command.EndCommand += onSetPtEnded;
    try
    {
      _ = RhinoApp.RunScript(
        "_-SetPt _XSet=_Yes _YSet=_Yes _ZSet=_Yes _Alignment=_World _Copy=_No", false);
      Log.Write(Tag, $"  -SetPt returned");
    }
    finally
    {
      Command.EndCommand -= onSetPtEnded;
      bool moved = picks.Any(p =>
      {
        if (!endpointsBefore.TryGetValue(p.Id, out var before) || before == Point3d.Unset) return false;
        var c = doc.Objects.FindId(p.Id)?.Geometry as Curve;
        if (c == null) return false;
        var after = p.IsStart ? c.PointAtStart : c.PointAtEnd;
        return after.DistanceTo(before) > doc.ModelAbsoluteTolerance;
      });
      bool completed = setPtResult == Result.Success ||
        (setPtResult == null && moved);
      Log.Write(Tag,
        $"  -SetPt result={setPtResult?.ToString() ?? "Unknown"}" +
        $" moved={moved} completed={completed}");
      UnselectObjectsAndGrips(doc);
      if (completed)
      {
        SelectUsedGrips(doc, picks, includeDetectedEndpoints: true);
      }
      else
      {
        RestoreGripStates(doc, picks);
        SelectUsedGrips(doc, picks, includeDetectedEndpoints: false);
      }
      doc.Views.Redraw();
    }

    // Silently re-run vSetPt so pressing Enter repeats this command, not -SetPt.
    _restartingAfterDelegate = true;
    _ = RhinoApp.RunScript("_vSetPt", false);
    _restartingAfterDelegate = false;
  }

  private static void UnselectObjectsAndGrips(RhinoDoc doc)
  {
    foreach (var selectedObject in doc.Objects.GetSelectedObjects(false, true).ToList())
    {
      if (selectedObject is GripObject grip)
        grip.Select(false);
      else
        selectedObject.Select(false);
    }

    doc.Objects.UnselectAll();
  }

  private static void EnableEditPointsForHiddenEndpointPicks(
    RhinoDoc doc,
    IEnumerable<PendingCurvePick> picks)
  {
    var objectIds = picks
      .Where(pick => pick.Grips.Length == 0 && !pick.GripsWereOn)
      .Select(pick => pick.Id)
      .Distinct()
      .ToArray();
    if (objectIds.Length == 0)
      return;

    foreach (var id in objectIds)
      doc.Objects.FindId(id)?.Select(true);

    var editPointsOn = RhinoApp.RunScript("_EditPtOn _Enter", false);
    Log.Write(Tag,
      $"  edit points for hidden endpoint picks: objects={objectIds.Length}" +
      $" result={editPointsOn}");

    foreach (var id in objectIds)
      doc.Objects.FindId(id)?.Select(false);
  }

  private static void SelectUsedGrips(
    RhinoDoc doc,
    IEnumerable<PendingCurvePick> picks,
    bool includeDetectedEndpoints)
  {
    var selectedCount = 0;
    foreach (var pick in picks)
    {
      var obj = doc.Objects.FindId(pick.Id);
      if (obj?.Geometry is not Curve curve)
        continue;

      var grips = obj.GetGrips();
      if (grips == null || grips.Length == 0)
        continue;

      if (pick.Grips.Length > 0)
      {
        var selectedGripIndices = new HashSet<int>();
        foreach (var selectedPoint in pick.Grips)
        {
          var grip = FindGripForPreselectedPoint(grips, selectedPoint, out _);
          if (grip == null || !selectedGripIndices.Add(grip.Index))
            continue;

          grip.Select(true);
          selectedCount++;
        }
        continue;
      }

      if (!includeDetectedEndpoints)
        continue;

      var endpoint = pick.IsStart ? curve.PointAtStart : curve.PointAtEnd;
      var usedGrip = grips
        .OrderBy(grip => grip.CurrentLocation.DistanceTo(endpoint))
        .FirstOrDefault();
      if (usedGrip != null)
      {
        usedGrip.Select(true);
        selectedCount++;
      }
    }

    Log.Write(Tag,
      $"  selected used grips after SetPt: {selectedCount}" +
      $" includeDetectedEndpoints={includeDetectedEndpoints}");
  }

  private static GripObject? FindGripForPreselectedPoint(
    GripObject[] grips,
    PreselectedGrip selectedPoint,
    out double bestDistance)
  {
    var exactGrip = grips.FirstOrDefault(
      grip => grip.Index == selectedPoint.GripIndex);
    bestDistance = exactGrip?.CurrentLocation.DistanceTo(selectedPoint.Point)
      ?? double.MaxValue;
    if (exactGrip != null)
      return exactGrip;

    foreach (var grip in grips)
    {
      var distance = grip.CurrentLocation.DistanceTo(selectedPoint.Point);
      if (distance >= bestDistance)
        continue;

      bestDistance = distance;
      exactGrip = grip;
    }

    return exactGrip;
  }

  private static void RestoreGripStates(
    RhinoDoc doc,
    IEnumerable<PendingCurvePick> picks)
  {
    foreach (var pick in picks)
    {
      var obj = doc.Objects.FindId(pick.Id);
      if (obj == null || obj.GripsOn == pick.GripsWereOn)
        continue;

      obj.GripsOn = pick.GripsWereOn;
      obj.CommitChanges();
    }
  }
}
