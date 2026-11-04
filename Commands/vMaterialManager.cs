using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using Material = vTools.MaterialLibrary.MaterialPreset;
using GrainMode = vTools.MaterialLibrary.GrainMode;

namespace vTools.Commands;

/// <summary>Organizes the global material presets shared by all drawings and Rhino versions.</summary>
[CommandStyle(Style.NotUndoable)]
public sealed class vMaterialManager : vToolsCommand
{
  // Window, editing and new-material defaults
  private static readonly Size InitialSize = new(1020, 460); // Initial client size in logical pixels.
  private static readonly Size MinimumWindowSize = new(760, 320); // Minimum usable window size in logical pixels.
  private const int Spacing = 6; // Logical-pixel spacing between controls.
  [global::vTools.CommandDefault("vMaterialManager", "vTools.Commands.vMaterialManager.WindowPadding", "Logical-pixel window-edge padding.")] private const int WindowPadding = 10; // Logical-pixel window-edge padding.

  private static readonly int ConfiguredWindowPadding = global::vTools.CommandDefaults.Get("vMaterialManager", "vTools.Commands.vMaterialManager.WindowPadding", WindowPadding); // Resolved once from advanced defaults; restart Rhino after changes.
  private const int RowHeight = 26; // Logical-pixel material-list row height.
  private const int NameWidth = 200; // Initial material-name column width in logical pixels; expands with the window.
  private const int NumberWidth = 90; // Initial numeric-column width in logical pixels.
  private const int GrainWidth = 80; // Initial grain-mode column width in logical pixels.
  private const int CheckWidth = 100; // Initial boolean-column width in logical pixels.
  private const string NumberFormat = "0.########"; // Up to eight decimal places, without changing stored precision until edited.
  private const string NewMaterialName = "New material"; // Unique-name seed for new library entries.
  private const string CopySuffix = " copy"; // Unique-name seed suffix for duplicated entries.
  private const double NewWidth = 60; // Initial material width in document units.
  private const double NewGap = 0; // Initial minimum part separation in document units.
  private const double NewDeviation = 0; // Initial allowed grain deviation in degrees, zero through ninety.
  private const string CommandName = "vMaterialManager"; // Command/log/profile identity.
  private static MaterialWindow? _window;

  public override string EnglishName => CommandName;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    => Open(doc);

  internal static Result Open(RhinoDoc doc)
  {
    if (_window is { IsDisposed: false }) { _window.BringToFront(); _window.Focus(); return Result.Success; }
    if (!MaterialLibrary.TrySnapshot(out var snapshot, out var error))
    { RhinoApp.WriteLine("vMaterialManager: {0}", error); return Result.Failure; }
    try
    {
      var window = new MaterialWindow(snapshot) { Owner = RhinoEtoApp.MainWindowForDocument(doc) };
      _window = window;
      window.Closed += (_, _) => { if (ReferenceEquals(_window, window)) _window = null; };
      window.Show();
      return Result.Success;
    }
    catch (Exception ex)
    {
      Log.Write(CommandName, "could not open material manager: {0}", ex);
      RhinoApp.WriteLine("vMaterialManager: could not open; see the diagnostic log.");
      return Result.Failure;
    }
  }

  private sealed class MaterialRow(Material value, Material? saved)
  {
    internal Material Value = value;
    internal Material Baseline = value;
    internal Material? Saved = saved;
    internal bool Dirty => Value != Baseline;
    internal bool Unsaved => Value != Saved;
  }

  private sealed class MaterialWindow : Form
  {
    private MaterialLibrary.Snapshot _snapshot;
    private readonly List<MaterialRow> _rows = [];
    private readonly GridView _grid = new() { AllowMultipleSelection = true, AllowColumnReordering = true, RowHeight = RowHeight };
    private readonly SearchBox _search = new() { PlaceholderText = "Find material" };
    private readonly Label _status = new();
    private readonly Button _save = new() { Text = "Save All" };
    private readonly Button _duplicate = new() { Text = "Duplicate", Enabled = false };
    private readonly Button _delete = new() { Text = "Delete", Enabled = false };
    private readonly Button _reset = new() { Text = "Revert", Enabled = false };
    private bool _updating;
    private bool _hostClosing;
    private int _sortColumn;
    private bool _descending;

    internal MaterialWindow(MaterialLibrary.Snapshot snapshot)
    {
      _snapshot = snapshot;
      Title = CommandName;
      ClientSize = InitialSize;
      MinimumSize = MinimumWindowSize;
      Padding = ConfiguredWindowPadding;
      _grid.Columns.Add(new GridColumn { HeaderText = "Material", DataCell = new TextBoxCell(0), Width = NameWidth, Expand = true, Editable = true, Sortable = true });
      foreach (var field in new[] { ("Width", 1), ("Gap", 2) })
        _grid.Columns.Add(new GridColumn { HeaderText = field.Item1, DataCell = new TextBoxCell(field.Item2), Width = NumberWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Grain", DataCell = new ComboBoxCell(3) { DataStore = Enum.GetNames<GrainMode>() }, Width = GrainWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Obey grain", DataCell = new CheckBoxCell(4), Width = CheckWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Deviation", DataCell = new TextBoxCell(5), Width = NumberWidth, Editable = true, Sortable = true, HeaderToolTip = "Allowed grain deviation in degrees, 0 to 90" });
      _grid.Columns.Add(new GridColumn { HeaderText = "180 reversal", DataCell = new CheckBoxCell(6), Width = CheckWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Status", DataCell = new TextBoxCell(7), Width = NumberWidth, Sortable = true });
      _grid.CellEdited += (_, e) =>
      {
        if (!_updating && e.Item is GridItem { Tag: MaterialRow row } item) EditCell(row, e.Column, item.Values[e.Column]);
      };
      _grid.ColumnHeaderClick += (_, e) =>
      {
        int column = _grid.Columns.IndexOf(e.Column);
        _descending = column == _sortColumn && !_descending;
        _sortColumn = column; RefreshRows();
      };
      _grid.SelectionChanged += (_, _) => { if (!_updating) UpdateActions(); };
      _grid.KeyDown += (_, e) => { if (e.Key == Keys.Delete) { DeleteSelected(); e.Handled = true; } };
      _search.TextChanged += (_, _) => RefreshRows();
      var add = new Button { Text = "New Material" };
      var reload = new Button { Text = "Reload" };
      var close = new Button { Text = "Close" };
      add.Click += (_, _) => AddMaterial();
      _duplicate.Click += (_, _) => DuplicateSelected();
      _delete.Click += (_, _) => DeleteSelected();
      _reset.Click += (_, _) => RevertSelected();
      reload.Click += (_, _) => Reload();
      _save.Click += (_, _) => SaveMaterials();
      close.Click += (_, _) => Close();
      Content = new TableLayout
      {
        Spacing = new Size(Spacing, Spacing), Rows =
        {
          new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = Spacing,
            Items = { add, _duplicate, _delete, _reset, new StackLayoutItem(_search, true), reload } }),
          new TableRow(new TableCell(_grid, true)) { ScaleHeight = true },
          new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = Spacing,
            Items = { new StackLayoutItem(_status, true), _save, close } })
        }
      };
      Closing += (_, e) =>
      {
        if (!_hostClosing && LocalChanges() && MessageBox.Show(this, "Discard unsaved material edits?", CommandName,
          MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) e.Cancel = true;
      };
      RhinoApp.Closing += HostClosing;
      Closed += (_, _) => RhinoApp.Closing -= HostClosing;
      LoadSnapshot(snapshot);
    }

    private void HostClosing(object? sender, EventArgs e) { _hostClosing = true; Close(); }
    private bool LocalChanges() => _rows.Any(row => row.Dirty || row.Saved == null) ||
      !_rows.Select(row => row.Baseline.Name).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_snapshot.Presets.Concat(_snapshot.Drafts).Select(material => material.Name));
    private MaterialRow[] SelectedRows() => _grid.SelectedItems.OfType<GridItem>().Select(item => item.Tag).OfType<MaterialRow>().ToArray();

    private void LoadSnapshot(MaterialLibrary.Snapshot snapshot)
    {
      _snapshot = snapshot; _rows.Clear();
      var drafts = snapshot.Drafts.ToDictionary(material => material.Name, StringComparer.OrdinalIgnoreCase);
      foreach (var saved in snapshot.Presets) _rows.Add(new(drafts.GetValueOrDefault(saved.Name) ?? saved, saved));
      foreach (var draft in snapshot.Drafts.Where(draft => !snapshot.Presets.Any(saved => saved.Name.Equals(draft.Name, StringComparison.OrdinalIgnoreCase))))
        _rows.Add(new(draft, null));
      RefreshRows();
    }

    private void RefreshRows(MaterialRow? current = null)
    {
      var selected = current != null ? new[] { current } : SelectedRows();
      var rows = _rows.Where(row => row.Value.Name.Contains(_search.Text ?? "", StringComparison.OrdinalIgnoreCase));
      object Key(MaterialRow row) => _sortColumn switch
      {
        1 => row.Value.Width, 2 => row.Value.Gap, 3 => row.Value.Grain.ToString(), 4 => row.Value.ObeyGrain,
        5 => row.Value.GrainDeviation, 6 => row.Value.AllowReverse, 7 => row.Unsaved, _ => row.Value.Name.ToUpperInvariant()
      };
      rows = _descending ? rows.OrderByDescending(Key) : rows.OrderBy(Key);
      var items = rows.Select(row => new GridItem
      {
        Tag = row, Values = [row.Value.Name, row.Value.Width.ToString(NumberFormat, CultureInfo.CurrentCulture),
          row.Value.Gap.ToString(NumberFormat, CultureInfo.CurrentCulture), row.Value.Grain.ToString(), row.Value.ObeyGrain,
          row.Value.GrainDeviation.ToString(NumberFormat, CultureInfo.CurrentCulture), row.Value.AllowReverse, row.Unsaved ? "Unsaved" : "Saved"]
      }).ToArray();
      _updating = true;
      try { _grid.DataStore = items; _grid.SelectedRows = items.Select((item, index) => (item, index)).Where(pair => selected.Contains(pair.item.Tag)).Select(pair => pair.index).ToArray(); }
      finally { _updating = false; }
      UpdateActions();
    }

    private void UpdateActions()
    {
      var selected = SelectedRows();
      _duplicate.Enabled = selected.Length == 1;
      _delete.Enabled = selected.Length > 0;
      _reset.Enabled = selected.Any(row => row.Dirty || row.Unsaved);
      _save.Enabled = LocalChanges() || _rows.Any(row => row.Unsaved);
      _status.Text = $"{_rows.Count} materials; {_rows.Count(row => row.Unsaved)} unsaved";
    }

    private void EditCell(MaterialRow row, int column, object? value)
    {
      var next = row.Value; string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
      bool number = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) && double.IsFinite(parsed);
      string error = "";
      switch (column)
      {
        case 0: next = next with { Name = MaterialLibrary.MaterialPresetName(text) }; break;
        case 1 when number: next = next with { Width = parsed }; break;
        case 2 when number: next = next with { Gap = parsed }; break;
        case 3 when Enum.TryParse<GrainMode>(text, true, out var mode): next = next with { Grain = mode }; break;
        case 4: next = next with { ObeyGrain = value is true }; break;
        case 5 when number: next = next with { GrainDeviation = parsed }; break;
        case 6: next = next with { AllowReverse = value is true }; break;
        default: error = "Enter a valid value."; break;
      }
      if (error.Length == 0) MaterialLibrary.Valid(next, out error);
      if (error.Length == 0 && _rows.Any(other => other != row && other.Value.Name.Equals(next.Name, StringComparison.OrdinalIgnoreCase))) error = "That material name already exists.";
      if (error.Length == 0) row.Value = next;
      RefreshRows(row);
      if (error.Length > 0) _status.Text = error;
    }

    private string UniqueName(string seed)
    {
      seed = seed[..Math.Min(seed.Length, MaterialLibrary.MaterialPresetNameLimit - CopySuffix.Length)];
      string name = seed; int suffix = 2;
      while (_rows.Any(row => row.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = seed + " " + suffix++;
      return name;
    }

    private void AddMaterial()
    {
      _search.Text = "";
      var row = new MaterialRow(new(UniqueName(NewMaterialName), NewWidth, NewGap, NewDeviation), null);
      _rows.Add(row); RefreshRows(row);
    }
    private void DuplicateSelected()
    {
      if (SelectedRows().SingleOrDefault() is not { } source) return;
      _search.Text = "";
      var row = new MaterialRow(source.Value with { Name = UniqueName(source.Value.Name + CopySuffix) }, null);
      _rows.Add(row); RefreshRows(row);
    }
    private void DeleteSelected()
    {
      var selected = SelectedRows(); if (selected.Length == 0) return;
      if (MessageBox.Show(this, $"Delete {selected.Length} selected material(s) from the library?\nExisting drawing parts and tables keep their material data.",
        CommandName, MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) return;
      foreach (var row in selected) _rows.Remove(row);
      RefreshRows();
    }
    private void RevertSelected()
    {
      foreach (var row in SelectedRows()) row.Value = row.Saved ?? row.Baseline;
      RefreshRows();
    }
    private void Reload()
    {
      if (LocalChanges() && MessageBox.Show(this, "Discard local edits and reload the material library?", CommandName,
        MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) return;
      if (MaterialLibrary.TrySnapshot(out var snapshot, out var error)) LoadSnapshot(snapshot);
      else _status.Text = error;
    }
    private void SaveMaterials()
    {
      if (!MaterialLibrary.SaveSnapshot(_snapshot, _rows.Select(row => row.Value), out var error)) { _status.Text = error; return; }
      if (MaterialLibrary.TrySnapshot(out var snapshot, out error)) { LoadSnapshot(snapshot); _status.Text = "Material library saved"; }
      else _status.Text = error;
    }
  }
}
