using System;
using System.IO;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Configures and toggles an audible notification when a Rhino command fails.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vCommandFailSound : vToolsCommand
{
  // Defaults
  internal const bool DefaultEnabled = true; // true plays a sound after command failure; false stays silent.
  internal const int DefaultSoundIndex = 0; // Zero-based SoundNames index.
  internal const string DefaultAudioFile = ""; // Relative or absolute audio path; empty uses the selected system sound.

  public override string EnglishName => "vCommandFailSound";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    CommandFailSoundMonitor.EnsureSettingsLoaded();

    while (true)
    {
      using var getter = new GetOption();
      getter.EnableTransparentCommands(true);
      getter.AcceptNothing(true);
      getter.SetCommandPrompt(CommandFailSoundMonitor.GetPrompt());

      var enabledOption = new OptionToggle(
        CommandFailSoundMonitor.Enabled,
        "No",
        "Yes");
      var enabledOptionIndex = getter.AddOptionToggle("Enabled", ref enabledOption);
      var soundOptionIndex = getter.AddOptionList(
        "Sound",
        CommandFailSoundMonitor.SoundNames,
        CommandFailSoundMonitor.SoundIndex);
      var audioFileOptionIndex = getter.AddOption("AudioFile");
      var previewOptionIndex = getter.AddOption("Preview");

      var getResult = getter.Get();
      if (getResult == GetResult.Cancel)
        return Result.Cancel;

      if (getResult == GetResult.Nothing)
        return Result.Success;

      if (getResult != GetResult.Option || getter.Option() == null)
        return getter.CommandResult();

      var option = getter.Option()!;
      if (option.Index == enabledOptionIndex)
      {
        CommandFailSoundMonitor.SetEnabled(enabledOption.CurrentValue);
      }
      else if (option.Index == soundOptionIndex)
      {
        CommandFailSoundMonitor.SetSound(option.CurrentListOptionIndex);
      }
      else if (option.Index == audioFileOptionIndex)
      {
        if (TryChooseAudioFile(CommandFailSoundMonitor.AudioFile, out var audioFile))
          CommandFailSoundMonitor.SetAudioFile(audioFile);
      }
      else if (option.Index == previewOptionIndex)
      {
        CommandFailSoundMonitor.Preview();
      }
    }
  }

  private static bool TryChooseAudioFile(string currentPath, out string audioFile)
  {
    audioFile = string.Empty;

    var dialog = new OpenFileDialog
    {
      Title = "Select command failure sound",
      Filter =
        "Audio files (*.wav;*.mp3;*.wma;*.m4a)|*.wav;*.mp3;*.wma;*.m4a|" +
        "All files (*.*)|*.*",
      DefaultExt = "wav",
      MultiSelect = false
    };

    if (!string.IsNullOrWhiteSpace(currentPath))
    {
      try
      {
        var fullPath = Path.GetFullPath(currentPath);
        dialog.FileName = fullPath;
        dialog.InitialDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
      }
      catch
      {
      }
    }

    if (!dialog.ShowOpenDialog() || string.IsNullOrWhiteSpace(dialog.FileName))
      return false;

    audioFile = Path.GetFullPath(dialog.FileName);
    return true;
  }
}
