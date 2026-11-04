namespace vTools.Commands
{
  using Rhino;
  using Rhino.Commands;


  /// <summary>Splits a planar fabrication part into separately grouped, width-limited copies.</summary>
  public sealed class vPartSplit : vToolsCommand
  {
    public override string EnglishName => "vPartSplit";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => PartSplitWorkflow.Run(doc);
  }
}

namespace vTools.Commands
{
  using System.Drawing;
  using Rhino;
  using Rhino.Commands;
  using Rhino.Display;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using Rhino.Geometry.Intersect;
  using Rhino.Input;
  using Rhino.Input.Custom;
  using Part = vTools.PlanarPartSelection.Part;
  using Item = vTools.PlanarPartSelection.Item;
  using SplitDirection = vTools.PlanarPartSplitter.SplitDirection;


  internal static class PartSplitWorkflow
  {
    // Option defaults and customizable interaction/output settings
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultWidth", "Material width in model units; must exceed twice Seam.", OptionKeys = "width")] internal const double DefaultWidth = 60.0; // Material width in model units; must exceed twice Seam.

    internal static readonly double ConfiguredDefaultWidth = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultWidth", DefaultWidth); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultSeam", "Extra model-unit allowance on each part at each common cut; zero disables overlap.", OptionKeys = "seam")] internal const double DefaultSeam = 0.5; // Extra model-unit allowance on each part at each common cut; zero disables overlap.

    internal static readonly double ConfiguredDefaultSeam = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultSeam", DefaultSeam); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultMode", "Mode index: 0 balances physical piece lengths; 1 fills each material width before the final remainder.", OptionKeys = "mode")] internal const int DefaultMode = 0; // Mode index: 0 balances physical piece lengths; 1 fills material width before the final remainder.

    internal static readonly int ConfiguredDefaultMode = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultMode", DefaultMode); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultLayout", "Layout index: 0 preserves original alignment; 1 aligns rotated copies side by side.", OptionKeys = "layout")] private const int DefaultLayout = 1; // Layout index: 0 preserves original alignment; 1 aligns rotated copies side by side.

    private static readonly int ConfiguredDefaultLayout = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultLayout", DefaultLayout); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultDistance", "Clear model-unit distance between neighboring final cut edges; zero permits touching edges.", OptionKeys = "distance")] private const double DefaultDistance = 1.0; // Clear model-unit distance between neighboring final cut edges; zero permits touching edges.

    private static readonly double ConfiguredDefaultDistance = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultDistance", DefaultDistance); // Resolved once from advanced defaults; restart Rhino after changes.
    private const int OriginalLayoutIndex = 0; // Fixed Original index in LayoutNames, independent of the default.
    [global::vTools.CommandChoicesAttribute("vPartSplit", "layout", true, "Original", "Vertical")] private static readonly string[] LayoutNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.PartSplitWorkflow), nameof(LayoutNames)); // Available copy-arrangement modes.
    private const int EvenModeIndex = 0; // Fixed index of Even in ModeNames, independent of the configurable default mode.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultDirection", "Start/End fills from one end; Inward fills from outer ends; Outward fills from the center.", OptionKeys = "direction")] private const SplitDirection DefaultDirection = SplitDirection.Start; // Start/End fills from one end; Inward fills from outer ends; Outward fills from the center.

    private static readonly SplitDirection ConfiguredDefaultDirection = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultDirection", DefaultDirection); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandChoicesAttribute("vPartSplit", "direction", false, "Start", "End", "Inward", "Outward")] private static readonly string[] DirectionNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.PartSplitWorkflow), nameof(DirectionNames)); // Per-part cut-placement and copy-order choices.
    [global::vTools.CommandChoicesAttribute("vPartSplit", "mode", true, "Even", "MaxWidth")] private static readonly string[] ModeNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.PartSplitWorkflow), nameof(ModeNames)); // Command-line values for the two split-spacing modes.
    private const double InitialLayoutGap = PlanarPartSplitter.DefaultGap; // Minimum model-unit separation used for the initial preview anchor.
    private const double PickRadius = 14.0; // Maximum cursor distance in screen pixels for picking a cut.
    private const double EndpointPickRadius = 18.0; // Screen-pixel distance from an endpoint that chooses pivoting rather than sliding a cut.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DragRefreshMilliseconds", "Minimum interval between clipped-preview updates while dragging.")] private const int DragRefreshMilliseconds = 30; // Minimum milliseconds between incremental panel rebuilds; zero updates every mouse event.

    private static readonly int ConfiguredDragRefreshMilliseconds = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DragRefreshMilliseconds", DragRefreshMilliseconds); // Resolved once from advanced defaults; restart Rhino after changes.
    private const bool AllowObjectSnaps = true; // true resolves native object snaps and displays snap cues while dragging cuts; false uses unsnapped points.
    private const int NormalAlignmentPasses = 6; // Maximum tangent-bisector updates for each automatic cross-cut; positive integer.
    private const double MinimumProgressCosine = 0.1; // Minimum absolute dot product of a local cut normal with the part's overall progression; zero through one.
    private const double LocalWidthReduction = 0.9; // Effective-width multiplier when a MaxWidth cross-cut panel is too wide; greater than zero and less than one.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.CoverageToleranceFactor", "Model-tolerance times source perimeter accepted as nominal clipping-area error; positive multiplier.")] private const double CoverageToleranceFactor = 2.0; // Model-tolerance times source perimeter accepted as nominal clipping-area error; positive multiplier.

    private static readonly double ConfiguredCoverageToleranceFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.CoverageToleranceFactor", CoverageToleranceFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.PreviewAlpha", "Preview stroke opacity, zero invisible through 255 opaque.")] private const int PreviewAlpha = 200; // Preview stroke opacity, zero invisible through 255 opaque.

    private static readonly int ConfiguredPreviewAlpha = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.PreviewAlpha", PreviewAlpha); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.PreviewPointSize", "Preview point-marker diameter in screen pixels; positive integer.")] private const int PreviewPointSize = 3; // Preview point-marker diameter in screen pixels; positive integer.

    private static readonly int ConfiguredPreviewPointSize = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.PreviewPointSize", PreviewPointSize); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.BoundarySamples", "Number of samples for distinguishing original boundary curves from interior details; three or greater.")] private const int BoundarySamples = 17; // Number of samples for distinguishing original boundary curves from interior details; three or greater.

    private static readonly int ConfiguredBoundarySamples = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.BoundarySamples", BoundarySamples); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.BoundaryToleranceFactor", "Model-tolerance multiplier for suppressing duplicate boundary strokes.")] private const double BoundaryToleranceFactor = 2.0; // Model-tolerance multiplier for suppressing duplicate boundary strokes.

    private static readonly double ConfiguredBoundaryToleranceFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.BoundaryToleranceFactor", BoundaryToleranceFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.InitialLayoutGapFactor", "Multiplier of the initial preview gap between the source and copied layout.")] private const double InitialLayoutGapFactor = 4.0; // Multiplier of the initial preview gap between the source and copied layout.

    private static readonly double ConfiguredInitialLayoutGapFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.InitialLayoutGapFactor", InitialLayoutGapFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.CutPreviewColor", "RGB color for nominal source cut positions, without seam allowance.", "#FFFFA500")] private static readonly Color CutPreviewColor = global::vTools.CommandDefaults.GetColor("vPartSplit", "vTools.Commands.PartSplitWorkflow.CutPreviewColor", Color.Orange); // RGB color for nominal source cut positions, without seam allowance.
    private const string ReferenceLayer = "Reference"; // Existing or new Rhino layer for nominal split cuts on the original part.
    private const string BoundaryName = "PartSplitBoundary"; // Object name for each new part's closed cutting perimeter.
    private const string CutName = "PartSplitCut"; // Object name for a nominal Reference-layer cut on the original part.
    private const string SewName = "PartSplitSewLine"; // Object name for nominal split lines included inside each output part.
    private const string SewLayer = "PLOT"; // Existing or new Rhino layer for the split parts' sew lines.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.MinimumFinishedAreaRatio", "Minimum finished-loop/cut-loop area ratio for untagged PLOT loops; rejects small internal holes as a finished perimeter.")] private const double MinimumFinishedAreaRatio = 0.5; // Minimum finished-loop/cut-loop area ratio for untagged PLOT loops; rejects small internal holes as a finished perimeter.

    private static readonly double ConfiguredMinimumFinishedAreaRatio = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.MinimumFinishedAreaRatio", MinimumFinishedAreaRatio); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultLabel", "true adds matching seam labels using vNotches settings; false omits new seam labels.", OptionKeys = "label")]
    private const bool DefaultLabel = false; // true labels mating seams; false retains the unlabeled split workflow.
    private static readonly bool ConfiguredDefaultLabel = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultLabel", DefaultLabel); // Resolved once from advanced defaults.
    private const string SeamLabelName = "PartSplitSeamLabel"; // Object name identifying new matching seam annotations.
    private const string ReferenceLabelName = "PartSplitReferenceLabel"; // Object name for opposing seam annotations on the retained source part's Reference layer.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.AllowanceLabelPositionFactor", "Fraction of Seam from the nominal split toward the allowance cut edge; zero through one.")] private const double AllowanceLabelPositionFactor = 0.5; // Fraction of Seam from the nominal split toward the allowance cut edge; zero through one.

    private static readonly double ConfiguredAllowanceLabelPositionFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.AllowanceLabelPositionFactor", AllowanceLabelPositionFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.LabelInsetHeightFactor", "Positive text-height inset into the finished panel only when Seam is zero.")] private const double LabelInsetHeightFactor = 1.0; // Positive text-height inset into the finished panel only when Seam is zero.

    private static readonly double ConfiguredLabelInsetHeightFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.LabelInsetHeightFactor", LabelInsetHeightFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    private const int LabelInsetAttempts = 8; // Maximum offset halvings when a narrow panel limits label placement.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultSewColor", "RGB sew-line color when PLOT must be created.", "#FF0F8A8A")] private static readonly Color DefaultSewColor = global::vTools.CommandDefaults.GetColor("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultSewColor", Color.FromArgb(15,138,138)); // RGB sew-line color when PLOT must be created.
    private const string GroupPrefix = "vPartSplit_"; // Group-name prefix followed by a session identifier and one-based part number.
    private const string MetadataPrefix = "vPartSplit."; // User-string key prefix identifying source, session, part, width, and seam allowance.
    private const string NotchName = "Notch"; // Source object name exempting notch fragments from duplicate-perimeter suppression.
    private const string NotchRoleKey = "notch.object_role"; // Source metadata key whose "notch" value preserves notch geometry at boundary contacts.
    private const string Section = "vPartSplit";
    private static double _width = ConfiguredDefaultWidth;
    private static double _seam = ConfiguredDefaultSeam;
    private static int _mode = ConfiguredDefaultMode;
    private static int _layout = ConfiguredDefaultLayout;
    private static double _distance = ConfiguredDefaultDistance;
    private static SplitDirection _direction = ConfiguredDefaultDirection;
    private static bool _label = ConfiguredDefaultLabel;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Part,SourceDetails> SourceCache = new();

    private sealed class State(Part part) : IDisposable
    {
      internal Part Part { get; } = part;
      internal PlanarPartSplitter.Proposal Proposal { get; set; } = null!;
      internal List<Panel> Panels { get; set; } = [];
      internal List<LocalCut>? Cuts { get; set; }
      internal int ActiveCut { get; set; }
      internal bool Dragging { get; set; }
      internal SplitDirection Direction { get; set; } = _direction;
      internal MatchingLabels.Style? LabelStyle { get; set; }
      internal List<Panel>? LabelPanels { get; set; }
      internal string LabelStart { get; set; } = "";
      internal string NextLabel { get; set; } = "";
      internal string? LabelError { get; set; }
      internal PlanarPartSplitter.Proposal? CachedProposal;
      internal List<LocalCut>? CachedCuts;
      internal List<CutPreview> CutPreviews { get; } = [];
      public void Dispose()
      {
        foreach(var panel in Panels)panel.Dispose();foreach(var cut in CutPreviews)cut.Dispose();
        if(SourceCache.TryGetValue(Part,out var source)){source.Dispose();SourceCache.Remove(Part);}
        Part.Dispose();
      }
    }
    private sealed record Output(List<Guid> Added, List<int> Groups, List<Guid> Copies);
    private sealed record LocalCut(Plane Frame);
    private sealed record SourceItem(Item Item,BoundingBox Bounds,bool Boundary);
    private sealed record SourceDetails(List<SourceItem> Items,double Area,Curve? FinishedBoundary) : IDisposable
    {
      public void Dispose(){FinishedBoundary?.Dispose();GC.SuppressFinalize(this);}
      ~SourceDetails(){FinishedBoundary?.Dispose();}
    }
    private sealed record CutPreview(Curve Line,List<Curve> Segments) : IDisposable
    { public void Dispose(){Line.Dispose();foreach(var segment in Segments)segment.Dispose();} }
    private sealed record SeamLabel(int Seam, int Segment, string Value, TextEntity Text);
    private sealed class Panel(Curve cut, BoundingBox bounds, List<Curve> preview, int bandIndex) : IDisposable
    {
      internal Curve Cut { get; } = cut;
      internal BoundingBox Bounds { get; } = bounds;
      internal int BandIndex { get; } = bandIndex;
      internal Plane Frame { get; set; } = Plane.Unset;
      internal double NominalArea { get; set; }
      internal PlanarFit.Fit? CompactFit { get; set; }
      internal Plane CompactSeed { get; set; } = Plane.Unset;
      internal double CompactWidth { get; set; } = double.NaN; // NaN means no width-specific compact layout has been cached.
      internal bool CompactRefined { get; set; }
      internal List<Curve> Preview { get; } = preview;
      internal List<Item> Details { get; } = [];
      internal List<Curve> SewLines { get; } = [];
      internal List<SeamLabel> Labels { get; } = [];
      public void Dispose()
      {
        Cut.Dispose(); foreach (var curve in Preview) curve.Dispose();
        foreach (var curve in SewLines) curve.Dispose();
        foreach (var label in Labels) label.Text.Dispose();
        foreach (var item in Details) item.Geometry.Dispose();
      }
    }
    private sealed record Options(OptionDouble Width, OptionDouble Seam, int ModeIndex,
      OptionDouble Distance, int LayoutIndex, int AutoIndex, int DirectionIndex, OptionToggle Label, int StartLabelIndex, int AddSplitIndex);

    private static PlanarPartSplitter.Settings Settings(SplitDirection? direction=null) => new(_width, _seam, Gap: _distance,
      Spacing: _mode == EvenModeIndex ? PlanarPartSplitter.SpacingMode.Even : PlanarPartSplitter.SpacingMode.MaxWidth)
      { Direction=direction??_direction };

    private static void Load() => ToolsOptionStore.Read<int>(Section, section =>
    {
      _width = ToolsOptionStore.TryGetDouble(section, "width", out var width) ? width : ConfiguredDefaultWidth;
      _seam = ToolsOptionStore.TryGetDouble(section, "seam", out var seam) ? seam : ConfiguredDefaultSeam;
      _mode = ToolsOptionStore.TryGetDouble(section, "mode", out var mode) && mode >= 0 && mode < ModeNames.Length && mode == Math.Truncate(mode) ? (int)mode : ConfiguredDefaultMode;
      _layout = ToolsOptionStore.TryGetDouble(section,"layout",out var layout) && layout>=0 && layout<LayoutNames.Length && layout==Math.Truncate(layout) ? (int)layout : ConfiguredDefaultLayout;
      _distance = ToolsOptionStore.TryGetDouble(section,"distance",out var distance) && double.IsFinite(distance) && distance>=0 ? distance : ConfiguredDefaultDistance;
      _direction = ToolsOptionStore.TryGetString(section,"direction",out var direction)&&Enum.TryParse<SplitDirection>(direction,true,out var parsed)&&Enum.IsDefined(parsed)?parsed:ConfiguredDefaultDirection;
      _label = ToolsOptionStore.TryGetBool(section,"label",out var label) ? label : ConfiguredDefaultLabel;
      if (!ValidOptions(_width, _seam)) { _width = ConfiguredDefaultWidth; _seam = ConfiguredDefaultSeam; }
      return 0;
    });

    private static void Save() => ToolsOptionStore.Update(Section, section =>
    { section["width"] = _width; section["seam"] = _seam; section["mode"] = _mode;
      section["layout"] = _layout; section["distance"] = _distance; section["direction"] = _direction.ToString(); section["label"] = _label; });

    private static bool ValidOptions(double width, double seam) =>
      double.IsFinite(width) && double.IsFinite(seam) && seam >= 0 && width > 2 * seam + RhinoMath.ZeroTolerance;

    private static Options AddOptions(GetBaseClass getter, double tolerance, bool editing = false, SplitDirection? direction = null, string? startLabel = null)
    {
      var width = new OptionDouble(_width, tolerance, double.MaxValue);
      var seam = new OptionDouble(_seam, 0, double.MaxValue);
      getter.AddOptionDouble("Width", ref width);
      getter.AddOptionDouble("Seam", ref seam);
      var label = new OptionToggle(_label,"No","Yes"); getter.AddOptionToggle("Label",ref label);
      int startLabelIndex=getter.AddOption("StartLabel",startLabel??MatchingLabels.DefaultValue);
      int mode = getter.AddOptionList("Mode", ModeNames, _mode);
      var distance = new OptionDouble(_distance,0,double.MaxValue);
      getter.AddOptionDouble("Distance",ref distance);
      int layout = getter.AddOptionList("Layout",LayoutNames,_layout);
      int auto = -1;
      int addSplit = -1;
      int directionIndex=getter.AddOptionList("Direction",DirectionNames,(int)(direction??_direction));
      if (editing)
      {
        auto = getter.AddOption("Auto");
        addSplit = getter.AddOption("AddSplit");
      }
      getter.AcceptNumber(true, false);
      return new Options(width, seam, mode, distance, layout, auto, directionIndex, label, startLabelIndex, addSplit);
    }

    private static bool ReadOptions(RhinoDoc doc, GetBaseClass getter, GetResult result, Options options)
    {
      if(result==GetResult.Option&&getter.Option()?.Index==options.StartLabelIndex)
      {
        var current=MatchingLabels.Read(doc).Value;
        string value=current;
        if(RhinoGet.GetString("Starting seam label",false,ref value)!=Result.Success) return false;
        value=LabelSequences.NormalizeValue(value);
        if(string.IsNullOrWhiteSpace(value)) return false;
        if(!MatchingLabels.Advance(doc,current,value))
        { RhinoApp.WriteLine("vPartSplit: starting label could not be saved; see the diagnostic log."); return false; }
      }
      var width = result == GetResult.Number ? getter.Number() : options.Width.CurrentValue;
      var seam = options.Seam.CurrentValue;
      if (!ValidOptions(width, seam) || !double.IsFinite(options.Distance.CurrentValue) || options.Distance.CurrentValue<0)
      { RhinoApp.WriteLine("vPartSplit: Width must exceed twice Seam; Seam cannot be negative."); return false; }
      _width = width; _seam = seam;
      _label = options.Label.CurrentValue;
      _distance = options.Distance.CurrentValue;
      if (result == GetResult.Option && getter.Option()?.Index == options.ModeIndex)
        _mode = getter.Option().CurrentListOptionIndex;
      if (result == GetResult.Option && getter.Option()?.Index == options.LayoutIndex)
        _layout = getter.Option().CurrentListOptionIndex;
      if (result == GetResult.Option && getter.Option()?.Index == options.DirectionIndex)
        _direction = (SplitDirection)getter.Option().CurrentListOptionIndex;
      return true;
    }

    internal static Result Run(RhinoDoc doc)
    {
      Load();
      double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
      Options? selectionOptions=null;
      var ids=PlanarPartSelection.Pick(doc,"Select parts to split",
        getter=>selectionOptions=AddOptions(getter,tolerance,startLabel:MatchingLabels.Read(doc).Value),
        (getter,result)=> { if(ReadOptions(doc,getter,result,selectionOptions!)) Save(); },out var selectionResult);
      if(selectionResult!=Result.Success) return selectionResult;
      if(ids.Count==0) return Result.Cancel;
      var states=PlanarPartSelection.Capture(doc,ids,tolerance).Select(part=>new State(part)).ToList();
      if(states.Count==0) { RhinoApp.WriteLine("vPartSplit: no closed planar parts found; selection retained."); return Result.Nothing; }
      using var highlight=new PreviewDisplay.ObjectHighlighter(doc);
      Curve? hoverGeometry=null;
      Guid hoverId=Guid.NewGuid();
      int active=0;
      var cplane=doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??states[0].Part.Plane;
      var bounds=BoundingBox.Empty;
      foreach(var state in states) bounds.Union(state.Part.Outline.GetBoundingBox(cplane));
      var anchor=cplane.PointAt(bounds.Max.X+Math.Max(_distance,InitialLayoutGap)*ConfiguredInitialLayoutGapFactor,bounds.Min.Y);
      bool committed=false;
      var labelStyle=MatchingLabels.Read(doc);
      void RefreshLabels(RhinoDoc changed)
      { if(changed.RuntimeSerialNumber==doc.RuntimeSerialNumber) { labelStyle=MatchingLabels.Read(doc); doc.Views.Redraw(); } }
      void ClearHover() { if(hoverGeometry==null)return;highlight.SetObjects([]); hoverGeometry.Dispose(); hoverGeometry=null; }
      void Hover(int partIndex,int seamIndex)
      {
        ClearHover();
        if(partIndex<0||seamIndex<0) return;
        var state=states[partIndex];
        state.ActiveCut=seamIndex;
        hoverGeometry=SplitLine(state.Part,state.Proposal,state.Cuts,seamIndex,tolerance);
        if(hoverGeometry!=null) highlight.SetObjects([hoverId],new Dictionary<Guid,GeometryBase>{{hoverId,hoverGeometry}});
      }
      (int Part,int Seam) Find(RhinoViewport viewport,int x,int y)
      {
        for(int index=0;index<states.Count;index++)
        {
          var state=states[index];
          var seam=FindCachedCut(viewport,x,y,CachedCutPreviews(state,tolerance));
          if(seam>=0) return(index,seam);
        }
        return(-1,-1);
      }
      bool Replan(bool automatic)
      {
        var pending=new List<(State State,PlanarPartSplitter.Proposal Proposal,List<LocalCut>? Cuts,List<Panel> Panels)>();
        foreach(var state in states)
        {
          PlanarPartSplitter.Proposal? next=null;
          if(automatic)
          {
            if(PlanarPartSplitter.TryPropose(state.Part.Outline,state.Part.Plane,Settings(state.Direction),out var candidate)) next=candidate;
          }
          else
          {
            var frame=state.Proposal.Frame;
            var box=state.Part.Outline.GetBoundingBox(frame);
            next=PlanarPartSplitter.AtPosition(frame,box.Min.Y,box.Max.Y,Settings(state.Direction),null);
          }
          if(next==null||!PlanCrossCuts(state.Part,next,Settings(state.Direction),tolerance,automatic,out var planned,out var cuts,out var panels))
          { foreach(var item in pending) foreach(var panel in item.Panels) panel.Dispose(); return false; }
          pending.Add((state,planned,cuts,panels));
        }
        ClearHover();
        foreach(var item in pending)
        { foreach(var panel in item.State.Panels) panel.Dispose(); item.State.Proposal=item.Proposal; item.State.Cuts=item.Cuts; item.State.Panels=item.Panels; }
        return true;
      }
      (Options Values,int PartIndex) Configure(GetPoint getter)
      {
        var options=AddOptions(getter,tolerance,true,states[active].Direction,labelStyle.Value);
        int partOption=states.Count>1?getter.AddOptionList("Part",Enumerable.Range(1,states.Count).Select(index=>index.ToString()).ToArray(),active):-1;
        return(options,partOption);
      }
      void Change(GetPoint getter,GetResult result,(Options Values,int PartIndex) controls)
      {
        if(result==GetResult.Option&&getter.Option()?.Index==controls.Values.AddSplitIndex)
        {
          using var add=new GetPoint();
          add.SetCommandPrompt("Location for additional cross-cut");
          add.Constrain(states[active].Part.Plane,false);
          add.PermitObjectSnap(AllowObjectSnaps);add.EnableObjectSnapCursors(AllowObjectSnaps);
          int candidatePart=-1;LocalCut? candidate=null;Point3d last=Point3d.Unset;
          void Update(Point3d point)
          {
            if(last.IsValid&&last.DistanceTo(point)<=tolerance)return;
            last=point;candidatePart=-1;candidate=null;
            for(int index=0;index<states.Count;index++)
              if(states[index].Part.Outline.Contains(point,states[index].Part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)
              { candidatePart=index;candidate=AlignCut(states[index].Part,states[index].Proposal,PlanarPartSplitter.FrameY(states[index].Proposal.Frame,point),tolerance);break; }
          }
          add.DynamicDraw+=(_,e)=>
          {
            Update(e.CurrentPoint);DrawBatch(e.Display,doc,states,cplane,anchor,tolerance,labelStyle);
            if(candidate!=null)
              using(var line=PlanarPartSplitter.Seam(states[candidatePart].Part.Outline,candidate.Frame,0,tolerance))
                if(line!=null)PreviewDisplay.DrawCurve(e.Display,line,CutPreviewColor);
          };
          if(add.Get()==GetResult.Point)
          {
            Update(add.Point());
            if(candidatePart>=0&&candidate!=null&&TryInsertCut(states[candidatePart],candidate,tolerance))active=candidatePart;
            else RhinoApp.WriteLine("vPartSplit: cut must cross a part, remain separate from other cuts and preserve the material width.");
          }
          ClearHover();return;
        }
        if(result==GetResult.Option&&getter.Option()?.Index==controls.PartIndex)
        { active=getter.Option().CurrentListOptionIndex; return; }
        var previous=(_width,_seam,_mode,_layout,_distance,_direction);
        var previousDirection=states[active].Direction;
        if(!ReadOptions(doc,getter,result,controls.Values)) return;
        bool directionChanged=result==GetResult.Option&&getter.Option()?.Index==controls.Values.DirectionIndex;
        if(directionChanged) states[active].Direction=_direction;
        bool automatic=result==GetResult.Option&&getter.Option()?.Index==controls.Values.AutoIndex;
        bool settingsChanged=directionChanged||_width!=previous._width||_seam!=previous._seam||_mode!=previous._mode;
        bool updated=true;
        if(settingsChanged||automatic)updated=Replan(automatic);
        if(!updated)
        { (_width,_seam,_mode,_layout,_distance,_direction)=previous; states[active].Direction=previousDirection; RhinoApp.WriteLine("vPartSplit: previous valid settings and all part previews retained."); return; }
        Save();
      }
      try
      {
        MatchingLabels.Changed+=RefreshLabels;
        if(!Replan(true)) { RhinoApp.WriteLine("vPartSplit: could not fit every selected part within Width including Seam."); return Result.Nothing; }
        foreach(var id in ids) doc.Objects.FindId(id)?.Select(false);
        doc.Views.Redraw();
        while(true)
        {
          using var getter=new GetPoint();
          getter.SetCommandPrompt("Adjust split cuts; Enter to place parts");
          getter.PermitObjectSnap(AllowObjectSnaps); getter.EnableObjectSnapCursors(AllowObjectSnaps); getter.AcceptNothing(true);
          var controls=Configure(getter);
          int movingPart=-1,movingSeam=-1;
          (int Part,int Seam) hovered=(-1,-1);
          bool pivoting=false,dragging=false;
          Point3d pivot=Point3d.Unset;
          double grabY=0,seamY=0;
          var dragFrame=states[active].Proposal.Frame;
          long refreshed=0;
          Point3d lastAttempt=Point3d.Unset;
          bool Drag(Point3d point)
          {
            if(movingPart<0||movingSeam<0||!point.IsValid) return false;
            var state=states[movingPart];
            var projected=state.Part.Plane.ClosestPoint(point);
            if(pivoting&&state.Part.Outline.ClosestPoint(projected,out double parameter))projected=state.Part.Outline.PointAt(parameter);
            if(lastAttempt.IsValid&&lastAttempt.DistanceTo(projected)<=tolerance)return true;
            lastAttempt=projected;
            var target=pivoting?pivot:dragFrame.PointAt(0,seamY+PlanarPartSplitter.FrameY(dragFrame,projected)-grabY);
            var direction=projected-pivot;
            if(pivoting&&direction.Length<=tolerance) return false;
            if(pivoting&&direction*dragFrame.XAxis<0) direction=-direction;
            if(state.Cuts is {Count:>0} local)
            {
              var across=pivoting?direction:dragFrame.XAxis;
              if(!across.Unitize())return false;
              var cuts=local.ToList();
              cuts[movingSeam]=new(new Plane(pivoting?pivot:target,across,Vector3d.CrossProduct(state.Part.Plane.Normal,across)));
              var localPanels=BuildLocalPanelsCore(state.Part,state.Proposal,cuts,Settings(state.Direction),tolerance,out double width,state.Panels,movingSeam);
              if(localPanels.Count==0||width>_width+tolerance){foreach(var panel in localPanels)if(!state.Panels.Contains(panel))panel.Dispose();return false;}
              ClearHover();ReplacePanels(state,localPanels);state.Cuts=cuts;return true;
            }
            double angle=pivoting?Math.Atan2(direction*state.Part.Plane.YAxis,direction*state.Part.Plane.XAxis)
              :Math.Atan2(dragFrame.XAxis*state.Part.Plane.YAxis,dragFrame.XAxis*state.Part.Plane.XAxis);
            if(!PlanarPartSplitter.TryAtPivot(state.Part.Outline,state.Part.Plane,Settings(state.Direction),angle,target,out var next,out var index)) return false;
            var panels=BuildPanels(state.Part,next,Settings(state.Direction),tolerance);
            if(panels.Count==0) return false;
            ClearHover();
            foreach(var panel in state.Panels) panel.Dispose();
            state.Proposal=next; state.Panels=panels; movingSeam=index;
            return true;
          }
          getter.MouseMove+=(_,e)=>
          {
            dragging=movingPart>=0&&e.LeftButtonDown;
            if(!e.LeftButtonDown)
            {
              var next=Find(e.Viewport,e.WindowPoint.X,e.WindowPoint.Y);
              if(next!=hovered)
              {
                hovered=next; Hover(next.Part,next.Seam);
                if(next.Part>=0)active=next.Part;
              }
            }
          };
          getter.MouseDown+=(_,e)=>
          {
            var selected=Find(e.Viewport,e.WindowPoint.X,e.WindowPoint.Y);
            movingPart=selected.Part; movingSeam=selected.Seam;
            if(movingPart<0) return;
            active=movingPart; var state=states[active];
            state.Dragging=true;
            state.ActiveCut=movingSeam;
            var cut=CachedCutPreviews(state,tolerance)[movingSeam].Line;
            var start=e.Viewport.WorldToClient(cut.PointAtStart); var end=e.Viewport.WorldToClient(cut.PointAtEnd);
            double Distance(Point2d pixel)=>Math.Sqrt(Math.Pow(pixel.X-e.WindowPoint.X,2)+Math.Pow(pixel.Y-e.WindowPoint.Y,2));
            pivoting=Math.Min(Distance(start),Distance(end))<=EndpointPickRadius;
            var grab=state.Part.Plane.ClosestPoint(e.Point);
            dragFrame=state.Cuts?[movingSeam].Frame??state.Proposal.Frame; seamY=state.Cuts==null?state.Proposal.Seams[movingSeam]:0; grabY=PlanarPartSplitter.FrameY(dragFrame,grab);
            pivot=grab.DistanceTo(cut.PointAtStart)<=grab.DistanceTo(cut.PointAtEnd)?cut.PointAtEnd:cut.PointAtStart;
            if(pivoting)getter.SetBasePoint(pivot,false);
            getter.Constrain(state.Part.Plane,false);ClearHover();
            dragging=true;
          };
          getter.DynamicDraw+=(_,e)=>
          {
            var now=System.Environment.TickCount64;
            if(dragging&&now-refreshed>=ConfiguredDragRefreshMilliseconds) { refreshed=now; Drag(e.CurrentPoint); }
            DrawBatch(e.Display,doc,states,cplane,anchor,tolerance,labelStyle);
            if(movingPart>=0&&movingSeam>=0)
              foreach(var segment in CachedCutPreviews(states[movingPart],tolerance)[movingSeam].Segments)
                PreviewDisplay.DrawCurve(e.Display,segment,CutPreviewColor);
          };
          var result=getter.Get(onMouseUp:true); ClearHover();
          foreach(var state in states)state.Dragging=false;
          if(result is GetResult.Option or GetResult.Number) { Change(getter,result,controls); continue; }
          if(result==GetResult.Nothing) break;
          if(result!=GetResult.Point) return Result.Cancel;
          if(movingPart>=0&&!Drag(getter.Point())) RhinoApp.WriteLine("vPartSplit: last valid cut position retained.");
          ClearHover();
        }
        while(true)
        {
          using var getter=new GetPoint();
          getter.SetCommandPrompt("Place split parts; Enter uses preview location"); getter.AcceptNothing(true);
          var controls=Configure(getter);
          getter.DynamicDraw+=(_,e)=> { anchor=e.CurrentPoint; DrawBatch(e.Display,doc,states,cplane,anchor,tolerance,labelStyle); };
          var result=getter.Get();
          if(result is GetResult.Option or GetResult.Number) { Change(getter,result,controls); continue; }
          if(result is not (GetResult.Point or GetResult.Nothing)) return Result.Cancel;
          if(result==GetResult.Point) anchor=getter.Point();
          string nextLabel=EnsureLabels(doc,states,labelStyle,tolerance);
          if(_label&&states.FirstOrDefault(state=>state.LabelError!=null) is {} invalid)
          { RhinoApp.WriteLine("vPartSplit: {0}",invalid.LabelError); continue; }
          var placements=BatchPlacements(states,cplane,anchor);
          var outputs=new List<Output>();
          foreach(var (state,index) in states.Select((state,index)=>(state,index)))
          {
            if(!Commit(doc,state.Part,state.Proposal,state.Panels,cplane,anchor,tolerance,out var output,placements[index],PanelOrder(state.Panels,state.Proposal,state.Direction),state.Direction,state.Cuts,labelStyle))
            {
              foreach(var created in outputs) { foreach(var id in created.Added) doc.Objects.Delete(id,true); foreach(var group in created.Groups) doc.Groups.Delete(group); }
              return Result.Failure;
            }
            outputs.Add(output!);
          }
          committed=true; Save();
          if(_label&&!MatchingLabels.Advance(doc,labelStyle.Value,nextLabel))
            RhinoApp.WriteLine("vPartSplit: parts placed, but the shared next label could not be saved; see the diagnostic log.");
          RhinoApp.WriteLine($"vPartSplit: split {states.Count} source part(s) into {states.Sum(state=>state.Panels.Count)} grouped part(s); Layout={LayoutNames[_layout]}, Distance={_distance:G6}.");
          doc.Objects.Select(outputs.SelectMany(output=>output.Copies));
          return Result.Success;
        }
      }
      catch(Exception ex) { Log.Write("vPartSplit","failed: {0}",ex); RhinoApp.WriteLine("vPartSplit: {0}",ex.Message); return Result.Failure; }
      finally
      { MatchingLabels.Changed-=RefreshLabels; ClearHover(); foreach(var state in states) state.Dispose(); if(!committed) foreach(var id in ids) doc.Objects.FindId(id)?.Select(true); doc.Views.Redraw(); }
    }

    private static Plane ProgressFrame(Part part,int samples)
    {
      var parameters=part.Outline.DivideByCount(samples,true);
      if(parameters==null||parameters.Length<3) return part.Plane;
      var points=parameters.Select(part.Outline.PointAt).Select(point=>(X:(point-part.Plane.Origin)*part.Plane.XAxis,Y:(point-part.Plane.Origin)*part.Plane.YAxis)).ToArray();
      double x=points.Average(point=>point.X),y=points.Average(point=>point.Y);
      double xx=points.Sum(point=>(point.X-x)*(point.X-x)),yy=points.Sum(point=>(point.Y-y)*(point.Y-y));
      double xy=points.Sum(point=>(point.X-x)*(point.Y-y));
      double angle=0.5*Math.Atan2(2*xy,xx-yy);
      var along=part.Plane.XAxis*Math.Cos(angle)+part.Plane.YAxis*Math.Sin(angle);
      return new Plane(part.Plane.Origin,Vector3d.CrossProduct(along,part.Plane.Normal),along);
    }

    private static Curve? SplitLine(Part part,PlanarPartSplitter.Proposal proposal,IReadOnlyList<LocalCut>? cuts,int index,double tolerance)
    {
      if(cuts==null) return PlanarPartSplitter.Seam(part.Outline,proposal.Frame,proposal.Seams[index],tolerance);
      return PlanarPartSplitter.Seam(part.Outline,cuts[index].Frame,0,tolerance);
    }

    private static bool CrossSection(Curve outline,Plane frame,double tolerance,out Point3d a,out Point3d b,out Vector3d along)
    {
      a=b=Point3d.Unset; along=Vector3d.Unset;
      var bounds=outline.GetBoundingBox(frame); double pad=Math.Max(InitialLayoutGap,bounds.Diagonal.Length);
      using var probe=new LineCurve(frame.PointAt(bounds.Min.X-pad,0),frame.PointAt(bounds.Max.X+pad,0));
      using var intersections=Intersection.CurveCurve(outline,probe,tolerance,tolerance);
      if(intersections==null||intersections.Any(hit=>hit.IsOverlap)) return false;
      var hits=intersections.Where(hit=>hit.IsPoint).OrderBy(hit=>hit.ParameterB).Aggregate(new List<IntersectionEvent>(),(values,hit)=>
      { if(values.Count==0||values[^1].PointB.DistanceTo(hit.PointB)>tolerance) values.Add(hit); return values; });
      if(hits.Count!=2) return false;
      a=hits[0].PointB;b=hits[1].PointB;
      if(outline.Contains(a+(b-a)*0.5,frame,tolerance)!=PointContainment.Inside) return false;
      var first=outline.TangentAt(hits[0].ParameterA);var second=outline.TangentAt(hits[1].ParameterA);
      if(!first.Unitize()||!second.Unitize()) return false;
      if(first*second<0)second=-second;
      along=first+second;
      along-=frame.Normal*(along*frame.Normal);
      return along.Unitize();
    }

    private static LocalCut AlignCut(Part part,PlanarPartSplitter.Proposal proposal,double position,double tolerance)
    {
      var frame=new Plane(proposal.Frame.PointAt(0,position),proposal.Frame.XAxis,proposal.Frame.YAxis);
      if(!CrossSection(part.Outline,frame,tolerance,out var a,out var b,out _)) return new(frame);
      var center=a+(b-a)*0.5;
      frame=new Plane(center,frame.XAxis,frame.YAxis);
      for(int pass=0;pass<NormalAlignmentPasses;pass++)
      {
        if(!CrossSection(part.Outline,frame,tolerance,out _,out _,out var along))break;
        if(along*proposal.Frame.YAxis<0)along=-along;
        if(along*proposal.Frame.YAxis<MinimumProgressCosine)break;
        var candidate=new Plane(center,Vector3d.CrossProduct(along,part.Plane.Normal),along);
        if(!CrossSection(part.Outline,candidate,tolerance,out _,out _,out _))break;
        frame=candidate;
      }
      return new(frame);
    }

    private static bool OrderedCuts(Part part,IReadOnlyList<LocalCut> cuts,double tolerance)
    {
      var lines=new List<Curve>();
      try
      {
        foreach(var cut in cuts)
        {
          var line=PlanarPartSplitter.Seam(part.Outline,cut.Frame,0,tolerance);
          if(line==null)return false;
          foreach(var other in lines)
          {
            using var hits=Intersection.CurveCurve(line,other,tolerance,tolerance);
            if(hits!=null&&hits.Any(hit=>hit.IsOverlap||part.Outline.Contains(hit.PointA,part.Plane,tolerance)!=PointContainment.Outside))
            {line.Dispose();return false;}
          }
          lines.Add(line);
        }
        return true;
      }
      finally{foreach(var line in lines)line.Dispose();}
    }

    private static Curve[] ClipLocalBand(Curve source,IReadOnlyList<LocalCut> cuts,int band,double allowance,double tolerance)
    {
      var pieces=new[]{source.DuplicateCurve()};
      try
      {
        foreach(int index in new[]{band-1,band})
        {
          if(index<0||index>=cuts.Count)continue;
          var next=new List<Curve>();
          try
          {
            foreach(var piece in pieces)
            {
              var bounds=piece.GetBoundingBox(cuts[index].Frame);
              double pad=Math.Max(InitialLayoutGap,bounds.Diagonal.Length);
              bool lower=index==band-1;
              next.AddRange(PlanarPartSplitter.ClipBand(piece,cuts[index].Frame,
                lower?-allowance:bounds.Min.Y-pad,lower?bounds.Max.Y+pad:allowance,tolerance));
            }
          }
          catch{foreach(var curve in next)curve.Dispose();throw;}
          foreach(var piece in pieces)piece.Dispose();pieces=next.ToArray();
        }
        var result=pieces;pieces=[];return result;
      }
      finally{foreach(var piece in pieces)piece.Dispose();}
    }

    private static List<Panel> BuildLocalPanels(Part part,PlanarPartSplitter.Proposal proposal,IReadOnlyList<LocalCut> cuts,
      PlanarPartSplitter.Settings settings,double tolerance,out double maximumWidth)
      => BuildLocalPanelsCore(part,proposal,cuts,settings,tolerance,out maximumWidth,null,-1);

    private static List<Panel> BuildLocalPanelsCore(Part part,PlanarPartSplitter.Proposal proposal,IReadOnlyList<LocalCut> cuts,
      PlanarPartSplitter.Settings settings,double tolerance,out double maximumWidth,IReadOnlyList<Panel>? previous,int changedSeam)
    {
      maximumWidth=0;
      var panels=new List<Panel>();double nominalArea=0;
      try
      {
        if(!OrderedCuts(part,cuts,tolerance))return [];
        var source=GetSourceDetails(part,tolerance);
        for(int band=0;band<=cuts.Count;band++)
        {
          if(previous!=null&&band!=changedSeam&&band!=changedSeam+1)
          {
            foreach(var panel in previous.Where(panel=>panel.BandIndex==band))
            { panels.Add(panel);nominalArea+=panel.NominalArea;maximumWidth=Math.Max(maximumWidth,panel.Bounds.Max.Y-panel.Bounds.Min.Y); }
            continue;
          }
          var nominal=ClipLocalBand(part.Outline,cuts,band,0,tolerance);
          var expanded=ClipLocalBand(part.Outline,cuts,band,settings.Allowance,tolerance);
          try
          {
            double bandArea=0;
            foreach(var outline in nominal){using var mass=AreaMassProperties.Compute(outline);bandArea+=mass?.Area??0;}
            nominalArea+=bandArea;
            int firstPanel=panels.Count;
            var along=band==0?cuts[0].Frame.YAxis:band==cuts.Count?cuts[^1].Frame.YAxis:cuts[band-1].Frame.YAxis+cuts[band].Frame.YAxis;
            if(!along.Unitize())throw new InvalidOperationException("neighboring cut directions are inconsistent");
            var frame=new Plane(part.Plane.Origin,Vector3d.CrossProduct(along,part.Plane.Normal),along);
            foreach(var cut in expanded)
            {
              var matching=nominal.Where(curve=>cut.Contains(curve.PointAtNormalizedLength(0.5),part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)
                .Select(curve=>curve.DuplicateCurve()).ToList();
              if(matching.Count==0)continue;
              var bounds=cut.GetBoundingBox(frame);
              if(!cut.IsClosed)throw new InvalidOperationException("a cross-cut produced an open boundary");
              maximumWidth=Math.Max(maximumWidth,bounds.Max.Y-bounds.Min.Y);
              var panel=new Panel(cut.DuplicateCurve(),bounds,matching,band){Frame=frame};panels.Add(panel);
              foreach(int index in new[]{band-1,band})
              {
                if(index<0||index>=cuts.Count)continue;
                using var sew=SplitLine(part,proposal,cuts,index,tolerance);
                if(sew!=null)panel.SewLines.AddRange(ClipSewLine(part,sew,cut,tolerance));
              }
            }
            if(panels.Count>firstPanel)panels[firstPanel].NominalArea=bandArea;
          }
          finally{foreach(var curve in nominal.Concat(expanded))curve.Dispose();}
        }
        if(panels.Count==0||panels.Count>settings.MaxParts||
          Math.Abs(nominalArea-source.Area)>tolerance*part.Outline.GetLength()*ConfiguredCoverageToleranceFactor)
          throw new InvalidOperationException("cross-cuts do not preserve the complete source area");
        // Trial layouts that fail the width limit never need expensive detail clipping.
        if(maximumWidth<=settings.Width+tolerance)
          foreach(var panel in panels)
          {
            if(previous!=null&&previous.Contains(panel))continue;
            var cutBounds=panel.Cut.GetBoundingBox(part.Plane);
            foreach(var detail in source.Items)
            {
              if(!BoundsOverlap(detail.Bounds,cutBounds,tolerance))continue;
              var item=detail.Item;
              if(item.Geometry is Curve curve)
              {
                foreach(var piece in ClipDetail(item,panel,part.Plane,tolerance))
                  if(detail.Boundary&&Coincident(piece,part.Outline,tolerance))piece.Dispose();else panel.Details.Add(item with{Geometry=piece});
              }
            }
          }
        if(maximumWidth<=settings.Width+tolerance)AssignNonCurveDetails(part,panels,tolerance);
        return panels;
      }
      catch(Exception ex){foreach(var panel in panels)if(previous==null||!previous.Contains(panel))panel.Dispose();Log.Write("vPartSplit","cross-cut layout rejected: {0}",ex.Message);return [];}
    }

    private static SourceDetails GetSourceDetails(Part part,double tolerance) => SourceCache.GetValue(part,source=>
    {
      var items=new List<SourceItem>();
      foreach(var item in source.Items)
      {
        bool boundary=item.Geometry is Curve&&item.Attributes.LayerIndex==source.Attributes.LayerIndex&&
          !string.Equals(item.Attributes.Name,NotchName,StringComparison.OrdinalIgnoreCase)&&item.Attributes.GetUserString(NotchRoleKey)!="notch";
        if(boundary&&Coincident((Curve)item.Geometry,source.Outline,tolerance))continue;
        items.Add(new(item,item.Geometry.GetBoundingBox(source.Plane),boundary));
      }
      using var mass=AreaMassProperties.Compute(source.Outline);
      double area=mass?.Area??throw new InvalidOperationException("source area could not be measured");
      var sewItems=source.Items.Where(item=>item.Geometry is Curve&&IsSewDetail(item)).ToArray();
      var loops=GroupBoundaryTopology.PlanarRegions(sewItems.Select(item=>(Curve)item.Geometry).ToArray(),tolerance,tolerance);
      try
      {
        var finished=loops.Where(loop=>loop.IsClosed&&loop.IsInPlane(source.Plane,tolerance))
          .Select(loop=>{using var properties=AreaMassProperties.Compute(loop);return (Loop:loop,Area:properties?.Area??0);})
          .Where(candidate=>candidate.Area<=area+tolerance*source.Outline.GetLength()&&
            (candidate.Area>=area*ConfiguredMinimumFinishedAreaRatio||sewItems.Any(item=>item.Attributes.GetUserString("vSeam.sourceRoot")!=null)))
          .Where(candidate=>Coincident(candidate.Loop,source.Outline,tolerance)||
            source.Outline.Contains(candidate.Loop.PointAtNormalizedLength(0.5),source.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)
          .OrderByDescending(candidate=>candidate.Area).FirstOrDefault().Loop;
        return new(items,area,finished?.DuplicateCurve());
      }
      finally {foreach(var loop in loops)loop.Dispose();}
    });

    private static bool BoundsOverlap(BoundingBox a,BoundingBox b,double tolerance) =>
      a.Max.X>=b.Min.X-tolerance&&b.Max.X>=a.Min.X-tolerance&&a.Max.Y>=b.Min.Y-tolerance&&b.Max.Y>=a.Min.Y-tolerance;

    private static void ReplacePanels(State state,List<Panel> panels)
    {
      foreach(var panel in panels.Where(panel=>!state.Panels.Contains(panel)))
      {
        var center=panel.Frame.PointAt(panel.Bounds.Center.X,panel.Bounds.Center.Y,panel.Bounds.Center.Z);
        var previous=state.Panels.Where(old=>old.BandIndex==panel.BandIndex&&old.CompactFit!=null)
          .OrderBy(old=>old.Frame.PointAt(old.Bounds.Center.X,old.Bounds.Center.Y,old.Bounds.Center.Z).DistanceToSquared(center)).FirstOrDefault();
        if(previous?.CompactFit is {} fit)panel.CompactSeed=fit.Plane;
      }
      foreach(var panel in state.Panels)if(!panels.Contains(panel))panel.Dispose();
      state.Panels=panels;
    }

    private static bool TryInsertCut(State state,LocalCut candidate,double tolerance)
    {
      var proposal=state.Proposal;
      var cuts=state.Cuts?.ToList()??proposal.Seams.Select(position=>new LocalCut(new Plane(proposal.Frame.PointAt(0,position),proposal.Frame.XAxis,proposal.Frame.YAxis))).ToList();
      if(candidate.Frame.YAxis*proposal.Frame.YAxis<0)
        candidate=new(new Plane(candidate.Frame.Origin,-candidate.Frame.XAxis,-candidate.Frame.YAxis));
      double position=PlanarPartSplitter.FrameY(proposal.Frame,candidate.Frame.Origin);
      if(position<=proposal.MinY+tolerance||position>=proposal.MaxY-tolerance||
        cuts.Any(cut=>Math.Abs(PlanarPartSplitter.FrameY(proposal.Frame,cut.Frame.Origin)-position)<=tolerance))return false;
      cuts.Add(candidate);cuts=cuts.OrderBy(cut=>PlanarPartSplitter.FrameY(proposal.Frame,cut.Frame.Origin)).ToList();
      var next=proposal with{Seams=cuts.Select(cut=>PlanarPartSplitter.FrameY(proposal.Frame,cut.Frame.Origin)).ToList()};
      var panels=BuildLocalPanels(state.Part,next,cuts,Settings(state.Direction),tolerance,out double width);
      if(panels.Count<=state.Panels.Count||width>_width+tolerance)
      { foreach(var panel in panels)panel.Dispose();return false; }
      ReplacePanels(state,panels);state.Cuts=cuts;state.Proposal=next;
      Log.Write("vPartSplit","manual cross-cut added cuts={0} parts={1}",cuts.Count,panels.Count);
      return true;
    }

    private static IReadOnlyList<CutPreview> CachedCutPreviews(State state,double tolerance)
    {
      if(ReferenceEquals(state.CachedProposal,state.Proposal)&&ReferenceEquals(state.CachedCuts,state.Cuts))return state.CutPreviews;
      foreach(var cut in state.CutPreviews)cut.Dispose();state.CutPreviews.Clear();
      for(int index=0;index<state.Proposal.Seams.Count;index++)
      {
        var line=SplitLine(state.Part,state.Proposal,state.Cuts,index,tolerance);
        if(line==null)throw new InvalidOperationException("split has no boundary intersections");
        state.CutPreviews.Add(new(line,PlanarPartSplitter.ClipCurve(line,state.Part.Outline,state.Part.Plane,tolerance)));
      }
      state.CachedProposal=state.Proposal;state.CachedCuts=state.Cuts;
      return state.CutPreviews;
    }

    private static bool PlanCrossCuts(Part part,PlanarPartSplitter.Proposal initial,PlanarPartSplitter.Settings settings,double tolerance,bool automatic,
      out PlanarPartSplitter.Proposal proposal,out List<LocalCut>? cuts,out List<Panel> panels)
    {
      proposal=initial;cuts=null;panels=[];
      if(initial.Seams.Count==0){panels=BuildPanels(part,initial,settings,tolerance);return panels.Count>0;}
      var frame=automatic?ProgressFrame(part,settings.Samples):initial.Frame;
      var bounds=part.Outline.GetBoundingBox(frame);
      var effective=settings;
      var seed=PlanarPartSplitter.AtPosition(frame,bounds.Min.Y,bounds.Max.Y,effective,null);
      if(seed==null)return false;
      for(int count=seed.Seams.Count+1;count<=settings.MaxParts;count++)
      {
        proposal=settings.Spacing==PlanarPartSplitter.SpacingMode.Even
          ?new(frame,bounds.Min.Y,bounds.Max.Y,Enumerable.Range(1,count-1).Select(index=>bounds.Min.Y+(bounds.Max.Y-bounds.Min.Y)*index/count).ToList(),0)
          :PlanarPartSplitter.AtPosition(frame,bounds.Min.Y,bounds.Max.Y,effective,null)!;
        if(proposal==null||proposal.Seams.Count==0)return false;
        var current=proposal;
        cuts=current.Seams.Select(position=>AlignCut(part,current,position,tolerance)).ToList();
        panels=BuildLocalPanels(part,proposal,cuts,settings,tolerance,out double span);
        if(panels.Count==0)return false;
        if(span<=settings.Width+tolerance)return true;
        foreach(var panel in panels)panel.Dispose();panels=[];
        effective=effective with{Width=Math.Max(2*settings.Allowance+tolerance,effective.Width*Math.Min(LocalWidthReduction,settings.Width/span))};
      }
      cuts=null;return false;
    }

    private static bool Coincident(Curve curve, Curve boundary, double tolerance) =>
      Enumerable.Range(0, ConfiguredBoundarySamples).All(index =>
      {
        var point = curve.PointAtNormalizedLength((double)index/(ConfiguredBoundarySamples-1));
        return boundary.ClosestPoint(point, out var parameter) && point.DistanceTo(boundary.PointAt(parameter)) <= tolerance*ConfiguredBoundaryToleranceFactor;
      });

    private static List<Panel> BuildPanels(Part part, PlanarPartSplitter.Proposal proposal, PlanarPartSplitter.Settings settings, double tolerance)
    {
      var panels = new List<Panel>();
      try
      {
        for (int index = 0; index <= proposal.Seams.Count; index++)
        {
          var (lower,upper) = PlanarPartSplitter.Band(proposal,index,settings.Allowance);
          var nominal = PlanarPartSplitter.Band(proposal,index,0);
          var previews = PlanarPartSplitter.ClipBand(part.Outline, proposal.Frame, nominal.Lower, nominal.Upper, tolerance);
          var cuts = PlanarPartSplitter.ClipBand(part.Outline, proposal.Frame, lower, upper, tolerance);
          try
          {
            foreach (var cut in cuts)
            {
              var bounds = cut.GetBoundingBox(proposal.Frame);
              var matching = previews.Where(curve => cut.Contains(curve.PointAtNormalizedLength(0.5),proposal.Frame,tolerance) is PointContainment.Inside or PointContainment.Coincident)
                .Select(curve => curve.DuplicateCurve()).ToList();
              if (matching.Count == 0) continue;
              if (!cut.IsClosed || bounds.Max.Y-bounds.Min.Y > settings.Width+tolerance) throw new InvalidOperationException("seam allowance exceeds material width");
              var panel = new Panel(cut.DuplicateCurve(), bounds, matching, index){Frame=proposal.Frame};
              panels.Add(panel);
              foreach(var seamIndex in new[] { index-1,index })
              {
                if(seamIndex<0 || seamIndex>=proposal.Seams.Count) continue;
                using var sew = PlanarPartSplitter.Seam(part.Outline,proposal.Frame,proposal.Seams[seamIndex],tolerance);
                if(sew!=null) panel.SewLines.AddRange(ClipSewLine(part,sew,cut,tolerance));
              }
              foreach (var item in part.Items)
              {
                if (item.Geometry is Curve curve)
                {
                  bool boundaryLayer = item.Attributes.LayerIndex == part.Attributes.LayerIndex &&
                    !string.Equals(item.Attributes.Name,NotchName,StringComparison.OrdinalIgnoreCase) &&
                    item.Attributes.GetUserString(NotchRoleKey) != "notch";
                  foreach (var piece in ClipDetail(item,panel,proposal.Frame,tolerance))
                    if (boundaryLayer && Coincident(piece,part.Outline,tolerance)) piece.Dispose();
                    else panel.Details.Add(item with { Geometry=piece });
                }
              }
            }
          }
          finally { foreach (var curve in cuts.Concat(previews)) curve.Dispose(); }
        }
        if (panels.Count == 0 || panels.Count > settings.MaxParts) throw new InvalidOperationException("no valid split panels");
        AssignNonCurveDetails(part,panels,tolerance);
        return panels;
      }
      catch (Exception ex)
      { foreach (var panel in panels) panel.Dispose(); Log.Write("vPartSplit","layout rejected: {0}",ex.Message); return []; }
    }

    private static bool IsSewDetail(Item item)=>string.Equals(item.LayerPath.Split("::").Last(),SewLayer,StringComparison.OrdinalIgnoreCase)||
      string.Equals(item.Attributes.Name,SewName,StringComparison.OrdinalIgnoreCase)||item.Attributes.GetUserString("vSeam.sourceRoot")!=null;

    private static IEnumerable<Curve> ClipSewLine(Part part,Curve line,Curve cut,double tolerance)
    {
      var boundary=GetSourceDetails(part,tolerance).FinishedBoundary??part.Outline;
      foreach(var piece in PlanarPartSplitter.ClipCurve(line,boundary,part.Plane,tolerance))
        using(piece)
          foreach(var clipped in PlanarPartSplitter.ClipCurve(piece,cut,part.Plane,tolerance))yield return clipped;
    }

    private static IEnumerable<Curve> ClipDetail(Item item,Panel panel,Plane plane,double tolerance)
    {
      var source=(Curve)item.Geometry;
      bool sew=IsSewDetail(item);
      // Finished seam/reference paths stop at the sew line, not at the allowance's outer cut edge.
      IEnumerable<Curve> boundaries=sew?panel.Preview:new[]{panel.Cut};
      foreach(var boundary in boundaries)
        foreach(var piece in PlanarPartSplitter.ClipCurve(source,boundary,plane,tolerance))yield return piece;
    }

    private static void AssignNonCurveDetails(Part part,IReadOnlyList<Panel> panels,double tolerance)
    {
      var assigned=panels.SelectMany(panel=>panel.Details).Where(item=>item.Geometry is not Curve).Select(item=>item.Id).ToHashSet();
      foreach(var item in part.Items.Where(item=>item.Geometry is not Curve&&!assigned.Contains(item.Id)))
      {
        var point=part.Plane.ClosestPoint(RepresentativePoint(item.Geometry));
        if(!point.IsValid)throw new InvalidOperationException("a part label or point has no valid location");
        // Grouped labels may sit outside the outline; keep them with the nearest nominal piece.
        var owner=panels.FirstOrDefault(panel=>panel.Preview.Any(outline=>outline.Contains(point,part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident))
          ??panels.OrderBy(panel=>panel.Preview.Min(outline=>outline.ClosestPoint(point,out var parameter)?point.DistanceToSquared(outline.PointAt(parameter)):double.PositiveInfinity)).First();
        owner.Details.Add(item with {Geometry=item.Geometry.Duplicate()});
        assigned.Add(item.Id);
      }
    }

    private static Point3d RepresentativePoint(GeometryBase geometry) => geometry switch
    {
      Rhino.Geometry.Point point => point.Location,
      TextDot dot => dot.Point,
      AnnotationBase annotation => annotation.Plane.Origin,
      _ => geometry.GetBoundingBox(true).Center
    };

    private static int FindCachedCut(RhinoViewport viewport, int x, int y, IReadOnlyList<CutPreview> cuts)
    {
      int nearest = -1; double radius = PickRadius;
      for (int index=0; index<cuts.Count; index++)
      {
        var cut=cuts[index].Line;
        var a=viewport.WorldToClient(cut.PointAtStart); var b=viewport.WorldToClient(cut.PointAtEnd);
        double dx=b.X-a.X, dy=b.Y-a.Y, length=dx*dx+dy*dy;
        if (length<=0) continue;
        double fraction=Math.Clamp(((x-a.X)*dx+(y-a.Y)*dy)/length,0,1);
        double distance=Math.Sqrt(Math.Pow(x-a.X-fraction*dx,2)+Math.Pow(y-a.Y-fraction*dy,2));
        if (distance<radius) { radius=distance; nearest=index; }
      }
      return nearest;
    }

    private static bool PreferReverse(Plane frame,Plane targetPlane) =>
      frame.XAxis*targetPlane.XAxis+frame.YAxis*targetPlane.YAxis<0;

    private static double OriginalBandSpread(int band,double seam,double distance) => band*(2*seam+distance);

    internal static int[] BandOrder(int count,SplitDirection direction)
    {
      var bands=Enumerable.Range(0,count);
      double middle=(count-1)/2.0;
      return direction switch {
        SplitDirection.End=>bands.Reverse().ToArray(),
        SplitDirection.Inward=>bands.OrderByDescending(index=>Math.Abs(index-middle)).ThenBy(index=>index).ToArray(),
        SplitDirection.Outward=>bands.OrderBy(index=>Math.Abs(index-middle)).ThenBy(index=>index).ToArray(),
        _=>bands.ToArray() };
    }

    private static int[] PanelOrder(IReadOnlyList<Panel> panels,PlanarPartSplitter.Proposal proposal,SplitDirection direction)
    {
      var ranks=BandOrder(proposal.Seams.Count+1,direction).Select((band,rank)=>(band,rank)).ToDictionary(item=>item.band,item=>item.rank);
      return Enumerable.Range(0,panels.Count).OrderBy(index=>ranks[panels[index].BandIndex]).ThenBy(index=>panels[index].Bounds.Min.X).ToArray();
    }

    private static Transform Placement(Plane frame, Plane targetPlane, Point3d anchor, BoundingBox bounds, double x)
    {
      bool longX=bounds.Max.X-bounds.Min.X>bounds.Max.Y-bounds.Min.Y;
      var axisX=longX?targetPlane.YAxis:targetPlane.XAxis;
      var axisY=longX?-targetPlane.XAxis:targetPlane.YAxis;
      if(frame.XAxis*axisX+frame.YAxis*axisY<0) { axisX=-axisX; axisY=-axisY; }
      var target=new Plane(anchor,axisX,axisY);
      var projected=BoundingBox.Empty;
      foreach(var corner in bounds.GetCorners())
      {
        var point=axisX*corner.X+axisY*corner.Y;
        projected.Union(new Point3d(point*targetPlane.XAxis,point*targetPlane.YAxis,corner.Z));
      }
      return Transform.Translation(targetPlane.XAxis*(x-projected.Min.X)-targetPlane.YAxis*projected.Min.Y)*Transform.PlaneToPlane(frame,target);
    }

    private static double VerticalWidth(BoundingBox bounds)=>Math.Min(bounds.Max.X-bounds.Min.X,bounds.Max.Y-bounds.Min.Y);

    private static PlanarFit.Fit CompactLayout(Panel panel,bool refine)
    {
      if(panel.CompactWidth!=_width)
      { panel.CompactFit=null;panel.CompactRefined=false;panel.CompactWidth=_width; }
      if(panel.CompactFit==null)
      {
        var frame=panel.CompactSeed.IsValid?panel.CompactSeed:panel.Frame;
        if(!PlanarFit.TryBounds(panel.Cut,frame,out var bounds)||VerticalWidth(bounds)>_width+RhinoMath.SqrtEpsilon)
        {
          frame=panel.Frame;
          if(!PlanarFit.TryBounds(panel.Cut,frame,out bounds))throw new InvalidOperationException("could not measure the split part for layout");
        }
        panel.CompactFit=new(frame,bounds,0);
      }
      if(refine&&!panel.CompactRefined)
      {
        var fit=PlanarFit.FitCurve(panel.Cut,panel.Frame,_width,RhinoMath.SqrtEpsilon);
        if(fit!=null&&fit.Area<=panel.CompactFit.Area)panel.CompactFit=fit;
        panel.CompactRefined=true;
      }
      return panel.CompactFit;
    }

    private static Transform[] PartPlacements(State state,Plane cplane,Point3d anchor)
    {
      var transforms=new Transform[state.Panels.Count];
      if(_layout==OriginalLayoutIndex)
      {
        var sourceBounds=state.Part.Outline.GetBoundingBox(state.Part.Plane);
        var sourceBase=state.Part.Plane.PointAt(sourceBounds.Min.X,sourceBounds.Min.Y);
        for(int index=0;index<state.Panels.Count;index++)
        {
          var spread=state.Cuts==null?state.Proposal.Frame.YAxis*OriginalBandSpread(state.Panels[index].BandIndex,_seam,_distance)
            :state.Cuts.Take(state.Panels[index].BandIndex).Aggregate(new Vector3d(),(sum,cut)=>sum+cut.Frame.YAxis*(2*_seam+_distance));
          transforms[index]=Transform.Translation(anchor-sourceBase+spread);
        }
        return transforms;
      }
      var order=PanelOrder(state.Panels,state.Proposal,state.Direction);
      double x=0;
      foreach(var index in order)
      {
        var panel=state.Panels[index];
        var fit=CompactLayout(panel,!state.Dragging);
        transforms[index]=Placement(fit.Plane,cplane,anchor,fit.Bounds,x);
        x+=VerticalWidth(fit.Bounds)+_distance;
      }
      return transforms;
    }

    private static Transform[][] BatchPlacements(IReadOnlyList<State> states,Plane cplane,Point3d anchor)
    {
      var result=new Transform[states.Count][];
      double y=0;
      for(int source=0;source<states.Count;source++)
      {
        var state=states[source];
        var placements=PartPlacements(state,cplane,anchor);
        var box=BoundingBox.Empty;
        for(int index=0;index<state.Panels.Count;index++)
        {
          var panel=state.Panels[index];
          var fit=_layout==OriginalLayoutIndex?null:CompactLayout(panel,!state.Dragging);
          foreach(var corner in (fit?.Bounds??panel.Bounds).GetCorners())
          {
            var world=placements[index]*(fit?.Plane??panel.Frame).PointAt(corner.X,corner.Y,corner.Z)-anchor;
            box.Union(new Point3d(world*cplane.XAxis,world*cplane.YAxis,world*cplane.Normal));
          }
        }
        var shift=Transform.Translation(-cplane.XAxis*box.Min.X+cplane.YAxis*(y-box.Min.Y));
        result[source]=placements.Select(transform=>shift*transform).ToArray();
        y+=box.Max.Y-box.Min.Y+_distance;
      }
      return result;
    }

    private static void ClearLabels(State state)
    {
      foreach(var panel in state.Panels)
      { foreach(var label in panel.Labels) label.Text.Dispose(); panel.Labels.Clear(); }
    }

    private static string EnsureLabels(RhinoDoc doc,IReadOnlyList<State> states,MatchingLabels.Style style,double tolerance)
    {
      string next=style.Value;
      foreach(var state in states)
      {
        if(state.LabelPanels==state.Panels&&state.LabelStyle==(_label?style:null)&&state.LabelStart==next)
        { next=state.NextLabel; continue; }
        ClearLabels(state);
        state.LabelPanels=state.Panels;state.LabelStyle=_label?style:null;state.LabelStart=next;state.LabelError=null;
        string start=next;
        try
        {
          if(_label&&state.Proposal.Seams.Count>0)
          {
            if(string.IsNullOrWhiteSpace(style.Value))
              throw new InvalidOperationException("enter a label value in vNotches, or use Label=No.");
            if(!double.IsFinite(style.Height)||style.Height<=tolerance)
              throw new InvalidOperationException("set a positive label size in vNotches, or use Label=No.");
            for(int seam=0;seam<state.Proposal.Seams.Count;seam++)
            {
              using var line=SplitLine(state.Part,state.Proposal,state.Cuts,seam,tolerance);
              if(line==null) throw new InvalidOperationException("a seam has no matching label location.");
              var pieces=PlanarPartSplitter.ClipCurve(line,state.Part.Outline,state.Part.Plane,tolerance);
              try
              {
                var frame=state.Cuts is {Count:>0}?state.Cuts[seam].Frame:state.Proposal.Frame;
                int segment=0;
                foreach(var piece in pieces.OrderBy(curve=>(curve.PointAtNormalizedLength(0.5)-frame.Origin)*frame.XAxis))
                {
                  if(piece.GetLength()<=tolerance) continue;
                  var midpoint=piece.PointAtNormalizedLength(0.5);
                  var mates=state.Panels.Where(panel=>(panel.BandIndex==seam||panel.BandIndex==seam+1)&&
                    panel.Preview.Any(outline=>outline.Contains(midpoint,state.Part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)).ToArray();
                  if(mates.Length!=2||mates[0].BandIndex==mates[1].BandIndex)
                    throw new InvalidOperationException($"cannot identify both mating edges of seam {seam+1} for labeling.");
                  foreach(var panel in mates)
                  {
                    var inward=frame.YAxis*(panel.BandIndex==seam?-1:1);
                    var origin=midpoint;
                    bool allowance=_seam>tolerance;
                    var offsetDirection=allowance?-inward:inward;
                    double inset=allowance?_seam*ConfiguredAllowanceLabelPositionFactor:style.Height*ConfiguredLabelInsetHeightFactor;
                    for(int attempt=0;attempt<LabelInsetAttempts;attempt++,inset*=0.5)
                    {
                      var candidate=midpoint+offsetDirection*inset;
                      if(panel.Cut.Contains(candidate,state.Part.Plane,tolerance)==PointContainment.Inside&&
                        (!allowance||!panel.Preview.Any(outline=>outline.Contains(candidate,state.Part.Plane,tolerance)==PointContainment.Inside)))
                      { origin=candidate; break; }
                    }
                    var plane=MatchingLabels.FacingPlane(doc,origin,frame.XAxis,offsetDirection,midpoint);
                    if(!plane.IsValid) throw new InvalidOperationException("a seam label has no valid facing direction.");
                    var text=MatchingLabels.CreateText(plane,next,style.Height);
                    text.DrawForward=false;
                    panel.Labels.Add(new SeamLabel(seam,segment,next,text));
                  }
                  next=MatchingLabels.Next(next,style.AutoAdvance);segment++;
                }
              }
              finally { foreach(var piece in pieces) piece.Dispose(); }
            }
          }
        }
        catch(Exception ex)
        { ClearLabels(state);next=start;state.LabelError=ex.Message;Log.Write(Section,"label preview rejected: {0}",ex.Message); }
        state.NextLabel=next;
      }
      return next;
    }

    private static void DrawBatch(DisplayPipeline display,RhinoDoc doc,IReadOnlyList<State> states,Plane cplane,Point3d anchor,double tolerance,MatchingLabels.Style labelStyle)
    {
      EnsureLabels(doc,states,labelStyle,tolerance);
      var transforms=BatchPlacements(states,cplane,anchor);
      int plot=doc.Layers.FindByFullPath(SewLayer,-1);
      var plotColor=plot>=0?doc.Layers[plot].Color:DefaultSewColor;
      int reference=doc.Layers.FindByFullPath(ReferenceLayer,-1);
      var referenceColor=reference>=0?doc.Layers[reference].Color:CutPreviewColor;
      for(int index=0;index<states.Count;index++)
        Draw(display,states[index].Part,states[index].Panels,transforms[index],plotColor,referenceColor,CachedCutPreviews(states[index],tolerance));
    }

    private static void Draw(DisplayPipeline display, Part part,IReadOnlyList<Panel> panels,
      IReadOnlyList<Transform> transforms,Color plotColor,Color referenceColor,IReadOnlyList<CutPreview> cuts)
    {
      foreach(var cut in cuts)
        foreach(var segment in cut.Segments)PreviewDisplay.DrawCurve(display,segment,CutPreviewColor);
      foreach(var label in panels.SelectMany(panel=>panel.Labels))
        display.DrawAnnotation(label.Text,Color.FromArgb(ConfiguredPreviewAlpha,referenceColor));
      for(int index=0;index<panels.Count;index++)
      {
        var panel=panels[index];
        display.PushModelTransform(transforms[index]);
        try
        {
          var color=Color.FromArgb(ConfiguredPreviewAlpha,part.Items.First(item=>item.Attributes.LayerIndex==part.Attributes.LayerIndex).Color);
          PreviewDisplay.DrawCurve(display,panel.Cut,color);
          foreach(var sew in panel.SewLines) PreviewDisplay.DrawCurve(display,sew,Color.FromArgb(ConfiguredPreviewAlpha,plotColor));
          foreach(var label in panel.Labels) display.DrawAnnotation(label.Text,Color.FromArgb(ConfiguredPreviewAlpha,plotColor));
          foreach(var item in panel.Details)
          {
            var faded=Color.FromArgb(ConfiguredPreviewAlpha,item.Color);
            switch(item.Geometry)
            {
              case Curve curve: PreviewDisplay.DrawCurve(display,curve,faded); break;
              case AnnotationBase text: display.DrawAnnotation(text,faded); break;
              case Rhino.Geometry.Point point: display.DrawPoint(point.Location,PointStyle.RoundSimple,ConfiguredPreviewPointSize,faded); break;
              case TextDot dot: display.DrawDot(dot.Point,dot.Text,faded,Color.White); break;
            }
          }
        }
        finally { display.PopModelTransform(); }
      }
    }

    private static bool Commit(RhinoDoc doc, Part part, PlanarPartSplitter.Proposal proposal,
      IReadOnlyList<Panel> panels, Plane cplane, Point3d anchor, double tolerance,
      out Output? output,IReadOnlyList<Transform> transforms,IReadOnlyList<int>? order=null,SplitDirection direction=DefaultDirection,IReadOnlyList<LocalCut>? localCuts=null,MatchingLabels.Style? labelStyle=null)
    {
      output=null;
      var added=new List<Guid>(); var groups=new List<int>(); var session=Guid.NewGuid().ToString("N");
      try
      {
        var plot=doc.Layers.FindByFullPath(SewLayer,-1);
        if(plot<0) plot=UzipCommon.EnsureLayer(doc,SewLayer,DefaultSewColor);
        order??=PanelOrder(panels,proposal,direction);
        for(int number=0; number<order.Count; number++)
        {
          int index=order[number];
          var panel=panels[index]; var transform=transforms[index];
          var group=doc.Groups.Add(GroupPrefix+session+"_"+(number+1));
          if(group<0) throw new InvalidOperationException("could not create a part group");
          groups.Add(group);
          void Add(GeometryBase geometry,ObjectAttributes source,string? name)
          {
            using var copy=geometry.Duplicate(); copy.Transform(transform);
            using var attributes=source.Duplicate(); attributes.RemoveFromAllGroups(); attributes.AddToGroup(group);
            if(name!=null) attributes.Name=name;
            attributes.SetUserString(MetadataPrefix+"session",session);
            attributes.SetUserString(MetadataPrefix+"source",part.Attributes.ObjectId.ToString());
            attributes.SetUserString(MetadataPrefix+"part",(number+1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MetadataPrefix+"direction",direction.ToString());
            attributes.SetUserString(MetadataPrefix+"width",_width.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MetadataPrefix+"seam",_seam.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MetadataPrefix+"layout",LayoutNames[_layout]);
            attributes.SetUserString(MetadataPrefix+"distance",_distance.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            var id=doc.Objects.Add(copy,attributes);
            if(id==Guid.Empty) throw new InvalidOperationException("could not add split geometry");
            added.Add(id);
          }
          Add(panel.Cut,part.Attributes,BoundaryName);
          foreach(var item in panel.Details) Add(item.Geometry,item.Attributes,null);
          using var sewAttributes=new ObjectAttributes {LayerIndex=plot,Name=SewName};
          foreach(var sew in panel.SewLines) Add(sew,sewAttributes,SewName);
          if(panel.Labels.Count>0)
          {
            if(labelStyle==null) throw new InvalidOperationException("label settings are unavailable");
            int labelLayer=string.IsNullOrWhiteSpace(labelStyle.Layer)||LayerSelector.IsCurrentLayerValue(labelStyle.Layer,DuplicateCommandSupport.CurrentLayerOption)
              ?doc.Layers.CurrentLayerIndex:UzipCommon.EnsureLayer(doc,labelStyle.Layer);
            if(labelLayer<0) throw new InvalidOperationException("could not create the seam label layer");
            foreach(var label in panel.Labels)
            {
              using var attributes=new ObjectAttributes {LayerIndex=labelLayer,Name=SeamLabelName};
              attributes.SetUserString(NotchRoleKey,"label");
              attributes.SetUserString(MetadataPrefix+"labelValue",label.Value);
              attributes.SetUserString(MetadataPrefix+"labelPair",$"{session}:{label.Seam}:{label.Segment}");
              Add(label.Text,attributes,SeamLabelName);
            }
          }
        }
        var cuts=new List<Guid>();
        var reference=UzipCommon.EnsureLayer(doc,ReferenceLayer);
        for(int seam=0;seam<proposal.Seams.Count;seam++)
        {
          using var cut=SplitLine(part,proposal,localCuts,seam,tolerance);
          if(cut==null) continue;
          foreach(var segment in PlanarPartSplitter.ClipCurve(cut,part.Outline,proposal.Frame,tolerance))
            using(segment)
            {
              var attributes=new ObjectAttributes { LayerIndex=reference,Name=CutName };
              attributes.SetUserString(MetadataPrefix+"session",session);
              var id=doc.Objects.AddCurve(segment,attributes);
              if(id==Guid.Empty) throw new InvalidOperationException("could not add source split cuts");
              added.Add(id); cuts.Add(id);
            }
        }
        foreach(var label in panels.SelectMany(panel=>panel.Labels))
        {
          using var attributes=new ObjectAttributes {LayerIndex=reference,Name=ReferenceLabelName};
          attributes.SetUserString(NotchRoleKey,"label");
          attributes.SetUserString(MetadataPrefix+"session",session);
          attributes.SetUserString(MetadataPrefix+"source",part.Attributes.ObjectId.ToString());
          attributes.SetUserString(MetadataPrefix+"labelValue",label.Value);
          attributes.SetUserString(MetadataPrefix+"labelPair",$"{session}:{label.Seam}:{label.Segment}");
          var id=doc.Objects.AddText(label.Text,attributes);
          if(id==Guid.Empty) throw new InvalidOperationException("could not add source seam labels");
          added.Add(id);cuts.Add(id);
        }
        if(cuts.Count>0)
        {
          var common=part.Items[0].Attributes.GetGroupList()?.ToHashSet()??[];
          foreach(var item in part.Items.Skip(1)) common.IntersectWith(item.Attributes.GetGroupList()??[]);
          int group=common.OrderBy(value=>doc.Groups.GroupObjectCount(value)).FirstOrDefault(-1);
          if(group<0) { group=doc.Groups.Add(GroupPrefix+session+"_source"); groups.Add(group); doc.Groups.AddToGroup(group,part.Items.Select(item=>item.Id)); }
          doc.Groups.AddToGroup(group,cuts);
        }
        Log.Write("vPartSplit","committed session={0} parts={1} objects={2} source preserved=True",session,panels.Count,added.Count);
        output=new Output(added,groups,added.Except(cuts).ToList());
        return true;
      }
      catch(Exception ex)
      {
        foreach(var id in added) doc.Objects.Delete(id,true);
        foreach(var group in groups) doc.Groups.Delete(group);
        Log.Write("vPartSplit","commit rolled back: {0}",ex);
        RhinoApp.WriteLine("vPartSplit: creation failed; original part preserved.");
        return false;
      }
    }
  }
}
