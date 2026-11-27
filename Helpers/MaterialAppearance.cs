using System.Globalization;
using System.Runtime.CompilerServices;
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
  [CommandDefault("vMaterialManager", "vTools.MaterialAppearance.ManagerButtonSize", "Side length in logical pixels of the button beside material selectors. Positive value.")]
  private const int DefaultManagerButtonSize = 24; // Positive logical pixels; fits inline grid editors.
  [CommandDefault("vMaterialManager", "vTools.MaterialAppearance.ManagerButtonGap", "Horizontal gap in logical pixels between a material selector and its manager button. Zero or greater.")]
  private const int DefaultManagerButtonGap = 3; // Nonnegative logical pixels.
  private const int ManagerIconSize = 16; // Square icon size in logical pixels.
  private const double ManagerIconStroke = 1.25; // Line thickness in logical pixels for the material-library icon.
  private const string ManagerTip = "Open Material Manager"; // Tooltip and accessible name.
  private const string ManagerIcon = "M2 2 L14 2 L14 14 L2 14 Z M2 6 L14 6 M6 6 L6 14 M9 9 L12 9 M9 12 L12 12"; // Material library table, in a 16 x 16 icon.
  private static readonly int ManagerButtonSize=Math.Max(1,CommandDefaults.Get("vMaterialManager","vTools.MaterialAppearance.ManagerButtonSize",DefaultManagerButtonSize)); // Logical pixels.
  private static readonly int ManagerButtonGap=Math.Max(0,CommandDefaults.Get("vMaterialManager","vTools.MaterialAppearance.ManagerButtonGap",DefaultManagerButtonGap)); // Logical pixels.
  internal static System.Drawing.Color PatternSmithColor => CommandDefaults.Get("vMaterialManager", "vTools.MaterialAppearance.PatternSmithColor", DefaultPatternSmithColor);
  private static readonly DependencyProperty LinkedMaterialProperty=DependencyProperty.RegisterAttached(
    "LinkedMaterial",typeof(bool),typeof(MaterialAppearance),new PropertyMetadata(false));
  private static readonly ConditionalWeakTable<System.Windows.Controls.ComboBox,PickerAppearance> Pickers=new();
  private static Brush LinkedBrush()
  {
    var color=PatternSmithColor;
    return new SolidColorBrush(Color.FromArgb(color.A,color.R,color.G,color.B));
  }
  internal static string Description(Reference? material)=>material==null?"Custom material":
    $"PatternSmith: {material.Name}\n{material.Group}\nID: {material.Id}";

  // Also used by Properties+ when vTools is loaded.
  internal static Eto.Forms.Button ManagerButton(Action open)
  {
    var button=new Eto.Forms.Button {Width=ManagerButtonSize,Height=ManagerButtonSize,Text="...",ToolTip=ManagerTip};
    void InstallIcon()
    {
      if(button.ControlObject is not System.Windows.Controls.Button native)return;
      System.Windows.Automation.AutomationProperties.SetName(native,ManagerTip);
      var icon=new System.Windows.Shapes.Path
      {
        Data=Geometry.Parse(ManagerIcon),StrokeThickness=ManagerIconStroke,Width=ManagerIconSize,Height=ManagerIconSize,
        Stretch=Stretch.Uniform,IsHitTestVisible=false
      };
      icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding(nameof(native.Foreground)){Source=native});
      native.Content=icon;
      native.Padding=new Thickness(0);
    }
    button.Load+=(_,_)=>InstallIcon();
    InstallIcon();
    button.Click+=(_,_)=>open();
    return button;
  }

  internal static Eto.Forms.Button ManagerButton(Rhino.RhinoDoc doc,Eto.Forms.Window? modalOwner=null,Action? afterClose=null)=>
    ManagerButton(()=>
    {
      if(doc.IsAvailable&&Commands.vMaterialManager.Open(doc,modalOwner)==Rhino.Commands.Result.Success)
        afterClose?.Invoke();
    });

  internal static Eto.Forms.TableLayout WithManager(Eto.Forms.ListControl picker,Eto.Forms.Button button)=>
    new(){Spacing=new Eto.Drawing.Size(ManagerButtonGap,0),Rows={new TableRow(new TableCell(picker,true),button)}};

  internal static Eto.Forms.TableLayout WithManager(Eto.Forms.ListControl picker,Rhino.RhinoDoc doc,
    Eto.Forms.Window? modalOwner=null,Action? afterClose=null)=>WithManager(picker,ManagerButton(doc,modalOwner,afterClose));

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
    string? Linked(int index)=>material(index) is {} reference?Description(reference):null;
    return AttachPicker(control,Linked,()=>Linked(control.SelectedIndex));
  }

  // Shared with Properties+ without exposing material-library types across plug-ins.
  internal static Action AttachPicker(Eto.Forms.ListControl control,Func<int,string?> linked,Func<string?> selected)
  {
    void Refresh()
    {
      if(!control.IsDisposed&&control.ControlObject is System.Windows.Controls.ComboBox combo)
        StylePicker(combo,linked,selected);
    }
    control.Load+=(_,_)=>Refresh();control.SelectedIndexChanged+=(_,_)=>Refresh();
    Refresh();return Refresh;
  }

  internal static void GridPicker(Eto.Forms.Control control,int column,Func<int,Reference?> material)
  {
    DataGrid? installed=null;
    void Install()
    {
      if(control.ControlObject is not DataGrid grid||ReferenceEquals(installed,grid))return;
      installed=grid;
      grid.PreparingCellForEdit+=(_,e)=>
      {
        if(column<0||column>=grid.Columns.Count||grid.Columns[column]!=e.Column)return;
        bool Apply()
        {
          if(FindPicker(e.EditingElement) is not {} combo)return false;
          string? Linked(int index)=>material(index) is {} reference?Description(reference):null;
          StylePicker(combo,Linked,()=>Linked(combo.SelectedIndex));
          return true;
        }
        if(!Apply()&&!e.EditingElement.IsLoaded)
        {
          void Loaded(object sender,RoutedEventArgs args)
          {
            e.EditingElement.Loaded-=Loaded;
            Apply();
          }
          e.EditingElement.Loaded+=Loaded;
        }
      };
    }
    control.Load+=(_,_)=>Install();Install();
  }

  private static System.Windows.Controls.ComboBox? FindPicker(DependencyObject element)
  {
    if(element is System.Windows.Controls.ComboBox combo)return combo;
    for(int index=0;index<VisualTreeHelper.GetChildrenCount(element);index++)
      if(FindPicker(VisualTreeHelper.GetChild(element,index)) is {} child)return child;
    return null;
  }

  private static void StylePicker(System.Windows.Controls.ComboBox combo,Func<int,string?> linked,Func<string?> selected)
  {
    var appearance=Pickers.GetValue(combo,element=>new PickerAppearance(element));
    appearance.Linked=linked;
    appearance.Selected=selected;
    appearance.Refresh();
  }

  private sealed class PickerAppearance
  {
    private readonly System.Windows.Controls.ComboBox _combo;
    private readonly Brush? _ordinaryBrush;
    private readonly FontWeight _ordinaryWeight;
    private readonly Brush _linkedBrush=LinkedBrush();
    internal Func<int,string?> Linked {get;set;}=_=>null;
    internal Func<string?> Selected {get;set;}=()=>null;
    internal PickerAppearance(System.Windows.Controls.ComboBox combo)
    {
      _combo=combo;_ordinaryBrush=combo.Foreground;_ordinaryWeight=combo.FontWeight;
      var basis=combo.ItemContainerStyle??combo.TryFindResource(typeof(ComboBoxItem)) as Style;
      var style=new Style(typeof(ComboBoxItem),basis);
      // A linked selection tints the closed field, not the ordinary entries in its popup.
      if(_ordinaryBrush!=null)style.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,_ordinaryBrush));
      style.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty,_ordinaryWeight));
      var linked=new Trigger {Property=LinkedMaterialProperty,Value=true};
      linked.Setters.Add(new Setter(System.Windows.Controls.Control.FontWeightProperty,FontWeights.SemiBold));
      style.Triggers.Add(linked);
      foreach(bool selected in new[]{false,true})
      {
        var tint=new MultiTrigger();
        tint.Conditions.Add(new Condition(LinkedMaterialProperty,true));
        tint.Conditions.Add(new Condition(ComboBoxItem.IsHighlightedProperty,false));
        tint.Conditions.Add(new Condition(ComboBoxItem.IsSelectedProperty,selected));
        if(selected)tint.Conditions.Add(new Condition(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty,false));
        tint.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty,_linkedBrush));
        style.Triggers.Add(tint);
      }
      combo.ItemContainerStyle=style;
      combo.ItemContainerGenerator.StatusChanged+=(_,_)=>Refresh();
      combo.DropDownOpened+=(_,_)=>Refresh();
      combo.SelectionChanged+=(_,_)=>Refresh();
    }

    internal void Refresh()
    {
      var status=_combo.ItemContainerGenerator.Status;
      if(status==System.Windows.Controls.Primitives.GeneratorStatus.GeneratingContainers)return;
      if(status==System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        for(int index=0;index<_combo.Items.Count;index++)
          if(_combo.ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem item)
          {
            var description=Linked(index);
            item.SetValue(LinkedMaterialProperty,description!=null);
            item.ToolTip=description??Description(null);
          }
      bool selected=Selected()!=null;
      _combo.SetCurrentValue(System.Windows.Controls.Control.ForegroundProperty,selected?_linkedBrush:_ordinaryBrush);
      _combo.SetCurrentValue(System.Windows.Controls.Control.FontWeightProperty,selected?FontWeights.SemiBold:_ordinaryWeight);
    }
  }
}
