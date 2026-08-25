using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

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

  internal sealed record Result(
    HashSet<Guid> CoveredObjectIds,
    int ItemCount,
    int PairChecks,
    int CoverHits);

  internal static Result Find(
    IReadOnlyCollection<RhinoObject> inputObjects,
    double tolerance,
    bool segments)
  {
    var curveCache = new Dictionary<uint, Curve>();
    var lengthCache = new Dictionary<uint, double>();
    var objectByKey = new Dictionary<uint, RhinoObject>();
    var parentByKey = new Dictionary<uint, RhinoObject>();

    if (segments)
    {
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
          parentByKey[key] = obj;
          key++;
        }
      }
    }
    else
    {
      foreach (var obj in inputObjects)
      {
        if (obj.Geometry is not Curve curve)
          continue;

        var duplicate = curve.DuplicateCurve();
        if (duplicate == null)
          continue;

        var key = obj.RuntimeSerialNumber;
        curveCache[key] = duplicate;
        lengthCache[key] = duplicate.GetLength();
        objectByKey[key] = obj;
      }
    }

    try
    {
      if (curveCache.Count < 2)
        return new Result([], curveCache.Count, 0, 0);

      var keys = new List<uint>(curveCache.Keys);
      var coveredBy = new Dictionary<uint, HashSet<uint>>();
      var duplicatePairs = new List<(uint A, uint B)>();
      var pairChecks = 0;
      var coverHits = 0;

      foreach (var key in keys)
        coveredBy[key] = [];

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

          if (CurvesAreSamePathSameSize(
                firstCurve, secondCurve, firstLength, secondLength, tolerance))
          {
            coveredBy[firstKey].Add(secondKey);
            coveredBy[secondKey].Add(firstKey);
            duplicatePairs.Add((firstKey, secondKey));
            coverHits += 2;
            continue;
          }

          if (firstLength <= secondLength)
          {
            if (CurveIsFullyCoveredBy(firstCurve, firstLength, secondCurve, tolerance))
            {
              coveredBy[firstKey].Add(secondKey);
              coverHits++;
            }
          }
          else if (CurveIsFullyCoveredBy(secondCurve, secondLength, firstCurve, tolerance))
          {
            coveredBy[secondKey].Add(firstKey);
            coverHits++;
          }
        }
      }

      var duplicateGroups = DuplicateGroups(keys, duplicatePairs);
      var originalsToKeep = new HashSet<uint>();
      foreach (var group in duplicateGroups)
      {
        if (group.Count < 2)
          continue;

        var oldestParent = uint.MaxValue;
        foreach (var key in group)
        {
          var parentSerial = segments && parentByKey.TryGetValue(key, out var parent)
            ? parent.RuntimeSerialNumber
            : key;
          if (parentSerial < oldestParent)
            oldestParent = parentSerial;
        }
        originalsToKeep.Add(oldestParent);
      }

      var coveredObjectIds = new HashSet<Guid>();
      foreach (var key in keys)
      {
        if (coveredBy[key].Count == 0)
          continue;

        if (segments)
        {
          if (!parentByKey.TryGetValue(key, out var parent) ||
              originalsToKeep.Contains(parent.RuntimeSerialNumber))
            continue;
          coveredObjectIds.Add(parent.Id);
        }
        else if (objectByKey.TryGetValue(key, out var obj) && !originalsToKeep.Contains(key))
        {
          coveredObjectIds.Add(obj.Id);
        }
      }

      return new Result(coveredObjectIds, curveCache.Count, pairChecks, coverHits);
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

    var samples = AdaptiveSampleCount(Math.Max(firstLength, secondLength), tolerance, dense: true);
    return AllSamplesFollowPath(first, second, samples, tolerance) &&
           AllSamplesFollowPath(second, first, samples, tolerance);
  }

  private static uint FindRoot(Dictionary<uint, uint> parents, uint value)
  {
    while (parents[value] != value)
    {
      parents[value] = parents[parents[value]];
      value = parents[value];
    }
    return value;
  }

  private static void Union(
    Dictionary<uint, uint> parents,
    Dictionary<uint, int> ranks,
    uint first,
    uint second)
  {
    var firstRoot = FindRoot(parents, first);
    var secondRoot = FindRoot(parents, second);
    if (firstRoot == secondRoot)
      return;

    if (ranks[firstRoot] < ranks[secondRoot])
      parents[firstRoot] = secondRoot;
    else if (ranks[firstRoot] > ranks[secondRoot])
      parents[secondRoot] = firstRoot;
    else
    {
      parents[secondRoot] = firstRoot;
      ranks[firstRoot]++;
    }
  }

  private static List<List<uint>> DuplicateGroups(
    List<uint> keys, List<(uint A, uint B)> duplicatePairs)
  {
    var parents = new Dictionary<uint, uint>();
    var ranks = new Dictionary<uint, int>();
    foreach (var key in keys)
    {
      parents[key] = key;
      ranks[key] = 0;
    }

    foreach (var (first, second) in duplicatePairs)
      Union(parents, ranks, first, second);

    var groupsByRoot = new Dictionary<uint, List<uint>>();
    foreach (var key in keys)
    {
      var root = FindRoot(parents, key);
      if (!groupsByRoot.TryGetValue(root, out var group))
      {
        group = [];
        groupsByRoot[root] = group;
      }
      group.Add(key);
    }
    return [.. groupsByRoot.Values];
  }
}
