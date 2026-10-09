using System;
using Rhino;
using Rhino.Commands;
using Rhino.Input;

namespace vTools.Commands;

/// <summary>
/// Executes one command directly from idle, then resumes after its call stack has unwound.
/// </summary>
internal sealed class DeferredNativeCommand : IDisposable
{
  private static int _dispatchDepth;

  internal static bool IsDispatching => _dispatchDepth > 0;

  private readonly uint _docSerial;
  private readonly string _commandName;
  private readonly Action<Result> _completed;
  private bool _dispatched;
  private bool _disposed;
  private Result? _result;

  private DeferredNativeCommand(RhinoDoc doc, string commandName, Action<Result> completed)
  {
    _docSerial = doc.RuntimeSerialNumber;
    if (string.IsNullOrWhiteSpace(commandName) || commandName.Any(char.IsWhiteSpace))
      throw new ArgumentException("A single command name is required, not a macro.", nameof(commandName));
    _commandName = commandName;
    _completed = completed;
    RhinoApp.Idle += OnIdle;
  }

  internal static void Run(RhinoDoc doc, string commandName, Action<Result> completed)
  {
    _ = new DeferredNativeCommand(doc, commandName, completed);
  }

  private void OnIdle(object? sender, EventArgs e)
  {
    // An interactive command pumps messages and can reenter Idle between commands.
    if (_disposed || IsDispatching)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != _docSerial)
    {
      Finish(Result.Cancel);
      return;
    }

    if (RhinoApp.InCommand != 0 || doc.InCommand(false) != 0 || RhinoGet.InGet(doc))
      return;

    if (_result.HasValue)
    {
      Finish(_result.Value);
      return;
    }

    if (!_dispatched)
    {
      _dispatched = true;
      _dispatchDepth++;
      try
      {
        IsolationDiagnostics.RecordNativeDispatch(doc, "direct-idle-call", _commandName);
        // Unlike a macro, ExecuteCommand returns the actual command result before returning.
        _result = RhinoApp.ExecuteCommand(doc, _commandName);
        IsolationDiagnostics.RecordNativeDispatch(doc, "direct-idle-return", _commandName,
          _result == Result.Success);
        Log.Write("NativeCommand", "{0} completed with {1}", _commandName, _result);
      }
      catch (Exception ex)
      {
        Log.Write("NativeCommand", "{0} could not start: {1}", _commandName, ex);
        _result = Result.Failure;
      }
      finally
      {
        _dispatchDepth--;
      }
      return;
    }

  }

  private void Finish(Result result)
  {
    if (_disposed)
      return;
    Dispose();
    _completed(result);
  }

  public void Dispose()
  {
    if (_disposed)
      return;
    _disposed = true;
    RhinoApp.Idle -= OnIdle;
  }
}
