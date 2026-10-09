namespace vTools.Commands
{
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


  /// <summary>Exports selected geometry, preserving Rhino's current DXF scheme and group blocks.</summary>
  [CommandStyle(Style.ScriptRunner)]
  public sealed partial class vExportDXF : vToolsCommand
  {
    // Defaults and output conventions
    private const NotchTrimMode DefaultNotchTrim = NotchTrimMode.Trim; // No keeps touched export curves; Split moves between-leg pieces; Trim omits them.
    private const string DefaultNotchSplitLayer = "Reference"; // Rhino layer path for between-leg export pieces in Split mode.
    private const ExportExplodeMode DefaultExplode = ExportExplodeMode.Full; // No preserves curves; SplitAtCorners splits tangent discontinuities; Full separates all curve components.
    private static readonly string[] ExplodeNames = ["No", "SplitAtCorners", "Full"]; // Command-line values for temporary export-curve decomposition.
    private const double ExportSplitParameterTolerance = 1e-10; // Relative domain tolerance for progressing past a reported curve discontinuity.
    private const bool DefaultNotchJoin = true; // true joins matching V/U notches into line/polyline export curves; false exports notch components separately.
    private const bool DefaultTextBreak = true; // true exports each multiline text row separately; false keeps multiline text objects intact.
    private const bool DefaultOptimize = true; // true runs vCleanup before export; false leaves the source document untouched.
    private const vCleanup.ExportCleanupAction DefaultCleanupAction = vCleanup.ExportCleanupAction.Ask; // Ask reviews findings; Delete removes them; Ignore exports them unchanged by deletion.
    private static readonly string[] CleanupActionNames = ["Ask", "Delete", "Ignore"]; // Saved export-only cleanup behavior; independent of vCleanup's own deletion setting.
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
    private const string NotchSplitLayerEntry = "vExportDXF.NotchSplitLayer"; // Document setting key for the export split layer.
    private const string PriorNotchTrimLayerEntry = "vExportDXF.NotchTrimLayer"; // Previous export split-layer key read for migration.
    private const string ExplodeEntry = "vExportDXF.Explode"; // Document setting key for temporary curve decomposition.
    private const string NotchJoinEntry = "vExportDXF.NotchJoin"; // Document setting key for joining split/trimmed notch geometry in exported copies.
    private const string TextBreakEntry = "vExportDXF.TextBreak"; // Document setting key for splitting multiline export text.
    private const string OptimizeEntry = "vExportDXF.Optimize"; // Document setting key for export cleanup.
    private const string LegacyNotchTrimEntry = "vExportOptions.NotchTrim"; // Previous document key read for migration.
    private const string LegacyNotchTrimLayerEntry = "vExportOptions.NotchTrimLayer"; // Previous document key read for migration.
    private const string LegacyOptimizeEntry = "vExportOptions.Optimize"; // Previous document key read for migration.
    private const string LastPathKey = ".DXF_export.LastPath"; // Document user-text key compatible with the Python exporter.
    private const string BlockInsertLayer = "Reference"; // Layer for temporary group-block inserts.
    private const string DefaultFileStem = "untitled"; // Suggested filename stem for an unsaved Rhino document.
    private const string UnnamedGroupPrefix = "Group_"; // Export block name prefix followed by a zero-based index for unnamed Rhino groups.
    private const string DxfExtension = ".dxf"; // Required output filename extension.
    private const int ExportWaitMilliseconds = 2000; // Maximum wait for Rhino to finish writing the temporary file.
    private const int ExportPollMilliseconds = 50; // File-existence polling interval in milliseconds.
    private const int ExportCopyBufferSize = 1024 * 1024; // Byte buffer for in-place destination overwriting; matches the original exporter without renaming existing files.
    private const int MaximumTemporaryNameAttempts = 100; // Collision attempts for temporary files and block names.
    private const int MaximumBlockNameLength = 48; // Maximum ASCII characters retained from a source group name.
    private const double TableLabelFramePaddingFraction = 0.1; // Fallback frame padding as a fraction of annotation height when only table text is selected.
    private const double TableAnnotationPaddingFraction = 0.2; // Export-label inset as a fraction of measured glyph height; matches the nest label area's padding.
    private const int BlockNamePrefixLength = 39; // Maximum retained stem before an eight-character collision suffix.
    private static readonly Guid AcadExportPluginId =
      new("39a88493-9e97-4f15-bd62-ad25896a2632"); // Rhino's ACAD export plug-in settings ID.

    public override string EnglishName => "vExportDXF";

    private readonly record struct ExportSettings(
      NotchTrimMode NotchTrim, string NotchSplitLayer, bool NotchJoin, bool TextBreak, bool Optimize, ExportExplodeMode Explode,
      vCleanup.ExportCleanupAction CleanupAction = DefaultCleanupAction);
    private enum ExportExplodeMode { No, SplitAtCorners, Full }

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
      if (settings.Optimize)
      {
        var decorations = targets.Where(obj => NestTableMetadata.IsCleanupDecoration(doc, obj))
          .Select(obj => obj.Id).ToHashSet();
        var exportScope = targets.Where(obj => !decorations.Contains(obj.Id))
          .Select(obj => obj.Id).ToHashSet();
        var result = vCleanup.OptimizeForExport(doc, exportScope, settings.CleanupAction);
        if (result != Result.Success)
          return result;
        exportScope.UnionWith(decorations);
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
        notchTrimOverrides = BuildNotchTrimOverrides(
          doc, targets, settings.NotchTrim, settings.NotchSplitLayer, settings.NotchJoin);
        ApplyExportExplode(targets,notchTrimOverrides,settings.Explode);
        textBreakOverrides = BuildTextBreakOverrides(doc, targets, settings.TextBreak);
        var translation = selection.Length > 0
          ? OriginTranslation(targets.Where(obj => !NestTableMetadata.IsTableBorder(doc, obj)).ToArray())
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
        if (!TryChoosePath(doc, out string path))
          return Result.Cancel;
        tempPath = MakeTemporaryPath(path);
        using (CommandHistoryCleanup.BeginExport(EnglishName, tempPath))
        {
          if (!doc.ExportSelected(tempPath, options.ToDictionary()))
            throw new InvalidOperationException("Rhino DXF export failed.");
        }
        if (!WaitForFile(tempPath))
          throw new IOException("Rhino did not create a nonempty DXF file.");
        CopyExportFile(tempPath, path);
        doc.Strings.SetString(LastPathKey, path);
        RhinoApp.WriteLine($"vExportDXF: exported {targets.Length} object(s) to {path}.");
        Log.Write("vExportDXF", $"exported source={targets.Length} prepared={selectedCount} " +
          $"groups={temporaryDefinitions.Count} explode={settings.Explode} textBreak={settings.TextBreak} " +
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
          ReadLayer(section, DefaultNotchSplitLayer),
          DefaultNotchJoin,
          DefaultTextBreak,
          ReadOptimize(section, DefaultOptimize),DefaultExplode));
      var current = ToolsOptionStore.Read(SettingsSection, section =>
        new ExportSettings(
          ReadTrim(section, legacy.NotchTrim),
          ReadLayer(section, legacy.NotchSplitLayer),
          ReadJoin(section, legacy.NotchJoin),
          ReadTextBreak(section, legacy.TextBreak),
          ReadOptimize(section, legacy.Optimize),ReadExplode(section,legacy.Explode),
          ReadCleanupAction(section, DefaultCleanupAction)));
      try
      {
        var trim = doc.Strings.GetValue(DocumentSection, NotchTrimEntry)
          ?? doc.Strings.GetValue(DocumentSection, LegacyNotchTrimEntry);
        var layer = doc.Strings.GetValue(DocumentSection, NotchSplitLayerEntry)
          ?? doc.Strings.GetValue(DocumentSection, PriorNotchTrimLayerEntry)
          ?? doc.Strings.GetValue(DocumentSection, LegacyNotchTrimLayerEntry);
        var explode = doc.Strings.GetValue(DocumentSection, ExplodeEntry);
        var join = doc.Strings.GetValue(DocumentSection, NotchJoinEntry);
        var textBreak = doc.Strings.GetValue(DocumentSection, TextBreakEntry);
        var optimize = doc.Strings.GetValue(DocumentSection, OptimizeEntry)
          ?? doc.Strings.GetValue(DocumentSection, LegacyOptimizeEntry);
        return new ExportSettings(
          Enum.TryParse(trim, true, out NotchTrimMode parsed) && Enum.IsDefined(parsed)
            ? parsed : current.NotchTrim,
          !string.IsNullOrWhiteSpace(layer) ? layer : current.NotchSplitLayer,
          bool.TryParse(join, out bool parsedJoin)
            ? parsedJoin : current.NotchJoin,
          bool.TryParse(textBreak, out bool parsedTextBreak)
            ? parsedTextBreak : current.TextBreak,
          bool.TryParse(optimize, out bool parsedOptimize)
            ? parsedOptimize : current.Optimize,
          Enum.TryParse(explode,true,out ExportExplodeMode parsedExplode)&&Enum.IsDefined(parsedExplode)?parsedExplode:current.Explode,
          current.CleanupAction);
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
      System.Text.Json.Nodes.JsonObject? section, string fallback)
    {
      if(ToolsOptionStore.TryGetString(section,"notch_split_layer",out var value)&&!string.IsNullOrWhiteSpace(value)) return value;
      return ToolsOptionStore.TryGetString(section,"notch_trim_layer",out value)&&!string.IsNullOrWhiteSpace(value)?value:fallback;
    }

    private static ExportExplodeMode ReadExplode(System.Text.Json.Nodes.JsonObject? section,ExportExplodeMode fallback)=>
      ToolsOptionStore.TryGetString(section,"explode",out var value)&&Enum.TryParse(value,true,out ExportExplodeMode mode)&&Enum.IsDefined(mode)?mode:fallback;

    private static bool ReadOptimize(
      System.Text.Json.Nodes.JsonObject? section, bool fallback) =>
      ToolsOptionStore.TryGetBool(section, "optimize", out bool value)
        ? value : fallback;

    private static vCleanup.ExportCleanupAction ReadCleanupAction(
      System.Text.Json.Nodes.JsonObject? section, vCleanup.ExportCleanupAction fallback) =>
      ToolsOptionStore.TryGetString(section, "cleanup_action", out var value) &&
      Enum.TryParse(value, true, out vCleanup.ExportCleanupAction action) && Enum.IsDefined(action)
        ? action : fallback;

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
        getter.SetCommandPrompt("DXF export options; Enter to prepare export");
        getter.AcceptNothing(true);
        int trimIndex = getter.AddOptionList(
          "NotchTrim", NotchTrimNames, (int)settings.NotchTrim);
        int layerIndex = getter.AddOption("NotchSplitLayer", settings.NotchSplitLayer);
        int explodeIndex = getter.AddOptionList("Explode",ExplodeNames,(int)settings.Explode);
        var join = new OptionToggle(settings.NotchJoin, "No", "Yes");
        var textBreak = new OptionToggle(settings.TextBreak, "No", "Yes");
        var optimize = new OptionToggle(settings.Optimize, "No", "Yes");
        var save = new OptionToggle(saveDefaults, "No", "Yes");
        getter.AddOptionToggle("NotchJoin", ref join);
        getter.AddOptionToggle("TextBreak", ref textBreak);
        getter.AddOptionToggle("Optimize", ref optimize);
        int cleanupIndex = getter.AddOptionList("CleanupAction", CleanupActionNames, (int)settings.CleanupAction);
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
        else if(getter.Option()?.Index==explodeIndex)
          settings=settings with {Explode=(ExportExplodeMode)getter.Option()!.CurrentListOptionIndex};
        else if (getter.Option()?.Index == cleanupIndex)
          settings = settings with { CleanupAction = (vCleanup.ExportCleanupAction)getter.Option()!.CurrentListOptionIndex };
        else if (getter.Option()?.Index == layerIndex)
        {
          string layer = settings.NotchSplitLayer;
          if (RhinoGet.GetString("Notch split destination layer", false, ref layer) == Result.Success &&
              !string.IsNullOrWhiteSpace(layer))
            settings = settings with { NotchSplitLayer = layer.Trim() };
        }
      }
    }

    private static bool SaveSettings(RhinoDoc doc, ExportSettings settings)
    {
      bool globalSaved = ToolsOptionStore.Update(SettingsSection, section =>
      {
        section["notch_trim"] = settings.NotchTrim.ToString();
        section["notch_split_layer"] = settings.NotchSplitLayer;
        section["explode"] = settings.Explode.ToString();
        section["notch_join"] = settings.NotchJoin;
        section["text_break"] = settings.TextBreak;
        section["optimize"] = settings.Optimize;
        section["cleanup_action"] = settings.CleanupAction.ToString();
      });
      try
      {
        doc.Strings.SetString(DocumentSection, NotchTrimEntry, settings.NotchTrim.ToString());
        doc.Strings.SetString(DocumentSection, NotchSplitLayerEntry, settings.NotchSplitLayer);
        doc.Strings.SetString(DocumentSection, ExplodeEntry, settings.Explode.ToString());
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

    private static void CopyExportFile(string source, string destination)
    {
      using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
      using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
      input.CopyTo(output, ExportCopyBufferSize);
      output.Flush(flushToDisk: true);
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
      var assigned = targets.Where(obj => NestTableMetadata.IsTableBorder(doc, obj)).Select(obj => obj.Id).ToHashSet();
      var exportIds = new List<Guid>();
      var nestTables=NestTableMetadata.TableGroups(doc);
      var tableAnnotations = targets.Where(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey)
        is NestTableMetadata.LabelRole or NestTableMetadata.LabelFrameRole or NestTableMetadata.BorderRole)
        .GroupBy(obj => (obj.Attributes.GetUserString(NestTableMetadata.GroupIdKey),
          obj.Attributes.GetUserString("vNest.session"), obj.Attributes.GetUserString("vNest.table")));
      foreach (var annotations in tableAnnotations)
      {
        var layoutMembers = annotations.ToList();
        var members = layoutMembers.Where(obj => !NestTableMetadata.IsTableBorder(doc, obj)).ToList();
        if (members.Count == 0) continue;
        if (!members.Any(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelFrameRole))
        {
          BoundingBox bounds = BoundingBox.Empty;
          double height = 0;
          var framePlane = Plane.WorldXY;
          foreach (var member in members)
          {
            if (member.Geometry is TextEntity text)
            {
              using var copy = (TextEntity)text.Duplicate();
              NormalizeTableLabelAlignment(doc, copy);
              var glyphs = ExportTableTextBounds(doc, copy, Plane.WorldXY);
              bounds.Union(glyphs);
              height = Math.Max(height, glyphs.Diagonal.Y);
            }
            else bounds.Union(member.Geometry.GetBoundingBox(true));
          }
          if (!bounds.IsValid)
            throw new InvalidOperationException("Could not frame table annotations for export.");
          double padding = Math.Max(doc.ModelAbsoluteTolerance, height * TableLabelFramePaddingFraction);
          bounds.Inflate(padding);
          var texts = members.Select(obj => obj.Geometry).OfType<TextEntity>().ToArray();
          if (texts.Length > 0 && TryGetTableAnnotationArea(layoutMembers, texts[0].Plane, out var tabPlane, out var tabArea))
          { framePlane = tabPlane; bounds = tabArea; }
          using var frame = new Rectangle3d(framePlane,
            new Interval(bounds.Min.X, bounds.Max.X), new Interval(bounds.Min.Y, bounds.Max.Y)).ToNurbsCurve();
          var attributes = members[0].Attributes.Duplicate();
          attributes.ObjectId = Guid.NewGuid();
          attributes.RemoveFromAllGroups();
          attributes.SetUserString(NestTableMetadata.RoleKey, NestTableMetadata.LabelFrameRole);
          Guid frameId = doc.Objects.AddCurve(frame, attributes);
          if (frameId == Guid.Empty)
            throw new InvalidOperationException("Could not add the temporary table-label frame.");
          temporaryObjects.Add(frameId);
          members.Add(doc.Objects.FindId(frameId));
        }
        string name = "vNest_" + annotations.Key.Item2 + "_table_" + annotations.Key.Item3 + "_label";
        var tableText = PrepareTableAnnotations(doc, members, textBreakOverrides);
        Guid labelInstance;
        try
        {
          if (!TryCreateGroupBlock(doc, name, members, translation, notchTrimOverrides, tableText,
              temporaryObjects, temporaryDefinitions, out labelInstance))
            throw new InvalidOperationException("Could not prepare the table-label block.");
        }
        finally { DisposeTextBreakOverrides(tableText); }
        exportIds.Add(labelInstance);
        foreach (var annotation in annotations)
          assigned.Add(annotation.Id);
      }
      for (int groupIndex = 0; groupIndex < doc.Groups.Count; groupIndex++)
      {
        if(nestTables.Contains(groupIndex)) continue;
        if (doc.Groups[groupIndex] is not { IsDeleted: false }) continue;
        string? groupName = doc.Groups.GroupName(groupIndex);
        if (string.IsNullOrWhiteSpace(groupName))
          groupName = UnnamedGroupPrefix + groupIndex.ToString(CultureInfo.InvariantCulture);
        var members = doc.Groups.GroupMembers(groupIndex)
          .Where(obj => obj != null && targetById.ContainsKey(obj.Id) &&
                        !assigned.Contains(obj.Id)).ToArray();
        if (members.Length == 0)
          continue;
        if (!TryCreateGroupBlock(doc, groupName, members, translation, notchTrimOverrides, textBreakOverrides,
              temporaryObjects, temporaryDefinitions, out Guid instanceId))
          throw new InvalidOperationException($"Could not prepare group '{groupName}' as a DXF block.");
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
}

namespace vTools.Commands
{
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vExportDXF
  {
    private static void ApplyExportExplode(IReadOnlyList<RhinoObject> targets,Dictionary<Guid,List<ExportCurvePiece>> overrides,ExportExplodeMode mode)
    {
      if(mode==ExportExplodeMode.No) return;
      int splitObjects=0,outputPieces=0;
      foreach(var source in targets.Where(obj=>obj.Geometry is Curve && !NestTableMetadata.HasDecorationRole(obj.Attributes)))
      {
        var originals=overrides.TryGetValue(source.Id,out var existing)?existing:[new ExportCurvePiece((Curve)source.Geometry,source.Attributes)];
        var replacement=new List<ExportCurvePiece>(); bool changed=false;
        try
        {
          foreach(var piece in originals)
          {
            var segments=ExplodeExportCurve(piece.Geometry,mode);
            changed|=segments.Length>1;
            replacement.AddRange(segments.Select(curve=>new ExportCurvePiece(curve,piece.Attributes,piece.Between)));
          }
          if(!changed) { foreach(var piece in replacement) piece.Geometry.Dispose(); continue; }
          overrides[source.Id]=replacement;
          if(existing!=null) foreach(var piece in existing) piece.Geometry.Dispose();
          splitObjects++; outputPieces+=replacement.Count;
        }
        catch { foreach(var piece in replacement) piece.Geometry.Dispose(); throw; }
      }
      Log.Write("vExportDXF","explode={0} sourceCurvesSplit={1} exportPieces={2}",mode,splitObjects,outputPieces);
    }

    private static Curve[] ExplodeExportCurve(Curve source,ExportExplodeMode mode)
    {
      if(mode==ExportExplodeMode.No) return [source.DuplicateCurve()];
      using var copy=source.DuplicateCurve();
      if(mode==ExportExplodeMode.Full)
      {
        if(copy is PolyCurve polycurve) polycurve.RemoveNesting();
        var segments=copy.DuplicateSegments();
        if(segments.Length>1)
        {
          var result=new List<Curve>();
          try
          {
            foreach(var segment in segments) result.AddRange(ExplodeExportCurve(segment,mode));
            return result.ToArray();
          }
          catch { foreach(var curve in result) curve.Dispose(); throw; }
          finally { foreach(var segment in segments) segment.Dispose(); }
        }
        foreach(var segment in segments) segment.Dispose();
      }
      var parameters=ExportCorners(copy);
      // Closed-curve Split rejoins through the seam, so keep that seam within a smooth span rather than at a corner.
      if(copy.IsClosed&&parameters.Count>0&&copy.ChangeClosedCurveSeam((copy.Domain.T0+parameters[0])/2)) parameters=ExportCorners(copy);
      return parameters.Count>0?copy.Split(parameters.ToArray())??[source.DuplicateCurve()]:[source.DuplicateCurve()];
    }

    private static List<double> ExportCorners(Curve curve)
    {
      var result=new List<double>(); var domain=curve.Domain;
      double start=domain.T0,epsilon=Math.Max(Math.Abs(domain.Length)*ExportSplitParameterTolerance,RhinoMath.ZeroTolerance);
      while(curve.GetNextDiscontinuity(Continuity.G1_continuous,start,domain.T1,out double parameter))
      {
        if(parameter<=start||parameter>=domain.T1) break;
        result.Add(parameter); start=parameter+epsilon;
      }
      return result;
    }
  }
}

namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.Linq;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vExportDXF
  {
    private sealed record ExportCurvePiece(
      Curve Geometry, ObjectAttributes Attributes, bool Between = false);
    private readonly record struct NotchContact(double First, double Second);
    private readonly record struct CurveHit(RhinoObject Object, double Parameter, double Distance);
    private readonly record struct NotchLegs(
      Point3d First, Point3d Second, Guid SourceId, bool OnSource,
      RhinoObject[] Components);
    private sealed record MatchedNotch(Guid[] CurveIds, RhinoObject[] Components);

    private static IEnumerable<(GeometryBase Geometry, ObjectAttributes Attributes)> ExportPieces(
      RhinoObject source, IReadOnlyDictionary<Guid, List<ExportCurvePiece>> overrides)
    {
      if (overrides.TryGetValue(source.Id, out var pieces))
      {
        foreach (var piece in pieces)
          yield return (piece.Geometry, piece.Attributes);
      }
      else
        yield return (source.Geometry, source.Attributes);
    }

    private static Dictionary<Guid, List<ExportCurvePiece>> BuildNotchTrimOverrides(
      RhinoDoc doc, IReadOnlyList<RhinoObject> targets,
      NotchTrimMode mode, string layerName, bool joinNotches)
    {
      var overrides = new Dictionary<Guid, List<ExportCurvePiece>>();
      if (mode == NotchTrimMode.No)
        return overrides;

      double tolerance = Math.Max(
        doc.ModelAbsoluteTolerance * NotchContactToleranceScale,
        RhinoMath.ZeroTolerance * NotchContactZeroToleranceScale);
      var curves = targets.Where(obj => obj.Geometry is Curve &&
        obj.Attributes.GetUserString(NotchMetadataPrefix + "object_role") != NotchRole)
        .ToArray();
      var notchComponents = targets.Where(obj => obj.Geometry is Curve &&
        obj.Attributes.GetUserString(NotchMetadataPrefix + "object_role") == NotchRole &&
        (obj.Attributes.GetUserString(NotchMetadataPrefix + "notch_type") is
          "V" or "U" or OpenVNotchType) &&
        double.TryParse(obj.Attributes.GetUserString(NotchMetadataPrefix + "notch_offset"),
          NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) &&
        offset >= 0.0).ToArray();
      var notchGroups = notchComponents.GroupBy(obj =>
        obj.Attributes.GetUserString(NotchMetadataPrefix + "component_set") is
          { Length: > 0 } set ? set : obj.Id.ToString("N"));
      var contacts = new Dictionary<Guid, List<NotchContact>>();
      var matchedNotches = new List<MatchedNotch>();
      int unmatched = 0;
      int matched = 0;
      foreach (var group in notchGroups)
      {
        foreach (var legs in ExtractNotchLegs(group))
        {
          var firstHits = FindContactHits(
            curves, legs.First, legs.SourceId, legs.OnSource, tolerance);
          var secondHits = FindContactHits(
            curves, legs.Second, legs.SourceId, legs.OnSource, tolerance);
          CurveHit? first = null;
          CurveHit? second = null;
          double best = double.MaxValue;
          foreach (var left in firstHits)
            foreach (var right in secondHits)
              if (left.Object.Id == right.Object.Id &&
                  Math.Abs(left.Parameter - right.Parameter) > RhinoMath.ZeroTolerance &&
                  left.Distance + right.Distance < best)
              {
                first = left;
                second = right;
                best = left.Distance + right.Distance;
              }
          if (first.HasValue && second.HasValue)
          {
            AddContact(contacts, first.Value.Object.Id,
              first.Value.Parameter, second.Value.Parameter);
            matchedNotches.Add(new MatchedNotch(
              [first.Value.Object.Id], legs.Components));
            matched++;
            continue;
          }

          best = double.MaxValue;
          double firstEnd = 0.0;
          double secondEnd = 0.0;
          foreach (var left in firstHits)
            foreach (var right in secondHits)
              if (left.Object.Id != right.Object.Id &&
                  left.Object.Geometry is Curve leftCurve &&
                  right.Object.Geometry is Curve rightCurve &&
                  TrySharedEnd(leftCurve, rightCurve, tolerance,
                    out double leftEnd, out double rightEnd) &&
                  left.Distance + right.Distance < best)
              {
                first = left;
                second = right;
                firstEnd = leftEnd;
                secondEnd = rightEnd;
                best = left.Distance + right.Distance;
              }
          if (first.HasValue && second.HasValue)
          {
            AddContact(contacts, first.Value.Object.Id, first.Value.Parameter, firstEnd);
            AddContact(contacts, second.Value.Object.Id, secondEnd, second.Value.Parameter);
            matchedNotches.Add(new MatchedNotch(
              [first.Value.Object.Id, second.Value.Object.Id], legs.Components));
            matched++;
          }
          else
            unmatched++;
        }
      }

      int changed = 0;
      int between = 0;
      foreach (var (id, spans) in contacts)
      {
        var target = curves.FirstOrDefault(obj => obj.Id == id);
        if (target?.Geometry is not Curve curve)
          continue;
        var pieces = SplitExportCurve(doc, target, curve, spans, mode, layerName, tolerance,
          out int betweenPieces);
        if (pieces == null)
          continue;
        overrides[id] = pieces;
        changed++;
        between += betweenPieces;
      }
      int joined = joinNotches
        ? JoinNotchedExportCurves(curves, matchedNotches, overrides, tolerance)
        : 0;
      Log.Write("vExportDXF", $"NotchTrim={mode} exportCurves={curves.Length} " +
        $"visibleNotchComponents={notchComponents.Length} matched={matched} " +
        $"unmatched={unmatched} curves={changed} between={between} " +
        $"NotchJoin={joinNotches} joined={joined}");
      if (unmatched > 0)
        RhinoApp.WriteLine($"vExportDXF: {unmatched} V/U notch contact(s) did not match an export curve.");
      return overrides;
    }

    private static int JoinNotchedExportCurves(
      IReadOnlyList<RhinoObject> curves, IReadOnlyList<MatchedNotch> matches,
      Dictionary<Guid, List<ExportCurvePiece>> overrides, double tolerance)
    {
      var layers = curves.ToDictionary(obj => obj.Id, obj => obj.Attributes.LayerIndex);
      var pending = new List<MatchedNotch>();
      foreach (var match in matches)
      {
        int layer = match.Components[0].Attributes.LayerIndex;
        if (match.Components.All(obj => obj.Attributes.LayerIndex == layer) &&
            match.CurveIds.All(id => layers.TryGetValue(id, out int sourceLayer) && sourceLayer == layer))
          pending.Add(match);
        else
          Log.Write("vExportDXF", $"NotchJoin skipped curves={string.Join(",", match.CurveIds)} " +
            "reason=notch and source curves have different layers");
      }
      int joinedCount = 0;
      while (pending.Count > 0)
      {
        var component = new List<MatchedNotch> { pending[0] };
        var sourceIds = pending[0].CurveIds.ToHashSet();
        pending.RemoveAt(0);
        bool expanded;
        do
        {
          expanded = false;
          for (int index = pending.Count - 1; index >= 0; index--)
          {
            if (!pending[index].CurveIds.Any(sourceIds.Contains))
              continue;
            component.Add(pending[index]);
            sourceIds.UnionWith(pending[index].CurveIds);
            pending.RemoveAt(index);
            expanded = true;
          }
        } while (expanded);

        if (TryJoinNotchComponent(curves, component, sourceIds, overrides,
              tolerance, out string reason))
          joinedCount += component.Count;
        else
          Log.Write("vExportDXF", $"NotchJoin skipped curves={string.Join(",", sourceIds)} " +
            $"notches={component.Count} reason={reason}");
      }
      return joinedCount;
    }

    private static bool TryJoinNotchComponent(
      IReadOnlyList<RhinoObject> curves, IReadOnlyList<MatchedNotch> matches,
      IReadOnlySet<Guid> sourceIds,
      Dictionary<Guid, List<ExportCurvePiece>> overrides,
      double tolerance, out string reason)
    {
      reason = string.Empty;
      var sources = curves.Where(obj => sourceIds.Contains(obj.Id)).ToArray();
      if (sources.Length != sourceIds.Count || sources.Any(obj =>
            obj.Geometry is not Curve curve ||
            !(curve is LineCurve || curve.IsPolyline()) ||
            !overrides.ContainsKey(obj.Id)))
      {
        reason = "a source is not a split line or polyline";
        return false;
      }
      var anchor = sources[0];
      var anchorGroups = (anchor.Attributes.GetGroupList() ?? []).ToHashSet();
      if (sources.Any(obj => obj.Attributes.LayerIndex != anchor.Attributes.LayerIndex ||
            !anchorGroups.SetEquals(obj.Attributes.GetGroupList() ?? [])))
      {
        reason = "source curves have different layers or groups";
        return false;
      }

      var outside = sources.SelectMany(obj => overrides[obj.Id])
        .Where(piece => !piece.Between).ToArray();
      var notchObjects = matches.SelectMany(match => match.Components)
        .DistinctBy(obj => obj.Id).ToArray();
      if (outside.Length == 0 || notchObjects.Length == 0 ||
          notchObjects.Any(obj => obj.Geometry is not Curve curve || curve.IsClosed))
      {
        reason = "no open notch or retained source section";
        return false;
      }
      var notchCurves = notchObjects.Select(obj => (Curve)obj.Geometry).ToArray();
      var input = outside.Select(piece => piece.Geometry).Concat(notchCurves).ToArray();
      var endpoints = input.Where(curve => !curve.IsClosed)
        .SelectMany(curve => new[] { curve.PointAtStart, curve.PointAtEnd }).ToArray();
      if (endpoints.Any(point => endpoints.Count(other =>
            point.DistanceTo(other) <= tolerance) > 2))
      {
        reason = "the notch would branch at a shared endpoint";
        return false;
      }

      Curve[] joined = [];
      var replacements = new List<ExportCurvePiece>();
      try
      {
        joined = Curve.JoinCurves(input, tolerance, preserveDirection: false);
        if (joined.Length == 0 || joined.Length > outside.Length ||
            notchCurves.Any(notch => joined.Any(output =>
              IsUnjoinedNotch(output, notch, tolerance))))
        {
          reason = "not every notch joins its source";
          return false;
        }
        double inputLength = input.Sum(curve => curve.GetLength());
        double joinedLength = joined.Sum(curve => curve.GetLength());
        if (Math.Abs(inputLength - joinedLength) >
            Math.Max(tolerance * input.Length * 2.0, inputLength * 1.0e-8))
        {
          reason = "joining changed the boundary length";
          return false;
        }
        foreach (var curve in joined)
          replacements.Add(new ExportCurvePiece(
            curve.DuplicateCurve(), anchor.Attributes.Duplicate()));

        foreach (var source in sources)
        {
          var pieces = overrides[source.Id];
          overrides[source.Id] = pieces.Where(piece => piece.Between).ToList();
          foreach (var piece in pieces.Where(piece => !piece.Between))
            piece.Geometry.Dispose();
        }
        overrides[anchor.Id].AddRange(replacements);
        foreach (var notch in notchObjects)
          overrides[notch.Id] = [];
        return true;
      }
      catch (Exception ex)
      {
        foreach (var piece in replacements)
          piece.Geometry.Dispose();
        reason = ex.Message;
        return false;
      }
      finally
      {
        foreach (var curve in joined)
          curve.Dispose();
      }
    }

    private static bool IsUnjoinedNotch(Curve output, Curve notch, double tolerance)
    {
      if (Math.Abs(output.GetLength() - notch.GetLength()) > tolerance)
        return false;
      return output.PointAtStart.DistanceTo(notch.PointAtStart) <= tolerance &&
             output.PointAtEnd.DistanceTo(notch.PointAtEnd) <= tolerance ||
             output.PointAtStart.DistanceTo(notch.PointAtEnd) <= tolerance &&
             output.PointAtEnd.DistanceTo(notch.PointAtStart) <= tolerance;
    }

    private static IEnumerable<NotchLegs> ExtractNotchLegs(IEnumerable<RhinoObject> group)
    {
      var components = group.OrderBy(obj =>
        int.TryParse(obj.Attributes.GetUserString(NotchMetadataPrefix + "component_index"),
          NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
          ? index : 0).ToArray();
      if (components.Length == 0 ||
          components.Any(obj => obj.Geometry is not Curve))
        yield break;
      var attributes = components[0].Attributes;
      if (int.TryParse(attributes.GetUserString(NotchMetadataPrefix + "component_count"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected) &&
          expected != components.Length)
        yield break;
      string? type = attributes.GetUserString(NotchMetadataPrefix + "notch_type");
      int perSide = type == OpenVNotchType ? 2 : 1;
      if (components.Length % perSide != 0)
        yield break;
      Guid.TryParse(attributes.GetUserString(NotchMetadataPrefix + "curve_id"),
        out Guid sourceId);
      bool onSource = double.TryParse(
        attributes.GetUserString(NotchMetadataPrefix + "notch_offset"),
        NumberStyles.Float, CultureInfo.InvariantCulture, out double offset) &&
        offset <= RhinoMath.ZeroTolerance;
      for (int index = 0; index < components.Length; index += perSide)
      {
        var side = components[index..(index + perSide)];
        yield return new NotchLegs(
          ((Curve)side[0].Geometry).PointAtStart,
          ((Curve)side[^1].Geometry).PointAtEnd,
          sourceId, onSource, side);
      }
    }

    private static List<CurveHit> FindContactHits(
      IReadOnlyList<RhinoObject> curves, Point3d point, Guid sourceId,
      bool onSource, double tolerance)
    {
      var hits = new List<CurveHit>();
      foreach (var obj in curves)
      {
        if ((!onSource && obj.Id == sourceId) || obj.Geometry is not Curve curve ||
            !curve.ClosestPoint(point, out double parameter))
          continue;
        double distance = point.DistanceTo(curve.PointAt(parameter));
        if (distance <= tolerance)
          hits.Add(new CurveHit(obj, parameter, distance));
      }
      hits.Sort((left, right) => left.Distance.CompareTo(right.Distance));
      return hits;
    }

    private static bool TrySharedEnd(Curve first, Curve second, double tolerance,
      out double firstParameter, out double secondParameter)
    {
      firstParameter = secondParameter = 0.0;
      if (first.IsClosed || second.IsClosed)
        return false;
      double best = tolerance;
      bool found = false;
      foreach (double a in new[] { first.Domain.T0, first.Domain.T1 })
        foreach (double b in new[] { second.Domain.T0, second.Domain.T1 })
        {
          double distance = first.PointAt(a).DistanceTo(second.PointAt(b));
          if (distance > best)
            continue;
          firstParameter = a;
          secondParameter = b;
          best = distance;
          found = true;
        }
      return found;
    }

    private static void AddContact(Dictionary<Guid, List<NotchContact>> contacts,
      Guid id, double first, double second)
    {
      if (!contacts.TryGetValue(id, out var spans))
        contacts[id] = spans = [];
      spans.Add(new NotchContact(first, second));
    }

    private static List<ExportCurvePiece>? SplitExportCurve(
      RhinoDoc doc, RhinoObject target, Curve curve,
      IReadOnlyList<NotchContact> spans, NotchTrimMode mode, string layerName,
      double tolerance, out int betweenCount)
    {
      betweenCount = 0;
      var parameters = spans.SelectMany(span => new[] { span.First, span.Second })
        .Where(parameter => curve.IsClosed ||
          curve.PointAt(parameter).DistanceTo(curve.PointAtStart) > tolerance &&
          curve.PointAt(parameter).DistanceTo(curve.PointAtEnd) > tolerance)
        .OrderBy(parameter => parameter).ToArray();
      var distinct = new List<double>();
      foreach (double parameter in parameters)
        if (distinct.Count == 0 ||
            curve.PointAt(parameter).DistanceTo(curve.PointAt(distinct[^1])) > tolerance)
          distinct.Add(parameter);
      var pieces = distinct.Count > 0 ? curve.Split(distinct) : [curve.DuplicateCurve()];
      if (pieces == null || pieces.Length == 0)
        return null;
      var replacements = new List<ExportCurvePiece>();
      try
      {
        for (int index = 0; index < pieces.Length; index++)
        {
          var piece = pieces[index];
          if (!curve.ClosestPoint(piece.PointAtNormalizedLength(0.5), out double midpoint))
            continue;
          bool between = spans.Any(span => ParameterBetween(curve, span, midpoint));
          if (between)
            betweenCount++;
          if (between && mode == NotchTrimMode.Trim)
            continue;
          var attributes = target.Attributes.Duplicate();
          if (between && mode == NotchTrimMode.Split)
            attributes.LayerIndex = UzipCommon.EnsureLayer(doc, layerName);
          replacements.Add(new ExportCurvePiece(piece.DuplicateCurve(), attributes, between));
        }
        if (betweenCount == 0)
        {
          foreach (var replacement in replacements)
            replacement.Geometry.Dispose();
          return null;
        }
        return replacements;
      }
      catch
      {
        foreach (var replacement in replacements)
          replacement.Geometry.Dispose();
        throw;
      }
      finally
      {
        foreach (var piece in pieces)
          piece.Dispose();
      }
    }

    private static bool ParameterBetween(Curve curve, NotchContact span, double parameter)
    {
      double first = Math.Min(span.First, span.Second);
      double last = Math.Max(span.First, span.Second);
      bool inside = parameter > first && parameter < last;
      if (curve.IsClosed)
      {
        using var section = curve.Trim(first, last);
        if (section != null && section.GetLength() > curve.GetLength() * 0.5)
          inside = !inside;
      }
      return inside;
    }
  }
}

namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Windows.Forms;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  public sealed partial class vExportDXF
  {
    // Text layout measurement defaults
    private const double TextBreakSmallCapsScale = 1.0; // Unitless glyph scale; 1 keeps the source character sizes.
    private const double TextBreakGlyphSpacing = 0.0; // Additional model-space glyph spacing; 0 preserves native kerning.
    private const double TextBreakLayoutTolerance = 1e-6; // Relative glyph-position tolerance when checking that a row was not wrapped or reformatted.

    private static Dictionary<Guid, List<TextEntity>> BuildTextBreakOverrides(
      RhinoDoc doc, IReadOnlyList<RhinoObject> targets, bool enabled)
    {
      var overrides = new Dictionary<Guid, List<TextEntity>>();
      try
      {
        foreach (var source in targets)
        {
          if (source.Geometry is not TextEntity text)
            continue;
          bool tableLabel = source.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelRole;
          bool split = enabled && text.PlainText.IndexOfAny(['\r', '\n']) >= 0;
          if (!split && !tableLabel)
            continue;
          var lines = split ? SplitExportText(doc, text) : new List<TextEntity> { (TextEntity)text.Duplicate() };
          overrides.Add(source.Id, lines);
          if (tableLabel)
            foreach (var line in lines)
              NormalizeTableLabelAlignment(doc, line);
        }
        return overrides;
      }
      catch
      {
        DisposeTextBreakOverrides(overrides);
        throw;
      }
    }

    private static void NormalizeTableLabelAlignment(RhinoDoc doc, TextEntity text)
    {
      var frame = text.Plane;
      var before = ExportTableTextBounds(doc, text, frame);
      var parent = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
      text.ParentDimensionStyle = parent;
      using var style = text.GetDimensionStyle(parent) ?? parent.Duplicate();
      style.Id = Guid.Empty;
      style.Index = -1;
      style.ParentId = parent.Id;
      style.TextHorizontalAlignment = TextHorizontalAlignment.Left;
      style.TextVerticalAlignment = TextVerticalAlignment.Bottom;
      style.SetFieldOverride(DimensionStyle.Field.TextHorizontalAlignment);
      style.SetFieldOverride(DimensionStyle.Field.TextVerticalAlignment);
      if (!text.SetOverrideDimStyle(style))
        throw new InvalidOperationException("Could not normalize table-label alignment for export.");
      text.TextHorizontalAlignment = TextHorizontalAlignment.Left;
      text.TextVerticalAlignment = TextVerticalAlignment.Bottom;
      var after = ExportTableTextBounds(doc, text, frame);
      if (!before.IsValid || !after.IsValid)
        throw new InvalidOperationException("Could not measure table-label alignment for export.");
      // Replace centre attachment without changing the glyphs' physical placement.
      text.Transform(Transform.Translation(frame.XAxis * (before.Min.X - after.Min.X) +
        frame.YAxis * (before.Min.Y - after.Min.Y)));
    }

    private static Dictionary<Guid, List<TextEntity>> PrepareTableAnnotations(RhinoDoc doc,
      IReadOnlyList<RhinoObject> members, IReadOnlyDictionary<Guid, List<TextEntity>> overrides)
    {
      var result = new Dictionary<Guid, List<TextEntity>>();
      try
      {
        foreach (var member in members.Where(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelRole))
        {
          if (member.Geometry is not TextEntity source) continue;
          var lines = overrides.TryGetValue(member.Id, out var existing)
            ? existing.Select(line => (TextEntity)line.Duplicate()).ToList()
            : new List<TextEntity> { (TextEntity)source.Duplicate() };
          result.Add(member.Id, lines);
          foreach (var line in lines) NormalizeTableLabelAlignment(doc, line);
        }
        var texts = result.Values.SelectMany(lines => lines).ToArray();
        if (texts.Length == 0) return result;
        if (!TryGetTableAnnotationArea(members, texts[0].Plane, out var frame, out var area)) return result;
        var bounds = texts.Select(text => ExportTableTextBounds(doc, text, frame)).ToArray();
        double padding = Math.Min(area.Diagonal.X / 4, Math.Max(doc.ModelAbsoluteTolerance,
          bounds.Max(box => box.Diagonal.Y) * TableAnnotationPaddingFraction));
        double available = area.Diagonal.X - 2 * padding;
        double scale = Math.Min(1, available / Math.Max(RhinoMath.ZeroTolerance, bounds.Max(box => box.Diagonal.X)));
        for (int index = 0; index < texts.Length; index++)
        {
          var text = texts[index];
          if (scale < 1) text.TextHeight *= scale;
          var box = ExportTableTextBounds(doc, text, frame);
          text.Transform(Transform.Translation(frame.XAxis * (area.Center.X - box.Center.X) +
            frame.YAxis * (bounds[index].Min.Y - box.Min.Y)));
          box = ExportTableTextBounds(doc, text, frame);
          if (!box.IsValid || box.Min.X < area.Min.X + padding - doc.ModelAbsoluteTolerance ||
              box.Max.X > area.Max.X - padding + doc.ModelAbsoluteTolerance)
            throw new InvalidOperationException("Could not fit table annotations inside their outline.");
        }
        return result;
      }
      catch { DisposeTextBreakOverrides(result); throw; }
    }

    private static bool TryGetTableAnnotationArea(IReadOnlyList<RhinoObject> members, Plane fallback,
      out Plane frame, out BoundingBox area)
    {
      frame = fallback;
      area = BoundingBox.Empty;
      var border = members.FirstOrDefault(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.BorderRole);
      if (border?.Geometry is Curve outline && outline.TryGetPolyline(out var points) && points.Count == 9)
      {
        frame = new Plane(points[0], points[1] - points[0], points[3] - points[0]);
        foreach (int index in new[] { 4, 5, 6, 7 })
        {
          frame.ClosestParameter(points[index], out double x, out double y);
          area.Union(new Point3d(x, y, 0));
        }
      }
      else
        foreach (var member in members.Where(obj => obj.Attributes.GetUserString(NestTableMetadata.RoleKey) == NestTableMetadata.LabelFrameRole))
          area.Union(member.Geometry.GetBoundingBox(frame));
      return area.IsValid;
    }

    private static BoundingBox ExportTableTextBounds(RhinoDoc doc, TextEntity text, Plane frame)
    {
      using var measurement = (TextEntity)text.Duplicate();
      var parent = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
      measurement.ParentDimensionStyle = parent;
      measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(doc, text, doc.Views.ActiveView?.ActiveViewport);
      using var style = measurement.GetDimensionStyle(parent);
      var curves = measurement.CreateCurves(style ?? parent, true, TextBreakSmallCapsScale, TextBreakGlyphSpacing);
      var bounds = BoundingBox.Empty;
      foreach (var curve in curves ?? [])
      {
        try { bounds.Union(curve.GetBoundingBox(frame)); }
        finally { curve.Dispose(); }
      }
      return bounds;
    }

    private static List<TextEntity> SplitExportText(RhinoDoc doc, TextEntity source)
    {
      var result = new List<TextEntity>();
      List<Curve[]>? sourceGlyphs = null;
      try
      {
        // RichEdit extracts complete per-row RTF, including fonts and inline formatting.
        using var editor = new RichTextBox();
        if (source.TextHasRtfFormatting)
          editor.Rtf = source.RichText;
        else
          editor.Text = NormalizeTextBreaks(source.PlainText);
        string content = editor.Text;
        if (NormalizeTextBreaks(content) != NormalizeTextBreaks(source.PlainText))
          throw new InvalidOperationException("TextBreak could not preserve this text's formatting. Use TextBreak=No to export it unchanged.");

        using var measurement = source.Duplicate() as TextEntity
          ?? throw new InvalidOperationException("Could not copy multiline text for export.");
        measurement.ParentDimensionStyle = source.ParentDimensionStyle;
        measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(
          doc, source, doc.Views.ActiveView?.ActiveViewport);
        using var style = measurement.DimensionStyle;
        sourceGlyphs = measurement.CreateCurvesGrouped(
          style, true, TextBreakSmallCapsScale, TextBreakGlyphSpacing);
        int glyphIndex = 0;
        int start = 0;
        while (start < content.Length)
        {
          int end = content.IndexOf('\n', start);
          if (end < 0) end = content.Length;
          int length = end - start;
          if (length > 0 && content[end - 1] == '\r') length--;
          if (!string.IsNullOrWhiteSpace(content.Substring(start, length)))
          {
            editor.Select(start, length);
            var line = measurement.Duplicate() as TextEntity
              ?? throw new InvalidOperationException("Could not copy a text row for export.");
            result.Add(line);
            line.ParentDimensionStyle = measurement.ParentDimensionStyle;
            if (source.TextHasRtfFormatting)
              line.SetRichText(editor.SelectedRtf, style);
            else
              line.PlainText = content.Substring(start, length);
            line.TextIsWrapped = false;
            var glyphs = line.CreateCurvesGrouped(
              style, true, TextBreakSmallCapsScale, TextBreakGlyphSpacing);
            try
            {
              if (glyphs.Count == 0 || glyphIndex + glyphs.Count > sourceGlyphs.Count)
                throw new InvalidOperationException("TextBreak could not measure a text row. Use TextBreak=No to export it unchanged.");
              // Matching native glyphs gives the exact baseline, scale and justification.
              var from = TextGlyphBounds(glyphs[0]);
              var to = TextGlyphBounds(sourceGlyphs[glyphIndex]);
              var translation = to.Center - from.Center;
              double tolerance = TextBreakLayoutTolerance * Math.Max(1.0, source.TextHeight * measurement.DimensionScale);
              for (int index = 0; index < glyphs.Count; index++)
              {
                var actual = TextGlyphBounds(glyphs[index]);
                var expected = TextGlyphBounds(sourceGlyphs[glyphIndex + index]);
                if (!actual.IsValid || !expected.IsValid ||
                    (actual.Min + translation).DistanceTo(expected.Min) > tolerance ||
                    (actual.Max + translation).DistanceTo(expected.Max) > tolerance)
                  throw new InvalidOperationException("TextBreak could not preserve a wrapped or formatted row's placement. Use TextBreak=No to export it unchanged.");
              }
              var plane = line.Plane;
              plane.Origin += translation;
              line.Plane = plane;
              glyphIndex += glyphs.Count;
            }
            finally
            {
              DisposeTextGlyphs(glyphs);
            }
          }
          start = end + 1;
        }
        if (glyphIndex != sourceGlyphs.Count)
          throw new InvalidOperationException("TextBreak could not preserve all text rows. Use TextBreak=No to export it unchanged.");
        return result;
      }
      catch
      {
        foreach (var line in result) line.Dispose();
        throw;
      }
      finally
      {
        if (sourceGlyphs != null) DisposeTextGlyphs(sourceGlyphs);
      }
    }

    private static string NormalizeTextBreaks(string text) =>
      text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

    private static BoundingBox TextGlyphBounds(IEnumerable<Curve> glyph)
    {
      var bounds = BoundingBox.Empty;
      foreach (var curve in glyph) bounds.Union(curve.GetBoundingBox(true));
      return bounds;
    }

    private static void DisposeTextGlyphs(IEnumerable<Curve[]> glyphs)
    {
      foreach (var glyph in glyphs)
        foreach (var curve in glyph) curve.Dispose();
    }

    private static void DisposeTextBreakOverrides(
      IReadOnlyDictionary<Guid, List<TextEntity>> overrides)
    {
      foreach (var lines in overrides.Values)
        foreach (var line in lines) line.Dispose();
    }

    private static IEnumerable<(GeometryBase Geometry, ObjectAttributes Attributes)> PreparedExportPieces(
      RhinoObject source, IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
      IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides)
    {
      if (textBreakOverrides.TryGetValue(source.Id, out var lines))
      {
        foreach (var line in lines)
          yield return (line, source.Attributes);
      }
      else
      {
        foreach (var piece in ExportPieces(source, notchTrimOverrides))
          yield return piece;
      }
    }
  }
}
