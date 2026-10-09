using Rhino;
using Rhino.DocObjects;

namespace vTools;

internal static class NestTableMetadata
{
  // Stable identification and legacy-output conventions
  internal const string GroupIdKey = "vNest.tableGroupId"; // Persistent group GUID in D format; survives group renaming and table-index remapping.
  internal const string RoleKey = "vNest.role"; // Role metadata for distinguishing table decorations from actual part geometry.
  internal const string BorderRole = "table-border"; // Table perimeter marker, not an individual part outline.
  internal const string LabelRole = "table-label"; // Table annotation marker, not an individual part label.
  internal const string LabelFrameRole = "table-label-frame"; // Frame of the grouped table-label part; excluded from nesting-source capture.
  private const string LegacyBorderName = "NestTable"; // Rectangle object name used by pre-metadata nest output.
  private const string LegacyGroupPrefix = "vNest_"; // Former generated table group prefix followed by a session identifier.
  private const string LegacyTableToken = "_table_"; // Former generated table group delimiter; individual part groups use a different delimiter.

  internal static void Identify(ObjectAttributes attributes,Guid groupId,string session,int table,string name,string role)
  {
    attributes.SetUserString(GroupIdKey,groupId.ToString("D")); attributes.SetUserString(RoleKey,role);
    attributes.SetUserString("vNest.session",session); attributes.SetUserString("vNest.table",(table+1).ToString());
    attributes.SetUserString("vNest.tableName",name);
  }
  private static bool Marker(ObjectAttributes attributes)=>attributes.GetUserString(RoleKey) is BorderRole or LabelRole or LabelFrameRole;
  internal static bool HasDecorationRole(ObjectAttributes attributes) => Marker(attributes);
  internal static bool IsTableBorder(RhinoDoc doc, RhinoObject obj) =>
    obj.Attributes.GetUserString(RoleKey) == BorderRole ||
    obj.Attributes.Name == LegacyBorderName && IsDecoration(doc, obj);
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
