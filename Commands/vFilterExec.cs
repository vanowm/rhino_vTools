using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Runs a delegated command with a temporary global selection filter.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vFilterExec : Command
{
  private const string Tag = "vFilterExec";

  private static readonly FilterDefinition[] FilterDefinitions =
  [
    new("All", ObjectType.AnyObject),
    new("Points", ObjectType.Point, false, "Point"),
    new("PointClouds", ObjectType.PointSet, false, "PointCloud", "PointSets", "PointSet"),
    new("Curves", ObjectType.Curve, false, "Curve"),
    new("Surfaces", ObjectType.Surface, false, "Surface"),
    new("Polysurfaces", ObjectType.PolysrfFilter, false, "Polysurface", "Polysrfs", "Polysrf"),
    new("Meshes", ObjectType.Mesh, false, "Mesh"),
    new("SubDs", ObjectType.SubD, false, "SubD"),
    new("Extrusions", ObjectType.Extrusion, false, "Extrusion"),
    new("Annotations", ObjectType.Annotation | ObjectType.TextDot, false, "Annotation", "Text", "Dimensions"),
    new("Hatches", ObjectType.Hatch, false, "Hatch"),
    new("Blocks", ObjectType.InstanceReference, false, "Block", "Instances", "Instance"),
    new("Lights", ObjectType.Light, false, "Light"),
    new("Grips", ObjectType.Grip, true, "Grip", "ControlPoints", "ControlPoint"),
    new("Edges", ObjectType.EdgeFilter | ObjectType.MeshEdge, true, "Edge"),
    new("Faces", ObjectType.Surface | ObjectType.MeshFace, true, "Face"),
    new("Vertices", ObjectType.BrepVertex | ObjectType.MeshVertex, true, "Vertex")
  ];

  private static readonly Dictionary<string, FilterDefinition> FilterLookup = BuildFilterLookup();

  private static PendingLaunch? _pendingLaunch;
  private static PendingLaunch? _lastLaunch;
  private static ActiveExecution? _activeExecution;
  private static EventHandler? _launchIdleHandler;
  private static EventHandler? _repeatIdleHandler;
  private static bool _registeringRepeat;

  public override string EnglishName => "vFilterExec";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    CancelPendingLaunch();
    CancelRepeatRegistration();

    if (!TryGetCommand(out var command, out var commandResult))
      return commandResult;

    if (!TryGetFilter(out var filter, out var filterResult))
      return filterResult;

    QueueLaunch(new PendingLaunch(command, filter));
    return Result.Success;
  }

  internal static void StopPending()
  {
    CancelPendingLaunch();
    CancelRepeatRegistration();
    CompleteActiveExecution(false, "plug-in shutdown");
    _registeringRepeat = false;
  }

  internal static Result RepeatLast()
  {
    if (_registeringRepeat)
      return Result.Success;

    var launch = _lastLaunch;
    if (launch == null)
      return Result.Nothing;

    CancelPendingLaunch();
    CancelRepeatRegistration();
    Log.Write(Tag,
      $"repeat command={launch.Command} filter={launch.Filter.CanonicalSpec}");
    QueueLaunch(launch);
    return Result.Success;
  }

  private static bool TryGetCommand(
    out string command,
    out Result commandResult)
  {
    command = string.Empty;
    using var getter = new GetString();
    getter.SetCommandPrompt("Command to execute");
    getter.AcceptNothing(false);
    getter.EnableTransparentCommands(true);

    var result = getter.Get();
    commandResult = getter.CommandResult();
    if (commandResult != Result.Success)
      return false;

    command = result == GetResult.String
      ? NormalizeInput(getter.StringResult())
      : string.Empty;

    if (!string.IsNullOrWhiteSpace(command))
      return true;

    RhinoApp.WriteLine("vFilterExec: enter a command to execute.");
    commandResult = Result.Nothing;
    return false;
  }

  private static bool TryGetFilter(
    out FilterSelection selection,
    out Result commandResult)
  {
    selection = default;
    using var getter = new GetString();
    getter.SetCommandPrompt("Selection filter");
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);
    getter.SetDefaultString("Curves");

    var optionFilters = new Dictionary<int, string>();
    foreach (var definition in FilterDefinitions)
    {
      var optionIndex = getter.AddOption(definition.Name);
      if (optionIndex > 0)
        optionFilters[optionIndex] = definition.Name;
    }

    var result = getter.Get();
    commandResult = getter.CommandResult();
    if (commandResult != Result.Success)
      return false;

    var filterSpec = result switch
    {
      GetResult.Nothing => "Curves",
      GetResult.String => getter.StringResult(),
      GetResult.Option when optionFilters.TryGetValue(getter.Option().Index, out var optionFilter) => optionFilter,
      _ => string.Empty
    };

    if (TryParseFilter(filterSpec, out selection, out var invalidToken))
      return true;

    RhinoApp.WriteLine($"vFilterExec: unknown filter '{invalidToken}'.");
    commandResult = Result.Failure;
    return false;
  }

  private static bool TryParseFilter(
    string? filterSpec,
    out FilterSelection selection,
    out string invalidToken)
  {
    selection = default;
    invalidToken = string.Empty;
    var tokens = NormalizeInput(filterSpec)
      .Split([',', '+', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (tokens.Length == 0)
    {
      invalidToken = filterSpec ?? string.Empty;
      return false;
    }

    var names = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var mask = ObjectType.None;
    var requiresSubObjects = false;
    foreach (var token in tokens)
    {
      if (!FilterLookup.TryGetValue(NormalizeFilterToken(token), out var definition))
      {
        invalidToken = token;
        return false;
      }

      if (definition.Mask == ObjectType.AnyObject)
      {
        selection = new FilterSelection(ObjectType.AnyObject, false, "All");
        return true;
      }

      mask |= definition.Mask;
      requiresSubObjects |= definition.RequiresSubObjects;
      if (seen.Add(definition.Name))
        names.Add(definition.Name);
    }

    if (mask == ObjectType.None)
    {
      invalidToken = filterSpec ?? string.Empty;
      return false;
    }

    selection = new FilterSelection(mask, requiresSubObjects, string.Join(",", names));
    return true;
  }

  private static Dictionary<string, FilterDefinition> BuildFilterLookup()
  {
    var lookup = new Dictionary<string, FilterDefinition>(StringComparer.OrdinalIgnoreCase);
    foreach (var definition in FilterDefinitions)
    {
      lookup[NormalizeFilterToken(definition.Name)] = definition;
      foreach (var alias in definition.Aliases)
        lookup[NormalizeFilterToken(alias)] = definition;
    }
    return lookup;
  }

  private static string NormalizeFilterToken(string token)
    => new(token.Where(char.IsLetterOrDigit).ToArray());

  private static string NormalizeInput(string? input)
  {
    var value = (input ?? string.Empty).Trim();
    if (value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') ||
         (value[0] == '\'' && value[^1] == '\'')))
    {
      value = value[1..^1].Trim();
    }
    return value;
  }

  private static void QueueLaunch(PendingLaunch launch)
  {
    _lastLaunch = launch;
    _pendingLaunch = launch;
    _launchIdleHandler = OnLaunchOnIdle;
    RhinoApp.Idle += _launchIdleHandler;
  }

  private static void CancelPendingLaunch()
  {
    if (_launchIdleHandler != null)
      RhinoApp.Idle -= _launchIdleHandler;
    _launchIdleHandler = null;
    _pendingLaunch = null;
  }

  private static void OnLaunchOnIdle(object? sender, EventArgs e)
  {
    var launch = _pendingLaunch;
    CancelPendingLaunch();
    if (launch == null)
      return;

    SelectionFilterSettingsState? previousState = null;
    try
    {
      CompleteActiveExecution(false, "replaced by another launch");
      previousState = SelectionFilterSettings.GetCurrentState();
      var temporaryState = SelectionFilterSettings.GetCurrentState();
      temporaryState.GlobalGeometryFilter = launch.Filter.Mask;
      temporaryState.OneShotGeometryFilter = ObjectType.None;
      temporaryState.Enabled = true;
      temporaryState.SubObjectSelect = launch.Filter.RequiresSubObjects;
      SelectionFilterSettings.UpdateFromState(temporaryState);

      _activeExecution = new ActiveExecution(previousState);
      Command.BeginCommand += OnDelegatedCommandBegin;
      Command.EndCommand += OnDelegatedCommandEnd;

      Log.Write(Tag, $"launch command={launch.Command} filter={launch.Filter.CanonicalSpec}");
      _ = RhinoApp.RunScript(launch.Command, false);

      if (_activeExecution is { HasStarted: false })
        CompleteActiveExecution(true, "delegated command did not start");
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"launch failed: {ex.Message}");
      RhinoApp.WriteLine($"vFilterExec: {ex.Message}");
      if (_activeExecution != null)
        CompleteActiveExecution(true, "launch failed");
      else if (previousState != null)
      {
        RestoreFilter(previousState);
        QueueRepeatRegistration();
      }
      else
        QueueRepeatRegistration();
    }
  }

  private static void OnDelegatedCommandBegin(object? sender, CommandEventArgs e)
  {
    var execution = _activeExecution;
    if (execution == null || execution.HasStarted)
      return;

    execution.HasStarted = true;
    execution.CommandId = e.CommandId;
    execution.DocumentSerialNumber = e.Document.RuntimeSerialNumber;
    Log.Write(Tag,
      $"delegated begin command={e.CommandEnglishName} id={e.CommandId}");
  }

  private static void OnDelegatedCommandEnd(object? sender, CommandEventArgs e)
  {
    var execution = _activeExecution;
    if (execution == null ||
        !execution.HasStarted ||
        execution.CommandId != e.CommandId ||
        execution.DocumentSerialNumber != e.Document.RuntimeSerialNumber)
    {
      return;
    }

    CompleteActiveExecution(
      true,
      $"delegated end command={e.CommandEnglishName} result={e.CommandResult}");
  }

  private static void CompleteActiveExecution(bool queueRepeat, string reason)
  {
    var execution = _activeExecution;
    if (execution == null)
      return;

    _activeExecution = null;
    Command.BeginCommand -= OnDelegatedCommandBegin;
    Command.EndCommand -= OnDelegatedCommandEnd;
    RestoreFilter(execution.PreviousState);
    Log.Write(Tag, $"filter restored reason={reason}");

    if (queueRepeat)
      QueueRepeatRegistration();
  }

  private static void RestoreFilter(SelectionFilterSettingsState state)
  {
    try
    {
      SelectionFilterSettings.UpdateFromState(state);
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"filter restore failed: {ex.Message}");
      RhinoApp.WriteLine("vFilterExec: could not restore the previous selection filter.");
    }
  }

  private static void QueueRepeatRegistration()
  {
    CancelRepeatRegistration();
    _repeatIdleHandler = OnRegisterRepeatOnIdle;
    RhinoApp.Idle += _repeatIdleHandler;
  }

  private static void CancelRepeatRegistration()
  {
    if (_repeatIdleHandler != null)
      RhinoApp.Idle -= _repeatIdleHandler;
    _repeatIdleHandler = null;
  }

  private static void OnRegisterRepeatOnIdle(object? sender, EventArgs e)
  {
    if (Command.InCommand())
      return;

    CancelRepeatRegistration();
    _registeringRepeat = true;
    try
    {
      _ = RhinoApp.RunScript("_vFilterExecRepeat", false);
    }
    finally
    {
      _registeringRepeat = false;
    }
  }

  private sealed record FilterDefinition(
    string Name,
    ObjectType Mask,
    bool RequiresSubObjects = false,
    params string[] Aliases);

  private sealed record PendingLaunch(string Command, FilterSelection Filter);

  private sealed class ActiveExecution
  {
    public ActiveExecution(SelectionFilterSettingsState previousState)
    {
      PreviousState = previousState;
    }

    public SelectionFilterSettingsState PreviousState { get; }
    public bool HasStarted { get; set; }
    public Guid CommandId { get; set; }
    public uint DocumentSerialNumber { get; set; }
  }

  private readonly record struct FilterSelection(
    ObjectType Mask,
    bool RequiresSubObjects,
    string CanonicalSpec);
}

[CommandStyle(Style.Hidden | Style.Transparent | Style.NotUndoable)]
public sealed class vFilterExecRepeat : Command
{
  public override string EnglishName => "vFilterExecRepeat";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    vFilterExec.RepeatLast();
}
