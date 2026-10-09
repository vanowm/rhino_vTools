namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Drawing;
  using System.Globalization;
  using System.Linq;
  using System.Text.Json;
  using System.Text.RegularExpressions;
  using Rhino;
  using Rhino.Commands;
  using Rhino.DocObjects;
  using Rhino.DocObjects.Tables;
  using Rhino.Display;
  using Rhino.Geometry;
  using Rhino.Geometry.Intersect;
  using Rhino.Input;
  using Rhino.Input.Custom;


  /// <summary>
  /// Creates angle-bisector references and perpendicular shade lines from
  /// clicked curve ends, with optional non-crossing connected boundaries.
  /// </summary>
  [CommandStyle(Style.ScriptRunner | Style.NotUndoable)]
  public sealed partial class vShade : vToolsCommand
  {
    // Option defaults and customizable output settings
    private const double DefaultOffset = 4.0; // Bisector length in model units; zero or greater.
    private const double DefaultChamfer = 1.0; // Perpendicular chamfer width in model units; zero or greater.
    private const double DefaultReinforcement = 6.0; // Arc radius in model units; zero disables reinforcement arcs.
    private const bool DefaultScallop = false; // true replaces perimeter connector lines with inward arcs; false keeps straight connectors.
    private const double DefaultScallopSize = 5.0; // Positive midpoint bulge: model units, or percent of connector span when DefaultScallopPercent is true.
    private const bool DefaultScallopPercent = true; // true interprets scallop size as percent of each connector span; false uses model units.
    private const double DefaultCutOffset = 1.5; // Outward cut distance in model units; zero omits the cut curve.
    private const string DefaultCutLayer = "CUT1"; // Existing or new Rhino layer path for the outward cut curve.
    private const bool DefaultCreateReinf = false; // true creates detached finished-size reinforcement parts; false keeps reinforcement arcs only on the shade.
    private const double ReinforcementPartGapInches = 2.0; // Minimum physical clearance in inches between each detached part and the outer cut outline.
    private const int ReinforcementPartPlacementPasses = 24; // Binary-search passes for outward placement at the requested clearance; positive integer.
    private const string ReinforcementPartObjectName = "ShadeReinforcementPart"; // Object name for detached finished-size reinforcement cutting outlines.
    private const string ReinforcementPartLabelName = "ShadeReinforcementPartLabel"; // Object name for detached copies of corner-identifying labels.
    private const string ReinforcementPartGroupPrefix = "vShade_Reinf_"; // Separate output group prefix followed by session and corner identifiers.
    private const double TunePickRadiusPixels = 14.0; // Maximum screen-space distance in pixels for picking a connector or corner.
    private const double DefaultLabelHeight = 0.5; // Reinforcement label height in model units; positive.
    private const double DefaultLabelDimensionScale = 1.0; // Positive annotation display scale for fixed-height labels.
    private const double LabelRadialPosition = 0.5; // Fraction from cap midpoint toward reinforcement arc midpoint; zero to one.
    private const string DefaultLabel = "1"; // Document-local next label; numeric or alphabetic suffix advances after each corner, empty disables labels.
    private const bool DefaultConnect = true; // true grows non-crossing boundaries between consecutive perpendiculars; false leaves perpendiculars separate.
    private const bool DefaultJoin = true; // true outputs each connected boundary as one curve; false keeps its segments separate.
    private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Rhino layer path or the shared current-layer sentinel.
    private const string ReferenceLayerName = "Reference"; // Rhino layer name used for every bisector line.
    private static readonly Color ReferenceLayerColor = Color.White; // RGB color assigned when the Reference layer must be created.
    private const double EndpointCoincidenceToleranceFactor = 4.0; // Model absolute-tolerance multiplier used to treat clicked endpoints as one corner.
    private const double DirectionToleranceFactor = 10.0; // Rhino zero-tolerance multiplier used for projected directions and intersection tests.
    private const string BisectorObjectName = "ShadeBisector"; // Rhino object name assigned to angle-bisector reference lines.
    private const string PerpendicularObjectName = "ShadePerpendicular"; // Rhino object name assigned to perpendicular end-cap lines.
    private const string ConnectionObjectName = "ShadeConnection"; // Rhino object name assigned to connected-boundary side lines.
    private const string ScallopObjectName = "ShadeScallop"; // Rhino object name assigned to inward-curved boundary connections.
    private const string BoundaryObjectName = "ShadeBoundary"; // Rhino object name assigned to joined closed boundaries.
    private const string ReinforcementObjectName = "ShadeReinforcement"; // Rhino object name assigned to cap-centered arcs between boundary sides.
    private const string LabelObjectName = "ShadeLabel"; // Rhino object name assigned to reinforcement text.
    private const string CutObjectName = "ShadeCut"; // Rhino object name assigned to the outward closed cut curve.
    private const string MetadataPrefix = "vShade."; // Prefix for user-string keys written to every vShade output object.
    private const string MetadataVersion = "1"; // Serialized vShade metadata schema version.
    private const string SessionStateSection = "vShade.sessions"; // Document-string section containing resumable shade snapshots keyed by session GUID.
    private const string ShadeGroupPrefix = "vShade_"; // Prefix for the output group name; the session GUID follows it.
    private const double DefaultSplitWidth = 63.0; // Material width in model units; must exceed twice the 0.5-unit joint overlap.
    private const double SplitJointOverlap = 0.5; // Total overlap across each split in model units; each part extends 0.25 past the seam.
    private const double SplitLayoutGap = 1.0; // Empty model-unit spacing between laid-out copies.
    private const double SplitAngleStepDegrees = 5.0; // Angular sampling interval in degrees for initial material-length search.
    private const int SplitBoundarySamples = 256; // Boundary samples for inexpensive initial layout scoring; positive integer.
    private const int SplitMaxParts = 16; // Maximum number of panel parts proposed automatically; positive integer.
    private const int SplitSeamRefinementSteps = 16; // Candidate positions evaluated per seam on each optimization pass; positive integer.
    private const int SplitSeamRefinementPasses = 3; // Coordinate-descent passes over all proposed seams; positive integer.
    private const int SplitDragRefreshMilliseconds = 90; // Minimum interval in milliseconds between expensive Boolean preview rebuilds while dragging.
    private const bool AllowSplitDragObjectSnaps = true; // true uses Rhino object snaps and their cursor cues while pivoting cuts; false uses free cursor points.
    private const int SplitPreviewAlpha = 200; // Alpha channel from 0 (invisible) to 255 (opaque) for destination-colored preview strokes.
    private const double SplitGhostTransparency = 0.4; // Panel-preview transparency from 0 (opaque) to 1 (invisible).
    private static readonly Color SplitSeamColor = Color.Orange; // RGB preview color for movable source-shade seam lines.
    private const string SplitPartObjectName = "ShadeSplitPart"; // Name of laid-out, clipped panel curves.
    private const string SplitSeamObjectName = "ShadeSplitCut"; // Name of Reference-layer seam lines on the source shade.
    private const string SplitOverlapLayerName = "PLOT"; // Existing or new Rhino layer for part-to-part overlap marks and digits.
    private static readonly Color DefaultSplitPlotColor = new Layer().Color; // Rhino's default new-layer RGB color when PLOT does not yet exist.
    private const string SplitOverlapLineObjectName = "ShadeOverlapLine"; // Name of each part's line marking the adjacent part's overlapping edge.
    private const string SplitOverlapLabelObjectName = "ShadeOverlapLabel"; // Name of each digit identifying the adjacent split part.
    private const double SplitOverlapLabelHeight = 0.4; // Common seam digit height in model units; fits within the 0.5-unit overlap.
    private const double SplitOverlapLabelMargin = 0.04; // Minimum model-unit clearance from cut and plot edges around a seam digit.
    private const double SplitLabelGlyphScale = 1.0; // Positive glyph-outline scale; 1 preserves the label's fixed text height.
    private const double SplitLabelGlyphSpacing = 0.0; // Additional model-unit glyph spacing; zero preserves native kerning.
    private const double SplitCoincidentBoundaryToleranceMultiplier = 2.0; // Model-tolerance multiplier for omitting boundary detail exactly coincident with the cut outline.

    private const string OptionsSectionName = "vShade";
    private const string OffsetKey = "offset";
    private const string ChamferKey = "chamfer";
    private const string LegacyLengthKey = "length";
    private const string ReinforcementKey = "reinforcement";
    private const string ScallopKey = "scallop";
    private const string ScallopSizeKey = "scallopSize";
    private const string ScallopPercentKey = "scallopPercent";
    private const string TuneScallopSizeKey = "tuneScallopSize";
    private const string TuneScallopPercentKey = "tuneScallopPercent";
    private const string CutOffsetKey = "cutOffset";
    private const string CutLayerKey = "cutLayer";
    private const string ConnectKey = "connect";
    private const string JoinKey = "join";
    private const string LayerKey = "layer";
    private const string FinishedLayerKey = "finishedLayer";
    private const string CreateReinfKey = "createReinf";
    private const string LabelKey = "label";
    private const string SplitWidthKey = "splitWidth";

    private static double _offset = DefaultOffset;
    private static double _chamfer = DefaultChamfer;
    private static double _reinforcement = DefaultReinforcement;
    private static bool _scallop = DefaultScallop;
    private static double _scallopSize = DefaultScallopSize;
    private static bool _scallopPercent = DefaultScallopPercent;
    private static double _cutOffset = DefaultCutOffset;
    private static string _cutLayer = DefaultCutLayer;
    private static bool _createReinf = DefaultCreateReinf;
    private static bool _connect = DefaultConnect;
    private static bool _join = DefaultJoin;
    private static string _layer = DefaultLayer;
    private static double _splitWidth = DefaultSplitWidth;
    private static ScallopSize _tuneScallopSize = new(DefaultScallopSize, DefaultScallopPercent); // Per-command click-to-apply scallop bulge; zero selects a line.

    private sealed record CurvePick(
      Guid ObjectId,
      Curve Curve,
      Point3d ClickPoint,
      Point3d Endpoint,
      Vector3d AwayTangent,
      bool AtStart);

    private readonly record struct ShadeGeometry(
      Point3d Corner,
      Point3d OffsetEnd,
      Vector3d InteriorDirection,
      Line? OffsetLine,
      Line? PerpendicularLine,
      Point3d FirstCapEnd,
      Point3d SecondCapEnd);

    private readonly record struct ShadeOptionState(
      OptionDouble Offset,
      OptionDouble Chamfer,
      OptionDouble Reinforcement,
      OptionToggle CreateReinf,
      OptionToggle Scallop,
      int ScallopSizeOptionIndex,
      OptionToggle Connect,
      OptionToggle Join,
      int LayerOptionIndex,
      int LabelOptionIndex,
      OptionDouble CutOffset,
      int CutLayerOptionIndex,
      int ScallopTuneOptionIndex,
      int OffsetTuneOptionIndex,
      int SplitOptionIndex);

    private sealed record ShadePlacement(
      int Sequence,
      ShadeGeometry Geometry,
      int OutputLayerIndex,
      Guid FirstSourceId,
      Guid SecondSourceId,
      bool Connect,
      double Reinforcement,
      string LabelText,
      string NextLabel);

    private sealed record ShadeHistoryState(
      List<ShadePlacement> Placements,
      Dictionary<(int From, int To), ScallopSize> ScallopSizes,
      string NextLabel);

    private sealed record ShadeHistoryRequest(bool Redo);

    private sealed record ShadeConnection(Line Line, int FromSequence, int ToSequence);

    private sealed record ShadeConnectionGeometry(ShadeConnection Connection, Curve Curve);

    private readonly record struct ScallopSize(double Value, bool Percent);

    private sealed class ShadeSnapshot
    {
      public ShadeSnapshot() { }

      public List<PlacementSnapshot> Placements { get; set; } = [];
      public List<ScallopSnapshot> Scallops { get; set; } = [];
      public bool Scallop { get; set; }
      public double ScallopSize { get; set; }
      public bool ScallopPercent { get; set; }
      public bool Join { get; set; }
      public double CutOffset { get; set; }
      public string CutLayer { get; set; } = DefaultCutLayer;
      public string LayerOption { get; set; } = string.Empty;
      public bool CreateReinf { get; set; } = DefaultCreateReinf;
    }

    private sealed class PlacementSnapshot
    {
      public PlacementSnapshot() { }

      public int Sequence { get; set; }
      public double[] Corner { get; set; } = [];
      public double[] OffsetEnd { get; set; } = [];
      public double[] InteriorDirection { get; set; } = [];
      public double[] FirstCapEnd { get; set; } = [];
      public double[] SecondCapEnd { get; set; } = [];
      public bool HasOffsetLine { get; set; }
      public bool HasPerpendicularLine { get; set; }
      public int OutputLayerIndex { get; set; }
      public Guid FirstSourceId { get; set; }
      public Guid SecondSourceId { get; set; }
      public bool Connect { get; set; }
      public double Reinforcement { get; set; }
      public string LabelText { get; set; } = string.Empty;
      public string NextLabel { get; set; } = string.Empty;
    }

    private sealed class ScallopSnapshot
    {
      public ScallopSnapshot() { }

      public int FromSequence { get; set; }
      public int ToSequence { get; set; }
      public double Value { get; set; }
      public bool Percent { get; set; }
    }

    private sealed class ShadeLayerMonitor : IDisposable
    {
      private readonly RhinoDoc _doc;
      private readonly DuplicateOutputLayerSession _layerSession;
      private readonly Action<int> _applyLayer;

      public ShadeLayerMonitor(
        RhinoDoc doc,
        DuplicateOutputLayerSession layerSession,
        Action<int> applyLayer)
      {
        _doc = doc;
        _layerSession = layerSession;
        _applyLayer = applyLayer;
        RhinoDoc.LayerTableEvent += OnLayerTableEvent;
      }

      private void OnLayerTableEvent(object? sender, LayerTableEventArgs e)
      {
        if (e.Document.RuntimeSerialNumber != _doc.RuntimeSerialNumber ||
            e.EventType != LayerTableEventType.Current ||
            _layerSession.OptionLayerName != DuplicateCommandSupport.CurrentLayerOption)
          return;

        _layerSession.ObserveCurrentLayer(_doc);
        _applyLayer(_layerSession.CreateAttributes(_doc).LayerIndex);
      }

      public void Dispose() => RhinoDoc.LayerTableEvent -= OnLayerTableEvent;
    }

    private sealed record ShadeConnectionCandidate(
      Line Line,
      int FromSequence,
      int ToSequence,
      int FromEnd,
      int ToEnd,
      bool SharedSource);

    public override string EnglishName => "vShade";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      LoadOptions();
      var sessionId = Guid.NewGuid();
      var placements = new List<ShadePlacement>();
      var outputIds = new List<Guid>();
      var scallopSizes = new Dictionary<(int From, int To), ScallopSize>();
      var labelValue = doc.Strings.GetValue(OptionsSectionName, LabelKey) ?? DefaultLabel;
      var selectedShadeIds = doc.Objects.GetSelectedObjects(false, false)
        .Select(obj => obj.Attributes.GetUserString(MetadataPrefix + "session"))
        .Where(value => Guid.TryParse(value, out _))
        .Distinct()
        .ToArray();
      if (selectedShadeIds.Length > 1)
      {
        RhinoApp.WriteLine("vShade: preselect objects from only one shade to continue it.");
        return Result.Failure;
      }
      if (selectedShadeIds.Length == 1)
      {
        sessionId = Guid.Parse(selectedShadeIds[0]!);
        if (!TryRestoreShade(
              doc, sessionId, placements, scallopSizes, outputIds, out var restoredLayerOption))
        {
          RhinoApp.WriteLine("vShade: the selected shade has no valid resumable state.");
          return Result.Failure;
        }
        var last = placements[^1];
        _offset = last.Geometry.OffsetLine?.Length ?? 0.0;
        _chamfer = last.Geometry.PerpendicularLine?.Length ?? 0.0;
        _reinforcement = last.Reinforcement;
        _connect = last.Connect;
        _layer = string.IsNullOrEmpty(restoredLayerOption)
          ? doc.Layers[last.OutputLayerIndex]?.FullPath ?? DefaultLayer
          : DuplicateCommandSupport.NormalizeLayerOption(restoredLayerOption);
        labelValue = last.NextLabel;
        RhinoApp.WriteLine($"vShade: continuing shade with {placements.Count} corners.");
      }
      using var undoRecords = new ShadeUndoRecords(doc);
      if (!undoRecords.BeginShade())
      {
        RhinoApp.WriteLine("vShade: cannot start separate undo records while another undo record is active.");
        return Result.Failure;
      }
      var layerSession = new DuplicateOutputLayerSession(doc, _layer, EnglishName);
      var historyPosition = placements.Count;
      var chainCount = 0;
      var joinedCount = 0;
      var history = new List<ShadeHistoryState>();
      for (var count = 0; count <= placements.Count; count++)
        history.Add(new ShadeHistoryState(
          placements.Take(count).ToList(),
          new Dictionary<(int From, int To), ScallopSize>(scallopSizes),
          count == 0 && placements.Count > 0
            ? placements[0].LabelText
            : count > 0 ? placements[count - 1].NextLabel : labelValue));
      var historyIndex = history.Count - 1;

      using var highlighter = new PreviewDisplay.ObjectHighlighter(doc);
      using var shortcuts = new LocalUndoRedoShortcutSession(
        EnglishName,
        redo => new ShadeHistoryRequest(redo));

      bool ApplyHistory(bool redo)
      {
        var nextIndex = historyIndex + (redo ? 1 : -1);
        if (nextIndex < 0 || nextIndex >= history.Count)
        {
          RhinoApp.WriteLine(redo ? "vShade: nothing to redo." : "vShade: nothing to undo.");
          return false;
        }

        var state = history[nextIndex];
        if (!RebuildOutputs(
              doc,
              sessionId,
              state.Placements,
              state.ScallopSizes,
              outputIds,
              out chainCount,
              out joinedCount))
          return false;

        placements.Clear();
        placements.AddRange(state.Placements);
        scallopSizes.Clear();
        foreach (var item in state.ScallopSizes)
          scallopSizes.Add(item.Key, item.Value);
        historyPosition = placements.Count;
        historyIndex = nextIndex;
        labelValue = state.NextLabel;
        doc.Strings.SetString(OptionsSectionName, LabelKey, labelValue);
        Log.Write("vShade", $"{(redo ? "redo" : "undo")} placements={historyPosition}");
        return true;
      }

      void RecordHistory()
      {
        if (historyIndex + 1 < history.Count)
          history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
        history.Add(new ShadeHistoryState(
          placements.ToList(),
          new Dictionary<(int From, int To), ScallopSize>(scallopSizes),
          labelValue));
        historyIndex = history.Count - 1;
      }

      bool RefreshBoundaryOutput()
      {
        return historyPosition == 0 ||
          RebuildOutputs(
            doc,
            sessionId,
            placements.Take(historyPosition).ToList(),
            scallopSizes,
            outputIds,
            out chainCount,
            out joinedCount);
      }

      bool RefreshReinforcementOutput() => RefreshReinforcementParts(
        doc,sessionId,placements.Take(historyPosition).ToList(),scallopSizes,outputIds);

      void ApplyOutputLayer(int layerIndex)
      {
        if (placements.Count == 0 || layerIndex < 0 || layerIndex >= doc.Layers.Count)
          return;

        var previousPlacements = placements.ToArray();
        var changedAttributes = new List<(Guid Id, ObjectAttributes Attributes)>();
        try
        {
          foreach (var id in outputIds)
          {
            var obj = doc.Objects.FindId(id);
            if (obj == null ||
                obj.Attributes.GetUserString(MetadataPrefix + "role") is "bisector" or "cut" or "reinforcement_part" ||
                obj.Attributes.LayerIndex == layerIndex)
              continue;

            var original = obj.Attributes.Duplicate();
            var updated = original.Duplicate();
            updated.LayerIndex = layerIndex;
            if (!doc.Objects.ModifyAttributes(id, updated, quiet: true))
              throw new InvalidOperationException($"could not move shade object {id} to the current layer");
            changedAttributes.Add((id, original));
          }

          for (var index = 0; index < placements.Count; index++)
            placements[index] = placements[index] with { OutputLayerIndex = layerIndex };
          if (historyPosition > 0)
            SaveShadeState(doc, sessionId, placements.Take(historyPosition).ToList(), scallopSizes);
          for (var index = 0; index < history.Count; index++)
            history[index] = history[index] with
            {
              Placements = history[index].Placements
                .Select(placement => placement with { OutputLayerIndex = layerIndex })
                .ToList()
            };
          doc.Views.Redraw();
          Log.Write("vShade", $"session={sessionId} output layer={doc.Layers[layerIndex].FullPath}");
        }
        catch (Exception ex)
        {
          for (var index = 0; index < previousPlacements.Length; index++)
            placements[index] = previousPlacements[index];
          foreach (var changed in changedAttributes)
            doc.Objects.ModifyAttributes(changed.Id, changed.Attributes, quiet: true);
          doc.Views.Redraw();
          Log.Write("vShade", $"output layer change failed: {ex}");
          RhinoApp.WriteLine($"vShade: could not change shade layer: {ex.Message}");
        }
      }

      void TuneScallops() => TuneScallopSizes(
        doc,
        placements.Take(historyPosition).ToList(),
        scallopSizes,
        RefreshBoundaryOutput,
        RecordHistory);

      void TuneOffsets() => TuneCornerOffsets(
        doc,
        placements,
        historyPosition,
        RefreshBoundaryOutput,
        RecordHistory);

      var splitCompleted = false;
      bool SplitShade()
      {
        splitCompleted = RunSplit(doc, mode, sessionId, outputIds, undoRecords.BeginSplit);
        return splitCompleted;
      }

      using var layerMonitor = new ShadeLayerMonitor(doc, layerSession, ApplyOutputLayer);

      while (true)
      {
        CurvePick? first = null;
        CurvePick? second = null;

        try
        {
          var firstPrompt = historyPosition == 0
            ? "Select first curve near corner end"
            : "Select first curve near next corner end; press Enter when done";
          var firstResult = PickCurve(
            doc,
            mode,
            layerSession,
            firstPrompt,
            Guid.Empty,
            allowFinish: historyPosition > 0,
            out first,
            out var firstHistory,
            RefreshBoundaryOutput,
            RefreshReinforcementOutput,
            TuneScallops,
            TuneOffsets,
            SplitShade,
            () => FindSplitSource(doc, outputIds) != null,
            ApplyOutputLayer,
            ref labelValue);

          if (splitCompleted)
            break;

          if (firstHistory.HasValue)
          {
            ApplyHistory(firstHistory.Value);
            continue;
          }

          if (firstResult == Result.Nothing || firstResult == Result.Cancel)
          {
            if (historyPosition == 0)
              return firstResult == Result.Nothing ? Result.Nothing : Result.Cancel;
            break;
          }

          if (firstResult != Result.Success || first == null)
            return firstResult;

          highlighter.SetObjects([first.ObjectId]);

          var secondResult = PickCurve(
            doc,
            mode,
            layerSession,
            "Select second curve near corner end",
            first.ObjectId,
            allowFinish: false,
            out second,
            out var secondHistory,
            RefreshBoundaryOutput,
            RefreshReinforcementOutput,
            TuneScallops,
            TuneOffsets,
            SplitShade,
            () => FindSplitSource(doc, outputIds) != null,
            ApplyOutputLayer,
            ref labelValue);

          if (splitCompleted)
            break;

          if (secondHistory.HasValue)
          {
            ApplyHistory(secondHistory.Value);
            continue;
          }

          if (secondResult == Result.Cancel)
          {
            if (historyPosition == 0)
              return Result.Cancel;
            break;
          }

          if (secondResult != Result.Success || second == null)
            continue;

          highlighter.SetObjects([first.ObjectId, second.ObjectId]);

          var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
          if (!TryBuildGeometry(doc, cplane, first, second, out var geometry, out var failure))
          {
            RhinoApp.WriteLine($"vShade: {failure}");
            continue;
          }

          if (geometry.OffsetLine == null && geometry.PerpendicularLine == null)
          {
            RhinoApp.WriteLine("vShade: Offset and Chamfer are both zero; no geometry was created.");
            continue;
          }

          var existingCornerIndex = FindExistingCornerIndex(
            doc, placements, historyPosition, geometry.Corner, first.ObjectId, second.ObjectId);
          var previousCorner = existingCornerIndex >= 0
            ? placements[existingCornerIndex]
            : null;
          var placement = new ShadePlacement(
            previousCorner?.Sequence ?? historyPosition + 1,
            geometry,
            layerSession.CreateAttributes(doc).LayerIndex,
            first.ObjectId,
            second.ObjectId,
            _connect,
            _reinforcement,
            previousCorner?.LabelText ?? labelValue,
            previousCorner?.NextLabel ??
              (string.IsNullOrWhiteSpace(labelValue)
                ? labelValue
                : IncrementLabelValue(labelValue)));
          var previousPlacements = placements.ToList();
          if (existingCornerIndex >= 0)
            placements[existingCornerIndex] = placement;
          else
            placements.Add(placement);
          if (!RebuildOutputs(
                doc,
                sessionId,
                placements,
                scallopSizes,
                outputIds,
                out chainCount,
                out joinedCount))
          {
            placements.Clear();
            placements.AddRange(previousPlacements);
            continue;
          }
          historyPosition = placements.Count;
          if (existingCornerIndex < 0)
          {
            labelValue = placement.NextLabel;
            doc.Strings.SetString(OptionsSectionName, LabelKey, labelValue);
          }
          RecordHistory();

          Log.Write(
            "vShade",
            $"session={sessionId} action={(existingCornerIndex >= 0 ? "replaced" : "added")} " +
            $"sequence={placement.Sequence} first={first.ObjectId} " +
            $"second={second.ObjectId} corner={FormatPoint(geometry.Corner)} " +
            $"offsetEnd={FormatPoint(geometry.OffsetEnd)} offset={_offset:G17} " +
            $"chamfer={_chamfer:G17} reinforcement={_reinforcement:G17} " +
            $"scallop={_scallop} scallopSize={FormatScallopSize()} " +
            $"connect={_connect} join={_join} " +
            $"layer={layerSession.ResolvedLayerName(doc)} chains={chainCount}");

          highlighter.SetObjects([]);
        }
        finally
        {
          first?.Curve.Dispose();
          second?.Curve.Dispose();
          highlighter.SetObjects([]);
        }
      }

      SaveOptions();
      Log.Write(
        "vShade",
        $"session={sessionId} placements={historyPosition} chains={chainCount} " +
        $"joined={joinedCount} connect={_connect} join={_join} " +
        $"scallop={_scallop} scallopSize={FormatScallopSize()} " +
        $"cutOffset={_cutOffset:G17} cutLayer={_cutLayer} " +
        $"layer={layerSession.ResolvedLayerName(doc)}");
      var hasFitSize = TryGetOutsideFitSize(doc, outputIds, out var fitSource, out var fitSize);
      RhinoApp.WriteLine(
        $"vShade: created one shade with {historyPosition} corner{(historyPosition == 1 ? string.Empty : "s")}" +
        $"; connected boundaries={chainCount}; joined={joinedCount}; " +
        (hasFitSize
          ? $"outside fit box ({fitSource})={fitSize}."
          : "outside fit box unavailable (no closed boundary)."));
      doc.Views.Redraw();
      return Result.Success;
    }

    private static Result PickCurve(
      RhinoDoc doc,
      RunMode mode,
      DuplicateOutputLayerSession layerSession,
      string prompt,
      Guid excludedObjectId,
      bool allowFinish,
      out CurvePick? pick,
      out bool? historyRequest,
      Func<bool> refreshBoundaryOutput,
      Func<bool> refreshReinforcementOutput,
      Action tuneScallops,
      Action tuneOffsets,
      Func<bool> splitShade,
      Func<bool> splitAvailable,
      Action<int> applyOutputLayer,
      ref string labelValue)
    {
      pick = null;
      historyRequest = null;

      while (true)
      {
        var selectedBefore = CollectSelectedObjectIds(doc);
        using var getter = new GetObject();
        getter.EnableTransparentCommands(true);
        getter.SetCommandPrompt(prompt);
        getter.GeometryFilter = ObjectType.Curve;
        getter.SubObjectSelect = false;
        getter.GroupSelect = false;
        getter.EnablePreSelect(false, true);
        getter.DeselectAllBeforePostSelect = false;
        getter.EnableUnselectObjectsOnExit(false);
        getter.AcceptNothing(allowFinish);
        getter.AcceptCustomMessage(true);
        getter.AcceptNumber(true, false);
        var options = AddOptions(getter, layerSession, labelValue, splitAvailable());

        var getResult = getter.Get();
        layerSession.ObserveCurrentLayer(doc);

        if (getResult == GetResult.Option)
        {
          if (getter.Option()?.Index == options.ScallopTuneOptionIndex)
          {
            tuneScallops();
            continue;
          }
          if (getter.Option()?.Index == options.OffsetTuneOptionIndex)
          {
            tuneOffsets();
            continue;
          }
          if (options.SplitOptionIndex >= 0 &&
              getter.Option()?.Index == options.SplitOptionIndex)
          {
            if (splitShade())
              return Result.Nothing;
            continue;
          }
          var previousJoin = _join;
          var previousScallop = _scallop;
          var previousScallopSize = _scallopSize;
          var previousScallopPercent = _scallopPercent;
          var previousCutOffset = _cutOffset;
          var previousCutLayer = _cutLayer;
          var previousCreateReinf = _createReinf;
          ApplyOptions(doc, mode, layerSession, getter, options, ref labelValue);
          if (getter.Option()?.Index == options.LayerOptionIndex)
            applyOutputLayer(layerSession.CreateAttributes(doc).LayerIndex);
          if ((previousJoin != _join ||
               previousScallop != _scallop ||
               previousScallopSize != _scallopSize ||
               previousScallopPercent != _scallopPercent ||
               previousCutOffset != _cutOffset ||
               previousCutLayer != _cutLayer) &&
              !refreshBoundaryOutput())
          {
            _join = previousJoin;
            _scallop = previousScallop;
            _scallopSize = previousScallopSize;
            _scallopPercent = previousScallopPercent;
            _cutOffset = previousCutOffset;
            _cutLayer = previousCutLayer;
            _createReinf = previousCreateReinf;
            SaveOptions();
          }
          else if (previousCreateReinf != _createReinf && !refreshReinforcementOutput())
          {
            _createReinf = previousCreateReinf;
            SaveOptions();
          }
          continue;
        }

        if (getResult == GetResult.CustomMessage &&
            getter.CustomMessage() is ShadeHistoryRequest request)
        {
          historyRequest = request.Redo;
          return Result.Nothing;
        }

        if (getResult == GetResult.Nothing)
          return Result.Nothing;

        if (getResult == GetResult.Number)
        {
          var offset = getter.Number();
          if (!double.IsFinite(offset) || offset < 0.0)
            RhinoApp.WriteLine("vShade: Offset must be a non-negative number.");
          else
          {
            _offset = offset;
            SaveOptions();
          }
          continue;
        }

        if (getResult != GetResult.Object || getter.ObjectCount == 0)
          return getter.CommandResult() == Result.Success
            ? Result.Cancel
            : getter.CommandResult();

        var objRef = getter.Object(0);
        var objectId = objRef.ObjectId;
        var rhinoObject = objRef.Object();
        var sourceCurve = objRef.Curve() ?? objRef.Geometry() as Curve;
        var duplicate = sourceCurve?.DuplicateCurve();

        if (!selectedBefore.Contains(objectId))
          rhinoObject?.Select(false);
        doc.Views.Redraw();

        if (duplicate == null)
        {
          RhinoApp.WriteLine("vShade: the selected object does not provide a usable curve.");
          continue;
        }

        if (objectId == excludedObjectId)
        {
          duplicate.Dispose();
          RhinoApp.WriteLine("vShade: select a different second curve.");
          continue;
        }

        if (duplicate.IsClosed)
        {
          duplicate.Dispose();
          RhinoApp.WriteLine("vShade: select an open curve so its clicked end identifies the corner.");
          continue;
        }

        var clickPoint = objRef.SelectionPoint();
        if (!clickPoint.IsValid)
        {
          duplicate.Dispose();
          RhinoApp.WriteLine("vShade: could not determine the clicked point on the curve.");
          continue;
        }

        var atStart = clickPoint.DistanceToSquared(duplicate.PointAtStart) <=
                      clickPoint.DistanceToSquared(duplicate.PointAtEnd);
        var endpoint = atStart ? duplicate.PointAtStart : duplicate.PointAtEnd;
        var awayTangent = atStart ? duplicate.TangentAtStart : -duplicate.TangentAtEnd;
        if (!awayTangent.Unitize())
        {
          duplicate.Dispose();
          RhinoApp.WriteLine("vShade: the clicked curve end has no usable tangent.");
          continue;
        }

        pick = new CurvePick(objectId, duplicate, clickPoint, endpoint, awayTangent, atStart);
        Log.Write(
          "vShade",
          $"pick object={objectId} click={FormatPoint(clickPoint)} endpoint={FormatPoint(endpoint)} atStart={atStart}");
        return Result.Success;
      }
    }

    private static bool TryBuildGeometry(
      RhinoDoc doc,
      Plane cplane,
      CurvePick first,
      CurvePick second,
      out ShadeGeometry geometry,
      out string failure)
    {
      geometry = default;
      failure = string.Empty;

      var p1 = ProjectPoint(cplane, first.Endpoint, out var height1);
      var p2 = ProjectPoint(cplane, second.Endpoint, out var height2);
      var d1 = ProjectVector(cplane, first.AwayTangent);
      var d2 = ProjectVector(cplane, second.AwayTangent);
      var directionTolerance = RhinoMath.ZeroTolerance * DirectionToleranceFactor;

      if (!Unitize(ref d1, directionTolerance) || !Unitize(ref d2, directionTolerance))
      {
        failure = "a clicked curve tangent is perpendicular to the active CPlane.";
        return false;
      }

      var endpointTolerance = Math.Max(
        doc.ModelAbsoluteTolerance * EndpointCoincidenceToleranceFactor,
        directionTolerance);
      Point2d corner2d;

      if (Distance(p1, p2) <= endpointTolerance)
      {
        corner2d = new Point2d((p1.X + p2.X) * 0.5, (p1.Y + p2.Y) * 0.5);
      }
      else
      {
        var denominator = Cross(d1, d2);
        if (Math.Abs(denominator) <= directionTolerance)
        {
          failure = "the selected curve-end tangents are parallel and do not define a corner.";
          return false;
        }

        var between = new Vector2d(p2.X - p1.X, p2.Y - p1.Y);
        var alongFirst = Cross(between, d2) / denominator;
        corner2d = new Point2d(
          p1.X + d1.X * alongFirst,
          p1.Y + d1.Y * alongFirst);
      }

      OrientAwayFromCorner(ref d1, p1, corner2d, directionTolerance);
      OrientAwayFromCorner(ref d2, p2, corner2d, directionTolerance);

      var bisector2d = new Vector2d(d1.X + d2.X, d1.Y + d2.Y);
      if (!Unitize(ref bisector2d, directionTolerance))
      {
        failure = "the selected curve ends form a straight line with no unique inward bisector.";
        return false;
      }

      var cornerHeight = (height1 + height2) * 0.5;
      var corner = PointFromPlane(cplane, corner2d, cornerHeight);
      var bisector = cplane.XAxis * bisector2d.X + cplane.YAxis * bisector2d.Y;
      if (!bisector.Unitize())
      {
        failure = "the angle bisector could not be calculated.";
        return false;
      }

      var offsetEnd = corner + bisector * _offset;
      var perpendicular = cplane.XAxis * -bisector2d.Y + cplane.YAxis * bisector2d.X;
      if (!perpendicular.Unitize())
      {
        failure = "the perpendicular shade direction could not be calculated.";
        return false;
      }

      var offsetLine = _offset > 0.0
        ? new Line(corner, offsetEnd)
        : (Line?)null;
      var halfChamfer = _chamfer * 0.5;
      var perpendicularLine = _chamfer > 0.0
        ? new Line(
          offsetEnd - perpendicular * halfChamfer,
          offsetEnd + perpendicular * halfChamfer)
        : (Line?)null;

      var firstCapEnd = Point3d.Unset;
      var secondCapEnd = Point3d.Unset;
      if (perpendicularLine is { } cap)
      {
        var from2d = ProjectPoint(cplane, cap.From, out _);
        var to2d = ProjectPoint(cplane, cap.To, out _);
        var fromOffset = new Vector2d(from2d.X - corner2d.X, from2d.Y - corner2d.Y);
        var toOffset = new Vector2d(to2d.X - corner2d.X, to2d.Y - corner2d.Y);
        var fromDistance = Math.Abs(Cross(d1, fromOffset));
        var toDistance = Math.Abs(Cross(d1, toOffset));
        firstCapEnd = fromDistance <= toDistance ? cap.From : cap.To;
        secondCapEnd = fromDistance <= toDistance ? cap.To : cap.From;
      }

      geometry = new ShadeGeometry(
        corner,
        offsetEnd,
        bisector,
        offsetLine,
        perpendicularLine,
        firstCapEnd,
        secondCapEnd);
      return true;
    }

    private static int FindExistingCornerIndex(
      RhinoDoc doc,
      IReadOnlyList<ShadePlacement> placements,
      int activeCount,
      Point3d corner,
      Guid firstSourceId,
      Guid secondSourceId)
    {
      var tolerance = Math.Max(
        doc.ModelAbsoluteTolerance * EndpointCoincidenceToleranceFactor,
        RhinoMath.ZeroTolerance * DirectionToleranceFactor);
      var bestIndex = -1;
      var bestSourceMatch = -1;
      var bestDistance = double.PositiveInfinity;
      for (var index = 0; index < activeCount; index++)
      {
        var placement = placements[index];
        var distance = placement.Geometry.Corner.DistanceTo(corner);
        if (distance > tolerance)
          continue;
        var samePair =
          placement.FirstSourceId == firstSourceId && placement.SecondSourceId == secondSourceId ||
          placement.FirstSourceId == secondSourceId && placement.SecondSourceId == firstSourceId;
        var sharedSource = placement.FirstSourceId == firstSourceId ||
                           placement.FirstSourceId == secondSourceId ||
                           placement.SecondSourceId == firstSourceId ||
                           placement.SecondSourceId == secondSourceId;
        var sourceMatch = samePair ? 2 : sharedSource ? 1 : 0;
        if (sourceMatch < bestSourceMatch ||
            sourceMatch == bestSourceMatch && distance >= bestDistance)
          continue;
        bestIndex = index;
        bestSourceMatch = sourceMatch;
        bestDistance = distance;
      }
      return bestIndex;
    }

    private static bool RebuildOutputs(
      RhinoDoc doc,
      Guid sessionId,
      IReadOnlyList<ShadePlacement> placements,
      IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes,
      List<Guid> existingOutputIds,
      out int chainCount,
      out int joinedCount)
    {
      chainCount = 0;
      joinedCount = 0;
      var newOutputIds = new List<Guid>();
      var connectionGeometry = new List<ShadeConnectionGeometry>();
      var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);

      try
      {
        var connections = BuildConnections(cplane, placements, tolerance);
        chainCount = CountConnectedChains(connections);
        foreach (var connection in connections)
          connectionGeometry.Add(new ShadeConnectionGeometry(
            connection,
            CreateConnectionCurve(cplane, connection, placements, scallopSizes, tolerance)));
        if (connectionGeometry.Any(item => item.Curve is not LineCurve))
          ValidateScallopPerimeter(placements, connectionGeometry, tolerance);

        foreach (var placement in placements)
        {
          if (placement.Geometry.OffsetLine is not { } offsetLine)
            continue;

          var referenceLayerIndex = UzipCommon.EnsureLayer(
            doc,
            ReferenceLayerName,
            ReferenceLayerColor);
          var attributes = CreateIdentifiedAttributes(
            referenceLayerIndex,
            BisectorObjectName,
            "bisector",
            sessionId,
            placement.Sequence,
            placement.FirstSourceId,
            placement.SecondSourceId);
          var id = doc.Objects.AddLine(offsetLine, attributes);
          if (id == Guid.Empty)
            throw new InvalidOperationException("could not add the bisector line");
          newOutputIds.Add(id);
        }

        foreach (var placement in placements)
        {
          if (placement.Reinforcement <= 0.0 ||
              placement.Geometry.PerpendicularLine is not { } cap)
            continue;

          var incident = connectionGeometry
            .Where(item =>
              item.Connection.FromSequence == placement.Sequence ||
              item.Connection.ToSequence == placement.Sequence)
            .ToArray();
          if (incident.Length != 2)
            continue;

          using var arc = CreateReinforcementArc(
            placement,
            cap,
            incident[0],
            incident[1],
            cplane,
            tolerance);
          if (arc == null)
            continue;

          var attributes = CreateIdentifiedAttributes(
            placement.OutputLayerIndex,
            ReinforcementObjectName,
            "reinforcement",
            sessionId,
            placement.Sequence,
            placement.FirstSourceId,
            placement.SecondSourceId);
          attributes.SetUserString(
            MetadataPrefix + "radius",
            placement.Reinforcement.ToString("G17", CultureInfo.InvariantCulture));
          var id = doc.Objects.AddCurve(arc, attributes);
          if (id == Guid.Empty)
            throw new InvalidOperationException("could not add the reinforcement arc");
          newOutputIds.Add(id);
          Log.Write(
            "vShade",
            $"reinforcement sequence={placement.Sequence} center={FormatPoint(placement.Geometry.OffsetEnd)} " +
            $"radius={placement.Reinforcement:G17} from={FormatPoint(arc.PointAtStart)} " +
            $"to={FormatPoint(arc.PointAtEnd)}");

          if (!string.IsNullOrWhiteSpace(placement.LabelText))
          {
            var labelId = AddReinforcementLabel(
              doc,
              cplane,
              placement,
              cap,
              arc,
              sessionId);
            if (labelId == Guid.Empty)
              throw new InvalidOperationException("could not add the reinforcement label");
            newOutputIds.Add(labelId);
          }
        }

        if (_join && connections.Count > 0)
        {
          var pieces = new List<Curve>();
          foreach (var placement in placements)
          {
            if (placement.Geometry.PerpendicularLine is { } cap)
              pieces.Add(new LineCurve(cap));
          }
          pieces.AddRange(connectionGeometry.Select(item => item.Curve.DuplicateCurve()));

          Curve[] joined;
          try
          {
            joined = Curve.JoinCurves(pieces, tolerance, preserveDirection: false);
          }
          finally
          {
            foreach (var piece in pieces)
              piece.Dispose();
          }

          if (joined.Length == 0)
            throw new InvalidOperationException("Rhino did not return a joined perimeter");
          Log.Write(
            "vShade",
            $"perimeter components={joined.Length} closed={string.Join(",", joined.Select(curve => curve.IsClosed))} " +
            $"caps={placements.Count(placement => placement.Geometry.PerpendicularLine.HasValue)} " +
            $"connections={connections.Count}");

          try
          {
            foreach (var curve in joined)
            {
              var last = placements[^1];
              var attributes = CreateIdentifiedAttributes(
                last.OutputLayerIndex,
                BoundaryObjectName,
                "boundary",
                sessionId,
                last.Sequence,
                last.FirstSourceId,
                last.SecondSourceId);
              attributes.SetUserString(
                MetadataPrefix + "start_sequence",
                placements[0].Sequence.ToString(CultureInfo.InvariantCulture));
              attributes.SetUserString(
                MetadataPrefix + "end_sequence",
                last.Sequence.ToString(CultureInfo.InvariantCulture));
              var id = doc.Objects.AddCurve(curve, attributes);
              if (id == Guid.Empty)
                throw new InvalidOperationException("could not add the joined perimeter");
              newOutputIds.Add(id);
            }
            joinedCount = Math.Min(chainCount, joined.Length);
          }
          finally
          {
            foreach (var curve in joined)
              curve.Dispose();
          }
        }
        else
        {
          foreach (var placement in placements)
          {
            if (placement.Geometry.PerpendicularLine is not { } cap)
              continue;
            var attributes = CreateIdentifiedAttributes(
              placement.OutputLayerIndex,
              PerpendicularObjectName,
              "perpendicular",
              sessionId,
              placement.Sequence,
              placement.FirstSourceId,
              placement.SecondSourceId);
            var id = doc.Objects.AddLine(cap, attributes);
            if (id == Guid.Empty)
              throw new InvalidOperationException("could not add a perpendicular line");
            newOutputIds.Add(id);
          }

          foreach (var item in connectionGeometry)
          {
            var connection = item.Connection;
            var placement = placements[connection.ToSequence - 1];
            var attributes = CreateIdentifiedAttributes(
              placement.OutputLayerIndex,
              item.Curve is LineCurve ? ConnectionObjectName : ScallopObjectName,
              item.Curve is LineCurve ? "connection" : "scallop",
              sessionId,
              placement.Sequence,
              placement.FirstSourceId,
              placement.SecondSourceId);
            attributes.SetUserString(
              MetadataPrefix + "from_sequence",
              connection.FromSequence.ToString(CultureInfo.InvariantCulture));
            var id = doc.Objects.AddCurve(item.Curve, attributes);
            if (id == Guid.Empty)
              throw new InvalidOperationException("could not add a perimeter connector");
            newOutputIds.Add(id);
          }
        }

        if (_cutOffset > tolerance)
          AddCutOutlines(doc, cplane, placements, connectionGeometry, sessionId, tolerance, newOutputIds);
        if (_createReinf)
          AddReinforcementParts(doc,cplane,placements,connectionGeometry,sessionId,tolerance,newOutputIds);

        var groupName = ShadeGroupPrefix + sessionId.ToString("N");
        var groupedIds = newOutputIds.Where(id =>
          doc.Objects.FindId(id)?.Attributes.GetUserString(MetadataPrefix + "role") is not ("bisector" or "reinforcement_part" or "reinforcement_part_label")).ToArray();
        if (groupedIds.Length > 0)
        {
          var groupIndex = doc.Groups.FindName(groupName)?.Index ?? doc.Groups.Add(groupName);
          if (groupIndex < 0 || !doc.Groups.AddToGroup(groupIndex, groupedIds))
            throw new InvalidOperationException("could not group shade output");
        }

        SaveShadeState(doc, sessionId, placements, scallopSizes);
        foreach (var id in existingOutputIds)
          doc.Objects.Delete(id, quiet: true);
        existingOutputIds.Clear();
        existingOutputIds.AddRange(newOutputIds);
        doc.Views.Redraw();
        return true;
      }
      catch (Exception ex)
      {
        foreach (var id in newOutputIds)
          doc.Objects.Delete(id, quiet: true);
        Log.Write("vShade", $"rebuild failed: {ex}");
        RhinoApp.WriteLine($"vShade: could not update the perimeter: {ex.Message}");
        return false;
      }
      finally
      {
        foreach (var item in connectionGeometry)
          item.Curve.Dispose();
      }
    }

    private static void AddCutOutlines(
      RhinoDoc doc,
      Plane cplane,
      IReadOnlyList<ShadePlacement> placements,
      IReadOnlyList<ShadeConnectionGeometry> connections,
      Guid sessionId,
      double tolerance,
      List<Guid> outputIds)
    {
      var pieces = placements
        .Where(placement => placement.Geometry.PerpendicularLine.HasValue)
        .Select(placement => (Curve)new LineCurve(placement.Geometry.PerpendicularLine!.Value))
        .Concat(connections.Select(item => item.Curve.DuplicateCurve()))
        .ToArray();
      try
      {
        var boundaries = Curve.JoinCurves(pieces, tolerance, preserveDirection: false);
        try
        {
          foreach (var boundary in boundaries.Where(curve => curve.IsClosed))
          {
            using var originalArea = AreaMassProperties.Compute(boundary);
            if (originalArea == null)
              continue;

            var candidates = new List<(Curve Curve, double Area)>();
            foreach (var signedOffset in new[] { _cutOffset, -_cutOffset })
            {
              var offsets = boundary.Offset(cplane, signedOffset, tolerance, CurveOffsetCornerStyle.Sharp);
              if (offsets == null)
                continue;
              foreach (var curve in offsets)
              {
                using var area = curve.IsClosed ? AreaMassProperties.Compute(curve) : null;
                if (area != null && area.Area > originalArea.Area + tolerance * tolerance)
                  candidates.Add((curve, area.Area));
                else
                  curve.Dispose();
              }
            }

            try
            {
              var best = candidates.OrderBy(item => item.Area).FirstOrDefault().Curve;
              if (best == null)
              {
                Log.Write("vShade", "cut outline skipped: no valid outward closed offset");
                continue;
              }

              var last = placements[^1];
              var layerIndex = UzipCommon.EnsureLayer(doc, _cutLayer);
              var attributes = CreateIdentifiedAttributes(
                layerIndex,
                CutObjectName,
                "cut",
                sessionId,
                last.Sequence,
                last.FirstSourceId,
                last.SecondSourceId);
              attributes.SetUserString(
                MetadataPrefix + "cut_offset",
                _cutOffset.ToString("G17", CultureInfo.InvariantCulture));
              var id = doc.Objects.AddCurve(best, attributes);
              if (id == Guid.Empty)
                throw new InvalidOperationException("could not add the cut outline");
              outputIds.Add(id);
            }
            finally
            {
              foreach (var candidate in candidates)
                candidate.Curve.Dispose();
            }
          }
        }
        finally
        {
          foreach (var boundary in boundaries)
            boundary.Dispose();
        }
      }
      finally
      {
        foreach (var piece in pieces)
          piece.Dispose();
      }
    }

    private static bool TryGetOutsideFitSize(
      RhinoDoc doc,
      IReadOnlyList<Guid> outputIds,
      out string source,
      out string size)
    {
      source = string.Empty;
      size = string.Empty;
      var objects = outputIds
        .Select(doc.Objects.FindId)
        .Where(obj => obj != null)
        .ToArray();
      try
      {
        foreach (var role in new[] { "cut", "boundary" })
        {
          var outer = LargestClosedCurve(objects
            .Where(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") == role)
            .Select(obj => obj!.Geometry)
            .OfType<Curve>());
          if (outer == null)
            continue;
          source = role == "cut" ? "cut" : "shade";
          return vFitBox.TryFormatGeometryFitSize(doc, outer, out size);
        }

        var pieces = objects
          .Where(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") is
            "perpendicular" or "connection" or "scallop")
          .Select(obj => obj!.Geometry)
          .OfType<Curve>()
          .Select(curve => curve.DuplicateCurve())
          .ToArray();
        try
        {
          var joined = Curve.JoinCurves(pieces, doc.ModelAbsoluteTolerance);
          try
          {
            var outer = LargestClosedCurve(joined);
            if (outer == null)
              return false;
            source = "shade";
            return vFitBox.TryFormatGeometryFitSize(doc, outer, out size);
          }
          finally
          {
            foreach (var curve in joined)
              curve.Dispose();
          }
        }
        finally
        {
          foreach (var piece in pieces)
            piece.Dispose();
        }
      }
      catch (Exception ex)
      {
        Log.Write("vShade", $"outside fit box failed: {ex}");
        return false;
      }
    }

    private static Curve? LargestClosedCurve(IEnumerable<Curve> curves)
    {
      Curve? largest = null;
      var largestArea = 0.0;
      foreach (var curve in curves.Where(curve => curve.IsClosed))
      {
        using var properties = AreaMassProperties.Compute(curve);
        if (properties == null || properties.Area <= largestArea)
          continue;
        largest = curve;
        largestArea = properties.Area;
      }
      return largest;
    }

    private static (int From, int To) ConnectionKey(ShadeConnection connection) =>
      connection.FromSequence < connection.ToSequence
        ? (connection.FromSequence, connection.ToSequence)
        : (connection.ToSequence, connection.FromSequence);

    private static double[] Components(Point3d point) => [point.X, point.Y, point.Z];

    private static double[] Components(Vector3d vector) => [vector.X, vector.Y, vector.Z];

    private static Point3d RestorePoint(double[] coordinates) =>
      coordinates.Length == 3 && coordinates.All(double.IsFinite)
        ? new Point3d(coordinates[0], coordinates[1], coordinates[2])
        : throw new InvalidOperationException("invalid saved shade point");

    private static Vector3d RestoreVector(double[] coordinates) =>
      coordinates.Length == 3 && coordinates.All(double.IsFinite)
        ? new Vector3d(coordinates[0], coordinates[1], coordinates[2])
        : throw new InvalidOperationException("invalid saved shade direction");

    private static void SaveShadeState(
      RhinoDoc doc,
      Guid sessionId,
      IReadOnlyList<ShadePlacement> placements,
      IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes)
    {
      var snapshot = new ShadeSnapshot
      {
        Scallop = _scallop,
        ScallopSize = _scallopSize,
        ScallopPercent = _scallopPercent,
        Join = _join,
        CutOffset = _cutOffset,
        CutLayer = _cutLayer,
        LayerOption = _layer,
        CreateReinf = _createReinf,
        Placements = placements.Select(placement => new PlacementSnapshot
        {
          Sequence = placement.Sequence,
          Corner = Components(placement.Geometry.Corner),
          OffsetEnd = Components(placement.Geometry.OffsetEnd),
          InteriorDirection = Components(placement.Geometry.InteriorDirection),
          FirstCapEnd = placement.Geometry.PerpendicularLine.HasValue
            ? Components(placement.Geometry.FirstCapEnd) : [],
          SecondCapEnd = placement.Geometry.PerpendicularLine.HasValue
            ? Components(placement.Geometry.SecondCapEnd) : [],
          HasOffsetLine = placement.Geometry.OffsetLine.HasValue,
          HasPerpendicularLine = placement.Geometry.PerpendicularLine.HasValue,
          OutputLayerIndex = placement.OutputLayerIndex,
          FirstSourceId = placement.FirstSourceId,
          SecondSourceId = placement.SecondSourceId,
          Connect = placement.Connect,
          Reinforcement = placement.Reinforcement,
          LabelText = placement.LabelText,
          NextLabel = placement.NextLabel
        }).ToList(),
        Scallops = scallopSizes.Select(item => new ScallopSnapshot
        {
          FromSequence = item.Key.From,
          ToSequence = item.Key.To,
          Value = item.Value.Value,
          Percent = item.Value.Percent
        }).ToList()
      };
      doc.Strings.SetString(
        SessionStateSection,
        sessionId.ToString("N"),
        JsonSerializer.Serialize(snapshot));
    }

    private static bool TryRestoreShade(
      RhinoDoc doc,
      Guid sessionId,
      List<ShadePlacement> placements,
      Dictionary<(int From, int To), ScallopSize> scallopSizes,
      List<Guid> outputIds,
      out string layerOption)
    {
      layerOption = string.Empty;
      try
      {
        var json = doc.Strings.GetValue(SessionStateSection, sessionId.ToString("N"));
        if (string.IsNullOrEmpty(json))
          return false;
        var snapshot = JsonSerializer.Deserialize<ShadeSnapshot>(json);
        if (snapshot == null || snapshot.Placements.Count == 0)
          return false;
        foreach (var saved in snapshot.Placements.OrderBy(item => item.Sequence))
        {
          if (saved.Sequence != placements.Count + 1 ||
              saved.OutputLayerIndex < 0 ||
              doc.Layers[saved.OutputLayerIndex] == null)
            return false;
          var corner = RestorePoint(saved.Corner);
          var offsetEnd = RestorePoint(saved.OffsetEnd);
          var firstEnd = saved.HasPerpendicularLine
            ? RestorePoint(saved.FirstCapEnd) : Point3d.Unset;
          var secondEnd = saved.HasPerpendicularLine
            ? RestorePoint(saved.SecondCapEnd) : Point3d.Unset;
          var geometry = new ShadeGeometry(
            corner,
            offsetEnd,
            RestoreVector(saved.InteriorDirection),
            saved.HasOffsetLine ? new Line(corner, offsetEnd) : null,
            saved.HasPerpendicularLine ? new Line(firstEnd, secondEnd) : null,
            firstEnd,
            secondEnd);
          placements.Add(new ShadePlacement(
            saved.Sequence,
            geometry,
            saved.OutputLayerIndex,
            saved.FirstSourceId,
            saved.SecondSourceId,
            saved.Connect,
            saved.Reinforcement,
            saved.LabelText,
            saved.NextLabel));
        }
        foreach (var saved in snapshot.Scallops)
          scallopSizes[(saved.FromSequence, saved.ToSequence)] =
            new ScallopSize(saved.Value, saved.Percent);
        _scallop = snapshot.Scallop;
        _scallopSize = snapshot.ScallopSize;
        _scallopPercent = snapshot.ScallopPercent;
        _join = snapshot.Join;
        _cutOffset = snapshot.CutOffset;
        _cutLayer = snapshot.CutLayer;
        _createReinf = snapshot.CreateReinf;
        layerOption = snapshot.LayerOption;

        var settings = new ObjectEnumeratorSettings
        {
          NormalObjects = true,
          LockedObjects = true,
          HiddenObjects = true,
          DeletedObjects = false
        };
        outputIds.AddRange(doc.Objects.GetObjectList(settings)
          .Where(obj => obj.Attributes.GetUserString(MetadataPrefix + "session") == sessionId.ToString())
          .Select(obj => obj.Id));
        return outputIds.Count > 0;
      }
      catch (Exception ex)
      {
        Log.Write("vShade", $"restore failed session={sessionId}: {ex}");
        return false;
      }
    }

    private static void TuneScallopSizes(
      RhinoDoc doc,
      IReadOnlyList<ShadePlacement> placements,
      Dictionary<(int From, int To), ScallopSize> scallopSizes,
      Func<bool> refreshBoundaryOutput,
      Action recordHistory)
    {
      if (placements.Count < 2)
      {
        RhinoApp.WriteLine("vShade: place at least two connected corners before tuning connectors.");
        return;
      }

      var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      while (true)
      {
        var connectors = BuildConnections(cplane, placements, tolerance)
          .Select(connection => new ShadeConnectionGeometry(
            connection,
            CreateConnectionCurve(cplane, connection, placements, scallopSizes, tolerance)))
          .ToArray();
        if (connectors.Length == 0)
        {
          RhinoApp.WriteLine("vShade: there are no connected perimeter segments to tune.");
          return;
        }

        try
        {
          var hovered = -1;
          var pickCurves = connectors.Select(item => item.Curve).ToArray();
          using var getter = new GetPoint();
          getter.EnableTransparentCommands(true);
          getter.SetCommandPrompt("Click shade sides to apply ScallopSize; press Enter when done");
          getter.AcceptNothing(true);
          getter.AcceptNumber(true, false);
          getter.AcceptString(true);
          var sizeOptionIndex = getter.AddOption("ScallopSize", FormatScallopSize(_tuneScallopSize));
          getter.MouseMove += (_, e) =>
          {
            hovered = PickTuneCurve(
              e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, cplane, pickCurves);
          };
          getter.DynamicDraw += (_, e) =>
          {
            if (hovered >= 0)
              PreviewDisplay.DrawOutlinedCurve(e.Display, connectors[hovered].Curve, Color.Orange);
          };
          var getResult = getter.Get();
          if (getResult == GetResult.Option && getter.Option()?.Index == sizeOptionIndex)
          {
            var value = PromptForScallopSize(
              _tuneScallopSize,
              allowZero: true,
              "ScallopTune size (0=line; positive model units or % = arc)");
            if (value.HasValue)
            {
              _tuneScallopSize = value.Value;
              SaveOptions();
            }
            continue;
          }
          if (getResult == GetResult.Number || getResult == GetResult.String)
          {
            var input = getResult == GetResult.Number
              ? getter.Number().ToString("G17", CultureInfo.InvariantCulture)
              : getter.StringResult().Trim();
            if (TryParseScallopSize(input, allowZero: true, out var size))
            {
              _tuneScallopSize = size;
              SaveOptions();
            }
            else
              RhinoApp.WriteLine("vShade: enter a non-negative scallop size, optionally followed by %.");
            continue;
          }
          if (getResult != GetResult.Point)
            return;
          if (hovered < 0)
            continue;

          var connection = connectors[hovered].Connection;
          var key = ConnectionKey(connection);
          var hadOverride = scallopSizes.TryGetValue(key, out var previous);
          scallopSizes[key] = _tuneScallopSize;
          if (!refreshBoundaryOutput())
          {
            if (hadOverride)
              scallopSizes[key] = previous;
            else
              scallopSizes.Remove(key);
            continue;
          }
          recordHistory();
          RhinoApp.WriteLine(
            $"vShade: connector {key.From}-{key.To} is now " +
            (_tuneScallopSize.Value == 0.0
              ? "a line."
              : $"an arc with bulge {FormatScallopSize(_tuneScallopSize)}."));
        }
        finally
        {
          foreach (var connector in connectors)
            connector.Curve.Dispose();
        }
      }
    }

    private static void TuneCornerOffsets(
      RhinoDoc doc,
      List<ShadePlacement> placements,
      int historyPosition,
      Func<bool> refreshBoundaryOutput,
      Action recordHistory)
    {
      if (historyPosition == 0)
      {
        RhinoApp.WriteLine("vShade: place a corner before tuning its offset.");
        return;
      }

      var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      while (true)
      {
        var targets = new List<(int PlacementIndex, Curve Curve)>();
        for (var index = 0; index < historyPosition; index++)
        {
          var geometry = placements[index].Geometry;
          if (geometry.OffsetLine is { } bisector)
            targets.Add((index, new LineCurve(bisector)));
          if (geometry.PerpendicularLine is { } cap)
            targets.Add((index, new LineCurve(cap)));
        }
        if (targets.Count == 0)
        {
          RhinoApp.WriteLine("vShade: no bisector or cap is available for offset tuning.");
          return;
        }

        try
        {
          var hovered = -1;
          var pickCurves = targets.Select(item => item.Curve).ToArray();
          using var getter = new GetPoint();
          getter.EnableTransparentCommands(true);
          getter.SetCommandPrompt("Click a shade corner to apply Offset; press Enter when done");
          getter.AcceptNothing(true);
          getter.AcceptNumber(true, false);
          var offsetOption = new OptionDouble(_offset, 0.0, double.MaxValue);
          getter.AddOptionDouble("Offset", ref offsetOption);
          getter.MouseMove += (_, e) =>
          {
            hovered = PickTuneCurve(
              e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, cplane, pickCurves);
          };
          getter.DynamicDraw += (_, e) =>
          {
            if (hovered >= 0)
              PreviewDisplay.DrawOutlinedCurve(e.Display, targets[hovered].Curve, Color.Orange);
          };

          var getResult = getter.Get();
          if (getResult == GetResult.Option)
          {
            _offset = offsetOption.CurrentValue;
            SaveOptions();
            continue;
          }
          if (getResult == GetResult.Number)
          {
            var value = getter.Number();
            if (!double.IsFinite(value) || value < 0.0)
              RhinoApp.WriteLine("vShade: Offset must be a non-negative number.");
            else
            {
              _offset = value;
              SaveOptions();
            }
            continue;
          }
          if (getResult != GetResult.Point)
            return;
          if (hovered < 0)
            continue;

          var placementIndex = targets[hovered].PlacementIndex;
          var previous = placements[placementIndex];
          var geometry = previous.Geometry;
          var offsetEnd = geometry.Corner + geometry.InteriorDirection * _offset;
          var shift = offsetEnd - geometry.OffsetEnd;
          if (shift.Length <= RhinoMath.ZeroTolerance)
            continue;
          var perpendicular = geometry.PerpendicularLine is { } cap
            ? new Line(cap.From + shift, cap.To + shift)
            : (Line?)null;
          placements[placementIndex] = previous with
          {
            Geometry = geometry with
            {
              OffsetEnd = offsetEnd,
              OffsetLine = _offset > 0.0 ? new Line(geometry.Corner, offsetEnd) : null,
              PerpendicularLine = perpendicular,
              FirstCapEnd = perpendicular.HasValue
                ? geometry.FirstCapEnd + shift : Point3d.Unset,
              SecondCapEnd = perpendicular.HasValue
                ? geometry.SecondCapEnd + shift : Point3d.Unset
            }
          };
          if (!refreshBoundaryOutput())
          {
            placements[placementIndex] = previous;
            continue;
          }
          recordHistory();
          RhinoApp.WriteLine(
            $"vShade: corner {previous.Sequence} offset is now " +
            _offset.ToString("G", CultureInfo.InvariantCulture) + ".");
        }
        finally
        {
          foreach (var target in targets)
            target.Curve.Dispose();
        }
      }
    }

    private static int PickTuneCurve(
      RhinoViewport viewport,
      int windowX,
      int windowY,
      Plane cplane,
      IReadOnlyList<Curve> curves)
    {
      if (!viewport.GetFrustumLine(windowX, windowY, out var ray))
        return -1;

      var nearest = TunePickRadiusPixels * TunePickRadiusPixels;
      var picked = -1;
      for (var index = 0; index < curves.Count; index++)
      {
        var curve = curves[index];
        if (!Intersection.LinePlane(
              ray,
              new Plane(curve.PointAtStart, cplane.XAxis, cplane.YAxis),
              out var rayParameter) ||
            !curve.ClosestPoint(ray.PointAt(rayParameter), out var curveParameter))
          continue;
        var point = viewport.WorldToClient(curve.PointAt(curveParameter));
        var dx = point.X - windowX;
        var dy = point.Y - windowY;
        var distance = dx * dx + dy * dy;
        if (distance >= nearest)
          continue;
        nearest = distance;
        picked = index;
      }
      return picked;
    }

    private static List<ShadeConnection> BuildConnections(
      Plane cplane,
      IReadOnlyList<ShadePlacement> placements,
      double tolerance)
    {
      var active = placements
        .Where(placement => placement.Connect && placement.Geometry.PerpendicularLine.HasValue)
        .ToArray();
      var connections = new List<ShadeConnection>();
      if (active.Length < 2)
        return connections;

      var candidates = new List<ShadeConnectionCandidate>();
      for (var firstIndex = 0; firstIndex < active.Length; firstIndex++)
      {
        var first = active[firstIndex];
        for (var secondIndex = firstIndex + 1; secondIndex < active.Length; secondIndex++)
        {
          var second = active[secondIndex];
          for (var firstEnd = 0; firstEnd < 2; firstEnd++)
          for (var secondEnd = 0; secondEnd < 2; secondEnd++)
          {
            var firstPoint = CapEnd(first, firstEnd);
            var secondPoint = CapEnd(second, secondEnd);
            var line = new Line(firstPoint, secondPoint);
            if (!line.IsValid || line.Length <= tolerance)
              continue;

            var firstSource = SourceId(first, firstEnd);
            var secondSource = SourceId(second, secondEnd);
            candidates.Add(new ShadeConnectionCandidate(
              line,
              first.Sequence,
              second.Sequence,
              firstEnd,
              secondEnd,
              firstSource != Guid.Empty && firstSource == secondSource));
          }
        }
      }

      var parents = active.ToDictionary(placement => placement.Sequence, placement => placement.Sequence);
      var usedEnds = new HashSet<(int Sequence, int End)>();
      var connectedPairs = new HashSet<(int First, int Second)>();
      var maximumConnections = active.Length > 2 ? active.Length : active.Length - 1;

      int Root(int sequence)
      {
        while (parents[sequence] != sequence)
        {
          parents[sequence] = parents[parents[sequence]];
          sequence = parents[sequence];
        }
        return sequence;
      }

      foreach (var candidate in candidates
        .OrderByDescending(value => value.SharedSource)
        .ThenBy(value => value.Line.Length)
        .ThenBy(value => value.FromSequence)
        .ThenBy(value => value.ToSequence))
      {
        if (connections.Count == maximumConnections)
          break;

        var pair = (candidate.FromSequence, candidate.ToSequence);
        if (connectedPairs.Contains(pair) ||
            usedEnds.Contains((candidate.FromSequence, candidate.FromEnd)) ||
            usedEnds.Contains((candidate.ToSequence, candidate.ToEnd)) ||
            EndpointAlreadyConnected(connections, candidate.Line.From, tolerance) ||
            EndpointAlreadyConnected(connections, candidate.Line.To, tolerance) ||
            connections.Any(connection =>
              HasForbiddenIntersection(cplane, candidate.Line, connection.Line, tolerance)) ||
            active.Any(placement =>
              HasForbiddenIntersection(
                cplane,
                candidate.Line,
                placement.Geometry.PerpendicularLine!.Value,
                tolerance)))
          continue;

        var fromRoot = Root(candidate.FromSequence);
        var toRoot = Root(candidate.ToSequence);
        if (fromRoot == toRoot && connections.Count < active.Length - 1)
          continue;

        connections.Add(new ShadeConnection(
          candidate.Line,
          candidate.FromSequence,
          candidate.ToSequence));
        connectedPairs.Add(pair);
        usedEnds.Add((candidate.FromSequence, candidate.FromEnd));
        usedEnds.Add((candidate.ToSequence, candidate.ToEnd));
        parents[fromRoot] = toRoot;
        Log.Write(
          "vShade",
          $"connection {candidate.FromSequence}<->{candidate.ToSequence} " +
          $"sharedSource={candidate.SharedSource} from={FormatPoint(candidate.Line.From)} " +
          $"to={FormatPoint(candidate.Line.To)}");
      }

      Log.Write(
        "vShade",
        $"perimeter corners={active.Length} connections={connections.Count} " +
        $"closed={active.Length > 2 && connections.Count == active.Length}");
      return connections;
    }

    private static Point3d CapEnd(ShadePlacement placement, int end) =>
      end == 0 ? placement.Geometry.FirstCapEnd : placement.Geometry.SecondCapEnd;

    private static Guid SourceId(ShadePlacement placement, int end) =>
      end == 0 ? placement.FirstSourceId : placement.SecondSourceId;

    private static bool EndpointAlreadyConnected(
      IEnumerable<ShadeConnection> connections,
      Point3d point,
      double tolerance) =>
      connections.Any(connection =>
        connection.Line.From.DistanceTo(point) <= tolerance ||
        connection.Line.To.DistanceTo(point) <= tolerance);

    private static int CountConnectedChains(IReadOnlyList<ShadeConnection> connections)
    {
      var remaining = new HashSet<int>(
        connections.SelectMany(connection =>
          new[] { connection.FromSequence, connection.ToSequence }));
      var count = 0;
      while (remaining.Count > 0)
      {
        count++;
        var pending = new Stack<int>();
        pending.Push(remaining.First());
        while (pending.Count > 0)
        {
          var sequence = pending.Pop();
          if (!remaining.Remove(sequence))
            continue;
          foreach (var connection in connections)
          {
            if (connection.FromSequence == sequence)
              pending.Push(connection.ToSequence);
            else if (connection.ToSequence == sequence)
              pending.Push(connection.FromSequence);
          }
        }
      }

      return count;
    }

    private static Curve CreateConnectionCurve(
      Plane cplane,
      ShadeConnection connection,
      IReadOnlyList<ShadePlacement> placements,
      IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes,
      double tolerance)
    {
      var hasOverride = scallopSizes.TryGetValue(ConnectionKey(connection), out var overrideSize);
      if ((hasOverride && overrideSize.Value <= 0.0) || (!hasOverride && !_scallop))
        return new LineCurve(connection.Line);

      var from = placements[connection.FromSequence - 1];
      var to = placements[connection.ToSequence - 1];
      var side = Vector3d.CrossProduct(cplane.Normal, connection.Line.Direction);
      if (!side.Unitize())
        throw new InvalidOperationException("scallop connector has no perpendicular direction");

      var orientation = Vector3d.Multiply(
        side,
        from.Geometry.InteriorDirection + to.Geometry.InteriorDirection);
      if (Math.Abs(orientation) <= RhinoMath.ZeroTolerance)
        orientation = Vector3d.Multiply(side, from.Geometry.InteriorDirection);
      if (Math.Abs(orientation) <= RhinoMath.ZeroTolerance)
        orientation = Vector3d.Multiply(side, to.Geometry.InteriorDirection);
      if (Math.Abs(orientation) <= RhinoMath.ZeroTolerance)
        throw new InvalidOperationException("could not determine the inside of a scallop connector");
      if (orientation < 0.0)
        side = -side;

      var size = hasOverride
        ? overrideSize
        : new ScallopSize(_scallopSize, _scallopPercent);
      var bulge = size.Percent
        ? connection.Line.Length * size.Value / 100.0
        : size.Value;
      if (!double.IsFinite(bulge) || bulge <= tolerance)
        throw new InvalidOperationException("ScallopSize is too small for a connector");
      var midpoint = 0.5 * (connection.Line.From + connection.Line.To) + side * bulge;
      var arc = new Arc(connection.Line.From, midpoint, connection.Line.To);
      if (!arc.IsValid)
        throw new InvalidOperationException("could not construct an inward scallop arc");
      return new ArcCurve(arc);
    }

    private static void ValidateScallopPerimeter(
      IReadOnlyList<ShadePlacement> placements,
      IReadOnlyList<ShadeConnectionGeometry> connections,
      double tolerance)
    {
      var caps = placements
        .Where(placement => placement.Geometry.PerpendicularLine.HasValue)
        .Select(placement => new LineCurve(placement.Geometry.PerpendicularLine!.Value))
        .ToArray();
      try
      {
        for (var index = 0; index < connections.Count; index++)
        {
          var curve = connections[index].Curve;
          if (caps.Any(cap => HasForbiddenCurveIntersection(curve, cap, tolerance)) ||
              connections.Take(index).Any(other =>
                HasForbiddenCurveIntersection(curve, other.Curve, tolerance)))
            throw new InvalidOperationException(
              "scallop arcs cross the perimeter; reduce ScallopSize");
        }
      }
      finally
      {
        foreach (var cap in caps)
          cap.Dispose();
      }
    }

    private static bool HasForbiddenCurveIntersection(
      Curve first,
      Curve second,
      double tolerance)
    {
      using var intersections = Intersection.CurveCurve(first, second, tolerance, tolerance);
      if (intersections == null)
        return false;
      foreach (var hit in intersections)
      {
        if (hit.IsOverlap)
          return true;
        if (!hit.IsPoint)
          continue;
        var point = hit.PointA;
        var firstEnd = point.DistanceTo(first.PointAtStart) <= tolerance ||
                       point.DistanceTo(first.PointAtEnd) <= tolerance;
        var secondEnd = point.DistanceTo(second.PointAtStart) <= tolerance ||
                        point.DistanceTo(second.PointAtEnd) <= tolerance;
        if (!firstEnd || !secondEnd)
          return true;
      }
      return false;
    }

    private static ArcCurve? CreateReinforcementArc(
      ShadePlacement placement,
      Line perpendicularLine,
      ShadeConnectionGeometry firstConnection,
      ShadeConnectionGeometry secondConnection,
      Plane cplane,
      double tolerance)
    {
      var center = 0.5 * (perpendicularLine.From + perpendicularLine.To);
      if (!TryCirclePointOnConnection(
            center,
            placement.Reinforcement,
            placement.Sequence,
            firstConnection,
            cplane,
            tolerance,
            out var firstPoint) ||
          !TryCirclePointOnConnection(
            center,
            placement.Reinforcement,
            placement.Sequence,
            secondConnection,
            cplane,
            tolerance,
            out var secondPoint))
      {
        Log.Write(
          "vShade",
          $"reinforcement skipped sequence={placement.Sequence} radius={placement.Reinforcement:G17} " +
          "because a connector does not reach the circle");
        return null;
      }

      var firstDirection = firstPoint - center;
      var secondDirection = secondPoint - center;
      var inward = placement.Geometry.InteriorDirection;

      var middleDirection = firstDirection + secondDirection;
      if (!middleDirection.Unitize())
        middleDirection = inward;
      if (Vector3d.Multiply(middleDirection, inward) < 0.0)
        middleDirection = -middleDirection;
      var midpoint = center + middleDirection * placement.Reinforcement;
      var arc = new Arc(firstPoint, midpoint, secondPoint);
      return arc.IsValid ? new ArcCurve(arc) : null;
    }

    private static bool TryCirclePointOnConnection(
      Point3d center,
      double radius,
      int sequence,
      ShadeConnectionGeometry item,
      Plane cplane,
      double tolerance,
      out Point3d point)
    {
      point = Point3d.Unset;
      if (item.Curve is not LineCurve)
      {
        var circlePlane = new Plane(center, cplane.XAxis, cplane.YAxis);
        using var circle = new Circle(circlePlane, radius).ToNurbsCurve();
        using var intersections = Intersection.CurveCurve(item.Curve, circle, tolerance, tolerance);
        if (intersections == null)
          return false;
        var hits = intersections.Where(hit => hit.IsPoint).ToArray();
        if (hits.Length == 0)
          return false;
        var fromStart = item.Connection.FromSequence == sequence;
        var hit = fromStart
          ? hits.OrderBy(value => value.ParameterA).First()
          : hits.OrderByDescending(value => value.ParameterA).First();
        point = hit.PointA;
        return point.IsValid;
      }

      var connection = item.Connection;
      var attached = connection.FromSequence == sequence
        ? connection.Line.From
        : connection.Line.To;
      var other = connection.FromSequence == sequence
        ? connection.Line.To
        : connection.Line.From;
      var direction = other - attached;
      var fromCenter = attached - center;
      var a = direction.SquareLength;
      if (a <= tolerance * tolerance)
        return false;
      var b = 2.0 * Vector3d.Multiply(fromCenter, direction);
      var c = fromCenter.SquareLength - radius * radius;
      var discriminant = b * b - 4.0 * a * c;
      if (discriminant < 0.0)
        return false;

      var root = Math.Sqrt(discriminant);
      var near = (-b - root) / (2.0 * a);
      var far = (-b + root) / (2.0 * a);
      var parameter = near >= 0.0 ? near : far;
      if (parameter < 0.0 || parameter > 1.0)
        return false;
      point = attached + direction * parameter;
      return true;
    }

    private static Guid AddReinforcementLabel(
      RhinoDoc doc,
      Plane cplane,
      ShadePlacement placement,
      Line cap,
      ArcCurve arc,
      Guid sessionId)
    {
      var center = 0.5 * (cap.From + cap.To);
      var arcMidpoint = arc.PointAt(arc.Domain.Mid);
      var yAxis = arcMidpoint - center;
      var xAxis = cap.Direction;
      if (!yAxis.Unitize() || !xAxis.Unitize())
        return Guid.Empty;
      if (Vector3d.Multiply(Vector3d.CrossProduct(xAxis, yAxis), cplane.Normal) < 0.0)
        xAxis = -xAxis;

      var origin = center + (arcMidpoint - center) * LabelRadialPosition;
      using var text = new TextEntity
      {
        Plane = new Plane(origin, xAxis, yAxis),
        PlainText = FormatDisplayLabel(placement.LabelText),
        TextHeight = DefaultLabelHeight,
        Justification = TextJustification.MiddleCenter,
        DimensionStyleId = doc.DimStyles.Current.Id,
        DimensionScale = DefaultLabelDimensionScale,
      };
      if (!AnnotationTextTransform.ApplyFixedDisplayTextHeight(
            doc,
            text,
            doc.Views.ActiveView?.ActiveViewport,
            DefaultLabelHeight,
            DefaultLabelDimensionScale))
        Log.Write("vShade", $"could not fix label height sequence={placement.Sequence}");

      var attributes = CreateIdentifiedAttributes(
        placement.OutputLayerIndex,
        LabelObjectName,
        "label",
        sessionId,
        placement.Sequence,
        placement.FirstSourceId,
        placement.SecondSourceId);
      return doc.Objects.AddText(text, attributes);
    }

    private static ObjectAttributes CreateIdentifiedAttributes(
      int layerIndex,
      string name,
      string role,
      Guid sessionId,
      int sequence,
      Guid firstSourceId,
      Guid secondSourceId,
      Guid? chainId = null)
    {
      var attributes = new ObjectAttributes
      {
        LayerIndex = layerIndex,
        Name = name
      };
      attributes.SetUserString(MetadataPrefix + "version", MetadataVersion);
      attributes.SetUserString(MetadataPrefix + "role", role);
      attributes.SetUserString(MetadataPrefix + "session", sessionId.ToString());
      attributes.SetUserString(
        MetadataPrefix + "sequence",
        sequence.ToString(CultureInfo.InvariantCulture));
      attributes.SetUserString(MetadataPrefix + "source_first", firstSourceId.ToString());
      attributes.SetUserString(MetadataPrefix + "source_second", secondSourceId.ToString());
      if (chainId.HasValue)
        attributes.SetUserString(MetadataPrefix + "chain", chainId.Value.ToString());
      return attributes;
    }

    private static HashSet<Guid> CollectSelectedObjectIds(RhinoDoc doc)
    {
      var result = new HashSet<Guid>();
      foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
        result.Add(obj.Id);
      return result;
    }

    private static ShadeOptionState AddOptions(
      GetBaseClass getter,
      DuplicateOutputLayerSession layerSession,
      string labelValue,
      bool splitAvailable)
    {
      var offsetOption = new OptionDouble(_offset, 0.0, double.MaxValue);
      var chamferOption = new OptionDouble(_chamfer, 0.0, double.MaxValue);
      var reinforcementOption = new OptionDouble(_reinforcement, 0.0, double.MaxValue);
      var createReinfOption = new OptionToggle(_createReinf,"No","Yes");
      var connectOption = new OptionToggle(_connect, "No", "Yes");
      var joinOption = new OptionToggle(_join, "No", "Yes");
      var scallopOption = new OptionToggle(_scallop, "No", "Yes");
      getter.AddOptionDouble("Offset", ref offsetOption);
      getter.AddOptionDouble("Chamfer", ref chamferOption);
      getter.AddOptionDouble("Reinforcement", ref reinforcementOption);
      getter.AddOptionToggle("CreateReinf", ref createReinfOption);
      getter.AddOptionToggle("Connect", ref connectOption);
      getter.AddOptionToggle("Join", ref joinOption);
      var labelOptionIndex = getter.AddOption(
        "Label",
        string.IsNullOrEmpty(labelValue) ? "None" : labelValue);
      getter.AddOptionToggle("Scallop", ref scallopOption);
      var scallopSizeOptionIndex = getter.AddOption("ScallopSize", FormatScallopSize());
      var cutOffsetOption = new OptionDouble(_cutOffset, 0.0, double.MaxValue);
      getter.AddOptionDouble("CutOffset", ref cutOffsetOption);
      var layerOptionIndex = getter.AddOption("FinishedLayer", layerSession.OptionLayerName);
      var cutLayerOptionIndex = getter.AddOption("CutLayer", _cutLayer);
      var scallopTuneOptionIndex = getter.AddOption("ScallopTune");
      var offsetTuneOptionIndex = getter.AddOption("OffsetTune");
      var splitOptionIndex = splitAvailable ? getter.AddOption("Split") : -1;
      return new ShadeOptionState(
        offsetOption,
        chamferOption,
        reinforcementOption,
        createReinfOption,
        scallopOption,
        scallopSizeOptionIndex,
        connectOption,
        joinOption,
        layerOptionIndex,
        labelOptionIndex,
        cutOffsetOption,
        cutLayerOptionIndex,
        scallopTuneOptionIndex,
        offsetTuneOptionIndex,
        splitOptionIndex);
    }

    private static void ApplyOptions(
      RhinoDoc doc,
      RunMode mode,
      DuplicateOutputLayerSession layerSession,
      GetBaseClass getter,
      ShadeOptionState options,
      ref string labelValue)
    {
      _offset = options.Offset.CurrentValue;
      _chamfer = options.Chamfer.CurrentValue;
      _reinforcement = options.Reinforcement.CurrentValue;
      _createReinf = options.CreateReinf.CurrentValue;
      _scallop = options.Scallop.CurrentValue;
      _connect = options.Connect.CurrentValue;
      _join = options.Join.CurrentValue;
      _cutOffset = options.CutOffset.CurrentValue;

      if (getter.Option()?.Index == options.LayerOptionIndex)
        PromptForLayer(doc, mode, layerSession);
      else if (getter.Option()?.Index == options.CutLayerOptionIndex &&
               LayerSelector.TrySelect(
                 doc,
                 _cutLayer,
                 DefaultCutLayer,
                 "vShade cut layer",
                 mode,
                 allowNewLayer: true,
                 out var cutLayer))
        _cutLayer = cutLayer;
      else if (getter.Option()?.Index == options.ScallopSizeOptionIndex)
        PromptForScallopSize();
      else if (getter.Option()?.Index == options.LabelOptionIndex)
      {
        using var textGetter = new GetString();
        textGetter.SetCommandPrompt("Reinforcement label (Enter to disable)");
        textGetter.AcceptNothing(true);
        var result = textGetter.Get();
        if (result == GetResult.String || result == GetResult.Nothing)
        {
          labelValue = result == GetResult.String
            ? textGetter.StringResult().Trim()
            : string.Empty;
          if (Regex.IsMatch(labelValue, @"^[0-9]+\.$"))
            labelValue = labelValue[..^1];
          doc.Strings.SetString(OptionsSectionName, LabelKey, labelValue);
        }
      }
      SaveOptions();
    }

    private static string FormatScallopSize() =>
      FormatScallopSize(new ScallopSize(_scallopSize, _scallopPercent));

    private static string FormatScallopSize(ScallopSize size) =>
      size.Value.ToString("G", CultureInfo.InvariantCulture) +
      (size.Percent ? "%" : string.Empty);

    private static void PromptForScallopSize()
    {
      var size = PromptForScallopSize(
        new ScallopSize(_scallopSize, _scallopPercent),
        allowZero: false,
        "Scallop midpoint bulge (model units or % of span)");
      if (!size.HasValue)
        return;
      _scallopSize = size.Value.Value;
      _scallopPercent = size.Value.Percent;
    }

    private static ScallopSize? PromptForScallopSize(
      ScallopSize current,
      bool allowZero,
      string prompt)
    {
      using var getter = new GetString();
      getter.SetCommandPrompt(prompt);
      getter.SetDefaultString(FormatScallopSize(current));
      getter.AcceptNumber(true, false);
      getter.AcceptNothing(true);
      var result = getter.Get();
      if (result == GetResult.Nothing || result == GetResult.Cancel)
        return null;

      var input = result == GetResult.Number
        ? getter.Number().ToString("G17", CultureInfo.InvariantCulture)
        : getter.StringResult().Trim();
      if (!TryParseScallopSize(input, allowZero, out var size))
      {
        RhinoApp.WriteLine(
          "vShade: ScallopSize must be " +
          (allowZero ? "non-negative" : "positive") + ", optionally followed by %.");
        return null;
      }
      return size;
    }

    private static bool TryParseScallopSize(string input, bool allowZero, out ScallopSize size)
    {
      size = default;
      var percent = input.EndsWith('%');
      var numberText = percent ? input[..^1].Trim() : input;
      if (!double.TryParse(
            numberText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value) ||
          !double.IsFinite(value) ||
          (allowZero ? value < 0.0 : value <= 0.0))
        return false;
      size = new ScallopSize(value, percent);
      return true;
    }

    private static string IncrementLabelValue(string text)
    {
      var match = Regex.Match(text, @"([A-Za-z]+|\d+)$");
      if (!match.Success)
        return text + "1";

      var prefix = text[..match.Index];
      var suffix = match.Value;
      if (suffix.All(char.IsDigit) &&
          System.Numerics.BigInteger.TryParse(
            suffix,
            CultureInfo.InvariantCulture,
            out var number))
        return prefix + (number + 1).ToString(CultureInfo.InvariantCulture)
          .PadLeft(suffix.Length, '0');

      var letters = suffix.ToUpperInvariant().ToCharArray();
      for (var index = letters.Length - 1; index >= 0; index--)
      {
        if (letters[index] == 'Z')
        {
          letters[index] = 'A';
          continue;
        }
        letters[index]++;
        return prefix + new string(letters);
      }
      return prefix + "A" + new string(letters);
    }

    private static string FormatDisplayLabel(string text)
    {
      if (text.Length == 0 || !text.All(char.IsDigit))
        return text;

      var rotated = new char[text.Length];
      for (var index = 0; index < text.Length; index++)
      {
        rotated[index] = text[text.Length - index - 1] switch
        {
          '0' => '0',
          '1' => '1',
          '6' => '9',
          '8' => '8',
          '9' => '6',
          _ => '\0'
        };
        if (rotated[index] == '\0')
          return text;
      }

      return new string(rotated) == text ? text : text + ".";
    }

    private static void PromptForLayer(
      RhinoDoc doc,
      RunMode mode,
      DuplicateOutputLayerSession layerSession)
    {
      if (!LayerSelector.TrySelect(
            doc,
            layerSession.OptionLayerName,
            DuplicateCommandSupport.CurrentLayerOption,
            "vShade finished layer",
            mode,
            allowNewLayer: false,
            out var selectedLayer))
      {
        return;
      }

      _layer = DuplicateCommandSupport.NormalizeLayerOption(selectedLayer);
      layerSession.ApplyOption(doc, _layer);
    }

    private static void LoadOptions()
    {
      var values = ToolsOptionStore.Read(
        OptionsSectionName,
        section =>
        {
          var offset = _offset;
          var chamfer = _chamfer;
          var reinforcement = _reinforcement;
          var createReinf = DefaultCreateReinf;
          var scallop = _scallop;
          var scallopSize = _scallopSize;
          var scallopPercent = _scallopPercent;
          var tuneScallopSize = _tuneScallopSize.Value;
          var tuneScallopPercent = _tuneScallopSize.Percent;
          var cutOffset = _cutOffset;
          var cutLayer = _cutLayer;
          var connect = _connect;
          var join = _join;
          var layer = _layer;
          var splitWidth = _splitWidth;

          if (ToolsOptionStore.TryGetDouble(section, OffsetKey, out var savedOffset) && savedOffset >= 0.0)
            offset = savedOffset;
          if (ToolsOptionStore.TryGetDouble(section, ChamferKey, out var savedChamfer) &&
              double.IsFinite(savedChamfer) && savedChamfer >= 0.0)
            chamfer = savedChamfer;
          else if (ToolsOptionStore.TryGetDouble(section, LegacyLengthKey, out var savedLength) &&
                   double.IsFinite(savedLength) && savedLength >= 0.0)
            chamfer = savedLength;
          if (ToolsOptionStore.TryGetDouble(section, ReinforcementKey, out var savedReinforcement) &&
              double.IsFinite(savedReinforcement) && savedReinforcement >= 0.0)
            reinforcement = savedReinforcement;
          if (ToolsOptionStore.TryGetBool(section,CreateReinfKey,out var savedCreateReinf)) createReinf=savedCreateReinf;
          if (ToolsOptionStore.TryGetBool(section, ConnectKey, out var savedConnect))
            connect = savedConnect;
          if (ToolsOptionStore.TryGetBool(section, ScallopKey, out var savedScallop))
            scallop = savedScallop;
          if (ToolsOptionStore.TryGetDouble(section, ScallopSizeKey, out var savedScallopSize) &&
              double.IsFinite(savedScallopSize) && savedScallopSize > 0.0)
            scallopSize = savedScallopSize;
          if (ToolsOptionStore.TryGetBool(section, ScallopPercentKey, out var savedScallopPercent))
            scallopPercent = savedScallopPercent;
          if (ToolsOptionStore.TryGetDouble(section, TuneScallopSizeKey, out var savedTuneScallopSize) &&
              double.IsFinite(savedTuneScallopSize) && savedTuneScallopSize >= 0.0)
            tuneScallopSize = savedTuneScallopSize;
          if (ToolsOptionStore.TryGetBool(section, TuneScallopPercentKey, out var savedTuneScallopPercent))
            tuneScallopPercent = savedTuneScallopPercent;
          if (ToolsOptionStore.TryGetDouble(section, CutOffsetKey, out var savedCutOffset) &&
              double.IsFinite(savedCutOffset) && savedCutOffset >= 0.0)
            cutOffset = savedCutOffset;
          if (ToolsOptionStore.TryGetString(section, CutLayerKey, out var savedCutLayer) &&
              !string.IsNullOrWhiteSpace(savedCutLayer))
            cutLayer = savedCutLayer;
          if (ToolsOptionStore.TryGetBool(section, JoinKey, out var savedJoin))
            join = savedJoin;
          if (ToolsOptionStore.TryGetString(section, FinishedLayerKey, out var savedLayer)||ToolsOptionStore.TryGetString(section, LayerKey, out savedLayer))
            layer = DuplicateCommandSupport.NormalizeLayerOption(savedLayer);
          if (ToolsOptionStore.TryGetDouble(section, SplitWidthKey, out var savedSplitWidth) &&
              double.IsFinite(savedSplitWidth) && savedSplitWidth > 2 * SplitJointOverlap)
            splitWidth = savedSplitWidth;

          return (offset, chamfer, reinforcement, scallop, scallopSize, scallopPercent,
            tuneScallopSize, tuneScallopPercent,
            cutOffset, cutLayer, connect, join, layer, splitWidth, createReinf);
        });

      _offset = values.offset;
      _chamfer = values.chamfer;
      _reinforcement = values.reinforcement;
      _createReinf = values.createReinf;
      _scallop = values.scallop;
      _scallopSize = values.scallopSize;
      _scallopPercent = values.scallopPercent;
      _tuneScallopSize = new ScallopSize(values.tuneScallopSize, values.tuneScallopPercent);
      _cutOffset = values.cutOffset;
      _cutLayer = values.cutLayer;
      _connect = values.connect;
      _join = values.join;
      _layer = DuplicateCommandSupport.NormalizeLayerOption(values.layer);
      _splitWidth = values.splitWidth;
    }

    private static void SaveOptions() =>
      ToolsOptionStore.Update(OptionsSectionName, section =>
      {
        section[OffsetKey] = _offset;
        section[ChamferKey] = _chamfer;
        section[ReinforcementKey] = _reinforcement;
        section[CreateReinfKey] = _createReinf;
        section[ScallopKey] = _scallop;
        section[ScallopSizeKey] = _scallopSize;
        section[ScallopPercentKey] = _scallopPercent;
        section[TuneScallopSizeKey] = _tuneScallopSize.Value;
        section[TuneScallopPercentKey] = _tuneScallopSize.Percent;
        section[CutOffsetKey] = _cutOffset;
        section[CutLayerKey] = _cutLayer;
        section[ConnectKey] = _connect;
        section[JoinKey] = _join;
        section[FinishedLayerKey] = _layer;
        section[SplitWidthKey] = _splitWidth;
      });

    private static bool HasForbiddenIntersection(
      Plane cplane,
      Line first,
      Line second,
      double tolerance)
    {
      var a = ProjectPoint(cplane, first.From, out _);
      var b = ProjectPoint(cplane, first.To, out _);
      var c = ProjectPoint(cplane, second.From, out _);
      var d = ProjectPoint(cplane, second.To, out _);

      if (!BoundsOverlap(a, b, c, d, tolerance))
        return false;

      var o1 = Orientation(a, b, c);
      var o2 = Orientation(a, b, d);
      var o3 = Orientation(c, d, a);
      var o4 = Orientation(c, d, b);
      var scale = Math.Max(
        1.0,
        Math.Max(Distance(a, b), Distance(c, d)));
      var crossTolerance = tolerance * scale;
      var collinear = Math.Abs(o1) <= crossTolerance &&
                      Math.Abs(o2) <= crossTolerance &&
                      Math.Abs(o3) <= crossTolerance &&
                      Math.Abs(o4) <= crossTolerance;

      if (collinear)
        return CollinearOverlapLength(a, b, c, d) > tolerance;

      var sharesEndpoint =
        Distance(a, c) <= tolerance ||
        Distance(a, d) <= tolerance ||
        Distance(b, c) <= tolerance ||
        Distance(b, d) <= tolerance;
      if (sharesEndpoint)
        return false;

      var proper = ((o1 > crossTolerance && o2 < -crossTolerance) ||
                    (o1 < -crossTolerance && o2 > crossTolerance)) &&
                   ((o3 > crossTolerance && o4 < -crossTolerance) ||
                    (o3 < -crossTolerance && o4 > crossTolerance));
      if (proper)
        return true;

      return (Math.Abs(o1) <= crossTolerance && OnSegment(a, b, c, tolerance)) ||
             (Math.Abs(o2) <= crossTolerance && OnSegment(a, b, d, tolerance)) ||
             (Math.Abs(o3) <= crossTolerance && OnSegment(c, d, a, tolerance)) ||
             (Math.Abs(o4) <= crossTolerance && OnSegment(c, d, b, tolerance));
    }

    private static bool BoundsOverlap(
      Point2d a,
      Point2d b,
      Point2d c,
      Point2d d,
      double tolerance) =>
      Math.Max(Math.Min(a.X, b.X), Math.Min(c.X, d.X)) <=
        Math.Min(Math.Max(a.X, b.X), Math.Max(c.X, d.X)) + tolerance &&
      Math.Max(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)) <=
        Math.Min(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)) + tolerance;

    private static bool OnSegment(
      Point2d start,
      Point2d end,
      Point2d point,
      double tolerance) =>
      point.X >= Math.Min(start.X, end.X) - tolerance &&
      point.X <= Math.Max(start.X, end.X) + tolerance &&
      point.Y >= Math.Min(start.Y, end.Y) - tolerance &&
      point.Y <= Math.Max(start.Y, end.Y) + tolerance;

    private static double CollinearOverlapLength(
      Point2d a,
      Point2d b,
      Point2d c,
      Point2d d)
    {
      var useX = Math.Abs(a.X - b.X) >= Math.Abs(a.Y - b.Y);
      var a0 = useX ? a.X : a.Y;
      var a1 = useX ? b.X : b.Y;
      var b0 = useX ? c.X : c.Y;
      var b1 = useX ? d.X : d.Y;
      return Math.Max(
        0.0,
        Math.Min(Math.Max(a0, a1), Math.Max(b0, b1)) -
        Math.Max(Math.Min(a0, a1), Math.Min(b0, b1)));
    }

    private static double Orientation(Point2d a, Point2d b, Point2d c) =>
      (b.X - a.X) * (c.Y - a.Y) -
      (b.Y - a.Y) * (c.X - a.X);

    private static Point2d ProjectPoint(Plane plane, Point3d point, out double height)
    {
      var delta = point - plane.Origin;
      height = delta * plane.ZAxis;
      return new Point2d(delta * plane.XAxis, delta * plane.YAxis);
    }

    private static Vector2d ProjectVector(Plane plane, Vector3d vector) =>
      new(vector * plane.XAxis, vector * plane.YAxis);

    private static Point3d PointFromPlane(Plane plane, Point2d point, double height) =>
      plane.Origin + plane.XAxis * point.X + plane.YAxis * point.Y + plane.ZAxis * height;

    private static void OrientAwayFromCorner(
      ref Vector2d direction,
      Point2d endpoint,
      Point2d corner,
      double tolerance)
    {
      var fromCorner = new Vector2d(endpoint.X - corner.X, endpoint.Y - corner.Y);
      if (fromCorner.Length <= tolerance)
        return;

      if (direction.X * fromCorner.X + direction.Y * fromCorner.Y < 0.0)
        direction = -direction;
    }

    private static bool Unitize(ref Vector2d vector, double tolerance)
    {
      var length = vector.Length;
      if (!double.IsFinite(length) || length <= tolerance)
        return false;

      vector /= length;
      return true;
    }

    private static double Cross(Vector2d first, Vector2d second) =>
      first.X * second.Y - first.Y * second.X;

    private static double Distance(Point2d first, Point2d second)
    {
      var dx = first.X - second.X;
      var dy = first.Y - second.Y;
      return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string FormatPoint(Point3d point) =>
      $"({point.X:G17},{point.Y:G17},{point.Z:G17})";
  }
}

namespace vTools.Commands
{
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


  public sealed partial class vShade
  {
    private sealed class ShadeUndoRecords(RhinoDoc doc) : IDisposable
    {
      private uint _record;

      public bool BeginShade()
      {
        if (!doc.UndoRecordingEnabled)
          return true;
        _record = doc.BeginUndoRecord("vShade");
        Log.Write("vShade", $"shade undo record={_record} commands={doc.InCommand(true)}");
        return _record != 0;
      }

      public void BeginSplit()
      {
        EndRecord();
        if (!doc.UndoRecordingEnabled)
          return;
        _record = doc.BeginUndoRecord("vShade Split");
        Log.Write("vShade", $"split undo record={_record} commands={doc.InCommand(true)}");
        if (_record == 0)
          throw new InvalidOperationException("could not start the separate split undo record");
      }

      private void EndRecord()
      {
        if (_record == 0)
          return;
        if (doc.CurrentUndoRecordSerialNumber != _record || !doc.EndUndoRecord(_record))
          throw new InvalidOperationException("could not finish the shade's owned undo record");
        Log.Write("vShade", $"finished undo record={_record}");
        _record = 0;
      }

      public void Dispose() => EndRecord();
    }

    private sealed record SplitProposal(Plane Frame, double MinY, double MaxY, List<double> Seams, double MaterialLength);

    private sealed class SplitPanel(Curve outline, BoundingBox bounds, Brep? face) : IDisposable
    {
      public Curve Outline { get; } = outline;
      public BoundingBox Bounds { get; } = bounds;
      public Brep? Face { get; } = face;
      public List<(GeometryBase Geometry, Color Color)> PreviewDetails { get; } = [];
      public void Dispose()
      {
        Outline.Dispose();
        Face?.Dispose();
        foreach (var detail in PreviewDetails)
          detail.Geometry.Dispose();
      }
    }

    private static bool RunSplit(
      RhinoDoc doc, RunMode mode, Guid sessionId, IReadOnlyList<Guid> outputIds,
      Action beginSplitUndo)
    {
      var sourceObject = FindSplitSource(doc, outputIds);
      var source = sourceObject?.Geometry as Curve;
      if (source == null)
      {
        RhinoApp.WriteLine("vShade: Split needs a closed cut or shade boundary.");
        return false;
      }
      var sourceColor = doc.Layers[sourceObject!.Attributes.LayerIndex]?.Color ?? Color.Black;
      Log.Write("vShade", $"split source={sourceObject.Id} role=" +
        $"{sourceObject.Attributes.GetUserString(MetadataPrefix + "role")} " +
        $"layer={doc.Layers[sourceObject.Attributes.LayerIndex]?.FullPath}");

      var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      if (!source.TryGetPlane(out var sourcePlane, doc.ModelAbsoluteTolerance))
      {
        RhinoApp.WriteLine("vShade: Split needs a planar shade boundary.");
        return false;
      }
      if (sourcePlane.Normal * cplane.Normal < 0.0)
        sourcePlane = new Plane(sourcePlane.Origin, sourcePlane.XAxis, -sourcePlane.YAxis);
      if (!TryProposeSplit(source, sourcePlane, _splitWidth, out var proposal))
      {
        RhinoApp.WriteLine("vShade: could not fit the closed planar shade within SplitWidth.");
        return false;
      }

      var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      var seamHover = -1;
      var seamMoving = -1;
      var dragPivot = Point3d.Unset;
      var dragAxis = Vector3d.Unset;
      var lastDragRefresh = 0L;
      var dragging = false;
      var panels = new List<SplitPanel>();

      bool UpdateDrag(Point3d cursor)
      {
        if (seamMoving < 0 || !dragPivot.IsValid)
          return false;
        var direction = sourcePlane.ClosestPoint(cursor) - dragPivot;
        if (direction.Length <= tolerance)
          return false;
        var angle = SplitDragAngle(sourcePlane, direction, dragAxis);
        if (!TryProposeSplitAtPivot(source, sourcePlane, _splitWidth, angle, dragPivot,
              out var next, out var pivotSeamIndex) ||
            !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
          return false;
        proposal = next with { MaterialLength = SplitMaterialLength(panels) };
        seamMoving = pivotSeamIndex;
        return true;
      }

      try
      {
        if (!TryBuildSplitPanels(doc, outputIds, source, proposal, tolerance, panels))
        {
          RhinoApp.WriteLine("vShade: could not form closed split parts.");
          return false;
        }
        proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };

        RhinoApp.WriteLine($"vShade: proposed {panels.Count} part(s), {proposal.MaterialLength:G5} material length. Drag an orange cut to adjust it; Enter accepts the cuts.");
        while (true)
        {
          using var getter = new GetPoint();
          getter.EnableTransparentCommands(true);
          getter.PermitObjectSnap(AllowSplitDragObjectSnaps);
          getter.EnableObjectSnapCursors(AllowSplitDragObjectSnaps);
          getter.SetCommandPrompt("Drag split cuts; Enter to place ghosted parts");
          getter.AcceptNothing(true);
          getter.AcceptNumber(true, false);
          var widthOption = new OptionDouble(_splitWidth, SplitJointOverlap * 2 + tolerance, double.MaxValue);
          getter.AddOptionDouble("SplitWidth", ref widthOption);
          var resetIndex = getter.AddOption("Auto");
          getter.MouseMove += (_, e) =>
          {
            dragging = seamMoving >= 0 && e.LeftButtonDown;
            if (!e.LeftButtonDown)
              seamHover = FindSplitSeam(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, source, proposal);
          };
          getter.MouseDown += (_, e) =>
          {
            seamMoving = FindSplitSeam(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, source, proposal);
            if (seamMoving >= 0)
            {
              using var seam = BuildSplitSeam(
                source, proposal.Frame, proposal.Seams[seamMoving], tolerance);
              if (seam == null)
              {
                seamMoving = -1;
                return;
              }
              var down = sourcePlane.ClosestPoint(e.Point);
              dragPivot = down.DistanceTo(seam.PointAtStart) <= down.DistanceTo(seam.PointAtEnd)
                ? seam.PointAtEnd : seam.PointAtStart;
              dragAxis = proposal.Frame.XAxis;
              getter.SetBasePoint(dragPivot, false);
              getter.Constrain(source, true);
              lastDragRefresh = 0;
              dragging = true;
            }
          };
          getter.DynamicDraw += (_, e) =>
          {
            // DynamicDraw receives the point after Rhino resolves object snaps.
            var now = System.Environment.TickCount64;
            if (dragging && e.CurrentPoint.IsValid &&
                now - lastDragRefresh >= SplitDragRefreshMilliseconds)
            {
              lastDragRefresh = now;
              UpdateDrag(e.CurrentPoint);
            }
            DrawSplitPreview(e.Display, doc, source, proposal, panels, cplane,
              DefaultSplitPreviewAnchor(source, cplane),
              seamMoving >= 0 ? seamMoving : seamHover, sourceColor);
          };
          var result = getter.Get(onMouseUp: true);
          if (result == GetResult.Option)
          {
            if (getter.Option()?.Index == resetIndex ||
                Math.Abs(_splitWidth - widthOption.CurrentValue) > tolerance)
            {
              if (!TryProposeSplit(source, sourcePlane, widthOption.CurrentValue, out var next) ||
                  !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
              {
                RhinoApp.WriteLine("vShade: no valid layout for that width.");
                continue;
              }
              _splitWidth = widthOption.CurrentValue;
              SaveOptions();
              proposal = next;
              proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };
            }
            continue;
          }
          if (result == GetResult.Number)
          {
            var newWidth = getter.Number();
            if (!double.IsFinite(newWidth) || newWidth <= SplitJointOverlap * 2 + tolerance ||
                !TryProposeSplit(source, sourcePlane, newWidth, out var next) ||
                !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
              RhinoApp.WriteLine("vShade: enter a larger valid SplitWidth.");
            else
            {
              _splitWidth = newWidth;
              SaveOptions();
              proposal = next;
              proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };
            }
            continue;
          }
          if (result == GetResult.Nothing)
            break;
          if (result != GetResult.Point)
            return false;
          if (seamMoving < 0)
            continue;

          var updated = UpdateDrag(getter.Point());
          seamMoving = -1;
          dragging = false;
          dragPivot = Point3d.Unset;
          if (!updated)
            RhinoApp.WriteLine("vShade: that pivot cannot form valid parts within SplitWidth; last valid preview kept.");
        }

        var placementAnchor = DefaultSplitPreviewAnchor(source, cplane);
        while (true)
        {
          using var getter = new GetPoint();
          getter.EnableTransparentCommands(true);
          getter.SetCommandPrompt("Place split parts; Enter uses the shown location");
          getter.AcceptNothing(true);
          getter.AcceptNumber(true, false);
          var widthOption = new OptionDouble(_splitWidth, SplitJointOverlap * 2 + tolerance, double.MaxValue);
          getter.AddOptionDouble("SplitWidth", ref widthOption);
          getter.MouseMove += (_, e) => placementAnchor = e.Point;
          getter.DynamicDraw += (_, e) => DrawSplitPreview(
            e.Display, doc, source, proposal, panels, cplane, placementAnchor, -1, sourceColor);
          var result = getter.Get();
          if (result == GetResult.Option || result == GetResult.Number)
          {
            var newWidth = result == GetResult.Number ? getter.Number() : widthOption.CurrentValue;
            if (!double.IsFinite(newWidth) || newWidth <= SplitJointOverlap * 2 + tolerance ||
                !TryProposeSplit(source, sourcePlane, newWidth, out var next) ||
                !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
            {
              RhinoApp.WriteLine("vShade: enter a larger valid SplitWidth.");
              continue;
            }
            _splitWidth = newWidth;
            SaveOptions();
            proposal = next;
            proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };
            continue;
          }
          if (result is not (GetResult.Point or GetResult.Nothing))
            return false;
          if (result == GetResult.Point)
            placementAnchor = getter.Point();
          if (CommitSplit(doc, source, sessionId,
                outputIds, proposal, panels, cplane, placementAnchor, tolerance, beginSplitUndo))
          {
            RhinoApp.WriteLine($"vShade: created {panels.Count} split part(s) within width {_splitWidth:G5}; material length {proposal.MaterialLength:G5}.");
            return true;
          }
          return false;
        }
      }
      finally
      {
        foreach (var panel in panels)
          panel.Dispose();
      }
    }

    private static RhinoObject? FindSplitSource(RhinoDoc doc, IReadOnlyList<Guid> outputIds) =>
      outputIds.Select(doc.Objects.FindId)
        .Where(obj => obj?.Geometry is Curve curve && curve.IsClosed)
        .Where(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") is "cut" or "boundary")
        .OrderBy(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") == "cut" ? 0 : 1)
        .FirstOrDefault();

    private static double PlaneAngle(Plane plane, Vector3d direction) =>
      Math.Atan2(direction * plane.YAxis, direction * plane.XAxis);

    private static double SplitDragAngle(Plane plane, Vector3d direction, Vector3d previousAxis)
    {
      // Either endpoint can move without reversing the laid-out parts' frame.
      if (direction * previousAxis < 0.0) direction = -direction;
      return PlaneAngle(plane, direction);
    }

    private static PlanarPartSplitter.Settings SplitPlannerSettings(double width) => new(
      width, SplitJointOverlap / 2, SplitJointOverlap, SplitLayoutGap, SplitAngleStepDegrees,
      SplitBoundarySamples, SplitMaxParts, SplitSeamRefinementSteps, SplitSeamRefinementPasses);

    private static PlanarPartSplitter.Proposal SharedSplitProposal(SplitProposal proposal) =>
      new(proposal.Frame, proposal.MinY, proposal.MaxY, proposal.Seams, proposal.MaterialLength);

    private static SplitProposal ShadeSplitProposal(PlanarPartSplitter.Proposal proposal) =>
      new(proposal.Frame, proposal.MinY, proposal.MaxY, proposal.Seams, proposal.MaterialLength);

    private static bool TryProposeSplitAtPivot(
      Curve source, Plane sourcePlane, double width, double angle, Point3d pivot,
      out SplitProposal proposal, out int pivotSeamIndex)
    {
      var valid = PlanarPartSplitter.TryAtPivot(source, sourcePlane, SplitPlannerSettings(width),
        angle, pivot, out var shared, out pivotSeamIndex);
      proposal = valid ? ShadeSplitProposal(shared) : null!;
      return valid;
    }

    private static bool TryProposeSplit(Curve source, Plane cplane, double width, out SplitProposal proposal)
    {
      var valid = PlanarPartSplitter.TryPropose(source, cplane, SplitPlannerSettings(width), out var shared);
      proposal = valid ? ShadeSplitProposal(shared) : null!;
      return valid;
    }

    private static bool ValidSplitSeams(SplitProposal proposal, double width, double tolerance) =>
      PlanarPartSplitter.Valid(SharedSplitProposal(proposal), SplitPlannerSettings(width), tolerance);

    private static bool TryReplaceSplitPanels(
      RhinoDoc doc, IReadOnlyList<Guid> outputIds,
      Curve source, SplitProposal proposal, double tolerance, List<SplitPanel> panels)
    {
      var replacement = new List<SplitPanel>();
      if (!TryBuildSplitPanels(doc, outputIds, source, proposal, tolerance, replacement))
        return false;
      foreach (var panel in panels)
        panel.Dispose();
      panels.Clear();
      panels.AddRange(replacement);
      return true;
    }

    private static double SplitMaterialLength(IReadOnlyList<SplitPanel> panels) =>
      panels.Sum(panel => panel.Bounds.Max.X - panel.Bounds.Min.X) +
      Math.Max(0, panels.Count - 1) * SplitLayoutGap;

    private static bool TryBuildSplitPanels(
      RhinoDoc doc, IReadOnlyList<Guid> outputIds,
      Curve source, SplitProposal proposal, double tolerance, List<SplitPanel> panels)
    {
      try
      {
        for (var index = 0; index <= proposal.Seams.Count; index++)
        {
          var lower = index == 0 ? proposal.MinY : proposal.Seams[index - 1] - SplitJointOverlap / 2;
          var upper = index == proposal.Seams.Count ? proposal.MaxY : proposal.Seams[index] + SplitJointOverlap / 2;
          var outline = BuildExactSplitOutline(
            source, proposal.Frame, lower, upper,
            index > 0, index < proposal.Seams.Count, tolerance);
          if (outline == null)
          {
            throw new InvalidOperationException($"part {index + 1} could not be assembled from the original cut boundary");
          }
          var faces = Brep.CreatePlanarBreps(outline, tolerance);
          var face = faces?.FirstOrDefault();
          if (faces != null)
            foreach (var extra in faces.Skip(1))
              extra.Dispose();
          panels.Add(new SplitPanel(
            outline, outline.GetBoundingBox(proposal.Frame), face));
        }
        for (var index = 0; index < panels.Count; index++)
        {
          var panel = panels[index];
          var firstY = index == 0 ? proposal.MinY : proposal.Seams[index - 1];
          var lastY = index == panels.Count - 1 ? proposal.MaxY : proposal.Seams[index];
          foreach (var sourceId in outputIds)
          {
            var obj = doc.Objects.FindId(sourceId);
            if (obj == null || obj.Attributes.LayerIndex < 0 ||
                obj.Attributes.LayerIndex >= doc.Layers.Count)
              continue;
            if(obj.Attributes.GetUserString(MetadataPrefix+"role") is "reinforcement_part" or "reinforcement_part_label") continue;
            var color = doc.Layers[obj.Attributes.LayerIndex]?.Color ?? Color.Black;
            if (obj.Geometry is Curve detail && !ReferenceEquals(detail, source) &&
                obj.Attributes.GetUserString(MetadataPrefix + "role") is not ("bisector" or "cut"))
            {
              bool boundary = obj.Attributes.GetUserString(MetadataPrefix + "role") == "boundary";
              foreach (var clipped in ClipSplitPartDetail(
                detail, panel, proposal.Frame, tolerance, boundary))
                panel.PreviewDetails.Add((clipped, color));
            }
            else if (obj.Geometry is TextEntity label)
            {
              var y = FrameY(proposal.Frame, label.Plane.Origin);
              if (y >= firstY - tolerance && y <= lastY + tolerance &&
                  panel.Outline.Contains(label.Plane.Origin, proposal.Frame, tolerance) != PointContainment.Outside &&
                  label.Duplicate() is TextEntity labelCopy)
                panel.PreviewDetails.Add((labelCopy, color));
            }
          }
        }
        return true;
      }
      catch (Exception ex)
      {
        foreach (var panel in panels)
          panel.Dispose();
        panels.Clear();
        Log.Write("vShade", $"split layout rejected: {ex.Message}");
        return false;
      }
    }

    private static Curve? BuildExactSplitOutline(
      Curve source, Plane frame, double lower, double upper,
      bool cutLower, bool cutUpper, double tolerance)
    {
      var seams = new List<Curve>();
      var pieces = new List<Curve>();
      try
      {
        if (cutLower)
        {
          var seam = BuildSplitSeam(source, frame, lower, tolerance);
          if (seam == null) return null;
          seams.Add(seam);
        }
        if (cutUpper)
        {
          var seam = BuildSplitSeam(source, frame, upper, tolerance);
          if (seam == null) return null;
          seams.Add(seam);
        }

        var parameters = new List<double>();
        foreach (var seam in seams)
        {
          if (!source.ClosestPoint(seam.PointAtStart, out double first) ||
              !source.ClosestPoint(seam.PointAtEnd, out double last) ||
              source.PointAt(first).DistanceTo(seam.PointAtStart) > tolerance ||
              source.PointAt(last).DistanceTo(seam.PointAtEnd) > tolerance)
            return null;
          parameters.Add(first);
          parameters.Add(last);
        }
        var splitParameters = parameters
          .Where(t => t > source.Domain.T0 + RhinoMath.ZeroTolerance &&
                      t < source.Domain.T1 - RhinoMath.ZeroTolerance)
          .Distinct()
          .OrderBy(t => t)
          .ToArray();
        var split = splitParameters.Length == 0
          ? new[] { source.DuplicateCurve() }
          : source.Split(splitParameters);
        if (split == null || split.Length == 0)
          return null;
        foreach (var piece in split)
        {
          var y = FrameY(frame, piece.PointAtNormalizedLength(0.5));
          if (y >= lower - tolerance && y <= upper + tolerance)
            pieces.Add(piece);
          else
            piece.Dispose();
        }
        if (pieces.Count == 0)
          return null;
        pieces.AddRange(seams.Select(seam => seam.DuplicateCurve()));
        var joined = Curve.JoinCurves(pieces, tolerance, preserveDirection: false);
        if (joined.Length != 1 || !joined[0].IsClosed)
        {
          foreach (var curve in joined) curve.Dispose();
          return null;
        }
        return joined[0];
      }
      finally
      {
        foreach (var seam in seams) seam.Dispose();
        foreach (var piece in pieces) piece.Dispose();
      }
    }

    private static double FrameY(Plane frame, Point3d point) => (point - frame.Origin) * frame.YAxis;

    private static int FindSplitSeam(
      Rhino.Display.RhinoViewport viewport, int x, int y, Curve source, SplitProposal proposal)
    {
      if (!viewport.GetFrustumLine(x, y, out var ray) ||
          !Intersection.LinePlane(ray, proposal.Frame, out var parameter))
        return -1;
      var cursorY = FrameY(proposal.Frame, ray.PointAt(parameter));
      var cursorX = (ray.PointAt(parameter) - proposal.Frame.Origin) * proposal.Frame.XAxis;
      var bounds = source.GetBoundingBox(proposal.Frame);
      if (cursorX < bounds.Min.X || cursorX > bounds.Max.X)
        return -1;
      var nearest = TunePickRadiusPixels;
      var result = -1;
      for (var index = 0; index < proposal.Seams.Count; index++)
      {
        var point = ray.PointAt(parameter) + proposal.Frame.YAxis * (proposal.Seams[index] - cursorY);
        var pixel = viewport.WorldToClient(point);
        var dx = pixel.X - x;
        var dy = pixel.Y - y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance >= nearest)
          continue;
        nearest = distance;
        result = index;
      }
      return result;
    }

    private static Point3d DefaultSplitPreviewAnchor(Curve source, Plane plane)
    {
      var bounds = source.GetBoundingBox(plane);
      return plane.PointAt(bounds.Max.X + SplitLayoutGap * 4, bounds.Min.Y);
    }

    private static Transform PanelTransform(
      Plane frame, Plane cplane, Point3d anchor, BoundingBox bounds, double xOffset)
    {
      var target = new Plane(anchor, cplane.XAxis, cplane.YAxis);
      return Transform.Translation(target.XAxis * (xOffset - bounds.Min.X) -
                                   target.YAxis * bounds.Min.Y) *
             Transform.PlaneToPlane(frame, target);
    }

    private static void DrawSplitPreview(
      Rhino.Display.DisplayPipeline display,
      RhinoDoc doc,
      Curve source,
      SplitProposal proposal,
      IReadOnlyList<SplitPanel> panels,
      Plane cplane,
      Point3d anchor,
      int highlightedSeam,
      Color sourceColor)
    {
      var cutLayerIndex = doc.Layers.FindByFullPath(_cutLayer, -1);
      var cutColor = cutLayerIndex >= 0 ? doc.Layers[cutLayerIndex].Color : sourceColor;
      var fadedCut = FadedSplitColor(cutColor);
      var plotLayerIndex = doc.Layers.FindByFullPath(SplitOverlapLayerName, -1);
      var plotColor = plotLayerIndex >= 0
        ? doc.Layers[plotLayerIndex].Color
        : DefaultSplitPlotColor;
      var fadedPlot = FadedSplitColor(plotColor);
      for (var index = 0; index < proposal.Seams.Count; index++)
      {
        using var seam = BuildSplitSeam(
          source, proposal.Frame, proposal.Seams[index], doc.ModelAbsoluteTolerance);
        if (seam != null)
        {
          if (index == highlightedSeam)
            PreviewDisplay.DrawOutlinedCurve(display, seam, SplitSeamColor);
          else
            PreviewDisplay.DrawCurve(display, seam, SplitSeamColor);
        }
      }
      var xOffset = 0.0;
      using var ghostMaterial = new Rhino.Display.DisplayMaterial(sourceColor)
      {
        Transparency = SplitGhostTransparency,
        BackTransparency = SplitGhostTransparency
      };
      for (var panelIndex = 0; panelIndex < panels.Count; panelIndex++)
      {
        var panel = panels[panelIndex];
        var transform = PanelTransform(proposal.Frame, cplane, anchor, panel.Bounds, xOffset);
        if (panel.Face != null)
        {
          using var face = panel.Face.DuplicateBrep();
          face.Transform(transform);
          display.DrawBrepShaded(face, ghostMaterial);
        }
        foreach (var detail in panel.PreviewDetails)
        {
          using var copy = detail.Geometry.Duplicate();
          if (copy == null)
            continue;
          copy.Transform(transform);
          var color = FadedSplitColor(detail.Color);
          if (copy is Curve curve)
            PreviewDisplay.DrawCurve(display, curve, color);
          else if (copy is TextEntity label)
            display.DrawAnnotation(label, color);
        }
        foreach (var seamIndex in new[] { panelIndex - 1, panelIndex })
        {
          if (seamIndex < 0 || seamIndex >= proposal.Seams.Count)
            continue;
          var overlapY = OverlapMarkY(proposal.Seams[seamIndex], panelIndex, seamIndex);
          using var overlapLine = BuildSplitSeam(
            source, proposal.Frame, overlapY, doc.ModelAbsoluteTolerance);
          if (overlapLine == null)
            continue;
          using var overlapLabel = CreateOverlapLabel(
            doc, source, proposal.Frame, overlapLine, proposal.Seams[seamIndex], overlapY,
            seamIndex + 1);
          overlapLine.Transform(transform);
          overlapLabel.Transform(transform);
          PreviewDisplay.DrawCurve(display, overlapLine, fadedPlot);
          display.DrawAnnotation(overlapLabel, fadedPlot);
        }
        using var preview = panel.Outline.DuplicateCurve();
        preview.Transform(transform);
        PreviewDisplay.DrawCurve(display, preview, fadedCut);
        xOffset += panel.Bounds.Max.X - panel.Bounds.Min.X + SplitLayoutGap;
      }
    }

    private static Color FadedSplitColor(Color color) =>
      Color.FromArgb(SplitPreviewAlpha, color.R, color.G, color.B);

    private static Curve? BuildSplitSeam(Curve source, Plane frame, double y, double tolerance) =>
      PlanarPartSplitter.Seam(source, frame, y, tolerance);

    private static double OverlapMarkY(double seamY, int partIndex, int seamIndex) =>
      seamY + (seamIndex == partIndex ? -SplitJointOverlap / 2 : SplitJointOverlap / 2);

    private static TextEntity CreateOverlapLabel(
      RhinoDoc doc,
      Curve source,
      Plane frame,
      Curve overlapLine,
      double seamY,
      double overlapY,
      int seamNumber)
    {
      var cutY = 2.0 * seamY - overlapY;
      using var cutLine = BuildSplitSeam(
        source, frame, cutY, doc.ModelAbsoluteTolerance);
      var overlapBounds = overlapLine.GetBoundingBox(frame);
      var cutBounds = cutLine?.GetBoundingBox(frame) ?? overlapBounds;
      double minX = Math.Max(overlapBounds.Min.X, cutBounds.Min.X);
      double maxX = Math.Min(overlapBounds.Max.X, cutBounds.Max.X);
      if (maxX <= minX)
      {
        minX = overlapBounds.Min.X;
        maxX = overlapBounds.Max.X;
      }
      var origin = frame.PointAt((minX + maxX) / 2.0, seamY);
      var text = new TextEntity
      {
        DimensionStyleId = doc.DimStyles.Current.Id,
        Plane = new Plane(origin, frame.XAxis, frame.YAxis),
        TextHeight = SplitOverlapLabelHeight,
        Justification = TextJustification.BottomLeft,
        DimensionScale = DefaultLabelDimensionScale,
        TextOrientation = TextOrientation.InPlane,
        DrawForward = false,
        PlainText = seamNumber.ToString(CultureInfo.InvariantCulture)
      };
      if (!AnnotationTextTransform.ApplyFixedDisplayTextHeight(
        doc, text, doc.Views.ActiveView?.ActiveViewport,
        SplitOverlapLabelHeight, DefaultLabelDimensionScale))
        Log.Write("vShade", $"split seam text height override failed scale={doc.ModelSpaceTextScale:G17}");
      var bounds = OverlapLabelGlyphBounds(doc, text, frame);
      if (bounds.IsValid)
      {
        text.Transform(Transform.Translation(
          frame.XAxis * (((minX + maxX) / 2.0) - bounds.Center.X) +
          frame.YAxis * (seamY - bounds.Center.Y)));
      }
      return text;
    }

    private static BoundingBox OverlapLabelGlyphBounds(RhinoDoc doc, TextEntity text, Plane frame)
    {
      var parentStyle = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
      using var measurement = (TextEntity)text.Duplicate();
      measurement.ParentDimensionStyle = parentStyle;
      measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(
        doc, text, doc.Views.ActiveView?.ActiveViewport);
      using var style = measurement.GetDimensionStyle(parentStyle);
      var outlines = measurement.CreateCurves(style ?? parentStyle, true,
        SplitLabelGlyphScale, SplitLabelGlyphSpacing);
      if (outlines == null || outlines.Length == 0)
        return text.GetBoundingBox(frame);
      var bounds = BoundingBox.Empty;
      foreach (var outline in outlines)
      {
        try { bounds.Union(outline.GetBoundingBox(frame)); }
        finally { outline.Dispose(); }
      }
      return bounds.IsValid ? bounds : text.GetBoundingBox(frame);
    }

    private static bool CommitSplit(
      RhinoDoc doc, Curve source, Guid sessionId, IReadOnlyList<Guid> outputIds,
      SplitProposal proposal, IReadOnlyList<SplitPanel> panels,
      Plane cplane, Point3d anchor, double tolerance, Action beginSplitUndo)
    {
      var created = new List<Guid>();
      var sourceSeams = new List<Guid>();
      var createdGroups = new List<int>();
      var splitId = Guid.NewGuid();
      try
      {
        beginSplitUndo();
        var referenceLayer = UzipCommon.EnsureLayer(doc, ReferenceLayerName, ReferenceLayerColor);
        var cutLayer = UzipCommon.EnsureLayer(doc, _cutLayer);
        var overlapLayer = proposal.Seams.Count > 0
          ? UzipCommon.EnsureLayer(doc, SplitOverlapLayerName)
          : -1;
        foreach (var y in proposal.Seams)
        {
          using var seam = BuildSplitSeam(source, proposal.Frame, y, tolerance);
          if (seam == null)
            throw new InvalidOperationException("a cut line no longer crosses the shade twice");
          var attributes = new ObjectAttributes { LayerIndex = referenceLayer, Name = SplitSeamObjectName };
          attributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
          attributes.SetUserString(MetadataPrefix + "source_session", sessionId.ToString());
          var id = doc.Objects.AddCurve(seam, attributes);
          if (id == Guid.Empty)
            throw new InvalidOperationException("could not create a split cut line");
          created.Add(id);
          sourceSeams.Add(id);
        }

        var xOffset = 0.0;
        for (var index = 0; index < panels.Count; index++)
        {
          var partIds = new List<Guid>();
          var panel = panels[index];
          var transform = PanelTransform(proposal.Frame, cplane, anchor, panel.Bounds, xOffset);
          foreach (var seamIndex in new[] { index - 1, index })
          {
            if (seamIndex < 0 || seamIndex >= proposal.Seams.Count)
              continue;
            var overlapY = OverlapMarkY(proposal.Seams[seamIndex], index, seamIndex);
            using var overlapLine = BuildSplitSeam(source, proposal.Frame, overlapY, tolerance);
            if (overlapLine == null)
              throw new InvalidOperationException("could not mark the overlap on a split part");
            using var overlapLabel = CreateOverlapLabel(
              doc, source, proposal.Frame, overlapLine, proposal.Seams[seamIndex], overlapY,
              seamIndex + 1);
            overlapLine.Transform(transform);
            overlapLabel.Transform(transform);
            var overlapAttributes = new ObjectAttributes
            {
              LayerIndex = overlapLayer,
              Name = SplitOverlapLineObjectName
            };
            overlapAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
            overlapAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
            var overlapId = doc.Objects.AddCurve(overlapLine, overlapAttributes);
            if (overlapId == Guid.Empty)
              throw new InvalidOperationException("could not add an overlap line");
            created.Add(overlapId);
            partIds.Add(overlapId);

            var labelAttributes = new ObjectAttributes
            {
              LayerIndex = overlapLayer,
              Name = SplitOverlapLabelObjectName
            };
            labelAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
            labelAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
            var overlapLabelId = doc.Objects.AddText(overlapLabel, labelAttributes);
            if (overlapLabelId == Guid.Empty)
              throw new InvalidOperationException("could not add an overlap label");
            created.Add(overlapLabelId);
            partIds.Add(overlapLabelId);
            var placedFrame = proposal.Frame;
            placedFrame.Transform(transform);
            var placedLabel = doc.Objects.FindId(overlapLabelId)?.Geometry as TextEntity ?? overlapLabel;
            var glyphBounds = OverlapLabelGlyphBounds(doc, placedLabel, placedFrame);
            var cutY = 2.0 * proposal.Seams[seamIndex] - overlapY;
            var clearance = Math.Min(
              glyphBounds.Min.Y - Math.Min(cutY, overlapY),
              Math.Max(cutY, overlapY) - glyphBounds.Max.Y);
            Log.Write("vShade", $"split label={overlapLabelId} part={index + 1} " +
              $"seam={seamIndex + 1} cutY={cutY:G17} " +
              $"plotY={overlapY:G17} height={overlapLabel.TextHeight:G6} " +
              $"glyphY=[{glyphBounds.Min.Y:G6},{glyphBounds.Max.Y:G6}] " +
              $"clearance={clearance:G6} fits={glyphBounds.IsValid && clearance >= SplitOverlapLabelMargin} " +
              $"displayScale={AnnotationTextTransform.ResolveDisplayDimensionScale(doc, overlapLabel, doc.Views.ActiveView?.ActiveViewport):G6} " +
              $"modelScale={doc.ModelSpaceTextScale:G6} layer={doc.Layers[overlapLayer]?.FullPath}");
          }

          foreach (var sourceId in outputIds)
          {
            var obj = doc.Objects.FindId(sourceId);
            if (obj?.Geometry is not Curve detail || ReferenceEquals(detail, source) ||
                obj.Attributes.GetUserString(MetadataPrefix + "role") is "bisector" or "cut")
              continue;
            bool boundary = obj.Attributes.GetUserString(MetadataPrefix + "role") == "boundary";
            foreach (var clipped in ClipSplitPartDetail(
              detail, panel, proposal.Frame, tolerance, boundary))
            {
              using (clipped)
              {
                clipped.Transform(transform);
                var detailAttributes = new ObjectAttributes
                {
                  LayerIndex = obj.Attributes.LayerIndex,
                  Name = SplitPartObjectName
                };
                detailAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
                detailAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
                var detailId = doc.Objects.AddCurve(clipped, detailAttributes);
                if (detailId == Guid.Empty)
                  throw new InvalidOperationException("could not copy split detail");
                created.Add(detailId);
                partIds.Add(detailId);
              }
            }
          }
          var firstY = index == 0 ? proposal.MinY : proposal.Seams[index - 1];
          var lastY = index == panels.Count - 1 ? proposal.MaxY : proposal.Seams[index];
          foreach (var sourceId in outputIds)
          {
            var obj = doc.Objects.FindId(sourceId);
            if (obj?.Geometry is not TextEntity label)
              continue;
            var y = FrameY(proposal.Frame, label.Plane.Origin);
            if (y < firstY - tolerance || y > lastY + tolerance ||
                panel.Outline.Contains(label.Plane.Origin, proposal.Frame, tolerance) == PointContainment.Outside)
              continue;
            using var copy = label.Duplicate() as TextEntity;
            if (copy == null)
              continue;
            copy.Transform(transform);
            var labelAttributes = new ObjectAttributes
            {
              LayerIndex = obj.Attributes.LayerIndex,
              Name = SplitPartObjectName
            };
            labelAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
            labelAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
            var labelId = doc.Objects.AddText(copy, labelAttributes);
            if (labelId == Guid.Empty)
              throw new InvalidOperationException("could not copy a reinforcement label");
            created.Add(labelId);
            partIds.Add(labelId);
          }
          using var outline = panel.Outline.DuplicateCurve();
          outline.Transform(transform);
          var attributes = new ObjectAttributes
          {
            LayerIndex = cutLayer,
            Name = SplitPartObjectName
          };
          attributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
          attributes.SetUserString(MetadataPrefix + "source_session", sessionId.ToString());
          attributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
          var id = doc.Objects.AddCurve(outline, attributes);
          if (id == Guid.Empty)
            throw new InvalidOperationException($"could not create part {index + 1}");
          created.Add(id);
          partIds.Add(id);
          Log.Write("vShade", $"split part={index + 1} cut={id} layer={doc.Layers[cutLayer]?.FullPath}");
          var partGroup = doc.Groups.Add(
            "vShadeSplit_" + splitId.ToString("N") + "_" + (index + 1).ToString(CultureInfo.InvariantCulture));
          if (partGroup < 0)
            throw new InvalidOperationException($"could not create group for part {index + 1}");
          createdGroups.Add(partGroup);
          if (!doc.Groups.AddToGroup(partGroup, partIds))
            throw new InvalidOperationException($"could not group part {index + 1}");
          xOffset += panel.Bounds.Max.X - panel.Bounds.Min.X + SplitLayoutGap;
        }

        if (sourceSeams.Count > 0)
        {
          var shadeGroupName = ShadeGroupPrefix + sessionId.ToString("N");
          var shadeGroup = doc.Groups.FindName(shadeGroupName)?.Index;
          if (!shadeGroup.HasValue)
          {
            shadeGroup = doc.Groups.Add(shadeGroupName);
            if (shadeGroup.Value >= 0)
              createdGroups.Add(shadeGroup.Value);
          }
          if (!shadeGroup.HasValue || shadeGroup.Value < 0 ||
              !doc.Groups.AddToGroup(shadeGroup.Value, sourceSeams))
            throw new InvalidOperationException("could not add split cuts to the original shade group");
        }
        doc.Views.Redraw();
        Log.Write("vShade", $"split source={sessionId} parts={panels.Count} width={_splitWidth:G17} cuts={proposal.Seams.Count}");
        return true;
      }
      catch (Exception ex)
      {
        foreach (var id in created)
          doc.Objects.Delete(id, quiet: true);
        foreach (var groupIndex in createdGroups)
          doc.Groups.Delete(groupIndex);
        doc.Views.Redraw();
        Log.Write("vShade", $"split failed: {ex}");
        RhinoApp.WriteLine($"vShade: split failed: {ex.Message}");
        return false;
      }
    }

    private static IEnumerable<Curve> ClipSplitDetail(Curve detail, Curve clip, Plane frame, double tolerance) =>
      PlanarPartSplitter.ClipCurve(detail, clip, frame, tolerance);

    private static IEnumerable<Curve> ClipSplitPartDetail(
      Curve detail, SplitPanel panel, Plane frame, double tolerance, bool boundary)
    {
      if (!boundary)
        return ClipSplitDetail(detail, panel.Outline, frame, tolerance);
      var segments = detail.DuplicateSegments();
      if (segments == null || segments.Length <= 1)
      {
        if (segments != null)
          foreach (var segment in segments) segment.Dispose();
        return ClipSplitDetail(detail, panel.Outline, frame, tolerance)
          .Where(KeepDistinctCutDetail).ToArray();
      }
      var retained = new List<Curve>();
      try
      {
        foreach (var segment in segments)
          foreach (var clipped in ClipSplitDetail(segment, panel.Outline, frame, tolerance))
            if (KeepDistinctCutDetail(clipped))
              retained.Add(clipped);
        return retained;
      }
      finally
      {
        foreach (var segment in segments) segment.Dispose();
      }

      bool KeepDistinctCutDetail(Curve candidate)
      {
        foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
        {
          var point = candidate.PointAtNormalizedLength(fraction);
          if (!panel.Outline.ClosestPoint(point, out double parameter) ||
              point.DistanceTo(panel.Outline.PointAt(parameter)) >
                tolerance * SplitCoincidentBoundaryToleranceMultiplier)
            return true;
        }
        candidate.Dispose();
        return false;
      }
    }
  }
}

namespace vTools.Commands
{
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vShade
  {
    private static bool RefreshReinforcementParts(RhinoDoc doc,Guid sessionId,IReadOnlyList<ShadePlacement> placements,
      IReadOnlyDictionary<(int From,int To),ScallopSize> scallopSizes,List<Guid> outputIds)
    {
      var oldParts=outputIds.Where(id=>doc.Objects.FindId(id)?.Attributes.GetUserString(MetadataPrefix+"role")
        is "reinforcement_part" or "reinforcement_part_label").ToArray();
      var retained=outputIds.Except(oldParts).ToList();
      int retainedCount=retained.Count;
      try
      {
        if(_createReinf&&placements.Count>0)
          AddReinforcementParts(doc,doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??Plane.WorldXY,
            placements,Array.Empty<ShadeConnectionGeometry>(),sessionId,Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance),retained);
        SaveShadeState(doc,sessionId,placements,scallopSizes);
        foreach(var id in oldParts) doc.Objects.Delete(id,true);
        outputIds.Clear(); outputIds.AddRange(retained);
        doc.Views.Redraw();
        return true;
      }
      catch(Exception ex)
      {
        foreach(var id in retained.Skip(retainedCount)) doc.Objects.Delete(id,true);
        Log.Write("vShade","reinforcement update failed: {0}",ex);
        RhinoApp.WriteLine("vShade: could not update detached reinforcement parts: {0}",ex.Message);
        return false;
      }
    }

    private static void AddReinforcementParts(RhinoDoc doc,Plane plane,IReadOnlyList<ShadePlacement> placements,
      IReadOnlyList<ShadeConnectionGeometry> connections,Guid sessionId,double tolerance,List<Guid> outputIds)
    {
      var originalObjects=outputIds.Select(doc.Objects.FindId).Where(obj=>obj!=null).Cast<RhinoObject>().ToArray();
      var cutCurves=originalObjects.Where(obj=>obj.Attributes.GetUserString(MetadataPrefix+"role")=="cut").Select(obj=>obj.Geometry).OfType<Curve>().ToArray();
      var currentFinished=originalObjects.Where(obj=>obj.Attributes.GetUserString(MetadataPrefix+"role")
        is "boundary" or "perpendicular" or "connection" or "scallop").Select(obj=>obj.Geometry).OfType<Curve>().ToArray();
      var finishedPieces=currentFinished.Length>0?currentFinished.Select(curve=>curve.DuplicateCurve()).ToArray():placements.Where(item=>item.Geometry.PerpendicularLine.HasValue)
        .Select(item=>(Curve)new LineCurve(item.Geometry.PerpendicularLine!.Value)).Concat(connections.Select(item=>item.Curve.DuplicateCurve())).ToArray();
      var finished=Curve.JoinCurves(finishedPieces,tolerance,false);
      try
      {
        var borders=cutCurves.Length>0?cutCurves:finished.Where(curve=>curve.IsClosed).ToArray();
        foreach(var placement in placements)
        {
          var sequence=placement.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
          var arc=originalObjects.FirstOrDefault(obj=>obj.Attributes.GetUserString(MetadataPrefix+"role")=="reinforcement"&&
            obj.Attributes.GetUserString(MetadataPrefix+"sequence")==sequence)?.Geometry as Curve;
          if(arc==null||placement.Geometry.PerpendicularLine is not {} cap) continue;
          var incident=connections.Where(item=>item.Connection.FromSequence==placement.Sequence||item.Connection.ToSequence==placement.Sequence).ToArray();
          using var part=currentFinished.Length>0
            ? ReinforcementPartOutlineFromBoundary(arc,cap,finished,tolerance)
            : incident.Length==2?ReinforcementPartOutline(arc,cap,incident[0],incident[1],placement.Sequence,tolerance):null;
          if(part==null) { Log.Write("vShade","reinforcement part skipped corner={0}: no closed finished-size outline",placement.Sequence); continue; }
          var border=borders.OrderBy(curve=>curve.ClosestPoint(placement.Geometry.OffsetEnd,out var parameter)?curve.PointAt(parameter).DistanceTo(placement.Geometry.OffsetEnd):double.MaxValue).FirstOrDefault();
          if(border==null) continue;
          double gap=ReinforcementPartGapInches*RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem);
          if(!double.IsFinite(gap)||gap<=0) gap=ReinforcementPartGapInches;
          var translation=ReinforcementPartTranslation(part,border,plane,-placement.Geometry.InteriorDirection,gap,tolerance);
          part.Transform(translation);
          using var attributes=CreateIdentifiedAttributes(UzipCommon.EnsureLayer(doc,_cutLayer),ReinforcementPartObjectName,
            "reinforcement_part",sessionId,placement.Sequence,placement.FirstSourceId,placement.SecondSourceId);
          var id=doc.Objects.AddCurve(part,attributes);
          if(id==Guid.Empty) throw new InvalidOperationException("could not add detached reinforcement part");
          outputIds.Add(id); var groupIds=new List<Guid> {id};
          var sourceLabel=originalObjects.FirstOrDefault(obj=>obj.Attributes.GetUserString(MetadataPrefix+"role")=="label"&&
            obj.Attributes.GetUserString(MetadataPrefix+"sequence")==sequence);
          if(sourceLabel?.Geometry is TextEntity sourceText)
          {
            using var text=(TextEntity)sourceText.Duplicate(); text.Transform(translation);
            using var labelAttributes=sourceLabel.Attributes.Duplicate(); labelAttributes.RemoveFromAllGroups();
            labelAttributes.Name=ReinforcementPartLabelName; labelAttributes.SetUserString(MetadataPrefix+"role","reinforcement_part_label");
            var labelId=doc.Objects.AddText(text,labelAttributes);
            if(labelId==Guid.Empty) throw new InvalidOperationException("could not add detached reinforcement label");
            outputIds.Add(labelId); groupIds.Add(labelId);
          }
          var name=ReinforcementPartGroupPrefix+sessionId.ToString("N")+"_"+sequence;
          int group=doc.Groups.FindName(name)?.Index??doc.Groups.Add(name);
          if(group<0||!doc.Groups.AddToGroup(group,groupIds)) throw new InvalidOperationException("could not group detached reinforcement part");
          Log.Write("vShade","reinforcement part corner={0} clearance={1:G17} {2} layer={3}",placement.Sequence,gap,doc.ModelUnitSystem,_cutLayer);
        }
      }
      finally { foreach(var curve in finished) curve.Dispose(); foreach(var curve in finishedPieces) curve.Dispose(); }
    }

    private static Curve? ReinforcementPartOutlineFromBoundary(Curve arc,Line cap,IReadOnlyList<Curve> boundaries,double tolerance)
    {
      foreach(var boundary in boundaries.Where(curve=>curve.IsClosed))
      {
        if(!boundary.ClosestPoint(arc.PointAtStart,out var first)||!boundary.ClosestPoint(arc.PointAtEnd,out var second)||
          boundary.PointAt(first).DistanceTo(arc.PointAtStart)>tolerance||boundary.PointAt(second).DistanceTo(arc.PointAtEnd)>tolerance)
          continue;
        var sides=boundary.Split(new[]{first,second});
        if(sides==null) continue;
        try
        {
          var side=sides.Where(curve=>curve.ClosestPoint(cap.PointAt(0.5),out var parameter)&&
            curve.PointAt(parameter).DistanceTo(cap.PointAt(0.5))<=tolerance).OrderBy(curve=>curve.GetLength()).FirstOrDefault();
          if(side==null) continue;
          var joined=Curve.JoinCurves(new[]{side,arc},tolerance,false);
          var outline=joined.FirstOrDefault(curve=>curve.IsClosed);
          foreach(var curve in joined) if(!ReferenceEquals(curve,outline)) curve.Dispose();
          if(outline!=null) return outline;
        }
        finally { foreach(var side in sides) side.Dispose(); }
      }
      return null;
    }

    private static Curve? ReinforcementPartOutline(Curve arc,Line cap,ShadeConnectionGeometry first,ShadeConnectionGeometry second,int sequence,double tolerance)
    {
      using var firstSide=TrimReinforcementSide(first,sequence,arc.PointAtStart);
      using var secondSide=TrimReinforcementSide(second,sequence,arc.PointAtEnd);
      if(firstSide==null||secondSide==null) return null;
      using var capCurve=new LineCurve(cap);
      var joined=Curve.JoinCurves(new[]{firstSide,capCurve,secondSide,arc},tolerance,false);
      var result=joined.FirstOrDefault(curve=>curve.IsClosed);
      foreach(var curve in joined) if(!ReferenceEquals(curve,result)) curve.Dispose();
      return result;
    }

    private static Curve? TrimReinforcementSide(ShadeConnectionGeometry side,int sequence,Point3d arcEnd)
    {
      if(!side.Curve.ClosestPoint(arcEnd,out var parameter)) return null;
      double corner=side.Connection.FromSequence==sequence?side.Curve.Domain.T0:side.Curve.Domain.T1;
      return side.Curve.Trim(Math.Min(parameter,corner),Math.Max(parameter,corner));
    }

    private static Transform ReinforcementPartTranslation(Curve part,Curve border,Plane plane,Vector3d outward,double gap,double tolerance)
    {
      outward-=plane.Normal*(outward*plane.Normal);
      if(!outward.Unitize()) throw new InvalidOperationException("invalid reinforcement outward direction");
      var frame=new Plane(plane.Origin,outward,Vector3d.CrossProduct(plane.Normal,outward));
      var partBounds=part.GetBoundingBox(frame); var borderBounds=border.GetBoundingBox(frame);
      double low=0,high=Math.Max(0,borderBounds.Max.X-partBounds.Min.X)+gap+tolerance;
      bool HasGap(double distance)
      {
        using var moved=part.DuplicateCurve(); moved.Transform(Transform.Translation(outward*distance));
        return Curve.PlanarClosedCurveRelationship(moved,border,plane,tolerance)==RegionContainment.Disjoint&&
          moved.ClosestPoints(border,out var a,out var b)&&a.DistanceTo(b)>=gap;
      }
      if(!HasGap(high)) throw new InvalidOperationException("could not place reinforcement outside the cut outline");
      for(int pass=0;pass<ReinforcementPartPlacementPasses;pass++)
      { double middle=(low+high)/2; if(HasGap(middle)) high=middle; else low=middle; }
      return Transform.Translation(outward*high);
    }
  }
}
