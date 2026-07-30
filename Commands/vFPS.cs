using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// Toggles the viewport frames-per-second overlay.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vFPS : Command
{
  public override string EnglishName => "vFPS";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var enabled = FpsDisplay.Toggle();
    doc.Views.Redraw();
    RhinoApp.WriteLine($"Viewport FPS: {(enabled ? "ON" : "OFF")}");
    Log.Write("vFPS", enabled ? "enabled" : "disabled");
    return Result.Success;
  }
}
