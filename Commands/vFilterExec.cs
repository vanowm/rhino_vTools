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
[CommandStyle(Style.Transparent | Style.ScriptRunner | Style.NotUndoable)]
public sealed class vFilterExec : vToolsCommand
{
  // Defaults
  private const string DefaultFilter = "Curves"; // Canonical filter name or supported filter expression.
  private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Existing full layer path; *Current*, . or * leaves the current layer unchanged.
  private const int InvocationHistoryTailCharacters = 4096; // Maximum trailing history characters inspected for the alias typed at an interrupted prompt.
  private static readonly string[] BackgroundRepeatObservers = ["vHelp", "vPerpGumballProxy"]; // Known non-repeatable automatic updates allowed before alias registration; other new commands cancel it.

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

  private static PendingLaunch? _lastLaunch;
  private static ActiveExecution? _activeExecution;
  private static string? _repeatHelpUrl;
  private static RepeatInvocation? _pendingRepeat;
  private static RepeatInvocation? _registeringAlias;

  public override string EnglishName => "vFilterExec";

  protected override string CommandContextHelpUrl => RepeatCommandHelpUrl;

  internal static string RepeatCommandHelpUrl =>
    string.IsNullOrWhiteSpace(_repeatHelpUrl)
      ? CommandHelpUrl.ForCommand(Tag)
      : _repeatHelpUrl;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var registration = _registeringAlias;
    if (registration != null) _registeringAlias = null;
    using var quiet = CommandHistoryCleanup.Begin(Tag, registration != null, IsRegistrationHistoryLine);
    var alias = registration == null ? CaptureInvokingAlias() : null;
    Log.Write(Tag, "invocation alias={0} registration={1}", alias?.Name ?? "<not recorded>", registration != null);
    if (registration == null) _repeatHelpUrl = null;

    var layerName = DefaultLayer;
    if (!TryGetCommand(doc, ref layerName, out var command, out var commandResult))
      return commandResult;

    if (!TryGetFilter(doc, ref layerName, out var filter, out var filterResult))
      return filterResult;

    var launch = new PendingLaunch(command, filter, layerName, doc.RuntimeSerialNumber);
    if (registration != null)
    {
      registration.Consumed = registration.Launch == launch;
      Log.Write(Tag, "alias repeat consumed alias={0} matched={1}", registration.Alias.Name, registration.Consumed);
      return registration.Consumed ? Result.Success : Result.Failure;
    }
    return Launch(launch, alias);
  }

  internal static void StopPending()
  {
    CancelRepeatRegistration();
    _registeringAlias = null;
    CompleteActiveExecution("plug-in shutdown");
  }

  internal static Result RepeatLast()
  {
    var launch = _lastLaunch;
    var doc = RhinoDoc.ActiveDoc;
    if (launch == null || doc == null)
      return Result.Nothing;

    Log.Write(Tag,
      $"repeat command={launch.Command} filter={launch.Filter.CanonicalSpec} layer={launch.LayerName}");
    return Launch(launch with { DocumentSerialNumber = doc.RuntimeSerialNumber });
  }

  private static AliasInvocation? CaptureInvokingAlias()
  {
    try
    {
      string history = RhinoApp.CommandHistoryWindowText ?? string.Empty;
      string tail = history[Math.Max(0, history.Length - InvocationHistoryTailCharacters)..].TrimEnd('\r', '\n');
      string line = tail[(tail.LastIndexOf('\n') + 1)..].Trim();
      int separator = line.LastIndexOf(':');
      if (separator < 0) return null;
      string name = line[(separator + 1)..].Trim();
      if (name.Length == 0 || name.Any(char.IsWhiteSpace) || !CommandAliasList.IsAlias(name)) return null;
      string macro = CommandAliasList.GetMacro(name);
      return TryParseStandaloneAlias(macro, out _, out _, out _) ? new(name, macro) : null;
    }
    catch (Exception ex) { Log.Write(Tag, "alias repeat context unavailable: {0}", ex.Message); return null; }
  }

  private static bool TryParseStandaloneAlias(string macro, out string layer, out string command, out FilterSelection filter)
  {
    layer = DefaultLayer; command = string.Empty; filter = default;
    var tokens = new List<string>();
    for (int index = 0; index < macro.Length;)
    {
      while (index < macro.Length && char.IsWhiteSpace(macro[index])) index++;
      if (index == macro.Length) break;
      bool quoted = macro[index] == '"'; int start = quoted ? ++index : index;
      while (index < macro.Length && (quoted ? macro[index] != '"' : !char.IsWhiteSpace(macro[index]) && macro[index] != '"')) index++;
      if (quoted && index == macro.Length || !quoted && index < macro.Length && macro[index] == '"') return false;
      tokens.Add(macro[start..index]); if (quoted) index++;
      if (index < macro.Length && !char.IsWhiteSpace(macro[index])) return false;
    }
    int next = 0;
    if (tokens.Count > 0 && tokens[0].TrimStart('_').Equals("NoEcho", StringComparison.OrdinalIgnoreCase)) next++;
    while (next < tokens.Count && tokens[next] == "!") next++;
    if (next >= tokens.Count || !tokens[next++].TrimStart('!', '_', '-', '\'').Equals(Tag, StringComparison.OrdinalIgnoreCase)) return false;
    bool ReadLayer(out string? requested)
    {
      requested = null;
      if (next >= tokens.Count || !tokens[next].TrimStart('_').Equals("Layer", StringComparison.OrdinalIgnoreCase)) return true;
      if (++next >= tokens.Count) return false;
      requested = NormalizeLayer(tokens[next++]);
      return true;
    }
    if (!ReadLayer(out var firstLayer)) return false;
    if (firstLayer != null) layer = firstLayer;
    if (next >= tokens.Count) return false;
    command = tokens[next++];
    if (!ReadLayer(out var secondLayer)) return false;
    if (secondLayer != null) layer = secondLayer;
    return next == tokens.Count - 1 && TryParseFilter(tokens[next], out filter, out _);
  }

  private static string NormalizeLayer(string value) => value is "." or "*" ? DefaultLayer : value;
  private static bool AliasMatches(AliasInvocation alias, PendingLaunch launch) =>
    TryParseStandaloneAlias(alias.Macro, out var layer, out var command, out var filter) &&
    command.Equals(launch.Command, StringComparison.Ordinal) && filter == launch.Filter &&
    layer.Equals(launch.LayerName, StringComparison.OrdinalIgnoreCase);

  private static bool IsRegistrationHistoryLine(string line) =>
    line.TrimStart().StartsWith("Command to execute (", StringComparison.Ordinal) ||
    line.TrimStart().StartsWith("Selection filter <", StringComparison.Ordinal) ||
    line.TrimStart().StartsWith("Temporary layer name (", StringComparison.Ordinal);

  private static void QueueRepeatRegistration(AliasInvocation alias, PendingLaunch launch)
  {
    CancelRepeatRegistration();
    _pendingRepeat = new(alias, launch);
    Command.BeginCommand += CancelRepeatForNewCommand;
    RhinoApp.Idle += RegisterAliasOnIdle;
  }

  private static void CancelRepeatForNewCommand(object? sender, CommandEventArgs e)
  {
    if (BackgroundRepeatObservers.Contains(e.CommandEnglishName, StringComparer.OrdinalIgnoreCase)) return;
    Log.Write(Tag, "alias repeat registration abandoned for newer command={0}", e.CommandEnglishName);
    CancelRepeatRegistration();
  }
  private static void CancelRepeatRegistration()
  {
    RhinoApp.Idle -= RegisterAliasOnIdle;
    Command.BeginCommand -= CancelRepeatForNewCommand;
    _pendingRepeat = null;
  }

  private static void RegisterAliasOnIdle(object? sender, EventArgs e)
  {
    if (_pendingRepeat is not {} pending || Command.InCommand() || Command.InScriptRunnerCommand()) return;
    CancelRepeatRegistration();
    try
    {
      var doc = RhinoDoc.ActiveDoc;
      if (doc?.RuntimeSerialNumber != pending.Launch.DocumentSerialNumber ||
        !CommandAliasList.IsAlias(pending.Alias.Name) || CommandAliasList.GetMacro(pending.Alias.Name) != pending.Alias.Macro ||
        pending.Launch.LayerName != DefaultLayer && doc.Layers.FindByFullPath(pending.Launch.LayerName, RhinoMath.UnsetIntIndex) < 0)
      {Log.Write(Tag, "alias repeat registration abandoned: document, alias or layer changed alias={0}", pending.Alias.Name);return;}
      // No enclosing command may finish after this macro and replace its MRU entry.
      // Outside a ScriptRunner, RunScript queues the alias; retain the one-shot guard
      // until the public command consumes it, rather than clearing it on return.
      _registeringAlias = pending;
      bool queued = RhinoApp.RunScript(doc.RuntimeSerialNumber,
        pending.Alias.Name, pending.Alias.Name, false);
      if (!queued && ReferenceEquals(_registeringAlias, pending)) _registeringAlias = null;
      Log.Write(Tag, "alias repeat queued alias={0} accepted={1} consumed={2}", pending.Alias.Name, queued, pending.Consumed);
    }
    catch (Exception ex)
    {
      _registeringAlias = null;
      Log.Write(Tag, "alias repeat registration failed: {0}", ex);
    }
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

  private static Result Launch(PendingLaunch launch, AliasInvocation? alias = null)
  {
    _lastLaunch = launch;
    ActiveExecution? execution = null;
    try
    {
      CompleteActiveExecution("replaced by another launch");
      var doc = RhinoDoc.FromRuntimeSerialNumber(launch.DocumentSerialNumber);
      if (doc == null)
        throw new InvalidOperationException("The command's document is no longer open.");

      var previousState = SelectionFilterSettings.GetCurrentState();
      var temporaryState = SelectionFilterSettings.GetCurrentState();
      temporaryState.GlobalGeometryFilter = launch.Filter.Mask;
      temporaryState.OneShotGeometryFilter = ObjectType.None;
      temporaryState.Enabled = true;
      temporaryState.SubObjectSelect = launch.Filter.RequiresSubObjects;
      execution = new ActiveExecution(previousState, doc.RuntimeSerialNumber);
      _activeExecution = execution;
      ApplyLayer(doc, launch.LayerName, execution);
      SelectionFilterSettings.UpdateFromState(temporaryState);

      Command.BeginCommand += OnDelegatedCommandBegin;
      Command.EndCommand += OnDelegatedCommandEnd;

      Log.Write(Tag, $"launch command={launch.Command} filter={launch.Filter.CanonicalSpec} layer={launch.LayerName}");
      // Stay inside the caller's alias/macro until the delegate finishes; Rhino owns its repeat entry.
      var accepted = RhinoApp.RunScript(doc.RuntimeSerialNumber, launch.Command, false);
      return execution.Result ?? (accepted && !execution.HasStarted ? Result.Nothing : Result.Failure);
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"launch failed: {ex.Message}");
      RhinoApp.WriteLine($"vFilterExec: {ex.Message}");
      return Result.Failure;
    }
    finally
    {
      if (ReferenceEquals(_activeExecution, execution))
        CompleteActiveExecution("delegated call finished");
      if (execution?.HasStarted == true && alias != null && AliasMatches(alias, launch))
        QueueRepeatRegistration(alias, launch);
      else if (alias != null)
        Log.Write(Tag, "alias repeat registration skipped: invocation did not match alias={0}", alias.Name);
    }
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

    execution.Result = e.CommandResult;
    CompleteActiveExecution($"delegated end command={e.CommandEnglishName} result={e.CommandResult}");
  }

  private static void CompleteActiveExecution(string reason)
  {
    var execution = _activeExecution;
    if (execution == null)
      return;

    _activeExecution = null;
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

  private sealed record AliasInvocation(string Name, string Macro);
  private sealed record RepeatInvocation(AliasInvocation Alias, PendingLaunch Launch)
  {
    internal bool Consumed { get; set; } // true only after the registration pass consumes the exact original invocation; false never launches its delegate.
  }

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
    public Result? Result { get; set; }
    public uint DocumentSerialNumber { get; }
    public Guid PreviousLayerId { get; set; }
    public List<LayerLockState> LayerLocks { get; } = new();
  }

  private readonly record struct FilterSelection(
    ObjectType Mask,
    bool RequiresSubObjects,
    string CanonicalSpec);
}

[CommandStyle(Style.Hidden | Style.Transparent | Style.ScriptRunner | Style.DoNotRepeat | Style.NotUndoable)]
public sealed class vFilterExecRepeat : Command
{
  public override string EnglishName => "vFilterExecRepeat";

  protected override string CommandContextHelpUrl =>
    vFilterExec.RepeatCommandHelpUrl;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
    vFilterExec.RepeatLast();
}
