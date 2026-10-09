using System;
using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;

namespace vTools;

internal static class PreviewDisplay
{
  // Thickness values are pixel increments above Rhino's current default curve thickness.
  private const int MinimumCurveThickness = 1; // Minimum preview stroke width in display pixels; one or greater.

  // Generic object highlighting is deliberately cyan with a dark outline so it cannot be
  // confused with Rhino's yellow selected-object display.
  private static readonly Color ObjectHighlightColor = Color.FromArgb(0, 220, 255); // RGB body/wire color for temporary object highlighting.
  private static readonly Color ObjectHighlightOutlineColor = Color.FromArgb(0, 55, 72); // RGB outline color around temporarily highlighted geometry.
  private static readonly Color ObjectHighlightDotBackground = Color.FromArgb(0, 120, 145); // RGB background color for highlighted text dots.
  private const double ObjectHighlightTransparency = 0.55; // Shaded-object transparency from 0.0 opaque through 1.0 invisible.
  private const int ObjectHighlightStrokeEmphasis = 1; // Cyan stroke pixels added to Rhino's current curve thickness.
  private const int ObjectHighlightOutlineEmphasis = 3; // Dark outline pixels added to Rhino's current curve thickness.
  private const int ObjectHighlightPointSize = 7; // Cyan point-marker diameter in display pixels; one or greater.
  private const int ObjectHighlightPointOutlineExtra = 2; // Dark point-outline pixels added beyond the cyan marker body.
  private const float ObjectHighlightSubDStrokeWidth = 2.0f; // Cyan SubD wire width in display pixels; positive float.
  private const float ObjectHighlightSubDOutlineWidth = 4.0f; // Dark SubD outline width in display pixels; greater than the cyan width.

  private static readonly ObjectHighlightStyle DefaultObjectStyle = new( // Generic RGB body, outline, dot background, and 0-1 shading transparency; preserves the cyan style.
    ObjectHighlightColor, ObjectHighlightOutlineColor, ObjectHighlightDotBackground, ObjectHighlightTransparency);
  internal static readonly ObjectHighlightStyle WireOnlyObjectStyle = DefaultObjectStyle with { Transparency=1.0 }; // Fully transparent body skips surface meshing; retains the shared cyan/dark outlined wires for batch feedback.
  internal static readonly ObjectHighlightStyle HistoryWarningStyle = new( // Original vSplit warning palette; used only for history feedback.
    BodyColor: Color.FromArgb(255, 128, 0), // RGB orange body/wire color for history-affected objects.
    OutlineColor: Color.FromArgb(155, 30, 100), // RGB dark magenta outline around history-affected objects.
    DotBackground: Color.FromArgb(155, 30, 100), // RGB background for history-affected text dots.
    Transparency: 0.25); // Shaded warning transparency from 0.0 opaque through 1.0 invisible.

  // Added geometry uses a green center stroke over a wider black outline.
  // StrokeEmphasis controls the colored width; OutlineEmphasis controls the total outlined width.
  private static readonly CurveHighlightStyle AddedStyle = new( // Colors and relative pixel widths for geometry being added.
    StrokeColor: Color.LimeGreen,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3);

  // Removed geometry uses a red center stroke over a wider black outline.
  // StrokeEmphasis controls the colored width; OutlineEmphasis controls the total outlined width.
  private static readonly CurveHighlightStyle RemovedStyle = new( // Colors and relative pixel widths for geometry being removed.
    StrokeColor: Color.Red,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3);

  // Overlapping geometry uses a cyan center stroke over a wider black outline.
  private static readonly CurveHighlightStyle OverlapStyle = new( // Colors and relative pixel widths for overlapping geometry.
    StrokeColor: Color.Cyan,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3);

  // Generic outlined curves use these defaults unless the caller supplies different thickness values.
  private static readonly Color OutlinedCurveOutlineColor = Color.Black; // Default outline color for emphasized source curves.
  private const int OutlinedCurveStrokeEmphasis = 1; // Colored-stroke pixels added to Rhino's curve thickness.
  private const int OutlinedCurveOutlineExtra = 2; // Outline pixels added beyond the colored stroke.

  // Highlight point markers use their curve style's colors and these pixel-size settings.
  private const PointStyle HighlightPointStyle = PointStyle.RoundSimple; // Rhino point marker style used by shared previews.
  private const int HighlightPointMinimumSize = 4; // Minimum point diameter in display pixels; one or greater.
  private const int HighlightPointThicknessEmphasis = 2; // Point-size pixels added to current curve thickness.
  private const int HighlightPointOutlineExtra = 2; // Outline pixels added beyond the colored point body.

  private readonly record struct CurveHighlightStyle(
    Color StrokeColor,
    int StrokeEmphasis,
    Color OutlineColor,
    int OutlineEmphasis);

  internal readonly record struct ObjectHighlightStyle(
    Color BodyColor,
    Color OutlineColor,
    Color DotBackground,
    double Transparency);

  public static int Thickness(DisplayPipeline display, int emphasis = 0) =>
    Math.Max(MinimumCurveThickness, display.DefaultCurveThickness + emphasis);

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
    int emphasis = OutlinedCurveStrokeEmphasis,
    int outlineExtra = OutlinedCurveOutlineExtra)
  {
    display.DrawCurve(
      curve,
      OutlinedCurveOutlineColor,
      Thickness(display, emphasis + Math.Max(1, outlineExtra)));
    display.DrawCurve(curve, color, Thickness(display, emphasis));
  }

  private static void DrawObjectHighlightCurve(DisplayPipeline display, Curve curve, ObjectHighlightStyle style)
  {
    display.DrawCurve(
      curve,
      style.OutlineColor,
      Thickness(display, ObjectHighlightOutlineEmphasis));
    display.DrawCurve(
      curve,
      style.BodyColor,
      Thickness(display, ObjectHighlightStrokeEmphasis));
  }

  private static void DrawObjectHighlightBrep(DisplayPipeline display, Brep brep, ObjectHighlightStyle style)
  {
    display.DrawBrepWires(
      brep,
      style.OutlineColor,
      Thickness(display, ObjectHighlightOutlineEmphasis));
    display.DrawBrepWires(
      brep,
      style.BodyColor,
      Thickness(display, ObjectHighlightStrokeEmphasis));
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
      HighlightPointMinimumSize,
      Thickness(display, HighlightPointThicknessEmphasis));
    DrawHighlightPoint(display, point, style, size);
  }

  private static void DrawHighlightPoint(
    DisplayPipeline display,
    Point3d point,
    CurveHighlightStyle style,
    int size)
  {
    size = Math.Max(MinimumCurveThickness, size);
    display.DrawPoint(
      point,
      HighlightPointStyle,
      size + HighlightPointOutlineExtra,
      style.OutlineColor);
    display.DrawPoint(
      point,
      HighlightPointStyle,
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
    private readonly RhinoDoc _doc;
    private readonly HashSet<Guid> _objectIds = [];
    private readonly Dictionary<Guid, GeometryBase> _fallbackGeometry = [];
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
    {
      var nextIds = objectIds.ToHashSet();
      var nextFallback = fallbackGeometry?.Where(entry => nextIds.Contains(entry.Key))
        .ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];
      if (_objectIds.SetEquals(nextIds) && _fallbackGeometry.Count == nextFallback.Count
        && nextFallback.All(entry => _fallbackGeometry.TryGetValue(entry.Key, out var old)
          && ReferenceEquals(old, entry.Value)))
        return;

      _objectIds.Clear();
      _objectIds.UnionWith(nextIds);
      _fallbackGeometry.Clear();
      foreach (var entry in nextFallback)
        _fallbackGeometry.Add(entry.Key, entry.Value);
      Enabled = _objectIds.Count > 0;
      _doc.Views.Redraw();
    }

    private GeometryBase? GetGeometry(Guid objectId) =>
      _doc.Objects.FindId(objectId)?.Geometry ?? _fallbackGeometry.GetValueOrDefault(objectId);

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (e.RhinoDoc.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;

      if (_style.Transparency >= 1.0)
        return;

      foreach (var objectId in _objectIds)
      {
        var geometry = GetGeometry(objectId);
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
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (e.RhinoDoc.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;

      foreach (var objectId in _objectIds)
      {
        var geometry = GetGeometry(objectId);
        switch (geometry)
        {
          case Curve curve:
            DrawObjectHighlightCurve(e.Display, curve, _style);
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
              Thickness(e.Display, ObjectHighlightOutlineEmphasis));
            e.Display.DrawMeshWires(
              mesh,
              _style.BodyColor,
              Thickness(e.Display, ObjectHighlightStrokeEmphasis));
            break;
          case SubD subD:
            e.Display.DrawSubDWires(
              subD,
              _style.OutlineColor,
              ObjectHighlightSubDOutlineWidth);
            e.Display.DrawSubDWires(
              subD,
              _style.BodyColor,
              ObjectHighlightSubDStrokeWidth);
            break;
          case Rhino.Geometry.Point point:
            e.Display.DrawPoint(
              point.Location,
              PointStyle.RoundSimple,
              ObjectHighlightPointSize + ObjectHighlightPointOutlineExtra,
              _style.OutlineColor);
            e.Display.DrawPoint(
              point.Location,
              PointStyle.RoundSimple,
              ObjectHighlightPointSize,
              _style.BodyColor);
            break;
          case PointCloud pointCloud:
            e.Display.DrawPointCloud(
              pointCloud,
              ObjectHighlightPointSize + ObjectHighlightPointOutlineExtra,
              _style.OutlineColor);
            e.Display.DrawPointCloud(
              pointCloud,
              ObjectHighlightPointSize,
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
              Thickness(e.Display, ObjectHighlightStrokeEmphasis));
            break;
        }
      }
    }

    public void Dispose()
    {
      Enabled = false;
      _objectIds.Clear();
      _fallbackGeometry.Clear();
      _doc.Views.Redraw();
    }
  }
}
