using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>Edits saved command options and advanced factory overrides in the loaded profile.</summary>
[CommandStyle(Style.NotUndoable)]
public sealed class vSettings : vToolsCommand
{
  // Defaults and customizable constants
  [CommandDefault("vSettings","vTools.Commands.vSettings.DefaultModifiedOnly","true initially shows only customized or unapplied values; false initially shows every setting.")]
  private const bool DefaultModifiedOnly = false; // true starts with customized/unsaved values only; false starts unfiltered.
  private static readonly bool ConfiguredDefaultModifiedOnly = CommandDefaults.Get("vSettings","vTools.Commands.vSettings.DefaultModifiedOnly",DefaultModifiedOnly); // Initial filter state; restart after advanced edits.
  [CommandDefault("vSettings","vTools.Commands.vSettings.SettingColumnWidth","Initial setting-name column width in logical pixels; positive.")]
  private const int SettingColumnWidth = 240; // Positive logical-pixel setting-name column width.
  private static readonly int ConfiguredSettingColumnWidth = CommandDefaults.Get("vSettings","vTools.Commands.vSettings.SettingColumnWidth",SettingColumnWidth); // Resolved initial width.
  [CommandDefault("vSettings","vTools.Commands.vSettings.ValueColumnWidth","Initial value column width in logical pixels, including HEX and swatch; positive.")]
  private const int ValueColumnWidth = 280; // Positive logical-pixel value column width.
  private static readonly int ConfiguredValueColumnWidth = CommandDefaults.Get("vSettings","vTools.Commands.vSettings.ValueColumnWidth",ValueColumnWidth); // Resolved initial width.
  [CommandDefault("vSettings","vTools.Commands.vSettings.FactoryColumnWidth","Initial built-in-default column width in logical pixels; positive.")]
  private const int FactoryColumnWidth = 150; // Positive logical-pixel factory column width.
  private static readonly int ConfiguredFactoryColumnWidth = CommandDefaults.Get("vSettings","vTools.Commands.vSettings.FactoryColumnWidth",FactoryColumnWidth); // Resolved initial width.
  [CommandDefault("vSettings","vTools.Commands.vSettings.ColorHexWidth","HEX text field width in logical pixels; positive, allowing #AARRGGBB.")]
  private const int ColorHexWidth = 96; // Positive logical-pixel width for ten-character ARGB HEX values.
  private static readonly int ConfiguredColorHexWidth = CommandDefaults.Get("vSettings","vTools.Commands.vSettings.ColorHexWidth",ColorHexWidth); // Resolved HEX field width.
  private static readonly Size InitialSize = new(900, 650); // Initial settings window size in logical pixels.
  private static readonly Size MinimumWindowSize = new(660, 480); // Minimum usable settings window size in logical pixels.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.SectionWidth","Command/service list width in logical pixels. Positive value.")]
  private const int SectionWidth = 180; // Command/service list width in logical pixels.
  private static readonly int ConfiguredSectionWidth = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.SectionWidth",SectionWidth); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.EditorHeight","Compact selected-setting description height in logical pixels; values are edited inline. Positive value.")]
  private const int EditorHeight = 44; // Compact selected-setting description height in logical pixels; values are edited inline.
  private static readonly int ConfiguredEditorHeight = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.EditorHeight",EditorHeight); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.ValueRowHeight","Fixed logical-pixel grid row height, including native inline editors. Positive value.")]
  private const int ValueRowHeight = 28; // Fixed logical-pixel grid row height, including native inline editors.
  private static readonly int ConfiguredValueRowHeight = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.ValueRowHeight",ValueRowHeight); // Resolved through vSettings; restart Rhino after advanced changes.
  [CommandDefault("vSettings","vTools.Commands.vSettings.CustomizedBackground","ARGB background for saved settings differing from factory defaults; foreground contrast is automatic.","#FFFFF2CC")]
  private static readonly Color CustomizedBackground = Color.FromArgb(CommandDefaults.GetColor("vSettings","vTools.Commands.vSettings.CustomizedBackground",System.Drawing.Color.FromArgb(255,242,204)).ToArgb()); // ARGB customized-row background; text contrast is automatic.
  [CommandDefault("vSettings","vTools.Commands.vSettings.UnsavedBackground","ARGB background for changed values not yet applied; selection takes precedence.","#FFFFDBB3")]
  private static readonly Color UnsavedBackground = Color.FromArgb(CommandDefaults.GetColor("vSettings","vTools.Commands.vSettings.UnsavedBackground",System.Drawing.Color.FromArgb(255,219,179)).ToArgb()); // ARGB unsaved-row background; text contrast is automatic.
  [CommandDefault("vSettings","vTools.Commands.vSettings.ActiveSelectionBackground","ARGB background for focused selections in the command and settings lists; text contrast is automatic.","#FF2A5F96")]
  private static readonly Color ActiveSelectionBackground = Color.FromArgb(CommandDefaults.GetColor("vSettings","vTools.Commands.vSettings.ActiveSelectionBackground",System.Drawing.Color.FromArgb(42,95,150)).ToArgb()); // ARGB focused-list selection background.
  [CommandDefault("vSettings","vTools.Commands.vSettings.InactiveSelectionBackground","ARGB background for unfocused selections in the command and settings lists; text contrast is automatic.","#FFD3E2F2")]
  private static readonly Color InactiveSelectionBackground = Color.FromArgb(CommandDefaults.GetColor("vSettings","vTools.Commands.vSettings.InactiveSelectionBackground",System.Drawing.Color.FromArgb(211,226,242)).ToArgb()); // ARGB unfocused-list selection background.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.TextContrastThreshold","Relative-luminance threshold for choosing white versus black editor text; zero through one. Accepted range is 0 through 1.")]
  private const double TextContrastThreshold = 0.179; // Relative-luminance threshold for choosing white versus black editor text; zero through one.
  private static readonly double ConfiguredTextContrastThreshold = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.TextContrastThreshold",TextContrastThreshold); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.Spacing","Logical-pixel gaps between controls. Zero or greater.")]
  private const int Spacing = 6; // Logical-pixel gaps between controls.
  private static readonly int ConfiguredSpacing = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.Spacing",Spacing); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.PaddingSize","Window edge padding in logical pixels. Zero or greater.")]
  private const int PaddingSize = 10; // Window edge padding in logical pixels.
  private static readonly int ConfiguredPaddingSize = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.PaddingSize",PaddingSize); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.NumberPrecision","Fractional digits available for non-integral settings; tiny tolerances also accept exponent text in JSON fields. Accepted range is 0 through 15.")]
  private const int NumberPrecision = 8; // Fractional digits available for non-integral settings; tiny tolerances also accept exponent text in JSON fields.
  private static readonly int ConfiguredNumberPrecision = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.NumberPrecision",NumberPrecision); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.InlineNumberLimit","Magnitudes below this use inline steppers; larger numbers retain exact text editing. Positive value.")]
  private const double InlineNumberLimit = 1e12; // Magnitudes below this use inline steppers; larger numbers retain exact text editing.
  private static readonly double ConfiguredInlineNumberLimit = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.InlineNumberLimit",InlineNumberLimit); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("vSettings","vTools.Commands.vSettings.StatusTextLimit","Maximum status characters before truncating; tooltip retains the complete message. Positive value.")]
  private const int StatusTextLimit = 70; // Maximum status characters before truncating; tooltip retains the complete message.
  private static readonly int ConfiguredStatusTextLimit = global::vTools.CommandDefaults.Get("vSettings","vTools.Commands.vSettings.StatusTextLimit",StatusTextLimit); // Resolved through vSettings; restart Rhino after advanced changes.
  [CommandDefault("vSettings","vTools.Commands.vSettings.SearchDelayMilliseconds","Delay after typing before filtering settings, in milliseconds; zero filters immediately. Accepted range is 0 through 1000.")]
  private const int SearchDelayMilliseconds = 120; // Zero filters immediately; positive milliseconds coalesce fast typing.
  private static readonly int ConfiguredSearchDelayMilliseconds = Math.Clamp(CommandDefaults.Get("vSettings","vTools.Commands.vSettings.SearchDelayMilliseconds",SearchDelayMilliseconds),0,1000); // Resolved once; restart after advanced edits.
  private static readonly Size JsonEditorSize = new(560, 360); // Structured-value editor dimensions in logical pixels.
  private const string AllSections = "All commands"; // First command-list entry; searches across command and shared-service defaults.
  private const string OptionsMode = "Command options"; // Values used the next time a command reads its profile; document overrides are retained.
  private const string AdvancedMode = "Advanced defaults (restart)"; // Once-resolved factory overrides; restart Rhino to apply them reliably.

  public override string EnglishName => "vSettings";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    if (!ToolsOptionStore.TrySnapshot(out var profile))
    {
      Log.Write(EnglishName, $"Cannot open settings: {ToolsOptionStore.LastError}");
      RhinoApp.WriteLine($"vSettings: {ToolsOptionStore.LastError}");
      return Result.Failure;
    }
    try
    {
      using var dialog = new SettingsDialog(BuildRows(profile, CommandDefaults.FactoryOptions(), CommandDefaults.Entries()));
      dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
      return Result.Success;
    }
    catch (Exception ex)
    {
      Log.Write(EnglishName, $"Settings editor failed: {ex}");
      RhinoApp.WriteLine("vSettings: could not open settings; see the diagnostic log.");
      return Result.Failure;
    }
  }

  internal sealed record Choice(string Label, JsonNode? Value);

  internal sealed class SettingRow : System.ComponentModel.INotifyPropertyChanged
  {
    internal required string Section { get; init; }
    internal required string Key { get; init; }
    internal required string[] Path { get; init; }
    private JsonNode? _factory, _before;
    private bool _hasFactory, _existed;
    internal JsonNode? Factory { get => _factory; set { _factory=value; Invalidate(); } }
    internal bool HasFactory { get => _hasFactory; set { _hasFactory=value; Invalidate(); } }
    internal JsonNode? Before { get => _before; set { _before=value; Invalidate(); } }
    internal bool Existed { get => _existed; set { _existed=value; Invalidate(); } }
    internal bool HasInherited { get; init; }
    internal JsonNode? Inherited { get; init; }
    private JsonNode? _value;
    internal JsonNode? Value { get => _value; set { _value = value; Notify(); } }
    internal CommandDefaults.Entry? Advanced { get; init; }
    internal string OptionDescription { get; set; } = string.Empty;
    internal Type? SavedEnum { get; set; }
    internal Type? EnumType => Advanced?.Type.IsEnum == true ? Advanced.Type : SavedEnum;
    private Choice[] _choices = [];
    internal Choice[] Choices { get => _choices; set { _choices=value; Invalidate(); } }
    internal string InternalName { get; set; } = string.Empty;
    private bool? _dirty, _customized;
    private string? _optionName, _displayValue, _factoryText, _searchText;
    internal bool Dirty => _dirty ??= !ToolsOptionStore.SameValue(Value, Existed ? Before : HasInherited ? Inherited : Factory);
    public bool Customized => _customized ??= HasFactory && !Equivalent(Value, Factory);
    public bool Unsaved => Dirty;
    public bool Modified => Customized || Dirty;
    internal string OptionName => _optionName ??= Advanced?.Label ?? Friendly(Key.Split('.').Last());
    internal string DisplayName => OptionName + (Modified ? " *" : "");
    internal string DisplayValue => _displayValue ??= ChoiceIndex(Value) is var index && index >= 0 ? Choices[index].Label :
      Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : Value?.ToJsonString() ?? "Automatic";
    internal string FactoryText => _factoryText ??= !HasFactory ? "-" : ChoiceIndex(Factory) is var index && index >= 0 ? Choices[index].Label :
      Factory is JsonValue value && value.TryGetValue<string>(out var text) ? text : Factory?.ToJsonString() ?? "Automatic";
    internal string Description => Advanced?.Description ?? OptionDescription;
    internal string SearchText => _searchText ??= string.Join(" ",Section,OptionName,Key,InternalName,Description,DisplayValue);
    internal string CopyText(string field) => field switch
    {
      "name" => OptionName,
      "internal" => string.IsNullOrEmpty(InternalName) ? Key : InternalName,
      "value" => DisplayValue,
      "default" => FactoryText,
      _ => throw new ArgumentOutOfRangeException(nameof(field))
    };
    public string ToolTipText => Description + "\nBuilt-in default: " + FactoryText +
      (Choices.Length > 0 ? "\nChoices: " + string.Join(", ", Choices.Select(choice => choice.Label)) : "") +
      (Advanced != null ? "\nRestart Rhino after applying advanced defaults." : "\nSaved preference; commands may retain document-specific overrides.");
    internal Type? ValueType => Advanced?.Type;
    internal ToolsOptionStore.Change Change => new(Path, Existed, ToolsOptionStore.Clone(Before), ToolsOptionStore.Clone(Value));
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Invalidate()
    {
      _dirty=null; _customized=null; _optionName=null; _displayValue=null; _factoryText=null; _searchText=null;
    }
    internal void Notify()
    {
      Invalidate();
      foreach (string property in new[] { nameof(Customized), nameof(Unsaved), nameof(Modified), nameof(DisplayName), nameof(DisplayValue), nameof(FactoryText), nameof(ToolTipText) })
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));
    }
    internal int ChoiceIndex(JsonNode? value)
    {
      for (int i = 0; i < Choices.Length; i++)
        if (ToolsOptionStore.SameValue(value, Choices[i].Value) || value is JsonValue item && item.TryGetValue<string>(out var text) &&
          string.Equals(text, Choices[i].Label, StringComparison.OrdinalIgnoreCase)) return i;
      if (Section == "vSetPt" && Key == "preview" && value is JsonValue legacy && legacy.TryGetValue<bool>(out var enabled))
        return Array.FindIndex(Choices, choice => choice.Label == (enabled ? "All" : "Off"));
      if (Section == "vCurveToSpline" && Key == "joinMode" && value is JsonValue number &&
        number.TryGetValue<double>(out double oldIndex) && oldIndex == Math.Truncate(oldIndex) && oldIndex >= 0 && oldIndex < Choices.Length) return (int)oldIndex;
      return -1;
    }
    private bool Equivalent(JsonNode? a, JsonNode? b) => ToolsOptionStore.SameValue(a, b) || ChoiceIndex(a) is var index && index >= 0 && index == ChoiceIndex(b);
  }

  internal static SettingRow[] BuildRows(JsonObject profile, JsonObject factory, CommandDefaults.Entry[] advanced)
  {
    var rows = new List<SettingRow>();
    var named = CommandChoices.All();
    static (bool Found, JsonNode? Value) InheritedOption(string section, string key, JsonObject current)
    {
      string? legacy = (section, key) switch
      {
        ("vSeam", "partSeam" or "edgeSeam") => "allowance",
        ("vExportDXF", "notch_split_layer") => "notch_trim_layer",
        ("vShade", "finishedLayer") => "layer",
        ("vMiddleCurve", "lines") => "tangentLines",
        ("vMiddleCurve", "lineInterval") => "tangentLineInterval",
        ("vPart", "cleanup") => "removeInteriorLines",
        ("vMatch", "startFrom") => "randStart",
        ("vNest", "showGapMode") => "showGap",
        ("vNest", "tableGridMode") => "tableGrid",
        ("vUzip", "createParts") => "parts",
        _ => null
      };
      if (legacy == null || current[legacy] is not JsonValue value) return (false, null);
      if ((section, key) is ("vMatch", "startFrom") or ("vNest", "showGapMode" or "tableGridMode") or ("vUzip", "createParts"))
      {
        if (!value.TryGetValue<bool>(out bool enabled)) return (false, null);
        return (true, section == "vUzip" ? JsonValue.Create(enabled) : JsonValue.Create(section == "vMatch" ? enabled ? "Random" : "Pick" : key == "showGapMode" ? enabled ? "Current" : "No" : enabled ? "3Feet" : "Off"));
      }
      return (true, ToolsOptionStore.Clone(value));
    }
    void AddOptions(string section, JsonObject current, JsonObject defaults, string[] path)
    {
      foreach (string key in current.Select(pair => pair.Key).Union(defaults.Select(pair => pair.Key)).OrderBy(key => key))
      {
        if ((section, key) is ("vMatch", "randStart") or ("vLine", "angleLock") or ("vCleanup", "preselectShort") or
          ("vPart", "removeInteriorLines") or ("vExportDXF", "notch_trim_layer") or
          ("vShade", "layer" or "length") or
          ("vMiddleCurve", "tangentLines" or "tangentLineInterval") or ("vNotches", "label_side_flip") or
          ("vNest", "stack" or "showGap" or "tableGrid")) continue; // Superseded encodings remain in profiles for command-owned migration, not as competing editor controls.
        var location = path.Append(key).ToArray();
        var value = current[key];
        var original = defaults[key];
        if ((value ?? original) is JsonObject)
        { AddOptions(section, value as JsonObject ?? new(), original as JsonObject ?? new(), location); continue; }
        string normalized = key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        var enumType = advanced.FirstOrDefault(entry => entry.Section == section && entry.Type.IsEnum &&
          new[] { normalized, normalized + "mode" }.Contains(entry.Key.Split('.').Last().Replace("Default", "", StringComparison.Ordinal).ToLowerInvariant()))?.Type;
        var inherited = current.ContainsKey(key) ? (Found: false, Value: (JsonNode?)null) : InheritedOption(section, key, current);
        rows.Add(new SettingRow
        {
          Section = section, Key = string.Join(".", location.Skip(1)), Path = location,
          Factory = ToolsOptionStore.Clone(original), HasFactory = defaults.ContainsKey(key),
          Before = ToolsOptionStore.Clone(value), Existed = current.ContainsKey(key),
          HasInherited = inherited.Found, Inherited = ToolsOptionStore.Clone(inherited.Value),
          Value = ToolsOptionStore.Clone(current.ContainsKey(key) ? value : inherited.Found ? inherited.Value : original), SavedEnum = enumType
        });
      }
    }
    foreach (string section in profile.Select(pair => pair.Key).Union(factory.Select(pair => pair.Key)).OrderBy(key => key))
      if (section != CommandDefaults.Section && section != "IsolationDiagnostics" && (profile[section] ?? factory[section]) is JsonObject)
        AddOptions(section, profile[section] as JsonObject ?? new(), factory[section] as JsonObject ?? new(), [section]);
    foreach (var entry in advanced)
    {
      var values = (profile[CommandDefaults.Section] as JsonObject)?[entry.Section] as JsonObject;
      bool exists = values?.ContainsKey(entry.Key) == true;
      rows.Add(new SettingRow
      {
        Section = entry.Section, Key = entry.Key, Path = [CommandDefaults.Section, entry.Section, entry.Key],
        Factory = entry.FactoryNode, HasFactory = true, Advanced = entry,
        Existed = exists, Before = ToolsOptionStore.Clone(values?[entry.Key]),
        Value = ToolsOptionStore.Clone(exists ? values?[entry.Key] : entry.FactoryNode)
      });
    }
    static string Normalize(string key) => key.Replace("_", "", StringComparison.Ordinal).Replace("Default", "", StringComparison.Ordinal)
      .Replace("Index", "", StringComparison.Ordinal).ToLowerInvariant();
    foreach (var row in rows)
    {
      string key = Normalize(row.Key.Split('.').Last());
      var metadata = advanced.FirstOrDefault(entry => entry.Section == row.Section && entry.DescribesOption(row.Key)) ??
        advanced.FirstOrDefault(entry => entry.Section == row.Section && entry.Key.Split('.').Last().StartsWith("Default", StringComparison.Ordinal) &&
          (Normalize(entry.Key.Split('.').Last()) == key || Normalize(entry.Key.Split('.').Last()) == key + "mode"));
      row.OptionDescription = metadata?.Description ?? DescribeStoredOption(row.Section, row.Key);
      row.InternalName = row.Advanced?.SourceName ?? metadata?.SourceName ?? row.Key;
      if (row.Advanced == null && metadata?.Type.IsEnum == true) row.SavedEnum = metadata.Type;
      if (row.Section == "vNest" && row.Key is "showGapMode" or "tableGridMode")
        row.OptionDescription = DescribeStoredOption(row.Section, row.Key);
      var definition = named.FirstOrDefault(entry => entry.Section == row.Section &&
        (Normalize(entry.Key) == key || Normalize(entry.Key) + "mode" == key));
      if (definition != null)
      {
        bool indexed = row.Advanced != null ? row.Advanced.Type == typeof(int) : definition.Indexed;
        row.Choices = definition.Names.Select((name, index) => new Choice(name, indexed ? JsonValue.Create(index) : JsonValue.Create(name))).ToArray();
      }
      else if (row.EnumType is { } enumType)
      {
        using var reference = JsonDocument.Parse((row.Factory ?? row.Before)?.ToJsonString() ?? "null");
        bool indexed = row.Advanced == null && reference.RootElement.ValueKind == JsonValueKind.Number;
        row.Choices = Enum.GetNames(enumType).Select(name => new Choice(name, indexed
          ? JsonValue.Create(Convert.ToInt32(Enum.Parse(enumType, name), CultureInfo.InvariantCulture)) : JsonValue.Create(name))).ToArray();
      }
      if (!row.HasFactory && row.Advanced == null && row.Path.Length == 2)
      {
        var fallback = metadata;
        if (fallback != null)
        {
          row.Factory = fallback.FactoryNode; row.HasFactory = true;
          if (fallback.Type == typeof(int) && definition is { Indexed: false } && row.Choices.Length > 0 && row.Factory is JsonValue number && number.TryGetValue<int>(out int index) && index >= 0 && index < row.Choices.Length)
            row.Factory = ToolsOptionStore.Clone(row.Choices[index].Value);
        }
      }
    }
    foreach(var row in rows)row.Notify();
    return rows.ToArray();
  }

  internal static string Friendly(string value) => Regex.Replace(value.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");

  internal static string DescribeStoredOption(string section, string key) => (section, key) switch
  {
    ("HistoryHighlight", "enabled") => "true highlights objects with broken Rhino history; false leaves history feedback to Rhino. Read at plug-in startup.",
    ("vNest", "showGapMode") => "No hides clearance guides; Current shows the selected parts' guides; All shows guides for every placed preview part. Display only; never exported.",
    ("vNest", "tableGridMode") => "Off hides grid lines; 3Feet uses the indeterminate spacing; 4Feet uses the checked spacing and first-line offset. Physical-inch distances are editable in Advanced defaults. Display only; never exported.",
    ("vMirror", "textReplacements") => "Text pairs swapped when mirroring. Each rule defines a/b, literal or regex matching, whole-word matching and case preservation.",
    ("vNotches", "curve_sides") => "Remembered curve IDs and their notch-side choices in this profile; this is placement history, not a global side rule.",
    ("vUzip", "label") => "Starting label for the generated zipper parts; an empty value omits the label.",
    ("vUzip", "tail") => "Extra zipper-tail length in model units.",
    ("vUzip", "parts") => "Zipper part recipes: names, center layers, end treatment, notes, mirroring and offset bands. The createParts checkbox separately controls whether these outputs are generated.",
    ("vUzip", var path) when path.StartsWith("layers.", StringComparison.Ordinal) => path.EndsWith(".color", StringComparison.Ordinal)
      ? "Color used when creating this output layer, as #RRGGBB; existing layer colors are retained."
      : "Name or full path of the output layer for this zipper component.",
    (_, "windowPlacement") => "Remembered window position and size; reset to let the command choose its initial placement.",
    ("vNest", "materialPresets" or "materialDrafts" or "retiredMaterials" or "materialDatabaseMigrated") => "Material-library migration or draft data. Edit material properties with vMaterialManager rather than changing this stored record.",
    ("vNest", "selectedMaterial") => "Name of the material selected when vNest last closed; it must exist in the global material library.",
    ("vRectangle", var coordinate) when coordinate.StartsWith("lastB", StringComparison.Ordinal) => "Remembered coordinate of the last rectangle placement in model units; used for repeat placement, not rectangle dimensions.",
    _ => $"Stored {Friendly(key.Split('.').Last()).ToLowerInvariant()} preference for {section}; structured records must retain their existing JSON keys and value types."
  };

  internal static bool TryValue(SettingRow row, string text, out JsonNode? value, out string error)
  {
    value = null; error = "";
    var template = row.Value ?? row.Before ?? row.Factory;
    using var reference = JsonDocument.Parse(template?.ToJsonString() ?? "null");
    var kind = reference.RootElement.ValueKind;
    try
    {
      value = kind == JsonValueKind.String ? JsonValue.Create(text) : JsonNode.Parse(text);
      if (kind != JsonValueKind.Null && value != null)
      {
        using var parsed = JsonDocument.Parse(value.ToJsonString());
        bool boolean = kind is JsonValueKind.True or JsonValueKind.False && parsed.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
        if (parsed.RootElement.ValueKind != kind && !boolean) throw new FormatException($"Expected {kind}.");
      }
      if (row.Advanced != null && !CommandDefaults.Valid(row.Advanced, value, out error)) return false;
      if (row.Choices.Length > 0 && row.ChoiceIndex(value) < 0) throw new FormatException("Choose one of the available values.");
      if (kind == JsonValueKind.Number && value != null &&
        (!double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)))
        throw new FormatException("Enter a finite number.");
      return true;
    }
    catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
    { error = ex.Message; return false; }
  }

  private sealed class SettingsDialog : Dialog<bool>
  {
    private readonly SettingRow[] _rows;
    private readonly TextBox _search = new() { PlaceholderText = "Search commands, settings, descriptions" };
    private readonly CheckBox _modifiedOnly = new() { Text = "Modified only", Checked = ConfiguredDefaultModifiedOnly };
    private readonly DropDown _mode = new() { Items = { OptionsMode, AdvancedMode }, SelectedIndex = 0 };
    private readonly ListBox _sections = new() { Width = ConfiguredSectionWidth };
    private readonly GridView _grid = new() { AllowMultipleSelection = false, RowHeight = ConfiguredValueRowHeight };
    private readonly Label _description = new() { Height = ConfiguredEditorHeight, Wrap = WrapMode.Word };
    private readonly Label _status = new();
    private readonly Button _apply = new() { Text = "Apply", Enabled = false };
    private readonly Button _reset = new() { Text = "Reset Value", Enabled = false };
    private readonly Button _resetSection = new() { Text = "Reset Command", Enabled = false };
    private readonly Button _close = new() { Text = "Close" };
    private readonly List<ValueCell> _cells = [];
    private readonly Dictionary<SettingRow, string> _errors = [];
    private readonly Dictionary<string, SettingRow[]> _rowsBySection;
    private readonly UITimer _searchTimer = new() { Interval = Math.Max(1,ConfiguredSearchDelayMilliseconds)/1000d };
    private readonly FieldResetOverlay _clearSearch;
    private readonly ContextMenu _copyMenu = new();
    private SettingRow[] _filtered = [];
    private SettingRow? _contextRow;
    private SettingRow? _current;
    private ValueCell? _editingCell;
    private bool _updating;
    private bool _discard;
    private bool _closed;
    private int _sort;
    private bool _descending;

    private sealed class SectionItem : ListItem, System.ComponentModel.INotifyPropertyChanged
    {
      public bool Customized { get; private set; }
      public bool Unsaved { get; private set; }
      public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
      internal void Update(IEnumerable<SettingRow> rows)
      {
        bool customized = rows.Any(row => row.Customized), unsaved = rows.Any(row => row.Dirty);
        if(Customized==customized&&Unsaved==unsaved)return;
        Customized = customized;
        Unsaved = unsaved;
        Text = Key + (Customized || Unsaved ? " *" : "");
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Customized)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Unsaved)));
      }
    }

    internal SettingsDialog(SettingRow[] rows)
    {
      _rows = rows;
      _rowsBySection = rows.GroupBy(row=>row.Section).ToDictionary(group=>group.Key,group=>group.ToArray());
      _clearSearch = new(_search,()=>!string.IsNullOrEmpty(_search.Text),()=>
      { _search.Text=""; RefreshSections(); _search.Focus(); },"Clear search",clear:true);
      _searchTimer.Elapsed += (_,_) => RefreshSections();
      Title = "vSettings";
      ClientSize = InitialSize;
      MinimumSize = MinimumWindowSize;
      Padding = ConfiguredPaddingSize;
      _mode.ToolTip = "Command options are saved command preferences. Advanced defaults control internal appearance, timing, sampling and tolerances and require restarting Rhino.";
      _search.ToolTip = "Find settings by command, option name, explanation or current value.";
      _modifiedOnly.ToolTip = "Show only saved values that differ from built-in defaults and changes not yet applied.";
      _sections.ToolTip = "Choose a command or shared service. An asterisk marks customized or unapplied settings.";
      _apply.ToolTip = "Save changed settings to the loaded plug-in profile. Advanced defaults require a Rhino restart.";
      _reset.ToolTip = "Restore this setting's built-in value; click Apply to save the reset.";
      _resetSection.ToolTip = "Restore built-in values for this command in the current settings mode; click Apply to save.";
      _close.ToolTip = "Close settings; unapplied changes require confirmation.";
      _grid.Columns.Add(new GridColumn { HeaderText = "Setting", Width = ConfiguredSettingColumnWidth, Sortable = true,
        DataCell = new TextBoxCell { Binding = Binding.Property<SettingRow, string>(row => row.DisplayName) } });
      _grid.Columns.Add(new GridColumn { HeaderText = "Value", Width = ConfiguredValueColumnWidth, Sortable = true, Editable = true,
        DataCell = new CustomCell
        {
          GetIdentifier = _ => "setting-value",
          CreateCell = _ => { var cell = new ValueCell(this); _cells.Add(cell); return cell; },
          ConfigureCell = (args, control) => ((ValueCell)control).Configure(args.Item as SettingRow)
        } });
      _grid.Columns.Add(new GridColumn { HeaderText = "Built-in Default", Width = ConfiguredFactoryColumnWidth, Sortable = true,
        DataCell = new TextBoxCell { Binding = Binding.Property<SettingRow, string>(row => row.FactoryText) } });
      _grid.SelectionChanged += (_, _) => { if (!_updating) Select(_grid.SelectedItem as SettingRow); };
      _grid.ColumnHeaderClick += (_, e) => { int index = _grid.Columns.IndexOf(e.Column); _descending = _sort == index && !_descending; _sort = index; RefreshGrid(); };
      _grid.Load += (_, _) => { InstallRowStyles(); InstallCopyMenu(); };
      _sections.Load += (_, _) => InstallSectionStyles();
      _mode.SelectedIndexChanged += (_, _) => RefreshSections();
      _search.TextChanged += (_, _) =>
      {
        _clearSearch.Refresh(); _searchTimer.Stop();
        if(ConfiguredSearchDelayMilliseconds==0)RefreshSections();else _searchTimer.Start();
      };
      _modifiedOnly.CheckedChanged += (_, _) => RefreshSections();
      _sections.SelectedIndexChanged += (_, _) => { if (!_updating) RefreshGrid(); };
      _reset.Click += (_, _) => { if (_current != null) Reset(_current); };
      _resetSection.Click += (_, _) =>
      {
        if (MessageBox.Show(this, "Restore this command's built-in defaults?", "Reset Command", MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) return;
        foreach (var row in CurrentMode().Where(row => row.Section == _sections.SelectedKey && row.HasFactory))
        { row.Value = ToolsOptionStore.Clone(row.Factory); _errors.Remove(row); }
        foreach (var cell in _cells) cell.Configure(cell.Row, true);
        ReloadValues(); UpdateStatus();
      };
      _apply.Click += (_, _) => Save();
      _close.Click += (_, _) => Close(true);
      Closing += (_, e) =>
      {
        if (_discard || !_rows.Any(row => row.Dirty) && _errors.Count == 0) return;
        var answer = MessageBox.Show(this, "Save changed settings before closing?", "vSettings", MessageBoxButtons.YesNoCancel, MessageBoxType.Question);
        if (answer == DialogResult.Cancel || answer == DialogResult.Yes && !Save()) { e.Cancel = true; return; }
        _discard = true;
      };
      Closed += (_, _) =>
      {
        _closed = true; _searchTimer.Stop(); _searchTimer.Dispose(); _clearSearch.Dispose(); _copyMenu.Dispose();
        foreach (var cell in _cells) cell.DisposeOverlays(); _cells.Clear();
      };
      Content = new TableLayout { Spacing = new Size(ConfiguredSpacing, ConfiguredSpacing), Rows =
      {
        new TableRow(new TableLayout { Spacing = new Size(ConfiguredSpacing, 0), Rows = { new TableRow(_mode, new TableCell(_search, true), _modifiedOnly) } }),
        new TableRow(new TableLayout { Spacing = new Size(ConfiguredSpacing, 0), Rows = { new TableRow(_sections, new TableCell(_grid, true)) } }) { ScaleHeight = true },
        new TableRow(_description),
        new TableRow(new TableLayout { Spacing = new Size(ConfiguredSpacing, 0), Rows = { new TableRow(new TableCell(_status, true), _resetSection, _reset, _apply, _close) } })
      } };
      AbortButton = _close;
      RefreshSections();
    }

    private IEnumerable<SettingRow> CurrentMode() => _rows.Where(row => (_mode.SelectedIndex == 1) == (row.Advanced != null));
    private void RefreshSections()
    {
      _searchTimer.Stop();
      if(_closed||IsDisposed)return;
      string query=_search.Text?.Trim()??"";
      bool modified=_modifiedOnly.Checked==true;
      _filtered=CurrentMode().Where(row=>(!modified||row.Modified)&&(query.Length==0||row.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase))).ToArray();
      string previous = _sections.SelectedKey ?? "";
      var sections=_filtered.Select(row=>row.Section).Distinct().OrderBy(value=>value).ToArray();
      // A zero-result query must not broaden a command-specific search.
      if(query.Length>0&&previous.Length>0&&!sections.Contains(previous)&&CurrentMode().Any(row=>row.Section==previous))
        sections=sections.Append(previous).OrderBy(value=>value).ToArray();
      _updating = true;
      if(!_sections.Items.Select(item=>item.Key).SequenceEqual(new[]{""}.Concat(sections)))
      {
        var existing=_sections.Items.OfType<SectionItem>().ToDictionary(item=>item.Key);
        _sections.Items.Clear(); _sections.Items.Add(new ListItem { Key = "", Text = AllSections });
        foreach(var section in sections)_sections.Items.Add(existing.GetValueOrDefault(section)??new SectionItem {Key=section,Text=section});
      }
      _sections.SelectedKey = _sections.Items.Any(item => item.Key == previous) ? previous : "";
      _updating = false;
      RefreshGrid();
    }
    private void RefreshGrid()
    {
      string section = _sections.SelectedKey ?? "";
      var rows = _filtered.Where(row => section.Length == 0 || row.Section == section);
      Func<SettingRow, string> key = _sort switch { 1 => row => row.DisplayValue, 2 => row => row.FactoryText, _ => row => row.Key };
      var sorted = (_descending ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase)).ToArray();
      _updating = true;
      if(_grid.DataStore is not SettingRow[] displayed||!displayed.SequenceEqual(sorted))_grid.DataStore=sorted;
      _grid.SelectedRow = _current == null ? -1 : Array.IndexOf(sorted, _current); _updating = false;
      GridSortIndicators.Update(_grid, _sort, _descending);
      _resetSection.Enabled = section.Length > 0 && sorted.Any(row => row.HasFactory);
      Select(_grid.SelectedItem as SettingRow);
    }
    private void Focus(SettingRow row)
    {
      if (_grid.DataStore is SettingRow[] rows && Array.IndexOf(rows, row) is int index && index >= 0 && _grid.SelectedRow != index) _grid.SelectedRow = index;
      Select(row);
    }
    private void Select(SettingRow? row)
    {
      _current = row;
      _description.Text = row?.Description ?? "";
      _description.ToolTip = row?.ToolTipText ?? "";
      UpdateStatus();
    }
    private void UpdateStatus()
    {
      int dirty = _rows.Count(row => row.Dirty), customized = _rows.Count(row => row.Customized);
      _apply.Enabled = dirty > 0 && _errors.Count == 0;
      _reset.Enabled = _current?.HasFactory == true && (_current.Customized || _errors.ContainsKey(_current));
      string status = _errors.Count > 0 ? _errors.First().Value : $"{dirty} unsaved; {customized} customized" +
        (_rows.Any(row => row.Dirty && row.Advanced != null) ? "; restart required" : "");
      _status.ToolTip = status;
      _status.Text = status.Length > ConfiguredStatusTextLimit ? status[..ConfiguredStatusTextLimit] + "..." : status;
      foreach (var item in _sections.Items.OfType<SectionItem>()) item.Update(_rowsBySection[item.Key]);
    }
    private void InstallCopyMenu()
    {
      if(_grid.ControlObject is not System.Windows.Controls.DataGrid native||_copyMenu.Items.Count>0)return;
      foreach(var choice in new[]{("Copy Option Name","name"),("Copy Internal Name","internal"),("Copy Current Value","value"),("Copy Default Value","default")})
      {
        var item=new ButtonMenuItem {Text=choice.Item1};
        item.Click+=(_,_)=>
        {
          if(_contextRow==null)return;
          try { System.Windows.Clipboard.SetText(_contextRow.CopyText(choice.Item2)); }
          catch(System.Runtime.InteropServices.ExternalException ex)
          { Log.Write("vSettings","Clipboard unavailable: {0}",ex.Message); _status.Text="Clipboard is busy; try again."; }
        };
        _copyMenu.Items.Add(item);
      }
      native.PreviewMouseRightButtonDown+=(_,e)=>
      {
        var position=e.GetPosition(native);
        var target=native.InputHitTest(position) as System.Windows.DependencyObject;
        var container=target==null?null:System.Windows.Controls.ItemsControl.ContainerFromElement(native,target) as System.Windows.Controls.DataGridRow;
        _contextRow=container?.Item as SettingRow;
        if(_contextRow==null)return;
        Focus(_contextRow); e.Handled=true;
        _copyMenu.Items[3].Enabled=_contextRow.HasFactory;
        _copyMenu.Show(_grid,new PointF((float)position.X,(float)position.Y));
      };
    }
    private void ReloadValues()
    {
      if (_modifiedOnly.Checked == true) { RefreshSections(); return; }
      _updating = true;
      try { _grid.ReloadData(Enumerable.Range(0, (_grid.DataStore as SettingRow[])?.Length ?? 0)); }
      finally { _updating = false; }
    }
    private void Reset(SettingRow row)
    {
      if (!row.HasFactory) return;
      row.Value = ToolsOptionStore.Clone(row.Factory); _errors.Remove(row);
      foreach (var cell in _cells.Where(cell => ReferenceEquals(cell.Row, row))) cell.Configure(row, true);
      ReloadValues(); UpdateStatus();
    }
    private void Change(SettingRow row, string text, ValueCell cell)
    {
      if (!TryValue(row, text, out var value, out var error)) { _errors[row] = error; UpdateStatus(); return; }
      Store(row, value, cell);
    }
    private void Store(SettingRow row, JsonNode? value, ValueCell? cell = null)
    {
      if (!ReferenceEquals(_current, row)) Focus(row);
      _editingCell = cell;
      try
      {
        row.Value = ToolsOptionStore.Clone(value); _errors.Remove(row);
        // Bound row labels and styles update themselves; rebuilding the active editor drops keyboard input.
        if (cell == null) ReloadValues();
      }
      finally { _editingCell = null; }
      UpdateStatus(); cell?.RefreshOverlays();
    }
    private void FinishInlineEdit(ValueCell cell)
    {
      Application.Instance.AsyncInvoke(() =>
      {
        if (_closed || IsDisposed || cell.IsDisposed || _cells.Any(editor => editor.HasEditorFocus)) return;
        if (_modifiedOnly.Checked == true || !string.IsNullOrWhiteSpace(_search.Text) || _sort == 1) RefreshSections();
      });
    }
    private bool Save()
    {
      if (_errors.Count > 0) return false;
      var changed = _rows.Where(row => row.Dirty).ToArray();
      if (!ToolsOptionStore.ApplyChanges(changed.Select(row => row.Change).ToArray()))
      {
        Log.Write("vSettings", $"Save failed: {ToolsOptionStore.LastError}");
        MessageBox.Show(this, ToolsOptionStore.LastError, "Settings not saved", MessageBoxButtons.OK, MessageBoxType.Error); return false;
      }
      bool restart = changed.Any(row => row.Advanced != null);
      foreach (var row in changed) { row.Before = ToolsOptionStore.Clone(row.Value); row.Existed = true; row.Notify(); }
      ReloadValues(); UpdateStatus(); _status.Text = restart ? "Saved; restart Rhino for advanced defaults" : "Saved";
      Log.Write("vSettings", $"Saved {changed.Length} defaults; advanced_restart={restart}"); return true;
    }

    private void EditJson(SettingRow row)
    {
      using var dialog = new Dialog<bool> { Title = row.Section + " / " + Friendly(row.Key), ClientSize = JsonEditorSize, Padding = ConfiguredPaddingSize };
      var text = new TextArea { Wrap = false, Text = row.Value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }) ?? "null" };
      var error = new Label { TextColor = Colors.Red, Wrap = WrapMode.Word };
      var save = new Button { Text = "Apply" }; var cancel = new Button { Text = "Cancel" };
      save.Click += (_, _) => { if (!TryValue(row, text.Text, out var value, out var message)) { error.Text = message; return; } Store(row, value); dialog.Close(true); };
      cancel.Click += (_, _) => dialog.Close(false);
      dialog.AbortButton = cancel;
      dialog.Content = new TableLayout { Spacing = new Size(ConfiguredSpacing, ConfiguredSpacing), Rows = { new TableRow(text) { ScaleHeight = true }, new TableRow(error),
        new TableRow(new TableLayout { Rows = { new TableRow(null, save, cancel) } }) } };
      dialog.ShowModal(this);
    }

    private sealed class ValueCell : Panel
    {
      private readonly SettingsDialog _owner;
      private TextBox? _text, _colorHex;
      private DropDown? _choices;
      private CheckBox? _boolean;
      private NumericStepper? _number;
      private ColorPicker? _color;
      private TableLayout? _colorEditor;
      private Button? _json;
      private readonly List<FieldResetOverlay> _overlays = [];
      private bool _assigning;
      private bool _unknownChoice;
      internal SettingRow? Row { get; private set; }
      internal bool HasEditorFocus => !IsDisposed && ControlObject is System.Windows.UIElement { IsKeyboardFocusWithin: true };
      internal ValueCell(SettingsDialog owner)
      {
        _owner = owner;
        Padding = new Padding(0, 1);
      }
      private T Editor<T>(ref T? slot,Func<T> create,Action<T> subscribe,bool reset=false) where T:Control
      {
        if(slot!=null)return slot;
        var control=slot=create();subscribe(control);
        control.GotFocus += (_, _) => { if (Row != null) _owner.Focus(Row); };
        control.LostFocus += (_, _) => _owner.FinishInlineEdit(this);
        control.Load += (_, _) => SetEditorContrast(control);
        if(reset)_overlays.Add(new FieldResetOverlay(control, () => Row?.HasFactory == true && Row.Customized,
          () => { if (Row != null) _owner.Reset(Row); }, "Restore built-in default"));
        return control;
      }
      private TextBox TextEditor=>Editor(ref _text,()=>new TextBox(),control=>control.TextChanged+=(_,_)=>Change(control.Text),true);
      private CheckBox BooleanInput=>Editor(ref _boolean,()=>new CheckBox(),control=>control.CheckedChanged+=(_,_)=>Change(control.Checked==true?"true":"false"));
      private NumericStepper NumberInput=>Editor(ref _number,()=>new NumericStepper(),control=>control.ValueChanged+=(_,_)=>Change(control.Value.ToString("G17",CultureInfo.InvariantCulture)),true);
      private DropDown ChoiceInput=>Editor(ref _choices,()=>new DropDown(),control=>control.SelectedIndexChanged+=(_,_)=>
        {
          if (_assigning || Row == null) return;
          int index = control.SelectedIndex - (_unknownChoice ? 1 : 0);
          if (index >= 0 && index < Row.Choices.Length) _owner.Store(Row, Row.Choices[index].Value, this);
        },true);
      private TextBox HexInput=>Editor(ref _colorHex,()=>new TextBox {Width=ConfiguredColorHexWidth,
        ToolTip="Enter #RRGGBB for an opaque color or #AARRGGBB including alpha; the # prefix is optional."},control=>control.TextChanged+=(_,_)=>ChangeHex());
      private ColorPicker ColorInput=>Editor(ref _color,()=>new ColorPicker {AllowAlpha=true},control=>control.ValueChanged+=(_,_)=>{if(!_assigning)ChangeColor();});
      private TableLayout ColorEditor=>_colorEditor??=new TableLayout {Spacing=new Size(ConfiguredSpacing,0),Rows={new TableRow(HexInput,new TableCell(ColorInput,true))}};
      private Button JsonInput=>Editor(ref _json,()=>new Button {Text="Edit..."},control=>control.Click+=(_,_)=>{if(Row!=null)_owner.EditJson(Row);});
      private void ChangeColor()
      {
        var value = ColorInput.Value;
        string hex = $"#{(int)Math.Round(value.A * 255):X2}{(int)Math.Round(value.R * 255):X2}{(int)Math.Round(value.G * 255):X2}{(int)Math.Round(value.B * 255):X2}";
        _assigning = true; try { HexInput.Text = hex; } finally { _assigning = false; }
        Change(hex);
      }
      private void ChangeHex()
      {
        if (_assigning || Row == null) return;
        string text = HexInput.Text.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint argb))
        { _owner.Change(Row, HexInput.Text, this); return; }
        if (text.Length == 6) argb |= 0xFF000000;
        _assigning = true;
        try
        {
          ColorInput.Value = new Color(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f);
        }
        finally { _assigning = false; }
        Change($"#{argb:X8}");
      }
      private void Change(string text) { if (!_assigning && Row != null) _owner.Change(Row, text, this); }
      internal void Configure(SettingRow? row, bool force = false)
      {
        if (row == null) { Row = null; Content = null; return; }
        if (!force && ReferenceEquals(Row, row) && (ReferenceEquals(_owner._editingCell, this) || HasEditorFocus)) { RefreshOverlays(); return; }
        Row = row; _assigning = true;
        try
        {
          string raw = row.Value?.ToJsonString() ?? "null";
          using var json = JsonDocument.Parse(raw);
          var kind = json.RootElement.ValueKind;
          Control editor;
          if (row.Choices.Length > 0)
          {
            int selected = row.ChoiceIndex(row.Value); _unknownChoice = selected < 0;
            var choices=ChoiceInput;choices.Items.Clear();
            if (_unknownChoice) choices.Items.Add("Unknown: " + row.DisplayValue);
            foreach (var choice in row.Choices) choices.Items.Add(choice.Label);
            choices.SelectedIndex = _unknownChoice ? 0 : selected; editor = choices;
          }
          else if (row.ValueType == typeof(System.Drawing.Color) && uint.TryParse(row.DisplayValue.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
          { HexInput.Text = $"#{argb:X8}"; ColorInput.Value = new Color(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f); editor = ColorEditor; }
          else if (kind is JsonValueKind.True or JsonValueKind.False)
          { BooleanInput.Checked = json.RootElement.GetBoolean(); editor = BooleanInput; }
          else if (kind == JsonValueKind.Number && json.RootElement.TryGetDouble(out double number) &&
            (number == 0 || Math.Abs(number) >= Math.Pow(10, -ConfiguredNumberPrecision)) && Math.Abs(number) < ConfiguredInlineNumberLimit)
          {
            bool integer = row.ValueType is { } type ? type == typeof(int) || type == typeof(long) : !raw.Contains('.') && !raw.Contains("e", StringComparison.OrdinalIgnoreCase);
            var bounds = row.Advanced == null ? (double.MinValue, double.MaxValue) : CommandDefaults.Bounds(row.Advanced);
            if (number < bounds.Item1 || number > bounds.Item2) { TextEditor.Text = raw; editor = TextEditor; }
            else { var input=NumberInput;input.DecimalPlaces = integer ? 0 : ConfiguredNumberPrecision; input.MinValue = bounds.Item1; input.MaxValue = bounds.Item2; input.Increment = 1; input.Value = number; editor = input; }
          }
          else if (kind is JsonValueKind.Array or JsonValueKind.Object) editor = JsonInput;
          else { TextEditor.Text = kind == JsonValueKind.String ? row.DisplayValue : raw; editor = TextEditor; }
          editor.ToolTip = row.ToolTipText;
          if (!ReferenceEquals(Content, editor)) Content = editor;
          SetEditorContrast(editor);
        }
        finally { _assigning = false; }
        RefreshOverlays();
      }
      internal void RefreshOverlays() { foreach (var overlay in _overlays) overlay.Refresh(); }
      internal void DisposeOverlays() { foreach (var overlay in _overlays) overlay.Dispose(); }
    }

    private void InstallRowStyles()
    {
      if (_grid.ControlObject is not System.Windows.Controls.DataGrid native) return;
      System.Windows.Media.SolidColorBrush Brush(Color color) => new(System.Windows.Media.Color.FromRgb((byte)Math.Round(color.R * 255), (byte)Math.Round(color.G * 255), (byte)Math.Round(color.B * 255)));
      foreach (var column in native.Columns)
      {
        var basis = column.CellStyle ?? native.CellStyle ?? native.TryFindResource(typeof(System.Windows.Controls.DataGridCell)) as System.Windows.Style;
        var style = new System.Windows.Style(typeof(System.Windows.Controls.DataGridCell), basis);
        style.Setters.Add(new System.Windows.Setter(System.Windows.FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(SettingRow.ToolTipText))));
        foreach (var state in new[] { (Property: nameof(SettingRow.Customized), Color: CustomizedBackground), (Property: nameof(SettingRow.Unsaved), Color: UnsavedBackground) })
        {
          var trigger = new System.Windows.DataTrigger { Binding = new System.Windows.Data.Binding(state.Property), Value = true };
          trigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brush(state.Color)));
          trigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.Black));
          trigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.FontWeightProperty, System.Windows.FontWeights.SemiBold));
          style.Triggers.Add(trigger);
        }
        var selected = new System.Windows.Trigger { Property = System.Windows.Controls.DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brush(ActiveSelectionBackground)));
        selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.White));
        style.Triggers.Add(selected);
        var inactive = new System.Windows.MultiTrigger();
        inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.DataGridCell.IsSelectedProperty, true));
        inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty, false));
        inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, Brush(InactiveSelectionBackground)));
        inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.Black));
        style.Triggers.Add(inactive);
        if (column != native.Columns[1]) style.Setters.Add(new System.Windows.EventSetter(System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler(BindTextColor)));
        column.CellStyle = style;
      }
    }
    private static void BindTextColor(object sender, System.Windows.RoutedEventArgs args)
    {
      if (sender is not System.Windows.Controls.Control cell) return;
      void Visit(System.Windows.DependencyObject element)
      {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); index++)
        {
          var child = System.Windows.Media.VisualTreeHelper.GetChild(element, index);
          if (child is System.Windows.Controls.TextBlock text) text.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = cell });
          Visit(child);
        }
      }
      Visit(cell);
    }

    private static void SetEditorContrast(Control editor)
    {
      if (editor.ControlObject is not System.Windows.DependencyObject root) return;
      static System.Windows.Media.Brush Foreground(System.Windows.Media.Brush? background)
      {
        var color = background is System.Windows.Media.SolidColorBrush solid && solid.Color.A > 0 ? solid.Color : System.Windows.SystemColors.WindowColor;
        static double Linear(byte channel) { double value = channel / 255.0; return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4); }
        double luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > ConfiguredTextContrastThreshold ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
      }
      void Visit(System.Windows.DependencyObject element, System.Windows.Controls.Control? parent)
      {
        if (element is System.Windows.Controls.Control control)
        { control.SetCurrentValue(System.Windows.Controls.Control.ForegroundProperty, control.IsEnabled ? Foreground(control.Background) : System.Windows.SystemColors.GrayTextBrush); parent = control; }
        if (element is System.Windows.Controls.TextBlock text && parent != null)
          text.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = parent });
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); index++)
          Visit(System.Windows.Media.VisualTreeHelper.GetChild(element, index), parent);
      }
      Visit(root, null);
    }

    private void InstallSectionStyles()
    {
      if (_sections.ControlObject is not System.Windows.Controls.ListBox native) return;
      var basis = native.ItemContainerStyle ?? native.TryFindResource(typeof(System.Windows.Controls.ListBoxItem)) as System.Windows.Style;
      var style = new System.Windows.Style(typeof(System.Windows.Controls.ListBoxItem), basis);
      // Theme templates may paint selection independently of Background; keep both colors under this style's control.
      var border = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
      border.SetValue(System.Windows.Controls.Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
      border.SetValue(System.Windows.Controls.Border.PaddingProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
      var content = new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
      content.SetValue(System.Windows.Controls.ContentPresenter.ContentSourceProperty, "Content");
      content.SetValue(System.Windows.Controls.ContentPresenter.HorizontalAlignmentProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.HorizontalContentAlignmentProperty));
      content.SetValue(System.Windows.Controls.ContentPresenter.VerticalAlignmentProperty, new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.VerticalContentAlignmentProperty));
      border.AppendChild(content);
      style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty,
        new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.ListBoxItem)) { VisualTree = border }));
      style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.HorizontalContentAlignmentProperty, System.Windows.HorizontalAlignment.Stretch));
      foreach (var state in new[] { (Property: nameof(SectionItem.Customized), Color: CustomizedBackground), (Property: nameof(SectionItem.Unsaved), Color: UnsavedBackground) })
      {
        var trigger = new System.Windows.DataTrigger { Binding = new System.Windows.Data.Binding(state.Property), Value = true };
        trigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,
          new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)Math.Round(state.Color.R * 255), (byte)Math.Round(state.Color.G * 255), (byte)Math.Round(state.Color.B * 255)))));
        trigger.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.Black));
        style.Triggers.Add(trigger);
      }
      var selected = new System.Windows.Trigger { Property = System.Windows.Controls.ListBoxItem.IsSelectedProperty, Value = true };
      selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(ActiveSelectionBackground.R*255),(byte)(ActiveSelectionBackground.G*255),(byte)(ActiveSelectionBackground.B*255)))));
      selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.White));
      style.Triggers.Add(selected);
      var inactive = new System.Windows.MultiTrigger();
      inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.ListBoxItem.IsSelectedProperty, true));
      inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty, false));
      inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(InactiveSelectionBackground.R*255),(byte)(InactiveSelectionBackground.G*255),(byte)(InactiveSelectionBackground.B*255)))));
      inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty, System.Windows.Media.Brushes.Black));
      style.Triggers.Add(inactive);
      style.Setters.Add(new System.Windows.EventSetter(System.Windows.FrameworkElement.LoadedEvent, new System.Windows.RoutedEventHandler(BindTextColor)));
      native.ItemContainerStyle = style;
    }
  }
}
