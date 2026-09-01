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
    var go = new GetObject();
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

    // Re-use the existing group when exactly one group spans the selection
    // and every member of that group is already selected.
    if (groupsInUse.Count == 1)
    {
      int reuseGroup = 0;
      foreach (var x in groupsInUse) reuseGroup = x;

      var existing    = doc.Objects.FindByGroup(reuseGroup);
      var selectedSet = new HashSet<System.Guid>(ids);
      bool allPresent = existing != null && existing.Length > 0
                        && System.Array.TrueForAll(existing, o => selectedSet.Contains(o.Id));

      if (allPresent)
      {
        var memberSet = new HashSet<System.Guid>(System.Array.ConvertAll(existing!, o => o.Id));
        foreach (var id in ids)
        {
          if (memberSet.Contains(id)) continue;
          var obj = doc.Objects.FindId(id);
          if (obj == null) continue;
          obj.Attributes.AddToGroup(reuseGroup);
          obj.CommitChanges();
        }
        doc.Views.Redraw();
        RhinoApp.WriteLine(
          $"vReGroup: {ids.Count} object(s) collected into one group | " +
          $"Consolidated groups: {groupsInUse.Count}");
        return Result.Success;
      }
    }

    // General case: dissolve all groups and create a new one.
    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;

      obj.Attributes.RemoveFromAllGroups();
      obj.CommitChanges();
    }

    // Delete groups that are now empty (no remaining members outside the selection).
    foreach (var g in groupsInUse)
    {
      var remaining = doc.Objects.FindByGroup(g);
      if (remaining == null || remaining.Length == 0)
        doc.Groups.Delete(g);
    }

    // Collect all objects in a single new group.
    var newGroup = doc.Groups.Add(ids);
    if (newGroup < 0)
    {
      RhinoApp.WriteLine("vReGroup: failed to create group.");
      return Result.Failure;
    }

    doc.Views.Redraw();
    RhinoApp.WriteLine(
      $"vReGroup: {ids.Count} object(s) collected into one group | " +
      $"Consolidated groups: {groupsInUse.Count}");
    return Result.Success;
  }
}
