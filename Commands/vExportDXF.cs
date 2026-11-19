namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Reflection;
  using System.Text.RegularExpressions;
  using System.Threading;
  using System.Xml.Linq;
  using Rhino;
  using Rhino.Commands;
  using Rhino.DocObjects;
  using Rhino.FileIO;
  using Rhino.Geometry;
  using Rhino.Input;
  using Rhino.Input.Custom;
  using Rhino.PlugIns;
  using Rhino.UI;


  /// <summary>Exports selected geometry, preserving Rhino's current DXF scheme and group blocks.</summary>
  [CommandStyle(Style.ScriptRunner)]
  public sealed partial class vExportDXF : vToolsCommand
  {
    // Defaults and output conventions
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultNotchTrim", "No keeps touched export curves; Split moves between-leg pieces; Trim omits them.", OptionKeys = "notch_trim")] private const NotchTrimMode DefaultNotchTrim = NotchTrimMode.Trim; // No keeps touched export curves; Split moves between-leg pieces; Trim omits them.

    private static readonly NotchTrimMode ConfiguredDefaultNotchTrim = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultNotchTrim", DefaultNotchTrim); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultNotchSplitLayer", "Rhino layer path for between-leg export pieces in Split mode.", OptionKeys = "notch_split_layer")] private const string DefaultNotchSplitLayer = "Reference"; // Rhino layer path for between-leg export pieces in Split mode.

    private static readonly string ConfiguredDefaultNotchSplitLayer = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultNotchSplitLayer", DefaultNotchSplitLayer); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultExplode", "No preserves curves; SplitAtCorners splits tangent discontinuities; Full separates all curve components.", OptionKeys = "explode")] private const ExportExplodeMode DefaultExplode = ExportExplodeMode.Full; // No preserves curves; SplitAtCorners splits tangent discontinuities; Full separates all curve components.

    private static readonly ExportExplodeMode ConfiguredDefaultExplode = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultExplode", DefaultExplode); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandChoicesAttribute("vExportDXF", "explode", false, "No", "SplitAtCorners", "Full")] private static readonly string[] ExplodeNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vExportDXF), nameof(ExplodeNames)); // Command-line values for temporary export-curve decomposition.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.ExportSplitParameterTolerance", "Relative domain tolerance for progressing past a reported curve discontinuity.")] private const double ExportSplitParameterTolerance = 1e-10; // Relative domain tolerance for progressing past a reported curve discontinuity.

    private static readonly double ConfiguredExportSplitParameterTolerance = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.ExportSplitParameterTolerance", ExportSplitParameterTolerance); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultNotchJoin", "true joins matching V/U notches into line/polyline export curves; false exports notch components separately.", OptionKeys = "notch_join")] private const bool DefaultNotchJoin = true; // true joins matching V/U notches into line/polyline export curves; false exports notch components separately.

    private static readonly bool ConfiguredDefaultNotchJoin = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultNotchJoin", DefaultNotchJoin); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultTextBreak", "true exports each multiline text row separately; false keeps multiline text objects intact.", OptionKeys = "text_break")] private const bool DefaultTextBreak = true; // true exports each multiline text row separately; false keeps multiline text objects intact.

    private static readonly bool ConfiguredDefaultTextBreak = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultTextBreak", DefaultTextBreak); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultRemoveGrain", "true omits open GRAIN-named/layered curves from export; false includes them without changing source geometry.", OptionKeys = "remove_grain")] private const bool DefaultRemoveGrain = true; // true omits open GRAIN-named/layered curves from export; false includes them without changing source geometry.

    private static readonly bool ConfiguredDefaultRemoveGrain = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultRemoveGrain", DefaultRemoveGrain); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultMoveOrigin", "true moves export copies to the world origin; false preserves source drawing coordinates.", OptionKeys = "move_origin")] private const bool DefaultMoveOrigin = true; // true moves export copies to the world origin; false preserves source drawing coordinates.

    private static readonly bool ConfiguredDefaultMoveOrigin = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultMoveOrigin", DefaultMoveOrigin); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultEndTrim", "Length in drawing units removed from each open non-cutting curve end touching the part's outer cutting outline; zero or greater, 0 disables. Only export copies change.", OptionKeys = "end_trim")]
    private const double DefaultEndTrim = 0.02; // Drawing units along each touching end; zero disables export-only shortening.
    private static readonly double ConfiguredDefaultEndTrim = Math.Max(0, global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultEndTrim", DefaultEndTrim)); // Resolved through vSettings; restart after advanced-default changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.CutLayerPrefix", "Case-insensitive, nonempty leaf-layer prefix identifying cutting geometry protected from EndTrim; CUT includes CUT1 and CUT2.")]
    private const string CutLayerPrefix = "CUT"; // Nonempty leaf-layer prefix; matching curves define cutting outlines and are never shortened by EndTrim.
    private static readonly string ConfiguredCutLayerPrefix = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.CutLayerPrefix", CutLayerPrefix); // Resolved through vSettings; restart after changes.
    private const string GrainIdentifier = "GRAIN"; // Case-insensitive object name or leaf layer name identifying an open grain guide.
    private const string NestMetadataPrefix = "vNest."; // Internal nesting bookkeeping omitted from export-copy user text; originals retain all provenance.
    private const string CleanupPieceKey = "vExportDXF.cleanupPiece"; // Temporary-only GUID in N format linking cleanup fragments to their original export piece; never written to source objects or export attributes.
    internal const string CleanupPartitionKey = "vExportDXF.cleanupPart"; // Temporary-only export-part identity; duplicate cleanup must not compare independent native patterns.
    private const bool DefaultIsolateCleanupParts=false; // false retains standalone DXF cleanup scope; true isolates native-project cleanup by the same groups and inferred parts used for export.
    private static readonly Regex NativeSkippedCounts = new(@"^[^:\r\n]+:\s*\d+\s+written,\s*(\d+)\s+skipped[^\S\r\n]*\r?$",RegexOptions.Multiline|RegexOptions.CultureInvariant|RegexOptions.IgnoreCase); // English native export totals; a nonzero skipped count rejects the temporary file.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultOptimize", "true cleans temporary export copies with vCleanup; false skips cleanup. Neither changes source geometry.", OptionKeys = "optimize")] private const bool DefaultOptimize = true; // true cleans temporary export copies; false skips cleanup; source geometry stays unchanged in either case.

    private static readonly bool ConfiguredDefaultOptimize = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultOptimize", DefaultOptimize); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultCleanupAction", "Ask highlights findings on originals; Delete removes findings from export copies only; Ignore keeps them in export copies.", OptionKeys = "cleanup_action")] private const vCleanup.ExportCleanupAction DefaultCleanupAction = vCleanup.ExportCleanupAction.Ask; // Ask reviews original-object highlights; Delete cleans copies only; Ignore leaves export findings intact.

    private static readonly vCleanup.ExportCleanupAction ConfiguredDefaultCleanupAction = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultCleanupAction", DefaultCleanupAction); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandChoicesAttribute("vExportDXF", "cleanup_action", false, "Ask", "Delete", "Ignore")] private static readonly string[] CleanupActionNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vExportDXF), nameof(CleanupActionNames)); // Saved export-only cleanup behavior; independent of vCleanup's own deletion setting.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultSaveDefaults", "true persists scripted option changes; false applies them to this export only.")] private const bool DefaultSaveDefaults = false; // true persists scripted option changes; false applies them to this export only.

    private static readonly bool ConfiguredDefaultSaveDefaults = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultSaveDefaults", DefaultSaveDefaults); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandChoicesAttribute("vExportDXF", "notch_trim", false, "No", "Split", "Trim")] private static readonly string[] NotchTrimNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vExportDXF), nameof(NotchTrimNames)); // Rhino option order matching NotchTrimMode.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.NotchContactToleranceScale", "Document absolute-tolerance multiplier for notch leg contact.")] private const double NotchContactToleranceScale = 2.0; // Document absolute-tolerance multiplier for notch leg contact.

    private static readonly double ConfiguredNotchContactToleranceScale = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.NotchContactToleranceScale", NotchContactToleranceScale); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.NotchContactZeroToleranceScale", "Minimum Rhino zero-tolerance multiplier for contact.")] private const double NotchContactZeroToleranceScale = 10.0; // Minimum Rhino zero-tolerance multiplier for contact.

    private static readonly double ConfiguredNotchContactZeroToleranceScale = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.NotchContactZeroToleranceScale", NotchContactZeroToleranceScale); // Resolved once from advanced defaults; restart Rhino after changes.
    private const string NotchMetadataPrefix = "notch."; // Metadata prefix stored on vNotches geometry.
    private const string NotchRole = "notch"; // Role value that distinguishes notch components from labels and source curves.
    private const string OpenVNotchType = "\\/"; // Two-component V notch type in vNotches metadata.
    private const string SettingsSection = "vExportDXF"; // Global settings section for this command.
    private const string LegacySettingsSection = "vExportOptions"; // Prior settings section retained as a read-only fallback.
    private const string DocumentSection = "vTools"; // Rhino document-string section for export settings.
    private const string NotchTrimEntry = "vExportDXF.NotchTrim"; // Document setting key for the export notch treatment.
    private const string NotchSplitLayerEntry = "vExportDXF.NotchSplitLayer"; // Document setting key for the export split layer.
    private const string PriorNotchTrimLayerEntry = "vExportDXF.NotchTrimLayer"; // Previous export split-layer key read for migration.
    private const string ExplodeEntry = "vExportDXF.Explode"; // Document setting key for temporary curve decomposition.
    private const string NotchJoinEntry = "vExportDXF.NotchJoin"; // Document setting key for joining split/trimmed notch geometry in exported copies.
    private const string TextBreakEntry = "vExportDXF.TextBreak"; // Document setting key for splitting multiline export text.
    private const string RemoveGrainEntry = "vExportDXF.RemoveGrain"; // Document setting key for export-only grain-guide removal.
    private const string MoveOriginEntry = "vExportDXF.MoveOrigin"; // Document setting key for export-only translation to the world origin.
    private const string EndTrimEntry = "vExportDXF.EndTrim"; // Document setting key for non-cutting endpoint shortening in drawing units; zero disables.
    private const string OptimizeEntry = "vExportDXF.Optimize"; // Document setting key for export cleanup.
    private const string LegacyNotchTrimEntry = "vExportOptions.NotchTrim"; // Previous document key read for migration.
    private const string LegacyNotchTrimLayerEntry = "vExportOptions.NotchTrimLayer"; // Previous document key read for migration.
    private const string LegacyOptimizeEntry = "vExportOptions.Optimize"; // Previous document key read for migration.
    private const string LastPathKey = ".DXF_export.LastPath"; // Document user-text key compatible with the Python exporter.
    private const string BlockInsertLayer = "Reference"; // Layer for temporary group-block inserts.
    private const int SelectionFailureLogLimit = 8; // Maximum prepared-object selection failures detailed in one diagnostic entry sequence.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.DefaultFileStem", "Suggested filename stem for an unsaved Rhino document.")] private const string DefaultFileStem = "untitled"; // Suggested filename stem for an unsaved Rhino document.

    private static readonly string ConfiguredDefaultFileStem = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.DefaultFileStem", DefaultFileStem); // Resolved once from advanced defaults; restart Rhino after changes.
    private const string UnnamedGroupPrefix = "Group_"; // Export block name prefix followed by a zero-based index for unnamed Rhino groups.
    private const string DxfExtension = ".dxf"; // Required output filename extension.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.ExportWaitMilliseconds", "Maximum wait for Rhino to finish writing the temporary file.")] private const int ExportWaitMilliseconds = 2000; // Maximum wait for Rhino to finish writing the temporary file.

    private static readonly int ConfiguredExportWaitMilliseconds = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.ExportWaitMilliseconds", ExportWaitMilliseconds); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.ExportPollMilliseconds", "File-existence polling interval in milliseconds.")] private const int ExportPollMilliseconds = 50; // File-existence polling interval in milliseconds.

    private static readonly int ConfiguredExportPollMilliseconds = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.ExportPollMilliseconds", ExportPollMilliseconds); // Resolved once from advanced defaults; restart Rhino after changes.
    private const int ExportCopyBufferSize = 1024 * 1024; // Byte buffer for in-place destination overwriting; matches the original exporter without renaming existing files.
    private const int MaximumTemporaryNameAttempts = 100; // Collision attempts for temporary files and block names.
    private const int MaximumBlockNameLength = 48; // Maximum ASCII characters retained from a source group name.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TableLabelFramePaddingFraction", "Fallback frame padding as a fraction of annotation height when only table text is selected.")] private const double TableLabelFramePaddingFraction = 0.1; // Fallback frame padding as a fraction of annotation height when only table text is selected.

    private static readonly double ConfiguredTableLabelFramePaddingFraction = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.TableLabelFramePaddingFraction", TableLabelFramePaddingFraction); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TableAnnotationPaddingFraction", "Export-label inset as a fraction of measured glyph height; matches the nest label area's padding.")] private const double TableAnnotationPaddingFraction = 0.2; // Export-label inset as a fraction of measured glyph height; matches the nest label area's padding.

    private static readonly double ConfiguredTableAnnotationPaddingFraction = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.TableAnnotationPaddingFraction", TableAnnotationPaddingFraction); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TableExportTextScale", "Fixed visible annotation scale for fresh title-style table text; independent of model-space annotation scaling.")] private const double TableExportTextScale = 1.0; // Fixed visible annotation scale for fresh title-style table text; independent of model-space annotation scaling.

    private static readonly double ConfiguredTableExportTextScale = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.TableExportTextScale", TableExportTextScale); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TableExportLineHeightFactor", "Minimum row clearance in nominal text heights, reserving font ascent/descent across DXF importers.")] private const double TableExportLineHeightFactor = 1.25; // Minimum row clearance in nominal text heights, reserving font ascent/descent across DXF importers.

    private static readonly double ConfiguredTableExportLineHeightFactor = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.TableExportLineHeightFactor", TableExportLineHeightFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TableHeaderTextSizeMultiplier", "Export-only compact-header text height multiplier; positive, 1 retains the drawing's text size.")]
    private const double TableHeaderTextSizeMultiplier = 1.15; // Positive multiplier of the source header's visible font height; does not change drawing text.
    private static readonly double ConfiguredTableHeaderTextSizeMultiplier=Math.Max(0.01,global::vTools.CommandDefaults.Get("vExportDXF","vTools.Commands.vExportDXF.TableHeaderTextSizeMultiplier",TableHeaderTextSizeMultiplier)); // Resolved through vSettings.
    [global::vTools.CommandDefault("vExportDXF","vTools.Commands.vExportDXF.TableHeaderTextSizeExtra","Additional compact exported-header text height in drawing units, after the size multiplier; zero or greater. Drawing annotations stay unchanged.")]
    private const double TableHeaderTextSizeExtra=3.0; // Nonnegative drawing-unit increase; larger export text extends the compact label toward the parts.
    private static readonly double ConfiguredTableHeaderTextSizeExtra=Math.Max(0,global::vTools.CommandDefaults.Get("vExportDXF","vTools.Commands.vExportDXF.TableHeaderTextSizeExtra",TableHeaderTextSizeExtra)); // Export-only size addition, configurable through vSettings.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TableHeaderSpacingMultiplier", "Export-only compact-header row separation and text-padding multiplier; positive, 1 retains the source row separation and base padding.")]
    private const double TableHeaderSpacingMultiplier = 1.2; // Positive multiplier for row-center separation and measured text padding in exported headers only.
    private static readonly double ConfiguredTableHeaderSpacingMultiplier=Math.Max(0.01,global::vTools.CommandDefaults.Get("vExportDXF","vTools.Commands.vExportDXF.TableHeaderSpacingMultiplier",TableHeaderSpacingMultiplier)); // Resolved through vSettings.
    private const double TableLabelExportGapInches = 10.0; // Export-only outward displacement of the label block from its table, in inches converted to document units.
    private const int DxfMiddleCenterAttachment = 5; // DXF MTEXT attachment code: middle-centre, matching working notch/title insertion anchors.
    private const int BlockNamePrefixLength = 39; // Maximum retained stem before an eight-character collision suffix.
    private static readonly Guid AcadExportPluginId =
      new("39a88493-9e97-4f15-bd62-ad25896a2632"); // Rhino's ACAD export plug-in settings ID.

    public override string EnglishName => "vExportDXF";

    internal readonly record struct ExportSettings(
      NotchTrimMode NotchTrim, string NotchSplitLayer, bool NotchJoin, bool TextBreak, bool Optimize, ExportExplodeMode Explode,
      vCleanup.ExportCleanupAction CleanupAction = DefaultCleanupAction, bool RemoveGrain = DefaultRemoveGrain, bool MoveOrigin = DefaultMoveOrigin,
      double EndTrim = DefaultEndTrim);
    internal enum ExportExplodeMode { No, SplitAtCorners, Full }
    private static ExportCleanupFeedback? _cleanupFeedback;
    private static readonly Dictionary<uint, ExportDocumentState> _documentStates = [];

    internal sealed record PreparedExportEntry(
      Guid Id, Guid[] SourceIds, string? GroupName, bool TableAnnotation);

    internal delegate Result PreparedExportWriter(RhinoDoc doc,
      IReadOnlyList<RhinoObject> sources, IReadOnlyList<PreparedExportEntry> entries,
      Transform translation,bool removeGrain);

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      RunPreparedExport(doc, mode, null);

    internal static Result RunPreparedExport(RhinoDoc doc, RunMode mode,
      PreparedExportWriter? writer,Action<string>? preparationStatus=null,ExportSettings? settingsOverride=null,
      IReadOnlyList<RhinoObject>? sourcesOverride=null,vCleanup.ExportGeometrySettings? geometryOverride=null,
      Func<int,int,string?,vCleanup.ExportCleanupAction?>? reviewFindings=null,
      Action? keepResponsive=null,Action<int,int>? findingsChanged=null,Action<Exception>? preparationFailed=null,
      Action<ExportCleanupFeedback>? cleanupReviewReady=null)
    {
      using var documentState = new ExportDocumentState(doc);
      CleanupFindingDisplay.ClearAll();
      var settings = settingsOverride??LoadSettings(doc);
      if (mode == RunMode.Scripted&&settingsOverride==null)
      {
        var optionResult = GetRunOptions(doc, ref settings);
        if (optionResult != Result.Success)
          return optionResult;
      }
      var selection = doc.Objects.GetSelectedObjects(false, false)
        .Where(obj => obj != null).GroupBy(obj => obj.Id).Select(group => group.First())
        .ToArray();
      var targets = sourcesOverride?.Where(obj=>!obj.IsDeleted).ToArray()??(selection.Length > 0 ? selection : doc.Objects.GetObjectList(
        new ObjectEnumeratorSettings
        {
          NormalObjects = true,
          LockedObjects = false,
          HiddenObjects = false,
          IncludeLights = false,
          IncludeGrips = false,
          IncludePhantoms = false,
        }).Where(obj => obj != null).ToArray());
      targets = FilterExportGrain(doc, targets, settings.RemoveGrain);
      if (targets.Length == 0)
      {
        RhinoApp.WriteLine("vExportDXF: no objects to export.");
        return Result.Nothing;
      }
      string? tempPath = null;
      var originalSelection = selection.Select(obj => obj.Id).ToArray();
      var originalGrips = doc.Objects.GetSelectedObjects(true, true).OfType<GripObject>().ToArray();
      var temporaryObjects = new List<Guid>();
      var temporaryDefinitions = new List<int>();
      var preparedEntries = writer == null ? null : new List<PreparedExportEntry>();
      TemporaryBlockLayerScope? blockLayerScope = null;
      ExportCleanupFeedback? cleanupFeedback = null;
      var notchTrimOverrides = new Dictionary<Guid, List<ExportCurvePiece>>();
      var textBreakOverrides = new Dictionary<Guid, List<TextEntity>>();
      try
      {
        preparationStatus?.Invoke("Preparing notch and curve copies");
        notchTrimOverrides = BuildNotchTrimOverrides(
          doc, targets, settings.NotchTrim, settings.NotchSplitLayer, settings.NotchJoin);
        ApplyNonCutEndTrim(doc, targets, notchTrimOverrides, settings.EndTrim);
        if (settings.Optimize)
        {
          preparationStatus?.Invoke("Checking export geometry");
          cleanupFeedback = new ExportCleanupFeedback(doc){KeepResponsive=keepResponsive,FindingsChanged=findingsChanged,ReviewReady=cleanupReviewReady};
          var result = OptimizeExportPieces(doc, targets, notchTrimOverrides, settings.CleanupAction, cleanupFeedback,geometryOverride,reviewFindings,writer!=null);
          if (result != Result.Success) return result;
          targets = targets.Where(obj => obj.Geometry is not Curve || ExportPieces(obj, notchTrimOverrides).Any()).ToArray();
          if (targets.Length == 0)
          {
            RhinoApp.WriteLine("vExportDXF: Optimize removed all exportable objects.");
            return Result.Nothing;
          }
        }
        preparationStatus?.Invoke("Preparing curves and labels");
        ApplyExportExplode(targets,notchTrimOverrides,settings.Explode);
        textBreakOverrides = BuildTextBreakOverrides(doc, targets, settings.TextBreak);
        var translation = settings.MoveOrigin
          ? OriginTranslation(targets.Where(obj => !NestTableMetadata.IsTableStructure(doc, obj)).ToArray())
          : Transform.Identity;
        preparationStatus?.Invoke("Building temporary export parts");
        var exportIds = PrepareExportObjects(
          doc, targets, translation, notchTrimOverrides, textBreakOverrides,
          temporaryObjects, temporaryDefinitions, preparedEntries);
        if (exportIds.Count == 0)
          throw new InvalidOperationException("No exportable objects were prepared.");

        if (writer != null)
          return writer(doc, targets, preparedEntries!, translation,settings.RemoveGrain);

        var options = CurrentDxfWriteOptions();
        blockLayerScope = new TemporaryBlockLayerScope(doc, exportIds);
        doc.Objects.UnselectAll();
        int selectedCount = 0;
        int selectionFailures = 0;
        foreach (Guid id in exportIds)
        {
          if (doc.Objects.Select(id)) { selectedCount++; continue; }
          if (++selectionFailures <= SelectionFailureLogLimit)
          {
            var rejected = doc.Objects.FindId(id);
            var layer = rejected == null ? null : doc.Layers.FindIndex(rejected.Attributes.LayerIndex);
            Log.Write("vExportDXF", $"prepared selection rejected id={id} type={rejected?.ObjectType} " +
              $"layer={layer?.FullPath} visible={layer?.IsVisible} locked={layer?.IsLocked} mode={rejected?.Attributes.Mode}");
          }
        }
        if (selectedCount != exportIds.Count)
          throw new InvalidOperationException("Not all prepared objects could be selected; export cancelled.");
        if (!TryChoosePath(doc, out string path))
          return Result.Cancel;
        tempPath = MakeTemporaryPath(path);
        string beforeExport = RhinoApp.CommandHistoryWindowText ?? string.Empty;
        using (CommandHistoryCleanup.BeginExport(nameof(vExportDXF), tempPath))
        {
          if (!doc.ExportSelected(tempPath, options.ToDictionary()))
            throw new InvalidOperationException("Rhino DXF export failed.");
        }
        if (!WaitForFile(tempPath))
          throw new IOException("Rhino did not create a nonempty DXF file.");
        VerifyNativeExport(beforeExport, RhinoApp.CommandHistoryWindowText ?? string.Empty);
        if (targets.Any(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelRole))
          NormalizeTableDxfAttachments(tempPath);
        CopyExportFile(tempPath, path);
        doc.Strings.SetString(LastPathKey, path);
        RhinoApp.WriteLine($"vExportDXF: exported {CountText.Format(targets.Length, "object")} to {path}.");
        Log.Write("vExportDXF", $"exported source={targets.Length} prepared={selectedCount} " +
          $"groups={temporaryDefinitions.Count} explode={settings.Explode} textBreak={settings.TextBreak} removeGrain={settings.RemoveGrain} moveOrigin={settings.MoveOrigin} endTrim={settings.EndTrim} " +
          $"textObjectsSplit={textBreakOverrides.Count} textLines={textBreakOverrides.Values.Sum(lines => lines.Count)} " +
          $"selectedOnly={selection.Length > 0} path={path}");
        return Result.Success;
      }
      catch(OperationCanceledException){return Result.Cancel;}
      catch (Exception ex)
      {
        Log.Write("vExportDXF", $"export failed: {ex}");
        preparationFailed?.Invoke(ex);
        RhinoApp.WriteLine($"vExportDXF: {ex.Message}");
        return Result.Failure;
      }
      finally
      {
        doc.Objects.UnselectAll();
        try
        {
          for (int index = temporaryObjects.Count - 1; index >= 0; index--)
            doc.Objects.Delete(temporaryObjects[index], quiet: true);
          for (int index = temporaryDefinitions.Count - 1; index >= 0; index--)
            doc.InstanceDefinitions.Delete(temporaryDefinitions[index], false, true);
        }
        finally { blockLayerScope?.Dispose(); }
        foreach (Guid id in originalSelection)
          doc.Objects.Select(id);
        foreach (var grip in originalGrips)
          if (grip.IsValid) grip.Select(true, true);
        if (cleanupFeedback is { HasFindings: true }) cleanupFeedback.Retain();
        else cleanupFeedback?.Dispose();
        doc.Views.Redraw();
        if (tempPath != null)
        {
          try { File.Delete(tempPath); }
          catch (Exception ex) { Log.Write("vExportDXF", $"temporary file cleanup failed: {ex}"); }
        }
        foreach (var pieces in notchTrimOverrides.Values)
          foreach (var piece in pieces)
            piece.Geometry.Dispose();
        DisposeTextBreakOverrides(textBreakOverrides);
      }
    }

    private static int EnsureExportLayer(RhinoDoc doc, string name)
    {
      var before = doc.Layers.Where(layer => !layer.IsDeleted).Select(layer => layer.Id).ToHashSet();
      int index = UzipCommon.EnsureLayer(doc, name);
      if (_documentStates.TryGetValue(doc.RuntimeSerialNumber, out var state) && doc.Layers.FindIndex(index) is { } layer)
        while (layer != null && !before.Contains(layer.Id))
        {
          state.TemporaryLayers.Add(layer.Id);
          layer = doc.Layers.FindId(layer.ParentLayerId);
        }
      return index;
    }

    private sealed class ExportDocumentState : IDisposable
    {
      private readonly RhinoDoc _doc;
      private readonly bool _modified;
      private readonly bool _undoRecording;
      private readonly Dictionary<string, string>? _before;
      private readonly ExportDocumentState? _previous;
      private bool _disposed;
      internal HashSet<Guid> TemporaryLayers { get; } = [];

      internal ExportDocumentState(RhinoDoc doc)
      {
        _doc = doc;
        _modified = doc.Modified;
        _undoRecording = doc.UndoRecordingEnabled;
        _before = _modified ? null : Snapshot(doc);
        _documentStates.TryGetValue(doc.RuntimeSerialNumber, out _previous);
        _documentStates[doc.RuntimeSerialNumber] = this;
        doc.UndoRecordingEnabled = false;
      }

      private static Dictionary<string, string> Snapshot(RhinoDoc doc)
      {
        var result = new Dictionary<string, string>();
        var serialization = new SerializationOptions { WriteRenderMeshes = false, WriteAnalysisMeshes = false };
        var objects = new ObjectEnumeratorSettings
        {
          NormalObjects = true, HiddenObjects = true, LockedObjects = true, ActiveObjects = true,
          ReferenceObjects = true, IncludeLights = true, IncludeGrips = false, IncludePhantoms = false
        };
        // Instance-definition filtering excludes normal objects; capture both sets explicitly.
        foreach (var obj in doc.Objects.GetObjectList(objects).Concat(doc.InstanceDefinitions
          .Where(definition => !definition.IsDeleted).SelectMany(definition => definition.GetObjects())))
          result["object/" + obj.Id] = obj.RuntimeSerialNumber.ToString(CultureInfo.InvariantCulture) + ":" +
            obj.Geometry.DataCRC(0).ToString(CultureInfo.InvariantCulture) + obj.Attributes.ToJSON(serialization);
        void Components<T>(string prefix, IEnumerable<T> values) where T : ModelComponent
        {
          foreach (var value in values.Where(value => !value.IsDeleted)) result[prefix + value.Id] = value.ToJSON(serialization);
        }
        Components("layer/", doc.Layers);
        Components("group/", doc.Groups);
        Components("definition/", doc.InstanceDefinitions);
        Components("dimstyle/", doc.DimStyles);
        Components("linetype/", doc.Linetypes);
        Components("hatch/", doc.HatchPatterns);
        Components("material/", doc.Materials);
        foreach (var group in doc.Groups.Where(group => !group.IsDeleted))
          result["group-members/" + group.Id] = string.Join(",", doc.Groups.GroupMembers(group.Index).Select(obj => obj.Id).Order());
        for (int index = 0; index < doc.Strings.Count; index++)
        {
          string key = doc.Strings.GetKey(index);
          if (key == LastPathKey || key.StartsWith(DocumentSection + "\\vExportDXF.", StringComparison.Ordinal)) continue;
          result["text/" + key] = doc.Strings.GetValue(index);
        }
        result["settings"] = System.Text.Json.JsonSerializer.Serialize(new
        {
          doc.ModelUnitSystem, doc.PageUnitSystem, doc.ModelAbsoluteTolerance,
          doc.ModelRelativeTolerance, doc.ModelAngleToleranceRadians, doc.Notes
        });
        return result;
      }

      public void Dispose()
      {
        if (_disposed) return;
        _disposed = true;
        try
        {
          foreach (var layer in TemporaryLayers.Select(_doc.Layers.FindId).Where(layer => layer != null && !layer.IsDeleted).OrderByDescending(layer => layer!.Index))
            _doc.Layers.Delete(layer!.Index, quiet: true);
          if (_modified) _doc.Modified = true;
          else
          {
            var after = Snapshot(_doc);
            _doc.Modified = _before!.Count != after.Count || _before.Any(entry => !after.TryGetValue(entry.Key, out var value) || value != entry.Value);
          }
        }
        catch (Exception ex)
        {
          _doc.Modified = true;
          Log.Write("vExportDXF", $"could not verify unchanged document state: {ex}");
        }
        finally
        {
          _doc.UndoRecordingEnabled = _undoRecording;
          if (_previous != null) _documentStates[_doc.RuntimeSerialNumber] = _previous;
          else _documentStates.Remove(_doc.RuntimeSerialNumber);
        }
      }
    }

    private static Result OptimizeExportPieces(RhinoDoc doc, IReadOnlyList<RhinoObject> targets,
      Dictionary<Guid, List<ExportCurvePiece>> overrides, vCleanup.ExportCleanupAction action, ExportCleanupFeedback feedback,
      vCleanup.ExportGeometrySettings? geometrySettings=null,Func<int,int,string?,vCleanup.ExportCleanupAction?>? reviewFindings=null,bool isolateParts=DefaultIsolateCleanupParts)
    {
      using var copies = new ExportCleanupCopies(doc){IsolateParts=isolateParts};
      copies.Add(targets, overrides);
      doc.Objects.UnselectAll();
      var result = vCleanup.OptimizeExportCopies(doc, copies.ObjectIds, action,
        (ids, shortFindings) =>
        {
          if((ids.Count>0||shortFindings.Count>0)&&feedback.ReviewReady!=null&&geometrySettings is {} own)feedback.CaptureDeletionPreview(
            copies.ObjectIds.Select(doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false,Geometry:Curve}).Cast<RhinoObject>().OrderBy(obj=>obj.RuntimeSerialNumber).ToArray(),copies.SourceIds(copies.ObjectIds),own);
          feedback.SetFindings(copies.SourceIds(ids), shortFindings
            .Select(finding => (copies.SourceId(finding.SourceId), finding.Curve, finding.Point)).ToArray());
          feedback.ReviewReady?.Invoke(feedback);
        }, copies.Refresh,geometrySettings,reviewFindings,feedback.KeepResponsive,feedback.FindingsChanged);
      feedback.ConfigureTracking(targets.Where(obj => obj.Geometry is Curve).Select(obj => obj.Id), geometrySettings is {} own
        ?new vCleanup.LiveFindingSettings(own.Threshold,own.FindOverlaps,own.OverlapTolerance):vCleanup.CaptureLiveFindingSettings());
      if (result == Result.Success)
      {
        try { copies.CopyResults(overrides); }
        catch { feedback.SetSources(copies.SourceIds(copies.ObjectIds)); throw; }
      }
      else if (result == Result.Failure && !feedback.HasFindings) feedback.SetSources(copies.SourceIds(copies.ObjectIds));
      return result;
    }

    private sealed class ExportCleanupCopies : Rhino.Display.DisplayConduit, IDisposable
    {
      private readonly RhinoDoc _doc;
      private readonly bool _undoRecording;
      private readonly bool _modified;
      private readonly Dictionary<string, (Guid Source, ExportCurvePiece Piece)> _pieces = [];
      private readonly Dictionary<Guid, Guid> _sources = [];
      private readonly HashSet<Guid> _sourceIds = [];
      private bool _disposed;
      internal HashSet<Guid> ObjectIds { get; } = [];
      internal bool IsolateParts {get;init;}=DefaultIsolateCleanupParts;

      internal ExportCleanupCopies(RhinoDoc doc)
      {
        _doc = doc;
        _undoRecording = doc.UndoRecordingEnabled;
        _modified = doc.Modified;
        doc.UndoRecordingEnabled = false;
        RhinoDoc.AddRhinoObject += Added;
        Enabled = true;
      }

      internal void Add(IReadOnlyList<RhinoObject> targets, IReadOnlyDictionary<Guid, List<ExportCurvePiece>> overrides)
      {
        var partitions=new Dictionary<Guid,string>();
        if(IsolateParts)
        {
          var tables=NestTableMetadata.TableGroups(_doc);
          foreach(var source in targets)
          {
            int group=(source.Attributes.GetGroupList()??[]).Where(index=>!tables.Contains(index)&&_doc.Groups[index] is {IsDeleted:false}).Order().FirstOrDefault(-1);
            if(group>=0)partitions[source.Id]="group:"+_doc.Groups[group].Id.ToString("N");
          }
          var ungrouped=targets.Where(obj=>obj.Attributes.GroupCount==0&&!NestTableMetadata.IsCleanupDecoration(_doc,obj)).ToArray();
          var inferred=UngroupedExportParts(ungrouped,_doc.ModelAbsoluteTolerance);
          for(int index=0;index<inferred.Count;index++)foreach(var source in inferred[index])partitions[source.Id]="inferred:"+index.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var source in targets.Where(obj => obj.Geometry is Curve && !NestTableMetadata.IsCleanupDecoration(_doc, obj)))
        {
          _sourceIds.Add(source.Id);
          foreach (var piece in ExportPieces(source, overrides))
          {
            if (piece.Geometry is not Curve curve) continue;
            string key = Guid.NewGuid().ToString("N");
            _pieces.Add(key, (source.Id, new ExportCurvePiece(curve, piece.Attributes,
              overrides.TryGetValue(source.Id, out var prepared) && prepared.Any(item => ReferenceEquals(item.Geometry, curve) && item.Between))));
            using var attributes = piece.Attributes.Duplicate();
            attributes.ObjectId = Guid.NewGuid();
            attributes.RemoveFromAllGroups();
            attributes.Mode = ObjectMode.Normal;
            attributes.SetUserString(CleanupPieceKey, key);
            if(IsolateParts)attributes.SetUserString(CleanupPartitionKey,partitions.GetValueOrDefault(source.Id,"object:"+source.Id.ToString("N")));
            Guid id = _doc.Objects.AddCurve(curve, attributes);
            if (id == Guid.Empty)
              throw new InvalidOperationException("Could not create a temporary cleanup curve.");
            ObjectIds.Add(id);
            _sources[id] = source.Id;
          }
        }
      }

      private void Added(object? sender, RhinoObjectEventArgs e)
      {
        if (e.TheObject.Document is { } owner && owner.RuntimeSerialNumber != _doc.RuntimeSerialNumber ||
            e.TheObject.Attributes.GetUserString(CleanupPieceKey) is not { } key || !_pieces.TryGetValue(key, out var origin)) return;
        ObjectIds.Add(e.ObjectId);
        _sources[e.ObjectId] = origin.Source;
      }

      internal void Refresh()
      {
        foreach (var obj in _doc.Objects.GetObjectList(ObjectType.Curve))
          if (obj.Attributes.GetUserString(CleanupPieceKey) is { } key && _pieces.TryGetValue(key, out var origin))
          {
            ObjectIds.Add(obj.Id);
            _sources[obj.Id] = origin.Source;
          }
      }

      internal Guid[] SourceIds(IEnumerable<Guid> ids)
      {
        var requested = ids.ToArray();
        if (requested.Any(id => !_sources.ContainsKey(id))) Refresh();
        return requested.Where(_sources.ContainsKey).Select(id => _sources[id]).Distinct().ToArray();
      }

      internal Guid SourceId(Guid id) => _sources.GetValueOrDefault(id);

      internal void CopyResults(Dictionary<Guid, List<ExportCurvePiece>> overrides)
      {
        Refresh();
        var result = _sourceIds.ToDictionary(id => id, _ => new List<ExportCurvePiece>());
        try
        {
          foreach (Guid id in ObjectIds)
          {
            var obj = _doc.Objects.FindId(id);
            if (obj is not { IsDeleted: false, Geometry: Curve curve }) continue;
            if (obj.Attributes.GetUserString(CleanupPieceKey) is not { } key || !_pieces.TryGetValue(key, out var origin))
              throw new InvalidOperationException("A temporary cleanup curve lost its source association; export cancelled.");
            result[origin.Source].Add(origin.Piece with { Geometry = curve.DuplicateCurve() });
          }
        }
        catch
        {
          foreach (var list in result.Values) foreach (var piece in list) piece.Geometry.Dispose();
          throw;
        }
        foreach (var entry in result)
        {
          if (overrides.TryGetValue(entry.Key, out var previous)) foreach (var piece in previous) piece.Geometry.Dispose();
          overrides[entry.Key] = entry.Value;
        }
      }

      protected override void ObjectCulling(Rhino.Display.CullObjectEventArgs e)
      {
        if (e.RhinoDoc.RuntimeSerialNumber == _doc.RuntimeSerialNumber &&
            ObjectIds.Contains(e.RhinoObject is GripObject grip ? grip.OwnerId : e.RhinoObject.Id)) e.CullObject = true;
      }

      public void Dispose()
      {
        if (_disposed) return;
        _disposed = true;
        Refresh();
        RhinoDoc.AddRhinoObject -= Added;
        try
        {
          foreach (Guid id in ObjectIds)
          {
            if (_doc.Objects.FindId(id) is not { IsDeleted: false } obj) continue;
            if (obj.GripsOn) obj.GripsOn = false;
            if (!_doc.Objects.Delete(obj, quiet: true, ignoreModes: true) && !_doc.Objects.Purge(obj))
              throw new InvalidOperationException("Could not remove a temporary cleanup curve.");
          }
        }
        finally
        {
          Enabled = false;
          _doc.UndoRecordingEnabled = _undoRecording;
          _doc.Modified = _modified;
        }
      }
    }

    private sealed class ExportDeletionPreview : Rhino.Display.DisplayConduit,IDisposable
    {
      private readonly RhinoDoc _doc;
      private readonly HashSet<Guid> _originals;
      private readonly vCleanup.ExportGeometrySettings _settings;
      private readonly List<(Guid Id,uint Crc,string AttributesJson,Curve Curve,ObjectAttributes Attributes)> _sources=[];
      private readonly List<(int Index,string Path,Layer Layer)> _layers=[];
      private readonly List<(Curve Curve,System.Drawing.Color Color)> _remaining=[];
      private bool _built,_building,_wanted,_disposed;
      internal ExportDeletionPreview(RhinoDoc doc,IReadOnlyList<RhinoObject> objects,IEnumerable<Guid> originals,vCleanup.ExportGeometrySettings settings)
      {
        _doc=doc;_originals=originals.ToHashSet();_settings=settings;
        try
        {
          foreach(var obj in objects)_sources.Add((obj.Id,obj.Geometry.DataCRC(0),obj.Attributes.ToJSON(new SerializationOptions()),((Curve)obj.Geometry).DuplicateCurve(),obj.Attributes.Duplicate()));
          foreach(var layer in doc.Layers.Where(layer=>!layer.IsDeleted).OrderBy(layer=>layer.FullPath.Count(c=>c==':')))
          {var copy=new Layer();copy.CopyAttributesFrom(layer);copy.Name=layer.Name;copy.Id=layer.Id;copy.ParentLayerId=layer.ParentLayerId;_layers.Add((layer.Index,layer.FullPath,copy));}
        }
        catch{Dispose();throw;}
      }
      internal bool Matches(IReadOnlyList<RhinoObject> objects,vCleanup.ExportGeometrySettings settings)=>
        !_disposed&&_settings==settings&&objects.Count==_sources.Count&&objects.Select((obj,index)=>obj.Id==_sources[index].Id&&
          obj.Geometry.DataCRC(0)==_sources[index].Crc&&obj.Attributes.ToJSON(new SerializationOptions())==_sources[index].AttributesJson).All(same=>same);
      internal void SetVisible(bool visible,Action? keepResponsive)
      {
        if(_disposed)return;
        _wanted=visible;
        if(!visible){Enabled=false;_doc.Views.Redraw();return;}
        if(_building)return;
        if(!_built)
        {
          _building=true;
          try
          {
            using var shadow=RhinoDoc.CreateHeadless(null);
            shadow.ModelUnitSystem=_doc.ModelUnitSystem;shadow.ModelAbsoluteTolerance=_doc.ModelAbsoluteTolerance;shadow.ModelAngleToleranceRadians=_doc.ModelAngleToleranceRadians;
            var layers=new Dictionary<int,int>();var layerIds=new Dictionary<Guid,Guid>();
            foreach(var layer in _layers)
            {
              var copy=new Layer();copy.CopyAttributesFrom(layer.Layer);copy.Name=layer.Layer.Name;
              copy.ParentLayerId=layerIds.GetValueOrDefault(layer.Layer.ParentLayerId,Guid.Empty);
              int index=shadow.Layers.FindByFullPath(layer.Path,-1);
              if(index<0)index=shadow.Layers.Add(copy);
              copy.Dispose();if(index<0)throw new InvalidOperationException("Could not copy a preview layer.");
              layers[layer.Index]=index;layerIds[layer.Layer.Id]=shadow.Layers[index].Id;
            }
            var scope=new HashSet<Guid>();
            foreach(var source in _sources)
            {
              using var attributes=source.Attributes.Duplicate();attributes.LayerIndex=layers[source.Attributes.LayerIndex];attributes.RemoveFromAllGroups();
              var id=shadow.Objects.AddCurve(source.Curve,attributes);
              if(id!=source.Id)throw new InvalidOperationException("Could not preserve a preview curve's identity.");
              scope.Add(id);
            }
            // Replay the same cleanup on already-simplified snapshots; no live document objects participate.
            var result=vCleanup.OptimizeExportCopies(shadow,scope,vCleanup.ExportCleanupAction.Delete,(_,_)=>{},settings:_settings with {SimplifyCurves=false},
              reviewFindings:(_,_,_)=>vCleanup.ExportCleanupAction.Ignore,keepResponsive:keepResponsive,previewOnly:true);
            if(result!=Result.Success||_disposed)throw new InvalidOperationException("Could not calculate the deletion preview safely.");
            foreach(var obj in scope.Select(shadow.Objects.FindId).Where(obj=>obj is {IsDeleted:false,Geometry:Curve}).Cast<RhinoObject>())
              _remaining.Add((((Curve)obj.Geometry).DuplicateCurve(),obj.Attributes.DrawColor(shadow)));
            _built=true;
          }
          catch{foreach(var item in _remaining)item.Curve.Dispose();_remaining.Clear();throw;}
          finally{_building=false;}
        }
        Enabled=_wanted&&!_disposed;_doc.Views.Redraw();
      }
      protected override void ObjectCulling(Rhino.Display.CullObjectEventArgs e)
      {
        if(e.RhinoDoc.RuntimeSerialNumber==_doc.RuntimeSerialNumber&&_originals.Contains(e.RhinoObject is GripObject grip?grip.OwnerId:e.RhinoObject.Id))e.CullObject=true;
      }
      protected override void CalculateBoundingBox(Rhino.Display.CalculateBoundingBoxEventArgs e)
      {if(e.RhinoDoc.RuntimeSerialNumber==_doc.RuntimeSerialNumber)foreach(var item in _remaining)e.IncludeBoundingBox(item.Curve.GetBoundingBox(false));}
      protected override void PostDrawObjects(Rhino.Display.DrawEventArgs e)
      {if(e.RhinoDoc.RuntimeSerialNumber==_doc.RuntimeSerialNumber)foreach(var item in _remaining)PreviewDisplay.DrawCurve(e.Display,item.Curve,item.Color);}
      public void Dispose()
      {
        if(_disposed)return;_disposed=true;Enabled=false;
        foreach(var source in _sources){source.Curve.Dispose();source.Attributes.Dispose();}_sources.Clear();
        foreach(var layer in _layers)layer.Layer.Dispose();_layers.Clear();
        foreach(var item in _remaining)item.Curve.Dispose();_remaining.Clear();_doc.Views.Redraw();
      }
    }

    internal sealed class ExportCleanupFeedback(RhinoDoc doc) : IDisposable
    {
      internal Action? KeepResponsive {get;init;}
      internal Action<int,int>? FindingsChanged {get;init;}
      internal Action<ExportCleanupFeedback>? ReviewReady {get;init;}
      private readonly CleanupFindingDisplay _display = new(doc);
      private Guid[]? _scope;
      private vCleanup.LiveFindingSettings _settings;
      private bool _retained;
      private bool _disposed;
      private ExportDeletionPreview? _deletionPreview;
      internal bool HasFindings => _display.HasFindings;
      internal bool CanPreview=>!_disposed&&_deletionPreview!=null;
      internal void CaptureDeletionPreview(IReadOnlyList<RhinoObject> objects,IEnumerable<Guid> originals,vCleanup.ExportGeometrySettings settings)
      {
        if(_deletionPreview?.Matches(objects,settings)==true)return;
        _deletionPreview?.Dispose();_deletionPreview=null;
        _deletionPreview=new(doc,objects,originals,settings);
      }
      internal void SetHighlightsVisible(bool visible)=>_display.SetVisible(visible);
      internal void SetDeletionPreview(bool visible)
      {
        if(_disposed)return;
        _deletionPreview?.SetVisible(visible,KeepResponsive);
      }
      internal void SetSources(IEnumerable<Guid> ids) => SetFindings(ids, []);
      internal void SetFindings(IEnumerable<Guid> ids, IReadOnlyCollection<(Guid SourceId, Curve Curve, Point3d Point)> shortFindings)
        => _display.SetFindings(ids, shortFindings);
      internal void ConfigureTracking(IEnumerable<Guid> scope, vCleanup.LiveFindingSettings settings)
      { _scope = scope.ToArray(); _settings = settings; }
      internal void Retain()
      {
        if (_retained) return;
        _retained = true;
        _cleanupFeedback = this;
        _display.Disposed += Dispose;
        _display.Track(_scope ?? [], _scope == null ? vCleanup.CaptureLiveFindingSettings() : _settings);
      }
      public void Dispose()
      {
        if (_disposed) return;
        _disposed = true;
        _display.Disposed -= Dispose;
        _deletionPreview?.Dispose();_deletionPreview=null;
        _display.Dispose();
        if (ReferenceEquals(_cleanupFeedback, this)) _cleanupFeedback = null;
      }
    }

    private sealed class TemporaryBlockLayerScope : IDisposable
    {
      readonly RhinoDoc _doc;
      readonly Guid _layerId;
      readonly bool _visible;
      readonly bool _locked;
      bool _changed;

      public TemporaryBlockLayerScope(RhinoDoc doc, IReadOnlyCollection<Guid> exportIds)
      {
        _doc = doc;
        int index = doc.Layers.FindByFullPath(BlockInsertLayer, -1);
        if (index < 0) return;
        if (!exportIds.Any(id => doc.Objects.FindId(id) is InstanceObject instance && instance.Attributes.LayerIndex == index))
          return;
        var layer = doc.Layers[index];
        _layerId = layer.Id;
        _visible = layer.IsVisible;
        _locked = layer.IsLocked;
        if (_visible && !_locked) return;
        _changed = true;
        try
        {
          layer.IsVisible = true;
          layer.IsLocked = false;
          if (!doc.Layers.Modify(layer, index, quiet: true))
            throw new InvalidOperationException($"Could not make temporary export blocks selectable on layer '{layer.FullPath}'.");
        }
        catch { Dispose(); throw; }
        Log.Write("vExportDXF", $"temporary block layer made selectable layer={layer.FullPath} visible={_visible} locked={_locked}");
      }

      public void Dispose()
      {
        if (!_changed) return;
        var layer = _doc.Layers.FindId(_layerId);
        if (layer == null || layer.IsDeleted) return;
        layer.IsVisible = _visible;
        layer.IsLocked = _locked;
        if (!_doc.Layers.Modify(layer, layer.Index, quiet: true))
        {
          Log.Write("vExportDXF", $"could not restore temporary block layer state layer={layer.FullPath}");
          return;
        }
        _changed = false;
      }
    }

    private static RhinoObject[] FilterExportGrain(RhinoDoc doc, IReadOnlyList<RhinoObject> targets, bool removeGrain)
    {
      if (!removeGrain) return targets.ToArray();
      return targets.Where(obj=>!IsExportGrain(doc,obj)).ToArray();
    }

    internal static bool IsExportGrain(RhinoDoc doc,RhinoObject obj)=>obj.Geometry is Curve {IsClosed:false}&&
      (string.Equals(obj.Attributes.Name?.Trim(),GrainIdentifier,StringComparison.OrdinalIgnoreCase)||
       string.Equals(doc.Layers.FindIndex(obj.Attributes.LayerIndex)?.Name.Trim(),GrainIdentifier,StringComparison.OrdinalIgnoreCase));

    private static ObjectAttributes ExportAttributes(ObjectAttributes source)
    {
      var attributes = source.Duplicate();
      attributes.ObjectId = Guid.NewGuid();
      attributes.RemoveFromAllGroups();
      // Long source-object lists make Rhino's DXF writer skip otherwise valid geometry.
      foreach (string? key in attributes.GetUserStrings().AllKeys)
        if (key?.StartsWith(NestMetadataPrefix, StringComparison.OrdinalIgnoreCase) == true)
          attributes.SetUserString(key, null);
      return attributes;
    }

    private static void VerifyNativeExport(string before, string current)
    {
      if (!current.StartsWith(before, StringComparison.Ordinal))
      {
        Log.Write("vExportDXF", "native export totals unavailable: command history was replaced during export");
        throw new InvalidOperationException("Could not verify Rhino's export result; the destination was not replaced.");
      }
      string output = current[before.Length..];
      long skipped = NativeSkippedCounts.Matches(output).Cast<Match>()
        .Sum(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
      if (skipped == 0 && !output.Contains("Objects were skipped in export.", StringComparison.OrdinalIgnoreCase) &&
          !output.Contains("Error converting ", StringComparison.OrdinalIgnoreCase)) return;
      Log.Write("vExportDXF", "native export rejected: {0}", output);
      throw new InvalidOperationException(skipped > 0
        ? $"Rhino skipped {CountText.Format(skipped, "object")}; the incomplete export was discarded and the destination was not replaced."
        : "Rhino reported a conversion failure; the incomplete export was discarded and the destination was not replaced.");
    }

    private static ExportSettings LoadSettings(RhinoDoc doc)
    {
      var legacy = ToolsOptionStore.Read(LegacySettingsSection, section =>
        new ExportSettings(
          ReadTrim(section, ConfiguredDefaultNotchTrim),
          ReadLayer(section, ConfiguredDefaultNotchSplitLayer),
          ConfiguredDefaultNotchJoin,
          ConfiguredDefaultTextBreak,
          ReadOptimize(section, ConfiguredDefaultOptimize),ConfiguredDefaultExplode));
      var current = ToolsOptionStore.Read(SettingsSection, section =>
        new ExportSettings(
          ReadTrim(section, legacy.NotchTrim),
          ReadLayer(section, legacy.NotchSplitLayer),
          ReadJoin(section, legacy.NotchJoin),
          ReadTextBreak(section, legacy.TextBreak),
          ReadOptimize(section, legacy.Optimize),ReadExplode(section,legacy.Explode),
          ReadCleanupAction(section, ConfiguredDefaultCleanupAction),
          ToolsOptionStore.TryGetBool(section, "remove_grain", out bool removeGrain) ? removeGrain : ConfiguredDefaultRemoveGrain,
          ToolsOptionStore.TryGetBool(section, "move_origin", out bool moveOrigin) ? moveOrigin : ConfiguredDefaultMoveOrigin,
          ToolsOptionStore.TryGetDouble(section, "end_trim", out double endTrim) && double.IsFinite(endTrim) && endTrim >= 0 ? endTrim : ConfiguredDefaultEndTrim));
      try
      {
        var trim = doc.Strings.GetValue(DocumentSection, NotchTrimEntry)
          ?? doc.Strings.GetValue(DocumentSection, LegacyNotchTrimEntry);
        var layer = doc.Strings.GetValue(DocumentSection, NotchSplitLayerEntry)
          ?? doc.Strings.GetValue(DocumentSection, PriorNotchTrimLayerEntry)
          ?? doc.Strings.GetValue(DocumentSection, LegacyNotchTrimLayerEntry);
        var explode = doc.Strings.GetValue(DocumentSection, ExplodeEntry);
        var join = doc.Strings.GetValue(DocumentSection, NotchJoinEntry);
        var textBreak = doc.Strings.GetValue(DocumentSection, TextBreakEntry);
        var removeGrain = doc.Strings.GetValue(DocumentSection, RemoveGrainEntry);
        var moveOrigin = doc.Strings.GetValue(DocumentSection, MoveOriginEntry);
        var endTrim = doc.Strings.GetValue(DocumentSection, EndTrimEntry);
        var optimize = doc.Strings.GetValue(DocumentSection, OptimizeEntry)
          ?? doc.Strings.GetValue(DocumentSection, LegacyOptimizeEntry);
        return new ExportSettings(
          Enum.TryParse(trim, true, out NotchTrimMode parsed) && Enum.IsDefined(parsed)
            ? parsed : current.NotchTrim,
          !string.IsNullOrWhiteSpace(layer) ? layer : current.NotchSplitLayer,
          bool.TryParse(join, out bool parsedJoin)
            ? parsedJoin : current.NotchJoin,
          bool.TryParse(textBreak, out bool parsedTextBreak)
            ? parsedTextBreak : current.TextBreak,
          bool.TryParse(optimize, out bool parsedOptimize)
            ? parsedOptimize : current.Optimize,
          Enum.TryParse(explode,true,out ExportExplodeMode parsedExplode)&&Enum.IsDefined(parsedExplode)?parsedExplode:current.Explode,
          current.CleanupAction,
          bool.TryParse(removeGrain, out bool parsedRemoveGrain) ? parsedRemoveGrain : current.RemoveGrain,
          bool.TryParse(moveOrigin, out bool parsedMoveOrigin) ? parsedMoveOrigin : current.MoveOrigin,
          double.TryParse(endTrim, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedEndTrim) && double.IsFinite(parsedEndTrim) && parsedEndTrim >= 0 ? parsedEndTrim : current.EndTrim);
      }
      catch (Exception ex)
      {
        Log.Write("vExportDXF", $"document options read failed: {ex}");
        return current;
      }
    }

    private static NotchTrimMode ReadTrim(
      System.Text.Json.Nodes.JsonObject? section, NotchTrimMode fallback) =>
      ToolsOptionStore.TryGetString(section, "notch_trim", out var value) &&
      Enum.TryParse(value, true, out NotchTrimMode mode) && Enum.IsDefined(mode)
        ? mode : fallback;

    private static string ReadLayer(
      System.Text.Json.Nodes.JsonObject? section, string fallback)
    {
      if(ToolsOptionStore.TryGetString(section,"notch_split_layer",out var value)&&!string.IsNullOrWhiteSpace(value)) return value;
      return ToolsOptionStore.TryGetString(section,"notch_trim_layer",out value)&&!string.IsNullOrWhiteSpace(value)?value:fallback;
    }

    private static ExportExplodeMode ReadExplode(System.Text.Json.Nodes.JsonObject? section,ExportExplodeMode fallback)=>
      ToolsOptionStore.TryGetString(section,"explode",out var value)&&Enum.TryParse(value,true,out ExportExplodeMode mode)&&Enum.IsDefined(mode)?mode:fallback;

    private static bool ReadOptimize(
      System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
      ToolsOptionStore.TryGetBool(section, "optimize", out bool value)
        ? value : fallback;

    private static vCleanup.ExportCleanupAction ReadCleanupAction(
      System.Text.Json.Nodes.JsonObject? section, vCleanup.ExportCleanupAction fallback) =>
      ToolsOptionStore.TryGetString(section, "cleanup_action", out var value) &&
      Enum.TryParse(value, true, out vCleanup.ExportCleanupAction action) && Enum.IsDefined(action)
        ? action : fallback;

    private static bool ReadJoin(
      System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
      ToolsOptionStore.TryGetBool(section, "notch_join", out bool value)
        ? value : fallback;

    private static bool ReadTextBreak(
      System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
      ToolsOptionStore.TryGetBool(section, "text_break", out bool value)
        ? value : fallback;

    private static Result GetRunOptions(RhinoDoc doc, ref ExportSettings settings)
    {
      bool saveDefaults = ConfiguredDefaultSaveDefaults;
      while (true)
      {
        using var getter = new GetOption();
        getter.SetCommandPrompt("DXF export options; Enter to prepare export");
        getter.AcceptNothing(true);
        int trimIndex = getter.AddOptionList(
          "NotchTrim", NotchTrimNames, (int)settings.NotchTrim);
        int layerIndex = getter.AddOption("NotchSplitLayer", settings.NotchSplitLayer);
        int explodeIndex = getter.AddOptionList("Explode",ExplodeNames,(int)settings.Explode);
        var endTrim = new OptionDouble(settings.EndTrim, 0, double.MaxValue);
        getter.AddOptionDouble("EndTrim", ref endTrim);
        var join = new OptionToggle(settings.NotchJoin, "No", "Yes");
        var textBreak = new OptionToggle(settings.TextBreak, "No", "Yes");
        var removeGrain = new OptionToggle(settings.RemoveGrain, "No", "Yes");
        var moveOrigin = new OptionToggle(settings.MoveOrigin, "No", "Yes");
        var optimize = new OptionToggle(settings.Optimize, "No", "Yes");
        var save = new OptionToggle(saveDefaults, "No", "Yes");
        getter.AddOptionToggle("NotchJoin", ref join);
        getter.AddOptionToggle("TextBreak", ref textBreak);
        getter.AddOptionToggle("RemoveGrain", ref removeGrain);
        getter.AddOptionToggle("MoveOrigin", ref moveOrigin);
        getter.AddOptionToggle("Optimize", ref optimize);
        int cleanupIndex = getter.AddOptionList("CleanupAction", CleanupActionNames, (int)settings.CleanupAction);
        getter.AddOptionToggle("SaveDefaults", ref save);
        var result = getter.Get();
        if (result == GetResult.Cancel)
          return Result.Cancel;
        if (result == GetResult.Nothing)
        {
          if (saveDefaults && !SaveSettings(doc, settings))
            RhinoApp.WriteLine("vExportDXF: could not save defaults; see vTools log.");
          return Result.Success;
        }
        if (result != GetResult.Option)
          return getter.CommandResult();
        settings = settings with
        {
          NotchJoin = join.CurrentValue,
          TextBreak = textBreak.CurrentValue,
          RemoveGrain = removeGrain.CurrentValue,
          MoveOrigin = moveOrigin.CurrentValue,
          Optimize = optimize.CurrentValue,
          EndTrim = endTrim.CurrentValue
        };
        saveDefaults = save.CurrentValue;
        if (getter.Option()?.Index == trimIndex)
          settings = settings with
            { NotchTrim = (NotchTrimMode)getter.Option()!.CurrentListOptionIndex };
        else if(getter.Option()?.Index==explodeIndex)
          settings=settings with {Explode=(ExportExplodeMode)getter.Option()!.CurrentListOptionIndex};
        else if (getter.Option()?.Index == cleanupIndex)
          settings = settings with { CleanupAction = (vCleanup.ExportCleanupAction)getter.Option()!.CurrentListOptionIndex };
        else if (getter.Option()?.Index == layerIndex)
        {
          string layer = settings.NotchSplitLayer;
          if (RhinoGet.GetString("Notch split destination layer", false, ref layer) == Result.Success &&
              !string.IsNullOrWhiteSpace(layer))
            settings = settings with { NotchSplitLayer = layer.Trim() };
        }
      }
    }

    private static bool SaveSettings(RhinoDoc doc, ExportSettings settings)
    {
      bool globalSaved = ToolsOptionStore.Update(SettingsSection, section =>
      {
        section["notch_trim"] = settings.NotchTrim.ToString();
        section["notch_split_layer"] = settings.NotchSplitLayer;
        section["explode"] = settings.Explode.ToString();
        section["notch_join"] = settings.NotchJoin;
        section["text_break"] = settings.TextBreak;
        section["remove_grain"] = settings.RemoveGrain;
        section["move_origin"] = settings.MoveOrigin;
        section["end_trim"] = settings.EndTrim;
        section["optimize"] = settings.Optimize;
        section["cleanup_action"] = settings.CleanupAction.ToString();
      });
      try
      {
        doc.Strings.SetString(DocumentSection, NotchTrimEntry, settings.NotchTrim.ToString());
        doc.Strings.SetString(DocumentSection, NotchSplitLayerEntry, settings.NotchSplitLayer);
        doc.Strings.SetString(DocumentSection, ExplodeEntry, settings.Explode.ToString());
        doc.Strings.SetString(DocumentSection, NotchJoinEntry, settings.NotchJoin.ToString());
        doc.Strings.SetString(DocumentSection, TextBreakEntry, settings.TextBreak.ToString());
        doc.Strings.SetString(DocumentSection, RemoveGrainEntry, settings.RemoveGrain.ToString());
        doc.Strings.SetString(DocumentSection, MoveOriginEntry, settings.MoveOrigin.ToString());
        doc.Strings.SetString(DocumentSection, EndTrimEntry, settings.EndTrim.ToString("R", CultureInfo.InvariantCulture));
        doc.Strings.SetString(DocumentSection, OptimizeEntry, settings.Optimize.ToString());
        return globalSaved;
      }
      catch (Exception ex)
      {
        Log.Write("vExportDXF", $"document options save failed: {ex}");
        return false;
      }
    }

    private static bool TryChoosePath(RhinoDoc doc, out string path)
    {
      path = string.Empty;
      string? previous = doc.Strings.GetValue(LastPathKey);
      string previousDirectory = !string.IsNullOrWhiteSpace(previous)
        ? Path.GetDirectoryName(previous) ?? string.Empty : string.Empty;
      string documentDirectory = !string.IsNullOrWhiteSpace(doc.Path)
        ? Path.GetDirectoryName(doc.Path) ?? string.Empty : string.Empty;
      string directory = Directory.Exists(previousDirectory)
        ? previousDirectory : documentDirectory;
      string filename = !string.IsNullOrWhiteSpace(previous)
        ? Path.GetFileName(previous)
        : (Path.GetFileNameWithoutExtension(doc.Name) is { Length: > 0 } stem
          ? stem : ConfiguredDefaultFileStem) + DxfExtension;
      var dialog = new SaveFileDialog
      {
        Title = "Export DXF",
        Filter = "DXF Files (*.dxf)|*.dxf||",
        DefaultExt = "dxf",
        FileName = filename,
        InitialDirectory = directory,
      };
      if (!dialog.ShowSaveDialog() || string.IsNullOrWhiteSpace(dialog.FileName))
        return false;
      path = NormalizeDxfPath(dialog.FileName);
      return true;
    }

    private static string NormalizeDxfPath(string path)
    {
      string fullPath = Path.GetFullPath(path);
      return string.Equals(Path.GetExtension(fullPath), DxfExtension,
          StringComparison.OrdinalIgnoreCase)
        ? fullPath : Path.ChangeExtension(fullPath, DxfExtension);
    }

    private static string MakeTemporaryPath(string destination)
    {
      string directory = Path.GetDirectoryName(destination)
        ?? throw new IOException("The export path has no directory.");
      if (!Directory.Exists(directory))
        throw new DirectoryNotFoundException(directory);
      string stem = Path.GetFileNameWithoutExtension(destination);
      for (int attempt = 0; attempt < MaximumTemporaryNameAttempts; attempt++)
      {
        string path = Path.Combine(directory,
          $"{stem}_rhino_export_{Guid.NewGuid():N}{DxfExtension}");
        if (!File.Exists(path))
          return path;
      }
      throw new IOException("Could not find a unique temporary DXF filename.");
    }

    private static bool WaitForFile(string path)
    {
      var watch = Stopwatch.StartNew();
      while (watch.ElapsedMilliseconds < ConfiguredExportWaitMilliseconds)
      {
        if (File.Exists(path) && new FileInfo(path).Length > 0)
          return true;
        Thread.Sleep(ConfiguredExportPollMilliseconds);
      }
      return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static void CopyExportFile(string source, string destination)
    {
      using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
      input.CopyTo(output, ExportCopyBufferSize);
      output.Flush(flushToDisk: true);
    }

    private static Transform OriginTranslation(IReadOnlyList<RhinoObject> objects)
    {
      BoundingBox bounds = BoundingBox.Empty;
      foreach (var obj in objects)
        bounds.Union(obj.Geometry.GetBoundingBox(true));
      return bounds.IsValid
        ? Transform.Translation(Point3d.Origin - bounds.Min)
        : Transform.Identity;
    }

    private static List<Guid> PrepareExportObjects(RhinoDoc doc,
      IReadOnlyList<RhinoObject> targets, Transform translation,
      IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
      IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides,
      List<Guid> temporaryObjects, List<int> temporaryDefinitions,
      List<PreparedExportEntry>? preparedEntries = null)
    {
      var targetById = targets.ToDictionary(obj => obj.Id);
      var assigned = targets.Where(obj => NestTableMetadata.IsTableStructure(doc, obj)).Select(obj => obj.Id).ToHashSet();
      var exportIds = new List<Guid>();
      var nestTables=NestTableMetadata.TableGroups(doc);
      var tableAnnotations = targets.Where(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey)
        is NestTableMetadata.LabelRole or NestTableMetadata.LabelFrameRole or NestTableMetadata.BorderRole or NestTableMetadata.LabelDividerRole)
        .GroupBy(obj => (obj.Attributes.GetUserString(NestTableMetadata.GroupIdKey),
          obj.Attributes.GetUserString("vNest.session"), obj.Attributes.GetUserString("vNest.table")));
      foreach (var annotations in tableAnnotations)
      {
        var layoutMembers = annotations.ToList();
        var members = layoutMembers.Where(obj => !NestTableMetadata.IsTableStructure(doc, obj)).ToList();
        bool header=layoutMembers.Any(obj=>NestTableMetadata.IsEnclosedHeader(obj.Geometry));
        if(header)members.RemoveAll(obj=>obj.Attributes.GetUserString(NestTableMetadata.RoleKey)==NestTableMetadata.LabelFrameRole);
        if (members.Count == 0) continue;
        if (!members.Any(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelFrameRole))
        {
          BoundingBox bounds = BoundingBox.Empty;
          double height = 0;
          var framePlane = Plane.WorldXY;
          foreach (var member in members)
          {
            if (member.Geometry is TextEntity text)
            {
              using var copy = CreateTableExportText(doc, text);
              var glyphs = ExportTableTextBounds(doc, copy, Plane.WorldXY);
              bounds.Union(glyphs);
              height = Math.Max(height, glyphs.Diagonal.Y);
            }
            else bounds.Union(member.Geometry.GetBoundingBox(true));
          }
          if (!bounds.IsValid)
            throw new InvalidOperationException("Could not frame table annotations for export.");
          double padding = Math.Max(doc.ModelAbsoluteTolerance, height * ConfiguredTableLabelFramePaddingFraction);
          bounds.Inflate(padding);
          var texts = members.Select(obj => obj.Geometry).OfType<TextEntity>().ToArray();
          if (texts.Length > 0 && TryGetTableAnnotationArea(layoutMembers, texts[0].Plane, out var tabPlane, out var tabArea))
          { framePlane = tabPlane; bounds = tabArea; }
          if(header)
          {
            var copies=texts.Select(text=>CreateTableExportText(doc,text)).ToArray();
            try
            {
              if(!TryGetTableAnnotationArea(layoutMembers,texts[0].Plane,out framePlane,out bounds))
              {
                framePlane=texts[0].Plane;bounds=BoundingBox.Empty;
                foreach(var text in copies)bounds.Union(ExportTableTextBounds(doc,text,framePlane));
                bounds.Inflate(padding);
              }
              bounds=CompactHeaderAnnotations(doc,copies,framePlane,bounds);
            }
            finally{foreach(var text in copies)text.Dispose();}
          }
          using var frame = new Rectangle3d(framePlane,
            new Interval(bounds.Min.X, bounds.Max.X), new Interval(bounds.Min.Y, bounds.Max.Y)).ToNurbsCurve();
          if(header)frame.SetUserString(NestTableMetadata.LayoutKey,NestTableMetadata.EnclosedHeaderLayout);
          var attributes = members[0].Attributes.Duplicate();
          attributes.ObjectId = Guid.NewGuid();
          attributes.RemoveFromAllGroups();
          attributes.SetUserString(NestTableMetadata.RoleKey, NestTableMetadata.LabelFrameRole);
          Guid frameId = doc.Objects.AddCurve(frame, attributes);
          if (frameId == Guid.Empty)
            throw new InvalidOperationException("Could not add the temporary table-label frame.");
          temporaryObjects.Add(frameId);
          members.Add(doc.Objects.FindId(frameId));
        }
        string name = "vNest_" + annotations.Key.Item2 + "_table_" + annotations.Key.Item3 + "_label";
        var tableText = PrepareTableAnnotations(doc, members, textBreakOverrides);
        Guid labelInstance;
        try
        {
          if (!TryCreateGroupBlock(doc, name, members, TableLabelExportTranslation(doc, members, translation), notchTrimOverrides, tableText,
              temporaryObjects, temporaryDefinitions, out labelInstance))
            throw new InvalidOperationException("Could not prepare the table-label block.");
        }
        finally { DisposeTextBreakOverrides(tableText); }
        exportIds.Add(labelInstance);
        preparedEntries?.Add(new PreparedExportEntry(labelInstance,
          annotations.Select(obj => obj.Id).ToArray(), name, true));
        foreach (var annotation in annotations)
          assigned.Add(annotation.Id);
      }
      for (int groupIndex = 0; groupIndex < doc.Groups.Count; groupIndex++)
      {
        if(nestTables.Contains(groupIndex)) continue;
        if (doc.Groups[groupIndex] is not { IsDeleted: false }) continue;
        string? groupName = doc.Groups.GroupName(groupIndex);
        if (string.IsNullOrWhiteSpace(groupName))
          groupName = UnnamedGroupPrefix + groupIndex.ToString(CultureInfo.InvariantCulture);
        var members = doc.Groups.GroupMembers(groupIndex)
          .Where(obj => obj != null && targetById.ContainsKey(obj.Id) &&
                        !assigned.Contains(obj.Id)).ToArray();
        if (members.Length == 0)
          continue;
        if (!TryCreateGroupBlock(doc, groupName, members, translation, notchTrimOverrides, textBreakOverrides,
              temporaryObjects, temporaryDefinitions, out Guid instanceId))
          throw new InvalidOperationException($"Could not prepare group '{groupName}' as a DXF block.");
        exportIds.Add(instanceId);
        preparedEntries?.Add(new PreparedExportEntry(instanceId,
          members.Select(obj => obj.Id).ToArray(), groupName, false));
        foreach (var member in members)
          assigned.Add(member.Id);
      }

      var ungrouped = targets.Where(obj => !assigned.Contains(obj.Id) &&
        obj.Attributes.GroupCount == 0 && !NestTableMetadata.IsCleanupDecoration(doc, obj)).ToArray();
      var inferredParts = UngroupedExportParts(ungrouped, doc.ModelAbsoluteTolerance);
      for (int index = 0; index < inferredParts.Count; index++)
      {
        var members = inferredParts[index];
        string name = UnnamedGroupPrefix + (doc.Groups.Count + index).ToString(CultureInfo.InvariantCulture);
        if (!TryCreateGroupBlock(doc, name, members, translation, notchTrimOverrides, textBreakOverrides,
              temporaryObjects, temporaryDefinitions, out Guid instanceId))
          throw new InvalidOperationException("Could not prepare an ungrouped part as a DXF block.");
        exportIds.Add(instanceId);
        preparedEntries?.Add(new PreparedExportEntry(instanceId,
          members.Select(obj => obj.Id).ToArray(), name, false));
        foreach (var member in members)
          assigned.Add(member.Id);
      }
      if (inferredParts.Count > 0)
        Log.Write("vExportDXF", $"prepared ungrouped part blocks={inferredParts.Count} objects={inferredParts.Sum(part => part.Count)}");

      foreach (var source in targets)
      {
        if (assigned.Contains(source.Id))
          continue;
        foreach (var piece in PreparedExportPieces(source, notchTrimOverrides, textBreakOverrides))
        {
          using var geometry = piece.Geometry.Duplicate();
          if (geometry == null || !geometry.Transform(translation))
            throw new InvalidOperationException($"Could not copy object {source.Id} for export.");
          using var attributes = ExportAttributes(piece.Attributes);
          Guid copyId = doc.Objects.Add(geometry, attributes);
          if (copyId == Guid.Empty)
            throw new InvalidOperationException($"Could not add temporary copy of {source.Id}.");
          temporaryObjects.Add(copyId);
          exportIds.Add(copyId);
          preparedEntries?.Add(new PreparedExportEntry(copyId, [source.Id], null, false));
        }
      }
      return exportIds;
    }

    private static List<List<RhinoObject>> UngroupedExportParts(IReadOnlyList<RhinoObject> objects, double tolerance)
    {
      var outer = ExportOuterLoops(objects.Select(obj => obj.Geometry).OfType<Curve>(), tolerance);
      try
      {
        var members = outer.Select(_ => new List<RhinoObject>()).ToArray();
        foreach (var obj in objects)
        {
          var owners = Enumerable.Range(0, outer.Count).Where(index =>
            ExportPartContains(outer[index].Outline, outer[index].Plane, obj.Geometry, tolerance)).ToArray();
          if (owners.Length == 1) members[owners[0]].Add(obj);
        }
        return members.Where(part => part.Any(obj => obj.Geometry is Curve)).ToList();
      }
      finally { foreach (var region in outer) region.Outline.Dispose(); }
    }

    private static List<(Curve Outline, Plane Plane)> ExportOuterLoops(IEnumerable<Curve> curves, double tolerance)
    {
      var source = curves.ToArray();
      if (source.Length == 0) return [];
      var joined = Curve.JoinCurves(source, tolerance, preserveDirection: false) ?? [];
      try
      {
        var regions = new List<(Curve Outline, Plane Plane, double Area)>();
        foreach (var curve in joined)
        {
          if (!curve.IsClosed || !curve.TryGetPlane(out var plane, tolerance)) continue;
          using var mass = AreaMassProperties.Compute(curve);
          if (mass?.Area > tolerance * tolerance) regions.Add((curve, plane, mass.Area));
        }
        // Nested closed curves are holes or internal seams, not additional part outlines.
        return regions.Where(region => !regions.Any(other => other.Area > region.Area &&
          region.Outline.IsInPlane(other.Plane, tolerance) && Curve.PlanarClosedCurveRelationship(
            region.Outline, other.Outline, other.Plane, tolerance) == RegionContainment.AInsideB))
          .Select(region => (region.Outline.DuplicateCurve(), region.Plane)).ToList();
      }
      finally { foreach (var curve in joined) curve.Dispose(); }
    }

    private static bool IsExportCuttingLayer(RhinoDoc doc, ObjectAttributes attributes)
    {
      string prefix = string.IsNullOrWhiteSpace(ConfiguredCutLayerPrefix) ? CutLayerPrefix : ConfiguredCutLayerPrefix.Trim();
      return doc.Layers.FindIndex(attributes.LayerIndex)?.Name.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void ApplyNonCutEndTrim(RhinoDoc doc, IReadOnlyList<RhinoObject> targets,
      Dictionary<Guid, List<ExportCurvePiece>> overrides, double distance)
    {
      if (!(distance > 0) || !double.IsFinite(distance)) return;
      double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      var tables = NestTableMetadata.TableGroups(doc);
      var scopes = targets.Where(obj => obj.Geometry is Curve && !NestTableMetadata.IsCleanupDecoration(doc, obj) &&
          obj.Attributes.GetUserString(NotchMetadataPrefix + "object_role") != NotchRole)
        .GroupBy(obj => (obj.Attributes.GetGroupList() ?? []).Where(index => !tables.Contains(index) &&
          doc.Groups[index] is { IsDeleted: false }).Order().FirstOrDefault(-1));
      int trimmedEnds = 0, shortCurves = 0;
      foreach (var scope in scopes)
      {
        var outlines = ExportOuterLoops(scope.Where(obj => IsExportCuttingLayer(doc, obj.Attributes))
          .Select(obj => (Curve)obj.Geometry), tolerance);
        try
        {
          if (outlines.Count == 0) continue;
          bool Touches(Point3d point) => outlines.Any(region => region.Outline.ClosestPoint(point, out double parameter) &&
            region.Outline.PointAt(parameter).DistanceTo(point) <= tolerance);
          foreach (var source in scope.Where(obj => !IsExportCuttingLayer(doc, obj.Attributes) && obj.Geometry is Curve { IsClosed: false }))
          {
            var originals = overrides.TryGetValue(source.Id, out var existing) ? existing :
              [new ExportCurvePiece((Curve)source.Geometry, source.Attributes)];
            var replacement = new List<ExportCurvePiece>();
            bool changed = false;
            try
            {
              foreach (var piece in originals)
              {
                bool eligible = !piece.Between && !piece.Geometry.IsClosed && !IsExportCuttingLayer(doc, piece.Attributes);
                bool start = eligible && Touches(piece.Geometry.PointAtStart), end = eligible && Touches(piece.Geometry.PointAtEnd);
                Curve? trimmed = null;
                if (start || end)
                {
                  double length = piece.Geometry.GetLength();
                  double first = start ? distance : 0, last = length - (end ? distance : 0);
                  if (last > first + tolerance && piece.Geometry.LengthParameter(first, out double t0) &&
                      piece.Geometry.LengthParameter(last, out double t1)) trimmed = piece.Geometry.Trim(t0, t1);
                  if (trimmed == null) shortCurves++;
                  else { changed = true; trimmedEnds += (start ? 1 : 0) + (end ? 1 : 0); }
                }
                replacement.Add(piece with { Geometry = trimmed ?? piece.Geometry.DuplicateCurve() });
              }
              if (!changed) { foreach (var piece in replacement) piece.Geometry.Dispose(); continue; }
              overrides[source.Id] = replacement;
              if (existing != null) foreach (var piece in existing) piece.Geometry.Dispose();
            }
            catch { foreach (var piece in replacement) piece.Geometry.Dispose(); throw; }
          }
        }
        finally { foreach (var region in outlines) region.Outline.Dispose(); }
      }
      Log.Write("vExportDXF", $"EndTrim={distance} shortenedEnds={trimmedEnds} untrimmableCurves={shortCurves}");
      if (shortCurves > 0)
        RhinoApp.WriteLine($"vExportDXF: EndTrim left {CountText.Format(shortCurves, "curve")} unchanged because the requested trim would consume or could not trim them.");
    }

    private static bool ExportPartContains(Curve outline, Plane plane, GeometryBase geometry, double tolerance)
    {
      bool Contains(Point3d point) => Math.Abs(plane.DistanceTo(point)) <= tolerance &&
        outline.Contains(point, plane, tolerance) is PointContainment.Inside or PointContainment.Coincident;
      if (geometry is not Curve curve)
        return geometry is AnnotationBase or TextDot or Rhino.Geometry.Point &&
          Contains(PlanarPartSelection.RepresentativePoint(geometry));
      if (!curve.IsInPlane(plane, tolerance) || !Contains(curve.PointAtStart) || !Contains(curve.PointAtEnd))
        return false;
      // Test every interval between crossings so an escaping curve is never assigned by its midpoint alone.
      var parameters = new List<double> { curve.Domain.T0, curve.Domain.T1 };
      using var intersections = Rhino.Geometry.Intersect.Intersection.CurveCurve(curve, outline, tolerance, tolerance);
      if (intersections != null)
        foreach (var hit in intersections)
        {
          parameters.Add(hit.ParameterA);
          if (hit.IsOverlap) parameters.Add(hit.OverlapA.T1);
        }
      parameters.Sort();
      for (int index = 1; index < parameters.Count; index++)
        if (!Contains(curve.PointAt((parameters[index - 1] + parameters[index]) / 2))) return false;
      return true;
    }

    private static Transform TableLabelExportTranslation(RhinoDoc doc, IReadOnlyList<RhinoObject> members, Transform translation)
    {
      var text = members.Select(obj => obj.Geometry).OfType<TextEntity>().FirstOrDefault();
      if (text == null) return translation;
      double gap = TableLabelExportGapInches * RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);
      bool header=members.Any(obj=>NestTableMetadata.IsEnclosedHeader(obj.Geometry));
      return translation * Transform.Translation((header?text.Plane.YAxis:-text.Plane.XAxis)*gap);
    }

    private static bool TryCreateGroupBlock(RhinoDoc doc, string groupName,
      IReadOnlyList<RhinoObject> members, Transform translation,
      IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
      IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides,
      List<Guid> temporaryObjects, List<int> temporaryDefinitions,
      out Guid instanceId)
    {
      instanceId = Guid.Empty;
      var geometries = new List<GeometryBase>();
      var attributes = new List<ObjectAttributes>();
      try
      {
        BoundingBox bounds = BoundingBox.Empty;
        foreach (var member in members)
        {
          foreach (var piece in PreparedExportPieces(member, notchTrimOverrides, textBreakOverrides))
          {
            var geometry = piece.Geometry.Duplicate();
            if (geometry == null)
              return false;
            geometries.Add(geometry);
            bounds.Union(geometry.GetBoundingBox(true));
            var attrs = ExportAttributes(piece.Attributes);
            attributes.Add(attrs);
          }
        }
        if (geometries.Count == 0)
          return false;
        Point3d basePoint = bounds.IsValid ? bounds.Min : Point3d.Origin;
        string blockName = UniqueBlockName(doc, groupName);
        int definitionIndex = doc.InstanceDefinitions.Add(
          blockName, string.Empty, basePoint, geometries, attributes);
        if (definitionIndex < 0)
          return false;
        temporaryDefinitions.Add(definitionIndex);

        int layerIndex = EnsureExportLayer(doc, BlockInsertLayer);
        var instanceAttributes = new ObjectAttributes
        {
          ObjectId = Guid.NewGuid(),
          LayerIndex = layerIndex,
        };
        var insertion = Transform.Translation(basePoint - Point3d.Origin);
        insertion = translation * insertion;
        instanceId = doc.Objects.AddInstanceObject(
          definitionIndex, insertion, instanceAttributes);
        if (instanceId == Guid.Empty)
          return false;
        temporaryObjects.Add(instanceId);
        return true;
      }
      finally
      {
        foreach (var geometry in geometries)
          geometry.Dispose();
        foreach (var attrs in attributes)
          attrs.Dispose();
      }
    }

    private static string UniqueBlockName(RhinoDoc doc, string groupName)
    {
      string root = new(groupName.Select(ch =>
        ch < 128 && char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
        .ToArray());
      root = root.Trim('_');
      if (root.Length == 0)
        root = "Group";
      root = root[..Math.Min(root.Length, MaximumBlockNameLength)];
      if (doc.InstanceDefinitions.Find(root) == null)
        return root;
      for (int attempt = 0; attempt < MaximumTemporaryNameAttempts; attempt++)
      {
        string name = $"{root[..Math.Min(root.Length, BlockNamePrefixLength)]}_" +
          Guid.NewGuid().ToString("N")[..8];
        if (doc.InstanceDefinitions.Find(name) == null)
          return name;
      }
      throw new InvalidOperationException("Could not name a temporary group block.");
    }

    private static FileDwgWriteOptions CurrentDxfWriteOptions()
    {
      var scheme = ReadCurrentScheme();
      var options = new FileDwgWriteOptions();
      SetProperty(options, "Name", scheme.Name);
      SetEnum(options, scheme, "Version", "AcadVersion");
      SetEnum(options, scheme, "ExportMeshesAs", "ExportMeshesAs", "WriteMeshesAs");
      SetEnum(options, scheme, "ExportSurfacesAs", "ExportSurfacesAs", "WriteSurfacesAs");
      SetEnum(options, scheme, "ExportLinesAs", "ExportLinesAs", "WriteLinesAs");
      SetEnum(options, scheme, "ExportArcsAs", "ExportArcsAs", "WriteArcsAs");
      SetEnum(options, scheme, "ExportSplinesAs", "ExportSplinesAs", "WriteSplinesAs");
      SetEnum(options, scheme, "ExportPolylinesAs", "ExportPolylinesAs", "WritePolylinesAs");
      SetEnum(options, scheme, "ExportPolycurvesAs", "ExportPolycurvesAs", "WritePolycurvesAs");
      foreach (string name in new[] { "Flatten", "ColorMethod", "UseColor" })
        SetEnum(options, scheme, name, name);

      SetDouble(options, scheme, "SimplifyTolerance", "SimplifyTolerance", "SimplifyTol");
      SetDouble(options, scheme, "MinPointDistance", "MinPointDistance");
      SetBool(options, scheme, "CurveUseMaxAngle", "CurveUseMaxAngle", "UseMaxAngle");
      SetBool(options, scheme, "CurveUseChordHeight", "CurveUseChordHeight", "UseChordHeight");
      SetBool(options, scheme, "CurveUseSegmentLength", "CurveUseSegmentLength", "UseSegmentLength");
      SetDouble(options, scheme, "CurveMaxAngleDegrees", "CurveMaxAngleDegrees", "MaxAngle");
      SetDouble(options, scheme, "CurveChordHeight", "CurveChordHeight", "ChordHeight");
      SetDouble(options, scheme, "CurveSegmentLength", "CurveSegmentLength", "SegmentLength");
      foreach (string name in new[]
      {
        "SplitPolycurves", "SplitSplines", "Simplify", "NoDxfHeader", "IsDefault",
        "FullLayerPath", "PreserveArcNormals", "UseLWPolylines", "WriteThickCurves"
      })
        SetBool(options, scheme, name, name);
      Log.Write("vExportDXF", $"using DXF scheme {scheme.Name}");
      return options;
    }

    private static void SetProperty(object target, string name, object value)
    {
      PropertyInfo? property = target.GetType().GetProperty(name);
      if (property?.CanWrite == true)
        property.SetValue(target, value);
    }

    private static void SetEnum(FileDwgWriteOptions options, SchemeValues settings,
      string propertyName, string currentKey, string? legacyKey = null)
    {
      string? key = settings.FindKey(currentKey, legacyKey);
      PropertyInfo? property = options.GetType().GetProperty(propertyName);
      if (key == null || property?.CanWrite != true)
        return;
      int value = settings.GetInteger(key);
      if (legacyKey != null && key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase))
        value = MapLegacyEnum(propertyName, key, value);
      property.SetValue(options, Enum.ToObject(property.PropertyType, value));
    }

    private static int MapLegacyEnum(string property, string key, int value)
    {
      int[] mapping = property switch
      {
        "ExportMeshesAs" => value switch { 6 => [0], 7 => [1], _ => [] },
        "ExportSurfacesAs" => value switch { 8 => [0], 5 => [1], 6 => [2], _ => [] },
        "ExportLinesAs" or "ExportSplinesAs" or "ExportPolylinesAs" =>
          value switch { 0 => [0], 3 => [1], 4 => [2], 9 => [3], _ => [] },
        "ExportArcsAs" => value switch
          { 0 => [0], 1 => [1], 2 => [2], 3 => [3], 4 => [4], 9 => [5], _ => [] },
        "ExportPolycurvesAs" => value switch
          { 0 => [0], 2 => [1], 3 => [2], 4 => [3], 9 => [4], _ => [] },
        _ => []
      };
      if (mapping.Length == 0)
        throw new InvalidOperationException($"Unsupported legacy DXF setting {key}={value}.");
      return mapping[0];
    }

    private static void SetBool(FileDwgWriteOptions options, SchemeValues settings,
      string propertyName, params string[] keys)
    {
      string? key = settings.FindKey(keys);
      if (key != null)
        SetProperty(options, propertyName, settings.GetBool(key));
    }

    private static void SetDouble(FileDwgWriteOptions options, SchemeValues settings,
      string propertyName, params string[] keys)
    {
      string? key = settings.FindKey(keys);
      if (key != null)
        SetProperty(options, propertyName, settings.GetDouble(key));
    }

    private sealed class SchemeValues(
      string name, IEnumerable<string> keys,
      Func<string, int> getInteger, Func<string, double> getDouble,
      Func<string, bool> getBool)
    {
      private readonly Dictionary<string, string> _keys = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(
        key => key, key => key, StringComparer.OrdinalIgnoreCase);
      public string Name { get; } = name;
      public string? FindKey(params string?[] names)
      {
        foreach (string? name in names)
          if (name != null && _keys.TryGetValue(name, out string? key))
            return key;
        return null;
      }
      public int GetInteger(string key) => getInteger(key);
      public double GetDouble(string key) => getDouble(key);
      public bool GetBool(string key) => getBool(key);
    }

    private static SchemeValues ReadCurrentScheme()
    {
      try
      {
        var settings = PlugIn.GetPluginSettings(AcadExportPluginId, true);
        if (settings != null)
        {
          string name = settings.GetString("CurrentOptionName", string.Empty);
          if (string.IsNullOrEmpty(name))
            name = settings.ChildKeys.FirstOrDefault(key =>
              settings.GetChild(key).GetBool("CurrentOption", false)) ?? string.Empty;
          if (!string.IsNullOrEmpty(name))
          {
            var child = settings.GetChild(name);
            return new SchemeValues(name, child.Keys, child.GetInteger,
              child.GetDouble, child.GetBool);
          }
        }
      }
      catch (Exception ex)
      {
        Log.Write("vExportDXF", $"persistent DXF settings unavailable: {ex.Message}");
      }
      return ReadSchemeXml();
    }

    private static SchemeValues ReadSchemeXml()
    {
      string appData = System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.ApplicationData);
      string pluginsPath = Path.Combine(appData, "McNeel", "Rhinoceros",
        $"{RhinoApp.ExeVersion}.0", "Plug-ins");
      if (!Directory.Exists(pluginsPath))
        throw new FileNotFoundException("Rhino DXF export settings folder was not found.");
      string suffix = $"({AcadExportPluginId})";
      string? path = Directory.EnumerateDirectories(pluginsPath)
        .Where(folder => Path.GetFileName(folder).EndsWith(
          suffix, StringComparison.OrdinalIgnoreCase))
        .Select(folder => Path.Combine(folder, "settings", "settings-Scheme__Default.xml"))
        .FirstOrDefault(File.Exists);
      if (path == null)
        throw new FileNotFoundException("Rhino DXF export settings file was not found.");
      XElement settings = XDocument.Load(path).Root?.Element("settings")
        ?? throw new InvalidDataException("DXF export settings XML has no settings node.");
      var rootValues = XmlEntries(settings);
      rootValues.TryGetValue("CurrentOptionName", out string? name);
      var children = settings.Elements("child").ToArray();
      if (string.IsNullOrEmpty(name))
        name = children.FirstOrDefault(child =>
          XmlEntries(child).TryGetValue("CurrentOption", out string? selected) &&
          bool.TryParse(selected, out bool active) && active)?.Attribute("key")?.Value;
      var selectedChild = children.FirstOrDefault(child =>
        string.Equals(child.Attribute("key")?.Value, name, StringComparison.Ordinal));
      if (selectedChild == null || string.IsNullOrEmpty(name))
        throw new InvalidDataException("Rhino's current DXF export scheme could not be read.");
      var values = XmlEntries(selectedChild);
      return new SchemeValues(name, values.Keys,
        key => int.Parse(values[key], CultureInfo.InvariantCulture),
        key => double.Parse(values[key], CultureInfo.InvariantCulture),
        key => values[key].Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on");
    }

    private static Dictionary<string, string> XmlEntries(XElement node) =>
      node.Elements("entry")
        .Where(entry => entry.Attribute("key") != null)
        .GroupBy(entry => entry.Attribute("key")!.Value,StringComparer.OrdinalIgnoreCase)
        .ToDictionary(entries => entries.Last().Attribute("key")!.Value,
          entries => entries.Last().Value, StringComparer.OrdinalIgnoreCase);
  }
}

namespace vTools.Commands
{
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vExportDXF
  {
    private static void ApplyExportExplode(IReadOnlyList<RhinoObject> targets,Dictionary<Guid,List<ExportCurvePiece>> overrides,ExportExplodeMode mode)
    {
      if(mode==ExportExplodeMode.No) return;
      int splitObjects=0,outputPieces=0;
      foreach(var source in targets.Where(obj=>obj.Geometry is Curve && !NestTableMetadata.HasDecorationRole(obj.Attributes)))
      {
        var originals=overrides.TryGetValue(source.Id,out var existing)?existing:[new ExportCurvePiece((Curve)source.Geometry,source.Attributes)];
        var replacement=new List<ExportCurvePiece>(); bool changed=false;
        try
        {
          foreach(var piece in originals)
          {
            var segments=ExplodeExportCurve(piece.Geometry,mode);
            changed|=segments.Length>1;
            replacement.AddRange(segments.Select(curve=>new ExportCurvePiece(curve,piece.Attributes,piece.Between)));
          }
          if(!changed) { foreach(var piece in replacement) piece.Geometry.Dispose(); continue; }
          overrides[source.Id]=replacement;
          if(existing!=null) foreach(var piece in existing) piece.Geometry.Dispose();
          splitObjects++; outputPieces+=replacement.Count;
        }
        catch { foreach(var piece in replacement) piece.Geometry.Dispose(); throw; }
      }
      Log.Write("vExportDXF","explode={0} sourceCurvesSplit={1} exportPieces={2}",mode,splitObjects,outputPieces);
    }

    private static Curve[] ExplodeExportCurve(Curve source,ExportExplodeMode mode)
    {
      if(mode==ExportExplodeMode.No) return [source.DuplicateCurve()];
      using var copy=source.DuplicateCurve();
      if(mode==ExportExplodeMode.Full)
      {
        if(copy is PolyCurve polycurve) polycurve.RemoveNesting();
        var segments=copy.DuplicateSegments();
        if(segments.Length>1)
        {
          var result=new List<Curve>();
          try
          {
            foreach(var segment in segments) result.AddRange(ExplodeExportCurve(segment,mode));
            return result.ToArray();
          }
          catch { foreach(var curve in result) curve.Dispose(); throw; }
          finally { foreach(var segment in segments) segment.Dispose(); }
        }
        foreach(var segment in segments) segment.Dispose();
      }
      var parameters=ExportCorners(copy);
      // Closed-curve Split rejoins through the seam, so keep that seam within a smooth span rather than at a corner.
      if(copy.IsClosed&&parameters.Count>0&&copy.ChangeClosedCurveSeam((copy.Domain.T0+parameters[0])/2)) parameters=ExportCorners(copy);
      return parameters.Count>0?copy.Split(parameters.ToArray())??[source.DuplicateCurve()]:[source.DuplicateCurve()];
    }

    private static List<double> ExportCorners(Curve curve)
    {
      var result=new List<double>(); var domain=curve.Domain;
      double start=domain.T0,epsilon=Math.Max(Math.Abs(domain.Length)*ConfiguredExportSplitParameterTolerance,RhinoMath.ZeroTolerance);
      while(curve.GetNextDiscontinuity(Continuity.G1_continuous,start,domain.T1,out double parameter))
      {
        if(parameter<=start||parameter>=domain.T1) break;
        result.Add(parameter); start=parameter+epsilon;
      }
      return result;
    }
  }
}

namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vExportDXF
  {
    private sealed record ExportCurvePiece(
      Curve Geometry, ObjectAttributes Attributes, bool Between = false);
    private readonly record struct NotchContact(double First, double Second);
    private readonly record struct CurveHit(RhinoObject Object, double Parameter, double Distance);
    private readonly record struct NotchLegs(
      Point3d First, Point3d Second, Guid SourceId, bool OnSource,
      RhinoObject[] Components);
    private sealed record MatchedNotch(Guid[] CurveIds, RhinoObject[] Components);

    private static IEnumerable<(GeometryBase Geometry, ObjectAttributes Attributes)> ExportPieces(
      RhinoObject source, IReadOnlyDictionary<Guid, List<ExportCurvePiece>> overrides)
    {
      if (overrides.TryGetValue(source.Id, out var pieces))
      {
        foreach (var piece in pieces)
          yield return (piece.Geometry, piece.Attributes);
      }
      else
        yield return (source.Geometry, source.Attributes);
    }

    private static Dictionary<Guid, List<ExportCurvePiece>> BuildNotchTrimOverrides(
      RhinoDoc doc, IReadOnlyList<RhinoObject> targets,
      NotchTrimMode mode, string layerName, bool joinNotches)
    {
      var overrides = new Dictionary<Guid, List<ExportCurvePiece>>();
      if (mode == NotchTrimMode.No)
        return overrides;

      double tolerance = Math.Max(
        doc.ModelAbsoluteTolerance * ConfiguredNotchContactToleranceScale,
        RhinoMath.ZeroTolerance * ConfiguredNotchContactZeroToleranceScale);
      var curves = targets.Where(obj => obj.Geometry is Curve &&
        obj.Attributes.GetUserString(NotchMetadataPrefix + "object_role") != NotchRole)
        .ToArray();
      var notchComponents = targets.Where(obj => obj.Geometry is Curve &&
        obj.Attributes.GetUserString(NotchMetadataPrefix + "object_role") == NotchRole &&
        (obj.Attributes.GetUserString(NotchMetadataPrefix + "notch_type") is
          "V" or "U" or OpenVNotchType) &&
        double.TryParse(obj.Attributes.GetUserString(NotchMetadataPrefix + "notch_offset"),
          NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) &&
        offset >= 0.0).ToArray();
      var notchGroups = notchComponents.GroupBy(obj =>
        obj.Attributes.GetUserString(NotchMetadataPrefix + "component_set") is
          { Length: > 0 } set ? set : obj.Id.ToString("N"));
      var contacts = new Dictionary<Guid, List<NotchContact>>();
      var matchedNotches = new List<MatchedNotch>();
      int unmatched = 0;
      int matched = 0;
      foreach (var group in notchGroups)
      {
        foreach (var legs in ExtractNotchLegs(group))
        {
          var firstHits = FindContactHits(
            curves, legs.First, legs.SourceId, legs.OnSource, tolerance);
          var secondHits = FindContactHits(
            curves, legs.Second, legs.SourceId, legs.OnSource, tolerance);
          CurveHit? first = null;
          CurveHit? second = null;
          double best = double.MaxValue;
          foreach (var left in firstHits)
            foreach (var right in secondHits)
              if (left.Object.Id == right.Object.Id &&
                  Math.Abs(left.Parameter - right.Parameter) > RhinoMath.ZeroTolerance &&
                  left.Distance + right.Distance < best)
              {
                first = left;
                second = right;
                best = left.Distance + right.Distance;
              }
          if (first.HasValue && second.HasValue)
          {
            AddContact(contacts, first.Value.Object.Id,
              first.Value.Parameter, second.Value.Parameter);
            matchedNotches.Add(new MatchedNotch(
              [first.Value.Object.Id], legs.Components));
            matched++;
            continue;
          }

          best = double.MaxValue;
          double firstEnd = 0.0;
          double secondEnd = 0.0;
          foreach (var left in firstHits)
            foreach (var right in secondHits)
              if (left.Object.Id != right.Object.Id &&
                  left.Object.Geometry is Curve leftCurve &&
                  right.Object.Geometry is Curve rightCurve &&
                  TrySharedEnd(leftCurve, rightCurve, tolerance,
                    out double leftEnd, out double rightEnd) &&
                  left.Distance + right.Distance < best)
              {
                first = left;
                second = right;
                firstEnd = leftEnd;
                secondEnd = rightEnd;
                best = left.Distance + right.Distance;
              }
          if (first.HasValue && second.HasValue)
          {
            AddContact(contacts, first.Value.Object.Id, first.Value.Parameter, firstEnd);
            AddContact(contacts, second.Value.Object.Id, secondEnd, second.Value.Parameter);
            matchedNotches.Add(new MatchedNotch(
              [first.Value.Object.Id, second.Value.Object.Id], legs.Components));
            matched++;
          }
          else
            unmatched++;
        }
      }

      int changed = 0;
      int between = 0;
      foreach (var (id, spans) in contacts)
      {
        var target = curves.FirstOrDefault(obj => obj.Id == id);
        if (target?.Geometry is not Curve curve)
          continue;
        var pieces = SplitExportCurve(doc, target, curve, spans, mode, layerName, tolerance,
          out int betweenPieces);
        if (pieces == null)
          continue;
        overrides[id] = pieces;
        changed++;
        between += betweenPieces;
      }
      int joined = joinNotches
        ? JoinNotchedExportCurves(curves, matchedNotches, overrides, tolerance)
        : 0;
      Log.Write("vExportDXF", $"NotchTrim={mode} exportCurves={curves.Length} " +
        $"visibleNotchComponents={notchComponents.Length} matched={matched} " +
        $"unmatched={unmatched} curves={changed} between={between} " +
        $"NotchJoin={joinNotches} joined={joined}");
      if (unmatched > 0)
        RhinoApp.WriteLine($"vExportDXF: {CountText.Format(unmatched, "V/U notch contact")} did not match an export curve.");
      return overrides;
    }

    private static int JoinNotchedExportCurves(
      IReadOnlyList<RhinoObject> curves, IReadOnlyList<MatchedNotch> matches,
      Dictionary<Guid, List<ExportCurvePiece>> overrides, double tolerance)
    {
      var layers = curves.ToDictionary(obj => obj.Id, obj => obj.Attributes.LayerIndex);
      var pending = new List<MatchedNotch>();
      foreach (var match in matches)
      {
        int layer = match.Components[0].Attributes.LayerIndex;
        if (match.Components.All(obj => obj.Attributes.LayerIndex == layer) &&
            match.CurveIds.All(id => layers.TryGetValue(id, out int sourceLayer) && sourceLayer == layer))
          pending.Add(match);
        else
          Log.Write("vExportDXF", $"NotchJoin skipped curves={string.Join(",", match.CurveIds)} " +
            "reason=notch and source curves have different layers");
      }
      int joinedCount = 0;
      while (pending.Count > 0)
      {
        var component = new List<MatchedNotch> { pending[0] };
        var sourceIds = pending[0].CurveIds.ToHashSet();
        pending.RemoveAt(0);
        bool expanded;
        do
        {
          expanded = false;
          for (int index = pending.Count - 1; index >= 0; index--)
          {
            if (!pending[index].CurveIds.Any(sourceIds.Contains))
              continue;
            component.Add(pending[index]);
            sourceIds.UnionWith(pending[index].CurveIds);
            pending.RemoveAt(index);
            expanded = true;
          }
        } while (expanded);

        if (TryJoinNotchComponent(curves, component, sourceIds, overrides,
              tolerance, out string reason))
          joinedCount += component.Count;
        else
          Log.Write("vExportDXF", $"NotchJoin skipped curves={string.Join(",", sourceIds)} " +
            $"notches={component.Count} reason={reason}");
      }
      return joinedCount;
    }

    private static bool TryJoinNotchComponent(
      IReadOnlyList<RhinoObject> curves, IReadOnlyList<MatchedNotch> matches,
      IReadOnlySet<Guid> sourceIds,
      Dictionary<Guid, List<ExportCurvePiece>> overrides,
      double tolerance, out string reason)
    {
      reason = string.Empty;
      var sources = curves.Where(obj => sourceIds.Contains(obj.Id)).ToArray();
      if (sources.Length != sourceIds.Count || sources.Any(obj =>
            obj.Geometry is not Curve curve ||
            !(curve is LineCurve || curve.IsPolyline()) ||
            !overrides.ContainsKey(obj.Id)))
      {
        reason = "a source is not a split line or polyline";
        return false;
      }
      var anchor = sources[0];
      var anchorGroups = (anchor.Attributes.GetGroupList() ?? []).ToHashSet();
      if (sources.Any(obj => obj.Attributes.LayerIndex != anchor.Attributes.LayerIndex ||
            !anchorGroups.SetEquals(obj.Attributes.GetGroupList() ?? [])))
      {
        reason = "source curves have different layers or groups";
        return false;
      }

      var outside = sources.SelectMany(obj => overrides[obj.Id])
        .Where(piece => !piece.Between).ToArray();
      var notchObjects = matches.SelectMany(match => match.Components)
        .DistinctBy(obj => obj.Id).ToArray();
      if (outside.Length == 0 || notchObjects.Length == 0 ||
          notchObjects.Any(obj => obj.Geometry is not Curve curve || curve.IsClosed))
      {
        reason = "no open notch or retained source section";
        return false;
      }
      var notchCurves = notchObjects.Select(obj => (Curve)obj.Geometry).ToArray();
      var input = outside.Select(piece => piece.Geometry).Concat(notchCurves).ToArray();
      var endpoints = input.Where(curve => !curve.IsClosed)
        .SelectMany(curve => new[] { curve.PointAtStart, curve.PointAtEnd }).ToArray();
      if (endpoints.Any(point => endpoints.Count(other =>
            point.DistanceTo(other) <= tolerance) > 2))
      {
        reason = "the notch would branch at a shared endpoint";
        return false;
      }

      Curve[] joined = [];
      var replacements = new List<ExportCurvePiece>();
      try
      {
        joined = Curve.JoinCurves(input, tolerance, preserveDirection: false);
        if (joined.Length == 0 || joined.Length > outside.Length ||
            notchCurves.Any(notch => joined.Any(output =>
              IsUnjoinedNotch(output, notch, tolerance))))
        {
          reason = "not every notch joins its source";
          return false;
        }
        double inputLength = input.Sum(curve => curve.GetLength());
        double joinedLength = joined.Sum(curve => curve.GetLength());
        if (Math.Abs(inputLength - joinedLength) >
            Math.Max(tolerance * input.Length * 2.0, inputLength * 1.0e-8))
        {
          reason = "joining changed the boundary length";
          return false;
        }
        foreach (var curve in joined)
          replacements.Add(new ExportCurvePiece(
            curve.DuplicateCurve(), anchor.Attributes.Duplicate()));

        foreach (var source in sources)
        {
          var pieces = overrides[source.Id];
          overrides[source.Id] = pieces.Where(piece => piece.Between).ToList();
          foreach (var piece in pieces.Where(piece => !piece.Between))
            piece.Geometry.Dispose();
        }
        overrides[anchor.Id].AddRange(replacements);
        foreach (var notch in notchObjects)
          overrides[notch.Id] = [];
        return true;
      }
      catch (Exception ex)
      {
        foreach (var piece in replacements)
          piece.Geometry.Dispose();
        reason = ex.Message;
        return false;
      }
      finally
      {
        foreach (var curve in joined)
          curve.Dispose();
      }
    }

    private static bool IsUnjoinedNotch(Curve output, Curve notch, double tolerance)
    {
      if (Math.Abs(output.GetLength() - notch.GetLength()) > tolerance)
        return false;
      return output.PointAtStart.DistanceTo(notch.PointAtStart) <= tolerance &&
             output.PointAtEnd.DistanceTo(notch.PointAtEnd) <= tolerance ||
             output.PointAtStart.DistanceTo(notch.PointAtEnd) <= tolerance &&
             output.PointAtEnd.DistanceTo(notch.PointAtStart) <= tolerance;
    }

    private static IEnumerable<NotchLegs> ExtractNotchLegs(IEnumerable<RhinoObject> group)
    {
      var components = group.OrderBy(obj =>
        int.TryParse(obj.Attributes.GetUserString(NotchMetadataPrefix + "component_index"),
          NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
          ? index : 0).ToArray();
      if (components.Length == 0 ||
          components.Any(obj => obj.Geometry is not Curve))
        yield break;
      var attributes = components[0].Attributes;
      if (int.TryParse(attributes.GetUserString(NotchMetadataPrefix + "component_count"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected) &&
          expected != components.Length)
        yield break;
      string? type = attributes.GetUserString(NotchMetadataPrefix + "notch_type");
      int perSide = type == OpenVNotchType ? 2 : 1;
      if (components.Length % perSide != 0)
        yield break;
      Guid.TryParse(attributes.GetUserString(NotchMetadataPrefix + "curve_id"),
        out Guid sourceId);
      bool onSource = double.TryParse(
        attributes.GetUserString(NotchMetadataPrefix + "notch_offset"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) &&
        offset <= RhinoMath.ZeroTolerance;
      for (int index = 0; index < components.Length; index += perSide)
      {
        var side = components[index..(index + perSide)];
        yield return new NotchLegs(
          ((Curve)side[0].Geometry).PointAtStart,
          ((Curve)side[^1].Geometry).PointAtEnd,
          sourceId, onSource, side);
      }
    }

    private static List<CurveHit> FindContactHits(
      IReadOnlyList<RhinoObject> curves, Point3d point, Guid sourceId,
      bool onSource, double tolerance)
    {
      var hits = new List<CurveHit>();
      foreach (var obj in curves)
      {
        if ((!onSource && obj.Id == sourceId) || obj.Geometry is not Curve curve ||
            !curve.ClosestPoint(point, out double parameter))
          continue;
        double distance = point.DistanceTo(curve.PointAt(parameter));
        if (distance <= tolerance)
          hits.Add(new CurveHit(obj, parameter, distance));
      }
      hits.Sort((left, right) => left.Distance.CompareTo(right.Distance));
      return hits;
    }

    private static bool TrySharedEnd(Curve first, Curve second, double tolerance,
      out double firstParameter, out double secondParameter)
    {
      firstParameter = secondParameter = 0.0;
      if (first.IsClosed || second.IsClosed)
        return false;
      double best = tolerance;
      bool found = false;
      foreach (double a in new[] { first.Domain.T0, first.Domain.T1 })
        foreach (double b in new[] { second.Domain.T0, second.Domain.T1 })
        {
          double distance = first.PointAt(a).DistanceTo(second.PointAt(b));
          if (distance > best)
            continue;
          firstParameter = a;
          secondParameter = b;
          best = distance;
          found = true;
        }
      return found;
    }

    private static void AddContact(Dictionary<Guid, List<NotchContact>> contacts,
      Guid id, double first, double second)
    {
      if (!contacts.TryGetValue(id, out var spans))
        contacts[id] = spans = [];
      spans.Add(new NotchContact(first, second));
    }

    private static List<ExportCurvePiece>? SplitExportCurve(
      RhinoDoc doc, RhinoObject target, Curve curve,
      IReadOnlyList<NotchContact> spans, NotchTrimMode mode, string layerName,
      double tolerance, out int betweenCount)
    {
      betweenCount = 0;
      var parameters = spans.SelectMany(span => new[] { span.First, span.Second })
        .Where(parameter => curve.IsClosed ||
          curve.PointAt(parameter).DistanceTo(curve.PointAtStart) > tolerance &&
          curve.PointAt(parameter).DistanceTo(curve.PointAtEnd) > tolerance)
        .OrderBy(parameter => parameter).ToArray();
      var distinct = new List<double>();
      foreach (double parameter in parameters)
        if (distinct.Count == 0 ||
            curve.PointAt(parameter).DistanceTo(curve.PointAt(distinct[^1])) > tolerance)
          distinct.Add(parameter);
      var pieces = distinct.Count > 0 ? curve.Split(distinct) : [curve.DuplicateCurve()];
      if (pieces == null || pieces.Length == 0)
        return null;
      var replacements = new List<ExportCurvePiece>();
      try
      {
        for (int index = 0; index < pieces.Length; index++)
        {
          var piece = pieces[index];
          if (!curve.ClosestPoint(piece.PointAtNormalizedLength(0.5), out double midpoint))
            continue;
          bool between = spans.Any(span => ParameterBetween(curve, span, midpoint));
          if (between)
            betweenCount++;
          if (between && mode == NotchTrimMode.Trim)
            continue;
          var attributes = target.Attributes.Duplicate();
          if (between && mode == NotchTrimMode.Split)
            attributes.LayerIndex = EnsureExportLayer(doc, layerName);
          replacements.Add(new ExportCurvePiece(piece.DuplicateCurve(), attributes, between));
        }
        if (betweenCount == 0)
        {
          foreach (var replacement in replacements)
            replacement.Geometry.Dispose();
          return null;
        }
        return replacements;
      }
      catch
      {
        foreach (var replacement in replacements)
          replacement.Geometry.Dispose();
        throw;
      }
      finally
      {
        foreach (var piece in pieces)
          piece.Dispose();
      }
    }

    private static bool ParameterBetween(Curve curve, NotchContact span, double parameter)
    {
      double first = Math.Min(span.First, span.Second);
      double last = Math.Max(span.First, span.Second);
      bool inside = parameter > first && parameter < last;
      if (curve.IsClosed)
      {
        using var section = curve.Trim(first, last);
        if (section != null && section.GetLength() > curve.GetLength() * 0.5)
          inside = !inside;
      }
      return inside;
    }
  }
}

namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Windows.Forms;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vExportDXF
  {
    // Text layout measurement defaults
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TextBreakSmallCapsScale", "Unitless glyph scale; 1 keeps the source character sizes.")] private const double TextBreakSmallCapsScale = 1.0; // Unitless glyph scale; 1 keeps the source character sizes.

    private static readonly double ConfiguredTextBreakSmallCapsScale = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.TextBreakSmallCapsScale", TextBreakSmallCapsScale); // Resolved once from advanced defaults; restart Rhino after changes.
    private const double TextBreakGlyphSpacing = 0.0; // Additional model-space glyph spacing; 0 preserves native kerning.
    [global::vTools.CommandDefault("vExportDXF", "vTools.Commands.vExportDXF.TextBreakLayoutTolerance", "Relative glyph-position tolerance when checking that a row was not wrapped or reformatted.")] private const double TextBreakLayoutTolerance = 1e-6; // Relative glyph-position tolerance when checking that a row was not wrapped or reformatted.

    private static readonly double ConfiguredTextBreakLayoutTolerance = global::vTools.CommandDefaults.Get("vExportDXF", "vTools.Commands.vExportDXF.TextBreakLayoutTolerance", TextBreakLayoutTolerance); // Resolved once from advanced defaults; restart Rhino after changes.

    private static Dictionary<Guid, List<TextEntity>> BuildTextBreakOverrides(
      RhinoDoc doc, IReadOnlyList<RhinoObject> targets, bool enabled)
    {
      var overrides = new Dictionary<Guid, List<TextEntity>>();
      try
      {
        foreach (var source in targets)
        {
          if (source.Geometry is not TextEntity text)
            continue;
          bool tableLabel = source.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelRole;
          bool split = enabled && text.PlainText.IndexOfAny(['\r', '\n']) >= 0;
          if (!split && !tableLabel)
            continue;
          var lines = split ? SplitExportText(doc, text) : new List<TextEntity> { tableLabel ? CreateTableExportText(doc, text) : (TextEntity)text.Duplicate() };
          overrides.Add(source.Id, lines);
        }
        return overrides;
      }
      catch
      {
        DisposeTextBreakOverrides(overrides);
        throw;
      }
    }

    private static TextEntity CreateTableExportText(RhinoDoc doc, TextEntity source)
    {
      double height = source.TextHeight * AnnotationTextTransform.ResolveDisplayDimensionScale(doc, source, doc.Views.ActiveView?.ActiveViewport);
      var text = new TextEntity { Plane = source.Plane, PlainText = source.PlainText, TextHeight = height,
        Justification = TextJustification.MiddleCenter, DimensionStyleId = source.DimensionStyleId,
        DimensionScale = ConfiguredTableExportTextScale, DrawForward = source.DrawForward, TextOrientation = source.TextOrientation };
      if (!AnnotationTextTransform.ApplyFixedDisplayTextHeight(doc, text, doc.Views.ActiveView?.ActiveViewport, height, ConfiguredTableExportTextScale))
      { text.Dispose(); throw new InvalidOperationException("Could not create title-style table text for export."); }
      text.ParentDimensionStyle = doc.DimStyles.FindId(source.DimensionStyleId) ?? doc.DimStyles.Current;
      text.TextHorizontalAlignment = TextHorizontalAlignment.Center;
      text.TextVerticalAlignment = TextVerticalAlignment.Middle;
      text.Justification = TextJustification.MiddleCenter;
      // vTitle inserts text before retrieving its resolved annotation layout; block definitions need the same step.
      Guid id = doc.Objects.AddText(text, new ObjectAttributes());
      text.Dispose();
      if (id == Guid.Empty) throw new InvalidOperationException("Could not resolve table text in the document.");
      try
      {
        return doc.Objects.FindId(id)?.Geometry.Duplicate() as TextEntity
          ?? throw new InvalidOperationException("Could not retrieve resolved title-style table text.");
      }
      finally { doc.Objects.Delete(id, quiet: true); }
    }

    private static void NormalizeTableDxfAttachments(string path)
    {
      var bytes = File.ReadAllBytes(path);
      var lines = new List<(int Start, int Length)>();
      for (int start = 0, index = 0; index <= bytes.Length; index++)
      {
        if (index < bytes.Length && bytes[index] != '\n') continue;
        int end = index > start && bytes[index - 1] == '\r' ? index - 1 : index;
        if (end > start || index < bytes.Length) lines.Add((start, end - start));
        start = index + 1;
      }
      bool mtext = false, tableLabel = false, roleKey = false;
      int attachment = -1, changed = 0;
      string Value((int Start, int Length) line) => System.Text.Encoding.ASCII.GetString(bytes, line.Start, line.Length).Trim();
      void FinishRecord()
      {
        if (!mtext || !tableLabel) return;
        if (attachment < 0) throw new InvalidOperationException("A table label has no DXF attachment field.");
        if (bytes[attachment] != '0' + DxfMiddleCenterAttachment)
        { bytes[attachment] = (byte)('0' + DxfMiddleCenterAttachment); changed++; }
      }
      for (int index = 0; index + 1 < lines.Count; index += 2)
      {
        if (!int.TryParse(Value(lines[index]), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
          throw new InvalidOperationException("Table label centring requires an ASCII DXF export scheme.");
        string value = Value(lines[index + 1]);
        if (code == 0)
        { FinishRecord(); mtext = value == "MTEXT"; tableLabel = false; roleKey = false; attachment = -1; }
        else if (code == 1000)
        { if (roleKey && value == NestTableMetadata.LabelRole) tableLabel = true; roleKey = value == NestTableMetadata.RoleKey; }
        else if (mtext && code == 71)
        {
          var line = lines[index + 1];
          for (int offset = line.Start; offset < line.Start + line.Length; offset++)
            if (bytes[offset] >= '1' && bytes[offset] <= '9') { attachment = offset; break; }
        }
      }
      FinishRecord();
      if (changed > 0) File.WriteAllBytes(path, bytes);
      Log.Write("vExportDXF", $"corrected middle-centre table text attachments={changed}");
    }

    private static Dictionary<Guid, List<TextEntity>> PrepareTableAnnotations(RhinoDoc doc,
      IReadOnlyList<RhinoObject> members, IReadOnlyDictionary<Guid, List<TextEntity>> overrides)
    {
      var result = new Dictionary<Guid, List<TextEntity>>();
      try
      {
        foreach (var member in members.Where(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelRole))
        {
          if (member.Geometry is not TextEntity source) continue;
          var lines = overrides.TryGetValue(member.Id, out var existing)
            ? existing.Select(line => CreateTableExportText(doc, line)).ToList()
            : new List<TextEntity> { CreateTableExportText(doc, source) };
          result.Add(member.Id, lines);
        }
        var texts = result.Values.SelectMany(lines => lines).ToArray();
        if (texts.Length == 0) return result;
        if (!TryGetTableAnnotationArea(members, texts[0].Plane, out var frame, out var area)) return result;
        if(members.Any(obj=>NestTableMetadata.IsEnclosedHeader(obj.Geometry)))
        {area=CompactHeaderAnnotations(doc,texts,frame,area);ValidateHeaderAnnotations(doc,texts,frame,area);return result;}
        // DXF middle-centre anchors belong to the row cells, not the source text's cached baseline bounds.
        texts = texts.OrderByDescending(text =>
        {
          frame.ClosestParameter(text.Plane.Origin, out _, out double y);
          return y;
        }).ToArray();
        var bounds = texts.Select(text => ExportTableTextBounds(doc, text, frame)).ToArray();
        if (bounds.Any(box => !box.IsValid))
          throw new InvalidOperationException("Could not measure table annotations for export.");
        var heights = texts.Select((text, index) => Math.Max(bounds[index].Diagonal.Y,
          text.TextHeight * ConfiguredTableExportLineHeightFactor)).ToArray();
        double padding = Math.Max(doc.ModelAbsoluteTolerance, heights.Max() * ConfiguredTableAnnotationPaddingFraction);
        double available = area.Diagonal.X - 2 * padding;
        double availableHeight = area.Diagonal.Y - 2 * padding;
        if (available <= RhinoMath.ZeroTolerance || availableHeight <= RhinoMath.ZeroTolerance)
          throw new InvalidOperationException("The table annotation box is too small for its padding.");
        double stackHeight = heights.Sum() + padding * (texts.Length - 1);
        double scale = Math.Min(1, Math.Min(available / Math.Max(RhinoMath.ZeroTolerance, bounds.Max(box => box.Diagonal.X)),
          availableHeight / stackHeight));
        double top = area.Center.Y + stackHeight * scale / 2;
        for (int index = 0; index < texts.Length; index++)
        {
          var text = texts[index];
          if (scale < 1) text.TextHeight *= scale;
          double height = heights[index] * scale;
          var plane = text.Plane;
          double centerY = top - height / 2;
          plane.Origin = frame.PointAt(area.Center.X, centerY);
          text.Plane = plane;
          top -= height + padding * scale;
          var box = ExportTableTextBounds(doc, text, frame);
          if (!box.IsValid || box.Diagonal.X > available + doc.ModelAbsoluteTolerance ||
              centerY - height / 2 < area.Min.Y + padding - doc.ModelAbsoluteTolerance ||
              centerY + height / 2 > area.Max.Y - padding + doc.ModelAbsoluteTolerance)
            throw new InvalidOperationException("Could not fit table annotations inside their outline.");
        }
        return result;
      }
      catch { DisposeTextBreakOverrides(result); throw; }
    }

    private static BoundingBox CompactHeaderAnnotations(RhinoDoc doc,TextEntity[] texts,Plane frame,BoundingBox area)
    {
      var anchors=texts.Select(text=>
      {frame.ClosestParameter(text.Plane.Origin,out double x,out double y);return new Point2d(x,y);}).ToArray();
      foreach(var text in texts)text.TextHeight=text.TextHeight*ConfiguredTableHeaderTextSizeMultiplier+ConfiguredTableHeaderTextSizeExtra;
      var bounds=texts.Select(text=>ExportTableTextBounds(doc,text,frame)).ToArray();
      if(bounds.Any(box=>!box.IsValid))throw new InvalidOperationException("Could not measure compact table header text.");
      double padding=Math.Max(doc.ModelAbsoluteTolerance,bounds.Max(box=>box.Diagonal.Y)*ConfiguredTableAnnotationPaddingFraction*ConfiguredTableHeaderSpacingMultiplier);
      double tolerance=Math.Max(doc.ModelAbsoluteTolerance,ConfiguredTextBreakLayoutTolerance);
      var rows=new List<List<int>>();
      foreach(int index in Enumerable.Range(0,texts.Length).OrderBy(index=>anchors[index].Y).ThenBy(index=>anchors[index].X))
      {
        var row=rows.FirstOrDefault(row=>Math.Abs(anchors[row[0]].Y-anchors[index].Y)<=tolerance);
        if(row==null){row=[];rows.Add(row);}row.Add(index);
      }
      var widths=bounds.Select((box,index)=>2*Math.Max(anchors[index].X-box.Min.X,box.Max.X-anchors[index].X)).ToArray();
      double width=rows.Max(row=>row.Sum(index=>widths[index])+2*padding*(row.Count-1))+2*padding;
      double left=area.Min.X,right=left+width;
      double center=(anchors.Min(anchor=>anchor.Y)+anchors.Max(anchor=>anchor.Y))/2;
      var rowCenters=new double[rows.Count];double previousTop=double.NegativeInfinity;
      for(int index=0;index<rows.Count;index++)
      {
        var row=rows[index];double lower=row.Min(item=>bounds[item].Min.Y-anchors[item].Y),upper=row.Max(item=>bounds[item].Max.Y-anchors[item].Y);
        double desired=center+(anchors[row[0]].Y-center)*ConfiguredTableHeaderSpacingMultiplier;
        rowCenters[index]=Math.Max(desired,previousTop+2*padding-lower);previousTop=rowCenters[index]+upper;
      }
      double centerShift=center-(rowCenters[0]+rowCenters[^1])/2;
      double bottom=double.PositiveInfinity,top=double.NegativeInfinity;
      for(int rowIndex=0;rowIndex<rows.Count;rowIndex++)
      {
        var row=rows[rowIndex];
        double edge=right-padding;
        for(int at=row.Count-1;at>=0;at--)
        {
          int index=row[at];var plane=texts[index].Plane;
          double x=at==0?left+padding+widths[index]/2:edge-widths[index]/2;
          double y=rowCenters[rowIndex]+centerShift;
          plane.Origin=frame.PointAt(x,y);texts[index].Plane=plane;
          bottom=Math.Min(bottom,y+bounds[index].Min.Y-anchors[index].Y);
          top=Math.Max(top,y+bounds[index].Max.Y-anchors[index].Y);
          edge=x-widths[index]/2-2*padding;
        }
      }
      return new BoundingBox(new Point3d(left,bottom-padding,0),new Point3d(right,top+padding,0));
    }
    private static void ValidateHeaderAnnotations(RhinoDoc doc,TextEntity[] texts,Plane frame,BoundingBox area)
    {
      var boxes=texts.Select(text=>ExportTableTextBounds(doc,text,frame)).ToArray();
      if(boxes.Any(box=>!box.IsValid))throw new InvalidOperationException("Could not measure table header annotations.");
      double padding=Math.Max(doc.ModelAbsoluteTolerance,boxes.Max(box=>box.Diagonal.Y)*ConfiguredTableAnnotationPaddingFraction);
      double tolerance=Math.Max(doc.ModelAbsoluteTolerance,ConfiguredTextBreakLayoutTolerance);
      foreach(var box in boxes)
      {
        if(box.Min.X<area.Min.X+padding-tolerance||box.Max.X>area.Max.X-padding+tolerance||
          box.Min.Y<area.Min.Y+padding-tolerance||box.Max.Y>area.Max.Y-padding+tolerance)
          throw new InvalidOperationException("A table header label exceeds its export frame.");
      }
    }

    private static bool TryGetTableAnnotationArea(IReadOnlyList<RhinoObject> members, Plane fallback,
      out Plane frame, out BoundingBox area)
    {
      frame = fallback;
      area = BoundingBox.Empty;
      var border = members.FirstOrDefault(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.BorderRole);
      if(border?.Geometry is Curve headerOutline&&NestTableMetadata.TryHeaderFrame(headerOutline,out var header))
      {frame=header.Plane;using var curve=header.ToNurbsCurve();area=curve.GetBoundingBox(frame);}
      else if (border?.Geometry is Curve outline && outline.TryGetPolyline(out var points) && points.Count is 8 or 9)
      {
        frame = new Plane(points[0], points[1] - points[0], points[3] - points[0]);
        foreach (int index in points.Count == 8 ? new[] { 3, 4, 5, 6 } : new[] { 4, 5, 6, 7 })
        {
          frame.ClosestParameter(points[index], out double x, out double y);
          area.Union(new Point3d(x, y, 0));
        }
      }
      else
        foreach (var member in members.Where(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelFrameRole))
          area.Union(member.Geometry.GetBoundingBox(frame));
      return area.IsValid;
    }

    private static BoundingBox ExportTableTextBounds(RhinoDoc doc, TextEntity text, Plane frame)
    {
      using var measurement = (TextEntity)text.Duplicate();
      var parent = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
      measurement.ParentDimensionStyle = parent;
      measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(doc, text, doc.Views.ActiveView?.ActiveViewport);
      using var style = measurement.GetDimensionStyle(parent);
      var curves = measurement.CreateCurves(style ?? parent, true, ConfiguredTextBreakSmallCapsScale, TextBreakGlyphSpacing);
      var bounds = BoundingBox.Empty;
      foreach (var curve in curves ?? [])
      {
        try { bounds.Union(curve.GetBoundingBox(frame)); }
        finally { curve.Dispose(); }
      }
      return bounds;
    }

    private static List<TextEntity> SplitExportText(RhinoDoc doc, TextEntity source)
    {
      var result = new List<TextEntity>();
      List<Curve[]>? sourceGlyphs = null;
      try
      {
        // RichEdit extracts complete per-row RTF, including fonts and inline formatting.
        using var editor = new RichTextBox();
        if (source.TextHasRtfFormatting)
          editor.Rtf = source.RichText;
        else
          editor.Text = NormalizeTextBreaks(source.PlainText);
        string content = editor.Text;
        if (NormalizeTextBreaks(content) != NormalizeTextBreaks(source.PlainText))
          throw new InvalidOperationException("TextBreak could not preserve this text's formatting. Use TextBreak=No to export it unchanged.");

        using var measurement = source.Duplicate() as TextEntity
          ?? throw new InvalidOperationException("Could not copy multiline text for export.");
        measurement.ParentDimensionStyle = source.ParentDimensionStyle;
        measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(
          doc, source, doc.Views.ActiveView?.ActiveViewport);
        using var style = measurement.DimensionStyle;
        sourceGlyphs = measurement.CreateCurvesGrouped(
          style, true, ConfiguredTextBreakSmallCapsScale, TextBreakGlyphSpacing);
        int glyphIndex = 0;
        int start = 0;
        while (start < content.Length)
        {
          int end = content.IndexOf('\n', start);
          if (end < 0) end = content.Length;
          int length = end - start;
          if (length > 0 && content[end - 1] == '\r') length--;
          if (!string.IsNullOrWhiteSpace(content.Substring(start, length)))
          {
            editor.Select(start, length);
            var line = measurement.Duplicate() as TextEntity
              ?? throw new InvalidOperationException("Could not copy a text row for export.");
            result.Add(line);
            line.ParentDimensionStyle = measurement.ParentDimensionStyle;
            if (source.TextHasRtfFormatting)
              line.SetRichText(editor.SelectedRtf, style);
            else
              line.PlainText = content.Substring(start, length);
            line.TextIsWrapped = false;
            var glyphs = line.CreateCurvesGrouped(
              style, true, ConfiguredTextBreakSmallCapsScale, TextBreakGlyphSpacing);
            try
            {
              if (glyphs.Count == 0 || glyphIndex + glyphs.Count > sourceGlyphs.Count)
                throw new InvalidOperationException("TextBreak could not measure a text row. Use TextBreak=No to export it unchanged.");
              // Matching native glyphs gives the exact baseline, scale and justification.
              var from = TextGlyphBounds(glyphs[0]);
              var to = TextGlyphBounds(sourceGlyphs[glyphIndex]);
              var translation = to.Center - from.Center;
              double tolerance = ConfiguredTextBreakLayoutTolerance * Math.Max(1.0, source.TextHeight * measurement.DimensionScale);
              for (int index = 0; index < glyphs.Count; index++)
              {
                var actual = TextGlyphBounds(glyphs[index]);
                var expected = TextGlyphBounds(sourceGlyphs[glyphIndex + index]);
                if (!actual.IsValid || !expected.IsValid ||
                    (actual.Min + translation).DistanceTo(expected.Min) > tolerance ||
                    (actual.Max + translation).DistanceTo(expected.Max) > tolerance)
                  throw new InvalidOperationException("TextBreak could not preserve a wrapped or formatted row's placement. Use TextBreak=No to export it unchanged.");
              }
              var plane = line.Plane;
              plane.Origin += translation;
              line.Plane = plane;
              glyphIndex += glyphs.Count;
            }
            finally
            {
              DisposeTextGlyphs(glyphs);
            }
          }
          start = end + 1;
        }
        if (glyphIndex != sourceGlyphs.Count)
          throw new InvalidOperationException("TextBreak could not preserve all text rows. Use TextBreak=No to export it unchanged.");
        return result;
      }
      catch
      {
        foreach (var line in result) line.Dispose();
        throw;
      }
      finally
      {
        if (sourceGlyphs != null) DisposeTextGlyphs(sourceGlyphs);
      }
    }

    private static string NormalizeTextBreaks(string text) =>
      text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private static BoundingBox TextGlyphBounds(IEnumerable<Curve> glyph)
    {
      var bounds = BoundingBox.Empty;
      foreach (var curve in glyph) bounds.Union(curve.GetBoundingBox(true));
      return bounds;
    }

    private static void DisposeTextGlyphs(IEnumerable<Curve[]> glyphs)
    {
      foreach (var glyph in glyphs)
        foreach (var curve in glyph) curve.Dispose();
    }

    private static void DisposeTextBreakOverrides(
      IReadOnlyDictionary<Guid, List<TextEntity>> overrides)
    {
      foreach (var lines in overrides.Values)
        foreach (var line in lines) line.Dispose();
    }

    private static IEnumerable<(GeometryBase Geometry, ObjectAttributes Attributes)> PreparedExportPieces(
      RhinoObject source, IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
      IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides)
    {
      if (textBreakOverrides.TryGetValue(source.Id, out var lines))
      {
        foreach (var line in lines)
          yield return (line, source.Attributes);
      }
      else
      {
        foreach (var piece in ExportPieces(source, notchTrimOverrides))
          yield return piece;
      }
    }
  }
}
