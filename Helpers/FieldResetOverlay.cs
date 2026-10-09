using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EtoControl = Eto.Forms.Control;

namespace vTools;

internal sealed class FieldResetOverlay : IDisposable
{
  // Customizable appearance defaults
  private const double ButtonSize = 16.0; // Reset hit target in device-independent pixels; positive square size.
  private const double IconSize = 12.0; // Reset icon canvas size in device-independent pixels; smaller than its button.
  private const double IconStroke = 1.3; // Icon stroke width in device-independent pixels; positive value.
  private const double EdgeGap = 2.0; // Space between the reset control and input edge or arrow buttons in device-independent pixels.
  private const double HoverOpacity = 0.12; // Foreground-colored hover tint opacity, zero through one, matching light and dark input themes.
  private const double PressedOpacity = 0.22; // Foreground-colored pressed tint opacity, zero through one.
  private const string IconPath = "M2,4 A4.25,4.25 0 1 1 2,8 M2,1 L2,4 L5,4"; // Balanced counterclockwise circular reset arrow on the 12-by-12 icon canvas.

  private readonly EtoControl _control;
  private readonly Func<bool> _changed;
  private readonly Action _reset;
  private readonly string _toolTip;
  private FrameworkElement? _host;
  private FrameworkElement? _editor;
  private System.Windows.Controls.Control? _paddingTarget;
  private Thickness _padding;
  private AdornerLayer? _layer;
  private ResetAdorner? _adorner;
  private bool _disposed;

  internal FieldResetOverlay(EtoControl control, Func<bool> changed, Action reset, string toolTip)
  {
    _control = control;
    _changed = changed;
    _reset = reset;
    _toolTip = toolTip;
    control.Load += OnLoaded;
  }

  internal static bool NumberChanged(double value, double defaultValue, int precision) =>
    Math.Round(value, precision, MidpointRounding.AwayFromZero) !=
    Math.Round(defaultValue, precision, MidpointRounding.AwayFromZero);

  private void OnLoaded(object? sender, EventArgs e) => Refresh();
  private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();

  internal void Refresh()
  {
    if (_disposed) return;
    if (_adorner == null && _control.ControlObject is FrameworkElement host && host.IsLoaded)
    {
      _host = host;
      if (host is System.Windows.Controls.Control nativeControl) nativeControl.ApplyTemplate();
      _editor = FindTextBox(host) ?? host;
      _layer = AdornerLayer.GetAdornerLayer(_editor);
      if (_layer == null) return;
      _paddingTarget = _editor as System.Windows.Controls.Control;
      if (_paddingTarget != null) _padding = _paddingTarget.Padding;
      _adorner = new ResetAdorner(_editor, _toolTip, () => { _reset(); Refresh(); });
      _layer.Add(_adorner);
      host.IsVisibleChanged += OnVisibilityChanged;
      host.IsEnabledChanged += OnVisibilityChanged;
    }
    if (_adorner == null || _host == null) return;
    bool visible = _host.IsVisible && _changed();
    _adorner.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    _adorner.IsEnabled = _host.IsEnabled;
    if (_paddingTarget != null)
      _paddingTarget.Padding = visible
        ? new Thickness(_padding.Left, _padding.Top, _padding.Right + ButtonSize + EdgeGap, _padding.Bottom)
        : _padding;
    _adorner.InvalidateArrange();
  }

  private static TextBox? FindTextBox(DependencyObject root)
  {
    if (root is TextBox text) return text;
    for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
      var found = FindTextBox(VisualTreeHelper.GetChild(root, index));
      if (found != null) return found;
    }
    return null;
  }

  internal static double ResetLeft(double width, double reservedRight) =>
    Math.Max(0.0, width - reservedRight - EdgeGap - ButtonSize);

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    _control.Load -= OnLoaded;
    if (_host != null)
    {
      _host.IsVisibleChanged -= OnVisibilityChanged;
      _host.IsEnabledChanged -= OnVisibilityChanged;
    }
    if (_adorner != null) _layer?.Remove(_adorner);
    if (_paddingTarget != null) _paddingTarget.Padding = _padding;
  }

  private sealed class ResetAdorner : Adorner
  {
    private readonly Button _button;
    private readonly VisualCollection _visuals;

    internal ResetAdorner(FrameworkElement editor, string toolTip, Action reset) : base(editor)
    {
      var icon = new System.Windows.Controls.Canvas { Width = IconSize, Height = IconSize };
      var path = new System.Windows.Shapes.Path
      {
        Data = Geometry.Parse(IconPath),
        StrokeThickness = IconStroke,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
      };
      icon.Children.Add(path);
      _button = new Button
      {
        Width = ButtonSize,
        Height = ButtonSize,
        Content = icon,
        ToolTip = toolTip,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Focusable = false,
        Cursor = Cursors.Arrow,
        Template = ButtonTemplate(),
      };
      if (editor is System.Windows.Controls.Control)
        _button.SetBinding(System.Windows.Controls.Control.ForegroundProperty,
          new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Foreground)) { Source = editor });
      path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
        new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = _button });
      _button.Click += (_, e) => { reset(); e.Handled = true; };
      _visuals = new VisualCollection(this) { _button };
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];
    protected override HitTestResult? HitTestCore(PointHitTestParameters parameters) => null;

    protected override Size MeasureOverride(Size constraint)
    {
      _button.Measure(new Size(ButtonSize, ButtonSize));
      return AdornedElement.RenderSize;
    }

    protected override Size ArrangeOverride(Size size)
    {
      double reserved = ReservedArrowWidth(AdornedElement);
      _button.Arrange(new Rect(ResetLeft(size.Width, reserved),
        Math.Max(0.0, (size.Height - ButtonSize) * 0.5), ButtonSize, ButtonSize));
      return size;
    }

    private static double ReservedArrowWidth(UIElement host)
    {
      double reserved = 0.0;
      void Visit(DependencyObject element)
      {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
          var child = VisualTreeHelper.GetChild(element, index);
          if (child is ButtonBase button && button.IsVisible && button.ActualWidth > 0)
          {
            var position = button.TranslatePoint(new Point(), host);
            if (position.X >= host.RenderSize.Width * 0.5)
              reserved = Math.Max(reserved, host.RenderSize.Width - position.X);
          }
          Visit(child);
        }
      }
      Visit(host);
      return reserved;
    }

    private static ControlTemplate ButtonTemplate()
    {
      var grid = new FrameworkElementFactory(typeof(Grid));
      var border = new FrameworkElementFactory(typeof(Border), "ResetBackground");
      border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.ForegroundProperty));
      border.SetValue(UIElement.OpacityProperty, 0.0);
      grid.AppendChild(border);
      var content = new FrameworkElementFactory(typeof(ContentPresenter));
      content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
      content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
      grid.AppendChild(content);
      var template = new ControlTemplate(typeof(Button)) { VisualTree = grid };
      var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
      hover.Setters.Add(new Setter(UIElement.OpacityProperty, HoverOpacity, "ResetBackground"));
      template.Triggers.Add(hover);
      var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
      pressed.Setters.Add(new Setter(UIElement.OpacityProperty, PressedOpacity, "ResetBackground"));
      template.Triggers.Add(pressed);
      return template;
    }
  }
}
