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
  internal const double DefaultSize = 0.3; // Nonnegative manual text height in model units.
  internal const string DefaultLayer = "PLOT"; // Label layer path, or the current-layer sentinel.
  internal const bool DefaultAutoAdvance = true; // true advances once per matching set; false reuses the selected value.
  internal const bool DefaultAutoSize = false; // true sizes from notch offset and percentage; false uses manual height.
  internal const int DefaultSizePercent = 75; // Auto-size percentage, from 20 through 100.
  internal const double DefaultNotchOffset = 0.5; // Nonnegative model-unit offset used by notch auto-sizing.
  internal const double DimensionScale = 0.9; // Positive annotation scale shared by notch and seam text.
  private const string Section = "vNotches"; // Existing global and document label settings owner.
  private const string DocumentSection = "vTools"; // Existing Rhino document-string section.
  private static readonly Style Defaults = new(
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelValue", DefaultValue),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelSize", DefaultSize),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelLayer", DefaultLayer),
    DefaultAutoAdvance,
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelSizeAuto", DefaultAutoSize),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultLabelSizePercent", DefaultSizePercent),
    CommandDefaults.Get(Section, "vTools.Commands.vNotches.DefaultNotchOffset", DefaultNotchOffset)); // Resolved once from the existing vNotches factory settings.

  internal sealed record Style(string Value, double ManualSize, string Layer, bool AutoAdvance,
    bool AutoSize, int SizePercent, double NotchOffset)
  {
    internal double Height => AutoSize ? NotchOffset * SizePercent * 0.01 : ManualSize;
  }

  internal static event Action<RhinoDoc>? Changed;

  internal static string ReadText(JsonObject? settings, string key, string fallback) =>
    settings?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text ?? string.Empty : fallback;

  private static Style Overlay(Style style, JsonObject? settings) => style with
  {
    Value = LabelSequences.NormalizeValue(ReadText(settings, "label_value", style.Value)),
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

  internal static TextEntity CreateText(Plane plane, string value, double height)
  {
    var text = new TextEntity
    {
      Plane = plane, PlainText = LabelSequences.FormatDisplayLabel(value), TextHeight = height,
      Justification = TextJustification.MiddleCenter, DimensionScale = DimensionScale
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
