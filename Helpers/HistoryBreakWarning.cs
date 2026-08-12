using System;
using System.Collections.Generic;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.UI;

namespace vTools;

/// <summary>
/// Restores Rhino's history warning for direct document edits made by script-runner commands.
/// Ordinary commands and delegated native commands are handled by Rhino itself.
/// </summary>
internal static class HistoryBreakWarning
{
  internal static HashSet<Guid> CaptureAffectedRecords(RhinoDoc doc, Guid objectId)
  {
    var records = new HashSet<Guid>();
    if (!HistorySettings.BrokenRecordWarningEnabled)
      return records;

    var obj = doc.Objects.FindId(objectId);
    if (obj == null)
      return records;

    try
    {
      if (obj.HasHistoryRecord())
        records.Add(obj.Id);

      foreach (var childId in obj.HistoryChildren() ?? Array.Empty<Guid>())
      {
        var child = doc.Objects.FindId(childId);
        if (child?.HasHistoryRecord() == true)
          records.Add(childId);
      }
    }
    catch (Exception ex)
    {
      Log.Write("History", $"could not inspect {objectId}: {ex.Message}");
    }

    return records;
  }

  internal static bool Confirm(string commandName, IReadOnlyCollection<Guid> affectedRecords)
  {
    if (!HistorySettings.BrokenRecordWarningEnabled || affectedRecords.Count == 0)
      return true;

    var objectLabel = affectedRecords.Count == 1 ? "object" : "objects";
    var message = $"The {commandName} command broke history on {affectedRecords.Count} {objectLabel}.";
    var result = Dialogs.ShowMessage(
      message,
      $"Rhino {RhinoApp.Version.Major}  History Warning",
      ShowMessageButton.OKCancel,
      ShowMessageIcon.Warning);
    var accepted = result == ShowMessageResult.OK;
    Log.Write("History",
      $"{commandName} pending records={affectedRecords.Count} accepted={accepted}");
    return accepted;
  }
}
