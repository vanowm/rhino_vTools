using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>
/// Opens the bundled vTools command index.
/// </summary>
[CommandStyle(Style.Transparent | Style.DoNotRepeat | Style.ScriptRunner)]
public sealed class vHelp : vToolsCommand
{
  private const string OpenHelpPanelMacro = "_CommandHelp"; // Native Rhino macro that opens and activates the Command Help panel.
  private const int HelpPanelReadyDelayMilliseconds = 500; // UI delay in milliseconds before replacing Rhino's initial welcome topic after opening the panel.

  public override string EnglishName => "vHelp";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    DisplayHelp(Id);
    var showAfterUtc = DateTime.UtcNow.AddMilliseconds(HelpPanelReadyDelayMilliseconds);
    EventHandler? showIndex = null;
    showIndex = (_, _) =>
    {
      if (DateTime.UtcNow < showAfterUtc)
        return;

      RhinoApp.Idle -= showIndex;
      DisplayHelp(Id);
    };

    RhinoApp.Idle += showIndex;
    _ = RhinoApp.RunScript(OpenHelpPanelMacro, false);
    return Result.Success;
  }
}
