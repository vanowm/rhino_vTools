using System;
using System.Collections.Generic;
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

  private static bool _autoTrim;
  private static bool _group;
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

    _pendingOffset = new PendingOffset(
      doc.RuntimeSerialNumber,
      picked.ObjectId,
      picked.RuntimeSerialNumber,
      picked.Curve,
      _autoTrim,
      _group,
      picked.GroupIndices,
      FindTouchingDrivers(doc, picked.ObjectId, picked.Curve, CurveEnd.Start),
      FindTouchingDrivers(doc, picked.ObjectId, picked.Curve, CurveEnd.End));

    _pendingOffsetIdleHandler = OnLaunchOffsetOnIdle;
    RhinoApp.Idle += _pendingOffsetIdleHandler;
    return Result.Success;
  }

  private static SourcePick? PickSourceCurve(RhinoDoc doc, out bool? historyRequest)
  {
    historyRequest = null;
    var autoTrimToggle = new OptionToggle(_autoTrim, "No", "Yes");
    var groupToggle = new OptionToggle(_group, "No", "Yes");

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
      getter.AcceptUndo(UndoHistory.Count > 0);
      getter.AcceptCustomMessage(true);
      var undoOptionIndex = UndoHistory.Count > 0
        ? getter.AddOption("Undo", string.Empty, true)
        : -1;
      var redoOptionIndex = RedoHistory.Count > 0
        ? getter.AddOption("Redo", string.Empty, true)
        : -1;
      getter.AddOptionToggle("AutoTrim", ref autoTrimToggle);
      getter.AddOptionToggle("Group", ref groupToggle);

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

      if (result == GetResult.Option)
      {
        if (getter.OptionIndex() == undoOptionIndex)
        {
          historyRequest = false;
          return null;
        }

        if (getter.OptionIndex() == redoOptionIndex)
        {
          historyRequest = true;
          return null;
        }

        _autoTrim = autoTrimToggle.CurrentValue;
        _group = groupToggle.CurrentValue;
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

  private static void LoadPersistedOptions()
  {
    _autoTrim = ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(section, AutoTrimKey, out var value)
        ? value
        : _autoTrim);
    _group = ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(section, GroupKey, out var value)
        ? value
        : _group);
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[AutoTrimKey] = _autoTrim;
        section[GroupKey] = _group;
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
      nativeResult = RhinoApp.RunScript("_Offset", false);

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

  private sealed record PendingOffset(
    uint DocSerial,
    Guid SourceId,
    uint SourceRuntimeSerialNumber,
    Curve SourceCurve,
    bool AutoTrim,
    bool Group,
    IReadOnlyList<int> SourceGroupIndices,
    List<Curve> StartDrivers,
    List<Curve> EndDrivers);

  private sealed record OffsetAdjustment(Guid ObjectId, Curve Curve);

  private sealed record OffsetOutputSnapshot(Curve Curve, ObjectAttributes Attributes);

  private sealed record PendingHistoryAction(uint DocSerial, bool Redo);

  private sealed record OffsetUndoRecord(IReadOnlyList<Guid> OutputIds);

  private sealed record OffsetHistoryRequest(bool Redo);
}
