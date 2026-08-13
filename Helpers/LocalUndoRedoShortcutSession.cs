using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Routes undo/redo keystrokes to the active getter without changing Rhino's shared shortcuts.
/// </summary>
internal sealed class LocalUndoRedoShortcutSession : IDisposable
{
  private const int WhGetMessage = 3;
  private const int PmRemove = 1;
  private const uint WmNull = 0x0000;
  private const uint WmKeyDown = 0x0100;
  private const uint WmSysKeyDown = 0x0104;
  private const int VkShift = 0x10;
  private const int VkControl = 0x11;
  private const int VkAlt = 0x12;
  private const int VkY = 0x59;
  private const int VkZ = 0x5A;
  private const uint GaRoot = 2;

  private readonly string _logTag;
  private readonly Func<bool, object> _messageFactory;
  private readonly HookProc _callback;
  private readonly IntPtr _mainWindow;
  private IntPtr _hook;

  internal LocalUndoRedoShortcutSession(
    string logTag,
    Func<bool, object> messageFactory)
  {
    _logTag = logTag;
    _messageFactory = messageFactory;
    _callback = OnGetMessage;
    _mainWindow = RhinoApp.MainWindowHandle();

    if (!OperatingSystem.IsWindows() || _mainWindow == IntPtr.Zero)
      return;

    uint threadId = GetWindowThreadProcessId(_mainWindow, out _);
    if (threadId != 0)
      _hook = SetWindowsHookEx(WhGetMessage, _callback, IntPtr.Zero, threadId);

    Log.Write(
      _logTag,
      _hook != IntPtr.Zero
        ? "installed process-local undo/redo key routing"
        : "failed to install process-local undo/redo key routing");
  }

  public void Dispose()
  {
    if (_hook == IntPtr.Zero)
      return;
    UnhookWindowsHookEx(_hook);
    _hook = IntPtr.Zero;
    Log.Write(_logTag, "removed process-local undo/redo key routing");
  }

  private IntPtr OnGetMessage(int code, IntPtr removeFlag, IntPtr messagePointer)
  {
    try
    {
      if (code >= 0 && removeFlag.ToInt64() == PmRemove && messagePointer != IntPtr.Zero)
      {
        var message = Marshal.PtrToStructure<NativeMessage>(messagePointer);
        if ((message.Message == WmKeyDown || message.Message == WmSysKeyDown) &&
            IsRhinoWindow(message.Window) &&
            TryHistoryAction(unchecked((int)message.WParam.ToUInt64()), out bool redo))
        {
          Marshal.WriteInt32(messagePointer, IntPtr.Size, unchecked((int)WmNull));
          GetBaseClass.PostCustomMessage(_messageFactory(redo));
          Log.Write(_logTag, $"local shortcut {(redo ? "redo" : "undo")} requested");
        }
      }
    }
    catch (Exception ex)
    {
      Log.Write(_logTag, $"local undo/redo key routing failed: {ex.Message}");
    }

    return CallNextHookEx(_hook, code, removeFlag, messagePointer);
  }

  private bool IsRhinoWindow(IntPtr window)
  {
    return window != IntPtr.Zero &&
           (window == _mainWindow || GetAncestor(window, GaRoot) == _mainWindow);
  }

  private static bool TryHistoryAction(int key, out bool redo)
  {
    redo = false;
    if (!IsDown(VkControl) || IsDown(VkAlt))
      return false;

    if (key == VkY)
    {
      redo = true;
      return true;
    }

    if (key != VkZ)
      return false;

    redo = IsDown(VkShift);
    return true;
  }

  internal static void RepairStaleShortcutMacros()
  {
    RepairShortcut(
      ShortcutKey.CtrlZ,
      "!_Undo",
      "vMatchUndo",
      "vNotchesUndo");
    RepairShortcut(
      ShortcutKey.CtrlY,
      "!_Redo",
      "vLineRedo",
      "vMatchRedo",
      "vNotchesRedo");
    RepairShortcut(
      ShortcutKey.ShiftCtrlZ,
      "!_Redo",
      "vLineRedo",
      "vMatchRedo",
      "vNotchesRedo");
  }

  private static void RepairShortcut(
    ShortcutKey shortcut,
    string replacement,
    params string[] internalCommands)
  {
    try
    {
      string macro = ShortcutKeySettings.GetMacro(shortcut) ?? string.Empty;
      if (!internalCommands.Any(command =>
            macro.IndexOf(command, StringComparison.OrdinalIgnoreCase) >= 0))
        return;

      ShortcutKeySettings.SetMacro(shortcut, replacement);
      Log.Write("shortcuts", $"repaired stale {shortcut} macro");
    }
    catch (Exception ex)
    {
      Log.Write("shortcuts", $"failed to repair stale {shortcut} macro: {ex.Message}");
    }
  }

  private static bool IsDown(int key) => (GetKeyState(key) & 0x8000) != 0;

  [StructLayout(LayoutKind.Sequential)]
  private readonly struct NativePoint
  {
    public readonly int X;
    public readonly int Y;
  }

  [StructLayout(LayoutKind.Sequential)]
  private readonly struct NativeMessage
  {
    public readonly IntPtr Window;
    public readonly uint Message;
    public readonly UIntPtr WParam;
    public readonly IntPtr LParam;
    public readonly uint Time;
    public readonly NativePoint Point;
    public readonly uint Private;
  }

  private delegate IntPtr HookProc(int code, IntPtr removeFlag, IntPtr messagePointer);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern IntPtr SetWindowsHookEx(
    int hookType,
    HookProc callback,
    IntPtr module,
    uint threadId);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool UnhookWindowsHookEx(IntPtr hook);

  [DllImport("user32.dll")]
  private static extern IntPtr CallNextHookEx(
    IntPtr hook,
    int code,
    IntPtr removeFlag,
    IntPtr messagePointer);

  [DllImport("user32.dll")]
  private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

  [DllImport("user32.dll")]
  private static extern IntPtr GetAncestor(IntPtr window, uint flags);

  [DllImport("user32.dll")]
  private static extern short GetKeyState(int key);
}
