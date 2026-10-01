using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

[CommandStyle(Style.Hidden | Style.Transparent | Style.ScriptRunner | Style.DoNotRepeat | Style.NotUndoable)]
public sealed class vPerpGumballProxy : Command
{
  public override string EnglishName => PerpGumballMonitor.ApplyProxyCommandName;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    PerpGumballMonitor.ApplyPendingCommand();
}
