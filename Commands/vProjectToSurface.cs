using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Projects selected curves and points to a selected surface or polysurface.
/// </summary>
public sealed class vProjectToSurface : Command
{
  private const string CommandName = "vProjectToSurface";

  public override string EnglishName => "vProjectToSurface";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var sources = PickSources(doc);
    if (sources == null)
      return Result.Cancel;

    if (sources.Count == 0)
    {
      RhinoApp.WriteLine($"{CommandName}: select at least one curve or point.");
      return Result.Nothing;
    }

    if (!PickTargetBrep(doc, out var targetBrep) || targetBrep == null)
      return Result.Cancel;

    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    var outputIds = new List<Guid>();
    var skipped = 0;

    var undoRecord = doc.BeginUndoRecord(CommandName);
    try
    {
      foreach (var source in sources)
      {
        switch (source.Geometry)
        {
          case Curve curve:
            var curveIds = AddProjectedCurves(doc, curve, source.Attributes, targetBrep, tolerance);
            if (curveIds.Count == 0)
              skipped++;
            outputIds.AddRange(curveIds);
            break;

          case Point point:
            var pointId = AddProjectedPoint(doc, point.Location, source.Attributes, targetBrep, tolerance);
            if (pointId == Guid.Empty)
              skipped++;
            else
              outputIds.Add(pointId);
            break;
        }
      }
    }
    finally
    {
      if (undoRecord > 0)
        doc.EndUndoRecord(undoRecord);
    }

    if (outputIds.Count == 0)
    {
      RhinoApp.WriteLine($"{CommandName}: no selected geometry touched the target surface.");
      return Result.Nothing;
    }

    doc.Objects.UnselectAll();
    foreach (var id in outputIds)
      doc.Objects.FindId(id)?.Select(true);

    doc.Views.Redraw();

    RhinoApp.WriteLine(
      $"{CommandName}: projected {outputIds.Count} object{(outputIds.Count == 1 ? "" : "s")}" +
      (skipped > 0 ? $" | skipped {skipped}" : ""));

    return Result.Success;
  }

  private static List<SourceItem>? PickSources(RhinoDoc doc)
  {
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select curves and points to project");
    go.GeometryFilter = ObjectType.Curve | ObjectType.Point;
    go.SubObjectSelect = false;
    go.GroupSelect = true;
    go.EnablePreSelect(true, true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;

    var result = go.GetMultiple(1, 0);
    if (result != GetResult.Object || go.CommandResult() != Result.Success)
      return null;

    var sources = new List<SourceItem>();
    var seen = new HashSet<Guid>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      var rhObj = objRef?.Object();
      if (rhObj == null || !seen.Add(rhObj.Id))
        continue;

      GeometryBase? duplicate = null;
      if (rhObj.Geometry is Curve curve)
        duplicate = curve.DuplicateCurve();
      else if (rhObj.Geometry is Point point)
        duplicate = new Point(point.Location);

      if (duplicate == null)
        continue;

      var attributes = rhObj.Attributes?.Duplicate() ?? new ObjectAttributes();
      attributes.RemoveFromAllGroups();
      sources.Add(new SourceItem(duplicate, attributes));
    }

    return sources;
  }

  private static bool PickTargetBrep(RhinoDoc doc, out Brep? targetBrep)
  {
    targetBrep = null;

    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select target surface or polysurface");
    go.GeometryFilter = ObjectType.Surface | ObjectType.PolysrfFilter;
    go.SubObjectSelect = false;
    go.EnablePreSelect(true, true);

    var result = go.Get();
    if (result != GetResult.Object || go.ObjectCount < 1 || go.CommandResult() != Result.Success)
      return false;

    var objRef = go.Object(0);
    if (objRef == null)
      return false;

    var brep = objRef.Brep();
    if (brep != null)
    {
      targetBrep = brep.DuplicateBrep();
      return targetBrep != null;
    }

    var surface = objRef.Surface();
    if (surface != null)
    {
      targetBrep = surface.ToBrep();
      return targetBrep != null;
    }

    return false;
  }

  private static List<Guid> AddProjectedCurves(
    RhinoDoc doc,
    Curve source,
    ObjectAttributes attributes,
    Brep targetBrep,
    double tolerance)
  {
    var projected = PullCurveToBrep(source, targetBrep, tolerance)
      .Where(curve => IsUsableCurve(curve, tolerance))
      .ToList();

    if (projected.Count == 0)
      return new List<Guid>();

    var joined = Curve.JoinCurves(projected, tolerance);
    var curvesToAdd = (joined != null && joined.Length > 0 ? joined : projected.ToArray())
      .Where(curve => IsUsableCurve(curve, tolerance))
      .ToList();

    var ids = new List<Guid>();
    foreach (var curve in curvesToAdd)
    {
      var id = doc.Objects.AddCurve(curve, attributes.Duplicate());
      if (id != Guid.Empty)
        ids.Add(id);
    }

    return ids;
  }

  private static IEnumerable<Curve> PullCurveToBrep(Curve source, Brep targetBrep, double tolerance)
  {
    foreach (var face in targetBrep.Faces)
    {
      Curve[]? pulled = null;
      try
      {
        pulled = source.PullToBrepFace(face, tolerance);
      }
      catch
      {
        try { pulled = Curve.PullToBrepFace(source, face, tolerance); }
        catch { pulled = null; }
      }

      if (pulled == null)
        continue;

      foreach (var curve in pulled)
      {
        if (curve != null)
          yield return curve;
      }
    }
  }

  private static Guid AddProjectedPoint(
    RhinoDoc doc,
    Point3d point,
    ObjectAttributes attributes,
    Brep targetBrep,
    double tolerance)
  {
    return TryClosestPointOnBrepFace(targetBrep, point, tolerance, out var projected)
      ? doc.Objects.AddPoint(projected, attributes.Duplicate())
      : Guid.Empty;
  }

  private static bool TryClosestPointOnBrepFace(
    Brep brep,
    Point3d point,
    double tolerance,
    out Point3d projected)
  {
    projected = Point3d.Unset;
    if (brep.Faces.Count == 0)
      return false;

    var bestDistanceSquared = double.MaxValue;
    foreach (var face in brep.Faces)
    {
      try
      {
        if (!face.ClosestPoint(point, out var u, out var v))
          continue;

        var relation = face.IsPointOnFace(u, v);
        if (relation == PointFaceRelation.Exterior)
          continue;

        var candidate = face.PointAt(u, v);
        if (!candidate.IsValid)
          continue;

        var distanceSquared = point.DistanceToSquared(candidate);
        if (distanceSquared > bestDistanceSquared + tolerance * tolerance)
          continue;

        bestDistanceSquared = distanceSquared;
        projected = candidate;
      }
      catch
      {
      }
    }

    return projected.IsValid;
  }

  private static bool IsUsableCurve(Curve? curve, double tolerance)
  {
    if (curve == null || !curve.IsValid)
      return false;

    try
    {
      return curve.GetLength() > tolerance;
    }
    catch
    {
      return true;
    }
  }

  private sealed record SourceItem(GeometryBase Geometry, ObjectAttributes Attributes);
}
