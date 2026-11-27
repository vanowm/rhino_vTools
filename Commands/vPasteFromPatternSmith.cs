using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;

namespace vTools.Commands;

/// <summary>Pastes PatternSmith workspace patterns or editor geometry into the current Rhino drawing.</summary>
public sealed class vPasteFromPatternSmith : vToolsCommand
{
  // Clipboard shortcut defaults
  [CommandDefault("vPasteFromPatternSmith","vTools.Commands.vPasteFromPatternSmith.ViewportCtrlV",
    "On: Rhino's standard Ctrl+V accepts PatternSmith clipboard data and otherwise runs Rhino Paste. Off: only the command pastes PatternSmith data. Customized shortcuts are never replaced.")]
  private const bool DefaultViewportCtrlV=true; // true adds PatternSmith support to the built-in paste shortcut; false keeps native paste only.
  [CommandDefault("vPasteFromPatternSmith","vTools.Commands.vPasteFromPatternSmith.DefaultPlacement",
    "At cursor: Center the pasted selection at the cursor. Pick point: Attach a preview to the cursor and click or snap to place.")]
  private const PastePlacement DefaultPlacement=PastePlacement.AtCursor; // AtCursor pastes immediately; PickPoint waits for a clicked or typed point.
  internal static PastePlacement ConfiguredPlacement=>CommandDefaults.Get(nameof(vPasteFromPatternSmith),
    "vTools.Commands.vPasteFromPatternSmith.DefaultPlacement",DefaultPlacement);
  [CommandDefault("vPasteFromPatternSmith","vTools.Commands.vPasteFromPatternSmith.PasteBackgroundImage",
    "On: Embed a PatternSmith background image when its local source is available, even if the preview is hidden.\nOff: Paste geometry only.",OptionKeys=BackgroundImageKey)]
  private const bool DefaultPasteBackgroundImage=true; // true includes available photos; false pastes geometry only.
  private const string BackgroundImageKey="paste_background_image"; // Saved checkbox shared by the paste window and ordinary Paste.
  internal static bool ConfiguredPasteBackgroundImage=>ToolsOptionStore.Read(nameof(vPasteFromPatternSmith),section=>
    ToolsOptionStore.TryGetBool(section,BackgroundImageKey,out bool saved)?saved:
      CommandDefaults.Get(nameof(vPasteFromPatternSmith),"vTools.Commands.vPasteFromPatternSmith.PasteBackgroundImage",DefaultPasteBackgroundImage));
  internal static void SavePasteBackgroundImage(bool value)=>
    ToolsOptionStore.Update(nameof(vPasteFromPatternSmith),section=>section[BackgroundImageKey]=value);
  internal enum PastePlacement {AtCursor,PickPoint}

  private const string NativePasteCommand="_Paste"; // Native Rhino fallback when no custom clipboard format is present.
  private const string DefaultPasteMacro="'"+NativePasteCommand; // Factory Ctrl+V macro, used to recover an unrestored shortcut.
  private const string RoutedPasteCommand="_"+nameof(vPatternSmithClipboardPaste); // Hidden command used only by the native shortcut.
  private const string CancelCurrentCommandPrefix="! "; // Rhino macro prefix; retain it only if present on the original binding.
  private static string? _previousMacro;
  private static string? _routedMacro;

  public override string EnglishName=>nameof(vPasteFromPatternSmith);
  protected override Result RunCommand(RhinoDoc doc,RunMode mode)=>vImportPatternSmith.ImportClipboard(doc,mode!=RunMode.Scripted);

  internal static void StartViewportPaste()
  {
    if(OperatingSystem.IsWindows())RhinoApp.Idle+=InstallViewportPaste;
  }

  internal static void StopViewportPaste()
  {
    RhinoApp.Idle-=InstallViewportPaste;
    try
    {
      if(_routedMacro!=null&&_previousMacro!=null&&
        string.Equals(ShortcutKeySettings.GetMacro(ShortcutKey.CtrlV)?.Trim(),_routedMacro,StringComparison.OrdinalIgnoreCase))
        ShortcutKeySettings.SetMacro(ShortcutKey.CtrlV,_previousMacro);
    }
    catch(Exception error){Log.Write(nameof(vPasteFromPatternSmith),"could not restore native paste shortcut: {0}",error.Message);}
    finally{_previousMacro=null;_routedMacro=null;}
  }

  private static void InstallViewportPaste(object? sender,EventArgs e)
  {
    RhinoApp.Idle-=InstallViewportPaste;
    try
    {
      if(_routedMacro!=null||RhinoApp.MainWindowHandle()==IntPtr.Zero)return;
      var current=ShortcutKeySettings.GetMacro(ShortcutKey.CtrlV)?.Trim()??"";
      bool stale=string.Equals(MacroCommand(current),RoutedPasteCommand,StringComparison.OrdinalIgnoreCase);
      if(stale)current=current.StartsWith('!')?CancelCurrentCommandPrefix+NativePasteCommand:DefaultPasteMacro;
      bool enabled=CommandDefaults.Get(nameof(vPasteFromPatternSmith),"vTools.Commands.vPasteFromPatternSmith.ViewportCtrlV",DefaultViewportCtrlV);
      if(!enabled)
      {
        if(stale)ShortcutKeySettings.SetMacro(ShortcutKey.CtrlV,current);
        return;
      }
      if(!UsesNativePaste(current))
      {Log.Write(nameof(vPasteFromPatternSmith),"left customized Ctrl+V unchanged");return;}
      var route=(current.StartsWith('!')?CancelCurrentCommandPrefix:"")+RoutedPasteCommand;
      ShortcutKeySettings.SetMacro(ShortcutKey.CtrlV,route);
      _previousMacro=current;_routedMacro=route;
      Log.Write(nameof(vPasteFromPatternSmith),"PatternSmith clipboard routing installed on the native Ctrl+V shortcut");
    }
    catch(Exception error){Log.Write(nameof(vPasteFromPatternSmith),"could not enable clipboard shortcut: {0}",error.Message);}
  }

  private static string MacroCommand(string? macro)=>(macro??"").Trim().TrimStart('!').TrimStart();

  internal static bool UsesNativePaste(string? macro)
  {
    var command=MacroCommand(macro);
    if(command.StartsWith('\''))command=command[1..];
    return command.TrimStart('_','-').Equals("Paste",StringComparison.OrdinalIgnoreCase);
  }
  internal static string NativeFallback()=>_previousMacro is {} macro?MacroCommand(macro):NativePasteCommand;
}

[CommandStyle(Style.Hidden|Style.ScriptRunner|Style.DoNotRepeat)]
public sealed class vPatternSmithClipboardPaste : vToolsCommand
{
  public override string EnglishName=>nameof(vPatternSmithClipboardPaste);

  protected override Result RunCommand(RhinoDoc doc,RunMode mode)
  {
    if(PatternSmithClipboard.HasPatternsOrEntities())return vImportPatternSmith.ImportClipboard(doc,interactive:false);
    return RhinoApp.RunScript(doc.RuntimeSerialNumber,vPasteFromPatternSmith.NativeFallback(),false)?Result.Success:Result.Failure;
  }
}
