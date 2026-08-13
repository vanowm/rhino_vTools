using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Display;
using Rhino.Geometry;
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

  internal static bool Confirm(
    RhinoDoc doc,
    string commandName,
    IReadOnlyCollection<Guid> affectedRecords)
  {
    if (!HistorySettings.BrokenRecordWarningEnabled || affectedRecords.Count == 0)
      return true;

    var objectLabel = affectedRecords.Count == 1 ? "object" : "objects";
    var message = $"The {commandName} command broke history on {affectedRecords.Count} {objectLabel}.";
    var highlight = new AffectedBodyConduit(doc, affectedRecords) { Enabled = true };
    doc.Views.Redraw();
    ShowMessageResult result;
    try
    {
      result = Dialogs.ShowMessage(
        message,
        $"Rhino {RhinoApp.Version.Major}  History Warning",
        ShowMessageButton.OKCancel,
        ShowMessageIcon.Warning);
    }
    finally
    {
      highlight.Enabled = false;
      doc.Views.Redraw();
    }

    var accepted = result == ShowMessageResult.OK;
    Log.Write("History",
      $"{commandName} pending records={affectedRecords.Count} accepted={accepted}");
    return accepted;
  }

  private sealed class AffectedBodyConduit : DisplayConduit
  {
    private static readonly Color Orange = Color.FromArgb(255, 128, 0);
    private static readonly Color Outline = Color.FromArgb(155, 30, 100);
    private readonly RhinoDoc _doc;
    private readonly IReadOnlyCollection<Guid> _objectIds;
    private readonly DisplayMaterial _material = new(Orange)
    {
      Transparency = 0.25,
      BackTransparency = 0.25
    };

    internal AffectedBodyConduit(RhinoDoc doc, IReadOnlyCollection<Guid> objectIds)
    {
      _doc = doc;
      _objectIds = objectIds;
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      foreach (var objectId in _objectIds)
      {
        var geometry = _doc.Objects.FindId(objectId)?.Geometry;
        switch (geometry)
        {
          case Brep brep:
            e.Display.DrawBrepShaded(brep, _material);
            break;
          case Extrusion extrusion:
          {
            using var extrusionBrep = extrusion.ToBrep();
            if (extrusionBrep != null) e.Display.DrawBrepShaded(extrusionBrep, _material);
            break;
          }
          case Surface surface:
          {
            using var surfaceBrep = surface.ToBrep();
            if (surfaceBrep != null) e.Display.DrawBrepShaded(surfaceBrep, _material);
            break;
          }
          case Mesh mesh:
            e.Display.DrawMeshShaded(mesh, _material);
            break;
        }
      }
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      foreach (var objectId in _objectIds)
      {
        var geometry = _doc.Objects.FindId(objectId)?.Geometry;
        switch (geometry)
        {
          case Brep brep:
            DrawBrepEdges(e.Display, brep);
            break;
          case Extrusion extrusion:
          {
            using var extrusionBrep = extrusion.ToBrep();
            if (extrusionBrep != null) DrawBrepEdges(e.Display, extrusionBrep);
            break;
          }
          case Surface surface:
          {
            using var surfaceBrep = surface.ToBrep();
            if (surfaceBrep != null) DrawBrepEdges(e.Display, surfaceBrep);
            break;
          }
          case Mesh mesh:
            e.Display.DrawMeshWires(mesh, Outline, 4);
            break;
          case Curve curve:
            e.Display.DrawCurve(curve, Outline, 5);
            e.Display.DrawCurve(curve, Orange, 2);
            break;
          case Rhino.Geometry.Point point:
            e.Display.DrawPoint(point.Location, PointStyle.RoundSimple, 7, Outline);
            e.Display.DrawPoint(point.Location, PointStyle.RoundSimple, 5, Orange);
            break;
        }
      }
    }

    private static void DrawBrepEdges(DisplayPipeline display, Brep brep)
    {
      foreach (var edge in brep.Edges)
        display.DrawCurve(edge, Outline, 4);
    }
  }
}
