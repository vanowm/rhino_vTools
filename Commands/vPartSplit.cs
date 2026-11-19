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
    private const double IntermediateWidthLimit = double.MaxValue; // Manual-edit build sentinel; oversized output requires explicit confirmation before placement.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.CreateOversizedDefault", "true defaults the oversized-piece confirmation to CreateAnyway; false defaults to BackToSplits. Confirmation is always shown.")]
    private const bool CreateOversizedDefault = false; // false defaults to returning to split editing; true defaults to creating oversized pieces after confirmation.
    private static readonly bool ConfiguredCreateOversizedDefault = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.CreateOversizedDefault", CreateOversizedDefault); // Resolved once from vSettings; confirmation remains mandatory.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.OriginalSpacingPasses", "Maximum clearance-refinement passes for corner-ending splits in Original layout; 1 through 64.")]
    private const int OriginalSpacingPasses = 32; // Bisection passes to find non-overlapping finite-piece spacing; 1 through 64.
    private static readonly int ConfiguredOriginalSpacingPasses = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.OriginalSpacingPasses", OriginalSpacingPasses); // Resolved once from advanced defaults; restart Rhino after changes.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.AllowanceBooleanToleranceFactor", "Fraction of model tolerance for joining new allowances to nearly coincident original cut edges; greater than zero through one.")]
    private const double AllowanceBooleanToleranceFactor = 0.01; // Computational Boolean tolerance relative to model tolerance; greater than zero through one, without changing the document tolerance.
    private static readonly double ConfiguredAllowanceBooleanToleranceFactor = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.AllowanceBooleanToleranceFactor", AllowanceBooleanToleranceFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.PickRadius", "Maximum screen-pixel distance for dragging the body of a split line; positive.")]
    private const double PickRadius = 14.0; // Maximum cursor distance in screen pixels for picking a cut body.
    private static readonly double ConfiguredPickRadius = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.PickRadius", PickRadius); // Resolved once from vSettings.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.EndpointPickRadius", "Screen-pixel radius around split endpoints for two-click movement; positive, independent of the line-body radius.")]
    private const double EndpointPickRadius = 18.0; // Screen-pixel endpoint hit radius; endpoint handles take precedence over line bodies.
    private static readonly double ConfiguredEndpointPickRadius = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.EndpointPickRadius", EndpointPickRadius); // Resolved once from vSettings.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.EndpointHandleSize", "Split endpoint marker diameter in screen pixels; positive integer.")]
    private const int EndpointHandleSize = 7; // Visible endpoint marker diameter in pixels; independent of zoom.
    private static readonly int ConfiguredEndpointHandleSize = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.EndpointHandleSize", EndpointHandleSize); // Resolved once from vSettings.
    private const int SplitReviewElevatorMode = 0; // Native mode sentinel: 0 disables Ctrl-based elevator constraints because Ctrl-click removes a split.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DragRefreshMilliseconds", "Minimum interval between clipped-preview updates while dragging.")] private const int DragRefreshMilliseconds = 30; // Minimum milliseconds between incremental panel rebuilds; zero updates every mouse event.

    private static readonly int ConfiguredDragRefreshMilliseconds = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DragRefreshMilliseconds", DragRefreshMilliseconds); // Resolved once from advanced defaults; restart Rhino after changes.
    private const bool AllowObjectSnaps = true; // true resolves native object snaps and displays snap cues while dragging cuts; false uses unsnapped points.
    private const int NormalAlignmentPasses = 6; // Maximum tangent-bisector updates for each automatic cross-cut; positive integer.
    private const double MinimumProgressCosine = 0.1; // Minimum absolute dot product of a local cut normal with the part's overall progression; zero through one.
    private const double LocalWidthReduction = 0.9; // Effective-width multiplier when a MaxWidth cross-cut panel is too wide; greater than zero and less than one.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.AutoFitStations", "Initial interval divisions for fit-box-based automatic splitting; 4 through 64. Higher values search more cut positions before exact validation.")]
    private const int AutoFitStations = 16; // Initial division count for fitted-width cut search; 4 through 64.
    private static readonly int ConfiguredAutoFitStations = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.AutoFitStations", AutoFitStations); // Resolved once from vSettings.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.AutoFitRefinementPasses", "Maximum continuous cut-position bisection passes per piece and direction; 1 through 40. Stops sooner at document tolerance.")]
    private const int AutoFitRefinementPasses = 20; // Bisection passes for maximal fitting intervals between sampled stations; 1 through 40.
    private static readonly int ConfiguredAutoFitRefinementPasses = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.AutoFitRefinementPasses", AutoFitRefinementPasses); // Resolved once from vSettings.
    private const int MaximumAutoFitRefinementPasses = 40; // Maximum bisection depth; bounds automatic refinement work.
    private const int MinimumAutoFitStations = 4; // Minimum useful subdivision count for fitted-width split search.
    private const int MaximumAutoFitStations = 64; // Maximum initial division count; bounds the automatic interval-search cost.
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
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.InvalidCutPreviewColor", "RGB color for an endpoint position that cannot preserve valid, noncrossing split geometry.", "#FFFF0000")]
    private static readonly Color InvalidCutPreviewColor = CommandDefaults.GetColor("vPartSplit", "vTools.Commands.PartSplitWorkflow.InvalidCutPreviewColor", Color.Red); // RGB rejected-endpoint cue; invalid clicks do not accept or change the last valid panels.
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
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultNotches", "true adds paired seam notches using vNotches settings; false omits seam notches independently of Label.",OptionKeys="notches")]
    private const bool DefaultNotches = true; // true creates matching start/end and intermediate seam notches; false retains labels only when Label is enabled.
    private static readonly bool ConfiguredDefaultNotches = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.DefaultNotches", DefaultNotches); // Initial Notches toggle, available at every stage.
    private const string SeamNotchType = "I"; // Split-seam notches are always straight slits, independent of vNotches' selected type.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.MiddleNotchThresholdInches", "Add at least one intermediate seam notch when the clear start-to-end span exceeds this many inches; nonnegative.")]
    private const double MiddleNotchThresholdInches = 40; // Clear span threshold in inches; longer seams receive intermediate matching notches.
    private static readonly double ConfiguredMiddleNotchThresholdInches = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.MiddleNotchThresholdInches", MiddleNotchThresholdInches); // Converted to document units at placement.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.MinimumNotchSpacingInches", "Preferred minimum spacing between intermediate seam notches in inches; positive and no greater than the maximum.")]
    private const double MinimumNotchSpacingInches = 20; // Minimum desired interval in inches for subdivided seams; short end-to-end spans are exempt.
    private static readonly double ConfiguredMinimumNotchSpacingInches = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.MinimumNotchSpacingInches", MinimumNotchSpacingInches); // Converted to document units at placement.
    [CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.MaximumNotchSpacingInches", "Maximum spacing between seam notches in inches; at least the configured minimum.")]
    private const double MaximumNotchSpacingInches = 48; // Maximum desired interval in inches for subdivided seams.
    private static readonly double ConfiguredMaximumNotchSpacingInches = CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.MaximumNotchSpacingInches", MaximumNotchSpacingInches); // Converted to document units at placement.
    private const string SeamLabelName = "PartSplitSeamLabel"; // Object name identifying new matching seam annotations.
    private const string ReferenceLabelName = "PartSplitReferenceLabel"; // Object name for opposing seam annotations on the retained source part's Reference layer.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.AllowanceLabelPositionFactor", "Fraction of Seam from the nominal split toward the allowance cut edge; zero through one.")] private const double AllowanceLabelPositionFactor = 0.5; // Fraction of Seam from the nominal split toward the allowance cut edge; zero through one.

    private static readonly double ConfiguredAllowanceLabelPositionFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.AllowanceLabelPositionFactor", AllowanceLabelPositionFactor); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vPartSplit", "vTools.Commands.PartSplitWorkflow.LabelInsetHeightFactor", "Positive text-height inset into the finished panel only when Seam is zero.")] private const double LabelInsetHeightFactor = 1.0; // Positive text-height inset into the finished panel only when Seam is zero.

    private static readonly double ConfiguredLabelInsetHeightFactor = global::vTools.CommandDefaults.Get("vPartSplit", "vTools.Commands.PartSplitWorkflow.LabelInsetHeightFactor", LabelInsetHeightFactor); // Resolved once from advanced defaults; restart Rhino after changes.
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
    private static bool _notches = ConfiguredDefaultNotches;
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
      internal MatchingNotches.Settings? MarkerStyle {get;set;}
      internal bool MarkerNotches {get;set;}
      internal List<Panel>? LabelPanels { get; set; }
      internal string LabelStart { get; set; } = "";
      internal string NextLabel { get; set; } = "";
      internal string? LabelError { get; set; }
      internal PlanarPartSplitter.Proposal? CachedProposal;
      internal List<LocalCut>? CachedCuts;
      internal List<CutPreview> CutPreviews { get; } = [];
      internal OriginalSpacing? Spacing;
      public void Dispose()
      {
        foreach(var panel in Panels)panel.Dispose();foreach(var cut in CutPreviews)cut.Dispose();
        if(SourceCache.TryGetValue(Part,out var source)){source.Dispose();SourceCache.Remove(Part);}
        Part.Dispose();
      }
    }
    private sealed record Output(List<Guid> Added, List<int> Groups, List<Guid> Copies);
    private sealed class PlacementConfirmationPreview(Action<DisplayPipeline> draw) : DisplayConduit,IDisposable
    {
      protected override void DrawForeground(DrawEventArgs e)=>draw(e.Display);
      public void Dispose(){Enabled=false;}
    }
    private sealed record LocalCut(Plane Frame)
    {internal Point3d? Start {get;init;} internal Point3d? End {get;init;}}
    private sealed record SourceItem(Item Item,BoundingBox Bounds,bool Boundary);
    private sealed record SourceDetails(List<SourceItem> Items,double Area,Curve? FinishedBoundary) : IDisposable
    {
      internal double NominalArea {get;init;}=Area; // Finished-boundary area when available; otherwise the outer cutting area.
      internal double Tolerance {get;init;}
      public void Dispose(){FinishedBoundary?.Dispose();GC.SuppressFinalize(this);}
      ~SourceDetails(){FinishedBoundary?.Dispose();}
    }
    private sealed record OriginalSpacing(List<Panel> Panels,double Seam,double Distance,Vector3d[] Offsets);
    private sealed record FittedInterval(PlanarFit.Fit Fit,double Length);
    private sealed record CutHit(int Seam,int Endpoint,double Distance); // Endpoint: 0=start, 1=end, -1=line body.
    private sealed record CutPreview(Curve Line,List<Curve> Segments) : IDisposable
    { public void Dispose(){Line.Dispose();foreach(var segment in Segments)segment.Dispose();} }
    private sealed class CutMove : IDisposable
    {
      private readonly State _state;
      private readonly PlanarPartSplitter.Proposal _beforeProposal;
      private readonly List<LocalCut>? _beforeCuts;
      private readonly List<Panel> _beforePanels;
      private readonly int _beforeActive;
      private readonly Plane _frame;
      private readonly double _grabY,_seamY,_tolerance;
      private Point3d _lastAttempt=Point3d.Unset;
      private bool _valid,_accepted,_disposed;
      internal int Seam {get;private set;}
      internal bool Pivoting {get;}
      internal Point3d Pivot {get;}
      internal Point3d Candidate=>_lastAttempt;
      internal bool Valid=>_valid;
      internal CutMove(State state,int seam,Point3d grab,bool pivoting,double tolerance)
      {
        _state=state;Seam=seam;Pivoting=pivoting;_tolerance=tolerance;
        _beforeProposal=state.Proposal;_beforeCuts=state.Cuts;_beforePanels=state.Panels;_beforeActive=state.ActiveCut;
        var cut=CachedCutPreviews(state,tolerance)[seam].Line;grab=state.Part.Plane.ClosestPoint(grab);
        _frame=state.Cuts?[seam].Frame??state.Proposal.Frame;_seamY=state.Cuts==null?state.Proposal.Seams[seam]:0;
        _grabY=PlanarPartSplitter.FrameY(_frame,grab);
        Pivot=grab.DistanceTo(cut.PointAtStart)<=grab.DistanceTo(cut.PointAtEnd)?cut.PointAtEnd:cut.PointAtStart;
        state.ActiveCut=seam;state.Dragging=true;
      }
      internal bool Update(Point3d point)
      {
        if(_disposed||!point.IsValid)return false;
        var projected=_state.Part.Plane.ClosestPoint(point);
        var boundary=SplitBoundary(_state.Part,_tolerance);
        if(Pivoting&&boundary.ClosestPoint(projected,out double parameter))projected=boundary.PointAt(parameter);
        if(_lastAttempt.IsValid&&_lastAttempt.DistanceTo(projected)<=RhinoMath.ZeroTolerance)return _valid;
        _lastAttempt=projected;_valid=false;
        var target=Pivoting?Pivot:_frame.PointAt(0,_seamY+PlanarPartSplitter.FrameY(_frame,projected)-_grabY);
        var direction=projected-Pivot;
        if(Pivoting&&direction.Length<=_tolerance)return false;
        if(Pivoting&&direction*_frame.XAxis<0)direction=-direction;
        var local=_state.Cuts??_state.Proposal.Seams.Select(position=>new LocalCut(
          new Plane(_state.Proposal.Frame.PointAt(0,position),_state.Proposal.Frame.XAxis,_state.Proposal.Frame.YAxis))).ToList();
        var across=Pivoting?direction:_frame.XAxis;if(!across.Unitize())return false;
        var cuts=local.ToList();cuts[Seam]=new(new Plane(Pivoting?Pivot:target,across,Vector3d.CrossProduct(_state.Part.Plane.Normal,across)))
          {Start=Pivoting?Pivot:null,End=Pivoting?projected:null};
        var panels=BuildLocalPanelsCore(_state.Part,_state.Proposal,cuts,EditingSettings(_state,_tolerance),_tolerance,out _,_state.Cuts==null?null:_state.Panels,Seam);
        if(panels.Count==0)return false;
        ReplacePanelsCore(_state,panels,_beforePanels);_state.Cuts=cuts;return _valid=true;
      }
      internal void Accept()=>_accepted=true;
      public void Dispose()
      {
        if(_disposed)return;_disposed=true;
        if(_accepted)
        {foreach(var panel in _beforePanels)if(!_state.Panels.Contains(panel))panel.Dispose();}
        else
        {
          foreach(var panel in _state.Panels)if(!_beforePanels.Contains(panel))panel.Dispose();
          _state.Proposal=_beforeProposal;_state.Cuts=_beforeCuts;_state.Panels=_beforePanels;_state.ActiveCut=_beforeActive;_state.LabelPanels=null;
        }
        _state.Dragging=false;
      }
    }
    private sealed record SeamLabel(int Seam, int Segment, string Value, TextEntity Text)
    {internal SeamMarker? Marker {get;init;}}
    private sealed record SeamMarker(int Seam,int Segment,string Value,Guid Match,Curve Source,double Length,
      Vector3d Direction,List<Curve> Geometry,MatchingNotches.Settings Style,Color Color) : IDisposable
    {public void Dispose(){Source.Dispose();foreach(var curve in Geometry)curve.Dispose();}}
    private sealed class Panel(Curve cut, BoundingBox bounds, List<Curve> preview, int bandIndex) : IDisposable
    {
      internal Curve Cut { get; } = cut;
      internal BoundingBox Bounds { get; } = bounds;
      internal int BandIndex { get; } = bandIndex;
      internal Plane Frame { get; set; } = Plane.Unset;
      internal double NominalArea { get; set; }
      internal HashSet<int> Seams {get;}=[];
      internal bool Finite {get;init;}
      internal PlanarFit.Fit? CompactFit { get; set; }
      internal Plane CompactSeed { get; set; } = Plane.Unset;
      internal double CompactWidth { get; set; } = double.NaN; // NaN means no width-specific compact layout has been cached.
      internal bool CompactRefined { get; set; }
      internal PlanarFit.Fit? EditingFit { get; set; } // Accepted rotated box when the cut-derived height alone exceeds Width.
      internal double EditingFitLimit { get; set; } = double.NaN; // Document-unit Width used for the cached fit; NaN means untested.
      internal double EditingFitTolerance { get; set; } = double.NaN; // Document-unit tolerance used by the cached fit.
      internal List<Curve> Preview { get; } = preview;
      internal List<Item> Details { get; } = [];
      internal List<Curve> SewLines { get; } = [];
      internal List<SeamLabel> Labels { get; } = [];
      internal List<SeamMarker> Markers {get;}=[];
      public void Dispose()
      {
        Cut.Dispose(); foreach (var curve in Preview) curve.Dispose();
        foreach (var curve in SewLines) curve.Dispose();
        foreach (var label in Labels) label.Text.Dispose();
        foreach(var marker in Markers)marker.Dispose();
        foreach (var item in Details) item.Geometry.Dispose();
      }
    }
    private sealed record Options(OptionDouble Width, OptionDouble Seam, int ModeIndex,
      OptionDouble Distance, int LayoutIndex, int AutoIndex, int DirectionIndex, OptionToggle Label, int StartLabelIndex, int AddSplitIndex,OptionToggle Notches);

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
      _notches = ToolsOptionStore.TryGetBool(section,"notches",out var notches) ? notches : ConfiguredDefaultNotches;
      if (!ValidOptions(_width, _seam)) { _width = ConfiguredDefaultWidth; _seam = ConfiguredDefaultSeam; }
      return 0;
    });

    private static void Save() => ToolsOptionStore.Update(Section, section =>
    { section["width"] = _width; section["seam"] = _seam; section["mode"] = _mode;
      section["layout"] = _layout; section["distance"] = _distance; section["direction"] = _direction.ToString(); section["label"] = _label;section["notches"]=_notches; });

    private static bool ValidOptions(double width, double seam) =>
      double.IsFinite(width) && double.IsFinite(seam) && seam >= 0 && width > 2 * seam + RhinoMath.ZeroTolerance;

    private static Options AddOptions(GetBaseClass getter, double tolerance, bool editing = false, SplitDirection? direction = null, string? startLabel = null)
    {
      var width = new OptionDouble(_width, tolerance, double.MaxValue);
      var seam = new OptionDouble(_seam, 0, double.MaxValue);
      getter.AddOptionDouble("Width", ref width);
      getter.AddOptionDouble("Seam", ref seam);
      var label = new OptionToggle(_label,"No","Yes"); getter.AddOptionToggle("Label",ref label);
      var notches = new OptionToggle(_notches,"No","Yes");getter.AddOptionToggle("Notches",ref notches);
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
      return new Options(width, seam, mode, distance, layout, auto, directionIndex, label, startLabelIndex, addSplit,notches);
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
      _notches = options.Notches.CurrentValue;
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
      (int Part,int Seam,int Endpoint) Find(RhinoViewport viewport,int x,int y)
      {
        int selected=-1;CutHit? best=null;
        for(int index=0;index<states.Count;index++)
        {
          var state=states[index];
          var hit=FindCutHit(viewport,x,y,CachedCutPreviews(state,tolerance));
          if(hit!=null&&BetterHit(hit,best)){best=hit;selected=index;}
        }
        return best==null?(-1,-1,-1):(selected,best.Seam,best.Endpoint);
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
          while(true)
          {
            using var add=new GetPoint();
            add.SetCommandPrompt("Location for additional cross-cut");
            add.Constrain(states[active].Part.Plane,false);
            add.PermitObjectSnap(AllowObjectSnaps);add.EnableObjectSnapCursors(AllowObjectSnaps);
            var addControls=Configure(add);
            int candidatePart=-1;LocalCut? candidate=null;Point3d last=Point3d.Unset;
            void Update(Point3d point)
            {
              if(last.IsValid&&last.DistanceTo(point)<=tolerance)return;
              last=point;candidatePart=-1;candidate=null;
              for(int index=0;index<states.Count;index++)
                if(SplitBoundary(states[index].Part,tolerance).Contains(point,states[index].Part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)
                { candidatePart=index;candidate=AlignCut(states[index].Part,states[index].Proposal,PlanarPartSplitter.FrameY(states[index].Proposal.Frame,point),tolerance);break; }
            }
            add.DynamicDraw+=(_,e)=>
            {
              Update(e.CurrentPoint);DrawBatch(e.Display,doc,states,cplane,anchor,tolerance,labelStyle);
              if(candidate!=null)
                using(var line=PlanarPartSplitter.Seam(SplitBoundary(states[candidatePart].Part,tolerance),candidate.Frame,0,tolerance))
                  if(line!=null)PreviewDisplay.DrawCurve(e.Display,line,CutPreviewColor);
            };
            var addResult=add.Get();
            if(addResult is GetResult.Option or GetResult.Number)
            {
              if(addResult!=GetResult.Option||add.Option()?.Index!=addControls.Values.AddSplitIndex)Change(add,addResult,addControls);
              continue;
            }
            if(addResult==GetResult.Point)
            {
              Update(add.Point());
              if(candidatePart>=0&&candidate!=null&&TryInsertCut(states[candidatePart],candidate,tolerance))active=candidatePart;
              else RhinoApp.WriteLine("vPartSplit: cut must cross a part and remain separate from other cuts.");
            }
            break;
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
        while(true)
        {
          using var getter=new GetPoint();
          getter.SetCommandPrompt("Select split endpoint or drag cut; Enter to place parts");
          getter.PermitElevatorMode(SplitReviewElevatorMode);
          getter.PermitObjectSnap(AllowObjectSnaps); getter.EnableObjectSnapCursors(AllowObjectSnaps); getter.AcceptNothing(true);
          var controls=Configure(getter);
          int movingPart=-1;
          (int Part,int Seam,int Endpoint) hovered=(-1,-1,-1);
          bool dragging=false;
          CutMove? move=null;
          long refreshed=0;
          try
          {
          getter.MouseMove+=(_,e)=>
          {
            dragging=move is {Pivoting:false}&&e.LeftButtonDown;
            if(move==null&&!e.LeftButtonDown)
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
            movingPart=selected.Part;
            if(movingPart<0) return;
            active=movingPart; var state=states[active];
            if(e.ControlKeyDown)
            {
              ClearHover();
              if(!TryRemoveCut(state,selected.Seam,tolerance))RhinoApp.WriteLine("vPartSplit: split could not be removed; previous preview retained.");
              return;
            }
            var cut=CachedCutPreviews(state,tolerance)[selected.Seam].Line;
            bool pivoting=selected.Endpoint>=0;
            var grab=selected.Endpoint==0?cut.PointAtStart:selected.Endpoint==1?cut.PointAtEnd:e.Point;
            move?.Dispose();move=new(state,selected.Seam,grab,pivoting,tolerance);
            getter.Constrain(state.Part.Plane,false);ClearHover();
            dragging=!pivoting;
          };
          getter.DynamicDraw+=(_,e)=>
          {
            var now=System.Environment.TickCount64;
            if(dragging&&now-refreshed>=ConfiguredDragRefreshMilliseconds) { refreshed=now;move?.Update(e.CurrentPoint); }
            DrawBatch(e.Display,doc,states,cplane,anchor,tolerance,labelStyle);
            if(movingPart>=0&&move!=null)
              foreach(var segment in CachedCutPreviews(states[movingPart],tolerance)[move.Seam].Segments)
                PreviewDisplay.DrawCurve(e.Display,segment,CutPreviewColor);
          };
          var result=getter.Get(onMouseUp:true); ClearHover();
          if(result is GetResult.Option or GetResult.Number) { move?.Dispose();move=null;Change(getter,result,controls);continue; }
          if(result==GetResult.Nothing)break;
          if(result!=GetResult.Point) return Result.Cancel;
          if(move==null)continue;
          if(move.Pivoting)
          {
            var endpointMove=move;
            while(true)
            {
              using var destination=new GetPoint();destination.SetCommandPrompt("Split endpoint destination; Esc cancels this move");
              destination.Constrain(states[movingPart].Part.Plane,false);
              destination.PermitOrthoSnap(false);destination.PermitTabMode(false);destination.PermitElevatorMode(SplitReviewElevatorMode);
              destination.PermitObjectSnap(AllowObjectSnaps);destination.EnableObjectSnapCursors(AllowObjectSnaps);refreshed=0;
              var destinationControls=Configure(destination);
              destination.DynamicDraw+=(_,e)=>
              {
                long now=System.Environment.TickCount64;
                if(now-refreshed>=ConfiguredDragRefreshMilliseconds){refreshed=now;endpointMove.Update(e.CurrentPoint);}
                DrawBatch(e.Display,doc,states,cplane,anchor,tolerance,labelStyle);
                if(endpointMove.Candidate.IsValid)
                {
                  var color=endpointMove.Valid?CutPreviewColor:InvalidCutPreviewColor;
                  if(!endpointMove.Valid&&endpointMove.Pivot.DistanceTo(endpointMove.Candidate)>tolerance)e.Display.DrawLine(endpointMove.Pivot,endpointMove.Candidate,color);
                  e.Display.DrawPoint(endpointMove.Candidate,PointStyle.RoundSimple,ConfiguredPreviewPointSize,color);
                }
              };
              var destinationResult=destination.Get();
              if(destinationResult is GetResult.Option or GetResult.Number)
              {move.Dispose();move=null;Change(destination,destinationResult,destinationControls);break;}
              if(destinationResult!=GetResult.Point)break;
              if(endpointMove.Update(destination.Point())){endpointMove.Accept();break;}
              RhinoApp.WriteLine("vPartSplit: that endpoint cannot preserve valid split geometry; choose another point.");
            }
          }
          else
          {if(!move.Update(getter.Point()))RhinoApp.WriteLine("vPartSplit: last valid cut position retained.");move.Accept();}
          ClearHover();
          }
          finally{move?.Dispose();ClearHover();}
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
          if(!ConfirmOversizedParts(states,tolerance,message=>
          {
            using var preview=new PlacementConfirmationPreview(display=>DrawBatch(display,doc,states,cplane,anchor,tolerance,labelStyle)){Enabled=true};
            try
            {
              doc.Views.Redraw();bool create=ConfiguredCreateOversizedDefault;
              return RhinoGet.GetBool(message,true,"BackToSplits","CreateAnyway",ref create)==Result.Success&&create;
            }
            finally{preview.Enabled=false;doc.Views.Redraw();}
          }))break;
          string nextLabel=EnsureLabels(doc,states,labelStyle,tolerance);
          if((_label||_notches)&&states.FirstOrDefault(state=>state.LabelError!=null) is {} invalid)
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
          RhinoApp.WriteLine($"vPartSplit: split {CountText.Format(states.Count, "source part")} into {CountText.Format(states.Sum(state=>state.Panels.Count), "grouped part")}; Layout={LayoutNames[_layout]}, Distance={_distance:G6}.");
          doc.Objects.Select(outputs.SelectMany(output=>output.Copies));
          return Result.Success;
        }
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
      var boundary=SplitBoundary(part,tolerance);
      if(cuts==null) return PlanarPartSplitter.Seam(boundary,proposal.Frame,proposal.Seams[index],tolerance);
      if(cuts[index] is {Start:{} start,End:{} end})return new LineCurve(start,end);
      return PlanarPartSplitter.Seam(boundary,cuts[index].Frame,0,tolerance);
    }
    private static Curve SplitBoundary(Part part,double tolerance)=>GetSourceDetails(part,tolerance).FinishedBoundary??part.Outline;
    private static double EditingWidth(Panel panel,double tolerance)
    {
      double height=panel.Bounds.Max.Y-panel.Bounds.Min.Y;
      if(height<=_width+tolerance)return height;
      if(panel.EditingFitLimit!=_width||panel.EditingFitTolerance!=tolerance)
      {
        panel.EditingFit=panel.CompactWidth==_width&&panel.CompactRefined&&panel.CompactFit is {} displayed&&
          VerticalWidth(displayed.Bounds)<=_width+tolerance?displayed:PlanarFit.FitCurve(panel.Cut,panel.Frame,_width,tolerance);
        panel.EditingFitLimit=_width;panel.EditingFitTolerance=tolerance;
      }
      return panel.EditingFit is {} accepted?Math.Min(accepted.Width,accepted.Height):height;
    }
    private static bool FitsWidth(State state,double tolerance)=>state.Panels.All(panel=>EditingWidth(panel,tolerance)<=_width+tolerance);
    private static bool ConfirmOversizedParts(IReadOnlyList<State> states,double tolerance,Func<string,bool> confirm)
    {
      var widths=states.SelectMany(state=>state.Panels).Select(panel=>EditingWidth(panel,tolerance)).Where(width=>width>_width+tolerance).ToArray();
      if(widths.Length==0)return true;
      bool accepted=confirm($"{CountText.Format(widths.Length, "piece")} {CountText.Form(widths.Length, "exceeds", "exceed")} Width={_width:G6}; largest measured width={widths.Max():G6}. Create anyway");
      Log.Write("vPartSplit","oversized placement pieces={0} width={1:G6} largest={2:G6} accepted={3}",widths.Length,_width,widths.Max(),accepted);
      return accepted;
    }
    private static PlanarPartSplitter.Settings EditingSettings(State state,double tolerance)=>
      Settings(state.Direction) with{Width=IntermediateWidthLimit};

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
      var boundary=SplitBoundary(part,tolerance);
      if(!CrossSection(boundary,frame,tolerance,out var a,out var b,out _)) return new(frame);
      var center=a+(b-a)*0.5;
      frame=new Plane(center,frame.XAxis,frame.YAxis);
      for(int pass=0;pass<NormalAlignmentPasses;pass++)
      {
        if(!CrossSection(boundary,frame,tolerance,out _,out _,out var along))break;
        if(along*proposal.Frame.YAxis<0)along=-along;
        if(along*proposal.Frame.YAxis<MinimumProgressCosine)break;
        var candidate=new Plane(center,Vector3d.CrossProduct(along,part.Plane.Normal),along);
        if(!CrossSection(boundary,candidate,tolerance,out _,out _,out _))break;
        frame=candidate;
      }
      return new(frame);
    }

    private static bool OrderedCuts(Part part,IReadOnlyList<LocalCut> cuts,double tolerance)
    {
      var lines=new List<Curve>();
      try
      {
        var boundary=SplitBoundary(part,tolerance);
        foreach(var cut in cuts)
        {
          var line=cut is {Start:{} start,End:{} end}?new LineCurve(start,end):PlanarPartSplitter.Seam(boundary,cut.Frame,0,tolerance);
          if(line==null)return false;
          if(Coincident(line,boundary,tolerance)){line.Dispose();return false;}
          if(cut.Start.HasValue&&cut.End.HasValue)
          {
            var pieces=PlanarPartSplitter.ClipCurve(line,boundary,part.Plane,tolerance);
            try{if(Math.Abs(pieces.Sum(piece=>piece.GetLength())-line.GetLength())>tolerance){line.Dispose();return false;}}
            finally{foreach(var piece in pieces)piece.Dispose();}
          }
          foreach(var other in lines)
          {
            using var hits=Intersection.CurveCurve(line,other,tolerance,tolerance);
            if(hits!=null&&hits.Any(hit=>hit.IsOverlap||boundary.Contains(hit.PointA,part.Plane,tolerance)==PointContainment.Inside))
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
        var finished=source.FinishedBoundary??part.Outline;
        bool finite=cuts.Any(cut=>NeedsFiniteCut(finished,cut,tolerance));
        if(finite)return BuildEndpointPanels(part,proposal,cuts,settings,tolerance,out maximumWidth);
        if(previous?.Any(panel=>panel.Finite)==true)previous=null;
        for(int band=0;band<=cuts.Count;band++)
        {
          if(previous!=null&&band!=changedSeam&&band!=changedSeam+1)
          {
            foreach(var panel in previous.Where(panel=>panel.BandIndex==band))
            { panels.Add(panel);nominalArea+=panel.NominalArea;maximumWidth=Math.Max(maximumWidth,panel.Bounds.Max.Y-panel.Bounds.Min.Y); }
            continue;
          }
          var nominal=ClipLocalBand(finished,cuts,band,0,tolerance);
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
                panel.Seams.Add(index);
                using var sew=SplitLine(part,proposal,cuts,index,tolerance);
                if(sew!=null)panel.SewLines.AddRange(ClipSewLine(part,sew,cut,tolerance));
              }
            }
            if(panels.Count>firstPanel)panels[firstPanel].NominalArea=bandArea;
          }
          finally{foreach(var curve in nominal.Concat(expanded))curve.Dispose();}
        }
        if(panels.Count==0||panels.Count>settings.MaxParts||
          Math.Abs(nominalArea-source.NominalArea)>tolerance*finished.GetLength()*ConfiguredCoverageToleranceFactor)
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

    private static bool NeedsFiniteCut(Curve boundary,LocalCut cut,double tolerance)
    {
      if(cut is not {Start:{} start,End:{} end})return false;
      using var full=PlanarPartSplitter.Seam(boundary,cut.Frame,0,tolerance);
      return full==null||Math.Min(full.PointAtStart.DistanceTo(start)+full.PointAtEnd.DistanceTo(end),
        full.PointAtStart.DistanceTo(end)+full.PointAtEnd.DistanceTo(start))>tolerance*ConfiguredBoundaryToleranceFactor;
    }
    private static List<Curve> PartitionBoundary(Curve boundary,IReadOnlyList<Curve> dividers,Plane plane,double tolerance)
    {
      using var regions=Curve.CreateBooleanRegions(new[]{boundary}.Concat(dividers),plane,false,tolerance);
      if(regions==null)throw new InvalidOperationException("could not partition the finished boundary");
      var loops=new List<Curve>();
      try
      {
        for(int index=0;index<regions.RegionCount;index++)loops.AddRange(regions.RegionCurves(index));
        if(loops.Any(loop=>!loop.IsClosed))throw new InvalidOperationException("a finite cut produced an open region");
        return loops;
      }
      catch{foreach(var loop in loops)loop.Dispose();throw;}
    }
    private static double CurveArea(Curve curve)
    {using var mass=AreaMassProperties.Compute(curve);return mass?.Area??0;}
    private static Curve AllowanceFootprint(Curve nominal,Plane plane,double allowance,double tolerance)
    {
      var left=nominal.Offset(plane,allowance,tolerance,CurveOffsetCornerStyle.Sharp)??[];
      var right=nominal.Offset(plane,-allowance,tolerance,CurveOffsetCornerStyle.Sharp)??[];
      try
      {
        double area=CurveArea(nominal);
        var outward=left.Concat(right).Where(curve=>curve.IsClosed&&CurveArea(curve)>area+tolerance*tolerance)
          .Where(curve=>curve.Contains(nominal.PointAtNormalizedLength(0.5),plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)
          .OrderByDescending(CurveArea).FirstOrDefault();
        return outward?.DuplicateCurve()??throw new InvalidOperationException("could not extend the split allowance to the original cut edge");
      }
      finally{foreach(var curve in left.Concat(right))curve.Dispose();}
    }
    private static List<Panel> BuildEndpointPanels(Part part,PlanarPartSplitter.Proposal proposal,IReadOnlyList<LocalCut> cuts,
      PlanarPartSplitter.Settings settings,double tolerance,out double maximumWidth)
    {
      maximumWidth=0;var panels=new List<Panel>();var seams=new List<Curve>();var paths=new List<Curve>();
      var nominal=new List<Curve>();var outer=new List<Curve>();
      try
      {
        var source=GetSourceDetails(part,tolerance);var finished=source.FinishedBoundary??part.Outline;
        double booleanTolerance=Math.Max(RhinoMath.ZeroTolerance,tolerance*Math.Clamp(ConfiguredAllowanceBooleanToleranceFactor,RhinoMath.ZeroTolerance,1));
        foreach(int index in Enumerable.Range(0,cuts.Count))
        {
          var seam=SplitLine(part,proposal,cuts,index,tolerance)??throw new InvalidOperationException("finite split has no endpoints");seams.Add(seam);
          Point3d OuterEnd(Point3d point)
          {
            if(!part.Outline.ClosestPoint(point,out double parameter))throw new InvalidOperationException("split cannot reach the outer cut edge");
            var end=part.Outline.PointAt(parameter);
            for(int sample=1;sample<ConfiguredBoundarySamples;sample++)
              if(finished.Contains(point+(end-point)*sample/ConfiguredBoundarySamples,part.Plane,tolerance)==PointContainment.Inside)
                throw new InvalidOperationException("the split's allowance connector would cross finished material");
            return end;
          }
          var points=new[]{OuterEnd(seam.PointAtStart),seam.PointAtStart,seam.PointAtEnd,OuterEnd(seam.PointAtEnd)}
            .Aggregate(new List<Point3d>(),(values,point)=>{if(values.Count==0||values[^1].DistanceTo(point)>tolerance)values.Add(point);return values;});
          var path=new PolylineCurve(points);paths.Add(path);
        }
        nominal=PartitionBoundary(finished,seams,part.Plane,tolerance);outer=PartitionBoundary(part.Outline,paths,part.Plane,tolerance);
        double areaTolerance=tolerance*finished.GetLength()*ConfiguredCoverageToleranceFactor;
        if(nominal.Count!=cuts.Count+1||nominal.Count>settings.MaxParts||Math.Abs(nominal.Sum(CurveArea)-source.NominalArea)>areaTolerance)
          throw new InvalidOperationException("finite splits do not preserve the complete finished area");
        nominal=nominal.OrderBy(loop=>PlanarPartSplitter.FrameY(proposal.Frame,loop.GetBoundingBox(true).Center)).ToList();
        var assigned=nominal.Select(_=>new List<Curve>()).ToArray();
        foreach(var piece in outer)
        {
          var scores=nominal.Select(loop=>
          {
            var shared=Curve.CreateBooleanIntersection(loop,piece,tolerance)??[];
            try{return shared.Sum(CurveArea);}finally{foreach(var curve in shared)curve.Dispose();}
          }).ToArray();
          int owner=Array.IndexOf(scores,scores.Max());
          if(scores[owner]<=tolerance*tolerance)
          {
            var point=piece.GetBoundingBox(true).Center;
            owner=nominal.Select((loop,index)=>(index,Distance:loop.ClosestPoint(point,out double t)?loop.PointAt(t).DistanceToSquared(point):double.PositiveInfinity))
              .MinBy(value=>value.Distance).index;
          }
          assigned[owner].Add(piece);
        }
        for(int region=0;region<nominal.Count;region++)
        {
          var touching=seams.Select((line,index)=>(line,index)).Where(value=>Coincident(value.line,nominal[region],tolerance)).Select(value=>value.index).ToArray();
          Curve[] extra=[];
          Curve[] joined;
          try
          {
            // Offset the finished piece, not its bent allowance connectors, to keep the new cut edge straight.
            if(settings.Allowance>tolerance)
            {
              using var footprint=AllowanceFootprint(nominal[region],part.Plane,settings.Allowance,tolerance);
              extra=Curve.CreateBooleanIntersection(footprint,part.Outline,booleanTolerance)??[];
              if(extra.Length==0)throw new InvalidOperationException("split allowance could not be intersected with the original cutting boundary");
            }
            joined=Curve.CreateBooleanUnion(assigned[region].Concat(extra),booleanTolerance)??[];
          }
          finally{foreach(var curve in extra)curve.Dispose();}
          if(joined.Length!=1){foreach(var curve in joined)curve.Dispose();throw new InvalidOperationException("finite split part is disconnected");}
          var along=touching.Aggregate(Vector3d.Zero,(sum,index)=>sum+cuts[index].Frame.YAxis);
          if(!along.Unitize())along=proposal.Frame.YAxis;
          var frame=new Plane(part.Plane.Origin,Vector3d.CrossProduct(along,part.Plane.Normal),along);var bounds=joined[0].GetBoundingBox(frame);
          var panel=new Panel(joined[0],bounds,[nominal[region].DuplicateCurve()],region){Frame=frame,NominalArea=CurveArea(nominal[region]),Finite=true};panels.Add(panel);
          panel.Seams.UnionWith(touching);maximumWidth=Math.Max(maximumWidth,bounds.Max.Y-bounds.Min.Y);
          foreach(int index in touching)panel.SewLines.AddRange(ClipSewLine(part,seams[index],panel.Cut,tolerance));
          foreach(var detail in source.Items.Where(detail=>detail.Item.Geometry is Curve))
            foreach(var piece in ClipDetail(detail.Item,panel,part.Plane,tolerance))
              if(detail.Boundary&&Coincident(piece,part.Outline,tolerance))piece.Dispose();else panel.Details.Add(detail.Item with{Geometry=piece});
        }
        AssignNonCurveDetails(part,panels,tolerance);return panels;
      }
      catch(Exception ex){foreach(var panel in panels)panel.Dispose();Log.Write("vPartSplit","finite split rejected: {0}",ex.Message);return [];}
      finally{foreach(var curve in seams.Concat(paths).Concat(nominal).Concat(outer))curve.Dispose();}
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
        using var finishedMass=finished==null?null:AreaMassProperties.Compute(finished);
        return new(items,area,finished?.DuplicateCurve()){NominalArea=finishedMass?.Area??area,Tolerance=tolerance};
      }
      finally {foreach(var loop in loops)loop.Dispose();}
    });

    private static bool BoundsOverlap(BoundingBox a,BoundingBox b,double tolerance) =>
      a.Max.X>=b.Min.X-tolerance&&b.Max.X>=a.Min.X-tolerance&&a.Max.Y>=b.Min.Y-tolerance&&b.Max.Y>=a.Min.Y-tolerance;

    private static void ReplacePanels(State state,List<Panel> panels)
      =>ReplacePanelsCore(state,panels,null);
    private static void ReplacePanelsCore(State state,List<Panel> panels,IReadOnlyCollection<Panel>? retained)
    {
      foreach(var panel in panels.Where(panel=>!state.Panels.Contains(panel)))
      {
        var center=panel.Frame.PointAt(panel.Bounds.Center.X,panel.Bounds.Center.Y,panel.Bounds.Center.Z);
        var previous=state.Panels.Where(old=>old.BandIndex==panel.BandIndex&&old.CompactFit!=null)
          .OrderBy(old=>old.Frame.PointAt(old.Bounds.Center.X,old.Bounds.Center.Y,old.Bounds.Center.Z).DistanceToSquared(center)).FirstOrDefault();
        if(previous?.CompactFit is {} fit)panel.CompactSeed=fit.Plane;
      }
      foreach(var panel in state.Panels)if(!panels.Contains(panel)&&retained?.Contains(panel)!=true)panel.Dispose();
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
      var panels=BuildLocalPanels(state.Part,next,cuts,EditingSettings(state,tolerance),tolerance,out _);
      if(panels.Count<=state.Panels.Count)
      { foreach(var panel in panels)panel.Dispose();return false; }
      ReplacePanels(state,panels);state.Cuts=cuts;state.Proposal=next;
      Log.Write("vPartSplit","manual cross-cut added cuts={0} parts={1}",cuts.Count,panels.Count);
      return true;
    }
    private static bool TryRemoveCut(State state,int index,double tolerance)
    {
      var cuts=state.Cuts?.ToList()??state.Proposal.Seams.Select(position=>new LocalCut(new Plane(state.Proposal.Frame.PointAt(0,position),state.Proposal.Frame.XAxis,state.Proposal.Frame.YAxis))).ToList();
      if(index<0||index>=cuts.Count)return false;
      cuts.RemoveAt(index);
      var proposal=state.Proposal with{Seams=cuts.Select(cut=>PlanarPartSplitter.FrameY(state.Proposal.Frame,cut.Frame.Origin)).ToList()};
      var settings=Settings(state.Direction) with{Width=IntermediateWidthLimit};
      var panels=cuts.Count==0?BuildPanels(state.Part,proposal,settings,tolerance):BuildLocalPanels(state.Part,proposal,cuts,settings,tolerance,out _);
      if(panels.Count==0)return false;
      ReplacePanels(state,panels);state.Proposal=proposal;state.Cuts=cuts;state.ActiveCut=Math.Min(index,Math.Max(0,cuts.Count-1));
      Log.Write("vPartSplit","manual cross-cut removed cuts={0} parts={1}",cuts.Count,panels.Count);return true;
    }

    private static IReadOnlyList<CutPreview> CachedCutPreviews(State state,double tolerance)
    {
      if(ReferenceEquals(state.CachedProposal,state.Proposal)&&ReferenceEquals(state.CachedCuts,state.Cuts))return state.CutPreviews;
      foreach(var cut in state.CutPreviews)cut.Dispose();state.CutPreviews.Clear();
      for(int index=0;index<state.Proposal.Seams.Count;index++)
      {
        var line=SplitLine(state.Part,state.Proposal,state.Cuts,index,tolerance);
        if(line==null)throw new InvalidOperationException("split has no boundary intersections");
        state.CutPreviews.Add(new(line,PlanarPartSplitter.ClipCurve(line,SplitBoundary(state.Part,tolerance),state.Part.Plane,tolerance)));
      }
      state.CachedProposal=state.Proposal;state.CachedCuts=state.Cuts;
      return state.CutPreviews;
    }

    private static bool TryFittedCrossCuts(Part part,PlanarPartSplitter.Settings settings,double tolerance,
      out PlanarPartSplitter.Proposal proposal,out List<LocalCut>? cuts,out List<Panel> panels)
    {
      proposal=null!;cuts=null;panels=[];
      var whole=PlanarFit.FitCurve(part.Outline,part.Plane,IntermediateWidthLimit,tolerance);
      if(whole==null)return false;
      var frame=whole.Plane;
      if(whole.Width>whole.Height)frame.Rotate(Math.PI/2,frame.Normal);
      if(frame.YAxis*ProgressFrame(part,settings.Samples).YAxis<0)frame=new(frame.Origin,-frame.XAxis,-frame.YAxis);
      if(!PlanarFit.TryBounds(part.Outline,frame,out var bounds))return false;
      double span=bounds.Max.Y-bounds.Min.Y;if(span<=tolerance)return false;
      var fittingWhole=VerticalWidth(whole.Bounds)<=settings.Width+tolerance?whole:
        PlanarFit.FitCurve(part.Outline,part.Plane,settings.Width,tolerance);
      var seed=PlanarPartSplitter.AtPosition(frame,bounds.Min.Y,bounds.Max.Y,settings,null);
      int divisions=Math.Clamp(Math.Max(ConfiguredAutoFitStations,seed?.Seams.Count+1??1),MinimumAutoFitStations,MaximumAutoFitStations);
      var finished=SplitBoundary(part,tolerance);
      var intervalCache=new Dictionary<(double Lower,double Upper),FittedInterval?>();
      FittedInterval? FitInterval(double lower,double upper)
      {
        if(intervalCache.TryGetValue((lower,upper),out var cached))return cached;
        FittedInterval? result=null;
        if(lower==bounds.Min.Y&&upper==bounds.Max.Y)
        {
          if(fittingWhole!=null)result=new(fittingWhole,MaterialLength(fittingWhole.Bounds,settings.Width,tolerance));
        }
        else
        {
          var loops=PlanarPartSplitter.ClipBand(part.Outline,frame,lower-(lower>bounds.Min.Y?settings.Allowance:0),
            upper+(upper<bounds.Max.Y?settings.Allowance:0),tolerance);
          try
          {
            if(loops.Length==1&&loops[0].IsClosed&&PlanarFit.FitCurve(loops[0],frame,settings.Width,tolerance) is {} fit)
              result=new(fit,MaterialLength(fit.Bounds,settings.Width,tolerance));
          }
          finally{foreach(var loop in loops)loop.Dispose();}
        }
        intervalCache[(lower,upper)]=result;return result;
      }
      bool IsStation(double value)=>CrossSection(finished,new Plane(frame.PointAt(0,value),frame.XAxis,frame.YAxis),tolerance,out _,out _,out _);
      var candidates=new List<double>{bounds.Min.Y,bounds.Max.Y};
      if(fittingWhole==null)
      {
        candidates.AddRange(Enumerable.Range(1,divisions-1).Select(index=>bounds.Min.Y+span*index/divisions));
        candidates.AddRange(seed?.Seams??[]);
        // Width frontiers add feasible cut positions between grid stations, from both ends.
        foreach(bool reverse in new[]{false,true})
        {
          double cursor=reverse?bounds.Max.Y:bounds.Min.Y,end=reverse?bounds.Min.Y:bounds.Max.Y;
          for(int piece=0;piece<settings.MaxParts-1;piece++)
          {
            if(FitInterval(Math.Min(cursor,end),Math.Max(cursor,end))!=null)break;
            double near=0,far=Math.Abs(end-cursor);double? best=null;
            for(int pass=0;pass<Math.Clamp(ConfiguredAutoFitRefinementPasses,1,MaximumAutoFitRefinementPasses)&&far-near>tolerance;pass++)
            {
              double distance=(near+far)/2,position=cursor+(reverse?-distance:distance);
              if(IsStation(position)&&FitInterval(Math.Min(cursor,position),Math.Max(cursor,position))!=null)
              {near=distance;best=position;}
              else far=distance;
            }
            if(best==null||Math.Abs(best.Value-cursor)<=tolerance)break;
            candidates.Add(best.Value);cursor=best.Value;
          }
        }
      }
      var positions=candidates.Where(value=>value==bounds.Min.Y||value==bounds.Max.Y||IsStation(value))
        .Order().Aggregate(new List<double>(),(values,value)=>
          {if(values.Count==0||value-values[^1]>tolerance)values.Add(value);return values;});
      int last=positions.Count-1;
      var intervals=new FittedInterval?[last+1,last+1];
      for(int lower=0;lower<last;lower++)for(int upper=lower+1;upper<=last;upper++)
        intervals[lower,upper]=FitInterval(positions[lower],positions[upper]);
      for(int count=1;count<=Math.Min(settings.MaxParts,last);count++)
      {
        var costs=new double[count+1,last+1];var ties=new double[count+1,last+1];var previous=new int[count+1,last+1];
        for(int used=0;used<=count;used++)for(int node=0;node<=last;node++)costs[used,node]=double.PositiveInfinity;
        costs[0,0]=0;
        var ranks=BandOrder(count,settings.Direction).Select((band,rank)=>(band,rank)).ToDictionary(pair=>pair.band,pair=>pair.rank);
        double balance=settings.Spacing==PlanarPartSplitter.SpacingMode.Even?Math.Max(0,PlanarPartSplitter.ConfiguredEvenBalanceWeight)*count/span:0;
        for(int used=1;used<=count;used++)for(int upper=used;upper<=last;upper++)for(int lower=used-1;lower<upper;lower++)
        {
          if(!double.IsFinite(costs[used-1,lower])||intervals[lower,upper] is not {} interval)continue;
          double width=positions[upper]-positions[lower];
          double cost=costs[used-1,lower]+interval.Length+settings.Gap+balance*width*width;
          double tie=ties[used-1,lower]-width*Math.Pow(1.0/(last+1),ranks[used-1]);
          if(cost>costs[used,upper]+tolerance||Math.Abs(cost-costs[used,upper])<=tolerance&&tie>=ties[used,upper])continue;
          costs[used,upper]=cost;ties[used,upper]=tie;previous[used,upper]=lower;
        }
        if(!double.IsFinite(costs[count,last]))continue;
        var nodes=new int[count+1];nodes[count]=last;
        for(int used=count;used>0;used--)nodes[used-1]=previous[used,nodes[used]];
        var seams=nodes.Skip(1).Take(count-1).Select(node=>positions[node]).ToList();
        double length=Enumerable.Range(0,count).Sum(index=>intervals[nodes[index],nodes[index+1]]!.Length)+settings.Gap*(count-1);
        var next=new PlanarPartSplitter.Proposal(frame,bounds.Min.Y,bounds.Max.Y,seams,length);
        var local=seams.Select(position=>new LocalCut(new Plane(frame.PointAt(0,position),frame.XAxis,frame.YAxis))).ToList();
        var built=count==1?BuildPanels(part,next,settings with{Width=IntermediateWidthLimit},tolerance):
          BuildLocalPanels(part,next,local,settings with{Width=IntermediateWidthLimit},tolerance,out _);
        bool valid=built.Count==count;
        foreach(var panel in built)
        {
          var expected=intervals[nodes[panel.BandIndex],nodes[panel.BandIndex+1]]!.Fit;
          if(!PlanarFit.TryBounds(panel.Cut,expected.Plane,out var measured)){valid=false;break;}
          var fitted=new PlanarFit.Fit(expected.Plane,measured,expected.Angle);
          if(VerticalWidth(measured)>settings.Width+tolerance)
          {fitted=PlanarFit.FitCurve(panel.Cut,panel.Frame,settings.Width,tolerance);if(fitted==null){valid=false;break;}}
          panel.CompactFit=fitted;panel.CompactWidth=settings.Width;panel.CompactRefined=true;
          panel.EditingFit=fitted;panel.EditingFitLimit=settings.Width;panel.EditingFitTolerance=tolerance;
        }
        if(!valid){foreach(var panel in built)panel.Dispose();continue;}
        proposal=next;cuts=local;panels=built;
        Log.Write("vPartSplit","fitted split parts={0} stations={1} width={2:G6} materialLength={3:G6} intervalTests={4}",count,last,settings.Width,length,intervalCache.Count);
        return true;
      }
      return false;
    }

    private static bool PlanCrossCuts(Part part,PlanarPartSplitter.Proposal initial,PlanarPartSplitter.Settings settings,double tolerance,bool automatic,
      out PlanarPartSplitter.Proposal proposal,out List<LocalCut>? cuts,out List<Panel> panels)
    {
      proposal=initial;cuts=null;panels=[];
      if(TryFittedCrossCuts(part,settings,tolerance,out proposal,out cuts,out panels))return true;
      proposal=initial;
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
        var finished=SplitBoundary(part,tolerance);
        for (int index = 0; index <= proposal.Seams.Count; index++)
        {
          var (lower,upper) = PlanarPartSplitter.Band(proposal,index,settings.Allowance);
          var nominal = PlanarPartSplitter.Band(proposal,index,0);
          var previews = PlanarPartSplitter.ClipBand(finished, proposal.Frame, nominal.Lower, nominal.Upper, tolerance);
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
              var panel = new Panel(cut.DuplicateCurve(), bounds, matching, index){Frame=proposal.Frame,NominalArea=matching.Sum(CurveArea)};
              panels.Add(panel);
              foreach(var seamIndex in new[] { index-1,index })
              {
                if(seamIndex<0 || seamIndex>=proposal.Seams.Count) continue;
                panel.Seams.Add(seamIndex);
                using var sew = SplitLine(part,proposal,null,seamIndex,tolerance);
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

    private static bool BetterHit(CutHit hit,CutHit? current)=>current==null||
      hit.Endpoint>=0&&current.Endpoint<0||(hit.Endpoint>=0)==(current.Endpoint>=0)&&hit.Distance<current.Distance;

    private static CutHit? HitCut(Point2d cursor,Point2d a,Point2d b,int index)
    {
      double start=cursor.DistanceTo(a),end=cursor.DistanceTo(b);
      double endpoint=Math.Min(start,end);
      if(endpoint<=ConfiguredEndpointPickRadius)return new(index,start<=end?0:1,endpoint);
      double dx=b.X-a.X,dy=b.Y-a.Y,length=dx*dx+dy*dy;
      if(length<=0)return null;
      double fraction=Math.Clamp(((cursor.X-a.X)*dx+(cursor.Y-a.Y)*dy)/length,0,1);
      double distance=cursor.DistanceTo(new Point2d(a.X+fraction*dx,a.Y+fraction*dy));
      return distance<=ConfiguredPickRadius?new(index,-1,distance):null;
    }

    private static CutHit? FindCutHit(RhinoViewport viewport, int x, int y, IReadOnlyList<CutPreview> cuts)
    {
      CutHit? nearest=null;
      for (int index=0; index<cuts.Count; index++)
      {
        var cut=cuts[index].Line;
        var a=viewport.WorldToClient(cut.PointAtStart); var b=viewport.WorldToClient(cut.PointAtEnd);
        var hit=HitCut(new Point2d(x,y),a,b,index);
        if(hit!=null&&BetterHit(hit,nearest))nearest=hit;
      }
      return nearest;
    }

    private static bool PreferReverse(Plane frame,Plane targetPlane) =>
      frame.XAxis*targetPlane.XAxis+frame.YAxis*targetPlane.YAxis<0;

    private static double OriginalBandSpread(int band,double seam,double distance) => band*(2*seam+distance);

    private static Vector3d[] OriginalSpreads(State state)
    {
      if(state.Spacing is {} cached&&ReferenceEquals(cached.Panels,state.Panels)&&cached.Seam==_seam&&cached.Distance==_distance)
        return cached.Offsets;
      var offsets=state.Panels.Select(panel=>state.Cuts==null?state.Proposal.Frame.YAxis*OriginalBandSpread(panel.BandIndex,_seam,_distance)
        :state.Cuts.Take(panel.BandIndex).Aggregate(Vector3d.Zero,(sum,cut)=>sum+cut.Frame.YAxis*(2*_seam+_distance))).ToArray();
      if(!state.Panels.Any(panel=>panel.Finite))return offsets;
      double tolerance=Math.Max(GetSourceDetails(state.Part,RhinoMath.SqrtEpsilon).Tolerance,RhinoMath.ZeroTolerance);
      var placed=new List<Curve>();
      try
      {
        // A finite corner cut can leave material past its endpoint, outside the usual numbered bands.
        for(int index=0;index<state.Panels.Count;index++)
        {
          var panel=state.Panels[index];
          using var original=panel.Cut.DuplicateCurve();original.Translate(offsets[index]);
          bool Clear(double distance,Vector3d axis)
          {
            using var moved=original.DuplicateCurve();moved.Translate(axis*distance);
            foreach(var other in placed)
            {
              var overlap=Curve.CreateBooleanIntersection(moved,other,tolerance)??[];
              try{if(overlap.Any(curve=>CurveArea(curve)>tolerance*tolerance))return false;}
              finally{foreach(var curve in overlap)curve.Dispose();}
              if(!moved.ClosestPoints(other,out var a,out var b)||a.DistanceTo(b)<_distance-tolerance)return false;
            }
            return true;
          }
          if(placed.Count>0&&!Clear(0,Vector3d.Zero))
          {
            int neighbor=Enumerable.Range(0,index).OrderByDescending(other=>panel.Seams.Intersect(state.Panels[other].Seams).Count())
              .ThenBy(other=>panel.Cut.GetBoundingBox(true).Center.DistanceToSquared(state.Panels[other].Cut.GetBoundingBox(true).Center)).First();
            var axis=panel.Frame.YAxis;
            var away=panel.Cut.GetBoundingBox(true).Center-state.Panels[neighbor].Cut.GetBoundingBox(true).Center;
            if(away*axis<0)axis=-axis;
            var frame=new Plane(state.Part.Plane.Origin,Vector3d.CrossProduct(axis,state.Part.Plane.Normal),axis);
            double high=Math.Max(0,placed.Max(curve=>curve.GetBoundingBox(frame).Max.Y)-original.GetBoundingBox(frame).Min.Y+_distance+tolerance);
            if(!Clear(high,axis))throw new InvalidOperationException("could not separate the finite split copies");
            double low=0;
            for(int pass=0;pass<Math.Clamp(ConfiguredOriginalSpacingPasses,1,64)&&high-low>tolerance;pass++)
            {
              double middle=low+(high-low)/2;
              if(Clear(middle,axis))high=middle;else low=middle;
            }
            var shift=axis*high;offsets[index]+=shift;original.Translate(shift);
          }
          placed.Add(original.DuplicateCurve());
        }
        state.Spacing=new(state.Panels,_seam,_distance,offsets);return offsets;
      }
      finally{foreach(var curve in placed)curve.Dispose();}
    }

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
      bool widthX=MaterialWidthIsX(bounds,_width,RhinoMath.SqrtEpsilon);
      var axisX=widthX?targetPlane.YAxis:targetPlane.XAxis;
      var axisY=widthX?-targetPlane.XAxis:targetPlane.YAxis;
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
    private static bool MaterialWidthIsX(BoundingBox bounds,double width,double tolerance)
    {
      double x=bounds.Max.X-bounds.Min.X,y=bounds.Max.Y-bounds.Min.Y;
      bool xFits=x<=width+tolerance,yFits=y<=width+tolerance;
      return xFits&&yFits?x>=y:xFits!=yFits?xFits:x<=y;
    }
    private static double MaterialLength(BoundingBox bounds,double width,double tolerance)=>MaterialWidthIsX(bounds,width,tolerance)?
      bounds.Max.Y-bounds.Min.Y:bounds.Max.X-bounds.Min.X;

    private static PlanarFit.Fit CompactLayout(Panel panel,bool refine)
    {
      if(panel.EditingFitLimit==_width&&panel.EditingFit is {} accepted)return accepted;
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
        if(fit!=null&&(VerticalWidth(panel.CompactFit.Bounds)>_width+RhinoMath.SqrtEpsilon||fit.Area<=panel.CompactFit.Area))panel.CompactFit=fit;
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
        var spreads=OriginalSpreads(state);
        for(int index=0;index<state.Panels.Count;index++)
        {
          transforms[index]=Transform.Translation(anchor-sourceBase+spreads[index]);
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
        x+=MaterialLength(fit.Bounds,_width,RhinoMath.SqrtEpsilon)+_distance;
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
      { foreach(var label in panel.Labels) label.Text.Dispose(); panel.Labels.Clear();foreach(var marker in panel.Markers)marker.Dispose();panel.Markers.Clear(); }
    }

    private static double[] SeamNotchPositions(double length,double start,double end,double unitsPerInch,double tolerance)
    {
      double lower=Math.Clamp(start,0,length),upper=Math.Clamp(length-end,0,length),available=upper-lower;
      if(available<=tolerance)return [length*.5];
      int intervals=1;
      if(available>ConfiguredMiddleNotchThresholdInches*unitsPerInch+tolerance)
      {
        double minimum=Math.Max(tolerance,ConfiguredMinimumNotchSpacingInches*unitsPerInch);
        double maximum=Math.Max(minimum,ConfiguredMaximumNotchSpacingInches*unitsPerInch);
        intervals=Math.Max(2,(int)Math.Ceiling((available-tolerance)/maximum));
        intervals=Math.Min(intervals,Math.Max(1,(int)Math.Floor((available+tolerance)/minimum)));
      }
      return Enumerable.Range(0,intervals+1).Select(index=>lower+available*index/intervals).ToArray();
    }

    private static string EnsureLabels(RhinoDoc doc,IReadOnlyList<State> states,MatchingLabels.Style style,double tolerance)
    {
      string next=style.Value;
      var notchStyle=MatchingNotches.Read(doc) with{Type=SeamNotchType};
      double unitsPerInch=RhinoMath.UnitScale(UnitSystem.Inches,doc.ModelUnitSystem);
      int notchLayer=LayerSelector.IsCurrentLayerValue(notchStyle.Layer,MatchingNotches.CurrentLayer)?doc.Layers.CurrentLayerIndex:doc.Layers.FindByFullPath(notchStyle.Layer,-1);
      var notchColor=notchLayer>=0?doc.Layers[notchLayer].Color:DefaultSewColor;
      foreach(var state in states)
      {
        if(state.LabelPanels==state.Panels&&state.LabelStyle==(_label?style:null)&&state.LabelStart==next&&state.MarkerStyle==notchStyle&&state.MarkerNotches==_notches)
        { next=state.NextLabel; continue; }
        ClearLabels(state);
        state.MarkerStyle=notchStyle;state.MarkerNotches=_notches;
        state.LabelPanels=state.Panels;state.LabelStyle=_label?style:null;state.LabelStart=next;state.LabelError=null;
        string start=next;
        try
        {
          if((_label||_notches)&&state.Proposal.Seams.Count>0)
          {
            if(_label&&string.IsNullOrWhiteSpace(style.Value)&&string.IsNullOrWhiteSpace(style.Prefix))
              throw new InvalidOperationException("enter a label value in vNotches, or use Label=No.");
            if(_label&&(!double.IsFinite(style.Height)||style.Height<=tolerance))
              throw new InvalidOperationException("set a positive label size in vNotches, or use Label=No.");
            if(_notches&&notchStyle.Length<=tolerance)throw new InvalidOperationException("set a positive notch length in vNotches, or use Notches=No.");
            for(int seam=0;seam<state.Proposal.Seams.Count;seam++)
            {
              using var line=SplitLine(state.Part,state.Proposal,state.Cuts,seam,tolerance);
              if(line==null) throw new InvalidOperationException("a seam has no matching label location.");
              var pieces=PlanarPartSplitter.ClipCurve(line,SplitBoundary(state.Part,tolerance),state.Part.Plane,tolerance);
              try
              {
                var frame=state.Cuts is {Count:>0}?state.Cuts[seam].Frame:state.Proposal.Frame;
                int segment=0;
                foreach(var piece in pieces.OrderBy(curve=>(curve.PointAtNormalizedLength(0.5)-frame.Origin)*frame.XAxis))
                {
                  if(piece.GetLength()<=tolerance) continue;
                  if(piece.TangentAtStart*frame.XAxis<0)piece.Reverse();
                  var midpoint=piece.PointAtNormalizedLength(0.5);
                  var mates=state.Panels.Where(panel=>panel.Seams.Contains(seam)&&
                    panel.Preview.Any(outline=>outline.Contains(midpoint,state.Part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident)).ToArray();
                  if(mates.Length!=2)
                    throw new InvalidOperationException($"cannot identify both mating edges of seam {seam+1} for labeling.");
                  var positions=_notches?SeamNotchPositions(piece.GetLength(),notchStyle.StartEnabled?notchStyle.Start:0,
                    notchStyle.EndEnabled?notchStyle.End:0,unitsPerInch,tolerance):new[]{piece.GetLength()*.5};
                  foreach(double position in positions)
                  {
                  var point=MatchingNotches.PointAtLength(piece,position);
                  var match=Guid.NewGuid();
                  foreach(var panel in mates)
                  {
                    var probe=midpoint+frame.YAxis*tolerance*ConfiguredBoundaryToleranceFactor;
                    var inward=frame.YAxis*(panel.Preview.Any(outline=>outline.Contains(probe,state.Part.Plane,tolerance)==PointContainment.Inside)?1:-1);
                    bool allowance=_seam>tolerance;
                    var offsetDirection=allowance?-inward:inward;
                    var geometry=_notches?MatchingNotches.StraightGeometry(point,frame.XAxis,offsetDirection,notchStyle.Length,
                      allowance?_seam:0,notchStyle.Type,notchStyle.Width):[];
                    var marker=new SeamMarker(seam,segment,_label?next:string.Empty,match,piece.DuplicateCurve(),position,offsetDirection,geometry,notchStyle,notchColor);
                    panel.Markers.Add(marker);
                    if(!_label)continue;
                    var (plane,_,_)=MatchingLabels.Layout(doc,piece,position,offsetDirection,frame.XAxis,allowance?_seam:0,
                      _notches?geometry:null,next,style.Height,notchStyle.LabelOffset,notchStyle.LabelOffsetY,notchStyle.TowardStart?"Left":"Right",style.Prefix);
                    if(allowance)plane.Origin+=offsetDirection*_seam*(ConfiguredAllowanceLabelPositionFactor-AllowanceLabelPositionFactor);
                    else plane=MatchingLabels.FacingPlane(doc,plane.Origin+inward*style.Height*ConfiguredLabelInsetHeightFactor,
                      frame.XAxis,offsetDirection,point);
                    if(!plane.IsValid) throw new InvalidOperationException("a seam label has no valid facing direction.");
                    var text=MatchingLabels.CreateText(plane,next,style.Height);
                    text.PlainText=MatchingLabels.FormatNotchLabel(next,style.Prefix,plane,point);
                    text.DrawForward=false;
                    panel.Labels.Add(new SeamLabel(seam,segment,next,text){Marker=marker});
                  }
                  if(_label)next=MatchingLabels.Next(next,style.AutoAdvance);segment++;
                  }
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
      {
        foreach(var segment in cut.Segments)PreviewDisplay.DrawCurve(display,segment,CutPreviewColor);
        display.DrawPoint(cut.Line.PointAtStart,PointStyle.RoundSimple,ConfiguredEndpointHandleSize,CutPreviewColor);
        display.DrawPoint(cut.Line.PointAtEnd,PointStyle.RoundSimple,ConfiguredEndpointHandleSize,CutPreviewColor);
      }
      foreach(var label in panels.SelectMany(panel=>panel.Labels))
        display.DrawAnnotation(label.Text,Color.FromArgb(ConfiguredPreviewAlpha,referenceColor));
      foreach(var marker in panels.SelectMany(panel=>panel.Markers))
        foreach(var geometry in marker.Geometry)PreviewDisplay.DrawCurve(display,geometry,Color.FromArgb(ConfiguredPreviewAlpha,referenceColor));
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
          foreach(var marker in panel.Markers)
            foreach(var geometry in marker.Geometry)PreviewDisplay.DrawCurve(display,geometry,Color.FromArgb(ConfiguredPreviewAlpha,marker.Color));
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

    private static void AddMatchingMarkers(RhinoDoc doc,IEnumerable<SeamMarker> markers,IReadOnlyList<SeamLabel> labels,
      IReadOnlyList<(Curve Curve,Guid Id)> sources,Transform transform,Vector3d normal,int notchLayer,int labelLayer,
      string labelName,string session,double tolerance,MatchingLabels.Style? style,Func<GeometryBase,ObjectAttributes,string?,Guid> emit)
    {
      foreach(var marker in markers)
      {
        var point=MatchingNotches.PointAtLength(marker.Source,marker.Length);
        var source=sources.Select(item=>
        {
          bool found=item.Curve.ClosestPoint(point,out double parameter);
          return (item.Curve,item.Id,Parameter:parameter,Distance:found?point.DistanceTo(item.Curve.PointAt(parameter)):double.MaxValue);
        }).OrderBy(item=>item.Distance).FirstOrDefault();
        if(source.Curve==null||source.Distance>tolerance*ConfiguredBoundaryToleranceFactor)
          throw new InvalidOperationException("a matching notch has no committed nominal seam curve.");
        var label=labels.FirstOrDefault(item=>ReferenceEquals(item.Marker,marker));
        var tangent=source.Curve.TangentAt(source.Parameter);
        string side=Vector3d.CrossProduct(normal,tangent)*marker.Direction>=0?"Left":"Right";
        double length=source.Parameter<=source.Curve.Domain.T0?0:source.Parameter>=source.Curve.Domain.T1?source.Curve.GetLength():
          source.Curve.GetLength(new Interval(source.Curve.Domain.T0,source.Parameter));
        using var transformed=source.Curve.DuplicateCurve();transformed.Transform(transform);tangent.Transform(transform);
        using var attributes=MatchingNotches.Attributes(transformed,source.Id,marker.Seam,"length",length,marker.Style.Length,_seam,
          marker.Style.Type,marker.Style.Width,side,label!=null,marker.Value,style?.Height??0,marker.Style.LabelOffset,marker.Style.LabelOffsetY,
          marker.Style.TowardStart?"Left":"Right",doc.Layers[notchLayer].FullPath,doc.Layers[labelLayer].FullPath,tangent,notchLayer);
        MatchingNotches.BindReference(doc,attributes,transformed,length);
        attributes.SetUserString(MatchingNotches.Prefix+"label_prefix",style?.Prefix??string.Empty);
        attributes.SetUserString(MatchingNotches.Prefix+"both_sides","0");
        attributes.SetUserString(MetadataPrefix+"session",session);
        attributes.SetUserString(MetadataPrefix+"labelValue",marker.Value);
        attributes.SetUserString(MetadataPrefix+"labelPair",$"{session}:{marker.Seam}:{marker.Segment}");
        Guid Add(GeometryBase geometry,string role,string name,Guid notchId,int layer)
        {
          using var current=attributes.Duplicate();current.LayerIndex=layer;
          MatchingNotches.SetMember(current,Guid.NewGuid(),marker.Match,role,notchId);
          var id=emit(geometry,current,name);
          if(id!=current.ObjectId)
          {
            using var actual=doc.Objects.FindId(id)!.Attributes.Duplicate();MatchingNotches.SetMember(actual,id,marker.Match,role,notchId);
            if(!doc.Objects.ModifyAttributes(id,actual,true))throw new InvalidOperationException("could not bind matching notch metadata.");
          }
          return id;
        }
        string components=marker.Geometry.Count>1?Guid.NewGuid().ToString("N"):string.Empty;
        Guid primary=Guid.Empty;
        for(int index=0;index<marker.Geometry.Count;index++)
        {
          if(components.Length>0)
          {
            attributes.SetUserString(MatchingNotches.Prefix+"component_set",components);
            attributes.SetUserString(MatchingNotches.Prefix+"component_index",index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MatchingNotches.Prefix+"component_count",marker.Geometry.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
          }
          var id=Add(marker.Geometry[index],"notch",NotchName,Guid.Empty,notchLayer);if(index==0)primary=id;
        }
        attributes.SetUserString(MatchingNotches.Prefix+"component_set",null);attributes.SetUserString(MatchingNotches.Prefix+"component_index",null);attributes.SetUserString(MatchingNotches.Prefix+"component_count",null);
        if(label!=null)Add(label.Text,"label",labelName,primary,labelLayer);
      }
    }

    private static bool Commit(RhinoDoc doc, Part part, PlanarPartSplitter.Proposal proposal,
      IReadOnlyList<Panel> panels, Plane cplane, Point3d anchor, double tolerance,
      out Output? output,IReadOnlyList<Transform> transforms,IReadOnlyList<int>? order=null,SplitDirection direction=DefaultDirection,IReadOnlyList<LocalCut>? localCuts=null,MatchingLabels.Style? labelStyle=null)
    {
      output=null;
      var added=new List<Guid>(); var groups=new List<int>(); var session=Guid.NewGuid().ToString("N");
      var referenceSources=new List<(Curve Curve,Guid Id)>();
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
          Guid Add(GeometryBase geometry,ObjectAttributes source,string? name)
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
            return id;
          }
          Add(panel.Cut,part.Attributes,BoundaryName);
          foreach(var item in panel.Details) Add(item.Geometry,item.Attributes,null);
          using var sewAttributes=new ObjectAttributes {LayerIndex=plot,Name=SewName};
          var sewSources=new List<(Curve Curve,Guid Id)>();
          foreach(var sew in panel.SewLines)sewSources.Add((sew,Add(sew,sewAttributes,SewName)));
          if(panel.Markers.Count>0)
          {
            if(panel.Labels.Count>0&&labelStyle==null) throw new InvalidOperationException("label settings are unavailable");
            string layer=labelStyle?.Layer??SewLayer;
            int labelLayer=string.IsNullOrWhiteSpace(layer)||LayerSelector.IsCurrentLayerValue(layer,DuplicateCommandSupport.CurrentLayerOption)
              ?doc.Layers.CurrentLayerIndex:UzipCommon.EnsureLayer(doc,layer);
            if(labelLayer<0) throw new InvalidOperationException("could not create the seam label layer");
            string notchLayerName=panel.Markers[0].Style.Layer;
            int notchLayer=string.IsNullOrWhiteSpace(notchLayerName)||LayerSelector.IsCurrentLayerValue(notchLayerName,MatchingNotches.CurrentLayer)?
              doc.Layers.CurrentLayerIndex:UzipCommon.EnsureLayer(doc,notchLayerName);
            if(notchLayer<0)throw new InvalidOperationException("could not create the seam notch layer");
            AddMatchingMarkers(doc,panel.Markers,panel.Labels,sewSources,transform,part.Plane.Normal,notchLayer,labelLayer,SeamLabelName,session,tolerance,labelStyle,Add);
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
              referenceSources.Add((segment.DuplicateCurve(),id));
            }
        }
        Guid AddReference(GeometryBase geometry,ObjectAttributes source,string? name)
        {
          using var attributes=source.Duplicate();attributes.LayerIndex=reference;attributes.Name=name??ReferenceLabelName;
          attributes.SetUserString(MetadataPrefix+"source",part.Attributes.ObjectId.ToString());
          var id=doc.Objects.Add(geometry,attributes);
          if(id==Guid.Empty)throw new InvalidOperationException("could not add source matching notches or labels.");
          added.Add(id);cuts.Add(id);return id;
        }
        foreach(var panel in panels)AddMatchingMarkers(doc,panel.Markers,panel.Labels,referenceSources,Transform.Identity,part.Plane.Normal,
          reference,reference,ReferenceLabelName,session,tolerance,labelStyle,AddReference);
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
      finally{foreach(var source in referenceSources)source.Curve.Dispose();}
    }
  }
}
