using Rhino.Commands;

namespace vTools.Commands;

[CommandStyle(Style.ScriptRunner)]
public sealed class vUnrollSrfUV : UnrollSrfCommandBase
{
  // Defaults and customizable constants
  private const string NativeCommandName = "_-UnrollSrfUV"; // Scriptable Rhino command used for UV-preserving surface unrolling.

  public override string EnglishName => "vUnrollSrfUV";

  protected override string NativeUnrollCommand => NativeCommandName;
}
