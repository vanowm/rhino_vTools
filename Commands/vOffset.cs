using System;
using Rhino;
using Rhino.Commands;

namespace vTools.Commands;

public sealed class vOffset : Command
{
  private static bool _restartingAfterOffsetDelegate;
  private static EventHandler? _pendingOffsetIdleHandler;

  public override string EnglishName => "vOffset";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    // Silent no-op re-run after delegating to _Offset — registers vOffset as the
    // repeatable last command without showing any prompt.
    if (_restartingAfterOffsetDelegate)
    {
      _restartingAfterOffsetDelegate = false;
      return Result.Success;
    }

    CancelPendingOffset();
    _pendingOffsetIdleHandler = OnLaunchOffsetOnIdle;
    RhinoApp.Idle += _pendingOffsetIdleHandler;
    return Result.Success;
  }

  private static void CancelPendingOffset()
  {
    if (_pendingOffsetIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingOffsetIdleHandler;
      _pendingOffsetIdleHandler = null;
    }
  }

  private static void OnLaunchOffsetOnIdle(object? sender, EventArgs e)
  {
    CancelPendingOffset();

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null)
      return;

    // Command result and RunScript return value are both unreliable in Rhino 9 BETA
    // (both report success even on Escape). Track whether any object was actually added.
    while (true)
    {
      int countBefore = doc.Objects.Count;
      RhinoApp.RunScript("_Offset", false);
      bool placed = doc.Objects.Count > countBefore;
      doc.Objects.UnselectAll();
      doc.Views.Redraw();
      if (!placed) break;
    }

    // Silently re-run vOffset so that pressing Enter afterward repeats vOffset, not _Offset.
    _restartingAfterOffsetDelegate = true;
    _ = RhinoApp.RunScript("_vOffset", false);
    _restartingAfterOffsetDelegate = false; // safety clear if RunScript didn't invoke us
  }
}
