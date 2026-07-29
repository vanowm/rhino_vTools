using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace vTools;

/// <summary>
/// Shared single-layer picker for commands that need values in addition to
/// the document's real layer table, such as a dynamic current-layer option.
/// </summary>
internal static class LayerSelector
{
  private static readonly ConditionalWeakTable<DropDown, LayerDropDownState>
    DropDownStates = new();

  internal static bool TrySelect(
    RhinoDoc doc,
    string selectedValue,
    string currentLayerValue,
    string title,
    RunMode runMode,
    bool allowNewLayer,
    out string result)
  {
    result = selectedValue;

    if (runMode == RunMode.Scripted)
      return TryGetManualValue(
        doc,
        selectedValue,
        currentLayerValue,
        title,
        allowNewLayer,
        out result);

    try
    {
      using var dialog = new LayerSelectorDialog(
        doc, selectedValue, currentLayerValue, title);
      if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow))
        return false;

      result = dialog.SelectedValue;
      return !string.IsNullOrWhiteSpace(result);
    }
    catch (Exception ex)
    {
      Log.Write("LayerSelector", $"Unable to show layer selector: {ex}");
      return false;
    }
  }

  internal static DropDown CreateDropDown(
    RhinoDoc doc,
    string selectedValue,
    string? currentLayerValue = null,
    int? width = null)
  {
    var dropDown = new DropDown();
    if (width.HasValue)
      dropDown.Width = width.Value;

    var state = new LayerDropDownState(doc, currentLayerValue);
    DropDownStates.Add(dropDown, state);
    ConfigureDropDown(dropDown);
    dropDown.Load += (_, _) => ConfigureDropDown(dropDown);
    PopulateDropDown(dropDown, state, selectedValue);
    dropDown.DropDownOpening += (_, _) =>
    {
      var selected = GetDropDownValue(dropDown, selectedValue);
      var revision = GetDropDownRevision(
        state.Doc, selected, state.CurrentLayerValue);
      if (revision != state.Revision)
        PopulateDropDown(dropDown, state, selected);
    };
    return dropDown;
  }

  internal static bool IsDropDownUpdating(DropDown dropDown) =>
    DropDownStates.TryGetValue(dropDown, out var state) && state.IsUpdating;

  internal static string GetDropDownValue(
    DropDown dropDown,
    string fallback)
  {
    if (dropDown.SelectedIndex < 0 ||
        dropDown.DataStore is not IEnumerable<LayerListItem> items)
    {
      return fallback;
    }

    var list = items.ToList();
    return dropDown.SelectedIndex < list.Count
      ? list[dropDown.SelectedIndex].Value
      : fallback;
  }

  internal static void SetDropDownValue(
    DropDown dropDown,
    string value)
  {
    if (DropDownStates.TryGetValue(dropDown, out var state))
    {
      PopulateDropDown(dropDown, state, value);
      return;
    }

    if (dropDown.DataStore is not IEnumerable<LayerListItem> items)
      return;

    var index = items.ToList().FindIndex(item => string.Equals(
      item.Value, value, StringComparison.OrdinalIgnoreCase));
    if (index >= 0)
      dropDown.SelectedIndex = index;
  }

  private static void ConfigureDropDown(DropDown dropDown)
  {
    if (dropDown.ControlObject is not System.Windows.Controls.ComboBox combo)
      return;

    System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(combo, true);
    System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(
      combo, System.Windows.Controls.VirtualizationMode.Recycling);
    System.Windows.Controls.ScrollViewer.SetCanContentScroll(combo, true);

    var itemPanel = new System.Windows.FrameworkElementFactory(
      typeof(System.Windows.Controls.VirtualizingStackPanel));
    itemPanel.SetValue(
      System.Windows.Controls.VirtualizingPanel.IsVirtualizingProperty, true);
    itemPanel.SetValue(
      System.Windows.Controls.VirtualizingPanel.VirtualizationModeProperty,
      System.Windows.Controls.VirtualizationMode.Recycling);
    combo.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(itemPanel);
  }

  private static void PopulateDropDown(
    DropDown dropDown,
    LayerDropDownState state,
    string selectedValue)
  {
    state.IsUpdating = true;
    try
    {
      var items = BuildItems(state.Doc, state.CurrentLayerValue);
      if (!items.Any(item => string.Equals(
            item.Value, selectedValue, StringComparison.OrdinalIgnoreCase)))
      {
        items.Insert(0, new LayerListItem(
          selectedValue,
          selectedValue,
          selectedValue,
          Colors.White,
          false));
      }

      dropDown.DataStore = items;
      dropDown.ItemTextBinding = Binding.Property<LayerListItem, string>(
        item => item.DisplayText);
      dropDown.ItemImageBinding = Binding.Property<LayerListItem, Image>(
        item => item.Swatch);
      var selectedIndex = items.FindIndex(item => string.Equals(
        item.Value, selectedValue, StringComparison.OrdinalIgnoreCase));
      dropDown.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
      state.Revision = GetDropDownRevision(
        state.Doc, selectedValue, state.CurrentLayerValue);
    }
    finally
    {
      state.IsUpdating = false;
    }
  }

  private static int GetDropDownRevision(
    RhinoDoc doc,
    string selectedValue,
    string? currentLayerValue)
  {
    var hash = new HashCode();
    hash.Add(selectedValue, StringComparer.OrdinalIgnoreCase);
    hash.Add(currentLayerValue, StringComparer.OrdinalIgnoreCase);
    hash.Add(doc.Layers.CurrentLayerIndex);
    hash.Add(doc.Views.ActiveView?.ActiveViewportID ?? Guid.Empty);

    foreach (var layer in doc.Layers)
    {
      if (layer == null)
        continue;

      hash.Add(layer.Id);
      hash.Add(layer.Index);
      hash.Add(layer.ParentLayerId);
      hash.Add(layer.SortIndex);
      hash.Add(layer.IsDeleted);
      hash.Add(layer.FullPath, StringComparer.Ordinal);
      hash.Add(ResolveLayerDisplayColor(doc, layer).ToArgb());
    }

    return hash.ToHashCode();
  }

  private static bool TryGetManualValue(
    RhinoDoc doc,
    string selectedValue,
    string currentLayerValue,
    string title,
    bool allowNewLayer,
    out string result)
  {
    result = selectedValue;

    while (true)
    {
      var getString = new GetString();
      getString.EnableTransparentCommands(true);
      getString.SetCommandPrompt(
        $"{title} name ({currentLayerValue}, . or * = current layer)");
      getString.SetDefaultString(selectedValue);
      getString.AcceptNothing(true);
      var getResult = getString.GetLiteralString();
      if (getString.CommandResult() == Result.Cancel)
        return false;

      var requested = getResult == Rhino.Input.GetResult.Nothing
        ? selectedValue
        : getString.StringResult();
      if (TryResolveManualValue(
            doc,
            requested,
            currentLayerValue,
            allowNewLayer,
            out result,
            out var error))
      {
        return true;
      }

      RhinoApp.WriteLine(error);
    }
  }

  private static bool TryResolveManualValue(
    RhinoDoc doc,
    string? requested,
    string currentLayerValue,
    bool allowNewLayer,
    out string result,
    out string error)
  {
    result = string.Empty;
    error = string.Empty;
    var value = requested?.Trim() ?? string.Empty;
    if (value.Length == 0)
    {
      error = "Layer name cannot be empty.";
      return false;
    }

    if (value == "." || value == "*" ||
        string.Equals(value, currentLayerValue, StringComparison.OrdinalIgnoreCase))
    {
      result = currentLayerValue;
      return true;
    }

    var fullPathIndex = doc.Layers.FindByFullPath(
      value, RhinoMath.UnsetIntIndex);
    if (IsUsableLayer(doc, fullPathIndex))
    {
      result = doc.Layers[fullPathIndex].FullPath;
      return true;
    }

    var matchingLayers = doc.Layers
      .Where(layer =>
        layer != null &&
        !layer.IsDeleted &&
        string.Equals(layer.Name, value, StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (matchingLayers.Count == 1)
    {
      result = matchingLayers[0].FullPath;
      return true;
    }

    if (matchingLayers.Count > 1)
    {
      error = $"Layer name '{value}' is ambiguous; enter its full path.";
      return false;
    }

    if (allowNewLayer)
    {
      result = value;
      return true;
    }

    error = $"Layer '{value}' was not found.";
    return false;
  }

  private static bool IsUsableLayer(RhinoDoc doc, int layerIndex)
  {
    if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
      return false;

    var layer = doc.Layers[layerIndex];
    return layer != null && !layer.IsDeleted;
  }

  private sealed class LayerSelectorDialog : Dialog<bool>
  {
    private readonly List<LayerListItem> _allItems;
    private readonly GridView _layerList;
    private readonly Button _selectButton;

    public LayerSelectorDialog(
      RhinoDoc doc,
      string selectedValue,
      string currentLayerValue,
      string title)
    {
      Title = title;
      Resizable = true;
      Result = false;
      ClientSize = new Size(420, 440);
      MinimumSize = new Size(300, 260);

      _allItems = BuildItems(doc, currentLayerValue);

      _layerList = new GridView
      {
        AllowEmptySelection = false,
        AllowMultipleSelection = false,
        DataStore = _allItems,
        RowHeight = 22,
        GridLines = GridLines.None,
        ShowHeader = false
      };
      _layerList.Columns.Add(new GridColumn
      {
        DataCell = new ImageViewCell
        {
          Binding = Binding.Property<LayerListItem, Image>(item => item.Swatch)
        },
        Resizable = false,
        Width = 26
      });
      _layerList.Columns.Add(new GridColumn
      {
        DataCell = new TextBoxCell
        {
          Binding = Binding.Property<LayerListItem, string>(item => item.DisplayText)
        },
        Expand = true
      });

      _selectButton = new Button { Text = "Select", Enabled = false };
      var cancelButton = new Button { Text = "Cancel" };
      var search = new SearchBox { PlaceholderText = "Filter layers" };

      _layerList.SelectedRowsChanged += (_, _) =>
        _selectButton.Enabled = _layerList.SelectedItem is LayerListItem;
      _layerList.CellDoubleClick += (_, _) => AcceptSelection();
      _selectButton.Click += (_, _) => AcceptSelection();
      cancelButton.Click += (_, _) => Close();
      search.TextChanged += (_, _) => ApplyFilter(search.Text, selectedValue);

      var buttons = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        Spacing = 8,
        Items = { cancelButton, _selectButton }
      };

      Content = new StackLayout
      {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Padding(10),
        Spacing = 8,
        Items =
        {
          search,
          new StackLayoutItem(_layerList, true),
          buttons
        }
      };

      DefaultButton = _selectButton;
      AbortButton = cancelButton;
      SelectValue(selectedValue);
    }

    public string SelectedValue =>
      (_layerList.SelectedItem as LayerListItem)?.Value ?? string.Empty;

    private void ApplyFilter(string? filter, string preferredValue)
    {
      var query = filter?.Trim() ?? string.Empty;
      var visible = query.Length == 0
        ? _allItems
        : _allItems.Where(item =>
            item.IsCurrentOption ||
            item.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
          .ToList();

      _layerList.DataStore = visible;
      SelectValue(preferredValue);
      if (_layerList.SelectedRow < 0 && visible.Count > 0)
        _layerList.SelectedRow = 0;
    }

    private void SelectValue(string value)
    {
      if (_layerList.DataStore is not IEnumerable<LayerListItem> items)
        return;

      var index = items
        .Select((item, itemIndex) => (item, itemIndex))
        .Where(entry => string.Equals(
          entry.item.Value, value, StringComparison.OrdinalIgnoreCase))
        .Select(entry => entry.itemIndex)
        .DefaultIfEmpty(-1)
        .First();

      _layerList.SelectedRow = index >= 0 ? index : 0;
    }

    private void AcceptSelection()
    {
      if (_layerList.SelectedItem is not LayerListItem)
        return;

      Result = true;
      Close();
    }

  }

  private static List<LayerListItem> BuildItems(
    RhinoDoc doc,
    string? currentLayerValue)
  {
    var items = new List<LayerListItem>();
    if (!string.IsNullOrWhiteSpace(currentLayerValue))
    {
      items.Add(new LayerListItem(
        currentLayerValue,
        currentLayerValue,
        currentLayerValue,
        ToEtoColor(ResolveLayerDisplayColor(doc, doc.Layers.CurrentLayer)),
        true));
    }

    var layers = doc.Layers
      .Where(layer =>
        layer != null &&
        !layer.IsDeleted &&
        !string.IsNullOrWhiteSpace(layer.FullPath))
      .OrderBy(layer => layer.SortIndex)
      .ToList();

    var childrenByParent = new Dictionary<Guid, List<Layer>>();
    foreach (var layer in layers)
    {
      if (!childrenByParent.TryGetValue(layer.ParentLayerId, out var children))
      {
        children = new List<Layer>();
        childrenByParent[layer.ParentLayerId] = children;
      }

      children.Add(layer);
    }

    void AddChildren(Guid parentId, int depth)
    {
      if (!childrenByParent.TryGetValue(parentId, out var children))
        return;

      foreach (var layer in children.OrderBy(child => child.SortIndex))
      {
        var indent = depth == 0 ? string.Empty : new string(' ', depth * 2);
        items.Add(new LayerListItem(
          layer.FullPath,
          indent + layer.Name,
          layer.FullPath,
          ToEtoColor(ResolveLayerDisplayColor(doc, layer)),
          false));
        AddChildren(layer.Id, depth + 1);
      }
    }

    AddChildren(Guid.Empty, 0);
    return items;
  }

  private static System.Drawing.Color ResolveLayerDisplayColor(
    RhinoDoc doc,
    Layer? layer)
  {
    if (layer == null)
      return System.Drawing.Color.Transparent;

    try
    {
      var activeView = doc.Views.ActiveView;
      if (activeView != null)
      {
        var viewportColor = layer.PerViewportColor(activeView.ActiveViewportID);
        if (viewportColor != System.Drawing.Color.Empty)
          return viewportColor;
      }
    }
    catch
    {
    }

    return layer.Color;
  }

  private static Color ToEtoColor(System.Drawing.Color color) =>
    Color.FromArgb(color.ToArgb());

  private sealed class LayerDropDownState
  {
    public LayerDropDownState(RhinoDoc doc, string? currentLayerValue)
    {
      Doc = doc;
      CurrentLayerValue = currentLayerValue;
    }

    public RhinoDoc Doc { get; }
    public string? CurrentLayerValue { get; }
    public int Revision { get; set; }
    public bool IsUpdating { get; set; }
  }

  private sealed class LayerListItem
  {
    public LayerListItem(
      string value,
      string displayText,
      string searchText,
      Color color,
      bool isCurrentOption)
    {
      Value = value;
      DisplayText = displayText;
      SearchText = searchText;
      IsCurrentOption = isCurrentOption;
      Swatch = CreateColorSwatch(color);
    }

    public string Value { get; }
    public string DisplayText { get; }
    public string SearchText { get; }
    public bool IsCurrentOption { get; }
    public Image Swatch { get; }

    private static Bitmap CreateColorSwatch(Color color)
    {
      var bitmap = new Bitmap(18, 18, PixelFormat.Format32bppRgba);
      using var graphics = new Graphics(bitmap);
      graphics.FillRectangle(Color.FromArgb(242, 242, 242), 0, 0, 9, 9);
      graphics.FillRectangle(Color.FromArgb(191, 191, 191), 9, 0, 9, 9);
      graphics.FillRectangle(Color.FromArgb(191, 191, 191), 0, 9, 9, 9);
      graphics.FillRectangle(Color.FromArgb(242, 242, 242), 9, 9, 9, 9);
      graphics.FillRectangle(color, 0, 0, 18, 18);
      graphics.DrawRectangle(Colors.Black, 0, 0, 17, 17);
      return bitmap;
    }
  }
}
