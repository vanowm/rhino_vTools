namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Drawing;
  using System.Globalization;
  using System.Linq;
  using System.Text.Json;
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
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultOffset", "Signed bisector offset in model units: positive inward, negative outward, zero at the corner.", OptionKeys = "offset")] private const double DefaultOffset = 4.0; // Positive inward, negative outward, zero at the corner; model units.

    private static readonly double ConfiguredDefaultOffset = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultOffset", DefaultOffset); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultChamfer", "Perpendicular chamfer width in model units; zero or greater.", OptionKeys = "chamfer")] private const double DefaultChamfer = 1.0; // Perpendicular chamfer width in model units; zero or greater.

    private static readonly double ConfiguredDefaultChamfer = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultChamfer", DefaultChamfer); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultReinforcement", "Arc radius in model units; zero disables reinforcement arcs.", OptionKeys = "reinforcement")] private const double DefaultReinforcement = 6.0; // Arc radius in model units; zero disables reinforcement arcs.

    private static readonly double ConfiguredDefaultReinforcement = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultReinforcement", DefaultReinforcement); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultScallop", "true replaces perimeter connector lines with inward arcs; false keeps straight connectors.", OptionKeys = "scallop")] private const bool DefaultScallop = false; // true replaces perimeter connector lines with inward arcs; false keeps straight connectors.

    private static readonly bool ConfiguredDefaultScallop = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultScallop", DefaultScallop); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultUseCurve", "true trims nonlinear source sides in place and extends caps to meet them; false generates straight/scalloped connectors. Linear sources always use Scallop.", OptionKeys = "useCurve")] private const bool DefaultUseCurve = true; // true trims nonlinear source sides in place and extends caps to meet them; false generates straight/scalloped connectors. Linear sources always use Scallop.

    private static readonly bool ConfiguredDefaultUseCurve = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultUseCurve", DefaultUseCurve); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.SourceCapExtensionFactor", "Positive multiplier on source bounding-box distance when intersecting the infinite corner-cap line.")] private const double SourceCapExtensionFactor = 2.0; // Positive multiplier on source bounding-box distance when intersecting the infinite corner-cap line.

    private static readonly double ConfiguredSourceCapExtensionFactor = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.SourceCapExtensionFactor", SourceCapExtensionFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultScallopSize", "Positive midpoint bulge: model units, or percent of connector span when DefaultScallopPercent is true.", OptionKeys = "scallopSize|tuneScallopSize")] private const double DefaultScallopSize = 5.0; // Positive midpoint bulge: model units, or percent of connector span when DefaultScallopPercent is true.

    private static readonly double ConfiguredDefaultScallopSize = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultScallopSize", DefaultScallopSize); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultScallopPercent", "true interprets scallop size as percent of each connector span; false uses model units.", OptionKeys = "scallopPercent|tuneScallopPercent")] private const bool DefaultScallopPercent = true; // true interprets scallop size as percent of each connector span; false uses model units.

    private static readonly bool ConfiguredDefaultScallopPercent = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultScallopPercent", DefaultScallopPercent); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultCutOffset", "Outward cut distance in model units; zero omits the cut curve.", OptionKeys = "cutOffset")] private const double DefaultCutOffset = 1.5; // Outward cut distance in model units; zero omits the cut curve.

    private static readonly double ConfiguredDefaultCutOffset = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultCutOffset", DefaultCutOffset); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultCutLayer", "Existing or new Rhino layer path for the outward cut curve.", OptionKeys = "cutLayer")] private const string DefaultCutLayer = "CUT1"; // Existing or new Rhino layer path for the outward cut curve.

    private static readonly string ConfiguredDefaultCutLayer = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultCutLayer", DefaultCutLayer); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultCreateReinf", "true creates detached finished-size reinforcement parts; false keeps reinforcement arcs only on the shade.", OptionKeys = "createReinf")] private const bool DefaultCreateReinf = false; // true creates detached finished-size reinforcement parts; false keeps reinforcement arcs only on the shade.

    private static readonly bool ConfiguredDefaultCreateReinf = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultCreateReinf", DefaultCreateReinf); // Resolved once from advanced defaults; restart Rhino after changes.
    private const double ReinforcementPartGapInches = 2.0; // Minimum physical clearance in inches between each detached part and the outer cut outline.
    private const int ReinforcementPartPlacementPasses = 24; // Binary-search passes for outward placement at the requested clearance; positive integer.
    private const string ReinforcementPartObjectName = "ShadeReinforcementPart"; // Object name for detached finished-size reinforcement cutting outlines.
    private const string ReinforcementPartLabelName = "ShadeReinforcementPartLabel"; // Object name for detached copies of corner-identifying labels.
    private const string ReinforcementPartGroupPrefix = "vShade_Reinf_"; // Separate output group prefix followed by session and corner identifiers.
    private const int ReinforcementPoseSamples = 16; // Evenly spaced closed-outline sample count used to recover rigid placement; at least three.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.TunePickRadiusPixels", "Maximum screen-space distance in pixels for picking a connector or corner.")] private const double TunePickRadiusPixels = 14.0; // Maximum screen-space distance in pixels for picking a connector or corner.

    private static readonly double ConfiguredTunePickRadiusPixels = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.TunePickRadiusPixels", TunePickRadiusPixels); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultLabelHeight", "Reinforcement label height in model units; positive.")] private const double DefaultLabelHeight = 0.5; // Reinforcement label height in model units; positive.

    private static readonly double ConfiguredDefaultLabelHeight = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultLabelHeight", DefaultLabelHeight); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultLabelDimensionScale", "Positive annotation display scale for fixed-height labels.")] private const double DefaultLabelDimensionScale = 1.0; // Positive annotation display scale for fixed-height labels.

    private static readonly double ConfiguredDefaultLabelDimensionScale = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultLabelDimensionScale", DefaultLabelDimensionScale); // Resolved once from advanced defaults; restart Rhino after changes.
    private const double LabelRadialPosition = 0.5; // Fraction from cap midpoint toward reinforcement arc midpoint; zero to one.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultLabel", "Document-local next label; numeric or alphabetic suffix advances after each corner, empty disables labels.")] private const string DefaultLabel = "1"; // Document-local next label; numeric or alphabetic suffix advances after each corner, empty disables labels.

    private static readonly string ConfiguredDefaultLabel = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultLabel", DefaultLabel); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultLabelPrefix", "Literal text prepended to new corner labels; empty leaves the incrementing value unprefixed.", OptionKeys = "labelPrefix")] private const string DefaultLabelPrefix = ""; // Literal text prepended to new corner labels; empty leaves the incrementing value unprefixed.

    private static readonly string ConfiguredDefaultLabelPrefix = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultLabelPrefix", DefaultLabelPrefix); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultConnect", "true grows non-crossing boundaries between consecutive perpendiculars; false leaves perpendiculars separate.", OptionKeys = "connect")] private const bool DefaultConnect = true; // true grows non-crossing boundaries between consecutive perpendiculars; false leaves perpendiculars separate.

    private static readonly bool ConfiguredDefaultConnect = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultConnect", DefaultConnect); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultJoin", "true outputs each connected boundary as one curve; false keeps its segments separate.", OptionKeys = "join")] private const bool DefaultJoin = true; // true outputs each connected boundary as one curve; false keeps its segments separate.

    private static readonly bool ConfiguredDefaultJoin = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultJoin", DefaultJoin); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultLayer", "Rhino layer path or the shared current-layer sentinel.", OptionKeys = "finishedLayer")] private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Rhino layer name or full path, with the current-layer sentinel supported.

    private static readonly string ConfiguredDefaultLayer = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultLayer", DefaultLayer); // Resolved once from advanced defaults; restart Rhino after changes.
    private const string ReferenceLayerName = "Reference"; // Rhino layer name used for every bisector line.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.ReferenceLayerColor", "RGB color assigned when the Reference layer must be created.", "#FFFFFFFF")] private static readonly Color ReferenceLayerColor = global::vTools.CommandDefaults.GetColor("vShade", "vTools.Commands.vShade.ReferenceLayerColor", Color.White); // RGB color assigned when the Reference layer must be created.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.EndpointCoincidenceToleranceFactor", "Model absolute-tolerance multiplier used to treat clicked endpoints as one corner.")] private const double EndpointCoincidenceToleranceFactor = 4.0; // Model absolute-tolerance multiplier used to treat clicked endpoints as one corner.

    private static readonly double ConfiguredEndpointCoincidenceToleranceFactor = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.EndpointCoincidenceToleranceFactor", EndpointCoincidenceToleranceFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DirectionToleranceFactor", "Rhino zero-tolerance multiplier used for projected directions and intersection tests.")] private const double DirectionToleranceFactor = 10.0; // Rhino zero-tolerance multiplier used for projected directions and intersection tests.

    private static readonly double ConfiguredDirectionToleranceFactor = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DirectionToleranceFactor", DirectionToleranceFactor); // Resolved once from advanced defaults; restart Rhino after changes.
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
    private const string ReinforcementPoseKey = MetadataPrefix + "reinforcement_pose"; // JSON model-coordinate samples of the generated reinforcement outline before placement.
    private const string ShadeGroupPrefix = "vShade_"; // Prefix for the output group name; the session GUID follows it.
    private const int OutputDrawOrderStep = 1; // Positive draw-order increment placing new shade output above existing document objects; never changes geometry or Z.
    [global::vTools.CommandDefault("vShade", "vTools.Commands.vShade.DefaultSplitWidth", "Initial material width in model units for the shared part-split workflow; must exceed the total cut overlap.", OptionKeys = "splitWidth")] private const double DefaultSplitWidth = 63.0; // Initial shared split width in model units, greater than the total cut overlap.

    private static readonly double ConfiguredDefaultSplitWidth = global::vTools.CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultSplitWidth", DefaultSplitWidth); // Resolved once from advanced defaults; restart Rhino after changes.
    [CommandDefault("vShade", "vTools.Commands.vShade.DefaultSplitSeamType", "Regular overlaps by twice Seam; Overlap overlaps by Seam and puts each sew line at the opposite cut edge.", OptionKeys = "splitSeamType")]
    private const PartSplitWorkflow.SeamType DefaultSplitSeamType = PartSplitWorkflow.SeamType.Overlap; // Regular or Overlap; independent of vPartSplit.
    private static readonly PartSplitWorkflow.SeamType ConfiguredDefaultSplitSeamType = CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultSplitSeamType", DefaultSplitSeamType); // Initial shade seam type until saved.
    [CommandChoicesAttribute("vShade", "splitSeamType", false, "Regular", "Overlap")]
    private const string SplitSeamTypeKey = "splitSeamType"; // Stored seam-type name for shade splits.
    [CommandDefault("vShade", "vTools.Commands.vShade.DefaultShadeMaterial", "vNest material preset for the main shade. Split pieces inherit it. Choose None to leave new shades unassigned.", OptionKeys = ShadeMaterialKey)]
    private const string DefaultShadeMaterial = "Paper"; // Name of an existing vNest material preset; blank leaves the part unassigned.
    private static readonly string ConfiguredDefaultShadeMaterial = CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultShadeMaterial", DefaultShadeMaterial); // Fallback when no shade material preference is saved.
    [CommandDefault("vShade", "vTools.Commands.vShade.DefaultReinforcementMaterial", "vNest material preset for detached reinforcement parts. Choose None to leave new reinforcements unassigned.", OptionKeys = ReinforcementMaterialKey)]
    private const string DefaultReinforcementMaterial = "Pink"; // Name of an existing vNest material preset; blank leaves the part unassigned.
    private static readonly string ConfiguredDefaultReinforcementMaterial = CommandDefaults.Get("vShade", "vTools.Commands.vShade.DefaultReinforcementMaterial", DefaultReinforcementMaterial); // Fallback when no reinforcement material preference is saved.

    private const string OptionsSectionName = "vShade";
    internal const string ShadeMaterialKey = "shadeMaterial"; // Saved name of the vNest preset for the main shade.
    internal const string ReinforcementMaterialKey = "reinforcementMaterial"; // Saved name of the vNest preset for detached reinforcements.
    private const string OffsetKey = "offset";
    private const string ChamferKey = "chamfer";
    private const string LegacyLengthKey = "length";
    private const string ReinforcementKey = "reinforcement";
    private const string ScallopKey = "scallop";
    private const string UseCurveKey = "useCurve";
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
    private const string LabelPrefixKey = "labelPrefix";
    private const string SplitWidthKey = "splitWidth";

    private static double _offset = ConfiguredDefaultOffset;
    private static double _chamfer = ConfiguredDefaultChamfer;
    private static double _reinforcement = ConfiguredDefaultReinforcement;
    private static bool _scallop = ConfiguredDefaultScallop;
    private static bool _useCurve = ConfiguredDefaultUseCurve;
    private static string _labelPrefix = ConfiguredDefaultLabelPrefix;
    private static double _scallopSize = ConfiguredDefaultScallopSize;
    private static bool _scallopPercent = ConfiguredDefaultScallopPercent;
    private static double _cutOffset = ConfiguredDefaultCutOffset;
    private static string _cutLayer = ConfiguredDefaultCutLayer;
    private static bool _createReinf = ConfiguredDefaultCreateReinf;
    private static bool _connect = ConfiguredDefaultConnect;
    private static bool _join = ConfiguredDefaultJoin;
    private static string _layer = ConfiguredDefaultLayer;
    private static double _splitWidth = ConfiguredDefaultSplitWidth;
    private static PartSplitWorkflow.SeamType _splitSeamType = ConfiguredDefaultSplitSeamType;
    private static MaterialLibrary.MaterialPreset? _shadeMaterial;
    private static MaterialLibrary.MaterialPreset? _reinforcementMaterial;
    private static ScallopSize _tuneScallopSize = new(ConfiguredDefaultScallopSize, ConfiguredDefaultScallopPercent); // Per-command click-to-apply scallop bulge; zero selects a line.

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
      Point3d SecondCapEnd)
    {
      internal double SignedOffset => Vector3d.Multiply(OffsetEnd - Corner, InteriorDirection);

      internal ShadeGeometry WithOffset(double offset)
      {
        var end = Corner + InteriorDirection * offset;
        var shift = end - OffsetEnd;
        return this with
        {
          OffsetEnd = end,
          OffsetLine = offset != 0.0 ? new Line(Corner, end) : null,
          PerpendicularLine = PerpendicularLine is { } cap ? new Line(cap.From + shift, cap.To + shift) : null,
          FirstCapEnd = PerpendicularLine.HasValue ? FirstCapEnd + shift : Point3d.Unset,
          SecondCapEnd = PerpendicularLine.HasValue ? SecondCapEnd + shift : Point3d.Unset
        };
      }
    }

    private readonly record struct ShadeOptionState(
      OptionDouble Offset,
      OptionDouble Chamfer,
      OptionDouble Reinforcement,
      OptionToggle CreateReinf,
      OptionToggle Scallop,
      OptionToggle UseCurve,
      int ScallopSizeOptionIndex,
      OptionToggle Connect,
      OptionToggle Join,
      int LayerOptionIndex,
      int LabelOptionIndex,
      int LabelPrefixOptionIndex,
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
      string NextLabel)
    {
      public string LabelPrefix { get; init; } = ConfiguredDefaultLabelPrefix;
    }

    private sealed record ShadeHistoryState(
      List<ShadePlacement> Placements,
      Dictionary<(int From, int To), ScallopSize> ScallopSizes,
      string NextLabel);

    private sealed record ShadeHistoryRequest(bool Redo);

    private sealed record ShadeConnection(Line Line, int FromSequence, int ToSequence);

    private sealed record ShadeConnectionGeometry(ShadeConnection Connection, Curve Curve);

    private sealed class ShadeSources : IDisposable
    {
      internal Dictionary<Guid, Curve> Curves { get; } = [];
      internal List<Curve> Owned { get; } = [];
      public void Dispose() { foreach (var curve in Owned) curve.Dispose(); }
    }

    private readonly record struct ScallopSize(double Value, bool Percent);

    private sealed class ShadeSnapshot
    {
      public ShadeSnapshot() { }

      public List<PlacementSnapshot> Placements { get; set; } = [];
      public List<ScallopSnapshot> Scallops { get; set; } = [];
      public List<Guid>? OutputIds { get; set; }
      public bool Scallop { get; set; }
      public bool UseCurve { get; set; } = ConfiguredDefaultUseCurve;
      public string LabelPrefix { get; set; } = ConfiguredDefaultLabelPrefix;
      public double ScallopSize { get; set; }
      public bool ScallopPercent { get; set; }
      public bool Join { get; set; }
      public double CutOffset { get; set; }
      public string CutLayer { get; set; } = ConfiguredDefaultCutLayer;
      public string LayerOption { get; set; } = string.Empty;
      public bool CreateReinf { get; set; } = ConfiguredDefaultCreateReinf;
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
      public string LabelPrefix { get; set; } = ConfiguredDefaultLabelPrefix;
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
      var labelValue = doc.Strings.GetValue(OptionsSectionName, LabelKey) ?? ConfiguredDefaultLabel;
      var selectedObjects = doc.Objects.GetSelectedObjects(false, false).ToArray();
      var selectedShadeIds = selectedObjects
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
        if (!selectedObjects.Any(obj => outputIds.Contains(obj.Id)))
        {
          RhinoApp.WriteLine("vShade: select the original shade, not a copy, to continue it.");
          return Result.Failure;
        }
        var last = placements[^1];
        _offset = last.Geometry.SignedOffset;
        _chamfer = last.Geometry.PerpendicularLine?.Length ?? 0.0;
        _reinforcement = last.Reinforcement;
        _connect = last.Connect;
        _layer = string.IsNullOrEmpty(restoredLayerOption)
          ? doc.Layers[last.OutputLayerIndex]?.FullPath ?? ConfiguredDefaultLayer
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
            placements,
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
                obj.Attributes.GetUserString(MetadataPrefix + "role") is "bisector" or "cut" or "reinforcement_part" or "reinforcement_part_label" ||
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
            SaveShadeState(doc, sessionId, placements.Take(historyPosition).ToList(), scallopSizes, outputIds);
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
        splitCompleted = RunSplit(doc, outputIds, undoRecords.BeginSplit);
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
                : IncrementLabelValue(labelValue)))
          { LabelPrefix = previousCorner?.LabelPrefix ?? _labelPrefix };
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
          placement = placements[existingCornerIndex >= 0 ? existingCornerIndex : historyPosition - 1];
          geometry = placement.Geometry;
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
          var previousUseCurve = _useCurve;
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
               previousUseCurve != _useCurve ||
               previousScallopSize != _scallopSize ||
               previousScallopPercent != _scallopPercent ||
               previousCutOffset != _cutOffset ||
               previousCutLayer != _cutLayer) &&
              !refreshBoundaryOutput())
          {
            _join = previousJoin;
            _scallop = previousScallop;
            _useCurve = previousUseCurve;
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
          if (!double.IsFinite(offset))
            RhinoApp.WriteLine("vShade: Offset must be a finite number.");
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
      var directionTolerance = RhinoMath.ZeroTolerance * ConfiguredDirectionToleranceFactor;

      if (!Unitize(ref d1, directionTolerance) || !Unitize(ref d2, directionTolerance))
      {
        failure = "a clicked curve tangent is perpendicular to the active CPlane.";
        return false;
      }

      var endpointTolerance = Math.Max(
        doc.ModelAbsoluteTolerance * ConfiguredEndpointCoincidenceToleranceFactor,
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

      var offsetLine = _offset != 0.0
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
        // Keep the same source side at each cap end when the offset crosses the corner.
        bool firstIsFrom = Vector2d.Multiply(d1, new Vector2d(-bisector2d.Y, bisector2d.X)) <= 0.0;
        firstCapEnd = firstIsFrom ? cap.From : cap.To;
        secondCapEnd = firstIsFrom ? cap.To : cap.From;
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
        doc.ModelAbsoluteTolerance * ConfiguredEndpointCoincidenceToleranceFactor,
        RhinoMath.ZeroTolerance * ConfiguredDirectionToleranceFactor);
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
      List<ShadePlacement> inputPlacements,
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
        var reinforcementPoses = _createReinf
          ? CaptureReinforcementPoses(doc, sessionId, inputPlacements, existingOutputIds, tolerance)
          : null;
        var (orientedPlacements, placements, connections) = BuildShadeBoundary(
          doc, cplane, inputPlacements, scallopSizes, tolerance, connectionGeometry);
        chainCount = CountConnectedChains(connections);

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
          AddReinforcementParts(doc,cplane,placements,connectionGeometry,sessionId,tolerance,newOutputIds,reinforcementPoses);

        var groupName = ShadeGroupPrefix + sessionId.ToString("N");
        var groupedIds = newOutputIds.Where(id =>
          doc.Objects.FindId(id)?.Attributes.GetUserString(MetadataPrefix + "role") is not ("bisector" or "reinforcement_part" or "reinforcement_part_label")).ToArray();
        if (groupedIds.Length > 0)
        {
          var groupIndex = doc.Groups.FindName(groupName)?.Index ?? doc.Groups.Add(groupName);
          if (groupIndex < 0 || !doc.Groups.AddToGroup(groupIndex, groupedIds))
            throw new InvalidOperationException("could not group shade output");
          if(_shadeMaterial!=null&&FindSplitSource(doc,groupedIds)!=null)
            NestWorkflow.AssignInitialPartMaterial(doc,groupIndex,_shadeMaterial);
        }

        BringOutputForward(doc,newOutputIds);
        SaveShadeState(doc, sessionId, orientedPlacements, scallopSizes, newOutputIds);
        foreach (var id in existingOutputIds)
          doc.Objects.Delete(id, quiet: true);
        existingOutputIds.Clear();
        existingOutputIds.AddRange(newOutputIds);
        for (int index = 0; index < inputPlacements.Count; index++)
          inputPlacements[index] = orientedPlacements[index];
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

    private static void BringOutputForward(RhinoDoc doc,IEnumerable<Guid> outputIds)
    {
      var ids=outputIds.ToHashSet();
      if(ids.Count==0)return;
      int maximum=doc.Objects.Where(obj=>!ids.Contains(obj.Id)).Select(obj=>obj.Attributes.DisplayOrder).DefaultIfEmpty(0).Max();
      int order=maximum>=int.MaxValue-OutputDrawOrderStep?int.MaxValue:Math.Max(OutputDrawOrderStep,maximum+OutputDrawOrderStep);
      foreach(var id in ids)
      {
        if(doc.Objects.FindId(id) is not {} obj)throw new InvalidOperationException("a new shade object is missing");
        using var attributes=obj.Attributes.Duplicate();attributes.DisplayOrder=order;
        if(!doc.Objects.ModifyAttributes(id,attributes,true))throw new InvalidOperationException("could not bring new shade output forward");
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
      IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes,
      IReadOnlyList<Guid> outputIds)
    {
      var snapshot = new ShadeSnapshot
      {
        OutputIds = outputIds.ToList(),
        Scallop = _scallop,
        UseCurve = _useCurve,
        LabelPrefix = _labelPrefix,
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
          LabelPrefix = placement.LabelPrefix,
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
            saved.NextLabel) { LabelPrefix = saved.LabelPrefix });
        }
        foreach (var saved in snapshot.Scallops)
          scallopSizes[(saved.FromSequence, saved.ToSequence)] =
            new ScallopSize(saved.Value, saved.Percent);
        _scallop = snapshot.Scallop;
        _useCurve = snapshot.UseCurve;
        _labelPrefix = snapshot.LabelPrefix;
        _scallopSize = snapshot.ScallopSize;
        _scallopPercent = snapshot.ScallopPercent;
        _join = snapshot.Join;
        _cutOffset = snapshot.CutOffset;
        _cutLayer = snapshot.CutLayer;
        _createReinf = snapshot.CreateReinf;
        layerOption = snapshot.LayerOption;

        if (snapshot.OutputIds is { } savedIds)
          outputIds.AddRange(savedIds.Distinct().Where(id =>
            doc.Objects.FindId(id) is { } obj && BelongsToShadeSession(obj, sessionId)));
        else
          outputIds.AddRange(FindLegacyShadeOutput(doc, sessionId, placements));
        return outputIds.Count > 0;
      }
      catch (Exception ex)
      {
        Log.Write("vShade", $"restore failed session={sessionId}: {ex}");
        return false;
      }
    }

    private static bool BelongsToShadeSession(RhinoObject obj, Guid sessionId) =>
      Guid.TryParse(obj.Attributes.GetUserString(MetadataPrefix + "session"), out var id) && id == sessionId;

    private static List<Guid> FindLegacyShadeOutput(RhinoDoc doc, Guid sessionId, IReadOnlyList<ShadePlacement> placements)
    {
      var session = sessionId.ToString("N");
      var rootGroup = doc.Groups.FindName(ShadeGroupPrefix + session)?.Index ?? -1;
      var reinforcementGroups = placements.ToDictionary(placement => placement.Sequence, placement =>
        doc.Groups.FindName(ReinforcementPartGroupPrefix + session + "_" + placement.Sequence.ToString(CultureInfo.InvariantCulture))?.Index ?? -1);
      double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      var settings = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true };
      // Legacy copies inherit the session strings. Restrict ownership to the original groups and exact corner guides.
      var candidates = doc.Objects.GetObjectList(settings)
        .Where(obj => BelongsToShadeSession(obj, sessionId) && obj.Attributes.GetUserString("vPartSplit.session") == null)
        .Where(obj =>
        {
          var attributes = obj.Attributes;
          var role = attributes.GetUserString(MetadataPrefix + "role");
          var groups = attributes.GetGroupList() ?? [];
          if (!int.TryParse(attributes.GetUserString(MetadataPrefix + "sequence"), out int sequence) ||
              sequence < 1 || sequence > placements.Count) return false;
          if (role is "reinforcement_part" or "reinforcement_part_label")
            return reinforcementGroups[sequence] >= 0 && groups.Contains(reinforcementGroups[sequence]);
          if (role != "bisector") return rootGroup >= 0 && groups.Contains(rootGroup);
          return obj.Geometry is Curve curve && curve.IsLinear(tolerance) &&
            placements[sequence - 1].Geometry.OffsetLine is { } expected &&
            SameEndpoints(curve, expected.From, expected.To, tolerance);
        }).ToArray();
      var duplicates = candidates.GroupBy(obj => (
        Role: obj.Attributes.GetUserString(MetadataPrefix + "role"),
        Sequence: obj.Attributes.GetUserString(MetadataPrefix + "sequence"),
        From: obj.Attributes.GetUserString(MetadataPrefix + "from_sequence")))
        .Where(group => group.Skip(1).Any()).ToArray();
      if (duplicates.Length != 0)
      {
        RhinoApp.WriteLine("vShade: original and copied objects share shade ownership. Separate copies from the original groups before tuning.");
        throw new InvalidOperationException("legacy shade has ambiguous original output");
      }
      return candidates.Select(obj => obj.Id).ToList();
    }

    private static bool SameEndpoints(Curve curve, Point3d first, Point3d second, double tolerance) =>
      (curve.PointAtStart.DistanceTo(first) <= tolerance && curve.PointAtEnd.DistanceTo(second) <= tolerance) ||
      (curve.PointAtStart.DistanceTo(second) <= tolerance && curve.PointAtEnd.DistanceTo(first) <= tolerance);

    private static ShadeSources ResolveShadeSources(RhinoDoc doc, IReadOnlyList<ShadePlacement> placements, double tolerance)
    {
      var result = new ShadeSources();
      try
      {
        RhinoObject[]? live = null;
        double endpointTolerance = Math.Max(tolerance, tolerance * ConfiguredEndpointCoincidenceToleranceFactor);
        var anchors = placements.SelectMany(placement => new[] { placement.FirstSourceId, placement.SecondSourceId }
          .Where(id => id != Guid.Empty).Select(id => (Id: id, Corner: placement.Geometry.Corner))).GroupBy(value => value.Id);
        foreach (var group in anchors)
        {
          var points = group.Select(value => value.Corner).Distinct().ToArray();
          var current = doc.Objects.FindId(group.Key)?.Geometry as Curve;
          bool joined = current != null && points.Any(point => current.ClosestPoint(point, out _, endpointTolerance) &&
            point.DistanceTo(current.PointAtStart) > endpointTolerance && point.DistanceTo(current.PointAtEnd) > endpointTolerance);
          if (current != null && !joined)
          {
            result.Curves.Add(group.Key, current);
            continue;
          }
          if (points.Length < 2) continue;
          var first = points[0];
          var second = points.MaxBy(point => first.DistanceToSquared(point));
          if (first.DistanceTo(second) <= endpointTolerance) continue;
          Curve? Find(IEnumerable<Curve> candidates)
          {
            Curve? match = null;
            foreach (var candidate in candidates)
            {
              if (!candidate.ClosestPoint(first, out _, endpointTolerance) ||
                  !candidate.ClosestPoint(second, out _, endpointTolerance)) continue;
              var segments = candidate.DuplicateSegments();
              try
              {
                foreach (var segment in segments)
                {
                  if (!SameEndpoints(segment, first, second, endpointTolerance)) continue;
                  if (match != null)
                  {
                    match.Dispose();
                    return null;
                  }
                  match = segment.DuplicateCurve();
                }
              }
              finally { foreach (var segment in segments) segment.Dispose(); }
            }
            return match;
          }
          var recovered = current == null ? null : Find([current]);
          if (recovered == null)
          {
            live ??= doc.Objects.GetObjectList(ObjectType.Curve).Where(obj => !obj.IsDeleted &&
              obj.Attributes.GetUserString(MetadataPrefix + "session") == null).ToArray();
            recovered = Find(live.Where(obj => obj.Id != group.Key).Select(obj => obj.Geometry).OfType<Curve>());
          }
          if (recovered == null) continue;
          result.Owned.Add(recovered);
          result.Curves.Add(group.Key, recovered);
          Log.Write("vShade", $"recovered joined source side {group.Key}");
        }
        return result;
      }
      catch
      {
        result.Dispose();
        throw;
      }
    }

    private static (IReadOnlyList<ShadePlacement> Oriented, IReadOnlyList<ShadePlacement> Capped, List<ShadeConnection> Connections)
      BuildShadeBoundary(RhinoDoc doc, Plane cplane, IReadOnlyList<ShadePlacement> input,
        IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes, double tolerance, List<ShadeConnectionGeometry> geometry)
    {
      using var sources = ResolveShadeSources(doc, input, tolerance);
      var oriented = OrientCornerOffsets(cplane, input, sources.Curves, tolerance);
      var capped = PrepareSourceCaps(sources.Curves, oriented, scallopSizes, tolerance);
      var connections = BuildConnections(cplane, capped, tolerance);
      var sides = ClosedPerimeterSides(cplane, connections, tolerance);
      foreach (var connection in connections)
        geometry.Add(new ShadeConnectionGeometry(connection,
          CreateConnectionCurve(sources.Curves, cplane, connection, capped, scallopSizes, sides, tolerance)));
      if (geometry.Any(item => item.Curve is not LineCurve))
        ValidateScallopPerimeter(capped, geometry, tolerance);
      return (oriented, capped, connections);
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
        var connectors = new List<ShadeConnectionGeometry>();
        try
        {
          try
          {
            BuildShadeBoundary(doc, cplane, placements, scallopSizes, tolerance, connectors);
          }
          catch (Exception ex)
          {
            Log.Write("vShade", $"scallop tuning could not prepare the shade: {ex}");
            RhinoApp.WriteLine($"vShade: cannot tune these sides: {ex.Message}. The shade is unchanged.");
            return;
          }
          if (connectors.Count == 0)
          {
            RhinoApp.WriteLine("vShade: there are no connected perimeter segments to tune.");
            return;
          }
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
          var offsetOption = new OptionDouble(_offset);
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
            if (double.IsFinite(offsetOption.CurrentValue))
            {
              _offset = offsetOption.CurrentValue;
              SaveOptions();
            }
            continue;
          }
          if (getResult == GetResult.Number)
          {
            var value = getter.Number();
            if (!double.IsFinite(value))
              RhinoApp.WriteLine("vShade: Offset must be a finite number.");
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
          var moved = geometry.WithOffset(_offset);
          if (geometry.OffsetEnd.DistanceTo(moved.OffsetEnd) <= RhinoMath.ZeroTolerance)
            continue;
          placements[placementIndex] = previous with { Geometry = moved };
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

      var nearest = ConfiguredTunePickRadiusPixels * ConfiguredTunePickRadiusPixels;
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
      double tolerance,
      bool useCornerChords = false)
    {
      var active = placements
        .Where(placement => useCornerChords || placement.Connect && placement.Geometry.PerpendicularLine.HasValue)
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
            var firstPoint = useCornerChords ? first.Geometry.Corner : CapEnd(first, firstEnd);
            var secondPoint = useCornerChords ? second.Geometry.Corner : CapEnd(second, secondEnd);
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
            !useCornerChords && (EndpointAlreadyConnected(connections, candidate.Line.From, tolerance) ||
            EndpointAlreadyConnected(connections, candidate.Line.To, tolerance)) ||
            connections.Any(connection =>
              HasForbiddenIntersection(cplane, candidate.Line, connection.Line, tolerance)) ||
            !useCornerChords && active.Any(placement =>
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
        if (!useCornerChords) Log.Write(
          "vShade",
          $"connection {candidate.FromSequence}<->{candidate.ToSequence} " +
          $"sharedSource={candidate.SharedSource} from={FormatPoint(candidate.Line.From)} " +
          $"to={FormatPoint(candidate.Line.To)}");
      }

      if (!useCornerChords) Log.Write(
        "vShade",
        $"perimeter corners={active.Length} connections={connections.Count} " +
        $"closed={active.Length > 2 && connections.Count == active.Length}");
      return connections;
    }

    private static Dictionary<ShadeConnection, bool>? ClosedPerimeterSides(
      Plane cplane, IReadOnlyList<ShadeConnection> connections, double tolerance)
    {
      if (connections.Count < 3) return null;
      var incident = connections.SelectMany(edge => new[]
        { (edge.FromSequence, Edge: edge), (edge.ToSequence, Edge: edge) })
        .ToLookup(item => item.Item1, item => item.Edge);
      if (incident.Any(group => group.Count() != 2)) return null;

      // Walk the perimeter, including the cap between every two connectors, to find its winding.
      var traversal = new Dictionary<ShadeConnection, bool>();
      var edge = connections[0];
      int start = edge.FromSequence, corner = start;
      var origin = edge.Line.From;
      var last = Vector2d.Zero;
      double area = 0.0;
      do
      {
        bool forward = edge.FromSequence == corner;
        if (!traversal.TryAdd(edge, forward)) return null;
        var a = ProjectVector(cplane, (forward ? edge.Line.From : edge.Line.To) - origin);
        var b = ProjectVector(cplane, (forward ? edge.Line.To : edge.Line.From) - origin);
        area += Cross(last, a) + Cross(a, b);
        last = b;
        corner = forward ? edge.ToSequence : edge.FromSequence;
        edge = incident[corner].First(other => other != edge);
      } while (corner != start);
      if (traversal.Count != connections.Count || Math.Abs(area) <= tolerance * tolerance) return null;
      return traversal.ToDictionary(pair => pair.Key, pair => pair.Value == (area > 0.0));
    }

    private static Dictionary<int, Vector3d> SourceInwardDirections(
      Plane cplane, IReadOnlyList<ShadePlacement> placements, IReadOnlyDictionary<Guid, Curve> sources, double tolerance)
    {
      var result = new Dictionary<int, Vector3d>();
      if (sources.Count < 2 || placements.Any(p => !sources.ContainsKey(p.FirstSourceId) || !sources.ContainsKey(p.SecondSourceId)))
        return result;
      double cornerTolerance = Math.Max(tolerance, tolerance * ConfiguredEndpointCoincidenceToleranceFactor);
      var bridges = new List<Curve>();
      Curve[] joined = [];
      try
      {
        var usedEnds = new HashSet<(Guid Id, bool Start)>();
        (bool Start, Point3d Point) NearestEnd(Guid id, Point3d corner)
        {
          var curve = sources[id]!;
          bool start = curve.PointAtStart.DistanceToSquared(corner) <= curve.PointAtEnd.DistanceToSquared(corner);
          return (start, start ? curve.PointAtStart : curve.PointAtEnd);
        }
        foreach (var placement in placements)
        {
          var corner = placement.Geometry.Corner;
          var a = NearestEnd(placement.FirstSourceId, corner);
          var b = NearestEnd(placement.SecondSourceId, corner);
          if (!usedEnds.Add((placement.FirstSourceId, a.Start)) || !usedEnds.Add((placement.SecondSourceId, b.Start)))
            return result;
          if (a.Point.DistanceTo(b.Point) <= cornerTolerance) continue;

          // Bridge only explicitly picked pairs. The reference may use a tangent-defined corner in a gap.
          bridges.Add(corner.DistanceTo(a.Point) > cornerTolerance && corner.DistanceTo(b.Point) > cornerTolerance
            ? new PolylineCurve(new[] { a.Point, corner, b.Point })
            : new LineCurve(a.Point, b.Point));
        }
        joined = Curve.JoinCurves(sources.Values.OfType<Curve>().Concat(bridges), cornerTolerance);
        if (joined.Length != 1 || !joined[0].IsClosed) return result;
        var outline = joined[0];
        var orientation = outline.ClosedCurveOrientation(cplane);
        if (orientation == CurveOrientation.Undefined) return result;

        // At a corner, either incident tangent's inward normal agrees with the inward bisector sign.
        foreach (var placement in placements)
        {
          var geometry = placement.Geometry;
          if (!outline.ClosestPoint(geometry.Corner, out double t, cornerTolerance)) continue;
          var inward = Vector3d.CrossProduct(cplane.Normal, outline.TangentAt(t));
          if (!inward.Unitize()) continue;
          if (orientation == CurveOrientation.Clockwise) inward = -inward;
          if (Math.Abs(Vector3d.Multiply(inward, geometry.InteriorDirection)) > RhinoMath.ZeroTolerance)
            result[placement.Sequence] = inward;
        }
        return result;
      }
      finally
      {
        foreach (var bridge in bridges) bridge.Dispose();
        foreach (var curve in joined) curve.Dispose();
      }
    }

    private static IReadOnlyList<ShadePlacement> OrientCornerOffsets(
      Plane cplane, IReadOnlyList<ShadePlacement> placements, IReadOnlyDictionary<Guid, Curve> sources, double tolerance)
    {
      if (placements.Count < 2) return placements;
      var sourceInward = SourceInwardDirections(cplane, placements, sources, tolerance);
      var inward = new Dictionary<int, Vector3d>();
      if (sourceInward.Count < placements.Count)
      {
        var chords = BuildConnections(cplane, placements, tolerance, useCornerChords: true);
        if (ClosedPerimeterSides(cplane, chords, tolerance) is {} sides)
          foreach (var (edge, onLeft) in sides)
          {
            var normal = Vector3d.CrossProduct(cplane.Normal, edge.Line.Direction);
            if (!normal.Unitize()) continue;
            if (!onLeft) normal = -normal;
            inward[edge.FromSequence] = inward.GetValueOrDefault(edge.FromSequence) + normal;
            inward[edge.ToSequence] = inward.GetValueOrDefault(edge.ToSequence) + normal;
          }
      }
      var origin = placements[0].Geometry.Corner;
      var middle = origin + placements.Aggregate(Vector3d.Zero, (sum, part) => sum + (part.Geometry.Corner - origin)) / placements.Count;
      return placements.Select(placement =>
      {
        var geometry = placement.Geometry;
        bool hasPerimeter = sourceInward.TryGetValue(placement.Sequence, out var direction) ||
          inward.TryGetValue(placement.Sequence, out direction);
        if (!hasPerimeter) direction = middle - geometry.Corner;
        var deadZone = hasPerimeter ? RhinoMath.ZeroTolerance : tolerance;
        if (Vector3d.Multiply(direction, geometry.InteriorDirection) >= -deadZone) return placement;
        var moved = (geometry with { InteriorDirection = -geometry.InteriorDirection }).WithOffset(geometry.SignedOffset);
        return placement with { Geometry = moved };
      }).ToArray();
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
      IReadOnlyDictionary<Guid, Curve> sources,
      Plane cplane,
      ShadeConnection connection,
      IReadOnlyList<ShadePlacement> placements,
      IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes,
      IReadOnlyDictionary<ShadeConnection, bool>? scallopSides,
      double tolerance)
    {
      var hasOverride = scallopSizes.TryGetValue(ConnectionKey(connection), out var overrideSize);
      var from = placements[connection.FromSequence - 1];
      var to = placements[connection.ToSequence - 1];
      if (_useCurve && !hasOverride)
      {
        var shared = new[] { from.FirstSourceId, from.SecondSourceId }
          .Where(id => id != Guid.Empty && (id == to.FirstSourceId || id == to.SecondSourceId)).Distinct().ToArray();
        if (shared.Length == 1 && sources.TryGetValue(shared[0], out var source) && !source.IsLinear(tolerance))
          return TrimScallopCurve(source, connection.Line, tolerance);
      }
      if ((hasOverride && overrideSize.Value <= 0.0) || (!hasOverride && !_scallop))
        return new LineCurve(connection.Line);

      var side = Vector3d.CrossProduct(cplane.Normal, connection.Line.Direction);
      if (!side.Unitize())
        throw new InvalidOperationException("scallop connector has no perpendicular direction");

      var midpoint = 0.5 * (connection.Line.From + connection.Line.To);
      if (scallopSides != null && scallopSides.TryGetValue(connection, out bool onLeft))
      {
        if (!onLeft) side = -side;
      }
      else
      {
        var a = from.Geometry.Corner;
        var b = to.Geometry.Corner;
        var referenceSide = Vector3d.CrossProduct(cplane.Normal, b - a);
        var orientation = Vector3d.Multiply(side, from.Geometry.InteriorDirection + to.Geometry.InteriorDirection);
        if (referenceSide.Unitize())
        {
          var inside = placements.Where(placement => placement.Connect && placement.Geometry.PerpendicularLine.HasValue &&
              placement.Sequence != connection.FromSequence && placement.Sequence != connection.ToSequence)
            .Select(placement => Vector3d.Multiply(referenceSide, placement.Geometry.Corner - a))
            .Where(distance => Math.Abs(distance) > tolerance).ToArray();
          double alignment = Vector3d.Multiply(side, referenceSide);
          if (Math.Abs(alignment) > RhinoMath.ZeroTolerance && inside.Length > 0 &&
              (inside.All(distance => distance > 0.0) || inside.All(distance => distance < 0.0)))
            orientation = alignment * Math.Sign(inside[0]);
        }
        if (Math.Abs(orientation) <= RhinoMath.ZeroTolerance)
          orientation = Vector3d.Multiply(side, from.Geometry.InteriorDirection);
        if (Math.Abs(orientation) <= RhinoMath.ZeroTolerance)
          orientation = Vector3d.Multiply(side, to.Geometry.InteriorDirection);
        if (Math.Abs(orientation) <= RhinoMath.ZeroTolerance)
          throw new InvalidOperationException("could not determine the inside of a scallop connector");
        if (orientation < 0.0) side = -side;
      }

      var size = hasOverride
        ? overrideSize
        : new ScallopSize(_scallopSize, _scallopPercent);
      var bulge = size.Percent
        ? connection.Line.Length * size.Value / 100.0
        : size.Value;
      if (!double.IsFinite(bulge) || bulge <= tolerance)
        throw new InvalidOperationException("ScallopSize is too small for a connector");
      var arc = new Arc(connection.Line.From, midpoint + side * bulge, connection.Line.To);
      if (!arc.IsValid)
        throw new InvalidOperationException("could not construct an inward scallop arc");
      return new ArcCurve(arc);
    }

    private static IReadOnlyList<ShadePlacement> PrepareSourceCaps(IReadOnlyDictionary<Guid, Curve> sources, IReadOnlyList<ShadePlacement> placements,
      IReadOnlyDictionary<(int From, int To), ScallopSize> scallopSizes, double tolerance)
    {
      if (!_useCurve) return placements;
      return placements.Select(placement =>
      {
        if (!placement.Connect || placement.Geometry.PerpendicularLine is not { } cap) return placement;
        var geometry = placement.Geometry;
        Point3d End(int end)
        {
          var id = SourceId(placement, end);
          bool used = placements.Any(other => other.Sequence != placement.Sequence && other.Connect &&
            other.Geometry.PerpendicularLine.HasValue && (id == other.FirstSourceId || id == other.SecondSourceId) &&
            !scallopSizes.ContainsKey((Math.Min(placement.Sequence, other.Sequence), Math.Max(placement.Sequence, other.Sequence))));
          if (!used || id == Guid.Empty)
            return CapEnd(placement, end);
          if (!sources.TryGetValue(id, out var source))
            throw new InvalidOperationException($"could not locate the original side at corner {placement.Sequence}; check the joined source curves or set UseCurve=No");
          if (source.IsLinear(tolerance))
            return CapEnd(placement, end);
          var center = cap.PointAt(0.5); var direction = cap.Direction;
          if (!direction.Unitize()) throw new InvalidOperationException("corner cap has no direction");
          double extent = source.GetBoundingBox(true).GetCorners().Max(point => point.DistanceTo(center)) * ConfiguredSourceCapExtensionFactor + tolerance;
          using var extended = new LineCurve(center - direction * extent, center + direction * extent);
          using var intersections = Intersection.CurveCurve(source, extended, tolerance, tolerance);
          var hits = intersections?.Where(hit => hit.IsPoint).Select(hit => hit.PointA)
            .OrderBy(point => point.DistanceToSquared(geometry.Corner)).ToArray() ?? [];
          if (hits.Length == 0) throw new InvalidOperationException("corner cap does not intersect the selected curved side; adjust Offset");
          return hits[0];
        }
        var first = End(0); var second = End(1);
        if (first.DistanceTo(geometry.FirstCapEnd) <= tolerance && second.DistanceTo(geometry.SecondCapEnd) <= tolerance) return placement;
        if (first.DistanceTo(second) <= tolerance) throw new InvalidOperationException("Offset leaves no corner cap between the selected sides");
        return placement with { Geometry = geometry with { FirstCapEnd = first, SecondCapEnd = second, PerpendicularLine = new Line(first, second) } };
      }).ToArray();
    }

    private static Curve TrimScallopCurve(Curve source, Line target, double tolerance)
    {
      if (!source.ClosestPoint(target.From, out double from) || !source.ClosestPoint(target.To, out double to) ||
          source.PointAt(from).DistanceTo(target.From) > tolerance || source.PointAt(to).DistanceTo(target.To) > tolerance)
        throw new InvalidOperationException("corner caps do not meet the selected curved side");
      var curve = source.Trim(Math.Min(from, to), Math.Max(from, to));
      if (curve == null) throw new InvalidOperationException("could not trim the selected curved side between its caps");
      if (from > to && !curve.Reverse())
      {
        curve.Dispose();
        throw new InvalidOperationException("could not orient the trimmed curved side");
      }
      return curve;
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
        PlainText = FormatCornerLabel(placement.LabelPrefix, placement.LabelText),
        TextHeight = ConfiguredDefaultLabelHeight,
        Justification = TextJustification.MiddleCenter,
        DimensionStyleId = doc.DimStyles.Current.Id,
        DimensionScale = ConfiguredDefaultLabelDimensionScale,
      };
      if (!AnnotationTextTransform.ApplyFixedDisplayTextHeight(
            doc,
            text,
            doc.Views.ActiveView?.ActiveViewport,
            ConfiguredDefaultLabelHeight,
            ConfiguredDefaultLabelDimensionScale))
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
      var offsetOption = new OptionDouble(_offset);
      var chamferOption = new OptionDouble(_chamfer, 0.0, double.MaxValue);
      var reinforcementOption = new OptionDouble(_reinforcement, 0.0, double.MaxValue);
      var createReinfOption = new OptionToggle(_createReinf,"No","Yes");
      var connectOption = new OptionToggle(_connect, "No", "Yes");
      var joinOption = new OptionToggle(_join, "No", "Yes");
      var scallopOption = new OptionToggle(_scallop, "No", "Yes");
      var useCurveOption = new OptionToggle(_useCurve, "No", "Yes");
      getter.AddOptionDouble("Offset", ref offsetOption);
      getter.AddOptionDouble("Chamfer", ref chamferOption);
      getter.AddOptionDouble("Reinforcement", ref reinforcementOption);
      getter.AddOptionToggle("CreateReinf", ref createReinfOption);
      getter.AddOptionToggle("Connect", ref connectOption);
      getter.AddOptionToggle("Join", ref joinOption);
      var labelOptionIndex = getter.AddOption(
        "Label",
        string.IsNullOrEmpty(labelValue) ? "None" : labelValue);
      var labelPrefixOptionIndex = getter.AddOption("LabelPrefix", string.IsNullOrEmpty(_labelPrefix) ? "None" : _labelPrefix);
      getter.AddOptionToggle("Scallop", ref scallopOption);
      getter.AddOptionToggle("UseCurve", ref useCurveOption);
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
        useCurveOption,
        scallopSizeOptionIndex,
        connectOption,
        joinOption,
        layerOptionIndex,
        labelOptionIndex,
        labelPrefixOptionIndex,
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
      if (double.IsFinite(options.Offset.CurrentValue))
        _offset = options.Offset.CurrentValue;
      _chamfer = options.Chamfer.CurrentValue;
      _reinforcement = options.Reinforcement.CurrentValue;
      _createReinf = options.CreateReinf.CurrentValue;
      _scallop = options.Scallop.CurrentValue;
      _useCurve = options.UseCurve.CurrentValue;
      _connect = options.Connect.CurrentValue;
      _join = options.Join.CurrentValue;
      _cutOffset = options.CutOffset.CurrentValue;

      if (getter.Option()?.Index == options.LayerOptionIndex)
        PromptForLayer(doc, mode, layerSession);
      else if (getter.Option()?.Index == options.CutLayerOptionIndex &&
               LayerSelector.TrySelect(
                 doc,
                 _cutLayer,
                 ConfiguredDefaultCutLayer,
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
          labelValue = LabelSequences.NormalizeValue(labelValue);
          doc.Strings.SetString(OptionsSectionName, LabelKey, labelValue);
        }
      }
      else if (getter.Option()?.Index == options.LabelPrefixOptionIndex)
      {
        using var textGetter = new GetString();
        textGetter.SetCommandPrompt("Label prefix (Enter to clear)");
        textGetter.AcceptNothing(true);
        var result = textGetter.Get();
        if (result == GetResult.String || result == GetResult.Nothing)
          _labelPrefix = result == GetResult.String ? textGetter.StringResult().Trim() : ConfiguredDefaultLabelPrefix;
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

    private static string IncrementLabelValue(string text) => LabelSequences.Increment(text, ConfiguredDefaultLabel);

    private static string FormatCornerLabel(string prefix, string value) => string.IsNullOrWhiteSpace(value)
      ? string.Empty : LabelSequences.FormatDisplayLabel(prefix + value);

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
      static string ReadMaterial(System.Text.Json.Nodes.JsonObject? section,string key,string fallback)=>
        section?[key] is System.Text.Json.Nodes.JsonValue value&&value.TryGetValue<string>(out var name)?name:fallback;
      var values = ToolsOptionStore.Read(
        OptionsSectionName,
        section =>
        {
          var offset = _offset;
          var chamfer = _chamfer;
          var reinforcement = _reinforcement;
          var createReinf = ConfiguredDefaultCreateReinf;
          var scallop = _scallop;
          var useCurve = ConfiguredDefaultUseCurve;
          var labelPrefix = ConfiguredDefaultLabelPrefix;
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
          var splitSeamType = ConfiguredDefaultSplitSeamType;
          var shadeMaterial = ReadMaterial(section,ShadeMaterialKey,ConfiguredDefaultShadeMaterial);
          var reinforcementMaterial = ReadMaterial(section,ReinforcementMaterialKey,ConfiguredDefaultReinforcementMaterial);

          if (ToolsOptionStore.TryGetDouble(section, OffsetKey, out var savedOffset) && double.IsFinite(savedOffset))
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
          if (ToolsOptionStore.TryGetBool(section, UseCurveKey, out var savedUseCurve))
            useCurve = savedUseCurve;
          if (ToolsOptionStore.TryGetString(section, LabelPrefixKey, out var savedLabelPrefix))
            labelPrefix = savedLabelPrefix.Trim();
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
              double.IsFinite(savedSplitWidth) && savedSplitWidth > 0.0)
            splitWidth = savedSplitWidth;
          if(ToolsOptionStore.TryGetString(section,SplitSeamTypeKey,out var savedType)&&
            Enum.TryParse<PartSplitWorkflow.SeamType>(savedType,true,out var parsedType)&&Enum.IsDefined(parsedType))
            splitSeamType=parsedType;

          return (offset, chamfer, reinforcement, scallop, scallopSize, scallopPercent,
            tuneScallopSize, tuneScallopPercent,
              cutOffset, cutLayer, connect, join, layer, splitWidth, createReinf, useCurve, labelPrefix, splitSeamType, shadeMaterial, reinforcementMaterial);
        });

      _offset = values.offset;
      _chamfer = values.chamfer;
      _reinforcement = values.reinforcement;
      _createReinf = values.createReinf;
      _scallop = values.scallop;
      _useCurve = values.useCurve;
      _labelPrefix = values.labelPrefix;
      _scallopSize = values.scallopSize;
      _scallopPercent = values.scallopPercent;
      _tuneScallopSize = new ScallopSize(values.tuneScallopSize, values.tuneScallopPercent);
      _cutOffset = values.cutOffset;
      _cutLayer = values.cutLayer;
      _connect = values.connect;
      _join = values.join;
      _layer = DuplicateCommandSupport.NormalizeLayerOption(values.layer);
      _splitWidth = values.splitWidth;
      _splitSeamType = values.splitSeamType;
      var materials=string.IsNullOrWhiteSpace(values.shadeMaterial)&&string.IsNullOrWhiteSpace(values.reinforcementMaterial)?
        []:MaterialLibrary.LoadMaterialPresets();
      MaterialLibrary.MaterialPreset? Resolve(string name)
      {
        string normalized=MaterialLibrary.MaterialPresetName(name);
        return materials.FirstOrDefault(preset=>preset.Name.Equals(normalized,StringComparison.OrdinalIgnoreCase));
      }
      _shadeMaterial=Resolve(values.shadeMaterial);
      _reinforcementMaterial=Resolve(values.reinforcementMaterial);
    }

    private static void SaveOptions() =>
      ToolsOptionStore.Update(OptionsSectionName, section =>
      {
        section[OffsetKey] = _offset;
        section[ChamferKey] = _chamfer;
        section[ReinforcementKey] = _reinforcement;
        section[CreateReinfKey] = _createReinf;
        section[ScallopKey] = _scallop;
        section[UseCurveKey] = _useCurve;
        section[LabelPrefixKey] = _labelPrefix;
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
        section[SplitSeamTypeKey] = _splitSeamType.ToString();
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

    private static bool RunSplit(RhinoDoc doc, IReadOnlyList<Guid> outputIds, Action beginSplitUndo)
    {
      var source = FindSplitSource(doc, outputIds);
      if (source == null)
      {
        RhinoApp.WriteLine("vShade: Split needs a closed cut or shade boundary.");
        return false;
      }
      var ids = outputIds.Where(id => id == source.Id || doc.Objects.FindId(id)?.Attributes.GetUserString(MetadataPrefix + "role")
        is "boundary" or "perpendicular" or "connection" or "scallop" or "reinforcement" or "label").ToArray();
      return PartSplitWorkflow.Run(doc, ids, _splitWidth, width => { beginSplitUndo(); _splitWidth = width; },
        _splitSeamType, type=>_splitSeamType=type) == Result.Success;
    }

    private static RhinoObject? FindSplitSource(RhinoDoc doc, IReadOnlyList<Guid> outputIds) =>
      outputIds.Select(doc.Objects.FindId)
        .Where(obj => obj?.Geometry is Curve curve && curve.IsClosed)
        .Where(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") is "cut" or "boundary")
        .OrderBy(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") == "cut" ? 0 : 1)
        .FirstOrDefault();
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
        {
          double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
          using var sources = ResolveShadeSources(doc, placements, tolerance);
          AddReinforcementParts(doc,doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??Plane.WorldXY,
            PrepareSourceCaps(sources.Curves,placements,scallopSizes,tolerance),Array.Empty<ShadeConnectionGeometry>(),sessionId,tolerance,retained);
        }
        BringOutputForward(doc,retained.Skip(retainedCount));
        SaveShadeState(doc,sessionId,placements,scallopSizes,retained);
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
      IReadOnlyList<ShadeConnectionGeometry> connections,Guid sessionId,double tolerance,List<Guid> outputIds,
      IReadOnlyDictionary<int, ReinforcementPose>? poses = null)
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
        var placed = new HashSet<int>();
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
          double gap=ReinforcementPartGapInches*RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem);
          if(!double.IsFinite(gap)||gap<=0) gap=ReinforcementPartGapInches;
          var samples = SampleReinforcementOutline(part);
          Transform transform;
          if (poses != null && poses.TryGetValue(placement.Sequence, out var pose))
          {
            using var area = AreaMassProperties.Compute(part);
            if (area == null) throw new InvalidOperationException("could not locate the updated reinforcement center");
            transform = Transform.Translation(pose.Center - (pose.Rotation * area.Centroid)) * pose.Rotation;
          }
          else
          {
            if (border == null) continue;
            transform = ReinforcementPartTranslation(part,border,plane,-placement.Geometry.InteriorDirection,gap,tolerance);
          }
          if (!part.Transform(transform)) throw new InvalidOperationException("could not position the updated reinforcement");
          using var attributes=CreateIdentifiedAttributes(UzipCommon.EnsureLayer(doc,_cutLayer),ReinforcementPartObjectName,
            "reinforcement_part",sessionId,placement.Sequence,placement.FirstSourceId,placement.SecondSourceId);
          attributes.SetUserString(ReinforcementPoseKey, System.Text.Json.JsonSerializer.Serialize(samples.Select(Components)));
          var id=doc.Objects.AddCurve(part,attributes);
          if(id==Guid.Empty) throw new InvalidOperationException("could not add detached reinforcement part");
          outputIds.Add(id); var groupIds=new List<Guid> {id};
          var sourceLabel=originalObjects.FirstOrDefault(obj=>obj.Attributes.GetUserString(MetadataPrefix+"role")=="label"&&
            obj.Attributes.GetUserString(MetadataPrefix+"sequence")==sequence);
          if(sourceLabel?.Geometry is TextEntity sourceText)
          {
            using var text=(TextEntity)sourceText.Duplicate(); text.Transform(transform);
            using var labelAttributes=sourceLabel.Attributes.Duplicate(); labelAttributes.RemoveFromAllGroups();
            labelAttributes.Name=ReinforcementPartLabelName; labelAttributes.SetUserString(MetadataPrefix+"role","reinforcement_part_label");
            var labelId=doc.Objects.AddText(text,labelAttributes);
            if(labelId==Guid.Empty) throw new InvalidOperationException("could not add detached reinforcement label");
            outputIds.Add(labelId); groupIds.Add(labelId);
          }
          var name=ReinforcementPartGroupPrefix+sessionId.ToString("N")+"_"+sequence;
          int group=doc.Groups.FindName(name)?.Index??doc.Groups.Add(name);
          if(group<0||!doc.Groups.AddToGroup(group,groupIds)) throw new InvalidOperationException("could not group detached reinforcement part");
          NestWorkflow.AssignInitialPartMaterial(doc,group,_reinforcementMaterial);
          placed.Add(placement.Sequence);
          Log.Write("vShade","reinforcement part corner={0} clearance={1:G17} {2} layer={3}",placement.Sequence,gap,doc.ModelUnitSystem,_cutLayer);
        }
        if (poses != null && poses.Keys.Any(sequence => !placed.Contains(sequence)))
          throw new InvalidOperationException("could not regenerate an existing detached reinforcement in place");
      }
      finally { foreach(var curve in finished) curve.Dispose(); foreach(var curve in finishedPieces) curve.Dispose(); }
    }

    private readonly record struct ReinforcementPose(Transform Rotation, Point3d Center);

    private static Point3d[] SampleReinforcementOutline(Curve curve) =>
      Enumerable.Range(0, ReinforcementPoseSamples)
        .Select(i => curve.PointAtNormalizedLength((double)i / ReinforcementPoseSamples)).ToArray();

    private static bool TryGetReinforcementPose(Point3d[] baseline, Curve existing, double tolerance, out ReinforcementPose pose)
    {
      pose = default;
      if (baseline.Length != ReinforcementPoseSamples || baseline.Any(point => !point.IsValid)) return false;
      var moved = SampleReinforcementOutline(existing);
      if (moved.Any(point => !point.IsValid)) return false;
      int far = Enumerable.Range(1, baseline.Length - 1).MaxBy(i => baseline[0].DistanceToSquared(baseline[i]));
      var axis = baseline[far] - baseline[0];
      int offAxis = Enumerable.Range(1, baseline.Length - 1).MaxBy(i =>
        Vector3d.CrossProduct(axis, baseline[i] - baseline[0]).SquareLength);
      var from = new Plane(baseline[0], axis, baseline[offAxis] - baseline[0]);
      var to = new Plane(moved[0], moved[far] - moved[0], moved[offAxis] - moved[0]);
      if (!from.IsValid || !to.IsValid) return false;
      var match = Transform.PlaneToPlane(from, to);
      if (baseline.Where((point, i) => (match * point).DistanceTo(moved[i]) > tolerance).Any()) return false;
      using var area = AreaMassProperties.Compute(existing);
      if (area == null || !area.Centroid.IsValid) return false;
      from.Origin = to.Origin = Point3d.Origin;
      pose = new ReinforcementPose(Transform.PlaneToPlane(from, to), area.Centroid);
      return true;
    }

    private static Dictionary<int, ReinforcementPose> CaptureReinforcementPoses(RhinoDoc doc, Guid session,
      IReadOnlyList<ShadePlacement> placements, IReadOnlyList<Guid> ids, double tolerance)
    {
      var result = new Dictionary<int, ReinforcementPose>();
      var objects = ids.Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
      ShadeSnapshot? previous = null;
      Curve[]? boundary = null;
      try
      {
        foreach (var obj in objects.Where(obj => obj.Attributes.GetUserString(MetadataPrefix + "role") == "reinforcement_part"))
        {
          if (obj.Geometry is not Curve existing ||
              !int.TryParse(obj.Attributes.GetUserString(MetadataPrefix + "sequence"), out int sequence) ||
              !placements.Any(placement => placement.Sequence == sequence)) continue;
          Point3d[] baseline = [];
          if (obj.Attributes.GetUserString(ReinforcementPoseKey) is { Length: > 0 } saved)
            baseline = (System.Text.Json.JsonSerializer.Deserialize<double[][]>(saved) ?? []).Select(RestorePoint).ToArray();
          else
          {
            if (previous == null && doc.Strings.GetValue(SessionStateSection, session.ToString("N")) is { Length: > 0 } json)
              previous = System.Text.Json.JsonSerializer.Deserialize<ShadeSnapshot>(json);
            var corner = previous?.Placements.FirstOrDefault(item => item.Sequence == sequence);
            var arc = objects.FirstOrDefault(candidate =>
              candidate.Attributes.GetUserString(MetadataPrefix + "role") == "reinforcement" &&
              candidate.Attributes.GetUserString(MetadataPrefix + "sequence") == sequence.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Geometry as Curve;
            if (corner is { HasPerpendicularLine: true } && arc != null)
            {
              boundary ??= Curve.JoinCurves(objects.Where(candidate => candidate.Attributes.GetUserString(MetadataPrefix + "role")
                is "boundary" or "perpendicular" or "connection" or "scallop").Select(candidate => candidate.Geometry).OfType<Curve>(), tolerance, false);
              var cap = new Line(RestorePoint(corner.FirstCapEnd), RestorePoint(corner.SecondCapEnd));
              using var outline = ReinforcementPartOutlineFromBoundary(arc, cap, boundary, tolerance);
              if (outline != null) baseline = SampleReinforcementOutline(outline);
            }
          }
          if (!TryGetReinforcementPose(baseline, existing, tolerance * ConfiguredEndpointCoincidenceToleranceFactor, out var pose))
            throw new InvalidOperationException($"could not recover the placement of detached reinforcement {sequence}; no shade objects were changed");
          result.Add(sequence, pose);
        }
        return result;
      }
      finally { if (boundary != null) foreach (var loop in boundary) loop.Dispose(); }
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
