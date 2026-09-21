using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Keeps selected objects visible and hides every other visible normal object,
/// optionally assigning the hidden objects to a named Rhino hide group.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vIsolate : vToolsCommand
{
  // Defaults and customizable constants
  private const string DefaultHideSetName = ""; // Empty string creates an unnamed isolate; any non-empty text is a Rhino hide-set name.
  private const string Tag = "vIsolate"; // Command and diagnostic-log name.
  private const string SelectionPrompt = // Combined object-selection and optional hide-set-name prompt.
    "Select objects to isolate or type an optional set name";
  private const string NamePrompt = "Name of object set to isolate"; // Sub-prompt used when the Name option is selected.
  private const string NameOption = "Name"; // Command option whose value displays the pending hide-set name.
  private const string EmptyNameLabel = "None"; // Name option value shown for an unnamed isolate.
  private const string ClearNameOption = "ClearName"; // Command option shown while a non-empty hide-set name is pending.

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var initialSelection = SelectedObjectIds(doc);
    var selectionSnapshot = HideSetState.CaptureNestedSelection(doc);
    try
    {
      return RunCommandCore(doc, initialSelection);
    }
    finally
    {
      HideSetState.RestoreNestedSelection(doc, selectionSnapshot);
    }
  }

  private static Result RunCommandCore(
    RhinoDoc doc,
    IReadOnlyCollection<Guid> initialSelection)
  {
    Log.Write(Tag, $"--- run start --- preselected={initialSelection.Count}");

    var isolateIds = GetObjectsToIsolate(
      doc,
      out var hideSetName,
      out var selectionResult);
    if (isolateIds.Count == 0)
    {
      RhinoApp.WriteLine("vIsolate: no objects selected.");
      Log.Write(Tag, $"  no isolate objects result={selectionResult}");
      return selectionResult == Result.Success ? Result.Nothing : selectionResult;
    }

    var isolateSet = isolateIds.ToHashSet();
    var objectsToHide = VisibleNormalObjects(doc)
      .Where(obj => !isolateSet.Contains(obj.Id))
      .ToList();

    Log.Write(Tag,
      $"  isolate={isolateIds.Count} hide candidates={objectsToHide.Count}");

    if (objectsToHide.Count == 0)
    {
      RestoreSelection(doc, isolateIds);
      doc.Views.Redraw();
      RhinoApp.WriteLine("vIsolate: nothing to hide.");
      return Result.Success;
    }

    doc.Objects.UnselectAll();
    var hideSetOrder = string.IsNullOrEmpty(hideSetName)
      ? 0
      : DateTime.UtcNow.Ticks;
    var hiddenCount = 0;
    var trackedCount = 0;
    var clearedCount = 0;
    foreach (var obj in objectsToHide)
    {
      var hasTrackedName = !string.IsNullOrEmpty(HideSetState.GetTrackedName(obj));
      var trackingReady = HideSetState.SetTrackedName(
        doc,
        obj.Id,
        hideSetName ?? string.Empty,
        hideSetOrder);
      if (string.IsNullOrEmpty(hideSetName))
      {
        var currentObject = doc.Objects.FindId(obj.Id);
        var nativeCleared = currentObject != null &&
          HideSetState.RemoveNativeName(currentObject);
        if ((hasTrackedName && trackingReady) || nativeCleared)
          clearedCount++;
      }

      using var objRef = new ObjRef(doc, obj.Id);
      var hidden = string.IsNullOrEmpty(hideSetName)
        ? doc.Objects.Hide(objRef, false)
        : doc.Objects.Hide(objRef, false, hideSetName);
      if (!hidden)
      {
        if (!string.IsNullOrEmpty(hideSetName))
          HideSetState.SetTrackedName(doc, obj.Id, string.Empty);
        continue;
      }

      hiddenCount++;
      if (!string.IsNullOrEmpty(hideSetName) && trackingReady)
        trackedCount++;
    }

    RestoreSelection(doc, isolateIds);
    doc.Views.Redraw();

    Log.Write(Tag,
      $"  hidden={hiddenCount}/{objectsToHide.Count}" +
      $" hideSet={(string.IsNullOrEmpty(hideSetName) ? "<none>" : hideSetName)}" +
      $" tracked={trackedCount} cleared={clearedCount}");

    if (hiddenCount != objectsToHide.Count)
    {
      RhinoApp.WriteLine(
        $"vIsolate: hid {hiddenCount} of {objectsToHide.Count} object(s).");
      return hiddenCount > 0 ? Result.Success : Result.Failure;
    }

    RhinoApp.WriteLine($"vIsolate: hidden {hiddenCount} object(s).");
    return Result.Success;
  }

  private static List<Guid> GetObjectsToIsolate(
    RhinoDoc doc,
    out string hideSetName,
    out Result commandResult)
  {
    hideSetName = DefaultHideSetName;

    using var getter = new GetObject();
    getter.SetCommandPrompt(SelectionPrompt);
    getter.SubObjectSelect = false;
    getter.GroupSelect = true;
    getter.AcceptNothing(true);
    getter.AcceptString(true);
    getter.EnablePreSelect(true, true);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;
    getter.EnableTransparentCommands(true);

    var preselectionReturned = false;
    while (true)
    {
      getter.ClearCommandOptions();
      var nameOptionIndex = getter.AddOption(
        NameOption,
        string.IsNullOrEmpty(hideSetName) ? EmptyNameLabel : hideSetName);
      var clearNameOptionIndex = -1;
      if (!string.IsNullOrEmpty(hideSetName))
        clearNameOptionIndex = getter.AddOption(ClearNameOption);

      var getResult = getter.GetMultiple(0, 0);
      commandResult = getter.CommandResult();
      if (commandResult != Result.Success)
        return new List<Guid>();

      if (getResult == GetResult.Option)
      {
        var optionIndex = getter.Option()?.Index ?? -1;
        if (optionIndex == clearNameOptionIndex)
        {
          hideSetName = DefaultHideSetName;
          Log.Write(Tag, "  hide-set cleared");
          continue;
        }

        if (optionIndex == nameOptionIndex)
        {
          if (!TryGetHideSetName(hideSetName, out hideSetName, out commandResult))
            return new List<Guid>();

          Log.Write(Tag, $"  hide-set={hideSetName}");
          var selected = SelectedObjectIds(doc);
          if (selected.Count > 0)
          {
            LogAcceptedSelection(selected.Count, hideSetName);
            return selected;
          }

          continue;
        }
      }

      if (getResult == GetResult.String)
      {
        hideSetName = HideSetState.NormalizeInput(getter.StringResult());
        Log.Write(Tag, $"  hide-set={hideSetName}");
        var selected = SelectedObjectIds(doc);
        if (selected.Count > 0)
        {
          LogAcceptedSelection(selected.Count, hideSetName);
          return selected;
        }

        continue;
      }

      if (getResult == GetResult.Object &&
          getter.ObjectsWerePreselected &&
          !preselectionReturned)
      {
        preselectionReturned = true;
        getter.EnablePreSelect(false, true);
        continue;
      }

      if (getResult is GetResult.Object or GetResult.Nothing)
      {
        var selected = SelectedObjectIds(doc);
        LogAcceptedSelection(selected.Count, hideSetName);
        return selected;
      }

      return new List<Guid>();
    }
  }

  private static bool TryGetHideSetName(
    string currentName,
    out string hideSetName,
    out Result commandResult)
  {
    hideSetName = currentName;
    using var getter = new GetString();
    getter.SetCommandPrompt(NamePrompt);
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);
    if (!string.IsNullOrEmpty(currentName))
      getter.SetDefaultString(currentName);

    var getResult = getter.Get();
    commandResult = getter.CommandResult();
    if (commandResult != Result.Success)
      return false;

    hideSetName = getResult == GetResult.String
      ? HideSetState.NormalizeInput(getter.StringResult())
      : DefaultHideSetName;
    return true;
  }

  private static void LogAcceptedSelection(int objectCount, string hideSetName)
  {
    Log.Write(Tag,
      $"  selection accepted objects={objectCount}" +
      $" hideSet={(string.IsNullOrEmpty(hideSetName) ? "<none>" : hideSetName)}");
  }

  private static List<Guid> SelectedObjectIds(RhinoDoc doc)
  {
    var selectedIds = new List<Guid>();
    foreach (var obj in doc.Objects.GetSelectedObjects(true, true))
    {
      selectedIds.Add(obj is GripObject grip ? grip.OwnerId : obj.Id);
    }

    // Rhino 9 can retain a parent as subobject-selected without returning it from
    // the ordinary selected-object enumeration used by earlier versions.
    foreach (var obj in doc.Objects)
    {
      if (obj.IsSelected(true) != 0)
        selectedIds.Add(obj is GripObject grip ? grip.OwnerId : obj.Id);
    }

    return ValidObjectIds(doc, selectedIds);
  }

  private static IEnumerable<RhinoObject> VisibleNormalObjects(RhinoDoc doc)
  {
    var settings = new ObjectEnumeratorSettings
    {
      NormalObjects = true,
      LockedObjects = false,
      HiddenObjects = false,
      IdefObjects = false,
      DeletedObjects = false,
      ActiveObjects = true,
      ReferenceObjects = false,
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      VisibleFilter = true
    };

    return doc.Objects.GetObjectList(settings);
  }

  private static List<Guid> ValidObjectIds(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds)
  {
    var result = new List<Guid>();
    var seen = new HashSet<Guid>();
    foreach (var objectId in objectIds)
    {
      if (objectId == Guid.Empty ||
          !seen.Add(objectId) ||
          doc.Objects.FindId(objectId) == null)
      {
        continue;
      }

      result.Add(objectId);
    }

    return result;
  }

  private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> objectIds)
  {
    doc.Objects.UnselectAll();
    foreach (var objectId in ValidObjectIds(doc, objectIds))
      doc.Objects.Select(objectId);
  }
}
