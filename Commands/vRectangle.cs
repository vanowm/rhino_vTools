using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Native rectangle command ported from Rectangle.py.
/// Creates an axis-aligned rectangle polyline from width/height inputs and a corner point.
/// Width and height can be driven by total selected curve length.
/// </summary>
public sealed class vRectangle : vToolsCommand
{
  private const string OptionsSectionName = "vRectangle";
  private const string WidthKey = "width";
  private const string HeightKey = "height";
  private const string LayerKey = "layer";
  private const string LabelKey = "label";
  private const string LastBlXKey = "lastBlX";
  private const string LastBlYKey = "lastBlY";
  private const string LastBlZKey = "lastBlZ";
  private const string LastBrXKey = "lastBrX";
  private const string LastBrYKey = "lastBrY";
  private const string LastBrZKey = "lastBrZ";

  // Option defaults
  private const double DefaultWidth = 10.0; // Rectangle width in model units; greater than zero.
  private const double DefaultHeight = 5.0; // Rectangle height in model units; greater than zero.
  private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Rhino layer path or the shared current-layer sentinel.
  private const bool DefaultLabel = false; // true creates a fitted dimension label; false creates only the rectangle.
  private const string LabelLayerName = "Reference"; // Rhino layer name used for rectangle dimension labels.
  private const double LabelPaddingFraction = 0.1; // Empty inset on each rectangle side as a fraction from zero through less than 0.5.
  private static readonly Color DefaultLabelLayerColor = Color.White; // Color assigned when the Reference label layer must be created.

  private static double _width = DefaultWidth;
  private static double _height = DefaultHeight;
  private static string _layer = DefaultLayer;
  private static bool _label = DefaultLabel;
  private static Point3d? _lastBottomLeft;
  private static Point3d? _lastBottomRight;

  /// <summary>
  /// Rhino command name.
  /// </summary>
  public override string EnglishName => "vRectangle";

  /// <summary>
  /// Creates an axis-aligned rectangle from width/height and a bottom-left corner pick.
  /// </summary>
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();
    var layerSession = new DuplicateOutputLayerSession(doc, _layer, EnglishName);

    var width = _width;
    var height = _height;

    // If curves are already selected, use their total length as the width.
    var preselectedWidth = SelectedObjectsTotalCurveLength(doc);
    if (preselectedWidth.HasValue)
    {
      width = preselectedWidth.Value;
      RhinoApp.WriteLine($"vRectangle: Width from selected objects: {width:G}");
    }

    // Default corner: last bottom-right, then last bottom-left, then nothing.
    var defaultCorner = _lastBottomRight ?? _lastBottomLeft;

    if (!PickBottomLeftCorner(
          doc,
          mode,
          layerSession,
          ref width,
          ref height,
          defaultCorner,
          out var bottomLeft))
      return Result.Cancel;

    var rectId = AddRectangle(
      doc,
      bottomLeft,
      width,
      height,
      layerSession.CreateAttributes(doc));
    if (rectId == Guid.Empty)
    {
      RhinoApp.WriteLine("vRectangle: failed to create rectangle.");
      return Result.Failure;
    }

    if (_label)
    {
      var labelEntity = BuildDimensionLabel(doc, bottomLeft, width, height);
      var labelAttributes = new ObjectAttributes
      {
        LayerIndex = EnsureLabelLayer(doc)
      };
      var labelId = doc.Objects.AddText(labelEntity, labelAttributes);
      if (labelId == Guid.Empty)
      {
        doc.Objects.Delete(rectId, quiet: true);
        RhinoApp.WriteLine("vRectangle: failed to create dimension label.");
        return Result.Failure;
      }

      doc.Groups.Add(new[] { rectId, labelId });
    }

    _width = width;
    _height = height;
    _lastBottomLeft = bottomLeft;
    _lastBottomRight = new Point3d(bottomLeft.X + width, bottomLeft.Y, bottomLeft.Z);
    SavePersistedOptions();

    doc.Views.Redraw();
    return Result.Success;
  }

  // -------------------------------------------------------------------------
  // Bottom-left corner pick with live preview and dimension input.
  // -------------------------------------------------------------------------

  private static bool PickBottomLeftCorner(
    RhinoDoc doc,
    RunMode mode,
    DuplicateOutputLayerSession layerSession,
    ref double width,
    ref double height,
    Point3d? defaultCorner,
    out Point3d bottomLeft)
  {
    bottomLeft = Point3d.Unset;
    var w = width;
    var h = height;

    while (true)
    {
      var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt(defaultCorner.HasValue
        ? "Pick bottom-left corner or type widthxheight or coordinates (Enter for last position)"
        : "Pick bottom-left corner or type widthxheight or coordinates");
      gp.AcceptNothing(defaultCorner.HasValue);
      gp.AcceptString(true);

      var widthOpt = new OptionDouble(w, true, 0.0);
      var heightOpt = new OptionDouble(h, true, 0.0);
      var idxWidth = gp.AddOptionDouble("Width", ref widthOpt);
      var idxHeight = gp.AddOptionDouble("Height", ref heightOpt);
      var idxLayer = gp.AddOption("Layer", layerSession.OptionLayerName);
      var labelToggle = new OptionToggle(_label, "No", "Yes");
      var idxLabel = gp.AddOptionToggle("Label", ref labelToggle);

      var rectanglePreviewColor = ResolveLayerColor(
        doc,
        layerSession.CreateAttributes(doc).LayerIndex,
        Color.Cyan);
      var labelPreviewColor = ResolveLayerColor(
        doc,
        doc.Layers.FindByFullPath(LabelLayerName, RhinoMath.UnsetIntIndex),
        DefaultLabelLayerColor);

      EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
      {
        if (w <= 0.0 || h <= 0.0)
          return;
        var poly = BuildRectanglePolyline(e.CurrentPoint, w, h);
        PreviewDisplay.DrawPolyline(e.Display, poly, rectanglePreviewColor, 1);
        if (_label)
        {
          var label = BuildDimensionLabel(doc, e.CurrentPoint, w, h);
          e.Display.DrawAnnotation(label, labelPreviewColor);
        }
      };

      gp.DynamicDraw += drawPreview;
      var res = gp.Get();
      gp.DynamicDraw -= drawPreview;
      layerSession.ObserveCurrentLayer(doc);
      _label = labelToggle.CurrentValue;

      if (gp.CommandResult() != Result.Success)
        return false;

      if (res == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt != null)
        {
          if (opt.Index == idxWidth)
          {
            w = widthOpt.CurrentValue;
            _width = w;
            SavePersistedOptions();
          }
          else if (opt.Index == idxHeight)
          {
            h = heightOpt.CurrentValue;
            _height = h;
            SavePersistedOptions();
          }
          else if (opt.Index == idxLayer)
          {
            PromptForLayer(doc, mode, layerSession);
          }
          else if (opt.Index == idxLabel)
          {
            SavePersistedOptions();
          }
        }

        if (w <= 0.0 || h <= 0.0)
          RhinoApp.WriteLine("vRectangle: Width and Height must be greater than zero.");

        continue;
      }

      if (res == GetResult.String)
      {
        if (TryParseDimensions(gp.StringResult(), out var newWidth, out var newHeight))
        {
          w = newWidth;
          h = newHeight;
          _width = w;
          _height = h;
          SavePersistedOptions();
        }
        else if (TryParsePlacementPoint(doc, gp.StringResult(), out var typedPoint))
        {
          width = w;
          height = h;
          bottomLeft = typedPoint;
          return true;
        }
        else
        {
          RhinoApp.WriteLine("vRectangle: enter widthxheight or one to three coordinate values.");
        }
        continue;
      }

      if (res == GetResult.Point)
      {
        if (w <= 0.0 || h <= 0.0)
        {
          RhinoApp.WriteLine("vRectangle: Width and Height must be greater than zero.");
          continue;
        }

        width = w;
        height = h;
        bottomLeft = gp.Point();
        return true;
      }

      if (res == GetResult.Nothing)
      {
        if (!defaultCorner.HasValue)
        {
          RhinoApp.WriteLine("vRectangle: no previous rectangle position available.");
          continue;
        }

        if (w <= 0.0 || h <= 0.0)
        {
          RhinoApp.WriteLine("vRectangle: Width and Height must be greater than zero.");
          continue;
        }

        width = w;
        height = h;
        bottomLeft = defaultCorner.Value;
        return true;
      }

      return false;
    }
  }

  // -------------------------------------------------------------------------
  // Geometry helpers.
  // -------------------------------------------------------------------------

  private static Polyline BuildRectanglePolyline(Point3d bl, double w, double h)
  {
    var br = new Point3d(bl.X + w, bl.Y, bl.Z);
    var tr = new Point3d(bl.X + w, bl.Y + h, bl.Z);
    var tl = new Point3d(bl.X, bl.Y + h, bl.Z);
    return new Polyline(new[] { bl, br, tr, tl, bl });
  }

  private static Guid AddRectangle(
    RhinoDoc doc,
    Point3d bl,
    double w,
    double h,
    ObjectAttributes attributes) =>
    doc.Objects.AddPolyline(BuildRectanglePolyline(bl, w, h), attributes);

  private static TextEntity BuildDimensionLabel(
    RhinoDoc doc,
    Point3d bottomLeft,
    double width,
    double height)
  {
    var availableWidth = width * (1.0 - 2.0 * LabelPaddingFraction);
    var availableHeight = height * (1.0 - 2.0 * LabelPaddingFraction);
    var center = new Point3d(
      bottomLeft.X + width * 0.5,
      bottomLeft.Y + height * 0.5,
      bottomLeft.Z);
    var text = new TextEntity
    {
      Plane = new Plane(center, Vector3d.XAxis, Vector3d.YAxis),
      PlainText = $"{FormatDimension(doc, width)} x {FormatDimension(doc, height)}",
      TextHeight = availableHeight,
      Justification = TextJustification.MiddleCenter
    };

    var bounds = text.GetBoundingBox(true);
    if (bounds.IsValid)
    {
      var renderedWidth = bounds.Max.X - bounds.Min.X;
      var renderedHeight = bounds.Max.Y - bounds.Min.Y;
      if (renderedWidth > RhinoMath.ZeroTolerance &&
          renderedHeight > RhinoMath.ZeroTolerance)
      {
        text.TextHeight *= Math.Min(
          1.0,
          Math.Min(
            availableWidth / renderedWidth,
            availableHeight / renderedHeight));
      }
    }

    return text;
  }

  private static string FormatDimension(RhinoDoc doc, double value)
  {
    try
    {
      var formatted = doc.FormatNumber(value);
      if (!string.IsNullOrWhiteSpace(formatted))
        return formatted.Trim();
    }
    catch
    {
    }

    var precision = Math.Max(0, Math.Min(10, doc.ModelDistanceDisplayPrecision));
    return value.ToString($"F{precision}", CultureInfo.CurrentCulture);
  }

  private static int EnsureLabelLayer(RhinoDoc doc)
  {
    var existing = doc.Layers.FindByFullPath(
      LabelLayerName,
      RhinoMath.UnsetIntIndex);
    if (existing >= 0)
      return existing;

    var created = doc.Layers.Add(new Layer
    {
      Name = LabelLayerName,
      Color = DefaultLabelLayerColor
    });
    return created >= 0 ? created : doc.Layers.CurrentLayerIndex;
  }

  private static Color ResolveLayerColor(
    RhinoDoc doc,
    int layerIndex,
    Color fallback) =>
    layerIndex >= 0 && layerIndex < doc.Layers.Count && doc.Layers[layerIndex] != null
      ? doc.Layers[layerIndex].Color
      : fallback;

  private static void PromptForLayer(
    RhinoDoc doc,
    RunMode mode,
    DuplicateOutputLayerSession layerSession)
  {
    if (!LayerSelector.TrySelect(
          doc,
          layerSession.OptionLayerName,
          DuplicateCommandSupport.CurrentLayerOption,
          "vRectangle target layer",
          mode,
          allowNewLayer: false,
          out var selectedLayer))
      return;

    _layer = DuplicateCommandSupport.NormalizeLayerOption(selectedLayer);
    layerSession.ApplyOption(doc, _layer);
    SavePersistedOptions();
  }

  private static bool TryParseDimensions(
    string? input,
    out double width,
    out double height)
  {
    width = 0.0;
    height = 0.0;
    var text = input?.Trim();
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var separator = text.IndexOf('x', StringComparison.OrdinalIgnoreCase);
    if (separator <= 0 || separator >= text.Length - 1)
      return false;

    return TryParsePositiveDimension(text[..separator], out width) &&
           TryParsePositiveDimension(text[(separator + 1)..], out height);
  }

  private static bool TryParsePositiveDimension(string input, out double value)
  {
    return TryParseFraction(input, out value) && value > 0.0;
  }

  private static bool TryParseFraction(string input, out double value)
  {
    value = 0.0;
    var text = input.Trim();
    if (TryParseCoordinateValue(text, out value))
      return true;

    var separator = text.IndexOf('+');
    if (separator < 0)
      separator = text.IndexOf('-', 1);
    if (separator > 0 &&
        TryParseCoordinateValue(text[..separator], out var whole) &&
        TryParseSimpleFraction(text[(separator + 1)..], out var fraction))
    {
      value = whole + fraction;
      return true;
    }

    return TryParseSimpleFraction(text, out value);
  }

  private static bool TryParseSimpleFraction(string input, out double value)
  {
    value = 0.0;
    var slash = input.IndexOf('/');
    if (slash <= 0 || slash >= input.Length - 1 ||
        !TryParseCoordinateValue(input[..slash], out var numerator) ||
        !TryParseCoordinateValue(input[(slash + 1)..], out var denominator) ||
        Math.Abs(denominator) <= double.Epsilon)
      return false;

    value = numerator / denominator;
    return true;
  }

  private static bool TryParsePlacementPoint(
    RhinoDoc doc,
    string? input,
    out Point3d point)
  {
    point = Point3d.Unset;
    var text = input?.Trim();
    if (string.IsNullOrWhiteSpace(text))
      return false;

    var components = text.Split(',', StringSplitOptions.TrimEntries);
    if (components.Length is < 1 or > 3)
      return false;

    var values = new double[3];
    for (var i = 0; i < components.Length; i++)
    {
      if (!TryParseCoordinateValue(components[i], out values[i]))
        return false;
    }

    var constructionPlane =
      doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    point = constructionPlane.PointAt(values[0], values[1], values[2]);
    return point.IsValid;
  }

  private static bool TryParseCoordinateValue(string input, out double value)
  {
    var text = input.Trim();
    return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
           double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
  }

  private static double? SumCurveLengths(IReadOnlyList<Curve> curves)
  {
    if (curves.Count == 0)
      return null;
    var total = 0.0;
    foreach (var c in curves)
      total += c.GetLength();
    return total > 0.0 ? total : null;
  }

  private static double? SelectedObjectsTotalCurveLength(RhinoDoc doc)
  {
    var curves = new List<Curve>();
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
    {
      if (obj?.Geometry is Curve c)
        curves.Add(c);
    }
    return SumCurveLengths(curves);
  }

  // -------------------------------------------------------------------------
  // Option persistence.
  // -------------------------------------------------------------------------

  private static void LoadPersistedOptions()
  {
    var values = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        var width = _width;
        var height = _height;
        var layer = _layer;
        var label = _label;
        Point3d? lastBl = null;
        Point3d? lastBr = null;

        if (ToolsOptionStore.TryGetDouble(section, WidthKey, out var w) && w > 0.0)
          width = w;
        if (ToolsOptionStore.TryGetDouble(section, HeightKey, out var h) && h > 0.0)
          height = h;
        if (ToolsOptionStore.TryGetString(section, LayerKey, out var savedLayer))
          layer = DuplicateCommandSupport.NormalizeLayerOption(savedLayer);
        if (ToolsOptionStore.TryGetBool(section, LabelKey, out var savedLabel))
          label = savedLabel;

        if (ToolsOptionStore.TryGetDouble(section, LastBlXKey, out var blX) &&
            ToolsOptionStore.TryGetDouble(section, LastBlYKey, out var blY) &&
            ToolsOptionStore.TryGetDouble(section, LastBlZKey, out var blZ))
          lastBl = new Point3d(blX, blY, blZ);

        if (ToolsOptionStore.TryGetDouble(section, LastBrXKey, out var brX) &&
            ToolsOptionStore.TryGetDouble(section, LastBrYKey, out var brY) &&
            ToolsOptionStore.TryGetDouble(section, LastBrZKey, out var brZ))
          lastBr = new Point3d(brX, brY, brZ);

        return (width, height, layer, label, lastBl, lastBr);
      });

    _width = values.width;
    _height = values.height;
    _layer = values.layer;
    _label = values.label;
    _lastBottomLeft = values.lastBl;
    _lastBottomRight = values.lastBr;
  }

  private static void SavePersistedOptions()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section =>
      {
        section[WidthKey] = _width;
        section[HeightKey] = _height;
        section[LayerKey] = _layer;
        section[LabelKey] = _label;

        if (_lastBottomLeft.HasValue)
        {
          section[LastBlXKey] = _lastBottomLeft.Value.X;
          section[LastBlYKey] = _lastBottomLeft.Value.Y;
          section[LastBlZKey] = _lastBottomLeft.Value.Z;
        }

        if (_lastBottomRight.HasValue)
        {
          section[LastBrXKey] = _lastBottomRight.Value.X;
          section[LastBrYKey] = _lastBottomRight.Value.Y;
          section[LastBrZKey] = _lastBottomRight.Value.Z;
        }
      });
  }
}
