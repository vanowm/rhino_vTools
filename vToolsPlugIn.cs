using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;
using Rhino.UI;
using Rhino.UI.DialogPanels;
using System;
using System.Collections.Generic;
using System.Linq;
using vTools.Commands;

namespace vTools;

/// <summary>
/// Rhino plug-in entry point for the vTools command set.
/// </summary>
[System.Runtime.InteropServices.Guid("2607512e-a1fc-4cf9-9329-a293431437a0")]
public class vToolsPlugIn : PlugIn
{
  // Defaults and customizable constants
  private const int CommandHelpPanelRestoreTimeoutMilliseconds = 10000; // Maximum milliseconds to wait for a startup-restored Command Help panel before leaving later panel activation to Rhino.

  private EventHandler? _commandHelpRestoreIdleHandler;

  public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

  protected override string LocalPlugInName => "vTools";

  public vToolsPlugIn() { Instance = this; }

  public static vToolsPlugIn Instance { get; private set; } = null!;

  protected override LoadReturnCode OnLoad(ref string errorMessage)
  {
    var asm = GetType().Assembly;
    var version = (!string.IsNullOrEmpty(asm.Location)
      ? System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location).FileVersion
      : null) ?? asm.GetName().Version?.ToString() ?? "unknown";

    Log.Initialize();
    Log.Write($"startup  rhino={RhinoApp.Version}  version={version}  dll={asm.Location}");
    LocalUndoRedoShortcutSession.RepairStaleShortcutMacros();
    FpsDisplay.Start();
    CommandFailSoundMonitor.Start();
    HideSetState.StartPolling();
    PerpGumballMonitor.Start();

    var commandNames = CollectRegisteredCommandNames();
    EnsureRestoredCommandHelpPanelIsActive();
    Log.Write($"startup  commands ({commandNames.Count}): {string.Join(", ", commandNames)}");

    RhinoApp.WriteLine($"vTools v{version} loaded — {commandNames.Count} commands: {string.Join(", ", commandNames)}.");
    return LoadReturnCode.Success;
  }

  protected override void OnShutdown()
  {
    StopCommandHelpPanelRestoreCheck();
    vFilterExec.StopPending();
    FpsDisplay.Stop();
    CommandFailSoundMonitor.Stop();
    HideSetState.StopPolling();
    PerpGumballMonitor.Stop();
    base.OnShutdown();
  }

  private List<string> CollectRegisteredCommandNames()
  {
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    try
    {
      foreach (var command in GetCommands())
      {
        if (command == null || IsHiddenCommand(command))
          continue;

        var name = (command.EnglishName ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(name))
          names.Add(name);
      }
    }
    catch { }
    return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
  }

  private void EnsureRestoredCommandHelpPanelIsActive()
  {
    var startedUtc = DateTime.UtcNow;
    var deadlineUtc = startedUtc.AddMilliseconds(
      CommandHelpPanelRestoreTimeoutMilliseconds);

    _commandHelpRestoreIdleHandler = (_, _) =>
    {
      var nowUtc = DateTime.UtcNow;
      var panel = CommandHelpPanel.Instance;
      if (panel != null && Panels.IsPanelVisible(CommandHelpPanel.Id))
      {
        panel.PanelShown(
          RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0,
          ShowPanelReason.Show);
        Log.Write(
          "CommandHelp",
          $"restored panel activated elapsedMs=" +
          $"{(nowUtc - startedUtc).TotalMilliseconds:F0} " +
          $"loaded={panel.Loaded} url={panel.HelpUrl ?? "<none>"}");
        StopCommandHelpPanelRestoreCheck();
        return;
      }

      if (nowUtc < deadlineUtc)
        return;

      Log.Write(
        "CommandHelp",
        $"no startup-restored panel detected elapsedMs=" +
        $"{(nowUtc - startedUtc).TotalMilliseconds:F0}");
      StopCommandHelpPanelRestoreCheck();
    };

    RhinoApp.Idle += _commandHelpRestoreIdleHandler;
  }

  private void StopCommandHelpPanelRestoreCheck()
  {
    if (_commandHelpRestoreIdleHandler == null)
      return;

    RhinoApp.Idle -= _commandHelpRestoreIdleHandler;
    _commandHelpRestoreIdleHandler = null;
  }

  private static bool IsHiddenCommand(Command command)
  {
    var attribute = command.GetType()
      .GetCustomAttributes(typeof(CommandStyleAttribute), false)
      .OfType<CommandStyleAttribute>()
      .FirstOrDefault();
    return attribute != null && (attribute.Styles & Style.Hidden) != 0;
  }
}
