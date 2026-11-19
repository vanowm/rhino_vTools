using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using vTools.Commands;

namespace vTools;

internal static class MatchingNotches
{
  // Shared vNotches/seam defaults and serialized output schema
  internal const double DefaultLength = 0.18; // Positive notch depth in model units.
  internal const double DefaultWidth = 0.18; // Positive notch width in model units.
  internal const string DefaultType = "I"; // I, T, V, U or \/ (Open Vee).
  internal const double DefaultEndOffset = 2.0; // Nonnegative start/end clearance in model units.
  internal const string CurrentLayer = "*[Current]*"; // Sentinel resolving to the document's current layer.
  private const bool DefaultOffsetEnabled = true; // true uses the saved end clearance; false uses zero clearance.
  private const double DefaultLabelOffsetInches = 0.1; // Nonnegative tangent clearance in inches, converted to model units.
  private const double DefaultLabelOffsetY = 0; // Signed perpendicular label displacement in model units.
  private const bool DefaultTowardStart = false; // true prefers labels before their notch; false prefers after it.
  [CommandDefault("vNotches","vTools.MatchingNotches.ReferenceSamples","Arc-length samples used to verify curve-relative ranges during creation and legacy upgrades; at least three. Restart Rhino after changing.")]
  private const int ReferenceSamples = 17; // At least three evenly spaced arc-length samples verify a stored range without changing source geometry.
  private static readonly int ConfiguredReferenceSamples=Math.Max(3,CommandDefaults.Get("vNotches","vTools.MatchingNotches.ReferenceSamples",ReferenceSamples)); // Effective verification sample count; never fewer than three.
  internal const string Prefix = "notch."; // Shared metadata namespace, compatible with vNotches editing/removal.
  internal const string Version = "2"; // Curve-relative schema; version 1 remains readable and upgradeable after verification.
  internal const string LegacyVersion = "1"; // Absolute-coordinate schema used until a physical curve reference can be verified.
  internal const string CurveReferenceKey = Prefix+"curve_reference"; // JSON: physical curve GUIDs, signed arc-length ranges, distance/percentage station; no world-coordinate authority.
  internal const string MatchSetKey = Prefix+"match_set"; // GUID linking a physical position across mating parts.
  internal const string MatchMemberKey = Prefix+"match_member"; // Own object GUID; prevents copied metadata joining the original match.
  internal const string MatchDirectionKey = Prefix+"match_direction"; // 1 follows the stored curve range; -1 moves toward its opposite end within a matched set.
  internal const string SourceMatchKey = Prefix+"source_match"; // Original matched-set GUID, retained across nested copies for editing.
  internal const string SourceMemberKey = Prefix+"source_member"; // Original component GUID, retained across nested copies for editing.
  internal const string SourceCurveIdsKey = Prefix+"source_curves"; // Ordered JSON array of all physical curves in a linked source row.
  private const string Section = "vNotches"; // Existing global/document settings owner.
  [CommandDefault("vNotches","vTools.MatchingNotches.RepairSeamOffsetInches","Preferred distance from a physical notch anchor to its seam source during repair, in inches; zero or greater.")]
  internal const double RepairSeamOffsetInches = 0.5; // Inches from the cut-edge anchor to a nominal seam; on-edge sources are the fallback.
  [CommandDefault("vNotches","vTools.MatchingNotches.RepairAnchorToleranceInches","Maximum physical-anchor discrepancy during legacy notch repair, in inches; zero or greater. Accounts for curved V-notch mouths, not source-curve gaps.")]
  private const double RepairAnchorToleranceInches = 0.02; // Inches of anchor uncertainty for legacy V mouths and reversed slit endpoints; source geometry is never changed.
  [CommandDefault("vNotches","vTools.MatchingNotches.RepairTangentDotLimit","Maximum absolute tangent/stem dot product when recovering a notch source; 0 through 1, zero requires perpendicularity.")]
  private const double RepairTangentDotLimit = 0.1; // Dimensionless tangent/stem cosine threshold for a plausible physical source.
  private static readonly Settings Defaults = new(
    Factory("DefaultNotchLength",DefaultLength),Factory("DefaultNotchWidth",DefaultWidth),
    Factory("DefaultNotchType",DefaultType),Factory("DefaultNotchLayer",CurrentLayer),
    Factory("DefaultMultipleStartOffset",DefaultEndOffset),Factory("DefaultMultipleEndOffset",DefaultEndOffset),
    Factory("DefaultMultipleStartOffsetEnabled",DefaultOffsetEnabled),Factory("DefaultMultipleEndOffsetEnabled",DefaultOffsetEnabled),
    Factory("DefaultLabelOffset",double.NaN),Factory("DefaultLabelOffsetY",DefaultLabelOffsetY),
    Factory("DefaultLabelSideFlip",DefaultTowardStart)); // Same vSettings defaults as vNotches; NaN label gap requests inch conversion.
  internal sealed record Settings(double Length,double Width,string Type,string Layer,double Start,double End,
    bool StartEnabled,bool EndEnabled,double LabelOffset,double LabelOffsetY,bool TowardStart);
  private static T Factory<T>(string key,T fallback)=>CommandDefaults.Get(Section,"vTools.Commands.vNotches."+key,fallback);
  private static Settings Overlay(Settings style,JsonObject? settings)
  {
    double Number(string key,double fallback)=>ToolsOptionStore.TryGetDouble(settings,key,out var value)&&double.IsFinite(value)?value:fallback;
    bool Flag(string key,bool fallback)=>ToolsOptionStore.TryGetBool(settings,key,out var value)?value:fallback;
    return style with
    {
      Length=Math.Max(0,Number("notch_length",style.Length)),Width=Math.Max(0,Number("notch_width",style.Width)),
      Type=MatchingLabels.ReadText(settings,"notch_type",style.Type),Layer=MatchingLabels.ReadText(settings,"notch_layer",style.Layer),
      Start=Math.Max(0,Number("multiple_start_offset",style.Start)),End=Math.Max(0,Number("multiple_end_offset",style.End)),
      StartEnabled=Flag("multiple_start_offset_enabled",style.StartEnabled),EndEnabled=Flag("multiple_end_offset_enabled",style.EndEnabled),
      LabelOffset=Number("label_offset",style.LabelOffset),LabelOffsetY=Number("label_offset_y",style.LabelOffsetY),
      TowardStart=Flag("label_toward_start",style.TowardStart)
    };
  }
  internal static Settings Read(RhinoDoc doc)
  {
    var style=ToolsOptionStore.Read(Section,settings=>Overlay(Defaults,settings));
    try
    {
      string text=doc.Strings.GetValue("vTools",Section);
      if(!string.IsNullOrWhiteSpace(text))style=Overlay(style,JsonNode.Parse(text) as JsonObject);
    }
    catch(JsonException ex){Log.Write("MatchingNotches","Document notch settings could not be read: {0}",ex.Message);}
    if(!double.IsFinite(style.LabelOffset))style=style with
    {LabelOffset=Factory("DefaultLabelOffIn",DefaultLabelOffsetInches)*RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem)};
    return style;
  }
  internal static Point3d PointAtLength(Curve curve,double length)
  {
    if(length<=0)return curve.PointAtStart;
    if(length>=curve.GetLength())return curve.PointAtEnd;
    return curve.LengthParameter(length,out double parameter)?curve.PointAt(parameter):curve.PointAtEnd;
  }
  internal static List<Curve> StraightGeometry(Point3d point,Vector3d tangent,Vector3d direction,
    double length,double offset,string type,double width)
  {
    type=type.Trim().ToUpperInvariant();if(type=="OPENV")type="\\/";
    if(!tangent.Unitize()||!direction.Unitize())return [];
    var start=point+direction*Math.Max(0,offset);
    var tip=offset>0?start-direction*length:point+direction*length;
    if(type is "I" or "T")
    {
      var end=offset>0?start-direction*Math.Min(length,offset):tip;
      var geometry=new List<Curve>{new LineCurve(start,end)};
      if(type=="T")geometry.Add(new LineCurve(end-tangent*width*.5,end+tangent*width*.5));
      return geometry;
    }
    var left=start-tangent*width*.5;var right=start+tangent*width*.5;
    if(type=="V")return [new PolylineCurve(new[]{left,tip,right})];
    var leftTip=tip-tangent*width*.25;var rightTip=tip+tangent*width*.25;
    if(type=="U")return [new PolylineCurve(new[]{left,leftTip,rightTip,right})];
    if(type=="\\/")return [new LineCurve(left,leftTip),new LineCurve(rightTip,right)];
    return [new LineCurve(point,point+direction*length)];
  }
  internal static void SetMember(ObjectAttributes attributes,Guid id,Guid match,string role,Guid notchId=default)
  {
    attributes.ObjectId=id;
    attributes.SetUserString(MatchSetKey,match.ToString("N"));attributes.SetUserString(MatchMemberKey,id.ToString("N"));
    attributes.SetUserString(Prefix+"object_role",role);
    attributes.SetUserString(Prefix+(role=="label"?"label_id":"notch_id"),id.ToString());
    if(role=="label")attributes.SetUserString(Prefix+"notch_id",notchId==Guid.Empty?string.Empty:notchId.ToString());
  }
  internal static bool HasMetadata(ObjectAttributes attributes)=>
    attributes.GetUserString(Prefix+"version")!=null||attributes.GetUserString(Prefix+"curve_point")!=null;

  internal sealed record CurveCopy(Dictionary<Guid,Guid> Map,Transform Transform,Guid Owner);
  internal static List<CurveCopy> NestedCurveCopies(RhinoDoc doc,IEnumerable<Guid> selected)
  {
    var requested=selected.Distinct().ToHashSet();var result=new List<CurveCopy>();
    if(requested.Count==0)return result;
    double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
    var nested=doc.Objects.GetObjectList(ObjectType.Curve).Where(obj=>!obj.IsDeleted&&
      !NestTableMetadata.HasDecorationRole(obj.Attributes)&&obj.Attributes.GetUserString("vNest.instance")!=null).ToArray();
    Vector3d Normal(Curve curve)
    {
      var normal=curve.TryGetPlane(out var plane,tolerance)?plane.Normal:Vector3d.ZAxis;
      double dominant=new[]{normal.X,normal.Y,normal.Z}.OrderByDescending(Math.Abs).First();
      return dominant<0?-normal:normal;
    }
    bool Same(Curve source,Curve target,Transform transform)
    {
      if(Math.Abs(source.GetLength()-target.GetLength())>tolerance)return false;
      for(int i=0;i<ConfiguredReferenceSamples;i++)
      {
        double fraction=(double)i/(ConfiguredReferenceSamples-1);
        if((transform*source.PointAtNormalizedLength(fraction)).DistanceTo(target.PointAtNormalizedLength(fraction))>tolerance)return false;
      }
      return true;
    }
    foreach(var group in nested.GroupBy(obj=>(obj.Attributes.GetUserString("vNest.session"),obj.Attributes.GetUserString("vNest.instance"))))
    {
      if(string.IsNullOrEmpty(group.Key.Item1))continue;
      Guid[] inventory;
      try{inventory=JsonSerializer.Deserialize<Guid[]>(group.First().Attributes.GetUserString("vNest.sourceObjects")??"[]")??[];}
      catch(JsonException ex){Log.Write("MatchingNotches","Invalid copy inventory: {0}",ex.Message);continue;}
      var wanted=requested.Where(inventory.Contains).ToArray();if(wanted.Length==0)continue;
      var sources=inventory.Select(doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false}&&obj.Geometry is Curve&&!HasMetadata(obj.Attributes)).ToArray();
      var targets=group.Where(obj=>!HasMetadata(obj.Attributes)).ToArray();
      var anchor=sources.OrderByDescending(obj=>((Curve)obj.Geometry).GetLength()).FirstOrDefault();if(anchor==null)continue;
      var from=(Curve)anchor.Geometry;
      var normal=Normal(from);
      var frame=new Plane(from.PointAtStart,from.TangentAtStart,Vector3d.CrossProduct(normal,from.TangentAtStart));
      var solutions=new List<CurveCopy>();
      foreach(var target in targets.Where(obj=>Math.Abs(((Curve)obj.Geometry).GetLength()-from.GetLength())<=tolerance))
      {
        var to=(Curve)target.Geometry;
        var targetNormal=Normal(to);
        foreach(int sign in new[]{1,-1})
        {
          var destination=new Plane(to.PointAtStart,to.TangentAtStart,sign*Vector3d.CrossProduct(targetNormal,to.TangentAtStart));
          var transform=Transform.PlaneToPlane(frame,destination);
          if(sign<0)transform=Transform.Mirror(to.PointAtStart,targetNormal)*transform;
          var map=new Dictionary<Guid,Guid>();bool valid=true;
          foreach(var source in sources)
          {
            var matches=targets.Where(obj=>Same((Curve)source.Geometry,(Curve)obj.Geometry,transform)).ToArray();
            if(matches.Length!=1){valid=false;break;}map[source.Id]=matches[0].Id;
          }
          if(valid&&wanted.All(map.ContainsKey)&&map.Values.Distinct().Count()==map.Count&&
              !solutions.Any(item=>wanted.All(id=>item.Map[id]==map[id])&&
                (item.Transform*(frame.Origin+frame.YAxis)).DistanceTo(transform*(frame.Origin+frame.YAxis))<=tolerance))
            solutions.Add(new(map,transform,target.Id));
        }
      }
      if(solutions.Count==1)result.Add(solutions[0]);
    }
    return result;
  }

  // Recover a source from the notch's physical anchor, never from an old world-space station.
  internal static bool Reassociate(RhinoDoc doc,RhinoObject notch,IEnumerable<RhinoObject> part,out ObjectAttributes? attributes,bool explicitSource=false,Curve? explicitCurve=null,IReadOnlyList<(Guid Id,Curve Segment)>? explicitPath=null)
  {
    attributes=null;if(notch.Geometry is not Curve geometry)return false;
    string type=notch.Attributes.GetUserString(Prefix+"notch_type")??DefaultType;
    if(type is not ("I" or "T" or "V" or "U"))return false;
    double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
    double offset=Math.Max(0,CommandDefaults.Get(Section,"vTools.MatchingNotches.RepairSeamOffsetInches",RepairSeamOffsetInches))*RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem);
    var members=part.Where(obj=>obj.Id!=notch.Id&&obj.Geometry is Curve&&!HasMetadata(obj.Attributes)&&!NestTableMetadata.HasDecorationRole(obj.Attributes)).ToArray();
    if(explicitCurve!=null&&(!explicitSource||members.Length!=1||!explicitCurve.IsValid))return false;
    Curve SourceCurve(RhinoObject obj)=>explicitCurve??(Curve)obj.Geometry;
    var anchor=type is "V" or "U"?(geometry.PointAtStart+geometry.PointAtEnd)*.5:geometry.PointAtStart;
    if(type is "I" or "T")
    {
      double CutDistance(Point3d point)=>members.Where(obj=>(doc.Layers[obj.Attributes.LayerIndex]?.Name??string.Empty).StartsWith("CUT",StringComparison.OrdinalIgnoreCase))
        .Select(obj=>{var curve=SourceCurve(obj);return curve.ClosestPoint(point,out double parameter)?curve.PointAt(parameter).DistanceTo(point):double.MaxValue;}).DefaultIfEmpty(double.MaxValue).Min();
      double AnchorScore(Point3d point)
      {
        double cut=CutDistance(point);if(cut<double.MaxValue)return cut;
        return explicitSource?members.Select(obj=>{var curve=SourceCurve(obj);curve.ClosestPoint(point,out double t);double d=curve.PointAt(t).DistanceTo(point);return Math.Min(d,Math.Abs(d-offset));}).DefaultIfEmpty(double.MaxValue).Min():cut;
      }
      if(AnchorScore(geometry.PointAtEnd)<AnchorScore(anchor))anchor=geometry.PointAtEnd;
    }
    var stem=type is "V" or "U"?geometry.PointAtNormalizedLength(.5)-anchor:
      (anchor==geometry.PointAtStart?geometry.PointAtEnd:geometry.PointAtStart)-anchor;
    if(!stem.Unitize())return false;
    double anchorTolerance=Math.Max(tolerance,CommandDefaults.Get(Section,"vTools.MatchingNotches.RepairAnchorToleranceInches",RepairAnchorToleranceInches)*RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem));
    double tangentLimit=Math.Clamp(CommandDefaults.Get(Section,"vTools.MatchingNotches.RepairTangentDotLimit",RepairTangentDotLimit),0,1);
    var candidates=new List<(RhinoObject Object,double Parameter,double Distance)>();
    foreach(var obj in members)
    {
      var curve=SourceCurve(obj);
      string layer=doc.Layers[obj.Attributes.LayerIndex]?.Name??string.Empty;
      if(!explicitSource&&(layer.Contains("grain",StringComparison.OrdinalIgnoreCase)||layer.Contains("reference",StringComparison.OrdinalIgnoreCase)))continue;
      if(!curve.ClosestPoint(anchor,out double parameter))continue;
      double distance=curve.PointAt(parameter).DistanceTo(anchor);
      if(!explicitSource&&Math.Abs(Vector3d.Multiply(curve.TangentAt(parameter),stem))>tangentLimit)continue;
      if(explicitSource||distance<=anchorTolerance||!layer.StartsWith("CUT",StringComparison.OrdinalIgnoreCase)&&Math.Abs(distance-offset)<=anchorTolerance)candidates.Add((obj,parameter,distance));
    }
    var preferred=candidates.Where(item=>offset>tolerance&&Math.Abs(item.Distance-offset)<=anchorTolerance).ToArray();
    if(preferred.Length==0)preferred=candidates.Where(item=>item.Distance<=anchorTolerance).ToArray();
    if(explicitSource)preferred=candidates.ToArray();
    if(preferred.Length!=1)return false;
    var chosen=preferred[0];var source=SourceCurve(chosen.Object);
    double station=source.GetLength(new Interval(source.Domain.T0,chosen.Parameter));
    var point=source.PointAt(chosen.Parameter);var tangent=source.TangentAt(chosen.Parameter);
    bool onEdge=chosen.Distance<=anchorTolerance;
    var direction=onEdge?stem:anchor-point;
    var side=Vector3d.Multiply(Vector3d.CrossProduct(Vector3d.ZAxis,tangent),direction)>=0?"Left":"Right";
    attributes=notch.Attributes.Duplicate();
    if(explicitSource&&Guid.TryParse(attributes.GetUserString(MatchMemberKey),out var member)&&member!=notch.Id)
      foreach(string key in new[]{MatchSetKey,MatchMemberKey,SourceMatchKey,SourceMemberKey})attributes.SetUserString(key,null);
    string Number(double number)=>number.ToString("R",CultureInfo.InvariantCulture);
    string Point(Point3d value)=>string.Join(",",new[]{value.X,value.Y,value.Z}.Select(Number));
    attributes.SetUserString(Prefix+"curve_id",chosen.Object.Id.ToString());
    attributes.SetUserString(Prefix+"curve_key","obj:"+chosen.Object.Id);
    attributes.SetUserString(Prefix+"curve_start",Point(source.PointAtStart));attributes.SetUserString(Prefix+"curve_end",Point(source.PointAtEnd));
    attributes.SetUserString(Prefix+"curve_mid",Point(source.PointAtNormalizedLength(.5)));attributes.SetUserString(Prefix+"curve_point",Point(point));
    attributes.SetUserString(Prefix+"tangent_hint",Point(new Point3d(tangent)));
    attributes.SetUserString(Prefix+"length_from_start",Number(station));attributes.SetUserString(Prefix+"percent",Number(station/source.GetLength()));
    attributes.SetUserString(Prefix+"notch_offset",Number(onEdge?0:explicitSource?chosen.Distance:offset));attributes.SetUserString(Prefix+"curve_side",side);
    var sourceIds=explicitPath?.Select(item=>item.Id).Distinct().ToArray()??[chosen.Object.Id];
    attributes.SetUserString(SourceCurveIdsKey,JsonSerializer.Serialize(sourceIds));
    attributes.SetUserString(Prefix+"source_reversed",JsonSerializer.Serialize(sourceIds.Select(_=>false)));
    if(!BindReference(doc,attributes,source,station,explicitPath)){attributes.Dispose();attributes=null;return false;}
    if(explicitCurve!=null)
    {
      var keys=ReferenceRangeKeys(attributes);
      attributes.SetUserString(Prefix+"curve_key",keys.Length==1?"section:"+keys[0]:"path:["+string.Join(",",keys)+"]");
    }
    return true;
  }
  internal static void CopySourceReference(ObjectAttributes source,ObjectAttributes target)
  {
    foreach(string key in new[]{"version","curve_id","curve_key","curve_start","curve_end","curve_mid","curve_point","curve_reference","tangent_hint","length_from_start","percent","notch_offset","curve_side","source_curves","source_reversed"})
      target.SetUserString(Prefix+key,source.GetUserString(Prefix+key));
  }

  internal static Dictionary<Guid,ObjectAttributes> PhysicalSourceRepairs(RhinoDoc doc)
  {
    var result=new Dictionary<Guid,ObjectAttributes>();
    var inventory=doc.Objects.GetObjectList(new ObjectEnumeratorSettings{NormalObjects=true,HiddenObjects=true,LockedObjects=true}).Where(obj=>!obj.IsDeleted).ToArray();
    var tables=NestTableMetadata.TableGroups(doc);var groups=new Dictionary<int,RhinoObject[]>();
    RhinoObject[] Part(RhinoObject obj)
    {
      return (obj.Attributes.GetGroupList()??[]).Where(id=>!tables.Contains(id)).Select(id=>
      {
        if(!groups.TryGetValue(id,out var members))groups[id]=members=doc.Groups.GroupMembers(id)??[];
        return members;
      }).Where(members=>members.Any(member=>member.Geometry is Curve&&!HasMetadata(member.Attributes)&&!NestTableMetadata.HasDecorationRole(member.Attributes)))
        .OrderBy(members=>members.Length).FirstOrDefault()??[];
    }
    foreach(var obj in inventory.Where(obj=>HasMetadata(obj.Attributes)&&obj.Geometry is Curve&&obj.Attributes.GetUserString(CurveReferenceKey)==null&&
      obj.Attributes.GetUserString(MatchSetKey)==null&&obj.IsSelectable(true,false,false,false)))
      if(Reassociate(doc,obj,Part(obj),out var attributes))result[obj.Id]=attributes!;
    foreach(var label in inventory.Where(obj=>HasMetadata(obj.Attributes)&&obj.Geometry is TextEntity or TextDot&&
      obj.Attributes.GetUserString(CurveReferenceKey)==null&&obj.Attributes.GetUserString(MatchSetKey)==null&&obj.IsSelectable(true,false,false,false)))
    {
      string? oldId=label.Attributes.GetUserString(Prefix+"notch_id");
      var matches=Part(label).Where(obj=>result.ContainsKey(obj.Id)&&
        (Guid.TryParse(oldId,out var id)&&(obj.Id==id||Guid.TryParse(obj.Attributes.GetUserString(Prefix+"notch_id"),out var local)&&local==id)||
          label.Attributes.GetUserString(Prefix+"curve_id")==obj.Attributes.GetUserString(Prefix+"curve_id")&&
          label.Attributes.GetUserString(Prefix+"length_from_start")==obj.Attributes.GetUserString(Prefix+"length_from_start")))
        .GroupBy(obj=>result[obj.Id].GetUserString(CurveReferenceKey)).ToArray();
      if(matches.Length!=1)continue;
      var notch=matches[0].First();var repaired=result[notch.Id];var attributes=label.Attributes.Duplicate();
      CopySourceReference(repaired,attributes);
      attributes.SetUserString(Prefix+"notch_id",notch.Id.ToString());attributes.SetUserString(Prefix+"label_id",label.Id.ToString());
      result[label.Id]=attributes;
    }
    return result;
  }

  internal sealed record CurveRange(Guid Id,double Start,double Span);
  internal sealed record CurveLocation(CurveRange[] Ranges,double Distance,double Fraction,string Mode);
  internal static string? StationKey(ObjectAttributes attributes)=>attributes.GetUserString(CurveReferenceKey)??attributes.GetUserString(Prefix+"curve_point");
  internal static Guid[] ReferenceIds(ObjectAttributes attributes)=>ReadLocation(attributes,out var location)?location!.Ranges.Select(range=>range.Id).ToArray():[];
  internal static string? ReferencePathKey(ObjectAttributes attributes)=>ReadLocation(attributes,out var location)?JsonSerializer.Serialize(location!.Ranges):null;
  internal static string[] ReferenceRangeKeys(ObjectAttributes attributes)=>ReadLocation(attributes,out var location)?location!.Ranges.Select(range=>JsonSerializer.Serialize(range)).ToArray():[];
  internal static bool TryReferenceSections(RhinoDoc doc,ObjectAttributes attributes,out List<(Guid Id,string Key,Curve Geometry,bool Whole,bool Reversed)> sections)
  {
    sections=[];
    if(!ReadLocation(attributes,out var location)||!TryResolve(doc,attributes,out var reference,out _))return false;
    reference!.Dispose();double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
    foreach(var range in location!.Ranges)
    {
      if(doc.Objects.FindId(range.Id)?.Geometry is not Curve source||RangeCurve(source,range,tolerance) is not {} curve)
      {foreach(var section in sections)section.Geometry.Dispose();sections.Clear();return false;}
      bool whole=Math.Abs(range.Start)<=RhinoMath.ZeroTolerance&&Math.Abs(range.Span-1)<=RhinoMath.ZeroTolerance;
      sections.Add((range.Id,JsonSerializer.Serialize(range),curve,whole,range.Span<0));
    }
    return true;
  }
  private static bool ReadLocation(ObjectAttributes attributes,out CurveLocation? location)
  {
    location=null;
    try{location=JsonSerializer.Deserialize<CurveLocation>(attributes.GetUserString(CurveReferenceKey)??"null");}
    catch(JsonException ex){Log.Write("MatchingNotches","Invalid curve-relative reference: {0}",ex.Message);}
    return location is {Ranges.Length:>0}&&double.IsFinite(location.Distance)&&location.Distance>=0&&
      double.IsFinite(location.Fraction)&&location.Fraction>=0&&location.Fraction<=1&&
      location.Mode is "percent" or "distance" or "length"&&
      location.Ranges.All(range=>range!=null&&range.Id!=Guid.Empty&&double.IsFinite(range.Start)&&range.Start>=0&&range.Start<=1&&
        double.IsFinite(range.Span)&&range.Span!=0&&Math.Abs(range.Span)<=1);
  }
  private static Curve? RangeCurve(Curve source,CurveRange range,double tolerance)
  {
    if(!source.IsValid)return null;double total=source.GetLength();if(!double.IsFinite(total)||total<=tolerance)return null;
    double start=range.Start,end=start+range.Span;
    if(!source.IsClosed&&(end<0||end>1))return null;
    bool reverse=range.Span<0;
    double low=reverse?end:start,high=reverse?start:end;
    while(low<0){low++;high++;}while(low>=1){low--;high--;}
    Curve? Piece(double from,double to)
    {
      if(from==0&&to==1)return source.DuplicateCurve();
      if(!source.NormalizedLengthParameter(from,out double a)||!source.NormalizedLengthParameter(to,out double b))return null;
      return source.Trim(a,b);
    }
    Curve? result;
    if(high<=1)result=Piece(low,high);
    else
    {
      using var first=Piece(low,1);using var last=Piece(0,high-1);
      if(first==null||last==null)return null;
      var joined=Curve.JoinCurves(new[]{first,last},tolerance,true);
      if(joined.Length!=1){foreach(var curve in joined)curve.Dispose();return null;}result=joined[0];
    }
    if(reverse)result?.Reverse();return result;
  }
  internal static bool TryResolve(RhinoDoc doc,ObjectAttributes attributes,out Curve? curve,out double station)
  {
    curve=null;station=0;if(!ReadLocation(attributes,out var location))return false;
    if(Guid.TryParse(attributes.GetUserString(MatchMemberKey),out var member)&&attributes.ObjectId!=Guid.Empty&&member!=attributes.ObjectId)return false;
    var pieces=new List<Curve>();double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
    try
    {
      foreach(var range in location!.Ranges)
      {
        if(doc.Objects.FindId(range.Id) is not {IsDeleted:false,Geometry:Curve source} owner)return false;
        if(attributes.GetUserString("vNest.instance") is {} instance&&
          (owner.Attributes.GetUserString("vNest.instance")!=instance||owner.Attributes.GetUserString("vNest.session")!=attributes.GetUserString("vNest.session")))return false;
        if(RangeCurve(source,range,tolerance) is not {} piece)return false;
        pieces.Add(piece);
      }
      if(pieces.Count==1)curve=pieces[0].DuplicateCurve();
      else
      {
        var joined=Curve.JoinCurves(pieces,tolerance,true);
        if(joined.Length!=1){foreach(var item in joined)item.Dispose();return false;}curve=joined[0];
      }
      double total=curve.GetLength();station=location.Mode=="percent"?location.Fraction*total:location.Distance;
      if(station>total+tolerance){curve.Dispose();curve=null;return false;}station=Math.Clamp(station,0,total);return true;
    }
    finally{foreach(var piece in pieces)piece.Dispose();}
  }
  internal static bool TryPoint(RhinoDoc doc,ObjectAttributes attributes,string key,out Point3d point)
  {
    if(attributes.GetUserString(CurveReferenceKey)==null)return TryPoint(attributes.GetUserString(Prefix+key),out point);
    point=Point3d.Unset;if(!TryResolve(doc,attributes,out var curve,out double station))return false;
    using(curve)
    {
      double total=curve!.GetLength();
      if(key=="tangent_hint")
      {curve.LengthParameter(station,out double parameter);point=new Point3d(curve.TangentAt(parameter));}
      else point=key switch{"curve_start"=>curve.PointAtStart,"curve_end"=>curve.PointAtEnd,"curve_mid"=>PointAtLength(curve,total*.5),_=>PointAtLength(curve,station)};
      return point.IsValid;
    }
  }
  private static bool SamePath(Curve a,Curve b,double tolerance)
  {
    if(!a.IsValid||!b.IsValid||Math.Abs(a.GetLength()-b.GetLength())>tolerance)return false;
    for(int i=0;i<ConfiguredReferenceSamples;i++)
    {double f=(double)i/(ConfiguredReferenceSamples-1);if(a.PointAtNormalizedLength(f).DistanceTo(b.PointAtNormalizedLength(f))>tolerance)return false;}
    return true;
  }
  private static CurveRange? BindRange(Guid id,Curve source,Curve segment,double tolerance)
  {
    if(!source.IsValid||!segment.IsValid)return null;
    double total=source.GetLength(),length=segment.GetLength();if(!double.IsFinite(total)||!double.IsFinite(length)||total<=tolerance||length>total+tolerance)return null;
    if(!source.ClosestPoint(segment.PointAtStart,out double parameter)||source.PointAt(parameter).DistanceTo(segment.PointAtStart)>tolerance)return null;
    double start=parameter<=source.Domain.T0?0:parameter>=source.Domain.T1?1:source.GetLength(new Interval(source.Domain.T0,parameter))/total;
    foreach(int sign in new[]{1,-1})
    {
      double span=sign*Math.Min(1,length/total);
      if(!source.IsClosed&&(start+span < -tolerance/total||start+span>1+tolerance/total))continue;
      var range=new CurveRange(id,start,source.IsClosed?span:Math.Clamp(start+span,0,1)-start);
      using var candidate=RangeCurve(source,range,tolerance);
      if(candidate!=null&&SamePath(candidate,segment,tolerance))return range;
    }
    return null;
  }
  internal static bool BindReference(RhinoDoc doc,ObjectAttributes attributes,Curve reference,double station,IReadOnlyList<(Guid Id,Curve Segment)>? path=null)
  {
    if(!reference.IsValid)return false;
    double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance),total=reference.GetLength();
    if(!double.IsFinite(total)||!double.IsFinite(station)||station < -tolerance||station>total+tolerance||total<=tolerance)return false;
    var ranges=new List<CurveRange>();
    if(path==null)
    {
      if(!Guid.TryParse(attributes.GetUserString(Prefix+"curve_id"),out var id)||doc.Objects.FindId(id)?.Geometry is not Curve source||BindRange(id,source,reference,tolerance) is not {} range)return false;
      ranges.Add(range);
    }
    else
    {
      foreach(var item in path)
      {
        bool included=Enumerable.Range(0,ConfiguredReferenceSamples).All(index=>
        {var point=item.Segment.PointAtNormalizedLength((double)index/(ConfiguredReferenceSamples-1));return reference.ClosestPoint(point,out double t)&&reference.PointAt(t).DistanceTo(point)<=tolerance;});
        if(!included)continue;
        if(doc.Objects.FindId(item.Id)?.Geometry is not Curve source||BindRange(item.Id,source,item.Segment,tolerance) is not {} range)return false;
        ranges.Add(range);
      }
      if(ranges.Count==0)return false;
    }
    string mode=attributes.GetUserString(Prefix+"mode")??"distance";
    using var probe=attributes.Duplicate();probe.SetUserString(CurveReferenceKey,JsonSerializer.Serialize(new CurveLocation(ranges.ToArray(),Math.Clamp(station,0,total),Math.Clamp(station/total,0,1),mode)));
    if(!TryResolve(doc,probe,out var resolved,out _))return false;
    using(resolved)if(!SamePath(reference,resolved!,tolerance))return false;
    attributes.SetUserString(CurveReferenceKey,probe.GetUserString(CurveReferenceKey));attributes.SetUserString(Prefix+"version",Version);return true;
  }
  internal static void UpdateStation(ObjectAttributes attributes,double station,double total)
  {
    if(!ReadLocation(attributes,out var location))return;
    attributes.SetUserString(CurveReferenceKey,JsonSerializer.Serialize(location! with {Distance=station,Fraction=total>0?station/total:0,Mode=attributes.GetUserString(Prefix+"mode")??location.Mode}));
  }
  internal static void RemapReference(ObjectAttributes attributes,IReadOnlyDictionary<Guid,Guid> ids)
  {
    if(!ReadLocation(attributes,out var location))return;
    var mapped=location! with {Ranges=location!.Ranges.Select(range=>range with {Id=ids.GetValueOrDefault(range.Id)}).ToArray()};
    attributes.SetUserString(CurveReferenceKey,JsonSerializer.Serialize(mapped));
    if(mapped.Ranges is [var section]&&(section.Start!=0||section.Span!=1))
      attributes.SetUserString(Prefix+"curve_key","section:"+JsonSerializer.Serialize(section));
    else if(mapped.Ranges.Length>1)
      attributes.SetUserString(Prefix+"curve_key","path:"+JsonSerializer.Serialize(mapped.Ranges));
  }
  internal static bool UpgradeReference(RhinoDoc doc,RhinoObject obj,ObjectAttributes attributes)
  {
    if(attributes.GetUserString(CurveReferenceKey)!=null)return false;
    if(!Guid.TryParse(attributes.GetUserString(Prefix+"curve_id"),out var id)||doc.Objects.FindId(id) is not {IsDeleted:false,Geometry:Curve owner} source)return false;
    if(attributes.GetUserString("vNest.instance") is {} instance&&
      (source.Attributes.GetUserString("vNest.instance")!=instance||source.Attributes.GetUserString("vNest.session")!=attributes.GetUserString("vNest.session")))return false;
    if(Guid.TryParse(attributes.GetUserString(MatchMemberKey),out var member)&&member!=obj.Id)return false;
    if(!TryPoint(attributes.GetUserString(Prefix+"curve_start"),out var start)||!TryPoint(attributes.GetUserString(Prefix+"curve_end"),out var end)||
      !TryPoint(attributes.GetUserString(Prefix+"curve_point"),out var point))return false;
    double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
    var candidates=new List<(Guid Id,Curve Segment)>{(id,owner.DuplicateCurve())};
    try
    {
      if(attributes.GetUserString(SourceCurveIdsKey) is {} json)
      {
        var ids=JsonSerializer.Deserialize<Guid[]>(json)??[];
        var directions=JsonSerializer.Deserialize<bool[]>(attributes.GetUserString(Prefix+"source_reversed")??"null");
        if(ids.Length>1)
        {
          foreach(var candidate in candidates)candidate.Segment.Dispose();candidates.Clear();
          foreach(var key in ids)
          {if(doc.Objects.FindId(key)?.Geometry is not Curve curve)return false;var copy=curve.DuplicateCurve();int index=candidates.Count;if(directions?.Length==ids.Length&&directions[index])copy.Reverse();candidates.Add((key,copy));}
        }
      }
      Curve? joined;
      if(candidates.Count==1)joined=candidates[0].Segment.DuplicateCurve();
      else{var paths=Curve.JoinCurves(candidates.Select(item=>item.Segment),tolerance,true);if(paths.Length!=1){foreach(var path in paths)path.Dispose();return false;}joined=paths[0];}
      using(joined)
      {
        if(!joined.ClosestPoint(start,out double first)||!joined.ClosestPoint(end,out double last)||
          joined.PointAt(first).DistanceTo(start)>tolerance||joined.PointAt(last).DistanceTo(end)>tolerance)return false;
        bool full=start.DistanceTo(joined.PointAtStart)<=tolerance&&end.DistanceTo(joined.PointAtEnd)<=tolerance;
        bool reverse=start.DistanceTo(joined.PointAtEnd)<=tolerance&&end.DistanceTo(joined.PointAtStart)<=tolerance;
        using var reference=full||reverse?joined.DuplicateCurve():joined.Trim(Math.Min(first,last),Math.Max(first,last));
        if(reference==null)return false;
        bool backwards=reverse&&!full||!full&&!reverse&&last<first;
        if(full&&reverse&&TryPoint(attributes.GetUserString(Prefix+"tangent_hint"),out var hint)&&reference.ClosestPoint(point,out double at))
          backwards=reference.TangentAt(at)*new Vector3d(hint)<0;
        if(backwards)reference.Reverse();
        if(!reference.ClosestPoint(point,out double parameter)||reference.PointAt(parameter).DistanceTo(point)>tolerance)return false;
        double station=reference.GetLength(new Interval(reference.Domain.T0,parameter));
        if(attributes.GetUserString(MatchSetKey)==null)
        {var box=obj.Geometry.GetBoundingBox(true);double reach=box.Diagonal.Length;foreach(string key in new[]{"notch_length","notch_offset","label_size"})if(double.TryParse(attributes.GetUserString(Prefix+key),NumberStyles.Float,CultureInfo.InvariantCulture,out double value))reach+=Math.Abs(value);if(box.ClosestPoint(point).DistanceTo(point)>reach+tolerance)return false;}
        if(BindReference(doc,attributes,reference,station))return true;
        if(!full&&!reverse)return false;
        if(backwards){candidates.Reverse();foreach(var candidate in candidates)candidate.Segment.Reverse();}
        return BindReference(doc,attributes,reference,station,candidates);
      }
    }
    catch(Exception ex) when(ex is JsonException or ArgumentException or InvalidOperationException)
    {Log.Write("MatchingNotches","Legacy reference could not be upgraded: {0}",ex.Message);return false;}
    finally{foreach(var candidate in candidates)candidate.Segment.Dispose();}
  }

  internal static bool TryPoint(string? text,out Point3d point)
  {
    point=Point3d.Unset;var values=text?.Split(',');
    if(values?.Length!=3||!double.TryParse(values[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double x)||
      !double.TryParse(values[1],NumberStyles.Float,CultureInfo.InvariantCulture,out double y)||
      !double.TryParse(values[2],NumberStyles.Float,CultureInfo.InvariantCulture,out double z))return false;
    point=new(x,y,z);return point.IsValid;
  }
  internal static bool TransformMetadata(ObjectAttributes attributes,Transform transform)
  {
    if(!HasMetadata(attributes))return false;
    static string Format(Point3d point)=>string.Join(",",new[]{point.X,point.Y,point.Z}.Select(value=>value.ToString("R",CultureInfo.InvariantCulture)));
    foreach(string key in new[]{"curve_start","curve_end","curve_mid","curve_point"})
      if(TryPoint(attributes.GetUserString(Prefix+key),out var point))attributes.SetUserString(Prefix+key,Format(transform*point));
    if(TryPoint(attributes.GetUserString(Prefix+"tangent_hint"),out var hint))
    {
      var tangent=transform*new Vector3d(hint);
      if(tangent.Unitize())attributes.SetUserString(Prefix+"tangent_hint",Format(new Point3d(tangent)));
    }
    if(transform.Determinant<0&&attributes.GetUserString(Prefix+"curve_side") is "Left" or "Right")
      attributes.SetUserString(Prefix+"curve_side",attributes.GetUserString(Prefix+"curve_side")=="Left"?"Right":"Left");
    return true;
  }
  internal static ObjectAttributes Attributes(Curve sourceCurve,Guid sourceCurveId,int curveIndex,string placementMode,
    double lengthFromStart,double notchLength,double notchOffset,string notchType,double notchWidth,string curveSide,
    bool labelEnabled,string labelText,double labelSize,double labelOffset,double labelOffsetY,string labelSide,
    string notchLayer,string labelLayer,Vector3d tangent,int layerIndex)
  {
    var attributes=new ObjectAttributes{ObjectId=Guid.NewGuid(),LayerIndex=layerIndex,Name="Notch"};
    void Set(string key,string value)=>attributes.SetUserString(Prefix+key,value??string.Empty);
    static string Number(double value)=>value.ToString("R",CultureInfo.InvariantCulture);
    static string Point(Point3d value)=>string.Join(",",Number(value.X),Number(value.Y),Number(value.Z));
    double sourceLength=sourceCurve.GetLength();
    Set("version",LegacyVersion);Set("object_role","notch");Set("notch_id",attributes.ObjectId.ToString());
    Set("curve_id",sourceCurveId==Guid.Empty?string.Empty:sourceCurveId.ToString());
    Set("curve_key",sourceCurveId==Guid.Empty?string.Empty:$"obj:{sourceCurveId}");Set("curve_index",curveIndex.ToString(CultureInfo.InvariantCulture));
    Set("mode",placementMode);Set("length_from_start",Number(lengthFromStart));
    Set("percent",Number(placementMode.Equals("percent",StringComparison.OrdinalIgnoreCase)&&sourceLength>RhinoMath.ZeroTolerance?lengthFromStart/sourceLength:0));
    Set("notch_length",Number(notchLength));Set("notch_offset",Number(notchOffset));Set("notch_type",notchType);Set("notch_width",Number(notchWidth));
    Set("curve_side",curveSide);Set("label_enabled",labelEnabled?"1":"0");Set("label_text",labelText);Set("label_size",Number(labelSize));
    Set("label_offset",Number(labelOffset));Set("label_offset_y",Number(labelOffsetY));Set("label_side",labelSide);
    Set("notch_layer",notchLayer);Set("label_layer",labelLayer);Set("tangent_hint",Point(new Point3d(tangent)));
    Set("curve_start",Point(sourceCurve.PointAtStart));Set("curve_end",Point(sourceCurve.PointAtEnd));
    Set("curve_mid",Point(PointAtLength(sourceCurve,sourceLength*.5)));Set("curve_point",Point(PointAtLength(sourceCurve,lengthFromStart)));
    return attributes;
  }
}
