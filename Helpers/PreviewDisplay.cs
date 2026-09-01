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

  private static void DrawObjectHighlightCurve(DisplayPipeline display, Curve curve)
  {
    display.DrawCurve(
      curve,
      ObjectHighlightOutlineColor,
      Thickness(display, ObjectHighlightOutlineEmphasis));
    display.DrawCurve(
      curve,
      ObjectHighlightColor,
      Thickness(display, ObjectHighlightStrokeEmphasis));
  }

  private static void DrawObjectHighlightBrep(DisplayPipeline display, Brep brep)
  {
    display.DrawBrepWires(
      brep,
      ObjectHighlightOutlineColor,
      Thickness(display, ObjectHighlightOutlineEmphasis));
    display.DrawBrepWires(
      brep,
      ObjectHighlightColor,
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
    private readonly DisplayMaterial _material = new(ObjectHighlightColor)
    {
      Transparency = ObjectHighlightTransparency,
      BackTransparency = ObjectHighlightTransparency
    };

    internal ObjectHighlighter(RhinoDoc doc)
    {
      _doc = doc;
    }

    internal void SetObjects(IEnumerable<Guid> objectIds)
    {
      var nextIds = objectIds.ToHashSet();
      if (_objectIds.SetEquals(nextIds))
        return;

      _objectIds.Clear();
      _objectIds.UnionWith(nextIds);
      Enabled = _objectIds.Count > 0;
      _doc.Views.Redraw();
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      foreach (var objectId in _objectIds)
      {
        var geometry = _doc.Objects.FindId(objectId)?.Geometry;
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
      foreach (var objectId in _objectIds)
      {
        var geometry = _doc.Objects.FindId(objectId)?.Geometry;
        switch (geometry)
        {
          case Curve curve:
            DrawObjectHighlightCurve(e.Display, curve);
            break;
          case Brep brep:
            DrawObjectHighlightBrep(e.Display, brep);
            break;
          case Extrusion extrusion:
          {
            using var brep = extrusion.ToBrep();
            if (brep != null)
              DrawObjectHighlightBrep(e.Display, brep);
            break;
          }
          case Surface surface:
          {
            using var brep = surface.ToBrep();
            if (brep != null)
              DrawObjectHighlightBrep(e.Display, brep);
            break;
          }
          case Mesh mesh:
            e.Display.DrawMeshWires(
              mesh,
              ObjectHighlightOutlineColor,
              Thickness(e.Display, ObjectHighlightOutlineEmphasis));
            e.Display.DrawMeshWires(
              mesh,
              ObjectHighlightColor,
              Thickness(e.Display, ObjectHighlightStrokeEmphasis));
            break;
          case SubD subD:
            e.Display.DrawSubDWires(
              subD,
              ObjectHighlightOutlineColor,
              ObjectHighlightSubDOutlineWidth);
            e.Display.DrawSubDWires(
              subD,
              ObjectHighlightColor,
              ObjectHighlightSubDStrokeWidth);
            break;
          case Rhino.Geometry.Point point:
            e.Display.DrawPoint(
              point.Location,
              PointStyle.RoundSimple,
              ObjectHighlightPointSize + ObjectHighlightPointOutlineExtra,
              ObjectHighlightOutlineColor);
            e.Display.DrawPoint(
              point.Location,
              PointStyle.RoundSimple,
              ObjectHighlightPointSize,
              ObjectHighlightColor);
            break;
          case PointCloud pointCloud:
            e.Display.DrawPointCloud(
              pointCloud,
              ObjectHighlightPointSize + ObjectHighlightPointOutlineExtra,
              ObjectHighlightOutlineColor);
            e.Display.DrawPointCloud(
              pointCloud,
              ObjectHighlightPointSize,
              ObjectHighlightColor);
            break;
          case TextEntity text:
            e.Display.DrawText(text, ObjectHighlightColor);
            break;
          case AnnotationBase annotation:
            e.Display.DrawAnnotation(annotation, ObjectHighlightColor);
            break;
          case TextDot dot:
            e.Display.DrawDot(
              dot,
              ObjectHighlightColor,
              ObjectHighlightDotBackground,
              ObjectHighlightOutlineColor);
            break;
          case Hatch hatch:
            e.Display.DrawHatch(hatch, ObjectHighlightColor, ObjectHighlightOutlineColor);
            break;
          case Light light:
            e.Display.DrawLight(light, ObjectHighlightColor);
            break;
          case { } other:
            e.Display.DrawBox(
              other.GetBoundingBox(true),
              ObjectHighlightColor,
              Thickness(e.Display, ObjectHighlightStrokeEmphasis));
            break;
        }
      }
    }

    public void Dispose()
    {
      Enabled = false;
      _objectIds.Clear();
      _doc.Views.Redraw();
    }
  }
}
