using Rhino.Commands;

namespace vTools.Commands;

[CommandStyle(Style.ScriptRunner)]
public sealed class vUnrollSrf : UnrollSrfCommandBase
{
  // Defaults and customizable constants
  private const string NativeCommandName = "_-UnrollSrf"; // Scriptable Rhino command used for developable-surface unrolling.

  public override string EnglishName => "vUnrollSrf";

  protected override string NativeUnrollCommand => NativeCommandName;
}
