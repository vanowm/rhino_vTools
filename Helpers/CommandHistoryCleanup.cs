using System.Text;
using System.Text.RegularExpressions;
using Rhino;

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
    "Area difference is within tolerance."]; // Exact English routine messages; warnings, numerical discrepancies and unknown/localized output are preserved.
  private static readonly Regex UnrollCount = new(@"^\d+ surfaces? unrolled\.$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase); // Native per-call surface count; the outer command reports its own result.
  private static readonly Regex UnrollAreaWarning = new(@"^Area is .+ (?:bigger|smaller) after unrolling\.?$",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase); // English numerical area warning; retained and qualified with its owning part.
  private const string ExportSavedPrefix = "File successfully saved as "; // English native export success prefix; removed only when its path matches this export's temporary file.

  [ThreadStatic] private static bool _replaying;
  private readonly string _owner;
  private readonly string _before;
  private readonly Func<string,bool> _removeLine;
  private Func<string,string>? _rewriteLine;
  private bool _disposed;

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
    if(scope!=null) scope._rewriteLine=line=>FormatUnrollLine(owner,partNumber,line);
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
    return UnrollProgressLines.Contains(text,StringComparer.OrdinalIgnoreCase)||UnrollCount.IsMatch(text);
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
    if(_replaying) return;
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
  }
}
