using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace vTools;

/// <summary>The Windows clipboard contract for native PatternSmith workspace patterns.</summary>
internal static class PatternSmithClipboard
{
  // Native format and validation limits
  internal const string Format="Autometrix.Presentation.Core.Clipboard.ClipboardPatternContainer"; // Registered format for workspace patterns, not nested copies or elements.
  private const string EditorFormat="Autometrix.Presentation.Core.Clipboard.ClipboardEntityContainer"; // Registered format for geometry within an open pattern editor.
  private const int MaximumPayloadBytes=32*1024*1024; // Maximum UTF-8 byte count for one clipboard transfer.
  private const int MaximumXmlDepth=128; // Maximum XML element depth accepted from the clipboard.
  private const int MaximumEnvelopeBytes=64; // Allows the WPF marker, string header, and HGLOBAL allocation padding.
  private const int MaximumRhinoCopyBytes=128*1024*1024; // Maximum raw 3DM clipboard geometry accepted by the local paste bridge.
  private static readonly byte[] RhinoFileMagic=Encoding.ASCII.GetBytes("3D Geometry File Format "); // openNURBS binary archive signature; never accept a path or arbitrary file.
  private static readonly string[] RhinoGeometryFormats=["Rhino 9 3DM Geometry","Rhino 8 3DM Geometry","Rhino 7 3DM Geometry","Rhino 6 3DM Geometry"]; // Modern Rhino HGLOBAL archive formats, newest first.
  private static readonly byte[] WpfMarker=new Guid("FD9EA796-3B13-4370-A679-56106BB288FB").ToByteArray(); // WPF serialized clipboard object marker.
  private static readonly UTF8Encoding Utf8=new(false,true); // Strict UTF-8 without a BOM or lossy replacement.
  internal static readonly XName ContainerName=XName.Get("ClipboardPatternContainer","http://autometrix.com/xml");
  internal static readonly XName EditorContainerName=ContainerName.Namespace+"ClipboardEntityContainer";
  internal sealed record PreparedPayload(string Workspace,string Editor);

  internal static PreparedPayload ReadPreparedPayload(string path)
  {
    using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    long maximumPackageBytes=2L*MaximumPayloadBytes;
    if(file.Length>maximumPackageBytes)throw new InvalidDataException("The PatternSmith clipboard payload is too large.");
    using var reader=XmlReader.Create(file,ReaderSettings(maximumPackageBytes));
    var package=XElement.Load(reader,LoadOptions.PreserveWhitespace);
    var workspace=package.Element(ContainerName);var editor=package.Element(EditorContainerName);
    if(package.Name!="PatternSmithClipboard"||workspace?.Element(ContainerName.Namespace+"FilePaths")==null||editor==null)
      throw new InvalidDataException("The prepared data is not a complete PatternSmith clipboard package.");
    return new(workspace.ToString(SaveOptions.DisableFormatting),editor.ToString(SaveOptions.DisableFormatting));
  }

  internal static XElement ReadContainer(string payload)
  {
    using(var check=XmlReader.Create(new StringReader(payload),ReaderSettings()))
      while(check.Read())if(check.Depth>MaximumXmlDepth)throw new InvalidDataException("The PatternSmith clipboard XML is nested too deeply.");
    using var reader=XmlReader.Create(new StringReader(payload),ReaderSettings());
    var root=XElement.Load(reader);
    if(root.Name!=ContainerName&&root.Name!=EditorContainerName)
      throw new InvalidDataException("The clipboard does not contain PatternSmith patterns or editor geometry.");
    return root;
  }

  private static XmlReaderSettings ReaderSettings(long maximum=MaximumPayloadBytes)=>new()
    {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=maximum};

  internal static void Write(PreparedPayload payload)
  {
    using var workspace=new MemoryStream(EncodeString(payload.Workspace),writable:false);
    using var editor=new MemoryStream(EncodeString(payload.Editor),writable:false);
    var data=new System.Windows.DataObject();
    data.SetData(Format,workspace,autoConvert:false);
    data.SetData(EditorFormat,editor,autoConvert:false);
    System.Windows.Clipboard.SetDataObject(data,copy:true);
  }

  internal static bool HasPatternsOrEntities()=>AvailableFormat()!=0;

  internal static bool IsCurrentRhinoCopy(uint sequence)=>
    sequence!=0&&GetWindowThreadProcessId(GetClipboardOwner(),out uint owner)!=0&&
    owner==Environment.ProcessId&&GetClipboardSequenceNumber()==sequence;

  internal static (uint Sequence,byte[] Model) ReadRhinoCopy(uint expectedSequence)
  {
    if(!OpenClipboard(IntPtr.Zero))throw new InvalidOperationException("Windows clipboard is busy. Try pasting again.");
    try
    {
      GetWindowThreadProcessId(GetClipboardOwner(),out uint owner);
      if(owner!=Environment.ProcessId||GetClipboardSequenceNumber()!=expectedSequence)
        throw new InvalidOperationException("The Rhino clipboard changed. Try pasting again.");
      foreach(var name in RhinoGeometryFormats)
      {
        uint format=RegisterClipboardFormat(name);
        if(format==0||!IsClipboardFormatAvailable(format))continue;
        var memory=GetClipboardData(format);
        nuint size=memory==IntPtr.Zero?0:GlobalSize(memory);
        if(size<(nuint)RhinoFileMagic.Length||size>MaximumRhinoCopyBytes)
          throw new InvalidDataException("The Rhino clipboard geometry is empty or too large.");
        var pointer=GlobalLock(memory);
        if(pointer==IntPtr.Zero)throw new InvalidOperationException("Could not read the Rhino clipboard geometry.");
        try
        {
          var model=new byte[(int)size];
          Marshal.Copy(pointer,model,0,model.Length);
          if(!model.AsSpan().StartsWith(RhinoFileMagic))throw new InvalidDataException("The Rhino clipboard does not contain a 3DM archive.");
          return (GetClipboardSequenceNumber(),model);
        }
        finally{GlobalUnlock(memory);}
      }
      throw new InvalidDataException("No supported Rhino geometry on the clipboard.");
    }
    finally{CloseClipboard();}
  }

  private static uint AvailableFormat()
  {
    uint workspace=RegisterClipboardFormat(Format);
    if(workspace!=0&&IsClipboardFormatAvailable(workspace))return workspace;
    uint editor=RegisterClipboardFormat(EditorFormat);
    return editor!=0&&IsClipboardFormatAvailable(editor)?editor:0;
  }

  internal static string? Read()
  {
    uint format=AvailableFormat();
    if(format==0)return null;
    if(!OpenClipboard(IntPtr.Zero))throw new InvalidOperationException("The Windows clipboard is busy. Try pasting again.");
    try
    {
      var memory=GetClipboardData(format);
      if(memory==IntPtr.Zero)return null;
      nuint size=GlobalSize(memory);
      if(size==0||size>MaximumPayloadBytes+MaximumEnvelopeBytes)
        throw new InvalidDataException("The PatternSmith clipboard data is empty or too large.");
      var pointer=GlobalLock(memory);
      if(pointer==IntPtr.Zero)throw new InvalidOperationException("Could not read the PatternSmith clipboard data.");
      try
      {
        var bytes=new byte[(int)size];
        Marshal.Copy(pointer,bytes,0,bytes.Length);
        return DecodeString(bytes);
      }
      finally{GlobalUnlock(memory);}
    }
    finally{CloseClipboard();}
  }

  // Only the NRBF stream header, one UTF-8 string, and the end record are supported.
  // This reads the legacy WPF string without deserializing arbitrary managed objects.
  internal static byte[] EncodeString(string payload)
  {
    if(Utf8.GetByteCount(payload)>MaximumPayloadBytes)throw new InvalidDataException("The PatternSmith clipboard payload is too large.");
    using var stream=new MemoryStream();
    using var writer=new BinaryWriter(stream,Utf8,leaveOpen:true);
    writer.Write(WpfMarker);
    writer.Write((byte)0);writer.Write(1);writer.Write(-1);writer.Write(1);writer.Write(0);
    writer.Write((byte)6);writer.Write(1);writer.Write(payload);writer.Write((byte)11);
    return stream.ToArray();
  }

  internal static string DecodeString(byte[] bytes)
  {
    using var stream=new MemoryStream(bytes,writable:false);
    using var reader=new BinaryReader(stream,Utf8);
    try
    {
      if(!reader.ReadBytes(WpfMarker.Length).AsSpan().SequenceEqual(WpfMarker)||reader.ReadByte()!=0)throw InvalidEnvelope();
      int rootId=reader.ReadInt32();
      if(rootId<=0||reader.ReadInt32()!=-1||reader.ReadInt32()!=1||reader.ReadInt32()!=0||
        reader.ReadByte()!=6||reader.ReadInt32()!=rootId)throw InvalidEnvelope();
      int count=reader.Read7BitEncodedInt();
      if(count<=0||count>MaximumPayloadBytes||count>=stream.Length-stream.Position)throw InvalidEnvelope();
      var text=Utf8.GetString(reader.ReadBytes(count));
      if(reader.ReadByte()!=11)throw InvalidEnvelope();
      return text;
    }
    catch(Exception error) when(error is EndOfStreamException or FormatException or DecoderFallbackException)
    {throw InvalidEnvelope();}
  }

  private static InvalidDataException InvalidEnvelope()=>new("The PatternSmith clipboard payload is not a supported XML string.");

  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
  [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
  [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
  [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
  [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
  [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
  [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
  [DllImport("user32.dll")] private static extern bool CloseClipboard();
  [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr memory);
  [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
  [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
}
