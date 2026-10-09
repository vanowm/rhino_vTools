using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace vTools;

internal static class GroupBoundaryTopology
{
  // Defaults and customizable constants
  private static readonly double[] PlaneSamples = [0.0, 0.25, 0.5, 0.75, 1.0]; // Normalized curve parameters in [0, 1] used to fit a component plane; depth is checked against the configured boundary tolerance afterward.

  internal static List<int[]> ConnectedComponents(
    IReadOnlyList<Point3d> starts, IReadOnlyList<Point3d> ends, double tolerance)
  {
    var parents = Enumerable.Range(0, starts.Count).ToArray();
    int Root(int index)
    {
      while (parents[index] != index)
      {
        parents[index] = parents[parents[index]];
        index = parents[index];
      }
      return index;
    }

    for (var i = 0; i < starts.Count; i++)
    {
      for (var j = 0; j < i; j++)
      {
        if (starts[i].DistanceTo(starts[j]) <= tolerance ||
            starts[i].DistanceTo(ends[j]) <= tolerance ||
            ends[i].DistanceTo(starts[j]) <= tolerance ||
            ends[i].DistanceTo(ends[j]) <= tolerance)
          parents[Root(i)] = Root(j);
      }
    }
    return Enumerable.Range(0, starts.Count).GroupBy(Root)
      .Select(group => group.ToArray()).ToList();
  }

  internal static List<Curve> PlanarRegions(
    IReadOnlyList<Curve> curves, double planeTolerance, double boundaryTolerance)
  {
    var result = new List<Curve>();
    var points = curves.SelectMany(curve => PlaneSamples.Select(
      fraction => curve.PointAt(curve.Domain.ParameterAt(fraction))));
    // Boolean regions project temporary results; never flatten the source objects.
    var allowedDepth = Math.Max(planeTolerance, boundaryTolerance);
    if (Plane.FitPlaneToPoints(points, out var plane) != PlaneFitResult.Success ||
        curves.Any(curve => !curve.IsInPlane(plane, allowedDepth)))
      return result;

    using var regions = Curve.CreateBooleanRegions(curves, plane, combineRegions: true, boundaryTolerance);
    if (regions != null)
      for (var region = 0; region < regions.RegionCount; region++)
        result.AddRange(regions.RegionCurves(region));
    return result;
  }
}
