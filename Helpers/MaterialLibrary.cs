using System.Text.Json.Nodes;
using ToolsOptionStore = vTools.Commands.ToolsOptionStore;

namespace vTools;

/// <summary>Global named materials and unsaved drafts shared by nesting and the material editor.</summary>
internal static class MaterialLibrary
{
  // Defaults and storage compatibility
  internal const int MaterialPresetNameLimit = 80; // Maximum normalized preset-name length in characters.
  private const string Section = "vNest"; // Existing profile section retained for migration from installed versions.
  private const string MaterialPresetsKey = "materialPresets"; // Saved material records in the version-one global database.
  private const string MaterialDraftsKey = "materialDrafts"; // Unsaved material records, independent of saved presets.
  private const string RetiredMaterialsKey = "retiredMaterials"; // Names explicitly removed from the library; prevents drawing recovery from restoring them.
  private const string MaterialDatabaseFolder = "vTools"; // Per-user roaming application-data subfolder shared across Rhino versions.
  private const string MaterialDatabaseFileName = "materials.json"; // Existing global material-library filename.
  private const string MaterialDatabaseMutexName = "Local\\vTools.MaterialDatabase"; // Cross-process Windows-session transaction mutex.
  [CommandDefault("vNest", "vTools.Commands.NestWorkflow.MaterialDatabaseLockMilliseconds", "Maximum milliseconds waiting for another Rhino process to finish a material transaction.")]
  private const int MaterialDatabaseLockMilliseconds = 5000; // Positive maximum wait in milliseconds for a library transaction.
  private static readonly int ConfiguredMaterialDatabaseLockMilliseconds = CommandDefaults.Get("vNest", "vTools.Commands.NestWorkflow.MaterialDatabaseLockMilliseconds", MaterialDatabaseLockMilliseconds); // Existing advanced override, resolved once at startup.
  private const int MaterialDatabaseVersion = 1; // Supported JSON schema revision; unknown versions are left unchanged.
  private const string MaterialDatabaseMigratedKey = "materialDatabaseMigrated"; // true means legacy presets were imported; false requests a non-overwriting merge.
  private const string CustomMaterialName = "Custom"; // Reserved placeholder, never a saved material name.
  private const double DefaultGrainDeviation = 0; // Grain deviation in degrees when omitted by a material constructor.
  private const double MaximumGrainDeviation = 90; // Largest accepted deviation in degrees.
  private static readonly double ConfiguredDefaultGrainDeviation = CommandDefaults.Get("vNest", "vTools.Commands.NestWorkflow.DefaultGrainDeviation", DefaultGrainDeviation); // Inherited allowed angular deviation in degrees.
  private static readonly GrainMode ConfiguredDefaultGrain = CommandDefaults.Get("vNest", "vTools.Commands.NestWorkflow.DefaultGrain", GrainMode.Auto); // Auto detects guides; Force requests missing guides; On falls back to source axes; Off ignores guides.
  private static readonly bool ConfiguredDefaultObeyGrain = CommandDefaults.Get("vNest", "vTools.Commands.NestWorkflow.DefaultObeyGrain", true); // true obeys grain constraints; false uses free rotation.
  private static readonly bool ConfiguredDefaultAllowReverse = CommandDefaults.Get("vNest", "vTools.Commands.NestWorkflow.DefaultAllowReverse", true); // true allows opposite grain alignment; false only allows forward alignment.

  internal enum GrainMode { Auto, Force, On, Off }

  internal sealed record Snapshot(IReadOnlyList<MaterialPreset> Presets,IReadOnlyList<MaterialPreset> Drafts);
  internal static event Action? Changed;

  internal static bool TrySnapshot(out Snapshot snapshot,out string error)
  {
    try {snapshot=MaterialDatabase.ReadStrict(ReadSnapshot);error="";return true;}
    catch(Exception ex) {snapshot=new([],[]);error=ex.Message;Log.Write("Materials","read failed: {0}",ex);return false;}
  }

  private static Snapshot ReadSnapshot(JsonObject? root)
  {
    foreach(string key in new[]{MaterialPresetsKey,MaterialDraftsKey})
      if(root?.ContainsKey(key)==true&&root[key] is not JsonArray)
        throw new System.IO.InvalidDataException("The material library lists must be JSON arrays; the file was left unchanged.");
    var snapshot=new Snapshot(ReadMaterialPresets(root),ReadMaterialRecords(root,MaterialDraftsKey));
    if(root?[MaterialPresetsKey] is JsonArray presets&&presets.Count!=snapshot.Presets.Count||
      root?[MaterialDraftsKey] is JsonArray drafts&&drafts.Count!=snapshot.Drafts.Count)
      throw new System.IO.InvalidDataException("The material library has invalid or duplicate entries; it was left unchanged.");
    return snapshot;
  }

  internal static bool SaveSnapshot(Snapshot expected,IEnumerable<MaterialPreset> materials,out string error)
  {
    var desired=materials.Select(material=>material with {Name=MaterialPresetName(material.Name)}).ToArray();
    error="";
    foreach(var material in desired)if(!Valid(material,out error))return false;
    if(desired.DistinctBy(material=>material.Name,StringComparer.OrdinalIgnoreCase).Count()!=desired.Length)
    {error="Material names must be unique.";return false;}
    bool saved=MaterialDatabase.Update(root=>ApplySnapshot(root,expected,desired),out error);
    if(saved)Changed?.Invoke();
    else Log.Write("Materials","save failed: {0}",error);
    return saved;
  }

  private static void ApplySnapshot(JsonObject root,Snapshot expected,IReadOnlyList<MaterialPreset> desired)
  {
    var current=ReadSnapshot(root);
    if(!current.Presets.SequenceEqual(expected.Presets)||!current.Drafts.SequenceEqual(expected.Drafts))
      throw new InvalidOperationException("Materials changed in another window or Rhino session. Reload before saving.");
    var names=desired.Select(material=>material.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var retired=RetiredNames(root);
    retired.UnionWith(expected.Presets.Concat(expected.Drafts).Select(material=>material.Name).Where(name=>!names.Contains(name)));
    retired.ExceptWith(names);
    root[RetiredMaterialsKey]=new JsonArray(retired.Order(StringComparer.OrdinalIgnoreCase).Select(name=>(JsonNode)JsonValue.Create(name)!).ToArray());
    root[MaterialPresetsKey]=MaterialPresetsJson(desired);
    root[MaterialDraftsKey]=new JsonArray();
  }

  internal static bool Valid(MaterialPreset values,out string error)
  {
    string name=MaterialPresetName(values.Name);error="";
    if(name.Length==0||name.Length>MaterialPresetNameLimit||name.Equals(CustomMaterialName,StringComparison.OrdinalIgnoreCase))
    {error=$"Use a material name of 1-{MaterialPresetNameLimit} characters other than {CustomMaterialName}.";return false;}
    if(!double.IsFinite(values.Width)||values.Width<=0||!double.IsFinite(values.Gap)||values.Gap<0||
      !double.IsFinite(values.GrainDeviation)||values.GrainDeviation<0||values.GrainDeviation>MaximumGrainDeviation||!Enum.IsDefined(values.Grain))
    {error="Width must be positive, Gap nonnegative, and grain deviation between 0 and 90 degrees.";return false;}
    return true;
  }

  private static HashSet<string> RetiredNames(JsonObject root)=>root[RetiredMaterialsKey] is JsonArray names
    ?names.OfType<JsonValue>().Select(value=>value.TryGetValue<string>(out var name)?name:"").Where(name=>name.Length>0).ToHashSet(StringComparer.OrdinalIgnoreCase)
    :new(StringComparer.OrdinalIgnoreCase);

  internal sealed record MaterialPreset(string Name,double Width,double Gap,double GrainDeviation=DefaultGrainDeviation)
  {
    public PatternSmithReference? PatternSmith { get; init; } // Optional catalog identity; kept independently of locally customized nesting dimensions.
    public GrainMode Grain { get; init; }=ConfiguredDefaultGrain; // Auto, Force, On or Off, inherited by parts without a mode override.
    public bool ObeyGrain { get; init; }=ConfiguredDefaultObeyGrain; // true constrains inherited grain; false permits free-rotation rules.
    public bool AllowReverse { get; init; }=ConfiguredDefaultAllowReverse; // true permits opposite grain alignment; false only permits forward alignment.
  }
  internal sealed record PatternSmithReference(string Id,string Name,string Group,string Catalog);
  internal static JsonObject? PatternSmithJson(PatternSmithReference? source)=>source==null?null:
    new JsonObject {["id"]=source.Id,["name"]=source.Name,["group"]=source.Group,["catalog"]=source.Catalog};
  internal static PatternSmithReference? ReadPatternSmith(JsonNode? node)=>node is JsonObject value&&
    ToolsOptionStore.TryGetString(value,"id",out var id)&&!string.IsNullOrWhiteSpace(id)&&
    ToolsOptionStore.TryGetString(value,"name",out var name)&&!string.IsNullOrWhiteSpace(name)&&
    ToolsOptionStore.TryGetString(value,"catalog",out var catalog)&&!string.IsNullOrWhiteSpace(catalog)
    ?new(id,name,ToolsOptionStore.TryGetString(value,"group",out var group)?group:"",catalog):null;
  internal static IReadOnlyDictionary<string,string> PatternSmithCustomIds(IEnumerable<string> names,IEnumerable<string> reservedIds)
  {
    return Commands.PatternSmithMaterialIdentity.Allocate(names,reservedIds,new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
  }
  internal static System.Text.Json.Nodes.JsonArray MaterialPresetsJson(IEnumerable<MaterialPreset> presets)=>
    new(presets.Select(preset=>(System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
      {["name"]=preset.Name,["width"]=preset.Width,["gap"]=preset.Gap,["grainDeviation"]=preset.GrainDeviation,
        ["grain"]=preset.Grain.ToString(),["obeyGrain"]=preset.ObeyGrain,["allowReverse"]=preset.AllowReverse,
        ["patternSmith"]=PatternSmithJson(preset.PatternSmith)}).ToArray());
  internal static string MaterialPresetName(string name)=>System.Text.RegularExpressions.Regex.Replace(name,@"\s+"," ").Trim();
  internal static List<MaterialPreset> ReadMaterialPresets(System.Text.Json.Nodes.JsonObject? section)
    =>ReadMaterialRecords(section,MaterialPresetsKey);
  internal static List<MaterialPreset> ReadMaterialRecords(System.Text.Json.Nodes.JsonObject? section,string key)
  {
    var result=new List<MaterialPreset>();
    if(section?[key] is not System.Text.Json.Nodes.JsonArray presets) return result;
    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var legacyGrain=ToolsOptionStore.TryGetString(section,"grain",out var legacyMode)&&Enum.TryParse<GrainMode>(legacyMode,true,out var legacyParsed)&&Enum.IsDefined(legacyParsed)?legacyParsed:ConfiguredDefaultGrain;
    bool legacyObey=ToolsOptionStore.TryGetBool(section,"obeyGrain",out var legacyObeyValue)?legacyObeyValue:ConfiguredDefaultObeyGrain;
    bool legacyReverse=ToolsOptionStore.TryGetBool(section,"allowReverse",out var legacyReverseValue)?legacyReverseValue:ConfiguredDefaultAllowReverse;
    foreach(var node in presets)
    {
      if(node is not System.Text.Json.Nodes.JsonObject preset||!ToolsOptionStore.TryGetString(preset,"name",out var name)) continue;
      name=MaterialPresetName(name);
      if(name.Length==0||name.Length>MaterialPresetNameLimit||name.Equals(CustomMaterialName,StringComparison.OrdinalIgnoreCase)||
        !ToolsOptionStore.TryGetDouble(preset,"width",out var width)||!double.IsFinite(width)||width<=0||
        !ToolsOptionStore.TryGetDouble(preset,"gap",out var gap)||!double.IsFinite(gap)||gap<0||!names.Add(name)) continue;
      double deviation=ConfiguredDefaultGrainDeviation;
      if(preset.ContainsKey("grainDeviation")&&(!ToolsOptionStore.TryGetDouble(preset,"grainDeviation",out deviation)||
        !double.IsFinite(deviation)||deviation<0||deviation>MaximumGrainDeviation)) continue;
      var mode=legacyGrain;bool obey=legacyObey,reverse=legacyReverse;
      if(preset.ContainsKey("grain")&&(!ToolsOptionStore.TryGetString(preset,"grain",out var grain)||!Enum.TryParse(grain,true,out mode)||!Enum.IsDefined(mode))) continue;
      if(preset.ContainsKey("obeyGrain")&&!ToolsOptionStore.TryGetBool(preset,"obeyGrain",out obey)) continue;
      if(preset.ContainsKey("allowReverse")&&!ToolsOptionStore.TryGetBool(preset,"allowReverse",out reverse)) continue;
      result.Add(new MaterialPreset(name,width,gap,deviation) {Grain=mode,ObeyGrain=obey,AllowReverse=reverse,PatternSmith=ReadPatternSmith(preset["patternSmith"])});
    }
    return result.OrderBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase).ToList();
  }
  internal static List<MaterialPreset> LoadMaterialPresets(IEnumerable<MaterialPreset>? recovered=null)=>MaterialDatabase.Read(ReadMaterialPresets,recovered);
  internal static List<MaterialPreset> LoadMaterialDrafts()=>MaterialDatabase.Read(section=>ReadMaterialRecords(section,MaterialDraftsKey));
  internal static bool SaveMaterialDrafts(IEnumerable<MaterialPreset> drafts,IEnumerable<MaterialPreset> baseline)
  {
    var current=drafts.ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
    var previous=baseline.ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
    var changed=current.Values.Where(preset=>!previous.TryGetValue(preset.Name,out var old)||preset!=old).ToArray();
    var removed=previous.Keys.Except(current.Keys,StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(changed.Length==0&&removed.Count==0)return true;
    bool saved=MaterialDatabase.Update(section=>
    {
      var stored=ReadMaterialRecords(section,MaterialDraftsKey).Where(preset=>!removed.Contains(preset.Name))
        .ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
      foreach(var preset in changed)stored[preset.Name]=preset;
      section[MaterialDraftsKey]=MaterialPresetsJson(stored.Values);
    },out var error);
    if(!saved)Log.Write("vNest","could not retain unsaved material edits: {0}",error);
    return saved;
  }
  internal static bool SaveMaterialValues(MaterialPreset values,bool overwrite,out string error)
  {
    string name=MaterialPresetName(values.Name);
    if(!Valid(values,out error))return false;
    bool saved=MaterialDatabase.Update(section=>
    {
      var presets=ReadMaterialPresets(section);
      int index=presets.FindIndex(preset=>preset.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
      if(index<0&&overwrite) throw new InvalidOperationException("The selected material preset no longer exists. Use Save As instead.");
      if(index>=0&&!overwrite) throw new InvalidOperationException("That material name already exists. Choose another name or overwrite the selected preset.");
      if(index<0) presets.Add(values with {Name=name});
      else presets[index]=values with {Name=presets[index].Name,PatternSmith=values.PatternSmith??presets[index].PatternSmith};
      section[MaterialPresetsKey]=MaterialPresetsJson(presets);
      section[MaterialDraftsKey]=MaterialPresetsJson(ReadMaterialRecords(section,MaterialDraftsKey)
        .Where(draft=>!draft.Name.Equals(name,StringComparison.OrdinalIgnoreCase)));
      var retired=RetiredNames(section);retired.Remove(name);
      section[RetiredMaterialsKey]=new JsonArray(retired.Select(value=>(JsonNode)JsonValue.Create(value)!).ToArray());
    },out error);
    if(!saved)
      Log.Write("vNest","material preset save failed: {0}",error);
    return saved;
  }

  private static class MaterialDatabase
  {
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions=new() {WriteIndented=true,
      TypeInfoResolver=new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()};
    private static string DatabasePath=>System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
      MaterialDatabaseFolder,MaterialDatabaseFileName);

    internal static T Read<T>(Func<System.Text.Json.Nodes.JsonObject?,T> reader,IEnumerable<MaterialPreset>? recovered=null)
    {
      try {return Access(DatabasePath,reader,false,recovered);}
      catch(Exception ex)
      {
        Log.Write("vNest","global material library read failed: {0}",ex.Message);
        return ToolsOptionStore.Read(Section,reader);
      }
    }
    internal static T ReadStrict<T>(Func<JsonObject?,T> reader)=>Access(DatabasePath,reader,false);
    internal static bool Update(Action<System.Text.Json.Nodes.JsonObject> updater,out string error)
    {
      try {Access(DatabasePath,section=>{updater(section!);return true;},true);error=string.Empty;return true;}
      catch(Exception ex) {error=ex.Message;return false;}
    }
    private static T Access<T>(string path,Func<System.Text.Json.Nodes.JsonObject?,T> action,bool write,IEnumerable<MaterialPreset>? recovered=null)
    {
      using var mutex=new Mutex(false,MaterialDatabaseMutexName);bool held=false;
      try
      {
        try {held=mutex.WaitOne(ConfiguredMaterialDatabaseLockMilliseconds);}
        catch(AbandonedMutexException) {held=true;}
        if(!held)throw new System.IO.IOException("The material library is busy in another Rhino session. Try again.");
        bool exists=System.IO.File.Exists(path);
        var root=exists?System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
          ??throw new System.IO.InvalidDataException("The material library must contain a JSON object."):new System.Text.Json.Nodes.JsonObject();
        if(exists&&(!ToolsOptionStore.TryGetDouble(root,"version",out double version)||version!=MaterialDatabaseVersion))
          throw new System.IO.InvalidDataException("The material library version is not supported; the file was left unchanged.");
        if(exists)ReadSnapshot(root);
        var legacy=ToolsOptionStore.Read(Section,section=>section==null?null:
          System.Text.Json.Nodes.JsonNode.Parse(section.ToJsonString()) as System.Text.Json.Nodes.JsonObject);
        bool migrate=legacy!=null&&(!ToolsOptionStore.TryGetBool(legacy,MaterialDatabaseMigratedKey,out bool migrated)||!migrated);
        if(!exists||migrate)
        {
          var retired=RetiredNames(root);
          foreach(string key in new[]{MaterialPresetsKey,MaterialDraftsKey})
            root[key]=MaterialPresetsJson(ReadMaterialRecords(root,key).Concat(ReadMaterialRecords(legacy,key))
              .Where(preset=>!retired.Contains(preset.Name))
              .DistinctBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase));
          root["version"]=MaterialDatabaseVersion;
        }
        int recoveredCount=0;
        if(recovered!=null)
        {
          var presets=ReadMaterialPresets(root);var names=presets.Select(preset=>preset.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
          var records=new System.Text.Json.Nodes.JsonObject {[MaterialPresetsKey]=MaterialPresetsJson(recovered)};
          var retired=RetiredNames(root);
          var missing=ReadMaterialPresets(records).Where(preset=>!retired.Contains(preset.Name)&&names.Add(preset.Name)).ToArray();
          recoveredCount=missing.Length;
          if(recoveredCount>0)root[MaterialPresetsKey]=MaterialPresetsJson(presets.Concat(missing));
        }
        T result=action(root);
        if(write||!exists||migrate||recoveredCount>0)Save(path,root);
        if(recoveredCount>0)Log.Write("vNest","recovered {0} into the global library",CountText.Format(recoveredCount,"drawing material preset"));
        // Mark only after a durable import; retain the legacy arrays as a recovery copy.
        if(migrate&&!ToolsOptionStore.Update(Section,section=>section[MaterialDatabaseMigratedKey]=true))
          Log.Write("vNest","material library migrated, but the legacy import marker could not be saved: {0}",ToolsOptionStore.LastError);
        return result;
      }
      finally {if(held)mutex.ReleaseMutex();}
    }
    private static void Save(string path,System.Text.Json.Nodes.JsonObject root)
    {
      System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
      string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
      try
      {
        byte[] json=System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
        using(var stream=new System.IO.FileStream(temporary,System.IO.FileMode.CreateNew,System.IO.FileAccess.Write,System.IO.FileShare.None))
        {stream.Write(json);stream.Flush(true);}
        if(System.IO.File.Exists(path))System.IO.File.Replace(temporary,path,path+".bak");
        else System.IO.File.Move(temporary,path);
      }
      finally {if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
    }
  }

}

