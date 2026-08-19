using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native line command ported from LinePlus.py.
/// </summary>
public sealed class vLine : Command
{
  private const string OptionsSectionName = "vLine";
  private const string ChainModeKey = "chainMode";
  private const string PriorityKey = "priority";
  private const string PersistConstraintKey = "persistConstraint";
  private const string LengthKey = "length";
  private const string AngleKey          = "angle";
  private const string AngleRelativeKey  = "angleRelative";
  private const string LayerKey          = "layer";
  private const string CurrentLayerOption = "*Current*";
  private const string UndoSessionMarkerKey = "vTools.vLine.UndoSession";

  private static readonly string[] ChainModeValues = { "Single", "Multiple", "Chained", "Polyline" };
  private static readonly string[] PriorityValues = { "Closest", "PerpFirst", "TanFirst", "KeepCurrent" };
  private static readonly Color SourceFeedbackColor = Color.Orange;
  private static readonly Color HoverFeedbackColor = Color.Orange;

  private const int ModeSingle = 0;
  private const int ModeMultiple = 1;
  private const int ModeChained = 2;
  private const int ModePolyline = 3;

  private const int PriorityClosest = 0;
  private const int PriorityPerpFirst = 1;
  private const int PriorityTanFirst = 2;
  private const int PriorityKeepCurrent = 3;

  private static int _chainMode = ModeSingle;
  private static int _priority = PriorityClosest;
  private static bool _persistConstraint;
  private static double _length;
  private static double _angle;
  private static bool   _angleRelative;
  private static string _layer = CurrentLayerOption;

  private static bool _debugMode = false;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vLine";

  /// <summary>
  /// Executes interactive line drawing with chain modes and curve-based constraints.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    if (LineUndoRecordSession.DeferRunUntilFinalized(doc))
      return Result.Success;

    Log.Write("vLine", "begin");
    LoadPersistedOptions();
    var undoSession = new LineUndoRecordSession(doc);
    var layerSession = new LineLayerSession(doc, _layer, undoSession.Token);

    try
    {

    var startResult = ResolveFirstPoint(
      doc, layerSession, initialBothSides: false, initialChainMode: _chainMode, mode);
    _chainMode = startResult.ChainMode;
    Log.Write("vLine",
      $"start hasPoint={startResult.HasPoint} completed={startResult.Completed} " +
      $"constraint={startResult.Constraint?.Kind.ToString() ?? "none"} " +
      $"direction={startResult.Direction.HasValue}");
    if (startResult.Completed)
      return Result.Success;
    if (!startResult.HasPoint)
    {
      SavePersistedOptions();
      return Result.Cancel;
    }

    var currentStart = startResult.Point;
    var startConstraintState = startResult.Constraint;
    var startDirectionState = startResult.Direction;
    var startFeedbackState = startResult.FeedbackGeometry;
    var firstSegment = true;
    var chainModeState = startResult.ChainMode;
    var initialBothSides = startResult.BothSides;

    string? constraintModeState = null;
    var persistConstraintState = _persistConstraint;
    var priorityState = _priority;
    var lengthState = _length;
    var angleLockState = false; // angle lock always starts off; activates when user sets an angle
    var angleState = _angle;
    var angleRelativeState = _angleRelative;

    Vector3d? lastSegmentVector = null;

    List<Point3d>? polylinePoints = null;
    Guid tempPolylineId = Guid.Empty;
    var redoStack    = new Stack<Point3d>();
    var lineHistory  = new Stack<(Point3d segStart, Point3d segEnd, Guid lineId)>(); // Chained/Multiple undo
    var lineRedoData = new Stack<(Point3d segStart, Point3d segEnd)>();              // Chained redo
    var pendingRedoEnds = new Stack<Point3d>(); // step-1 redo queues end here; step-2 redo completes it

    var continueChain = true;
    using var shortcutSession = new LocalUndoRedoShortcutSession(
      "vLine",
      redo => redo ? "redo" : "undo");
    while (continueChain)
    {
      var segmentBothDefault = firstSegment ? initialBothSides : false;
      // Endpoint constraint options are single-use in multi-segment modes; only Single mode inherits.
      var modeSeed = chainModeState == ModeSingle && (persistConstraintState || firstSegment) ? constraintModeState : null;

      var canUndo = (chainModeState == ModePolyline && polylinePoints is { Count: >= 2 })
                 || (chainModeState == ModeChained   && lineHistory.Count > 0)
                 || (chainModeState == ModeMultiple); // always enabled: step-2 undo returns to start pick
      // Keep the local redo route active in every mode.
      var canRedo = true;
      var secondResult = ResolveSecondPoint(
        doc,
        currentStart,
        segmentBothDefault,
        lengthState,
        modeSeed,
        chainModeState,
        persistConstraintState,
        priorityState,
        angleLockState,
        angleState,
        angleRelativeState,
        lastSegmentVector,
        startConstraintState,
        startDirectionState,
        startFeedbackState,
        false,
        layerSession,
        mode,
        canUndo,
        canRedo);

      if (secondResult.State != null)
      {
        var state = secondResult.State.Value;
        constraintModeState = state.Mode;
        persistConstraintState = state.PersistConstraint;
        priorityState = state.Priority;
        lengthState = state.Length;
        angleLockState = state.AngleLock;
        angleState = state.Angle;
        angleRelativeState = state.AngleRelative;

        _persistConstraint = persistConstraintState;
        _priority = priorityState;
        _length = lengthState;
        _angle = angleState;
        _angleRelative = angleRelativeState;
      }

      if (secondResult.IsUndo)
      {
        if (chainModeState == ModeMultiple)
        {
          // Step-2 undo: mini step-1 loop — handles undo, redo, and new start pick.
          while (true)
          {
            var sr = ResolveFirstPoint(doc, layerSession, initialBothSides, ModeMultiple, mode,
              canUndo: lineHistory.Count > 0, canRedo: lineRedoData.Count > 0);
            if (sr.Completed) return Result.Success;
            if (sr.IsUndo && lineHistory.TryPop(out var undoSeg))
            {
              lineRedoData.Push((undoSeg.segStart, undoSeg.segEnd));
              pendingRedoEnds.Clear();
              DeleteObjectIfValid(doc, undoSeg.lineId);
              currentStart = undoSeg.segStart;
              lastSegmentVector = lineHistory.Count > 0
                ? (Vector3d?)(lineHistory.Peek().segEnd - lineHistory.Peek().segStart)
                : null;
              startConstraintState = null; startDirectionState = null; startFeedbackState = null;
              doc.Views.Redraw(); break;
            }
            if (sr.IsRedo && lineRedoData.TryPop(out var redoSeg))
            {
              // Step-1 redo: restore start for step-2, queue end for completion
              pendingRedoEnds.Push(redoSeg.segEnd);
              currentStart = redoSeg.segStart;
              lastSegmentVector = null; startConstraintState = null; startDirectionState = null; startFeedbackState = null;
              break;
            }
            if (!sr.HasPoint) { SavePersistedOptions(); return Result.Cancel; }
            currentStart = sr.Point; startConstraintState = sr.Constraint;
            startDirectionState = sr.Direction; startFeedbackState = sr.FeedbackGeometry;
            chainModeState = sr.ChainMode; lastSegmentVector = null; break;
          }
          continueChain = true;
          SavePersistedOptions();
          continue;
        }
        if (polylinePoints is { Count: >= 2 })
        {
          redoStack.Push(polylinePoints[^1]);
          polylinePoints.RemoveAt(polylinePoints.Count - 1);
          currentStart = polylinePoints[^1];
          DeleteObjectIfValid(doc, tempPolylineId);
          if (polylinePoints.Count >= 2)
          {
            tempPolylineId = doc.Objects.AddPolyline(
              new Polyline(polylinePoints), layerSession.CreateAttributes(doc));
          }
          else
          {
            tempPolylineId = Guid.Empty;
            polylinePoints = null;
            firstSegment = true;
          }
          lastSegmentVector = polylinePoints is { Count: >= 2 }
            ? polylinePoints[^1] - polylinePoints[^2]
            : null;
          doc.Views.Redraw();
        }
        else if (chainModeState == ModeChained && lineHistory.TryPop(out var lastSeg))
        {
          lineRedoData.Push((lastSeg.segStart, lastSeg.segEnd));
          pendingRedoEnds.Clear();
          DeleteObjectIfValid(doc, lastSeg.lineId);
          currentStart = lastSeg.segStart;
          lastSegmentVector = lineHistory.Count > 0
            ? (Vector3d?)(lineHistory.Peek().segEnd - lineHistory.Peek().segStart)
            : null;
          doc.Views.Redraw();
        }
        continueChain = true;
        SavePersistedOptions();
        continue;
      }

      if (secondResult.IsRedo)
      {
        if (pendingRedoEnds.TryPop(out var pendingEnd) && chainModeState == ModeMultiple)
        {
          // Step-2 redo: complete pending segment from currentStart (=segStart) to stored end
          var cmpId = doc.Objects.AddLine(currentStart, pendingEnd, layerSession.CreateAttributes(doc));
          if (cmpId != Guid.Empty)
          {
            lineHistory.Push((currentStart, pendingEnd, cmpId));
            lastSegmentVector = pendingEnd - currentStart;
            doc.Views.Redraw();
            var undoneByRedo = false;
            while (true)
            {
              var ns = ResolveFirstPoint(doc, layerSession, initialBothSides, ModeMultiple, mode,
                canUndo: lineHistory.Count > 0, canRedo: lineRedoData.Count > 0);
              if (ns.Completed) return Result.Success;
              if (ns.IsUndo && lineHistory.TryPop(out var uSeg2))
              { lineRedoData.Push((uSeg2.segStart, uSeg2.segEnd)); pendingRedoEnds.Clear(); DeleteObjectIfValid(doc, uSeg2.lineId); currentStart = uSeg2.segStart; lastSegmentVector = lineHistory.Count > 0 ? (Vector3d?)(lineHistory.Peek().segEnd - lineHistory.Peek().segStart) : null; startConstraintState = null; startDirectionState = null; startFeedbackState = null; doc.Views.Redraw(); undoneByRedo = true; break; }
              if (ns.IsRedo && lineRedoData.TryPop(out var rSeg2))
              { pendingRedoEnds.Push(rSeg2.segEnd); currentStart = rSeg2.segStart; lastSegmentVector = null; startConstraintState = null; startDirectionState = null; startFeedbackState = null; break; }
              if (!ns.HasPoint) { SavePersistedOptions(); return Result.Cancel; }
              currentStart = ns.Point; startConstraintState = ns.Constraint; startDirectionState = ns.Direction; startFeedbackState = ns.FeedbackGeometry; chainModeState = ns.ChainMode; firstSegment = true; lastSegmentVector = null; continueChain = true; break;
            }
            if (undoneByRedo) { continueChain = true; SavePersistedOptions(); continue; }
            // currentStart is already set by the mini step-1 sub-loop; do not overwrite with pendingEnd
          }
        }
        else if (redoStack.TryPop(out var redoPoint))
        {
          polylinePoints ??= new List<Point3d> { currentStart };
          polylinePoints.Add(redoPoint);
          currentStart = redoPoint;
          DeleteObjectIfValid(doc, tempPolylineId);
          if (polylinePoints.Count >= 2)
          {
            tempPolylineId = doc.Objects.AddPolyline(
              new Polyline(polylinePoints), layerSession.CreateAttributes(doc));
            lastSegmentVector = polylinePoints[^1] - polylinePoints[^2];
          }
          doc.Views.Redraw();
        }
        else if ((chainModeState == ModeChained || chainModeState == ModeMultiple) && lineRedoData.TryPop(out var redoSeg))
        {
          if (pendingRedoEnds.Count > 0 || chainModeState == ModeChained)
          {
            // Chained redo or fallthrough: immediate full segment redo
            pendingRedoEnds.Clear();
            var redoId = doc.Objects.AddLine(redoSeg.segStart, redoSeg.segEnd, layerSession.CreateAttributes(doc));
            if (redoId != Guid.Empty)
            {
              lineHistory.Push((redoSeg.segStart, redoSeg.segEnd, redoId));
              currentStart = redoSeg.segEnd;
              lastSegmentVector = redoSeg.segEnd - redoSeg.segStart;
              doc.Views.Redraw();
              if (chainModeState == ModeMultiple)
              {
                // run step-1 sub-loop (same as normal placement)
                var undoneByRedo = false;
                while (true)
                {
                  var ns = ResolveFirstPoint(doc, layerSession, initialBothSides, ModeMultiple, mode,
                    canUndo: lineHistory.Count > 0, canRedo: lineRedoData.Count > 0);
                  if (ns.Completed) return Result.Success;
                  if (ns.IsUndo && lineHistory.TryPop(out var uSeg))
                  {
                    lineRedoData.Push((uSeg.segStart, uSeg.segEnd)); pendingRedoEnds.Clear();
                    DeleteObjectIfValid(doc, uSeg.lineId);
                    currentStart = uSeg.segStart; lastSegmentVector = lineHistory.Count > 0 ? (Vector3d?)(lineHistory.Peek().segEnd - lineHistory.Peek().segStart) : null;
                    startConstraintState = null; startDirectionState = null; startFeedbackState = null;
                    doc.Views.Redraw(); undoneByRedo = true; break;
                  }
                  if (ns.IsRedo && lineRedoData.TryPop(out var reSeg))
                  { pendingRedoEnds.Push(reSeg.segEnd); currentStart = reSeg.segStart; lastSegmentVector = null; startConstraintState = null; startDirectionState = null; startFeedbackState = null; break; }
                  if (!ns.HasPoint) { SavePersistedOptions(); return Result.Cancel; }
                  currentStart = ns.Point; startConstraintState = ns.Constraint;
                  startDirectionState = ns.Direction; startFeedbackState = ns.FeedbackGeometry;
                  chainModeState = ns.ChainMode; firstSegment = true; lastSegmentVector = null; continueChain = true; break;
                }
                if (undoneByRedo) { continueChain = true; SavePersistedOptions(); continue; }
                SavePersistedOptions();
                continue;
              }
            }
          }
          else
          {
            // Multiple step-1 redo: queue end, go to step-2 from stored start
            pendingRedoEnds.Push(redoSeg.segEnd);
            currentStart = redoSeg.segStart;
            lastSegmentVector = null;
          }
        }
        // else: nothing to redo — silently ignore
        continueChain = true;
        SavePersistedOptions();
        continue;
      }

      if (!secondResult.HasPoint)
      {
        if (tempPolylineId != Guid.Empty)
          doc.Views.Redraw();
        SavePersistedOptions();
        return Result.Success;
      }

      var endPoint = secondResult.Point;
      var segmentStart = secondResult.StartPoint;
      var bothSides = secondResult.BothSides;
      var selectedChainMode = secondResult.ChainMode;

      if (selectedChainMode == ModePolyline)
      {
        redoStack.Clear();
        polylinePoints ??= new List<Point3d> { segmentStart };
        var prevPoints = new List<Point3d>(polylinePoints);
        polylinePoints.Add(endPoint);

        DeleteObjectIfValid(doc, tempPolylineId);
        tempPolylineId = doc.Objects.AddPolyline(
          new Polyline(polylinePoints), layerSession.CreateAttributes(doc));
        if (tempPolylineId == Guid.Empty)
        {
          Log.Write("vLine", "failed to add polyline");
          RhinoApp.WriteLine("vLine: failed to add the polyline to the document.");
          return Result.Failure;
        }

        if (polylinePoints.Count >= 2)
          lastSegmentVector = polylinePoints[^1] - polylinePoints[^2];
      }
      else
      {
        if (tempPolylineId != Guid.Empty)
          tempPolylineId = Guid.Empty;

        if (polylinePoints is { Count: > 1 })
          polylinePoints = null;

        Guid lineId;
        if (bothSides)
        {
          var vec = endPoint - segmentStart;
          if (vec.IsTiny())
            return Result.Cancel;

          var startA = segmentStart - vec;
          var startB = segmentStart + vec;
          lineId = doc.Objects.AddLine(
            startA, startB, layerSession.CreateAttributes(doc));
        }
        else
        {
          lineId = doc.Objects.AddLine(
            segmentStart, endPoint, layerSession.CreateAttributes(doc));
        }

        if (lineId == Guid.Empty)
        {
          Log.Write("vLine", "failed to add line");
          RhinoApp.WriteLine("vLine: failed to add the line to the document.");
          return Result.Failure;
        }

        var addedLine = doc.Objects.FindId(lineId);
        var addedLayer = addedLine != null && IsUsableLayer(doc, addedLine.Attributes.LayerIndex)
          ? doc.Layers[addedLine.Attributes.LayerIndex]
          : null;
        Log.Write(
          "vLine",
          $"added line id={lineId} visible={addedLine?.Visible.ToString() ?? "unknown"} " +
          $"layer={addedLayer?.FullPath ?? "unknown"} " +
          $"layerVisible={addedLayer?.IsVisible.ToString() ?? "unknown"} " +
          $"layerLocked={addedLayer?.IsLocked.ToString() ?? "unknown"}");
        if (addedLine?.Visible == false)
        {
          RhinoApp.WriteLine(
            $"vLine: line created on hidden layer \"{addedLayer?.FullPath ?? "unknown"}\".");
        }
        lastSegmentVector = endPoint - segmentStart;
        if (selectedChainMode is ModeChained or ModeMultiple)
        {
          lineHistory.Push((segmentStart, endPoint, lineId));
          lineRedoData.Clear();
          pendingRedoEnds.Clear();
        }
      }

      doc.Views.Redraw();
      firstSegment = false;

      if (selectedChainMode == ModeMultiple)
      {
        var undoneByMultiple = false;
        while (true)
        {
          var canUndoStart = lineHistory.Count > 0;
          var newStartResult = ResolveFirstPoint(
            doc, layerSession, initialBothSides, selectedChainMode, mode, canUndoStart,
            canRedo: lineRedoData.Count > 0);

          if (newStartResult.Completed)
            return Result.Success;

          if (newStartResult.IsUndo && lineHistory.TryPop(out var lastSeg))
          {
            lineRedoData.Push((lastSeg.segStart, lastSeg.segEnd));
            pendingRedoEnds.Clear();
            DeleteObjectIfValid(doc, lastSeg.lineId);
            currentStart = lastSeg.segStart;
            lastSegmentVector = lineHistory.Count > 0
              ? (Vector3d?)(lineHistory.Peek().segEnd - lineHistory.Peek().segStart)
              : null;
            startConstraintState = null;
            startDirectionState  = null;
            startFeedbackState   = null;
            doc.Views.Redraw();
            undoneByMultiple = true;
            break;
          }

          if (newStartResult.IsRedo && lineRedoData.TryPop(out var redoStart))
          {
            pendingRedoEnds.Push(redoStart.segEnd);
            currentStart = redoStart.segStart;
            lastSegmentVector = null; startConstraintState = null; startDirectionState = null; startFeedbackState = null;
            break;
          }

          if (!newStartResult.HasPoint)
          {
            SavePersistedOptions();
            return Result.Cancel;
          }

          currentStart = newStartResult.Point;
          startConstraintState = newStartResult.Constraint;
          startDirectionState = newStartResult.Direction;
          startFeedbackState = newStartResult.FeedbackGeometry;
          chainModeState = newStartResult.ChainMode;
          firstSegment = true;
          lastSegmentVector = null;
          continueChain = true;
          break;
        }

        if (undoneByMultiple) continue;
        SavePersistedOptions();
        continue;
      }

      currentStart = endPoint;
      startConstraintState = null;
      startDirectionState = null;
      startFeedbackState = null;
      chainModeState = selectedChainMode;
      if (!persistConstraintState)
        constraintModeState = null;

      continueChain = chainModeState != ModeSingle;
      SavePersistedOptions();
    }

    if (polylinePoints is { Count: > 1 } && (tempPolylineId == Guid.Empty || doc.Objects.FindId(tempPolylineId) == null))
      _ = doc.Objects.AddPolyline(
        new Polyline(polylinePoints), layerSession.CreateAttributes(doc));

    doc.Views.Redraw();
    return Result.Success;
    }
    finally
    {
      undoSession.QueueFinalization();
    }
  }

  private static void LoadPersistedOptions()
  {
    var values = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var chainMode = _chainMode;
        var priority = _priority;
        var persistConstraint = _persistConstraint;
        var length = _length;
        var angle = _angle;
        var angleRelative = _angleRelative;
        var layer = _layer;

        if (ToolsOptionStore.TryGetDouble(section, ChainModeKey, out var persistedChain))
          chainMode = ClampIndex((int)Math.Round(persistedChain, MidpointRounding.AwayFromZero), ChainModeValues.Length);
        if (ToolsOptionStore.TryGetDouble(section, PriorityKey, out var persistedPriority))
          priority = ClampIndex((int)Math.Round(persistedPriority, MidpointRounding.AwayFromZero), PriorityValues.Length);
        if (ToolsOptionStore.TryGetBool(section, PersistConstraintKey, out var persistedPersist))
          persistConstraint = persistedPersist;
        if (ToolsOptionStore.TryGetBool(section, AngleRelativeKey, out var persistedAngleRelative))
          angleRelative = persistedAngleRelative;
        if (ToolsOptionStore.TryGetString(section, LayerKey, out var persistedLayer))
          layer = NormalizeLayerOption(persistedLayer);

        return (chainMode, priority, persistConstraint, length, angle, angleRelative, layer);
      });

    _chainMode = ClampIndex(values.chainMode, ChainModeValues.Length);
    _priority = ClampIndex(values.priority, PriorityValues.Length);
    _persistConstraint = values.persistConstraint;
    _length = values.length;
    _angle = values.angle;
    _angleRelative = values.angleRelative;
    _layer = NormalizeLayerOption(values.layer);
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[ChainModeKey] = _chainMode;
        section[PriorityKey] = _priority;
        section[PersistConstraintKey] = _persistConstraint;
        section[AngleRelativeKey] = _angleRelative;
        section[LayerKey] = _layer;
      });
  }

  private static void PromptForLayer(
    RhinoDoc doc,
    LineLayerSession layerSession,
    RunMode runMode)
  {
    if (!LayerSelector.TrySelect(
          doc,
          layerSession.OptionLayerName,
          CurrentLayerOption,
          "vLine target layer",
          runMode,
          allowNewLayer: false,
          out var resolvedLayer))
      return;

    _layer = resolvedLayer;
    layerSession.ApplyOption(doc, resolvedLayer);
    SavePersistedOptions();
  }

  private static string NormalizeLayerOption(string? layerName)
  {
    var value = layerName?.Trim();
    if (string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, CurrentLayerOption, StringComparison.OrdinalIgnoreCase) ||
        value == "." || value == "*")
    {
      return CurrentLayerOption;
    }

    return value;
  }

  private static bool IsUsableLayer(RhinoDoc doc, int layerIndex)
  {
    if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
      return false;

    var layer = doc.Layers[layerIndex];
    return layer != null && !layer.IsDeleted;
  }

  private static FirstPointResult ResolveFirstPoint(
    RhinoDoc doc,
    LineLayerSession layerSession,
    bool initialBothSides,
    int initialChainMode,
    RunMode runMode,
    bool canUndo = false,
    bool canRedo = false)
  {
    var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.AcceptNothing(true);
    if (canUndo || canRedo) getPoint.AcceptCustomMessage(true);
    getPoint.DynamicDraw += (_, e) =>
      DrawHiddenLayerWarning(e, doc, layerSession);
    var bothSides = new OptionToggle(initialBothSides, "No", "Yes");
    var chainModeIndex = ClampIndex(initialChainMode, ChainModeValues.Length);

    while (true)
    {
      getPoint.SetCommandPrompt(
        layerSession.DecoratePrompt(doc, "Start of line"));
      getPoint.ClearCommandOptions();
      var chainModeOptionIndex = getPoint.AddOptionList(
        "Mode", ChainModeValues, chainModeIndex);
      getPoint.AddOptionToggle("BothSides", ref bothSides);

      var idxNormal = getPoint.AddOption("Normal");
      var idxAngled = getPoint.AddOption("Angled");
      var idxVertical = getPoint.AddOption("Vertical");
      var idxFourPoint = getPoint.AddOption("FourPoint");
      var idxBisector = getPoint.AddOption("Bisector");
      var idxPerp = getPoint.AddOption("Perpendicular");
      var idxTangent = getPoint.AddOption("Tangent");
      var idxBiTangent = getPoint.AddOption("BiTangent");
      var idxExtension = getPoint.AddOption("Extension");
      var idxParallel = getPoint.AddOption("Parallel");
      var layerOptionIndex = getPoint.AddOption(
        "Layer", layerSession.OptionLayerName);

      var result = getPoint.Get();
      layerSession.ObserveCurrentLayer(doc);

      if (result == GetResult.Undo)
        return FirstPointResult.Undo(bothSides.CurrentValue, chainModeIndex);

      if (result == GetResult.CustomMessage && getPoint.CustomMessage() is string cm)
      {
        if (cm == "undo" && canUndo)
          return FirstPointResult.Undo(bothSides.CurrentValue, chainModeIndex);
        if (cm == "redo" && canRedo)
          return FirstPointResult.Redo(bothSides.CurrentValue, chainModeIndex);
        continue;
      }

      if (result == GetResult.Point)
      {
        _chainMode = chainModeIndex;
        return FirstPointResult.WithPoint(getPoint.Point(), bothSides.CurrentValue, chainModeIndex);
      }

      if (result == GetResult.Nothing)
        return FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);

      if (result == GetResult.Option)
      {
        var option = getPoint.Option();
        if (option == null)
          continue;

        if (option.Index == layerOptionIndex)
        {
          PromptForLayer(doc, layerSession, runMode);
          continue;
        }

        if (option.Index == idxBiTangent)
        {
          _chainMode = chainModeIndex;
          return RunBiTangent(doc, layerSession)
            ? FirstPointResult.CompletedResult(bothSides.CurrentValue, chainModeIndex)
            : FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);
        }

        if (option.Index == idxPerp || option.Index == idxTangent)
        {
          var constraintKind = option.Index == idxPerp
            ? EndpointConstraintKind.Perpendicular
            : EndpointConstraintKind.Tangent;
          var picked = PickCurveWithPoint(
            constraintKind == EndpointConstraintKind.Perpendicular
              ? "Select curve near perpendicular start"
              : "Select curve near tangent start",
            layerSession,
            constraintKind);
          if (picked == null)
            return FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);

          var curve = picked.Value.Curve;
          var hintPoint = picked.Value.PickPoint;
          if (!curve.ClosestPoint(hintPoint, out var seedParameter))
            seedParameter = curve.Domain.Mid;

          Log.Write(
            "vLine",
            $"selected {constraintKind} start hint={hintPoint} seed={seedParameter:R}");

          _chainMode = chainModeIndex;
          return FirstPointResult.WithConstraint(
            curve.PointAt(seedParameter),
            bothSides.CurrentValue,
            chainModeIndex,
            new EndpointConstraint(
              curve,
              seedParameter,
              hintPoint,
              constraintKind,
              picked.Value.ObjectId,
              picked.Value.ComponentIndex));
        }

        string? directionMode = null;
        if (option.Index == idxNormal) directionMode = "Normal";
        else if (option.Index == idxAngled) directionMode = "Angled";
        else if (option.Index == idxVertical) directionMode = "Vertical";
        else if (option.Index == idxFourPoint) directionMode = "FourPoint";
        else if (option.Index == idxBisector) directionMode = "Bisector";
        else if (option.Index == idxExtension) directionMode = "Extension";
        else if (option.Index == idxParallel) directionMode = "Parallel";

        if (directionMode != null)
        {
          if (!TryGetStartDirectionDefinition(
                doc,
                directionMode,
                out var origin,
                out var direction,
                out var feedbackGeometry))
            return FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);

          Log.Write(
            "vLine",
            $"start direction mode={directionMode} origin={origin} direction={direction}");
          _chainMode = chainModeIndex;
          return FirstPointResult.WithDirection(
            origin,
            direction,
            bothSides.CurrentValue,
            chainModeIndex,
            feedbackGeometry);
        }

        if (option.Index == chainModeOptionIndex)
        {
          chainModeIndex = ClampIndex(option.CurrentListOptionIndex, ChainModeValues.Length);
          _chainMode = chainModeIndex;
          SavePersistedOptions();
        }

        continue;
      }

      return FirstPointResult.None(bothSides.CurrentValue, chainModeIndex);
    }
  }

  private static bool TryGetStartDirectionDefinition(
    RhinoDoc doc,
    string modeName,
    out Point3d origin,
    out Vector3d direction,
    out Curve? feedbackGeometry)
  {
    origin = Point3d.Unset;
    direction = Vector3d.Unset;
    feedbackGeometry = null;
    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    switch (modeName)
    {
      case "Normal":
        return TryPickNormalDefinition(doc, cplane, out origin, out direction);

      case "Angled":
      {
        if (!TryGetPoint("Start of reference line", null, out var referenceStart) ||
            !TryGetPoint("End of reference line", referenceStart, out var referenceEnd))
          return false;

        var reference = referenceEnd - referenceStart;
        if (reference.IsTiny())
          return false;

        var angle = _angle;
        if (RhinoGet.GetNumber("Angle", true, ref angle) != Result.Success)
          return false;
        _angle = angle;
        SavePersistedOptions();

        direction = reference;
        direction.Transform(Transform.Rotation(
          RhinoMath.ToRadians(angle),
          cplane.ZAxis,
          Point3d.Origin));
        origin = referenceStart;
        break;
      }

      case "Vertical":
        if (!TryGetPoint("Start of vertical line", null, out origin))
          return false;
        direction = cplane.ZAxis;
        break;

      case "FourPoint":
      {
        if (!TryGetPoint("Start of reference direction", null, out var referenceStart) ||
            !TryGetPoint("End of reference direction", referenceStart, out var referenceEnd) ||
            !TryGetPoint("Start of line", null, out origin))
          return false;
        direction = referenceEnd - referenceStart;
        break;
      }

      case "Parallel":
      {
        if (!TryGetPoint("Start of reference direction", null, out var referenceStart) ||
            !TryGetPoint("End of reference direction", referenceStart, out var referenceEnd) ||
            !TryGetPoint("Start of line", null, out origin))
          return false;
        direction = referenceEnd - referenceStart;
        break;
      }

      case "Bisector":
      {
        if (!TryGetPoint("Start of bisector line", null, out origin) ||
            !TryGetPoint("First side of angle", origin, out var firstSide) ||
            !TryGetPoint("Second side of angle", origin, out var secondSide))
          return false;

        var first = firstSide - origin;
        var second = secondSide - origin;
        if (!first.Unitize() || !second.Unitize())
          return false;
        direction = first + second;
        if (direction.IsTiny())
        {
          RhinoApp.WriteLine("vLine: the selected angle has no stable bisector.");
          return false;
        }
        break;
      }

      case "Extension":
        return TryPickExtensionDefinition(
          out origin,
          out direction,
          out feedbackGeometry);

      default:
        return false;
    }

    if (!direction.Unitize())
      return false;
    return origin.IsValid;
  }

  private static bool TryGetPoint(
    string prompt,
    Point3d? basePoint,
    out Point3d point)
  {
    point = Point3d.Unset;
    var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.SetCommandPrompt(prompt);
    if (basePoint.HasValue)
    {
      getPoint.SetBasePoint(basePoint.Value, true);
      getPoint.DrawLineFromPoint(basePoint.Value, true);
    }

    if (getPoint.Get() != GetResult.Point)
      return false;
    point = getPoint.Point();
    return point.IsValid;
  }

  private static bool TryPickNormalDefinition(
    RhinoDoc doc,
    Plane cplane,
    out Point3d origin,
    out Vector3d direction)
  {
    origin = Point3d.Unset;
    direction = Vector3d.Unset;
    var picked = PickGeometryWithPoint(
      "Select curve or surface near normal origin",
      ObjectType.Curve | ObjectType.Surface | ObjectType.Brep,
      subObjects: true);
    if (!picked.HasValue)
      return false;

    using var pickedGeometry = picked.Value.Geometry;
    var selectionPoint = picked.Value.PickPoint;
    var curve = pickedGeometry as Curve;
    if (curve != null)
    {
      if (!curve.ClosestPoint(selectionPoint, out var t))
        return false;
      origin = curve.PointAt(t);
      var tangent = curve.TangentAt(t);
      direction = Vector3d.CrossProduct(cplane.ZAxis, tangent);
      return direction.Unitize();
    }

    var surface = pickedGeometry as Surface;
    if (surface == null && pickedGeometry is Brep brep && brep.Faces.Count == 1)
      surface = brep.Faces[0];
    if (surface == null)
      return false;

    if (!surface.ClosestPoint(selectionPoint, out var u, out var v))
      return false;

    origin = surface.PointAt(u, v);
    direction = surface.NormalAt(u, v);
    if (!direction.Unitize())
      return false;

    var viewDirection = doc.Views.ActiveView?.ActiveViewport.CameraDirection ?? Vector3d.Unset;
    if (viewDirection.IsValid && direction * viewDirection > 0.0)
      direction = -direction;
    return true;
  }

  private static bool TryPickExtensionDefinition(
    out Point3d origin,
    out Vector3d direction,
    out Curve? feedbackGeometry)
  {
    origin = Point3d.Unset;
    direction = Vector3d.Unset;
    feedbackGeometry = null;
    var picked = PickCurveWithPoint("Select curve near end to extend");
    if (picked == null || picked.Value.Curve.IsClosed)
      return false;

    var curve = picked.Value.Curve;
    feedbackGeometry = curve;
    var pickPoint = picked.Value.PickPoint;
    var useStart = pickPoint.DistanceToSquared(curve.PointAtStart) <=
                   pickPoint.DistanceToSquared(curve.PointAtEnd);
    origin = useStart ? curve.PointAtStart : curve.PointAtEnd;
    direction = useStart ? -curve.TangentAtStart : curve.TangentAtEnd;
    return direction.Unitize();
  }

  private static bool TryGetEndDirectionDefinition(
    RhinoDoc doc,
    string modeName,
    Point3d lineStart,
    out Vector3d direction)
  {
    direction = Vector3d.Unset;
    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    switch (modeName)
    {
      case "Angled":
      {
        if (!TryGetPoint("Start of reference line", null, out var referenceStart) ||
            !TryGetPoint("End of reference line", referenceStart, out var referenceEnd))
          return false;
        direction = referenceEnd - referenceStart;
        if (direction.IsTiny())
          return false;

        var angle = _angle;
        if (RhinoGet.GetNumber("Angle", true, ref angle) != Result.Success)
          return false;
        _angle = angle;
        SavePersistedOptions();
        direction.Transform(Transform.Rotation(
          RhinoMath.ToRadians(angle),
          cplane.ZAxis,
          Point3d.Origin));
        break;
      }

      case "Vertical":
        direction = cplane.ZAxis;
        break;

      case "FourPoint":
      {
        if (!TryGetPoint("Start of reference direction", null, out var referenceStart) ||
            !TryGetPoint("End of reference direction", referenceStart, out var referenceEnd))
          return false;
        direction = referenceEnd - referenceStart;
        break;
      }

      case "Bisector":
      {
        if (!TryGetPoint("First side of angle", lineStart, out var firstSide) ||
            !TryGetPoint("Second side of angle", lineStart, out var secondSide))
          return false;
        var first = firstSide - lineStart;
        var second = secondSide - lineStart;
        if (!first.Unitize() || !second.Unitize())
          return false;
        direction = first + second;
        break;
      }

      default:
        return false;
    }

    return direction.Unitize();
  }

  private static SecondPointResult ResolveSecondPoint(
    RhinoDoc doc,
    Point3d startPoint,
    bool initialBothSides,
    double initialLength,
    string? initialMode,
    int initialChainMode,
    bool initialPersistConstraint,
    int initialPriority,
    bool initialAngleLock,
    double initialAngle,
    bool initialAngleRelative,
    Vector3d? referenceVector,
    EndpointConstraint? startConstraint,
    Vector3d? startDirection,
    GeometryBase? startFeedbackGeometry,
    bool initialFromFirstPoint,
    LineLayerSession layerSession,
    RunMode runMode,
    bool canUndo = false,
    bool canRedo = false)
  {
    var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.EnableSnapToCurves(true);
    getPoint.SetBasePoint(startPoint, true);
    getPoint.AcceptNumber(true, true);
    getPoint.AcceptNothing(true);
    getPoint.AcceptCustomMessage(true);

    var bothSides = new OptionToggle(initialBothSides, "No", "Yes");
    var chainModeIndex = ClampIndex(initialChainMode, ChainModeValues.Length);
    var priorityIndex = ClampIndex(initialPriority, PriorityValues.Length);

    var lengthOption = new OptionDouble(initialLength);
    var angleOption = new OptionDouble(initialAngle);
    var persistConstraint = new OptionToggle(initialPersistConstraint, "No", "Yes");
    var angleLock = new OptionToggle(initialAngleLock, "No", "Yes");
    var angleRelative = new OptionToggle(initialAngleRelative, "Absolute", "Relative");
    var debugToggle = new OptionToggle(_debugMode, "Off", "On");
    var mode = initialMode;
    var originalStartPoint = startPoint;
    var originalStartConstraint = startConstraint;
    var fromFirstPoint = initialFromFirstPoint;
    var fromPointActive = false;
    Vector3d? parallelDir = startDirection;
    GeometryBase? projectToGeometry = null;
    EndAnchor? endAnchor = null;
    EndpointConstraint? activeEndConstraint = null;
    string? lastPreviewException = null;

    var cacheState = new CurveCacheState(CollectCurveCache(doc), DateTime.UtcNow.AddMilliseconds(500));
    string? lastAutoChoice = null;
    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    ScreenCurvePick? hoveredConstraintPick = null;
    Curve? hoveredConstraintFallbackCurve = null;
    var hoveredConstraintFallbackPoint = Point3d.Unset;
    System.Drawing.Point constraintHoverHitWindow = System.Drawing.Point.Empty;
    var constraintHoverLogged = false;
    var constraintHoverMoveCount = 0;
    Guid constraintHoverLoggedId = Guid.Empty;
    var constraintHoverLoggedComponent = ComponentIndex.Unset;
    var constraintHoverLoggedFallback = false;
    string? lastResolveFailure = null;
    Curve? fallbackPairEndCurve = null;
    EndpointConstraintKind? fallbackPairEndKind = null;
    var fallbackPairLine = Line.Unset;
    var lastPreviewResolvedStart = Point3d.Unset;
    var lastPreviewResolvedEnd   = Point3d.Unset;

    var sourceFeedbackGeometry = startConstraint.HasValue
      ? startConstraint.Value.Curve
      : startFeedbackGeometry;
    using var sourceHighlight = sourceFeedbackGeometry != null
      ? TemporaryGeometryHighlight.Create(
          doc,
          sourceFeedbackGeometry,
          SourceFeedbackColor)
      : null;
    TemporaryGeometryHighlight? projectTargetHighlight = null;

    Curve? HoveredConstraintCurve()
      => hoveredConstraintPick.HasValue
        ? CurveFromScreenPick(doc, hoveredConstraintPick.Value)
        : hoveredConstraintFallbackCurve;

    Point3d HoveredConstraintPoint(Point3d fallback)
      => hoveredConstraintPick.HasValue
        ? hoveredConstraintPick.Value.PickPoint
        : hoveredConstraintFallbackPoint.IsValid
          ? hoveredConstraintFallbackPoint
          : fallback;

    bool TryUseNativeConstraintSnap(Point3d currentPoint)
    {
      var pointObject = getPoint.PointOnObject();
      var curve = pointObject?.Curve();
      if (curve == null)
        return false;

      var hint = currentPoint;
      if (curve.ClosestPoint(currentPoint, out var parameter))
        hint = curve.PointAt(parameter);
      hoveredConstraintPick = new ScreenCurvePick(
        pointObject!.ObjectId,
        pointObject.GeometryComponentIndex,
        hint);
      hoveredConstraintFallbackCurve = null;
      hoveredConstraintFallbackPoint = Point3d.Unset;
      return true;
    }

    bool TryUseWorldConstraintFallback(
      Rhino.Display.RhinoViewport viewport,
      Point3d currentPoint)
    {
      MaybeRefreshCurveCache(false);
      var capturePixels = Math.Max(
        6.0,
        Rhino.ApplicationSettings.ModelAidSettings.MousePickboxRadius + 2.0);
      var captureTolerance = doc.ModelAbsoluteTolerance * 4.0;
      if (viewport.GetWorldToScreenScale(currentPoint, out var pixelsPerUnit) &&
          pixelsPerUnit > RhinoMath.SqrtEpsilon)
      {
        captureTolerance = Math.Max(
          captureTolerance,
          capturePixels / pixelsPerUnit);
      }

      var curve = CurveAtCursorPoint(
        currentPoint,
        cacheState.CurveCache,
        captureTolerance);
      if (curve == null || !curve.ClosestPoint(currentPoint, out var parameter))
        return false;

      hoveredConstraintPick = null;
      hoveredConstraintFallbackCurve = curve;
      hoveredConstraintFallbackPoint = curve.PointAt(parameter);
      return true;
    }

    EventHandler<GetPointMouseEventArgs> trackConstraintHover = (_, e) =>
    {
      if (mode is not ("perp" or "tangent"))
      {
        hoveredConstraintPick = null;
        hoveredConstraintFallbackCurve = null;
        hoveredConstraintFallbackPoint = Point3d.Unset;
        return;
      }

      constraintHoverMoveCount++;
      var nextPick = PickCurveAtScreenPoint(
        doc,
        e.Viewport,
        e.WindowPoint,
        out var diagnostic);
      if (nextPick.HasValue)
      {
        hoveredConstraintPick = nextPick;
        hoveredConstraintFallbackCurve = null;
        hoveredConstraintFallbackPoint = Point3d.Unset;
        constraintHoverHitWindow = e.WindowPoint;
      }
      else if (TryUseNativeConstraintSnap(e.Point) ||
               TryUseWorldConstraintFallback(e.Viewport, e.Point))
      {
        constraintHoverHitWindow = e.WindowPoint;
        diagnostic += hoveredConstraintPick.HasValue
          ? $" native={hoveredConstraintPick.Value.ObjectId}"
          : " world-fallback";
      }
      else if ((hoveredConstraintPick.HasValue ||
                hoveredConstraintFallbackCurve != null) &&
               ScreenDistanceSquared(e.WindowPoint, constraintHoverHitWindow) <= 100)
      {
        diagnostic += hoveredConstraintPick.HasValue
          ? $" retained={hoveredConstraintPick.Value.ObjectId}"
          : " retained=world-fallback";
      }
      else
      {
        hoveredConstraintPick = null;
        hoveredConstraintFallbackCurve = null;
        hoveredConstraintFallbackPoint = Point3d.Unset;
      }
      var hoveredId = hoveredConstraintPick?.ObjectId ?? Guid.Empty;
      var hoveredComponent = hoveredConstraintPick?.ComponentIndex ?? ComponentIndex.Unset;
      var usingFallback = hoveredConstraintFallbackCurve != null;
      if (!constraintHoverLogged ||
          hoveredId != constraintHoverLoggedId ||
          hoveredComponent != constraintHoverLoggedComponent ||
          usingFallback != constraintHoverLoggedFallback)
      {
        constraintHoverLogged = true;
        constraintHoverLoggedId = hoveredId;
        constraintHoverLoggedComponent = hoveredComponent;
        constraintHoverLoggedFallback = usingFallback;
        Log.Write(
          "vLine.Hover",
          $"mode={mode} move={constraintHoverMoveCount} window={e.WindowPoint} " +
          $"world={e.Point} result={diagnostic}");
      }
    };
    getPoint.MouseMove += trackConstraintHover;

    void ApplyNativeDirectionConstraint()
    {
      getPoint.ClearConstraints();

      // Without a native 3-D constraint, CPlane Z projects back onto the
      // CPlane and collapses a vertical preview to the start point.
      if (startConstraint.HasValue)
        return;

      var activeDirection = startDirection ??
                            (mode is "parallel" or "extension_direction" ? parallelDir : null);
      if (!activeDirection.HasValue)
        return;

      var direction = activeDirection.Value;
      if (!direction.Unitize())
        return;

      getPoint.Constrain(startPoint, startPoint + direction);
    }

    void ApplyModePrompt()
    {
      ApplyNativeDirectionConstraint();
      if (mode != "project_to")
      {
        projectTargetHighlight?.Dispose();
        projectTargetHighlight = null;
      }

      string Prompt(string value)
      {
        var lockSuffix = fromFirstPoint
          ? fromPointActive
            ? " [FromPoint]"
            : " [FromFirstPoint]"
          : string.Empty;
        return layerSession.DecoratePrompt(doc, value + lockSuffix);
      }

      if (mode == "perp")
        getPoint.SetCommandPrompt(Prompt("End point of line (Perpendicular mode: hover near curve, click to accept)"));
      else if (mode == "tangent")
        getPoint.SetCommandPrompt(Prompt("End point of line (Tangent mode: hover near curve, click to accept)"));
      else if (mode == "perp_any")
        getPoint.SetCommandPrompt(Prompt("End point of line (PerpNear mode: solves against nearest curve)"));
      else if (mode == "tangent_any")
        getPoint.SetCommandPrompt(Prompt("End point of line (TanNear mode: solves against nearest curve)"));
      else if (mode == "auto")
        getPoint.SetCommandPrompt(Prompt("End point of line (Auto mode: priority chooses Perp/Tangent)"));
      else if (mode == "parallel")
        getPoint.SetCommandPrompt(Prompt("End point of line (Parallel)"));
      else if (mode == "extension_direction")
        getPoint.SetCommandPrompt(Prompt("End point of line (Extension)"));
      else if (mode == "project_to")
        getPoint.SetCommandPrompt(Prompt("End point of line (ProjectTo: endpoint snaps to nearest point on target geometry)"));
      else if (mode == "end_anchor")
        getPoint.SetCommandPrompt(Prompt("Click to accept constrained end point"));
      else if (startDirection.HasValue)
        getPoint.SetCommandPrompt(Prompt("End point of line (start direction constrained)"));
      else
        getPoint.SetCommandPrompt(Prompt("End point of line"));
    }

    void MaybeRefreshCurveCache(bool force)
    {
      var now = DateTime.UtcNow;
      if (!force && now < cacheState.NextRefreshUtc)
        return;

      cacheState.CurveCache = CollectCurveCache(doc);
      cacheState.NextRefreshUtc = now.AddMilliseconds(500);
    }

    Point3d ApplyAnglePointFromCurrent(Point3d segmentStart, Point3d currentPoint)
    {
      if (!angleLock.CurrentValue)
        return currentPoint;

      var baseVec = cplane.XAxis;
      if (angleRelative.CurrentValue && referenceVector.HasValue)
      {
        var rv = referenceVector.Value;
        if (!rv.IsTiny())
          baseVec = rv;
      }

      var base2 = ToCPlane2d(baseVec, cplane);
      if (!TryUnitize2d(base2, out var base2u))
        base2u = new Vector2d(1.0, 0.0);

      var radians = RhinoMath.ToRadians(angleOption.CurrentValue);
      var cosA = Math.Cos(radians);
      var sinA = Math.Sin(radians);
      var dir2 = new Vector2d((base2u.X * cosA) - (base2u.Y * sinA), (base2u.X * sinA) + (base2u.Y * cosA));
      if (!TryUnitize2d(dir2, out var dir2u))
        return currentPoint;

      var dir3 = (cplane.XAxis * dir2u.X) + (cplane.YAxis * dir2u.Y);
      if (dir3.IsTiny())
        return currentPoint;

      var toCursor = currentPoint - segmentStart;
      var dist = toCursor.Length;
      if (dist < doc.ModelAbsoluteTolerance)
        dist = doc.ModelAbsoluteTolerance;

      var sign = Vector3d.Multiply(toCursor, dir3) < 0.0 ? -1.0 : 1.0;
      return segmentStart + (dir3 * (dist * sign));
    }

    Point3d PreviewEndFromCurrent(
      Point3d segmentStart,
      Point3d currentPoint,
      bool preserveEndConstraint,
      bool applyAngle)
    {
      var endPoint = applyAngle
        ? ApplyAnglePointFromCurrent(segmentStart, currentPoint)
        : currentPoint;
      if (!preserveEndConstraint && Math.Abs(lengthOption.CurrentValue) > doc.ModelAbsoluteTolerance)
      {
        var direction = endPoint - segmentStart;
        if (direction.Unitize())
          endPoint = segmentStart + direction * lengthOption.CurrentValue;
      }

      return endPoint;
    }

    Point3d? EndpointForMode(string? modeName, Point3d cursorPoint, bool preview)
    {
      if (string.IsNullOrWhiteSpace(modeName))
        return null;

      if (modeName == "parallel")
      {
        if (!parallelDir.HasValue)
          return null;

        MaybeRefreshCurveCache(false);
        var parallelCache = cacheState.CurveCache;

        var proj = Vector3d.Multiply(cursorPoint - startPoint, parallelDir.Value);
        var constrainedPt = startPoint + (parallelDir.Value * proj);

        if (parallelCache.Count > 0)
        {
          var snapPt = FindParallelRaySnap(startPoint, parallelDir.Value, cursorPoint, parallelCache, doc.ModelAbsoluteTolerance);
          if (snapPt.HasValue)
          {
            return snapPt.Value;
          }
        }
        return constrainedPt;
      }

      if (modeName == "project_to")
        return projectToGeometry != null ? ProjectClosestPoint(projectToGeometry, cursorPoint, cplane) : null;

      MaybeRefreshCurveCache(false);
      var curveCache = cacheState.CurveCache;

      if (curveCache.Count == 0)
      {
        DebugLog($"EndpointForMode({modeName}): no curves in cache");
        return null;
      }

      // Direct endpoint constraints resolve only against the curve under the cursor.
      Curve? curve;
      if (modeName is "perp" or "tangent")
      {
        curve = HoveredConstraintCurve();
        if (curve == null)
        {
          DebugLog($"EndpointForMode({modeName}): no curve under cursor");
          return null;
        }
      }
      else
      {
        curve = NearestCurveToPoint(cursorPoint, curveCache);
      }

      if (curve == null)
      {
        DebugLog($"EndpointForMode({modeName}): no curve resolved");
        return null;
      }

      var curveHint = modeName is "perp" or "tangent"
        ? HoveredConstraintPoint(cursorPoint)
        : cursorPoint;

      if (modeName is "perp" or "perp_any")
      {
        DebugLog($"PerpNear: hint=({curveHint.X:F3},{curveHint.Y:F3},{curveHint.Z:F3}) curve={curve.GetType().Name} preview={preview}");
        var pt = PerpPointFromStartWithHint(startPoint, curve, curveHint, preview ? 80 : 240, preview ? 16 : 18);
        if (pt.HasValue)
        {
          DebugLog($"PerpNear: found ({pt.Value.X:F3},{pt.Value.Y:F3},{pt.Value.Z:F3})");
          return pt.Value;
        }
        DebugLog("PerpNear: null -> trying fallback");
        var fb = PerpFallbackToPointedSegment(startPoint, curve, curveHint, preview);
        DebugLog($"PerpNear: fallback={(fb.HasValue ? $"({fb.Value.X:F3},{fb.Value.Y:F3},{fb.Value.Z:F3})" : "null")}");
        return fb;
      }

      if (modeName is "tangent" or "tangent_any")
        return TangentPointFromStart(startPoint, curve, curveHint, preview ? 80 : 240, preview ? 16 : 18);

      if (modeName == "auto")
      {
        var perp = PerpPointFromStartWithHint(startPoint, curve, cursorPoint, preview ? 80 : 240, preview ? 16 : 18);
        var tan = TangentPointFromStart(startPoint, curve, cursorPoint, preview ? 80 : 240, preview ? 16 : 18);

        if (!perp.HasValue)
          perp = PerpFallbackToPointedSegment(startPoint, curve, cursorPoint, preview);

        if (!perp.HasValue && !tan.HasValue)
          return null;

        if (priorityIndex == PriorityPerpFirst)
        {
          if (perp.HasValue)
          {
            lastAutoChoice = "perp";
            return perp.Value;
          }

          lastAutoChoice = "tangent";
          return tan;
        }

        if (priorityIndex == PriorityTanFirst)
        {
          if (tan.HasValue)
          {
            lastAutoChoice = "tangent";
            return tan.Value;
          }

          lastAutoChoice = "perp";
          return perp;
        }

        if (priorityIndex == PriorityKeepCurrent)
        {
          if (lastAutoChoice == "perp" && perp.HasValue)
            return perp.Value;
          if (lastAutoChoice == "tangent" && tan.HasValue)
            return tan.Value;
        }

        if (!perp.HasValue)
        {
          lastAutoChoice = "tangent";
          return tan;
        }

        if (!tan.HasValue)
        {
          lastAutoChoice = "perp";
          return perp;
        }

        if (perp.Value.DistanceToSquared(cursorPoint) <= tan.Value.DistanceToSquared(cursorPoint))
        {
          lastAutoChoice = "perp";
          return perp.Value;
        }

        lastAutoChoice = "tangent";
        return tan.Value;
      }

      return null;
    }

    EndpointConstraint? EndConstraintForDisplay(
      string? modeName,
      Point3d solvedEnd,
      Point3d cursorPoint)
    {
      EndpointConstraintKind kind;
      Curve? curve;
      if (modeName is "perp" or "tangent")
      {
        kind = modeName == "perp"
          ? EndpointConstraintKind.Perpendicular
          : EndpointConstraintKind.Tangent;
        curve = HoveredConstraintCurve();
      }
      else if (modeName is "perp_any" or "tangent_any" or "auto")
      {
        kind = modeName == "perp_any" || lastAutoChoice == "perp"
          ? EndpointConstraintKind.Perpendicular
          : EndpointConstraintKind.Tangent;
        curve = NearestCurveToPoint(cursorPoint, cacheState.CurveCache);
      }
      else
      {
        return null;
      }

      if (curve == null || !curve.ClosestPoint(solvedEnd, out var seed))
        return null;
      return new EndpointConstraint(curve, seed, cursorPoint, kind);
    }

    bool TryResolveSegment(
      Point3d cursorPoint,
      bool preview,
      out Point3d resolvedStart,
      out Point3d resolvedEnd)
    {
      resolvedStart = startPoint;
      resolvedEnd = Point3d.Unset;
      activeEndConstraint = null;
      lastResolveFailure = null;

      var activeDirection = startDirection ??
                            (mode is "parallel" or "extension_direction" ? parallelDir : null);

      if (endAnchor.HasValue)
      {
        var anchor = endAnchor.Value;
        if (startConstraint.HasValue)
        {
          if (fromFirstPoint)
          {
            resolvedStart = startConstraint.Value.Curve.PointAt(
              startConstraint.Value.SeedParameter);
          }
          else if (!TryResolveConstraintToPoint(
                     startConstraint.Value,
                     anchor.Point,
                     preview,
                     out resolvedStart))
          {
            return false;
          }
        }

        resolvedEnd = anchor.Point;
        var lineDirection = resolvedEnd - resolvedStart;
        if (startConstraint.HasValue &&
            fromFirstPoint &&
            !DirectionMatchesConstraintAtSeed(startConstraint.Value, lineDirection))
          return false;
        if (!DirectionsAreParallel(lineDirection, anchor.Direction, 0.02))
          return false;
        if (activeDirection.HasValue &&
            !DirectionsAreParallel(lineDirection, activeDirection.Value, 0.02))
          return false;
        return lineDirection.Length > doc.ModelAbsoluteTolerance;
      }

      if (!startConstraint.HasValue)
      {
        var rawEnd = cursorPoint;
        if (!string.IsNullOrWhiteSpace(mode))
        {
          var endpointForMode = EndpointForMode(mode, cursorPoint, preview);
          if (!endpointForMode.HasValue)
            return false;
          rawEnd = endpointForMode.Value;
          activeEndConstraint = EndConstraintForDisplay(mode, rawEnd, cursorPoint);
        }

        if (activeDirection.HasValue)
        {
          var direction = activeDirection.Value;
          if (!direction.Unitize())
            return false;

          if (string.IsNullOrWhiteSpace(mode) ||
              mode is "parallel" or "extension_direction")
          {
            var distance = Vector3d.Multiply(cursorPoint - resolvedStart, direction);
            rawEnd = resolvedStart + (direction * distance);
          }
          else
          {
            var lineDirection = rawEnd - resolvedStart;
            if (!DirectionsAreParallel(lineDirection, direction, 0.02))
              return false;
          }
        }

        resolvedEnd = PreviewEndFromCurrent(
          resolvedStart,
          rawEnd,
          preserveEndConstraint: false,
          applyAngle: !activeDirection.HasValue);
        return resolvedEnd.IsValid;
      }

      var firstConstraint = startConstraint.Value;
      if (string.IsNullOrWhiteSpace(mode) || mode == "project_to")
      {
        var rawEnd = mode == "project_to"
          ? projectToGeometry != null
            ? ProjectClosestPoint(projectToGeometry, cursorPoint, cplane)
            : null
          : cursorPoint;
        if (!rawEnd.HasValue)
          return false;

        if (fromFirstPoint)
        {
          resolvedStart = firstConstraint.Curve.PointAt(firstConstraint.SeedParameter);
          if (string.IsNullOrWhiteSpace(mode))
          {
            if (!TryConstrainEndpointFromFixedStart(
                  firstConstraint,
                  resolvedStart,
                  rawEnd.Value,
                  out var fixedEnd))
              return false;
            rawEnd = fixedEnd;
          }
          else if (!DirectionMatchesConstraintAtSeed(
                     firstConstraint,
                     rawEnd.Value - resolvedStart))
          {
            return false;
          }
        }
        else if (!TryResolveConstraintToPoint(
                   firstConstraint,
                   rawEnd.Value,
                   preview,
                   out resolvedStart))
        {
          return false;
        }

        resolvedEnd = PreviewEndFromCurrent(
          resolvedStart,
          rawEnd.Value,
          preserveEndConstraint: false,
          applyAngle: false);
        return resolvedEnd.IsValid;
      }

      if (activeDirection.HasValue &&
          (string.IsNullOrWhiteSpace(mode) ||
           mode is "parallel" or "extension_direction"))
      {
        var direction = activeDirection.Value;
        if (!direction.Unitize())
          return false;

        if (fromFirstPoint)
        {
          resolvedStart = firstConstraint.Curve.PointAt(firstConstraint.SeedParameter);
          if (!DirectionMatchesConstraintAtSeed(firstConstraint, direction))
            return false;
        }
        else if (!TryResolveConstraintToDirection(
                   firstConstraint,
                   direction,
                   preview,
                   out resolvedStart))
        {
          return false;
        }

        var distance = Vector3d.Multiply(cursorPoint - resolvedStart, direction);
        resolvedEnd = PreviewEndFromCurrent(
          resolvedStart,
          resolvedStart + (direction * distance),
          preserveEndConstraint: false,
          applyAngle: false);
        return resolvedEnd.IsValid;
      }

      MaybeRefreshCurveCache(false);
      Curve? endCurve = null;
      if (mode is "perp" or "tangent")
      {
        endCurve = HoveredConstraintCurve();
      }
      else if (mode is "perp_any" or "tangent_any" or "auto")
      {
        endCurve = NearestCurveToPoint(cursorPoint, cacheState.CurveCache);
      }

      var endSearchPoint = mode is "perp" or "tangent"
        ? HoveredConstraintPoint(cursorPoint)
        : cursorPoint;
      if (endCurve == null)
      {
        lastResolveFailure =
          $"no endpoint curve mode={mode} hoverMoves={constraintHoverMoveCount} " +
          $"hoverId={hoveredConstraintPick?.ObjectId.ToString() ?? "none"}";
        return false;
      }

      if (!endCurve.ClosestPoint(endSearchPoint, out var endSeed))
      {
        lastResolveFailure = $"endpoint curve ClosestPoint failed mode={mode} hint={endSearchPoint}";
        return false;
      }

      var endHint = endCurve.PointAt(endSeed);
      bool TryPair(EndpointConstraintKind kind, out Line solvedLine)
      {
        var endConstraint = new EndpointConstraint(
          endCurve,
          endSeed,
          endHint,
          kind);
        if (fromFirstPoint)
        {
          if (TryResolveFixedStartConstraintPair(
                firstConstraint,
                endConstraint,
                preview,
                out solvedLine))
            return true;

          return preview && TryResolveFixedStartConstraintPair(
            firstConstraint,
            endConstraint,
            preview: false,
            out solvedLine);
        }

        if (TryResolveConstraintPair(
              firstConstraint,
              endConstraint,
              preview,
              out solvedLine))
        {
          if (preview)
          {
            fallbackPairEndCurve = endCurve;
            fallbackPairEndKind = kind;
            fallbackPairLine = solvedLine;
          }
          return true;
        }

        if (!preview)
          return false;

        if (ReferenceEquals(fallbackPairEndCurve, endCurve) &&
            fallbackPairEndKind == kind &&
            fallbackPairLine.IsValid)
        {
          solvedLine = fallbackPairLine;
          return true;
        }

        if (!TryResolveConstraintPair(
              firstConstraint,
              endConstraint,
              preview: false,
              out solvedLine,
              writeLog: false))
          return false;

        fallbackPairEndCurve = endCurve;
        fallbackPairEndKind = kind;
        fallbackPairLine = solvedLine;
        return true;
      }

      Line line;
      EndpointConstraintKind selectedEndKind;
      if (mode == "auto")
      {
        var hasPerp = TryPair(EndpointConstraintKind.Perpendicular, out var perpLine);
        var hasTangent = TryPair(EndpointConstraintKind.Tangent, out var tangentLine);
        if (!hasPerp && !hasTangent)
          return false;

        if (priorityIndex == PriorityPerpFirst && hasPerp)
        {
          line = perpLine;
          selectedEndKind = EndpointConstraintKind.Perpendicular;
          lastAutoChoice = "perp";
        }
        else if (priorityIndex == PriorityTanFirst && hasTangent)
        {
          line = tangentLine;
          selectedEndKind = EndpointConstraintKind.Tangent;
          lastAutoChoice = "tangent";
        }
        else if (priorityIndex == PriorityKeepCurrent && lastAutoChoice == "perp" && hasPerp)
        {
          line = perpLine;
          selectedEndKind = EndpointConstraintKind.Perpendicular;
        }
        else if (priorityIndex == PriorityKeepCurrent && lastAutoChoice == "tangent" && hasTangent)
        {
          line = tangentLine;
          selectedEndKind = EndpointConstraintKind.Tangent;
        }
        else if (!hasTangent ||
                 (hasPerp && perpLine.To.DistanceToSquared(cursorPoint) <= tangentLine.To.DistanceToSquared(cursorPoint)))
        {
          line = perpLine;
          selectedEndKind = EndpointConstraintKind.Perpendicular;
          lastAutoChoice = "perp";
        }
        else
        {
          line = tangentLine;
          selectedEndKind = EndpointConstraintKind.Tangent;
          lastAutoChoice = "tangent";
        }
      }
      else
      {
        var endKind = mode is "perp" or "perp_any"
          ? EndpointConstraintKind.Perpendicular
          : EndpointConstraintKind.Tangent;
        if (!TryPair(endKind, out line))
        {
          lastResolveFailure =
            $"pair solver failed start={firstConstraint.Kind} end={endKind} " +
            $"startHint={firstConstraint.HintPoint} endHint={endHint}";
          return false;
        }
        selectedEndKind = endKind;
      }

      resolvedStart = line.From;
      resolvedEnd = PreviewEndFromCurrent(
        resolvedStart,
        line.To,
        preserveEndConstraint: true,
        applyAngle: false);
      if (activeDirection.HasValue &&
          !DirectionsAreParallel(resolvedEnd - resolvedStart, activeDirection.Value, 0.02))
        return false;
      activeEndConstraint = new EndpointConstraint(
        endCurve,
        endSeed,
        cursorPoint,
        selectedEndKind);
      return resolvedStart.IsValid && resolvedEnd.IsValid;
    }

    Color CurrentPreviewColor()
    {
      var baseColor = layerSession.ResolveColor(doc);
      return Color.FromArgb(120, baseColor.R, baseColor.G, baseColor.B);
    }

    EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
    {
      try
      {
        DrawHiddenLayerWarning(e, doc, layerSession);

        var previewColor = CurrentPreviewColor();
        if (mode is "perp" or "tangent")
        {
          if (!hoveredConstraintPick.HasValue &&
              hoveredConstraintFallbackCurve == null)
          {
            _ = TryUseNativeConstraintSnap(e.CurrentPoint) ||
                TryUseWorldConstraintFallback(e.Viewport, e.CurrentPoint);
          }

          var hoveredCurve = HoveredConstraintCurve();
          if (hoveredCurve != null)
          {
            PreviewDisplay.DrawCurve(
              e.Display,
              hoveredCurve,
              HoverFeedbackColor,
              2);
          }
        }

        if (!TryResolveSegment(e.CurrentPoint, preview: true, out var previewStart, out var ep))
        {
          lastPreviewResolvedEnd = Point3d.Unset;
          return;
        }
        lastPreviewResolvedStart = previewStart;
        lastPreviewResolvedEnd   = ep;
        if (bothSides.CurrentValue)
        {
          var vec = ep - previewStart;
          if (vec.IsTiny())
            return;

          var a = previewStart - vec;
          var b = previewStart + vec;
          PreviewDisplay.DrawLine(e.Display, a, b, previewColor);
          e.Display.DrawDottedLine(a, b, previewColor);
        }
        else
        {
          PreviewDisplay.DrawLine(e.Display, previewStart, ep, previewColor);
          e.Display.DrawDottedLine(previewStart, ep, previewColor);
        }

        if (startConstraint.HasValue)
          DrawCurveConstraintHelper(e.Display, doc, startConstraint.Value, previewStart);
        if (activeEndConstraint.HasValue)
          DrawCurveConstraintHelper(e.Display, doc, activeEndConstraint.Value, ep);
        e.Display.DrawPoint(ep, Rhino.Display.PointStyle.RoundSimple, 2, previewColor);
        if (mode == "project_to")
          e.Display.DrawPoint(ep, Rhino.Display.PointStyle.X, 6, Color.Cyan);
        lastPreviewException = null;
      }
      catch (Exception ex)
      {
        var details = ex.ToString();
        if (!string.Equals(details, lastPreviewException, StringComparison.Ordinal))
        {
          lastPreviewException = details;
          Log.Write("vLine.Preview", details);
        }
      }
    };

    getPoint.DynamicDraw += drawPreview;
    ApplyModePrompt();

    try
    {
      while (true)
      {
        getPoint.ClearCommandOptions();
        var idxChainMode = getPoint.AddOptionList(
          "Mode", ChainModeValues, chainModeIndex);
        getPoint.AddOptionToggle("BothSides", ref bothSides);
        var allowDirectionMode = !startDirection.HasValue;
        var allowEndDirectionAnchor = !startDirection.HasValue;
        var allowAngleControls = !startConstraint.HasValue &&
                                 !startDirection.HasValue &&
                                 endAnchor == null &&
                                 string.IsNullOrWhiteSpace(mode);
        var idxNormal = allowEndDirectionAnchor ? getPoint.AddOption("Normal") : -1;
        var idxAngled = allowDirectionMode ? getPoint.AddOption("Angled") : -1;
        var idxVertical = allowDirectionMode ? getPoint.AddOption("Vertical") : -1;
        var idxFourPoint = allowDirectionMode ? getPoint.AddOption("FourPoint") : -1;
        var idxBisector = allowDirectionMode ? getPoint.AddOption("Bisector") : -1;
        var idxPerp = getPoint.AddOption("Perpendicular");
        var idxTan = getPoint.AddOption("Tangent");
        var idxPerpNear = getPoint.AddOption("PerpNear");
        var idxTanNear = getPoint.AddOption("TanNear");
        var idxExtension = allowEndDirectionAnchor ? getPoint.AddOption("Extension") : -1;
        var idxParallel = allowDirectionMode ? getPoint.AddOption("Parallel") : -1;
        var idxPriority = mode == "auto"
          ? getPoint.AddOptionList("Priority", PriorityValues, priorityIndex)
          : -1;
        var idxFromPoint = startConstraint?.Kind == EndpointConstraintKind.Perpendicular
          ? getPoint.AddOption("FromPoint")
          : -1;
        var idxFromFirstPoint = originalStartConstraint.HasValue &&
                                (!fromFirstPoint || fromPointActive)
          ? getPoint.AddOption("FromFirstPoint")
          : -1;
        var idxAuto = getPoint.AddOption("Auto");
        var idxProjectTo = getPoint.AddOption("ProjectTo");
        if (allowAngleControls)
          getPoint.AddOptionToggle("AngleRef", ref angleRelative);
        var idxAngle = allowAngleControls
          ? getPoint.AddOptionDouble("Angle", ref angleOption)
          : -1;
        var idxLength = getPoint.AddOptionDouble("Length", ref lengthOption);
        var idxLayer = getPoint.AddOption("Layer", layerSession.OptionLayerName);
        var idxPersistConstraint = getPoint.AddOption(
          "PersistConstraint",
          persistConstraint.CurrentValue ? "Yes" : "No",
          false);
        var idxDebug = getPoint.AddOption(
          "Debug",
          debugToggle.CurrentValue ? "On" : "Off",
          true);

        if (debugToggle.CurrentValue && !_debugMode)
        {
          _debugMode = true;
          EnsureDebugLog();
          DebugLog($"Debug ON  start=({startPoint.X:F3},{startPoint.Y:F3},{startPoint.Z:F3}) mode={mode ?? "none"} curves={cacheState.CurveCache.Count}");
        }
        else if (!debugToggle.CurrentValue && _debugMode)
        {
          DebugLog("Debug OFF");
          _debugMode = false;
        }

        var result = getPoint.Get();
        layerSession.ObserveCurrentLayer(doc);

        if (result == GetResult.Undo)
        {
          var undoState = new ConstraintState(mode, persistConstraint.CurrentValue, priorityIndex, lengthOption.CurrentValue, angleLock.CurrentValue, angleOption.CurrentValue, angleRelative.CurrentValue);
          return SecondPointResult.Undo(bothSides.CurrentValue, chainModeIndex, undoState);
        }

        if (result == GetResult.CustomMessage && getPoint.CustomMessage() is string customCmd)
        {
          var historyState = new ConstraintState(mode, persistConstraint.CurrentValue, priorityIndex, lengthOption.CurrentValue, angleLock.CurrentValue, angleOption.CurrentValue, angleRelative.CurrentValue);
          if (customCmd == "undo" && canUndo)
            return SecondPointResult.Undo(bothSides.CurrentValue, chainModeIndex, historyState);
          if (customCmd == "redo" && canRedo)
            return SecondPointResult.Redo(bothSides.CurrentValue, chainModeIndex, historyState);
          continue;
        }

        if (result == GetResult.Point)
        {
          var clickedRaw = getPoint.Point();
          if (mode is "perp" or "tangent")
          {
            var pointObject = getPoint.PointOnObject();
            var pointCurve = pointObject?.Curve();
            var pointObjectId = pointCurve != null
              ? pointObject!.ObjectId
              : Guid.Empty;
            Log.Write(
              "vLine.Accept",
              $"mode={mode} click={clickedRaw} hoverId={hoveredConstraintPick?.ObjectId.ToString() ?? "none"} " +
              $"hoverComponent={hoveredConstraintPick?.ComponentIndex.ToString() ?? "none"} " +
              $"pointOnObject={pointObjectId} type={pointCurve?.GetType().Name ?? "none"}");

            if (!hoveredConstraintPick.HasValue && pointCurve != null)
            {
              var pointHint = pointObject!.SelectionPoint();
              if (!pointHint.IsValid)
                pointHint = clickedRaw;
              hoveredConstraintPick = new ScreenCurvePick(
                pointObjectId,
                pointObject!.GeometryComponentIndex,
                pointHint);
              Log.Write(
                "vLine.Accept",
                $"using PointOnObject fallback id={pointObjectId} " +
                $"component={pointObject.GeometryComponentIndex} hint={pointHint}");
            }
          }

          DebugLog($"Click: cursor=({clickedRaw.X:F3},{clickedRaw.Y:F3},{clickedRaw.Z:F3}) mode={mode ?? "free"}");
          Point3d resolvedStart;
          Point3d endPoint;
          if (lastPreviewResolvedEnd.IsValid)
          {
            resolvedStart = lastPreviewResolvedStart;
            endPoint      = lastPreviewResolvedEnd;
          }
          else
          {
            try
            {
              if (!TryResolveSegment(clickedRaw, preview: false, out resolvedStart, out endPoint))
              {
                Log.Write(
                  "vLine",
                  $"accept failed mode={mode ?? "free"} point={clickedRaw} " +
                  $"reason={lastResolveFailure ?? "unspecified"}");
                RhinoApp.WriteLine($"vLine: no valid {mode ?? "endpoint"} solution found at this cursor location.");
                continue;
              }
            }
            catch (Exception ex)
            {
              Log.Write("vLine.Accept", ex.ToString());
              RhinoApp.WriteLine("vLine: failed to resolve the selected endpoint. See vTools.log.");
              continue;
            }
          }

          Log.Write("vLine", $"accept mode={mode ?? "free"} start={resolvedStart} end={endPoint}");
          var state = new ConstraintState(mode, persistConstraint.CurrentValue, priorityIndex, lengthOption.CurrentValue, angleLock.CurrentValue, angleOption.CurrentValue, angleRelative.CurrentValue);
          return SecondPointResult.WithPoint(resolvedStart, endPoint, bothSides.CurrentValue, chainModeIndex, state);
        }

        if (result == GetResult.Option)
        {
          var option = getPoint.Option();
          if (option == null)
            continue;

          if (option.Index == idxFromFirstPoint)
          {
            startConstraint = originalStartConstraint;
            startPoint = originalStartPoint;
            fromFirstPoint = true;
            fromPointActive = false;
            getPoint.SetBasePoint(startPoint, true);
            lastPreviewResolvedStart = Point3d.Unset;
            lastPreviewResolvedEnd = Point3d.Unset;
            Log.Write(
              "vLine",
              $"FromFirstPoint activated start={startPoint} " +
              $"constraint={startConstraint?.Kind.ToString() ?? "none"}");
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxFromPoint && startConstraint.HasValue)
          {
            if (TryPickConstraintStartPoint(
                  doc,
                  layerSession,
                  startConstraint.Value,
                  out var updatedConstraint,
                  out var selectedStart))
            {
              startConstraint = updatedConstraint;
              startPoint = selectedStart;
              fromFirstPoint = true;
              fromPointActive = true;
              getPoint.SetBasePoint(startPoint, true);
              lastPreviewResolvedStart = Point3d.Unset;
              lastPreviewResolvedEnd = Point3d.Unset;
              Log.Write(
                "vLine",
                $"FromPoint selected start={startPoint} seed={updatedConstraint.SeedParameter:R}");
              ApplyModePrompt();
              doc.Views.Redraw();
            }
            continue;
          }

          if (option.Index == idxPersistConstraint)
          {
            persistConstraint.CurrentValue = !persistConstraint.CurrentValue;
            _persistConstraint = persistConstraint.CurrentValue;
            SavePersistedOptions();
            continue;
          }

          if (option.Index == idxDebug)
          {
            debugToggle.CurrentValue = !debugToggle.CurrentValue;
            continue;
          }

          if (option.Index == idxLayer)
          {
            PromptForLayer(doc, layerSession, runMode);
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxNormal)
          {
            if (TryPickNormalDefinition(doc, cplane, out var anchorPoint, out var anchorDirection))
            {
              endAnchor = new EndAnchor(anchorPoint, anchorDirection);
              mode = "end_anchor";
              ApplyModePrompt();
            }
            continue;
          }

          if (option.Index == idxAngled ||
              option.Index == idxVertical ||
              option.Index == idxFourPoint ||
              option.Index == idxBisector)
          {
            var directionMode = option.Index == idxAngled
              ? "Angled"
              : option.Index == idxVertical
                ? "Vertical"
                : option.Index == idxFourPoint
                  ? "FourPoint"
                  : "Bisector";
            if (TryGetEndDirectionDefinition(doc, directionMode, startPoint, out var direction))
            {
              parallelDir = direction;
              endAnchor = null;
              mode = "parallel";
              Log.Write(
                "vLine",
                $"endpoint direction mode={directionMode} start={startPoint} direction={direction}");
              ApplyModePrompt();
            }
            continue;
          }

          if (option.Index == idxExtension)
          {
            if (TryPickExtensionDefinition(
                  out var anchorPoint,
                  out var anchorDirection,
                  out var extensionCurve))
            {
              extensionCurve?.Dispose();
              var sharedEndpointTolerance = Math.Max(
                doc.ModelAbsoluteTolerance * 2.0,
                RhinoMath.ZeroTolerance);
              if (anchorPoint.DistanceTo(startPoint) <= sharedEndpointTolerance)
              {
                parallelDir = anchorDirection;
                endAnchor = null;
                mode = "extension_direction";
                Log.Write(
                  "vLine",
                  $"endpoint extension continues from shared anchor={anchorPoint} " +
                  $"direction={anchorDirection}");
              }
              else
              {
                endAnchor = new EndAnchor(anchorPoint, anchorDirection);
                mode = "end_anchor";
              }
              ApplyModePrompt();
            }
            continue;
          }

          if (option.Index == idxPerp)
          {
            endAnchor = null;
            mode = "perp";
            hoveredConstraintPick = null;
            constraintHoverHitWindow = System.Drawing.Point.Empty;
            constraintHoverLogged = false;
            constraintHoverMoveCount = 0;
            Log.Write("vLine", "endpoint mode=perp activated");
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxTan)
          {
            endAnchor = null;
            mode = "tangent";
            hoveredConstraintPick = null;
            constraintHoverHitWindow = System.Drawing.Point.Empty;
            constraintHoverLogged = false;
            constraintHoverMoveCount = 0;
            Log.Write("vLine", "endpoint mode=tangent activated");
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxPerpNear)
          {
            endAnchor = null;
            mode = "perp_any";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxTanNear)
          {
            endAnchor = null;
            mode = "tangent_any";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxAuto)
          {
            endAnchor = null;
            mode = "auto";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxParallel)
          {
            endAnchor = null;
            var gDir1 = new GetPoint();
            gDir1.EnableTransparentCommands(true);
            gDir1.SetCommandPrompt("Direction start point");
            gDir1.AcceptNothing(true);
            if (gDir1.Get() != GetResult.Point) continue;
            var dirPt1 = gDir1.Point();

            var gDir2 = new GetPoint();
            gDir2.EnableTransparentCommands(true);
            gDir2.SetCommandPrompt("Direction end point");
            gDir2.SetBasePoint(dirPt1, true);
            gDir2.DrawLineFromPoint(dirPt1, true);
            gDir2.AcceptNothing(true);
            if (gDir2.Get() != GetResult.Point) continue;
            var dirPt2 = gDir2.Point();

            var dirVec = dirPt2 - dirPt1;
            if (dirVec.IsTiny()) continue;
            dirVec.Unitize();
            parallelDir = dirVec;
            mode = "parallel";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxProjectTo)
          {
            endAnchor = null;
            var picked = PickGeometryWithPoint(
              "Select curve, surface, polysurface, or mesh to project endpoint onto",
              ObjectType.Curve | ObjectType.Surface | ObjectType.Brep | ObjectType.Mesh,
              layerSession,
              subObjects: false);
            if (!picked.HasValue)
              continue;

            var prjGeom = picked.Value.Geometry;
            projectTargetHighlight?.Dispose();
            projectTargetHighlight = TemporaryGeometryHighlight.Create(
              doc,
              prjGeom,
              SourceFeedbackColor);
            projectToGeometry?.Dispose();
            projectToGeometry = prjGeom;
            mode = "project_to";
            ApplyModePrompt();
            continue;
          }

          if (option.Index == idxPriority)
          {
            priorityIndex = ClampIndex(option.CurrentListOptionIndex, PriorityValues.Length);
            _priority = priorityIndex;
            SavePersistedOptions();
            continue;
          }

          if (option.Index == idxLength)
          {
            _length = lengthOption.CurrentValue;
            SavePersistedOptions();
            continue;
          }

          if (option.Index == idxAngle)
          {
            _angle = angleOption.CurrentValue;
            angleLock.CurrentValue = true; // activate lock automatically when user sets an angle
            SavePersistedOptions();
            continue;
          }

          if (option.Index == idxChainMode)
          {
            chainModeIndex = ClampIndex(option.CurrentListOptionIndex, ChainModeValues.Length);
            _chainMode = chainModeIndex;
            SavePersistedOptions();
            continue;
          }

          // Catches OptionToggle changes: PersistConstraint, AngleRef.
          _persistConstraint = persistConstraint.CurrentValue;
          _angleRelative = angleRelative.CurrentValue;
          SavePersistedOptions();
          continue;
        }

        if (result == GetResult.Number)
        {
          var numberValue = getPoint.Number();
          return ResolveSecondPoint(
            doc,
            startPoint,
            bothSides.CurrentValue,
            numberValue,
            mode,
            chainModeIndex,
            persistConstraint.CurrentValue,
            priorityIndex,
            angleLock.CurrentValue,
            angleOption.CurrentValue,
            angleRelative.CurrentValue,
            referenceVector,
            startConstraint,
            startDirection,
            startFeedbackGeometry,
            fromFirstPoint,
            layerSession,
            runMode,
            canUndo,
            canRedo);
        }

        var fallbackState = new ConstraintState(mode, persistConstraint.CurrentValue, priorityIndex, lengthOption.CurrentValue, angleLock.CurrentValue, angleOption.CurrentValue, angleRelative.CurrentValue);
        return SecondPointResult.None(bothSides.CurrentValue, chainModeIndex, fallbackState);
      }
    }
    finally
    {
      getPoint.MouseMove -= trackConstraintHover;
      getPoint.DynamicDraw -= drawPreview;
      projectTargetHighlight?.Dispose();
      projectToGeometry?.Dispose();
    }
  }

  private static List<CurveCacheItem> CollectCurveCache(RhinoDoc doc)
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

    var cache = new List<CurveCacheItem>();

    void AddCurve(Curve source)
    {
      var duplicate = source.DuplicateCurve();
      if (duplicate != null)
        cache.Add(new CurveCacheItem(duplicate, duplicate.GetBoundingBox(true)));
    }

    void AddBrepEdges(Brep brep)
    {
      foreach (var edge in brep.Edges)
        AddCurve(edge);
    }

    foreach (var rhObj in doc.Objects.GetObjectList(settings))
    {
      switch (rhObj.Geometry)
      {
        case Curve curve:
          AddCurve(curve);
          break;

        case Brep brep:
          AddBrepEdges(brep);
          break;

        case Extrusion extrusion:
          using (var extrusionBrep = extrusion.ToBrep())
          {
            if (extrusionBrep != null)
              AddBrepEdges(extrusionBrep);
          }
          break;

        case Surface surface:
          using (var surfaceBrep = Brep.CreateFromSurface(surface))
          {
            if (surfaceBrep != null)
              AddBrepEdges(surfaceBrep);
          }
          break;
      }
    }

    return cache;
  }

  private static Curve? NearestCurveToPoint(Point3d point, IReadOnlyList<CurveCacheItem> curveCache)
  {
    if (curveCache.Count == 0)
      return null;

    var shortlist = BuildCurveShortlist(point, curveCache, 8);

    Curve? best = null;
    var bestD2 = double.MaxValue;

    foreach (var (curve, _) in shortlist)
    {
      if (!curve.ClosestPoint(point, out var t))
        continue;

      var cp = curve.PointAt(t);
      var d2 = point.DistanceToSquared(cp);
      if (d2 >= bestD2)
        continue;

      bestD2 = d2;
      best = curve;
    }

    return best;
  }

  private static Curve? CurveAtCursorPoint(Point3d point, IReadOnlyList<CurveCacheItem> curveCache, double captureTolerance)
  {
    if (curveCache.Count == 0)
      return null;

    var shortlist = BuildCurveShortlist(point, curveCache, 12);

    Curve? best = null;
    var bestD2 = double.MaxValue;

    foreach (var (curve, _) in shortlist)
    {
      if (!curve.ClosestPoint(point, out var t))
        continue;

      var cp = curve.PointAt(t);
      var d2 = point.DistanceToSquared(cp);
      if (d2 >= bestD2)
        continue;

      bestD2 = d2;
      best = curve;
    }

    if (best == null)
      return null;

    return bestD2 <= captureTolerance * captureTolerance ? best : null;
  }

  private static List<CurveCacheItem> BuildCurveShortlist(Point3d point, IReadOnlyList<CurveCacheItem> curveCache, int count)
  {
    var sorted = new List<(CurveCacheItem Item, double DistanceSq)>();
    foreach (var item in curveCache)
      sorted.Add((item, BoundingBoxDistanceSquared(item.BoundingBox, point)));

    sorted.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));

    var shortlist = new List<CurveCacheItem>();
    for (var i = 0; i < sorted.Count && i < count; i++)
      shortlist.Add(sorted[i].Item);

    return shortlist;
  }

  private static double BoundingBoxDistanceSquared(BoundingBox bbox, Point3d point)
  {
    if (!bbox.IsValid)
      return 1e300;

    double dx;
    if (point.X < bbox.Min.X)
      dx = bbox.Min.X - point.X;
    else if (point.X > bbox.Max.X)
      dx = point.X - bbox.Max.X;
    else
      dx = 0.0;

    double dy;
    if (point.Y < bbox.Min.Y)
      dy = bbox.Min.Y - point.Y;
    else if (point.Y > bbox.Max.Y)
      dy = point.Y - bbox.Max.Y;
    else
      dy = 0.0;

    double dz;
    if (point.Z < bbox.Min.Z)
      dz = bbox.Min.Z - point.Z;
    else if (point.Z > bbox.Max.Z)
      dz = point.Z - bbox.Max.Z;
    else
      dz = 0.0;

    return (dx * dx) + (dy * dy) + (dz * dz);
  }

  private static Vector2d ToCPlane2d(Vector3d vector, Plane plane)
  {
    return new Vector2d(Vector3d.Multiply(vector, plane.XAxis), Vector3d.Multiply(vector, plane.YAxis));
  }

  private static bool TryUnitize2d(Vector2d value, out Vector2d unit)
  {
    unit = value;
    if (unit.IsTiny())
      return false;

    unit.Unitize();
    return true;
  }

  private static bool CurveIsLinear(Curve curve, out Line line)
  {
    if (curve is LineCurve lineCurve)
    {
      line = lineCurve.Line;
      return true;
    }

    if (curve.IsLinear(RhinoMath.SqrtEpsilon))
    {
      line = new Line(curve.PointAtStart, curve.PointAtEnd);
      return line.IsValid;
    }

    line = Line.Unset;
    return false;
  }

  private static double PerpScore2d(Curve curve, double t, Point3d startPoint, Plane cplane)
  {
    var point = curve.PointAt(t);
    var tangent = curve.TangentAt(t);

    var v2 = ToCPlane2d(startPoint - point, cplane);
    var t2 = new Vector2d(Vector3d.Multiply(tangent, cplane.XAxis), Vector3d.Multiply(tangent, cplane.YAxis));

    if (!TryUnitize2d(v2, out var v2u) || !TryUnitize2d(t2, out var t2u))
      return 1.0;

    var dot = (t2u.X * v2u.X) + (t2u.Y * v2u.Y);
    return Math.Abs(dot);
  }

  private static (double Parameter, double Error) RefinePerpParameter(Curve curve, Point3d startPoint, Plane cplane, double t0, double t1, int iterations)
  {
    const double phi = 0.61803398875;
    var a = Math.Min(t0, t1);
    var b = Math.Max(t0, t1);

    var c = b - ((b - a) * phi);
    var d = a + ((b - a) * phi);
    var fc = PerpScore2d(curve, c, startPoint, cplane);
    var fd = PerpScore2d(curve, d, startPoint, cplane);

    for (var i = 0; i < iterations; i++)
    {
      if (fc < fd)
      {
        b = d;
        d = c;
        fd = fc;
        c = b - ((b - a) * phi);
        fc = PerpScore2d(curve, c, startPoint, cplane);
      }
      else
      {
        a = c;
        c = d;
        fc = fd;
        d = a + ((b - a) * phi);
        fd = PerpScore2d(curve, d, startPoint, cplane);
      }
    }

    var best = 0.5 * (a + b);
    return (best, PerpScore2d(curve, best, startPoint, cplane));
  }

  private static Point3d? PerpPointFromStartWithHint(Point3d startPoint, Curve curve, Point3d hintPoint, int samples, int refineIterations)
  {
    if (CurveIsLinear(curve, out var line))
    {
      var t = line.ClosestParameter(startPoint);
      var point = line.PointAt(t);

      // Only accept the projected perpendicular if it lies on the finite line segment.
      // Otherwise let the general solver/fallback handle it.
      if (t >= -RhinoMath.SqrtEpsilon && t <= 1.0 + RhinoMath.SqrtEpsilon)
        return point;
    }
    var cplane = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    var domain = curve.Domain;
    var a = domain.T0;
    var b = domain.T1;
    if (b <= a)
      return null;

    double hintT;
    if (!curve.ClosestPoint(hintPoint, out hintT))
      hintT = 0.5 * (a + b);

    var dt = (b - a) / samples;
    if (dt <= 0.0)
      return null;

    var candidates = new List<(double T, double Error)>();
    var values = new List<double>();
    var parameters = new List<double>();

    for (var i = 0; i <= samples; i++)
    {
      var t = a + dt * i;
      parameters.Add(t);
      values.Add(PerpScore2d(curve, t, startPoint, cplane));
    }

    for (var i = 1; i < values.Count - 1; i++)
    {
      if (values[i] <= values[i - 1] && values[i] <= values[i + 1])
        candidates.Add((parameters[i], values[i]));
    }

    if (values.Count > 1)
    {
      if (values[0] <= values[1])
        candidates.Add((parameters[0], values[0]));
      if (values[^1] <= values[^2])
        candidates.Add((parameters[^1], values[^1]));
    }

    if (candidates.Count == 0)
    {
      var bestIndex = 0;
      var bestValue = values[0];
      for (var i = 1; i < values.Count; i++)
      {
        if (values[i] < bestValue)
        {
          bestValue = values[i];
          bestIndex = i;
        }
      }

      candidates.Add((parameters[bestIndex], values[bestIndex]));
    }

    candidates.Sort((x, y) =>
    {
      var px = curve.PointAt(x.T);
      var py = curve.PointAt(y.T);

      var dx = px.DistanceToSquared(hintPoint).CompareTo(py.DistanceToSquared(hintPoint));
      if (dx != 0)
        return dx;

      var de = x.Error.CompareTo(y.Error);
      if (de != 0)
        return de;

      return Math.Abs(x.T - hintT).CompareTo(Math.Abs(y.T - hintT));
    });

    var refined = new List<(double T, double Error, Point3d Point)>();

    // Refine nearest-to-cursor perpendicular candidates, not first/best-error candidates.
    // More than 16 is still cheap enough here and avoids wrong picks on curves with many perpendiculars.
    var maxCandidatesToRefine = Math.Min(candidates.Count, 64);

    for (var i = 0; i < maxCandidatesToRefine; i++)
    {
      var seedT = candidates[i].T;
      var window = Math.Max(dt * 4.0, (b - a) * 0.01);
      var t0 = Math.Max(a, seedT - window);
      var t1 = Math.Min(b, seedT + window);
      var refinedResult = RefinePerpParameter(curve, startPoint, cplane, t0, t1, refineIterations);
      if (candidates[i].Error < refinedResult.Error)
      {
        refined.Add((seedT, candidates[i].Error, curve.PointAt(seedT)));
      }
      else
      {
        refined.Add((refinedResult.Parameter, refinedResult.Error,
          curve.PointAt(refinedResult.Parameter)));
      }
    }
    if (refined.Count == 0)
      return null;

    refined.Sort((x, y) => x.T.CompareTo(y.T));
    var unique = new List<(double T, double Error, Point3d Point)>();
    var paramTol = Math.Max((b - a) * 1e-6, 1e-9);

    foreach (var item in refined)
    {
      if (unique.Count == 0 || Math.Abs(item.T - unique[^1].T) > paramTol)
      {
        unique.Add(item);
      }
      else if (item.Error < unique[^1].Error)
      {
        unique[^1] = item;
      }
    }

    var scoreTolerance = ConstraintCrossScoreTolerance();
    var valid = unique.FindAll(v => v.Error <= scoreTolerance);
    if (_debugMode)
    {
      DebugLog($"PerpSolver: candidates={candidates.Count} refined={refined.Count} unique={unique.Count} valid={valid.Count}");
      if (unique.Count > 0)
      {
        var bestErr = unique[0].Error;
        for (var i = 1; i < unique.Count; i++)
          if (unique[i].Error < bestErr) bestErr = unique[i].Error;
        DebugLog($"PerpSolver: best error={bestErr:F6} threshold={scoreTolerance:F6}{(valid.Count == 0 ? " -> FAILED" : " -> OK")}");
      }
    }

    if (valid.Count == 0)
      return null;

    valid.Sort((x, y) =>
    {
      var dx = x.Point.DistanceToSquared(hintPoint).CompareTo(y.Point.DistanceToSquared(hintPoint));
      if (dx != 0)
        return dx;

      var de = x.Error.CompareTo(y.Error);
      if (de != 0)
        return de;

      return Math.Abs(x.T - hintT).CompareTo(Math.Abs(y.T - hintT));
    });

    return valid[0].Point;
  }

  private static Point3d? PerpFallbackToPointedSegment(Point3d startPoint, Curve curve, Point3d hintPoint, bool preview)
  {
    var segments = curve.DuplicateSegments();
    if (segments == null || segments.Length == 0)
      return null;

    Curve? bestSeg = null;
    var bestD2 = double.MaxValue;

    foreach (var seg in segments)
    {
      if (seg == null)
        continue;

      if (!seg.ClosestPoint(hintPoint, out var t))
        continue;

      var cp = seg.PointAt(t);
      var d2 = cp.DistanceToSquared(hintPoint);
      if (d2 >= bestD2)
        continue;

      bestD2 = d2;
      bestSeg = seg;
    }

    if (bestSeg == null)
      return null;

    var pt = PerpPointFromStartWithHint(startPoint, bestSeg, hintPoint, preview ? 80 : 240, preview ? 16 : 18);
    if (pt.HasValue)
      return pt;

    if (CurveIsLinear(bestSeg, out var line))
      return line.PointAt(line.ClosestParameter(startPoint));

    return null;
  }

  private static double TangentScore2d(Curve curve, double t, Point3d startPoint, Plane cplane)
  {
    var point = curve.PointAt(t);
    var tangent = curve.TangentAt(t);

    var v2 = ToCPlane2d(startPoint - point, cplane);
    var t2 = new Vector2d(Vector3d.Multiply(tangent, cplane.XAxis), Vector3d.Multiply(tangent, cplane.YAxis));

    if (!TryUnitize2d(v2, out var v2u) || !TryUnitize2d(t2, out var t2u))
      return 1.0;

    var cross = (t2u.X * v2u.Y) - (t2u.Y * v2u.X);
    return Math.Abs(cross);
  }

  private static (double Parameter, double Error) RefineTangentParameter(Curve curve, Point3d startPoint, Plane cplane, double t0, double t1, int iterations)
  {
    const double phi = 0.61803398875;
    var a = Math.Min(t0, t1);
    var b = Math.Max(t0, t1);

    var c = b - ((b - a) * phi);
    var d = a + ((b - a) * phi);
    var fc = TangentScore2d(curve, c, startPoint, cplane);
    var fd = TangentScore2d(curve, d, startPoint, cplane);

    for (var i = 0; i < iterations; i++)
    {
      if (fc < fd)
      {
        b = d;
        d = c;
        fd = fc;
        c = b - ((b - a) * phi);
        fc = TangentScore2d(curve, c, startPoint, cplane);
      }
      else
      {
        a = c;
        c = d;
        fc = fd;
        d = a + ((b - a) * phi);
        fd = TangentScore2d(curve, d, startPoint, cplane);
      }
    }

    var best = 0.5 * (a + b);
    return (best, TangentScore2d(curve, best, startPoint, cplane));
  }

  private static Point3d? TangentPointFromStart(Point3d startPoint, Curve curve, Point3d hintPoint, int samples, int refineIterations)
  {
    var cplane = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

    var domain = curve.Domain;
    var a = domain.T0;
    var b = domain.T1;
    if (b <= a)
      return null;

    double hintT;
    if (!curve.ClosestPoint(hintPoint, out hintT))
      hintT = 0.5 * (a + b);

    var dt = (b - a) / samples;
    if (dt <= 0.0)
      return null;

    var candidates = new List<(double T, double Error)>();
    var values = new List<double>();
    var parameters = new List<double>();

    for (var i = 0; i <= samples; i++)
    {
      var t = a + dt * i;
      parameters.Add(t);
      values.Add(TangentScore2d(curve, t, startPoint, cplane));
    }

    for (var i = 1; i < values.Count - 1; i++)
    {
      if (values[i] <= values[i - 1] && values[i] <= values[i + 1])
        candidates.Add((parameters[i], values[i]));
    }

    if (values.Count > 1)
    {
      if (values[0] <= values[1])
        candidates.Add((parameters[0], values[0]));
      if (values[^1] <= values[^2])
        candidates.Add((parameters[^1], values[^1]));
    }

    if (candidates.Count == 0)
    {
      var bestIndex = 0;
      var bestValue = values[0];
      for (var i = 1; i < values.Count; i++)
      {
        if (values[i] < bestValue)
        {
          bestValue = values[i];
          bestIndex = i;
        }
      }

      candidates.Add((parameters[bestIndex], values[bestIndex]));
    }

    candidates.Sort((x, y) =>
    {
      var px = curve.PointAt(x.T);
      var py = curve.PointAt(y.T);
      var distanceComparison = px.DistanceToSquared(hintPoint)
        .CompareTo(py.DistanceToSquared(hintPoint));
      if (distanceComparison != 0)
        return distanceComparison;

      var errorComparison = x.Error.CompareTo(y.Error);
      if (errorComparison != 0)
        return errorComparison;

      return Math.Abs(x.T - hintT).CompareTo(Math.Abs(y.T - hintT));
    });

    var refined = new List<(double T, double Error, Point3d Point)>();
    var maxCandidatesToRefine = Math.Min(candidates.Count, 64);
    for (var i = 0; i < maxCandidatesToRefine; i++)
    {
      var seedT = candidates[i].T;
      var window = Math.Max(dt * 4.0, (b - a) * 0.01);
      var t0 = Math.Max(a, seedT - window);
      var t1 = Math.Min(b, seedT + window);
      var refinedResult = RefineTangentParameter(curve, startPoint, cplane, t0, t1, refineIterations);
      if (candidates[i].Error < refinedResult.Error)
      {
        refined.Add((seedT, candidates[i].Error, curve.PointAt(seedT)));
      }
      else
      {
        refined.Add((refinedResult.Parameter, refinedResult.Error,
          curve.PointAt(refinedResult.Parameter)));
      }
    }

    if (refined.Count == 0)
      return null;

    refined.Sort((x, y) => x.T.CompareTo(y.T));
    var unique = new List<(double T, double Error, Point3d Point)>();
    var paramTol = Math.Max((b - a) * 1e-6, 1e-9);

    foreach (var item in refined)
    {
      if (unique.Count == 0 || Math.Abs(item.T - unique[^1].T) > paramTol)
      {
        unique.Add(item);
      }
      else if (item.Error < unique[^1].Error)
      {
        unique[^1] = item;
      }
    }

    var valid = unique.FindAll(v => v.Error <= ConstraintCrossScoreTolerance());
    if (_debugMode)
    {
      DebugLog(
        $"TangentSolver: candidates={candidates.Count} refined={refined.Count}" +
        $" unique={unique.Count} valid={valid.Count}");
    }
    if (valid.Count == 0)
      return null;

    valid.Sort((x, y) =>
    {
      var dx = x.Point.DistanceToSquared(hintPoint).CompareTo(y.Point.DistanceToSquared(hintPoint));
      if (dx != 0)
        return dx;

      var de = x.Error.CompareTo(y.Error);
      if (de != 0)
        return de;

      return Math.Abs(x.T - hintT).CompareTo(Math.Abs(y.T - hintT));
    });

    return valid[0].Point;
  }

  private static PickedGeometry? PickGeometryWithPoint(
    string prompt,
    ObjectType geometryFilter,
    LineLayerSession? layerSession = null,
    bool subObjects = false)
  {
    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
      return null;

    var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.SetCommandPrompt(
      layerSession?.DecoratePrompt(doc, prompt) ?? prompt);
    getPoint.AcceptNothing(true);

    ScreenGeometryPick? hovered = null;
    System.Drawing.Point hoverHitWindow = System.Drawing.Point.Empty;
    getPoint.MouseMove += (_, e) =>
    {
      var nextPick = PickGeometryAtScreenPoint(
        doc,
        e.Viewport,
        e.WindowPoint,
        geometryFilter,
        subObjects,
        out var diagnostic);
      if (nextPick.HasValue)
      {
        hovered = nextPick;
        hoverHitWindow = e.WindowPoint;
      }
      else if (hovered.HasValue &&
               ScreenDistanceSquared(e.WindowPoint, hoverHitWindow) <= 100)
      {
        diagnostic += $" retained={hovered.Value.ObjectId}";
      }
      else
      {
        hovered = null;
      }
    };
    getPoint.DynamicDraw += (_, e) =>
    {
      if (layerSession != null)
        DrawHiddenLayerWarning(e, doc, layerSession);
      if (!hovered.HasValue)
        return;

      var geometry = GeometryFromScreenPick(
        doc,
        hovered.Value,
        preferWholeObject: !subObjects);
      if (geometry != null)
        DrawFeedbackGeometry(e.Display, geometry, HoverFeedbackColor, 3);
    };

    var result = getPoint.Get();
    if (result != GetResult.Point || !hovered.HasValue)
      return null;

    var sourceGeometry = GeometryFromScreenPick(
      doc,
      hovered.Value,
      preferWholeObject: !subObjects);
    var duplicate = DuplicatePickedGeometry(sourceGeometry);
    if (duplicate == null)
      return null;

    Log.Write(
      "vLine.PickGeometry",
      $"prompt={prompt} source={hovered.Value.ObjectId} " +
      $"component={hovered.Value.ComponentIndex} selected=" +
      $"{doc.Objects.FindId(hovered.Value.ObjectId)?.IsSelected(true) > 0}");
    return new PickedGeometry(
      duplicate,
      hovered.Value.PickPoint,
      hovered.Value.ObjectId,
      hovered.Value.ComponentIndex);
  }

  private static ScreenGeometryPick? PickGeometryAtScreenPoint(
    RhinoDoc doc,
    Rhino.Display.RhinoViewport viewport,
    System.Drawing.Point windowPoint,
    ObjectType geometryFilter,
    bool subObjects,
    out string diagnostic)
  {
    if (viewport.ParentView == null ||
        !viewport.GetFrustumLine(windowPoint.X, windowPoint.Y, out var pickLine))
    {
      diagnostic = "no pick line";
      return null;
    }

    using var pickContext = new PickContext
    {
      View = viewport.ParentView,
      PickLine = pickLine,
      PickStyle = PickStyle.PointPick,
      PickMode = PickMode.Shaded,
      PickGroupsEnabled = false,
      SubObjectSelectionEnabled = subObjects
    };
    pickContext.SetPickTransform(viewport.GetPickTransform(windowPoint));
    pickContext.UpdateClippingPlanes();

    var picked = doc.Objects.PickObjects(pickContext);
    if (picked == null)
    {
      diagnostic = "PickObjects returned null";
      return null;
    }

    foreach (var objRef in picked)
    {
      var geometry = subObjects
        ? objRef.Geometry()
        : objRef.Object()?.Geometry;
      if (!GeometryMatchesFilter(geometry, geometryFilter))
        continue;

      var pickPoint = objRef.SelectionPoint();
      if (!pickPoint.IsValid && geometry != null)
        pickPoint = geometry.GetBoundingBox(true).Center;
      if (!pickPoint.IsValid)
        continue;

      diagnostic =
        $"picked={picked.Length} id={objRef.ObjectId} " +
        $"component={objRef.GeometryComponentIndex} type={geometry!.GetType().Name}";
      return new ScreenGeometryPick(
        objRef.ObjectId,
        objRef.GeometryComponentIndex,
        pickPoint);
    }

    diagnostic = $"picked={picked.Length} no matching geometry";
    return null;
  }

  private static bool GeometryMatchesFilter(
    GeometryBase? geometry,
    ObjectType geometryFilter)
  {
    return geometry switch
    {
      Curve => (geometryFilter & ObjectType.Curve) != 0,
      Brep => (geometryFilter & (ObjectType.Brep | ObjectType.Surface)) != 0,
      Extrusion => (geometryFilter & (ObjectType.Brep | ObjectType.Surface)) != 0,
      Surface => (geometryFilter & (ObjectType.Brep | ObjectType.Surface)) != 0,
      Mesh => (geometryFilter & ObjectType.Mesh) != 0,
      _ => false
    };
  }

  private static GeometryBase? GeometryFromScreenPick(
    RhinoDoc doc,
    ScreenGeometryPick pick,
    bool preferWholeObject)
  {
    var rhinoObject = doc.Objects.FindId(pick.ObjectId);
    if (rhinoObject == null)
      return null;
    if (preferWholeObject)
      return rhinoObject.Geometry;

    if (rhinoObject.Geometry is Brep brep)
    {
      if (pick.ComponentIndex.ComponentIndexType == ComponentIndexType.BrepFace &&
          pick.ComponentIndex.Index >= 0 &&
          pick.ComponentIndex.Index < brep.Faces.Count)
      {
        return brep.Faces[pick.ComponentIndex.Index];
      }

      if (pick.ComponentIndex.ComponentIndexType == ComponentIndexType.BrepEdge &&
          pick.ComponentIndex.Index >= 0 &&
          pick.ComponentIndex.Index < brep.Edges.Count)
      {
        return brep.Edges[pick.ComponentIndex.Index];
      }
    }

    return rhinoObject.Geometry;
  }

  private static GeometryBase? DuplicatePickedGeometry(GeometryBase? geometry)
  {
    return geometry switch
    {
      Extrusion extrusion => extrusion.ToBrep(),
      BrepFace face => face.DuplicateSurface(),
      BrepEdge edge => edge.DuplicateCurve(),
      _ => geometry?.Duplicate()
    };
  }

  private static bool RunBiTangent(
    RhinoDoc doc,
    LineLayerSession layerSession)
  {
    var first = PickCurveWithPoint(
      "Select first tangent curve",
      layerSession,
      EndpointConstraintKind.Tangent);
    if (first == null)
      return false;

    using var firstHighlight = TemporaryGeometryHighlight.Create(
      doc,
      first.Value.Curve,
      SourceFeedbackColor);
    var second = PickCurveWithPoint(
      "Select second tangent curve",
      layerSession,
      EndpointConstraintKind.Tangent,
      first.Value);
    if (second == null)
      return false;

    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    if (!TryFindBiTangent(first.Value.Curve, second.Value.Curve,
                         first.Value.PickPoint, second.Value.PickPoint,
                         cplane, out var line))
    {
      RhinoApp.WriteLine("vLine: no bitangent solution found for the selected curves.");
      return false;
    }

    _ = doc.Objects.AddLine(line, layerSession.CreateAttributes(doc));
    doc.Views.Redraw();
    return true;
  }

  private static PickedCurve? PickCurveWithPoint(
    string prompt,
    LineLayerSession? layerSession = null,
    EndpointConstraintKind? cueKind = null,
    PickedCurve? biTangentFrom = null)
  {
    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
      return null;

    var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.SetCommandPrompt(
      layerSession?.DecoratePrompt(doc, prompt) ?? prompt);
    getPoint.AcceptNothing(true);
    if (cueKind.HasValue)
    {
      getPoint.EnableSnapToCurves(true);
      getPoint.EnableCurveSnapTangentBar(true, false);
    }

    ScreenCurvePick? hovered = null;
    System.Drawing.Point hoverHitWindow = System.Drawing.Point.Empty;
    var hoverLogged = false;
    Guid hoverLoggedId = Guid.Empty;
    Line? biTangentPreview = null;
    getPoint.MouseMove += (_, e) =>
    {
      var nextPick = PickCurveAtScreenPoint(
        doc,
        e.Viewport,
        e.WindowPoint,
        out var diagnostic);
      if (nextPick.HasValue)
      {
        hovered = nextPick;
        hoverHitWindow = e.WindowPoint;
      }
      else if (hovered.HasValue &&
               ScreenDistanceSquared(e.WindowPoint, hoverHitWindow) <= 100)
      {
        diagnostic += $" retained={hovered.Value.ObjectId}";
      }
      else
      {
        hovered = null;
      }

      biTangentPreview = null;
      if (biTangentFrom.HasValue && hovered.HasValue)
      {
        var secondCurve = CurveFromScreenPick(doc, hovered.Value);
        if (secondCurve != null &&
            TryFindBiTangent(
              biTangentFrom.Value.Curve,
              secondCurve,
              biTangentFrom.Value.PickPoint,
              hovered.Value.PickPoint,
              e.Viewport.ConstructionPlane(),
              out var previewLine))
        {
          biTangentPreview = previewLine;
        }
      }

      var hoveredId = hovered?.ObjectId ?? Guid.Empty;
      if (!hoverLogged || hoveredId != hoverLoggedId)
      {
        hoverLogged = true;
        hoverLoggedId = hoveredId;
        Log.Write(
          "vLine.PickCurve",
          $"prompt={prompt} window={e.WindowPoint} world={e.Point} result={diagnostic}");
      }
    };
    getPoint.DynamicDraw += (_, e) =>
    {
      if (layerSession != null)
        DrawHiddenLayerWarning(e, doc, layerSession);

      if (!hovered.HasValue)
        return;

      var curve = CurveFromScreenPick(doc, hovered.Value);
      if (curve != null)
      {
        PreviewDisplay.DrawCurve(
          e.Display,
          curve,
          HoverFeedbackColor,
          2);
      }

      if (biTangentPreview.HasValue)
      {
        var previewColor = layerSession?.ResolveColor(doc) ?? Color.Cyan;
        PreviewDisplay.DrawLine(e.Display, biTangentPreview.Value, previewColor);
        e.Display.DrawDottedLine(
          biTangentPreview.Value.From,
          biTangentPreview.Value.To,
          previewColor);
      }
    };

    var result = getPoint.Get();
    Log.Write(
      "vLine.PickCurve",
      $"prompt={prompt} getResult={result} commandResult={getPoint.CommandResult()} " +
      $"hoverId={hovered?.ObjectId.ToString() ?? "none"}");
    if (result != GetResult.Point)
      return null;

    Curve? pickedCurve = null;
    var pickedPoint = getPoint.Point();  // always use Rhino's snapped point so the visual marker matches
    var pickedObjectId = Guid.Empty;
    var pickedComponentIndex = ComponentIndex.Unset;
    if (hovered.HasValue)
    {
      pickedCurve = CurveFromScreenPick(doc, hovered.Value);
      pickedObjectId = hovered.Value.ObjectId;
      pickedComponentIndex = hovered.Value.ComponentIndex;
    }

    if (pickedCurve == null)
    {
      var pointObject = getPoint.PointOnObject();
      pickedCurve = pointObject?.Curve();
      pickedObjectId = pointObject?.ObjectId ?? Guid.Empty;
      pickedComponentIndex = pointObject?.GeometryComponentIndex ?? ComponentIndex.Unset;
      Log.Write(
        "vLine.PickCurve",
        $"prompt={prompt} native fallback id={pointObject?.ObjectId.ToString() ?? "none"} " +
        $"component={pointObject?.GeometryComponentIndex.ToString() ?? "none"} " +
        $"curveType={pickedCurve?.GetType().Name ?? "none"} point={pickedPoint}");
    }

    var duplicate = pickedCurve?.DuplicateCurve();
    if (duplicate == null)
      return null;

    Log.Write(
      "vLine.PickCurve",
      $"prompt={prompt} source={pickedObjectId} " +
      $"selected={doc.Objects.FindId(pickedObjectId)?.IsSelected(true) > 0}");

    return new PickedCurve(
      duplicate,
      pickedPoint,
      pickedObjectId,
      pickedComponentIndex);
  }

  private static ScreenCurvePick? PickCurveAtScreenPoint(
    RhinoDoc doc,
    Rhino.Display.RhinoViewport viewport,
    System.Drawing.Point windowPoint,
    out string diagnostic)
  {
    if (viewport.ParentView == null)
    {
      diagnostic = "no parent view";
      return null;
    }

    if (!viewport.GetFrustumLine(windowPoint.X, windowPoint.Y, out var pickLine))
    {
      diagnostic = "GetFrustumLine failed";
      return null;
    }

    using var pickContext = new PickContext
    {
      View = viewport.ParentView,
      PickLine = pickLine,
      PickStyle = PickStyle.PointPick,
      PickMode = PickMode.Wireframe,
      PickGroupsEnabled = false,
      SubObjectSelectionEnabled = true
    };
    pickContext.SetPickTransform(viewport.GetPickTransform(windowPoint));
    pickContext.UpdateClippingPlanes();

    var picked = doc.Objects.PickObjects(pickContext);
    if (picked == null)
    {
      diagnostic = "PickObjects returned null";
      return null;
    }

    var candidates = new List<ScreenCurveCandidate>();
    var rejected = new List<string>();

    foreach (var objRef in picked)
    {
      var curve = objRef.Curve();
      if (curve == null)
      {
        if (objRef.Object()?.Geometry is Brep brep &&
            TryPickBrepEdge(
              pickContext,
              brep,
              out var edgeIndex,
              out var edgePoint,
              out var edgeDepth,
              out var edgeDistance))
        {
          var componentIndex = new ComponentIndex(
            ComponentIndexType.BrepEdge,
            edgeIndex);
          var edge = brep.Edges[edgeIndex];
          var endpointHit = edge.ClosestPoint(edgePoint, out var edgeParameter) &&
                            IsCurveEndpointParameter(edge, edgeParameter);
          candidates.Add(new ScreenCurveCandidate(
            new ScreenCurvePick(objRef.ObjectId, componentIndex, edgePoint),
            edgeDistance,
            edgeDepth,
            endpointHit,
            "BrepEdge"));
          continue;
        }

        rejected.Add($"{objRef.ObjectId}:{objRef.Object()?.ObjectType.ToString() ?? "unknown"}");
        continue;
      }

      using var pickNurbs = curve.ToNurbsCurve();
      if (pickNurbs == null ||
          !pickContext.PickFrustumTest(
            pickNurbs,
            out var curveParameter,
            out var curveDepth,
            out var curveDistance))
      {
        rejected.Add($"{objRef.ObjectId}:{curve.GetType().Name}:frustum-failed");
        continue;
      }

      var pickPoint = pickNurbs.PointAt(curveParameter);
      candidates.Add(new ScreenCurveCandidate(
        new ScreenCurvePick(
          objRef.ObjectId,
          objRef.GeometryComponentIndex,
          pickPoint),
        curveDistance,
        curveDepth,
        IsCurveEndpointParameter(pickNurbs, curveParameter),
        curve.GetType().Name));
    }

    if (candidates.Count == 0)
    {
      diagnostic = $"picked={picked.Length} no curves rejected=[{string.Join(",", rejected)}]";
      return null;
    }

    candidates.Sort(CompareScreenCurveCandidates);
    var chosen = candidates[0];
    diagnostic =
      $"picked={picked.Length} chosen={chosen.Pick.ObjectId} " +
      $"component={chosen.Pick.ComponentIndex} curveType={chosen.CurveType} " +
      $"pick={chosen.Pick.PickPoint} depth={chosen.Depth:G6} " +
      $"distance={chosen.Distance:G6} endpoint={chosen.EndpointHit} " +
      $"ranked=[{string.Join(",", candidates.Select(candidate =>
        $"{candidate.Pick.ObjectId}:{candidate.Distance:G6}:{candidate.Depth:G6}:{(candidate.EndpointHit ? "end" : "body")}"))}]";
    return chosen.Pick;
  }

  private static int CompareScreenCurveCandidates(
    ScreenCurveCandidate left,
    ScreenCurveCandidate right)
  {
    const double distanceTieTolerance = 1.0e-9;
    var distanceDifference = left.Distance - right.Distance;
    if (Math.Abs(distanceDifference) > distanceTieTolerance)
      return distanceDifference < 0.0 ? -1 : 1;

    if (left.EndpointHit != right.EndpointHit)
      return left.EndpointHit ? 1 : -1;

    var depthDifference = left.Depth - right.Depth;
    if (Math.Abs(depthDifference) > 1.0e-12)
      return depthDifference < 0.0 ? -1 : 1;

    return left.Pick.ObjectId.CompareTo(right.Pick.ObjectId);
  }

  private static bool IsCurveEndpointParameter(Curve curve, double parameter)
  {
    if (curve.IsClosed)
      return false;

    var normalized = curve.Domain.NormalizedParameterAt(parameter);
    return normalized <= 1.0e-6 || normalized >= 1.0 - 1.0e-6;
  }

  private static bool TryPickBrepEdge(
    PickContext pickContext,
    Brep brep,
    out int edgeIndex,
    out Point3d edgePoint,
    out double depth,
    out double distance)
  {
    edgeIndex = -1;
    edgePoint = Point3d.Unset;
    depth = double.MaxValue;
    distance = double.MaxValue;

    for (var i = 0; i < brep.Edges.Count; i++)
    {
      using var nurbs = brep.Edges[i].ToNurbsCurve();
      if (nurbs == null ||
          !pickContext.PickFrustumTest(
            nurbs,
            out var parameter,
            out var candidateDepth,
            out var candidateDistance))
      {
        continue;
      }

      if (candidateDistance > distance ||
          (Math.Abs(candidateDistance - distance) <= 1e-12 && candidateDepth >= depth))
      {
        continue;
      }

      edgeIndex = i;
      edgePoint = nurbs.PointAt(parameter);
      depth = candidateDepth;
      distance = candidateDistance;
    }

    return edgeIndex >= 0 && edgePoint.IsValid;
  }

  private static Curve? CurveFromScreenPick(RhinoDoc doc, ScreenCurvePick pick)
  {
    var rhinoObject = doc.Objects.FindId(pick.ObjectId);
    if (rhinoObject?.Geometry is Curve curve)
      return curve;

    if (pick.ComponentIndex.ComponentIndexType == ComponentIndexType.BrepEdge &&
        rhinoObject?.Geometry is Brep brep &&
        pick.ComponentIndex.Index >= 0 &&
        pick.ComponentIndex.Index < brep.Edges.Count)
    {
      return brep.Edges[pick.ComponentIndex.Index];
    }

    if (pick.ComponentIndex.ComponentIndexType == ComponentIndexType.PolycurveSegment &&
        rhinoObject?.Geometry is PolyCurve polyCurve &&
        pick.ComponentIndex.Index >= 0 &&
        pick.ComponentIndex.Index < polyCurve.SegmentCount)
    {
      return polyCurve.SegmentCurve(pick.ComponentIndex.Index);
    }

    return null;
  }

  private static int ScreenDistanceSquared(
    System.Drawing.Point first,
    System.Drawing.Point second)
  {
    var dx = first.X - second.X;
    var dy = first.Y - second.Y;
    return (dx * dx) + (dy * dy);
  }

  private static bool TryFindBiTangent(Curve a, Curve b, Point3d pickA, Point3d pickB,
                                       Plane cplane, out Line line)
  {
    line = Line.Unset;

    var da = a.Domain;
    var db = b.Domain;
    if (da.T1 <= da.T0 || db.T1 <= db.T0)
      return false;

    const int samples = 64;
    var candidates = new List<(double TA, double TB, double Error, double PickScore)>();

    for (var ia = 0; ia <= samples; ia++)
    {
      var ta = da.T0 + (da.T1 - da.T0) * ia / samples;
      for (var ib = 0; ib <= samples; ib++)
      {
        var tb = db.T0 + (db.T1 - db.T0) * ib / samples;
        var score = BiTangentScore(a, b, ta, tb, cplane);
        if (!score.HasValue)
          continue;

        var pa = a.PointAt(ta);
        var pb = b.PointAt(tb);
        candidates.Add((ta, tb, score.Value,
          pa.DistanceToSquared(pickA) + pb.DistanceToSquared(pickB)));
      }
    }

    if (candidates.Count == 0)
      return false;

    candidates.Sort((x, y) =>
    {
      var e = x.Error.CompareTo(y.Error);
      return e != 0 ? e : x.PickScore.CompareTo(y.PickScore);
    });

    var refined = new List<(double TA, double TB, double Error, double PickScore, Point3d PA, Point3d PB)>();
    var refineCount = Math.Min(48, candidates.Count);
    var stepA = (da.T1 - da.T0) / samples;
    var stepB = (db.T1 - db.T0) / samples;

    for (var i = 0; i < refineCount; i++)
    {
      var r = RefineBiTangent(a, b, candidates[i].TA, candidates[i].TB,
                              stepA, stepB, cplane, 28);
      if (!r.HasValue)
        continue;

      var pa = a.PointAt(r.Value.TA);
      var pb = b.PointAt(r.Value.TB);
      refined.Add((r.Value.TA, r.Value.TB, r.Value.Error,
        pa.DistanceToSquared(pickA) + pb.DistanceToSquared(pickB), pa, pb));
    }

    if (refined.Count == 0)
      return false;

    var bestError = double.MaxValue;
    foreach (var r in refined)
      if (r.Error < bestError)
        bestError = r.Error;

    var valid = refined.FindAll(r => r.Error <= Math.Max(0.02, bestError + 0.01));
    if (valid.Count == 0)
      valid = refined;

    valid.Sort((x, y) =>
    {
      var endPick = x.PB.DistanceToSquared(pickB)
        .CompareTo(y.PB.DistanceToSquared(pickB));
      if (endPick != 0)
        return endPick;

      var startPick = x.PA.DistanceToSquared(pickA)
        .CompareTo(y.PA.DistanceToSquared(pickA));
      return startPick != 0 ? startPick : x.Error.CompareTo(y.Error);
    });

    var best = valid[0];
    if (best.PA.DistanceTo(best.PB) <= RhinoMath.SqrtEpsilon)
      return false;

    line = new Line(best.PA, best.PB);
    return line.IsValid;
  }

  private static (double TA, double TB, double Error)? RefineBiTangent(
    Curve a, Curve b, double ta, double tb, double stepA, double stepB,
    Plane cplane, int iterations)
  {
    var da = a.Domain;
    var db = b.Domain;

    var current = BiTangentScore(a, b, ta, tb, cplane);
    if (!current.HasValue)
      return null;

    for (var iter = 0; iter < iterations; iter++)
    {
      var improved = false;
      var bestTA = ta;
      var bestTB = tb;
      var best = current.Value;

      for (var ia = -1; ia <= 1; ia++)
      {
        for (var ib = -1; ib <= 1; ib++)
        {
          if (ia == 0 && ib == 0) continue;
          var ca = Math.Max(da.T0, Math.Min(da.T1, ta + ia * stepA));
          var cb = Math.Max(db.T0, Math.Min(db.T1, tb + ib * stepB));
          var score = BiTangentScore(a, b, ca, cb, cplane);
          if (!score.HasValue || score.Value >= best) continue;
          best = score.Value;
          bestTA = ca;
          bestTB = cb;
          improved = true;
        }
      }

      if (improved)
      {
        ta = bestTA;
        tb = bestTB;
        current = best;
      }
      else
      {
        stepA *= 0.5;
        stepB *= 0.5;
      }
    }

    return (ta, tb, current.Value);
  }

  private static double? BiTangentScore(Curve a, Curve b, double ta, double tb, Plane cplane)
  {
    var pa = a.PointAt(ta);
    var pb = b.PointAt(tb);
    var dir2 = ToCPlane2d(pb - pa, cplane);
    if (!TryUnitize2d(dir2, out var dirU))
      return null;

    var tanA = ToCPlane2d(a.TangentAt(ta), cplane);
    var tanB = ToCPlane2d(b.TangentAt(tb), cplane);
    if (!TryUnitize2d(tanA, out var tanAU) || !TryUnitize2d(tanB, out var tanBU))
      return null;

    var crossA = Math.Abs((tanAU.X * dirU.Y) - (tanAU.Y * dirU.X));
    var crossB = Math.Abs((tanBU.X * dirU.Y) - (tanBU.Y * dirU.X));
    return crossA + crossB;
  }

  private static bool TryResolveConstraintToPoint(
    EndpointConstraint constraint,
    Point3d oppositePoint,
    bool preview,
    out Point3d constrainedPoint)
  {
    constrainedPoint = Point3d.Unset;
    Point3d? point;
    if (constraint.Kind == EndpointConstraintKind.Perpendicular)
    {
      point = PerpPointFromStartWithHint(
        oppositePoint,
        constraint.Curve,
        constraint.HintPoint,
        preview ? 80 : 240,
        preview ? 8 : 18);
      point ??= PerpFallbackToPointedSegment(
        oppositePoint,
        constraint.Curve,
        constraint.HintPoint,
        preview);
    }
    else
    {
      point = TangentPointFromStart(
        oppositePoint,
        constraint.Curve,
        constraint.HintPoint,
        preview ? 80 : 240,
        preview ? 8 : 18);
    }

    if (!point.HasValue)
      return false;

    constrainedPoint = point.Value;
    return constrainedPoint.IsValid;
  }

  private static bool TryPickConstraintStartPoint(
    RhinoDoc doc,
    LineLayerSession layerSession,
    EndpointConstraint constraint,
    out EndpointConstraint updatedConstraint,
    out Point3d selectedStart)
  {
    updatedConstraint = constraint;
    selectedStart = Point3d.Unset;

    using var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.SetCommandPrompt(
      layerSession.DecoratePrompt(doc, "Select perpendicular starting point"));
    if (!getPoint.Constrain(constraint.Curve, true))
      return false;

    getPoint.DynamicDraw += (_, e) =>
    {
      DrawCurveConstraintHelper(
        e.Display,
        doc,
        constraint with { HintPoint = e.CurrentPoint },
        e.CurrentPoint);
      DrawHiddenLayerWarning(e, doc, layerSession);
    };

    var result = getPoint.Get();
    layerSession.ObserveCurrentLayer(doc);
    if (result != GetResult.Point || getPoint.CommandResult() != Result.Success)
      return false;

    var point = getPoint.Point();
    if (!constraint.Curve.ClosestPoint(point, out var parameter))
      return false;

    selectedStart = constraint.Curve.PointAt(parameter);
    if (!selectedStart.IsValid)
      return false;

    updatedConstraint = constraint with
    {
      SeedParameter = parameter,
      HintPoint = selectedStart
    };
    return true;
  }

  private static bool TryResolveConstraintToDirection(
    EndpointConstraint constraint,
    Vector3d direction,
    bool preview,
    out Point3d constrainedPoint)
  {
    constrainedPoint = Point3d.Unset;
    if (!direction.Unitize())
      return false;

    var domain = constraint.Curve.Domain;
    if (!domain.IsValid || domain.Length <= RhinoMath.SqrtEpsilon)
      return false;

    var samples = preview ? 64 : 192;
    var values = new double[samples + 1];
    for (var i = 0; i <= samples; i++)
    {
      var t = domain.T0 + (domain.Length * i / samples);
      values[i] = ConstraintDirectionScore(constraint, direction, t);
    }

    var candidates = new List<(double Parameter, double Error)>();
    for (var i = 0; i <= samples; i++)
    {
      var previous = i == 0 ? double.MaxValue : values[i - 1];
      var next = i == samples ? double.MaxValue : values[i + 1];
      if (values[i] > previous || values[i] > next)
        continue;

      var t0 = domain.T0 + (domain.Length * Math.Max(0, i - 1) / samples);
      var t1 = domain.T0 + (domain.Length * Math.Min(samples, i + 1) / samples);
      candidates.Add(RefineConstraintDirection(
        constraint,
        direction,
        t0,
        t1,
        preview ? 10 : 22));
    }

    candidates.Sort((a, b) =>
    {
      var aValid = a.Error <= 0.015;
      var bValid = b.Error <= 0.015;
      if (aValid != bValid)
        return aValid ? -1 : 1;

      var aPoint = constraint.Curve.PointAt(a.Parameter);
      var bPoint = constraint.Curve.PointAt(b.Parameter);
      var byHint = aPoint.DistanceToSquared(constraint.HintPoint)
        .CompareTo(bPoint.DistanceToSquared(constraint.HintPoint));
      return byHint != 0 ? byHint : a.Error.CompareTo(b.Error);
    });

    if (candidates.Count == 0 || candidates[0].Error > 0.015)
      return false;

    constrainedPoint = constraint.Curve.PointAt(candidates[0].Parameter);
    return constrainedPoint.IsValid;
  }

  private static double ConstraintDirectionScore(
    EndpointConstraint constraint,
    Vector3d direction,
    double parameter)
  {
    var tangent = constraint.Curve.TangentAt(parameter);
    if (!tangent.Unitize())
      return double.MaxValue;

    var dot = Math.Abs(Vector3d.Multiply(tangent, direction));
    return constraint.Kind == EndpointConstraintKind.Perpendicular
      ? dot
      : Math.Abs(1.0 - dot);
  }

  private static (double Parameter, double Error) RefineConstraintDirection(
    EndpointConstraint constraint,
    Vector3d direction,
    double t0,
    double t1,
    int iterations)
  {
    const double phi = 0.61803398875;
    var a = Math.Min(t0, t1);
    var b = Math.Max(t0, t1);
    var c = b - ((b - a) * phi);
    var d = a + ((b - a) * phi);
    var fc = ConstraintDirectionScore(constraint, direction, c);
    var fd = ConstraintDirectionScore(constraint, direction, d);
    for (var i = 0; i < iterations; i++)
    {
      if (fc <= fd)
      {
        b = d;
        d = c;
        fd = fc;
        c = b - ((b - a) * phi);
        fc = ConstraintDirectionScore(constraint, direction, c);
      }
      else
      {
        a = c;
        c = d;
        fc = fd;
        d = a + ((b - a) * phi);
        fd = ConstraintDirectionScore(constraint, direction, d);
      }
    }

    return fc <= fd ? (c, fc) : (d, fd);
  }

  private static bool DirectionsAreParallel(
    Vector3d first,
    Vector3d second,
    double angularError)
  {
    if (!first.Unitize() || !second.Unitize())
      return false;
    return 1.0 - Math.Abs(Vector3d.Multiply(first, second)) <= angularError;
  }

  private static bool TryConstrainEndpointFromFixedStart(
    EndpointConstraint constraint,
    Point3d fixedStart,
    Point3d cursorPoint,
    out Point3d endpoint)
  {
    endpoint = Point3d.Unset;
    var tangent = constraint.Curve.TangentAt(constraint.SeedParameter);
    if (!tangent.Unitize())
      return false;

    var toCursor = cursorPoint - fixedStart;
    Vector3d constrained;
    if (constraint.Kind == EndpointConstraintKind.Tangent)
    {
      constrained = tangent * Vector3d.Multiply(toCursor, tangent);
    }
    else
    {
      constrained = toCursor -
                    (tangent * Vector3d.Multiply(toCursor, tangent));
    }

    if (constrained.IsTiny())
      return false;

    endpoint = fixedStart + constrained;
    return endpoint.IsValid;
  }

  private static bool DirectionMatchesConstraintAtSeed(
    EndpointConstraint constraint,
    Vector3d direction)
    => DirectionMatchesConstraintAtParameter(
      constraint,
      constraint.SeedParameter,
      direction);

  private static bool DirectionMatchesConstraintAtParameter(
    EndpointConstraint constraint,
    double parameter,
    Vector3d direction)
  {
    var tangent = constraint.Curve.TangentAt(parameter);
    if (!tangent.Unitize() || !direction.Unitize())
      return false;

    var dot = Math.Abs(Vector3d.Multiply(tangent, direction));
    var angleTolerance = ConstraintAngleToleranceRadians();
    return constraint.Kind == EndpointConstraintKind.Tangent
      ? 1.0 - dot <= Math.Max(1.0 - Math.Cos(angleTolerance), 1e-10)
      : dot <= Math.Max(Math.Sin(angleTolerance), 1e-6);
  }

  private static double ConstraintAngleToleranceRadians()
  {
    var maximum = RhinoMath.ToRadians(0.1);
    var modelTolerance = RhinoDoc.ActiveDoc?.ModelAngleToleranceRadians ?? maximum;
    if (!double.IsFinite(modelTolerance) || modelTolerance <= 0.0)
      return maximum;
    return Math.Min(modelTolerance, maximum);
  }

  private static double ConstraintCrossScoreTolerance()
    => Math.Max(Math.Sin(ConstraintAngleToleranceRadians()), 1e-6);

  private static bool TryResolveFixedStartConstraintPair(
    EndpointConstraint startConstraint,
    EndpointConstraint endConstraint,
    bool preview,
    out Line line)
  {
    line = Line.Unset;
    var fixedStart = startConstraint.Curve.PointAt(
      startConstraint.SeedParameter);
    if (!TryResolveConstraintToPoint(
          endConstraint,
          fixedStart,
          preview,
          out var constrainedEnd))
      return false;

    var candidate = new Line(fixedStart, constrainedEnd);
    if (!candidate.IsValid ||
        candidate.Length <= RhinoMath.SqrtEpsilon ||
        !DirectionMatchesConstraintAtSeed(startConstraint, candidate.Direction))
      return false;

    line = candidate;
    return true;
  }

  private static void DrawCurveConstraintHelper(
    Rhino.Display.DisplayPipeline display,
    RhinoDoc doc,
    EndpointConstraint constraint,
    Point3d point)
  {
    if (!constraint.Curve.ClosestPoint(point, out var parameter))
      parameter = constraint.SeedParameter;

    var tangent = constraint.Curve.TangentAt(parameter);
    if (!tangent.Unitize())
      return;

    const double HalfLengthPixels = 36.0;
    var halfLength = Math.Max(doc.ModelAbsoluteTolerance * 8.0, 0.1);
    var viewport = doc.Views.ActiveView?.ActiveViewport;
    if (viewport != null &&
        viewport.GetWorldToScreenScale(point, out var pixelsPerUnit) &&
        pixelsPerUnit > RhinoMath.SqrtEpsilon)
      halfLength = HalfLengthPixels / pixelsPerUnit;

    display.PushDepthTesting(false);
    display.PushDepthWriting(false);
    try
    {
      PreviewDisplay.DrawLine(
        display,
        point - (tangent * halfLength),
        point + (tangent * halfLength),
        Color.White);
      if (constraint.HintPoint.IsValid)
        display.DrawPoint(constraint.HintPoint, Color.White);
      display.DrawPoint(point, Color.Red);
    }
    finally
    {
      display.PopDepthWriting();
      display.PopDepthTesting();
    }
  }

  private static bool TryResolveConstraintPair(
    EndpointConstraint startConstraint,
    EndpointConstraint endConstraint,
    bool preview,
    out Line line,
    bool writeLog = true)
  {
    line = Line.Unset;
    var startSeeds = BuildConstraintSeeds(
      startConstraint.Curve.Domain,
      startConstraint.SeedParameter,
      preview);
    var endSeeds = BuildConstraintSeeds(
      endConstraint.Curve.Domain,
      endConstraint.SeedParameter,
      preview);

    var bestStartDistance = double.MaxValue;
    var bestEndDistance = double.MaxValue;
    var distanceTieTolerance = Math.Pow(
      Math.Max(RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.0, 1e-6) * 4.0,
      2.0);
    var rawCandidateCount = 0;
    var rejectedConstraintCount = 0;
    var candidateCount = 0;
    var bestT0 = RhinoMath.UnsetValue;
    var bestT1 = RhinoMath.UnsetValue;
    foreach (var startSeed in startSeeds)
    {
      foreach (var endSeed in endSeeds)
      {
        var t0 = startSeed;
        var t1 = endSeed;
        if (!Line.TryCreateBetweenCurves(
              startConstraint.Curve,
              endConstraint.Curve,
              ref t0,
              ref t1,
              startConstraint.Kind == EndpointConstraintKind.Perpendicular,
              endConstraint.Kind == EndpointConstraintKind.Perpendicular,
              out _))
          continue;

        var candidate = new Line(
          startConstraint.Curve.PointAt(t0),
          endConstraint.Curve.PointAt(t1));
        if (!candidate.IsValid || candidate.Length <= RhinoMath.SqrtEpsilon)
          continue;

        rawCandidateCount++;
        if (!DirectionMatchesConstraintAtParameter(
              startConstraint,
              t0,
              candidate.Direction) ||
            !DirectionMatchesConstraintAtParameter(
              endConstraint,
              t1,
              candidate.Direction))
        {
          rejectedConstraintCount++;
          continue;
        }

        candidateCount++;
        var endDistance = candidate.To.DistanceToSquared(endConstraint.HintPoint);
        var startDistance = candidate.From.DistanceToSquared(startConstraint.HintPoint);
        if (startDistance > bestStartDistance + distanceTieTolerance ||
            (Math.Abs(startDistance - bestStartDistance) <= distanceTieTolerance &&
             endDistance >= bestEndDistance))
          continue;

        bestStartDistance = startDistance;
        bestEndDistance = endDistance;
        bestT0 = t0;
        bestT1 = t1;
        line = candidate;
      }
    }

    if (!preview && writeLog)
    {
      Log.Write(
        "vLine.Pair",
        $"start={startConstraint.Kind} hint={startConstraint.HintPoint} " +
        $"end={endConstraint.Kind} hint={endConstraint.HintPoint} " +
        $"raw={rawCandidateCount} rejected={rejectedConstraintCount} " +
        $"candidates={candidateCount} t0={bestT0:R} t1={bestT1:R} " +
        $"lineFrom={line.From} lineTo={line.To}");
    }

    return line.IsValid;
  }

  private static IReadOnlyList<double> BuildConstraintSeeds(
    Interval domain,
    double preferred,
    bool preview)
  {
    if (!domain.IsValid || domain.Length <= RhinoMath.SqrtEpsilon)
      return new[] { preferred };

    var normalized = (preferred - domain.T0) / domain.Length;
    normalized = Math.Max(0.0, Math.Min(1.0, normalized));
    var offsets = preview
      ? new[] { 0.0, -0.08, 0.08 }
      : new[] { 0.0, -0.04, 0.04, -0.12, 0.12, -0.3, 0.3 };
    var divisions = preview ? 6 : 16;
    var seeds = new List<double>(offsets.Length + divisions + 1);
    void AddSeed(double unit)
    {
      unit = Math.Max(0.0, Math.Min(1.0, unit));
      var parameter = domain.T0 + (domain.Length * unit);
      if (seeds.Count == 0 || !seeds.Exists(value => Math.Abs(value - parameter) <= domain.Length * 1e-9))
        seeds.Add(parameter);
    }

    foreach (var offset in offsets)
      AddSeed(normalized + offset);

    for (var i = 0; i <= divisions; i++)
      AddSeed((double)i / divisions);

    return seeds;
  }

  private static Point3d? FindParallelRaySnap(
    Point3d startPoint,
    Vector3d dir,
    Point3d cursorPoint,
    IReadOnlyList<CurveCacheItem> curveCache,
    double tol)
  {
    const double rayLen = 1e6;
    var rayLine = new LineCurve(new Line(startPoint - dir * rayLen, startPoint + dir * rayLen));

    var cursorDist = cursorPoint.DistanceTo(startPoint);
    var snapTol = Math.Max(tol * 50.0, cursorDist * 0.08);
    var snapTolSq = snapTol * snapTol;

    Point3d? best = null;
    var bestDistSq = double.MaxValue;

    foreach (var (curve, _) in curveCache)
    {
      var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(rayLine, curve, tol, tol);
      if (events == null) continue;

      foreach (var ev in events)
      {
        var pt = ev.PointA;
        if (pt.DistanceTo(startPoint) < tol * 2.0) continue;
        if (Vector3d.Multiply(pt - startPoint, dir) < -tol) continue;

        var d2 = pt.DistanceToSquared(cursorPoint);
        if (d2 < bestDistSq)
        {
          bestDistSq = d2;
          best = pt;
        }
      }
    }

    return best.HasValue && bestDistSq <= snapTolSq ? best : null;
  }

  /// <summary>
  /// Finds the point on <paramref name="curve"/> whose CPlane-XY shadow is
  /// nearest to (curU, curV) — i.e., the true "project along CPlane Z" snap.
  /// Uses sampling + golden-section refinement so it works for any curve type.
  /// </summary>
  private static Point3d? ProjectCurveOnCPlane(Curve curve, double curU, double curV, Plane cplane)
  {
    var domain = curve.Domain;
    var domainLen = domain.T1 - domain.T0;
    if (domainLen <= 0) return null;

    // Number of samples: more for polylines with many segments.
    var segCount = curve is PolyCurve pc ? pc.SegmentCount
                 : curve is PolylineCurve pl ? Math.Max(1, pl.PointCount - 1)
                 : 50;
    var samples = Math.Min(500, Math.Max(50, segCount * 10));
    var dt = domainLen / samples;

    double bestT = domain.T0, bestD2 = double.MaxValue;
    for (var i = 0; i <= samples; i++)
    {
      var t = domain.T0 + i * dt;
      cplane.ClosestParameter(curve.PointAt(t), out var pu, out var pv);
      var d2 = (pu - curU) * (pu - curU) + (pv - curV) * (pv - curV);
      if (d2 < bestD2) { bestD2 = d2; bestT = t; }
    }

    // Golden-section refinement within ±2 sample widths of the best hit.
    var a = Math.Max(domain.T0, bestT - dt * 2);
    var b = Math.Min(domain.T1, bestT + dt * 2);
    const double phi = 0.61803398875;
    var c = b - (b - a) * phi;
    var d = a + (b - a) * phi;

    double Score(double t2)
    {
      cplane.ClosestParameter(curve.PointAt(t2), out var pu, out var pv);
      return (pu - curU) * (pu - curU) + (pv - curV) * (pv - curV);
    }

    var fc = Score(c); var fd = Score(d);
    for (var i = 0; i < 30; i++)
    {
      if (fc < fd) { b = d; d = c; fd = fc; c = b - (b - a) * phi; fc = Score(c); }
      else         { a = c; c = d; fc = fd; d = a + (b - a) * phi; fd = Score(d); }
    }

    return curve.PointAt(0.5 * (a + b));
  }

  /// <summary>
  /// Projects <paramref name="point"/> onto <paramref name="geometry"/> along the CPlane Z axis.
  /// Fires a ray through the point in the ±Z direction and returns the nearest hit.
  /// Falls back to 3D closest point when no intersection is found.
  /// Supports Curve, Brep, Surface, and Mesh.
  /// </summary>
  private static Point3d? ProjectClosestPoint(GeometryBase geometry, Point3d point, Plane cplane)
  {
    var tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
    // Always use a fresh cplane from the active viewport so the projection direction
    // is correct even when the closure's captured cplane is stale.
    var activeCplane = RhinoDoc.ActiveDoc?.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? cplane;
    var dir = activeCplane.ZAxis;

    // Flatten point onto the CPlane (zero out the Z component) so the ray starts
    // exactly on the CPlane, not at whatever height the 3D cursor is at.
    if (!activeCplane.ClosestParameter(point, out var flatU, out var flatV)) { flatU = 0; flatV = 0; }
    var flatPoint = activeCplane.PointAt(flatU, flatV);

    Log.Write("vLine.ProjectTo", $"cursor=({point.X:F3},{point.Y:F3},{point.Z:F3}) flat=({flatPoint.X:F3},{flatPoint.Y:F3},{flatPoint.Z:F3}) dir=({dir.X:F3},{dir.Y:F3},{dir.Z:F3}) geom={geometry.GetType().Name}");

    if (geometry is Curve curve)
    {
      // Project cursor along CPlane Z onto the curve.
      // Strategy: find the curve parameter whose CPlane-XY shadow is nearest to
      // the cursor's CPlane-XY position (flatU, flatV).  This is different from
      // 3D-closest when the curve is at a different elevation than the cursor.
      var result = ProjectCurveOnCPlane(curve, flatU, flatV, activeCplane);
      if (result.HasValue)
      {
        Log.Write("vLine.ProjectTo", $"  Curve CPlane-shadow result=({result.Value.X:F3},{result.Value.Y:F3},{result.Value.Z:F3})");
        return result;
      }
      Log.Write("vLine.ProjectTo", "  Curve CPlane-shadow: no result");
    }
    else if (geometry is Brep brep)
    {
      var pts = Rhino.Geometry.Intersect.Intersection.ProjectPointsToBreps(
        new[] { brep }, new[] { flatPoint }, dir, tol);
      Log.Write("vLine.ProjectTo", $"  ProjectToBreps hits={pts?.Length ?? 0}");
      if (pts != null && pts.Length > 0)
      {
        Point3d? best = null; var bestD = double.MaxValue;
        foreach (var p in pts) { var d = p.DistanceTo(flatPoint); if (d < bestD) { bestD = d; best = p; } }
        Log.Write("vLine.ProjectTo", $"  result=({best!.Value.X:F3},{best.Value.Y:F3},{best.Value.Z:F3})");
        return best;
      }
    }
    else if (geometry is Surface srf)
    {
      var brepSrf = srf.ToBrep();
      if (brepSrf != null)
      {
        var pts = Rhino.Geometry.Intersect.Intersection.ProjectPointsToBreps(
          new[] { brepSrf }, new[] { flatPoint }, dir, tol);
        Log.Write("vLine.ProjectTo", $"  ProjectToSrf hits={pts?.Length ?? 0}");
        if (pts != null && pts.Length > 0)
        {
          Log.Write("vLine.ProjectTo", $"  result=({pts[0].X:F3},{pts[0].Y:F3},{pts[0].Z:F3})");
          return pts[0];
        }
      }
      if (srf.ClosestPoint(flatPoint, out var u, out var v))
      {
        var fb = srf.PointAt(u, v);
        Log.Write("vLine.ProjectTo", $"  fallback srf closest=({fb.X:F3},{fb.Y:F3},{fb.Z:F3})");
        return fb;
      }
    }
    else if (geometry is Mesh mesh)
    {
      var pts = Rhino.Geometry.Intersect.Intersection.ProjectPointsToMeshes(
        new[] { mesh }, new[] { flatPoint }, dir, tol);
      Log.Write("vLine.ProjectTo", $"  ProjectToMesh hits={pts?.Length ?? 0}");
      if (pts != null && pts.Length > 0)
      {
        Log.Write("vLine.ProjectTo", $"  result=({pts[0].X:F3},{pts[0].Y:F3},{pts[0].Z:F3})");
        return pts[0];
      }
      var cp = mesh.ClosestPoint(flatPoint);
      if (cp.IsValid)
      {
        Log.Write("vLine.ProjectTo", $"  fallback mesh closest=({cp.X:F3},{cp.Y:F3},{cp.Z:F3})");
        return cp;
      }
    }
    Log.Write("vLine.ProjectTo", "  -> null (no result)");
    return null;
  }

  private static void DebugLog(string msg)
  {
    if (!_debugMode) return;
    var line = $"[vLine {DateTime.Now:HH:mm:ss.fff}] {msg}";
    RhinoApp.WriteLine(line);
    Log.Write("vLine.Debug", msg);
  }

  private static void DrawFeedbackGeometry(
    Rhino.Display.DisplayPipeline display,
    GeometryBase geometry,
    Color color,
    int thickness)
  {
    switch (geometry)
    {
      case Curve curve:
        PreviewDisplay.DrawCurve(display, curve, color, thickness - 1);
        break;
      case Brep brep:
        PreviewDisplay.DrawBrepWires(display, brep, color, thickness - 1);
        break;
      case Mesh mesh:
        PreviewDisplay.DrawMeshWires(display, mesh, color, thickness - 1);
        break;
      case Extrusion extrusion:
      {
        using var brep = extrusion.ToBrep();
        if (brep != null)
          PreviewDisplay.DrawBrepWires(display, brep, color, thickness - 1);
        break;
      }
      case Surface surface:
      {
        using var brep = surface.ToBrep();
        if (brep != null)
          PreviewDisplay.DrawBrepWires(display, brep, color, thickness - 1);
        break;
      }
    }
  }

  private static void DrawHiddenLayerWarning(
    GetPointDrawEventArgs e,
    RhinoDoc doc,
    LineLayerSession layerSession)
  {
    var layerName = layerSession.HiddenLayerName(doc);
    if (layerName == null || !e.CurrentPoint.IsValid)
      return;

    var client = e.Viewport.WorldToClient(e.CurrentPoint);
    var position = new Point2d(client.X + 18.0, client.Y + 26.0);
    var shadow = new Point2d(position.X + 1.0, position.Y + 1.0);
    var message = $"Layer hidden: {layerName}";
    e.Display.Draw2dText(message, Color.Black, shadow, false, 12);
    e.Display.Draw2dText(message, Color.OrangeRed, position, false, 12);
  }

  private static void EnsureDebugLog()
  {
    RhinoApp.WriteLine($"[vLine] Debug log: {Log.FilePath ?? "unavailable"}");
  }

  private static void DeleteObjectIfValid(RhinoDoc doc, Guid id)
  {
    if (id == Guid.Empty)
      return;

    if (doc.Objects.FindId(id) != null)
      _ = doc.Objects.Delete(id, true);
  }

  private sealed class LineUndoRecordSession
  {
    private static readonly Queue<PendingLineFinalization> PendingFinalizations = new();
    private static readonly Queue<PendingLineFinalization> PendingRecordCreations = new();
    private static readonly HashSet<uint> DeferredRuns = new();
    private static bool _idleHandlerAttached;
    private static bool _recordCreationHandlerAttached;
    private static bool _restartHandlerAttached;

    private readonly uint _docSerial;
    private bool _queued;

    public LineUndoRecordSession(RhinoDoc doc)
    {
      _docSerial = doc.RuntimeSerialNumber;
      Token = Guid.NewGuid().ToString("N");
    }

    public string Token { get; }

    public static bool DeferRunUntilFinalized(RhinoDoc doc)
    {
      var docSerial = doc.RuntimeSerialNumber;
      var pending = PendingFinalizations.Any(item => item.DocSerial == docSerial) ||
                    PendingRecordCreations.Any(item => item.DocSerial == docSerial);
      if (!pending)
        return false;

      DeferredRuns.Add(docSerial);
      Log.Write("vLine", "run deferred until undo finalization completes");
      return true;
    }

    public void QueueFinalization()
    {
      if (_queued)
        return;
      _queued = true;

      var doc = RhinoDoc.FromRuntimeSerialNumber(_docSerial);
      if (doc == null)
        return;

      var snapshots = doc.Objects
        .GetObjectList(ObjectType.Curve)
        .Where(obj =>
          obj != null &&
          !obj.IsDeleted &&
          string.Equals(
            obj.Attributes.GetUserString(UndoSessionMarkerKey),
            Token,
            StringComparison.Ordinal))
        .OrderBy(obj => obj.RuntimeSerialNumber)
        .Select(CreateSnapshot)
        .Where(snapshot => snapshot != null)
        .Cast<LineOutputSnapshot>()
        .ToList();

      if (snapshots.Count == 0)
      {
        Log.Write("vLine", "undo finalization skipped: no surviving outputs");
        return;
      }

      PendingFinalizations.Enqueue(new PendingLineFinalization(_docSerial, snapshots));
      Log.Write("vLine", "undo finalization queued outputs={0}", snapshots.Count);
      if (_idleHandlerAttached)
        return;

      _idleHandlerAttached = true;
      RhinoApp.Idle += OnFinalizeIdle;
    }

    private static LineOutputSnapshot? CreateSnapshot(RhinoObject obj)
    {
      if (obj.Geometry is not Curve curve)
        return null;

      var duplicate = curve.DuplicateCurve();
      if (duplicate == null)
        return null;

      var attributes = obj.Attributes.Duplicate();
      attributes.DeleteUserString(UndoSessionMarkerKey);
      return new LineOutputSnapshot(
        obj.Id,
        duplicate,
        attributes,
        curve is PolylineCurve);
    }

    private static void OnFinalizeIdle(object? sender, EventArgs e)
    {
      RhinoApp.Idle -= OnFinalizeIdle;
      _idleHandlerAttached = false;

      if (Command.InCommand())
      {
        _idleHandlerAttached = true;
        RhinoApp.Idle += OnFinalizeIdle;
        return;
      }

      while (PendingFinalizations.TryDequeue(out var pending))
      {
        if (RollbackCombinedRecord(pending, out var prepared))
        {
          CreateIndividualRecords(prepared, out var continuation);
          if (continuation != null)
            PendingRecordCreations.Enqueue(continuation);
        }
      }

      if (PendingRecordCreations.Count > 0 && !_recordCreationHandlerAttached)
      {
        _recordCreationHandlerAttached = true;
        RhinoApp.Idle += OnCreateRecordsIdle;
      }
      else if (PendingRecordCreations.Count == 0)
      {
        QueueDeferredRuns();
      }
    }

    private static bool RollbackCombinedRecord(
      PendingLineFinalization pending,
      out PendingLineFinalization prepared)
    {
      prepared = pending;
      var doc = RhinoDoc.FromRuntimeSerialNumber(pending.DocSerial);
      if (doc == null)
        return false;

      var redrawWasEnabled = doc.Views.RedrawEnabled;
      doc.Views.RedrawEnabled = false;
      var presentCount = pending.Outputs.Count(output => IsPresent(doc, output.OriginalId));
      var rolledBack = presentCount == 0;
      var undoResult = false;
      try
      {
        if (presentCount == pending.Outputs.Count)
        {
          undoResult = RunSilentHistoryCommand(pending.DocSerial, "_Undo");
          rolledBack = pending.Outputs.All(output => !IsPresent(doc, output.OriginalId));
        }
      }
      catch
      {
        rolledBack = false;
      }

      Log.Write(
        "vLine",
        "undo finalization rollback present={0}/{1} undo_result={2} rolled_back={3}",
        presentCount,
        pending.Outputs.Count,
        undoResult,
        rolledBack);

      if (!rolledBack)
      {
        doc.Views.RedrawEnabled = redrawWasEnabled;
        RhinoApp.WriteLine("vLine: could not separate the completed objects into individual undo records.");
        return false;
      }

      prepared = pending with
      {
        RedrawSuppressed = true,
        RedrawWasEnabled = redrawWasEnabled
      };
      return true;
    }

    private static void OnCreateRecordsIdle(object? sender, EventArgs e)
    {
      RhinoApp.Idle -= OnCreateRecordsIdle;
      _recordCreationHandlerAttached = false;

      var retry = new List<PendingLineFinalization>();
      while (PendingRecordCreations.TryDequeue(out var pending))
      {
        CreateIndividualRecords(pending, out var continuation);
        if (continuation != null)
          retry.Add(continuation);
      }

      foreach (var pending in retry)
      {
        if (pending.Attempts >= 5)
        {
          var doc = RhinoDoc.FromRuntimeSerialNumber(pending.DocSerial);
          if (doc != null &&
              (doc.UndoRecordingIsActive || doc.UndoActive || doc.RedoActive))
          {
            PendingRecordCreations.Enqueue(pending);
            continue;
          }

          RestoreCombinedResult(pending);
          continue;
        }

        PendingRecordCreations.Enqueue(pending);
      }

      if (PendingRecordCreations.Count > 0 && !_recordCreationHandlerAttached)
      {
        _recordCreationHandlerAttached = true;
        RhinoApp.Idle += OnCreateRecordsIdle;
      }
      else if (PendingRecordCreations.Count == 0)
      {
        QueueDeferredRuns();
      }
    }

    private static void RestoreCombinedResult(PendingLineFinalization pending)
    {
      var doc = RhinoDoc.FromRuntimeSerialNumber(pending.DocSerial);
      if (doc == null)
        return;

      var restored = false;
      if (pending.NextOutputIndex == 0)
      {
        var redoResult = RunSilentHistoryCommand(pending.DocSerial, "_Redo");
        restored = redoResult && pending.Outputs.All(output => IsPresent(doc, output.OriginalId));
        Log.Write(
          "vLine",
          "undo record creation fallback redo_result={0} restored={1}",
          redoResult,
          restored);
      }

      if (!restored)
      {
        foreach (var output in pending.Outputs.Skip(pending.NextOutputIndex))
        {
          _ = doc.Objects.AddCurve(
            output.Curve.DuplicateCurve(),
            output.Attributes.Duplicate());
        }

        Log.Write(
          "vLine",
          "undo record creation fallback restored snapshots={0}",
          pending.Outputs.Count - pending.NextOutputIndex);
      }

      RhinoApp.WriteLine("vLine: separate undo records were unavailable; restored the completed geometry.");
      RestoreRedraw(doc, pending);
    }

    private static void CreateIndividualRecords(
      PendingLineFinalization pending,
      out PendingLineFinalization? continuation)
    {
      continuation = null;
      var doc = RhinoDoc.FromRuntimeSerialNumber(pending.DocSerial);
      if (doc == null)
        return;

      if (doc.UndoRecordingIsActive || doc.UndoActive || doc.RedoActive)
      {
        Log.Write(
          "vLine",
          "undo record creation deferred attempts={0} recording_active={1} undo_active={2} redo_active={3}",
          pending.Attempts,
          doc.UndoRecordingIsActive,
          doc.UndoActive,
          doc.RedoActive);
        continuation = pending with { Attempts = pending.Attempts + 1 };
        return;
      }

      var added = 0;
      for (var index = pending.NextOutputIndex; index < pending.Outputs.Count; index++)
      {
        var output = pending.Outputs[index];
        var description = output.IsPolyline ? "vLine Polyline" : "vLine";
        var undoRecord = doc.BeginUndoRecord(description);
        if (undoRecord == 0)
        {
          Log.Write(
            "vLine",
            "undo record creation deferred attempts={0} begin returned zero type={1} added={2}",
            pending.Attempts,
            output.IsPolyline ? "polyline" : "line",
            added);
          continuation = pending with
          {
            NextOutputIndex = index,
            Attempts = pending.Attempts + 1
          };
          return;
        }

        var objectId = Guid.Empty;
        try
        {
          objectId = doc.Objects.AddCurve(
            output.Curve.DuplicateCurve(),
            output.Attributes.Duplicate());
        }
        finally
        {
          if (undoRecord != 0)
            doc.EndUndoRecord(undoRecord);
        }

        if (objectId == Guid.Empty)
        {
          Log.Write("vLine", "undo finalization add failed type={0}", description);
          continue;
        }

        added++;
        Log.Write(
          "vLine",
          "undo record created type={0} record={1} object={2}",
          output.IsPolyline ? "polyline" : "line",
          undoRecord,
          objectId);
      }

      Log.Write("vLine", "undo finalization completed records={0}", added);
      RestoreRedraw(doc, pending);
    }

    private static void RestoreRedraw(RhinoDoc doc, PendingLineFinalization pending)
    {
      if (pending.RedrawSuppressed)
        doc.Views.RedrawEnabled = pending.RedrawWasEnabled;
      if (pending.RedrawWasEnabled)
        doc.Views.Redraw();
    }

    private static void QueueDeferredRuns()
    {
      if (DeferredRuns.Count == 0 || _restartHandlerAttached)
        return;

      _restartHandlerAttached = true;
      RhinoApp.Idle += OnRestartDeferredRuns;
    }

    private static void OnRestartDeferredRuns(object? sender, EventArgs e)
    {
      RhinoApp.Idle -= OnRestartDeferredRuns;
      _restartHandlerAttached = false;

      var doc = RhinoDoc.ActiveDoc;
      if (doc == null || !DeferredRuns.Remove(doc.RuntimeSerialNumber))
        return;

      _ = RhinoApp.RunScript("_vLine", false);
    }

    private static bool IsPresent(RhinoDoc doc, Guid objectId)
    {
      var obj = doc.Objects.FindId(objectId);
      return obj != null && !obj.IsDeleted;
    }

    private static bool RunSilentHistoryCommand(uint docSerial, string command)
    {
      var restoreEcho = Rhino.ApplicationSettings.AppearanceSettings
        .EchoCommandsToHistoryWindow;
      var script = restoreEcho
        ? $"_NoEcho {command} _Echo"
        : $"_NoEcho {command}";
      return RhinoApp.RunScript(
        docSerial,
        script,
        false);
    }
  }

  private sealed record PendingLineFinalization(
    uint DocSerial,
    IReadOnlyList<LineOutputSnapshot> Outputs,
    int NextOutputIndex = 0,
    int Attempts = 0,
    bool RedrawSuppressed = false,
    bool RedrawWasEnabled = true);

  private sealed record LineOutputSnapshot(
    Guid OriginalId,
    Curve Curve,
    ObjectAttributes Attributes,
    bool IsPolyline);

  private static int ClampIndex(int value, int count)
  {
    if (count <= 0)
      return 0;
    if (value < 0)
      return 0;
    return value >= count ? count - 1 : value;
  }

  private sealed class LineLayerSession
  {
    private readonly string _undoSessionToken;
    private int _observedCurrentLayerIndex;
    private int? _externalLayerOverride;

    public LineLayerSession(RhinoDoc doc, string optionLayerName, string undoSessionToken)
    {
      OptionLayerName = NormalizeLayerOption(optionLayerName);
      _undoSessionToken = undoSessionToken;
      _observedCurrentLayerIndex = doc.Layers.CurrentLayerIndex;
    }

    public string OptionLayerName { get; private set; }

    public string DecoratePrompt(RhinoDoc doc, string prompt)
    {
      var hiddenLayerName = HiddenLayerName(doc);
      return hiddenLayerName == null
        ? prompt
        : $"{prompt} [Layer hidden: {hiddenLayerName}]";
    }

    public string? HiddenLayerName(RhinoDoc doc)
    {
      var layerIndex = ResolveLayerIndex(doc);
      if (!IsUsableLayer(doc, layerIndex) || IsEffectivelyVisible(doc.Layers[layerIndex], doc))
        return null;

      return doc.Layers[layerIndex].FullPath;
    }

    public void ApplyOption(RhinoDoc doc, string optionLayerName)
    {
      OptionLayerName = NormalizeLayerOption(optionLayerName);
      _externalLayerOverride = null;
      _observedCurrentLayerIndex = doc.Layers.CurrentLayerIndex;
    }

    public void ObserveCurrentLayer(RhinoDoc doc)
    {
      var currentLayerIndex = doc.Layers.CurrentLayerIndex;
      if (currentLayerIndex == _observedCurrentLayerIndex)
        return;

      _observedCurrentLayerIndex = currentLayerIndex;
      _externalLayerOverride = IsUsableLayer(doc, currentLayerIndex)
        ? currentLayerIndex
        : null;

      var layerName = _externalLayerOverride.HasValue
        ? doc.Layers[_externalLayerOverride.Value].FullPath
        : "<invalid>";
      Log.Write("vLine",
        $"  current layer changed externally; session target={layerName}");
    }

    public ObjectAttributes CreateAttributes(RhinoDoc doc)
    {
      var attributes = new ObjectAttributes { LayerIndex = ResolveLayerIndex(doc) };
      attributes.SetUserString(UndoSessionMarkerKey, _undoSessionToken);
      return attributes;
    }

    public Color ResolveColor(RhinoDoc doc)
    {
      var layerIndex = ResolveLayerIndex(doc);
      return IsUsableLayer(doc, layerIndex)
        ? doc.Layers[layerIndex].Color
        : Color.White;
    }

    private int ResolveLayerIndex(RhinoDoc doc)
    {
      ObserveCurrentLayer(doc);

      if (_externalLayerOverride.HasValue &&
          IsUsableLayer(doc, _externalLayerOverride.Value))
      {
        return _externalLayerOverride.Value;
      }

      if (OptionLayerName != CurrentLayerOption)
      {
        var configuredIndex = doc.Layers.FindByFullPath(
          OptionLayerName, RhinoMath.UnsetIntIndex);
        if (IsUsableLayer(doc, configuredIndex))
          return configuredIndex;
      }

      var currentLayerIndex = doc.Layers.CurrentLayerIndex;
      return IsUsableLayer(doc, currentLayerIndex)
        ? currentLayerIndex
        : 0;
    }

    private static bool IsEffectivelyVisible(Layer layer, RhinoDoc doc)
    {
      var visited = new HashSet<Guid>();
      var current = layer;
      while (current != null)
      {
        if (!current.IsVisible)
          return false;

        var parentId = current.ParentLayerId;
        if (parentId == Guid.Empty)
          return true;
        if (!visited.Add(parentId))
          return false;

        current = doc.Layers.FindId(parentId);
      }

      return false;
    }
  }

  private sealed class CurveCacheState
  {
    public CurveCacheState(List<CurveCacheItem> curveCache, DateTime nextRefreshUtc)
    {
      CurveCache = curveCache;
      NextRefreshUtc = nextRefreshUtc;
    }

    public List<CurveCacheItem> CurveCache { get; set; }
    public DateTime NextRefreshUtc { get; set; }
  }

  private readonly record struct CurveCacheItem(Curve Curve, BoundingBox BoundingBox);

  private sealed class TemporaryGeometryHighlight : Rhino.Display.DisplayConduit, IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly GeometryBase _geometry;
    private readonly Color _color;

    private TemporaryGeometryHighlight(
      RhinoDoc doc,
      GeometryBase geometry,
      Color color)
    {
      _doc = doc;
      _geometry = geometry;
      _color = color;
      Enabled = true;
      _doc.Views.Redraw();
    }

    public static TemporaryGeometryHighlight? Create(
      RhinoDoc doc,
      GeometryBase geometry,
      Color color)
    {
      var duplicate = DuplicatePickedGeometry(geometry);
      if (duplicate == null)
        return null;
      return new TemporaryGeometryHighlight(doc, duplicate, color);
    }

    protected override void DrawForeground(Rhino.Display.DrawEventArgs e)
      => DrawFeedbackGeometry(e.Display, _geometry, _color, 3);

    public void Dispose()
    {
      Enabled = false;
      _geometry.Dispose();
      _doc.Views.Redraw();
    }
  }

  private readonly record struct ScreenGeometryPick(
    Guid ObjectId,
    ComponentIndex ComponentIndex,
    Point3d PickPoint);

  private readonly record struct PickedGeometry(
    GeometryBase Geometry,
    Point3d PickPoint,
    Guid ObjectId,
    ComponentIndex ComponentIndex);

  private readonly record struct ScreenCurvePick(
    Guid ObjectId,
    ComponentIndex ComponentIndex,
    Point3d PickPoint);

  private readonly record struct ScreenCurveCandidate(
    ScreenCurvePick Pick,
    double Distance,
    double Depth,
    bool EndpointHit,
    string CurveType);

  private readonly record struct PickedCurve(
    Curve Curve,
    Point3d PickPoint,
    Guid ObjectId,
    ComponentIndex ComponentIndex);

  private enum EndpointConstraintKind
  {
    Tangent,
    Perpendicular
  }

  private readonly record struct EndpointConstraint(
    Curve Curve,
    double SeedParameter,
    Point3d HintPoint,
    EndpointConstraintKind Kind,
    Guid ObjectId = default,
    ComponentIndex ComponentIndex = default);

  private readonly record struct EndAnchor(
    Point3d Point,
    Vector3d Direction);

  private readonly record struct ConstraintState(
    string? Mode,
    bool PersistConstraint,
    int Priority,
    double Length,
    bool AngleLock,
    double Angle,
    bool AngleRelative);

  private readonly record struct FirstPointResult(
    bool HasPoint,
    Point3d Point,
    bool BothSides,
    int ChainMode,
    bool Completed,
    EndpointConstraint? Constraint,
    Vector3d? Direction,
    GeometryBase? FeedbackGeometry)
  {
    public bool IsUndo { get; init; } = false;
    public bool IsRedo { get; init; } = false;

    public static FirstPointResult WithPoint(Point3d point, bool bothSides, int chainMode)
      => new(true, point, bothSides, chainMode, false, null, null, null);

    public static FirstPointResult WithConstraint(
      Point3d point,
      bool bothSides,
      int chainMode,
      EndpointConstraint constraint)
      => new(true, point, bothSides, chainMode, false, constraint, null, null);

    public static FirstPointResult WithDirection(
      Point3d point,
      Vector3d direction,
      bool bothSides,
      int chainMode,
      GeometryBase? feedbackGeometry)
      => new(
        true,
        point,
        bothSides,
        chainMode,
        false,
        null,
        direction,
        feedbackGeometry);

    public static FirstPointResult CompletedResult(bool bothSides, int chainMode)
      => new(false, Point3d.Unset, bothSides, chainMode, true, null, null, null);

    public static FirstPointResult None(bool bothSides, int chainMode)
      => new(false, Point3d.Unset, bothSides, chainMode, false, null, null, null);

    public static FirstPointResult Undo(bool bothSides, int chainMode)
      => new(false, Point3d.Unset, bothSides, chainMode, false, null, null, null) { IsUndo = true };

    public static FirstPointResult Redo(bool bothSides, int chainMode)
      => new(false, Point3d.Unset, bothSides, chainMode, false, null, null, null) { IsRedo = true };
  }

  private readonly record struct SecondPointResult(
    bool HasPoint,
    Point3d StartPoint,
    Point3d Point,
    bool BothSides,
    int ChainMode,
    ConstraintState? State)
  {
    public bool IsUndo { get; init; } = false;
    public bool IsRedo { get; init; } = false;

    public static SecondPointResult WithPoint(Point3d startPoint, Point3d point, bool bothSides, int chainMode, ConstraintState state)
      => new(true, startPoint, point, bothSides, chainMode, state);

    public static SecondPointResult None(bool bothSides, int chainMode, ConstraintState state)
      => new(false, Point3d.Unset, Point3d.Unset, bothSides, chainMode, state);

    public static SecondPointResult Undo(bool bothSides, int chainMode, ConstraintState state)
      => new(false, Point3d.Unset, Point3d.Unset, bothSides, chainMode, state) { IsUndo = true };

    public static SecondPointResult Redo(bool bothSides, int chainMode, ConstraintState state)
      => new(false, Point3d.Unset, Point3d.Unset, bothSides, chainMode, state) { IsRedo = true };
  }
}
