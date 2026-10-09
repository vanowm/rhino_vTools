namespace vTools.Commands
{
  using Rhino;
  using Rhino.Commands;


  /// <summary>Manages grain-aware part layouts across fixed-size material tables.</summary>
  public sealed class vNest : vToolsCommand
  {
    public override string EnglishName => "vNest";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode) => NestWorkflow.Run(doc,mode);
  }
}

namespace vTools.Commands
{
  using System.Drawing;
  using Rhino;
  using Rhino.Commands;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using Rhino.Input;
  using Rhino.Input.Custom;
  using Part = vTools.PlanarPartSelection.Part;


  internal static partial class NestWorkflow
  {
    // Option defaults and customizable behavior/output settings
    internal const double DefaultWidth = 60.0; // Material-table width in document units; positive.
    internal const double DefaultLength = 315.0; // Material-table length in document units; positive.
    internal const double DefaultPartGap = NestPackingEngine.DefaultGap; // Minimum distance between part outlines in document units; nonnegative, zero permits touching.
    internal const double DefaultSearchTime = NestContourPackingEngine.DefaultSearchSeconds; // Refinement seconds after the initial layout; zero disables refinement.
    internal const double MaximumSearchTime = 3600.0; // Maximum user-requested refinement time in seconds; positive.
    internal const GrainMode DefaultGrain = GrainMode.Auto; // Auto uses detected/picked guides, Force requests missing guides, On falls back to source up/down, Off ignores guides.
    internal const RotationMode DefaultRotationMode = RotationMode.Step180; // None, Any, Step45, Step90, Step180, or Custom; default offers the grain/source direction and its opposite.
    internal const double DefaultRotationStep = 45.0; // Custom rotation increment in degrees, 0.1 through 360; preset modes ignore this value.
    internal const double MinimumRotationStep = 0.1; // Minimum custom degree increment; limits discrete alternatives to at most 3,600.
    internal const double MaximumRotationStep = 360.0; // Maximum custom degree increment; one complete turn gives only the reference orientation.
    internal const bool DefaultAllowReverse = true; // true offers both grain-aligned directions; false permits only the forward grain direction.
    internal const bool DefaultObeyGrain = true; // true constrains parts with grain to the deviation/reversal rules; false uses unconstrained source-relative rotation presets.
    internal const double DefaultGrainDeviation = 0.0; // Allowed degrees either side of permitted grain-aligned directions; zero keeps exact orientations.
    internal const double MaximumGrainDeviation = 90.0; // Maximum grain deviation in degrees; zero through a quarter turn.
    internal const double SourceGrainAngle = 90.0; // Source-plane degrees used by Grain=On when no guide exists; source up/down.
    internal const TableStack DefaultStack = TableStack.Horizontal; // Horizontal places tables side by side; Vertical stacks horizontal tables downward.
    internal const bool DefaultIncluded = true; // true includes new parts in nesting; false initially excludes them.
    internal const bool DefaultFixed = false; // true keeps new parts at their entered table coordinates; false packs them automatically.
    internal const bool DefaultManualRotation = false; // true initially fixes the specified rotation; false chooses among allowed rotations.
    internal const bool DefaultAutoNest = false; // true re-nests after relevant edits/additions; false waits for the Nest button.
    internal const bool DefaultAllowFlip = false; // true offers mirrored cutting outlines; false preserves each part's handedness.
    private const bool GrainObjectSnaps = true; // true enables native snaps while drawing a grain guide; false uses unsnapped points.
    internal const double DefaultRotation = 0.0; // Source-relative rotation in degrees before the first layout; -180 through 180.
    internal const double DefaultCoordinate = 0.0; // Initial local table X/Y in document units; zero or greater.
    internal const int DefaultTable = 0; // Zero-based initial nest-table index; UI displays index plus one.
    internal const int DefaultCopies = 1; // Number of individually nested copies per included source part; positive integer.
    internal const int MaximumCopies = 1000; // Maximum requested copies per source part; positive integer limiting accidental expansion.
    internal const double TableGap = 0.5; // Model-unit space between displayed material tables; zero or greater.
    internal const double MaterialBasisAngle = -90.0; // Degrees from CPlane X to the material-width axis; length runs horizontally along CPlane X.
    internal const int PreviewAlpha = 200; // Preview stroke alpha from zero invisible through 255 opaque.
    internal const int PreviewPointSize = 3; // Preview point diameter in display pixels; positive.
    internal static readonly Color TableColor = Color.Gray; // RGB color of material-table preview outlines.
    internal const double FreeAngleStep = 5.0; // Degrees between free-rotation bounding-box candidates; greater than zero through 90.
    private const int FreePoseLimit = 8; // Maximum free-rotation alternatives, including each exact half-turn counterpart; positive even integer.
    private const int RotationRefinementSeeds = 6; // Best coarse free-rotation candidates refined locally; positive integer.
    private const int RotationRefinementPasses = 9; // Step-halving refinement passes per seed; positive integer, reaching sub-degree angles.
    private const double LinearEdgeTolerance = 1e-6; // Model-unit tolerance for exact straight boundary directions added to rotation candidates.
    private const double OutlineTolerance = 0.001; // Model-unit deviation used for curved outline polygon sampling; positive.
    private const double OutlineAngleTolerance = 0.05; // Radian turning tolerance for native curve-to-polyline sampling; positive.
    private const double PoseDimensionTolerance = 1e-6; // Model-unit threshold for coalescing equivalent rotated bounds; positive.
    private const double PoseAngleSeparation = FreeAngleStep/2; // Minimum degree separation between polygon rotation alternatives; preserves half-turn concavity choices.
    internal const double GrainAngularTolerance = 0.05; // Allowed numeric grain-axis deviation in degrees; positive and much smaller than a quarter turn.
    internal const string GrainIdentifier = "GRAIN"; // Case-insensitive object name or leaf layer name identifying an open grain guide.
    private const double GrainCurveSample = 0.5; // Arc-length fraction, zero through one, used for automatic tangents on curved marked grain guides.
    private const string NameLayer = "Reference"; // Preferred leaf layer for the large text label identifying a part.
    internal const string TableLayer = "Reference"; // Existing or new layer for accepted material-table outlines.
    internal const string TableName = "NestTable"; // Object name for accepted material-table rectangles.
    internal const string DefaultTableNamePrefix = "table"; // Default editable label/group stem followed by the one-based table number.
    private const double TableLabelHeightFraction = 0.08; // Visible glyph height as a fraction of material width; positive, 0.08 gives 4.8 units for a 60-unit table.
    private const double TableLabelGapFraction = 0.2; // Border clearance as a fraction of label glyph height; positive.
    private const double TableLabelRowGapFraction = 0.2; // Inter-row clearance as a fraction of label glyph height; positive.
    private const double TableLabelBoxPaddingFraction = 0.2; // Padding inside the label frame as a fraction of visible glyph height; positive.
    private const double TableLabelScale = 1.0; // Fixed annotation display scale; positive, independent of the document's model-space text scale.
    private const string TableWidthFormat = "0.###"; // Width display preserves up to three fractional decimal digits in document units.
    private const string TableLengthFormat = "0.0"; // Occupied length and yardage are rounded to one decimal place.
    private const int TableNameLimit = 80; // Maximum editable table-name characters; longer input is truncated after whitespace normalization.
    private const int TableNameAttempts = 10000; // Maximum numeric suffix trials for unique document group names.
    internal const string GroupPrefix = "vNest_"; // Output group prefix followed by session and table/part identifiers.
    internal const string MetadataPrefix = "vNest."; // Output metadata namespace for session, table, part, and rotation.
    private const string Section = "vNest";
    internal static readonly string[] GrainNames = ["Auto","Force","On","Off"]; // Named command-line and GUI grain modes.
    internal static readonly string[] RotationNames = ["NoRotation","AnyRotation","45","90","180","Custom"]; // Command preset values in RotationMode enum order; numeric values are increments, not limits.
    internal static readonly string[] RotationLabels = ["No rotation","Any rotation","45","90","180","Custom"]; // Human-readable GUI rotation-step presets in RotationMode enum order.
    internal static readonly string[] ReversalLabels = ["No reversal","Allow 180 reversal"]; // Grain-constrained orientations; index zero forbids reversal, one permits it.
    internal static readonly string[] StackNames = ["Horizontal","Vertical"]; // Table-arrangement direction; does not rotate any table or part.
    internal static double Width=DefaultWidth,Length=DefaultLength,RotationStep=DefaultRotationStep;
    internal static double SearchTime=DefaultSearchTime;
    internal static double PartGap=DefaultPartGap;
    internal static double GrainDeviation=DefaultGrainDeviation;
    internal static GrainMode Grain=DefaultGrain;
    internal static TableStack Stack=DefaultStack;
    internal static bool AllowFlip=DefaultAllowFlip;
    internal static RotationMode RotationChoice=DefaultRotationMode;
    internal static bool AllowReverse=DefaultAllowReverse;
    internal static bool ObeyGrain=DefaultObeyGrain;
    internal static NestWindow? ActiveWindow;
    private static int _nextId;

    internal enum GrainMode { Auto,Force,On,Off }
    internal enum RotationMode { None,Any,Step45,Step90,Step180,Custom }
    internal enum TableStack { Horizontal,Vertical }
    private sealed record PoseKey(double Width,double Length,GrainMode Mode,RotationMode RotationChoice,double Step,bool Reverse,bool Obey,double Deviation,double? Guide,bool Manual,double Rotation,bool AllowFlip,bool? Flip);
    internal sealed class Row(Part part,int id,Vector3d? guide) : IDisposable
    {
      internal Part Part { get; }=part;
      internal int Id { get; }=id;
      internal string Name { get; set; }=id.ToString(System.Globalization.CultureInfo.InvariantCulture);
      internal Vector3d? Guide { get; set; }=guide;
      internal Vector3d? DetectedGuide { get; }=guide;
      internal int Copies { get; set; }=DefaultCopies;
      private readonly List<int> _instances=[id];
      private readonly Dictionary<int,int> _instanceIndices=new() {[id]=0};
      internal IReadOnlyList<int> InstanceIds()
      {
        int count=Math.Clamp(Copies,DefaultCopies,MaximumCopies);
        while(_instances.Count<count) { int instance=++_nextId; _instanceIndices[instance]=_instances.Count; _instances.Add(instance); }
        return _instances.Take(count).ToArray();
      }
      internal bool OwnsInstance(int instance)=>_instanceIndices.TryGetValue(instance,out var index)&&index<Math.Clamp(Copies,DefaultCopies,MaximumCopies);
      internal bool Included { get; set; }=DefaultIncluded;
      internal bool Fixed { get; set; }=DefaultFixed;
      internal bool ManualRotation { get; set; }=DefaultManualRotation;
      internal bool Flipped { get; set; }=DefaultAllowFlip;
      internal bool? FlipOverride { get; set; }
      internal GrainMode? GrainOverride { get; set; }
      internal RotationMode? RotationOverride { get; set; }
      internal double? RotationStepOverride { get; set; }
      internal bool? ObeyGrainOverride { get; set; }
      internal bool? AllowReverseOverride { get; set; }
      internal double? GrainDeviationOverride { get; set; }
      internal double Rotation { get; set; }=DefaultRotation;
      internal int Table { get; set; }=DefaultTable;
      internal double X { get; set; }=DefaultCoordinate;
      internal double Y { get; set; }=DefaultCoordinate;
      internal string Status { get; set; }=string.Empty;
      private PoseKey? _poseKey;
      private List<NestPackingEngine.Pose>? _poses;
      private Point3d[]? _outlinePoints;
      internal IReadOnlyList<Point3d> OutlinePoints()
      {
        if(_outlinePoints!=null) return _outlinePoints;
        if(Part.Outline.TryGetPolyline(out var polyline)) _outlinePoints=polyline.ToArray();
        else
        {
          using var approximation=Part.Outline.ToPolyline(OutlineTolerance,OutlineAngleTolerance,0,0);
          _outlinePoints=approximation!=null&&approximation.TryGetPolyline(out polyline)?polyline.ToArray():[];
        }
        if(_outlinePoints.Length>1&&_outlinePoints[0].DistanceTo(_outlinePoints[^1])<=OutlineTolerance) _outlinePoints=_outlinePoints[..^1];
        return _outlinePoints;
      }
      internal List<NestPackingEngine.Pose> CachedPoses(double? guide,Func<List<NestPackingEngine.Pose>> create)
      {
        var key=new PoseKey(Width,Length,GrainOverride??Grain,RotationOverride??RotationChoice,RotationStepOverride??RotationStep,AllowReverseOverride??AllowReverse,ObeyGrainOverride??ObeyGrain,GrainDeviationOverride??GrainDeviation,guide,ManualRotation||Fixed,ManualRotation||Fixed?Rotation:DefaultRotation,AllowFlip,Fixed?Flipped:FlipOverride);
        if(key!=_poseKey||_poses==null) { _poses=create(); _poseKey=key; }
        return _poses;
      }
      public void Dispose()=>Part.Dispose();
    }
    private sealed record Options(OptionDouble Width,OptionDouble Length,int GrainIndex,OptionDouble RotationStep,int WidthIndex,int LengthIndex,int RotationIndex,int StepIndex,OptionToggle Reverse,int ReverseIndex,int StackIndex,OptionToggle Flip,int FlipIndex,OptionDouble Search,int SearchIndex,OptionDouble Deviation,int DeviationIndex,OptionToggle Obey,int ObeyIndex,OptionDouble Gap,int GapIndex);

    internal static Result Run(RhinoDoc doc,RunMode mode)
    {
      var preselection=doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id).ToArray();
      if(ActiveWindow is {IsDisposed:false} existing)
      {
        if(existing.DocumentSerial==doc.RuntimeSerialNumber)
        { existing.AddPreselectedParts(preselection); existing.BringToFront(); existing.Focus(); return Result.Success; }
        existing.Close();
      }
      Load();
      if(mode==RunMode.Scripted)
      {
        using var getter=new GetOption(); getter.SetCommandPrompt("Nest settings"); getter.AcceptNothing(true);
        while(true)
        {
          getter.ClearCommandOptions(); var options=AddOptions(getter); var result=getter.Get();
          if(result==GetResult.Nothing) break;
          if(result is not (GetResult.Option or GetResult.Number)) return getter.CommandResult();
          if(ReadOptions(getter,result,options)) Save();
        }
      }
      var window=new NestWindow(doc,[]) {Owner=Rhino.UI.RhinoEtoApp.MainWindow};
      ActiveWindow=window;
      window.Closed+=(_,_)=> { if(ReferenceEquals(ActiveWindow,window)) ActiveWindow=null; };
      try
      {
        window.AddPreselectedParts(preselection); window.SyncSelection(); window.Show();
      }
      catch(Exception ex)
      { window.Close(); Log.Write("vNest","selection failed: {0}",ex); return Result.Failure; }
      return Result.Success;
    }

    private static Options AddOptions(GetBaseClass getter)
    {
      var width=new OptionDouble(Width,RhinoMath.ZeroTolerance,double.MaxValue);
      var length=new OptionDouble(Length,RhinoMath.ZeroTolerance,double.MaxValue);
      var step=new OptionDouble(RotationStep,MinimumRotationStep,MaximumRotationStep);
      int widthIndex=getter.AddOptionDouble("Width",ref width),lengthIndex=getter.AddOptionDouble("Length",ref length);
      int grain=getter.AddOptionList("Grain",GrainNames,(int)Grain);
      int rotationIndex=getter.AddOptionList("Rotation",RotationNames,(int)RotationChoice);
      int stepIndex=RotationChoice==RotationMode.Custom?getter.AddOptionDouble("RotationStep",ref step):-1;
      var reverse=new OptionToggle(AllowReverse,"No","Yes"); int reverseIndex=getter.AddOptionToggle("Allow180Turn",ref reverse);
      var obey=new OptionToggle(ObeyGrain,"No","Yes"); int obeyIndex=getter.AddOptionToggle("ObeyGrain",ref obey);
      var deviation=new OptionDouble(GrainDeviation,DefaultGrainDeviation,MaximumGrainDeviation); int deviationIndex=getter.AddOptionDouble("GrainDeviation",ref deviation);
      int stackIndex=getter.AddOptionList("Stack",StackNames,(int)Stack);
      var flip=new OptionToggle(AllowFlip,"No","Yes"); int flipIndex=getter.AddOptionToggle("Flip",ref flip);
      var search=new OptionDouble(SearchTime,0,MaximumSearchTime); int searchIndex=getter.AddOptionDouble("SearchTime",ref search);
      var gap=new OptionDouble(PartGap,DefaultPartGap,double.MaxValue); int gapIndex=getter.AddOptionDouble("Gap",ref gap);
      getter.AcceptNumber(true,false);
      return new Options(width,length,grain,step,widthIndex,lengthIndex,rotationIndex,stepIndex,reverse,reverseIndex,stackIndex,flip,flipIndex,search,searchIndex,deviation,deviationIndex,obey,obeyIndex,gap,gapIndex);
    }

    private static bool ReadOptions(GetBaseClass getter,GetResult result,Options options)
    {
      int index=getter.Option()?.Index??-1;
      double width=result==GetResult.Number?getter.Number():index==options.WidthIndex?options.Width.CurrentValue:Width;
      double length=index==options.LengthIndex?options.Length.CurrentValue:Length;
      if(!double.IsFinite(width)||width<=0||!double.IsFinite(length)||length<=0) return false;
      Width=width; Length=length;
      if(result==GetResult.Option&&index==options.RotationIndex) RotationChoice=(RotationMode)getter.Option().CurrentListOptionIndex;
      if(index==options.StepIndex&&options.StepIndex>=0) RotationStep=options.RotationStep.CurrentValue;
      if(index==options.ReverseIndex) AllowReverse=options.Reverse.CurrentValue;
      if(index==options.ObeyIndex) ObeyGrain=options.Obey.CurrentValue;
      if(index==options.DeviationIndex) GrainDeviation=options.Deviation.CurrentValue;
      if(result==GetResult.Option&&index==options.GrainIndex) Grain=(GrainMode)getter.Option().CurrentListOptionIndex;
      if(result==GetResult.Option&&index==options.StackIndex) Stack=(TableStack)getter.Option().CurrentListOptionIndex;
      if(index==options.FlipIndex) AllowFlip=options.Flip.CurrentValue;
      if(index==options.SearchIndex) SearchTime=options.Search.CurrentValue;
      if(index==options.GapIndex) PartGap=options.Gap.CurrentValue;
      return true;
    }

    internal static void Load()=>ToolsOptionStore.Read<int>(Section,section=>
    {
      Width=ToolsOptionStore.TryGetDouble(section,"width",out var width)&&double.IsFinite(width)&&width>0?width:DefaultWidth;
      Length=ToolsOptionStore.TryGetDouble(section,"length",out var length)&&double.IsFinite(length)&&length>0?length:DefaultLength;
      PartGap=ToolsOptionStore.TryGetDouble(section,"gap",out var gap)&&double.IsFinite(gap)&&gap>=DefaultPartGap?gap:DefaultPartGap;
      RotationChoice=ToolsOptionStore.TryGetString(section,"rotationMode",out var rotation)&&Enum.TryParse<RotationMode>(rotation,true,out var choice)&&Enum.IsDefined(choice)?choice:DefaultRotationMode;
      RotationStep=ToolsOptionStore.TryGetDouble(section,"rotationStep",out var step)&&double.IsFinite(step)&&step>=MinimumRotationStep&&step<=MaximumRotationStep?step:DefaultRotationStep;
      AllowReverse=ToolsOptionStore.TryGetBool(section,"allowReverse",out var reverse)?reverse:DefaultAllowReverse;
      ObeyGrain=ToolsOptionStore.TryGetBool(section,"obeyGrain",out var obey)?obey:DefaultObeyGrain;
      GrainDeviation=ToolsOptionStore.TryGetDouble(section,"grainDeviation",out var deviation)&&double.IsFinite(deviation)&&deviation>=DefaultGrainDeviation&&deviation<=MaximumGrainDeviation?deviation:DefaultGrainDeviation;
      Grain=ToolsOptionStore.TryGetString(section,"grain",out var grain)&&Enum.TryParse<GrainMode>(grain,true,out var parsed)&&Enum.IsDefined(parsed)?parsed:DefaultGrain;
      Stack=ToolsOptionStore.TryGetString(section,"stack",out var stack)&&Enum.TryParse<TableStack>(stack,true,out var layout)&&Enum.IsDefined(layout)?layout:DefaultStack;
      AllowFlip=ToolsOptionStore.TryGetBool(section,"flip",out var flip)?flip:DefaultAllowFlip;
      SearchTime=ToolsOptionStore.TryGetDouble(section,"searchTime",out var search)&&double.IsFinite(search)&&search>=0&&search<=MaximumSearchTime?search:DefaultSearchTime;
      return 0;
    });
    internal static void Save()=>ToolsOptionStore.Update(Section,section=>
    { section["width"]=Width; section["length"]=Length; section["gap"]=PartGap; section["grain"]=Grain.ToString(); section["rotationMode"]=RotationChoice.ToString(); section["rotationStep"]=RotationStep; section["allowReverse"]=AllowReverse; section["obeyGrain"]=ObeyGrain; section["grainDeviation"]=GrainDeviation; section["stack"]=Stack.ToString(); section["flip"]=AllowFlip; section["searchTime"]=SearchTime; });

    internal static Row CreateRow(RhinoDoc doc,Part part)
    {
      Vector3d? guide=null;
      Guid guideId=Guid.Empty;
      foreach(var item in part.Items.Where(item=>item.Geometry is Curve curve&&!curve.IsClosed&&IsGrainMarker(doc,item.Attributes))
        .OrderByDescending(item=>((Curve)item.Geometry).GetLength()))
      {
        var curve=(Curve)item.Geometry;
        Vector3d direction;
        if(curve.IsLinear(doc.ModelAbsoluteTolerance)) direction=curve.PointAtEnd-curve.PointAtStart;
        else if(curve.TryGetPolyline(out var polyline)&&polyline.SegmentCount>0)
        {
          var longest=Enumerable.Range(0,polyline.SegmentCount).Select(polyline.SegmentAt).OrderByDescending(segment=>segment.Length).First();
          direction=longest.Direction;
        }
        else direction=curve.TangentAt(curve.NormalizedLengthParameter(GrainCurveSample,out var parameter)?parameter:curve.Domain.Mid);
        direction-=part.Plane.Normal*(direction*part.Plane.Normal);
        if(direction.Unitize()) { guide=direction; guideId=item.Id; break; }
      }
      var row=new Row(part,++_nextId,guide);
      var labels=part.Items.Where(item=>item.Geometry is TextEntity text&&!string.IsNullOrWhiteSpace(text.PlainText))
        .OrderByDescending(item=>item.Attributes.LayerIndex>=0&&item.Attributes.LayerIndex<doc.Layers.Count&&
          string.Equals(doc.Layers[item.Attributes.LayerIndex].FullPath.Split("::").Last(),NameLayer,StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(item=> { var bounds=item.Geometry.GetBoundingBox(part.Plane); return (bounds.Max.X-bounds.Min.X)*(bounds.Max.Y-bounds.Min.Y); });
      if(labels.FirstOrDefault()?.Geometry is TextEntity label)
        row.Name=System.Text.RegularExpressions.Regex.Replace(label.PlainText,@"\s+"," ").Trim();
      Log.Write("vNest","part id={0} name={1} grain_object={2} grain_angle={3}",row.Id,row.Name,guideId,
        guide.HasValue?RhinoMath.ToDegrees(Math.Atan2(guide.Value*part.Plane.YAxis,guide.Value*part.Plane.XAxis)).ToString("G17",System.Globalization.CultureInfo.InvariantCulture):"missing");
      return row;
    }

    private static bool IsGrainMarker(RhinoDoc doc,ObjectAttributes attributes)
    {
      var layer=attributes.LayerIndex>=0&&attributes.LayerIndex<doc.Layers.Count?doc.Layers[attributes.LayerIndex].FullPath.Split("::").Last().Trim():string.Empty;
      return string.Equals(attributes.Name?.Trim(),GrainIdentifier,StringComparison.OrdinalIgnoreCase)||string.Equals(layer,GrainIdentifier,StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PickGrain(RhinoDoc doc,Row row)
    {
      var selected=doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id).ToArray();
      try
      {
        using var getter=new GetObject();
        getter.SetCommandPrompt($"Pick a part curve for grain, or Draw a guide for {row.Name}"); getter.GeometryFilter=ObjectType.Curve;
        getter.GroupSelect=false; getter.SubObjectSelect=true;
        getter.EnablePreSelect(false,true); getter.AlreadySelectedObjectSelect=true;
        getter.EnableUnselectObjectsOnExit(false); getter.DeselectAllBeforePostSelect=false;
        var members=row.Part.Items.Select(item=>item.Id).ToHashSet();
        getter.SetCustomGeometryFilter((obj,geometry,_)=>members.Contains(obj.Id)&&geometry is Curve);
        int draw=getter.AddOption("Draw");
        Vector3d direction;
        var result=getter.Get();
        if(result==GetResult.Option&&getter.OptionIndex()==draw)
        {
          using var start=new GetPoint(); start.SetCommandPrompt("Grain guide start"); start.PermitObjectSnap(GrainObjectSnaps);
          if(start.Get()!=GetResult.Point) return false;
          using var end=new GetPoint(); end.SetCommandPrompt("Grain guide end"); end.PermitObjectSnap(GrainObjectSnaps);
          end.SetBasePoint(start.Point(),true); end.DrawLineFromPoint(start.Point(),true);
          if(end.Get()!=GetResult.Point) return false;
          direction=end.Point()-start.Point();
        }
        else
        {
          if(result!=GetResult.Object) return false;
          using var reference=getter.Object(0);
          if(reference.Curve() is not {} curve) return false;
          // Curved guides use the tangent at the clicked location, not their arbitrary endpoint chord.
          if(!curve.ClosestPoint(reference.SelectionPoint(),out double parameter)) return false;
          direction=curve.TangentAt(parameter);
        }
        direction-=row.Part.Plane.Normal*(direction*row.Part.Plane.Normal);
        if(!direction.Unitize()) return false;
        row.Guide=direction; row.ManualRotation=false;
        if((row.GrainOverride??Grain)==GrainMode.Off) row.GrainOverride=GrainMode.Auto;
        return true;
      }
      finally
      {
        doc.Objects.UnselectAll(); foreach(var id in selected) doc.Objects.FindId(id)?.Select(true);
        doc.Views.Redraw();
      }
    }

    internal static double NormalizeRotation(double degrees)
    { degrees%=360; if(degrees>180) degrees-=360; if(degrees<=-180) degrees+=360; return degrees; }

    internal static double? EffectiveGuide(GrainMode mode,double? guide)=>mode==GrainMode.Off?null:guide??(mode==GrainMode.On?SourceGrainAngle:null);

    internal static bool GrainConstrained(GrainMode mode,double? guide,bool obey)=>obey&&EffectiveGuide(mode,guide).HasValue;

    internal static double? GuideAngle(Row row)=>row.Guide.HasValue?
      RhinoMath.ToDegrees(Math.Atan2(row.Guide.Value*row.Part.Plane.YAxis,row.Guide.Value*row.Part.Plane.XAxis)):null;

    internal static double[] RotationOffsets(RotationMode choice,double customStep,bool reverse)
    {
      double step=choice switch {RotationMode.Step45=>45,RotationMode.Step90=>90,RotationMode.Step180=>180,RotationMode.Custom=>customStep,_=>FreeAngleStep};
      if(choice==RotationMode.None) return [DefaultRotation];
      if(!double.IsFinite(step)||step<MinimumRotationStep||step>MaximumRotationStep) return [];
      return Enumerable.Range(0,(int)Math.Ceiling(MaximumRotationStep/step)).Select(index=>NormalizeRotation(index*step)).Distinct().ToArray();
    }

    internal static bool RotationAllowed(double rotation,GrainMode mode,double? guideDegrees,RotationMode choice,double step,bool reverse)
      =>PartRotationAllowed(rotation,mode,guideDegrees,choice,step,reverse,ObeyGrain,GrainDeviation);

    internal static bool PartRotationAllowed(double rotation,GrainMode mode,double? guide,RotationMode choice,double step,bool reverse,bool obey,double deviation)
    {
      if(GrainConstrained(mode,guide,obey))
      {
        double offset=NormalizeRotation(rotation-(MaterialBasisAngle-EffectiveGuide(mode,guide)!.Value));
        return Math.Abs(offset)<=deviation+GrainAngularTolerance||reverse&&Math.Abs(NormalizeRotation(offset-180))<=deviation+GrainAngularTolerance;
      }
      if(choice==RotationMode.Any) return true;
      return RotationOffsets(choice,step,true).Any(candidate=>Math.Abs(NormalizeRotation(rotation-candidate))<=GrainAngularTolerance);
    }

    internal static double[] CandidateRotations(GrainMode mode,double? guide,RotationMode choice,double step,bool reverse)
      =>PartCandidateRotations(mode,guide,choice,step,reverse,ObeyGrain,GrainDeviation);

    internal static double[] PartCandidateRotations(GrainMode mode,double? guide,RotationMode choice,double step,bool reverse,bool obey,double deviation)
    {
      if(!GrainConstrained(mode,guide,obey)) return RotationOffsets(choice,step,true);
      double reference=NormalizeRotation(MaterialBasisAngle-EffectiveGuide(mode,guide)!.Value);
      var offsets=reverse?new[]{0.0,180.0}:new[]{0.0};
      var deviations=new List<double> {DefaultGrainDeviation};
      if(deviation>0)
      {
        deviations.Add(-deviation); deviations.Add(deviation);
        for(double angle=FreeAngleStep;angle<deviation;angle+=FreeAngleStep) { deviations.Add(-angle); deviations.Add(angle); }
      }
      return offsets.SelectMany(offset=>deviations.Select(delta=>NormalizeRotation(reference+offset+delta)))
        .Distinct().ToArray();
    }

    internal static BoundingBox Bounds(Row row,double rotation)
    {
      var frame=PlanarPartSplitter.RotatedFrame(row.Part.Plane,RhinoMath.ToRadians(MaterialBasisAngle-rotation));
      return row.Part.Outline.GetBoundingBox(frame);
    }

    internal static List<NestPackingEngine.Pose> Poses(Row row)
    {
      double? guide=GuideAngle(row);
      return row.CachedPoses(guide,()=>FindPoses(row,guide));
    }

    private static List<NestPackingEngine.Pose> FindPoses(Row row,double? guide)
    {
      var mode=row.GrainOverride??Grain;
      var choice=row.RotationOverride??RotationChoice; double step=row.RotationStepOverride??RotationStep;
      bool obey=row.ObeyGrainOverride??ObeyGrain,reverse=row.AllowReverseOverride??AllowReverse;
      double deviation=row.GrainDeviationOverride??GrainDeviation;
      bool constrained=GrainConstrained(mode,guide,obey),manual=row.ManualRotation||row.Fixed;
      bool? flip=row.Fixed?row.Flipped:row.FlipOverride;
      bool offerBase=flip!=true,offerFlip=flip==true||flip==null&&AllowFlip;
      // Mirroring across material width reverses a grain vector; validate its final direction too.
      double MirrorRotation(double angle)=>NormalizeRotation(-angle-2*EffectiveGuide(mode,guide)!.Value);
      bool Allowed(double angle,bool mirrored)=>!constrained&&manual||PartRotationAllowed(
        mirrored&&constrained?MirrorRotation(angle):angle,mode,guide,choice,step,reverse,obey,deviation);
      IEnumerable<double> angles=manual?[row.Rotation]:PartCandidateRotations(mode,guide,choice,step,reverse,obey,deviation);
      if(!manual&&constrained&&offerFlip) angles=angles.Concat(angles.Select(MirrorRotation)).ToArray();
      var candidates=new Dictionary<double,NestPackingEngine.Pose>();
      NestPackingEngine.Pose? Evaluate(double angle)
      {
        angle=NormalizeRotation(angle);
        if(candidates.TryGetValue(angle,out var cached)) return cached;
        if(!(offerBase&&Allowed(angle,false)||offerFlip&&Allowed(angle,true))) return null;
        var bounds=Bounds(row,angle); if(!bounds.IsValid) return null;
        double width=bounds.Max.X-bounds.Min.X,length=bounds.Max.Y-bounds.Min.Y;
        if(width<=0||length<=0) return null;
        var frame=PlanarPartSplitter.RotatedFrame(row.Part.Plane,RhinoMath.ToRadians(MaterialBasisAngle-angle));
        var contour=row.OutlinePoints().Select(point=>new NestPackingEngine.ContourPoint(
          (point-frame.Origin)*frame.XAxis-bounds.Min.X,(point-frame.Origin)*frame.YAxis-bounds.Min.Y)).ToArray();
        if(contour.Length<3) return null;
        var pose=new NestPackingEngine.Pose(width,length,angle) {Contour=contour}; candidates.Add(angle,pose); return pose;
      }
      foreach(var angle in angles.Select(NormalizeRotation).Distinct().OrderBy(Math.Abs)) Evaluate(angle);
      double FitScore(NestPackingEngine.Pose pose)=>Math.Max(pose.Width/Width,pose.Length/Length);
      bool free=!manual&&!constrained&&choice==RotationMode.Any;
      bool refine=free||!manual&&constrained&&deviation>0;
      if(refine)
      {
        var segments=row.Part.Outline.DuplicateSegments();
        try
        {
          foreach(var segment in segments.Where(segment=>segment.IsLinear(LinearEdgeTolerance)))
          {
            var direction=segment.PointAtEnd-segment.PointAtStart;
            double angle=RhinoMath.ToDegrees(Math.Atan2(direction*row.Part.Plane.YAxis,direction*row.Part.Plane.XAxis));
            foreach(var offset in new[]{0.0,90.0,180.0,270.0}) Evaluate(offset-angle);
          }
        }
        finally { foreach(var segment in segments) segment.Dispose(); }
        foreach(var seed in candidates.Values.OrderBy(FitScore).ThenBy(pose=>pose.Width*pose.Length).Take(RotationRefinementSeeds).ToArray())
        {
          var best=seed; double refinementStep=FreeAngleStep/2;
          for(int pass=0;pass<RotationRefinementPasses;pass++,refinementStep/=2)
          {
            var lower=Evaluate(best.Rotation-refinementStep); var upper=Evaluate(best.Rotation+refinementStep);
            foreach(var candidate in new[]{lower,upper})
              if(candidate!=null&&(FitScore(candidate)<FitScore(best)||FitScore(candidate)==FitScore(best)&&candidate.Width*candidate.Length<best.Width*best.Length)) best=candidate;
          }
        }
      }
      var poses=new List<NestPackingEngine.Pose>();
      foreach(var candidate in candidates.Values.OrderByDescending(pose=>pose.Width<=Width&&pose.Length<=Length).ThenBy(pose=>pose.Width*pose.Length).ThenBy(pose=>Math.Abs(pose.Rotation)))
      {
        if(poses.Any(pose=>Math.Abs(NormalizeRotation(pose.Rotation-candidate.Rotation))<(free?PoseAngleSeparation:GrainAngularTolerance))) continue;
        poses.Add(candidate);
      }
      if(free)
      {
        var paired=new List<NestPackingEngine.Pose>();
        foreach(var pose in poses)
        {
          if(paired.Any(existing=>Math.Abs(NormalizeRotation(existing.Rotation-pose.Rotation))<PoseAngleSeparation)) continue;
          var opposite=Evaluate(pose.Rotation+180);
          if(opposite==null) continue;
          paired.Add(pose); paired.Add(opposite);
          if(paired.Count>=FreePoseLimit) break;
        }
        poses=paired;
      }
      return poses.SelectMany(pose=>
        (offerBase&&Allowed(pose.Rotation,false)?new[]{pose}:Array.Empty<NestPackingEngine.Pose>())
        .Concat(offerFlip&&Allowed(pose.Rotation,true)?new[]{FlipPose(pose)}:Array.Empty<NestPackingEngine.Pose>())).ToList();
    }

    internal static NestPackingEngine.Pose FlipPose(NestPackingEngine.Pose pose)=>pose with
    { Flipped=true,Contour=pose.Contour?.Select(point=>new NestPackingEngine.ContourPoint(pose.Width-point.X,point.Y)).ToArray() };

    internal static Transform Placement(Row row,Plane plane,Point3d anchor)
      =>PlacementAt(row,plane,anchor,row.Table,row.X,row.Y,row.Rotation,row.Flipped);

    internal static Transform PlacementAt(Row row,Plane plane,Point3d anchor,int table,double x,double y,double rotation,bool flipped)
    {
      var frame=PlanarPartSplitter.RotatedFrame(row.Part.Plane,RhinoMath.ToRadians(MaterialBasisAngle-rotation));
      var bounds=Bounds(row,rotation);
      var target=TablePlane(plane,anchor,table);
      var transform=Transform.Translation(target.XAxis*(x-bounds.Min.X)+target.YAxis*(y-bounds.Min.Y))*Transform.PlaneToPlane(frame,target);
      return flipped?Transform.Mirror(target.PointAt(x+(bounds.Max.X-bounds.Min.X)/2,0),target.XAxis)*transform:transform;
    }

    internal static Plane TablePlane(Plane plane,Point3d anchor,int table)=>
      new(anchor+TableOffset(plane,table)+plane.YAxis*Width,-plane.YAxis,plane.XAxis);

    internal static Vector3d TableOffset(Plane plane,int table)=>Stack==TableStack.Vertical
      ? -plane.YAxis*(table*(Width+TableGap)) : plane.XAxis*(table*(Length+TableGap));

    internal static int PreviewTableCount(int tables,bool locating)=>tables==0&&locating?1:tables;

  }
}

namespace vTools.Commands
{
  using Eto.Drawing;
  using Eto.Forms;
  using Rhino;
  using Rhino.Commands;
  using Rhino.Display;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using Rhino.Input;
  using SDColor = System.Drawing.Color;


  internal static partial class NestWorkflow
  {
    // UI defaults and customizable limits
    private const int WindowWidth = 840; // Initial client width in device-independent pixels; fits three uniform label/editor pairs.
    private const int WindowHeight = 620; // Initial client height in device-independent pixels; includes per-part grain constraints.
    private const int MinimumWindowWidth = 840; // Minimum client width in device-independent pixels; keeps all three editor columns readable.
    private const int MinimumWindowHeight = 400; // Minimum client height in device-independent pixels.
    private const int WindowPadding = 8; // Uniform client padding in device-independent pixels.
    private const int ControlGap = 6; // Inter-control gap in device-independent pixels.
    private const int GridRowHeight = 24; // Stable part-row height in device-independent pixels.
    private const int FieldLabelWidth = 88; // Uniform right-aligned label width in device-independent pixels; fits Grain deviation.
    private const int FieldWidth = 165; // Uniform input width in device-independent pixels; fits numeric values and grain-reversal choices.
    private const int PartNameWidth = 160; // Initial part-name column width in device-independent pixels.
    private const int StatusColumnMinimumWidth = 210; // Minimum status width in device-independent pixels; leaves enough room for copy counts and state text.
    private const int IconButtonSize = 26; // Stable add/remove button width in device-independent pixels.
    private const int NumberDecimals = 3; // Decimal places displayed for model-unit coordinates and angles; nonnegative integer.
    private const string MixedValue = "<varies>"; // Non-actionable dropdown placeholder for unequal selected settings.
    private const double NumberIncrement = 0.1; // Model-unit/degree increment for numeric editor arrows; positive.
    private const double AutoNestDelay = 0.2; // Seconds to debounce automatic re-nesting after edits; positive.
    private const string WindowTitle = "Nest"; // Modeless manager window title.
    private const bool LocationObjectSnaps = true; // true enables native object snaps in the location picker; false uses unsnapped points.
    private const int UnpackedRevision = -1; // Sentinel for a selection/options revision that has no accepted packing result.
    private const float ProgressTintAlpha = 0.3f; // Theme highlight opacity from zero through one; keeps overlaid status text legible.
    private const string ElapsedShortFormat = @"m\:ss\.f"; // Completed durations below one hour: minutes, seconds and tenths; not a remaining-time estimate.
    private const string ElapsedHourTailFormat = @"mm\:ss\.f"; // Minutes/seconds/tenths appended to total elapsed hours for longer operations.
    private static readonly PreviewDisplay.ObjectHighlightStyle SkippedStyle = new(
      SDColor.OrangeRed,SDColor.Maroon,SDColor.Maroon,0.25); // RGB body/outline/dot colors and 0-1 transparency for skipped source parts.
    private static readonly PreviewDisplay.ObjectHighlightStyle MembershipStyle = new(
      SDColor.FromArgb(0,145,165),SDColor.FromArgb(0,55,72),SDColor.FromArgb(0,120,145),0.6); // Muted cyan membership feedback; active rows use the distinct SelectedPartStyle.
    private static readonly PreviewDisplay.ObjectHighlightStyle SelectedPartStyle = new(
      SDColor.Magenta,SDColor.FromArgb(85,0,65),SDColor.FromArgb(150,0,110),0.25); // Magenta body, dark outline and dot background distinguish active rows from cyan membership and yellow native selection.

    internal sealed partial class NestWindow : Form
    {
      private readonly RhinoDoc _doc;
      private readonly List<Row> _rows;
      private readonly GridView _grid=new() {AllowMultipleSelection=true,RowHeight=GridRowHeight};
      private readonly NumericStepper _width=Number(),_length=Number(),_rotationStep=Number();
      private readonly DropDown _rotationPreset=new() {DataStore=RotationLabels,Width=FieldWidth};
      private readonly DropDown _partRotation=new() {DataStore=new[]{"Default"}.Concat(RotationLabels).ToArray(),Width=FieldWidth};
      private readonly DropDown _allowReverse=new() {DataStore=ReversalLabels,Width=FieldWidth};
      private readonly CheckBox _obeyDefault=new() {Text="Obey grain",Checked=ObeyGrain};
      private readonly CheckBox _obey=new() {Text="Obey grain"},_defaultDeviation=new() {Text="Default"};
      private readonly NumericStepper _guideAngle=Number(),_partDeviation=Number();
      private readonly NumericStepper _searchTime=Number(),_grainDeviation=Number(),_partGap=Number();
      private readonly DropDown _grain=new() {DataStore=GrainNames};
      private readonly DropDown _stack=new() {DataStore=StackNames,Width=FieldWidth};
      private readonly CheckBox _auto=new() {Text="Auto",Checked=DefaultAutoNest};
      private readonly CheckBox _allowFlip=new() {Text="Allow Flip",Checked=AllowFlip};
      private readonly CheckBox _flipped=new() {Text="Flipped",Checked=DefaultAllowFlip};
      private readonly CheckBox _included=new() {Text="Included"},_fixed=new() {Text="Fixed"};
      private readonly NumericStepper _x=Number(),_y=Number(),_angle=Number(),_table=Number(),_limit=Number();
      private readonly NumericStepper _copies=Number();
      private readonly Dictionary<NumericStepper,NestMixedNumber> _mixedNumbers=[];
      private readonly Dictionary<NumericStepper,double> _displayedNumbers=[];
      private readonly List<FieldResetOverlay> _resets=[];
      private HashSet<int> _displayedSelection=[];
      private readonly DropDown _partGrain=new() {DataStore=new[]{"Default"}.Concat(GrainNames).ToArray()};
      private readonly PackingStatus _status=new();
      private readonly Button _place=new() {Text="Place",Enabled=false};
      private readonly Button _nest=new() {Text="Nest",ToolTip="Pack included parts; retain fixed positions, manual angles and grain settings"};
      private readonly UITimer _timer=new() {Interval=AutoNestDelay};
      private readonly Preview _preview;
      private readonly PreviewDisplay.ObjectHighlighter _highlight;
      private readonly PreviewDisplay.ObjectHighlighter _sources;
      private readonly PreviewDisplay.ObjectHighlighter _selectedSources;
      private readonly NestDocumentPicking _picking;
      private readonly PreviewDisplay.ObjectHighlighter _skipped;
      private readonly List<GeometryBase> _highlightCopies=[];
      private readonly Dictionary<int,(Transform Transform,List<PlanarPartSelection.Item> Items)> _previewCopies=[];
      private bool _updating,_closed,_anchored,_needsNest,_locationPicking,_shown,_packing,_stopping,_placing;
      private int _layoutRevision,_packedRevision=UnpackedRevision;
      private CancellationTokenSource? _packingCancellation;
      private HashSet<Guid> _selection=[];
      private NestPackingEngine.Result _result=new([],0,[]);
      private NestPackingEngine.Result? _workingResult;
      private NestPackingEngine.Result? _bestInProgress;
      private NestPackingEngine.Result VisibleResult=>_workingResult??_result;
      private readonly Plane _plane;
      private Point3d _anchor;
      private Row? _grainRequest;

      internal uint DocumentSerial=>_doc.RuntimeSerialNumber;
      private Row? Selected=>_grid.SelectedItem is GridItem item&&item.Tag is Row row?row:null;
      private Row[] SelectedParts=>_grid.SelectedRows.Where(index=>index>=0&&index<_rows.Count).Select(index=>_rows[index]).ToArray();
      private Control MixedEditor(NumericStepper field)
      {
        var mixed=new NestMixedNumber(field); _mixedNumbers[field]=mixed;
        mixed.Committed+=value=> { _displayedNumbers.Remove(field); field.Value=value; CommitMixedNumber(field,value); };
        return mixed;
      }
      private void NumberChanged(NumericStepper field,Action action)
      {
        if(_updating||_closed) return;
        if(_displayedNumbers.TryGetValue(field,out var shown)&&!FieldResetOverlay.NumberChanged(field.Value,shown,field.DecimalPlaces)) return;
        _displayedNumbers[field]=field.Value; action();
      }
      private void CommitMixedNumber(NumericStepper field,double value)
      {
        // Equal-to-first-row input still needs to replace the other selected values.
        if(field==_copies) Edit(row=>row.Copies=(int)value);
        else if(field==_angle) Edit(row=> { row.Rotation=value; row.ManualRotation=true; });
        else if(field==_limit) Edit(row=> { row.RotationStepOverride=value; row.RotationOverride=RotationMode.Custom; });
        else if(field==_partDeviation) Edit(row=>row.GrainDeviationOverride=value);
        else if(field==_guideAngle) Edit(row=>SetGuide(row,value));
      }
      private static void SetGuide(Row row,double degrees)
      {
        double angle=RhinoMath.ToRadians(degrees);
        row.Guide=row.Part.Plane.XAxis*Math.Cos(angle)+row.Part.Plane.YAxis*Math.Sin(angle);
        if((row.GrainOverride??Grain)==GrainMode.Off) row.GrainOverride=GrainMode.Auto;
      }
      private static NumericStepper Number()=>new() {Width=FieldWidth,DecimalPlaces=NumberDecimals,Increment=NumberIncrement,MinValue=0,MaxValue=double.MaxValue};
      private static Label Caption(string text)=>new() {Text=text,Width=FieldLabelWidth,TextAlignment=TextAlignment.Right};
      private static TableRow FieldRow(params (string Label,Control Input)[] fields)
      {
        var row=new TableRow();
        foreach(var field in fields)
        {
          field.Input.Width=FieldWidth;
          row.Cells.Add(new TableCell(Caption(field.Label)));
          row.Cells.Add(new TableCell(field.Input));
        }
        while(row.Cells.Count<6) row.Cells.Add(new TableCell(null));
        row.Cells.Add(new TableCell(null,true));
        return row;
      }

      internal NestWindow(RhinoDoc doc,List<Row> rows)
      {
        _doc=doc; _rows=rows;
        Title=WindowTitle; ClientSize=new Size(WindowWidth,WindowHeight); MinimumSize=new Size(MinimumWindowWidth,MinimumWindowHeight);
        Padding=new Padding(WindowPadding);
        _plane=doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??Plane.WorldXY;
        var bounds=BoundingBox.Empty; foreach(var row in rows) bounds.Union(row.Part.Outline.GetBoundingBox(_plane));
        _anchored=bounds.IsValid;
        _anchor=_anchored?_plane.PointAt(bounds.Max.X+TableGap+InitialLabelExtent(),bounds.Min.Y):_plane.Origin;
        _preview=new Preview(this) {Enabled=true}; _highlight=new PreviewDisplay.ObjectHighlighter(doc,SelectedPartStyle);
        _sources=new PreviewDisplay.ObjectHighlighter(doc,MembershipStyle);
        _selectedSources=new PreviewDisplay.ObjectHighlighter(doc,SelectedPartStyle);
        _skipped=new PreviewDisplay.ObjectHighlighter(doc,SkippedStyle);
        _picking=new NestDocumentPicking(doc,()=>!_closed&&!_placing&&!_locationPicking&&!PackingBlocked(),SyncSelection,
          id=>_rows.FirstOrDefault(row=>row.Part.Items.Any(item=>item.Id==id))?.Part.Items.Select(item=>item.Id).ToArray(),KnownInteriorMembers);
        _picking.RetainedSelection=()=>_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id);
        _picking.PartClicked+=FocusClickedPart;
        _width.Value=NestWorkflow.Width; _length.Value=Length; _rotationStep.Value=RotationStep; _rotationStep.MinValue=MinimumRotationStep; _rotationStep.MaxValue=MaximumRotationStep;
        _rotationPreset.SelectedIndex=(int)RotationChoice; _rotationStep.Enabled=RotationChoice==RotationMode.Custom;
        _searchTime.Value=SearchTime; _searchTime.MaxValue=MaximumSearchTime; _searchTime.DecimalPlaces=0; _searchTime.Increment=1;
        _grainDeviation.MaxValue=MaximumGrainDeviation; _grainDeviation.Value=GrainDeviation;
        _partGap.Value=PartGap;
        _partDeviation.MaxValue=MaximumGrainDeviation; _guideAngle.MinValue=-180; _guideAngle.MaxValue=180;
        _allowReverse.SelectedIndex=AllowReverse?1:0;
        _grain.SelectedIndex=(int)Grain; _angle.MinValue=-180; _angle.MaxValue=180;
        _stack.SelectedIndex=(int)Stack;
        _limit.MinValue=MinimumRotationStep; _limit.MaxValue=MaximumRotationStep; _table.MinValue=1; _table.MaxValue=NestPackingEngine.MaximumTables; _table.DecimalPlaces=0; _table.Increment=1;
        _grid.Columns.Add(new GridColumn {HeaderText="",Width=32,DataCell=new CheckBoxCell(0),Editable=true});
        _grid.Columns.Add(new GridColumn {HeaderText="Part",Width=PartNameWidth,DataCell=new TextBoxCell(1)});
        _grid.Columns.Add(new GridColumn {HeaderText="Table",Width=65,DataCell=new TextBoxCell(2)});
        _grid.Columns.Add(new GridColumn {HeaderText="Rotation",Width=85,DataCell=new TextBoxCell(3)});
        _grid.Columns.Add(new GridColumn {HeaderText="Fixed",Width=55,DataCell=new CheckBoxCell(4)});
        _grid.Columns.Add(new GridColumn {HeaderText="Copies",Width=60,DataCell=new TextBoxCell(6)});
        _grid.Columns.Add(new GridColumn {HeaderText="Status",Width=StatusColumnMinimumWidth,DataCell=new TextBoxCell(5),Expand=true});
        _grid.Load+=(_,_)=>
        {
          if(_grid.Columns.Last().ControlObject is System.Windows.Controls.DataGridColumn statusColumn)
          { statusColumn.MinWidth=StatusColumnMinimumWidth; statusColumn.Width=new System.Windows.Controls.DataGridLength(1,System.Windows.Controls.DataGridLengthUnitType.Star); }
        };
        _copies.DecimalPlaces=0; _copies.Increment=1; _copies.MinValue=DefaultCopies; _copies.MaxValue=MaximumCopies;
        _copies.ToolTip="Number of separately grouped copies to nest. Fixed position applies to the first copy; extra copies pack automatically.";
        foreach(var check in new[]{_included,_fixed,_flipped,_obey,_defaultDeviation}) check.ThreeState=true;
        var add=new Button {Text="+",Width=IconButtonSize,ToolTip="Add selected parts from the drawing"};
        var remove=new Button {Text="-",Width=IconButtonSize,ToolTip="Remove highlighted parts from this nest, keeping originals"};
        var clear=new Button {Text="Clear",ToolTip="Clear the part list and nest preview without deleting original objects"};
        var grainPick=new Button {Text="Pick Grain",ToolTip="Pick a curve of the active part or Draw a guide; apply its direction to all selected rows"};
        var resetRotation=new Button {Text="Auto Rotation",ToolTip="Allow the nesting engine to choose this part's rotation again"};
        var location=new Button {Text="Location",ToolTip="Pick a location for the preview tables"};
        var close=new Button {Text="Close",ToolTip="Close the preview without adding parts"};
        var reset=new Button {Text="Reset",ToolTip="Reset selected parts to their detected grain, global option defaults, one copy and automatic placement"};
        _place.ToolTip="Create separately grouped part copies and material table outlines at the preview location";
        _width.ToolTip="Material width in document units"; _length.ToolTip="Material length of each table in document units";
        _partGap.ToolTip="Minimum distance between part outlines in document units; zero permits touching. Does not add a table-edge margin.";
        _grain.ToolTip="Auto uses detected or picked grain; Force asks for missing guides when obeying grain; On uses source up/down if no guide exists; Off ignores grain.";
        _rotationPreset.ToolTip="Rotation choices for parts not obeying grain: preserve source angle, any angle, or source-relative increments. Grain-constrained parts use Grain turn instead.";
        _rotationStep.ToolTip="Custom rotation increment in degrees; 0.1 through 360";
        _allowReverse.ToolTip="No reversal aligns grain in its forward direction; Allow 180 reversal also permits the opposite direction. Both obey the deviation allowance.";
        _obeyDefault.ToolTip="Default for parts: constrain specified grain to the material's up/down direction, using deviation and reversal rather than free rotation presets";
        _obey.ToolTip="Obey this part's specified grain; off enables the full source-relative Rotation presets";
        _grainDeviation.ToolTip="Global allowed angle either side of permitted grain directions (0-90 degrees); zero requires exact alignment";
        _guideAngle.ToolTip="Grain direction in degrees from this part's source-plane X axis; editing specifies a guide without drawing geometry";
        _partDeviation.ToolTip="This part's grain-deviation allowance in degrees (0-90); Default uses the global allowance";
        _defaultDeviation.ToolTip="Use the global grain deviation; uncheck to set a separate allowance for this part";
        _stack.ToolTip="Arrange horizontal tables side by side or stack them vertically without changing part rotations";
        _x.ToolTip="Position across material width, in document units";
        _y.ToolTip="Position along material length, in document units";
        _angle.ToolTip="Source-relative rotation in degrees; editing fixes the rotation for re-nesting";
        _table.ToolTip="One-based material table; manual placement sets Fixed";
        _fixed.ToolTip="Keep this part at its entered table and coordinates during re-nesting";
        _limit.ToolTip="Custom per-part rotation increment in degrees; 0.1 through 360"; _partGrain.ToolTip="Override the global grain reference for this part";
        _partRotation.ToolTip="For grain-constrained parts choose No reversal or Allow 180 reversal; otherwise choose a free-rotation preset. Default uses global settings.";
        _included.ToolTip="Include this part in the current nest"; _auto.ToolTip="Automatically re-nest after changes";
        _searchTime.ToolTip="Seconds to search alternative full layouts after the initial layout; zero keeps only the first pass";
        _allowFlip.ToolTip="Allow the packer to mirror parts across material grain; off preserves their handedness";
        _flipped.ToolTip="Set this part's mirrored orientation explicitly; Auto Rotation restores automatic choices";
        var settings=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          FieldRow(("Width",_width),("Length",_length),("Grain",_grain)),
          FieldRow(("Search (s)",_searchTime),("Grain deviation",_grainDeviation),("",_obeyDefault)),
          FieldRow(("Grain turn",_allowReverse),("Free rotation",_rotationPreset),("Step",_rotationStep)),
          FieldRow(("Gap",_partGap))}};
        var toolbar=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(Caption("Stack"),_stack,_auto,_allowFlip,new TableCell(null,true),add,remove,clear)}};
        var editor=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          FieldRow(("Table",_table),("Angle",MixedEditor(_angle)),("Grain",_partGrain)),
          FieldRow(("X",_x),("Y",_y),("Rotation",_partRotation)),
          FieldRow(("Grain angle",MixedEditor(_guideAngle)),("Deviation",MixedEditor(_partDeviation)),("",_defaultDeviation)),
          FieldRow(("Copies",MixedEditor(_copies)),("Step",MixedEditor(_limit)),("",_obey))}};
        var partActions=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(_included,_fixed,_flipped,grainPick,resetRotation,reset,new TableCell(null,true))}};
        var footer=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(new TableCell(_status,true),_nest,location,_place,close)}};
        var editorCaption=new Label {Text="Selected part"};
        var lists=CreateListsView(editor,partActions,editorCaption);
        Content=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(new TableCell(settings,true)),
          new TableRow(new TableCell(toolbar,true)),
          new TableRow(new TableCell(lists,true)) {ScaleHeight=true},
          new TableRow(editorCaption),
          new TableRow(new TableCell(editor,true)),
          new TableRow(new TableCell(partActions,true)),
          new TableRow(new TableCell(footer,true))}};
        _timer.Elapsed+=(_,_)=>
        {
          if(_closed||_locationPicking||Busy()) return;
          SyncSelection();
          if(_needsNest&&!_packing&&!PackingBlocked()) ReNest();
        };
        foreach(var field in new[]{_width,_length,_rotationStep,_searchTime,_grainDeviation,_partGap})
        { _displayedNumbers[field]=field.Value; field.ValueChanged+=(_,_)=>NumberChanged(field,GlobalChanged); }
        _rotationPreset.SelectedIndexChanged+=(_,_)=>GlobalChanged(); _allowReverse.SelectedIndexChanged+=(_,_)=>GlobalChanged();
        _obeyDefault.CheckedChanged+=(_,_)=>GlobalChanged();
        _grain.SelectedIndexChanged+=(_,_)=>GlobalChanged();
        _allowFlip.CheckedChanged+=(_,_)=>GlobalChanged();
        _stack.SelectedIndexChanged+=(_,_)=>
        {
          if(_updating||_closed||_stack.SelectedIndex<0) return;
          Stack=(TableStack)_stack.SelectedIndex; Save(); RefreshHighlights(); _doc.Views.Redraw();
        };
        _auto.CheckedChanged+=(_,_)=> { if(_auto.Checked==true) Queue(); };
        _grid.SelectionChanged+=(_,_)=> { if(_updating||_displayedSelection.SetEquals(SelectedParts.Select(row=>row.Id))) return; UpdateEditor(); RefreshHighlights(); _doc.Views.Redraw(); };
        _grid.KeyDown+=(_,e)=> { if(e.Key==Keys.Delete) { RemoveSelected(); e.Handled=true; } };
        _grid.CellEdited+=(_,e)=> { if(!_updating&&e.Item is GridItem item&&item.Tag is Row row&&row.Included!=Convert.ToBoolean(item.Values[0])) { row.Included=Convert.ToBoolean(item.Values[0]); Queue(); RefreshGrid(); RefreshHighlights(); _doc.Views.Redraw(); } };
        _included.CheckedChanged+=(_,_)=>CheckChanged(_included,()=>Edit(row=>row.Included=_included.Checked!.Value));
        _fixed.CheckedChanged+=(_,_)=>CheckChanged(_fixed,()=>Edit(row=>row.Fixed=_fixed.Checked!.Value));
        _flipped.CheckedChanged+=(_,_)=>CheckChanged(_flipped,()=>Edit(row=> { row.Flipped=_flipped.Checked!.Value; row.FlipOverride=row.Flipped; }));
        _angle.ValueChanged+=(_,_)=>NumberChanged(_angle,()=>Edit(row=> { row.Rotation=_angle.Value; row.ManualRotation=true; }));
        _copies.ValueChanged+=(_,_)=>NumberChanged(_copies,()=>Edit(row=>row.Copies=(int)_copies.Value));
        _partRotation.SelectedIndexChanged+=(_,_)=>ChoiceChanged(_partRotation,()=>Edit(row=>
        {
          bool constrained=SelectedParts.Any(UsesGrain);
          if(UsesGrain(row)) row.AllowReverseOverride=_partRotation.SelectedIndex<=0?null:_partRotation.SelectedIndex==2;
          else row.RotationOverride=_partRotation.SelectedIndex<=0?null:constrained?(_partRotation.SelectedIndex==2?RotationMode.Step180:RotationMode.None):(RotationMode)(_partRotation.SelectedIndex-1);
        }));
        _obey.CheckedChanged+=(_,_)=>CheckChanged(_obey,()=>Edit(row=>row.ObeyGrainOverride=_obey.Checked!.Value));
        _guideAngle.ValueChanged+=(_,_)=>NumberChanged(_guideAngle,()=>Edit(row=>SetGuide(row,_guideAngle.Value)));
        _partDeviation.ValueChanged+=(_,_)=>NumberChanged(_partDeviation,()=>Edit(row=>row.GrainDeviationOverride=_partDeviation.Value));
        _defaultDeviation.CheckedChanged+=(_,_)=>CheckChanged(_defaultDeviation,()=>Edit(row=>row.GrainDeviationOverride=_defaultDeviation.Checked!.Value?null:_partDeviation.Value));
        _limit.ValueChanged+=(_,_)=>NumberChanged(_limit,()=>Edit(row=> { row.RotationStepOverride=_limit.Value; row.RotationOverride=RotationMode.Custom; }));
        _partGrain.SelectedIndexChanged+=(_,_)=>ChoiceChanged(_partGrain,()=>Edit(row=>row.GrainOverride=_partGrain.SelectedIndex<=0?null:(GrainMode)(_partGrain.SelectedIndex-1)));
        _x.ValueChanged+=(_,_)=>NumberChanged(_x,()=> { if(SelectedParts.Length==1) Edit(row=> { row.X=_x.Value; row.Fixed=true; }); });
        _y.ValueChanged+=(_,_)=>NumberChanged(_y,()=> { if(SelectedParts.Length==1) Edit(row=> { row.Y=_y.Value; row.Fixed=true; }); });
        _table.ValueChanged+=(_,_)=>NumberChanged(_table,()=> { if(SelectedParts.Length==1) Edit(row=> { row.Table=(int)_table.Value-1; row.Fixed=true; }); });
        add.Click+=(_,_)=>AddSelected(); remove.Click+=(_,_)=>RemoveSelected(); _nest.Click+=(_,_)=> { if(_packing) StopPacking(); else ReNest(); };
        grainPick.Click+=(_,_)=> { if(Selected is {} row) RequestGrain(row); };
        resetRotation.Click+=(_,_)=>Edit(row=> { row.ManualRotation=false; row.FlipOverride=null; });
        reset.Click+=(_,_)=>Edit(ResetPart);
        InstallResets();
        location.Click+=(_,_)=>PickLocation(); _place.Click+=(_,_)=>Place(); close.Click+=(_,_)=>Close();
        clear.Click+=(_,_)=>ClearNest();
        Closed+=(_,_)=>DisposeState();
        RhinoDoc.CloseDocument+=DocumentClosed;
        RhinoApp.Idle+=PackingIdle;
        Shown+=(_,_)=> { if(_shown) return; _shown=true; RefreshGrid(); RefreshHighlights(); _timer.Start(); };
      }

      internal void AddPreselectedParts(IReadOnlyCollection<Guid> preselection)
      {
        if(_closed||_placing||preselection.Count==0) return;
        var ids=_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id).Concat(preselection)
          .Concat(_doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id))
          .Distinct().Where(id=>_doc.Objects.FindId(id) is {IsDeleted:false}).ToArray();
        foreach(var id in ids)
          if(_doc.Objects.FindId(id) is {} obj&&obj.IsSelected(false)==0) obj.Select(true);
        if(_selection.SetEquals(ids)) return;
        try
        {
          ReplaceSelection(ids); Queue();
          Log.Write("vNest","preselection added objects={0} parts={1}",preselection.Count,_rows.Count);
        }
        catch(Exception ex)
        { RestoreMemberSelection(); _status.Text="Selection has incomplete parts"; Log.Write("vNest","preselection skipped: {0}",ex); }
      }

      internal void SyncSelection()
      {
        if(_closed||_placing||_picking.DefersSelectionSync) return;
        var ids=_doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id).ToHashSet();
        if(_selection.SetEquals(ids)) return;
        try { ReplaceSelection(ids.ToArray()); Queue(); }
        catch(Exception ex) { RestoreMemberSelection(); _status.Text="Selection has incomplete parts"; Log.Write("vNest","selection update skipped: {0}",ex); }
      }
      private void RestoreMemberSelection()
      {
        _selection=_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id).Where(id=>_doc.Objects.FindId(id) is {IsDeleted:false}).ToHashSet();
        _doc.Objects.UnselectAll();
        foreach(var id in _selection) _doc.Objects.FindId(id)?.Select(true);
        _doc.Views.Redraw();
      }
      internal void SyncOptions()
      {
        if(_closed) return; _updating=true;
        _width.Value=NestWorkflow.Width; _length.Value=Length; _rotationStep.Value=RotationStep; _rotationPreset.SelectedIndex=(int)RotationChoice; _grain.SelectedIndex=(int)Grain;
        _allowReverse.SelectedIndex=AllowReverse?1:0; _obeyDefault.Checked=ObeyGrain; _rotationStep.Enabled=RotationChoice==RotationMode.Custom;
        _stack.SelectedIndex=(int)Stack;
        _searchTime.Value=SearchTime;
        _partGap.Value=PartGap;
        _grainDeviation.Value=GrainDeviation;
        _allowFlip.Checked=AllowFlip;
        RememberGlobalNumbers();
        _updating=false; Queue(); RefreshGrid();
      }
      private void ReplaceSelection(IReadOnlyList<Guid> ids)
      {
        if(_closed) return;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        var selectedIds=ids.ToHashSet();
        var oldMembers=_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id).ToHashSet();
        var added=ids.Where(id=>!oldMembers.Contains(id)).Select(_doc.Objects.FindId).Where(obj=>obj!=null&&!NestTableMetadata.IsDecoration(_doc,obj)).ToArray();
        var counts=new Dictionary<int,int>();
        int Group(IEnumerable<int> groups)=>groups.Where(index=>_doc.Groups[index] is {IsDeleted:false})
          .OrderBy(index=> { if(!counts.TryGetValue(index,out int count)) counts[index]=count=_doc.Groups.GroupObjectCount(index); return count; }).FirstOrDefault(-1);
        var addedGroups=added.Select(obj=>Group(obj!.Attributes.GetGroupList()??[])).ToArray();
        bool Touches(Row row)
        {
          int group=Group(row.Part.Attributes.GetGroupList()??[]);
          if(group>=0) return addedGroups.Contains(group);
          var bounds=row.Part.Outline.GetBoundingBox(row.Part.Plane);
          for(int index=0;index<added.Length;index++)
          {
            if(addedGroups[index]>=0) continue;
            var other=added[index]!.Geometry.GetBoundingBox(row.Part.Plane); double tolerance=_doc.ModelAbsoluteTolerance;
            if(other.Min.X<=bounds.Max.X+tolerance&&other.Max.X>=bounds.Min.X-tolerance&&
              other.Min.Y<=bounds.Max.Y+tolerance&&other.Max.Y>=bounds.Min.Y-tolerance) return true;
          }
          return false;
        }
        var kept=_rows.Where(row=>!Touches(row)&&row.Part.Items.All(item=>selectedIds.Contains(item.Id)&&_doc.Objects.FindId(item.Id) is {IsDeleted:false})).ToList();
        var retainedMembers=kept.SelectMany(row=>row.Part.Items).Select(item=>item.Id).ToHashSet();
        var captureIds=ids.Where(id=>!retainedMembers.Contains(id)&&_doc.Objects.FindId(id) is {} obj&&!NestTableMetadata.IsDecoration(_doc,obj)).ToArray();
        var parts=captureIds.Length==0?[]:PlanarPartSelection.Capture(_doc,captureIds,_doc.ModelAbsoluteTolerance,requirePlanarDetails:false);
        if(parts.Count==0&&captureIds.Any(id=>_doc.Objects.FindId(id) is {IsDeleted:false}))
          throw new InvalidOperationException("Selection has no complete planar part boundary; existing nest members retained.");
        var next=new List<Row>(kept);
        foreach(var part in parts)
        {
          var key=part.Items.Select(item=>item.Id).ToHashSet();
          var existing=_rows.FirstOrDefault(row=>key.SetEquals(row.Part.Items.Select(item=>item.Id)));
          if(existing!=null) { part.Dispose(); next.Add(existing); }
          else next.Add(CreateRow(_doc,part));
        }
        var retained=Volatile.Read(ref _bestInProgress)??_result;
        var working=_workingResult; int? selected=Selected?.Id;
        _packingCancellation?.Cancel();
        ClearHighlights();
        ClearPreviewCopies();
        foreach(var row in _rows.Except(next)) row.Dispose();
        _rows.Clear(); _rows.AddRange(next); _selection=ids.ToHashSet();
        if(!_anchored&&_rows.Count>0)
        {
          var bounds=BoundingBox.Empty; foreach(var row in _rows) bounds.Union(row.Part.Outline.GetBoundingBox(_plane));
          _anchor=_plane.PointAt(bounds.Max.X+TableGap+InitialLabelExtent(),bounds.Min.Y); _anchored=true;
        }
        var retainedIds=_rows.SelectMany(row=>row.InstanceIds()).ToHashSet();
        _result=RetainLayout(retained,retainedIds);
        _workingResult=working==null?null:RetainLayout(working,retainedIds);
        _packedRevision=UnpackedRevision; _place.Enabled=false;
        foreach(var row in _rows)
        {
          var placement=_result.Placements.FirstOrDefault(part=>part.Id==row.Id);
          if(placement!=null) { row.Table=placement.Table; row.X=placement.X; row.Y=placement.Y; row.Rotation=placement.Pose.Rotation; row.Flipped=placement.Pose.Flipped; }
          row.Status=!row.Included?"Excluded":_result.Errors.GetValueOrDefault(row.Id,placement==null?"Pending":"Ready");
        }
        _status.Text=$"{_rows.Count} parts"; RefreshGrid(selected); RefreshHighlights(); if(!_picking.DefersRedraw) _doc.Views.Redraw();
        Log.Write("vNest","selection update retainedParts={0} capturedParts={1} capturedObjects={2} totalParts={3} elapsedMs={4:F1}",
          kept.Count,parts.Count,captureIds.Length,_rows.Count,clock.Elapsed.TotalMilliseconds);
      }

      internal static NestPackingEngine.Result RetainLayout(NestPackingEngine.Result result,IReadOnlySet<int> ids)
      {
        var placements=result.Placements.Where(part=>ids.Contains(part.Id)).ToList();
        return result with {Placements=placements,Tables=placements.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),
          Errors=result.Errors.Where(pair=>ids.Contains(pair.Key)).ToDictionary(pair=>pair.Key,pair=>pair.Value)};
      }

      private bool Busy()=>RhinoDoc.ActiveDoc!=_doc||RhinoApp.InCommand!=0||RhinoGet.InGet(_doc)||DeferredNativeCommand.IsDispatching;
      private bool PackingBlocked()=>_placing||RhinoDoc.ActiveDoc!=_doc||RhinoApp.InCommand!=0||_doc.InCommand(false)!=0||DeferredNativeCommand.IsDispatching||_grainRequest!=null;
      private void PackingIdle(object? sender,EventArgs e)
      { if(_needsNest&&!_closed&&!_locationPicking&&!_packing&&!PackingBlocked()) ReNest(); }
      private void GlobalChanged()
      {
        if(_updating||_closed) return;
        if(_width.Value<=0||_length.Value<=0) return;
        var before=(NestWorkflow.Width,Length,RotationStep,RotationChoice,Grain,AllowReverse,ObeyGrain,AllowFlip,SearchTime,PartGap,GrainDeviation);
        NestWorkflow.Width=_width.Value; Length=_length.Value; RotationStep=_rotationStep.Value; RotationChoice=(RotationMode)Math.Max(0,_rotationPreset.SelectedIndex); Grain=(GrainMode)Math.Max(0,_grain.SelectedIndex);
        AllowReverse=_allowReverse.SelectedIndex==1; ObeyGrain=_obeyDefault.Checked==true; _rotationStep.Enabled=RotationChoice==RotationMode.Custom;
        AllowFlip=_allowFlip.Checked==true;
        SearchTime=_searchTime.Value;
        PartGap=_partGap.Value;
        GrainDeviation=_grainDeviation.Value;
        if(before==(NestWorkflow.Width,Length,RotationStep,RotationChoice,Grain,AllowReverse,ObeyGrain,AllowFlip,SearchTime,PartGap,GrainDeviation)) return;
        Save(); Queue(); RefreshGrid();
      }
      private void Edit(Action<Row> change)
      {
        if(_updating||_closed) return;
        var rows=SelectedParts; bool changed=false;
        foreach(var row in rows) { var before=SettingsOf(row); change(row); changed|=before!=SettingsOf(row); }
        if(!changed) return;
        Queue(); RefreshGrid(); RefreshHighlights(); _doc.Views.Redraw();
      }
      private void Queue([System.Runtime.CompilerServices.CallerMemberName]string reason="")
      {
        if(_closed) return; _layoutRevision++; _place.Enabled=false;
        Log.Write("vNest","nest invalidated revision={0} reason={1}",_layoutRevision,reason);
        if(_packing&&!_stopping)
        {
          _result=RetainLayout(Volatile.Read(ref _bestInProgress)??_workingResult??_result,_rows.SelectMany(row=>row.InstanceIds()).ToHashSet());
          _needsNest=true;
          _packingCancellation?.Cancel();
          _status.Text="Settings changed; re-nest queued";
        }
        else if(!_stopping&&_auto.Checked==true) _needsNest=true;
      }

      private void ReNest()
      {
        if(_closed||_locationPicking) return;
        if(PackingBlocked()||_packing)
        {
          _needsNest=true; _status.Text=_packing?"Nesting; re-nest queued":"Waiting for current command";
          Log.Write("vNest","nest queued appCommands={0} docCommands={1} inGet={2} dispatch={3} packing={4}",RhinoApp.InCommand,_doc.InCommand(false),RhinoGet.InGet(_doc),DeferredNativeCommand.IsDispatching,_packing);
          return;
        }
        SyncSelection();
        if(RequestMissingGrain()) { _needsNest=true; return; }
        _needsNest=false;
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try
        {
          var inputs=_rows.Where(row=>row.Included).SelectMany(row=>row.InstanceIds().Select(id=>new NestPackingEngine.Input(id,Poses(row),row.Fixed&&id==row.Id,row.Table,row.X,row.Y)
            {GrainGuided=UsesGrain(row)&&row.Guide.HasValue,Gap=PartGap})).ToList();
          Log.Write("vNest","nest started parts={0} preselectedObjects={1} grain={2} width={3} length={4}",inputs.Count,_selection.Count,Grain,NestWorkflow.Width,Length);
          foreach(var row in _rows.Where(row=>row.Included))
            Log.Write("vNest","grain poses id={0} name={1} mode={2} guide={3} manual={4} fixed={5} rotations={6} obey={7} deviation={8} reverse={9}",
              row.Id,row.Name,row.GrainOverride??Grain,row.Guide?.ToString()??"missing",row.ManualRotation,row.Fixed,
              string.Join(",",inputs.First(input=>input.Id==row.Id).Poses.Select(pose=>pose.Rotation.ToString("G17",System.Globalization.CultureInfo.InvariantCulture))),
              UsesGrain(row),row.GrainDeviationOverride??GrainDeviation,row.AllowReverseOverride??AllowReverse);
          int revision=_layoutRevision; double width=NestWorkflow.Width,length=Length,searchTime=SearchTime;
          var cancellation=new CancellationTokenSource(); _packingCancellation=cancellation;
          _stopping=false; Interlocked.Exchange(ref _bestInProgress,null);
          _packing=true; _place.Enabled=false; _status.Text="Nesting...";
          RefreshGrid();
          _status.SetProgress(0,clock.Elapsed); UpdateNestButton();
          Action<int> feedback=value=>Application.Instance.AsyncInvoke(()=>
          {
            if(_closed||!ReferenceEquals(_packingCancellation,cancellation)||!_packing||_stopping) return;
            _status.SetProgress(value,clock.Elapsed);
          });
          NestPackingEngine.Result? pending=null; int queued=0;
          Action<NestPackingEngine.Result> preview=result=>
          {
            Interlocked.Exchange(ref pending,result);
            if(Interlocked.CompareExchange(ref queued,1,0)!=0) return;
            Application.Instance.AsyncInvoke(()=>
            {
              var snapshot=Interlocked.Exchange(ref pending,null); Interlocked.Exchange(ref queued,0);
              if(snapshot==null||_closed||!ReferenceEquals(_packingCancellation,cancellation)||cancellation.IsCancellationRequested||revision!=_layoutRevision) return;
              _workingResult=snapshot;
              if(snapshot.Search is {} search) _status.ToolTip=$"{(snapshot.IsTrial?"Testing layout":"Best layout")}: {search.PlacementTests} placement tests; {search.Improvements} improvements";
              RefreshGrid();
              RefreshHighlights(); _doc.Views.Redraw();
            });
          };
          Action<NestPackingEngine.Result> accepted=result=>
          {
            if(ReferenceEquals(_packingCancellation,cancellation)&&revision==Volatile.Read(ref _layoutRevision))
              Interlocked.Exchange(ref _bestInProgress,result);
          };
          Task.Run(()=>NestContourPackingEngine.SolveUntilStopped(inputs,width,length,feedback,preview,cancellation.Token,searchTime,accepted),cancellation.Token).ContinueWith(task=>
          {
            clock.Stop();
            Application.Instance.AsyncInvoke(()=>
            {
              bool stopped=_stopping;
              var currentBest=Interlocked.Exchange(ref _bestInProgress,null);
              _packing=false; _packingCancellation=null; cancellation.Dispose();
              if(_closed) return;
              _stopping=false; _workingResult=null; _status.EndProgress(); UpdateNestButton();
              if(revision!=_layoutRevision)
              {
                if(currentBest!=null) _result=RetainLayout(currentBest,_rows.SelectMany(row=>row.InstanceIds()).ToHashSet());
                _packedRevision=UnpackedRevision; _place.Enabled=false;
                _status.Text=_needsNest?"Restarting nesting with updated settings":_rows.Count==0?"0 parts":"Modified; previous layout retained";
                Log.Write("vNest","packing revision changed from={0} to={1}; retained={2}",revision,_layoutRevision,_result.Placements.Count);
                RefreshGrid(); RefreshHighlights(); _doc.Views.Redraw(); return;
              }
              Log.Write("vNest","packing finished elapsedSeconds={0:F3} taskStatus={1}",clock.Elapsed.TotalSeconds,task.Status);
              if(task.IsCanceled)
              {
                if(currentBest!=null) ApplyPackingResult(currentBest with {Stopped=true},revision,width,length);
                _status.Text="Nesting stopped; "+ElapsedText(clock.Elapsed);
                _place.Enabled=_packedRevision==_layoutRevision&&_result.Placements.Count>0;
                RefreshHighlights(); _doc.Views.Redraw();
                return;
              }
              if(task.IsFaulted)
              { _status.Text="Nesting failed; "+ElapsedText(clock.Elapsed); RefreshHighlights(); _doc.Views.Redraw(); Log.Write("vNest","packing failed: {0}",task.Exception!); return; }
              ApplyPackingResult(task.Result,revision,width,length);
              if(stopped||task.Result.Stopped) _status.Text=$"Stopped; {_result.Tables} tables; {_result.Placements.Count} parts";
              _status.Text+="; "+ElapsedText(clock.Elapsed);
            });
          });
        }
        catch(Exception ex) { clock.Stop(); _packing=false; UpdateNestButton(); _place.Enabled=false; _status.Text="Nesting failed; "+ElapsedText(clock.Elapsed); Log.Write("vNest","packing preparation failed: {0}",ex); }
      }

      private void UpdateNestButton()
      {
        _nest.Text=_packing?"Stop":"Nest";
        _nest.Enabled=!_stopping;
        _nest.ToolTip=_packing?"Stop searching and retain the best valid nest reached so far":"Pack included parts; retain fixed positions, manual angles and grain settings";
      }

      private void StopPacking()
      {
        if(!_packing||_packingCancellation==null) return;
        _stopping=true; _needsNest=false; UpdateNestButton(); _status.Text="Stopping...";
        _workingResult=Volatile.Read(ref _bestInProgress)??_result; RefreshHighlights(); _doc.Views.Redraw();
        _packingCancellation.Cancel();
      }

      private void ClearNest()
      {
        if(_closed) return;
        if(Busy()) { RhinoApp.WriteLine("vNest: finish the current command before clearing."); return; }
        _needsNest=false; _layoutRevision++; _packedRevision=UnpackedRevision;
        _packingCancellation?.Cancel(); _stopping=false; Interlocked.Exchange(ref _bestInProgress,null);
        _doc.Objects.UnselectAll(); ReplaceSelection([]); _anchored=false;
        _status.EndProgress(); UpdateNestButton(); _status.ToolTip=string.Empty;
        Log.Write("vNest","part list and preview cleared; source geometry retained");
      }

      private void FocusClickedPart(Guid[] ids,bool selected)
      {
        if(_closed||!selected) return;
        var members=ids.ToHashSet(); int index=_rows.FindIndex(row=>row.Part.Items.Any(item=>members.Contains(item.Id)));
        if(index<0) return;
        bool previous=_updating; _updating=true;
        try { _grid.UnselectAll(); _grid.SelectRow(index); _grid.ScrollToRow(index); }
        finally { _updating=previous; }
        UpdateEditor(); RefreshHighlights();
      }

      private void ApplyPackingResult(NestPackingEngine.Result result,int revision,double width,double length)
      {
        try
        {
          _result=result; _packedRevision=revision;
          foreach(var row in _rows)
          {
            var packed=_result.Placements.FirstOrDefault(part=>part.Id==row.Id);
            if(packed!=null) { row.Table=packed.Table; row.X=packed.X; row.Y=packed.Y; row.Rotation=packed.Pose.Rotation; row.Flipped=packed.Pose.Flipped; }
            row.Status=PlacementStatus(row.Id,row.Included,_result,false,true);
          }
          _place.Enabled=_result.Placements.Count>0;
          _status.Text=$"{_result.Tables} tables; {_result.Placements.Count} parts; {_result.Errors.Count} skipped";
          _skipped.SetObjects(_rows.Where(row=>RowHasError(row,_result)).SelectMany(row=>row.Part.Items).Select(item=>item.Id));
          RefreshGrid(Selected?.Id); RefreshHighlights(); _doc.Views.Redraw();
          Log.Write("vNest","preview tables={0} parts={1} errors={2} width={3:G17} length={4:G17}",_result.Tables,_result.Placements.Count,_result.Errors.Count,width,length);
          if(result.Search is {} search)
          {
            Log.Write("vNest","search tests={0} layouts={1} improvements={2} initialSeconds={3:F2} searchSeconds={4:F2} budgetExpired={5} halfTurns={6} consumedLength={7:G17}",search.PlacementTests,search.Layouts,search.Improvements,search.InitialSeconds,search.SearchSeconds,search.BudgetExpired,search.HalfTurnTests,search.ConsumedLength);
            _status.ToolTip=$"{search.Layouts} layouts; {search.PlacementTests} placement tests; {search.HalfTurnTests} half-turn tests; {search.Improvements} improvements{(search.BudgetExpired?"; search time reached":"")}";
          }
          if(result.Profile is {} profile)
            Log.Write("vNest","profile pairHits={0} pairMisses={1} pairSeconds={2:F3} anchorCalls={3} anchorSeconds={4:F3} collisionCalls={5} collisionSeconds={6:F3} posesPruned={7} cacheEntries={8} cacheVertices={9}",
              profile.PairHits,profile.PairMisses,profile.PairSeconds,profile.AnchorCalls,profile.AnchorSeconds,profile.CollisionCalls,profile.CollisionSeconds,profile.PosesPruned,profile.CacheEntries,profile.CacheVertices);
          foreach(var row in _rows.Where(row=>_result.Errors.ContainsKey(row.Id)))
          {
            var poses=Poses(row); var best=poses.OrderBy(pose=>Math.Max(pose.Width/NestWorkflow.Width,pose.Length/Length)).FirstOrDefault();
            Log.Write("vNest","rejected part={0} reason={1} grain={2} poses={3} bestWidth={4} bestLength={5} source={6}",row.Id,_result.Errors[row.Id],row.GrainOverride??Grain,poses.Count,
              best==null?"none":best.Width.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
              best==null?"none":best.Length.ToString("R",System.Globalization.CultureInfo.InvariantCulture),string.Join(",",row.Part.Items.Select(item=>item.Id)));
          }
        }
        catch(Exception ex) { _place.Enabled=false; _status.Text="Nesting failed"; Log.Write("vNest","packing display failed: {0}",ex); }
      }

      private void RefreshGrid(int? selected=null)
      {
        var selectedIds=selected.HasValue?new HashSet<int> {selected.Value}:_grid.SelectedRows
          .Where(index=>index>=0&&index<_rows.Count).Select(index=>_rows[index].Id).ToHashSet();
        var result=VisibleResult; var placements=result.Placements.ToDictionary(part=>part.Id);
        _updating=true;
        _grid.DataStore=_rows.Select(row=>
        {
          var placement=placements.GetValueOrDefault(row.Id);
          bool live=_packing&&_workingResult!=null;
          row.Status=PlacementStatus(row.Id,row.Included,result,_packing,_packing?live:_packedRevision==_layoutRevision);
          int count=result.Placements.Count(part=>row.OwnsInstance(part.Id));
          if(row.Copies>DefaultCopies&&row.Included) row.Status=$"{count}/{row.Copies} placed; "+row.Status;
          return new GridItem {Tag=row,Values=[row.Included,row.Name,placement==null?"-":((live?placement.Table:row.Table)+1).ToString(),
            (live?placement?.Pose.Rotation??row.Rotation:row.Rotation).ToString("G6"),row.Fixed,row.Status,row.Copies]};
        }).ToList();
        for(int index=0;index<_rows.Count;index++) if(selectedIds.Contains(_rows[index].Id)) _grid.SelectRow(index);
        _updating=false; UpdateEditor();
        RefreshTableRows();
      }

      internal static string PlacementStatus(int id,bool included,NestPackingEngine.Result result,bool packing,bool current)
      {
        if(!included) return "Excluded";
        bool placed=result.Placements.Any(part=>part.Id==id);
        if(!current) return packing?(placed?"Previous layout":"Queued"):(placed?"Re-nest needed":"Pending");
        if(result.Errors.TryGetValue(id,out var error)) return error;
        if(!placed) return packing?"Queued":"Pending";
        return result.IsTrial?"Trial placement":packing?"Placed; refining":result.Stopped?"Placed; stopped":"Placed";
      }
      private void UpdateEditor()
      {
        var rows=SelectedParts; _displayedSelection=rows.Select(part=>part.Id).ToHashSet(); var row=rows.FirstOrDefault(); _updating=true;
        foreach(var control in new Control[]{_included,_fixed,_flipped,_table,_angle,_x,_y,_limit,_partGrain,_partRotation,_obey,_guideAngle,_partDeviation,_defaultDeviation,_copies}) control.Enabled=row!=null;
        foreach(var field in _mixedNumbers.Values) field.Enabled=row!=null;
        _table.Enabled=_x.Enabled=_y.Enabled=rows.Length==1;
        if(row!=null)
        {
          var placement=_packing&&_workingResult!=null?_workingResult.Placements.FirstOrDefault(part=>part.Id==row.Id):null;
          ShowCheck(_included,rows.Select(part=>part.Included)); ShowCheck(_fixed,rows.Select(part=>part.Fixed));
          ShowCheck(_flipped,rows.Select(part=>part.Flipped));
          _table.Value=(placement?.Table??row.Table)+1; _x.Value=Math.Max(0,placement?.X??row.X); _y.Value=Math.Max(0,placement?.Y??row.Y);
          ShowNumber(_angle,rows.Select(part=>NormalizeRotation(_packing&&_workingResult?.Placements.FirstOrDefault(copy=>copy.Id==part.Id) is {} live?live.Pose.Rotation:part.Rotation)));
          ShowNumber(_copies,rows.Select(part=>(double)part.Copies));
          ShowNumber(_limit,rows.Select(part=>part.RotationStepOverride??RotationStep));
          ShowChoice(_partGrain,new[]{"Default"}.Concat(GrainNames),rows.Select(part=>part.GrainOverride.HasValue?(int)part.GrainOverride+1:0));
          bool constrained=rows.Any(UsesGrain);
          ShowChoice(_partRotation,new[]{"Default"}.Concat(constrained?ReversalLabels:RotationLabels),rows.Select(part=>RotationIndex(part,constrained)));
          _limit.Enabled=!constrained; _mixedNumbers[_limit].Enabled=!constrained;
          ShowCheck(_obey,rows.Select(part=>part.ObeyGrainOverride??ObeyGrain));
          ShowNumber(_guideAngle,rows.Select(part=>NormalizeRotation(EffectiveGuide(part.GrainOverride??Grain,GuideAngle(part))??SourceGrainAngle)));
          ShowCheck(_defaultDeviation,rows.Select(part=>!part.GrainDeviationOverride.HasValue));
          ShowNumber(_partDeviation,rows.Select(part=>part.GrainDeviationOverride??GrainDeviation));
          _partDeviation.Enabled=constrained; _mixedNumbers[_partDeviation].Enabled=constrained; _defaultDeviation.Enabled=constrained;
          foreach(var field in new[]{_table,_x,_y}) _displayedNumbers[field]=field.Value;
        }
        _updating=false;
        foreach(var reset in _resets) reset.Refresh();
      }
      private static bool UsesGrain(Row row)=>GrainConstrained(row.GrainOverride??Grain,GuideAngle(row),row.ObeyGrainOverride??ObeyGrain);
      private void AddSelected()
      {
        if(Busy()) return;
        SyncSelection();
      }
      private void RemoveSelected()
      {
        if(Busy()) return;
        int neighbor=_grid.SelectedRows.Where(index=>index>=0&&index<_rows.Count).DefaultIfEmpty(-1).Min();
        var ids=_grid.SelectedRows.Where(index=>index>=0&&index<_rows.Count).SelectMany(index=>_rows[index].Part.Items).Select(item=>item.Id).Distinct().ToArray();
        foreach(var id in ids) _doc.Objects.FindId(id)?.Select(false);
        SyncSelection();
        if(neighbor>=0&&_rows.Count>0) { _grid.UnselectAll(); _grid.SelectRow(Math.Min(neighbor,_rows.Count-1)); }
      }
      private bool RequestMissingGrain()
      {
        if(_grainRequest!=null) return true;
        if(_closed||_locationPicking) return false;
        var row=_rows.FirstOrDefault(row=>row.Included&&(row.ObeyGrainOverride??ObeyGrain)&&(row.GrainOverride??Grain)==GrainMode.Force&&row.Guide==null);
        if(row!=null) RequestGrain(row);
        return row!=null;
      }
      private void RequestGrain(Row row)
      {
        if(_grainRequest!=null||_locationPicking||Busy()) return;
        _grainRequest=row; Visible=false;
        DeferredNativeCommand.Run(_doc,"_vNestGrainProxy",result=>
        {
          _grainRequest=null;
          if(_closed) return;
          if(result!=Result.Success) _needsNest=false;
          if(result==Result.Success)
          {
            if(row.Guide is {} guide)
              foreach(var selected in SelectedParts)
              {
                var direction=guide-selected.Part.Plane.Normal*(guide*selected.Part.Plane.Normal);
                if(direction.Unitize()) selected.Guide=direction;
                if((selected.GrainOverride??Grain)==GrainMode.Off) selected.GrainOverride=GrainMode.Auto;
              }
            Queue();
          }
          Show(); _timer.Start(); UpdateEditor();
        });
      }
      internal Result PickRequestedGrain()=>_grainRequest is {} row&&PickGrain(_doc,row)?Result.Success:Result.Cancel;
      private void PickLocation()
      {
        if(_closed||_locationPicking) return;
        if(Busy()) { RhinoApp.WriteLine("vNest: finish the current command before choosing a location."); return; }
        BeginLocation();
      }
      private void BeginLocation()
      {
        _locationPicking=true; _timer.Stop(); ClearHighlights(); Visible=false;
        DeferredNativeCommand.Run(_doc,"_vNestLocationProxy",result=>
        {
          _locationPicking=false;
          if(_closed) return;
          Log.Write("vNest","location completed result={0} anchor={1}",result,_anchor);
          _place.Enabled=_result.Placements.Count>0;
          Show(); RefreshHighlights(); _doc.Views.Redraw();
          if(result==Result.Failure) RhinoApp.WriteLine("vNest: could not open the location picker; previous location retained.");
          _timer.Start();
        });
      }
      internal Result PickRequestedLocation()
      {
        var previous=_anchor;
        bool previousAnchored=_anchored;
        _preview.Enabled=false;
        try
        {
          RhinoApp.SetFocusToMainWindow(_doc);
          using var getter=new Rhino.Input.Custom.GetPoint(); getter.SetCommandPrompt("Place nest tables; Enter keeps preview location");
          getter.PermitObjectSnap(LocationObjectSnaps); getter.EnableObjectSnapCursors(LocationObjectSnaps);
          getter.SetBasePoint(previous,false); getter.AcceptNothing(true);
          getter.DynamicDraw+=(_,e)=> { _anchor=e.CurrentPoint; _preview.Draw(e.Display,true); };
          var result=getter.Get();
          if(result==GetResult.Point) { _anchor=getter.Point(); _anchored=true; return Result.Success; }
          if(result==GetResult.Nothing) { _anchored=true; return Result.Success; }
          _anchor=previous; _anchored=previousAnchored; return Result.Cancel;
        }
        catch { _anchor=previous; _anchored=previousAnchored; throw; }
        finally { _preview.Enabled=!_closed; _doc.Views.Redraw(); }
      }

      private void ClearHighlights()
      { _highlight.SetObjects([]); foreach(var copy in _highlightCopies) copy.Dispose(); _highlightCopies.Clear(); }

      private void ClearPreviewCopies()
      { foreach(var entry in _previewCopies.Values) foreach(var item in entry.Items) item.Geometry.Dispose(); _previewCopies.Clear(); }

      private IReadOnlyList<PlanarPartSelection.Item> PreviewCopies(Row row)
      {
        return CopiesAt(row,row.Id,VisiblePlacement(row));
      }
      private IReadOnlyList<PlanarPartSelection.Item> InstancePreviewCopies(Row row,NestPackingEngine.Placement placement)=>
        CopiesAt(row,placement.Id,placement.Id==row.Id?VisiblePlacement(row):InstanceTransform(row,placement));
      private Transform InstanceTransform(Row row,NestPackingEngine.Placement placement)=>
        PlacementAt(row,_plane,DisplayAnchor(placement.Table),placement.Table,placement.X,placement.Y,placement.Pose.Rotation,placement.Pose.Flipped);
      private IReadOnlyList<PlanarPartSelection.Item> CopiesAt(Row row,int id,Transform transform)
      {
        if(_previewCopies.TryGetValue(id,out var existing)&&existing.Transform==transform) return existing.Items;
        if(existing.Items!=null) foreach(var item in existing.Items) item.Geometry.Dispose();
        var copies=new List<PlanarPartSelection.Item>();
        foreach(var item in row.Part.Items)
        { var copy=TransformedCopy(item.Geometry,transform); copies.Add(item with {Geometry=copy}); }
        _previewCopies[id]=(transform,copies); return copies;
      }
      private Transform VisiblePlacement(Row row)
      {
        var placement=_workingResult?.Placements.FirstOrDefault(part=>part.Id==row.Id);
        return placement==null?Placement(row,_plane,DisplayAnchor(row.Table)):InstanceTransform(row,placement);
      }
      private Guid[]? KnownInteriorMembers(Line ray)
      {
        foreach(var row in _rows)
        {
          if(NestDocumentPicking.Inside(row.Part.Outline,row.Part.Plane,ray,_doc.ModelAbsoluteTolerance,out _))
            return row.Part.Items.Select(item=>item.Id).ToArray();
        }
        foreach(var row in _rows.Where(row=>row.Included))
        foreach(var placement in VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)))
        {
          if(!InstanceTransform(row,placement).TryGetInverse(out var inverse)) continue;
          var sourceRay=ray; sourceRay.Transform(inverse);
          if(NestDocumentPicking.Inside(row.Part.Outline,row.Part.Plane,sourceRay,_doc.ModelAbsoluteTolerance,out _))
            return row.Part.Items.Select(item=>item.Id).ToArray();
        }
        return null;
      }
      private void RefreshHighlights()
      {
        ClearHighlights();
        var selectedIds=_grid.SelectedRows.Where(index=>index>=0&&index<_rows.Count).SelectMany(index=>_rows[index].Part.Items).Select(item=>item.Id).ToHashSet();
        _sources.SetObjects(_rows.Where(row=>row.Included&&!RowHasError(row,VisibleResult)).SelectMany(row=>row.Part.Items).Select(item=>item.Id).Where(id=>!selectedIds.Contains(id)));
        var errorIds=_rows.Where(row=>RowHasError(row,VisibleResult)).SelectMany(row=>row.Part.Items).Select(item=>item.Id).ToHashSet();
        _selectedSources.SetObjects(selectedIds.Where(id=>!errorIds.Contains(id)));
        _skipped.SetObjects(errorIds);
        var geometry=new Dictionary<Guid,GeometryBase>();
        foreach(var index in _grid.SelectedRows)
        {
          if(index<0||index>=_rows.Count) continue;
          var row=_rows[index]; if(!row.Included) continue;
          foreach(var placement in VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)))
          {
          var transform=placement.Id==row.Id?VisiblePlacement(row):InstanceTransform(row,placement);
          foreach(var item in row.Part.Items)
          { var copy=TransformedCopy(item.Geometry,transform); geometry.Add(Guid.NewGuid(),copy); _highlightCopies.Add(copy); }
          }
        }
        _highlight.SetObjects(geometry.Keys,geometry);
      }
      private void DocumentClosed(object? sender,DocumentEventArgs e) { if(e.Document.RuntimeSerialNumber==DocumentSerial) Close(); }
      private void DisposeState()
      {
        if(_closed) return; _closed=true; _timer.Stop(); _timer.Dispose();
        ClearTableLabels();
        foreach(var reset in _resets) reset.Dispose();
        _packingCancellation?.Cancel();
        RhinoDoc.CloseDocument-=DocumentClosed; RhinoApp.Idle-=PackingIdle; _picking.Dispose(); ClearHighlights(); ClearPreviewCopies(); _highlight.Dispose(); _sources.Dispose(); _selectedSources.Dispose(); _skipped.Dispose(); _preview.Enabled=false;
        foreach(var row in _rows) row.Dispose(); _rows.Clear(); _doc.Views.Redraw();
      }

      private void Place()
      {
        if(_placing||_closed) return;
        if(Busy())
        {
          _status.Text="Finish the current Rhino command before Place";
          Log.Write("vNest","placement blocked appCommands={0} docCommands={1} inGet={2} dispatch={3}",RhinoApp.InCommand,_doc.InCommand(false),RhinoGet.InGet(_doc),DeferredNativeCommand.IsDispatching);
          RhinoApp.WriteLine("vNest: finish the current command before placing."); return;
        }
        if(_packing||_packedRevision!=_layoutRevision||_needsNest||_grainRequest!=null||_result.Placements.Count==0)
        { _status.Text="Press Nest and wait for the current layout before Place"; RhinoApp.WriteLine("vNest: press Nest and wait for the current layout before placing."); return; }
        var added=new List<Guid>(); var groups=new List<int>();
        _placing=true; _timer.Stop(); _picking.Enabled=false;
        uint undo=0;
        try
        {
          undo=_doc.BeginUndoRecord("vNest");
          if(_doc.UndoRecordingEnabled&&undo==0)
          { _status.Text="Could not begin placement undo record"; RhinoApp.WriteLine("vNest: could not begin an independent undo record; no objects added."); return; }
          var session=Guid.NewGuid().ToString("N"); int reference=UzipCommon.EnsureLayer(_doc,TableLayer);
          for(int table=0;table<_result.Tables;table++)
          {
            var plane=TablePlane(_plane,DisplayAnchor(table),table);
            string tableName=TableDisplayName(table);
            int tableGroup=_doc.Groups.Add(UniqueTableGroupName(tableName)); groups.Add(tableGroup);
            if(tableGroup<0) throw new InvalidOperationException("Could not create table group.");
            Guid tableGroupId=_doc.Groups[tableGroup].Id;
            using var border=TableOutlineForTable(table).DuplicateCurve();
            var attributes=new ObjectAttributes {LayerIndex=reference,Name=TableName};
            attributes.AddToGroup(tableGroup); NestTableMetadata.Identify(attributes,tableGroupId,session,table,tableName,NestTableMetadata.BorderRole);
            var borderId=_doc.Objects.AddCurve(border,attributes);
            if(borderId==Guid.Empty) throw new InvalidOperationException("Could not add table outline."); added.Add(borderId);
            foreach(var label in LabelsForTable(table))
            {
              using var labelAttributes=new ObjectAttributes {LayerIndex=reference}; labelAttributes.AddToGroup(tableGroup);
              NestTableMetadata.Identify(labelAttributes,tableGroupId,session,table,tableName,NestTableMetadata.LabelRole);
              var labelId=_doc.Objects.AddText(label,labelAttributes);
              if(labelId==Guid.Empty) throw new InvalidOperationException("Could not add table label."); added.Add(labelId);
            }
            foreach(var placement in _result.Placements.Where(part=>part.Table==table))
            {
              var row=_rows.Single(part=>part.OwnsInstance(placement.Id));
              int partGroup=_doc.Groups.Add(GroupPrefix+session+"_part_"+placement.Id); groups.Add(partGroup);
              if(partGroup<0) throw new InvalidOperationException("Could not create part group.");
              var transform=InstanceTransform(row,placement);
              foreach(var item in row.Part.Items)
              {
                using var copy=TransformedCopy(item.Geometry,transform);
                using var attr=item.Attributes.Duplicate(); attr.RemoveFromAllGroups(); attr.AddToGroup(partGroup); attr.AddToGroup(tableGroup);
                attr.SetUserString(MetadataPrefix+"session",session); attr.SetUserString(MetadataPrefix+"table",(table+1).ToString());
                attr.SetUserString(MetadataPrefix+"part",row.Id.ToString()); attr.SetUserString(MetadataPrefix+"instance",placement.Id.ToString());
                attr.SetUserString(MetadataPrefix+"copy",(row.InstanceIds().ToList().IndexOf(placement.Id)+1).ToString());
                attr.SetUserString(MetadataPrefix+"rotation",placement.Pose.Rotation.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                attr.SetUserString(MetadataPrefix+"flipped",placement.Pose.Flipped.ToString());
                var id=_doc.Objects.Add(copy,attr); if(id==Guid.Empty) throw new InvalidOperationException("Could not add a nested part."); added.Add(id);
              }
            }
          }
          _doc.Objects.UnselectAll(); _doc.Objects.Select(added); Save();
          Log.Write("vNest","placement created tables={0} parts={1} objects={2}",_result.Tables,_result.Placements.Count,added.Count);
          RhinoApp.WriteLine($"vNest: created {_result.Tables} table(s), {_result.Placements.Count} grouped part copies, {_result.Tables} grouped table-label parts, {_result.Errors.Count} skipped; originals retained.");
          Close();
        }
        catch(Exception ex)
        { foreach(var id in added) _doc.Objects.Delete(id,true); foreach(var group in groups) _doc.Groups.Delete(group);
          Log.Write("vNest","placement rolled back: {0}",ex); RhinoApp.WriteLine("vNest: placement failed; originals retained."); }
        finally
        {
          if(undo!=0) _doc.EndUndoRecord(undo);
          _placing=false;
          if(!_closed) { _picking.Enabled=true; _timer.Start(); }
          _doc.Views.Redraw();
        }
      }

      private GeometryBase TransformedCopy(GeometryBase source,Transform transform)
      {
        var copy=source.Duplicate();
        if(!copy.Transform(transform)) { copy.Dispose(); throw new InvalidOperationException("Could not transform a nested part item."); }
        if(transform.Determinant<0&&copy is AnnotationBase annotation&&!AnnotationTextTransform.MakeMirroredTextReadable(_doc,annotation))
        { copy.Dispose(); throw new InvalidOperationException("Could not retain readable text on a flipped part."); }
        return copy;
      }

      private sealed class Preview(NestWindow window) : DisplayConduit
      {
        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber!=window.DocumentSerial) return;
          for(int table=0;table<PreviewTableCount(window.VisibleResult.Tables,window._locationPicking);table++)
          { var plane=TablePlane(window._plane,window.DisplayAnchor(table),table);
            e.IncludeBoundingBox(new Rectangle3d(plane,NestWorkflow.Width,Length).BoundingBox);
            if(table<window.VisibleResult.Tables)
            { foreach(var label in window.LabelsForTable(table)) e.IncludeBoundingBox(label.GetBoundingBox(true)); e.IncludeBoundingBox(window.TableOutlineForTable(table).GetBoundingBox(true)); } }
        }
        protected override void PostDrawObjects(DrawEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber!=window.DocumentSerial) return;
          Draw(e.Display);
        }
        internal void Draw(DisplayPipeline display,bool locating=false)
        {
          for(int table=0;table<PreviewTableCount(window.VisibleResult.Tables,locating||window._locationPicking);table++)
          { var plane=TablePlane(window._plane,window.DisplayAnchor(table),table);
            using var border=new Rectangle3d(plane,NestWorkflow.Width,Length).ToNurbsCurve();
            var color=window._lists.SelectedIndex==1&&window._tableGrid.SelectedRow==table?SelectedPartStyle.BodyColor:TableColor;
            PreviewDisplay.DrawCurve(display,table<window.VisibleResult.Tables?window.TableOutlineForTable(table):border,color);
            if(table<window.VisibleResult.Tables)
            { foreach(var label in window.LabelsForTable(table)) display.DrawAnnotation(label,color); } }
          foreach(var row in window._rows.Where(row=>row.Included))
          foreach(var placement in window.VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)))
          {
            foreach(var item in window.InstancePreviewCopies(row,placement))
            {
              var color=SDColor.FromArgb(PreviewAlpha,item.Color);
              switch(item.Geometry)
              {
                case Curve curve: PreviewDisplay.DrawCurve(display,curve,color); break;
                case AnnotationBase text: display.DrawAnnotation(text,color); break;
                case Rhino.Geometry.Point point: display.DrawPoint(point.Location,PointStyle.RoundSimple,PreviewPointSize,color); break;
                case TextDot dot: display.DrawDot(dot.Point,dot.Text,color,SDColor.White); break;
              }
            }
          }
        }
      }
    }

    private sealed class PackingStatus : Drawable
    {
      private string _text=string.Empty;
      private int? _percent;
      internal string Text { get=>_text; set { _text=value; Invalidate(); } }
      internal PackingStatus() { Height=GridRowHeight; }
      internal void SetProgress(int percent,TimeSpan elapsed)
      {
        _percent=Math.Clamp(percent,0,100);
        Text=ProgressText(_percent.Value,elapsed);
      }
      internal void EndProgress() { _percent=null; Invalidate(); }
      protected override void OnPaint(PaintEventArgs e)
      {
        base.OnPaint(e);
        e.Graphics.FillRectangle(SystemColors.ControlBackground,new RectangleF(0,0,Width,Height));
        if(_percent.HasValue)
          e.Graphics.FillRectangle(new Eto.Drawing.Color(SystemColors.Highlight,ProgressTintAlpha),new RectangleF(0,0,Width*_percent.Value/100f,Height));
        var font=SystemFonts.Default(); var size=e.Graphics.MeasureString(font,Text);
        float x=_percent.HasValue?Math.Max(0,(Width-size.Width)/2):0;
        e.Graphics.DrawText(font,SystemColors.ControlText,x,Math.Max(0,(Height-size.Height)/2),Text);
      }
    }

    internal static string ProgressText(int percent,TimeSpan elapsed)
      => $"{Math.Clamp(percent,0,100)}%";

    internal static string ElapsedText(TimeSpan elapsed)
    {
      if(elapsed<TimeSpan.Zero) elapsed=TimeSpan.Zero;
      var culture=System.Globalization.CultureInfo.InvariantCulture;
      string duration=elapsed.TotalHours<1?elapsed.ToString(ElapsedShortFormat,culture):
        Math.Floor(elapsed.TotalHours).ToString(culture)+":"+elapsed.ToString(ElapsedHourTailFormat,culture);
      return "elapsed "+duration;
    }
  }

  [CommandStyle(Style.Hidden|Style.Transparent|Style.DoNotRepeat|Style.NotUndoable)]
  public sealed class vNestGrainProxy : vToolsCommand
  {
    public override string EnglishName=>"vNestGrainProxy";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)=>NestWorkflow.ActiveWindow is {} window&&window.DocumentSerial==doc.RuntimeSerialNumber?window.PickRequestedGrain():Result.Cancel;
  }
  [CommandStyle(Style.Hidden|Style.Transparent|Style.DoNotRepeat|Style.NotUndoable)]
  public sealed class vNestLocationProxy : vToolsCommand
  {
    public override string EnglishName=>"vNestLocationProxy";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)=>NestWorkflow.ActiveWindow is {} window&&window.DocumentSerial==doc.RuntimeSerialNumber?window.PickRequestedLocation():Result.Cancel;
  }
}

namespace vTools.Commands
{
  using Eto.Forms;
  using Rhino.Geometry;


  internal static partial class NestWorkflow
  {
    internal sealed partial class NestWindow
    {
      private readonly Dictionary<CheckBox,bool?> _displayedChecks=[];
      private readonly Dictionary<DropDown,int> _displayedChoices=[];
      private readonly Dictionary<DropDown,int> _mixedChoiceIndices=[];
      private readonly record struct PartSettings(bool Included,bool Fixed,bool Manual,bool Flipped,bool? Flip,GrainMode? Grain,
        RotationMode? Rotation,double? Step,bool? Obey,bool? Reverse,double? Deviation,double Angle,int Table,double X,double Y,Vector3d? Guide,int Copies);
      private static PartSettings SettingsOf(Row row)=>new(row.Included,row.Fixed,row.ManualRotation,row.Flipped,row.FlipOverride,
        row.GrainOverride,row.RotationOverride,row.RotationStepOverride,row.ObeyGrainOverride,row.AllowReverseOverride,row.GrainDeviationOverride,
        row.Rotation,row.Table,row.X,row.Y,row.Guide,row.Copies);

      private void RememberGlobalNumbers()
      { foreach(var field in new[]{_width,_length,_rotationStep,_searchTime,_grainDeviation,_partGap}) _displayedNumbers[field]=field.Value; }
      private void CheckChanged(CheckBox field,Action action)
      {
        if(_updating||_closed) return;
        var shown=_displayedChecks.GetValueOrDefault(field);
        if(!field.Checked.HasValue) { if(shown.HasValue) field.Checked=!shown.Value; return; }
        if(shown==field.Checked) return;
        _displayedChecks[field]=field.Checked; action();
      }
      private void ChoiceChanged(DropDown field,Action action)
      {
        if(_updating||_closed||field.SelectedIndex<0||field.SelectedIndex==_mixedChoiceIndices.GetValueOrDefault(field,-1)||
          _displayedChoices.GetValueOrDefault(field,-1)==field.SelectedIndex) return;
        _displayedChoices[field]=field.SelectedIndex; action();
      }
      private void ShowNumber(NumericStepper field,IEnumerable<double> values)
      {
        var numbers=values.ToArray(); field.Value=numbers[0]; _displayedNumbers[field]=field.Value;
        _mixedNumbers[field].SetMixed(numbers.Skip(1).Any(value=>FieldResetOverlay.NumberChanged(value,numbers[0],field.DecimalPlaces)));
      }
      private void ShowCheck(CheckBox field,IEnumerable<bool> values)
      {
        var distinct=values.Distinct().ToArray(); field.Checked=distinct.Length==1?distinct[0]:null;
        _displayedChecks[field]=field.Checked;
      }
      private void ShowChoice(DropDown field,IEnumerable<string> labels,IEnumerable<int> values)
      {
        var choices=labels.ToArray(); var indices=values.Distinct().ToArray();
        bool mixed=indices.Length!=1||indices[0]<0;
        _mixedChoiceIndices[field]=mixed?choices.Length:-1;
        field.DataStore=mixed?choices.Append(MixedValue).ToArray():choices;
        field.SelectedIndex=mixed?choices.Length:indices[0];
        _displayedChoices[field]=field.SelectedIndex;
      }
      private static int RotationIndex(Row row,bool constrained)
      {
        if(UsesGrain(row)) return row.AllowReverseOverride.HasValue?(row.AllowReverseOverride.Value?2:1):0;
        if(!row.RotationOverride.HasValue) return 0;
        return constrained?row.RotationOverride==RotationMode.None?1:row.RotationOverride==RotationMode.Step180?2:-1:(int)row.RotationOverride+1;
      }
      private static bool RowHasError(Row row,NestPackingEngine.Result result)=>row.InstanceIds().Any(result.Errors.ContainsKey);
      private static void ResetPart(Row row)
      {
        row.Copies=DefaultCopies; row.Included=DefaultIncluded; row.Fixed=DefaultFixed; row.ManualRotation=DefaultManualRotation;
        row.Flipped=DefaultAllowFlip; row.FlipOverride=null; row.GrainOverride=null; row.RotationOverride=null;
        row.RotationStepOverride=null; row.ObeyGrainOverride=null; row.AllowReverseOverride=null; row.GrainDeviationOverride=null;
        row.Rotation=DefaultRotation; row.Table=DefaultTable; row.X=row.Y=DefaultCoordinate; row.Guide=row.DetectedGuide;
      }
      private void InstallResets()
      {
        void Add(Control field,Func<bool> changed,Action reset,string name)
        {
          var overlay=new FieldResetOverlay(field,changed,reset,"Reset "+name+" to default"); _resets.Add(overlay);
        }
        void Global(NumericStepper field,double value,string name)=>Add(field,
          ()=>FieldResetOverlay.NumberChanged(field.Value,value,field.DecimalPlaces),()=>field.Value=value,name);
        Global(_width,DefaultWidth,"width"); Global(_length,DefaultLength,"length");
        Global(_rotationStep,DefaultRotationStep,"rotation step"); Global(_searchTime,DefaultSearchTime,"search time");
        Global(_partGap,DefaultPartGap,"gap"); Global(_grainDeviation,DefaultGrainDeviation,"grain deviation");
        Add(_grain,()=>_grain.SelectedIndex!=(int)DefaultGrain,()=>_grain.SelectedIndex=(int)DefaultGrain,"grain mode");
        Add(_rotationPreset,()=>_rotationPreset.SelectedIndex!=(int)DefaultRotationMode,()=>_rotationPreset.SelectedIndex=(int)DefaultRotationMode,"free rotation");
        Add(_allowReverse,()=>AllowReverse!=DefaultAllowReverse,()=>_allowReverse.SelectedIndex=DefaultAllowReverse?1:0,"grain reversal");
        void PartNumber(NumericStepper field,Func<Row,bool> changed,Action<Row> reset,string name)
        {
          Add(field,()=>SelectedParts.Any(changed),()=>Edit(reset),name);
          Add(_mixedNumbers[field].MixedEditor,()=>SelectedParts.Any(changed),()=>Edit(reset),name);
        }
        PartNumber(_copies,row=>row.Copies!=DefaultCopies,row=>row.Copies=DefaultCopies,"copies");
        PartNumber(_angle,row=>row.ManualRotation||row.Rotation!=DefaultRotation,row=> { row.Rotation=DefaultRotation; row.ManualRotation=false; },"automatic angle");
        PartNumber(_limit,row=>row.RotationStepOverride.HasValue,row=> { row.RotationStepOverride=null; if(row.RotationOverride==RotationMode.Custom) row.RotationOverride=null; },"global step");
        PartNumber(_partDeviation,row=>row.GrainDeviationOverride.HasValue,row=>row.GrainDeviationOverride=null,"global deviation");
        PartNumber(_guideAngle,row=>row.Guide!=row.DetectedGuide,row=>row.Guide=row.DetectedGuide,"detected grain");
        Add(_partGrain,()=>SelectedParts.Any(row=>row.GrainOverride.HasValue),()=>Edit(row=>row.GrainOverride=null),"global grain mode");
        Add(_partRotation,()=>SelectedParts.Any(row=>row.RotationOverride.HasValue||row.AllowReverseOverride.HasValue),
          ()=>Edit(row=> { row.RotationOverride=null; row.AllowReverseOverride=null; }),"global rotation");
        Shown+=(_,_)=> { foreach(var overlay in _resets) overlay.Refresh(); };
      }
    }
  }
}

namespace vTools.Commands
{
  using System.Globalization;
  using Eto.Forms;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  internal static partial class NestWorkflow
  {
    internal static double TableLabelTargetHeight(double width)=>width*TableLabelHeightFraction;
    internal static double OccupiedLength(NestPackingEngine.Result result,int table)=>result.Placements.Where(part=>part.Table==table)
      .Select(part=>part.Y+part.Pose.Length).DefaultIfEmpty(0).Max();
    internal static string[] TableLabelRows(string name,double width,double occupied,double yardsPerUnit)=>
      [name,width.ToString(TableWidthFormat,CultureInfo.InvariantCulture)+"x"+occupied.ToString(TableLengthFormat,CultureInfo.InvariantCulture),
        double.IsFinite(yardsPerUnit)&&yardsPerUnit>0?(occupied*yardsPerUnit).ToString(TableLengthFormat,CultureInfo.InvariantCulture)+"yds":"n/a yds"];

    internal sealed partial class NestWindow
    {
      private readonly Dictionary<int,string> _tableNames=[];
      private readonly GridView _tableGrid=new() {AllowMultipleSelection=false,RowHeight=GridRowHeight};
      private readonly TabControl _lists=new();
      private bool _tableEditing;
      private sealed record TableLabels(string Key,Point3d Origin,TextEntity[] Text,Curve Frame,Curve Outline,double FrameWidth,double Clearance);
      private readonly Dictionary<int,TableLabels> _tableLabels=[];
      private string TableDisplayName(int index)=>_tableNames.GetValueOrDefault(index,DefaultTableNamePrefix+(index+1));
      private Control CreateListsView(Control editor,Control actions,Control caption)
      {
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Name",DataCell=new TextBoxCell(0),Editable=true,Width=PartNameWidth,Expand=true,HeaderToolTip="Editable table name"});
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Width x used length",DataCell=new TextBoxCell(1),Width=FieldWidth});
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Used material",DataCell=new TextBoxCell(2),Width=FieldWidth});
        _tableGrid.CellEditing+=(_,_)=>_tableEditing=true;
        _tableGrid.CellEdited+=(_,e)=>
        {
          _tableEditing=false;
          if(e.Item is GridItem {Tag:int table} item) RenameTable(table,Convert.ToString(item.Values[0])??string.Empty);
        };
        _tableGrid.KeyDown+=(_,e)=> { if(e.Key==Keys.Escape) { _tableEditing=false; Application.Instance.AsyncInvoke(RefreshTableRows); } };
        _tableGrid.SelectionChanged+=(_,_)=> { if(!_updating) _doc.Views.Redraw(); };
        _lists.Pages.Add(new TabPage {Text="Parts",Content=_grid});
        _lists.Pages.Add(new TabPage {Text="Tables",Content=_tableGrid});
        _lists.SelectedIndexChanged+=(_,_)=>
        { bool parts=_lists.SelectedIndex==0; editor.Visible=actions.Visible=caption.Visible=parts; _doc.Views.Redraw(); };
        return _lists;
      }
      private void RenameTable(int table,string value)
      {
        var name=System.Text.RegularExpressions.Regex.Replace(value,@"\s+"," ").Trim();
        if(name.Length==0) name=DefaultTableNamePrefix+(table+1);
        _tableNames[table]=name[..Math.Min(name.Length,TableNameLimit)];
        RefreshTableRows(); _doc.Views.Redraw();
      }
      private double YardsPerUnit()=>_doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits
        ?double.NaN:RhinoMath.UnitScale(_doc.ModelUnitSystem,UnitSystem.Yards);
      private void RefreshTableRows()
      {
        if(_tableEditing||_closed) return;
        int selected=_tableGrid.SelectedRow; bool before=_updating; _updating=true;
        try
        {
          _tableGrid.DataStore=Enumerable.Range(0,VisibleResult.Tables).Select(table=>
          {
            var rows=TableLabelRows(TableDisplayName(table),NestWorkflow.Width,OccupiedLength(VisibleResult,table),YardsPerUnit());
            return new GridItem {Tag=table,Values=rows.Cast<object>().ToArray()};
          }).ToList();
          if(selected>=0&&selected<VisibleResult.Tables) _tableGrid.SelectedRow=selected;
        }
        finally { _updating=before; }
      }
      private Vector3d DisplayOffset(int table)
      {
        if(Stack==TableStack.Vertical) return TableOffset(_plane,table);
        double offset=0;
        for(int index=1;index<=table;index++)
        { var label=LabelGeometry(index); offset+=Length+TableGap+label.FrameWidth+label.Clearance; }
        return _plane.XAxis*offset;
      }
      private Point3d DisplayAnchor(int table)=>_anchor+DisplayOffset(table)-TableOffset(_plane,table);
      private double InitialLabelExtent()=>LabelGeometry(0).FrameWidth+LabelGeometry(0).Clearance;
      private TextEntity[] LabelsForTable(int table)
      {
        var entry=LabelGeometry(table); var origin=_anchor+DisplayOffset(table);
        if(entry.Origin!=origin)
        {
          var move=Transform.Translation(origin-entry.Origin);
          foreach(var text in entry.Text) text.Transform(move);
          entry.Frame.Transform(move); entry.Outline.Transform(move); _tableLabels[table]=entry with {Origin=origin};
        }
        return entry.Text;
      }
      private Curve LabelFrameForTable(int table)
      { LabelsForTable(table); return _tableLabels[table].Frame; }
      private Curve TableOutlineForTable(int table)
      { LabelsForTable(table); return _tableLabels[table].Outline; }
      private TableLabels LabelGeometry(int table)
      {
        double used=VisibleResult.Tables==0?Length:OccupiedLength(VisibleResult,table);
        var rows=TableLabelRows(TableDisplayName(table),NestWorkflow.Width,used,YardsPerUnit());
        var style=_doc.DimStyles.Current;
        string key=string.Join("\n",rows)+FormattableString.Invariant($"|{NestWorkflow.Width:R}|{Length:R}|{_doc.ModelSpaceTextScale:R}|{style.Id}|{style.DimensionScale:R}|{style.Font.FaceName}|{style.Font.Bold}|{style.Font.Italic}");
        if(_tableLabels.TryGetValue(table,out var previous)&&previous.Key==key)
          return previous;
        if(previous!=null) { foreach(var text in previous.Text) text.Dispose(); previous.Frame.Dispose(); previous.Outline.Dispose(); }
        double targetHeight=TableLabelTargetHeight(NestWorkflow.Width);
        var texts=rows.Select(row=>new TextEntity {PlainText=row,Plane=new Plane(Point3d.Origin,_plane.XAxis,_plane.YAxis),
          ParentDimensionStyle=style,DimensionStyleId=style.Id,TextHeight=targetHeight,DimensionScale=TableLabelScale,Justification=TextJustification.BottomLeft,
          TextOrientation=TextOrientation.InPlane,DrawForward=false}).ToArray();
        try
        {
          void Height(TextEntity text,double height)
          {
            if(!AnnotationTextTransform.ApplyFixedDisplayTextHeight(_doc,text,_doc.Views.ActiveView?.ActiveViewport,height,TableLabelScale))
              throw new InvalidOperationException("Could not set table label display height.");
            text.TextHorizontalAlignment=TextHorizontalAlignment.Left;
            text.TextVerticalAlignment=TextVerticalAlignment.Bottom;
          }
          foreach(var text in texts) Height(text,targetHeight);
          var frame=texts[0].Plane;
          var bounds=texts.Select(text=>LabelGlyphBounds(text,frame)).ToArray();
          // Rhino's nominal text height differs from actual glyph height for most fonts.
          double glyphHeight=bounds.Max(box=>box.Diagonal.Y);
          if(glyphHeight<=RhinoMath.ZeroTolerance) throw new InvalidOperationException("Could not size table label glyphs.");
          double nominalHeight=targetHeight*targetHeight/glyphHeight;
          foreach(var text in texts) Height(text,nominalHeight);
          bounds=texts.Select(text=>LabelGlyphBounds(text,frame)).ToArray();
          double maxWidth=bounds.Max(box=>box.Diagonal.X);
          if(maxWidth>Length)
          {
            foreach(var text in texts) Height(text,nominalHeight*Length/maxWidth);
            bounds=texts.Select(text=>LabelGlyphBounds(text,frame)).ToArray();
          }
          double rowGap=targetHeight*TableLabelRowGapFraction;
          double padding=targetHeight*TableLabelBoxPaddingFraction;
          double clearance=targetHeight*TableLabelGapFraction;
          maxWidth=bounds.Max(box=>box.Diagonal.X);
          double blockHeight=bounds.Sum(box=>box.Diagonal.Y)+(texts.Length-1)*rowGap;
          double left=-clearance-padding-maxWidth;
          double bottom=(NestWorkflow.Width-blockHeight)/2;
          double frameBottom=bottom-padding;
          for(int index=texts.Length-1;index>=0;index--)
          {
            var box=bounds[index];
            texts[index].Transform(Transform.Translation(_plane.XAxis*((left-padding)/2-box.Center.X)+_plane.YAxis*(bottom-box.Min.Y)));
            bottom+=box.Diagonal.Y+rowGap;
          }
          double frameWidth=maxWidth+2*padding;
          var labelFrame=new Rectangle3d(new Plane(Point3d.Origin,_plane.XAxis,_plane.YAxis),
            new Interval(left-padding,0),new Interval(frameBottom,frameBottom+blockHeight+2*padding)).ToNurbsCurve();
          double frameTop=frameBottom+blockHeight+2*padding;
          var outline=new PolylineCurve(new[] {frame.PointAt(0,0),frame.PointAt(Length,0),frame.PointAt(Length,NestWorkflow.Width),
            frame.PointAt(0,NestWorkflow.Width),frame.PointAt(0,frameTop),frame.PointAt(left-padding,frameTop),
            frame.PointAt(left-padding,frameBottom),frame.PointAt(0,frameBottom),frame.PointAt(0,0)});
          var result=new TableLabels(key,Point3d.Origin,texts,labelFrame,outline,frameWidth,clearance);
          Log.Write("vNest","table label table={0} glyphHeight={1:G17} frameWidth={2:G17} frameHeight={3:G17} side=left-middle",
            table+1,bounds.Max(box=>box.Diagonal.Y),frameWidth,blockHeight+2*padding);
          _tableLabels[table]=result; return result;
        }
        catch { foreach(var text in texts) text.Dispose(); _tableLabels.Remove(table); throw; }
      }
      private BoundingBox LabelGlyphBounds(TextEntity text,Plane frame)
      {
        using var measurement=(TextEntity)text.Duplicate();
        var parent=_doc.DimStyles.FindId(text.DimensionStyleId)??_doc.DimStyles.Current;
        measurement.ParentDimensionStyle=parent;
        measurement.DimensionScale=AnnotationTextTransform.ResolveDisplayDimensionScale(_doc,text,_doc.Views.ActiveView?.ActiveViewport);
        using var style=measurement.GetDimensionStyle(parent);
        var curves=measurement.CreateCurves(style??parent,true,TableLabelScale,0);
        var bounds=BoundingBox.Empty;
        foreach(var curve in curves??[]){try {bounds.Union(curve.GetBoundingBox(frame));} finally {curve.Dispose();}}
        if(!bounds.IsValid) bounds=text.GetBoundingBox(frame);
        if(!bounds.IsValid) throw new InvalidOperationException("Could not measure table label glyphs.");
        return bounds;
      }
      private void ClearTableLabels()
      { foreach(var entry in _tableLabels.Values) { foreach(var text in entry.Text) text.Dispose(); entry.Frame.Dispose(); entry.Outline.Dispose(); } _tableLabels.Clear(); }
      private string UniqueTableGroupName(string requested)
      {
        var names=Enumerable.Range(0,_doc.Groups.Count).Where(index=>_doc.Groups[index] is {IsDeleted:false})
          .Select(_doc.Groups.GroupName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if(!names.Contains(requested)) return requested;
        for(int suffix=2;suffix<TableNameAttempts;suffix++) { string name=requested+"_"+suffix; if(!names.Contains(name)) return name; }
        throw new InvalidOperationException("Could not create a unique table group name.");
      }
    }
  }
}

namespace vTools.Commands
{
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using Rhino.Geometry.Intersect;
  using Rhino.Input.Custom;
  using Rhino.UI;


  internal sealed class NestDocumentPicking : MouseCallback,IDisposable
  {
    // Document-picking defaults
    private const string ReferenceLayer = "Reference"; // Leaf layer excluded when identifying a group's part perimeter rather than its label frame.
    private const int DragThreshold = 4; // Screen-pixel movement distinguishing a blank click from native window/crossing selection.

    private readonly RhinoDoc _doc;
    private readonly Func<bool> _canPick;
    private readonly Action _changed;
    private readonly Func<Guid,Guid[]?> _knownMembers;
    private readonly Func<Line,Guid[]?>? _knownInterior;
    private Guid[]? _blankSelection;
    private System.Drawing.Point _blankPoint;
    private Guid _blankViewport;
    private bool _dragged,_completingClick;
    private Guid[]? _interiorMembers;
    private bool _interiorSelect;
    private bool _boundariesDirty=true;
    private readonly List<(Curve Curve,Plane Plane,double Area,Guid Source,Guid[] Members)> _boundaries=[];

    internal bool DefersSelectionSync=>_blankSelection!=null;
    internal bool DefersRedraw=>_completingClick;
    internal Func<IEnumerable<Guid>>? RetainedSelection { get; set; }
    internal event Action<Guid[],bool>? PartClicked;
    internal static bool IsDrag(System.Drawing.Point start,System.Drawing.Point current)=>
      Math.Abs((long)current.X-start.X)>DragThreshold||Math.Abs((long)current.Y-start.Y)>DragThreshold;
    internal static bool RetainPriorSelection(bool dragged,bool selecting)=>!dragged||selecting;

    internal NestDocumentPicking(RhinoDoc doc,Func<bool> canPick,Action changed,Func<Guid,Guid[]?> knownMembers)
      :this(doc,canPick,changed,knownMembers,null) { }

    internal NestDocumentPicking(RhinoDoc doc,Func<bool> canPick,Action changed,Func<Guid,Guid[]?> knownMembers,Func<Line,Guid[]?>? knownInterior)
    {
      _doc=doc; _canPick=canPick; _changed=changed; _knownMembers=knownMembers; _knownInterior=knownInterior; Enabled=true;
      RhinoDoc.AddRhinoObject+=ObjectChanged; RhinoDoc.DeleteRhinoObject+=ObjectChanged; RhinoDoc.UndeleteRhinoObject+=ObjectChanged;
      RhinoDoc.ReplaceRhinoObject+=ObjectReplaced; RhinoDoc.ModifyObjectAttributes+=AttributesChanged;
    }

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
      if(e.MouseButton!=MouseButton.Left||!_canPick()||e.View?.ActiveViewport is not {} viewport)
      { base.OnMouseDown(e); return; }
      try
      {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        if(!viewport.GetFrustumLine(e.ViewportPoint.X,e.ViewportPoint.Y,out var line)) return;
        using var context=new PickContext {View=e.View,PickLine=line,PickStyle=PickStyle.PointPick,PickMode=PickMode.Shaded,PickGroupsEnabled=false};
        context.SetPickTransform(viewport.GetPickTransform(e.ViewportPoint)); context.UpdateClippingPlanes();
        var references=_doc.Objects.PickObjects(context);
        RhinoObject? picked=null;
        try
        { picked=references?.Select(reference=>reference.Object()).FirstOrDefault(obj=>obj?.Geometry is Rhino.Geometry.Curve or Rhino.Geometry.AnnotationBase or Rhino.Geometry.Point or Rhino.Geometry.TextDot); }
        finally { if(references!=null) foreach(var reference in references) reference.Dispose(); }
        _blankSelection=_doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id)
          .Concat(RetainedSelection?.Invoke()??[]).Distinct().ToArray();
        _blankPoint=e.ViewportPoint; _blankViewport=viewport.Id; _dragged=false;
        _interiorMembers=picked==null?InteriorMembers(line):_knownMembers(picked.Id)??FindMembers(picked);
        _interiorSelect=!e.CtrlKeyDown;
        Log.Write("vNest","part down members={0} ctrl={1} inGet={2} elapsedMs={3:F1}",_interiorMembers?.Length??0,e.CtrlKeyDown,Rhino.Input.RhinoGet.InGet(_doc),clock.Elapsed.TotalMilliseconds);
      }
      catch(Exception ex) { Log.Write("vNest","document pick failed: {0}",ex); }
      finally { base.OnMouseDown(e); }
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      if(_blankSelection!=null) _dragged|=IsDrag(_blankPoint,e.ViewportPoint);
      base.OnMouseMove(e);
    }

    protected override void OnEndMouseUp(MouseCallbackEventArgs e)
    {
      if(_blankSelection is {} saved)
      {
        _blankSelection=null;
        try
        {
          // The click was authorized on mouse-down; native selection may still be unwinding its input state here.
          bool allowed=Enabled&&RhinoDoc.ActiveDoc==_doc&&e.View?.ActiveViewport.Id==_blankViewport;
          Log.Write("vNest","interior up allowed={0} dragged={1} members={2} button={3} inGet={4}",allowed,_dragged,_interiorMembers?.Length??0,e.MouseButton,Rhino.Input.RhinoGet.InGet(_doc));
          if(allowed)
          {
            var clock=System.Diagnostics.Stopwatch.StartNew(); _completingClick=true;
            bool dragged=_dragged||IsDrag(_blankPoint,e.ViewportPoint);
            if(RetainPriorSelection(dragged,_interiorSelect))
              foreach(var id in saved) if(_doc.Objects.FindId(id) is {} obj&&obj.IsSelected(false)==0) obj.Select(true);
            if(!dragged)
            {
              if(_interiorMembers!=null) foreach(var id in _interiorMembers) _doc.Objects.FindId(id)?.Select(_interiorSelect);
            }
            _changed();
            if(!_dragged&&!IsDrag(_blankPoint,e.ViewportPoint)&&_interiorMembers!=null) PartClicked?.Invoke(_interiorMembers,_interiorSelect);
            _doc.Views.Redraw();
            Log.Write("vNest","part click completed elapsedMs={0:F1} members={1}",clock.Elapsed.TotalMilliseconds,_interiorMembers?.Length??0);
          }
        }
        catch(Exception ex) { Log.Write("vNest","blank-click selection restore failed: {0}",ex); }
        finally { _interiorMembers=null; _completingClick=false; }
      }
      base.OnEndMouseUp(e);
    }

    private Guid[] FindMembers(RhinoObject obj)
    {
      var groups=(obj.Attributes.GetGroupList()??[]).OrderBy(group=>_doc.Groups.GroupObjectCount(group));
      foreach(var group in groups)
      {
        var members=_doc.Groups.GroupMembers(group)??[];
        if(members.Any(member=>member.Geometry is Rhino.Geometry.Curve {IsClosed:true}&&
          !string.Equals(_doc.Layers[member.Attributes.LayerIndex].FullPath.Split("::").Last(),ReferenceLayer,StringComparison.OrdinalIgnoreCase)))
          return members.Select(member=>member.Id).ToArray();
      }
      var first=groups.FirstOrDefault(-1);
      return first>=0?(_doc.Groups.GroupMembers(first)??[]).Select(member=>member.Id).ToArray():[obj.Id];
    }

    internal static bool Inside(Curve curve,Plane plane,Line ray,double tolerance,out double parameter)
    {
      parameter=0;
      return Intersection.LinePlane(ray,plane,out parameter)&&parameter>=0&&parameter<=1&&
        curve.Contains(ray.PointAt(parameter),plane,tolerance) is PointContainment.Inside or PointContainment.Coincident;
    }

    private Guid[]? InteriorMembers(Line ray)
    {
      if(_knownInterior?.Invoke(ray) is {} known) return known;
      if(_boundaries.Any(boundary=>_doc.Objects.FindId(boundary.Source) is not {IsDeleted:false})) _boundariesDirty=true;
      if(_boundariesDirty) RebuildBoundaries();
      return _boundaries.Where(boundary=>Inside(boundary.Curve,boundary.Plane,ray,_doc.ModelAbsoluteTolerance,out _))
        .OrderBy(boundary=>boundary.Area).Select(boundary=>boundary.Members).FirstOrDefault();
    }

    private void RebuildBoundaries()
    {
      foreach(var boundary in _boundaries) boundary.Curve.Dispose(); _boundaries.Clear();
      var settings=new ObjectEnumeratorSettings {ObjectTypeFilter=ObjectType.Curve,NormalObjects=true,LockedObjects=false,HiddenObjects=false,ReferenceObjects=false};
      var objects=_doc.Objects.GetObjectList(settings).Where(obj=>obj.Geometry is Curve&&
        !string.Equals(_doc.Layers[obj.Attributes.LayerIndex].FullPath.Split("::").Last(),ReferenceLayer,StringComparison.OrdinalIgnoreCase)).ToArray();
      foreach(var bucket in objects.GroupBy(obj=>(obj.Attributes.GetGroupList()??[]).OrderBy(group=>_doc.Groups.GroupObjectCount(group)).FirstOrDefault(-1)))
      {
        var curves=bucket.Select(obj=>(Curve)obj.Geometry).ToArray();
        var closed=curves.Where(curve=>curve.IsClosed).ToArray();
        var loops=closed.Length>0?closed.Select(curve=>curve.DuplicateCurve()).ToArray():GroupBoundaryTopology.PlanarRegions(curves,_doc.ModelAbsoluteTolerance,_doc.ModelAbsoluteTolerance).ToArray();
        foreach(var curve in loops)
        {
          if(!curve.IsClosed||!curve.TryGetPlane(out var plane,_doc.ModelAbsoluteTolerance)) { curve.Dispose(); continue; }
          using var mass=AreaMassProperties.Compute(curve);
          if(mass==null) { curve.Dispose(); continue; }
          var representative=bucket.FirstOrDefault(obj=>PlanarPartSelection.Coincident((Curve)obj.Geometry,curve,_doc.ModelAbsoluteTolerance));
          if(representative==null) { curve.Dispose(); continue; }
          var members=_knownMembers(representative.Id)??(bucket.Key>=0?FindMembers(representative):bucket
            .Where(obj=>curve.Contains(PlanarPartSelection.RepresentativePoint(obj.Geometry),plane,_doc.ModelAbsoluteTolerance) is PointContainment.Inside or PointContainment.Coincident)
            .Select(obj=>obj.Id).ToArray());
          _boundaries.Add((curve,plane,mass.Area,representative.Id,members));
        }
      }
      _boundariesDirty=false;
    }

    private void ObjectChanged(object? sender,RhinoObjectEventArgs e)
    { if(e.TheObject.Document?.RuntimeSerialNumber==_doc.RuntimeSerialNumber||_boundaries.Any(boundary=>boundary.Members.Contains(e.ObjectId))) _boundariesDirty=true; }
    private void ObjectReplaced(object? sender,RhinoReplaceObjectEventArgs e) { if(e.Document.RuntimeSerialNumber==_doc.RuntimeSerialNumber) _boundariesDirty=true; }
    private void AttributesChanged(object? sender,RhinoModifyObjectAttributesEventArgs e) { if(e.Document.RuntimeSerialNumber==_doc.RuntimeSerialNumber) _boundariesDirty=true; }

    public void Dispose()
    {
      _blankSelection=null; _interiorMembers=null; RetainedSelection=null; PartClicked=null; Enabled=false;
      RhinoDoc.AddRhinoObject-=ObjectChanged; RhinoDoc.DeleteRhinoObject-=ObjectChanged; RhinoDoc.UndeleteRhinoObject-=ObjectChanged;
      RhinoDoc.ReplaceRhinoObject-=ObjectReplaced; RhinoDoc.ModifyObjectAttributes-=AttributesChanged;
      foreach(var boundary in _boundaries) boundary.Curve.Dispose(); _boundaries.Clear();
    }
  }
}

namespace vTools
{
  using System.Globalization;
  using Eto.Drawing;
  using Eto.Forms;


  internal sealed class NestMixedNumber : Panel
  {
    // Mixed numeric editor defaults
    private const string MixedText = "<varies>"; // Placeholder for unequal selected numeric values; never submitted as a number.
    private const int MinimumHeight = 24; // Minimum editor height in device-independent pixels, preserving row alignment between mixed/common states.
    private readonly NumericStepper _number;
    private readonly TextBox _mixed=new() {PlaceholderText=MixedText,Visible=false};
    internal event Action<double>? Committed;
    internal bool IsMixed { get; private set; }
    internal TextBox MixedEditor=>_mixed;
    internal NestMixedNumber(NumericStepper number)
    {
      _number=number; Width=number.Width; MinimumSize=new Size(0,MinimumHeight);
      Content=new TableLayout {Spacing=Size.Empty,Rows={new TableRow(number),new TableRow(_mixed)}};
      _mixed.LostFocus+=(_,_)=>Commit();
      _mixed.KeyDown+=(_,e)=> { if(e.Key==Keys.Enter) { Commit(); e.Handled=true; } };
    }
    internal void SetMixed(bool mixed)
    {
      IsMixed=mixed; _number.Visible=!mixed; _mixed.Visible=mixed;
      if(mixed&&!_mixed.HasFocus) _mixed.Text=string.Empty;
    }
    private void Commit()
    {
      if(!IsMixed||string.IsNullOrWhiteSpace(_mixed.Text)) return;
      if(!double.TryParse(_mixed.Text,NumberStyles.Float,CultureInfo.CurrentCulture,out var value)||!double.IsFinite(value)||
        value<_number.MinValue||value>_number.MaxValue||_number.DecimalPlaces==0&&value!=Math.Truncate(value)) { _mixed.Text=string.Empty; return; }
      _mixed.Text=string.Empty; Committed?.Invoke(value);
    }
  }
}

namespace vTools
{
  using System.Runtime.CompilerServices;
  using RectpackSharp;


  internal static class NestPackingEngine
  {
    // Packing defaults and numeric safety limits
    private const double MaximumScale = 1000.0; // Maximum integer grid cells per model unit; positive value.
    private const double AreaSafetyFactor = 0.25; // Fraction of uint capacity reserved for one table's grid area, greater than zero through one.
    private const double QuantizationEpsilon = 1e-8; // Integer-cell roundoff allowance; much smaller than one cell.
    private const uint GridDivisions = 60; // Grid alignment divisible by 2, 3, 4, 5 and 6 so common exact-width fractions remain packable.
    private const double ComparisonTolerance = 1e-9; // Model-unit tolerance for touching rectangles and table limits; positive value.
    internal const int MaximumTables = 128; // Maximum number of nest tables created automatically; positive integer.
    private const double PackingDensity = 1.0; // Desired used-area density, zero through one; one searches all enabled packing hints.
    private const uint PackingStep = 1; // Integer grid cells between constrained packing trials; one or greater.
    private const int OrderTrials = 6; // Number of deterministic batch orders compared; positive integer.
    private const int RotationSearchParts = 8; // Maximum parts in one joint rotation search; positive integer.
    private const int RotationSearchTrials = 128; // Maximum joint pose combinations examined per addition; positive integer.
    private const int LengthRefinementSteps = 8; // Maximum binary-search trials for reducing used material length; zero disables refinement.
    private const double SearchSeconds = 1.5; // Soft seconds budget for optional order/rotation refinement; the baseline always completes.
    private const bool DefaultFlipped = false; // true starts a pose mirrored; false preserves its handedness unless explicitly requested.
    private const bool DefaultTrial = false; // true marks a transient layout under evaluation; false marks an accepted or initial layout.
    private const bool DefaultStopped = false; // true reports a user-stopped search; false reports normal completion.
    private const bool DefaultGrainGuided = false; // true identifies an enabled detected/user-specified grain constraint; false receives no skinny-grain priority.
    internal const double DefaultGap = 0.0; // Minimum outline-to-outline spacing in model units; nonnegative, zero permits touching.
    private const int DefaultHalfTurnTests = 0; // Initial count of explicit allowed 180-degree partner trials; nonnegative.
    private const double DefaultConsumedLength = 0.0; // Initial total roll consumption in model units; includes earlier table tails.

    internal readonly record struct ContourPoint(double X,double Y);
    internal sealed record Pose(double Width,double Length,double Rotation)
    {
      internal bool Flipped { get; init; }=DefaultFlipped;
      internal IReadOnlyList<ContourPoint>? Contour { get; init; }
    }
    internal sealed record Input(int Id,IReadOnlyList<Pose> Poses,bool Fixed=false,int Table=0,double X=0,double Y=0)
    {
      internal bool GrainGuided { get; init; }=DefaultGrainGuided;
      internal double Gap { get; init; }=DefaultGap;
    }
    internal sealed record Placement(int Id,int Table,double X,double Y,Pose Pose);
    internal sealed record SearchStatistics(int PlacementTests,int Layouts,int Improvements,double InitialSeconds,double SearchSeconds,bool BudgetExpired)
    {
      internal int HalfTurnTests { get; init; }=DefaultHalfTurnTests;
      internal double ConsumedLength { get; init; }=DefaultConsumedLength;
    }
    internal sealed record ProfileStatistics(int PairHits,int PairMisses,double PairSeconds,int AnchorCalls,double AnchorSeconds,
      int CollisionCalls,double CollisionSeconds,int PosesPruned,int CacheEntries,long CacheVertices);
    internal sealed record Result(List<Placement> Placements,int Tables,Dictionary<int,string> Errors)
    {
      internal bool IsTrial { get; init; }=DefaultTrial;
      internal bool Stopped { get; init; }=DefaultStopped;
      internal SearchStatistics? Search { get; init; }
      internal ProfileStatistics? Profile { get; init; }
    }
    private readonly record struct Space(double X,double Y,double Width,double Length);

    internal static Result Solve(IReadOnlyList<Input> inputs,double width,double length)
    {
      NestPackingDependency.EnsureLoaded();
      return SolveLoaded(inputs,width,length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result SolveLoaded(IReadOnlyList<Input> inputs,double width,double length)
    {
      var watch=System.Diagnostics.Stopwatch.StartNew();
      Result? best=null;
      for(int order=0;order<OrderTrials;order++)
      {
        if(order>0&&watch.Elapsed.TotalSeconds>=SearchSeconds) break;
        var result=SolveTrial(inputs,width,length,order,watch);
        if(best==null||Better(result,best)) best=result;
      }
      return best!;
    }

    private static bool Better(Result candidate,Result previous)
    {
      if(candidate.Placements.Count!=previous.Placements.Count) return candidate.Placements.Count>previous.Placements.Count;
      if(candidate.Tables!=previous.Tables) return candidate.Tables<previous.Tables;
      double UsedLength(Result result)=>result.Placements.GroupBy(part=>part.Table).Sum(table=>table.Max(part=>part.Y+part.Pose.Length));
      return UsedLength(candidate)<UsedLength(previous)-ComparisonTolerance;
    }

    private static IEnumerable<Input> Ordered(IEnumerable<Input> inputs,int order)
    {
      double Key(Input input)=>order switch {
        1=>input.Poses.Min(pose=>Math.Max(pose.Width,pose.Length)),
        2=>input.Poses.Min(pose=>pose.Width),
        3=>input.Poses.Min(pose=>pose.Length),
        4=>input.Poses.Min(pose=>Math.Min(pose.Width,pose.Length)),
        _=>input.Poses.Min(pose=>pose.Width*pose.Length)};
      string Shape(Input input)=>string.Join(";",input.Poses.OrderBy(pose=>pose.Width).ThenBy(pose=>pose.Length)
        .Select(pose=>FormattableString.Invariant($"{pose.Width:R},{pose.Length:R},{pose.Rotation:R}")));
      var sorted=order==OrderTrials-1?inputs.OrderBy(Key):inputs.OrderByDescending(Key);
      return sorted.ThenBy(Shape,StringComparer.Ordinal).ThenBy(input=>input.Id);
    }

    private static Result SolveTrial(IReadOnlyList<Input> inputs,double width,double length,int order,System.Diagnostics.Stopwatch watch)
    {
      double refinementDeadline=Math.Min(SearchSeconds,watch.Elapsed.TotalSeconds+SearchSeconds/(2*OrderTrials));
      var errors=new Dictionary<int,string>();
      var placements=new List<Placement>();
      if(!double.IsFinite(width)||!double.IsFinite(length)||width<=0||length<=0)
        return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Invalid table size"));
      var valid=inputs.Where(input=>input.Poses.Count>0).ToList();
      foreach(var input in inputs.Where(input=>input.Poses.Count==0)) errors[input.Id]="No allowed rotation";
      int tables=0;
      foreach(var input in valid.Where(input=>input.Fixed).OrderBy(input=>input.Table).ThenBy(input=>input.X).ThenBy(input=>input.Y).ThenBy(input=>input.Id))
      {
        var pose=input.Poses[0];
        var box=new Space(input.X,input.Y,pose.Width,pose.Length);
        if(input.Table<0||input.Table>=MaximumTables||!Inside(box,width,length))
        { errors[input.Id]="Fixed part outside table"; continue; }
        if(placements.Any(other=>other.Table==input.Table&&Overlap(box,new Space(other.X,other.Y,other.Pose.Width,other.Pose.Length))))
        { errors[input.Id]="Fixed parts overlap"; continue; }
        placements.Add(new Placement(input.Id,input.Table,input.X,input.Y,pose));
        tables=Math.Max(tables,input.Table+1);
      }
      var remaining=Ordered(valid.Where(input=>!input.Fixed),order).ToList();
      foreach(var input in remaining.ToArray())
        if(!input.Poses.Any(pose=>pose.Width<=width+ComparisonTolerance&&pose.Length<=length+ComparisonTolerance))
        { errors[input.Id]="Part exceeds table"; remaining.Remove(input); }
      var scale=Math.Min(MaximumScale,Math.Sqrt(uint.MaxValue*AreaSafetyFactor/(width*length)));
      if(!double.IsFinite(scale)||scale<=0) return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Table dimensions exceed packing limits"));
      for(int table=0;table<MaximumTables&&(remaining.Count>0||table<tables);table++)
      {
        var spaces=new List<Space>{new(0,0,width,length)};
        foreach(var fixedPart in placements.Where(part=>part.Table==table))
          spaces=spaces.SelectMany(space=>Subtract(space,new Space(fixedPart.X,fixedPart.Y,fixedPart.Pose.Width,fixedPart.Pose.Length))).ToList();
        foreach(var space in spaces.OrderByDescending(space=>space.Width*space.Length))
        {
          var chosen=new List<(int Id,Pose Pose)>();
          List<(int Id,double X,double Y)> best=[];
          foreach(var input in remaining.ToArray())
          {
            List<(int Id,Pose Pose)>? accepted=null;
            List<(int Id,double X,double Y)> acceptedPack=[];
            double bestScore=double.PositiveInfinity;
            void Consider(IReadOnlyList<(int Id,Pose Pose)> candidate)
            {
              if(!TryPack(candidate,space.Width,space.Length,scale,watch,refinementDeadline,out var packed,out double score)||score>=bestScore) return;
              accepted=candidate.ToList(); acceptedPack=packed; bestScore=score;
            }
            foreach(var pose in input.Poses.Where(pose=>pose.Width<=space.Width+ComparisonTolerance&&pose.Length<=space.Length+ComparisonTolerance))
              Consider(chosen.Append((input.Id,pose)).ToArray());
            // Revisit earlier rotations jointly, rather than locking them to selection order.
            if(chosen.Count>0&&watch.Elapsed.TotalSeconds<refinementDeadline)
            {
              for(int previous=0;previous<chosen.Count&&watch.Elapsed.TotalSeconds<refinementDeadline;previous++)
                foreach(var oldPose in valid.First(part=>part.Id==chosen[previous].Id).Poses)
                  foreach(var newPose in input.Poses)
                  {
                    if(watch.Elapsed.TotalSeconds>=refinementDeadline) break;
                    if(oldPose.Width>space.Width||oldPose.Length>space.Length||newPose.Width>space.Width||newPose.Length>space.Length) continue;
                    var pair=(accepted??chosen.Append((input.Id,newPose)).ToList()).ToArray();
                    pair[previous]=(chosen[previous].Id,oldPose); pair[^1]=(input.Id,newPose); Consider(pair);
                  }
            }
            if(chosen.Count>0&&chosen.Count<RotationSearchParts&&watch.Elapsed.TotalSeconds<refinementDeadline)
            {
              var candidates=chosen.Select(item=>valid.First(part=>part.Id==item.Id)).Append(input).ToArray();
              var options=candidates.Select(part=>part.Poses.Where(pose=>pose.Width<=space.Width+ComparisonTolerance&&pose.Length<=space.Length+ComparisonTolerance)
                .OrderBy(pose=>pose.Width*pose.Length).ThenBy(pose=>Math.Abs(pose.Rotation)).ToArray()).ToArray();
              var trial=new (int Id,Pose Pose)[candidates.Length]; int attempts=0;
              void Search(int index,double area)
              {
                if(attempts>=RotationSearchTrials||watch.Elapsed.TotalSeconds>=refinementDeadline||area>space.Width*space.Length+ComparisonTolerance) return;
                if(index==trial.Length) { attempts++; Consider(trial); return; }
                foreach(var pose in options[index])
                { trial[index]=(candidates[index].Id,pose); Search(index+1,area+pose.Width*pose.Length); }
              }
              Search(0,0);
            }
            if(accepted==null) continue;
            chosen=accepted; best=acceptedPack; remaining.Remove(input);
          }
          foreach(var position in best)
          {
            var pose=chosen.First(item=>item.Id==position.Id).Pose;
            placements.Add(new Placement(position.Id,table,space.X+position.X,space.Y+position.Y,pose));
          }
        }
        if(placements.Any(part=>part.Table==table)) tables=Math.Max(tables,table+1);
      }
      foreach(var input in remaining) errors[input.Id]="Table limit reached";
      return new Result(placements,tables,errors);
    }

    private static bool TryPack(IReadOnlyList<(int Id,Pose Pose)> inputs,double width,double length,double scale,
      System.Diagnostics.Stopwatch watch,double deadline,
      out List<(int Id,double X,double Y)> positions,out double score)
    {
      positions=[]; score=double.PositiveInfinity;
      uint Cells(double value)
      {
        uint cells=(uint)Math.Floor(value*scale+QuantizationEpsilon);
        return cells>=GridDivisions?cells/GridDivisions*GridDivisions:cells;
      }
      uint maxWidth=Cells(width),maxLength=Cells(length);
      if(maxWidth==0||maxLength==0) return false;
      double scaleX=maxWidth/width,scaleY=maxLength/length;
      uint CellSize(double value,double axisScale)=>(uint)Math.Max(1,Math.Ceiling(value*axisScale-QuantizationEpsilon));
      var rectangles=inputs.Select(input=>new PackingRectangle(0,0,CellSize(input.Pose.Width,scaleX),CellSize(input.Pose.Length,scaleY),input.Id)).ToArray();
      ulong area=rectangles.Aggregate<PackingRectangle,ulong>(0,(sum,rectangle)=>sum+(ulong)rectangle.Width*rectangle.Height);
      if(area>(ulong)maxWidth*maxLength||rectangles.Any(rectangle=>rectangle.Width>maxWidth||rectangle.Height>maxLength)) return false;
      try
      {
        var original=(PackingRectangle[])rectangles.Clone();
        RectanglePacker.Pack(rectangles,out var bounds,PackingHints.FindBest,PackingDensity,PackingStep,maxWidth,maxLength);
        // A smaller bounding-box area is not necessarily a shorter roll length.
        uint lower=Math.Max(original.Max(rectangle=>rectangle.Height),(uint)((area+maxWidth-1)/maxWidth)),upper=bounds.Height;
        for(int step=0;step<LengthRefinementSteps&&lower<upper&&watch.Elapsed.TotalSeconds<deadline;step++)
        {
          uint limit=lower+(upper-lower)/2;
          var candidate=(PackingRectangle[])original.Clone();
          try
          {
            RectanglePacker.Pack(candidate,out var candidateBounds,PackingHints.FindBest,PackingDensity,PackingStep,maxWidth,limit);
            rectangles=candidate; bounds=candidateBounds; upper=bounds.Height;
          }
          catch(Exception) { lower=limit+1; }
        }
        positions=rectangles.Select(rectangle=>(rectangle.Id,rectangle.X/scaleX,rectangle.Y/scaleY)).ToList();
        score=bounds.Height/scaleY*width+bounds.Width/scaleX;
        return true;
      }
      catch(Exception) { return false; }
    }

    private static bool Inside(Space box,double width,double length) => double.IsFinite(box.X)&&double.IsFinite(box.Y)&&
      box.X>=-ComparisonTolerance&&box.Y>=-ComparisonTolerance&&box.Width>0&&box.Length>0&&
      box.X+box.Width<=width+ComparisonTolerance&&box.Y+box.Length<=length+ComparisonTolerance;
    private static bool Overlap(Space a,Space b) => a.X<b.X+b.Width-ComparisonTolerance&&a.X+a.Width>b.X+ComparisonTolerance&&
      a.Y<b.Y+b.Length-ComparisonTolerance&&a.Y+a.Length>b.Y+ComparisonTolerance;

    private static IEnumerable<Space> Subtract(Space free,Space occupied)
    {
      if(!Overlap(free,occupied)) { yield return free; yield break; }
      double left=Math.Max(free.X,occupied.X),right=Math.Min(free.X+free.Width,occupied.X+occupied.Width);
      double bottom=Math.Max(free.Y,occupied.Y),top=Math.Min(free.Y+free.Length,occupied.Y+occupied.Length);
      if(left>free.X+ComparisonTolerance) yield return new Space(free.X,free.Y,left-free.X,free.Length);
      if(right<free.X+free.Width-ComparisonTolerance) yield return new Space(right,free.Y,free.X+free.Width-right,free.Length);
      if(bottom>free.Y+ComparisonTolerance) yield return new Space(left,free.Y,right-left,bottom-free.Y);
      if(top<free.Y+free.Length-ComparisonTolerance) yield return new Space(left,top,right-left,free.Y+free.Length-top);
    }
  }
}

namespace vTools
{
  using System.Diagnostics;
  using System.Runtime.CompilerServices;
  using Clipper2Lib;
  using Input = vTools.NestPackingEngine.Input;
  using Pose = vTools.NestPackingEngine.Pose;
  using Placement = vTools.NestPackingEngine.Placement;
  using Result = vTools.NestPackingEngine.Result;


  internal static partial class NestContourPackingEngine
  {
    // Polygon packing defaults and numeric/search limits
    private const int Precision = 6; // Decimal places retained by polygon Boolean and Minkowski operations; zero through eight.
    private const double CoordinateTolerance = 1e-6; // Model-unit roundoff accepted at table edges and coincident contacts.
    private const double AreaTolerance = 1e-8; // Square-model-unit threshold distinguishing contact from polygon overlap.
    private const int OrderTrials = 6; // Deterministic area/extent orders compared, independent of selection order.
    internal const double DefaultSearchSeconds = 30.0; // Default refinement seconds after the initial layout; zero disables refinement.
    private const double BeamTimeFraction = 0.75; // Fraction of refinement time allocated to full-layout search before pair refinement; zero through one.
    private const int BeamWidth = 3; // Alternative partial layouts retained while jointly choosing part poses and contacts; positive integer.
    private const int BeamOrders = 3; // Full-layout orders using area, extent and concavity; positive integer.
    private const int BeamAnchors = 3; // Distinct contact placements retained per pose during full-layout search; positive integer.
    private const int PairTrials = 48; // Maximum concavity-prioritized part pairs reconsidered per solve; positive integer.
    private const int PairPoseLimit = 8; // First-part rotations/mirrors tested in each pair order; positive integer.
    private const int CandidateLimit = 2048; // Maximum feasible anchors tested per pose and table; positive integer.
    private const int ContactVertexLimit = 96; // Maximum vertices used for optional curved contact-path simplification; exact collision paths are retained.
    private const double ContactTolerance = 0.002; // Initial model-unit simplification/clearance tolerance for dense contact outlines.
    private const double PreviewInterval = 0.3; // Minimum seconds between provisional layout snapshots; limits UI geometry rebuilding.
    private const double MinimumSkinnyAspect = 3.0; // Minimum long/short fitting-pose aspect ratio for skinny grain-guided priority; at least one.
    private const double SkinnyTimeFraction = 0.2; // Maximum fraction of refinement seconds spent relocating skinny parts into existing gaps; zero through one.
    private const int SkinnyPasses = 2; // Skinny reinsertion orders attempted before general layout refinement; positive integer.
    private const int GapNeighborLimit = 2; // Distinct touching neighboring parts needed for the gap-filling score; two identifies placement between parts.
    private const bool DefaultSkinny = false; // true prioritizes fitting grain-guided strips for gap filling; false uses ordinary contour packing.
    private const double HalfTurnTimeFraction = 0.2; // Fraction of refinement seconds reserved for explicit 180-degree partner fitting; zero through one.
    private const int HalfTurnParts = 8; // Most concave movable parts considered by the dedicated half-turn pass; positive.
    private const int HalfTurnPartners = 6; // Partners considered per allowed half-turn, prioritizing removable used length; positive.
    private const double HalfTurnAngleTolerance = 0.05; // Degree tolerance for matching an already-allowed opposite pose; much less than 180.

    private sealed record Shape(int Id,Pose Pose,PathD Path,PathD Contact,double Area)
    {
      internal bool Skinny { get; init; }=DefaultSkinny;
      internal double Gap { get; init; }=NestPackingEngine.DefaultGap;
      internal PathsD? Clearance { get; init; }
      internal SolveMetrics? Metrics { get; init; }
      internal string GeometryKey { get; }=ContactKey(Contact);
    }
    private sealed class Placed(Shape shape,int table,double x,double y)
    {
      internal Shape Shape { get; }=shape;
      internal int Table { get; }=table;
      internal double X { get; }=x;
      internal double Y { get; }=y;
      private PathD? _path;
      private PathsD? _clearance;
      internal PathD WorldPath=>_path??=Move(Shape.Path,X,Y);
      internal PathsD WorldClearance=>_clearance??=Shape.Clearance is {} paths
        ?new PathsD(paths.Select(path=>Move(path,X,Y))):new PathsD {WorldPath};
    }
    private sealed record Plan(List<Placed> Parts,int Tables,Dictionary<int,string> Errors);
    private sealed class SearchState
    {
      internal int Tests,Layouts,Improvements,HalfTurns;
    }

    internal static Result Solve(IReadOnlyList<Input> inputs,double width,double length)
      =>SolveWithProgress(inputs,width,length,null,CancellationToken.None);

    internal static Result SolveWithProgress(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,CancellationToken cancellationToken)
      =>SolveInteractive(inputs,width,length,progress,null,cancellationToken);

    internal static Result SolveInteractive(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,CancellationToken cancellationToken)
      =>SolveWithSearch(inputs,width,length,progress,preview,cancellationToken,DefaultSearchSeconds);

    internal static Result SolveWithSearch(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,CancellationToken cancellationToken,double searchSeconds)
    {
      NestPackingDependency.EnsurePolygonsLoaded();
      return SolveLoaded(inputs,width,length,progress,preview,cancellationToken,Math.Max(0,double.IsFinite(searchSeconds)?searchSeconds:DefaultSearchSeconds));
    }

    internal static Result SolveUntilStopped(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,
      CancellationToken cancellationToken,double searchSeconds,Action<Result>? accepted)
    {
      NestPackingDependency.EnsurePolygonsLoaded();
      var kept=new Result([],0,[]);
      void Keep(Result result) { kept=result; accepted?.Invoke(result); }
      try
      { return SolveLoaded(inputs,width,length,progress,preview,cancellationToken,Math.Max(0,double.IsFinite(searchSeconds)?searchSeconds:DefaultSearchSeconds),Keep); }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
        var placed=kept.Placements.Select(part=>part.Id).ToHashSet(); var errors=new Dictionary<int,string>(kept.Errors);
        foreach(var input in inputs.Where(input=>!placed.Contains(input.Id))) errors.TryAdd(input.Id,"Not placed (stopped)");
        return kept with {Stopped=true,IsTrial=false,Errors=errors};
      }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result SolveLoaded(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,CancellationToken cancellationToken,double searchSeconds,Action<Result>? accepted=null)
    {
      cancellationToken.ThrowIfCancellationRequested(); progress?.Invoke(0);
      if(!double.IsFinite(width)||!double.IsFinite(length)||width<=0||length<=0)
        return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Invalid table size"));
      if(inputs.Any(input=>!double.IsFinite(input.Gap)||input.Gap<0))
        return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Invalid part gap"));
      var metrics=new SolveMetrics();
      var shapes=inputs.ToDictionary(input=>input.Id,input=>
      {
        bool skinny=!input.Fixed&&SkinnyPriority(input,width,length)>0;
        return input.Poses.Select(pose=>
        { cancellationToken.ThrowIfCancellationRequested(); var shape=CreateShape(input.Id,pose); return shape==null?null:shape with
          {Skinny=skinny,Gap=input.Gap,Metrics=metrics,Clearance=input.Gap>0?Clipper.InflatePaths(new PathsD {shape.Path},input.Gap,JoinType.Miter,EndType.Polygon,2,Precision):null}; })
          .Where(shape=>shape!=null).Cast<Shape>().ToArray();
      });
      var cache=new Dictionary<(Shape Moving,Shape Fixed),PathsD>(new PairIdentityComparer());
      var clock=Stopwatch.StartNew(); Plan? best=null;
      var state=new SearchState(); double initialSeconds=0;
      var refinement=new Stopwatch();
      Result Report(Plan plan,bool trial=false)=>Snapshot(plan) with {IsTrial=trial,Search=new(state.Tests,state.Layouts,state.Improvements,initialSeconds>0?initialSeconds:clock.Elapsed.TotalSeconds,refinement.Elapsed.TotalSeconds,searchSeconds>0&&refinement.Elapsed.TotalSeconds>=searchSeconds) {HalfTurnTests=state.HalfTurns,ConsumedLength=TotalMaterialLength(plan.Tables,Used(plan),length)},Profile=metrics.Snapshot()};
      double lastPreview=-PreviewInterval;
      void Preview(Plan plan,bool force=false,bool trial=false)
      {
        if(preview==null||!force&&clock.Elapsed.TotalSeconds-lastPreview<PreviewInterval) return;
        lastPreview=clock.Elapsed.TotalSeconds;
        preview(Report(plan,trial));
      }
      best=Pack(inputs,shapes,width,length,0,cache,progress,partial=> { accepted?.Invoke(Report(partial)); Preview(partial); },cancellationToken);
      initialSeconds=clock.Elapsed.TotalSeconds; state.Layouts++;
      Preview(best,true);
      accepted?.Invoke(Report(best));
      refinement.Start();
      if(searchSeconds>0)
      {
        best=FillSkinnyGaps(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },cancellationToken);
        best=ImproveHalfTurns(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          (plan,trial)=>Preview(plan,false,trial),plan=>accepted?.Invoke(Report(plan)),cancellationToken);
        best=SearchLayouts(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          (plan,trial)=>Preview(plan,false,trial),()=>preview!=null&&clock.Elapsed.TotalSeconds-lastPreview>=PreviewInterval,plan=>accepted?.Invoke(Report(plan)),progress,cancellationToken);
        best=ImprovePairs(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },progress,cancellationToken);
      }
      for(int trial=1;trial<OrderTrials;trial++)
      {
        if(searchSeconds<=0||refinement.Elapsed.TotalSeconds>=searchSeconds) break;
        cancellationToken.ThrowIfCancellationRequested();
        var plan=Pack(inputs,shapes,width,length,trial,cache,null,null,cancellationToken,
          ()=>refinement.Elapsed.TotalSeconds>=searchSeconds);
        if(plan.Parts.Count+plan.Errors.Count!=inputs.Count) break;
        state.Layouts++;
        if(Better(plan,best)) { best=plan; state.Improvements++; accepted?.Invoke(Report(best)); }
        Preview(plan,false,true);
        progress?.Invoke(98+2*trial/(OrderTrials-1));
      }
      cancellationToken.ThrowIfCancellationRequested(); progress?.Invoke(100);
      Preview(best,true);
      return Report(best!);
    }

    private static Result Snapshot(Plan plan)=>new(plan.Parts.Select(part=>new Placement(part.Shape.Id,part.Table,part.X,part.Y,part.Shape.Pose)).ToList(),plan.Tables,new(plan.Errors));

    internal static double SkinnyGrainRatio(Input input,double width,double length)=>!input.GrainGuided?0:
      input.Poses.Where(pose=>double.IsFinite(pose.Width)&&double.IsFinite(pose.Length)&&pose.Width>0&&pose.Length>0&&pose.Width<=width+CoordinateTolerance&&pose.Length<=length+CoordinateTolerance)
        .Select(pose=>Math.Max(pose.Width,pose.Length)/Math.Min(pose.Width,pose.Length)).DefaultIfEmpty(0).Max();

    private static double SkinnyPriority(Input input,double width,double length)
    { double ratio=SkinnyGrainRatio(input,width,length); return ratio>=MinimumSkinnyAspect?ratio:0; }

    private static Plan FillSkinnyGaps(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> preview,CancellationToken token)
    {
      var skinny=inputs.Where(input=>!input.Fixed&&SkinnyPriority(input,width,length)>0&&best.Parts.Any(part=>part.Shape.Id==input.Id)).ToArray();
      if(skinny.Length==0) return best;
      var ids=skinny.Select(input=>input.Id).ToHashSet();
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds*SkinnyTimeFraction;
      for(int pass=0;pass<SkinnyPasses&&!Expired();pass++)
      {
        token.ThrowIfCancellationRequested();
        // Retain all larger/fixed placements and cap each table at its already-used length.
        var limits=best.Parts.GroupBy(part=>part.Table).ToDictionary(table=>table.Key,table=>table.Max(part=>part.Y+part.Shape.Pose.Length));
        var parts=best.Parts.Where(part=>!ids.Contains(part.Shape.Id)).ToList();
        var sorted=pass==0?skinny.OrderByDescending(input=>SkinnyPriority(input,width,length)):skinny.OrderBy(input=>SkinnyPriority(input,width,length));
        foreach(var input in sorted.ThenBy(input=>input.Id))
        {
          if(Expired()) break;
          token.ThrowIfCancellationRequested(); state.Tests++;
          var placement=FindPlacement(shapes[input.Id],parts,width,length,best.Tables,cache,token,Expired,limits,true);
          if(placement==null) break;
          parts.Add(placement);
        }
        if(parts.Count!=best.Parts.Count) continue;
        var candidate=new Plan(parts,parts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors));
        state.Layouts++;
        if(candidate.Tables>best.Tables||candidate.Tables==best.Tables&&Used(candidate)>=Used(best)-CoordinateTolerance) continue;
        if(Better(candidate,best))
        { best=candidate; state.Improvements++; preview(best); }
      }
      return best;
    }

    private static Plan ImprovePairs(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> preview,Action<int>? progress,CancellationToken token)
    {
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      var movable=best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)).ToArray();
      double Concavity(Placed part)=>1-part.Shape.Area/(part.Shape.Pose.Width*part.Shape.Pose.Length);
      double Contribution(Placed part)=>best.Parts.Where(other=>other.Table==part.Table).Max(other=>other.Y+other.Shape.Pose.Length)-
        best.Parts.Where(other=>other.Table==part.Table&&other.Shape.Id!=part.Shape.Id).Select(other=>other.Y+other.Shape.Pose.Length).DefaultIfEmpty(0).Max();
      var pairs=movable.SelectMany((first,index)=>movable.Skip(index+1).Select(second=>(First:first,Second:second)))
        .OrderByDescending(pair=>Contribution(pair.First)+Contribution(pair.Second)).ThenByDescending(pair=>Concavity(pair.First)+Concavity(pair.Second)).ThenBy(pair=>pair.First.Shape.Id).ThenBy(pair=>pair.Second.Shape.Id)
        .Take(PairTrials).ToArray();
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds;
      for(int index=0;index<pairs.Length&&!Expired();index++)
      {
        token.ThrowIfCancellationRequested(); var pair=pairs[index];
        foreach(var (firstId,secondId) in new[]{(pair.First.Shape.Id,pair.Second.Shape.Id),(pair.Second.Shape.Id,pair.First.Shape.Id)})
        {
          var others=best.Parts.Where(part=>part.Shape.Id!=firstId&&part.Shape.Id!=secondId).ToList();
          foreach(var firstShape in shapes[firstId].Where(shape=>Inside(shape,0,0,width,length)).OrderBy(shape=>Math.Abs(shape.Pose.Rotation)).Take(PairPoseLimit))
          {
            if(Expired()) break;
            state.Tests++;
            token.ThrowIfCancellationRequested();
            var first=FindPlacement([firstShape],others,width,length,best.Tables,cache,token,Expired);
            if(first==null) continue;
            var occupied=new List<Placed>(others) {first};
            var second=FindPlacement(shapes[secondId],occupied,width,length,best.Tables,cache,token,Expired);
            if(second==null) continue;
            occupied.Add(second);
            var candidate=new Plan(occupied,occupied.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors));
            state.Layouts++;
            if(Better(candidate,best)) { best=candidate; state.Improvements++; preview(best); }
          }
        }
        progress?.Invoke(80+18*(index+1)/Math.Max(1,pairs.Length));
      }
      progress?.Invoke(98); return best;
    }

    private static Plan ImproveHalfTurns(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,
      Action<Plan,bool> preview,Action<Plan> accepted,CancellationToken token)
    {
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      double Concavity(Placed part)=>1-part.Shape.Area/(part.Shape.Pose.Width*part.Shape.Pose.Length);
      double Contribution(Placed part)=>best.Parts.Where(other=>other.Table==part.Table).Max(other=>other.Y+other.Shape.Pose.Length)-
        best.Parts.Where(other=>other.Table==part.Table&&other.Shape.Id!=part.Shape.Id).Select(other=>other.Y+other.Shape.Pose.Length).DefaultIfEmpty(0).Max();
      bool Opposite(Pose first,Pose second)=>Math.Abs(Math.Abs(first.Rotation-second.Rotation)%360-180)<=HalfTurnAngleTolerance&&first.Flipped==second.Flipped;
      var ids=best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)&&shapes[part.Shape.Id].Any(shape=>Opposite(shape.Pose,part.Shape.Pose)))
        .OrderByDescending(Concavity).ThenByDescending(part=>part.Shape.Area).Take(HalfTurnParts).Select(part=>part.Shape.Id).ToArray();
      double deadline=Math.Min(searchSeconds*BeamTimeFraction,clock.Elapsed.TotalSeconds+searchSeconds*HalfTurnTimeFraction);
      bool Expired()=>clock.Elapsed.TotalSeconds>=deadline;
      foreach(int id in ids)
      {
        if(Expired()) break;
        token.ThrowIfCancellationRequested();
        var moving=best.Parts.First(part=>part.Shape.Id==id);
        var partners=best.Parts.Where(part=>part.Shape.Id!=id&&!fixedIds.Contains(part.Shape.Id))
          .OrderByDescending(Contribution).ThenByDescending(Concavity).Take(HalfTurnPartners).Select(part=>part.Shape.Id).ToArray();
        foreach(var shape in shapes[id].Where(shape=>Opposite(shape.Pose,moving.Shape.Pose)))
        {
          Log.Write("vNest","half-turn part={0} from={1:G17} to={2:G17} partners={3}",id,moving.Shape.Pose.Rotation,shape.Pose.Rotation,string.Join(",",partners));
          foreach(int partner in partners)
          {
            if(Expired()) break;
            token.ThrowIfCancellationRequested(); state.Tests++; state.HalfTurns++;
            var others=best.Parts.Where(part=>part.Shape.Id!=id&&part.Shape.Id!=partner).ToList();
            var turned=new Placed(shape,moving.Table,moving.X,moving.Y);
            if(!Inside(shape,turned.X,turned.Y,width,length)||others.Any(other=>other.Table==turned.Table&&Collides(turned,other))) continue;
            others.Add(turned);
            var fitted=FindPlacement(shapes[partner],others,width,length,best.Tables,cache,token,Expired);
            if(fitted==null) continue;
            others.Add(fitted);
            var candidate=new Plan(others,others.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors));
            state.Layouts++; preview(candidate,true);
            if(Better(candidate,best)) { best=candidate; state.Improvements++; accepted(best); preview(best,false); }
          }
        }
      }
      return best;
    }

    private static Plan SearchLayouts(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,
      Action<Plan,bool> preview,Func<bool> previewDue,Action<Plan> accepted,Action<int>? progress,CancellationToken token)
    {
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      var fixedParts=best.Parts.Where(part=>fixedIds.Contains(part.Shape.Id)).ToList();
      var valid=inputs.Where(input=>!input.Fixed&&shapes[input.Id].Any(shape=>Inside(shape,0,0,width,length))).ToArray();
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds*BeamTimeFraction;
      for(int order=0;order<BeamOrders&&!Expired();order++)
      {
        double Key(Input input)=>order switch {
          1=>shapes[input.Id].Min(shape=>Math.Max(shape.Pose.Width,shape.Pose.Length)),
          2=>shapes[input.Id].Max(shape=>1-shape.Area/(shape.Pose.Width*shape.Pose.Length)),
          _=>shapes[input.Id][0].Area };
        var sorted=valid.OrderByDescending(Key).ThenBy(input=>input.Id).ToArray();
        var beams=new List<Plan> {new(fixedParts,fixedParts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors))};
        for(int index=0;index<sorted.Length&&!Expired();index++)
        {
          var next=new List<Plan>(); var input=sorted[index];
          foreach(var beam in beams)
          {
            void ShowTrial(Placed placement)
            {
              if(!previewDue()) return;
              var parts=new List<Placed>(beam.Parts) {placement}; var ids=parts.Select(part=>part.Shape.Id).ToHashSet();
              parts.AddRange(best.Parts.Where(part=>!ids.Contains(part.Shape.Id)));
              preview(new Plan(parts,parts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(beam.Errors)),true);
            }
            foreach(var placement in PlacementChoices(shapes[input.Id],beam.Parts,width,length,beam.Tables,cache,token,Expired,state,ShowTrial))
            {
              var parts=new List<Placed>(beam.Parts) {placement}; var errors=new Dictionary<int,string>(beam.Errors); errors.Remove(input.Id);
              var candidate=new Plan(parts,Math.Max(beam.Tables,placement.Table+1),errors); next.Add(candidate);
              if(index==sorted.Length-1)
              {
                state.Layouts++;
                if(Better(candidate,best)) { best=candidate; state.Improvements++; accepted(best); }
              }
            }
          }
          if(next.Count==0) break;
          var ranked=next.OrderBy(plan=>plan.Tables).ThenBy(Used).ThenBy(SkinnyLeft)
            .ThenBy(plan=>plan.Parts.Sum(part=>part.Shape.Pose.Width*part.Shape.Pose.Length)).ToList();
          beams=[];
          foreach(var plan in ranked)
          {
            if(beams.Any(other=>Equivalent(other,plan))) continue;
            beams.Add(plan); if(beams.Count==BeamWidth) break;
          }
          foreach(var plan in beams)
          {
            // Display actual branches under evaluation, keeping untouched parts ghosted at the saved best positions.
            var ids=plan.Parts.Select(part=>part.Shape.Id).ToHashSet();
            var trialParts=plan.Parts.Concat(best.Parts.Where(part=>!ids.Contains(part.Shape.Id))).ToList();
            preview(new Plan(trialParts,trialParts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(plan.Errors)),true);
          }
          progress?.Invoke(50+30*(order*sorted.Length+index+1)/Math.Max(1,BeamOrders*sorted.Length));
        }
      }
      progress?.Invoke(80); return best;
    }

    private static double Used(Plan plan)=>plan.Parts.Where(part=>part.Table==plan.Tables-1)
      .Select(part=>part.Y+part.Shape.Pose.Length).DefaultIfEmpty(0).Max();

    internal static double TotalMaterialLength(int tables,double finalUsed,double tableLength)=>tables<=0?0:
      (tables-1)*tableLength+finalUsed;

    private static bool Equivalent(Plan first,Plan second)=>first.Parts.Count==second.Parts.Count&&first.Parts.Zip(second.Parts)
      .All(pair=>pair.First.Shape==pair.Second.Shape&&pair.First.Table==pair.Second.Table&&Math.Abs(pair.First.X-pair.Second.X)<=CoordinateTolerance&&Math.Abs(pair.First.Y-pair.Second.Y)<=CoordinateTolerance);

    private static IEnumerable<Placed> PlacementChoices(IReadOnlyList<Shape> shapes,IReadOnlyList<Placed> parts,double width,double length,int tables,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken token,Func<bool> stop,SearchState state,Action<Placed> trial)
    {
      for(int table=0;table<=tables&&table<NestPackingEngine.MaximumTables&&!stop();table++)
      {
        var occupied=parts.Where(part=>part.Table==table).ToArray();
        var choices=new List<Placed>();
        foreach(var shape in shapes.Where(shape=>Inside(shape,0,0,width,length)))
        {
          token.ThrowIfCancellationRequested(); if(stop()) yield break;
          var positions=new List<Placed>();
          foreach(var point in Anchors(shape,occupied,width,length,cache,token))
          {
            token.ThrowIfCancellationRequested(); if(stop()) yield break;
            state.Tests++;
            var placement=new Placed(shape,table,point.x,point.y);
            if(!occupied.Any(other=>Collides(placement,other))) { positions.Add(placement); trial(placement); }
          }
          choices.AddRange(positions.OrderBy(part=>part.Y+part.Shape.Pose.Length).ThenBy(part=>part.Y).ThenByDescending(part=>part.X+part.Shape.Pose.Width).Take(BeamAnchors));
        }
        if(choices.Count==0) continue;
        foreach(var choice in choices) yield return choice;
        yield break;
      }
    }

    private static Shape? CreateShape(int id,Pose pose)
    {
      if(!double.IsFinite(pose.Width)||!double.IsFinite(pose.Length)||pose.Width<=0||pose.Length<=0) return null;
      var path=pose.Contour is {Count:>=3} contour
        ? new PathD(contour.Select(point=>new PointD(point.X,point.Y)))
        : new PathD {new(0,0),new(pose.Width,0),new(pose.Width,pose.Length),new(0,pose.Length)};
      if(path.Any(point=>!double.IsFinite(point.x)||!double.IsFinite(point.y))) return null;
      double area=Clipper.Area(path);
      if(Math.Abs(area)<=AreaTolerance) return null;
      if(area<0) path.Reverse();
      var contact=path;
      if(path.Count>ContactVertexLimit)
      {
        double tolerance=ContactTolerance;
        do { contact=Clipper.SimplifyPath(path,tolerance,true); tolerance*=2; } while(contact.Count>ContactVertexLimit);
        var expanded=Clipper.InflatePaths(new PathsD {contact},tolerance/2,JoinType.Miter,EndType.Polygon,2,Precision);
        contact=expanded.OrderByDescending(part=>Math.Abs(Clipper.Area(part))).FirstOrDefault()??path;
      }
      return new Shape(id,pose,path,contact,Math.Abs(area));
    }

    private static bool Better(Plan candidate,Plan previous)
    {
      if(candidate.Parts.Count!=previous.Parts.Count) return candidate.Parts.Count>previous.Parts.Count;
      // Earlier tables consume their full length; opening another table cannot be a free gap-saving move.
      if(candidate.Tables!=previous.Tables) return candidate.Tables<previous.Tables;
      double candidateLength=Used(candidate),previousLength=Used(previous);
      if(Math.Abs(candidateLength-previousLength)>CoordinateTolerance) return candidateLength<previousLength;
      double candidateLeft=SkinnyLeft(candidate),previousLeft=SkinnyLeft(previous);
      if(Math.Abs(candidateLeft-previousLeft)>CoordinateTolerance) return candidateLeft<previousLeft;
      if(SkinnyMoved(candidate,previous)) return false;
      return candidate.Parts.Sum(part=>part.X+part.Shape.Pose.Width)>previous.Parts.Sum(part=>part.X+part.Shape.Pose.Width)+CoordinateTolerance;
    }

    private static double SkinnyLeft(Plan plan)=>plan.Parts.Where(part=>part.Shape.Skinny).Sum(part=>part.Y);

    private static bool SkinnyMoved(Plan candidate,Plan previous)
    {
      var skinny=previous.Parts.Where(part=>part.Shape.Skinny).ToDictionary(part=>part.Shape.Id);
      return candidate.Parts.Any(part=>skinny.TryGetValue(part.Shape.Id,out var before)&&
        (before.Shape!=part.Shape||before.Table!=part.Table||Math.Abs(before.X-part.X)>CoordinateTolerance||Math.Abs(before.Y-part.Y)>CoordinateTolerance));
    }

    private static Plan Pack(IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,int order,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Action<int>? progress,Action<Plan>? preview,CancellationToken cancellationToken,Func<bool>? stop=null)
    {
      var parts=new List<Placed>(); var errors=new Dictionary<int,string>(); int tables=0;
      int processed=0;
      void Report()
      {
        double fraction=(double)processed/Math.Max(1,inputs.Count);
        progress?.Invoke((int)(order==0?50*fraction:50+50*(order-1+fraction)/(OrderTrials-1)));
      }
      foreach(var input in inputs.Where(input=>input.Fixed).OrderBy(input=>input.Table).ThenBy(input=>input.X).ThenBy(input=>input.Y))
      {
        cancellationToken.ThrowIfCancellationRequested(); processed++; Report();
        var shape=shapes[input.Id].FirstOrDefault();
        if(shape==null) { errors[input.Id]="No allowed rotation"; continue; }
        if(input.Table<0||input.Table>=NestPackingEngine.MaximumTables||!Inside(shape,input.X,input.Y,width,length))
        { errors[input.Id]="Fixed part outside table"; continue; }
        var part=new Placed(shape,input.Table,input.X,input.Y);
        if(parts.Any(other=>other.Table==input.Table&&Collides(part,other)))
        { errors[input.Id]=input.Gap>0?"Fixed parts violate gap":"Fixed parts overlap"; continue; }
        parts.Add(part); tables=Math.Max(tables,input.Table+1);
        preview?.Invoke(new Plan(parts,tables,errors));
      }
      double Key(Input input)
      {
        var options=shapes[input.Id];
        return order switch {
          1=>options.Min(shape=>shape.Pose.Width),
          2=>options.Min(shape=>shape.Pose.Length),
          3=>options.Min(shape=>Math.Max(shape.Pose.Width,shape.Pose.Length)),
          4=>options.Min(shape=>Math.Min(shape.Pose.Width,shape.Pose.Length)),
          _=>options[0].Area };
      }
      var remaining=inputs.Where(input=>!input.Fixed).ToList();
      foreach(var input in remaining.ToArray())
      {
        cancellationToken.ThrowIfCancellationRequested();
        if(shapes[input.Id].Length==0) { errors[input.Id]="No allowed rotation"; remaining.Remove(input); }
        else if(!shapes[input.Id].Any(shape=>Inside(shape,0,0,width,length)))
        { errors[input.Id]="Part exceeds table"; remaining.Remove(input); }
      }
      var sorted=remaining.OrderByDescending(input=>order==0?SkinnyPriority(input,width,length):0)
        .ThenBy(input=>order==OrderTrials-1?Key(input):-Key(input))
        .ThenBy(input=>shapes[input.Id].Min(shape=>shape.Pose.Width))
        .ThenBy(input=>shapes[input.Id].Min(shape=>shape.Pose.Length)).ThenBy(input=>input.Id);
      foreach(var input in sorted)
      {
        if(stop?.Invoke()==true) break;
        cancellationToken.ThrowIfCancellationRequested();
        var accepted=FindPlacement(shapes[input.Id],parts,width,length,tables,cache,cancellationToken,stop);
        processed++; Report();
        if(accepted==null) { errors[input.Id]="Table limit reached"; continue; }
        parts.Add(accepted); tables=Math.Max(tables,accepted.Table+1);
        preview?.Invoke(new Plan(parts,tables,errors));
      }
      return new Plan(parts,tables,errors);
    }

    private static Placed? FindPlacement(IReadOnlyList<Shape> shapes,IReadOnlyList<Placed> parts,double width,double length,int tables,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken token,Func<bool>? stop,IReadOnlyDictionary<int,double>? materialLimits=null,bool preferGaps=false)
    {
      int lastExisting=parts.Select(part=>part.Table).DefaultIfEmpty(-1).Max();
      double lastEnd=parts.Where(part=>part.Table==lastExisting).Select(part=>part.Y+part.Shape.Pose.Length).DefaultIfEmpty(0).Max();
      double existingMaterial=TotalMaterialLength(lastExisting+1,lastEnd,length);
      for(int table=0;table<=tables&&table<NestPackingEngine.MaximumTables;table++)
      {
        Placed? accepted=null;
        if(materialLimits!=null&&!materialLimits.ContainsKey(table)) continue;
        double availableLength=materialLimits?.GetValueOrDefault(table)??length;
        int bestGap=-1;
        double bestLength=double.PositiveInfinity,bestY=double.PositiveInfinity,bestBottom=double.NegativeInfinity;
        var occupied=parts.Where(part=>part.Table==table).ToArray();
        foreach(var shape in shapes.OrderBy(shape=>Math.Abs(shape.Pose.Rotation)))
        {
          token.ThrowIfCancellationRequested(); if(stop?.Invoke()==true) return null;
          if(!Inside(shape,0,0,width,availableLength)) continue;
          double lowerBound=table<lastExisting?existingMaterial:TotalMaterialLength(table+1,
            table==lastExisting?Math.Max(lastEnd,shape.Pose.Length):shape.Pose.Length,length);
          // An ideal y=0/bottom-aligned placement bounds every anchor for this pose.
          if(lowerBound>bestLength+CoordinateTolerance||!preferGaps&&Math.Abs(lowerBound-bestLength)<=CoordinateTolerance&&
            bestY<=CoordinateTolerance&&bestBottom>=width-CoordinateTolerance)
          { if(shape.Metrics is {} profile) profile.PosesPruned++; continue; }
          foreach(var point in Anchors(shape,occupied,width,availableLength,cache,token))
          {
            token.ThrowIfCancellationRequested(); if(stop?.Invoke()==true) return null;
            double end=point.y+shape.Pose.Length;
            double material=table<lastExisting?existingMaterial:TotalMaterialLength(table+1,table==lastExisting?Math.Max(lastEnd,end):end,length);
            if(material>bestLength+CoordinateTolerance) continue;
            double bottom=point.x+shape.Pose.Width;
            if(!preferGaps&&Math.Abs(material-bestLength)<=CoordinateTolerance&&
              (point.y>bestY+CoordinateTolerance||Math.Abs(point.y-bestY)<=CoordinateTolerance&&bottom<=bestBottom+CoordinateTolerance)) continue;
            var candidate=new Placed(shape,table,point.x,point.y);
            if(occupied.Any(other=>Collides(candidate,other))) continue;
            int gap=preferGaps?GapNeighbors(candidate,occupied):0;
            if(Math.Abs(material-bestLength)<=CoordinateTolerance)
            {
              if(point.y>bestY+CoordinateTolerance) continue;
              if(Math.Abs(point.y-bestY)<=CoordinateTolerance&&
                (gap<bestGap||gap==bestGap&&bottom<=bestBottom+CoordinateTolerance)) continue;
            }
            accepted=candidate; bestLength=material; bestY=point.y; bestBottom=bottom; bestGap=gap;
          }
        }
        if(accepted!=null) return accepted;
      }
      return null;
    }

    private static int GapNeighbors(Placed part,IEnumerable<Placed> others)
    {
      int count=0;
      foreach(var other in others)
      {
        double clearance=Math.Max(part.Shape.Gap,other.Shape.Gap)+ContactTolerance;
        if(part.X+part.Shape.Pose.Width<other.X-clearance||other.X+other.Shape.Pose.Width<part.X-clearance||
          part.Y+part.Shape.Pose.Length<other.Y-clearance||other.Y+other.Shape.Pose.Length<part.Y-clearance) continue;
        bool Near(PathD first,double x,double y,PathD second,double otherX,double otherY)
        {
          foreach(var point in first)
            for(int index=0;index<second.Count;index++)
            {
              var a=second[index]; var b=second[(index+1)%second.Count];
              double dx=b.x-a.x,dy=b.y-a.y,px=point.x+x-a.x-otherX,py=point.y+y-a.y-otherY;
              double squared=dx*dx+dy*dy,t=squared>0?Math.Clamp((px*dx+py*dy)/squared,0,1):0;
              if((px-t*dx)*(px-t*dx)+(py-t*dy)*(py-t*dy)<=clearance*clearance) return true;
            }
          return false;
        }
        if(!Near(part.Shape.Contact,part.X,part.Y,other.Shape.Contact,other.X,other.Y)&&!Near(other.Shape.Contact,other.X,other.Y,part.Shape.Contact,part.X,part.Y)) continue;
        if(++count>=GapNeighborLimit) break;
      }
      return count;
    }

    private static bool Inside(Shape shape,double x,double y,double width,double length)=>double.IsFinite(x)&&double.IsFinite(y)&&
      x>=-CoordinateTolerance&&y>=-CoordinateTolerance&&x+shape.Pose.Width<=width+CoordinateTolerance&&y+shape.Pose.Length<=length+CoordinateTolerance;

    private static PathD Move(PathD path,double x,double y)=>new(path.Select(point=>new PointD(point.x+x,point.y+y)));
    private static bool Collides(Placed first,Placed second)
    {
      double gap=Math.Max(first.Shape.Gap,second.Shape.Gap);
      if(first.X+first.Shape.Pose.Width+gap<=second.X+CoordinateTolerance||second.X+second.Shape.Pose.Width+gap<=first.X+CoordinateTolerance||
        first.Y+first.Shape.Pose.Length+gap<=second.Y+CoordinateTolerance||second.Y+second.Shape.Pose.Length+gap<=first.Y+CoordinateTolerance) return false;
      if(second.Shape.Gap>first.Shape.Gap) (first,second)=(second,first);
      var metrics=first.Shape.Metrics; long started=Stopwatch.GetTimestamp();
      var overlap=Clipper.Intersect(first.WorldClearance,new PathsD {second.WorldPath},FillRule.NonZero,Precision);
      if(metrics!=null) { metrics.CollisionCalls++; metrics.CollisionTicks+=Stopwatch.GetTimestamp()-started; }
      return Math.Abs(Clipper.Area(overlap))>AreaTolerance;
    }

    private static IEnumerable<PointD> Anchors(Shape moving,IReadOnlyList<Placed> occupied,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken cancellationToken)
    {
      double maxX=Math.Max(0,width-moving.Pose.Width),maxY=Math.Max(0,length-moving.Pose.Length);
      var candidates=new List<PointD> {new(0,0),new(maxX,0),new(0,maxY),new(maxX,maxY)};
      if(occupied.Count==0) return candidates.Take(2);
      var forbidden=new PathsD();
      foreach(var fixedPart in occupied)
      {
        cancellationToken.ThrowIfCancellationRequested();
        var key=(moving,fixedPart.Shape);
        if(!cache.TryGetValue(key,out var paths))
        {
          double gap=Math.Max(moving.Gap,fixedPart.Shape.Gap);
          paths=PairGeometry(moving,fixedPart.Shape,gap);
          cache.Add(key,paths);
        }
        foreach(var path in paths) forbidden.Add(Move(path,fixedPart.X,fixedPart.Y));
      }
      cancellationToken.ThrowIfCancellationRequested();
      long started=Stopwatch.GetTimestamp();
      var union=Clipper.Union(forbidden,new PathsD(),FillRule.NonZero,Precision);
      if(maxX>CoordinateTolerance&&maxY>CoordinateTolerance)
      {
        var sheet=new PathD {new(0,0),new(maxX,0),new(maxX,maxY),new(0,maxY)};
        var feasible=Clipper.Difference(new PathsD {sheet},union,FillRule.NonZero,Precision);
        candidates.AddRange(feasible.SelectMany(path=>path));
      }
      if(moving.Metrics is {} metrics) { metrics.AnchorCalls++; metrics.AnchorTicks+=Stopwatch.GetTimestamp()-started; }
      // Exact-fit corridors disappear from the union's outer boundary; retain pairwise contact seams for skinny parts.
      foreach(var path in moving.Skinny?union.Concat(forbidden):union)
        for(int index=0;index<path.Count;index++)
        {
          var a=path[index]; var b=path[(index+1)%path.Count]; candidates.Add(a);
          foreach(var x in new[]{0.0,maxX})
            if(Math.Abs(b.x-a.x)>CoordinateTolerance)
            { double t=(x-a.x)/(b.x-a.x); if(t>=0&&t<=1) candidates.Add(new PointD(x,a.y+t*(b.y-a.y))); }
          foreach(var y in new[]{0.0,maxY})
            if(Math.Abs(b.y-a.y)>CoordinateTolerance)
            { double t=(y-a.y)/(b.y-a.y); if(t>=0&&t<=1) candidates.Add(new PointD(a.x+t*(b.x-a.x),y)); }
        }
      return candidates.Where(point=>double.IsFinite(point.x)&&double.IsFinite(point.y)&&point.x>=-CoordinateTolerance&&point.y>=-CoordinateTolerance&&point.x<=maxX+CoordinateTolerance&&point.y<=maxY+CoordinateTolerance)
        .Select(point=>new PointD(Math.Clamp(point.x,0,maxX),Math.Clamp(point.y,0,maxY)))
        .DistinctBy(point=>(Math.Round(point.x,Precision),Math.Round(point.y,Precision)))
        .OrderBy(point=>point.y).ThenByDescending(point=>point.x).Take(CandidateLimit);
    }
  }
}

namespace vTools
{
  using System.Diagnostics;
  using System.Globalization;
  using System.Text;
  using Clipper2Lib;


  internal static partial class NestContourPackingEngine
  {
    // Reusable geometry-cache defaults and memory limits
    private const int PairCacheEntryLimit = 8192; // Maximum reusable pair results per loaded plug-in; positive, least-recently-used entries are evicted.
    private const long PairCacheVertexLimit = 2000000; // Maximum retained PointD vertices, about 32 MB of coordinates plus collection/key overhead.
    private static readonly object PairCacheLock = new();
    private static readonly Dictionary<PairKey,LinkedListNode<PairEntry>> PairCache = [];
    private static readonly LinkedList<PairEntry> PairRecency = new();
    private static long _pairCacheVertices;
    private readonly record struct PairKey(string Moving,string Fixed,double Gap);
    private sealed record PairEntry(PairKey Key,PathsD Paths,long Vertices);
    private sealed class PairIdentityComparer : IEqualityComparer<(Shape Moving,Shape Fixed)>
    {
      public bool Equals((Shape Moving,Shape Fixed) first,(Shape Moving,Shape Fixed) second)=>
        ReferenceEquals(first.Moving,second.Moving)&&ReferenceEquals(first.Fixed,second.Fixed);
      public int GetHashCode((Shape Moving,Shape Fixed) pair)=>System.HashCode.Combine(
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Moving),System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Fixed));
    }
    private sealed class SolveMetrics
    {
      internal int PairHits,PairMisses,AnchorCalls,CollisionCalls,PosesPruned;
      internal long PairTicks,AnchorTicks,CollisionTicks;
      internal NestPackingEngine.ProfileStatistics Snapshot()
      {
        lock(PairCacheLock) return new(PairHits,PairMisses,(double)PairTicks/Stopwatch.Frequency,AnchorCalls,
          (double)AnchorTicks/Stopwatch.Frequency,CollisionCalls,(double)CollisionTicks/Stopwatch.Frequency,
          PosesPruned,PairCache.Count,_pairCacheVertices);
      }
    }

    private static string ContactKey(PathD path)
    {
      var key=new StringBuilder();
      foreach(var point in path)
        key.Append(BitConverter.DoubleToInt64Bits(point.x).ToString("X16",CultureInfo.InvariantCulture))
          .Append(BitConverter.DoubleToInt64Bits(point.y).ToString("X16",CultureInfo.InvariantCulture));
      return key.ToString();
    }

    private static PathsD PairGeometry(Shape moving,Shape fixedShape,double gap)
    {
      var key=new PairKey(moving.GeometryKey,fixedShape.GeometryKey,gap);
      lock(PairCacheLock)
      {
        if(PairCache.TryGetValue(key,out var node))
        {
          PairRecency.Remove(node); PairRecency.AddLast(node);
          if(moving.Metrics is {} hit) hit.PairHits++;
          return node.Value.Paths;
        }
      }
      long started=Stopwatch.GetTimestamp();
      var paths=Minkowski.Diff(moving.Contact,fixedShape.Contact,true,Precision);
      if(gap>0) paths=Clipper.InflatePaths(paths,gap,JoinType.Miter,EndType.Polygon,2,Precision);
      if(moving.Metrics is {} miss) { miss.PairMisses++; miss.PairTicks+=Stopwatch.GetTimestamp()-started; }
      long vertices=paths.Sum(path=>(long)path.Count);
      if(vertices>PairCacheVertexLimit) return paths;
      lock(PairCacheLock)
      {
        if(PairCache.TryGetValue(key,out var existing)) return existing.Value.Paths;
        while(PairRecency.First is {} first&&(PairCache.Count>=PairCacheEntryLimit||_pairCacheVertices+vertices>PairCacheVertexLimit))
        { PairCache.Remove(first.Value.Key); PairRecency.RemoveFirst(); _pairCacheVertices-=first.Value.Vertices; }
        PairCache[key]=PairRecency.AddLast(new PairEntry(key,paths,vertices)); _pairCacheVertices+=vertices;
      }
      return paths;
    }

    internal static void ClearGeometryCache()
    { lock(PairCacheLock) { PairCache.Clear(); PairRecency.Clear(); _pairCacheVertices=0; } }
  }
}

namespace vTools
{
  using System.Reflection;
  using System.Runtime.Loader;


  internal static class NestPackingDependency
  {
    // Embedded dependency identity
    private const string AssemblyName = "RectpackSharp"; // Exact simple name of the pinned MIT-licensed rectangle-packing dependency.
    private const string ResourceName = "vTools.Dependencies.RectpackSharp.dll"; // Embedded dependency resource; keeps the plugin distributable as one DLL.
    private const string PolygonAssemblyName = "Clipper2Lib"; // Exact simple name of the pinned polygon Boolean/Minkowski dependency.
    private const string PolygonResourceName = "vTools.Dependencies.Clipper2Lib.dll"; // Embedded polygon engine resource, without an additional deployed DLL.

    internal static void EnsureLoaded()
      =>EnsureLoaded(AssemblyName,ResourceName);

    internal static void EnsurePolygonsLoaded()=>EnsureLoaded(PolygonAssemblyName,PolygonResourceName);

    private static void EnsureLoaded(string name,string resourceName)
    {
      var owner=typeof(NestPackingDependency).Assembly;
      var context=AssemblyLoadContext.GetLoadContext(owner)??AssemblyLoadContext.Default;
      if(context.Assemblies.Any(assembly=>assembly.GetName().Name==name)) return;
      using var resource=owner.GetManifestResourceStream(resourceName)??throw new InvalidOperationException("The embedded nesting engine is missing.");
      context.LoadFromStream(resource);
    }
  }
}
