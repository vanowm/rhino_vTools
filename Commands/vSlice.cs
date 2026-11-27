using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace vTools.Commands;

public sealed class vSlice : vToolsCommand
{
  // Defaults
  [CommandDefault("vSlice", "vTools.Commands.vSlice.DefaultCopy",
    "True keeps the originals and adds sliced copies; false replaces sliced objects with their pieces.", OptionKeys = "copy")]
  private const bool DefaultCopy = false; // true adds sliced copies; false replaces only the objects that split.
  private static readonly bool ConfiguredDefaultCopy = CommandDefaults.Get(
    "vSlice", "vTools.Commands.vSlice.DefaultCopy", DefaultCopy); // Initial Copy until a value is saved.
  private const ObjectType SliceTypes = ObjectType.Curve | ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh; // Geometry types with native split operations.

  private const string CopyKey = "copy";
  public override string EnglishName => "vSlice";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    bool copy = ToolsOptionStore.Read(EnglishName,
      section => ToolsOptionStore.TryGetBool(section, CopyKey, out bool saved) ? saved : ConfiguredDefaultCopy);
    try
    {
      var result = PlaneToolInput.SelectObjects("Select objects to slice", SliceTypes, ref copy, out var ids);
      if (result != Result.Success) return result;
      while (true)
      {
        result = PlaneToolInput.PickPlane(doc, "Pick a planar face that cuts the selection", ref copy, out var plane,
          face => CanSlice(doc, ids, face));
        if (result != Result.Success) return result;
        result = Slice(doc, ids, plane, copy);
        if (result != Result.Nothing) return result;
      }
    }
    finally
    {
      _ = ToolsOptionStore.Update(EnglishName, section => section[CopyKey] = copy);
    }
  }

  internal static bool CanSlice(RhinoDoc doc, IReadOnlyList<Guid> ids, Plane plane)
  {
    foreach (Guid id in ids)
    {
      if (doc.Objects.FindId(id)?.Geometry is not { } geometry) continue;
      var pieces = SplitGeometry(geometry, plane, doc.ModelAbsoluteTolerance);
      foreach (var piece in pieces) piece.Dispose();
      if (pieces.Length > 0) return true;
    }
    return false;
  }

  internal static Result Slice(RhinoDoc doc, IReadOnlyList<Guid> ids, Plane plane, bool copy)
  {
    var removed = new List<uint>();
    var added = new List<Guid>();
    var changes = new List<(RhinoObject Source, GeometryBase[] Pieces)>();
    try
    {
      double tolerance = doc.ModelAbsoluteTolerance;
      foreach (Guid id in ids)
      {
        if (doc.Objects.FindId(id) is not { } obj)
          throw new InvalidOperationException("A selected object is no longer available.");
        var pieces = SplitGeometry(obj.Geometry, plane, tolerance);
        if (pieces.Length > 0) changes.Add((obj, pieces));
      }
      if (changes.Count == 0)
      {
        RhinoApp.WriteLine("vSlice: That plane did not divide any selected object. Pick another plane, or press Esc to cancel.");
        return Result.Nothing;
      }
      var sourceIds = new List<Guid>();
      foreach (var (source, pieces) in changes)
      {
        using var attributes = source.Attributes.Duplicate();
        foreach (var piece in pieces)
        {
          Guid id = doc.Objects.Add(piece, attributes);
          if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not add a sliced piece.");
          sourceIds.Add(source.Id);
          added.Add(id);
        }
      }
      if (copy) OrientCommon.RecreateGroupsForCopiedObjects(doc, sourceIds, added);
      else
      {
        foreach (var (source, _) in changes)
        {
          uint serial = source.RuntimeSerialNumber;
          if (!doc.Objects.Delete(source, true)) throw new InvalidOperationException("Rhino could not remove a sliced original.");
          removed.Add(serial);
        }
      }
      doc.Views.Redraw();
      return Result.Success;
    }
    catch (Exception ex)
    {
      foreach (Guid id in added) doc.Objects.Delete(id, true);
      foreach (uint serial in removed) doc.Objects.Undelete(serial);
      Log.Write(nameof(vSlice), ex.ToString());
      RhinoApp.WriteLine("vSlice: Could not complete the slice. The originals were kept.");
      doc.Views.Redraw();
      return Result.Failure;
    }
    finally
    {
      foreach (var (_, pieces) in changes)
        foreach (var piece in pieces) piece.Dispose();
    }
  }

  internal static GeometryBase[] SplitGeometry(GeometryBase geometry, Plane plane, double tolerance)
  {
    GeometryBase[]? pieces = null;
    switch (geometry)
    {
      case Curve curve when !curve.IsInPlane(plane, tolerance):
        pieces = SplitCurve(curve, plane, tolerance);
        break;
      case Mesh mesh:
        pieces = mesh.Split(plane);
        break;
      case Extrusion extrusion:
        using (var brep = extrusion.ToBrep(splitKinkyFaces: true)) pieces = SplitBrep(brep, plane, tolerance);
        break;
      case Brep brep:
        pieces = SplitBrep(brep, plane, tolerance);
        break;
      case Surface surface:
        using (var brep = surface.ToBrep()) pieces = SplitBrep(brep, plane, tolerance);
        break;
    }
    if (pieces == null) return [];
    if (pieces.Length > 1 && pieces.All(piece => piece.IsValid)) return pieces;
    foreach (var piece in pieces) piece.Dispose();
    return [];
  }

  private static Curve[]? SplitCurve(Curve curve, Plane plane, double tolerance)
  {
    using var hits = Intersection.CurvePlane(curve, plane, tolerance);
    if (hits == null) return null;
    var domain = curve.Domain;
    double seamEpsilon = domain.Length * RhinoMath.ZeroTolerance;
    var parameters = hits.Where(hit => hit.IsPoint).Select(hit => hit.ParameterA)
      .Select(t => curve.IsClosed && t >= domain.T1 - seamEpsilon ? domain.T0 : t)
      .Where(t => t >= domain.T0 && t < domain.T1).Distinct().OrderBy(t => t).ToArray();
    if (!curve.IsClosed)
    {
      var interior = parameters.Where(t => t > domain.T0).ToArray();
      return interior.Length > 0 ? curve.Split(interior) : null;
    }
    if (parameters.Length < 2) return null;

    // Trim closed curves cyclically, including cuts that land on the domain seam.
    var pieces = new List<Curve>(parameters.Length);
    for (int i = 0; i < parameters.Length; i++)
    {
      double end = parameters[(i + 1) % parameters.Length];
      if (i == parameters.Length - 1 && end == domain.T0) end = domain.T1;
      var piece = curve.Trim(parameters[i], end);
      if (piece != null) { pieces.Add(piece); continue; }
      foreach (var made in pieces) made.Dispose();
      return null;
    }
    return pieces.ToArray();
  }

  private static Brep[]? SplitBrep(Brep brep, Plane plane, double tolerance)
  {
    var bounds = brep.GetBoundingBox(plane);
    if (!bounds.IsValid || bounds.Min.Z >= -tolerance || bounds.Max.Z <= tolerance) return null;
    // Bound the infinite plane by the target, not by the face used to pick it.
    double padding = Math.Max(bounds.Diagonal.Length, tolerance);
    using var cutter = new PlaneSurface(plane,
      new Interval(bounds.Min.X - padding, bounds.Max.X + padding),
      new Interval(bounds.Min.Y - padding, bounds.Max.Y + padding));
    using var tool = cutter.ToBrep();
    var pieces = brep.Split(tool, tolerance);
    if (!brep.IsSolid) return pieces;
    if (FinishSolidSlice(pieces, plane, tolerance) is { } split) return split;

    using var halfSpace = new Box(plane, cutter.Domain(0), cutter.Domain(1),
      new Interval(bounds.Min.Z - padding, 0)).ToBrep();
    if (FinishSolidSlice(Brep.CreateBooleanSplit(brep, halfSpace, tolerance), plane, tolerance) is { } booleanSplit)
      return booleanSplit;
    // Opposed plane trims can recover a cut along an existing coplanar patch.
    var reverse = new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
    return FinishSolidSlice((brep.Trim(plane, tolerance) ?? []).Concat(brep.Trim(reverse, tolerance) ?? []).ToArray(), plane, tolerance);
  }

  private static Brep[]? FinishSolidSlice(Brep[]? pieces, Plane plane, double tolerance)
  {
    if (pieces == null) return null;
    var solids = new List<Brep>(pieces.Length);
    bool below = false, above = false, failed = false;
    foreach (var piece in pieces)
    {
      var bounds = piece.GetBoundingBox(plane);
      bool low = bounds.IsValid && bounds.Min.Z < -tolerance, high = bounds.IsValid && bounds.Max.Z > tolerance;
      // A face already on the cutting plane can be returned as a detached sheet.
      if (!low && !high) { piece.Dispose(); continue; }
      var solid = piece.IsSolid ? piece : piece.CapPlanarHoles(tolerance);
      if (!ReferenceEquals(solid, piece)) piece.Dispose();
      if (low == high || solid is not { IsSolid: true, IsValid: true })
      {
        solid?.Dispose();
        failed = true;
        continue;
      }
      solids.Add(solid);
      below |= low;
      above |= high;
    }
    if (!failed && below && above) return solids.ToArray();
    foreach (var solid in solids) solid.Dispose();
    return null;
  }
}
