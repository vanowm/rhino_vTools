using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

[CommandStyle(Style.Hidden | Style.Transparent | Style.NotUndoable)]
public sealed class vFilterExecRepeat : Command
{
  public override string EnglishName => "vFilterExecRepeat";

  protected override string CommandContextHelpUrl =>
    vFilterExec.RepeatCommandHelpUrl;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    vFilterExec.RepeatLast();
}
