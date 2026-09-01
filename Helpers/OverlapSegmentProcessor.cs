using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

/// <summary>
/// Partitions partial-overlap source curves and returns the pieces that occupy the overlap spans.
/// </summary>
internal static class OverlapSegmentProcessor
{
  // Customizable geometric limits
  private const double MinimumPieceLength = 1e-12; // Model-unit length below which a trimmed result is ignored; greater than zero.
  private const double ParameterToleranceFraction = 1e-10; // Source-domain fraction used to merge equivalent cut parameters; positive and much less than one.
  private const double PointMappingToleranceMultiplier = 2.0; // Command-tolerance multiple allowed while mapping retained overlap points back to document curves; one or greater.

  internal sealed record Result(
    HashSet<Guid> OverlapObjectIds,
    HashSet<Guid> ResultObjectIds,
    HashSet<Guid> ProcessedSourceObjectIds,
    int SplitSourceCount);

  private readonly record struct ParameterSpan(
    double Start,
    double End,
    bool Wrapped);

  private sealed record CurvePart(
    Curve Curve,
    bool IsOverlap);

  internal static Result Materialize(
    RhinoDoc doc,
    IReadOnlyCollection<OverlapFinder.OverlapSpan> spans,
    IReadOnlyCollection<Guid> wholeOverlapIds,
    double tolerance)
  {
    var overlapObjectIds = new HashSet<Guid>();
    var resultObjectIds = new HashSet<Guid>();
    var processedSourceObjectIds = new HashSet<Guid>();
    var wholeOverlapIdSet = wholeOverlapIds as HashSet<Guid> ?? [.. wholeOverlapIds];

    foreach (var spanGroup in spans
               .Where(span => !wholeOverlapIdSet.Contains(span.ObjectId))
               .GroupBy(span => span.ObjectId))
    {
      var source = doc.Objects.FindId(spanGroup.Key);
      if (source?.Geometry is not Curve sourceCurve || !source.IsValid)
        continue;

      if (!TryCreateParts(
            sourceCurve,
            spanGroup,
            tolerance,
            out var parts,
            out var keeperIndex,
            out var sourceEntirelyOverlap,
            out var failureReason))
      {
        Log.Write(
          "OverlapSegments",
          $"partition skipped source={source.Id} reason={failureReason}");
        continue;
      }

      if (sourceEntirelyOverlap)
      {
        processedSourceObjectIds.Add(source.Id);
        resultObjectIds.Add(source.Id);
        overlapObjectIds.Add(source.Id);
        Log.Write(
          "OverlapSegments",
          $"whole source is exact overlap source={source.Id}");
        continue;
      }

      try
      {
        var addedParts = new List<(Guid Id, bool IsOverlap)>();
        var addFailed = false;
        for (var index = 0; index < parts.Count; index++)
        {
          if (index == keeperIndex)
            continue;

          var id = doc.Objects.AddCurve(
            parts[index].Curve,
            source.Attributes.Duplicate());
          if (id == Guid.Empty)
          {
            addFailed = true;
            break;
          }
          addedParts.Add((id, parts[index].IsOverlap));
        }

        if (addFailed || !doc.Objects.Replace(source.Id, parts[keeperIndex].Curve))
        {
          foreach (var added in addedParts)
            doc.Objects.Delete(added.Id, quiet: true);
          Log.Write(
            "OverlapSegments",
            $"document split failed source={source.Id} addFailed={addFailed}");
          continue;
        }

        processedSourceObjectIds.Add(source.Id);
        resultObjectIds.Add(source.Id);
        foreach (var added in addedParts)
        {
          resultObjectIds.Add(added.Id);
          if (added.IsOverlap)
            overlapObjectIds.Add(added.Id);
        }
        Log.Write(
          "OverlapSegments",
          $"partitioned source={source.Id} pieces={parts.Count} overlapPieces={addedParts.Count(part => part.IsOverlap)}");
      }
      finally
      {
        foreach (var part in parts)
          part.Curve.Dispose();
      }
    }

    return new Result(
      overlapObjectIds,
      resultObjectIds,
      processedSourceObjectIds,
      processedSourceObjectIds.Count);
  }

  private static bool TryCreateParts(
    Curve source,
    IEnumerable<OverlapFinder.OverlapSpan> sourceSpans,
    double tolerance,
    out List<CurvePart> parts,
    out int keeperIndex,
    out bool sourceEntirelyOverlap,
    out string failureReason)
  {
    parts = [];
    keeperIndex = -1;
    sourceEntirelyOverlap = false;
    failureReason = string.Empty;
    if (!source.Domain.IsValid || source.Domain.Length <= 0.0)
    {
      failureReason = "invalid source domain";
      return false;
    }

    var parameterTolerance = Math.Max(
      Math.Abs(source.Domain.Length) * ParameterToleranceFraction,
      RhinoMath.ZeroTolerance);
    var mappingTolerance = Math.Max(
      tolerance * PointMappingToleranceMultiplier,
      RhinoMath.ZeroTolerance);
    var parameterSpans = new List<ParameterSpan>();
    foreach (var span in sourceSpans)
    {
      double start;
      double interior;
      double end;
      if (span.ParametersMatchSource)
      {
        start = span.StartParameter;
        interior = span.InteriorParameter;
        end = span.EndParameter;
      }
      else if (!TryMapPoint(source, span.StartPoint, mappingTolerance, out start) ||
               !TryMapPoint(source, span.InteriorPoint, mappingTolerance, out interior) ||
               !TryMapPoint(source, span.EndPoint, mappingTolerance, out end))
      {
        continue;
      }

      start = ClampToDomain(source.Domain, start);
      interior = ClampToDomain(source.Domain, interior);
      end = ClampToDomain(source.Domain, end);
      var minimum = Math.Min(start, end);
      var maximum = Math.Max(start, end);
      var interiorOnDirectSpan =
        interior >= minimum - parameterTolerance &&
        interior <= maximum + parameterTolerance;
      parameterSpans.Add(new ParameterSpan(
        minimum,
        maximum,
        source.IsClosed && !interiorOnDirectSpan));
    }

    if (parameterSpans.Count == 0)
    {
      failureReason = "no spans mapped to source";
      return false;
    }

    var cuts = new List<double> { source.Domain.Min, source.Domain.Max };
    foreach (var span in parameterSpans)
    {
      cuts.Add(span.Start);
      cuts.Add(span.End);
    }
    cuts.Sort();
    cuts = DistinctParameters(cuts, parameterTolerance);

    var partitions = new List<(double Start, double End, bool IsOverlap)>();
    for (var index = 0; index < cuts.Count - 1; index++)
    {
      var start = cuts[index];
      var end = cuts[index + 1];
      if (end - start <= parameterTolerance)
        continue;

      var midpoint = (start + end) * 0.5;
      var isOverlap = parameterSpans.Any(span => Contains(span, midpoint, parameterTolerance));
      if (partitions.Count > 0 && partitions[^1].IsOverlap == isOverlap)
      {
        var previous = partitions[^1];
        partitions[^1] = (previous.Start, end, isOverlap);
      }
      else
      {
        partitions.Add((start, end, isOverlap));
      }
    }

    foreach (var partition in partitions)
    {
      var piece = source.Trim(new Interval(partition.Start, partition.End));
      if (piece == null)
        continue;
      if (!piece.IsValid || piece.GetLength() < MinimumPieceLength)
      {
        piece.Dispose();
        continue;
      }
      parts.Add(new CurvePart(piece, partition.IsOverlap));
    }

    var hasOverlap = parts.Any(part => part.IsOverlap);
    var hasRemainder = parts.Any(part => !part.IsOverlap);
    if (hasOverlap && !hasRemainder)
    {
      foreach (var part in parts)
        part.Curve.Dispose();
      parts = [];
      sourceEntirelyOverlap = true;
      return true;
    }

    if (parts.Count < 2 || !hasOverlap || !hasRemainder)
    {
      var partCount = parts.Count;
      foreach (var part in parts)
        part.Curve.Dispose();
      parts = [];
      failureReason = $"partition pieces={partCount} overlap={hasOverlap} remainder={hasRemainder}";
      return false;
    }

    keeperIndex = parts
      .Select((part, index) => (part, index))
      .Where(item => !item.part.IsOverlap)
      .OrderByDescending(item => item.part.Curve.GetLength())
      .Select(item => item.index)
      .First();
    return true;
  }

  private static bool TryMapPoint(
    Curve source,
    Point3d point,
    double tolerance,
    out double parameter)
  {
    parameter = 0.0;
    return point.IsValid &&
           source.ClosestPoint(point, out parameter) &&
           point.DistanceTo(source.PointAt(parameter)) <= tolerance;
  }

  private static double ClampToDomain(Interval domain, double parameter) =>
    Math.Max(domain.Min, Math.Min(domain.Max, parameter));

  private static List<double> DistinctParameters(
    IEnumerable<double> parameters,
    double tolerance)
  {
    var result = new List<double>();
    foreach (var parameter in parameters)
    {
      if (result.Count == 0 || Math.Abs(parameter - result[^1]) > tolerance)
        result.Add(parameter);
    }
    return result;
  }

  private static bool Contains(
    ParameterSpan span,
    double parameter,
    double tolerance)
  {
    if (span.Wrapped)
      return parameter <= span.Start + tolerance ||
             parameter >= span.End - tolerance;
    return parameter >= span.Start - tolerance &&
           parameter <= span.End + tolerance;
  }
}
