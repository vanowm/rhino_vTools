using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed partial class vSetPt
{
  private static readonly Action<uint>? UpdateHistoryDescendants =
    typeof(RhinoApp).Assembly.GetType("UnsafeNativeMethods")?
      .GetMethod("RHC_RhHistoryUpdateDescendants", BindingFlags.NonPublic | BindingFlags.Static)?
      .CreateDelegate<Action<uint>>();

  private sealed record PlacementTarget(
    Point3d Point, bool XSet, bool YSet, bool ZSet, int Alignment, bool Copy)
  {
    internal string NativeScript =>
      $"_-SetPt _XSet=_{YesNo(XSet)} _YSet=_{YesNo(YSet)}" +
      $" _ZSet=_{YesNo(ZSet)} _Alignment=_{AlignmentValues[Alignment]}" +
      $" _Copy=_{YesNo(Copy)} w" +
      Point.X.ToString("R", CultureInfo.InvariantCulture) + "," +
      Point.Y.ToString("R", CultureInfo.InvariantCulture) + "," +
      Point.Z.ToString("R", CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "Yes" : "No";
  }

  private static PlacementTarget? PickPlacementTarget(
    RhinoDoc doc, PendingCurvePick[] picks, out Result commandResult)
  {
    commandResult = Result.Success;
    using var session = new LivePlacementPreview(doc, picks);
    using var get = new GetPoint();
    get.SetCommandPrompt("Location for selected points");
    get.EnableTransparentCommands(AllowPlacementTransparentCommands);
    get.AcceptNothing(DefaultCancelPlacementOnEnter);
    get.EnableNoRedrawOnExit(true);

    var x = new OptionToggle(DefaultSetCoordinate, "No", "Yes");
    var y = new OptionToggle(DefaultSetCoordinate, "No", "Yes");
    var z = new OptionToggle(DefaultSetCoordinate, "No", "Yes");
    var copy = new OptionToggle(DefaultCopy, "No", "Yes");
    var alignment = DefaultAlignment;
    get.MouseMove += (_, e) => session.Queue(e.Point, e.Viewport.ConstructionPlane());

    while (true)
    {
      get.ClearCommandOptions();
      get.AddOptionToggle("XSet", ref x);
      get.AddOptionToggle("YSet", ref y);
      get.AddOptionToggle("ZSet", ref z);
      var alignmentOption = get.AddOptionList("Alignment", AlignmentValues, alignment);
      get.AddOptionToggle("Copy", ref copy);
      var previewOption = get.AddOptionList("Preview", PreviewValues, (int)_previewMode);
      session.SetOptions(x.CurrentValue, y.CurrentValue, z.CurrentValue,
        alignment, copy.CurrentValue, _previewMode);

      GetResult result;
      try
      {
        session.Start();
        result = get.Get();
      }
      finally
      {
        session.Stop();
      }
      session.ThrowIfFailed();
      if (result == GetResult.Option)
      {
        if (get.OptionIndex() == alignmentOption)
          alignment = get.Option().CurrentListOptionIndex;
        if (get.OptionIndex() == previewOption)
        {
          var previewMode = (PreviewMode)get.Option().CurrentListOptionIndex;
          if (_previewMode != previewMode)
          {
            _previewMode = previewMode;
            SavePersistedOptions();
          }
        }
        continue;
      }

      if (result != GetResult.Point)
      {
        // Empty Enter is a completed no-op, not an aborted native script macro.
        commandResult = result == GetResult.Nothing ? Result.Success : get.CommandResult();
        return null;
      }
      if (!x.CurrentValue && !y.CurrentValue && !z.CurrentValue)
      {
        RhinoApp.WriteLine("vSetPt: enable at least one coordinate.");
        continue;
      }

      return new PlacementTarget(get.Point(), x.CurrentValue, y.CurrentValue,
        z.CurrentValue, alignment, copy.CurrentValue);
    }
  }

  private static void UndoPlacementPreview(RhinoDoc doc, uint expectedRecord)
  {
    if (RhinoDoc.ActiveDoc != doc || RhinoGet.InGet(doc) ||
        doc.InCommand(true) != 0 || doc.UndoRecordingIsActive)
      throw new InvalidOperationException("Preview rollback must run after native point input has exited.");

    uint undoneRecord = 0;
    Result? undoResult = null;
    EventHandler<UndoRedoEventArgs> onUndo = (_, e) =>
    {
      if (e.IsEndUndo) undoneRecord = e.UndoSerialNumber;
    };
    EventHandler<CommandEventArgs> onEnded = (_, e) =>
    {
      if (e.Document == doc && e.CommandEnglishName.Equals("Undo", StringComparison.OrdinalIgnoreCase))
        undoResult = e.CommandResult;
    };

    Command.UndoRedo += onUndo;
    Command.EndCommand += onEnded;
    try
    {
      // doc.Undo() alone leaves its inverse on the undo stack in a ScriptRunner command.
      // Let Rhino's Undo command finish recording and route that inverse to the redo stack.
      _ = RhinoApp.RunScript("_Undo", false);
    }
    finally
    {
      Command.EndCommand -= onEnded;
      Command.UndoRedo -= onUndo;
    }
    if (undoneRecord != expectedRecord || undoResult != Result.Success ||
        doc.UndoActive || doc.UndoRecordingIsActive)
      throw new InvalidOperationException("Rhino did not finish the preview rollback undo command.");
  }

  private sealed class LivePlacementPreview : IDisposable
  {
    private sealed record Source(
      Guid Id, NurbsCurve Curve, PreselectedGrip[] Grips, Dictionary<int, Point3d> Locations)
    {
      internal HashSet<int> SelectedIndices { get; } = Grips.Select(grip => grip.GripIndex).ToHashSet();
    }

    private readonly RhinoDoc _doc;
    private readonly List<Source> _sources = new();
    private readonly HashSet<Guid> _children = new();
    private readonly Dictionary<Guid, GeometryBase> _originalGeometry = new();
    private readonly EndpointPreviewConduit _temporary = new();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _canEditOriginals;
    private readonly bool _historyUpdateWasEnabled;
    private readonly double _positionTolerance;
    private Point3d[]? _previewLocations;
    private System.Windows.Forms.Cursor? _placementCursor;
    private Point3d _target;
    private Plane _cplane = Plane.WorldXY;
    private bool _hasTarget;
    private bool _pending;
    private bool _running;
    private bool _refreshing;
    private Exception? _failure;
    private bool _x, _y, _z, _copy;
    private PreviewMode _previewMode = DefaultPreviewMode;
    private int _alignment;
    private uint _undoRecord;
    private bool _changed;
    private long _lastRefresh;

    private int RefreshInterval => _previewMode == PreviewMode.All && !_copy &&
      _canEditOriginals && _children.Count > 0
      ? HistoryPreviewIntervalMilliseconds : PreviewIntervalMilliseconds;

    internal LivePlacementPreview(RhinoDoc doc, PendingCurvePick[] picks)
    {
      _doc = doc;
      _historyUpdateWasEnabled = HistorySettings.UpdateEnabled;
      _positionTolerance = Math.Max(RhinoMath.ZeroTolerance,
        doc.ModelAbsoluteTolerance * HistoryPreviewToleranceFactor);
      foreach (var pick in picks)
      {
        var obj = doc.Objects.FindId(pick.Id);
        if (obj?.Geometry is not Curve curve)
          throw new InvalidOperationException("A selected curve no longer exists.");
        var allGrips = obj.GetGrips() ?? Array.Empty<GripObject>();
        var grips = allGrips.Where(grip => grip.IsSelected(false) > 0).ToArray();
        if (grips.Length == 0)
          throw new InvalidOperationException("A selected curve has no selected grips.");

        var points = grips.Select(grip =>
        {
          var isControl = TryGetControlPointIndices(grip, out var indices);
          var isEdit = TryGetCurveParameter(grip, out var parameter);
          return new PreselectedGrip(grip.Index,
            ResolveGripType(doc, obj, curve, grip, isControl, isEdit),
            indices, parameter, grip.CurrentLocation);
        }).ToArray();
        _sources.Add(new Source(pick.Id, curve.ToNurbsCurve(), points,
          allGrips.ToDictionary(grip => grip.Index, grip => grip.CurrentLocation)));
        CollectHistoryChildren(pick.Id);
      }

      // Editing a history child itself would break its input record before the final confirmation.
      _canEditOriginals = _historyUpdateWasEnabled && doc.UndoRecordingEnabled &&
        doc.InCommand(true) == 0 && !doc.UndoRecordingIsActive &&
        (_children.Count == 0 || UpdateHistoryDescendants != null) &&
        _sources.All(source => doc.Objects.FindId(source.Id)?.HasHistoryRecord() == false);
      foreach (var id in _sources.Select(source => source.Id).Concat(_children).Distinct())
      {
        var geometry = doc.Objects.FindId(id)?.Geometry.Duplicate();
        if (geometry != null) _originalGeometry.Add(id, geometry);
      }

      _timer = new System.Windows.Forms.Timer { Interval = HistoryPreviewIntervalMilliseconds };
      _timer.Tick += (_, _) =>
      {
        _timer.Stop();
        TryRefresh();
      };
    }

    private void CollectHistoryChildren(Guid id)
    {
      foreach (var child in _doc.Objects.FindId(id)?.HistoryChildren() ?? Array.Empty<Guid>())
        if (_children.Add(child)) CollectHistoryChildren(child);
    }

    internal void SetOptions(bool x, bool y, bool z, int alignment, bool copy, PreviewMode preview)
    {
      var displayChanged = _copy != copy || _previewMode != preview;
      _x = x; _y = y; _z = z;
      _alignment = alignment;
      _copy = copy;
      _previewMode = preview;
      if ((preview != PreviewMode.All || copy) && _undoRecord != 0) Rollback();
      if (displayChanged) _previewLocations = null;
      if (preview == PreviewMode.Off)
      {
        _pending = false;
        _timer.Stop();
        if (!_temporary.Enabled) return;
        _temporary.Enabled = false;
        _doc.Views.Redraw();
        return;
      }
      _pending = _hasTarget && !PreviewPositionsEqual(
        _previewLocations, GetPlacementLocations(), _positionTolerance);
    }

    internal void Start()
    {
      _running = _failure == null;
      if (_running && _pending) _timer.Start();
    }

    internal void ThrowIfFailed()
    {
      if (_failure != null)
        throw new InvalidOperationException("The live placement preview failed.", _failure);
    }

    internal void Stop()
    {
      _running = false;
      _timer.Stop();
    }

    internal void Queue(Point3d target, Plane cplane)
    {
      if (!target.IsValid) return;
      _target = target;
      _cplane = cplane;
      _hasTarget = true;
      if (_previewMode == PreviewMode.Off || _failure != null) return;
      _pending = !PreviewPositionsEqual(
        _previewLocations, GetPlacementLocations(), _positionTolerance);
      if (!_pending)
      {
        _timer.Stop();
        return;
      }
      if (_refreshing) return;
      // Updating document geometry inside GetPoint.MouseMove interferes with its input/redraw cycle.
      ArmRefreshTimer();
    }

    private void ArmRefreshTimer()
    {
      if (_timer.Enabled) return;
      var elapsed = System.Environment.TickCount64 - _lastRefresh;
      _timer.Interval = (int)Math.Max(1, RefreshInterval - elapsed);
      _timer.Start();
    }

    private void TryRefresh()
    {
      if (!_running || !_pending || _refreshing || _failure != null) return;
      _placementCursor = System.Windows.Forms.Cursor.Current;
      try
      {
        // Keep the native point get alive; never inject macro input from a preview callback.
        Refresh();
      }
      catch (Exception ex)
      {
        _failure = ex;
        Stop();
        Log.Write(Tag, $"  live preview failed: {ex}");
      }
    }

    private Point3d[] GetPlacementLocations()
    {
      var plane = _alignment == WorldAlignment ? Plane.WorldXY : _cplane;
      return _sources.SelectMany(source => source.Grips.Select(grip =>
        AlignPoint(grip.Point, _target, plane, _x, _y, _z))).ToArray();
    }

    internal void Refresh()
    {
      if (_refreshing || !_pending || !_hasTarget || _previewMode == PreviewMode.Off) return;
      _pending = false;
      var locations = GetPlacementLocations();
      if (PreviewPositionsEqual(_previewLocations, locations, _positionTolerance)) return;
      var cursor = _placementCursor ?? System.Windows.Forms.Cursor.Current;
      _lastRefresh = System.Environment.TickCount64;
      _refreshing = true;
      try
      {
        System.Windows.Forms.Cursor.Current = cursor;
        RefreshLocations(locations);
      }
      finally
      {
        _refreshing = false;
        System.Windows.Forms.Cursor.Current = cursor;
        if (_running && _previewMode != PreviewMode.Off)
        {
          _pending = !PreviewPositionsEqual(_previewLocations, GetPlacementLocations(), _positionTolerance);
          if (_pending)
          {
            ArmRefreshTimer();
          }
          else _timer.Stop();
        }
      }
    }

    private void RefreshLocations(Point3d[] locations)
    {
      var target = _target;
      var coordinatePlane = _alignment == WorldAlignment ? Plane.WorldXY : _cplane;
      Point3d Place(Point3d original) => AlignPoint(original, target, coordinatePlane, _x, _y, _z);

      if (_previewMode == PreviewMode.Curves || _copy || !_canEditOriginals)
      {
        var curves = _sources.Select(source =>
          CreateSetPtPreview(source.Curve, false, source.Grips, target, Place))
          .OfType<Curve>().ToArray();
        _temporary.SetCurves(curves);
        _temporary.Enabled = true;
        _previewLocations = locations;
        _doc.Views.Redraw();
        return;
      }

      var redrawNeeded = _temporary.Enabled;
      _temporary.Enabled = false;
      var sourcesChanged = false;
      var redraw = _doc.Views.RedrawEnabled;
      _doc.Views.RedrawEnabled = false;
      try
      {
        // Keep history enabled while replacing parents so Rhino queues their descendants for replay.
        foreach (var source in _sources)
        {
          var obj = _doc.Objects.FindId(source.Id);
          var grips = obj?.GetGrips();
          if (obj == null || grips == null)
            throw new InvalidOperationException("Preview source grips became unavailable.");
          var byIndex = grips.ToDictionary(grip => grip.Index);
          var moves = new List<(GripObject Grip, Point3d Point)>();
          foreach (var pair in source.Locations)
          {
            if (!byIndex.TryGetValue(pair.Key, out var grip))
              throw new InvalidOperationException("Preview source grip topology changed.");
            var point = source.SelectedIndices.Contains(pair.Key) ? Place(pair.Value) : pair.Value;
            if (grip.CurrentLocation.DistanceTo(point) > _positionTolerance)
              moves.Add((grip, point));
          }
          if (moves.Count == 0) continue;
          EnsureUndoRecord();
          foreach (var move in moves) move.Grip.Move(move.Point);
          var updated = _doc.Objects.GripUpdate(obj, true);
          if (updated == null)
          {
            foreach (var move in moves) move.Grip.UndoMove();
            throw new InvalidOperationException("Could not update a preview curve.");
          }
          _changed = true;
          sourcesChanged = true;
          if (updated.Id != source.Id)
            throw new InvalidOperationException("A preview curve changed its identity.");
        }

        if (sourcesChanged)
        {
          ReplayChildren();
          SelectPreviewGrips();
        }
      }
      finally
      {
        _doc.Views.RedrawEnabled = redraw;
      }
      _previewLocations = locations;
      if (sourcesChanged || redrawNeeded) _doc.Views.Redraw();
    }

    private void SelectPreviewGrips()
    {
      foreach (var source in _sources)
      {
        foreach (var grip in _doc.Objects.FindId(source.Id)?.GetGrips() ?? Array.Empty<GripObject>())
          if (source.SelectedIndices.Contains(grip.Index) && grip.IsSelected(false) == 0) grip.Select(true);
      }
    }

    private void EnsureUndoRecord()
    {
      if (_undoRecord != 0) return;
      if (_doc.UndoRecordingIsActive)
        throw new InvalidOperationException("Another operation is recording undo during placement.");
      _undoRecord = _doc.BeginUndoRecord("vSetPt preview");
      if (_undoRecord == 0)
        throw new InvalidOperationException("Could not create the preview rollback record.");
    }

    private void ReplayChildren()
    {
      if (_undoRecord == 0 || _children.Count == 0 || UpdateHistoryDescendants == null) return;
      UpdateHistoryDescendants(_doc.RuntimeSerialNumber);
      if (_doc.CurrentUndoRecordSerialNumber != _undoRecord)
        throw new InvalidOperationException("History replay changed the preview undo record.");
    }

    private void Rollback()
    {
      if (_undoRecord == 0) return;
      var record = _undoRecord;
      if (_doc.CurrentUndoRecordSerialNumber != record || !_doc.EndUndoRecord(record))
        throw new InvalidOperationException("The preview rollback record is no longer active.");
      _undoRecord = 0;
      if (_changed)
      {
        UndoPlacementPreview(_doc, record);
        _doc.ClearRedoRecords();
        var restored = _originalGeometry.All(pair =>
          GeometryBase.GeometryEquals(pair.Value, _doc.Objects.FindId(pair.Key)?.Geometry));
        if (!restored)
          throw new InvalidOperationException("Preview rollback did not restore all original geometry.");
      }
      _changed = false;
      HistorySettings.UpdateEnabled = _historyUpdateWasEnabled;
      SelectPreviewGrips();
      _doc.Views.Redraw();
    }

    public void Dispose()
    {
      Stop();
      _timer.Dispose();
      _temporary.Enabled = false;
      try
      {
        Rollback();
      }
      finally
      {
        HistorySettings.UpdateEnabled = _historyUpdateWasEnabled;
        _temporary.SetCurves(Array.Empty<Curve>());
        foreach (var source in _sources) source.Curve.Dispose();
        foreach (var geometry in _originalGeometry.Values) geometry.Dispose();
        _doc.Views.Redraw();
      }
    }
  }

  private static bool PreviewPositionsEqual(Point3d[]? previous, Point3d[] current, double tolerance)
  {
    if (previous == null || previous.Length != current.Length) return false;
    for (var i = 0; i < current.Length; i++)
      if (previous[i].DistanceTo(current[i]) > tolerance) return false;
    return true;
  }

  private static Point3d AlignPoint(
    Point3d original, Point3d target, Plane plane, bool x, bool y, bool z)
  {
    if (x && y && z) return target;
    if (!x && !y && !z) return original;
    if (plane == Plane.WorldXY)
      return new Point3d(x ? target.X : original.X,
        y ? target.Y : original.Y, z ? target.Z : original.Z);
    if (!plane.RemapToPlaneSpace(original, out var local) ||
        !plane.RemapToPlaneSpace(target, out var destination))
      throw new InvalidOperationException("The alignment plane is invalid.");
    return plane.PointAt(x ? destination.X : local.X,
      y ? destination.Y : local.Y, z ? destination.Z : local.Z);
  }
}
