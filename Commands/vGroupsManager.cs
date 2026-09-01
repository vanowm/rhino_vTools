using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.Geometry;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Lists and manages document groups in one sortable window.
/// </summary>
public sealed class vGroupsManager : vToolsCommand
{
  // Defaults and customizable constants
  private const string SettingsSection = "vGroupsManager"; // JSON object name used for persistent command settings.
  private const string AutoSelectKey = "autoSelect"; // JSON boolean key controlling automatic document selection from tree rows.
  private const bool DefaultAutoSelect = false; // true selects highlighted tree members automatically; false only highlights until Select is clicked.
  private const string WindowTitle = "Groups Manager"; // Modeless window title shown in the native window chrome.
  private const int DefaultWindowWidth = 680; // Initial window client width in device-independent pixels.
  private const int DefaultWindowHeight = 460; // Initial window client height in device-independent pixels.
  private const int MinimumWindowWidth = 520; // Smallest allowed window width in device-independent pixels.
  private const int MinimumWindowHeight = 300; // Smallest allowed window height in device-independent pixels.
  private const int RenameWindowWidth = 420; // Rename-dialog client width in device-independent pixels.
  private const int RenameWindowHeight = 100; // Rename-dialog client height in device-independent pixels.
  private const int GridRowHeight = 25; // Group-tree row height in device-independent pixels.
  private const int GroupNameColumnWidth = 360; // Initial group/object-name column width in device-independent pixels.
  private const int ObjectCountColumnWidth = 110; // Initial object-count column width in device-independent pixels.
  private const int DialogPadding = 10; // Uniform content inset in device-independent pixels.
  private const int ControlSpacing = 8; // Gap between adjacent controls in device-independent pixels.
  private const int SelectCheckboxRightMargin = 7; // Gap after the unlabeled Auto Select checkbox in device-independent pixels.
  private const int ObjectIdDisplayLength = 8; // Number of hexadecimal object-ID characters shown for unnamed members; 1 through 32.
  private const double InactiveSelectionSaturationFactor = 0.72; // Unfocused tree-selection saturation from 0.0 grayscale through 1.0 matching the active selection color.

  private static GroupsManagerWindow? ActiveWindow;

  public override string EnglishName => "vGroupsManager";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    if (ActiveWindow is { IsDisposed: false } existing)
    {
      if (existing.DocumentSerialNumber == doc.RuntimeSerialNumber)
      {
        existing.RefreshFromDocument();
        existing.BringToFront();
        existing.Focus();
        return Result.Success;
      }

      existing.Close();
    }

    var autoSelect = ToolsOptionStore.Read(
      SettingsSection,
      section => ToolsOptionStore.TryGetBool(section, AutoSelectKey, out var saved)
        ? saved
        : DefaultAutoSelect);
    var window = new GroupsManagerWindow(doc, autoSelect)
    {
      Owner = RhinoEtoApp.MainWindow
    };
    ActiveWindow = window;
    window.Closed += (_, _) =>
    {
      if (ReferenceEquals(ActiveWindow, window))
        ActiveWindow = null;
    };
    window.Show();
    return Result.Success;
  }

  private static void SaveAutoSelect(bool autoSelect)
  {
    if (!ToolsOptionStore.Update(
          SettingsSection,
          section => section[AutoSelectKey] = autoSelect))
    {
      RhinoApp.WriteLine(
        $"vGroupsManager: failed to save AutoSelect: {ToolsOptionStore.LastError}");
    }
  }

  private enum SortField
  {
    Name,
    ObjectCount
  }

  private readonly record struct NodeKey(int GroupIndex, Guid ObjectId)
  {
    internal static NodeKey ForGroup(int groupIndex) =>
      new(groupIndex, Guid.Empty);
  }

  private sealed class GroupListItem
  {
    internal GroupListItem(int index, string name, int objectCount)
    {
      Index = index;
      Name = name;
      ObjectCount = objectCount;
    }

    internal int Index { get; }
    internal string Name { get; }
    internal int ObjectCount { get; }
    internal string ObjectCountText => ObjectCount.ToString();
  }

  private sealed record GroupNodeTag(GroupListItem Group);
  private sealed record ObjectNodeTag(int GroupIndex, Guid ObjectId);

  private sealed class GroupsManagerWindow : Form
  {
    private readonly RhinoDoc _doc;
    private readonly TreeGridView _tree;
    private readonly GridColumn _nameColumn;
    private readonly GridColumn _objectCountColumn;
    private readonly Button _selectObjectsButton;
    private readonly Button _addButton;
    private readonly Button _removeButton;
    private readonly Button _renameButton;
    private readonly Button _ungroupButton;
    private readonly Button _ungroupSinglesButton;
    private readonly Button _purgeEmptyButton;
    private readonly Label _status;
    private readonly PreviewDisplay.ObjectHighlighter _objectHighlighter;
    private readonly HashSet<int> _expandedGroupIndices = [];
    private readonly HashSet<Guid> _autoSelectedObjectIds = [];
    private System.Windows.Controls.CheckBox? _autoSelectCheck;
    private TreeGridItem _treeRoot = new();
    private List<GroupListItem> _rows = [];
    private SortField _sortField = SortField.Name;
    private bool _sortAscending = true;
    private bool _refreshing;
    private bool _refreshQueued;
    private bool _selectionRefreshQueued;
    private bool _changingDocument;
    private bool _changingSelection;
    private bool _autoSelect;

    internal GroupsManagerWindow(RhinoDoc doc, bool autoSelect)
    {
      _doc = doc;
      _autoSelect = autoSelect;
      _objectHighlighter = new PreviewDisplay.ObjectHighlighter(doc);
      Title = WindowTitle;
      Resizable = true;
      ClientSize = new Size(DefaultWindowWidth, DefaultWindowHeight);
      MinimumSize = new Size(MinimumWindowWidth, MinimumWindowHeight);

      _tree = new TreeGridView
      {
        AllowColumnReordering = false,
        AllowEmptySelection = true,
        AllowMultipleSelection = true,
        GridLines = GridLines.Both,
        RowHeight = GridRowHeight,
        ShowHeader = true
      };

      _nameColumn = new GridColumn
      {
        ID = "name",
        HeaderText = "Group / Object",
        HeaderToolTip = "Sort groups by name",
        DataCell = new TextBoxCell(0),
        Editable = false,
        Expand = true,
        Resizable = true,
        Sortable = true,
        Width = GroupNameColumnWidth
      };
      _objectCountColumn = new GridColumn
      {
        ID = "objects",
        HeaderText = "Objects",
        HeaderToolTip = "Sort groups by object count",
        HeaderTextAlignment = TextAlignment.Right,
        DataCell = new TextBoxCell(1) { TextAlignment = TextAlignment.Right },
        Editable = false,
        Resizable = true,
        Sortable = true,
        Width = ObjectCountColumnWidth
      };
      _tree.Columns.Add(_nameColumn);
      _tree.Columns.Add(_objectCountColumn);

      _selectObjectsButton = new Button
      {
        Text = "Select",
        ToolTip = "Select the objects represented by the highlighted tree rows"
      };
      _addButton = new Button
      {
        Text = "Add",
        ToolTip = "Add the currently selected Rhino objects to the highlighted groups"
      };
      _removeButton = new Button
      {
        Text = "Remove",
        ToolTip = "Remove highlighted members, or selected Rhino objects, from the highlighted groups"
      };
      _renameButton = new Button
      {
        Text = "Rename",
        ToolTip = "Rename the highlighted group"
      };
      _ungroupButton = new Button
      {
        Text = "Ungroup",
        ToolTip = "Dissolve every highlighted group"
      };
      _ungroupSinglesButton = new Button
      {
        Text = "Ungroup Singles",
        ToolTip = "Dissolve every group containing exactly one object"
      };
      _purgeEmptyButton = new Button
      {
        Text = "Purge Empty",
        ToolTip = "Delete every group containing no objects"
      };
      var closeButton = new Button { Text = "Close" };
      _status = new Label { Wrap = WrapMode.Word };

      _tree.SelectedItemsChanged += (_, _) => TreeSelectionChanged();
      _tree.Load += (_, _) => InstallTreeSelectionStyle();
      _tree.ColumnHeaderClick += (_, args) => SortBy(args.Column);
      _tree.CellDoubleClick += (_, args) =>
      {
        if (args.Item is TreeGridItem { Tag: GroupNodeTag })
          BeginRename();
      };
      _tree.Expanded += (_, args) => SetExpanded(args.Item, true);
      _tree.Collapsed += (_, args) => SetExpanded(args.Item, false);
      _selectObjectsButton.Click += (_, _) => SelectObjects(_autoSelect);
      _selectObjectsButton.Load += (_, _) => InstallSelectButtonContent();
      _addButton.Click += (_, _) => AddSelectedObjectsToGroups();
      _removeButton.Click += (_, _) => RemoveObjectsFromGroups();
      _renameButton.Click += (_, _) => BeginRename();
      _ungroupButton.Click += (_, _) => UngroupRows(SelectedGroups(), "selected");
      _ungroupSinglesButton.Click += (_, _) =>
        UngroupRows(_rows.Where(row => row.ObjectCount == 1).ToList(), "single-object");
      _purgeEmptyButton.Click += (_, _) => PurgeEmptyGroups();
      closeButton.Click += (_, _) =>
      {
        Close();
      };
      Closed += (_, _) => DisposeWindowState();

      var selectionActions = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = ControlSpacing,
        Items =
        {
          _selectObjectsButton,
          _addButton,
          _removeButton,
          _renameButton,
          _ungroupButton
        }
      };
      var documentActions = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = ControlSpacing,
        Items = { _ungroupSinglesButton, _purgeEmptyButton }
      };
      var bottomRow = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Spacing = ControlSpacing,
        Items =
        {
          new StackLayoutItem(documentActions, true),
          closeButton
        }
      };

      Content = new StackLayout
      {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Padding(DialogPadding),
        Spacing = ControlSpacing,
        Items =
        {
          new StackLayoutItem(_tree, true),
          _status,
          selectionActions,
          bottomRow
        }
      };

      InstallTreeSelectionStyle();
      InstallSelectButtonContent();
      SubscribeDocumentEvents();
      RefreshRows();
      SyncTreeSelectionFromDocument();
    }

    internal uint DocumentSerialNumber => _doc.RuntimeSerialNumber;

    internal void RefreshFromDocument() => RefreshRows();

    private void SubscribeDocumentEvents()
    {
      RhinoDoc.CloseDocument += DocumentClosed;
      RhinoDoc.GroupTableEvent += GroupTableChanged;
      RhinoDoc.ModifyObjectAttributes += ObjectAttributesChanged;
      RhinoDoc.AddRhinoObject += ObjectTableChanged;
      RhinoDoc.DeleteRhinoObject += ObjectTableChanged;
      RhinoDoc.UndeleteRhinoObject += ObjectTableChanged;
      RhinoDoc.SelectObjects += DocumentSelectionChanged;
      RhinoDoc.DeselectObjects += DocumentSelectionChanged;
    }

    private void DisposeWindowState()
    {
      RhinoDoc.CloseDocument -= DocumentClosed;
      RhinoDoc.GroupTableEvent -= GroupTableChanged;
      RhinoDoc.ModifyObjectAttributes -= ObjectAttributesChanged;
      RhinoDoc.AddRhinoObject -= ObjectTableChanged;
      RhinoDoc.DeleteRhinoObject -= ObjectTableChanged;
      RhinoDoc.UndeleteRhinoObject -= ObjectTableChanged;
      RhinoDoc.SelectObjects -= DocumentSelectionChanged;
      RhinoDoc.DeselectObjects -= DocumentSelectionChanged;
      _objectHighlighter.Dispose();
    }

    private void DocumentClosed(object? sender, DocumentEventArgs args)
    {
      if (args.DocumentSerialNumber == _doc.RuntimeSerialNumber)
        Close();
    }

    private void GroupTableChanged(object? sender, GroupTableEventArgs args) =>
      QueueDocumentRefresh(args.Document);

    private void ObjectAttributesChanged(
      object? sender,
      RhinoModifyObjectAttributesEventArgs args) =>
      QueueDocumentRefresh(args.Document);

    private void ObjectTableChanged(object? sender, RhinoObjectEventArgs args) =>
      QueueDocumentRefresh(args.TheObject.Document ?? _doc);

    private void DocumentSelectionChanged(
      object? sender,
      RhinoObjectSelectionEventArgs args)
    {
      if (_changingSelection || IsDisposed ||
          args.Document.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;

      QueueSelectionRefresh();
    }

    private void QueueDocumentRefresh(RhinoDoc? eventDocument)
    {
      if (_changingDocument || _refreshQueued || IsDisposed ||
          eventDocument?.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
      {
        return;
      }

      _refreshQueued = true;
      Application.Instance.AsyncInvoke(() =>
      {
        _refreshQueued = false;
        if (!IsDisposed)
          RefreshRows();
      });
    }

    private void QueueSelectionRefresh()
    {
      if (_selectionRefreshQueued || IsDisposed)
        return;

      _selectionRefreshQueued = true;
      Application.Instance.AsyncInvoke(() =>
      {
        _selectionRefreshQueued = false;
        if (!IsDisposed)
          SyncTreeSelectionFromDocument();
      });
    }

    private List<TreeGridItem> SelectedTreeItems() =>
      _tree.SelectedItems.OfType<TreeGridItem>().ToList();

    private List<GroupListItem> SelectedGroups() =>
      SelectedTreeItems()
        .Select(item => item.Tag)
        .OfType<GroupNodeTag>()
        .Select(tag => tag.Group)
        .DistinctBy(group => group.Index)
        .ToList();

    private HashSet<int> SelectedGroupIndices()
    {
      var indices = new HashSet<int>();
      foreach (var item in SelectedTreeItems())
      {
        switch (item.Tag)
        {
          case GroupNodeTag group:
            indices.Add(group.Group.Index);
            break;
          case ObjectNodeTag obj:
            indices.Add(obj.GroupIndex);
            break;
        }
      }

      return indices;
    }

    private HashSet<NodeKey> SelectedNodeKeys() =>
      SelectedTreeItems()
        .Select(NodeKeyForItem)
        .Where(key => key.HasValue)
        .Select(key => key!.Value)
        .ToHashSet();

    private static NodeKey? NodeKeyForItem(TreeGridItem item) =>
      item.Tag switch
      {
        GroupNodeTag group => NodeKey.ForGroup(group.Group.Index),
        ObjectNodeTag obj => new NodeKey(obj.GroupIndex, obj.ObjectId),
        _ => null
      };

    private void RefreshRows(
      IEnumerable<NodeKey>? selectedNodeKeys = null,
      IEnumerable<NodeKey>? scrollNodeKeys = null,
      bool applyAutoSelection = true)
    {
      var selected = selectedNodeKeys?.ToHashSet() ?? SelectedNodeKeys();
      var scrollTargets = scrollNodeKeys?.ToHashSet() ?? [];

      _refreshing = true;
      try
      {
        _rows = BuildRows();
        ApplySort();
        BuildTree();
        _tree.DataStore = _treeRoot;
        _tree.SelectedRows = FlattenVisibleItems()
          .Select((item, rowIndex) => (item, rowIndex))
          .Where(entry =>
          {
            var key = NodeKeyForItem(entry.item);
            return key.HasValue && selected.Contains(key.Value);
          })
          .Select(entry => entry.rowIndex)
          .ToList();
      }
      finally
      {
        _refreshing = false;
      }

      UpdateButtons();
      UpdateHighlights();
      if (_autoSelect && applyAutoSelection)
        SelectObjects(automatic: true);
      else if (selected.Count == 0)
        _status.Text = $"{_rows.Count} group{(_rows.Count == 1 ? string.Empty : "s")}";
      if (scrollTargets.Count > 0)
        QueueScrollToFirstVisible(scrollTargets);
    }

    private void SyncTreeSelectionFromDocument()
    {
      var selectedObjectIds = CurrentDocumentSelection();
      var selectedNodes = new HashSet<NodeKey>();
      var membersByGroup = _rows
        .Select(row => new
        {
          Row = row,
          ObjectIds = (_doc.Groups.GroupMembers(row.Index) ?? [])
            .Where(member => member != null)
            .Select(member => member.Id)
            .ToHashSet()
        })
        .ToList();
      var completeGroups = membersByGroup
        .Where(group => group.ObjectIds.Count > 0 &&
                        group.ObjectIds.IsSubsetOf(selectedObjectIds))
        .ToList();
      var selectedGroups = completeGroups
        .Where(candidate => !completeGroups.Any(other =>
          other.Row.Index != candidate.Row.Index &&
          candidate.ObjectIds.IsProperSubsetOf(other.ObjectIds)))
        .ToList();
      var representedObjectIds = new HashSet<Guid>();
      foreach (var group in selectedGroups)
      {
        selectedNodes.Add(NodeKey.ForGroup(group.Row.Index));
        representedObjectIds.UnionWith(group.ObjectIds);
      }

      foreach (var group in membersByGroup)
      {
        foreach (var objectId in group.ObjectIds)
        {
          if (!selectedObjectIds.Contains(objectId) ||
              representedObjectIds.Contains(objectId))
            continue;

          selectedNodes.Add(new NodeKey(group.Row.Index, objectId));
          _expandedGroupIndices.Add(group.Row.Index);
        }
      }

      RefreshRows(
        selectedNodes,
        selectedNodes,
        applyAutoSelection: false);
    }

    private void QueueScrollToFirstVisible(IReadOnlySet<NodeKey> targets)
    {
      Application.Instance.AsyncInvoke(() =>
      {
        if (IsDisposed)
          return;

        var visible = FlattenVisibleItems();
        var rowIndex = visible.FindIndex(item =>
        {
          var key = NodeKeyForItem(item);
          return key.HasValue && targets.Contains(key.Value);
        });
        if (rowIndex >= 0)
          _tree.ScrollToRow(rowIndex);
      });
    }

    private NodeKey? NeighborOfRemovedNodes(IReadOnlySet<NodeKey> removedNodes)
    {
      var visible = FlattenVisibleItems();
      var firstRemovedRow = visible.FindIndex(item =>
      {
        var key = NodeKeyForItem(item);
        return key.HasValue && removedNodes.Contains(key.Value);
      });
      if (firstRemovedRow < 0)
        return null;

      for (var rowIndex = firstRemovedRow + 1;
           rowIndex < visible.Count;
           rowIndex++)
      {
        var key = NodeKeyForItem(visible[rowIndex]);
        if (key.HasValue && !removedNodes.Contains(key.Value))
          return key;
      }

      for (var rowIndex = firstRemovedRow - 1; rowIndex >= 0; rowIndex--)
      {
        var key = NodeKeyForItem(visible[rowIndex]);
        if (key.HasValue && !removedNodes.Contains(key.Value))
          return key;
      }

      return null;
    }

    private List<GroupListItem> BuildRows()
    {
      var rows = new List<GroupListItem>();
      for (var groupIndex = 0; groupIndex < _doc.Groups.Count; groupIndex++)
      {
        var group = _doc.Groups.FindIndex(groupIndex);
        if (group == null || group.IsDeleted)
          continue;

        var members = _doc.Groups.GroupMembers(groupIndex) ?? [];
        rows.Add(new GroupListItem(
          groupIndex,
          group.Name ?? _doc.Groups.GroupName(groupIndex) ?? string.Empty,
          members.Length));
      }

      return rows;
    }

    private void BuildTree()
    {
      var groupNodes = new List<ITreeGridItem>();
      foreach (var row in _rows)
      {
        var childNodes = (_doc.Groups.GroupMembers(row.Index) ?? [])
          .Where(member => member != null)
          .Select(member => new
          {
            Member = member,
            DisplayName = ObjectDisplayName(member)
          })
          .OrderBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
          .ThenBy(entry => entry.Member.Id)
          .Select(entry =>
          {
            var child = new TreeGridItem(new object[] { entry.DisplayName, string.Empty })
            {
              Tag = new ObjectNodeTag(row.Index, entry.Member.Id)
            };
            return (ITreeGridItem)child;
          })
          .ToList();

        var groupNode = new TreeGridItem(
          childNodes,
          new object[] { row.Name, row.ObjectCountText })
        {
          Expanded = _expandedGroupIndices.Contains(row.Index),
          Tag = new GroupNodeTag(row)
        };
        groupNodes.Add(groupNode);
      }

      _treeRoot = new TreeGridItem(groupNodes, Array.Empty<object>());
    }

    private static string ObjectDisplayName(RhinoObject member)
    {
      var objectType = ObjectTypeName(member);
      var objectName = member.Attributes.Name?.Trim();
      var displayName = string.IsNullOrWhiteSpace(objectName)
        ? objectType
        : $"{objectType} \"{objectName}\"";
      var textValue = ObjectTextValue(member);
      if (!string.IsNullOrWhiteSpace(textValue))
        return $"{displayName} ({textValue})";

      if (!string.IsNullOrWhiteSpace(objectName))
        return displayName;

      var compactId = member.Id.ToString("N");
      compactId = compactId[..Math.Min(ObjectIdDisplayLength, compactId.Length)];
      return $"{objectType} {compactId}";
    }

    private static string? ObjectTextValue(RhinoObject member)
    {
      var value = member.Geometry switch
      {
        TextDot dot => dot.Text,
        AnnotationBase annotation => annotation.PlainText,
        _ => null
      };
      if (string.IsNullOrWhiteSpace(value))
        return null;

      return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string ObjectTypeName(RhinoObject member)
    {
      if (member.Geometry is Curve curve)
        return CurveTypeName(curve);

      var explicitName = member.Geometry switch
      {
        Brep brep => brep.Faces.Count <= 1 ? "surface" : "polysurface",
        Extrusion => "extrusion",
        Surface => "surface",
        Mesh => "mesh",
        SubD => "subd",
        TextEntity => "text",
        TextDot => "text dot",
        AngularDimension => "angle dim",
        RadialDimension => "radial dim",
        Centermark => "centermark",
        Dimension => "dimension",
        Leader => "leader",
        Rhino.Geometry.Point => "point",
        PointCloud => "point cloud",
        Hatch => "hatch",
        InstanceReferenceGeometry => "block instance",
        _ => null
      };
      if (explicitName != null)
        return explicitName;

      var description = member.ShortDescription(false)?.Trim();
      return !string.IsNullOrWhiteSpace(description)
        ? description.ToLowerInvariant()
        : member.ObjectType.ToString().Replace("_", " ").ToLowerInvariant();
    }

    private static string CurveTypeName(Curve curve)
    {
      if (curve.TryGetCircle(out _))
        return "circle";
      if (curve.TryGetArc(out _))
        return "arc";
      if (curve.IsClosed && curve.TryGetEllipse(out _))
        return "ellipse";
      if (curve.TryGetPolyline(out var polyline) && polyline.IsValid)
        return polyline.SegmentCount == 1 ? "line" : "polyline";
      if (curve is LineCurve)
        return "line";
      return curve.IsClosed ? "closed curve" : "curve";
    }

    private List<TreeGridItem> FlattenVisibleItems()
    {
      var items = new List<TreeGridItem>();
      foreach (var item in _treeRoot.Children.OfType<TreeGridItem>())
      {
        items.Add(item);
        if (item.Expanded)
          items.AddRange(item.Children.OfType<TreeGridItem>());
      }

      return items;
    }

    private void ApplySort()
    {
      IEnumerable<GroupListItem> ordered = _sortField == SortField.Name
        ? _rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
        : _rows.OrderBy(row => row.ObjectCount)
          .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase);

      if (!_sortAscending)
      {
        ordered = _sortField == SortField.Name
          ? _rows.OrderByDescending(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
          : _rows.OrderByDescending(row => row.ObjectCount)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase);
      }

      _rows = ordered.ToList();
    }

    private void SortBy(GridColumn column)
    {
      var nextField = column == _objectCountColumn
        ? SortField.ObjectCount
        : SortField.Name;
      _sortAscending = nextField == _sortField ? !_sortAscending : true;
      _sortField = nextField;
      RefreshRows();
    }

    private void SetExpanded(ITreeGridItem item, bool expanded)
    {
      if (_refreshing || item is not TreeGridItem { Tag: GroupNodeTag group })
        return;

      if (expanded)
        _expandedGroupIndices.Add(group.Group.Index);
      else
        _expandedGroupIndices.Remove(group.Group.Index);
    }

    private void TreeSelectionChanged()
    {
      if (_refreshing)
        return;

      UpdateButtons();
      UpdateHighlights();
      if (_autoSelect)
        SelectObjects(automatic: true);
    }

    private void UpdateButtons()
    {
      var selectedItems = SelectedTreeItems();
      var selectedGroups = selectedItems
        .Select(item => item.Tag)
        .OfType<GroupNodeTag>()
        .ToList();
      _selectObjectsButton.Enabled = true;
      _addButton.Enabled = SelectedGroupIndices().Count > 0;
      _removeButton.Enabled = selectedItems.Count > 0;
      _renameButton.Enabled = selectedItems.Count == 1 && selectedGroups.Count == 1;
      _ungroupButton.Enabled = selectedGroups.Count > 0;
      _ungroupSinglesButton.Enabled = _rows.Any(row => row.ObjectCount == 1);
      _purgeEmptyButton.Enabled = _rows.Any(row => row.ObjectCount == 0);
    }

    private HashSet<Guid> SelectedObjectIds()
    {
      var objectIds = new HashSet<Guid>();
      foreach (var item in SelectedTreeItems())
      {
        switch (item.Tag)
        {
          case GroupNodeTag group:
            foreach (var member in _doc.Groups.GroupMembers(group.Group.Index) ?? [])
            {
              if (member != null)
                objectIds.Add(member.Id);
            }
            break;

          case ObjectNodeTag obj:
            objectIds.Add(obj.ObjectId);
            break;
        }
      }

      return objectIds;
    }

    private void UpdateHighlights()
    {
      var nextIds = SelectedObjectIds();
      _objectHighlighter.SetObjects(nextIds);
      _status.Text =
        $"Highlighted {nextIds.Count} object{(nextIds.Count == 1 ? string.Empty : "s")}.";
    }

    private void SelectObjects(bool automatic = false)
    {
      var objectIds = SelectedObjectIds();
      if (objectIds.Count == 0)
      {
        if (automatic)
        {
          var clearedCount = ClearAutoSelection();
          _status.Text = clearedCount > 0
            ? $"Cleared {clearedCount} automatically selected object{(clearedCount == 1 ? string.Empty : "s")}."
            : "No highlighted objects to select.";
        }
        else
        {
          _status.Text = "Highlight a group or object row before selecting objects.";
        }

        return;
      }

      var selectedCount = 0;
      _changingSelection = true;
      try
      {
        _doc.Objects.UnselectAll();
        _autoSelectedObjectIds.Clear();
        foreach (var objectId in objectIds)
        {
          if (!_doc.Objects.Select(objectId))
            continue;

          selectedCount++;
          if (automatic)
            _autoSelectedObjectIds.Add(objectId);
        }
      }
      finally
      {
        _changingSelection = false;
      }

      _doc.Views.Redraw();
      _status.Text =
        $"Selected {selectedCount} object{(selectedCount == 1 ? string.Empty : "s")}.";
    }

    private int ClearAutoSelection()
    {
      var clearedCount = 0;
      _changingSelection = true;
      try
      {
        foreach (var objectId in _autoSelectedObjectIds)
        {
          if ((_doc.Objects.FindId(objectId)?.Select(false) ?? 0) > 0)
            clearedCount++;
        }
      }
      finally
      {
        _changingSelection = false;
      }

      _autoSelectedObjectIds.Clear();
      if (clearedCount > 0)
        _doc.Views.Redraw();
      return clearedCount;
    }

    private void InstallTreeSelectionStyle()
    {
      if (_tree.ControlObject is not System.Windows.FrameworkElement nativeTree)
        return;

      nativeTree.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey] =
        CreateInactiveSelectionBrush(nativeTree);
      nativeTree.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey] =
        System.Windows.SystemColors.HighlightTextBrush;
    }

    private static System.Windows.Media.SolidColorBrush CreateInactiveSelectionBrush(
      System.Windows.FrameworkElement nativeTree)
    {
      var activeBrush =
        nativeTree.TryFindResource(System.Windows.SystemColors.HighlightBrushKey) as
          System.Windows.Media.SolidColorBrush ??
        System.Windows.SystemColors.HighlightBrush;
      var active = activeBrush.Color;
      var gray = (byte)Math.Round(
        active.R * 0.2126 + active.G * 0.7152 + active.B * 0.0722);
      static byte Desaturate(byte channel, byte gray) =>
        (byte)Math.Round(
          gray + (channel - gray) * InactiveSelectionSaturationFactor);
      var brush = new System.Windows.Media.SolidColorBrush(
        System.Windows.Media.Color.FromArgb(
          active.A,
          Desaturate(active.R, gray),
          Desaturate(active.G, gray),
          Desaturate(active.B, gray)))
      {
        Opacity = activeBrush.Opacity
      };
      brush.Freeze();
      return brush;
    }

    private void InstallSelectButtonContent()
    {
      if (_selectObjectsButton.ControlObject is not System.Windows.Controls.Button nativeButton)
        return;
      if (_autoSelectCheck != null)
      {
        _autoSelectCheck.IsChecked = _autoSelect;
        return;
      }

      var content = new System.Windows.Controls.StackPanel
      {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center
      };
      _autoSelectCheck = new System.Windows.Controls.CheckBox
      {
        IsChecked = _autoSelect,
        Margin = new System.Windows.Thickness(0, 0, SelectCheckboxRightMargin, 0),
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        ToolTip = "Automatically select highlighted tree objects; uncheck to clear that automatic selection"
      };
      _autoSelectCheck.Click += (_, args) =>
      {
        _autoSelect = _autoSelectCheck.IsChecked == true;
        SaveAutoSelect(_autoSelect);
        if (_autoSelect)
          SelectObjects(automatic: true);
        else
        {
          var clearedCount = ClearAutoSelection();
          _status.Text = clearedCount > 0
            ? $"Cleared {clearedCount} automatically selected object{(clearedCount == 1 ? string.Empty : "s")}."
            : "Auto Select disabled.";
        }
        args.Handled = true;
      };
      content.Children.Add(_autoSelectCheck);
      content.Children.Add(new System.Windows.Controls.TextBlock
      {
        Text = "Select",
        VerticalAlignment = System.Windows.VerticalAlignment.Center
      });
      nativeButton.Content = content;
      nativeButton.ToolTip =
        "Select highlighted tree objects; the checkbox enables automatic selection and clears it when disabled";
    }

    private HashSet<Guid> CurrentDocumentSelection() =>
      _doc.Objects.GetSelectedObjects(false, false)
        .Where(obj => obj != null && !obj.IsDeleted)
        .Select(obj => obj.Id)
        .ToHashSet();

    private void AddSelectedObjectsToGroups()
    {
      var groupIndices = SelectedGroupIndices();
      var objectIds = CurrentDocumentSelection();
      if (groupIndices.Count == 0 || objectIds.Count == 0)
      {
        _status.Text = groupIndices.Count == 0
          ? "Highlight a destination group before adding objects."
          : "Select one or more Rhino objects to add.";
        return;
      }

      var selected = SelectedNodeKeys();
      var addedNodes = new HashSet<NodeKey>();
      var undoRecord = _doc.BeginUndoRecord("vGroupsManager Add");
      var membershipCount = 0;
      _changingDocument = true;
      try
      {
        foreach (var groupIndex in groupIndices)
        {
          foreach (var objectId in objectIds)
          {
            var obj = _doc.Objects.FindId(objectId);
            if (obj == null || obj.Attributes.IsInGroup(groupIndex))
              continue;
            if (_doc.Groups.AddToGroup(groupIndex, objectId))
            {
              membershipCount++;
              addedNodes.Add(new NodeKey(groupIndex, objectId));
              _expandedGroupIndices.Add(groupIndex);
            }
          }
        }
      }
      finally
      {
        _changingDocument = false;
        _doc.EndUndoRecord(undoRecord);
      }

      RefreshRows(selected, addedNodes);
      _status.Text = membershipCount == 0
        ? "The selected objects already belong to the highlighted groups."
        : $"Added {membershipCount} group membership{(membershipCount == 1 ? string.Empty : "s")}.";
      Log.Write(
        "vGroupsManager",
        $"add groups={groupIndices.Count} objects={objectIds.Count} memberships={membershipCount}");
    }

    private void RemoveObjectsFromGroups()
    {
      var memberships = new HashSet<(int GroupIndex, Guid ObjectId)>();
      var selectedDocumentIds = CurrentDocumentSelection();
      foreach (var item in SelectedTreeItems())
      {
        switch (item.Tag)
        {
          case ObjectNodeTag obj:
            memberships.Add((obj.GroupIndex, obj.ObjectId));
            break;
          case GroupNodeTag group:
            foreach (var objectId in selectedDocumentIds)
            {
              var obj = _doc.Objects.FindId(objectId);
              if (obj?.Attributes.IsInGroup(group.Group.Index) == true)
                memberships.Add((group.Group.Index, objectId));
            }
            break;
        }
      }

      if (memberships.Count == 0)
      {
        _status.Text = "Highlight members, or highlight groups and select their Rhino objects, to remove them.";
        return;
      }

      var selected = SelectedNodeKeys();
      var removedNodes = new HashSet<NodeKey>();
      var undoRecord = _doc.BeginUndoRecord("vGroupsManager Remove");
      var membershipCount = 0;
      _changingDocument = true;
      try
      {
        foreach (var membership in memberships)
        {
          var obj = _doc.Objects.FindId(membership.ObjectId);
          if (obj == null || !obj.Attributes.IsInGroup(membership.GroupIndex))
            continue;

          var attributes = obj.Attributes.Duplicate();
          attributes.RemoveFromGroup(membership.GroupIndex);
          if (_doc.Objects.ModifyAttributes(obj.Id, attributes, true))
          {
            membershipCount++;
            removedNodes.Add(new NodeKey(
              membership.GroupIndex,
              membership.ObjectId));
          }
        }
      }
      finally
      {
        _changingDocument = false;
        _doc.EndUndoRecord(undoRecord);
      }

      var nextSelection = selected
        .Where(node => !removedNodes.Contains(node))
        .ToHashSet();
      if (nextSelection.Count == 0)
      {
        var neighbor = NeighborOfRemovedNodes(removedNodes);
        if (neighbor.HasValue)
          nextSelection.Add(neighbor.Value);
      }

      RefreshRows(nextSelection);
      _status.Text =
        $"Removed {membershipCount} group membership{(membershipCount == 1 ? string.Empty : "s")}.";
      Log.Write("vGroupsManager", $"remove memberships={membershipCount}");
    }

    private void BeginRename()
    {
      var selectedItems = SelectedTreeItems();
      if (selectedItems.Count != 1 ||
          selectedItems[0].Tag is not GroupNodeTag groupTag)
        return;

      var dialog = new RenameGroupDialog(groupTag.Group.Name);
      if (!dialog.ShowModal(this))
        return;

      ApplyRename(groupTag.Group, dialog.GroupName);
    }

    private void ApplyRename(GroupListItem row, string requestedName)
    {
      var currentName = _doc.Groups.GroupName(row.Index) ?? string.Empty;
      requestedName = requestedName.Trim();
      if (string.Equals(currentName, requestedName, StringComparison.Ordinal))
        return;

      var existing = requestedName.Length == 0
        ? null
        : _doc.Groups.FindName(requestedName);
      if (requestedName.Length == 0 ||
          (existing != null && existing.Index != row.Index))
      {
        _status.Text = requestedName.Length == 0
          ? "Group names cannot be empty."
          : $"A group named '{requestedName}' already exists.";
        return;
      }

      var selected = SelectedNodeKeys();
      var undoRecord = _doc.BeginUndoRecord("vGroupsManager Rename");
      var changed = false;
      _changingDocument = true;
      try
      {
        changed = _doc.Groups.ChangeGroupName(row.Index, requestedName);
      }
      finally
      {
        _changingDocument = false;
        _doc.EndUndoRecord(undoRecord);
      }

      RefreshRows(selected);
      if (changed)
      {
        _status.Text = $"Renamed '{currentName}' to '{requestedName}'.";
        Log.Write("vGroupsManager", $"renamed index={row.Index} from={currentName} to={requestedName}");
      }
      else
      {
        _status.Text = $"Could not rename '{currentName}'.";
      }
    }

    private void UngroupRows(IReadOnlyCollection<GroupListItem> rows, string scope)
    {
      if (rows.Count == 0)
        return;

      var undoRecord = _doc.BeginUndoRecord("vGroupsManager Ungroup");
      var membershipCount = 0;
      var groupCount = 0;
      _changingDocument = true;
      try
      {
        foreach (var row in rows)
        {
          foreach (var member in _doc.Groups.GroupMembers(row.Index) ?? [])
          {
            if (member == null || !member.Attributes.IsInGroup(row.Index))
              continue;

            var attributes = member.Attributes.Duplicate();
            attributes.RemoveFromGroup(row.Index);
            if (_doc.Objects.ModifyAttributes(member.Id, attributes, true))
              membershipCount++;
          }

          if ((_doc.Groups.GroupMembers(row.Index) ?? []).Length == 0 &&
              _doc.Groups.Delete(row.Index))
          {
            _expandedGroupIndices.Remove(row.Index);
            groupCount++;
          }
        }
      }
      finally
      {
        _changingDocument = false;
        _doc.EndUndoRecord(undoRecord);
      }

      RefreshRows();
      _doc.Views.Redraw();
      _status.Text = $"Ungrouped {groupCount} group{(groupCount == 1 ? string.Empty : "s")} " +
        $"and removed {membershipCount} membership{(membershipCount == 1 ? string.Empty : "s")}.";
      Log.Write(
        "vGroupsManager",
        $"ungroup scope={scope} groups={groupCount} memberships={membershipCount}");
    }

    private void PurgeEmptyGroups()
    {
      var emptyRows = _rows.Where(row => row.ObjectCount == 0).ToList();
      if (emptyRows.Count == 0)
        return;

      var undoRecord = _doc.BeginUndoRecord("vGroupsManager Purge Empty");
      var deletedCount = 0;
      _changingDocument = true;
      try
      {
        deletedCount = emptyRows.Count(row => _doc.Groups.Delete(row.Index));
      }
      finally
      {
        _changingDocument = false;
        _doc.EndUndoRecord(undoRecord);
      }

      foreach (var row in emptyRows)
        _expandedGroupIndices.Remove(row.Index);
      RefreshRows();
      _status.Text = $"Purged {deletedCount} empty group{(deletedCount == 1 ? string.Empty : "s")}.";
      Log.Write("vGroupsManager", $"purged empty_groups={deletedCount}");
    }
  }

  private sealed class RenameGroupDialog : Dialog<bool>
  {
    private readonly TextBox _name;

    internal RenameGroupDialog(string currentName)
    {
      Title = "Rename Group";
      Result = false;
      Resizable = false;
      ClientSize = new Size(RenameWindowWidth, RenameWindowHeight);

      _name = new TextBox { Text = currentName };
      var renameButton = new Button { Text = "Rename" };
      var cancelButton = new Button { Text = "Cancel" };
      renameButton.Click += (_, _) =>
      {
        Result = true;
        Close();
      };
      cancelButton.Click += (_, _) => Close();

      Content = new StackLayout
      {
        Padding = new Padding(DialogPadding),
        Spacing = ControlSpacing,
        Items =
        {
          _name,
          new StackLayout
          {
            Orientation = Orientation.Horizontal,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Spacing = ControlSpacing,
            Items = { cancelButton, renameButton }
          }
        }
      };

      DefaultButton = renameButton;
      AbortButton = cancelButton;
      Shown += (_, _) =>
      {
        _name.Focus();
        _name.SelectAll();
      };
    }

    internal string GroupName => _name.Text;
  }
}
