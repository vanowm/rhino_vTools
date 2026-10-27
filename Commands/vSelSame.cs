using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools.Commands;

[CommandStyle(Style.NotUndoable)]
public sealed class vSelSame : vToolsCommand
{
  // Defaults and customizable constants
  private const double PreviewDelaySeconds = 0.08; // Checkbox debounce in seconds; positive.
  private const long SlowPreviewMilliseconds = 100; // Minimum elapsed milliseconds recorded in diagnostics.
  private static readonly Size InitialDialogSize = new(560, 570); // Initial filter-window dimensions in logical pixels.
  private static readonly Size MinimumDialogSize = new(430, 280); // Smallest filter-window dimensions in logical pixels.
  private static readonly Size FilterEditorSize = new(480, 350); // Initial condition-editor dimensions in logical pixels.
  private const int IconButtonWidth = 26; // Fixed logical-pixel width for condition edit/remove and value add/remove controls.
  private const bool DefaultAddToSelection = false; // true adds matches to the existing selection; false replaces it when confirmed.
  private const int DialogPadding = 10; // Dialog edge clearance in logical pixels.
  private const int RowSpacing = 5; // Vertical criterion spacing in logical pixels.
  private const int LabelWidth = 140; // Criterion checkbox column width in logical pixels.
  private const int LabelLimit = 25; // Maximum visible criterion-label characters; tooltip retains full text.
  private const int ValueLimit = 100; // Maximum visible criterion-value characters; tooltip retains full text.
  private static readonly Color SeparatorColor = Color.FromArgb(222, 226, 233); // Faint section separator RGB.
  private static readonly Size CellSpacing = new(6, 0); // Criterion column spacing in logical pixels.
  private static readonly Size DialogSpacing = new(6, 8); // Dialog column/row spacing in logical pixels.
  private const int ButtonSpacing = 6; // Footer button spacing in logical pixels.
  private const bool DefaultMatchType = true; // true initially checks Type; false starts with no checked criteria.
  private const bool DefaultAllowMissingReferences = false; // true offers a criterion for any applicable reference; false requires it on every reference.
  private const bool DefaultSelectAllValues = true; // true enables every reference value initially; false starts value checklists empty.
  private const double ValueMenuMaximumHeight = 320; // Maximum checklist popup height in logical pixels before menu scrolling.
  private static readonly Size ValueArrowSize = new(10, 6); // Dropdown arrow bitmap dimensions in logical pixels.
  private const double DistanceToleranceFactor = 2; // Model-tolerance multiplier for rectangle and part recognition.
  private const double MinimumDistanceTolerance = 1e-8; // Lower recognition tolerance in document units.
  private const double RectangleAngleDegrees = 2; // Maximum rectangle edge-angle deviation in degrees.
  private const double PolygonRelativeTolerance = 1e-4; // Relative side-length/radius equality tolerance.
  private const double PolygonAngleDegrees = 1.5; // Maximum regular-polygon angle deviation in degrees.
  private const double SquareRelativeTolerance = 1e-6; // Relative square side-length equality tolerance.
  private const double RectangleCosineTolerance = 1e-6; // Absolute adjacent-edge cosine tolerance for type names.
  private const double OppositeEdgeToleranceFactor = 10; // Distance-tolerance multiplier for opposite rectangle edges.
  private const double OppositeEdgeMinimumTolerance = 1e-6; // Minimum opposite-edge length tolerance in document units.
  private const int SchemaVersion = 1; // Supported vNest JSON record version.
  private const int MaximumNestCharacters = 16384; // Maximum nesting JSON characters per identity.
  private const int MaximumCustomCharacters = 1048576; // Maximum custom-field JSON characters per identity.
  private const int MaximumCopies = 1000; // Supported copies range is 1 through this limit.
  private const int MaximumTables = 128; // Supported zero-based placement-table index is below this limit.
  private const int MaximumFieldNameCharacters = 256; // Maximum custom-field key length.
  private const int MaximumMaterialNameCharacters = 80; // Maximum normalized material-name length.
  private const int MaximumPartNameCharacters = 256; // Maximum saved display-name length.
  private const int DefaultPartCopies = 1; // Copies for an uncustomized part; positive integer.
  private const bool DefaultPartIncluded = true; // true includes an uncustomized part; false excludes it.
  private const bool DefaultPartFixed = false; // true fixes table placement; false lets nesting choose it.
  private const bool DefaultPartManualRotation = false; // true fixes the saved angle; false uses allowed rotations.
  private const bool DefaultPartFlipped = false; // true mirrors an uncustomized part; false preserves its orientation.
  private const double DefaultPartAngle = 0; // Initial source-relative placement angle in degrees.
  private const int DefaultPartTable = 0; // Initial zero-based placement table index.
  private const double DefaultPartCoordinate = 0; // Initial table-local X/Y position in document units.
  private const string NestSection = "vNest.PartSettings"; // Document-local nesting records keyed by group/boundary GUID.
  private const string CustomSection = "vObjectPropertiesPlus.PartData"; // Independent document-local part custom fields.
  private const string NamesSection = "vTools.PartNames"; // Display-name overrides; labels and group names are never edited.
  private const string ReferenceLayer = "Reference"; // Preferred part-label layer and fallback outline layer.
  private const string SourceSettingsKey = "vNest.sourceSettings"; // Original part identity attached to placed geometry.

  public override string EnglishName => "vSelSame";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var references = doc.Objects.GetSelectedObjects(false, false).Where(obj => !obj.IsDeleted).ToArray();
    var horizontal = GetHorizontalReference(doc);
    (double Width, double Height)? RectangleSize(Curve curve) =>
      TryGetRectangleDimensions(curve, Math.Max(doc.ModelAbsoluteTolerance * DistanceToleranceFactor, MinimumDistanceTolerance),
        RhinoMath.ToRadians(RectangleAngleDegrees), horizontal, out double width, out double height) ? (width, height) : null;
    using var highlight = new PreviewDisplay.ObjectHighlighter(doc);
    SelSameDialog? dialog = null;
    try
    {
      List<FilterSetting>? filters = null;
      bool referenceChanged = false;
      while (true)
      {
        dialog = new SelSameDialog(doc, references, TypeName, RectangleSize,
          objects => highlight.SetObjects(objects.Select(obj => obj.Id)));
        if (filters != null) dialog.RestoreFilters(filters, referenceChanged);
        bool confirmed = dialog.ShowSemiModal(doc, RhinoEtoApp.MainWindowForDocument(doc));
        if (!dialog.ReferenceRequested)
        {
          if (!confirmed) return Result.Cancel;
          break;
        }
        filters = dialog.CaptureFilters();
        dialog.Dispose();
        dialog = null;
        var selection = doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).ToArray();
        referenceChanged = false;
        try
        {
          doc.Objects.UnselectAll();
          using var getter = new GetObject();
          getter.SetCommandPrompt("Select reference objects to match");
          getter.GroupSelect = true;
          getter.SubObjectSelect = false;
          getter.EnablePreSelect(false, true);
          getter.EnableUnselectObjectsOnExit(false);
          getter.GetMultiple(1, 0);
          if (getter.CommandResult() == Result.Success)
          {
            references = getter.Objects().Select(reference => reference.Object()).OfType<RhinoObject>().DistinctBy(obj => obj.Id).ToArray();
            referenceChanged = true;
          }
        }
        finally
        {
          doc.Objects.UnselectAll();
          doc.Objects.Select(selection);
          doc.Views.Redraw();
        }
      }
      var ids = dialog.Matches.Select(obj => doc.Objects.FindId(obj.Id))
        .Where(obj => obj != null && obj.IsSelectable(true, false, false, false)).Select(obj => obj!.Id).ToArray();
      if (ids.Length == 0) return Result.Nothing;
      highlight.SetObjects(Array.Empty<Guid>());
      int alreadySelected = ids.Count(id => doc.Objects.FindId(id)?.IsSelected(false) > 0);
      if (!dialog.AddToSelection) doc.Objects.UnselectAll();
      doc.Objects.Select(ids);
      doc.Views.Redraw();
      RhinoApp.WriteLine(dialog.AddToSelection ? "vSelSame: added {0} objects to selection." : "vSelSame: selected {0} objects.",
        dialog.AddToSelection ? ids.Length - alreadySelected : ids.Length);
      return Result.Success;
    }
    catch (Exception ex)
    {
      Log.Write("vSelSame", $"Selection failed: {ex}");
      RhinoApp.WriteLine("vSelSame could not complete. Details were written to the plug-in log.");
      return Result.Failure;
    }
    finally
    {
      dialog?.StopPreview();
      dialog?.Dispose();
    }
  }
  private static Vector3d GetHorizontalReference(RhinoDoc? doc)
  {
    try
    {
      var view = doc?.Views?.ActiveView;
      if (view != null)
      {
        Vector3d axis = view.ActiveViewport.ConstructionPlane().XAxis;
        if (axis.IsValid && axis.Unitize())
          return axis;
      }
    }
    catch
    {
    }

    return Vector3d.XAxis;
  }

  private static bool IsPerpendicular(Vector3d a, Vector3d b, double angleToleranceRadians)
  {
    double angle = Vector3d.VectorAngle(a, b);
    return Math.Abs(angle - (Math.PI * 0.5)) <= angleToleranceRadians;
  }

  private static bool IsParallel(Vector3d a, Vector3d b, double angleToleranceRadians)
  {
    double angle = Vector3d.VectorAngle(a, b);
    return angle <= angleToleranceRadians || Math.Abs(angle - Math.PI) <= angleToleranceRadians;
  }

  private static bool TryGetRectangleFrame(
    Curve curve,
    double distanceTolerance,
    double angleToleranceRadians,
    Vector3d horizontalReference,
    out Point3d center,
    out Vector3d widthAxis,
    out Vector3d heightAxis,
    out double width,
    out double height)
  {
    center = Point3d.Unset;
    widthAxis = Vector3d.Unset;
    heightAxis = Vector3d.Unset;
    width = 0.0;
    height = 0.0;

    if (curve == null || !curve.IsClosed)
      return false;

    if (!curve.TryGetPolyline(out Polyline polyline) || !polyline.IsValid || polyline.Count < 4)
      return false;

    var points = polyline.ToList();
    if (points.Count < 4)
      return false;

    if (points[0].DistanceTo(points[^1]) <= distanceTolerance)
      points.RemoveAt(points.Count - 1);

    if (points.Count != 4)
      return false;

    var edges = new Vector3d[4];
    var lengths = new double[4];
    for (int i = 0; i < 4; i++)
    {
      Vector3d edge = points[(i + 1) % 4] - points[i];
      double length = edge.Length;
      if (length <= distanceTolerance)
        return false;

      edge.Unitize();
      edges[i] = edge;
      lengths[i] = length;
    }

    for (int i = 0; i < 4; i++)
    {
      if (!IsPerpendicular(edges[i], edges[(i + 1) % 4], angleToleranceRadians))
        return false;
    }

    if (!IsParallel(edges[0], edges[2], angleToleranceRadians) || !IsParallel(edges[1], edges[3], angleToleranceRadians))
      return false;

    double lengthTolerance = Math.Max(distanceTolerance * OppositeEdgeToleranceFactor, OppositeEdgeMinimumTolerance);
    if (Math.Abs(lengths[0] - lengths[2]) > lengthTolerance || Math.Abs(lengths[1] - lengths[3]) > lengthTolerance)
      return false;

    Vector3d planeNormal = Vector3d.CrossProduct(edges[0], edges[1]);
    if (!planeNormal.IsValid || !planeNormal.Unitize())
      return false;

    Vector3d horizontalInPlane = horizontalReference.IsValid ? horizontalReference : Vector3d.XAxis;
    horizontalInPlane -= planeNormal * Vector3d.Multiply(horizontalInPlane, planeNormal);
    if (!horizontalInPlane.IsValid || horizontalInPlane.Length <= RhinoMath.SqrtEpsilon || !horizontalInPlane.Unitize())
      horizontalInPlane = edges[0];

    double scoreA = Math.Abs(Vector3d.Multiply(edges[0], horizontalInPlane));
    double scoreB = Math.Abs(Vector3d.Multiply(edges[1], horizontalInPlane));

    double sideA = (lengths[0] + lengths[2]) * 0.5;
    double sideB = (lengths[1] + lengths[3]) * 0.5;

    if (scoreA >= scoreB)
    {
      width = sideA;
      height = sideB;
      widthAxis = edges[0];
      heightAxis = edges[1];
    }
    else
    {
      width = sideB;
      height = sideA;
      widthAxis = edges[1];
      heightAxis = edges[0];
    }

    center = new Point3d(
      (points[0].X + points[1].X + points[2].X + points[3].X) * 0.25,
      (points[0].Y + points[1].Y + points[2].Y + points[3].Y) * 0.25,
      (points[0].Z + points[1].Z + points[2].Z + points[3].Z) * 0.25);

    return true;
  }

  private static bool TryGetRectangleDimensions(
    Curve curve,
    double distanceTolerance,
    double angleToleranceRadians,
    Vector3d horizontalReference,
    out double width,
    out double height)
  {
    return TryGetRectangleFrame(
      curve,
      distanceTolerance,
      angleToleranceRadians,
      horizontalReference,
      out _,
      out _,
      out _,
      out width,
      out height);
  }

  private static string TypeName(RhinoObject obj)
  {
    if (obj.ObjectType == ObjectType.Curve)
      return obj.Geometry is Curve c ? CurveTypeName(c) : "curve";

    string? explicitName = obj.Geometry switch
    {
      Brep brep     => BrepTypeName(brep),
      Surface       => "surface",
      TextEntity    => "text",
      TextDot       => "text dot",
      AngularDimension => "angle dim",
      RadialDimension  => "radial dim",
      Centermark    => "centermark",
      Dimension     => "dimension",
      Leader        => "leader",
      _             => null
    };

    if (explicitName != null)
      return explicitName;

    string shortDescription = obj.ShortDescription(false)?.Trim() ?? string.Empty;
    return shortDescription.Length > 0
      ? shortDescription.ToLowerInvariant()
      : obj.ObjectType.ToString().Replace("_", " ").ToLowerInvariant();
  }

  private static string BrepTypeName(Brep brep)
  {
    return brep.Faces.Count <= 1 ? "surface" : "polysurface";
  }

  private static string CurveTypeName(Curve c)
  {
    // Circle (check before arc - circles satisfy TryGetArc too).
    if (c.TryGetCircle(out _))
      return "circle";

    // Arc.
    if (c.TryGetArc(out _))
      return "arc";

    // Full ellipse.
    if (c.IsClosed && c.TryGetEllipse(out _))
      return "ellipse";

    // Polyline / line / rectangle / square.
    if (c.TryGetPolyline(out Polyline pl) && pl.IsValid)
    {
      if (pl.SegmentCount == 1)
        return "line";

      if (pl.IsClosed && pl.SegmentCount == 4 && IsPolylineRectangle(pl))
        return IsPolylineSquare(pl) ? "square" : "rectangle";

      if (pl.IsClosed && pl.SegmentCount >= 3
        && TryGetRegularPolygon(pl, RhinoMath.SqrtEpsilon * 100,
          out _, out _, out _, out _, out _, out _))
        return "polygon";

      return "polyline";
    }

    // LineCurve fallback.
    if (c is LineCurve)
      return "line";

    return c.IsClosed ? "closed curve" : "curve";
  }

  private static bool IsPolylineRectangle(Polyline pl)
  {
    // pl is closed with 4 segments; check all interior angles are 90 degrees.
    for (int i = 0; i < 4; i++)
    {
      Vector3d v1 = pl[(i + 1) % 4] - pl[i];
      Vector3d v2 = pl[(i + 2) % 4] - pl[(i + 1) % 4];
      double len1 = v1.Length;
      double len2 = v2.Length;
      if (len1 < RhinoMath.SqrtEpsilon || len2 < RhinoMath.SqrtEpsilon)
        return false;
      double cosAngle = (v1 * v2) / (len1 * len2);
      if (Math.Abs(cosAngle) > RectangleCosineTolerance)
        return false;
    }
    return true;
  }

  private static bool IsPolylineSquare(Polyline pl)
  {
    double l0 = pl[0].DistanceTo(pl[1]);
    double l1 = pl[1].DistanceTo(pl[2]);
    double l2 = pl[2].DistanceTo(pl[3]);
    double l3 = pl[3].DistanceTo(pl[0]);
    double avg = (l0 + l1 + l2 + l3) / 4.0;
    if (avg < RhinoMath.SqrtEpsilon)
      return false;
    double tol = avg * SquareRelativeTolerance;
    return Math.Abs(l0 - l1) < tol && Math.Abs(l1 - l2) < tol && Math.Abs(l2 - l3) < tol;
  }

  private static bool TryGetRegularPolygon(
    Polyline pl,
    double distTol,
    out int sides,
    out Point3d center,
    out Vector3d firstVertexDir,
    out Vector3d normal,
    out double circumRadius,
    out double apothem)
  {
    sides = 0;
    center = Point3d.Unset;
    firstVertexDir = Vector3d.Zero;
    normal = Vector3d.Zero;
    circumRadius = 0;
    apothem = 0;

    if (!pl.IsValid || !pl.IsClosed)
      return false;

    int n = pl.SegmentCount;
    if (n < 3)
      return false;

    // All segments must have equal length.
    double len0 = pl[0].DistanceTo(pl[1]);
    if (len0 < distTol)
      return false;
    double lenTol = Math.Max(distTol, len0 * PolygonRelativeTolerance);
    for (int i = 1; i < n; i++)
    {
      double li = pl[i].DistanceTo(pl[(i + 1) % n]);
      if (Math.Abs(li - len0) > lenTol)
        return false;
    }

    // All interior angles must equal (n-2)*180/n degrees.
    double expectedInteriorDeg = (n - 2) * 180.0 / n;
    double angTolDeg = PolygonAngleDegrees;
    for (int i = 0; i < n; i++)
    {
      var incoming = pl[(i + 1) % n] - pl[i];
      var outgoing = pl[(i + 2) % n] - pl[(i + 1) % n];
      if (!incoming.Unitize() || !outgoing.Unitize())
        return false;
      double cosInterior = (-incoming) * outgoing;
      double interiorDeg = RhinoMath.ToDegrees(Math.Acos(Math.Clamp(cosInterior, -1.0, 1.0)));
      if (Math.Abs(interiorDeg - expectedInteriorDeg) > angTolDeg)
        return false;
    }

    // Center = mean of vertices (excluding duplicate closing vertex).
    double cx = 0, cy = 0, cz = 0;
    for (int i = 0; i < n; i++) { cx += pl[i].X; cy += pl[i].Y; cz += pl[i].Z; }
    center = new Point3d(cx / n, cy / n, cz / n);

    // All vertices must be equidistant from center.
    circumRadius = center.DistanceTo(pl[0]);
    if (circumRadius < distTol)
      return false;
    double radTol = Math.Max(distTol, circumRadius * PolygonRelativeTolerance);
    for (int i = 1; i < n; i++)
    {
      if (Math.Abs(center.DistanceTo(pl[i]) - circumRadius) > radTol)
        return false;
    }

    // Normal from first two edge vectors.
    var edge0 = pl[1] - pl[0];
    var edge1 = pl[2] - pl[1];
    normal = Vector3d.CrossProduct(edge0, edge1);
    if (!normal.Unitize())
      return false;

    firstVertexDir = pl[0] - center;
    if (!firstVertexDir.Unitize())
      return false;

    Point3d midEdge = pl[0] + (pl[1] - pl[0]) * 0.5;
    apothem = center.DistanceTo(midEdge);
    sides = n;
    return true;
  }


private static class SimilarObjectMatcher
{
  internal sealed record Value(string Display, string? Text = null, double? Number = null)
  {
    internal static Value String(string text) => new(string.IsNullOrEmpty(text) ? "(empty)" : text, text);
    internal static Value? Numeric(double number, string suffix = "") => double.IsFinite(number)
      ? new(number.ToString("G8", CultureInfo.CurrentCulture) + suffix, Number: number) : null;
  }

  internal sealed class Criterion
  {
    private readonly Func<RhinoObject, Value?> _read;
    private readonly Dictionary<Guid, (uint Serial, Value? Value)> _cache = new();
    private readonly Value?[] _references;
    private readonly int[] _referenceChoices;
    private readonly List<Value> _values = new();
    private readonly List<bool> _selectedValues = new();
    private readonly Func<string, Value?> _parse;
    private bool _loggedFailure;

    internal Criterion(string key, string group, string label, Func<RhinoObject, Value?> read,
      Value?[] references, double tolerance = 0, int cost = 0, Func<string, Value?>? parse = null)
    {
      Key = key; Group = group; Label = label; _read = read; _references = references;
      Tolerance = tolerance; Cost = cost;
      _parse = parse ?? Value.String;
      var values = _values;
      _referenceChoices = references.Select(value =>
      {
        if (value == null) return -1;
        int index = values.FindIndex(existing => Equal(existing, value));
        if (index >= 0) return index;
        values.Add(value);
        return values.Count - 1;
      }).ToArray();
      _selectedValues.AddRange(Enumerable.Repeat(DefaultSelectAllValues, values.Count));
    }

    internal string Key { get; }
    internal string Group { get; }
    internal string Label { get; }
    internal IReadOnlyList<Value> Values => _values;
    internal bool Edited { get; private set; }
    internal bool IsValueSelected(int index) => _selectedValues[index];
    internal void SetValueSelected(int index, bool selected) => _selectedValues[index] = selected;
    internal void SetAllValuesSelected(bool selected)
    { for (int index = 0; index < _selectedValues.Count; index++) _selectedValues[index] = selected; }
    internal bool TrySetValues(IReadOnlyList<string> texts, out string error)
    {
      var values = new List<Value>();
      try
      {
        foreach (string text in texts)
        {
          var value = _values.FirstOrDefault(existing => existing.Display == text || existing.Text == text)
            ?? _parse(text) ?? throw new ArgumentException($"Invalid value: {text}");
          if (!values.Any(existing => Equal(existing, value))) values.Add(value);
        }
      }
      catch (Exception ex) { error = ex.Message; return false; }
      _values.Clear(); _values.AddRange(values);
      _selectedValues.Clear(); _selectedValues.AddRange(Enumerable.Repeat(true, values.Count));
      Edited = true;
      error = "";
      return true;
    }
    internal string Display
    {
      get
      {
        var selected = Values.Where((_, index) => _selectedValues[index]).ToArray();
        return selected.Length == 0 ? "None" : selected.Length == 1 ? selected[0].Display
          : selected.Length == Values.Count ? $"All ({selected.Length})" : $"{selected.Length} of {Values.Count}";
      }
    }
    internal double Tolerance { get; }
    internal int Cost { get; }

    internal bool Matches(RhinoObject obj, int reference)
    {
      if (!Edited && (reference >= _references.Length || _referenceChoices[reference] < 0 ||
          !_selectedValues[_referenceChoices[reference]] || _references[reference] == null)) return false;
      if (!_cache.TryGetValue(obj.Id, out var entry) || entry.Serial != obj.RuntimeSerialNumber)
      {
        Value? value = null;
        try { value = _read(obj); }
        catch (Exception ex)
        {
          if (!_loggedFailure) Log.Write("SelSame", $"Cannot read {Key}: {ex.Message}");
          _loggedFailure = true;
        }
        _cache[obj.Id] = entry = (obj.RuntimeSerialNumber, value);
      }
      return entry.Value != null && (Edited
        ? _values.Where((_, index) => _selectedValues[index]).Any(expected => Equal(entry.Value, expected))
        : Equal(entry.Value, _references[reference]!));
    }

    private bool Equal(Value first, Value second) => first.Number.HasValue && second.Number.HasValue
      ? Math.Abs(first.Number.Value - second.Number.Value) <= Tolerance
      : first.Number == null && second.Number == null && string.Equals(first.Text, second.Text, StringComparison.Ordinal);
  }

  internal static IReadOnlyList<Criterion> CreateCriteria(RhinoDoc doc, IReadOnlyList<RhinoObject> references,
    Func<RhinoObject, string> typeName, Func<Curve, (double Width, double Height)?> rectangleSize)
    => CreateCriteria(doc, references, typeName, rectangleSize, false);

  internal static IReadOnlyList<Criterion> CreateCriteria(RhinoDoc doc, IReadOnlyList<RhinoObject> references,
    Func<RhinoObject, string> typeName, Func<Curve, (double Width, double Height)?> rectangleSize, bool catalog)
  {
    var criteria = new List<Criterion>();
    double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);

    void Add(string key, string group, string label, Func<RhinoObject, Value?> read, double epsilon = 0, int cost = 0,
      bool allowMissingReferences = DefaultAllowMissingReferences, Func<string, Value?>? parse = null)
    {
      Value?[] values;
      try { values = references.Select(read).ToArray(); }
      catch (Exception ex)
      {
        Log.Write("SelSame", $"Unavailable criterion {key}: {ex.Message}");
        return;
      }
      if (catalog || key == "part:name" || (references.Count > 0 &&
          (allowMissingReferences ? values.Any(value => value != null) : values.All(value => value != null))))
        criteria.Add(new Criterion(key, group, label, read, values, epsilon, cost, parse));
    }
    void Number(string key, string label, Func<RhinoObject, double?> read, double epsilon, string suffix = "") =>
      Add(key, "Geometry", label, obj => read(obj) is double value ? Value.Numeric(value, suffix) : null, epsilon, 10,
        parse: text => ParseNumber(text, suffix));

    Add("type", "Object", "Type", obj => Value.String(typeName(obj)));
    Add("layer", "Object", "Layer", obj => obj.Attributes.LayerIndex >= 0 && obj.Attributes.LayerIndex < doc.Layers.Count
      ? new Value(doc.Layers[obj.Attributes.LayerIndex].FullPath, Number: obj.Attributes.LayerIndex) : null,
      parse: text => doc.Layers.FirstOrDefault(layer => !layer.IsDeleted && string.Equals(layer.FullPath, text,
        StringComparison.OrdinalIgnoreCase)) is { } layer ? new Value(layer.FullPath, Number: layer.Index) : null);
    Add("name", "Object", "Name", obj => Value.String(obj.Attributes.Name ?? ""));
    Add("id", "Object", "Object ID", obj => Value.String(obj.Id.ToString()),
      parse: text => Guid.TryParse(text, out var id) ? Value.String(id.ToString()) : null);
    Add("group", "Object", "Group name", obj => obj.Attributes.GetGroupList() is { Length: > 0 } groups
      ? Value.String(string.Join("; ", groups.Select(index => doc.Groups[index].Name ?? ""))) : null);
    Add("color", "Object", "Display color", obj =>
    {
      var attributes = obj.Attributes;
      string source = attributes.ColorSource.ToString();
      string value = attributes.ColorSource == ObjectColorSource.ColorFromObject
        ? attributes.ObjectColor.ToArgb().ToString(CultureInfo.InvariantCulture) : source;
      string display = attributes.ColorSource == ObjectColorSource.ColorFromObject
        ? $"#{attributes.ObjectColor.R:X2}{attributes.ObjectColor.G:X2}{attributes.ObjectColor.B:X2}" : source;
      return new Value(display, source + ":" + value);
    }, parse: ParseColor);

    Number("length", "Length", obj => obj.Geometry is Curve curve ? curve.GetLength() : null, tolerance);
    Number("radius", "Radius", obj => Radius(obj.Geometry), tolerance);
    Number("diameter", "Diameter", obj => Radius(obj.Geometry) * 2, tolerance);
    Number("width", "Width", obj => Rectangle(obj.Geometry, rectangleSize)?.Width, tolerance);
    Number("height", "Height", obj => Rectangle(obj.Geometry, rectangleSize)?.Height ?? PrimitiveHeight(obj.Geometry), tolerance);
    Number("angle", "Angle", obj => obj.Geometry is Curve curve && !curve.IsClosed && curve.TryGetArc(out var arc)
      ? arc.AngleDegrees : null, RhinoMath.ToDegrees(doc.ModelAngleToleranceRadians), " deg");
    Number("major", "Major axis", obj => EllipseAxes(obj.Geometry)?.Major, tolerance);
    Number("minor", "Minor axis", obj => EllipseAxes(obj.Geometry)?.Minor, tolerance);
    Number("segments", "Segment count", obj => obj.Geometry is Curve curve && curve.TryGetPolyline(out var polyline)
      ? polyline.SegmentCount : null, 0);
    Add("closed", "Geometry", "Closed", obj => obj.Geometry is Curve curve ? Value.String(curve.IsClosed ? "Yes" : "No") : null,
      parse: text => ParseYesNo(text, false));
    Number("vertices", "Vertex count", obj => obj.Geometry is Mesh mesh ? mesh.Vertices.Count : null, 0);
    Number("faces", "Face count", obj => obj.Geometry switch
      { Mesh mesh => mesh.Faces.Count, Brep brep => brep.Faces.Count, _ => null }, 0);
    Number("text-height", "Text height", obj => obj.Geometry is AnnotationBase annotation ? annotation.TextHeight : null, tolerance);
    Number("dot-height", "Font height", obj => obj.Geometry is TextDot dot ? dot.FontHeight : null, 0);
    Add("text", "Geometry", "Text", obj => obj.Geometry switch
      { TextDot dot => Value.String(dot.Text), AnnotationBase annotation => Value.String(annotation.PlainText), _ => null });
    Add("dot-alternative", "Geometry", "Alternative text", obj => obj.Geometry is TextDot dot ? Value.String(dot.SecondaryText ?? "") : null);
    Add("dot-font", "Geometry", "Font", obj => obj.Geometry is TextDot dot ? Value.String(dot.FontFace) : null);
    Add("block", "Geometry", "Block definition", obj => obj is InstanceObject instance
      ? Value.String(instance.InstanceDefinition.Name) : null);

    foreach (string source in new[] { "attribute", "geometry", "part:custom" })
      foreach (string key in DynamicKeys(doc, references, source))
        criteria.Add(CreateDynamicCriterion(doc, references, source, key));

    var parts = new PartDataLookup(doc);
    Add("part:name", "Part", "Part name", obj => parts.Name(obj) is { } name ? Value.String(name) : null,
      cost: 20, allowMissingReferences: true);
    if (catalog || references.Count > 0 && references.All(obj => parts.Read(obj) != null))
    {
      void Part(string field, string label, Func<JsonNode?, Value?> convert, double epsilon = 0,
        Func<JsonObject, bool>? applies = null, Func<string, Value?>? parse = null) =>
        Add("nest:" + field, "vNest", label, obj => parts.Read(obj)?.Nest is { } data && (applies?.Invoke(data) ?? true)
          ? convert(data[field]) : null, epsilon, 2, parse: parse);
      void Boolean(string field, string label) => Part(field, label,
        node => node == null ? new Value("Default") : new Value(node.GetValue<bool>() ? "Yes" : "No", node.ToJsonString()),
        parse: text => ParseYesNo(text, true));
      void Numeric(string field, string label, double epsilon = 0, double offset = 0,
        Func<JsonObject, bool>? applies = null) => Part(field, label,
        node => node == null ? new Value("Default") : Value.Numeric(PartDataStore.FiniteNumber(node, field) + offset), epsilon, applies,
        text => IsDefault(text) ? new Value("Default") : ParseNumber(text));
      void Choice(string field, string label, string[] choices) => Part(field, label,
        node => node == null ? new Value("Default") : new Value(choices[node.GetValue<int>()], Number: node.GetValue<int>()),
        parse: text => ParseChoice(text, choices));
      void Vector(string field, string label) => Part(field, label, node =>
      {
        if (node == null) return new Value("Default");
        var values = node.AsArray().Select(item => PartDataStore.FiniteNumber(item, field)).ToArray();
        return new Value(string.Join(", ", values.Select(value => value.ToString("G8", CultureInfo.CurrentCulture))),
          string.Join(",", values.Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
      }, parse: ParseVector);

      Boolean("Included", "Included");
      Numeric("Copies", "Copies");
      Part("Material", "Material", node => node == null ? new Value("Default") : Value.String(PartDataStore.Text(node)),
        parse: text => IsDefault(text) ? new Value("Default") : Value.String(text));
      Choice("Grain", "Grain", new[] { "Auto", "Force", "On", "Off" });
      Boolean("Obey", "Obey grain");
      Choice("Rotation", "Rotation", new[] { "No rotation", "Any rotation", "45", "90", "180", "Custom" });
      Boolean("Reverse", "Reverse");
      Numeric("Step", "Step", 1e-8);
      Numeric("Deviation", "Deviation", 1e-8);
      Boolean("Flip", "Flip");
      Boolean("ManualRotation", "Manual angle");
      if (catalog || references.All(obj => parts.Read(obj)?.Nest is { } data
        && (PartDataStore.Boolean(data, "ManualRotation", false) == true || PartDataStore.Boolean(data, "Fixed", false) == true)))
        Numeric("Angle", "Angle", 1e-8, applies: data => PartDataStore.Boolean(data, "ManualRotation", false) == true
          || PartDataStore.Boolean(data, "Fixed", false) == true);
      Boolean("Fixed", "Fixed");
      if (catalog || references.All(obj => parts.Read(obj)?.Nest is { } data && PartDataStore.Boolean(data, "Fixed", false) == true))
      {
        bool Fixed(JsonObject data) => PartDataStore.Boolean(data, "Fixed", false) == true;
        Numeric("Table", "Table", offset: 1, applies: Fixed);
        Numeric("X", "X", tolerance, applies: Fixed);
        Numeric("Y", "Y", tolerance, applies: Fixed);
      }
      Boolean("Flipped", "Flipped");
      Vector("Guide", "Grain guide");
      Vector("GuideFrom", "Guide start");
      Vector("GuideTo", "Guide end");
      foreach (var field in new[] { (Key: "Width", Label: "Material width"), (Key: "Gap", Label: "Material gap"),
        (Key: "GrainDeviation", Label: "Material deviation") })
        Add("nest:Recipe:" + field.Key, "vNest Material", field.Label, obj =>
          parts.Read(obj)?.Nest?["Recipe"] is JsonObject recipe && recipe[field.Key] is { } node
            ? Value.Numeric(PartDataStore.FiniteNumber(node, field.Key)) : null,
          field.Key == "GrainDeviation" ? 1e-8 : tolerance, 2, parse: text => ParseNumber(text));
      Add("nest:Recipe:Grain", "vNest Material", "Material grain", obj =>
        parts.Read(obj)?.Nest?["Recipe"] is JsonObject recipe
          ? recipe["Grain"] is { } node
            ? new Value(new[] { "Auto", "Force", "On", "Off" }[node.GetValue<int>()], Number: node.GetValue<int>())
            : new Value("Default") : null, cost: 2,
        parse: text => ParseChoice(text, new[] { "Auto", "Force", "On", "Off" }));
      foreach (var field in new[] { (Key: "ObeyGrain", Label: "Material obey grain"), (Key: "AllowReverse", Label: "Material reverse") })
        Add("nest:Recipe:" + field.Key, "vNest Material", field.Label, obj =>
          parts.Read(obj)?.Nest?["Recipe"] is JsonObject recipe
            ? recipe[field.Key] is { } node ? new Value(node.GetValue<bool>() ? "Yes" : "No", node.ToJsonString())
              : new Value("Default") : null, cost: 2, parse: text => ParseYesNo(text, true));
    }
    return criteria;
  }

  internal static IReadOnlyList<RhinoObject> FindMatches(IEnumerable<RhinoObject> candidates,
    IReadOnlyList<Criterion> criteria, int referenceCount)
  {
    if (criteria.Count == 0) return Array.Empty<RhinoObject>();
    var ordered = criteria.OrderBy(criterion => criterion.Cost).ToArray();
    var matches = new List<RhinoObject>();
    foreach (var obj in candidates)
    {
      if (obj.IsDeleted || obj.IsHidden || !obj.IsSelectable(true, false, false, false)) continue;
      // Every checked criterion must match the same reference, never a mixture of references.
      for (int reference = 0; reference < Math.Max(1, referenceCount); reference++)
        if (ordered.All(criterion => criterion.Matches(obj, reference)))
        { matches.Add(obj); break; }
    }
    return matches;
  }

  internal static string[] DynamicKeys(RhinoDoc doc, IEnumerable<RhinoObject> objects, string source)
  {
    var parts = new PartDataLookup(doc);
    return objects.SelectMany(obj => source switch
      {
        "attribute" => obj.Attributes.GetUserStrings().AllKeys.OfType<string>(),
        "geometry" => obj.Geometry.GetUserStrings().AllKeys.OfType<string>(),
        "part:custom" => parts.Read(obj)?.Custom?.Select(field => field.Key) ?? Enumerable.Empty<string>(),
        _ => Enumerable.Empty<string>()
      }).Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.CurrentCultureIgnoreCase).ToArray();
  }

  internal static Criterion CreateDynamicCriterion(RhinoDoc doc, IReadOnlyList<RhinoObject> references, string source, string key)
  {
    PartDataStore.ValidateKey(key);
    var parts = new PartDataLookup(doc);
    Value? Read(RhinoObject obj) => source switch
    {
      "attribute" => obj.Attributes.GetUserString(key) is { } value ? Value.String(value) : null,
      "geometry" => obj.Geometry.GetUserString(key) is { } value ? Value.String(value) : null,
      "part:custom" => parts.Read(obj)?.Custom?[key] is { } value ? Value.String(PartDataStore.Text(value)) : null,
      _ => null
    };
    return new Criterion(source + ":" + key, source switch
      { "attribute" => "Attribute User Text", "geometry" => "Geometry User Text", _ => "Part Custom Fields" },
      key, Read, references.Select(Read).ToArray(), cost: 2);
  }

  private static bool IsDefault(string text) => string.Equals(text, "Default", StringComparison.OrdinalIgnoreCase);

  private static Value? ParseNumber(string text, string suffix = "")
  {
    if (suffix.Length > 0 && text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^suffix.Length];
    return (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double number) ||
      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) ? Value.Numeric(number, suffix) : null;
  }

  private static Value? ParseYesNo(string text, bool json)
  {
    if (json && IsDefault(text)) return new Value("Default");
    bool? value = text.Trim().ToLowerInvariant() switch { "yes" or "true" => true, "no" or "false" => false, _ => null };
    return value == null ? null : new Value(value.Value ? "Yes" : "No", json ? (value.Value ? "true" : "false") : value.Value ? "Yes" : "No");
  }

  private static Value? ParseChoice(string text, string[] choices)
  {
    if (IsDefault(text)) return new Value("Default");
    int index = Array.FindIndex(choices, choice => string.Equals(choice, text, StringComparison.OrdinalIgnoreCase));
    return index < 0 ? null : new Value(choices[index], Number: index);
  }

  private static Value? ParseVector(string text)
  {
    if (IsDefault(text)) return new Value("Default");
    var values = text.Trim('[', ']', '(', ')').Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
      .Select(value => ParseNumber(value)?.Number).ToArray();
    if (values.Length != 3 || values.Any(value => value == null)) return null;
    return new Value(string.Join(", ", values.Select(value => value!.Value.ToString("G8", CultureInfo.CurrentCulture))),
      string.Join(",", values.Select(value => value!.Value.ToString("R", CultureInfo.InvariantCulture))));
  }

  private static Value? ParseColor(string text)
  {
    if (text.StartsWith("#") && text.Length == 7 && int.TryParse(text[1..], NumberStyles.HexNumber,
        CultureInfo.InvariantCulture, out int rgb))
      return new Value(text.ToUpperInvariant(), "ColorFromObject:" + unchecked((int)(0xff000000u | (uint)rgb)).ToString(CultureInfo.InvariantCulture));
    return Enum.TryParse<ObjectColorSource>(text, true, out var source) && source != ObjectColorSource.ColorFromObject
      ? new Value(source.ToString(), source + ":" + source) : null;
  }

  private static Surface? SurfaceOf(GeometryBase geometry) => geometry switch
    { Surface surface => surface, Brep brep when brep.Faces.Count == 1 => brep.Faces[0], _ => null };

  private static double? Radius(GeometryBase geometry)
  {
    if (geometry is Curve curve)
    {
      if (curve.TryGetCircle(out var circle)) return circle.Radius;
      if (curve.TryGetArc(out var arc)) return arc.Radius;
    }
    var surface = SurfaceOf(geometry);
    if (surface == null) return null;
    if (surface.TryGetSphere(out var sphere)) return sphere.Radius;
    if (surface.TryGetCylinder(out var cylinder)) return cylinder.Radius;
    return surface.TryGetCone(out var cone) ? cone.Radius : null;
  }

  private static double? PrimitiveHeight(GeometryBase geometry)
  {
    var surface = SurfaceOf(geometry);
    if (surface == null) return null;
    if (surface.TryGetCylinder(out var cylinder)) return Math.Abs(cylinder.TotalHeight);
    return surface.TryGetCone(out var cone) ? Math.Abs(cone.Height) : null;
  }

  private static (double Major, double Minor)? EllipseAxes(GeometryBase geometry)
  {
    if (geometry is not Curve curve || !curve.IsClosed || curve.TryGetCircle(out _) || !curve.TryGetEllipse(out var ellipse)) return null;
    return (Math.Max(ellipse.Radius1, ellipse.Radius2) * 2, Math.Min(ellipse.Radius1, ellipse.Radius2) * 2);
  }

  private static (double Width, double Height)? Rectangle(GeometryBase geometry,
    Func<Curve, (double Width, double Height)?> rectangleSize)
  {
    if (geometry is Curve curve) return rectangleSize(curve);
    if (geometry is Brep brep && brep.Faces.Count == 1 && brep.Faces[0].OuterLoop is { } loop)
    {
      using var boundary = loop.To3dCurve();
      if (boundary != null) return rectangleSize(boundary);
    }
    return null;
  }
}
private sealed record FilterSetting(SimilarObjectMatcher.Criterion Criterion, bool Enabled);

private sealed class SelSameDialog : Dialog<bool>
{
  private readonly RhinoDoc _doc;
  private readonly IReadOnlyList<RhinoObject> _references;
  private readonly IReadOnlyList<SimilarObjectMatcher.Criterion> _catalog;
  private readonly int _referenceCount;
  private readonly Action<IReadOnlyList<RhinoObject>> _preview;
  private readonly List<(SimilarObjectMatcher.Criterion Criterion, CheckBox Check)> _checks = new();
  private readonly List<ContextMenu> _valueMenus = new();
  private readonly List<Bitmap> _valueImages = new();
  private readonly UITimer _timer = new() { Interval = PreviewDelaySeconds };
  private readonly Label _status = new() { Text = "...", Wrap = WrapMode.Word };
  private readonly Button _select = new() { Text = "Select", Enabled = false };
  private readonly Button _add = new() { Text = "Add to Selection", Enabled = false };
  private readonly StackLayout _rows = new() { Spacing = RowSpacing, HorizontalContentAlignment = HorizontalAlignment.Stretch };
  private IReadOnlyList<RhinoObject>? _candidates;
  private bool _stopped;

  internal IReadOnlyList<RhinoObject> Matches { get; private set; } = Array.Empty<RhinoObject>();
  internal bool ReferenceRequested { get; private set; }
  internal bool AddToSelection { get; private set; } = DefaultAddToSelection;

  internal SelSameDialog(RhinoDoc doc, IReadOnlyList<RhinoObject> references,
    Func<RhinoObject, string> typeName, Func<Curve, (double Width, double Height)?> rectangleSize,
    Action<IReadOnlyList<RhinoObject>> preview)
  {
    _doc = doc; _references = references; _referenceCount = references.Count; _preview = preview;
    _catalog = SimilarObjectMatcher.CreateCriteria(doc, references, typeName, rectangleSize, true);
    Title = "SelSame";
    Resizable = true;
    ClientSize = InitialDialogSize;
    MinimumSize = MinimumDialogSize;
    Padding = new Padding(DialogPadding);
    var initial = references.Count == 0 ? _catalog.Where(criterion => criterion.Key is "type" or "layer" or "name" or "part:name")
      : SimilarObjectMatcher.CreateCriteria(doc, references, typeName, rectangleSize);
    RestoreFilters(initial.Select(criterion => new FilterSetting(criterion,
      references.Count > 0 && DefaultMatchType && criterion.Key == "type")).ToList(), false);
    var addFilter = new Button { Text = "Add Filter" };
    addFilter.Click += (_, _) => EditCondition(null);
    var reference = new Button { Text = "Pick Reference" };
    reference.Click += (_, _) => { ReferenceRequested = true; Close(false); };
    var cancel = new Button { Text = "Cancel" };
    cancel.Click += (_, _) => Close(false);
    _select.Click += (_, _) => Confirm(false);
    _add.Click += (_, _) => Confirm(true);
    DefaultButton = _select;
    AbortButton = cancel;
    Content = new TableLayout
    {
      Spacing = DialogSpacing,
      Rows =
      {
        new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = ButtonSpacing,
          Items = { addFilter, reference } }),
        new TableRow(new Scrollable { Content = _rows, Border = BorderType.None,
          ExpandContentWidth = true, ExpandContentHeight = false }) { ScaleHeight = true },
        new TableRow(_status),
        new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = ButtonSpacing,
          Items = { new StackLayoutItem(null, true), _select, _add, cancel } })
      }
    };
    _timer.Elapsed += (_, _) => { _timer.Stop(); RefreshPreview(); };
    LoadComplete += (_, _) => QueuePreview();
    Closed += (_, _) => StopPreview();
  }

  private Control CreateValueSelector(SimilarObjectMatcher.Criterion criterion, CheckBox check)
  {
    var arrow = new Bitmap(ValueArrowSize.Width, ValueArrowSize.Height, PixelFormat.Format32bppRgba);
    using (var graphics = new Graphics(arrow))
      graphics.FillPolygon(SystemColors.ControlText, new PointF(0, 0),
        new PointF(ValueArrowSize.Width - 1, 0), new PointF((ValueArrowSize.Width - 1) / 2f, ValueArrowSize.Height - 1));
    _valueImages.Add(arrow);
    var button = new Button { Image = arrow, ImagePosition = ButtonImagePosition.Right };
    var menu = new ContextMenu();
    _valueMenus.Add(menu);
    var all = new ButtonMenuItem { Text = "Select All" };
    var none = new ButtonMenuItem { Text = "Clear" };
    var edit = new ButtonMenuItem { Text = "Edit Values..." };
    menu.Items.Add(all);
    menu.Items.Add(none);
    menu.Items.Add(new SeparatorMenuItem());
    var choices = new List<CheckMenuItem>();
    bool syncing = false;

    void Refresh(bool changed)
    {
      button.Text = ShortText(criterion.Display, ValueLimit);
      button.ToolTip = criterion.Label + ": " + string.Join(", ", criterion.Values
        .Where((_, index) => criterion.IsValueSelected(index)).Select(value => value.Display));
      if (!changed) return;
      check.Checked = true;
      QueuePreview();
    }

    void SetAll(bool selected)
    {
      criterion.SetAllValuesSelected(selected);
      syncing = true;
      try { foreach (var item in choices) item.Checked = selected; }
      finally { syncing = false; }
      Refresh(true);
    }

    for (int index = 0; index < criterion.Values.Count; index++)
    {
      int choice = index;
      var item = new CheckMenuItem { Text = criterion.Values[index].Display,
        Checked = criterion.IsValueSelected(index) };
      item.CheckedChanged += (_, _) =>
      {
        if (syncing) return;
        criterion.SetValueSelected(choice, item.Checked);
        Refresh(true);
      };
      choices.Add(item);
      menu.Items.Add(item);
    }
    all.Click += (_, _) => SetAll(true);
    none.Click += (_, _) => SetAll(false);
    menu.Items.Add(new SeparatorMenuItem());
    menu.Items.Add(edit);
    edit.Click += (_, _) => Application.Instance.AsyncInvoke(() => EditCondition(criterion));
    menu.Opening += (_, _) =>
    {
      if (menu.ControlObject is System.Windows.Controls.ContextMenu popup)
        popup.MaxHeight = ValueMenuMaximumHeight;
      foreach (var item in menu.Items)
        if (item.ControlObject is System.Windows.Controls.MenuItem native)
          native.StaysOpenOnClick = item != edit;
    };
    button.Click += (_, _) =>
    {
      if (criterion.Values.Count == 0) EditCondition(criterion);
      else menu.Show(button, new PointF(0, button.Height));
    };
    Refresh(false);
    if (criterion.Values.Count == 0) button.Text = "Set values...";
    return button;
  }

  internal List<FilterSetting> CaptureFilters() => _checks.Select(item => new FilterSetting(item.Criterion, item.Check.Checked == true)).ToList();

  internal void RestoreFilters(List<FilterSetting> filters, bool referenceChanged)
  {
    if (referenceChanged)
    {
      var current = CaptureFilters();
      foreach (var saved in filters)
      {
        int index = current.FindIndex(item => item.Criterion.Key == saved.Criterion.Key);
        if (saved.Criterion.Edited)
        { if (index >= 0) current[index] = saved; else current.Add(saved); }
        else if (index >= 0) current[index] = current[index] with { Enabled = saved.Enabled };
      }
      filters = current;
    }
    _checks.Clear();
    _rows.Items.Clear();
    foreach (var menu in _valueMenus) menu.Dispose();
    foreach (var image in _valueImages) image.Dispose();
    _valueMenus.Clear(); _valueImages.Clear();
    foreach (var group in filters.GroupBy(item => item.Criterion.Group))
    {
      _rows.Items.Add(new Label { Text = group.Key });
      _rows.Items.Add(new Panel { Height = 1, BackgroundColor = SeparatorColor });
      foreach (var setting in group)
      {
        var criterion = setting.Criterion;
        var check = new CheckBox { Text = ShortText(criterion.Label, LabelLimit), Width = LabelWidth,
          ToolTip = criterion.Label, Checked = setting.Enabled };
        var edit = new Button { Text = "...", Width = IconButtonWidth, ToolTip = "Edit filter values or field key" };
        edit.Click += (_, _) => EditCondition(criterion);
        var remove = new Button { Text = "-", Width = IconButtonWidth, ToolTip = "Remove filter" };
        remove.Click += (_, _) =>
        {
          RestoreFilters(CaptureFilters().Where(item => item.Criterion != criterion).ToList(), false);
          QueuePreview();
        };
        _checks.Add((criterion, check));
        check.CheckedChanged += (_, _) => QueuePreview();
        _rows.Items.Add(new TableLayout { Spacing = CellSpacing,
          Rows = { new TableRow(check, new TableCell(CreateValueSelector(criterion, check), true), edit, remove) } });
      }
    }
  }

  private void EditCondition(SimilarObjectMatcher.Criterion? criterion)
  {
    if (_stopped) return;
    _timer.Stop();
    using var editor = new FilterEditor(_doc, _references, _catalog, criterion);
    if (!editor.ShowModal(this) || editor.Condition == null) { QueuePreview(); return; }
    var filters = CaptureFilters();
    if (criterion != null) filters.RemoveAll(item => item.Criterion == criterion);
    int existing = filters.FindIndex(item => item.Criterion.Key == editor.Condition.Key);
    var replacement = new FilterSetting(editor.Condition, true);
    if (existing >= 0) filters[existing] = replacement; else filters.Add(replacement);
    RestoreFilters(filters, false);
    QueuePreview();
  }

  private static string ShortText(string text, int length)
  {
    string singleLine = text.Replace("\r", " ").Replace("\n", " ");
    return singleLine.Length <= length ? singleLine : singleLine[..(length - 3)] + "...";
  }

  private void QueuePreview()
  {
    if (_stopped) return;
    _timer.Stop();
    _status.Text = "...";
    _select.Enabled = false;
    _add.Enabled = false;
    _timer.Start();
  }

  private IReadOnlyList<SimilarObjectMatcher.Criterion> CheckedCriteria() =>
    _checks.Where(item => item.Check.Checked == true).Select(item => item.Criterion).ToArray();

  private IReadOnlyList<RhinoObject> Find(IReadOnlyList<SimilarObjectMatcher.Criterion> criteria)
  {
    _candidates ??= _doc.Objects.GetObjectList(new ObjectEnumeratorSettings
    {
      NormalObjects = true, LockedObjects = false, HiddenObjects = false, IdefObjects = false,
      DeletedObjects = false, ReferenceObjects = false, IncludeGrips = false, IncludeLights = false,
      VisibleFilter = true
    }).ToArray();
    return SimilarObjectMatcher.FindMatches(_candidates, criteria, _referenceCount);
  }

  private void RefreshPreview()
  {
    if (_stopped) return;
    try
    {
      var watch = Stopwatch.StartNew();
      var criteria = CheckedCriteria();
      var matches = Find(criteria);
      _preview(matches);
      _status.Text = matches.Count + " matches";
      _add.Enabled = _select.Enabled = criteria.Count > 0 && matches.Count > 0;
      if (watch.ElapsedMilliseconds >= SlowPreviewMilliseconds)
        Log.Write("SelSame", $"Preview: candidates={_candidates?.Count}, matches={matches.Count}, criteria={string.Join(",", criteria.Select(item => item.Key))}, elapsed={watch.ElapsedMilliseconds}ms");
    }
    catch (Exception ex)
    {
      Log.Write("SelSame", $"Preview failed: {ex}");
      _preview(Array.Empty<RhinoObject>());
      _status.Text = "Preview unavailable";
      _select.Enabled = false;
      _add.Enabled = false;
    }
  }

  private void Confirm(bool add)
  {
    _timer.Stop();
    try
    {
      Matches = Find(CheckedCriteria());
      if (Matches.Count > 0) { AddToSelection = add; Close(true); }
      else { _status.Text = "0 matches"; _add.Enabled = _select.Enabled = false; }
    }
    catch (Exception ex)
    {
      Log.Write("SelSame", $"Matching failed: {ex}");
      _status.Text = "Matching unavailable";
      _select.Enabled = false;
      _add.Enabled = false;
    }
  }

  internal void StopPreview()
  {
    if (_stopped) return;
    _stopped = true;
    _timer.Stop();
    _timer.Dispose();
    foreach (var menu in _valueMenus) menu.Dispose();
    foreach (var image in _valueImages) image.Dispose();
    _preview(Array.Empty<RhinoObject>());
  }
}

private sealed class FilterEditor : Dialog<bool>
{
  private readonly DropDown _property = new();
  private readonly ComboBox _key = new();
  private readonly Label _keyLabel = new() { Text = "Key" };
  private readonly Label _error = new() { Wrap = WrapMode.Word };
  private readonly GridView _values = new() { AllowMultipleSelection = true };
  private readonly List<GridItem> _items = new();
  private readonly IReadOnlyList<SimilarObjectMatcher.Criterion> _catalog;
  private readonly RhinoDoc _doc;
  private readonly IReadOnlyList<RhinoObject> _references;
  private readonly SimilarObjectMatcher.Criterion? _original;
  private readonly string[] _sources = { "attribute", "geometry", "part:custom" };
  private bool _updating;

  internal SimilarObjectMatcher.Criterion? Condition { get; private set; }

  internal FilterEditor(RhinoDoc doc, IReadOnlyList<RhinoObject> references,
    IReadOnlyList<SimilarObjectMatcher.Criterion> catalog, SimilarObjectMatcher.Criterion? original)
  {
    _doc = doc; _references = references; _original = original;
    _catalog = catalog.Where(item => !_sources.Any(source => item.Key.StartsWith(source + ":", StringComparison.Ordinal))).ToArray();
    Title = original == null ? "Add Filter" : "Edit Filter";
    Padding = new Padding(DialogPadding);
    ClientSize = FilterEditorSize; Resizable = true;
    _property.DataStore = _catalog.Select(item => item.Group + ": " + item.Label)
      .Concat(new[] { "Attribute User Text", "Geometry User Text", "Part Custom Field" }).ToArray();
    _values.Columns.Add(new GridColumn { HeaderText = "Value", DataCell = new TextBoxCell(0), Editable = true, Expand = true });
    var add = new Button { Text = "+", Width = IconButtonWidth, ToolTip = "Add value" };
    var remove = new Button { Text = "-", Width = IconButtonWidth, ToolTip = "Remove selected values" };
    add.Click += (_, _) => { _items.Add(new GridItem { Values = new object[] { "" } }); RefreshItems(); _values.SelectedRow = _items.Count - 1; };
    void Remove()
    {
      foreach (var item in _values.SelectedItems.OfType<GridItem>().ToArray()) _items.Remove(item);
      RefreshItems();
    }
    remove.Click += (_, _) => Remove();
    _values.KeyDown += (_, args) =>
    {
      if (args.Key == Keys.Delete && !_editing) { Remove(); args.Handled = true; }
    };
    _values.CellEditing += (_, _) => _editing = true;
    _values.CellEdited += (_, _) => _editing = false;
    var apply = new Button { Text = "Apply" };
    var cancel = new Button { Text = "Cancel" };
    apply.Click += (_, _) => Apply();
    cancel.Click += (_, _) => Close(false);
    DefaultButton = apply; AbortButton = cancel;
    Content = new TableLayout { Spacing = DialogSpacing, Rows =
    {
      new TableRow(new TableLayout { Spacing = CellSpacing,
        Rows = { new TableRow(new Label { Text = "Property", Width = LabelWidth }, new TableCell(_property, true)) } }),
      new TableRow(new TableLayout { Spacing = CellSpacing,
        Rows = { new TableRow(_keyLabel, new TableCell(_key, true)) } }),
      new TableRow(_values) { ScaleHeight = true },
      new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = ButtonSpacing, Items = { add, remove } }),
      new TableRow(_error),
      new TableRow(new StackLayout { Orientation = Orientation.Horizontal, Spacing = ButtonSpacing,
        Items = { new StackLayoutItem(null, true), apply, cancel } })
    } };
    _keyLabel.Width = LabelWidth;
    _property.SelectedIndexChanged += (_, _) => Populate();
    int dynamicIndex = original == null ? -1 : Array.FindIndex(_sources, source => original.Key.StartsWith(source + ":", StringComparison.Ordinal));
    _property.SelectedIndex = dynamicIndex >= 0 ? _catalog.Count + dynamicIndex
      : Math.Max(0, _catalog.ToList().FindIndex(item => item.Key == original?.Key));
    _property.Enabled = original == null;
    if (original != null)
    {
      _updating = true;
      if (dynamicIndex >= 0) _key.Text = original.Key[(_sources[dynamicIndex].Length + 1)..];
      _updating = false;
      SetItems(original.Values);
    }
    _key.TextChanged += (_, _) =>
    {
      if (!_updating && _property.SelectedIndex >= _catalog.Count)
      {
        try { SetItems(SimilarObjectMatcher.CreateDynamicCriterion(_doc, _references,
          _sources[_property.SelectedIndex - _catalog.Count], _key.Text).Values); }
        catch (InvalidOperationException) { SetItems(Array.Empty<SimilarObjectMatcher.Value>()); }
      }
    };
  }

  private bool _editing;
  private void RefreshItems() { _values.DataStore = null; _values.DataStore = _items.ToArray(); }
  private void SetItems(IReadOnlyList<SimilarObjectMatcher.Value> values)
  {
    _items.Clear();
    foreach (var value in values) _items.Add(new GridItem { Values = new object[] { value.Display == "(empty)" ? "" : value.Display } });
    if (_items.Count == 0) _items.Add(new GridItem { Values = new object[] { "" } });
    RefreshItems();
  }

  private void Populate()
  {
    bool dynamic = _property.SelectedIndex >= _catalog.Count;
    _key.Visible = _keyLabel.Visible = dynamic;
    if (!dynamic) { SetItems(_catalog[_property.SelectedIndex].Values); return; }
    _updating = true;
    _key.DataStore = SimilarObjectMatcher.DynamicKeys(_doc,
      _doc.Objects.GetObjectList(ObjectType.AnyObject), _sources[_property.SelectedIndex - _catalog.Count]);
    _key.Text = "";
    _updating = false;
    SetItems(Array.Empty<SimilarObjectMatcher.Value>());
  }

  private void Apply()
  {
    try
    {
      var condition = _property.SelectedIndex < _catalog.Count
        ? _original ?? _catalog[_property.SelectedIndex]
        : SimilarObjectMatcher.CreateDynamicCriterion(_doc, _references,
          _sources[_property.SelectedIndex - _catalog.Count], _key.Text);
      if (_items.Count == 0) { _error.Text = "Add at least one value."; return; }
      if (!condition.TrySetValues(_items.Select(item => Convert.ToString(item.Values[0]) ?? "").ToArray(), out string error))
      { _error.Text = error; return; }
      Condition = condition;
      Close(true);
    }
    catch (Exception ex) { _error.Text = ex.Message; }
  }
}

  // Metadata and labels are resolved once per group/part, never through Boolean region reconstruction.
  private sealed class PartDataLookup(RhinoDoc doc)
  {
    internal sealed record Data(JsonObject? Nest, JsonObject? Custom, string? Name);
    private sealed record Entry(bool Saved, Data Data);
    private readonly Dictionary<string, Entry> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<int, RhinoObject[]> _members = new();
    private readonly Dictionary<int, int> _sizes = new();
    private readonly Dictionary<Guid, (string Key, string Name)?> _identities = new();
    private readonly HashSet<int> _resolved = new();
    private readonly Data _defaults = new(PartDataStore.Defaults(), new JsonObject(), null);

    internal Data? Read(RhinoObject obj)
    {
      if (!Candidate(obj)) return null;
      foreach (int group in Groups(obj))
      {
        var saved = Record(GroupKey(group));
        if (saved.Saved) return saved.Data;
      }
      var boundary = Record("boundary-" + obj.Id.ToString("N"));
      if (boundary.Saved) return boundary.Data;
      string? source = obj.Attributes.GetUserString(SourceSettingsKey);
      if (IsKey(source) && Record(source!).Saved) return Record(source!).Data;
      return Identity(obj) is { } identity ? Record(identity.Key).Data : null;
    }

    internal string? Name(RhinoObject obj)
    {
      if (!Candidate(obj)) return null;
      // A local saved alias needs no geometry or group-member scan.
      foreach (int group in Groups(obj))
        if (Record(GroupKey(group)).Data.Name is { } name) return name;
      if (Record("boundary-" + obj.Id.ToString("N")).Data.Name is { } ownName) return ownName;
      if (Identity(obj) is { } identity)
        return Record(identity.Key).Data.Name ?? identity.Name;
      string? source = obj.Attributes.GetUserString(SourceSettingsKey);
      return IsKey(source) ? Record(source!).Data.Name : null;
    }

    private IEnumerable<int> Groups(RhinoObject obj) => (obj.Attributes.GetGroupList() ?? Array.Empty<int>())
      .Where(index => index >= 0 && index < doc.Groups.Count && !doc.Groups[index].IsDeleted)
      .OrderBy(Size).ThenBy(index => index);

    private int Size(int group)
    {
      if (!_sizes.TryGetValue(group, out int size)) _sizes[group] = size = doc.Groups.GroupObjectCount(group);
      return size;
    }

    private string GroupKey(int group) => "group-" + doc.Groups[group].Id.ToString("N");

    private RhinoObject[] Members(int group)
    {
      if (!_members.TryGetValue(group, out var members))
        _members[group] = members = doc.Groups.GroupMembers(group) ?? Array.Empty<RhinoObject>();
      return members;
    }

    private (string Key, string Name)? Identity(RhinoObject obj)
    {
      if (_identities.TryGetValue(obj.Id, out var identity)) return identity;
      foreach (int group in Groups(obj))
      {
        if (_resolved.Add(group)) ResolveGroup(group);
        if (_identities.TryGetValue(obj.Id, out identity)) return identity;
      }
      if (obj.Geometry is Curve { IsClosed: true } curve && curve.TryGetPlane(out var plane, doc.ModelAbsoluteTolerance))
      {
        string key = "boundary-" + obj.Id.ToString("N");
        return _identities[obj.Id] = (key, DisplayName(new[] { obj }, obj, plane, ""));
      }
      return _identities[obj.Id] = null;
    }

    private void ResolveGroup(int group)
    {
      var all = Members(group);
      // An enclosing nest table is not a part, even though its border is closed.
      if (all.Any(obj => NestTableMetadata.IsDecoration(doc, obj))) return;
      var members = all.Where(Candidate).ToArray();
      // A labeled part remains identifiable when its cut edges are segmented or slightly nonplanar.
      var labels = members.Where(obj => obj.Geometry is TextEntity text && !string.IsNullOrWhiteSpace(text.PlainText)
        && NestWorkflow.IsPartNameLabel(obj.Attributes)).ToArray();
      if (labels.Length == 1)
      {
        var label = labels[0];
        var identity = (GroupKey(group), DisplayName(members, label, ((TextEntity)label.Geometry).Plane, doc.Groups[group].Name));
        foreach (var member in members)
          if (!_identities.TryGetValue(member.Id, out var existing) || existing == null) _identities[member.Id] = identity;
        return;
      }
      var outlines = members.Where(obj => obj.Geometry is Curve { IsClosed: true } curve
        && curve.TryGetPlane(out _, doc.ModelAbsoluteTolerance)).ToArray();
      var cutting = outlines.Where(obj => !OnReferenceLayer(obj)).ToArray();
      if (cutting.Length > 0) outlines = cutting;
      double tolerance = Math.Max(doc.ModelAbsoluteTolerance * DistanceToleranceFactor, MinimumDistanceTolerance);
      var outer = outlines.Where(obj =>
      {
        var curve = (Curve)obj.Geometry;
        curve.TryGetPlane(out var plane, tolerance);
        return !outlines.Any(other => other.Id != obj.Id
          && Curve.PlanarClosedCurveRelationship((Curve)other.Geometry, curve, plane, tolerance) == RegionContainment.BInsideA);
      }).ToArray();
      if (outer.Length == 0)
      {
        // Segmented outlines are joined only once for the entire group, not once per member.
        var joined = members.Where(obj => obj.Geometry is Curve).GroupBy(obj => obj.Attributes.LayerIndex)
          .SelectMany(layer => Curve.JoinCurves(layer.Select(obj => (Curve)obj.Geometry), tolerance)).ToArray();
        try
        {
          var closed = joined.Where(curve => curve.IsClosed && curve.TryGetPlane(out _, tolerance)).ToArray();
          var outerJoined = closed.Where(curve =>
          {
            curve.TryGetPlane(out var plane, tolerance);
            return !closed.Any(other => other != curve && Curve.PlanarClosedCurveRelationship(other, curve, plane, tolerance)
              == RegionContainment.BInsideA);
          }).ToArray();
          if (outerJoined.Length != 1) return;
          outerJoined[0].TryGetPlane(out var plane, tolerance);
          var boundary = members.FirstOrDefault(obj => obj.Geometry is Curve);
          if (boundary == null) return;
          var identity = (GroupKey(group), DisplayName(members, boundary, plane, doc.Groups[group].Name));
          foreach (var member in members)
            if (!_identities.TryGetValue(member.Id, out var existing) || existing == null) _identities[member.Id] = identity;
        }
        finally { foreach (var curve in joined) curve.Dispose(); }
        return;
      }
      var assigned = outer.ToDictionary(obj => obj.Id, _ => new List<RhinoObject>());
      foreach (var member in members)
      {
        if (_identities.TryGetValue(member.Id, out var existing) && existing != null) continue; // Preserve an already resolved inner part.
        var point = member.Geometry is TextEntity text ? text.Plane.Origin : member.Geometry.GetBoundingBox(false).Center;
        var boundary = outer.Length == 1 ? outer[0] : outer.FirstOrDefault(obj => obj.Id == member.Id) ?? outer
          .Where(obj =>
          {
            var curve = (Curve)obj.Geometry;
            curve.TryGetPlane(out var plane, tolerance);
            return curve.Contains(point, plane, tolerance) is PointContainment.Inside or PointContainment.Coincident;
          }).OrderBy(obj => obj.Geometry.GetBoundingBox(false).Diagonal.Length).FirstOrDefault();
        if (boundary != null) assigned[boundary.Id].Add(member);
      }
      foreach (var boundary in outer)
      {
        var part = assigned[boundary.Id];
        ((Curve)boundary.Geometry).TryGetPlane(out var plane, tolerance);
        string key = outer.Length == 1 ? GroupKey(group) : "boundary-" + boundary.Id.ToString("N");
        var identity = (key, DisplayName(part, boundary, plane, outer.Length == 1 ? doc.Groups[group].Name : ""));
        foreach (var member in part) _identities[member.Id] = identity;
      }
    }

    private bool OnReferenceLayer(RhinoObject obj) => obj.Attributes.LayerIndex >= 0 && obj.Attributes.LayerIndex < doc.Layers.Count
      && string.Equals(doc.Layers[obj.Attributes.LayerIndex].Name, ReferenceLayer, StringComparison.OrdinalIgnoreCase);

    private string DisplayName(IEnumerable<RhinoObject> members, RhinoObject boundary, Plane plane, string? groupName)
    {
      var label = members.Where(obj => obj.Geometry is TextEntity text && !string.IsNullOrWhiteSpace(text.PlainText)
          && NestWorkflow.IsPartNameLabel(obj.Attributes))
        .OrderByDescending(OnReferenceLayer).ThenByDescending(obj =>
        {
          var bounds = obj.Geometry.GetBoundingBox(plane);
          return (bounds.Max.X - bounds.Min.X) * (bounds.Max.Y - bounds.Min.Y);
        }).FirstOrDefault();
      string name = label?.Geometry is TextEntity text ? text.PlainText : groupName ?? "";
      if (string.IsNullOrWhiteSpace(name)) name = boundary.Attributes.Name ?? "";
      if (string.IsNullOrWhiteSpace(name)) name = "Part " + boundary.Id.ToString("N")[..8];
      return System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
    }

    private static bool Candidate(RhinoObject obj) => !obj.IsDeleted && !NestTableMetadata.HasDecorationRole(obj.Attributes)
      && obj.Attributes.Name != "NestTable";

    private static bool IsKey(string? key) => key != null &&
      (key.StartsWith("group-", StringComparison.Ordinal) && Guid.TryParse(key[6..], out _)
        || key.StartsWith("boundary-", StringComparison.Ordinal) && Guid.TryParse(key[9..], out _));

    private Entry Record(string key)
    {
      if (_records.TryGetValue(key, out var cached)) return cached;
      string? nestRaw = doc.Strings.GetValue(NestSection, key);
      string? customRaw = doc.Strings.GetValue(CustomSection, key);
      string? name = doc.Strings.GetValue(NamesSection, key);
      if (nestRaw == null && customRaw == null && name == null) return _records[key] = new(false, _defaults);
      JsonObject? nest = null, custom = null;
      try { nest = PartDataStore.ReadNest(nestRaw); }
      catch (Exception ex) { Log.Write("vSelSame", $"Invalid nesting data for {key}: {ex.Message}"); }
      try { custom = PartDataStore.ReadCustom(customRaw); }
      catch (Exception ex) { Log.Write("vSelSame", $"Invalid custom fields for {key}: {ex.Message}"); }
      if (string.IsNullOrWhiteSpace(name) || name.Length > MaximumPartNameCharacters) name = null;
      return _records[key] = new(true, new(nest, custom, name));
    }
  }

  private static class PartDataStore
  {
  internal static JsonObject Defaults() => new()
  {
    ["Version"] = SchemaVersion, ["Copies"] = DefaultPartCopies, ["Included"] = DefaultPartIncluded,
    ["Fixed"] = DefaultPartFixed, ["ManualRotation"] = DefaultPartManualRotation, ["Flipped"] = DefaultPartFlipped,
    ["Flip"] = null, ["Grain"] = null, ["Rotation"] = null, ["Step"] = null,
    ["Obey"] = null, ["Reverse"] = null, ["Deviation"] = null,
    ["Angle"] = DefaultPartAngle, ["Table"] = DefaultPartTable, ["X"] = DefaultPartCoordinate, ["Y"] = DefaultPartCoordinate,
    ["Material"] = null, ["Recipe"] = null, ["Guide"] = null,
    ["GuideFrom"] = null, ["GuideTo"] = null
  };

  internal static JsonObject ReadNest(string? json)
  {
    if (string.IsNullOrEmpty(json))
      return Defaults();
    if (json.Length > MaximumNestCharacters)
      throw new InvalidOperationException("The saved nesting record is too large.");
    var data = JsonNode.Parse(json) as JsonObject
      ?? throw new InvalidOperationException("The nesting record is not a JSON object.");
    ValidateNest(data);
    return data;
  }

  internal static JsonObject ReadCustom(string? json)
  {
    if (string.IsNullOrEmpty(json))
      return new JsonObject();
    if (json.Length > MaximumCustomCharacters)
      throw new InvalidOperationException("The saved custom fields are too large.");
    var data = JsonNode.Parse(json) as JsonObject
      ?? throw new InvalidOperationException("The custom fields are not a JSON object.");
    foreach (var field in data)
    {
      ValidateKey(field.Key);
      if (field.Value is not JsonValue value || !value.TryGetValue<string>(out _))
        throw new InvalidOperationException($"Custom field '{field.Key}' is not text.");
    }
    return data;
  }

  internal static void ValidateKey(string key)
  {
    if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumFieldNameCharacters || key.Contains('\0'))
      throw new InvalidOperationException("Use a nonempty field name of at most 256 characters.");
  }

  internal static string NormalizeMaterial(string value)
  {
    string name = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
    if (name.Length > MaximumMaterialNameCharacters || name.Contains('\0'))
      throw new InvalidOperationException("Material names may contain at most 80 characters.");
    return name;
  }

  internal static void ValidateNest(JsonObject data)
  {
    if (Integer(data, "Version") != SchemaVersion)
      throw new InvalidOperationException("This nesting-record version is not supported.");
    Range(Integer(data, "Copies"), 1, MaximumCopies, "Copies");
    Range(Integer(data, "Table"), 0, MaximumTables - 1, "Table");
    foreach (string key in new[] { "Included", "Fixed", "ManualRotation", "Flipped" })
      Boolean(data, key, false);
    foreach (string key in new[] { "Flip", "Obey", "Reverse" })
      Boolean(data, key, true);
    foreach (string key in new[] { "Angle", "X", "Y" })
    {
      double value = Number(data, key);
      if (key != "Angle" && value < 0)
        throw new InvalidOperationException($"{key} must not be negative.");
    }
    NullableRange(data, "Step", 0.1, 360);
    NullableRange(data, "Deviation", 0, 90);
    NullableEnum(data, "Grain", 3);
    NullableEnum(data, "Rotation", 5);
    foreach (string key in new[] { "Guide", "GuideFrom", "GuideTo" })
      ValidateVector(data[key], key);
    if (data["Material"] != null)
      NormalizeMaterial(Text(data["Material"]));
    if (data["Recipe"] is JsonObject recipe)
    {
      NormalizeMaterial(Text(recipe["Name"]));
      if (Number(recipe, "Width") <= 0 || Number(recipe, "Gap") < 0)
        throw new InvalidOperationException("Material width must be positive and gap nonnegative.");
      Range(recipe["GrainDeviation"] == null ? 0 : Number(recipe, "GrainDeviation"),
        0, 90, "Material grain deviation");
    }
    else if (data["Recipe"] != null)
      throw new InvalidOperationException("The material recipe is invalid.");
  }

  internal static void ValidateVector(JsonNode? node, string name)
  {
    if (node == null)
      return;
    if (node is not JsonArray vector || vector.Count != 3)
      throw new InvalidOperationException($"{name} must contain three coordinates.");
    foreach (var item in vector)
      FiniteNumber(item, name);
  }

  internal static double Number(JsonObject data, string name) => FiniteNumber(data[name], name);

  internal static double FiniteNumber(JsonNode? node, string name)
  {
    if (node is not JsonValue value)
      throw new InvalidOperationException($"{name} must be a finite number.");
    double number;
    if (!value.TryGetValue<double>(out number))
    {
      if (value.TryGetValue<int>(out int integer))
        number = integer;
      else if (value.TryGetValue<long>(out long longInteger))
        number = longInteger;
      else
        throw new InvalidOperationException($"{name} must be a finite number.");
    }
    if (!double.IsFinite(number))
      throw new InvalidOperationException($"{name} must be a finite number.");
    return number;
  }

  internal static int Integer(JsonObject data, string name)
  {
    if (data[name] is JsonValue value && value.TryGetValue<int>(out int integer))
      return integer;
    throw new InvalidOperationException($"{name} must be a whole number.");
  }

  internal static string Text(JsonNode? node)
  {
    if (node is JsonValue value && value.TryGetValue<string>(out var text))
      return text;
    throw new InvalidOperationException("The stored text value is invalid.");
  }

  internal static bool? Boolean(JsonObject data, string key, bool nullable)
  {
    if (nullable && data[key] == null)
      return null;
    if (data[key] is JsonValue value && value.TryGetValue<bool>(out bool result))
      return result;
    throw new InvalidOperationException($"{key} must be a Boolean value.");
  }

  private static void NullableRange(JsonObject data, string key, double minimum, double maximum)
  {
    if (data[key] != null)
      Range(Number(data, key), minimum, maximum, key);
  }

  private static void NullableEnum(JsonObject data, string key, int maximum)
  {
    if (data[key] != null)
      Range(Integer(data, key), 0, maximum, key);
  }

  private static void Range(double value, double minimum, double maximum, string name)
  {
    if (value < minimum || value > maximum)
      throw new InvalidOperationException($"{name} must be between {minimum} and {maximum}.");
  }


  }
}

