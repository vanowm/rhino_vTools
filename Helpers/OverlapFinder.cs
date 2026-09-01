using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace vTools.Commands;

/// <summary>
/// Shared covered-curve detector used by vOverlaps and cleanup workflows.
/// </summary>
internal static class OverlapFinder
{
  // Detection defaults and customizable limits
  private const double MinimumCurveLength = 1e-12; // Model-unit length below which a curve item is ignored; greater than zero.
  private const double MinimumLengthTolerance = 1e-6; // Model-unit floor used when comparing duplicate lengths; greater than zero.
  private const int SparseSampleMinimum = 40; // Minimum samples used for one-way coverage checks; integer four or greater.
  private const int SparseSampleMaximum = 220; // Maximum samples used for one-way coverage checks; integer at least SparseSampleMinimum.
  private const int DenseSampleMinimum = 70; // Minimum samples used for duplicate-path checks; integer four or greater.
  private const int DenseSampleMaximum = 280; // Maximum samples used for duplicate-path checks; integer at least DenseSampleMinimum.
  private const double SparseToleranceMultiplier = 6.0; // Tolerance multiple controlling one-way sample density; greater than zero.
  private const double DenseToleranceMultiplier = 4.0; // Tolerance multiple controlling duplicate-path sample density; greater than zero.
  private const double CoverageLengthToleranceMultiplier = 2.0; // Command-tolerance multiple allowed as uncovered arc length when native overlap intervals cover a curve; greater than or equal to one.
  private const double MinimumPartialOverlapToleranceMultiplier = 1.0; // Command-tolerance multiple required for a native partial-overlap interval; greater than zero.
  private const double MinimumPartialOverlapLengthFraction = 1e-8; // Shorter-curve length fraction required for a native partial-overlap interval; positive fraction below one.

  private sealed record NativeOverlapResult(
    bool HasLengthOverlap,
    bool FirstCovered,
    bool SecondCovered,
    IReadOnlyList<NativeOverlapSpan> Spans);

  private readonly record struct NativeOverlapSpan(
    Interval First,
    Interval Second);

  private sealed record ConnectivityCurve(
    Guid ObjectId,
    Curve Curve,
    BoundingBox Bounds);

  internal sealed record Result(
    HashSet<Guid> CoveredObjectIds,
    HashSet<Guid> PartiallyOverlappingObjectIds,
    List<OverlapSpan> PartialOverlapSpans,
    int ItemCount,
    int PairChecks,
    int CoverHits,
    int PartialOverlapHits);

  internal readonly record struct OverlapSpan(
    Guid ObjectId,
    Point3d StartPoint,
    Point3d InteriorPoint,
    Point3d EndPoint,
    bool ParametersMatchSource,
    double StartParameter,
    double InteriorParameter,
    double EndParameter);

  internal static Result Find(
    IReadOnlyCollection<RhinoObject> inputObjects,
    double tolerance,
    IReadOnlyCollection<RhinoObject>? connectivityObjects = null)
  {
    var curveCache = new Dictionary<uint, Curve>();
    var lengthCache = new Dictionary<uint, double>();
    var boundsCache = new Dictionary<uint, BoundingBox>();
    var objectByKey = new Dictionary<uint, RhinoObject>();
    var parentByKey = new Dictionary<uint, RhinoObject>();
    var parametersMatchSourceByKey = new Dictionary<uint, bool>();
    var connectivityCurves = (connectivityObjects ?? inputObjects)
      .Where(obj => obj.Geometry is Curve)
      .Select(obj => new ConnectivityCurve(
        obj.Id,
        (Curve)obj.Geometry,
        obj.Geometry.GetBoundingBox(accurate: true)))
      .ToList();

    uint key = 1;
    foreach (var obj in inputObjects)
    {
      if (obj.Geometry is not Curve curve)
        continue;

      foreach (var segment in ExplodeSegments(curve))
      {
        var duplicate = segment.DuplicateCurve();
        if (duplicate == null)
          continue;

        var length = duplicate.GetLength();
        if (length < MinimumCurveLength)
        {
          duplicate.Dispose();
          continue;
        }

        curveCache[key] = duplicate;
        lengthCache[key] = length;
        boundsCache[key] = duplicate.GetBoundingBox(accurate: true);
        parentByKey[key] = obj;
        parametersMatchSourceByKey[key] = ReferenceEquals(segment, curve);
        key++;
      }
    }

    try
    {
      if (curveCache.Count < 2)
        return new Result([], [], [], curveCache.Count, 0, 0, 0);

      var keys = new List<uint>(curveCache.Keys);
      var partiallyOverlappingObjectIds = new HashSet<Guid>();
      var partialOverlapSpans = new List<OverlapSpan>();
      var pairChecks = 0;
      var coverHits = 0;
      var partialOverlapHits = 0;

      for (var firstIndex = 0; firstIndex < keys.Count - 1; firstIndex++)
      {
        var firstKey = keys[firstIndex];
        var firstCurve = curveCache[firstKey];
        var firstLength = lengthCache[firstKey];

        for (var secondIndex = firstIndex + 1; secondIndex < keys.Count; secondIndex++)
        {
          var secondKey = keys[secondIndex];
          var secondCurve = curveCache[secondKey];
          var secondLength = lengthCache[secondKey];
          pairChecks++;

          if (!BoundingBoxesOverlap(
                boundsCache[firstKey], boundsCache[secondKey], tolerance))
            continue;

          var firstObjectId = SourceObjectId(firstKey, objectByKey, parentByKey);
          var secondObjectId = SourceObjectId(secondKey, objectByKey, parentByKey);
          if (firstObjectId == Guid.Empty || firstObjectId == secondObjectId)
            continue;

          var samePath = CurvesAreSamePathSameSize(
            firstCurve, secondCurve, firstLength, secondLength, tolerance);
          var firstCovered = firstLength <= secondLength &&
                             CurveIsFullyCoveredBy(
                               firstCurve, firstLength, secondCurve, tolerance);
          var secondCovered = secondLength <= firstLength &&
                              CurveIsFullyCoveredBy(
                                secondCurve, secondLength, firstCurve, tolerance);
          var segmentNativeOverlap = FindNativeOverlap(
            firstCurve,
            secondCurve,
            firstLength,
            secondLength,
            tolerance);
          if (!samePath && !firstCovered && !secondCovered &&
              !segmentNativeOverlap.HasLengthOverlap)
            continue;

          var segmentOverlapKey = ChoosePartialOverlapCandidate(
            firstKey,
            secondKey,
            curveCache,
            connectivityCurves,
            objectByKey,
            parentByKey,
            tolerance);
          AddObjectId(
            segmentOverlapKey,
            objectByKey,
            parentByKey,
            partiallyOverlappingObjectIds);
          if (segmentNativeOverlap.Spans.Count > 0)
          {
            AddOverlapSpans(
              segmentOverlapKey,
              firstKey,
              parametersMatchSourceByKey,
              segmentNativeOverlap.Spans,
              curveCache,
              objectByKey,
              parentByKey,
              partialOverlapSpans);
          }
          else if (samePath)
          {
            AddFullOverlapSpan(
              segmentOverlapKey,
              parametersMatchSourceByKey,
              curveCache,
              objectByKey,
              parentByKey,
              partialOverlapSpans);
          }
          else
          {
            var coveredKey = firstCovered ? firstKey : secondKey;
            AddProjectedCoverageSpan(
              segmentOverlapKey,
              coveredKey,
              parametersMatchSourceByKey,
              curveCache,
              objectByKey,
              parentByKey,
              partialOverlapSpans);
          }
          if (samePath)
            coverHits += 2;
          else if (firstCovered || secondCovered ||
                   segmentNativeOverlap.FirstCovered ||
                   segmentNativeOverlap.SecondCovered)
            coverHits++;
          partialOverlapHits++;
        }
      }

      return new Result(
        [],
        partiallyOverlappingObjectIds,
        partialOverlapSpans,
        curveCache.Count,
        pairChecks,
        coverHits,
        partialOverlapHits);
    }
    finally
    {
      foreach (var curve in curveCache.Values)
        curve.Dispose();
    }
  }

  private static IEnumerable<Curve> ExplodeSegments(Curve curve)
  {
    if (curve is PolyCurve polyCurve)
    {
      var segments = polyCurve.Explode();
      if (segments is { Length: > 0 })
      {
        foreach (var segment in segments)
        {
          try
          {
            foreach (var nested in ExplodeSegments(segment))
              yield return nested;
          }
          finally
          {
            segment.Dispose();
          }
        }
        yield break;
      }
    }

    if (curve.TryGetPolyline(out var polyline) && polyline is { Count: > 1 })
    {
      for (var index = 0; index < polyline.Count - 1; index++)
      {
        using var segment = new LineCurve(polyline[index], polyline[index + 1]);
        yield return segment;
      }
      yield break;
    }

    yield return curve;
  }

  private static int AdaptiveSampleCount(double length, double tolerance, bool dense)
  {
    if (tolerance <= 0.0)
      return dense ? DenseSampleMinimum : SparseSampleMinimum;

    var multiplier = dense ? DenseToleranceMultiplier : SparseToleranceMultiplier;
    var minimum = dense ? DenseSampleMinimum : SparseSampleMinimum;
    var maximum = dense ? DenseSampleMaximum : SparseSampleMaximum;
    return Math.Max(
      minimum,
      Math.Min(maximum, (int)Math.Round(length / Math.Max(tolerance * multiplier, 1e-9))));
  }

  private static bool AllSamplesFollowPath(
    Curve source, Curve target, int samples, double tolerance)
  {
    for (var index = 0; index <= samples; index++)
    {
      var point = source.PointAtNormalizedLength((double)index / samples);
      if (!point.IsValid)
        continue;
      if (!target.ClosestPoint(point, out var parameter) ||
          point.DistanceTo(target.PointAt(parameter)) > tolerance)
        return false;
    }
    return true;
  }

  private static bool CurveIsFullyCoveredBy(
    Curve source, double sourceLength, Curve target, double tolerance)
  {
    var samples = AdaptiveSampleCount(sourceLength, tolerance, dense: false);
    return AllSamplesFollowPath(source, target, samples, tolerance);
  }

  private static bool CurvesAreSamePathSameSize(
    Curve first,
    Curve second,
    double firstLength,
    double secondLength,
    double tolerance)
  {
    if (Math.Abs(firstLength - secondLength) > Math.Max(tolerance * 2.0, MinimumLengthTolerance))
      return false;

    if (GeometryBase.GeometryEquals(first, second))
      return true;

    var samples = AdaptiveSampleCount(Math.Max(firstLength, secondLength), tolerance, dense: true);
    return AllSamplesFollowPath(first, second, samples, tolerance) &&
           AllSamplesFollowPath(second, first, samples, tolerance);
  }

  private static NativeOverlapResult FindNativeOverlap(
    Curve first,
    Curve second,
    double firstLength,
    double secondLength,
    double tolerance)
  {
    using var intersections = Intersection.CurveCurve(
      first,
      second,
      tolerance,
      tolerance);
    if (intersections == null || intersections.Count == 0)
      return new NativeOverlapResult(false, false, false, []);

    var firstIntervals = new List<Interval>();
    var secondIntervals = new List<Interval>();
    var overlapSpans = new List<NativeOverlapSpan>();
    foreach (var intersection in intersections)
    {
      if (!intersection.IsOverlap)
        continue;

      firstIntervals.Add(intersection.OverlapA);
      secondIntervals.Add(intersection.OverlapB);
      overlapSpans.Add(new NativeOverlapSpan(
        intersection.OverlapA,
        intersection.OverlapB));
    }

    var firstOverlapLength = CoveredLength(first, firstIntervals);
    var secondOverlapLength = CoveredLength(second, secondIntervals);
    var minimumOverlapLength = Math.Max(
      tolerance * MinimumPartialOverlapToleranceMultiplier,
      Math.Min(firstLength, secondLength) * MinimumPartialOverlapLengthFraction);
    var coverageTolerance = Math.Max(
      tolerance * CoverageLengthToleranceMultiplier,
      MinimumLengthTolerance);

    return new NativeOverlapResult(
      Math.Min(firstOverlapLength, secondOverlapLength) > minimumOverlapLength,
      firstLength - firstOverlapLength <= coverageTolerance,
      secondLength - secondOverlapLength <= coverageTolerance,
      overlapSpans);
  }

  private static void AddOverlapSpans(
    uint selectedKey,
    uint firstKey,
    IReadOnlyDictionary<uint, bool> parametersMatchSourceByKey,
    IReadOnlyCollection<NativeOverlapSpan> nativeSpans,
    IReadOnlyDictionary<uint, Curve> curves,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey,
    ICollection<OverlapSpan> result)
  {
    var objectId = SourceObjectId(
      selectedKey, objectByKey, parentByKey);
    if (objectId == Guid.Empty || !curves.TryGetValue(selectedKey, out var curve))
      return;

    foreach (var nativeSpan in nativeSpans)
    {
      var interval = selectedKey == firstKey
        ? nativeSpan.First
        : nativeSpan.Second;
      if (!interval.IsValid)
        continue;

      var start = Math.Max(curve.Domain.Min, interval.Min);
      var end = Math.Min(curve.Domain.Max, interval.Max);
      if (end <= start || curve.GetLength(new Interval(start, end)) < MinimumCurveLength)
        continue;

      var interior = (start + end) * 0.5;
      result.Add(new OverlapSpan(
        objectId,
        curve.PointAt(start),
        curve.PointAt(interior),
        curve.PointAt(end),
        parametersMatchSourceByKey.TryGetValue(selectedKey, out var parametersMatchSource) &&
        parametersMatchSource,
        start,
        interior,
        end));
    }
  }

  private static void AddFullOverlapSpan(
    uint selectedKey,
    IReadOnlyDictionary<uint, bool> parametersMatchSourceByKey,
    IReadOnlyDictionary<uint, Curve> curves,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey,
    ICollection<OverlapSpan> result)
  {
    var objectId = SourceObjectId(
      selectedKey, objectByKey, parentByKey);
    if (objectId == Guid.Empty || !curves.TryGetValue(selectedKey, out var curve))
      return;

    var start = curve.Domain.Min;
    var end = curve.Domain.Max;
    var interior = (start + end) * 0.5;
    result.Add(new OverlapSpan(
      objectId,
      curve.PointAt(start),
      curve.PointAt(interior),
      curve.PointAt(end),
      parametersMatchSourceByKey.TryGetValue(selectedKey, out var parametersMatchSource) &&
      parametersMatchSource,
      start,
      interior,
      end));
  }

  private static void AddProjectedCoverageSpan(
    uint selectedKey,
    uint coveredKey,
    IReadOnlyDictionary<uint, bool> parametersMatchSourceByKey,
    IReadOnlyDictionary<uint, Curve> curves,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey,
    ICollection<OverlapSpan> result)
  {
    var objectId = SourceObjectId(
      selectedKey, objectByKey, parentByKey);
    if (objectId == Guid.Empty ||
        !curves.TryGetValue(selectedKey, out var selectedCurve) ||
        !curves.TryGetValue(coveredKey, out var coveredCurve))
      return;

    var coveredStart = coveredCurve.Domain.Min;
    var coveredEnd = coveredCurve.Domain.Max;
    var coveredInterior = (coveredStart + coveredEnd) * 0.5;
    var samplePoints = new[]
    {
      coveredCurve.PointAt(coveredStart),
      coveredCurve.PointAt(coveredInterior),
      coveredCurve.PointAt(coveredEnd)
    };
    var selectedParameters = new double[3];
    for (var index = 0; index < samplePoints.Length; index++)
      if (!selectedCurve.ClosestPoint(samplePoints[index], out selectedParameters[index]))
        return;

    result.Add(new OverlapSpan(
      objectId,
      selectedCurve.PointAt(selectedParameters[0]),
      selectedCurve.PointAt(selectedParameters[1]),
      selectedCurve.PointAt(selectedParameters[2]),
      parametersMatchSourceByKey.TryGetValue(selectedKey, out var parametersMatchSource) &&
      parametersMatchSource,
      selectedParameters[0],
      selectedParameters[1],
      selectedParameters[2]));
  }

  private static double CoveredLength(Curve curve, List<Interval> intervals)
  {
    if (intervals.Count == 0 || !curve.Domain.IsValid)
      return 0.0;

    var spans = new List<(double Start, double End)>();
    foreach (var interval in intervals)
    {
      if (!interval.IsValid)
        continue;

      var start = Math.Max(curve.Domain.Min, interval.Min);
      var end = Math.Min(curve.Domain.Max, interval.Max);
      if (end > start)
        spans.Add((start, end));
    }

    spans.Sort((first, second) => first.Start.CompareTo(second.Start));
    var total = 0.0;
    for (var index = 0; index < spans.Count;)
    {
      var start = spans[index].Start;
      var end = spans[index].End;
      index++;
      while (index < spans.Count && spans[index].Start <= end)
      {
        end = Math.Max(end, spans[index].End);
        index++;
      }

      total += curve.GetLength(new Interval(start, end));
    }

    return total;
  }

  private static bool BoundingBoxesOverlap(
    BoundingBox first,
    BoundingBox second,
    double tolerance)
  {
    if (!first.IsValid || !second.IsValid)
      return true;

    return first.Min.X <= second.Max.X + tolerance &&
           first.Max.X + tolerance >= second.Min.X &&
           first.Min.Y <= second.Max.Y + tolerance &&
           first.Max.Y + tolerance >= second.Min.Y &&
           first.Min.Z <= second.Max.Z + tolerance &&
           first.Max.Z + tolerance >= second.Min.Z;
  }

  private static uint ChoosePartialOverlapCandidate(
    uint firstKey,
    uint secondKey,
    IReadOnlyDictionary<uint, Curve> curves,
    IReadOnlyCollection<ConnectivityCurve> connectivityCurves,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey,
    double tolerance)
  {
    var firstObjectId = SourceObjectId(
      firstKey, objectByKey, parentByKey);
    var secondObjectId = SourceObjectId(
      secondKey, objectByKey, parentByKey);
    var firstConnections = ConnectedEndpointCount(
      firstKey,
      firstObjectId,
      secondObjectId,
      curves,
      connectivityCurves,
      tolerance);
    var secondConnections = ConnectedEndpointCount(
      secondKey,
      secondObjectId,
      firstObjectId,
      curves,
      connectivityCurves,
      tolerance);
    if (firstConnections != secondConnections)
      return firstConnections < secondConnections ? firstKey : secondKey;

    var firstSerial = SourceRuntimeSerial(
      firstKey, objectByKey, parentByKey);
    var secondSerial = SourceRuntimeSerial(
      secondKey, objectByKey, parentByKey);
    return firstSerial > secondSerial ? firstKey : secondKey;
  }

  private static int ConnectedEndpointCount(
    uint sourceKey,
    Guid sourceObjectId,
    Guid excludedOverlapObjectId,
    IReadOnlyDictionary<uint, Curve> curves,
    IReadOnlyCollection<ConnectivityCurve> connectivityCurves,
    double tolerance)
  {
    var source = connectivityCurves
      .FirstOrDefault(candidate => candidate.ObjectId == sourceObjectId)
      ?.Curve ?? curves[sourceKey];
    if (source.IsClosed)
      return 2;

    var connectionCount = 0;
    foreach (var endpoint in new[] { source.PointAtStart, source.PointAtEnd })
    {
      foreach (var candidate in connectivityCurves)
      {
        if (candidate.ObjectId == sourceObjectId ||
            candidate.ObjectId == excludedOverlapObjectId ||
            !BoundingBoxContains(candidate.Bounds, endpoint, tolerance) ||
            !candidate.Curve.ClosestPoint(endpoint, out var parameter) ||
            endpoint.DistanceTo(candidate.Curve.PointAt(parameter)) > tolerance)
          continue;

        connectionCount++;
        break;
      }
    }

    return connectionCount;
  }

  private static bool BoundingBoxContains(
    BoundingBox bounds,
    Point3d point,
    double tolerance)
  {
    if (!bounds.IsValid || !point.IsValid)
      return true;

    return point.X >= bounds.Min.X - tolerance &&
           point.X <= bounds.Max.X + tolerance &&
           point.Y >= bounds.Min.Y - tolerance &&
           point.Y <= bounds.Max.Y + tolerance &&
           point.Z >= bounds.Min.Z - tolerance &&
           point.Z <= bounds.Max.Z + tolerance;
  }

  private static uint SourceRuntimeSerial(
    uint key,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey)
  {
    if (parentByKey.TryGetValue(key, out var parent))
      return parent.RuntimeSerialNumber;
    return objectByKey.TryGetValue(key, out var obj)
      ? obj.RuntimeSerialNumber
      : key;
  }

  private static Guid SourceObjectId(
    uint key,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey)
  {
    if (parentByKey.TryGetValue(key, out var parent))
      return parent.Id;
    return objectByKey.TryGetValue(key, out var obj)
      ? obj.Id
      : Guid.Empty;
  }

  private static void AddObjectId(
    uint key,
    IReadOnlyDictionary<uint, RhinoObject> objectByKey,
    IReadOnlyDictionary<uint, RhinoObject> parentByKey,
    HashSet<Guid> objectIds)
  {
    var source = parentByKey.TryGetValue(key, out var parent)
      ? parent
      : objectByKey.TryGetValue(key, out var obj) ? obj : null;
    if (source != null)
      objectIds.Add(source.Id);
  }

}
