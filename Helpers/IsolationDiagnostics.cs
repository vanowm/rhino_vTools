using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using vTools.Commands;
using Environment = System.Environment;

namespace vTools;

/// <summary>
/// Read-only command and input tracing for the native Isolate regression.
/// </summary>
internal static class IsolationDiagnostics
{
  // Defaults and customizable constants
  private const bool DefaultEnabled = true; // true records the temporary investigation trace; false disables all its event subscriptions.
  private const string TraceFileName = "vTools.commands.jsonl"; // Append-only JSON Lines filename beside the loaded DLL; preserved across restarts.
  private const int ReviewSessionCount = 5; // Tested clean sessions required before diagnostics can be reviewed for removal; positive integer.
  private const int InputSampleIntervalMilliseconds = 500; // Minimum milliseconds between changed input-state samples during commands; positive integer.
  private const int IsolationSettleMilliseconds = 300; // Quiet milliseconds after command completion before checking Isolate's retained selection.
  private const int ObjectSampleLimit = 32; // Maximum object IDs included in ordinary event samples; Isolate checks use the full retained selection.
  private const int HistoryTailCharacters = 8192; // Command-history suffix retained to find newly printed text; positive character count.
  private const int HistoryChunkCharacters = 65536; // Maximum command-history characters emitted in one event; older excess is marked as truncated.

  private const string SettingsSection = "IsolationDiagnostics";
  private static readonly FieldInfo? ActivePointGetter = typeof(GetPoint).GetField("m_active_gp", BindingFlags.Static | BindingFlags.NonPublic);
  private static readonly FieldInfo? ActiveObjectGetter = typeof(GetObject).GetField("g_active_go", BindingFlags.Static | BindingFlags.NonPublic);
  private static readonly PropertyInfo? EventWatcher = typeof(RhinoApp).GetProperty("InEventWatcher", BindingFlags.Static | BindingFlags.NonPublic);
  private static readonly List<CommandFrame> Commands = [];
  private static readonly List<IsolationCheck> PendingChecks = [];
  private static readonly Dictionary<string, int> Changes = [];
  private static readonly HashSet<Guid> ChangedObjects = [];
  private static bool _started;
  private static bool _idlePending;
  private static bool _traceHadErrors;
  private static Guid _session;
  private static long _lastSampleTick;
  private static long _lastCommandEndTick;
  private static string _historyTail = string.Empty;
  private static string _lastInputState = string.Empty;
  private static int _passed;
  private static int _failed;
  private static int _unverified;
  private static IsolationCheck? _isolationInput;

  internal static void Start(string version)
  {
    if (_started)
      return;
    var enabled = ToolsOptionStore.Read(SettingsSection, section =>
      ToolsOptionStore.TryGetBool(section, "enabled", out var value) ? value : DefaultEnabled);
    if (!enabled)
      return;

    _session = Guid.NewGuid();
    _passed = _failed = _unverified = 0;
    _traceHadErrors = false;
    var prior = ReadSessions();
    if (!Trace("session-start", new
    {
      rhino = RhinoApp.Version.ToString(), plugin = version, process = Environment.ProcessId,
      sessionNumber = prior.Started + 1, cleanTestedSessions = prior.Clean, reviewAfter = ReviewSessionCount
    }))
    {
      Log.Write("IsolationDiagnostics", "Cannot write the command trace; monitoring not started.");
      return;
    }

    _started = true;
    _historyTail = Tail(RhinoApp.CommandHistoryWindowText ?? string.Empty, HistoryTailCharacters);
    _idlePending = true;
    Command.BeginCommand += OnBeginCommand;
    Command.EndCommand += OnEndCommand;
    Command.UndoRedo += OnUndoRedo;
    RhinoDoc.SelectObjects += OnSelection;
    RhinoDoc.DeselectObjects += OnSelection;
    RhinoDoc.DeselectAllObjects += OnDeselectAll;
    RhinoDoc.AddRhinoObject += OnAdd;
    RhinoDoc.DeleteRhinoObject += OnDelete;
    RhinoDoc.ReplaceRhinoObject += OnReplace;
    RhinoDoc.ModifyObjectAttributes += OnAttributes;
    RhinoDoc.ActiveDocumentChanged += OnDocumentChanged;
    RhinoApp.Idle += OnIdle;
    RhinoApp.EscapeKeyPressed += OnEscape;
    Log.Write("IsolationDiagnostics", "Command trace={0}; tested clean sessions={1}/{2}",
      PluginPaths.ResolveFile(TraceFileName), prior.Clean, ReviewSessionCount);
  }

  internal static void Stop()
  {
    if (!_started)
      return;
    _started = false;
    Command.BeginCommand -= OnBeginCommand;
    Command.EndCommand -= OnEndCommand;
    Command.UndoRedo -= OnUndoRedo;
    RhinoDoc.SelectObjects -= OnSelection;
    RhinoDoc.DeselectObjects -= OnSelection;
    RhinoDoc.DeselectAllObjects -= OnDeselectAll;
    RhinoDoc.AddRhinoObject -= OnAdd;
    RhinoDoc.DeleteRhinoObject -= OnDelete;
    RhinoDoc.ReplaceRhinoObject -= OnReplace;
    RhinoDoc.ModifyObjectAttributes -= OnAttributes;
    RhinoDoc.ActiveDocumentChanged -= OnDocumentChanged;
    RhinoApp.Idle -= OnIdle;
    RhinoApp.EscapeKeyPressed -= OnEscape;
    Watch(() =>
    {
      FlushChanges();
      FlushHistory();
      _unverified += PendingChecks.Count + (_isolationInput == null ? 0 : 1);
      var prior = ReadSessions();
      var clean = _failed > 0 || _traceHadErrors ? 0 : prior.Clean + (_passed > 0 ? 1 : 0);
      Trace("session-summary", new
      {
        passed = _passed, failed = _failed, unverified = _unverified,
        cleanTestedSessions = clean, reviewReady = clean >= ReviewSessionCount, traceComplete = !_traceHadErrors
      });
      Log.Write("IsolationDiagnostics", "Session checks passed={0} failed={1} unverified={2}; tested clean sessions={3}/{4}; review_ready={5}",
        _passed, _failed, _unverified, clean, ReviewSessionCount, clean >= ReviewSessionCount);
    });
    Commands.Clear();
    PendingChecks.Clear();
    Changes.Clear();
    ChangedObjects.Clear();
    _isolationInput = null;
    _lastInputState = string.Empty;
  }

  private static void OnBeginCommand(object? sender, CommandEventArgs e) => Watch(() =>
  {
    FlushChanges();
    foreach (var check in PendingChecks)
      if (!IsIsolationInternalCommand(e.CommandEnglishName))
        check.Interrupted = true;
    if (_isolationInput != null && IsIsolationInternalCommand(e.CommandEnglishName))
    {
      _isolationInput.Frozen = true;
      if (e.CommandEnglishName.Equals("Invert", StringComparison.OrdinalIgnoreCase))
        _isolationInput.InvertStarted = true;
    }
    if (e.CommandEnglishName.Equals("Isolate", StringComparison.OrdinalIgnoreCase))
      _isolationInput = new IsolationCheck(e.Document,
        e.Document.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id));
    Commands.Add(new CommandFrame(e.CommandId, e.DocumentRuntimeSerialNumber, Environment.TickCount64));
    Trace("command-begin", new
    {
      command = e.CommandEnglishName, local = e.CommandLocalName, plugin = e.CommandPluginName,
      id = e.CommandId, state = CaptureState(e.Document)
    });
    _idlePending = true;
  });

  private static void OnEndCommand(object? sender, CommandEventArgs e) => Watch(() =>
  {
    FlushChanges();
    var index = Commands.FindLastIndex(frame => frame.Id == e.CommandId && frame.DocSerial == e.DocumentRuntimeSerialNumber);
    var elapsed = index < 0 ? -1 : Environment.TickCount64 - Commands[index].Started;
    if (index >= 0)
      Commands.RemoveAt(index);
    if (_isolationInput != null && e.CommandEnglishName.Equals("Invert", StringComparison.OrdinalIgnoreCase))
      _isolationInput.InvertCompleted = e.CommandResult == Result.Success;
    if (_isolationInput != null && e.CommandEnglishName.Equals("Isolate", StringComparison.OrdinalIgnoreCase))
    {
      _isolationInput.Frozen = true;
      _isolationInput.Result = e.CommandResult;
      PendingChecks.Add(_isolationInput);
      _isolationInput = null;
    }
    // Inspect immediately after Hide so a quick Undo cannot erase the failure evidence.
    if (e.CommandEnglishName.Equals("Hide", StringComparison.OrdinalIgnoreCase))
    {
      foreach (var check in PendingChecks.Where(check => check.DocSerial == e.DocumentRuntimeSerialNumber).ToArray())
      {
        CheckIsolation(e.Document, check);
        PendingChecks.Remove(check);
      }
    }
    Trace("command-end", new
    {
      command = e.CommandEnglishName, result = e.CommandResult.ToString(), elapsedMilliseconds = elapsed,
      state = CaptureState(e.Document)
    });
    _lastCommandEndTick = Environment.TickCount64;
    _idlePending = true;
  });

  private static void OnUndoRedo(object? sender, UndoRedoEventArgs e) => Watch(() =>
  {
    Trace("undo-redo", new
    {
      serial = e.UndoSerialNumber, command = e.CommandId,
      beginRecord = e.IsBeginRecording, endRecord = e.IsEndRecording,
      beginUndo = e.IsBeginUndo, endUndo = e.IsEndUndo,
      beginRedo = e.IsBeginRedo, endRedo = e.IsEndRedo, purge = e.IsPurgeRecord,
      state = CaptureState(RhinoDoc.ActiveDoc)
    });
    _idlePending = true;
  });

  private static void OnSelection(object? sender, RhinoObjectSelectionEventArgs e) => Watch(() =>
  {
    foreach (var obj in e.RhinoObjects)
    {
      Change(e.Selected ? "selected" : "deselected", obj.Id);
      if (_isolationInput is { Frozen: false } check && check.DocSerial == e.Document.RuntimeSerialNumber)
      {
        if (e.Selected) check.Keep.Add(obj.Id);
        else check.Keep.Remove(obj.Id);
      }
    }
  });

  private static void OnDeselectAll(object? sender, RhinoDeselectAllObjectsEventArgs e) => Watch(() =>
  {
    Change("deselect-all", Guid.Empty, e.ObjectCount);
    if (_isolationInput is { Frozen: false } check && check.DocSerial == e.Document.RuntimeSerialNumber)
      check.Keep.Clear();
  });

  private static void OnAdd(object? sender, RhinoObjectEventArgs e) => Change("added", e.ObjectId);
  private static void OnDelete(object? sender, RhinoObjectEventArgs e) => Change("deleted", e.ObjectId);
  private static void OnReplace(object? sender, RhinoReplaceObjectEventArgs e) => Change("replaced", e.ObjectId);
  private static void OnAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e) => Watch(() =>
  {
    Change("attributes", e.RhinoObject.Id);
    if (e.OldAttributes.Mode != e.NewAttributes.Mode)
      Change($"mode-{e.OldAttributes.Mode}-to-{e.NewAttributes.Mode}", e.RhinoObject.Id);
    if (e.OldAttributes.LayerIndex != e.NewAttributes.LayerIndex)
      Change("layer-changed", e.RhinoObject.Id);
  });

  private static void OnDocumentChanged(object? sender, DocumentEventArgs e) => Watch(() =>
  {
    Trace("active-document", CaptureState(e.Document));
    _idlePending = true;
  });

  private static void OnEscape(object? sender, EventArgs e) => Watch(() => Trace("escape", CaptureState(RhinoDoc.ActiveDoc)));

  private static void OnIdle(object? sender, EventArgs e) => Watch(() =>
  {
    var now = Environment.TickCount64;
    if (now - _lastSampleTick < InputSampleIntervalMilliseconds)
      return;
    _lastSampleTick = now;
    if (!_idlePending && Commands.Count == 0 && PendingChecks.Count == 0)
      return;
    var doc = RhinoDoc.ActiveDoc;
    FlushChanges();
    FlushHistory();
    var state = CaptureState(doc);
    var fingerprint = JsonSerializer.Serialize(state);
    if (!string.Equals(fingerprint, _lastInputState, StringComparison.Ordinal))
    {
      Trace("input-state", state);
      _lastInputState = fingerprint;
    }
    if (doc != null && (doc.InCommand(false) != 0 || RhinoGet.InGet(doc)))
      return;
    if (now - _lastCommandEndTick < IsolationSettleMilliseconds)
      return;
    foreach (var check in PendingChecks)
      CheckIsolation(doc, check);
    PendingChecks.Clear();
    _idlePending = false;
  });

  private static void CheckIsolation(RhinoDoc? doc, IsolationCheck check)
  {
    if (doc == null || doc.RuntimeSerialNumber != check.DocSerial || check.Interrupted ||
        check.Result != Result.Success || check.Keep.Count == 0)
    {
      _unverified++;
      Trace("isolate-unverified", new { doc = check.DocSerial, result = check.Result.ToString(), check.Interrupted, retainedCount = check.Keep.Count });
      return;
    }
    var outcome = InspectIsolation(doc, check.Keep);
    if (outcome.Hidden.Length > 0)
      _failed++;
    else if (outcome.Missing.Length > 0 || !check.InvertCompleted)
      _unverified++;
    else
      _passed++;
    Trace("isolate-check", new
    {
      doc = check.DocSerial, retainedCount = check.Keep.Count, hidden = outcome.Hidden, missing = outcome.Missing,
      check.InvertStarted, check.InvertCompleted,
      verdict = outcome.Hidden.Length > 0 ? "FAILED-selected-objects-hidden" :
        outcome.Missing.Length > 0 || !check.InvertCompleted ? "unverified" : "retained-selection-visible",
      state = CaptureState(doc)
    });
    if (outcome.Hidden.Length > 0)
      Log.Write("IsolationDiagnostics", "Native Isolate hid {0} retained objects; full command trace={1}", outcome.Hidden.Length, PluginPaths.ResolveFile(TraceFileName));
  }

  private static IsolationOutcome InspectIsolation(RhinoDoc doc, IEnumerable<Guid> keep)
  {
    var hidden = new List<Guid>();
    var missing = new List<Guid>();
    foreach (var id in keep)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null || obj.IsDeleted) missing.Add(id);
      else if (obj.IsHidden) hidden.Add(id);
    }
    return new IsolationOutcome(hidden.ToArray(), missing.ToArray());
  }

  private static object CaptureState(RhinoDoc? doc)
  {
    if (doc == null)
      return new { doc = 0 };
    var selected = doc.Objects.GetSelectedObjects(false, false).ToArray();
    var filter = SelectionFilterSettings.GetCurrentState();
    return new
    {
      doc = doc.RuntimeSerialNumber, inGet = RhinoGet.InGet(doc), inGetPoint = doc.InGetPoint,
      appCommands = RhinoApp.InCommand, prompt = RhinoApp.CommandPrompt,
      regularCommands = doc.InCommand(true), allCommands = doc.InCommand(false), scriptRunner = Command.InScriptRunnerCommand(),
      commandStack = (Command.GetCommandStack() ?? []).Select(id => Command.LookupCommandName(id, true) ?? id.ToString()).ToArray(),
      managedPointGetter = ActivePointGetter?.GetValue(null)?.GetType().FullName,
      managedObjectGetter = ActiveObjectGetter?.GetValue(null)?.GetType().FullName,
      eventWatcher = EventWatcher?.GetValue(null), modifiers = System.Windows.Forms.Control.ModifierKeys.ToString(),
      undoEnabled = doc.UndoRecordingEnabled, recording = doc.UndoRecordingIsActive, undo = doc.UndoActive, redo = doc.RedoActive,
      undoRecord = doc.CurrentUndoRecordSerialNumber, nextUndoRecord = doc.NextUndoRecordSerialNumber,
      historyUpdate = HistorySettings.UpdateEnabled,
      selectedCount = selected.Length, selectedSample = selected.Take(ObjectSampleLimit).Select(obj => obj.Id).ToArray(),
      filterEnabled = filter.Enabled, filter = filter.GlobalGeometryFilter.ToString(), oneShotFilter = filter.OneShotGeometryFilter.ToString(),
      filterSubObjects = filter.SubObjectSelect, view = doc.Views.ActiveView?.ActiveViewport.Name,
      perspective = doc.Views.ActiveView?.ActiveViewport.IsPerspectiveProjection
    };
  }

  internal static void RecordNativeDispatch(RhinoDoc doc, string stage, string script, bool? accepted = null) => Watch(() =>
  {
    if (_started)
      Trace("native-dispatch", new { stage, script, accepted, thread = Environment.CurrentManagedThreadId, state = CaptureState(doc) });
  });

  private static void Change(string kind, Guid id, int count = 1)
  {
    if (!_started)
      return;
    Changes[kind] = Changes.GetValueOrDefault(kind) + count;
    if (id != Guid.Empty && ChangedObjects.Count < ObjectSampleLimit)
      ChangedObjects.Add(id);
    _idlePending = true;
  }

  private static void FlushChanges()
  {
    if (Changes.Count == 0)
      return;
    Trace("object-changes", new { counts = Changes.ToDictionary(pair => pair.Key, pair => pair.Value), objectSample = ChangedObjects.ToArray() });
    Changes.Clear();
    ChangedObjects.Clear();
  }

  private static void FlushHistory()
  {
    var history = RhinoApp.CommandHistoryWindowText ?? string.Empty;
    var index = string.IsNullOrEmpty(_historyTail) ? 0 : history.LastIndexOf(_historyTail, StringComparison.Ordinal);
    var delta = index < 0 ? history : history[(index + _historyTail.Length)..];
    if (!string.IsNullOrEmpty(delta))
      Trace("command-history", new { text = Tail(delta, HistoryChunkCharacters), reset = index < 0, truncated = delta.Length > HistoryChunkCharacters });
    _historyTail = Tail(history, HistoryTailCharacters);
  }

  private static SessionCounts ReadSessions()
  {
    try
    {
      var path = PluginPaths.ResolveFile(TraceFileName);
      return File.Exists(path) ? CountSessions(File.ReadLines(path), _session.ToString()) : new SessionCounts(0, 0);
    }
    catch (Exception ex)
    {
      _traceHadErrors = true;
      Log.Write("IsolationDiagnostics", "Cannot read earlier command-trace sessions: {0}", ex.Message);
      return new SessionCounts(0, 0);
    }
  }

  private static SessionCounts CountSessions(IEnumerable<string> lines, string activeSession)
  {
    var starts = new List<string>();
    var summaries = new Dictionary<string, SessionSummary>();
    foreach (var line in lines)
    {
      try
      {
        using var json = JsonDocument.Parse(line);
        var root = json.RootElement;
        var session = root.GetProperty("session").GetString() ?? string.Empty;
        var kind = root.GetProperty("kind").GetString();
        if (kind == "session-start" && !starts.Contains(session)) starts.Add(session);
        if (kind != "session-summary" || summaries.ContainsKey(session)) continue;
        var data = root.GetProperty("data");
        summaries.Add(session, new SessionSummary(data.GetProperty("passed").GetInt32(),
          data.GetProperty("failed").GetInt32(),
          !data.TryGetProperty("traceComplete", out var complete) || complete.GetBoolean()));
      }
      catch (JsonException) { }
      catch (KeyNotFoundException) { }
      catch (InvalidOperationException) { }
      catch (FormatException) { }
    }
    var clean = 0;
    foreach (var session in starts)
    {
      if (!summaries.TryGetValue(session, out var summary))
      {
        if (session != activeSession) clean = 0;
        continue;
      }
      if (summary.Failed > 0 || !summary.Complete) clean = 0;
      else if (summary.Passed > 0 && summary.Complete) clean++;
    }
    return new SessionCounts(starts.Count, clean);
  }

  private static bool Trace(string kind, object data)
  {
    var written = Log.WriteDiagnosticJson(TraceFileName, new { time = DateTimeOffset.Now, session = _session, kind, data });
    if (!written)
      _traceHadErrors = true;
    return written;
  }

  private static string Tail(string value, int count) => value.Length > count ? value[^count..] : value;
  private static bool IsIsolationInternalCommand(string name) =>
    name.Equals("Invert", StringComparison.OrdinalIgnoreCase) || name.Equals("Hide", StringComparison.OrdinalIgnoreCase);

  private static void Watch(Action action)
  {
    try { action(); }
    catch (Exception ex)
    {
      _traceHadErrors = true;
      Trace("trace-error", new { error = ex.Message });
    }
  }

  private sealed record CommandFrame(Guid Id, uint DocSerial, long Started);
  private sealed record SessionCounts(int Started, int Clean);
  private sealed record SessionSummary(int Passed, int Failed, bool Complete);
  private sealed record IsolationOutcome(Guid[] Hidden, Guid[] Missing);
  private sealed class IsolationCheck(RhinoDoc doc, IEnumerable<Guid> keep)
  {
    internal uint DocSerial { get; } = doc.RuntimeSerialNumber;
    internal HashSet<Guid> Keep { get; } = keep.ToHashSet();
    internal bool Frozen;
    internal bool Interrupted;
    internal bool InvertStarted;
    internal bool InvertCompleted;
    internal Result Result = Result.Nothing;
  }
}
