using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace vTools;

/// <summary>Visible sort arrows that retain custom checkbox and symbol headers.</summary>
internal static class GridSortIndicators
{
  private sealed class HeaderState(DataTemplate? original)
  {
    internal DataTemplate? Original = original;
    internal DataTemplate? Applied;
    internal DataTemplate? Ascending;
    internal DataTemplate? Descending;
  }
  private static readonly ConditionalWeakTable<DataGridColumn, HeaderState> Headers = new();

  internal static void Update(Eto.Forms.GridView grid, int sortedColumn, bool descending)
  {
    if (grid.ControlObject is not DataGrid native) return;
    for (int index = 0; index < native.Columns.Count; index++)
    {
      var column = native.Columns[index];
      var state = Headers.GetValue(column, item => new HeaderState(item.HeaderTemplate));
      if (!ReferenceEquals(column.HeaderTemplate, state.Applied) && !ReferenceEquals(column.HeaderTemplate, state.Original))
      {
        state.Original = column.HeaderTemplate;
        state.Ascending = state.Descending = null;
      }
      // Eto's theme can omit the native sorting glyph; draw one in the content instead.
      column.SortDirection = null;
      state.Applied = index != sortedColumn ? state.Original : descending
        ? state.Descending ??= Create(state.Original, true)
        : state.Ascending ??= Create(state.Original, false);
      column.HeaderTemplate = state.Applied;
    }
  }

  private static DataTemplate Create(DataTemplate? original, bool descending)
  {
    var panel = new FrameworkElementFactory(typeof(DockPanel));
    var arrow = new FrameworkElementFactory(typeof(TextBlock));
    arrow.SetValue(DockPanel.DockProperty, Dock.Right);
    arrow.SetValue(TextBlock.TextProperty, descending ? "\u25BC" : "\u25B2");
    arrow.SetValue(FrameworkElement.MarginProperty, new Thickness(SystemParameters.MenuCheckmarkWidth / 2, 0, 0, 0));
    arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
    arrow.SetValue(FrameworkElement.ToolTipProperty, descending ? "Descending order" : "Ascending order");
    panel.AppendChild(arrow);
    var content = new FrameworkElementFactory(typeof(ContentPresenter));
    content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding());
    if (original != null) content.SetValue(ContentPresenter.ContentTemplateProperty, original);
    panel.AppendChild(content);
    return new DataTemplate { VisualTree = panel };
  }
}
