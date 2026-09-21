using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Draws an argyle diamond pattern on PLOT, a bounding rectangle on CUT1,
/// and a size label on Reference.  Ported from Diamonds.py.
/// </summary>
public sealed class vDiamonds : vToolsCommand
{
  private const string SettingsSection = "vDiamonds";
  private const string WidthKey        = "width";
  private const string HeightKey       = "height";
  private const string CountWidthKey   = "countWidth";
  private const string CountHeightKey  = "countHeight";
  private const string ShowBoundaryKey = "showBoundary";
  private const string ShowSizeKey     = "showSize";
  private const string ShowCountKey    = "showCount";
  private const string LabelInsideKey  = "labelInside";
  private const string BySizeWKey      = "bySizeW";
  private const string BySizeHKey      = "bySizeH";
  private const string RoundToDiamondKey = "roundToDiamond";
  private const string LayerPlotKey    = "layerPlot";
  private const string LayerCutKey     = "layerCut";
  private const string LayerRefKey     = "layerRef";

  // Option defaults
  private const string DefaultLayerPlot = "PLOT"; // Rhino layer name or full layer path.
  private const string DefaultLayerCut = "CUT1"; // Rhino layer name or full layer path.
  private const string DefaultLayerReference = "Reference"; // Rhino layer name or full layer path.
  private const double DefaultWidth  = 2.0; // Diamond width in model units; greater than zero.
  private const double DefaultHeight = 2.0; // Diamond height in model units; greater than zero.
  private const double DefaultCW     = 3.0; // Number of diamonds across; one or greater.
  private const double DefaultCH     = 3.0; // Number of diamonds high; one or greater.
  private const bool DefaultShowBoundary = true; // true creates the bounding rectangle; false omits it.
  private const bool DefaultShowSize = true; // true creates the size label; false omits it.
  private const bool DefaultShowCount = true; // true includes diamond counts in the label; false omits them.
  private const LabelInsideMode DefaultLabelInside = LabelInsideMode.No; // FitAll fits the stack within both padded dimensions; Fit fits padded width and bottom-aligns; No places labels above.
  private const double DefaultBySizeWidth = 0.0; // Boundary width in model units; zero means unset.
  private const double DefaultBySizeHeight = 0.0; // Boundary height in model units; zero means unset.
  private const DiamondRoundingMode DefaultRoundToDiamond = DiamondRoundingMode.None; // Full rounds upward by whole diamonds; Half rounds upward by half diamonds; None preserves the requested boundary.

  private static string _layerPlot = DefaultLayerPlot;
  private static string _layerCut  = DefaultLayerCut;
  private static string _layerRef  = DefaultLayerReference;

  private static readonly Color PlotColor = Color.FromArgb(0x0F, 0x8A, 0x8A); // Color assigned when the PLOT layer is created.
  private static readonly Color CutColor  = Color.FromArgb(0xCC, 0x33, 0x33); // Color assigned when the CUT1 layer is created.
  private static readonly Color RefColor  = Color.White; // Color assigned when the Reference layer is created.

  private const double LabelGap = 0.125; // Minimum gap from the boundary to the label stack in model units; zero or greater.
  private const double LabelLineGapFactor = 0.2; // Gap between stacked annotations as a fraction of the upper label's text height; zero or greater.
  private const double DetailsLineGapFactor = 0.2; // Gap between the grid-count and boundary-size annotations as a fraction of their shared text height; zero or greater.
  private const double DetailsTextWidthFactor = 0.8; // Boundary-width share available to the two-row details text before its parenthesis curves are added; greater than zero and less than one.
  private const double ParenthesisHorizontalGapFactor = 0.12; // Horizontal gap from details text to each parenthesis as a fraction of text height; zero or greater.
  private const double ParenthesisVerticalPaddingFactor = 0.08; // Vertical extension beyond the details text as a fraction of text height; zero or greater.
  private const double ParenthesisWidthFactor = 0.12; // Parenthesis outward bow as a fraction of the full parenthesis height; greater than zero.
  private const double ParenthesisControlHeightFactor = 0.22; // Cubic control-point inset from each parenthesis end as a fraction of parenthesis height; from zero through 0.5.
  private const double InsideLabelPaddingFraction = 0.1; // Empty inset on each boundary side when LabelInside is enabled; from zero through less than 0.5.
  private const double RoundUpRatioTolerance = 1e-10; // Dimensionless tolerance subtracted before ceiling a diamond-count increment; small positive value.
  private static readonly string[] RoundToDiamondNames = ["Full", "Half", "None"]; // Command-line values in DiamondRoundingMode numeric order.
  private static readonly string[] LabelInsideNames = ["FitAll", "Fit", "No"]; // Command-line values in LabelInsideMode numeric order.

  private enum DiamondRoundingMode
  {
    Full,
    Half,
    None
  }

  private enum LabelInsideMode
  {
    FitAll,
    Fit,
    No
  }

  private static double _width        = DefaultWidth;
  private static double _height       = DefaultHeight;
  private static double _cw           = DefaultCW;
  private static double _ch           = DefaultCH;
  private static bool   _showBoundary = DefaultShowBoundary;
  private static bool   _showSize     = DefaultShowSize;
  private static bool   _showCount    = DefaultShowCount;
  private static LabelInsideMode _labelInside = DefaultLabelInside;
  private static double _bySizeW      = DefaultBySizeWidth; // stored dims (always positive when ever set)
  private static double _bySizeH      = DefaultBySizeHeight;
  private static DiamondRoundingMode _roundToDiamond = DefaultRoundToDiamond;
  private static bool   _bySizeActive = false; // transient: true only during the current invocation

  public override string EnglishName => "vDiamonds";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadSettings();
    _bySizeActive = false;  // never carry over across command invocations
    EnsureLayer(doc, _layerPlot, PlotColor);
    EnsureLayer(doc, _layerCut,  CutColor);
    EnsureLayer(doc, _layerRef,  RefColor);
    var lastPlacementPoint = Point3d.Unset;

    while (true)
    {
      // Effective geometry parameters
      double W, H, patOffX, patOffY, byCW, byCH;
      if (_bySizeActive && _bySizeW > 0.0 && _bySizeH > 0.0)
      {
        if (_roundToDiamond != DiamondRoundingMode.None)
        {
          var countIncrement = _roundToDiamond == DiamondRoundingMode.Half
            ? 0.5
            : 1.0;
          byCW = DiamondCountAtLeast(_bySizeW, _width, countIncrement);
          byCH = DiamondCountAtLeast(_bySizeH, _height, countIncrement);
          W = byCW * _width;
          H = byCH * _height;
          patOffX = patOffY = 0.0;
        }
        else
        {
          W       = _bySizeW;
          H       = _bySizeH;
          byCW    = Math.Max(1.0, Math.Floor(W / _width));
          byCH    = Math.Max(1.0, Math.Floor(H / _height));
          patOffX = (W - byCW * _width)  / 2.0;
          patOffY = (H - byCH * _height) / 2.0;
        }
      }
      else
      {
        byCW = _cw; byCH = _ch;
        W = byCW * _width; H = byCH * _height;
        patOffX = patOffY = 0.0;
      }

      // Build diamond pattern curves; in BySize mode the grid is centered and all
      // lines are clipped to the full bbox (extending to touch all four edges).
      var (plotCurves, _, sizeLabelTe, _) = BuildGeometry(
        _width, _height, byCW, byCH,
        patOffX, patOffY,
        _bySizeActive ? W : 0.0,
        _bySizeActive ? H : 0.0);

      // Full bbox CUT1 curve (may be larger than pattern area in bySize mode)
      var cutCurve = new PolylineCurve(new[]
      {
        new Point3d(0, 0, 0), new Point3d(W, 0, 0),
        new Point3d(W, H, 0), new Point3d(0, H, 0),
        new Point3d(0, 0, 0),
      });

      sizeLabelTe.Plane = Plane.WorldXY;
      CalibrateTextHeight(sizeLabelTe, W);

      // Build one two-row details block: grid count above final boundary size.
      // Parentheses are separate curves so both rows share one continuous pair.
      TextEntity? countLabelTe = null;
      TextEntity? boundaryLabelTe = null;
      var detailParenthesisCurves = new List<NurbsCurve>();
      if (_showCount)
      {
        countLabelTe = new TextEntity
        {
          Plane = Plane.WorldXY,
          PlainText = $"{FmtFrac(byCW)} x {FmtFrac(byCH)}",
          TextHeight = sizeLabelTe.TextHeight,
          Justification = TextJustification.BottomCenter,
        };
        boundaryLabelTe = new TextEntity
        {
          Plane = Plane.WorldXY,
          PlainText = $"{FmtFrac(W)} x {FmtFrac(H)}",
          TextHeight = sizeLabelTe.TextHeight,
          Justification = TextJustification.BottomCenter,
        };
        CalibrateSharedTextHeight(
          W * DetailsTextWidthFactor,
          countLabelTe,
          boundaryLabelTe);
      }

      // Use rendered bounds so the parenthesis descenders clear the boundary,
      // then place the diamond-size line above the two-row details block.
      if (countLabelTe != null && boundaryLabelTe != null)
      {
        var parenthesisPadding =
          countLabelTe.TextHeight * ParenthesisVerticalPaddingFactor;
        var boundaryTop = PlaceTextAbove(
          boundaryLabelTe,
          W / 2.0,
          H + LabelGap + parenthesisPadding);
        PlaceTextAbove(
          countLabelTe,
          W / 2.0,
          boundaryTop + countLabelTe.TextHeight * DetailsLineGapFactor);
        detailParenthesisCurves = BuildDetailParentheses(
          countLabelTe,
          boundaryLabelTe,
          out var parenthesisTop);
        if (_showSize)
        {
          PlaceTextAbove(
            sizeLabelTe,
            W / 2.0,
            parenthesisTop + sizeLabelTe.TextHeight * LabelLineGapFactor);
        }
      }
      else if (_showSize)
      {
        PlaceTextAbove(sizeLabelTe, W / 2.0, H + LabelGap);
      }

      if (_labelInside != LabelInsideMode.No)
      {
        FitAnnotationsInside(
          W,
          H,
          _labelInside,
          _showSize ? sizeLabelTe : null,
          countLabelTe,
          boundaryLabelTe,
          detailParenthesisCurves);
      }

      // Print bbox size to command history
      var bySizeMode = _roundToDiamond switch
      {
        DiamondRoundingMode.Full => "full-rounded",
        DiamondRoundingMode.Half => "half-rounded",
        _ => "centered"
      };
      var bySizeNote = _bySizeActive
        ? $"  ({bySizeMode} {FmtFrac(byCW)} x {FmtFrac(byCH)} diamonds)"
        : "";
      RhinoApp.WriteLine($"Boundary box: {FmtFrac(W)} x {FmtFrac(H)}{bySizeNote}");

      var fadedPlot = FadeColor(LayerColor(doc, _layerPlot));
      var fadedCut  = FadeColor(LayerColor(doc, _layerCut));
      var fadedRef  = FadeColor(LayerColor(doc, _layerRef));

      var previewItems = new List<(GeometryBase Geom, Color Color)>();
      foreach (var crv in plotCurves)
        previewItems.Add((crv.DuplicateCurve(), fadedPlot));
      if (_showBoundary)
        previewItems.Add((cutCurve.DuplicateCurve(), fadedCut));
      if (_showSize)
        previewItems.Add((sizeLabelTe.Duplicate(), fadedRef));
      if (countLabelTe != null)
        previewItems.Add((countLabelTe.Duplicate(), fadedRef));
      if (boundaryLabelTe != null)
        previewItems.Add((boundaryLabelTe.Duplicate(), fadedRef));
      foreach (var parenthesis in detailParenthesisCurves)
        previewItems.Add((parenthesis.DuplicateCurve(), fadedRef));

      var capturedBase  = new Point3d(0.0, H, 0.0);
      var capturedItems = previewItems;

      EventHandler<GetPointDrawEventArgs> onDraw = (_, e) =>
      {
        lastPlacementPoint = e.CurrentPoint;
        var xform = Transform.Translation(e.CurrentPoint - capturedBase);
        foreach (var (geom, color) in capturedItems)
        {
          var g = geom.Duplicate();
          if (g == null) continue;
          g.Transform(xform);
          if (g is Curve c)
            PreviewDisplay.DrawCurve(e.Display, c, color);
          else if (g is AnnotationBase ann)
            e.Display.DrawAnnotation(ann, color);
        }
      };

      var togBoundary = new OptionToggle(_showBoundary, "No", "Yes");
      var togSize     = new OptionToggle(_showSize,     "No", "Yes");
      var togCount    = new OptionToggle(_showCount,    "No", "Yes");

      var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt("Pick diamond pattern placement point");
      gp.AcceptString(true);
      gp.AcceptNothing(true);
      var idxW        = gp.AddOption("Width",       FmtOpt(_width));
      var idxH        = gp.AddOption("Height",      FmtOpt(_height));
      var idxCW       = gp.AddOption("CountWidth",  FmtOpt(_cw));
      var idxCH       = gp.AddOption("CountHeight", FmtOpt(_ch));
      var idxBySize   = _bySizeActive
        ? gp.AddOption("BySize", $"{FmtOpt(_bySizeW)}x{FmtOpt(_bySizeH)}")
        : gp.AddOption("BySize");
      var idxBoundary = gp.AddOptionToggle("Boundary", ref togBoundary);
      var idxSize     = gp.AddOptionToggle("Size",     ref togSize);
      var idxCount    = gp.AddOptionToggle("Count",    ref togCount);
      var idxLabelInside = gp.AddOptionList(
        "LabelInside",
        LabelInsideNames,
        (int)_labelInside);

      gp.DynamicDraw += onDraw;
      var result = gp.Get();
      gp.DynamicDraw -= onDraw;

      if (result == GetResult.Cancel)
        return Result.Cancel;

      // Direct string input at placement: "widthxheight" sets diamond size
      if (result == GetResult.String)
      {
        var raw = gp.StringResult().Trim();
        var xi  = raw.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (xi > 0)
        {
          var a = ParseFrac(raw[..xi]);
          var b = ParseFrac(raw[(xi + 1)..]);
          if (a.HasValue && b.HasValue)
          {
            if (a.Value > 0.0) _width  = a.Value;
            if (b.Value > 0.0) _height = b.Value;
          }
        }
        else
        {
          var v = ParseFrac(raw);
          if (v.HasValue && v.Value > 0.0) _width = v.Value;
        }
        SaveSettings();
        continue;
      }

      if (result == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt == null) continue;

        if (opt.Index == idxW)
        {
          var v = GetDoubleSubprompt("Diamond width", _width);
          if (v == null) return Result.Cancel;
          if (v.Value > 0.0) _width = v.Value;
        }
        else if (opt.Index == idxH)
        {
          var v = GetDoubleSubprompt("Diamond height", _height);
          if (v == null) return Result.Cancel;
          if (v.Value > 0.0) _height = v.Value;
        }
        else if (opt.Index == idxCW)
        {
          var v = GetDoubleSubprompt("Number of diamonds wide", _cw);
          if (v == null) return Result.Cancel;
          if (v.Value >= 1.0) _cw = v.Value;
        }
        else if (opt.Index == idxCH)
        {
          var v = GetDoubleSubprompt("Number of diamonds tall", _ch);
          if (v == null) return Result.Cancel;
          if (v.Value >= 1.0) _ch = v.Value;
        }
        else if (opt.Index == idxBySize)
        {
          double curBsW = _bySizeW > 0.0 ? _bySizeW : W;
          double curBsH = _bySizeH > 0.0 ? _bySizeH : H;
          var v = GetBySizeSubprompt(
            "Boundary box size (0 to deactivate)",
            curBsW,
            curBsH,
            _roundToDiamond);
          if (v == null) return Result.Cancel;
          if (v.Value.A <= 0.0 || v.Value.B <= 0.0)
            _bySizeActive = false;  // deactivate only; stored dims are preserved
          else
          {
            _bySizeW = v.Value.A; _bySizeH = v.Value.B;
            _bySizeActive = true;
          }
          _roundToDiamond = v.Value.RoundToDiamond;
        }
        else if (opt.Index == idxBoundary) _showBoundary = togBoundary.CurrentValue;
        else if (opt.Index == idxSize)     _showSize     = togSize.CurrentValue;
        else if (opt.Index == idxCount)    _showCount    = togCount.CurrentValue;
        else if (opt.Index == idxLabelInside)
        {
          var selectedIndex = opt.CurrentListOptionIndex;
          selectedIndex = Math.Max(
            0,
            Math.Min(LabelInsideNames.Length - 1, selectedIndex));
          _labelInside = (LabelInsideMode)selectedIndex;
        }

        SaveSettings();
        continue;
      }

      if (result is GetResult.Point or GetResult.Nothing)
      {
        var placementPoint = result == GetResult.Point
          ? gp.Point()
          : lastPlacementPoint;
        if (!placementPoint.IsValid)
          placementPoint = capturedBase;
        var xform = Transform.Translation(placementPoint - capturedBase);
        AddToDoc(doc, plotCurves,
                 _showBoundary ? cutCurve    : null,
                 _showSize     ? sizeLabelTe : null,
                 countLabelTe,
                 boundaryLabelTe,
                 detailParenthesisCurves,
                 xform, _width, _height, byCW, byCH);
        _bySizeActive = false;  // BySize is one-time; deactivate after placement (dims preserved)
        SaveSettings();
        doc.Views.Redraw();
        return Result.Success;
      }
    }
  }

  // ── Geometry ────────────────────────────────────────────────────────────────

  private static (List<NurbsCurve> PlotCurves, PolylineCurve CutCurve, TextEntity SizeLabelTe, Point3d BasePt)
    BuildGeometry(double width, double height, double cw, double ch,
                  double offsetX = 0.0, double offsetY = 0.0,
                  double clipW = 0.0, double clipH = 0.0)
  {
    // clipW/clipH > 0: clip lines to this full bbox (BySize mode).
    // offsetX/offsetY: shift the diamond grid origin within that bbox.
    double innerH = ch * height;
    double W = clipW > 0.0 ? clipW : cw * width;
    double H = clipH > 0.0 ? clipH : innerH;
    double s = height / width;

    int cwCeil = (int)Math.Ceiling(cw);
    int chCeil = (int)Math.Ceiling(ch);
    // Widen the loop range in BySize mode so lines reach all four bbox edges.
    int extra = clipW > 0.0 ? 2 : 0;

    var plotCurves = new List<NurbsCurve>();

    // ↗ family (slope +s): y = s*x + b_n, intercept adjusted for grid offset
    for (int n = -extra; n < cwCeil + chCeil + extra; n++)
    {
      double b        = innerH - ((n + 0.5) * height);
      double bShifted = b + offsetY - s * offsetX;
      var r = ClipLineToBbox(s, bShifted, W, H);
      if (r.HasValue)
        plotCurves.Add(new Line(r.Value.A, r.Value.B).ToNurbsCurve());
    }

    // ↘ family (slope -s): y = -s*x + c_k, intercept adjusted for grid offset
    for (int k = -chCeil - extra; k < cwCeil + extra; k++)
    {
      double c        = innerH + ((k + 0.5) * height);
      double cShifted = c + offsetY + s * offsetX;
      var r = ClipLineToBbox(-s, cShifted, W, H);
      if (r.HasValue)
        plotCurves.Add(new Line(r.Value.A, r.Value.B).ToNurbsCurve());
    }

    var corners = new List<Point3d>
    {
      new(0, 0, 0), new(W, 0, 0),
      new(W, H, 0), new(0, H, 0),
      new(0, 0, 0),
    };
    var cutCurve = new PolylineCurve(corners);

    var labelText = $"{FmtFrac(width)} x {FmtFrac(height)}";
    var textHeight = W / 10.0;
    var origin     = new Point3d(W / 2.0, H + LabelGap, 0.0);
    var labelPlane = new Plane(origin, Vector3d.XAxis, Vector3d.YAxis);
    var sizeLabelTe = new TextEntity
    {
      Plane = labelPlane,
      PlainText = labelText,
      TextHeight = textHeight,
      Justification = TextJustification.BottomCenter,
    };

    return (plotCurves, cutCurve, sizeLabelTe, new Point3d(0.0, H, 0.0));
  }

  private static (Point3d A, Point3d B)? ClipLineToBbox(double slope, double intercept, double W, double H)
  {
    const double tol = 1e-9;
    var pts = new List<Point3d>();

    // x = 0
    double y = intercept;
    if (y >= -tol && y <= H + tol)
      pts.Add(new Point3d(0.0, Math.Max(0.0, Math.Min(H, y)), 0.0));

    // x = W
    y = (slope * W) + intercept;
    if (y >= -tol && y <= H + tol)
      pts.Add(new Point3d(W, Math.Max(0.0, Math.Min(H, y)), 0.0));

    if (Math.Abs(slope) > tol)
    {
      // y = 0 (strict interior in x)
      double x = -intercept / slope;
      if (x > tol && x < W - tol)
        pts.Add(new Point3d(x, 0.0, 0.0));

      // y = H (strict interior in x)
      x = (H - intercept) / slope;
      if (x > tol && x < W - tol)
        pts.Add(new Point3d(x, H, 0.0));
    }

    var unique = new List<Point3d>();
    foreach (var p in pts)
    {
      if (!unique.Exists(u => p.DistanceTo(u) < 1e-6))
        unique.Add(p);
    }

    if (unique.Count < 2 || unique[0].DistanceTo(unique[1]) < 1e-6)
      return null;

    return (unique[0], unique[1]);
  }

  private static void CalibrateTextHeight(TextEntity text, double targetWidth)
  {
    var textWidth = text.TextModelWidth;
    if (textWidth > 0.0)
      text.TextHeight *= targetWidth / textWidth;
  }

  private static void CalibrateSharedTextHeight(
    double targetWidth,
    params TextEntity[] labels)
  {
    var widestText = 0.0;
    foreach (var label in labels)
      widestText = Math.Max(widestText, label.TextModelWidth);

    if (widestText <= 0.0)
      return;

    var scale = targetWidth / widestText;
    foreach (var label in labels)
      label.TextHeight *= scale;
  }

  private static double PlaceTextAbove(
    TextEntity text,
    double centerX,
    double minimumY)
  {
    text.Plane = Plane.WorldXY;
    var bounds = text.GetBoundingBox(true);
    var minOffsetY = bounds.IsValid ? bounds.Min.Y : 0.0;
    var maxOffsetY = bounds.IsValid ? bounds.Max.Y : text.TextHeight;
    var originY = minimumY - minOffsetY;
    text.Plane = new Plane(
      new Point3d(centerX, originY, 0.0),
      Vector3d.XAxis,
      Vector3d.YAxis);
    return originY + maxOffsetY;
  }

  private static List<NurbsCurve> BuildDetailParentheses(
    TextEntity upperText,
    TextEntity lowerText,
    out double topY)
  {
    var upperBounds = upperText.GetBoundingBox(true);
    var lowerBounds = lowerText.GetBoundingBox(true);
    if (!upperBounds.IsValid || !lowerBounds.IsValid)
    {
      topY = upperText.Plane.OriginY + upperText.TextHeight;
      return [];
    }

    var verticalPadding =
      upperText.TextHeight * ParenthesisVerticalPaddingFactor;
    var top = Math.Max(upperBounds.Max.Y, lowerBounds.Max.Y) + verticalPadding;
    var bottom = Math.Min(upperBounds.Min.Y, lowerBounds.Min.Y) - verticalPadding;
    var height = Math.Max(upperText.TextHeight, top - bottom);
    var horizontalGap =
      upperText.TextHeight * ParenthesisHorizontalGapFactor;
    var bow = height * ParenthesisWidthFactor;
    var controlInset = height * ParenthesisControlHeightFactor;
    var leftInner = Math.Min(upperBounds.Min.X, lowerBounds.Min.X) - horizontalGap;
    var rightInner = Math.Max(upperBounds.Max.X, lowerBounds.Max.X) + horizontalGap;

    using var left = new BezierCurve(
      new[]
      {
        new Point3d(leftInner, top, 0.0),
        new Point3d(leftInner - bow, top - controlInset, 0.0),
        new Point3d(leftInner - bow, bottom + controlInset, 0.0),
        new Point3d(leftInner, bottom, 0.0),
      });
    using var right = new BezierCurve(
      new[]
      {
        new Point3d(rightInner, top, 0.0),
        new Point3d(rightInner + bow, top - controlInset, 0.0),
        new Point3d(rightInner + bow, bottom + controlInset, 0.0),
        new Point3d(rightInner, bottom, 0.0),
      });

    topY = top;
    return [left.ToNurbsCurve(), right.ToNurbsCurve()];
  }

  private static void FitAnnotationsInside(
    double boundaryWidth,
    double boundaryHeight,
    LabelInsideMode mode,
    TextEntity? sizeLabel,
    TextEntity? countLabel,
    TextEntity? boundaryLabel,
    IReadOnlyList<NurbsCurve> parentheses)
  {
    var annotations = new List<GeometryBase>();
    if (sizeLabel != null)
      annotations.Add(sizeLabel);
    if (countLabel != null)
      annotations.Add(countLabel);
    if (boundaryLabel != null)
      annotations.Add(boundaryLabel);
    foreach (var parenthesis in parentheses)
      annotations.Add(parenthesis);

    var bounds = BoundingBox.Empty;
    foreach (var annotation in annotations)
      bounds.Union(annotation.GetBoundingBox(true));
    if (!bounds.IsValid)
      return;

    var availableWidth = boundaryWidth * (1.0 - 2.0 * InsideLabelPaddingFraction);
    var availableHeight = boundaryHeight * (1.0 - 2.0 * InsideLabelPaddingFraction);
    var boundsWidth = bounds.Max.X - bounds.Min.X;
    var boundsHeight = bounds.Max.Y - bounds.Min.Y;
    if (availableWidth <= RhinoMath.ZeroTolerance ||
        availableHeight <= RhinoMath.ZeroTolerance ||
        boundsWidth <= RhinoMath.ZeroTolerance ||
        boundsHeight <= RhinoMath.ZeroTolerance)
      return;

    var scale = mode == LabelInsideMode.FitAll
      ? Math.Min(
          availableWidth / boundsWidth,
          availableHeight / boundsHeight)
      : availableWidth / boundsWidth;
    var sourceAnchor = mode == LabelInsideMode.FitAll
      ? bounds.Center
      : new Point3d(bounds.Center.X, bounds.Min.Y, bounds.Center.Z);
    var targetAnchor = mode == LabelInsideMode.FitAll
      ? new Point3d(boundaryWidth * 0.5, boundaryHeight * 0.5, 0.0)
      : new Point3d(
          boundaryWidth * 0.5,
          boundaryHeight * InsideLabelPaddingFraction,
          0.0);
    var moveToOrigin = Transform.Translation(Point3d.Origin - sourceAnchor);
    var resize = Transform.Scale(Point3d.Origin, scale);
    var moveInside = Transform.Translation(targetAnchor - Point3d.Origin);

    foreach (var annotation in annotations)
    {
      annotation.Transform(moveToOrigin);
      annotation.Transform(resize);
      annotation.Transform(moveInside);
    }
  }

  private static void AddToDoc(
    RhinoDoc doc,
    List<NurbsCurve> plotCurves,
    PolylineCurve? cutCurve,
    TextEntity? sizeLabelTe,
    TextEntity? countLabelTe,
    TextEntity? boundaryLabelTe,
    IReadOnlyList<NurbsCurve> detailParenthesisCurves,
    Transform xform,
    double width, double height, double cw, double ch)
  {
    var plotAttr = new ObjectAttributes { LayerIndex = EnsureLayer(doc, _layerPlot, PlotColor) };
    var cutAttr  = new ObjectAttributes { LayerIndex = EnsureLayer(doc, _layerCut,  CutColor)  };
    var refAttr  = new ObjectAttributes { LayerIndex = EnsureLayer(doc, _layerRef,  RefColor)  };

    var addedIds = new List<Guid>();

    foreach (var crv in plotCurves)
    {
      var c = crv.DuplicateCurve();
      c.Transform(xform);
      var id = doc.Objects.AddCurve(c, plotAttr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    if (cutCurve != null)
    {
      var cut = cutCurve.DuplicateCurve();
      cut.Transform(xform);
      var cutId = doc.Objects.AddCurve(cut, cutAttr);
      if (cutId != Guid.Empty) addedIds.Add(cutId);
    }

    foreach (var text in new[] { sizeLabelTe, countLabelTe, boundaryLabelTe })
    {
      if (text?.Duplicate() is not TextEntity copy)
        continue;

      copy.Transform(xform);
      var textId = doc.Objects.AddText(copy, refAttr);
      if (textId != Guid.Empty)
        addedIds.Add(textId);
    }

    foreach (var source in detailParenthesisCurves)
    {
      var curve = source.DuplicateCurve();
      curve.Transform(xform);
      var curveId = doc.Objects.AddCurve(curve, refAttr);
      if (curveId != Guid.Empty) addedIds.Add(curveId);
    }

    if (addedIds.Count > 1)
    {
      var shortId   = Guid.NewGuid().ToString()[..8];
      var groupName = $"Diamonds_{FmtFrac(width)}x{FmtFrac(height)}_({FmtFrac(cw)}x{FmtFrac(ch)})_{shortId}";
      var groupIdx  = doc.Groups.Add(groupName);
      foreach (var id in addedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        var a = obj.Attributes.Duplicate();
        a.AddToGroup(groupIdx);
        doc.Objects.ModifyAttributes(id, a, false);
      }
    }
  }

  // ── Settings ─────────────────────────────────────────────────────────────

  private static void LoadSettings()
  {
    (_width, _height, _cw, _ch, _showBoundary, _showSize, _showCount, _labelInside, _bySizeW, _bySizeH, _roundToDiamond, _layerPlot, _layerCut, _layerRef) = ToolsOptionStore.Read(SettingsSection, section =>
    {
      var w   = _width;
      var h   = _height;
      var cw  = _cw;
      var ch  = _ch;
      var sb  = _showBoundary;
      var ss  = _showSize;
      var sc  = _showCount;
      var labelInside = _labelInside;
      var bsW = _bySizeW;
      var bsH = _bySizeH;
      var roundToDiamond = _roundToDiamond;
      var lp  = _layerPlot;
      var lc  = _layerCut;
      var lr  = _layerRef;

      if (ToolsOptionStore.TryGetDouble(section, WidthKey,       out var pw)  && pw  > 0.0) w   = pw;
      if (ToolsOptionStore.TryGetDouble(section, HeightKey,      out var ph)  && ph  > 0.0) h   = ph;
      if (ToolsOptionStore.TryGetDouble(section, CountWidthKey,  out var pcw) && pcw >= 1.0) cw  = pcw;
      if (ToolsOptionStore.TryGetDouble(section, CountHeightKey, out var pch) && pch >= 1.0) ch  = pch;
      if (ToolsOptionStore.TryGetBool(section, ShowBoundaryKey, out var psb)) sb  = psb;
      if (ToolsOptionStore.TryGetBool(section, ShowSizeKey,     out var pss)) ss  = pss;
      if (ToolsOptionStore.TryGetBool(section, ShowCountKey,    out var psc)) sc  = psc;
      if (ToolsOptionStore.TryGetString(section, LabelInsideKey, out var pli) &&
          Enum.TryParse(pli, true, out LabelInsideMode parsedLabelInside))
      {
        labelInside = parsedLabelInside;
      }
      else if (ToolsOptionStore.TryGetBool(section, LabelInsideKey, out var legacyLabelInside))
      {
        labelInside = legacyLabelInside
          ? LabelInsideMode.FitAll
          : LabelInsideMode.No;
      }
      if (ToolsOptionStore.TryGetDouble(section, BySizeWKey,    out var pbsW) && pbsW > 0.0) bsW = pbsW;
      if (ToolsOptionStore.TryGetDouble(section, BySizeHKey,    out var pbsH) && pbsH > 0.0) bsH = pbsH;
      if (ToolsOptionStore.TryGetString(section, RoundToDiamondKey, out var prtd) &&
          Enum.TryParse(prtd, true, out DiamondRoundingMode parsedRounding))
      {
        roundToDiamond = parsedRounding;
      }
      else if (ToolsOptionStore.TryGetBool(section, RoundToDiamondKey, out var legacyRounding))
      {
        roundToDiamond = legacyRounding
          ? DiamondRoundingMode.Full
          : DiamondRoundingMode.None;
      }
      if (ToolsOptionStore.TryGetString(section, LayerPlotKey,  out var plp) && !string.IsNullOrWhiteSpace(plp)) lp = plp;
      if (ToolsOptionStore.TryGetString(section, LayerCutKey,   out var plc) && !string.IsNullOrWhiteSpace(plc)) lc = plc;
      if (ToolsOptionStore.TryGetString(section, LayerRefKey,   out var plr) && !string.IsNullOrWhiteSpace(plr)) lr = plr;

      return (w, h, cw, ch, sb, ss, sc, labelInside, bsW, bsH, roundToDiamond, lp, lc, lr);
    });
  }

  private static void SaveSettings() =>
    ToolsOptionStore.Update(SettingsSection, section =>
    {
      section[WidthKey]        = _width;
      section[HeightKey]       = _height;
      section[CountWidthKey]   = _cw;
      section[CountHeightKey]  = _ch;
      section[ShowBoundaryKey] = _showBoundary;
      section[ShowSizeKey]     = _showSize;
      section[ShowCountKey]    = _showCount;
      section[LabelInsideKey]  = _labelInside.ToString();
      section[BySizeWKey]      = _bySizeW;
      section[BySizeHKey]      = _bySizeH;
      section[RoundToDiamondKey] = _roundToDiamond.ToString();
      section[LayerPlotKey]    = _layerPlot;
      section[LayerCutKey]     = _layerCut;
      section[LayerRefKey]     = _layerRef;
    });

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static int EnsureLayer(RhinoDoc doc, string name, Color createColor)
  {
    var idx = doc.Layers.FindByFullPath(name, -1);
    if (idx >= 0) return idx;
    var layer = new Layer { Name = name, Color = createColor };
    var added = doc.Layers.Add(layer);
    return added >= 0 ? added : doc.Layers.CurrentLayerIndex;
  }

  private static Color LayerColor(RhinoDoc doc, string name)
  {
    var idx = doc.Layers.FindByFullPath(name, -1);
    return idx >= 0 ? doc.Layers[idx].Color : Color.Gray;
  }

  private static Color FadeColor(Color c) =>
    Color.FromArgb((c.R + 255) / 2, (c.G + 255) / 2, (c.B + 255) / 2);

  private static double? GetDoubleSubprompt(string prompt, double current)
  {
    var gs = new GetString();
    gs.SetCommandPrompt($"{prompt} ({FmtFrac(current)})");
    gs.AcceptNothing(true);
    var res = gs.Get();
    if (res == GetResult.Nothing) return current;
    if (res == GetResult.String)
    {
      var raw = gs.StringResult().Trim();
      if (string.IsNullOrEmpty(raw)) return current;
      var v = ParseFrac(raw);
      return v.HasValue ? v.Value : current;
    }
    return null;
  }

  private static (double A, double B, DiamondRoundingMode RoundToDiamond)? GetBySizeSubprompt(
    string prompt,
    double curA,
    double curB,
    DiamondRoundingMode roundToDiamond)
  {
    while (true)
    {
      var gs = new GetString();
      gs.SetCommandPrompt($"{prompt} ({FmtFrac(curA)} x {FmtFrac(curB)})");
      gs.AcceptNothing(true);
      var roundOption = gs.AddOptionList(
        "RoundToDiamond",
        RoundToDiamondNames,
        (int)roundToDiamond);
      var res = gs.Get();
      if (res == GetResult.Nothing)
        return (curA, curB, roundToDiamond);
      if (res == GetResult.Option && gs.Option()?.Index == roundOption)
      {
        var selectedIndex = gs.Option()?.CurrentListOptionIndex ?? (int)roundToDiamond;
        selectedIndex = Math.Max(
          0,
          Math.Min(RoundToDiamondNames.Length - 1, selectedIndex));
        roundToDiamond = (DiamondRoundingMode)selectedIndex;
        continue;
      }
      if (res == GetResult.String)
      {
        var raw = gs.StringResult().Trim();
        if (string.IsNullOrEmpty(raw))
          return (curA, curB, roundToDiamond);
        var xi = raw.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (xi > 0)
        {
          var a = ParseFrac(raw[..xi]);
          var b = ParseFrac(raw[(xi + 1)..]);
          if (a.HasValue && b.HasValue && a.Value > 0.0 && b.Value > 0.0)
            return (a.Value, b.Value, roundToDiamond);
        }
        var single = ParseFrac(raw);
        if (single.HasValue)
        {
          if (single.Value <= 0.0)
            return (0.0, 0.0, roundToDiamond);  // 0 = deactivate signal
          return (single.Value, curB, roundToDiamond);
        }
        return (curA, curB, roundToDiamond);
      }
      return null;
    }
  }

  private static double DiamondCountAtLeast(
    double minimumBoundarySize,
    double diamondSize,
    double countIncrement)
  {
    var incrementUnits =
      minimumBoundarySize / (diamondSize * countIncrement);
    var count = Math.Max(
      countIncrement,
      Math.Ceiling(incrementUnits - RoundUpRatioTolerance) * countIncrement);

    while (count * diamondSize < minimumBoundarySize - RhinoMath.ZeroTolerance)
      count += countIncrement;

    return count;
  }

  /// <summary>
  /// Parses decimal, fraction (N+P/Q or N-P/Q or P/Q), or plain integer.
  /// Returns null if input cannot be parsed.
  /// </summary>
  private static double? ParseFrac(string s)
  {
    s = s.Trim();
    if (string.IsNullOrEmpty(s)) return null;

    // Plain decimal / integer
    if (double.TryParse(s, System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var d))
      return d;

    // N+P/Q or N-P/Q  (dash must not be leading minus)
    int sep = s.IndexOf('+');
    if (sep < 0) { var di = s.IndexOf('-', 1); if (di > 0) sep = di; }
    if (sep > 0)
    {
      var sl = s.IndexOf('/', sep + 1);
      if (sl > sep + 1 &&
          double.TryParse(s[..sep], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var whole) &&
          double.TryParse(s[(sep + 1)..sl], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var num) &&
          double.TryParse(s[(sl + 1)..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var den) &&
          den != 0)
        return whole + num / den;
    }

    // Pure fraction P/Q
    var psl = s.IndexOf('/');
    if (psl > 0 &&
        double.TryParse(s[..psl], System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var pn) &&
        double.TryParse(s[(psl + 1)..], System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out var pd) &&
        pd != 0)
      return pn / pd;

    return null;
  }


  private static (int Whole, int Num, int Den) ToFraction(double v, int den = 16)
  {
    int whole = (int)v;
    int num   = (int)Math.Round((v - whole) * den);
    if (num >= den) { whole++; num = 0; }
    if (num == 0) return (whole, 0, 1);
    int a = num, b = den;
    while (b != 0) { int t = b; b = a % b; a = t; }
    return (whole, num / a, den / a);
  }

  private static string FmtFrac(double v)
  {
    var (whole, num, den) = ToFraction(v);
    return num == 0 ? whole.ToString() : $"{whole}+{num}/{den}";
  }

  private static string FmtOpt(double v)
  {
    var (whole, num, den) = ToFraction(v);
    return num == 0 ? whole.ToString() : $"{whole}-{num}/{den}";
  }
}
