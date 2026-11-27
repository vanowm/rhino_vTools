using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vGrain : vToolsCommand
{
  // Defaults and placement limits
  private const double MaximumLengthInches = 2; // Absolute guide length limit in inches.
  [CommandDefault("vGrain", "vTools.Commands.vGrain.DefaultLengthInches", "Grain guide length in inches, greater than zero and no more than 2; shortens to fit.")]
  private const double DefaultLengthInches = MaximumLengthInches; // Preferred length, capped at two inches.
  [CommandDefault("vGrain", "vTools.Commands.vGrain.DefaultLabelClearanceInches", "Minimum space from label bounds, in nonnegative inches.")]
  private const double DefaultLabelClearanceInches = .05; // Space around label bounds in inches; zero keeps only model tolerance.
  private const int CenterSearchSlices = 33; // Number of transverse search positions across the part when finding the initial centered guide.
  private const double EdgeClearanceFactor = 2; // Minimum perimeter clearance in multiples of document tolerance.
  private static readonly Color GrainPreviewColor = Color.Plum; // Preview color when the GRAIN layer does not exist.
  private const string CutLayerPrefix = "CUT"; // Cut-layer names identify enclosed holes; PLOT seams are not holes.

  public override string EnglishName => "vGrain";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    bool added = false;
    do
    {
      using var part = PickPart(doc);
      if (part == null) return added ? Result.Success : Result.Cancel;
      if (AddGuide(doc, part.Outline, part.Plane, part.Members, part.Groups, part.BoundaryLayer, out bool cancelled) != Guid.Empty)
        added = true;
      else if (cancelled) return added ? Result.Success : Result.Cancel;
    } while (mode == RunMode.Interactive);
    return added ? Result.Success : Result.Nothing;
  }

  private sealed record PartTarget(Curve Outline, Plane Plane, Guid[] Members, int[] Groups, int BoundaryLayer) : IDisposable
  {
    public void Dispose() => Outline.Dispose();
  }

  private static PartTarget? PickPart(RhinoDoc doc)
  {
    while (true)
    {
      using var pick = new GetObject();
      pick.SetCommandPrompt("Click a part edge or label; Enter to finish");
      pick.GeometryFilter = ObjectType.Curve | ObjectType.Annotation | ObjectType.TextDot;
      pick.GroupSelect = false;
      pick.EnablePreSelect(false, true);
      pick.EnableUnselectObjectsOnExit(true);
      pick.AcceptNothing(true);
      pick.SetCustomGeometryFilter((obj, _, _) => !NestTableMetadata.IsDecoration(doc, obj));
      if (pick.Get() != GetResult.Object) return null;
      using var reference = pick.Object(0);
      if (reference.Object() is { } obj && CaptureTarget(doc, obj) is {} target) return target;
      RhinoApp.WriteLine("vGrain: could not find a closed planar perimeter for that part.");
    }
  }

  private static PartTarget? CaptureTarget(RhinoDoc doc, RhinoObject picked)
  {
    double tolerance = doc.ModelAbsoluteTolerance;
    bool Grain(RhinoObject obj) => obj.Geometry is Curve {IsClosed:false} && NestWorkflow.IsGrainMarker(doc,obj.Attributes);
    var directGroups=(picked.Attributes.GetGroupList()??[]).Where(index=>doc.Groups[index] is {IsDeleted:false}).ToArray();
    // A guide previously attached to a single curve can find the part through that curve's memberships.
    var candidateGroups=Grain(picked)?directGroups.Concat(directGroups.SelectMany(index=>doc.Groups.GroupMembers(index))
      .Where(obj=>!obj.IsDeleted&&!Grain(obj)).SelectMany(obj=>obj.Attributes.GetGroupList()??[])):directGroups;
    foreach (int group in candidateGroups.Distinct().Where(index => doc.Groups[index] is { IsDeleted: false })
      .OrderByDescending(doc.Groups.GroupObjectCount))
    {
      var members = doc.Groups.GroupMembers(group).Where(obj => !obj.IsDeleted).ToArray();
      if (members.Any(obj => NestTableMetadata.IsDecoration(doc,obj))) continue;
      var parts = PlanarPartSelection.CaptureGroup(doc, members.Select(obj => obj.Id).ToArray(), tolerance);
      try
      {
        if(parts.Count!=1)continue; // A multi-part assembly is not the selected part's group.
        var part=parts[0];
        var partIds=part.Items.Select(item=>item.Id).ToHashSet();
        foreach(int child in members.SelectMany(obj=>obj.Attributes.GetGroupList()??[]).Distinct()
          .Where(index=>index!=group&&doc.Groups[index] is {IsDeleted:false}))
        {
          var related=doc.Groups.GroupMembers(child).Where(obj=>!obj.IsDeleted).ToArray();
          if(related.Any(obj=>!partIds.Contains(obj.Id)&&!Grain(obj)))continue;
          foreach(var guide in related.Where(Grain))
            if(part.Outline.Contains(PlanarPartSelection.RepresentativePoint(guide.Geometry),part.Plane,tolerance) is PointContainment.Inside)
              partIds.Add(guide.Id);
        }
        if(!partIds.Contains(picked.Id))continue;
        var common=members.Select(obj=>obj.Attributes.GetGroupList()??[]).Aggregate((a,b)=>a.Intersect(b).ToArray());
        return new(part.Outline.DuplicateCurve(), part.Plane, partIds.ToArray(), PartMembership(doc,group,common), part.Attributes.LayerIndex);
      }
      finally { foreach(var part in parts) part.Dispose(); }
    }
    var chain = PlanarPartSelection.ConnectedPerimeter(doc, picked, tolerance);
    using var pickedOutline = JoinPerimeter(chain, tolerance);
    var enclosing = pickedOutline == null ? FindEnclosingPerimeter(doc, picked, tolerance) : (null, -1);
    using var enclosingOutline = enclosing.Item1;
    var outline = pickedOutline ?? enclosingOutline;
    if (outline == null || !outline.TryGetPlane(out var plane,tolerance)) return null;
    var bounds = outline.GetBoundingBox(plane); bounds.Inflate(tolerance);
    var ids = chain.Select(obj => obj.Id).ToHashSet();
    foreach (var item in doc.Objects.GetObjectList(ObjectType.Curve | ObjectType.Annotation | ObjectType.TextDot))
    {
      if (!item.Visible || item.IsDeleted || (item.Attributes.GetGroupList()?.Length ?? 0) > 0 ||
        item.Geometry is Curve candidate && !candidate.IsClosed && !NestWorkflow.IsGrainMarker(doc,item.Attributes)) continue;
      var point = PlanarPartSelection.RepresentativePoint(item.Geometry);
      if (plane.RemapToPlaneSpace(point,out var local) && Math.Abs(local.Z)<=tolerance &&
        bounds.Contains(local) && outline.Contains(point,plane,tolerance)==PointContainment.Inside) ids.Add(item.Id);
    }
    return new(outline.DuplicateCurve(),plane,ids.ToArray(),[],pickedOutline!=null?picked.Attributes.LayerIndex:enclosing.Item2);
  }

  internal static int[] PartMembership(RhinoDoc doc,int partGroup,IEnumerable<int> commonGroups) =>
    partGroup<0?[]:new[]{partGroup}.Concat(commonGroups.Where(index=>index!=partGroup&&
      doc.Groups[index] is {IsDeleted:false}&&(doc.Groups.GroupMembers(index)??[]).Any(obj=>NestTableMetadata.IsTableBorder(doc,obj)))).ToArray();

  private static Curve? JoinPerimeter(IReadOnlyList<RhinoObject> chain,double tolerance)
  {
    if (chain.Count==0) return null;
    if (chain.Count==1 && chain[0].Geometry is Curve {IsClosed:true} closed) return closed.DuplicateCurve();
    var loops=Curve.JoinCurves(chain.Select(obj=>(Curve)obj.Geometry),tolerance);
    if(loops.Length==1&&loops[0].IsClosed)return loops[0];
    foreach(var loop in loops)loop.Dispose();
    return null;
  }

  private static (Curve? Outline,int Layer) FindEnclosingPerimeter(RhinoDoc doc,RhinoObject picked,double tolerance)
  {
    var point=picked.Geometry is AnnotationBase text?text.Plane.Origin:PlanarPartSelection.RepresentativePoint(picked.Geometry);
    if(!point.IsValid)return (null,-1);
    var candidates=doc.Objects.GetObjectList(ObjectType.Curve).Where(obj=>obj.Visible&&!obj.IsDeleted&&
      (obj.Attributes.GetGroupList()?.Length??0)==0&&!NestTableMetadata.IsDecoration(doc,obj)&&
      !NestWorkflow.IsGrainMarker(doc,obj.Attributes)).ToArray();
    bool Cutting(RhinoObject obj)=>doc.Layers[obj.Attributes.LayerIndex].Name.StartsWith(CutLayerPrefix,StringComparison.OrdinalIgnoreCase);
    Curve? best=null;int layer=-1;double area=double.PositiveInfinity;
    void Consider(Curve curve,int index)
    {
      var box=curve.GetBoundingBox(false);box.Inflate(tolerance);
      if(!box.Contains(point)||!curve.TryGetPlane(out var plane,tolerance)||Math.Abs(plane.DistanceTo(point))>tolerance||
        curve.Contains(point,plane,tolerance)!=PointContainment.Inside)return;
      using var properties=AreaMassProperties.Compute(curve);
      if(properties==null||properties.Area>=area)return;
      best?.Dispose();best=curve.DuplicateCurve();area=properties.Area;layer=index;
    }
    // Prefer the cutting perimeter to interior seams, independently of whether it is joined.
    foreach(var set in candidates.GroupBy(Cutting).OrderByDescending(group=>group.Key))
    {
      foreach(var obj in set.Where(obj=>obj.Geometry is Curve {IsClosed:true}))Consider((Curve)obj.Geometry,obj.Attributes.LayerIndex);
      var open=set.Where(obj=>obj.Geometry is Curve {IsClosed:false}).ToArray();
      var visited=new HashSet<Guid>();
      foreach(var obj in open)
      {
        if(!visited.Add(obj.Id))continue;
        var chain=PlanarPartSelection.TracePerimeter(obj,open,tolerance);
        visited.UnionWith(chain.Select(edge=>edge.Id));
        using var outline=JoinPerimeter(chain,tolerance);
        if(outline!=null)Consider(outline,obj.Attributes.LayerIndex);
      }
      if(best!=null)break;
    }
    return (best,layer);
  }

  internal static Guid AddGuide(RhinoDoc doc, Curve outline, Plane plane, IReadOnlyList<Guid> members, IReadOnlyList<int> groups,
    int boundaryLayer, out bool cancelled)
  {
    cancelled = false;
    var objects = members.Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
    var existing = objects.Where(obj => obj.Geometry is Curve { IsClosed: false } && NestWorkflow.IsGrainMarker(doc,obj.Attributes)).ToArray();
    if (existing.Length > 1)
    {
      RhinoApp.WriteLine("vGrain: this part has multiple grain guides; remove the extras first.");
      return Guid.Empty;
    }
    if (!TryDirection(plane, out var direction)) { cancelled = true; return Guid.Empty; }
    double inch = RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);
    double length = CommandDefaults.Get("vGrain", "vTools.Commands.vGrain.DefaultLengthInches", DefaultLengthInches);
    if (!double.IsFinite(length) || length <= 0) length = DefaultLengthInches;
    double clearance = CommandDefaults.Get("vGrain", "vTools.Commands.vGrain.DefaultLabelClearanceInches", DefaultLabelClearanceInches);
    if (!double.IsFinite(clearance) || clearance < 0) clearance = DefaultLabelClearanceInches;
    double tol = doc.ModelAbsoluteTolerance;
    var holes = objects.Where(obj => obj.Geometry is Curve { IsClosed: true } loop &&
      (obj.Attributes.LayerIndex == boundaryLayer || doc.Layers[obj.Attributes.LayerIndex].Name.StartsWith(CutLayerPrefix,StringComparison.OrdinalIgnoreCase)) &&
      loop.IsInPlane(plane,tol) && !PlanarPartSelection.Coincident(loop,outline,tol) &&
      outline.Contains(loop.PointAtStart,plane,tol) == PointContainment.Inside).Select(obj => (Curve)obj.Geometry).ToArray();
    var labels = objects.Select(obj=>obj.Geometry).Where(geometry=>geometry is AnnotationBase or TextDot).ToArray();
    var fit = new GuidePlacement(outline,plane,direction,holes,labels,Math.Min(length,MaximumLengthInches)*inch,clearance*inch,tol);
    if (fit.Centered() is not {} line)
    {
      RhinoApp.WriteLine("vGrain: no grain line fits inside the part clear of its labels and holes.");
      return Guid.Empty;
    }
    int layer = doc.Layers.FindByFullPath(NestWorkflow.GrainIdentifier,-1);
    var color = layer>=0 ? doc.Layers[layer].Color : GrainPreviewColor;
    using (var position = new GetPoint())
    {
      position.SetCommandPrompt("Move grain guide inside the part; click or press Enter to place");
      position.Constrain(plane,false);position.AcceptNothing(true);
      position.DynamicDraw += (_,e) =>
      {
        if (fit.At(e.CurrentPoint) is {} next) line=next;
        e.Display.DrawLine(line,color);
      };
      var result=position.Get();
      if (result is not (GetResult.Point or GetResult.Nothing)) { cancelled=true;return Guid.Empty; }
      if (result==GetResult.Point && fit.At(position.Point()) is {} next)line=next;
    }
    var id=WriteGuide(doc,line,layer,existing.FirstOrDefault(),groups);
    if (id==Guid.Empty) RhinoApp.WriteLine("vGrain: could not write the guide. Check that the guide and layer are unlocked.");
    doc.Views.Redraw();
    return id;
  }

  private static Guid WriteGuide(RhinoDoc doc,Line line,int layer,RhinoObject? existing,IReadOnlyList<int> groups)
  {
    if(existing!=null)
    {
      if(!existing.IsSelectable(true,false,false,false)||existing.Geometry is not Curve curve)return Guid.Empty;
      var id=existing.Id;
      using var attributes=existing.Attributes.Duplicate();
      bool regroup=!(attributes.GetGroupList()??[]).ToHashSet().SetEquals(groups);
      using var before=regroup?curve.DuplicateCurve():null;
      if(!doc.Objects.Replace(id,line))return Guid.Empty;
      if(!regroup)return id;
      attributes.RemoveFromAllGroups();
      foreach(int group in groups)attributes.AddToGroup(group);
      if(doc.Objects.ModifyAttributes(id,attributes,true))return id;
      doc.Objects.Replace(id,before!);
      return Guid.Empty;
    }
    if(layer<0)layer=UzipCommon.EnsureLayer(doc,NestWorkflow.GrainIdentifier,GrainPreviewColor);
    if(layer<0)return Guid.Empty;
    using var added=new ObjectAttributes {LayerIndex=layer,Name=NestWorkflow.ConfiguredGrainIdentifier};
    foreach(int group in groups)added.AddToGroup(group);
    return doc.Objects.AddLine(line,added);
  }

  private static bool TryDirection(Plane plane, out Vector3d direction)
  {
    while (true)
    {
      using var pick = new GetObject();
      pick.SetCommandPrompt("Pick a curve or segment for grain direction, or choose TwoPoints");
      pick.GeometryFilter=ObjectType.Curve;
      pick.GroupSelect=false;pick.SubObjectSelect=true;
      pick.EnablePreSelect(false,true);pick.EnableUnselectObjectsOnExit(true);
      int points=pick.AddOption("TwoPoints");
      direction=Vector3d.Unset;
      var result=pick.Get();
      if (result==GetResult.Object)
      {
        using var reference=pick.Object(0);
        if (reference.Curve() is {} curve)
        {
          double parameter=curve.ClosestPoint(reference.SelectionPoint(),out var t)?t:curve.Domain.Mid;
          direction=curve.TangentAt(parameter);
        }
      }
      else if (result==GetResult.Option&&pick.OptionIndex()==points)
      {
        using var first=new GetPoint();first.SetCommandPrompt("First grain direction point");
        if (first.Get()!=GetResult.Point) return false;
        using var second=new GetPoint();second.SetCommandPrompt("Second grain direction point");second.DrawLineFromPoint(first.Point(),true);
        if (second.Get()!=GetResult.Point) return false;
        direction=second.Point()-first.Point();
      }
      else return false;
      direction-=plane.Normal*(direction*plane.Normal);
      if (direction.Unitize()) return true;
      RhinoApp.WriteLine("vGrain: pick a direction that is not perpendicular to the part.");
    }
  }

  internal sealed class GuidePlacement
  {
    private readonly Curve _outline;
    private readonly Curve[] _holes;
    private readonly BoundingBox[] _labels;
    private readonly Plane _frame;
    private readonly BoundingBox _bounds;
    private readonly double _length,_tol;

    internal GuidePlacement(Curve outline,Plane plane,Vector3d direction,Curve[] holes,GeometryBase[] labels,double length,double clearance,double tolerance)
    {
      _outline=outline;_holes=holes;_length=length;_tol=tolerance;
      using var area=AreaMassProperties.Compute(outline);
      _frame=new Plane(plane.ClosestPoint(area?.Centroid??outline.GetBoundingBox(true).Center),direction,Vector3d.CrossProduct(plane.Normal,direction));
      _bounds=outline.GetBoundingBox(_frame);
      _labels=labels.Select(geometry=>
      {
        var box=geometry is TextEntity text&&text.Plane.IsValid
          ?new BoundingBox(AnnotationTextTransform.TextBounds(text).GetCorners().Select(corner=>text.Plane.PointAt(corner.X,corner.Y,corner.Z)).Select(point=>Local(point)))
          :geometry.GetBoundingBox(_frame);
        if(box.IsValid)box.Inflate(clearance+_tol);
        return box;
      }).Where(box=>box.IsValid).ToArray();
    }

    private Point3d Local(Point3d point)=>_frame.RemapToPlaneSpace(point,out var local)?local:Point3d.Unset;
    internal Line? Centered()
    {
      if(!_bounds.IsValid)return null;
      var ys=new List<double>{0};
      foreach(var label in _labels){ys.Add(label.Min.Y-_tol);ys.Add(label.Max.Y+_tol);}
      for(int i=1;i<CenterSearchSlices;i++)ys.Add(_bounds.Min.Y+(_bounds.Max.Y-_bounds.Min.Y)*i/CenterSearchSlices);
      return ys.Distinct().Select(y=>Find(y,0)).OfType<Line>()
        .OrderBy(line=>line.PointAt(.5).DistanceTo(_frame.Origin)+(_length-line.Length)).Select(line=>(Line?)line).FirstOrDefault();
    }

    internal Line? At(Point3d point)
    {
      if(!point.IsValid||_outline.Contains(_frame.ClosestPoint(point),_frame,_tol) is not PointContainment.Inside)return null;
      var local=Local(point);
      return Find(local.Y,local.X);
    }

    private Line? Find(double y,double targetX)
    {
      if(!_bounds.IsValid||y<=_bounds.Min.Y||y>=_bounds.Max.Y)return null;
      double margin=EdgeClearanceFactor*_tol;
      double left=_bounds.Min.X-margin,right=_bounds.Max.X+margin;
      var axis=new Line(_frame.PointAt(left,y),_frame.PointAt(right,y));
      var events=new List<double>{left,right};
      void Cuts(Curve curve)
      {
        using var hits=Intersection.CurveLine(curve,axis,_tol,_tol);
        if(hits==null)return;
        foreach(var hit in hits){events.Add(Local(hit.PointA).X);if(hit.IsOverlap)events.Add(Local(hit.PointA2).X);}
      }
      Cuts(_outline);foreach(var hole in _holes)Cuts(hole);
      var blocked=_labels.Where(box=>y>=box.Min.Y&&y<=box.Max.Y).ToArray();
      foreach(var box in blocked){events.Add(box.Min.X);events.Add(box.Max.X);}
      var sorted=events.Where(x=>double.IsFinite(x)&&x>=left&&x<=right).Distinct().Order().ToArray();
      Line? best=null;double bestScore=double.PositiveInfinity;
      for(int i=1;i<sorted.Length;i++)
      {
        double a=sorted[i-1]+margin,b=sorted[i]-margin,length=Math.Min(b-a,_length);
        if(length<=_tol)continue;
        double middle=(a+b)/2;
        if(blocked.Any(box=>middle>=box.Min.X&&middle<=box.Max.X))continue;
        var point=_frame.PointAt(middle,y);
        if(_outline.Contains(point,_frame,_tol)!=PointContainment.Inside||
          _holes.Any(hole=>hole.Contains(point,_frame,_tol)!=PointContainment.Outside))continue;
        double x=Math.Clamp(targetX,a+length/2,Math.Max(a+length/2,b-length/2));
        var proposed=new Line(_frame.PointAt(x-length/2,y),_frame.PointAt(x+length/2,y));
        if(_outline.Contains(proposed.From,_frame,_tol)!=PointContainment.Inside||_outline.Contains(proposed.To,_frame,_tol)!=PointContainment.Inside)continue;
        double score=Math.Abs(x-targetX)+(_length-length);
        if(score<bestScore){best=proposed;bestScore=score;}
      }
      return best;
    }
  }
}
