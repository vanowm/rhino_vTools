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
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultAllowance", "Seam allowance in model units; zero converts the chosen finished edges to cutting edges.")]
  private const double DefaultAllowance = 0.5; // Nonnegative seam distance in model units; zero converts source edges.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultType", "Part offsets the complete boundary; Edge offsets the picked section between kinks.")]
  private const SeamType DefaultType = SeamType.Part; // Part or Edge.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultLayer", "Output layer path or *Current*.")]
  private const string DefaultLayer = "CUT1"; // Existing or new output layer path.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.DefaultChamfer", "true clips acute convex corners at the allowance distance from the finished vertex; false keeps miters.")]
  private const bool DefaultChamfer = true; // true chamfers pointy corners; false keeps extended/trimmed miters.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.KinkAngleDegrees", "Minimum tangent change defining a selectable section, in degrees; positive.")]
  private const double KinkAngleDegrees = 1.0; // Positive G1 kink threshold in degrees.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.ChamferInteriorDegrees", "Maximum convex interior angle to chamfer, in degrees from 0 through 180; zero disables angle-based chamfering.")]
  private const double ChamferInteriorDegrees = 70; // Maximum chamfered interior angle in degrees, inclusive; zero through 180.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.ExtensionFactor", "Maximum corner-extension reach as a multiple of allowance; positive.")]
  private const double ExtensionFactor = 64; // Positive allowance multiplier for natural curve extensions.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.MatchAngleDegrees", "Maximum tangent/normal angle error when recognizing existing allowance, in degrees; positive.")]
  private const double MatchAngleDegrees = 5; // Positive legacy allowance recognition angle.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.MatchRelativeDistance", "Maximum relative variation when recognizing an existing allowance; positive and less than one.")]
  private const double MatchRelativeDistance = 0.015; // Fraction of allowance accepted as distance variation.
  private const int MatchSamples = 9; // Interior samples for recognition and boundary membership; three or greater.
  private const double MinimumMatchCoverage = 0.1; // Minimum source-edge arc-length fraction covered by an adopted allowance; zero through one.
  private const double ParameterFraction = 1e-12; // Domain-relative parameter merging/endpoint tolerance; positive.
  private const double LegacySearchFactor = 8; // Allowance multiplier for nearby ungrouped existing-CUT1 searches; positive.
  private const double BoundaryToleranceFactor = 2; // Document-tolerance multiplier for source membership; positive.
  [CommandDefault("vSeam", "vTools.Commands.vSeam.BoundaryJoinToleranceFactor", "Boundary joining tolerance as a positive multiple of document absolute tolerance; 2.1 matches Rhino's default curve joining.")]
  private const double BoundaryJoinToleranceFactor = 2.1; // Positive document-tolerance multiplier for temporary boundary joining only; source geometry and offset accuracy are unchanged.
  private static readonly PreviewDisplay.ObjectHighlightStyle GapHighlightStyle = new(System.Drawing.Color.Red, System.Drawing.Color.Black, System.Drawing.Color.Red, 1); // Red endpoint markers with black outlines for gaps beyond joining tolerance; no surface fill.
  private const CurveExtensionStyle CornerExtensionStyle = CurveExtensionStyle.Line; // Tangent-line extensions join offset ends without curling or changing existing curved sections.
  private const int BoxDragThresholdPixels = 4; // Screen-pixel movement separating a click from a selection-box drag; positive.
  private const int BoxPreviewIntervalMilliseconds = 80; // Minimum time between box-selection previews in milliseconds; nonnegative.
  private const int BoxLineThicknessPixels = 1; // Selection-box stroke width in screen pixels; positive.
  private const int PreviewStrokeEmphasis = 0; // Extra pixels above Rhino's normal curve width for the allowance preview; nonnegative.
  private const bool PickerObjectSnaps = false; // false prevents native snap highlighting of labels/whole curves; true permits object snaps during picking.
  private static readonly System.Drawing.Color BoxColor = System.Drawing.Color.DodgerBlue; // RGB color of the temporary selection rectangle.
  private const string FinishedLayerName = "PLOT"; // Preferred leaf layer for finished boundaries, without excluding other source layers.
  private static readonly System.Drawing.Color NewCutLayerColor = System.Drawing.Color.FromArgb(204, 51, 51); // RGB for newly created output layers only.
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
  private const string GeometryStampKey = "vSeam.geometryStamp";
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
  private sealed record AllowanceProfile(double[] Lengths, double[] Distances);
  private sealed class BoundaryGapException(Point3d[] endpoints) : InvalidOperationException("A closed planar finished boundary is required; red markers identify unmatched endpoints beyond the joining tolerance.")
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
    internal bool Envelope { get; set; }
    public void Dispose() { foreach (var curve in Curves) curve?.Dispose(); }
  }

  private sealed class Plan : IDisposable
  {
    internal List<Piece> Pieces { get; } = [];
    internal int AffectedEdges { get; set; }
    internal double[]? Allowances { get; set; }
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
    private readonly record struct Key(Part Part, string Edges, SeamOptions Settings);
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
      { StrokeEmphasis = PreviewStrokeEmphasis, OutlineEmphasis = PreviewStrokeEmphasis };
      _display = new PreviewDisplay.ObjectHighlighter(doc, style) { RedrawOnChange = false, DrawOnTop = true };
      _cull = new ReplacedAllowanceCull(doc);
    }
    private static Key CacheKey(Part part, int[]? edges) => new(part, string.Join(",", PreviewEdges(part, edges)), _settings);
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
    while (true)
    {
      using var selection = PickPart(doc, mode, preselect, out var result, out var historyRequest);
      preselect = false;
      if (historyRequest != null) { history.Move(doc, historyRequest.Redo); continue; }
      if (selection == null) return applied > 0 ? Result.Success : result;
      foreach (var part in selection.Parts)
      {
        try
        {
          using var prepared = selection.Previews.Remove(part, out var previewed) ? previewed : PrepareSeam(doc, part);
          if (prepared.Settings != _settings) throw new InvalidOperationException("Seam options changed during preview. Pick the part again.");
          part.SelectedEdges = _settings.Type == SeamType.Edge ? prepared.Edges.ToHashSet() : null;
          var plan = prepared.Plan;
          if (Commit(doc, part, prepared.Existing, plan, history))
          {
            applied++;
            RhinoApp.WriteLine(_settings.Allowance == 0
              ? "vSeam: converted {0} finished edge(s) to cutting edges."
              : "vSeam: Seam={1:G6} applied to {0} edge(s); finished geometry retained.", plan.AffectedEdges, _settings.Allowance);
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
  {
    var type = ToolsOptionStore.TryGetString(section, "type", out var name) && Enum.TryParse<SeamType>(name, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : Defaults.Type;
    double legacy = ReadDistance("allowance", Defaults.Allowance);
    _partAllowance = ReadDistance("partSeam", type == SeamType.Part ? legacy : Defaults.Allowance);
    _edgeAllowance = ReadDistance("edgeSeam", type == SeamType.Edge ? legacy : Defaults.Allowance);
    return new SeamOptions(type == SeamType.Part ? _partAllowance : _edgeAllowance, type,
      ToolsOptionStore.TryGetString(section, "layer", out var layer) ? layer : Defaults.Layer,
      ToolsOptionStore.TryGetBool(section, "chamfer", out var chamfer) ? chamfer : Defaults.Chamfer)
      { ChamferAngle = ToolsOptionStore.TryGetDouble(section, "chamferAngle", out var angle) && double.IsFinite(angle) && angle >= 0 && angle <= 180 ? angle : Defaults.ChamferAngle };
    double ReadDistance(string key, double fallback) => ToolsOptionStore.TryGetDouble(section, key, out var value) && double.IsFinite(value) && value >= 0 ? value : fallback;
  }

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
      for (int i = 0; i < count; i++)
      {
        if (settings.Type == SeamType.Edge && !part.Includes(i))
        {
          if (cuts[i] == null && distances[i] > 0) cuts[i] = OffsetEdge(part.Edges[i], part.Plane, distances[i], tolerance);
          continue;
        }
        var replacement = settings.Allowance == 0 ? part.Edges[i].DuplicateCurve() : OffsetEdge(part.Edges[i], part.Plane, settings.Allowance, tolerance);
        cuts[i]?.Dispose(); cuts[i] = replacement; distances[i] = settings.Allowance;
        plan.AffectedEdges++;
      }
      if (!(count == 1 && part.Edges[0].IsClosed))
        for (int i = 0; i < count; i++)
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
          var chamfer = settings.Chamfer && IsPointy(incoming, outgoing, part.Plane.Normal, settings.ChamferAngle);
          if (chamfer)
          {
            var direction = Unit(Outward(incoming, part.Plane.Normal) + Outward(outgoing, part.Plane.Normal));
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
      for (int i = 0; i < count; i++)
        if (cuts[i] != null)
        { plan.Pieces.Add(new Piece(cuts[i]!, "edge", i, distances[i])); cuts[i] = null; }
      ValidatePlan(part, plan, tolerance);
      if (existing.Envelope) plan.Allowances = distances.ToArray();
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

  private static Curve OffsetEdge(Curve edge, Plane plane, double distance, double tolerance)
  {
    if (!edge.NormalizedLengthParameter(0.5, out var t)) throw new InvalidOperationException("Cannot measure a finished edge.");
    var directionPoint = edge.PointAt(t) + Outward(edge.TangentAt(t), plane.Normal) * distance;
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
      hit.PointA.DistanceTo(vertex) <= reach && (allowInterior || part.Outline.Contains(hit.PointA, part.Plane, tolerance) != PointContainment.Inside))
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

  private static PartSelection? PickPart(RhinoDoc doc, RunMode mode, bool preselect, out Result result, out HistoryRequest? historyRequest)
  {
    historyRequest = null;
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
        if (preview.Show(targets) || changed) doc.Views.Redraw();
      }
      Rhino.Display.RhinoViewport? clickedViewport = null;
      System.Drawing.Point clickedPoint = default;
      System.Drawing.Point boxEnd = default;
      bool dragging = false;
      long previewTick = 0;
      getter.SetCommandPrompt(_settings.Type == SeamType.Part ? "Click inside a finished part or on its boundary. Enter when done" : "Select finished edge between kinks. Enter when done");
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
          dragging |= Math.Abs(boxEnd.X - clickedPoint.X) >= BoxDragThresholdPixels || Math.Abs(boxEnd.Y - clickedPoint.Y) >= BoxDragThresholdPixels;
          if (dragging)
          {
            if (System.Environment.TickCount64 - previewTick >= BoxPreviewIntervalMilliseconds)
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
          e.Display.Draw2dRectangle(SelectionRectangle(clickedPoint, boxEnd), BoxColor, BoxLineThicknessPixels, System.Drawing.Color.Transparent);
      };
      var controls = new Controls(getter);
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
    var pool=objects.Where(obj=>obj.Geometry is Curve&&!IsLabelObject(obj)&&obj.Attributes.GetUserString(OwnerKey)==null).OrderBy(obj=>obj.Id).ToArray();
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
    var selected = (Curve)picked.Geometry;
    var candidates = new List<RhinoObject>();
    RhinoObject[] incomplete = [];
    Curve? outline = null;
    // A directly picked closed finished curve has an unambiguous identity, even without grouping.
    bool grouped = (picked.Attributes.GetGroupList() ?? []).Any(index => doc.Groups[index] is { IsDeleted: false });
    if (!grouped && selected.IsClosed && (IsFinishedCut(picked) || !IsLayer(doc, picked, DefaultLayer))) { candidates.Add(picked); outline = selected.DuplicateCurve(); }
    else
    {
      var groups = (picked.Attributes.GetGroupList() ?? []).Where(index => doc.Groups[index] is { IsDeleted: false })
        .OrderBy(doc.Groups.GroupObjectCount).ToArray();
      foreach (int group in groups)
      {
        var members = (doc.Groups.GroupMembers(group) ?? []).Where(obj => obj.Geometry is Curve && !IsLabelObject(obj) && obj.Attributes.GetUserString(OwnerKey) == null).ToArray();
        var plot = members.Where(obj => IsLayer(doc, obj, FinishedLayerName) || IsFinishedCut(obj)).ToArray();
        var pool = plot.Length > 0 ? plot : members.Where(obj => !IsLayer(doc, obj, DefaultLayer)).ToArray();
        if (incomplete.Length == 0) incomplete = pool;
        outline = FindOutline(pool, selected, pickPoint, tolerance, true);
        if (outline != null) { candidates.AddRange(pool); break; }
        // A partial preferred-layer set is not a complete part. Add only end-connected source edges, never a detached reference offset.
        var connected=ConnectedSources(members.Where(obj=>IsFinishedCut(obj)||!IsLayer(doc,obj,DefaultLayer)),picked,pickPoint,tolerance);
        outline=FindOutline(connected,selected,pickPoint,tolerance,true);
        if(outline!=null){candidates.AddRange(connected);break;}
      }
      if (outline == null)
      {
        var visible=doc.Objects.GetObjectList(ObjectType.Curve).Where(obj=>!obj.IsDeleted&&!obj.IsHidden&&
          !IsLabelObject(obj)&&obj.Attributes.GetUserString(OwnerKey)==null).ToArray();
        var pools=new[]{selectedSources.ToArray(),visible.Where(obj=>obj.Attributes.LayerIndex==picked.Attributes.LayerIndex).ToArray(),
          visible.Where(obj=>IsFinishedCut(obj)||!IsLayer(doc,obj,DefaultLayer)).ToArray()};
        foreach(var pool in pools)
        {
          if(pool.Length==0)continue;
          var connected=ConnectedSources(pool,picked,pickPoint,tolerance);
          if (incomplete.Length == 0) incomplete = connected;
          outline=FindOutline(connected,selected,pickPoint,tolerance,false);
          if(outline==null)continue;
          candidates.AddRange(connected);break;
        }
      }
    }
    if (outline == null)
    {
      var gaps = BoundaryGaps(incomplete.Length > 0 ? incomplete : [picked], tolerance);
      if (gaps.Length > 0 && (_settings.Allowance != 0 || incomplete.Length > 1)) throw new BoundaryGapException(gaps);
    }
    if (outline == null && _settings.Allowance == 0) { candidates.Clear(); candidates.Add(picked); outline = selected.DuplicateCurve(); }
    if (outline == null) throw new InvalidOperationException("A closed planar finished boundary is required to determine the outside.");
    return CaptureOutline(doc, outline, candidates, pickPoint);
  }

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

  private static Part CaptureOutline(RhinoDoc doc, Curve outline, List<RhinoObject> candidates, Point3d pickPoint)
  {
    double tolerance = doc.ModelAbsoluteTolerance;
    try
    {
      if (!outline.TryGetPlane(out var fitted, tolerance))
      {
        fitted = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
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
      var self = Intersection.CurveSelf(outline, tolerance);
      if (self != null && self.Any(hit => !ClosureContact(outline, hit, tolerance)))
        throw new InvalidOperationException("The finished boundary intersects itself.");
      var edges = SplitEdges(outline, candidates.Select(obj => (Curve)obj.Geometry), tolerance *
        (double.IsFinite(JoinToleranceFactor) && JoinToleranceFactor > 0 ? JoinToleranceFactor : BoundaryJoinToleranceFactor));
      var boundaryObjects = candidates.Where(obj => OnBoundary((Curve)obj.Geometry, outline, tolerance) ||
        edges.Any(edge => OnBoundary(edge, (Curve)obj.Geometry, tolerance))).OrderBy(obj => obj.Id).ToArray();
      if (boundaryObjects.Length == 0) throw new InvalidOperationException("No source curves could be assigned to the finished boundary.");
      var ids = boundaryObjects.Select(obj => obj.Id).ToArray();
      var savedOwners = boundaryObjects.Where(obj => Guid.TryParse(obj.Attributes.GetUserString(SourceRootKey), out var root) && ids.Contains(root))
        .Select(obj => obj.Attributes.GetUserString(SourceOwnerKey)).Where(value => !string.IsNullOrEmpty(value)).Distinct().ToArray();
      string owner = savedOwners.Length == 1 ? savedOwners[0]! : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", ids.Select(id => id.ToString("N"))))));
      string stamp = string.Join(";", boundaryObjects.Select(obj => obj.Id.ToString("N") + ":" + obj.Geometry.DataCRC(0).ToString("X8", CultureInfo.InvariantCulture)));
      var common = (boundaryObjects[0].Attributes.GetGroupList() ?? []).Where(index => doc.Groups[index] is { IsDeleted: false } &&
        boundaryObjects.All(obj => (obj.Attributes.GetGroupList() ?? []).Contains(index))).OrderBy(doc.Groups.GroupObjectCount).ToArray();
      var sources = boundaryObjects.Select(obj => new SourceObject(obj.Id, obj.RuntimeSerialNumber, obj.Geometry.DataCRC(0), AttributeStamp(obj.Attributes))).ToArray();
      return new Part(outline, plane, edges, sources, common, owner, stamp, pickPoint);
    }
    catch { outline.Dispose(); throw; }
  }

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
        (OnBoundary(selected, curve, tolerance) || pickPoint.IsValid && Distance(curve, pickPoint) <= tolerance * BoundaryToleranceFactor ||
          allowContaining && curve.TryGetPlane(out var plane, tolerance) &&
          curve.Contains(pickPoint.IsValid ? pickPoint : selected.PointAtNormalizedLength(0.5), plane, tolerance) != PointContainment.Outside));
      var best = choices.OrderByDescending(curve => { using var mass = AreaMassProperties.Compute(curve); return mass?.Area ?? 0; }).FirstOrDefault();
      if (best == null && allowContaining && pickPoint.IsValid)
        best = eligible.OrderBy(curve => Distance(curve, pickPoint)).FirstOrDefault();
      return best?.DuplicateCurve();
    }
    finally { foreach (var curve in curves.Concat(joined).Concat(loops).Distinct()) curve.Dispose(); }
  }

  private static bool OnBoundary(Curve curve, Curve outline, double tolerance) => Enumerable.Range(0, MatchSamples).All(index =>
    Distance(outline, curve.PointAtNormalizedLength((double)index / (MatchSamples - 1))) <= tolerance * BoundaryToleranceFactor);

  private static List<Curve> SplitEdges(Curve outline, IEnumerable<Curve> sources, double tolerance)
  {
    var parameters = new List<double>();
    var domain = outline.Domain;
    double epsilon = Math.Abs(domain.Length) * ParameterFraction;
    double cursor = domain.T0;
    while (outline.GetNextDiscontinuity(Continuity.G1_continuous, cursor,
      cosAngleTolerance: KinkCosine, curvatureTolerance: 0, t1: domain.T1, t: out double next))
    { if (next <= cursor || next >= domain.T1 - epsilon) break; parameters.Add(next); cursor = Math.Min(next + epsilon, domain.T1); }
    // Separate source objects remain individually addressable even at a tangent-continuous join.
    foreach (var source in sources.Where(curve => !curve.IsClosed))
      foreach (var point in new[] { source.PointAtStart, source.PointAtEnd })
        if (outline.ClosestPoint(point, out double t) && outline.PointAt(t).DistanceTo(point) <= tolerance && t > domain.T0 + epsilon && t < domain.T1 - epsilon)
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
    bool sourceJoin = sources.Where(curve => !curve.IsClosed).Any(curve =>
      curve.PointAtStart.DistanceTo(outline.PointAtStart) <= tolerance || curve.PointAtEnd.DistanceTo(outline.PointAtStart) <= tolerance);
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

  private static bool ReadProfile(RhinoDoc doc, Part part, double[] distances, out bool recoveredUniform)
  {
    recoveredUniform = false;
    var sources = part.Sources.Select(source => doc.Objects.FindId(source.Id)).OfType<RhinoObject>().ToArray();
    var roots = sources.Where(obj => obj.Attributes.GetUserString(ProfileRootKey) == "1").ToArray();
    if (roots.Length != 1) return false;
    try
    {
      var profile = System.Text.Json.JsonSerializer.Deserialize<AllowanceProfile>(roots[0].Attributes.GetUserString(ProfileKey) ?? "null");
      if (profile?.Lengths == null || profile.Distances == null || profile.Lengths.Length == 0 || profile.Lengths.Length != profile.Distances.Length ||
        profile.Lengths.Any(value => !double.IsFinite(value) || value <= 0) ||
        profile.Distances.Any(value => !double.IsFinite(value) || value < 0)) return false;
      int anchor = ProfileAnchor(part, roots[0]);
      bool matches = profile.Lengths.Length == part.Edges.Count && Enumerable.Range(0, part.Edges.Count).All(i =>
        Math.Abs(profile.Lengths[i] - part.Edges[(i + anchor) % part.Edges.Count].GetLength()) <= doc.ModelAbsoluteTolerance * BoundaryToleranceFactor);
      if (!matches)
      {
        // A uniform assignment survives section splits/merges without guessing which edge had which allowance.
        if (profile.Distances.Any(value => value != profile.Distances[0])) return false;
        Array.Fill(distances, profile.Distances[0]);
        recoveredUniform = true;
        Log.Write(Section, $"Recovered uniform seam profile owner={part.Owner} allowance={profile.Distances[0]:G17} savedSections={profile.Lengths.Length} currentSections={part.Edges.Count}");
        return true;
      }
      for (int i = 0; i < part.Edges.Count; i++) distances[(i + anchor) % part.Edges.Count] = profile.Distances[i];
      return true;
    }
    catch (System.Text.Json.JsonException ex) { Log.Write(Section, $"Invalid allowance profile: {ex.Message}"); return false; }
  }

  private static string WriteProfile(RhinoDoc doc, Part original, double[] distances, Guid rootId)
  {
    var root = doc.Objects.FindId(rootId) ?? throw new InvalidOperationException("The allowance-profile anchor disappeared.");
    var sources = doc.Objects.GetObjectList(ObjectType.Curve).Where(obj => obj.Attributes.GetUserString(OwnerKey) == null &&
      obj.Attributes.GetUserString(SourceOwnerKey) == original.Owner &&
      string.Equals(obj.Attributes.GetUserString(SourceRootKey), rootId.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
      (original.Groups.Length == 0 || (obj.Attributes.GetGroupList() ?? []).Contains(original.Groups[0]))).ToList();
    // Source splitting must not let the bookkeeping root select a different region from the one previewed.
    using var current = CaptureOutline(doc, original.Outline.DuplicateCurve(), sources, original.PickPoint);
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
    int anchor = ProfileAnchor(current, root);
    var indices = Enumerable.Range(0, current.Edges.Count).Select(i => (i + anchor) % current.Edges.Count).ToArray();
    string profile = System.Text.Json.JsonSerializer.Serialize(new AllowanceProfile(indices.Select(i => current.Edges[i].GetLength()).ToArray(), indices.Select(i => mapped[i]).ToArray()));
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
      .Where(obj => part.Groups.Length == 0 || (obj.Attributes.GetGroupList() ?? []).Contains(part.Groups[0])).ToList();
    if (_settings.Type == SeamType.Part)
    {
      // Zero-seam detachment can retire an earlier owner. Recover its entire output from source lineage, not offset shape.
      var scope = part.Groups.Length > 0 ? doc.Groups.GroupMembers(part.Groups[0]) ?? [] : doc.Objects.GetObjectList(ObjectType.Curve).ToArray();
      var lineage = sourceIds.Concat(sources.Select(source => Guid.TryParse(source.Attributes.GetUserString(SourceCurveKey), out var id) ? id : Guid.Empty))
        .Where(id => id != Guid.Empty).ToHashSet();
      var foreignLineage = scope.Where(obj => !obj.IsDeleted && !sourceIds.Contains(obj.Id) && obj.Attributes.GetUserString(OwnerKey) == null)
        .Select(obj => Guid.TryParse(obj.Attributes.GetUserString(SourceCurveKey), out var id) ? id : Guid.Empty).ToHashSet();
      foreach (var obj in scope.Where(obj => !obj.IsDeleted && obj.Geometry is Curve && obj.Attributes.GetUserString(OwnerKey) != null && owned.All(known => known.Id != obj.Id)))
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
    state.Envelope = profile;
    var processed = new HashSet<Guid>();
    void Remember(RhinoObject obj)
    {
      if (processed.Add(obj.Id)) state.Objects.Add(new ExistingObject(obj.Id, obj.RuntimeSerialNumber, obj.Geometry.DataCRC(0), AttributeStamp(obj.Attributes)));
    }
    foreach (var obj in owned.Where(obj => !obj.IsDeleted))
    {
      bool sameSource = obj.Attributes.GetUserString(StampKey) == part.Stamp && obj.Attributes.GetUserString(VersionKey) == SchemaVersion;
      if (_settings.Type == SeamType.Part) { Remember(obj); continue; }
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
          Math.Abs(measured - distance) <= Math.Max(doc.ModelAbsoluteTolerance * BoundaryToleranceFactor, distance * MatchVariation);
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
    var search = part.Outline.GetBoundingBox(true); search.Inflate(Math.Max(_settings.Allowance, Defaults.Allowance) * LegacySearchFactor);
    int partGroup = part.Groups.FirstOrDefault(-1);
    try
    {
      foreach (var obj in doc.Objects.GetObjectList(ObjectType.Curve))
      {
        if (obj.IsDeleted || obj.IsHidden || processed.Contains(obj.Id) || sourceIds.Contains(obj.Id) ||
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
      var sourceCorners = part.Edges.Count == 1 && part.Edges[0].IsClosed ? [] : part.Edges.Select(edge => edge.PointAtStart).ToArray();
      bool changed;
      do
      {
        var offsetEnds = active.SelectMany(candidate => candidate.Pieces.Where((_, index) => candidate.Edges[index] >= 0))
          .Concat(state.Curves.OfType<Curve>()).SelectMany(curve => new[] { curve.PointAtStart, curve.PointAtEnd }).ToArray();
        bool Near(Point3d point, IEnumerable<Point3d> values) => values.Any(value => value.DistanceTo(point) <= tolerance * BoundaryToleranceFactor);
        bool Connector(Curve curve) => curve.IsLinear(tolerance) &&
          (Near(curve.PointAtStart, offsetEnds) && (Near(curve.PointAtEnd, offsetEnds) || Near(curve.PointAtEnd, sourceCorners)) ||
           Near(curve.PointAtEnd, offsetEnds) && Near(curve.PointAtStart, sourceCorners));
        var valid = active.Where(candidate => candidate.Pieces.Select((piece, index) => candidate.Edges[index] >= 0 || Connector(piece)).All(value => value)).ToList();
        changed = valid.Count != active.Count; active = valid;
      } while (changed);
      foreach (var candidate in active) Remember(candidate.Object);
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
    for (int i = 1; i <= MatchSamples; i++)
    {
      if (!candidate.NormalizedLengthParameter((double)i / (MatchSamples + 1), out double t)) continue;
      var point = candidate.PointAt(t);
      if (!source.ClosestPoint(point, out double s)) continue;
      var nearest = source.PointAt(s); var delta = point - nearest;
      if (delta.Length <= tolerance || part.Outline.Contains(point, part.Plane, tolerance) != PointContainment.Outside) continue;
      if (!source.IsClosed && (nearest.DistanceTo(source.PointAtStart) <= tolerance || nearest.DistanceTo(source.PointAtEnd) <= tolerance)) continue;
      var tangent = source.TangentAt(s); var cutTangent = candidate.TangentAt(t);
      if (!tangent.Unitize() || !cutTangent.Unitize() || Math.Abs(tangent * cutTangent) < MatchCosine ||
        Unit(delta) * Outward(tangent, part.Plane.Normal) < MatchCosine) continue;
      distances.Add(delta.Length); parameters.Add(s);
    }
    if (distances.Count < Math.Max(3, MatchSamples / 2)) return false;
    allowance = distances.Average();
    if (distances.Max() - distances.Min() > Math.Max(tolerance * BoundaryToleranceFactor, allowance * MatchVariation)) return false;
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
    Enumerable.Range(0, MatchSamples).All(index => targets.Any(target =>
      Distance(target, curve.PointAtNormalizedLength((double)index / (MatchSamples - 1))) <= tolerance * BoundaryToleranceFactor));

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

  private static bool Commit(RhinoDoc doc, Part part, Existing existing, Plan plan, SessionHistory? history = null)
  {
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
      var targets = _settings.Type == SeamType.Part ? part.Edges.ToArray() : part.Edges.Where((_, index) => part.Includes(index)).ToArray();
      foreach (var source in part.Sources)
      {
        var obj = doc.Objects.FindId(source.Id)!;
        if (obj.IsLocked || obj.IsReference) throw new InvalidOperationException("A finished boundary is locked or referenced; nothing was changed.");
        var edit = new SourceEdit(obj); edits.Add(edit);
        if (_settings.Allowance == 0 || IsFinishedCut(obj)) PartitionSource(edit, targets, doc.ModelAbsoluteTolerance);
      }
      if (_settings.Allowance == 0 && !edits.Any(edit => edit.Selected.Count > 0))
        throw new InvalidOperationException("The chosen finished section could not be detached; nothing was changed.");
      var sourceIds = part.Sources.Select(source => source.Id).ToHashSet();
      string root = edits.Select(edit => edit.Attributes.GetUserString(SourceRootKey))
        .FirstOrDefault(value => Guid.TryParse(value, out var id) && sourceIds.Contains(id)) ?? part.Sources[0].Id.ToString("D");
      int layer = LayerSelector.IsCurrentLayerValue(_settings.Layer, DuplicateCommandSupport.CurrentLayerOption)
        ? doc.Layers.CurrentLayerIndex : UzipCommon.EnsureLayer(doc, _settings.Layer, NewCutLayerColor);
      if (layer < 0) throw new InvalidOperationException("Cannot create the seam output layer.");
      foreach (var piece in plan.Pieces)
      {
        if (piece.Role == "edge" && piece.Allowance == 0) continue; // Exact source curves supply these cut edges, without coincident copies.
        using var attributes = new ObjectAttributes { LayerIndex = layer, ColorSource = ObjectColorSource.ColorFromLayer, Name = "SeamAllowance" };
        foreach (int group in part.Groups.Where(index => doc.Groups[index] is { IsDeleted: false })) attributes.AddToGroup(group);
        attributes.SetUserString(OwnerKey, part.Owner); attributes.SetUserString(StampKey, part.Stamp);
        attributes.SetUserString(VersionKey, SchemaVersion); attributes.SetUserString(RoleKey, piece.Role);
        attributes.SetUserString(EdgeKey, piece.Edge.ToString(CultureInfo.InvariantCulture));
        attributes.SetUserString(DistanceKey, piece.Allowance.ToString("R", CultureInfo.InvariantCulture));
        if (piece.Role == "envelope") attributes.SetUserString(GeometryStampKey, piece.Curve.DataCRC(0).ToString("X8", CultureInfo.InvariantCulture));
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
        attributes.SetUserString(SourceOwnerKey, part.Owner);
        attributes.SetUserString(SourceRootKey, root);
        attributes.SetUserString(SourceCurveKey, edit.Origin);
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
        string stamp = WriteProfile(doc, part, plan.Allowances, Guid.Parse(root));
        foreach (var id in added)
        {
          var obj = doc.Objects.FindId(id)!;
          if (obj.Attributes.GetUserString(OwnerKey) != part.Owner) continue;
          using var attributes = obj.Attributes.Duplicate(); attributes.SetUserString(StampKey, stamp);
          if (!doc.Objects.ModifyAttributes(id, attributes, true)) throw new InvalidOperationException("Cannot preserve the updated seam ownership.");
        }
      }
      Log.Write(Section, $"Applied owner={part.Owner} type={_settings.Type} allowance={_settings.Allowance:G17} edges={plan.AffectedEdges} added={added.Count} replaced={deleted.Count}");
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
