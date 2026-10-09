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
  using Rhino.Input;
  using Rhino.Input.Custom;
  using Part = vTools.PlanarPartSelection.Part;
  using Item = vTools.PlanarPartSelection.Item;
  using SplitDirection = vTools.PlanarPartSplitter.SplitDirection;


  internal static class PartSplitWorkflow
  {
    // Option defaults and customizable interaction/output settings
    internal const double DefaultWidth = 60.0; // Material width in model units; must exceed twice Seam.
    internal const double DefaultSeam = 0.5; // Extra model-unit allowance on each part at each common cut; zero disables overlap.
    internal const int DefaultMode = 0; // Mode index: 0 evenly divides the part; 1 fills each material width before the final remainder.
    private const int DefaultLayout = 1; // Layout index: 0 preserves original alignment; 1 aligns rotated copies side by side.
    private const double DefaultDistance = 1.0; // Clear model-unit distance between neighboring final cut edges; zero permits touching edges.
    private const int OriginalLayoutIndex = 0; // Fixed Original index in LayoutNames, independent of the default.
    private static readonly string[] LayoutNames = ["Original", "Vertical"]; // Available copy-arrangement modes.
    private const int EvenModeIndex = 0; // Fixed index of Even in ModeNames, independent of the configurable default mode.
    private const double DefaultAngle = 0.0; // Orientation in degrees before an automatic proposal exists; zero follows the fitted part plane's X axis.
    private const SplitDirection DefaultDirection = SplitDirection.Start; // Start/End fills from one end; Inward fills from outer ends; Outward fills from the center.
    private static readonly string[] DirectionNames = ["Start","End","Inward","Outward"]; // Per-part cut-placement and copy-order choices.
    private static readonly string[] ModeNames = ["Even", "MaxWidth"]; // Command-line values for the two split-spacing modes.
    private const double InitialLayoutGap = PlanarPartSplitter.DefaultGap; // Minimum model-unit separation used for the initial preview anchor.
    private const double PickRadius = 14.0; // Maximum cursor distance in screen pixels for picking a cut.
    private const double EndpointPickRadius = 18.0; // Screen-pixel distance from an endpoint that chooses pivoting rather than sliding a cut.
    private const int DragRefreshMilliseconds = 90; // Minimum interval between expensive clipped-preview rebuilds while dragging.
    private const bool AllowObjectSnaps = true; // true resolves native object snaps and displays snap cues while dragging cuts; false uses unsnapped points.
    private const int PreviewAlpha = 200; // Preview stroke opacity, zero invisible through 255 opaque.
    private const int PreviewPointSize = 3; // Preview point-marker diameter in screen pixels; positive integer.
    private const int BoundarySamples = 17; // Number of samples for distinguishing original boundary curves from interior details; three or greater.
    private const double BoundaryToleranceFactor = 2.0; // Model-tolerance multiplier for suppressing duplicate boundary strokes.
    private const double InitialLayoutGapFactor = 4.0; // Multiplier of the initial preview gap between the source and copied layout.
    private static readonly Color CutPreviewColor = Color.Orange; // RGB color for nominal source cut positions, without seam allowance.
    private const string ReferenceLayer = "Reference"; // Existing or new Rhino layer for nominal split cuts on the original part.
    private const string BoundaryName = "PartSplitBoundary"; // Object name for each new part's closed cutting perimeter.
    private const string CutName = "PartSplitCut"; // Object name for a nominal Reference-layer cut on the original part.
    private const string SewName = "PartSplitSewLine"; // Object name for nominal split lines included inside each output part.
    private const string SewLayer = "PLOT"; // Existing or new Rhino layer for the split parts' sew lines.
    private static readonly Color DefaultSewColor = Color.FromArgb(15,138,138); // RGB sew-line color when PLOT must be created.
    private const string GroupPrefix = "vPartSplit_"; // Group-name prefix followed by a session identifier and one-based part number.
    private const string MetadataPrefix = "vPartSplit."; // User-string key prefix identifying source, session, part, width, and seam allowance.
    private const string NotchName = "Notch"; // Source object name exempting notch fragments from duplicate-perimeter suppression.
    private const string NotchRoleKey = "notch.object_role"; // Source metadata key whose "notch" value preserves notch geometry at boundary contacts.
    private const string Section = "vPartSplit";
    private static double _width = DefaultWidth;
    private static double _seam = DefaultSeam;
    private static int _mode = DefaultMode;
    private static int _layout = DefaultLayout;
    private static double _distance = DefaultDistance;
    private static SplitDirection _direction = DefaultDirection;

    private sealed class State(Part part) : IDisposable
    {
      internal Part Part { get; } = part;
      internal PlanarPartSplitter.Proposal Proposal { get; set; } = null!;
      internal List<Panel> Panels { get; set; } = [];
      internal SplitDirection Direction { get; set; } = _direction;
      internal double Angle => RhinoMath.ToDegrees(Math.Atan2(Proposal.Frame.XAxis*Part.Plane.YAxis,Proposal.Frame.XAxis*Part.Plane.XAxis));
      public void Dispose() { foreach(var panel in Panels) panel.Dispose(); Part.Dispose(); }
    }
    private sealed record Output(List<Guid> Added, List<int> Groups, List<Guid> Copies);
    private sealed class Panel(Curve cut, BoundingBox bounds, List<Curve> preview, int bandIndex) : IDisposable
    {
      internal Curve Cut { get; } = cut;
      internal BoundingBox Bounds { get; } = bounds;
      internal int BandIndex { get; } = bandIndex;
      internal List<Curve> Preview { get; } = preview;
      internal List<Item> Details { get; } = [];
      internal List<Curve> SewLines { get; } = [];
      public void Dispose()
      {
        Cut.Dispose(); foreach (var curve in Preview) curve.Dispose();
        foreach (var curve in SewLines) curve.Dispose();
        foreach (var item in Details) item.Geometry.Dispose();
      }
    }
    private sealed record Options(OptionDouble Width, OptionDouble Seam, int ModeIndex,
      OptionDouble Distance, int LayoutIndex, OptionDouble? Angle, int AutoIndex, int DirectionIndex);

    private static PlanarPartSplitter.Settings Settings(SplitDirection? direction=null) => new(_width, _seam, Gap: _distance,
      Spacing: _mode == EvenModeIndex ? PlanarPartSplitter.SpacingMode.Even : PlanarPartSplitter.SpacingMode.MaxWidth)
      { Direction=direction??_direction };

    private static void Load() => ToolsOptionStore.Read<int>(Section, section =>
    {
      _width = ToolsOptionStore.TryGetDouble(section, "width", out var width) ? width : DefaultWidth;
      _seam = ToolsOptionStore.TryGetDouble(section, "seam", out var seam) ? seam : DefaultSeam;
      _mode = ToolsOptionStore.TryGetDouble(section, "mode", out var mode) && mode >= 0 && mode < ModeNames.Length && mode == Math.Truncate(mode) ? (int)mode : DefaultMode;
      _layout = ToolsOptionStore.TryGetDouble(section,"layout",out var layout) && layout>=0 && layout<LayoutNames.Length && layout==Math.Truncate(layout) ? (int)layout : DefaultLayout;
      _distance = ToolsOptionStore.TryGetDouble(section,"distance",out var distance) && double.IsFinite(distance) && distance>=0 ? distance : DefaultDistance;
      _direction = ToolsOptionStore.TryGetString(section,"direction",out var direction)&&Enum.TryParse<SplitDirection>(direction,true,out var parsed)&&Enum.IsDefined(parsed)?parsed:DefaultDirection;
      if (!ValidOptions(_width, _seam)) { _width = DefaultWidth; _seam = DefaultSeam; }
      return 0;
    });

    private static void Save() => ToolsOptionStore.Update(Section, section =>
    { section["width"] = _width; section["seam"] = _seam; section["mode"] = _mode;
      section["layout"] = _layout; section["distance"] = _distance; section["direction"] = _direction.ToString(); });

    private static bool ValidOptions(double width, double seam) =>
      double.IsFinite(width) && double.IsFinite(seam) && seam >= 0 && width > 2 * seam + RhinoMath.ZeroTolerance;

    private static Options AddOptions(GetBaseClass getter, double tolerance, double? angle = null, SplitDirection? direction = null)
    {
      var width = new OptionDouble(_width, tolerance, double.MaxValue);
      var seam = new OptionDouble(_seam, 0, double.MaxValue);
      getter.AddOptionDouble("Width", ref width);
      getter.AddOptionDouble("Seam", ref seam);
      int mode = getter.AddOptionList("Mode", ModeNames, _mode);
      var distance = new OptionDouble(_distance,0,double.MaxValue);
      getter.AddOptionDouble("Distance",ref distance);
      int layout = getter.AddOptionList("Layout",LayoutNames,_layout);
      OptionDouble? rotation = null;
      int auto = -1;
      int directionIndex=getter.AddOptionList("Direction",DirectionNames,(int)(direction??_direction));
      if (angle.HasValue)
      {
        var angleOption = new OptionDouble(angle.Value, -double.MaxValue, double.MaxValue);
        getter.AddOptionDouble("Angle", ref angleOption);
        rotation = angleOption;
        auto = getter.AddOption("Auto");
      }
      getter.AcceptNumber(true, false);
      return new Options(width, seam, mode, distance, layout, rotation, auto, directionIndex);
    }

    private static bool ReadOptions(GetBaseClass getter, GetResult result, Options options)
    {
      var width = result == GetResult.Number ? getter.Number() : options.Width.CurrentValue;
      var seam = options.Seam.CurrentValue;
      if (!ValidOptions(width, seam) || !double.IsFinite(options.Distance.CurrentValue) || options.Distance.CurrentValue<0)
      { RhinoApp.WriteLine("vPartSplit: Width must exceed twice Seam; Seam cannot be negative."); return false; }
      _width = width; _seam = seam;
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
        getter=>selectionOptions=AddOptions(getter,tolerance),
        (getter,result)=> { if(ReadOptions(getter,result,selectionOptions!)) Save(); },out var selectionResult);
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
      var anchor=cplane.PointAt(bounds.Max.X+Math.Max(_distance,InitialLayoutGap)*InitialLayoutGapFactor,bounds.Min.Y);
      bool committed=false;
      void ClearHover() { highlight.SetObjects([]); hoverGeometry?.Dispose(); hoverGeometry=null; }
      void Hover(int partIndex,int seamIndex)
      {
        ClearHover();
        if(partIndex<0||seamIndex<0) return;
        var state=states[partIndex];
        hoverGeometry=PlanarPartSplitter.Seam(state.Part.Outline,state.Proposal.Frame,state.Proposal.Seams[seamIndex],tolerance);
        if(hoverGeometry!=null) highlight.SetObjects([hoverId],new Dictionary<Guid,GeometryBase>{{hoverId,hoverGeometry}});
      }
      (int Part,int Seam) Find(RhinoViewport viewport,int x,int y)
      {
        for(int index=0;index<states.Count;index++)
        {
          var state=states[index];
          var seam=FindCut(viewport,x,y,state.Part.Outline,state.Proposal,tolerance);
          if(seam>=0) return(index,seam);
        }
        return(-1,-1);
      }
      bool Replan(bool automatic,double? newAngle=null)
      {
        var pending=new List<(State State,PlanarPartSplitter.Proposal Proposal,List<Panel> Panels)>();
        foreach(var state in states)
        {
          PlanarPartSplitter.Proposal? next=null;
          if(automatic)
          {
            if(PlanarPartSplitter.TryPropose(state.Part.Outline,state.Part.Plane,Settings(state.Direction),out var candidate)) next=candidate;
          }
          else
          {
            double angle=ReferenceEquals(state,states[active])&&newAngle.HasValue?newAngle.Value:state.Angle;
            var frame=PlanarPartSplitter.RotatedFrame(state.Part.Plane,RhinoMath.ToRadians(angle));
            var box=state.Part.Outline.GetBoundingBox(frame);
            next=PlanarPartSplitter.AtPosition(frame,box.Min.Y,box.Max.Y,Settings(state.Direction),null);
          }
          var panels=next==null?[]:BuildPanels(state.Part,next,Settings(state.Direction),tolerance);
          if(next==null||panels.Count==0)
          { foreach(var item in pending) foreach(var panel in item.Panels) panel.Dispose(); return false; }
          pending.Add((state,next,panels));
        }
        ClearHover();
        foreach(var item in pending)
        { foreach(var panel in item.State.Panels) panel.Dispose(); item.State.Proposal=item.Proposal; item.State.Panels=item.Panels; }
        return true;
      }
      (Options Values,int PartIndex) Configure(GetPoint getter)
      {
        var options=AddOptions(getter,tolerance,states[active].Angle,states[active].Direction);
        int partOption=states.Count>1?getter.AddOptionList("Part",Enumerable.Range(1,states.Count).Select(index=>index.ToString()).ToArray(),active):-1;
        return(options,partOption);
      }
      void Change(GetPoint getter,GetResult result,(Options Values,int PartIndex) controls)
      {
        if(result==GetResult.Option&&getter.Option()?.Index==controls.PartIndex)
        { active=getter.Option().CurrentListOptionIndex; return; }
        var previous=(_width,_seam,_mode,_layout,_distance,_direction);
        var previousDirection=states[active].Direction;
        if(!ReadOptions(getter,result,controls.Values)) return;
        bool directionChanged=result==GetResult.Option&&getter.Option()?.Index==controls.Values.DirectionIndex;
        if(directionChanged) states[active].Direction=_direction;
        bool automatic=result==GetResult.Option&&getter.Option()?.Index==controls.Values.AutoIndex;
        double angle=controls.Values.Angle!.CurrentValue;
        bool geometryChanged=directionChanged||automatic||_width!=previous._width||_seam!=previous._seam||_mode!=previous._mode||Math.Abs(angle-states[active].Angle)>RhinoMath.ZeroTolerance;
        if(geometryChanged&&!Replan(automatic,angle))
        { (_width,_seam,_mode,_layout,_distance,_direction)=previous; states[active].Direction=previousDirection; RhinoApp.WriteLine("vPartSplit: previous valid settings and all part previews retained."); return; }
        Save();
      }
      try
      {
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
          bool Drag(Point3d point)
          {
            if(movingPart<0||movingSeam<0||!point.IsValid) return false;
            var state=states[movingPart];
            var projected=state.Part.Plane.ClosestPoint(point);
            var target=pivoting?pivot:dragFrame.PointAt(0,seamY+PlanarPartSplitter.FrameY(dragFrame,projected)-grabY);
            var direction=projected-pivot;
            if(pivoting&&direction.Length<=tolerance) return false;
            if(pivoting&&direction*dragFrame.XAxis<0) direction=-direction;
            double angle=pivoting?Math.Atan2(direction*state.Part.Plane.YAxis,direction*state.Part.Plane.XAxis)
              :Math.Atan2(dragFrame.XAxis*state.Part.Plane.YAxis,dragFrame.XAxis*state.Part.Plane.XAxis);
            if(!PlanarPartSplitter.TryAtPivot(state.Part.Outline,state.Part.Plane,Settings(state.Direction),angle,target,out var next,out var index)) return false;
            var panels=BuildPanels(state.Part,next,Settings(state.Direction),tolerance);
            if(panels.Count==0) return false;
            ClearHover();
            foreach(var panel in state.Panels) panel.Dispose();
            state.Proposal=next; state.Panels=panels; movingSeam=index;
            controls.Values.Angle!.CurrentValue=state.Angle;
            Hover(movingPart,movingSeam);
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
                if(next.Part>=0) { active=next.Part; controls.Values.Angle!.CurrentValue=states[active].Angle; }
              }
            }
          };
          getter.MouseDown+=(_,e)=>
          {
            var selected=Find(e.Viewport,e.WindowPoint.X,e.WindowPoint.Y);
            movingPart=selected.Part; movingSeam=selected.Seam;
            if(movingPart<0) return;
            active=movingPart; var state=states[active];
            using var cut=PlanarPartSplitter.Seam(state.Part.Outline,state.Proposal.Frame,state.Proposal.Seams[movingSeam],tolerance);
            if(cut==null) { movingPart=-1; return; }
            var start=e.Viewport.WorldToClient(cut.PointAtStart); var end=e.Viewport.WorldToClient(cut.PointAtEnd);
            double Distance(Point2d pixel)=>Math.Sqrt(Math.Pow(pixel.X-e.WindowPoint.X,2)+Math.Pow(pixel.Y-e.WindowPoint.Y,2));
            pivoting=Math.Min(Distance(start),Distance(end))<=EndpointPickRadius;
            var grab=state.Part.Plane.ClosestPoint(e.Point);
            dragFrame=state.Proposal.Frame; seamY=state.Proposal.Seams[movingSeam]; grabY=PlanarPartSplitter.FrameY(dragFrame,grab);
            pivot=grab.DistanceTo(cut.PointAtStart)<=grab.DistanceTo(cut.PointAtEnd)?cut.PointAtEnd:cut.PointAtStart;
            if(pivoting) { getter.SetBasePoint(pivot,false); getter.Constrain(state.Part.Outline,true); }
            dragging=true;
          };
          getter.DynamicDraw+=(_,e)=>
          {
            var now=System.Environment.TickCount64;
            if(dragging&&now-refreshed>=DragRefreshMilliseconds) { refreshed=now; Drag(e.CurrentPoint); }
            DrawBatch(e.Display,doc,states,cplane,anchor,tolerance);
          };
          var result=getter.Get(onMouseUp:true); ClearHover();
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
          getter.DynamicDraw+=(_,e)=> { anchor=e.CurrentPoint; DrawBatch(e.Display,doc,states,cplane,anchor,tolerance); };
          var result=getter.Get();
          if(result is GetResult.Option or GetResult.Number) { Change(getter,result,controls); continue; }
          if(result is not (GetResult.Point or GetResult.Nothing)) return Result.Cancel;
          if(result==GetResult.Point) anchor=getter.Point();
          var placements=BatchPlacements(states,cplane,anchor);
          var outputs=new List<Output>();
          foreach(var (state,index) in states.Select((state,index)=>(state,index)))
          {
            if(!Commit(doc,state.Part,state.Proposal,state.Panels,cplane,anchor,tolerance,out var output,placements[index],PanelOrder(state.Panels,state.Proposal,state.Direction),state.Direction))
            {
              foreach(var created in outputs) { foreach(var id in created.Added) doc.Objects.Delete(id,true); foreach(var group in created.Groups) doc.Groups.Delete(group); }
              return Result.Failure;
            }
            outputs.Add(output!);
          }
          committed=true; Save();
          RhinoApp.WriteLine($"vPartSplit: split {states.Count} source part(s) into {states.Sum(state=>state.Panels.Count)} grouped part(s); Layout={LayoutNames[_layout]}, Distance={_distance:G6}.");
          doc.Objects.Select(outputs.SelectMany(output=>output.Copies));
          return Result.Success;
        }
      }
      catch(Exception ex) { Log.Write("vPartSplit","failed: {0}",ex); RhinoApp.WriteLine("vPartSplit: {0}",ex.Message); return Result.Failure; }
      finally
      { ClearHover(); foreach(var state in states) state.Dispose(); if(!committed) foreach(var id in ids) doc.Objects.FindId(id)?.Select(true); doc.Views.Redraw(); }
    }

    private static bool Coincident(Curve curve, Curve boundary, double tolerance) =>
      Enumerable.Range(0, BoundarySamples).All(index =>
      {
        var point = curve.PointAtNormalizedLength((double)index/(BoundarySamples-1));
        return boundary.ClosestPoint(point, out var parameter) && point.DistanceTo(boundary.PointAt(parameter)) <= tolerance*BoundaryToleranceFactor;
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
              var panel = new Panel(cut.DuplicateCurve(), bounds, matching, index);
              panels.Add(panel);
              foreach(var seamIndex in new[] { index-1,index })
              {
                if(seamIndex<0 || seamIndex>=proposal.Seams.Count) continue;
                using var sew = PlanarPartSplitter.Seam(part.Outline,proposal.Frame,proposal.Seams[seamIndex],tolerance);
                if(sew!=null) panel.SewLines.AddRange(PlanarPartSplitter.ClipCurve(sew,cut,proposal.Frame,tolerance));
              }
              foreach (var item in part.Items)
              {
                if (item.Geometry is Curve curve)
                {
                  bool boundaryLayer = item.Attributes.LayerIndex == part.Attributes.LayerIndex &&
                    !string.Equals(item.Attributes.Name,NotchName,StringComparison.OrdinalIgnoreCase) &&
                    item.Attributes.GetUserString(NotchRoleKey) != "notch";
                  foreach (var piece in PlanarPartSplitter.ClipCurve(curve,cut,proposal.Frame,tolerance))
                    if (boundaryLayer && Coincident(piece,part.Outline,tolerance)) piece.Dispose();
                    else panel.Details.Add(item with { Geometry=piece });
                }
                else
                {
                  var point = RepresentativePoint(item.Geometry);
                  double y = PlanarPartSplitter.FrameY(proposal.Frame,point);
                  bool owns = y >= nominal.Lower-tolerance && (index==proposal.Seams.Count
                    ? y <= nominal.Upper+tolerance : y < nominal.Upper-tolerance);
                  if (point.IsValid && owns && cut.Contains(point,proposal.Frame,tolerance) is PointContainment.Inside or PointContainment.Coincident)
                  {
                    panel.Details.Add(item with { Geometry=item.Geometry.Duplicate() });
                  }
                }
              }
            }
          }
          finally { foreach (var curve in cuts.Concat(previews)) curve.Dispose(); }
        }
        if (panels.Count == 0 || panels.Count > settings.MaxParts) throw new InvalidOperationException("no valid split panels");
        return panels;
      }
      catch (Exception ex)
      { foreach (var panel in panels) panel.Dispose(); Log.Write("vPartSplit","layout rejected: {0}",ex.Message); return []; }
    }

    private static Point3d RepresentativePoint(GeometryBase geometry) => geometry switch
    {
      Rhino.Geometry.Point point => point.Location,
      TextDot dot => dot.Point,
      _ => geometry.GetBoundingBox(true).Center
    };

    private static int FindCut(RhinoViewport viewport, int x, int y, Curve source, PlanarPartSplitter.Proposal proposal, double tolerance)
    {
      int nearest = -1; double radius = PickRadius;
      for (int index=0; index<proposal.Seams.Count; index++)
      {
        using var cut = PlanarPartSplitter.Seam(source,proposal.Frame,proposal.Seams[index],tolerance);
        if (cut == null) continue;
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

    private static Transform[] PartPlacements(State state,Plane cplane,Point3d anchor)
    {
      var transforms=new Transform[state.Panels.Count];
      if(_layout==OriginalLayoutIndex)
      {
        var sourceBounds=state.Part.Outline.GetBoundingBox(state.Part.Plane);
        var sourceBase=state.Part.Plane.PointAt(sourceBounds.Min.X,sourceBounds.Min.Y);
        for(int index=0;index<state.Panels.Count;index++)
          transforms[index]=Transform.Translation(anchor-sourceBase+state.Proposal.Frame.YAxis*
            OriginalBandSpread(state.Panels[index].BandIndex,_seam,_distance));
        return transforms;
      }
      var order=PanelOrder(state.Panels,state.Proposal,state.Direction);
      double x=0;
      foreach(var index in order)
      {
        var panel=state.Panels[index];
        transforms[index]=Placement(state.Proposal.Frame,cplane,anchor,panel.Bounds,x);
        x+=VerticalWidth(panel.Bounds)+_distance;
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
          foreach(var corner in state.Panels[index].Bounds.GetCorners())
          {
            var world=placements[index]*state.Proposal.Frame.PointAt(corner.X,corner.Y,corner.Z)-anchor;
            box.Union(new Point3d(world*cplane.XAxis,world*cplane.YAxis,world*cplane.Normal));
          }
        var shift=Transform.Translation(-cplane.XAxis*box.Min.X+cplane.YAxis*(y-box.Min.Y));
        result[source]=placements.Select(transform=>shift*transform).ToArray();
        y+=box.Max.Y-box.Min.Y+_distance;
      }
      return result;
    }

    private static void DrawBatch(DisplayPipeline display,RhinoDoc doc,IReadOnlyList<State> states,Plane cplane,Point3d anchor,double tolerance)
    {
      var transforms=BatchPlacements(states,cplane,anchor);
      int plot=doc.Layers.FindByFullPath(SewLayer,-1);
      var plotColor=plot>=0?doc.Layers[plot].Color:DefaultSewColor;
      for(int index=0;index<states.Count;index++)
        Draw(display,states[index].Part,states[index].Proposal,states[index].Panels,cplane,anchor,tolerance,transforms[index],plotColor);
    }

    private static void Draw(DisplayPipeline display, Part part, PlanarPartSplitter.Proposal proposal,
      IReadOnlyList<Panel> panels, Plane cplane, Point3d anchor, double tolerance,
      IReadOnlyList<Transform> transforms,Color plotColor)
    {
      foreach (var y in proposal.Seams)
      {
        using var cut=PlanarPartSplitter.Seam(part.Outline,proposal.Frame,y,tolerance);
        if (cut==null) continue;
        foreach(var segment in PlanarPartSplitter.ClipCurve(cut,part.Outline,proposal.Frame,tolerance))
          using(segment) PreviewDisplay.DrawCurve(display,segment,CutPreviewColor);
      }
      for(int index=0;index<panels.Count;index++)
      {
        var panel=panels[index];
        display.PushModelTransform(transforms[index]);
        try
        {
          var color=Color.FromArgb(PreviewAlpha,part.Items.First(item=>item.Attributes.LayerIndex==part.Attributes.LayerIndex).Color);
          PreviewDisplay.DrawCurve(display,panel.Cut,color);
          foreach(var sew in panel.SewLines) PreviewDisplay.DrawCurve(display,sew,Color.FromArgb(PreviewAlpha,plotColor));
          foreach(var item in panel.Details)
          {
            var faded=Color.FromArgb(PreviewAlpha,item.Color);
            switch(item.Geometry)
            {
              case Curve curve: PreviewDisplay.DrawCurve(display,curve,faded); break;
              case AnnotationBase text: display.DrawAnnotation(text,faded); break;
              case Rhino.Geometry.Point point: display.DrawPoint(point.Location,PointStyle.RoundSimple,PreviewPointSize,faded); break;
              case TextDot dot: display.DrawDot(dot.Point,dot.Text,faded,Color.White); break;
            }
          }
        }
        finally { display.PopModelTransform(); }
      }
    }

    private static bool Commit(RhinoDoc doc, Part part, PlanarPartSplitter.Proposal proposal,
      IReadOnlyList<Panel> panels, Plane cplane, Point3d anchor, double tolerance,
      out Output? output,IReadOnlyList<Transform> transforms,IReadOnlyList<int>? order=null,SplitDirection direction=DefaultDirection)
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
        }
        var cuts=new List<Guid>();
        var reference=UzipCommon.EnsureLayer(doc,ReferenceLayer);
        foreach(var y in proposal.Seams)
        {
          using var cut=PlanarPartSplitter.Seam(part.Outline,proposal.Frame,y,tolerance);
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
