using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using vTools.Commands;

namespace vTools;

/// <summary>
/// Observes lost history links without modifying geometry, selection, undo, or native warnings.
/// </summary>
internal static class HistoryBreakMonitor
{
  // Defaults and customizable constants
  private const bool DefaultEnabled = true; // true highlights broken history; false leaves all feedback to Rhino. Read at plug-in startup.
  private const int DoubleEscapeIntervalMilliseconds = 600; // Maximum milliseconds between two Esc presses to dismiss history highlights.
  private const int WarningCompletionIntervalMilliseconds = 50; // UI-thread polling interval in milliseconds while a completed command's history warning may still be open; positive integer.

  private const string SettingsSection = "HistoryHighlight";
  private const string Tag = "HistoryHighlight";
  private static readonly Dictionary<uint, DocumentState> Documents = [];
  private static readonly HashSet<uint> OpeningDocuments = [];
  private static PreviewDisplay.ObjectHighlighter? _highlight;
  private static Frame? _highlightFrame;
  private static System.Windows.Forms.Timer? _completionTimer;
  private static bool _started;
  private static long _lastEscapeTick;

  internal static void Start()
  {
    if (_started)
      return;

    var enabled = ToolsOptionStore.Read(SettingsSection, section =>
      ToolsOptionStore.TryGetBool(section, "enabled", out var value) ? value : DefaultEnabled);
    Log.Write(Tag, $"enabled={enabled}");
    if (!enabled)
      return;

    _started = true;
    Command.BeginCommand += OnBeginCommand;
    Command.EndCommand += OnEndCommand;
    Command.UndoRedo += OnUndoRedo;
    RhinoDoc.SelectObjects += OnSelectObjects;
    RhinoDoc.ReplaceRhinoObject += OnReplaceObject;
    RhinoDoc.DeleteRhinoObject += OnDeleteObject;
    RhinoDoc.AddRhinoObject += OnAddObject;
    RhinoDoc.UndeleteRhinoObject += OnAddObject;
    RhinoDoc.BeginOpenDocument += OnBeginOpen;
    RhinoDoc.EndOpenDocument += OnEndOpen;
    RhinoDoc.CloseDocument += OnCloseDocument;
    RhinoDoc.ActiveDocumentChanged += OnActiveDocumentChanged;
    RhinoApp.Idle += OnIdle;
    RhinoApp.EscapeKeyPressed += OnEscape;
    Watch(() => WarmIndex(RhinoDoc.ActiveDoc));
  }

  internal static void Stop()
  {
    if (!_started)
      return;

    _started = false;
    Command.BeginCommand -= OnBeginCommand;
    Command.EndCommand -= OnEndCommand;
    Command.UndoRedo -= OnUndoRedo;
    RhinoDoc.SelectObjects -= OnSelectObjects;
    RhinoDoc.ReplaceRhinoObject -= OnReplaceObject;
    RhinoDoc.DeleteRhinoObject -= OnDeleteObject;
    RhinoDoc.AddRhinoObject -= OnAddObject;
    RhinoDoc.UndeleteRhinoObject -= OnAddObject;
    RhinoDoc.BeginOpenDocument -= OnBeginOpen;
    RhinoDoc.EndOpenDocument -= OnEndOpen;
    RhinoDoc.CloseDocument -= OnCloseDocument;
    RhinoDoc.ActiveDocumentChanged -= OnActiveDocumentChanged;
    RhinoApp.Idle -= OnIdle;
    RhinoApp.EscapeKeyPressed -= OnEscape;
    ClearHighlight();
    foreach (var state in Documents.Values)
      state.Frame?.Dispose();
    Documents.Clear();
    OpeningDocuments.Clear();
  }

  private static void OnBeginCommand(object? sender, CommandEventArgs e) => Watch(() =>
  {
    var state = GetState(e.Document);
    if (state == null)
    {
      ClearHighlight();
      return;
    }

    if (IsUndoRedo(e.CommandEnglishName))
    {
      ClearHighlight();
      state.IndexDirty = true;
      return;
    }

    if (state.Frame == null || state.Frame.Completed)
    {
      Reconcile(state);
      ClearHighlight();
      state.Frame?.Dispose();
      state.EnsureIndex();
      state.Frame = new Frame(e.CommandEnglishName, state.NativeRecordCount);
    }

    state.Frame.Commands.Add(e.CommandId);
    foreach (var obj in state.Doc.Objects.GetSelectedObjects(false, false))
      CaptureCandidates(state, obj.Id);
  });

  private static void OnEndCommand(object? sender, CommandEventArgs e) => Watch(() =>
  {
    if (!Documents.TryGetValue(e.Document.RuntimeSerialNumber, out var state)
      || state.Frame == null)
      return;

    var frame = state.Frame;
    var index = frame.Commands.LastIndexOf(e.CommandId);
    if (index < 0)
      return;

    frame.Commands.RemoveAt(index);
    if (frame.Commands.Count != 0)
      return;

    frame.Completed = true;
    Publish(state, "command-end");
    if (ReferenceEquals(_highlightFrame, frame))
    {
      Log.Write(Tag, $"command={frame.Name} completion pending modal={IsRhinoModalOpen()}");
      QueueCompletion();
    }
  });

  private static void OnSelectObjects(object? sender, RhinoObjectSelectionEventArgs e) => Watch(() =>
  {
    if (!Documents.TryGetValue(e.Document.RuntimeSerialNumber, out var state)
      || state.Frame == null || state.Frame.Completed)
      return;

    foreach (var obj in e.RhinoObjects)
      CaptureCandidates(state, obj.Id);
  });

  private static void OnReplaceObject(object? sender, RhinoReplaceObjectEventArgs e) => Watch(() =>
  {
    var state = GetEditState(e.Document);
    if (state == null)
      return;

    CaptureCandidates(state, e.ObjectId);
    state.Frame!.Changed.Add(e.ObjectId);
    state.Frame.Replacing.Add(e.ObjectId);
  });

  private static void OnDeleteObject(object? sender, RhinoObjectEventArgs e) => Watch(() =>
  {
    var state = GetEditState(e.TheObject?.Document);
    if (state == null)
      return;

    CaptureCandidates(state, e.ObjectId);
    var frame = state.Frame!;
    frame.Changed.Add(e.ObjectId);
    // Replace emits Delete/Add too, including ordinary history replay. It is not a deletion.
    if (frame.Replacing.Contains(e.ObjectId))
      return;

    if (frame.Before.ContainsKey(e.ObjectId) && !frame.DeletedGeometry.ContainsKey(e.ObjectId))
    {
      var geometry = e.TheObject?.Geometry?.Duplicate();
      if (geometry != null)
        frame.DeletedGeometry.Add(e.ObjectId, geometry);
    }

    Publish(state, "delete");
  });

  private static void OnAddObject(object? sender, RhinoObjectEventArgs e) => Watch(() =>
  {
    var state = GetEditState(e.TheObject?.Document);
    if (state == null)
      return;

    state.Frame!.Changed.Add(e.ObjectId);
    state.Frame.Replacing.Remove(e.ObjectId);
  });

  private static void OnUndoRedo(object? sender, UndoRedoEventArgs e) => Watch(() =>
  {
    if (!e.IsBeginUndo && !e.IsBeginRedo)
      return;

    ClearHighlight();
    if (RhinoDoc.ActiveDoc is { } doc && Documents.TryGetValue(doc.RuntimeSerialNumber, out var state))
    {
      state.IndexDirty = true;
      if (state.Frame != null)
        state.Frame.Dismissed = true;
    }
  });

  private static void OnIdle(object? sender, EventArgs e) => Watch(() =>
  {
    if (Command.InCommand())
      return;

    foreach (var state in Documents.Values)
    {
      if (state.Frame is { Commands.Count: 0 } frame)
      {
        frame.Completed = true;
        if (ReferenceEquals(_highlightFrame, frame))
          QueueCompletion();
        else
        {
          CompleteFrame(frame);
          Reconcile(state);
        }
      }
    }
  });

  private static void OnBeginOpen(object? sender, DocumentEventArgs e) => Watch(() =>
  {
    OpeningDocuments.Add(e.DocumentSerialNumber);
    RemoveDocument(e.DocumentSerialNumber);
  });

  private static void OnEndOpen(object? sender, DocumentEventArgs e) => Watch(() =>
  {
    OpeningDocuments.Remove(e.DocumentSerialNumber);
    WarmIndex(e.Document);
  });

  private static void OnCloseDocument(object? sender, DocumentEventArgs e) => Watch(() =>
  {
    OpeningDocuments.Remove(e.DocumentSerialNumber);
    RemoveDocument(e.DocumentSerialNumber);
  });

  private static void OnActiveDocumentChanged(object? sender, DocumentEventArgs e) => Watch(() =>
  {
    ClearHighlight();
    WarmIndex(e.Document);
  });

  private static void OnEscape(object? sender, EventArgs e) => Watch(() =>
  {
    if (_highlightFrame == null)
      return;

    var now = System.Environment.TickCount64;
    if (_lastEscapeTick != 0 && now - _lastEscapeTick <= DoubleEscapeIntervalMilliseconds)
      ClearHighlight();
    else
      _lastEscapeTick = now;
  });

  private static DocumentState? GetState(RhinoDoc? doc)
  {
    if (doc == null || OpeningDocuments.Contains(doc.RuntimeSerialNumber))
      return null;

    if (!Documents.TryGetValue(doc.RuntimeSerialNumber, out var state))
    {
      state = new DocumentState(doc);
      Documents.Add(doc.RuntimeSerialNumber, state);
    }

    if (!HistorySettings.BrokenRecordWarningEnabled || doc.UndoActive || doc.RedoActive)
    {
      state.IndexDirty = true;
      return null;
    }
    return state;
  }

  private static void WarmIndex(RhinoDoc? doc) => GetState(doc)?.EnsureIndex();

  private static DocumentState? GetEditState(RhinoDoc? doc)
  {
    var state = GetState(doc);
    if (state == null)
      return null;

    if (state.Frame == null || state.Frame.Reconciled)
    {
      ClearHighlight();
      state.Frame?.Dispose();
      if (state.IndexDirty)
        state.EnsureIndex();
      state.Frame = new Frame("Document edit", state.NativeRecordCount);
    }
    return state;
  }

  private static void CaptureCandidates(DocumentState state, Guid objectId)
  {
    var frame = state.Frame!;
    if (state.Records.TryGetValue(objectId, out var record))
      frame.Before.TryAdd(objectId, record);
    if (state.Children.TryGetValue(objectId, out var children))
      foreach (var childId in children)
        frame.Before.TryAdd(childId, state.Records[childId]);
  }

  private static void Publish(DocumentState state, string phase)
  {
    var frame = state.Frame!;
    if (frame.Dismissed || !HistorySettings.BrokenRecordWarningEnabled
      || state.Doc.UndoActive || state.Doc.RedoActive)
      return;

    if (frame.Completed && state.Doc.Objects.HistoryRecordCount < frame.InitialRecordCount)
      foreach (var record in state.Records)
        frame.Before.TryAdd(record.Key, record.Value);

    var affected = new HashSet<Guid>();
    foreach (var entry in frame.Before)
    {
      var obj = state.Doc.Objects.FindId(entry.Key);
      if (obj == null)
      {
        if (frame.DeletedGeometry.ContainsKey(entry.Key))
          affected.Add(entry.Key);
        continue;
      }

      if (!obj.HasHistoryRecord())
      {
        // During replay a replacement's record may not be attached until the command ends.
        if (frame.Completed || entry.Value.ExistingParents.Any(parent =>
          state.Doc.Objects.FindId(parent) == null && !frame.Replacing.Contains(parent)))
          affected.Add(entry.Key);
        continue;
      }

      var parents = obj.HistoryParents() ?? Array.Empty<Guid>();
      if (entry.Value.ExistingParents.Any(parent => parents.Contains(parent)
        && state.Doc.Objects.FindId(parent) == null
        && (frame.Completed || !frame.Replacing.Contains(parent))))
        affected.Add(entry.Key);
    }

    if (RhinoDoc.ActiveDoc?.RuntimeSerialNumber != state.Doc.RuntimeSerialNumber)
      return;

    if (affected.Count == 0)
    {
      if (ReferenceEquals(_highlightFrame, frame))
        ClearHighlight(dismiss: false);
      return;
    }

    if (!ReferenceEquals(_highlightFrame, frame))
    {
      ClearHighlight();
      _highlightFrame = frame;
      _highlight = new PreviewDisplay.ObjectHighlighter(state.Doc, PreviewDisplay.HistoryWarningStyle);
    }
    _highlight!.SetObjects(affected, frame.DeletedGeometry);
    if (!frame.LastAffected.SetEquals(affected))
    {
      Log.Write(Tag, $"command={frame.Name} phase={phase} affected={affected.Count} objects={string.Join(",", affected)}");
      frame.LastAffected.Clear();
      frame.LastAffected.UnionWith(affected);
    }
  }

  private static void QueueCompletion()
  {
    if (_completionTimer != null)
      return;

    _completionTimer = new System.Windows.Forms.Timer { Interval = WarningCompletionIntervalMilliseconds };
    _completionTimer.Tick += OnCompletionTick;
    _completionTimer.Start();
  }

  private static void OnCompletionTick(object? sender, EventArgs e) => Watch(() =>
  {
    if (_highlightFrame is not { Completed: true } frame || Command.InCommand())
      return;

    // EndCommand can precede Rhino's modal history warning. Never clear inside its nested message loop.
    if (IsRhinoModalOpen())
      return;

    foreach (var state in Documents.Values)
      if (ReferenceEquals(state.Frame, frame))
      {
        Log.Write(Tag, $"command={frame.Name} warning closed; clearing highlights");
        CompleteFrame(frame);
        Reconcile(state);
        break;
      }
  });

  private static bool IsRhinoModalOpen()
  {
    if (!OperatingSystem.IsWindows())
      return false;
    var window = RhinoApp.MainWindowHandle();
    return window != IntPtr.Zero && IsWindow(window) && !IsWindowEnabled(window);
  }

  private static void CompleteFrame(Frame frame)
  {
    frame.Completed = true;
    frame.Dismissed = true;
    if (ReferenceEquals(_highlightFrame, frame))
      ClearHighlight();
  }

  private static void Reconcile(DocumentState state)
  {
    var frame = state.Frame;
    if (frame == null || !frame.Completed || frame.Reconciled)
      return;

    // Rhino can strip records after EndCommand; reconcile the index without re-highlighting.
    if (state.IndexDirty || state.Doc.Objects.HistoryRecordCount < frame.InitialRecordCount)
      state.EnsureIndex();
    else
    {
      foreach (var objectId in frame.Changed.Concat(frame.Before.Keys).Distinct())
        state.RefreshRecord(objectId);
      state.NativeRecordCount = state.Doc.Objects.HistoryRecordCount;
    }
    frame.Reconciled = true;
    if (!ReferenceEquals(_highlightFrame, frame))
      frame.Dispose();
  }

  private static void ClearHighlight(bool dismiss = true)
  {
    if (dismiss && _highlightFrame != null)
      _highlightFrame.Dismissed = true;
    var highlight = _highlight;
    var frame = _highlightFrame;
    _highlight = null;
    _highlightFrame = null;
    _lastEscapeTick = 0;
    if (_completionTimer != null)
    {
      _completionTimer.Stop();
      _completionTimer.Tick -= OnCompletionTick;
      _completionTimer.Dispose();
      _completionTimer = null;
    }
    if (highlight != null)
      Watch(highlight.Dispose);
    if (frame?.Reconciled == true)
      frame.Dispose();
  }

  private static void RemoveDocument(uint serialNumber)
  {
    if (!Documents.Remove(serialNumber, out var state))
      return;
    if (ReferenceEquals(_highlightFrame, state.Frame))
      ClearHighlight();
    state.Frame?.Dispose();
  }

  private static bool IsUndoRedo(string name) =>
    name.Equals("Undo", StringComparison.OrdinalIgnoreCase)
    || name.Equals("Redo", StringComparison.OrdinalIgnoreCase);

  private static void Watch(Action action)
  {
    try { action(); }
    catch (Exception ex) { Log.Write(Tag, $"monitor: {ex.Message}"); }
  }

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindow(IntPtr window);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool IsWindowEnabled(IntPtr window);

  private sealed record HistoryRecord(Guid[] ExistingParents);

  private sealed class DocumentState(RhinoDoc doc)
  {
    internal readonly RhinoDoc Doc = doc;
    internal readonly Dictionary<Guid, HistoryRecord> Records = [];
    internal readonly Dictionary<Guid, HashSet<Guid>> Children = [];
    internal Frame? Frame;
    internal bool IndexDirty = true;
    internal int NativeRecordCount;

    internal void EnsureIndex()
    {
      if (!IndexDirty && NativeRecordCount == Doc.Objects.HistoryRecordCount)
        return;

      Records.Clear();
      Children.Clear();
      if (Doc.Objects.HistoryRecordCount > 0)
      {
        var settings = new ObjectEnumeratorSettings
        {
          NormalObjects = true,
          LockedObjects = true,
          HiddenObjects = true
        };
        foreach (var obj in Doc.Objects.GetObjectList(settings))
          RefreshRecord(obj.Id);
      }
      NativeRecordCount = Doc.Objects.HistoryRecordCount;
      IndexDirty = false;
      Log.Write(Tag, $"indexed doc={Doc.RuntimeSerialNumber} objects={Records.Count} records={NativeRecordCount}");
    }

    internal void RefreshRecord(Guid objectId)
    {
      if (Records.Remove(objectId, out var old))
        foreach (var parent in old.ExistingParents)
          if (Children.TryGetValue(parent, out var children))
          {
            children.Remove(objectId);
            if (children.Count == 0)
              Children.Remove(parent);
          }

      var obj = Doc.Objects.FindId(objectId);
      if (obj?.HasHistoryRecord() != true)
        return;

      var parents = (obj.HistoryParents() ?? Array.Empty<Guid>())
        .Where(parent => Doc.Objects.FindId(parent) != null).Distinct().ToArray();
      Records.Add(objectId, new HistoryRecord(parents));
      foreach (var parent in parents)
      {
        if (!Children.TryGetValue(parent, out var children))
          Children.Add(parent, children = []);
        children.Add(objectId);
      }
    }
  }

  private sealed class Frame(string name, int initialRecordCount) : IDisposable
  {
    internal readonly string Name = name;
    internal readonly int InitialRecordCount = initialRecordCount;
    internal readonly List<Guid> Commands = [];
    internal readonly Dictionary<Guid, HistoryRecord> Before = [];
    internal readonly HashSet<Guid> Changed = [];
    internal readonly HashSet<Guid> Replacing = [];
    internal readonly Dictionary<Guid, GeometryBase> DeletedGeometry = [];
    internal readonly HashSet<Guid> LastAffected = [];
    internal bool Completed;
    internal bool Reconciled;
    internal bool Dismissed;

    public void Dispose()
    {
      foreach (var geometry in DeletedGeometry.Values)
        geometry.Dispose();
      DeletedGeometry.Clear();
    }
  }
}
