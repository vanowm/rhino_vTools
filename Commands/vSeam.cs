using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>Adds seam allowances or converts exact finished edges to cutting edges when Seam is zero.</summary>
public sealed class vSeam : vToolsCommand
{
  // Defaults and customizable geometry settings
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultAllowance", "Seam allowance in model units; zero converts the chosen finished edges to cutting edges.", OptionKeys = "allowance|edgeSeam|partSeam")]
  private const double DefaultAllowance = 0.5; // Nonnegative seam distance in model units; zero converts source edges.
  private const int DefaultOpenSide = 1; // +1 previews the right side of an open chain; -1 is the left side.
  private const bool DefaultOpenCap = true; // true caps an original open end; false leaves a new split end uncapped.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultType", "Part offsets the complete boundary; Edge offsets the picked section between kinks.", OptionKeys = "type")]
  private const SeamType DefaultType = SeamType.Part; // Part or Edge.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultLayer", "Output layer path or *Current*.", OptionKeys = "layer")]
  private const string DefaultLayer = "CUT1"; // Existing or new output layer path.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultChamfer", "true clips acute convex corners at the allowance distance from the finished vertex; false keeps miters.", OptionKeys = "chamfer")]
  private const bool DefaultChamfer = true; // true chamfers pointy corners; false keeps extended/trimmed miters.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.KinkAngleDegrees", "Minimum tangent change defining a selectable section, in degrees; positive.")]
  private const double KinkAngleDegrees = 1.0; // Positive G1 kink threshold in degrees.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.ChamferInteriorDegrees", "Maximum convex interior angle to chamfer, in degrees from 0 through 180; zero disables angle-based chamfering.", OptionKeys = "chamferAngle")]
  private const double ChamferInteriorDegrees = 70; // Maximum chamfered interior angle in degrees, inclusive; zero through 180.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.ExtensionFactor", "Maximum corner-extension reach as a multiple of allowance; positive.")]
  private const double ExtensionFactor = 64; // Positive allowance multiplier for natural curve extensions.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.MatchAngleDegrees", "Maximum tangent/normal angle error when recognizing existing allowance, in degrees; positive.")]
  private const double MatchAngleDegrees = 5; // Positive legacy allowance recognition angle.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.MatchRelativeDistance", "Maximum relative variation when recognizing an existing allowance; positive and less than one.")]
  private const double MatchRelativeDistance = 0.015; // Fraction of allowance accepted as distance variation.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.MatchSamples", "Interior samples for recognition and boundary membership; three or greater.")] private const int MatchSamples = 9; // Interior samples for recognition and boundary membership; three or greater.

  private static readonly int ConfiguredMatchSamples = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.MatchSamples", MatchSamples); // Resolved once from advanced defaults; restart Rhino after changes.
  private const double MinimumMatchCoverage = 0.1; // Minimum source-edge arc-length fraction covered by an adopted allowance; zero through one.
  private const double ParameterFraction = 1e-12; // Domain-relative parameter merging/endpoint tolerance; positive.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.LegacySearchFactor", "Allowance multiplier for nearby ungrouped existing-CUT1 searches; positive.")] private const double LegacySearchFactor = 8; // Allowance multiplier for nearby ungrouped existing-CUT1 searches; positive.

  private static readonly double ConfiguredLegacySearchFactor = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.LegacySearchFactor", LegacySearchFactor); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.BoundaryToleranceFactor", "Document-tolerance multiplier for source membership; positive.")] private const double BoundaryToleranceFactor = 2; // Document-tolerance multiplier for source membership; positive.

  private static readonly double ConfiguredBoundaryToleranceFactor = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.BoundaryToleranceFactor", BoundaryToleranceFactor); // Resolved once from advanced defaults; restart Rhino after changes.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.BoundaryJoinToleranceFactor", "Boundary joining tolerance as a positive multiple of document absolute tolerance; 2.1 matches Rhino's default curve joining.")]
  private const double BoundaryJoinToleranceFactor = 2.1; // Positive document-tolerance multiplier for temporary boundary joining only; source geometry and offset accuracy are unchanged.
  [CommandHighlightDefault("vSeam","vTools.Commands.vSeam.GapHighlightStyle","Open boundary gaps","#FFFF0000","#FF000000","#FFFF0000",0)]
  private static readonly PreviewDisplay.ObjectHighlightStyle GapHighlightStyle = CommandDefaults.Highlight("vSeam","vTools.Commands.vSeam.GapHighlightStyle",new(System.Drawing.Color.Red, System.Drawing.Color.Black, System.Drawing.Color.Red, 1)); // Red endpoint markers with black outlines for gaps beyond joining tolerance; no surface fill.
  private const CurveExtensionStyle CornerExtensionStyle = CurveExtensionStyle.Line; // Tangent-line extensions join offset ends without curling or changing existing curved sections.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.BoxDragThresholdPixels", "Screen-pixel movement separating a click from a selection-box drag; positive.")] private const int BoxDragThresholdPixels = 4; // Screen-pixel movement separating a click from a selection-box drag; positive.

  private static readonly int ConfiguredBoxDragThresholdPixels = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.BoxDragThresholdPixels", BoxDragThresholdPixels); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.BoxPreviewIntervalMilliseconds", "Minimum time between box-selection previews in milliseconds; nonnegative.")] private const int BoxPreviewIntervalMilliseconds = 80; // Minimum time between box-selection previews in milliseconds; nonnegative.

  private static readonly int ConfiguredBoxPreviewIntervalMilliseconds = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.BoxPreviewIntervalMilliseconds", BoxPreviewIntervalMilliseconds); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.BoxLineThicknessPixels", "Selection-box stroke width in screen pixels; positive.")] private const int BoxLineThicknessPixels = 1; // Selection-box stroke width in screen pixels; positive.

  private static readonly int ConfiguredBoxLineThicknessPixels = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.BoxLineThicknessPixels", BoxLineThicknessPixels); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vSeam", "vTools.Commands.vSeam.PreviewStrokeEmphasis", "Extra pixels above Rhino's normal curve width for the allowance preview; nonnegative.")] private const int PreviewStrokeEmphasis = 0; // Extra pixels above Rhino's normal curve width for the allowance preview; nonnegative.

  private static readonly int ConfiguredPreviewStrokeEmphasis = global::vTools.CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.PreviewStrokeEmphasis", PreviewStrokeEmphasis); // Resolved once from advanced defaults; restart Rhino after changes.
  private const bool PickerObjectSnaps = false; // false prevents native snap highlighting of labels/whole curves; true permits object snaps during picking.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.BoxColor", "RGB color of the temporary selection rectangle.", "#FF1E90FF")]
  private static readonly System.Drawing.Color BoxColor = CommandDefaults.GetColor("vSeam", "vTools.Commands.vSeam.BoxColor", System.Drawing.Color.DodgerBlue); // RGB color of the temporary selection rectangle; restart after advanced edits.
  private const string FinishedLayerName = "PLOT"; // Preferred leaf layer for finished boundaries, without excluding other source layers.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.NewCutLayerColor", "RGB color assigned to newly created seam output layers; existing layer colors are retained.", "#FFCC3333")]
  private static readonly System.Drawing.Color NewCutLayerColor = CommandDefaults.GetColor("vSeam", "vTools.Commands.vSeam.NewCutLayerColor", System.Drawing.Color.FromArgb(204, 51, 51)); // RGB for new output layers only; restart after advanced edits.
  private static readonly SeamOptions Defaults = new(
    CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.DefaultAllowance", DefaultAllowance),
    CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.DefaultType", DefaultType),
    CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.DefaultLayer", DefaultLayer),
    CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.DefaultChamfer", DefaultChamfer))
    { ChamferAngle = CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.ChamferInteriorDegrees", ChamferInteriorDegrees) }; // Resolved factory defaults; restart after advanced changes.
  private static readonly double KinkCosine = Math.Cos(CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.KinkAngleDegrees", KinkAngleDegrees) * Math.PI / 180);
  private static readonly double CornerReach = CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.ExtensionFactor", ExtensionFactor);
  private static readonly double MatchCosine = Math.Cos(CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.MatchAngleDegrees", MatchAngleDegrees) * Math.PI / 180);
  private static readonly double MatchVariation = CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.MatchRelativeDistance", MatchRelativeDistance);
  private static readonly double JoinToleranceFactor = CommandDefaults.Get("vSeam", "vTools.Commands.vSeam.BoundaryJoinToleranceFactor", BoundaryJoinToleranceFactor); // Resolved positive boundary-join multiplier; restart after advanced changes.

  private const string Section = "vSeam";
  private const string OwnerKey = "vSeam.owner";
  private const string StampKey = "vSeam.sourceStamp";
  private const string RoleKey = "vSeam.role";
  private const string EdgeKey = "vSeam.edge";
  private const string DistanceKey = "vSeam.allowance";
  private const string VersionKey = "vSeam.version";
  private const string SchemaVersion = "1";
  private const string FinishedCutKey = "vSeam.finishedCut";
  private const string FinishedLayerKey = "vSeam.finishedLayer";
  private const string SourceOwnerKey = "vSeam.sourceOwner";
  private const string SourceRootKey = "vSeam.sourceRoot";
  private const string SourceCurveKey = "vSeam.sourceCurve";
  private const string ProfileKey = "vSeam.allowanceProfile";
  private const string ProfileRootKey = "vSeam.profileRoot";
  private const string JoinedSectionsKey = "vSeam.joinedSections";
  private const string ImportedStyleKey = "vSeam.patternSmithStyle";
  private const string GeometryStampKey = "vSeam.geometryStamp";
  private const string DetachedKey = "vSeam.detached";
  private const string DetachedProfileKey = "vSeam.detachedProfile";
  private const string DetachedObjectKey = "vSeam.detachedObject";
  [global::vTools.CommandChoicesAttribute("vSeam", "type", false, "Part", "Edge")] private static readonly string[] Types = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vSeam), nameof(Types)); // Visible names in SeamType order.
  private static SeamOptions _settings = Defaults;
  private static double _partAllowance = Defaults.Allowance;
  private static double _edgeAllowance = Defaults.Allowance;

  public override string EnglishName => "vSeam";
  private enum SeamType { Part, Edge }
  private sealed record SeamOptions(double Allowance, SeamType Type, string Layer, bool Chamfer)
  {
    public double ChamferAngle { get; init; } = ChamferInteriorDegrees; // Maximum included interior angle in degrees; zero through 180.
  }
  private sealed record SourceObject(Guid Id, uint Serial, uint GeometryCrc, string AttributesCrc);
  private sealed record ExistingObject(Guid Id, uint Serial, uint GeometryCrc, string AttributesCrc);
  private sealed record Piece(Curve Curve, string Role, int Edge, double Allowance);
  private sealed record AllowanceProfile(double[] Lengths, double[] Distances)
  {
    public string[]? EdgeSources { get; init; }
    public Dictionary<Guid, int>? SourceDirections { get; init; }
    public int? Side { get; init; }
    public bool? Chamfer { get; init; }
    public double? ChamferAngle { get; init; }
    public bool? Open { get; init; }
    public double[]? OpenPlaneNormal { get; init; }
    public bool? CapStart { get; init; }
    public bool? CapEnd { get; init; }
  }
  internal sealed record ImportedAllowance(Guid Id,Guid[] Sources);
  internal sealed record ImportedStyle(double SignedWidth,string Start,string End);
  private const string ImportedJoinedRole = "imported"; // An exact imported offset with built-in joins or caps, not a single vSeam edge.
  private sealed class BoundaryGapException(Point3d[] endpoints) : InvalidOperationException("Pick a single planar boundary or an unbranched chain; red markers identify unmatched endpoints.")
  {
    internal Point3d[] Endpoints { get; } = endpoints;
  }

  private sealed class Part(Curve outline, Plane plane, List<Curve> edges, SourceObject[] sources, int[] groups,
    string owner, string stamp, Point3d pickPoint) : IDisposable
  {
    internal Curve Outline { get; } = outline;
    internal Plane Plane { get; } = plane;
    internal List<Curve> Edges { get; } = edges;
    internal SourceObject[] Sources { get; } = sources;
    internal int[] Groups { get; } = groups;
    internal string Owner { get; } = owner;
    internal string Stamp { get; } = stamp;
    internal Point3d PickPoint { get; set; } = pickPoint;
    internal int OffsetSide { get; set; } = DefaultOpenSide;
    internal bool CapStart { get; set; } = DefaultOpenCap;
    internal bool CapEnd { get; set; } = DefaultOpenCap;
    internal HashSet<int>? SelectedEdges { get; set; }
    internal int PickedEdge => Edges.Select((curve, index) => (index, distance: Distance(curve, PickPoint))).OrderBy(item => item.distance).First().index;
    internal bool Includes(int index) => SelectedEdges?.Contains(index) ?? index == PickedEdge;
    public void Dispose() { Outline.Dispose(); foreach (var edge in Edges) edge.Dispose(); }
  }

  private sealed record BoxTarget(Part Part, int[] Edges);
  private sealed class PartSelection(List<Part> parts) : IDisposable
  {
    internal List<Part> Parts { get; } = parts;
    internal Dictionary<Part, PreparedSeam> Previews { get; } = [];
    public void Dispose()
    {
      foreach (var preview in Previews.Values) preview.Dispose();
      foreach (var part in Parts) part.Dispose();
    }
  }

  private sealed class Existing(int count) : IDisposable
  {
    internal Curve?[] Curves { get; } = new Curve?[count];
    internal double[] Distances { get; } = new double[count];
    internal List<ExistingObject> Objects { get; } = [];
    internal int OpenSide { get; set; }
    public void Dispose() { foreach (var curve in Curves) curve?.Dispose(); }
  }

  private sealed class Plan : IDisposable
  {
    internal List<Piece> Pieces { get; } = [];
    internal int AffectedEdges { get; set; }
    internal double[]? Allowances { get; set; }
    internal int OffsetSide { get; set; }=DefaultOpenSide;
    public void Dispose() { foreach (var piece in Pieces) piece.Curve.Dispose(); Pieces.Clear(); }
  }

  private sealed record HistoryRequest(bool Redo);

  private sealed class PreparedSeam(Existing existing, Plan plan, int[] edges, SeamOptions settings) : IDisposable
  {
    internal Existing Existing { get; } = existing;
    internal Plan Plan { get; } = plan;
    internal int[] Edges { get; } = edges;
    internal SeamOptions Settings { get; } = settings;
    internal Dictionary<Guid, GeometryBase> Geometry { get; } = plan.Pieces.ToDictionary(_ => Guid.NewGuid(), piece => (GeometryBase)piece.Curve);
    public void Dispose() { Plan.Dispose(); Existing.Dispose(); }
  }

  private static int[] PreviewEdges(Part part, int[]? edges) => _settings.Type == SeamType.Part
    ? Enumerable.Range(0, part.Edges.Count).ToArray() : (edges is { Length: > 0 } ? edges : [part.PickedEdge]).Distinct().Order().ToArray();

  private static PreparedSeam PrepareSeam(RhinoDoc doc, Part part, int[]? selectedEdges = null)
  {
    var edges = PreviewEdges(part, selectedEdges);
    var before = part.SelectedEdges;
    var existing = new Existing(part.Edges.Count);
    try
    {
      part.SelectedEdges = edges.ToHashSet();
      ReadExisting(doc, part, existing);
      return new PreparedSeam(existing, BuildPlan(part, existing, _settings, doc.ModelAbsoluteTolerance), edges, _settings);
    }
    catch { existing.Dispose(); throw; }
    finally { part.SelectedEdges = before; }
  }

  private sealed class ReplacedAllowanceCull(RhinoDoc doc) : Rhino.Display.DisplayConduit, IDisposable
  {
    private readonly HashSet<Guid> _ids = [];
    internal void SetObjects(IEnumerable<Guid> ids)
    {
      _ids.Clear(); _ids.UnionWith(ids);
      SetObjectIdFilter(_ids);
      Enabled = _ids.Count > 0;
    }
    protected override void ObjectCulling(Rhino.Display.CullObjectEventArgs e)
    {
      if (e.RhinoObject?.Document?.RuntimeSerialNumber == doc.RuntimeSerialNumber && _ids.Contains(e.RhinoObject.Id)) e.CullObject = true;
    }
    public void Dispose() { Enabled = false; _ids.Clear(); }
  }

  private sealed class SeamPreview : IDisposable
  {
    private readonly record struct Key(Part Part, string Edges, SeamOptions Settings,int OffsetSide);
    private readonly RhinoDoc _doc;
    private readonly Dictionary<Key, PreparedSeam> _cache = [];
    private readonly Dictionary<Key, string> _errors = [];
    private readonly PreviewDisplay.ObjectHighlighter _display;
    private readonly ReplacedAllowanceCull _cull;
    private PreparedSeam[] _shown = [];
    internal string? LastError { get; private set; }
    internal SeamPreview(RhinoDoc doc)
    {
      _doc = doc;
      int layer = LayerSelector.IsCurrentLayerValue(_settings.Layer, DuplicateCommandSupport.CurrentLayerOption)
        ? doc.Layers.CurrentLayerIndex : doc.Layers.FindByFullPath(_settings.Layer, -1);
      var color = layer >= 0 ? doc.Layers[layer].Color : NewCutLayerColor;
      var style = new PreviewDisplay.ObjectHighlightStyle(color, color, color, 0)
      { StrokeEmphasis = ConfiguredPreviewStrokeEmphasis, OutlineEmphasis = ConfiguredPreviewStrokeEmphasis };
      _display = new PreviewDisplay.ObjectHighlighter(doc, style) { RedrawOnChange = false, DrawOnTop = true };
      _cull = new ReplacedAllowanceCull(doc);
    }
    private static Key CacheKey(Part part, int[]? edges) => new(part, string.Join(",", PreviewEdges(part, edges)), _settings,part.OffsetSide);
    internal PreparedSeam? Get(Part part, int[]? edges = null)
    {
      var key = CacheKey(part, edges);
      if (_cache.TryGetValue(key, out var cached)) return cached;
      if (_errors.TryGetValue(key, out var error)) { LastError = error; return null; }
      try { var prepared = PrepareSeam(_doc, part, edges); _cache.Add(key, prepared); return prepared; }
      catch (Exception ex)
      {
        _errors[key] = LastError = ex.Message;
        Log.Write(Section, $"Preview rejected owner={part.Owner} edges={key.Edges}: {ex.Message}");
        return null;
      }
    }
    internal bool Show(IReadOnlyList<BoxTarget> targets)
    {
      LastError = null;
      var next = targets.Select(target => Get(target.Part, target.Edges)).OfType<PreparedSeam>().ToArray();
      if (_shown.SequenceEqual(next)) return false;
      _shown = next;
      var geometry = next.SelectMany(entry => entry.Geometry).ToDictionary(pair => pair.Key, pair => pair.Value);
      _display.SetObjects(geometry.Keys, geometry);
      _cull.SetObjects(next.SelectMany(entry => entry.Existing.Objects.Select(obj => obj.Id)));
      return true;
    }
    internal PreparedSeam? Take(Part part, int[]? edges = null)
    {
      var prepared = Get(part, edges);
      if (prepared != null) _cache.Remove(CacheKey(part, edges));
      return prepared;
    }
    public void Dispose()
    {
      _display.Dispose(); _cull.Dispose();
      foreach (var prepared in _cache.Values) prepared.Dispose();
      _cache.Clear(); _errors.Clear(); _shown = [];
      _doc.Views.Redraw();
    }
  }

  private sealed class CurveState(RhinoObject obj) : IDisposable
  {
    internal Curve Geometry { get; } = ((Curve)obj.Geometry).DuplicateCurve();
    internal ObjectAttributes Attributes { get; } = obj.Attributes.Duplicate();
    private readonly uint _geometryCrc = obj.Geometry.DataCRC(0);
    private readonly string _attributesStamp = AttributeStamp(obj.Attributes);
    internal bool Matches(RhinoObject? obj) => obj is { IsDeleted: false } &&
      obj.Geometry.DataCRC(0) == _geometryCrc && AttributeStamp(obj.Attributes) == _attributesStamp;
    public void Dispose() { Geometry.Dispose(); Attributes.Dispose(); }
  }

  private sealed class HistoryObject(RhinoObject obj) : IDisposable
  {
    internal Guid Id { get; } = obj.Id;
    internal uint DeletedSerial { get; set; } = obj.RuntimeSerialNumber;
    internal CurveState? Before { get; set; } = new(obj);
    internal CurveState? After { get; set; }
    public void Dispose() { Before?.Dispose(); After?.Dispose(); }
  }

  private sealed class HistoryRecord : IDisposable
  {
    internal List<HistoryObject> Objects { get; } = [];
    internal int CreatedGroup { get; private set; } = -1;
    internal HistoryRecord(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      try { foreach (var id in ids.Distinct()) Objects.Add(new HistoryObject(doc.Objects.FindId(id)
        ?? throw new InvalidOperationException("A seam object disappeared before it could be recorded."))); }
      catch { Dispose(); throw; }
    }
    internal void Complete(RhinoDoc doc, IEnumerable<Guid> added, int group)
    {
      CreatedGroup = group;
      foreach (var id in added)
      {
        var item = new HistoryObject(doc.Objects.FindId(id)
          ?? throw new InvalidOperationException("A new seam object disappeared before it could be recorded."));
        item.Before!.Dispose(); item.Before = null; Objects.Add(item);
      }
      foreach (var item in Objects)
        if (doc.Objects.FindId(item.Id) is {} obj) item.After = new CurveState(obj);
    }
    internal bool Apply(RhinoDoc doc, bool redo)
    {
      if (!redo && CreatedGroup >= 0 && (doc.Groups.GroupMembers(CreatedGroup) ?? [])
        .Any(obj => !obj.IsDeleted && Objects.All(item => item.Id != obj.Id)))
      { RhinoApp.WriteLine("vSeam: the seam group has new members; history was not applied."); return false; }
      foreach (var item in Objects)
      {
        var expected = redo ? item.Before : item.After;
        var obj = doc.Objects.FindId(item.Id);
        if (expected == null ? obj != null : !expected.Matches(obj) || obj is not { IsLocked: false, IsReference: false, IsDeletable: true })
        { RhinoApp.WriteLine("vSeam: seam objects changed or are locked; history was not applied."); return false; }
      }
      try { Restore(doc, redo); return true; }
      catch (Exception ex)
      {
        Log.Write(Section, $"Local {(redo ? "redo" : "undo")} failed: {ex}");
        try { Restore(doc, !redo); }
        catch (Exception rollback) { Log.Write(Section, $"History rollback failed: {rollback}"); }
        RhinoApp.WriteLine("vSeam: could not restore the complete seam; see the diagnostic log.");
        return false;
      }
    }
    private void Restore(RhinoDoc doc, bool redo)
    {
      if (redo && CreatedGroup >= 0 && doc.Groups[CreatedGroup] is { IsDeleted: true } && !doc.Groups.Undelete(CreatedGroup))
        throw new InvalidOperationException("Cannot restore the seam group.");
      foreach (var item in Objects)
      {
        var state = redo ? item.After : item.Before;
        if (state == null) continue;
        var obj = doc.Objects.FindId(item.Id);
        if (obj == null)
        {
          if (!doc.Objects.Undelete(item.DeletedSerial)) throw new InvalidOperationException("Cannot restore a deleted seam object.");
          obj = doc.Objects.FindId(item.Id) ?? throw new InvalidOperationException("Restored seam object has a different identity.");
        }
        if (obj.Geometry.DataCRC(0) != state.Geometry.DataCRC(0))
        {
          using var geometry = state.Geometry.DuplicateCurve();
          if (!doc.Objects.Replace(item.Id, geometry)) throw new InvalidOperationException("Cannot restore finished-edge geometry.");
        }
        // Native writes may normalize the supplied attributes. Never give them the stored history snapshot.
        using var attributes = state.Attributes.Duplicate();
        if (!doc.Objects.ModifyAttributes(item.Id, attributes, true))
          throw new InvalidOperationException("Cannot restore seam layers, groups or metadata.");
      }
      foreach (var item in Objects.Where(item => (redo ? item.After : item.Before) == null))
        if (doc.Objects.FindId(item.Id) is {} obj)
        {
          item.DeletedSerial = obj.RuntimeSerialNumber;
          if (!doc.Objects.Delete(item.Id, true)) throw new InvalidOperationException("Cannot remove a seam object.");
        }
      if (!redo && CreatedGroup >= 0 && doc.Groups[CreatedGroup] is { IsDeleted: false } && !doc.Groups.Delete(CreatedGroup))
        throw new InvalidOperationException("Cannot remove the seam group.");
    }
    public void Dispose() { foreach (var item in Objects) item.Dispose(); Objects.Clear(); }
  }

  private sealed class SessionHistory : IDisposable
  {
    private readonly Stack<HistoryRecord> _undo = new();
    private readonly Stack<HistoryRecord> _redo = new();
    internal void Push(HistoryRecord record)
    { foreach (var previous in _redo) previous.Dispose(); _redo.Clear(); _undo.Push(record); }
    internal bool Move(RhinoDoc doc, bool redo)
    {
      var source = redo ? _redo : _undo;
      var target = redo ? _undo : _redo;
      if (!source.TryPeek(out var record)) { RhinoApp.WriteLine(redo ? "vSeam: nothing to redo." : "vSeam: nothing to undo."); return false; }
      if (!record.Apply(doc, redo)) return false;
      source.Pop(); target.Push(record); doc.Views.Redraw();
      RhinoApp.WriteLine(redo ? "vSeam: seam redone." : "vSeam: seam undone.");
      return true;
    }
    public void Dispose() { foreach (var record in _undo.Concat(_redo)) record.Dispose(); _undo.Clear(); _redo.Clear(); }
  }

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    _settings = ToolsOptionStore.Read(Section, LoadOptions);
    bool preselect = true;
    int applied = 0;
    using var history = new SessionHistory();
    var preselected = doc.Objects.GetSelectedObjects(false, false).ToArray();
    if (preselected.Length > 0 && preselected.All(IsSeamEditCandidate) && FindSeamBatches(doc,preselected,true).Length>0)
      return RunSeamEditor(doc, mode, preselected);
    while (true)
    {
      using var selection = PickPart(doc, mode, preselect, out var result, out var historyRequest, out bool edit);
      preselect = false;
      if (edit) return RunSeamEditor(doc, mode, []);
      if (historyRequest != null) { history.Move(doc, historyRequest.Redo); continue; }
      if (selection == null) return applied > 0 ? Result.Success : result;
      foreach (var part in selection.Parts)
      {
        try
        {
          using var prepared=NeedsOpenSide(part)?PickOpenSide(doc,part):
            selection.Previews.Remove(part,out var previewed)?previewed:PrepareSeam(doc,part,part.SelectedEdges?.ToArray());
          if(prepared==null)continue;
          if (prepared.Settings != _settings) throw new InvalidOperationException("Seam options changed during preview. Pick the part again.");
          part.SelectedEdges = _settings.Type == SeamType.Edge ? prepared.Edges.ToHashSet() : null;
          var plan = prepared.Plan;
          if (Commit(doc, part, prepared.Existing, plan, history))
          {
            applied++;
            RhinoApp.WriteLine(_settings.Allowance == 0
              ? $"vSeam: converted {CountText.Format(plan.AffectedEdges, "finished edge")} to {CountText.Form(plan.AffectedEdges, "a cutting edge", "cutting edges")}."
              : $"vSeam: Seam={_settings.Allowance:G6} applied to {CountText.Format(plan.AffectedEdges, "edge")}; finished geometry retained.");
          }
        }
        catch (Exception ex)
        {
          Log.Write(Section, $"Seam rejected owner={part.Owner} sources={string.Join(",", part.Sources.Select(source => source.Id))} type={_settings.Type} edge={part.PickedEdge} allowance={_settings.Allowance:G17}: {ex}");
          RhinoApp.WriteLine($"vSeam: {ex.Message}");
        }
        doc.Views.Redraw();
      }
    }
  }

  private static SeamOptions LoadOptions(System.Text.Json.Nodes.JsonObject? section)
    => ReadOptions(section,out _partAllowance,out _edgeAllowance);

  private static SeamOptions ReadOptions(System.Text.Json.Nodes.JsonObject? section,out double partAllowance,out double edgeAllowance)
  {
    var type = ToolsOptionStore.TryGetString(section, "type", out var name) && Enum.TryParse<SeamType>(name, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : Defaults.Type;
    double legacy = ReadDistance("allowance", Defaults.Allowance);
    partAllowance = ReadDistance("partSeam", type == SeamType.Part ? legacy : Defaults.Allowance);
    edgeAllowance = ReadDistance("edgeSeam", type == SeamType.Edge ? legacy : Defaults.Allowance);
    return new SeamOptions(type == SeamType.Part ? partAllowance : edgeAllowance, type,
      ToolsOptionStore.TryGetString(section, "layer", out var layer) ? layer : Defaults.Layer,
      ToolsOptionStore.TryGetBool(section, "chamfer", out var chamfer) ? chamfer : Defaults.Chamfer)
      { ChamferAngle = ToolsOptionStore.TryGetDouble(section, "chamferAngle", out var angle) && double.IsFinite(angle) && angle >= 0 && angle <= 180 ? angle : Defaults.ChamferAngle };
    double ReadDistance(string key, double fallback) => ToolsOptionStore.TryGetDouble(section, key, out var value) && double.IsFinite(value) && value >= 0 ? value : fallback;
  }

  private static SeamOptions ReadPanelDefaults() => ToolsOptionStore.Read(Section,section=>ReadOptions(section,out _,out _));

  private static void SetSeamDistance(double distance, SeamType type)
  {
    if (_settings.Type == SeamType.Part) _partAllowance = distance;
    else _edgeAllowance = distance;
    _settings = _settings with { Type = type, Allowance = type == SeamType.Part ? _partAllowance : _edgeAllowance };
  }

  private sealed class Controls
  {
    internal readonly OptionDouble Allowance = new(_settings.Allowance, 0, double.MaxValue);
    internal readonly OptionToggle Chamfer = new(_settings.Chamfer, "No", "Yes");
    internal readonly OptionDouble ChamferAngle = new(_settings.ChamferAngle, 0, 180);
    internal readonly int TypeIndex;
    internal readonly int[] DirectTypeIndices;
    internal readonly int[] ShortTypeIndices;
    internal readonly int LayerIndex;
    internal Controls(GetBaseClass getter)
    {
      var distance = Allowance; getter.AddOptionDouble("Seam", ref distance);
      TypeIndex = getter.AddOptionList("Type", Types, (int)_settings.Type);
      DirectTypeIndices = Types.Select(type => getter.AddOption(type, string.Empty, true)).ToArray();
      ShortTypeIndices = Types.Select(type => getter.AddOption(type[..1], string.Empty, true)).ToArray();
      LayerIndex = getter.AddOption("Layer", _settings.Layer);
      var chamfer = Chamfer; getter.AddOptionToggle("Chamfer", ref chamfer);
      var angle = ChamferAngle; getter.AddOptionDouble("ChamferAngle", ref angle);
    }
  }

  private static void ReadControls(RhinoDoc doc, RunMode mode, GetBaseClass getter, GetResult result, Controls controls)
  {
    double distance = result == GetResult.Number ? getter.Number() : controls.Allowance.CurrentValue;
    if (!double.IsFinite(distance) || distance < 0) { RhinoApp.WriteLine("vSeam: Seam must be zero or greater."); return; }
    double angle = controls.ChamferAngle.CurrentValue;
    if (!double.IsFinite(angle) || angle < 0 || angle > 180) { RhinoApp.WriteLine("vSeam: ChamferAngle must be from 0 through 180 degrees."); return; }
    var type = _settings.Type;
    if (getter.Option()?.Index == controls.TypeIndex) type = (SeamType)getter.Option()!.CurrentListOptionIndex;
    int directType = Array.IndexOf(controls.DirectTypeIndices, getter.Option()?.Index ?? -1);
    if (directType < 0) directType = Array.IndexOf(controls.ShortTypeIndices, getter.Option()?.Index ?? -1);
    if (directType >= 0) type = (SeamType)directType;
    SetSeamDistance(distance, type);
    _settings = _settings with { Chamfer = controls.Chamfer.CurrentValue, ChamferAngle = angle };
    if (getter.Option()?.Index == controls.LayerIndex && LayerSelector.TrySelect(doc, _settings.Layer,
      DuplicateCommandSupport.CurrentLayerOption, "Seam output layer", mode, allowNewLayer: true, out var selected))
      _settings = _settings with { Layer = selected };
    if (!ToolsOptionStore.Update(Section, section =>
      { section["allowance"] = _settings.Allowance; section["partSeam"] = _partAllowance; section["edgeSeam"] = _edgeAllowance; section["type"] = _settings.Type.ToString(); section["layer"] = _settings.Layer; section["chamfer"] = _settings.Chamfer; section["chamferAngle"] = _settings.ChamferAngle; }))
      Log.Write(Section, $"Could not save options: {ToolsOptionStore.LastError}");
  }

  private sealed record SeamBatch(string Owner, string Stamp, string? Detached, Guid[] Outputs);
  internal sealed record ExportAllowance(Guid[] Outputs,IReadOnlyDictionary<Guid,double> SignedWidths,
    IReadOnlyDictionary<Guid,ImportedStyle>? Styles=null);
  internal static bool IsAttachedAllowance(RhinoObject obj) =>
    obj.Attributes.GetUserString(OwnerKey)!=null && !IsDetached(obj);

  internal static bool IsJoinedAllowanceSource(RhinoObject obj) =>
    obj.Attributes.GetUserString(JoinedSectionsKey)=="1" && obj.Attributes.GetUserString(SourceOwnerKey)!=null;

  internal static List<ExportAllowance> ExportAllowances(RhinoDoc doc,RhinoObject[] selected)
  {
    var ids=selected.Select(obj=>obj.Id).ToHashSet();
    var result=new List<ExportAllowance>();
    var included=new HashSet<Guid>();
    foreach(var batch in FindSeamBatches(doc,selected))
    {
      var sources=ReadSourceIds(batch.Stamp);
      if(!sources.All(ids.Contains) || !batch.Outputs.All(ids.Contains))continue;
      var objects=sources.Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
      if(objects.Length!=sources.Length || objects.Any(obj=>obj.Attributes.GetUserString(SourceOwnerKey)!=batch.Owner) ||
        BoundaryIdentity(objects).Stamp!=batch.Stamp)continue;
      Add(sources,objects,batch.Outputs);
    }
    // Placed copies retain the original source stamps. Only resolve them inside a single placed part.
    foreach(var placement in selected.Where(obj=>obj.Attributes.GetUserString("vNest.instance")!=null)
      .GroupBy(obj=>(obj.Attributes.GetUserString("vNest.session"),obj.Attributes.GetUserString("vNest.instance"))))
    {
      if(string.IsNullOrEmpty(placement.Key.Item1))continue;
      var pieces=placement.ToArray();
      foreach(var output in pieces.Where(obj=>!IsDetached(obj) && obj.Attributes.GetUserString(OwnerKey)!=null)
        .GroupBy(obj=>(Owner:obj.Attributes.GetUserString(OwnerKey),Stamp:obj.Attributes.GetUserString(StampKey))))
      {
        if(output.Key.Stamp==null)continue;
        var recorded=ReadSourceIds(output.Key.Stamp);
        var outputs=output.Select(obj=>obj.Id).ToArray();
        if(recorded.Length==0 || outputs.Any(included.Contains))continue;
        var candidates=pieces.Where(obj=>obj.Attributes.GetUserString(SourceOwnerKey)==output.Key.Owner &&
          obj.Attributes.GetUserString(OwnerKey)==null).ToLookup(obj=>
            Guid.TryParse(obj.Attributes.GetUserString(SourceCurveKey),out var id)?id:Guid.Empty);
        if(recorded.Any(id=>candidates[id].Count()!=1))continue;
        Add(recorded,recorded.Select(id=>candidates[id].First()).ToArray(),outputs);
      }
    }
    return result;

    void Add(Guid[] recorded,RhinoObject[] objects,Guid[] outputs)
    {
      var authored=new Dictionary<Guid,ImportedStyle>();
      foreach(var obj in objects)
      {
        var fields=obj.Attributes.GetUserString(ImportedStyleKey)?.Split('|');
        if(fields is {Length:3} && double.TryParse(fields[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double width) &&
          double.IsFinite(width) && width!=0)authored[obj.Id]=new(width,fields[1],fields[2]);
      }
      if(authored.Count>0)
      {
        result.Add(new(outputs,objects.ToDictionary(obj=>obj.Id,obj=>authored.GetValueOrDefault(obj.Id)?.SignedWidth??0),authored));
        included.UnionWith(outputs);
      }
      var profile=GetSourceProfile(objects);
      if(profile==null || !MapSeparatedWidths(profile,recorded.Select(id=>id.ToString("N")).ToArray(),out var widths))return;
      int side=profile.Open==false?DefaultOpenSide:profile.Side.GetValueOrDefault();
      if(side is not (1 or -1))return;
      var signed=new Dictionary<Guid,double>();
      for(int i=0;i<recorded.Length;i++)
      {
        int direction=profile.SourceDirections?.GetValueOrDefault(recorded[i])??0;
        if(direction==0 && recorded.Length==1)direction=1;
        if(direction is not (1 or -1) && widths[i]>0)return;
        signed.Add(objects[i].Id,widths[i]*side*direction);
      }
      result.Add(new(outputs,signed));
      included.UnionWith(outputs);
    }
  }
  private sealed record EditOptions(double? Width=null, bool? Chamfer=null, double? Angle=null, string? Layer=null, bool Flip=false,
    bool WholeBoundary=false);
  private static readonly EditOptions UnchangedEdit = new();

  private static bool IsDetached(RhinoObject obj) => obj.Attributes.GetUserString(DetachedKey)!=null;
  private static bool IsSeamEditCandidate(RhinoObject obj) => !obj.IsDeleted && obj.Geometry is Curve &&
    (obj.Attributes.GetUserString(OwnerKey)!=null || obj.Attributes.GetUserString(SourceOwnerKey)!=null);

  private static Guid[] ReadSourceIds(string stamp)
  {
    var entries=stamp.Split(';');
    var ids=new Guid[entries.Length];
    for(int i=0;i<entries.Length;i++)
    {
      int colon=entries[i].IndexOf(':');
      if(colon!=32 || !Guid.TryParseExact(entries[i][..colon],"N",out ids[i]) || ids[i]==Guid.Empty ||
        !uint.TryParse(entries[i][(colon+1)..],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out _))return [];
    }
    return ids.Distinct().Count()==ids.Length?ids:[];
  }

  private static RhinoObject[] BatchSources(RhinoDoc doc,SeamBatch batch)
  {
    var ids=ReadSourceIds(batch.Stamp);
    var sources=ids.Select(doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false,Geometry:Curve} &&
      obj.Attributes.GetUserString(OwnerKey)==null && obj.Attributes.GetUserString(SourceOwnerKey)==batch.Owner)
      .Cast<RhinoObject>().ToArray();
    if(ids.Length==0 || sources.Length!=ids.Length)
      throw new InvalidOperationException("The saved source curves are no longer available.");
    return sources;
  }

  private static RhinoObject? ProfileRoot(IEnumerable<RhinoObject> sources)
  {
    var roots=sources.Where(obj=>obj.Attributes.GetUserString(ProfileRootKey)=="1").Take(2).ToArray();
    return roots.Length==1?roots[0]:null;
  }

  private static AllowanceProfile? GetSourceProfile(IEnumerable<RhinoObject> sources)
    => ParseProfile(ProfileRoot(sources)?.Attributes.GetUserString(ProfileKey));

  private static Curve[] JoinSeamSources(IEnumerable<Curve> sources,double tolerance)
  {
    var curves=sources.ToArray();
    return curves.Length==1?[curves[0].DuplicateCurve()]:Curve.JoinCurves(curves,tolerance)??[];
  }

  private static SeamBatch[] FindSeamBatches(RhinoDoc doc,IEnumerable<RhinoObject> picks,bool includeDetached=false)
  {
    var objects=picks.Where(IsSeamEditCandidate).ToArray();
    var ids=objects.Select(obj=>obj.Id).ToHashSet();
    var sourceIds=objects.Where(obj=>obj.Attributes.GetUserString(OwnerKey)==null).Select(obj=>obj.Id).ToHashSet();
    var owners=objects.Select(obj=>obj.Attributes.GetUserString(OwnerKey)??obj.Attributes.GetUserString(SourceOwnerKey)).Distinct();
    var batches=new List<SeamBatch>();
    foreach(string? owner in owners)
    {
      var outputs=doc.Objects.FindByUserString(OwnerKey,owner!,true,false,true,ObjectType.Curve)??[];
      foreach(var group in outputs.Where(obj=>!obj.IsDeleted).GroupBy(obj=>(
        Stamp:obj.Attributes.GetUserString(StampKey), Detached:obj.Attributes.GetUserString(DetachedKey))))
      {
        if(string.IsNullOrEmpty(group.Key.Stamp))continue;
        var sources=ReadSourceIds(group.Key.Stamp);
        if(sources.Length==0)continue;
        bool sourcePicked=sources.Any(sourceIds.Contains);
        if(group.Key.Detached!=null && !includeDetached)continue;
        if(!sourcePicked && !group.Any(obj=>ids.Contains(obj.Id)))continue;

        var members=group.ToArray();
        // Copied groups may still contain the original allowance's user strings.
        if(group.Key.Detached==null)
        {
          var root=doc.Objects.FindId(sources[0]);
          var common=(root?.Attributes.GetGroupList()??[]).Where(index=>sources.All(id=>
            (doc.Objects.FindId(id)?.Attributes.GetGroupList()??[]).Contains(index))).ToHashSet();
          if(common.Count>0)members=members.Where(obj=>common.Contains(SmallestGroup(doc,obj))).ToArray();
          else if(root is not {IsDeleted:false})
          {
            var pickedGroups=members.Where(obj=>ids.Contains(obj.Id)).Select(obj=>SmallestGroup(doc,obj)).Distinct().ToArray();
            if(pickedGroups.Length!=1)continue;
            members=members.Where(obj=>SmallestGroup(doc,obj)==pickedGroups[0]).ToArray();
          }
        }
        else members=members.Where(obj=>obj.Attributes.GetUserString(DetachedObjectKey)==obj.Id.ToString("N")).ToArray();
        if(members.Length==0 || !sourcePicked && !members.Any(obj=>ids.Contains(obj.Id)))continue;
        batches.Add(new SeamBatch(owner!,group.Key.Stamp,group.Key.Detached,members.Select(obj=>obj.Id).ToArray()));
      }
    }
    if(!includeDetached)return batches.ToArray();
    var attachedSources=batches.Where(batch=>batch.Detached==null).SelectMany(batch=>ReadSourceIds(batch.Stamp)).ToHashSet();
    return batches.Where(batch=>batch.Detached==null || batch.Outputs.Any(ids.Contains) ||
      !ReadSourceIds(batch.Stamp).Any(attachedSources.Contains)).ToArray();
  }

  private static Result RunSeamEditor(RhinoDoc doc,RunMode mode,RhinoObject[] initial)
  {
    bool applied=false;
    var picks=initial;
    while(true)
    {
      if(picks.Length==0)
      {
        doc.Objects.UnselectAll();
        using var getter=new GetObject();
        getter.SetCommandPrompt("Edit seams: select finished or allowance curves. Enter when done");
        getter.GeometryFilter=ObjectType.Curve;
        getter.GroupSelect=false;
        getter.SubObjectSelect=false;
        getter.SetCustomGeometryFilter((obj,_,_)=>IsSeamEditCandidate(obj));
        getter.AcceptNothing(true);
        if(getter.GetMultiple(1,0)!=GetResult.Object)
          return applied||getter.Result()==GetResult.Nothing?Result.Success:getter.CommandResult();
        picks=Enumerable.Range(0,getter.ObjectCount).Select(i=>getter.Object(i).Object()).OfType<RhinoObject>().ToArray();
      }
      try
      {
        var batches=FindSeamBatches(doc,picks,true);
        if(batches.Length==0){RhinoApp.WriteLine("vSeam: No linked allowance found for the selected curves.");continue;}
        using var highlight=new PreviewDisplay.ObjectHighlighter(doc);
        doc.Objects.UnselectAll();
        highlight.SetObjects(batches.SelectMany(batch=>batch.Outputs.Concat(ReadSourceIds(batch.Stamp))).Distinct().ToArray());
        applied|=EditSeamBatches(doc,mode,batches,picks.Select(obj=>obj.Id).ToHashSet());
      }
      catch(Exception ex){ReportSeamFailure(ex);}
      finally{picks=[];doc.Objects.UnselectAll();doc.Views.Redraw();}
    }
  }

  private static bool EditSeamBatches(RhinoDoc doc,RunMode mode,SeamBatch[] batches,HashSet<Guid> picks)
  {
    var edit=UnchangedEdit;
    var outputs=batches.SelectMany(batch=>batch.Outputs).Distinct().Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
    var widths=outputs.Where(obj=>obj.Attributes.GetUserString(RoleKey) is "edge" or "envelope" or ImportedJoinedRole)
      .Select(obj=>double.TryParse(obj.Attributes.GetUserString(DistanceKey),NumberStyles.Float,CultureInfo.InvariantCulture,out double width)?width:0)
      .Where(width=>width>0 && double.IsFinite(width)).Distinct().ToArray();
    string? action=null;
    while(true)
    {
      using var getter=new GetOption();
      getter.SetCommandPrompt($"Edit {CountText.Format(batches.Length,"seam allowance")}. Enter to apply");
      getter.AcceptNothing(true);
      int widthOption=getter.AddOption("Seam",edit.Width?.ToString("G6",CultureInfo.InvariantCulture)??
        (widths.Length==1?widths[0].ToString("G6",CultureInfo.InvariantCulture):"Varies"));
      int chamfer=getter.AddOptionList("Chamfer",["Keep","Yes","No"],edit.Chamfer is true?1:edit.Chamfer is false?2:0);
      int angle=getter.AddOption("ChamferAngle",edit.Angle?.ToString("G6",CultureInfo.InvariantCulture)??"Keep");
      int layer=getter.AddOption("Layer",edit.Layer??"Keep");
      var flip=new OptionToggle(edit.Flip,"Keep","Flip");
      getter.AddOptionToggle("FlipSide",ref flip);
      var scope=new OptionToggle(edit.WholeBoundary,"Selected","Part");
      getter.AddOptionToggle("Scope",ref scope);
      int detach=batches.Any(batch=>batch.Detached==null)?getter.AddOption("Detach"):-1;
      int reattach=batches.Any(batch=>batch.Detached!=null)?getter.AddOption("Reattach"):-1;
      int remove=getter.AddOption("Remove");
      int reselect=getter.AddOption("Reselect");
      var result=getter.Get();
      if(result==GetResult.Nothing)break;
      if(result!=GetResult.Option)return false;
      var option=getter.Option()!;
      if(option.Index==reselect)return false;
      edit=edit with {Flip=flip.CurrentValue,WholeBoundary=scope.CurrentValue};
      if(option.Index==detach){action="Detach";break;}
      if(option.Index==reattach){action="Reattach";break;}
      if(option.Index==remove){action="Remove";break;}
      if(option.Index==widthOption)
      {
        using var number=new GetNumber();
        number.SetCommandPrompt("New positive allowance width");
        number.SetDefaultNumber(edit.Width??(widths.Length==1?widths[0]:_settings.Allowance>0?_settings.Allowance:Defaults.Allowance));
        number.SetLowerLimit(0,true);
        if(number.Get()==GetResult.Number && double.IsFinite(number.Number()))edit=edit with {Width=number.Number()};
      }
      if(option.Index==chamfer)edit=edit with {Chamfer=option.CurrentListOptionIndex switch {1=>true,2=>false,_=>null}};
      if(option.Index==angle)
      {
        using var number=new GetNumber();
        number.SetCommandPrompt("New chamfer angle");
        number.SetDefaultNumber(edit.Angle??_settings.ChamferAngle);
        number.SetLowerLimit(0,false);
        number.SetUpperLimit(180,false);
        if(number.Get()==GetResult.Number && double.IsFinite(number.Number()))edit=edit with {Angle=number.Number()};
      }
      if(option.Index==layer && LayerSelector.TrySelect(doc,edit.Layer??_settings.Layer,DuplicateCommandSupport.CurrentLayerOption,
        "Seam output layer",mode,allowNewLayer:true,out var pickedLayer))edit=edit with {Layer=pickedLayer};
    }
    if(action==null && (edit with {WholeBoundary=false})==UnchangedEdit)return false;
    int changed=ApplySeamBatchEdits(doc,batches,edit,action??"Apply",picks,ReportSeamFailure);
    if(changed>0)RhinoApp.WriteLine($"vSeam: {(action=="Detach"?"Detached":action=="Reattach"?"Reattached":action=="Remove"?"Removed":"Updated")} {CountText.Format(changed,"seam allowance")}.");
    else RhinoApp.WriteLine("vSeam: No allowances changed.");
    return changed>0;
  }

  private static int ApplySeamBatchEdits(RhinoDoc doc,SeamBatch[] batches,EditOptions edit,string action,
    HashSet<Guid> picks,Action<Exception> onError)
  {
    int changed=0;
    foreach(var batch in batches)
    {
      try
      {
        switch(action)
        {
          case "Detach" when batch.Detached==null: DetachSeam(doc,batch); changed++; break;
          case "Remove":
            if(batch.Detached!=null || edit.WholeBoundary || ReadSourceIds(batch.Stamp).Any(id=>doc.Objects.FindId(id) is not {IsDeleted:false}))
            {RemoveSeam(doc,batch);changed++;}
            else if(RebuildSeam(doc,batch,UnchangedEdit with {Width=0},false,picks))changed++;
            break;
          case "Reattach" when batch.Detached!=null:
          case "Apply" when batch.Detached==null:
            if(RebuildSeam(doc,batch,edit,false,picks))changed++;
            break;
        }
      }
      catch(Exception ex){onError(ex);}
    }
    return changed;
  }

  private sealed record SeamSelectionState(int Available,int Attached,int Detached,bool Open,double? Width,
    bool? Chamfer,double? Angle,string? Layer,bool CanFlip);
  private sealed record SeamSelectionResult(int Changed,string Message);

  private static RhinoObject[] PanelCurves(RhinoDoc doc,Guid[] ids) => ids.Distinct().Select(doc.Objects.FindId)
    .OfType<RhinoObject>().Where(obj=>IsSeamEditCandidate(obj)||IsBoundarySource(obj)).ToArray();

  private static RhinoObject[] UnlinkedCurves(RhinoObject[] selection,SeamBatch[] batches)
  {
    var linked=batches.SelectMany(batch=>ReadSourceIds(batch.Stamp)).ToHashSet();
    return selection.Where(obj=>IsBoundarySource(obj) && !obj.IsReference && !obj.IsLocked && !linked.Contains(obj.Id)).ToArray();
  }

  /// <summary>Read the selected curves' seam settings for the Properties+ Seam panel.</summary>
  public static string GetSeamSelection(RhinoDoc doc,Guid[] ids)
  {
    var selection=PanelCurves(doc,ids);
    var batches=FindSeamBatches(doc,selection,true);
    var unlinked=UnlinkedCurves(selection,batches);
    var saved=new List<AllowanceProfile>();
    bool canFlip=false;
    var outputs=batches.SelectMany(batch=>batch.Outputs).Distinct().Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
    foreach(var batch in batches)
    {
      var profile=batch.Detached==null?GetSourceProfile(ReadSourceIds(batch.Stamp).Select(doc.Objects.FindId).OfType<RhinoObject>()):
        ParseProfile(doc.Objects.FindId(batch.Outputs[0])?.Attributes.GetUserString(DetachedProfileKey));
      if(profile!=null)saved.Add(profile);
      if(batch.Detached==null && profile?.Open==true)canFlip=true;
    }
    var settings=batches.Length==0?ReadPanelDefaults():Defaults;
    double defaultWidth=settings.Allowance>0?settings.Allowance:Defaults.Allowance;
    var widths=outputs.Where(obj=>obj.Attributes.GetUserString(RoleKey) is "edge" or "envelope" or ImportedJoinedRole)
      .Select(obj=>double.TryParse(obj.Attributes.GetUserString(DistanceKey),NumberStyles.Float,CultureInfo.InvariantCulture,out double width)?width:0)
      .Where(width=>width>0).Distinct().ToArray();
    var chamfer=saved.Select(profile=>profile.Chamfer??Defaults.Chamfer).Distinct().ToArray();
    var angles=saved.Select(profile=>profile.ChamferAngle??Defaults.ChamferAngle).Distinct().ToArray();
    var layers=outputs.Select(obj=>doc.Layers[obj.Attributes.LayerIndex].FullPath).Distinct().ToArray();
    return System.Text.Json.JsonSerializer.Serialize(new SeamSelectionState(unlinked.Length,batches.Count(batch=>batch.Detached==null),
      batches.Count(batch=>batch.Detached!=null),saved.Any(profile=>profile.Open==true)||unlinked.Any(obj=>obj.Geometry is Curve {IsClosed:false}),
      batches.Length==0?defaultWidth:widths.Length==1?widths[0]:null,
      batches.Length==0?settings.Chamfer:chamfer.Length==1?chamfer[0]:null,
      batches.Length==0?settings.ChamferAngle:angles.Length==1?angles[0]:null,
      batches.Length==0?settings.Layer:layers.Length==1?layers[0]:null,canFlip));
  }

  /// <summary>Choose a seam layer through the same vTools picker used by the command.</summary>
  public static string? PickSeamOutputLayer(RhinoDoc doc,string current)
    => LayerSelector.TrySelect(doc,string.IsNullOrEmpty(current)?Defaults.Layer:current,DuplicateCommandSupport.CurrentLayerOption,
      "Seam output layer",RunMode.Interactive,allowNewLayer:true,out var selected)?selected:null;

  /// <summary>Apply Properties+ edits using vSeam's normal planner and transactional edit actions.</summary>
  public static string ApplySeamSelection(RhinoDoc doc,Guid[] ids,string action,string options)
  {
    string ResultJson(int changed,string message) => System.Text.Json.JsonSerializer.Serialize(new SeamSelectionResult(changed,message));
    if(doc.InCommand(false)!=0)return ResultJson(0,"Finish the active Rhino command first.");
    EditOptions edit;
    try{edit=System.Text.Json.JsonSerializer.Deserialize<EditOptions>(options)??UnchangedEdit;}
    catch(System.Text.Json.JsonException){return ResultJson(0,"Invalid seam settings.");}
    if(edit.Width is double width && (!double.IsFinite(width)||width<=0) ||
      edit.Angle is double angle && (!double.IsFinite(angle)||angle<0||angle>180))return ResultJson(0,"Enter a positive width and an angle from 0 through 180.");
    if(action is not ("Enable" or "Apply" or "Detach" or "Reattach" or "Remove"))return ResultJson(0,"Unknown seam action.");
    if(action=="Apply" && (edit with {WholeBoundary=false})==UnchangedEdit)return ResultJson(0,"No settings changed.");
    var selection=PanelCurves(doc,ids);
    var batches=FindSeamBatches(doc,selection,true);
    var skipped=new List<string>();
    void OnError(Exception ex){skipped.Add(ex.Message);Log.Write(Section,$"Seam properties edit rejected: {ex}");}
    int changed=0;
    uint undo=doc.UndoRecordingEnabled?doc.BeginUndoRecord("Edit seam allowances"):0;
    try
    {
      if(action=="Enable")
      {
        var preferred=ReadPanelDefaults();
        var settings=preferred with {Type=SeamType.Part,Allowance=edit.Width??(preferred.Allowance>0?preferred.Allowance:Defaults.Allowance),
          Layer=edit.Layer??preferred.Layer,Chamfer=edit.Chamfer??preferred.Chamfer,ChamferAngle=edit.Angle??preferred.ChamferAngle};
        foreach(var obj in UnlinkedCurves(selection,batches))
        {
          try
          {
            var curve=(Curve)obj.Geometry;
            using var part=CaptureOutline(doc,curve.DuplicateCurve(),[obj],curve.PointAtStart);
            part.OffsetSide=edit.Flip?-DefaultOpenSide:DefaultOpenSide;
            using var existing=new Existing(part.Edges.Count);
            using var plan=BuildPlan(part,existing,settings,doc.ModelAbsoluteTolerance);
            if(Commit(doc,part,existing,plan,options:settings))changed++;
            else skipped.Add("Could not apply an allowance. See Rhino's command history.");
          }
          catch(Exception ex){OnError(ex);}
        }
      }
      else changed=ApplySeamBatchEdits(doc,batches,edit,action,selection.Select(obj=>obj.Id).ToHashSet(),OnError);
    }
    finally
    {
      if(undo!=0)doc.EndUndoRecord(undo);
      if(changed>0)doc.Views.Redraw();
    }
    string summary=changed>0?$"{CountText.Format(changed,"allowance")} changed.":"No allowances changed.";
    return ResultJson(changed,skipped.Count==0?summary:$"{summary} {skipped[0]}");
  }

  private static void RemoveSeam(RhinoDoc doc,SeamBatch batch)
  {
    var outputs=EditableOutputs(doc,batch);
    using var rollback=new SeamChanges(doc);
    if(batch.Detached==null)
      foreach(var id in ReadSourceIds(batch.Stamp))
      {
        if(doc.Objects.FindId(id) is not {IsDeleted:false} source ||
          source.Attributes.GetUserString(SourceOwnerKey)!=batch.Owner)continue;
        using var attributes=source.Attributes.Duplicate();
        attributes.SetUserString(ProfileKey,null);
        attributes.SetUserString(ProfileRootKey,null);
        rollback.SetAttributes(id,attributes);
      }
    foreach(var output in outputs)rollback.Delete(output);
    rollback.Complete();
  }

  private static void ReportSeamFailure(Exception ex)
  {
    Log.Write(Section,$"Seam edit/update rejected: {ex}");
    RhinoApp.WriteLine($"vSeam: {ex.Message} The previous allowance was kept.");
  }

  private static RhinoObject[] EditableOutputs(RhinoDoc doc,SeamBatch batch)
  {
    var outputs=batch.Outputs.Select(doc.Objects.FindId).ToArray();
    if(outputs.Any(obj=>obj is not {IsDeleted:false,IsLocked:false,IsReference:false,Geometry:Curve} ||
      obj.Attributes.GetUserString(StampKey)!=batch.Stamp || obj.Attributes.GetUserString(DetachedKey)!=batch.Detached))
      throw new InvalidOperationException("An allowance is unavailable, locked or referenced.");
    if(outputs.Where(obj=>obj!.Attributes.GetUserString(RoleKey) is "edge" or "envelope")
      .GroupBy(obj=>(obj!.Attributes.GetUserString(RoleKey),obj.Attributes.GetUserString(EdgeKey))).Any(group=>group.Skip(1).Any()))
      throw new InvalidOperationException("Duplicate allowance curves share the same source edge.");
    return outputs.Cast<RhinoObject>().ToArray();
  }

  private static void DetachSeam(RhinoDoc doc,SeamBatch batch)
  {
    var sources=BatchSources(doc,batch);
    var outputs=EditableOutputs(doc,batch);
    string key=Guid.NewGuid().ToString("N");
    string? profile=ProfileRoot(sources)?.Attributes.GetUserString(ProfileKey);
    var sourceGroups=sources.SelectMany(obj=>obj.Attributes.GetGroupList()??[]).ToHashSet();
    using var rollback=new SeamChanges(doc);
    foreach(var output in outputs)
    {
      using var attributes=output.Attributes.Duplicate();
      attributes.SetUserString(DetachedKey,key);
      attributes.SetUserString(DetachedProfileKey,profile);
      attributes.SetUserString(DetachedObjectKey,output.Id.ToString("N"));
      foreach(int group in (attributes.GetGroupList()??[]).Where(sourceGroups.Contains))attributes.RemoveFromGroup(group);
      rollback.SetAttributes(output.Id,attributes);
    }
    rollback.Complete();
  }

  private static bool RebuildSeam(RhinoDoc doc,SeamBatch batch,EditOptions edit,bool automatic,HashSet<Guid>? picks=null,
    IReadOnlyDictionary<Guid,Curve>? previousSources=null)
  {
    var sources=BatchSources(doc,batch);
    var root=ProfileRoot(sources)??sources[0];
    var outputs=EditableOutputs(doc,batch);
    bool changed=BoundaryIdentity(sources).Stamp!=batch.Stamp;
    if(automatic && !changed)return false;
    if(batch.Detached!=null && FindSeamBatches(doc,sources).Length>0)
      throw new InvalidOperationException("The source already has an attached allowance. Detach it before reattaching another.");
    if(sources.Any(obj=>obj.IsLocked||obj.IsReference))
      throw new InvalidOperationException("A source curve is locked or referenced.");
    double tolerance=doc.ModelAbsoluteTolerance;
    var profile=ParseProfile(batch.Detached==null?root.Attributes.GetUserString(ProfileKey):
      outputs[0].Attributes.GetUserString(DetachedProfileKey));
    double joinFactor=double.IsFinite(JoinToleranceFactor)&&JoinToleranceFactor>0?JoinToleranceFactor:BoundaryJoinToleranceFactor;
    var curves=sources.Select(obj=>(Curve)obj.Geometry).ToArray();
    int[] sourceChains=[0];
    var chain=curves.Length==1?[curves[0].DuplicateCurve()]:
      Curve.JoinCurves(curves,tolerance*joinFactor,false,out sourceChains);
    if(chain.Length!=1)
    {
      try
      {
        if(chain.Length<2 || !automatic || profile==null || sourceChains.Length!=sources.Length ||
          sourceChains.Any(index=>index<0||index>=chain.Length) || sourceChains.Distinct().Count()!=chain.Length)
          throw new InvalidOperationException("The sources no longer form a single boundary.");
        RebuildSeparatedSeam(doc,sources,outputs,profile,chain,sourceChains,tolerance*joinFactor,previousSources);
        return true;
      }
      finally { foreach(var curve in chain)curve.Dispose(); }
    }
    using var part=CaptureOutline(doc,chain[0],sources.ToList(),chain[0].PointAtStart,profile?.OpenPlaneNormal);
    part.CapStart=profile?.CapStart??DefaultOpenCap;
    part.CapEnd=profile?.CapEnd??DefaultOpenCap;
    if(part.Sources.Length!=sources.Length || part.Owner!=batch.Owner)
      throw new InvalidOperationException("The source curves no longer identify this allowance unambiguously.");
    if(!part.Outline.IsClosed)
    {
      int side=profile?.Side is int saved && saved is 1 or -1?saved:
        changed?0:InferOpenSide(part,outputs.Select(obj=>(Curve)obj.Geometry),tolerance);
      if(side==0)throw new InvalidOperationException("The saved allowance side is unknown. Choose a side in Part mode.");
      part.OffsetSide=edit.Flip?-side:side;
    }
    else if(edit==new EditOptions(Flip:true) && batch.Detached==null)return false;
    using var existing=new Existing(part.Edges.Count){OpenSide=part.OffsetSide};
    if(profile==null || !MapProfile(doc,part,root,profile,existing.Distances,changed,out _))
      throw new InvalidOperationException("The saved edge widths cannot be matched safely. Rebuild in Part mode.");
    if(edit.Width is double width)
    {
      var affected=edit.WholeBoundary||picks==null?null:SelectedSeamEdges(part,sources,outputs,picks,tolerance,changed);
      if(affected?.Count==0)throw new InvalidOperationException("Pick an allowance edge or finished curve to change its width, or use Part scope.");
      for(int i=0;i<existing.Distances.Length;i++)
        if(existing.Distances[i]>0 && (affected==null||affected.Contains(i)))existing.Distances[i]=width;
    }
    var settings=Defaults with {Type=SeamType.Edge,Chamfer=edit.Chamfer??profile.Chamfer??Defaults.Chamfer,
      ChamferAngle=edit.Angle??profile.ChamferAngle??Defaults.ChamferAngle};
    part.SelectedEdges=[];
    using var plan=BuildPlan(part,existing,settings,tolerance);
    if(!existing.Distances.Any(distance=>distance>0) && edit.Width==0)
    {RemoveSeam(doc,batch);return true;}
    if(!plan.Pieces.Any(piece=>piece.Allowance>0))throw new InvalidOperationException("No valid allowance could be rebuilt.");
    int layer=edit.Layer==null?-1:LayerSelector.IsCurrentLayerValue(edit.Layer,DuplicateCommandSupport.CurrentLayerOption)
      ?doc.Layers.CurrentLayerIndex:UzipCommon.EnsureLayer(doc,edit.Layer,NewCutLayerColor);
    if(edit.Layer!=null && layer<0)throw new InvalidOperationException("Cannot create the seam output layer.");
    if(layer<0 && outputs.Select(obj=>obj.Attributes.LayerIndex).Distinct().Skip(1).Any())
      throw new InvalidOperationException("The allowance uses multiple layers. Choose one output layer to rebuild it.");

    var pieces=plan.Pieces.Where(piece=>piece.Allowance>0).ToArray();
    Curve[] joined=[];
    if(outputs.Length==1 && outputs[0].Attributes.GetUserString(RoleKey) is "envelope" or ImportedJoinedRole)
    {
      joined=Curve.JoinCurves(pieces.Select(piece=>piece.Curve),tolerance);
      if(joined.Length==1)pieces=[new Piece(joined[0],joined[0].IsClosed?"envelope":ImportedJoinedRole,0,pieces.Max(piece=>piece.Allowance))];
    }
    try
    {
      var previous=outputs.ToLookup(obj=>(obj.Attributes.GetUserString(RoleKey),obj.Attributes.GetUserString(EdgeKey)));
      var used=new HashSet<Guid>();
      using var rollback=new SeamChanges(doc);
      foreach(var piece in pieces)
      {
        var original=outputs.Length==1 && pieces.Length==1?outputs[0]:
          previous[(piece.Role,piece.Edge.ToString(CultureInfo.InvariantCulture))].FirstOrDefault(obj=>!used.Contains(obj.Id));
        using var attributes=(original??outputs[0]).Attributes.Duplicate();
        if(layer>=0)attributes.LayerIndex=layer;
        if(batch.Detached!=null)
          foreach(int group in part.Groups)attributes.AddToGroup(group);
        string? name=attributes.Name;
        var colorSource=attributes.ColorSource;
        SetAllowanceAttributes(attributes,piece.Curve,part.Owner,part.Stamp,piece.Role,piece.Edge,piece.Allowance);
        attributes.Name=name;
        attributes.ColorSource=colorSource;
        attributes.SetUserString(DetachedKey,null);
        attributes.SetUserString(DetachedProfileKey,null);
        attributes.SetUserString(DetachedObjectKey,null);
        if(original==null){rollback.Add(piece.Curve,attributes);continue;}
        var id=original.Id;
        used.Add(id);
        if(!GeometryBase.GeometryEquals(original.Geometry,piece.Curve))rollback.Replace(id,piece.Curve);
        rollback.SetAttributes(id,attributes);
      }
      string serialized=SerializeProfile(doc,part,root,existing.Distances,settings,part.OffsetSide);
      foreach(var source in sources)
      {
        using var attributes=source.Attributes.Duplicate();
        attributes.SetUserString(ProfileKey,serialized);
        attributes.SetUserString(ProfileRootKey,source.Id==root.Id?"1":null);
        rollback.SetAttributes(source.Id,attributes);
      }
      foreach(var output in outputs.Where(obj=>!used.Contains(obj.Id)))rollback.Delete(output);
      rollback.Complete();
      return true;
    }
    finally{foreach(var curve in joined)curve.Dispose();}
  }

  private static void RebuildSeparatedSeam(RhinoDoc doc,RhinoObject[] sources,RhinoObject[] outputs,
    AllowanceProfile profile,Curve[] chains,int[] sourceChains,double joinTolerance,
    IReadOnlyDictionary<Guid,Curve>? previousSources)
  {
    if(outputs.Select(obj=>obj.Attributes.LayerIndex).Distinct().Skip(1).Any())
      throw new InvalidOperationException("The allowance uses several output layers. Set one layer in Edit before moving sources apart.");
    var settings=Defaults with { Type=SeamType.Edge, Chamfer=profile.Chamfer??Defaults.Chamfer,
      ChamferAngle=profile.ChamferAngle??Defaults.ChamferAngle };
    var directions=profile.SourceDirections;
    var normal=profile.OpenPlaneNormal;
    int side=profile.Open==false?DefaultOpenSide:profile.Side.GetValueOrDefault();
    if(directions==null || normal is not {Length:3} || sources.Any(source=>directions.GetValueOrDefault(source.Id) is not (1 or -1)))
    {
      var previous=sources.Select(obj=>previousSources?.GetValueOrDefault(obj.Id)??(Curve)obj.Geometry).ToArray();
      var oldChains=JoinSeamSources(previous,joinTolerance);
      try
      {
        if(oldChains.Length==1)
        {
          var outline=oldChains[0];
          int anchor=Array.IndexOf(sources,sources.MinBy(obj=>obj.Id)!);
          var plane=OrientOutline(doc,outline,previous[anchor],normal);
          normal=[plane.Normal.X,plane.Normal.Y,plane.Normal.Z];
          directions=sources.Select((source,i)=>(source.Id,Sign:DirectionOnOutline(previous[i],outline,joinTolerance)))
            .ToDictionary(item=>item.Id,item=>item.Sign);
          if(outline.IsClosed)side=DefaultOpenSide;
        }
      }
      finally { foreach(var curve in oldChains)curve.Dispose(); }
    }

    double tolerance=doc.ModelAbsoluteTolerance;
    using var rollback=new SeamChanges(doc);
    for(int i=0;i<chains.Length;i++)
    {
      var members=sources.Where((_,index)=>sourceChains[index]==i).ToList();
      using var part=CaptureOutline(doc,chains[i].DuplicateCurve(),members,chains[i].PointAtStart,normal);
      if(part.Sources.Length!=members.Count)
        throw new InvalidOperationException("Could not identify the sources of a separated boundary.");
      var keys=ProfileEdgeSources(part,members,0,tolerance);
      if(!MapSeparatedWidths(profile,keys,out var distances))
        throw new InvalidOperationException("Could not match the saved widths to the separated curves.");
      var root=members.MinBy(obj=>obj.Id)!;
      if(!part.Outline.IsClosed && distances.Any(width=>width>0))
      {
        int oldDirection=directions?.GetValueOrDefault(root.Id)??0;
        int newDirection=DirectionOnOutline((Curve)root.Geometry,part.Outline,joinTolerance);
        if(oldDirection==0 || newDirection==0 || side is not (1 or -1) || normal is not {Length:3})
          throw new InvalidOperationException("The previous allowance side is unknown. Reconnect the source curves before moving them apart.");
        var previousNormal=new Vector3d(normal[0],normal[1],normal[2]);
        part.OffsetSide=side*oldDirection*newDirection*(previousNormal*part.Plane.Normal<0?-1:1);
      }
      StageSeamComponent(doc,rollback,part,members,root,distances,settings,outputs[0]);
    }
    foreach(var output in outputs)rollback.Delete(output);
    rollback.Complete();
  }

  private static bool MapSeparatedWidths(AllowanceProfile profile,string[] edgeSources,out double[] distances)
  {
    distances=new double[edgeSources.Length];
    if(profile.Distances.All(width=>width==profile.Distances[0]))
    {
      Array.Fill(distances,profile.Distances[0]);
      return true;
    }
    if(profile.EdgeSources is not { } keys || keys.Length!=profile.Distances.Length)return false;
    var widths=keys.Zip(profile.Distances).Where(item=>!string.IsNullOrEmpty(item.First))
      .ToLookup(item=>item.First,item=>item.Second,StringComparer.Ordinal);
    for(int i=0;i<edgeSources.Length;i++)
    {
      var candidates=widths[edgeSources[i]].Distinct().Take(2).ToArray();
      if(candidates.Length!=1)return false;
      distances[i]=candidates[0];
    }
    return true;
  }

  private static void StageSeamComponent(RhinoDoc doc,SeamChanges changes,Part part,IEnumerable<RhinoObject> sources,
    RhinoObject root,double[] distances,SeamOptions settings,RhinoObject template,bool newSourceLineage=false,bool useCommonGroups=false,
    IReadOnlySet<Guid>? joinedSources=null)
  {
    using var existing=new Existing(part.Edges.Count){OpenSide=part.OffsetSide};
    Array.Copy(distances,existing.Distances,distances.Length);
    part.SelectedEdges=[];
    using var plan=BuildPlan(part,existing,settings,doc.ModelAbsoluteTolerance);
    string owner=SourceIdentity(part.Sources.Select(source=>source.Id));
    bool hasAllowance=plan.Pieces.Any(piece=>piece.Allowance>0);
    if(!hasAllowance && distances.Any(distance=>distance>0))
      throw new InvalidOperationException("No valid allowance could be rebuilt for a source component.");
    string? profile=hasAllowance?SerializeProfile(doc,part,root,distances,settings,part.OffsetSide,joinedSources):null;
    foreach(var source in sources)
    {
      using var attributes=source.Attributes.Duplicate();
      string lineage=source.Id.ToString("D");
      LinkFinishedSource(attributes,owner,root.Id.ToString("D"),newSourceLineage?lineage:attributes.GetUserString(SourceCurveKey)??lineage);
      attributes.SetUserString(ProfileKey,profile);
      attributes.SetUserString(ProfileRootKey,hasAllowance&&source.Id==root.Id?"1":null);
      if(joinedSources?.Contains(source.Id)==true)attributes.SetUserString(JoinedSectionsKey,"1");
      changes.SetAttributes(source.Id,attributes);
    }
    foreach(var piece in plan.Pieces.Where(piece=>piece.Allowance>0))
    {
      using var output=template.Attributes.Duplicate();
      if(useCommonGroups)
      {
        foreach(int group in output.GetGroupList()??[])output.RemoveFromGroup(group);
        foreach(int group in part.Groups)output.AddToGroup(group);
      }
      SetAllowanceAttributes(output,piece.Curve,owner,part.Stamp,piece.Role,piece.Edge,piece.Allowance);
      output.Name=template.Attributes.Name;
      output.ColorSource=template.Attributes.ColorSource;
      changes.Add(piece.Curve,output);
    }
  }

  private static bool IsEndpointSource(RhinoObject obj) => IsBoundarySource(obj) &&
    !obj.IsHidden && !obj.IsLocked && !obj.IsReference && obj.Geometry is Curve {IsClosed:false};

  private static bool ExtendSeamBoundaries(RhinoDoc doc,IReadOnlySet<Guid> changed,ref uint undo)
  {
    if(changed.Count==0 || !changed.Select(doc.Objects.FindId).OfType<RhinoObject>().Any(IsEndpointSource) ||
      !(doc.Objects.FindByUserString(OwnerKey,"*",true,false,true,ObjectType.Curve)??[])
      .Any(obj=>!obj.IsDeleted && !IsDetached(obj)))return false;
    double joinFactor=double.IsFinite(JoinToleranceFactor)&&JoinToleranceFactor>0?JoinToleranceFactor:BoundaryJoinToleranceFactor;
    double tolerance=doc.ModelAbsoluteTolerance*joinFactor;
    var sources=doc.Objects.GetObjectList(ObjectType.Curve).Where(IsEndpointSource).ToArray();
    var endpoints=sources.SelectMany(obj=>new[]{((Curve)obj.Geometry).PointAtStart,((Curve)obj.Geometry).PointAtEnd}).ToArray();
    using var tree=new RTree();
    for(int i=0;i<endpoints.Length;i++)tree.Insert(endpoints[i],i);
    var visited=new HashSet<int>();
    bool rebuilt=false;
    for(int i=0;i<sources.Length;i++)
    {
      if(!changed.Contains(sources[i].Id) || !visited.Add(i))continue;
      var component=new List<RhinoObject>();
      var pending=new Queue<int>();
      pending.Enqueue(i);
      bool branched=false;
      while(pending.TryDequeue(out int index))
      {
        component.Add(sources[index]);
        for(int end=index*2;end<=index*2+1;end++)
        {
          int matches=0;
          tree.Search(new Sphere(endpoints[end],tolerance),(_,hit)=>
          {
            int other=hit.Id/2;
            if(other==index)return;
            matches++;
            if(visited.Add(other))pending.Enqueue(other);
          });
          if(matches>1)branched=true;
        }
      }
      if(branched || component.Count<2)continue;
      try { rebuilt|=ExtendSeamBoundary(doc,component,tolerance,ref undo); }
      catch(Exception ex){Log.Write(Section,$"Could not extend connected seam allowances: {ex}");}
    }
    return rebuilt;
  }

  private static bool ExtendSeamBoundary(RhinoDoc doc,List<RhinoObject> sources,double joinTolerance,ref uint undo)
  {
    var batches=FindSeamBatches(doc,sources);
    if(batches.Length==0)return false;
    var ids=sources.Select(source=>source.Id).ToHashSet();
    var linkedIds=batches.SelectMany(batch=>ReadSourceIds(batch.Stamp)).ToArray();
    if(linkedIds.Distinct().Count()!=linkedIds.Length || linkedIds.Any(id=>!ids.Contains(id)) ||
      batches.Length==1 && linkedIds.Length==sources.Count)return false;
    var linkedSet=linkedIds.ToHashSet();
    var extensions=sources.Where(source=>!linkedSet.Contains(source.Id)).ToArray();
    // Do not adopt an explicitly detached allowance or a copied source whose original is elsewhere.
    if(extensions.Any(source=>source.Attributes.GetUserString(SourceOwnerKey)!=null && !IsFinishedCut(source)))return false;
    var joined=JoinSeamSources(sources.OrderBy(source=>source.Id).Select(source=>(Curve)source.Geometry),joinTolerance);
    if(joined.Length!=1)
    {
      foreach(var curve in joined)curve.Dispose();
      return false;
    }
    using var part=CaptureOutline(doc,joined[0],sources,joined[0].PointAtStart);
    if(part.Sources.Length!=sources.Count)return false;

    var oldParts=new List<Part>();
    var outputs=new List<RhinoObject>();
    var widths=new List<(Curve Edge,double Width)>();
    SeamOptions? settings=null;
    int side=0;
    try
    {
      foreach(var batch in batches)
      {
        var current=BatchSources(doc,batch);
        var root=ProfileRoot(current);
        var profile=ParseProfile(root?.Attributes.GetUserString(ProfileKey));
        if(root==null || profile==null)return false;
        var previous=JoinSeamSources(current.Select(source=>(Curve)source.Geometry),joinTolerance);
        if(previous.Length!=1)
        {
          foreach(var curve in previous)curve.Dispose();
          return false;
        }
        var old=CaptureOutline(doc,previous[0],current.ToList(),previous[0].PointAtStart,profile.OpenPlaneNormal);
        oldParts.Add(old);
        if(old.Sources.Length!=current.Length)return false;
        var saved=new double[old.Edges.Count];
        if(!MapProfile(doc,old,root,profile,saved,old.Stamp!=batch.Stamp,out _))return false;
        widths.AddRange(old.Edges.Select((edge,index)=>(edge,saved[index])));
        var currentOutputs=EditableOutputs(doc,batch);
        outputs.AddRange(currentOutputs);
        var options=Defaults with {Type=SeamType.Edge,Chamfer=profile.Chamfer??Defaults.Chamfer,
          ChamferAngle=profile.ChamferAngle??Defaults.ChamferAngle};
        if(settings!=null && (settings.Chamfer!=options.Chamfer ||
          options.Chamfer && settings.ChamferAngle!=options.ChamferAngle))return false;
        settings=options;
        if(!part.Outline.IsClosed && saved.Any(width=>width>0))
        {
          var reference=(Curve)root.Geometry;
          int oldDirection=DirectionOnOutline(reference,old.Outline,joinTolerance);
          int newDirection=DirectionOnOutline(reference,part.Outline,joinTolerance);
          int oldSide=profile.Side is 1 or -1?profile.Side.Value:
            InferOpenSide(old,currentOutputs.Select(obj=>(Curve)obj.Geometry),doc.ModelAbsoluteTolerance);
          int sameNormal=old.Plane.Normal*part.Plane.Normal<0?-1:1;
          int currentSide=oldSide*oldDirection*newDirection*sameNormal;
          if(currentSide==0 || side!=0 && side!=currentSide)return false;
          side=currentSide;
        }
      }
      if(outputs.Count==0 || outputs.Select(obj=>obj.Attributes.LayerIndex).Distinct().Skip(1).Any())return false;
      widths.AddRange(extensions.Where(IsFinishedCut).Select(source=>((Curve)source.Geometry,0.0)));
      var unknown=extensions.Where(source=>!IsFinishedCut(source)).Select(source=>(Curve)source.Geometry).ToArray();
      var spans=widths.GroupBy(item=>item.Width).Select(group=>(Width:group.Key,Edges:group.Select(item=>item.Edge).ToArray())).ToArray();
      var assigned=new double?[part.Edges.Count];
      for(int i=0;i<assigned.Length;i++)
      {
        var edge=part.Edges[i];
        var matches=spans.Where(span=>OnTargets(edge,span.Edges,doc.ModelAbsoluteTolerance)).Take(2).ToArray();
        if(matches.Length==1)assigned[i]=matches[0].Width;
        else if(matches.Length>1 || !OnTargets(edge,unknown,doc.ModelAbsoluteTolerance))return false;
      }
      if(!InheritAdjacentWidths(assigned,part.Outline.IsClosed,out var distances))return false;
      if(side!=0)part.OffsetSide=side;
      if(undo==0 && !doc.UndoRecordingIsActive && doc.UndoRecordingEnabled)undo=doc.BeginUndoRecord("Update seam allowances");
      using var rollback=new SeamChanges(doc);
      StageSeamComponent(doc,rollback,part,sources,sources.MinBy(obj=>obj.Id)!,distances,settings!,outputs[0],useCommonGroups:true);
      foreach(var output in outputs)rollback.Delete(output);
      rollback.Complete();
      return true;
    }
    finally{foreach(var old in oldParts)old.Dispose();}
  }

  private static bool InheritAdjacentWidths(double?[] assigned,bool closed,out double[] result)
  {
    int count=assigned.Length;
    result=new double[count];
    int first=Array.FindIndex(assigned,width=>width.HasValue);
    if(first<0)return false;
    int previous=first;
    result[first]=assigned[first]!.Value;
    if(!closed)Array.Fill(result,result[first],0,first);
    int limit=closed?first+count:count-1;
    for(int current=first+1;current<=limit;current++)
    {
      if(assigned[current%count] is not double nextWidth)continue;
      double previousWidth=result[previous%count];
      for(int i=previous+1;i<current;i++)
        result[i%count]=i-previous<=current-i?previousWidth:nextWidth;
      result[current%count]=nextWidth;
      previous=current;
    }
    if(!closed)Array.Fill(result,result[previous],previous+1,count-previous-1);
    return true;
  }

  private static HashSet<int>? SelectedSeamEdges(Part part,RhinoObject[] sources,RhinoObject[] outputs,
    HashSet<Guid> picks,double tolerance,bool changed)
  {
    var pickedOutputs=outputs.Where(obj=>picks.Contains(obj.Id)).ToArray();
    if(pickedOutputs.Any(obj=>obj.Attributes.GetUserString(RoleKey) is "envelope" or ImportedJoinedRole))return null;
    var pickedSources=sources.Where(obj=>picks.Contains(obj.Id)).Select(obj=>(Curve)obj.Geometry).ToArray();
    var edges=new HashSet<int>();
    for(int i=0;i<part.Edges.Count;i++)
      if(pickedSources.Length>0 && OnTargets(part.Edges[i],pickedSources,tolerance))edges.Add(i);
    if(!changed)
      foreach(var output in pickedOutputs)
        if(output.Attributes.GetUserString(RoleKey)=="edge" &&
          int.TryParse(output.Attributes.GetUserString(EdgeKey),out int edge) && edge>=0 && edge<part.Edges.Count)edges.Add(edge);
    return edges;
  }

  private sealed class SeamChanges(RhinoDoc doc) : IDisposable
  {
    private readonly Stack<Action> _rollback=new();
    private readonly List<IDisposable> _snapshots=[];
    internal void Complete()=>_rollback.Clear();
    internal void SetAttributes(Guid id,ObjectAttributes attributes)
    {
      var before=doc.Objects.FindId(id)!.Attributes.Duplicate();
      _snapshots.Add(before);
      if(AttributeStamp(before)==AttributeStamp(attributes))return;
      if(!doc.Objects.ModifyAttributes(id,attributes,true))throw new InvalidOperationException("Cannot save the seam attributes.");
      _rollback.Push(()=>Check(doc.Objects.ModifyAttributes(id,before,true),id));
    }
    internal void Replace(Guid id,Curve curve)
    {
      var before=((Curve)doc.Objects.FindId(id)!.Geometry).DuplicateCurve();
      _snapshots.Add(before);
      if(!doc.Objects.Replace(id,curve))throw new InvalidOperationException("Cannot replace the allowance curve.");
      _rollback.Push(()=>Check(doc.Objects.Replace(id,before),id));
    }
    internal void Add(Curve curve,ObjectAttributes attributes)
    {
      var id=doc.Objects.AddCurve(curve,attributes);
      if(id==Guid.Empty)throw new InvalidOperationException("Cannot add the complete allowance.");
      _rollback.Push(()=>Check(doc.Objects.Delete(id,true),id));
    }
    internal void Delete(RhinoObject obj)
    {
      uint serial=obj.RuntimeSerialNumber;
      var id=obj.Id;
      if(!doc.Objects.Delete(id,true))throw new InvalidOperationException("Cannot remove an outdated allowance curve.");
      _rollback.Push(()=>Check(doc.Objects.Undelete(serial),id));
    }
    private static void Check(bool success,Guid id)
    {
      if(!success)Log.Write(Section,$"Failed to restore allowance object {id}.");
    }
    public void Dispose()
    {
      while(_rollback.TryPop(out var restore))
        try{restore();}catch(Exception ex){Log.Write(Section,$"Seam rollback failed: {ex}");}
      foreach(var snapshot in _snapshots)snapshot.Dispose();
    }
  }

  private sealed class SourceChanges : IDisposable
  {
    internal Dictionary<Guid,Curve> Before { get; }=[];
    internal HashSet<Guid> Connections { get; }=[];
    public void Dispose(){foreach(var curve in Before.Values)curve.Dispose();}
  }

  private static readonly Dictionary<uint,SourceChanges> PendingSourceChanges=[];
  private static readonly Dictionary<uint,SourceTopologyTracking> ActiveTopologyChanges=[];

  private sealed class SourceTopologyTracking(string command) : IDisposable
  {
    internal string Command { get; }=command;
    internal bool Splitting { get; }=command is "Split" or "vSplit";
    internal bool Filleting { get; }=command is "Fillet" or "FilletCrv";
    internal HashSet<Guid> NewIds { get; }=[];
    internal HashSet<Guid> ChangedIds { get; }=[];
    internal Dictionary<string,SplitBatch> Batches { get; }=[];
    internal Dictionary<Guid,Curve> Originals { get; }=[];
    public void Dispose()
    {
      foreach(var batch in Batches.Values)batch.Dispose();
      foreach(var curve in Originals.Values)curve.Dispose();
    }
  }

  private sealed record SplitBatch(SeamBatch Batch,Part Part,Dictionary<Guid,Curve> Sources,AllowanceProfile Profile,
    double[] Distances,int Side) : IDisposable
  {
    public void Dispose(){Part.Dispose();foreach(var curve in Sources.Values)curve.Dispose();}
  }

  internal static void StartSourceTracking()
  {
    RhinoDoc.ReplaceRhinoObject+=OnSourceReplaced;
    RhinoDoc.BeforeTransformObjects+=OnSourcesTransforming;
    RhinoDoc.AddRhinoObject+=OnSeamObjectAdded;
    RhinoDoc.DeleteRhinoObject+=OnTopologyObjectDeleted;
    RhinoDoc.CloseDocument+=OnSeamDocumentClosed;
    Command.BeginCommand+=OnSeamCommandBeginning;
    Command.EndCommand+=OnSeamCommandEnded;
    RhinoApp.Idle+=UpdateSourceChanges;
  }

  internal static void StopSourceTracking()
  {
    RhinoApp.Idle-=UpdateSourceChanges;
    Command.EndCommand-=OnSeamCommandEnded;
    Command.BeginCommand-=OnSeamCommandBeginning;
    RhinoDoc.CloseDocument-=OnSeamDocumentClosed;
    RhinoDoc.DeleteRhinoObject-=OnTopologyObjectDeleted;
    RhinoDoc.AddRhinoObject-=OnSeamObjectAdded;
    RhinoDoc.BeforeTransformObjects-=OnSourcesTransforming;
    RhinoDoc.ReplaceRhinoObject-=OnSourceReplaced;
    foreach(var number in PendingSourceChanges.Keys.ToArray())DiscardSourceChanges(number);
    foreach(var topology in ActiveTopologyChanges.Values)topology.Dispose();
    ActiveTopologyChanges.Clear();
  }

  private static void OnSeamDocumentClosed(object? sender,DocumentEventArgs e)
  {
    DiscardSourceChanges(e.Document.RuntimeSerialNumber);
    if(ActiveTopologyChanges.Remove(e.Document.RuntimeSerialNumber,out var topology))topology.Dispose();
  }

  private static void OnSourceReplaced(object? sender,RhinoReplaceObjectEventArgs e)
  {
    if(e.OldRhinoObject is {Geometry:Curve} before)
    {
      CaptureTopologySource(before);
      QueueSourceChange(before);
    }
  }

  private static bool TracksTopology(string name) => name is "Split" or "vSplit" or "Join" or "Fillet" or "FilletCrv";

  private static void OnSeamCommandBeginning(object? sender,CommandEventArgs e)
  {
    if(!TracksTopology(e.CommandEnglishName) || e.Document is not {IsHeadless:false} doc)return;
    ActiveTopologyChanges.TryAdd(doc.RuntimeSerialNumber,new SourceTopologyTracking(e.CommandEnglishName));
  }

  private static void OnSeamCommandEnded(object? sender,CommandEventArgs e)
  {
    if(e.Document is not {IsHeadless:false} doc)return;
    if(ActiveTopologyChanges.TryGetValue(doc.RuntimeSerialNumber,out var topology) && topology.Command==e.CommandEnglishName &&
      ActiveTopologyChanges.Remove(doc.RuntimeSerialNumber))
      using(topology)
        if(e.CommandResult==Result.Success)
        {
          if(topology.Splitting)TransferSplitAllowances(doc,topology);
          else
          {
            TransferJoinedAllowances(doc,topology);
            foreach(var id in topology.NewIds.Concat(topology.ChangedIds).Distinct())
              if(doc.Objects.FindId(id) is {IsDeleted:false} obj)QueueSourceChange(obj,capturePrevious:false);
          }
        }
    if(e.CommandEnglishName==Section && PendingSourceChanges.TryGetValue(doc.RuntimeSerialNumber,out var pending))
      pending.Connections.Clear();
    if(doc.UndoRecordingIsActive && RhinoApp.InCommand<=1)UpdateSourceChanges(doc);
  }

  private static void OnSeamObjectAdded(object? sender,RhinoObjectEventArgs e)
  {
    if(e.TheObject is not {Geometry:Curve} obj || obj.Document is not { } doc)return;
    if(ActiveTopologyChanges.TryGetValue(doc.RuntimeSerialNumber,out var topology))topology.NewIds.Add(obj.Id);
    else QueueSourceChange(obj,capturePrevious:false);
  }

  private static void OnTopologyObjectDeleted(object? sender,RhinoObjectEventArgs e)
  {
    if(ActiveTopologyChanges.Count>0 && e.TheObject is {Geometry:Curve} obj)CaptureTopologySource(obj);
  }

  private static void CaptureTopologySource(RhinoObject obj)
  {
    if(ActiveTopologyChanges.Count==0 || obj.Document is not {UndoActive:false,RedoActive:false} doc ||
      !ActiveTopologyChanges.TryGetValue(doc.RuntimeSerialNumber,out var topology) || obj.Geometry is not Curve curve ||
      obj.Attributes.GetUserString(OwnerKey)!=null || topology.NewIds.Contains(obj.Id) || !topology.ChangedIds.Add(obj.Id))return;
    if(!topology.Splitting)topology.Originals.Add(obj.Id,curve.DuplicateCurve());
    if(obj.Attributes.GetUserString(SourceOwnerKey) is not { } owner)return;
    var groups=obj.Attributes.GetGroupList()??[];
    var matching=(doc.Objects.FindByUserString(OwnerKey,owner,true,false,true,ObjectType.Curve)??[])
      .Where(output=>!output.IsDeleted && !IsDetached(output) && (groups.Length==0 || groups.Contains(SmallestGroup(doc,output))))
      .GroupBy(output=>output.Attributes.GetUserString(StampKey));
    foreach(var group in matching)
    {
      if(group.Key is not { } stamp || topology.Batches.ContainsKey(stamp))continue;
      var ids=ReadSourceIds(stamp);
      if(!ids.Contains(obj.Id))continue;
      try
      {
        var sources=ids.Select(id=>id==obj.Id?obj:doc.Objects.FindId(id)).OfType<RhinoObject>().ToArray();
        if(sources.Length!=ids.Length || sources.Any(source=>source.Geometry is not Curve))continue;
        var root=ProfileRoot(sources);
        var profile=ParseProfile(root?.Attributes.GetUserString(ProfileKey));
        if(root==null || profile==null)continue;
        var curves=sources.Select(source=>(Curve)source.Geometry).ToArray();
        var chain=JoinSeamSources(curves,doc.ModelAbsoluteTolerance*JoinToleranceFactor);
        if(chain.Length!=1){foreach(var fragment in chain)fragment.Dispose();continue;}
        var part=CaptureOutline(doc,chain[0],sources.ToList(),chain[0].PointAtStart,profile.OpenPlaneNormal);
        bool retained=false;
        try
        {
          double[] distances=new double[part.Edges.Count];
          if(!MapProfile(doc,part,root,profile,distances,part.Stamp!=stamp,out _))continue;
          int side=part.Outline.IsClosed?DefaultOpenSide:profile.Side??InferOpenSide(part,group.Select(output=>(Curve)output.Geometry),doc.ModelAbsoluteTolerance);
          if(side is not (1 or -1))continue;
          var batch=new SeamBatch(owner,stamp,null,group.Select(output=>output.Id).ToArray());
          topology.Batches[stamp]=new SplitBatch(batch,part,sources.ToDictionary(source=>source.Id,source=>((Curve)source.Geometry).DuplicateCurve()),profile,distances,side);
          retained=true;
        }
        finally{if(!retained)part.Dispose();}
      }
      catch(Exception ex){Log.Write(Section,$"Could not snapshot a seam before a source topology change: {ex}");}
    }
  }

  private static void TransferSplitAllowances(RhinoDoc doc,SourceTopologyTracking split)
  {
    if(split.Batches.Count==0)return;
    var added=split.NewIds.Concat(split.ChangedIds).Distinct().Select(doc.Objects.FindId)
      .Where(obj=>obj is {IsDeleted:false,Geometry:Curve}).Cast<RhinoObject>().ToArray();
    uint undo=0;
    try
    {
      if(!doc.UndoRecordingIsActive && doc.UndoRecordingEnabled)undo=doc.BeginUndoRecord("Transfer split seam allowances");
      foreach(var saved in split.Batches.Values)
      {
        try{TransferSplitAllowance(doc,split,saved,added);}
        catch(Exception ex){ReportSeamFailure(ex);}
      }
    }
    finally{if(undo!=0)doc.EndUndoRecord(undo);}
    doc.Views.Redraw();
  }

  private static void TransferSplitAllowance(RhinoDoc doc,SourceTopologyTracking split,SplitBatch saved,RhinoObject[] added)
  {
    double tolerance=doc.ModelAbsoluteTolerance;
    var sections=new List<RhinoObject>();
    var sourceIds=saved.Sources.Keys.ToHashSet();
    bool splitConfirmed=false;
    foreach(var (id,old) in saved.Sources)
    {
      var current=doc.Objects.FindId(id);
      if(current is {IsDeleted:false,Geometry:Curve} && (!split.ChangedIds.Contains(id) || GeometryBase.GeometryEquals(current.Geometry,old)))
      {sections.Add(current);continue;}
      var children=added.Where(obj=>(obj.Id==id || !sourceIds.Contains(obj.Id)) &&
        obj.Attributes.GetUserString(SourceOwnerKey)==saved.Batch.Owner &&
        OnBoundary((Curve)obj.Geometry,old,tolerance)).ToArray();
      var curves=children.Select(obj=>(Curve)obj.Geometry).ToArray();
      bool covers=curves.Length>1 && Math.Abs(curves.Sum(curve=>curve.GetLength())-old.GetLength())<=tolerance*curves.Length &&
        OnTargets(old,curves,tolerance) && !curves.Select((curve,index)=>(curve,index)).Any(item=>curves.Take(item.index)
          .Any(other=>Intersection.CurveCurve(item.curve,other,tolerance,tolerance)?.Any(hit=>hit.IsOverlap)==true));
      if(!covers)throw new InvalidOperationException("Could not verify which sections inherited the split seam.");
      sections.AddRange(children);
      splitConfirmed=true;
    }
    if(!splitConfirmed)return;
    if(sections.Select(obj=>obj.Id).Distinct().Count()!=sections.Count)
      throw new InvalidOperationException("A split section matches more than one source.");
    var outputs=EditableOutputs(doc,saved.Batch);
    if(outputs.Select(obj=>obj.Attributes.LayerIndex).Distinct().Skip(1).Any())
      throw new InvalidOperationException("The split allowance uses several output layers. Set one layer in Edit first.");
    var settings=Defaults with {Type=SeamType.Edge,Chamfer=saved.Profile.Chamfer??Defaults.Chamfer,
      ChamferAngle=saved.Profile.ChamferAngle??Defaults.ChamferAngle};
    var normal=saved.Part.Plane.Normal;
    var spans=saved.Part.Edges.Select((edge,index)=>(edge,Width:saved.Distances[index])).GroupBy(item=>item.Width)
      .Select(group=>(group.Key,Edges:group.Select(item=>item.edge).ToArray())).ToArray();
    using var rollback=new SeamChanges(doc);
    foreach(var source in sections)
    {
      var curve=(Curve)source.Geometry;
      using var part=CaptureOutline(doc,curve.DuplicateCurve(),[source],curve.PointAtStart,[normal.X,normal.Y,normal.Z]);
      if(part.Outline.IsClosed)throw new InvalidOperationException("A new split section is not open.");
      var point=part.Outline.PointAtNormalizedLength(0.5);
      if(!part.Outline.ClosestPoint(point,out double here) || !saved.Part.Outline.ClosestPoint(point,out double there))
        throw new InvalidOperationException("Could not preserve the split allowance side.");
      part.OffsetSide=part.Outline.TangentAt(here)*saved.Part.Outline.TangentAt(there)*
        (part.Plane.Normal*normal)<0?-saved.Side:saved.Side;
      bool KeepCap(Point3d point)=>!saved.Part.Outline.IsClosed &&
        ((saved.Profile.CapStart??DefaultOpenCap) && point.DistanceTo(saved.Part.Outline.PointAtStart)<=tolerance ||
         (saved.Profile.CapEnd??DefaultOpenCap) && point.DistanceTo(saved.Part.Outline.PointAtEnd)<=tolerance);
      part.CapStart=KeepCap(part.Outline.PointAtStart);
      part.CapEnd=KeepCap(part.Outline.PointAtEnd);
      var distances=new double[part.Edges.Count];
      for(int i=0;i<part.Edges.Count;i++)
      {
        var widths=spans.Where(span=>OnTargets(part.Edges[i],span.Edges,tolerance)).Select(span=>span.Key).Take(2).ToArray();
        if(widths.Length!=1)throw new InvalidOperationException("Could not match the saved widths to the split sections.");
        distances[i]=widths[0];
      }
      StageSeamComponent(doc,rollback,part,[source],source,distances,settings,outputs[0],newSourceLineage:true);
    }
    foreach(var output in outputs)rollback.Delete(output);
    rollback.Complete();
    RhinoApp.WriteLine($"vSeam: Transferred the allowance to {CountText.Format(sections.Count,"source section")}.");
  }

  private static int SharedSpanDirection(Curve a,Curve b,double tolerance)
  {
    if(a.GetLength()>tolerance && OnBoundary(a,b,tolerance))
    {
      int sign=DirectionOnOutline(a,b,tolerance);
      if(sign!=0)return sign;
    }
    if(b.GetLength()>tolerance && OnBoundary(b,a,tolerance))
    {
      int sign=DirectionOnOutline(b,a,tolerance);
      if(sign!=0)return sign;
    }
    var hits=Intersection.CurveCurve(a,b,tolerance,tolerance);
    if(hits==null)return 0;
    foreach(var hit in hits)
    {
      if(!hit.IsOverlap || a.GetLength(hit.OverlapA)<=tolerance)continue;
      double at=hit.OverlapA.Mid;
      if(!b.ClosestPoint(a.PointAt(at),out double bt))continue;
      double dot=a.TangentAt(at)*b.TangentAt(bt);
      if(Math.Abs(dot)>=MatchCosine)return Math.Sign(dot);
    }
    return 0;
  }

  private static void TransferJoinedAllowances(RhinoDoc doc,SourceTopologyTracking topology)
  {
    if(topology.Batches.Count==0 || topology.Originals.Count<2)return;
    uint undo=0;
    bool changed=false;
    try
    {
      double tolerance=doc.ModelAbsoluteTolerance;
      var originals=topology.Originals.Select(pair=>(Id:pair.Key,Curve:pair.Value,Current:doc.Objects.FindId(pair.Key)))
        .Where(item=>item.Current is not {IsDeleted:false,Geometry:Curve} ||
          !GeometryBase.GeometryEquals(item.Curve,item.Current.Geometry)).ToArray();
      var results=topology.NewIds.Concat(topology.ChangedIds).Distinct().Select(doc.Objects.FindId).OfType<RhinoObject>()
        .Where(obj=>IsBoundarySource(obj) && !obj.IsLocked && !obj.IsReference)
        .Select(obj=>(Object:obj,Inputs:originals.Where(input=>
          (input.Current is not {IsDeleted:false} || input.Id==obj.Id) &&
          (topology.Filleting?SharedSpanDirection(input.Curve,(Curve)obj.Geometry,tolerance)!=0:
            OnBoundary(input.Curve,(Curve)obj.Geometry,tolerance))).Select(input=>input.Id).ToArray()))
        .Where(result=>result.Inputs.Length>=2).ToArray();
      var counts=results.SelectMany(result=>result.Inputs).GroupBy(id=>id).ToDictionary(group=>group.Key,group=>group.Count());
      var replacements=results.Where(result=>result.Inputs.All(id=>counts[id]==1))
        .SelectMany(result=>result.Inputs.Select(id=>(id,result.Object))).ToDictionary(pair=>pair.id,pair=>pair.Object);

      // A join can consume sources belonging to several independent allowance profiles.
      var batches=topology.Batches.Values.Select(saved=>(Saved:saved,Products:saved.Sources.Keys
        .Where(replacements.ContainsKey).Select(id=>replacements[id].Id).ToHashSet())).Where(item=>item.Products.Count>0).ToList();
      while(batches.Count>0)
      {
        var (first,products)=batches[0];
        batches.RemoveAt(0);
        var group=new List<SplitBatch>{first};
        for(int i=0;i<batches.Count;)
        {
          if(!products.Overlaps(batches[i].Products)){i++;continue;}
          products.UnionWith(batches[i].Products);
          group.Add(batches[i].Saved);
          batches.RemoveAt(i);
          i=0;
        }
        try
        {
          RebuildJoinedAllowance(doc,group,replacements,products,ref undo);
          changed=true;
        }
        catch(Exception ex){ReportSeamFailure(ex);}
      }
    }
    catch(Exception ex){ReportSeamFailure(ex);}
    finally
    {
      if(undo!=0)doc.EndUndoRecord(undo);
      if(changed)doc.Views.Redraw();
    }
  }

  private static void RebuildJoinedAllowance(RhinoDoc doc,List<SplitBatch> group,IReadOnlyDictionary<Guid,RhinoObject> replacements,
    IReadOnlySet<Guid> joinedSources,ref uint undo)
  {
    var members=new Dictionary<Guid,RhinoObject>();
    foreach(var (id,old) in group.SelectMany(saved=>saved.Sources))
    {
      if(replacements.TryGetValue(id,out var product))members[product.Id]=product;
      else if(doc.Objects.FindId(id) is {IsDeleted:false,IsLocked:false,IsReference:false,Geometry:Curve} source &&
        GeometryBase.GeometryEquals(source.Geometry,old))members[id]=source;
      else throw new InvalidOperationException("Could not identify every source of the joined allowance.");
    }
    var outputs=group.SelectMany(saved=>EditableOutputs(doc,saved.Batch)).DistinctBy(obj=>obj.Id).ToArray();
    if(outputs.Select(obj=>obj.Attributes.LayerIndex).Distinct().Skip(1).Any())
      throw new InvalidOperationException("The joined allowances use different output layers.");
    SeamOptions Options(AllowanceProfile profile)=>Defaults with {Type=SeamType.Edge,Chamfer=profile.Chamfer??Defaults.Chamfer,
      ChamferAngle=profile.ChamferAngle??Defaults.ChamferAngle};
    var settings=Options(group[0].Profile);
    if(group.Skip(1).Select(saved=>Options(saved.Profile)).Any(other=>other.Chamfer!=settings.Chamfer ||
      settings.Chamfer && other.ChamferAngle!=settings.ChamferAngle))
      throw new InvalidOperationException("The joined allowances use different corner settings.");
    double tolerance=doc.ModelAbsoluteTolerance;
    var sources=members.Values.OrderBy(obj=>obj.Id).ToList();
    double factor=double.IsFinite(JoinToleranceFactor)&&JoinToleranceFactor>0?JoinToleranceFactor:BoundaryJoinToleranceFactor;
    var chains=JoinSeamSources(sources.Select(source=>(Curve)source.Geometry),tolerance*factor);
    if(chains.Length!=1)
    {
      foreach(var chain in chains)chain.Dispose();
      throw new InvalidOperationException("The joined sources do not form a single boundary.");
    }
    using var part=CaptureOutline(doc,chains[0],sources,chains[0].PointAtStart,group[0].Profile.OpenPlaneNormal,joinedSources);
    if(part.Sources.Length!=sources.Count)throw new InvalidOperationException("Could not assign the joined boundary sources.");

    var assigned=new double?[part.Edges.Count];
    int side=0;
    for(int i=0;i<part.Edges.Count;i++)
    {
      var edge=part.Edges[i];
      foreach(var saved in group)
        for(int j=0;j<saved.Part.Edges.Count;j++)
        {
          int sign=SharedSpanDirection(edge,saved.Part.Edges[j],tolerance);
          if(sign==0)continue;
          double width=saved.Distances[j];
          if(assigned[i] is double previous && previous!=width)
            throw new InvalidOperationException("One joined section spans different allowance widths.");
          assigned[i]=width;
          if(part.Outline.IsClosed || width<=0)continue;
          int current=saved.Side*sign*(saved.Part.Plane.Normal*part.Plane.Normal<0?-1:1);
          if(side!=0 && side!=current)throw new InvalidOperationException("The joined allowances are on different sides.");
          side=current;
        }
    }
    if(!InheritAdjacentWidths(assigned,part.Outline.IsClosed,out var distances) || !distances.Any(width=>width>0) ||
      !part.Outline.IsClosed && side==0)
      throw new InvalidOperationException("Could not match the allowance to the joined boundary.");
    if(!part.Outline.IsClosed)
    {
      part.OffsetSide=side;
      bool UseCap(Point3d point)
      {
        foreach(var saved in group.Where(saved=>!saved.Part.Outline.IsClosed))
        {
          if(point.DistanceTo(saved.Part.Outline.PointAtStart)<=tolerance)return saved.Profile.CapStart??DefaultOpenCap;
          if(point.DistanceTo(saved.Part.Outline.PointAtEnd)<=tolerance)return saved.Profile.CapEnd??DefaultOpenCap;
        }
        return DefaultOpenCap;
      }
      part.CapStart=UseCap(part.Outline.PointAtStart);
      part.CapEnd=UseCap(part.Outline.PointAtEnd);
    }
    if(undo==0 && !doc.UndoRecordingIsActive && doc.UndoRecordingEnabled)undo=doc.BeginUndoRecord("Update joined seam allowances");
    using var rollback=new SeamChanges(doc);
    StageSeamComponent(doc,rollback,part,sources,sources[0],distances,settings,outputs[0],useCommonGroups:true,joinedSources:joinedSources);
    foreach(var output in outputs)rollback.Delete(output);
    rollback.Complete();
  }

  private static void OnSourcesTransforming(object? sender,RhinoTransformObjectsEventArgs e)
  {
    if(e.ObjectsWillBeCopied)return;
    foreach(var obj in e.Objects.Concat(e.GripOwners))QueueSourceChange(obj);
  }

  private static void QueueSourceChange(RhinoObject obj,bool capturePrevious=true)
  {
    if(obj.Document is not {IsHeadless:false,UndoActive:false,RedoActive:false} doc ||
      obj.Attributes.GetUserString(OwnerKey)!=null || obj.Geometry is not Curve curve)return;
    bool linked=capturePrevious && obj.Attributes.GetUserString(SourceOwnerKey)!=null;
    bool connect=!ActiveTopologyChanges.ContainsKey(doc.RuntimeSerialNumber) && IsEndpointSource(obj);
    if(!linked && !connect)return;
    if(!PendingSourceChanges.TryGetValue(doc.RuntimeSerialNumber,out var changes))
      PendingSourceChanges[doc.RuntimeSerialNumber]=changes=new SourceChanges();
    if(connect)changes.Connections.Add(obj.Id);
    if(linked && !changes.Before.ContainsKey(obj.Id))changes.Before.Add(obj.Id,curve.DuplicateCurve());
  }

  private static void DiscardSourceChanges(uint document)
  {
    if(PendingSourceChanges.Remove(document,out var changes))changes.Dispose();
  }

  private static void UpdateSourceChanges(object? sender,EventArgs e)
  {
    if(PendingSourceChanges.Count==0 || RhinoApp.InCommand!=0)return;
    foreach(var number in PendingSourceChanges.Keys.ToArray())
    {
      var doc=RhinoDoc.FromRuntimeSerialNumber(number);
      if(doc==null){DiscardSourceChanges(number);continue;}
      if(doc.InCommand(false)==0)UpdateSourceChanges(doc);
    }
  }

  private static void UpdateSourceChanges(RhinoDoc doc)
  {
      if(doc.UndoActive || doc.RedoActive || ActiveTopologyChanges.ContainsKey(doc.RuntimeSerialNumber) ||
        !PendingSourceChanges.Remove(doc.RuntimeSerialNumber,out var changes))return;
      uint undo=0;
      bool changed=false;
      try
      {
        try { changed|=ExtendSeamBoundaries(doc,changes.Connections,ref undo); }
        catch(Exception ex){Log.Write(Section,$"Could not check connected seam sources: {ex}");}
        var batches=FindSeamBatches(doc,changes.Before.Keys.Select(doc.Objects.FindId).OfType<RhinoObject>());
        foreach(var batch in batches)
        {
          try
          {
            if(BoundaryIdentity(BatchSources(doc,batch)).Stamp==batch.Stamp)continue;
            if(undo==0 && !doc.UndoRecordingIsActive && doc.UndoRecordingEnabled)undo=doc.BeginUndoRecord("Update seam allowances");
            changed|=RebuildSeam(doc,batch,UnchangedEdit,true,previousSources:changes.Before);
          }
          catch(Exception ex){ReportSeamFailure(ex);}
        }
      }
      catch(Exception ex){ReportSeamFailure(ex);}
      finally
      {
        changes.Dispose();
        if(undo!=0)doc.EndUndoRecord(undo);
        if(changed)doc.Views.Redraw();
      }
  }

  private static Plan BuildPlan(Part part, Existing existing, SeamOptions settings, double tolerance)
  {
    try { return BuildJoinedPlan(part, existing, settings, tolerance); }
    catch (InvalidOperationException) when (part.Outline.IsClosed)
    {
      // Overlapping allowances can swallow a concave return. Resolve their outer envelope, not a remote miter.
      return BuildEnvelope(part, existing, settings, tolerance);
    }
  }

  private static Plan BuildJoinedPlan(Part part, Existing existing, SeamOptions settings, double tolerance)
  {
    int count = part.Edges.Count;
    var cuts = existing.Curves.Select(curve => curve?.DuplicateCurve()).ToArray();
    var distances = existing.Distances.ToArray();
    var plan = new Plan();
    try
    {
      bool closed=part.Outline.IsClosed;
      int side=closed?DefaultOpenSide:part.OffsetSide;
      if(!closed&&settings.Type==SeamType.Edge&&Enumerable.Range(0,count).Any(i=>!part.Includes(i)&&distances[i]>0))
      {
        if(settings.Allowance==0&&existing.OpenSide!=0)side=existing.OpenSide;
        else if(existing.OpenSide!=side)throw new InvalidOperationException("Switch the side of existing neighboring allowances in Part mode.");
      }
      var outwardNormal=part.Plane.Normal*side;
      for (int i = 0; i < count; i++)
      {
        if (settings.Type == SeamType.Edge && !part.Includes(i))
        {
          if (cuts[i] == null && distances[i] > 0) cuts[i] = OffsetEdge(part.Edges[i], part.Plane, distances[i], tolerance,side);
          continue;
        }
        var replacement = settings.Allowance == 0 ? part.Edges[i].DuplicateCurve() : OffsetEdge(part.Edges[i], part.Plane, settings.Allowance, tolerance,side);
        cuts[i]?.Dispose(); cuts[i] = replacement; distances[i] = settings.Allowance;
        plan.AffectedEdges++;
      }
      if (!(count == 1 && part.Edges[0].IsClosed))
        for (int i = 0; i < (closed?count:count-1); i++)
        {
          int next = (i + 1) % count;
          var before = cuts[i]; var after = cuts[next];
          var vertex = part.Edges[i].PointAtEnd;
          bool beforeAllowance = before != null && distances[i] > 0;
          bool afterAllowance = after != null && distances[next] > 0;
          if (!beforeAllowance && !afterAllowance) continue;
          if (!beforeAllowance || !afterAllowance)
          {
            int index = beforeAllowance ? i : next;
            bool start = !beforeAllowance;
            var tangent = start ? part.Edges[index].TangentAtStart : part.Edges[index].TangentAtEnd;
            var trimmed = SliceEnd(cuts[index]!, start, new Plane(vertex, tangent), distances[index] * CornerReach, tolerance);
            Replace(cuts, index, trimmed);
            var end = start ? trimmed.PointAtStart : trimmed.PointAtEnd;
            if (vertex.DistanceTo(end) > tolerance)
              plan.Pieces.Add(new Piece(new LineCurve(vertex, end), "cap", i, distances[index]));
            continue;
          }
          double allowance = Math.Max(distances[i], distances[next]);
          var incoming = Unit(part.Edges[i].TangentAtEnd);
          var outgoing = Unit(part.Edges[next].TangentAtStart);
          var chamfer = settings.Chamfer && IsPointy(incoming, outgoing, outwardNormal, settings.ChamferAngle);
          if (chamfer)
          {
            var direction = Unit(Outward(incoming, outwardNormal) + Outward(outgoing, outwardNormal));
            var section = new Plane(vertex + direction * allowance, direction);
            Replace(cuts, i, SliceEnd(before!, false, section, allowance * CornerReach, tolerance));
            Replace(cuts, next, SliceEnd(after!, true, section, allowance * CornerReach, tolerance));
          }
          else if (incoming * outgoing >= KinkCosine)
          {
            // Parallel unequal allowances form the perpendicular step shown in the source pattern.
            var section = new Plane(vertex, Unit(incoming + outgoing));
            Replace(cuts, i, SliceEnd(before!, false, section, allowance * CornerReach, tolerance));
            Replace(cuts, next, SliceEnd(after!, true, section, allowance * CornerReach, tolerance));
          }
          else
          {
            var joined = JoinEnds(before!, after!, vertex, part, allowance * CornerReach, tolerance);
            Replace(cuts, i, joined.Before); Replace(cuts, next, joined.After);
          }
          var a = cuts[i]!.PointAtEnd; var b = cuts[next]!.PointAtStart;
          if (a.DistanceTo(b) > tolerance)
            plan.Pieces.Add(new Piece(new LineCurve(a, b), chamfer ? "chamfer" : "join", i, allowance));
        }
      if(!closed)
      {
        if(part.CapStart)CapEnd(0,true);
        if(part.CapEnd)CapEnd(count-1,false);
      }
      void CapEnd(int index,bool start)
      {
        if(cuts[index] is not {} cut||distances[index]<=0)return;
        var edge=part.Edges[index];
        var vertex=start?edge.PointAtStart:edge.PointAtEnd;
        var tangent=start?edge.TangentAtStart:edge.TangentAtEnd;
        var trimmed=SliceEnd(cut,start,new Plane(vertex,tangent),distances[index]*CornerReach,tolerance);
        Replace(cuts,index,trimmed);
        var end=start?trimmed.PointAtStart:trimmed.PointAtEnd;
        if(vertex.DistanceTo(end)>tolerance)plan.Pieces.Add(new Piece(new LineCurve(vertex,end),"cap",index,distances[index]));
      }
      for (int i = 0; i < count; i++)
        if (cuts[i] != null)
        { plan.Pieces.Add(new Piece(cuts[i]!, "edge", i, distances[i])); cuts[i] = null; }
      ValidatePlan(part, plan, tolerance);
      plan.Allowances=distances;
      plan.OffsetSide=side;
      return plan;
    }
    catch { plan.Dispose(); throw; }
    finally { foreach (var curve in cuts) curve?.Dispose(); }
  }

  private static Plan BuildEnvelope(Part part, Existing existing, SeamOptions settings, double tolerance)
  {
    int count = part.Edges.Count;
    var distances = existing.Distances.ToArray();
    var cuts = new Curve?[count];
    var regions = new List<Curve>();
    var plan = new Plan();
    Curve[]? union = null;
    try
    {
      for (int i = 0; i < count; i++)
      {
        bool selected = settings.Type == SeamType.Part || part.Includes(i);
        if (selected) { distances[i] = settings.Allowance; plan.AffectedEdges++; }
        if (distances[i] > 0) cuts[i] = !selected && existing.Curves[i] != null
          ? existing.Curves[i]!.DuplicateCurve() : OffsetEdge(part.Edges[i], part.Plane, distances[i], tolerance);
      }
      for (int i = 0; i < count; i++)
      {
        int next = (i + 1) % count;
        var vertex = part.Edges[i].PointAtEnd;
        var incoming = Unit(part.Edges[i].TangentAtEnd);
        var outgoing = Unit(part.Edges[next].TangentAtStart);
        if (cuts[i] == null || cuts[next] == null)
        {
          int edge = cuts[i] != null ? i : next;
          if (cuts[edge] == null) continue;
          bool start = edge == next;
          Replace(cuts, edge, SliceEnd(cuts[edge]!, start, new Plane(vertex, start ? outgoing : incoming), distances[edge] * CornerReach, tolerance));
          continue;
        }
        double distance = Math.Max(distances[i], distances[next]);
        double turn = Vector3d.CrossProduct(incoming, outgoing) * part.Plane.Normal;
        if (turn < -RhinoMath.ZeroTolerance) continue; // Boolean union trims concave overlaps, including closed darts.
        if (settings.Chamfer && IsPointy(incoming, outgoing, part.Plane.Normal, settings.ChamferAngle))
        {
          var direction = Unit(Outward(incoming, part.Plane.Normal) + Outward(outgoing, part.Plane.Normal));
          var section = new Plane(vertex + direction * distance, direction);
          Replace(cuts, i, SliceEnd(cuts[i]!, false, section, distance * CornerReach, tolerance));
          Replace(cuts, next, SliceEnd(cuts[next]!, true, section, distance * CornerReach, tolerance));
        }
        else if (incoming * outgoing >= KinkCosine)
        {
          var section = new Plane(vertex, Unit(incoming + outgoing));
          Replace(cuts, i, SliceEnd(cuts[i]!, false, section, distance * CornerReach, tolerance));
          Replace(cuts, next, SliceEnd(cuts[next]!, true, section, distance * CornerReach, tolerance));
        }
        else
        {
          var joined = JoinEnds(cuts[i]!, cuts[next]!, vertex, part, distance * CornerReach, tolerance, allowInterior: true);
          Replace(cuts, i, joined.Before); Replace(cuts, next, joined.After);
        }
        var a = cuts[i]!.PointAtEnd; var b = cuts[next]!.PointAtStart;
        if (Vector3d.CrossProduct(a - vertex, b - vertex).Length > tolerance * tolerance)
          regions.Add(new PolylineCurve([vertex, a, b, vertex]));
      }
      regions.Add(part.Outline.DuplicateCurve());
      for (int i = 0; i < count; i++)
      {
        if (cuts[i] is not {} cut) continue;
        using var reverse = cut.DuplicateCurve(); reverse.Reverse();
        var source = part.Edges[i];
        var pieces = new List<Curve> { source.DuplicateCurve() };
        try
        {
          if (source.PointAtEnd.DistanceTo(cut.PointAtEnd) > tolerance) pieces.Add(new LineCurve(source.PointAtEnd, cut.PointAtEnd));
          pieces.Add(reverse.DuplicateCurve());
          if (cut.PointAtStart.DistanceTo(source.PointAtStart) > tolerance) pieces.Add(new LineCurve(cut.PointAtStart, source.PointAtStart));
          var joined = Curve.JoinCurves(pieces, tolerance);
          if (joined?.Length != 1 || !joined[0].IsClosed)
          { if (joined != null) foreach (var curve in joined) curve.Dispose(); throw new InvalidOperationException("Cannot close an exact allowance strip around this edge."); }
          regions.Add(joined[0]);
        }
        finally { foreach (var curve in pieces) curve.Dispose(); }
      }
      union = Curve.CreateBooleanUnion(regions, tolerance);
      if (union?.Length != 1 || !union[0].IsClosed)
        throw new InvalidOperationException("The seam envelope is disconnected or ambiguous; no geometry was changed.");
      var boundary = union[0].DuplicateSegments();
      if (boundary == null || boundary.Length == 0) boundary = [union[0].DuplicateCurve()];
      var outside = new List<Curve>();
      try
      {
        foreach (var curve in boundary)
        {
          var hits = Intersection.CurveCurve(curve, part.Outline, tolerance, tolerance);
          double epsilon = Math.Abs(curve.Domain.Length) * ParameterFraction;
          var parameters = (hits?.SelectMany(hit => hit.IsOverlap
              ? new[] { hit.OverlapA.T0, hit.OverlapA.T1 } : new[] { hit.ParameterA }) ?? [])
            .Where(t => t > curve.Domain.T0 + epsilon && t < curve.Domain.T1 - epsilon).OrderBy(t => t)
            .Aggregate(new List<double>(), (values, t) =>
              { if (values.Count == 0 || t - values[^1] > epsilon) values.Add(t); return values; });
          foreach (var fragment in TrimSections(curve, parameters))
            if (fragment.GetLength() > tolerance && !OnBoundary(fragment, part.Outline, tolerance)) outside.Add(fragment);
            else fragment.Dispose();
        }
        var output = Curve.JoinCurves(outside, tolerance) ?? [];
        foreach (var curve in output) plan.Pieces.Add(new Piece(curve, "envelope", 0, distances.Max()));
        if (plan.Pieces.Count == 0 && distances.Any(distance => distance > 0))
          throw new InvalidOperationException("Rhino did not produce an outward allowance boundary.");
      }
      finally { foreach (var curve in outside.Concat(boundary)) curve.Dispose(); }
      plan.Allowances = distances;
      ValidatePlan(part, plan, tolerance);
      return plan;
    }
    catch { plan.Dispose(); throw; }
    finally
    {
      foreach (var curve in cuts) curve?.Dispose();
      foreach (var curve in regions) curve.Dispose();
      if (union != null) foreach (var curve in union) curve.Dispose();
    }
  }

  private static Vector3d Unit(Vector3d vector)
  { if (!vector.Unitize()) throw new InvalidOperationException("A boundary tangent is degenerate."); return vector; }
  private static Vector3d Outward(Vector3d tangent, Vector3d normal) => Unit(Vector3d.CrossProduct(tangent, normal));
  private static double Distance(Curve curve, Point3d point) => point.IsValid && curve.ClosestPoint(point, out double t)
    ? curve.PointAt(t).DistanceTo(point) : double.PositiveInfinity;
  private static bool IsPointy(Vector3d incoming, Vector3d outgoing, Vector3d normal, double angle = ChamferInteriorDegrees) => angle > 0 &&
    Vector3d.CrossProduct(incoming, outgoing) * normal > RhinoMath.ZeroTolerance &&
    180 - Math.Acos(Math.Clamp(incoming * outgoing, -1, 1)) * 180 / Math.PI <= angle + 1e-9;
  private static void Replace(Curve?[] curves, int index, Curve replacement)
  { curves[index]?.Dispose(); curves[index] = replacement; }

  private static Curve OffsetEdge(Curve edge, Plane plane, double distance, double tolerance,int side=DefaultOpenSide)
  {
    if (!edge.NormalizedLengthParameter(0.5, out var t)) throw new InvalidOperationException("Cannot measure a finished edge.");
    var directionPoint = edge.PointAt(t) + Outward(edge.TangentAt(t), plane.Normal) * (distance*side);
    var offsets = edge.Offset(directionPoint, plane.Normal, distance, tolerance, CurveOffsetCornerStyle.Sharp);
    if (offsets == null || offsets.Length == 0) throw new InvalidOperationException("Rhino could not offset this edge; try a smaller allowance.");
    try
    {
      var joined = Curve.JoinCurves(offsets, tolerance);
      if (joined == null || joined.Length != 1)
      { if (joined != null) foreach (var curve in joined) curve.Dispose(); throw new InvalidOperationException("The offset is disconnected; no partial seam was created."); }
      var result = joined[0];
      if (edge.IsClosed && !result.IsClosed)
      { result.Dispose(); throw new InvalidOperationException("Rhino returned an open offset for this closed section; no partial seam was created."); }
      if (result.TangentAt(result.Domain.Mid) * edge.TangentAt(t) < 0) result.Reverse();
      return result;
    }
    finally { foreach (var curve in offsets) curve.Dispose(); }
  }

  private static Curve Extended(Curve curve, bool start, double reach)
  {
    return curve.Extend(start ? CurveEnd.Start : CurveEnd.End, reach,
      CornerExtensionStyle)
      ?? curve.DuplicateCurve();
  }

  private static Curve TrimEnd(Curve curve, bool start, double parameter)
  {
    double epsilon = Math.Max(Math.Abs(curve.Domain.Length) * ParameterFraction, RhinoMath.ZeroTolerance);
    if (Math.Abs(parameter - (start ? curve.Domain.T0 : curve.Domain.T1)) <= epsilon) return curve.DuplicateCurve();
    var domain = start ? new Interval(parameter, curve.Domain.T1) : new Interval(curve.Domain.T0, parameter);
    if (domain.Length <= epsilon) throw new InvalidOperationException("Allowance consumes an entire edge; try a smaller distance.");
    return curve.Trim(domain) ?? throw new InvalidOperationException("Cannot trim the seam without changing its shape.");
  }

  private static Curve SliceEnd(Curve curve, bool start, Plane section, double reach, double tolerance)
  {
    using var extended = Extended(curve, start, reach);
    var hits = Intersection.CurvePlane(extended, section, tolerance);
    double epsilon = Math.Abs(extended.Domain.Length) * ParameterFraction;
    var candidates = hits?.Where(hit => hit.IsPoint && (start ? hit.ParameterA < extended.Domain.T1 - epsilon : hit.ParameterA > extended.Domain.T0 + epsilon))
      .OrderBy(hit => hit.PointA.DistanceTo(start ? curve.PointAtStart : curve.PointAtEnd)).ToArray();
    if (candidates == null || candidates.Length == 0) throw new InvalidOperationException("Cannot reach an end cap/chamfer by trimming or extending this seam.");
    return TrimEnd(extended, start, candidates[0].ParameterA);
  }

  private static (Curve Before, Curve After) JoinEnds(Curve before, Curve after, Point3d vertex, Part part, double reach, double tolerance, bool allowInterior = false)
  {
    if (before.PointAtEnd.DistanceTo(after.PointAtStart) <= tolerance)
      return (before.DuplicateCurve(), after.DuplicateCurve());
    using var a = Extended(before, false, reach);
    using var b = Extended(after, true, reach);
    var hits = Intersection.CurveCurve(a, b, tolerance, tolerance);
    var candidates = hits?.Where(hit => hit.IsPoint && hit.ParameterA > a.Domain.T0 && hit.ParameterB < b.Domain.T1 &&
      hit.PointA.DistanceTo(vertex) <= reach && (allowInterior || !part.Outline.IsClosed || part.Outline.Contains(hit.PointA, part.Plane, tolerance) != PointContainment.Inside))
      .OrderBy(hit => hit.PointA.DistanceTo(vertex)).ToArray();
    if (candidates == null || candidates.Length == 0)
    {
      Log.Write(Section, $"Corner join rejected vertex={vertex} reach={reach:G17} envelope={allowInterior} before={before.GetType().Name} extendedEnd={a.PointAtEnd} after={after.GetType().Name} extendedStart={b.PointAtStart} hits={hits?.Count ?? 0}");
      throw new InvalidOperationException("Neighbor allowances cannot be joined by trimming/extending alone.");
    }
    Curve first = TrimEnd(a, false, candidates[0].ParameterA);
    try { return (first, TrimEnd(b, true, candidates[0].ParameterB)); }
    catch { first.Dispose(); throw; }
  }

  private static void ValidatePlan(Part part, Plan plan, double tolerance)
  {
    foreach (var piece in plan.Pieces)
    {
      if (!piece.Curve.IsValid || piece.Curve.GetLength() <= tolerance) throw new InvalidOperationException("The seam contains a degenerate section.");
      var self = Intersection.CurveSelf(piece.Curve, tolerance);
      if (self != null && self.Any(hit => !ClosureContact(piece.Curve, hit, tolerance)))
        throw new InvalidOperationException("An offset section crosses itself; try a smaller allowance.");
      if (piece.Role == "edge" && piece.Allowance == 0) continue;
      var sourceHits = Intersection.CurveCurve(piece.Curve, part.Outline, tolerance, tolerance);
      if (sourceHits != null && sourceHits.Any(hit => hit.IsOverlap || hit.IsPoint &&
        (piece.Role is not ("cap" or "envelope") || hit.PointA.DistanceTo(piece.Curve.PointAtStart) > tolerance && hit.PointA.DistanceTo(piece.Curve.PointAtEnd) > tolerance)))
        throw new InvalidOperationException("The allowance crosses the finished boundary; try a smaller distance.");
    }
    for (int i = 0; i < plan.Pieces.Count; i++)
      for (int j = 0; j < i; j++)
      {
        var a = plan.Pieces[i].Curve; var b = plan.Pieces[j].Curve;
        if (!BoundingBox.Intersection(a.GetBoundingBox(true), b.GetBoundingBox(true)).IsValid) continue;
        var hits = Intersection.CurveCurve(a, b, tolerance, tolerance);
        if (hits != null && hits.Any(hit => hit.IsOverlap || hit.IsPoint &&
          !(NearEnd(a, hit.PointA, tolerance) && NearEnd(b, hit.PointB, tolerance))))
          throw new InvalidOperationException("Allowance sections cross or overlap; try a smaller distance.");
      }
  }
  private static bool NearEnd(Curve curve, Point3d point, double tolerance) =>
    point.DistanceTo(curve.PointAtStart) <= tolerance || point.DistanceTo(curve.PointAtEnd) <= tolerance;

  private static bool ClosureContact(Curve curve, IntersectionEvent hit, double tolerance)
  {
    double epsilon = Math.Abs(curve.Domain.Length) * ParameterFraction;
    return hit.IsPoint && curve.IsClosed && hit.PointA.DistanceTo(curve.PointAtStart) <= tolerance &&
      (Math.Abs(hit.ParameterA - curve.Domain.T0) <= epsilon && Math.Abs(hit.ParameterB - curve.Domain.T1) <= epsilon ||
       Math.Abs(hit.ParameterB - curve.Domain.T0) <= epsilon && Math.Abs(hit.ParameterA - curve.Domain.T1) <= epsilon);
  }

  private static bool NeedsOpenSide(Part part)=>!part.Outline.IsClosed&&_settings.Allowance>0;

  private static int SideAt(Curve edge,Plane plane,Point3d point,double tolerance)
  {
    if(!edge.ClosestPoint(point,out double t))return 0;
    var right=Vector3d.CrossProduct(edge.TangentAt(t),plane.Normal);
    if(!right.Unitize())return 0;
    double distance=(point-edge.PointAt(t))*right;
    return Math.Abs(distance)<=tolerance?0:Math.Sign(distance);
  }

  private static int InferOpenSide(Part part,IEnumerable<Curve> outputs,double tolerance)
  {
    var sides=outputs.SelectMany(curve=>Enumerable.Range(1,ConfiguredMatchSamples)
      .Select(i=>SideAt(part.Outline,part.Plane,curve.PointAtNormalizedLength((double)i/(ConfiguredMatchSamples+1)),tolerance)))
      .Where(side=>side!=0).Distinct().Take(2).ToArray();
    return sides.Length==1?sides[0]:0;
  }

  private static PreparedSeam? PickOpenSide(RhinoDoc doc,Part part)
  {
    int[] edges=PreviewEdges(part,part.SelectedEdges?.ToArray());
    using(var existing=new Existing(part.Edges.Count))
    {
      ReadExisting(doc,part,existing);
      if(existing.OpenSide!=0)part.OffsetSide=existing.OpenSide;
    }
    using var highlight=new PreviewDisplay.ObjectHighlighter(doc){RedrawOnChange=false};
    var sources=edges.ToDictionary(_=>Guid.NewGuid(),index=>(GeometryBase)part.Edges[index]);
    highlight.SetObjects(sources.Keys,sources);
    using var preview=new SeamPreview(doc);
    var targets=new[]{new BoxTarget(part,edges)};
    int Choose(Point3d point)
    {
      var nearest=edges.Select(index=>part.Edges[index]).MinBy(edge=>Distance(edge,point))!;
      return SideAt(nearest,part.Plane,point,doc.ModelAbsoluteTolerance);
    }
    while(true)
    {
      using var getter=new GetPoint();
      getter.SetCommandPrompt("Click the allowance side, FlipSide, or press Enter to use the preview");
      getter.AcceptNothing(true);
      getter.PermitObjectSnap(false);getter.EnableObjectSnapCursors(false);
      getter.Constrain(part.Plane,false);
      int flip=getter.AddOption("FlipSide");
      getter.MouseMove+=(_,e)=>
      {
        int side=Choose(e.Point);
        if(side==0||side==part.OffsetSide)return;
        part.OffsetSide=side;
        if(preview.Show(targets))doc.Views.Redraw();
      };
      preview.Show(targets);doc.Views.Redraw();
      var result=getter.Get();
      if(result==GetResult.Option&&getter.Option()?.Index==flip){part.OffsetSide=-part.OffsetSide;continue;}
      if(result==GetResult.Point)
      {int picked=Choose(getter.Point());if(picked!=0)part.OffsetSide=picked;}
      if(result is not (GetResult.Point or GetResult.Nothing))return null;
      if(preview.Take(part,edges) is {} prepared)return prepared;
      RhinoApp.WriteLine("vSeam: "+(preview.LastError??"No valid offset was found on that side."));
    }
  }

  private static PartSelection? PickPart(RhinoDoc doc, RunMode mode, bool preselect, out Result result, out HistoryRequest? historyRequest, out bool edit)
  {
    historyRequest = null;
    edit = false;
    if (preselect)
    {
      var preselected=doc.Objects.GetSelectedObjects(false,false).Where(obj=>obj.Geometry is Curve&&!IsLabelObject(obj)).ToArray();
      var selected=preselected.Where(obj=>obj.Attributes.GetUserString(OwnerKey)==null).ToArray();
      foreach (var source in preselected)
      {
        try
        {
          var part = CaptureBoundary(doc, source, ((Curve)source.Geometry).PointAtNormalizedLength(0.5),selected);
          doc.Objects.UnselectAll(); result = Result.Success; return new PartSelection([part]);
        }
        catch (Exception ex) { Log.Write(Section, $"Preselected boundary rejected id={source.Id}: {ex.Message}"); }
      }
    }
    doc.Objects.UnselectAll();
    Rhino.Display.RhinoViewport? lastViewport = null;
    System.Drawing.Point lastPixel = default;
    while (true)
    {
      using var getter = new GetPoint();
      getter.PermitObjectSnap(PickerObjectSnaps);
      getter.EnableObjectSnapCursors(PickerObjectSnaps);
      using var picker = new PartPicker(doc);
      using var highlight = new PreviewDisplay.ObjectHighlighter(doc) { RedrawOnChange = false };
      using var gapHighlight = new PreviewDisplay.ObjectHighlighter(doc, GapHighlightStyle) { RedrawOnChange = false };
      using var preview = new SeamPreview(doc);
      var highlightIds = new Dictionary<Curve, Guid>(ReferenceEqualityComparer.Instance);
      var shownSources = new HashSet<Curve>(ReferenceEqualityComparer.Instance);
      IReadOnlyDictionary<Guid, GeometryBase>? shownGaps = null;
      void Show(IReadOnlyList<BoxTarget> targets)
      {
        var curves = targets.SelectMany(target => _settings.Type == SeamType.Part
          ? new[] { target.Part.Outline } : PreviewEdges(target.Part, target.Edges).Select(i => target.Part.Edges[i])).ToHashSet<Curve>(ReferenceEqualityComparer.Instance);
        bool changed = !shownSources.SetEquals(curves) || !ReferenceEquals(shownGaps, picker.GapGeometry);
        shownGaps = picker.GapGeometry;
        gapHighlight.SetObjects(shownGaps.Keys, shownGaps);
        shownSources = curves;
        var geometry = curves.ToDictionary(curve =>
        {
          if (!highlightIds.TryGetValue(curve, out var id)) highlightIds[curve] = id = Guid.NewGuid();
          return id;
        }, curve => (GeometryBase)curve);
        highlight.SetObjects(geometry.Keys, geometry);
        if (preview.Show(targets.Where(target=>!NeedsOpenSide(target.Part)).ToArray()) || changed) doc.Views.Redraw();
      }
      Rhino.Display.RhinoViewport? clickedViewport = null;
      System.Drawing.Point clickedPoint = default;
      System.Drawing.Point boxEnd = default;
      bool dragging = false;
      long previewTick = 0;
      getter.SetCommandPrompt(_settings.Type == SeamType.Part ? "Click a finished boundary or inside a closed part. Enter when done" : "Select finished edge between kinks. Enter when done");
      getter.AcceptNothing(true);
      getter.AcceptNumber(true, true);
      getter.AcceptString(true);
      getter.AcceptCustomMessage(true);
      getter.MouseMove += (_, e) =>
      {
        lastViewport = e.Viewport; lastPixel = e.WindowPoint;
        if (clickedViewport?.Id == e.Viewport.Id && e.LeftButtonDown)
        {
          boxEnd = e.WindowPoint;
          dragging |= Math.Abs(boxEnd.X - clickedPoint.X) >= ConfiguredBoxDragThresholdPixels || Math.Abs(boxEnd.Y - clickedPoint.Y) >= ConfiguredBoxDragThresholdPixels;
          if (dragging)
          {
            if (System.Environment.TickCount64 - previewTick >= ConfiguredBoxPreviewIntervalMilliseconds)
            {
              var targets = picker.ResolveBox(e.Viewport, SelectionRectangle(clickedPoint, boxEnd), boxEnd.X < clickedPoint.X);
              Show(targets);
              previewTick = System.Environment.TickCount64;
            }
            return;
          }
        }
        var hovered = picker.Resolve(e.Viewport, e.WindowPoint);
        Show(hovered == null ? [] : [new BoxTarget(hovered, [])]);
      };
      getter.MouseDown += (_, e) => { clickedViewport = e.Viewport; clickedPoint = boxEnd = e.WindowPoint; dragging = false; previewTick = 0; };
      getter.DynamicDraw += (_, e) =>
      {
        if (dragging && clickedViewport?.Id == e.Viewport.Id)
          e.Display.Draw2dRectangle(SelectionRectangle(clickedPoint, boxEnd), BoxColor, ConfiguredBoxLineThicknessPixels, System.Drawing.Color.Transparent);
      };
      var controls = new Controls(getter);
      int editIndex = getter.AddOption("Edit");
      if (lastViewport != null)
      {
        var hovered = picker.Resolve(lastViewport, lastPixel);
        Show(hovered == null ? [] : [new BoxTarget(hovered, [])]);
      }
      GetResult input;
      using (var shortcuts = new LocalUndoRedoShortcutSession(Section, redo => new HistoryRequest(redo))) input = getter.Get(onMouseUp: true);
      highlight.SetObjects([]);
      gapHighlight.SetObjects([]);
      if (input == GetResult.CustomMessage && getter.CustomMessage() is HistoryRequest request) historyRequest = request;
      else if (input == GetResult.String) historyRequest = ParseHistoryRequest(getter.StringResult());
      if (historyRequest != null) { result = Result.Success; return null; }
      if (input == GetResult.Option && getter.Option()?.Index == editIndex)
      { edit = true; result = Result.Success; return null; }
      if (input == GetResult.String) { RhinoApp.WriteLine("vSeam: unrecognized input."); continue; }
      if (input is GetResult.Option or GetResult.Number) { ReadControls(doc, mode, getter, input, controls); continue; }
      result = input == GetResult.Nothing ? Result.Success : getter.CommandResult();
      if (input != GetResult.Point) return null;
      var viewport = clickedViewport ?? doc.Views.ActiveView?.ActiveViewport;
      if (viewport == null) return null;
      if (dragging)
      {
        var targets = picker.ResolveBox(viewport, SelectionRectangle(clickedPoint, boxEnd), boxEnd.X < clickedPoint.X);
        if (targets.Count == 0) { RhinoApp.WriteLine("vSeam: no finished parts or edges selected by the box."); continue; }
        Show(targets);
        var selection = new PartSelection([]);
        foreach (var target in targets)
        {
          if(NeedsOpenSide(target.Part))
          {
            target.Part.SelectedEdges=_settings.Type==SeamType.Edge?target.Edges.ToHashSet():null;
            selection.Parts.Add(picker.Take(target.Part));
            continue;
          }
          var prepared = preview.Take(target.Part, target.Edges);
          if (prepared == null) { RhinoApp.WriteLine($"vSeam: {preview.LastError}"); continue; }
          target.Part.SelectedEdges = _settings.Type == SeamType.Edge ? target.Edges.ToHashSet() : null;
          selection.Parts.Add(picker.Take(target.Part));
          selection.Previews.Add(target.Part, prepared);
        }
        if (selection.Parts.Count > 0) return selection;
        selection.Dispose(); continue;
      }
      if (clickedViewport == null)
      {
        var pixel = viewport.WorldToClient(getter.Point());
        clickedPoint = new System.Drawing.Point((int)Math.Round(pixel.X), (int)Math.Round(pixel.Y));
      }
      var selected = picker.Resolve(viewport, clickedPoint);
      if (selected != null)
      {
        if(NeedsOpenSide(selected))return new PartSelection([picker.Take(selected)]);
        Show([new BoxTarget(selected, [])]);
        var prepared = preview.Take(selected);
        if (prepared != null)
        {
          var selection = new PartSelection([picker.Take(selected)]);
          selection.Previews.Add(selected, prepared);
          return selection;
        }
      }
      RhinoApp.WriteLine($"vSeam: {preview.LastError ?? picker.LastError ?? "No finished part was found at that point."}");
      Log.Write(Section, $"Boundary pick rejected: {preview.LastError ?? picker.LastError}");
    }
  }

  private static System.Drawing.Rectangle SelectionRectangle(System.Drawing.Point start, System.Drawing.Point end) =>
    new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(1, Math.Abs(end.X - start.X)), Math.Max(1, Math.Abs(end.Y - start.Y)));

  private static HistoryRequest? ParseHistoryRequest(string? text) => text?.Trim().TrimStart('_', '-').ToLowerInvariant() switch
  { "u" or "undo" => new(false), "r" or "redo" => new(true), _ => null };

  private sealed class PartPicker : IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly Dictionary<Guid, Part> _parts = [];
    private readonly Dictionary<Guid, (string Message, Dictionary<Guid, GeometryBase> Markers)> _errors = [];
    private readonly Dictionary<Guid, GeometryBase> _noGaps = [];
    private readonly (RhinoObject Object, BoundingBox Bounds)[] _seeds;
    internal string? LastError { get; private set; }
    internal IReadOnlyDictionary<Guid, GeometryBase> GapGeometry { get; private set; }
    internal PartPicker(RhinoDoc doc)
    {
      _doc = doc;
      GapGeometry = _noGaps;
      var groups = new Dictionary<int, BoundingBox>();
      _seeds = doc.Objects.GetObjectList(ObjectType.Curve).Where(obj => !obj.IsDeleted && !obj.IsHidden &&
        !obj.IsLocked && !obj.IsReference && !IsLabelObject(obj) && obj.Attributes.GetUserString(OwnerKey) == null)
        .OrderByDescending(obj => IsLayer(doc, obj, FinishedLayerName)).Select(obj =>
      {
        var bounds = obj.Geometry.GetBoundingBox(true);
        int group = (obj.Attributes.GetGroupList() ?? []).Where(i => doc.Groups[i] is { IsDeleted: false })
          .OrderBy(doc.Groups.GroupObjectCount).DefaultIfEmpty(-1).First();
        if (group >= 0)
        {
          if (!groups.TryGetValue(group, out bounds))
          {
            bounds = BoundingBox.Empty;
            foreach (var member in doc.Groups.GroupMembers(group) ?? [])
              if (member.Geometry is Curve && member.Attributes.GetUserString(OwnerKey) == null) bounds.Union(member.Geometry.GetBoundingBox(true));
            groups[group] = bounds;
          }
        }
        return (obj, bounds);
      }).ToArray();
    }
    private Part? Capture(RhinoObject obj, Point3d point)
    {
      if (_parts.TryGetValue(obj.Id, out var part)) { part.PickPoint = point; return part; }
      if (_errors.TryGetValue(obj.Id, out var error)) { LastError = error.Message; GapGeometry = error.Markers; return null; }
      try
      {
        part = CapturePart(_doc, obj, point);
        _parts[obj.Id] = part;
        foreach (var source in part.Sources) _parts.TryAdd(source.Id, part);
        return part;
      }
      catch (Exception ex)
      {
        var markers = ex is BoundaryGapException gap
          ? gap.Endpoints.ToDictionary(_ => Guid.NewGuid(), point => (GeometryBase)new Rhino.Geometry.Point(point)) : [];
        _errors[obj.Id] = (ex.Message, markers); LastError = ex.Message; GapGeometry = markers; return null;
      }
    }
    internal Part? Resolve(Rhino.Display.RhinoViewport viewport, System.Drawing.Point pixel)
    {
      try { return ResolveCore(viewport, pixel); }
      catch (Exception ex) { LastError = ex.Message; return null; }
    }
    internal List<BoxTarget> ResolveBox(Rhino.Display.RhinoViewport viewport, System.Drawing.Rectangle rectangle, bool crossing)
    {
      GapGeometry = _noGaps;
      var targets = new List<BoxTarget>();
      if (viewport.ParentView is not {} view) return targets;
      using var context = new PickContext { View = view, PickStyle = crossing ? PickStyle.CrossingPick : PickStyle.WindowPick,
        PickMode = PickMode.Wireframe, PickGroupsEnabled = false, SubObjectSelectionEnabled = false };
      context.SetPickTransform(viewport.GetPickTransform(rectangle)); context.UpdateClippingPlanes();
      var seen = new HashSet<string>();
      foreach (var seed in _seeds)
      {
        var pixels = seed.Bounds.GetCorners().Select(viewport.WorldToClient).Where(point => point.IsValid).ToArray();
        if (pixels.Length == 0 || pixels.Max(p => p.X) < rectangle.Left || pixels.Min(p => p.X) > rectangle.Right ||
          pixels.Max(p => p.Y) < rectangle.Top || pixels.Min(p => p.Y) > rectangle.Bottom) continue;
        var part = Capture(seed.Object, ((Curve)seed.Object.Geometry).PointAtNormalizedLength(0.5));
        if (part == null || !seen.Add(part.Owner)) continue;
        bool Hit(Curve curve)
        { using var nurbs = curve.ToNurbsCurve(); return nurbs != null && context.PickFrustumTest(nurbs, out _, out _, out _); }
        if (_settings.Type == SeamType.Part)
        { if (Hit(part.Outline)) targets.Add(new BoxTarget(part, [])); }
        else
        {
          var edges = Enumerable.Range(0, part.Edges.Count).Where(i => Hit(part.Edges[i])).ToArray();
          if (edges.Length > 0) targets.Add(new BoxTarget(part, edges));
        }
      }
      return targets;
    }
    private Part? ResolveCore(Rhino.Display.RhinoViewport viewport, System.Drawing.Point pixel)
    {
      LastError = null;
      GapGeometry = _noGaps;
      if (viewport.ParentView is not {} view || !viewport.GetFrustumLine(pixel.X, pixel.Y, out var ray)) return null;
      using var context = new PickContext { View = view, PickLine = ray, PickStyle = PickStyle.PointPick,
        PickMode = PickMode.Wireframe, PickGroupsEnabled = false, SubObjectSelectionEnabled = false };
      context.SetPickTransform(viewport.GetPickTransform(pixel)); context.UpdateClippingPlanes();
      var references = _doc.Objects.PickObjects(context) ?? [];
      try
      {
        var hits = new List<(RhinoObject Object, Point3d Point, double Distance)>();
        foreach (var reference in references)
        {
          if (reference.Object() is not { Geometry: Curve } obj || IsLabelObject(obj) || !obj.IsSelectable(true, false, false, false)) continue;
          using var nurbs = ((Curve)obj.Geometry).ToNurbsCurve();
          if (nurbs == null || !context.PickFrustumTest(nurbs, out double t, out _, out _)) continue;
          var point = nurbs.PointAt(t); var screen = viewport.WorldToClient(point);
          hits.Add((obj, point, Math.Pow(screen.X - pixel.X, 2) + Math.Pow(screen.Y - pixel.Y, 2)));
        }
        foreach (var hit in hits.OrderBy(hit => hit.Distance))
          if (Capture(hit.Object, hit.Point) is {} part) { GapGeometry = _noGaps; LastError = null; return part; }
      }
      finally { foreach (var reference in references) reference.Dispose(); }
      if (_settings.Type != SeamType.Part) return null;
      foreach (var seed in _seeds)
      {
        var pixels = seed.Bounds.GetCorners().Select(viewport.WorldToClient).Where(point => point.IsValid).ToArray();
        if (pixels.Length == 0 || pixel.X < pixels.Min(p => p.X) || pixel.X > pixels.Max(p => p.X) ||
          pixel.Y < pixels.Min(p => p.Y) || pixel.Y > pixels.Max(p => p.Y)) continue;
        var part = Capture(seed.Object, ((Curve)seed.Object.Geometry).PointAtNormalizedLength(0.5));
        if (part == null || !part.Outline.IsClosed || !Intersection.LinePlane(ray, part.Plane, out double t)) continue;
        var point = ray.PointAt(t);
        if (part.Outline.Contains(point, part.Plane, _doc.ModelAbsoluteTolerance) == PointContainment.Outside) continue;
        part.PickPoint = point; GapGeometry = _noGaps; LastError = null; return part;
      }
      return null;
    }
    internal Part Take(Part part)
    { foreach (var id in _parts.Where(pair => ReferenceEquals(pair.Value, part)).Select(pair => pair.Key).ToArray()) _parts.Remove(id); return part; }
    public void Dispose()
    {
      foreach (var part in _parts.Values.Distinct()) part.Dispose(); _parts.Clear();
      foreach (var marker in _errors.Values.SelectMany(error => error.Markers.Values)) marker.Dispose();
      _errors.Clear(); GapGeometry = _noGaps;
    }
  }

  private static bool IsLayer(RhinoDoc doc, RhinoObject obj, string name) =>
    string.Equals(doc.Layers[obj.Attributes.LayerIndex].Name, name, StringComparison.OrdinalIgnoreCase);
  private static bool IsFinishedCut(RhinoObject obj) => obj.Attributes.GetUserString(FinishedCutKey) == "1";
  private static bool IsLabelObject(RhinoObject obj) => obj.Geometry is not Curve ||
    obj.Attributes.GetUserString("vTitle") == "1" || obj.Attributes.GetUserString("vTitleFrame") == "1" ||
    obj.Attributes.GetUserString("notch.object_role") == "label" ||
    obj.Attributes.Name is "PartLabel" or "NotchLabel";

  private static Part CapturePart(RhinoDoc doc, RhinoObject picked, Point3d pickPoint)
    => CaptureBoundary(doc,picked,pickPoint,[]);

  private static RhinoObject[] ConnectedSources(IEnumerable<RhinoObject> objects,RhinoObject picked,Point3d pickPoint,double tolerance)
  {
    var pool=objects.Where(IsBoundarySource).OrderBy(obj=>obj.Id).ToArray();
    var seed=pool.FirstOrDefault(obj=>obj.Id==picked.Id)??pool.OrderBy(obj=>Distance((Curve)obj.Geometry,pickPoint)).FirstOrDefault();
    if(seed==null)return [];
    var selected=(Curve)seed.Geometry;
    var connected=new HashSet<Guid>{seed.Id};
    var ends=new List<Point3d>{selected.PointAtStart,selected.PointAtEnd};
    bool changed;
    do
    {
      changed=false;
      foreach(var obj in pool.Where(obj=>!connected.Contains(obj.Id)))
      {
        var curve=(Curve)obj.Geometry;
        if(!ends.Any(point=>point.DistanceTo(curve.PointAtStart)<=tolerance||point.DistanceTo(curve.PointAtEnd)<=tolerance))continue;
        connected.Add(obj.Id);ends.Add(curve.PointAtStart);ends.Add(curve.PointAtEnd);changed=true;
      }
    }while(changed);
    return pool.Where(obj=>connected.Contains(obj.Id)).ToArray();
  }

  private static Part CaptureBoundary(RhinoDoc doc,RhinoObject picked,Point3d pickPoint,IReadOnlyList<RhinoObject> selectedSources)
  {
    double tolerance = doc.ModelAbsoluteTolerance * (double.IsFinite(JoinToleranceFactor) && JoinToleranceFactor > 0 ? JoinToleranceFactor : BoundaryJoinToleranceFactor);
    if (IsLabelObject(picked)) throw new InvalidOperationException("Text and label outlines are not finished part boundaries.");
    if(IsDetached(picked))throw new InvalidOperationException("Use Edit > Reattach to reconnect a detached allowance.");
    if(picked.Attributes.GetUserString(OwnerKey) is {} owner)
    {
      int group=SmallestGroup(doc,picked);
      var linked=(doc.Objects.FindByUserString(SourceOwnerKey,owner,true,false,true,ObjectType.Curve)??[])
        .Where(obj=>obj.Geometry is Curve&&!obj.IsDeleted&&obj.Attributes.GetUserString(OwnerKey)==null&&
          (group<0||(obj.Attributes.GetGroupList()??[]).Contains(group))).ToArray();
      if(linked.MinBy(obj=>Distance((Curve)obj.Geometry,pickPoint)) is {} source)
      {picked=source;selectedSources=linked;}
    }
    if(IsSeamEditCandidate(picked) && FindSeamBatches(doc,[picked]).FirstOrDefault() is {} batch)
    {
      var linked=BatchSources(doc,batch);
      var profile=GetSourceProfile(linked);
      var joined=JoinSeamSources(linked.Select(obj=>(Curve)obj.Geometry),tolerance);
      if(joined.Length==1)
      {
        var part=CaptureOutline(doc,joined[0],linked.ToList(),pickPoint,profile?.OpenPlaneNormal);
        part.CapStart=profile?.CapStart??DefaultOpenCap;
        part.CapEnd=profile?.CapEnd??DefaultOpenCap;
        return part;
      }
      foreach(var curve in joined)curve.Dispose();
    }
    var selected = (Curve)picked.Geometry;
    var candidates = new List<RhinoObject>();
    RhinoObject[] incomplete = [];
    Curve? outline = null;
    // A directly picked closed finished curve has an unambiguous identity, even without grouping.
    bool grouped = (picked.Attributes.GetGroupList() ?? []).Any(index => doc.Groups[index] is { IsDeleted: false });
    if (!grouped && selected.IsClosed && (IsKnownFinishedSource(picked) || !IsLayer(doc, picked, DefaultLayer))) { candidates.Add(picked); outline = selected.DuplicateCurve(); }
    else
    {
      var groups = (picked.Attributes.GetGroupList() ?? []).Where(index => doc.Groups[index] is { IsDeleted: false })
        .OrderBy(doc.Groups.GroupObjectCount).ToArray();
      foreach (int group in groups)
      {
        var members=(doc.Groups.GroupMembers(group)??[]).Where(IsBoundarySource).ToArray();
        var plot = members.Where(obj => IsLayer(doc, obj, FinishedLayerName) || IsKnownFinishedSource(obj)).ToArray();
        var pool = plot.Length > 0 ? plot : members.Where(obj => !IsLayer(doc, obj, DefaultLayer)).ToArray();
        outline = FindOutline(pool, selected, pickPoint, tolerance,!pool.Any(obj=>obj.Id==picked.Id));
        if (outline != null) { candidates.AddRange(pool); break; }
        // A partial preferred-layer set is not a complete part. Add only end-connected source edges, never a detached reference offset.
        var connected=ConnectedSources(members.Where(obj=>IsKnownFinishedSource(obj)||!IsLayer(doc,obj,DefaultLayer)),picked,pickPoint,tolerance);
        if(incomplete.Length==0&&connected.Any(obj=>obj.Id==picked.Id))incomplete=connected;
        outline=FindOutline(connected,selected,pickPoint,tolerance,!connected.Any(obj=>obj.Id==picked.Id));
        if(outline!=null){candidates.AddRange(connected);break;}
      }
      if (outline == null)
      {
        var visible=doc.Objects.GetObjectList(ObjectType.Curve).Where(obj=>!obj.IsHidden&&IsBoundarySource(obj)).ToArray();
        var pools=new[]{selectedSources.ToArray(),visible.Where(obj=>obj.Attributes.LayerIndex==picked.Attributes.LayerIndex).ToArray(),
          visible.Where(obj=>IsKnownFinishedSource(obj)||!IsLayer(doc,obj,DefaultLayer)).ToArray()};
        foreach(var pool in pools)
        {
          if(pool.Length==0)continue;
          var connected=ConnectedSources(pool,picked,pickPoint,tolerance);
          if (incomplete.Length == 0&&connected.Any(obj=>obj.Id==picked.Id)) incomplete=connected;
          outline=FindOutline(connected,selected,pickPoint,tolerance,false);
          if(outline==null)continue;
          candidates.AddRange(connected);break;
        }
      }
    }
    if (outline == null)
    {
      var openSources=incomplete.Length>0?incomplete:[picked];
      var joined=JoinSeamSources(openSources.Select(obj=>(Curve)obj.Geometry),tolerance);
      if(joined.Length==1&&!joined[0].IsClosed)
      {outline=joined[0];candidates.AddRange(openSources);}
      else
      {
        foreach(var curve in joined)curve.Dispose();
        var gaps=BoundaryGaps(openSources,tolerance);
        if(gaps.Length>0)throw new BoundaryGapException(gaps);
      }
    }
    if (outline == null && _settings.Allowance == 0) { candidates.Clear(); candidates.Add(picked); outline = selected.DuplicateCurve(); }
    if (outline == null) throw new InvalidOperationException("No unambiguous planar finished boundary was found.");
    return CaptureOutline(doc, outline, candidates, pickPoint);
  }

  private static bool IsKnownFinishedSource(RhinoObject obj)=>IsFinishedCut(obj)||
    obj.Attributes.GetUserString(SourceRootKey)!=null;
  private static bool IsBoundarySource(RhinoObject obj)=>obj.Geometry is Curve&&!obj.IsDeleted&&!IsLabelObject(obj)&&
    obj.Attributes.GetUserString(OwnerKey)==null&&obj.Attributes.GetUserString("notch.object_role")==null&&obj.Attributes.Name!="Notch";
  private static int SmallestGroup(RhinoDoc doc,RhinoObject obj)=>(obj.Attributes.GetGroupList()??[])
    .Where(index=>doc.Groups[index] is {IsDeleted:false}).OrderBy(doc.Groups.GroupObjectCount).FirstOrDefault(-1);

  private static Point3d[] BoundaryGaps(IReadOnlyList<RhinoObject> sources, double tolerance)
  {
    var curves = sources.Where(obj => obj.Geometry is Curve && obj.Attributes.GetUserString(OwnerKey) == null)
      .Select(obj => (Curve)obj.Geometry).ToArray();
    var endpoints = new List<Point3d>();
    foreach (var curve in curves.Where(curve => !curve.IsClosed))
      foreach (var point in new[] { curve.PointAtStart, curve.PointAtEnd })
        if (!curves.Any(other => !ReferenceEquals(curve, other) && other.ClosestPoint(point, out var t) && other.PointAt(t).DistanceTo(point) <= tolerance) &&
          !endpoints.Any(existing => existing.DistanceTo(point) <= tolerance)) endpoints.Add(point);
    return endpoints.ToArray();
  }

  private static int DirectionOnOutline(Curve source,Curve outline,double tolerance)
  {
    if(!source.NormalizedLengthParameter(0.5,out double t))return 0;
    var point=source.PointAt(t);
    if(!outline.ClosestPoint(point,out double at) || outline.PointAt(at).DistanceTo(point)>tolerance)return 0;
    double dot=outline.TangentAt(at)*source.TangentAt(t);
    return dot>=MatchCosine?1:dot<=-MatchCosine?-1:0;
  }

  private static Plane OrientOutline(RhinoDoc doc,Curve outline,Curve? anchor,double[]? openPlaneNormal)
  {
      double tolerance=doc.ModelAbsoluteTolerance;
      var activePlane=doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??Plane.WorldXY;
      bool straight=!outline.IsClosed && outline.IsLinear(tolerance);
      if(straight)activePlane=new Plane(outline.PointAtStart,activePlane.XAxis,activePlane.YAxis);
      if(straight && openPlaneNormal is {Length:3})
      {
        var normalHint=new Vector3d(openPlaneNormal[0],openPlaneNormal[1],openPlaneNormal[2]);
        var saved=new Plane(outline.PointAtStart,normalHint);
        if(saved.IsValid && outline.IsInPlane(saved,tolerance))activePlane=saved;
      }
      var fitted=activePlane;
      if ((!straight||!outline.IsInPlane(activePlane,tolerance))&&
        !outline.TryGetPlane(out fitted,tolerance))
      {
        fitted=activePlane;
        if (!outline.IsInPlane(fitted, tolerance)) throw new InvalidOperationException("The finished boundary is not planar.");
      }
      var normal = fitted.Normal;
      double[] components = [normal.X, normal.Y, normal.Z];
      int major = Enumerable.Range(0, 3).OrderByDescending(index => Math.Abs(components[index])).First();
      if (components[major] < 0) normal = -normal;
      var xAxis = Vector3d.XAxis - normal.X * normal;
      if (!xAxis.Unitize()) xAxis = Unit(Vector3d.YAxis - normal.Y * normal);
      var plane = new Plane(fitted.Origin, xAxis, Vector3d.CrossProduct(normal, xAxis));
      if (outline.IsClosed)
      {
        var orientation = outline.ClosedCurveOrientation(plane);
        if (orientation == CurveOrientation.Clockwise) outline.Reverse();
        else if (orientation != CurveOrientation.CounterClockwise) throw new InvalidOperationException("The finished boundary orientation is ambiguous.");
      }
      else if(anchor!=null &&
        anchor.NormalizedLengthParameter(0.5,out double sourceParameter) &&
        outline.ClosestPoint(anchor.PointAt(sourceParameter),out double t) &&
        outline.TangentAt(t)*anchor.TangentAt(sourceParameter)<0)outline.Reverse();
      return plane;
  }

  private static bool KeepsJoinedSections(RhinoObject source,IReadOnlySet<Guid>? pending) =>
    pending?.Contains(source.Id)==true || source.Attributes.GetUserString(JoinedSectionsKey)=="1";

  private static Point3d[] JoinedJunctions(Curve curve)
  {
    var segments=curve.DuplicateSegments()??[];
    try { return segments.Skip(1).Select(segment=>segment.PointAtStart).ToArray(); }
    finally { foreach(var segment in segments)segment.Dispose(); }
  }

  private static Part CaptureOutline(RhinoDoc doc, Curve outline, List<RhinoObject> candidates, Point3d pickPoint,
    double[]? openPlaneNormal=null,IReadOnlySet<Guid>? joinedSources=null)
  {
    double tolerance=doc.ModelAbsoluteTolerance;
    try
    {
      var plane=OrientOutline(doc,outline,candidates.MinBy(obj=>obj.Id)?.Geometry as Curve,openPlaneNormal);
      var self = Intersection.CurveSelf(outline, tolerance);
      if (self != null && self.Any(hit => !ClosureContact(outline, hit, tolerance)))
        throw new InvalidOperationException("The finished boundary intersects itself.");
      var junctions=candidates.Where(obj=>KeepsJoinedSections(obj,joinedSources)).SelectMany(obj=>JoinedJunctions((Curve)obj.Geometry));
      var edges = SplitEdges(outline, candidates.Select(obj => (Curve)obj.Geometry), tolerance *
        (double.IsFinite(JoinToleranceFactor) && JoinToleranceFactor > 0 ? JoinToleranceFactor : BoundaryJoinToleranceFactor),junctions);
      var boundaryObjects = candidates.Where(obj => OnBoundary((Curve)obj.Geometry, outline, tolerance) ||
        edges.Any(edge => OnBoundary(edge, (Curve)obj.Geometry, tolerance))).OrderBy(obj => obj.Id).ToArray();
      if (boundaryObjects.Length == 0) throw new InvalidOperationException("No source curves could be assigned to the finished boundary.");
      var (owner,stamp)=BoundaryIdentity(boundaryObjects);
      var common = (boundaryObjects[0].Attributes.GetGroupList() ?? []).Where(index => doc.Groups[index] is { IsDeleted: false } &&
        boundaryObjects.All(obj => (obj.Attributes.GetGroupList() ?? []).Contains(index))).OrderBy(doc.Groups.GroupObjectCount).ToArray();
      var sources = boundaryObjects.Select(obj => new SourceObject(obj.Id, obj.RuntimeSerialNumber, obj.Geometry.DataCRC(0), AttributeStamp(obj.Attributes))).ToArray();
      return new Part(outline, plane, edges, sources, common, owner, stamp, pickPoint);
    }
    catch { outline.Dispose(); throw; }
  }

  private static (string Owner,string Stamp) BoundaryIdentity(IEnumerable<RhinoObject> objects)
  {
    var boundary=objects.OrderBy(obj=>obj.Id).ToArray();
    var ids=boundary.Select(obj=>obj.Id).ToHashSet();
    var savedOwners=boundary.Where(obj=>Guid.TryParse(obj.Attributes.GetUserString(SourceRootKey),out var root)&&ids.Contains(root))
      .Select(obj=>obj.Attributes.GetUserString(SourceOwnerKey)).Where(value=>!string.IsNullOrEmpty(value)).Distinct().ToArray();
    string owner=savedOwners.Length==1?savedOwners[0]!:SourceIdentity(boundary.Select(obj=>obj.Id));
    string stamp=string.Join(";",boundary.Select(obj=>obj.Id.ToString("N")+":"+obj.Geometry.DataCRC(0).ToString("X8",CultureInfo.InvariantCulture)));
    return (owner,stamp);
  }

  private static string SourceIdentity(IEnumerable<Guid> ids) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";",ids.Order().Select(id=>id.ToString("N"))))));

  private static Curve? FindOutline(IEnumerable<RhinoObject> objects, Curve selected, Point3d pickPoint, double tolerance, bool allowContaining)
  {
    var curves = objects.GroupBy(obj => obj.Attributes.GetUserString(SourceCurveKey) ?? obj.Id.ToString("D"))
      .OrderBy(group => group.Key, StringComparer.Ordinal).SelectMany(group =>
        Curve.JoinCurves(group.OrderBy(obj => obj.Id).Select(obj => (Curve)obj.Geometry), tolerance, preserveDirection: true) ?? []).ToArray();
    if (curves.Length == 0) return null;
    var joined = Curve.JoinCurves(curves, tolerance) ?? [];
    var loops = joined.Where(curve => curve.IsClosed && curve.TryGetPlane(out _, tolerance)).ToList();
    // Resolve crossings consistently: splitting a source must not make endpoint joining choose a different perimeter.
    if (curves.Length > 1 || loops.Count == 0)
    {
      var regions = GroupBoundaryTopology.PlanarRegions(curves, tolerance, tolerance);
      if (regions.Count > 0) { loops.Clear(); loops.AddRange(regions); }
    }
    try
    {
      var eligible = loops.Where(curve => curve.IsClosed && curve.TryGetPlane(out _, tolerance)).ToArray();
      var choices = eligible.Where(curve =>
        (OnBoundary(selected, curve, tolerance) || pickPoint.IsValid && Distance(curve, pickPoint) <= tolerance * ConfiguredBoundaryToleranceFactor ||
          allowContaining && curve.TryGetPlane(out var plane, tolerance) &&
          curve.Contains(pickPoint.IsValid ? pickPoint : selected.PointAtNormalizedLength(0.5), plane, tolerance) != PointContainment.Outside));
      var best = choices.OrderByDescending(curve => { using var mass = AreaMassProperties.Compute(curve); return mass?.Area ?? 0; }).FirstOrDefault();
      if (best == null && allowContaining && pickPoint.IsValid)
        best = eligible.OrderBy(curve => Distance(curve, pickPoint)).FirstOrDefault();
      return best?.DuplicateCurve();
    }
    finally { foreach (var curve in curves.Concat(joined).Concat(loops).Distinct()) curve.Dispose(); }
  }

  private static bool OnBoundary(Curve curve, Curve outline, double tolerance) => Enumerable.Range(0, ConfiguredMatchSamples).All(index =>
    Distance(outline, curve.PointAtNormalizedLength((double)index / (ConfiguredMatchSamples - 1))) <= tolerance * ConfiguredBoundaryToleranceFactor);

  private static List<Curve> SplitEdges(Curve outline, IEnumerable<Curve> sources, double tolerance,
    IEnumerable<Point3d>? additionalJunctions=null)
  {
    var parameters = new List<double>();
    var domain = outline.Domain;
    double epsilon = Math.Abs(domain.Length) * ParameterFraction;
    double cursor = domain.T0;
    while (outline.GetNextDiscontinuity(Continuity.G1_continuous, cursor,
      cosAngleTolerance: KinkCosine, curvatureTolerance: 0, t1: domain.T1, t: out double next))
    { if (next <= cursor || next >= domain.T1 - epsilon) break; parameters.Add(next); cursor = Math.Min(next + epsilon, domain.T1); }
    // Preserve source and native join junctions even when the tangents match.
    var junctions=sources.Where(curve=>!curve.IsClosed).SelectMany(curve=>new[]{curve.PointAtStart,curve.PointAtEnd})
      .Concat(additionalJunctions??[]).ToArray();
    foreach(var point in junctions)
      if(outline.ClosestPoint(point,out double t) && outline.PointAt(t).DistanceTo(point)<=tolerance && t>domain.T0+epsilon && t<domain.T1-epsilon)
        parameters.Add(t);
    var split = parameters.OrderBy(t => t).Aggregate(new List<double>(), (values, t) =>
      { if (values.Count == 0 || t - values[^1] > epsilon) values.Add(t); return values; });
    // Keep sub-tolerance slivers in their neighboring exact section, not as independently offsettable edges.
    double start = domain.T0;
    split = split.Where(t =>
    {
      if (outline.GetLength(new Interval(start, t)) <= tolerance) return false;
      start = t; return true;
    }).ToList();
    if (split.Count > 0 && outline.GetLength(new Interval(split[^1], domain.T1)) <= tolerance) split.RemoveAt(split.Count - 1);
    var edges = TrimSections(outline, split);
    if (edges.Count == 0) throw new InvalidOperationException("Could not split the finished boundary into exact edges.");
    bool sourceJoin=junctions.Any(point=>point.DistanceTo(outline.PointAtStart)<=tolerance);
    if (outline.IsClosed && edges.Count > 1 && !sourceJoin &&
      edges[^1].PointAtEnd.DistanceTo(edges[0].PointAtStart) <= tolerance &&
      Unit(edges[^1].TangentAtEnd) * Unit(edges[0].TangentAtStart) >= KinkCosine)
    {
      var wrapped = Curve.JoinCurves([edges[^1], edges[0]], tolerance, preserveDirection: true);
      if (wrapped?.Length == 1)
      { edges[^1].Dispose(); edges[0].Dispose(); edges.RemoveAt(edges.Count - 1); edges[0] = wrapped[0]; }
      else if (wrapped != null) foreach (var curve in wrapped) curve.Dispose();
    }
    return edges;
  }

  private static List<Curve> TrimSections(Curve source, IReadOnlyList<double> parameters)
  {
    if (parameters.Count == 0) return [source.DuplicateCurve()];
    var pieces = new List<Curve>();
    double start = source.Domain.T0;
    try
    {
      foreach (double end in parameters.Append(source.Domain.T1))
      {
        pieces.Add(source.Trim(new Interval(start, end))
          ?? throw new InvalidOperationException("Cannot extract an exact finished section."));
        start = end;
      }
      return pieces;
    }
    catch { foreach (var piece in pieces) piece.Dispose(); throw; }
  }

  private static string AttributeStamp(ObjectAttributes attributes)
  {
    var strings = attributes.GetUserStrings();
    var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
    {
      attributes.LayerIndex, attributes.Name, attributes.Mode, attributes.ColorSource,
      Color = attributes.ObjectColor.ToArgb(), attributes.MaterialIndex, attributes.MaterialSource,
      Groups = (attributes.GetGroupList() ?? []).OrderBy(index => index).ToArray(),
      Strings = strings.AllKeys.Where(key => key != null).OrderBy(key => key, StringComparer.Ordinal)
        .Select(key => new { Key = key, Value = strings[key!] }).ToArray()
    });
    return Convert.ToHexString(SHA256.HashData(data));
  }

  private static bool SnapshotMatches(RhinoObject? obj, uint serial, uint geometry, string attributes) =>
    obj != null && !obj.IsDeleted && obj.RuntimeSerialNumber == serial && obj.Geometry.DataCRC(0) == geometry && AttributeStamp(obj.Attributes) == attributes;

  private sealed record LegacyCandidate(RhinoObject Object, Curve[] Pieces, int[] Edges, double[] Distances);

  private static int ProfileAnchor(Part part, RhinoObject root) => part.Edges.Select((edge, index) =>
    (index, distance: Distance(edge, ((Curve)root.Geometry).PointAtStart), start: edge.PointAtStart.DistanceTo(((Curve)root.Geometry).PointAtStart)))
    .OrderBy(item => item.distance <= RhinoMath.ZeroTolerance ? 0 : item.distance).ThenBy(item => item.start).First().index;

  private static string[] ProfileEdgeSources(Part part, IEnumerable<RhinoObject> sources, int anchor, double tolerance,
    IReadOnlySet<Guid>? joinedSources=null)
  {
    var curves=sources.Select(obj=>(obj.Id,Curve:(Curve)obj.Geometry,Sections:KeepsJoinedSections(obj,joinedSources))).ToArray();
    var segments=curves.Where(item=>item.Sections).ToDictionary(item=>item.Id,item=>item.Curve.DuplicateSegments()??[]);
    try
    {
      return Enumerable.Range(0,part.Edges.Count).Select(i =>
      {
        var edge=part.Edges[(anchor+i)%part.Edges.Count];
        var matches=curves.Where(item=>OnBoundary(edge,item.Curve,tolerance)).Take(2).ToArray();
        if(matches.Length!=1)return string.Empty;
        string key=matches[0].Id.ToString("N");
        if(!segments.TryGetValue(matches[0].Id,out var sections) || sections.Length<2)return key;
        var indices=Enumerable.Range(0,sections.Length).Where(index=>OnBoundary(edge,sections[index],tolerance)).Take(2).ToArray();
        return indices.Length==1?key+":"+indices[0].ToString(CultureInfo.InvariantCulture):string.Empty;
      }).ToArray();
    }
    finally { foreach(var segment in segments.Values.SelectMany(sections=>sections))segment.Dispose(); }
  }

  private static string SerializeProfile(RhinoDoc doc,Part part,RhinoObject root,double[] distances,SeamOptions settings,int side,
    IReadOnlySet<Guid>? joinedSources=null)
  {
    int anchor=ProfileAnchor(part,root);
    var indices=Enumerable.Range(0,part.Edges.Count).Select(i=>(i+anchor)%part.Edges.Count).ToArray();
    var sources=part.Sources.Select(source=>doc.Objects.FindId(source.Id)).OfType<RhinoObject>().ToArray();
    return System.Text.Json.JsonSerializer.Serialize(new AllowanceProfile(
      indices.Select(i=>part.Edges[i].GetLength()).ToArray(),indices.Select(i=>distances[i]).ToArray())
    {
      EdgeSources=ProfileEdgeSources(part,sources,anchor,doc.ModelAbsoluteTolerance,joinedSources),
      SourceDirections=sources.ToDictionary(source=>source.Id,source=>DirectionOnOutline((Curve)source.Geometry,part.Outline,
        doc.ModelAbsoluteTolerance*ConfiguredBoundaryToleranceFactor)),
      Side=side is 1 or -1?side:null, Open=!part.Outline.IsClosed, Chamfer=settings.Chamfer, ChamferAngle=settings.ChamferAngle,
      OpenPlaneNormal=[part.Plane.Normal.X,part.Plane.Normal.Y,part.Plane.Normal.Z],
      CapStart=part.Outline.IsClosed?null:part.CapStart, CapEnd=part.Outline.IsClosed?null:part.CapEnd
    });
  }

  private static void SetAllowanceAttributes(ObjectAttributes attributes,Curve geometry,string owner,string stamp,
    string role,int edge,double allowance)
  {
    attributes.Name="SeamAllowance";
    attributes.ColorSource=ObjectColorSource.ColorFromLayer;
    attributes.SetUserString(OwnerKey,owner);attributes.SetUserString(StampKey,stamp);
    attributes.SetUserString(VersionKey,SchemaVersion);attributes.SetUserString(RoleKey,role);
    attributes.SetUserString(EdgeKey,edge.ToString(CultureInfo.InvariantCulture));
    attributes.SetUserString(DistanceKey,allowance.ToString("R",CultureInfo.InvariantCulture));
    attributes.SetUserString(GeometryStampKey,geometry.DataCRC(0).ToString("X8",CultureInfo.InvariantCulture));
  }

  private static void LinkFinishedSource(ObjectAttributes attributes,string owner,string root,string origin)
  {
    attributes.SetUserString(SourceOwnerKey,owner);
    attributes.SetUserString(SourceRootKey,root);
    attributes.SetUserString(SourceCurveKey,origin);
    attributes.SetUserString(ImportedStyleKey,null);
  }

  internal static void RegisterImportedAllowances(RhinoDoc doc,IReadOnlyDictionary<Guid,double> widths,
    IReadOnlyDictionary<Guid,bool> boundary,IReadOnlyList<ImportedAllowance> allowances,Func<int>? createGroup,
    IReadOnlyDictionary<Guid,ImportedStyle> styles)
  {
    var sources=boundary.Keys.Union(widths.Keys).Select(id=>doc.Objects.FindId(id) is {Geometry:Curve} obj?obj:
      throw new InvalidOperationException("An imported finished curve is unavailable.")).ToArray();
    if(sources.Length==0||allowances.Count==0)return;
    double tolerance=doc.ModelAbsoluteTolerance;
    double joinTolerance=tolerance*(double.IsFinite(JoinToleranceFactor)&&JoinToleranceFactor>0?JoinToleranceFactor:BoundaryJoinToleranceFactor);
    var joined=Curve.JoinCurves(sources.Select(source=>(Curve)source.Geometry),joinTolerance,false,out int[] keys);
    try
    {
      bool haveChains=joined.Length>0&&keys.Length==sources.Length&&keys.All(key=>key>=0&&key<joined.Length);
      if(!haveChains)keys=Enumerable.Range(0,sources.Length).ToArray();
      var byId=sources.Select((source,index)=>(source,Chain:keys[index])).ToDictionary(entry=>entry.source.Id);
      var byChain=byId.Values.ToLookup(entry=>entry.Chain,entry=>entry.source);
      var parents=Enumerable.Range(0,haveChains?joined.Length:sources.Length).ToArray();
      int Find(int chain)
      {
        while(parents[chain]!=chain){parents[chain]=parents[parents[chain]];chain=parents[chain];}
        return chain;
      }
      // A native joined offset may cover more than one original chain; keep all of its declared sources together.
      foreach(var allowance in allowances)
      {
        int root=Find(byId[allowance.Sources[0]].Chain);
        foreach(var id in allowance.Sources)parents[Find(byId[id].Chain)]=root;
      }
      foreach(var batch in allowances.GroupBy(allowance=>Find(byId[allowance.Sources[0]].Chain)))
      {
        var known=batch.SelectMany(allowance=>allowance.Sources).Distinct().ToArray();
        var chains=known.Select(id=>byId[id].Chain).Distinct().ToArray();
        var finished=known.Select(id=>byId[id].source).ToArray();
        Part? captured=null;
        try
        {
          if(haveChains&&chains.Length==1)
          {
            try
            {
              var contour=joined[chains[0]];
              captured=CaptureOutline(doc,contour.DuplicateCurve(),byChain[chains[0]].ToList(),contour.PointAtStart);
              var ids=captured.Sources.Select(source=>source.Id).ToHashSet();
              if(known.All(ids.Contains))finished=captured.Sources.Select(source=>byId[source.Id].source).ToArray();
              else {captured.Dispose();captured=null;}
            }
            catch(InvalidOperationException ex)
            {Log.Write(Section,"Imported allowance has no editable profile: {0}",ex.Message);}
          }
          WriteImportedSeamBatch(doc,batch,finished,captured,widths,boundary,createGroup,styles);
        }
        finally{captured?.Dispose();}
      }
    }
    finally{foreach(var curve in joined)curve.Dispose();}
  }

  private static void WriteImportedSeamBatch(RhinoDoc doc,IEnumerable<ImportedAllowance> allowances,RhinoObject[] finished,
    Part? part,IReadOnlyDictionary<Guid,double> widths,IReadOnlyDictionary<Guid,bool> boundary,Func<int>? createGroup,
    IReadOnlyDictionary<Guid,ImportedStyle> styles)
  {
    var (owner,stamp)=BoundaryIdentity(finished);
    var root=finished.MinBy(source=>source.Id)!;
    string rootId=root.Id.ToString("D");
    double[]? distances=null;
    if(part!=null)
    {
      var spans=finished.GroupBy(source=>widths.GetValueOrDefault(source.Id))
        .Select(group=>(Width:group.Key,Curves:group.Select(source=>(Curve)source.Geometry).ToArray())).ToArray();
      var values=part.Edges.Select(edge=>spans.Where(span=>OnTargets(edge,span.Curves,doc.ModelAbsoluteTolerance))
        .Select(span=>span.Width).ToArray()).ToArray();
      if(values.All(value=>value.Length==1))distances=values.Select(value=>value[0]).ToArray();
    }
    int side=part==null||part.Outline.IsClosed?DefaultOpenSide:InferOpenSide(part,
      allowances.Select(entry=>doc.Objects.FindId(entry.Id)?.Geometry).OfType<Curve>(),doc.ModelAbsoluteTolerance);
    string? profile=distances==null?null:SerializeProfile(doc,part!,root,distances,Defaults,side);
    int group=createGroup?.Invoke()??-1;
    foreach(var source in finished)
    {
      using var attributes=source.Attributes.Duplicate();
      LinkFinishedSource(attributes,owner,rootId,source.Id.ToString("D"));
      if(styles.TryGetValue(source.Id,out var style))
        attributes.SetUserString(ImportedStyleKey,string.Join("|",style.SignedWidth.ToString("R",CultureInfo.InvariantCulture),style.Start,style.End));
      attributes.SetUserString(ProfileKey,profile);
      attributes.SetUserString(ProfileRootKey,profile!=null&&source.Id==root.Id?"1":null);
      if(!widths.ContainsKey(source.Id)&&boundary.GetValueOrDefault(source.Id))attributes.SetUserString(FinishedCutKey,"1");
      if(group>=0)attributes.AddToGroup(group);
      if(!doc.Objects.ModifyAttributes(source.Id,attributes,true))
        throw new InvalidOperationException("Cannot link an imported finished edge to its allowance.");
    }
    foreach(var allowance in allowances)
    {
      if(doc.Objects.FindId(allowance.Id) is not {Geometry:Curve curve} obj)
        throw new InvalidOperationException("An imported allowance is unavailable.");
      double width=allowance.Sources.Max(id=>widths[id]);
      string role=curve.IsClosed?"envelope":ImportedJoinedRole;
      int edge=0;
      if(role==ImportedJoinedRole&&part!=null&&distances!=null&&allowance.Sources.Length==1)
      {
        var candidates=Enumerable.Range(0,part.Edges.Count).Where(i=>distances[i]==width&&
          MatchAllowance(curve,part.Edges[i],part,doc.ModelAbsoluteTolerance,out double measured,out _)&&
          Math.Abs(measured-width)<=Math.Max(doc.ModelAbsoluteTolerance*ConfiguredBoundaryToleranceFactor,width*MatchVariation)).ToArray();
        if(candidates.Length==1){role="edge";edge=candidates[0];}
      }
      using var attributes=obj.Attributes.Duplicate();
      SetAllowanceAttributes(attributes,curve,owner,stamp,role,edge,width);
      if(group>=0)attributes.AddToGroup(group);
      if(!doc.Objects.ModifyAttributes(obj.Id,attributes,true))
        throw new InvalidOperationException("Cannot save the imported seam allowance metadata.");
    }
  }

  private static AllowanceProfile? ParseProfile(string? value)
  {
    try
    {
      var profile=System.Text.Json.JsonSerializer.Deserialize<AllowanceProfile>(value??"null");
      return profile?.Lengths is {Length:>0} && profile.Distances!=null && profile.Lengths.Length==profile.Distances.Length &&
        profile.Lengths.All(length=>double.IsFinite(length)&&length>0) &&
        profile.Distances.All(distance=>double.IsFinite(distance)&&distance>=0)?profile:null;
    }
    catch(System.Text.Json.JsonException ex){Log.Write(Section,$"Invalid allowance profile: {ex.Message}");return null;}
  }

  private static bool ReadProfile(RhinoDoc doc, Part part, double[] distances, out bool recoveredUniform)
  {
    recoveredUniform = false;
    var sources = part.Sources.Select(source => doc.Objects.FindId(source.Id)).OfType<RhinoObject>().ToArray();
    var roots = sources.Where(obj => obj.Attributes.GetUserString(ProfileRootKey) == "1").ToArray();
    if (roots.Length != 1) return false;
    var profile=ParseProfile(roots[0].Attributes.GetUserString(ProfileKey));
    return profile!=null && MapProfile(doc,part,roots[0],profile,distances,false,out recoveredUniform);
  }

  private static bool MapProfile(RhinoDoc doc,Part part,RhinoObject root,AllowanceProfile profile,double[] distances,
    bool sourceChanged,out bool recoveredUniform)
  {
    recoveredUniform=false;
    // Closing or opening a chain changes its order and caps, but not the widths tied to its sources.
    bool sameTopology=profile.Open is not bool wasOpen || wasOpen!=part.Outline.IsClosed;
    int count=part.Edges.Count,anchor=ProfileAnchor(part,root);
    bool matches=!sourceChanged && sameTopology && profile.Lengths.Length==count && Enumerable.Range(0,count).All(i =>
      Math.Abs(profile.Lengths[i]-part.Edges[(i+anchor)%count].GetLength())<=doc.ModelAbsoluteTolerance*ConfiguredBoundaryToleranceFactor);
    if(!matches && profile.EdgeSources is { } keys && keys.Length==count && keys.All(key=>key.Length>0) &&
      keys.Distinct().Count()==count)
    {
      var current=ProfileEdgeSources(part,part.Sources.Select(source=>doc.Objects.FindId(source.Id)).OfType<RhinoObject>(),anchor,doc.ModelAbsoluteTolerance);
      var indices=current.Select(key=>Array.IndexOf(keys,key)).ToArray();
      if(indices.All(index=>index>=0) && indices.Distinct().Count()==count)
      {
        for(int i=0;i<count;i++)distances[(i+anchor)%count]=profile.Distances[indices[i]];
        return true;
      }
    }
    if(!matches)
    {
      if(profile.Distances.Any(value=>value!=profile.Distances[0]))return false;
      Array.Fill(distances,profile.Distances[0]);
      recoveredUniform=true;
      return true;
    }
    for(int i=0;i<count;i++)distances[(i+anchor)%count]=profile.Distances[i];
    return true;
  }

  private static string WriteProfile(RhinoDoc doc, Part original, double[] distances, Guid rootId, int side, SeamOptions settings)
  {
    var root = doc.Objects.FindId(rootId) ?? throw new InvalidOperationException("The allowance-profile anchor disappeared.");
    var sources = doc.Objects.GetObjectList(ObjectType.Curve).Where(obj => obj.Attributes.GetUserString(OwnerKey) == null &&
      obj.Attributes.GetUserString(SourceOwnerKey) == original.Owner &&
      string.Equals(obj.Attributes.GetUserString(SourceRootKey), rootId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
      (original.Groups.Length == 0 || (obj.Attributes.GetGroupList() ?? []).Contains(original.Groups[0]))).ToList();
    // Source splitting must not let the bookkeeping root select a different region from the one previewed.
    using var current = CaptureOutline(doc, original.Outline.DuplicateCurve(), sources, original.PickPoint);
    current.CapStart=original.CapStart;
    current.CapEnd=original.CapEnd;
    if(!current.Outline.IsClosed && current.Outline.PointAtStart.DistanceTo(original.Outline.PointAtEnd)<=doc.ModelAbsoluteTolerance)
    {
      side=-side;
      (current.CapStart,current.CapEnd)=(current.CapEnd,current.CapStart);
    }
    var mapped = current.Edges.Select(edge =>
    {
      var matches = Enumerable.Range(0, original.Edges.Count).GroupBy(i => distances[i])
        .Where(group => OnTargets(edge, group.Select(i => original.Edges[i]).ToArray(), doc.ModelAbsoluteTolerance)).Select(group => group.Key).ToArray();
      if (matches.Length != 1)
      {
        Log.Write(Section, $"Profile correspondence rejected start={edge.PointAtStart} end={edge.PointAtEnd} length={edge.GetLength():G17} matches={string.Join(",", matches)} originalEdges={original.Edges.Count} currentEdges={current.Edges.Count}");
        throw new InvalidOperationException("Cannot preserve the allowance assigned to a finished section.");
      }
      return matches[0];
    }).ToArray();
    string profile=SerializeProfile(doc,current,root,mapped,settings,side);
    foreach (var source in current.Sources)
    {
      var obj = doc.Objects.FindId(source.Id) ?? throw new InvalidOperationException("A finished section disappeared while saving its allowance.");
      using var attributes = obj.Attributes.Duplicate();
      attributes.SetUserString(ProfileKey, profile);
      attributes.SetUserString(ProfileRootKey, source.Id == rootId ? "1" : null);
      if (!doc.Objects.ModifyAttributes(source.Id, attributes, true)) throw new InvalidOperationException("Cannot save the per-edge allowances.");
    }
    return current.Stamp;
  }

  private static void ReadExisting(RhinoDoc doc, Part part, Existing state)
  {
    bool profile = ReadProfile(doc, part, state.Distances, out bool recoveredUniform);
    if (_settings.Type == SeamType.Edge)
      for (int edge = 0; edge < part.Edges.Count; edge++)
        if (part.Sources.Select(source => doc.Objects.FindId(source.Id)).Any(obj => obj != null && IsFinishedCut(obj) &&
          OnBoundary(part.Edges[edge], (Curve)obj.Geometry, doc.ModelAbsoluteTolerance)))
        { state.Curves[edge] = part.Edges[edge].DuplicateCurve(); state.Distances[edge] = 0; }
    var sourceIds = part.Sources.Select(source => source.Id).ToHashSet();
    var sources = part.Sources.Select(source => doc.Objects.FindId(source.Id)).OfType<RhinoObject>().ToArray();
    // Geometry profiles may be stale after editing, but their part-scoped ownership still identifies the old output.
    bool LinkedOwner(RhinoObject source)
    {
      if (part.Groups.Length > 0) return true;
      string? owner = source.Attributes.GetUserString(SourceOwnerKey);
      return owner != null && Guid.TryParse(source.Attributes.GetUserString(SourceRootKey), out var root) &&
        doc.Objects.FindId(root) is not { IsDeleted: false } &&
        !(doc.Objects.FindByUserString(SourceOwnerKey, owner, true, false, true, ObjectType.Curve) ?? [])
          .Any(obj => !sourceIds.Contains(obj.Id) && obj.Attributes.GetUserString(OwnerKey) == null);
    }
    var owners = sources.Where(LinkedOwner).Select(source => source.Attributes.GetUserString(SourceOwnerKey))
      .Where(owner => owner != null).Cast<string>().Append(part.Owner).Distinct().ToArray();
    if (part.Groups.Length > 0 && owners.Any(owner => owner != part.Owner) && (doc.Groups.GroupMembers(part.Groups[0]) ?? []).Any(obj =>
      !sourceIds.Contains(obj.Id) && owners.Contains(obj.Attributes.GetUserString(SourceOwnerKey)) &&
      (obj.Attributes.GetUserString(ProfileRootKey) == "1" ||
       Guid.TryParse(obj.Attributes.GetUserString(SourceRootKey), out var root) && obj.Id == root)))
      throw new InvalidOperationException("Copied seam sources share a group; group the parts separately before editing their allowances.");
    var owned = owners.SelectMany(owner => doc.Objects.FindByUserString(OwnerKey, owner, true, false, true, ObjectType.Curve) ?? [])
      .Where(obj => !IsDetached(obj) && (part.Groups.Length == 0 || (obj.Attributes.GetGroupList() ?? []).Contains(part.Groups[0]))).ToList();
    if (_settings.Type == SeamType.Part)
    {
      // Zero-seam detachment can retire an earlier owner. Recover its entire output from source lineage, not offset shape.
      var scope = part.Groups.Length > 0 ? doc.Groups.GroupMembers(part.Groups[0]) ?? [] : doc.Objects.GetObjectList(ObjectType.Curve).ToArray();
      var lineage = sourceIds.Concat(sources.Select(source => Guid.TryParse(source.Attributes.GetUserString(SourceCurveKey), out var id) ? id : Guid.Empty))
        .Where(id => id != Guid.Empty).ToHashSet();
      var foreignLineage = scope.Where(obj => !obj.IsDeleted && !sourceIds.Contains(obj.Id) && obj.Attributes.GetUserString(OwnerKey) == null)
        .Select(obj => Guid.TryParse(obj.Attributes.GetUserString(SourceCurveKey), out var id) ? id : Guid.Empty).ToHashSet();
      foreach (var obj in scope.Where(obj => !obj.IsDeleted && !IsDetached(obj) && obj.Geometry is Curve && obj.Attributes.GetUserString(OwnerKey) != null && owned.All(known => known.Id != obj.Id)))
      {
        var stamp = obj.Attributes.GetUserString(StampKey);
        if (string.IsNullOrEmpty(stamp)) continue;
        var ancestors = stamp.Split(';').Select(value => value.Split(':')).ToArray();
        if (ancestors.Any(value => value.Length != 2 || !Guid.TryParseExact(value[0], "N", out var id) || !lineage.Contains(id) ||
          foreignLineage.Contains(id) || doc.Objects.FindId(id) is { IsDeleted: false } && !sourceIds.Contains(id) ||
          !uint.TryParse(value[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))) continue;
        owned.Add(obj);
        Log.Write(Section, $"Replacing retired seam lineage object={obj.Id} owner={obj.Attributes.GetUserString(OwnerKey)} currentOwner={part.Owner}");
      }
    }
    var processed = new HashSet<Guid>();
    void Remember(RhinoObject obj)
    {
      if (processed.Add(obj.Id)) state.Objects.Add(new ExistingObject(obj.Id, obj.RuntimeSerialNumber, obj.Geometry.DataCRC(0), AttributeStamp(obj.Attributes)));
    }
    foreach (var obj in owned.Where(obj => !obj.IsDeleted))
    {
      bool sameSource = obj.Attributes.GetUserString(StampKey) == part.Stamp && obj.Attributes.GetUserString(VersionKey) == SchemaVersion;
      if (_settings.Type == SeamType.Part) { Remember(obj); continue; }
      if(obj.Attributes.GetUserString(RoleKey)==ImportedJoinedRole&&!profile)
      {
        if(part.Outline.IsClosed)throw new InvalidOperationException("The imported allowance has no verified finished profile; use Part mode to rebuild it.");
        continue;
      }
      if (obj.Attributes.GetUserString(RoleKey) == "envelope")
      {
        if (!profile) throw new InvalidOperationException("The filled seam's finished sections changed; use Part mode to rebuild it.");
        if ((sameSource || recoveredUniform) && obj.Attributes.GetUserString(GeometryStampKey) != obj.Geometry.DataCRC(0).ToString("X8", CultureInfo.InvariantCulture))
          throw new InvalidOperationException("The filled seam was edited manually; use Part mode to rebuild it.");
      }
      if (obj.Attributes.GetUserString(RoleKey) == "edge")
      {
        if (!double.TryParse(obj.Attributes.GetUserString(DistanceKey), NumberStyles.Float, CultureInfo.InvariantCulture, out double distance) || !double.IsFinite(distance) || distance <= 0)
          throw new InvalidOperationException("Existing seam metadata is inconsistent; use Part mode to rebuild it.");
        bool Matches(int index) => index >= 0 && index < part.Edges.Count &&
          MatchAllowance((Curve)obj.Geometry, part.Edges[index], part, doc.ModelAbsoluteTolerance, out double measured, out _) &&
          Math.Abs(measured - distance) <= Math.Max(doc.ModelAbsoluteTolerance * ConfiguredBoundaryToleranceFactor, distance * MatchVariation);
        int edge = sameSource && int.TryParse(obj.Attributes.GetUserString(EdgeKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out int saved) && Matches(saved)
          ? saved : Enumerable.Range(0, part.Edges.Count).Where(Matches).DefaultIfEmpty(-1).First();
        if (edge < 0) continue; // Joined legacy pieces still use the conservative recognition path below.
        if (state.Curves[edge] != null) throw new InvalidOperationException("Duplicate seam objects refer to the same edge; remove the duplicate first.");
        state.Curves[edge] = ((Curve)obj.Geometry).DuplicateCurve(); state.Distances[edge] = distance;
      }
      // Owned caps/chamfers are rebuilt; their old edge indices need not survive a zero-seam source split.
      Remember(obj);
    }
    var candidates = new List<LegacyCandidate>();
    double tolerance = doc.ModelAbsoluteTolerance;
    var search = part.Outline.GetBoundingBox(true); search.Inflate(Math.Max(_settings.Allowance, Defaults.Allowance) * ConfiguredLegacySearchFactor);
    int partGroup = part.Groups.FirstOrDefault(-1);
    try
    {
      foreach (var obj in doc.Objects.GetObjectList(ObjectType.Curve))
      {
        if (obj.IsDeleted || obj.IsHidden || IsDetached(obj) || processed.Contains(obj.Id) || sourceIds.Contains(obj.Id) ||
          obj.Attributes.Name?.StartsWith("Notch", StringComparison.OrdinalIgnoreCase) == true || obj.Attributes.GetUserString("notch.object_role") != null) continue;
        bool sameGroup = partGroup >= 0 && (obj.Attributes.GetGroupList() ?? []).Contains(partGroup);
        string? owner = obj.Attributes.GetUserString(OwnerKey);
        if (owner != null && owner != part.Owner && !sameGroup) continue;
        bool cuttingLayer = IsLayer(doc, obj, DefaultLayer) || string.Equals(doc.Layers[obj.Attributes.LayerIndex].FullPath, _settings.Layer, StringComparison.OrdinalIgnoreCase);
        if (owner != part.Owner && (!cuttingLayer || !sameGroup && !BoundingBox.Intersection(search, obj.Geometry.GetBoundingBox(true)).IsValid)) continue;
        var curve = (Curve)obj.Geometry;
        if (!curve.IsInPlane(part.Plane, tolerance)) continue;
        var pieces = curve.DuplicateSegments();
        if (pieces == null || pieces.Length == 0) pieces = [curve.DuplicateCurve()];
        var indices = Enumerable.Repeat(-1, pieces.Length).ToArray(); var distances = new double[pieces.Length];
        for (int i = 0; i < pieces.Length; i++)
        {
          double bestCoverage = 0;
          for (int edge = 0; edge < part.Edges.Count; edge++)
            if (MatchAllowance(pieces[i], part.Edges[edge], part, tolerance, out double allowance, out double coverage) && coverage > bestCoverage)
            { bestCoverage = coverage; indices[i] = edge; distances[i] = allowance; }
        }
        candidates.Add(new LegacyCandidate(obj, pieces, indices, distances));
      }
      var active = candidates.ToList();
      var sourceCorners=part.Edges.Count==1&&part.Edges[0].IsClosed?[]:
        (part.Outline.IsClosed?part.Edges.Select(edge=>edge.PointAtStart):
          part.Edges.Select(edge=>edge.PointAtStart).Append(part.Outline.PointAtEnd)).ToArray();
      bool changed;
      do
      {
        var offsetEnds = active.SelectMany(candidate => candidate.Pieces.Where((_, index) => candidate.Edges[index] >= 0))
          .Concat(state.Curves.OfType<Curve>()).SelectMany(curve => new[] { curve.PointAtStart, curve.PointAtEnd }).ToArray();
        bool Near(Point3d point, IEnumerable<Point3d> values) => values.Any(value => value.DistanceTo(point) <= tolerance * ConfiguredBoundaryToleranceFactor);
        bool Connector(Curve curve) => curve.IsLinear(tolerance) &&
          (Near(curve.PointAtStart, offsetEnds) && (Near(curve.PointAtEnd, offsetEnds) || Near(curve.PointAtEnd, sourceCorners)) ||
           Near(curve.PointAtEnd, offsetEnds) && Near(curve.PointAtStart, sourceCorners));
        var valid = active.Where(candidate => candidate.Pieces.Select((piece, index) => candidate.Edges[index] >= 0 || Connector(piece)).All(value => value)).ToList();
        changed = valid.Count != active.Count; active = valid;
      } while (changed);
      foreach (var candidate in active) Remember(candidate.Object);
      if(!part.Outline.IsClosed)
        state.OpenSide=GetSourceProfile(sources)?.Side is int savedSide && savedSide is 1 or -1 ? savedSide :
          InferOpenSide(part,state.Objects.Select(entry=>doc.Objects.FindId(entry.Id)?.Geometry).OfType<Curve>(),tolerance);
      if (_settings.Type == SeamType.Edge && owned.Any(obj => !obj.IsDeleted && !processed.Contains(obj.Id)))
        throw new InvalidOperationException("Existing seam no longer follows the finished boundary. Use Part mode to rebuild it.");
      if (_settings.Type == SeamType.Part) return;
      for (int edge = 0; edge < state.Curves.Length; edge++)
      {
        var matches = active.SelectMany(candidate => candidate.Pieces.Select((piece, index) =>
          (piece, Edge: candidate.Edges[index], Distance: candidate.Distances[index]))).Where(item => item.Edge == edge).ToArray();
        if (matches.Length == 0) continue;
        if (state.Curves[edge] != null || matches.Any(item => Math.Abs(item.Distance - matches[0].Distance) > Math.Max(tolerance, matches[0].Distance * MatchVariation)))
          throw new InvalidOperationException("Overlapping or conflicting allowances exist on one edge; remove the duplicate first.");
        var joined = Curve.JoinCurves(matches.Select(item => item.piece), tolerance);
        if (joined?.Length != 1)
        { if (joined != null) foreach (var curve in joined) curve.Dispose(); throw new InvalidOperationException("Existing edge allowance is disconnected; no geometry was changed."); }
        var result = joined[0];
        if (result.TangentAt(result.Domain.Mid) * part.Edges[edge].TangentAt(part.Edges[edge].Domain.Mid) < 0) result.Reverse();
        state.Curves[edge] = result; state.Distances[edge] = matches.Average(item => item.Distance);
      }
    }
    finally { foreach (var candidate in candidates) foreach (var piece in candidate.Pieces) piece.Dispose(); }
  }

  private static bool MatchAllowance(Curve candidate, Curve source, Part part, double tolerance, out double allowance, out double coverage)
  {
    allowance = coverage = 0;
    var distances = new List<double>(); var parameters = new List<double>();
    for (int i = 1; i <= ConfiguredMatchSamples; i++)
    {
      if (!candidate.NormalizedLengthParameter((double)i / (ConfiguredMatchSamples + 1), out double t)) continue;
      var point = candidate.PointAt(t);
      if (!source.ClosestPoint(point, out double s)) continue;
      var nearest = source.PointAt(s); var delta = point - nearest;
      if (delta.Length <= tolerance || part.Outline.IsClosed&&part.Outline.Contains(point, part.Plane, tolerance) != PointContainment.Outside) continue;
      if (!source.IsClosed && (nearest.DistanceTo(source.PointAtStart) <= tolerance || nearest.DistanceTo(source.PointAtEnd) <= tolerance)) continue;
      var tangent = source.TangentAt(s); var cutTangent = candidate.TangentAt(t);
      if (!tangent.Unitize() || !cutTangent.Unitize() || Math.Abs(tangent * cutTangent) < MatchCosine) continue;
      double side=Unit(delta)*Outward(tangent,part.Plane.Normal);
      if((part.Outline.IsClosed?side:Math.Abs(side))<MatchCosine)continue;
      distances.Add(delta.Length); parameters.Add(s);
    }
    if (distances.Count < Math.Max(3, ConfiguredMatchSamples / 2)) return false;
    allowance = distances.Average();
    if (distances.Max() - distances.Min() > Math.Max(tolerance * ConfiguredBoundaryToleranceFactor, allowance * MatchVariation)) return false;
    double length = source.GetLength();
    if (length <= tolerance) return false;
    coverage = source.GetLength(new Interval(parameters.Min(), parameters.Max())) / length;
    return coverage >= MinimumMatchCoverage;
  }

  private sealed class SourceEdit(RhinoObject obj) : IDisposable
  {
    internal Guid Id { get; } = obj.Id;
    internal Curve Before { get; } = ((Curve)obj.Geometry).DuplicateCurve();
    internal ObjectAttributes Attributes { get; } = obj.Attributes.Duplicate();
    internal string Origin { get; } = obj.Attributes.GetUserString(SourceCurveKey) ?? obj.Id.ToString("D");
    internal List<Curve> Selected { get; } = [];
    internal List<Curve> Remaining { get; } = [];
    internal bool GeometryChanged { get; set; }
    internal bool AttributesChanged { get; set; }
    public void Dispose()
    { Before.Dispose(); Attributes.Dispose(); foreach (var curve in Selected.Concat(Remaining)) curve.Dispose(); }
  }

  private static bool OnTargets(Curve curve, IReadOnlyList<Curve> targets, double tolerance) =>
    Enumerable.Range(0, ConfiguredMatchSamples).All(index => targets.Any(target =>
      Distance(target, curve.PointAtNormalizedLength((double)index / (ConfiguredMatchSamples - 1))) <= tolerance * ConfiguredBoundaryToleranceFactor));

  private static void PartitionSource(SourceEdit edit, IReadOnlyList<Curve> targets, double tolerance)
  {
    var source = edit.Before;
    if (OnTargets(source, targets, tolerance)) { edit.Selected.Add(source.DuplicateCurve()); return; }
    double epsilon = Math.Abs(source.Domain.Length) * ParameterFraction;
    var parameters = targets.SelectMany(edge => new[] { edge.PointAtStart, edge.PointAtEnd })
      .Select(point => source.ClosestPoint(point, out double t) && source.PointAt(t).DistanceTo(point) <= tolerance ? t : double.NaN)
      .Where(t => double.IsFinite(t) && t > source.Domain.T0 + epsilon && t < source.Domain.T1 - epsilon &&
        source.GetLength(new Interval(source.Domain.T0, t)) > tolerance && source.GetLength(new Interval(t, source.Domain.T1)) > tolerance)
      .OrderBy(t => t).Aggregate(new List<double>(), (values, t) =>
        { if (values.Count == 0 || t - values[^1] > epsilon && source.GetLength(new Interval(values[^1], t)) > tolerance) values.Add(t); return values; });
    var pieces = TrimSections(source, parameters).ToArray();
    try
    {
      var selected = pieces.Where(piece => OnTargets(piece, targets, tolerance)).ToArray();
      if (selected.Length == 0) return;
      if (selected.Length == pieces.Length) { edit.Selected.Add(source.DuplicateCurve()); return; }
      var remaining = pieces.Except(selected).ToArray();
      var moved = Curve.JoinCurves(selected, tolerance, preserveDirection: true);
      if (moved == null || moved.Length == 0) throw new InvalidOperationException("Cannot preserve the selected finished section.");
      edit.Selected.AddRange(moved);
      var kept = Curve.JoinCurves(remaining, tolerance, preserveDirection: true);
      if (kept == null || kept.Length == 0) throw new InvalidOperationException("Cannot preserve the unselected finished sections.");
      edit.Remaining.AddRange(kept);
    }
    finally { foreach (var piece in pieces) piece.Dispose(); }
  }

  private static ObjectAttributes ConvertedAttributes(RhinoDoc doc, ObjectAttributes before, int cuttingLayer, string owner, string root)
  {
    var attributes = before.Duplicate();
    attributes.SetUserString(SourceOwnerKey, owner);
    attributes.SetUserString(SourceRootKey, root);
    if (_settings.Allowance == 0)
    {
      if (attributes.GetUserString(FinishedLayerKey) == null)
        attributes.SetUserString(FinishedLayerKey, doc.Layers[before.LayerIndex].FullPath);
      attributes.SetUserString(FinishedCutKey, "1");
      attributes.LayerIndex = cuttingLayer;
    }
    else
    {
      string previous = attributes.GetUserString(FinishedLayerKey) ?? FinishedLayerName;
      int layer = doc.Layers.FindByFullPath(previous, -1);
      if (layer < 0) layer = UzipCommon.EnsureLayer(doc, previous);
      if (layer < 0) { attributes.Dispose(); throw new InvalidOperationException("Cannot restore the finished-edge layer."); }
      attributes.LayerIndex = layer;
      attributes.SetUserString(FinishedCutKey, null);
      attributes.SetUserString(FinishedLayerKey, null);
    }
    return attributes;
  }

  private static bool Commit(RhinoDoc doc, Part part, Existing existing, Plan plan, SessionHistory? history = null,SeamOptions? options=null)
  {
    var settings=options??_settings;
    HistoryRecord? record = null;
    var added = new List<Guid>();
    var deleted = new List<uint>();
    var edits = new List<SourceEdit>();
    int newGroup = -1;
    try
    {
      if (part.Sources.Any(source => !SnapshotMatches(doc.Objects.FindId(source.Id), source.Serial, source.GeometryCrc, source.AttributesCrc)) ||
        existing.Objects.Any(source => !SnapshotMatches(doc.Objects.FindId(source.Id), source.Serial, source.GeometryCrc, source.AttributesCrc)))
        throw new InvalidOperationException("Source or allowance objects changed during preview. Pick the part again.");
      if (existing.Objects.Any(source => doc.Objects.FindId(source.Id) is not { IsLocked: false, IsReference: false, IsDeletable: true }))
        throw new InvalidOperationException("An existing allowance is locked or referenced; nothing was changed.");
      if (history != null) record = new HistoryRecord(doc, part.Sources.Select(source => source.Id).Concat(existing.Objects.Select(source => source.Id)));
      var targets = settings.Type == SeamType.Part ? part.Edges.ToArray() : part.Edges.Where((_, index) => part.Includes(index)).ToArray();
      foreach (var source in part.Sources)
      {
        var obj = doc.Objects.FindId(source.Id)!;
        if (obj.IsLocked || obj.IsReference) throw new InvalidOperationException("A finished boundary is locked or referenced; nothing was changed.");
        var edit = new SourceEdit(obj); edits.Add(edit);
        if (settings.Allowance == 0 || IsFinishedCut(obj)) PartitionSource(edit, targets, doc.ModelAbsoluteTolerance);
      }
      if (settings.Allowance == 0 && !edits.Any(edit => edit.Selected.Count > 0))
        throw new InvalidOperationException("The chosen finished section could not be detached; nothing was changed.");
      var sourceIds = part.Sources.Select(source => source.Id).ToHashSet();
      string root = edits.Select(edit => edit.Attributes.GetUserString(SourceRootKey))
        .FirstOrDefault(value => Guid.TryParse(value, out var id) && sourceIds.Contains(id)) ?? part.Sources[0].Id.ToString("D");
      int layer = LayerSelector.IsCurrentLayerValue(settings.Layer, DuplicateCommandSupport.CurrentLayerOption)
        ? doc.Layers.CurrentLayerIndex : UzipCommon.EnsureLayer(doc, settings.Layer, NewCutLayerColor);
      if (layer < 0) throw new InvalidOperationException("Cannot create the seam output layer.");
      foreach (var piece in plan.Pieces)
      {
        if (piece.Role == "edge" && piece.Allowance == 0) continue; // Exact source curves supply these cut edges, without coincident copies.
        using var attributes = new ObjectAttributes { LayerIndex = layer };
        foreach (int group in part.Groups.Where(index => doc.Groups[index] is { IsDeleted: false })) attributes.AddToGroup(group);
        SetAllowanceAttributes(attributes,piece.Curve,part.Owner,part.Stamp,piece.Role,piece.Edge,piece.Allowance);
        var id = doc.Objects.AddCurve(piece.Curve, attributes);
        if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not add the complete seam.");
        added.Add(id);
      }
      // Stage detached curves before replacing any joined source. The original survives every failed addition.
      foreach (var edit in edits.Where(edit => edit.Selected.Count > 0 && edit.Remaining.Count > 0))
      {
        using var converted = ConvertedAttributes(doc, edit.Attributes, layer, part.Owner, root);
        converted.SetUserString(SourceCurveKey, edit.Origin);
        foreach (var curve in edit.Selected)
        {
          var id = doc.Objects.AddCurve(curve, converted);
          if (id == Guid.Empty) throw new InvalidOperationException("Cannot add the detached cutting/finished section.");
          added.Add(id);
        }
        using var remainingAttributes = edit.Attributes.Duplicate();
        remainingAttributes.SetUserString(SourceOwnerKey, part.Owner);
        remainingAttributes.SetUserString(SourceRootKey, root);
        remainingAttributes.SetUserString(SourceCurveKey, edit.Origin);
        foreach (var curve in edit.Remaining.Skip(1))
        {
          var id = doc.Objects.AddCurve(curve, remainingAttributes);
          if (id == Guid.Empty) throw new InvalidOperationException("Cannot preserve the remaining joined curve.");
          added.Add(id);
        }
      }
      foreach (var edit in edits)
      {
        if (edit.Remaining.Count > 0)
        {
          if (!doc.Objects.Replace(edit.Id, edit.Remaining[0])) throw new InvalidOperationException("Cannot split the original joined curve.");
          edit.GeometryChanged = true;
        }
        using var attributes = edit.Selected.Count > 0 && edit.Remaining.Count == 0
          ? ConvertedAttributes(doc, edit.Attributes, layer, part.Owner, root) : edit.Attributes.Duplicate();
        LinkFinishedSource(attributes,part.Owner,root,edit.Origin);
        if (!doc.Objects.ModifyAttributes(edit.Id, attributes, true)) throw new InvalidOperationException("Cannot update the finished/cutting edge layer.");
        edit.AttributesChanged = true;
      }
      if (part.Groups.Length == 0 && (added.Count > 0 || edits.Any(edit => edit.Selected.Count > 0)))
      {
        newGroup = doc.Groups.Add(part.Sources.Select(source => source.Id).Concat(added));
        if (newGroup < 0) throw new InvalidOperationException("Could not group the allowance with its finished boundary.");
      }
      foreach (var previous in existing.Objects)
      {
        if (!doc.Objects.Delete(previous.Id, true)) throw new InvalidOperationException("Could not replace an existing allowance.");
        deleted.Add(previous.Serial);
      }
      if (plan.Allowances != null)
      {
        string stamp = WriteProfile(doc, part, plan.Allowances, Guid.Parse(root), plan.OffsetSide,settings);
        foreach (var id in added)
        {
          var obj = doc.Objects.FindId(id)!;
          if (obj.Attributes.GetUserString(OwnerKey) != part.Owner) continue;
          using var attributes = obj.Attributes.Duplicate(); attributes.SetUserString(StampKey, stamp);
          if (!doc.Objects.ModifyAttributes(id, attributes, true)) throw new InvalidOperationException("Cannot preserve the updated seam ownership.");
        }
      }
      Log.Write(Section, $"Applied owner={part.Owner} type={settings.Type} allowance={settings.Allowance:G17} edges={plan.AffectedEdges} added={added.Count} replaced={deleted.Count}");
      if (record != null) { record.Complete(doc, added, newGroup); history!.Push(record); record = null; }
      return true;
    }
    catch (Exception ex)
    {
      foreach (var serial in deleted) if (!doc.Objects.Undelete(serial)) Log.Write(Section, $"Rollback could not restore object serial={serial}");
      if (newGroup >= 0) doc.Groups.Delete(newGroup);
      foreach (var id in added) if (!doc.Objects.Delete(id, true)) Log.Write(Section, $"Rollback could not remove staged object={id}");
      foreach (var edit in edits.AsEnumerable().Reverse())
      {
        if (edit.GeometryChanged && !doc.Objects.Replace(edit.Id, edit.Before)) Log.Write(Section, $"Rollback could not restore finished curve={edit.Id}");
        if ((edit.GeometryChanged || edit.AttributesChanged) && !doc.Objects.ModifyAttributes(edit.Id, edit.Attributes, true))
          Log.Write(Section, $"Rollback could not restore finished attributes={edit.Id}");
      }
      Log.Write(Section, $"Apply failed: {ex}");
      RhinoApp.WriteLine($"vSeam: {ex.Message}");
      return false;
    }
    finally { record?.Dispose(); foreach (var edit in edits) edit.Dispose(); doc.Views.Redraw(); }
  }
}
