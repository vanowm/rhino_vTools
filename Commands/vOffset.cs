using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vOffset : Command
{
  private const string OptionsSectionName = "vOffset";
  private const string AutoTrimKey = "autoTrim";
  private const string GroupKey = "group";
  private const string DistanceKey = "distance";
  private const string LooseKey = "loose";
  private const string CornerKey = "corner";
  private const string ThroughPointKey = "throughPoint";
  private const string TrimKey = "trim";
  private const string ToleranceKey = "tolerance";
  private const string BothSidesKey = "bothSides";
  private const string InCPlaneKey = "inCPlane";
  private const string CapKey = "cap";
  private const string OutputLayerKey = "outputLayer";

  private static readonly string[] CornerNames = { "None", "Sharp", "Round", "Smooth", "Chamfer" };
  private static readonly string[] CapNames = { "None", "Flat", "Round" };
  private static readonly string[] OutputLayerNames = { "Current", "Input" };

  private static bool _autoTrim;
  private static bool _group;
  private static double _distance = 0.5;
  private static bool _loose;
  private static int _corner = 1;
  private static bool _throughPoint;
  private static bool _trim = true;
  private static double _tolerance = 0.001;
  private static bool _bothSides;
  private static bool _inCPlane = true;
  private static int _cap;
  private static int _outputLayer;
  private static bool _restartingAfterOffsetDelegate;
  private static bool _continuingAfterOffsetDelegate;
  private static EventHandler? _pendingOffsetIdleHandler;
  private static EventHandler? _pendingHistoryIdleHandler;
  private static PendingOffset? _pendingOffset;
  private static PendingHistoryAction? _pendingHistoryAction;
  private static readonly Stack<OffsetUndoRecord> UndoHistory = new();
  private static readonly Stack<OffsetUndoRecord> RedoHistory = new();

  public override string EnglishName => "vOffset";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    if (_restartingAfterOffsetDelegate)
    {
      _restartingAfterOffsetDelegate = false;
      UndoHistory.Clear();
      RedoHistory.Clear();
      return Result.Success;
    }

    if (_continuingAfterOffsetDelegate)
    {
      _continuingAfterOffsetDelegate = false;
    }
    else
    {
      UndoHistory.Clear();
      RedoHistory.Clear();
    }

    CancelPendingOffset();
    LoadPersistedOptions();

    using var shortcutSession = new LocalUndoRedoShortcutSession(
      "vOffset",
      redo => new OffsetHistoryRequest(redo));
    var picked = PickSourceCurve(doc, out var historyRequest);
    if (historyRequest.HasValue)
    {
      QueueHistoryAction(doc, historyRequest.Value);
      return Result.Success;
    }

    if (picked == null)
      return Result.Cancel;

    var source = new OffsetSource(
      doc.RuntimeSerialNumber,
      picked.ObjectId,
      picked.RuntimeSerialNumber,
      picked.Curve,
      picked.GroupIndices,
      FindCueKinks(picked.Curve),
      FindTouchingDrivers(doc, picked.ObjectId, picked.Curve, CurveEnd.Start),
      FindTouchingDrivers(doc, picked.ObjectId, picked.Curve, CurveEnd.End));

    var sidePoint = PickOffsetSide(doc, source, out historyRequest);
    if (historyRequest.HasValue)
    {
      QueueHistoryAction(doc, historyRequest.Value);
      return Result.Success;
    }

    if (!sidePoint.HasValue)
      return Result.Cancel;

    _pendingOffset = new PendingOffset(
      source,
      sidePoint.Value,
      CurrentSettings());

    _pendingOffsetIdleHandler = OnLaunchOffsetOnIdle;
    RhinoApp.Idle += _pendingOffsetIdleHandler;
    return Result.Success;
  }

  private static SourcePick? PickSourceCurve(RhinoDoc doc, out bool? historyRequest)
  {
    historyRequest = null;

    while (true)
    {
      using var getter = new GetObject();
      getter.SetCommandPrompt("Select curve to offset");
      getter.GeometryFilter = ObjectType.Curve;
      getter.GroupSelect = false;
      getter.SubObjectSelect = false;
      getter.EnablePreSelect(true, true);
      getter.DeselectAllBeforePostSelect = false;
      getter.EnableClearObjectsOnEntry(false);
      getter.EnableUnselectObjectsOnExit(false);
      getter.AcceptNumber(true, false);
      getter.AcceptUndo(UndoHistory.Count > 0);
      getter.AcceptCustomMessage(true);

      var distanceOption = new OptionDouble(_distance, RhinoMath.ZeroTolerance, double.MaxValue);
      var toleranceOption = new OptionDouble(_tolerance, 0.0, double.MaxValue);
      var looseToggle = new OptionToggle(_loose, "No", "Yes");
      var throughPointToggle = new OptionToggle(_throughPoint, "No", "Yes");
      var trimToggle = new OptionToggle(_trim, "No", "Yes");
      var bothSidesToggle = new OptionToggle(_bothSides, "No", "Yes");
      var inCPlaneToggle = new OptionToggle(_inCPlane, "No", "Yes");
      var outputLayerToggle = new OptionToggle(_outputLayer == 0, "Input", "Current");
      var groupToggle = new OptionToggle(_group, "No", "Yes");
      var autoTrimToggle = new OptionToggle(_autoTrim, "No", "Yes");

      var distanceOptionIndex = getter.AddOptionDouble("Distance", ref distanceOption);
      getter.AddOptionToggle("Loose", ref looseToggle);
      var cornerOptionIndex = getter.AddOptionList("Corner", CornerNames, _corner);
      getter.AddOptionToggle("ThroughPoint", ref throughPointToggle);
      getter.AddOptionToggle("Trim", ref trimToggle);
      getter.AddOptionDouble("Tolerance", ref toleranceOption);
      getter.AddOptionToggle("BothSides", ref bothSidesToggle);
      getter.AddOptionToggle("InCPlane", ref inCPlaneToggle);
      var capOptionIndex = getter.AddOptionList("Cap", CapNames, _cap);
      getter.AddOptionToggle("OutputLayer", ref outputLayerToggle);
      getter.AddOptionToggle("Group", ref groupToggle);
      getter.AddOptionToggle("AutoTrim", ref autoTrimToggle);
      var undoOptionIndex = UndoHistory.Count > 0
        ? getter.AddOption("Undo", string.Empty, true)
        : -1;
      var redoOptionIndex = RedoHistory.Count > 0
        ? getter.AddOption("Redo", string.Empty, true)
        : -1;

      var result = getter.Get();

      if (result == GetResult.CustomMessage &&
          getter.CustomMessage() is OffsetHistoryRequest shortcutRequest)
      {
        var available = shortcutRequest.Redo ? RedoHistory.Count : UndoHistory.Count;
        if (available == 0)
        {
          RhinoApp.WriteLine(shortcutRequest.Redo
            ? "vOffset: nothing to redo."
            : "vOffset: nothing to undo.");
          continue;
        }

        historyRequest = shortcutRequest.Redo;
        return null;
      }

      if (getter.CommandResult() != Result.Success)
        return null;

      if (result == GetResult.Undo)
      {
        if (UndoHistory.Count == 0)
        {
          RhinoApp.WriteLine("vOffset: nothing to undo.");
          continue;
        }

        historyRequest = false;
        return null;
      }

      if (result == GetResult.Number)
      {
        _distance = Math.Max(RhinoMath.ZeroTolerance, getter.Number());
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Option)
      {
        var option = getter.Option();
        if (option == null)
          continue;

        if (option.Index == undoOptionIndex)
        {
          historyRequest = false;
          return null;
        }

        if (option.Index == redoOptionIndex)
        {
          historyRequest = true;
          return null;
        }

        if (option.Index == distanceOptionIndex)
          _distance = Math.Max(RhinoMath.ZeroTolerance, distanceOption.CurrentValue);
        else if (option.Index == cornerOptionIndex)
          _corner = ClampIndex(option.CurrentListOptionIndex, CornerNames.Length);
        else if (option.Index == capOptionIndex)
          _cap = ClampIndex(option.CurrentListOptionIndex, CapNames.Length);

        _loose = looseToggle.CurrentValue;
        _throughPoint = throughPointToggle.CurrentValue;
        _trim = trimToggle.CurrentValue;
        _tolerance = Math.Max(0.0, toleranceOption.CurrentValue);
        _bothSides = bothSidesToggle.CurrentValue;
        _inCPlane = inCPlaneToggle.CurrentValue;
        _outputLayer = outputLayerToggle.CurrentValue ? 0 : 1;
        _group = groupToggle.CurrentValue;
        _autoTrim = autoTrimToggle.CurrentValue;
        SavePersistedOptions();
        continue;
      }

      if (result != GetResult.Object || getter.ObjectCount == 0)
        return null;

      var objRef = getter.Object(0);
      var curve = objRef?.Curve();
      if (objRef == null || curve == null)
        continue;

      var duplicate = curve.DuplicateCurve();
      if (duplicate == null)
        continue;

      if (_autoTrim && duplicate.IsClosed)
      {
        RhinoApp.WriteLine("vOffset: AutoTrim applies only to open source curves.");
      }

      var sourceObject = objRef.Object();
      var groupIndices = sourceObject?.Attributes.GetGroupList()
        ?.Distinct()
        .ToArray() ?? Array.Empty<int>();
      return new SourcePick(
        objRef.ObjectId,
        sourceObject?.RuntimeSerialNumber ?? 0,
        duplicate,
        groupIndices);
    }
  }

  private static Point3d? PickOffsetSide(
    RhinoDoc doc,
    OffsetSource source,
    out bool? historyRequest)
  {
    historyRequest = null;
    Vector3d? previousCueTangent = null;

    while (true)
    {
      using var getter = new OffsetSideGetter(e =>
      {
        if (!e.CurrentPoint.IsValid)
          return;

        var settings = CurrentSettings();
        var preview = BuildOffsetPreview(doc, source, e.CurrentPoint, settings);
        try
        {
          var previewColor = OffsetPreviewColor(doc, source, _outputLayer);
          foreach (var curve in preview)
            PreviewDisplay.DrawCurve(e.Display, curve, previewColor);

          previousCueTangent = DrawOffsetCue(
            e.Display,
            doc,
            source,
            e.CurrentPoint,
            settings,
            previousCueTangent);
        }
        finally
        {
          foreach (var curve in preview)
            curve.Dispose();
        }
      });
      getter.EnableTransparentCommands(true);
      getter.EnableObjectSnapCursors(false);
      getter.SetCommandPrompt(_throughPoint ? "Point to offset through" : "Side to offset");
      getter.AcceptNothing(true);
      getter.AcceptNumber(true, false);
      getter.AcceptUndo(UndoHistory.Count > 0);
      getter.AcceptCustomMessage(true);

      var distanceOption = new OptionDouble(_distance, RhinoMath.ZeroTolerance, double.MaxValue);
      var toleranceOption = new OptionDouble(_tolerance, 0.0, double.MaxValue);
      var looseToggle = new OptionToggle(_loose, "No", "Yes");
      var throughPointToggle = new OptionToggle(_throughPoint, "No", "Yes");
      var trimToggle = new OptionToggle(_trim, "No", "Yes");
      var bothSidesToggle = new OptionToggle(_bothSides, "No", "Yes");
      var inCPlaneToggle = new OptionToggle(_inCPlane, "No", "Yes");
      var outputLayerToggle = new OptionToggle(_outputLayer == 0, "Input", "Current");
      var groupToggle = new OptionToggle(_group, "No", "Yes");
      var autoTrimToggle = new OptionToggle(_autoTrim, "No", "Yes");

      var distanceOptionIndex = getter.AddOptionDouble("Distance", ref distanceOption);
      getter.AddOptionToggle("Loose", ref looseToggle);
      var cornerOptionIndex = getter.AddOptionList("Corner", CornerNames, _corner);
      getter.AddOptionToggle("ThroughPoint", ref throughPointToggle);
      getter.AddOptionToggle("Trim", ref trimToggle);
      getter.AddOptionDouble("Tolerance", ref toleranceOption);
      getter.AddOptionToggle("BothSides", ref bothSidesToggle);
      getter.AddOptionToggle("InCPlane", ref inCPlaneToggle);
      var capOptionIndex = getter.AddOptionList("Cap", CapNames, _cap);
      getter.AddOptionToggle("OutputLayer", ref outputLayerToggle);
      getter.AddOptionToggle("Group", ref groupToggle);
      getter.AddOptionToggle("AutoTrim", ref autoTrimToggle);
      var undoOptionIndex = UndoHistory.Count > 0
        ? getter.AddOption("Undo", string.Empty, true)
        : -1;
      var redoOptionIndex = RedoHistory.Count > 0
        ? getter.AddOption("Redo", string.Empty, true)
        : -1;

      var result = getter.Get();

      if (result == GetResult.CustomMessage &&
          getter.CustomMessage() is OffsetHistoryRequest shortcutRequest)
      {
        var available = shortcutRequest.Redo ? RedoHistory.Count : UndoHistory.Count;
        if (available == 0)
        {
          RhinoApp.WriteLine(shortcutRequest.Redo
            ? "vOffset: nothing to redo."
            : "vOffset: nothing to undo.");
          continue;
        }

        historyRequest = shortcutRequest.Redo;
        return null;
      }

      if (getter.CommandResult() != Result.Success)
        return null;

      if (result == GetResult.Undo)
      {
        if (UndoHistory.Count == 0)
          continue;
        historyRequest = false;
        return null;
      }

      if (result == GetResult.Number)
      {
        _distance = Math.Max(RhinoMath.ZeroTolerance, getter.Number());
        SavePersistedOptions();
        continue;
      }

      if (result == GetResult.Option)
      {
        var option = getter.Option();
        if (option == null)
          continue;

        if (option.Index == undoOptionIndex)
        {
          historyRequest = false;
          return null;
        }

        if (option.Index == redoOptionIndex)
        {
          historyRequest = true;
          return null;
        }

        if (option.Index == distanceOptionIndex)
          _distance = Math.Max(RhinoMath.ZeroTolerance, distanceOption.CurrentValue);
        else if (option.Index == cornerOptionIndex)
          _corner = ClampIndex(option.CurrentListOptionIndex, CornerNames.Length);
        else if (option.Index == capOptionIndex)
          _cap = ClampIndex(option.CurrentListOptionIndex, CapNames.Length);

        _loose = looseToggle.CurrentValue;
        _throughPoint = throughPointToggle.CurrentValue;
        _trim = trimToggle.CurrentValue;
        _tolerance = Math.Max(0.0, toleranceOption.CurrentValue);
        _bothSides = bothSidesToggle.CurrentValue;
        _inCPlane = inCPlaneToggle.CurrentValue;
        _outputLayer = outputLayerToggle.CurrentValue ? 0 : 1;
        _group = groupToggle.CurrentValue;
        _autoTrim = autoTrimToggle.CurrentValue;
        SavePersistedOptions();
        doc.Views.Redraw();
        continue;
      }

      if (result == GetResult.Point)
        return getter.Point();

      return null;
    }
  }

  private static OffsetSettings CurrentSettings() => new(
    _distance,
    _loose,
    _corner,
    _throughPoint,
    _trim,
    _tolerance,
    _bothSides,
    _inCPlane,
    _cap,
    _outputLayer,
    _group,
    _autoTrim);

  private static int ClampIndex(int value, int count) =>
    Math.Max(0, Math.Min(count - 1, value));

  private static void LoadPersistedOptions()
  {
    var values = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var autoTrim = _autoTrim;
        var group = _group;
        var distance = _distance;
        var loose = _loose;
        var corner = _corner;
        var throughPoint = _throughPoint;
        var trim = _trim;
        var tolerance = _tolerance;
        var bothSides = _bothSides;
        var inCPlane = _inCPlane;
        var cap = _cap;
        var outputLayer = _outputLayer;

        if (ToolsOptionStore.TryGetBool(section, AutoTrimKey, out var boolValue)) autoTrim = boolValue;
        if (ToolsOptionStore.TryGetBool(section, GroupKey, out boolValue)) group = boolValue;
        if (ToolsOptionStore.TryGetDouble(section, DistanceKey, out var doubleValue)) distance = doubleValue;
        if (ToolsOptionStore.TryGetBool(section, LooseKey, out boolValue)) loose = boolValue;
        if (ToolsOptionStore.TryGetDouble(section, CornerKey, out doubleValue)) corner = (int)Math.Round(doubleValue);
        if (ToolsOptionStore.TryGetBool(section, ThroughPointKey, out boolValue)) throughPoint = boolValue;
        if (ToolsOptionStore.TryGetBool(section, TrimKey, out boolValue)) trim = boolValue;
        if (ToolsOptionStore.TryGetDouble(section, ToleranceKey, out doubleValue)) tolerance = doubleValue;
        if (ToolsOptionStore.TryGetBool(section, BothSidesKey, out boolValue)) bothSides = boolValue;
        if (ToolsOptionStore.TryGetBool(section, InCPlaneKey, out boolValue)) inCPlane = boolValue;
        if (ToolsOptionStore.TryGetDouble(section, CapKey, out doubleValue)) cap = (int)Math.Round(doubleValue);
        if (ToolsOptionStore.TryGetDouble(section, OutputLayerKey, out doubleValue)) outputLayer = (int)Math.Round(doubleValue);

        return (autoTrim, group, distance, loose, corner, throughPoint, trim, tolerance, bothSides, inCPlane, cap, outputLayer);
      });

    _autoTrim = values.autoTrim;
    _group = values.group;
    _distance = Math.Max(RhinoMath.ZeroTolerance, values.distance);
    _loose = values.loose;
    _corner = ClampIndex(values.corner, CornerNames.Length);
    _throughPoint = values.throughPoint;
    _trim = values.trim;
    _tolerance = Math.Max(0.0, values.tolerance);
    _bothSides = values.bothSides;
    _inCPlane = values.inCPlane;
    _cap = ClampIndex(values.cap, CapNames.Length);
    _outputLayer = ClampIndex(values.outputLayer, OutputLayerNames.Length);
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[AutoTrimKey] = _autoTrim;
        section[GroupKey] = _group;
        section[DistanceKey] = _distance;
        section[LooseKey] = _loose;
        section[CornerKey] = _corner;
        section[ThroughPointKey] = _throughPoint;
        section[TrimKey] = _trim;
        section[ToleranceKey] = _tolerance;
        section[BothSidesKey] = _bothSides;
        section[InCPlaneKey] = _inCPlane;
        section[CapKey] = _cap;
        section[OutputLayerKey] = _outputLayer;
      });
  }

  private static void CancelPendingOffset()
  {
    if (_pendingOffsetIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingOffsetIdleHandler;
      _pendingOffsetIdleHandler = null;
    }

    _pendingOffset = null;
  }

  private static void QueueHistoryAction(RhinoDoc doc, bool redo)
  {
    if (_pendingHistoryIdleHandler != null)
      RhinoApp.Idle -= _pendingHistoryIdleHandler;

    _pendingHistoryAction = new PendingHistoryAction(doc.RuntimeSerialNumber, redo);
    _pendingHistoryIdleHandler = OnHistoryActionIdle;
    RhinoApp.Idle += _pendingHistoryIdleHandler;
  }

  private static void OnHistoryActionIdle(object? sender, EventArgs e)
  {
    if (_pendingHistoryIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingHistoryIdleHandler;
      _pendingHistoryIdleHandler = null;
    }

    var request = _pendingHistoryAction;
    _pendingHistoryAction = null;
    if (request == null)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != request.DocSerial)
      return;

    var source = request.Redo ? RedoHistory : UndoHistory;
    var destination = request.Redo ? UndoHistory : RedoHistory;
    if (!source.TryPeek(out var record))
    {
      RhinoApp.WriteLine(request.Redo
        ? "vOffset: nothing to redo."
        : "vOffset: nothing to undo.");
      RestartContinuousOffset();
      return;
    }

    var undoActiveBefore = doc.UndoActive;
    var redoActiveBefore = doc.RedoActive;
    var commandResult = RhinoApp.RunScript(request.Redo ? "_Redo" : "_Undo", false);
    var stateMatches = request.Redo
      ? record.OutputIds.All(id => IsObjectPresent(doc, id))
      : record.OutputIds.All(id => !IsObjectPresent(doc, id));
    Log.Write(
      "vOffset",
      "{0} command_result={1} undo_active_before={2} redo_active_before={3} state_matches={4}",
      request.Redo ? "Redo" : "Undo",
      commandResult,
      undoActiveBefore,
      redoActiveBefore,
      stateMatches);

    if (stateMatches)
    {
      source.Pop();
      destination.Push(record);
      RhinoApp.WriteLine(request.Redo ? "vOffset: offset redone." : "vOffset: offset undone.");
      Log.Write(
        "vOffset",
        "{0} completed outputs={1} undo_available={2} redo_available={3}",
        request.Redo ? "Redo" : "Undo",
        record.OutputIds.Count,
        UndoHistory.Count,
        RedoHistory.Count);
    }
    else
    {
      RhinoApp.WriteLine(request.Redo
        ? "vOffset: redo did not restore the expected offset."
        : "vOffset: undo did not remove the expected offset.");
      Log.Write(
        "vOffset",
        "{0} state mismatch outputs={1}",
        request.Redo ? "Redo" : "Undo",
        string.Join(",", record.OutputIds));
    }

    doc.Views.Redraw();
    RestartContinuousOffset();
  }

  private static bool IsObjectPresent(RhinoDoc doc, Guid objectId)
  {
    var obj = doc.Objects.FindId(objectId);
    return obj != null && !obj.IsDeleted;
  }

  private static void RestartContinuousOffset()
  {
    _continuingAfterOffsetDelegate = true;
    _ = RhinoApp.RunScript("_vOffset", false);
    _continuingAfterOffsetDelegate = false;
  }

  private static List<Curve> BuildOffsetPreview(
    RhinoDoc doc,
    OffsetSource source,
    Point3d sidePoint,
    OffsetSettings settings)
  {
    var plane = OffsetPlane(doc, source.SourceCurve, settings.InCPlane);
    var tolerance = settings.Tolerance > 0.0
      ? settings.Tolerance
      : doc.ModelAbsoluteTolerance;
    var distance = settings.ThroughPoint
      ? ThroughPointDistance(source.SourceCurve, plane, sidePoint, tolerance)
      : settings.Distance;
    if (distance <= RhinoMath.ZeroTolerance)
      return new List<Curve>();

    var preview = OffsetToward(
      source.SourceCurve,
      sidePoint,
      plane.ZAxis,
      distance,
      tolerance,
      doc.ModelAngleToleranceRadians,
      settings);

    if (settings.BothSides)
    {
      var oppositePoint = OppositeSidePoint(source.SourceCurve, plane, sidePoint);
      preview.AddRange(OffsetToward(
        source.SourceCurve,
        oppositePoint,
        plane.ZAxis,
        distance,
        tolerance,
        doc.ModelAngleToleranceRadians,
        settings));
    }

    return settings.AutoTrim && !source.SourceCurve.IsClosed
      ? AutoTrimPreview(doc, source, preview)
      : preview;
  }

  private static Color OffsetPreviewColor(
    RhinoDoc doc,
    OffsetSource source,
    int outputLayer)
  {
    var layerIndex = doc.Layers.CurrentLayerIndex;
    if (ClampIndex(outputLayer, OutputLayerNames.Length) == 1)
    {
      var sourceObject = doc.Objects.FindId(source.SourceId);
      if (sourceObject != null)
        layerIndex = sourceObject.Attributes.LayerIndex;
    }

    var color = layerIndex >= 0 && layerIndex < doc.Layers.Count
      ? doc.Layers[layerIndex].Color
      : Color.White;
    return Color.FromArgb(176, color.R, color.G, color.B);
  }

  private static Vector3d? DrawOffsetCue(
    Rhino.Display.DisplayPipeline display,
    RhinoDoc doc,
    OffsetSource source,
    Point3d cursorPoint,
    OffsetSettings settings,
    Vector3d? previousTangent)
  {
    var sourceCurve = source.SourceCurve;
    if (!sourceCurve.ClosestPoint(cursorPoint, out var sourceParameter))
      return previousTangent;

    var sourcePoint = sourceCurve.PointAt(sourceParameter);
    var plane = OffsetPlane(doc, sourceCurve, settings.InCPlane);
    var tolerance = settings.Tolerance > 0.0
      ? settings.Tolerance
      : doc.ModelAbsoluteTolerance;
    var distance = settings.ThroughPoint
      ? ThroughPointDistance(sourceCurve, plane, cursorPoint, tolerance)
      : settings.Distance;
    var anchor = ResolveCueAnchor(
      sourceCurve,
      source.CueKinks,
      sourceParameter,
      sourcePoint,
      cursorPoint,
      plane,
      distance,
      doc.ModelAbsoluteTolerance,
      previousTangent);
    sourcePoint = anchor.Point;
    var tangent = anchor.Tangent;
    var perpendicular = Vector3d.CrossProduct(plane.ZAxis, tangent);
    if (!perpendicular.Unitize())
      return previousTangent;

    if (perpendicular * (cursorPoint - sourcePoint) < 0.0)
      perpendicular.Reverse();

    if (distance <= RhinoMath.ZeroTolerance)
      return tangent;

    var targetPoint = sourcePoint + perpendicular * distance;
    PreviewDisplay.DrawLine(display, sourcePoint, targetPoint, Color.Black);
    PreviewDisplay.DrawLine(display, targetPoint, cursorPoint, Color.White);
    display.DrawPoint(
      targetPoint,
      Rhino.Display.PointStyle.RoundActivePoint,
      Color.Black,
      Color.White,
      3.0f,
      1.0f,
      0.0f,
      0.0f,
      true,
      true);
    return tangent;
  }

  private static CueAnchor ResolveCueAnchor(
    Curve source,
    IReadOnlyList<CueKink> cueKinks,
    double parameter,
    Point3d sourcePoint,
    Point3d cursorPoint,
    Plane plane,
    double offsetDistance,
    double modelTolerance,
    Vector3d? previousTangent)
  {
    var tangent = source.TangentAt(parameter);
    var kinkTolerance = Math.Max(
      RhinoMath.ZeroTolerance * 100.0,
      modelTolerance);
    CueKink? kink = null;
    var bestKinkDistance = double.MaxValue;
    foreach (var candidate in cueKinks)
    {
      var distance = candidate.Point.DistanceTo(sourcePoint);
      if (distance > kinkTolerance || distance >= bestKinkDistance)
        continue;

      kink = candidate;
      bestKinkDistance = distance;
    }

    if (kink == null)
      return new CueAnchor(sourcePoint, tangent);

    sourcePoint = kink.Point;
    var before = kink.BeforeTangent;
    var after = kink.AfterTangent;

    var cursorVector = cursorPoint - sourcePoint;
    var beforeDistance = CueTargetDistance(
      sourcePoint,
      cursorPoint,
      plane.ZAxis,
      before,
      offsetDistance);
    var afterDistance = CueTargetDistance(
      sourcePoint,
      cursorPoint,
      plane.ZAxis,
      after,
      offsetDistance);
    var tieTolerance = Math.Max(
      RhinoMath.ZeroTolerance * 100.0,
      cursorVector.Length * 1.0e-9);
    if (Math.Abs(beforeDistance - afterDistance) > tieTolerance)
      return new CueAnchor(
        sourcePoint,
        beforeDistance < afterDistance ? before : after);

    if (previousTangent.HasValue)
    {
      var previous = previousTangent.Value;
      if (previous.Unitize())
      {
        return new CueAnchor(
          sourcePoint,
          Math.Abs(previous * before) >= Math.Abs(previous * after)
            ? before
            : after);
      }
    }

    return new CueAnchor(
      sourcePoint,
      beforeDistance <= afterDistance ? before : after);
  }

  private static List<CueKink> FindCueKinks(Curve source)
  {
    var kinks = new List<CueKink>();
    var domain = source.Domain;
    var epsilon = Math.Max(
      domain.Length * 1.0e-7,
      RhinoMath.ZeroTolerance * 10.0);
    var seek = domain.T0 + epsilon;

    while (seek < domain.T1 &&
           source.GetNextDiscontinuity(
             Continuity.G1_continuous,
             seek,
             domain.T1,
             out var parameter))
    {
      AddKink(parameter, false);
      seek = parameter + epsilon;
    }

    if (source.IsClosed)
      AddKink(domain.T0, true);

    return kinks;

    void AddKink(double parameter, bool seam)
    {
      var beforeParameter = seam
        ? domain.T1 - epsilon
        : Math.Max(domain.T0, parameter - epsilon);
      var afterParameter = seam
        ? domain.T0 + epsilon
        : Math.Min(domain.T1, parameter + epsilon);
      if (afterParameter <= beforeParameter && !seam)
        return;

      var before = source.TangentAt(beforeParameter);
      var after = source.TangentAt(afterParameter);
      if (!before.Unitize() || !after.Unitize())
        return;

      var angle = Vector3d.VectorAngle(before, after);
      if (!RhinoMath.IsValidDouble(angle) || angle < RhinoMath.ToRadians(1.0))
        return;

      kinks.Add(new CueKink(source.PointAt(parameter), before, after));
    }
  }

  private static double CueTargetDistance(
    Point3d sourcePoint,
    Point3d cursorPoint,
    Vector3d planeNormal,
    Vector3d tangent,
    double offsetDistance)
  {
    var perpendicular = Vector3d.CrossProduct(planeNormal, tangent);
    if (!perpendicular.Unitize())
      return double.MaxValue;

    var cursorVector = cursorPoint - sourcePoint;
    if (perpendicular * cursorVector < 0.0)
      perpendicular.Reverse();

    var targetPoint = sourcePoint + perpendicular * offsetDistance;
    return targetPoint.DistanceTo(cursorPoint);
  }

  private static Plane OffsetPlane(RhinoDoc doc, Curve source, bool inCPlane)
  {
    if (!inCPlane && source.TryGetPlane(out var curvePlane, doc.ModelAbsoluteTolerance))
      return curvePlane;

    return doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
  }

  private static double ThroughPointDistance(
    Curve source,
    Plane plane,
    Point3d sidePoint,
    double tolerance)
  {
    var projectedPoint = plane.ClosestPoint(sidePoint);
    Curve? projected = null;
    try
    {
      projected = Curve.ProjectToPlane(source, plane);
      var working = projected ?? source;
      if (working.ClosestPoint(projectedPoint, out var parameter))
        return working.PointAt(parameter).DistanceTo(projectedPoint);
    }
    catch
    {
    }
    finally
    {
      projected?.Dispose();
    }

    return Math.Max(tolerance, source.PointAtStart.DistanceTo(projectedPoint));
  }

  private static Point3d OppositeSidePoint(Curve source, Plane plane, Point3d sidePoint)
  {
    var projectedPoint = plane.ClosestPoint(sidePoint);
    Curve? projected = null;
    try
    {
      projected = Curve.ProjectToPlane(source, plane);
      var working = projected ?? source;
      if (working.ClosestPoint(projectedPoint, out var parameter))
      {
        var onCurve = working.PointAt(parameter);
        return onCurve + (onCurve - projectedPoint);
      }
    }
    catch
    {
    }
    finally
    {
      projected?.Dispose();
    }

    return source.PointAtStart + (source.PointAtStart - projectedPoint);
  }

  private static List<Curve> OffsetToward(
    Curve source,
    Point3d sidePoint,
    Vector3d normal,
    double distance,
    double tolerance,
    double angleTolerance,
    OffsetSettings settings)
  {
    try
    {
      var curves = source.Offset(
        sidePoint,
        normal,
        distance,
        tolerance,
        angleTolerance,
        settings.Loose,
        (CurveOffsetCornerStyle)ClampIndex(settings.Corner, CornerNames.Length),
        (CurveOffsetEndStyle)ClampIndex(settings.Cap, CapNames.Length));
      return curves?.Where(curve => curve != null && curve.IsValid).ToList() ?? new List<Curve>();
    }
    catch
    {
      return new List<Curve>();
    }
  }

  private static List<Curve> AutoTrimPreview(
    RhinoDoc doc,
    OffsetSource source,
    IReadOnlyList<Curve> curves)
  {
    var adjustedCurves = new List<Curve>(curves.Count);
    foreach (var output in curves)
    {
      var adjusted = output;
      if (adjusted.IsClosed)
      {
        adjustedCurves.Add(adjusted);
        continue;
      }

      var sameDirection = SameEndpointDirection(source.SourceCurve, adjusted);
      if (source.StartDrivers.Count > 0)
      {
        var next = AdjustOffsetEnd(
          doc,
          adjusted,
          sameDirection ? CurveEnd.Start : CurveEnd.End,
          source.StartDrivers,
          out _,
          out _);
        if (!ReferenceEquals(next, adjusted))
          adjusted.Dispose();
        adjusted = next;
      }

      if (source.EndDrivers.Count > 0)
      {
        var next = AdjustOffsetEnd(
          doc,
          adjusted,
          sameDirection ? CurveEnd.End : CurveEnd.Start,
          source.EndDrivers,
          out _,
          out _);
        if (!ReferenceEquals(next, adjusted))
          adjusted.Dispose();
        adjusted = next;
      }

      adjustedCurves.Add(adjusted);
    }

    return adjustedCurves;
  }

  private static string BuildNativeOffsetScript(RhinoDoc doc, PendingOffset pending)
  {
    var settings = pending.Settings;
    var plane = OffsetPlane(doc, pending.SourceCurve, settings.InCPlane);
    var distance = settings.ThroughPoint && settings.BothSides
      ? ThroughPointDistance(
          pending.SourceCurve,
          plane,
          pending.SidePoint,
          settings.Tolerance > 0.0 ? settings.Tolerance : doc.ModelAbsoluteTolerance)
      : settings.Distance;
    var parts = new List<string>
    {
      "_-Offset",
      $"_Distance={Number(distance)}",
      $"_Loose={YesNo(settings.Loose)}"
    };

    if (!settings.Loose)
    {
      parts.Add($"_Corner=_{CornerNames[ClampIndex(settings.Corner, CornerNames.Length)]}");
      parts.Add($"_Tolerance={Number(settings.Tolerance)}");
    }

    parts.Add($"_Trim={YesNo(settings.Trim)}");
    parts.Add($"_InCPlane={YesNo(settings.InCPlane)}");
    parts.Add($"_Cap=_{CapNames[ClampIndex(settings.Cap, CapNames.Length)]}");
    parts.Add($"_OutputLayer=_{OutputLayerNames[ClampIndex(settings.OutputLayer, OutputLayerNames.Length)]}");

    if (settings.BothSides)
    {
      parts.Add("_BothSides");
    }
    else if (settings.ThroughPoint)
    {
      parts.Add("_ThroughPoint");
      parts.Add(WorldPoint(pending.SidePoint));
    }
    else
    {
      parts.Add(WorldPoint(pending.SidePoint));
    }

    return string.Join(" ", parts);
  }

  private static string Number(double value) =>
    value.ToString("R", CultureInfo.InvariantCulture);

  private static string YesNo(bool value) => value ? "_Yes" : "_No";

  private static string WorldPoint(Point3d point) =>
    string.Create(
      CultureInfo.InvariantCulture,
      $"w{point.X:R},{point.Y:R},{point.Z:R}");

  private static void OnLaunchOffsetOnIdle(object? sender, EventArgs e)
  {
    if (_pendingOffsetIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingOffsetIdleHandler;
      _pendingOffsetIdleHandler = null;
    }

    var pending = _pendingOffset;
    _pendingOffset = null;
    if (pending == null)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != pending.DocSerial)
      return;

    var sourceObject = doc.Objects.FindId(pending.SourceId);
    if (sourceObject?.Geometry is not Curve)
    {
      RhinoApp.WriteLine("vOffset: source curve no longer exists.");
      return;
    }

    var objectIdsBefore = CurrentObjectIds(doc);
    doc.Objects.UnselectAll();
    if (!doc.Objects.Select(pending.SourceId))
    {
      RhinoApp.WriteLine("vOffset: failed to preselect the source curve.");
      return;
    }

    doc.Views.Redraw();

    var recordingWasEnabled = doc.UndoRecordingEnabled;
    var temporaryOutputIds = new List<Guid>();
    var finalSnapshots = new List<OffsetOutputSnapshot>();
    var sourceDeletedByNative = false;
    var nativeResult = false;
    try
    {
      doc.UndoRecordingEnabled = false;
      var nativeScript = BuildNativeOffsetScript(doc, pending);
      Log.Write("vOffset", "native handoff {0}", nativeScript);
      nativeResult = RhinoApp.RunScript(nativeScript, false);

      temporaryOutputIds = doc.Objects
        .GetObjectList(ObjectType.Curve)
        .Where(obj => obj != null && !objectIdsBefore.Contains(obj.Id))
        .Select(obj => obj.Id)
        .ToList();

      if (pending.AutoTrim && !pending.SourceCurve.IsClosed && temporaryOutputIds.Count > 0)
        _ = ApplyAutoTrim(doc, pending, temporaryOutputIds);

      finalSnapshots = CaptureOffsetOutputs(doc, temporaryOutputIds);
      sourceDeletedByNative = IsSourceDeleted(doc, pending);

      foreach (var outputId in temporaryOutputIds)
      {
        var output = doc.Objects.FindId(outputId);
        if (output != null && !doc.Objects.Purge(output.RuntimeSerialNumber))
          Log.Write("vOffset", "Temporary output purge failed output={0}", outputId);
      }

      if (sourceDeletedByNative &&
          pending.SourceRuntimeSerialNumber != 0 &&
          !doc.Objects.Undelete(pending.SourceRuntimeSerialNumber))
      {
        Log.Write(
          "vOffset",
          "Could not restore source after native DeleteInput source={0} runtime_serial={1}",
          pending.SourceId,
          pending.SourceRuntimeSerialNumber);
      }
    }
    finally
    {
      doc.UndoRecordingEnabled = recordingWasEnabled;
    }

    var outputIds = finalSnapshots.Count > 0
      ? RecordFinalOffset(doc, pending, finalSnapshots, sourceDeletedByNative)
      : new List<Guid>();

    Log.Write(
      "vOffset",
      "Native offset result={0} temporary_outputs={1} final_outputs={2} delete_input={3}",
      nativeResult,
      temporaryOutputIds.Count,
      outputIds.Count,
      sourceDeletedByNative);

    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    if (outputIds.Count > 0)
    {
      UndoHistory.Push(new OffsetUndoRecord(outputIds));
      RedoHistory.Clear();
      Log.Write(
        "vOffset",
        "Offset completed outputs={0} undo_available={1} undo_record={2} next_undo_record={3}",
        string.Join(",", outputIds),
        UndoHistory.Count,
        doc.CurrentUndoRecordSerialNumber,
        doc.NextUndoRecordSerialNumber);
      RestartContinuousOffset();
      return;
    }

    _restartingAfterOffsetDelegate = true;
    _ = RhinoApp.RunScript("_vOffset", false);
    _restartingAfterOffsetDelegate = false;
  }

  private static HashSet<Guid> CurrentObjectIds(RhinoDoc doc)
  {
    return doc.Objects
      .GetObjectList(ObjectType.AnyObject)
      .Where(obj => obj != null)
      .Select(obj => obj.Id)
      .ToHashSet();
  }

  private static List<OffsetOutputSnapshot> CaptureOffsetOutputs(
    RhinoDoc doc,
    IReadOnlyList<Guid> outputIds)
  {
    var snapshots = new List<OffsetOutputSnapshot>();
    foreach (var outputId in outputIds)
    {
      var obj = doc.Objects.FindId(outputId);
      if (obj?.Geometry is not Curve curve)
        continue;

      var duplicate = curve.DuplicateCurve();
      if (duplicate == null)
        continue;

      snapshots.Add(new OffsetOutputSnapshot(duplicate, obj.Attributes.Duplicate()));
    }

    return snapshots;
  }

  private static bool IsSourceDeleted(RhinoDoc doc, PendingOffset pending)
  {
    var source = pending.SourceRuntimeSerialNumber == 0
      ? doc.Objects.FindId(pending.SourceId)
      : doc.Objects.Find(pending.SourceRuntimeSerialNumber);
    return source == null || source.IsDeleted;
  }

  private static List<Guid> RecordFinalOffset(
    RhinoDoc doc,
    PendingOffset pending,
    IReadOnlyList<OffsetOutputSnapshot> snapshots,
    bool deleteSource)
  {
    var outputIds = new List<Guid>();
    var undoRecord = doc.BeginUndoRecord("vOffset");
    try
    {
      foreach (var snapshot in snapshots)
      {
        var outputId = doc.Objects.AddCurve(
          snapshot.Curve.DuplicateCurve(),
          snapshot.Attributes.Duplicate());
        if (outputId != Guid.Empty)
          outputIds.Add(outputId);
        else
          Log.Write("vOffset", "Final output add failed");
      }

      if (pending.Group && outputIds.Count > 0)
        ApplyOutputGroups(doc, pending, outputIds);

      if (deleteSource && !doc.Objects.Delete(pending.SourceId, true))
        Log.Write("vOffset", "Recorded DeleteInput failed source={0}", pending.SourceId);
    }
    finally
    {
      if (undoRecord != 0)
        doc.EndUndoRecord(undoRecord);
    }

    Log.Write(
      "vOffset",
      "Recorded final offset undo_record={0} outputs={1} delete_input={2}",
      undoRecord,
      string.Join(",", outputIds),
      deleteSource);
    return outputIds;
  }

  private static void ApplyOutputGroups(
    RhinoDoc doc,
    PendingOffset pending,
    IReadOnlyCollection<Guid> outputIds)
  {
    if (pending.SourceGroupIndices.Count > 0)
    {
      var applied = 0;
      foreach (var groupIndex in pending.SourceGroupIndices)
      {
        if (doc.Groups.FindIndex(groupIndex) == null)
          continue;

        if (doc.Groups.AddToGroup(groupIndex, outputIds))
          applied++;
      }

      Log.Write(
        "vOffset",
        "Applied source groups source={0} groups={1} outputs={2}",
        pending.SourceId,
        applied,
        outputIds.Count);
      return;
    }

    var groupIndexCreated = doc.Groups.Add(
      new[] { pending.SourceId }.Concat(outputIds));
    Log.Write(
      "vOffset",
      "Created source/output group source={0} group={1} outputs={2}",
      pending.SourceId,
      groupIndexCreated,
      outputIds.Count);
  }

  private static List<Curve> FindTouchingDrivers(
    RhinoDoc doc,
    Guid sourceId,
    Curve source,
    CurveEnd sourceEnd)
  {
    var endpoint = sourceEnd == CurveEnd.Start ? source.PointAtStart : source.PointAtEnd;
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance * 2.0, 1.0e-8);
    var drivers = new List<Curve>();

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.Curve,
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false,
      DeletedObjects = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj == null || obj.Id == sourceId || obj.Geometry is not Curve curve)
        continue;

      try
      {
        if (!curve.ClosestPoint(endpoint, out var parameter) ||
            curve.PointAt(parameter).DistanceTo(endpoint) > tolerance)
          continue;

        var duplicate = curve.DuplicateCurve();
        if (duplicate != null)
          drivers.Add(duplicate);
      }
      catch
      {
      }
    }

    return drivers;
  }

  private static bool ApplyAutoTrim(
    RhinoDoc doc,
    PendingOffset pending,
    IReadOnlyList<Guid> outputIds)
  {
    Log.Write(
      "vOffset",
      "AutoTrim source={0} start={1} end={2} start_drivers={3} end_drivers={4}",
      pending.SourceId,
      FormatPoint(pending.SourceCurve.PointAtStart),
      FormatPoint(pending.SourceCurve.PointAtEnd),
      pending.StartDrivers.Count,
      pending.EndDrivers.Count);

    if (pending.StartDrivers.Count == 0 && pending.EndDrivers.Count == 0)
    {
      Log.Write("vOffset", "AutoTrim source={0} skipped: no touching endpoint curves", pending.SourceId);
      return false;
    }

    var plans = new List<OffsetAdjustment>();
    foreach (var outputId in outputIds)
    {
      var obj = doc.Objects.FindId(outputId);
      if (obj?.Geometry is not Curve output || output.IsClosed)
        continue;

      var adjusted = output.DuplicateCurve();
      if (adjusted == null)
        continue;

      var sameDirection = SameEndpointDirection(pending.SourceCurve, adjusted);
      Log.Write(
        "vOffset",
        "AutoTrim output={0} start={1} end={2} same_direction={3}",
        outputId,
        FormatPoint(adjusted.PointAtStart),
        FormatPoint(adjusted.PointAtEnd),
        sameDirection);

      var changed = false;
      if (pending.StartDrivers.Count > 0)
      {
        var offsetEnd = sameDirection ? CurveEnd.Start : CurveEnd.End;
        adjusted = AdjustOffsetEnd(
          doc,
          adjusted,
          offsetEnd,
          pending.StartDrivers,
          out var endChanged,
          out var action);
        changed |= endChanged;
        Log.Write(
          "vOffset",
          "AutoTrim output={0} source_end=start offset_end={1} action={2}",
          outputId,
          offsetEnd,
          action);
      }

      if (pending.EndDrivers.Count > 0)
      {
        var offsetEnd = sameDirection ? CurveEnd.End : CurveEnd.Start;
        adjusted = AdjustOffsetEnd(
          doc,
          adjusted,
          offsetEnd,
          pending.EndDrivers,
          out var endChanged,
          out var action);
        changed |= endChanged;
        Log.Write(
          "vOffset",
          "AutoTrim output={0} source_end=end offset_end={1} action={2}",
          outputId,
          offsetEnd,
          action);
      }

      if (changed)
        plans.Add(new OffsetAdjustment(outputId, adjusted));
    }

    if (plans.Count == 0)
      return false;

    var affectedHistory = plans
      .SelectMany(plan => HistoryBreakWarning.CaptureAffectedRecords(doc, plan.ObjectId))
      .ToHashSet();
    if (!HistoryBreakWarning.Confirm(doc, "Offset", affectedHistory))
      return false;

    var replaced = 0;
    foreach (var plan in plans)
    {
      if (doc.Objects.Replace(plan.ObjectId, plan.Curve))
        replaced++;
      else
        Log.Write("vOffset", "AutoTrim output={0} replace failed", plan.ObjectId);
    }

    Log.Write(
      "vOffset",
      "AutoTrim source={0} outputs={1} adjusted={2}",
      pending.SourceId,
      outputIds.Count,
      replaced);
    return replaced > 0;
  }

  private static bool SameEndpointDirection(Curve source, Curve offset)
  {
    if (source.ClosestPoint(offset.PointAtStart, out var sourceAtOffsetStart) &&
        source.ClosestPoint(offset.PointAtEnd, out var sourceAtOffsetEnd))
    {
      var startNormalized = source.Domain.NormalizedParameterAt(sourceAtOffsetStart);
      var endNormalized = source.Domain.NormalizedParameterAt(sourceAtOffsetEnd);
      if (Math.Abs(startNormalized - endNormalized) > 1.0e-6)
        return startNormalized < endNormalized;
    }

    var sameTangentScore =
      Vector3d.Multiply(source.TangentAtStart, offset.TangentAtStart) +
      Vector3d.Multiply(source.TangentAtEnd, offset.TangentAtEnd);
    var reversedTangentScore =
      -Vector3d.Multiply(source.TangentAtStart, offset.TangentAtEnd) -
      Vector3d.Multiply(source.TangentAtEnd, offset.TangentAtStart);
    if (Math.Abs(sameTangentScore - reversedTangentScore) > 1.0e-6)
      return sameTangentScore >= reversedTangentScore;

    var same = source.PointAtStart.DistanceTo(offset.PointAtStart) +
               source.PointAtEnd.DistanceTo(offset.PointAtEnd);
    var reversed = source.PointAtStart.DistanceTo(offset.PointAtEnd) +
                   source.PointAtEnd.DistanceTo(offset.PointAtStart);
    return same <= reversed;
  }

  private static Curve AdjustOffsetEnd(
    RhinoDoc doc,
    Curve curve,
    CurveEnd end,
    IReadOnlyList<Curve> drivers,
    out bool changed,
    out string action)
  {
    changed = false;
    action = "none";
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance * 2.0, 1.0e-8);
    var endpoint = end == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;

    if (PointTouchesAnyCurve(endpoint, drivers, tolerance))
    {
      action = "already touching";
      return curve;
    }

    if (TryNearestIntersectionFromEnd(doc, curve, end, drivers, out var hitParameter, out var distanceFromEnd))
    {
      if (distanceFromEnd <= tolerance)
      {
        action = "already touching";
        return curve;
      }

      Curve? trimmed;
      try
      {
        trimmed = end == CurveEnd.Start
          ? curve.Trim(new Interval(hitParameter, curve.Domain.T1))
          : curve.Trim(new Interval(curve.Domain.T0, hitParameter));
      }
      catch
      {
        trimmed = null;
      }

      if (trimmed != null && trimmed.IsValid && trimmed.GetLength() > tolerance)
      {
        changed = true;
        action = $"trim {distanceFromEnd:G17}";
        return trimmed;
      }

      action = "trim failed";
      return curve;
    }

    var styles = curve.IsLinear(tolerance)
      ? new[] { CurveExtensionStyle.Line }
      : new[] { CurveExtensionStyle.Smooth, CurveExtensionStyle.Line };
    foreach (var style in styles)
    {
      Curve? extended;
      try
      {
        extended = curve.Extend(end, style, drivers.ToArray());
      }
      catch
      {
        extended = null;
      }

      if (extended == null || !extended.IsValid)
        continue;

      var newEndpoint = end == CurveEnd.Start ? extended.PointAtStart : extended.PointAtEnd;
      if (!PointTouchesAnyCurve(newEndpoint, drivers, tolerance * 5.0))
        continue;

      var originalLength = curve.GetLength();
      var extendedLength = extended.GetLength();
      if (extendedLength <= originalLength + tolerance)
        continue;

      changed = true;
      action = $"extend {extendedLength - originalLength:G17}";
      return extended;
    }

    action = "extend failed";
    return curve;
  }

  private static bool TryNearestIntersectionFromEnd(
    RhinoDoc doc,
    Curve curve,
    CurveEnd end,
    IReadOnlyList<Curve> drivers,
    out double parameter,
    out double distanceFromEnd)
  {
    var bestParameter = RhinoMath.UnsetValue;
    var bestDistance = double.PositiveInfinity;
    var tolerance = doc.ModelAbsoluteTolerance;
    var totalLength = curve.GetLength();

    foreach (var driver in drivers)
    {
      var events = Intersection.CurveCurve(curve, driver, tolerance, tolerance);
      if (events == null)
        continue;

      foreach (var intersection in events)
      {
        if (intersection.IsPoint)
        {
          Consider(intersection.ParameterA);
        }
        else if (intersection.IsOverlap)
        {
          Consider(intersection.OverlapA.T0);
          Consider(intersection.OverlapA.T1);
        }
      }
    }

    parameter = bestParameter;
    distanceFromEnd = bestDistance;
    return bestParameter != RhinoMath.UnsetValue;

    void Consider(double candidate)
    {
      var clamped = Math.Max(curve.Domain.T0, Math.Min(curve.Domain.T1, candidate));
      double distanceFromSelectedEnd;
      double distanceFromOppositeEnd;
      try
      {
        var selectedInterval = end == CurveEnd.Start
          ? new Interval(curve.Domain.T0, clamped)
          : new Interval(clamped, curve.Domain.T1);
        var oppositeInterval = end == CurveEnd.Start
          ? new Interval(clamped, curve.Domain.T1)
          : new Interval(curve.Domain.T0, clamped);
        distanceFromSelectedEnd = curve.GetLength(selectedInterval);
        distanceFromOppositeEnd = curve.GetLength(oppositeInterval);
      }
      catch
      {
        return;
      }

      if (distanceFromSelectedEnd > distanceFromOppositeEnd + tolerance ||
          distanceFromSelectedEnd > totalLength + tolerance ||
          distanceFromSelectedEnd >= bestDistance)
        return;

      bestParameter = clamped;
      bestDistance = distanceFromSelectedEnd;
    }
  }

  private static string FormatPoint(Point3d point)
  {
    return $"({point.X:G17},{point.Y:G17},{point.Z:G17})";
  }

  private static bool PointTouchesAnyCurve(
    Point3d point,
    IReadOnlyList<Curve> curves,
    double tolerance)
  {
    foreach (var curve in curves)
    {
      try
      {
        if (curve.ClosestPoint(point, out var parameter) &&
            curve.PointAt(parameter).DistanceTo(point) <= tolerance)
          return true;
      }
      catch
      {
      }
    }

    return false;
  }

  private sealed record SourcePick(
    Guid ObjectId,
    uint RuntimeSerialNumber,
    Curve Curve,
    IReadOnlyList<int> GroupIndices);

  private sealed record OffsetSource(
    uint DocSerial,
    Guid SourceId,
    uint SourceRuntimeSerialNumber,
    Curve SourceCurve,
    IReadOnlyList<int> SourceGroupIndices,
    IReadOnlyList<CueKink> CueKinks,
    List<Curve> StartDrivers,
    List<Curve> EndDrivers);

  private sealed record CueKink(
    Point3d Point,
    Vector3d BeforeTangent,
    Vector3d AfterTangent);

  private readonly record struct CueAnchor(Point3d Point, Vector3d Tangent);

  private sealed record OffsetSettings(
    double Distance,
    bool Loose,
    int Corner,
    bool ThroughPoint,
    bool Trim,
    double Tolerance,
    bool BothSides,
    bool InCPlane,
    int Cap,
    int OutputLayer,
    bool Group,
    bool AutoTrim);

  private sealed record PendingOffset(
    OffsetSource Source,
    Point3d SidePoint,
    OffsetSettings Settings)
  {
    public uint DocSerial => Source.DocSerial;
    public Guid SourceId => Source.SourceId;
    public uint SourceRuntimeSerialNumber => Source.SourceRuntimeSerialNumber;
    public Curve SourceCurve => Source.SourceCurve;
    public IReadOnlyList<int> SourceGroupIndices => Source.SourceGroupIndices;
    public List<Curve> StartDrivers => Source.StartDrivers;
    public List<Curve> EndDrivers => Source.EndDrivers;
    public bool Group => Settings.Group;
    public bool AutoTrim => Settings.AutoTrim;
  }

  private sealed record OffsetAdjustment(Guid ObjectId, Curve Curve);

  private sealed record OffsetOutputSnapshot(Curve Curve, ObjectAttributes Attributes);

  private sealed record PendingHistoryAction(uint DocSerial, bool Redo);

  private sealed record OffsetUndoRecord(IReadOnlyList<Guid> OutputIds);

  private sealed record OffsetHistoryRequest(bool Redo);

  private sealed class OffsetSideGetter : GetPoint
  {
    private readonly Action<GetPointDrawEventArgs> _draw;

    public OffsetSideGetter(Action<GetPointDrawEventArgs> draw)
    {
      _draw = draw;
    }

    protected override void OnDynamicDraw(GetPointDrawEventArgs e)
    {
      _draw(e);
    }
  }
}
