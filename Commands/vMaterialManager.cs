using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.UI;
using Material = vTools.MaterialLibrary.MaterialPreset;
using GrainMode = vTools.MaterialLibrary.GrainMode;
using CatalogMaterial = vTools.PatternSmithCatalog.CatalogMaterial;
using CatalogResult = vTools.PatternSmithCatalog.CatalogResult;

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
  private const int StatusWidth = 120; // Logical-pixel width for the catalog identity and save-state column.
  private const string NumberFormat = "0.########"; // Up to eight decimal places, without changing stored precision until edited.
  private const string NewMaterialName = "New material"; // Unique-name seed for new library entries.
  private const string CopySuffix = " copy"; // Unique-name seed suffix for duplicated entries.
  private const double NewWidth = 60; // Initial material width in document units.
  private const double NewGap = 0; // Initial minimum part separation in document units.
  private const double NewDeviation = 0; // Initial allowed grain deviation in degrees, zero through ninety.
  private const string CommandName = "vMaterialManager"; // Command/log/profile identity.
  [CommandDefault(CommandName,"vTools.Commands.vMaterialManager.SyncDialogWidth","Positive logical-pixel width of the PatternSmith material import picker.")]
  private const int SyncDialogWidth = 780; // Positive logical pixels; import columns remain resizable.
  [CommandDefault(CommandName,"vTools.Commands.vMaterialManager.SyncDialogHeight","Positive logical-pixel height of the PatternSmith material import picker.")]
  private const int SyncDialogHeight = 460; // Positive logical pixels; material rows scroll.
  private const string NewPresetChoice = "Create new preset"; // Explicit picker choice; unmatched PS materials are not selected automatically.
  private const double DefaultUnitsPerInch = 1; // Inches per inch for direct editor construction; normal command use supplies document-unit conversion.
  private static MaterialWindow? _window;

  public override string EnglishName => CommandName;

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    => Open(doc);

  internal static Result Open(RhinoDoc doc,Window? modalOwner=null)
  {
    if (modalOwner==null&&_window is { Host.IsDisposed: false } existing)
    { existing.Host.BringToFront(); existing.Host.Focus(); return Result.Success; }
    if (!MaterialLibrary.TrySnapshot(out var snapshot, out var error))
    { RhinoApp.WriteLine("vMaterialManager: {0}", error); return Result.Failure; }
    try
    {
      double unitsPerInch=doc.ModelUnitSystem is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits
        ?double.NaN:RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem);
      if(modalOwner!=null)
      {
        using var dialog=new Dialog();
        _=new MaterialWindow(snapshot,unitsPerInch,dialog);
        dialog.ShowModal(modalOwner);
      }
      else
      {
        var form=new Form{Owner=RhinoEtoApp.MainWindowForDocument(doc)};
        var window=new MaterialWindow(snapshot,unitsPerInch,form);
        _window=window;
        form.Closed+=(_,_)=>{if(ReferenceEquals(_window,window))_window=null;};
        form.Show();
      }
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
    public bool IsPatternSmith => Value.PatternSmith != null;
    public string CatalogDescription => Value.PatternSmith is {} link
      ? $"PatternSmith: {link.Name}\n{link.Group}\nID: {link.Id}" : "Custom material";
  }

  internal sealed record SyncResult(IReadOnlyList<Material> Materials,int Added,int Updated,IReadOnlyList<string> Skipped);
  private static string ComparableName(string name)=>MaterialLibrary.MaterialPresetName(name).TrimEnd('"','\u2033').TrimEnd();
  internal static SyncResult ApplyPatternSmithMappings(IEnumerable<Material> current,CatalogResult catalog,
    IReadOnlyDictionary<CatalogMaterial,string?> assignments,double unitsPerInch)
  {
    if(string.IsNullOrWhiteSpace(catalog.Identity))throw new InvalidOperationException("The PatternSmith catalog is unavailable.");
    var materials=current.ToList();int added=0,updated=0;
    var targets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var pair in assignments)
    {
      var source=pair.Key;if(!catalog.Materials.Contains(source))throw new InvalidOperationException("Choose a material from the current PS catalog.");
      Material? existing=pair.Value==null?null:materials.FirstOrDefault(material=>material.Name.Equals(pair.Value,StringComparison.OrdinalIgnoreCase));
      if(pair.Value!=null&&existing==null)throw new InvalidOperationException("The selected vNest preset no longer exists: "+pair.Value);
      string name=existing?.Name??MaterialLibrary.MaterialPresetName(source.Name);
      if(!targets.Add(name))throw new InvalidOperationException("More than one PS material is mapped to vNest preset '"+name+"'.");
      if(existing==null&&materials.Any(material=>material.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Preset '"+name+"' already exists. Choose it in the vNest preset column.");
      Material next;
      if(existing!=null)next=existing;
      else
      {
        if(!double.IsFinite(unitsPerInch)||unitsPerInch<=0)throw new InvalidOperationException("Set known document units before creating new presets.");
        if(source.Width is not {} width||!double.IsFinite(width)||width<=0)throw new InvalidOperationException(source.Name+": no valid roll width.");
        double gap=NewGap;string? spacing=(string?)source.Settings.Attribute("copySpacing");
        if(spacing!=null&&(!double.TryParse(spacing,NumberStyles.Float,CultureInfo.InvariantCulture,out gap)||!double.IsFinite(gap)||gap<0))
          throw new InvalidOperationException(source.Name+": invalid copy spacing.");
        next=new(name,width*unitsPerInch,gap*unitsPerInch,NewDeviation);
      }
      next=next with {PatternSmith=new(source.Id,source.Name,source.Group,catalog.Identity)};
      if(!MaterialLibrary.Valid(next,out string error))throw new InvalidOperationException(name+": "+error);
      if(existing==null){materials.Add(next);added++;}else if(next!=existing){materials[materials.IndexOf(existing)]=next;updated++;}
    }
    return new(materials,added,updated,Array.Empty<string>());
  }
  internal static SyncResult MergePatternSmith(IEnumerable<Material> current,CatalogResult catalog,
    IEnumerable<CatalogMaterial> selected,double unitsPerInch,bool replaceDimensions)
  {
    if(!double.IsFinite(unitsPerInch)||unitsPerInch<=0)throw new InvalidOperationException("Set known document units before synchronizing materials.");
    if(string.IsNullOrWhiteSpace(catalog.Identity))throw new InvalidOperationException("The PatternSmith catalog is unavailable.");
    var materials=current.ToList();var skipped=new List<string>();int added=0,updated=0;
    var choices=selected.Distinct().ToArray();
    var conflicts=choices.GroupBy(material=>MaterialLibrary.MaterialPresetName(material.Name),StringComparer.OrdinalIgnoreCase)
      .Where(group=>group.Count()>1).Select(group=>group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach(var source in choices)
    {
      string name=MaterialLibrary.MaterialPresetName(source.Name);
      if(!catalog.Materials.Contains(source)||conflicts.Contains(name)) {skipped.Add(name+": ambiguous catalog name");continue;}
      var linked=materials.Where(material=>material.PatternSmith is {} link&&link.Id==source.Id&&PatternSmithCatalog.SameIdentity(link.Catalog,catalog.Identity)).ToArray();
      if(linked.Length>1){skipped.Add(name+": multiple local materials share this catalog ID");continue;}
      var matches=materials.Where(material=>ComparableName(material.Name).Equals(ComparableName(name),StringComparison.OrdinalIgnoreCase)).ToArray();
      var existing=linked.SingleOrDefault()??(matches.Length==1?matches[0]:null);
      if(existing==null&&matches.Length>1){skipped.Add(name+": choose the existing preset explicitly");continue;}
      if(existing?.PatternSmith is {} other&&(other.Id!=source.Id||!PatternSmithCatalog.SameIdentity(other.Catalog,catalog.Identity)))
      {skipped.Add(name+": already linked to another catalog material");continue;}
      if(source.Width is not {} width||!double.IsFinite(width)||width<=0){skipped.Add(name+": no valid roll width");continue;}
      double gap=NewGap;
      string? spacing=(string?)source.Settings.Attribute("copySpacing");
      if(spacing!=null&&(!double.TryParse(spacing,NumberStyles.Float,CultureInfo.InvariantCulture,out gap)||!double.IsFinite(gap)||gap<0))
      {skipped.Add(name+": invalid copy spacing");continue;}
      var next=(existing??new Material(name,width*unitsPerInch,gap*unitsPerInch,NewDeviation)) with
        {PatternSmith=new(source.Id,source.Name,source.Group,catalog.Identity)};
      if(existing!=null&&replaceDimensions)next=next with {Width=width*unitsPerInch,Gap=gap*unitsPerInch};
      if(!MaterialLibrary.Valid(next,out string error)){skipped.Add(name+": "+error);continue;}
      if(existing==null){materials.Add(next);added++;}
      else if(next!=existing){materials[materials.IndexOf(existing)]=next;updated++;}
    }
    return new(materials,added,updated,skipped);
  }

  private sealed class PatternSmithSyncDialog : Dialog<bool>
  {
    private readonly CatalogResult _catalog;
    private readonly DropDown _folder=new();
    private readonly SearchBox _search=new() {PlaceholderText="Find PS material"};
    private readonly GridView _grid=new() {RowHeight=RowHeight};
    private readonly CheckBox _all=new() {Text="Select visible",ThreeState=true};
    private readonly Label _status=new();
    private readonly HashSet<CatalogMaterial> _selected=[];
    private readonly Dictionary<CatalogMaterial,string> _targets=[];
    private readonly IReadOnlyList<Material> _existing;
    private readonly double _unitsPerInch;
    private string[] _folders=[];
    private bool _updating;
    internal CatalogMaterial[] Selected=>_selected.ToArray();
    internal SyncResult? SyncChanges {get;private set;}

    internal PatternSmithSyncDialog(CatalogResult catalog,IReadOnlyList<Material> existing,double unitsPerInch=DefaultUnitsPerInch)
    {
      _catalog=catalog;_existing=existing;_unitsPerInch=unitsPerInch;Title="Map PatternSmith materials";Padding=ConfiguredWindowPadding;Resizable=true;
      ClientSize=new(Math.Max(MinimumWindowSize.Width,CommandDefaults.Get(CommandName,"vTools.Commands.vMaterialManager.SyncDialogWidth",SyncDialogWidth)),
        Math.Max(MinimumWindowSize.Height,CommandDefaults.Get(CommandName,"vTools.Commands.vMaterialManager.SyncDialogHeight",SyncDialogHeight)));
      _folders=new[]{""}.Concat(catalog.Groups.Where(group=>group.Length>0).Concat(catalog.Materials.Select(material=>material.Group).Where(group=>group.Length>0))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(PatternSmithCatalog.PreferredGroup).ThenBy(group=>group,StringComparer.OrdinalIgnoreCase)).ToArray();
      _folder.DataStore=_folders.Select(group=>group.Length==0?"All folders":group).ToArray();
      _folder.SelectedIndex=Math.Max(0,Array.FindIndex(_folders,group=>PatternSmithCatalog.PreferredGroup(group)));
      foreach(var material in catalog.Materials)
      {
        var matching=existing.Where(preset=>ComparableName(preset.Name).Equals(ComparableName(material.Name),StringComparison.OrdinalIgnoreCase)).ToArray();
        var unlinked=matching.Where(preset=>preset.PatternSmith==null).ToArray();
        var target=unlinked.Length==1?unlinked[0]:matching.Length==1?matching[0]:existing.FirstOrDefault(preset=>preset.PatternSmith is {} link&&link.Id==material.Id&&PatternSmithCatalog.SameIdentity(link.Catalog,catalog.Identity));
        _targets[material]=target?.Name??NewPresetChoice;
        if(target!=null&&InFolder(material))_selected.Add(material);
      }
      _grid.Columns.Add(new GridColumn {HeaderText="",DataCell=new CheckBoxCell(0),Editable=true,Width=Spacing*5,Resizable=false});
      _grid.Columns.Add(new GridColumn {HeaderText="PS material",DataCell=new TextBoxCell(1),Width=NameWidth,Expand=true,Resizable=true});
      _grid.Columns.Add(new GridColumn {HeaderText="Folder",DataCell=new TextBoxCell(2),Width=NameWidth/2,Resizable=true});
      _grid.Columns.Add(new GridColumn {HeaderText="Width (in)",DataCell=new TextBoxCell(3),Width=NumberWidth,Resizable=true});
      _grid.Columns.Add(new GridColumn {HeaderText="Gap (in)",DataCell=new TextBoxCell(4),Width=NumberWidth,Resizable=true});
      var targets=existing.OrderBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase).ToArray();
      _grid.Columns.Add(new GridColumn {HeaderText="vNest preset",DataCell=new ComboBoxCell(5){DataStore=new[]{NewPresetChoice}.Concat(targets.Select(preset=>preset.Name)).ToArray()},Width=NameWidth,Resizable=true,Editable=true,
        HeaderToolTip="Map to an existing preset without changing its settings, or explicitly create a new preset."});
      MaterialAppearance.GridPicker(_grid,5,index=>index>0&&index<=targets.Length?targets[index-1].PatternSmith:null);
      MaterialAppearance.Grid(_grid,1,item=>item is GridItem {Tag:CatalogMaterial material}?new(material.Id,material.Name,material.Group,_catalog.Identity):null);
      MaterialAppearance.Grid(_grid,5,item=>item is GridItem {Tag:CatalogMaterial material}&&_targets.TryGetValue(material,out var name)?_existing.FirstOrDefault(preset=>preset.Name==name)?.PatternSmith:null);
      _grid.CellEdited+=(_,e)=>{if(e.Item is GridItem {Tag:CatalogMaterial material} item){if(e.Column==5){_targets[material]=Convert.ToString(item.Values[5],CultureInfo.CurrentCulture)??NewPresetChoice;_selected.Add(material);RefreshRows();}else{if(item.Values[0] is true)_selected.Add(material);else _selected.Remove(material);UpdateCount();}}};
      _folder.SelectedIndexChanged+=(_,_)=>RefreshRows();_search.TextChanged+=(_,_)=>RefreshRows();
      _all.CheckedChanged+=(_,_)=>{if(_updating)return;foreach(var material in VisibleMaterials())if(_all.Checked==true)_selected.Add(material);else _selected.Remove(material);RefreshRows();};
      var import=new Button {Text="Apply mappings",ToolTip="Stage selected catalog links without changing existing preset settings. Save All writes the global vNest library."};
      var cancel=new Button {Text="Cancel"};cancel.Click+=(_,_)=>Close(false);import.Click+=(_,_)=>
      {
        try{SyncChanges=ApplyPatternSmithMappings(_existing,catalog,_selected.ToDictionary(material=>material,material=>_targets[material]==NewPresetChoice?null:_targets[material]),_unitsPerInch);Close(true);}
        catch(Exception ex){_status.Text=ex.Message;}
      };
      DefaultButton=import;AbortButton=cancel;
      Content=new TableLayout {Spacing=new(Spacing,Spacing),Rows={
        new TableRow(new StackLayout {Orientation=Orientation.Horizontal,Spacing=Spacing,Items={new Label {Text="Folder",VerticalAlignment=VerticalAlignment.Center},_folder,new StackLayoutItem(_search,true)}}),
        new TableRow(new TableCell(_grid,true)){ScaleHeight=true},
        new TableRow(_all),
        new TableRow(new Label {Text="Source: "+catalog.Source}),
        new TableRow(new StackLayout {Orientation=Orientation.Horizontal,Spacing=Spacing,Items={new StackLayoutItem(_status,true),import,cancel}})}};
      RefreshRows();
    }
    private bool InFolder(CatalogMaterial material)=>_folder.SelectedIndex<=0||material.Group.Equals(_folders[_folder.SelectedIndex],StringComparison.OrdinalIgnoreCase)||
      material.Group.StartsWith(_folders[_folder.SelectedIndex]+"\\",StringComparison.OrdinalIgnoreCase);
    private IEnumerable<CatalogMaterial> VisibleMaterials()=>_catalog.Materials.Where(material=>InFolder(material)&&
      (material.Name.Contains(_search.Text??"",StringComparison.OrdinalIgnoreCase)||material.Group.Contains(_search.Text??"",StringComparison.OrdinalIgnoreCase)));
    private void RefreshRows()
    {
      _grid.DataStore=VisibleMaterials().Select(material=>new GridItem {Tag=material,Values=[_selected.Contains(material),material.Name,material.Group,
        material.Width?.ToString(NumberFormat,CultureInfo.CurrentCulture)??"Unavailable",(string?)material.Settings.Attribute("copySpacing")??NewGap.ToString(CultureInfo.CurrentCulture),_targets[material]]}).ToArray();
      UpdateCount();
    }
    private void UpdateCount()
    {
      var visible=VisibleMaterials().ToArray();int selected=visible.Count(_selected.Contains);_updating=true;
      try{_all.Checked=selected==0?false:selected==visible.Length?true:null;}finally{_updating=false;}
      _status.Text=CountText.Format(_selected.Count,"selected material");
    }
  }

  private sealed class MaterialWindow
  {
    internal Window Host {get;}
    private MaterialLibrary.Snapshot _snapshot;
    private readonly List<MaterialRow> _rows = [];
    private readonly GridView _grid = new() { AllowMultipleSelection = true, AllowColumnReordering = true, RowHeight = RowHeight };
    private readonly SearchBox _search = new() { PlaceholderText = "Find material" };
    private readonly Label _status = new();
    private readonly Button _save = new() { Text = "Save All" };
    private readonly Button _duplicate = new() { Text = "Duplicate", Enabled = false };
    private readonly Button _delete = new() { Text = "Delete", Enabled = false };
    private readonly Button _reset = new() { Text = "Revert", Enabled = false };
    private readonly Button _sync=new() {Text="Map PS...",ToolTip="Map existing vNest presets to PS materials without changing their nesting settings; creating a new preset is optional."};
    private readonly double _unitsPerInch;
    private CancellationTokenSource? _syncCancellation;
    private bool _updating;
    private bool _hostClosing;
    private int _sortColumn;
    private bool _descending;
    private MaterialRow[] _mouseEditSelection = [];
    private MaterialRow[] _editingSelection = [];

    internal MaterialWindow(MaterialLibrary.Snapshot snapshot,double unitsPerInch=DefaultUnitsPerInch,Window? host=null)
    {
      Host=host??new Form();
      _unitsPerInch=unitsPerInch;
      _snapshot = snapshot;
      Host.Title = CommandName;
      Host.ClientSize = InitialSize;
      Host.MinimumSize = MinimumWindowSize;
      Host.Padding = ConfiguredWindowPadding;
      _grid.Columns.Add(new GridColumn { HeaderText = "Material", DataCell = new TextBoxCell(0), Width = NameWidth, Expand = true, Editable = true, Sortable = true,
        HeaderToolTip = "Material names are edited individually; other editable fields apply to all selected materials." });
      foreach (var field in new[] { ("Width", 1), ("Gap", 2) })
        _grid.Columns.Add(new GridColumn { HeaderText = field.Item1, DataCell = new TextBoxCell(field.Item2), Width = NumberWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Grain", DataCell = new ComboBoxCell(3) { DataStore = Enum.GetNames<GrainMode>() }, Width = GrainWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Obey grain", DataCell = new CheckBoxCell(4), Width = CheckWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Deviation", DataCell = new TextBoxCell(5), Width = NumberWidth, Editable = true, Sortable = true, HeaderToolTip = "Allowed grain deviation in degrees, 0 to 90" });
      _grid.Columns.Add(new GridColumn { HeaderText = "180 reversal", DataCell = new CheckBoxCell(6), Width = CheckWidth, Editable = true, Sortable = true });
      _grid.Columns.Add(new GridColumn { HeaderText = "Status", DataCell = new TextBoxCell(7), Width = StatusWidth, Sortable = true });
      _grid.CellEditing += (_, e) =>
      {
        if (e.Item is GridItem { Tag: MaterialRow row })
          _editingSelection = _mouseEditSelection.Contains(row) ? _mouseEditSelection : SelectedRows();
      };
      _grid.CellEdited += (_, e) =>
      {
        if (!_updating && e.Item is GridItem { Tag: MaterialRow row } item) EditCell(row, e.Column, item.Values[e.Column]);
      };
      _grid.Load += (_, _) => ConfigureMaterialGrid();
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
      _sync.Click+=(_,_)=>SyncPatternSmith();
      close.Click += (_, _) => Host.Close();
      Host.Content = new TableLayout
      {
        Spacing = new Size(Spacing, Spacing), Rows =
        {
          new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = Spacing,
            Items = { add, _duplicate, _delete, _reset, new StackLayoutItem(_search, true), _sync, reload } }),
          new TableRow(new TableCell(_grid, true)) { ScaleHeight = true },
          new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = Spacing,
            Items = { new StackLayoutItem(_status, true), _save, close } })
        }
      };
      Host.Closing += (_, e) =>
      {
        if(_syncCancellation!=null){_syncCancellation.Cancel();if(!_hostClosing){e.Cancel=true;_status.Text="Cancelling catalog read...";}return;}
        if (!_hostClosing && (LocalChanges() || _rows.Any(row => row.Unsaved)))
          e.Cancel = !ApplyCloseDecision(CloseConfirmation.Show(Host, CommandName, "Save material changes before closing?", "Save"));
      };
      RhinoApp.Closing += HostClosing;
      Host.Closed += (_, _) => RhinoApp.Closing -= HostClosing;
      LoadSnapshot(snapshot);
    }

    private async void SyncPatternSmith()
    {
      if(_syncCancellation is {} pending){pending.Cancel();return;}
      using var cancel=new CancellationTokenSource();_syncCancellation=cancel;_sync.Text="Cancel Sync";
      _status.Text="Reading PatternSmith material catalog...";
      try
      {
        var catalog=await Task.Run(()=>PatternSmithCatalog.Load(cancel.Token),cancel.Token);
        if(Host.IsDisposed||_hostClosing)return;
        cancel.Token.ThrowIfCancellationRequested();
        if(catalog.Materials.Count==0){_status.Text="No supported PS roll materials available. Check the material server or library settings in vSettings.";return;}
        using var dialog=new PatternSmithSyncDialog(catalog,_rows.Select(row=>row.Value).ToArray(),_unitsPerInch);
        if(!dialog.ShowModal(Host))return;
        var result=dialog.SyncChanges!;
        foreach(var value in result.Materials)
        {
          var row=_rows.FirstOrDefault(row=>row.Value.Name.Equals(value.Name,StringComparison.OrdinalIgnoreCase));
          if(row==null)_rows.Add(new(value,null));else row.Value=value;
        }
        _search.Text="";RefreshRows();
        _status.Text=$"PS: {result.Added} added, {result.Updated} updated, {result.Skipped.Count} skipped. Save All to apply.";
        Log.Write(CommandName,"PatternSmith sync staged source={0} added={1} updated={2} skipped={3}; {4}",catalog.Source,result.Added,result.Updated,result.Skipped.Count,string.Join("; ",result.Skipped));
        if(result.Skipped.Count>0)MessageBox.Show(Host,string.Join("\n",result.Skipped),"Materials not imported",MessageBoxButtons.OK,MessageBoxType.Warning);
      }
      catch(OperationCanceledException){if(!Host.IsDisposed)_status.Text="PatternSmith synchronization cancelled.";}
      catch(Exception ex){Log.Write(CommandName,"PatternSmith sync failed: {0}",ex);if(!Host.IsDisposed)_status.Text="PatternSmith synchronization failed: "+ex.Message;}
      finally{_syncCancellation=null;if(!Host.IsDisposed)_sync.Text="Map PS...";}
    }

    private void HostClosing(object? sender, EventArgs e) { _hostClosing = true; Host.Close(); }
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

    private void RefreshRows(MaterialRow? current = null, IReadOnlyCollection<MaterialRow>? selection = null)
    {
      var selected = selection ?? (current != null ? new[] { current } : SelectedRows());
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
          row.Value.GrainDeviation.ToString(NumberFormat, CultureInfo.CurrentCulture), row.Value.AllowReverse,
          (row.IsPatternSmith ? "PS / " : "Custom / ") + (row.Unsaved ? "Unsaved" : "Saved")]
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
      _status.Text = $"{CountText.Format(_rows.Count, "material")}; {selected.Length} selected; {_rows.Count(row => row.Unsaved)} unsaved";
    }

    private void EditCell(MaterialRow row, int column, object? value)
    {
      var selected = _editingSelection.Contains(row) ? _editingSelection
        : _mouseEditSelection.Contains(row) ? _mouseEditSelection : SelectedRows();
      if (!selected.Contains(row)) selected = [row];
      _editingSelection = []; _mouseEditSelection = [];
      var targets = column == 0 ? new[] { row } : selected;
      var changes = new List<(MaterialRow Row, Material Value)>();
      string text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
      bool number = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) && double.IsFinite(parsed);
      string error = "";
      foreach (var target in targets)
      {
        var next = target.Value;
        switch (column)
        {
          case 0: next = next with { Name = MaterialLibrary.MaterialPresetName(text) }; break;
          case 1 when number: next = next with { Width = parsed }; break;
          case 2 when number: next = next with { Gap = parsed }; break;
          case 3 when Enum.TryParse<GrainMode>(text, true, out var mode): next = next with { Grain = mode }; break;
          case 4 when value is bool obey: next = next with { ObeyGrain = obey }; break;
          case 5 when number: next = next with { GrainDeviation = parsed }; break;
          case 6 when value is bool reverse: next = next with { AllowReverse = reverse }; break;
          default: error = "Enter a valid value."; break;
        }
        if (error.Length == 0) MaterialLibrary.Valid(next, out error);
        if (error.Length == 0 && _rows.Any(other => other != target && other.Value.Name.Equals(next.Name, StringComparison.OrdinalIgnoreCase))) error = "That material name already exists.";
        if (error.Length > 0) break;
        changes.Add((target, next));
      }
      if (error.Length == 0) foreach (var change in changes) change.Row.Value = change.Value;
      RefreshRows(selection: selected);
      if (error.Length > 0) _status.Text = error;
    }

    private void ConfigureMaterialGrid()
    {
      if (_grid.ControlObject is not System.Windows.Controls.DataGrid native) return;
      native.Columns[0].MinWidth = NameWidth;
      native.PreviewMouseLeftButtonDown -= CaptureEditSelection;
      native.PreviewMouseLeftButtonDown += CaptureEditSelection;
      native.PreviewKeyDown -= ClearMouseEditSelection;
      native.PreviewKeyDown += ClearMouseEditSelection;
      foreach (var column in native.Columns)
      {
        MaterialAppearance.Cell(native,column,new System.Windows.Data.Binding("Tag.IsPatternSmith"),new System.Windows.Data.Binding("Tag.CatalogDescription"));
      }
    }

    private void CaptureEditSelection(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      _mouseEditSelection = [];
      if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
      var source = e.OriginalSource as System.Windows.DependencyObject;
      while (source != null && source is not System.Windows.Controls.DataGridRow)
        source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
          ? System.Windows.Media.VisualTreeHelper.GetParent(source) : System.Windows.LogicalTreeHelper.GetParent(source);
      if (source is System.Windows.Controls.DataGridRow { IsSelected: true }) _mouseEditSelection = SelectedRows();
    }
    private void ClearMouseEditSelection(object sender, System.Windows.Input.KeyEventArgs e) => _mouseEditSelection = [];

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
      if (MessageBox.Show(Host, $"Delete {CountText.Format(selected.Length, "selected material")} from the library?\nExisting drawing parts and tables keep their material data.",
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
      if (LocalChanges() && MessageBox.Show(Host, "Discard local edits and reload the material library?", CommandName,
        MessageBoxButtons.YesNo, MessageBoxType.Question) != DialogResult.Yes) return;
      if (MaterialLibrary.TrySnapshot(out var snapshot, out var error)) LoadSnapshot(snapshot);
      else _status.Text = error;
    }
    private bool ApplyCloseDecision(CloseConfirmation.Decision decision) => decision switch
    {
      CloseConfirmation.Decision.Discard => true,
      CloseConfirmation.Decision.Save => SaveMaterials(),
      _ => false
    };

    private bool SaveMaterials()
    {
      if (!MaterialLibrary.SaveSnapshot(_snapshot, _rows.Select(row => row.Value), out var error)) { _status.Text = error; return false; }
      if (MaterialLibrary.TrySnapshot(out var snapshot, out error)) { LoadSnapshot(snapshot); _status.Text = "Material library saved"; }
      else _status.Text = error;
      return true;
    }
  }
}
