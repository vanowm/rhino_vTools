using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

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
      doc.ModelAbsoluteTolerance * NotchContactToleranceScale,
      RhinoMath.ZeroTolerance * NotchContactZeroToleranceScale);
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
      RhinoApp.WriteLine($"vExportDXF: {unmatched} V/U notch contact(s) did not match an export curve.");
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
          attributes.LayerIndex = UzipCommon.EnsureLayer(doc, layerName);
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
