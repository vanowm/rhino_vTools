using Rhino;
using Rhino.Geometry;

namespace vTools.Commands;

internal static class FaceOverlapFinder
{
  // Detection defaults and customizable limits
  private const int DefaultInteriorGridDivisions = 10; // Number of equal U/V cells sampled inside each curved face; integer >= 2.
  private const int DefaultBoundarySamples = 16; // Samples taken around each face loop to detect narrow curved overlaps; integer >= 2.
  private const double DefaultNormalAngleDegrees = 2.0; // Maximum angular difference between coincident face normals, in degrees from parallel or antiparallel.
  private const double MinimumPlanarAreaToleranceFactor = 4.0; // Multiplier applied to tolerance squared when rejecting edge-only planar contact.
  private const int PlanarBooleanRefinementLevels = 3; // Number of planar-intersection attempts from the requested tolerance toward finer tolerances; integer >= 1.
  private const double PlanarBooleanToleranceScale = 0.1; // Tolerance multiplier applied at each planar-intersection refinement level; greater than 0.0 and less than 1.0.

  internal readonly record struct FaceReference(Guid ObjectId, int FaceIndex);

  internal readonly record struct FacePair(FaceReference First, FaceReference Second);

  internal sealed record FaceItem(FaceReference Reference, BrepFace Face);

  internal sealed record Result(
    HashSet<FaceReference> OverlappingFaces,
    IReadOnlyList<FacePair> OverlappingPairs,
    int FaceCount,
    int PairChecks,
    int OverlapHits);

  internal static Result Find(
    IReadOnlyCollection<FaceItem> inputFaces,
    double coincidenceTolerance,
    double areaTolerance)
  {
    coincidenceTolerance = Math.Max(
      coincidenceTolerance,
      RhinoMath.ZeroTolerance);
    areaTolerance = Math.Max(areaTolerance, RhinoMath.ZeroTolerance);
    var prepared = inputFaces
      .Where(item => item.Face != null && item.Face.IsValid)
      .Select(item => Prepare(item, coincidenceTolerance, areaTolerance))
      .Where(item => item != null)
      .Cast<PreparedFace>()
      .ToList();

    var overlaps = new HashSet<FaceReference>();
    var overlappingPairs = new List<FacePair>();
    var pairChecks = 0;
    var overlapHits = 0;

    try
    {
      for (var firstIndex = 0; firstIndex < prepared.Count; firstIndex++)
      {
        var first = prepared[firstIndex];
        for (var secondIndex = firstIndex + 1; secondIndex < prepared.Count; secondIndex++)
        {
          var second = prepared[secondIndex];
          if (!BoundingBoxesMeet(
                first.Bounds,
                second.Bounds,
                coincidenceTolerance))
            continue;

          pairChecks++;
          if (!FacesShareArea(
                first,
                second,
                coincidenceTolerance,
                areaTolerance))
            continue;

          overlaps.Add(first.Reference);
          overlaps.Add(second.Reference);
          overlappingPairs.Add(new FacePair(first.Reference, second.Reference));
          overlapHits++;
        }
      }
    }
    finally
    {
      foreach (var face in prepared)
        face.SingleFace.Dispose();
    }

    return new Result(
      overlaps,
      overlappingPairs,
      prepared.Count,
      pairChecks,
      overlapHits);
  }

  internal static List<Brep> CreateOverlapAreas(
    IReadOnlyCollection<FaceItem> inputFaces,
    IReadOnlyCollection<FacePair> overlappingPairs,
    double coincidenceTolerance,
    double areaTolerance)
  {
    coincidenceTolerance = Math.Max(
      coincidenceTolerance,
      RhinoMath.ZeroTolerance);
    areaTolerance = Math.Max(areaTolerance, RhinoMath.ZeroTolerance);
    var facesByReference = inputFaces.ToDictionary(face => face.Reference);
    var overlapAreas = new List<Brep>();

    foreach (var pair in overlappingPairs)
    {
      if (!facesByReference.TryGetValue(pair.First, out var first) ||
          !facesByReference.TryGetValue(pair.Second, out var second))
        continue;

      var planarAreas = CreatePlanarOverlapAreas(
        first.Face,
        second.Face,
        coincidenceTolerance,
        areaTolerance);
      if (planarAreas is { Count: > 0 })
      {
        Log.Write(
          "vOverlaps",
          $"area pair={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
          $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} mode=planar " +
          $"breps={planarAreas.Count} regions={planarAreas.Sum(area => area.Faces.Count)} " +
          $"area={planarAreas.Sum(BrepArea):G6}");
        overlapAreas.AddRange(planarAreas);
        continue;
      }

      var containedArea = CreateContainedOverlapArea(
        first.Face,
        second.Face,
        coincidenceTolerance,
        areaTolerance);
      if (containedArea != null)
      {
        Log.Write(
          "vOverlaps",
          $"area pair={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
          $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} mode=contained " +
          $"regions={containedArea.Faces.Count} area={BrepArea(containedArea):G6}");
        overlapAreas.Add(containedArea);
      }
      else
      {
        Log.Write(
          "vOverlaps",
          $"area pair={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
          $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} mode=unresolved");
      }
    }

    return overlapAreas;
  }

  private static PreparedFace? Prepare(
    FaceItem item,
    double coincidenceTolerance,
    double areaTolerance)
  {
    var singleFace = item.Face.DuplicateFace(duplicateMeshes: false);
    if (singleFace == null || !singleFace.IsValid)
    {
      singleFace?.Dispose();
      return null;
    }

    var bounds = singleFace.GetBoundingBox(accurate: true);
    if (!bounds.IsValid)
    {
      singleFace.Dispose();
      return null;
    }

    var isPlanar = item.Face.TryGetPlane(
      out var plane,
      coincidenceTolerance);
    var area = 0.0;
    using (var properties = AreaMassProperties.Compute(singleFace))
    {
      if (properties != null)
        area = properties.Area;
    }

    return new PreparedFace(
      item.Reference,
      item.Face,
      singleFace,
      bounds,
      isPlanar ? plane : null,
      area,
      BuildSamples(item.Face, areaTolerance));
  }

  private static List<FaceSample> BuildSamples(BrepFace face, double tolerance)
  {
    var samples = new List<FaceSample>();
    var uDomain = face.Domain(0);
    var vDomain = face.Domain(1);

    for (var uIndex = 0; uIndex < DefaultInteriorGridDivisions; uIndex++)
    {
      var u = uDomain.ParameterAt((uIndex + 0.5) / DefaultInteriorGridDivisions);
      for (var vIndex = 0; vIndex < DefaultInteriorGridDivisions; vIndex++)
      {
        var v = vDomain.ParameterAt((vIndex + 0.5) / DefaultInteriorGridDivisions);
        if (face.IsPointOnFace(u, v, tolerance) != PointFaceRelation.Interior)
          continue;

        AddSample(samples, face, u, v);
      }
    }

    foreach (var loop in face.Loops)
    {
      using var loopCurve = loop.To3dCurve();
      if (loopCurve == null || !loopCurve.IsValid)
        continue;

      var parameters = loopCurve.DivideByCount(DefaultBoundarySamples, includeEnds: false);
      if (parameters == null)
        continue;

      foreach (var parameter in parameters)
      {
        var point = loopCurve.PointAt(parameter);
        if (!face.ClosestPoint(point, out var u, out var v))
          continue;
        AddSample(samples, face, u, v);
      }
    }

    return samples;
  }

  private static void AddSample(
    ICollection<FaceSample> samples,
    BrepFace face,
    double u,
    double v)
  {
    var normal = face.NormalAt(u, v);
    if (!normal.Unitize())
      return;
    samples.Add(new FaceSample(face.PointAt(u, v), normal));
  }

  private static bool FacesShareArea(
    PreparedFace first,
    PreparedFace second,
    double coincidenceTolerance,
    double areaTolerance)
  {
    if (first.Plane.HasValue && second.Plane.HasValue &&
        PlanesCoincide(
          first.Plane.Value,
          second.Plane.Value,
          coincidenceTolerance))
    {
      var planarResult = PlanarFacesShareArea(
        first,
        second,
        coincidenceTolerance,
        areaTolerance);
      if (planarResult.HasValue)
        return planarResult.Value;
    }

    return SamplesReachInterior(
             first.Samples,
             second.Face,
             coincidenceTolerance,
             areaTolerance) ||
           SamplesReachInterior(
             second.Samples,
             first.Face,
             coincidenceTolerance,
             areaTolerance);
  }

  private static bool? PlanarFacesShareArea(
    PreparedFace first,
    PreparedFace second,
    double coincidenceTolerance,
    double areaTolerance)
  {
    var intersections = CreatePlanarOverlapAreas(
      first.Face,
      second.Face,
      coincidenceTolerance,
      areaTolerance);
    if (intersections == null)
      return null;
    try
    {
      return intersections.Count > 0 ? true : null;
    }
    finally
    {
      foreach (var intersection in intersections)
        intersection.Dispose();
    }
  }

  private static List<Brep>? CreatePlanarOverlapAreas(
    BrepFace first,
    BrepFace second,
    double coincidenceTolerance,
    double areaTolerance)
  {
    if (!first.TryGetPlane(out var firstPlane, coincidenceTolerance) ||
        !second.TryGetPlane(out var secondPlane, coincidenceTolerance) ||
        !PlanesCoincide(firstPlane, secondPlane, coincidenceTolerance))
      return null;

    using var firstFace = first.DuplicateFace(duplicateMeshes: false);
    using var secondFace = second.DuplicateFace(duplicateMeshes: false);
    if (firstFace == null || secondFace == null)
      return null;

    List<Brep>? bestAreas = null;
    var bestArea = -1.0;
    var minimumReferenceArea =
      Math.Min(FaceArea(first), FaceArea(second)) * 1e-12;
    var comparisonTolerance = Math.Max(
      areaTolerance * areaTolerance * 1e-3,
      RhinoMath.ZeroTolerance);
    for (var level = 0; level < PlanarBooleanRefinementLevels; level++)
    {
      var booleanTolerance = Math.Max(
        areaTolerance * Math.Pow(PlanarBooleanToleranceScale, level),
        RhinoMath.ZeroTolerance);
      var candidateAreas = TryCreatePlanarOverlapAreas(
        firstFace,
        secondFace,
        firstPlane,
        booleanTolerance,
        minimumReferenceArea);
      if (candidateAreas == null)
        continue;

      var candidateArea = candidateAreas.Sum(BrepArea);
      var isBetter = bestAreas == null ||
                     candidateArea > bestArea + comparisonTolerance ||
                     Math.Abs(candidateArea - bestArea) <= comparisonTolerance &&
                     candidateAreas.Count > bestAreas.Count;
      if (isBetter)
      {
        DisposeBreps(bestAreas);
        bestAreas = candidateAreas;
        bestArea = candidateArea;
      }
      else
      {
        DisposeBreps(candidateAreas);
      }
    }

    return bestAreas;
  }

  private static List<Brep>? TryCreatePlanarOverlapAreas(
    Brep firstFace,
    Brep secondFace,
    Plane plane,
    double tolerance,
    double minimumReferenceArea)
  {
    Brep[]? intersections;
    try
    {
      intersections = Brep.CreatePlanarIntersection(
        firstFace,
        secondFace,
        plane,
        tolerance);
    }
    catch
    {
      return null;
    }
    if (intersections == null)
      return null;

    var minimumArea = Math.Max(
      tolerance * tolerance * MinimumPlanarAreaToleranceFactor,
      minimumReferenceArea);
    var validAreas = new List<Brep>();
    foreach (var intersection in intersections)
    {
      using var properties = AreaMassProperties.Compute(intersection);
      if (properties != null && Math.Abs(properties.Area) > minimumArea)
        validAreas.Add(intersection);
      else
        intersection.Dispose();
    }

    return validAreas;
  }

  private static void DisposeBreps(IEnumerable<Brep>? breps)
  {
    if (breps == null)
      return;

    foreach (var brep in breps)
      brep.Dispose();
  }

  private static Brep? CreateContainedOverlapArea(
    BrepFace first,
    BrepFace second,
    double coincidenceTolerance,
    double areaTolerance)
  {
    var firstContained = SamplesLieOnFace(
      BuildSamples(first, areaTolerance),
      second,
      coincidenceTolerance,
      areaTolerance);
    var secondContained = SamplesLieOnFace(
      BuildSamples(second, areaTolerance),
      first,
      coincidenceTolerance,
      areaTolerance);
    if (!firstContained && !secondContained)
      return null;

    var containedFace = firstContained && secondContained
      ? FaceArea(first) <= FaceArea(second) ? first : second
      : firstContained ? first : second;
    return containedFace.DuplicateFace(duplicateMeshes: true);
  }

  private static bool SamplesLieOnFace(
    IReadOnlyCollection<FaceSample> samples,
    BrepFace target,
    double coincidenceTolerance,
    double areaTolerance)
  {
    if (samples.Count == 0)
      return false;

    var minimumNormalDot = Math.Cos(RhinoMath.ToRadians(DefaultNormalAngleDegrees));
    foreach (var sample in samples)
    {
      if (!target.ClosestPoint(sample.Point, out var u, out var v) ||
          target.PointAt(u, v).DistanceTo(sample.Point) > coincidenceTolerance ||
          target.IsPointOnFace(u, v, areaTolerance) == PointFaceRelation.Exterior)
        return false;

      var targetNormal = target.NormalAt(u, v);
      if (!targetNormal.Unitize() ||
          Math.Abs(targetNormal * sample.Normal) < minimumNormalDot)
        return false;
    }

    return true;
  }

  private static double FaceArea(BrepFace face)
  {
    using var properties = AreaMassProperties.Compute(face);
    return properties?.Area ?? 0.0;
  }

  private static double BrepArea(Brep brep)
  {
    using var properties = AreaMassProperties.Compute(brep);
    return properties?.Area ?? 0.0;
  }

  private static bool SamplesReachInterior(
    IReadOnlyCollection<FaceSample> samples,
    BrepFace target,
    double coincidenceTolerance,
    double areaTolerance)
  {
    var minimumNormalDot = Math.Cos(RhinoMath.ToRadians(DefaultNormalAngleDegrees));
    foreach (var sample in samples)
    {
      if (!target.ClosestPoint(sample.Point, out var u, out var v))
        continue;

      var targetPoint = target.PointAt(u, v);
      if (targetPoint.DistanceTo(sample.Point) > coincidenceTolerance)
        continue;
      if (target.IsPointOnFace(u, v, areaTolerance) != PointFaceRelation.Interior)
        continue;

      var targetNormal = target.NormalAt(u, v);
      if (!targetNormal.Unitize())
        continue;
      if (Math.Abs(targetNormal * sample.Normal) >= minimumNormalDot)
        return true;
    }

    return false;
  }

  private static bool PlanesCoincide(
    Plane first,
    Plane second,
    double tolerance)
  {
    var minimumNormalDot = Math.Cos(RhinoMath.ToRadians(DefaultNormalAngleDegrees));
    return Math.Abs(first.Normal * second.Normal) >= minimumNormalDot &&
           Math.Abs(first.DistanceTo(second.Origin)) <= tolerance &&
           Math.Abs(second.DistanceTo(first.Origin)) <= tolerance;
  }

  private static bool BoundingBoxesMeet(
    BoundingBox first,
    BoundingBox second,
    double tolerance) =>
    first.Min.X <= second.Max.X + tolerance &&
    first.Max.X + tolerance >= second.Min.X &&
    first.Min.Y <= second.Max.Y + tolerance &&
    first.Max.Y + tolerance >= second.Min.Y &&
    first.Min.Z <= second.Max.Z + tolerance &&
    first.Max.Z + tolerance >= second.Min.Z;

  private sealed record PreparedFace(
    FaceReference Reference,
    BrepFace Face,
    Brep SingleFace,
    BoundingBox Bounds,
    Plane? Plane,
    double Area,
    IReadOnlyCollection<FaceSample> Samples);

  private readonly record struct FaceSample(Point3d Point, Vector3d Normal);
}
