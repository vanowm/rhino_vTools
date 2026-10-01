using System;
using System.Collections.Generic;
using System.Diagnostics;
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
  // Defaults and customizable constants
  private const bool DefaultAllowVirtualization = true; // true realizes visible dropdown rows only; false permits Eto's nonvirtualized list.
  private const int VirtualizationThreshold = 1; // Minimum item count for Eto dropdown virtualization; positive integer.
  private const int LayerIndentSpaces = 4; // Spaces added for each level of the layer hierarchy; non-negative integer.
  private const int SwatchSize = 18; // Color-swatch width and height, in pixels.
  private static readonly Color SwatchLightColor = Color.FromArgb(242, 242, 242); // Light checker color behind transparent layer colors.
  private static readonly Color SwatchDarkColor = Color.FromArgb(191, 191, 191); // Dark checker color behind transparent layer colors.
  private static readonly Color SwatchBorderColor = Colors.Black; // Outline color of layer swatches.
  private static readonly Color MissingLayerColor = Colors.White; // Swatch color for a stored layer name that is not in the document.
  private static readonly Color SpecialChoiceColor = Colors.Gray; // Swatch color of additional command-specific layer choices.
  private static readonly Size DefaultDialogSize = new(420, 440); // Initial layer-selector client width and height, in logical pixels.
  private static readonly Size MinimumDialogSize = new(300, 260); // Minimum layer-selector width and height, in logical pixels.
  private const int LayerRowHeight = 22; // Height of a row in the searchable layer dialog, in logical pixels.
  private const int SwatchColumnWidth = 26; // Width of the layer-dialog swatch column, in logical pixels.
  private const int DialogSpacing = 8; // Spacing between dialog controls and buttons, in logical pixels.
  private const int DialogPadding = 10; // Padding around dialog content, in logical pixels.
  private const int SlowPopupMilliseconds = 100; // Log popup timings on the first opening and when total layout exceeds this duration.

  private const string Tag = "LayerSelector";

  internal readonly record struct SpecialChoice(
    string Value,
    string DisplayText);

  private static readonly ConditionalWeakTable<DropDown, LayerDropDownState>
    DropDownStates = new();

  internal static bool IsCurrentLayerValue(
    string? value,
    string currentLayerValue)
  {
    var normalized = value?.Trim() ?? string.Empty;
    return normalized is "." or "*" ||
           string.Equals(
             normalized,
             currentLayerValue,
             StringComparison.OrdinalIgnoreCase);
  }

  internal static string NormalizeCurrentLayerValue(
    string? value,
    string currentLayerValue) =>
    IsCurrentLayerValue(value, currentLayerValue)
      ? currentLayerValue
      : value?.Trim() ?? string.Empty;

  internal static bool TrySelect(
    RhinoDoc doc,
    string selectedValue,
    string currentLayerValue,
    string title,
    RunMode runMode,
    bool allowNewLayer,
    out string result) =>
    TrySelect(
      doc,
      selectedValue,
      currentLayerValue,
      title,
      runMode,
      allowNewLayer,
      [],
      out result);

  internal static bool TrySelect(
    RhinoDoc doc,
    string selectedValue,
    string currentLayerValue,
    string title,
    RunMode runMode,
    bool allowNewLayer,
    IReadOnlyList<SpecialChoice> specialChoices,
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
        specialChoices,
        out result);

    try
    {
      using var dialog = new LayerSelectorDialog(
        doc,
        selectedValue,
        currentLayerValue,
        title,
        allowNewLayer,
        specialChoices);
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
    dropDown.ItemTextBinding = Binding.Property<LayerListItem, string>(item => item.DisplayText);
    dropDown.ItemImageBinding = Binding.Property<LayerListItem, Image>(item => item.Swatch);
    ConfigureDropDown(dropDown, state);
    dropDown.Load += (_, _) =>
    {
      state.Start();
      ConfigureDropDown(dropDown, state);
    };
    dropDown.UnLoad += (_, _) => state.Stop();
    PopulateDropDown(dropDown, state, selectedValue);
    dropDown.DropDownOpening += (_, _) =>
    {
      var selected = GetDropDownValue(dropDown, selectedValue);
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
    return (dropDown.SelectedValue as LayerListItem)?.Value ?? fallback;
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

  private static void ConfigureDropDown(DropDown dropDown, LayerDropDownState state)
  {
    try
    {
      var handler = dropDown.Handler;
      var handlerType = handler.GetType();
      handlerType.GetProperty("AllowVirtualization")?.SetValue(handler, DefaultAllowVirtualization);
      handlerType.GetProperty("VirtualizationThreshold")?.SetValue(handler, VirtualizationThreshold);

      var combo = FindVisualChild<System.Windows.Controls.ComboBox>(
        dropDown.ControlObject as System.Windows.DependencyObject);
      if (combo == null) return;
      state.AttachNativeControl(combo);
      System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(combo, DefaultAllowVirtualization);
      System.Windows.Controls.VirtualizingPanel.SetVirtualizationMode(
        combo, System.Windows.Controls.VirtualizationMode.Recycling);
      System.Windows.Controls.ScrollViewer.SetCanContentScroll(combo, true);

      if (combo.ItemsPanel?.VisualTree?.Type != typeof(System.Windows.Controls.VirtualizingStackPanel))
      {
        var itemPanel = new System.Windows.FrameworkElementFactory(
          typeof(System.Windows.Controls.VirtualizingStackPanel));
        itemPanel.SetValue(
          System.Windows.Controls.VirtualizingPanel.IsVirtualizingProperty, DefaultAllowVirtualization);
        itemPanel.SetValue(
          System.Windows.Controls.VirtualizingPanel.VirtualizationModeProperty,
          System.Windows.Controls.VirtualizationMode.Recycling);
        combo.ItemsPanel = new System.Windows.Controls.ItemsPanelTemplate(itemPanel);
      }
    }
    catch (Exception ex)
    {
      Log.Write(Tag, $"Unable to configure layer-dropdown virtualization: {ex.Message}");
    }
  }

  private static T? FindVisualChild<T>(System.Windows.DependencyObject? root)
    where T : System.Windows.DependencyObject
  {
    if (root == null) return null;
    if (root is T match) return match;
    for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
    {
      var child = FindVisualChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
      if (child != null) return child;
    }
    return null;
  }

  private static void PopulateDropDown(
    DropDown dropDown,
    LayerDropDownState state,
    string selectedValue)
  {
    state.IsUpdating = true;
    try
    {
      Stopwatch? timer = null;
      var retiredItems = new List<LayerListItem>();
      var viewportId = state.Doc.Views.ActiveView?.ActiveViewportID ?? Guid.Empty;
      if (state.IsDirty || state.ViewportId != viewportId)
      {
        timer = Stopwatch.StartNew();
        var previousItems = state.Layers;
        state.Layers = BuildItems(state.Doc, state.CurrentLayerValue, previousItems);
        var retainedItems = state.Layers.ToHashSet();
        retiredItems.AddRange(previousItems.Where(item => !retainedItems.Contains(item)));
        state.ViewportId = viewportId;
        state.IsDirty = false;
      }

      var items = new List<LayerListItem>(state.Layers);
      if (!items.Any(item => string.Equals(
            item.Value, selectedValue, StringComparison.OrdinalIgnoreCase)))
      {
        if (state.StatusItem == null || !string.Equals(
              state.StatusItem.Value, selectedValue, StringComparison.OrdinalIgnoreCase))
        {
          if (state.StatusItem != null) retiredItems.Add(state.StatusItem);
          state.StatusItem = new LayerListItem(selectedValue, selectedValue, selectedValue,
            MissingLayerColor, false, Guid.Empty);
        }
        items.Insert(0, state.StatusItem);
      }
      else if (state.StatusItem != null)
      {
        retiredItems.Add(state.StatusItem);
        state.StatusItem = null;
      }

      if (dropDown.DataStore is not IEnumerable<LayerListItem> existing || !existing.SequenceEqual(items))
        dropDown.DataStore = items;
      var selectedIndex = items.FindIndex(item => string.Equals(
        item.Value, selectedValue, StringComparison.OrdinalIgnoreCase));
      selectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
      if (dropDown.SelectedIndex != selectedIndex) dropDown.SelectedIndex = selectedIndex;
      foreach (var item in retiredItems) item.DisposeSwatch();
      if (timer != null)
        Log.Write(Tag, $"Layer cache prepared: items={state.Layers.Count} retired={retiredItems.Count}" +
          $" elapsed_ms={timer.Elapsed.TotalMilliseconds:0.0}");
    }
    finally
    {
      state.IsUpdating = false;
    }
  }

  private static bool TryGetManualValue(
    RhinoDoc doc,
    string selectedValue,
    string currentLayerValue,
    string title,
    bool allowNewLayer,
    IReadOnlyList<SpecialChoice> specialChoices,
    out string result)
  {
    result = selectedValue;

    while (true)
    {
      var getString = new GetString();
      getString.EnableTransparentCommands(true);
      var specialPrompt = specialChoices.Count == 0
        ? string.Empty
        : ", " + string.Join(", ", specialChoices.Select(choice => choice.Value));
      getString.SetCommandPrompt(
        $"{title} name ({currentLayerValue}, . or * = current layer{specialPrompt})");
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
            specialChoices,
            out result,
            out var error))
      {
        return true;
      }

      RhinoApp.WriteLine(error);
    }
  }

  internal static bool TryResolveManualValue(
    RhinoDoc doc,
    string? requested,
    string currentLayerValue,
    bool allowNewLayer,
    IReadOnlyList<SpecialChoice> specialChoices,
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

    if (IsCurrentLayerValue(value, currentLayerValue))
    {
      result = currentLayerValue;
      return true;
    }

    foreach (var choice in specialChoices)
    {
      if (!MatchesSpecialChoice(value, choice))
        continue;

      result = choice.Value;
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

  private static bool MatchesSpecialChoice(
    string value,
    SpecialChoice choice)
  {
    if (string.Equals(value, choice.Value, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, choice.DisplayText, StringComparison.OrdinalIgnoreCase))
      return true;

    return string.Equals(
      value.Trim('*'),
      choice.Value.Trim('*'),
      StringComparison.OrdinalIgnoreCase);
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
      string title,
      bool allowNewLayer,
      IReadOnlyList<SpecialChoice> specialChoices)
    {
      Title = title;
      Resizable = true;
      Result = false;
      ClientSize = DefaultDialogSize;
      MinimumSize = MinimumDialogSize;

      _allItems = BuildItems(doc, currentLayerValue);
      var specialInsertIndex = string.IsNullOrWhiteSpace(currentLayerValue) ? 0 : 1;
      foreach (var choice in specialChoices.Reverse())
      {
        _allItems.Insert(specialInsertIndex, new LayerListItem(
          choice.Value,
          choice.DisplayText,
          choice.Value + " " + choice.DisplayText,
          SpecialChoiceColor,
          true,
          Guid.Empty));
      }
      if (allowNewLayer &&
          !string.IsNullOrWhiteSpace(selectedValue) &&
          !_allItems.Any(item => string.Equals(
            item.Value, selectedValue, StringComparison.OrdinalIgnoreCase)))
      {
        _allItems.Insert(0, new LayerListItem(
          selectedValue,
          selectedValue,
          selectedValue,
          MissingLayerColor,
          false,
          Guid.Empty));
      }

      _layerList = new GridView
      {
        AllowEmptySelection = false,
        AllowMultipleSelection = false,
        DataStore = _allItems,
        RowHeight = LayerRowHeight,
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
        Width = SwatchColumnWidth
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
        Spacing = DialogSpacing,
        Items = { cancelButton, _selectButton }
      };

      Content = new StackLayout
      {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Padding(DialogPadding),
        Spacing = DialogSpacing,
        Items =
        {
          search,
          new StackLayoutItem(_layerList, true),
          buttons
        }
      };

      DefaultButton = _selectButton;
      AbortButton = cancelButton;
      UnLoad += (_, _) =>
      {
        foreach (var item in _allItems) item.DisposeSwatch();
      };
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
    string? currentLayerValue) => BuildItems(doc, currentLayerValue, Array.Empty<LayerListItem>());

  private static List<LayerListItem> BuildItems(
    RhinoDoc doc,
    string? currentLayerValue,
    IReadOnlyList<LayerListItem> previousItems)
  {
    var previousById = previousItems.ToDictionary(item => item.LayerId);
    var items = new List<LayerListItem>();
    if (!string.IsNullOrWhiteSpace(currentLayerValue))
    {
      items.Add(GetCachedItem(previousById, Guid.Empty,
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

      foreach (var layer in children)
      {
        var indent = depth == 0 ? string.Empty : new string(' ', depth * LayerIndentSpaces);
        items.Add(GetCachedItem(previousById, layer.Id,
          layer.FullPath,
          indent + layer.Name,
          ToEtoColor(ResolveLayerDisplayColor(doc, layer)),
          false));
        AddChildren(layer.Id, depth + 1);
      }
    }

    AddChildren(Guid.Empty, 0);
    return items;
  }

  private static LayerListItem GetCachedItem(
    IReadOnlyDictionary<Guid, LayerListItem> previousItems,
    Guid layerId,
    string value,
    string displayText,
    Color color,
    bool isCurrentOption)
  {
    if (previousItems.TryGetValue(layerId, out var item) &&
        item.Value == value && item.DisplayText == displayText &&
        item.SwatchColor.ToArgb() == color.ToArgb() && item.IsCurrentOption == isCurrentOption)
      return item;
    return new LayerListItem(value, displayText, value, color, isCurrentOption, layerId);
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
    public List<LayerListItem> Layers { get; set; } = new();
    public LayerListItem? StatusItem { get; set; }
    public Guid ViewportId { get; set; }
    public bool IsDirty { get; set; } = true;
    public bool IsUpdating { get; set; }
    private bool _listening;
    private System.Windows.Controls.ComboBox? _native;
    private Stopwatch? _openTimer;
    private bool _firstOpen = true;

    internal void Start()
    {
      if (_listening) return;
      RhinoDoc.LayerTableEvent += OnLayerTableChanged;
      _listening = true;
      IsDirty = true;
    }

    internal void Stop()
    {
      if (_listening) RhinoDoc.LayerTableEvent -= OnLayerTableChanged;
      _listening = false;
      DetachNativeControl();
      foreach (var item in Layers) item.DisposeSwatch();
      StatusItem?.DisposeSwatch();
    }

    private void OnLayerTableChanged(object? sender, Rhino.DocObjects.Tables.LayerTableEventArgs e)
    {
      if (e.Document.RuntimeSerialNumber == Doc.RuntimeSerialNumber) IsDirty = true;
    }

    internal void AttachNativeControl(System.Windows.Controls.ComboBox combo)
    {
      if (ReferenceEquals(_native, combo)) return;
      DetachNativeControl();
      _native = combo;
      _firstOpen = true;
      combo.PreviewMouseDown += OnMouseDown;
      combo.PreviewKeyDown += OnKeyDown;
      combo.DropDownOpened += OnOpened;
      combo.DropDownClosed += OnClosed;
    }

    private void DetachNativeControl()
    {
      if (_native != null)
      {
        _native.PreviewMouseDown -= OnMouseDown;
        _native.PreviewKeyDown -= OnKeyDown;
        _native.DropDownOpened -= OnOpened;
        _native.DropDownClosed -= OnClosed;
      }
      _native = null;
      _openTimer = null;
    }

    private void OnMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
      if (e.ChangedButton == System.Windows.Input.MouseButton.Left && _native?.IsDropDownOpen == false)
        _openTimer = Stopwatch.StartNew();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
      if (_native?.IsDropDownOpen == false &&
          (e.Key == System.Windows.Input.Key.F4 ||
           (e.SystemKey == System.Windows.Input.Key.Down &&
            System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))))
        _openTimer = Stopwatch.StartNew();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
      var native = _native;
      if (native == null) return;
      var timer = _openTimer ??= Stopwatch.StartNew();
      var openMs = timer.Elapsed.TotalMilliseconds;
      var first = _firstOpen;
      _firstOpen = false;
      native.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
      {
        if (!ReferenceEquals(_openTimer, timer)) return;
        timer.Stop();
        _openTimer = null;
        if (first || timer.ElapsedMilliseconds >= SlowPopupMilliseconds)
          Log.Write(Tag, $"Layer popup: first={first} items={native.Items.Count} open_ms={openMs:0.0}" +
            $" total_ms={timer.Elapsed.TotalMilliseconds:0.0}" +
            $" swatch_ms={Layers.Sum(item => item.SwatchMilliseconds) + (StatusItem?.SwatchMilliseconds ?? 0.0):0.0}");
      }));
    }

    private void OnClosed(object? sender, EventArgs e) => _openTimer = null;
  }

  private sealed class LayerListItem
  {
    private Image? _swatch;

    public LayerListItem(
      string value,
      string displayText,
      string searchText,
      Color color,
      bool isCurrentOption,
      Guid layerId)
    {
      Value = value;
      DisplayText = displayText;
      SearchText = searchText;
      IsCurrentOption = isCurrentOption;
      LayerId = layerId;
      SwatchColor = color;
    }

    public string Value { get; }
    public string DisplayText { get; }
    public string SearchText { get; }
    public bool IsCurrentOption { get; }
    public Guid LayerId { get; }
    public Color SwatchColor { get; }
    public double SwatchMilliseconds { get; private set; }
    public Image Swatch
    {
      get
      {
        if (_swatch != null) return _swatch;
        var timer = Stopwatch.StartNew();
        _swatch = CreateColorSwatch(SwatchColor);
        SwatchMilliseconds = timer.Elapsed.TotalMilliseconds;
        return _swatch;
      }
    }

    public void DisposeSwatch()
    {
      _swatch?.Dispose();
      _swatch = null;
      SwatchMilliseconds = 0.0;
    }

    private static Bitmap CreateColorSwatch(Color color)
    {
      var bitmap = new Bitmap(SwatchSize, SwatchSize, PixelFormat.Format32bppRgba);
      using var pixels = bitmap.Lock();
      var half = SwatchSize / 2;
      Color OverChecker(Color background) => new(
        color.R * color.A + background.R * (1.0f - color.A),
        color.G * color.A + background.G * (1.0f - color.A),
        color.B * color.A + background.B * (1.0f - color.A), 1.0f);
      var light = OverChecker(SwatchLightColor);
      var dark = OverChecker(SwatchDarkColor);
      for (var y = 0; y < SwatchSize; y++)
        for (var x = 0; x < SwatchSize; x++)
          pixels.SetPixel(x, y, x == 0 || y == 0 || x == SwatchSize - 1 || y == SwatchSize - 1
            ? SwatchBorderColor
            : (x < half) == (y < half) ? light : dark);
      return bitmap;
    }
  }
}
