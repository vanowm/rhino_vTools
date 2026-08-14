using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoPoint2d = Rhino.Geometry.Point2d;

namespace vTools.Commands;

/// <summary>
/// Native trim/extend workflow with optional auto-cutter mode.
/// </summary>
public sealed class vTrim : Command
{
  private const string OptionsSectionName = "vTrim";
  private const string ExtendAsLineKey = "extendAsLine";
  private const string JoinAfterTrimKey = "joinAfterTrim";

  private static bool _extendAsLine = true;
  private static bool _joinAfterTrim = true;
  private static bool _restartingAfterTrimDelegate;
  private static EventHandler? _nativeTrimLaunchIdleHandler;
  private static Guid[]? _pendingNativeTrimCutters;
  private static uint _pendingNativeTrimDocSerial;

  public override string EnglishName => "vTrim";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating to _-Trim — registers vTrim as the
    // repeatable last command without showing any prompt.
    if (_restartingAfterTrimDelegate)
    {
      _restartingAfterTrimDelegate = false;
      return Result.Success;
    }

    CancelPendingNativeTrimLaunch();
    LoadPersistedOptions();

    var history = new SessionHistory();

    var cutters = PickCutters(doc);
    if (cutters.State == PickerState.Cancel)
      return Result.Cancel;

    // Explicit cutters selected: delegate to built-in _-Trim with those cutters
    // pre-selected so the user gets native trim behaviour.
    if (!cutters.AutoMode && cutters.CutterIds.Count > 0)
    {
      SavePersistedOptions();
      return QueueBuiltInTrimWithCutters(doc, cutters.CutterIds);
    }

    // Auto-mode: deselect any remnants and run the custom hover-trim loop.
    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    while (true)
    {
      var pick = PickTarget(doc, cutters.AutoMode, cutters.CutterIds, _extendAsLine, _joinAfterTrim, allowDone: true);
      if (pick.State == PickerState.Cancel)
      {
        SavePersistedOptions();
        return Result.Cancel;
      }

      if (pick.State == PickerState.Done)
      {
        SavePersistedOptions();
        return Result.Success;
      }

      if (pick.State == PickerState.Undo)
      {
        if (!TryUndo(doc, history))
          RhinoApp.WriteLine("vTrim: nothing to undo.");

        doc.Views.Redraw();
        continue;
      }

      if (pick.State == PickerState.Redo)
      {
        if (!TryRedo(doc, history))
          RhinoApp.WriteLine("vTrim: nothing to redo.");

        doc.Views.Redraw();
        continue;
      }

      _extendAsLine = pick.ExtendAsLine;
      _joinAfterTrim = pick.JoinAfterTrim;
      SavePersistedOptions();

      if (pick.TargetObject == null || pick.TargetCurve == null || !pick.PickPoint.IsValid)
        continue;

      Log.Write(
        "vTrim",
        "click mode={0} preview={1} target={2} point=({3:G17},{4:G17},{5:G17}) cutters={6} target_length={7:G17} pick_position={8:G17} removed_length={9:G17} output_lengths={10} preview_failure={11}",
        pick.ExtendMode ? "extend" : "trim",
        pick.HadValidPreview,
        pick.TargetObject.Id,
        pick.PickPoint.X,
        pick.PickPoint.Y,
        pick.PickPoint.Z,
        cutters.AutoMode ? "auto" : cutters.CutterIds.Count.ToString(),
        TargetCurveLength(pick),
        PickPosition(pick),
        PreviewCurveLength(pick),
        PreviewOutputLengths(pick),
        string.IsNullOrWhiteSpace(pick.PreviewFailure) ? "none" : pick.PreviewFailure);

      var changed = false;
      ActionRecord? record = null;
      if (pick.ExtendMode)
      {
        changed = ExtendCurveObject(
          doc,
          pick.TargetObject,
          pick.PreviewExtendPlan,
          out record);
      }
      else
      {
        changed = TrimCurveObject(
          doc,
          pick.TargetObject,
          pick.PreviewTrimPlan,
          out record);
      }

      if (changed)
      {
        if (record != null)
          history.Push(record);

        doc.Objects.UnselectAll();
        doc.Views.Redraw();
      }
      else
      {
        doc.Objects.UnselectAll();
        doc.Views.Redraw();
        RhinoApp.WriteLine("vTrim: click did not produce a valid trim/extend result.");
      }
    }
  }

  private static Result QueueBuiltInTrimWithCutters(RhinoDoc doc, IReadOnlyList<Guid> cutterIds)
  {
    var validIds = cutterIds.Where(id => id != Guid.Empty).Distinct().ToArray();
    if (validIds.Length == 0)
    {
      RhinoApp.WriteLine("vTrim: no valid cutting curves selected.");
      return Result.Cancel;
    }

    _pendingNativeTrimCutters = validIds;
    _pendingNativeTrimDocSerial = doc.RuntimeSerialNumber;

    if (_nativeTrimLaunchIdleHandler != null)
      RhinoApp.Idle -= _nativeTrimLaunchIdleHandler;

    _nativeTrimLaunchIdleHandler = OnLaunchNativeTrimOnIdle;
    RhinoApp.Idle += _nativeTrimLaunchIdleHandler;

    return Result.Success;
  }

  private static void CancelPendingNativeTrimLaunch()
  {
    if (_nativeTrimLaunchIdleHandler != null)
    {
      RhinoApp.Idle -= _nativeTrimLaunchIdleHandler;
      _nativeTrimLaunchIdleHandler = null;
    }

    _pendingNativeTrimCutters = null;
    _pendingNativeTrimDocSerial = 0u;
  }

  private static void OnLaunchNativeTrimOnIdle(object? sender, EventArgs e)
  {
    if (_nativeTrimLaunchIdleHandler != null)
    {
      RhinoApp.Idle -= _nativeTrimLaunchIdleHandler;
      _nativeTrimLaunchIdleHandler = null;
    }

    var cutterIds = _pendingNativeTrimCutters;
    var docSerial = _pendingNativeTrimDocSerial;
    _pendingNativeTrimCutters = null;
    _pendingNativeTrimDocSerial = 0u;

    if (cutterIds == null || cutterIds.Length == 0)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != docSerial)
      return;

    doc.Objects.UnselectAll();

    var selectedCutters = new List<Guid>();
    foreach (var id in cutterIds)
    {
      if (id == Guid.Empty)
        continue;
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not Curve)
        continue;
      if (doc.Objects.Select(id))
        selectedCutters.Add(id);
    }

    doc.Views.Redraw();

    if (selectedCutters.Count == 0)
    {
      RhinoApp.WriteLine("vTrim: no valid cutting curves for native Trim.");
      return;
    }

    // _-Trim (scripted) auto-accepts pre-selected cutting curves without
    // requiring an extra Enter confirmation, then waits for target picks.
    _ = RhinoApp.RunScript("_-Trim", false);

    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    // Silently re-run vTrim (restart flag set so RunCommand returns immediately)
    // so that pressing Enter afterward repeats vTrim, not _-Trim.
    _restartingAfterTrimDelegate = true;
    _ = RhinoApp.RunScript("_vTrim", false);
    _restartingAfterTrimDelegate = false; // safety clear if RunScript didn't invoke us
  }

  private static void LoadPersistedOptions()
  {
    var loaded = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var extendAsLine = _extendAsLine;
        var joinAfterTrim = _joinAfterTrim;

        if (ToolsOptionStore.TryGetBool(section, ExtendAsLineKey, out var persistedExtendAsLine))
          extendAsLine = persistedExtendAsLine;

        if (ToolsOptionStore.TryGetBool(section, JoinAfterTrimKey, out var persistedJoinAfterTrim))
          joinAfterTrim = persistedJoinAfterTrim;

        return (extendAsLine, joinAfterTrim);
      });

    _extendAsLine = loaded.extendAsLine;
    _joinAfterTrim = loaded.joinAfterTrim;
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[ExtendAsLineKey] = _extendAsLine;
        section[JoinAfterTrimKey] = _joinAfterTrim;
      });
  }

  private enum PickerState
  {
    Ok,
    Done,
    Undo,
    Redo,
    Cancel
  }

  private sealed record CutterPick(PickerState State, bool AutoMode, List<Guid> CutterIds);

  private sealed class TargetPick
  {
    public PickerState State { get; set; }
    public RhinoObject? TargetObject { get; set; }
    public Curve? TargetCurve { get; set; }
    public Point3d PickPoint { get; set; } = Point3d.Unset;
    public bool ExtendMode { get; set; }
    public bool ExtendAsLine { get; set; }
    public bool JoinAfterTrim { get; set; }
    public bool HadValidPreview { get; set; }
    public TrimPlan? PreviewTrimPlan { get; set; }
    public ExtendPlan? PreviewExtendPlan { get; set; }
    public string PreviewFailure { get; set; } = string.Empty;
  }

  private static double PreviewCurveLength(TargetPick pick)
  {
    var curve = pick.ExtendMode
      ? pick.PreviewExtendPlan?.AddedPiece
      : pick.PreviewTrimPlan?.RemovedPiece;
    if (curve == null)
      return 0.0;

    try
    {
      return curve.GetLength();
    }
    catch
    {
      return 0.0;
    }
  }

  private static double TargetCurveLength(TargetPick pick)
  {
    try
    {
      return pick.TargetCurve?.GetLength() ?? 0.0;
    }
    catch
    {
      return 0.0;
    }
  }

  private static double PickPosition(TargetPick pick)
  {
    try
    {
      if (pick.TargetCurve == null ||
          !pick.TargetCurve.ClosestPoint(pick.PickPoint, out var parameter))
        return double.NaN;

      return pick.TargetCurve.Domain.NormalizedParameterAt(parameter);
    }
    catch
    {
      return double.NaN;
    }
  }

  private static string PreviewOutputLengths(TargetPick pick)
  {
    if (pick.ExtendMode)
    {
      var extendedCurve = pick.PreviewExtendPlan?.ExtendedCurve;
      if (extendedCurve == null)
        return "none";

      var length = TryGetCurveLength(extendedCurve);
      return length.HasValue ? length.Value.ToString("G17") : "none";
    }

    if (pick.PreviewTrimPlan == null)
      return "none";

    return string.Join(
      ",",
      pick.PreviewTrimPlan.Output.Select(curve =>
        (TryGetCurveLength(curve) ?? 0.0).ToString("G17")));
  }

  private sealed class CurveSnapshot
  {
    public CurveSnapshot(Guid objectId, Curve curve, ObjectAttributes attributes)
    {
      ObjectId = objectId;
      Curve = curve;
      Attributes = attributes;
    }

    public Guid ObjectId { get; }
    public Curve Curve { get; }
    public ObjectAttributes Attributes { get; }
  }

  private sealed class ActionRecord
  {
    public required CurveSnapshot BeforeTarget { get; init; }
    public required CurveSnapshot AfterTarget { get; init; }
    public List<CurveSnapshot> AddedCurves { get; } = new();
    public List<Guid> ActiveAddedIds { get; } = new();
  }

  private sealed class TrimPlan
  {
    public required Curve RemovedPiece { get; init; }
    public required List<Curve> Output { get; init; }
  }

  private sealed class ExtendPlan
  {
    public required Curve ExtendedCurve { get; init; }
    public required Curve AddedPiece { get; init; }
  }

  private sealed class SessionHistory
  {
    private readonly Stack<ActionRecord> _undo = new();
    private readonly Stack<ActionRecord> _redo = new();

    public void Push(ActionRecord record)
    {
      _undo.Push(record);
      _redo.Clear();
    }

    public bool TryPopUndo(out ActionRecord? record)
    {
      if (_undo.Count == 0)
      {
        record = null;
        return false;
      }

      record = _undo.Pop();
      return true;
    }

    public bool TryPopRedo(out ActionRecord? record)
    {
      if (_redo.Count == 0)
      {
        record = null;
        return false;
      }

      record = _redo.Pop();
      return true;
    }

    public void PushUndo(ActionRecord record)
    {
      _undo.Push(record);
    }

    public void PushRedo(ActionRecord record)
    {
      _redo.Push(record);
    }
  }

  private static bool TryUndo(RhinoDoc doc, SessionHistory history)
  {
    if (!history.TryPopUndo(out var record) || record == null)
      return false;

    if (!ApplyRecordState(doc, record, redo: false))
    {
      history.PushUndo(record);
      return false;
    }

    history.PushRedo(record);
    return true;
  }

  private static bool TryRedo(RhinoDoc doc, SessionHistory history)
  {
    if (!history.TryPopRedo(out var record) || record == null)
      return false;

    if (!ApplyRecordState(doc, record, redo: true))
    {
      history.PushRedo(record);
      return false;
    }

    history.PushUndo(record);
    return true;
  }

  private static bool ApplyRecordState(RhinoDoc doc, ActionRecord record, bool redo)
  {
    if (redo)
    {
      if (!RestoreTargetSnapshot(doc, record.AfterTarget))
        return false;

      record.ActiveAddedIds.Clear();
      foreach (var add in record.AddedCurves)
      {
        var id = doc.Objects.AddCurve(add.Curve.DuplicateCurve(), add.Attributes.Duplicate());
        if (id != Guid.Empty)
          record.ActiveAddedIds.Add(id);
      }

      return true;
    }

    foreach (var id in record.ActiveAddedIds)
    {
      if (id != Guid.Empty)
        doc.Objects.Delete(id, true);
    }

    record.ActiveAddedIds.Clear();
    return RestoreTargetSnapshot(doc, record.BeforeTarget);
  }

  private static bool RestoreTargetSnapshot(RhinoDoc doc, CurveSnapshot snapshot)
  {
    if (!doc.Objects.Replace(snapshot.ObjectId, snapshot.Curve.DuplicateCurve()))
      return false;

    _ = doc.Objects.ModifyAttributes(snapshot.ObjectId, snapshot.Attributes.Duplicate(), true);
    return true;
  }

  private static bool TryCaptureCurveSnapshot(RhinoDoc doc, Guid objectId, out CurveSnapshot? snapshot)
  {
    snapshot = null;
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not Curve curve)
      return false;

    snapshot = new CurveSnapshot(objectId, curve.DuplicateCurve(), obj.Attributes.Duplicate());
    return true;
  }

  private static ActionRecord? BuildActionRecord(RhinoDoc doc, CurveSnapshot beforeTarget, Guid targetId, IReadOnlyList<Guid>? addedIds)
  {
    if (!TryCaptureCurveSnapshot(doc, targetId, out var afterTarget) || afterTarget == null)
      return null;

    var record = new ActionRecord
    {
      BeforeTarget = beforeTarget,
      AfterTarget = afterTarget
    };

    if (addedIds == null)
      return record;

    foreach (var id in addedIds)
    {
      if (id == Guid.Empty)
        continue;

      if (!TryCaptureCurveSnapshot(doc, id, out var addSnap) || addSnap == null)
        continue;

      record.AddedCurves.Add(addSnap);
      record.ActiveAddedIds.Add(id);
    }

    return record;
  }

  private static CutterPick PickCutters(RhinoDoc doc)
  {
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select cutting curves or press Enter for AutoClosest");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = false;
    go.AcceptNothing(true);
    go.EnablePreSelect(false, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(true);
    go.DeselectAllBeforePostSelect = false;

    var result = go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success)
      return new CutterPick(PickerState.Cancel, true, new List<Guid>());

    if (result == GetResult.Nothing)
      return new CutterPick(PickerState.Ok, true, new List<Guid>());

    var cutterIds = new List<Guid>();
    var seen = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef == null)
        continue;

      var id = objRef.ObjectId;
      if (id == Guid.Empty || !seen.Add(id))
        continue;

      cutterIds.Add(id);
    }

    return new CutterPick(cutterIds.Count > 0 ? PickerState.Ok : PickerState.Cancel, false, cutterIds);
  }

  private static TargetPick PickTarget(
    RhinoDoc doc,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds,
    bool extendAsLine,
    bool joinAfterTrim,
    bool allowDone)
  {
    var pick = new TargetPick
    {
      State = PickerState.Cancel,
      ExtendAsLine = extendAsLine,
      JoinAfterTrim = joinAfterTrim
    };

    while (true)
    {
      var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = false;
      go.EnablePreSelect(false, true);
      go.AcceptNothing(true);
      go.AcceptString(true);
      go.DeselectAllBeforePostSelect = false;
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(true);
      go.EnableHighlight(false);

      var extendToggle = new OptionToggle(extendAsLine, "Smooth", "Line");
      var joinToggle = new OptionToggle(joinAfterTrim, "No", "Yes");
      go.AddOptionToggle("Extend", ref extendToggle);
      go.AddOptionToggle("Join", ref joinToggle);

      var preview = new TrimPreviewConduit(doc, autoMode, cutterIds, extendAsLine, joinAfterTrim)
      {
        Enabled = true
      };

      var hover = new TrimHoverMouseCallback(doc, preview)
      {
        Enabled = true
      };

      var lastShiftState = ShiftPressed();
      preview.HoverExtendMode = lastShiftState;
      var modeLocked = false;
      var lockedExtendMode = lastShiftState;
      var shiftReleaseTicks = 0;
      string? lastPrompt = null;

      void RefreshPrompt()
      {
        var modeLabel = modeLocked
          ? (lockedExtendMode ? "Extend" : "Trim")
          : (lastShiftState ? "Extend" : "Trim");

        var prompt = modeLocked
          ? $"Select curve to trim; mode locked: {modeLabel}; Enter when done"
          : $"Select curve to trim (hold Shift to extend; current: {modeLabel}); Enter when done";

        if (string.Equals(prompt, lastPrompt, StringComparison.Ordinal))
          return;

        go.SetCommandPrompt(prompt);
        lastPrompt = prompt;
      }

      RefreshPrompt();

      System.Windows.Forms.Timer? shiftTimer = null;

      void OnIdleShiftRefresh(object? _sender, EventArgs _args)
      {
        var clickCapture = hover.LastClick;
        if (clickCapture.HasCapture && !modeLocked)
        {
          modeLocked = true;
          lockedExtendMode = clickCapture.ExtendMode;
          lastShiftState = lockedExtendMode;
          preview.HoverExtendMode = lockedExtendMode;
          RefreshPrompt();
          doc.Views.Redraw();
          return;
        }

        if (modeLocked)
        {
          if (preview.HoverExtendMode != lockedExtendMode)
          {
            preview.HoverExtendMode = lockedExtendMode;
            doc.Views.Redraw();
          }

          return;
        }

        var currentShift = ShiftPressed();
        if (currentShift)
        {
          shiftReleaseTicks = 0;
          if (lastShiftState)
            return;

          lastShiftState = true;
          preview.HoverExtendMode = true;
          RefreshPrompt();
          doc.Views.Redraw();
        }
        else
        {
          if (!lastShiftState) { shiftReleaseTicks = 0; return; }

          // Debounce: require 2 consecutive shift-up reads (~60 ms) before flipping
          // to trim, so single-frame ShiftPressed() glitches don't cause twitching.
          shiftReleaseTicks++;
          if (shiftReleaseTicks < 2)
            return;

          shiftReleaseTicks = 0;
          lastShiftState = false;
          preview.HoverExtendMode = false;
          RefreshPrompt();
          doc.Views.Redraw();
        }
      }

      RhinoApp.Idle += OnIdleShiftRefresh;

      try
      {
        shiftTimer = new System.Windows.Forms.Timer
        {
          Interval = 30
        };
        shiftTimer.Tick += OnIdleShiftRefresh;
        shiftTimer.Start();
      }
      catch
      {
        shiftTimer = null;
      }

      doc.Views.Redraw();

      GetResult result;
      try
      {
        result = go.Get();
      }
      finally
      {
        if (shiftTimer != null)
        {
          try
          {
            shiftTimer.Stop();
          }
          catch
          {
          }

          try
          {
            shiftTimer.Tick -= OnIdleShiftRefresh;
          }
          catch
          {
          }

          try
          {
            shiftTimer.Dispose();
          }
          catch
          {
          }
        }

        RhinoApp.Idle -= OnIdleShiftRefresh;
        doc.Objects.UnselectAll();
        hover.Enabled = false;
        preview.Enabled = false;
        doc.Views.Redraw();
      }

      if (go.CommandResult() != Result.Success)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      if (result == GetResult.Option)
      {
        extendAsLine = extendToggle.CurrentValue;
        joinAfterTrim = joinToggle.CurrentValue;
        pick.ExtendAsLine = extendAsLine;
        pick.JoinAfterTrim = joinAfterTrim;
        continue;
      }

      if (result == GetResult.String)
      {
        var text = (go.StringResult() ?? string.Empty).Trim().ToLowerInvariant();
        while (text.StartsWith("_", StringComparison.Ordinal) || text.StartsWith("-", StringComparison.Ordinal))
          text = text[1..];

        if (text is "u" or "undo")
        {
          pick.State = PickerState.Undo;
          pick.ExtendAsLine = extendAsLine;
          pick.JoinAfterTrim = joinAfterTrim;
          return pick;
        }

        if (text is "r" or "redo")
        {
          pick.State = PickerState.Redo;
          pick.ExtendAsLine = extendAsLine;
          pick.JoinAfterTrim = joinAfterTrim;
          return pick;
        }

        RhinoApp.WriteLine("vTrim: unknown hidden keyword. Use 'u'/'undo' or 'r'/'redo'.");
        continue;
      }

      if (result == GetResult.Nothing)
      {
        if (!allowDone)
          continue;

        pick.State = PickerState.Done;
        pick.ExtendAsLine = extendAsLine;
        pick.JoinAfterTrim = joinAfterTrim;
        return pick;
      }

      if (result != GetResult.Object || go.ObjectCount == 0)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      var objRef = go.Object(0);
      if (objRef == null)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      var targetObj = objRef.Object();
      var targetCurve = objRef.Curve();
      if (targetObj == null || targetCurve == null)
      {
        pick.State = PickerState.Cancel;
        return pick;
      }

      var clickHover = hover.LastClick;
      if (clickHover.HasCapture && clickHover.ObjectId.HasValue)
      {
        var capturedObject = doc.Objects.FindId(clickHover.ObjectId.Value);
        if (capturedObject?.Geometry is Curve capturedCurve)
        {
          if (capturedObject.Id != targetObj.Id)
            Log.Write(
              "vTrim",
              "using highlighted click target={0} instead of native pick={1}",
              capturedObject.Id,
              targetObj.Id);
          targetObj = capturedObject;
          targetCurve = capturedCurve;
        }
      }

      var pickPoint = objRef.SelectionPoint();
      if (!pickPoint.IsValid)
      {
        if (targetCurve.GetLength() > RhinoMath.ZeroTolerance && targetCurve.LengthParameter(0.5 * targetCurve.GetLength(), out var tMid))
          pickPoint = targetCurve.PointAt(tMid);
        else
          pickPoint = targetCurve.PointAtStart;
      }

      var pickedExtendMode = clickHover.HasCapture
        ? clickHover.ExtendMode
        : preview.HoverExtendMode;
      var previewMatchesTarget = clickHover.HasCapture &&
                                 clickHover.ObjectId.HasValue &&
                                 clickHover.ObjectId.Value == targetObj.Id;

      if (previewMatchesTarget && clickHover.Point.IsValid)
        pickPoint = clickHover.Point;

      var trimPlan = previewMatchesTarget ? clickHover.TrimPlan : null;
      var extendPlan = previewMatchesTarget ? clickHover.ExtendPlan : null;
      var previewFailure = previewMatchesTarget
        ? clickHover.PreviewFailure ?? string.Empty
        : string.Empty;

      if ((pickedExtendMode && extendPlan == null) || (!pickedExtendMode && trimPlan == null))
      {
        preview.SetHover(targetObj, targetCurve, pickPoint, pickedExtendMode);
        trimPlan = pickedExtendMode ? null : preview.CurrentTrimPlan;
        extendPlan = pickedExtendMode ? preview.CurrentExtendPlan : null;
        previewFailure = preview.CurrentPreviewFailure;
      }

      pick.State = PickerState.Ok;
      pick.TargetObject = targetObj;
      pick.TargetCurve = targetCurve;
      pick.PickPoint = pickPoint;
      pick.ExtendMode = pickedExtendMode;
      pick.ExtendAsLine = extendAsLine;
      pick.JoinAfterTrim = joinAfterTrim;
      pick.HadValidPreview = trimPlan != null || extendPlan != null;
      pick.PreviewTrimPlan = trimPlan;
      pick.PreviewExtendPlan = extendPlan;
      pick.PreviewFailure = previewFailure;
      return pick;
    }
  }

  private static bool ShiftPressed()
  {
    try
    {
      return (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Shift) == System.Windows.Forms.Keys.Shift;
    }
    catch
    {
      return false;
    }
  }

  private readonly struct HoverClickCapture
  {
    public bool HasCapture { get; init; }
    public Guid? ObjectId { get; init; }
    public Point3d Point { get; init; }
    public bool ExtendMode { get; init; }
    public bool HadValidPreview { get; init; }
    public TrimPlan? TrimPlan { get; init; }
    public ExtendPlan? ExtendPlan { get; init; }
    public string? PreviewFailure { get; init; }
  }

  private sealed class TrimPreviewConduit : Rhino.Display.DisplayConduit
  {
    private readonly RhinoDoc _doc;
    private readonly bool _autoMode;
    private readonly IReadOnlyList<Guid> _cutterIds;

    public TrimPreviewConduit(RhinoDoc doc, bool autoMode, IReadOnlyList<Guid> cutterIds, bool extendAsLine, bool joinAfterTrim)
    {
      _doc = doc;
      _autoMode = autoMode;
      _cutterIds = cutterIds;
      ExtendAsLine = extendAsLine;
      JoinAfterTrim = joinAfterTrim;
    }

    public Guid? HoverObjectId { get; private set; }
    public RhinoObject? HoverObject { get; private set; }
    public Curve? HoverCurve { get; private set; }
    public Point3d HoverPoint { get; private set; } = Point3d.Unset;
    public bool HoverExtendMode { get; set; }
    public bool ExtendAsLine { get; set; }
    public bool JoinAfterTrim { get; set; }
    public bool HasValidActionPreview { get; private set; }
    public TrimPlan? CurrentTrimPlan { get; private set; }
    public ExtendPlan? CurrentExtendPlan { get; private set; }
    public string CurrentPreviewFailure { get; private set; } = string.Empty;
    private Guid? _lastValidObjectId;
    private Point3d _lastValidPoint = Point3d.Unset;
    private bool _lastValidExtendMode;
    private TrimPlan? _lastValidTrimPlan;
    private ExtendPlan? _lastValidExtendPlan;

    public void SetHover(RhinoObject? obj, Curve? curve, Point3d point, bool extendMode)
    {
      HasValidActionPreview = false;
      CurrentTrimPlan = null;
      CurrentExtendPlan = null;
      CurrentPreviewFailure = string.Empty;
      HoverObject = obj;
      HoverObjectId = obj?.Id;
      HoverCurve = curve;
      HoverPoint = point;
      HoverExtendMode = extendMode;
      ResolveCurrentAction(extendMode);
    }

    public void ResolveCurrentAction(bool extendMode)
    {
      HoverExtendMode = extendMode;
      HasValidActionPreview = false;
      CurrentTrimPlan = null;
      CurrentExtendPlan = null;
      CurrentPreviewFailure = string.Empty;

      if (HoverObject == null || HoverCurve == null || !HoverPoint.IsValid)
      {
        CurrentPreviewFailure = "hover target is unavailable";
        return;
      }

      if (HoverExtendMode)
      {
        if (TryBuildExtendPlan(
              _doc,
              HoverObject,
              HoverCurve,
              HoverPoint,
              ExtendAsLine,
              _autoMode,
              _cutterIds,
              out var extendPlan,
              out var extendFailure) && extendPlan != null)
        {
          HasValidActionPreview = true;
          CurrentExtendPlan = extendPlan;
          CacheCurrentAction();
        }
        else
        {
          CurrentPreviewFailure = extendFailure;
          RestoreCachedActionIfApplicable();
        }

        return;
      }

      var cutters = ResolveCuttersForTarget(
        _doc,
        HoverObject,
        HoverCurve,
        HoverPoint,
        _autoMode,
        _cutterIds);
      if (TryBuildTrimPlan(
            _doc,
            HoverCurve,
            HoverPoint,
            cutters,
            allowViewProjection: !_autoMode,
            allowBoundaryExtend: false,
            extendAsLine: ExtendAsLine,
            joinAfterTrim: JoinAfterTrim,
            out var trimPlan,
            out var trimFailure) && trimPlan != null)
      {
        HasValidActionPreview = true;
        CurrentTrimPlan = trimPlan;
        CacheCurrentAction();
      }
      else
      {
        CurrentPreviewFailure = trimFailure;
        RestoreCachedActionIfApplicable();
      }
    }

    private void CacheCurrentAction()
    {
      _lastValidObjectId = HoverObjectId;
      _lastValidPoint = HoverPoint;
      _lastValidExtendMode = HoverExtendMode;
      _lastValidTrimPlan = CurrentTrimPlan;
      _lastValidExtendPlan = CurrentExtendPlan;
    }

    private void RestoreCachedActionIfApplicable()
    {
      if (!HoverObjectId.HasValue ||
          HoverObjectId != _lastValidObjectId ||
          HoverCurve == null ||
          !HoverPoint.IsValid ||
          !_lastValidPoint.IsValid ||
          HoverExtendMode != _lastValidExtendMode)
        return;

      if (HoverExtendMode)
      {
        if (_lastValidExtendPlan == null ||
            !TryGetExtendAnchor(HoverCurve, HoverPoint, out var currentEnd, out _) ||
            !TryGetExtendAnchor(HoverCurve, _lastValidPoint, out var cachedEnd, out _) ||
            currentEnd != cachedEnd)
          return;

        CurrentExtendPlan = _lastValidExtendPlan;
      }
      else
      {
        if (_lastValidTrimPlan == null)
          return;

        var retainTolerance = Math.Max(
          _doc.ModelAbsoluteTolerance * 10.0,
          RhinoMath.ZeroTolerance * 100.0);
        if (DistanceToCurve(_lastValidTrimPlan.RemovedPiece, HoverPoint) > retainTolerance)
          return;

        CurrentTrimPlan = _lastValidTrimPlan;
      }

      HasValidActionPreview = true;
      CurrentPreviewFailure = string.Empty;
    }

    protected override void DrawOverlay(Rhino.Display.DrawEventArgs e)
    {
      // Draw explicit cutter curves highlighted so the user can see them
      // even after they are deselected.
      if (!_autoMode && _cutterIds.Count > 0)
      {
        var cutterColor = Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
        foreach (var id in _cutterIds)
        {
          if (id == Guid.Empty)
            continue;
          var obj = _doc.Objects.FindId(id);
          if (obj?.Geometry is Curve cutterCurve)
            PreviewDisplay.DrawCurve(e.Display, cutterCurve, cutterColor, 1);
        }
      }

      if (HoverObject == null || HoverCurve == null || !HoverPoint.IsValid)
        return;

      ResolveCurrentAction(HoverExtendMode);

      if (HoverExtendMode)
      {
        if (CurrentExtendPlan != null)
        {
          PreviewDisplay.DrawAddedCurve(e.Display, CurrentExtendPlan.AddedPiece);
          if (NeedsEndpointCue(e.Viewport, e.Display, CurrentExtendPlan.AddedPiece))
            PreviewDisplay.DrawAddedPoint(e.Display, ExtensionEndpoint(CurrentExtendPlan.AddedPiece));
        }
      }
      else
      {
        if (CurrentTrimPlan != null)
        {
          PreviewDisplay.DrawRemovedCurve(e.Display, CurrentTrimPlan.RemovedPiece);
          if (NeedsEndpointCue(e.Viewport, e.Display, CurrentTrimPlan.RemovedPiece))
            PreviewDisplay.DrawRemovedPoint(e.Display, CurveMidpoint(CurrentTrimPlan.RemovedPiece));
        }
      }
    }

    private static bool NeedsEndpointCue(
      Rhino.Display.RhinoViewport viewport,
      Rhino.Display.DisplayPipeline display,
      Curve curve)
    {
      var screenStart = viewport.WorldToClient(curve.PointAtStart);
      var screenEnd = viewport.WorldToClient(curve.PointAtEnd);
      var dx = screenEnd.X - screenStart.X;
      var dy = screenEnd.Y - screenStart.Y;
      var screenLength = Math.Sqrt((dx * dx) + (dy * dy));
      var minimumVisibleLength = Math.Max(6.0, PreviewDisplay.Thickness(display, 2) * 2.0);
      return screenLength < minimumVisibleLength;
    }

    private Point3d ExtensionEndpoint(Curve addedPiece)
    {
      if (HoverCurve == null)
        return addedPiece.PointAtEnd;

      var startDistance = DistanceToCurve(HoverCurve, addedPiece.PointAtStart);
      var endDistance = DistanceToCurve(HoverCurve, addedPiece.PointAtEnd);
      return startDistance > endDistance
        ? addedPiece.PointAtStart
        : addedPiece.PointAtEnd;
    }

    private static double DistanceToCurve(Curve curve, Point3d point)
    {
      return curve.ClosestPoint(point, out var parameter)
        ? curve.PointAt(parameter).DistanceTo(point)
        : double.PositiveInfinity;
    }

    private static Point3d CurveMidpoint(Curve curve)
    {
      try
      {
        var length = curve.GetLength();
        if (length > RhinoMath.ZeroTolerance &&
            curve.LengthParameter(length * 0.5, out var parameter))
          return curve.PointAt(parameter);
      }
      catch
      {
      }

      return curve.PointAt(curve.Domain.ParameterAt(0.5));
    }
  }

  private sealed class TrimHoverMouseCallback : MouseCallback
  {
    private readonly RhinoDoc _doc;
    private readonly TrimPreviewConduit _preview;
    private RhinoObject? _lastHoverObject;
    private Curve? _lastHoverCurve;
    private Guid? _lastHoverObjectId;
    private Point3d _lastHoverPoint = Point3d.Unset;

    public TrimHoverMouseCallback(RhinoDoc doc, TrimPreviewConduit preview)
    {
      _doc = doc;
      _preview = preview;
    }

    public HoverClickCapture LastClick { get; private set; }

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
      // Only capture left-click; middle/right are pan/zoom and must not trigger modeLocked.
      try { if (e.MouseButton != Rhino.UI.MouseButton.Left) { base.OnMouseDown(e); return; } } catch { }

      var shiftDown = ShiftPressed();
      try
      {
        shiftDown = shiftDown || e.ShiftKeyDown;
      }
      catch
      {
      }

      if (_lastHoverObjectId.HasValue && _lastHoverPoint.IsValid)
      {
        _preview.SetHover(_lastHoverObject, _lastHoverCurve, _lastHoverPoint, shiftDown);
      }
      else
      {
        _preview.ResolveCurrentAction(shiftDown);
      }

      var trimPlan = shiftDown ? null : _preview.CurrentTrimPlan;
      var extendPlan = shiftDown ? _preview.CurrentExtendPlan : null;

      LastClick = new HoverClickCapture
      {
        HasCapture = true,
        ObjectId = _lastHoverObjectId,
        Point = _lastHoverPoint,
        ExtendMode = shiftDown,
        HadValidPreview = trimPlan != null || extendPlan != null,
        TrimPlan = trimPlan,
        ExtendPlan = extendPlan,
        PreviewFailure = _preview.CurrentPreviewFailure
      };
      base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      var view = e.View;
      var viewport = view?.ActiveViewport;
      var vpClient = e.ViewportPoint;
      if (viewport == null)
      {
        base.OnMouseMove(e);
        return;
      }

      var screenPoint = new System.Drawing.Point(
        (int)Math.Round((double)vpClient.X),
        (int)Math.Round((double)vpClient.Y));
      TryPickCurveWithRhino(
        _doc,
        viewport,
        screenPoint,
        out var hoverObj,
        out var hoverCurve,
        out var hoverPoint);

      if (_lastHoverObject != null && _lastHoverCurve != null)
      {
        var viewportPoint = new Point2d(screenPoint.X, screenPoint.Y);
        var previousSample = CurveBestScreenPick(_lastHoverCurve, viewport, viewportPoint);
        var retainRadius = PickboxRadiusPixels();
        var previousInRange = previousSample.Point.IsValid &&
                              previousSample.DistanceSquared.HasValue &&
                              previousSample.DistanceSquared.Value <= retainRadius * retainRadius;
        var pickedDistance = hoverPoint.IsValid
          ? PixelDistanceSquared(viewport, viewportPoint, hoverPoint)
          : null;
        var previousIsPreferred = previousInRange &&
          (hoverObj == null ||
           hoverObj.Id == _lastHoverObject.Id ||
           !pickedDistance.HasValue ||
           previousSample.DistanceSquared!.Value <= pickedDistance.Value + 4.0);

        if (previousIsPreferred)
        {
          hoverObj = _lastHoverObject;
          hoverCurve = _lastHoverCurve;
          hoverPoint = previousSample.Point;
        }
      }

      // Skip redraw when still nothing is hovered — no visual state changed.
      if (hoverObj == null && _lastHoverObjectId == null)
      {
        base.OnMouseMove(e);
        return;
      }

      _lastHoverObjectId = hoverObj?.Id;
      _lastHoverObject = hoverObj;
      _lastHoverCurve = hoverCurve;
      _lastHoverPoint = hoverPoint;

      // HoverExtendMode is owned by the shift timer — do not overwrite it here.
      _preview.SetHover(hoverObj, hoverCurve, hoverPoint, _preview.HoverExtendMode);
      _doc.Views.Redraw();

      base.OnMouseMove(e);
    }

    private static bool TryPickCurveWithRhino(
      RhinoDoc doc,
      Rhino.Display.RhinoViewport viewport,
      System.Drawing.Point screenPoint,
      out RhinoObject? pickedObject,
      out Curve? pickedCurve,
      out Point3d pickedPoint)
    {
      pickedObject = null;
      pickedCurve = null;
      pickedPoint = Point3d.Unset;

      var view = viewport.ParentView;
      if (view == null ||
          !viewport.GetFrustumLine(screenPoint.X, screenPoint.Y, out var pickLine))
        return false;

      using var pickContext = new PickContext
      {
        View = view,
        PickLine = pickLine,
        PickStyle = PickStyle.PointPick,
        PickMode = PickMode.Wireframe,
        PickGroupsEnabled = false,
        SubObjectSelectionEnabled = false
      };
      pickContext.SetPickTransform(viewport.GetPickTransform(screenPoint));
      pickContext.UpdateClippingPlanes();

      var picked = doc.Objects.PickObjects(pickContext);
      if (picked == null)
        return false;

      foreach (var objRef in picked)
      {
        var obj = objRef.Object();
        var curve = objRef.Curve();
        if (obj == null || curve == null || obj.Geometry is not Curve)
          continue;

        var point = Point3d.Unset;
        try
        {
          using var nurbs = curve.ToNurbsCurve();
          if (nurbs != null &&
              pickContext.PickFrustumTest(
                nurbs,
                out var parameter,
                out _,
                out _))
          {
            point = nurbs.PointAt(parameter);
          }
        }
        catch
        {
        }

        if (!point.IsValid)
          point = objRef.SelectionPoint();
        if (!point.IsValid)
        {
          var fallback = CurveBestScreenPick(
            curve,
            viewport,
            new Point2d(screenPoint.X, screenPoint.Y));
          point = fallback.Point;
        }

        if (!point.IsValid)
          continue;

        pickedObject = obj;
        pickedCurve = curve;
        pickedPoint = point;
        return true;
      }

      return false;
    }

    private static double PickboxRadiusPixels()
    {
      try
      {
        return Math.Max(6.0, Rhino.ApplicationSettings.ModelAidSettings.MousePickboxRadius + 2.0);
      }
      catch
      {
        return 6.0;
      }
    }
  }

  private readonly struct ScreenPickSample
  {
    public Point3d Point { get; init; }
    public double? DistanceSquared { get; init; }
  }

  private static ScreenPickSample CurveBestScreenPick(Curve curve, Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint)
  {
    var lineSample = LineBestScreenPick(curve, viewport, vpPoint);
    if (lineSample.Point.IsValid && lineSample.DistanceSquared.HasValue)
      return lineSample;

    if (!TryGetDomain(curve, out var d0, out var d1) || Math.Abs(d1 - d0) <= 1.0e-12)
      return default;

    var parameters = new List<double>();
    try
    {
      var div = curve.DivideByCount(32, true);
      if (div != null)
        parameters.AddRange(div.Select(v => (double)v));
    }
    catch
    {
    }

    if (parameters.Count == 0)
    {
      for (var i = 0; i <= 32; i++)
      {
        var t = d0 + ((d1 - d0) * (i / 32.0));
        parameters.Add(t);
      }
    }

    var bestT = d0;
    double? bestD2 = null;

    foreach (var t in parameters)
    {
      var pt = curve.PointAt(t);
      var d2 = PixelDistanceSquared(viewport, vpPoint, pt);
      if (!d2.HasValue)
        continue;

      if (!bestD2.HasValue || d2.Value < bestD2.Value)
      {
        bestD2 = d2.Value;
        bestT = t;
      }
    }

    if (!bestD2.HasValue)
      return default;

    var idx = parameters.FindIndex(v => Math.Abs(v - bestT) <= 1.0e-15);
    if (idx < 0)
      idx = 0;

    var left = parameters[Math.Max(0, idx - 1)];
    var right = parameters[Math.Min(parameters.Count - 1, idx + 1)];
    if (right <= left)
    {
      left = Math.Max(d0, bestT - ((d1 - d0) / 96.0));
      right = Math.Min(d1, bestT + ((d1 - d0) / 96.0));
    }

    for (var i = 0; i < 8; i++)
    {
      var t1 = left + ((right - left) / 3.0);
      var t2 = right - ((right - left) / 3.0);

      var p1 = curve.PointAt(t1);
      var p2 = curve.PointAt(t2);
      var d21 = PixelDistanceSquared(viewport, vpPoint, p1);
      var d22 = PixelDistanceSquared(viewport, vpPoint, p2);
      if (!d21.HasValue || !d22.HasValue)
        break;

      if (d21.Value <= d22.Value)
      {
        right = t2;
        if (d21.Value < bestD2.Value)
        {
          bestD2 = d21.Value;
          bestT = t1;
        }
      }
      else
      {
        left = t1;
        if (d22.Value < bestD2.Value)
        {
          bestD2 = d22.Value;
          bestT = t2;
        }
      }
    }

    return new ScreenPickSample
    {
      Point = curve.PointAt(bestT),
      DistanceSquared = bestD2
    };
  }

  private static ScreenPickSample LineBestScreenPick(Curve curve, Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint)
  {
    var (start, end) = LineEndpoints(curve);
    if (!start.HasValue || !end.HasValue)
      return default;

    RhinoPoint2d c0;
    RhinoPoint2d c1;
    try
    {
      c0 = viewport.WorldToClient(start.Value);
      c1 = viewport.WorldToClient(end.Value);
    }
    catch
    {
      return default;
    }

    var x0 = (double)c0.X;
    var y0 = (double)c0.Y;
    var x1 = (double)c1.X;
    var y1 = (double)c1.Y;
    var px = vpPoint.X;
    var py = vpPoint.Y;

    var vx = x1 - x0;
    var vy = y1 - y0;
    var denom = (vx * vx) + (vy * vy);
    var t = denom <= 1.0e-12
      ? 0.0
      : Math.Max(0.0, Math.Min(1.0, (((px - x0) * vx) + ((py - y0) * vy)) / denom));

    var cx = x0 + (vx * t);
    var cy = y0 + (vy * t);
    var dx = cx - px;
    var dy = cy - py;
    var d2 = (dx * dx) + (dy * dy);

    var bestPoint = start.Value + ((end.Value - start.Value) * t);
    return new ScreenPickSample
    {
      Point = bestPoint,
      DistanceSquared = d2
    };
  }

  /// <summary>
  /// Returns true if the curve's world bounding box projects to a screen region
  /// that overlaps the cursor position within <paramref name="marginPx"/> pixels.
  /// Conservative: passes curves that cannot be cheaply ruled out.
  /// </summary>
  private static bool CurveBboxNearCursor(Curve curve, Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint, double marginPx)
  {
    BoundingBox bbox;
    try { bbox = curve.GetBoundingBox(false); }
    catch { return true; }
    if (!bbox.IsValid)
      return true;

    var minX = double.MaxValue;
    var minY = double.MaxValue;
    var maxX = double.MinValue;
    var maxY = double.MinValue;

    foreach (var corner in bbox.GetCorners())
    {
      try
      {
        var sc = viewport.WorldToClient(corner);
        if (sc.X < minX) minX = sc.X;
        if (sc.Y < minY) minY = sc.Y;
        if (sc.X > maxX) maxX = sc.X;
        if (sc.Y > maxY) maxY = sc.Y;
      }
      catch { return true; }
    }

    return vpPoint.X >= minX - marginPx && vpPoint.X <= maxX + marginPx
        && vpPoint.Y >= minY - marginPx && vpPoint.Y <= maxY + marginPx;
  }

  private static (Point3d? Start, Point3d? End) LineEndpoints(Curve curve)
  {
    if (curve is LineCurve lineCurve)
    {
      var line = lineCurve.Line;
      return (line.From, line.To);
    }

    try
    {
      if (curve.IsLinear(RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1.0e-6))
        return (curve.PointAtStart, curve.PointAtEnd);
    }
    catch
    {
    }

    return (null, null);
  }

  private static double? PixelDistanceSquared(Rhino.Display.RhinoViewport viewport, RhinoPoint2d vpPoint, Point3d worldPoint)
  {
    try
    {
      var client = viewport.WorldToClient(worldPoint);
      var dx = client.X - vpPoint.X;
      var dy = client.Y - vpPoint.Y;
      return (dx * dx) + (dy * dy);
    }
    catch
    {
      return null;
    }
  }

  private static IEnumerable<(RhinoObject Obj, Curve Curve)> EnumerateDocCurves(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj == null || obj.ObjectType != ObjectType.Curve)
        continue;

      if (obj.Geometry is not Curve curve)
        continue;

      yield return (obj, curve);
    }
  }

  private static List<Curve> ResolveCuttersForTarget(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    Point3d pickPoint,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds)
  {
    if (autoMode)
      return FindAutoCutters(doc, targetObj.Id, targetCurve, pickPoint);

    var cutters = new List<Curve>();
    foreach (var id in cutterIds)
    {
      if (id == Guid.Empty || id == targetObj.Id)
        continue;

      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not Curve curve)
        continue;

      cutters.Add(curve);
    }

    return cutters;
  }

  private static List<double> UniqueParams(IEnumerable<double> values, double tolerance)
  {
    var sorted = values.OrderBy(v => v).ToList();
    var unique = new List<double>();
    foreach (var v in sorted)
    {
      if (unique.Count == 0 || Math.Abs(v - unique[^1]) > tolerance)
        unique.Add(v);
    }

    return unique;
  }

  private static Plane? ActiveViewPlane(RhinoDoc doc)
  {
    try
    {
      var viewport = doc.Views.ActiveView?.ActiveViewport;
      if (viewport == null)
        return null;

      var x = viewport.CameraX;
      var y = viewport.CameraY;
      if (x.IsTiny() || y.IsTiny() || !x.Unitize() || !y.Unitize())
        return null;

      var plane = new Plane(viewport.CameraLocation, x, y);
      return plane.IsValid ? plane : null;
    }
    catch
    {
      return null;
    }
  }

  private static Curve? ProjectCurveToPlane(Curve curve, Plane? plane)
  {
    if (plane == null)
      return null;

    try
    {
      return Curve.ProjectToPlane(curve, plane.Value);
    }
    catch
    {
      return null;
    }
  }

  private static bool TryGetDomain(Curve curve, out double d0, out double d1)
  {
    d0 = 0.0;
    d1 = 0.0;

    try
    {
      var domain = curve.Domain;
      d0 = domain.T0;
      d1 = domain.T1;
      return true;
    }
    catch
    {
      return false;
    }
  }

  private static double StrictCurveContactTolerance(RhinoDoc doc, Curve curveA, Curve curveB)
  {
    var documentTolerance = Math.Max(RhinoMath.ZeroTolerance, doc.ModelAbsoluteTolerance);
    var shortestLength = double.PositiveInfinity;

    foreach (var curve in new[] { curveA, curveB })
    {
      try
      {
        var length = curve.GetLength();
        if (double.IsFinite(length) && length > RhinoMath.ZeroTolerance)
          shortestLength = Math.Min(shortestLength, length);
      }
      catch
      {
      }
    }

    if (!double.IsFinite(shortestLength))
      return documentTolerance;

    var scaleTolerance = Math.Max(RhinoMath.ZeroTolerance, shortestLength * 1.0e-6);
    return Math.Min(documentTolerance, scaleTolerance);
  }

  private static List<double> CollectInteriorCurveCurveParams(
    Curve curveA,
    Curve curveB,
    double d0,
    double d1,
    double endTol,
    RhinoDoc doc,
    bool includeEndpoints = false,
    double? intersectionTolerance = null)
  {
    var result = new List<double>();
    var tolerance = Math.Max(
      RhinoMath.ZeroTolerance,
      intersectionTolerance ?? doc.ModelAbsoluteTolerance);
    var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(
      curveA,
      curveB,
      tolerance,
      tolerance);
    if (events == null)
      return result;

    var isClosed = curveA.IsClosed;
    var endpointTolerance = tolerance;

    void AddParameter(double parameter)
    {
      if (isClosed)
      {
        if (parameter < d0 - endTol || parameter > d1 + endTol)
          return;

        result.Add(parameter >= d1 - endTol ? d0 : Math.Max(d0, parameter));
        return;
      }

      if (includeEndpoints)
      {
        if (parameter >= d0 - endTol && parameter <= d1 + endTol)
          result.Add(Math.Max(d0, Math.Min(d1, parameter)));
        return;
      }

      Point3d point;
      try
      {
        point = curveA.PointAt(parameter);
      }
      catch
      {
        return;
      }

      if (point.DistanceTo(curveA.PointAtStart) <= endpointTolerance ||
          point.DistanceTo(curveA.PointAtEnd) <= endpointTolerance)
        return;

      if (parameter > d0 + endTol && parameter < d1 - endTol)
        result.Add(parameter);
    }

    foreach (var ev in events)
    {
      if (ev.IsPoint)
      {
        AddParameter(ev.ParameterA);
      }
      else if (ev.IsOverlap)
      {
        AddParameter(ev.OverlapA.T0);
        AddParameter(ev.OverlapA.T1);
      }
    }

    return result;
  }

  private static double? NearestPickDistanceForParams(Curve targetCurve, Point3d pickPoint, IEnumerable<double> parameters)
  {
    double? best = null;
    foreach (var t in parameters)
    {
      try
      {
        var pt = targetCurve.PointAt(t);
        var d = pt.DistanceTo(pickPoint);
        if (!best.HasValue || d < best.Value)
          best = d;
      }
      catch
      {
      }
    }

    return best;
  }

  private static List<double> ParamsClosestSource(
    RhinoDoc doc,
    Curve targetCurve,
    Curve cutterCurve,
    Point3d pickPoint,
    double d0,
    double d1,
    double endTol,
    Plane? viewPlane)
  {
    var worldParams = CollectInteriorCurveCurveParams(targetCurve, cutterCurve, d0, d1, endTol, doc);

    var viewParams = new List<double>();
    var targetProj = ProjectCurveToPlane(targetCurve, viewPlane);
    var cutterProj = ProjectCurveToPlane(cutterCurve, viewPlane);
    if (targetProj != null && cutterProj != null && TryGetDomain(targetProj, out var p0, out var p1))
    {
      var projEndTol = Math.Max(1.0e-9, Math.Abs(p1 - p0) * 1.0e-9);
      var projRaw = CollectInteriorCurveCurveParams(targetProj, cutterProj, p0, p1, projEndTol, doc);

      foreach (var tp in projRaw)
      {
        double tOrig;
        if (Math.Abs(p1 - p0) <= 1.0e-12)
        {
          tOrig = d0;
        }
        else
        {
          var s = (tp - p0) / (p1 - p0);
          s = Math.Max(0.0, Math.Min(1.0, s));
          tOrig = d0 + ((d1 - d0) * s);
        }

        if (targetCurve.IsClosed)
        {
          if (tOrig >= d0 - endTol && tOrig <= d1 + endTol)
            viewParams.Add(tOrig >= d1 - endTol ? d0 : Math.Max(d0, tOrig));
        }
        else if (tOrig > d0 + endTol && tOrig < d1 - endTol)
        {
          viewParams.Add(tOrig);
        }
      }
    }

    if (worldParams.Count > 0 && viewParams.Count > 0)
    {
      var isLinear = false;
      try
      {
        isLinear = targetCurve.IsLinear(doc.ModelAbsoluteTolerance);
      }
      catch
      {
      }

      if (isLinear)
      {
        var merged = worldParams.Concat(viewParams);
        return UniqueParams(merged, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
      }

      var worldD = NearestPickDistanceForParams(targetCurve, pickPoint, worldParams);
      var viewD = NearestPickDistanceForParams(targetCurve, pickPoint, viewParams);
      if (!worldD.HasValue)
        return viewParams;
      if (!viewD.HasValue)
        return worldParams;
      return worldD.Value <= viewD.Value ? worldParams : viewParams;
    }

    if (worldParams.Count > 0)
      return worldParams;
    return viewParams;
  }

  private static List<double> CollectSplitParameters(
    RhinoDoc doc,
    Curve targetCurve,
    IReadOnlyList<Curve> cutterCurves,
    Point3d pickPoint,
    bool allowViewProjection)
  {
    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return new List<double>();

    var endTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-9);
    var viewPlane = allowViewProjection ? ActiveViewPlane(doc) : null;

    var parameters = new List<double>();
    foreach (var cutter in cutterCurves)
    {
      parameters.AddRange(allowViewProjection
        ? ParamsClosestSource(doc, targetCurve, cutter, pickPoint, d0, d1, endTol, viewPlane)
        : CollectInteriorCurveCurveParams(
          targetCurve,
          cutter,
          d0,
          d1,
          endTol,
          doc,
          intersectionTolerance: StrictCurveContactTolerance(doc, targetCurve, cutter)));
    }

    if (parameters.Count == 0)
      return new List<double>();

    return UniqueParams(parameters, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
  }

  private static List<double> SanitizeSplitParameters(Curve targetCurve, IReadOnlyList<double> splitParameters)
  {
    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return new List<double>();

    var boundaryTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-9);
    if (targetCurve.IsClosed)
    {
      var closed = splitParameters
        .Where(t => t >= d0 - boundaryTol && t <= d1 + boundaryTol)
        .Select(t => t >= d1 - boundaryTol ? d0 : Math.Max(d0, t));
      return UniqueParams(closed, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
    }

    var valid = splitParameters.Where(t => t > d0 + boundaryTol && t < d1 - boundaryTol);
    return UniqueParams(valid, Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-8));
  }

  private static List<double> CollectPreparedSplitParameters(
    RhinoDoc doc,
    Curve targetCurve,
    IReadOnlyList<Curve> cutterCurves,
    Point3d pickPoint,
    bool allowViewProjection,
    out Curve workingCurve)
  {
    workingCurve = targetCurve;

    if (!targetCurve.IsClosed)
    {
      return SanitizeSplitParameters(
        targetCurve,
        CollectSplitParameters(doc, targetCurve, cutterCurves, pickPoint, allowViewProjection));
    }

    var initial = SanitizeSplitParameters(
      targetCurve,
      CollectSplitParameters(doc, targetCurve, cutterCurves, pickPoint, allowViewProjection));
    if (initial.Count < 2 || !TryGetDomain(targetCurve, out var d0, out var d1))
      return initial;

    var period = d1 - d0;
    if (period <= RhinoMath.ZeroTolerance)
      return initial;

    var contacts = initial
      .Select(t => t >= d1 ? d0 : Math.Max(d0, Math.Min(d1, t)))
      .OrderBy(t => t)
      .ToList();
    var seamCandidates = new List<(double Gap, double Parameter)>();
    for (var i = 0; i < contacts.Count; i++)
    {
      var start = contacts[i];
      var end = i + 1 < contacts.Count ? contacts[i + 1] : contacts[0] + period;
      var gap = end - start;
      if (gap <= RhinoMath.ZeroTolerance)
        continue;

      var parameter = start + (gap * 0.5);
      while (parameter >= d1)
        parameter -= period;
      seamCandidates.Add((gap, parameter));
    }

    Curve? bestCurve = null;
    var bestSplit = new List<double>();
    foreach (var candidate in seamCandidates.OrderByDescending(item => item.Gap))
    {
      var reseamed = targetCurve.DuplicateCurve();
      if (reseamed == null || !reseamed.ChangeClosedCurveSeam(candidate.Parameter))
        continue;

      var split = SanitizeSplitParameters(
        reseamed,
        CollectSplitParameters(doc, reseamed, cutterCurves, pickPoint, allowViewProjection));
      if (split.Count > bestSplit.Count)
      {
        bestCurve = reseamed;
        bestSplit = split;
      }

      if (split.Count >= initial.Count)
        break;
    }

    if (bestCurve != null && bestSplit.Count >= 2)
    {
      workingCurve = bestCurve;
      return bestSplit;
    }

    return initial;
  }

  private static int ClosestPieceIndex(IReadOnlyList<Curve> pieces, Point3d pickPoint)
  {
    var bestIndex = -1;
    var bestD2 = double.MaxValue;

    for (var i = 0; i < pieces.Count; i++)
    {
      var piece = pieces[i];
      if (piece == null)
        continue;

      if (!piece.ClosestPoint(pickPoint, out var t))
        continue;

      var cp = piece.PointAt(t);
      var d2 = cp.DistanceToSquared(pickPoint);
      if (d2 < bestD2)
      {
        bestD2 = d2;
        bestIndex = i;
      }
    }

    return bestIndex;
  }

  private static Curve? TrimOpenCurveFromEnd(Curve targetCurve, Point3d pickPoint, IReadOnlyList<double> splitParameters, double tolScale = 1.0)
  {
    if (targetCurve == null || targetCurve.IsClosed || splitParameters.Count == 0)
      return null;

    if (!targetCurve.ClosestPoint(pickPoint, out var tPick))
      return null;

    var domain = targetCurve.Domain;
    var d0 = domain.T0;
    var d1 = domain.T1;
    var tFirst = splitParameters.Min();
    var tLast = splitParameters.Max();
    var tTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-6) * Math.Max(1.0, tolScale);

    if (tPick <= tFirst + tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(tFirst, d1));
      }
      catch
      {
        return null;
      }
    }

    if (tPick >= tLast - tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(d0, tLast));
      }
      catch
      {
        return null;
      }
    }

    return null;
  }

  private static Curve? TrimOpenCurveFromEndRemovedPiece(Curve targetCurve, Point3d pickPoint, IReadOnlyList<double> splitParameters, double tolScale = 1.0)
  {
    if (targetCurve == null || targetCurve.IsClosed || splitParameters.Count == 0)
      return null;

    if (!targetCurve.ClosestPoint(pickPoint, out var tPick))
      return null;

    var domain = targetCurve.Domain;
    var d0 = domain.T0;
    var d1 = domain.T1;
    var tFirst = splitParameters.Min();
    var tLast = splitParameters.Max();
    var tTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-6) * Math.Max(1.0, tolScale);

    if (tPick <= tFirst + tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(d0, tFirst));
      }
      catch
      {
        return null;
      }
    }

    if (tPick >= tLast - tTol)
    {
      try
      {
        return targetCurve.Trim(new Interval(tLast, d1));
      }
      catch
      {
        return null;
      }
    }

    return null;
  }

  private static double? TryGetCurveLength(Curve curve)
  {
    try
    {
      return curve.GetLength();
    }
    catch
    {
      return null;
    }
  }

  private static Curve? ExtendCurveToCuttersFromPick(
    RhinoDoc doc,
    Curve targetCurve,
    Point3d pickPoint,
    IReadOnlyList<Curve> cutterCurves,
    bool extendAsLine)
  {
    if (cutterCurves == null || cutterCurves.Count == 0)
      return null;

    if (!TryGetExtendAnchorAndDirection(targetCurve, pickPoint, out var movingEnd, out _, out _))
      return null;

    var style = extendAsLine ? CurveExtensionStyle.Line : CurveExtensionStyle.Smooth;
    var ends = movingEnd == CurveEnd.Start
      ? new[] { CurveEnd.Start, CurveEnd.End }
      : new[] { CurveEnd.End, CurveEnd.Start };

    var sourceLength = TryGetCurveLength(targetCurve);
    var minGain = Math.Max(doc.ModelAbsoluteTolerance, 1.0e-8);

    foreach (var end in ends)
    {
      try
      {
        var candidate = targetCurve.Extend(end, style, cutterCurves.ToArray());
        if (candidate == null)
          continue;

        if (sourceLength.HasValue)
        {
          var candidateLength = TryGetCurveLength(candidate);
          if (candidateLength.HasValue && candidateLength.Value <= sourceLength.Value + minGain)
            continue;
        }

        return candidate;
      }
      catch
      {
      }
    }

    return null;
  }

  private static bool TryBuildTrimPlan(
    RhinoDoc doc,
    Curve targetCurve,
    Point3d pickPoint,
    IReadOnlyList<Curve> cutterCurves,
    bool allowViewProjection,
    bool allowBoundaryExtend,
    bool extendAsLine,
    bool joinAfterTrim,
    out TrimPlan? plan,
    out string failure)
  {
    plan = null;
    failure = string.Empty;

    var split = CollectPreparedSplitParameters(
      doc,
      targetCurve,
      cutterCurves,
      pickPoint,
      allowViewProjection,
      out var workingCurve);

    if (split.Count == 0 && allowBoundaryExtend)
    {
      var extended = ExtendCurveToCuttersFromPick(doc, workingCurve, pickPoint, cutterCurves, extendAsLine);
      if (extended != null)
      {
        split = CollectPreparedSplitParameters(
          doc,
          extended,
          cutterCurves,
          pickPoint,
          allowViewProjection,
          out workingCurve);
      }
    }

    if (split.Count == 0)
    {
      failure = "no valid trim intersections";
      return false;
    }

    var directScale = 1.0;
    var direct = TrimOpenCurveFromEnd(workingCurve, pickPoint, split);
    if (direct == null)
    {
      directScale = 250.0;
      direct = TrimOpenCurveFromEnd(workingCurve, pickPoint, split, directScale);
    }

    if (direct != null)
    {
      var removed = TrimOpenCurveFromEndRemovedPiece(workingCurve, pickPoint, split, directScale);
      if (removed == null)
      {
        failure = "could not resolve removed end segment";
        return false;
      }

      try
      {
        if (!direct.IsValid || direct.GetLength() <= doc.ModelAbsoluteTolerance)
        {
          failure = "trim result is too short or invalid";
          return false;
        }
      }
      catch
      {
        failure = "trim result could not be measured";
        return false;
      }

      plan = new TrimPlan
      {
        RemovedPiece = removed,
        Output = new List<Curve> { direct }
      };
      return true;
    }

    Curve[]? pieces;
    try
    {
      pieces = workingCurve.Split(split);
    }
    catch
    {
      pieces = null;
    }

    if (pieces == null || pieces.Length < 2)
    {
      failure = "target could not be split";
      return false;
    }

    var removeIndex = ClosestPieceIndex(pieces, pickPoint);
    if (removeIndex < 0 || pieces[removeIndex] == null)
    {
      failure = "clicked segment could not be resolved";
      return false;
    }

    var keep = new List<Curve>();
    foreach (var (piece, index) in pieces.Select((piece, index) => (piece, index)))
    {
      if (index == removeIndex || piece == null)
        continue;

      try
      {
        if (piece.IsValid && piece.GetLength() > doc.ModelAbsoluteTolerance)
          keep.Add(piece);
      }
      catch
      {
      }
    }

    if (keep.Count == 0)
    {
      failure = "trim would remove the entire curve";
      return false;
    }

    var output = new List<Curve>(keep);
    if (joinAfterTrim)
    {
      try
      {
        var joined = Curve.JoinCurves(keep, doc.ModelAbsoluteTolerance);
        if (joined != null && joined.Length > 0)
          output = joined.Where(curve => curve != null && curve.IsValid).ToList();
      }
      catch
      {
      }
    }

    if (output.Count == 0)
    {
      failure = "trim output is invalid";
      return false;
    }

    plan = new TrimPlan
    {
      RemovedPiece = pieces[removeIndex],
      Output = output
    };
    return true;
  }

  private static bool TrimCurveObject(
    RhinoDoc doc,
    RhinoObject targetObj,
    TrimPlan? plan,
    out ActionRecord? actionRecord)
  {
    actionRecord = null;

    if (plan == null)
    {
      const string failure = "no valid preview plan was captured";
      RhinoApp.WriteLine($"vTrim: {failure}.");
      Log.Write("vTrim", "trim rejected target={0} reason={1}", targetObj.Id, failure);
      return false;
    }

    if (!TryCaptureCurveSnapshot(doc, targetObj.Id, out var beforeTarget) || beforeTarget == null)
    {
      RhinoApp.WriteLine("vTrim: failed to capture target state.");
      Log.Write("vTrim", "trim rejected target={0} reason=snapshot failed", targetObj.Id);
      return false;
    }

    var attr = targetObj.Attributes.Duplicate();
    if (!doc.Objects.Replace(targetObj.Id, plan.Output[0]))
    {
      RhinoApp.WriteLine("vTrim: failed to replace target curve.");
      Log.Write("vTrim", "trim apply failed target={0} reason=replace failed", targetObj.Id);
      return false;
    }

    var addedIds = new List<Guid>();
    for (var i = 1; i < plan.Output.Count; i++)
    {
      var id = doc.Objects.AddCurve(plan.Output[i], attr);
      if (id != Guid.Empty)
        addedIds.Add(id);
    }

    actionRecord = BuildActionRecord(doc, beforeTarget, targetObj.Id, addedIds);
    Log.Write(
      "vTrim",
      "trim applied target={0} output={1} added={2}",
      targetObj.Id,
      plan.Output.Count,
      addedIds.Count);

    return true;
  }

  private static List<Curve> FindAutoCutters(RhinoDoc doc, Guid targetId, Curve targetCurve, Point3d pickPoint)
  {
    if (!TryGetDomain(targetCurve, out var d0, out var d1))
      return new List<Curve>();

    var endTol = Math.Max(1.0e-9, Math.Abs(d1 - d0) * 1.0e-9);
    var ranked = new List<(double Distance, Curve Curve)>();

    foreach (var (obj, curve) in EnumerateDocCurves(doc))
    {
      if (obj.Id == targetId)
        continue;

      var paramsForCurve = CollectInteriorCurveCurveParams(
        targetCurve,
        curve,
        d0,
        d1,
        endTol,
        doc,
        includeEndpoints: true,
        intersectionTolerance: StrictCurveContactTolerance(doc, targetCurve, curve));
      if (paramsForCurve.Count == 0)
        continue;

      var nearest = NearestPickDistanceForParams(targetCurve, pickPoint, paramsForCurve);
      if (!nearest.HasValue)
        continue;

      ranked.Add((nearest.Value, curve));
    }

    var closest = ranked.OrderBy(r => r.Distance).FirstOrDefault();
    return closest.Curve == null
      ? new List<Curve>()
      : new List<Curve> { closest.Curve };
  }

  private static bool TryGetExtendAnchor(Curve curve, Point3d pickPoint, out CurveEnd movingEnd, out Point3d anchor)
  {
    var start = curve.PointAtStart;
    var end = curve.PointAtEnd;

    if (pickPoint.DistanceToSquared(start) <= pickPoint.DistanceToSquared(end))
    {
      movingEnd = CurveEnd.Start;
      anchor = start;
    }
    else
    {
      movingEnd = CurveEnd.End;
      anchor = end;
    }

    return true;
  }

  private static bool TryGetExtendAnchorAndDirection(Curve curve, Point3d pickPoint, out CurveEnd movingEnd, out Point3d anchor, out Vector3d direction)
  {
    direction = Vector3d.Zero;

    if (!TryGetExtendAnchor(curve, pickPoint, out movingEnd, out anchor))
      return false;

    direction = movingEnd == CurveEnd.Start
      ? -curve.TangentAtStart
      : curve.TangentAtEnd;

    if (!direction.Unitize())
      return false;

    return true;
  }

  private static Curve? TryBoundaryExtendToSelectedCutters(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    CurveEnd movingEnd,
    Point3d anchor,
    bool extendAsLine,
    IReadOnlyCollection<Guid> candidateIds,
    bool strictValidation)
  {
    var drivers = ExtendDriverCurves(doc, targetObj.Id, candidateIds);
    if (drivers.Count == 0)
      return null;

    var styles = extendAsLine
      ? new[] { CurveExtensionStyle.Line, CurveExtensionStyle.Smooth }
      : new[] { CurveExtensionStyle.Smooth, CurveExtensionStyle.Line };

    foreach (var style in styles)
    {
      Curve? byBoundary;
      try
      {
        byBoundary = targetCurve.Extend(movingEnd, style, drivers.ToArray());
      }
      catch
      {
        byBoundary = null;
      }

      if (byBoundary == null)
        continue;

      if (!strictValidation)
        return byBoundary;

      var validatedBoundary = ValidateExtendedCandidate(doc, byBoundary, anchor, movingEnd, drivers, out _);
      if (validatedBoundary != null)
        return validatedBoundary;
    }

    return null;
  }

  private static bool IsForwardHit(Point3d anchor, Vector3d direction, Point3d candidate, double minForward, double pathTolerance, out double forwardDistance)
  {
    forwardDistance = double.PositiveInfinity;
    var vector = candidate - anchor;
    var forward = Vector3d.Multiply(vector, direction);
    if (forward <= minForward)
      return false;

    var lateral = vector - (direction * forward);
    if (lateral.Length > pathTolerance)
      return false;

    forwardDistance = forward;
    return true;
  }

  private static double Cross2d(double ax, double ay, double bx, double by)
  {
    return (ax * by) - (ay * bx);
  }

  private static bool TryIntersectRayWithSegment2d(
    double ox,
    double oy,
    double rx,
    double ry,
    double ax,
    double ay,
    double bx,
    double by,
    out double rayT,
    out double segmentU)
  {
    rayT = 0.0;
    segmentU = 0.0;

    var sx = bx - ax;
    var sy = by - ay;
    var denom = Cross2d(rx, ry, sx, sy);
    if (Math.Abs(denom) <= 1.0e-12)
      return false;

    var qx = ax - ox;
    var qy = ay - oy;

    var t = Cross2d(qx, qy, sx, sy) / denom;
    var u = Cross2d(qx, qy, rx, ry) / denom;

    if (t < 0.0 || u < -1.0e-9 || u > 1.0 + 1.0e-9)
      return false;

    rayT = t;
    segmentU = Math.Max(0.0, Math.Min(1.0, u));
    return true;
  }

  private static List<double> CurveSampleParameters(Curve curve, int divisions)
  {
    var parameters = new List<double>();
    var count = Math.Max(32, divisions);

    if (!TryGetDomain(curve, out var d0, out var d1) || Math.Abs(d1 - d0) <= 1.0e-12)
      return parameters;

    try
    {
      var div = curve.DivideByCount(count, true);
      if (div != null)
        parameters.AddRange(div.Select(v => (double)v));
    }
    catch
    {
    }

    if (parameters.Count == 0)
    {
      for (var i = 0; i <= count; i++)
      {
        var t = d0 + ((d1 - d0) * (i / (double)count));
        parameters.Add(t);
      }
    }

    return parameters;
  }

  private static bool TryClosestForwardHitInViewport(
    Rhino.Display.RhinoViewport viewport,
    Curve curve,
    Point3d anchor,
    Vector3d direction,
    double rayLength,
    double minForward,
    out Point3d hitPoint,
    out double extensionDistance)
  {
    hitPoint = Point3d.Unset;
    extensionDistance = double.PositiveInfinity;

    RhinoPoint2d rayStart;
    RhinoPoint2d rayEnd;
    try
    {
      rayStart = viewport.WorldToClient(anchor);
      rayEnd = viewport.WorldToClient(anchor + (direction * rayLength));
    }
    catch
    {
      return false;
    }

    var rx = (double)(rayEnd.X - rayStart.X);
    var ry = (double)(rayEnd.Y - rayStart.Y);
    if ((rx * rx) + (ry * ry) <= 1.0e-12)
      return false;

    var parameters = CurveSampleParameters(curve, 192);
    if (parameters.Count < 2)
      return false;

    Point3d prevWorld;
    RhinoPoint2d prevClient;
    try
    {
      prevWorld = curve.PointAt(parameters[0]);
      prevClient = viewport.WorldToClient(prevWorld);
    }
    catch
    {
      return false;
    }

    for (var i = 1; i < parameters.Count; i++)
    {
      Point3d currWorld;
      RhinoPoint2d currClient;
      try
      {
        currWorld = curve.PointAt(parameters[i]);
        currClient = viewport.WorldToClient(currWorld);
      }
      catch
      {
        prevWorld = Point3d.Unset;
        continue;
      }

      if (!prevWorld.IsValid)
      {
        prevWorld = currWorld;
        prevClient = currClient;
        continue;
      }

      if (TryIntersectRayWithSegment2d(
            rayStart.X,
            rayStart.Y,
            rx,
            ry,
            prevClient.X,
            prevClient.Y,
            currClient.X,
            currClient.Y,
            out _,
            out var segU))
      {
        var candidate = prevWorld + ((currWorld - prevWorld) * segU);
        var forward = Vector3d.Multiply(candidate - anchor, direction);
        if (forward > minForward && forward < extensionDistance)
        {
          extensionDistance = forward;
          hitPoint = candidate;
        }
      }

      prevWorld = currWorld;
      prevClient = currClient;
    }

    return hitPoint.IsValid && double.IsFinite(extensionDistance);
  }

  private static bool TryNearestForwardHitFromOverlap(
    Point3d anchor,
    Vector3d direction,
    Point3d overlapPointA,
    Point3d overlapPointB,
    double minForward,
    double pathTolerance,
    out Point3d bestPoint,
    out double bestDistance)
  {
    bestPoint = Point3d.Unset;
    bestDistance = double.PositiveInfinity;

    var rawA = Vector3d.Multiply(overlapPointA - anchor, direction);
    var rawB = Vector3d.Multiply(overlapPointB - anchor, direction);
    var minRaw = Math.Min(rawA, rawB);
    var maxRaw = Math.Max(rawA, rawB);

    // If overlap starts at/behind the anchor and continues forward,
    // extending would create coincident overlap with the driver.
    if (minRaw <= minForward && maxRaw > minForward)
      return false;

    if (IsForwardHit(anchor, direction, overlapPointA, minForward, pathTolerance, out var dA))
    {
      bestPoint = overlapPointA;
      bestDistance = dA;
    }

    if (IsForwardHit(anchor, direction, overlapPointB, minForward, pathTolerance, out var dB) && dB < bestDistance)
    {
      bestPoint = overlapPointB;
      bestDistance = dB;
    }

    return bestPoint.IsValid && double.IsFinite(bestDistance);
  }

  private static double MapProjectedToLineParameter(double projectedT, double projectedStart, double projectedEnd, double lineStart, double lineEnd)
  {
    if (Math.Abs(projectedEnd - projectedStart) <= 1.0e-12)
      return lineStart;

    var s = (projectedT - projectedStart) / (projectedEnd - projectedStart);
    s = Math.Max(0.0, Math.Min(1.0, s));
    return lineStart + ((lineEnd - lineStart) * s);
  }

  private static bool TryClosestForwardHit(
    RhinoDoc doc,
    Point3d anchor,
    Vector3d direction,
    Guid targetId,
    IReadOnlyCollection<Guid>? candidateIds,
    bool allowViewProjection,
    out Point3d hitPoint,
    out double extensionDistance)
  {
    hitPoint = Point3d.Unset;
    extensionDistance = double.PositiveInfinity;

    var minForward = 1.0e-9;
    var pathTol = allowViewProjection
      ? Math.Max(doc.ModelAbsoluteTolerance * 10.0, 1.0e-4)
      : Math.Max(RhinoMath.ZeroTolerance, doc.ModelAbsoluteTolerance * 1.0e-3);
    var rayLength = Math.Max(10000.0, 100000.0 * doc.ModelAbsoluteTolerance);

    var line = new Line(anchor, anchor + (direction * rayLength));
    var lineCurve = new LineCurve(line);

    var viewPlane = allowViewProjection ? ActiveViewPlane(doc) : null;
    var projectedLine = allowViewProjection ? ProjectCurveToPlane(lineCurve, viewPlane) : null;
    var useViewportFallback = candidateIds != null && candidateIds.Count > 0;
    var viewport = doc.Views.ActiveView?.ActiveViewport;

    foreach (var (obj, curve) in EnumerateDocCurves(doc))
    {
      if (obj.Id == targetId)
        continue;

      if (candidateIds != null && !candidateIds.Contains(obj.Id))
        continue;

      var candidateBest = double.PositiveInfinity;
      var candidatePoint = Point3d.Unset;

      var worldTolerance = allowViewProjection
        ? doc.ModelAbsoluteTolerance
        : StrictCurveContactTolerance(doc, lineCurve, curve);
      var worldEvents = Rhino.Geometry.Intersect.Intersection.CurveCurve(
        lineCurve,
        curve,
        worldTolerance,
        worldTolerance);
      if (worldEvents != null)
      {
        foreach (var ev in worldEvents)
        {
          if (ev.IsPoint)
          {
            var point = ev.PointA;
            if (IsForwardHit(anchor, direction, point, minForward, pathTol, out var dist) && dist < candidateBest)
            {
              candidateBest = dist;
              candidatePoint = point;
            }
          }
          else if (ev.IsOverlap)
          {
            var p0 = lineCurve.PointAt(ev.OverlapA.T0);
            var p1 = lineCurve.PointAt(ev.OverlapA.T1);

            if (TryNearestForwardHitFromOverlap(anchor, direction, p0, p1, minForward, pathTol, out var overlapPoint, out var overlapDistance) && overlapDistance < candidateBest)
            {
              candidateBest = overlapDistance;
              candidatePoint = overlapPoint;
            }
          }
        }
      }

      if (projectedLine != null)
      {
        var projectedCurve = ProjectCurveToPlane(curve, viewPlane);
        if (projectedCurve != null)
        {
          var projEvents = Rhino.Geometry.Intersect.Intersection.CurveCurve(projectedLine, projectedCurve, doc.ModelAbsoluteTolerance, doc.ModelAbsoluteTolerance);
          if (projEvents != null && TryGetDomain(projectedLine, out var p0, out var p1) && TryGetDomain(lineCurve, out var l0, out var l1))
          {
            foreach (var ev in projEvents)
            {
              if (ev.IsPoint)
              {
                var tLine = MapProjectedToLineParameter(ev.ParameterA, p0, p1, l0, l1);
                var point = lineCurve.PointAt(tLine);
                if (IsForwardHit(anchor, direction, point, minForward, pathTol, out var dist) && dist < candidateBest)
                {
                  candidateBest = dist;
                  candidatePoint = point;
                }
              }
              else if (ev.IsOverlap)
              {
                var tLine0 = MapProjectedToLineParameter(ev.OverlapA.T0, p0, p1, l0, l1);
                var tLine1 = MapProjectedToLineParameter(ev.OverlapA.T1, p0, p1, l0, l1);
                var overlapPoint0 = lineCurve.PointAt(tLine0);
                var overlapPoint1 = lineCurve.PointAt(tLine1);

                if (TryNearestForwardHitFromOverlap(anchor, direction, overlapPoint0, overlapPoint1, minForward, pathTol, out var overlapPoint, out var overlapDistance) && overlapDistance < candidateBest)
                {
                  candidateBest = overlapDistance;
                  candidatePoint = overlapPoint;
                }
              }
            }
          }
        }
      }

      if (useViewportFallback && viewport != null && TryClosestForwardHitInViewport(viewport, curve, anchor, direction, rayLength, minForward, out var viewPoint, out var viewDistance))
      {
        if (viewDistance < candidateBest)
        {
          candidateBest = viewDistance;
          candidatePoint = viewPoint;
        }
      }

      if (candidatePoint.IsValid && candidateBest < extensionDistance)
      {
        extensionDistance = candidateBest;
        hitPoint = candidatePoint;
      }
    }

    return hitPoint.IsValid && double.IsFinite(extensionDistance);
  }

  private static List<Curve> ExtendDriverCurves(RhinoDoc doc, Guid targetId, IReadOnlyCollection<Guid>? candidateIds)
  {
    var curves = new List<Curve>();

    foreach (var (obj, curve) in EnumerateDocCurves(doc))
    {
      if (obj.Id == targetId)
        continue;
      if (candidateIds != null && !candidateIds.Contains(obj.Id))
        continue;

      curves.Add(curve);
    }

    return curves;
  }

  private static Curve? ExtractAddedExtensionPiece(RhinoDoc doc, Curve extendedCurve, Point3d anchor, CurveEnd movingEnd)
  {
    if (!extendedCurve.ClosestPoint(anchor, out var tAnchor))
      return null;

    try
    {
      Curve? addedPiece = movingEnd == CurveEnd.Start
        ? extendedCurve.Trim(new Interval(extendedCurve.Domain.T0, tAnchor))
        : extendedCurve.Trim(new Interval(tAnchor, extendedCurve.Domain.T1));

      if (addedPiece == null)
        return null;

      if (addedPiece.GetLength() <= 1.0e-9)
        return null;

      return addedPiece;
    }
    catch
    {
      return null;
    }
  }

  private static bool CurveOverlapsAnyDriver(RhinoDoc doc, Curve candidate, IReadOnlyList<Curve> drivers)
  {
    if (drivers.Count == 0)
      return false;

    var overlapLenTol = Math.Max(doc.ModelAbsoluteTolerance * 2.0, 1.0e-8);
    foreach (var driver in drivers)
    {
      if (driver == null)
        continue;

      var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(
        candidate,
        driver,
        doc.ModelAbsoluteTolerance,
        doc.ModelAbsoluteTolerance);
      if (events == null)
        continue;

      foreach (var ev in events)
      {
        if (!ev.IsOverlap)
          continue;

        try
        {
          var overlapPiece = candidate.Trim(ev.OverlapA);
          if (overlapPiece != null && overlapPiece.GetLength() > overlapLenTol)
            return true;
        }
        catch
        {
          if (Math.Abs(ev.OverlapA.Length) > 1.0e-9)
            return true;
        }
      }
    }

    return false;
  }

  private static Curve? ValidateExtendedCandidate(
    RhinoDoc doc,
    Curve? extendedCandidate,
    Point3d anchor,
    CurveEnd movingEnd,
    IReadOnlyList<Curve> drivers,
    out string failure)
  {
    failure = string.Empty;
    if (extendedCandidate == null)
    {
      failure = "candidate is null";
      return null;
    }

    var addedPiece = ExtractAddedExtensionPiece(doc, extendedCandidate, anchor, movingEnd);
    if (addedPiece == null)
    {
      failure = "added segment is empty or too short";
      return null;
    }

    var newEndpoint = movingEnd == CurveEnd.Start
      ? extendedCandidate.PointAtStart
      : extendedCandidate.PointAtEnd;
    const double movementTolerance = 1.0e-9;
    var endpointTolerance = Math.Max(doc.ModelAbsoluteTolerance * 10.0, 1.0e-8);
    var addedDistance = newEndpoint.DistanceTo(anchor);
    if (addedDistance <= movementTolerance)
    {
      failure = $"endpoint moved only {addedDistance:G17} (tolerance {movementTolerance:G17})";
      return null;
    }

    if (!EndpointTouchesAnyDriver(
          doc,
          newEndpoint,
          drivers,
          endpointTolerance,
          out var nearestWorld,
          out var nearestView))
    {
      failure = $"endpoint misses cutters (world {nearestWorld:G17}, view {nearestView:G17}, tolerance {endpointTolerance:G17})";
      return null;
    }

    if (CurveOverlapsAnyDriver(doc, addedPiece, drivers))
    {
      failure = "added segment overlaps a cutter";
      return null;
    }

    return extendedCandidate;
  }

  private static bool EndpointTouchesAnyDriver(
    RhinoDoc doc,
    Point3d endpoint,
    IReadOnlyList<Curve> drivers,
    double tolerance,
    out double nearestWorld,
    out double nearestView)
  {
    nearestWorld = double.PositiveInfinity;
    nearestView = double.PositiveInfinity;
    var viewPlane = ActiveViewPlane(doc);
    foreach (var driver in drivers)
    {
      if (driver == null)
        continue;

      try
      {
        if (driver.ClosestPoint(endpoint, out var parameter))
        {
          var distance = driver.PointAt(parameter).DistanceTo(endpoint);
          nearestWorld = Math.Min(nearestWorld, distance);
          if (distance <= tolerance)
            return true;
        }
      }
      catch
      {
      }

      if (viewPlane == null)
        continue;

      try
      {
        var projectedDriver = ProjectCurveToPlane(driver, viewPlane);
        var projectedEndpoint = viewPlane.Value.ClosestPoint(endpoint);
        if (projectedDriver != null &&
            projectedDriver.ClosestPoint(projectedEndpoint, out var projectedParameter))
        {
          var distance = projectedDriver.PointAt(projectedParameter).DistanceTo(projectedEndpoint);
          nearestView = Math.Min(nearestView, distance);
          if (distance <= tolerance)
            return true;
        }
      }
      catch
      {
      }
    }

    return false;
  }

  private static Curve? TryBuildExtendedCurve(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    CurveEnd movingEnd,
    Point3d anchor,
    Vector3d direction,
    Point3d hitPoint,
    double extensionDistance,
    bool extendAsLine,
    IReadOnlyCollection<Guid>? candidateIds,
    bool strictValidation,
    bool allowLengthFallback,
    out string failure)
  {
    failure = string.Empty;
    var style = extendAsLine ? CurveExtensionStyle.Line : CurveExtensionStyle.Smooth;
    var drivers = ExtendDriverCurves(doc, targetObj.Id, candidateIds);
    var failures = new List<string>();

    Curve? MaybeValidate(Curve? candidate, string source)
    {
      if (candidate == null)
      {
        failures.Add($"{source}: no candidate");
        return null;
      }

      if (!strictValidation)
        return candidate;

      var validated = ValidateExtendedCandidate(
        doc,
        candidate,
        anchor,
        movingEnd,
        drivers,
        out var validationFailure);
      if (validated == null)
        failures.Add($"{source}: {validationFailure}");
      return validated;
    }

    Curve? MaybeValidateExactHit(Curve? candidate, string source)
    {
      if (candidate == null)
      {
        failures.Add($"{source}: no candidate");
        return null;
      }

      var endpoint = movingEnd == CurveEnd.Start
        ? candidate.PointAtStart
        : candidate.PointAtEnd;
      var hitTolerance = Math.Max(1.0e-8, doc.ModelAbsoluteTolerance * 1.0e-4);
      var hitError = endpoint.DistanceTo(hitPoint);
      if (hitError > hitTolerance)
      {
        failures.Add($"{source}: endpoint misses exact hit by {hitError:G17}");
        return null;
      }

      return MaybeValidate(candidate, source);
    }

    if (targetCurve.IsLinear(StrictCurveContactTolerance(doc, targetCurve, targetCurve)))
    {
      var exactLine = movingEnd == CurveEnd.Start
        ? new LineCurve(hitPoint, targetCurve.PointAtEnd)
        : new LineCurve(targetCurve.PointAtStart, hitPoint);
      var validatedExactLine = MaybeValidateExactHit(exactLine, "exact line");
      if (validatedExactLine != null)
        return validatedExactLine;
    }

    try
    {
      var byHitPoint = targetCurve.Extend(movingEnd, style, hitPoint);
      var validatedHitPoint = MaybeValidateExactHit(byHitPoint, "hit point");
      if (validatedHitPoint != null)
        return validatedHitPoint;
    }
    catch (Exception ex)
    {
      failures.Add($"hit point: {ex.GetType().Name}");
    }

    if (drivers.Count > 0)
    {
      try
      {
        var byBoundary = targetCurve.Extend(movingEnd, style, drivers.ToArray());
        var validatedBoundary = MaybeValidate(byBoundary, "boundary");
        if (validatedBoundary != null)
          return validatedBoundary;
      }
      catch (Exception ex)
      {
        failures.Add($"boundary: {ex.GetType().Name}");
      }
    }

    if (allowLengthFallback)
    {
      try
      {
        var byLength = targetCurve.Extend(movingEnd, extensionDistance, style);
        var validatedByLength = MaybeValidate(byLength, "distance");
        if (validatedByLength != null)
          return validatedByLength;
      }
      catch (Exception ex)
      {
        failures.Add($"distance: {ex.GetType().Name}");
      }

      // Last fallback: line-extend to hit point if boundary extend failed.
      try
      {
        var extra = Math.Max(extensionDistance, doc.ModelAbsoluteTolerance * 2.0);
        var byLength = targetCurve.Extend(movingEnd, extra, CurveExtensionStyle.Line);
        var validatedFallback = MaybeValidate(byLength, "line distance");
        if (validatedFallback != null)
          return validatedFallback;
      }
      catch (Exception ex)
      {
        failures.Add($"line distance: {ex.GetType().Name}");
      }
    }

    failure = failures.Count > 0
      ? string.Join("; ", failures)
      : "no candidate was generated";
    return null;
  }

  private static bool TryBuildExtendPlan(
    RhinoDoc doc,
    RhinoObject targetObj,
    Curve targetCurve,
    Point3d pickPoint,
    bool extendAsLine,
    bool autoMode,
    IReadOnlyList<Guid> cutterIds,
    out ExtendPlan? plan,
    out string failure)
  {
    plan = null;
    failure = string.Empty;

    IReadOnlyCollection<Guid>? candidateIds = null;
    if (!autoMode)
      candidateIds = cutterIds.Where(id => id != Guid.Empty && id != targetObj.Id).ToHashSet();

    if (!TryGetExtendAnchorAndDirection(targetCurve, pickPoint, out var movingEnd, out var anchor, out var direction))
    {
      failure = "could not resolve the clicked endpoint";
      return false;
    }

    if (candidateIds != null && candidateIds.Count > 0)
    {
      var boundaryExtended = TryBoundaryExtendToSelectedCutters(
        doc,
        targetObj,
        targetCurve,
        movingEnd,
        anchor,
        extendAsLine,
        candidateIds,
        strictValidation: true);

      if (boundaryExtended != null)
      {
        var boundaryPiece = ExtractAddedExtensionPiece(doc, boundaryExtended, anchor, movingEnd);
        if (boundaryPiece != null)
        {
          plan = new ExtendPlan
          {
            ExtendedCurve = boundaryExtended,
            AddedPiece = boundaryPiece
          };
          return true;
        }
      }
    }

    var hasForwardHit = TryClosestForwardHit(
      doc,
      anchor,
      direction,
      targetObj.Id,
      candidateIds,
      allowViewProjection: !autoMode,
      out var hitPoint,
      out var extensionDistance);
    if (!hasForwardHit)
    {
      failure = "no forward cutter intersection from the clicked endpoint";
      return false;
    }

    var extended = TryBuildExtendedCurve(
      doc,
      targetObj,
      targetCurve,
      movingEnd,
      anchor,
      direction,
      hitPoint,
      extensionDistance,
      extendAsLine,
      candidateIds,
      strictValidation: true,
      allowLengthFallback: true,
      out var buildFailure);
    if (extended == null)
    {
      failure = $"hit=({hitPoint.X:G17},{hitPoint.Y:G17},{hitPoint.Z:G17}) distance={extensionDistance:G17}; {buildFailure}";
      return false;
    }

    var addedPiece = ExtractAddedExtensionPiece(doc, extended, anchor, movingEnd);
    if (addedPiece == null)
    {
      failure = "the added extension is empty or too short";
      return false;
    }

    plan = new ExtendPlan
    {
      ExtendedCurve = extended,
      AddedPiece = addedPiece
    };
    return true;
  }

  private static bool ExtendCurveObject(
    RhinoDoc doc,
    RhinoObject targetObj,
    ExtendPlan? plan,
    out ActionRecord? actionRecord)
  {
    actionRecord = null;

    if (plan == null)
    {
      const string failure = "no valid preview plan was captured";
      RhinoApp.WriteLine($"vTrim: {failure}.");
      Log.Write("vTrim", "extend rejected target={0} reason={1}", targetObj.Id, failure);
      return false;
    }

    if (!TryCaptureCurveSnapshot(doc, targetObj.Id, out var beforeTarget) || beforeTarget == null)
    {
      RhinoApp.WriteLine("vTrim: failed to capture target state.");
      Log.Write("vTrim", "extend rejected target={0} reason=snapshot failed", targetObj.Id);
      return false;
    }

    if (!doc.Objects.Replace(targetObj.Id, plan.ExtendedCurve))
    {
      RhinoApp.WriteLine("vTrim: failed to replace target curve.");
      Log.Write("vTrim", "extend apply failed target={0} reason=replace failed", targetObj.Id);
      return false;
    }

    actionRecord = BuildActionRecord(doc, beforeTarget, targetObj.Id, null);
    Log.Write("vTrim", "extend applied target={0}", targetObj.Id);
    return true;
  }
}
