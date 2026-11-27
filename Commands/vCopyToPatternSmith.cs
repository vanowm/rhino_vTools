using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Xml.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace vTools.Commands;

/// <summary>Copies selected Rhino parts or loose geometry to the PatternSmith clipboard.</summary>
[CommandStyle(Style.NotUndoable)]
public sealed class vCopyToPatternSmith : vToolsCommand
{
  // Cleanup review defaults
  [CommandDefault("vCopyToPatternSmith","vTools.Commands.vCopyToPatternSmith.CleanupRetrySeconds",
    "Seconds to repeat a copy with unchanged objects and preparation settings to keep overlap and short-curve findings. 0 disables the shortcut. Applies when cleanup is set to Ask.")]
  private const int CleanupRetrySeconds=15; // Seconds after cleanup stops; 0 disables automatic retry.
  [CommandDefault("vCopyToPatternSmith","vTools.Commands.vCopyToPatternSmith.EnablePasteBridge",
    "On: PatternSmith fixer can request conversion when pasting standard Rhino clipboard geometry. Off: use the explicit copy command instead. Restart Rhino after changing this setting.")]
  private const bool DefaultEnablePasteBridge=true; // true starts the local, current-user paste service; false leaves it off.
  private const string PastePipePrefix="vTools.PatternSmithPaste.1."; // Versioned local pipe name; append the Rhino clipboard owner's process ID.
  private const int PasteRequestTimeoutSeconds=60; // Maximum lifetime of one local conversion request, including waiting for Rhino to be idle.
  private const int MaximumPasteResponseBytes=32*1024*1024; // Maximum encoded workspace or editor XML returned to the fixer.
  private const int MaximumPasteErrorLength=512; // Maximum characters of diagnostic text sent back to PatternSmith.

  private sealed record CleanupKey(uint Document,UnitSystem Units,double Tolerance,string Sources,
    vExportDXF.ExportSettings Export,vCleanup.ExportGeometrySettings Geometry,int Overlaps,int Shorts);
  private sealed record CachedPaste(uint Sequence,byte[]? Workspace,byte[]? Editor)
  {
    internal byte[] Get(bool editor)=>(editor?Editor:Workspace)??throw new InvalidDataException("The PatternSmith paste is too large.");
  }
  private sealed class PasteConversionCache
  {
    internal CachedPaste? Last;
  }
  private static (CleanupKey Key,long Time)? _pendingCleanup;
  private static CancellationTokenSource? _pasteBridge;

  public override string EnglishName=>nameof(vCopyToPatternSmith);
  protected override Result RunCommand(RhinoDoc doc,RunMode mode)=>
    vExportPatternSmith.RunWithWindow(doc,mode,clipboard:true,showMappings:mode!=RunMode.Scripted);

  internal static Func<int,int,string?,vCleanup.ExportCleanupAction?> BeginCleanupReview(RhinoDoc doc,
    RhinoObject[] sources,vExportDXF.ExportSettings export,vCleanup.ExportGeometrySettings geometry,
    uint? clipboardSequence=null,Action<string>? report=null)
  {
    var previous=_pendingCleanup;_pendingCleanup=null;
    var started=Stopwatch.GetTimestamp();
    int seconds=CommandDefaults.Get(nameof(vCopyToPatternSmith),
      "vTools.Commands.vCopyToPatternSmith.CleanupRetrySeconds",CleanupRetrySeconds);
    bool retryInTime=seconds>0&&previous is {} pending&&Stopwatch.GetElapsedTime(pending.Time,started).TotalSeconds<=seconds;
    var ids=sources.Select(obj=>obj.Id).Order().ToArray();
    report??=message=>RhinoApp.WriteLine($"{nameof(vCopyToPatternSmith)}: {message}");
    return (overlaps,shorts,notice)=>
    {
      if(export.CleanupAction!=vCleanup.ExportCleanupAction.Ask)
      {
        report($"{notice} Keeping the remaining findings.");
        return vCleanup.ExportCleanupAction.Ignore;
      }
      var serializer=new Rhino.FileIO.SerializationOptions {WriteRenderMeshes=false,WriteAnalysisMeshes=false};
      var fingerprint=clipboardSequence is {} sequence?$"clipboard:{sequence}":string.Join("\n",ids.Select(id=>doc.Objects.FindId(id) is {IsDeleted:false} obj
        ?$"{id:N}:{obj.Geometry.DataCRC(0):X8}:{doc.Layers.FindIndex(obj.Attributes.LayerIndex)?.FullPath}:{obj.Attributes.ToJSON(serializer)}"
        :$"{id:N}:deleted"));
      var key=new CleanupKey(clipboardSequence.HasValue?0:doc.RuntimeSerialNumber,doc.ModelUnitSystem,doc.ModelAbsoluteTolerance,fingerprint,export,geometry,overlaps,shorts);
      var counts=$"{CountText.Format(overlaps,"overlap")} and {CountText.Format(shorts,"short finding")}";
      if(retryInTime&&previous?.Key==key)
      {
        report($"Keeping {counts} on repeat {(clipboardSequence.HasValue?"paste":"copy")}.");
        return vCleanup.ExportCleanupAction.Ignore;
      }
      if(seconds>0)_pendingCleanup=(key,Stopwatch.GetTimestamp());
      var repeat=seconds>0?$" Repeat within {seconds} seconds to keep the findings.":"";
      report($"Found {counts}; {(clipboardSequence.HasValue?"not pasted":"not copied")}.{repeat} Run {nameof(vCopyToPatternSmith)} to review.");
      return null;
    };
  }

  internal static void StartPasteBridge()
  {
    if(!OperatingSystem.IsWindows()||_pasteBridge!=null||!CommandDefaults.Get(nameof(vCopyToPatternSmith),
      "vTools.Commands.vCopyToPatternSmith.EnablePasteBridge",DefaultEnablePasteBridge))return;
    var token=(_pasteBridge=new CancellationTokenSource()).Token;
    _=Task.Run(()=>ServePasteRequests(token));
  }

  internal static void StopPasteBridge()
  {
    var lifetime=_pasteBridge;_pasteBridge=null;
    lifetime?.Cancel();
    lifetime?.Dispose();
  }

  private static async Task ServePasteRequests(CancellationToken stop)
  {
    var cache=new PasteConversionCache();
    try
    {
      while(!stop.IsCancellationRequested)
      {
        using var pipe=new NamedPipeServerStream(PastePipePrefix+System.Environment.ProcessId,PipeDirection.InOut,1,
          PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
        using var request=CancellationTokenSource.CreateLinkedTokenSource(stop);
        request.CancelAfter(TimeSpan.FromSeconds(PasteRequestTimeoutSeconds));
        try{await ServePasteRequest(pipe,cache,request.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(request.IsCancellationRequested){}
        catch(IOException error){Log.Write(nameof(vCopyToPatternSmith),"paste client disconnected: {0}",error.Message);}
      }
    }
    catch(OperationCanceledException) when(stop.IsCancellationRequested){}
    catch(Exception error){Log.Write(nameof(vCopyToPatternSmith),"paste bridge stopped: {0}",error);}
  }

  private static async Task ServePasteRequest(Stream pipe,PasteConversionCache cache,CancellationToken token)
  {
    // Request: target (0 workspace, 1 editor) and clipboard sequence. Response: status, effective sequence, and byte count.
    var header=new byte[5];await pipe.ReadExactlyAsync(header,token).ConfigureAwait(false);
    if(header[0]>1)return;
    bool editor=header[0]==1;
    uint sequence=BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(1));
    byte status=0;byte[] response;
    try
    {
      if(cache.Last is {} cached&&cached.Sequence==sequence&&PatternSmithClipboard.IsCurrentRhinoCopy(sequence))
        response=cached.Get(editor);
      else
      {
        if(cache.Last is {} last&&!PatternSmithClipboard.IsCurrentRhinoCopy(last.Sequence))cache.Last=null;
        var snapshot=PatternSmithClipboard.ReadRhinoCopy(sequence);
        sequence=snapshot.Sequence;
        string file=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".3dm");
        try
        {
          await using(var output=new FileStream(file,new FileStreamOptions
            {Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.Asynchronous}))
            await output.WriteAsync(snapshot.Model,token).ConfigureAwait(false);
          var (model,splitLayer)=await PreparePasteAtIdle(file,sequence,token).ConfigureAwait(false);
          var payload=vExportPatternSmith.ConvertClipboardCopy(model,splitLayer,token,background:true);
          var converted=new CachedPaste(sequence,EncodePaste(payload.Workspace),EncodePaste(payload.Editor));
          if(!PatternSmithClipboard.IsCurrentRhinoCopy(sequence))
            throw new InvalidOperationException("The clipboard changed while preparing the paste.");
          if(converted.Workspace!=null||converted.Editor!=null)cache.Last=converted;
          response=converted.Get(editor);
        }
        finally{if(File.Exists(file))File.Delete(file);}
      }
    }
    catch(Exception error) when(!token.IsCancellationRequested)
    {
      status=1;
      Log.Write(nameof(vCopyToPatternSmith),"paste bridge conversion: {0}",error);
      var message=error.Message.Split('\n','\r')[0];
      response=Encoding.UTF8.GetBytes(message[..Math.Min(message.Length,MaximumPasteErrorLength)]);
    }
    var reply=new byte[9];reply[0]=status;
    BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(1),sequence);
    BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(5),response.Length);
    await pipe.WriteAsync(reply,token).ConfigureAwait(false);
    await pipe.WriteAsync(response,token).ConfigureAwait(false);
  }

  private static byte[]? EncodePaste(string xml)=>
    Encoding.UTF8.GetByteCount(xml)<=MaximumPasteResponseBytes?Encoding.UTF8.GetBytes(xml):null;

  private static async Task<(XDocument Request,string SplitLayer)> PreparePasteAtIdle(string file,uint sequence,CancellationToken token)
  {
    var result=new TaskCompletionSource<(XDocument,string)>(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnIdle(object? sender,EventArgs args)
    {
      if(token.IsCancellationRequested){RhinoApp.Idle-=OnIdle;result.TrySetCanceled(token);return;}
      if(RhinoApp.InCommand!=0)return;
      RhinoApp.Idle-=OnIdle;
      try
      {
        if(PatternSmithClipboard.GetClipboardSequenceNumber()!=sequence)
          throw new InvalidOperationException("The clipboard changed while preparing the paste.");
        using var doc=RhinoDoc.OpenHeadless(file)??throw new InvalidDataException("Could not read the Rhino clipboard model.");
        result.TrySetResult(vExportPatternSmith.PrepareRhinoClipboardPaste(doc,sequence,token));
      }
      catch(Exception error){result.TrySetException(error);}
    }
    RhinoApp.InvokeOnUiThread((Action)(()=>{if(!token.IsCancellationRequested)RhinoApp.Idle+=OnIdle;}));
    return await result.Task.WaitAsync(token).ConfigureAwait(false);
  }
}
