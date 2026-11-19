using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;

namespace vTools;

internal static class PartGroupData
{
  // Shared part-data contract and conflict-dialog defaults.
  internal const string NestSection = "vNest.PartSettings"; // Document section holding opaque nesting settings per persistent part identity.
  internal const string CustomSection = "vObjectPropertiesPlus.PartData"; // Document section holding custom fields per persistent part identity.
  internal const string NamesSection = "vTools.PartNames"; // Document section holding display-name overrides per persistent part identity.
  internal const string SourceSettingsKey = "vNest.sourceSettings"; // Object user-string linking placed output to a source part identity.
  internal const string FaceDownKey = "vNest.faceDown"; // Placed-copy cutting-side marker; true means face down, false or absent means face up.
  private const int MaximumNestRecordCharacters = 16384; // Maximum JSON characters accepted when reading the shared nesting record.
  private const string GroupPrefix = "group-"; // Part key prefix followed by a group GUID in N format.
  private const string BoundaryPrefix = "boundary-"; // Part key prefix followed by a boundary object GUID in N format.
  [global::vTools.CommandDefault("PartGroupData", "vTools.PartGroupData.DialogPadding", "Conflict-dialog padding in device-independent pixels.")] private const int DialogPadding = 10; // Conflict-dialog padding in device-independent pixels.

  private static readonly int ConfiguredDialogPadding = global::vTools.CommandDefaults.Get("PartGroupData", "vTools.PartGroupData.DialogPadding", DialogPadding); // Resolved once from advanced defaults; restart Rhino after changes.
  private const int ChoiceWidth = 380; // Part-data source list width in device-independent pixels.
  private const int ChoiceHeight = 180; // Part-data source list height in device-independent pixels.
  private const int DisplayNameLimit = 80; // Maximum displayed source-name characters; stored data is never truncated.
  private static readonly string[] Sections = [NestSection, CustomSection, NamesSection]; // Whole-record copy order; absent values clear only the destination entry.

  internal sealed record Snapshot(string Key, Guid GroupId, int GroupIndex,
    HashSet<Guid> Members, string Name, string?[] Values);

  internal static string GroupKey(Guid id) => GroupPrefix + id.ToString("N");
  internal static bool ReadFaceDown(RhinoDoc doc,RhinoObject obj)
  {
    var keys=(obj.Attributes.GetGroupList()??[]).Where(index=>doc.Groups[index] is {IsDeleted:false})
      .OrderBy(index=>doc.Groups.GroupObjectCount(index)).Select(index=>GroupKey(doc.Groups[index].Id))
      .Concat(new[]{BoundaryPrefix+obj.Id.ToString("N"),obj.Attributes.GetUserString(SourceSettingsKey)});
    foreach(var key in keys.Where(key=>!string.IsNullOrEmpty(key)))
    {
      string? saved=doc.Strings.GetValue(NestSection,key!);if(string.IsNullOrEmpty(saved))continue;
      if(saved.Length>MaximumNestRecordCharacters)throw new InvalidOperationException("Saved part settings are too large.");
      using var json=JsonDocument.Parse(saved);
      return json.RootElement.TryGetProperty("FaceDown",out var face)&&face.GetBoolean();
    }
    return bool.TryParse(obj.Attributes.GetUserString(FaceDownKey),out bool down)&&down;
  }

  internal static List<Snapshot> Capture(RhinoDoc doc, IEnumerable<Guid> ids)
  {
    var objects = ids.Distinct().Select(doc.Objects.FindId).Where(obj => obj is { IsDeleted: false }).ToArray();
    var snapshots = new List<Snapshot>();
    foreach (int index in objects.SelectMany(obj => obj!.Attributes.GetGroupList() ?? []).Distinct())
    {
      if (doc.Groups[index] is not { IsDeleted: false } group) continue;
      Add(GroupKey(group.Id), group.Id, index,
        doc.Groups.GroupMembers(index).Where(obj => !obj.IsDeleted).Select(obj => obj.Id).ToHashSet(), group.Name);
    }
    foreach (var obj in objects)
      Add(BoundaryPrefix + obj!.Id.ToString("N"), Guid.Empty, -1, [obj.Id], obj.Attributes.Name);
    return snapshots;

    void Add(string key, Guid groupId, int index, HashSet<Guid> members, string? name)
    {
      var values = Sections.Select(section => doc.Strings.GetValue(section, key)).ToArray();
      if (!values.Any(value => !string.IsNullOrEmpty(value))) return;
      snapshots.Add(new(key, groupId, index, members,
        !string.IsNullOrWhiteSpace(values[2]) ? values[2]! : !string.IsNullOrWhiteSpace(name) ? name : key, values));
    }
  }

  internal static Snapshot[] Candidates(IEnumerable<Snapshot> snapshots, IReadOnlySet<Guid> members)
  {
    var candidates = snapshots.Where(snapshot => snapshot.Members.Overlaps(members)).ToArray();
    var grouped = candidates.Where(snapshot => snapshot.GroupIndex >= 0 && snapshot.Members.IsSubsetOf(members)).ToArray();
    return candidates.Where(snapshot => snapshot.GroupIndex >= 0 || !grouped.Any(group => snapshot.Members.IsSubsetOf(group.Members)))
      .OrderByDescending(snapshot => snapshot.Members.IsSubsetOf(members))
      .ThenBy(snapshot => snapshot.Members.Count).ThenBy(snapshot => snapshot.Key, StringComparer.Ordinal).ToArray();
  }

  internal static bool TryAutomaticSource(IReadOnlyList<Snapshot> candidates, out Snapshot? source)
  {
    source = candidates.FirstOrDefault();
    return source == null || candidates.All(candidate => candidate.Values.SequenceEqual(candidates[0].Values));
  }

  internal static bool TryChooseSource(RhinoDoc doc, IReadOnlyList<Snapshot> candidates, out Snapshot? source)
  {
    if (TryAutomaticSource(candidates, out source)) return true;
    using var dialog = new Dialog<Snapshot?> { Title = "Keep Part Data", Padding = new Padding(ConfiguredDialogPadding), Resizable = true };
    var descriptions = candidates.Select(Description).ToArray();
    var list = new ListBox { DataStore = descriptions.Select((name, index) => descriptions.Count(value => value == name) > 1
      ? name + " [" + candidates[index].Key[^8..] + "]" : name).ToArray(), SelectedIndex = 0, Width = ChoiceWidth, Height = ChoiceHeight };
    using var highlight = new PreviewDisplay.ObjectHighlighter(doc);
    void HighlightSource()
    { highlight.SetObjects(list.SelectedIndex >= 0 ? candidates[list.SelectedIndex].Members : []); doc.Views.Redraw(); }
    list.SelectedIndexChanged += (_, _) => HighlightSource();
    var keep = new Button { Text = "Keep Data" };
    var cancel = new Button { Text = "Cancel" };
    keep.Click += (_, _) => { if (list.SelectedIndex >= 0) dialog.Close(candidates[list.SelectedIndex]); };
    cancel.Click += (_, _) => dialog.Close(null);
    dialog.DefaultButton = keep; dialog.AbortButton = cancel;
    var layout = new DynamicLayout { Spacing = new Size(ConfiguredDialogPadding, ConfiguredDialogPadding) };
    layout.AddRow(new Label { Text = "Keep data from:" });
    layout.AddRow(list); layout.AddRow(null, keep, cancel); dialog.Content = layout;
    HighlightSource();
    try
    { source = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindowForDocument(doc)); return source != null; }
    finally { highlight.SetObjects([]); doc.Views.Redraw(); }
  }

  private static string Description(Snapshot source)
  {
    string name = source.Name.Length > DisplayNameLimit ? source.Name[..(DisplayNameLimit - 3)] + "..." : source.Name;
    try
    {
      if (!string.IsNullOrEmpty(source.Values[0]))
      {
        using var json = JsonDocument.Parse(source.Values[0]!);
        if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Material", out var material) && material.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(material.GetString())) return name + " (" + material.GetString() + ")";
      }
    }
    catch (JsonException) { }
    return name;
  }

  private static int ReusableGroup(RhinoDoc doc, HashSet<Guid> ids, Snapshot? source)
  {
    if (source is { GroupIndex: >= 0 } && doc.Groups[source.GroupIndex] is { IsDeleted: false } chosen &&
        chosen.Id == source.GroupId && source.Members.IsSubsetOf(ids)) return source.GroupIndex;
    foreach (int index in ids.Select(doc.Objects.FindId).Where(obj => obj != null)
        .SelectMany(obj => obj!.Attributes.GetGroupList() ?? []).Distinct())
    {
      if (doc.Groups[index] is not { IsDeleted: false }) continue;
      if (doc.Groups.GroupMembers(index).Where(obj => !obj.IsDeleted).Select(obj => obj.Id).ToHashSet().SetEquals(ids)) return index;
    }
    return -1;
  }

  internal static bool ApplyGroup(RhinoDoc doc, IEnumerable<Guid> memberIds, Snapshot? source, bool flatten, out int groupIndex)
  {
    groupIndex = -1;
    var ids = memberIds.ToHashSet();
    var objects = ids.Select(doc.Objects.FindId).ToArray();
    if (ids.Count == 0 || objects.Any(obj => obj is not { IsDeleted: false } || !obj.IsSelectable(true, false, false, false))) return false;
    var before = objects.Select(obj => (obj!.Id, Attributes: obj.Attributes.Duplicate())).ToArray();
    var oldGroups = before.SelectMany(item => item.Attributes.GetGroupList() ?? []).Distinct().ToArray();
    int reuse = ReusableGroup(doc, ids, source);
    int target = reuse >= 0 ? reuse : doc.Groups.Add();
    if (target < 0) { foreach (var item in before) item.Attributes.Dispose(); return false; }
    string destination = GroupKey(doc.Groups[target].Id);
    var previousData = Sections.Select(section => doc.Strings.GetValue(section, destination)).ToArray();
    try
    {
      foreach (var item in before)
      {
        var attributes = item.Attributes.Duplicate();
        if (flatten) attributes.RemoveFromAllGroups();
        attributes.AddToGroup(target);
        if (!doc.Objects.ModifyAttributes(item.Id, attributes, true)) throw new InvalidOperationException("Could not update group membership.");
      }
      if (source != null)
      {
        for (int i = 0; i < Sections.Length; i++)
        {
          if (doc.Strings.GetValue(Sections[i], destination) == source.Values[i]) continue;
          if (source.Values[i] == null) doc.Strings.Delete(Sections[i], destination);
          else doc.Strings.SetString(Sections[i], destination, source.Values[i]);
        }
      }
      if (flatten)
        foreach (int old in oldGroups)
          if (old != target && doc.Groups[old] is { IsDeleted: false } && doc.Groups.GroupObjectCount(old) == 0) doc.Groups.Delete(old);
      groupIndex = target;
      Log.Write("PartGroupData", "group={0} reused={1} members={2} source={3} flatten={4}",
        target, reuse >= 0, ids.Count, source?.Key ?? "none", flatten);
      return true;
    }
    catch (Exception ex)
    {
      foreach (var item in before) doc.Objects.ModifyAttributes(item.Id, item.Attributes.Duplicate(), true);
      for (int i = 0; i < Sections.Length; i++)
        if (previousData[i] == null) doc.Strings.Delete(Sections[i], destination);
        else doc.Strings.SetString(Sections[i], destination, previousData[i]);
      if (reuse < 0) doc.Groups.Delete(target);
      Log.Write("PartGroupData", "grouping rolled back: {0}", ex);
      return false;
    }
    finally { foreach (var item in before) item.Attributes.Dispose(); }
  }
}
