using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>Exports selected geometry, preserving Rhino's current DXF scheme and group blocks.</summary>
public sealed partial class vExportDXF : vToolsCommand
{
  // Defaults and output conventions
  private const NotchTrimMode DefaultNotchTrim = NotchTrimMode.Split; // No keeps touched export curves; Split moves between-leg pieces; Trim omits them.
  private const string DefaultNotchTrimLayer = "Reference"; // Rhino layer path for between-leg export pieces in Split mode.
  private const bool DefaultNotchJoin = true; // true joins matching V/U notches into line/polyline export curves; false exports notch components separately.
  private const bool DefaultTextBreak = true; // true exports each multiline text row separately; false keeps multiline text objects intact.
  private const bool DefaultOptimize = false; // true runs vCleanup before export; false leaves the source document untouched.
  private const bool DefaultSaveDefaults = false; // true persists scripted option changes; false applies them to this export only.
  private static readonly string[] NotchTrimNames = ["No", "Split", "Trim"]; // Rhino option order matching NotchTrimMode.
  private const double NotchContactToleranceScale = 2.0; // Document absolute-tolerance multiplier for notch leg contact.
  private const double NotchContactZeroToleranceScale = 10.0; // Minimum Rhino zero-tolerance multiplier for contact.
  private const string NotchMetadataPrefix = "notch."; // Metadata prefix stored on vNotches geometry.
  private const string NotchRole = "notch"; // Role value that distinguishes notch components from labels and source curves.
  private const string OpenVNotchType = "\\/"; // Two-component V notch type in vNotches metadata.
  private const string SettingsSection = "vExportDXF"; // Global settings section for this command.
  private const string LegacySettingsSection = "vExportOptions"; // Prior settings section retained as a read-only fallback.
  private const string DocumentSection = "vTools"; // Rhino document-string section for export settings.
  private const string NotchTrimEntry = "vExportDXF.NotchTrim"; // Document setting key for the export notch treatment.
  private const string NotchTrimLayerEntry = "vExportDXF.NotchTrimLayer"; // Document setting key for the export split layer.
  private const string NotchJoinEntry = "vExportDXF.NotchJoin"; // Document setting key for joining split/trimmed notch geometry in exported copies.
  private const string TextBreakEntry = "vExportDXF.TextBreak"; // Document setting key for splitting multiline export text.
  private const string OptimizeEntry = "vExportDXF.Optimize"; // Document setting key for export cleanup.
  private const string LegacyNotchTrimEntry = "vExportOptions.NotchTrim"; // Previous document key read for migration.
  private const string LegacyNotchTrimLayerEntry = "vExportOptions.NotchTrimLayer"; // Previous document key read for migration.
  private const string LegacyOptimizeEntry = "vExportOptions.Optimize"; // Previous document key read for migration.
  private const string LastPathKey = ".DXF_export.LastPath"; // Document user-text key compatible with the Python exporter.
  private const string BlockInsertLayer = "Reference"; // Layer for temporary group-block inserts.
  private const string DefaultFileStem = "untitled"; // Suggested filename stem for an unsaved Rhino document.
  private const string DxfExtension = ".dxf"; // Required output filename extension.
  private const int ExportWaitMilliseconds = 2000; // Maximum wait for Rhino to finish writing the temporary file.
  private const int ExportPollMilliseconds = 50; // File-existence polling interval in milliseconds.
  private const int MaximumTemporaryNameAttempts = 100; // Collision attempts for temporary files and block names.
  private const int MaximumBlockNameLength = 48; // Maximum ASCII characters retained from a source group name.
  private const int BlockNamePrefixLength = 39; // Maximum retained stem before an eight-character collision suffix.
  private static readonly Guid AcadExportPluginId =
    new("39a88493-9e97-4f15-bd62-ad25896a2632"); // Rhino's ACAD export plug-in settings ID.

  public override string EnglishName => "vExportDXF";

  private readonly record struct ExportSettings(
    NotchTrimMode NotchTrim, string NotchTrimLayer, bool NotchJoin, bool TextBreak, bool Optimize);

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var settings = LoadSettings(doc);
    if (mode == RunMode.Scripted)
    {
      var optionResult = GetRunOptions(doc, ref settings);
      if (optionResult != Result.Success)
        return optionResult;
    }
    var selection = doc.Objects.GetSelectedObjects(false, false)
      .Where(obj => obj != null).GroupBy(obj => obj.Id).Select(group => group.First())
      .ToArray();
    var targets = selection.Length > 0 ? selection : doc.Objects.GetObjectList(
      new ObjectEnumeratorSettings
      {
        NormalObjects = true,
        LockedObjects = false,
        HiddenObjects = false,
        IncludeLights = false,
        IncludeGrips = false,
        IncludePhantoms = false,
      }).Where(obj => obj != null).ToArray();
    if (targets.Length == 0)
    {
      RhinoApp.WriteLine("vExportDXF: no objects to export.");
      return Result.Nothing;
    }
    if (!TryChoosePath(doc, out string path))
      return Result.Cancel;
    if (settings.Optimize)
    {
      var exportScope = targets.Select(obj => obj.Id).ToHashSet();
      var result = vCleanup.OptimizeForExport(doc, exportScope);
      if (result != Result.Success)
        return result;
      targets = exportScope
        .Select(id => doc.Objects.FindId(id))
        .Where(obj => obj != null)
        .Cast<RhinoObject>()
        .ToArray();
      if (targets.Length == 0)
      {
        RhinoApp.WriteLine("vExportDXF: Optimize removed all exportable objects.");
        return Result.Nothing;
      }
    }
    string? tempPath = null;
    var originalSelection = selection.Select(obj => obj.Id).ToArray();
    var temporaryObjects = new List<Guid>();
    var temporaryDefinitions = new List<int>();
    var notchTrimOverrides = new Dictionary<Guid, List<ExportCurvePiece>>();
    var textBreakOverrides = new Dictionary<Guid, List<TextEntity>>();
    try
    {
      var options = CurrentDxfWriteOptions();
      tempPath = MakeTemporaryPath(path);
      notchTrimOverrides = BuildNotchTrimOverrides(
        doc, targets, settings.NotchTrim, settings.NotchTrimLayer, settings.NotchJoin);
      textBreakOverrides = BuildTextBreakOverrides(doc, targets, settings.TextBreak);
      var translation = selection.Length > 0
        ? OriginTranslation(targets)
        : Transform.Identity;
      var exportIds = PrepareExportObjects(
        doc, targets, translation, notchTrimOverrides, textBreakOverrides,
        temporaryObjects, temporaryDefinitions);
      if (exportIds.Count == 0)
        throw new InvalidOperationException("No exportable objects were prepared.");

      doc.Objects.UnselectAll();
      int selectedCount = 0;
      foreach (Guid id in exportIds)
        if (doc.Objects.Select(id)) selectedCount++;
      if (selectedCount == 0)
        throw new InvalidOperationException("No prepared objects could be selected.");
      if (!doc.ExportSelected(tempPath, options.ToDictionary()))
        throw new InvalidOperationException("Rhino DXF export failed.");
      if (!WaitForFile(tempPath))
        throw new IOException("Rhino did not create a nonempty DXF file.");
      if (File.Exists(path))
        File.Replace(tempPath, path, null);
      else
        File.Move(tempPath, path);
      tempPath = null;
      doc.Strings.SetString(LastPathKey, path);
      RhinoApp.WriteLine($"vExportDXF: exported {targets.Length} object(s) to {path}.");
      Log.Write("vExportDXF", $"exported source={targets.Length} prepared={selectedCount} " +
        $"groups={temporaryDefinitions.Count} textBreak={settings.TextBreak} " +
        $"textObjectsSplit={textBreakOverrides.Count} textLines={textBreakOverrides.Values.Sum(lines => lines.Count)} " +
        $"selectedOnly={selection.Length > 0} path={path}");
      return Result.Success;
    }
    catch (Exception ex)
    {
      Log.Write("vExportDXF", $"export failed: {ex}");
      RhinoApp.WriteLine($"vExportDXF: {ex.Message}");
      return Result.Failure;
    }
    finally
    {
      doc.Objects.UnselectAll();
      for (int index = temporaryObjects.Count - 1; index >= 0; index--)
        doc.Objects.Delete(temporaryObjects[index], quiet: true);
      for (int index = temporaryDefinitions.Count - 1; index >= 0; index--)
        doc.InstanceDefinitions.Delete(temporaryDefinitions[index], false, true);
      foreach (Guid id in originalSelection)
        doc.Objects.Select(id);
      doc.Views.Redraw();
      if (tempPath != null)
      {
        try { File.Delete(tempPath); }
        catch (Exception ex) { Log.Write("vExportDXF", $"temporary file cleanup failed: {ex}"); }
      }
      foreach (var pieces in notchTrimOverrides.Values)
        foreach (var piece in pieces)
          piece.Geometry.Dispose();
      DisposeTextBreakOverrides(textBreakOverrides);
    }
  }

  private static ExportSettings LoadSettings(RhinoDoc doc)
  {
    var legacy = ToolsOptionStore.Read(LegacySettingsSection, section =>
      new ExportSettings(
        ReadTrim(section, DefaultNotchTrim),
        ReadLayer(section, DefaultNotchTrimLayer),
        DefaultNotchJoin,
        DefaultTextBreak,
        ReadOptimize(section, DefaultOptimize)));
    var current = ToolsOptionStore.Read(SettingsSection, section =>
      new ExportSettings(
        ReadTrim(section, legacy.NotchTrim),
        ReadLayer(section, legacy.NotchTrimLayer),
        ReadJoin(section, legacy.NotchJoin),
        ReadTextBreak(section, legacy.TextBreak),
        ReadOptimize(section, legacy.Optimize)));
    try
    {
      var trim = doc.Strings.GetValue(DocumentSection, NotchTrimEntry)
        ?? doc.Strings.GetValue(DocumentSection, LegacyNotchTrimEntry);
      var layer = doc.Strings.GetValue(DocumentSection, NotchTrimLayerEntry)
        ?? doc.Strings.GetValue(DocumentSection, LegacyNotchTrimLayerEntry);
      var join = doc.Strings.GetValue(DocumentSection, NotchJoinEntry);
      var textBreak = doc.Strings.GetValue(DocumentSection, TextBreakEntry);
      var optimize = doc.Strings.GetValue(DocumentSection, OptimizeEntry)
        ?? doc.Strings.GetValue(DocumentSection, LegacyOptimizeEntry);
      return new ExportSettings(
        Enum.TryParse(trim, true, out NotchTrimMode parsed) && Enum.IsDefined(parsed)
          ? parsed : current.NotchTrim,
        !string.IsNullOrWhiteSpace(layer) ? layer : current.NotchTrimLayer,
        bool.TryParse(join, out bool parsedJoin)
          ? parsedJoin : current.NotchJoin,
        bool.TryParse(textBreak, out bool parsedTextBreak)
          ? parsedTextBreak : current.TextBreak,
        bool.TryParse(optimize, out bool parsedOptimize)
          ? parsedOptimize : current.Optimize);
    }
    catch (Exception ex)
    {
      Log.Write("vExportDXF", $"document options read failed: {ex}");
      return current;
    }
  }

  private static NotchTrimMode ReadTrim(
    System.Text.Json.Nodes.JsonObject? section, NotchTrimMode fallback) =>
    ToolsOptionStore.TryGetString(section, "notch_trim", out var value) &&
    Enum.TryParse(value, true, out NotchTrimMode mode) && Enum.IsDefined(mode)
      ? mode : fallback;

  private static string ReadLayer(
    System.Text.Json.Nodes.JsonObject? section, string fallback) =>
    ToolsOptionStore.TryGetString(section, "notch_trim_layer", out var value) &&
    !string.IsNullOrWhiteSpace(value) ? value : fallback;

  private static bool ReadOptimize(
    System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
    ToolsOptionStore.TryGetBool(section, "optimize", out bool value)
      ? value : fallback;

  private static bool ReadJoin(
    System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
    ToolsOptionStore.TryGetBool(section, "notch_join", out bool value)
      ? value : fallback;

  private static bool ReadTextBreak(
    System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
    ToolsOptionStore.TryGetBool(section, "text_break", out bool value)
      ? value : fallback;

  private static Result GetRunOptions(RhinoDoc doc, ref ExportSettings settings)
  {
    bool saveDefaults = DefaultSaveDefaults;
    while (true)
    {
      using var getter = new GetOption();
      getter.SetCommandPrompt("DXF export options; Enter to choose file");
      getter.AcceptNothing(true);
      int trimIndex = getter.AddOptionList(
        "NotchTrim", NotchTrimNames, (int)settings.NotchTrim);
      int layerIndex = getter.AddOption("NotchTrimLayer", settings.NotchTrimLayer);
      var join = new OptionToggle(settings.NotchJoin, "No", "Yes");
      var textBreak = new OptionToggle(settings.TextBreak, "No", "Yes");
      var optimize = new OptionToggle(settings.Optimize, "No", "Yes");
      var save = new OptionToggle(saveDefaults, "No", "Yes");
      getter.AddOptionToggle("NotchJoin", ref join);
      getter.AddOptionToggle("TextBreak", ref textBreak);
      getter.AddOptionToggle("Optimize", ref optimize);
      getter.AddOptionToggle("SaveDefaults", ref save);
      var result = getter.Get();
      if (result == GetResult.Cancel)
        return Result.Cancel;
      if (result == GetResult.Nothing)
      {
        if (saveDefaults && !SaveSettings(doc, settings))
          RhinoApp.WriteLine("vExportDXF: could not save defaults; see vTools log.");
        return Result.Success;
      }
      if (result != GetResult.Option)
        return getter.CommandResult();
      settings = settings with
      {
        NotchJoin = join.CurrentValue,
        TextBreak = textBreak.CurrentValue,
        Optimize = optimize.CurrentValue
      };
      saveDefaults = save.CurrentValue;
      if (getter.Option()?.Index == trimIndex)
        settings = settings with
          { NotchTrim = (NotchTrimMode)getter.Option()!.CurrentListOptionIndex };
      else if (getter.Option()?.Index == layerIndex)
      {
        string layer = settings.NotchTrimLayer;
        if (RhinoGet.GetString("Notch trim destination layer", false, ref layer) == Result.Success &&
            !string.IsNullOrWhiteSpace(layer))
          settings = settings with { NotchTrimLayer = layer.Trim() };
      }
    }
  }

  private static bool SaveSettings(RhinoDoc doc, ExportSettings settings)
  {
    bool globalSaved = ToolsOptionStore.Update(SettingsSection, section =>
    {
      section["notch_trim"] = settings.NotchTrim.ToString();
      section["notch_trim_layer"] = settings.NotchTrimLayer;
      section["notch_join"] = settings.NotchJoin;
      section["text_break"] = settings.TextBreak;
      section["optimize"] = settings.Optimize;
    });
    try
    {
      doc.Strings.SetString(DocumentSection, NotchTrimEntry, settings.NotchTrim.ToString());
      doc.Strings.SetString(DocumentSection, NotchTrimLayerEntry, settings.NotchTrimLayer);
      doc.Strings.SetString(DocumentSection, NotchJoinEntry, settings.NotchJoin.ToString());
      doc.Strings.SetString(DocumentSection, TextBreakEntry, settings.TextBreak.ToString());
      doc.Strings.SetString(DocumentSection, OptimizeEntry, settings.Optimize.ToString());
      return globalSaved;
    }
    catch (Exception ex)
    {
      Log.Write("vExportDXF", $"document options save failed: {ex}");
      return false;
    }
  }

  private static bool TryChoosePath(RhinoDoc doc, out string path)
  {
    path = string.Empty;
    string? previous = doc.Strings.GetValue(LastPathKey);
    string previousDirectory = !string.IsNullOrWhiteSpace(previous)
      ? Path.GetDirectoryName(previous) ?? string.Empty : string.Empty;
    string documentDirectory = !string.IsNullOrWhiteSpace(doc.Path)
      ? Path.GetDirectoryName(doc.Path) ?? string.Empty : string.Empty;
    string directory = Directory.Exists(previousDirectory)
      ? previousDirectory : documentDirectory;
    string filename = !string.IsNullOrWhiteSpace(previous)
      ? Path.GetFileName(previous)
      : (Path.GetFileNameWithoutExtension(doc.Name) is { Length: > 0 } stem
        ? stem : DefaultFileStem) + DxfExtension;
    var dialog = new SaveFileDialog
    {
      Title = "Export DXF",
      Filter = "DXF Files (*.dxf)|*.dxf||",
      DefaultExt = "dxf",
      FileName = filename,
      InitialDirectory = directory,
    };
    if (!dialog.ShowSaveDialog() || string.IsNullOrWhiteSpace(dialog.FileName))
      return false;
    path = NormalizeDxfPath(dialog.FileName);
    return true;
  }

  private static string NormalizeDxfPath(string path)
  {
    string fullPath = Path.GetFullPath(path);
    return string.Equals(Path.GetExtension(fullPath), DxfExtension,
        StringComparison.OrdinalIgnoreCase)
      ? fullPath : Path.ChangeExtension(fullPath, DxfExtension);
  }

  private static string MakeTemporaryPath(string destination)
  {
    string directory = Path.GetDirectoryName(destination)
      ?? throw new IOException("The export path has no directory.");
    if (!Directory.Exists(directory))
      throw new DirectoryNotFoundException(directory);
    string stem = Path.GetFileNameWithoutExtension(destination);
    for (int attempt = 0; attempt < MaximumTemporaryNameAttempts; attempt++)
    {
      string path = Path.Combine(directory,
        $"{stem}_rhino_export_{Guid.NewGuid():N}{DxfExtension}");
      if (!File.Exists(path))
        return path;
    }
    throw new IOException("Could not find a unique temporary DXF filename.");
  }

  private static bool WaitForFile(string path)
  {
    var watch = Stopwatch.StartNew();
    while (watch.ElapsedMilliseconds < ExportWaitMilliseconds)
    {
      if (File.Exists(path) && new FileInfo(path).Length > 0)
        return true;
      Thread.Sleep(ExportPollMilliseconds);
    }
    return File.Exists(path) && new FileInfo(path).Length > 0;
  }

  private static Transform OriginTranslation(IReadOnlyList<RhinoObject> objects)
  {
    BoundingBox bounds = BoundingBox.Empty;
    foreach (var obj in objects)
      bounds.Union(obj.Geometry.GetBoundingBox(true));
    return bounds.IsValid
      ? Transform.Translation(Point3d.Origin - bounds.Min)
      : Transform.Identity;
  }

  private static List<Guid> PrepareExportObjects(RhinoDoc doc,
    IReadOnlyList<RhinoObject> targets, Transform translation,
    IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
    IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides,
    List<Guid> temporaryObjects, List<int> temporaryDefinitions)
  {
    var targetById = targets.ToDictionary(obj => obj.Id);
    var assigned = new HashSet<Guid>();
    var exportIds = new List<Guid>();
    for (int groupIndex = 0; groupIndex < doc.Groups.Count; groupIndex++)
    {
      string? groupName = doc.Groups.GroupName(groupIndex);
      if (string.IsNullOrWhiteSpace(groupName))
        continue;
      var members = doc.Groups.GroupMembers(groupIndex)
        .Where(obj => obj != null && targetById.ContainsKey(obj.Id) &&
                      !assigned.Contains(obj.Id)).ToArray();
      if (members.Length == 0)
        continue;
      if (!TryCreateGroupBlock(doc, groupName, members, translation, notchTrimOverrides, textBreakOverrides,
            temporaryObjects, temporaryDefinitions, out Guid instanceId))
        continue;
      exportIds.Add(instanceId);
      foreach (var member in members)
        assigned.Add(member.Id);
    }

    foreach (var source in targets)
    {
      if (assigned.Contains(source.Id))
        continue;
      foreach (var piece in PreparedExportPieces(source, notchTrimOverrides, textBreakOverrides))
      {
        using var geometry = piece.Geometry.Duplicate();
        if (geometry == null || !geometry.Transform(translation))
          throw new InvalidOperationException($"Could not copy object {source.Id} for export.");
        var attributes = piece.Attributes.Duplicate();
        attributes.ObjectId = Guid.NewGuid();
        attributes.RemoveFromAllGroups();
        Guid copyId = doc.Objects.Add(geometry, attributes);
        if (copyId == Guid.Empty)
          throw new InvalidOperationException($"Could not add temporary copy of {source.Id}.");
        temporaryObjects.Add(copyId);
        exportIds.Add(copyId);
      }
    }
    return exportIds;
  }

  private static bool TryCreateGroupBlock(RhinoDoc doc, string groupName,
    IReadOnlyList<RhinoObject> members, Transform translation,
    IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
    IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides,
    List<Guid> temporaryObjects, List<int> temporaryDefinitions,
    out Guid instanceId)
  {
    instanceId = Guid.Empty;
    var geometries = new List<GeometryBase>();
    var attributes = new List<ObjectAttributes>();
    try
    {
      BoundingBox bounds = BoundingBox.Empty;
      foreach (var member in members)
      {
        foreach (var piece in PreparedExportPieces(member, notchTrimOverrides, textBreakOverrides))
        {
          var geometry = piece.Geometry.Duplicate();
          if (geometry == null)
            return false;
          geometries.Add(geometry);
          bounds.Union(geometry.GetBoundingBox(true));
          var attrs = piece.Attributes.Duplicate();
          attrs.ObjectId = Guid.NewGuid();
          attrs.RemoveFromAllGroups();
          attributes.Add(attrs);
        }
      }
      if (geometries.Count == 0)
        return false;
      Point3d basePoint = bounds.IsValid ? bounds.Min : Point3d.Origin;
      string blockName = UniqueBlockName(doc, groupName);
      int definitionIndex = doc.InstanceDefinitions.Add(
        blockName, string.Empty, basePoint, geometries, attributes);
      if (definitionIndex < 0)
        return false;
      temporaryDefinitions.Add(definitionIndex);

      int layerIndex = UzipCommon.EnsureLayer(doc, BlockInsertLayer);
      var instanceAttributes = new ObjectAttributes
      {
        ObjectId = Guid.NewGuid(),
        LayerIndex = layerIndex,
      };
      var insertion = Transform.Translation(basePoint - Point3d.Origin);
      insertion = translation * insertion;
      instanceId = doc.Objects.AddInstanceObject(
        definitionIndex, insertion, instanceAttributes);
      if (instanceId == Guid.Empty)
        return false;
      temporaryObjects.Add(instanceId);
      return true;
    }
    finally
    {
      foreach (var geometry in geometries)
        geometry.Dispose();
    }
  }

  private static string UniqueBlockName(RhinoDoc doc, string groupName)
  {
    string root = new(groupName.Select(ch =>
      ch < 128 && char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
      .ToArray());
    root = root.Trim('_');
    if (root.Length == 0)
      root = "Group";
    root = root[..Math.Min(root.Length, MaximumBlockNameLength)];
    if (doc.InstanceDefinitions.Find(root) == null)
      return root;
    for (int attempt = 0; attempt < MaximumTemporaryNameAttempts; attempt++)
    {
      string name = $"{root[..Math.Min(root.Length, BlockNamePrefixLength)]}_" +
        Guid.NewGuid().ToString("N")[..8];
      if (doc.InstanceDefinitions.Find(name) == null)
        return name;
    }
    throw new InvalidOperationException("Could not name a temporary group block.");
  }

  private static FileDwgWriteOptions CurrentDxfWriteOptions()
  {
    var scheme = ReadCurrentScheme();
    var options = new FileDwgWriteOptions();
    SetProperty(options, "Name", scheme.Name);
    SetEnum(options, scheme, "Version", "AcadVersion");
    SetEnum(options, scheme, "ExportMeshesAs", "ExportMeshesAs", "WriteMeshesAs");
    SetEnum(options, scheme, "ExportSurfacesAs", "ExportSurfacesAs", "WriteSurfacesAs");
    SetEnum(options, scheme, "ExportLinesAs", "ExportLinesAs", "WriteLinesAs");
    SetEnum(options, scheme, "ExportArcsAs", "ExportArcsAs", "WriteArcsAs");
    SetEnum(options, scheme, "ExportSplinesAs", "ExportSplinesAs", "WriteSplinesAs");
    SetEnum(options, scheme, "ExportPolylinesAs", "ExportPolylinesAs", "WritePolylinesAs");
    SetEnum(options, scheme, "ExportPolycurvesAs", "ExportPolycurvesAs", "WritePolycurvesAs");
    foreach (string name in new[] { "Flatten", "ColorMethod", "UseColor" })
      SetEnum(options, scheme, name, name);

    SetDouble(options, scheme, "SimplifyTolerance", "SimplifyTolerance", "SimplifyTol");
    SetDouble(options, scheme, "MinPointDistance", "MinPointDistance");
    SetBool(options, scheme, "CurveUseMaxAngle", "CurveUseMaxAngle", "UseMaxAngle");
    SetBool(options, scheme, "CurveUseChordHeight", "CurveUseChordHeight", "UseChordHeight");
    SetBool(options, scheme, "CurveUseSegmentLength", "CurveUseSegmentLength", "UseSegmentLength");
    SetDouble(options, scheme, "CurveMaxAngleDegrees", "CurveMaxAngleDegrees", "MaxAngle");
    SetDouble(options, scheme, "CurveChordHeight", "CurveChordHeight", "ChordHeight");
    SetDouble(options, scheme, "CurveSegmentLength", "CurveSegmentLength", "SegmentLength");
    foreach (string name in new[]
    {
      "SplitPolycurves", "SplitSplines", "Simplify", "NoDxfHeader", "IsDefault",
      "FullLayerPath", "PreserveArcNormals", "UseLWPolylines", "WriteThickCurves"
    })
      SetBool(options, scheme, name, name);
    Log.Write("vExportDXF", $"using DXF scheme {scheme.Name}");
    return options;
  }

  private static void SetProperty(object target, string name, object value)
  {
    PropertyInfo? property = target.GetType().GetProperty(name);
    if (property?.CanWrite == true)
      property.SetValue(target, value);
  }

  private static void SetEnum(FileDwgWriteOptions options, SchemeValues settings,
    string propertyName, string currentKey, string? legacyKey = null)
  {
    string? key = settings.FindKey(currentKey, legacyKey);
    PropertyInfo? property = options.GetType().GetProperty(propertyName);
    if (key == null || property?.CanWrite != true)
      return;
    int value = settings.GetInteger(key);
    if (legacyKey != null && key.Equals(legacyKey, StringComparison.OrdinalIgnoreCase))
      value = MapLegacyEnum(propertyName, key, value);
    property.SetValue(options, Enum.ToObject(property.PropertyType, value));
  }

  private static int MapLegacyEnum(string property, string key, int value)
  {
    int[] mapping = property switch
    {
      "ExportMeshesAs" => value switch { 6 => [0], 7 => [1], _ => [] },
      "ExportSurfacesAs" => value switch { 8 => [0], 5 => [1], 6 => [2], _ => [] },
      "ExportLinesAs" or "ExportSplinesAs" or "ExportPolylinesAs" =>
        value switch { 0 => [0], 3 => [1], 4 => [2], 9 => [3], _ => [] },
      "ExportArcsAs" => value switch
        { 0 => [0], 1 => [1], 2 => [2], 3 => [3], 4 => [4], 9 => [5], _ => [] },
      "ExportPolycurvesAs" => value switch
        { 0 => [0], 2 => [1], 3 => [2], 4 => [3], 9 => [4], _ => [] },
      _ => []
    };
    if (mapping.Length == 0)
      throw new InvalidOperationException($"Unsupported legacy DXF setting {key}={value}.");
    return mapping[0];
  }

  private static void SetBool(FileDwgWriteOptions options, SchemeValues settings,
    string propertyName, params string[] keys)
  {
    string? key = settings.FindKey(keys);
    if (key != null)
      SetProperty(options, propertyName, settings.GetBool(key));
  }

  private static void SetDouble(FileDwgWriteOptions options, SchemeValues settings,
    string propertyName, params string[] keys)
  {
    string? key = settings.FindKey(keys);
    if (key != null)
      SetProperty(options, propertyName, settings.GetDouble(key));
  }

  private sealed class SchemeValues(
    string name, IEnumerable<string> keys,
    Func<string, int> getInteger, Func<string, double> getDouble,
    Func<string, bool> getBool)
  {
    private readonly Dictionary<string, string> _keys = keys.ToDictionary(
      key => key, key => key, StringComparer.OrdinalIgnoreCase);
    public string Name { get; } = name;
    public string? FindKey(params string?[] names)
    {
      foreach (string? name in names)
        if (name != null && _keys.TryGetValue(name, out string? key))
          return key;
      return null;
    }
    public int GetInteger(string key) => getInteger(key);
    public double GetDouble(string key) => getDouble(key);
    public bool GetBool(string key) => getBool(key);
  }

  private static SchemeValues ReadCurrentScheme()
  {
    try
    {
      var settings = PlugIn.GetPluginSettings(AcadExportPluginId, true);
      if (settings != null)
      {
        string name = settings.GetString("CurrentOptionName", string.Empty);
        if (string.IsNullOrEmpty(name))
          name = settings.ChildKeys.FirstOrDefault(key =>
            settings.GetChild(key).GetBool("CurrentOption", false)) ?? string.Empty;
        if (!string.IsNullOrEmpty(name))
        {
          var child = settings.GetChild(name);
          return new SchemeValues(name, child.Keys, child.GetInteger,
            child.GetDouble, child.GetBool);
        }
      }
    }
    catch (Exception ex)
    {
      Log.Write("vExportDXF", $"persistent DXF settings unavailable: {ex.Message}");
    }
    return ReadSchemeXml();
  }

  private static SchemeValues ReadSchemeXml()
  {
    string appData = System.Environment.GetFolderPath(
      System.Environment.SpecialFolder.ApplicationData);
    string pluginsPath = Path.Combine(appData, "McNeel", "Rhinoceros",
      $"{RhinoApp.ExeVersion}.0", "Plug-ins");
    if (!Directory.Exists(pluginsPath))
      throw new FileNotFoundException("Rhino DXF export settings folder was not found.");
    string suffix = $"({AcadExportPluginId})";
    string? path = Directory.EnumerateDirectories(pluginsPath)
      .Where(folder => Path.GetFileName(folder).EndsWith(
        suffix, StringComparison.OrdinalIgnoreCase))
      .Select(folder => Path.Combine(folder, "settings", "settings-Scheme__Default.xml"))
      .FirstOrDefault(File.Exists);
    if (path == null)
      throw new FileNotFoundException("Rhino DXF export settings file was not found.");
    XElement settings = XDocument.Load(path).Root?.Element("settings")
      ?? throw new InvalidDataException("DXF export settings XML has no settings node.");
    var rootValues = XmlEntries(settings);
    rootValues.TryGetValue("CurrentOptionName", out string? name);
    var children = settings.Elements("child").ToArray();
    if (string.IsNullOrEmpty(name))
      name = children.FirstOrDefault(child =>
        XmlEntries(child).TryGetValue("CurrentOption", out string? selected) &&
        bool.TryParse(selected, out bool active) && active)?.Attribute("key")?.Value;
    var selectedChild = children.FirstOrDefault(child =>
      string.Equals(child.Attribute("key")?.Value, name, StringComparison.Ordinal));
    if (selectedChild == null || string.IsNullOrEmpty(name))
      throw new InvalidDataException("Rhino's current DXF export scheme could not be read.");
    var values = XmlEntries(selectedChild);
    return new SchemeValues(name, values.Keys,
      key => int.Parse(values[key], CultureInfo.InvariantCulture),
      key => double.Parse(values[key], CultureInfo.InvariantCulture),
      key => values[key].Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on");
  }

  private static Dictionary<string, string> XmlEntries(XElement node) =>
    node.Elements("entry")
      .Where(entry => entry.Attribute("key") != null)
      .ToDictionary(entry => entry.Attribute("key")!.Value,
        entry => entry.Value, StringComparer.OrdinalIgnoreCase);
}
