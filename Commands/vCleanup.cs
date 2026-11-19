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
/// Simplifies curves, finds overlaps, and locates short curve geometry and control spans.
/// </summary>
[CommandStyle(Style.ScriptRunner)]
public sealed class vCleanup : vToolsCommand
{
  // Option defaults
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.DefaultThreshold", "Maximum short-geometry length in model units; zero or greater.", OptionKeys = "threshold")] private const double DefaultThreshold = 0.02; // Maximum short-geometry length in model units; zero or greater.

  private static readonly double ConfiguredDefaultThreshold = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.DefaultThreshold", DefaultThreshold); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.DefaultHighlightShort", "true draws short findings in magenta; false uses no cleanup overlay.", OptionKeys = "highlightShort")] private const bool DefaultHighlightShort = false; // true draws short findings in magenta; false uses no cleanup overlay.

  private static readonly bool ConfiguredDefaultHighlightShort = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.DefaultHighlightShort", DefaultHighlightShort); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.DefaultPreselectMode", "Short, Overlaps, All, or No findings selected after cleanup.", OptionKeys = "preselect")] private const CleanupTargetMode DefaultPreselectMode = CleanupTargetMode.Short; // Short, Overlaps, All, or No findings selected after cleanup.

  private static readonly CleanupTargetMode ConfiguredDefaultPreselectMode = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.DefaultPreselectMode", DefaultPreselectMode); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.DefaultAutoDeleteMode", "Short, Overlaps, All, or No findings deleted during cleanup.", OptionKeys = "autoDelete")] private const CleanupTargetMode DefaultAutoDeleteMode = CleanupTargetMode.No; // Short, Overlaps, All, or No findings deleted during cleanup.

  private static readonly CleanupTargetMode ConfiguredDefaultAutoDeleteMode = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.DefaultAutoDeleteMode", DefaultAutoDeleteMode); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.DefaultSimplifyCurves", "true simplifies scoped curves before analysis; false preserves their existing structure.", OptionKeys = "simplifyCrv")] private const bool DefaultSimplifyCurves = true; // true simplifies scoped curves before analysis; false preserves their existing structure.

  private static readonly bool ConfiguredDefaultSimplifyCurves = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.DefaultSimplifyCurves", DefaultSimplifyCurves); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.DefaultFindOverlaps", "true runs shared vOverlaps detection before the short scan; false skips overlap detection.", OptionKeys = "overlaps")] private const bool DefaultFindOverlaps = true; // true runs shared vOverlaps detection before the short scan; false skips overlap detection.

  private static readonly bool ConfiguredDefaultFindOverlaps = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.DefaultFindOverlaps", DefaultFindOverlaps); // Resolved once from advanced defaults; restart Rhino after changes.
  private const bool ProtectOpenCurveEnds = true; // true excludes open-curve endpoint segments and endpoint control points; false permits them.
  private const int MaximumExportCleanupPasses = 100; // Positive limit on export deletion passes; stalled or exhausted cleanup returns to review.
  private const string OrphanPartDataOption = "OrphanPartData"; // Explicit cleanup-only action; never persisted or run automatically with geometry/export cleanup.
  private const string OrphanPartDataUndoName = "vCleanup orphaned part data"; // Native Undo/Redo record description for confirmed part-data removal.
  private static readonly string[] PartDataSections = [PartGroupData.NestSection, PartGroupData.CustomSection, PartGroupData.NamesSection]; // Only these part-data namespaces are eligible for orphan cleanup.

  // Customizable appearance, output, limits, tolerance, and sampling
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.CandidatePointColor", "ARGB object color used for unmatched control-point helpers.", "#FF00FF00")] private static readonly Color CandidatePointColor = global::vTools.CommandDefaults.GetColor("vCleanup", "vTools.Commands.vCleanup.CandidatePointColor", Color.Lime); // ARGB object color used for unmatched control-point helpers.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.ControlPointImpactSamples", "Samples used to estimate curve change from candidate removal; integer four or greater.")] private const int ControlPointImpactSamples = 32; // Samples used to estimate curve change from candidate removal; integer four or greater.

  private static readonly int ConfiguredControlPointImpactSamples = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.ControlPointImpactSamples", ControlPointImpactSamples); // Resolved once from advanced defaults; restart Rhino after changes.
  private const int MaximumExactRemovalCandidates = 18; // Largest candidate count evaluated combinatorially; integer one or greater.
  private const int MaximumCombinedImpactPlans = 64; // Maximum exact plans retained for detailed impact scoring; integer one or greater.
  private const int PointKeyDecimals = 9; // Decimal places used to deduplicate geometric span keys; integer zero or greater.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.ZeroLengthTolerance", "Model-unit floor for zero-length geometry checks; greater than zero.")] private const double ZeroLengthTolerance = 1e-12; // Model-unit floor for zero-length geometry checks; greater than zero.

  private static readonly double ConfiguredZeroLengthTolerance = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.ZeroLengthTolerance", ZeroLengthTolerance); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.GripMatchToleranceScale", "Document-tolerance multiplier used when matching control points to grips; greater than zero.")] private const double GripMatchToleranceScale = 0.01; // Document-tolerance multiplier used when matching control points to grips; greater than zero.

  private static readonly double ConfiguredGripMatchToleranceScale = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.GripMatchToleranceScale", GripMatchToleranceScale); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vCleanup", "vTools.Commands.vCleanup.MinimumGripMatchTolerance", "Model-unit floor for control-point grip matching; greater than zero.")] private const double MinimumGripMatchTolerance = 1e-9; // Model-unit floor for control-point grip matching; greater than zero.

  private static readonly double ConfiguredMinimumGripMatchTolerance = global::vTools.CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.MinimumGripMatchTolerance", MinimumGripMatchTolerance); // Resolved once from advanced defaults; restart Rhino after changes.
  private const string ShortSelectionName = "vCleanup Short geometry"; // Rhino named-selection label for short findings; non-empty text.
  private const string OverlapSelectionName = "vCleanup Overlaps"; // Rhino named-selection label for covered curves; non-empty text.
  private const string HelperName = "vCleanup control-point candidate"; // Object name for fallback helper geometry; non-empty text.
  private const string HelperFlagKey = "vCleanup.Helper"; // User-string key identifying cleanup fallback helpers; non-empty text.
  [global::vTools.CommandChoicesAttribute("vCleanup", "preselect|autoDelete", false, "Short", "Overlaps", "All", "No")] private static readonly string[] CleanupTargetNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vCleanup), nameof(CleanupTargetNames)); // Ordered command-option labels matching CleanupTargetMode values.

  private const string SectionName = "vCleanup";
  private const string ThresholdKey = "threshold";
  private const string HighlightKey = "highlightShort";
  private const string PreselectKey = "preselect";
  private const string LegacyPreselectKey = "preselectShort";
  private const string AutoDeleteKey = "autoDelete";
  private const string SimplifyKey = "simplifyCrv";
  private const string OverlapsKey = "overlaps";

  private static double _threshold = ConfiguredDefaultThreshold;
  private static bool _highlightShort = ConfiguredDefaultHighlightShort;
  private static CleanupTargetMode _preselectMode = ConfiguredDefaultPreselectMode;
  private static CleanupTargetMode _autoDeleteMode = ConfiguredDefaultAutoDeleteMode;
  private static bool _simplifyCurves = ConfiguredDefaultSimplifyCurves;
  private static bool _findOverlaps = ConfiguredDefaultFindOverlaps;
  private static CleanupFindingDisplay? _activeConduit;

  private enum CleanupTargetMode
  {
    Short,
    Overlaps,
    All,
    No
  }

  private enum ShortHitKind
  {
    Object,
    Segment,
    ControlPoint
  }

  private sealed record ShortHit(
    Guid SourceId,
    ShortHitKind Kind,
    Curve HighlightCurve,
    int SegmentIndex,
    string SegmentPath,
    int GripIndex,
    Point3d CandidatePoint);

  private sealed class AnalysisResult
  {
    internal List<ShortHit> Hits { get; } = [];
    internal int ScannedObjects { get; set; }
    internal int ScannedSegments { get; set; }
    internal int ScannedControlSpans { get; set; }
    internal int ProtectedEndpointSegments { get; set; }
    internal int ProtectedEndpointSpans { get; set; }
    internal int RedirectedEndpointSpans { get; set; }
  }

  private readonly record struct ImpactScore(
    double MaximumDeviation,
    double RmsDeviation,
    double TotalMovement,
    string TieBreak) : IComparable<ImpactScore>
  {
    public int CompareTo(ImpactScore other)
    {
      var comparison = MaximumDeviation.CompareTo(other.MaximumDeviation);
      if (comparison != 0) return comparison;
      comparison = RmsDeviation.CompareTo(other.RmsDeviation);
      if (comparison != 0) return comparison;
      comparison = TotalMovement.CompareTo(other.TotalMovement);
      return string.Compare(TieBreak, other.TieBreak, StringComparison.Ordinal);
    }
  }

  public override string EnglishName => "vCleanup";

  internal enum ExportCleanupAction { Ask, Delete, Ignore }

  internal readonly record struct LiveFindingSettings(double Threshold, bool FindOverlaps, double OverlapTolerance);
  internal readonly record struct ExportGeometrySettings(double Threshold,bool SimplifyCurves,bool FindOverlaps,double OverlapTolerance,bool OverlapSegments);

  internal static LiveFindingSettings CaptureLiveFindingSettings() =>
    new(_threshold, _findOverlaps, vOverlaps.GetDetectionSettings().Tolerance);

  internal static Result OptimizeForExport(
    RhinoDoc doc, HashSet<Guid> sourceIds, ExportCleanupAction action = ExportCleanupAction.Ask)
    => OptimizeExportCopies(doc, sourceIds, action, null);

  internal static Result OptimizeExportCopies(RhinoDoc doc, HashSet<Guid> sourceIds,
    ExportCleanupAction action,
    Action<IReadOnlyCollection<Guid>, IReadOnlyCollection<(Guid SourceId, Curve Curve, Point3d Point)>>? showFindings,
    Action? refreshScope = null,ExportGeometrySettings? settings=null,Func<int,int,string?,ExportCleanupAction?>? reviewFindings=null,
    Action? keepResponsive=null,Action<int,int>? findingsChanged=null,bool previewOnly=false)
  {
    if(settings==null)LoadOptions();
    double threshold=settings?.Threshold??_threshold;
    bool simplify=settings?.SimplifyCurves??_simplifyCurves,findOverlaps=settings?.FindOverlaps??_findOverlaps;
    var curves = sourceIds
      .Select(id => doc.Objects.FindId(id))
      .Where(obj => obj?.Geometry is Curve)
      .Cast<RhinoObject>()
      .ToList();
    if (curves.Count == 0)
      return Result.Success;

    var selection = doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).ToArray();
    var gripStates = curves.ToDictionary(obj => obj.Id, obj => obj.GripsOn);
    var cleanupClock = System.Diagnostics.Stopwatch.StartNew();
    double reviewMilliseconds = 0;
    try
    {
      var overlapSettings = settings is {} own?(Tolerance:own.OverlapTolerance,OverlapSegments:own.OverlapSegments):vOverlaps.GetDetectionSettings();
      if (simplify)
      {
        var historyRecords = curves
          .SelectMany(obj => HistoryBreakWarning.CaptureAffectedRecords(doc, obj.Id))
          .ToHashSet();
        if (!HistoryBreakWarning.Confirm(doc, "vExportDXF Optimize", historyRecords))
          return Result.Cancel;
      }
      var phaseClock = System.Diagnostics.Stopwatch.StartNew();
      int simplified = simplify ? SimplifyCurvesResponsive(doc, curves,keepResponsive) : 0;
      Log.Write("vExportDXF", "cleanup simplify curves={0} elapsedMs={1:F1}", curves.Count, phaseClock.Elapsed.TotalMilliseconds);
      phaseClock.Restart();
      curves = sourceIds
        .Select(id => doc.Objects.FindId(id))
        .Where(obj => obj?.Geometry is Curve)
        .Cast<RhinoObject>()
        .ToList();
      var overlapResult = findOverlaps
        ? OverlapFinder.FindResponsive(curves, overlapSettings.Tolerance,
            curves,keepResponsive)
        : new OverlapFinder.Result([], [], [], 0, 0, 0, 0);
      var overlapTargets = findOverlaps
        ? ResolveOverlapTargets(doc, overlapResult, (overlapSettings.Tolerance, false),
            sourceIds, out _)
        : [];
      curves = sourceIds
        .Select(id => doc.Objects.FindId(id))
        .Where(obj => obj?.Geometry is Curve)
        .Cast<RhinoObject>()
        .ToList();
      Log.Write("vExportDXF", "cleanup overlap scan curves={0} elapsedMs={1:F1}", curves.Count, phaseClock.Elapsed.TotalMilliseconds);
      phaseClock.Restart();
      var shortAnalysis = AnalyzeShortGeometryResponsive(curves, threshold,keepResponsive);
      Log.Write("vExportDXF", "cleanup short scan curves={0} elapsedMs={1:F1}", curves.Count, phaseClock.Elapsed.TotalMilliseconds);
      int removedOverlaps = 0;
      int removedShort = 0;
      int overlaps = overlapTargets.Count;
      int shortCount = shortAnalysis.Hits.Count;
      findingsChanged?.Invoke(overlaps,shortCount);
      bool ignored = false;
      string? deletionNotice = null;
      try
      {
        for (int pass = 0; overlaps > 0 || shortCount > 0; pass++)
        {
          using var highlight = showFindings == null ? new PreviewDisplay.ObjectHighlighter(doc) : null;
          var findingIds = overlapTargets.Concat(shortAnalysis.Hits.Select(hit => hit.SourceId)).Distinct().ToArray();
          // Finding geometry is borrowed during this callback; retained feedback must duplicate it.
          if (showFindings != null) showFindings(overlapTargets,
            shortAnalysis.Hits.Select(hit => (hit.SourceId, hit.HighlightCurve, hit.CandidatePoint)).ToArray());
          else highlight!.SetObjects(findingIds);
          doc.Views.Redraw();
          if (pass >= MaximumExportCleanupPasses)
            deletionNotice = "Cleanup reached its deletion limit. Ignore the remaining findings to continue, or cancel export.";
          var choice = deletionNotice == null ? action : ExportCleanupAction.Ask;
          if (choice == ExportCleanupAction.Ask)
          {
            var reviewClock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
            if(reviewFindings!=null)
            {
              var response=reviewFindings(overlaps,shortCount,deletionNotice);if(response==null)return Result.Cancel;choice=response.Value;
            }
            else
            {
            using var getter = new GetOption();
            getter.SetCommandPrompt($"{(showFindings == null ? "Cleanup" : "Export-copy cleanup")} found {CountText.Format(overlaps, "overlap")} and {CountText.Format(shortCount, "short finding")}");
            if (deletionNotice != null) RhinoApp.WriteLine(deletionNotice);
            int deleteOption = deletionNotice == null ? getter.AddOption("Delete") : 0;
            getter.AddOption("Ignore");
            if (getter.Get() != GetResult.Option)
              return Result.Cancel;
            choice = getter.OptionIndex() == deleteOption
              ? ExportCleanupAction.Delete : ExportCleanupAction.Ignore;
            }
            }
            finally { reviewMilliseconds += reviewClock.Elapsed.TotalMilliseconds; }
          }
          if (choice == ExportCleanupAction.Ignore)
          {
            ignored = true;
            break;
          }
          if (deletionNotice != null) return Result.Cancel;
          var previousGeometry = CaptureExportCleanupGeometry(doc, sourceIds);
          var historyRecords = overlapTargets.Concat(shortAnalysis.Hits.Select(hit => hit.SourceId))
            .SelectMany(id => HistoryBreakWarning.CaptureAffectedRecords(doc, id)).ToHashSet();
          if (!HistoryBreakWarning.Confirm(doc, "vExportDXF cleanup deletion", historyRecords))
            return Result.Cancel;
          if (findOverlaps && overlapSettings.OverlapSegments)
          {
            overlapTargets = ResolveOverlapTargets(doc, overlapResult, overlapSettings, sourceIds, out _);
            DisposeHits(shortAnalysis.Hits);
            curves = sourceIds.Select(id => doc.Objects.FindId(id))
              .Where(obj => obj?.Geometry is Curve).Cast<RhinoObject>().ToList();
            shortAnalysis = AnalyzeShortGeometryResponsive(curves, threshold,keepResponsive);
          }
          doc.Objects.UnselectAll();
          int deletedOverlaps = DeleteExportOverlapObjects(doc, overlapTargets, sourceIds);
          var wholeShortIds = shortAnalysis.Hits.Where(hit => hit.Kind == ShortHitKind.Object).Select(hit => hit.SourceId).Distinct().ToArray();
          int deletedShortObjects = DeleteExportOverlapObjects(doc, wholeShortIds, sourceIds);
          int deletedShortComponents = DeleteExportShortComponents(doc, shortAnalysis.Hits, sourceIds);
          removedOverlaps += deletedOverlaps;
          removedShort += deletedShortObjects + deletedShortComponents;
          doc.Objects.UnselectAll();
          refreshScope?.Invoke();
          DisposeHits(shortAnalysis.Hits);
          curves = sourceIds.Select(id => doc.Objects.FindId(id))
            .Where(obj => obj?.Geometry is Curve).Cast<RhinoObject>().ToList();
          overlapResult = findOverlaps
            ? OverlapFinder.FindResponsive(curves, overlapSettings.Tolerance, curves,keepResponsive)
            : new OverlapFinder.Result([], [], [], 0, 0, 0, 0);
          overlapTargets = findOverlaps
            ? ResolveOverlapTargets(doc, overlapResult, (overlapSettings.Tolerance, false), sourceIds, out _)
            : [];
          curves = sourceIds.Select(id => doc.Objects.FindId(id))
            .Where(obj => obj?.Geometry is Curve).Cast<RhinoObject>().ToList();
          shortAnalysis = AnalyzeShortGeometryResponsive(curves, threshold,keepResponsive);
          overlaps = overlapTargets.Count;
          shortCount = shortAnalysis.Hits.Count;
          findingsChanged?.Invoke(overlaps,shortCount);
          Log.Write("vExportDXF", $"cleanup review delete pass={pass + 1} remainingOverlaps={overlaps} remainingShort={shortCount}");
          if ((overlaps > 0 || shortCount > 0) &&
              previousGeometry.SetEquals(CaptureExportCleanupGeometry(doc, sourceIds)))
          {
            deletionNotice = "The remaining findings could not be deleted safely. Ignore them to continue, or cancel export.";
            Log.Write("vExportDXF", "cleanup deletion stalled; returning to review with originals unchanged");
          }
        }
        showFindings?.Invoke(overlapTargets,
          shortAnalysis.Hits.Select(hit => (hit.SourceId, hit.HighlightCurve, hit.CandidatePoint)).ToArray());
      }
      finally
      {
        DisposeHits(shortAnalysis.Hits);
      }
      Log.Write("vExportDXF", $"optimize scope={curves.Count} simplified={simplified} " +
        $"removedOverlaps={removedOverlaps} removedShort={removedShort} " +
        $"remainingOverlaps={overlaps}/{overlapResult.ItemCount} " +
        $"remainingShort={shortCount} ignored={ignored} threshold={threshold}");
      if(!previewOnly)RhinoApp.WriteLine($"vExportDXF: {(showFindings == null ? "Optimize" : "Export-copy cleanup")} simplified {CountText.Format(simplified, "curve")}, " +
        $"removed {CountText.Format(removedOverlaps, "overlap")} and {CountText.Format(removedShort, "short finding")}; " +
        (ignored ? $"ignored {CountText.Format(overlaps, "overlap")} and {CountText.Format(shortCount, "short finding")}." : "no cleanup findings remain."));
      return Result.Success;
    }
    catch (Exception ex)
    {
      Log.Write("vExportDXF", $"optimization failed: {ex}");
      if(!previewOnly)RhinoApp.WriteLine(showFindings == null ? "vExportDXF: Optimize failed; see vTools log." :
        "vExportDXF: export-copy cleanup failed; originals unchanged. See vTools log.");
      return Result.Failure;
    }
    finally
    {
      doc.Objects.UnselectAll();
      foreach (var state in gripStates)
        if (doc.Objects.FindId(state.Key) is { } obj && obj.GripsOn != state.Value)
          obj.GripsOn = state.Value;
      foreach (Guid id in selection)
        doc.Objects.Select(id);
      doc.Views.Redraw();
      Log.Write("vExportDXF", "cleanup timing workMs={0:F1} reviewMs={1:F1}", cleanupClock.Elapsed.TotalMilliseconds - reviewMilliseconds, reviewMilliseconds);
    }
  }

  private static HashSet<(Guid ObjectId, uint GeometryCrc)> CaptureExportCleanupGeometry(
    RhinoDoc doc, IEnumerable<Guid> sourceIds) => sourceIds
      .Select(id => doc.Objects.FindId(id))
      .Where(obj => obj is { IsDeleted: false })
      .Select(obj => (obj!.Id, obj.Geometry.DataCRC(0)))
      .ToHashSet();

  private static int DeleteExportOverlapObjects(
    RhinoDoc doc, IReadOnlyCollection<Guid> overlapIds, HashSet<Guid> sourceIds)
  {
    if (overlapIds.Count == 0) return 0;
    int removed = 0;
    uint undoRecord = doc.BeginUndoRecord("vExportDXF Cleanup");
    try
    {
      foreach (Guid id in overlapIds)
      {
        if (!sourceIds.Contains(id))
          throw new InvalidOperationException($"Overlap object {id} is outside the export scope; nothing outside that scope will be deleted.");
        var obj = doc.Objects.FindId(id);
        if (obj == null || obj.IsDeleted) continue;
        if (obj.GripsOn) obj.GripsOn = false;
        if (obj.IsLocked || obj.IsReference || !doc.Objects.Delete(id, quiet: true) ||
            doc.Objects.FindId(id) is { IsDeleted: false })
        {
          Log.Write("vExportDXF", $"overlap deletion rejected source={id} layer={doc.Layers.FindIndex(obj.Attributes.LayerIndex)?.FullPath} mode={obj.Attributes.Mode}");
          throw new InvalidOperationException($"Overlap object {id} could not be deleted; check whether it is locked or reference geometry.");
        }
        removed++;
      }
    }
    finally
    {
      if (undoRecord != 0) doc.EndUndoRecord(undoRecord);
    }
    return removed;
  }

  private static int DeleteExportShortComponents(
    RhinoDoc doc, IReadOnlyCollection<ShortHit> hits, HashSet<Guid> sourceIds)
  {
    int removed = 0;
    uint undoRecord = doc.BeginUndoRecord("vExportDXF Cleanup");
    try
    {
      foreach (var group in hits.Where(hit => hit.Kind != ShortHitKind.Object).GroupBy(hit => hit.SourceId))
      {
        if (!sourceIds.Contains(group.Key))
          throw new InvalidOperationException("Short geometry is outside the export scope; cleanup was cancelled.");
        var obj = doc.Objects.FindId(group.Key);
        if (obj is not { IsDeleted: false }) continue;
        if (obj.IsLocked || obj.IsReference)
          throw new InvalidOperationException("Short geometry could not be deleted; check whether it is locked or reference geometry.");
        // Delete grips in one native call: selected-subobject Delete can be a no-op inside an export dialog.
        var indices = group.Where(hit => hit.Kind == ShortHitKind.ControlPoint)
          .Select(hit => FindMatchingGrip(doc, obj, hit)?.Index)
          .Where(index => index.HasValue).Select(index => index!.Value).Distinct().ToArray();
        if (indices.Length > 0)
          removed += doc.Objects.DeleteGrips(doc.Objects.FindId(group.Key), indices);
      }
    }
    finally
    {
      if (undoRecord != 0) doc.EndUndoRecord(undoRecord);
    }
    return removed;
  }

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    CleanupFindingDisplay.ClearAll();
    LoadOptions();
    var overlapSettings = vOverlaps.GetDetectionSettings();
    var overlapSegments = overlapSettings.OverlapSegments;

    var selectionResult = GetScope(doc, ref overlapSegments, out var scopedIds, out var partDataOnly);
    if (selectionResult != Result.Success)
      return selectionResult;
    if (partDataOnly) return CleanOrphanPartData(doc);
    overlapSettings = (
      overlapSettings.Tolerance,
      overlapSegments);

    var inputObjects = ResolveScopeObjects(doc, scopedIds);
    var inputCurves = inputObjects
      .Where(obj => obj.Geometry is Curve)
      .ToList();
    if (inputCurves.Count == 0)
    {
      RhinoApp.WriteLine("vCleanup: no curve objects found in the cleanup scope.");
      return Result.Nothing;
    }

    var autoDeleteEnabled = _autoDeleteMode != CleanupTargetMode.No;
    var deleteShort = IncludesShort(_autoDeleteMode);
    var deleteOverlaps = IncludesOverlaps(_autoDeleteMode);
    var historyRecords = new HashSet<Guid>();
    if (_simplifyCurves || autoDeleteEnabled ||
        (_findOverlaps && overlapSettings.OverlapSegments))
      foreach (var obj in inputCurves)
        historyRecords.UnionWith(HistoryBreakWarning.CaptureAffectedRecords(doc, obj.Id));
    if (!HistoryBreakWarning.Confirm(doc, EnglishName, historyRecords))
      return Result.Cancel;

    DeleteOldHelpers(doc);
    doc.Objects.UnselectAll();

    var simplifiedCount = _simplifyCurves
      ? SimplifyCurves(doc, inputCurves)
      : 0;
    inputObjects = ResolveScopeObjects(doc, scopedIds);
    inputCurves = inputObjects.Where(obj => obj.Geometry is Curve).ToList();

    OverlapFinder.Result overlaps = new([], [], [], 0, 0, 0, 0);
    var overlapTargetIds = new HashSet<Guid>();
    var splitOverlapSourceCount = 0;
    if (_findOverlaps)
    {
      overlaps = OverlapFinder.Find(
        inputCurves,
        overlapSettings.Tolerance,
        ResolveConnectivityCurves(doc));
      overlapTargetIds = ResolveOverlapTargets(
        doc,
        overlaps,
        overlapSettings,
        scopedIds,
        out var splitSourceCount);
      splitOverlapSourceCount += splitSourceCount;
      inputObjects = ResolveScopeObjects(doc, scopedIds);
      inputCurves = inputObjects.Where(obj => obj.Geometry is Curve).ToList();
    }

    var shortAnalysis = AnalyzeShortGeometry(inputCurves, _threshold);
    var foundOverlapCount = overlapTargetIds.Count;
    var foundShortCount = shortAnalysis.Hits.Count;
    IReadOnlyCollection<Guid> availableOverlapIds = overlapTargetIds;
    IReadOnlyCollection<ShortHit> availableShortHits = shortAnalysis.Hits;
    var helperIds = new List<Guid>();
    var deletedOverlapCount = 0;
    var deletedShortCount = 0;
    var finalSimplifiedCount = 0;

    if (autoDeleteEnabled)
    {
      doc.Objects.UnselectAll();
      if (deleteOverlaps)
        deletedOverlapCount = SelectObjects(doc, overlapTargetIds);
      if (deleteShort)
        deletedShortCount = SelectShortHits(
          doc,
          shortAnalysis.Hits,
          helperIds,
          createHelpers: false);
      doc.Views.Redraw();

      var deleteSucceeded = deletedOverlapCount + deletedShortCount == 0 ||
                            RhinoApp.RunScript("_Delete", false);
      if (!deleteSucceeded)
      {
        Log.Write("vCleanup",
          $"delete failed mode={_autoDeleteMode} overlaps={deletedOverlapCount} short={deletedShortCount}");
        DisposeHits(shortAnalysis.Hits);
        return Result.Failure;
      }

      if (_simplifyCurves)
        finalSimplifiedCount = SimplifyCurves(doc, ResolveScopeObjects(doc, scopedIds));
      simplifiedCount += finalSimplifiedCount;
      DisableNamedSelection(ShortSelectionName);
      DisableNamedSelection(OverlapSelectionName);

      inputCurves = ResolveScopeObjects(doc, scopedIds)
        .Where(obj => obj.Geometry is Curve)
        .ToList();
      if (deleteOverlaps || !_findOverlaps)
      {
        availableOverlapIds = [];
      }
      else
      {
        var refreshedOverlaps = OverlapFinder.Find(
          inputCurves,
          overlapSettings.Tolerance,
          ResolveConnectivityCurves(doc));
        availableOverlapIds = ResolveOverlapTargets(
          doc,
          refreshedOverlaps,
          overlapSettings,
          scopedIds,
          out var refreshedSplitSourceCount);
        splitOverlapSourceCount += refreshedSplitSourceCount;
        inputCurves = ResolveScopeObjects(doc, scopedIds)
          .Where(obj => obj.Geometry is Curve)
          .ToList();
      }
      availableShortHits = deleteShort
        ? []
        : AnalyzeShortGeometry(inputCurves, _threshold).Hits;
      DisposeHits(shortAnalysis.Hits);
    }
    else
    {
      SaveObjectNamedSelection(doc, OverlapSelectionName, overlapTargetIds);
      doc.Objects.UnselectAll();
      var namedShortCount = SelectShortHits(
        doc,
        shortAnalysis.Hits,
        helperIds,
        createHelpers: true);
      SaveCurrentNamedSelection(ShortSelectionName, namedShortCount > 0);
    }

    doc.Objects.UnselectAll();
    var preselectedOverlapCount = IncludesOverlaps(_preselectMode)
      ? SelectObjects(doc, availableOverlapIds)
      : 0;
    var preselectedShortCount = IncludesShort(_preselectMode)
      ? SelectShortHits(
          doc,
          availableShortHits,
          helperIds,
          createHelpers: autoDeleteEnabled)
      : 0;
    if (IncludesShort(_preselectMode))
      foreach (var helperId in helperIds)
        doc.Objects.Select(helperId);

    try
    {
      if (_highlightShort && (availableShortHits.Count > 0 || availableOverlapIds.Count > 0))
      {
        var display = new CleanupFindingDisplay(doc);
        _activeConduit = display;
        display.Disposed += () => { if (ReferenceEquals(_activeConduit, display)) _activeConduit = null; };
        display.SetFindings(availableOverlapIds,
          availableShortHits.Select(hit => (hit.SourceId, hit.HighlightCurve, hit.CandidatePoint)).ToArray());
        display.Track(inputCurves.Select(obj => obj.Id), new(_threshold, _findOverlaps, overlapSettings.Tolerance));
      }
    }
    finally { DisposeHits(availableShortHits); }

    doc.Views.Redraw();
    Log.Write("vCleanup",
      $"scope={inputCurves.Count} simplified={simplifiedCount} " +
      $"finalSimplified={finalSimplifiedCount} " +
      $"overlaps={foundOverlapCount}/{overlaps.ItemCount} short={foundShortCount} " +
      $"overlapSegments={overlapSettings.OverlapSegments} splitSources={splitOverlapSourceCount} " +
      $"autoDelete={_autoDeleteMode} deletedOverlaps={deletedOverlapCount} deletedShort={deletedShortCount} " +
      $"preselect={_preselectMode} selectedOverlaps={preselectedOverlapCount} selectedShort={preselectedShortCount} " +
      $"segments={shortAnalysis.ScannedSegments} controlSpans={shortAnalysis.ScannedControlSpans} " +
      $"helpers={helperIds.Count}");
    var resultAction = autoDeleteEnabled
      ? $"deleted {CountText.Format(deletedOverlapCount, "overlap")} and {CountText.Format(deletedShortCount, "short finding")}"
      : foundOverlapCount + foundShortCount == 0
        ? "no cleanup findings"
        : $"saved '{ShortSelectionName}' and '{OverlapSelectionName}'";
    QueueResultOutput(
      $"vCleanup: simplified {simplifiedCount}; found {CountText.Format(foundOverlapCount, "overlap")} " +
      $"and {CountText.Format(foundShortCount, "short finding")}; {resultAction}.");
    return Result.Success;
  }

  private static void QueueResultOutput(string message)
  {
    EventHandler? idleHandler = null;
    idleHandler = (_, _) =>
    {
      RhinoApp.Idle -= idleHandler;
      RhinoApp.WriteLine(message);
    };
    RhinoApp.Idle += idleHandler;
  }

  private static Result GetScope(
    RhinoDoc doc,
    ref bool overlapSegments,
    out HashSet<Guid> scopedIds,
    out bool partDataOnly)
  {
    scopedIds = [];
    partDataOnly = false;
    using var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt("Select curves to clean; Enter with none selected processes the document");
    getter.GeometryFilter = ObjectType.Curve;
    getter.SubObjectSelect = false;
    getter.GroupSelect = false;
    getter.AcceptNothing(true);
    getter.AcceptNumber(true, false);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnablePreSelect(true, true);
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;

    var preselectionAcknowledged = false;
    while (true)
    {
      getter.ClearCommandOptions();
      var thresholdOption = new OptionDouble(_threshold, 0.0, double.MaxValue);
      var highlightOption = new OptionToggle(_highlightShort, "No", "Yes");
      var simplifyOption = new OptionToggle(_simplifyCurves, "No", "Yes");
      var overlapOption = new OptionToggle(_findOverlaps, "No", "Yes");
      var overlapSegmentsOption = new OptionToggle(overlapSegments, "No", "Yes");
      getter.AddOptionDouble("Threshold", ref thresholdOption);
      getter.AddOptionToggle("HighlightShort", ref highlightOption);
      var preselectOptionIndex = getter.AddOptionList(
        "Preselect",
        CleanupTargetNames,
        (int)_preselectMode);
      var deleteOptionIndex = getter.AddOptionList(
        "AutoDelete",
        CleanupTargetNames,
        (int)_autoDeleteMode);
      getter.AddOptionToggle("SimplifyCrv", ref simplifyOption);
      getter.AddOptionToggle("Overlaps", ref overlapOption);
      getter.AddOptionToggle("OverlapSegments", ref overlapSegmentsOption);
      int orphanDataOption = getter.AddOption(OrphanPartDataOption);

      var result = getter.GetMultiple(0, 0);
      _threshold = thresholdOption.CurrentValue;
      _highlightShort = highlightOption.CurrentValue;
      _simplifyCurves = simplifyOption.CurrentValue;
      _findOverlaps = overlapOption.CurrentValue;
      overlapSegments = overlapSegmentsOption.CurrentValue;
      vOverlaps.SetOverlapSegments(overlapSegments);
      if (result == GetResult.Option)
      {
        var selectedOption = getter.Option();
        if (selectedOption?.Index == orphanDataOption)
        { partDataOnly = true; return Result.Success; }
        if (selectedOption?.Index == preselectOptionIndex)
          _preselectMode = TargetModeFromIndex(selectedOption.CurrentListOptionIndex);
        else if (selectedOption?.Index == deleteOptionIndex)
          _autoDeleteMode = TargetModeFromIndex(selectedOption.CurrentListOptionIndex);
      }
      SaveOptions();

      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      if (result == GetResult.Number)
      {
        _threshold = Math.Max(0.0, getter.Number());
        SaveOptions();
        continue;
      }

      if (result == GetResult.Option)
        continue;

      if (result == GetResult.Object &&
          getter.ObjectsWerePreselected &&
          !preselectionAcknowledged)
      {
        preselectionAcknowledged = true;
        getter.EnablePreSelect(false, true);
        continue;
      }

      if (result is GetResult.Object or GetResult.Nothing)
      {
        scopedIds = doc.Objects
          .GetSelectedObjects(false, false)
          .Where(obj => obj.Geometry is Curve)
          .Select(obj => obj.Id)
          .ToHashSet();
        return Result.Success;
      }

      return Result.Cancel;
    }
  }

  private sealed record OrphanPartRecord(string Key, Dictionary<string, string> Values);
  private sealed class OrphanPartDataUndo(List<OrphanPartRecord> records)
  {
    internal readonly List<OrphanPartRecord> Records = records;
    internal bool Committed;
  }

  private static string? CanonicalPartKey(string key)
  {
    string? prefix = key.StartsWith("group-", StringComparison.Ordinal) ? "group-"
      : key.StartsWith("boundary-", StringComparison.Ordinal) ? "boundary-" : null;
    return prefix != null && Guid.TryParse(key[prefix.Length..], out var id) ? prefix + id.ToString("N") : null;
  }

  private static List<OrphanPartRecord> FindOrphanPartData(RhinoDoc doc)
  {
    var active = new HashSet<string>(StringComparer.Ordinal);
    for (int index = 0; index < doc.Groups.Count; index++)
      if (doc.Groups[index] is { IsDeleted: false } group) active.Add(PartGroupData.GroupKey(group.Id));
    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.AnyObject, NormalObjects = true, HiddenObjects = true,
      LockedObjects = true, ReferenceObjects = true, IdefObjects = false, DeletedObjects = false
    };
    foreach (var obj in doc.Objects.GetObjectList(settings)) Protect(obj);
    foreach (var definition in doc.InstanceDefinitions)
      if (!definition.IsDeleted)
        foreach (var obj in definition.GetObjects() ?? []) Protect(obj);
    void Protect(RhinoObject obj)
    {
      if (obj.IsDeleted) return;
      active.Add("boundary-" + obj.Id.ToString("N"));
      if (obj.Attributes.GetUserString(PartGroupData.SourceSettingsKey) is {} key && CanonicalPartKey(key) is {} source) active.Add(source);
    }
    var keys = PartDataSections.SelectMany(section => doc.Strings.GetEntryNames(section) ?? []).Distinct(StringComparer.Ordinal);
    var records = new List<OrphanPartRecord>();
    foreach (string key in keys)
    {
      string? canonical = CanonicalPartKey(key);
      if (canonical == null || active.Contains(canonical)) continue;
      if (canonical.StartsWith("boundary-", StringComparison.Ordinal) &&
          Guid.TryParse(canonical["boundary-".Length..], out var id) && doc.Objects.FindId(id) is { IsDeleted: false }) continue;
      var values = new Dictionary<string, string>(StringComparer.Ordinal);
      foreach (string section in PartDataSections)
        if (doc.Strings.GetValue(section, key) is {} value) values[section] = value;
      if (values.Count > 0) records.Add(new(key, values));
    }
    return records;
  }

  private static Result CleanOrphanPartData(RhinoDoc doc)
  {
    var records = FindOrphanPartData(doc);
    if (records.Count == 0)
    { RhinoApp.WriteLine("vCleanup: no orphaned part data found."); return Result.Success; }
    using var getter = new GetOption();
    getter.SetCommandPrompt($"Found {CountText.Format(records.Count, "orphaned part record")} ({CountText.Format(records.Sum(record => record.Values.Count), "entry", "entries")})");
    int delete = getter.AddOption("Delete"); getter.AddOption("Cancel"); getter.AcceptNothing(true);
    if (getter.Get() != GetResult.Option || getter.OptionIndex() != delete) return Result.Cancel;
    int removed = DeleteOrphanPartData(doc, records);
    RhinoApp.WriteLine($"vCleanup: removed {removed} orphaned part records; active and referenced part data retained.");
    return Result.Success;
  }

  private static int DeleteOrphanPartData(RhinoDoc doc, IReadOnlyList<OrphanPartRecord> requested)
  {
    var current = FindOrphanPartData(doc).ToDictionary(record => record.Key, StringComparer.Ordinal);
    var records = requested.Where(record => current.TryGetValue(record.Key, out var live) &&
      record.Values.Count == live.Values.Count && record.Values.All(pair => live.Values.TryGetValue(pair.Key, out var value) && value == pair.Value)).ToList();
    if (records.Count == 0) return 0;
    bool recording = doc.UndoRecordingEnabled; doc.UndoRecordingEnabled = true;
    uint undo = doc.BeginUndoRecord(OrphanPartDataUndoName);
    var history = new OrphanPartDataUndo(records);
    try
    {
      if (!doc.AddCustomUndoEvent(OrphanPartDataUndoName, UndoOrphanPartData, history))
        throw new InvalidOperationException("Could not record part-data cleanup for Undo.");
      foreach (var record in records)
        foreach (var pair in record.Values)
        {
          doc.Strings.Delete(pair.Key, record.Key);
          if (doc.Strings.GetValue(pair.Key, record.Key) != null) throw new InvalidOperationException("Could not remove orphaned part data.");
        }
      Log.Write("vCleanup", "removed orphaned part records={0} entries={1}", records.Count, records.Sum(record => record.Values.Count));
      history.Committed = true;
      return records.Count;
    }
    catch
    {
      history.Committed = false;
      foreach (var record in records)
        foreach (var pair in record.Values) doc.Strings.SetString(pair.Key, record.Key, pair.Value);
      throw;
    }
    finally { if (undo != 0) doc.EndUndoRecord(undo); doc.UndoRecordingEnabled = recording; }
  }

  private static void UndoOrphanPartData(object? sender, CustomUndoEventArgs e)
  {
    if (e.Document is not {} doc || e.Tag is not OrphanPartDataUndo { Committed: true } history) return;
    var records = history.Records;
    doc.AddCustomUndoEvent(OrphanPartDataUndoName, UndoOrphanPartData, history);
    var eligible = doc.RedoActive ? FindOrphanPartData(doc).Select(record => record.Key).ToHashSet() : [];
    foreach (var record in records)
      foreach (var pair in record.Values)
      {
        string? current = doc.Strings.GetValue(pair.Key, record.Key);
        if (doc.RedoActive)
        { if (eligible.Contains(record.Key) && current == pair.Value) doc.Strings.Delete(pair.Key, record.Key); }
        else if (current == null) doc.Strings.SetString(pair.Key, record.Key, pair.Value);
      }
  }

  private static List<RhinoObject> ResolveScopeObjects(
    RhinoDoc doc, IReadOnlyCollection<Guid> scopedIds)
  {
    if (scopedIds.Count > 0)
      return scopedIds
        .Select(id => doc.Objects.FindId(id))
        .Where(obj => obj?.Geometry is Curve)
        .Cast<RhinoObject>()
        .ToList();

    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      LockedObjects = true,
      HiddenObjects = true,
      DeletedObjects = false
    };
    return doc.Objects
      .GetObjectList(settings)
      .Where(obj => obj?.Geometry is Curve)
      .ToList();
  }

  private static List<RhinoObject> ResolveConnectivityCurves(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      LockedObjects = true,
      HiddenObjects = false,
      DeletedObjects = false
    };
    return doc.Objects
      .GetObjectList(settings)
      .Where(obj => obj?.Geometry is Curve)
      .ToList();
  }

  private static HashSet<Guid> ResolveOverlapTargets(
    RhinoDoc doc,
    OverlapFinder.Result overlaps,
    (double Tolerance, bool OverlapSegments) settings,
    HashSet<Guid> scopedIds,
    out int splitSourceCount)
  {
    var targetIds = new HashSet<Guid>(overlaps.CoveredObjectIds);
    var remainingPartialIds = new HashSet<Guid>(
      overlaps.PartiallyOverlappingObjectIds);
    splitSourceCount = 0;

    if (!settings.OverlapSegments)
    {
      targetIds.UnionWith(remainingPartialIds);
      return targetIds;
    }

    if (overlaps.PartialOverlapSpans.Count == 0)
      return targetIds;

    var undoRecord = doc.BeginUndoRecord("vCleanup Overlap Segments");
    OverlapSegmentProcessor.Result materialized;
    try
    {
      materialized = OverlapSegmentProcessor.Materialize(
        doc,
        overlaps.PartialOverlapSpans,
        overlaps.CoveredObjectIds,
        settings.Tolerance);
    }
    finally
    {
      if (undoRecord != 0)
        doc.EndUndoRecord(undoRecord);
    }

    splitSourceCount = materialized.SplitSourceCount;
    remainingPartialIds.ExceptWith(materialized.ProcessedSourceObjectIds);
    targetIds.UnionWith(materialized.OverlapObjectIds);
    if (remainingPartialIds.Count > 0)
      Log.Write(
        "vCleanup",
        $"overlap segment isolation skipped sources={remainingPartialIds.Count}");
    if (scopedIds.Count > 0)
      scopedIds.UnionWith(materialized.ResultObjectIds);
    return targetIds;
  }

  private static int SimplifyCurves(RhinoDoc doc, IReadOnlyCollection<RhinoObject> curves)
    =>SimplifyCurvesResponsive(doc,curves,null);
  private static int SimplifyCurvesResponsive(RhinoDoc doc,IReadOnlyCollection<RhinoObject> curves,Action? keepResponsive)
  {
    var undoRecord = doc.BeginUndoRecord("vCleanup SimplifyCrv");
    var simplified = 0;
    try
    {
      foreach (var obj in curves)
      {
        keepResponsive?.Invoke();
        if (obj.Geometry is not Curve source)
          continue;

        var result = source.Simplify(
          CurveSimplifyOptions.All,
          doc.ModelAbsoluteTolerance,
          doc.ModelAngleToleranceRadians);
        if (result == null)
          continue;
        if (doc.Objects.Replace(obj.Id, result))
          simplified++;
      }
    }
    finally
    {
      if (undoRecord != 0)
        doc.EndUndoRecord(undoRecord);
    }
    return simplified;
  }

  private static AnalysisResult AnalyzeShortGeometry(
    IReadOnlyCollection<RhinoObject> objects, double threshold)
    =>AnalyzeShortGeometryResponsive(objects,threshold,null);
  private static AnalysisResult AnalyzeShortGeometryResponsive(IReadOnlyCollection<RhinoObject> objects,double threshold,Action? keepResponsive)
  {
    var analysis = new AnalysisResult();
    try
    {
    foreach (var obj in objects)
    {
      keepResponsive?.Invoke();
      if (obj.Geometry is not Curve curve)
        continue;

      analysis.ScannedObjects++;
      var shortSpanKeys = ScanShortSegments(obj, curve, threshold, analysis);
      ScanShortControlSpans(obj, curve, threshold, shortSpanKeys, analysis);
    }
    return analysis;
    }
    catch{DisposeHits(analysis.Hits);throw;}
  }

  private static HashSet<string> ScanShortSegments(
    RhinoObject obj,
    Curve curve,
    double threshold,
    AnalysisResult analysis)
  {
    var spanKeys = new HashSet<string>(StringComparer.Ordinal);
    var segments = DuplicateSegments(curve);
    try
    {
      if (segments.Count == 0)
      {
        if (curve.GetLength() < threshold)
        {
          if (ProtectOpenCurveEnds && !curve.IsClosed)
            analysis.ProtectedEndpointSegments++;
          else
          {
            var duplicate = curve.DuplicateCurve();
            if (duplicate != null)
            {
              analysis.Hits.Add(new ShortHit(
                obj.Id,
                ShortHitKind.Object,
                duplicate,
                -1,
                "object",
                -1,
                Point3d.Unset));
              spanKeys.Add(SpanKey(curve.PointAtStart, curve.PointAtEnd));
            }
          }
        }
        return spanKeys;
      }

      for (var index = 0; index < segments.Count; index++)
      {
        analysis.ScannedSegments++;
        var segment = segments[index];
        if (segment.GetLength() >= threshold)
          continue;

        var isEndpointSegment = index == 0 || index == segments.Count - 1;
        if (ProtectOpenCurveEnds && !curve.IsClosed && isEndpointSegment)
        {
          analysis.ProtectedEndpointSegments++;
          continue;
        }

        spanKeys.Add(SpanKey(segment.PointAtStart, segment.PointAtEnd));
        var duplicate = segment.DuplicateCurve();
        if (duplicate != null)
          analysis.Hits.Add(new ShortHit(
            obj.Id,
            ShortHitKind.Segment,
            duplicate,
            index,
            $"segment_{index}",
            -1,
            Point3d.Unset));
      }
      return spanKeys;
    }
    finally
    {
      foreach (var segment in segments)
        segment.Dispose();
    }
  }

  private static void ScanShortControlSpans(
    RhinoObject obj,
    Curve sourceCurve,
    double threshold,
    HashSet<string> segmentSpanKeys,
    AnalysisResult analysis)
  {
    var scanItems = CurvesForControlPointScan(sourceCurve);
    try
    {
      for (var itemIndex = 0; itemIndex < scanItems.Count; itemIndex++)
      {
        var (segmentPath, scanCurve) = scanItems[itemIndex];
        using var nurbs = scanCurve.ToNurbsCurve();
        if (nurbs == null || nurbs.Points.Count < 2)
          continue;

        var points = Enumerable.Range(0, nurbs.Points.Count)
          .Select(index => nurbs.Points[index].Location)
          .ToList();
        var pairs = new List<(int A, int B)>();
        for (var index = 0; index < points.Count - 1; index++)
          pairs.Add((index, index + 1));
        if (scanCurve.IsClosed && points.Count > 2 &&
            points[0].DistanceTo(points[^1]) > ConfiguredZeroLengthTolerance)
          pairs.Add((points.Count - 1, 0));

        analysis.ScannedControlSpans += pairs.Count;
        var shortPairs = pairs
          .Where(pair => points[pair.A].DistanceTo(points[pair.B]) < threshold)
          .ToList();
        if (shortPairs.Count == 0)
          continue;

        var protectedIndices = new HashSet<int>();
        if (ProtectOpenCurveEnds && !sourceCurve.IsClosed)
        {
          if (segmentPath == "object")
          {
            protectedIndices.Add(0);
            protectedIndices.Add(points.Count - 1);
          }
          else
          {
            if (itemIndex == 0) protectedIndices.Add(0);
            if (itemIndex == scanItems.Count - 1) protectedIndices.Add(points.Count - 1);
          }
        }

        var removalPlan = PlanControlPointRemovals(
          scanCurve,
          points,
          shortPairs,
          protectedIndices,
          threshold,
          out var redirectedEndpointSpans,
          out var protectedEndpointSpans);
        analysis.RedirectedEndpointSpans += redirectedEndpointSpans;
        analysis.ProtectedEndpointSpans += protectedEndpointSpans;

        foreach (var candidate in removalPlan.OrderBy(index => index))
        {
          var pair = shortPairs.FirstOrDefault(p => p.A == candidate || p.B == candidate);
          if (pair == default && shortPairs.Count > 0)
            pair = shortPairs[0];
          var highlight = new LineCurve(points[pair.A], points[pair.B]);
          var key = SpanKey(points[pair.A], points[pair.B]);
          if (segmentSpanKeys.Contains(key))
            Log.Write("vCleanup", $"control span duplicates short segment source={obj.Id} path={segmentPath}");
          analysis.Hits.Add(new ShortHit(
            obj.Id,
            ShortHitKind.ControlPoint,
            highlight,
            -1,
            segmentPath,
            candidate,
            points[candidate]));
        }
      }
    }
    finally
    {
      foreach (var (_, scanCurve) in scanItems)
        scanCurve.Dispose();
    }
  }

  private static List<Curve> DuplicateSegments(Curve curve)
  {
    var segments = curve.DuplicateSegments();
    if (segments is { Length: > 1 })
      return [.. segments];
    if (segments != null)
      foreach (var segment in segments)
        segment.Dispose();
    return [];
  }

  private static List<(string Path, Curve Curve)> CurvesForControlPointScan(Curve curve)
  {
    var segments = DuplicateSegments(curve);
    if (segments.Count == 0)
    {
      var duplicate = curve.DuplicateCurve();
      return duplicate == null ? [] : [("object", duplicate)];
    }
    return segments
      .Select((segment, index) => ($"segment_{index}", segment))
      .ToList();
  }

  private static HashSet<int> PlanControlPointRemovals(
    Curve curve,
    IReadOnlyList<Point3d> points,
    IReadOnlyList<(int A, int B)> shortPairs,
    HashSet<int> protectedIndices,
    double threshold,
    out int redirectedEndpointSpans,
    out int protectedEndpointSpans)
  {
    redirectedEndpointSpans = 0;
    protectedEndpointSpans = 0;
    var candidates = new HashSet<int>();
    foreach (var (first, second) in shortPairs)
    {
      var protectedCount = (protectedIndices.Contains(first) ? 1 : 0) +
                           (protectedIndices.Contains(second) ? 1 : 0);
      if (protectedCount == 2)
      {
        protectedEndpointSpans++;
        continue;
      }
      if (protectedCount == 1)
        redirectedEndpointSpans++;
      if (!protectedIndices.Contains(first)) candidates.Add(first);
      if (!protectedIndices.Contains(second)) candidates.Add(second);
    }

    if (candidates.Count == 0)
      return [];
    if (candidates.Count > MaximumExactRemovalCandidates)
      return GreedyRemovalPlan(curve, points, candidates, protectedIndices, threshold);

    var candidateList = candidates.OrderBy(index => index).ToArray();
    var validPlans = new List<int[]>();
    for (var removalCount = 1; removalCount <= candidateList.Length; removalCount++)
    {
      foreach (var plan in Combinations(candidateList, removalCount))
        if (RemovalPlanEliminatesShortSpans(
              points, plan, protectedIndices, threshold, curve.IsClosed))
          validPlans.Add(plan);
      if (validPlans.Count > 0)
        break;
    }

    if (validPlans.Count == 0)
      return GreedyRemovalPlan(curve, points, candidates, protectedIndices, threshold);

    if (validPlans.Count > MaximumCombinedImpactPlans)
      validPlans = validPlans
        .OrderBy(plan => plan.Sum(index =>
          CombinedCandidateImpactScore(curve, points, [index]).MaximumDeviation))
        .ThenBy(plan => string.Join(",", plan))
        .Take(MaximumCombinedImpactPlans)
        .ToList();

    var best = validPlans
      .OrderBy(plan => CombinedCandidateImpactScore(curve, points, plan))
      .First();
    return [.. best];
  }

  private static HashSet<int> GreedyRemovalPlan(
    Curve curve,
    IReadOnlyList<Point3d> points,
    HashSet<int> candidatePool,
    HashSet<int> protectedIndices,
    double threshold)
  {
    var removed = new HashSet<int>();
    for (var iteration = 0; iteration < candidatePool.Count; iteration++)
    {
      var unresolved = RemainingPairs(points.Count, removed, curve.IsClosed)
        .FirstOrDefault(pair =>
          points[pair.A].DistanceTo(points[pair.B]) < threshold &&
          !(protectedIndices.Contains(pair.A) && protectedIndices.Contains(pair.B)));
      if (unresolved == default)
        break;

      var options = new[] { unresolved.A, unresolved.B }
        .Where(index => candidatePool.Contains(index) && !protectedIndices.Contains(index))
        .ToList();
      if (options.Count == 0)
        break;
      var best = options
        .OrderBy(index => CombinedCandidateImpactScore(curve, points, removed.Append(index)))
        .First();
      removed.Add(best);
    }
    return removed;
  }

  private static IEnumerable<int[]> Combinations(int[] values, int count)
  {
    var buffer = new int[count];
    return Enumerate(0, 0);

    IEnumerable<int[]> Enumerate(int sourceIndex, int targetIndex)
    {
      if (targetIndex == count)
      {
        yield return (int[])buffer.Clone();
        yield break;
      }

      for (var index = sourceIndex; index <= values.Length - (count - targetIndex); index++)
      {
        buffer[targetIndex] = values[index];
        foreach (var combination in Enumerate(index + 1, targetIndex + 1))
          yield return combination;
      }
    }
  }

  private static bool RemovalPlanEliminatesShortSpans(
    IReadOnlyList<Point3d> points,
    IEnumerable<int> removals,
    HashSet<int> protectedIndices,
    double threshold,
    bool closed)
  {
    var removed = removals.ToHashSet();
    foreach (var (first, second) in RemainingPairs(points.Count, removed, closed))
    {
      if (points[first].DistanceTo(points[second]) >= threshold)
        continue;
      if (protectedIndices.Contains(first) && protectedIndices.Contains(second))
        continue;
      return false;
    }
    return true;
  }

  private static List<(int A, int B)> RemainingPairs(
    int pointCount, HashSet<int> removals, bool closed)
  {
    var remaining = Enumerable.Range(0, pointCount)
      .Where(index => !removals.Contains(index))
      .ToList();
    var pairs = new List<(int A, int B)>();
    for (var index = 0; index < remaining.Count - 1; index++)
      pairs.Add((remaining[index], remaining[index + 1]));
    if (closed && remaining.Count > 1)
      pairs.Add((remaining[^1], remaining[0]));
    return pairs;
  }

  private static ImpactScore CombinedCandidateImpactScore(
    Curve curve,
    IReadOnlyList<Point3d> points,
    IEnumerable<int> candidateIndices)
  {
    var removals = candidateIndices.Distinct().OrderBy(index => index).ToArray();
    if (removals.Length == 0)
      return new ImpactScore(0.0, 0.0, 0.0, string.Empty);

    using var original = curve.ToNurbsCurve();
    using var modified = original?.DuplicateCurve() as NurbsCurve;
    if (original == null || modified == null)
      return new ImpactScore(double.MaxValue, double.MaxValue, double.MaxValue, string.Join(",", removals));

    var totalMovement = 0.0;
    var removalSet = removals.ToHashSet();
    foreach (var candidate in removals)
    {
      var target = CandidateTarget(points, candidate, removalSet, curve.IsClosed);
      if (!target.IsValid || candidate < 0 || candidate >= modified.Points.Count)
        continue;
      totalMovement += points[candidate].DistanceTo(target);
      var controlPoint = modified.Points[candidate];
      if (!modified.Points.SetPoint(candidate, target, controlPoint.Weight))
        return new ImpactScore(double.MaxValue, double.MaxValue, totalMovement, string.Join(",", removals));
    }

    var maximumDeviation = 0.0;
    var squaredDeviation = 0.0;
    for (var sample = 0; sample <= ConfiguredControlPointImpactSamples; sample++)
    {
      var fraction = (double)sample / ConfiguredControlPointImpactSamples;
      var parameter = original.Domain.T0 + original.Domain.Length * fraction;
      var deviation = original.PointAt(parameter).DistanceTo(modified.PointAt(parameter));
      maximumDeviation = Math.Max(maximumDeviation, deviation);
      squaredDeviation += deviation * deviation;
    }
    var rms = Math.Sqrt(squaredDeviation / (ConfiguredControlPointImpactSamples + 1));
    return new ImpactScore(maximumDeviation, rms, totalMovement, string.Join(",", removals));
  }

  private static Point3d CandidateTarget(
    IReadOnlyList<Point3d> points,
    int candidate,
    HashSet<int> removals,
    bool closed)
  {
    var retained = Enumerable.Range(0, points.Count)
      .Where(index => !removals.Contains(index))
      .ToHashSet();
    if (retained.Count == 0)
      return Point3d.Unset;

    int? previous = null;
    int? next = null;
    if (closed)
    {
      for (var offset = 1; offset < points.Count && previous == null; offset++)
      {
        var index = (candidate - offset + points.Count) % points.Count;
        if (retained.Contains(index)) previous = index;
      }
      for (var offset = 1; offset < points.Count && next == null; offset++)
      {
        var index = (candidate + offset) % points.Count;
        if (retained.Contains(index)) next = index;
      }
    }
    else
    {
      for (var index = candidate - 1; index >= 0 && previous == null; index--)
        if (retained.Contains(index)) previous = index;
      for (var index = candidate + 1; index < points.Count && next == null; index++)
        if (retained.Contains(index)) next = index;
    }

    if (previous == null)
      return next.HasValue ? points[next.Value] : Point3d.Unset;
    if (next == null || next == previous)
      return points[previous.Value];
    return ProjectToSegment(points[candidate], points[previous.Value], points[next.Value]);
  }

  private static Point3d ProjectToSegment(
    Point3d point, Point3d segmentStart, Point3d segmentEnd)
  {
    var direction = segmentEnd - segmentStart;
    var lengthSquared = direction.SquareLength;
    if (lengthSquared <= ConfiguredZeroLengthTolerance)
      return segmentStart;
    var parameter = Vector3d.Multiply(point - segmentStart, direction) / lengthSquared;
    return segmentStart + direction * Math.Max(0.0, Math.Min(1.0, parameter));
  }

  private static int SelectShortHits(
    RhinoDoc doc,
    IReadOnlyCollection<ShortHit> hits,
    ICollection<Guid> helperIds,
    bool createHelpers)
  {
    var selected = 0;
    foreach (var hit in hits)
    {
      var obj = doc.Objects.FindId(hit.SourceId);
      if (obj == null)
        continue;

      if (hit.Kind == ShortHitKind.Object)
      {
        if (obj.Select(true) > 0) selected++;
        continue;
      }

      if (hit.Kind == ShortHitKind.Segment)
      {
        var component = new ComponentIndex(
          ComponentIndexType.PolycurveSegment,
          hit.SegmentIndex);
        if (obj.SelectSubObject(component, true, true, true) > 0)
        {
          selected++;
          continue;
        }
        if (createHelpers)
        {
          var helperId = AddHelperCurve(doc, obj, hit.HighlightCurve);
          if (helperId != Guid.Empty)
          {
            helperIds.Add(helperId);
            doc.Objects.Select(helperId);
            selected++;
          }
        }
        continue;
      }

      var grip = FindMatchingGrip(doc, obj, hit);
      if (grip != null && grip.Select(true, true) > 0)
      {
        selected++;
        continue;
      }
      if (createHelpers)
      {
        var helperId = AddHelperPoint(doc, obj, hit.CandidatePoint);
        if (helperId != Guid.Empty)
        {
          helperIds.Add(helperId);
          doc.Objects.Select(helperId);
          selected++;
        }
      }
    }
    return selected;
  }

  private static int SelectObjects(
    RhinoDoc doc,
    IReadOnlyCollection<Guid> objectIds)
  {
    var selected = 0;
    foreach (var objectId in objectIds)
      if (doc.Objects.Select(objectId))
        selected++;
    return selected;
  }

  private static GripObject? FindMatchingGrip(
    RhinoDoc doc, RhinoObject source, ShortHit hit)
  {
    var gripsWereOn = source.GripsOn;
    source.GripsOn = true;
    source = doc.Objects.FindId(source.Id) ?? source;
    var grips = source.GetGrips() ?? [];
    var tolerance = Math.Max(
      ConfiguredMinimumGripMatchTolerance,
      doc.ModelAbsoluteTolerance * ConfiguredGripMatchToleranceScale);

    if (hit.SegmentPath == "object" &&
        hit.GripIndex >= 0 &&
        hit.GripIndex < grips.Length &&
        grips[hit.GripIndex].CurrentLocation.DistanceTo(hit.CandidatePoint) <= tolerance)
      return grips[hit.GripIndex];

    var matches = grips
      .Where(grip => grip.CurrentLocation.DistanceTo(hit.CandidatePoint) <= tolerance)
      .ToList();
    if (matches.Count == 1)
      return matches[0];

    if (!gripsWereOn && !source.GripsSelected)
      source.GripsOn = false;
    return null;
  }

  private static Guid AddHelperCurve(
    RhinoDoc doc, RhinoObject source, Curve geometry)
  {
    var attributes = CreateHelperAttributes(source);
    return doc.Objects.AddCurve(geometry.DuplicateCurve(), attributes);
  }

  private static Guid AddHelperPoint(
    RhinoDoc doc, RhinoObject source, Point3d point)
  {
    var attributes = CreateHelperAttributes(source);
    attributes.ObjectColor = CandidatePointColor;
    return doc.Objects.AddPoint(point, attributes);
  }

  private static ObjectAttributes CreateHelperAttributes(RhinoObject source)
  {
    var attributes = source.Attributes.Duplicate();
    attributes.Name = HelperName;
    attributes.RemoveFromAllGroups();
    attributes.ColorSource = ObjectColorSource.ColorFromObject;
    attributes.ObjectColor = CleanupFindingDisplay.ShortHighlightColor;
    attributes.SetUserString(HelperFlagKey, "1");
    return attributes;
  }

  private static void DeleteOldHelpers(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true,
      LockedObjects = true,
      HiddenObjects = true,
      IncludeGrips = false,
      DeletedObjects = false
    };
    var ids = doc.Objects
      .GetObjectList(settings)
      .Where(obj => obj.Attributes.GetUserString(HelperFlagKey) == "1")
      .Select(obj => obj.Id)
      .ToList();
    if (ids.Count > 0)
      doc.Objects.Delete(ids, quiet: true);
  }

  private static void SaveObjectNamedSelection(
    RhinoDoc doc, string name, IReadOnlyCollection<Guid> objectIds)
  {
    doc.Objects.UnselectAll();
    foreach (var id in objectIds)
      doc.Objects.Select(id);
    SaveCurrentNamedSelection(name, objectIds.Count > 0);
  }

  private static void SaveCurrentNamedSelection(string name, bool hasSelection)
  {
    DisableNamedSelection(name);
    if (!hasSelection)
      return;
    RhinoApp.RunScript($"_-NamedSelections _Save \"{name}\" _Enter", false);
  }

  private static void DisableNamedSelection(string name)
  {
    RhinoApp.RunScript($"_-NamedSelections _Delete \"{name}\" _Enter", false);
  }

  private static string SpanKey(Point3d first, Point3d second)
  {
    static string PointKey(Point3d point) =>
      $"{Math.Round(point.X, PointKeyDecimals):R}," +
      $"{Math.Round(point.Y, PointKeyDecimals):R}," +
      $"{Math.Round(point.Z, PointKeyDecimals):R}";
    var firstKey = PointKey(first);
    var secondKey = PointKey(second);
    return string.Compare(firstKey, secondKey, StringComparison.Ordinal) <= 0
      ? $"{firstKey}|{secondKey}"
      : $"{secondKey}|{firstKey}";
  }

  private static void DisposeHits(IEnumerable<ShortHit> hits)
  {
    foreach (var hit in hits)
      hit.HighlightCurve.Dispose();
  }

  private static bool IncludesShort(CleanupTargetMode mode) =>
    mode is CleanupTargetMode.Short or CleanupTargetMode.All;

  private static bool IncludesOverlaps(CleanupTargetMode mode) =>
    mode is CleanupTargetMode.Overlaps or CleanupTargetMode.All;

  private static CleanupTargetMode TargetModeFromIndex(int index) =>
    index >= 0 && index < CleanupTargetNames.Length
      ? (CleanupTargetMode)index
      : CleanupTargetMode.No;

  private static bool TryParseTargetMode(
    string value,
    out CleanupTargetMode mode)
  {
    return Enum.TryParse(value, true, out mode) &&
           Enum.IsDefined(typeof(CleanupTargetMode), mode);
  }

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      _threshold = ConfiguredDefaultThreshold;
      _highlightShort = ConfiguredDefaultHighlightShort;
      _preselectMode = ConfiguredDefaultPreselectMode;
      _autoDeleteMode = ConfiguredDefaultAutoDeleteMode;
      _simplifyCurves = ConfiguredDefaultSimplifyCurves;
      _findOverlaps = ConfiguredDefaultFindOverlaps;

      if (ToolsOptionStore.TryGetDouble(section, ThresholdKey, out var threshold) && threshold >= 0.0)
        _threshold = threshold;
      if (ToolsOptionStore.TryGetBool(section, HighlightKey, out var highlight))
        _highlightShort = highlight;
      if (ToolsOptionStore.TryGetString(section, PreselectKey, out var preselectMode) &&
          TryParseTargetMode(preselectMode, out var parsedPreselectMode))
        _preselectMode = parsedPreselectMode;
      else if (ToolsOptionStore.TryGetBool(section, LegacyPreselectKey, out var legacyPreselect))
        _preselectMode = legacyPreselect
          ? CleanupTargetMode.Short
          : CleanupTargetMode.No;
      if (ToolsOptionStore.TryGetString(section, AutoDeleteKey, out var autoDeleteMode) &&
          TryParseTargetMode(autoDeleteMode, out var parsedAutoDeleteMode))
        _autoDeleteMode = parsedAutoDeleteMode;
      else if (ToolsOptionStore.TryGetBool(section, AutoDeleteKey, out var legacyAutoDelete))
        _autoDeleteMode = legacyAutoDelete
          ? CleanupTargetMode.All
          : CleanupTargetMode.No;
      if (ToolsOptionStore.TryGetBool(section, SimplifyKey, out var simplify))
        _simplifyCurves = simplify;
      if (ToolsOptionStore.TryGetBool(section, OverlapsKey, out var overlaps))
        _findOverlaps = overlaps;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[ThresholdKey] = _threshold;
      section[HighlightKey] = _highlightShort;
      section.Remove(LegacyPreselectKey);
      section[PreselectKey] = _preselectMode.ToString();
      section[AutoDeleteKey] = _autoDeleteMode.ToString();
      section[SimplifyKey] = _simplifyCurves;
      section[OverlapsKey] = _findOverlaps;
    });

  internal sealed class FindingTracker : IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly uint _documentSerial;
    private readonly HashSet<Guid> _scope;
    private readonly HashSet<Guid> _overlapSources;
    private readonly HashSet<Guid> _shortSources;
    private readonly LiveFindingSettings _settings;
    private readonly Action<IEnumerable<Guid>, IReadOnlyCollection<(Guid SourceId, Curve Curve, Point3d Point)>> _show;
    private readonly Action _closed;
    private bool _pending;
    private bool _disposed;

    internal FindingTracker(RhinoDoc doc, IEnumerable<Guid> scope, IEnumerable<Guid> overlaps,
      IEnumerable<Guid> shorts, LiveFindingSettings settings,
      Action<IEnumerable<Guid>, IReadOnlyCollection<(Guid SourceId, Curve Curve, Point3d Point)>> show, Action closed)
    {
      _doc = doc;
      _documentSerial = doc.RuntimeSerialNumber;
      _scope = scope.Concat(overlaps).Concat(shorts).ToHashSet();
      _overlapSources = overlaps.ToHashSet();
      _shortSources = shorts.ToHashSet();
      _settings = settings;
      _show = show;
      _closed = closed;
      RhinoDoc.AddRhinoObject += ObjectChanged;
      RhinoDoc.DeleteRhinoObject += ObjectChanged;
      RhinoDoc.UndeleteRhinoObject += ObjectChanged;
      RhinoDoc.ReplaceRhinoObject += ObjectReplaced;
      RhinoDoc.ModifyObjectAttributes += AttributesChanged;
      RhinoDoc.CloseDocument += DocumentClosed;
      RhinoApp.Closing += ApplicationClosing;
      RhinoApp.Idle += Idle;
    }

    private void ObjectChanged(object? sender, RhinoObjectEventArgs e)
    {
      var owner = e.TheObject.Document;
      if (owner != null && owner.RuntimeSerialNumber != _documentSerial) return;
      if (e.TheObject.Geometry is Curve && (_scope.Contains(e.ObjectId) || owner != null && _overlapSources.Count > 0))
        _pending = true;
    }

    private void ObjectReplaced(object? sender, RhinoReplaceObjectEventArgs e)
    {
      if (e.Document?.RuntimeSerialNumber == _documentSerial &&
          (_scope.Contains(e.ObjectId) || _overlapSources.Count > 0)) _pending = true;
    }

    private void AttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
      if (e.Document?.RuntimeSerialNumber == _documentSerial && _scope.Contains(e.RhinoObject.Id)) _pending = true;
    }

    private void Idle(object? sender, EventArgs e)
    {
      if (!_disposed && _pending && !Command.InCommand() && !RhinoGet.InGet(_doc)) Refresh();
    }

    internal void Refresh()
    {
      if (_disposed) return;
      _pending = false;
      IReadOnlyCollection<ShortHit> hits = [];
      try
      {
        var curves = _scope.Select(_doc.Objects.FindId)
          .Where(obj => obj is { IsDeleted: false, Geometry: Curve }).Cast<RhinoObject>().ToList();
        hits = AnalyzeShortGeometry(curves.Where(obj => _shortSources.Contains(obj.Id)).ToArray(), _settings.Threshold).Hits;
        IEnumerable<Guid> overlaps = [];
        if (_settings.FindOverlaps && _overlapSources.Count > 0 && curves.Count > 1)
        {
          var analysis = OverlapFinder.Find(curves, _settings.OverlapTolerance, ResolveConnectivityCurves(_doc));
          overlaps = analysis.CoveredObjectIds.Concat(analysis.PartiallyOverlappingObjectIds)
            .Where(_overlapSources.Contains).Distinct();
        }
        _show(overlaps, hits.Select(hit => (hit.SourceId, hit.HighlightCurve, hit.CandidatePoint)).ToArray());
      }
      catch (Exception ex) { Log.Write("vCleanup", $"live finding refresh failed: {ex}"); }
      finally { DisposeHits(hits); }
    }

    private void DocumentClosed(object? sender, DocumentEventArgs e)
    {
      if (e.DocumentSerialNumber == _documentSerial) _closed();
    }

    private void ApplicationClosing(object? sender, EventArgs e) => _closed();

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      RhinoDoc.AddRhinoObject -= ObjectChanged;
      RhinoDoc.DeleteRhinoObject -= ObjectChanged;
      RhinoDoc.UndeleteRhinoObject -= ObjectChanged;
      RhinoDoc.ReplaceRhinoObject -= ObjectReplaced;
      RhinoDoc.ModifyObjectAttributes -= AttributesChanged;
      RhinoDoc.CloseDocument -= DocumentClosed;
      RhinoApp.Closing -= ApplicationClosing;
      RhinoApp.Idle -= Idle;
    }
  }
}
