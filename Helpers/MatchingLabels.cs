using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Geometry;
using vTools.Commands;

namespace vTools;

internal static class MatchingLabels
{
  // Shared notch/seam label defaults
  internal const string DefaultValue = "A"; // Plain alphanumeric sequence value; never includes the display-only orientation dot.
  internal const string DefaultPrefix = ""; // Literal label prefix; empty omits it and trailing spaces separate prefix from value.
  private const double LabelWidthFactor = 0.9; // Positive measured-width factor matching the existing vNotches label clearance.
  internal const double DefaultSize = 0.3; // Nonnegative manual text height in model units.
  internal const string DefaultLayer = "PLOT"; // Label layer path, or the current-layer sentinel.
  internal const bool DefaultAutoAdvance = true; // true advances once per matching set; false reuses the selected value.
  internal const bool DefaultAutoSize = false; // true sizes from notch offset and percentage; false uses manual height.
  internal const int DefaultSizePercent = 75; // Auto-size percentage, from 20 through 100.
  internal const double DefaultNotchOffset = 0.5; // Nonnegative model-unit offset used by notch auto-sizing.
  [global::vTools.CommandDefault("MatchingLabels", "vTools.MatchingLabels.DimensionScale", "Positive annotation scale shared by notch and seam text.")] internal const double DimensionScale = 0.9; // Positive annotation scale shared by notch and seam text.

  internal static readonly double ConfiguredDimensionScale = global::vTools.CommandDefaults.Get("MatchingLabels", "vTools.MatchingLabels.DimensionScale", DimensionScale); // Resolved once from advanced defaults; restart Rhino after changes.
  private const string Section = "vNotches"; // Existing global and document label settings owner.
  private const string DocumentSection = "vTools"; // Existing Rhino document-string section.
  private static readonly Style Defaults = new(
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelValue", DefaultValue),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelSize", DefaultSize),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelLayer", DefaultLayer),
    DefaultAutoAdvance,
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelSizeAuto", DefaultAutoSize),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelSizePercent", DefaultSizePercent),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultNotchOffset", DefaultNotchOffset))
    {Prefix=CommandDefaults.Get(Section,"vTools.Commands.vNotches.DefaultLabelPrefix",DefaultPrefix)}; // Same existing vNotches factory settings.

  internal sealed record Style(string Value, double ManualSize, string Layer, bool AutoAdvance,
    bool AutoSize, int SizePercent, double NotchOffset)
  {
    internal string Prefix {get;init;}=DefaultPrefix; // Literal prefix; only Value advances through the sequence.
    internal double Height => AutoSize ? NotchOffset * SizePercent * 0.01 : ManualSize;
  }

  internal static event Action<RhinoDoc>? Changed;

  internal static string ReadText(JsonObject? settings, string key, string fallback) =>
    settings?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? string.Empty : fallback;

  private static Style Overlay(Style style, JsonObject? settings) => style with
  {
    Value = LabelSequences.NormalizeValue(ReadText(settings, "label_value", style.Value)),
    Prefix = ReadText(settings,"label_prefix",style.Prefix),
    ManualSize = ToolsOptionStore.TryGetDouble(settings, "label_size", out var size) && double.IsFinite(size) && size >= 0 ? size : style.ManualSize,
    Layer = ReadText(settings, "label_layer", style.Layer),
    AutoAdvance = !string.IsNullOrWhiteSpace(ReadText(settings, "label_value", style.Value)),
    AutoSize = ToolsOptionStore.TryGetBool(settings, "label_size_auto", out var auto) ? auto : style.AutoSize,
    SizePercent = ToolsOptionStore.TryGetDouble(settings, "label_size_pct", out var pct) && double.IsFinite(pct) ? Math.Clamp((int)Math.Round(pct), 20, 100) : style.SizePercent,
    NotchOffset = ToolsOptionStore.TryGetDouble(settings, "notch_offset", out var offset) && double.IsFinite(offset) && offset >= 0 ? offset : style.NotchOffset
  };

  private static JsonObject DocumentSettings(RhinoDoc doc)
  {
    var text = doc.Strings.GetValue(DocumentSection, Section);
    return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) as JsonObject
      ?? throw new JsonException("Notch document settings are not an object.");
  }

  internal static Style Read(RhinoDoc doc)
  {
    var style = ToolsOptionStore.Read(Section, settings => Overlay(Defaults, settings));
    try { return Overlay(style, DocumentSettings(doc)); }
    catch (JsonException ex) { Log.Write("MatchingLabels", $"Document label settings could not be read: {ex.Message}"); return style; }
  }

  internal static string Next(string value, bool advance) => advance ? LabelSequences.Increment(value, Defaults.Value) : value;

  internal static bool Advance(RhinoDoc doc, string expected, string next)
  {
    if (string.Equals(expected, next, StringComparison.Ordinal)) return true;
    try
    {
      if (!string.Equals(Read(doc).Value, expected, StringComparison.Ordinal))
        throw new InvalidOperationException("The shared label value changed during placement; its newer value was retained.");
      var document = DocumentSettings(doc);
      if (!ToolsOptionStore.Update(Section, settings => settings["label_value"] = next))
        throw new InvalidOperationException(ToolsOptionStore.LastError);
      document["label_value"] = next;
      doc.Strings.SetString(DocumentSection, Section, document.ToJsonString());
      NotifyChanged(doc);
      return true;
    }
    catch (Exception ex) { Log.Write("MatchingLabels", $"Label sequence was not advanced: {ex.Message}"); return false; }
  }

  internal static void NotifyChanged(RhinoDoc doc)
  {
    foreach (var handler in Changed?.GetInvocationList() ?? [])
      try { ((Action<RhinoDoc>)handler)(doc); }
      catch (Exception ex) { Log.Write("MatchingLabels", $"Label settings refresh failed: {ex.Message}"); }
  }

  static double EstimatedLabelHalfWidth(string text, double height)
  {
    if (height <= 0.0 || string.IsNullOrEmpty(text)) return 0.5 * height * 0.65;
    double units = 0.0;
    foreach (char ch in text)
    {
      if ("ilI1|".IndexOf(ch) >= 0)             units += 0.35;
      else if ("mwMW@#%&".IndexOf(ch) >= 0)     units += 0.95;
      else if (".,;:!`'\"".IndexOf(ch) >= 0)    units += 0.28;
      else if ("-_/\\".IndexOf(ch) >= 0)         units += 0.45;
      else if (ch == ' ')                        units += 0.32;
      else if (char.IsDigit(ch))                 units += 0.62;
      else                                       units += 0.68;
    }
    double w = Math.Max(height * 0.65, units * height);
    return 0.5 * w;
  }

  static double GeometryXInPlane(Plane plane, Point3d pt)
  {
    if (plane.ClosestParameter(pt, out double u, out _)) return u;
    return Vector3d.Multiply(new Vector3d(pt.X, pt.Y, pt.Z), plane.XAxis);
  }

  static (double min, double max) GeometryXRangeInPlane(
    IReadOnlyList<Curve> geometry, Plane plane)
  {
    var values = new List<double>();
    foreach (var geom in geometry)
    {
      if (geom is LineCurve lc)
      {
        values.Add(GeometryXInPlane(plane, lc.Line.From));
        values.Add(GeometryXInPlane(plane, lc.Line.To));
      }
      else if (geom is PolylineCurve plc)
      {
        foreach (var pt in plc.ToPolyline())
          values.Add(GeometryXInPlane(plane, pt));
      }
      else
      {
        var bbox = geom.GetBoundingBox(plane);
        if (bbox.IsValid)
        {
          values.Add(bbox.Min.X);
          values.Add(bbox.Max.X);
        }
      }
    }
    if (values.Count > 0) return (values.Min(), values.Max());
    return (0.0, 0.0);
  }

  static double MeasuredLabelHalfWidth(RhinoDoc doc, string text, double height, Plane plane, bool formatted = false)
  {
    if (height <= doc.ModelAbsoluteTolerance) return 0.0;
    if (!formatted) text = LabelSequences.FormatDisplayLabel(text);
    // Measure in WorldXY for orientation-independent width
    using var te = new TextEntity
    {
      Plane         = Plane.WorldXY,
      PlainText     = text,
      TextHeight    = height,
      Justification = TextJustification.MiddleCenter,
      DimensionScale= ConfiguredDimensionScale,
    };
    var bbox = te.GetBoundingBox(Plane.WorldXY);
    if (bbox.IsValid)
    {
      double w = Math.Max(0.0, bbox.Max.X - bbox.Min.X);
      if (w > doc.ModelAbsoluteTolerance)
        return 0.5 * w * LabelWidthFactor;
    }
    return EstimatedLabelHalfWidth(text, height) * LabelWidthFactor;
  }

  static double ChooseLabelTangentSide(Curve curve, double lengthFromStart,
    double preferredSign, double requiredOffset, double tol)
  {
    double total = curve.GetLength();
    if (total <= tol) return preferredSign;
    double d    = Math.Clamp(lengthFromStart, 0.0, total);
    double posS = Math.Max(0.0, total - d);
    double negS = Math.Max(0.0, d);
    double prefS = preferredSign >= 0.0 ? posS : negS;
    if (prefS + tol >= requiredOffset) return preferredSign;
    double othS = preferredSign >= 0.0 ? negS : posS;
    if (othS + tol >= requiredOffset) return -preferredSign;
    return preferredSign;
  }

  internal static (Plane plane, bool sideFlipped, BoundingBox bbox) Layout(
    RhinoDoc doc, Curve curve, double lengthFromStart,
    Vector3d direction, Vector3d tangent, double notchOffset,
    IReadOnlyList<Curve>? notchGeom, string labelText, double labelSize,
    double labelOffset, double labelOffsetY, string curveSide, string prefix = DefaultPrefix)
  {
    double tol = doc.ModelAbsoluteTolerance;
    if (labelSize <= tol)
      return (Plane.Unset, false, BoundingBox.Empty);

    var curvePoint = MatchingNotches.PointAtLength(curve, lengthFromStart);
    var anchor = curvePoint + direction * Math.Max(0.0, notchOffset) * 0.5;
    if (Math.Abs(labelOffsetY) > tol)
      anchor = anchor + direction * labelOffsetY;

    var plane = FacingPlane(doc, anchor, tangent, direction, curvePoint);
    if (!plane.IsValid)
      return (Plane.Unset, false, BoundingBox.Empty);

    string displayValue = LabelSequences.FormatDisplayLabel(labelText);
    double labelHW = prefix.Length == 0 ? MeasuredLabelHalfWidth(doc, displayValue, labelSize, plane, true)
      : Math.Max(MeasuredLabelHalfWidth(doc,
          FormatNotchLabel(labelText, prefix, plane, plane.Origin + plane.XAxis), labelSize, plane, true),
        MeasuredLabelHalfWidth(doc,
          FormatNotchLabel(labelText, prefix, plane, plane.Origin - plane.XAxis), labelSize, plane, true));
    var effectiveBbox = new BoundingBox(
      new Point3d(-labelHW, -0.5 * labelSize, 0.0),
      new Point3d( labelHW,  0.5 * labelSize, 0.0));

    if (notchGeom == null)
      return (plane, false, effectiveBbox);

    var (notchMin, notchMax) = GeometryXRangeInPlane(notchGeom, plane);
    double notchCenter  = 0.5 * (notchMin + notchMax);
    double labelCenter  = GeometryXInPlane(plane, plane.Origin);
    double requestedGap = labelOffset;
    double notchHW      = Math.Max(0.0, 0.5 * (notchMax - notchMin));

    double preferredCurveSign = curveSide == "Right" ? 1.0 : -1.0;
    double requiredOffset = notchHW + Math.Max(0.0, requestedGap) + 2.0 * labelHW;
    double sideSignCurve  = ChooseLabelTangentSide(curve, lengthFromStart, preferredCurveSign, requiredOffset, tol);
    bool sideFlipped      = sideSignCurve != preferredCurveSign;

    double tanPlaneDot    = Vector3d.Multiply(plane.XAxis, tangent);
    double curveToPlane   = tanPlaneDot < 0.0 ? -1.0 : 1.0;
    double sideSignPlane  = sideSignCurve * curveToPlane;

    double clearance = sideSignPlane * (labelCenter - notchCenter) - (notchHW + labelHW);
    if (clearance >= requestedGap - tol)
      return (plane, sideFlipped, effectiveBbox);

    double delta = sideSignPlane * (requestedGap - clearance);
    if (Math.Abs(delta) <= tol)
      return (plane, sideFlipped, effectiveBbox);

    var shifted = new Plane(plane);
    shifted.Origin = shifted.Origin + shifted.XAxis * delta;
    return (shifted, sideFlipped, effectiveBbox);
  }

  internal static string FormatNotchLabel(string? value,string prefix,Plane plane,Point3d notchPoint)
  {
    string display=LabelSequences.FormatDisplayLabel((value??string.Empty).Trim());
    if(display.Length==0)return prefix.TrimEnd();
    if(prefix.Length==0)return display;
    string fixedText=prefix.TrimEnd(),separator=prefix.Substring(fixedText.Length);
    return (notchPoint-plane.Origin)*plane.XAxis<0?display+separator+fixedText:fixedText+separator+display;
  }

  internal static TextEntity CreateText(Plane plane, string value, double height)
  {
    var text = new TextEntity
    {
      Plane = plane, PlainText = LabelSequences.FormatDisplayLabel(value), TextHeight = height,
      Justification = TextJustification.MiddleCenter, DimensionScale = ConfiguredDimensionScale
    };
    return text;
  }

  internal static Plane FacingPlane(RhinoDoc doc, Point3d anchor,
    Vector3d tangent, Vector3d direction, Point3d? boundaryPoint)
  {
    var xAxis = new Vector3d(tangent);
    if (!xAxis.Unitize()) return Plane.Unset;
    var view = doc.Views.ActiveView;
    var upAxis = view?.ActiveViewport.ConstructionPlane().ZAxis ?? Vector3d.ZAxis;
    if (!upAxis.Unitize()) upAxis = Vector3d.ZAxis;
    var yAxis = Vector3d.CrossProduct(upAxis,xAxis);
    if (!yAxis.Unitize())
    {
      yAxis = -direction;
      if (!yAxis.Unitize()) return Plane.Unset;
    }
    var plane = new Plane(anchor,xAxis,yAxis);
    if (!plane.IsValid) return Plane.Unset;
    var refX = view?.ActiveViewport.ConstructionPlane().XAxis ?? Vector3d.XAxis;
    if (!refX.Unitize()) refX = Vector3d.XAxis;
    if (plane.XAxis*refX < 0)
    {
      xAxis = -xAxis; yAxis = -yAxis;
      plane = new Plane(anchor,xAxis,yAxis);
      if (!plane.IsValid) return Plane.Unset;
    }
    if (boundaryPoint.HasValue && (boundaryPoint.Value-anchor)*plane.YAxis < 0)
      plane = new Plane(anchor,-plane.XAxis,-plane.YAxis);
    return plane.IsValid ? plane : Plane.Unset;
  }
}
