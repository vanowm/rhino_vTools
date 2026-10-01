using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// Toggles the perpendicular gumball monitor on and off.
/// </summary>
public sealed class vTogglePerpGumball : vToolsCommand
{
  // Defaults
  internal const bool DefaultEnabled = false; // true auto-orients eligible gumballs at startup; false leaves them unchanged.

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vTogglePerpGumball";

  /// <summary>
  /// Executes toggle for the monitor that auto-orients gumball for one selected grip.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var enabled = PerpGumballMonitor.Toggle(doc);
    RhinoApp.WriteLine(enabled ? "Perpendicular Gumball: ON" : "Perpendicular Gumball: OFF");
    return Result.Success;
  }
}
