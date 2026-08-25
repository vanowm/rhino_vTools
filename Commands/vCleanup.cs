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
public sealed class vCleanup : Command
{
  // Option defaults
  private const double DefaultThreshold = 0.02; // Maximum short-geometry length in model units; zero or greater.
  private const bool DefaultHighlightShort = false; // true draws short findings in magenta; false uses no cleanup overlay.
  private const CleanupTargetMode DefaultPreselectMode = CleanupTargetMode.Short; // Short, Overlaps, All, or No findings selected after cleanup.
  private const CleanupTargetMode DefaultAutoDeleteMode = CleanupTargetMode.No; // Short, Overlaps, All, or No findings deleted during cleanup.
  private const bool DefaultSimplifyCurves = true; // true simplifies scoped curves before analysis; false preserves their existing structure.
  private const bool DefaultFindOverlaps = true; // true runs shared vOverlaps detection before the short scan; false skips overlap detection.
  private const bool ProtectOpenCurveEnds = true; // true excludes open-curve endpoint segments and endpoint control points; false permits them.

  // Customizable appearance, output, limits, tolerance, and sampling
  private static readonly Color ShortHighlightColor = Color.Magenta; // ARGB viewport color used for all short-geometry overlays.
  private static readonly Color OverlapHighlightColor = Color.Cyan; // ARGB viewport color used for covered-curve overlap overlays.
  private static readonly Color CandidatePointColor = Color.Lime; // ARGB object color used for unmatched control-point helpers.
  private const int ShortHighlightThicknessPixels = 7; // Viewport pixel width for magenta short-curve overlays; integer one or greater.
  private const int CandidatePointSizePixels = 10; // Viewport pixel diameter for control-point candidates; integer one or greater.
  private const int ControlPointImpactSamples = 32; // Samples used to estimate curve change from candidate removal; integer four or greater.
  private const int MaximumExactRemovalCandidates = 18; // Largest candidate count evaluated combinatorially; integer one or greater.
  private const int MaximumCombinedImpactPlans = 64; // Maximum exact plans retained for detailed impact scoring; integer one or greater.
  private const int PointKeyDecimals = 9; // Decimal places used to deduplicate geometric span keys; integer zero or greater.
  private const double ZeroLengthTolerance = 1e-12; // Model-unit floor for zero-length geometry checks; greater than zero.
  private const double GripMatchToleranceScale = 0.01; // Document-tolerance multiplier used when matching control points to grips; greater than zero.
  private const double MinimumGripMatchTolerance = 1e-9; // Model-unit floor for control-point grip matching; greater than zero.
  private const string ShortSelectionName = "vCleanup Short geometry"; // Rhino named-selection label for short findings; non-empty text.
  private const string OverlapSelectionName = "vCleanup Overlaps"; // Rhino named-selection label for covered curves; non-empty text.
  private const string HelperName = "vCleanup control-point candidate"; // Object name for fallback helper geometry; non-empty text.
  private const string HelperFlagKey = "vCleanup.Helper"; // User-string key identifying cleanup fallback helpers; non-empty text.
  private static readonly string[] CleanupTargetNames = ["Short", "Overlaps", "All", "No"]; // Ordered command-option labels matching CleanupTargetMode values.

  private const string SectionName = "vCleanup";
  private const string ThresholdKey = "threshold";
  private const string HighlightKey = "highlightShort";
  private const string PreselectKey = "preselect";
  private const string LegacyPreselectKey = "preselectShort";
  private const string AutoDeleteKey = "autoDelete";
  private const string SimplifyKey = "simplifyCrv";
  private const string OverlapsKey = "overlaps";

  private static double _threshold = DefaultThreshold;
  private static bool _highlightShort = DefaultHighlightShort;
  private static CleanupTargetMode _preselectMode = DefaultPreselectMode;
  private static CleanupTargetMode _autoDeleteMode = DefaultAutoDeleteMode;
  private static bool _simplifyCurves = DefaultSimplifyCurves;
  private static bool _findOverlaps = DefaultFindOverlaps;
  private static ShortGeometryConduit? _activeConduit;

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

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    DisableHighlight(doc);
    LoadOptions();

    var selectionResult = GetScope(doc, out var scopedIds);
    if (selectionResult != Result.Success)
      return selectionResult;

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
    if (_simplifyCurves || autoDeleteEnabled)
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

    (double Tolerance, bool Segments) overlapSettings = _findOverlaps
      ? vOverlaps.GetDetectionSettings()
      : default;
    OverlapFinder.Result overlaps = new([], 0, 0, 0);
    if (_findOverlaps)
      overlaps = OverlapFinder.Find(
        inputCurves,
        overlapSettings.Tolerance,
        overlapSettings.Segments);

    var shortAnalysis = AnalyzeShortGeometry(inputCurves, _threshold);
    var foundOverlapCount = overlaps.CoveredObjectIds.Count;
    var foundShortCount = shortAnalysis.Hits.Count;
    IReadOnlyCollection<Guid> availableOverlapIds = overlaps.CoveredObjectIds;
    IReadOnlyCollection<ShortHit> availableShortHits = shortAnalysis.Hits;
    var helperIds = new List<Guid>();
    var deletedOverlapCount = 0;
    var deletedShortCount = 0;
    var finalSimplifiedCount = 0;

    if (autoDeleteEnabled)
    {
      doc.Objects.UnselectAll();
      if (deleteOverlaps)
        deletedOverlapCount = SelectObjects(doc, overlaps.CoveredObjectIds);
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
      availableOverlapIds = deleteOverlaps || !_findOverlaps
        ? []
        : OverlapFinder.Find(
            inputCurves,
            overlapSettings.Tolerance,
            overlapSettings.Segments).CoveredObjectIds;
      availableShortHits = deleteShort
        ? []
        : AnalyzeShortGeometry(inputCurves, _threshold).Hits;
      DisposeHits(shortAnalysis.Hits);
    }
    else
    {
      SaveObjectNamedSelection(doc, OverlapSelectionName, overlaps.CoveredObjectIds);
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

    var conduitOwnsShortHits = false;
    if (_highlightShort &&
        (availableShortHits.Count > 0 || availableOverlapIds.Count > 0))
    {
      _activeConduit = new ShortGeometryConduit(
        doc,
        availableShortHits,
        availableOverlapIds)
      {
        Enabled = true
      };
      conduitOwnsShortHits = true;
    }

    if (!conduitOwnsShortHits)
      DisposeHits(availableShortHits);

    doc.Views.Redraw();
    Log.Write("vCleanup",
      $"scope={inputCurves.Count} simplified={simplifiedCount} " +
      $"finalSimplified={finalSimplifiedCount} " +
      $"overlaps={foundOverlapCount}/{overlaps.ItemCount} short={foundShortCount} " +
      $"autoDelete={_autoDeleteMode} deletedOverlaps={deletedOverlapCount} deletedShort={deletedShortCount} " +
      $"preselect={_preselectMode} selectedOverlaps={preselectedOverlapCount} selectedShort={preselectedShortCount} " +
      $"segments={shortAnalysis.ScannedSegments} controlSpans={shortAnalysis.ScannedControlSpans} " +
      $"helpers={helperIds.Count}");
    var resultAction = autoDeleteEnabled
      ? $"deleted {deletedOverlapCount} overlaps and {deletedShortCount} short findings"
      : $"saved '{ShortSelectionName}' and '{OverlapSelectionName}'";
    RhinoApp.WriteLine(
      $"vCleanup: simplified {simplifiedCount}; found {foundOverlapCount} overlaps " +
      $"and {foundShortCount} short findings; {resultAction}.");
    return Result.Success;
  }

  private static Result GetScope(RhinoDoc doc, out HashSet<Guid> scopedIds)
  {
    scopedIds = [];
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

      var result = getter.GetMultiple(0, 0);
      _threshold = thresholdOption.CurrentValue;
      _highlightShort = highlightOption.CurrentValue;
      _simplifyCurves = simplifyOption.CurrentValue;
      _findOverlaps = overlapOption.CurrentValue;
      if (result == GetResult.Option)
      {
        var selectedOption = getter.Option();
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

  private static int SimplifyCurves(RhinoDoc doc, IReadOnlyCollection<RhinoObject> curves)
  {
    var undoRecord = doc.BeginUndoRecord("vCleanup SimplifyCrv");
    var simplified = 0;
    try
    {
      foreach (var obj in curves)
      {
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
  {
    var analysis = new AnalysisResult();
    foreach (var obj in objects)
    {
      if (obj.Geometry is not Curve curve)
        continue;

      analysis.ScannedObjects++;
      var shortSpanKeys = ScanShortSegments(obj, curve, threshold, analysis);
      ScanShortControlSpans(obj, curve, threshold, shortSpanKeys, analysis);
    }
    return analysis;
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
            points[0].DistanceTo(points[^1]) > ZeroLengthTolerance)
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
    for (var sample = 0; sample <= ControlPointImpactSamples; sample++)
    {
      var fraction = (double)sample / ControlPointImpactSamples;
      var parameter = original.Domain.T0 + original.Domain.Length * fraction;
      var deviation = original.PointAt(parameter).DistanceTo(modified.PointAt(parameter));
      maximumDeviation = Math.Max(maximumDeviation, deviation);
      squaredDeviation += deviation * deviation;
    }
    var rms = Math.Sqrt(squaredDeviation / (ControlPointImpactSamples + 1));
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
    if (lengthSquared <= ZeroLengthTolerance)
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
      MinimumGripMatchTolerance,
      doc.ModelAbsoluteTolerance * GripMatchToleranceScale);

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
    attributes.ObjectColor = ShortHighlightColor;
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

  private static void DisableHighlight(RhinoDoc doc)
  {
    if (_activeConduit != null)
    {
      _activeConduit.Enabled = false;
      _activeConduit.Dispose();
      _activeConduit = null;
      doc.Views.Redraw();
    }
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
      _threshold = DefaultThreshold;
      _highlightShort = DefaultHighlightShort;
      _preselectMode = DefaultPreselectMode;
      _autoDeleteMode = DefaultAutoDeleteMode;
      _simplifyCurves = DefaultSimplifyCurves;
      _findOverlaps = DefaultFindOverlaps;

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

  private sealed class ShortGeometryConduit(
    RhinoDoc doc,
    IReadOnlyCollection<ShortHit> hits,
    IReadOnlyCollection<Guid> overlapIds) : DisplayConduit, IDisposable
  {
    private bool _disposed;

    public void Dispose()
    {
      if (_disposed)
        return;

      _disposed = true;
      DisposeHits(hits);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      foreach (var overlapId in overlapIds)
      {
        if (doc.Objects.FindId(overlapId)?.Geometry is not Curve overlapCurve)
          continue;
        e.Display.DrawCurve(
          overlapCurve,
          OverlapHighlightColor,
          ShortHighlightThicknessPixels);
      }

      foreach (var hit in hits)
      {
        if (doc.Objects.FindId(hit.SourceId) == null)
          continue;
        e.Display.DrawCurve(
          hit.HighlightCurve,
          ShortHighlightColor,
          ShortHighlightThicknessPixels);
        if (hit.Kind == ShortHitKind.ControlPoint)
          e.Display.DrawPoint(
            hit.CandidatePoint,
            PointStyle.ControlPoint,
            CandidatePointSizePixels,
            ShortHighlightColor);
      }
    }
  }
}
