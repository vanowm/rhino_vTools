using System;
using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace vTools;

internal static class PreviewDisplay
{
  private readonly record struct CurveHighlightStyle(
    Color StrokeColor,
    int StrokeEmphasis,
    Color OutlineColor,
    int OutlineEmphasis);

  private static readonly CurveHighlightStyle AddedStyle = new(
    StrokeColor: Color.LimeGreen,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3);

  private static readonly CurveHighlightStyle RemovedStyle = new(
    StrokeColor: Color.Red,
    StrokeEmphasis: 1,
    OutlineColor: Color.Black,
    OutlineEmphasis: 3);

  public static int Thickness(DisplayPipeline display, int emphasis = 0) =>
    Math.Max(1, display.DefaultCurveThickness + emphasis);

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
    int emphasis = 1,
    int outlineExtra = 2)
  {
    display.DrawCurve(
      curve,
      Color.Black,
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

  public static void DrawAddedPoint(DisplayPipeline display, Point3d point)
  {
    var size = Math.Max(4, Thickness(display, 2));
    display.DrawPoint(
      point,
      PointStyle.RoundSimple,
      size + 2,
      AddedStyle.OutlineColor);
    display.DrawPoint(
      point,
      PointStyle.RoundSimple,
      size,
      AddedStyle.StrokeColor);
  }

  public static void DrawRemovedCurve(DisplayPipeline display, Curve curve) =>
    DrawHighlightCurve(display, curve, RemovedStyle);
}
