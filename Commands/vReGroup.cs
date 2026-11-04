using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Dissolves all existing groups (including nested sub-groups) on the selected
/// objects and collects them into one new group.
/// </summary>
public sealed class vReGroup : vToolsCommand
{
  public override string EnglishName => "vReGroup";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    using var go = new GetObject();
    go.SetCommandPrompt("Select objects to re-group");
    go.GroupSelect     = true;
    go.SubObjectSelect = false;
    go.EnablePreSelect(true, true);
    go.GetMultiple(1, 0);

    if (go.CommandResult() != Result.Success)
      return go.CommandResult();

    if (go.ObjectCount == 0)
      return Result.Nothing;

    // Collect selected object IDs and all group indices they belong to.
    var ids         = new List<System.Guid>(go.ObjectCount);
    var groupsInUse = new HashSet<int>();

    for (int i = 0; i < go.ObjectCount; i++)
    {
      var id  = go.Object(i).ObjectId;
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;

      ids.Add(id);

      var groups = obj.Attributes.GetGroupList();
      if (groups != null)
        foreach (var g in groups)
          groupsInUse.Add(g);
    }

    if (ids.Count == 0)
      return Result.Nothing;

    var data = PartGroupData.Capture(doc, ids);
    var candidates = PartGroupData.Candidates(data, new HashSet<System.Guid>(ids));
    if (!PartGroupData.TryChooseSource(doc, candidates, out var source)) return Result.Cancel;
    if (!PartGroupData.ApplyGroup(doc, ids, source, flatten: true, out _))
    {
      RhinoApp.WriteLine("vReGroup: failed to create group.");
      return Result.Failure;
    }

    doc.Views.Redraw();
    RhinoApp.WriteLine(ResultSummary(ids.Count, groupsInUse.Count));
    return Result.Success;
  }

  private static string ResultSummary(int objects, int groups) =>
    $"vReGroup: {objects} {(objects == 1 ? "object" : "objects")} and " +
    $"{groups} {(groups == 1 ? "group" : "groups")} collected into 1 group.";
}
