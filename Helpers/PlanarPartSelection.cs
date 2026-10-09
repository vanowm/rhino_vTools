using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools;

internal static class PlanarPartSelection
{
  // Shared selection and geometry defaults
  private const bool SelectGroups = true; // true picks group members together; false picks individual objects only.
  private const int BoundarySamples = 17; // Samples used to identify source boundary attributes; three or greater.
  private const double BoundaryToleranceFactor = 2.0; // Model-tolerance multiplier for coincidence and object-to-part assignment.
  private const bool DefaultRequirePlanarDetails = true; // true requires all part curves to share the outline plane for splitting; false lets nesting carry original off-plane details.
  private const string ReferenceLayerName = "Reference"; // Leaf layer excluded from loose nesting perimeter candidates when non-reference closed curves exist.

  internal sealed record Item(Guid Id, GeometryBase Geometry, ObjectAttributes Attributes, Color Color);
  internal sealed class Part(Curve outline, Plane plane, ObjectAttributes attributes, List<Item> items) : IDisposable
  {
    internal Curve Outline { get; } = outline;
    internal Plane Plane { get; } = plane;
    internal ObjectAttributes Attributes { get; } = attributes;
    internal List<Item> Items { get; } = items;
    public void Dispose()
    {
      Outline.Dispose(); Attributes.Dispose();
      foreach(var item in Items) { item.Geometry.Dispose(); item.Attributes.Dispose(); }
    }
  }
  private sealed record Region(Curve Outline, Plane Plane, List<RhinoObject> Objects)
  {
    internal BoundingBox Bounds { get; }=Outline.GetBoundingBox(Plane);
  }

  internal static List<Guid> Pick(RhinoDoc doc, string prompt, Action<GetObject> configure,
    Action<GetObject,GetResult> handleOption, out Result result)
  {
    using var getter = new GetObject();
    getter.SetCommandPrompt(prompt);
    getter.GeometryFilter = ObjectType.Curve|ObjectType.Annotation|ObjectType.Point|ObjectType.TextDot;
    getter.GroupSelect = SelectGroups;
    getter.SubObjectSelect = false;
    getter.EnablePreSelect(true,true);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;
    getter.AcceptNothing(true);
    bool returnedPreselection = false;
    while(true)
    {
      getter.ClearCommandOptions(); configure(getter);
      var input = getter.GetMultiple(0,0);
      result = getter.CommandResult();
      if(result != Result.Success) return [];
      if(input is GetResult.Option or GetResult.Number) { handleOption(getter,input); continue; }
      if(input == GetResult.Object && getter.ObjectsWerePreselected && !returnedPreselection)
      { returnedPreselection=true; getter.EnablePreSelect(false,true); continue; }
      if(input is not (GetResult.Object or GetResult.Nothing)) { result=Result.Cancel; return []; }
      var selected = doc.Objects.GetSelectedObjects(false,false).Where(obj=>Supported(obj.Geometry)).Select(obj=>obj.Id).ToList();
      if(selected.Count==0 && input==GetResult.Object)
        selected.AddRange(getter.Objects().Where(reference=>reference.Object() is { } obj && Supported(obj.Geometry)).Select(reference=>reference.ObjectId).Distinct());
      return selected;
    }
  }

  private static bool Supported(GeometryBase geometry) => geometry is Curve or AnnotationBase or Rhino.Geometry.Point or TextDot;

  internal static List<Part> Capture(RhinoDoc doc, IReadOnlyList<Guid> ids, double tolerance,bool requirePlanarDetails=DefaultRequirePlanarDetails)
  {
    var selected=ids.Select(doc.Objects.FindId).Where(obj=>obj!=null).Cast<RhinoObject>().ToList();
    var regions=new List<Region>();
    var parts=new List<Part>();
    try
    {
      // Preserve distinct group identities even when two part boundaries touch.
      var buckets=selected.GroupBy(obj=>(obj.Attributes.GetGroupList()??[])
        .Where(index=>doc.Groups[index] is {IsDeleted:false})
        .OrderBy(index=>doc.Groups.GroupObjectCount(index)).FirstOrDefault(-1));
      foreach(var bucket in buckets)
      {
        var objects=bucket.ToList();
        var curves=objects.Select(obj=>obj.Geometry).OfType<Curve>().ToArray();
        if(curves.Length==0) continue;
        var loops=BoundaryLoops(doc,objects,tolerance,requirePlanarDetails);
        var candidates=loops.Where(curve=>curve.IsClosed && curve.TryGetPlane(out _,tolerance)).ToList();
        foreach(var candidate in candidates)
        {
          candidate.TryGetPlane(out var plane,tolerance);
          using var mass=AreaMassProperties.Compute(candidate);
          if(mass==null || mass.Area<=tolerance*tolerance) continue;
          bool nested=candidates.Any(other=>!ReferenceEquals(candidate,other) && ContainsCurve(other,candidate,plane,tolerance,!requirePlanarDetails));
          if(!nested) regions.Add(new Region(candidate.DuplicateCurve(),AlignedPlane(plane,doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??plane),objects));
        }
        foreach(var loop in loops) loop.Dispose();
      }
      if(regions.Count==0)
      {
        // Small notch subgroups may not contain the whole boundary; try their parent selection together.
        var curves=selected.Select(obj=>obj.Geometry).OfType<Curve>().ToArray();
        var loops=BoundaryLoops(doc,selected,tolerance,requirePlanarDetails);
        foreach(var loop in loops)
          if(loop.IsClosed && loop.TryGetPlane(out var plane,tolerance))
            regions.Add(new Region(loop.DuplicateCurve(),AlignedPlane(plane,doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??plane),selected));
        foreach(var loop in loops) loop.Dispose();
      }
      var assignments=Enumerable.Range(0,regions.Count).Select(_=>new List<RhinoObject>()).ToArray();
      var membership=new Dictionary<RhinoObject,List<int>>();
      for(int index=0;index<regions.Count;index++)
        foreach(var obj in regions[index].Objects)
        {
          if(!membership.TryGetValue(obj,out var indices)) membership[obj]=indices=[];
          indices.Add(index);
        }
      bool Contains(int index,Point3d point)
      {
        var region=regions[index]; var delta=point-region.Plane.Origin;
        double x=delta*region.Plane.XAxis,y=delta*region.Plane.YAxis,padding=tolerance*BoundaryToleranceFactor;
        if(x<region.Bounds.Min.X-padding||x>region.Bounds.Max.X+padding||y<region.Bounds.Min.Y-padding||y>region.Bounds.Max.Y+padding) return false;
        return region.Outline.Contains(point,region.Plane,padding) is PointContainment.Inside or PointContainment.Coincident;
      }
      foreach(var obj in selected)
      {
        var point=RepresentativePoint(obj.Geometry);
        var bucket=membership.GetValueOrDefault(obj)??[];
        var choices=bucket.Where(index=>Contains(index,point)).ToArray();
        if(choices.Length==0) choices=Enumerable.Range(0,regions.Count).Where(index=>!bucket.Contains(index)&&Contains(index,point)).ToArray();
        if(choices.Length==0) choices=bucket.ToArray();
        if(choices.Length==0) choices=Enumerable.Range(0,regions.Count).ToArray();
        if(choices.Length==0) continue;
        var best=choices.Length==1?choices[0]:choices.OrderBy(index=>Distance(regions[index].Outline,point)).First();
        assignments[best].Add(obj);
      }
      for(int index=0; index<regions.Count; index++)
      {
        var region=regions[index]; var objects=assignments[index];
        if(objects.Count==0) continue;
        var boundary=objects.Where(obj=>obj.Geometry is Curve curve && Coincident(curve,region.Outline,tolerance))
          .OrderByDescending(obj=>((Curve)obj.Geometry).GetLength()).FirstOrDefault();
        if(boundary==null) continue;
        if(requirePlanarDetails&&objects.Select(obj=>obj.Geometry).OfType<Curve>().Any(curve=>!curve.IsInPlane(region.Plane,tolerance)))
          throw new InvalidOperationException("Selected part curves must share a plane.");
        if(!requirePlanarDetails)
          foreach(var detail in objects.Where(obj=>obj.Geometry is Curve curve&&!curve.IsInPlane(region.Plane,tolerance)))
            Log.Write("PartSelection","nest detail retained off plane id={0} layer={1}",detail.Id,doc.Layers[detail.Attributes.LayerIndex].FullPath);
        var items=objects.Select(obj=>new Item(obj.Id,obj.Geometry.Duplicate(),obj.Attributes.Duplicate(),obj.Attributes.DrawColor(doc))).ToList();
        parts.Add(new Part(region.Outline.DuplicateCurve(),region.Plane,boundary.Attributes.Duplicate(),items));
      }
      if(parts.Sum(part=>part.Items.Count)!=selected.Count)
        throw new InvalidOperationException("Some selected geometry could not be assigned to a closed part boundary.");
      Log.Write("PartSelection","captured parts={0} objects={1}",parts.Count,selected.Count);
      return parts;
    }
    catch(Exception ex)
    { foreach(var part in parts) part.Dispose(); Log.Write("PartSelection","capture rejected: {0}",ex.Message); return []; }
    finally { foreach(var region in regions) region.Outline.Dispose(); }
  }

  private static List<Curve> BoundaryLoops(RhinoDoc doc,IReadOnlyList<RhinoObject> objects,double tolerance,bool requirePlanarDetails)
  {
    var curves=objects.Select(obj=>obj.Geometry).OfType<Curve>().ToArray();
    if(requirePlanarDetails) return GroupBoundaryTopology.PlanarRegions(curves,tolerance,tolerance);
    var closed=objects.Where(obj=>obj.Geometry is Curve {IsClosed:true} curve&&curve.TryGetPlane(out _,tolerance)).ToArray();
    var cutting=closed.Where(obj=>!string.Equals(doc.Layers[obj.Attributes.LayerIndex].FullPath.Split("::").Last(),ReferenceLayerName,StringComparison.OrdinalIgnoreCase)).ToArray();
    if(cutting.Length>0) closed=cutting;
    return closed.Length>0?closed.Select(obj=>((Curve)obj.Geometry).DuplicateCurve()).ToList():GroupBoundaryTopology.PlanarRegions(curves,tolerance,tolerance);
  }

  private static bool ContainsCurve(Curve outer,Curve inner,Plane plane,double tolerance,bool projectInner)
  {
    if(projectInner&&outer.TryGetPlane(out var outerPlane,tolerance)) plane=outerPlane;
    using var projection=projectInner?Curve.ProjectToPlane(inner,plane):null;
    var candidate=projection??inner;
    using var outerMass=AreaMassProperties.Compute(outer);
    using var innerMass=AreaMassProperties.Compute(inner);
    return outerMass!=null && innerMass!=null && outerMass.Area>innerMass.Area+tolerance*tolerance &&
      Enumerable.Range(0,BoundarySamples).All(index=>outer.Contains(candidate.PointAtNormalizedLength((double)index/(BoundarySamples-1)),plane,tolerance)
        is PointContainment.Inside or PointContainment.Coincident);
  }

  internal static Plane AlignedPlane(Plane plane,Plane reference)
  {
    var normal=plane.Normal;
    if(normal*reference.Normal<0) normal=-normal;
    var x=reference.XAxis-(reference.XAxis*normal)*normal;
    if(!x.Unitize()) { x=reference.YAxis-(reference.YAxis*normal)*normal; x.Unitize(); }
    return new Plane(plane.Origin,x,Vector3d.CrossProduct(normal,x));
  }

  internal static bool Coincident(Curve curve,Curve boundary,double tolerance) =>
    Enumerable.Range(0,BoundarySamples).All(index=>
    { var point=curve.PointAtNormalizedLength((double)index/(BoundarySamples-1));
      return boundary.ClosestPoint(point,out var parameter) && point.DistanceTo(boundary.PointAt(parameter))<=tolerance*BoundaryToleranceFactor; });

  internal static Point3d RepresentativePoint(GeometryBase geometry) => geometry switch
  { Rhino.Geometry.Point point=>point.Location, TextDot dot=>dot.Point, Curve curve=>curve.PointAtNormalizedLength(0.5), _=>geometry.GetBoundingBox(true).Center };
  private static double Distance(Curve boundary,Point3d point) => boundary.ClosestPoint(point,out var parameter)?point.DistanceTo(boundary.PointAt(parameter)):double.PositiveInfinity;
}
