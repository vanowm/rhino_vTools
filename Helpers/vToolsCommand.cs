using System.Drawing;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.Runtime;

namespace vTools.Commands;

/// <summary>
/// Gives every visible vTools command a Rhino Help topic with its description and workflow.
/// </summary>
public abstract class vToolsCommand : Command
{
  // Defaults and customizable constants
  internal const string HelpFileName = "vToolsHelp.html"; // Deployment-relative HTML filename copied or extracted beside the loaded DLL.
  internal const string HelpResourceName = "vTools.Help.vToolsHelp.html"; // Manifest resource name containing the offline command-help document.
  internal const string TemporaryFileSuffix = ".tmp"; // Suffix used for an atomic same-directory help-file replacement.
  internal const string LightThemeName = "light"; // HTML color-scheme name used when Rhino's current panel background is light.
  internal const string DarkThemeName = "dark"; // HTML color-scheme name used when Rhino's current panel background is dark.
  internal const double DarkBackgroundLuminanceThreshold = 0.5; // Relative RGB luminance from 0 through 1 below which the help uses dark native controls.
  internal const double SectionPanelColorWeight = 0.5; // Rhino panel-color share from 0 through 1 blended into the document background for section bars.

  protected override string CommandContextHelpUrl =>
    CommandHelpUrl.ForCommand(EnglishName);
}

internal static class CommandHelpUrl
{
  private static readonly object SyncRoot = new();
  private static string? _documentUrl;

  internal static string ForCommand(string commandName)
  {
    var documentUrl = EnsureDocumentUrl();
    if (string.IsNullOrWhiteSpace(documentUrl) ||
        string.IsNullOrWhiteSpace(commandName))
      return string.Empty;

    return documentUrl + BuildThemeQuery() + "#" +
      Uri.EscapeDataString(commandName.Trim().ToLowerInvariant());
  }

  private static string BuildThemeQuery()
  {
    try
    {
      var panel = GetThemeColor(PaintColor.PanelBackground);
      var background = GetThemeColor(PaintColor.EditBoxBackground);
      var theme = IsDark(background)
        ? vToolsCommand.DarkThemeName
        : vToolsCommand.LightThemeName;
      var values = new Dictionary<string, string>
      {
        ["theme"] = theme,
        ["background"] = ToHtmlRgb(background),
        ["surface"] = ToHtmlRgb(Blend(
          background,
          panel,
          vToolsCommand.SectionPanelColorWeight)),
        ["field"] = ToHtmlRgb(background),
        ["text"] = ToHtmlRgb(GetThemeColor(PaintColor.TextEnabled)),
        ["muted"] = ToHtmlRgb(GetThemeColor(PaintColor.TextDisabled)),
        ["border"] = ToHtmlRgb(GetThemeColor(PaintColor.GridLinesOnPanelBackground)),
        ["link"] = ToHtmlRgb(AppearanceSettings.CommandPromptHypertextColor)
      };

      return "?" + string.Join("&", values.Select(pair =>
        Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }
    catch (Exception ex)
    {
      Log.Write("CommandHelp", $"Unable to read the current Rhino help palette: {ex}");
      return "?theme=" + (HostUtils.RunningInDarkMode
        ? vToolsCommand.DarkThemeName
        : vToolsCommand.LightThemeName);
    }
  }

  private static Color GetThemeColor(PaintColor color) =>
    AppearanceSettings.GetPaintColor(color, compute: true);

  private static bool IsDark(Color color)
  {
    var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
    return luminance < vToolsCommand.DarkBackgroundLuminanceThreshold;
  }

  private static Color Blend(Color background, Color foreground, double foregroundWeight)
  {
    var backgroundWeight = 1.0 - foregroundWeight;
    return Color.FromArgb(
      (int)Math.Round(background.R * backgroundWeight + foreground.R * foregroundWeight),
      (int)Math.Round(background.G * backgroundWeight + foreground.G * foregroundWeight),
      (int)Math.Round(background.B * backgroundWeight + foreground.B * foregroundWeight));
  }

  private static string ToHtmlRgb(Color color) =>
    $"{color.R:X2}{color.G:X2}{color.B:X2}";

  private static string EnsureDocumentUrl()
  {
    lock (SyncRoot)
    {
      if (!string.IsNullOrWhiteSpace(_documentUrl))
        return _documentUrl;

      try
      {
        var helpPath = PluginPaths.ResolveFile(vToolsCommand.HelpFileName);
        EnsureCurrentHelpFile(helpPath);
        if (!File.Exists(helpPath))
          return string.Empty;

        _documentUrl = new Uri(helpPath).AbsoluteUri;
        return _documentUrl;
      }
      catch (Exception ex)
      {
        Log.Write("CommandHelp", $"Unable to prepare command help: {ex}");
        return string.Empty;
      }
    }
  }

  private static void EnsureCurrentHelpFile(string helpPath)
  {
    var assembly = typeof(CommandHelpUrl).Assembly;
    if (File.Exists(helpPath) &&
        File.GetLastWriteTimeUtc(helpPath) >= File.GetLastWriteTimeUtc(assembly.Location))
      return;

    using var resource = assembly.GetManifestResourceStream(vToolsCommand.HelpResourceName);
    if (resource == null)
    {
      Log.Write("CommandHelp", $"Embedded help resource missing: {vToolsCommand.HelpResourceName}");
      return;
    }

    using var memory = new MemoryStream();
    resource.CopyTo(memory);
    var embeddedBytes = memory.ToArray();
    if (File.Exists(helpPath) &&
        File.ReadAllBytes(helpPath).AsSpan().SequenceEqual(embeddedBytes))
      return;

    var temporaryPath = helpPath + vToolsCommand.TemporaryFileSuffix;
    try
    {
      File.WriteAllBytes(temporaryPath, embeddedBytes);
      File.Move(temporaryPath, helpPath, overwrite: true);
    }
    finally
    {
      if (File.Exists(temporaryPath))
        File.Delete(temporaryPath);
    }
  }
}
