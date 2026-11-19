using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools;

internal static class NestTableMetadata
{
  // Stable identification and legacy-output conventions
  internal const string GroupIdKey = "vNest.tableGroupId"; // Persistent group GUID in D format; survives group renaming and table-index remapping.
  internal const string RoleKey = "vNest.role"; // Role metadata for distinguishing table decorations from actual part geometry.
  internal const string BorderRole = "table-border"; // Table perimeter marker, not an individual part outline.
  internal const string LabelRole = "table-label"; // Table annotation marker, not an individual part label.
  internal const string LabelFrameRole = "table-label-frame"; // Frame of the grouped table-label part; excluded from nesting-source capture.
  internal const string LabelDividerRole = "table-label-divider"; // Shared table/label closing edge; decoration only, omitted from exported label blocks and parts.
  internal const string MaterialFrameRole = "table-material-frame"; // True usable material boundary inside an enclosed ruler frame; never a cutting part or export outline.
  internal const string HeaderFillRole = "table-header-fill"; // Persistent header background; table decoration, excluded from cleanup, nesting and DXF export.
  internal const string LayoutKey = "vNest.tableLayout"; // Geometry/attribute format discriminator; absent means legacy left-hand labels.
  internal const string EnclosedHeaderLayout = "enclosed-header-1"; // One outer rectangle enclosing the header and four ruler bands.
  internal const string HeaderFractionKey = "vNest.headerFraction"; // Header height divided by outer-frame height, strictly between zero and one; invariant under uniform scale.
  internal const string HeaderSlotKey = "vNest.headerSlot"; // Header text cell index 0 through 4: material, name, count, dimensions, yardage; independent of horizontal position.
  private const string LegacyBorderName = "NestTable"; // Rectangle object name used by pre-metadata nest output.
  private const string LegacyGroupPrefix = "vNest_"; // Former generated table group prefix followed by a session identifier.
  private const string LegacyTableToken = "_table_"; // Former generated table group delimiter; individual part groups use a different delimiter.

  internal static void Identify(ObjectAttributes attributes,Guid groupId,string session,int table,string name,string role)
  {
    attributes.SetUserString(GroupIdKey,groupId.ToString("D")); attributes.SetUserString(RoleKey,role);
    attributes.SetUserString("vNest.session",session); attributes.SetUserString("vNest.table",(table+1).ToString());
    attributes.SetUserString("vNest.tableName",name);
  }
  private static bool Marker(ObjectAttributes attributes)=>attributes.GetUserString(RoleKey) is BorderRole or LabelRole or LabelFrameRole or LabelDividerRole or MaterialFrameRole or HeaderFillRole;
  internal static bool HasDecorationRole(ObjectAttributes attributes) => Marker(attributes);
  internal static bool IsBorderGroup(RhinoDoc doc,RhinoObject border,int index)
  {
    if(index<0||index>=doc.Groups.Count||doc.Groups[index] is not {IsDeleted:false} group)return false;
    return Guid.TryParse(border.Attributes.GetUserString(GroupIdKey),out var id)?group.Id==id:
      border.Attributes.Name==LegacyBorderName&&Legacy(group.Name);
  }
  internal static bool IsTableBorder(RhinoDoc doc, RhinoObject obj) =>
    obj.Attributes.GetUserString(RoleKey) == BorderRole ||
    obj.Attributes.Name == LegacyBorderName && IsDecoration(doc, obj);
  internal static bool IsTableStructure(RhinoDoc doc,RhinoObject obj)=>IsTableBorder(doc,obj)||obj.Attributes.GetUserString(RoleKey) is LabelDividerRole or MaterialFrameRole or HeaderFillRole;
  internal static bool IsEnclosedHeader(GeometryBase geometry)=>geometry.GetUserString(LayoutKey)==EnclosedHeaderLayout;
  internal static bool TryHeaderFrame(Curve outline,out Rectangle3d header)
  {
    header=Rectangle3d.Unset;
    if(!IsEnclosedHeader(outline)||!outline.TryGetPolyline(out var points)||points.Count!=5||
      !double.TryParse(outline.GetUserString(HeaderFractionKey),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double fraction)||
      !double.IsFinite(fraction)||fraction<=0||fraction>=1)return false;
    var plane=new Plane(points[0],points[1]-points[0],points[3]-points[0]);
    if(!plane.IsValid)return false;
    double width=points[0].DistanceTo(points[1]),height=points[0].DistanceTo(points[3]);
    header=new Rectangle3d(plane,new Interval(0,width),new Interval(height*(1-fraction),height));
    return header.IsValid;
  }
  internal static bool IsCleanupDecoration(RhinoDoc doc, RhinoObject obj) =>
    Marker(obj.Attributes) || IsDecoration(doc, obj);
  private static bool Legacy(string? name)=>name?.StartsWith(LegacyGroupPrefix,StringComparison.Ordinal)==true&&name.Contains(LegacyTableToken,StringComparison.Ordinal);
  internal static bool IsDecoration(RhinoDoc doc,RhinoObject obj)
  {
    if(Marker(obj.Attributes)&&Guid.TryParse(obj.Attributes.GetUserString(GroupIdKey),out var id))
      return (obj.Attributes.GetGroupList()??[]).Any(index=>doc.Groups[index] is {IsDeleted:false} group&&group.Id==id);
    return obj.Attributes.Name==LegacyBorderName&&(obj.Attributes.GetGroupList()??[]).Any(index=>Legacy(doc.Groups.GroupName(index)));
  }
  internal static HashSet<int> TableGroups(RhinoDoc doc)
  {
    var result=new HashSet<int>();
    for(int index=0;index<doc.Groups.Count;index++)
    {
      if(doc.Groups[index] is not {IsDeleted:false} group) continue;
      var members=doc.Groups.GroupMembers(index)??[];
      if(members.Any(obj=>obj!=null&&Marker(obj.Attributes)&&Guid.TryParse(obj.Attributes.GetUserString(GroupIdKey),out var id)&&id==group.Id)||
        Legacy(group.Name)&&members.Any(obj=>obj?.Attributes.Name==LegacyBorderName)) result.Add(index);
    }
    return result;
  }
}
