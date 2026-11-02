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
  private static readonly Size InitialSize = new(900, 650); // Initial settings window size in logical pixels.
  private static readonly Size MinimumWindowSize = new(660, 480); // Minimum usable settings window size in logical pixels.
  private const int SectionWidth = 180; // Command/service list width in logical pixels.
  private const int EditorHeight = 44; // Compact selected-setting description height in logical pixels; values are edited inline.
  private const int ValueRowHeight = 28; // Fixed logical-pixel grid row height, including native inline editors.
  private static readonly Color CustomizedBackground = Color.FromArgb(255, 242, 204); // RGB tint for saved settings differing from built-in defaults; use dark text.
  private static readonly Color UnsavedBackground = Color.FromArgb(255, 219, 179); // RGB tint for changed values not yet applied; selected rows retain native selection colors.
  private static readonly Color ActiveSelectionBackground = Color.FromArgb(42, 95, 150); // Focused selection RGB, paired with white text independently of Rhino theme overrides.
  private static readonly Color InactiveSelectionBackground = Color.FromArgb(211, 226, 242); // Unfocused selection RGB, paired with black text.
  private const double TextContrastThreshold = 0.179; // Relative-luminance threshold for choosing white versus black editor text; zero through one.
  private const int Spacing = 6; // Logical-pixel gaps between controls.
  private const int PaddingSize = 10; // Window edge padding in logical pixels.
  private const int NumberPrecision = 8; // Fractional digits available for non-integral settings; tiny tolerances also accept exponent text in JSON fields.
  private const double InlineNumberLimit = 1e12; // Magnitudes below this use inline steppers; larger numbers retain exact text editing.
  private const int StatusTextLimit = 70; // Maximum status characters before truncating; tooltip retains the complete message.
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
    internal JsonNode? Factory { get; set; }
    internal bool HasFactory { get; set; }
    internal JsonNode? Before { get; set; }
    internal bool Existed { get; set; }
    private JsonNode? _value;
    internal JsonNode? Value { get => _value; set { _value = value; Notify(); } }
    internal CommandDefaults.Entry? Advanced { get; init; }
    internal Type? SavedEnum { get; init; }
    internal Type? EnumType => Advanced?.Type.IsEnum == true ? Advanced.Type : SavedEnum;
    internal Choice[] Choices { get; set; } = [];
    internal bool Dirty => !ToolsOptionStore.SameValue(Value, Existed ? Before : Factory);
    public bool Customized => HasFactory && !Equivalent(Value, Factory);
    public bool Unsaved => Dirty;
    public bool Modified => Customized || Dirty;
    internal string DisplayName => Friendly(Key.Split('.').Last()) + (Modified ? " *" : "");
    internal string DisplayValue => ChoiceIndex(Value) is var index && index >= 0 ? Choices[index].Label :
      Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : Value?.ToJsonString() ?? "Automatic";
    internal string FactoryText => !HasFactory ? "-" : ChoiceIndex(Factory) is var index && index >= 0 ? Choices[index].Label :
      Factory is JsonValue value && value.TryGetValue<string>(out var text) ? text : Factory?.ToJsonString() ?? "Automatic";
    internal string Description => Advanced?.Description ?? string.Empty;
    internal Type? ValueType => Advanced?.Type;
    internal ToolsOptionStore.Change Change => new(Path, Existed, ToolsOptionStore.Clone(Before), ToolsOptionStore.Clone(Value));
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    internal void Notify()
    {
      foreach (string property in new[] { nameof(Customized), nameof(Unsaved), nameof(Modified), nameof(DisplayName), nameof(DisplayValue), nameof(FactoryText) })
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
    void AddOptions(string section, JsonObject current, JsonObject defaults, string[] path)
    {
      foreach (string key in current.Select(pair => pair.Key).Union(defaults.Select(pair => pair.Key)).OrderBy(key => key))
      {
        var location = path.Append(key).ToArray();
        var value = current[key];
        var original = defaults[key];
        if ((value ?? original) is JsonObject)
        { AddOptions(section, value as JsonObject ?? new(), original as JsonObject ?? new(), location); continue; }
        string normalized = key.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        var enumType = advanced.FirstOrDefault(entry => entry.Section == section && entry.Type.IsEnum &&
          new[] { normalized, normalized + "mode" }.Contains(entry.Key.Split('.').Last().Replace("Default", "", StringComparison.Ordinal).ToLowerInvariant()))?.Type;
        rows.Add(new SettingRow
        {
          Section = section, Key = string.Join(".", location.Skip(1)), Path = location,
          Factory = ToolsOptionStore.Clone(original), HasFactory = defaults.ContainsKey(key),
          Before = ToolsOptionStore.Clone(value), Existed = current.ContainsKey(key),
          Value = ToolsOptionStore.Clone(current.ContainsKey(key) ? value : original), SavedEnum = enumType
        });
      }
    }
    foreach (string section in profile.Select(pair => pair.Key).Union(factory.Select(pair => pair.Key)).OrderBy(key => key))
      if (section != CommandDefaults.Section && (profile[section] ?? factory[section]) is JsonObject)
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
        var fallback = advanced.FirstOrDefault(entry => entry.Section == row.Section && entry.Key.Split('.').Last().StartsWith("Default", StringComparison.Ordinal) &&
          (Normalize(entry.Key.Split('.').Last()) == key || Normalize(entry.Key.Split('.').Last()) == key + "mode"));
        if (fallback != null)
        {
          row.Factory = fallback.FactoryNode; row.HasFactory = true;
          if (fallback.Type == typeof(int) && definition is { Indexed: false } && row.Choices.Length > 0 && row.Factory is JsonValue number && number.TryGetValue<int>(out int index) && index >= 0 && index < row.Choices.Length)
            row.Factory = ToolsOptionStore.Clone(row.Choices[index].Value);
        }
      }
    }
    return rows.ToArray();
  }

  internal static string Friendly(string value) => Regex.Replace(value.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");

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
    private readonly DropDown _mode = new() { Items = { OptionsMode, AdvancedMode }, SelectedIndex = 0 };
    private readonly ListBox _sections = new() { Width = SectionWidth };
    private readonly GridView _grid = new() { AllowMultipleSelection = false, RowHeight = ValueRowHeight };
    private readonly Label _description = new() { Height = EditorHeight, Wrap = WrapMode.Word };
    private readonly Label _status = new();
    private readonly Button _apply = new() { Text = "Apply", Enabled = false };
    private readonly Button _reset = new() { Text = "Reset Value", Enabled = false };
    private readonly Button _resetSection = new() { Text = "Reset Command", Enabled = false };
    private readonly Button _close = new() { Text = "Close" };
    private readonly List<ValueCell> _cells = [];
    private readonly Dictionary<SettingRow, string> _errors = [];
    private SettingRow? _current;
    private ValueCell? _editingCell;
    private bool _updating;
    private bool _discard;
    private int _sort;
    private bool _descending;

    private sealed class SectionItem : ListItem, System.ComponentModel.INotifyPropertyChanged
    {
      public bool Customized { get; private set; }
      public bool Unsaved { get; private set; }
      public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
      internal void Update(IEnumerable<SettingRow> rows)
      {
        Customized = rows.Any(row => row.Customized);
        Unsaved = rows.Any(row => row.Dirty);
        Text = Key + (Customized || Unsaved ? " *" : "");
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Text)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Customized)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Unsaved)));
      }
    }

    internal SettingsDialog(SettingRow[] rows)
    {
      _rows = rows;
      Title = "vSettings";
      ClientSize = InitialSize;
      MinimumSize = MinimumWindowSize;
      Padding = PaddingSize;
      _mode.ToolTip = "Command options are saved command preferences. Advanced defaults control internal appearance, timing, sampling and tolerances and require restarting Rhino.";
      _grid.Columns.Add(new GridColumn { HeaderText = "Setting", Width = 260, Sortable = true,
        DataCell = new TextBoxCell { Binding = Binding.Property<SettingRow, string>(row => row.DisplayName) } });
      _grid.Columns.Add(new GridColumn { HeaderText = "Value", Width = 220, Sortable = true, Editable = true,
        DataCell = new CustomCell
        {
          GetIdentifier = _ => "setting-value",
          CreateCell = _ => { var cell = new ValueCell(this); _cells.Add(cell); return cell; },
          ConfigureCell = (args, control) => ((ValueCell)control).Configure(args.Item as SettingRow)
        } });
      _grid.Columns.Add(new GridColumn { HeaderText = "Built-in Default", Width = 170, Sortable = true,
        DataCell = new TextBoxCell { Binding = Binding.Property<SettingRow, string>(row => row.FactoryText) } });
      _grid.SelectionChanged += (_, _) => { if (!_updating) Select(_grid.SelectedItem as SettingRow); };
      _grid.ColumnHeaderClick += (_, e) => { int index = _grid.Columns.IndexOf(e.Column); _descending = _sort == index && !_descending; _sort = index; RefreshGrid(); };
      _grid.Load += (_, _) => InstallRowStyles();
      _sections.Load += (_, _) => InstallSectionStyles();
      _mode.SelectedIndexChanged += (_, _) => RefreshSections();
      _search.TextChanged += (_, _) => RefreshSections();
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
      Closed += (_, _) => { foreach (var cell in _cells) cell.DisposeOverlays(); _cells.Clear(); };
      Content = new TableLayout { Spacing = new Size(Spacing, Spacing), Rows =
      {
        new TableRow(new TableLayout { Spacing = new Size(Spacing, 0), Rows = { new TableRow(_mode, new TableCell(_search, true)) } }),
        new TableRow(new TableLayout { Spacing = new Size(Spacing, 0), Rows = { new TableRow(_sections, new TableCell(_grid, true)) } }) { ScaleHeight = true },
        new TableRow(_description),
        new TableRow(new TableLayout { Spacing = new Size(Spacing, 0), Rows = { new TableRow(new TableCell(_status, true), _resetSection, _reset, _apply, _close) } })
      } };
      AbortButton = _close;
      RefreshSections();
    }

    private IEnumerable<SettingRow> CurrentMode() => _rows.Where(row => (_mode.SelectedIndex == 1) == (row.Advanced != null));
    private bool Matches(SettingRow row) => string.IsNullOrWhiteSpace(_search.Text) ||
      (row.Section + " " + row.Key + " " + row.Description + " " + row.DisplayValue).Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    private void RefreshSections()
    {
      string previous = _sections.SelectedKey ?? "";
      _updating = true;
      _sections.Items.Clear(); _sections.Items.Add(new ListItem { Key = "", Text = AllSections });
      foreach (var section in CurrentMode().Where(Matches).Select(row => row.Section).Distinct().OrderBy(value => value))
        _sections.Items.Add(new SectionItem { Key = section, Text = section });
      _sections.SelectedKey = _sections.Items.Any(item => item.Key == previous) ? previous : "";
      _updating = false;
      RefreshGrid();
    }
    private void RefreshGrid()
    {
      string section = _sections.SelectedKey ?? "";
      var rows = CurrentMode().Where(row => (section.Length == 0 || row.Section == section) && Matches(row));
      Func<SettingRow, string> key = _sort switch { 1 => row => row.DisplayValue, 2 => row => row.FactoryText, _ => row => row.Key };
      var sorted = (_descending ? rows.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : rows.OrderBy(key, StringComparer.OrdinalIgnoreCase)).ToArray();
      _updating = true; _grid.DataStore = sorted; _grid.SelectedRow = _current == null ? -1 : Array.IndexOf(sorted, _current); _updating = false;
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
      _description.ToolTip = row == null ? "" : string.Join(" / ", row.Path);
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
      _status.Text = status.Length > StatusTextLimit ? status[..StatusTextLimit] + "..." : status;
      foreach (var item in _sections.Items.OfType<SectionItem>()) item.Update(_rows.Where(row => row.Section == item.Key));
    }
    private void ReloadValues()
    {
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
      Focus(row);
      _editingCell = cell;
      try { row.Value = ToolsOptionStore.Clone(value); _errors.Remove(row); ReloadValues(); }
      finally { _editingCell = null; }
      UpdateStatus(); cell?.RefreshOverlays();
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
      using var dialog = new Dialog<bool> { Title = row.Section + " / " + Friendly(row.Key), ClientSize = JsonEditorSize, Padding = PaddingSize };
      var text = new TextArea { Wrap = false, Text = row.Value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }) ?? "null" };
      var error = new Label { TextColor = Colors.Red, Wrap = WrapMode.Word };
      var save = new Button { Text = "Apply" }; var cancel = new Button { Text = "Cancel" };
      save.Click += (_, _) => { if (!TryValue(row, text.Text, out var value, out var message)) { error.Text = message; return; } Store(row, value); dialog.Close(true); };
      cancel.Click += (_, _) => dialog.Close(false);
      dialog.AbortButton = cancel;
      dialog.Content = new TableLayout { Spacing = new Size(Spacing, Spacing), Rows = { new TableRow(text) { ScaleHeight = true }, new TableRow(error),
        new TableRow(new TableLayout { Rows = { new TableRow(null, save, cancel) } }) } };
      dialog.ShowModal(this);
    }

    private sealed class ValueCell : Panel
    {
      private readonly SettingsDialog _owner;
      private readonly TextBox _text = new();
      private readonly DropDown _choices = new();
      private readonly CheckBox _boolean = new();
      private readonly NumericStepper _number = new();
      private readonly ColorPicker _color = new();
      private readonly Button _json = new() { Text = "Edit..." };
      private readonly List<FieldResetOverlay> _overlays = [];
      private bool _assigning;
      private bool _unknownChoice;
      internal SettingRow? Row { get; private set; }
      internal ValueCell(SettingsDialog owner)
      {
        _owner = owner;
        Padding = new Padding(0, 1);
        _text.TextChanged += (_, _) => Change(_text.Text);
        _boolean.CheckedChanged += (_, _) => Change(_boolean.Checked == true ? "true" : "false");
        _number.ValueChanged += (_, _) => Change(_number.Value.ToString("G17", CultureInfo.InvariantCulture));
        _color.ValueChanged += (_, _) => { var value = _color.Value; Change($"#{(int)Math.Round(value.A * 255):X2}{(int)Math.Round(value.R * 255):X2}{(int)Math.Round(value.G * 255):X2}{(int)Math.Round(value.B * 255):X2}"); };
        _choices.SelectedIndexChanged += (_, _) =>
        {
          if (_assigning || Row == null) return;
          int index = _choices.SelectedIndex - (_unknownChoice ? 1 : 0);
          if (index >= 0 && index < Row.Choices.Length) _owner.Store(Row, Row.Choices[index].Value, this);
        };
        _json.Click += (_, _) => { if (Row != null) _owner.EditJson(Row); };
        foreach (Control control in new Control[] { _text, _choices, _boolean, _number, _color, _json })
        {
          control.GotFocus += (_, _) => { if (Row != null) _owner.Focus(Row); };
          control.Load += (_, _) => SetEditorContrast(control);
        }
        foreach (Control control in new Control[] { _text, _choices, _number })
          _overlays.Add(new FieldResetOverlay(control, () => Row?.HasFactory == true && Row.Customized,
            () => { if (Row != null) _owner.Reset(Row); }, "Restore built-in default"));
      }
      private void Change(string text) { if (!_assigning && Row != null) _owner.Change(Row, text, this); }
      internal void Configure(SettingRow? row, bool force = false)
      {
        if (row == null) { Row = null; Content = null; return; }
        if (!force && ReferenceEquals(Row, row) && ReferenceEquals(_owner._editingCell, this)) { RefreshOverlays(); return; }
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
            _choices.Items.Clear();
            if (_unknownChoice) _choices.Items.Add("Unknown: " + row.DisplayValue);
            foreach (var choice in row.Choices) _choices.Items.Add(choice.Label);
            _choices.SelectedIndex = _unknownChoice ? 0 : selected; editor = _choices;
          }
          else if (row.ValueType == typeof(System.Drawing.Color) && uint.TryParse(row.DisplayValue.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
          { _color.Value = new Color(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, ((argb >> 24) & 255) / 255f); editor = _color; }
          else if (kind is JsonValueKind.True or JsonValueKind.False)
          { _boolean.Checked = json.RootElement.GetBoolean(); editor = _boolean; }
          else if (kind == JsonValueKind.Number && json.RootElement.TryGetDouble(out double number) &&
            (number == 0 || Math.Abs(number) >= Math.Pow(10, -NumberPrecision)) && Math.Abs(number) < InlineNumberLimit)
          {
            bool integer = row.ValueType is { } type ? type == typeof(int) : !raw.Contains('.') && !raw.Contains("e", StringComparison.OrdinalIgnoreCase);
            var bounds = row.Advanced == null ? (double.MinValue, double.MaxValue) : CommandDefaults.Bounds(row.Advanced);
            if (number < bounds.Item1 || number > bounds.Item2) { _text.Text = raw; editor = _text; }
            else { _number.DecimalPlaces = integer ? 0 : NumberPrecision; _number.MinValue = bounds.Item1; _number.MaxValue = bounds.Item2; _number.Increment = 1; _number.Value = number; editor = _number; }
          }
          else if (kind is JsonValueKind.Array or JsonValueKind.Object) editor = _json;
          else { _text.Text = kind == JsonValueKind.String ? row.DisplayValue : raw; editor = _text; }
          editor.ToolTip = row.Description.Length > 0 ? row.Description : row.Section + " / " + row.Key;
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
        return luminance > TextContrastThreshold ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White;
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
