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
public sealed class vJoin : Command
{
  private const string OptionsSectionName = "vJoin";
  private const string CopyOptionKey = "copy";

  private static bool _copy;

  public override string EnglishName => "vJoin";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();

    var selectionResult = GetObjectsToJoin(doc, out var objectIds);
    if (selectionResult != Result.Success)
      return selectionResult;

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

    SelectOnly(doc, joinIds);
    Log.Write("vJoin", $"joining count={joinIds.Count} copy={_copy}");

    bool joined;
    try
    {
      joined = RhinoApp.RunScript("_Join", false);
    }
    catch (Exception ex)
    {
      Log.Write("vJoin", $"native Join failed: {ex}");
      joined = false;
    }

    if (joined)
      return Result.Success;

    if (_copy)
      DeleteObjects(doc, joinIds);
    SelectOnly(doc, objectIds);
    return Result.Failure;
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

      var getResult = getter.GetMultiple(2, 0);
      if (copyToggle.CurrentValue != _copy)
      {
        _copy = copyToggle.CurrentValue;
        SavePersistedOptions();
        Log.Write("vJoin", $"Copy -> {_copy}");
      }

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
    _copy = ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(section, CopyOptionKey, out var copy) && copy);
  }

  private static void SavePersistedOptions()
  {
    if (!ToolsOptionStore.Update(
      OptionsSectionName,
      section => section[CopyOptionKey] = _copy))
    {
      Log.Write("vJoin", $"could not save Copy: {ToolsOptionStore.LastError}");
    }
  }
}
