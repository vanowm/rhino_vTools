using System.Buffers.Binary;
using vTools.Commands;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace vTools;

/// <summary>Read-only PatternSmith catalog access shared by export and material synchronization.</summary>
internal static class PatternSmithCatalog
{
  // Shared discovery, compatibility and bounded read defaults
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogServerHost","Windows material-library server host; empty uses PatternSmith preferences, a dot means this computer.")]
    internal const string CatalogServerHost=""; // Empty discovers the configured server; otherwise a Windows machine name or dot.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogLibraryPath","Optional current .library path; empty reads PatternSmith's server preferences after server queries fail.")]
    internal const string CatalogLibraryPath=""; // Empty uses installed current-server preferences; otherwise a user-specified .library path.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.LegacyCatalogLibraryPath","Optional PatternSmith 10 .library path; empty reads its legacy Material.library.preferences file as the final fallback.")]
    internal const string LegacyCatalogLibraryPath=""; // Empty uses legacy PS10 preferences; otherwise a user-specified fallback library path.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.PreferredMaterialGroup","Preferred material-library group path, shown first and used to disambiguate exact material names; empty gives no group priority.")]
    internal const string PreferredMaterialGroup="Artful"; // Group path or empty; other groups remain available, and fuzzy material matches are never automatic.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogConnectTimeoutMilliseconds","Positive milliseconds per connection to an already-running material server; export never launches the server.")]
    internal const int CatalogConnectTimeoutMilliseconds=300; // Positive milliseconds per discovery-pipe connection.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogOperationTimeoutMilliseconds","Positive milliseconds per read-only material query; failure permits read-only file fallbacks.")]
    internal const int CatalogOperationTimeoutMilliseconds=5000; // Positive milliseconds per handshake/query, not an unbounded wait.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogConnectionSettleMilliseconds","Milliseconds for the server to register a private session before handshaking; range is 0 through 2000.")]
    internal const int CatalogConnectionSettleMilliseconds=200; // Nonnegative connection-registration delay, capped at 2000 milliseconds.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogDisconnectTimeoutMilliseconds","Positive maximum milliseconds for best-effort session Disconnect before closing the pipe.")]
    internal const int CatalogDisconnectTimeoutMilliseconds=250; // Positive bounded close wait; never delays Rhino indefinitely.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogMaximumFrameBytes","Positive maximum catalog payload bytes before allocation; accepted range 1024 through 268435456.")]
    internal const int CatalogMaximumFrameBytes=33554432; // 32 MiB default payload/decompressed library-XML limit; hard maximum 256 MiB.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogMaximumStringBytes","Positive even UTF-16 command-string byte limit, at least 128 and no larger than the catalog payload limit.")]
    internal const int CatalogMaximumStringBytes=65536; // 64 KiB default command/argument ceiling before allocation.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogMaximumXmlDepth","Maximum material XML nesting depth; accepted range 8 through 512.")]
    internal const int CatalogMaximumXmlDepth=128; // Positive XML depth ceiling for server and file readers.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogMaximumXmlNodes","Positive maximum catalog XML element/attribute count before materializing a reply or file.")]
    internal const int CatalogMaximumXmlNodes=1000000; // Positive catalog node-count ceiling to bound memory use.
    private const int CatalogWriteChunkBytes=65000; // Fixed native transport block size; command and data are framed together before writing.
    internal sealed record CatalogMaterial(string Id,string Name,double? Width,XElement Settings,string Group="");
    internal sealed record CatalogResult(string Source,IReadOnlyList<CatalogMaterial> Materials,string Identity="")
    {internal IReadOnlyList<string> Groups {get;init;}=[];internal IReadOnlyList<string> ReservedIds {get;init;}=[];internal bool Available {get;init;}=true;}
    private static T Config<T>(string key,T factory)=>CommandDefaults.Get("vExportPatternSmith","vTools.Commands.vExportPatternSmith."+key,factory);
    internal static bool SameIdentity(string first,string second)
    {
      if(first.Equals(second,StringComparison.OrdinalIgnoreCase))return true;
      string PathIdentity(string value)=>value.StartsWith("library:",StringComparison.Ordinal)?value["library:".Length..]:
        value.StartsWith("server:",StringComparison.Ordinal)&&value.Contains('\n')?value[(value.LastIndexOf('\n')+1)..]:"";
      string left=PathIdentity(first),right=PathIdentity(second);
      return left.Length>0&&right.Length>0&&left.Equals(right,StringComparison.OrdinalIgnoreCase);
    }
    internal static CatalogResult Load(CancellationToken cancellationToken)
    {
      string? Optional(string value)=>string.IsNullOrWhiteSpace(value)?null:value;
      var preferences=PatternSmithCatalogPreferences.Read(currentLibraryOverride:Optional(Config(nameof(CatalogLibraryPath),CatalogLibraryPath)),
        patternSmith10LibraryOverride:Optional(Config(nameof(LegacyCatalogLibraryPath),LegacyCatalogLibraryPath)),serverHostOverride:Optional(Config(nameof(CatalogServerHost),CatalogServerHost)));
      try
      {
        var result=PatternSmithCatalogLoader.ReadAsync(preferences,ReadLibraryXml,connectTimeoutMilliseconds:Math.Max(1,Config(nameof(CatalogConnectTimeoutMilliseconds),CatalogConnectTimeoutMilliseconds)),
          operationTimeoutMilliseconds:Math.Max(1,Config(nameof(CatalogOperationTimeoutMilliseconds),CatalogOperationTimeoutMilliseconds)),cancellationToken:cancellationToken).GetAwaiter().GetResult();
        foreach(string diagnostic in result.Diagnostics)Log.Write(nameof(PatternSmithCatalog),"catalog fallback: {0}",diagnostic);
        XNamespace n=PatternSmithCatalogSnapshot.Nesting,a="http://autometrix.com/xml",xsi="http://www.w3.org/2001/XMLSchema-instance";
        XNamespace library="http://autometrix.com/xml/nesting/MaterialLibrary";
        var groups=new Dictionary<XElement,string>();
        foreach(var snapshot in result.Snapshots)
          foreach(var group in snapshot.Groups)
            if(snapshot.ResolveOptional(group.RawGroup.Element(library+"Materials")) is {} members)
              foreach(var member in members.Elements())groups.TryAdd(snapshot.Resolve(member),group.Path);
        var materials=new List<CatalogMaterial>();
        foreach(var material in result.Materials)
        {
          var snapshot=result.Snapshots.First(item=>ReferenceEquals(item.Document,material.RawTemplate.Document));
          var template=material.RawTemplate;
          var pattern=snapshot.ResolveOptional(template.Element(n+"MaterialPattern"));
          // Patterned/sheet templates need a separate native pattern adapter; do
          // not silently change their type while retaining a catalog identity.
          if(!(((string?)template.Attribute(xsi+"type"))?.EndsWith(":RollTemplate",StringComparison.Ordinal)??false))continue;
          if(bool.TryParse(snapshot.Value(template.Element(n+"IsHideMaterial")),out bool hide)&&hide)continue;
          if(pattern==null||!(((string?)pattern.Attribute(xsi+"type"))?.EndsWith(":SolidMaterialPattern",StringComparison.Ordinal)??false))continue;
          var settings=new XElement("Settings");
          void Add(string attribute,XElement? element)
          {
            string value=snapshot.Value(element);
            if(!string.IsNullOrWhiteSpace(value))settings.SetAttributeValue(attribute,value);
          }
          Add("copySpacing",template.Element(n+"CopySpacing"));Add("edgeSpacing",template.Element(n+"EdgeSpacing"));
          Add("maxCutSpeed",template.Element(n+"MaxCutSpeed"));Add("maxPlotSpeed",template.Element(n+"MaxPlotSpeed"));
          Add("materialCost",template.Element(n+"Cost"));Add("materialUnit",template.Element(n+"Unit"));
          var color=snapshot.ResolveOptional(template.Element(n+"Color"));
          Add("materialHue",color?.Element(a+"Hue"));Add("materialSaturation",color?.Element(a+"Saturation"));
          Add("materialLightness",color?.Element(a+"Lightness"));Add("materialAlpha",color?.Element(a+"Alpha"));
          var power=snapshot.ResolveOptional(template.Element(n+"PowerLevelSettings"));
          Add("enablePower",power?.Element(n+"EnablePowerSettings"));Add("minimumPower",power?.Element(n+"MinimumPower"));Add("maximumPower",power?.Element(n+"MaximumPower"));
          materials.Add(new(material.MaterialId,material.Name,material.CatalogWidthInches,settings,groups.GetValueOrDefault(template,"")));
        }
        Log.Write(nameof(PatternSmithCatalog),"catalog source={0}; available={1}; supported solid rolls={2}",result.Source,result.Materials.Count,materials.Count);
        bool server=result.Source.StartsWith("server:",StringComparison.Ordinal);
        return new(server?"running library server":result.Source["library:".Length..].Equals(preferences.CurrentLibraryPath,StringComparison.OrdinalIgnoreCase)?"current library file (read-only)":"PatternSmith 10 library (read-only)",
          materials.OrderByDescending(material=>PreferredGroup(material.Group)).ThenBy(material=>material.Group,StringComparer.OrdinalIgnoreCase)
            .ThenBy(material=>material.Name,StringComparer.OrdinalIgnoreCase).ThenBy(material=>material.Id,StringComparer.Ordinal).ToArray(),
          result.Source+(server?"\n"+(preferences.CurrentLibraryPath??""):""))
          {Groups=result.Snapshots.SelectMany(snapshot=>snapshot.Groups).Select(group=>group.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ReservedIds=result.Materials.Select(material=>material.MaterialId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()};
      }
      catch(IOException ex)
      {
        Log.Write(nameof(PatternSmithCatalog),"material catalog unavailable: {0}",ex.Message);
        return new("unavailable (project materials only)",[]){Available=false};
      }
    }

    private static async Task<XDocument> ReadLibraryXml(string path,CancellationToken cancellationToken)
    {
      path=Environment.ExpandEnvironmentVariables(path);
      if(!Path.IsPathRooted(path))path=Path.GetFullPath(path,Path.GetDirectoryName(typeof(vExportPatternSmith).Assembly.Location)!);
      int maximum=Config(nameof(CatalogMaximumFrameBytes),CatalogMaximumFrameBytes);
      if(maximum<1024||maximum>268435456)throw new InvalidDataException("Catalog byte limit must be between 1024 and 268435456.");
      using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
      using var archive=new System.IO.Compression.ZipArchive(file,System.IO.Compression.ZipArchiveMode.Read);
      var entry=archive.GetEntry("Groups.xml")??throw new InvalidDataException("Material library has no Groups.xml catalog.");
      if(entry.Length>maximum)throw new InvalidDataException("Material catalog exceeds the configured decompressed byte limit.");
      using var contents=entry.Open();using var data=new MemoryStream();
      var buffer=new byte[Math.Min(CatalogMaximumStringBytes,maximum)];int read;
      while((read=await contents.ReadAsync(buffer.AsMemory(),cancellationToken).ConfigureAwait(false))!=0)
      {
        if(data.Length+read>maximum)throw new InvalidDataException("Material catalog exceeds its declared size limit.");
        await data.WriteAsync(buffer.AsMemory(0,read),cancellationToken).ConfigureAwait(false);
      }
      var settings=new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=maximum};
      data.Position=0;int nodes=0,maxDepth=Config(nameof(CatalogMaximumXmlDepth),CatalogMaximumXmlDepth),maxNodes=Config(nameof(CatalogMaximumXmlNodes),CatalogMaximumXmlNodes);
      using(var check=XmlReader.Create(data,settings))while(check.Read())
      {
        cancellationToken.ThrowIfCancellationRequested();
        if(check.Depth>maxDepth)throw new InvalidDataException("Material catalog XML is too deeply nested.");
        if(check.NodeType==XmlNodeType.Element&&checked(nodes+=1+check.AttributeCount)>maxNodes)
          throw new InvalidDataException("Material catalog XML exceeds its node limit.");
      }
      data.Position=0;using var reader=XmlReader.Create(data,settings);return XDocument.Load(reader);
    }

    internal static bool PreferredGroup(string path)
    {
      string preferred=Config(nameof(PreferredMaterialGroup),PreferredMaterialGroup).Trim().Trim('\\');
      return preferred.Length>0&&(path.Equals(preferred,StringComparison.OrdinalIgnoreCase)||path.StartsWith(preferred+"\\",StringComparison.OrdinalIgnoreCase));
    }
    // BCL only, net7.0. Use short-lived sessions: connect, query, dispose.
    // NewServer allocates a private pipe in an EXISTING service; it never launches a process.
    private sealed class PatternSmithCatalogClient : IDisposable
    {
        // Defaults and protocol limits. Constructor arguments can override operating limits.
        public const string DefaultHost = "."; // Windows named-pipe host; '.' means this computer.
        public const string DefaultPipeName = "Autometrix.MaterialLibrary.Server"; // Existing discovery pipe, not a filesystem path.
        public const int DefaultConnectTimeoutMilliseconds = CatalogConnectTimeoutMilliseconds; // Positive milliseconds for each pipe connection.
        public const int DefaultOperationTimeoutMilliseconds = CatalogOperationTimeoutMilliseconds; // Positive milliseconds for an entire handshake or query.
        public const int DefaultMaximumFrameBytes = CatalogMaximumFrameBytes; // 32 MiB maximum binary XML payload, before allocation.
        public const int DefaultMaximumStringBytes = CatalogMaximumStringBytes; // 64 KiB maximum UTF-16LE command/argument payload.
        public const int DefaultMaximumXmlDepth = CatalogMaximumXmlDepth; // Maximum binary XML nesting depth.
        public const int DefaultMaximumXmlNodes = CatalogMaximumXmlNodes; // Maximum elements and attributes retained in one reply.
        public const int DefaultHandshakeSettleMilliseconds = CatalogConnectionSettleMilliseconds; // Delay for native server callback registration after connecting.
        private const int HardMaximumFrameBytes = 268435456; // 256 MiB hard ceiling for caller-provided allocation limits.
        private const int MaximumControlFrames = 64; // Maximum keepalives accepted during a single reply.
        private const string ProtocolVersion = "2"; // Installed 11.2 material-library protocol version, not model version.
        private const string ApplicationName = "vTools.CatalogQuery"; // Honest client identity, not a vendor application identity.
        private static readonly Encoding WireEncoding = new UnicodeEncoding(false, false, true);
        private static readonly XNamespace Data = "http://schemas.datacontract.org/2004/07/Autometrix.Core.Nesting.MaterialLibrary.Data";
        private static readonly XNamespace Io = "http://schemas.datacontract.org/2004/07/Autometrix.Core.IO";
        private static readonly XNamespace Nil = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly string[] CallbackMethods =
        {
            "FinishApiHandshake", "ReadMaterialTemplate", "ReadMaterialTemplates",
            "ReadMaterialTemplatesFromGroup", "ReadMessage", "ReadRootMaterialGroup"
        };
        private readonly string _host, _pipeName;
        private readonly int _connectTimeout, _operationTimeout, _maxFrame, _maxString, _xmlDepth, _xmlNodes, _settleMilliseconds;
        private readonly bool _bootstrap;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> _serverMethods = new HashSet<string>(StringComparer.Ordinal);
        private NamedPipeClientStream? _pipe;
        private bool _disposed, _ready;

        public PatternSmithCatalogClient(string host = DefaultHost, string pipeName = DefaultPipeName,
            int connectTimeoutMilliseconds = DefaultConnectTimeoutMilliseconds,
            int operationTimeoutMilliseconds = DefaultOperationTimeoutMilliseconds,
            int maximumFrameBytes = DefaultMaximumFrameBytes,
            int maximumStringBytes = DefaultMaximumStringBytes,
            int maximumXmlDepth = DefaultMaximumXmlDepth,
            int maximumXmlNodes = DefaultMaximumXmlNodes,
            bool useDiscoveryBootstrap = true,
            int handshakeSettleMilliseconds = DefaultHandshakeSettleMilliseconds)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A pipe host is required.", nameof(host));
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.IndexOfAny(new[] { '\\', '/', '\0' }) >= 0)
                throw new ArgumentException("Use a pipe name, not a full pipe path.", nameof(pipeName));
            if (connectTimeoutMilliseconds <= 0 || operationTimeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(connectTimeoutMilliseconds));
            if (maximumFrameBytes < 1024 || maximumFrameBytes > HardMaximumFrameBytes) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
            if (maximumStringBytes < 128 || maximumStringBytes > maximumFrameBytes || maximumStringBytes % 2 != 0) throw new ArgumentOutOfRangeException(nameof(maximumStringBytes));
            if (maximumXmlDepth < 8 || maximumXmlDepth > 512 || maximumXmlNodes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumXmlDepth));
            if (handshakeSettleMilliseconds < 0 || handshakeSettleMilliseconds > 2000) throw new ArgumentOutOfRangeException(nameof(handshakeSettleMilliseconds));
            _host = host; _pipeName = pipeName; _connectTimeout = connectTimeoutMilliseconds;
            _operationTimeout = operationTimeoutMilliseconds; _maxFrame = maximumFrameBytes;
            _maxString = maximumStringBytes; _xmlDepth = maximumXmlDepth; _xmlNodes = maximumXmlNodes; _bootstrap = useDiscoveryBootstrap;
            _settleMilliseconds = handshakeSettleMilliseconds;
        }

        public string Endpoint => @"\\" + _host + @"\pipe\" + _pipeName;
        public string? ConnectedPipeName { get; private set; }
        public bool IsConnected => !_disposed && _ready && _pipe != null && _pipe.IsConnected;
        public IReadOnlyCollection<string> ServerMethods => Array.AsReadOnly(_serverMethods.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (IsConnected) return;
                Abort();
                string sessionPipe = _pipeName;
                if (_bootstrap)
                {
                    sessionPipe = ApplicationName + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N");
                    using (NamedPipeClientStream discovery = await OpenPipeAsync(_pipeName, cancellationToken).ConfigureAwait(false))
                    using (var deadline = Deadline(cancellationToken))
                    {
                        try
                        {
                            var identity = new XElement("NamedPipeClientServerInformation", new XAttribute("Version", "1"),
                                new XElement("ComputerName", Environment.MachineName),
                                new XElement("ApplicationId", Environment.ProcessId),
                                new XElement("ApplicationName", ApplicationName),
                                new XElement("ApplicationPipeName", sessionPipe));
                            await WriteRequestAsync(discovery,"NewServer",EncodeString(identity.ToString(SaveOptions.DisableFormatting)),deadline.Token).ConfigureAwait(false);
                            XDocument response = DecodeBinaryXml(await ReadFrameAsync(discovery, _maxFrame, deadline.Token).ConfigureAwait(false));
                            if (response.Root?.Name != Io + "NewServerResultsData" || (string?)response.Root.Element(Io + "Result") != "Success")
                                throw new InvalidDataException("The running service refused the private catalog connection.");
                            await WriteStringAsync(discovery, "Disconnect", deadline.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        { throw new TimeoutException("PatternSmith discovery handshake timed out."); }
                    }
                }
                _pipe = await OpenPipeAsync(sessionPipe, cancellationToken).ConfigureAwait(false);
                ConnectedPipeName = sessionPipe;
                using (var deadline = Deadline(cancellationToken))
                {
                    try
                    {
                        if (_settleMilliseconds != 0) await Task.Delay(_settleMilliseconds, deadline.Token).ConfigureAwait(false);
                        await WriteRequestAsync(_pipe,"BeginApiHandshake",EncodeString("Version:"+ProtocolVersion+"\n"+string.Join("\n",CallbackMethods)),deadline.Token).ConfigureAwait(false);
                        WireReply reply = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                        if (reply.Method != "FinishApiHandshake" || reply.Text == null) throw new InvalidDataException("Expected FinishApiHandshake.");
                        string[] lines = reply.Text.Split('\n').Select(s => s.Trim()).Where(s => s.Length != 0).ToArray();
                        string[] versions = lines.Where(s => s.StartsWith("Version:", StringComparison.Ordinal)).ToArray();
                        if (versions.Length != 1 || versions[0] != "Version:" + ProtocolVersion)
                            throw new NotSupportedException("Unsupported PatternSmith material-library protocol version.");
                        foreach (string method in lines.Where(s => !s.StartsWith("Version:", StringComparison.Ordinal)))
                        {
                            if (method.IndexOf(':') >= 0 || method.Any(char.IsWhiteSpace)) throw new InvalidDataException("Malformed server API advertisement.");
                            _serverMethods.Add(method);
                        }
                        if (!_serverMethods.Contains("GetRootMaterialGroup")) throw new NotSupportedException("Server does not advertise GetRootMaterialGroup.");
                        _ready = true;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new TimeoutException("PatternSmith API handshake timed out."); }
                }
            }
            catch { Abort(); throw; }
            finally { _gate.Release(); }
        }

        // No public arbitrary-method API: only these catalog queries can be emitted.
        public Task<PatternSmithCatalogSnapshot> GetRootMaterialGroupAsync(CancellationToken cancellationToken = default)
            => QueryAsync("GetRootMaterialGroup", null, null, "ReadRootMaterialGroup", cancellationToken);

        public Task<PatternSmithCatalogSnapshot> GetMaterialTemplatesInGroupAsync(string groupPath, CancellationToken cancellationToken = default)
        {
            if (groupPath == null) throw new ArgumentNullException(nameof(groupPath));
            var document = new XDocument(new XElement(Data + "MaterialTemplatesInGroupData",
                new XAttribute(XNamespace.Xmlns + "i", Nil), new XAttribute(XNamespace.Xmlns + "io", Io),
                new XElement(Io + "ChangeEvents", new XAttribute(Nil + "nil", "true")),
                new XElement(Io + "ClientInformation", new XAttribute(Nil + "nil", "true")), new XElement(Data + "GroupPath", groupPath)));
            return QueryAsync("GetMaterialTemplatesInGroup", null, document, "ReadMaterialTemplatesFromGroup", cancellationToken);
        }

        public Task<PatternSmithCatalogSnapshot> GetMaterialTemplatesAsync(IEnumerable<string> materialIds, CancellationToken cancellationToken = default)
        {
            if (materialIds == null) throw new ArgumentNullException(nameof(materialIds));
            string[] ids = materialIds.Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0 || ids.Any(s => string.IsNullOrWhiteSpace(s) || s.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0))
                throw new ArgumentException("Supply nonempty, single-line material IDs.", nameof(materialIds));
            return QueryAsync("GetMaterialTemplates", string.Join("\n", ids), null, "ReadMaterialTemplates", cancellationToken);
        }

        private async Task<PatternSmithCatalogSnapshot> QueryAsync(string method, string? text, XDocument? data, string expected, CancellationToken cancellationToken)
        {
            byte[]? payload = text != null ? EncodeString(text) : data != null ? EncodeBinaryXml(data) : null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (!IsConnected) throw new InvalidOperationException("Connect before querying; do not reuse a failed session.");
                if (!_serverMethods.Contains(method)) throw new NotSupportedException("Server does not advertise " + method + ".");
                using (var deadline = Deadline(cancellationToken))
                {
                    try
                    {
                        await WriteRequestAsync(_pipe!,method,payload,deadline.Token).ConfigureAwait(false);
                        WireReply reply = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                        bool singleTemplate = method == "GetMaterialTemplates" && reply.Method == "ReadMaterialTemplate";
                        if ((!singleTemplate && reply.Method != expected) || reply.Document == null) throw new InvalidDataException("Unexpected catalog response method.");
                        XElement responseRoot = reply.Document.Root ?? throw new InvalidDataException("Catalog payload has no root.");
                        XName required = reply.Method switch
                        {
                            "ReadRootMaterialGroup" => Data + "RootMaterialGroupData",
                            "ReadMaterialTemplates" or "ReadMaterialTemplatesFromGroup" => Data + "MaterialTemplatesData",
                            _ => PatternSmithCatalogSnapshot.Nesting + "MaterialTemplate"
                        };
                        bool runtimeRollRoot = reply.Method == "ReadMaterialTemplate" && responseRoot.Name == PatternSmithCatalogSnapshot.Nesting + "RollTemplate";
                        if (responseRoot.Name != required && !runtimeRollRoot) throw new InvalidDataException("Unexpected catalog payload root.");
                        if (data != null && (string?)responseRoot.Element(Data + "GroupPath") != (string?)data.Root?.Element(Data + "GroupPath"))
                            throw new InvalidDataException("Catalog reply group does not match the request.");
                        return new PatternSmithCatalogSnapshot(reply.Document);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    { throw new TimeoutException("PatternSmith catalog query timed out."); }
                }
            }
            catch { Abort(); throw; }
            finally { _gate.Release(); }
        }

        private async Task<WireReply> ReceiveAsync(CancellationToken token)
        {
            for (int count = 0; count < MaximumControlFrames; count++)
            {
                string method = DecodeString(await ReadFrameAsync(_pipe!, _maxString, token).ConfigureAwait(false));
                switch (method)
                {
                    case "Ping": await WriteStringAsync(_pipe!, "Ping", token).ConfigureAwait(false); continue;
                    case "Disconnect": throw new IOException("PatternSmith server disconnected the catalog session.");
                    case "Reconnect": throw new IOException("PatternSmith requested reconnection; no automatic reconnection is performed.");
                    case "FinishApiHandshake":
                        return new WireReply(method, DecodeString(await ReadFrameAsync(_pipe!, _maxString, token).ConfigureAwait(false)), null);
                    case "ReadMessage":
                        DecodeBinaryXml(await ReadFrameAsync(_pipe!, _maxFrame, token).ConfigureAwait(false));
                        throw new IOException("PatternSmith returned a server message instead of a catalog response.");
                    case "ReadRootMaterialGroup": case "ReadMaterialTemplates": case "ReadMaterialTemplatesFromGroup": case "ReadMaterialTemplate":
                        return new WireReply(method, null, DecodeBinaryXml(await ReadFrameAsync(_pipe!, _maxFrame, token).ConfigureAwait(false)));
                    default: throw new InvalidDataException("Unadvertised or unsupported server response method.");
                }
            }
            throw new InvalidDataException("Excessive catalog control frames.");
        }

        private async Task<NamedPipeClientStream> OpenPipeAsync(string name, CancellationToken token)
        {
            var pipe = new NamedPipeClientStream(_host, name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(_connectTimeout);
            try { await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false); return pipe; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { pipe.Dispose(); throw new TimeoutException("No connection to existing PatternSmith pipe " + name + "."); }
            catch { pipe.Dispose(); throw; }
        }

        private CancellationTokenSource Deadline(CancellationToken token)
        { var result = CancellationTokenSource.CreateLinkedTokenSource(token); result.CancelAfter(_operationTimeout); return result; }

        private byte[] EncodeString(string value)
        {
            if (value.IndexOf('\0') >= 0 || WireEncoding.GetByteCount(value) > _maxString) throw new InvalidDataException("Outgoing string exceeds the catalog protocol limit.");
            return WireEncoding.GetBytes(value);
        }
        private static string DecodeString(byte[] bytes)
        {
            if (bytes.Length == 0 || bytes.Length % 2 != 0) throw new InvalidDataException("Invalid UTF-16LE frame length.");
            string value;
            try { value = WireEncoding.GetString(bytes); } catch (DecoderFallbackException ex) { throw new InvalidDataException("Invalid UTF-16LE content.", ex); }
            if (value.IndexOf('\0') >= 0) throw new InvalidDataException("NUL in catalog protocol string.");
            return value;
        }
        private Task WriteStringAsync(Stream stream,string value,CancellationToken token)=>WriteRequestAsync(stream,value,null,token);
        private async Task WriteRequestAsync(Stream stream,string method,byte[]? payload,CancellationToken token)
        {
            bool requiresData=method switch {
                "NewServer" or "BeginApiHandshake" or "GetMaterialTemplates" or "GetMaterialTemplatesInGroup"=>true,
                "GetRootMaterialGroup" or "Ping" or "Disconnect"=>false,
                _=>throw new ArgumentException("Not a read-only catalog/session method.",nameof(method))};
            if(requiresData!=(payload!=null))throw new ArgumentException("Wrong argument shape for catalog method.",nameof(payload));
            byte[] command=EncodeString(method);
            if(command.Length>_maxFrame||payload!=null&&(payload.Length==0||payload.Length>_maxFrame))throw new InvalidDataException("Outgoing catalog frame exceeds limit.");
            byte[] packet=new byte[checked(4+command.Length+(payload==null?0:4+payload.Length))];
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0,4),command.Length);command.CopyTo(packet,4);
            if(payload!=null){int offset=4+command.Length;BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset,4),payload.Length);payload.CopyTo(packet,offset+4);}
            // Never yield between an API method and its data frame: the server
            // can otherwise dispatch the data as another command.
            for(int offset=0;offset<packet.Length;offset+=CatalogWriteChunkBytes)
                await stream.WriteAsync(packet.AsMemory(offset,Math.Min(CatalogWriteChunkBytes,packet.Length-offset)),token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        private static async Task<byte[]> ReadFrameAsync(Stream stream, int limit, CancellationToken token)
        {
            byte[] header = new byte[4]; await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > limit) throw new InvalidDataException("Invalid or oversized catalog frame length.");
            byte[] payload = new byte[length]; await ReadExactlyAsync(stream, payload, token).ConfigureAwait(false); return payload;
        }
        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            { int read = await stream.ReadAsync(bytes.AsMemory(offset), token).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException("Truncated PatternSmith catalog frame."); offset += read; }
        }
        private byte[] EncodeBinaryXml(XDocument document)
        {
            using var stream = new MemoryStream();
            using (XmlDictionaryWriter writer = XmlDictionaryWriter.CreateBinaryWriter(stream, null, null, false)) { document.WriteTo(writer); writer.Flush(); }
            if (stream.Length > _maxFrame) throw new InvalidDataException("Outgoing catalog XML exceeds limit.");
            return stream.ToArray();
        }
        private XDocument DecodeBinaryXml(byte[] bytes)
        {
            var quotas = new XmlDictionaryReaderQuotas { MaxDepth = _xmlDepth, MaxStringContentLength = _maxFrame,
                MaxArrayLength = _maxFrame, MaxBytesPerRead = Math.Min(_maxFrame, 65536), MaxNameTableCharCount = Math.Min(_maxFrame, 1048576) };
            using XmlDictionaryReader reader = XmlDictionaryReader.CreateBinaryReader(bytes, quotas);
            // Count before materializing: repeated empty elements can be compact on the binary wire.
            int nodes = 0;
            while (reader.Read()) if (reader.NodeType == XmlNodeType.Element)
            { nodes = checked(nodes + 1 + reader.AttributeCount); if (nodes > _xmlNodes) throw new InvalidDataException("Catalog XML node limit exceeded."); }
            using XmlDictionaryReader second = XmlDictionaryReader.CreateBinaryReader(bytes, quotas);
            return XDocument.Load(second, LoadOptions.PreserveWhitespace);
        }
        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PatternSmithCatalogClient)); }
        private void Abort() { _ready = false; _serverMethods.Clear(); _pipe?.Dispose(); _pipe = null; ConnectedPipeName = null; }
        public void Dispose()
        {
            if(_disposed)return;_disposed=true;
            try
            {
                if(_ready&&_pipe?.IsConnected==true)
                {
                    using var deadline=new CancellationTokenSource(Math.Max(1,Config(nameof(CatalogDisconnectTimeoutMilliseconds),CatalogDisconnectTimeoutMilliseconds)));
                    WriteStringAsync(_pipe,"Disconnect",deadline.Token).GetAwaiter().GetResult();
                }
            }
            catch(Exception ex) when(ex is IOException||ex is OperationCanceledException||ex is InvalidOperationException)
            {Log.Write(nameof(vExportPatternSmith),"catalog session closed without disconnect acknowledgement: {0}",ex.Message);}
            finally{Abort();}
        }
        private sealed record WireReply(string Method, string? Text, XDocument? Document);
    }

    private sealed class PatternSmithCatalogSnapshot
    {
        public static readonly XNamespace Nesting = "http://autometrix.com/xml/nesting";
        private static readonly XNamespace References = "http://schemas.microsoft.com/2003/10/Serialization/";
        private readonly Dictionary<string, XElement> _objects = new Dictionary<string, XElement>(StringComparer.Ordinal);
        public XDocument Document { get; }
        public IReadOnlyList<PatternSmithCatalogMaterial> Materials { get; }
        public XElement? RootGroup { get; }
        public IReadOnlyList<PatternSmithCatalogGroup> Groups { get; }
        public PatternSmithCatalogSnapshot(XDocument document)
        {
            Document = document ?? throw new ArgumentNullException(nameof(document));
            if (document.Root == null || document.DocumentType != null) throw new InvalidDataException("Invalid catalog XML document.");
            foreach (XElement element in document.Root.DescendantsAndSelf())
            {
                string? id = (string?)element.Attribute(References + "Id");
                if (id != null && !_objects.TryAdd(id, element)) throw new InvalidDataException("Duplicate catalog object reference ID.");
            }
            foreach (XElement element in document.Root.DescendantsAndSelf()) Resolve(element);
            var materials = new List<PatternSmithCatalogMaterial>();
            foreach (XElement template in document.Root.DescendantsAndSelf().Where(e => e.Element(Nesting + "MaterialID") != null))
            {
                string id = Value(template.Element(Nesting + "MaterialID"));
                if (string.IsNullOrWhiteSpace(id)) throw new InvalidDataException("Catalog material has no business ID.");
                string name = Value(template.Element(Nesting + "Name"));
                var pattern = ResolveOptional(template.Element(Nesting + "MaterialPattern"));
                string widthText = Value(pattern?.Element(Nesting + "Width"));
                double? width = double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) && number > 0 ? number : null;
                materials.Add(new PatternSmithCatalogMaterial(id, name, width, template));
            }
            Materials = new ReadOnlyCollection<PatternSmithCatalogMaterial>(materials);
            XNamespace data = "http://schemas.datacontract.org/2004/07/Autometrix.Core.Nesting.MaterialLibrary.Data";
            XNamespace library = "http://autometrix.com/xml/nesting/MaterialLibrary";
            RootGroup = ResolveOptional(document.Root.Element(data + "Root") ?? document.Root.Element(library + "Root"));
            var groups = new List<PatternSmithCatalogGroup>();
            void Walk(XElement group, string path, HashSet<XElement> ancestry)
            {
                if (!ancestry.Add(group)) throw new InvalidDataException("Cyclic catalog group hierarchy.");
                groups.Add(new PatternSmithCatalogGroup(path, Value(group.Element(library + "Name")), group));
                XElement? children = ResolveOptional(group.Element(library + "Groups"));
                if (children != null) foreach (XElement child in children.Elements())
                {
                    XElement resolved = Resolve(child);
                    string name = Value(resolved.Element(library + "Name"));
                    Walk(resolved, path.Length == 0 ? name : path + "\\" + name, ancestry);
                }
                ancestry.Remove(group);
            }
            if (RootGroup != null) Walk(RootGroup, "", new HashSet<XElement>());
            Groups = groups.AsReadOnly();
        }
        public XElement Resolve(XElement element)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (element.Attribute(References + "Ref") is XAttribute reference)
            {
                if (!seen.Add(reference.Value) || !_objects.TryGetValue(reference.Value, out XElement? target))
                    throw new InvalidDataException("Dangling or cyclic catalog object reference.");
                element = target;
            }
            return element;
        }
        public XElement? ResolveOptional(XElement? element) => element == null ? null : Resolve(element);
        public string Value(XElement? element) => element == null ? "" : Resolve(element).Value;
    }

    // RawTemplate remains attached to Snapshot.Document: all namespaces, IDs and material properties survive.
    private sealed record PatternSmithCatalogMaterial(string MaterialId, string Name, double? CatalogWidthInches, XElement RawTemplate);
    private sealed record PatternSmithCatalogGroup(string Path, string Name, XElement RawGroup);

    private sealed class PatternSmithCatalogPreferences
    {
        public IReadOnlyList<string> ServerHosts { get; private set; } = Array.Empty<string>();
        public string? CurrentLibraryPath { get; private set; }
        public string? PatternSmith10LibraryPath { get; private set; }
        public IReadOnlyList<string> LibraryFallbackPaths => Array.AsReadOnly(new[] { CurrentLibraryPath, PatternSmith10LibraryPath }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        public IReadOnlyList<string> Diagnostics { get; private set; } = Array.Empty<string>();

        // commonApplicationData can be supplied for fixtures; production uses the relocatable OS folder.
        // Explicit library paths take precedence. Missing/malformed settings never cause settings creation.
        public static PatternSmithCatalogPreferences Read(string? commonApplicationData = null,
            string? currentLibraryOverride = null, string? patternSmith10LibraryOverride = null, string? serverHostOverride = null)
        {
            string root = commonApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var diagnostics = new List<string>();
            string? Leaf(string path, string name)
            {
                if (!File.Exists(path)) return null;
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4194304 };
                    using var reader = XmlReader.Create(path, settings);
                    var document = XDocument.Load(reader);
                    return document.Descendants().FirstOrDefault(e => e.Name.LocalName == name && !e.HasElements)?.Value;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
                { diagnostics.Add("Unable to read " + Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path) + ": " + ex.GetType().Name); return null; }
            }
            string vendor = Path.Combine(root, "Autometrix");
            string? configuredHost = serverHostOverride ?? Leaf(Path.Combine(vendor, "PatternSmith11", "user.settings"), "MaterialLibraryServerName");
            string? current = currentLibraryOverride ?? Leaf(Path.Combine(vendor, "MaterialLibrary.Server", "user.settings"), "LibraryLocation");
            string? legacy = patternSmith10LibraryOverride ?? Leaf(Path.Combine(vendor, "Material.library.preferences"), "LibraryLocation");
            return new PatternSmithCatalogPreferences
            {
                ServerHosts = Array.AsReadOnly(new[] { string.IsNullOrWhiteSpace(configuredHost) ? PatternSmithCatalogClient.DefaultHost : configuredHost.Trim() }),
                CurrentLibraryPath = Normalize(current), PatternSmith10LibraryPath = Normalize(legacy), Diagnostics = diagnostics.AsReadOnly()
            };
        }
        private static string? Normalize(string? path) => string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path.Trim());
    }

    private sealed record PatternSmithCatalogReadResult(string Source, IReadOnlyList<PatternSmithCatalogSnapshot> Snapshots,
        IReadOnlyList<PatternSmithCatalogMaterial> Materials, IReadOnlyList<string> Diagnostics);

    private static class PatternSmithCatalogLoader
    {
        // File parsing is supplied by the parent; this routine never opens a vendor library object.
        // The delegate must open ZIP/XML read-only, prohibit DTDs and bound its decompressed input.
        public static async Task<PatternSmithCatalogReadResult> ReadAsync(PatternSmithCatalogPreferences preferences,
            Func<string, CancellationToken, Task<XDocument>>? readLibraryXml = null,
            string pipeName = PatternSmithCatalogClient.DefaultPipeName,
            int connectTimeoutMilliseconds = PatternSmithCatalogClient.DefaultConnectTimeoutMilliseconds,
            int operationTimeoutMilliseconds = PatternSmithCatalogClient.DefaultOperationTimeoutMilliseconds,
            CancellationToken cancellationToken = default)
        {
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            var diagnostics = new List<string>(preferences.Diagnostics);
            foreach (string host in preferences.ServerHosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var client = new PatternSmithCatalogClient(host, pipeName, connectTimeoutMilliseconds, operationTimeoutMilliseconds,
                    Config(nameof(CatalogMaximumFrameBytes),CatalogMaximumFrameBytes),Config(nameof(CatalogMaximumStringBytes),CatalogMaximumStringBytes),
                    Config(nameof(CatalogMaximumXmlDepth),CatalogMaximumXmlDepth),Config(nameof(CatalogMaximumXmlNodes),CatalogMaximumXmlNodes),
                    handshakeSettleMilliseconds:Math.Clamp(Config(nameof(CatalogConnectionSettleMilliseconds),CatalogConnectionSettleMilliseconds),0,2000));
                try
                {
                    await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    PatternSmithCatalogSnapshot root = await client.GetRootMaterialGroupAsync(cancellationToken).ConfigureAwait(false);
                    var snapshots = new List<PatternSmithCatalogSnapshot> { root };
                    // Native root normally contains the entire catalog. Group queries are only a fallback.
                    if (root.Materials.Count == 0)
                        foreach (PatternSmithCatalogGroup group in root.Groups.Where(g => g.Path.Length != 0))
                            snapshots.Add(await client.GetMaterialTemplatesInGroupAsync(group.Path, cancellationToken).ConfigureAwait(false));
                    return Result("server:" + client.Endpoint, snapshots, diagnostics);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is TimeoutException || ex is UnauthorizedAccessException || ex is NotSupportedException || ex is XmlException)
                { diagnostics.Add("Server " + host + ": " + ex.GetType().Name + ": " + ex.Message); }
            }
            if (readLibraryXml != null)
                foreach (string path in preferences.LibraryFallbackPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        XDocument document = await readLibraryXml(path, cancellationToken).ConfigureAwait(false);
                        var snapshot = new PatternSmithCatalogSnapshot(document);
                        return Result("library:" + path, new[] { snapshot }, diagnostics);
                    }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is XmlException)
                    { diagnostics.Add("Library " + path + ": " + ex.GetType().Name + ": " + ex.Message); }
                }
            throw new IOException("No read-only PatternSmith catalog source is available. " + string.Join("; ", diagnostics));
        }
        private static PatternSmithCatalogReadResult Result(string source, IEnumerable<PatternSmithCatalogSnapshot> snapshots, List<string> diagnostics)
        {
            PatternSmithCatalogSnapshot[] documents = snapshots.ToArray();
            // Keep each template attached to its own document so z:Id scopes cannot collide across replies.
            PatternSmithCatalogMaterial[] materials = documents.SelectMany(s => s.Materials).GroupBy(m => m.MaterialId, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            return new PatternSmithCatalogReadResult(source, Array.AsReadOnly(documents), Array.AsReadOnly(materials), diagnostics.AsReadOnly());
        }
    }

}
