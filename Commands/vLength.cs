using System.Drawing;
using System.Globalization;
using BigInteger = System.Numerics.BigInteger;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>Places a live curve-length label parallel to the local tangent on the cursor side.</summary>
public sealed class vLength : vToolsCommand
{
  // Defaults and customizable constants
  [CommandDefault("vLength", "vTools.Commands.vLength.DefaultOffset", "Nonnegative gap from the curve tangent to the nearest text bounds, in model units.", OptionKeys = "offset")]
  private const double DefaultOffset = 0.2; // Nonnegative model-unit gap to the visible label bounds.
  [CommandDefault("vLength", "vTools.Commands.vLength.DefaultSize", "Positive label height in model units.", OptionKeys = "size")]
  private const double DefaultSize = 0.25; // Positive model-unit text height, independent of annotation scale.
  [CommandDefault("vLength", "vTools.Commands.vLength.DefaultRounding", "Nonnegative displayed rounding increment in model units; zero uses document display precision.", OptionKeys = "rounding")]
  private const double DefaultRounding = 0; // Zero means Default/document display precision; positive values are rounding increments.
  [CommandDefault("vLength", "vTools.Commands.vLength.DefaultFraction", "true displays reduced mixed fractions; false displays decimal lengths.", OptionKeys = "fraction")]
  private const bool DefaultFraction = true; // true uses reduced mixed fractions; false uses decimal numbers.
  [CommandDefault("vLength", "vTools.Commands.vLength.DefaultRotation", "Quarter turns from the local tangent, normalized from zero through three.", OptionKeys = "rotate90")]
  private const int DefaultRotation = 0; // Quarter turns: zero is parallel to the curve, one is perpendicular.
  private const int MaximumFractionPrecision = 8; // Largest document fractional precision index, giving a denominator of 256.
  private const int MaximumDecimalPrecision = 15; // Largest document decimal precision accepted for display rounding.
  [global::vTools.CommandDefault("vLength", "vTools.Commands.vLength.DisplayScale", "Positive fixed annotation display scale so Size is in model units.")] private const double DisplayScale = 1; // Positive fixed annotation display scale so Size is in model units.

  private static readonly double ConfiguredDisplayScale = global::vTools.CommandDefaults.Get("vLength", "vTools.Commands.vLength.DisplayScale", DisplayScale); // Resolved once from advanced defaults; restart Rhino after changes.
  private const bool PlacementObjectSnaps = false; // false follows the cursor continuously; true lets native object snaps pull the length label to snap points.
  private const string LabelName = "LengthLabel"; // Object name assigned to newly placed annotations.
  [global::vTools.CommandDefault("vLength", "vTools.Commands.vLength.PreviewColor", "RGB color of the temporary length annotation.", "#FF1E90FF")] private static readonly Color PreviewColor = global::vTools.CommandDefaults.GetColor("vLength", "vTools.Commands.vLength.PreviewColor", Color.DodgerBlue); // RGB color of the temporary length annotation.
  private static readonly Options Defaults = new(
    CommandDefaults.Get("vLength", "vTools.Commands.vLength.DefaultOffset", DefaultOffset),
    CommandDefaults.Get("vLength", "vTools.Commands.vLength.DefaultSize", DefaultSize),
    CommandDefaults.Get("vLength", "vTools.Commands.vLength.DefaultRounding", DefaultRounding),
    CommandDefaults.Get("vLength", "vTools.Commands.vLength.DefaultFraction", DefaultFraction),
    CommandDefaults.Get("vLength", "vTools.Commands.vLength.DefaultRotation", DefaultRotation)); // Advanced defaults are resolved once; restart after changing them.

  private const string Section = "vLength";
  private sealed record Options(double Offset, double Size, double Rounding, bool Fraction, int Rotation);
  private static Options _settings = Defaults;
  public override string EnglishName => Section;

  private sealed class Controls
  {
    internal OptionDouble Offset = new(_settings.Offset, true, 0);
    internal OptionDouble Size = new(_settings.Size, RhinoMath.ZeroTolerance, double.MaxValue);
    internal OptionToggle Fraction = new(_settings.Fraction, "Off", "On");
    internal int Rounding { get; }
    internal int Rotate { get; }
    internal Controls(GetBaseClass getter)
    {
      getter.AddOptionDouble("Offset", ref Offset);
      getter.AddOptionDouble("Size", ref Size);
      Rounding = getter.AddOption("Rounding", FormatRounding(_settings));
      getter.AddOptionToggle("Fraction", ref Fraction);
      Rotate = getter.AddOption("Rotate", (NormalizeRotation(_settings.Rotation) * 90).ToString(CultureInfo.InvariantCulture));
    }
  }

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    try
    {
      using var selector = new GetObject();
      selector.SetCommandPrompt("Select curve to label");
      selector.GeometryFilter = ObjectType.Curve;
      selector.SubObjectSelect = false;
      selector.GroupSelect = false;
      selector.EnablePreSelect(true, true);
      selector.EnableHighlight(false);
      selector.EnableClearObjectsOnEntry(false);
      selector.EnableUnselectObjectsOnExit(false);
      selector.DeselectAllBeforePostSelect = false;
      selector.AcceptNothing(true);
      while (true)
      {
        selector.ClearCommandOptions();
        var controls = new Controls(selector);
        var input = selector.Get();
        if (input == GetResult.Option) { ReadControls(doc, selector, controls); continue; }
        if (input != GetResult.Object) return selector.CommandResult();
        break;
      }
      using var reference = selector.Object(0);
      using var curve = reference.Curve()?.DuplicateCurve();
      if (curve == null || !curve.IsValid || !double.IsFinite(curve.GetLength()) || curve.GetLength() <= RhinoMath.ZeroTolerance)
      { RhinoApp.WriteLine("vLength: select a valid curve with nonzero length."); return Result.Failure; }
      uint sourceCrc = reference.Object()!.Geometry.DataCRC(0);
      using var highlight = new PreviewDisplay.ObjectHighlighter(doc);
      highlight.SetObjects([reference.ObjectId]);
      while (true)
      {
        using var text = CreateTemplate(doc, FormatLength(doc, curve.GetLength(), _settings), _settings.Size);
        using var getter = new PlacementGetter(doc, curve, text, _settings);
        getter.SetCommandPrompt("Position length label. Click to place");
        getter.AcceptNothing(true);
        var controls = new Controls(getter);
        var input = getter.Get();
        if (input == GetResult.Option) { ReadControls(doc, getter, controls); continue; }
        if (input != GetResult.Point) return getter.CommandResult();
        getter.Update(getter.Point(), doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY);
        if (getter.Preview == null) { RhinoApp.WriteLine("vLength: cannot align text at that position."); continue; }
        if (doc.Objects.FindId(reference.ObjectId)?.Geometry.DataCRC(0) != sourceCrc)
        { RhinoApp.WriteLine("vLength: source curve changed; select it again."); return Result.Cancel; }
        using var attributes = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex, Name = LabelName };
        attributes.SetUserString("vLength.source", reference.ObjectId.ToString("D"));
        foreach (int group in reference.Object()!.Attributes.GetGroupList() ?? [])
          if (doc.Groups[group] is { IsDeleted: false }) attributes.AddToGroup(group);
        var id = doc.Objects.AddText(getter.Preview, attributes);
        if (id == Guid.Empty) return Result.Failure;
        Log.Write(Section, $"Placed source={reference.ObjectId} text={getter.Preview.PlainText} label={id}");
        doc.Views.Redraw();
        return Result.Success;
      }
    }
    catch (Exception ex)
    {
      Log.Write(Section, $"Placement failed: {ex}");
      RhinoApp.WriteLine("vLength: label placement failed; see the diagnostic log.");
      return Result.Failure;
    }
  }

  private static void LoadOptions()
  {
    _settings = ToolsOptionStore.Read(Section, section => new Options(
      ToolsOptionStore.TryGetDouble(section, "offset", out var offset) && double.IsFinite(offset) && offset >= 0 ? offset : Defaults.Offset,
      ToolsOptionStore.TryGetDouble(section, "size", out var size) && double.IsFinite(size) && size > RhinoMath.ZeroTolerance ? size : Defaults.Size,
      ToolsOptionStore.TryGetDouble(section, "rounding", out var rounding) && double.IsFinite(rounding) && rounding >= 0 ? rounding : Defaults.Rounding,
      ToolsOptionStore.TryGetBool(section, "fraction", out var fraction) ? fraction : Defaults.Fraction,
      ToolsOptionStore.TryGetDouble(section, "rotate90", out var rotate) && double.IsFinite(rotate) && rotate >= 0 && rotate <= 3 ? (int)rotate : Defaults.Rotation));
  }

  private static void ReadControls(RhinoDoc doc, GetBaseClass getter, Controls controls)
  {
    _settings = _settings with { Offset = controls.Offset.CurrentValue, Size = controls.Size.CurrentValue, Fraction = controls.Fraction.CurrentValue };
    if (getter.Option()?.Index == controls.Rotate) _settings = _settings with { Rotation = NormalizeRotation(_settings.Rotation + 1) };
    if (getter.Option()?.Index == controls.Rounding)
    {
      using var number = new GetNumber();
      number.SetCommandPrompt("Rounding increment");
      number.SetLowerLimit(RhinoMath.ZeroTolerance, true);
      number.SetCommandPromptDefault(FormatValue((decimal)RoundingStep(doc, _settings), _settings.Fraction));
      number.AcceptNothing(true);
      int useDefault = number.AddOption("Default");
      var input = number.Get();
      if (input == GetResult.Option && number.Option()?.Index == useDefault) _settings = _settings with { Rounding = 0 };
      else if (input == GetResult.Number) _settings = _settings with { Rounding = number.Number() };
    }
    if (!ToolsOptionStore.Update(Section, section =>
    {
      section["offset"] = _settings.Offset; section["size"] = _settings.Size; section["rounding"] = _settings.Rounding;
      section["fraction"] = _settings.Fraction; section["rotate90"] = NormalizeRotation(_settings.Rotation);
    })) Log.Write(Section, $"Options not saved: {ToolsOptionStore.LastError}");
  }

  private static int NormalizeRotation(int value) => ((value % 4) + 4) % 4;

  private static string FormatRounding(Options settings) => settings.Rounding > 0
    ? FormatValue((decimal)settings.Rounding, settings.Fraction) : "Default";

  private static double RoundingStep(RhinoDoc doc, Options settings) => settings.Rounding > 0 ? settings.Rounding : settings.Fraction
    ? 1.0 / (1 << Math.Clamp(doc.ModelDistanceDisplayPrecision, 0, MaximumFractionPrecision))
    : Math.Pow(10, -Math.Clamp(doc.ModelDistanceDisplayPrecision, 0, MaximumDecimalPrecision));

  private static string FormatLength(RhinoDoc doc, double length, Options settings)
  {
    double increment = RoundingStep(doc, settings);
    if (!double.IsFinite(length) || length < 0 || !double.IsFinite(increment) || increment <= 0)
      throw new ArgumentOutOfRangeException(nameof(length), "Length and rounding must be finite and nonnegative, with positive rounding.");
    decimal value = decimal.Parse(length.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    decimal step = decimal.Parse(increment.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    decimal rounded = decimal.Round(value / step, 0, MidpointRounding.AwayFromZero) * step;
    return FormatValue(rounded, settings.Fraction);
  }

  private static string FormatValue(decimal value, bool useFractions)
  {
    if (!useFractions) return value.ToString("0.############################", CultureInfo.CurrentCulture);
    decimal whole = decimal.Floor(value), fraction = value - whole, denominator = 1;
    while (fraction * denominator != decimal.Truncate(fraction * denominator)) denominator *= 10;
    if (fraction == 0) return whole.ToString(CultureInfo.CurrentCulture);
    var numerator = new BigInteger(fraction * denominator);
    var divisor = new BigInteger(denominator);
    var gcd = BigInteger.GreatestCommonDivisor(numerator, divisor);
    string tail = (numerator / gcd).ToString(CultureInfo.CurrentCulture) + "/" + (divisor / gcd).ToString(CultureInfo.CurrentCulture);
    return whole == 0 ? tail : whole.ToString(CultureInfo.CurrentCulture) + " " + tail;
  }

  private static TextEntity CreateTemplate(RhinoDoc doc, string value, double size)
  {
    var text = new TextEntity
    {
      Plane = Plane.WorldXY, PlainText = value, TextHeight = size, DrawForward = false,
      Justification = TextJustification.MiddleCenter, DimensionStyleId = doc.DimStyles.Current.Id, DimensionScale = ConfiguredDisplayScale
    };
    if (!AnnotationTextTransform.ApplyFixedDisplayTextHeight(doc, text, doc.Views.ActiveView?.ActiveViewport, size, ConfiguredDisplayScale))
    { text.Dispose(); throw new InvalidOperationException("Cannot set the label's model-unit height."); }
    using var style = text.GetDimensionStyle(doc.DimStyles.Current);
    style.TextOrientation = TextOrientation.InPlane; style.TextRotation = 0;
    style.TextHorizontalAlignment = TextHorizontalAlignment.Center;
    style.TextVerticalAlignment = TextVerticalAlignment.Middle;
    style.SetFieldOverride(DimensionStyle.Field.TextHorizontalAlignment);
    style.SetFieldOverride(DimensionStyle.Field.TextVerticalAlignment);
    if (!text.SetOverrideDimStyle(style)) { text.Dispose(); throw new InvalidOperationException("Cannot set the label orientation."); }
    text.TextHorizontalAlignment = TextHorizontalAlignment.Center;
    text.TextVerticalAlignment = TextVerticalAlignment.Middle;
    text.Justification = TextJustification.MiddleCenter;
    return text;
  }

  private static TextEntity? PlaceText(Curve curve, TextEntity template, BoundingBox bounds, Point3d cursor, Plane constructionPlane, Options settings)
  {
    if (!cursor.IsValid || !bounds.IsValid || !curve.ClosestPoint(cursor, out double parameter)) return null;
    var point = curve.PointAt(parameter);
    var tangent = curve.TangentAt(parameter);
    if (!tangent.Unitize()) return null;
    var normal = constructionPlane.Normal;
    if (!curve.IsLinear() && curve.TryGetPlane(out var curvePlane))
    { normal = curvePlane.Normal; if (normal * constructionPlane.Normal < 0) normal = -normal; }
    normal -= tangent * (normal * tangent);
    if (!normal.Unitize()) { normal = constructionPlane.YAxis - tangent * (constructionPlane.YAxis * tangent); if (!normal.Unitize()) return null; }
    var side = Vector3d.CrossProduct(normal, tangent);
    if (!side.Unitize()) return null;
    if ((cursor - point) * side < 0) side = -side;
    var xAxis = tangent;
    if (xAxis * constructionPlane.XAxis < -RhinoMath.ZeroTolerance ||
      Math.Abs(xAxis * constructionPlane.XAxis) <= RhinoMath.ZeroTolerance && xAxis * constructionPlane.YAxis < 0) xAxis = -xAxis;
    var yAxis = Vector3d.CrossProduct(normal, xAxis);
    double angle = NormalizeRotation(settings.Rotation) * Math.PI / 2;
    xAxis.Rotate(angle, normal); yAxis.Rotate(angle, normal);
    double halfSpan = Math.Abs(side * xAxis) * (bounds.Max.X - bounds.Min.X) / 2 + Math.Abs(side * yAxis) * (bounds.Max.Y - bounds.Min.Y) / 2;
    var center = point + side * (settings.Offset + halfSpan);
    var plane = new Plane(center - xAxis * bounds.Center.X - yAxis * bounds.Center.Y, xAxis, yAxis);
    var text = template.Duplicate() as TextEntity;
    if (text != null) text.Plane = plane;
    return text;
  }

  private sealed class PlacementGetter : GetPoint
  {
    private readonly Curve _curve;
    private readonly TextEntity _template;
    private readonly BoundingBox _bounds;
    private readonly Options _options;
    internal TextEntity? Preview { get; private set; }
    internal PlacementGetter(RhinoDoc doc, Curve curve, TextEntity template, Options options)
    {
      _curve = curve; _template = template; _options = options;
      _bounds = AnnotationTextTransform.TextBounds(template);
      EnableTransparentCommands(true);
      PermitObjectSnap(PlacementObjectSnaps);
      EnableObjectSnapCursors(PlacementObjectSnaps);
      var plane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
      plane.Origin = curve.PointAtNormalizedLength(0.5);
      if (!curve.IsLinear() && curve.TryGetPlane(out var fitted, doc.ModelAbsoluteTolerance)) plane = fitted;
      Constrain(plane, false);
    }
    internal void Update(Point3d point, Plane plane)
    { Preview?.Dispose(); Preview = PlaceText(_curve, _template, _bounds, point, plane, _options); }
    protected override void OnDynamicDraw(GetPointDrawEventArgs e)
    {
      Update(e.CurrentPoint, e.Viewport.ConstructionPlane());
      if (Preview != null) e.Display.DrawText(Preview, PreviewColor, ConfiguredDisplayScale);
      base.OnDynamicDraw(e);
    }
    protected override void Dispose(bool disposing)
    { if (disposing) { Preview?.Dispose(); Preview = null; } base.Dispose(disposing); }
  }
}
