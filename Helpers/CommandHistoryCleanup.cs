using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.ApplicationSettings;

namespace vTools;

internal sealed class CommandHistoryCleanup : IDisposable
{
  // History safety limits and native output whitelists
  internal const int MaximumHistoryCharacters = 1000000; // Maximum UTF-16 characters replayed; larger histories are left untouched.
  private static readonly char[] LineEndings = ['\r','\n']; // Supported history line separators: CRLF, CR and LF.
  private static readonly string[] UnrollProgressLines = [
    "Calculating starting area... Press Esc to cancel",
    "Calculating ending area... Press Esc to cancel",
    "Calculating starting area...",
    "Calculating ending area...",
    "Creating meshes... Press Esc to cancel",
    "Area difference is within tolerance."]; // Exact English routine messages; warnings, numerical discrepancies and unknown/localized output are preserved.
  private static readonly Regex UnrollCount = new(@"^(?:\d+|One|Two|Three|Four|Five|Six|Seven|Eight|Nine|Ten) surfaces? unrolled\.$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase); // Native per-call surface count, including worded small counts; the outer command reports its own result.
  private static readonly Regex UnrollSelectionCount = new(@"^(?:\d+|One|Two|Three|Four|Five|Six|Seven|Eight|Nine|Ten) (?:text dots?|points?|curves?|(?:open |closed )?(?:surfaces?|polysurfaces?)) added to selection\.$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase); // Routine selection counts from the internal SelID macro; other selection messages are retained.
  private static readonly Regex UnrollAreaWarning = new(@"^Area is .+ (?:bigger|smaller) after unrolling\.?$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase); // English numerical area warning; retained and qualified with its owning part.
  private const string ExportSavedPrefix = "File successfully saved as "; // English native export success prefix; removed only when its path matches this export's temporary file.
  private const int WindowClassNameCapacity = 256; // UTF-16 character capacity for a Windows control class name, including its terminator.
  private const int PaintMessage = 0x000F; // WM_PAINT: defer command-control painting until native output has been filtered.
  private const int EraseBackgroundMessage = 0x0014; // WM_ERASEBKGND: preserve the existing command-pane pixels during a native operation.

  [ThreadStatic] private static bool _replaying;
  private readonly string _owner;
  private readonly string _before;
  private readonly Func<string,bool> _removeLine;
  private Func<string,string>? _rewriteLine;
  private bool _disposed;
  private CommandPaintScope? _paintScope;
  private string? _promptBefore;

  private CommandHistoryCleanup(string owner,string before,Func<string,bool> removeLine)
  { _owner=owner; _before=before; _removeLine=removeLine; }

  internal static CommandHistoryCleanup? Begin(string owner,bool enabled,Func<string,bool> removeLine)
  {
    if(!enabled||_replaying) return null;
    try
    {
      var before=RhinoApp.CommandHistoryWindowText??string.Empty;
      if(before.Length>MaximumHistoryCharacters)
      { Log.Write(owner,"history cleanup skipped: history exceeds {0} characters",MaximumHistoryCharacters); return null; }
      return new(owner,before,removeLine);
    }
    catch(Exception ex) { Log.Write(owner,"history cleanup could not start: {0}",ex); return null; }
  }

  internal static CommandHistoryCleanup? BeginUnroll(string owner,int partNumber,bool quietNative)
  {
    var scope=Begin(owner,true,line=>quietNative&&(IsUnrollProgress(line)||UnrollAreaWarning.IsMatch(line.Trim())));
    if(scope!=null)
    {
      scope._rewriteLine=line=>FormatUnrollLine(owner,partNumber,line);
      if(quietNative)
      {
        scope._promptBefore=RhinoApp.CommandPrompt;
        scope._paintScope=CommandPaintScope.TryBegin(owner,scope._before);
      }
    }
    return scope;
  }

  internal static CommandHistoryCleanup? BeginExport(string owner,string temporaryPath)
    =>Begin(owner,true,line=>IsExportProgress(line,temporaryPath));

  internal static bool IsExportProgress(string line,string temporaryPath)=>string.IsNullOrWhiteSpace(line)||
    string.Equals(line.Trim(),ExportSavedPrefix+temporaryPath+".",StringComparison.OrdinalIgnoreCase);

  internal static string FormatUnrollLine(string owner,int partNumber,string line) =>
    UnrollAreaWarning.IsMatch(line.Trim())?$"{owner}: part #{partNumber} native unroll: {line.Trim()}":line;

  internal static bool IsUnrollProgress(string line)
  {
    var text=line.Trim();
    return UnrollProgressLines.Contains(text,StringComparer.OrdinalIgnoreCase)||UnrollCount.IsMatch(text)||UnrollSelectionCount.IsMatch(text);
  }

  internal static bool TryFilter(string before,string current,Func<string,bool> removeLine,out string replacement,out int removed)
    => TryRewrite(before,current,removeLine,null,out replacement,out removed);

  private static bool TryRewrite(string before,string current,Func<string,bool> removeLine,Func<string,string>? rewriteLine,
    out string replacement,out int removed)
  {
    replacement=current; removed=0;
    if(current.Length>MaximumHistoryCharacters||!current.StartsWith(before,StringComparison.Ordinal)) return false;
    var filtered=new StringBuilder(current.Length); filtered.Append(before);
    int start=before.Length;
    while(start<current.Length)
    {
      int end=current.IndexOfAny(LineEndings,start);
      if(end<0) end=current.Length;
      int next=end;
      if(next<current.Length) { next++; if(current[end]=='\r'&&next<current.Length&&current[next]=='\n') next++; }
      var line=current.Substring(start,end-start);
      if(removeLine(line)) removed++;
      else
      {
        var rewritten=rewriteLine?.Invoke(line)??line;
        if(!string.Equals(line,rewritten,StringComparison.Ordinal)) removed++;
        filtered.Append(rewritten); filtered.Append(current,end,next-end);
      }
      start=next;
    }
    if(removed==0) return false;
    replacement=filtered.ToString(); return true;
  }

  public void Dispose()
  {
    if(_disposed) return; _disposed=true;
    if(_replaying) { _paintScope?.Dispose(); return; }
    var timer=System.Diagnostics.Stopwatch.StartNew();
    try
    {
      var current=RhinoApp.CommandHistoryWindowText??string.Empty;
      if(!TryRewrite(_before,current,_removeLine,_rewriteLine,out var replacement,out int removed)) return;
      if(!string.Equals(current,RhinoApp.CommandHistoryWindowText,StringComparison.Ordinal))
      { Log.Write(_owner,"history cleanup skipped: history changed during filtering"); return; }
      bool capture=RhinoApp.CommandWindowCaptureEnabled;
      _replaying=true;
      try
      {
        // Replaying prior history must not flood another caller's active output capture.
        if(capture) RhinoApp.CommandWindowCaptureEnabled=false;
        RhinoApp.ClearCommandHistoryWindow();
        try { RhinoApp.Write("{0}",replacement); }
        catch
        {
          RhinoApp.ClearCommandHistoryWindow(); RhinoApp.Write("{0}",current);
          throw;
        }
      }
      finally
      {
        try { RhinoApp.CommandWindowCaptureEnabled=capture; }
        finally { _replaying=false; }
      }
      Log.Write(_owner,"history cleanup removed or qualified {0} native lines; elapsed_ms={1} history_chars={2}",removed,timer.ElapsedMilliseconds,current.Length);
    }
    catch(Exception ex) { Log.Write(_owner,"history cleanup failed; command result unchanged: {0}",ex); }
    finally
    {
      try
      {
        if(_paintScope!=null&&_promptBefore!=null)
        {
          bool echo=AppearanceSettings.EchoPromptsToHistoryWindow;
          try { AppearanceSettings.EchoPromptsToHistoryWindow=false; RhinoApp.SetCommandPrompt(_promptBefore); }
          finally { AppearanceSettings.EchoPromptsToHistoryWindow=echo; }
        }
      }
      catch(Exception ex) { Log.Write(_owner,"command prompt restore failed: {0}",ex.Message); }
      finally { _paintScope?.Dispose(); }
    }
  }

  private sealed class CommandPaintScope : IDisposable
  {
    private static bool _unavailableReported;
    private readonly List<PaintGuard> _guards = new();

    internal static CommandPaintScope? TryBegin(string owner,string history)
    {
      if(!OperatingSystem.IsWindows()) return null;
      var scope=new CommandPaintScope();
      try
      {
        var main=RhinoApp.MainWindowHandle();
        if(main==IntPtr.Zero) return null;
        var controls=new List<IntPtr>();
        EnumChildWindows(main,(window,_)=>
        {
          if(IsWindowVisible(window)&&IsEditControl(window)) controls.Add(window);
          return true;
        },IntPtr.Zero);
        var histories=controls.Where(window=>MatchesHistory(ReadWindowText(window),history)).ToArray();
        var parents=histories.Select(GetParent).Where(parent=>parent!=IntPtr.Zero&&parent!=main).ToHashSet();
        foreach(var window in controls.Where(window=>histories.Contains(window)||parents.Contains(GetParent(window))))
          scope._guards.Add(new PaintGuard(window));
        if(scope._guards.Count>0) return scope;
        if(!_unavailableReported)
        {
          _unavailableReported=true;
          Log.Write(owner,"command-pane paint guard unavailable; retaining safe post-call history cleanup");
        }
      }
      catch(Exception ex) { Log.Write(owner,"command-pane paint guard unavailable: {0}",ex.Message); }
      scope.Dispose();
      return null;
    }

    private static bool IsEditControl(IntPtr window)
    {
      var name=new StringBuilder(WindowClassNameCapacity);
      GetClassName(window,name,name.Capacity);
      return name.ToString().Contains("RichEdit",StringComparison.OrdinalIgnoreCase)||
        name.ToString().Equals("Edit",StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWindowText(IntPtr window)
    {
      int length=GetWindowTextLength(window);
      if(length<=0||length>MaximumHistoryCharacters) return string.Empty;
      var text=new StringBuilder(length+1);
      GetWindowText(window,text,text.Capacity);
      return text.ToString();
    }

    private static bool MatchesHistory(string text,string history)
    {
      var normalized=text.Replace("\r\n","\n").Replace('\r','\n').TrimEnd('\n');
      return normalized.Contains('\n')&&history.Replace("\r\n","\n").Replace('\r','\n').TrimEnd('\n')
        .EndsWith(normalized,StringComparison.Ordinal);
    }

    public void Dispose()
    {
      foreach(var guard in _guards.AsEnumerable().Reverse())
      {
        try { guard.Dispose(); }
        catch(Exception ex) { Log.Write("CommandHistoryCleanup","command-pane paint restore failed: {0}",ex.Message); }
      }
      _guards.Clear();
    }

    private sealed class PaintGuard : System.Windows.Forms.NativeWindow,IDisposable
    {
      internal PaintGuard(IntPtr window) { AssignHandle(window); }
      protected override void WndProc(ref System.Windows.Forms.Message message)
      {
        if(message.Msg==PaintMessage)
        {
          ValidateRect(Handle,IntPtr.Zero);
          message.Result=IntPtr.Zero;
          return;
        }
        if(message.Msg==EraseBackgroundMessage)
        {
          message.Result=new IntPtr(1);
          return;
        }
        base.WndProc(ref message);
      }
      public void Dispose()
      {
        var window=Handle;
        if(window==IntPtr.Zero) return;
        ReleaseHandle();
        if(IsWindow(window)) InvalidateRect(window,IntPtr.Zero,true);
      }
    }

    private delegate bool WindowCallback(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent,WindowCallback callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int capacity);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr window,IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window,IntPtr rectangle,bool erase);
  }
}
