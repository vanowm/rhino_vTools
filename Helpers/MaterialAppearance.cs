using System.Globalization;
using Eto.Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Reference = vTools.MaterialLibrary.PatternSmithReference;

namespace vTools;

/// <summary>Consistent catalog identity feedback for material lists and selectors.</summary>
internal static class MaterialAppearance
{
  // Appearance defaults
  [CommandDefault("vMaterialManager", "vTools.MaterialAppearance.PatternSmithColor", "Text color for PatternSmith-linked materials across material lists and selectors, including inactive selections. Active selection and hover colors retain native contrast.", "#FF0066CC")]
  private static readonly System.Drawing.Color DefaultPatternSmithColor = System.Drawing.Color.FromArgb(0, 102, 204); // Opaque RGB catalog-link color; names and saved material data are unchanged.
  internal static System.Drawing.Color PatternSmithColor => CommandDefaults.Get("vMaterialManager", "vTools.MaterialAppearance.PatternSmithColor", DefaultPatternSmithColor);
  private static Brush LinkedBrush()
  {
    var color=PatternSmithColor;
    return new SolidColorBrush(Color.FromArgb(color.A,color.R,color.G,color.B));
  }
  internal static string Description(Reference? material)=>material==null?"Custom material":
    $"PatternSmith: {material.Name}\n{material.Group}\nID: {material.Id}";

  internal static void Grid(Eto.Forms.Control control,int column,Func<object?,Reference?> material)
  {
    void Install()
    {
      if(control.ControlObject is not DataGrid grid||column<0||column>=grid.Columns.Count)return;
      Cell(grid,grid.Columns[column],new System.Windows.Data.Binding {Converter=new CatalogConverter(material)},
        new System.Windows.Data.Binding {Converter=new CatalogConverter(material,description:true)});
    }
    control.Load+=(_,_)=>Install();
    Install();
  }
  internal static void Cell(DataGrid grid,DataGridColumn column,BindingBase isLinked,BindingBase description)
  {
    var style=new Style(typeof(DataGridCell),column.CellStyle??grid.CellStyle??grid.TryFindResource(typeof(DataGridCell)) as Style);
    var identity=new DataTrigger {Binding=isLinked,Value=true};
    identity.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty,FontWeights.SemiBold));style.Triggers.Add(identity);
    foreach(bool selected in new[]{false,true})
    {
      var color=new MultiDataTrigger();color.Conditions.Add(new Condition(isLinked,true));
      color.Conditions.Add(new Condition(new System.Windows.Data.Binding("IsSelected"){RelativeSource=RelativeSource.Self},selected));
      if(selected)color.Conditions.Add(new Condition(new System.Windows.Data.Binding {Path=new PropertyPath(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty),RelativeSource=RelativeSource.Self},false));
      color.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,LinkedBrush()));style.Triggers.Add(color);
    }
    style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,description));column.CellStyle=style;
  }
  private sealed class CatalogConverter(Func<object?,Reference?> read,bool description=false) : System.Windows.Data.IValueConverter
  {
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>description?Description(read(value)):read(value)!=null;
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
  }

  internal static Action Picker(Eto.Forms.ListControl control,Func<int,Reference?> material)
  {
    System.Windows.Controls.ComboBox? native=null;
    Brush? original=null;
    FontWeight originalWeight=FontWeights.Normal;
    void Refresh()
    {
      if(control.IsDisposed||control.ControlObject is not System.Windows.Controls.ComboBox combo||
        combo.ItemContainerGenerator.Status==System.Windows.Controls.Primitives.GeneratorStatus.GeneratingContainers)return;
      if(!ReferenceEquals(native,combo))
      {
        native=combo;original=combo.Foreground;originalWeight=combo.FontWeight;
        var basis=combo.ItemContainerStyle??combo.TryFindResource(typeof(ComboBoxItem)) as Style;
        var style=new Style(typeof(ComboBoxItem),basis);
        var linked=new Trigger {Property=FrameworkElement.TagProperty,Value=true};
        linked.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty,FontWeights.SemiBold));
        style.Triggers.Add(linked);
        var color=new MultiTrigger();
        color.Conditions.Add(new Condition(FrameworkElement.TagProperty,true));
        color.Conditions.Add(new Condition(ComboBoxItem.IsSelectedProperty,false));
        color.Conditions.Add(new Condition(ComboBoxItem.IsHighlightedProperty,false));
        color.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,LinkedBrush()));
        style.Triggers.Add(color);
        var inactive=new MultiTrigger();inactive.Conditions.Add(new Condition(FrameworkElement.TagProperty,true));
        inactive.Conditions.Add(new Condition(ComboBoxItem.IsHighlightedProperty,false));
        inactive.Conditions.Add(new Condition(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty,false));
        inactive.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,LinkedBrush()));style.Triggers.Add(inactive);
        combo.ItemContainerStyle=style;
        combo.ItemContainerGenerator.StatusChanged+=(_,_)=>Refresh();
      }
      for(int index=0;combo.ItemContainerGenerator.Status==System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated&&index<combo.Items.Count;index++)
        if(combo.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem item)
        {var reference=material(index);item.Tag=reference!=null;item.ToolTip=Description(reference);}
      bool selected=material(control.SelectedIndex)!=null;
      combo.SetCurrentValue(System.Windows.Controls.Control.ForegroundProperty,selected?LinkedBrush():original);
      combo.SetCurrentValue(System.Windows.Controls.Control.FontWeightProperty,selected?FontWeights.SemiBold:originalWeight);
    }
    control.Load+=(_,_)=>Refresh();control.SelectedIndexChanged+=(_,_)=>Refresh();
    Refresh();return Refresh;
  }
}
