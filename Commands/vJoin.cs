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
/// Joins selected objects with Rhino's native Join command, optionally using copies.
/// </summary>
[CommandStyle(Style.Transparent | Style.ScriptRunner)]
public sealed class vJoin : vToolsCommand
{
  // Option defaults
  private const bool DefaultCopy = true; // true joins duplicates and keeps inputs; false joins the original objects.
  private const JoinLayerMode DefaultLayerMode = JoinLayerMode.Source; // Source inherits the first compatible input layer; Current uses Rhino's current layer.
  private static readonly string[] LayerModeNames = ["Current", "Source"]; // Command option labels matching JoinLayerMode order.

  private const string OptionsSectionName = "vJoin";
  private const string CopyOptionKey = "copy";
  private const string LayerOptionKey = "layer";

  private static bool _copy = DefaultCopy;
  private static JoinLayerMode _layerMode = DefaultLayerMode;

  private enum JoinLayerMode
  {
    Current,
    Source
  }

  public override string EnglishName => "vJoin";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var selectionResult = GetObjectsToJoin(doc, out var objectIds);
    if (selectionResult != Result.Success)
      return selectionResult;

    var groupMerge = _copy ? null : CaptureGroupMerge(doc, objectIds);
    var joinIds = objectIds;
    if (_copy)
    {
      joinIds = DuplicateObjects(doc, objectIds);
      if (joinIds.Count != objectIds.Count)
      {
        DeleteObjects(doc, joinIds);
        SelectOnly(doc, objectIds);
        RhinoApp.WriteLine("vJoin: could not copy every selected object.");
        Log.Write("vJoin", $"copy failed selected={objectIds.Count} copied={joinIds.Count}");
        return Result.Failure;
      }
    }

    Log.Write(
      "vJoin",
      $"joining count={joinIds.Count} copy={_copy} layer={_layerMode}");
    var inputDescription = DescribeObjects(doc, joinIds);

    var joined = JoinObjects(
      doc,
      joinIds,
      _layerMode,
      out var resultIds);

    // For copy mode the duplicates were intermediate; for no-copy mode the originals are replaced.
    DeleteObjects(doc, _copy ? joinIds : objectIds);

    if (!joined || resultIds.Count == 0)
    {
      SelectOnly(doc, objectIds);
      RhinoApp.WriteLine("vJoin: join failed.");
      return Result.Failure;
    }

    if (groupMerge != null)
      ApplyGroupMerge(doc, groupMerge, resultIds);

    SelectOnly(doc, resultIds);
    var outputDescription = DescribeObjects(doc, resultIds);
    var copyDescription = _copy
      ? "copies joined; originals kept"
      : "originals joined";
    RhinoApp.WriteLine(
      $"vJoin: {inputDescription} -> {outputDescription} ({copyDescription}).");
    Log.Write(
      "vJoin",
      $"result {inputDescription} -> {outputDescription} copy={_copy}");
    return Result.Success;
  }

  private static string DescribeObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    var total = 0;
    foreach (var objectId in objectIds.Distinct())
    {
      var geometry = doc.Objects.FindId(objectId)?.Geometry;
      if (geometry == null)
        continue;

      total++;
      var typeName = geometry switch
      {
        Curve => "curve",
        Brep brep when brep.Faces.Count == 1 => "surface",
        Brep => "polysurface",
        Extrusion => "extrusion",
        Mesh => "mesh",
        SubD => "SubD",
        _ => "object"
      };
      typeCounts[typeName] = typeCounts.TryGetValue(typeName, out var count)
        ? count + 1
        : 1;
    }

    var descriptions = typeCounts
      .Select(pair => $"{pair.Value} {Pluralize(pair.Key, pair.Value)}")
      .ToList();
    if (descriptions.Count == 1)
      return descriptions[0];
    if (descriptions.Count > 1)
      return $"{total} objects ({string.Join(", ", descriptions)})";
    return "0 objects";
  }

  private static string Pluralize(string typeName, int count)
  {
    if (count == 1)
      return typeName;
    return typeName switch
    {
      "mesh" => "meshes",
      "polysurface" => "polysurfaces",
      "SubD" => "SubDs",
      _ => typeName + "s"
    };
  }

  private static Result GetObjectsToJoin(RhinoDoc doc, out List<Guid> objectIds)
  {
    objectIds = new List<Guid>();

    var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt("Select objects to join");
    getter.GeometryFilter =
      ObjectType.Curve |
      ObjectType.Surface |
      ObjectType.Brep |
      ObjectType.Extrusion |
      ObjectType.Mesh |
      ObjectType.SubD;
    getter.SubObjectSelect = false;
    getter.GroupSelect = false;
    getter.AlreadySelectedObjectSelect = true;
    getter.AcceptNothing(true);
    getter.EnablePreSelect(true, true);
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;

    bool preselectedWaitingForConfirmation = false;
    while (true)
    {
      getter.ClearCommandOptions();
      var copyToggle = new OptionToggle(_copy, "No", "Yes");
      getter.AddOptionToggle("Copy", ref copyToggle);
      var layerOptionIndex = getter.AddOptionList(
        "Layer",
        LayerModeNames,
        (int)_layerMode);

      var getResult = getter.GetMultiple(2, 0);
      var optionChanged = false;
      if (copyToggle.CurrentValue != _copy)
      {
        _copy = copyToggle.CurrentValue;
        Log.Write("vJoin", $"Copy -> {_copy}");
        optionChanged = true;
      }
      if (getResult == GetResult.Option &&
          getter.Option().Index == layerOptionIndex)
      {
        var selectedLayerMode = getter.Option().CurrentListOptionIndex;
        if (selectedLayerMode >= 0 &&
            selectedLayerMode < LayerModeNames.Length &&
            selectedLayerMode != (int)_layerMode)
        {
          _layerMode = (JoinLayerMode)selectedLayerMode;
          Log.Write("vJoin", $"Layer -> {_layerMode}");
          optionChanged = true;
        }
      }
      if (optionChanged)
        SavePersistedOptions();

      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      if (getResult == GetResult.Option)
        continue;

      objectIds = SelectedJoinableObjectIds(doc);
      if (getResult == GetResult.Object)
      {
        if (getter.ObjectsWerePreselected && !preselectedWaitingForConfirmation)
        {
          preselectedWaitingForConfirmation = true;
          getter.EnablePreSelect(false, true);
          continue;
        }

        return objectIds.Count >= 2 ? Result.Success : Result.Nothing;
      }

      if (getResult == GetResult.Nothing)
        return objectIds.Count >= 2 ? Result.Success : Result.Nothing;

      return getResult == GetResult.Cancel ? Result.Cancel : Result.Failure;
    }
  }

  private static List<Guid> SelectedJoinableObjectIds(RhinoDoc doc)
  {
    var filter =
      ObjectType.Curve |
      ObjectType.Surface |
      ObjectType.Brep |
      ObjectType.Extrusion |
      ObjectType.Mesh |
      ObjectType.SubD;

    return doc.Objects
      .GetSelectedObjects(includeLights: false, includeGrips: false)
      .Where(obj => (obj.ObjectType & filter) != 0)
      .Select(obj => obj.Id)
      .Distinct()
      .ToList();
  }

  private static bool JoinObjects(
    RhinoDoc doc,
    List<Guid> ids,
    JoinLayerMode layerMode,
    out List<Guid> resultIds)
  {
    resultIds = new List<Guid>();
    var tol = doc.ModelAbsoluteTolerance;

    // Split by geometry type.
    var curves  = new List<(Guid Id, Curve Crv, ObjectAttributes Attr)>();
    var breps   = new List<(Guid Id, Brep   Brp, ObjectAttributes Attr)>();
    var meshes  = new List<(Guid Id, Mesh   Msh, ObjectAttributes Attr)>();

    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;
      switch (obj.Geometry)
      {
        case Curve c: curves.Add((id, c, obj.Attributes)); break;
        case Brep   b: breps.Add((id, b, obj.Attributes)); break;
        case Mesh   m: meshes.Add((id, m, obj.Attributes)); break;
        case Extrusion e:
          var eb = e.ToBrep(splitKinkyFaces: false);
          if (eb != null) breps.Add((id, eb, obj.Attributes));
          break;
      }
    }

    // Join curves.
    if (curves.Count > 0)
    {
      var crvArray = curves.Select(t => t.Crv).ToArray();
      var joined   = Curve.JoinCurves(crvArray, tol);
      var attr = OutputAttributes(doc, curves[0].Attr, layerMode);
      if (joined == null || joined.Length == 0)
      {
        Log.Write("vJoin", "Curve.JoinCurves returned null/empty");
        return false;
      }
      foreach (var jc in joined)
      {
        var nid = doc.Objects.AddCurve(jc, attr);
        if (nid == Guid.Empty) return false;
        resultIds.Add(nid);
      }
      Log.Write("vJoin", $"curves: {curves.Count} → {joined.Length}");
    }

    // Join breps.
    if (breps.Count > 0)
    {
      var brpArray = breps.Select(t => t.Brp).ToArray();
      var joined   = Brep.JoinBreps(brpArray, tol);
      var attr = OutputAttributes(doc, breps[0].Attr, layerMode);
      if (joined == null || joined.Length == 0)
      {
        Log.Write("vJoin", "Brep.JoinBreps returned null/empty");
        return false;
      }
      foreach (var jb in joined)
      {
        var nid = doc.Objects.AddBrep(jb, attr);
        if (nid == Guid.Empty) return false;
        resultIds.Add(nid);
      }
      Log.Write("vJoin", $"breps: {breps.Count} → {joined.Length}");
    }

    // Join meshes.
    if (meshes.Count > 0)
    {
      var joined = new Mesh();
      foreach (var (_, m, _) in meshes) joined.Append(m);
      joined.Weld(Math.PI);
      var attr = OutputAttributes(doc, meshes[0].Attr, layerMode);
      var nid  = doc.Objects.AddMesh(joined, attr);
      if (nid == Guid.Empty) return false;
      resultIds.Add(nid);
      Log.Write("vJoin", $"meshes: {meshes.Count} → 1");
    }

    return resultIds.Count > 0;
  }

  private static ObjectAttributes OutputAttributes(
    RhinoDoc doc,
    ObjectAttributes source,
    JoinLayerMode layerMode)
  {
    var attributes = source.Duplicate();
    if (layerMode == JoinLayerMode.Current)
      attributes.LayerIndex = doc.Layers.CurrentLayerIndex;
    return attributes;
  }

  private static GroupMergePlan? CaptureGroupMerge(
    RhinoDoc doc,
    IReadOnlyList<Guid> sourceIds)
  {
    var groupIndices = new List<int>();
    foreach (var sourceId in sourceIds)
    {
      var obj = doc.Objects.FindId(sourceId);
      if (obj == null)
        continue;

      foreach (var groupIndex in obj.Attributes.GetGroupList() ?? Array.Empty<int>())
      {
        if (groupIndex >= 0 &&
            !doc.Groups.IsDeleted(groupIndex) &&
            !groupIndices.Contains(groupIndex))
          groupIndices.Add(groupIndex);
      }
    }

    if (groupIndices.Count < 2)
      return null;

    var memberIds = new HashSet<Guid>();
    foreach (var groupIndex in groupIndices)
    {
      foreach (var member in doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<RhinoObject>())
      {
        if (member != null && !member.IsDeleted)
          memberIds.Add(member.Id);
      }
    }

    return new GroupMergePlan(groupIndices[0], groupIndices, memberIds);
  }

  private static void ApplyGroupMerge(
    RhinoDoc doc,
    GroupMergePlan plan,
    IReadOnlyCollection<Guid> resultIds)
  {
    var survivingIds = plan.MemberIds
      .Concat(resultIds)
      .Where(id => id != Guid.Empty && doc.Objects.FindId(id) != null)
      .Distinct()
      .ToList();

    foreach (var objectId in survivingIds)
    {
      var obj = doc.Objects.FindId(objectId);
      if (obj == null)
        continue;

      var attributes = obj.Attributes.Duplicate();
      foreach (var groupIndex in plan.GroupIndices)
        attributes.RemoveFromGroup(groupIndex);
      attributes.AddToGroup(plan.PrimaryGroupIndex);
      _ = doc.Objects.ModifyAttributes(obj, attributes, quiet: true);
    }

    foreach (var groupIndex in plan.GroupIndices.Skip(1))
      _ = doc.Groups.Delete(groupIndex);

    Log.Write(
      "vJoin",
      $"merged groups count={plan.GroupIndices.Count} primary={plan.PrimaryGroupIndex} members={survivingIds.Count}");
  }

  private static List<Guid> DuplicateObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    var duplicates = new List<Guid>();
    foreach (var objectId in objectIds)
    {
      var duplicateId = doc.Objects.Transform(objectId, Transform.Identity, deleteOriginal: false);
      if (duplicateId == Guid.Empty)
        break;
      duplicates.Add(duplicateId);
    }
    return duplicates;
  }

  private static void DeleteObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    foreach (var objectId in objectIds)
    {
      if (objectId != Guid.Empty && doc.Objects.FindId(objectId) != null)
        doc.Objects.Delete(objectId, quiet: true);
    }
  }

  private static void SelectOnly(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    doc.Objects.UnselectAll();
    foreach (var objectId in objectIds)
      doc.Objects.Select(objectId);
    doc.Views.Redraw();
  }

  private static void LoadPersistedOptions()
  {
    _copy = DefaultCopy;
    _layerMode = DefaultLayerMode;
    ToolsOptionStore.Read<int>(OptionsSectionName, section =>
    {
      if (ToolsOptionStore.TryGetBool(section, CopyOptionKey, out var copy))
        _copy = copy;
      if (ToolsOptionStore.TryGetString(section, LayerOptionKey, out var layer) &&
          Enum.TryParse(layer, true, out JoinLayerMode parsedLayerMode) &&
          Enum.IsDefined(parsedLayerMode))
        _layerMode = parsedLayerMode;
      return 0;
    });
  }

  private static void SavePersistedOptions()
  {
    if (!ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[CopyOptionKey] = _copy;
        section[LayerOptionKey] = _layerMode.ToString();
      }))
    {
      Log.Write("vJoin", $"could not save options: {ToolsOptionStore.LastError}");
    }
  }

  private sealed record GroupMergePlan(
    int PrimaryGroupIndex,
    IReadOnlyList<int> GroupIndices,
    IReadOnlyCollection<Guid> MemberIds);
}
