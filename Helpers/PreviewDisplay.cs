using System;
using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;

namespace vTools;

internal static class PreviewDisplay
{
  // Defaults and customizable constants
  private const int DefaultFillBoundaryEmphasis = 0; // Extra screen pixels for an opt-in fill silhouette band; zero disables it for ordinary highlights.
  private const int DefaultMinimumFillBoundaryPixels = 0; // Zero leaves normal bands unchanged; positive pixels keep opt-in narrow silhouettes visible.
  private const double DefaultMinimumFillBoundaryOpacity = 0; // Zero retains the style opacity; positive 0-1 opacity boosts only opt-in subpixel silhouettes.
  private const int DefaultFillBoundaryHaloPixels = 0; // Zero omits the separate halo; positive values are its total screen-pixel stroke width.
  private const bool DefaultScreenSpaceCurves = false; // true projects curve highlights into screen space; false uses ordinary world-space drawing.
  private const int DefaultMinimumCurveMarkerPixels = 0; // Zero preserves normal highlighting; positive pixels add a cue for subpixel curves when a caller opts in.
  private const int DefaultMinimumObjectMarkerPixels = 0; // Zero preserves ordinary object rendering; positive pixels add a cue for subpixel non-curve objects when opted in.
  private const int DefaultObjectLocatorPixels = 0; // Zero omits screen-space object bounds; positive pixels size a persistent locator cross and minimum rectangle for opt-in callers.
  private const bool DefaultDepthBiasedShading = false; // true pulls a fill slightly toward the camera; false draws the fill at model depth.
  private const double HoverDepthOffsetPixels = 1; // Camera-facing depth displacement in world units per screen pixel; positive avoids coincident hover fills vanishing.
  // Thickness values are pixel increments above Rhino's current default curve thickness.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.MinimumCurveThickness", "Minimum preview stroke width in display pixels; one or greater.")] private const int MinimumCurveThickness = 1; // Minimum preview stroke width in display pixels; one or greater.

  private static readonly int ConfiguredMinimumCurveThickness = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.MinimumCurveThickness", MinimumCurveThickness); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.DefaultHighlightRedrawOnChange", "true redraws after each highlight update; false lets the caller batch updates and redraw once.")] private const bool DefaultHighlightRedrawOnChange = true; // true redraws after each highlight update; false lets the caller batch updates and redraw once.

  private static readonly bool ConfiguredDefaultHighlightRedrawOnChange = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.DefaultHighlightRedrawOnChange", DefaultHighlightRedrawOnChange); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.DefaultHighlightDrawOnTop", "true draws highlighted curves and annotations over scene depth; false retains normal depth testing.")] private const bool DefaultHighlightDrawOnTop = false; // true draws highlighted curves and annotations over scene depth; false retains normal depth testing.

  private static readonly bool ConfiguredDefaultHighlightDrawOnTop = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.DefaultHighlightDrawOnTop", DefaultHighlightDrawOnTop); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.DefaultAutomaticForegroundDrawing", "true draws through this conduit; false lets a caller coordinate several highlight layers explicitly.")] private const bool DefaultAutomaticForegroundDrawing = true; // true draws through this conduit; false lets a caller coordinate several highlight layers explicitly.

  private static readonly bool ConfiguredDefaultAutomaticForegroundDrawing = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.DefaultAutomaticForegroundDrawing", DefaultAutomaticForegroundDrawing); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.DefaultAutomaticShadedDrawing", "true draws shaded bodies in this conduit; false lets a caller draw fills before its own wires.")] private const bool DefaultAutomaticShadedDrawing = true; // true draws shaded bodies in this conduit; false lets a caller draw fills before its own wires.

  private static readonly bool ConfiguredDefaultAutomaticShadedDrawing = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.DefaultAutomaticShadedDrawing", DefaultAutomaticShadedDrawing); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.DefaultUseObjectCurveColor", "true uses each curve's resolved object/layer/viewport color; false uses the supplied highlight color.")] private const bool DefaultUseObjectCurveColor = false; // true uses each curve's resolved object/layer/viewport color; false uses the supplied highlight color.

  private static readonly bool ConfiguredDefaultUseObjectCurveColor = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.DefaultUseObjectCurveColor", DefaultUseObjectCurveColor); // Resolved once from advanced defaults; restart Rhino after changes.

  // Generic object highlighting is deliberately cyan with a dark outline so it cannot be
  // confused with Rhino's yellow selected-object display.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightColor", "RGB body/wire color for temporary object highlighting.", "#FF00DCFF")] private static readonly Color ObjectHighlightColor = global::vTools.CommandDefaults.GetColor("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightColor", Color.FromArgb(0, 220, 255)); // RGB body/wire color for temporary object highlighting.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightOutlineColor", "RGB outline color around temporarily highlighted geometry.", "#FF003748")] private static readonly Color ObjectHighlightOutlineColor = global::vTools.CommandDefaults.GetColor("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightOutlineColor", Color.FromArgb(0, 55, 72)); // RGB outline color around temporarily highlighted geometry.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightDotBackground", "RGB background color for highlighted text dots.", "#FF007891")] private static readonly Color ObjectHighlightDotBackground = global::vTools.CommandDefaults.GetColor("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightDotBackground", Color.FromArgb(0, 120, 145)); // RGB background color for highlighted text dots.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightTransparency", "Shaded-object transparency from 0.0 opaque through 1.0 invisible.")] private const double ObjectHighlightTransparency = 0.55; // Shaded-object transparency from 0.0 opaque through 1.0 invisible.

  private static readonly double ConfiguredObjectHighlightTransparency = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightTransparency", ObjectHighlightTransparency); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightStrokeEmphasis", "Cyan stroke pixels added to Rhino's current curve thickness.")] private const int ObjectHighlightStrokeEmphasis = 1; // Cyan stroke pixels added to Rhino's current curve thickness.

  private static readonly int ConfiguredObjectHighlightStrokeEmphasis = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightStrokeEmphasis", ObjectHighlightStrokeEmphasis); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightOutlineEmphasis", "Dark outline pixels added to Rhino's current curve thickness.")] private const int ObjectHighlightOutlineEmphasis = 3; // Dark outline pixels added to Rhino's current curve thickness.

  private static readonly int ConfiguredObjectHighlightOutlineEmphasis = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightOutlineEmphasis", ObjectHighlightOutlineEmphasis); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightPointSize", "Cyan point-marker diameter in display pixels; one or greater.")] private const int ObjectHighlightPointSize = 7; // Cyan point-marker diameter in display pixels; one or greater.

  private static readonly int ConfiguredObjectHighlightPointSize = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.ObjectHighlightPointSize", ObjectHighlightPointSize); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay","vTools.PreviewDisplay.ObjectHighlightPointOutlineExtra","Dark point-outline pixels added beyond the cyan marker body. Zero or greater.")]
  private const int ObjectHighlightPointOutlineExtra = 2; // Dark point-outline pixels added beyond the cyan marker body.
  private static readonly int ConfiguredObjectHighlightPointOutlineExtra = global::vTools.CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.ObjectHighlightPointOutlineExtra",ObjectHighlightPointOutlineExtra); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("PreviewDisplay","vTools.PreviewDisplay.ObjectHighlightSubDStrokeWidth","Cyan SubD wire width in display pixels; positive float. Positive value.")]
  private const float ObjectHighlightSubDStrokeWidth = 2.0f; // Cyan SubD wire width in display pixels; positive float.
  private static readonly float ConfiguredObjectHighlightSubDStrokeWidth = global::vTools.CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.ObjectHighlightSubDStrokeWidth",ObjectHighlightSubDStrokeWidth); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("PreviewDisplay","vTools.PreviewDisplay.ObjectHighlightSubDOutlineWidth","Dark SubD outline width in display pixels; greater than the cyan width. Positive value.")]
  private const float ObjectHighlightSubDOutlineWidth = 4.0f; // Dark SubD outline width in display pixels; greater than the cyan width.
  private static readonly float ConfiguredObjectHighlightSubDOutlineWidth = global::vTools.CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.ObjectHighlightSubDOutlineWidth",ObjectHighlightSubDOutlineWidth); // Resolved through vSettings; restart Rhino after advanced changes.

  private static readonly ObjectHighlightStyle DefaultObjectStyle = new( // Generic RGB body, outline, dot background, and 0-1 shading transparency; preserves the cyan style.
    ObjectHighlightColor, ObjectHighlightOutlineColor, ObjectHighlightDotBackground, ConfiguredObjectHighlightTransparency);
  internal static readonly ObjectHighlightStyle WireOnlyObjectStyle = DefaultObjectStyle with { Transparency=1.0 }; // Fully transparent body skips surface meshing; retains the shared cyan/dark outlined wires for batch feedback.
  [CommandHighlightDefault("PreviewDisplay","vTools.PreviewDisplay.HistoryWarningStyle","History warning","#FFFF8000","#FF9B1E64","#FF9B1E64",0.75)]
  internal static readonly ObjectHighlightStyle HistoryWarningStyle = CommandDefaults.Highlight("PreviewDisplay","vTools.PreviewDisplay.HistoryWarningStyle",new( // Original vSplit warning palette; used only for history feedback.
    BodyColor: Color.FromArgb(255, 128, 0), // RGB orange body/wire color for history-affected objects.
    OutlineColor: Color.FromArgb(155, 30, 100), // RGB dark magenta outline around history-affected objects.
    DotBackground: Color.FromArgb(155, 30, 100), // RGB background for history-affected text dots.
    Transparency: 0.25)); // Shaded warning transparency from 0.0 opaque through 1.0 invisible.

  // Added geometry uses a green center stroke over a wider black outline.
  // StrokeEmphasis controls the colored width; OutlineEmphasis controls the total outlined width.
  [CommandHighlightDefault("PreviewDisplay","vTools.PreviewDisplay.AddedStyle","Added curve","#FF32CD32","#FF000000","#00000000",0,CurveOnly=true)]
  private static readonly CurveHighlightStyle AddedStyle = ConfigureCurveStyle("AddedStyle",new( // Colors and relative pixel widths for geometry being added.
    StrokeColor: Color.LimeGreen,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3));

  // Removed geometry uses a red center stroke over a wider black outline.
  // StrokeEmphasis controls the colored width; OutlineEmphasis controls the total outlined width.
  [CommandHighlightDefault("PreviewDisplay","vTools.PreviewDisplay.RemovedStyle","Removed curve","#FFFF0000","#FF000000","#00000000",0,CurveOnly=true)]
  private static readonly CurveHighlightStyle RemovedStyle = ConfigureCurveStyle("RemovedStyle",new( // Colors and relative pixel widths for geometry being removed.
    StrokeColor: Color.Red,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3));

  // Overlapping geometry uses a cyan center stroke over a wider black outline.
  [CommandHighlightDefault("PreviewDisplay","vTools.PreviewDisplay.OverlapStyle","Overlapping curve","#FF00FFFF","#FF000000","#00000000",0,CurveOnly=true)]
  private static readonly CurveHighlightStyle OverlapStyle = ConfigureCurveStyle("OverlapStyle",new( // Colors and relative pixel widths for overlapping geometry.
    StrokeColor: Color.Cyan,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3));

  // Generic outlined curves use these defaults unless the caller supplies different thickness values.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.OutlinedCurveOutlineColor", "Default outline color for emphasized source curves.", "#FF000000")] private static readonly Color OutlinedCurveOutlineColor = global::vTools.CommandDefaults.GetColor("PreviewDisplay", "vTools.PreviewDisplay.OutlinedCurveOutlineColor", Color.Black); // Default outline color for emphasized source curves.
  [CommandDefault("PreviewDisplay","vTools.PreviewDisplay.OutlinedCurveStrokeEmphasis","Extra colored-stroke width in screen pixels; zero or greater.")]
  private const int OutlinedCurveStrokeEmphasis = 1; // Nonnegative stroke pixels added to Rhino's curve thickness.
  private static readonly int ConfiguredOutlinedCurveStrokeEmphasis = CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.OutlinedCurveStrokeEmphasis",OutlinedCurveStrokeEmphasis); // Resolved screen pixels; restart after advanced changes.
  [CommandDefault("PreviewDisplay","vTools.PreviewDisplay.OutlinedCurveOutlineExtra","Outline width added beyond the colored stroke in screen pixels; one or greater.")]
  private const int OutlinedCurveOutlineExtra = 2; // Positive outline pixels added beyond the colored stroke.
  private static readonly int ConfiguredOutlinedCurveOutlineExtra = CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.OutlinedCurveOutlineExtra",OutlinedCurveOutlineExtra); // Resolved screen pixels; restart after advanced changes.

  // Highlight point markers use their curve style's colors and these pixel-size settings.
  [global::vTools.CommandDefault("PreviewDisplay","vTools.PreviewDisplay.HighlightPointStyle","Rhino point marker style used by shared previews.")]
  private const PointStyle HighlightPointStyle = PointStyle.RoundSimple; // Rhino point marker style used by shared previews.
  private static readonly PointStyle ConfiguredHighlightPointStyle = global::vTools.CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.HighlightPointStyle",HighlightPointStyle); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("PreviewDisplay","vTools.PreviewDisplay.HighlightPointMinimumSize","Minimum point diameter in display pixels; one or greater. Positive value.")]
  private const int HighlightPointMinimumSize = 4; // Minimum point diameter in display pixels; one or greater.
  private static readonly int ConfiguredHighlightPointMinimumSize = global::vTools.CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.HighlightPointMinimumSize",HighlightPointMinimumSize); // Resolved through vSettings; restart Rhino after advanced changes.
  [global::vTools.CommandDefault("PreviewDisplay", "vTools.PreviewDisplay.HighlightPointThicknessEmphasis", "Point-size pixels added to current curve thickness.")] private const int HighlightPointThicknessEmphasis = 2; // Point-size pixels added to current curve thickness.

  private static readonly int ConfiguredHighlightPointThicknessEmphasis = global::vTools.CommandDefaults.Get("PreviewDisplay", "vTools.PreviewDisplay.HighlightPointThicknessEmphasis", HighlightPointThicknessEmphasis); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PreviewDisplay","vTools.PreviewDisplay.HighlightPointOutlineExtra","Outline pixels added beyond the colored point body. Zero or greater.")]
  private const int HighlightPointOutlineExtra = 2; // Outline pixels added beyond the colored point body.
  private static readonly int ConfiguredHighlightPointOutlineExtra = global::vTools.CommandDefaults.Get("PreviewDisplay","vTools.PreviewDisplay.HighlightPointOutlineExtra",HighlightPointOutlineExtra); // Resolved through vSettings; restart Rhino after advanced changes.

  private readonly record struct CurveHighlightStyle(
    Color StrokeColor,
    int StrokeEmphasis,
    Color OutlineColor,
    int OutlineEmphasis);

  private static CurveHighlightStyle ConfigureCurveStyle(string name,CurveHighlightStyle factory)
  {
    string key="vTools.PreviewDisplay."+name;
    return factory with
    {
      StrokeColor=CommandDefaults.GetColor("PreviewDisplay",key+".StrokeColor",factory.StrokeColor),
      OutlineColor=CommandDefaults.GetColor("PreviewDisplay",key+".OutlineColor",factory.OutlineColor),
      StrokeEmphasis=Math.Max(0,CommandDefaults.Get("PreviewDisplay",key+".StrokeEmphasis",factory.StrokeEmphasis)),
      OutlineEmphasis=Math.Max(0,CommandDefaults.Get("PreviewDisplay",key+".OutlineEmphasis",factory.OutlineEmphasis))
    };
  }

  internal readonly record struct ObjectHighlightStyle(
    Color BodyColor,
    Color OutlineColor,
    Color DotBackground,
    double Transparency)
  {
    internal int StrokeEmphasis { get; init; } = ConfiguredObjectHighlightStrokeEmphasis; // Pixels added to Rhino's curve width; zero keeps normal-width highlighted curves.
    internal int OutlineEmphasis { get; init; } = ConfiguredObjectHighlightOutlineEmphasis; // Total outline emphasis in pixels; at or below StrokeEmphasis omits the outline.
    internal bool UseObjectCurveColor { get; init; } = ConfiguredDefaultUseObjectCurveColor; // true preserves source curve colors; false uses BodyColor.
    internal int PointSizePixels { get; init; } = ConfiguredObjectHighlightPointSize; // Positive viewport-pixel diameter; defaults to the shared point-size setting.
    internal int PointOutlineExtraPixels { get; init; } = ConfiguredObjectHighlightPointOutlineExtra; // Nonnegative screen pixels added around point markers; defaults to the shared outline setting.
  }

  public static int Thickness(DisplayPipeline display, int emphasis = 0) =>
    Math.Max(ConfiguredMinimumCurveThickness, display.DefaultCurveThickness + emphasis);

  public static void DrawCurve(
    DisplayPipeline display,
    Curve curve,
    Color color,
    int emphasis = 0)
  {
    display.DrawCurve(curve, color, Thickness(display, emphasis));
  }

  public static void DrawLine(
    DisplayPipeline display,
    Point3d from,
    Point3d to,
    Color color,
    int emphasis = 0)
  {
    display.DrawLine(from, to, color, Thickness(display, emphasis));
  }

  public static void DrawLine(
    DisplayPipeline display,
    Line line,
    Color color,
    int emphasis = 0)
  {
    display.DrawLine(line, color, Thickness(display, emphasis));
  }

  public static void DrawPolyline(
    DisplayPipeline display,
    Polyline polyline,
    Color color,
    int emphasis = 0)
  {
    display.DrawPolyline(polyline, color, Thickness(display, emphasis));
  }

  public static void DrawBrepWires(
    DisplayPipeline display,
    Brep brep,
    Color color,
    int emphasis = 0)
  {
    display.DrawBrepWires(brep, color, Thickness(display, emphasis));
  }

  public static void DrawMeshWires(
    DisplayPipeline display,
    Mesh mesh,
    Color color,
    int emphasis = 0)
  {
    display.DrawMeshWires(mesh, color, Thickness(display, emphasis));
  }

  public static void DrawOutlinedCurve(
    DisplayPipeline display,
    Curve curve,
    Color color,
    int? emphasis = null,
    int? outlineExtra = null)
  {
    int stroke = emphasis ?? ConfiguredOutlinedCurveStrokeEmphasis;
    display.DrawCurve(
      curve,
      OutlinedCurveOutlineColor,
      Thickness(display, stroke + Math.Max(1, outlineExtra ?? ConfiguredOutlinedCurveOutlineExtra)));
    display.DrawCurve(curve, color, Thickness(display, stroke));
  }

  private static void DrawObjectHighlightCurve(DisplayPipeline display, Curve curve, ObjectHighlightStyle style)
  {
    if (style.OutlineEmphasis > style.StrokeEmphasis)
      display.DrawCurve(curve, style.OutlineColor, Thickness(display, style.OutlineEmphasis));
    display.DrawCurve(
      curve,
      style.BodyColor,
      Thickness(display, style.StrokeEmphasis));
  }

  private static void DrawObjectHighlightBrep(DisplayPipeline display, Brep brep, ObjectHighlightStyle style)
  {
    if (style.OutlineEmphasis > style.StrokeEmphasis)
      display.DrawBrepWires(brep, style.OutlineColor, Thickness(display, style.OutlineEmphasis));
    display.DrawBrepWires(
      brep,
      style.BodyColor,
      Thickness(display, style.StrokeEmphasis));
  }

  private static void DrawHighlightCurve(
    DisplayPipeline display,
    Curve curve,
    CurveHighlightStyle style)
  {
    display.DrawCurve(
      curve,
      style.OutlineColor,
      Thickness(display, style.OutlineEmphasis));
    display.DrawCurve(
      curve,
      style.StrokeColor,
      Thickness(display, style.StrokeEmphasis));
  }

  public static void DrawAddedCurve(DisplayPipeline display, Curve curve) =>
    DrawHighlightCurve(display, curve, AddedStyle);

  private static void DrawHighlightPoint(
    DisplayPipeline display,
    Point3d point,
    CurveHighlightStyle style)
  {
    var size = Math.Max(
      ConfiguredHighlightPointMinimumSize,
      Thickness(display, ConfiguredHighlightPointThicknessEmphasis));
    DrawHighlightPoint(display, point, style, size);
  }

  private static void DrawHighlightPoint(
    DisplayPipeline display,
    Point3d point,
    CurveHighlightStyle style,
    int size)
  {
    size = Math.Max(ConfiguredMinimumCurveThickness, size);
    display.DrawPoint(
      point,
      ConfiguredHighlightPointStyle,
      size + ConfiguredHighlightPointOutlineExtra,
      style.OutlineColor);
    display.DrawPoint(
      point,
      ConfiguredHighlightPointStyle,
      size,
      style.StrokeColor);
  }

  public static void DrawAddedPoint(DisplayPipeline display, Point3d point) =>
    DrawHighlightPoint(display, point, AddedStyle);

  public static void DrawAddedPoint(DisplayPipeline display, Point3d point, int size) =>
    DrawHighlightPoint(display, point, AddedStyle, size);

  public static void DrawRemovedCurve(DisplayPipeline display, Curve curve) =>
    DrawHighlightCurve(display, curve, RemovedStyle);

  public static void DrawRemovedPoint(DisplayPipeline display, Point3d point) =>
    DrawHighlightPoint(display, point, RemovedStyle);

  public static void DrawOverlapCurve(DisplayPipeline display, Curve curve) =>
    DrawHighlightCurve(display, curve, OverlapStyle);

  /// <summary>
  /// Draws a temporary, selection-distinct highlight over arbitrary document objects.
  /// Call <see cref="SetObjects"/> as the highlighted set changes and dispose it when the
  /// owning interaction ends.
  /// </summary>
  internal sealed class ObjectHighlighter : DisplayConduit, IDisposable
  {
    internal bool RedrawOnChange { get; set; } = ConfiguredDefaultHighlightRedrawOnChange;
    internal bool DrawOnTop { get; set; } = ConfiguredDefaultHighlightDrawOnTop;
    internal bool AutomaticForegroundDrawing { get; set; } = ConfiguredDefaultAutomaticForegroundDrawing;
    internal bool AutomaticShadedDrawing { get; set; } = ConfiguredDefaultAutomaticShadedDrawing;
    internal bool DepthBiasedShading { get; set; } = DefaultDepthBiasedShading;
    internal int FillBoundaryEmphasis { get; set; } = DefaultFillBoundaryEmphasis;
    internal int MinimumFillBoundaryPixels { get; set; } = DefaultMinimumFillBoundaryPixels;
    internal double MinimumFillBoundaryOpacity { get; set; } = DefaultMinimumFillBoundaryOpacity;
    internal int FillBoundaryHaloPixels { get; set; } = DefaultFillBoundaryHaloPixels;
    internal bool ScreenSpaceCurves { get; set; } = DefaultScreenSpaceCurves;
    internal Func<Guid,Guid>? CurveColorSourceId { get; set; } // Resolves a temporary curve section to the live object that supplies its color.
    internal int MinimumCurveMarkerPixels { get; set; } = DefaultMinimumCurveMarkerPixels;
    internal int MinimumObjectMarkerPixels { get; set; } = DefaultMinimumObjectMarkerPixels;
    internal int ObjectLocatorPixels { get; set; } = DefaultObjectLocatorPixels;
    private readonly RhinoDoc _doc;
    private readonly HashSet<Guid> _objectIds = [];
    private readonly Dictionary<Guid, GeometryBase> _fallbackGeometry = [];
    private readonly Dictionary<Guid, Polyline[]> _fillBoundaries = [];
    private readonly ObjectHighlightStyle _style;
    private readonly DisplayMaterial _material;

    internal ObjectHighlighter(RhinoDoc doc) : this(doc, DefaultObjectStyle) { }

    internal ObjectHighlighter(RhinoDoc doc, ObjectHighlightStyle style)
    {
      _doc = doc;
      _style = style;
      _material = new DisplayMaterial(style.BodyColor)
      {
        Transparency = style.Transparency,
        BackTransparency = style.Transparency
      };
    }

    internal void SetObjects(IEnumerable<Guid> objectIds) => SetObjects(objectIds, null);

    // Snapshot geometry is borrowed from the caller, which must keep it alive until cleared.
    internal void SetObjects(IEnumerable<Guid> objectIds, IReadOnlyDictionary<Guid, GeometryBase>? fallbackGeometry)
      => SetObjects(objectIds, fallbackGeometry, null);

    // Fill silhouettes are also borrowed; their owner keeps them synchronized with the mesh.
    internal void SetObjects(IEnumerable<Guid> objectIds, IReadOnlyDictionary<Guid, GeometryBase>? fallbackGeometry,
      IReadOnlyDictionary<Guid, Polyline[]>? fillBoundaries)
    {
      var nextIds = objectIds.ToHashSet();
      var nextFallback = fallbackGeometry?.Where(entry => nextIds.Contains(entry.Key))
        .ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];
      var nextBoundaries = fillBoundaries?.Where(entry => nextIds.Contains(entry.Key))
        .ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];
      if (_objectIds.SetEquals(nextIds) && _fallbackGeometry.Count == nextFallback.Count
        && nextFallback.All(entry => _fallbackGeometry.TryGetValue(entry.Key, out var old)
          && ReferenceEquals(old, entry.Value)) && _fillBoundaries.Count == nextBoundaries.Count
        && nextBoundaries.All(entry => _fillBoundaries.TryGetValue(entry.Key, out var old)
          && ReferenceEquals(old, entry.Value)))
        return;

      _objectIds.Clear();
      _objectIds.UnionWith(nextIds);
      _fallbackGeometry.Clear();
      foreach (var entry in nextFallback)
        _fallbackGeometry.Add(entry.Key, entry.Value);
      _fillBoundaries.Clear();
      foreach (var entry in nextBoundaries)
        _fillBoundaries.Add(entry.Key, entry.Value);
      Enabled = _objectIds.Count > 0;
      if (RedrawOnChange) _doc.Views.Redraw();
    }

    private GeometryBase? GetGeometry(Guid objectId) =>
      _doc.Objects.FindId(objectId)?.Geometry ?? _fallbackGeometry.GetValueOrDefault(objectId);

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (!AutomaticShadedDrawing) return;
      if (!DepthBiasedShading)
      {
        DrawShadedHighlights(e);
        return;
      }
      _material.IsTwoSided = true;
      _material.BackDiffuse = _style.BodyColor;
      var previousDepth = e.Display.DepthMode;
      var previousBias = e.Display.ZBiasMode;
      e.Display.PushDepthTesting(true);
      e.Display.PushDepthWriting(false);
      e.Display.PushCullFaceMode(CullFaceMode.DrawFrontAndBack);
      try
      {
        e.Display.DepthMode = DepthMode.Neutral;
        e.Display.ZBiasMode = ZBiasMode.TowardsCamera;
        DrawShadedHighlights(e);
      }
      finally
      {
        e.Display.ZBiasMode = previousBias;
        e.Display.DepthMode = previousDepth;
        e.Display.PopCullFaceMode();
        e.Display.PopDepthWriting();
        e.Display.PopDepthTesting();
      }
    }

    internal void DrawFillBoundaryHighlights(DrawEventArgs e,IReadOnlySet<Guid>? included=null)
    {
      if (FillBoundaryEmphasis <= 0 || e.RhinoDoc.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;
      int alpha = (int)Math.Round(_style.BodyColor.A * Math.Clamp(1 - _style.Transparency, 0, 1));
      if (alpha <= 0) return;
      foreach (var (id,paths) in _fillBoundaries)
      {
        if(included!=null&&!included.Contains(id))continue;
        foreach (var path in paths)
        {
          var stroke=FillBoundaryStroke(e,path,alpha);
          e.Display.DrawPolyline(path,Color.FromArgb(stroke.Alpha,_style.BodyColor),stroke.Width);
        }
      }
    }

    private (int Width,int Alpha) FillBoundaryStroke(DrawEventArgs e,Polyline path,int alpha)
    {
      int width=Thickness(e.Display,Math.Max(0,FillBoundaryEmphasis));
      if(FillBoundaryEmphasis>0&&MinimumFillBoundaryPixels>0&&path.Count>2&&e.Viewport.GetWorldToScreenScale(path[0],out double scale))
      {
        var area=Vector3d.Zero;double perimeter=0;var origin=path[0];
        for(int i=1;i<path.Count;i++)
        {area+=Vector3d.CrossProduct(path[i-1]-origin,path[i]-origin);perimeter+=path[i-1].DistanceTo(path[i]);}
        if(perimeter<=0||area.Length/perimeter*scale<MinimumFillBoundaryPixels)
        {
          width=Math.Max(width,MinimumFillBoundaryPixels);
          alpha=Math.Max(alpha,(int)Math.Round(_style.BodyColor.A*Math.Clamp(MinimumFillBoundaryOpacity,0,1)));
        }
      }
      return(width,alpha);
    }

    internal void DrawFillHaloHighlights(DrawEventArgs e,IReadOnlySet<Guid>? included=null)
    {
      if(FillBoundaryHaloPixels<=0||e.RhinoDoc.RuntimeSerialNumber!=_doc.RuntimeSerialNumber||_style.BodyColor.A==0)return;
      foreach(var (id,paths) in _fillBoundaries)
        if(included==null||included.Contains(id))
          foreach(var path in paths)DrawScreenPolyline(e,path,_style.BodyColor,FillBoundaryHaloPixels);
    }
    private static void DrawScreenPolyline(DrawEventArgs e,Polyline path,Color color,float width)
    {
      if(path.Count<2)return;
      var first=e.Viewport.WorldToClient(path[0]);
      for(int index=1;index<path.Count;index++)
      {
        var second=e.Viewport.WorldToClient(path[index]);
        if(first.IsValid&&second.IsValid)e.Display.Draw2dLine(new System.Drawing.PointF((float)first.X,(float)first.Y),
          new System.Drawing.PointF((float)second.X,(float)second.Y),color,width);
        first=second;
      }
    }

    internal void DrawShadedHighlights(DrawEventArgs e,IReadOnlySet<Guid>? included=null)
    {
      if (e.RhinoDoc.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;

      if (_style.Transparency >= 1.0)
        return;

      foreach (var objectId in _objectIds)
      {
        if(included!=null&&!included.Contains(objectId))continue;
        var geometry = GetGeometry(objectId);
        if (geometry == null) continue;
        var direction = DepthBiasedShading ? -e.Viewport.CameraDirection : Vector3d.Zero;
        double scale = 0;
        bool offset = DepthBiasedShading && direction.Unitize() &&
          e.Viewport.GetWorldToScreenScale(geometry.GetBoundingBox(false).Center, out scale) && scale > 0;
        if (offset)
          e.Display.PushModelTransform(Transform.Translation(direction * (HoverDepthOffsetPixels / scale)));
        try
        {
          switch (geometry)
          {
            case Brep brep:
              e.Display.DrawBrepShaded(brep, _material);
              break;
            case Extrusion extrusion:
            {
              using var brep = extrusion.ToBrep();
              if (brep != null)
                e.Display.DrawBrepShaded(brep, _material);
              break;
            }
            case Surface surface:
            {
              using var brep = surface.ToBrep();
              if (brep != null)
                e.Display.DrawBrepShaded(brep, _material);
              break;
            }
            case Mesh mesh:
              e.Display.DrawMeshShaded(mesh, _material);
              break;
            case SubD subD:
              e.Display.DrawSubDShaded(subD, _material);
              break;
          }
        }
        finally { if (offset) e.Display.PopModelTransform(); }
      }
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (AutomaticForegroundDrawing) DrawForegroundHighlights(e);
    }

    internal void DrawForegroundHighlights(DrawEventArgs e)
    {
      if (e.RhinoDoc.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;

      bool onTop = DrawOnTop;
      if (onTop) { e.Display.PushDepthTesting(false); e.Display.PushDepthWriting(false); }
      try
      {
      foreach (var objectId in _objectIds)
      {
        var obj = _doc.Objects.FindId(objectId);
        var geometry = obj?.Geometry ?? _fallbackGeometry.GetValueOrDefault(objectId);
        switch (geometry)
        {
          case Curve curve:
            var colorSource = _style.UseObjectCurveColor
              ? obj ?? (CurveColorSourceId is {} resolve ? _doc.Objects.FindId(resolve(objectId)) : null)
              : null;
            var curveStyle = colorSource != null
              ? _style with { BodyColor = colorSource.Attributes.DrawColor(_doc, e.Viewport.Id) }
              : _style;
            if(ScreenSpaceCurves&&curve.TryGetPolyline(out var polyline))
            {
              if(curveStyle.OutlineEmphasis>curveStyle.StrokeEmphasis)DrawScreenPolyline(e,polyline,curveStyle.OutlineColor,Thickness(e.Display,curveStyle.OutlineEmphasis));
              DrawScreenPolyline(e,polyline,curveStyle.BodyColor,Thickness(e.Display,curveStyle.StrokeEmphasis));
            }
            else DrawObjectHighlightCurve(e.Display, curve, curveStyle);
            DrawMinimumCurveMarker(e, curve, curveStyle);
            break;
          case Brep brep:
            DrawObjectHighlightBrep(e.Display, brep, _style);
            break;
          case Extrusion extrusion:
          {
            using var brep = extrusion.ToBrep();
            if (brep != null)
              DrawObjectHighlightBrep(e.Display, brep, _style);
            break;
          }
          case Surface surface:
          {
            using var brep = surface.ToBrep();
            if (brep != null)
              DrawObjectHighlightBrep(e.Display, brep, _style);
            break;
          }
          case Mesh mesh:
            e.Display.DrawMeshWires(
              mesh,
              _style.OutlineColor,
              Thickness(e.Display, _style.OutlineEmphasis));
            e.Display.DrawMeshWires(
              mesh,
              _style.BodyColor,
              Thickness(e.Display, _style.StrokeEmphasis));
            break;
          case SubD subD:
            e.Display.DrawSubDWires(
              subD,
              _style.OutlineColor,
              Math.Max(ConfiguredObjectHighlightSubDOutlineWidth,Thickness(e.Display,_style.OutlineEmphasis)));
            e.Display.DrawSubDWires(
              subD,
              _style.BodyColor,
              Math.Max(ConfiguredObjectHighlightSubDStrokeWidth,Thickness(e.Display,_style.StrokeEmphasis)));
            break;
          case Rhino.Geometry.Point point:
            e.Display.DrawPoint(
              point.Location,
              PointStyle.RoundSimple,
              _style.PointSizePixels + _style.PointOutlineExtraPixels,
              _style.OutlineColor);
            e.Display.DrawPoint(
              point.Location,
              PointStyle.RoundSimple,
              _style.PointSizePixels,
              _style.BodyColor);
            break;
          case PointCloud pointCloud:
            e.Display.DrawPointCloud(
              pointCloud,
              _style.PointSizePixels + _style.PointOutlineExtraPixels,
              _style.OutlineColor);
            e.Display.DrawPointCloud(
              pointCloud,
              _style.PointSizePixels,
              _style.BodyColor);
            break;
          case TextEntity text:
            e.Display.DrawAnnotation(text, _style.BodyColor);
            break;
          case AnnotationBase annotation:
            e.Display.DrawAnnotation(annotation, _style.BodyColor);
            break;
          case TextDot dot:
            e.Display.DrawDot(
              dot,
              _style.BodyColor,
              _style.DotBackground,
              _style.OutlineColor);
            break;
          case Hatch hatch:
            e.Display.DrawHatch(hatch, _style.BodyColor, _style.OutlineColor);
            break;
          case Light light:
            e.Display.DrawLight(light, _style.BodyColor);
            break;
          case { } other:
            e.Display.DrawBox(
              other.GetBoundingBox(true),
              _style.BodyColor,
              Thickness(e.Display, ConfiguredObjectHighlightStrokeEmphasis));
            break;
        }
        if(MinimumObjectMarkerPixels>0&&geometry is not null and not Curve and not Rhino.Geometry.Point and not PointCloud and not TextDot&&
          NeedsBoundsMarker(e.Viewport,geometry.GetBoundingBox(true),MinimumObjectMarkerPixels))
        {
          var point=geometry.GetBoundingBox(true).Center;
          e.Display.DrawPoint(point,PointStyle.RoundSimple,MinimumObjectMarkerPixels+_style.PointOutlineExtraPixels,_style.OutlineColor);
          e.Display.DrawPoint(point,PointStyle.RoundSimple,MinimumObjectMarkerPixels,_style.BodyColor);
        }
        if(ObjectLocatorPixels>0&&geometry!=null)DrawObjectLocator(e,geometry.GetBoundingBox(true));
      }
      }
      finally
      {
        if (onTop) { e.Display.PopDepthWriting(); e.Display.PopDepthTesting(); }
      }
    }

    private void DrawObjectLocator(DrawEventArgs e,BoundingBox bounds)
    {
      if(!TryScreenBounds(e.Viewport,bounds,out double left,out double top,out double right,out double bottom))return;
      var size=e.Viewport.Size;
      if(right<0||bottom<0||left>=size.Width||top>=size.Height)return;
      double radius=ObjectLocatorPixels/2d;
      double x=(Math.Max(0,left)+Math.Min(size.Width-1,right))/2,y=(Math.Max(0,top)+Math.Min(size.Height-1,bottom))/2;
      left=Math.Max(0,Math.Min(left,x-radius));right=Math.Min(size.Width-1,Math.Max(right,x+radius));
      top=Math.Max(0,Math.Min(top,y-radius));bottom=Math.Min(size.Height-1,Math.Max(bottom,y+radius));
      var rectangle=Rectangle.FromLTRB((int)Math.Floor(left),(int)Math.Floor(top),(int)Math.Ceiling(right),(int)Math.Ceiling(bottom));
      foreach(bool outline in new[]{true,false})
      {
        var color=outline?_style.OutlineColor:_style.BodyColor;
        int width=Thickness(e.Display,outline?_style.OutlineEmphasis:_style.StrokeEmphasis);
        e.Display.Draw2dRectangle(rectangle,color,width,Color.Transparent);
        e.Display.Draw2dLine(new PointF((float)(x-radius),(float)y),new PointF((float)(x+radius),(float)y),color,width);
        e.Display.Draw2dLine(new PointF((float)x,(float)(y-radius)),new PointF((float)x,(float)(y+radius)),color,width);
      }
    }

    private void DrawMinimumCurveMarker(DrawEventArgs e, Curve curve, ObjectHighlightStyle style)
    {
      if (!NeedsCurveMarker(e.Viewport, curve, MinimumCurveMarkerPixels)) return;
      var point = curve.PointAt(curve.Domain.Mid);
      if (!point.IsValid) return;
      e.Display.DrawPoint(point, PointStyle.RoundSimple, MinimumCurveMarkerPixels + style.PointOutlineExtraPixels, style.OutlineColor);
      e.Display.DrawPoint(point, PointStyle.RoundSimple, MinimumCurveMarkerPixels, style.BodyColor);
    }

    internal static bool NeedsCurveMarker(RhinoViewport viewport, Curve curve, int minimumPixels)
    {
      if (minimumPixels <= 0) return false;
      return NeedsBoundsMarker(viewport,curve.GetBoundingBox(true),minimumPixels);
    }
    internal static bool NeedsBoundsMarker(RhinoViewport viewport,BoundingBox bounds,int minimumPixels)
    {
      return minimumPixels>0&&TryScreenBounds(viewport,bounds,out double left,out double top,out double right,out double bottom)&&
        Math.Max(right-left,bottom-top)<minimumPixels;
    }
    private static bool TryScreenBounds(RhinoViewport viewport,BoundingBox bounds,out double left,out double top,out double right,out double bottom)
    {
      left=top=double.PositiveInfinity;right=bottom=double.NegativeInfinity;
      if(!bounds.IsValid)return false;
      foreach (var corner in bounds.GetCorners())
      {
        var pixel = viewport.WorldToClient(corner);
        if (!pixel.IsValid) return false;
        left = Math.Min(left, pixel.X); right = Math.Max(right, pixel.X);
        top = Math.Min(top, pixel.Y); bottom = Math.Max(bottom, pixel.Y);
      }
      return true;
    }

    public void Dispose()
    {
      Enabled = false;
      _objectIds.Clear();
      _fallbackGeometry.Clear();
      _fillBoundaries.Clear();
      if (RedrawOnChange) _doc.Views.Redraw();
    }
  }
}
