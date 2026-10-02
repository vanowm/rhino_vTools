using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using Rhino.UI.DialogPanels;

namespace vTools.Commands;

/// <summary>
/// Runs a delegated command with a temporary selection filter and optional current layer.
/// </summary>
[CommandStyle(Style.Transparent)]
public sealed class vFilterExec : vToolsCommand
{
  // Defaults
  private const string DefaultFilter = "Curves"; // Canonical filter name or supported filter expression.
  private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Existing full layer path; *Current*, . or * leaves the current layer unchanged.

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
  private static EventHandler? _startIdleHandler;
  private static EventHandler? _repeatIdleHandler;
  private static bool _registeringRepeat;
  private static string? _repeatHelpUrl;

  public override string EnglishName => "vFilterExec";

  internal static string RepeatCommandHelpUrl =>
    string.IsNullOrWhiteSpace(_repeatHelpUrl)
      ? CommandHelpUrl.ForCommand(Tag)
      : _repeatHelpUrl;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    CancelPendingLaunch();
    CancelRepeatRegistration();

    var layerName = DefaultLayer;
    if (!TryGetCommand(doc, ref layerName, out var command, out var commandResult))
      return commandResult;

    if (!TryGetFilter(doc, ref layerName, out var filter, out var filterResult))
      return filterResult;

    QueueLaunch(new PendingLaunch(command, filter, layerName, doc.RuntimeSerialNumber));
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
    var doc = RhinoDoc.ActiveDoc;
    if (launch == null || doc == null)
      return Result.Nothing;

    CancelPendingLaunch();
    CancelRepeatRegistration();
    Log.Write(Tag,
      $"repeat command={launch.Command} filter={launch.Filter.CanonicalSpec} layer={launch.LayerName}");
    QueueLaunch(launch with { DocumentSerialNumber = doc.RuntimeSerialNumber });
    return Result.Success;
  }

  private static bool TryGetCommand(
    RhinoDoc doc,
    ref string layerName,
    out string command,
    out Result commandResult)
  {
    command = string.Empty;
    using var getter = new GetString();
    getter.SetCommandPrompt("Command to execute");
    getter.AcceptNothing(false);
    getter.EnableTransparentCommands(true);

    while (true)
    {
      getter.ClearCommandOptions();
      var layerOption = getter.AddOption("Layer", layerName);
      var result = getter.Get();
      commandResult = getter.CommandResult();
      if (commandResult != Result.Success)
        return false;

      if (result == GetResult.Option && getter.Option().Index == layerOption)
      {
        if (!TryGetLayer(doc, ref layerName))
        {
          commandResult = Result.Cancel;
          return false;
        }
        continue;
      }

      command = result == GetResult.String
        ? NormalizeInput(getter.StringResult())
        : string.Empty;

      if (!string.IsNullOrWhiteSpace(command))
        return true;

      RhinoApp.WriteLine("vFilterExec: enter a command to execute.");
      commandResult = Result.Nothing;
      return false;
    }
  }

  private static bool TryGetFilter(
    RhinoDoc doc,
    ref string layerName,
    out FilterSelection selection,
    out Result commandResult)
  {
    selection = default;
    using var getter = new GetString();
    getter.SetCommandPrompt("Selection filter");
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);
    getter.SetDefaultString(DefaultFilter);

    while (true)
    {
      getter.ClearCommandOptions();
      var layerOption = getter.AddOption("Layer", layerName);
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

      if (result == GetResult.Option && getter.Option().Index == layerOption)
      {
        if (!TryGetLayer(doc, ref layerName))
        {
          commandResult = Result.Cancel;
          return false;
        }
        continue;
      }

      var filterSpec = result switch
      {
        GetResult.Nothing => DefaultFilter,
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
  }

  private static bool TryGetLayer(RhinoDoc doc, ref string layerName)
  {
    using var getter = new GetString();
    getter.SetCommandPrompt("Temporary layer name (. or * = current layer)");
    getter.SetDefaultString(layerName);
    getter.AcceptNothing(true);
    getter.EnableTransparentCommands(true);

    while (true)
    {
      // Consume one macro argument even when the alias runs as Interactive.
      var result = getter.Get();
      if (getter.CommandResult() != Result.Success)
        return false;

      var requested = result == GetResult.Nothing
        ? layerName
        : NormalizeInput(getter.StringResult());
      if (LayerSelector.TryResolveManualValue(
            doc, requested, DefaultLayer, allowNewLayer: false, specialChoices: [],
            out var selectedLayer, out var error))
      {
        layerName = selectedLayer;
        return true;
      }

      RhinoApp.WriteLine(error);
    }
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

    try
    {
      CompleteActiveExecution(false, "replaced by another launch");
      var doc = RhinoDoc.FromRuntimeSerialNumber(launch.DocumentSerialNumber);
      if (doc == null)
        throw new InvalidOperationException("The command's document is no longer open.");

      var previousState = SelectionFilterSettings.GetCurrentState();
      var temporaryState = SelectionFilterSettings.GetCurrentState();
      temporaryState.GlobalGeometryFilter = launch.Filter.Mask;
      temporaryState.OneShotGeometryFilter = ObjectType.None;
      temporaryState.Enabled = true;
      temporaryState.SubObjectSelect = launch.Filter.RequiresSubObjects;
      _activeExecution = new ActiveExecution(previousState, doc.RuntimeSerialNumber);
      ApplyLayer(doc, launch.LayerName, _activeExecution);
      SelectionFilterSettings.UpdateFromState(temporaryState);

      Command.BeginCommand += OnDelegatedCommandBegin;
      Command.EndCommand += OnDelegatedCommandEnd;

      Log.Write(Tag, $"launch command={launch.Command} filter={launch.Filter.CanonicalSpec} layer={launch.LayerName}");
      var accepted = RhinoApp.RunScript(doc.RuntimeSerialNumber, launch.Command, false);

      // Idle launches can return before BeginCommand; keep the temporary state until EndCommand.
      if (_activeExecution is { HasStarted: false })
      {
        if (!accepted)
          CompleteActiveExecution(true, "delegated command rejected");
        else
        {
          _startIdleHandler = OnCheckDelegatedStartOnIdle;
          RhinoApp.Idle += _startIdleHandler;
        }
      }
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"launch failed: {ex.Message}");
      RhinoApp.WriteLine($"vFilterExec: {ex.Message}");
      if (_activeExecution != null)
        CompleteActiveExecution(true, "launch failed");
      else
        QueueRepeatRegistration();
    }
  }

  private static void OnCheckDelegatedStartOnIdle(object? sender, EventArgs e)
  {
    if (Command.InCommand())
      return;

    CompleteActiveExecution(true, "delegated command did not start");
  }

  private static void OnDelegatedCommandBegin(object? sender, CommandEventArgs e)
  {
    var execution = _activeExecution;
    if (execution == null || execution.HasStarted ||
        execution.DocumentSerialNumber != e.Document.RuntimeSerialNumber)
      return;

    execution.HasStarted = true;
    execution.CommandId = e.CommandId;
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
    if (_startIdleHandler != null)
      RhinoApp.Idle -= _startIdleHandler;
    _startIdleHandler = null;
    Command.BeginCommand -= OnDelegatedCommandBegin;
    Command.EndCommand -= OnDelegatedCommandEnd;
    RestoreLayer(execution);
    RestoreFilter(execution.PreviousState);
    RestoreSelectedPanels(execution.SelectedPanels);
    Log.Write(Tag, $"filter restored reason={reason}");

    if (execution.HasStarted)
    {
      try
      {
        _repeatHelpUrl = Command.GetCommandContextHelpUrl(execution.CommandId);
        if (string.IsNullOrWhiteSpace(_repeatHelpUrl))
          _repeatHelpUrl = CommandHelpPanel.Instance?.HelpUrl;
        Log.Write(Tag, $"repeat help command={execution.CommandId} url={_repeatHelpUrl ?? "<none>"}");
      }
      catch (Exception ex)
      {
        _repeatHelpUrl = CommandHelpPanel.Instance?.HelpUrl;
        Log.Write(Tag, $"repeat help capture failed: {ex.Message}");
      }
    }

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

  private static SelectedPanel[] CaptureSelectedPanels()
  {
    try
    {
      return Panels.GetOpenPanelIds()
        .Where(id => Panels.IsPanelVisible(id, isSelectedTab: true))
        .Select(id => new SelectedPanel(id, Panels.PanelDockBar(id)))
        .ToArray();
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"panel capture failed: {ex.Message}");
      return [];
    }
  }

  private static void RestoreSelectedPanels(IEnumerable<SelectedPanel> selectedPanels)
  {
    foreach (var panel in selectedPanels)
    {
      try
      {
        // Do not reopen closed panels or move tabs the user rearranged during the command.
        if (!Panels.IsPanelVisible(panel.PanelId) || Panels.IsPanelVisible(panel.PanelId, isSelectedTab: true) ||
            Panels.PanelDockBar(panel.PanelId) != panel.DockBarId)
          continue;

        if (panel.DockBarId == Guid.Empty)
          Panels.OpenPanel(panel.PanelId);
        else if (Panels.OpenPanel(panel.DockBarId, panel.PanelId, makeSelectedPanel: true) == Guid.Empty)
          throw new InvalidOperationException($"Could not reactivate panel '{panel.PanelId}'.");
      }
      catch (Exception ex)
      {
        Log.Write(Tag, $"panel restore failed: {ex.Message}");
      }
    }
  }

  private static void ApplyLayer(RhinoDoc doc, string layerName, ActiveExecution execution)
  {
    if (LayerSelector.IsCurrentLayerValue(layerName, DefaultLayer))
      return;

    var index = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
    if (index < 0 || index >= doc.Layers.Count || doc.Layers[index].IsDeleted)
      throw new InvalidOperationException($"Layer '{layerName}' was not found.");

    var layer = doc.Layers[index];
    var layerLocks = new List<LayerLockState>();
    for (var ancestor = layer; ancestor != null; ancestor = doc.Layers.FindId(ancestor.ParentLayerId))
    {
      if (ancestor.IsDeleted || !ancestor.IsVisible || ancestor.IsReference)
        throw new InvalidOperationException($"Layer '{ancestor.FullPath}' must be visible and editable.");

      if (ancestor.IsLocked || ancestor.GetPersistentLocking())
        layerLocks.Add(new(ancestor.Id, ancestor.IsLocked, ancestor.GetPersistentLocking()));
      if (ancestor.ParentLayerId == Guid.Empty)
        break;
    }

    execution.PreviousLayerId = doc.Layers.CurrentLayer.Id;
    execution.LayerLocks.AddRange(layerLocks);
    // Unlock parents first so inherited locks clear before making the destination current.
    foreach (var state in layerLocks.AsEnumerable().Reverse())
    {
      var unlocked = doc.Layers.FindId(state.LayerId)!;
      unlocked.IsLocked = false;
      unlocked.SetPersistentLocking(false);
      if (!doc.Layers.Modify(unlocked, unlocked.Index, quiet: true))
        throw new InvalidOperationException($"Could not unlock layer '{unlocked.FullPath}'.");
    }
    if (!doc.Layers.SetCurrentLayerIndex(index, quiet: true))
      throw new InvalidOperationException($"Could not make layer '{layerName}' current.");

    Log.Write(Tag, $"layer switched document={doc.RuntimeSerialNumber} previous={execution.PreviousLayerId} target={layer.FullPath}");
  }

  private static void RestoreLayer(ActiveExecution execution)
  {
    if (execution.PreviousLayerId == Guid.Empty)
      return;

    var doc = RhinoDoc.FromRuntimeSerialNumber(execution.DocumentSerialNumber);
    if (doc == null)
    {
      Log.Write(Tag, $"layer restore skipped; document={execution.DocumentSerialNumber} closed");
      return;
    }

    try
    {
      var previousLayer = doc.Layers.FindId(execution.PreviousLayerId);
      if (previousLayer == null || previousLayer.IsDeleted ||
          !doc.Layers.SetCurrentLayerIndex(previousLayer.Index, quiet: true))
      {
        throw new InvalidOperationException("Could not restore the previous current layer.");
      }

      Log.Write(Tag, $"layer restored document={execution.DocumentSerialNumber} layer={previousLayer.FullPath}");
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"layer restore failed: {ex.Message}");
      RhinoApp.WriteLine($"vFilterExec: {ex.Message}");
    }

    // Restore children before parents, preserving how they behave when a parent is later unlocked.
    foreach (var state in execution.LayerLocks)
    {
      try
      {
        var layer = doc.Layers.FindId(state.LayerId);
        if (layer == null || layer.IsDeleted)
          continue;
        layer.IsLocked = state.IsLocked;
        layer.SetPersistentLocking(state.PersistentLocking);
        if (!doc.Layers.Modify(layer, layer.Index, quiet: true))
          throw new InvalidOperationException($"Could not restore the lock on layer '{layer.FullPath}'.");
      }
      catch (Exception ex)
      {
        Log.Write(Tag, $"layer lock restore failed: {ex.Message}");
        RhinoApp.WriteLine($"vFilterExec: {ex.Message}");
      }
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

  private sealed record PendingLaunch(
    string Command,
    FilterSelection Filter,
    string LayerName,
    uint DocumentSerialNumber);

  private readonly record struct LayerLockState(Guid LayerId, bool IsLocked, bool PersistentLocking);
  private readonly record struct SelectedPanel(Guid PanelId, Guid DockBarId);

  private sealed class ActiveExecution
  {
    public ActiveExecution(SelectionFilterSettingsState previousState, uint documentSerialNumber)
    {
      PreviousState = previousState;
      DocumentSerialNumber = documentSerialNumber;
    }

    public SelectionFilterSettingsState PreviousState { get; }
    public SelectedPanel[] SelectedPanels { get; } = CaptureSelectedPanels();
    public bool HasStarted { get; set; }
    public Guid CommandId { get; set; }
    public uint DocumentSerialNumber { get; }
    public Guid PreviousLayerId { get; set; }
    public List<LayerLockState> LayerLocks { get; } = new();
  }

  private readonly record struct FilterSelection(
    ObjectType Mask,
    bool RequiresSubObjects,
    string CanonicalSpec);
}
