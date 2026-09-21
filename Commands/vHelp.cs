using Rhino;
using Rhino.Commands;
using Rhino.UI;
using Rhino.UI.DialogPanels;

namespace vTools.Commands;

/// <summary>
/// Opens the bundled vTools command index.
/// </summary>
[CommandStyle(Style.Transparent | Style.DoNotRepeat | Style.ScriptRunner)]
public sealed class vHelp : vToolsCommand
{
  // Defaults and customizable constants
  private const string Tag = "vHelp"; // Command and diagnostic-log name.
  private const string DeferredHelpNavigationMacro = "_vHelp"; // Silent command replay used to restore active command-help context after opening the panel.
  private const int HelpPanelPollIntervalMilliseconds = 25; // Milliseconds between checks while the Command Help panel is being created.
  private const int HelpNavigationRetryIntervalMilliseconds = 100; // Milliseconds between Rhino Command Help FindCommand navigation attempts.
  private const int HelpNavigationTimeoutMilliseconds = 1500; // Maximum milliseconds before falling back to a silent active-command replay.

  private static bool _isDeferredNavigation; // True only while the internally replayed vHelp invocation is pending.
  private static EventHandler? _navigationIdleHandler; // Current panel-readiness watcher, or null when no navigation is pending.

  public override string EnglishName => Tag;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var startedUtc = DateTime.UtcNow;
    var isDeferredNavigation = _isDeferredNavigation;
    _isDeferredNavigation = false;
    var targetUrl = CommandHelpUrl.ForCommand(Tag);
    var panel = CommandHelpPanel.Instance;
    Log.Write(Tag,
      $"--- run start --- visible={Panels.IsPanelVisible(CommandHelpPanel.Id)}" +
      $" loaded={panel?.Loaded == true} url={DisplayUrl(panel?.HelpUrl)}" +
      $" deferred={isDeferredNavigation}");

    DisplayHelp(Id);
    Log.Write(Tag, "  help navigation requested in active command context");
    if (isDeferredNavigation)
    {
      Log.Write(Tag, "--- deferred navigation complete ---");
      return Result.Success;
    }

    panel = CommandHelpPanel.Instance;
    if (CommandHelpUrl.IsCommandTopic(panel?.HelpUrl, targetUrl))
    {
      Log.Write(Tag, "--- vTools index already active ---");
      return Result.Success;
    }

    Panels.OpenPanel(CommandHelpPanel.Id);
    Log.Write(Tag,
      $"  Command Help panel opened visible=" +
      $"{Panels.IsPanelVisible(CommandHelpPanel.Id)}");
    NavigateWhenPanelIsReady(targetUrl, startedUtc);
    return Result.Success;
  }

  private static void NavigateWhenPanelIsReady(
    string targetUrl,
    DateTime startedUtc)
  {
    StopPendingNavigation();
    var deadlineUtc = startedUtc.AddMilliseconds(
      HelpNavigationTimeoutMilliseconds);
    var nextCheckUtc = DateTime.UtcNow;

    _navigationIdleHandler = (_, _) =>
    {
      var nowUtc = DateTime.UtcNow;
      if (nowUtc < nextCheckUtc)
        return;

      var panel = CommandHelpPanel.Instance;
      if (panel?.Loaded == true)
      {
        var beforeUrl = panel.HelpUrl;
        if (CommandHelpUrl.IsCommandTopic(beforeUrl, targetUrl))
        {
          Log.Write(Tag,
            $"--- panel navigation complete --- elapsedMs=" +
            $"{(nowUtc - startedUtc).TotalMilliseconds:F0}" +
            $" url={DisplayUrl(beforeUrl)}");
          StopPendingNavigation();
          return;
        }

        var found = panel.FindCommand(Tag, true);
        var afterUrl = panel.HelpUrl;
        Log.Write(Tag,
          $"  FindCommand found={found}" +
          $" elapsedMs={(nowUtc - startedUtc).TotalMilliseconds:F0}" +
          $" before={DisplayUrl(beforeUrl)}" +
          $" after={DisplayUrl(afterUrl)}");
        if (CommandHelpUrl.IsCommandTopic(afterUrl, targetUrl))
        {
          StopPendingNavigation();
          return;
        }

        nextCheckUtc = nowUtc.AddMilliseconds(
          HelpNavigationRetryIntervalMilliseconds);
      }
      else
      {
        nextCheckUtc = nowUtc.AddMilliseconds(
          HelpPanelPollIntervalMilliseconds);
      }

      if (nowUtc < deadlineUtc)
        return;

      StopPendingNavigation();

      _isDeferredNavigation = true;
      var replayStarted = RhinoApp.RunScript(DeferredHelpNavigationMacro, false);
      if (!replayStarted)
        _isDeferredNavigation = false;

      var elapsedMilliseconds = (DateTime.UtcNow - startedUtc).TotalMilliseconds;
      Log.Write(Tag,
        $"  panel navigation timed out; deferred command started={replayStarted}" +
        $" elapsedMs={elapsedMilliseconds:F0}" +
        $" loaded={panel?.Loaded == true}" +
        $" url={DisplayUrl(panel?.HelpUrl)}");
    };

    RhinoApp.Idle += _navigationIdleHandler;
  }

  private static void StopPendingNavigation()
  {
    if (_navigationIdleHandler == null)
      return;

    RhinoApp.Idle -= _navigationIdleHandler;
    _navigationIdleHandler = null;
  }

  private static string DisplayUrl(string? url) =>
    string.IsNullOrWhiteSpace(url) ? "<none>" : url;
}
