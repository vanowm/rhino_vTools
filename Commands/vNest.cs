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
    internal const double TableAllowanceInches = 2.0; // Extra material consumed per used table, in inches; converted to document units for scoring and yardage.
    private const int ManualPreviewInterval = 30; // Minimum milliseconds between drag/Alt-rotation redraws; the final mouse-up position is always evaluated.
    private const double ManualContactSnapPixels = 2.0; // Maximum screen-pixel correction aligning a blocked drag with a nearby exact part contact; never relaxes collisions.
    private const double ManualRotationPixelStep = 0.5; // Degrees per horizontal screen pixel when Alt rotation starts exactly at the part's pivot.
    private const double ManualKeyboardRotationStep = 45.0; // Degrees per Alt+arrow press; Left/Up turn forward, Right/Down reverse around the part center.
    private const int PreviewKeyboardHook = 3; // Windows thread-local WH_GETMESSAGE hook; never changes Rhino's global shortcuts.
    private const int PreviewRemovedMessage = 1; // PM_REMOVE value identifying keyboard messages about to be dispatched.
    private const uint PreviewWmKeyDown = 0x0100,PreviewWmKeyUp = 0x0101,PreviewWmSysKeyDown = 0x0104,PreviewWmSysKeyUp = 0x0105; // Win32 normal/Alt key-down and key-up messages.
    private const long PreviewRepeatedKeyMask = 1L<<30; // Win32 key-message previous-state bit; set for auto-repeat rather than a new physical press.
    private const int PreviewAltKey = 0x12,PreviewEscapeKey = 0x1B; // Virtual keys ending an Alt gesture or cancelling a live manual edit.
    private const int PreviewControlKey = 0x11,PreviewShiftKey = 0x10,PreviewUndoKey = 0x5A,PreviewRedoKey = 0x59; // Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z edit preview history without changing Rhino shortcuts.
    private const int ManualHistoryLimit = 100; // Maximum completed manual-preview edits retained for local undo; oldest snapshots are dropped.
    private const int RotationPivotSize = 7; // Screen-pixel cross size for the fixed rotation pivot cue while Alt is held.
    private const double ManualFreeArrowStep = 1.0; // Model-unit arrow increment with Ctrl when both table and obstacle limits are bypassed.
    private const int PreviewLeftKey = 0x25,PreviewUpKey = 0x26,PreviewRightKey = 0x27,PreviewDownKey = 0x28; // Virtual arrow keys routed only while a Rhino viewport has keyboard focus.
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
    internal const TableStack DefaultStack = TableStack.Vertical; // Tables always stack vertically without rotating the material or parts.
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
    internal const double TableGap = 10; // Model-unit space between displayed material tables; zero or greater.
    internal const double MaterialBasisAngle = -90.0; // Degrees from CPlane X to the material-width axis; length runs horizontally along CPlane X.
    internal const int PreviewAlpha = 200; // Preview stroke alpha from zero invisible through 255 opaque.
    internal const int PreviewPointSize = 3; // Preview point diameter in display pixels; positive.
    internal const bool DefaultShowGap = false; // true shows active parts' full outward collision gap; false hides the clearance guide.
    private const int GapOutlineWidth = 1; // Clearance-guide stroke width in screen pixels; one is a hairline independent of object thickness.
    private static readonly Color GapOutlineColor = Color.DimGray; // Neutral clearance-guide color with contrast on Rhino's gray viewport, distinct from active perimeter feedback.
    internal static readonly Color TableColor = Color.Gray; // RGB color of material-table preview outlines.
    internal const double FreeAngleStep = 5.0; // Degrees between free-rotation bounding-box candidates; greater than zero through 90.
    private const int FreePoseLimit = 8; // Normal free-rotation alternatives, including exact half-turn counterparts; positive even integer, expanded only for small simple jobs.
    private const int SmallNestCopyLimit = 8; // Maximum included copies in one material job receiving a broader free-rotation search; larger jobs keep the normal budget.
    private const int SmallNestFreePoseLimit = 72; // Maximum free-rotation alternatives for a small material job, before optional mirroring; positive even integer.
    private const int SmallNestPoseVertexBudget = 432; // Maximum outline-vertex/pose product used to expand one small-job part's rotations; dense outlines retain at least the normal budget.
    private const int RotationRefinementSeeds = 6; // Best coarse free-rotation candidates refined locally; positive integer.
    private const int RotationRefinementPasses = 9; // Step-halving refinement passes per seed; positive integer, reaching sub-degree angles.
    private const double LinearEdgeTolerance = 1e-6; // Model-unit tolerance for exact straight boundary directions added to rotation candidates.
    private const double OutlineTolerance = 0.001; // Model-unit deviation used for curved outline polygon sampling; positive.
    private const double OutlineAngleTolerance = 0.05; // Radian turning tolerance for native curve-to-polyline sampling; positive.
    private const string CutLayerPrefix = "CUT"; // Case-insensitive leaf-layer prefix identifying cutting geometry for nesting footprints.
    private const double PoseDimensionTolerance = 1e-6; // Model-unit threshold for coalescing equivalent rotated bounds; positive.
    private const double PoseAngleSeparation = FreeAngleStep/2; // Minimum degree separation between polygon rotation alternatives; preserves half-turn concavity choices.
    internal const double GrainAngularTolerance = 0.05; // Allowed numeric grain-axis deviation in degrees; positive and much smaller than a quarter turn.
    internal const string GrainIdentifier = "GRAIN"; // Case-insensitive object name or leaf layer name identifying an open grain guide.
    private const double GrainCurveSample = 0.5; // Arc-length fraction, zero through one, used for automatic tangents on curved marked grain guides.
    private const double DrawnGuideSpanFraction = 0.8; // Fraction of another selected part's bounding-box span used when copying a drawn grain direction; greater than zero through one.
    private const string NameLayer = "Reference"; // Preferred leaf layer for the large text label identifying a part.
    private const string NotchLabelName = "NotchLabel"; // Legacy/current notch-label object name; excluded from automatic part names regardless of layer or size.
    private const string NotchRoleKey = "notch.object_role"; // Notch metadata key; value label identifies an annotation rather than a part title.
    private const string NotchLabelIdKey = "notch.label_id"; // Notch-label metadata identifier; its presence excludes older metadata-tagged labels from part naming.
    private const string DetectedGrainLabel = "Detected"; // Part-list indication for a grain guide found in the source geometry.
    private const string SetGrainLabel = "Set"; // Part-list indication for a picked, drawn or entered grain direction.
    private const string MissingGrainLabel = "None"; // Part-list indication when no grain direction is available.
    private const string SourceGrainLabel = "Source axis"; // Part-list indication when Grain=On uses the source up/down axis instead of a guide.
    internal const string TableLayer = "Reference"; // Existing or new layer for accepted material-table outlines.
    internal const string TableName = "NestTable"; // Object name for accepted material-table rectangles.
    internal const string DefaultTableNamePrefix = "table"; // Default editable label/group stem followed by the one-based table number.
    private const long FirstTableNumber = 1; // First automatic table-name suffix in drawings without existing numbered nest tables.
    private const double TableLabelHeightFraction = 0.08; // Visible glyph height as a fraction of material width; positive, 0.08 gives 4.8 units for a 60-unit table.
    private const double TableLabelGapFraction = 0.2; // Border clearance as a fraction of label glyph height; positive.
    private const double TableLabelRowGapFraction = 0.2; // Inter-row clearance as a fraction of label glyph height; positive.
    private const double TableLabelBoxPaddingFraction = 0.2; // Padding inside the label frame as a fraction of visible glyph height; positive.
    private const double TableLabelScale = 1.0; // Fixed annotation display scale; positive, independent of the document's model-space text scale.
    private const double TableLabelLayoutTolerance = 1e-6; // Model-unit/scale tolerance for comparing persisted annotation positions and display heights with the current layout.
    private const string TableLabelMinimumDimensions = "888.888 x 888.888"; // Minimum label-frame width reserved for three-digit dimensions with three fractional digits; names may expand it.
    private const string TableLabelHeightSample = "Ag"; // Font-height reference including ascenders and descenders; keeps label sizing independent of usage values.
    private const string TableWidthFormat = "0.###"; // Width display preserves up to three fractional decimal digits in document units.
    private const string TableLengthFormat = "0.0"; // Occupied length and yardage are rounded to one decimal place.
    private const string YardageUnitLabel = "yds"; // Display suffix for material lengths converted to yards.
    private const string UnknownYardageText = "n/a yds"; // Display value when document units cannot be converted to yards.
    private const string ResultColumnGap = "\t"; // Tab character between material-result columns in Rhino command history.
    private const string TableSinglePartLabel = "part"; // Label suffix when a table contains exactly one nested part copy.
    private const string TableMultiplePartsLabel = "parts"; // Label suffix when a table contains zero or multiple nested part copies.
    private const int TableNameLimit = 80; // Maximum editable table-name characters; longer input is truncated after whitespace normalization.
    private const int MaterialPresetNameLimit = 80; // Maximum material-preset name length after whitespace normalization; longer names are rejected.
    private const string MaterialPresetsKey = "materialPresets"; // Global presets store name, document-unit width/gap and grain mode, obedience, deviation and reversal.
    private const string MaterialDraftsKey = "materialDrafts"; // Persisted unsaved named-material edits; saved presets remain unchanged until explicitly saved.
    private const string MaterialDatabaseFolder = "vTools"; // Per-user roaming application-data subfolder shared by every Rhino version and plug-in installation.
    private const string MaterialDatabaseFileName = "materials.json"; // Global named-material library and unsaved drafts; independent of documents and DLL folders.
    private const string MaterialDatabaseMutexName = "Local\\vTools.MaterialDatabase"; // Windows-session mutex serializing material-library transactions across Rhino processes.
    private const int MaterialDatabaseLockMilliseconds = 5000; // Maximum milliseconds waiting for another Rhino process to finish a material transaction.
    private const int MaterialDatabaseVersion = 1; // Supported positive JSON schema revision; unknown revisions are never overwritten.
    private const string MaterialDatabaseMigratedKey = "materialDatabaseMigrated"; // true means this installation's legacy presets were imported; false or missing requests a non-overwriting merge.
    private const string MaterialUnsavedSuffix = " * (unsaved)"; // Dropdown suffix for material drafts; not part of the persisted material name.
    private const string SelectedMaterialKey = "selectedMaterial"; // Last named material preset; empty means unnamed custom settings.
    private const string PartSettingsSection = "vNest.PartSettings"; // Document user-text section keyed by part-group GUID or ungrouped boundary object GUID.
    private const string PartCustomDataSection = "vObjectPropertiesPlus.PartData"; // Opaque part-level custom text fields, keyed by the same persistent identity as nesting settings.
    private const string PartNamesSection = "vTools.PartNames"; // Display-name overrides keyed by part identity; drawing labels and group names remain unchanged.
    private const int PartDisplayNameLimit = 256; // Maximum display-name characters retained in a part override; longer records are ignored.
    private const int PartSettingsVersion = 1; // JSON schema version for document-local customized part settings.
    private const int MaximumPartSettingsCharacters = 16384; // Maximum stored JSON characters per part; rejects corrupt or unexpectedly large records.
    private const int MaximumPartCustomDataCharacters = 1048576; // Maximum custom-field JSON characters copied to a placed part; larger records remain on their source only.
    private const string CustomMaterialName = "Custom"; // Reserved dropdown placeholder for values not loaded from a named material preset.
    private const string MaterialPresetCopySuffix = " copy"; // Suggested Save As suffix when copying an existing preset; user can change the name.
    private const int TableNameAttempts = 10000; // Maximum numeric suffix trials for unique document group names.
    internal const string GroupPrefix = "vNest_"; // Output group prefix followed by session and table/part identifiers.
    internal const string MetadataPrefix = "vNest."; // Output metadata namespace for session, table, part, and rotation.
    private const string SourceObjectsKey = "vNest.sourceObjects"; // JSON array of source object GUIDs; identifies originals without depending on part numbers or group names.
    private const string SourceSettingsKey = "vNest.sourceSettings"; // Persistent source part identity: group-<GUID> or boundary-<GUID>.
    private const string TableFrameKey = "vNest.tableFrame"; // JSON array of material-plane origin, X axis and Y axis; nine world-coordinate numbers.
    private const string TableFrameSamplesKey = "vNest.tableFrameSamples"; // Three reference points on the table outline used to follow rigid movement of placed tables.
    private const string TableGapSection = "vNest.TableGaps"; // Document-local collision-gap overrides in model units, keyed by the persistent table-group GUID.
    private static readonly double[] TableFrameSampleFractions = [0,1.0/3,2.0/3]; // Normalized arc-length positions identifying a table's reference frame; three distinct samples.
    private const double LegacySourceMatchTolerance = 16.0; // Model-tolerance multiplier for unique legacy source length/area matching.
    private const double LegacySourceMatchRelativeTolerance = 1e-5; // Relative tolerance for legacy source length/area comparisons; ambiguous originals are never guessed.
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
    internal static bool ShowGap=DefaultShowGap;
    internal static NestWindow? ActiveWindow;
    private static string? _selectedMaterialName;
    private static bool _registeringRepeat;
    private static EventHandler? _repeatIdle;
    private static int _nextId;

    internal enum GrainMode { Auto,Force,On,Off }
    internal enum RotationMode { None,Any,Step45,Step90,Step180,Custom }
    internal enum TableStack { Horizontal,Vertical }
    private sealed record PoseKey(double Width,double Length,GrainMode Mode,RotationMode RotationChoice,double Step,bool Reverse,bool Obey,double Deviation,double? Guide,bool Manual,double Rotation,bool AllowFlip,bool? Flip,int FreePoseCount);
    internal sealed class Row(Part part,int id,Vector3d? guide) : IDisposable
    {
      internal Part Part { get; }=part;
      internal int Id { get; }=id;
      internal string Name { get; set; }=id.ToString(System.Globalization.CultureInfo.InvariantCulture);
      internal string DefaultName { get; set; }=id.ToString(System.Globalization.CultureInfo.InvariantCulture);
      internal Vector3d? Guide { get; set; }=guide;
      internal Line? DrawnGuide { get; set; } // User-drawn grain segment in original part coordinates; null keeps existing geometry/direction-only guides.
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
      internal string? MaterialName { get; set; } // null uses the window's current material; a name assigns this part to a separate material job.
      internal MaterialPreset? StoredMaterial { get; set; } // Document recipe fallback when the assigned global material preset no longer exists.
      internal double Rotation { get; set; }=DefaultRotation;
      internal int Table { get; set; }=DefaultTable;
      internal double X { get; set; }=DefaultCoordinate;
      internal double Y { get; set; }=DefaultCoordinate;
      internal string Status { get; set; }=string.Empty;
      internal int FreeRotationPoseLimit { get; set; }=FreePoseLimit; // Window-local search budget; never a saved part preference or a constraint on manual angles.
      private PoseKey? _poseKey;
      private List<NestPackingEngine.Pose>? _poses;
      private Point3d[]? _outlinePoints;
      private BoundingBox _previewBounds=BoundingBox.Unset; // Unset until the source content bounds are cached; includes lifted curves and annotations.
      internal BoundingBox PreviewBounds
      {
        get
        {
          if(_previewBounds.IsValid)return _previewBounds;
          _previewBounds=Part.Outline.GetBoundingBox(false);
          foreach(var item in Part.Items)_previewBounds.Union(item.Geometry.GetBoundingBox(false));
          return _previewBounds;
        }
      }
      private double _clearanceGap=double.NaN; // NaN marks an unbuilt source-space gap contour.
      private Polyline[] _clearance=[];
      internal IReadOnlyList<Polyline> Clearance(double gap)
      {
        if(_clearanceGap==gap)return _clearance;
        var contour=OutlinePoints().Select(point=>
        {Part.Plane.ClosestParameter(point,out double x,out double y);return new NestPackingEngine.ContourPoint(x,y);}).ToArray();
        _clearance=NestContourPackingEngine.ClearanceContours(contour,gap).Select(path=>
        {var line=new Polyline(path.Select(point=>Part.Plane.PointAt(point.X,point.Y)));if(line.Count>0)line.Add(line[0]);return line;}).ToArray();
        _clearanceGap=gap;return _clearance;
      }
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
        =>CachedMaterialPoses(guide,Width,GrainDeviation,create);
      internal List<NestPackingEngine.Pose> CachedMaterialPoses(double? guide,double materialWidth,double materialDeviation,Func<List<NestPackingEngine.Pose>> create)
        =>CachedRecipePoses(guide,ConfiguredMaterial(CustomMaterialName,materialWidth,PartGap,materialDeviation),create);
      internal List<NestPackingEngine.Pose> CachedRecipePoses(double? guide,MaterialPreset material,Func<List<NestPackingEngine.Pose>> create)
      {
        var key=new PoseKey(material.Width,Length,GrainOverride??material.Grain,RotationOverride??RotationChoice,RotationStepOverride??RotationStep,AllowReverseOverride??material.AllowReverse,ObeyGrainOverride??material.ObeyGrain,GrainDeviationOverride??material.GrainDeviation,guide,ManualRotation||Fixed,ManualRotation||Fixed?Rotation:DefaultRotation,AllowFlip,Fixed?Flipped:FlipOverride,FreeRotationPoseLimit);
        if(key!=_poseKey||_poses==null) { _poses=create(); _poseKey=key; }
        return _poses;
      }
      public void Dispose()=>Part.Dispose();
    }
    private sealed record Options(OptionDouble Width,OptionDouble Length,int GrainIndex,OptionDouble RotationStep,int WidthIndex,int LengthIndex,int RotationIndex,int StepIndex,OptionToggle Reverse,int ReverseIndex,int StackIndex,OptionToggle Flip,int FlipIndex,OptionDouble Search,int SearchIndex,OptionDouble Deviation,int DeviationIndex,OptionToggle Obey,int ObeyIndex,OptionDouble Gap,int GapIndex,OptionToggle ShowGap,int ShowGapIndex);

    internal static Result Run(RhinoDoc doc,RunMode mode)
    {
      if(_registeringRepeat) return Result.Success;
      var preselection=doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id).ToArray();
      if(ActiveWindow is {IsDisposed:false} existing)
      {
        if(existing.DocumentSerial==doc.RuntimeSerialNumber)
        { existing.AddPreselectedParts(preselection); existing.BringToFront(); existing.Focus(); return Result.Success; }
        existing.Close();
        if(ReferenceEquals(ActiveWindow,existing)) return Result.Cancel;
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

    internal static void RestoreRepeatTarget()
    {
      if(_repeatIdle!=null) RhinoApp.Idle-=_repeatIdle;
      Guid previous=Command.LastCommandId;
      _repeatIdle=(_,_)=>
      {
        if(Command.InCommand()||DeferredNativeCommand.IsDispatching) return;
        RhinoApp.Idle-=_repeatIdle; _repeatIdle=null;
        if(Command.LastCommandId!=previous) return;
        _registeringRepeat=true;
        try { _=RhinoApp.RunScript("_vNest",false); }
        finally {_registeringRepeat=false;}
      };
      RhinoApp.Idle+=_repeatIdle;
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
      int stackIndex=-1;
      var flip=new OptionToggle(AllowFlip,"No","Yes"); int flipIndex=getter.AddOptionToggle("Flip",ref flip);
      var search=new OptionDouble(SearchTime,0,MaximumSearchTime); int searchIndex=getter.AddOptionDouble("SearchTime",ref search);
      var gap=new OptionDouble(PartGap,DefaultPartGap,double.MaxValue); int gapIndex=getter.AddOptionDouble("Gap",ref gap);
      var showGap=new OptionToggle(ShowGap,"No","Yes");int showGapIndex=getter.AddOptionToggle("ShowGap",ref showGap);
      getter.AcceptNumber(true,false);
      return new Options(width,length,grain,step,widthIndex,lengthIndex,rotationIndex,stepIndex,reverse,reverseIndex,stackIndex,flip,flipIndex,search,searchIndex,deviation,deviationIndex,obey,obeyIndex,gap,gapIndex,showGap,showGapIndex);
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
      if(index==options.FlipIndex) AllowFlip=options.Flip.CurrentValue;
      if(index==options.SearchIndex) SearchTime=options.Search.CurrentValue;
      if(index==options.GapIndex) PartGap=options.Gap.CurrentValue;
      if(index==options.ShowGapIndex)ShowGap=options.ShowGap.CurrentValue;
      return true;
    }

    internal static void Load()=>ToolsOptionStore.Read<int>(Section,section=>
    {
      Width=ToolsOptionStore.TryGetDouble(section,"width",out var width)&&double.IsFinite(width)&&width>0?width:DefaultWidth;
      Length=ToolsOptionStore.TryGetDouble(section,"length",out var length)&&double.IsFinite(length)&&length>0?length:DefaultLength;
      PartGap=ToolsOptionStore.TryGetDouble(section,"gap",out var gap)&&double.IsFinite(gap)&&gap>=DefaultPartGap?gap:DefaultPartGap;
      ShowGap=ToolsOptionStore.TryGetBool(section,"showGap",out var showGap)?showGap:DefaultShowGap;
      RotationChoice=ToolsOptionStore.TryGetString(section,"rotationMode",out var rotation)&&Enum.TryParse<RotationMode>(rotation,true,out var choice)&&Enum.IsDefined(choice)?choice:DefaultRotationMode;
      RotationStep=ToolsOptionStore.TryGetDouble(section,"rotationStep",out var step)&&double.IsFinite(step)&&step>=MinimumRotationStep&&step<=MaximumRotationStep?step:DefaultRotationStep;
      AllowReverse=ToolsOptionStore.TryGetBool(section,"allowReverse",out var reverse)?reverse:DefaultAllowReverse;
      ObeyGrain=ToolsOptionStore.TryGetBool(section,"obeyGrain",out var obey)?obey:DefaultObeyGrain;
      GrainDeviation=ToolsOptionStore.TryGetDouble(section,"grainDeviation",out var deviation)&&double.IsFinite(deviation)&&deviation>=DefaultGrainDeviation&&deviation<=MaximumGrainDeviation?deviation:DefaultGrainDeviation;
      Grain=ToolsOptionStore.TryGetString(section,"grain",out var grain)&&Enum.TryParse<GrainMode>(grain,true,out var parsed)&&Enum.IsDefined(parsed)?parsed:DefaultGrain;
      Stack=DefaultStack;
      AllowFlip=ToolsOptionStore.TryGetBool(section,"flip",out var flip)?flip:DefaultAllowFlip;
      SearchTime=ToolsOptionStore.TryGetDouble(section,"searchTime",out var search)&&double.IsFinite(search)&&search>=0&&search<=MaximumSearchTime?search:DefaultSearchTime;
      _selectedMaterialName=ToolsOptionStore.TryGetString(section,SelectedMaterialKey,out var selected)&&!string.IsNullOrWhiteSpace(selected)?selected:null;
      return 0;
    });
    internal static void Save()=>ToolsOptionStore.Update(Section,section=>
    {
      // Freeze old presets' inherited grain settings before changing the saved global fallback.
      if(section[MaterialPresetsKey] is System.Text.Json.Nodes.JsonArray)
        section[MaterialPresetsKey]=MaterialPresetsJson(ReadMaterialPresets(section));
      section["width"]=Width; section["length"]=Length; section["gap"]=PartGap; section["grain"]=Grain.ToString(); section["rotationMode"]=RotationChoice.ToString(); section["rotationStep"]=RotationStep; section["allowReverse"]=AllowReverse; section["obeyGrain"]=ObeyGrain; section["grainDeviation"]=GrainDeviation; section["stack"]=Stack.ToString(); section["flip"]=AllowFlip; section["searchTime"]=SearchTime; section[SelectedMaterialKey]=_selectedMaterialName??string.Empty;
      section["showGap"]=ShowGap;
    });

    internal sealed record MaterialPreset(string Name,double Width,double Gap,double GrainDeviation=DefaultGrainDeviation)
    {
      public GrainMode Grain { get; init; }=DefaultGrain; // Auto, Force, On or Off, inherited by parts without a mode override.
      public bool ObeyGrain { get; init; }=DefaultObeyGrain; // true constrains inherited grain; false permits free-rotation rules.
      public bool AllowReverse { get; init; }=DefaultAllowReverse; // true permits opposite grain alignment; false only permits forward alignment.
    }
    private static MaterialPreset ConfiguredMaterial(string name,double width,double gap,double deviation)=>
      new(name,width,gap,deviation) {Grain=Grain,ObeyGrain=ObeyGrain,AllowReverse=AllowReverse};
    private static System.Text.Json.Nodes.JsonArray MaterialPresetsJson(IEnumerable<MaterialPreset> presets)=>
      new(presets.Select(preset=>(System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {["name"]=preset.Name,["width"]=preset.Width,["gap"]=preset.Gap,["grainDeviation"]=preset.GrainDeviation,
          ["grain"]=preset.Grain.ToString(),["obeyGrain"]=preset.ObeyGrain,["allowReverse"]=preset.AllowReverse}).ToArray());
    private static string MaterialPresetName(string name)=>System.Text.RegularExpressions.Regex.Replace(name,@"\s+"," ").Trim();
    private static List<MaterialPreset> ReadMaterialPresets(System.Text.Json.Nodes.JsonObject? section)
      =>ReadMaterialRecords(section,MaterialPresetsKey);
    private static List<MaterialPreset> ReadMaterialRecords(System.Text.Json.Nodes.JsonObject? section,string key)
    {
      var result=new List<MaterialPreset>();
      if(section?[key] is not System.Text.Json.Nodes.JsonArray presets) return result;
      var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var legacyGrain=ToolsOptionStore.TryGetString(section,"grain",out var legacyMode)&&Enum.TryParse<GrainMode>(legacyMode,true,out var legacyParsed)&&Enum.IsDefined(legacyParsed)?legacyParsed:DefaultGrain;
      bool legacyObey=ToolsOptionStore.TryGetBool(section,"obeyGrain",out var legacyObeyValue)?legacyObeyValue:DefaultObeyGrain;
      bool legacyReverse=ToolsOptionStore.TryGetBool(section,"allowReverse",out var legacyReverseValue)?legacyReverseValue:DefaultAllowReverse;
      foreach(var node in presets)
      {
        if(node is not System.Text.Json.Nodes.JsonObject preset||!ToolsOptionStore.TryGetString(preset,"name",out var name)) continue;
        name=MaterialPresetName(name);
        if(name.Length==0||name.Length>MaterialPresetNameLimit||name.Equals(CustomMaterialName,StringComparison.OrdinalIgnoreCase)||
          !ToolsOptionStore.TryGetDouble(preset,"width",out var width)||!double.IsFinite(width)||width<=0||
          !ToolsOptionStore.TryGetDouble(preset,"gap",out var gap)||!double.IsFinite(gap)||gap<DefaultPartGap||!names.Add(name)) continue;
        double deviation=DefaultGrainDeviation;
        if(preset.ContainsKey("grainDeviation")&&(!ToolsOptionStore.TryGetDouble(preset,"grainDeviation",out deviation)||
          !double.IsFinite(deviation)||deviation<DefaultGrainDeviation||deviation>MaximumGrainDeviation)) continue;
        var mode=legacyGrain;bool obey=legacyObey,reverse=legacyReverse;
        if(preset.ContainsKey("grain")&&(!ToolsOptionStore.TryGetString(preset,"grain",out var grain)||!Enum.TryParse(grain,true,out mode)||!Enum.IsDefined(mode))) continue;
        if(preset.ContainsKey("obeyGrain")&&!ToolsOptionStore.TryGetBool(preset,"obeyGrain",out obey)) continue;
        if(preset.ContainsKey("allowReverse")&&!ToolsOptionStore.TryGetBool(preset,"allowReverse",out reverse)) continue;
        result.Add(new MaterialPreset(name,width,gap,deviation) {Grain=mode,ObeyGrain=obey,AllowReverse=reverse});
      }
      return result.OrderBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static List<MaterialPreset> LoadMaterialPresets()=>MaterialDatabase.Read(ReadMaterialPresets);
    private static List<MaterialPreset> LoadMaterialDrafts()=>MaterialDatabase.Read(section=>ReadMaterialRecords(section,MaterialDraftsKey));
    private static bool SaveMaterialDrafts(IEnumerable<MaterialPreset> drafts,IEnumerable<MaterialPreset> baseline)
    {
      var current=drafts.ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
      var previous=baseline.ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
      var changed=current.Values.Where(preset=>!previous.TryGetValue(preset.Name,out var old)||preset!=old).ToArray();
      var removed=previous.Keys.Except(current.Keys,StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
      if(changed.Length==0&&removed.Count==0)return true;
      bool saved=MaterialDatabase.Update(section=>
      {
        var stored=ReadMaterialRecords(section,MaterialDraftsKey).Where(preset=>!removed.Contains(preset.Name))
          .ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
        foreach(var preset in changed)stored[preset.Name]=preset;
        section[MaterialDraftsKey]=MaterialPresetsJson(stored.Values);
      },out var error);
      if(!saved)Log.Write("vNest","could not retain unsaved material edits: {0}",error);
      return saved;
    }
    private static bool SaveMaterialPreset(string name,double width,double gap,bool overwrite,out string error)
      =>SaveMaterialValues(ConfiguredMaterial(name,width,gap,GrainDeviation),overwrite,out error);
    private static bool SaveMaterialValues(MaterialPreset values,bool overwrite,out string error)
    {
      string name=MaterialPresetName(values.Name); double width=values.Width,gap=values.Gap;
      error=string.Empty;
      if(name.Length==0||name.Length>MaterialPresetNameLimit||name.Equals(CustomMaterialName,StringComparison.OrdinalIgnoreCase))
      { error=$"Use a material name of 1-{MaterialPresetNameLimit} characters other than {CustomMaterialName}."; return false; }
      if(!double.IsFinite(width)||width<=0||!double.IsFinite(gap)||gap<DefaultPartGap||
        !double.IsFinite(values.GrainDeviation)||values.GrainDeviation<DefaultGrainDeviation||values.GrainDeviation>MaximumGrainDeviation||!Enum.IsDefined(values.Grain))
      { error="Width must be positive, Gap nonnegative, and grain deviation between 0 and 90 degrees."; return false; }
      bool saved=MaterialDatabase.Update(section=>
      {
        var presets=ReadMaterialPresets(section);
        int index=presets.FindIndex(preset=>preset.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        if(index<0&&overwrite) throw new InvalidOperationException("The selected material preset no longer exists. Use Save As instead.");
        if(index>=0&&!overwrite) throw new InvalidOperationException("That material name already exists. Choose another name or overwrite the selected preset.");
        if(index<0) presets.Add(values with {Name=name});
        else presets[index]=values with {Name=presets[index].Name};
        section[MaterialPresetsKey]=MaterialPresetsJson(presets);
        section[MaterialDraftsKey]=MaterialPresetsJson(ReadMaterialRecords(section,MaterialDraftsKey)
          .Where(draft=>!draft.Name.Equals(name,StringComparison.OrdinalIgnoreCase)));
      },out error);
      if(!saved)
        Log.Write("vNest","material preset save failed: {0}",error);
      return saved;
    }

    private static class MaterialDatabase
    {
      private static readonly System.Text.Json.JsonSerializerOptions JsonOptions=new() {WriteIndented=true,
        TypeInfoResolver=new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()};
      private static string DatabasePath=>System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        MaterialDatabaseFolder,MaterialDatabaseFileName);

      internal static T Read<T>(Func<System.Text.Json.Nodes.JsonObject?,T> reader)
      {
        try {return Access(DatabasePath,reader,false);}
        catch(Exception ex)
        {
          Log.Write("vNest","global material library read failed: {0}",ex.Message);
          return ToolsOptionStore.Read(Section,reader);
        }
      }
      internal static bool Update(Action<System.Text.Json.Nodes.JsonObject> updater,out string error)
      {
        try {Access(DatabasePath,section=>{updater(section!);return true;},true);error=string.Empty;return true;}
        catch(Exception ex) {error=ex.Message;return false;}
      }
      private static T Access<T>(string path,Func<System.Text.Json.Nodes.JsonObject?,T> action,bool write)
      {
        using var mutex=new Mutex(false,MaterialDatabaseMutexName);bool held=false;
        try
        {
          try {held=mutex.WaitOne(MaterialDatabaseLockMilliseconds);}
          catch(AbandonedMutexException) {held=true;}
          if(!held)throw new System.IO.IOException("The material library is busy in another Rhino session. Try again.");
          bool exists=System.IO.File.Exists(path);
          var root=exists?System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
            ??throw new System.IO.InvalidDataException("The material library must contain a JSON object."):new System.Text.Json.Nodes.JsonObject();
          if(exists&&(!ToolsOptionStore.TryGetDouble(root,"version",out double version)||version!=MaterialDatabaseVersion))
            throw new System.IO.InvalidDataException("The material library version is not supported; the file was left unchanged.");
          var legacy=ToolsOptionStore.Read(Section,section=>section==null?null:
            System.Text.Json.Nodes.JsonNode.Parse(section.ToJsonString()) as System.Text.Json.Nodes.JsonObject);
          bool migrate=legacy!=null&&(!ToolsOptionStore.TryGetBool(legacy,MaterialDatabaseMigratedKey,out bool migrated)||!migrated);
          if(!exists||migrate)
          {
            foreach(string key in new[]{MaterialPresetsKey,MaterialDraftsKey})
              root[key]=MaterialPresetsJson(ReadMaterialRecords(root,key).Concat(ReadMaterialRecords(legacy,key))
                .DistinctBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase));
            root["version"]=MaterialDatabaseVersion;
          }
          T result=action(root);
          if(write||!exists||migrate)Save(path,root);
          // Mark only after a durable import; retain the legacy arrays as a recovery copy.
          if(migrate&&!ToolsOptionStore.Update(Section,section=>section[MaterialDatabaseMigratedKey]=true))
            Log.Write("vNest","material library migrated, but the legacy import marker could not be saved: {0}",ToolsOptionStore.LastError);
          return result;
        }
        finally {if(held)mutex.ReleaseMutex();}
      }
      private static void Save(string path,System.Text.Json.Nodes.JsonObject root)
      {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
          byte[] json=System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(JsonOptions));
          using(var stream=new System.IO.FileStream(temporary,System.IO.FileMode.CreateNew,System.IO.FileAccess.Write,System.IO.FileShare.None))
          {stream.Write(json);stream.Flush(true);}
          if(System.IO.File.Exists(path))System.IO.File.Replace(temporary,path,path+".bak");
          else System.IO.File.Move(temporary,path);
        }
        finally {if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
      }
    }

    internal static Row CreateRow(RhinoDoc doc,Part part)
    {
      EnsureCutFootprint(doc,part);
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
      var labels=part.Items.Where(item=>item.Geometry is TextEntity text&&!string.IsNullOrWhiteSpace(text.PlainText)&&IsPartNameLabel(item.Attributes))
        .OrderByDescending(item=>item.Attributes.LayerIndex>=0&&item.Attributes.LayerIndex<doc.Layers.Count&&
          string.Equals(doc.Layers[item.Attributes.LayerIndex].FullPath.Split("::").Last(),NameLayer,StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(item=> { var bounds=item.Geometry.GetBoundingBox(part.Plane); return (bounds.Max.X-bounds.Min.X)*(bounds.Max.Y-bounds.Min.Y); });
      if(labels.FirstOrDefault()?.Geometry is TextEntity label)
        row.Name=System.Text.RegularExpressions.Regex.Replace(label.PlainText,@"\s+"," ").Trim();
      row.DefaultName=row.Name;
      LoadPartName(doc,row);
      LoadPartSettings(doc,row);
      Log.Write("vNest","part id={0} name={1} grain_object={2} grain_angle={3}",row.Id,row.Name,guideId,
        guide.HasValue?RhinoMath.ToDegrees(Math.Atan2(guide.Value*part.Plane.YAxis,guide.Value*part.Plane.XAxis)).ToString("G17",System.Globalization.CultureInfo.InvariantCulture):"missing");
      return row;
    }

    internal static bool IsPartNameLabel(ObjectAttributes attributes)=>
      !string.Equals(attributes.Name,NotchLabelName,StringComparison.OrdinalIgnoreCase)&&
      !string.Equals(attributes.GetUserString(NotchRoleKey),"label",StringComparison.OrdinalIgnoreCase)&&
      string.IsNullOrEmpty(attributes.GetUserString(NotchLabelIdKey));

    private static (Guid[] Objects,int[] Tables) OriginalTableSelection(RhinoDoc doc,IReadOnlyCollection<Guid> selected)
    {
      var tableGroups=NestTableMetadata.TableGroups(doc);
      var tables=selected.Select(doc.Objects.FindId).Where(obj=>obj!=null&&NestTableMetadata.IsDecoration(doc,obj))
        .SelectMany(obj=>obj!.Attributes.GetGroupList()??[]).Where(tableGroups.Contains).Distinct().ToArray();
      if(tables.Length==0)return (selected.ToArray(),tables);
      var tableMembers=tables.SelectMany(table=>doc.Groups.GroupMembers(table)).DistinctBy(obj=>obj.Id).ToArray();
      var result=selected.Except(tableMembers.Select(obj=>obj.Id)).ToHashSet();
      List<Part>? originals=null;int missing=0;
      string? Name(Part part)
      {
        var probe=new Row(part,0,null);LoadPartName(doc,probe);
        if(probe.Name!="0")return probe.Name;
        return part.Items.Where(item=>item.Geometry is TextEntity text&&!string.IsNullOrWhiteSpace(text.PlainText)&&IsPartNameLabel(item.Attributes))
          .OrderByDescending(item=>((TextEntity)item.Geometry).TextHeight).Select(item=>System.Text.RegularExpressions.Regex.Replace(((TextEntity)item.Geometry).PlainText,@"\s+"," ").Trim()).FirstOrDefault();
      }
      bool Same(Part source,Part placed)
      {
        if(Name(source)!=Name(placed))return false;
        double a=source.Outline.GetLength(),b=placed.Outline.GetLength();
        if(Math.Abs(a-b)>Math.Max(doc.ModelAbsoluteTolerance*LegacySourceMatchTolerance,Math.Max(a,b)*LegacySourceMatchRelativeTolerance))return false;
        using var first=AreaMassProperties.Compute(source.Outline);using var second=AreaMassProperties.Compute(placed.Outline);
        return first!=null&&second!=null&&Math.Abs(first.Area-second.Area)<=Math.Max(doc.ModelAbsoluteTolerance*Math.Max(a,b)*LegacySourceMatchTolerance,Math.Max(first.Area,second.Area)*LegacySourceMatchRelativeTolerance);
      }
      try
      {
        var parts=PlanarPartSelection.Capture(doc,tableMembers.Where(obj=>!NestTableMetadata.IsDecoration(doc,obj)).Select(obj=>obj.Id).ToArray(),doc.ModelAbsoluteTolerance,requirePlanarDetails:false);
        try
        {
          foreach(var part in parts)
          {
            Guid[] ids=[];
            if(part.Items.Select(item=>item.Attributes.GetUserString(SourceSettingsKey)).FirstOrDefault(value=>!string.IsNullOrEmpty(value)) is {} key&&key.StartsWith("group-",StringComparison.Ordinal)&&Guid.TryParse(key[6..],out var groupId)&&
              doc.Groups.FirstOrDefault(group=>!group.IsDeleted&&group.Id==groupId) is {} group)
              ids=doc.Groups.GroupMembers(group.Index).Where(obj=>!obj.IsDeleted&&!NestTableMetadata.IsDecoration(doc,obj)).Select(obj=>obj.Id).ToArray();
            if(ids.Length==0)
            {
              var json=part.Items.Select(item=>item.Attributes.GetUserString(SourceObjectsKey)).FirstOrDefault(value=>!string.IsNullOrEmpty(value));
              if(json!=null)
              {try{ids=(System.Text.Json.JsonSerializer.Deserialize<Guid[]>(json)??[]).Where(id=>doc.Objects.FindId(id) is {IsDeleted:false}).ToArray();}catch(System.Text.Json.JsonException ex){Log.Write("vNest","invalid original-part references: {0}",ex.Message);}}
            }
            if(ids.Length==0)
            {
              originals??=PlanarPartSelection.Capture(doc,doc.Objects.GetObjectList(ObjectType.Curve|ObjectType.Annotation|ObjectType.TextDot|ObjectType.Point)
                .Where(obj=>!obj.IsDeleted&&!obj.IsHidden&&string.IsNullOrEmpty(obj.Attributes.GetUserString(MetadataPrefix+"session"))).Select(obj=>obj.Id).ToArray(),doc.ModelAbsoluteTolerance,requirePlanarDetails:false);
              EnsureCutFootprint(doc,part);
              foreach(var candidate in originals)EnsureCutFootprint(doc,candidate);
              var matches=originals.Where(candidate=>Same(candidate,part)).Take(2).ToArray();
              if(matches.Length==1)ids=matches[0].Items.Select(item=>item.Id).ToArray();
            }
            if(ids.Length==0)missing++;else result.UnionWith(ids);
          }
        }
        finally{foreach(var part in parts)part.Dispose();}
      }
      finally{if(originals!=null)foreach(var part in originals)part.Dispose();}
      if(missing>0)RhinoApp.WriteLine($"vNest: original sources could not be resolved uniquely for {missing} placed part(s); their existing copies remain editable.");
      return (result.ToArray(),tables);
    }

    internal static string GrainGuideSource(Row row,GrainMode mode)=>!row.Guide.HasValue
      ?mode==GrainMode.On?SourceGrainLabel:MissingGrainLabel
      :row.DrawnGuide.HasValue||row.Guide!=row.DetectedGuide?SetGrainLabel:DetectedGrainLabel;

    internal static double TableAllowanceForUnits(UnitSystem units)=>units is UnitSystem.None or UnitSystem.CustomUnits
      ?NestPackingEngine.DefaultTableAllowance:TableAllowanceInches*RhinoMath.UnitScale(UnitSystem.Inches,units);

    internal static void EnsureCutFootprint(RhinoDoc doc,Part part)
    {
      string Layer(ObjectAttributes attributes)=>attributes.LayerIndex>=0&&attributes.LayerIndex<doc.Layers.Count
        ?doc.Layers[attributes.LayerIndex].FullPath.Split("::").Last():string.Empty;
      var curves=part.Items.Where(item=>item.Geometry is Curve&&Layer(item.Attributes).StartsWith(CutLayerPrefix,StringComparison.OrdinalIgnoreCase))
        .Select(item=>(Curve)item.Geometry).ToArray();
      if(curves.Length==0) curves=part.Items.Where(item=>item.Geometry is Curve&&item.Attributes.LayerIndex==part.Attributes.LayerIndex)
        .Select(item=>(Curve)item.Geometry).ToArray();
      if(curves.Length==0) return;
      double tolerance=doc.ModelAbsoluteTolerance;
      Point3d[] Samples(Curve curve)
      {
        if(curve.TryGetPolyline(out var points)) return points.ToArray();
        using var approximation=curve.ToPolyline(OutlineTolerance,OutlineAngleTolerance,0,0);
        return approximation!=null&&approximation.TryGetPolyline(out points)?points.ToArray():[];
      }
      var samples=curves.Append(part.Outline).SelectMany(Samples).Select(part.Plane.ClosestPoint).ToArray();
      if(samples.Length<3) throw new InvalidOperationException("Could not determine the part's cutting footprint.");
      bool Covers(Curve boundary)=>samples.All(point=>boundary.Contains(point,part.Plane,tolerance) is PointContainment.Inside or PointContainment.Coincident);
      if(Covers(part.Outline)) return;
      var joined=Curve.JoinCurves(curves,tolerance);
      try
      {
        double Area(Curve curve) { using var mass=AreaMassProperties.Compute(curve); return mass?.Area??double.PositiveInfinity; }
        var boundary=joined.Where(curve=>curve.IsClosed&&Covers(curve)).OrderBy(Area).FirstOrDefault();
        if(boundary!=null)
        {
          double previous=Area(part.Outline),updated=Area(boundary);
          part.ReplaceOutline(boundary.DuplicateCurve());
          Log.Write("vNest","cut footprint rebuilt from joined members: area={0:G17} -> {1:G17}",previous,updated);
          return;
        }
        // Broken/open cutting perimeters must not leave exterior geometry outside the collision footprint.
        var points=samples.Select(point=> {part.Plane.ClosestParameter(point,out double x,out double y);return new Point2d(x,y);})
          .Distinct().OrderBy(point=>point.X).ThenBy(point=>point.Y).ToArray();
        double Cross(Point2d a,Point2d b,Point2d c)=>(b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        var hull=new List<Point2d>();
        foreach(var point in points) {while(hull.Count>=2&&Cross(hull[^2],hull[^1],point)<=0)hull.RemoveAt(hull.Count-1);hull.Add(point);}
        int lower=hull.Count;
        foreach(var point in Enumerable.Reverse(points).Skip(1)) {while(hull.Count>lower&&Cross(hull[^2],hull[^1],point)<=0)hull.RemoveAt(hull.Count-1);hull.Add(point);}
        if(hull.Count<4) throw new InvalidOperationException("The part's cutting footprint is degenerate.");
        var replacement=new PolylineCurve(hull.Select(point=>part.Plane.PointAt(point.X,point.Y)));
        part.ReplaceOutline(replacement);
        Log.Write("vNest","open cutting perimeter: using conservative outline around {0} cutting curves",curves.Length);
      }
      finally {foreach(var curve in joined)curve.Dispose();}
    }

    private sealed record StoredPartSettings(int Version,int Copies,bool Included,bool Fixed,bool ManualRotation,bool Flipped,
      bool? Flip,GrainMode? Grain,RotationMode? Rotation,double? Step,bool? Obey,bool? Reverse,double? Deviation,
      double Angle,int Table,double X,double Y,string? Material,MaterialPreset? Recipe,double[]? Guide,double[]? GuideFrom,double[]? GuideTo);

    private static string? PartSettingsKey(RhinoDoc doc,Row row)
    {
      var ids=row.Part.Items.Select(item=>item.Id).ToHashSet();
      var boundary=doc.Objects.FindId(row.Part.Attributes.ObjectId);
      if(boundary is not {IsDeleted:false}) return null;
      var groups=(boundary?.Attributes.GetGroupList()??row.Part.Attributes.GetGroupList()??[])
        .Where(index=>doc.Groups[index] is {IsDeleted:false})
        .OrderBy(index=>doc.Groups.GroupObjectCount(index));
      foreach(var index in groups)
      {
        var members=doc.Groups.GroupMembers(index);
        if(members.Length>0&&members.All(member=>ids.Contains(member.Id))) return "group-"+doc.Groups[index].Id.ToString("N");
      }
      return row.Part.Attributes.ObjectId==Guid.Empty?null:"boundary-"+row.Part.Attributes.ObjectId.ToString("N");
    }

    private static Plane PartSettingsFrame(Row row)
    {
      var x=row.Part.Outline.TangentAtStart;
      x-=row.Part.Plane.Normal*(x*row.Part.Plane.Normal);
      if(!x.Unitize()) x=row.Part.Plane.XAxis;
      return new Plane(row.Part.Outline.PointAtStart,x,Vector3d.CrossProduct(row.Part.Plane.Normal,x));
    }

    private static void LoadPartName(RhinoDoc doc,Row row)
    {
      string? name=PartSettingsKey(doc,row) is {} key?doc.Strings.GetValue(PartNamesSection,key):null;
      row.Name=!string.IsNullOrWhiteSpace(name)&&name.Length<=PartDisplayNameLimit?name:row.DefaultName;
    }

    private static StoredPartSettings CapturePartSettings(Row row,MaterialPreset? recipe)
    {
      var frame=PartSettingsFrame(row);
      double[] Direction(Vector3d value)=>[value*frame.XAxis,value*frame.YAxis,value*frame.Normal];
      double[] Point(Point3d value)=>Direction(value-frame.Origin);
      return new(PartSettingsVersion,row.Copies,row.Included,row.Fixed,row.ManualRotation,row.Flipped,row.FlipOverride,
        row.GrainOverride,row.RotationOverride,row.RotationStepOverride,row.ObeyGrainOverride,row.AllowReverseOverride,row.GrainDeviationOverride,
        row.ManualRotation||row.Fixed?row.Rotation:DefaultRotation,row.Fixed?row.Table:DefaultTable,
        row.Fixed?row.X:DefaultCoordinate,row.Fixed?row.Y:DefaultCoordinate,row.MaterialName,recipe,
        row.Guide!=row.DetectedGuide&&row.Guide is {} guide?Direction(guide):null,
        row.DrawnGuide is {} line?Point(line.From):null,row.DrawnGuide is {} segment?Point(segment.To):null);
    }

    private static void WritePartSettings(RhinoDoc doc,string key,StoredPartSettings? data)
    {
      string? json=data==null?null:System.Text.Json.JsonSerializer.Serialize(data);
      if(doc.Strings.GetValue(PartSettingsSection,key)==json) return;
      if(json==null) doc.Strings.Delete(PartSettingsSection,key);
      else doc.Strings.SetString(PartSettingsSection,key,json);
      doc.Modified=true;
    }

    private static bool IsPlacedPart(RhinoDoc doc,Row row)
    {
      var attributes=doc.Objects.FindId(row.Part.Attributes.ObjectId)?.Attributes??row.Part.Attributes;
      return !string.IsNullOrEmpty(attributes.GetUserString(MetadataPrefix+"session"))&&
        !string.IsNullOrEmpty(attributes.GetUserString(MetadataPrefix+"part"));
    }

    private static StoredPartSettings ReusablePartSettings(StoredPartSettings data)=>data with
    {Fixed=DefaultFixed,ManualRotation=DefaultManualRotation,Flipped=DefaultAllowFlip,
      Angle=DefaultRotation,Table=DefaultTable,X=DefaultCoordinate,Y=DefaultCoordinate};

    private static bool LoadPartSettings(RhinoDoc doc,Row row)
    {
      var key=PartSettingsKey(doc,row);
      if(key==null) return false;
      string? json=doc.Strings.GetValue(PartSettingsSection,key);
      if(string.IsNullOrEmpty(json)) return true;
      try
      {
        if(json.Length>MaximumPartSettingsCharacters) throw new InvalidOperationException("Oversized part settings.");
        var data=System.Text.Json.JsonSerializer.Deserialize<StoredPartSettings>(json);
        if(data==null||data.Version!=PartSettingsVersion) return false;
        if(!IsPlacedPart(doc,row))data=ReusablePartSettings(data);
        bool VectorValid(double[]? value)=>value==null||value.Length==3&&value.All(double.IsFinite);
        if(data.Copies<DefaultCopies||data.Copies>MaximumCopies||data.Table<DefaultTable||data.Table>=NestPackingEngine.MaximumTables||
          !double.IsFinite(data.Angle)||!double.IsFinite(data.X)||!double.IsFinite(data.Y)||
          data.Step is {} step&&(!double.IsFinite(step)||step<MinimumRotationStep||step>MaximumRotationStep)||
          data.Deviation is {} deviation&&(!double.IsFinite(deviation)||deviation<DefaultGrainDeviation||deviation>MaximumGrainDeviation)||
          data.Grain.HasValue&&!Enum.IsDefined(data.Grain.Value)||data.Rotation.HasValue&&!Enum.IsDefined(data.Rotation.Value)||
          !VectorValid(data.Guide)||!VectorValid(data.GuideFrom)||!VectorValid(data.GuideTo))
          throw new InvalidOperationException("Invalid part settings.");
        var frame=PartSettingsFrame(row);
        Vector3d Direction(double[] value)=>frame.XAxis*value[0]+frame.YAxis*value[1]+frame.Normal*value[2];
        row.Copies=data.Copies;row.Included=data.Included;row.Fixed=data.Fixed;row.ManualRotation=data.ManualRotation;
        row.Flipped=data.Fixed?data.Flipped:data.Flip??DefaultAllowFlip;row.FlipOverride=data.Flip;
        row.GrainOverride=data.Grain;row.RotationOverride=data.Rotation;row.RotationStepOverride=data.Step;
        row.ObeyGrainOverride=data.Obey;row.AllowReverseOverride=data.Reverse;row.GrainDeviationOverride=data.Deviation;
        row.Rotation=data.Angle;row.Table=data.Table;row.X=data.X;row.Y=data.Y;
        row.MaterialName=string.IsNullOrWhiteSpace(data.Material)?null:MaterialPresetName(data.Material);
        if(data.Recipe is {} material&&material.Name==row.MaterialName&&double.IsFinite(material.Width)&&material.Width>0&&
          double.IsFinite(material.Gap)&&material.Gap>=DefaultPartGap&&double.IsFinite(material.GrainDeviation)&&
          material.GrainDeviation>=DefaultGrainDeviation&&material.GrainDeviation<=MaximumGrainDeviation&&Enum.IsDefined(material.Grain))
        {
          var recipe=System.Text.Json.Nodes.JsonNode.Parse(json)?["Recipe"];
          row.StoredMaterial=material with {Grain=recipe?["Grain"]==null?Grain:material.Grain,
            ObeyGrain=recipe?["ObeyGrain"]==null?ObeyGrain:material.ObeyGrain,AllowReverse=recipe?["AllowReverse"]==null?AllowReverse:material.AllowReverse};
        }
        if(data.Guide is {} vector)
        { var direction=Direction(vector);if(direction.Unitize()) row.Guide=direction; }
        if(data.GuideFrom is {} from&&data.GuideTo is {} to)
        { var line=new Line(frame.Origin+Direction(from),frame.Origin+Direction(to));if(line.IsValid&&line.Length>RhinoMath.ZeroTolerance) row.DrawnGuide=line; }
        Log.Write("vNest","restored part settings name={0} key={1}",row.Name,key);
        return true;
      }
      catch(Exception ex) {Log.Write("vNest","ignored invalid saved part settings name={0}: {1}",row.Name,ex.Message);return false;}
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
        Vector3d direction; Line? drawn=null;
        var result=getter.Get();
        if(result==GetResult.Option&&getter.OptionIndex()==draw)
        {
          using var start=new GetPoint(); start.SetCommandPrompt("Grain guide start"); start.PermitObjectSnap(GrainObjectSnaps);
          if(start.Get()!=GetResult.Point) return false;
          using var end=new GetPoint(); end.SetCommandPrompt("Grain guide end"); end.PermitObjectSnap(GrainObjectSnaps);
          end.SetBasePoint(start.Point(),true); end.DrawLineFromPoint(start.Point(),true);
          if(end.Get()!=GetResult.Point) return false;
          drawn=new Line(start.Point(),end.Point());
          direction=drawn.Value.Direction;
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
        row.Guide=direction; row.DrawnGuide=drawn; row.ManualRotation=false;
        if(row.GrainOverride==GrainMode.Off) row.GrainOverride=GrainMode.Auto;
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
      =>MaterialPoses(row,ConfiguredMaterial(CustomMaterialName,Width,PartGap,GrainDeviation));
    internal static int RotationSearchLimit(Row row,int copies)=>copies<=SmallNestCopyLimit
      ?Math.Clamp(SmallNestPoseVertexBudget/Math.Max(1,row.OutlinePoints().Count),FreePoseLimit,SmallNestFreePoseLimit)/2*2:FreePoseLimit;
    internal static List<NestPackingEngine.Pose> MaterialPoses(Row row,MaterialPreset material)
    {
      double? guide=GuideAngle(row);
      return row.CachedRecipePoses(guide,material,()=>FindMaterialPoses(row,guide,material.Width,material.GrainDeviation,material.Grain,material.ObeyGrain,material.AllowReverse));
    }

    private static List<NestPackingEngine.Pose> FindPoses(Row row,double? guide)
      =>FindMaterialPoses(row,guide,Width,GrainDeviation,Grain,ObeyGrain,AllowReverse);
    private static List<NestPackingEngine.Pose> FindMaterialPoses(Row row,double? guide,double materialWidth,double materialDeviation,
      GrainMode materialGrain=DefaultGrain,bool materialObey=DefaultObeyGrain,bool materialReverse=DefaultAllowReverse)
    {
      var mode=row.GrainOverride??materialGrain;
      var choice=row.RotationOverride??RotationChoice; double step=row.RotationStepOverride??RotationStep;
      bool obey=row.ObeyGrainOverride??materialObey,reverse=row.AllowReverseOverride??materialReverse;
      double deviation=row.GrainDeviationOverride??materialDeviation;
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
      double FitScore(NestPackingEngine.Pose pose)=>Math.Max(pose.Width/materialWidth,pose.Length/Length);
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
      foreach(var candidate in candidates.Values.OrderByDescending(pose=>pose.Width<=materialWidth&&pose.Length<=Length).ThenBy(pose=>pose.Width*pose.Length).ThenBy(pose=>Math.Abs(pose.Rotation)))
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
          if(paired.Count>=row.FreeRotationPoseLimit) break;
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

    internal static Vector3d TableOffset(Plane plane,int table)=>-plane.YAxis*(table*(Width+TableGap));

    internal static int PreviewTableCount(int tables,bool locating)=>tables==0&&locating?1:tables;

    internal sealed record MaterialBatch(MaterialPreset Material,List<NestPackingEngine.Input> Inputs);
    internal static List<NestPackingEngine.Input> ShuffleInputs(IReadOnlyList<NestPackingEngine.Input> inputs,System.Random random)
    {
      var movable=inputs.Where(input=>!input.Fixed).ToArray();
      for(int index=movable.Length-1;index>0;index--)
      {int other=random.Next(index+1);(movable[index],movable[other])=(movable[other],movable[index]);}
      var ranks=movable.Select((input,index)=>(input.Id,index)).ToDictionary(pair=>pair.Id,pair=>pair.index);
      return inputs.Select(input=>input with {ShuffleRank=ranks.TryGetValue(input.Id,out int rank)?rank:null}).ToList();
    }
    internal static NestPackingEngine.Result SolveMaterials(IReadOnlyList<MaterialBatch> batches,double length,double searchSeconds,
      Action<int>? progress,Action<NestPackingEngine.Result>? preview,CancellationToken token,Action<NestPackingEngine.Result>? accepted)
      =>SolveMaterialsWithActivity(batches,length,searchSeconds,progress,preview,token,accepted,null);
    internal static NestPackingEngine.Result SolveMaterialsWithActivity(IReadOnlyList<MaterialBatch> batches,double length,double searchSeconds,
      Action<int>? progress,Action<NestPackingEngine.Result>? preview,CancellationToken token,Action<NestPackingEngine.Result>? accepted,Action<string>? activity)
      =>SolveMaterialsWithStages(batches,length,searchSeconds,progress,preview,token,accepted,activity,null);
    internal static NestPackingEngine.Result SolveMaterialsWithStages(IReadOnlyList<MaterialBatch> batches,double length,double searchSeconds,
      Action<int>? progress,Action<NestPackingEngine.Result>? preview,CancellationToken token,Action<NestPackingEngine.Result>? accepted,Action<string>? activity,
      Action<string,string,int,int>? stageProgress)
    {
      var completed=new List<(MaterialPreset Material,NestPackingEngine.Result Result)>();
      NestPackingEngine.Result Merge(MaterialPreset? material=null,NestPackingEngine.Result? current=null)
      {
        var entries=new List<(MaterialPreset Material,NestPackingEngine.Result Result)>(completed);
        if(current!=null) entries.Add((material!,current));
        var placements=new List<NestPackingEngine.Placement>(); var errors=new Dictionary<int,string>();
        var tables=new Dictionary<int,MaterialPreset>(); int offset=0;
        foreach(var entry in entries)
        {
          if(offset+entry.Result.Tables>NestPackingEngine.MaximumTables)
          {
            foreach(var part in entry.Result.Placements) errors[part.Id]="Material table limit exceeded";
            foreach(var error in entry.Result.Errors) errors[error.Key]=error.Value;
            continue;
          }
          placements.AddRange(entry.Result.Placements.Select(part=>part with {Table=part.Table+offset}));
          foreach(var error in entry.Result.Errors) errors[error.Key]=error.Value;
          for(int table=0;table<entry.Result.Tables;table++) tables[offset+table]=entry.Material;
          offset+=entry.Result.Tables;
        }
        var stats=entries.Select(entry=>entry.Result.Search).OfType<NestPackingEngine.SearchStatistics>().ToArray();
        var profiles=entries.Select(entry=>entry.Result.Profile).OfType<NestPackingEngine.ProfileStatistics>().ToArray();
        double allowance=entries.Select(entry=>entry.Result.TableAllowance).DefaultIfEmpty(NestPackingEngine.DefaultTableAllowance).Max();
        return new NestPackingEngine.Result(placements,offset,errors) {TableMaterials=tables,TableAllowance=allowance,Stopped=entries.Any(entry=>entry.Result.Stopped),
          Search=stats.Length==0?null:new NestPackingEngine.SearchStatistics(stats.Sum(s=>s.PlacementTests),stats.Sum(s=>s.Layouts),stats.Sum(s=>s.Improvements),
            stats.Sum(s=>s.InitialSeconds),stats.Sum(s=>s.SearchSeconds),stats.Any(s=>s.BudgetExpired))
            {HalfTurnTests=stats.Sum(s=>s.HalfTurnTests),ConsumedLength=placements.GroupBy(part=>part.Table).Sum(group=>group.Max(part=>part.Y+part.Pose.Length)+allowance)},
          Profile=profiles.Length==0?null:new NestPackingEngine.ProfileStatistics(profiles.Sum(p=>p.PairHits),profiles.Sum(p=>p.PairMisses),profiles.Sum(p=>p.PairSeconds),
            profiles.Sum(p=>p.AnchorCalls),profiles.Sum(p=>p.AnchorSeconds),profiles.Sum(p=>p.CollisionCalls),profiles.Sum(p=>p.CollisionSeconds),profiles.Sum(p=>p.PosesPruned),
            profiles[^1].CacheEntries,profiles[^1].CacheVertices)};
      }
      int total=batches.Sum(batch=>batch.Inputs.Count),processed=0;
      for(int index=0;index<batches.Count&&!token.IsCancellationRequested;index++)
      {
        var batch=batches[index]; int finished=processed;
        var timer=System.Diagnostics.Stopwatch.StartNew();
        double budget=searchSeconds*batch.Inputs.Count/Math.Max(1,total);
        Log.Write("vNest","material started name={0} parts={1} poses={2} width={3} searchSeconds={4:G6}",
          batch.Material.Name,batch.Inputs.Count,batch.Inputs.Sum(input=>input.Poses.Count),batch.Material.Width,budget);
        var result=NestContourPackingEngine.SolveUntilStoppedWithStages(batch.Inputs,batch.Material.Width,length,
          value=>progress?.Invoke((finished*100+batch.Inputs.Count*value)/Math.Max(1,total)),
          value=>preview?.Invoke(Merge(batch.Material,value)),token,searchSeconds*batch.Inputs.Count/Math.Max(1,total),
          value=>accepted?.Invoke(Merge(batch.Material,value)),activity,
          (stage,percent,overall)=>stageProgress?.Invoke(batch.Material.Name,stage,percent,(finished*100+batch.Inputs.Count*overall)/Math.Max(1,total)));
        Log.Write("vNest","material finished name={0} parts={1} tables={2} elapsedSeconds={3:G6} initialSeconds={4:G6} searchSeconds={5:G6} pairSeconds={6:G6} anchorSeconds={7:G6} collisionSeconds={8:G6} stopped={9}",
          batch.Material.Name,batch.Inputs.Count,result.Tables,timer.Elapsed.TotalSeconds,result.Search?.InitialSeconds??0,result.Search?.SearchSeconds??0,
          result.Profile?.PairSeconds??0,result.Profile?.AnchorSeconds??0,result.Profile?.CollisionSeconds??0,result.Stopped);
        processed+=batch.Inputs.Count;
        if(completed.Sum(entry=>entry.Result.Tables)+result.Tables>NestPackingEngine.MaximumTables)
          result=new NestPackingEngine.Result([],0,batch.Inputs.ToDictionary(input=>input.Id,_=>"Material table limit exceeded"));
        completed.Add((batch.Material,result));
      }
      var final=Merge();
      if(token.IsCancellationRequested)
      {
        var placed=final.Placements.Select(part=>part.Id).ToHashSet();
        foreach(var input in batches.SelectMany(batch=>batch.Inputs).Where(input=>!placed.Contains(input.Id))) final.Errors.TryAdd(input.Id,"Not placed (stopped)");
        final=final with {Stopped=true};
      }
      accepted?.Invoke(final); return final;
    }

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
  using Rhino.Input.Custom;
  using Rhino.UI;
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
    private const int MaterialColumnWidth = 120; // Initial material-name column width in device-independent pixels; leaves room for status at the minimum window width.
    private const int SettingColumnWidth = 85; // Read-only per-part setting column width in device-independent pixels; additional settings remain horizontally scrollable.
    private const int TotalYardageWidth = 130; // Fixed footer width in device-independent pixels for the total occupied yardage, independent of status/progress text.
    private const int MaterialUsageTableCountWidth = 100; // Used-table count column width in device-independent pixels.
    private const int TablePartCountWidth = 90; // Physical part-copy count column width in device-independent pixels.
    private const float CustomizedRowTint = 0.2f; // Amber contribution to the current theme's row background, zero through one; selected rows keep native selection colors.
    private const float CustomizedRowAmberGreen = 0.65f; // Green component of the amber customization tint, zero through one.
    private const double CustomizedRowMinimumContrast = 4.5; // Minimum text/background contrast ratio for customized rows; greater than one.
    private const double InactiveRowSelectionSaturation = 0.72; // Unfocused selection saturation, zero through one; keeps inactive rows visibly blue.
    private const bool HighlightRedrawOnChange = false; // true redraws each highlighter separately; false redraws once after a complete UI/selection update.
    private const string CustomizedRowBackgroundKey = "vNest.CustomizedBackground"; // Scoped WPF resource key for unselected customized-row fill.
    private const string CustomizedRowForegroundKey = "vNest.CustomizedForeground"; // Scoped WPF resource key for readable customized-row text.
    private static readonly SDColor DrawnGuideColor = SDColor.ForestGreen; // Fallback stroke color for drawn guide previews when no GRAIN layer exists.
    private const int StatusColumnMinimumWidth = 210; // Minimum status width in device-independent pixels; leaves enough room for copy counts and state text.
    private const int IconButtonSize = 26; // Stable add/remove button width in device-independent pixels.
    private const int MaterialPresetActionWidth = (FieldWidth-ControlGap)/2; // Two preset-save command buttons fit one uniform field column, in device-independent pixels.
    private const int NumberDecimals = 3; // Decimal places displayed for model-unit coordinates and angles; nonnegative integer.
    private const string MixedValue = "<varies>"; // Non-actionable dropdown placeholder for unequal selected settings.
    private const double NumberIncrement = 0.1; // Model-unit/degree increment for numeric editor arrows; positive.
    private const double AutoNestDelay = 0.2; // Seconds to debounce automatic re-nesting after edits; positive.
    private const string WindowTitle = "vNest"; // Modeless manager window title.
    private const string InitialPackingActivity = "Preparing"; // Initial progress-bar stage text before worker feedback arrives.
    private const int LabelMeasurementCacheLimit = 512; // Maximum cached glyph bounds per window; bounds-only entries avoid retaining native curves.
    private const double ManualRotationCheckStep = 1.0; // Maximum degrees between collision checks during ordinary cursor rotation; positive and much smaller than a half-turn.
    private const int ManualRotationContactRefinements = 10; // Bisection steps locating the last valid cursor angle when the rotation path meets an obstacle.
    private const bool LocationObjectSnaps = true; // true enables native object snaps in the location picker; false uses unsnapped points.
    private const int UnpackedRevision = -1; // Sentinel for a selection/options revision that has no accepted packing result.
    private const int NoSortColumn = -1; // Sentinel for keeping the original displayed list order until a header is clicked.
    private const bool DefaultSortDescending = false; // false sorts a new column ascending; true would start descending.
    private const bool AllowColumnReordering = true; // true lets users drag list headers to reorder columns; false fixes their display order.
    private static readonly int[] PartColumnValueIndices = [0,1,7,2,3,4,6,8,17,9,10,11,12,13,14,15,16,5]; // Grid-value indexes in displayed part-column order.
    private const float ProgressTintAlpha = 0.3f; // Theme highlight opacity from zero through one; keeps overlaid status text legible.
    private const float ProgressFontSize = 7f; // Maximum text size in points for the two compact progress tracks; shrinks further only to fit track height.
    private const float ProgressTrackGap = 1f; // Device-independent pixels separating step and total tracks within the unchanged status-bar height.
    private const string ElapsedShortFormat = @"m\:ss\.f"; // Completed durations below one hour: minutes, seconds and tenths; not a remaining-time estimate.
    private const string ElapsedHourTailFormat = @"mm\:ss\.f"; // Minutes/seconds/tenths appended to total elapsed hours for longer operations.
    private static readonly PreviewDisplay.ObjectHighlightStyle SkippedStyle = new(
      SDColor.OrangeRed,SDColor.Maroon,SDColor.Maroon,0.25)
      {StrokeEmphasis=0,OutlineEmphasis=0}; // Normal-width orange-red curves for skipped parts; zero emphasis omits the heavy outline.
    private static readonly PreviewDisplay.ObjectHighlightStyle MembershipStyle = new(
      SDColor.FromArgb(0,145,165),SDColor.FromArgb(0,55,72),SDColor.FromArgb(0,120,145),0.6)
      {StrokeEmphasis=0,OutlineEmphasis=0}; // Normal-width muted cyan membership curves; zero emphasis omits the heavy outline.
    private static readonly PreviewDisplay.ObjectHighlightStyle SelectedPartStyle = new(
      SDColor.Magenta,SDColor.FromArgb(85,0,65),SDColor.FromArgb(150,0,110),0.25)
      {StrokeEmphasis=0,OutlineEmphasis=0}; // Normal-width magenta curves for selected sources and previews at rest or moving; zero emphasis omits the heavy outline.
    private static readonly PreviewDisplay.ObjectHighlightStyle InactivePartStyle = new(
      SDColor.SteelBlue,SDColor.DarkSlateGray,SDColor.SteelBlue,0.25)
      {StrokeEmphasis=0,OutlineEmphasis=0}; // Muted blue selected perimeter while the viewport lacks keyboard focus; internals retain original colors.
    private static readonly PreviewDisplay.ObjectHighlightStyle WorkingTableStyle = new(
      SDColor.LimeGreen,SDColor.DarkGreen,SDColor.DarkGreen,0.25); // Green table-frame feedback distinguishes editable placed tables from cyan members and magenta active parts.
    private static readonly PreviewDisplay.ObjectHighlightStyle SelectedTableStyle = new(
      SDColor.Gold,SDColor.DarkGoldenrod,SDColor.Goldenrod,0.25)
      {StrokeEmphasis=0,OutlineEmphasis=0}; // Thin amber current-table frame; distinct from magenta focused parts and green other working tables.

    internal sealed partial class NestWindow : Form
    {
      private readonly RhinoDoc _doc;
      private readonly List<Row> _rows;
      private List<Row> _visibleRows=[];
      private int _partSortColumn=NoSortColumn,_tableSortColumn=NoSortColumn,_materialSortColumn=NoSortColumn;
      private bool _partSortDescending=DefaultSortDescending,_tableSortDescending=DefaultSortDescending,_materialSortDescending=DefaultSortDescending;
      private readonly GridView _grid=new() {AllowMultipleSelection=true,RowHeight=GridRowHeight};
      private readonly NumericStepper _width=Number(),_length=Number(),_rotationStep=Number();
      private readonly DropDown _rotationPreset=new() {DataStore=RotationLabels,Width=FieldWidth};
      private readonly DropDown _partRotation=new() {DataStore=new[]{"Default"}.Concat(RotationLabels).ToArray(),Width=FieldWidth};
      private readonly DropDown _allowReverse=new() {DataStore=ReversalLabels,Width=FieldWidth};
      private readonly CheckBox _obeyDefault=new() {Text="Obey grain",Checked=ObeyGrain};
      private readonly CheckBox _obey=new() {Text="Obey grain"},_defaultDeviation=new() {Text="Default"};
      private readonly NumericStepper _guideAngle=Number(),_partDeviation=Number();
      private readonly NumericStepper _searchTime=Number(),_grainDeviation=Number(),_partGap=Number();
      private readonly DropDown _material=new() {Width=FieldWidth,ToolTip="Material preset for Width, Gap and grain rules; * marks unsaved edits retained between Rhino restarts"};
      private readonly Button _saveMaterialAs=new() {Text="Save As...",Width=MaterialPresetActionWidth,ToolTip="Save current material settings under a new name"};
      private readonly Button _saveMaterial=new() {Text="Save",Width=MaterialPresetActionWidth,Enabled=false,ToolTip="Overwrite the selected material's Width, Gap and grain rules"};
      private List<MaterialPreset> _materialPresets=[];
      private readonly Dictionary<string,MaterialPreset> _materialDrafts=LoadMaterialDrafts().ToDictionary(preset=>preset.Name,StringComparer.OrdinalIgnoreCase);
      private Dictionary<string,MaterialPreset> _persistedMaterialDrafts=[];
      private string? _activeMaterialName;
      private MaterialPreset _customMaterial=ConfiguredMaterial(CustomMaterialName,NestWorkflow.Width,PartGap,GrainDeviation);
      private bool _confirmingClose;
      private bool _writingPartSettings;
      private readonly HashSet<string> _externalPartSettings=[];
      private readonly DropDown _grain=new() {DataStore=GrainNames};
      private readonly CheckBox _auto=new() {Text="Auto",Checked=DefaultAutoNest};
      private readonly CheckBox _allowFlip=new() {Text="Allow Flip",Checked=AllowFlip};
      private readonly CheckBox _showGap=new() {Text="Show Gap",Checked=ShowGap,ToolTip="Show a one-pixel outline at the full collision gap outside active parts; preview only, never exported"};
      private readonly CheckBox _flipped=new() {Text="Flipped",Checked=DefaultAllowFlip};
      private readonly CheckBox _included=new() {Text="Included"},_fixed=new() {Text="Fixed"};
      private readonly NumericStepper _x=Number(),_y=Number(),_angle=Number(),_table=Number(),_limit=Number();
      private readonly NumericStepper _copies=Number();
      private readonly Dictionary<NumericStepper,NestMixedNumber> _mixedNumbers=[];
      private readonly Dictionary<NumericStepper,double> _displayedNumbers=[];
      private readonly List<FieldResetOverlay> _resets=[];
      private HashSet<int> _displayedSelection=[];
      private (Eto.Drawing.Color Background,Eto.Drawing.Color Foreground)? _customizedRowColors;
      private readonly DropDown _partGrain=new() {DataStore=new[]{"Default"}.Concat(GrainNames).ToArray()};
      private readonly DropDown _partMaterial=new() {Width=FieldWidth,ToolTip="Assign selected parts to a material; different materials nest into separate tables"};
      private readonly Button _grainPick=new() {Text="Pick Grain",Enabled=false,ToolTip="Pick a curve or draw a grain guide for selected parts"};
      private readonly Button _autoRotation=new() {Text="Auto Rotation",Enabled=false,ToolTip="Allow the packer to choose rotation for selected parts"};
      private readonly Button _resetPart=new() {Text="Reset Part",Enabled=false,ToolTip="Reset selected parts to material/global defaults"};
      private readonly Button _close=new() {Text="Close",ToolTip="Close without placing preview geometry"};
      private readonly PackingStatus _status=new();
      private readonly Label _totalYardage=new() {Width=TotalYardageWidth,Text="Total: 0.0 yds",ToolTip="Sum of occupied lengths across all material tables; excludes unused tails"};
      private readonly Button _place=new() {Text="Place",Enabled=false};
      private readonly Button _nest=new() {Text="Nest",ToolTip="Pack included parts; retain fixed positions, manual angles and grain settings"};
      private readonly Button _editTables=new() {Text="Edit Tables",ToolTip="Edit existing table parts immediately; return to Original Parts for a new nest"};
      private Control? _settingsPanel;
      private Button? _locationAction;
      private readonly List<LiveTable> _liveTables=[];
      private readonly HashSet<int> _pendingPlacedTables=[];
      private readonly Dictionary<int,Guid[]> _liveParts=[];
      private readonly HashSet<Guid> _gestureObjectIds=[];
      private readonly HashSet<uint> _gestureObjectSerials=[];
      private OriginalLayout? _originalLayout;
      private uint _liveUndoRecord;
      private bool _applyingLive;
      private bool _replayingLiveHistory;
      private uint _liveHistoryInverseRecord;
      private sealed record OriginalLayout(List<Row> Rows,NestPackingEngine.Result Result,Point3d Anchor,bool Anchored,
        int Revision,int PackedRevision,Dictionary<int,NestPackingEngine.Placement> Instances);
      private sealed record LiveTable(int Group,Guid Border,Curve Outline,Plane Frame,double Length,MaterialPreset Material,string Name,TextEntity[] Labels,Guid[] LabelIds,Guid Divider=default) : IDisposable
      {public void Dispose(){Outline.Dispose();foreach(var text in Labels)text.Dispose();}}
      private sealed record TableGroupRename(Guid Id,string Before,string After);
      private static void UndoTableGroupRename(object? sender,CustomUndoEventArgs e)
      {
        if(e.Tag is not TableGroupRename rename||e.Document is not {} doc)return;
        var group=doc.Groups.FirstOrDefault(value=>!value.IsDeleted&&value.Id==rename.Id);if(group==null)return;
        doc.AddCustomUndoEvent("vNest table name",UndoTableGroupRename,rename);
        string name=doc.RedoActive?rename.After:rename.Before;
        if(doc.Groups.GroupName(group.Index)!=name&&!doc.Groups.ChangeGroupName(group.Index,name))Log.Write("vNest","could not restore table group name id={0}",rename.Id);
      }
      private readonly UITimer _timer=new() {Interval=AutoNestDelay};
      private readonly Preview _preview;
      private readonly PreviewDisplay.ObjectHighlighter _highlight;
      private readonly PreviewDisplay.ObjectHighlighter _inactiveHighlight;
      private readonly PreviewDisplay.ObjectHighlighter _sources;
      private readonly PreviewDisplay.ObjectHighlighter _selectedSources;
      private readonly PreviewDisplay.ObjectHighlighter _workingTables;
      private readonly PreviewDisplay.ObjectHighlighter _selectedWorkingTables;
      private readonly NestDocumentPicking _picking;
      private readonly PreviewKeyboard _previewKeyboard;
      private readonly Dictionary<int,NestPackingEngine.Placement> _manualInstances=[];
      private readonly List<ManualState> _manualUndo=[],_manualRedo=[];
      private sealed record ManualPartState(bool Fixed,bool ManualRotation,bool Flipped,int Table,double X,double Y,double Rotation);
      private sealed record ManualState(NestPackingEngine.Result Layout,Dictionary<int,ManualPartState> Parts,
        Dictionary<int,NestPackingEngine.Placement> Instances,int? ActiveInstance,uint DocumentRecord=0,int[]? Selection=null);
      private int? _activePreviewInstance;
      private readonly HashSet<int> _manualSelectedInstances=[];
      private sealed record PreviewBox(System.Drawing.Point Start,Guid Viewport,int? Hit,bool Add,bool Remove);
      private PreviewBox? _previewBox;
      private ManualGesture? _manualGesture;
      private long _manualPreviewTick;
      private bool _pivotVisible;
      private bool _previewHasFocus;
      private sealed record ManualGesture(NestPackingEngine.Result Before,HashSet<int> Ids,Plane Plane,Point3d Start,Point3d Pivot,
        Guid Viewport,int StartPixelX,bool Rotation,bool Dragging,NestContourPackingEngine.ManualLayout Scene)
      {internal double ContactAlignment {get;set;}=NestContourPackingEngine.DefaultManualContactAlignment;}
      private readonly PreviewDisplay.ObjectHighlighter _skipped;
      private readonly Dictionary<int,(Transform Transform,List<PlanarPartSelection.Item> Items,Line? DrawnGuide)> _previewCopies=[];
      private readonly Dictionary<int,(Transform Transform,Curve Outline)> _previewOutlines=[];
      private readonly Dictionary<int,(Transform Transform,double Gap,Polyline[] Contours)> _previewClearance=[];
      private readonly HashSet<uint> _tableObjectSerials=[];
      private readonly HashSet<uint> _liveObjectSerials=[];
      private (Row Row,NestPackingEngine.Placement Placement)[] _activePreviewParts=[];
      private bool _updating,_closed,_anchored,_needsNest,_locationPicking,_shown,_packing,_stopping,_placing;
      private int _layoutRevision,_packedRevision=UnpackedRevision;
      private CancellationTokenSource? _packingCancellation;
      private HashSet<Guid> _selection=[];
      private NestPackingEngine.Result _result=new([],0,[]);
      private NestPackingEngine.Result? _workingResult;
      private NestPackingEngine.Result? _bestInProgress;
      private readonly System.Random _nestRandom=new();
      private NestPackingEngine.Result VisibleResult=>_workingResult??_result;
      private readonly Plane _plane;
      private Point3d _anchor;
      private Row? _grainRequest;

      internal uint DocumentSerial=>_doc.RuntimeSerialNumber;
      private Row? Selected=>_grid.SelectedItem is GridItem item&&item.Tag is Row row?row:null;
      private Row[] SelectedParts=>_grid.SelectedRows.Where(index=>index>=0&&index<_visibleRows.Count).Select(index=>_visibleRows[index]).ToArray();
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
        else if(field==_angle) {if(!ManualNumber(field,value))Edit(row=> { row.Rotation=value; row.ManualRotation=true; });}
        else if(field==_limit) Edit(row=> { row.RotationStepOverride=value; row.RotationOverride=RotationMode.Custom; });
        else if(field==_partDeviation) Edit(row=>row.GrainDeviationOverride=value);
        else if(field==_guideAngle) Edit(row=>SetGuide(row,value));
      }
      private void SetGuide(Row row,double degrees)
      {
        double previous=GuideAngle(row)??SourceGrainAngle;
        double angle=RhinoMath.ToRadians(degrees);
        row.Guide=row.Part.Plane.XAxis*Math.Cos(angle)+row.Part.Plane.YAxis*Math.Sin(angle);
        if(row.DrawnGuide is {} line)
        {
          var turn=Transform.Rotation(RhinoMath.ToRadians(degrees-previous),row.Part.Plane.Normal,line.PointAt(0.5));
          row.DrawnGuide=new Line(turn*line.From,turn*line.To);
        }
        if(EffectiveGrainMode(row)==GrainMode.Off) row.GrainOverride=GrainMode.Auto;
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
        _persistedMaterialDrafts=new(_materialDrafts,StringComparer.OrdinalIgnoreCase);
        _doc=doc; _rows=rows;
        _result=_result with {TableAllowance=TableAllowanceForUnits(doc.ModelUnitSystem)};
        Title=WindowTitle; ClientSize=new Size(WindowWidth,WindowHeight); MinimumSize=new Size(MinimumWindowWidth,MinimumWindowHeight);
        Padding=new Padding(WindowPadding);
        _plane=doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()??Plane.WorldXY;
        var bounds=BoundingBox.Empty; foreach(var row in rows) bounds.Union(row.Part.Outline.GetBoundingBox(_plane));
        _anchored=bounds.IsValid;
        _anchor=_plane.Origin;
        _preview=new Preview(this) {Enabled=true};
        _sources=new PreviewDisplay.ObjectHighlighter(doc,MembershipStyle) {RedrawOnChange=HighlightRedrawOnChange};
        _selectedSources=new PreviewDisplay.ObjectHighlighter(doc,SelectedPartStyle) {RedrawOnChange=HighlightRedrawOnChange};
        _workingTables=new PreviewDisplay.ObjectHighlighter(doc,WorkingTableStyle) {RedrawOnChange=HighlightRedrawOnChange};
        _selectedWorkingTables=new PreviewDisplay.ObjectHighlighter(doc,SelectedTableStyle) {RedrawOnChange=HighlightRedrawOnChange};
        _skipped=new PreviewDisplay.ObjectHighlighter(doc,SkippedStyle) {RedrawOnChange=HighlightRedrawOnChange};
        _highlight=new PreviewDisplay.ObjectHighlighter(doc,SelectedPartStyle) {RedrawOnChange=HighlightRedrawOnChange};
        _inactiveHighlight=new PreviewDisplay.ObjectHighlighter(doc,InactivePartStyle) {RedrawOnChange=HighlightRedrawOnChange};
        foreach(var layer in new[]{_sources,_selectedSources,_workingTables,_selectedWorkingTables,_skipped,_highlight,_inactiveHighlight})layer.AutomaticForegroundDrawing=false;
        _picking=new NestDocumentPicking(doc,()=>!_closed&&!_placing&&!_locationPicking&&!PackingBlocked(),SyncSelection,
          id=>_rows.FirstOrDefault(row=>row.Part.Items.Any(item=>item.Id==id))?.Part.Items.Select(item=>item.Id).ToArray(),KnownInteriorMembers);
        _picking.RetainedSelection=()=>_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id);
        _picking.PartClicked+=FocusClickedPart;
        _picking.PreviewMouseDown=PreviewMouseDown;_picking.PreviewMouseMove=PreviewMouseMove;_picking.PreviewMouseUp=PreviewMouseUp;
        _picking.PreviewWindowSelection=SelectManualBox;
        _picking.PreviewGestureActive=()=>_manualGesture!=null||_previewBox!=null;
        _picking.PreviewCancelled=()=>EndManualGesture(false);
        _picking.SelectionSuppressed=()=>_liveTables.Count>0;
        _previewKeyboard=new PreviewKeyboard(doc,()=>CanFineTune,PreviewKeyWithModifiers,ManualHistory,
          ()=>ControlObject is System.Windows.Window window?new System.Windows.Interop.WindowInteropHelper(window).Handle:IntPtr.Zero,
          ()=>_manualGesture!=null||ManualSelection().Length>0,down=>{_pivotVisible=down;_doc.Views.Redraw();});
        _width.Value=NestWorkflow.Width; _length.Value=Length; _rotationStep.Value=RotationStep; _rotationStep.MinValue=MinimumRotationStep; _rotationStep.MaxValue=MaximumRotationStep;
        _rotationPreset.SelectedIndex=(int)RotationChoice; _rotationStep.Enabled=RotationChoice==RotationMode.Custom;
        _searchTime.Value=SearchTime; _searchTime.MaxValue=MaximumSearchTime; _searchTime.DecimalPlaces=0; _searchTime.Increment=1;
        _grainDeviation.MaxValue=MaximumGrainDeviation; _grainDeviation.Value=GrainDeviation;
        _partGap.Value=PartGap;
        _allowReverse.SelectedIndex=AllowReverse?1:0; _grain.SelectedIndex=(int)Grain;
        RefreshMaterialPresets();
        _anchor=_anchored?_plane.PointAt(bounds.Max.X+TableGap+InitialLabelExtent(),bounds.Min.Y):_plane.Origin;
        _partDeviation.MaxValue=MaximumGrainDeviation; _guideAngle.MinValue=-180; _guideAngle.MaxValue=180;
        _allowReverse.SelectedIndex=AllowReverse?1:0;
        _grain.SelectedIndex=(int)Grain; _angle.MinValue=-180; _angle.MaxValue=180;
        _limit.MinValue=MinimumRotationStep; _limit.MaxValue=MaximumRotationStep; _table.MinValue=1; _table.MaxValue=NestPackingEngine.MaximumTables; _table.DecimalPlaces=0; _table.Increment=1;
        _x.MinValue=_y.MinValue=-double.MaxValue;
        _grid.Columns.Add(new GridColumn {HeaderText="",Width=32,DataCell=new CheckBoxCell(0),Editable=true});
        _grid.Columns.Add(new GridColumn {HeaderText="Part",Width=PartNameWidth,DataCell=new TextBoxCell(1)});
        _grid.Columns.Add(new GridColumn {HeaderText="Material",Width=MaterialColumnWidth,DataCell=new TextBoxCell(7)});
        _grid.Columns.Add(new GridColumn {HeaderText="Table",Width=65,DataCell=new TextBoxCell(2)});
        _grid.Columns.Add(new GridColumn {HeaderText="Angle",Width=SettingColumnWidth,DataCell=new TextBoxCell(3)});
        _grid.Columns.Add(new GridColumn {HeaderText="Fixed",Width=55,DataCell=new CheckBoxCell(4)});
        _grid.Columns.Add(new GridColumn {HeaderText="Copies",Width=60,DataCell=new TextBoxCell(6)});
        _grid.Columns.Add(new GridColumn {HeaderText="Grain",Width=SettingColumnWidth,DataCell=new TextBoxCell(8)});
        _grid.Columns.Add(new GridColumn {HeaderText="Grain guide",Width=SettingColumnWidth,DataCell=new TextBoxCell(17),HeaderToolTip="Detected from the part, explicitly set, source-axis fallback, or not available"});
        _grid.Columns.Add(new GridColumn {HeaderText="Grain angle",Width=SettingColumnWidth,DataCell=new TextBoxCell(9)});
        _grid.Columns.Add(new GridColumn {HeaderText="Obey grain",Width=SettingColumnWidth,DataCell=new CheckBoxCell(10)});
        _grid.Columns.Add(new GridColumn {HeaderText="Deviation",Width=SettingColumnWidth,DataCell=new TextBoxCell(11)});
        _grid.Columns.Add(new GridColumn {HeaderText="Rotation",Width=FieldWidth,DataCell=new TextBoxCell(12)});
        _grid.Columns.Add(new GridColumn {HeaderText="Step",Width=SettingColumnWidth,DataCell=new TextBoxCell(13)});
        _grid.Columns.Add(new GridColumn {HeaderText="Flipped",Width=SettingColumnWidth,DataCell=new CheckBoxCell(14)});
        _grid.Columns.Add(new GridColumn {HeaderText="X",Width=SettingColumnWidth,DataCell=new TextBoxCell(15)});
        _grid.Columns.Add(new GridColumn {HeaderText="Y",Width=SettingColumnWidth,DataCell=new TextBoxCell(16)});
        _grid.Columns.Add(new GridColumn {HeaderText="Status",Width=StatusColumnMinimumWidth,DataCell=new TextBoxCell(5),Expand=true});
        EnableSorting(_grid,SortPartColumn);
        _grid.Load+=(_,_)=>
        {
          foreach(var column in _grid.Columns.Take(_grid.Columns.Count-1))
            if(column.ControlObject is System.Windows.Controls.DataGridColumn native)
            { native.MinWidth=column.Width; native.Width=new System.Windows.Controls.DataGridLength(column.Width); }
          if(_grid.Columns.Last().ControlObject is System.Windows.Controls.DataGridColumn statusColumn)
          { statusColumn.MinWidth=StatusColumnMinimumWidth; statusColumn.Width=new System.Windows.Controls.DataGridLength(1,System.Windows.Controls.DataGridLengthUnitType.Star); }
          InstallRowSelectionStyles();
        };
        _copies.DecimalPlaces=0; _copies.Increment=1; _copies.MinValue=DefaultCopies; _copies.MaxValue=MaximumCopies;
        _copies.ToolTip="Number of separately grouped copies to nest. Fixed position applies to the first copy; extra copies pack automatically.";
        foreach(var check in new[]{_included,_fixed,_flipped,_obey,_defaultDeviation}) check.ThreeState=true;
        var add=new Button {Text="Add Selected",ToolTip="Add selected parts from the drawing"};
        var remove=new Button {Text="Remove Parts",ToolTip="Remove highlighted parts from this nest, keeping originals"};
        var clear=new Button {Text="Clear Nest",ToolTip="Clear the part list and nest preview without deleting original objects"};
        var grainPick=_grainPick;
        var resetRotation=_autoRotation;
        var location=new Button {Text="Location",ToolTip="Pick a location for the preview tables"};
        _locationAction=location;
        var close=_close;
        var reset=_resetPart;
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
        _defaultDeviation.ToolTip="Use this part's material grain deviation; uncheck to set a separate allowance";
        _x.ToolTip="Position across material width, in document units; numeric entries override collisions and table boundaries";
        _y.ToolTip="Position along material length, in document units; numeric entries override collisions and table boundaries";
        _angle.ToolTip="Source-relative rotation in degrees; updates the part around its center and fixes it for re-nesting. Numeric entries and reset override collisions and table boundaries; enabled grain rules still apply.";
        _table.ToolTip="One-based material table; manual placement sets Fixed";
        _fixed.ToolTip="Keep this part at its entered table and coordinates during re-nesting";
        _limit.ToolTip="Custom per-part rotation increment in degrees; 0.1 through 360"; _partGrain.ToolTip="Override this part's material grain mode";
        _partRotation.ToolTip="For grain-constrained parts choose No reversal or Allow 180 reversal; otherwise choose a free-rotation preset. Default uses global settings.";
        _included.ToolTip="Include this part in the current nest"; _auto.ToolTip="Automatically re-nest after changes";
        _searchTime.ToolTip="Seconds to search alternative full layouts after the initial layout; zero keeps only the first pass";
        _allowFlip.ToolTip="Allow the packer to mirror parts across material grain; off preserves their handedness";
        _flipped.ToolTip="Set this part's mirrored orientation explicitly; Auto Rotation restores automatic choices";
        var settings=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          FieldRow(("Material",_material),("",new StackLayout {Orientation=Orientation.Horizontal,Spacing=ControlGap,Items={_saveMaterialAs,_saveMaterial}}),("Search (s)",_searchTime)),
          FieldRow(("Width",_width),("Length",_length),("Grain",_grain)),
          FieldRow(("Gap",_partGap),("Grain deviation",_grainDeviation),("",_obeyDefault)),
          FieldRow(("Grain turn",_allowReverse),("Free rotation",_rotationPreset),("Step",_rotationStep))}};
        _settingsPanel=settings;
        var toolbar=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(_auto,_allowFlip,_showGap,new TableCell(null,true),_editTables,add,remove,clear)}};
        var editor=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          FieldRow(("Material",_partMaterial),("Rotation",_partRotation),("Copies",MixedEditor(_copies))),
          FieldRow(("Table",_table),("Angle",MixedEditor(_angle)),("Grain",_partGrain)),
          FieldRow(("X",_x),("Y",_y),("Step",MixedEditor(_limit))),
          FieldRow(("Grain angle",MixedEditor(_guideAngle)),("Deviation",MixedEditor(_partDeviation)),("",_defaultDeviation))}};
        var partActions=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(_included,_fixed,_flipped,_obey,grainPick,resetRotation,reset,new TableCell(null,true))}};
        var footer=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(new TableCell(_status,true),_totalYardage,_nest,location,_place,close)}};
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
          if(_closed||_locationPicking||_pendingPlacedTables.Count>0||Busy()) return;
          SyncSelection();
          if(_needsNest&&!_packing&&!PackingBlocked()) ReNest();
        };
        foreach(var field in new[]{_width,_length,_rotationStep,_searchTime,_grainDeviation})
        { _displayedNumbers[field]=field.Value; field.ValueChanged+=(_,_)=>NumberChanged(field,GlobalChanged); }
        _displayedNumbers[_partGap]=_partGap.Value;
        _partGap.ValueChanged+=(_,_)=>NumberChanged(_partGap,()=>{if(_liveTables.Count>0)ChangeLiveGap();else GlobalChanged();});
        _rotationPreset.SelectedIndexChanged+=(_,_)=>GlobalChanged(); _allowReverse.SelectedIndexChanged+=(_,_)=>GlobalChanged();
        _obeyDefault.CheckedChanged+=(_,_)=>GlobalChanged();
        _grain.SelectedIndexChanged+=(_,_)=>GlobalChanged();
        _allowFlip.CheckedChanged+=(_,_)=>GlobalChanged();
        _showGap.CheckedChanged+=(_,_)=>{ShowGap=_showGap.Checked==true;Save();_doc.Views.Redraw();};
        _material.SelectedIndexChanged+=(_,_)=>SelectMaterialPreset();
        _saveMaterialAs.Click+=(_,_)=>SaveMaterialAs(CurrentMaterialValues(),true);
        _saveMaterial.Click+=(_,_)=>SaveSelectedMaterial();
        _partMaterial.SelectedIndexChanged+=(_,_)=>ChoiceChanged(_partMaterial,()=>Edit(row=>
          { row.MaterialName=_partMaterial.SelectedIndex<=0?null:_materialPresets[_partMaterial.SelectedIndex-1].Name; row.Fixed=false; }));
        _auto.CheckedChanged+=(_,_)=> { if(_auto.Checked==true) Queue(); };
        _grid.SelectionChanged+=(_,_)=> { if(_updating||_displayedSelection.SetEquals(SelectedParts.Select(row=>row.Id))) return; _activePreviewInstance=null;_manualSelectedInstances.Clear(); UpdateEditor(); RefreshHighlights(); _doc.Views.Redraw(); };
        _grid.KeyDown+=(_,e)=> { if(e.Key==Keys.Delete) { RemoveSelected(); e.Handled=true; } };
        _grid.CellEdited+=(_,e)=> { if(!_updating&&e.Item is GridItem item&&item.Tag is Row row&&row.Included!=Convert.ToBoolean(item.Values[0])) { row.Included=Convert.ToBoolean(item.Values[0]); Queue(); RefreshGrid(); RefreshHighlights(); _doc.Views.Redraw(); } };
        _included.CheckedChanged+=(_,_)=>CheckChanged(_included,()=>Edit(row=>row.Included=_included.Checked!.Value));
        _fixed.CheckedChanged+=(_,_)=>CheckChanged(_fixed,()=>Edit(row=> {row.Fixed=_fixed.Checked!.Value;if(!row.Fixed)foreach(int id in row.InstanceIds())_manualInstances.Remove(id);}));
        _flipped.CheckedChanged+=(_,_)=>CheckChanged(_flipped,()=>Edit(row=> { row.Flipped=_flipped.Checked!.Value; row.FlipOverride=row.Flipped; }));
        _angle.ValueChanged+=(_,_)=>NumberChanged(_angle,()=> {if(!ManualNumber(_angle,_angle.Value))Edit(row=> { row.Rotation=_angle.Value; row.ManualRotation=true; });});
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
        _x.ValueChanged+=(_,_)=>NumberChanged(_x,()=> { if(SelectedParts.Length==1&&!ManualNumber(_x,_x.Value)) Edit(row=> { row.X=_x.Value; row.Fixed=true; }); });
        _y.ValueChanged+=(_,_)=>NumberChanged(_y,()=> { if(SelectedParts.Length==1&&!ManualNumber(_y,_y.Value)) Edit(row=> { row.Y=_y.Value; row.Fixed=true; }); });
        _table.ValueChanged+=(_,_)=>NumberChanged(_table,()=> { if(SelectedParts.Length==1&&!ManualNumber(_table,_table.Value)) Edit(row=> { row.Table=(int)_table.Value-1; row.Fixed=true; }); });
        add.Click+=(_,_)=>AddSelected(); remove.Click+=(_,_)=>RemoveSelected(); _nest.Click+=(_,_)=> { if(_packing) StopPacking(); else ReNest(); };
        grainPick.Click+=(_,_)=> { if(Selected is {} row) RequestGrain(row); };
        resetRotation.Click+=(_,_)=>Edit(row=> { row.ManualRotation=false; row.FlipOverride=null; });
        reset.Click+=(_,_)=>Edit(ResetPart);
        InstallResets();
        location.Click+=(_,_)=>PickLocation(); _place.Click+=(_,_)=>Place(); close.Click+=(_,_)=>Close();
        clear.Click+=(_,_)=>ClearNest();
        _editTables.Click+=(_,_)=>{if(_liveTables.Count>0)ExitPlacedTables();else EnterPlacedTables();};
        Closing+=(_,e)=>
        {
          if(_placing||_closed) return;
          if(_confirmingClose) {e.Cancel=true;return;}
          _confirmingClose=true;
          try
          {
            if(_liveTables.Count==0&&VisibleResult.Tables>0&&!ConfirmDiscardNest()) {e.Cancel=true;return;}
          }
          finally {_confirmingClose=false;}
        };
        Closed+=(_,_)=>DisposeState();
        RhinoDoc.CloseDocument+=DocumentClosed;
        RhinoDoc.UserStringChanged+=PartSettingsChanged;
        Rhino.Commands.Command.UndoRedo+=LiveUndoRedo;
        RhinoApp.Idle+=PackingIdle;
        Shown+=(_,_)=> { if(_shown||_closed||IsDisposed) return; _shown=true; RefreshGrid(); RefreshHighlights(); _doc.Views.Redraw(); if(!_closed&&!IsDisposed)_timer.Start(); };
      }

      private static (Eto.Drawing.Color Background,Eto.Drawing.Color Foreground) ReadCustomizedRowColors()
      {
        var background=Rhino.ApplicationSettings.AppearanceSettings.GetPaintColor(Rhino.ApplicationSettings.PaintColor.EditBoxBackground,true);
        var foreground=Rhino.ApplicationSettings.AppearanceSettings.GetPaintColor(Rhino.ApplicationSettings.PaintColor.TextEnabled,true);
        return CustomizedRowColors(new Eto.Drawing.Color(background.R/255f,background.G/255f,background.B/255f),
          new Eto.Drawing.Color(foreground.R/255f,foreground.G/255f,foreground.B/255f));
      }
      private static (Eto.Drawing.Color Background,Eto.Drawing.Color Foreground) CustomizedRowColors(Eto.Drawing.Color background,Eto.Drawing.Color foreground)
      {
        var tinted=new Eto.Drawing.Color(background.R*(1-CustomizedRowTint)+CustomizedRowTint,
          background.G*(1-CustomizedRowTint)+CustomizedRowAmberGreen*CustomizedRowTint,background.B*(1-CustomizedRowTint));
        if(RowColorContrast(tinted,foreground)<CustomizedRowMinimumContrast)
          foreground=RowColorContrast(tinted,Eto.Drawing.Colors.Black)>=RowColorContrast(tinted,Eto.Drawing.Colors.White)
            ?Eto.Drawing.Colors.Black:Eto.Drawing.Colors.White;
        return (tinted,foreground);
      }
      private static double RowColorContrast(Eto.Drawing.Color background,Eto.Drawing.Color foreground)
      {
        static double Luminance(Eto.Drawing.Color color)
        {
          static double Linear(float value)=>value<=0.04045?value/12.92:Math.Pow((value+0.055)/1.055,2.4);
          return 0.2126*Linear(color.R)+0.7152*Linear(color.G)+0.0722*Linear(color.B);
        }
        double first=Luminance(background),second=Luminance(foreground);
        return (Math.Max(first,second)+0.05)/(Math.Min(first,second)+0.05);
      }
      private void UpdateRowPalette()
      {
        if(_grid.ControlObject is not System.Windows.Controls.DataGrid native)return;
        var colors=_customizedRowColors??ReadCustomizedRowColors();_customizedRowColors=colors;
        static System.Windows.Media.SolidColorBrush Brush(Eto.Drawing.Color color)=>new(System.Windows.Media.Color.FromRgb(
          (byte)Math.Round(color.R*255),(byte)Math.Round(color.G*255),(byte)Math.Round(color.B*255)));
        native.Resources[CustomizedRowBackgroundKey]=Brush(colors.Background);
        native.Resources[CustomizedRowForegroundKey]=Brush(colors.Foreground);
      }
      private sealed class CustomizedRowConverter : System.Windows.Data.IValueConverter
      {
        public object Convert(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>value is GridItem {Tag:Row row}&&IsCustomized(row);
        public object ConvertBack(object value,Type targetType,object parameter,System.Globalization.CultureInfo culture)=>System.Windows.Data.Binding.DoNothing;
      }
      private void InstallRowSelectionStyles()
      {
        if(_grid.ControlObject is not System.Windows.Controls.DataGrid native)return;
        UpdateRowPalette();
        var active=(native.TryFindResource(System.Windows.SystemColors.HighlightBrushKey) as System.Windows.Media.SolidColorBrush??System.Windows.SystemColors.HighlightBrush).Color;
        double gray=active.R*0.2126+active.G*0.7152+active.B*0.0722;
        byte Muted(byte channel)=>(byte)Math.Round(gray+(channel-gray)*InactiveRowSelectionSaturation);
        native.Resources[System.Windows.SystemColors.InactiveSelectionHighlightBrushKey]=new System.Windows.Media.SolidColorBrush(
          System.Windows.Media.Color.FromRgb(Muted(active.R),Muted(active.G),Muted(active.B)));
        native.Resources[System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey]=System.Windows.SystemColors.HighlightTextBrush;
        foreach(var column in native.Columns)
        {
          var basis=column.CellStyle??native.CellStyle??native.TryFindResource(typeof(System.Windows.Controls.DataGridCell)) as System.Windows.Style;
          var style=new System.Windows.Style(typeof(System.Windows.Controls.DataGridCell),basis);
          var customized=new System.Windows.DataTrigger {Binding=new System.Windows.Data.Binding {Converter=new CustomizedRowConverter()},Value=true};
          customized.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,new System.Windows.DynamicResourceExtension(CustomizedRowBackgroundKey)));
          customized.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,new System.Windows.DynamicResourceExtension(CustomizedRowForegroundKey)));
          style.Triggers.Add(customized);
          var selected=new System.Windows.Trigger {Property=System.Windows.Controls.DataGridCell.IsSelectedProperty,Value=true};
          selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,new System.Windows.DynamicResourceExtension(System.Windows.SystemColors.HighlightBrushKey)));
          selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,new System.Windows.DynamicResourceExtension(System.Windows.SystemColors.HighlightTextBrushKey)));
          style.Triggers.Add(selected);
          var inactive=new System.Windows.MultiTrigger();
          inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.DataGridCell.IsSelectedProperty,true));
          inactive.Conditions.Add(new System.Windows.Condition(System.Windows.Controls.Primitives.Selector.IsSelectionActiveProperty,false));
          inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,new System.Windows.DynamicResourceExtension(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey)));
          inactive.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,new System.Windows.DynamicResourceExtension(System.Windows.SystemColors.InactiveSelectionHighlightTextBrushKey)));
          style.Triggers.Add(inactive);
          style.Setters.Add(new System.Windows.EventSetter(System.Windows.FrameworkElement.LoadedEvent,new System.Windows.RoutedEventHandler(BindCellTextColor)));
          column.CellStyle=style;
        }
      }
      private static void BindCellTextColor(object sender,System.Windows.RoutedEventArgs e)
      {
        if(sender is not System.Windows.Controls.DataGridCell cell)return;
        void Visit(System.Windows.DependencyObject element)
        {
          for(int index=0;index<System.Windows.Media.VisualTreeHelper.GetChildrenCount(element);index++)
          {
            var child=System.Windows.Media.VisualTreeHelper.GetChild(element,index);
            if(child is System.Windows.Controls.TextBlock text)
              text.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty,new System.Windows.Data.Binding("Foreground") {Source=cell});
            Visit(child);
          }
        }
        Visit(cell);
      }
      private static void EnableSorting(GridView grid,Action<int> sort)
      {
        grid.AllowColumnReordering=AllowColumnReordering;
        foreach(var column in grid.Columns) column.Sortable=true;
        grid.ColumnHeaderClick+=(_,e)=>sort(grid.Columns.IndexOf(e.Column));
      }
      private static void ChangeSort(GridView grid,int column,ref int current,ref bool descending)
      {
        descending=current==column?!descending:DefaultSortDescending;current=column;
        for(int index=0;index<grid.Columns.Count;index++)
          if(grid.Columns[index].ControlObject is System.Windows.Controls.DataGridColumn native)
            native.SortDirection=index==column?descending?System.ComponentModel.ListSortDirection.Descending:System.ComponentModel.ListSortDirection.Ascending:null;
      }
      private void SortPartColumn(int column)
      {if(column<0)return;ChangeSort(_grid,column,ref _partSortColumn,ref _partSortDescending);RefreshGrid();RefreshHighlights();_doc.Views.Redraw();}
      private void SortTableColumn(int column)
      {if(column<0||_tableEditing)return;ChangeSort(_tableGrid,column,ref _tableSortColumn,ref _tableSortDescending);RefreshTableRows();_doc.Views.Redraw();}
      private void SortMaterialColumn(int column)
      {if(column<0)return;ChangeSort(_materialUsageGrid,column,ref _materialSortColumn,ref _materialSortDescending);RefreshMaterialUsage(MaterialUsageTotals(VisibleResult,CurrentMaterialValues().Name));}
      private static int CompareListValues(object? first,object? second,bool descending)
      {
        string Text(object? value)=>Convert.ToString(value,System.Globalization.CultureInfo.CurrentCulture)??string.Empty;
        bool Missing(object? value)=>value==null||Text(value) is "" or "-" or UnknownYardageText;
        bool missingFirst=Missing(first),missingSecond=Missing(second);
        if(missingFirst||missingSecond)return missingFirst==missingSecond?0:missingFirst?1:-1;
        bool Number(object? value,out double number)=>double.TryParse(Text(value),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.CurrentCulture,out number)||
          double.TryParse(Text(value),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out number);
        int compared;
        if(Number(first,out var a)&&Number(second,out var b))compared=a.CompareTo(b);
        else if(first is IComparable comparable&&first.GetType()==second!.GetType())compared=first is string?
          StringComparer.CurrentCultureIgnoreCase.Compare(Text(first),Text(second)):comparable.CompareTo(second);
        else compared=StringComparer.CurrentCultureIgnoreCase.Compare(Text(first),Text(second));
        return descending?-Math.Sign(compared):Math.Sign(compared);
      }
      private static List<GridItem> SortedItems(IEnumerable<GridItem> items,int column,bool descending,Func<GridItem,object?> value)=>
        column==NoSortColumn?items.ToList():items.OrderBy(value,Comparer<object?>.Create((a,b)=>CompareListValues(a,b,descending))).ToList();

      internal void AddPreselectedParts(IReadOnlyCollection<Guid> preselection)
      {
        if(_closed||_placing||_liveTables.Count>0||preselection.Count==0) return;
        var original=OriginalTableSelection(_doc,preselection);
        if(original.Tables.Length>0)
        {
          var placed=original.Tables.SelectMany(table=>_doc.Groups.GroupMembers(table)).Select(obj=>obj.Id).ToHashSet();
          foreach(var id in placed)_doc.Objects.FindId(id)?.Select(false);
        }
        var ids=_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id).Concat(original.Objects)
          .Concat(_doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id))
          .Distinct().Where(id=>_doc.Objects.FindId(id) is {IsDeleted:false}).ToArray();
        foreach(var id in ids)
          if(_doc.Objects.FindId(id) is {} obj&&obj.IsSelected(false)==0) obj.Select(true);
        try
        {
          if(!_selection.SetEquals(ids)) { ReplaceSelection(ids); Queue(); }
          if(original.Tables.Length>0)
          {
            _pendingPlacedTables.UnionWith(original.Tables);_needsNest=false;
            Log.Write("vNest","preselected tables queued count={0} appCommands={1} docCommands={2}",_pendingPlacedTables.Count,RhinoApp.InCommand,_doc.InCommand(false));
            OpenPreselectedTables();
          }
          Log.Write("vNest","preselection added objects={0} parts={1}",preselection.Count,_rows.Count);
        }
        catch(Exception ex)
        { RestoreMemberSelection(); _status.Text="Selection has incomplete parts"; Log.Write("vNest","preselection skipped: {0}",ex); }
      }

      internal void SyncSelection()
      {
        if(_closed||_placing||_liveTables.Count>0||_pendingPlacedTables.Count>0||_picking.DefersSelectionSync) return;
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
        _searchTime.Value=SearchTime;
        _partGap.Value=PartGap;
        _grainDeviation.Value=GrainDeviation;
        _allowFlip.Checked=AllowFlip;
        RememberGlobalNumbers();
        _updating=false; MaterialValuesChanged(); Queue(); RefreshGrid();
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
        var working=_workingResult;
        int? selected=_picking.FocusMembers is {} focus
          ?next.FirstOrDefault(row=>row.Part.Items.Any(item=>focus.Contains(item.Id)))?.Id:Selected?.Id;
        _packingCancellation?.Cancel();
        ClearHighlights();
        RetainPreviewCopies(next);
        foreach(var row in _rows.Except(next)) row.Dispose();
        _rows.Clear(); _rows.AddRange(next); _selection=ids.ToHashSet();
        if(_rows.Any(row=>row.StoredMaterial is {} material&&!_materialPresets.Any(preset=>preset.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase))))
          RefreshMaterialPresets();
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
      {
        if(!_closed&&_previewHasFocus!=(_manualGesture!=null||_previewKeyboard.ViewportHasFocus))
        {RefreshMovingHighlights();if(!_previewHasFocus)_pivotVisible=false;_doc.Views.Redraw();}
        if(_pendingPlacedTables.Count>0) { OpenPreselectedTables(); return; }
        if(!_closed&&!_applyingLive&&!_replayingLiveHistory&&_liveUndoRecord==0&&_externalPartSettings.Count>0&&!PackingBlocked()) ReloadExternalPartSettings();
        if(_needsNest&&!_closed&&!_locationPicking&&!_packing&&!PackingBlocked()) ReNest();
      }
      private void PartSettingsChanged(object? sender,RhinoDoc.UserStringChangedArgs e)
      {
        if(_closed||_writingPartSettings||_applyingLive||_replayingLiveHistory||e.Document.RuntimeSerialNumber!=DocumentSerial) return;
        string? section=e.Key.StartsWith(PartSettingsSection+"\\",StringComparison.Ordinal)?PartSettingsSection:
          e.Key.StartsWith(PartNamesSection+"\\",StringComparison.Ordinal)?PartNamesSection:null;
        if(section!=null) _externalPartSettings.Add(e.Key[(section.Length+1)..]);
      }
      private void ReloadExternalPartSettings()
      {
        var keys=_externalPartSettings.ToHashSet();_externalPartSettings.Clear();bool changed=false;
        foreach(var row in _rows)
        {
          if(PartSettingsKey(_doc,row) is not {} key||!keys.Contains(key)) continue;
          string oldName=row.Name;LoadPartName(_doc,row);changed|=oldName!=row.Name;
          var restored=new Row(row.Part,row.Id,row.DetectedGuide) {DefaultName=row.DefaultName};
          if(!LoadPartSettings(_doc,restored)) continue;
          var before=SettingsOf(row);
          var beforeRecipe=row.StoredMaterial;
          row.Copies=restored.Copies;row.Included=restored.Included;row.Fixed=restored.Fixed;
          row.ManualRotation=restored.ManualRotation;row.Flipped=restored.Flipped;row.FlipOverride=restored.FlipOverride;
          row.GrainOverride=restored.GrainOverride;row.RotationOverride=restored.RotationOverride;
          row.RotationStepOverride=restored.RotationStepOverride;row.ObeyGrainOverride=restored.ObeyGrainOverride;
          row.AllowReverseOverride=restored.AllowReverseOverride;row.GrainDeviationOverride=restored.GrainDeviationOverride;
          row.MaterialName=restored.MaterialName;row.StoredMaterial=restored.StoredMaterial;
          row.Rotation=restored.Rotation;row.Table=restored.Table;row.X=restored.X;row.Y=restored.Y;
          row.Guide=restored.Guide;row.DrawnGuide=restored.DrawnGuide;
          changed|=before!=SettingsOf(row)||beforeRecipe!=row.StoredMaterial;
        }
        if(!changed) return;
        RefreshMaterialPresets();Queue();RefreshGrid();RefreshHighlights();_doc.Views.Redraw();
        Log.Write("vNest","reloaded externally edited part settings records={0}",keys.Count);
      }
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
        MaterialValuesChanged();
        if(before==(NestWorkflow.Width,Length,RotationStep,RotationChoice,Grain,AllowReverse,ObeyGrain,AllowFlip,SearchTime,PartGap,GrainDeviation)) return;
        Save(); Queue(); RefreshGrid();
      }
      private MaterialPreset CurrentMaterialValues()=>new(_activeMaterialName??CustomMaterialName,_width.Value,_partGap.Value,_grainDeviation.Value)
        {Grain=(GrainMode)_grain.SelectedIndex,ObeyGrain=_obeyDefault.Checked==true,AllowReverse=_allowReverse.SelectedIndex==1};
      private static bool SameMaterialValues(MaterialPreset first,MaterialPreset second)=>
        !FieldResetOverlay.NumberChanged(first.Width,second.Width,NumberDecimals)&&
        !FieldResetOverlay.NumberChanged(first.Gap,second.Gap,NumberDecimals)&&
        !FieldResetOverlay.NumberChanged(first.GrainDeviation,second.GrainDeviation,NumberDecimals)&&
        first.Grain==second.Grain&&first.ObeyGrain==second.ObeyGrain&&first.AllowReverse==second.AllowReverse;
      private void RefreshMaterialPresets(string? selected=null)
      {
        bool opening=_materialPresets.Count==0&&_activeMaterialName==null;
        _materialPresets=LoadMaterialPresets().Concat(_rows.Select(row=>row.StoredMaterial).OfType<MaterialPreset>())
          .DistinctBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase).OrderBy(preset=>preset.Name,StringComparer.OrdinalIgnoreCase).ToList();
        foreach(var name in _materialDrafts.Keys.ToArray())
          if(_materialPresets.FirstOrDefault(preset=>preset.Name.Equals(name,StringComparison.OrdinalIgnoreCase)) is not {} original||
            SameMaterialValues(original,_materialDrafts[name])) _materialDrafts.Remove(name);
        _activeMaterialName=selected??_activeMaterialName??(opening?_selectedMaterialName:null);
        if(_activeMaterialName==null) _activeMaterialName=_materialPresets.FirstOrDefault(preset=>SameMaterialValues(preset,CurrentMaterialValues()))?.Name;
        if(!_materialPresets.Any(preset=>preset.Name.Equals(_activeMaterialName,StringComparison.OrdinalIgnoreCase))) _activeMaterialName=null;
        if(opening&&_activeMaterialName!=null)
        {
          var values=_materialDrafts.GetValueOrDefault(_activeMaterialName)??
            _materialPresets.First(preset=>preset.Name.Equals(_activeMaterialName,StringComparison.OrdinalIgnoreCase));
          ApplyMaterialValues(values);
          NestWorkflow.Width=values.Width;PartGap=values.Gap;GrainDeviation=values.GrainDeviation;
          Grain=values.Grain;ObeyGrain=values.ObeyGrain;AllowReverse=values.AllowReverse;
        }
        _selectedMaterialName=_activeMaterialName;
        PersistMaterialDrafts();
        RefreshMaterialChoices();
      }
      private void RefreshMaterialChoices()
      {
        bool before=_updating; _updating=true;
        try
        {
          _material.DataStore=new[]{CustomMaterialName}.Concat(_materialPresets.Select(preset=>
            preset.Name+(_materialDrafts.ContainsKey(preset.Name)?MaterialUnsavedSuffix:string.Empty))).ToArray();
          _material.SelectedIndex=_materialPresets.FindIndex(preset=>preset.Name.Equals(_activeMaterialName,StringComparison.OrdinalIgnoreCase))+1;
          _saveMaterial.Enabled=_activeMaterialName!=null&&_materialDrafts.ContainsKey(_activeMaterialName);
        }
        finally {_updating=before;}
        foreach(var overlay in _resets) overlay.Refresh();
        if(_shown) UpdateEditor();
      }
      private void PersistMaterialDrafts()
      {
        if(SaveMaterialDrafts(_materialDrafts.Values,_persistedMaterialDrafts.Values))
          _persistedMaterialDrafts=new(_materialDrafts,StringComparer.OrdinalIgnoreCase);
      }
      private MaterialPreset RowMaterial(Row row)=>_liveParts.ContainsKey(row.Id)&&row.Table>=0&&row.Table<_liveTables.Count?_liveTables[row.Table].Material:row.MaterialName==null?CurrentMaterialValues():
        _materialDrafts.GetValueOrDefault(row.MaterialName)??_materialPresets.FirstOrDefault(preset=>preset.Name.Equals(row.MaterialName,StringComparison.OrdinalIgnoreCase))??row.StoredMaterial??CurrentMaterialValues();
      private GrainMode EffectiveGrainMode(Row row)=>row.GrainOverride??RowMaterial(row).Grain;
      private bool EffectiveObeyGrain(Row row)=>row.ObeyGrainOverride??RowMaterial(row).ObeyGrain;
      private bool EffectiveReverse(Row row)=>row.AllowReverseOverride??RowMaterial(row).AllowReverse;
      private MaterialPreset MaterialBaseline()=>_materialPresets.FirstOrDefault(preset=>preset.Name.Equals(_activeMaterialName,StringComparison.OrdinalIgnoreCase))??
        new MaterialPreset(CustomMaterialName,DefaultWidth,DefaultPartGap,DefaultGrainDeviation);
      private void ApplyMaterialValues(MaterialPreset values)
      {
        bool before=_updating;_updating=true;
        try
        {_width.Value=values.Width;_partGap.Value=values.Gap;_grainDeviation.Value=values.GrainDeviation;
          _grain.SelectedIndex=(int)values.Grain;_obeyDefault.Checked=values.ObeyGrain;_allowReverse.SelectedIndex=values.AllowReverse?1:0;RememberGlobalNumbers();}
        finally {_updating=before;}
      }
      private MaterialPreset TableMaterial(int table)=>VisibleResult.TableMaterials.GetValueOrDefault(table)??CurrentMaterialValues();
      private Plane DisplayTablePlane(int table)=>table>=0&&table<_liveTables.Count?_liveTables[table].Frame:
        new(_anchor+DisplayOffset(table)+_plane.YAxis*TableMaterial(table).Width,-_plane.YAxis,_plane.XAxis);
      private double TableLength(int table)=>table>=0&&table<_liveTables.Count?_liveTables[table].Length:Length;
      private void MaterialValuesChanged()
      {
        var current=CurrentMaterialValues();
        if(_activeMaterialName==null) _customMaterial=current;
        else if(_materialPresets.FirstOrDefault(preset=>preset.Name.Equals(_activeMaterialName,StringComparison.OrdinalIgnoreCase)) is {} original)
        {
          if(SameMaterialValues(original,current)) _materialDrafts.Remove(original.Name);
          else _materialDrafts[original.Name]=current;
        }
        PersistMaterialDrafts();
        RefreshMaterialChoices();
      }
      private void SelectMaterialPreset()
      {
        if(_updating||_closed||_material.SelectedIndex<0) return;
        int index=_material.SelectedIndex-1;
        var original=index>=0&&index<_materialPresets.Count?_materialPresets[index]:null;
        if(string.Equals(original?.Name,_activeMaterialName,StringComparison.OrdinalIgnoreCase)) return;
        _activeMaterialName=original?.Name;
        _selectedMaterialName=_activeMaterialName;
        var values=original==null?_customMaterial:_materialDrafts.GetValueOrDefault(original.Name,original);
        ApplyMaterialValues(values);
        int revision=_layoutRevision; GlobalChanged();
        Save();
        if(revision==_layoutRevision) {Queue(); RefreshGrid();}
      }
      private bool SaveMaterial(MaterialPreset values)
      {
        if(!SaveMaterialValues(values,true,out var error))
        { MessageBox.Show(this,error,"Material preset",MessageBoxButtons.OK,MessageBoxType.Error); return false; }
        _materialDrafts.Remove(values.Name); RefreshMaterialPresets(); Save(); return true;
      }
      private void SaveSelectedMaterial()
      { if(_activeMaterialName!=null) SaveMaterial(CurrentMaterialValues()); }
      private bool SaveMaterialAs(MaterialPreset values,bool selectSaved)
      {
        using var dialog=new Dialog<string> {Title="Save Material As",Padding=new Padding(WindowPadding),Resizable=false};
        var name=new TextBox {Width=FieldWidth*2,MaxLength=MaterialPresetNameLimit,
          Text=values.Name==CustomMaterialName?string.Empty:values.Name+MaterialPresetCopySuffix};
        var save=new Button {Text="Save"}; var cancel=new Button {Text="Cancel"};
        dialog.DefaultButton=save; dialog.AbortButton=cancel;
        var fields=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={new TableRow(Caption("Name"),name)}};
        var actions=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={new TableRow(new TableCell(null,true),save,cancel)}};
        dialog.Content=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={new TableRow(fields),new TableRow(actions)}};
        save.Click+=(_,_)=>
        {
          if(!SaveMaterialValues(values with {Name=name.Text},false,out var error))
          { MessageBox.Show(dialog,error,"Material preset",MessageBoxButtons.OK,MessageBoxType.Error); return; }
          dialog.Close(MaterialPresetName(name.Text));
        };
        cancel.Click+=(_,_)=>dialog.Close(string.Empty);
        dialog.Shown+=(_,_)=> {name.Focus(); name.SelectAll();};
        string? saved=dialog.ShowModal(this);
        if(string.IsNullOrEmpty(saved)) return false;
        _materialDrafts.Remove(values.Name);
        string? before=_activeMaterialName;
        RefreshMaterialPresets(selectSaved||string.Equals(values.Name,_activeMaterialName,StringComparison.OrdinalIgnoreCase)?saved:null);
        Save();
        if(before!=_activeMaterialName) {Queue(); RefreshGrid();}
        return true;
      }
      private bool ConfirmDiscardNest()
      {
        using var dialog=new Dialog<bool> {Title="Discard Nest?",Padding=new Padding(WindowPadding),Resizable=false};
        var discard=new Button {Text="Discard"};var cancel=new Button {Text="Cancel"};
        dialog.DefaultButton=cancel;dialog.AbortButton=cancel;
        var actions=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={new TableRow(new TableCell(null,true),discard,cancel)}};
        dialog.Content=new TableLayout {Spacing=new Size(ControlGap,ControlGap),Rows={
          new TableRow(new Label {Text="Discard the unplaced nest preview?"}),
          new TableRow(new Label {Text=$"{VisibleResult.Tables} tables; {VisibleResult.Placements.Count} parts"}),new TableRow(actions)}};
        discard.Click+=(_,_)=>dialog.Close(true);cancel.Click+=(_,_)=>dialog.Close(false);
        return dialog.ShowModal(this);
      }
      private void Edit(Action<Row> change)
      {
        if(_updating||_closed) return;
        EndManualGesture(true);
        var rows=SelectedParts; bool changed=false;
        foreach(var row in rows) { var before=SettingsOf(row); change(row); changed|=before!=SettingsOf(row); }
        if(!changed) return;
        foreach(var row in rows)
        {if(!row.Fixed&&!row.ManualRotation)foreach(int id in row.InstanceIds())_manualInstances.Remove(id);SavePartSettings(row);}
        Queue(); RefreshGrid(); RefreshHighlights(); _doc.Views.Redraw();
      }
      private void SavePartSettings(Row row)
      {
        bool wasWriting=_writingPartSettings;_writingPartSettings=true;
        try
        {
          if(PartSettingsKey(_doc,row) is {} key)
          {
            bool placed=IsPlacedPart(_doc,row);
            var data=HasReusableSettings(row)||placed&&IsCustomized(row)?CapturePartSettings(row,row.MaterialName==null?null:RowMaterial(row)):null;
            WritePartSettings(_doc,key,data==null||placed?data:ReusablePartSettings(data));
          }
        }
        catch(Exception ex) {Log.Write("vNest","could not remember part settings name={0}: {1}",row.Name,ex.Message);}
        finally {_writingPartSettings=wasWriting;}
      }
      private void Queue([System.Runtime.CompilerServices.CallerMemberName]string reason="")
      {
        if(_closed) return; EndManualGesture(true); _manualUndo.Clear();_manualRedo.Clear(); _layoutRevision++; _place.Enabled=false;
        if(_liveTables.Count>0){_packedRevision=_layoutRevision;_needsNest=false;RefreshGrid();return;}
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
        if(_closed||_locationPicking||_liveTables.Count>0) return;
        EndManualGesture(true);
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
          var batches=_rows.Where(row=>row.Included).GroupBy(row=>RowMaterial(row).Name,StringComparer.OrdinalIgnoreCase).Select(group=>
          {
            var material=RowMaterial(group.First());
            int copies=group.Sum(row=>Math.Clamp(row.Copies,DefaultCopies,MaximumCopies));
            foreach(var row in group)row.FreeRotationPoseLimit=RotationSearchLimit(row,copies);
            var previous=_result.TableMaterials.Where(pair=>pair.Value.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase)).Select(pair=>pair.Key).OrderBy(table=>table).ToList();
            var parts=group.SelectMany(row=>row.InstanceIds().Select(id=>
            {
              bool pinned=_manualInstances.TryGetValue(id,out var manual)&&(id!=row.Id||row.Fixed)&&
                _result.TableMaterials.GetValueOrDefault(manual.Table)?.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase)==true;
              IReadOnlyList<NestPackingEngine.Pose> poses=pinned?ManualPose(row,manual!.Pose.Rotation,manual.Pose.Flipped) is {} pose?[pose]:[]:MaterialPoses(row,material);
              return new NestPackingEngine.Input(id,poses,pinned||row.Fixed&&id==row.Id,
                pinned?Math.Max(0,previous.IndexOf(manual!.Table)):_result.TableMaterials.Count==0?row.Table:Math.Max(0,previous.IndexOf(row.Table)),
                pinned?manual!.X:row.X,pinned?manual!.Y:row.Y)
                {GrainGuided=UsesGrain(row)&&row.Guide.HasValue,Gap=material.Gap,TableAllowance=TableAllowanceForUnits(_doc.ModelUnitSystem)};
            })).ToList();
            var shuffled=ShuffleInputs(parts,_nestRandom);
            Log.Write("vNest","trial order material={0} ids={1}",material.Name,string.Join(",",shuffled.Where(input=>!input.Fixed).OrderBy(input=>input.ShuffleRank).Select(input=>input.Id)));
            return new MaterialBatch(material,shuffled);
          }).ToList();
          var inputs=batches.SelectMany(batch=>batch.Inputs).ToList();
          Log.Write("vNest","nest started parts={0} preselectedObjects={1} grain={2} width={3} length={4}",inputs.Count,_selection.Count,Grain,NestWorkflow.Width,Length);
          foreach(var row in _rows.Where(row=>row.Included))
            Log.Write("vNest","grain poses id={0} name={1} mode={2} guide={3} manual={4} fixed={5} rotations={6} obey={7} deviation={8} reverse={9}",
              row.Id,row.Name,EffectiveGrainMode(row),row.Guide?.ToString()??"missing",row.ManualRotation,row.Fixed,
              string.Join(",",inputs.First(input=>input.Id==row.Id).Poses.Select(pose=>pose.Rotation.ToString("G17",System.Globalization.CultureInfo.InvariantCulture))),
              UsesGrain(row),row.GrainDeviationOverride??RowMaterial(row).GrainDeviation,EffectiveReverse(row));
          int revision=_layoutRevision; double width=NestWorkflow.Width,length=Length,searchTime=SearchTime;
          var cancellation=new CancellationTokenSource(); _packingCancellation=cancellation;
          var incumbent=_packedRevision==revision?_result:null;
          _stopping=false; Interlocked.Exchange(ref _bestInProgress,incumbent);
          _workingResult=incumbent;
          _manualUndo.Clear();_manualRedo.Clear();_packing=true; _place.Enabled=false; _status.Text="Nesting...";
          RefreshGrid();
          _status.SetProgress(0,clock.Elapsed); UpdateNestButton();
          Action<string,string,int,int> stages=(material,stage,percent,total)=>Application.Instance.AsyncInvoke(()=>
          {
            if(_closed||!ReferenceEquals(_packingCancellation,cancellation)||!_packing||_stopping) return;
            _status.SetStageProgress(stage,percent,total); _status.ToolTip=material;
          });
          NestPackingEngine.Result? pending=null; int queued=0;
          Action<NestPackingEngine.Result> preview=result=>
          {
            Interlocked.Exchange(ref pending,result);
            if(Interlocked.CompareExchange(ref queued,1,0)!=0) return;
            Application.Instance.AsyncInvoke(()=>
            {
              var snapshot=Interlocked.Exchange(ref pending,null); Interlocked.Exchange(ref queued,0);
              if(snapshot==null||_closed||!_packing||!ReferenceEquals(_packingCancellation,cancellation)||cancellation.IsCancellationRequested||revision!=_layoutRevision) return;
              _workingResult=PreferLayout(snapshot,Volatile.Read(ref _bestInProgress));
              if(snapshot.Search is {} search) _status.ToolTip=$"{(snapshot.IsTrial?"Testing layout":"Best layout")}: {search.PlacementTests} placement tests; {search.Improvements} improvements";
              RefreshGrid();
              RefreshHighlights(); _doc.Views.Redraw();
            });
          };
          Action<NestPackingEngine.Result> accepted=result=>
          {
            if(ReferenceEquals(_packingCancellation,cancellation)&&revision==Volatile.Read(ref _layoutRevision))
              Interlocked.Exchange(ref _bestInProgress,PreferLayout(result,Volatile.Read(ref _bestInProgress)));
          };
          Task.Run(()=>SolveMaterialsWithStages(batches,length,searchTime,null,preview,cancellation.Token,accepted,null,stages),cancellation.Token).ContinueWith(task=>
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
              var chosen=PreferLayout(task.Result,currentBest);
              if(!ReferenceEquals(chosen,task.Result))
              {
                Log.Write("vNest","retained better previous layout tables={0} newTables={1}",chosen.Tables,task.Result.Tables);
                chosen=chosen with {Search=task.Result.Search is {} statistics?statistics with {ConsumedLength=UsedLength(chosen)}:null,Profile=task.Result.Profile};
              }
              ApplyPackingResult(chosen,revision,width,length);
              if(stopped||task.Result.Stopped) _status.Text=$"Stopped; {_result.Tables} tables; {_result.Placements.Count} parts";
              _status.Text+="; "+ElapsedText(clock.Elapsed);
            });
          });
        }
        catch(Exception ex) { clock.Stop(); _packing=false; UpdateNestButton(); _place.Enabled=false; _status.Text="Nesting failed; "+ElapsedText(clock.Elapsed); Log.Write("vNest","packing preparation failed: {0}",ex); }
      }

      private static double UsedLength(NestPackingEngine.Result result)=>NestPackingEngine.MaterialConsumption(result);
      private static NestPackingEngine.Result PreferLayout(NestPackingEngine.Result candidate,NestPackingEngine.Result? previous)
      {
        if(previous==null||candidate.Placements.Count>previous.Placements.Count) return candidate;
        if(candidate.Placements.Count<previous.Placements.Count) return previous;
        double used=UsedLength(candidate),saved=UsedLength(previous);
        if(used<saved-PoseDimensionTolerance) return candidate;
        if(used>saved+PoseDimensionTolerance) return previous;
        if(candidate.Tables!=previous.Tables)return candidate.Tables<previous.Tables?candidate:previous;
        double Left(NestPackingEngine.Result layout)=>layout.Placements.Where(part=>
          Math.Max(part.Pose.Width,part.Pose.Length)>=NestContourPackingEngine.MinimumSkinnyAspect*Math.Min(part.Pose.Width,part.Pose.Length)).Sum(part=>part.Y);
        double left=Left(candidate),savedLeft=Left(previous);
        if(Math.Abs(left-savedLeft)>PoseDimensionTolerance)return left<savedLeft?candidate:previous;
        return candidate.Placements.Sum(part=>part.X+part.Pose.Width)>previous.Placements.Sum(part=>part.X+part.Pose.Width)+PoseDimensionTolerance?candidate:previous;
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
        _pendingPlacedTables.Clear();
        if(_liveTables.Count>0)ExitPlacedTables();
        if(Busy()) { RhinoApp.WriteLine("vNest: finish the current command before clearing."); return; }
        EndManualGesture(false);ClearLayoutState();
        _needsNest=false; _layoutRevision++; _packedRevision=UnpackedRevision;
        _packingCancellation?.Cancel(); _stopping=false; Interlocked.Exchange(ref _bestInProgress,null);
        ClearPreviewCopies();ClearTableLabels();_place.Enabled=false;
        RefreshGrid();RefreshHighlights();_doc.Views.Redraw();
        _totalYardage.Text=TotalYardageText(_result,YardsPerUnit());
        RefreshMaterialUsage(MaterialUsageTotals(_result,CurrentMaterialValues().Name));
        _status.Text=$"{_rows.Count} parts; nest cleared";
        _status.EndProgress(); UpdateNestButton(); _status.ToolTip=string.Empty;
        Log.Write("vNest","nest cleared; {0} input parts retained",_rows.Count);
      }

      private void ClearLayoutState()
      {
        _manualInstances.Clear();_manualUndo.Clear();_manualRedo.Clear();_activePreviewInstance=null;_manualSelectedInstances.Clear();
        _workingResult=null;_result=new([],0,[]){TableAllowance=TableAllowanceForUnits(_doc.ModelUnitSystem)};
        foreach(var row in _rows)
        {
          row.Fixed=DefaultFixed;row.ManualRotation=DefaultManualRotation;row.Rotation=DefaultRotation;
          row.Table=DefaultTable;row.X=DefaultCoordinate;row.Y=DefaultCoordinate;row.Flipped=row.FlipOverride??DefaultAllowFlip;
          row.Status=row.Included?"Pending":"Excluded";
        }
      }

      private void FocusClickedPart(Guid[] ids,bool selected)
      {
        if(_closed||!selected) return;
        var members=ids.ToHashSet(); int index=_visibleRows.FindIndex(row=>row.Part.Items.Any(item=>members.Contains(item.Id)));
        if(index<0) return;
        if(Selected?.Id==_visibleRows[index].Id&&_grid.SelectedRows.Count()==1)return;
        bool previous=_updating; _updating=true;
        try { _grid.UnselectAll(); _grid.SelectRow(index); _grid.ScrollToRow(index); }
        finally { _updating=previous; }
        _activePreviewInstance=null;_manualSelectedInstances.Clear();
        UpdateEditor(); RefreshHighlights();
      }

      private void ApplyPackingResult(NestPackingEngine.Result result,int revision,double width,double length)
      {
        try
        {
          _result=result; _packedRevision=revision;
          _manualSelectedInstances.IntersectWith(result.Placements.Select(part=>part.Id));
          foreach(var row in _rows)
          {
            var packed=_result.Placements.FirstOrDefault(part=>part.Id==row.Id);
            if(packed!=null) { row.Table=packed.Table; row.X=packed.X; row.Y=packed.Y; row.Rotation=packed.Pose.Rotation; row.Flipped=packed.Pose.Flipped; }
            row.Status=PlacementStatus(row.Id,row.Included,_result,false,true);
          }
          _place.Enabled=_result.Placements.Count>0;
          _status.Text=$"{_result.Tables} tables; {_result.Placements.Count} parts; {_result.Errors.Count} skipped";
          RefreshGrid(Selected?.Id); RefreshHighlights(); _doc.Views.Redraw();
          Log.Write("vNest","preview tables={0} parts={1} errors={2} width={3:G17} length={4:G17}",_result.Tables,_result.Placements.Count,_result.Errors.Count,width,length);
          RhinoApp.WriteLine($"vNest: preview {_result.Tables} table(s), {_result.Placements.Count} part copies, {_result.Errors.Count} skipped; {TotalYardageText(_result,YardsPerUnit())}.");
          foreach(var line in MaterialUsageLines(MaterialUsageTotals(_result,CurrentMaterialValues().Name),YardsPerUnit()))
            RhinoApp.WriteLine(line);
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
            var material=RowMaterial(row); var poses=MaterialPoses(row,material); var best=poses.OrderBy(pose=>Math.Max(pose.Width/material.Width,pose.Length/Length)).FirstOrDefault();
            Log.Write("vNest","rejected part={0} reason={1} grain={2} poses={3} bestWidth={4} bestLength={5} source={6}",row.Id,_result.Errors[row.Id],EffectiveGrainMode(row),poses.Count,
              best==null?"none":best.Width.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
              best==null?"none":best.Length.ToString("R",System.Globalization.CultureInfo.InvariantCulture),string.Join(",",row.Part.Items.Select(item=>item.Id)));
          }
        }
        catch(Exception ex) { _place.Enabled=false; _status.Text="Nesting failed"; Log.Write("vNest","packing display failed: {0}",ex); }
      }

      private void RefreshGrid(int? selected=null)
      {
        _customizedRowColors=null;
        UpdateRowPalette();
        var selectedIds=selected.HasValue?new HashSet<int> {selected.Value}:SelectedParts.Select(row=>row.Id).ToHashSet();
        var result=VisibleResult; var placements=result.Placements.ToDictionary(part=>part.Id);
        _totalYardage.Text=TotalYardageText(result,YardsPerUnit());
        var usage=MaterialUsageTotals(result,CurrentMaterialValues().Name);
        _totalYardage.ToolTip=string.Join(System.Environment.NewLine,usage.Select(material=>material.Name+": "+YardageText(material.OccupiedLength,YardsPerUnit())));
        RefreshMaterialUsage(usage);
        _close.Text=_liveTables.Count==0&&result.Tables>0?"Cancel":"Close";
        _close.ToolTip=result.Tables>0?"Cancel the unplaced tables and close without creating objects":"Close the nesting window";
        _updating=true;
        var items=_rows.Select(row=>
        {
          var placement=placements.GetValueOrDefault(row.Id);
          bool live=_packing&&_workingResult!=null;
          row.Status=PlacementStatus(row.Id,row.Included,result,_packing,_packing?live:_packedRevision==_layoutRevision);
          int count=result.Placements.Count(part=>row.OwnsInstance(part.Id));
          if(row.Copies>DefaultCopies&&row.Included) row.Status=$"{count}/{row.Copies} placed; "+row.Status;
          return new GridItem {Tag=row,Values=[row.Included,row.Name+(IsCustomized(row)?" *":string.Empty),placement==null?"-":((live?placement.Table:row.Table)+1).ToString(),
            (live?placement?.Pose.Rotation??row.Rotation:row.Rotation).ToString("G6"),row.Fixed,row.Status,row.Copies,RowMaterial(row).Name,
            EffectiveGrainMode(row).ToString(),EffectiveGuide(EffectiveGrainMode(row),GuideAngle(row))?.ToString("G6")??"-",
            EffectiveObeyGrain(row),(row.GrainDeviationOverride??RowMaterial(row).GrainDeviation).ToString("G6"),
            UsesGrain(row)?ReversalLabels[EffectiveReverse(row)?1:0]:RotationLabels[(int)(row.RotationOverride??RotationChoice)],
            !UsesGrain(row)&&(row.RotationOverride??RotationChoice)==RotationMode.Custom?(row.RotationStepOverride??RotationStep).ToString("G6"):"-",
            live?placement?.Pose.Flipped??row.Flipped:row.Flipped,(live?placement?.X??row.X:row.X).ToString("G6"),(live?placement?.Y??row.Y:row.Y).ToString("G6"),GrainGuideSource(row,EffectiveGrainMode(row))]};
        }).ToList();
        var ordered=SortedItems(items,_partSortColumn,_partSortDescending,item=>_partSortColumn==1?((Row)item.Tag!).Name:item.Values[PartColumnValueIndices[_partSortColumn]]);
        _visibleRows=ordered.Select(item=>(Row)item.Tag!).ToList();_grid.DataStore=ordered;
        for(int index=0;index<_visibleRows.Count;index++) if(selectedIds.Contains(_visibleRows[index].Id)) _grid.SelectRow(index);
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
        foreach(var control in new Control[]{_included,_fixed,_flipped,_table,_angle,_x,_y,_limit,_partGrain,_partRotation,_partMaterial,_obey,_guideAngle,_partDeviation,_defaultDeviation,_copies,_grainPick,_autoRotation,_resetPart}) control.Enabled=row!=null;
        foreach(var field in _mixedNumbers.Values) field.Enabled=row!=null;
        _table.Enabled=_x.Enabled=_y.Enabled=rows.Length==1;
        if(row!=null)
        {
          var placement=VisibleResult.Placements.FirstOrDefault(part=>part.Id==(_activePreviewInstance.HasValue&&rows.Length==1?_activePreviewInstance.Value:row.Id));
          ShowCheck(_included,rows.Select(part=>part.Included)); ShowCheck(_fixed,rows.Select(part=>part.Fixed));
          ShowCheck(_flipped,rows.Select(part=>part.Flipped));
          _table.Value=(placement?.Table??row.Table)+1; _x.Value=placement?.X??row.X; _y.Value=placement?.Y??row.Y;
          ShowNumber(_angle,rows.Select(part=>NormalizeRotation(rows.Length==1&&placement!=null?placement.Pose.Rotation:
            VisibleResult.Placements.FirstOrDefault(copy=>copy.Id==part.Id)?.Pose.Rotation??part.Rotation)));
          ShowNumber(_copies,rows.Select(part=>(double)part.Copies));
          ShowChoice(_partMaterial,new[]{"Default"}.Concat(_materialPresets.Select(preset=>preset.Name)),rows.Select(part=>
            part.MaterialName==null?0:_materialPresets.FindIndex(preset=>preset.Name.Equals(part.MaterialName,StringComparison.OrdinalIgnoreCase))+1));
          ShowNumber(_limit,rows.Select(part=>part.RotationStepOverride??RotationStep));
          ShowChoice(_partGrain,new[]{"Default"}.Concat(GrainNames),rows.Select(part=>part.GrainOverride.HasValue?(int)part.GrainOverride+1:0));
          bool constrained=rows.Any(UsesGrain);
          ShowChoice(_partRotation,new[]{"Default"}.Concat(constrained?ReversalLabels:RotationLabels),rows.Select(part=>RotationIndex(part,constrained)));
          bool custom=rows.All(part=>!UsesGrain(part)&&(part.RotationOverride??RotationChoice)==RotationMode.Custom);
          _limit.Enabled=custom; _mixedNumbers[_limit].Enabled=custom;
          ShowCheck(_obey,rows.Select(EffectiveObeyGrain));
          ShowNumber(_guideAngle,rows.Select(part=>NormalizeRotation(EffectiveGuide(EffectiveGrainMode(part),GuideAngle(part))??SourceGrainAngle)));
          ShowCheck(_defaultDeviation,rows.Select(part=>!part.GrainDeviationOverride.HasValue));
          ShowNumber(_partDeviation,rows.Select(part=>part.GrainDeviationOverride??RowMaterial(part).GrainDeviation));
          _partDeviation.Enabled=constrained; _mixedNumbers[_partDeviation].Enabled=constrained; _defaultDeviation.Enabled=constrained;
          foreach(var field in new[]{_table,_x,_y}) _displayedNumbers[field]=field.Value;
        }
        _updating=false;
        if(_liveTables.Count>0)SetPlacedControls(true);
        foreach(var reset in _resets) reset.Refresh();
      }
      private bool UsesGrain(Row row)=>GrainConstrained(EffectiveGrainMode(row),GuideAngle(row),EffectiveObeyGrain(row));
      private void AddSelected()
      {
        if(_liveTables.Count>0){_status.Text="Return to Original Parts to change nesting inputs";return;}
        if(Busy()) return;
        SyncSelection();
      }
      private void RemoveSelected()
      {
        if(_liveTables.Count>0){_status.Text="Return to Original Parts to change nesting inputs";return;}
        if(Busy()) return;
        int neighbor=_grid.SelectedRows.Where(index=>index>=0&&index<_visibleRows.Count).DefaultIfEmpty(-1).Min();
        var ids=SelectedParts.SelectMany(row=>row.Part.Items).Select(item=>item.Id).Distinct().ToArray();
        foreach(var id in ids) _doc.Objects.FindId(id)?.Select(false);
        SyncSelection();
        if(neighbor>=0&&_visibleRows.Count>0) { _grid.UnselectAll(); _grid.SelectRow(Math.Min(neighbor,_visibleRows.Count-1)); }
      }
      private bool RequestMissingGrain()
      {
        if(_grainRequest!=null) return true;
        if(_closed||_locationPicking) return false;
        var row=_rows.FirstOrDefault(row=>row.Included&&EffectiveObeyGrain(row)&&EffectiveGrainMode(row)==GrainMode.Force&&row.Guide==null);
        if(row!=null) RequestGrain(row);
        return row!=null;
      }
      private void RequestGrain(Row row)
      {
        if(_grainRequest!=null||_locationPicking||Busy()) return;
        _grainRequest=row; Visible=false;
        DeferredNativeCommand.Run(_doc,"_vNestGrainProxy",result=>
        {
          RestoreRepeatTarget();
          _grainRequest=null;
          if(_closed) return;
          if(result!=Result.Success) _needsNest=false;
          if(result==Result.Success)
          {
            if(row.Guide is {} guide)
              foreach(var selected in SelectedParts.Append(row).Distinct())
              {
                var direction=guide-selected.Part.Plane.Normal*(guide*selected.Part.Plane.Normal);
                if(direction.Unitize()) selected.Guide=direction;
                if(selected!=row&&row.DrawnGuide.HasValue&&direction.IsValid&&direction.Length>RhinoMath.ZeroTolerance)
                {
                  var bounds=selected.Part.Outline.GetBoundingBox(selected.Part.Plane);
                  var center=selected.Part.Plane.PointAt(bounds.Center.X,bounds.Center.Y);
                  double span=Math.Min(bounds.Diagonal.X/Math.Max(Math.Abs(direction*selected.Part.Plane.XAxis),RhinoMath.ZeroTolerance),
                    bounds.Diagonal.Y/Math.Max(Math.Abs(direction*selected.Part.Plane.YAxis),RhinoMath.ZeroTolerance))*DrawnGuideSpanFraction;
                  selected.DrawnGuide=new Line(center-direction*span/2,center+direction*span/2);
                }
                if(EffectiveGrainMode(selected)==GrainMode.Off) selected.GrainOverride=GrainMode.Auto;
              }
            Queue();
            foreach(var selected in SelectedParts.Append(row).Distinct()) SavePartSettings(selected);
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
          RestoreRepeatTarget();
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
          var labelOffset=LocationLabelOffset();
          getter.SetBasePoint(previous+labelOffset,false); getter.AcceptNothing(true);
          getter.DynamicDraw+=(_,e)=> { _anchor=AnchorFromLabelLocation(e.CurrentPoint); _preview.DrawTables(e.Display,true); _preview.Draw(e.Display,true); _preview.DrawPartHighlights(e); };
          var result=getter.Get();
          if(result==GetResult.Point) { _anchor=AnchorFromLabelLocation(getter.Point()); _anchored=true; return Result.Success; }
          if(result==GetResult.Nothing) { _anchored=true; return Result.Success; }
          _anchor=previous; _anchored=previousAnchored; return Result.Cancel;
        }
        catch { _anchor=previous; _anchored=previousAnchored; throw; }
        finally { _preview.Enabled=!_closed; _doc.Views.Redraw(); }
      }

      private void ClearHighlights()
      { _highlight.SetObjects([]);_inactiveHighlight.SetObjects([]); }
      private static double MetadataNumber(ObjectAttributes attributes,string key,double fallback)=>
        double.TryParse(attributes.GetUserString(key),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)?value:fallback;
      private List<LiveTable> ReadPlacedTables(IReadOnlyCollection<int>? selectedGroups=null)
      {
        var result=new List<LiveTable>();
        foreach(int group in NestTableMetadata.TableGroups(_doc).Where(group=>selectedGroups==null||selectedGroups.Contains(group)).Order())
        {
          var members=_doc.Groups.GroupMembers(group);
          var border=members.FirstOrDefault(obj=>NestTableMetadata.IsTableBorder(_doc,obj)&&obj.Geometry is Curve);
          if(border?.Geometry is not Curve curve||!curve.TryGetPlane(out var curvePlane)||!curve.TryGetPolyline(out var polyline))continue;
          var labels=members.Where(obj=>obj.Geometry is TextEntity&&obj.Attributes.GetUserString(NestTableMetadata.RoleKey)==NestTableMetadata.LabelRole).ToArray();
          var longest=Enumerable.Range(0,polyline.SegmentCount).Select(polyline.SegmentAt).OrderByDescending(line=>line.Length).First();
          var roll=longest.Direction;if(!roll.Unitize())continue;
          var reference=labels.Select(obj=>((TextEntity)obj.Geometry).Plane.XAxis).FirstOrDefault(_plane.XAxis);
          if(roll*reference<0)roll=-roll;
          var across=Vector3d.CrossProduct(curvePlane.Normal,roll);if(!across.Unitize())continue;
          var basePlane=new Plane(_plane.Origin,roll,across);var bounds=curve.GetBoundingBox(basePlane);
          double length=MetadataNumber(border.Attributes,MetadataPrefix+"tableLength",longest.Length);
          double width=bounds.Diagonal.Y;
          var frame=new Plane(basePlane.PointAt(bounds.Max.X-length,bounds.Max.Y),-across,roll);
          try
          {
            var stored=System.Text.Json.JsonSerializer.Deserialize<double[]>(border.Attributes.GetUserString(TableFrameKey)??"null");
            var samples=System.Text.Json.JsonSerializer.Deserialize<double[]>(border.Attributes.GetUserString(TableFrameSamplesKey)??"null");
            if(stored is {Length:9}&&samples is {Length:9}&&stored.All(double.IsFinite)&&samples.All(double.IsFinite))
            {
              var original=new Plane(new Point3d(stored[0],stored[1],stored[2]),new Vector3d(stored[3],stored[4],stored[5]),new Vector3d(stored[6],stored[7],stored[8]));
              var oldPoints=Enumerable.Range(0,3).Select(index=>new Point3d(samples[index*3],samples[index*3+1],samples[index*3+2])).ToArray();
              var current=TableFrameSampleFractions.Select(curve.PointAtNormalizedLength).ToArray();
              var oldPlane=new Plane(oldPoints[0],oldPoints[1],oldPoints[2]);var currentPlane=new Plane(current[0],current[1],current[2]);
              double distance=oldPoints[0].DistanceTo(oldPoints[1]);
              if(original.IsValid&&oldPlane.IsValid&&currentPlane.IsValid&&distance>RhinoMath.ZeroTolerance)
              {
                double scale=current[0].DistanceTo(current[1])/distance;
                var transform=Transform.PlaneToPlane(oldPlane,currentPlane)*Transform.Scale(oldPoints[0],scale);
                original.Transform(transform);frame=original;
                width=MetadataNumber(border.Attributes,MetadataPrefix+"tableWidth",width)*scale;length*=scale;
              }
            }
          }
          catch(System.Text.Json.JsonException ex){Log.Write("vNest","placed table frame metadata ignored: {0}",ex.Message);}
          if(!frame.IsValid||width<=0||length<=0)continue;
          string name=border.Attributes.GetUserString(MetadataPrefix+"tableName")??_doc.Groups.GroupName(group);
          string materialName=border.Attributes.GetUserString(MetadataPrefix+"material")??CustomMaterialName;
          MaterialPreset? recipe=null;
          foreach(int child in members.SelectMany(obj=>obj.Attributes.GetGroupList()??[]).Distinct().Where(index=>index!=group))
          {
            var json=_doc.Strings.GetValue(PartSettingsSection,"group-"+_doc.Groups[child].Id.ToString("N"));
            if(string.IsNullOrEmpty(json))continue;
            try{recipe=System.Text.Json.JsonSerializer.Deserialize<StoredPartSettings>(json)?.Recipe;}catch(System.Text.Json.JsonException){ }
            if(recipe!=null)break;
          }
          var material=(recipe??_materialPresets.FirstOrDefault(value=>value.Name.Equals(materialName,StringComparison.OrdinalIgnoreCase))??CurrentMaterialValues()) with {Name=materialName,Width=width};
          if(double.TryParse(_doc.Strings.GetValue(TableGapSection,_doc.Groups[group].Id.ToString("N")),System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,out var gap)&&double.IsFinite(gap)&&gap>=DefaultPartGap)material=material with {Gap=gap};
          var ordered=labels.OrderBy(obj=>obj.Geometry.GetBoundingBox(frame).Center.X).ToArray();
          var divider=members.FirstOrDefault(obj=>obj.Attributes.GetUserString(NestTableMetadata.RoleKey)==NestTableMetadata.LabelDividerRole);
          result.Add(new(group,border.Id,curve.DuplicateCurve(),frame,length,material,name,ordered.Select(obj=>(TextEntity)obj.Geometry.Duplicate()).ToArray(),ordered.Select(obj=>obj.Id).ToArray(),divider?.Id??Guid.Empty));
        }
        return result;
      }
      private Guid? PlacedPartAt(MouseCallbackEventArgs e)
      {
        if(e.View?.ActiveViewport is not {} view||!view.GetFrustumLine(e.ViewportPoint.X,e.ViewportPoint.Y,out var ray))return null;
        if(CanFineTune&&PreviewHit(ray)!=null)return null;
        foreach(var obj in _doc.Objects.GetObjectList(ObjectType.Curve).Where(obj=>!obj.IsDeleted&&!obj.IsHidden&&
          !string.IsNullOrEmpty(obj.Attributes.GetUserString(MetadataPrefix+"part"))&&obj.Geometry is Curve curve&&curve.IsClosed))
        {
          var curve=(Curve)obj.Geometry;
          if(curve.TryGetPlane(out var plane)&&NestDocumentPicking.Inside(curve,plane,ray,_doc.ModelAbsoluteTolerance,out _))return obj.Id;
        }
        return null;
      }
      private void SetPlacedControls(bool editing)
      {
        if(_settingsPanel!=null)_settingsPanel.Enabled=true;
        foreach(var control in new Control[]{_material,_saveMaterialAs,_width,_length,_searchTime,_grain,_grainDeviation,_obeyDefault,_allowReverse,_rotationPreset})control.Enabled=!editing;
        _saveMaterial.Enabled=!editing&&_activeMaterialName!=null&&_materialDrafts.ContainsKey(_activeMaterialName);
        _rotationStep.Enabled=!editing&&RotationChoice==RotationMode.Custom;
        bool updating=_updating;_updating=true;
        try
        {
          int? table=GapTables().Cast<int?>().FirstOrDefault();
          _partGap.Value=editing&&table.HasValue?_liveTables[table.Value].Material.Gap:PartGap;
          _displayedNumbers[_partGap]=_partGap.Value;
          _partGap.ToolTip=editing?"Collision gap for selected placed tables, in document units. Changing it does not reposition parts; saved with the document.":
            "Minimum distance between part outlines in document units; zero permits touching. Does not add a table-edge margin.";
        }
        finally{_updating=updating;}
        _partGap.Enabled=true;
        if(_locationAction!=null)_locationAction.Enabled=!editing;
        _nest.Enabled=!editing;_auto.Enabled=!editing;_partMaterial.Enabled=!editing;_copies.Enabled=!editing;
        _included.Enabled=!editing;_place.Enabled=!editing&&_result.Placements.Count>0;
        _editTables.Text=editing?"Finish Table Editing":"Edit Placed Tables";
        _editTables.ToolTip=editing?"Finish editing placed tables and return to source parts; keeps all placed geometry in the drawing":
          "Open placed nest tables for immediate movement and rotation of their existing parts";
      }
      private int[] GapTables()=>_liveTables.Count==0?[]:_lists.SelectedIndex==1&&_tableGrid.SelectedItem is GridItem {Tag:int table}?[table]:
        SelectedParts.Select(row=>row.Table).Where(index=>index>=0&&index<_liveTables.Count).Distinct().ToArray();
      private void ChangeLiveGap()
      {
        double gap=_partGap.Value;EndManualGesture(true);
        var tables=GapTables();if(tables.Length==0)return;
        var materials=new Dictionary<int,MaterialPreset>(_result.TableMaterials);
        foreach(int table in tables)
        {
          var info=_liveTables[table];var material=info.Material with {Gap=gap};
          _liveTables[table]=info with {Material=material};materials[table]=material;
          _doc.Strings.SetString(TableGapSection,_doc.Groups[info.Group].Id.ToString("N"),gap.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        }
        _doc.Modified=true;_result=_result with {TableMaterials=materials};
        RefreshGrid();_status.Text=$"Collision gap updated for {tables.Length} working table(s)";
        Log.Write("vNest","live collision gap={0} tables={1}",gap,string.Join(",",tables));
      }
      private void OpenPreselectedTables()
      {
        if(_closed||_locationPicking||_packing||PackingBlocked()||_pendingPlacedTables.Count==0)return;
        var groups=_pendingPlacedTables.ToArray();_pendingPlacedTables.Clear();
        EnterPlacedTables(null,groups);
      }
      private void EnterPlacedTables(Guid? clicked=null,IReadOnlyCollection<int>? selectedGroups=null)
      {
        if(_closed||_liveTables.Count>0||_packing||PackingBlocked())return;
        EndManualGesture(true);NestPackingDependency.EnsurePolygonsLoaded();
        var tables=ReadPlacedTables(selectedGroups);if(tables.Count==0){_status.Text="No placed nest tables found";Log.Write("vNest","placed-table edit found no readable frames requested={0}",selectedGroups?.Count??0);return;}
        var rows=new List<Row>();var placements=new List<NestPackingEngine.Placement>();var ids=new Dictionary<int,Guid[]>();
        try
        {
          for(int table=0;table<tables.Count;table++)
          {
            var info=tables[table];
            var parts=PlanarPartSelection.Capture(_doc,_doc.Groups.GroupMembers(info.Group).Where(obj=>!NestTableMetadata.IsDecoration(_doc,obj)).Select(obj=>obj.Id).ToArray(),_doc.ModelAbsoluteTolerance,requirePlanarDetails:false);
            foreach(var part in parts)
            {
              EnsureCutFootprint(_doc,part);
              var attributes=part.Items.First().Attributes;
              double angle=NormalizeRotation(MetadataNumber(attributes,MetadataPrefix+"rotation",DefaultRotation));
              bool flipped=bool.TryParse(attributes.GetUserString(MetadataPrefix+"flipped"),out var mirror)&&mirror;
              var basis=new Plane(info.Frame.Origin,info.Frame.YAxis,-info.Frame.XAxis);
              var sourcePlane=PlanarPartSplitter.RotatedFrame(basis,RhinoMath.ToRadians(angle));
              var box=part.Outline.GetBoundingBox(info.Frame);
              if(flipped)
              {
                var reflection=Transform.Mirror(info.Frame.PointAt(box.Center.X,0),info.Frame.XAxis);
                part.Outline.Transform(reflection);foreach(var item in part.Items)item.Geometry.Transform(reflection);
              }
              var canonical=new PlanarPartSelection.Part(part.Outline.DuplicateCurve(),sourcePlane,part.Attributes.Duplicate(),part.Items.Select(item=>item with {Geometry=item.Geometry.Duplicate(),Attributes=item.Attributes.Duplicate()}).ToList());
              part.Dispose();var row=CreateRow(_doc,canonical);
              row.Copies=DefaultCopies;row.Table=table;row.X=box.Min.X;row.Y=box.Min.Y;row.Rotation=angle;row.Flipped=flipped;
              row.MaterialName=info.Material.Name==CustomMaterialName?null:info.Material.Name;row.StoredMaterial=info.Material;
              var flags=(row.Fixed,row.ManualRotation,row.ObeyGrainOverride);
              row.Fixed=true;row.ManualRotation=true;row.ObeyGrainOverride=false;
              var pose=MaterialPoses(row,info.Material).FirstOrDefault(value=>value.Flipped==flipped);
              (row.Fixed,row.ManualRotation,row.ObeyGrainOverride)=flags;
              if(pose==null){row.Dispose();continue;}
              rows.Add(row);ids[row.Id]=row.Part.Items.Select(item=>item.Id).ToArray();placements.Add(new(row.Id,table,box.Min.X,box.Min.Y,pose));
            }
          }
          if(rows.Count==0){foreach(var table in tables)table.Dispose();_status.Text="Placed tables have no editable parts";return;}
          _originalLayout=new(_rows.ToList(),_result,_anchor,_anchored,_layoutRevision,_packedRevision,new(_manualInstances));
          ClearHighlights();ClearPreviewCopies();ClearTableLabels();_rows.Clear();_rows.AddRange(rows);
          _liveTables.AddRange(tables);foreach(var pair in ids)_liveParts[pair.Key]=pair.Value;
          _result=new(placements,tables.Count,[]){TableAllowance=TableAllowanceForUnits(_doc.ModelUnitSystem),TableMaterials=tables.Select((table,index)=>(index,table.Material)).ToDictionary(pair=>pair.index,pair=>pair.Material)};
          _workingResult=null;_manualInstances.Clear();_manualUndo.Clear();_manualRedo.Clear();_needsNest=false;
          _packedRevision=++_layoutRevision;_selection=rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id).ToHashSet();
          _doc.Objects.UnselectAll();
          var active=rows.FirstOrDefault(row=>clicked.HasValue&&row.Part.Items.Any(item=>item.Id==clicked))??rows[0];
          _activePreviewInstance=active.Id;_manualSelectedInstances.Clear();RefreshMaterialPresets();RefreshGrid(active.Id);SetPlacedControls(true);RefreshHighlights();
          RefreshLiveLabelFormatting();
          _status.Text=$"Editing {tables.Count} placed tables; changes are immediate";_doc.Views.Redraw();
          Log.Write("vNest","placed-table editing started tables={0} parts={1}",tables.Count,rows.Count);
        }
        catch(Exception ex)
        {
          if(_originalLayout!=null)ExitPlacedTables();
          else{foreach(var row in rows)row.Dispose();foreach(var table in tables)table.Dispose();}
          _status.Text="Could not open placed tables";Log.Write("vNest","placed-table editing failed: {0}",ex);
        }
      }
      private void ExitPlacedTables()
      {
        if(_originalLayout is not {} original)return;EndManualGesture(true);
        bool enabled=_preview.Enabled;_preview.Enabled=false;
        try
        {
        ClearHighlights();ClearPreviewCopies();foreach(var row in _rows)row.Dispose();foreach(var table in _liveTables)table.Dispose();
        _liveTables.Clear();_liveParts.Clear();_rows.Clear();_rows.AddRange(original.Rows);_result=original.Result;
        _anchor=original.Anchor;_anchored=original.Anchored;_layoutRevision=original.Revision;_packedRevision=original.PackedRevision;
        _originalLayout=null;_manualInstances.Clear();foreach(var pair in original.Instances)_manualInstances[pair.Key]=pair.Value;
        _manualUndo.Clear();_manualRedo.Clear();_activePreviewInstance=null;_manualSelectedInstances.Clear();
        _selection=_rows.SelectMany(row=>row.Part.Items).Select(item=>item.Id).ToHashSet();_doc.Objects.UnselectAll();foreach(var id in _selection)_doc.Objects.FindId(id)?.Select(true);
        SetPlacedControls(false);RefreshGrid();RefreshHighlights();
        _status.Text="Table editing finished; showing source parts. Placed tables retained.";
        }
        finally{_preview.Enabled=enabled&&!_closed;_doc.Views.Redraw();}
      }
      private void EndLiveRecord()
      {if(_liveUndoRecord!=0){_doc.EndUndoRecord(_liveUndoRecord);_liveUndoRecord=0;}}
      private bool ExecuteLiveHistory(bool redo)
      {
        // The native command closes the inverse record; RhinoDoc.Undo alone leaves it open.
        bool success=RhinoApp.ExecuteCommand(_doc,redo?"_Redo":"_Undo")==Result.Success;
        RestoreRepeatTarget();return success;
      }
      private void UpdateLiveUsage()
      {
        for(int table=0;table<_liveTables.Count;table++)
        {
          var info=_liveTables[table];double used=OccupiedLength(_result,table);int count=_result.Placements.Count(part=>part.Table==table);
          if(info.LabelIds.Length<3)continue;
          var labels=BuildLiveTableLabels(table);
          try
          {
            if(info.Outline.DataCRC(0)!=labels.Outline.DataCRC(0)&&!_doc.Objects.Replace(info.Border,labels.Outline,true))
              throw new InvalidOperationException("Could not refresh table label frame.");
            Guid dividerId=info.Divider;
            if(TryTableLabelDivider(labels.Outline,out var divider))
            {
              using var curve=new LineCurve(divider);
              if(_doc.Objects.FindId(dividerId) is {} existingDivider)
              {
                if(existingDivider.Geometry.DataCRC(0)!=curve.DataCRC(0)&&!_doc.Objects.Replace(dividerId,curve,true))
                  throw new InvalidOperationException("Could not refresh table label divider.");
              }
              else
              {
                using var attributes=_doc.Objects.FindId(info.Border)!.Attributes.Duplicate();attributes.ObjectId=Guid.Empty;attributes.Name=string.Empty;
                attributes.SetUserString(NestTableMetadata.RoleKey,NestTableMetadata.LabelDividerRole);
                dividerId=_doc.Objects.AddCurve(curve,attributes);
                if(dividerId==Guid.Empty)throw new InvalidOperationException("Could not add table label divider.");
              }
            }
            for(int index=0;index<labels.Text.Length;index++)
            {
              if(_doc.Objects.FindId(info.LabelIds[index])?.Geometry is not TextEntity existing)
                throw new InvalidOperationException("A table label is missing.");
              if(!SameTableLabelLayout(existing,labels.Text[index])&&
                !_doc.Objects.Replace(info.LabelIds[index],(GeometryBase)labels.Text[index],true))throw new InvalidOperationException("Could not refresh table label.");
            }
            if(_doc.Objects.FindId(info.Border) is {} border)
            {
              using var attributes=border.Attributes.Duplicate();attributes.SetUserString(MetadataPrefix+"occupiedLength",used.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
              attributes.SetUserString(MetadataPrefix+"partCount",count.ToString(System.Globalization.CultureInfo.InvariantCulture));
              attributes.SetUserString(TableFrameKey,System.Text.Json.JsonSerializer.Serialize(new[]{info.Frame.OriginX,info.Frame.OriginY,info.Frame.OriginZ,
                info.Frame.XAxis.X,info.Frame.XAxis.Y,info.Frame.XAxis.Z,info.Frame.YAxis.X,info.Frame.YAxis.Y,info.Frame.YAxis.Z}));
              attributes.SetUserString(TableFrameSamplesKey,System.Text.Json.JsonSerializer.Serialize(TableFrameSampleFractions.SelectMany(fraction=>
                {var point=labels.Outline.PointAtNormalizedLength(fraction);return new[]{point.X,point.Y,point.Z};}).ToArray()));
              attributes.SetUserString(MetadataPrefix+"tableLength",info.Length.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
              attributes.SetUserString(MetadataPrefix+"tableWidth",info.Material.Width.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
              _doc.Objects.ModifyAttributes(border.Id,attributes,true);
            }
            _liveTables[table]=info with {Outline=labels.Outline.DuplicateCurve(),Labels=labels.Text.Select(text=>(TextEntity)text.Duplicate()).ToArray(),Divider=dividerId};
            info.Dispose();
          }
          finally{labels.Frame.Dispose();labels.Outline.Dispose();foreach(var text in labels.Text)text.Dispose();}
        }
      }
      private TableLabels BuildLiveTableLabels(int table)
      {
        var info=_liveTables[table];double used=OccupiedLength(_result,table);
        var rows=new[]{info.Material.Name}.Concat(TableLabelRowsWithParts(info.Name,_result.Placements.Count(part=>part.Table==table),
          OccupiedWidth(_result,table),used,YardsPerUnit())).ToArray();
        rows[^1]=YardageText(used+_result.TableAllowance,YardsPerUnit());
        rows=info.LabelIds.Length==3?new[]{info.Name,rows[^2],rows[^1]}:rows.TakeLast(info.LabelIds.Length).ToArray();
        var plane=new Plane(info.Frame.PointAt(info.Material.Width,0),info.Frame.YAxis,-info.Frame.XAxis);
        return BuildTableLabels(string.Empty,rows,plane,info.Material.Width,info.Length);
      }
      private static bool SameTableLabelLayout(TextEntity existing,TextEntity expected)=>
        // Annotation CRCs do not reliably include style overrides such as alignment and height.
        existing.PlainText==expected.PlainText&&existing.RichText==expected.RichText&&
        existing.DimensionStyleId==expected.DimensionStyleId&&existing.Plane.EpsilonEquals(expected.Plane,TableLabelLayoutTolerance)&&
        Math.Abs(existing.TextHeight-expected.TextHeight)<=TableLabelLayoutTolerance&&
        Math.Abs(existing.DimensionScale-expected.DimensionScale)<=TableLabelLayoutTolerance&&
        existing.TextHorizontalAlignment==expected.TextHorizontalAlignment&&existing.TextVerticalAlignment==expected.TextVerticalAlignment&&
        existing.TextOrientation==expected.TextOrientation&&existing.DrawForward==expected.DrawForward;
      private void RefreshLiveLabelFormatting()
      {
        bool changed=false;
        for(int table=0;table<_liveTables.Count&&!changed;table++)
        {
          var info=_liveTables[table];if(info.LabelIds.Length<3)continue;
          var labels=BuildLiveTableLabels(table);
          try
          {
            changed=_doc.Objects.FindId(info.Divider)==null||info.Outline.DataCRC(0)!=labels.Outline.DataCRC(0)||labels.Text.Where((text,index)=>
              _doc.Objects.FindId(info.LabelIds[index])?.Geometry is not TextEntity existing||!SameTableLabelLayout(existing,text)).Any();
          }
          finally{labels.Frame.Dispose();labels.Outline.Dispose();foreach(var text in labels.Text)text.Dispose();}
        }
        if(!changed)return;
        uint undo=_doc.UndoRecordingEnabled?_doc.BeginUndoRecord("vNest table label formatting"):0;
        if(undo==0){Log.Write("vNest","placed label formatting deferred: undo recording unavailable");return;}
        var snapshot=CaptureManualState(_result) with {DocumentRecord=undo};bool success=false;
        _applyingLive=true;
        try{UpdateLiveUsage();success=true;}
        catch(Exception ex){Log.Write("vNest","placed label formatting failed: {0}",ex);}
        finally{_doc.EndUndoRecord(undo);_applyingLive=false;}
        if(success){_manualUndo.Add(snapshot);_manualRedo.Clear();}
        else
        {
          _applyingLive=true;
          try{ExecuteLiveHistory(false);_doc.ClearRedoRecords();RefreshLiveDecorations();}
          finally{_applyingLive=false;}
        }
      }
      private void RefreshLiveDecorations()
      {
        for(int table=0;table<_liveTables.Count;table++)
        {
          var info=_liveTables[table];
          if(_doc.Objects.FindId(info.Border)?.Geometry is not Curve outline)continue;
          var labels=info.LabelIds.Select(id=>_doc.Objects.FindId(id)?.Geometry).OfType<TextEntity>().Select(text=>(TextEntity)text.Duplicate()).ToArray();
          string name=_doc.Objects.FindId(info.Border)!.Attributes.GetUserString(MetadataPrefix+"tableName")??info.Name;
          var divider=_doc.Groups.GroupMembers(info.Group).FirstOrDefault(obj=>obj.Attributes.GetUserString(NestTableMetadata.RoleKey)==NestTableMetadata.LabelDividerRole);
          _liveTables[table]=info with {Name=name,Outline=outline.DuplicateCurve(),Labels=labels,Divider=divider?.Id??Guid.Empty};
          info.Dispose();
        }
      }
      private bool ApplyLiveParts(NestPackingEngine.Result before,NestPackingEngine.Result after)
      {
        var changes=after.Placements.Where(part=>before.Placements.FirstOrDefault(old=>old.Id==part.Id) is {} old&&
          (old.Table!=part.Table||Math.Abs(old.X-part.X)>PoseDimensionTolerance||Math.Abs(old.Y-part.Y)>PoseDimensionTolerance||
           Math.Abs(old.Pose.Rotation-part.Pose.Rotation)>PoseDimensionTolerance||old.Pose.Flipped!=part.Pose.Flipped)).ToArray();
        if(changes.Length==0)return true;
        if(changes.SelectMany(part=>_liveParts[part.Id]).Any(id=>_doc.Objects.FindId(id) is not {IsDeleted:false} obj||obj.IsLocked||obj.IsReference||_doc.Layers[obj.Attributes.LayerIndex].IsLocked))
        {_status.Text="A placed-part object is missing or locked";return false;}
        if(_liveUndoRecord==0)
        {
          if(!_doc.UndoRecordingEnabled){_status.Text="Enable Rhino undo before editing placed parts";return false;}
          _liveUndoRecord=_doc.BeginUndoRecord("vNest placed-part edit");
          if(_liveUndoRecord==0){_status.Text="Could not begin placed-part undo record";return false;}
        }
        _applyingLive=true;bool redraw=_doc.Views.RedrawEnabled;_doc.Views.RedrawEnabled=false;
        try
        {
          foreach(var part in changes)
          {
            var old=before.Placements.Single(value=>value.Id==part.Id);var row=PlacementOwner(part.Id)!;
            if(!InstanceTransform(row,old).TryGetInverse(out var inverse))throw new InvalidOperationException("Placed-part transform is not invertible.");
            var transform=InstanceTransform(row,part)*inverse;
            foreach(var id in _liveParts[part.Id])
            {
              var obj=_doc.Objects.FindId(id)!;using var geometry=TransformedCopy(obj.Geometry,transform);
              if(!_doc.Objects.Replace(id,geometry,false))throw new InvalidOperationException("Could not update a placed-part object.");
              if(old.Table==part.Table&&old.Pose.Rotation==part.Pose.Rotation&&old.Pose.Flipped==part.Pose.Flipped)continue;
              using var attributes=obj.Attributes.Duplicate();
              attributes.SetUserString(MetadataPrefix+"rotation",part.Pose.Rotation.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
              attributes.SetUserString(MetadataPrefix+"flipped",part.Pose.Flipped.ToString());
              attributes.SetUserString(MetadataPrefix+"table",(part.Table+1).ToString());
              if(old.Table!=part.Table){attributes.RemoveFromGroup(_liveTables[old.Table].Group);attributes.AddToGroup(_liveTables[part.Table].Group);}
              if(!_doc.Objects.ModifyAttributes(id,attributes,true))throw new InvalidOperationException("Could not update placed-part attributes.");
            }
          }
          return true;
        }
        catch(Exception ex)
        {EndLiveRecord();ExecuteLiveHistory(false);_doc.ClearRedoRecords();_result=_manualGesture?.Before??before;_status.Text="Placed-part edit restored";Log.Write("vNest","placed-part edit failed: {0}",ex);return false;}
        finally{_doc.Views.RedrawEnabled=redraw;_applyingLive=false;}
      }
      private void LiveUndoRedo(object? sender,UndoRedoEventArgs e)
      {
        if(_closed||_liveTables.Count==0||RhinoDoc.ActiveDoc!=_doc)return;
        if(_applyingLive)
        {if(e.IsEndUndo||e.IsEndRedo)_liveHistoryInverseRecord=_doc.CurrentUndoRecordSerialNumber;return;}
        if(e.IsBeginUndo||e.IsBeginRedo){_replayingLiveHistory=true;return;}
        if(!(e.IsEndUndo||e.IsEndRedo))return;
        _replayingLiveHistory=false;
        var from=e.IsEndRedo?_manualRedo:_manualUndo;var to=e.IsEndRedo?_manualUndo:_manualRedo;
        if(from.Count==0||from[^1].DocumentRecord!=e.UndoSerialNumber){_manualUndo.Clear();_manualRedo.Clear();_packedRevision=-1;_status.Text="Drawing history changed; reopen Edit Tables to refresh";return;}
        var state=from[^1];from.RemoveAt(from.Count-1);to.Add(CaptureManualState(_result) with {DocumentRecord=_doc.CurrentUndoRecordSerialNumber});
        RestoreManualState(state,false);
      }
      private Vector3d LocationLabelOffset()
      { var label=LabelGeometry(0); return -_plane.XAxis*(label.FrameWidth+label.Clearance)+_plane.YAxis*TableMaterial(0).Width; }
      private Point3d AnchorFromLabelLocation(Point3d point)=>point-LocationLabelOffset();

      private void ClearPreviewCopies()
      {
        _activePreviewParts=[];_liveObjectSerials.Clear();_tableObjectSerials.Clear();_gestureObjectSerials.Clear();_gestureObjectIds.Clear();
        ClearHighlights();foreach(var entry in _previewCopies.Values)foreach(var item in entry.Items)item.Geometry.Dispose();_previewCopies.Clear();
        foreach(var entry in _previewOutlines.Values)entry.Outline.Dispose();_previewOutlines.Clear();_previewClearance.Clear();
      }
      private void RetainPreviewCopies(IEnumerable<Row> rows)
      {
        var retained=rows.SelectMany(row=>row.InstanceIds()).ToHashSet();
        foreach(int id in _manualInstances.Keys.Where(id=>!retained.Contains(id)).ToArray())_manualInstances.Remove(id);
        if(_activePreviewInstance is {} active&&!retained.Contains(active))_activePreviewInstance=null;
        foreach(var id in _previewCopies.Keys.Where(id=>!retained.Contains(id)).ToArray())
        {foreach(var item in _previewCopies[id].Items)item.Geometry.Dispose();_previewCopies.Remove(id);}
        foreach(var id in _previewOutlines.Keys.Where(id=>!retained.Contains(id)).ToArray())
        {_previewOutlines[id].Outline.Dispose();_previewOutlines.Remove(id);_previewClearance.Remove(id);}
      }

      private Curve PreviewOutline(Row row,NestPackingEngine.Placement placement)
      {
        var transform=InstanceTransform(row,placement);
        if(_previewOutlines.TryGetValue(placement.Id,out var cached))
        {
          if(cached.Transform==transform)return cached.Outline;
          if(cached.Transform.TryGetInverse(out var inverse)&&cached.Outline.Transform(transform*inverse))
          {_previewOutlines[placement.Id]=(transform,cached.Outline);return cached.Outline;}
          cached.Outline.Dispose();
        }
        var outline=row.Part.Outline.DuplicateCurve();outline.Transform(transform);
        _previewOutlines[placement.Id]=(transform,outline);return outline;
      }
      private IReadOnlyList<Polyline> PreviewClearance(Row row,NestPackingEngine.Placement placement)
      {
        double gap=RowMaterial(row).Gap;if(gap<=0)return [];
        var transform=InstanceTransform(row,placement);
        if(_previewClearance.TryGetValue(placement.Id,out var cached)&&cached.Gap==gap)
        {
          if(cached.Transform==transform)return cached.Contours;
          if(cached.Transform.TryGetInverse(out var inverse))
          {foreach(var path in cached.Contours)path.Transform(transform*inverse);_previewClearance[placement.Id]=(transform,gap,cached.Contours);return cached.Contours;}
        }
        var contours=row.Clearance(gap).Select(path=>{var copy=new Polyline(path);copy.Transform(transform);return copy;}).ToArray();
        _previewClearance[placement.Id]=(transform,gap,contours);return contours;
      }

      private IReadOnlyList<PlanarPartSelection.Item> PreviewCopies(Row row)
      {
        return CopiesAt(row,row.Id,VisiblePlacement(row));
      }
      private IReadOnlyList<PlanarPartSelection.Item> InstancePreviewCopies(Row row,NestPackingEngine.Placement placement)=>
        CopiesAt(row,placement.Id,placement.Id==row.Id?VisiblePlacement(row):InstanceTransform(row,placement));
      private Transform InstanceTransform(Row row,NestPackingEngine.Placement placement)
      {
        var frame=PlanarPartSplitter.RotatedFrame(row.Part.Plane,RhinoMath.ToRadians(MaterialBasisAngle-placement.Pose.Rotation));
        var bounds=Bounds(row,placement.Pose.Rotation); var target=DisplayTablePlane(placement.Table);
        var transform=Transform.Translation(target.XAxis*(placement.X-bounds.Min.X)+target.YAxis*(placement.Y-bounds.Min.Y))*Transform.PlaneToPlane(frame,target);
        return placement.Pose.Flipped?Transform.Mirror(target.PointAt(placement.X+(bounds.Max.X-bounds.Min.X)/2,0),target.XAxis)*transform:transform;
      }
      private IReadOnlyList<PlanarPartSelection.Item> CopiesAt(Row row,int id,Transform transform)
      {
        if(_previewCopies.TryGetValue(id,out var existing)&&existing.Transform==transform&&Nullable.Equals(existing.DrawnGuide,row.DrawnGuide)) return existing.Items;
        if(_manualGesture!=null&&existing.Items!=null&&Nullable.Equals(existing.DrawnGuide,row.DrawnGuide)&&existing.Transform.TryGetInverse(out var inverse))
        {
          var delta=transform*inverse;
          if(delta.Determinant>0&&existing.Items.All(item=>item.Geometry.Transform(delta)))
          {_previewCopies[id]=(transform,existing.Items,row.DrawnGuide);return existing.Items;}
        }
        if(existing.Items!=null) foreach(var item in existing.Items) item.Geometry.Dispose();
        var copies=new List<PlanarPartSelection.Item>();
        foreach(var item in PartItemsForCopies(row))
        { var copy=TransformedCopy(item.Geometry,transform); copies.Add(item with {Geometry=copy}); }
        _previewCopies[id]=(transform,copies,row.DrawnGuide); return copies;
      }
      private IEnumerable<PlanarPartSelection.Item> PartItemsForCopies(Row row)=>row.Part.Items.Where(item=>
        !row.DrawnGuide.HasValue||item.Geometry is not Curve curve||curve.IsClosed||!IsGrainMarker(_doc,item.Attributes));
      private Transform VisiblePlacement(Row row)
      {
        var placement=VisibleResult.Placements.FirstOrDefault(part=>part.Id==row.Id);
        return InstanceTransform(row,placement??new NestPackingEngine.Placement(row.Id,row.Table,row.X,row.Y,new NestPackingEngine.Pose(0,0,row.Rotation) {Flipped=row.Flipped}));
      }

      private bool CanFineTune=>!_closed&&!_packing&&!_placing&&!_locationPicking&&!PackingBlocked()&&
        _packedRevision==_layoutRevision&&_result.Placements.Count>0;
      private Row? PlacementOwner(int id)=>_rows.FirstOrDefault(row=>row.OwnsInstance(id));
      private NestPackingEngine.Placement[] ManualSelection()
      {var rows=SelectedParts;return _result.Placements.Where(part=>rows.Any(row=>row.OwnsInstance(part.Id))&&
        (_manualSelectedInstances.Count>0?_manualSelectedInstances.Contains(part.Id):_activePreviewInstance==null||rows.Length>1||part.Id==_activePreviewInstance)).ToArray();}
      private void SelectManualPart(NestPackingEngine.Placement hit,bool toggle)
      {
        var ids=ManualSelection().Select(part=>part.Id).ToHashSet();
        if(toggle){if(!ids.Add(hit.Id))ids.Remove(hit.Id);}
        else if(!ids.Contains(hit.Id))ids=[hit.Id];
        SelectManualInstances(ids);
      }
      private void SelectManualInstances(HashSet<int> ids)
      {
        _manualSelectedInstances.Clear();_manualSelectedInstances.UnionWith(ids);
        var owners=ids.Select(PlacementOwner).OfType<Row>().ToHashSet();bool previous=_updating;_updating=true;
        try
        {
          _grid.UnselectAll();
          for(int index=0;index<_visibleRows.Count;index++)if(owners.Contains(_visibleRows[index]))_grid.SelectRow(index);
        }
        finally{_updating=previous;}
        _activePreviewInstance=ids.Count==1?ids.Single():null;UpdateEditor();RefreshHighlights();_doc.Views.Redraw();
      }
      private bool SelectManualBox(RhinoView view,System.Drawing.Point start,System.Drawing.Point end,bool add,bool remove)
      {
        if(!CanFineTune)return false;
        var rectangle=NestDocumentPicking.SelectionRectangle(start,end);bool crossing=end.X<start.X;
        view.ActiveViewport.GetFrustumLine(rectangle.Left+rectangle.Width/2,rectangle.Top+rectangle.Height/2,out var ray);
        using var context=new PickContext {View=view,PickLine=ray,PickStyle=crossing?PickStyle.CrossingPick:PickStyle.WindowPick,PickMode=PickMode.Wireframe,PickGroupsEnabled=false};
        context.SetPickTransform(view.ActiveViewport.GetPickTransform(rectangle));context.UpdateClippingPlanes();
        var hits=new HashSet<int>();
        foreach(var part in _result.Placements)
        {
          var row=PlacementOwner(part.Id);if(row==null)continue;
          var outline=PreviewOutline(row,part);
          if(!context.PickFrustumTest(outline.GetBoundingBox(true),out bool contained))continue;
          if(contained){hits.Add(part.Id);continue;}
          if(crossing){using var nurbs=outline.ToNurbsCurve();if(context.PickFrustumTest(nurbs,out _,out _,out _))hits.Add(part.Id);}
        }
        if(hits.Count==0)return false;
        var selected=add||remove?ManualSelection().Select(part=>part.Id).ToHashSet():[];
        if(remove)selected.ExceptWith(hits);else selected.UnionWith(hits);
        SelectManualInstances(selected);return true;
      }
      private bool CompatibleManualFrames(IReadOnlyList<NestPackingEngine.Placement> parts)
      {
        var frame=DisplayTablePlane(parts[0].Table);
        return parts.All(part=>DisplayTablePlane(part.Table).XAxis.EpsilonEquals(frame.XAxis,PoseDimensionTolerance)&&
          DisplayTablePlane(part.Table).YAxis.EpsilonEquals(frame.YAxis,PoseDimensionTolerance));
      }
      private Point3d ManualPivot(IReadOnlyList<NestPackingEngine.Placement> parts)
      {
        var frame=DisplayTablePlane(parts[0].Table);var bounds=BoundingBox.Empty;
        foreach(var part in parts)
        {
          var plane=DisplayTablePlane(part.Table);
          foreach(double x in new[]{part.X,part.X+part.Pose.Width})foreach(double y in new[]{part.Y,part.Y+part.Pose.Length})
          {frame.ClosestParameter(plane.PointAt(x,y),out double u,out double v);bounds.Union(new Point3d(u,v,0));}
        }
        return frame.PointAt(bounds.Center.X,bounds.Center.Y);
      }
      private NestContourPackingEngine.ManualLayout ManualScene(NestPackingEngine.Result result)=>new(result.Placements,
        Enumerable.Range(0,result.Tables).ToDictionary(table=>table,table=>TableMaterial(table).Width),Length,
        result.Placements.ToDictionary(part=>part.Id,part=>RowMaterial(PlacementOwner(part.Id)!).Gap),
        Enumerable.Range(0,result.Tables).ToDictionary(table=>table,TableLength),_doc.ModelAbsoluteTolerance);
      private NestPackingEngine.Pose? ManualPose(Row row,double angle,bool flipped)
      {
        var before=(row.Rotation,row.Fixed,row.ManualRotation,row.Flipped);
        try
        {
          row.Rotation=NormalizeRotation(angle);row.Fixed=true;row.ManualRotation=true;row.Flipped=flipped;
          return MaterialPoses(row,RowMaterial(row)).FirstOrDefault(pose=>pose.Flipped==flipped);
        }
        finally {(row.Rotation,row.Fixed,row.ManualRotation,row.Flipped)=before;}
      }
      private NestPackingEngine.Placement? TurnPlacement(NestPackingEngine.Placement before,double angle)
      {
        var row=PlacementOwner(before.Id);if(row==null||ManualPose(row,angle,before.Pose.Flipped) is not {} pose)return null;
        var frame=DisplayTablePlane(before.Table);var center=frame.PointAt(before.X+before.Pose.Width/2,before.Y+before.Pose.Length/2);
        if(!InstanceTransform(row,before).TryGetInverse(out var inverse))return null;
        var source=inverse*center;var candidate=before with {X=0,Y=0,Pose=pose};
        frame.ClosestParameter(InstanceTransform(row,candidate)*source,out double x,out double y);
        return candidate with {X=before.X+before.Pose.Width/2-x,Y=before.Y+before.Pose.Length/2-y};
      }
      private NestPackingEngine.Placement[]? TurnManualGroup(IReadOnlyList<NestPackingEngine.Placement> parts,double delta,Point3d pivot)
      {
        var axis=DisplayTablePlane(parts[0].Table).Normal;var rotation=Transform.Rotation(RhinoMath.ToRadians(delta),axis,pivot);
        var result=new List<NestPackingEngine.Placement>();
        foreach(var part in parts)
        {
          var row=PlacementOwner(part.Id);
          // Mirroring reverses the stored angle's handedness, but the group still turns rigidly in world space.
          double angle=part.Pose.Rotation+(part.Pose.Flipped?-delta:delta);
          if(row==null||ManualPose(row,angle,part.Pose.Flipped) is not {} pose||!InstanceTransform(row,part).TryGetInverse(out var inverse))return null;
          var frame=DisplayTablePlane(part.Table);var center=frame.PointAt(part.X+part.Pose.Width/2,part.Y+part.Pose.Length/2);
          var candidate=part with {X=0,Y=0,Pose=pose};var shift=rotation*center-InstanceTransform(row,candidate)*(inverse*center);
          result.Add(candidate with {X=shift*frame.XAxis,Y=shift*frame.YAxis});
        }
        return result.ToArray();
      }
      private void ShowManual(IReadOnlyList<NestPackingEngine.Placement> changes)
      {
        var byId=changes.ToDictionary(part=>part.Id);
        if(changes.Count>0&&changes.All(part=>_result.Placements.FirstOrDefault(old=>old.Id==part.Id) is {} old&&
          old.Table==part.Table&&old.X==part.X&&old.Y==part.Y&&old.Pose==part.Pose))return;
        var next=_result with {Placements=_result.Placements.Select(part=>byId.GetValueOrDefault(part.Id)??part).ToList()};
        if(_liveTables.Count>0&&_manualGesture==null&&changes.Count>0&&!ApplyLiveParts(_result,next))return;
        _result=next;
        if(_manualGesture!=null)
        {
          RefreshMovingHighlights();
          // View-only redraws can reuse the old scene; moving geometry must invalidate it.
          _doc.Views.Redraw();return;
        }
        _place.Enabled=_liveTables.Count==0;UpdateEditor();RefreshHighlights();RefreshTableRows();
        _totalYardage.Text=TotalYardageText(_result,YardsPerUnit());
        RefreshMaterialUsage(MaterialUsageTotals(_result,CurrentMaterialValues().Name));_doc.Views.Redraw();
      }
      private void CommitManual(NestPackingEngine.Result before)
      {
        var snapshot=CaptureManualState(before) with {DocumentRecord=_liveUndoRecord};bool changed=false;
        foreach(var part in _result.Placements)
        {
          var old=before.Placements.FirstOrDefault(value=>value.Id==part.Id);
          if(old==null||old.Table==part.Table&&Math.Abs(old.X-part.X)<=PoseDimensionTolerance&&Math.Abs(old.Y-part.Y)<=PoseDimensionTolerance&&
            Math.Abs(NormalizeRotation(old.Pose.Rotation-part.Pose.Rotation))<=PoseDimensionTolerance&&old.Pose.Flipped==part.Pose.Flipped)continue;
          changed=true;_manualInstances[part.Id]=part;
          if(PlacementOwner(part.Id) is {} row&&part.Id==row.Id)
          {row.Fixed=true;row.ManualRotation=true;row.Table=part.Table;row.X=part.X;row.Y=part.Y;row.Rotation=part.Pose.Rotation;row.Flipped=part.Pose.Flipped;SavePartSettings(row);}
        }
        if(changed)
        {_manualUndo.Add(snapshot);if(_manualUndo.Count>ManualHistoryLimit)_manualUndo.RemoveAt(0);_manualRedo.Clear();}
        if(_liveUndoRecord!=0&&!changed)
        {EndLiveRecord();_applyingLive=true;try{ExecuteLiveHistory(false);_doc.ClearRedoRecords();}finally{_applyingLive=false;}}
        else{if(changed&&_liveUndoRecord!=0)UpdateLiveUsage();EndLiveRecord();}
        _packedRevision=_layoutRevision;_needsNest=false;
        _status.Text=$"{_result.Tables} tables; {_result.Placements.Count} parts; manually adjusted";
        RefreshGrid();RefreshHighlights();_doc.Views.Redraw();
      }
      private ManualState CaptureManualState(NestPackingEngine.Result layout)=>new(layout,
        _rows.ToDictionary(row=>row.Id,row=>new ManualPartState(row.Fixed,row.ManualRotation,row.Flipped,row.Table,row.X,row.Y,row.Rotation)),
        new(_manualInstances),_activePreviewInstance,Selection:ManualSelection().Select(part=>part.Id).ToArray());
      private void ManualHistory(bool redo)
      {
        if(!CanFineTune)return;EndManualGesture(true);
        var from=redo?_manualRedo:_manualUndo;var to=redo?_manualUndo:_manualRedo;
        if(from.Count==0){_status.Text=redo?"Nothing to redo":"Nothing to undo";return;}
        var state=from[^1];from.RemoveAt(from.Count-1);to.Add(CaptureManualState(_result));
        if(_liveTables.Count>0)
        {
          _liveHistoryInverseRecord=0;_applyingLive=true;
          try{if(!ExecuteLiveHistory(redo)){from.Add(state);to.RemoveAt(to.Count-1);return;}}
          finally{_applyingLive=false;}
          to[^1]=to[^1] with {DocumentRecord=_liveHistoryInverseRecord};
        }
        RestoreManualState(state,_liveTables.Count==0);
      }
      private void RestoreManualState(ManualState state,bool persist)
      {
        if(_liveTables.Count>0)RefreshLiveDecorations();
        _result=_liveTables.Count>0?state.Layout with {TableMaterials=_result.TableMaterials}:state.Layout;_manualInstances.Clear();foreach(var pair in state.Instances)_manualInstances[pair.Key]=pair.Value;
        foreach(var row in _rows)
        {
          if(!state.Parts.TryGetValue(row.Id,out var part))continue;
          (row.Fixed,row.ManualRotation,row.Flipped,row.Table,row.X,row.Y,row.Rotation)=
            (part.Fixed,part.ManualRotation,part.Flipped,part.Table,part.X,part.Y,part.Rotation);
          if(persist)SavePartSettings(row);
        }
        _activePreviewInstance=state.ActiveInstance;_manualSelectedInstances.Clear();_manualSelectedInstances.UnionWith(state.Selection??[]);_packedRevision=_layoutRevision;_needsNest=false;
        ShowManual([]);RefreshGrid();if(state.Selection is {Length:>0})SelectManualInstances(state.Selection.Where(id=>_result.Placements.Any(part=>part.Id==id)).ToHashSet());_status.Text="Manual edit history restored";
      }
      private bool ManualNumber(NumericStepper field,double value)
      {
        if(!CanFineTune)return false;
        EndManualGesture(true);var selected=ManualSelection();if(selected.Length==0)return false;
        var before=_result;var changes=new List<NestPackingEngine.Placement>();
        if(field==_angle)
        {
          if(!CompatibleManualFrames(selected)){_status.Text="Selected tables have incompatible orientations";UpdateEditor();return true;}
          var turned=TurnManualGroup(selected,(value-selected[0].Pose.Rotation)*(selected[0].Pose.Flipped?-1:1),ManualPivot(selected));
          if(turned==null){_status.Text="Angle conflicts with grain rules";UpdateEditor();return true;}changes.AddRange(turned);
        }
        else if(field==_x||field==_y||field==_table)
        {
          if(selected.Length!=1){_status.Text="Pick one preview copy to edit its location";UpdateEditor();return true;}
          if(field==_table)
          {
            int table=(int)value-1;
            if(table<0||table>=_result.Tables||!TableMaterial(table).Name.Equals(RowMaterial(PlacementOwner(selected[0].Id)!).Name,StringComparison.OrdinalIgnoreCase))
            {_status.Text="Choose an existing table of the same material";UpdateEditor();return true;}
            changes.Add(selected[0] with {Table=table});
          }
          else changes.Add(field==_x?selected[0] with {X=value}:selected[0] with {Y=value});
        }
        else return false;
        // Explicit field entries and resets override obstacles; mouse gestures retain collision checks.
        ShowManual(changes);CommitManual(before);return true;
      }
      private bool AngleResetChanged()=>SelectedParts.Any(row=>row.ManualRotation||FieldResetOverlay.NumberChanged(NormalizeRotation(
        VisibleResult.Placements.FirstOrDefault(part=>part.Id==(_activePreviewInstance.HasValue&&SelectedParts.Length==1?_activePreviewInstance.Value:row.Id))?.Pose.Rotation??row.Rotation),
        AutomaticAngle(row),_angle.DecimalPlaces));
      private double AutomaticAngle(Row row)
      {
        var before=(row.Fixed,row.ManualRotation);
        try
        {
          row.Fixed=DefaultFixed;row.ManualRotation=DefaultManualRotation;
          return MaterialPoses(row,RowMaterial(row)).OrderBy(pose=>Math.Abs(NormalizeRotation(pose.Rotation))).FirstOrDefault()?.Rotation??DefaultRotation;
        }
        finally{(row.Fixed,row.ManualRotation)=before;}
      }
      private void ResetAngle()
      {
        if(_updating||_closed)return;
        EndManualGesture(true);
        if(CanFineTune&&ManualSelection() is {Length:>0} selected)
        {
          var before=_result;var changes=new List<NestPackingEngine.Placement>();
          foreach(var part in selected)
            if(PlacementOwner(part.Id) is {} row&&TurnPlacement(part,AutomaticAngle(row)) is {} reset)changes.Add(reset);
          ShowManual(changes);CommitManual(before);
          foreach(var row in selected.Select(part=>PlacementOwner(part.Id)).OfType<Row>().Distinct())
          {row.ManualRotation=DefaultManualRotation;SavePartSettings(row);}
          UpdateEditor();RefreshGrid();return;
        }
        Edit(row=>{row.Rotation=DefaultRotation;row.ManualRotation=DefaultManualRotation;});
      }
      private NestPackingEngine.Placement? PreviewHit(Line ray)
      {
        foreach(var part in _result.Placements.AsEnumerable().Reverse())
        {
          var row=PlacementOwner(part.Id);if(row==null||!InstanceTransform(row,part).TryGetInverse(out var inverse))continue;
          var source=ray;source.Transform(inverse);
          if(NestDocumentPicking.Inside(row.Part.Outline,row.Part.Plane,source,_doc.ModelAbsoluteTolerance,out _))return part;
        }
        return null;
      }
      private bool CursorPoint(MouseCallbackEventArgs e,out Point3d point)
      {
        point=Point3d.Unset;
        var plane=_manualGesture?.Plane??(_liveTables.Count>0&&ManualSelection().FirstOrDefault() is {} selected?DisplayTablePlane(selected.Table):_plane);
        if(e.View?.ActiveViewport.GetFrustumLine(e.ViewportPoint.X,e.ViewportPoint.Y,out var ray)!=true||
          !Rhino.Geometry.Intersect.Intersection.LinePlane(ray,plane,out double t))return false;
        point=ray.PointAt(t);return point.IsValid;
      }
      private static bool AltPressed=>(System.Windows.Forms.Control.ModifierKeys&System.Windows.Forms.Keys.Alt)!=0;
      private static bool ControlPressed=>(System.Windows.Forms.Control.ModifierKeys&System.Windows.Forms.Keys.Control)!=0;
      private static bool ShiftPressed=>(System.Windows.Forms.Control.ModifierKeys&System.Windows.Forms.Keys.Shift)!=0;
      private static double ContactAlignmentDistance(MouseCallbackEventArgs e,Plane plane)
      {
        if(e.View?.ActiveViewport is not {} view)return NestContourPackingEngine.DefaultManualContactAlignment;
        Point3d? At(int dx,int dy)=>view.GetFrustumLine(e.ViewportPoint.X+dx,e.ViewportPoint.Y+dy,out var ray)&&
          Rhino.Geometry.Intersect.Intersection.LinePlane(ray,plane,out double parameter)?ray.PointAt(parameter):null;
        if(At(0,0) is not {} origin)return NestContourPackingEngine.DefaultManualContactAlignment;
        double distance=Math.Max(At(1,0) is {} x?origin.DistanceTo(x):0,At(0,1) is {} y?origin.DistanceTo(y):0)*ManualContactSnapPixels;
        return double.IsFinite(distance)?distance:NestContourPackingEngine.DefaultManualContactAlignment;
      }
      private void BeginManualGesture(MouseCallbackEventArgs e,bool rotate,bool dragging,Point3d start)
      {
        var selected=ManualSelection();if(selected.Length==0)return;
        if(!CompatibleManualFrames(selected)){_status.Text="Selected tables have incompatible orientations";return;}
        _doc.Views.ActiveView=e.View;PreviewKeyboard.FocusViewport(e.View!);
        var first=selected[0];var frame=DisplayTablePlane(first.Table);
        _manualGesture=new(_result,selected.Select(part=>part.Id).ToHashSet(),frame,start,
          ManualPivot(selected),e.View!.ActiveViewport.Id,e.ViewportPoint.X,rotate,dragging,ManualScene(_result))
          {ContactAlignment=ContactAlignmentDistance(e,frame)};
        foreach(var id in selected.Where(part=>_liveParts.ContainsKey(part.Id)).SelectMany(part=>_liveParts[part.Id]))
          if(_doc.Objects.FindId(id) is {} obj){_gestureObjectIds.Add(id);_gestureObjectSerials.Add(obj.RuntimeSerialNumber);}
        RefreshHighlights();
        _manualPreviewTick=0;
      }
      private bool PreviewMouseDown(MouseCallbackEventArgs e)
      {
        if((_manualGesture!=null||_previewBox!=null)&&e.MouseButton==MouseButton.Right){EndManualGesture(false);return true;}
        if(_liveTables.Count==0&&e.MouseButton==MouseButton.Left&&!_packing&&!PackingBlocked()&&PlacedPartAt(e) is {} placed)
          EnterPlacedTables(placed);
        if(!CanFineTune||e.MouseButton!=MouseButton.Left||e.View?.ActiveViewport is not {} view)return false;
        if(!view.GetFrustumLine(e.ViewportPoint.X,e.ViewportPoint.Y,out var ray))return false;
        EndManualGesture(true);
        var hit=PreviewHit(ray);
        bool inTable=Enumerable.Range(0,_result.Tables).Any(table=>
          Rhino.Geometry.Intersect.Intersection.LinePlane(ray,DisplayTablePlane(table),out double t)&&
          DisplayTablePlane(table).ClosestParameter(ray.PointAt(t),out double x,out double y)&&x>=0&&x<=TableMaterial(table).Width&&y>=0&&y<=TableLength(table));
        if(!AltPressed&&(e.ShiftKeyDown||hit==null&&(inTable||_liveTables.Count>0)))
        {_previewBox=new(e.ViewportPoint,view.Id,hit?.Id,e.ShiftKeyDown,e.CtrlKeyDown);return true;}
        if(hit==null||!CursorPoint(e,out var point))return false;
        SelectManualPart(hit,false);
        BeginManualGesture(e,AltPressed,true,point);return true;
      }
      private bool PreviewMouseMove(MouseCallbackEventArgs e)
      {
        if(!CanFineTune)return false;
        if(_previewBox is {} box)
        {if(e.View?.ActiveViewport.Id==box.Viewport)_picking.ShowPreviewBox(box.Viewport,box.Start,e.ViewportPoint);return true;}
        if(_manualGesture is not {} gesture)return false;
        if(gesture.Rotation&&!AltPressed)return true;
        if(!gesture.Dragging&&!AltPressed){EndManualGesture(true);return true;}
        if(e.View?.ActiveViewport.Id!=gesture.Viewport||!CursorPoint(e,out var point))return true;
        if(System.Environment.TickCount64-_manualPreviewTick<ManualPreviewInterval)return true;
        gesture.ContactAlignment=ContactAlignmentDistance(e,gesture.Plane);
        _manualPreviewTick=System.Environment.TickCount64;MoveManualGesture(gesture,point,e.ViewportPoint.X);return true;
      }
      private void MoveManualGesture(ManualGesture gesture,Point3d point,int pixelX)
      {
        var selected=gesture.Before.Placements.Where(part=>gesture.Ids.Contains(part.Id)).ToArray();
        if(gesture.Rotation)
        {
          var a=gesture.Start-gesture.Pivot;var b=point-gesture.Pivot;
          if(b.SquareLength<=RhinoMath.ZeroTolerance)return;
          double delta=a.SquareLength<=RhinoMath.ZeroTolerance?(pixelX-gesture.StartPixelX)*ManualRotationPixelStep:
            RhinoMath.ToDegrees(Math.Atan2(b*gesture.Plane.YAxis,b*gesture.Plane.XAxis)-Math.Atan2(a*gesture.Plane.YAxis,a*gesture.Plane.XAxis));
          var desired=TurnManualGroup(selected,delta,gesture.Pivot);
          if(desired==null){_status.Text="Rotation blocked by grain rules";return;}
          if(!ControlPressed)
          {
            if(ShiftPressed)
            {var relaxed=gesture.Scene.RelaxRigid(desired);if(relaxed==null){_status.Text="No collision-free rotation fits the table";return;}desired=relaxed;}
            else
            {
              var current=_result.Placements.Where(part=>gesture.Ids.Contains(part.Id)).ToArray();
              double accepted=NormalizeRotation(current[0].Pose.Rotation-selected.First(part=>part.Id==current[0].Id).Pose.Rotation)*(current[0].Pose.Flipped?-1:1);
              double travel=NormalizeRotation(delta-accepted),valid=accepted;
              int steps=Math.Max(1,(int)Math.Ceiling(Math.Abs(travel)/ManualRotationCheckStep));
              var last=current;
              for(int step=1;step<=steps;step++)
              {
                double angle=accepted+travel*step/steps;
                var candidate=TurnManualGroup(selected,angle,gesture.Pivot);
                if(candidate!=null&&gesture.Scene.ValidRigid(candidate))
                {valid=angle;last=candidate;continue;}
                double blocked=angle;
                for(int attempt=0;attempt<ManualRotationContactRefinements;attempt++)
                {
                  double middle=(valid+blocked)/2;
                  var refined=TurnManualGroup(selected,middle,gesture.Pivot);
                  if(refined!=null&&gesture.Scene.ValidRigid(refined))
                  {valid=middle;last=refined;}else blocked=middle;
                }
                ShowManual(last);_status.Text="Rotation blocked by table edge or part gap";return;
              }
              desired=last;
            }
          }
          ShowManual(desired);
        }
        else
        {
          var delta=point-gesture.Start;
          var current=_result.Placements.Where(part=>gesture.Ids.Contains(part.Id)).ToArray();
          var first=current.First(part=>part.Id==selected[0].Id);
          double dx=selected[0].X+delta*gesture.Plane.XAxis-first.X,dy=selected[0].Y+delta*gesture.Plane.YAxis-first.Y;
          ShowManual(ControlPressed?current.Select(part=>part with {X=part.X+dx,Y=part.Y+dy}).ToArray():gesture.Scene.MoveFromAligned(current,dx,dy,gesture.ContactAlignment));
        }
      }
      private bool PreviewMouseUp(MouseCallbackEventArgs e)
      {
        if(_previewBox is {} box&&e.MouseButton==MouseButton.Left)
        {
          _previewBox=null;_picking.HidePreviewBox();
          if(e.View?.ActiveViewport.Id==box.Viewport)
          {
            if(NestDocumentPicking.IsDrag(box.Start,e.ViewportPoint))SelectManualBox(e.View,box.Start,e.ViewportPoint,box.Add,box.Remove);
            else if(box.Hit is {} id&&_result.Placements.FirstOrDefault(part=>part.Id==id) is {} hit)SelectManualPart(hit,true);
          }
          return true;
        }
        if(_manualGesture is not {Dragging:true} gesture||e.MouseButton!=MouseButton.Left)return false;
        if((!gesture.Rotation||AltPressed)&&CursorPoint(e,out var point))
        {gesture.ContactAlignment=ContactAlignmentDistance(e,gesture.Plane);MoveManualGesture(gesture,point,e.ViewportPoint.X);}
        EndManualGesture(true);return true;
      }
      private void EndManualGesture(bool commit)
      {
        if(_previewBox!=null){_previewBox=null;_picking.HidePreviewBox();}
        if(_manualGesture is not {} gesture)return;_manualGesture=null;_gestureObjectIds.Clear();_gestureObjectSerials.Clear();
        if(commit)
        {
          if(_liveTables.Count>0&&!ApplyLiveParts(gesture.Before,_result)){RefreshGrid();RefreshHighlights();_doc.Views.Redraw();return;}
          CommitManual(gesture.Before);
        }
        else
        {
          if(_liveUndoRecord!=0)
          {EndLiveRecord();_applyingLive=true;try{ExecuteLiveHistory(false);_doc.ClearRedoRecords();}finally{_applyingLive=false;}}
          _result=gesture.Before;ShowManual([]);RefreshGrid();
        }
      }
      private void PreviewKey(int key)
        =>PreviewKeyWithModifiers(key,AltPressed,ShiftPressed,ControlPressed);
      private void PreviewKeyWithModifiers(int key,bool alt,bool shift,bool control)
      {
        if(key==PreviewAltKey){_pivotVisible=false;_doc.Views.Redraw();return;}
        if(key==PreviewEscapeKey){EndManualGesture(false);return;}
        if(!CanFineTune||_doc.Views.ActiveView?.ActiveViewport is not {} view)return;
        EndManualGesture(true);var selected=ManualSelection();if(selected.Length==0)return;
        if(!CompatibleManualFrames(selected)){_status.Text="Selected tables have incompatible orientations";return;}
        if(alt)
        {
          var beforeRotation=_result;
          double delta=key is PreviewLeftKey or PreviewUpKey?ManualKeyboardRotationStep:-ManualKeyboardRotationStep;
          var scene=control?null:ManualScene(beforeRotation);
          NestPackingEngine.Placement[] desired=[];
          int steps=control?1:Math.Max(1,(int)Math.Ceiling(Math.Abs(delta)/ManualRotationCheckStep));
          for(int step=1;step<=steps;step++)
          {
            var candidate=TurnManualGroup(selected,delta*step/steps,ManualPivot(selected));
            if(candidate==null){_status.Text="Rotation blocked by grain rules";return;}
            desired=candidate;
            if(scene!=null)
            {
              if(shift)
              {var relaxed=scene.RelaxRigid(desired);if(relaxed==null){_status.Text="No collision-free rotation fits the table";return;}desired=relaxed;}
              else if(!scene.ValidRigid(desired)){_status.Text="Rotation blocked by table edge or part gap";return;}
            }
          }
          ShowManual(desired);CommitManual(beforeRotation);return;
        }
        var direction=key==PreviewLeftKey?-view.CameraX:key==PreviewRightKey?view.CameraX:key==PreviewUpKey?view.CameraY:-view.CameraY;
        var frame=DisplayTablePlane(selected[0].Table);double dx=direction*frame.XAxis,dy=direction*frame.YAxis;
        double size=Math.Sqrt(dx*dx+dy*dy);if(size<=RhinoMath.ZeroTolerance)return;
        var before=_result;double travel=selected.Max(part=>TableLength(part.Table)+TableMaterial(part.Table).Width);
        ShowManual(control?selected.Select(part=>part with {X=part.X+dx/size*ManualFreeArrowStep,Y=part.Y+dy/size*ManualFreeArrowStep}).ToArray():
          ManualScene(before).MoveToContact(selected.Select(part=>part.Id).ToHashSet(),dx/size*travel,dy/size*travel));CommitManual(before);
      }

      private sealed class PreviewKeyboard : IDisposable
      {
        private readonly RhinoDoc _doc;
        private readonly Func<bool> _canEdit;
        private readonly Action<int,bool,bool,bool> _action;
        private readonly Action<bool> _history;
        private readonly Func<IntPtr> _window;
        private readonly Func<bool> _canMove;
        private readonly Action<bool> _alt;
        private readonly Func<int,bool> _isKeyDown;
        private readonly Hook _callback;
        private IntPtr _hook;
        private int _pendingMoves;
        private bool _disposed;
        private delegate IntPtr Hook(int code,IntPtr remove,IntPtr message);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Message {internal IntPtr Window;internal uint Kind;internal UIntPtr Key;internal IntPtr Parameter;internal uint Time;internal System.Drawing.Point Point;}
        internal PreviewKeyboard(RhinoDoc doc,Func<bool> canEdit,Action<int,bool,bool,bool> action,Action<bool> history,Func<IntPtr> window,Func<bool> canMove,Action<bool> alt,Func<int,bool>? isKeyDown=null)
        {
          _doc=doc;_canEdit=canEdit;_action=action;_history=history;_window=window;_canMove=canMove;_alt=alt;_isKeyDown=isKeyDown??(key=>(GetAsyncKeyState(key)&0x8000)!=0);_callback=ReadMessage;
          uint thread=GetWindowThreadProcessId(RhinoApp.MainWindowHandle(),out _);
          if(thread!=0)_hook=SetWindowsHookEx(PreviewKeyboardHook,_callback,IntPtr.Zero,thread);
          Log.Write("vNest","viewport preview key routing installed={0}",_hook!=IntPtr.Zero);
        }
        private IntPtr ReadMessage(int code,IntPtr remove,IntPtr pointer)
        {
          try
          {
            if(code>=0&&remove.ToInt64()==PreviewRemovedMessage&&pointer!=IntPtr.Zero&&_canEdit()&&RhinoDoc.ActiveDoc==_doc)
            {
              var message=System.Runtime.InteropServices.Marshal.PtrToStructure<Message>(pointer);
              int key=unchecked((int)message.Key.ToUInt64());
              var focus=GetFocus();var view=_doc.Views.ActiveView;
              bool viewport=ViewportHasFocus;
              bool down=message.Kind is PreviewWmKeyDown or PreviewWmSysKeyDown;
              bool altUp=message.Kind is PreviewWmKeyUp or PreviewWmSysKeyUp&&key==PreviewAltKey;
              var window=_window();bool manager=window!=IntPtr.Zero&&(focus==window||IsChild(window,focus));
              if(viewport&&key==PreviewAltKey&&(down||altUp))
              {
                System.Runtime.InteropServices.Marshal.WriteInt32(pointer,IntPtr.Size,0);
                Application.Instance.AsyncInvoke(()=>{if(!_disposed)_alt(down&&ViewportHasFocus);});
                return CallNextHookEx(_hook,code,remove,pointer);
              }
              bool history=down&&(GetKeyState(PreviewControlKey)&0x8000)!=0&&(GetKeyState(PreviewAltKey)&0x8000)==0&&key is PreviewUndoKey or PreviewRedoKey;
              if((viewport||manager)&&history)
              {
                bool redo=key==PreviewRedoKey||(GetKeyState(PreviewShiftKey)&0x8000)!=0;
                System.Runtime.InteropServices.Marshal.WriteInt32(pointer,IntPtr.Size,0);
                Application.Instance.AsyncInvoke(()=>_history(redo));
                return CallNextHookEx(_hook,code,remove,pointer);
              }
              bool managerRotation=manager&&down&&(GetKeyState(PreviewAltKey)&0x8000)!=0&&
                key is PreviewLeftKey or PreviewRightKey or PreviewUpKey or PreviewDownKey&&ManagerRotationAllowed();
              if((viewport||managerRotation)&&_canMove()&&(altUp||down&&(key is PreviewLeftKey or PreviewRightKey or PreviewUpKey or PreviewDownKey or PreviewEscapeKey or PreviewAltKey)))
              {
                bool alt=(GetKeyState(PreviewAltKey)&0x8000)!=0,shift=(GetKeyState(PreviewShiftKey)&0x8000)!=0,control=(GetKeyState(PreviewControlKey)&0x8000)!=0;
                System.Runtime.InteropServices.Marshal.WriteInt32(pointer,IntPtr.Size,0);
                if(key is PreviewLeftKey or PreviewRightKey or PreviewUpKey or PreviewDownKey)
                  QueueMove(key,alt,shift,control,(message.Parameter.ToInt64()&PreviewRepeatedKeyMask)!=0);
                else if(key!=PreviewAltKey||altUp)Application.Instance.AsyncInvoke(()=>{if(!_disposed)_action(key,alt,shift,control);});
              }
            }
          }
          catch(Exception ex){Log.Write("vNest","preview key routing failed: {0}",ex.Message);}
          return CallNextHookEx(_hook,code,remove,pointer);
        }
        private void QueueMove(int key,bool alt,bool shift,bool control,bool repeated)
        {
          if(_disposed||repeated&&_pendingMoves>0)return;
          _pendingMoves++;
          Application.Instance.AsyncInvoke(()=>
          {
            try
            {
              if(_disposed||!_canEdit()||!_canMove()||RhinoDoc.ActiveDoc!=_doc)return;
              if(repeated&&(!_isKeyDown(key)||alt!=_isKeyDown(PreviewAltKey)||shift!=_isKeyDown(PreviewShiftKey)||control!=_isKeyDown(PreviewControlKey)))return;
              _action(key,alt,shift,control);
            }
            catch(Exception ex){Log.Write("vNest","preview key move failed: {0}",ex.Message);}
            finally{_pendingMoves--;}
          });
        }
        public void Dispose(){_disposed=true;if(_hook!=IntPtr.Zero){UnhookWindowsHookEx(_hook);_hook=IntPtr.Zero;}}
        private static bool ManagerRotationAllowed()
        {
          var focused=System.Windows.Input.Keyboard.FocusedElement as System.Windows.DependencyObject;
          while(focused!=null)
          {
            if(focused is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox or System.Windows.Controls.PasswordBox)return false;
            focused=focused is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D?
              System.Windows.Media.VisualTreeHelper.GetParent(focused):System.Windows.LogicalTreeHelper.GetParent(focused);
          }
          return true;
        }
        internal static void FocusViewport(RhinoView view)=>SetFocus(view.Handle);
        internal bool ViewportHasFocus
        {
          get{var focus=GetFocus();var view=_doc.Views.ActiveView;return view!=null&&(focus==view.Handle||IsChild(view.Handle,focus));}
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr SetWindowsHookEx(int hook,Hook callback,IntPtr module,uint thread);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr remove,IntPtr message);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr GetFocus();
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern IntPtr SetFocus(IntPtr window);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern bool IsChild(IntPtr parent,IntPtr child);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern short GetKeyState(int key);
        [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
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
        RefreshTableHighlights();
        _tableObjectSerials.Clear();foreach(var table in _liveTables)
          foreach(var id in new[]{table.Border,table.Divider})if(_doc.Objects.FindId(id) is {} border)_tableObjectSerials.Add(border.RuntimeSerialNumber);
        _liveObjectSerials.Clear();
        foreach(var row in _rows.Where(row=>row.Included))
        foreach(var part in VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)))
          if(_liveParts.TryGetValue(part.Id,out var ids))
            foreach(var id in ids)if(_doc.Objects.FindId(id) is {} obj)_liveObjectSerials.Add(obj.RuntimeSerialNumber);
        var selected=SelectedParts.ToHashSet();
        IEnumerable<Curve> Outlines(IEnumerable<Row> rows)=>rows.SelectMany(row=>_liveTables.Count==0?new[]{row.Part.Outline}:
          VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)&&_manualGesture?.Ids.Contains(part.Id)!=true).Select(part=>PreviewOutline(row,part)));
        _sources.SetObjects(_liveTables.Count>0?OriginalsForRows(selected):[]);
        SetOutlineHighlights(_selectedSources,_liveTables.Count==0?Outlines(selected.Where(row=>!RowHasError(row,VisibleResult))):[]);
        SetOutlineHighlights(_skipped,Outlines(selected.Where(row=>RowHasError(row,VisibleResult))));
        RefreshMovingHighlights();
      }
      private IEnumerable<Guid> OriginalsForRows(IEnumerable<Row> rows)
      {
        foreach(var row in rows)
        {
          var json=row.Part.Items.Select(item=>item.Attributes.GetUserString(SourceObjectsKey)).FirstOrDefault(value=>!string.IsNullOrEmpty(value));
          if(json==null)continue;
          Guid[] ids;
          try{ids=System.Text.Json.JsonSerializer.Deserialize<Guid[]>(json)??[];}
          catch(System.Text.Json.JsonException ex){Log.Write("vNest","invalid original-part highlight references: {0}",ex.Message);continue;}
          foreach(var id in ids)if(_doc.Objects.FindId(id) is {IsDeleted:false})yield return id;
        }
      }
      private void RefreshTableHighlights()
      {
        _workingTables.SetObjects(_liveTables.Where((_,index)=>!IsTableSelected(index)).SelectMany(table=>new[]{table.Border,table.Divider}).Where(id=>id!=Guid.Empty));
        _selectedWorkingTables.SetObjects(_liveTables.Where((_,index)=>IsTableSelected(index)).SelectMany(table=>new[]{table.Border,table.Divider}).Where(id=>id!=Guid.Empty));
      }
      private static void SetOutlineHighlights(PreviewDisplay.ObjectHighlighter layer,IEnumerable<Curve> outlines)
      {var geometry=outlines.ToDictionary(_=>Guid.NewGuid(),curve=>(GeometryBase)curve);layer.SetObjects(geometry.Keys,geometry);}
      private void RefreshMovingHighlights()
      {
        _previewHasFocus=_manualGesture!=null||_previewKeyboard.ViewportHasFocus;
        _highlight.DrawOnTop=_manualGesture!=null;
        var geometry=new Dictionary<Guid,GeometryBase>();
        var active=new List<(Row Row,NestPackingEngine.Placement Placement)>();
        var selected=SelectedParts;
        foreach(var row in selected)
        {
          if(!row.Included) continue;
          foreach(var placement in VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)))
          {
          if(_manualSelectedInstances.Count>0&&!_manualSelectedInstances.Contains(placement.Id))continue;
          if(_activePreviewInstance.HasValue&&selected.Length==1&&placement.Id!=_activePreviewInstance.Value)continue;
          geometry.Add(Guid.NewGuid(),PreviewOutline(row,placement));
          InstancePreviewCopies(row,placement);active.Add((row,placement));
          }
        }
        _highlight.SetObjects(_previewHasFocus?geometry.Keys:[],geometry);
        _inactiveHighlight.SetObjects(_previewHasFocus?[]:geometry.Keys,geometry);
        _activePreviewParts=active.ToArray();
      }
      private void DocumentClosed(object? sender,DocumentEventArgs e) { if(e.Document.RuntimeSerialNumber==DocumentSerial) Close(); }
      private void DisposeState()
      {
        EndManualGesture(true);
        if(!_closed&&_externalPartSettings.Count>0) ReloadExternalPartSettings();
        if(_closed) return; _closed=true; _timer.Stop(); _timer.Dispose();
        _pendingPlacedTables.Clear();
        _manualUndo.Clear();_manualRedo.Clear();
        foreach(var row in _rows) SavePartSettings(row);
        if(_originalLayout!=null)foreach(var row in _originalLayout.Rows)row.Dispose();
        foreach(var table in _liveTables)table.Dispose();_liveTables.Clear();
        ClearTableLabels();
        foreach(var reset in _resets) reset.Dispose();
        _packingCancellation?.Cancel();
        RhinoDoc.CloseDocument-=DocumentClosed; RhinoDoc.UserStringChanged-=PartSettingsChanged; Rhino.Commands.Command.UndoRedo-=LiveUndoRedo; RhinoApp.Idle-=PackingIdle; _previewKeyboard.Dispose(); _picking.Dispose(); ClearHighlights(); ClearPreviewCopies(); _highlight.Dispose(); _inactiveHighlight.Dispose(); _sources.Dispose(); _selectedSources.Dispose(); _workingTables.Dispose(); _selectedWorkingTables.Dispose(); _skipped.Dispose(); _preview.Enabled=false;
        foreach(var row in _rows) row.Dispose(); _rows.Clear(); _doc.Views.Redraw();
      }

      private void Place()
      {
        if(_placing||_closed||_liveTables.Count>0) return;
        EndManualGesture(true);
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
            var material=TableMaterial(table);
            string tableName=TableDisplayName(table);
            int tableGroup=_doc.Groups.Add(UniqueTableGroupName(tableName)); groups.Add(tableGroup);
            if(tableGroup<0) throw new InvalidOperationException("Could not create table group.");
            Guid tableGroupId=_doc.Groups[tableGroup].Id;
            using var border=TableOutlineForTable(table).DuplicateCurve();
            var attributes=new ObjectAttributes {LayerIndex=reference,Name=TableName};
            attributes.AddToGroup(tableGroup); NestTableMetadata.Identify(attributes,tableGroupId,session,table,tableName,NestTableMetadata.BorderRole);
            attributes.SetUserString(MetadataPrefix+"material",material.Name);
            attributes.SetUserString(MetadataPrefix+"tableLength",Length.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MetadataPrefix+"occupiedLength",OccupiedLength(_result,table).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MetadataPrefix+"partCount",_result.Placements.Count(part=>part.Table==table).ToString(System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(MetadataPrefix+"tableWidth",material.Width.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            var tableFrame=DisplayTablePlane(table);
            attributes.SetUserString(TableFrameKey,System.Text.Json.JsonSerializer.Serialize(new[]{tableFrame.OriginX,tableFrame.OriginY,tableFrame.OriginZ,
              tableFrame.XAxis.X,tableFrame.XAxis.Y,tableFrame.XAxis.Z,tableFrame.YAxis.X,tableFrame.YAxis.Y,tableFrame.YAxis.Z}));
            attributes.SetUserString(TableFrameSamplesKey,System.Text.Json.JsonSerializer.Serialize(TableFrameSampleFractions.SelectMany(fraction=>
              {var point=border.PointAtNormalizedLength(fraction);return new[]{point.X,point.Y,point.Z};}).ToArray()));
            var borderId=_doc.Objects.AddCurve(border,attributes);
            if(borderId==Guid.Empty) throw new InvalidOperationException("Could not add table outline."); added.Add(borderId);
            if(TryTableLabelDivider(border,out var divider))
            {
              using var dividerAttributes=attributes.Duplicate();dividerAttributes.ObjectId=Guid.Empty;dividerAttributes.Name=string.Empty;
              dividerAttributes.SetUserString(NestTableMetadata.RoleKey,NestTableMetadata.LabelDividerRole);
              var dividerId=_doc.Objects.AddLine(divider,dividerAttributes);
              if(dividerId==Guid.Empty)throw new InvalidOperationException("Could not add table label divider.");added.Add(dividerId);
            }
            foreach(var label in LabelsForTable(table))
            {
              using var labelAttributes=new ObjectAttributes {LayerIndex=reference}; labelAttributes.AddToGroup(tableGroup);
              NestTableMetadata.Identify(labelAttributes,tableGroupId,session,table,tableName,NestTableMetadata.LabelRole);
              labelAttributes.SetUserString(MetadataPrefix+"material",material.Name);
              var labelId=_doc.Objects.AddText(label,labelAttributes);
              if(labelId==Guid.Empty) throw new InvalidOperationException("Could not add table label."); added.Add(labelId);
            }
            foreach(var placement in _result.Placements.Where(part=>part.Table==table))
            {
              var row=_rows.Single(part=>part.OwnsInstance(placement.Id));
              int partGroup=_doc.Groups.Add(GroupPrefix+session+"_part_"+placement.Id); groups.Add(partGroup);
              if(partGroup<0) throw new InvalidOperationException("Could not create part group.");
              var settings=CapturePartSettings(row,material) with {Copies=DefaultCopies,Included=DefaultIncluded,Fixed=DefaultFixed,
                ManualRotation=DefaultManualRotation,Flipped=DefaultAllowFlip,Angle=DefaultRotation,Table=DefaultTable,X=DefaultCoordinate,Y=DefaultCoordinate,
                Material=material.Name==CustomMaterialName?null:material.Name,Recipe=material.Name==CustomMaterialName?null:material};
              string placedKey="group-"+_doc.Groups[partGroup].Id.ToString("N");
              WritePartSettings(_doc,placedKey,settings);
              if(PartSettingsKey(_doc,row) is {} nameKey&&
                _doc.Strings.GetValue(PartNamesSection,nameKey) is {Length:>0} partName&&partName.Length<=PartDisplayNameLimit)
                _doc.Strings.SetString(PartNamesSection,placedKey,partName);
              if(PartSettingsKey(_doc,row) is {} sourceKey&&
                _doc.Strings.GetValue(PartCustomDataSection,sourceKey) is {Length:>0} custom)
              {
                if(custom.Length<=MaximumPartCustomDataCharacters) _doc.Strings.SetString(PartCustomDataSection,placedKey,custom);
                else Log.Write("vNest","custom part fields not copied: oversized record key={0}",sourceKey);
              }
              var transform=InstanceTransform(row,placement);
              foreach(var item in PartItemsForCopies(row))
              {
                using var copy=TransformedCopy(item.Geometry,transform);
                using var attr=item.Attributes.Duplicate(); attr.RemoveFromAllGroups(); attr.AddToGroup(partGroup); attr.AddToGroup(tableGroup);
                attr.SetUserString(MetadataPrefix+"session",session); attr.SetUserString(MetadataPrefix+"table",(table+1).ToString());
                attr.SetUserString(MetadataPrefix+"part",row.Id.ToString()); attr.SetUserString(MetadataPrefix+"instance",placement.Id.ToString());
                attr.SetUserString(MetadataPrefix+"copy",(row.InstanceIds().ToList().IndexOf(placement.Id)+1).ToString());
                attr.SetUserString(MetadataPrefix+"rotation",placement.Pose.Rotation.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                attr.SetUserString(MetadataPrefix+"flipped",placement.Pose.Flipped.ToString());
                attr.SetUserString(MetadataPrefix+"material",material.Name);
                attr.SetUserString(SourceObjectsKey,System.Text.Json.JsonSerializer.Serialize(row.Part.Items.Select(source=>source.Id).ToArray()));
                attr.SetUserString(SourceSettingsKey,PartSettingsKey(_doc,row));
                var id=_doc.Objects.Add(copy,attr); if(id==Guid.Empty) throw new InvalidOperationException("Could not add a nested part."); added.Add(id);
              }
              if(row.DrawnGuide is {} guide)
              {
                using var attr=new ObjectAttributes {Name=GrainIdentifier,LayerIndex=UzipCommon.EnsureLayer(_doc,GrainIdentifier)};
                attr.AddToGroup(partGroup); attr.AddToGroup(tableGroup);
                attr.SetUserString(MetadataPrefix+"material",material.Name);
                attr.SetUserString(MetadataPrefix+"session",session); attr.SetUserString(MetadataPrefix+"table",(table+1).ToString());
                attr.SetUserString(MetadataPrefix+"part",row.Id.ToString()); attr.SetUserString(MetadataPrefix+"instance",placement.Id.ToString());
                attr.SetUserString(SourceObjectsKey,System.Text.Json.JsonSerializer.Serialize(row.Part.Items.Select(source=>source.Id).ToArray()));
                attr.SetUserString(SourceSettingsKey,PartSettingsKey(_doc,row));
                var id=_doc.Objects.AddLine(transform*guide.From,transform*guide.To,attr);
                if(id==Guid.Empty) throw new InvalidOperationException("Could not add drawn grain guide."); added.Add(id);
              }
            }
          }
          _doc.Objects.UnselectAll(); _doc.Objects.Select(added); Save();
          Log.Write("vNest","placement created tables={0} parts={1} objects={2}",_result.Tables,_result.Placements.Count,added.Count);
          RhinoApp.WriteLine($"vNest: created {_result.Tables} table(s), {_result.Placements.Count} grouped part copies, {_result.Tables} grouped table-label parts, {_result.Errors.Count} skipped; {TotalYardageText(_result,YardsPerUnit())}; originals retained.");
          foreach(var line in MaterialUsageLines(MaterialUsageTotals(_result,CurrentMaterialValues().Name),YardsPerUnit()))
            RhinoApp.WriteLine(line);
          Close();
        }
        catch(Exception ex)
        {
          foreach(var id in added) _doc.Objects.Delete(id,true);
          foreach(var group in groups)
          {
            if(group>=0&&_doc.Groups[group] is {} created)
            {
              string key="group-"+created.Id.ToString("N");
              _doc.Strings.Delete(PartSettingsSection,key);_doc.Strings.Delete(PartCustomDataSection,key);_doc.Strings.Delete(PartNamesSection,key);
              _doc.Groups.Delete(group);
            }
          }
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
        private bool SuppressObject(uint serial)=>window._liveTables.Count>0&&
          (window._tableObjectSerials.Contains(serial)||window._liveObjectSerials.Contains(serial)||window._manualGesture!=null&&window._gestureObjectSerials.Contains(serial));
        protected override void ObjectCulling(CullObjectEventArgs e)
        {
          if(SuppressObject(e.RhinoObjectSerialNumber))e.CullObject=true;
        }
        protected override void PreDrawObject(DrawObjectEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber==window.DocumentSerial&&
            SuppressObject(e.RhinoObject.RuntimeSerialNumber))e.DrawObject=false;
        }
        protected override void PreDrawObjects(DrawEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber!=window.DocumentSerial)return;
          e.Display.PushDepthTesting(false);e.Display.PushDepthWriting(false);
          try{DrawTables(e.Display);window._workingTables.DrawForegroundHighlights(e);window._selectedWorkingTables.DrawForegroundHighlights(e);}
          finally{e.Display.PopDepthWriting();e.Display.PopDepthTesting();}
        }
        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber!=window.DocumentSerial) return;
          foreach(var row in window._rows)
            if(row.DrawnGuide is {} guide) e.IncludeBoundingBox(new BoundingBox(new[]{guide.From,guide.To}));
          for(int table=0;table<PreviewTableCount(window.VisibleResult.Tables,window._locationPicking);table++)
          { var plane=window.DisplayTablePlane(table);
            e.IncludeBoundingBox(new Rectangle3d(plane,window.TableMaterial(table).Width,Length).BoundingBox);
            foreach(var label in window.LabelsForTable(table)) e.IncludeBoundingBox(label.GetBoundingBox(true));
            e.IncludeBoundingBox(window.TableOutlineForTable(table).GetBoundingBox(true)); }
          foreach(var part in window.VisibleResult.Placements)
          {
            var plane=window.DisplayTablePlane(part.Table);
            e.IncludeBoundingBox(new Rectangle3d(new Plane(plane.PointAt(part.X,part.Y),plane.XAxis,plane.YAxis),part.Pose.Width,part.Pose.Length).BoundingBox);
            if(window.PlacementOwner(part.Id) is {} row)
            {
              var bounds=row.PreviewBounds;
              if(row.DrawnGuide is {} guide)bounds.Union(new BoundingBox(new[]{guide.From,guide.To}));
              bounds.Transform(window.InstanceTransform(row,part));e.IncludeBoundingBox(bounds);
            }
          }
        }
        protected override void PostDrawObjects(DrawEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber!=window.DocumentSerial) return;
          Draw(e.Display);
        }
        protected override void DrawForeground(DrawEventArgs e)
        {
          DrawPartHighlights(e);
        }
        internal void DrawPartHighlights(DrawEventArgs e)
        {
          if(e.RhinoDoc.RuntimeSerialNumber!=window.DocumentSerial)return;
          window._sources.DrawForegroundHighlights(e);
          window._selectedSources.DrawForegroundHighlights(e);
          window._skipped.DrawForegroundHighlights(e);
          e.Display.PushDepthTesting(false);e.Display.PushDepthWriting(false);
          try
          {
            foreach(var part in window._activePreviewParts)DrawContents(e.Display,part.Row,part.Placement);
            window._highlight.DrawForegroundHighlights(e);
            window._inactiveHighlight.DrawForegroundHighlights(e);
            if(window._showGap.Checked==true&&!window._locationPicking)
              foreach(var part in window._activePreviewParts)
                foreach(var path in window.PreviewClearance(part.Row,part.Placement))e.Display.DrawPolyline(path,GapOutlineColor,GapOutlineWidth);
            DrawPivot(e.Display);
          }
          finally{e.Display.PopDepthWriting();e.Display.PopDepthTesting();}
        }
        private void DrawPivot(DisplayPipeline display)
        {
          if(window._pivotVisible&&window.CanFineTune&&window._previewKeyboard.ViewportHasFocus)
          {
            var selected=window._manualGesture is {Rotation:true} gesture?
              gesture.Before.Placements.Where(part=>gesture.Ids.Contains(part.Id)):window.ManualSelection();
            var parts=selected.ToArray();
            if(parts.Length>0)display.DrawPoint(window._manualGesture is {Rotation:true} rotating?rotating.Pivot:window.ManualPivot(parts),
              PointStyle.X,RotationPivotSize,SelectedPartStyle.BodyColor);
          }
        }
        internal void Draw(DisplayPipeline display,bool locating=false)
        {
          var grainLayer=window._doc.Layers.FirstOrDefault(layer=>!layer.IsDeleted&&layer.Name.Equals(GrainIdentifier,StringComparison.OrdinalIgnoreCase));
          var grainColor=grainLayer?.Color??DrawnGuideColor;
          foreach(var row in window._rows)
            if(row.DrawnGuide is {} guide) display.DrawLine(guide,grainColor);
          foreach(var row in window._rows.Where(row=>row.Included))
          foreach(var placement in window.VisibleResult.Placements.Where(part=>row.OwnsInstance(part.Id)))
          {
            DrawContents(display,row,placement);
          }
        }
        internal void DrawTables(DisplayPipeline display,bool locating=false)
        {
          for(int table=0;table<PreviewTableCount(window.VisibleResult.Tables,locating||window._locationPicking);table++)
          { var plane=window.DisplayTablePlane(table);
            if(window._liveTables.Count>0)
            {
              if(window.IsTableSelected(table))
              {
                var outline=window.TableOutlineForTable(table);PreviewDisplay.DrawCurve(display,outline,SelectedTableStyle.BodyColor);
                if(TryTableLabelDivider(outline,out var divider))display.DrawLine(divider,SelectedTableStyle.BodyColor);
              }
              continue;
            }
            using var border=new Rectangle3d(plane,window.TableMaterial(table).Width,Length).ToNurbsCurve();
            var color=window.IsTableSelected(table)?SelectedTableStyle.BodyColor:TableColor;
            PreviewDisplay.DrawCurve(display,window.TableOutlineForTable(table),color);
            if(TryTableLabelDivider(window.TableOutlineForTable(table),out var labelDivider))display.DrawLine(labelDivider,color);
            foreach(var label in window.LabelsForTable(table)) display.DrawAnnotation(label,color); }
        }
        private void DrawContents(DisplayPipeline display,Row row,NestPackingEngine.Placement placement)
        {
            if(row.DrawnGuide is {} guide)
            {
              var grainLayer=window._doc.Layers.FirstOrDefault(layer=>!layer.IsDeleted&&layer.Name.Equals(GrainIdentifier,StringComparison.OrdinalIgnoreCase));
              var transform=window.InstanceTransform(row,placement);display.DrawLine(transform*guide.From,transform*guide.To,grainLayer?.Color??DrawnGuideColor);
            }
            foreach(var item in window.InstancePreviewCopies(row,placement))
            {
              var color=item.Color;
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

    private sealed class PackingStatus : Drawable
    {
      private string _text=string.Empty;
      private string _activity=InitialPackingActivity;
      private int? _percent;
      private int _stepPercent;
      internal string Text { get=>_text; set { _text=value; Invalidate(); } }
      internal PackingStatus() { Height=GridRowHeight; }
      internal void SetProgress(int percent,TimeSpan elapsed)
      {
        _percent=Math.Clamp(percent,0,100);
        _stepPercent=_percent.Value;
        Text=$"{ProgressText(_percent.Value,elapsed)} {_activity}";
      }
      internal void SetActivity(string activity)
      { _activity=activity; if(_percent.HasValue) Text=$"{_percent.Value}% {_activity}"; }
      internal void SetStageProgress(string stage,int percent,int total)
      { _activity=stage; _stepPercent=Math.Clamp(percent,0,100); _percent=Math.Clamp(total,0,100); Text=$"{stage}: {_stepPercent}% | Total: {_percent}%"; }
      internal void EndProgress() { _percent=null; _activity=InitialPackingActivity; Invalidate(); }
      protected override void OnPaint(PaintEventArgs e)
      {
        base.OnPaint(e);
        e.Graphics.FillRectangle(SystemColors.ControlBackground,new RectangleF(0,0,Width,Height));
        if(_percent.HasValue)
        {
          float height=Math.Max(0,(Height-ProgressTrackGap)/2f);
          // SystemFonts returns shared fonts that must not be disposed by this control.
          var nominal=SystemFonts.Default(ProgressFontSize);
          float measured=e.Graphics.MeasureString(nominal,"Total: 100%").Height;
          var font=SystemFonts.Default(measured>height?ProgressFontSize*height/measured:ProgressFontSize);
          void Track(float y,int percent,string text)
          {
            e.Graphics.FillRectangle(new Eto.Drawing.Color(SystemColors.Highlight,ProgressTintAlpha),new RectangleF(0,y,Width*percent/100f,height));
            var size=e.Graphics.MeasureString(font,text);
            e.Graphics.DrawText(font,SystemColors.ControlText,Math.Max(0,(Width-size.Width)/2),y+Math.Max(0,(height-size.Height)/2),text);
          }
          Track(0,_stepPercent,$"{_activity}: {_stepPercent}%");
          Track(height+ProgressTrackGap,_percent.Value,$"Total: {_percent}%");
          return;
        }
        var statusFont=SystemFonts.Default(); var statusSize=e.Graphics.MeasureString(statusFont,Text);
        e.Graphics.DrawText(statusFont,SystemColors.ControlText,0,Math.Max(0,(Height-statusSize.Height)/2),Text);
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
        RotationMode? Rotation,double? Step,bool? Obey,bool? Reverse,double? Deviation,double Angle,int Table,double X,double Y,Vector3d? Guide,int Copies,string? Material,Line? DrawnGuide);
      private static PartSettings SettingsOf(Row row)=>new(row.Included,row.Fixed,row.ManualRotation,row.Flipped,row.FlipOverride,
        row.GrainOverride,row.RotationOverride,row.RotationStepOverride,row.ObeyGrainOverride,row.AllowReverseOverride,row.GrainDeviationOverride,
        row.Rotation,row.Table,row.X,row.Y,row.Guide,row.Copies,row.MaterialName,row.DrawnGuide);
      private static bool IsCustomized(Row row)=>row.Fixed||row.ManualRotation||HasReusableSettings(row);
      private static bool HasReusableSettings(Row row)=>row.Copies!=DefaultCopies||row.Included!=DefaultIncluded||
        row.FlipOverride.HasValue||row.GrainOverride.HasValue||row.RotationOverride.HasValue||row.RotationStepOverride.HasValue||
        row.ObeyGrainOverride.HasValue||row.AllowReverseOverride.HasValue||row.GrainDeviationOverride.HasValue||row.MaterialName!=null||
        row.DrawnGuide.HasValue||row.Guide!=row.DetectedGuide;

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
      private int RotationIndex(Row row,bool constrained)
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
        row.RotationStepOverride=null; row.ObeyGrainOverride=null; row.AllowReverseOverride=null; row.GrainDeviationOverride=null; row.MaterialName=null; row.StoredMaterial=null;
        row.Rotation=DefaultRotation; row.Table=DefaultTable; row.X=row.Y=DefaultCoordinate; row.Guide=row.DetectedGuide; row.DrawnGuide=null;
      }
      private void InstallResets()
      {
        void Add(Control field,Func<bool> changed,Action reset,string name)
        {
          var overlay=new FieldResetOverlay(field,changed,reset,"Reset "+name+" to default"); _resets.Add(overlay);
        }
        void Global(NumericStepper field,double value,string name)=>Add(field,
          ()=>FieldResetOverlay.NumberChanged(field.Value,value,field.DecimalPlaces),()=>field.Value=value,name);
        void Material(NumericStepper field,Func<MaterialPreset,double> value,string name)
        {
          double Baseline()=>value(MaterialBaseline());
          var overlay=new FieldResetOverlay(field,()=>FieldResetOverlay.NumberChanged(field.Value,Baseline(),field.DecimalPlaces),
            ()=>field.Value=Baseline(),"Reset "+name+" to material preset"); _resets.Add(overlay);
        }
        Material(_width,preset=>preset.Width,"width"); Global(_length,DefaultLength,"length");
        Global(_rotationStep,DefaultRotationStep,"rotation step"); Global(_searchTime,DefaultSearchTime,"search time");
        Material(_partGap,preset=>preset.Gap,"gap"); Material(_grainDeviation,preset=>preset.GrainDeviation,"grain deviation");
        Add(_grain,()=>_grain.SelectedIndex!=(int)MaterialBaseline().Grain,()=>_grain.SelectedIndex=(int)MaterialBaseline().Grain,"material grain mode");
        Add(_rotationPreset,()=>_rotationPreset.SelectedIndex!=(int)DefaultRotationMode,()=>_rotationPreset.SelectedIndex=(int)DefaultRotationMode,"free rotation");
        Add(_allowReverse,()=>AllowReverse!=MaterialBaseline().AllowReverse,()=>_allowReverse.SelectedIndex=MaterialBaseline().AllowReverse?1:0,"material grain reversal");
        Add(_obeyDefault,()=>_obeyDefault.Checked!=MaterialBaseline().ObeyGrain,()=>_obeyDefault.Checked=MaterialBaseline().ObeyGrain,"material grain obedience");
        void PartNumber(NumericStepper field,Func<Row,bool> changed,Action<Row> reset,string name)
        {
          Add(field,()=>SelectedParts.Any(changed),()=>Edit(reset),name);
          Add(_mixedNumbers[field].MixedEditor,()=>SelectedParts.Any(changed),()=>Edit(reset),name);
        }
        PartNumber(_copies,row=>row.Copies!=DefaultCopies,row=>row.Copies=DefaultCopies,"copies");
        Add(_angle,AngleResetChanged,ResetAngle,"angle");
        Add(_mixedNumbers[_angle].MixedEditor,AngleResetChanged,ResetAngle,"angle");
        PartNumber(_limit,row=>row.RotationStepOverride.HasValue,row=> { row.RotationStepOverride=null; if(row.RotationOverride==RotationMode.Custom) row.RotationOverride=null; },"global step");
        PartNumber(_partDeviation,row=>row.GrainDeviationOverride.HasValue,row=>row.GrainDeviationOverride=null,"material deviation");
        PartNumber(_guideAngle,row=>row.Guide!=row.DetectedGuide||row.DrawnGuide.HasValue,row=> {row.Guide=row.DetectedGuide;row.DrawnGuide=null;},"detected grain");
        Add(_partGrain,()=>SelectedParts.Any(row=>row.GrainOverride.HasValue),()=>Edit(row=>row.GrainOverride=null),"material grain mode");
        Add(_partRotation,()=>SelectedParts.Any(row=>row.RotationOverride.HasValue||row.AllowReverseOverride.HasValue),
          ()=>Edit(row=> { row.RotationOverride=null; row.AllowReverseOverride=null; }),"global rotation");
        Add(_partMaterial,()=>SelectedParts.Any(row=>row.MaterialName!=null),()=>Edit(row=>row.MaterialName=null),"default material");
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
    internal static double OccupiedWidth(NestPackingEngine.Result result,int table)
    {
      var parts=result.Placements.Where(part=>part.Table==table).ToArray();
      return parts.Length==0?0:parts.Max(part=>part.X+part.Pose.Width)-parts.Min(part=>part.X);
    }
    internal sealed record MaterialUsage(string Name,int Tables,double OccupiedLength);
    internal static MaterialUsage[] MaterialUsageTotals(NestPackingEngine.Result result,string fallbackMaterialName=CustomMaterialName)=>
      result.Placements.GroupBy(part=>part.Table)
        .Select(table=>new {Name=result.TableMaterials.GetValueOrDefault(table.Key)?.Name??fallbackMaterialName,Length=table.Max(part=>part.Y+part.Pose.Length)+result.TableAllowance})
        .GroupBy(table=>table.Name,StringComparer.OrdinalIgnoreCase)
        .Select(material=>new MaterialUsage(material.Key,material.Count(),material.Sum(table=>table.Length)))
        .OrderBy(material=>material.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    private static string YardageText(double occupiedLength,double yardsPerUnit)=>
      double.IsFinite(yardsPerUnit)&&yardsPerUnit>0?(occupiedLength*yardsPerUnit).ToString(TableLengthFormat,CultureInfo.InvariantCulture)+" "+YardageUnitLabel:UnknownYardageText;
    internal static string TotalYardageText(NestPackingEngine.Result result,double yardsPerUnit)=>
      "Total: "+YardageText(NestPackingEngine.MaterialConsumption(result),yardsPerUnit);

    internal static string[] MaterialUsageLines(IReadOnlyList<MaterialUsage> usage,double yardsPerUnit)
    {
      var rows=usage.Select(material=>(Name:material.Name+":",Yards:YardageText(material.OccupiedLength,yardsPerUnit),
        Tables:material.Tables.ToString(CultureInfo.InvariantCulture))).ToArray();
      int nameWidth=rows.Select(row=>row.Name.Length).DefaultIfEmpty(0).Max();
      int yardsWidth=rows.Select(row=>row.Yards.Length).DefaultIfEmpty(0).Max();
      int tablesWidth=rows.Select(row=>row.Tables.Length).DefaultIfEmpty(0).Max();
      return rows.Select(row=>"vNest: "+row.Name.PadRight(nameWidth)+ResultColumnGap+row.Yards.PadLeft(yardsWidth)+
        ResultColumnGap+"("+row.Tables.PadLeft(tablesWidth)+" used table(s)).").ToArray();
    }
    internal static long NextTableNumber(IEnumerable<string?> names)
    {
      long next=FirstTableNumber;
      foreach(var name in names)
        if(name!=null&&name.StartsWith(DefaultTableNamePrefix,StringComparison.OrdinalIgnoreCase)&&
          long.TryParse(name[DefaultTableNamePrefix.Length..],System.Globalization.NumberStyles.None,CultureInfo.InvariantCulture,out long number)&&
          number>=next&&number<long.MaxValue-NestPackingEngine.MaximumTables)next=number+1;
      return next;
    }
    internal static string[] TableLabelRows(string name,double width,double occupied,double yardsPerUnit)=>
      [name,occupied.ToString(TableLengthFormat,CultureInfo.InvariantCulture)+" x "+width.ToString(TableWidthFormat,CultureInfo.InvariantCulture),
        double.IsFinite(yardsPerUnit)&&yardsPerUnit>0?(occupied*yardsPerUnit).ToString(TableLengthFormat,CultureInfo.InvariantCulture)+" yds":"n/a yds"];
    internal static string[] TableLabelRowsWithParts(string name,int parts,double width,double occupied,double yardsPerUnit)
    {
      var rows=TableLabelRows(name,width,occupied,yardsPerUnit);
      return [rows[0],parts.ToString(CultureInfo.InvariantCulture)+" "+(parts==1?TableSinglePartLabel:TableMultiplePartsLabel),rows[1],rows[2]];
    }

    internal sealed partial class NestWindow
    {
      private readonly Dictionary<int,string> _tableNames=[];
      private readonly Dictionary<int,string> _defaultTableNames=[];
      private readonly GridView _tableGrid=new() {AllowMultipleSelection=false,RowHeight=GridRowHeight};
      private readonly GridView _materialUsageGrid=new() {AllowMultipleSelection=false,RowHeight=GridRowHeight};
      private readonly TabControl _lists=new();
      private bool _tableEditing;
      private sealed record TableLabels(string Key,Point3d Origin,TextEntity[] Text,Curve Frame,Curve Outline,double FrameWidth,double Clearance);
      private readonly Dictionary<int,TableLabels> _tableLabels=[];
      private readonly Dictionary<(string Text,double Height,string Font,bool Bold,bool Italic,uint Style,double Scale,Plane Plane,Plane Frame),BoundingBox> _labelMeasurements=[];
      private string TableDisplayName(int index)
      {
        if(index>=0&&index<_liveTables.Count)return _liveTables[index].Name;
        if(_tableNames.TryGetValue(index,out var custom))return custom;
        if(_defaultTableNames.TryGetValue(index,out var name))return name;
        var names=NestTableMetadata.TableGroups(_doc).SelectMany(group=>
          _doc.Groups.GroupMembers(group).Where(obj=>NestTableMetadata.IsTableBorder(_doc,obj))
            .Select(obj=>obj.Attributes.GetUserString(MetadataPrefix+"tableName")??_doc.Groups.GroupName(group)))
          .Concat(_defaultTableNames.Values).Concat(_tableNames.Values);
        long next=NextTableNumber(names);
        for(int table=0;table<=index;table++)
          if(!_defaultTableNames.ContainsKey(table))_defaultTableNames[table]=DefaultTableNamePrefix+next++;
        return _defaultTableNames[index];
      }
      private Control CreateListsView(Control editor,Control actions,Control caption)
      {
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Name",DataCell=new TextBoxCell(0),Editable=true,Width=PartNameWidth,Expand=true,HeaderToolTip="Editable table name"});
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Used length x used width",DataCell=new TextBoxCell(1),Width=FieldWidth});
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Used material",DataCell=new TextBoxCell(2),Width=FieldWidth});
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Material",DataCell=new TextBoxCell(3),Width=FieldWidth});
        _tableGrid.Columns.Add(new GridColumn {HeaderText="Parts",DataCell=new TextBoxCell(4),Width=TablePartCountWidth});
        _tableGrid.CellEditing+=(_,_)=>_tableEditing=true;
        _tableGrid.CellEdited+=(_,e)=>
        {
          _tableEditing=false;
          if(e.Item is GridItem {Tag:int table} item) RenameTable(table,Convert.ToString(item.Values[0])??string.Empty);
        };
        _tableGrid.KeyDown+=(_,e)=> { if(e.Key==Keys.Escape) { _tableEditing=false; Application.Instance.AsyncInvoke(RefreshTableRows); } };
        _tableGrid.SelectionChanged+=(_,_)=> { if(!_updating) {if(_liveTables.Count>0)SetPlacedControls(true);RefreshTableHighlights();_doc.Views.Redraw();} };
        _materialUsageGrid.Columns.Add(new GridColumn {HeaderText="Material",DataCell=new TextBoxCell(0),Width=PartNameWidth,Expand=true});
        _materialUsageGrid.Columns.Add(new GridColumn {HeaderText="Used tables",DataCell=new TextBoxCell(1),Width=MaterialUsageTableCountWidth});
        _materialUsageGrid.Columns.Add(new GridColumn {HeaderText="Total yardage",DataCell=new TextBoxCell(2),Width=TotalYardageWidth});
        _materialUsageGrid.SelectionChanged+=(_,_)=> { if(!_updating) {RefreshTableHighlights();_doc.Views.Redraw();} };
        EnableSorting(_tableGrid,SortTableColumn);EnableSorting(_materialUsageGrid,SortMaterialColumn);
        _lists.Pages.Add(new TabPage {Text="Parts",Content=_grid});
        _lists.Pages.Add(new TabPage {Text="Tables",Content=_tableGrid});
        _lists.Pages.Add(new TabPage {Text="Materials",Content=_materialUsageGrid});
        _lists.SelectedIndexChanged+=(_,_)=>
        { bool parts=_lists.SelectedIndex==0; editor.Visible=actions.Visible=caption.Visible=parts; if(_liveTables.Count>0)SetPlacedControls(true);RefreshTableHighlights();_doc.Views.Redraw(); };
        return _lists;
      }
      private bool IsTableSelected(int table)=>_lists.SelectedIndex switch
      {
        1=>_tableGrid.SelectedItem is GridItem {Tag:int selectedTable}&&selectedTable==table,
        2=>_materialUsageGrid.SelectedItem is GridItem {Tag:MaterialUsage material}&&
          TableMaterial(table).Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase),
        _=>false
      };
      private void RefreshMaterialUsage(MaterialUsage[] usage)
      {
        string? selected=(_materialUsageGrid.SelectedItem as GridItem)?.Tag is MaterialUsage selectedUsage?selectedUsage.Name:null;
        var items=usage.Select(material=>new GridItem {Tag=material,
          Values=[material.Name,material.Tables.ToString(CultureInfo.InvariantCulture),YardageText(material.OccupiedLength,YardsPerUnit())]}).ToList();
        var ordered=SortedItems(items,_materialSortColumn,_materialSortDescending,item=>_materialSortColumn==2?
          ((MaterialUsage)item.Tag!).OccupiedLength:item.Values[_materialSortColumn]);
        _materialUsageGrid.DataStore=ordered;
        int index=ordered.FindIndex(item=>string.Equals(((MaterialUsage)item.Tag!).Name,selected,StringComparison.OrdinalIgnoreCase));
        if(index>=0) _materialUsageGrid.SelectedRow=index;
      }
      private void RenameTable(int table,string value)
      {
        var name=System.Text.RegularExpressions.Regex.Replace(value,@"\s+"," ").Trim();
        if(name.Length==0){if(table<0||table>=_liveTables.Count)_tableNames.Remove(table);name=TableDisplayName(table);}
        name=name[..Math.Min(name.Length,TableNameLimit)];
        if(table<0||table>=_liveTables.Count)_tableNames[table]=name;
        else if(name!=_liveTables[table].Name)
        {
          EndManualGesture(true);
          uint undo=_doc.UndoRecordingEnabled?_doc.BeginUndoRecord("vNest rename table"):0;
          if(undo==0){_status.Text="Enable Rhino undo before renaming placed tables";RefreshTableRows();return;}
          var snapshot=CaptureManualState(_result) with {DocumentRecord=undo};bool success=false;
          string beforeGroup=_doc.Groups.GroupName(_liveTables[table].Group);
          _applyingLive=true;
          try
          {
            var info=_liveTables[table];_liveTables[table]=info with {Name=name};
            if(_doc.Groups.GroupName(info.Group)!=name&&!_doc.Groups.ChangeGroupName(info.Group,UniqueTableGroupName(name)))throw new InvalidOperationException("Could not rename table group.");
            foreach(var obj in _doc.Groups.GroupMembers(info.Group).Where(obj=>!string.IsNullOrEmpty(obj.Attributes.GetUserString(MetadataPrefix+"tableName"))))
            {
              using var attributes=obj.Attributes.Duplicate();attributes.SetUserString(MetadataPrefix+"tableName",name);
              if(!_doc.Objects.ModifyAttributes(obj.Id,attributes,true))throw new InvalidOperationException("Could not rename table metadata.");
            }
            UpdateLiveUsage();
            if(!_doc.AddCustomUndoEvent("vNest table name",UndoTableGroupRename,new TableGroupRename(_doc.Groups[info.Group].Id,beforeGroup,_doc.Groups.GroupName(info.Group))))
              throw new InvalidOperationException("Could not record table group-name undo.");
            success=true;
          }
          catch(Exception ex){Log.Write("vNest","table rename failed: {0}",ex);_status.Text="Could not rename placed table";}
          finally{_doc.EndUndoRecord(undo);_applyingLive=false;}
          if(success){_manualUndo.Add(snapshot);_manualRedo.Clear();}
          else
          {
            _applyingLive=true;
            try{ExecuteLiveHistory(false);_doc.ClearRedoRecords();_doc.Groups.ChangeGroupName(_liveTables[table].Group,beforeGroup);RefreshLiveDecorations();}
            finally{_applyingLive=false;}
          }
        }
        RefreshTableRows();RefreshHighlights();_doc.Views.Redraw();
      }
      private double YardsPerUnit()=>_doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits
        ?double.NaN:RhinoMath.UnitScale(_doc.ModelUnitSystem,UnitSystem.Yards);
      private void RefreshTableRows()
      {
        if(_tableEditing||_closed) return;
        int? selected=(_tableGrid.SelectedItem as GridItem)?.Tag as int?; bool before=_updating; _updating=true;
        try
        {
          var items=Enumerable.Range(0,VisibleResult.Tables).Select(table=>
          {
            var material=TableMaterial(table);
            var rows=TableLabelRows(TableDisplayName(table),OccupiedWidth(VisibleResult,table),OccupiedLength(VisibleResult,table),YardsPerUnit());
            rows[^1]=YardageText(OccupiedLength(VisibleResult,table)+VisibleResult.TableAllowance,YardsPerUnit());
            return new GridItem {Tag=table,Values=rows.Cast<object>().Append(material.Name)
              .Append(VisibleResult.Placements.Count(part=>part.Table==table).ToString(CultureInfo.InvariantCulture)).ToArray()};
          }).ToList();
          var ordered=SortedItems(items,_tableSortColumn,_tableSortDescending,item=>_tableSortColumn switch
          {1=>(OccupiedLength(VisibleResult,(int)item.Tag!),OccupiedWidth(VisibleResult,(int)item.Tag!)),
            2=>OccupiedLength(VisibleResult,(int)item.Tag!),_=>item.Values[_tableSortColumn]});
          _tableGrid.DataStore=ordered;
          if(selected.HasValue){int index=ordered.FindIndex(item=>(int)item.Tag! ==selected.Value);if(index>=0)_tableGrid.SelectedRow=index;}
        }
        finally { _updating=before; }
      }
      private Vector3d DisplayOffset(int table)
      {
        double offset=0;
        for(int index=1;index<=table;index++)
          offset+=TableMaterial(index).Width+TableGap;
        return -_plane.YAxis*offset;
      }
      private Point3d DisplayAnchor(int table)=>_anchor+DisplayOffset(table)-TableOffset(_plane,table);
      private double InitialLabelExtent()=>LabelGeometry(0).FrameWidth+LabelGeometry(0).Clearance;
      private TextEntity[] LabelsForTable(int table)
      {
        if(table>=0&&table<_liveTables.Count)return _liveTables[table].Labels;
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
      {if(table>=0&&table<_liveTables.Count)return _liveTables[table].Outline;LabelsForTable(table); return _tableLabels[table].Outline; }
      private static bool TryTableLabelDivider(Curve outline,out Line divider)
      {
        divider=Line.Unset;
        if(!outline.TryGetPolyline(out var points)||points.Count is not (8 or 9))return false;
        divider=new Line(points[points.Count==8?3:4],points[points.Count==8?6:7]);
        return divider.IsValid&&divider.Length>RhinoMath.ZeroTolerance;
      }
      private TableLabels LabelGeometry(int table)
      {
        if(_manualGesture!=null&&_tableLabels.TryGetValue(table,out var duringGesture))return duringGesture;
        double used=VisibleResult.Tables==0?Length:OccupiedLength(VisibleResult,table);
        var material=TableMaterial(table); double width=material.Width;
        int parts=VisibleResult.Placements.Count(part=>part.Table==table);
        double usedWidth=VisibleResult.Tables==0?width:OccupiedWidth(VisibleResult,table);
        var rows=new[]{material.Name}.Concat(TableLabelRowsWithParts(TableDisplayName(table),parts,usedWidth,used,YardsPerUnit())).ToArray();
        rows[^1]=YardageText(used+VisibleResult.TableAllowance,YardsPerUnit());
        var style=_doc.DimStyles.Current;
        string key=string.Join("\n",rows)+FormattableString.Invariant($"|{width:R}|{Length:R}|{_doc.ModelSpaceTextScale:R}|{style.Id}|{style.DimensionScale:R}|{style.Font.FaceName}|{style.Font.Bold}|{style.Font.Italic}");
        if(_tableLabels.TryGetValue(table,out var previous)&&previous.Key==key)
          return previous;
        if(previous!=null) { foreach(var text in previous.Text) text.Dispose(); previous.Frame.Dispose(); previous.Outline.Dispose(); }
        try
        {
          var result=BuildTableLabels(key,rows,new Plane(Point3d.Origin,_plane.XAxis,_plane.YAxis),width,Length);
          _tableLabels[table]=result;return result;
        }
        catch{_tableLabels.Remove(table);throw;}
      }
      private TableLabels BuildTableLabels(string key,string[] rows,Plane plane,double width,double length)
      {
        var style=_doc.DimStyles.Current;
        double targetHeight=TableLabelTargetHeight(width);
        TextEntity MakeText(string row)=>new() {PlainText=row,Plane=plane,
          ParentDimensionStyle=style,DimensionStyleId=style.Id,TextHeight=targetHeight,DimensionScale=TableLabelScale,Justification=TextJustification.BottomLeft,
          TextOrientation=TextOrientation.InPlane,DrawForward=false};
        var texts=rows.Select(MakeText).ToArray();
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
          using var sample=MakeText(TableLabelHeightSample);Height(sample,targetHeight);
          double glyphHeight=LabelGlyphBounds(sample,frame).Diagonal.Y;
          if(glyphHeight<=RhinoMath.ZeroTolerance) throw new InvalidOperationException("Could not size table label glyphs.");
          double nominalHeight=targetHeight*targetHeight/glyphHeight;
          foreach(var text in texts) Height(text,nominalHeight);
          Height(sample,nominalHeight);
          double baselineOffset=-LabelGlyphBounds(sample,frame).Min.Y;
          sample.PlainText=TableLabelMinimumDimensions;Height(sample,nominalHeight);
          double minimumWidth=LabelGlyphBounds(sample,frame).Diagonal.X;
          var bounds=texts.Select(text=>LabelGlyphBounds(text,frame)).ToArray();
          double maxWidth=Math.Max(minimumWidth,bounds.Take(rows.Length>=5?2:1).Max(box=>box.Diagonal.X));
          for(int index=0;index<texts.Length;index++)
          {
            double scale=Math.Min(1,Math.Min(maxWidth/bounds[index].Diagonal.X,targetHeight/bounds[index].Diagonal.Y));
            if(scale<1)Height(texts[index],nominalHeight*scale);
          }
          double rowGap=targetHeight*TableLabelRowGapFraction;
          double padding=targetHeight*TableLabelBoxPaddingFraction;
          double clearance=targetHeight*TableLabelGapFraction;
          double blockHeight=texts.Length*targetHeight+(texts.Length-1)*rowGap;
          double left=-clearance-padding-maxWidth;
          double frameBottom=width-blockHeight-2*padding;
          double bottom=frameBottom+padding;
          for(int index=texts.Length-1;index>=0;index--)
          {
            // Shared anchors keep row alignment stable when strings or inherited styles change.
            texts[index].TextHorizontalAlignment=TextHorizontalAlignment.Center;
            var textPlane=texts[index].Plane;
            textPlane.Origin=frame.PointAt((left-padding)/2,bottom+baselineOffset);
            texts[index].Plane=textPlane;
            bottom+=targetHeight+rowGap;
          }
          double frameWidth=maxWidth+2*padding;
          var labelFrame=new Rectangle3d(plane,
            new Interval(left-padding,0),new Interval(frameBottom,frameBottom+blockHeight+2*padding)).ToNurbsCurve();
          double frameTop=frameBottom+blockHeight+2*padding;
          var outline=new PolylineCurve(new[] {frame.PointAt(0,0),frame.PointAt(length,0),frame.PointAt(length,width),
            frame.PointAt(0,width),frame.PointAt(left-padding,frameTop),
            frame.PointAt(left-padding,frameBottom),frame.PointAt(0,frameBottom),frame.PointAt(0,0)});
          return new TableLabels(key,plane.Origin,texts,labelFrame,outline,frameWidth,clearance);
        }
        catch { foreach(var text in texts) text.Dispose(); throw; }
      }
      private BoundingBox LabelGlyphBounds(TextEntity text,Plane frame)
      {
        using var measurement=(TextEntity)text.Duplicate();
        var parent=_doc.DimStyles.FindId(text.DimensionStyleId)??_doc.DimStyles.Current;
        measurement.ParentDimensionStyle=parent;
        measurement.DimensionScale=AnnotationTextTransform.ResolveDisplayDimensionScale(_doc,text,_doc.Views.ActiveView?.ActiveViewport);
        using var style=measurement.GetDimensionStyle(parent);
        var resolved=style??parent;
        var key=(measurement.RichText,measurement.TextHeight,resolved.Font.FaceName,resolved.Font.Bold,resolved.Font.Italic,
          resolved.DataCRC(0),measurement.DimensionScale,measurement.Plane,frame);
        if(_labelMeasurements.TryGetValue(key,out var cached))return cached;
        // Outline curves can disagree with displayed text bounds, notably in Rhino 9.
        var bounds=measurement.GetBoundingBox(frame);
        if(!bounds.IsValid) bounds=text.GetBoundingBox(frame);
        if(!bounds.IsValid) throw new InvalidOperationException("Could not measure table label glyphs.");
        if(_labelMeasurements.Count>=LabelMeasurementCacheLimit)_labelMeasurements.Clear();
        _labelMeasurements[key]=bounds;
        return bounds;
      }
      private void ClearTableLabels()
      { foreach(var entry in _tableLabels.Values) { foreach(var text in entry.Text) text.Dispose(); entry.Frame.Dispose(); entry.Outline.Dispose(); } _tableLabels.Clear(); _labelMeasurements.Clear(); }
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
  using Rhino.Display;
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
    private const int SelectionBoxThickness = 1; // Selection rectangle border width in display pixels; positive.
    private static readonly System.Drawing.Color WindowBoxColor = System.Drawing.Color.DodgerBlue; // Outline color for left-to-right enclosure selection.
    private static readonly System.Drawing.Color CrossingBoxColor = System.Drawing.Color.LimeGreen; // Outline color for right-to-left crossing selection.

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
    private readonly SelectionBox _selectionBox=new();
    private readonly List<(Curve Curve,Plane Plane,double Area,Guid Source,Guid[] Members)> _boundaries=[];

    internal bool DefersSelectionSync=>_blankSelection!=null||PreviewGestureActive?.Invoke()==true;
    internal bool DefersRedraw=>_completingClick;
    internal Guid[]? FocusMembers=>_completingClick&&!_dragged&&_interiorSelect?_interiorMembers:null;
    internal Func<IEnumerable<Guid>>? RetainedSelection { get; set; }
    internal event Action<Guid[],bool>? PartClicked;
    internal Func<MouseCallbackEventArgs,bool>? PreviewMouseDown,PreviewMouseMove,PreviewMouseUp;
    internal Func<RhinoView,System.Drawing.Point,System.Drawing.Point,bool,bool,bool>? PreviewWindowSelection;
    internal Func<bool>? PreviewGestureActive;
    internal Func<bool>? SelectionSuppressed;
    internal Action? PreviewCancelled;
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
      RhinoApp.EscapeKeyPressed+=CancelGesture;
    }

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
      if(_canPick()&&PreviewMouseDown?.Invoke(e)==true){e.Cancel=true;base.OnMouseDown(e);return;}
      if(e.MouseButton==MouseButton.Left&&_canPick()&&SelectionSuppressed?.Invoke()==true){e.Cancel=true;base.OnMouseDown(e);return;}
      if(_blankSelection!=null&&e.MouseButton==MouseButton.Right)
      {e.Cancel=true;CancelGesture(this,EventArgs.Empty);base.OnMouseDown(e);return;}
      if(e.MouseButton!=MouseButton.Left||!_canPick()||e.View?.ActiveViewport is not {} viewport)
      { base.OnMouseDown(e); return; }
      try
      {
        var clock=System.Diagnostics.Stopwatch.StartNew();
        // Own the entire gesture: cancelling only mouse-up leaves Rhino's blank-space picker open.
        e.Cancel=true;
        _blankSelection=_doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id)
          .Concat(RetainedSelection?.Invoke()??[]).Distinct().ToArray();
        _blankPoint=e.ViewportPoint; _blankViewport=viewport.Id; _dragged=false;
        _interiorMembers=null;_interiorSelect=!e.CtrlKeyDown;
        if(!viewport.GetFrustumLine(e.ViewportPoint.X,e.ViewportPoint.Y,out var line)) return;
        using var context=new PickContext {View=e.View,PickLine=line,PickStyle=PickStyle.PointPick,PickMode=PickMode.Shaded,PickGroupsEnabled=false};
        context.SetPickTransform(viewport.GetPickTransform(e.ViewportPoint)); context.UpdateClippingPlanes();
        var references=_doc.Objects.PickObjects(context);
        RhinoObject? picked=null;
        try
        { picked=references?.Select(reference=>reference.Object()).FirstOrDefault(obj=>obj?.Geometry is Rhino.Geometry.Curve or Rhino.Geometry.AnnotationBase or Rhino.Geometry.Point or Rhino.Geometry.TextDot); }
        finally { if(references!=null) foreach(var reference in references) reference.Dispose(); }
        _interiorMembers=picked==null?InteriorMembers(line):_knownMembers(picked.Id)??FindMembers(picked);
        Log.Write("vNest","part down members={0} ctrl={1} inGet={2} elapsedMs={3:F1}",_interiorMembers?.Length??0,e.CtrlKeyDown,Rhino.Input.RhinoGet.InGet(_doc),clock.Elapsed.TotalMilliseconds);
      }
      catch(Exception ex) { Log.Write("vNest","document pick failed: {0}",ex); }
      finally { base.OnMouseDown(e); }
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      if(_canPick()&&PreviewMouseMove?.Invoke(e)==true){e.Cancel=true;base.OnMouseMove(e);return;}
      if(_blankSelection!=null&&e.View?.ActiveViewport.Id==_blankViewport)
      {
        e.Cancel=true;_dragged|=IsDrag(_blankPoint,e.ViewportPoint);
        _selectionBox.ViewportId=_blankViewport;
        _selectionBox.Rectangle=SelectionRectangle(_blankPoint,e.ViewportPoint);
        _selectionBox.Crossing=e.ViewportPoint.X<_blankPoint.X;
        _selectionBox.Enabled=_dragged;e.View.Redraw();
      }
      base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseCallbackEventArgs e)
    {
      if(PreviewMouseUp?.Invoke(e)==true){e.Cancel=true;base.OnMouseUp(e);return;}
      if(e.MouseButton==MouseButton.Left&&_canPick()&&SelectionSuppressed?.Invoke()==true){e.Cancel=true;base.OnMouseUp(e);return;}
      if(_blankSelection!=null&&e.MouseButton==MouseButton.Left&&RhinoDoc.ActiveDoc==_doc&&
        e.View?.ActiveViewport.Id==_blankViewport)
        e.Cancel=true;
      base.OnMouseUp(e);
    }

    protected override void OnEndMouseUp(MouseCallbackEventArgs e)
    {
      if(_blankSelection is {} saved)
      {
        _blankSelection=null;_selectionBox.Enabled=false;
        try
        {
          // Complete only gestures authorized on mouse-down, without starting a native GetObject loop.
          bool allowed=Enabled&&RhinoDoc.ActiveDoc==_doc&&e.View?.ActiveViewport.Id==_blankViewport;
          Log.Write("vNest","interior up allowed={0} dragged={1} members={2} button={3} inGet={4}",allowed,_dragged,_interiorMembers?.Length??0,e.MouseButton,Rhino.Input.RhinoGet.InGet(_doc));
          if(allowed)
          {
            var clock=System.Diagnostics.Stopwatch.StartNew(); _completingClick=true;
            bool dragged=_dragged||IsDrag(_blankPoint,e.ViewportPoint);
            _dragged=dragged;
            if(dragged&&e.View is {} previewView&&PreviewWindowSelection?.Invoke(previewView,_blankPoint,e.ViewportPoint,e.ShiftKeyDown,!_interiorSelect)==true)
            {base.OnEndMouseUp(e);return;}
            if(RetainPriorSelection(dragged,_interiorSelect))
              foreach(var id in saved) if(_doc.Objects.FindId(id) is {} obj&&obj.IsSelected(false)==0) obj.Select(true);
            if(!dragged)
            {
              if(_interiorMembers!=null) foreach(var id in _interiorMembers) _doc.Objects.FindId(id)?.Select(_interiorSelect);
            }
            else if(e.View is {} view)
              foreach(var id in WindowMembers(view,_blankPoint,e.ViewportPoint)) _doc.Objects.FindId(id)?.Select(_interiorSelect);
            double restored=clock.Elapsed.TotalMilliseconds;
            _changed();
            double synced=clock.Elapsed.TotalMilliseconds;
            if(!_dragged&&!IsDrag(_blankPoint,e.ViewportPoint)&&_interiorMembers!=null) PartClicked?.Invoke(_interiorMembers,_interiorSelect);
            double focused=clock.Elapsed.TotalMilliseconds;
            _doc.Views.Redraw();
            Log.Write("vNest","part click completed elapsedMs={0:F1} members={1} restoreMs={2:F1} syncMs={3:F1} focusMs={4:F1} redrawMs={5:F1}",
              clock.Elapsed.TotalMilliseconds,_interiorMembers?.Length??0,restored,synced-restored,focused-synced,clock.Elapsed.TotalMilliseconds-focused);
          }
        }
        catch(Exception ex) { Log.Write("vNest","blank-click selection restore failed: {0}",ex); }
        finally { _interiorMembers=null; _completingClick=false; }
      }
      base.OnEndMouseUp(e);
    }

    internal static System.Drawing.Rectangle SelectionRectangle(System.Drawing.Point start,System.Drawing.Point end)=>
      new(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y),Math.Max(1,Math.Abs(end.X-start.X)),Math.Max(1,Math.Abs(end.Y-start.Y)));
    internal void ShowPreviewBox(Guid viewport,System.Drawing.Point start,System.Drawing.Point end)
    {_selectionBox.ViewportId=viewport;_selectionBox.Rectangle=SelectionRectangle(start,end);_selectionBox.Crossing=end.X<start.X;_selectionBox.Enabled=IsDrag(start,end);_doc.Views.Redraw();}
    internal void HidePreviewBox(){_selectionBox.Enabled=false;_doc.Views.Redraw();}

    private Guid[] WindowMembers(RhinoView view,System.Drawing.Point start,System.Drawing.Point end)
    {
      var rectangle=SelectionRectangle(start,end);
      view.ActiveViewport.GetFrustumLine(rectangle.Left+rectangle.Width/2,rectangle.Top+rectangle.Height/2,out var ray);
      using var context=new PickContext {View=view,PickStyle=end.X<start.X?PickStyle.CrossingPick:PickStyle.WindowPick,
        PickLine=ray,PickMode=PickMode.Shaded,PickGroupsEnabled=false};
      context.SetPickTransform(view.ActiveViewport.GetPickTransform(rectangle));context.UpdateClippingPlanes();
      var references=_doc.Objects.PickObjects(context);
      try
      {
        var hits=references?.Select(reference=>reference.Object()).Where(obj=>obj?.Geometry is Curve or AnnotationBase or Rhino.Geometry.Point or TextDot)
          .Cast<RhinoObject>().ToList()??[];
        // Native picking may omit already-selected objects; Ctrl-drag must still be able to remove them.
        var settings=new ObjectEnumeratorSettings {ObjectTypeFilter=ObjectType.Curve|ObjectType.Annotation|ObjectType.Point|ObjectType.TextDot,
          NormalObjects=true,LockedObjects=false,HiddenObjects=false,ReferenceObjects=false};
        foreach(var obj in _doc.Objects.GetObjectList(settings).Where(obj=>obj.IsSelected(false)!=0))
        {
          if(!context.PickFrustumTest(obj.Geometry.GetBoundingBox(true),out bool contained))continue;
          if(contained)hits.Add(obj);
          else if(end.X<start.X&&obj.Geometry is Curve curve)
          {using var nurbs=curve.ToNurbsCurve();if(context.PickFrustumTest(nurbs,out _,out _,out _))hits.Add(obj);}
        }
        return hits.SelectMany(obj=>_knownMembers(obj.Id)??FindMembers(obj)).Distinct().ToArray();
      }
      finally {if(references!=null)foreach(var reference in references)reference.Dispose();}
    }

    private void CancelGesture(object? sender,EventArgs e)
    {
      PreviewCancelled?.Invoke();
      if(_blankSelection==null)return;
      _blankSelection=null;_interiorMembers=null;_dragged=false;_selectionBox.Enabled=false;_doc.Views.Redraw();
    }

    private sealed class SelectionBox : DisplayConduit
    {
      internal Guid ViewportId;
      internal System.Drawing.Rectangle Rectangle;
      internal bool Crossing;
      protected override void DrawForeground(DrawEventArgs e)
      {
        if(e.Viewport.Id==ViewportId)e.Display.Draw2dRectangle(Rectangle,Crossing?CrossingBoxColor:WindowBoxColor,SelectionBoxThickness,System.Drawing.Color.Transparent);
      }
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
      _blankSelection=null; _interiorMembers=null; RetainedSelection=null; PartClicked=null;PreviewMouseDown=null;PreviewMouseMove=null;PreviewMouseUp=null;PreviewWindowSelection=null;PreviewGestureActive=null;SelectionSuppressed=null;PreviewCancelled=null; Enabled=false;
      _selectionBox.Enabled=false;RhinoApp.EscapeKeyPressed-=CancelGesture;
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
    private static readonly int? DefaultShuffleRank = null; // null uses deterministic order trials only; a nonnegative rank requests one additional shuffled-order trial.
    internal const double DefaultGap = 0.0; // Minimum outline-to-outline spacing in model units; nonnegative, zero permits touching.
    internal const double DefaultTableAllowance = 0.0; // Per-used-table material overhead in model units; zero disables overhead for standalone geometry callers without a document.
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
      internal int? ShuffleRank { get; init; }=DefaultShuffleRank;
      internal bool GrainGuided { get; init; }=DefaultGrainGuided;
      internal double Gap { get; init; }=DefaultGap;
      internal double TableAllowance { get; init; }=DefaultTableAllowance;
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
      internal IReadOnlyDictionary<int,vTools.Commands.NestWorkflow.MaterialPreset> TableMaterials { get; init; }=new Dictionary<int,vTools.Commands.NestWorkflow.MaterialPreset>();
      internal double TableAllowance { get; init; }=DefaultTableAllowance;
      internal bool IsTrial { get; init; }=DefaultTrial;
      internal bool Stopped { get; init; }=DefaultStopped;
      internal SearchStatistics? Search { get; init; }
      internal ProfileStatistics? Profile { get; init; }
    }
    private readonly record struct Space(double X,double Y,double Width,double Length);

    internal static double MaterialConsumption(Result result)=>result.Placements.GroupBy(part=>part.Table)
      .Sum(table=>table.Max(part=>part.Y+part.Pose.Length)+result.TableAllowance);

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
  using System.Globalization;
  using Clipper2Lib;
  using Input = vTools.NestPackingEngine.Input;
  using Pose = vTools.NestPackingEngine.Pose;
  using Placement = vTools.NestPackingEngine.Placement;
  using Result = vTools.NestPackingEngine.Result;


  internal static partial class NestContourPackingEngine
  {
    // Polygon packing defaults and numeric/search limits
    private static readonly (string Name,int Start,int End)[] PackingStages =
      [("Preparing outlines",0,5),("Placing parts",5,50),("Compacting",50,55),("Filling gaps",55,60),
       ("Stacking similar shapes",60,68),("Repacking clusters",68,80),("Refining stacks",80,84),("Matching parts",84,88),("Searching layouts",88,95),
       ("Testing part orders",95,98),("Combining tables",98,100),("Finalizing",100,100)]; // Ordered stage names and planned percentage ranges; total progress is not a time estimate.
    private const int Precision = 6; // Decimal places retained by polygon Boolean and Minkowski operations; zero through eight.
    private const double ClearanceArcToleranceFraction = 0.001; // Maximum guide/packing arc chord error as a fraction of gap; manual clearance uses exact circular distances.
    private const double ClearanceJoinLimit = 2.0; // Clipper join parameter; round joins do not extend corner clearance beyond the requested radius.
    private const double CoordinateTolerance = 1e-6; // Model-unit roundoff accepted at table edges and coincident contacts.
    private const double ManualGapTolerance = 10 * CoordinateTolerance; // Model-unit saved-placement roundoff allowed in manual clearance only; physical overlap and table bounds stay unchanged.
    private const double ManualGapToleranceFraction = 0.01; // Maximum fraction of requested Gap allowed as drawing-tolerance slack during manual editing; never removes a positive gap entirely.
    private const double AreaTolerance = 1e-8; // Square-model-unit threshold distinguishing contact from polygon overlap.
    private const double SweepParallelTolerance = 1e-12; // Model-unit-squared determinant below which swept edges are treated as parallel.
    private const double SweepCircleRelativeTolerance = 1e-14; // Relative quadratic-discriminant roundoff discarded for tangent swept circular contacts; zero would treat cancellation noise as a collision.
    internal const double DefaultManualContactAlignment = 0.0; // Model-unit contact alignment radius; zero disables alignment for non-viewport geometry callers.
    private const int ArrowGapRecoveryRefinements = 32; // Maximum bisections locating the nearest valid reverse arrow position after a gap increase.
    private const double ArrowGapProbeFraction = 0.001; // Fraction of required gap sampled to distinguish worsening clearance from tangential motion.
    private const bool DefaultSimplifyContact = true; // true builds simplified packing contacts; false keeps exact paths for direct manual collision tests.
    private const int OrderTrials = 6; // Deterministic area/extent orders compared, independent of selection order.
    internal const double DefaultSearchSeconds = 30.0; // Default refinement seconds after the initial layout; zero disables refinement.
    private const double BeamTimeFraction = 0.95; // Refinement deadline fraction for full-layout search, leaving time for alternative orders.
    private const double PairTimeFraction = 0.85; // Refinement deadline fraction for joint pair reassignment after cluster searches.
    private const double ClusterTimeFraction = 0.75; // Refinement deadline fraction reserved for complete cluster reinsertions; zero through one.
    private const double StackTimeFraction = 0.90; // Refinement deadline fraction for fitting shape-similar stacks after general cluster searches; zero through one.
    private const double StackEarlyTimeFraction = 0.45; // Refinement deadline fraction reserved before general clusters when every eligible sparse profile belongs to one compatible family.
    private const int StackProfileSamples = 32; // Cross-sections along the long axis used to compare actual outline profiles; positive integer.
    private const double StackProfileTolerance = 0.12; // Maximum normalized RMS boundary difference for matching profiles; zero through one, without implicit mirroring.
    private const double StackSizeRatio = 1.25; // Maximum corresponding width/length ratio for shape-family membership; at least one.
    private const double StackMinimumAspect = 1.5; // Minimum long/short extent ratio for a hooked or sparse profile; at least one.
    private const double StackMaximumFill = 0.65; // Maximum outline area divided by bounding-box area for stack candidates; zero through one, excluding dense rectangles/circles.
    private const int StackEligibleLimit = 16; // Maximum movable sparse profiles considered in one bounded stacking pass.
    private const int StackMinimumMembers = 2; // Minimum similar parts in one stack; singles remain in their existing positions.
    private const int StackMaximumMembers = 6; // Maximum members per stack; larger families are split into size-balanced, interleaved banks.
    private const int StackMaximumBanks = 4; // Maximum banks simultaneously fitted against the remaining fixed/movable obstacles.
    private const int StackReferencePoses = 2; // Current and allowed opposite reference poses tested per bank; no pose is synthesized.
    private const int StackGrowthBeam = 4; // Different stagger patterns retained per allowed reference pose while growing a bank.
    private const int StackLayoutBeam = 8; // Complete-bank placement alternatives retained while fitting banks together.
    private const double StackPitchToleranceFraction = 0.02; // Fraction of the smallest positive member gap used to merge near-identical stagger trials.
    private const int StackAnchorLimit = 8192; // Maximum no-fit-boundary contact anchors considered per bank insertion; positive integer.
    private const int SmallClusterJobLimit = 10; // Movable part count at or below which the entire material job can be reconsidered together.
    private const int ClusterMinimumParts = 3; // Minimum movable members in a local removal/reinsertion neighborhood.
    private const int ClusterMaximumParts = 6; // Maximum movable members in a large-job neighborhood; small whole-job searches use SmallClusterJobLimit instead.
    private const int ClusterLimit = 12; // Maximum distinct tail/neighbor or cross-table neighborhoods per refinement pass.
    private const int ClusterBeamWidth = 32; // Maximum alternative partial cluster layouts retained; larger jobs keep their existing full-layout beam limit.
    private const int ClusterPatternAlternatives = 3; // Maximum spatial variants retained for one table/rotation/mirror combination at each cluster depth.
    private const int ClusterOrders = 2; // Ascending- and descending-area reinsertion orders, with grain-guided skinny members first.
    private const int ClusterAnchorLimit = 4; // Distinct contact locations retained per allowed pose and destination table during cluster search.
    private const double ClusterCompactLengthFactor = 4.0 / 3.0; // Positive multiplier on the cluster's per-part/area lower bound for its first compact strip trial.
    private const double AnchorWidthSeparation = 0.05; // Fraction of moving-part width distinguishing alternative contact positions; zero through one.
    private const double AnchorLengthSeparation = 0.02; // Fraction of moving-part length distinguishing alternative contact positions; zero through one.
    private const double CompactTimeFraction = 0.08; // Initial refinement fraction for individual contour-based compaction.
    private const int CompactPasses = 2; // Maximum same-pose compaction sweeps per solve; preserves fixed positions and allowed rotations.
    private const int PairAnchorAlternatives = 4; // Different table/contact locations retained for the first member of a joint pair.
    private const double CombineSeconds = 6.0; // Soft seconds budget for automatically consolidating existing tables after refinement, including when SearchTime is zero.
    private const int CombineOrders = 4; // Table-pair repacking orders: orphan-table first, area, length and shortest-first.
    private const double CombineHalfTurn = 180.0; // Degree reversal tested with each accepted pose during bounded table-pair repacking; never creates an unallowed pose.
    private const int BeamWidth = 3; // Alternative partial layouts retained while jointly choosing part poses and contacts; positive integer.
    private const int BeamOrders = 3; // Full-layout orders using area, extent and concavity; positive integer.
    private const int BeamAnchors = 3; // Distinct contact placements retained per pose during full-layout search; positive integer.
    private const int PairTrials = 48; // Maximum concavity-prioritized part pairs reconsidered per solve; positive integer.
    private const int PairPoseLimit = 8; // First-part rotations/mirrors tested in each pair order; positive integer.
    private const int CandidateLimit = 2048; // Maximum feasible anchors tested per pose and table; positive integer.
    private const int ContactVertexLimit = 96; // Maximum vertices used for optional curved contact-path simplification; exact collision paths are retained.
    private const double ContactTolerance = 0.002; // Initial model-unit simplification/clearance tolerance for dense contact outlines.
    private const double PreviewInterval = 0.3; // Minimum seconds between provisional layout snapshots; limits UI geometry rebuilding.
    internal const double MinimumSkinnyAspect = 3.0; // Minimum long/short fitting-pose aspect ratio for skinny grain-guided priority and retained-layout left alignment; at least one.
    private const double SkinnyTimeFraction = 0.2; // Maximum fraction of refinement seconds spent relocating skinny parts into existing gaps; zero through one.
    private const int SkinnyPasses = 2; // Skinny reinsertion orders attempted before general layout refinement; positive integer.
    private const int GapNeighborLimit = 2; // Distinct touching neighboring parts needed for the gap-filling score; two identifies placement between parts.
    private const bool DefaultSkinny = false; // true prioritizes fitting grain-guided strips for gap filling; false uses ordinary contour packing.
    private const double HalfTurnTimeFraction = 0.08; // Fraction of refinement seconds reserved for explicit 180-degree partner fitting after cluster search; zero through one.
    private const int HalfTurnParts = 8; // Most concave movable parts considered by the dedicated half-turn pass; positive.
    private const int HalfTurnPartners = 6; // Partners considered per allowed half-turn, prioritizing removable used length; positive.
    private const double HalfTurnAngleTolerance = 0.05; // Degree tolerance for matching an already-allowed opposite pose; much less than 180.

    internal sealed class ManualLayout
    {
      private readonly IReadOnlyDictionary<int,double> _widths;
      private readonly IReadOnlyDictionary<int,double> _gaps;
      private readonly double _length;
      private readonly IReadOnlyDictionary<int,double> _lengths;
      private readonly double _gapTolerance;
      private readonly Dictionary<int,Placed> _parts;

      internal ManualLayout(IReadOnlyList<Placement> parts,IReadOnlyDictionary<int,double> widths,double length,IReadOnlyDictionary<int,double> gaps)
        :this(parts,widths,length,gaps,new Dictionary<int,double>()) { }
      internal ManualLayout(IReadOnlyList<Placement> parts,IReadOnlyDictionary<int,double> widths,double length,IReadOnlyDictionary<int,double> gaps,IReadOnlyDictionary<int,double> lengths)
        :this(parts,widths,length,gaps,lengths,ManualGapTolerance) { }
      internal ManualLayout(IReadOnlyList<Placement> parts,IReadOnlyDictionary<int,double> widths,double length,IReadOnlyDictionary<int,double> gaps,IReadOnlyDictionary<int,double> lengths,double gapTolerance)
      {
        NestPackingDependency.EnsurePolygonsLoaded();
        _widths=widths;_gaps=gaps;_length=length;_lengths=lengths;
        _gapTolerance=double.IsFinite(gapTolerance)&&gapTolerance>0?Math.Max(CoordinateTolerance,gapTolerance):ManualGapTolerance;
        _parts=parts.ToDictionary(part=>part.Id,Make);
      }
      private Placed Make(Placement part)
      {
        var source=CreateShapeCore(part.Id,part.Pose,false)??throw new ArgumentException("Invalid part outline");
        double requested=_gaps.GetValueOrDefault(part.Id);
        double gap=Math.Max(0,requested-Math.Min(_gapTolerance,requested*ManualGapToleranceFraction));
        var shape=new Shape(source.Id,source.Pose,source.Path,source.Path,source.Area) {Gap=gap};
        return new Placed(shape,part.Table,part.X,part.Y);
      }
      internal bool Valid(IReadOnlyList<Placement> changes)
        =>ValidCore(changes,true);
      internal bool ValidRigid(IReadOnlyList<Placement> changes)
        =>ValidCore(changes,false);
      private bool ValidCore(IReadOnlyList<Placement> changes,bool internalGaps)
      {
        var moved=changes.Select(Make).ToArray();var ids=changes.Select(part=>part.Id).ToHashSet();
        for(int i=0;i<moved.Length;i++)
        {
          var part=moved[i];
          if(!_widths.TryGetValue(part.Table,out double width)||!Inside(part.Shape,part.X,part.Y,width,_lengths.GetValueOrDefault(part.Table,_length)))return false;
          if(_parts.Values.Where(other=>other.Table==part.Table&&!ids.Contains(other.Shape.Id)).Any(other=>Collides(part,other)))return false;
          if(internalGaps&&moved.Take(i).Any(other=>other.Table==part.Table&&Collides(part,other)))return false;
        }
        return true;
      }
      internal Placement[] Move(IReadOnlySet<int> ids,double dx,double dy)
      {
        if(!double.IsFinite(dx)||!double.IsFinite(dy))return [];
        var moving=_parts.Values.Where(part=>ids.Contains(part.Shape.Id)).ToArray();
        double limit=1;
        foreach(var part in moving)
        {
          double width=_widths[part.Table];
          if(dx>0)limit=Math.Min(limit,(width-part.Shape.Pose.Width-part.X)/dx);
          else if(dx<0)limit=Math.Min(limit,-part.X/dx);
          if(dy>0)limit=Math.Min(limit,(_lengths.GetValueOrDefault(part.Table,_length)-part.Shape.Pose.Length-part.Y)/dy);
          else if(dy<0)limit=Math.Min(limit,-part.Y/dy);
        }
        limit=Math.Clamp(limit,0,1);
        foreach(var part in moving)
        foreach(var other in _parts.Values.Where(other=>other.Table==part.Table&&!ids.Contains(other.Shape.Id)))
        {
          limit=PairTravel(WithGap(part,0),WithGap(other,0),dx,dy,limit);
          limit=GapTravel(part,other,dx,dy,limit);
        }
        return moving.Select(part=>new Placement(part.Shape.Id,part.Table,part.X+dx*limit,part.Y+dy*limit,part.Shape.Pose)).ToArray();
      }
      internal Placement[] MoveToContact(IReadOnlySet<int> ids,double dx,double dy)
      {
        var requested=Move(ids,dx,dy);
        if(requested.Length==0)return requested;
        var moving=_parts.Values.Where(part=>ids.Contains(part.Shape.Id)).ToArray();
        if(ValidRigid(requested))return requested;
        double distance=Math.Sqrt(dx*dx+dy*dy);
        if(!double.IsFinite(distance)||distance<=CoordinateTolerance)return requested;
        bool worsening=false;
        foreach(var part in moving)
        foreach(var other in _parts.Values.Where(other=>other.Table==part.Table&&!ids.Contains(other.Shape.Id)))
        {
          if(!Collides(part,other)||Collides(WithGap(part,0),WithGap(other,0)))continue;
          double probe=Math.Min(distance,Math.Max(CoordinateTolerance/ArrowGapProbeFraction,Math.Max(part.Shape.Gap,other.Shape.Gap)*ArrowGapProbeFraction));
          var next=new Placed(part.Shape,part.Table,part.X+dx/distance*probe,part.Y+dy/distance*probe);
          if(Collides(WithGap(next,0),WithGap(other,0))||GapTravel(part,other,dx/distance*probe,dy/distance*probe,1)<1)worsening=true;
        }
        if(!worsening)return requested;
        var escape=Move(ids,-dx,-dy);
        if(!ValidRigid(escape))return requested;
        // The swept move already excludes new obstacles; stop at its first restored clearance, not the opposite table edge.
        double blocked=0,valid=1;
        Placement[] Between(double fraction)=>escape.Select(part=>part with
          {X=_parts[part.Id].X+(part.X-_parts[part.Id].X)*fraction,Y=_parts[part.Id].Y+(part.Y-_parts[part.Id].Y)*fraction}).ToArray();
        for(int step=0;step<ArrowGapRecoveryRefinements&&(valid-blocked)*distance>CoordinateTolerance;step++)
        {
          double middle=(blocked+valid)/2;
          if(ValidRigid(Between(middle)))valid=middle;else blocked=middle;
        }
        return Between(valid);
      }
      private static Placed WithGap(Placed part,double gap)=>new(part.Shape with {Gap=gap,Clearance=null},part.Table,part.X,part.Y);
      private static double GapTravel(Placed part,Placed other,double dx,double dy,double limit)
      {
        double gap=Math.Max(part.Shape.Gap,other.Shape.Gap);
        if(gap<=CoordinateTolerance||limit<=0)return limit;
        var contacts=NearbyEdges(part,other,dx*limit,dy*limit,gap).GroupBy(pair=>pair.Fixed)
          .Select(group=>(Edge:group.Key,Moving:group.Select(pair=>pair.Moving).ToArray())).ToArray();
        var caps=new Dictionary<PointD,double>();
        foreach(var contact in contacts)
        {
          double strip=Math.Min(gap,contact.Moving.Min(edge=>StripDistance(edge,contact.Edge)));
          foreach(var vertex in new[]{contact.Edge.A,contact.Edge.B})
          {
            // A short edge clearance must continue around its endpoints while sliding along that same edge.
            double radius=Math.Min(strip,Math.Sqrt(contact.Moving.Min(edge=>PointEdgeDistanceSquared(vertex,edge))));
            caps[vertex]=Math.Min(caps.GetValueOrDefault(vertex,gap),radius);
          }
        }
        foreach(var contact in contacts)
        {
          var stationary=contact.Edge;var moving=contact.Moving;
          // Other edge interiors keep their own clearance; a short right-side gap cannot waive a bottom edge.
          // Apply roundoff to the requested gap once, never subtract it repeatedly from an existing contact.
          double radius=Math.Min(gap-CoordinateTolerance/2,moving.Min(edge=>StripDistance(edge,stationary)));
          if(radius>CoordinateTolerance)
            foreach(var edge in moving)limit=StripTravel(edge,stationary,dx,dy,radius,limit);
          foreach(var vertex in new[]{stationary.A,stationary.B})
          {
            radius=Math.Min(gap-CoordinateTolerance/2,caps[vertex]);
            if(radius>CoordinateTolerance)
              foreach(var edge in moving)limit=CapsuleTravel(vertex,edge,-dx,-dy,radius,limit);
          }
        }
        return limit;
      }
      private static double StripDistance(BoundaryEdge moving,BoundaryEdge edge)
      {
        double ex=edge.B.x-edge.A.x,ey=edge.B.y-edge.A.y,length=Math.Sqrt(ex*ex+ey*ey);
        if(length<=CoordinateTolerance)return double.PositiveInfinity;
        double ax=moving.A.x-edge.A.x,ay=moving.A.y-edge.A.y,bx=moving.B.x-edge.A.x,by=moving.B.y-edge.A.y;
        double start=(ax*ex+ay*ey)/length,end=(bx*ex+by*ey)/length,delta=end-start;
        double first=0,last=1;
        if(Math.Abs(delta)<=SweepParallelTolerance)
        {if(start<0||start>length)return double.PositiveInfinity;}
        else
        {double a=-start/delta,b=(length-start)/delta;first=Math.Max(0,Math.Min(a,b));last=Math.Min(1,Math.Max(a,b));if(first>last)return double.PositiveInfinity;}
        double normal=(ax*ey-ay*ex)/length,change=((bx-ax)*ey-(by-ay)*ex)/length;
        double left=normal+change*first,right=normal+change*last;
        return left*right<=0?0:Math.Min(Math.Abs(left),Math.Abs(right));
      }
      private static double StripTravel(BoundaryEdge moving,BoundaryEdge edge,double dx,double dy,double radius,double limit)
      {
        double ex=edge.B.x-edge.A.x,ey=edge.B.y-edge.A.y,length=Math.Sqrt(ex*ex+ey*ey);
        if(length<=CoordinateTolerance)return limit;
        double nx=-ey/length*radius,ny=ex/length*radius;
        var corners=new[]{new PointD(edge.A.x+nx,edge.A.y+ny),new PointD(edge.B.x+nx,edge.B.y+ny),
          new PointD(edge.B.x-nx,edge.B.y-ny),new PointD(edge.A.x-nx,edge.A.y-ny)};
        double entry=0,exit=limit;
        bool Axis(double x,double y)
        {
          double norm=Math.Sqrt(x*x+y*y);if(norm<=SweepParallelTolerance)return true;x/=norm;y/=norm;
          double a=moving.A.x*x+moving.A.y*y,b=moving.B.x*x+moving.B.y*y;
          double min=Math.Min(a,b),max=Math.Max(a,b),lo=double.PositiveInfinity,hi=double.NegativeInfinity;
          foreach(var corner in corners){double value=corner.x*x+corner.y*y;lo=Math.Min(lo,value);hi=Math.Max(hi,value);}
          double speed=dx*x+dy*y;
          if(Math.Abs(speed)<=SweepParallelTolerance)return max>lo+SweepParallelTolerance&&min<hi-SweepParallelTolerance;
          double first=(lo-max)/speed,last=(hi-min)/speed;
          entry=Math.Max(entry,Math.Min(first,last));exit=Math.Min(exit,Math.Max(first,last));return entry<exit;
        }
        if(Axis(ex,ey)&&Axis(-ey,ex)&&Axis(moving.A.y-moving.B.y,moving.B.x-moving.A.x)&&exit>0)return Math.Min(limit,Math.Max(0,entry));
        return limit;
      }
      private static double CapsuleTravel(PointD point,BoundaryEdge edge,double dx,double dy,double radius,double limit)
      {
        double ex=edge.B.x-edge.A.x,ey=edge.B.y-edge.A.y,length=Math.Sqrt(ex*ex+ey*ey);
        double speed=dx*dx+dy*dy;
        if(speed<=SweepParallelTolerance)return limit;
        void Circle(PointD center)
        {
          double x=point.x-center.x,y=point.y-center.y,dot=x*dx+y*dy;
          double offset=x*x+y*y-radius*radius;
          double discriminant=dot*dot-speed*offset;
          if(discriminant<=SweepCircleRelativeTolerance*(dot*dot+speed*(x*x+y*y+radius*radius)))return;
          double root=Math.Sqrt(discriminant),entry=(-dot-root)/speed,exit=(-dot+root)/speed;
          if(exit>0&&entry<limit)limit=Math.Min(limit,Math.Max(0,entry));
        }
        Circle(edge.A);Circle(edge.B);
        if(length<=CoordinateTolerance)return limit;
        double entry=0,exit=limit;
        bool Slab(double position,double direction,double min,double max)
        {
          if(Math.Abs(direction)<=SweepParallelTolerance)return position>min+SweepParallelTolerance&&position<max-SweepParallelTolerance;
          double first=(min-position)/direction,last=(max-position)/direction;
          entry=Math.Max(entry,Math.Min(first,last));exit=Math.Min(exit,Math.Max(first,last));return entry<exit;
        }
        double px=point.x-edge.A.x,py=point.y-edge.A.y;
        if(Slab((px*ex+py*ey)/length,(dx*ex+dy*ey)/length,0,length)&&
          Slab((px*ey-py*ex)/length,(dx*ey-dy*ex)/length,-radius,radius)&&exit>0)
          limit=Math.Min(limit,Math.Max(0,entry));
        return limit;
      }
      private static double PairTravel(Placed part,Placed other,double dx,double dy,double limit)
      {
          double gap=Math.Max(part.Shape.Gap,other.Shape.Gap);
          if(Math.Max(part.X,part.X+dx*limit)+part.Shape.Pose.Width+gap<other.X||
            Math.Min(part.X,part.X+dx*limit)>other.X+other.Shape.Pose.Width+gap||
            Math.Max(part.Y,part.Y+dy*limit)+part.Shape.Pose.Length+gap<other.Y||
            Math.Min(part.Y,part.Y+dy*limit)>other.Y+other.Shape.Pose.Length+gap)return limit;
          var contacts=new List<double>{0,limit};
          // Include extent contacts as well as polygon contacts in the physical-overlap sweep.
          double entry=0,exit=limit;
          bool Slab(double position,double direction,double min,double max)
          {
            if(direction==0)return position>=min&&position<=max;
            double first=(min-position)/direction,last=(max-position)/direction;
            entry=Math.Max(entry,Math.Min(first,last));exit=Math.Min(exit,Math.Max(first,last));return entry<=exit;
          }
          if(!Slab(part.X,dx,other.X-part.Shape.Pose.Width-gap,other.X+other.Shape.Pose.Width+gap)||
            !Slab(part.Y,dy,other.Y-part.Shape.Pose.Length-gap,other.Y+other.Shape.Pose.Length+gap))return limit;
          contacts.Add(Math.Clamp(entry,0,limit));contacts.Add(Math.Clamp(exit,0,limit));
          // Translation contacts occur when either polygon's vertex reaches the other's edge.
          // Sweep the same exact clearance paths as Collides, without constructing a no-fit polygon.
          var movingPaths=part.Shape.Gap>=other.Shape.Gap?part.WorldClearance:new PathsD{part.WorldPath};
          var stationary=part.Shape.Gap>=other.Shape.Gap?new PathsD{other.WorldPath}:other.WorldClearance;
          foreach(var path in movingPaths)
          foreach(var obstacle in stationary)
          {
            SweepContacts(path,obstacle,dx,dy,limit,contacts);
            SweepContacts(obstacle,path,-dx,-dy,limit,contacts);
          }
          var ordered=contacts.Distinct().Order().ToArray();
          for(int index=1;index<ordered.Length;index++)
          {
            double middle=(ordered[index-1]+ordered[index])/2;
            bool collision=Collides(new Placed(part.Shape,part.Table,part.X+dx*middle,part.Y+dy*middle),other);
            if(collision)
            {limit=Math.Min(limit,ordered[index-1]);break;}
          }
          return limit;
      }
      private static void SweepContacts(PathD vertices,PathD edges,double dx,double dy,double limit,List<double> contacts)
      {
        bool xAxis=Math.Abs(dx)<Math.Abs(dy);
        var ordered=Enumerable.Range(0,edges.Count).Select(index=>
        {
          var a=edges[index];var b=edges[(index+1)%edges.Count];
          return (A:a,B:b,Min:Math.Min(xAxis?a.x:a.y,xAxis?b.x:b.y),Max:Math.Max(xAxis?a.x:a.y,xAxis?b.x:b.y));
        }).OrderBy(edge=>edge.Min).ToArray();
        var prefixMax=new double[ordered.Length];double maximum=double.NegativeInfinity;
        for(int index=0;index<ordered.Length;index++)prefixMax[index]=maximum=Math.Max(maximum,ordered[index].Max);
        foreach(var point in vertices)
        {
          double minX=Math.Min(point.x,point.x+dx*limit)-CoordinateTolerance,maxX=Math.Max(point.x,point.x+dx*limit)+CoordinateTolerance;
          double minY=Math.Min(point.y,point.y+dy*limit)-CoordinateTolerance,maxY=Math.Max(point.y,point.y+dy*limit)+CoordinateTolerance;
          double min=xAxis?minX:minY,max=xAxis?maxX:maxY;
          int low=0,high=ordered.Length;
          while(low<high){int middle=(low+high)/2;if(prefixMax[middle]<min)low=middle+1;else high=middle;}
          for(int index=low;index<ordered.Length&&ordered[index].Min<=max;index++)
          {
            var a=ordered[index].A;var b=ordered[index].B;
            if(Math.Min(a.x,b.x)>maxX||Math.Max(a.x,b.x)<minX||Math.Min(a.y,b.y)>maxY||Math.Max(a.y,b.y)<minY)continue;
            double ex=b.x-a.x,ey=b.y-a.y,px=a.x-point.x,py=a.y-point.y,cross=dx*ey-dy*ex;
            if(Math.Abs(cross)<=SweepParallelTolerance)continue;
            double t=(px*ey-py*ex)/cross,u=(px*dy-py*dx)/cross;
            if(t>=-CoordinateTolerance&&t<=limit+CoordinateTolerance&&u>=-CoordinateTolerance&&u<=1+CoordinateTolerance)contacts.Add(Math.Clamp(t,0,limit));
          }
        }
      }
      internal Placement[] MoveFrom(IReadOnlyList<Placement> current,double dx,double dy)
        =>MoveFromAligned(current,dx,dy,DefaultManualContactAlignment);
      internal Placement[] MoveFromAligned(IReadOnlyList<Placement> current,double dx,double dy,double alignment)
      {
        foreach(var part in current)
          _parts[part.Id]=new Placed(_parts[part.Id].Shape,part.Table,part.X,part.Y);
        var ids=current.Select(part=>part.Id).ToHashSet();
        if(Math.Abs(dx)<=CoordinateTolerance)dx=0;
        if(Math.Abs(dy)<=CoordinateTolerance)dy=0;
        var moved=Move(ids,dx,dy);
        if(moved.Length==0)return moved;
        var start=current.First(part=>part.Id==moved[0].Id);
        double remainingX=dx-(moved[0].X-start.X),remainingY=dy-(moved[0].Y-start.Y);
        if(Math.Abs(remainingX)<=CoordinateTolerance&&Math.Abs(remainingY)<=CoordinateTolerance)return moved;
        try
        {
          if(dx!=0&&dy!=0)
          {
            foreach(var part in moved)_parts[part.Id]=new Placed(_parts[part.Id].Shape,part.Table,part.X,part.Y);
            // Ordinary sliding stays on the requested axes; exact contact alignment below is limited to a screen-pixel radius.
            var x=Move(ids,remainingX,0);var y=Move(ids,0,remainingY);
            double Progress(Placement[] result)=>(result[0].X-moved[0].X)*remainingX+(result[0].Y-moved[0].Y)*remainingY;
            moved=Progress(x)>=Progress(y)?x:y;
          }
          return AlignContact(moved,start,dx,dy,alignment);
        }
        finally
        {
          foreach(var part in current)_parts[part.Id]=new Placed(_parts[part.Id].Shape,part.Table,part.X,part.Y);
        }
      }
      private Placement[] AlignContact(Placement[] stopped,Placement origin,double dx,double dy,double alignment)
      {
        if(!double.IsFinite(alignment)||alignment<=CoordinateTolerance)return stopped;
        bool alongY=Math.Abs(dy)>=Math.Abs(dx);
        double targetX=origin.X+dx,targetY=origin.Y+dy;
        double remainder=alongY?targetY-stopped[0].Y:targetX-stopped[0].X;
        if(Math.Abs(remainder)<=CoordinateTolerance)return stopped;
        var ids=stopped.Select(part=>part.Id).ToHashSet();
        void Restore(Placement[] parts)
        {foreach(var part in parts)_parts[part.Id]=new Placed(_parts[part.Id].Shape,part.Table,part.X,part.Y);}
        double Score(Placement[] parts)=>(parts[0].X-targetX)*(parts[0].X-targetX)+(parts[0].Y-targetY)*(parts[0].Y-targetY);
        var shifts=new List<double>();
        foreach(var part in stopped)
        foreach(var other in _parts.Values.Where(other=>other.Table==part.Table&&!ids.Contains(other.Shape.Id)))
        {
          double gap=Math.Max(_parts[part.Id].Shape.Gap,other.Shape.Gap);
          if(alongY)
          {shifts.Add(other.X-part.Pose.Width-gap-part.X);shifts.Add(other.X+other.Shape.Pose.Width+gap-part.X);}
          else
          {shifts.Add(other.Y-part.Pose.Length-gap-part.Y);shifts.Add(other.Y+other.Shape.Pose.Length+gap-part.Y);}
        }
        var best=stopped;double bestScore=Score(best);
        foreach(double shift in shifts.Where(shift=>Math.Abs(shift)>CoordinateTolerance&&Math.Abs(shift)<=alignment).Distinct().OrderBy(Math.Abs))
        {
          Restore(stopped);var aligned=Move(ids,alongY?shift:0,alongY?0:shift);
          if(aligned.Length==0||Math.Abs((alongY?aligned[0].X-stopped[0].X:aligned[0].Y-stopped[0].Y)-shift)>CoordinateTolerance)continue;
          Restore(aligned);var candidate=Move(ids,alongY?0:targetX-aligned[0].X,alongY?targetY-aligned[0].Y:0);
          double advance=alongY?candidate[0].Y-stopped[0].Y:candidate[0].X-stopped[0].X;
          double error=alongY?candidate[0].X-targetX:candidate[0].Y-targetY;
          double score=Score(candidate);
          if(advance*remainder<=CoordinateTolerance*CoordinateTolerance||Math.Abs(error)>alignment||score>=bestScore-CoordinateTolerance*CoordinateTolerance||!ValidRigid(candidate))continue;
          best=candidate;bestScore=score;
        }
        return best;
      }
      internal Placement[]? Relax(IReadOnlyList<Placement> desired)
      {
        if(Valid(desired))return desired.ToArray();
        var ids=desired.Select(part=>part.Id).ToHashSet();var accepted=new List<Placed>();var result=new List<Placement>();
        foreach(var part in desired)
        {
          var moving=Make(part);double maxX=_widths[part.Table]-part.Pose.Width,maxY=_lengths.GetValueOrDefault(part.Table,_length)-part.Pose.Length;
          if(maxX<-CoordinateTolerance||maxY<-CoordinateTolerance)return null;
          maxX=Math.Max(0,maxX);maxY=Math.Max(0,maxY);
          var obstacles=_parts.Values.Where(other=>other.Table==part.Table&&!ids.Contains(other.Shape.Id)).Concat(accepted.Where(other=>other.Table==part.Table)).ToArray();
          var candidates=new List<PointD>{new(Math.Clamp(part.X,0,maxX),Math.Clamp(part.Y,0,maxY))};
          var forbidden=new PathsD();
          foreach(var other in obstacles)
          {
            var paths=Minkowski.Diff(moving.Shape.Path,other.Shape.Path,true,Precision);double gap=Math.Max(moving.Shape.Gap,other.Shape.Gap);
            if(gap>0)paths=RoundClearance(paths,gap);
            forbidden.AddRange(paths.Select(path=>NestContourPackingEngine.Move(path,other.X,other.Y)));
          }
          var bounds=new PathsD{new(){new(0,0),new(maxX,0),new(maxX,maxY),new(0,maxY)}};
          var allowed=maxX>CoordinateTolerance&&maxY>CoordinateTolerance?Clipper.Difference(bounds,forbidden,FillRule.NonZero,Precision):forbidden;
          foreach(var path in allowed)
          for(int index=0;index<path.Count;index++)
          {
            var a=path[index];var b=path[(index+1)%path.Count];double x=b.x-a.x,y=b.y-a.y,length=x*x+y*y;
            double fraction=length<=Rhino.RhinoMath.ZeroTolerance?0:Math.Clamp(((part.X-a.x)*x+(part.Y-a.y)*y)/length,0,1);
            candidates.Add(new(Math.Clamp(a.x+fraction*x,0,maxX),Math.Clamp(a.y+fraction*y,0,maxY)));
            candidates.Add(new(Math.Clamp(a.x,0,maxX),Math.Clamp(a.y,0,maxY)));
          }
          var nearest=candidates.OrderBy(point=>(point.x-part.X)*(point.x-part.X)+(point.y-part.Y)*(point.y-part.Y))
            .Select(point=>new Placed(moving.Shape,part.Table,point.x,point.y)).FirstOrDefault(candidate=>!obstacles.Any(other=>Collides(candidate,other)));
          if(nearest==null)return null;
          accepted.Add(nearest);result.Add(part with {X=nearest.X,Y=nearest.Y});
        }
        return result.ToArray();
      }
      internal Placement[]? RelaxRigid(IReadOnlyList<Placement> desired)
      {
        if(desired.Count==0)return [];
        if(desired.Count==1)return Relax(desired);
        if(ValidRigid(desired))return desired.ToArray();
        var moving=desired.Select(Make).ToArray();var ids=desired.Select(part=>part.Id).ToHashSet();
        double minX=moving.Max(part=>-part.X),minY=moving.Max(part=>-part.Y);
        double maxX=moving.Min(part=>_widths[part.Table]-part.Shape.Pose.Width-part.X);
        double maxY=moving.Min(part=>_lengths.GetValueOrDefault(part.Table,_length)-part.Shape.Pose.Length-part.Y);
        if(minX>maxX+CoordinateTolerance||minY>maxY+CoordinateTolerance)return null;
        maxX=Math.Max(minX,maxX);maxY=Math.Max(minY,maxY);
        var candidates=new List<PointD>{new(Math.Clamp(0,minX,maxX),Math.Clamp(0,minY,maxY))};var forbidden=new PathsD();
        foreach(var part in moving)
        foreach(var other in _parts.Values.Where(other=>other.Table==part.Table&&!ids.Contains(other.Shape.Id)))
        {
          var paths=Minkowski.Diff(part.Shape.Path,other.Shape.Path,true,Precision);double gap=Math.Max(part.Shape.Gap,other.Shape.Gap);
          if(gap>0)paths=RoundClearance(paths,gap);
          forbidden.AddRange(paths.Select(path=>NestContourPackingEngine.Move(path,other.X-part.X,other.Y-part.Y)));
        }
        var bounds=new PathsD{new(){new(minX,minY),new(maxX,minY),new(maxX,maxY),new(minX,maxY)}};
        var allowed=maxX-minX>CoordinateTolerance&&maxY-minY>CoordinateTolerance?Clipper.Difference(bounds,forbidden,FillRule.NonZero,Precision):forbidden;
        foreach(var path in allowed)
        for(int index=0;index<path.Count;index++)
        {
          var a=path[index];var b=path[(index+1)%path.Count];double x=b.x-a.x,y=b.y-a.y,length=x*x+y*y;
          double fraction=length<=SweepParallelTolerance?0:Math.Clamp(-(a.x*x+a.y*y)/length,0,1);
          candidates.Add(new(Math.Clamp(a.x+fraction*x,minX,maxX),Math.Clamp(a.y+fraction*y,minY,maxY)));
          candidates.Add(new(Math.Clamp(a.x,minX,maxX),Math.Clamp(a.y,minY,maxY)));
        }
        foreach(var point in candidates.OrderBy(point=>point.x*point.x+point.y*point.y))
        {
          var shifted=desired.Select(part=>part with {X=part.X+point.x,Y=part.Y+point.y}).ToArray();
          if(ValidRigid(shifted))return shifted;
        }
        return null;
      }
    }

    private sealed record Shape(int Id,Pose Pose,PathD Path,PathD Contact,double Area)
    {
      internal bool Skinny { get; init; }=DefaultSkinny;
      internal double Gap { get; init; }=NestPackingEngine.DefaultGap;
      internal double TableAllowance { get; init; }=NestPackingEngine.DefaultTableAllowance;
      internal PathsD? Clearance { get; init; }
      internal SolveMetrics? Metrics { get; init; }
      internal string GeometryKey { get; }=ContactKey(Contact);
    }
    private readonly record struct BoundaryEdge(PointD A,PointD B)
    {
      internal double MinX=>Math.Min(A.x,B.x);
      internal double MaxX=>Math.Max(A.x,B.x);
      internal double MinY=>Math.Min(A.y,B.y);
      internal double MaxY=>Math.Max(A.y,B.y);
    }
    private static double PointEdgeDistanceSquared(PointD point,BoundaryEdge edge)
    {
      double x=edge.B.x-edge.A.x,y=edge.B.y-edge.A.y,length=x*x+y*y;
      double t=length<=SweepParallelTolerance?0:Math.Clamp(((point.x-edge.A.x)*x+(point.y-edge.A.y)*y)/length,0,1);
      double dx=point.x-edge.A.x-t*x,dy=point.y-edge.A.y-t*y;
      return dx*dx+dy*dy;
    }
    private static double EdgeDistanceSquared(BoundaryEdge first,BoundaryEdge second)=>
      Math.Min(Math.Min(PointEdgeDistanceSquared(first.A,second),PointEdgeDistanceSquared(first.B,second)),
        Math.Min(PointEdgeDistanceSquared(second.A,first),PointEdgeDistanceSquared(second.B,first)));
    private static IEnumerable<(BoundaryEdge Moving,BoundaryEdge Fixed)> NearbyEdges(Placed moving,Placed other,double dx,double dy,double gap)
    {
      var edges=other.Edges;var maxima=other.EdgeMaxima;
      foreach(var edge in moving.Edges)
      {
        double minX=edge.MinX+Math.Min(0,dx)-gap,maxX=edge.MaxX+Math.Max(0,dx)+gap;
        double minY=edge.MinY+Math.Min(0,dy)-gap,maxY=edge.MaxY+Math.Max(0,dy)+gap;
        int low=0,high=edges.Length;
        while(low<high){int middle=(low+high)/2;if(maxima[middle]<minX)low=middle+1;else high=middle;}
        for(int index=low;index<edges.Length&&edges[index].MinX<=maxX;index++)
          if(edges[index].MaxX>=minX&&edges[index].MinY<=maxY&&edges[index].MaxY>=minY)yield return (edge,edges[index]);
      }
    }
    private static PathsD RoundClearance(PathsD paths,double gap)=>Clipper.InflatePaths(paths,gap,JoinType.Round,EndType.Polygon,
      ClearanceJoinLimit,Precision,Math.Max(CoordinateTolerance,gap*ClearanceArcToleranceFraction));
    private sealed class Placed(Shape shape,int table,double x,double y)
    {
      internal Shape Shape { get; }=shape;
      internal int Table { get; }=table;
      internal double X { get; }=x;
      internal double Y { get; }=y;
      private PathD? _path;
      private PathsD? _clearance;
      private BoundaryEdge[]? _edges;
      private double[]? _edgeMaxima;
      internal BoundaryEdge[] Edges=>_edges??=WorldPath.Select((point,index)=>new BoundaryEdge(point,WorldPath[(index+1)%WorldPath.Count])).OrderBy(edge=>edge.MinX).ToArray();
      internal double[] EdgeMaxima
      {
        get
        {
          if(_edgeMaxima!=null)return _edgeMaxima;
          _edgeMaxima=new double[Edges.Length];double max=double.NegativeInfinity;
          for(int index=0;index<Edges.Length;index++)_edgeMaxima[index]=max=Math.Max(max,Edges[index].MaxX);
          return _edgeMaxima;
        }
      }
      internal PathD WorldPath=>_path??=Move(Shape.Path,X,Y);
      internal PathsD WorldClearance=>_clearance??=Shape.Clearance is {} paths
        ?new PathsD(paths.Select(path=>Move(path,X,Y))):Shape.Gap>0
          ?RoundClearance(new PathsD {WorldPath},Shape.Gap):new PathsD {WorldPath};
    }
    private sealed record Plan(List<Placed> Parts,int Tables,Dictionary<int,string> Errors);
    private sealed class SearchState
    {
      internal int Tests,Layouts,Improvements,HalfTurns;
      internal Action<int>? StageProgress;
    }

    internal static Result Solve(IReadOnlyList<Input> inputs,double width,double length)
      =>SolveWithProgress(inputs,width,length,null,CancellationToken.None);

    internal static Result SolveWithProgress(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,CancellationToken cancellationToken)
      =>SolveInteractive(inputs,width,length,progress,null,cancellationToken);

    internal static IReadOnlyList<IReadOnlyList<NestPackingEngine.ContourPoint>> ClearanceContours(IReadOnlyList<NestPackingEngine.ContourPoint> contour,double gap)
    {
      if(gap<=0||contour.Count<3)return [];
      NestPackingDependency.EnsurePolygonsLoaded();return ClearanceContoursLoaded(contour,gap);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<IReadOnlyList<NestPackingEngine.ContourPoint>> ClearanceContoursLoaded(IReadOnlyList<NestPackingEngine.ContourPoint> contour,double gap)
    {
      var path=new PathD(contour.Select(point=>new PointD(point.X,point.Y)));
      if(Clipper.Area(path)<0)path.Reverse();
      return RoundClearance(new PathsD {path},gap)
        .Select(offset=>(IReadOnlyList<NestPackingEngine.ContourPoint>)offset.Select(point=>new NestPackingEngine.ContourPoint(point.x,point.y)).ToArray()).ToArray();
    }

    internal static Result SolveInteractive(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,CancellationToken cancellationToken)
      =>SolveWithSearch(inputs,width,length,progress,preview,cancellationToken,DefaultSearchSeconds);

    internal static Result SolveWithSearch(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,CancellationToken cancellationToken,double searchSeconds)
    {
      NestPackingDependency.EnsurePolygonsLoaded();
      return SolveLoaded(inputs,width,length,progress,preview,cancellationToken,Math.Max(0,double.IsFinite(searchSeconds)?searchSeconds:DefaultSearchSeconds));
    }

    internal static Result SolveUntilStopped(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,
      CancellationToken cancellationToken,double searchSeconds,Action<Result>? accepted)
      =>SolveUntilStoppedWithActivity(inputs,width,length,progress,preview,cancellationToken,searchSeconds,accepted,null);
    internal static Result SolveUntilStoppedWithActivity(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,
      CancellationToken cancellationToken,double searchSeconds,Action<Result>? accepted,Action<string>? activity)
      =>SolveUntilStoppedWithStages(inputs,width,length,progress,preview,cancellationToken,searchSeconds,accepted,activity,null);
    internal static Result SolveUntilStoppedWithStages(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,
      CancellationToken cancellationToken,double searchSeconds,Action<Result>? accepted,Action<string>? activity,Action<string,int,int>? stages)
    {
      NestPackingDependency.EnsurePolygonsLoaded();
      var kept=new Result([],0,[]);
      void Keep(Result result) { kept=result; accepted?.Invoke(result); }
      try
      { return SolveLoaded(inputs,width,length,progress,preview,cancellationToken,Math.Max(0,double.IsFinite(searchSeconds)?searchSeconds:DefaultSearchSeconds),Keep,activity,stages); }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
        var placed=kept.Placements.Select(part=>part.Id).ToHashSet(); var errors=new Dictionary<int,string>(kept.Errors);
        foreach(var input in inputs.Where(input=>!placed.Contains(input.Id))) errors.TryAdd(input.Id,"Not placed (stopped)");
        return kept with {Stopped=true,IsTrial=false,Errors=errors};
      }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Result SolveLoaded(IReadOnlyList<Input> inputs,double width,double length,Action<int>? progress,Action<Result>? preview,CancellationToken cancellationToken,double searchSeconds,Action<Result>? accepted=null,Action<string>? activity=null,Action<string,int,int>? stages=null)
    {
      int currentStage=-1,stagePercent=-1;
      void StageProgress(int value)
      {
        value=Math.Clamp(value,0,100); if(value<=stagePercent)return; stagePercent=value;
        var stage=PackingStages[currentStage]; stages?.Invoke(stage.Name,value,stage.Start+(stage.End-stage.Start)*value/100);
      }
      void Stage(int index)
      {
        if(currentStage>=0)StageProgress(100);
        currentStage=index;stagePercent=-1;activity?.Invoke(PackingStages[index].Name);StageProgress(0);
      }
      Stage(0);
      var reportProgress=progress; int previousProgress=0;
      progress=reportProgress==null?null:value=> { previousProgress=Math.Max(previousProgress,Math.Clamp(value,0,100)); reportProgress(previousProgress); };
      cancellationToken.ThrowIfCancellationRequested(); progress?.Invoke(0);
      if(!double.IsFinite(width)||!double.IsFinite(length)||width<=0||length<=0)
        return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Invalid table size"));
      if(inputs.Any(input=>!double.IsFinite(input.Gap)||input.Gap<0))
        return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Invalid part gap"));
      if(inputs.Any(input=>!double.IsFinite(input.TableAllowance)||input.TableAllowance<0))
        return new Result([],0,inputs.ToDictionary(input=>input.Id,_=>"Invalid table allowance"));
      var metrics=new SolveMetrics();
      int prepared=0;
      var shapes=inputs.ToDictionary(input=>input.Id,input=>
      {
        bool skinny=!input.Fixed&&SkinnyPriority(input,width,length)>0;
        var options=input.Poses.Select(pose=>
        { cancellationToken.ThrowIfCancellationRequested(); var shape=CreateShape(input.Id,pose); return shape==null?null:shape with
          {Skinny=skinny,Gap=input.Gap,TableAllowance=input.TableAllowance,Metrics=metrics}; })
          .Where(shape=>shape!=null).Cast<Shape>().ToArray();
        StageProgress(100*++prepared/Math.Max(1,inputs.Count)); return options;
      });
      var cache=new Dictionary<(Shape Moving,Shape Fixed),PathsD>(new PairIdentityComparer());
      var clock=Stopwatch.StartNew(); Plan? best=null;
      var state=new SearchState {StageProgress=StageProgress}; double initialSeconds=0;
      var refinement=new Stopwatch();
      Result Report(Plan plan,bool trial=false)=>Snapshot(plan) with {IsTrial=false,Search=new(state.Tests,state.Layouts,state.Improvements,initialSeconds>0?initialSeconds:clock.Elapsed.TotalSeconds,refinement.Elapsed.TotalSeconds,searchSeconds>0&&refinement.Elapsed.TotalSeconds>=searchSeconds) {HalfTurnTests=state.HalfTurns,ConsumedLength=Used(plan)},Profile=metrics.Snapshot()};
      double lastPreview=-PreviewInterval;
      void Preview(Plan plan,bool force=false,bool trial=false)
      {
        if(trial||preview==null||!force&&clock.Elapsed.TotalSeconds-lastPreview<PreviewInterval) return;
        lastPreview=clock.Elapsed.TotalSeconds;
        preview(Report(plan,trial));
      }
      Stage(1);
      best=Pack(inputs,shapes,width,length,0,cache,value=> {progress?.Invoke(value);StageProgress(value*2);},partial=> { accepted?.Invoke(Report(partial)); Preview(partial); },cancellationToken);
      initialSeconds=clock.Elapsed.TotalSeconds; state.Layouts++;
      Preview(best,true);
      accepted?.Invoke(Report(best));
      refinement.Start();
      if(searchSeconds>0)
      {
        Stage(2);
        best=Compact(best,inputs,width,length,cache,refinement,searchSeconds,state,
          plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },cancellationToken);
        Stage(3);
        best=FillSkinnyGaps(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },cancellationToken);
        Stage(4);
        best=ImproveStacksCore(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },cancellationToken,true);
        Stage(5);
        best=ImproveClusters(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },cancellationToken);
        Stage(6);
        best=ImproveStacks(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },cancellationToken);
        Stage(7);
        best=ImproveHalfTurns(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          (plan,trial)=>Preview(plan,false,trial),plan=>accepted?.Invoke(Report(plan)),cancellationToken);
        best=ImprovePairs(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },progress,cancellationToken);
        Stage(8);
        best=SearchLayouts(best,inputs,shapes,width,length,cache,refinement,searchSeconds,state,
          (plan,trial)=>Preview(plan,false,trial),()=>false,plan=> { accepted?.Invoke(Report(plan)); Preview(plan,true); },progress,cancellationToken);
      }
      Stage(9);
      int orderTrials=OrderTrials+(inputs.Any(input=>input.ShuffleRank.HasValue)?1:0);
      for(int trial=1;trial<orderTrials;trial++)
      {
        if(searchSeconds<=0||refinement.Elapsed.TotalSeconds>=searchSeconds) break;
        cancellationToken.ThrowIfCancellationRequested();
        var plan=Pack(inputs,shapes,width,length,trial,cache,null,null,cancellationToken,
          ()=>refinement.Elapsed.TotalSeconds>=searchSeconds);
        if(plan.Parts.Count+plan.Errors.Count!=inputs.Count) break;
        state.Layouts++;
        if(Better(plan,best)) { best=plan; state.Improvements++; accepted?.Invoke(Report(best)); }
        Preview(best);
        progress?.Invoke(98+2*trial/(orderTrials-1));
        StageProgress(100*trial/(orderTrials-1));
      }
      best=CompactTableOrigins(best,inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet(),width);
      Stage(10);
      best=CombineTables(best,inputs,shapes,width,length,cache,state,
        plan=> {accepted?.Invoke(Report(plan));Preview(plan,true);},cancellationToken);
      cancellationToken.ThrowIfCancellationRequested(); Stage(11); StageProgress(100); progress?.Invoke(100);
      accepted?.Invoke(Report(best));
      Preview(best,true);
      return Report(best!);
    }

    private static Result Snapshot(Plan plan)=>new(plan.Parts.Select(part=>new Placement(part.Shape.Id,part.Table,part.X,part.Y,part.Shape.Pose)).ToList(),plan.Tables,new(plan.Errors))
      {TableAllowance=plan.Parts.Select(part=>part.Shape.TableAllowance).DefaultIfEmpty(NestPackingEngine.DefaultTableAllowance).Max()};

    private static Plan CombineTables(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,SearchState state,Action<Plan> preview,CancellationToken token)
    {
      var clock=Stopwatch.StartNew(); bool Expired()=>clock.Elapsed.TotalSeconds>=CombineSeconds;
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      while(best.Tables>1&&!Expired())
      {
        bool combined=false; double budget=Used(best);
        // Do cheap complete singleton merges before a whole-table NFP trial can consume the time budget.
        foreach(var source in SkinnyFirst(best.Parts.GroupBy(part=>part.Table).Where(table=>table.Count()==1&&!fixedIds.Contains(table.First().Shape.Id)),table=>table.First().Shape.Skinny).ToArray())
        {
          var moving=source.First();var kept=best.Parts.Where(part=>part.Table!=source.Key).ToList();
          foreach(var destination in kept.GroupBy(part=>part.Table).ToArray())
          {
            token.ThrowIfCancellationRequested();
            double gap=Math.Max(moving.Shape.Gap,destination.Max(part=>part.Shape.Gap));
            bool prepend=moving.Shape.Skinny&&!destination.Any(part=>fixedIds.Contains(part.Shape.Id));
            double shift=prepend?moving.Shape.Pose.Length+gap:0;
            double y=prepend?0:destination.Max(part=>part.Y+part.Shape.Pose.Length)+gap;
            double x=width-moving.Shape.Pose.Width;
            if(!Inside(moving.Shape,x,y,width,length))continue;
            var shifted=new Placed(moving.Shape,destination.Key,x,y);
            var relocated=destination.Select(part=>new Placed(part.Shape,part.Table,part.X,part.Y+shift)).ToArray();
            if(relocated.Any(part=>!Inside(part.Shape,part.X,part.Y,width,length)||Collides(shifted,part)))continue;
            var merged=kept.Where(part=>part.Table!=destination.Key).Concat(relocated).Append(shifted).ToList();
            var candidate=CompactTableOrigins(new Plan(merged,best.Tables,new(best.Errors)),fixedIds,width);
            state.Tests++;state.Layouts++;
            if(candidate.Tables>=best.Tables||Used(candidate)>budget+CoordinateTolerance)continue;
            best=candidate;state.Improvements++;preview(best);combined=true;break;
          }
          if(combined)break;
        }
        if(combined)continue;
        var candidates=best.Parts.GroupBy(part=>part.Table).Where(table=>!table.Any(part=>fixedIds.Contains(part.Shape.Id)))
          .OrderBy(table=>table.Max(part=>part.Y+part.Shape.Pose.Length)).ThenBy(table=>table.Count()).Select(table=>table.Key).ToArray();
        // Reconsider both tables' positions, not just whether one fits around the other's fixed layout.
        for(int first=0;first<candidates.Length&&!combined&&!Expired();first++)
        for(int second=first+1;second<candidates.Length&&!combined&&!Expired();second++)
        {
          int a=candidates[first],b=candidates[second],target=Math.Min(a,b);
          var moving=best.Parts.Where(part=>part.Table==a||part.Table==b).ToArray();
          double limit=Math.Min(length,Used(new Plan(moving.ToList(),best.Tables,[]))-moving.Max(part=>part.Shape.TableAllowance));
          var options=moving.ToDictionary(part=>part.Shape.Id,part=>
          {
            double difference(Shape shape)=>Math.Abs(Math.Abs(Math.IEEERemainder(shape.Pose.Rotation-part.Shape.Pose.Rotation,360))-CombineHalfTurn);
            var reversed=shapes[part.Shape.Id].Where(shape=>shape.Pose.Flipped==part.Shape.Pose.Flipped).OrderBy(difference).FirstOrDefault();
            return reversed!=null&&difference(reversed)<=CoordinateTolerance?[part.Shape,reversed]:new[]{part.Shape};
          });
          for(int order=0;order<CombineOrders&&!Expired();order++)
          {
            var trial=new List<Placed>();
            var sorted=SkinnyFirst(moving,part=>part.Shape.Skinny).ThenBy(part=>order==0?part.Table==a?0:1:order==1?-part.Shape.Area:order==2?-part.Shape.Pose.Length:part.Shape.Pose.Length)
              .ThenBy(part=>order==0?-part.Shape.Area:0).ThenBy(part=>part.Shape.Id);
            foreach(var part in sorted)
            {
              var placed=FindPlacement(options[part.Shape.Id],trial,width,length,best.Tables,cache,token,Expired,new Dictionary<int,double>{{target,limit}});
              state.Tests++;
              if(placed==null)break;
              trial.Add(placed);
            }
            if(trial.Count!=moving.Length)continue;
            trial.AddRange(best.Parts.Where(part=>part.Table!=a&&part.Table!=b));
            var candidate=CompactTableOrigins(new Plan(trial,best.Tables,new(best.Errors)),fixedIds,width);state.Layouts++;
            if(candidate.Tables>=best.Tables||Used(candidate)>budget+CoordinateTolerance)continue;
            best=candidate;state.Improvements++;preview(best);combined=true;break;
          }
        }
        if(combined)continue;
        int examined=0;
        foreach(int table in candidates)
        {
          state.StageProgress?.Invoke(100*examined++/Math.Max(1,candidates.Length));
          if(Expired()) break; token.ThrowIfCancellationRequested();
          var moving=best.Parts.Where(part=>part.Table==table).ToArray();
          var kept=best.Parts.Where(part=>part.Table!=table).ToList();
          var limits=kept.Select(part=>part.Table).Distinct().ToDictionary(id=>id,_=>length);
          if(limits.Count==0) continue;
          for(int order=0;order<CombineOrders&&!Expired();order++)
          {
            var trial=new List<Placed>(kept); bool fitted=true;
            var sorted=SkinnyFirst(moving,part=>part.Shape.Skinny).ThenBy(part=>order==0?-part.Shape.Area:order==1?-part.Shape.Pose.Length:part.Shape.Pose.Length).ThenBy(part=>part.Shape.Id);
            foreach(var part in sorted)
            {
              var replacement=FindPlacement(shapes[part.Shape.Id],trial,width,length,best.Tables,cache,token,Expired,limits);
              if(replacement==null) {fitted=false;break;}
              trial.Add(replacement); state.Tests++;
              if(Used(new Plan(trial,best.Tables,[]))>budget+CoordinateTolerance) {fitted=false;break;}
            }
            if(!fitted) continue;
            var candidate=CompactTableOrigins(new Plan(trial,best.Tables,new(best.Errors)),fixedIds,width); state.Layouts++;
            if(candidate.Tables>=best.Tables||Used(candidate)>budget+CoordinateTolerance) continue;
            best=candidate; state.Improvements++; preview(best); combined=true; break;
          }
          if(combined) break;
        }
        if(!combined) break;
      }
      return best;
    }

    private static Plan CompactTableOrigins(Plan plan,IReadOnlySet<int> fixedIds,double width)
    {
      var parts=new List<Placed>();
      var tableIds=plan.Parts.Select(part=>part.Table).Distinct().Order().ToArray();
      var remap=tableIds.Select((table,index)=>(table,index)).ToDictionary(item=>item.table,item=>fixedIds.Count>0?item.table:item.index);
      foreach(var group in plan.Parts.GroupBy(part=>part.Table))
      {
        bool pinned=group.Any(part=>fixedIds.Contains(part.Shape.Id));
        double dy=pinned?0:group.Min(part=>part.Y);
        double dx=pinned||dy<=CoordinateTolerance&&group.Any(part=>part.Shape.Skinny)?0:
          Math.Max(0,width-group.Max(part=>part.X+part.Shape.Pose.Width));
        foreach(var part in group) parts.Add(new Placed(part.Shape,fixedIds.Count==0?remap[part.Table]:part.Table,part.X+dx,part.Y-dy));
      }
      return new Plan(parts,parts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(plan.Errors));
    }

    private static Plan Compact(Plan best,IReadOnlyList<Input> inputs,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> preview,CancellationToken token)
    {
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      var aligned=CompactTableOrigins(best,fixedIds,width);
      if(Better(aligned,best)) { best=aligned; state.Improvements++; preview(best); }
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds*CompactTimeFraction;
      for(int pass=0;pass<CompactPasses&&!Expired();pass++)
      {
        var ids=SkinnyFirst(best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)),part=>part.Shape.Skinny).ThenByDescending(part=>part.Y+part.Shape.Pose.Length).Select(part=>part.Shape.Id).ToArray();
        bool improved=false;
        int examined=0;
        foreach(int id in ids)
        {
          state.StageProgress?.Invoke(100*(pass*ids.Length+examined++)/Math.Max(1,CompactPasses*ids.Length));
          if(Expired()) break;
          token.ThrowIfCancellationRequested();
          var current=best.Parts.First(part=>part.Shape.Id==id);
          var others=best.Parts.Where(part=>part.Shape.Id!=id).ToList();
          var replacement=FindPlacement([current.Shape],others,width,length,best.Tables,cache,token,Expired,new Dictionary<int,double>{{current.Table,length}});
          if(replacement==null) continue;
          others.Add(replacement); state.Tests++;
          var candidate=CompactTableOrigins(new Plan(others,best.Tables,new(best.Errors)),fixedIds,width);
          if(Better(candidate,best)) { best=candidate; state.Improvements++; improved=true; preview(best); }
        }
        if(!improved) break;
      }
      return best;
    }

    internal static double SkinnyGrainRatio(Input input,double width,double length)=>!input.GrainGuided?0:
      input.Poses.Where(pose=>double.IsFinite(pose.Width)&&double.IsFinite(pose.Length)&&pose.Width>0&&pose.Length>0&&pose.Width<=width+CoordinateTolerance&&pose.Length<=length+CoordinateTolerance)
        .Select(pose=>Math.Max(pose.Width,pose.Length)/Math.Min(pose.Width,pose.Length)).DefaultIfEmpty(0).Max();

    private static double SkinnyPriority(Input input,double width,double length)
    { double ratio=SkinnyGrainRatio(input,width,length); return ratio>=MinimumSkinnyAspect?ratio:0; }

    internal static IOrderedEnumerable<T> SkinnyFirst<T>(IEnumerable<T> items,Func<T,bool> skinny)=>items.OrderByDescending(skinny);

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
        int examined=0;
        foreach(var input in sorted.ThenBy(input=>input.Id))
        {
          state.StageProgress?.Invoke(100*(pass*skinny.Length+examined++)/Math.Max(1,SkinnyPasses*skinny.Length));
          if(Expired()) break;
          token.ThrowIfCancellationRequested(); state.Tests++;
          var placement=FindPlacement(shapes[input.Id],parts,width,length,best.Tables,cache,token,Expired,limits,true);
          if(placement==null) break;
          parts.Add(placement);
        }
        if(parts.Count!=best.Parts.Count) continue;
        var candidate=new Plan(parts,parts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors));
        state.Layouts++;
        if(Used(candidate)>=Used(best)-CoordinateTolerance) continue;
        if(Better(candidate,best))
        { best=candidate; state.Improvements++; preview(best); }
      }
      return best;
    }

    private static Plan ImproveClusters(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> accepted,CancellationToken token)
    {
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      var movable=best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)).ToArray();
      if(movable.Length<ClusterMinimumParts)return best;
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds*ClusterTimeFraction;
      var neighborhoods=new List<int[]>();var seen=new HashSet<string>();
      void Add(IEnumerable<Placed> members)
      {
        var ids=members.Select(part=>part.Shape.Id).Distinct().Order().ToArray();
        if(ids.Length>=ClusterMinimumParts&&seen.Add(string.Join(",",ids)))neighborhoods.Add(ids);
      }
      if(movable.Length<=SmallClusterJobLimit)Add(movable);
      else
      {
        double Tail(Placed part)=>best.Parts.Where(other=>other.Table==part.Table).Max(other=>other.Y+other.Shape.Pose.Length)-
          best.Parts.Where(other=>other.Table==part.Table&&other.Shape.Id!=part.Shape.Id).Select(other=>other.Y+other.Shape.Pose.Length).DefaultIfEmpty(0).Max();
        foreach(var seed in SkinnyFirst(movable,part=>part.Shape.Skinny).ThenByDescending(Tail).ThenByDescending(part=>part.Y+part.Shape.Pose.Length))
        {
          double Distance(Placed other)=>Math.Abs(other.X+other.Shape.Pose.Width/2-seed.X-seed.Shape.Pose.Width/2)/width+
            Math.Abs(other.Y+other.Shape.Pose.Length/2-seed.Y-seed.Shape.Pose.Length/2)/length;
          var nearby=movable.Where(part=>part.Table==seed.Table).OrderBy(Distance).ToArray();
          Add(nearby.Take(ClusterMaximumParts));
          var partners=movable.Where(part=>part.Table!=seed.Table).OrderByDescending(Tail).ThenBy(Distance).Take(ClusterMinimumParts).ToArray();
          if(partners.Length>0)Add(nearby.Take(ClusterMaximumParts-partners.Length).Concat(partners));
          if(neighborhoods.Count>=ClusterLimit)break;
        }
      }
      int examined=0;
      foreach(var ids in neighborhoods.Take(ClusterLimit))
      {
        if(Expired())break;token.ThrowIfCancellationRequested();
        var members=best.Parts.Where(part=>ids.Contains(part.Shape.Id)).ToArray();
        var others=best.Parts.Where(part=>!ids.Contains(part.Shape.Id)).ToList();
        var options=members.ToDictionary(part=>part.Shape.Id,part=>PairPoses(part.Shape,shapes[part.Shape.Id]).Where(shape=>Inside(shape,0,0,width,length)).ToArray());
        if(options.Values.Any(poses=>poses.Length==0))continue;
        double lower=Math.Max(members.Max(part=>options[part.Shape.Id].Min(shape=>shape.Pose.Length)),members.Sum(part=>part.Shape.Area)/width);
        double previousEnd=members.GroupBy(part=>part.Table).Max(table=>table.Max(part=>part.Y+part.Shape.Pose.Length));
        var limits=new[]{Math.Min(length,Math.Min(previousEnd,lower*ClusterCompactLengthFactor)),Math.Min(length,previousEnd)}.Distinct().ToArray();
        Log.Write("vNest","cluster search members={0} currentMaterial={1:G17} compactLength={2:G17}",string.Join(",",ids),Used(best),limits[0]);
        // Trials may temporarily rank worse, but only a complete validated improvement replaces the saved best.
        for(int order=0;order<ClusterOrders&&!Expired();order++)
        foreach(double limit in limits)
        {
          if(Expired())break;
          var sorted=SkinnyFirst(members,part=>part.Shape.Skinny).ThenBy(part=>order==0?part.Shape.Area:-part.Shape.Area).ThenBy(part=>part.Shape.Id).ToArray();
          var beams=new List<Plan>{new(others,others.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors))};
          var tables=best.Parts.Select(part=>part.Table).Distinct().Order().ToArray();
          for(int depth=0;depth<sorted.Length&&!Expired();depth++)
          {
            var next=new List<Plan>();int id=sorted[depth].Shape.Id;
            foreach(var beam in beams)
            {
              if(Expired())break;
              foreach(var placement in ClusterChoices(options[id],beam.Parts,tables,width,length,limit,cache,token,Expired,state))
              {
                var parts=new List<Placed>(beam.Parts){placement};
                var candidate=new Plan(parts,parts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors));
                if(depth==sorted.Length-1)
                {
                  state.Layouts++;
                  if(Better(candidate,best)&&ValidCluster(candidate,best,fixedIds,width,length,token))
                  {
                    Log.Write("vNest","cluster improvement members={0} materialBefore={1:G17} materialAfter={2:G17}",string.Join(",",ids),Used(best),Used(candidate));
                    best=candidate;state.Improvements++;accepted(best);
                  }
                }
                else next.Add(candidate);
              }
            }
            if(depth==sorted.Length-1||next.Count==0)break;
            string Pattern(Plan plan)=>string.Join(";",plan.Parts.Skip(others.Count).Select(part=>
              string.Create(CultureInfo.InvariantCulture,$"{part.Shape.Id}:{part.Table}:{part.Shape.Pose.Rotation:R}:{part.Shape.Pose.Flipped}")));
            beams=next.OrderBy(Used).ThenBy(plan=>plan.Tables).ThenBy(SkinnyLeft).GroupBy(Pattern)
              .SelectMany(group=>group.Take(ClusterPatternAlternatives)).OrderBy(Used).ThenBy(plan=>plan.Tables).ThenBy(SkinnyLeft).Take(ClusterBeamWidth).ToList();
            state.StageProgress?.Invoke(100*(examined*SmallClusterJobLimit+depth+1)/Math.Max(1,neighborhoods.Count*SmallClusterJobLimit));
          }
        }
        examined++;state.StageProgress?.Invoke(100*examined/Math.Max(1,neighborhoods.Count));
      }
      return best;
    }

    private sealed record StackBank(List<Placed> Parts,double Width,double Length);

    private static StackBank NormalizeStack(IEnumerable<Placed> source)
    {
      var parts=source.ToList();double x=parts.Min(part=>part.X),y=parts.Min(part=>part.Y);
      double width=parts.Max(part=>part.X+part.Shape.Pose.Width)-x,length=parts.Max(part=>part.Y+part.Shape.Pose.Length)-y;
      return new(parts.Select(part=>new Placed(part.Shape,part.Table,part.X-x,part.Y-y)).ToList(),width,length);
    }

    private static double[] StackProfile(Shape shape)
    {
      bool alongY=shape.Pose.Length>=shape.Pose.Width;
      double across=alongY?shape.Pose.Width:shape.Pose.Length,along=alongY?shape.Pose.Length:shape.Pose.Width;
      var profile=new double[StackProfileSamples*2];
      for(int sample=0;sample<StackProfileSamples;sample++)
      {
        double cut=along*(sample+0.5)/StackProfileSamples,min=double.PositiveInfinity,max=double.NegativeInfinity;
        for(int index=0;index<shape.Path.Count;index++)
        {
          var a=shape.Path[index];var b=shape.Path[(index+1)%shape.Path.Count];
          double ay=alongY?a.y:a.x,by=alongY?b.y:b.x;
          if(!(ay<=cut&&by>cut||by<=cut&&ay>cut))continue;
          double ax=alongY?a.x:a.y,bx=alongY?b.x:b.y,x=(ax+(cut-ay)*(bx-ax)/(by-ay))/across;
          min=Math.Min(min,x);max=Math.Max(max,x);
        }
        profile[sample*2]=double.IsFinite(min)?min:0;profile[sample*2+1]=double.IsFinite(max)?max:0;
      }
      return profile;
    }

    private static double StackHead(Shape shape)
    {
      double twiceArea=0,moment=0;bool alongY=shape.Pose.Length>=shape.Pose.Width;
      for(int i=0;i<shape.Path.Count;i++)
      {
        var a=shape.Path[i];var b=shape.Path[(i+1)%shape.Path.Count];double cross=a.x*b.y-b.x*a.y;
        twiceArea+=cross;moment+=((alongY?a.y:a.x)+(alongY?b.y:b.x))*cross;
      }
      return Math.Abs(twiceArea)<=AreaTolerance?0:moment/(3*twiceArea)/(alongY?shape.Pose.Length:shape.Pose.Width)-0.5;
    }

    private static Plan ImproveStacks(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> accepted,CancellationToken token)
      =>ImproveStacksCore(best,inputs,shapes,width,length,cache,clock,searchSeconds,state,accepted,token,false);

    private static Plan ImproveStacksCore(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> accepted,CancellationToken token,bool early)
    {
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds*(early?StackEarlyTimeFraction:StackTimeFraction);
      if(Expired())return best;
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      bool Eligible(Shape shape)=>Inside(shape,0,0,width,length)&&shape.Area<=StackMaximumFill*shape.Pose.Width*shape.Pose.Length&&
        Math.Max(shape.Pose.Width,shape.Pose.Length)>=StackMinimumAspect*Math.Min(shape.Pose.Width,shape.Pose.Length);
      var eligible=SkinnyFirst(best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)&&shapes[part.Shape.Id].Any(Eligible)),part=>part.Shape.Skinny)
        .ThenByDescending(part=>shapes[part.Shape.Id].Where(Eligible).Max(shape=>Math.Max(shape.Pose.Width,shape.Pose.Length))).ThenBy(part=>part.Shape.Id)
        .Take(StackEligibleLimit).ToArray();
      if(eligible.Length<StackMinimumMembers)return best;
      var options=eligible.ToDictionary(part=>part.Shape.Id,part=>PairPoses(part.Shape,shapes[part.Shape.Id]).Where(Eligible).ToArray());
      var profiles=new Dictionary<Shape,double[]>();
      double Similarity(Shape first,Shape second)
      {
        var a=first.Pose;var b=second.Pose;
        if((a.Length>=a.Width)!=(b.Length>=b.Width)||Math.Max(a.Width,b.Width)>StackSizeRatio*Math.Min(a.Width,b.Width)||
          Math.Max(a.Length,b.Length)>StackSizeRatio*Math.Min(a.Length,b.Length))return double.PositiveInfinity;
        if(!profiles.TryGetValue(first,out var p))profiles.Add(first,p=StackProfile(first));
        if(!profiles.TryGetValue(second,out var q))profiles.Add(second,q=StackProfile(second));
        double sum=0;for(int i=0;i<p.Length;i++)sum+=(p[i]-q[i])*(p[i]-q[i]);return Math.Sqrt(sum/p.Length);
      }
      var remaining=eligible.Where(part=>options[part.Shape.Id].Length>0).Select(part=>part.Shape.Id).ToList();
      var families=new List<int[]>();
      while(remaining.Count>0&&families.Count<StackMaximumBanks&&!Expired())
      {
        token.ThrowIfCancellationRequested();int seed=remaining[0];var reference=options[seed][0];
        var family=remaining.Where(id=>options[id].Min(shape=>Similarity(reference,shape))<=StackProfileTolerance).ToArray();
        if(early&&family.Length!=remaining.Count)return best;
        remaining.RemoveAll(family.Contains);
        int count=(family.Length+StackMaximumMembers-1)/StackMaximumMembers;
        for(int bank=0;bank<count&&families.Count<StackMaximumBanks;bank++)
        {
          var members=family.Where((_,index)=>index%count==bank).ToArray();
          if(members.Length>=StackMinimumMembers)families.Add(members);
        }
      }
      if(families.Count==0)return best;
      double pitchTolerance=Math.Max(ContactTolerance,eligible.Select(part=>part.Shape.Gap).Where(gap=>gap>0).DefaultIfEmpty(0).Min()*StackPitchToleranceFraction);
      string Pattern(IEnumerable<Placed> parts)=>string.Join(";",parts.Select(part=>string.Create(CultureInfo.InvariantCulture,
        $"{part.Shape.Id}:{part.Table}:{part.Shape.Pose.Rotation:R}:{part.Shape.Pose.Flipped}:{Math.Round(part.X/pitchTolerance)}:{Math.Round(part.Y/pitchTolerance)}")));
      var banks=new List<List<StackBank>>();
      foreach(var family in families)
      {
        if(Expired())return best;token.ThrowIfCancellationRequested();var variants=new List<StackBank>();
        foreach(var reference in options[family[0]].Take(StackReferencePoses))
        {
          var aligned=family.Select(id=>options[id].MinBy(shape=>Similarity(reference,shape))!).ToArray();
          if(aligned.Any(shape=>Similarity(reference,shape)>StackProfileTolerance))continue;
          aligned=aligned.OrderByDescending(shape=>Math.Max(shape.Pose.Width,shape.Pose.Length)).ThenBy(shape=>shape.Id).ToArray();
          var growing=new List<StackBank>{new([new(aligned[0],0,0,0)],aligned[0].Pose.Width,aligned[0].Pose.Length)};
          foreach(var shape in aligned.Skip(1))
          {
            var next=new List<StackBank>();
            foreach(var bank in growing)
            foreach(var point in StackAnchors([new(shape,0,0,0)],bank.Parts,null,cache,token,Expired))
            {
              if(Expired())return best;state.Tests++;var member=new Placed(shape,0,point.x,point.y);
              if(bank.Parts.Any(other=>Collides(member,other)))continue;
              var candidate=NormalizeStack(bank.Parts.Append(member));
              if(candidate.Width<=width+CoordinateTolerance&&candidate.Length<=length+CoordinateTolerance)next.Add(candidate);
            }
            growing=next.OrderBy(bank=>bank.Width*bank.Length).ThenBy(bank=>bank.Length).DistinctBy(bank=>Pattern(bank.Parts)).Take(StackGrowthBeam).ToList();
            if(growing.Count==0)break;
          }
          variants.AddRange(growing);
        }
        if(variants.Count==0)return best;
        Log.Write("vNest","similar-profile stack members={0} variants={1} shortestLength={2:G17}",string.Join(",",family),variants.Count,variants.Min(bank=>bank.Length));
        banks.Add(variants);
        state.StageProgress?.Invoke(50*banks.Count/families.Count);
      }
      var selected=families.SelectMany(ids=>ids).ToHashSet();
      var outsiders=best.Parts.Where(part=>!selected.Contains(part.Shape.Id)).ToList();
      double lower=outsiders.Count==0?banks.Max(variants=>variants.Min(bank=>bank.Length))+best.Parts.Max(part=>part.Shape.TableAllowance):double.NegativeInfinity;
      if(Used(best)<=lower+CoordinateTolerance)return best;
      var beams=new List<Plan>{new(outsiders,outsiders.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors))};
      var tables=best.Parts.Select(part=>part.Table).Distinct().Order().ToArray();
      for(int index=0;index<banks.Count&&!Expired();index++)
      {
        var next=new List<Plan>();
        foreach(var beam in beams)
        foreach(var bank in banks[index].OrderBy(bank=>
          bank.Parts.Average(part=>StackHead(part.Shape))*beam.Parts.Where(part=>selected.Contains(part.Shape.Id)).Select(part=>StackHead(part.Shape)).DefaultIfEmpty(0).Average()).ThenBy(bank=>bank.Length))
        foreach(int table in tables)
        {
          if(Expired())return best;token.ThrowIfCancellationRequested();
          var occupied=beam.Parts.Where(part=>part.Table==table).ToList();
          bool floating=!outsiders.Any(part=>part.Table==table);
          var extent=occupied.Count>0?NormalizeStack(occupied):null;
          var region=(MinX:floating&&extent!=null?extent.Width-width:0,MaxX:width-bank.Width,
            MinY:floating&&extent!=null?extent.Length-length:0,MaxY:length-bank.Length);
          if(region.MinX>region.MaxX||region.MinY>region.MaxY)continue;
          Plan? complete=null;
          var anchors=StackAnchors(bank.Parts,occupied,region,cache,token,Expired)
            .Where(point=>point.x>=region.MinX-CoordinateTolerance&&point.x<=region.MaxX+CoordinateTolerance&&
              point.y>=region.MinY-CoordinateTolerance&&point.y<=region.MaxY+CoordinateTolerance)
            .OrderBy(point=>floating?Math.Max(extent?.Length??0,point.y+bank.Length)-Math.Min(0,point.y):
              Math.Max(occupied.Select(part=>part.Y+part.Shape.Pose.Length).DefaultIfEmpty(0).Max(),point.y+bank.Length)).ToArray();
          foreach(var point in anchors)
          {
            if(Expired())break;state.Tests++;
            if(point.x<region.MinX-CoordinateTolerance||point.x>region.MaxX+CoordinateTolerance||
              point.y<region.MinY-CoordinateTolerance||point.y>region.MaxY+CoordinateTolerance)continue;
            double x=Math.Clamp(point.x,region.MinX,region.MaxX),y=Math.Clamp(point.y,region.MinY,region.MaxY);
            var moved=bank.Parts.Select(part=>new Placed(part.Shape,table,part.X+x,part.Y+y)).ToList();
            if(moved.Any(part=>occupied.Any(other=>Collides(part,other))))continue;
            var local=occupied.Concat(moved).ToList();var placed=floating?NormalizeStack(local):new StackBank(local,
              local.Max(part=>part.X+part.Shape.Pose.Width),local.Max(part=>part.Y+part.Shape.Pose.Length));
            if(placed.Width>width+CoordinateTolerance||placed.Length>length+CoordinateTolerance)continue;
            var all=beam.Parts.Where(part=>part.Table!=table).Concat(placed.Parts).ToList();
            var candidate=new Plan(all,all.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors));
            if(index==banks.Count-1)
            {
              state.Layouts++;
              if(complete==null||Better(candidate,complete))complete=candidate;
              if(Used(complete)<=lower+CoordinateTolerance)break;
            }
            else next.Add(candidate);
          }
          if(complete!=null&&Better(complete,best)&&ValidCluster(complete,best,fixedIds,width,length,token))
          {
            Log.Write("vNest","stack improvement members={0} materialBefore={1:G17} materialAfter={2:G17}",string.Join(",",selected.Order()),Used(best),Used(complete));
            best=complete;state.Improvements++;accepted(best);
            if(Used(best)<=lower+CoordinateTolerance)return best;
          }
        }
        beams=next.OrderBy(Used).ThenBy(plan=>plan.Tables).ThenBy(SkinnyLeft).DistinctBy(plan=>Pattern(plan.Parts)).Take(StackLayoutBeam).ToList();
        state.StageProgress?.Invoke(50+50*(index+1)/banks.Count);
        if(beams.Count==0)break;
      }
      return best;
    }

    private static IEnumerable<PointD> StackAnchors(IReadOnlyList<Placed> moving,IReadOnlyList<Placed> occupied,
      (double MinX,double MaxX,double MinY,double MaxY)? region,Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken token,Func<bool> stop)
    {
      var candidates=new List<PointD>{new(0,0)};
      if(region is {} box)candidates.AddRange([new(box.MinX,box.MinY),new(box.MaxX,box.MinY),new(box.MinX,box.MaxY),new(box.MaxX,box.MaxY)]);
      if(occupied.Count>0)
      {
        var forbidden=new PathsD();
        foreach(var member in moving)
        foreach(var other in occupied)
        {
          if(stop())yield break;token.ThrowIfCancellationRequested();var key=(member.Shape,other.Shape);
          if(!cache.TryGetValue(key,out var paths))
          {paths=PairGeometry(member.Shape,other.Shape,Math.Max(member.Shape.Gap,other.Shape.Gap));cache.Add(key,paths);}
          foreach(var path in paths)forbidden.Add(Move(path,other.X-member.X,other.Y-member.Y));
        }
        if(stop())yield break;token.ThrowIfCancellationRequested();
        var union=Clipper.Union(forbidden,new PathsD(),FillRule.NonZero,Precision);
        foreach(var path in union)
        for(int index=0;index<path.Count;index++)
        {
          var a=path[index];var b=path[(index+1)%path.Count];candidates.Add(a);
          if(region is not {} bounds)continue;
          // Floating banks can straddle the origin; still test equal-start contacts explicitly.
          foreach(double x in new[]{bounds.MinX,0,bounds.MaxX})
            if(Math.Abs(b.x-a.x)>CoordinateTolerance){double t=(x-a.x)/(b.x-a.x);if(t>=0&&t<=1)candidates.Add(new(x,a.y+t*(b.y-a.y)));}
          foreach(double y in new[]{bounds.MinY,0,bounds.MaxY})
            if(Math.Abs(b.y-a.y)>CoordinateTolerance){double t=(y-a.y)/(b.y-a.y);if(t>=0&&t<=1)candidates.Add(new(a.x+t*(b.x-a.x),y));}
        }
      }
      foreach(var point in candidates.Where(point=>double.IsFinite(point.x)&&double.IsFinite(point.y))
        .DistinctBy(point=>(Math.Round(point.x,Precision),Math.Round(point.y,Precision))).Take(StackAnchorLimit))
      {if(stop())yield break;token.ThrowIfCancellationRequested();yield return point;}
    }

    private static bool ValidCluster(Plan candidate,Plan previous,IReadOnlySet<int> fixedIds,double width,double length,CancellationToken token)
    {
      if(candidate.Parts.Count!=previous.Parts.Count||candidate.Parts.Select(part=>part.Shape.Id).Distinct().Count()!=previous.Parts.Count)return false;
      foreach(var old in previous.Parts)
      {
        token.ThrowIfCancellationRequested();
        var part=candidate.Parts.FirstOrDefault(part=>part.Shape.Id==old.Shape.Id);
        if(part==null||!Inside(part.Shape,part.X,part.Y,width,length)||fixedIds.Contains(old.Shape.Id)&&
          (part.Shape!=old.Shape||part.Table!=old.Table||part.X!=old.X||part.Y!=old.Y))return false;
      }
      for(int index=0;index<candidate.Parts.Count;index++)
      {
        token.ThrowIfCancellationRequested();var part=candidate.Parts[index];
        if(candidate.Parts.Take(index).Any(other=>other.Table==part.Table&&Collides(part,other)))return false;
      }
      return true;
    }

    private static IEnumerable<Placed> ClusterChoices(IReadOnlyList<Shape> shapes,IReadOnlyList<Placed> parts,IReadOnlyList<int> tables,
      double width,double length,double compactLimit,Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,
      CancellationToken token,Func<bool> stop,SearchState state)
    {
      foreach(int table in tables)
      {
        var occupied=parts.Where(part=>part.Table==table).ToArray();
        double available=Math.Min(length,Math.Max(compactLimit,occupied.Select(part=>part.Y+part.Shape.Pose.Length).DefaultIfEmpty(0).Max()));
        foreach(var shape in shapes.Where(shape=>Inside(shape,0,0,width,available)))
        {
          if(stop())yield break;token.ThrowIfCancellationRequested();var positions=new List<Placed>();
          foreach(var point in Anchors(shape,occupied,width,available,cache,token))
          {
            if(stop())yield break;token.ThrowIfCancellationRequested();state.Tests++;
            var candidate=new Placed(shape,table,point.x,point.y);
            if(!occupied.Any(other=>Collides(candidate,other)))positions.Add(candidate);
          }
          foreach(var candidate in DistinctContactAnchors(positions,ClusterAnchorLimit))yield return candidate;
        }
      }
    }

    private static IEnumerable<Placed> DistinctContactAnchors(IEnumerable<Placed> positions,int limit)
    {
      var kept=new List<Placed>();
      foreach(var part in positions.OrderBy(part=>part.Y+part.Shape.Pose.Length).ThenBy(part=>part.Y).ThenByDescending(part=>part.X+part.Shape.Pose.Width))
      {
        if(kept.Any(other=>Math.Abs(other.X-part.X)<Math.Max(ContactTolerance,part.Shape.Pose.Width*AnchorWidthSeparation)&&
          Math.Abs(other.Y-part.Y)<Math.Max(ContactTolerance,part.Shape.Pose.Length*AnchorLengthSeparation)))continue;
        kept.Add(part);yield return part;if(kept.Count==limit)yield break;
      }
    }

    private static Plan ImprovePairs(Plan best,IReadOnlyList<Input> inputs,Dictionary<int,Shape[]> shapes,double width,double length,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,Stopwatch clock,double searchSeconds,SearchState state,Action<Plan> preview,Action<int>? progress,CancellationToken token)
    {
      var fixedIds=inputs.Where(input=>input.Fixed).Select(input=>input.Id).ToHashSet();
      var movable=best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)).ToArray();
      double Concavity(Placed part)=>1-part.Shape.Area/(part.Shape.Pose.Width*part.Shape.Pose.Length);
      double Contribution(Placed part)=>best.Parts.Where(other=>other.Table==part.Table).Max(other=>other.Y+other.Shape.Pose.Length)-
        best.Parts.Where(other=>other.Table==part.Table&&other.Shape.Id!=part.Shape.Id).Select(other=>other.Y+other.Shape.Pose.Length).DefaultIfEmpty(0).Max();
      var allPairs=movable.SelectMany((first,index)=>movable.Skip(index+1).Select(second=>(First:first,Second:second))).ToArray();
      var byContribution=allPairs.OrderByDescending(pair=>Contribution(pair.First)+Contribution(pair.Second))
        .ThenByDescending(pair=>Math.Min(pair.First.Shape.Area,pair.Second.Shape.Area)).ThenByDescending(pair=>Concavity(pair.First)+Concavity(pair.Second)).ToArray();
      var byFit=allPairs.OrderByDescending(pair=>Math.Max(Concavity(pair.First),Concavity(pair.Second)))
        .ThenByDescending(pair=>Math.Min(pair.First.Shape.Area,pair.Second.Shape.Area)).ThenByDescending(pair=>Concavity(pair.First)+Concavity(pair.Second)).ToArray();
      var pairs=SkinnyFirst(Enumerable.Range(0,allPairs.Length).SelectMany(index=>new[]{byContribution[index],byFit[index]})
        .DistinctBy(pair=>(pair.First.Shape.Id,pair.Second.Shape.Id)),pair=>pair.First.Shape.Skinny||pair.Second.Shape.Skinny).Take(PairTrials).ToArray();
      bool Expired()=>clock.Elapsed.TotalSeconds>=searchSeconds*PairTimeFraction;
      for(int index=0;index<pairs.Length&&!Expired();index++)
      {
        token.ThrowIfCancellationRequested(); var pair=pairs[index];
        foreach(var (firstId,secondId) in SkinnyFirst(new[]{(pair.First.Shape.Id,pair.Second.Shape.Id),(pair.Second.Shape.Id,pair.First.Shape.Id)},order=>shapes[order.Item1][0].Skinny))
        {
          var others=best.Parts.Where(part=>part.Shape.Id!=firstId&&part.Shape.Id!=secondId).ToList();
          var current=best.Parts.First(part=>part.Shape.Id==firstId).Shape;
          foreach(var firstShape in PairPoses(current,shapes[firstId].Where(shape=>Inside(shape,0,0,width,length))))
          {
            if(Expired()) break;
            state.Tests++;
            token.ThrowIfCancellationRequested();
            var locations=PlacementChoices([firstShape],others,width,length,best.Tables,cache,token,Expired,state,null)
              .GroupBy(part=>part.Table).Select(group=>group.OrderBy(part=>part.Y+part.Shape.Pose.Length).ThenByDescending(part=>part.X+part.Shape.Pose.Width).First())
              .OrderBy(part=>MaterialWith(others,part)).Take(PairAnchorAlternatives).ToList();
            var previous=best.Parts.First(part=>part.Shape.Id==firstId);
            var atPrevious=new Placed(firstShape,previous.Table,previous.X,previous.Y);
            if(Inside(firstShape,atPrevious.X,atPrevious.Y,width,length)&&!others.Any(part=>part.Table==atPrevious.Table&&Collides(atPrevious,part))) locations.Insert(0,atPrevious);
            foreach(var first in locations)
            {
              if(Expired()) break;
              var occupied=new List<Placed>(others) {first};
              var secondCurrent=best.Parts.First(part=>part.Shape.Id==secondId).Shape;
              var second=FindPlacement(PairPoses(secondCurrent,shapes[secondId]).ToArray(),occupied,width,length,best.Tables,cache,token,Expired);
              if(second==null) continue;
              occupied.Add(second);
              var candidate=CompactTableOrigins(new Plan(occupied,occupied.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors)),fixedIds,width);
              state.Layouts++;
              if(Better(candidate,best)) { best=candidate; state.Improvements++; preview(best); }
            }
          }
        }
        progress?.Invoke(55+20*(index+1)/Math.Max(1,pairs.Length));
        state.StageProgress?.Invoke(100*(index+1)/Math.Max(1,pairs.Length));
      }
      progress?.Invoke(75); return best;
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
      var ids=SkinnyFirst(best.Parts.Where(part=>!fixedIds.Contains(part.Shape.Id)&&shapes[part.Shape.Id].Any(shape=>Opposite(shape.Pose,part.Shape.Pose))),part=>part.Shape.Skinny)
        .ThenByDescending(Concavity).ThenByDescending(part=>part.Shape.Area).Take(HalfTurnParts).Select(part=>part.Shape.Id).ToArray();
      double deadline=Math.Min(searchSeconds*BeamTimeFraction,clock.Elapsed.TotalSeconds+searchSeconds*HalfTurnTimeFraction);
      bool Expired()=>clock.Elapsed.TotalSeconds>=deadline;
      int examined=0;
      foreach(int id in ids)
      {
        state.StageProgress?.Invoke(100*examined++/Math.Max(1,ids.Length));
        if(Expired()) break;
        token.ThrowIfCancellationRequested();
        var moving=best.Parts.First(part=>part.Shape.Id==id);
        var partners=SkinnyFirst(best.Parts.Where(part=>part.Shape.Id!=id&&!fixedIds.Contains(part.Shape.Id)),part=>part.Shape.Skinny)
          .ThenByDescending(Contribution).ThenByDescending(Concavity).Take(HalfTurnPartners).Select(part=>part.Shape.Id).ToArray();
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
            var candidate=CompactTableOrigins(new Plan(others,others.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(best.Errors)),fixedIds,width);
            state.Layouts++; preview(candidate,true);
            if(Better(candidate,best)) { best=candidate; state.Improvements++; accepted(best); preview(best,false); }
          }
        }
      }
      return best;
    }

    private static IEnumerable<Shape> PairPoses(Shape current,IEnumerable<Shape> options)
    {
      var allowed=options.ToArray();
      double Difference(Shape shape)=>Math.Abs(Math.IEEERemainder(shape.Pose.Rotation-current.Pose.Rotation,360));
      var forward=allowed.Where(shape=>shape.Pose.Flipped==current.Pose.Flipped).OrderBy(Difference).Take(1);
      var reversed=allowed.Where(shape=>shape.Pose.Flipped==current.Pose.Flipped&&Math.Abs(Difference(shape)-180)<=HalfTurnAngleTolerance)
        .OrderBy(shape=>Math.Abs(Difference(shape)-180)).Take(1);
      return forward.Concat(reversed).Concat(allowed.OrderBy(shape=>shape.Pose.Width*shape.Pose.Length)).Distinct().Take(PairPoseLimit);
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
        var sorted=SkinnyFirst(valid,input=>SkinnyPriority(input,width,length)>0).ThenByDescending(input=>SkinnyPriority(input,width,length)).ThenByDescending(Key).ThenBy(input=>input.Id).ToArray();
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
          var ranked=next.OrderBy(Used).ThenBy(plan=>plan.Tables).ThenBy(SkinnyLeft)
            .ThenBy(plan=>plan.Parts.Sum(part=>part.Shape.Pose.Width*part.Shape.Pose.Length)).ToList();
          beams=[];
          foreach(var plan in ranked)
          {
            if(beams.Any(other=>Equivalent(other,plan))) continue;
            beams.Add(plan); if(beams.Count==(valid.Length<=SmallClusterJobLimit?ClusterBeamWidth:BeamWidth)) break;
          }
          foreach(var plan in beams)
          {
            // Display actual branches under evaluation, keeping untouched parts ghosted at the saved best positions.
            var ids=plan.Parts.Select(part=>part.Shape.Id).ToHashSet();
            var trialParts=plan.Parts.Concat(best.Parts.Where(part=>!ids.Contains(part.Shape.Id))).ToList();
            preview(new Plan(trialParts,trialParts.Select(part=>part.Table+1).DefaultIfEmpty(0).Max(),new(plan.Errors)),true);
          }
          progress?.Invoke(75+20*(order*sorted.Length+index+1)/Math.Max(1,BeamOrders*sorted.Length));
          state.StageProgress?.Invoke(100*(order*sorted.Length+index+1)/Math.Max(1,BeamOrders*sorted.Length));
        }
      }
      progress?.Invoke(95); return best;
    }

    private static double Used(Plan plan)=>plan.Parts.GroupBy(part=>part.Table).Sum(table=>table.Max(part=>part.Y+part.Shape.Pose.Length)+table.Max(part=>part.Shape.TableAllowance));

    internal static double TotalMaterialLength(IReadOnlyList<Placement> parts)=>parts.GroupBy(part=>part.Table).Sum(table=>table.Max(part=>part.Y+part.Pose.Length));

    private static double MaterialWith(IReadOnlyList<Placed> parts,Placed placement)=>parts.GroupBy(part=>part.Table)
      .Sum(table=>table.Key==placement.Table?Math.Max(table.Max(part=>part.Y+part.Shape.Pose.Length),placement.Y+placement.Shape.Pose.Length)+Math.Max(table.Max(part=>part.Shape.TableAllowance),placement.Shape.TableAllowance)
        :table.Max(part=>part.Y+part.Shape.Pose.Length)+table.Max(part=>part.Shape.TableAllowance))+
      (parts.Any(part=>part.Table==placement.Table)?0:placement.Y+placement.Shape.Pose.Length+placement.Shape.TableAllowance);

    private static bool Equivalent(Plan first,Plan second)=>first.Parts.Count==second.Parts.Count&&first.Parts.Zip(second.Parts)
      .All(pair=>pair.First.Shape==pair.Second.Shape&&pair.First.Table==pair.Second.Table&&Math.Abs(pair.First.X-pair.Second.X)<=CoordinateTolerance&&Math.Abs(pair.First.Y-pair.Second.Y)<=CoordinateTolerance);

    private static IEnumerable<Placed> PlacementChoices(IReadOnlyList<Shape> shapes,IReadOnlyList<Placed> parts,double width,double length,int tables,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken token,Func<bool> stop,SearchState state,Action<Placed>? trial)
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
            if(!occupied.Any(other=>Collides(placement,other))) { positions.Add(placement); trial?.Invoke(placement); }
          }
          choices.AddRange(DistinctContactAnchors(positions,BeamAnchors));
        }
        if(choices.Count==0) continue;
        foreach(var choice in choices) yield return choice;
      }
    }

    private static Shape? CreateShape(int id,Pose pose)=>CreateShapeCore(id,pose,DefaultSimplifyContact);
    private static Shape? CreateShapeCore(int id,Pose pose,bool simplifyContact)
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
      if(simplifyContact&&path.Count>ContactVertexLimit)
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
      double candidateLength=Used(candidate),previousLength=Used(previous);
      if(Math.Abs(candidateLength-previousLength)>CoordinateTolerance) return candidateLength<previousLength;
      if(candidate.Tables!=previous.Tables) return candidate.Tables<previous.Tables;
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
        progress?.Invoke((int)(order==0?50*fraction:50+50*(order-1+fraction)/(Math.Max(OrderTrials,order+1)-1)));
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
      var sorted=SkinnyFirst(remaining,input=>SkinnyPriority(input,width,length)>0)
        .ThenByDescending(input=>SkinnyPriority(input,width,length))
        .ThenBy(input=>order>=OrderTrials?input.ShuffleRank.GetValueOrDefault():order==OrderTrials-1?Key(input):-Key(input))
        .ThenBy(input=>shapes[input.Id].Min(shape=>shape.Pose.Width))
        .ThenBy(input=>shapes[input.Id].Min(shape=>shape.Pose.Length)).ThenBy(input=>input.Id);
      foreach(var input in sorted)
      {
        if(stop?.Invoke()==true) break;
        cancellationToken.ThrowIfCancellationRequested();
        var accepted=FindInitialPlacement(shapes[input.Id],parts,width,length,tables,cache,cancellationToken,stop);
        processed++; Report();
        if(accepted==null) { errors[input.Id]="Table limit reached"; continue; }
        parts.Add(accepted); tables=Math.Max(tables,accepted.Table+1);
        preview?.Invoke(new Plan(parts,tables,errors));
      }
      return new Plan(parts,tables,errors);
    }

    private static Placed? FindInitialPlacement(IReadOnlyList<Shape> shapes,IReadOnlyList<Placed> parts,double width,double length,int tables,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken token,Func<bool>? stop)
    {
      // Build a compact seed; global reassignment subsequently compares its actual occupied material.
      for(int table=0;table<=tables&&table<NestPackingEngine.MaximumTables;table++)
      {
        token.ThrowIfCancellationRequested(); if(stop?.Invoke()==true) return null;
        var placed=FindPlacement(shapes,parts,width,length,tables,cache,token,stop,new Dictionary<int,double>{{table,length}});
        if(placed!=null) return placed;
      }
      return null;
    }

    private static Placed? FindPlacement(IReadOnlyList<Shape> shapes,IReadOnlyList<Placed> parts,double width,double length,int tables,
      Dictionary<(Shape Moving,Shape Fixed),PathsD> cache,CancellationToken token,Func<bool>? stop,IReadOnlyDictionary<int,double>? materialLimits=null,bool preferGaps=false)
    {
      var ends=parts.GroupBy(part=>part.Table).ToDictionary(group=>group.Key,group=>group.Max(part=>part.Y+part.Shape.Pose.Length));
      var allowances=parts.GroupBy(part=>part.Table).ToDictionary(group=>group.Key,group=>group.Max(part=>part.Shape.TableAllowance));
      double existingMaterial=ends.Values.Sum()+allowances.Values.Sum();
      Placed? accepted=null;
      int bestGap=-1;
      double bestLength=double.PositiveInfinity,bestY=double.PositiveInfinity,bestBottom=double.NegativeInfinity;
      for(int table=0;table<=tables&&table<NestPackingEngine.MaximumTables;table++)
      {
        if(materialLimits!=null&&!materialLimits.ContainsKey(table)) continue;
        double availableLength=materialLimits?.GetValueOrDefault(table)??length;
        double tableEnd=ends.GetValueOrDefault(table);
        var occupied=parts.Where(part=>part.Table==table).ToArray();
        foreach(var shape in shapes.OrderBy(shape=>Math.Abs(shape.Pose.Rotation)))
        {
          token.ThrowIfCancellationRequested(); if(stop?.Invoke()==true) return null;
          if(!Inside(shape,0,0,width,availableLength)) continue;
          double extraAllowance=Math.Max(allowances.GetValueOrDefault(table),shape.TableAllowance)-allowances.GetValueOrDefault(table);
          double lowerBound=existingMaterial+Math.Max(tableEnd,shape.Pose.Length)-tableEnd+extraAllowance;
          // An ideal y=0/bottom-aligned placement bounds every anchor for this pose.
          if(lowerBound>bestLength+CoordinateTolerance||!preferGaps&&Math.Abs(lowerBound-bestLength)<=CoordinateTolerance&&
            bestY<=CoordinateTolerance&&bestBottom>=width-CoordinateTolerance)
          { if(shape.Metrics is {} profile) profile.PosesPruned++; continue; }
          foreach(var point in Anchors(shape,occupied,width,availableLength,cache,token))
          {
            token.ThrowIfCancellationRequested(); if(stop?.Invoke()==true) return null;
            double end=point.y+shape.Pose.Length;
            double material=existingMaterial+Math.Max(tableEnd,end)-tableEnd+extraAllowance;
            if(material>bestLength+CoordinateTolerance) continue;
            double bottom=point.x+shape.Pose.Width;
            if(!preferGaps&&Math.Abs(material-bestLength)<=CoordinateTolerance&&
              !BetterAnchor(point.y,bottom,bestY,bestBottom,shape.Skinny)) continue;
            var candidate=new Placed(shape,table,point.x,point.y);
            if(occupied.Any(other=>Collides(candidate,other))) continue;
            int gap=preferGaps?GapNeighbors(candidate,occupied):0;
            if(preferGaps&&Math.Abs(material-bestLength)<=CoordinateTolerance)
            {
              if(point.y>bestY+CoordinateTolerance) continue;
              if(Math.Abs(point.y-bestY)<=CoordinateTolerance&&
                (gap<bestGap||gap==bestGap&&bottom<=bestBottom+CoordinateTolerance)) continue;
            }
            accepted=candidate; bestLength=material; bestY=point.y; bestBottom=bottom; bestGap=gap;
          }
        }
        if(accepted!=null&&!preferGaps&&bestLength<=existingMaterial+CoordinateTolerance&&bestY<=CoordinateTolerance&&bestBottom>=width-CoordinateTolerance) return accepted;
      }
      return accepted;
    }

    private static bool BetterAnchor(double y,double bottom,double previousY,double previousBottom,bool skinny)
    {
      if(skinny&&Math.Abs(y-previousY)>CoordinateTolerance)return y<previousY;
      if(Math.Abs(bottom-previousBottom)>CoordinateTolerance)return bottom>previousBottom;
      return y<previousY-CoordinateTolerance;
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
      var metrics=first.Shape.Metrics; long started=Stopwatch.GetTimestamp();
      bool boundsOverlap=first.X+first.Shape.Pose.Width>second.X&&second.X+second.Shape.Pose.Width>first.X&&
        first.Y+first.Shape.Pose.Length>second.Y&&second.Y+second.Shape.Pose.Length>first.Y;
      bool collision=boundsOverlap&&Math.Abs(Clipper.Area(Clipper.Intersect(new PathsD {first.WorldPath},new PathsD {second.WorldPath},FillRule.NonZero,Precision)))>AreaTolerance||gap>CoordinateTolerance&&
        NearbyEdges(first,second,0,0,gap).Any(pair=>EdgeDistanceSquared(pair.Moving,pair.Fixed)<(gap-CoordinateTolerance)*(gap-CoordinateTolerance));
      if(metrics!=null) { metrics.CollisionCalls++; metrics.CollisionTicks+=Stopwatch.GetTimestamp()-started; }
      return collision;
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
        if(PairCache.TryGetValue(new PairKey(fixedShape.GeometryKey,moving.GeometryKey,gap),out var reverse))
        {
          PairRecency.Remove(reverse); PairRecency.AddLast(reverse);
          if(moving.Metrics is {} hit) hit.PairHits++;
          return new PathsD(reverse.Value.Paths.Select(path=>new PathD(path.Select(point=>new PointD(-point.x,-point.y)))));
        }
      }
      long started=Stopwatch.GetTimestamp();
      var paths=Minkowski.Diff(moving.Contact,fixedShape.Contact,true,Precision);
      if(gap>0) paths=RoundClearance(paths,gap);
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
