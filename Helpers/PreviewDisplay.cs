using System;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace vTools;

internal static class PreviewDisplay
{
  // Thickness values are pixel increments above Rhino's current default curve thickness.
  private const int MinimumCurveThickness = 1; // Minimum preview stroke width in display pixels; one or greater.

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
}
