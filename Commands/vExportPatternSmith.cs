#if PATTERNSMITH_BUILDER
// Template-free PatternSmith 10 native-model builder. Request lengths are inches,
// angles radians, and geometry coordinates relative to the table lower-left.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autometrix.Core;
using Autometrix.Core.Entities;
using Autometrix.Core.Geometry;
using Autometrix.Core.Mathematics;
using Autometrix.Core.Plotting;
using Autometrix.Core.Nesting;
using Autometrix.Core.Nesting.Materials;
using Autometrix.Core.Nesting.Materials.Patterns;
using Autometrix.Core.Nesting.Pattern;
using Autometrix.Presentation.Core.Data;
using Autometrix.Conversion.Project;
using NativeNotch = Autometrix.Core.Entities.Notches.Notch;
using NativeNotchExecution = Autometrix.Core.Entities.Notches.ExecutionStyle;
using NativeNotchStyle = Autometrix.Core.Entities.Notches.Style;

public static class EntryPoint
{
    static string libraryDir;
    [STAThread]
    public static int Main(string[] args)
    {
        try {
            if (args.Length != 3) throw new ArgumentException("Usage: NativeProjectBuilder.exe <PatternSmith-directory> <request.xml> <new-output.project>");
            libraryDir = Path.GetFullPath(args[0]);
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            if(Environment.GetEnvironmentVariable("PATTERNSMITH_BUILDER_DIAGNOSTICS")=="1")AppDomain.CurrentDomain.FirstChanceException+=(sender,e)=>Console.Error.WriteLine("FIRST CHANCE "+e.Exception+"\nCURRENT STACK "+Environment.StackTrace);
            return NativeProjectBuilder.Run(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string path = Path.Combine(libraryDir, new AssemblyName(e.Name).Name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
public static class NativeProjectBuilder
{
    // Customizable export defaults. Lengths/speeds use native inches and inches/sec.
    public const string DefaultUnits = "inches"; // Fixed native request unit; other unit encodings are rejected.
    public const string FaceDownSuffix = " [Face down]"; // Cutting-side annotation for SDKs without a per-pattern face-down field; never mirrors geometry.
    public const string DefaultTool = "Cut"; // Native processing-role fallback for a request without a tool attribute.
    public const string DefaultPerimeter = "Auto"; // Auto, Derived, Element or Box; Element is the UI name for a derived entity perimeter.
    public const double DefaultTextHeight = 1.0; // Positive native inches when a request omits text height.
    public const float NativeFontPointSize = 12f; // Positive GDI font size in points; drawing height is set from the native glyph bounds.
    public const double FontPointsPerInch = 72; // Fixed conversion between PatternSmith 11 font points and native drawing inches.
    public const bool DefaultLegacyTableFontSizing = false; // true stores the PS10 font units; false stores editable PS11 font units.
    public const double DefaultPointRadius = 0.125; // Positive native inches when a request omits point radius.
    public const double DefaultCopySpacing = 0.0; // Nonnegative native inches for uncatalogued materials.
    public const double DefaultEdgeSpacing = 0.0; // Nonnegative native inches for uncatalogued materials.
    public const double DefaultMaximumCutSpeed = 60.0; // Positive inches/second for uncatalogued materials.
    public const double DefaultMaximumPlotSpeed = 60.0; // Positive inches/second for uncatalogued materials.
    public const double DefaultMaterialLightness = 0.9; // Zero through one, used with neutral hue/saturation for uncatalogued materials.
    public const double DefaultMaterialCost = 0.0; // Nonnegative cost per native MaterialUnit for uncatalogued materials.
    public const int DefaultMinimumPower = 0; // Native minimum power percentage, zero through one hundred.
    public const int DefaultMaximumPower = 100; // Native maximum power percentage, zero through one hundred.
    public const bool DefaultEnablePowerSettings = false; // true enables configured laser power; false leaves it disabled.
    public const bool DefaultAllowNesting = true; // true permits editing/AutoNest of real parts; false disables it; table annotations are always excluded.
    public const bool DefaultPopulated = true; // true creates explicit copies; false exports unplaced definitions only.
    public const bool DefaultMaterialAssigned = true; // true preserves material links for legacy requests without provenance; false explicitly leaves new unassigned groups without materials.
    public const string EmptyProjectMaterialId = "0001"; // Native fallback ID used only for the empty roll required by a wholly unassigned project; never linked to a pattern.
    public const string EmptyProjectMaterialName = "Default"; // Native empty-project roll name; not a material assignment for exported items.
    public const PatternFillColorStyle DefaultPatternFillStyle = PatternFillColorStyle.Custom; // Explicit pattern color copies its material; Material style does not reliably draw stripes in PatternSmith.
    public const bool DefaultPatternFillStriped = true; // true displays slanted material-color stripes; false would use a solid material fill.
    public const double MinimumGeometryDistance = 1e-12; // Positive inches; shorter or collapsed segments are rejected, never padded.
    public const NativeNotchExecution DefaultNotchExecutionStyle = NativeNotchExecution.Discrete; // Compatibility fallback for decoder requests predating inline profiles.
    public const int NativeEffectSamplingDivisor = 16; // Divisor greater than two; reserve the sampling error of both paths during effect verification.
    public const int NativeEffectSplineFitDivisor = 20; // Positive divisor of effect tolerance for fitting sew curves before generating cut paths.
    public const int NativeEffectCompactPointThreshold = 64; // Fit-point count above which to test a smaller equivalent native effect spline.
    public const int NativeEffectCompactAttempts = 4; // Maximum thinning trials per dense spline; halve the requested trial error each time.
    public const int NativeEffectEndpointKnots = 4; // Original fit points retained at each end of an open spline to preserve endpoint tangents.
    public const int NativeEffectNotchKnotRadius = 8; // Original fit points kept on either side of each notch anchor.
    static readonly int[] NativeEffectKnotStrides = {16,12,8,4,2}; // Try sparse-to-dense interior fit-point strides; all must pass path verification.
    public const int NativeNotchStationRefinementAttempts = 3; // Maximum corrected arc-length stations after the initial notch does not match.
    public const int NativeNotchStationRefinementReach = 16; // Maximum arc-length correction as a positive multiple of document tolerance.
    public const int DiagnosticEntityCharacterLimit = 1024; // Maximum request characters included in an invalid-entity diagnostic.
    public const double DefaultWorkspaceGap = 12.0; // Nonnegative inches between table groups in the pattern-definition workspace, never between nested copies.
    public const double DefaultMergeWorkspaceGap = 36.0; // Nonnegative native inches between the old workspace and a new merged layout.
    public const long MaximumMergeXmlBytes = 268435456; // Maximum uncompressed project XML bytes accepted for a merge; oversized files are left untouched.
    public const int MaximumMergeArchiveEntries = 4096; // Maximum ZIP entries accepted from each project; payloads are copied without extraction.
    public const double DefaultSplineTolerance = 0.001; // Positive inches of maximum sampled conversion deviation when a request omits its document tolerance.
    public const double LegacySplineThinError = 0.0; // Zero preserves older requests that omit thinning; current Rhino requests supply their configured error in inches.
    public const int SplineConversionAttempts = 6; // Positive maximum native spline refinements before rejecting an inaccurate conversion.
    public const double SplineRefinementFactor = 4.0; // Factor greater than one reducing each native conversion's subdivision tolerance.
    public const int MaximumPerimeterFailureDetails = 4; // Positive maximum offending entity descriptions included in a perimeter error.
    public const int PreviewSizePixels = 1024; // Fixed square embedded PNG dimensions; alpha is preserved and no background is painted.
    public const double PreviewMarginPixels = 16; // Positive pixel inset around actual exported patterns, including their header geometry.
    public const double PreviewStrokePixels = 2; // Fixed pixel width, remaining visible when the embedded preview is scaled down.
    public const int DefaultConversionWorkers = 1; // Positive serial fallback for older requests without an explicit worker count.
    public const int MaximumConversionWorkers = 32; // Fixed safety ceiling for explicitly requested independent geometry workers.
    public const int SplineProgressStart = 5; // Native progress percentage where spline conversion begins.
    public const int SplineProgressEnd = 55; // Native progress percentage reserved for cached spline conversion.
    public const int EntityProgressEnd = 70; // Native progress percentage where entity assembly ends.
    public const double NativeDistanceInterpolation = 0.001; // Fixed inches used internally by SDK Curve2d.DistanceTo; compatibility value, not an export-error setting.
    public const double DefaultSourceRotation = 0.0; // Radians from source orientation; older requests and table annotations retain zero.
    public const int DefaultDistanceIndexLeafSegments = 16; // Positive maximum segment count per distance-index leaf; final tests always use native Line2d distance.
    public const int MinimumDistanceIndexSegments = 32; // Fixed segment-count threshold below which a linear distance search is cheaper.
    public const int LocalDistanceProbeSegments = 128; // Positive ordered-search budget before lazily building a spatial index for expensive queries.
    public const int DistanceIndexLongSearchThreshold = 8; // Positive number of expensive searches needed to amortize indexing a dense interpolation.
    public const double DistanceBoundsRelativePadding = 1e-12; // Conservative relative expansion of index boxes, never of the final distance threshold.
    public const int DefaultProgressIntervalMilliseconds = 100; // Nonnegative minimum interval between same-stage progress messages; zero disables throttling.
    public const int SplineCacheRevision = 4; // Persistent conversion format/algorithm revision; increment when conversion semantics change.
    public const int DefaultSplineCacheMegabytes = 256; // Positive maximum MiB retained in the optional per-user conversion cache.
    public const int MaximumCachedSplinePoints = 500000; // Positive per-entry point limit; larger splines still export without disk caching.
    public const int SplineShapeKeyDecimals = 9; // Decimal places in local-coordinate lookup keys; every transformed reuse is independently error-checked.
    public const int CacheTemporaryFileAgeHours = 24; // Positive age in hours before abandoned atomic-write files owned by this cache can be removed.
    public const double AnnotationRowToleranceInches = 1e-6; // Fixed native-inch numerical tolerance when grouping existing table-header rows; never changes part geometry.
    static readonly ReadOnlyCollection<Layer> NoLayers = new ReadOnlyCollection<Layer>(new Layer[0]);
    static readonly ReadOnlyCollection<DynamicEntity> NoDynamic = new ReadOnlyCollection<DynamicEntity>(new DynamicEntity[0]);
    static readonly Layer SplineValidationLayer = new Layer("Spline validation"); // Temporary SDK validation layer; never exported.
    static readonly Dictionary<string, Layer> Layers = new Dictionary<string, Layer>();
    static readonly Dictionary<Layer, ToolReference> ToolMap = new Dictionary<Layer, ToolReference>();
    static readonly Dictionary<string,System.Windows.Media.SolidColorBrush> PreviewToolBrushes = new Dictionary<string,System.Windows.Media.SolidColorBrush>(StringComparer.Ordinal);
    static readonly HashSet<EntityCollection> ValidatedPerimeters = new HashSet<EntityCollection>();
    static int distanceIndexLeafSegments=DefaultDistanceIndexLeafSegments;
    sealed class SegmentNode
    {
        internal readonly double MinX,MinY,MaxX,MaxY;
        internal readonly int Start,Count;
        internal readonly SegmentNode Left,Right;
        internal SegmentNode(Point2d[] points,int start,int count)
        {
            Start=start;Count=count;
            // Curve order is already spatially coherent; a bottom-up hierarchy avoids sorting dense interpolations.
            if(count>distanceIndexLeafSegments) {
                int half=count/2;Left=new SegmentNode(points,start,half);Right=new SegmentNode(points,start+half,count-half);
                MinX=Math.Min(Left.MinX,Right.MinX);MinY=Math.Min(Left.MinY,Right.MinY);
                MaxX=Math.Max(Left.MaxX,Right.MaxX);MaxY=Math.Max(Left.MaxY,Right.MaxY);return;
            }
            MinX=MinY=double.PositiveInfinity;MaxX=MaxY=double.NegativeInfinity;
            for(int i=start;i<start+count;i++) {
                var a=points[i];var b=points[i+1];
                MinX=Math.Min(MinX,Math.Min(a.X,b.X));MinY=Math.Min(MinY,Math.Min(a.Y,b.Y));
                MaxX=Math.Max(MaxX,Math.Max(a.X,b.X));MaxY=Math.Max(MaxY,Math.Max(a.Y,b.Y));
            }
        }
        internal bool MayContain(Point2d point,double threshold)
        {
            double padding=DistanceBoundsRelativePadding*Math.Max(1,Math.Max(Math.Max(Math.Abs(point.X),Math.Abs(point.Y)),
                Math.Max(Math.Max(Math.Abs(MinX),Math.Abs(MaxX)),Math.Max(Math.Abs(MinY),Math.Abs(MaxY)))));
            double radius=threshold+padding;
            return !(point.X<MinX&&MinX-point.X>radius||point.X>MaxX&&point.X-MaxX>radius||
                point.Y<MinY&&MinY-point.Y>radius||point.Y>MaxY&&point.Y-MaxY>radius);
        }
    }
    sealed class CurveDistanceQuery
    {
        readonly Curve2d curve;
        Poly2d interpolation;
        Point2d[] points;
        int hint;
        int longSearches;
        SegmentNode indexRoot;
        readonly double interpolationTolerance;
        double minX=double.NegativeInfinity,minY=double.NegativeInfinity;
        double maxX=double.PositiveInfinity,maxY=double.PositiveInfinity;
        internal CurveDistanceQuery(Curve2d source,double tolerance=NativeDistanceInterpolation) { curve=source;interpolationTolerance=tolerance; }
        internal CurveDistanceQuery(Point2d[] samples)
        {
            points=samples;
            if(samples.Length==0)return;
            minX=maxX=samples[0].X;minY=maxY=samples[0].Y;
            foreach(var p in samples) {
                minX=Math.Min(minX,p.X);maxX=Math.Max(maxX,p.X);
                minY=Math.Min(minY,p.Y);maxY=Math.Max(maxY,p.Y);
            }
        }
        internal bool Within(Point2d point,double threshold)
        {
            if(curve!=null) {
                var current=curve.GetInterpolatedCurve(interpolationTolerance);
                if(!Object.ReferenceEquals(current,interpolation)) {
                    interpolation=current;points=current.Points.ToArray();hint=0;
                    indexRoot=null;longSearches=0;
                }
            }
            if(points==null||points.Length==0)return false;
            if(double.IsNaN(threshold)||threshold<0)return false;
            if(point.X<minX-threshold||point.X>maxX+threshold||point.Y<minY-threshold||point.Y>maxY+threshold)return false;
            if(points.Length==1)return points[0].DistanceTo(point)<=threshold;
            if(indexRoot!=null) {
                int budget=LocalDistanceProbeSegments;
                for(int i=hint;i<points.Length-1&&budget>0;i++,budget--)
                    if(new Line2d(points[i],points[i+1]).DistanceTo(point)<=threshold) {hint=i;return true;}
                for(int i=0;i<hint&&budget>0;i++,budget--)
                    if(new Line2d(points[i],points[i+1]).DistanceTo(point)<=threshold) {hint=i;return true;}
                return Search(indexRoot,point,threshold)||double.MaxValue<=threshold;
            }
            // Queries follow each curve's parameter order. Start near the last hit,
            // then wrap so shuffled points and closed seams still search every segment.
            for(int index=hint;index<points.Length-1;index++)
                if(new Line2d(points[index],points[index+1]).DistanceTo(point)<=threshold) { NoteSearch(index-hint);hint=index;return true; }
            for(int index=0;index<hint;index++)
                if(new Line2d(points[index],points[index+1]).DistanceTo(point)<=threshold) { NoteSearch(points.Length-1-hint+index);hint=index;return true; }
            NoteSearch(points.Length-1);
            return double.MaxValue<=threshold;
        }
        void NoteSearch(int scanned)
        {
            if(scanned>=Math.Max(MinimumDistanceIndexSegments,LocalDistanceProbeSegments)&&++longSearches>=DistanceIndexLongSearchThreshold)
                indexRoot=new SegmentNode(points,0,points.Length-1);
        }
        bool Search(SegmentNode node,Point2d point,double threshold)
        {
            if(!node.MayContain(point,threshold))return false;
            if(node.Left!=null)return Search(node.Left,point,threshold)||Search(node.Right,point,threshold);
            for(int i=node.Start;i<node.Start+node.Count;i++) {
                int segment=i;
                if(new Line2d(points[segment],points[segment+1]).DistanceTo(point)<=threshold) {hint=segment;return true;}
            }
            return false;
        }
    }
    sealed class SplineData
    {
        internal readonly Point2d[] Points;
        internal readonly Point2d[] Source;
        internal readonly string Key;
        internal readonly bool Closed;
        internal SplineData(Spline2d spline,Point2d[] source,string key) : this(spline.ControlPoints.ToArray(),spline.IsClosed,source,key) { }
        internal SplineData(Point2d[] points,bool closed,Point2d[] source,string key) { Points=points;Closed=closed;Source=source;Key=key; }
        internal Spline2d Create() { return new Spline2d(Points,Closed); }
    }
    sealed class SplineFrame
    {
        internal readonly Point2d Origin;
        internal readonly double X,Y;
        internal SplineFrame(Point2d[] source)
        {
            Origin=source[0];X=1;Y=0;
            foreach(var point in source) {
                double dx=point.X-Origin.X,dy=point.Y-Origin.Y,length=Math.Sqrt(dx*dx+dy*dy);
                if(length>MinimumGeometryDistance&&!double.IsInfinity(length)) {X=dx/length;Y=dy/length;break;}
            }
        }
        internal Point2d Local(Point2d point) {double dx=point.X-Origin.X,dy=point.Y-Origin.Y;return new Point2d(dx*X+dy*Y,-dx*Y+dy*X);}
        internal Point2d World(Point2d point) {return new Point2d(Origin.X+point.X*X-point.Y*Y,Origin.Y+point.X*Y+point.Y*X);}
    }
    static readonly ConcurrentDictionary<string,Lazy<SplineData>> ShapeSplines=new ConcurrentDictionary<string,Lazy<SplineData>>(StringComparer.Ordinal);
    static string splineCacheDirectory;
    static bool reuseEquivalentSplines;
    static int splineCacheMegabytes=DefaultSplineCacheMegabytes,diskCacheHits,shapeCacheHits,cacheWarning;
    static readonly Dictionary<string,SplineData> ConvertedSplines = new Dictionary<string,SplineData>(StringComparer.Ordinal);
    static readonly Dictionary<XElement,SplineData> RequestedSplines = new Dictionary<XElement,SplineData>();
    static readonly Stopwatch PhaseClock = new Stopwatch();
    static string currentPhase;
    static int entityProgressStart;
    static int completedEntities,totalEntities;
    static readonly Stopwatch ProgressClock=Stopwatch.StartNew();
    static readonly object ProgressLock=new object();
    static string progressKind;
    static long lastProgressMilliseconds;
    static int progressIntervalMilliseconds=DefaultProgressIntervalMilliseconds;
    static void Phase(string name)
    {
        if(PhaseClock.IsRunning)Console.WriteLine("TIMING|"+currentPhase+"|"+PhaseClock.Elapsed.TotalMilliseconds.ToString("0.###",CultureInfo.InvariantCulture)+" ms");
        currentPhase=name;PhaseClock.Restart();
    }
    static void Progress(int percent,string stage)
    {
        string kind=stage.StartsWith("Converting native splines ",StringComparison.Ordinal)?"splines":
            stage.StartsWith("Converting ",StringComparison.Ordinal)?"entities":stage.StartsWith("Checking ",StringComparison.Ordinal)?"checking":stage;
        lock(ProgressLock) {
            long now=ProgressClock.ElapsedMilliseconds;
            if(kind==progressKind&&percent!=100&&now-lastProgressMilliseconds<progressIntervalMilliseconds)return;
            progressKind=kind;lastProgressMilliseconds=now;
            Console.WriteLine("PROGRESS|"+percent.ToString(CultureInfo.InvariantCulture)+"|"+stage.Replace('\r',' ').Replace('\n',' '));Console.Out.Flush();
        }
    }
    public static int Run(string input,string output) { return Build(input,output); }
    static string S(XElement e, string name, string fallback)
    { return (string)e.Attribute(name) ?? fallback; }
    static double D(XElement e, string name, double fallback)
    {
        string text = (string)e.Attribute(name);
        double n = text == null ? fallback : double.Parse(text, CultureInfo.InvariantCulture);
        if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException("Nonfinite " + name);
        return n;
    }
    static bool B(XElement e, string name, bool fallback)
    { return e.Attribute(name) == null ? fallback : bool.Parse((string)e.Attribute(name)); }
    static Point2d P(XElement e) { return new Point2d(D(e, "x", 0), D(e, "y", 0)); }
    static List<Point2d> Points(XElement e) { return e.Elements("P").Select(P).ToList(); }
    static Layer ToolLayer(XElement e)
    {
        string tool = S(e, "tool", DefaultTool);
        if (tool.Equals("Plot", StringComparison.OrdinalIgnoreCase)) tool = "Mark";
        ProcessType process;
        if (!Enum.TryParse<ProcessType>(tool, true, out process) || !Enum.IsDefined(typeof(ProcessType), process)) throw new ArgumentException("Unsupported tool: " + tool);
        int index = int.Parse(S(e, "index", "0"), CultureInfo.InvariantCulture);
        if (index < 0) throw new ArgumentException("Tool index must be nonnegative.");
        // Match PatternSmith ImportLayers.ValidateLayer for each entity family.
        string type=e.Name.LocalName.ToLowerInvariant();
        bool common=process==ProcessType.Reference || process==ProcessType.Mark || process==ProcessType.Inkjet || process==ProcessType.Print;
        bool supported=common || (type=="point" ? process==ProcessType.Drill || process==ProcessType.Punch : type=="text" ? false :
            process==ProcessType.Cut || process==ProcessType.Crease || type=="notch"&&process.ToString()=="Notch");
        if(!supported) {
            Console.WriteLine("ROLE FALLBACK " + e.Name.LocalName + " " + process + " -> Reference");
            process=ProcessType.Reference;index=0;
        }
        string key = process + ":" + index;
        Layer layer;
        if (!Layers.TryGetValue(key, out layer)) {
            layer = new Layer(process == ProcessType.Mark ? "Plot" : process.ToString() + (index == 0 ? "" : (index+1).ToString(CultureInfo.InvariantCulture)));
            Layers.Add(key, layer); ToolMap.Add(layer, new ToolReference(process, index));
        }
        return layer;
    }
    static Entity PreserveShortNativeLine(Entity entity)
    {
        var line=entity as LineEntity;
        if(line==null || line.IsValid())return entity;
        double length=line.A.DistanceTo(line.B);
        if(double.IsNaN(length) || double.IsInfinity(length) || length<MinimumGeometryDistance)return entity;
        // Native LineEntity rejects lengths rounding below 0.02 inches. A
        // two-point PolyEntity represents the identical finite segment without
        // deleting it, lengthening it, or discarding its layer/perimeter data.
        var polyline=new PolyEntity(line.Layer,line.SecondaryLayers,
            new Poly2d(new[]{line.A,line.B}),line.Seam,line.DynamicEntities,
            line.ExtensionStyles,line.Order,line.PersistentId,line.PartTemplateId,line.ExcludeFromPerimeter);
        if(!polyline.IsValid())return entity;
        Console.WriteLine("SHORT LINE PRESERVED lengthInches="+length.ToString("R",CultureInfo.InvariantCulture));
        return polyline;
    }
    static Entity EntityFromRequest(XElement e,bool effectReference=false)
    {
        Layer layer = ToolLayer(e);
        Entity entity;
        string type = e.Name.LocalName.ToLowerInvariant();
        List<Point2d> points;
        if (type == "line") {
            points = Points(e);
            if (points.Count != 2 || points[0].DistanceTo(points[1]) < MinimumGeometryDistance) throw new ArgumentException("Line requires two distinct P points.");
            entity = new LineEntity(layer, NoLayers, points[0], points[1], null, NoDynamic);
        } else if (type == "polyline") {
            points = Points(e);
            if (points.Count < 2) throw new ArgumentException("Polyline requires at least two P points.");
            if (B(e, "closed", false) && points[0].DistanceTo(points[points.Count-1]) > MinimumGeometryDistance) points.Add(points[0]);
            entity = new Poly2d(points).GetEntity(layer, NoLayers, null, NoDynamic);
        } else if (type == "circle") {
            double radius = D(e, "radius", 0); if (radius <= 0) throw new ArgumentException("Circle radius must be positive.");
            entity = new Circle2d(P(e), radius, CircularDirection.Counterclockwise).GetEntity(layer, NoLayers, null, NoDynamic);
        } else if (type == "arc") {
            double radius = D(e, "radius", 0), sweep = D(e, "sweep", 0);
            if (radius <= 0 || sweep == 0 || Math.Abs(sweep) > 2*Math.PI) throw new ArgumentException("Invalid arc radius/sweep.");
            entity = new Arc2d(P(e), radius, D(e, "start", 0), sweep).GetEntity(layer, NoLayers, null, NoDynamic);
        } else if (type == "beziergroup") {
            points = Points(e);
            if (points.Count < 4 || (points.Count-1)%3 != 0) throw new ArgumentException("BezierGroup requires 3n+1 control points.");
            var curve = new BezierGroup2d(points);
            entity = !effectReference && B(e,"nativeSpline",false)
                ? CachedNativeSpline(e).GetEntity(layer,NoLayers,null,NoDynamic)
                : curve.GetEntity(layer, NoLayers, null, NoDynamic);
        } else if (type == "point") {
            entity = new PointEntity(layer, NoLayers, P(e), D(e, "rotation", 0), new NonnegativeDouble(D(e, "radius", DefaultPointRadius)), S(e, "label", ""));
        } else if (type == "text") {
            entity = TextFromRequest(e,layer,P(e),D(e,"rotation",0));
        } else throw new ArgumentException("Unsupported entity: " + e.Name);
        var continuous = entity as ContinuousEntity;
        bool exclude = B(e, "excludeFromPerimeter", !S(e,"tool",DefaultTool).Equals("Cut",StringComparison.OrdinalIgnoreCase));
        if (continuous != null && exclude) entity = continuous.With(null, null, null, true, null);
        entity=PreserveShortNativeLine(entity);
        if (!entity.IsValid()) {
            string detail=e.ToString(SaveOptions.DisableFormatting);
            if(detail.Length>DiagnosticEntityCharacterLimit)detail=detail.Substring(0,DiagnosticEntityCharacterLimit)+"...";
            throw new ArgumentException("Invalid native entity: " + e.Name + "; request=" + detail);
        }
        return entity;
    }
    static string SplineKey(XElement e)
    {
        var key=new StringBuilder();
        key.Append(D(e,"tolerance",DefaultSplineTolerance).ToString("R",CultureInfo.InvariantCulture)).Append('|')
            .Append(D(e,"thinError",LegacySplineThinError).ToString("R",CultureInfo.InvariantCulture)).Append('|').Append(B(e,"closed",false));
        foreach(var point in Points(e))key.Append('|').Append(point.X.ToString("R",CultureInfo.InvariantCulture)).Append(',').Append(point.Y.ToString("R",CultureInfo.InvariantCulture));
        return key.ToString();
    }
    static SplineData ConvertSpline(XElement e,string key,Point2d[] source)
    {
        return new SplineData(NativeSpline(source,D(e,"tolerance",DefaultSplineTolerance),B(e,"closed",false),D(e,"thinError",LegacySplineThinError)),source,key);
    }
    static string ShapeKey(XElement e,Point2d[] source)
    {
        var frame=new SplineFrame(source);
        var key=new StringBuilder();key.Append(D(e,"tolerance",DefaultSplineTolerance).ToString("R",CultureInfo.InvariantCulture)).Append('|')
            .Append(D(e,"thinError",LegacySplineThinError).ToString("R",CultureInfo.InvariantCulture)).Append('|').Append(B(e,"closed",false));
        foreach(var point in source) {
            var local=frame.Local(point);
            key.Append('|').Append(Math.Round(local.X,SplineShapeKeyDecimals).ToString("R",CultureInfo.InvariantCulture))
                .Append(',').Append(Math.Round(local.Y,SplineShapeKeyDecimals).ToString("R",CultureInfo.InvariantCulture));
        }
        return key.ToString();
    }
    static bool TryRepositionSpline(SplineData original,XElement request,Point2d[] source,string key,out SplineData result)
    {
        result=null;
        if(source.Length!=original.Source.Length)return false;
        var from=new SplineFrame(original.Source);var to=new SplineFrame(source);
        var candidate=new Spline2d(original.Points.Select(point=>to.World(from.Local(point))),original.Closed);
        if(!IsValidSpline(candidate))return false;
        var curve=new BezierGroup2d(source);
        if(candidate.Start.DistanceTo(curve.Start)>MinimumGeometryDistance||candidate.End.DistanceTo(curve.End)>MinimumGeometryDistance||candidate.IsClosed!=B(request,"closed",false))return false;
        double error=Math.Max(D(request,"tolerance",DefaultSplineTolerance),D(request,"thinError",LegacySplineThinError));
        var sourceDistance=new CurveDistanceQuery(curve);var candidateDistance=new CurveDistanceQuery(candidate);
        if(!curve.GetPoly(new PositiveDouble(error/SplineRefinementFactor)).Points.All(point=>candidateDistance.Within(point,error))||
           !candidate.GetPoly(new PositiveDouble(error/SplineRefinementFactor)).Points.All(point=>sourceDistance.Within(point,error)))return false;
        result=new SplineData(candidate,source,key);return true;
    }
    static SplineData LoadOrConvertSpline(XElement request,string key)
    {
        var source=Points(request).ToArray();SplineData data;
        if(TryReadSplineCache(key,request,source,out data)) {
            Interlocked.Increment(ref diskCacheHits);
            if(reuseEquivalentSplines) {var cached=data;ShapeSplines.TryAdd(ShapeKey(request,source),new Lazy<SplineData>(()=>cached));}
            return data;
        }
        if(reuseEquivalentSplines) {
            var first=ShapeSplines.GetOrAdd(ShapeKey(request,source),ignored=>new Lazy<SplineData>(()=>ConvertSpline(request,key,source))).Value;
            if(first.Key==key)data=first;
            else if(TryRepositionSpline(first,request,source,key,out data))Interlocked.Increment(ref shapeCacheHits);
            else data=ConvertSpline(request,key,source);
        } else data=ConvertSpline(request,key,source);
        WriteSplineCache(key,data);return data;
    }
    static byte[] Hash(byte[] bytes) {using(var sha=SHA256.Create())return sha.ComputeHash(bytes);}
    static byte[] CacheHash(string key) {return Hash(Encoding.UTF8.GetBytes(key));}
    static string CachePath(byte[] hash) {return Path.Combine(splineCacheDirectory,BitConverter.ToString(hash).Replace("-","")+".bin");}
    static void CacheNote(Exception error)
    {if(Interlocked.Exchange(ref cacheWarning,1)==0)Console.WriteLine("CACHE NOTE|Disk cache unavailable; conversion continues normally: "+error.Message.Replace('\r',' ').Replace('\n',' '));}
    static bool TryReadSplineCache(string key,XElement request,Point2d[] source,out SplineData data)
    {
        data=null;if(splineCacheDirectory==null)return false;string path=CachePath(CacheHash(key));
        try {
            if(!File.Exists(path))return false;
            long length=new FileInfo(path).Length;
            if(length<73||length>73L+MaximumCachedSplinePoints*16L)throw new InvalidDataException("Invalid spline cache entry length.");
            byte[] file=File.ReadAllBytes(path);
            using(var reader=new BinaryReader(new MemoryStream(file))) {
                if(reader.ReadInt32()!=SplineCacheRevision||!reader.ReadBytes(32).SequenceEqual(CacheHash(key)))throw new InvalidDataException("Stale spline cache entry.");
                var digest=reader.ReadBytes(32);var payload=reader.ReadBytes(file.Length-68);
                if(!digest.SequenceEqual(Hash(payload)))throw new InvalidDataException("Spline cache checksum mismatch.");
                using(var values=new BinaryReader(new MemoryStream(payload))) {
                    bool closed=values.ReadBoolean();int count=values.ReadInt32();
                    if(closed!=B(request,"closed",false)||count<2||count>MaximumCachedSplinePoints||payload.Length!=5L+count*16L)throw new InvalidDataException("Invalid spline cache point count.");
                    var points=new Point2d[count];
                    for(int i=0;i<count;i++) {
                        double x=values.ReadDouble(),y=values.ReadDouble();
                        if(double.IsNaN(x)||double.IsInfinity(x)||double.IsNaN(y)||double.IsInfinity(y))throw new InvalidDataException("Nonfinite cached spline point.");
                        points[i]=new Point2d(x,y);
                    }
                    data=new SplineData(points,closed,source,key);return true;
                }
            }
        } catch(Exception error) {
            CacheNote(error);try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}return false;
        }
    }
    static void WriteSplineCache(string key,SplineData data)
    {
        if(splineCacheDirectory==null||data.Points.Length>MaximumCachedSplinePoints)return;
        string path=CachePath(CacheHash(key)),stage=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            if(File.Exists(path))return;
            byte[] payload;
            using(var memory=new MemoryStream()) {
                using(var writer=new BinaryWriter(memory,Encoding.UTF8,true)) {
                    writer.Write(data.Closed);writer.Write(data.Points.Length);foreach(var point in data.Points){writer.Write(point.X);writer.Write(point.Y);}
                }
                payload=memory.ToArray();
            }
            using(var writer=new BinaryWriter(new FileStream(stage,FileMode.CreateNew,FileAccess.Write,FileShare.None))) {
                writer.Write(SplineCacheRevision);writer.Write(CacheHash(key));writer.Write(Hash(payload));writer.Write(payload);
            }
            File.Move(stage,path);
        } catch(Exception error) {if(!File.Exists(path))CacheNote(error);}
        finally {try{if(File.Exists(stage))File.Delete(stage);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
    static void PruneSplineCache()
    {
        if(splineCacheDirectory==null)return;
        try {
            foreach(var file in new DirectoryInfo(splineCacheDirectory).GetFiles("*.tmp")) {
                var parts=file.Name.Split('.');Guid identity;
                if(parts.Length==4&&parts[0].Length==64&&parts[0].All(Uri.IsHexDigit)&&parts[1]=="bin"&&
                    Guid.TryParseExact(parts[2],"N",out identity)&&file.LastWriteTimeUtc<DateTime.UtcNow.AddHours(-CacheTemporaryFileAgeHours))file.Delete();
            }
            var files=new DirectoryInfo(splineCacheDirectory).GetFiles("*.bin").Where(file=>
                Path.GetFileNameWithoutExtension(file.Name).Length==64&&Path.GetFileNameWithoutExtension(file.Name).All(Uri.IsHexDigit)).OrderBy(file=>file.LastWriteTimeUtc).ToArray();
            long size=files.Sum(file=>file.Length),limit=splineCacheMegabytes*1024L*1024L;
            foreach(var file in files) {if(size<=limit)break;long length=file.Length;file.Delete();size-=length;}
        } catch(Exception error) {CacheNote(error);}
    }
    static Spline2d CachedNativeSpline(XElement e)
    {
        SplineData data;
        if(RequestedSplines.TryGetValue(e,out data))return data.Create();
        string key=SplineKey(e);
        if(!ConvertedSplines.TryGetValue(key,out data))ConvertedSplines.Add(key,data=LoadOrConvertSpline(e,key));
        // Native entities are transformed in place later; never share a mutable spline between patterns.
        return data.Create();
    }
    static void PrepareSplines(XElement request)
    {
        var curves=request.Elements("Table").Elements("Pattern").Elements("BezierGroup").Where(e=>B(e,"nativeSpline",false)).ToArray();
        var unique=curves.GroupBy(SplineKey,StringComparer.Ordinal).Select(group=>new {Key=group.Key,Element=group.First(),Elements=group.ToArray()}).ToArray();
        entityProgressStart=unique.Length==0?SplineProgressStart:SplineProgressEnd;
        int workers=int.Parse(S(request,"conversionWorkers",DefaultConversionWorkers.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
        if(workers<1||workers>MaximumConversionWorkers)throw new ArgumentException("Conversion workers must be between 1 and "+MaximumConversionWorkers+".");
        workers=Math.Min(workers,Math.Max(1,unique.Length));
        var results=new SplineData[unique.Length];int finished=0;var progressLock=new object();
        try {
            Parallel.For(0,unique.Length,new ParallelOptions {MaxDegreeOfParallelism=workers},index=> {
                try { results[index]=LoadOrConvertSpline(unique[index].Element,unique[index].Key); }
                catch(Exception error) { throw new InvalidDataException("Spline in pattern '"+S(unique[index].Element.Parent,"name","Rhino pattern")+"': "+error.Message,error); }
                lock(progressLock) {
                    finished++;
                    Progress(SplineProgressStart+finished*(SplineProgressEnd-SplineProgressStart)/Math.Max(1,unique.Length),"Converting native splines "+finished+" / "+unique.Length);
                }
            });
        } catch(AggregateException error) { throw error.Flatten().InnerExceptions[0]; }
        for(int index=0;index<unique.Length;index++) {
            ConvertedSplines.Add(unique[index].Key,results[index]);foreach(var element in unique[index].Elements)RequestedSplines.Add(element,results[index]);
        }
        PruneSplineCache();
        Console.WriteLine("CACHE|splines="+curves.Length+" unique="+unique.Length+" reused="+(curves.Length-unique.Length)+" diskHits="+diskCacheHits+" shapeHits="+shapeCacheHits+" workers="+workers);
    }
    static bool IsValidSpline(Spline2d spline)
    {
        return spline.GetEntity(SplineValidationLayer,NoLayers,null,NoDynamic).IsValid();
    }
    static Spline2d NativeSpline(Point2d[] controls,double tolerance,bool closed,double thinError)
    {
        if(tolerance<=0)throw new ArgumentException("Spline tolerance must be positive inches.");
        var source=new BezierGroup2d(controls);
        double sampling=tolerance/SplineRefinementFactor;
        var sourceSamples=source.GetPoly(new PositiveDouble(sampling)).Points.ToArray();
        double interpolation=Math.Min(NativeDistanceInterpolation,sampling);
        var sourceDistance=new CurveDistanceQuery(source,interpolation);
        Func<Spline2d,bool> matches=candidate=> {
            if(!IsValidSpline(candidate))return false;
            var samples=candidate.GetPoly(new PositiveDouble(tolerance/SplineRefinementFactor)).Points;
            var splineDistance=new CurveDistanceQuery(candidate,interpolation);
            return sourceSamples.All(point=>splineDistance.Within(point,tolerance))&&samples.All(point=>sourceDistance.Within(point,tolerance));
        };
        // The cubic spans of an imported native spline already meet at its original fit points.
        var knots=controls.Where((point,index)=>index%3==0).ToList();
        if(closed&&knots[0].DistanceTo(knots[knots.Count-1])<=MinimumGeometryDistance)knots.RemoveAt(knots.Count-1);
        if(knots.Count>= (closed?3:2)) {
            try {
                var original=new Spline2d(knots,closed);
                if(matches(original))return ThinSpline(source,original,thinError,sourceDistance);
            } catch(ArgumentException) {
                // Arbitrary Rhino cubics need not be fit-point interpolation splines.
            }
        }
        for(int attempt=0;attempt<SplineConversionAttempts;attempt++,sampling/=SplineRefinementFactor) {
            var spline=source.AsSpline(new PositiveDouble(sampling));
            if(spline.IsClosed!=closed)spline=new Spline2d(spline.ControlPoints,closed);
            if(matches(spline))return ThinSpline(source,spline,thinError,sourceDistance);
        }
        throw new InvalidDataException("Could not produce a valid native spline within the document tolerance; try Bezier mode to keep the cubic curve.");
    }
    static Spline2d ThinSpline(BezierGroup2d source,Spline2d spline,double error,CurveDistanceQuery sourceDistance)
    {
        if(error<0)throw new ArgumentException("Curve thinning error must be nonnegative inches.");
        if(error==0)return spline;
        int original=spline.ControlPoints.Count();
        var sourcePoints=source.GetPoly(new PositiveDouble(error/SplineRefinementFactor)).Points.ToArray();
        double trialError=error;
        for(int attempt=0;attempt<SplineConversionAttempts;attempt++,trialError/=SplineRefinementFactor)
        {
            var candidate=spline.Thin(new PositiveDouble(trialError));
            if(!IsValidSpline(candidate)||candidate.IsClosed!=spline.IsClosed||
                candidate.Start.DistanceTo(spline.Start)>MinimumGeometryDistance||candidate.End.DistanceTo(spline.End)>MinimumGeometryDistance)continue;
            if(candidate.ControlPoints.Count()>=original)return spline;
            var points=candidate.GetPoly(new PositiveDouble(error/SplineRefinementFactor)).Points;
            var candidateDistance=new CurveDistanceQuery(candidate);
            if(sourcePoints.All(point=>candidateDistance.Within(point,error))&&points.All(point=>sourceDistance.Within(point,error)))
            {
                Console.WriteLine("SPLINE THIN points="+original+"->"+candidate.ControlPoints.Count()+" maxErrorInches="+error.ToString("R",CultureInfo.InvariantCulture));
                return candidate;
            }
        }
        Console.WriteLine("SPLINE THIN kept original to preserve endpoints and requested error.");
        return spline;
    }
    static bool IsEditableText(Entity entity) { return entity is TextEntity || entity is FontEntity; }

    static DiscreteEntity TextFromRequest(XElement e,Layer layer,Point2d center,double rotation)
    {
        double height=D(e,"height",DefaultTextHeight);
        if(height<=0)throw new ArgumentException("Text height must be positive.");
        string family=S(e,"fontFamily","");
        if(String.IsNullOrWhiteSpace(family))return new TextEntity(layer,NoLayers,center,rotation,e.Value,height,D(e,"characterRotation",0));
        var font=new System.Drawing.Font(family,NativeFontPointSize,System.Drawing.FontStyle.Regular,System.Drawing.GraphicsUnit.Point);
        try {
            var text=new FontEntity(layer,NoLayers,center,rotation,e.Value,font,height,new Point2d(1,1));
            return text.WithHeight(height);
        }
        catch {font.Dispose();throw;}
    }

    static IEnumerable<Entity> EntitiesFromRequest(XElement e)
    {
        if(e.Name.LocalName.Equals("Text",StringComparison.OrdinalIgnoreCase) && B(e,"mirrorY",false)) {
            Layer layer=ToolLayer(e);
            // TextEntity.Transform discards reflections and regenerates upright
            // glyphs. Preserve mirrored labels as native font stroke geometry.
            var text=TextFromRequest(e,layer,new Point2d(0,0),0);
            var transform=new Transform2d();
            transform.Scale(1,-1);transform.Rotate(D(e,"rotation",0));transform.Translate(P(e));
            int count=0;
            foreach(var primitive in text.AbsolutePrimitives) {
                Entity stroke=primitive.Clone();
                if(!stroke.Transform(transform))throw new InvalidOperationException("Could not mirror native text stroke.");
                var continuous=stroke as ContinuousEntity;
                if(continuous!=null && B(e,"excludeFromPerimeter",true))stroke=continuous.With(null,null,null,true,null);
                stroke=PreserveShortNativeLine(stroke);
                if(!stroke.IsValid())throw new InvalidDataException("Mirrored native text stroke is invalid.");
                count++;yield return stroke;
            }
            Console.WriteLine("TEXT MIRROR FLATTENED native-strokes="+count);
        } else yield return EntityFromRequest(e);
    }
    static List<Entity> ReplaceWithNativeEffects(XElement pattern,List<Tuple<string,Entity>> flat,out bool incomplete)
    {
        var replaced=new HashSet<string>(StringComparer.Ordinal);
        var requested=new HashSet<string>(S(pattern,"effectOrigins","").Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
        var native=new List<Entity>();
        var referenceRequests=pattern.Elements().Where(node=>node.Name=="BezierGroup"&&B(node,"nativeSpline",false))
            .ToLookup(node=>S(node,"origin",""));
        var references=new Dictionary<string,Entity[]>(StringComparer.Ordinal);
        foreach(var recipe in pattern.Elements("EffectSet")) {
            var ids=new HashSet<string>(S(recipe,"replace","").Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries),StringComparer.Ordinal);
            requested.UnionWith(ids);
            if(ids.Count==0||ids.Overlaps(replaced))continue;
            var expected=flat.Where(item=>ids.Contains(item.Item1)).ToArray();
            if(expected.Select(item=>item.Item1).Distinct().Count()!=ids.Count)continue;
            List<Entity> candidate;
            try {
                // Compare against the prepared path before display/editing splines are thinned.
                var validation=expected.GroupBy(item=>item.Item1,StringComparer.Ordinal).SelectMany(group=>{
                    Entity[] unthinned;
                    if(!references.TryGetValue(group.Key,out unthinned))
                        references[group.Key]=unthinned=referenceRequests[group.Key].Select(node=>EntityFromRequest(node,true)).ToArray();
                    return group.Where(item=>!(item.Item2 is SplineEntity)||unthinned.Length==0)
                        .Concat(unthinned.Select(curve=>Tuple.Create(group.Key,curve)));
                }).ToArray();
                candidate=TryNativeEffectSet(recipe,validation);
            } catch(Exception error) {
                Console.WriteLine("Native effect reference declined: "+error.Message);
                continue;
            }
            if(candidate==null)continue;
            replaced.UnionWith(ids);
            native.AddRange(candidate);
        }
        incomplete=!requested.IsSubsetOf(replaced);
        if(native.Count==0)return null;
        var curves=native.OfType<ContinuousEntity>().ToArray();
        Console.WriteLine("NATIVE EFFECTS|"+S(pattern,"name","Rhino pattern")+"|seams="+curves.Count(curve=>curve.HasSeam)+
            " notches="+curves.Sum(curve=>curve.DynamicEntities.OfType<NativeNotch>().Count()));
        native.AddRange(flat.Where(item=>!replaced.Contains(item.Item1)).Select(item=>item.Item2.Clone()));
        return native;
    }

    static List<Entity> TryNativeEffectSet(XElement recipe,Tuple<string,Entity>[] expected)
    {
        double tolerance=D(recipe,"tolerance",DefaultSplineTolerance);
        if(tolerance<=0)return null;
        bool hasSeam=recipe.Elements("Source").Any(node=>node.Element("Seam")!=null);
        var styles=new[]{SeamStyle.Square,SeamStyle.Extend,SeamStyle.Slant,SeamStyle.Mirror,SeamStyle.Return};
        var sourceItems=recipe.Elements("Source").ToArray();
        var sourceGeometry=sourceItems.Select(item=>item.Elements().Where(node=>node.Name!="Seam"&&node.Name!="Notch").ToArray()).ToArray();
        var stylePairs=styles.SelectMany(start=>styles.Select(end=>Tuple.Create(start,end)))
            .OrderBy(pair=>pair.Item1==pair.Item2?0:1);
        bool authored=hasSeam&&sourceItems.Where(item=>item.Element("Seam")!=null).All(item=>item.Element("Seam").Attribute("StartStyle")!=null);
        bool openSeam=hasSeam&&!authored&&sourceItems.Any(item=>item.Element("Seam")!=null &&
            item.Elements().Any(node=>node.Name=="Line"||node.Name=="Arc"||
                (node.Name=="Polyline"||node.Name=="BezierGroup")&&!B(node,"closed",false)));
        var bases=sourceGeometry.Select(geometry=>geometry
            .Select(element=> {
                if(element.Name!="BezierGroup"||!B(element,"nativeSpline",false))return EntityFromRequest(element) as ContinuousEntity;
                var source=new XElement(element);
                source.SetAttributeValue("thinError",0);
                double fit=D(source,"tolerance",DefaultSplineTolerance);
                double tightFit=Math.Min(fit,tolerance/NativeEffectSplineFitDivisor);
                if(tightFit<fit) {
                    source.SetAttributeValue("tolerance",tightFit);
                    try { return EntityFromRequest(source) as ContinuousEntity; }
                    catch(InvalidDataException) { source.SetAttributeValue("tolerance",fit); }
                }
                return EntityFromRequest(source) as ContinuousEntity;
            }).ToArray()).ToArray();
        if(bases.Any(sources=>sources.Length==0||sources.Any(source=>source==null)))
            throw new InvalidDataException("A native effect requires continuous source geometry.");
        // On a guide-only tool, seam style and side cannot change the original sew line.
        if(hasSeam&&!sourceItems.Any(item=>item.Element("Seam")==null&&
            item.Elements("Notch").Any(notch=>ImportNotchExecution(notch)==NativeNotchExecution.Inline))) {
            var effectLayers=new HashSet<Guid>(sourceItems.SelectMany(item=>item.Elements())
                .Where(node=>node.Name=="Seam"||node.Name=="Notch").Select(node=>ToolLayer(node).Id));
            foreach(var guides in bases.SelectMany(sources=>sources).Where(source=>!effectLayers.Contains(source.Layer.Id))
                .GroupBy(source=>source.Layer.Id)) {
                if(!NativeGeometryMatches(guides,expected.Select(item=>item.Item2).Where(entity=>entity.Layer.Id==guides.Key),tolerance)) {
                    Console.WriteLine("Native effect candidate declined: The sew path differs from the prepared geometry.");
                    return null;
                }
            }
        }
        string failure=null;
        foreach(var style in openSeam?stylePairs:stylePairs.Take(1))
        foreach(int sign in hasSeam&&!authored?new[]{1,-1}:new[]{1}) {
            try {
                var result=BuildNativeEffectGeometry(sourceItems,bases,expected,tolerance,style,sign,authored);
                if(NativeGeometryMatches(ExpandNativeEffects(result),expected.Select(item=>item.Item2),tolerance))
                    return CompactNativeEffectGeometry(sourceItems,sourceGeometry,bases,result,expected,tolerance,style,sign,authored);
                failure="Its generated paths differ from the prepared geometry or tools.";
            } catch(Exception error) {
                if(failure==null)failure=error.Message;
            }
        }
        if(failure!=null)Console.WriteLine("Native effect candidate declined: "+failure);
        return null;
    }

    static List<Entity> BuildNativeEffectGeometry(XElement[] items,ContinuousEntity[][] bases,Tuple<string,Entity>[] expected,
        double tolerance,Tuple<SeamStyle,SeamStyle> style,int sign,bool authored)
    {
        var result=new List<Entity>();
        for(int index=0;index<items.Length;index++) {
            var item=items[index];
            var sources=bases[index].Select(source=>(ContinuousEntity)source.Clone()).ToArray();
            var allowance=item.Element("Seam");
            if(allowance!=null) {
                var layer=ToolLayer(allowance);
                var seam=new Seam(layer,sign*D(allowance,"Width",0),
                    authored?ReadEffectChoice<SeamStyle>(allowance,"StartStyle"):style.Item1,
                    authored?ReadEffectChoice<SeamStyle>(allowance,"EndStyle"):style.Item2);
                for(int i=0;i<sources.Length;i++)
                    sources[i]=sources[i].With(null,seam,null,ToolMap[layer].ProcessType==ProcessType.Cut?(bool?)false:null,null);
            }
            var dynamics=sources.Select(_=>new List<DynamicEntity>()).ToArray();
            foreach(var request in item.Elements("Notch")) {
                bool placed=false;
                for(int i=0;i<sources.Length;i++) {
                    var notch=CreateExportNotch(sources[i],request,expected,tolerance);
                    if(notch==null)continue;
                    dynamics[i].Add(notch);placed=true;break;
                }
                if(!placed)throw new InvalidDataException("The editable notch does not match its prepared strokes.");
            }
            for(int i=0;i<sources.Length;i++) {
                var source=sources[i];
                if(dynamics[i].Count>0)source=source.With(dynamicEntities:new ReadOnlyCollection<DynamicEntity>(dynamics[i]));
                if(!source.IsValid())throw new InvalidDataException("An editable effect made its source invalid.");
                result.Add(source);
            }
        }
        return result;
    }

    static List<Entity> CompactNativeEffectGeometry(XElement[] items,XElement[][] geometry,ContinuousEntity[][] bases,
        List<Entity> verified,Tuple<string,Entity>[] expected,double tolerance,Tuple<SeamStyle,SeamStyle> style,int sign,bool authored)
    {
        var comparison=expected.Select(item=>item.Item2).ToArray();
        for(int i=0;i<bases.Length;i++)
        for(int j=0;j<bases[i].Length;j++) {
            var source=bases[i][j] as SplineEntity;
            if(source==null)continue;
            var spline=source.Geometry;
            int count=spline.ControlPoints.Count();
            if(count<=NativeEffectCompactPointThreshold)continue;
            double requested=D(geometry[i][j],"thinError",LegacySplineThinError);
            if(requested<=0)continue;
            Func<Spline2d,bool> accept=thin=> {
                bool accepted=false;
                try {
                    int reduced=thin.ControlPoints.Count();
                    if(reduced>=count||thin.IsClosed!=spline.IsClosed||
                        thin.Start.DistanceTo(spline.Start)>MinimumGeometryDistance||thin.End.DistanceTo(spline.End)>MinimumGeometryDistance)
                        return false;
                    var candidate=thin.GetEntity(source.Layer,NoLayers,null,NoDynamic) as ContinuousEntity;
                    if(candidate==null||!candidate.IsValid())return false;
                    if(requested<tolerance&&!NativeGeometryMatches(new[]{candidate},new[]{source},requested))return false;
                    bases[i][j]=candidate.With(null,null,null,source.ExcludeFromPerimeter,null);
                    var trial=BuildNativeEffectGeometry(items,bases,expected,tolerance,style,sign,authored);
                    if(!NativeGeometryMatches(ExpandNativeEffects(trial),comparison,tolerance))return false;
                    verified=trial;accepted=true;
                    Console.WriteLine("NATIVE EFFECT SPLINE THIN points="+count+"->"+reduced);
                    return true;
                } catch(InvalidDataException) {
                    // A thinner spline may move its offset or dynamic notch beyond tolerance.
                } catch(ArgumentException) {
                    // The SDK can reject a degenerate thinned result.
                } catch(InvalidOperationException) {
                    // A candidate can invalidate a native join even when the source spline is valid.
                } finally {
                    if(!accepted)bases[i][j]=source;
                }
                return false;
            };

            var anchors=items[i].Elements("Notch").Select(node=>P(node.Element("P"))).ToArray();
            if(anchors.Length>0 && CompactSplineAtNotches(spline,anchors,tolerance,accept))continue;
            double error=Math.Min(requested,tolerance);
            for(int attempt=0;attempt<NativeEffectCompactAttempts;attempt++,error/=2) {
                try { if(accept(spline.Thin(new PositiveDouble(error))))break; }
                catch(ArgumentException) { }
                catch(InvalidOperationException) { }
            }
        }
        return verified;
    }

    static bool CompactSplineAtNotches(Spline2d spline,Point2d[] anchors,double tolerance,Func<Spline2d,bool> accept)
    {
        var points=spline.ControlPoints.ToArray();
        var locked=new HashSet<int>();
        var guide=new CurveDistanceQuery(spline.GetPoly(new PositiveDouble(tolerance/NativeEffectSamplingDivisor)).Points.ToArray());
        foreach(var anchor in anchors) {
            if(!guide.Within(anchor,tolerance))continue;
            int closest=0;
            for(int i=1;i<points.Length;i++)
                if(points[i].DistanceTo(anchor)<points[closest].DistanceTo(anchor))closest=i;
            for(int i=closest-NativeEffectNotchKnotRadius;i<=closest+NativeEffectNotchKnotRadius;i++)
                if(spline.IsClosed)locked.Add((i+points.Length)%points.Length);
                else if(i>=0&&i<points.Length)locked.Add(i);
        }
        if(locked.Count==0)return false;
        for(int i=0;i<NativeEffectEndpointKnots;i++) {
            locked.Add(i);locked.Add(points.Length-i-1);
        }
        // SDK Thin can resample around a notch. Retaining its original neighbors keeps that local tangent.
        foreach(int stride in NativeEffectKnotStrides) {
            try {
                var sparse=new Spline2d(points.Where((point,i)=>i%stride==0||locked.Contains(i)),spline.IsClosed);
                if(accept(sparse))return true;
            } catch(ArgumentException) { }
        }
        return false;
    }

    static NativeNotch CreateExportNotch(ContinuousEntity source,XElement request,Tuple<string,Entity>[] flat,double tolerance)
    {
        var guide=source.HasSeam?source.With(seam:Seam.EmptySeam):source;
        var anchor=P(request.Element("P"));
        if(guide.ClosestCurvePoint(anchor).DistanceTo(anchor)>tolerance &&
            !new CurveDistanceQuery(guide.GetPoly(new PositiveDouble(tolerance/NativeEffectSamplingDivisor)).Points.ToArray())
                .Within(anchor,tolerance))
            return null;
        double length=guide.GetArcLength(),distance=guide.GetNotchDistance(anchor);
        if(length<=MinimumGeometryDistance||distance<-tolerance||distance>length+tolerance)
            return null;
        var effect=new XElement(request);
        effect.SetAttributeValue("Placement","Start");
        distance=Math.Max(0,Math.Min(length,distance));
        bool fractional=S(effect,"DistanceUnits","Native")=="Fractional";
        var layer=ToolLayer(effect);
        var ids=new HashSet<string>(S(request,"pieces","").Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries),StringComparer.Ordinal);
        var expected=flat.Where(item=>ids.Contains(item.Item1)).Select(item=>item.Item2).ToArray();
        if(expected.Length==0)return null;
        Point2d target=StrokeCenter(expected);
        double low=Math.Max(0,distance-tolerance*NativeNotchStationRefinementReach);
        double high=Math.Min(length,distance+tolerance*NativeNotchStationRefinementReach);
        Func<double,NativeNotch> at=station=> {
            effect.SetAttributeValue("Distance",fractional?station/length:station);
            return ReadImportNotch(effect,source,NativeNotchExecution.Discrete,layer);
        };
        string preferred=S(request,"Side","Internal");
        foreach(var side in new[]{preferred,preferred=="Internal"?"External":"Internal"}) {
            effect.SetAttributeValue("Side",side);
            double station=distance;
            for(int step=0;step<=NativeNotchStationRefinementAttempts;step++) {
                var probe=at(station);
                var strokes=source.GetDynamicEntityPrimitives(probe).ToArray();
                if(NativeGeometryMatches(strokes,expected,tolerance))
                    return ImportNotchExecution(effect)==NativeNotchExecution.Discrete?probe:
                        ReadImportNotch(effect,source,NativeNotchExecution.Inline,layer);
                if(step==NativeNotchStationRefinementAttempts||strokes.Length==0)break;
                double sample=station+tolerance<=high?station+tolerance:Math.Max(low,station-tolerance);
                if(Math.Abs(sample-station)<=MinimumGeometryDistance)break;
                var neighbor=source.GetDynamicEntityPrimitives(at(sample)).ToArray();
                if(neighbor.Length==0)break;
                // Correct PS's approximate arc-length lookup using the motion of the actual notch strokes.
                Point2d from=StrokeCenter(strokes),to=StrokeCenter(neighbor);
                double x=to.X-from.X,y=to.Y-from.Y,squared=x*x+y*y;
                if(squared<=MinimumGeometryDistance*MinimumGeometryDistance)break;
                double next=station+(sample-station)*((target.X-from.X)*x+(target.Y-from.Y)*y)/squared;
                next=Math.Max(low,Math.Min(high,next));
                if(Math.Abs(next-station)<=MinimumGeometryDistance)break;
                station=next;
            }
        }
        return null;
    }

    static Point2d StrokeCenter(IEnumerable<Entity> strokes)
    {
        var bounds=strokes.Select(stroke=>stroke.GetBounds()).ToArray();
        return new Point2d((bounds.Min(box=>box.Min.X)+bounds.Max(box=>box.Max.X))/2,
            (bounds.Min(box=>box.Min.Y)+bounds.Max(box=>box.Max.Y))/2);
    }

    static IEnumerable<Entity> ExpandNativeEffects(IEnumerable<Entity> entities)
    {
        var items=entities.ToArray();
        var seamed=items.OfType<ContinuousEntity>().Where(entity=>entity.HasSeam).ToArray();
        var chain=seamed.Length<2?null:new EntityCollection(seamed).GetGroupedEntities(PerimeterOptions.OnlyContinuous);
        var pathOptions=new PerimeterPathOptions();
        foreach(var entity in items) {
            var source=entity as ContinuousEntity;
            if(source==null) {yield return entity;continue;}
            var notches=source.DynamicEntities.OfType<NativeNotch>().ToArray();
            bool inline=notches.Any(notch=>notch.ExecutionStyle==NativeNotchExecution.Inline);
            if(source.HasSeam||!inline)yield return entity;
            if(source.HasSeam)
                foreach(var cut in ReadImportAllowance(source,chain,inline)) {
                    if(cut==null||!cut.IsValid())throw new InvalidDataException("Could not generate the native cutting edge.");
                    yield return cut;
                }
            else if(inline)
                foreach(var cut in source.GetSplicedDynamics(ContinuousEntity.GetInlineDynamicPrimitiveConstructors(
                    source,source.With(dynamicEntities:NoDynamic),pathOptions)))yield return cut;
            foreach(var notch in notches.Where(notch=>notch.ExecutionStyle==NativeNotchExecution.Discrete))
                foreach(var primitive in source.GetDynamicEntityPrimitives(notch))yield return primitive;
        }
    }

    static bool NativeGeometryMatches(IEnumerable<Entity> candidate,IEnumerable<Entity> prepared,double tolerance)
    {
        var first=candidate.ToArray();var second=prepared.ToArray();
        var a=first.OfType<ContinuousEntity>().ToArray();var b=second.OfType<ContinuousEntity>().ToArray();
        if(a.Length==0||b.Length==0||a.Length!=first.Length||b.Length!=second.Length)return false;
        double step=tolerance/NativeEffectSamplingDivisor;
        var sampling=new PositiveDouble(step);
        double allowance=tolerance-2*step;
        var expected=b.ToLookup(curve=>curve.Layer.Id);
        foreach(var group in a.GroupBy(curve=>curve.Layer.Id)) {
            var left=group.ToArray();var right=expected[group.Key].ToArray();
            if(right.Length==0 || Math.Abs(left.Sum(curve=>curve.GetArcLength())-right.Sum(curve=>curve.GetArcLength()))>
                tolerance*Math.Max(left.Length,right.Length) ||
                !EquivalentEffectCoverage(left,right,sampling,allowance))return false;
        }
        return expected.Count==a.Select(curve=>curve.Layer.Id).Distinct().Count();
    }

    static bool EquivalentEffectCoverage(ContinuousEntity[] a,ContinuousEntity[] b,PositiveDouble sampling,double tolerance)
    {
        var first=a.Select(curve=>curve.GetPoly(sampling).Points.ToArray()).ToArray();
        var second=b.Select(curve=>curve.GetPoly(sampling).Points.ToArray()).ToArray();
        return CoversNativeEffects(first,second,tolerance)&&CoversNativeEffects(second,first,tolerance);
    }

    static bool CoversNativeEffects(Point2d[][] from,Point2d[][] to,double tolerance)
    {
        var queries=to.Select(path=>new CurveDistanceQuery(path)).ToArray();
        foreach(var points in from) {
            if(points.Length<2)return false;
            for(int i=0;i<points.Length;i++) {
                var point=points[i];
                if(!queries.Any(target=>target.Within(point,tolerance)))return false;
                if(i==0)continue;
                var previous=points[i-1];
                var middle=new Point2d((previous.X+point.X)/2,(previous.Y+point.Y)/2);
                if(!queries.Any(target=>target.Within(middle,tolerance)))return false;
            }
        }
        return true;
    }

    static Pattern PatternFromRequest(XElement e,out Point2d copyLocation,bool clipboard)
    {
        string name=S(e,"name",S(e,"id","Rhino pattern"));
        var entities=new List<Entity>();
        var flat=e.Element("EffectSet")==null?null:new List<Tuple<string,Entity>>();
        int ordinal=0;var requests=e.Elements().Where(node=>node.Name!="EffectSet").ToArray();
        foreach(var requested in requests) {
            ordinal++;
            Progress(entityProgressStart+(totalEntities==0?0:completedEntities*(EntityProgressEnd-entityProgressStart)/totalEntities),"Converting "+name+": entity "+ordinal+" / "+requests.Length);
            try {
                foreach(var entity in EntitiesFromRequest(requested)) {
                    entities.Add(entity);
                    if(flat!=null)flat.Add(Tuple.Create(S(requested,"origin",""),entity));
                }
            }
            catch(Exception error) {
                throw new ArgumentException("Pattern '"+name+"' ("+S(e,"id","unknown")+"), entity "+ordinal+" ("+requested.Name+"): "+error.Message,error);
            }
            completedEntities++;
        }
        bool incomplete=flat==null&&e.Attribute("effectOrigins")!=null;
        var native=flat==null?null:ReplaceWithNativeEffects(e,flat,out incomplete);
        Exception nativeError=null;
        if(native!=null) {
            try {
                bool ignored;
                var pattern=AssemblePattern(e,native.Select(entity=>entity.Clone()).ToList(),out copyLocation,clipboard,false,out ignored);
                if(incomplete)Console.WriteLine("NATIVE EFFECT FALLBACK|"+name+"|Some editable effects did not match the prepared curves.");
                return pattern;
            }
            catch(Exception error) { nativeError=error; }
        }
        bool openCut;
        var prepared=AssemblePattern(e,entities,out copyLocation,clipboard,true,out openCut);
        if(openCut&&native!=null) {
            // An already-open path can keep verified native effects without becoming a nestable perimeter.
            try {
                Point2d nativeLocation;bool nativeOpen;
                var candidate=AssemblePattern(e,native,out nativeLocation,clipboard,true,out nativeOpen);
                if(nativeOpen) {prepared=candidate;copyLocation=nativeLocation;nativeError=null;}
            } catch(Exception error) { nativeError=error; }
        }
        if(!clipboard&&openCut)Console.WriteLine("OPEN CUT PATH|"+name.Replace('\r',' ').Replace('\n',' '));
        if(nativeError!=null||incomplete)Console.WriteLine("NATIVE EFFECT FALLBACK|"+name+"|"+
            (nativeError==null?"Some editable effects did not match the prepared curves.":nativeError.Message));
        return prepared;
    }
    static Pattern AssemblePattern(XElement e,List<Entity> entities,out Point2d copyLocation,bool clipboard,bool allowOpenCutPaths,out bool openCut)
    {
        openCut=false;
        string name=S(e,"name",S(e,"id","Rhino pattern"));
        if (entities.Count == 0) throw new ArgumentException("Pattern has no entities.");
        if(B(e,"annotation",false)&&B(e,"fitAnnotation",false))FitTableAnnotation(entities);
        var bounds = entities.Select(x => x.GetBounds()).ToList();
        double minX=bounds.Min(b=>b.Min.X), minY=bounds.Min(b=>b.Min.Y), maxX=bounds.Max(b=>b.Max.X), maxY=bounds.Max(b=>b.Max.Y);
        var center = new Point2d((minX+maxX)/2, (minY+maxY)/2);
        double rotation=B(e,"annotation",false)?DefaultSourceRotation:D(e,"sourceRotation",DefaultSourceRotation);
        var shift = new Transform2d(); shift.Translate(-center.X, -center.Y);
        // PatternSmith copies reflect after rotating; reverse those operations here.
        bool flipped=B(e,"sourceFlipped",false);
        if(flipped)shift.Scale(1,-1);
        if(rotation!=0)shift.Rotate(-rotation);
        // Entity.Transform returns bool and mutates each native entity in place.
        for(int index=0;index<entities.Count;index++) {
            if(!entities[index].Transform(shift))throw new InvalidOperationException("Could not translate native entity into pattern coordinates.");
            // Translation can move a boundary-length line across the native
            // rounding threshold; validate its final local representation too.
            entities[index]=PreserveShortNativeLine(entities[index]);
        }
        var pattern = new Pattern(entities, PerimeterType.Derived, center, rotation, name);
        string perimeter=S(e,"perimeter",DefaultPerimeter);
        if(!new[]{"Auto","Derived","Element","Box"}.Any(p=>p.Equals(perimeter,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Pattern perimeter must be Auto, Derived, Element or Box.");
        bool cutting=entities.OfType<ContinuousEntity>().Any(entity=>!entity.ExcludeFromPerimeter&&
            (ToolMap[entity.Layer].ProcessType==ProcessType.Cut || entity.HasSeam&&ToolMap[entity.Seam.Layer].ProcessType==ProcessType.Cut));
        bool autoPerimeter=perimeter.Equals("Auto",StringComparison.OrdinalIgnoreCase);
        if(perimeter.Equals("Box",StringComparison.OrdinalIgnoreCase)||autoPerimeter&&!cutting)
            pattern=new Pattern(entities,PerimeterType.Box,center,rotation,name);
        else {
            try { ValidateDerivedPerimeter(pattern); }
            catch(InvalidDataException) {
                if(!allowOpenCutPaths||!autoPerimeter||!clipboard&&pattern.ImportedPatternHasValidPerimeter())throw;
                // This is a workspace and placement wrapper only; no rectangular cutting curve is created.
                pattern=new Pattern(entities,PerimeterType.Box,center,rotation,name);
                openCut=true;
            }
        }
        // Existing table placements are always explicit copies; auto-nesting is optional.
        pattern=pattern.With(allowNesting:!B(e,"annotation",false)&&B(e,"allowNesting",DefaultAllowNesting)&&
            !openCut&&(!clipboard||pattern.PerimeterType!=PerimeterType.Box));
        if(e.Attribute("copies")!=null) {
            double step=D(e,"rotationStep",-1);
            pattern=pattern.With(constraints:pattern.Constraints.With(
                quota:new QuotaConstraint((int)D(e,"copies",1),1,false),
                rotation:step<0?RotationConstraint.FullRotation:new RotationConstraint(new NonnegativeDouble(step))));
        }
        // Pattern recenters its local entities; reflect the resulting adjustment
        // around the original center to keep a mirrored copy from translating.
        copyLocation=flipped?new Point2d(pattern.Location.X,2*center.Y-pattern.Location.Y):pattern.Location;
        Console.WriteLine("PATTERN " + name + " native=" + pattern.PerimeterType + " center=" + center + " entities=" + entities.Count);
        return pattern;
    }
    static bool PatternHasMaterial(XElement pattern,XElement table)
    {
        return !B(pattern,"annotation",false)&&B(pattern,"materialAssigned",B(pattern,"enforceMaterial",B(table,"materialAssigned",DefaultMaterialAssigned)));
    }
    static void FitTableAnnotation(List<Entity> entities)
    {
        var originalTexts=entities.Where(IsEditableText).Cast<DiscreteEntity>().ToArray();if(originalTexts.Length==0)return;
        double angle=originalTexts[0].Rotation;
        var local=new Transform2d();local.Rotate(-angle);
        var world=new Transform2d();world.Rotate(angle);
        var copies=entities.Select(entity=>entity.Clone()).ToList();
        foreach(var entity in copies)if(!entity.Transform(local))throw new InvalidOperationException("Could not measure exported table information.");
        var frames=copies.OfType<PolyEntity>().Where(entity=>entity.IsClosed&&!entity.ExcludeFromPerimeter).ToArray();
        if(frames.Length!=1)throw new InvalidDataException("Exported table information needs one rectangular frame.");
        var frame=frames[0];var area=frame.GetBounds();var points=frame.GetPoly(new PositiveDouble(AnnotationRowToleranceInches)).Points.ToArray();
        if(points.Length!=5||points.Any(point=>
            Math.Min(Math.Abs(point.X-area.Min.X),Math.Abs(point.X-area.Max.X))>AnnotationRowToleranceInches||
            Math.Min(Math.Abs(point.Y-area.Min.Y),Math.Abs(point.Y-area.Max.Y))>AnnotationRowToleranceInches))
            throw new InvalidDataException("Exported table information has a nonrectangular frame.");
        var texts=copies.Where(IsEditableText).Cast<DiscreteEntity>().ToArray();var boxes=texts.Select(text=>text.GetBounds()).ToArray();
        var anchors=texts.Select(text=>text.Center).ToArray();
        // Retain the existing visible inset, measuring the final native glyphs rather than the earlier Rhino name.
        double padding=boxes.SelectMany(box=>new[]{box.Min.X-area.Min.X,area.Max.X-box.Max.X,box.Min.Y-area.Min.Y,area.Max.Y-box.Max.Y})
            .Where(value=>value>AnnotationRowToleranceInches).DefaultIfEmpty(AnnotationRowToleranceInches).Min();
        var rows=new List<List<int>>();
        foreach(int index in Enumerable.Range(0,texts.Length).OrderBy(index=>anchors[index].Y).ThenBy(index=>anchors[index].X)) {
            var row=rows.FirstOrDefault(items=>Math.Abs(anchors[items[0]].Y-anchors[index].Y)<=AnnotationRowToleranceInches);
            if(row==null){row=new List<int>();rows.Add(row);}row.Add(index);
        }
        var widths=boxes.Select((box,index)=>2*Math.Max(anchors[index].X-box.Min.X,box.Max.X-anchors[index].X)).ToArray();
        double width=area.Max.X-area.Min.X;
        foreach(var row in rows) {
            double required=2*padding*row.Count;foreach(int index in row)required+=widths[index];
            width=Math.Max(width,required);
        }
        var centers=new double[rows.Count];double top=Double.NegativeInfinity,bottom=Double.PositiveInfinity;
        for(int at=0;at<rows.Count;at++) {
            var row=rows[at];double lower=row.Min(index=>boxes[index].Min.Y-anchors[index].Y),upper=row.Max(index=>boxes[index].Max.Y-anchors[index].Y);
            centers[at]=Math.Max(anchors[row[0]].Y,top+2*padding-lower);top=centers[at]+upper;bottom=Math.Min(bottom,centers[at]+lower);
        }
        double shift=area.Max.Y-padding-top,right=area.Min.X+width;
        for(int at=0;at<rows.Count;at++) {
            var row=rows[at];double edge=right-padding;
            for(int item=row.Count-1;item>=0;item--) {
                int index=row[item];double x=row.Count==1?area.Min.X+width/2:item==0?area.Min.X+padding+widths[index]/2:edge-widths[index]/2;
                copies[copies.IndexOf(texts[index])]=texts[index].WithCenter(new Point2d(x,centers[at]+shift));
                edge=x-widths[index]/2-2*padding;
            }
        }
        double lowerEdge=Math.Min(area.Min.Y,bottom+shift-padding);
        copies[copies.IndexOf(frame)]=new Poly2d(new[]{new Point2d(area.Min.X,lowerEdge),new Point2d(right,lowerEdge),new Point2d(right,area.Max.Y),new Point2d(area.Min.X,area.Max.Y),new Point2d(area.Min.X,lowerEdge)})
            .GetEntity(frame.Layer,frame.SecondaryLayers,null,NoDynamic);
        foreach(var entity in copies)if(!entity.Transform(world))throw new InvalidOperationException("Could not position exported table information.");
        entities.Clear();entities.AddRange(copies);
    }
    static void ValidateDerivedPerimeter(Pattern pattern)
    {
        if(pattern.PerimeterType!=PerimeterType.Derived)return;
        if(ValidatedPerimeters.Contains(pattern.Entities))return;
        if(!pattern.ImportedPatternHasValidPerimeter())throw new InvalidDataException("Pattern '"+pattern.Name+"' has an open or disconnected cutting perimeter; a boxed replacement was not exported.");
        Autometrix.Core.Entities.Cycles.EntityGroupResult groups;
        if(pattern.HasInvalidDerivedPerimeter(out groups))
        {
            var outside=pattern.GetInternalEntitiesOutsideDerivedPerimeter();
            if(outside.Count>0&&outside.All(entity=>IsEditableText(entity)&&
                (ToolMap[entity.Layer].ProcessType==ProcessType.Reference||ToolMap[entity.Layer].ProcessType==ProcessType.Mark||
                 ToolMap[entity.Layer].ProcessType==ProcessType.Inkjet||ToolMap[entity.Layer].ProcessType==ProcessType.Print)))
                Console.WriteLine("OUTSIDE LABEL|"+pattern.Name+"|Non-cutting text retained; native nesting clearance may include its bounds.");
            else {
                string reason=outside.Count==0?"invalid cutting perimeter":String.Join(", ",outside.Take(MaximumPerimeterFailureDetails).Select(entity=>
                    entity.Layer.Name+" "+entity.GetType().Name+" #"+(pattern.Entities.ToList().IndexOf(entity)+1)))+" outside the cutting perimeter";
                throw new InvalidDataException("Pattern '"+pattern.Name+"': "+reason+"; a boxed replacement was not exported.");
            }
        }
        pattern.GetNestingPerimeter();
        ValidatedPerimeters.Add(pattern.Entities);
    }
    static System.Windows.Media.Imaging.BitmapSource MakePreview(PresentationProject project)
    {return MakePreview(project.Patterns);}
    static System.Windows.Media.Imaging.BitmapSource MakePreview(IEnumerable<Pattern> patterns)
    {
        var placed=patterns.SelectMany(pattern=>WorkspaceEntities(pattern).Select(entity=>new {Entity=entity,Origin=pattern.Location,Bounds=entity.GetBounds()})).ToArray();
        double minX=placed.Min(item=>item.Bounds.Min.X+item.Origin.X),minY=placed.Min(item=>item.Bounds.Min.Y+item.Origin.Y);
        double maxX=placed.Max(item=>item.Bounds.Max.X+item.Origin.X),maxY=placed.Max(item=>item.Bounds.Max.Y+item.Origin.Y);
        double available=PreviewSizePixels-2*PreviewMarginPixels;
        double scale=available/Math.Max(MinimumGeometryDistance,Math.Max(maxX-minX,maxY-minY));
        double left=(PreviewSizePixels-(maxX-minX)*scale)/2,top=(PreviewSizePixels-(maxY-minY)*scale)/2;
        var visual=new System.Windows.Media.DrawingVisual();
        using(var draw=visual.RenderOpen())
        {
            foreach(var item in placed)
            {
                var entity=item.Entity;double originX=item.Origin.X,originY=item.Origin.Y;
                ToolReference tool;
                if(!ToolMap.TryGetValue(entity.Layer,out tool))tool=new ToolReference(ProcessType.Reference,0);
                System.Windows.Media.SolidColorBrush brush;
                if(!PreviewToolBrushes.TryGetValue(tool.ProcessType+":"+tool.Index.ToString(CultureInfo.InvariantCulture),out brush))
                    brush=tool.ProcessType==ProcessType.Cut?System.Windows.Media.Brushes.Crimson:
                        tool.ProcessType==ProcessType.Reference?System.Windows.Media.Brushes.Gray:System.Windows.Media.Brushes.Teal;
                var pen=new System.Windows.Media.Pen(brush,PreviewStrokePixels);pen.Freeze();
                var text=IsEditableText(entity)?(DiscreteEntity)entity:null;
                if(text==null&&!(entity is ContinuousEntity))
                {
                    var box=entity.GetBounds();
                    draw.DrawEllipse(null,pen,
                        new System.Windows.Point(left+((box.Min.X+box.Max.X)/2+originX-minX)*scale,top+(maxY-originY-(box.Min.Y+box.Max.Y)/2)*scale),PreviewStrokePixels,PreviewStrokePixels);
                    continue;
                }
                var curves=text!=null?text.AbsolutePrimitives.OfType<ContinuousEntity>():entity is ContinuousEntity?new[]{(ContinuousEntity)entity}:Enumerable.Empty<ContinuousEntity>();
                foreach(var curve in curves)
                {
                    var points=curve.GetPoly(new PositiveDouble(Math.Max(MinimumGeometryDistance,0.5/scale))).Points.ToArray();
                    if(points.Length<2)continue;
                    var path=new System.Windows.Media.StreamGeometry();
                    using(var geometry=path.Open())
                    {
                        geometry.BeginFigure(new System.Windows.Point(left+(points[0].X+originX-minX)*scale,top+(maxY-originY-points[0].Y)*scale),false,curve.IsClosed);
                        for(int i=1;i<points.Length;i++)geometry.LineTo(new System.Windows.Point(left+(points[i].X+originX-minX)*scale,top+(maxY-originY-points[i].Y)*scale),true,false);
                    }
                    path.Freeze();
                    draw.DrawGeometry(null,pen,path);
                }
            }
        }
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap(PreviewSizePixels,PreviewSizePixels,96,96,System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(visual);image.Freeze();return image;
    }
    static void ReadPreviewTools(XElement root)
    {
        PreviewToolBrushes.Clear();var tools=root.Element("PreviewTools");if(tools==null)return;
        foreach(var item in tools.Elements("Tool"))
        {
            ProcessType process;int index;uint argb;string color=S(item,"color","").TrimStart('#');
            if(!Enum.TryParse<ProcessType>(S(item,"process",""),false,out process)||!Enum.IsDefined(typeof(ProcessType),process)||
                !Int32.TryParse(S(item,"index",""),NumberStyles.Integer,CultureInfo.InvariantCulture,out index)||index<0||
                color.Length!=8||!UInt32.TryParse(color,NumberStyles.HexNumber,CultureInfo.InvariantCulture,out argb))
                throw new ArgumentException("Invalid preview tool process, slot or #AARRGGBB color.");
            var brush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(argb>>24),(byte)(argb>>16),(byte)(argb>>8),(byte)argb));brush.Freeze();
            PreviewToolBrushes.Add(process+":"+index.ToString(CultureInfo.InvariantCulture),brush);
        }
    }
    static IEnumerable<Entity> WorkspaceEntities(Pattern pattern)
    {
        var entities=pattern.Entities.AsEnumerable();
        if(pattern.Entities.OfType<ContinuousEntity>().Any(curve=>curve.HasSeam||curve.DynamicEntities.OfType<NativeNotch>().Any())) {
            try { entities=ExpandNativeEffects(pattern.Entities).ToArray(); }
            catch(Exception error) { Console.WriteLine("Native effect preview unavailable for "+pattern.Name+": "+error.Message); }
        }
        if(pattern.Rotation==0) { foreach(var entity in entities)yield return entity;yield break; }
        var turn=new Transform2d();turn.Rotate(pattern.Rotation);
        // Transform the saved outlines, since the PS10 merge reader regenerates PS11 font outlines at a different scale.
        var display=entities.SelectMany(entity=>entity is FontEntity?((FontEntity)entity).AbsolutePrimitives.Cast<Entity>():new[]{entity});
        foreach(var entity in display) {
            var copy=entity.Clone();
            if(!copy.Transform(turn))throw new InvalidOperationException("Could not orient a pattern in the definition workspace.");
            yield return copy;
        }
    }
    static XDocument ReadMergeProject(ZipArchive archive)
    {
        if(archive.Entries.Count>MaximumMergeArchiveEntries)throw new InvalidDataException("The existing project contains too many archive entries.");
        var entries=archive.Entries.Where(entry=>entry.FullName=="project.xml").ToArray();
        if(entries.Length!=1||entries[0].Length>MaximumMergeXmlBytes)throw new InvalidDataException("The project XML is missing, duplicated or too large.");
        using(var stream=entries[0].Open())using(var reader=XmlReader.Create(stream,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumMergeXmlBytes})) {
            var document=XDocument.Load(reader);
            if(document.Root==null||document.Root.Name!=XName.Get("PresentationProject","http://autometrix.com/xml"))throw new InvalidDataException("The selected file is not a PatternSmith project.");
            return document;
        }
    }
    static Pattern[] MergePatterns(XElement root)
    {
        XNamespace a="http://autometrix.com/xml";
        var source=root.Element(a+"Patterns");if(source==null)throw new InvalidDataException("The project has no pattern collection.");
        var collection=new XElement(source);
        foreach(var attribute in root.Attributes().Where(attribute=>attribute.IsNamespaceDeclaration))
            if(collection.Attribute(attribute.Name)==null)collection.Add(new XAttribute(attribute));
        var dictionary=new XmlDictionary();
        var serializer=new DataContractSerializer(typeof(Pattern[]),new DataContractSerializerSettings {
            RootName=dictionary.Add("Patterns"),RootNamespace=dictionary.Add(a.NamespaceName),MaxItemsInObjectGraph=Int32.MaxValue});
        // Only patterns are read. MaterialCollection's global-library import callback is never invoked.
        using(var reader=XmlReader.Create(new StringReader(collection.ToString(SaveOptions.DisableFormatting)),new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=MaximumMergeXmlBytes}))
            return (Pattern[])serializer.ReadObject(reader);
    }
    [DataContract(Name="ClipboardPatternContainer",Namespace="http://autometrix.com/xml")]
    sealed class WorkspaceClipboard
    {
        // PS11 serializes its base-class tool map before the workspace pattern collection.
        [DataMember(Order=0)] public ReadOnlyDictionary<Layer,ToolReference> ToolReferenceMap;
        [DataMember(Order=1)] public Dictionary<Guid,Autometrix.Core.Imaging.BackgroundPhotoFilePaths> FilePaths;
        [DataMember(Order=2)] public ReadOnlyCollection<Group> PatternGroups;
        [DataMember(Order=3)] public ReadOnlyCollection<Pattern> Patterns;
        [DataMember(Order=4)] public string ProjectFileName;
    }
    [DataContract(Name="ClipboardEntityContainer",Namespace="http://autometrix.com/xml")]
    sealed class EditorClipboard
    {
        [DataMember(Order=0)] public ReadOnlyDictionary<Layer,ToolReference> ToolReferenceMap;
        [DataMember(Order=1)] public EntityCollection Entities;
    }
    static XElement SerializeClipboard(object container)
    {
        var xml=new StringBuilder();
        using(var writer=XmlWriter.Create(xml))Autometrix.Core.Serialization.Operations.Write(container,writer,null);
        return XElement.Parse(xml.ToString());
    }
    static void WriteWorkspaceClipboard(string output,List<Pattern> patterns,Dictionary<Guid,string> partStates)
    {
        var tools=new ReadOnlyDictionary<Layer,ToolReference>(ToolMap);
        var container=new WorkspaceClipboard {
            ToolReferenceMap=tools,
            // Native paste queries this dictionary even for patterns with no photos.
            FilePaths=new Dictionary<Guid,Autometrix.Core.Imaging.BackgroundPhotoFilePaths>(),
            PatternGroups=new List<Group>().AsReadOnly(),Patterns=patterns.AsReadOnly(),ProjectFileName=""
        };
        var workspace=new XDocument(SerializeClipboard(container));
        if(partStates.Count>0)AddPartStates(workspace,partStates,clipboard:true);
        var entities=new List<Entity>();
        foreach(var pattern in patterns) {
            var pose=new Transform2d();pose.Rotate(pattern.Rotation);pose.Translate(pattern.Location.X,pattern.Location.Y);
            foreach(var entity in pattern.Entities) {
                var copy=entity.Clone();
                if(!copy.Transform(pose))throw new InvalidOperationException("Could not position PatternSmith editor geometry.");
                entities.Add(copy);
            }
        }
        var editor=SerializeClipboard(new EditorClipboard {ToolReferenceMap=tools,Entities=new EntityCollection(entities)});
        var document=new XDocument(new XElement("PatternSmithClipboard",workspace.Root,editor));
        using(var writer=XmlWriter.Create(output,new XmlWriterSettings{Encoding=new UTF8Encoding(false)}))document.Save(writer);
    }
    static void AddPartStates(XDocument document,Dictionary<Guid,string> states,bool clipboard=false)
    {
        XNamespace a=document.Root.Name.Namespace;
        int written=0;
        var patterns=document.Root.Element(a+"Patterns");
        if(clipboard)patterns=patterns.Element(XName.Get("list",vTools.Commands.PatternSmithPartContract.CollectionNamespace));
        foreach(var pattern in patterns.Elements()) {
            Guid id;string state;
            if(!Guid.TryParse((string)pattern.Element(a+"PersistentId"),out id)||!states.TryGetValue(id,out state))continue;
            pattern.Add(new XElement(a+vTools.Commands.PatternSmithPartContract.StateMember,state));written++;
            // Pattern.With replaces the outer pattern but retains its constraints
            // during ordinary workspace edits such as move and rename.
            var constraints=pattern.Element(a+"Constraints");
            if(constraints!=null)constraints.Add(new XElement(XName.Get(vTools.Commands.PatternSmithPartContract.StateMember,
                vTools.Commands.PatternSmithPartContract.ConstraintNamespace),state));
        }
        if(written!=states.Count)throw new InvalidDataException("Could not match vTools metadata to the serialized patterns.");
    }
    static bool StorePatternSmith11FontSizes(XDocument document)
    {
        XNamespace p="http://autometrix.com/xml/plotting",e="http://autometrix.com/xml/entities",
            g="http://autometrix.com/xml/geometry",i="http://www.w3.org/2001/XMLSchema-instance";
        var patterns=document.Root.Element(document.Root.Name.Namespace+"Patterns");
        var fonts=patterns.Descendants(p+"Entity").Where(node=>((string)node.Attribute(i+"type")??"").Split(':').Last()=="Font");
        int resized=0;
        foreach(var font in fonts) {
            var fontData=font.Element(e+"Font");
            var size=fontData==null?null:fontData.Element("Size");
            var scale=font.Element(e+"ScaleFactor");
            var x=scale==null?null:scale.Element(g+"X");var y=scale==null?null:scale.Element(g+"Y");
            var outlines=font.Element(e+"Entities");
            if(size==null||x==null||y==null||outlines==null)throw new InvalidDataException("A table label has incomplete native font metrics.");
            double sy=(double)y,aspect=(double)x/sy;
            float points=(float)((double)size*sy*FontPointsPerInch);
            if(points<=0||Single.IsNaN(points)||Single.IsInfinity(points)||aspect<=0||Double.IsNaN(aspect)||Double.IsInfinity(aspect))
                throw new InvalidDataException("A table label has invalid native font metrics.");
            // PS10 normalizes the font to 40 pt and stores local outlines in inches. PS11 edits the
            // physical font and expects the local outline cache in points, while absolute geometry stays in inches.
            size.SetValue(points);x.SetValue(aspect);y.SetValue(1.0);
            foreach(var coordinate in outlines.Descendants().Where(value=>value.Name==g+"X"||value.Name==g+"Y"))
                coordinate.SetValue((double)coordinate*FontPointsPerInch);
            resized++;
        }
        return resized>0;
    }
    static double[] MergeBounds(IEnumerable<Pattern> patterns)
    {
        var boxes=patterns.SelectMany(pattern=>WorkspaceEntities(pattern).Select(entity=>new {Box=entity.GetBounds(),Origin=pattern.Location})).ToArray();
        if(boxes.Length==0)throw new InvalidDataException("Cannot merge a project without pattern geometry.");
        var bounds=new[]{boxes.Min(item=>item.Box.Min.X+item.Origin.X),boxes.Min(item=>item.Box.Min.Y+item.Origin.Y),
            boxes.Max(item=>item.Box.Max.X+item.Origin.X),boxes.Max(item=>item.Box.Max.Y+item.Origin.Y)};
        if(bounds.Any(value=>Double.IsNaN(value)||Double.IsInfinity(value)))throw new InvalidDataException("The project has invalid workspace bounds.");
        return bounds;
    }
    static void CheckMergeReferences(XElement root)
    {
        XNamespace z="http://schemas.microsoft.com/2003/10/Serialization/",a="http://autometrix.com/xml",n="http://autometrix.com/xml/nesting";
        var ids=root.DescendantsAndSelf().Attributes(z+"Id").Select(attribute=>attribute.Value).ToArray();
        var unique=new HashSet<string>(ids,StringComparer.Ordinal);
        if(unique.Count!=ids.Length||root.DescendantsAndSelf().Attributes(z+"Ref").Any(attribute=>!unique.Contains(attribute.Value)))
            throw new InvalidDataException("The merged project has invalid object references.");
        var materialIds=root.Element(a+"Materials").Element(n+"Templates").Elements().Select(item=>(string)item.Element(n+"MaterialID")).ToArray();
        if(materialIds.Any(String.IsNullOrWhiteSpace)||materialIds.Distinct(StringComparer.Ordinal).Count()!=materialIds.Length)
            throw new InvalidDataException("The merged project has conflicting material identifiers.");
    }
    static void AppendMergeCollection(XElement target,XElement incoming,string name)
    {
        XNamespace a="http://autometrix.com/xml",z="http://schemas.microsoft.com/2003/10/Serialization/",i="http://www.w3.org/2001/XMLSchema-instance";
        var source=incoming.Element(a+name);if(source==null||!source.Elements().Any())return;
        var destination=target.Element(a+name);
        if(destination==null){destination=new XElement(a+name);target.Add(destination);}
        destination.SetAttributeValue(i+"nil",null);
        foreach(var item in source.Elements()) {
            var copy=new XElement(item);
            foreach(var attribute in incoming.Attributes().Where(attribute=>attribute.IsNamespaceDeclaration))
                if(copy.Attribute(attribute.Name)==null)copy.Add(new XAttribute(attribute));
            destination.Add(copy);
        }
        if(destination.Attribute(z+"Size")!=null)destination.SetAttributeValue(z+"Size",destination.Elements().Count());
    }
    // Compare resolved recipes, not project-local IDs or XML namespace prefixes.
    static XElement MergeMaterialValue(XElement element,IDictionary<string,XElement> definitions,HashSet<string> visiting)
    {
        XNamespace z="http://schemas.microsoft.com/2003/10/Serialization/",i="http://www.w3.org/2001/XMLSchema-instance";
        string reference=(string)element.Attribute(z+"Ref");var source=reference==null?element:definitions[reference];
        string id=(string)source.Attribute(z+"Id");
        if(id!=null&&!visiting.Add(id))throw new InvalidDataException("A material contains circular object references.");
        var value=new XElement(element.Name);
        foreach(var attribute in source.Attributes().Where(attribute=>!attribute.IsNamespaceDeclaration&&attribute.Name.Namespace!=z).OrderBy(attribute=>attribute.Name.ToString(),StringComparer.Ordinal)) {
            string text=attribute.Value;
            if(attribute.Name==i+"type") {
                int colon=text.IndexOf(':');var ns=colon<0?source.GetDefaultNamespace():source.GetNamespaceOfPrefix(text.Substring(0,colon));
                text=XName.Get(colon<0?text:text.Substring(colon+1),ns==null?"":ns.NamespaceName).ToString();
            }
            value.SetAttributeValue(attribute.Name,text);
        }
        foreach(var child in source.Elements().Where(child=>child.Name.LocalName!="MaterialID"))value.Add(MergeMaterialValue(child,definitions,visiting));
        if(!source.HasElements) {
            double number;string text=source.Value;
            if(new[]{"Value","Alpha","Hue","Lightness","Saturation","Cost","MaximumPower","MinimumPower"}.Contains(source.Name.LocalName)&&
                Double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out number))text=number.ToString("R",CultureInfo.InvariantCulture);
            value.Value=text;
        }
        if(id!=null)visiting.Remove(id);return value;
    }
    static bool TryReuseMergeMaterial(XElement existing,XElement incoming,IDictionary<string,XElement> oldDefinitions,
        IDictionary<string,XElement> newDefinitions,out Dictionary<string,string> redirects)
    {
        XNamespace z="http://schemas.microsoft.com/2003/10/Serialization/";
        redirects=new Dictionary<string,string>(StringComparer.Ordinal);
        if(!XNode.DeepEquals(MergeMaterialValue(existing,oldDefinitions,new HashSet<string>(StringComparer.Ordinal)),
            MergeMaterialValue(incoming,newDefinitions,new HashSet<string>(StringComparer.Ordinal))))return false;
        var pending=new Stack<KeyValuePair<XElement,XElement>>();pending.Push(new KeyValuePair<XElement,XElement>(existing,incoming));
        while(pending.Count>0) {
            var pair=pending.Pop();var oldValue=pair.Key;var newValue=pair.Value;
            if(oldValue.Attribute(z+"Ref")!=null)oldValue=oldDefinitions[(string)oldValue.Attribute(z+"Ref")];
            if(newValue.Attribute(z+"Ref")!=null)newValue=newDefinitions[(string)newValue.Attribute(z+"Ref")];
            string oldId=(string)oldValue.Attribute(z+"Id"),newId=(string)newValue.Attribute(z+"Id");
            if(newId!=null) {
                if(oldId==null)return false;
                if(!redirects.ContainsKey(newId))redirects.Add(newId,oldId);
            }
            var oldChildren=oldValue.Elements().Where(child=>child.Name.LocalName!="MaterialID").ToArray();
            var newChildren=newValue.Elements().Where(child=>child.Name.LocalName!="MaterialID").ToArray();
            for(int index=0;index<oldChildren.Length;index++)pending.Push(new KeyValuePair<XElement,XElement>(oldChildren[index],newChildren[index]));
        }
        return true;
    }
    static int MergeProjects(XElement request,string output)
    {
        string existing=Path.GetFullPath(S(request,"existing","")),incomingPath=Path.GetFullPath(S(request,"incoming",""));
        double gap=D(request,"gap",DefaultMergeWorkspaceGap);if(gap<0)throw new ArgumentException("Merge spacing must be nonnegative inches.");
        XNamespace a="http://autometrix.com/xml",n="http://autometrix.com/xml/nesting",g="http://autometrix.com/xml/geometry",z="http://schemas.microsoft.com/2003/10/Serialization/",p="http://autometrix.com/xml/plotting";
        using(var oldFile=File.OpenRead(existing))using(var oldZip=new ZipArchive(oldFile,ZipArchiveMode.Read))
        using(var newFile=File.OpenRead(incomingPath))using(var newZip=new ZipArchive(newFile,ZipArchiveMode.Read)) {
            ProgressClock.Restart();progressIntervalMilliseconds=DefaultProgressIntervalMilliseconds;
            Progress(5,"Reading existing and new project layouts");
            var merged=ReadMergeProject(oldZip);var added=ReadMergeProject(newZip);
            CheckMergeReferences(merged.Root);CheckMergeReferences(added.Root);
            var existingPatterns=MergePatterns(merged.Root);var oldBounds=existingPatterns.Length==0?null:MergeBounds(existingPatterns);var newBounds=MergeBounds(MergePatterns(added.Root));
            double dx=oldBounds==null?0:oldBounds[2]+gap-newBounds[0],dy=oldBounds==null?0:oldBounds[3]-newBounds[3];
            foreach(var pattern in added.Root.Element(a+"Patterns").Elements()) {
                var location=pattern.Element(a+"Location");if(location==null)throw new InvalidDataException("An incoming pattern has no workspace location.");
                location.Element(g+"X").Value=(Double.Parse(location.Element(g+"X").Value,CultureInfo.InvariantCulture)+dx).ToString("R",CultureInfo.InvariantCulture);
                location.Element(g+"Y").Value=(Double.Parse(location.Element(g+"Y").Value,CultureInfo.InvariantCulture)+dy).ToString("R",CultureInfo.InvariantCulture);
            }
            Progress(25,"Resolving project object and material identities");
            string prefix="merge_"+Guid.NewGuid().ToString("N")+"_";
            var objectIds=added.Root.DescendantsAndSelf().Attributes(z+"Id").ToDictionary(attribute=>attribute.Value,attribute=>prefix+attribute.Value,StringComparer.Ordinal);
            foreach(var attribute in added.Root.DescendantsAndSelf().Attributes().Where(attribute=>attribute.Name==z+"Id"||attribute.Name==z+"Ref"))attribute.Value=objectIds[attribute.Value];
            Func<XElement,bool> identityField=element=>!element.HasElements&&element.Name.LocalName!="MaterialID"&&element.Name.LocalName!="MaterialId"&&
                (element.Name.LocalName.EndsWith("id",StringComparison.OrdinalIgnoreCase)||element.Name.LocalName=="Group");
            var oldGuids=new HashSet<Guid>(merged.Root.Descendants().Where(identityField).Select(element=>{Guid value;return Guid.TryParse(element.Value,out value)?value:Guid.Empty;}));
            var guidMap=new Dictionary<Guid,Guid>();
            foreach(var element in added.Root.Descendants().Where(identityField)) {
                Guid value;if(!Guid.TryParse(element.Value,out value)||value==Guid.Empty||!oldGuids.Contains(value))continue;
                Guid next;if(!guidMap.TryGetValue(value,out next)){next=Guid.NewGuid();guidMap.Add(value,next);}element.Value=next.ToString();
            }
            var oldTemplates=merged.Root.Element(a+"Materials").Element(n+"Templates");
            var newTemplates=added.Root.Element(a+"Materials").Element(n+"Templates");
            var oldDefinitions=merged.Root.DescendantsAndSelf().Where(element=>element.Attribute(z+"Id")!=null).ToDictionary(element=>(string)element.Attribute(z+"Id"),StringComparer.Ordinal);
            var newDefinitions=added.Root.DescendantsAndSelf().Where(element=>element.Attribute(z+"Id")!=null).ToDictionary(element=>(string)element.Attribute(z+"Id"),StringComparer.Ordinal);
            var materialReferences=new Dictionary<string,string>(StringComparer.Ordinal);
            var used=new HashSet<string>(oldTemplates.Elements().Select(item=>(string)item.Element(n+"MaterialID")),StringComparer.Ordinal);
            var reserved=new HashSet<string>(used.Concat(newTemplates.Elements().Select(item=>(string)item.Element(n+"MaterialID"))),StringComparer.Ordinal);
            var materialIds=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var template in newTemplates.Elements().ToArray()) {
                string id=(string)template.Element(n+"MaterialID"),next=id;
                XElement reused=null;
                foreach(var candidate in oldTemplates.Elements()) {
                    Dictionary<string,string> redirects;
                    if(!TryReuseMergeMaterial(candidate,template,oldDefinitions,newDefinitions,out redirects))continue;
                    reused=candidate;foreach(var pair in redirects)materialReferences[pair.Key]=pair.Value;break;
                }
                if(reused!=null) {
                    materialIds.Add(id,(string)reused.Element(n+"MaterialID"));template.Remove();continue;
                }
                if(used.Contains(next)) {
                    bool custom=System.Text.RegularExpressions.Regex.IsMatch(id,@"^c[1-9][0-9]*$");int index=custom?1:2;
                    do{next=custom?"c"+index:id+"-"+index;index++;}while(reserved.Contains(next)||used.Contains(next));
                }
                used.Add(next);materialIds.Add(id,next);
            }
            foreach(var element in added.Root.Descendants().Where(element=>!element.HasElements&&(element.Name.LocalName=="MaterialID"||element.Name.LocalName=="MaterialId"))) {
                string value;if(materialIds.TryGetValue(element.Value,out value))element.Value=value;
            }
            foreach(var reference in added.Root.DescendantsAndSelf().Attributes(z+"Ref")) {
                string value;if(materialReferences.TryGetValue(reference.Value,out value))reference.Value=value;
            }
            foreach(string name in new[]{"Patterns","PatternGroups","ToolReferences","Nests"})AppendMergeCollection(merged.Root,added.Root,name);
            foreach(string name in new[]{"Templates","Instances"}) {
                var target=merged.Root.Element(a+"Materials").Element(n+name);var source=added.Root.Element(a+"Materials").Element(n+name);
                foreach(var item in source.Elements()) {
                    var copy=new XElement(item);foreach(var attribute in added.Root.Attributes().Where(attribute=>attribute.IsNamespaceDeclaration))if(copy.Attribute(attribute.Name)==null)copy.Add(new XAttribute(attribute));target.Add(copy);
                }
                if(target.Attribute(z+"Size")!=null)target.SetAttributeValue(z+"Size",target.Elements().Count());
            }
            foreach(string name in new[]{"CurrentMaterialTemplate","CurrentNest"}) {
                var current=merged.Root.Element(a+name);
                if((current==null||(string)current.Attribute(XName.Get("nil","http://www.w3.org/2001/XMLSchema-instance"))=="true")&&added.Root.Element(a+name)!=null) {
                    var copy=new XElement(added.Root.Element(a+name));if(current==null)merged.Root.Add(copy);else current.ReplaceWith(copy);
                }
            }
            CheckMergeReferences(merged.Root);
            Progress(55,"Rendering the complete combined workspace preview");
            var patterns=MergePatterns(merged.Root);ToolMap.Clear();ReadPreviewTools(request);
            var definitions=merged.Root.DescendantsAndSelf().Where(element=>element.Attribute(z+"Id")!=null).ToDictionary(element=>(string)element.Attribute(z+"Id"),StringComparer.Ordinal);
            Func<XElement,XElement> resolve=element=>element.Attribute(z+"Ref")==null?element:definitions[(string)element.Attribute(z+"Ref")];
            var tools=new Dictionary<Guid,ToolReference>();
            foreach(var item in merged.Root.Element(a+"ToolReferences").Elements()) {
                var key=resolve(item.Element(p+"Key"));var value=resolve(item.Element(p+"Value"));
                tools[Guid.Parse((string)key.Element(a+"Id"))]=new ToolReference((ProcessType)Enum.Parse(typeof(ProcessType),(string)value.Element(p+"ProcessType")),Int32.Parse((string)value.Element(p+"Index"),CultureInfo.InvariantCulture));
            }
            foreach(var layer in patterns.SelectMany(pattern=>pattern.Entities).Select(entity=>entity.Layer).Distinct()) {
                ToolReference tool;if(!tools.TryGetValue(layer.Id,out tool))throw new InvalidDataException("The existing project has a pattern layer without a tool mapping.");ToolMap[layer]=tool;
            }
            var effects=patterns.SelectMany(pattern=>pattern.Entities).OfType<ContinuousEntity>();
            foreach(var layer in effects.SelectMany(entity=>entity.DynamicEntities.OfType<NativeNotch>().Select(effect=>effect.Layer)
                .Concat(entity.HasSeam?new[]{entity.Seam.Layer}:new Layer[0])).Distinct()) {
                ToolReference tool;if(tools.TryGetValue(layer.Id,out tool))ToolMap[layer]=tool;
            }
            var preview=MakePreview(patterns);
            Progress(80,"Writing merged patterns, nests and preview");
            using(var stream=new FileStream(output,FileMode.CreateNew))using(var archive=new ZipArchive(stream,ZipArchiveMode.Create)) {
                foreach(var entry in oldZip.Entries.Where(entry=>entry.FullName!="project.xml"&&entry.FullName!="preview.png"))
                    using(var source=entry.Open())using(var target=archive.CreateEntry(entry.FullName,CompressionLevel.Optimal).Open())source.CopyTo(target);
                using(var target=archive.CreateEntry("project.xml",CompressionLevel.Optimal).Open())merged.Save(target);
                using(var target=archive.CreateEntry("preview.png",CompressionLevel.Optimal).Open()) {
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(preview));
                    using(var bitmap=new MemoryStream()){encoder.Save(bitmap);bitmap.Position=0;bitmap.CopyTo(target);}
                }
            }
            Console.WriteLine("MERGED patterns="+patterns.Length+" nests="+merged.Root.Element(a+"Nests").Elements().Count()+" workspaceDx="+dx.ToString("R",CultureInfo.InvariantCulture)+" workspaceDy="+dy.ToString("R",CultureInfo.InvariantCulture));
            Progress(100,"Merged export ready");return 0;
        }
    }
    static T ReadEffectChoice<T>(XElement node,string field) where T:struct
    {
        T choice;
        if(!Enum.TryParse(S(node,field,""),out choice)||!Enum.IsDefined(typeof(T),choice))
            throw new InvalidDataException("Invalid PatternSmith "+node.Name.LocalName+" "+field+".");
        return choice;
    }
    static Entity ReadEffectSource(XElement source,Seam seam)
    {
        var layer=new Layer("Source");
        switch(source.Name.LocalName) {
            case "Line":
                return new LineEntity(layer,NoLayers,P(source.Element("A")),P(source.Element("B")),seam,NoDynamic);
            case "Polyline":
                var points=Points(source.Element("Points"));
                if(B(source,"Closed",false)&&points[0]!=points[points.Count-1])points.Add(points[0]);
                return new PolyEntity(layer,NoLayers,points,seam,NoDynamic);
            case "Circle":
                return new Circle2d(P(source.Element("Center")),D(source,"Radius",0),ReadEffectChoice<CircularDirection>(source,"Direction"))
                    .GetEntity(layer,NoLayers,seam,NoDynamic);
            case "Arc":
                return new Arc2d(P(source.Element("Center")),D(source,"Radius",0),D(source,"StartAngle",0),D(source,"SweepAngle",0))
                    .GetEntity(layer,NoLayers,seam,NoDynamic);
            case "Bezier":
            case "BezierGroup":
                return new BezierGroup2d(Points(source.Element("Points"))).GetEntity(layer,NoLayers,seam,NoDynamic);
            case "Spline":
                return new Spline2d(Points(source.Element("Points")),B(source,"Closed",false)).GetEntity(layer,NoLayers,seam,NoDynamic);
            case "Ellipse":
                return new Ellipse2d(P(source.Element("Center")),D(source,"A",0),D(source,"B",0),D(source,"Rotation",0),
                    ReadEffectChoice<CircularDirection>(source,"Direction")).GetEntity(layer,NoLayers,seam,NoDynamic);
            case "Rectangle":
                return new RectangleEntity(layer,NoLayers,seam,NoDynamic,P(source.Element("A")),new PositiveDouble(D(source,"Width",0)),
                    new PositiveDouble(D(source,"Height",0)),D(source,"Rotation",0),ReadEffectChoice<CircularDirection>(source,"Direction"));
            default:throw new InvalidDataException("Unsupported PatternSmith effect source "+source.Name.LocalName+".");
        }
    }
    static XElement EffectPoint(string name,Point2d point)
    {
        return new XElement(name,new XElement("X",point.X),new XElement("Y",point.Y));
    }
    static XElement WriteEffectPrimitive(Entity entity,string layer)
    {
        XElement primitive;
        var line=entity as LineEntity;var point=entity as PointEntity;var poly=entity as PolyEntity;
        var arc=entity as ArcEntity;var circle=entity as CircleEntity;var bezier=entity as BezierGroupEntity;
        var rect=entity as RectangleEntity;var single=entity as BezierEntity;var spline=entity as SplineEntity;
        if(rect!=null)poly=rect.AsPoly;
        if(line!=null)primitive=new XElement("Line",EffectPoint("A",line.A),EffectPoint("B",line.B));
        else if(point!=null)primitive=new XElement("Point",EffectPoint("Center",point.Center));
        else if(poly!=null)primitive=new XElement("Polyline",new XElement("IsClosed",poly.IsClosed),
            new XElement("Points",poly.Points.Select(p=>EffectPoint("P",p))));
        else if(circle!=null)primitive=new XElement("Circle",EffectPoint("Center",circle.Center),
            new XElement("Radius",circle.Radius),new XElement("Direction",circle.Direction));
        else if(arc!=null)primitive=new XElement("Arc",EffectPoint("Center",arc.Center),
            new XElement("Radius",arc.Radius),new XElement("StartAngle",arc.StartAngle),new XElement("SweepAngle",arc.SweepAngle));
        else if(bezier!=null)primitive=new XElement("BezierGroup",new XElement("ControlPoints",bezier.ControlPoints.Select(p=>EffectPoint("P",p))));
        else if(single!=null)primitive=new XElement("Bezier",new XElement("ControlPoints",single.ControlPoints.Select(p=>EffectPoint("P",p))));
        else if(spline!=null)primitive=new XElement("BezierGroup",new XElement("ControlPoints",spline.Geometry.BezierControlPoints.Select(p=>EffectPoint("P",p))));
        else throw new InvalidDataException("Unsupported native effect primitive "+entity.GetType().Name+".");
        primitive.SetAttributeValue("layer",layer);
        return primitive;
    }
    static NativeNotch ReadImportNotch(XElement effect,ContinuousEntity source,NativeNotchExecution execution,Layer layer=null)
    {
        var position=new Autometrix.Core.Entities.Notches.Position(D(effect,"Distance",0),
            ReadEffectChoice<Autometrix.Core.Entities.Notches.Placement>(effect,"Placement"),
            ReadEffectChoice<Autometrix.Core.Entities.Notches.DistanceUnits>(effect,"DistanceUnits"),
            ReadEffectChoice<Autometrix.Core.Entities.Notches.Side>(effect,"Side"),source.GetArcLength());
        return new NativeNotch(layer??new Layer(S(effect,"layer","")),ReadEffectChoice<NativeNotchStyle>(effect,"Style"),
            new PositiveDouble(D(effect,"Depth",0)),new NonnegativeDouble(D(effect,"Width",0)),position,execution);
    }
    static NativeNotchExecution ImportNotchExecution(XElement effect)
    {
        return effect.Attribute("ExecutionStyle")==null
            ? DefaultNotchExecutionStyle
            : ReadEffectChoice<NativeNotchExecution>(effect,"ExecutionStyle");
    }
    static ContinuousEntity ReadImportEffectSource(XElement recipe,out ContinuousEntity original,out Dictionary<XElement,NativeNotch> inline)
    {
        var allowance=recipe.Element("Seam");
        var seam=allowance==null?null:new Seam(new Layer(S(allowance,"layer","")),D(allowance,"Width",0),
            ReadEffectChoice<SeamStyle>(allowance,"StartStyle"),ReadEffectChoice<SeamStyle>(allowance,"EndStyle"));
        var source=ReadEffectSource(recipe.Elements().First(),seam) as ContinuousEntity;
        if(source==null||!source.IsValid())throw new InvalidDataException("Invalid PatternSmith curve for a seam, notch, or anchor.");
        original=source;
        inline=new Dictionary<XElement,NativeNotch>();
        foreach(var effect in recipe.Elements("Notch")) {
            if(ImportNotchExecution(effect)!=NativeNotchExecution.Inline)continue;
            var notch=ReadImportNotch(effect,source,NativeNotchExecution.Inline);
            if(notch.Style!=NativeNotchStyle.V&&notch.Style!=NativeNotchStyle.Castle)
                throw new InvalidDataException("An inline notch requires a V or Castle profile.");
            inline.Add(effect,notch);
        }
        return inline.Count==0?source:source.With(dynamicEntities:new ReadOnlyCollection<DynamicEntity>(inline.Values.Cast<DynamicEntity>().ToList()));
    }
    static XElement[] WriteImportNotchPrimitives(Entity[] pieces,string layer,
        Autometrix.Core.Entities.Notches.Style style)
    {
        if((style==Autometrix.Core.Entities.Notches.Style.V||style==Autometrix.Core.Entities.Notches.Style.Castle)&&pieces.Length>1) {
            var lines=pieces.OfType<LineEntity>().ToList();
            if(lines.Count==pieces.Length) {
                var points=new LinkedList<Point2d>(new[]{lines[0].A,lines[0].B});
                lines.RemoveAt(0);
                while(lines.Count>0) {
                    int index=lines.FindIndex(candidate=>candidate.A.DistanceTo(points.First.Value)<=MinimumGeometryDistance||
                        candidate.B.DistanceTo(points.First.Value)<=MinimumGeometryDistance||
                        candidate.A.DistanceTo(points.Last.Value)<=MinimumGeometryDistance||
                        candidate.B.DistanceTo(points.Last.Value)<=MinimumGeometryDistance);
                    if(index<0)break;
                    var line=lines[index];lines.RemoveAt(index);
                    if(line.A.DistanceTo(points.First.Value)<=MinimumGeometryDistance)points.AddFirst(line.B);
                    else if(line.B.DistanceTo(points.First.Value)<=MinimumGeometryDistance)points.AddFirst(line.A);
                    else if(line.A.DistanceTo(points.Last.Value)<=MinimumGeometryDistance)points.AddLast(line.B);
                    else points.AddLast(line.A);
                }
                if(lines.Count==0)return new[]{new XElement("Polyline",new XAttribute("layer",layer),
                    new XElement("IsClosed",false),new XElement("Points",points.Select(point=>EffectPoint("P",point))))};
            }
        }
        return pieces.Select(primitive=>WriteEffectPrimitive(primitive,layer)).ToArray();
    }
    static ContinuousEntity[] ReadImportAllowance(ContinuousEntity source,Autometrix.Core.Entities.Cycles.EntityGroupResult chain,bool splice)
    {
        var options=new PerimeterPathOptions();
        if(!source.IsClosed)return chain!=null?chain.SeamAsEntities(source).ContinuousEntities.ToArray():
            Autometrix.Core.Entities.Extensions.CollectionExtensions.GetSeamEntities(source,null,null,options).ToArray();
        var offset=source.GetSeamEntity();
        return !splice||offset==null?new[]{offset}:
            offset.GetSplicedDynamics(ContinuousEntity.GetInlineDynamicPrimitiveConstructors(source,offset,options)).ToArray();
    }

    static IEnumerable<XElement> ResolveImportEffects(XElement recipe,ContinuousEntity source,
        Autometrix.Core.Entities.Cycles.EntityGroupResult chain,Dictionary<XElement,NativeNotch> inline,
        ContinuousEntity original,Autometrix.Core.Entities.Cycles.EntityGroupResult originalChain)
    {
        var allowance=recipe.Element("Seam");
        ContinuousEntity sewCurve=null;
        var inlinePieces=new Dictionary<Guid,List<Entity>>();
        if(inline.Count>0) {
            // The SDK keeps the stroke's layer ID even when it clones the inline notch.
            foreach(var notch in inline.Values)inlinePieces.Add(notch.Layer.Id,new List<Entity>());
            var spliced=allowance!=null?ReadImportAllowance(source,chain,true):
                original.GetSplicedDynamics(ContinuousEntity.GetInlineDynamicPrimitiveConstructors(
                    source,original,new PerimeterPathOptions())).ToArray();
            foreach(var stroke in spliced.OfType<NotchPrimitive>()) {
                List<Entity> pieces;
                if(!inlinePieces.TryGetValue(stroke.Layer.Id,out pieces))
                    throw new InvalidDataException("PatternSmith returned an unrecognized inline notch.");
                pieces.Add(stroke);
            }
        }
        if(allowance!=null) {
            // Use the original cut edge with its caps, and extract exact profiles from a separate spliced copy.
            var allowanceCurves=ReadImportAllowance(original,originalChain,false);
            if(allowanceCurves.Length==0||allowanceCurves.Any(curve=>curve==null||!curve.IsValid()))
                throw new InvalidDataException("PatternSmith could not generate a valid seam allowance.");
            foreach(var curve in allowanceCurves) {
                var primitive=WriteEffectPrimitive(curve,S(allowance,"layer",""));
                primitive.SetAttributeValue("seam",true);
                yield return primitive;
            }
        }
        foreach(var effect in recipe.Elements().Skip(1).Where(node=>node.Name!="Seam")) {
            string key=S(effect,"layer","");
            var layer=new Layer(key);
            DynamicEntity attachment;
            if(effect.Name=="Anchor") {
                attachment=new Autometrix.Core.Entities.Anchors.Anchor(layer,
                    ReadEffectChoice<Autometrix.Core.Entities.Anchors.AnchorEntityType>(effect,"EntityType"),
                    Int32.Parse(S(effect,"Count","0"),CultureInfo.InvariantCulture),
                    new PositiveDouble(D(effect,"Radius",0)),new PositiveDouble(D(effect,"Length",0)),
                    new NonnegativeDouble(D(effect,"DistanceFromStart",0)),new NonnegativeDouble(D(effect,"DistanceFromEnd",0)));
            } else if(effect.Name=="Notch") {
                NativeNotch notch;
                bool spliced=inline.TryGetValue(effect,out notch);
                if(!spliced)notch=ReadImportNotch(effect,source,NativeNotchExecution.Discrete);
                var strokes=spliced?inlinePieces[notch.Layer.Id].ToArray():source.GetDynamicEntityPrimitives(notch).ToArray();
                var fragments=WriteImportNotchPrimitives(strokes,key,notch.Style);
                if(spliced&&(fragments.Length!=1||fragments[0].Name!="Polyline"))
                    throw new InvalidDataException("PatternSmith could not build a complete inline notch on the cut edge.");
                if(fragments.Length==0)continue;
                var position=notch.Position;
                var anchor=source.GetPosition(notch);
                if(sewCurve==null)sewCurve=allowance==null?source:source.With(seam:Seam.EmptySeam);
                var sewAnchor=sewCurve.GetPosition(notch).Location;
                yield return new XElement("Notch",new XAttribute("style",notch.Style),new XAttribute("depth",D(effect,"Depth",0)),
                    new XAttribute("width",D(effect,"Width",0)),new XAttribute("angle",anchor.Rotation),
                    new XAttribute("inline",spliced),
                    new XAttribute("percent",position.DistanceUnits!=Autometrix.Core.Entities.Notches.DistanceUnits.Native),
                    new XAttribute("external",position.Side==Autometrix.Core.Entities.Notches.Side.External),
                    EffectPoint("Anchor",anchor.Location),EffectPoint("SewAnchor",sewAnchor),fragments);
                continue;
            } else throw new InvalidDataException("Unknown PatternSmith effect "+effect.Name.LocalName+".");
            foreach(var primitive in source.GetDynamicEntityPrimitives(attachment))
                yield return WriteEffectPrimitive(primitive,key);
        }
    }
    // Kept separate so AssemblyResolve is installed before JIT touches vendor types.
    static int ReadImportSplines(XElement request,string output)
    {
        var result=new XElement("PatternSmithSplineCurves");
        foreach(var item in request.Elements("Spline")) {
            string ordinal=S(item,"id","");
            try {
                var spline=new Spline2d(item.Elements("P").Select(P),B(item,"closed",false));
                result.Add(new XElement("Curve",new XAttribute("id",ordinal),spline.BezierControlPoints.Select(
                    point=>new XElement("P",new XAttribute("x",point.X),new XAttribute("y",point.Y)))));
            } catch(Exception error) { Console.Error.WriteLine("Spline "+ordinal+": "+error.Message); }
        }
        foreach(var recipes in request.Elements("Effects").GroupBy(item=>S(item,"group","single:"+S(item,"id","")))) {
            var sources=new Dictionary<XElement,ContinuousEntity>();
            var originals=new Dictionary<XElement,ContinuousEntity>();
            var inlineNotches=new Dictionary<XElement,Dictionary<XElement,NativeNotch>>();
            var responses=new Dictionary<XElement,XElement>();
            foreach(var item in recipes) {
                var response=new XElement("Effects",new XAttribute("id",S(item,"id","")));
                result.Add(response);responses.Add(item,response);
                try {
                    Dictionary<XElement,NativeNotch> inline;
                    ContinuousEntity original;
                    sources.Add(item,ReadImportEffectSource(item,out original,out inline));
                    originals.Add(item,original);inlineNotches.Add(item,inline);
                }
                catch(Exception error) { response.SetAttributeValue("error",error.Message); }
            }
            var seamed=originals.Values.Where(source=>source.HasSeam&&!source.IsClosed).ToArray();
            var originalChain=seamed.Length<2?null:new EntityCollection(seamed).GetGroupedEntities(PerimeterOptions.OnlyContinuous);
            var chain=originalChain==null||!inlineNotches.Values.Any(notches=>notches.Count>0)?null:
                new EntityCollection(sources.Values.Where(source=>source.HasSeam&&!source.IsClosed))
                    .GetGroupedEntities(PerimeterOptions.OnlyContinuous);
            foreach(var source in sources) {
                var response=responses[source.Key];
                try { response.Add(ResolveImportEffects(source.Key,source.Value,chain,inlineNotches[source.Key],
                    originals[source.Key],originalChain).ToArray()); }
                catch(Exception error) { response.SetAttributeValue("error",error.Message); }
            }
        }
        using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))result.Save(file);
        return 0;
    }
    static int Build(string input, string output)
    {
        if (File.Exists(output)) throw new IOException("Output already exists: " + output);
        XDocument request;
        using (var reader=XmlReader.Create(input,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit })) request=XDocument.Load(reader);
        if(request.Root!=null&&request.Root.Name=="PatternSmithMerge")return MergeProjects(request.Root,output);
        if(request.Root!=null&&request.Root.Name=="PatternSmithReadSplines")return ReadImportSplines(request.Root,output);
        if (request.Root == null || request.Root.Name != "PatternSmithRequest" || S(request.Root,"units",DefaultUnits) != "inches") throw new ArgumentException("Expected PatternSmithRequest units=inches.");
        Layers.Clear();ToolMap.Clear();ValidatedPerimeters.Clear();ConvertedSplines.Clear();RequestedSplines.Clear();completedEntities=0;
        totalEntities=request.Root.Elements("Table").Elements("Pattern").Elements().Count(node=>node.Name!="EffectSet");
        ReadPreviewTools(request.Root);
        distanceIndexLeafSegments=Math.Max(1,int.Parse(S(request.Root,"distanceIndexLeafSegments",DefaultDistanceIndexLeafSegments.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture));
        progressIntervalMilliseconds=Math.Max(0,int.Parse(S(request.Root,"progressIntervalMilliseconds",DefaultProgressIntervalMilliseconds.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture));
        progressKind=null;ProgressClock.Restart();lastProgressMilliseconds=0;
        ShapeSplines.Clear();diskCacheHits=shapeCacheHits=cacheWarning=0;splineCacheDirectory=null;
        reuseEquivalentSplines=B(request.Root,"reuseEquivalentSplines",false);
        splineCacheMegabytes=Math.Max(1,int.Parse(S(request.Root,"splineCacheMegabytes",DefaultSplineCacheMegabytes.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture));
        string cacheRoot=S(request.Root,"splineCacheDirectory","");
        if(!String.IsNullOrWhiteSpace(cacheRoot))try {
            splineCacheDirectory=Path.Combine(Path.GetFullPath(cacheRoot),"v"+SplineCacheRevision+"-"+typeof(Spline2d).Assembly.GetName().Version);
            Directory.CreateDirectory(splineCacheDirectory);
        } catch(Exception error) {splineCacheDirectory=null;CacheNote(error);}
        Progress(1,"Preparing native materials and patterns");
        Phase("spline conversion");PrepareSplines(request.Root);
        if(B(request.Root,"prepareSplinesOnly",false)) {
            if(ConvertedSplines.Count>0&&(splineCacheDirectory==null||ConvertedSplines.Keys.Any(key=>!File.Exists(CachePath(CacheHash(key))))))
                throw new IOException("Preconversion results could not be retained; conversion will be completed during export.");
            Phase("curves ready");Progress(100,"Curves ready");PhaseClock.Stop();return 0;
        }
        Phase("entity assembly");
        var patterns=new List<Pattern>();var templates=new List<MaterialTemplate>();var instances=new List<MaterialInstance>();var nests=new List<Nest>();
        var partStates=new Dictionary<Guid,string>();
        var recipesById=new Dictionary<string,string>(StringComparer.Ordinal);
        var templatesByRecipe=new Dictionary<string,MaterialTemplate>(StringComparer.Ordinal);
        bool clipboard=B(request.Root,"clipboardPatterns",false);
        double workspaceY=0,workspaceGap=D(request.Root,"workspaceGap",DefaultWorkspaceGap);
        if(workspaceGap<0)throw new ArgumentException("Workspace gap must be nonnegative inches.");
        var materialTables=request.Root.Elements("Table").Where(table=>B(table,"materialAssigned",DefaultMaterialAssigned)).ToArray();
        var customIds=vTools.Commands.PatternSmithMaterialIdentity.Allocate(materialTables.Select(table=>S(table,"material",S(table,"name","Material"))),
            materialTables.Select(table=>S(table,"materialId","")).Where(value=>value.Length>0),new Dictionary<string,string>());
        var reservedMaterialIds=new HashSet<string>(materialTables.Select(table=>S(table,"materialId",customIds[S(table,"material",S(table,"name","Material"))])),StringComparer.Ordinal);
        var usedMaterialIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var table in request.Root.Elements("Table")) {
            string id=S(table,"id",Guid.NewGuid().ToString()), name=S(table,"name",id), materialName=S(table,"material",name);
            double width=D(table,"width",0), length=D(table,"length",0);
            if(width<=0 || length<=0)throw new ArgumentException("Table width and length must be positive inches.");
            double hue=D(table,"materialHue",0),saturation=D(table,"materialSaturation",0),lightness=D(table,"materialLightness",DefaultMaterialLightness),alpha=D(table,"materialAlpha",1),cost=D(table,"materialCost",DefaultMaterialCost);
            string materialId="";RollInstance instance=null;
            if(clipboard) {
                if(B(table,"populated",DefaultPopulated))throw new ArgumentException("Copy workspace patterns, not an entire nest.");
                // Project-only IDs may identify different rolls in the destination project.
                materialId=S(table,"clipboardCatalogId","");
            } else if(B(table,"materialAssigned",DefaultMaterialAssigned)) {
                double copySpacing=D(table,"copySpacing",DefaultCopySpacing),edgeSpacing=D(table,"edgeSpacing",DefaultEdgeSpacing),cutSpeed=D(table,"maxCutSpeed",DefaultMaximumCutSpeed),plotSpeed=D(table,"maxPlotSpeed",DefaultMaximumPlotSpeed);
                int minimumPower=int.Parse(S(table,"minimumPower",DefaultMinimumPower.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture),maximumPower=int.Parse(S(table,"maximumPower",DefaultMaximumPower.ToString(CultureInfo.InvariantCulture)),CultureInfo.InvariantCulture);
                bool enablePower=B(table,"enablePower",DefaultEnablePowerSettings);
                MaterialUnit unit;
                if(!Enum.TryParse<MaterialUnit>(S(table,"materialUnit",MaterialUnit.LinearYards.ToString()),true,out unit)||!Enum.IsDefined(typeof(MaterialUnit),unit))throw new ArgumentException("Unknown material cost unit.");
                double projectSpacing=D(table,"copySpacingOverride",copySpacing);
                if(projectSpacing<0)throw new ArgumentException("Copy spacing override must be nonnegative inches.");
                string baseRecipe=String.Join("\n",new[]{materialName,String.Join("\n",new[]{copySpacing,edgeSpacing,cutSpeed,plotSpeed,hue,saturation,lightness,alpha,cost}.Select(value=>value.ToString("R",CultureInfo.InvariantCulture))),enablePower.ToString(),minimumPower.ToString(CultureInfo.InvariantCulture),maximumPower.ToString(CultureInfo.InvariantCulture),unit.ToString()});
                materialId=S(table,"materialId",customIds[materialName]);
                string recipe=materialId+"\n"+width.ToString("R",CultureInfo.InvariantCulture)+"\n"+projectSpacing.ToString("R",CultureInfo.InvariantCulture)+"\n"+baseRecipe;
                MaterialTemplate template;
                string previous;
                if(recipesById.TryGetValue(materialId,out previous)&&previous!=baseRecipe)throw new ArgumentException("Material identifier is reused for incompatible catalog properties: "+materialId);
                recipesById[materialId]=baseRecipe;
                if(!templatesByRecipe.TryGetValue(recipe,out template)) {
                    // PatternSmith requires unique template IDs, including project-local width/spacing variants.
                    string templateId=materialId;
                    if(usedMaterialIds.Contains(templateId)) {
                        bool custom=System.Text.RegularExpressions.Regex.IsMatch(materialId,@"^c[1-9][0-9]*$");
                        int index=custom?1:2;
                        do {templateId=custom?"c"+index:materialId+"-"+index;index++;}
                        while(reservedMaterialIds.Contains(templateId)||usedMaterialIds.Contains(templateId));
                    }
                    usedMaterialIds.Add(templateId);
                    template=new RollTemplate(templateId,new NonnegativeDouble(projectSpacing),new NonnegativeDouble(edgeSpacing),new PositiveDouble(cutSpeed),new PositiveDouble(plotSpeed),new SolidMaterialPattern(width),materialName,new HSLColor(hue,saturation,lightness,alpha),cost,unit,new PowerLevelSettings(enablePower,minimumPower,maximumPower),false);
                    templatesByRecipe.Add(recipe,template);templates.Add(template);
                }
                materialId=template.MaterialId;
                instance=new RollInstance(template,new PositiveDouble(length),new Flaw[0]);
                instances.Add(instance);
            }
            var tablePatterns=new Dictionary<string,Pattern>();
            var copyLocations=new Dictionary<string,Point2d>();
            var copyRotations=new Dictionary<string,double>();
            var flipped=new HashSet<string>();
            var annotations=new HashSet<string>();
            foreach(var item in table.Elements("Pattern")) {
                string key=S(item,"id",Guid.NewGuid().ToString());
                Point2d copyLocation;
                var pattern=PatternFromRequest(item,out copyLocation,clipboard);
                if(B(item,"faceDown",false))pattern=pattern.With(name:pattern.Name+FaceDownSuffix);
                bool assigned=PatternHasMaterial(item,table)&&(!clipboard||materialId.Length>0);
                if(assigned&&instance==null&&!clipboard)throw new ArgumentException("A pattern material requires an assigned material group.");
                if(assigned)pattern=pattern.With(constraints:pattern.Constraints.With(materialId:new MaterialIdConstraint(materialId,
                    B(item,"enforceMaterial",B(table,"populated",DefaultPopulated))?MaterialConstraintType.Strict:MaterialConstraintType.Loose)),
                    patternFillColorStyle:DefaultPatternFillStyle,patternFillColor:new HSLColor(hue,saturation,lightness,alpha),patternFillStriped:DefaultPatternFillStriped);
                else pattern=pattern.With(patternFillColorStyle:PatternFillColorStyle.None,patternFillStriped:false);
                tablePatterns.Add(key,pattern);copyLocations.Add(key,copyLocation);copyRotations.Add(key,pattern.Rotation);
                if(B(item,"sourceFlipped",false))flipped.Add(key);
                if(item.Attribute("partState")!=null)partStates.Add(pattern.PersistentId,S(item,"partState",""));
                if(B(item,"annotation",false))annotations.Add(key);
            }
            if(tablePatterns.Count>0) {
              if(table.Attribute("workspaceX")!=null) {
                double angle=D(table,"workspaceRotation",0),x=D(table,"workspaceX",0),y=D(table,"workspaceY",0);
                foreach(string key in tablePatterns.Keys.ToArray()) {
                    var local=tablePatterns[key].Location;var pattern=tablePatterns[key].With(
                        location:new Point2d(x+local.X*Math.Cos(angle)-local.Y*Math.Sin(angle),y+local.X*Math.Sin(angle)+local.Y*Math.Cos(angle)),
                        rotation:copyRotations[key]+angle);
                    tablePatterns[key]=pattern;patterns.Add(pattern);
                }
              } else {
                var bounds=tablePatterns.Values.SelectMany(pattern=>WorkspaceEntities(pattern).Select(entity=>new {Box=entity.GetBounds(),Origin=pattern.Location})).ToArray();
                double minX=bounds.Min(item=>item.Box.Min.X+item.Origin.X),minY=bounds.Min(item=>item.Box.Min.Y+item.Origin.Y),maxY=bounds.Max(item=>item.Box.Max.Y+item.Origin.Y);
                workspaceY-=maxY-minY;
                foreach(string key in tablePatterns.Keys.ToArray()) {
                    var local=tablePatterns[key].Location;
                    var pattern=tablePatterns[key].With(location:new Point2d(local.X-minX,local.Y-minY+workspaceY));
                    tablePatterns[key]=pattern;patterns.Add(pattern);
                }
                workspaceY-=workspaceGap;
              }
            }
            var copies=new List<Copy>();
            if(B(table,"populated",DefaultPopulated))foreach(var pair in tablePatterns) {
                if(annotations.Contains(pair.Key))continue;
                var placements=table.Elements("Copy").Where(c=>S(c,"pattern","")==pair.Key).ToList();
                if(placements.Count==0)copies.Add(new Copy(pair.Value,copyLocations[pair.Key],copyRotations[pair.Key],flipped.Contains(pair.Key)?Axis.Y:Axis.None));
                else foreach(var place in placements) {
                    Axis flip; if(!Enum.TryParse<Axis>(S(place,"flip","None"),true,out flip) || (flip!=Axis.None && flip!=Axis.X && flip!=Axis.Y))throw new ArgumentException("Copy flip must be None, X or Y.");
                    copies.Add(new Copy(pair.Value,P(place),D(place,"rotation",copyRotations[pair.Key]),flip));
                }
            }
            foreach(var place in table.Elements("Copy"))if(!tablePatterns.ContainsKey(S(place,"pattern","")))throw new ArgumentException("Copy references unknown table pattern.");
            if(instance!=null)nests.Add(new Nest(instance,copies,name));
            else if(copies.Count>0)throw new ArgumentException("Material-free patterns cannot have nested copies.");
            Console.WriteLine("TABLE " + name + " length=" + length + " width=" + width + " patterns=" + tablePatterns.Count + " copies=" + copies.Count);
        }
        if(patterns.Count==0)throw new ArgumentException("Request must contain at least one pattern.");
        if(clipboard) {
            Progress(90,"Writing clipboard patterns");
            WriteWorkspaceClipboard(output,patterns,partStates);
            Progress(100,"Clipboard patterns ready");return 0;
        }
        if(nests.Count==0) {
            // The native project contract requires a current roll even when no pattern has a material.
            var table=request.Root.Element("Table");
            var template=new RollTemplate(EmptyProjectMaterialId,new NonnegativeDouble(DefaultCopySpacing),new NonnegativeDouble(DefaultEdgeSpacing),
                new PositiveDouble(DefaultMaximumCutSpeed),new PositiveDouble(DefaultMaximumPlotSpeed),new SolidMaterialPattern(D(table,"width",0)),
                EmptyProjectMaterialName,new HSLColor(0,0,DefaultMaterialLightness,1),DefaultMaterialCost,MaterialUnit.LinearYards,
                new PowerLevelSettings(DefaultEnablePowerSettings,DefaultMinimumPower,DefaultMaximumPower),false);
            var instance=new RollInstance(template,new PositiveDouble(D(table,"length",0)),new Flaw[0]);
            templates.Add(template);instances.Add(instance);nests.Add(new Nest(instance,new Copy[0],S(table,"name",EmptyProjectMaterialName)));
        }
        var materials=new MaterialCollection(templates,instances);
        if(templates.Select(template=>template.MaterialId).Distinct(StringComparer.Ordinal).Count()!=templates.Count)
            throw new InvalidDataException("Native material template identifiers must be unique.");
        Progress(75,"Validating native project");
        Phase("project validation");
        var project=new PresentationProject(patterns,materials,nests,new ReadOnlyDictionary<Layer,ToolReference>(ToolMap),Project.SavedState.NotPersisted,nests[0]);
        typeof(Project).GetMethod("Validate",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(project,null);
        string stage=output+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            Progress(80,"Rendering transparent preview");
            Phase("transparent preview");
            var preview=MakePreview(project);
            Progress(85,"Writing PatternSmith project");
            Phase("project serialization");
            using(var stream=new FileStream(stage,FileMode.CreateNew))using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))ProjectFileUtility.SaveAsProject(zip,project,preview);
            // Native SaveAsProject identifies the entry EXE in version.xml. Record
            // the actual PatternSmith model version for downstream compatibility.
            using(var stream=new FileStream(stage,FileMode.Open,FileAccess.ReadWrite))using(var zip=new ZipArchive(stream,ZipArchiveMode.Update)) {
                var old=zip.GetEntry("version.xml");if(old!=null)old.Delete();
                using(var version=zip.CreateEntry("version.xml").Open())new XDocument(new XElement("Versions",new XElement("PatternSmith",typeof(Project).Assembly.GetName().Version.ToString()))).Save(version);
                var document=ReadMergeProject(zip);XNamespace a="http://autometrix.com/xml",z="http://schemas.microsoft.com/2003/10/Serialization/";
                if(document.Root.Element(a+"Patterns").Elements().Count()!=patterns.Count || document.Root.Element(a+"Nests").Elements().Count()!=nests.Count)throw new InvalidDataException("Serialized object count mismatch.");
                var ids=document.Descendants().Attributes(z+"Id").Select(x=>x.Value).ToList();
                var uniqueIds=new HashSet<string>(ids);
                if(uniqueIds.Count!=ids.Count || document.Descendants().Attributes(z+"Ref").Any(x=>!uniqueIds.Contains(x.Value)))throw new InvalidDataException("Serialized object references are invalid.");
                bool resizedFonts=!B(request.Root,"legacyTableFontSizing",DefaultLegacyTableFontSizing)&&StorePatternSmith11FontSizes(document);
                if(partStates.Count>0)AddPartStates(document,partStates);
                if(resizedFonts||partStates.Count>0) {
                    zip.GetEntry("project.xml").Delete();
                    using(var data=zip.CreateEntry("project.xml",CompressionLevel.Optimal).Open())document.Save(data,SaveOptions.DisableFormatting);
                }
            }
            // Validate constructed objects and plain XML. MaterialCollection's
            // OnDeserialized callback imports materials into the workstation's
            // global library; an exporter must never invoke that callback.
            int verified=0;
            Phase("export verification");
            foreach(var pattern in project.Patterns) {
                Progress(90+verified++*8/project.Patterns.Count,"Checking "+pattern.Name);
                if(pattern.Entities.Any(entity=>!entity.IsValid()))throw new InvalidDataException("Native model has invalid geometry.");
                ValidateDerivedPerimeter(pattern);
            }
            foreach(var copy in project.Nests.SelectMany(n=>n.Copies)) {
                if(!project.Patterns.Any(p=>Object.ReferenceEquals(p,copy.Source)))throw new InvalidDataException("Copy source is outside the project pattern collection.");
                var bounds=copy.GetBounds();
                foreach(double coordinate in new[]{bounds.Min.X,bounds.Min.Y,bounds.Max.X,bounds.Max.Y})if(Double.IsNaN(coordinate)||Double.IsInfinity(coordinate))throw new InvalidDataException("Copy has invalid bounds.");
            }
            File.Move(stage,output);
        } finally { if(File.Exists(stage))File.Delete(stage); }
        Console.WriteLine("SUCCESS " + output + " patterns=" + patterns.Count + " nests=" + nests.Count);
        Phase("complete");PhaseClock.Stop();
        Progress(100,"Native project ready");
        return 0;
    }
}

#else
namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Drawing;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Reflection;
  using System.Text;
  using System.Threading;
  using System.Threading.Tasks;
  using System.Xml.Linq;
  using System.Text.Json.Nodes;
  using System.Buffers.Binary;
  using System.Collections.ObjectModel;
  using System.IO.Pipes;
  using System.Xml;
  using static vTools.PatternSmithCatalog;
  using Environment=System.Environment;
  using Rhino;
  using Rhino.Commands;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using Rhino.UI;

  /// <summary>Exports prepared vTools parts and placed vNest tables to a native PatternSmith project.</summary>
  [CommandStyle(Style.ScriptRunner)]
  public sealed partial class vExportPatternSmith : vToolsCommand
  {
    // Defaults and output conventions
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.StandaloneMaterialWidthInches","Positive material width in inches for standalone parts without a vNest table.")]
    private const double StandaloneMaterialWidthInches=60.0; // Positive material width in inches when no placed vNest table owns a part.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.StandaloneMaterialLengthInches","Positive unpopulated-nest length in inches for standalone parts.")]
    private const double StandaloneMaterialLengthInches=140.0; // Positive material length in inches for the unpopulated standalone-pattern nest.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.StandaloneTableName","Display name for the unpopulated nest containing standalone parts.")]
    private const string StandaloneTableName="Patterns"; // Display name for the unpopulated standalone-pattern nest.
    private const string DefaultFileStem="untitled"; // Filename stem used for an unsaved Rhino document.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.RecentOutputLimit","Maximum successful export filenames available in the editable filename dropdown; one or greater. The current drawing supplies the default filename.")]
    private const int RecentOutputLimit=20; // Positive maximum recent filenames in the dropdown; newest first, never selected automatically.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MergeGapInches","Nonnegative inches between existing workspace objects and the new layout when merging. Existing objects and all nest-local placements stay unchanged.",OptionKeys="mergeGapInches")]
    private const double MergeGapInches=36; // Nonnegative native inches of workspace separation; the incoming layout is top-aligned to the existing layout.
    private const int MergeGapDecimalPlaces=3; // Decimal places displayed for native-inch merge spacing.
    private const string MergeTemporaryPrefix="vTools-merged-"; // Disposable candidate filename prefix; the destination is changed only after successful verification.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CurveConversionWorkers","Independent native spline conversion workers: 0 selects automatically; 1 through 32 sets an explicit count. Geometry tolerances are unchanged.")]
    private const int CurveConversionWorkers=0; // Zero selects automatic parallelism; one is serial, two through 32 permit independent geometry workers.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.AutomaticCurveWorkerLimit","Maximum automatic native spline conversion workers, from 1 through 32; automatic selection also leaves one logical CPU available.")]
    private const int AutomaticCurveWorkerLimit=4; // Positive automatic worker ceiling, one through 32; explicit CurveConversionWorkers bypasses this preference.
    private const string TableAnnotationPerimeter="Element"; // Native element-derived frame for non-cutting table-information patterns.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.TableLabelFontFamily","Installed Windows font family for editable PatternSmith table headers. Other exported labels retain their existing stroke font.")]
    private const string TableLabelFontFamily="Arial"; // Installed Windows font family; exported in the Regular style.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultTargetVersion","PatternSmith version targeted by the export window. PatternSmith10 uses legacy editable table fonts; PatternSmith11 uses physical point sizes.",OptionKeys=TargetVersionKey)]
    private const PatternSmithTargetVersion DefaultTargetVersion=PatternSmithTargetVersion.PatternSmith11; // Default target when no compatible choice has been saved.
    private const bool LegacyTableLabelFontSizing=false; // Migration fallback: true selects PS10; false uses the factory target.
    private const string TargetVersionKey="target_version"; // Saved enum name for the selected PatternSmith export target.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CatalogReconnectIntervalMilliseconds","Milliseconds between nonblocking material-library retries while the export window is open; zero disables automatic retries. Manual Reconnect remains available.")]
    private const int CatalogReconnectIntervalMilliseconds=10000; // Zero disables automatic reconnect; positive milliseconds bound the retry frequency after server/file failures.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.PublishTouchDelayMilliseconds","Nonnegative milliseconds between safely replacing a saved project and updating its write time so PatternSmith can prompt to reload it.")]
    private const int PublishTouchDelayMilliseconds=250; // Nonnegative milliseconds for a destination watcher to finish handling the replacement rename; applies only to final exports.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.UnsupportedHighlightMarkerPixels","Minimum screen-pixel cue for ignored objects; one or greater. Highlights exist only while the export window is open.")]
    private const int UnsupportedHighlightMarkerPixels=12; // Positive fixed-pixel minimum cue/point diameter for temporary unsupported-object feedback.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.UnsupportedHighlightBounds","True adds outlined screen-space locators around skipped objects at every zoom; false shows only their ordinary object overlay and distant markers.")]
    private const bool UnsupportedHighlightBounds=true; // true shows a bounding rectangle and center cue for each visible skipped object; false omits those locators.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultHighlightFindings","True shows cleanup finding markers; false keeps findings and deletion review available without drawing their markers.",OptionKeys="highlight_findings")]
    private const bool DefaultHighlightFindings=true; // true draws current overlap/short findings; false hides only their temporary feedback.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPreviewDelete","True initially shows the hypothetical deletion result when findings are ready; false initially shows original drawing geometry. Preview never changes the export decision or originals.")]
    private const bool DefaultPreviewDelete=false; // true enables deletion preview on first review; false starts in the unmodified drawing view.
    [global::vTools.CommandHighlightDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.UnsupportedHighlightStyle","Unsupported object","#FFFF8000","#FF000000","#FF000000",0,StrokeEmphasis=6,OutlineEmphasis=8)]
    private static readonly PreviewDisplay.ObjectHighlightStyle UnsupportedHighlightStyle=global::vTools.CommandDefaults.Highlight("vExportPatternSmith","vTools.Commands.vExportPatternSmith.UnsupportedHighlightStyle",
      new(System.Drawing.Color.FromArgb(255,128,0),System.Drawing.Color.Black,System.Drawing.Color.Black,1){StrokeEmphasis=6,OutlineEmphasis=8,UseObjectCurveColor=false}); // Orange/black screen-space feedback; zero fill opacity leaves original shaded objects unobscured.
    private const string NestedRotationKey="vNest.rotation"; // Finite invariant-culture degrees saved on placed part objects; original parts without this key use zero.
    private const string NestedFlipKey="vNest.flipped"; // true marks an actual mirrored placement; false or absent leaves the copy unmirrored.
    private const double RotationMetadataTolerance=1e-9; // Fixed degree consistency tolerance between members of one placed part; does not change output coordinates.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MaximumBlockDepth","Positive recursion limit for prepared blocks; rejects deeper definitions without exporting an incomplete file.")]
    private const int MaximumBlockDepth=32; // Positive maximum number of recursively nested block definitions.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ApproximationAngleRadians","Positive kink angle in radians; cubic fitting preserves corners sharper than this angle.")]
    private const double ApproximationAngleRadians=Math.PI/180.0; // Positive kink angle in radians for cubic fitting of rational or higher-degree curves.
    private const double CubicFitToleranceDivisor=4.0; // Use one quarter of the permitted error for cubic fitting, leaving room for endpoint alignment and native refitting.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.PointMarkerRadiusInches","Positive native point marker radius in inches; does not resize curves or text.")]
    private const double PointMarkerRadiusInches=0.125; // Positive reference-point marker radius in native PatternSmith inches.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.TextTransformRelativeTolerance","Positive relative tolerance when checking text for uniform scale and perpendicular axes; invalid transforms are rejected.")]
    private const double TextTransformRelativeTolerance=1e-10; // Positive relative tolerance for native text's required uniform, orthogonal plane transform.
    private const int PrimaryToolIndex=0; // Zero-based default slot for each named processing tool; CUT2 uses slot one.
    internal const string NativeBuilderResourceName="vTools.PatternSmith.Builder.exe"; // Manifest resource identifier of the isolated .NET Framework serializer helper.
    private const string SupportedPatternSmithVersion="10.2.17.873"; // Exact installed native schema/API version validated by the isolated helper.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.BuilderTimeoutMilliseconds","Positive maximum milliseconds for the isolated native writer; timeout preserves the existing destination.")]
    private const int BuilderTimeoutMilliseconds=120000; // Positive maximum milliseconds allowed for native serialization.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.BuilderPollMilliseconds","Positive milliseconds between native writer status checks while keeping Rhino responsive.")]
    private const int BuilderPollMilliseconds=50; // Positive milliseconds per wait slice while pumping Rhino UI events.
    private const string RuntimeFolderName="vTools"; // Single temporary-directory component below the user's temp root.
    private const string BuilderFolderName="PatternSmith"; // Single temporary-directory component below RuntimeFolderName.
    private const string NativeOutputFileName="output.project"; // Short native-writer filename in its isolated runtime directory; avoids classic .NET path limits.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultCurveMode","NativeSpline writes editable fit-point splines; Bezier keeps exact cubic control points where possible. Other smooth curves are fitted within tolerance.",OptionKeys="curve_mode")]
    [global::vTools.CommandChoices("vExportPatternSmith","curve_mode",false,"NativeSpline","Bezier")]
    private const string DefaultCurveMode="NativeSpline"; // NativeSpline or Bezier; analytic lines, arcs, circles and polylines retain their native types.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultCurveErrorInches","Maximum native spline thinning error in inches, zero or greater; zero disables Thin. Bezier output is not thinned.",OptionKeys="curve_error")]
    private const double DefaultCurveErrorInches=0.010; // Nonnegative inches, independent of Rhino document units; zero leaves converted fit points unthinned.
    private const int CurveErrorDecimalPlaces=3; // Displayed fractional inches in the thinning-error field, matching the 0.010-inch factory value.
    private const double CurveErrorIncrementInches=0.001; // Positive numeric-step size in inches for spline thinning error.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CacheSplineConversions","True retains validated conversions between exports; false limits conversion reuse to the current export window.")]
    private const bool CacheSplineConversions=true; // true enables persistent reuse; false keeps only a disposable per-window cache.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ReuseEquivalentSplines","True validates and reuses moved/rotated curve copies; false converts differently positioned curves independently.")]
    private const bool ReuseEquivalentSplines=true; // true checks equivalent local shapes for reuse; false uses exact-coordinate cache entries only.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.SplineCacheMegabytes","Positive disk conversion cache budget in MiB; oldest entries are evicted first.")]
    private const int SplineCacheMegabytes=256; // Positive MiB budget, clamped to 1-4096 for each SDK/cache revision.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DistanceIndexLeafSegments","Positive maximum number of native line-distance tests per spatial-index leaf.")]
    private const int DistanceIndexLeafSegments=16; // Positive leaf size, clamped to 1-256; geometry error tolerances remain unchanged.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.NativeProgressIntervalMilliseconds","Minimum milliseconds between same-stage native progress updates; zero disables throttling.")]
    private const int NativeProgressIntervalMilliseconds=100; // Nonnegative progress interval; phase changes and completion are always reported.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ConversionRestartDelayMilliseconds","Nonnegative debounce delay before geometry-setting edits restart background conversion.")]
    private const int ConversionRestartDelayMilliseconds=250; // Nonnegative milliseconds after the most recent geometry-setting change.
    private const int MaterialAssignmentVersion=1; // Supported document-local part-settings schema; newer schemas are rejected rather than guessed.
    private const int MaximumMaterialAssignmentCharacters=16384; // Positive JSON character limit when reading an assigned material/recipe.
    private const string MixedFacesSuffix=" [Mixed faces]"; // Stable cutting instruction for approved tables containing both face-up and face-down parts.
    private const string SplineCacheFolderName="SplineCache"; // Single per-user cache-directory component, independent of any drawing filename.
    private const int SessionFilenameLimit=128; // Positive maximum number of drawing-session filenames retained in memory; never persisted as defaults.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.WorkspaceGapInches","Nonnegative inches separating each table's patterns in the definition workspace; does not move any nested copy.")]
    private const double WorkspaceGapInches=12.0; // Nonnegative workspace-only gap in inches.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingDialogWidth","Positive mapping-dialog width in logical screen pixels.")]
    private const int MappingDialogWidth=740; // Positive logical pixels for the material/tool mapping dialog.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingDialogHeight","Positive mapping-dialog height in logical screen pixels; long mapping lists scroll.")]
    private const int MappingDialogHeight=520; // Positive logical pixels; the material/tool lists scroll rather than expanding the window.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingOverviewHeight","Positive logical-pixel height of the Rhino-to-PatternSmith material mapping overview.")]
    private const int MappingOverviewHeight=128; // Positive logical pixels; longer mapping lists scroll independently of the folder browser.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingNameColumnWidth","Positive logical-pixel width for individual material/tool names; the material mapping column combines two names and its folder allowance.")]
    private const int MappingNameColumnWidth=210; // Positive initial name-column width in logical screen pixels.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingGroupColumnWidth","Positive logical-pixel width reserved for folder paths in the combined material mapping column.")]
    private const int MappingGroupColumnWidth=180; // Positive folder-path contribution to the consolidated mapping-column width, in logical pixels.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingIdColumnWidth","Positive initial logical-pixel width for PatternSmith material IDs.")]
    private const int MappingIdColumnWidth=80; // Positive initial native-ID column width in logical screen pixels.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MappingWidthColumnWidth","Positive initial logical-pixel width for catalog material widths in inches.")]
    private const int MappingWidthColumnWidth=90; // Positive catalog-width column width in logical screen pixels.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CustomMaterialSaturation","Saturation of randomly colored project-only materials, from zero through one.")]
    private const double CustomMaterialSaturation=0.65; // HSL saturation from zero through one; existing saved colors remain unchanged.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.CustomMaterialLightness","Lightness of randomly colored project-only materials, from zero through one.")]
    private const double CustomMaterialLightness=0.60; // HSL lightness from zero through one; existing saved colors remain unchanged.
    private const int MappingSpacing=8; // Logical screen pixels separating native mapping controls.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ProgressStatusHeight","Minimum status-bar message height in logical pixels; positive, grows for wrapped stage, findings and error messages.")]
    private const int ProgressStatusHeight=36; // Positive minimum logical-pixel height; longer messages can wrap without clipping.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ProgressTickMilliseconds","Positive milliseconds between elapsed-time display updates during native export.")]
    private const int ProgressTickMilliseconds=100; // Positive milliseconds; affects UI feedback, not geometry or native writer work.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepNotchTrim","PS-only notch treatment: No, Split or Trim; independent of vExportDXF.",OptionKeys="notch_trim")]
    private const NotchTrimMode DefaultPrepNotchTrim=NotchTrimMode.Trim; // No preserves contacts; Split relocates between-leg pieces; Trim omits them from copies.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepSplitLayer","PS-only Rhino layer path for between-leg pieces in Split mode.",OptionKeys="notch_split_layer")]
    private const string DefaultPrepSplitLayer="Reference"; // Full Rhino layer path; only temporary export copies use it.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepExplode","PS-only curve decomposition: No, SplitAtCorners or Full.",OptionKeys="explode")]
    private const vExportDXF.ExportExplodeMode DefaultPrepExplode=vExportDXF.ExportExplodeMode.Full; // No keeps curves; SplitAtCorners separates kinks; Full separates components.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepNotchJoin","True joins V/U notch geometry in PS export copies; false leaves notch components separate.",OptionKeys="notch_join")]
    private const bool DefaultPrepNotchJoin=true; // true joins eligible notch contacts; false preserves separate components.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultNativeEffects","On: keep verified seam allowances and notches editable in PatternSmith.\nOff: export their prepared curves.",OptionKeys="native_effects")]
    private const bool DefaultNativeEffects=false; // true tries native editable effects; false retains prepared curve export.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepTextBreak","True exports multiline text as separate rows; false preserves multiline entities.",OptionKeys="text_break")]
    private const bool DefaultPrepTextBreak=true; // true splits rows; false preserves multiline text.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepOptimize","True cleans temporary PS export copies; false skips optimization.",OptionKeys="optimize")]
    private const bool DefaultPrepOptimize=true; // true runs PS-owned cleanup settings; false skips cleanup.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepCleanupAction","PS-only cleanup findings action: Ask, Delete or Ignore.",OptionKeys="cleanup_action")]
    private const vCleanup.ExportCleanupAction DefaultPrepCleanupAction=vCleanup.ExportCleanupAction.Ask; // Ask reviews findings; Delete removes only copies; Ignore preserves findings.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepRemoveGrain","True omits grain guides from PS output; false includes them.",OptionKeys="remove_grain")]
    private const bool DefaultPrepRemoveGrain=true; // true removes export-copy guides; false retains them.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepMoveOrigin","True moves temporary PS export copies to the origin; false retains drawing coordinates.",OptionKeys="move_origin")]
    private const bool DefaultPrepMoveOrigin=true; // true applies export-only translation; false preserves source coordinates.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepEndTrim","PS-only touching non-cut endpoint shortening in model units; zero disables.",OptionKeys="end_trim")]
    private const double DefaultPrepEndTrim=0.02; // Nonnegative model-unit length; source curves are unchanged.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepShortThreshold","PS-only short-geometry threshold in model units; zero disables short detection.",OptionKeys="cleanup_threshold")]
    private const double DefaultPrepShortThreshold=0.02; // Nonnegative model-unit length.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepSimplify","True simplifies PS export copies before analysis; false keeps curve structure.",OptionKeys="cleanup_simplify")]
    private const bool DefaultPrepSimplify=true; // true simplifies copies; false preserves structure.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepOverlaps","True finds overlaps in PS export copies; false skips overlap detection.",OptionKeys="cleanup_overlaps")]
    private const bool DefaultPrepOverlaps=true; // true detects overlaps; false skips them.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepOverlapTolerance","PS-only overlap tolerance in model units; greater than zero.",OptionKeys="cleanup_tolerance")]
    private const double DefaultPrepOverlapTolerance=0.001; // Positive model-unit distance.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.DefaultPrepOverlapSegments","True removes overlapping curve portions only; false removes chosen whole overlapping curves.",OptionKeys="cleanup_segments")]
    private const bool DefaultPrepOverlapSegments=true; // true splits overlapping portions on copies; false uses whole-curve cleanup.
    private const int MaximumToolIndex=31; // Zero-based native preset-index ceiling accepted in saved/exported mappings; never presented as a mount number.
    private static readonly string[] ToolProcesses=["Cut","Mark","Reference","Punch","Drill","Crease","Print","Inkjet","Notch"]; // Native processing roles; Inkjet is displayed as Dot Mark, never as a separate tool category.
    private static readonly string[] ToolCategories=["Marking","Cutting","Drilling","Creasing"]; // PatternSmith category display order; configured preset order is retained within each category.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerMaximumHeight","Positive logical-pixel maximum height of the expanded PatternSmith tool tree; longer lists scroll.")]
    private const int ToolPickerMaximumHeight=360; // Positive logical pixels for a tool-picker popup; not the mapping window height.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerRowHeight","Positive tool-row height in logical pixels; expands if needed to fit the text and swatch.")]
    private const int ToolPickerRowHeight=20; // Positive logical pixels per tool row, before any required content-fit expansion.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerRowGap","Nonnegative vertical gap in logical pixels between tool rows, outside their hover and selection backgrounds.")]
    private const int ToolPickerRowGap=3; // Nonnegative logical pixels after each tool row; zero joins adjacent row backgrounds.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerSwatchSize","Positive square tool-swatch size in logical pixels; rows retain a visible gap above and below it.")]
    private const int ToolPickerSwatchSize=14; // Positive logical pixels; row height expands to leave at least one pixel above and below.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerSwatchBorder","Nonnegative logical-pixel border thickness around tool swatches; zero hides the border.")]
    private const double ToolPickerSwatchBorder=1; // Nonnegative logical pixels, limited to half the swatch size.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerSwatchGap","Nonnegative logical-pixel spacing between a tool swatch and its name.")]
    private const int ToolPickerSwatchGap=6; // Nonnegative logical pixels between each color square and its label.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ToolPickerHoverColor","Full-row background color when hovering over an unselected tool; selected rows retain system selection colors.","#FFE8F2FC")]
    private static readonly Color ToolPickerHoverColor=Color.FromArgb(232,242,252); // ARGB hover color; alpha zero disables visible hover fill, without changing selection.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.ConfiguredToolsPath","Optional PatternSmith tools.xml path; empty reads the shared Autometrix Plotting configuration.")]
    private const string ConfiguredToolsPath=""; // Empty uses CommonApplicationData/Autometrix/Plotting/tools.xml; otherwise a full XML path.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.MaximumToolSettingsCharacters","Positive XML character limit for reading PatternSmith tool and plotter settings; range 1024 through 67108864.")]
    private const long MaximumToolSettingsCharacters=4194304; // Positive XML character limit for read-only tool and plotter settings.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.PreparationChoiceWidth","Positive preparation-dropdown width in logical screen pixels.")]
    private const int PreparationChoiceWidth=190; // Logical-pixel width of compact preparation dropdowns.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.PreparationNumberWidth","Positive preparation numeric-field width in logical screen pixels.")]
    private const int PreparationNumberWidth=110; // Logical-pixel width of preparation numeric inputs.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.EtaMinimumProgressSamples","Positive distinct stage-progress samples required before extrapolating its measured rate.")]
    private const int EtaMinimumProgressSamples=3; // Distinct progress samples needed before estimating a stage from its measured rate.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.EtaMinimumElapsedSeconds","Positive elapsed stage seconds required before extrapolating its measured rate.")]
    private const double EtaMinimumElapsedSeconds=1; // Minimum measured stage seconds before extrapolating progress.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.EtaMinimumProgressFraction","Stage progress fraction required before extrapolation, greater than zero and less than one.")]
    private const double EtaMinimumProgressFraction=.05; // Minimum zero-to-one stage fraction before extrapolating its measured rate.
    [global::vTools.CommandDefault("vExportPatternSmith","vTools.Commands.vExportPatternSmith.EtaMaximumWorkloadScale","Maximum workload multiplier or divisor trusted from previous stage timings; one or greater.")]
    private const double EtaMaximumWorkloadScale=4; // Largest trusted workload multiplier or divisor for a previous successful stage timing.

    private static readonly Dictionary<uint,(string Drawing,string Output)> SessionFilenames=[];
    private enum PatternSmithTargetVersion {PatternSmith10,PatternSmith11}
    internal enum ExistingFileAction {Cancel,Overwrite,Merge}
    private static string ProjectFileHash(string path)
    {
      using var stream=File.OpenRead(path);
      return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
    private static string SessionOutputPath(RhinoDoc doc,string fallback)=>SessionFilenames.TryGetValue(doc.RuntimeSerialNumber,out var entry)&&
      entry.Drawing.Equals(doc.Path??"",StringComparison.OrdinalIgnoreCase)?entry.Output:fallback;
    private static void RememberSessionOutput(RhinoDoc doc,string output)
    {
      SessionFilenames[doc.RuntimeSerialNumber]=(doc.Path??"",output);
      while(SessionFilenames.Count>SessionFilenameLimit)SessionFilenames.Remove(SessionFilenames.Keys.First());
    }
    private sealed record ExportTable(int Group,Plane Plane,double Width,double Length,string Material,string Name,HashSet<Guid> Members,double Gap)
    {internal bool FaceDown {get;init;}=NestWorkflow.DefaultFaceDown;}
    private sealed record Tool(string Process,int Index,string? SourceLayer=null);
    private sealed record CurveConversionSettings(string Mode,double ThinError);
    private sealed record PartMaterial(string Name,double Width,double Gap);
    private sealed class PartMaterials
    {
      private readonly RhinoDoc _doc;
      private readonly Dictionary<string,PartMaterial?> _records=new(StringComparer.Ordinal);
      private readonly Dictionary<Guid,PartMaterial?> _objects=[];
      private readonly Dictionary<string,MaterialLibrary.MaterialPreset> _presets;
      internal PartMaterials(RhinoDoc doc)
      {
        _doc=doc;
        _presets=MaterialLibrary.LoadMaterialPresets().Concat(MaterialLibrary.LoadMaterialDrafts())
          .GroupBy(material=>material.Name,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.Last(),StringComparer.OrdinalIgnoreCase);
      }
      internal PartMaterial? ForObjects(IEnumerable<Guid> ids)
      {
        var found=ids.Select(ForObject).Where(material=>material!=null)
          .DistinctBy(material=>(material!.Name.ToUpperInvariant(),material.Width,material.Gap)).ToArray();
        if(found.Length>1)throw new InvalidOperationException("A standalone export part contains conflicting saved material assignments; export its material parts separately.");
        return found.SingleOrDefault();
      }
      private PartMaterial? ForObject(Guid id)
      {
        if(_objects.TryGetValue(id,out var saved))return saved;
        if(_doc.Objects.FindId(id) is not {IsDeleted:false} obj)return null;
        string? key=(obj.Attributes.GetGroupList()??[]).Where(index=>_doc.Groups[index] is {IsDeleted:false})
          .OrderBy(index=>_doc.Groups.GroupObjectCount(index)).Select(index=>PartGroupData.GroupKey(_doc.Groups[index].Id))
          .FirstOrDefault(candidate=>!string.IsNullOrEmpty(_doc.Strings.GetValue(PartGroupData.NestSection,candidate)));
        string boundaryKey="boundary-"+id.ToString("N");
        if(key==null&&!string.IsNullOrEmpty(_doc.Strings.GetValue(PartGroupData.NestSection,boundaryKey)))key=boundaryKey;
        if(key==null&&obj.Attributes.GetUserString(PartGroupData.SourceSettingsKey) is {Length:>0} sourceKey)key=sourceKey;
        key??=boundaryKey;
        if(!_records.TryGetValue(key,out saved))
        {
          string? json=_doc.Strings.GetValue(PartGroupData.NestSection,key);
          if(!string.IsNullOrEmpty(json))
          {
            if(json.Length>MaximumMaterialAssignmentCharacters)throw new InvalidOperationException("Saved part material data is too large.");
            using var data=System.Text.Json.JsonDocument.Parse(json);var root=data.RootElement;
            if(root.TryGetProperty("Version",out var version)&&version.GetInt32()!=MaterialAssignmentVersion)
              throw new InvalidOperationException("The saved part material uses an unsupported settings version.");
            if(root.TryGetProperty("Material",out var name)&&name.ValueKind==System.Text.Json.JsonValueKind.String&&
              !string.IsNullOrWhiteSpace(name.GetString()))
            {
              string materialName=MaterialLibrary.MaterialPresetName(name.GetString()!);
              if(!_presets.TryGetValue(materialName,out var recipe)&&root.TryGetProperty("Recipe",out var stored)&&stored.ValueKind==System.Text.Json.JsonValueKind.Object)
                recipe=System.Text.Json.JsonSerializer.Deserialize<MaterialLibrary.MaterialPreset>(stored.GetRawText());
              double scale=RhinoMath.UnitScale(_doc.ModelUnitSystem,UnitSystem.Inches);
              bool valid=recipe!=null&&recipe.Name.Equals(materialName,StringComparison.OrdinalIgnoreCase)&&double.IsFinite(recipe.Width)&&recipe.Width>0&&double.IsFinite(recipe.Gap)&&recipe.Gap>=0;
              saved=new(materialName,valid?recipe!.Width*scale:Config(nameof(StandaloneMaterialWidthInches),StandaloneMaterialWidthInches),valid?recipe!.Gap*scale:0);
            }
          }
          _records[key]=saved;
        }
        return _objects[id]=saved;
      }
    }
    private static CurveConversionSettings ReadCurveConversionSettings()=>ToolsOptionStore.Read("vExportPatternSmith",section=>new CurveConversionSettings(
      ToolsOptionStore.TryGetString(section,"curve_mode",out var mode)?mode:Config(nameof(DefaultCurveMode),DefaultCurveMode),
      ToolsOptionStore.TryGetDouble(section,"curve_error",out double error)&&double.IsFinite(error)&&error>=0?error:Math.Max(0,Config(nameof(DefaultCurveErrorInches),DefaultCurveErrorInches))));
    private static string ToolProcessName(string process)=>process=="Inkjet"?"Dot Mark":process;
    private sealed record ConfiguredTool(string Id,string Name,string Process,int Index,Eto.Drawing.Color? Color=null,string Mount="")
    {
      internal string Key=>Id.Length>0?Id:Process+":"+Index.ToString(CultureInfo.InvariantCulture);
      internal string Category=>Process switch {"Mark" or "Inkjet" or "Print"=>"Marking","Cut" or "Notch"=>"Cutting","Drill" or "Punch"=>"Drilling","Crease"=>"Creasing",_=>""};
      internal string MountName=>Process is "Cut" or "Notch" or "Punch" or "Crease"
        ?Mount switch {"Steered1"=>"Steer1","Steered2"=>"Steer2","Any"=>"Any",_=>Mount}:"";
      internal string Description=>ToolProcessName(Process)+(MountName.Length>0?" ["+MountName+"]":"");
      public override string ToString()=>Name;
    }
    private sealed record ConfiguredCutter(string Id,string Name,IReadOnlyList<ConfiguredTool> Tools);
    private sealed record ToolConfiguration(string Source,string Plotter,IReadOnlyList<ConfiguredTool> Tools,string Warning)
    {
      internal IReadOnlyList<ConfiguredCutter> Cutters {get;init;}=[];
      internal string? SelectedCutterId {get;init;}
    }
    private sealed class ToolPicker
    {
      internal readonly Eto.Forms.DropDown Choice;
      internal readonly Eto.Forms.Label Details=new(){Wrap=Eto.Forms.WrapMode.Word,VerticalAlignment=Eto.Forms.VerticalAlignment.Center};
      internal readonly System.Windows.Controls.TreeView Tree=new(){BorderThickness=new(0)};
      private IReadOnlyList<ConfiguredTool> _tools=[];
      private readonly Dictionary<string,System.Windows.Controls.TreeViewItem> _nodes=new(StringComparer.OrdinalIgnoreCase);
      private readonly Dictionary<System.Windows.Controls.TreeViewItem,(System.Windows.Controls.Border Row,System.Windows.Controls.ContentPresenter Header,double HeaderOffset)> _rowFrames=[];
      private readonly double _rowHeight,_rowGap,_swatchSize,_swatchBorder,_swatchGap;
      private readonly System.Windows.Media.Brush _hoverBrush;
      private System.Windows.Controls.ComboBox? _native;
      private bool _syncing,_updatingRows;
      internal ConfiguredTool? Selected=>_tools.FirstOrDefault(tool=>tool.Key.Equals(Choice.SelectedKey,StringComparison.OrdinalIgnoreCase));
      internal ToolPicker(IReadOnlyList<ConfiguredTool> tools,ConfiguredTool? selected,Func<Eto.Drawing.Color,Eto.Drawing.Bitmap> swatch)
      {
        _swatchSize=Math.Max(1,Config(nameof(ToolPickerSwatchSize),ToolPickerSwatchSize));
        _rowHeight=Math.Max(Math.Max(Tree.FontSize+2,_swatchSize+2),Config(nameof(ToolPickerRowHeight),ToolPickerRowHeight));
        _rowGap=Math.Max(0,Config(nameof(ToolPickerRowGap),ToolPickerRowGap));
        _swatchBorder=Math.Clamp(Config(nameof(ToolPickerSwatchBorder),ToolPickerSwatchBorder),0,_swatchSize/2);
        _swatchGap=Math.Max(0,Config(nameof(ToolPickerSwatchGap),ToolPickerSwatchGap));
        var hover=global::vTools.CommandDefaults.GetColor("vExportPatternSmith","vTools.Commands.vExportPatternSmith."+nameof(ToolPickerHoverColor),ToolPickerHoverColor);
        var hoverBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(hover.A,hover.R,hover.G,hover.B));hoverBrush.Freeze();_hoverBrush=hoverBrush;
        Tree.HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch;
        Choice=new(){Width=Config(nameof(MappingNameColumnWidth),MappingNameColumnWidth),ToolTip="Choose a configured PatternSmith tool. Categories are expanded; process and mount are shown beside the picker."};
        Choice.ItemImageBinding=Eto.Forms.Binding.Property<Eto.Forms.ImageListItem,Eto.Drawing.Image>(item=>item.Image!);
        Tree.PreviewMouseDown+=(_,e)=>
        {
          if(e.ChangedButton!=System.Windows.Input.MouseButton.Left)return;
          System.Windows.DependencyObject? source=e.OriginalSource as System.Windows.DependencyObject;
          while(source!=null&&source is not System.Windows.Controls.TreeViewItem)
            source=source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
              ?System.Windows.Media.VisualTreeHelper.GetParent(source):System.Windows.LogicalTreeHelper.GetParent(source);
          if(source is System.Windows.Controls.TreeViewItem {Tag:ConfiguredTool tool}){Commit(tool);e.Handled=true;}
        };
        Tree.PreviewKeyDown+=(_,e)=>
        {
          if(e.Key==System.Windows.Input.Key.Enter&&Tree.SelectedItem is System.Windows.Controls.TreeViewItem {Tag:ConfiguredTool tool}){Commit(tool);e.Handled=true;}
          else if(e.Key==System.Windows.Input.Key.Escape&&_native!=null){_native.IsDropDownOpen=false;_native.Focus();e.Handled=true;}
        };
        Choice.Load+=(_,_)=>Install();Choice.SelectedIndexChanged+=(_,_)=>Refresh();
        SetTools(tools,selected,swatch);Install();
      }
      internal void SetTools(IReadOnlyList<ConfiguredTool> tools,ConfiguredTool? selected,Func<Eto.Drawing.Color,Eto.Drawing.Bitmap> swatch)
      {
        if(_native!=null)_native.IsDropDownOpen=false;
        _tools=tools;Choice.Items.Clear();Tree.Items.Clear();_nodes.Clear();_rowFrames.Clear();
        foreach(var tool in tools)Choice.Items.Add(new Eto.Forms.ImageListItem {Key=tool.Key,Text=tool.Name,Image=swatch(tool.Color??Eto.Drawing.Colors.Transparent)});
        foreach(string category in ToolCategories)
        {
          var entries=tools.Where(tool=>tool.Category==category).ToArray();if(entries.Length==0)continue;
          var branch=new System.Windows.Controls.TreeViewItem {Header=category,IsExpanded=true,FontWeight=System.Windows.FontWeights.SemiBold,HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch};
          foreach(var tool in entries)branch.Items.Add(Node(tool));Tree.Items.Add(branch);
        }
        foreach(var tool in tools.Where(tool=>tool.Category.Length==0))Tree.Items.Add(Node(tool));
        Choice.SelectedIndex=-1;if(selected!=null)Choice.SelectedKey=selected.Key;Refresh();
      }
      private System.Windows.Controls.TreeViewItem Node(ConfiguredTool tool)
      {
        var header=new System.Windows.Controls.StackPanel {Orientation=System.Windows.Controls.Orientation.Horizontal,VerticalAlignment=System.Windows.VerticalAlignment.Center};
        var color=tool.Color??Eto.Drawing.Colors.Transparent;
        double verticalGap=(_rowHeight-_swatchSize)/2;
        header.Children.Add(new System.Windows.Controls.Border {Width=_swatchSize,Height=_swatchSize,Margin=new(0,verticalGap,_swatchGap,verticalGap),
          BorderThickness=new(_swatchBorder),BorderBrush=System.Windows.SystemColors.ControlDarkDarkBrush,
          Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)color.Ab,(byte)color.Rb,(byte)color.Gb,(byte)color.Bb))});
        header.Children.Add(new System.Windows.Controls.TextBlock {Text=tool.Name,VerticalAlignment=System.Windows.VerticalAlignment.Center});
        var node=new System.Windows.Controls.TreeViewItem {Header=header,Tag=tool,FontWeight=System.Windows.FontWeights.Normal,HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch,Margin=new(0,0,0,_rowGap)};_nodes.Add(tool.Key,node);return node;
      }
      private void UpdateRows(object? sender,EventArgs e)
      {
        if(_updatingRows||Tree.ActualWidth<=0)return;_updatingRows=true;
        try
        {
          foreach(var node in _nodes.Values)
          {
            if(node.ActualHeight<=0||node.Header is not System.Windows.FrameworkElement header||header.ActualWidth<=0)continue;
            double indent=node.TranslatePoint(new System.Windows.Point(),Tree).X;
            if(!_rowFrames.TryGetValue(node,out var frame))
            {
              double headerOffset=header.TranslatePoint(new System.Windows.Point(),node).X;
              var row=new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border),"FullRow");
              row.SetBinding(System.Windows.Controls.Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
              var content=new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter),"RowHeader");
              content.SetValue(System.Windows.Controls.ContentPresenter.ContentSourceProperty,"Header");content.SetValue(System.Windows.FrameworkElement.HeightProperty,_rowHeight);row.AppendChild(content);
              var style=new System.Windows.Style(typeof(System.Windows.Controls.TreeViewItem));
              style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.TemplateProperty,new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.TreeViewItem)){VisualTree=row}));
              style.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,System.Windows.Media.Brushes.Transparent));
              var hover=new System.Windows.Trigger {Property=System.Windows.UIElement.IsMouseOverProperty,Value=true};
              hover.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,_hoverBrush));style.Triggers.Add(hover);
              var selected=new System.Windows.Trigger {Property=System.Windows.Controls.TreeViewItem.IsSelectedProperty,Value=true};
              selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.BackgroundProperty,System.Windows.SystemColors.HighlightBrush));
              selected.Setters.Add(new System.Windows.Setter(System.Windows.Controls.Control.ForegroundProperty,System.Windows.SystemColors.HighlightTextBrush));style.Triggers.Add(selected);
              node.Style=style;node.ApplyTemplate();
              frame=((System.Windows.Controls.Border)node.Template.FindName("FullRow",node),(System.Windows.Controls.ContentPresenter)node.Template.FindName("RowHeader",node),headerOffset);
              _rowFrames.Add(node,frame);
            }
            // Preserve the theme's indentation while the feedback fills the full popup viewport.
            var backgroundMargin=new System.Windows.Thickness(-indent,0,0,0);
            var contentMargin=new System.Windows.Thickness(indent+frame.HeaderOffset,0,_swatchGap,0);
            if(frame.Row.Margin!=backgroundMargin)frame.Row.Margin=backgroundMargin;
            if(frame.Header.Margin!=contentMargin)frame.Header.Margin=contentMargin;
          }
        }
        finally{_updatingRows=false;}
      }
      private void Refresh()
      {
        Details.Text=Selected?.Description??(_tools.Count==0?"No compatible tools":"Choose a tool");
        if(_syncing)return;_syncing=true;
        try{foreach(var pair in _nodes)pair.Value.IsSelected=Selected?.Key==pair.Key;}finally{_syncing=false;}
      }
      private void Commit(ConfiguredTool tool)
      {Choice.SelectedKey=tool.Key;if(_native!=null){_native.IsDropDownOpen=false;_native.Focus();}}
      internal void Release()
      {
        if(_native!=null)_native.IsDropDownOpen=false;
        Tree.LayoutUpdated-=UpdateRows;Choice.ItemImageBinding=null;Choice.Items.Clear();Tree.Items.Clear();_nodes.Clear();_rowFrames.Clear();
      }
      private void Install()
      {
        if(Choice.ControlObject is not System.Windows.Controls.ComboBox combo||ReferenceEquals(_native,combo))return;
        _native=combo;combo.DropDownOpened+=(_,_)=>OpenTree();combo.DropDownClosed+=(_,_)=>Tree.LayoutUpdated-=UpdateRows;
      }
      private void OpenTree()
      {
        if(_native==null)return;_native.ApplyTemplate();
        if(_native.Template.FindName("PART_Popup",_native) is not System.Windows.Controls.Primitives.Popup popup)return;
        if(Tree.Parent is System.Windows.Controls.Border parent)parent.Child=null;
        var border=new System.Windows.Controls.Border {Child=Tree,Padding=new(MappingSpacing/2),BorderThickness=new(1),
          BorderBrush=System.Windows.SystemColors.ActiveBorderBrush,Background=System.Windows.SystemColors.WindowBrush,
          MinWidth=Math.Max(_native.ActualWidth,Config(nameof(MappingNameColumnWidth),MappingNameColumnWidth)),
          MaxHeight=Math.Max(MappingSpacing,Config(nameof(ToolPickerMaximumHeight),ToolPickerMaximumHeight))};
        popup.Child=border;
        Tree.LayoutUpdated-=UpdateRows;Tree.LayoutUpdated+=UpdateRows;
        foreach(var branch in Tree.Items.OfType<System.Windows.Controls.TreeViewItem>().Where(item=>item.Tag==null))branch.IsExpanded=true;
        Refresh();if(Selected is {} selected&&_nodes.TryGetValue(selected.Key,out var node)){node.BringIntoView();node.Focus();}else Tree.Focus();
      }
    }
    private sealed class ExportTimeEstimate
    {
      private static readonly object HistoryLock=new();
      private static readonly Dictionary<int,(double Seconds,double Units)> History=[];
      private readonly Dictionary<int,(double Seconds,double Units)> _baseline=[];
      private readonly Dictionary<int,(double Seconds,double Units)> _measured=[];
      private readonly double[] _units=new double[12];
      private int _stage=-1,_percent,_samples;
      private double _started;
      private bool _cleanup,_complete,_nativeWorkload;
      internal void Reset(double sources,bool cleanup)
      {
        _stage=-1;_samples=0;_complete=false;_nativeWorkload=false;_cleanup=cleanup;_measured.Clear();
        Array.Fill(_units,Math.Max(1,sources));_units[5]=1;
        lock(HistoryLock){_baseline.Clear();foreach(var pair in History)_baseline.Add(pair.Key,pair.Value);}
      }
      internal void SetNativeWorkload(XElement request)
      {
        double patterns=Math.Max(1,request.Elements("Table").Elements("Pattern").Count());
        double geometry=Math.Max(1,request.Descendants().Count()+request.Descendants("Text").Sum(text=>text.Value.Length));
        _units[4]=patterns;_units[6]=_units[8]=_units[9]=_units[11]=geometry;_units[7]=_units[10]=patterns;_nativeWorkload=true;
      }
      private static int Stage(string text)=>text switch
      {
        "Preparing export" or "Preparing notch and curve copies"=>0,
        "Checking export geometry"=>1,"Preparing curves and labels"=>2,"Building temporary export parts"=>3,
        _ when text.StartsWith("Preparing pattern ",StringComparison.Ordinal)=>4,
        "Preparing native materials and patterns"=>5,
        _ when text.StartsWith("Converting ",StringComparison.Ordinal)=>6,
        "Validating native project"=>7,"Rendering transparent preview"=>8,"Writing PatternSmith project"=>9,
        _ when text.StartsWith("Checking ",StringComparison.Ordinal)=>10,
        "Native project ready"=>11,_=>-1
      };
      internal void Observe(int percent,string text,double seconds)
      {
        int stage=Stage(text);
        if(percent>=100&&text=="Export complete")
        {
          if(_complete)return;FinishStage(seconds);_complete=true;
          lock(HistoryLock)foreach(var pair in _measured)History[pair.Key]=pair.Value;
          return;
        }
        if(stage<0)return;
        if(stage!=_stage){FinishStage(seconds);_stage=stage;_started=seconds;_samples=0;_percent=percent;}
        if(percent!=_percent){_samples++;_percent=percent;}
      }
      private void FinishStage(double seconds)
      {if(_stage>=0)_measured[_stage]=(Math.Max(0,seconds-_started),_units[_stage]);}
      private double? Expected(int stage)
      {
        if(stage>=5&&!_nativeWorkload)return null;
        if(!_baseline.TryGetValue(stage,out var previous))return null;
        double ratio=_units[stage]/Math.Max(1,previous.Units);
        double maximum=Math.Max(1,Config(nameof(EtaMaximumWorkloadScale),EtaMaximumWorkloadScale));
        return ratio<1/maximum||ratio>maximum?null:previous.Seconds*ratio;
      }
      internal string Remaining(double seconds)
      {
        if(_complete)return "0:00";
        if(_stage<0)return "Estimating";
        double elapsed=Math.Max(0,seconds-_started);
        // Conversion occupies only its own progress band; validation and I/O are timed independently.
        double fraction=_stage switch {4=>_percent/25d,6=>(_percent-28)/47d,10=>(_percent-89)/6d,_=>0};
        double? current=null;
        if(_samples>=Math.Max(1,Config(nameof(EtaMinimumProgressSamples),EtaMinimumProgressSamples))&&
          elapsed>=Math.Max(.001,Config(nameof(EtaMinimumElapsedSeconds),EtaMinimumElapsedSeconds))&&
          fraction>=Math.Clamp(Config(nameof(EtaMinimumProgressFraction),EtaMinimumProgressFraction),.001,.99))
          current=elapsed*(1-Math.Clamp(fraction,0,.99))/Math.Clamp(fraction,.01,.99);
        else if(Expected(_stage) is double expected&&elapsed<=expected)current=expected-elapsed;
        if(current==null)return "Estimating";
        double total=current.Value;
        for(int stage=_stage+1;stage<_units.Length;stage++)
        {
          if(stage==1&&!_cleanup)continue;
          if(Expected(stage) is not double future)return "Stage ~"+Duration(current.Value)+"; total estimating";
          total+=future;
        }
        return "~"+Duration(total);
      }
      private static string Duration(double seconds)
      {var duration=TimeSpan.FromSeconds(Math.Max(1,seconds));return (int)duration.TotalMinutes+":"+duration.Seconds.ToString("00",CultureInfo.InvariantCulture);}
    }
    private static ToolConfiguration ReadConfiguredTools(string? commonApplicationData=null)
    {
      string vendor=Path.Combine(commonApplicationData??Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Autometrix");
      string configured=Config(nameof(ConfiguredToolsPath),ConfiguredToolsPath);
      string path=Path.Combine(vendor,"Plotting","tools.xml");
      XDocument Read(string file)
      {
        using var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
        using var reader=XmlReader.Create(stream,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=Math.Clamp(Config(nameof(MaximumToolSettingsCharacters),MaximumToolSettingsCharacters),1024,67108864)});
        return XDocument.Load(reader);
      }
      try
      {
        if(!string.IsNullOrWhiteSpace(configured))path=Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        var document=Read(path);XNamespace ns="http://autometrix.com/xml/plotting";
        if(document.Root?.Name!=ns+"ToolCollection")throw new InvalidDataException("Expected PatternSmith ToolCollection XML.");
        var counts=new Dictionary<string,int>(StringComparer.Ordinal);var tools=new List<ConfiguredTool>();var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var element in document.Root.Elements(ns+"Tool"))
        {
          string process=(string?)element.Element(ns+"ProcessType")??"";
          int index=counts.GetValueOrDefault(process);counts[process]=index+1;
          if(!ToolProcesses.Contains(process,StringComparer.Ordinal)||index>MaximumToolIndex)continue;
          string id=(string?)element.Element(ns+"ID")??"",name=((string?)element.Element(ns+"Name"))?.Trim()??"";
          if(!Guid.TryParse(id,out _)||!ids.Add(id))throw new InvalidDataException("PatternSmith tool IDs are missing or duplicated.");
          var colorElement=element.Element(ns+"Color");XNamespace common="http://autometrix.com/xml",serialization="http://schemas.microsoft.com/2003/10/Serialization/";
          if(colorElement?.Attribute(serialization+"Ref") is {} reference)colorElement=document.Descendants(ns+"Color").FirstOrDefault(color=>(string?)color.Attribute(serialization+"Id")==reference.Value);
          float Component(string name,float fallback)=>float.TryParse((string?)colorElement?.Element(common+name),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&float.IsFinite(value)?Math.Clamp(value,0,1):fallback;
          Eto.Drawing.Color? color=colorElement==null?null:new Eto.Drawing.ColorHSL(Component("Hue",0)*360,Component("Saturation",0),Component("Lightness",.5f),Component("Alpha",1)).ToColor();
          tools.Add(new(id,name.Length>0?name:ToolProcessName(process),process,index,color,((string?)element.Element(ns+"ToolMountConstraint"))?.Trim()??""));
        }
        if(tools.Count==0)throw new InvalidDataException("No supported PatternSmith tools were found.");
        string? selected=null;
        var cutters=new List<ConfiguredCutter>();
        try
        {
          string settings=new[]{"PatternSmith11","PatternSmith10"}.Select(version=>Path.Combine(vendor,version,"user.settings")).FirstOrDefault(File.Exists)??"";
          if(settings.Length>0)selected=Read(settings).Descendants().FirstOrDefault(item=>item.Name.LocalName=="SelectedPlotter")?.Value.Trim();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or XmlException)
        {Log.Write(nameof(vExportPatternSmith),"selected cutter could not be read: {0}",ex.Message);}
        try
        {
          var cutterIds=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
          foreach(var device in Read(Path.Combine(vendor,"Plotting","Devices.xml")).Descendants(ns+"Plotter"))
          {
            string id=((string?)device.Element(ns+"Id"))?.Trim()??"";
            if(id.Length==0||!cutterIds.Add(id))continue;
            var connection=device.Element(ns+"Connection");var machine=device.Element(ns+"MachineInfo");
            string name=new[]{(string?)connection?.Element(ns+"Name"),(string?)machine?.Element(ns+"ProductName"),(string?)connection?.Element(ns+"OriginalName"),id}
              .First(value=>!string.IsNullOrWhiteSpace(value))!.Trim();
            cutters.Add(new(id,name,tools.Where(tool=>CutterSupportsTool(device,ns,tool)).ToArray()));
          }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or XmlException)
        {Log.Write(nameof(vExportPatternSmith),"configured cutters could not be read: {0}",ex.Message);}
        var active=cutters.FirstOrDefault(cutter=>string.Equals(cutter.Id,selected,StringComparison.OrdinalIgnoreCase))??cutters.FirstOrDefault();
        return new(path,active?.Name??"",tools,""){Cutters=cutters,SelectedCutterId=active?.Id};
      }
      catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException or ArgumentException or NotSupportedException)
      {Log.Write(nameof(vExportPatternSmith),"configured tools unavailable: {0}",ex.Message);return new(path,"",[],"Configured PS tools unavailable; showing saved/default tools.");}
    }
    private static bool CutterSupportsTool(XElement device,XNamespace ns,ConfiguredTool tool)
    {
      var machine=device.Element(ns+"MachineInfo");
      var mounts=device.Element(ns+"MountPoints")?.Elements(ns+"MountPoint").ToArray();
      bool IsMount(XElement mount,string type)=>string.Equals((string?)mount.Element(ns+"MountPointType"),type,StringComparison.OrdinalIgnoreCase);
      int Heads(string type,string count)=>mounts?.Count(mount=>IsMount(mount,type))??
        (int.TryParse((string?)machine?.Element(ns+count),NumberStyles.Integer,CultureInfo.InvariantCulture,out var heads)?heads:int.MaxValue);
      bool Steered(int head)
      {
        var names=mounts?.Where(mount=>IsMount(mount,"Steering")).Select(mount=>((string?)mount.Element(ns+"Name"))?.Trim()).ToArray();
        if(names?.Any(name=>string.Equals(name,"Steered1",StringComparison.OrdinalIgnoreCase)||string.Equals(name,"Steered2",StringComparison.OrdinalIgnoreCase))==true)
          return names.Contains("Steered"+head.ToString(CultureInfo.InvariantCulture),StringComparer.OrdinalIgnoreCase);
        return Heads("Steering","SteeredHeadCount")>=head;
      }
      return tool.Process switch
      {
        "Mark"=>Heads("Marking","MarkingHeadCount")>0,
        "Inkjet"=>Heads("Marking","MarkingHeadCount")>0&&(!bool.TryParse((string?)machine?.Element(ns+"DotMarkUnlocked"),out bool dotMark)||dotMark),
        "Print"=>machine?.Element(ns+"TijIpAddress") is not {} printer||!string.IsNullOrWhiteSpace(printer.Value),
        "Drill"=>Heads("Drilling","DrillingHeadCount")>0,
        "Cut" or "Notch" or "Punch" or "Crease"=>tool.Mount.ToUpperInvariant() switch
        {"STEERED1"=>Steered(1),"STEERED2"=>Steered(2),_=>Heads("Steering","SteeredHeadCount")>0},
        _=>true
      };
    }
    private static string NormalizeProjectPath(string value)
    {
      if(string.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Enter an export filename.");
      string full=Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
      if(Path.GetExtension(full).Length==0)full+=".project";
      if(!Path.GetExtension(full).Equals(".project",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The export filename must end in .project.");
      if(!Directory.Exists(Path.GetDirectoryName(full)))throw new InvalidOperationException("The export folder does not exist.");
      return full;
    }
    private static string[] RecentOutputPaths(JsonObject section)=>
      (section["recent_paths"] as JsonArray??[]).OfType<JsonValue>().Select(value=>value.TryGetValue<string>(out var path)?path:"")
        .Where(path=>!string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Max(1,Config(nameof(RecentOutputLimit),RecentOutputLimit))).ToArray();
    private static void RememberOutputPath(JsonObject section,string destination)
    {
      int limit=Math.Max(1,Config(nameof(RecentOutputLimit),RecentOutputLimit));
      var paths=new[]{destination}.Concat(RecentOutputPaths(section)).Distinct(StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
      section["recent_paths"]=new JsonArray(paths.Select(path=>(JsonNode?)JsonValue.Create(path)).ToArray());
      section.Remove("last_output_by_document");
    }
    private static T Config<T>(string key,T factory)=>global::vTools.CommandDefaults.Get("vExportPatternSmith","vTools.Commands.vExportPatternSmith."+key,factory);
    private sealed record PreparationConfiguration(vExportDXF.ExportSettings Export,vCleanup.ExportGeometrySettings Geometry,bool NativeEffects);
    private static PreparationConfiguration ReadPreparation()=>ToolsOptionStore.Read<PreparationConfiguration>("vExportPatternSmith",section=>
    {
      bool Flag(string key,string field,bool factory)=>ToolsOptionStore.TryGetBool(section,key,out var value)?value:Config(field,factory);
      double NumberOption(string key,string field,double factory,bool positive=false)
      {double value=ToolsOptionStore.TryGetDouble(section,key,out var saved)?saved:Config(field,factory);return double.IsFinite(value)&&(positive?value>0:value>=0)?value:factory;}
      T Choice<T>(string key,string field,T factory) where T:struct,Enum=>ToolsOptionStore.TryGetString(section,key,out var text)&&Enum.TryParse<T>(text,true,out var value)&&Enum.IsDefined(value)?value:Config(field,factory);
      string layer=ToolsOptionStore.TryGetString(section,"notch_split_layer",out var savedLayer)&&!string.IsNullOrWhiteSpace(savedLayer)?savedLayer:Config(nameof(DefaultPrepSplitLayer),DefaultPrepSplitLayer);
      return new(new(Choice("notch_trim",nameof(DefaultPrepNotchTrim),DefaultPrepNotchTrim),layer,
        Flag("notch_join",nameof(DefaultPrepNotchJoin),DefaultPrepNotchJoin),Flag("text_break",nameof(DefaultPrepTextBreak),DefaultPrepTextBreak),
        Flag("optimize",nameof(DefaultPrepOptimize),DefaultPrepOptimize),Choice("explode",nameof(DefaultPrepExplode),DefaultPrepExplode),
        Choice("cleanup_action",nameof(DefaultPrepCleanupAction),DefaultPrepCleanupAction),Flag("remove_grain",nameof(DefaultPrepRemoveGrain),DefaultPrepRemoveGrain),
        Flag("move_origin",nameof(DefaultPrepMoveOrigin),DefaultPrepMoveOrigin),NumberOption("end_trim",nameof(DefaultPrepEndTrim),DefaultPrepEndTrim)),
        new(NumberOption("cleanup_threshold",nameof(DefaultPrepShortThreshold),DefaultPrepShortThreshold),Flag("cleanup_simplify",nameof(DefaultPrepSimplify),DefaultPrepSimplify),
          Flag("cleanup_overlaps",nameof(DefaultPrepOverlaps),DefaultPrepOverlaps),NumberOption("cleanup_tolerance",nameof(DefaultPrepOverlapTolerance),DefaultPrepOverlapTolerance,true),
          Flag("cleanup_segments",nameof(DefaultPrepOverlapSegments),DefaultPrepOverlapSegments)),
        Flag("native_effects",nameof(DefaultNativeEffects),DefaultNativeEffects));
    });
    private sealed class PreparationControls:Eto.Forms.Panel
    {
      private readonly Eto.Forms.CheckBox _optimize=new(){Text="Clean export copies"},_grain=new(){Text="Remove grain"},_origin=new(){Text="Move origin"},
        _join=new(){Text="Join notches"},_text=new(){Text="Split multiline text"},_simplify=new(){Text="Simplify curves"},_overlaps=new(){Text="Find overlaps"},_segments=new(){Text="Remove overlapping portions only"};
      private readonly Eto.Forms.CheckBox _nativeEffects=new(){Text="Native seams and notches",
        ToolTip="Keep matching seam allowances and notches editable in PatternSmith. Other effects keep their prepared curves."};
      private readonly Eto.Forms.DropDown _notches=new(){DataStore=Enum.GetNames<NotchTrimMode>()},_explode=new(){DataStore=Enum.GetNames<vExportDXF.ExportExplodeMode>()},
        _action=new(){DataStore=Enum.GetNames<vCleanup.ExportCleanupAction>()};
      private readonly Eto.Forms.DropDown _layer;
      private readonly string _layerFallback;
      private readonly bool _documentLayerPicker;
      private readonly Eto.Forms.NumericStepper _trim=new(){MinValue=0,MaxValue=double.MaxValue,DecimalPlaces=6},
        _short=new(){MinValue=0,MaxValue=double.MaxValue,DecimalPlaces=6},_tolerance=new(){MinValue=RhinoMath.ZeroTolerance,MaxValue=double.MaxValue,DecimalPlaces=6};
      internal PreparationConfiguration Values=>new(new((NotchTrimMode)_notches.SelectedIndex,_documentLayerPicker?LayerSelector.GetDropDownValue(_layer,_layerFallback):_layer.SelectedKey??_layerFallback,_join.Checked==true,_text.Checked==true,
        _optimize.Checked==true,(vExportDXF.ExportExplodeMode)_explode.SelectedIndex,(vCleanup.ExportCleanupAction)_action.SelectedIndex,_grain.Checked==true,_origin.Checked==true,_trim.Value),
        new(_short.Value,_simplify.Checked==true,_overlaps.Checked==true,_tolerance.Value,_segments.Checked==true),_nativeEffects.Checked==true);
      internal event EventHandler? RemoveGrainChanged;
      internal PreparationControls(RhinoDoc? doc=null,Eto.Forms.Control? curveType=null,Eto.Forms.Control? curveError=null)
      {
        var settings=ReadPreparation();var own=settings.Export;var geometry=settings.Geometry;
        _nativeEffects.Checked=settings.NativeEffects;
        _layerFallback=own.NotchSplitLayer;doc??=RhinoDoc.ActiveDoc;_documentLayerPicker=doc!=null;
        int choiceWidth=Math.Max(1,Config(nameof(PreparationChoiceWidth),PreparationChoiceWidth)),numberWidth=Math.Max(1,Config(nameof(PreparationNumberWidth),PreparationNumberWidth));
        if(doc!=null)_layer=LayerSelector.CreateDropDown(doc,own.NotchSplitLayer,width:choiceWidth);
        else
        {
          _layer=new Eto.Forms.DropDown {Width=choiceWidth};
          _layer.ItemImageBinding=Eto.Forms.Binding.Property<Eto.Forms.ImageListItem,Eto.Drawing.Image>(item=>item.Image!);
          _layer.Items.Add(new Eto.Forms.ImageListItem {Key=own.NotchSplitLayer,Text=own.NotchSplitLayer,Image=LayerSelector.CreateColorSwatch(Eto.Drawing.Colors.White)});_layer.SelectedIndex=0;
        }
        _optimize.Checked=own.Optimize;_grain.Checked=own.RemoveGrain;_origin.Checked=own.MoveOrigin;_join.Checked=own.NotchJoin;_text.Checked=own.TextBreak;
        _notches.SelectedIndex=(int)own.NotchTrim;_explode.SelectedIndex=(int)own.Explode;_action.SelectedIndex=(int)own.CleanupAction;_trim.Value=own.EndTrim;
        _short.Value=geometry.Threshold;_simplify.Checked=geometry.SimplifyCurves;_overlaps.Checked=geometry.FindOverlaps;_tolerance.Value=geometry.OverlapTolerance;_segments.Checked=geometry.OverlapSegments;
        _optimize.ToolTip="Clean temporary PS copies only; drawing geometry and DXF settings stay unchanged.";
        _grain.ToolTip="Omit open GRAIN-named or GRAIN-layer curves from this PS export.";
        _origin.ToolTip="Translate temporary copies to World origin; placed table coordinates are retained in PS nests.";
        _join.ToolTip="Join eligible V/U-notch components to touching export boundaries.";_text.ToolTip="Create a separate centered entity for each multiline text row.";
        _notches.ToolTip="No keeps touched curves; Split moves between-leg pieces to the split layer; Trim omits them.";
        _layer.ToolTip="Existing or new Rhino layer path for export-only pieces in Split mode.";_explode.ToolTip="No preserves curves; SplitAtCorners separates kinks; Full separates components.";
        _action.ToolTip="Ask reviews original highlights; Delete removes findings from copies only; Ignore retains them.";
        _trim.ToolTip="Model-unit distance removed from touching non-cut endpoints. Zero disables trimming.";
        _short.ToolTip="Maximum short-geometry length in model units. Zero disables short detection.";
        _tolerance.ToolTip="Positive overlap-comparison distance in model units.";_simplify.ToolTip="Simplify temporary curves before checking them.";
        _overlaps.ToolTip="Find overlapping curves, excluding table decorations and valid notch contacts.";_segments.ToolTip="Delete only overlapping portions rather than selected whole curves.";
        _grain.CheckedChanged+=(_,_)=>RemoveGrainChanged?.Invoke(this,EventArgs.Empty);
        foreach(var choice in new[]{_notches,_explode,_action})choice.Width=choiceWidth;
        foreach(var number in new[]{_trim,_short,_tolerance})number.Width=numberWidth;
        Eto.Forms.Control Row(string title,Eto.Forms.Control field)=>new Eto.Forms.TableLayout {Spacing=new Eto.Drawing.Size(MappingSpacing,0),Rows={new Eto.Forms.TableRow(new Eto.Forms.Label {Text=title,Width=MappingNameColumnWidth},field,new Eto.Forms.TableCell(null,true))}};
        Eto.Forms.GroupBox Section(string title,params Eto.Forms.Control[] controls)
        {
          var content=new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Vertical,HorizontalContentAlignment=Eto.Forms.HorizontalAlignment.Stretch,Spacing=MappingSpacing,Padding=MappingSpacing};
          foreach(var control in controls)content.Items.Add(control);
          return new Eto.Forms.GroupBox {Text=title,Content=content};
        }
        var geometryRows=new List<Eto.Forms.Control>();
        if(curveType!=null){curveType.Width=choiceWidth;geometryRows.Add(Row("Curve type",curveType));}
        if(curveError!=null){curveError.Width=numberWidth;geometryRows.Add(Row("Max error (in)",curveError));}
        geometryRows.AddRange([_grain,_origin,_text,Row("Explode curves",_explode),Row("End trim (model units)",_trim)]);
        var column=new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Vertical,HorizontalContentAlignment=Eto.Forms.HorizontalAlignment.Stretch,Spacing=MappingSpacing,Padding=MappingSpacing,
          Items={Section("Geometry",geometryRows.ToArray()),
            Section("Seams and notches",_nativeEffects,Row("Treatment",_notches),Row("Split layer",_layer),_join),
            Section("Cleanup",_optimize,_simplify,_overlaps,_segments,Row("Findings",_action),Row("Short threshold (model units)",_short),Row("Overlap tolerance (model units)",_tolerance))}};
        void UpdateNotchControls()
        {
          var mode=(NotchTrimMode)_notches.SelectedIndex;
          _layer.Enabled=mode==NotchTrimMode.Split;
          _join.Enabled=mode!=NotchTrimMode.Trim || _nativeEffects.Checked!=true;
        }
        _notches.SelectedIndexChanged+=(_,_)=>UpdateNotchControls();
        _nativeEffects.CheckedChanged+=(_,_)=>UpdateNotchControls();
        UpdateNotchControls();
        Content=new Eto.Forms.Scrollable {Content=column};
      }
      internal void Save(JsonObject section)
      {
        var current=Values;var own=current.Export;var geometry=current.Geometry;
        if(string.IsNullOrWhiteSpace(own.NotchSplitLayer))throw new InvalidOperationException("Choose a split layer.");
        section["notch_trim"]=own.NotchTrim.ToString();section["notch_split_layer"]=own.NotchSplitLayer;section["notch_join"]=own.NotchJoin;
        section["native_effects"]=current.NativeEffects;
        section["text_break"]=own.TextBreak;section["optimize"]=own.Optimize;section["explode"]=own.Explode.ToString();section["cleanup_action"]=own.CleanupAction.ToString();
        section["remove_grain"]=own.RemoveGrain;section["move_origin"]=own.MoveOrigin;section["end_trim"]=own.EndTrim;
        section["cleanup_threshold"]=geometry.Threshold;section["cleanup_simplify"]=geometry.SimplifyCurves;section["cleanup_overlaps"]=geometry.FindOverlaps;
        section["cleanup_tolerance"]=geometry.OverlapTolerance;section["cleanup_segments"]=geometry.OverlapSegments;
      }
    }
    public override string EnglishName=>"vExportPatternSmith";
    protected override Result RunCommand(RhinoDoc doc,RunMode mode)=>RunWithWindow(doc,mode);

    private static XDocument PrepareRequest(RhinoDoc doc,IReadOnlyList<RhinoObject> sources,
      IReadOnlyList<vExportDXF.PreparedExportEntry> entries,Transform exportTranslation,bool removeGrain=false,
      Action<int,string>? report=null,CancellationToken cancellationToken=default,Func<string,bool>? approveMixedFaces=null,
      Action<string>? unsupportedNotice=null,PreparationConfiguration? preparation=null)
    {
        var clock=System.Diagnostics.Stopwatch.StartNew();var curveSettings=ReadCurveConversionSettings();
        if(doc.ModelUnitSystem is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
          throw new InvalidOperationException("Set known standard document units before exporting a PatternSmith project.");
        double scale=RhinoMath.UnitScale(doc.ModelUnitSystem,UnitSystem.Inches);
        if(!double.IsFinite(scale)||scale<=0)throw new InvalidOperationException("Could not convert document units to inches.");
        if(!exportTranslation.TryGetInverse(out var restoreTranslation))throw new InvalidOperationException("Could not restore the prepared export coordinates.");
        double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance)*scale;
        var selected=sources.Select(obj=>obj.Id).ToHashSet();
        var tables=ReadTables(doc,selected);
        int configuredWorkers=Config(nameof(CurveConversionWorkers),CurveConversionWorkers);
        int workers=configuredWorkers==0?Math.Min(Math.Max(1,Environment.ProcessorCount-1),Math.Clamp(Config(nameof(AutomaticCurveWorkerLimit),AutomaticCurveWorkerLimit),1,32)):Math.Clamp(configuredWorkers,1,32);
        var root=new XElement("PatternSmithRequest",new XAttribute("units","inches"),new XAttribute("conversionWorkers",workers),new XAttribute("workspaceGap",Number(Math.Max(0,global::vTools.CommandDefaults.Get("vExportPatternSmith","vTools.Commands.vExportPatternSmith.WorkspaceGapInches",WorkspaceGapInches)))),
          new XAttribute("legacyTableFontSizing",ToolsOptionStore.Read("vExportPatternSmith",ReadTargetVersion)==PatternSmithTargetVersion.PatternSmith10),
          new XAttribute("reuseEquivalentSplines",Config(nameof(ReuseEquivalentSplines),ReuseEquivalentSplines)),new XAttribute("splineCacheMegabytes",Math.Clamp(Config(nameof(SplineCacheMegabytes),SplineCacheMegabytes),1,4096)),
          new XAttribute("distanceIndexLeafSegments",Math.Clamp(Config(nameof(DistanceIndexLeafSegments),DistanceIndexLeafSegments),1,256)),new XAttribute("progressIntervalMilliseconds",Math.Max(0,Config(nameof(NativeProgressIntervalMilliseconds),NativeProgressIntervalMilliseconds))));
        if(Config(nameof(CacheSplineConversions),CacheSplineConversions))root.SetAttributeValue("splineCacheDirectory",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),RuntimeFolderName,BuilderFolderName,SplineCacheFolderName));
        var materials=new PartMaterials(doc);var tableElements=new Dictionary<string,XElement>(StringComparer.OrdinalIgnoreCase);
        var ignored=new Dictionary<string,int>(StringComparer.Ordinal);
        void Ignore(string type)=>ignored[type]=ignored.GetValueOrDefault(type)+1;
        entries=entries.Select(entry=>entry with {SourceIds=entry.SourceIds.Where(id=>doc.Objects.FindId(id) is {} obj&&InspectExportObject(doc,obj,0,null)).ToArray()}).ToArray();
        var titleAnnotations=entries.Where(entry=>IsTitleAnnotation(doc,entry.SourceIds)).ToHashSet();
        var noteAnnotations=entries.Where(entry=>entry.SourceIds.Length>0&&entry.SourceIds.All(id=>doc.Objects.FindId(id) is {} obj&&(IsTitleObject(obj)||IsDimensionAnnotation(obj)))).ToHashSet();
        var savedFaces=new Dictionary<Guid,bool>();
        bool CuttingSide(Guid id)
        {
          if(savedFaces.TryGetValue(id,out bool saved))return saved;
          return savedFaces[id]=doc.Objects.FindId(id) is {} obj&&PartGroupData.ReadFaceDown(doc,obj);
        }
        var tableFaces=tables.ToDictionary(table=>table.Group,table=>entries.Where(entry=>!entry.TableAnnotation&&!noteAnnotations.Contains(entry)&&entry.SourceIds.Any(table.Members.Contains))
          .SelectMany(entry=>entry.SourceIds.Where(table.Members.Contains)).Select(CuttingSide).Distinct().ToArray());
        var approvedTables=new HashSet<int>();
        int number=0;
        foreach(var entry in entries)
        {
          cancellationToken.ThrowIfCancellationRequested();
          report?.Invoke(number*100/Math.Max(1,entries.Count),"Preparing pattern "+(number+1)+" / "+entries.Count);
          if(doc.Objects.FindId(entry.Id) is not {IsDeleted:false} prepared)
            throw new InvalidOperationException("A prepared export object is no longer available.");
          var owners=tables.Where(table=>entry.SourceIds.Any(table.Members.Contains)).ToArray();
          if(owners.Length>1)throw new InvalidOperationException("A prepared part belongs to more than one vNest table.");
          ExportTable? table=owners.SingleOrDefault();
          if(table!=null&&entry.SourceIds.Any(id=>selected.Contains(id)&&!table.Members.Contains(id)))
            throw new InvalidOperationException("A prepared part mixes table-owned and standalone objects.");
          bool title=titleAnnotations.Contains(entry),note=noteAnnotations.Contains(entry)&&!title,annotation=entry.TableAnnotation||title||note;
          var assigned=table==null&&!annotation?materials.ForObjects(entry.SourceIds):null;
          var faces=annotation?[]:entry.SourceIds.Select(CuttingSide).Distinct().ToArray();
          if(faces.Length>1)throw new InvalidOperationException("An export part contains conflicting face-up and face-down settings.");
          bool faceDown=faces.FirstOrDefault();
          var sides=table==null?faces:tableFaces[table.Group];
          string? tableName=table==null?null:sides.Length>1?NestWorkflow.CuttingTableName(table.Name,false)+MixedFacesSuffix:
            NestWorkflow.CuttingTableName(table.Name,sides.FirstOrDefault(table.FaceDown));
          if(table!=null&&!annotation&&sides.Length>1&&approvedTables.Add(table.Group))
          {
            if(approveMixedFaces==null)throw new InvalidOperationException("Approval is required to export table '"+table.Name+"' with mixed face-up and face-down parts.");
            if(!approveMixedFaces(table.Name))throw new OperationCanceledException("Mixed cutting-side export was not approved.");
          }
          string? material=table?.Material??assigned?.Name;
          string key=table!=null?"table-"+table.Group:assigned==null?"unassigned|"+faceDown:"material-"+material+"|"+Number(assigned.Width)+"|"+Number(assigned.Gap)+"|"+faceDown;
          if(!tableElements.TryGetValue(key,out var element))
          {
            element=new XElement("Table",new XAttribute("id","t"+tableElements.Count),
              new XAttribute("name",tableName??NestWorkflow.CuttingTableName(Config(nameof(StandaloneTableName),StandaloneTableName),faceDown)),
              new XAttribute("materialAssigned",material!=null),material==null?null:new XAttribute("material",material),
              new XAttribute("width",Number(table==null?assigned?.Width??Config(nameof(StandaloneMaterialWidthInches),StandaloneMaterialWidthInches):table.Width*scale)),
              new XAttribute("length",Number(table==null?Config(nameof(StandaloneMaterialLengthInches),StandaloneMaterialLengthInches):table.Length*scale)),
              new XAttribute("populated",table!=null?"true":"false"));
            var workspaceOrigin=table?.Plane.Origin??Point3d.Origin;
            // Apply the shared origin shift to the workspace only; nested copy coordinates stay table-local.
            workspaceOrigin.Transform(exportTranslation);
            if(table!=null&&Math.Abs(table.Plane.Normal*Vector3d.ZAxis-1)>RotationMetadataTolerance)
              throw new InvalidOperationException("Preserving PatternSmith workspace placement requires tables in the world XY plane.");
            element.SetAttributeValue("workspaceX",Number(workspaceOrigin.X*scale));element.SetAttributeValue("workspaceY",Number(workspaceOrigin.Y*scale));
            element.SetAttributeValue("workspaceRotation",Number(table==null?0:Math.Atan2(table.Plane.XAxis.Y,table.Plane.XAxis.X)));
            if(table!=null)element.SetAttributeValue("copySpacingOverride",Number(table.Gap*scale));
            else if(assigned!=null)element.SetAttributeValue("copySpacingOverride",Number(assigned.Gap));
            root.Add(element);tableElements[key]=element;
          }
          Transform transform=Transform.Scale(Point3d.Origin,scale)*(table==null?Transform.Identity:Transform.PlaneToPlane(table.Plane,Plane.WorldXY))*restoreTranslation;
          var pattern=new XElement("Pattern",new XAttribute("id","p"+(++number)),
            new XAttribute("name",entry.TableAnnotation?(tableName??"Table")+" information":title?TitleName(doc,entry):PartName(doc,entry,number)),new XAttribute("perimeter",note?"Box":annotation?TableAnnotationPerimeter:"Auto"),
            new XAttribute("annotation",annotation?"true":"false"),
            new XAttribute("allowNesting",annotation?"false":"true"),
            new XAttribute("materialAssigned",!annotation&&material!=null),
            new XAttribute("enforceMaterial",!annotation&&(table!=null||assigned!=null)));
          pattern.SetAttributeValue("faceDown",faceDown);
          if(entry.TableAnnotation)pattern.SetAttributeValue("fitAnnotation",true);
          if(table!=null&&!annotation)pattern.SetAttributeValue("sourceRotation",Number(NestedSourceRotation(doc,entry.SourceIds)));
          if(!annotation)
          {
            var enforcement=entry.SourceIds.Select(id=>doc.Objects.FindId(id)?.Attributes.GetUserString(PatternSmithPartContract.MaterialEnforcedKey))
              .Where(value=>!string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if(assigned!=null&&enforcement.Length==1&&bool.TryParse(enforcement[0],out bool enforce))
              pattern.SetAttributeValue("enforceMaterial",enforce);
            var partState=NestWorkflow.ExportPatternSmithPartState(doc,entry.SourceIds,table?.Group??-1,scale) with {CuttingFaceDown=faceDown};
            var settings=partState.Settings;
            pattern.SetAttributeValue("partState",System.Text.Json.JsonSerializer.Serialize(partState));
            pattern.SetAttributeValue("copies",settings?.Copies??NestWorkflow.ConfiguredDefaultCopies);
            pattern.SetAttributeValue("allowNesting",settings?.Included??true);
            if(NestWorkflow.PatternSmithRotationStep(settings) is {} step)pattern.SetAttributeValue("rotationStep",Number(RhinoMath.ToRadians(step)));
            if(table!=null&&NestedSourceFlipped(doc,entry.SourceIds))pattern.SetAttributeValue("sourceFlipped",true);
          }
          bool nativeEffects=preparation?.NativeEffects==true && !annotation && (bool?)pattern.Attribute("sourceFlipped")!=true;
          string? tableLabelFont=entry.TableAnnotation?ConfiguredTableLabelFont():null;
          foreach(var entity in SerializeObject(doc,prepared,transform,tolerance,0,null,annotation,removeGrain,curveSettings,Ignore,tableLabelFont,nativeEffects))
          {
            if(annotation&&!note&&entity.Name.LocalName is not ("Text" or "Point"))entity.SetAttributeValue("excludeFromPerimeter",false);
            if(entry.TableAnnotation&&table!=null&&entity.Name.LocalName=="Text"&&entity.Value.Equals(table.Name,StringComparison.OrdinalIgnoreCase))entity.Value=tableName!;
            pattern.Add(entity);
          }
          if(nativeEffects) AddNativeEffectCandidates(doc,pattern,entry.SourceIds,transform*exportTranslation,scale,tolerance,
            preparation!.Export.NotchTrim,curveSettings);
          if(title&&!entry.SourceIds.Select(doc.Objects.FindId).Any(obj=>obj!=null&&vTitle.IsTitleFrame(obj))) {
            foreach(var source in entry.SourceIds.Select(doc.Objects.FindId).Where(obj=>obj?.Geometry is TextEntity)) {
              using var frame=vTitle.CreateFrameForTitlePreview(source!, (TextEntity)source!.Geometry);
              if(frame==null||!frame.Transform(transform))throw new InvalidOperationException("Could not frame an exported title.");
              foreach(var entity in SerializeCurve(frame,new("Reference",PrimaryToolIndex),tolerance,curveSettings)) {
                entity.SetAttributeValue("excludeFromPerimeter",false);pattern.Add(entity);
              }
            }
          }
          if(!pattern.HasElements)continue;
          element.Add(pattern);
        }
        foreach(var empty in root.Elements("Table").Where(table=>!table.Elements("Pattern").Any()).ToArray())empty.Remove();
        unsupportedNotice?.Invoke(UnsupportedMessage(ignored));
        if(!root.Elements("Table").Any())throw new InvalidOperationException("No supported geometry remains; nothing was exported.");
        OrderRequestTables(root);Log.Write(nameof(vExportPatternSmith),"prepared request patterns={0} elapsedMs={1:F1} workers={2}",number,clock.Elapsed.TotalMilliseconds,workers);return new XDocument(root);
    }
    private static double NestedSourceRotation(RhinoDoc doc,IReadOnlyList<Guid> sourceIds)
    {
      double? angle=null;
      foreach(var id in sourceIds)
      {
        string? saved=doc.Objects.FindId(id)?.Attributes.GetUserString(NestedRotationKey);if(string.IsNullOrWhiteSpace(saved))continue;
        if(!double.TryParse(saved,NumberStyles.Float,CultureInfo.InvariantCulture,out double degrees)||!double.IsFinite(degrees))
          throw new InvalidOperationException("A nested part has invalid source-rotation metadata.");
        degrees%=360;
        if(angle is {} previous&&Math.Abs(Math.IEEERemainder(degrees-previous,360))>RotationMetadataTolerance)
          throw new InvalidOperationException("A prepared part combines objects with different nested rotation angles.");
        angle??=degrees;
      }
      return RhinoMath.ToRadians(angle??0);
    }
    private static bool NestedSourceFlipped(RhinoDoc doc,IReadOnlyList<Guid> sourceIds)
    {
      var flags=sourceIds.Select(id=>doc.Objects.FindId(id)?.Attributes.GetUserString(NestedFlipKey))
        .Where(value=>!string.IsNullOrWhiteSpace(value)).Select(value=>bool.TryParse(value,out bool flag)?flag:
          throw new InvalidOperationException("A nested part has invalid flip metadata.")).Distinct().ToArray();
      if(flags.Length>1)throw new InvalidOperationException("A prepared part combines flipped and unflipped objects.");
      return flags.FirstOrDefault();
    }
    private static void OrderRequestTables(XElement root)
    {
      string Key(string name)=>System.Text.RegularExpressions.Regex.Replace(name,"[0-9]+",match=>
      {string digits=match.Value.TrimStart('0');if(digits.Length==0)digits="0";return digits.Length.ToString("D10",CultureInfo.InvariantCulture)+"/"+digits;});
      var tables=root.Elements("Table").OrderBy(table=>(string?)table.Attribute("populated")=="false"?1:0)
        .ThenBy(table=>Key((string?)table.Attribute("name")??""),StringComparer.OrdinalIgnoreCase).ToArray();
      root.ReplaceNodes(tables);
    }

    internal static Result RunWithWindow(RhinoDoc doc,RunMode mode,bool clipboard=false,bool showMappings=true)
    {
      string command=clipboard?nameof(vCopyToPatternSmith):nameof(vExportPatternSmith);
      using var cancellation=new CancellationTokenSource();EventHandler escape=(_,_)=>cancellation.Cancel();RhinoApp.EscapeKeyPressed+=escape;
      try
      {
        StatusBar.ShowProgressMeter(0,100,"Reading PatternSmith export sources (Esc to cancel)",true,false);
        var selected=doc.Objects.GetSelectedObjects(false,false).ToArray();
        if(clipboard&&selected.Length==0)
        {
          using var picker=new Rhino.Input.Custom.GetObject();
          picker.SetCommandPrompt("Select objects to copy to PatternSmith");
          picker.GroupSelect=true;picker.GetMultiple(1,0);
          if(picker.CommandResult()!=Result.Success)return picker.CommandResult();
          selected=picker.Objects().Select(reference=>reference.Object()).OfType<RhinoObject>().DistinctBy(obj=>obj.Id).ToArray();
        }
        var targets=clipboard||selected.Length>0?selected:doc.Objects.GetObjectList(new ObjectEnumeratorSettings
          {NormalObjects=true,HiddenObjects=false,LockedObjects=false,IncludeLights=false,IncludeGrips=false,IncludePhantoms=false}).ToArray();
        if(targets.Length==0)return Result.Nothing;
        if(clipboard)RequireWorkspaceClipboardSources(doc,targets);
        var ignored=new Dictionary<string,int>(StringComparer.Ordinal);
        bool anySupported=false;
        foreach(var target in targets)anySupported|=InspectExportObject(doc,target,0,ignored);
        string ignoredMessage=UnsupportedMessage(ignored);
        if(!anySupported)
        {RhinoApp.WriteLine(command+": "+ignoredMessage+" No supported geometry to copy or export.");return Result.Nothing;}
        if(clipboard&&!showMappings)
        {
          if(ignoredMessage.Length>0)RhinoApp.WriteLine(command+": "+ignoredMessage);
          return CopyWithDefaults(doc,targets,cancellation.Token);
        }
        var initial=CreateMappingRequest(doc,targets);
        var catalog=ReadCatalogWithUi(cancellation.Token);
        StatusBar.HideProgressMeter();
        var names=AssignedMaterialTables(initial).Attributes("material").Select(attribute=>attribute.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ownCatalogIds=catalog.Materials.Where(material=>names.Contains(material.Name,StringComparer.OrdinalIgnoreCase)).Select(material=>material.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids=MaterialLibrary.PatternSmithCustomIds(names,catalog.ReservedIds.Except(ownCatalogIds,StringComparer.OrdinalIgnoreCase));
        string outputPath=Path.ChangeExtension(string.IsNullOrWhiteSpace(doc.Path)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),string.IsNullOrWhiteSpace(doc.Name)?DefaultFileStem:doc.Name):doc.Path,"project");
        outputPath=SessionOutputPath(doc,outputPath);
        using var mappings=new ExportMappingDialog(initial,catalog,ids,outputPath,doc:doc,clipboard:clipboard);
        mappings.ReportUnsupportedObjects(ignoredMessage);
        mappings.SetUnsupportedSources(targets.Select(obj=>obj.Id));
        mappings.PreparationSourceFingerprint=()=>string.Join(";",targets.Select(source=>doc.Objects.FindId(source.Id)).Select(obj=>obj==null?"deleted":
          obj.Id.ToString("N")+":"+obj.Geometry.DataCRC(0)+":"+obj.Attributes.ToJSON(new Rhino.FileIO.SerializationOptions {WriteRenderMeshes=false,WriteAnalysisMeshes=false})))+string.Join(";",targets.Select(source=>doc.Objects.FindId(source.Id))
          .Where(obj=>obj!=null).SelectMany(obj=>(obj!.Attributes.GetGroupList()??[]).Where(index=>doc.Groups[index] is {IsDeleted:false})
            .Select(index=>PartGroupData.GroupKey(doc.Groups[index].Id)).Concat(new[]{"boundary-"+obj.Id.ToString("N"),obj.Attributes.GetUserString(PartGroupData.SourceSettingsKey)??""}))
          .Distinct().Select(key=>key+"="+doc.Strings.GetValue(PartGroupData.NestSection,key)));
        mappings.PrepareConversion=(owner,token,report)=>
        {
          var preparation=owner.Preparation;
          preparation=preparation with {Export=preparation.Export with {CleanupAction=owner.TakeCleanupAction(preparation.Export.CleanupAction)}};
          var (result,request)=PreparePatternSmithRequest(doc,targets,preparation,token,report,
            owner.KeepPreparationResponsive,owner.ReviewFindings,owner.ApproveMixedFaces,owner.ReportUnsupportedObjects,owner.ShowFindings,owner.SetCleanupFeedback);
          if(result!=Result.Success||request==null)throw new OperationCanceledException("Curve preparation was cancelled.");
          owner.ApplyConversionSettings(request.Root!);
          return request;
        };
        mappings.ExecuteExport=owner=>
        {
            var request=owner.PreparedRequest();
            owner.ApplyResolvedMappings(request.Root!);ValidateToolMappings(request.Root!);
            owner.SetTimingWorkload(request.Root!);
            string preparedProject=owner.PrepareNativeProject(request);
            if(clipboard)
            {
              owner.ExportToken.ThrowIfCancellationRequested();
              WriteClipboard(PatternSmithClipboard.ReadPreparedPayload(preparedProject),request.Root!);
              return Result.Success;
            }
            string? destination=owner.ResolveDestination();
            if(destination==null)return Result.Cancel;
            owner.ExportToken.ThrowIfCancellationRequested();
            if(owner.DestinationAction==ExistingFileAction.Merge)
            {
              string original=ProjectFileHash(destination);
              string merged=Path.Combine(Path.GetTempPath(),MergeTemporaryPrefix+Guid.NewGuid().ToString("N")+".project");
              try
              {
                var merge=new XElement("PatternSmithMerge",new XAttribute("existing",destination),new XAttribute("incoming",preparedProject),new XAttribute("gap",owner.MergeSpacingInches));
                if(request.Root!.Element("PreviewTools") is {} tools)merge.Add(new XElement(tools));
                BuildNativeProject(new XDocument(merge),merged,owner.ExportToken,owner.ReportProgress);
                if(ProjectFileHash(destination)!=original)throw new IOException("The existing project changed while merging. Export again to avoid replacing newer changes.");
                PublishNativeProject(merged,destination,owner.ExportToken);
              }
              finally{ExportMappingDialog.DeleteNativeProject(merged);}
            }
            else PublishNativeProject(preparedProject,destination,owner.ExportToken);
            owner.RememberSuccessfulOutput(destination);
            int patterns=request.Root!.Elements("Table").Elements("Pattern").Count();
            int copies=request.Root.Elements("Table").Where(table=>(string?)table.Attribute("populated")=="true").Elements("Pattern").Count(pattern=>(string?)pattern.Attribute("annotation")!="true");
            Log.Write(nameof(vExportPatternSmith),"exported patterns={0} copies={1} materialSource={2} path={3}",patterns,copies,catalog.Source,destination);
            RhinoApp.WriteLine($"vExportPatternSmith: exported {CountText.Format(patterns,"pattern")} and {CountText.Format(copies,"placed copy","placed copies")} to {destination}.");
            return Result.Success;
        };
        try{return mappings.ShowSemiModal(doc,RhinoEtoApp.MainWindowForDocument(doc))?Result.Success:Result.Cancel;}
        finally{mappings.ClearUnsupportedHighlights();}
      }
      catch(OperationCanceledException){RhinoApp.WriteLine(command+": cancelled.");return Result.Cancel;}
      catch(Exception ex){Log.Write(command,"export failed: {0}",ex);RhinoApp.WriteLine(command+": "+ex.Message);return Result.Failure;}
      finally{RhinoApp.EscapeKeyPressed-=escape;StatusBar.HideProgressMeter();}
    }
    private static void WriteClipboard(PatternSmithClipboard.PreparedPayload payload,XElement request)
    {
      PatternSmithClipboard.Write(payload);
      RhinoApp.WriteLine($"{nameof(vCopyToPatternSmith)}: copied {CountText.Format(request.Elements("Table").Elements("Pattern").Count(),"pattern")}. Press Ctrl+V in PatternSmith.");
    }

    private static Result CopyWithDefaults(RhinoDoc doc,RhinoObject[] targets,CancellationToken token)
    {
      var preparation=ReadPreparation();
      void Report(int percent,string stage)=>StatusBar.UpdateProgressMeter(stage,percent,true);
      Action responsive=()=>{RhinoApp.Wait();token.ThrowIfCancellationRequested();};
      var review=vCopyToPatternSmith.BeginCleanupReview(doc,targets,preparation.Export,preparation.Geometry);
      RhinoApp.WriteLine($"{nameof(vCopyToPatternSmith)}: preparing patterns. Esc to cancel.");
      var (result,request)=PreparePatternSmithRequest(doc,targets,preparation,token,Report,responsive,review,unsupported:message=>
      {
        if(message.Length>0)RhinoApp.WriteLine($"{nameof(vCopyToPatternSmith)}: {message}");
      });
      if(result!=Result.Success||request==null)return result;
      var payload=ConvertClipboardCopy(request,preparation.Export.NotchSplitLayer,token,Report);
      token.ThrowIfCancellationRequested();
      WriteClipboard(payload,request.Root!);
      return Result.Success;
    }

    private static (Result Result,XDocument? Request) PreparePatternSmithRequest(RhinoDoc doc,RhinoObject[] targets,
      PreparationConfiguration preparation,CancellationToken token,Action<int,string>? report=null,Action? responsive=null,
      Func<int,int,string?,vCleanup.ExportCleanupAction?>? review=null,Func<string,bool>? approveMixedFaces=null,Action<string>? unsupported=null,
      Action<int,int>? findingsChanged=null,Action<vExportDXF.ExportCleanupFeedback>? cleanupReviewReady=null)
    {
      XDocument? request=null;Exception? error=null;
      void CheckResponsive(){token.ThrowIfCancellationRequested();responsive?.Invoke();}
      token.ThrowIfCancellationRequested();
      // Native inline notches need their strokes available separately after trimming, not baked into the cut curve.
      var export=preparation.NativeEffects && preparation.Export.NotchTrim==NotchTrimMode.Trim
        ?preparation.Export with {NotchJoin=false}:preparation.Export;
      var preserve=preparation.NativeEffects && export.Optimize && preparation.Geometry.SimplifyCurves
        ?NativeEffectShapeSources(doc,targets,export.NotchTrim):null;
      var result=vExportDXF.RunPreparedExport(doc,RunMode.Interactive,(working,sources,entries,translation,removeGrain)=>
      {
        request=PrepareRequest(working,sources,entries,translation,removeGrain,report,token,approveMixedFaces,unsupported,preparation);
        return Result.Success;
      },stage=>{report?.Invoke(0,stage);CheckResponsive();},export,targets,preparation.Geometry,review,CheckResponsive,
        findingsChanged,ex=>error=ex,cleanupReviewReady,recordSource:preparation.NativeEffects,preserveSourceShapes:preserve);
      if(result==Result.Failure)throw new InvalidOperationException("Curve preparation failed: "+(error?.Message??"see the vTools log for details"),error);
      return (result,request);
    }

    private static void RequireWorkspaceClipboardSources(RhinoDoc doc,IEnumerable<RhinoObject> targets)
    {
      if(ReadTables(doc,targets.Select(obj=>obj.Id).ToHashSet()).Count>0)
        throw new InvalidOperationException("Copy original workspace parts. Use PatternSmith project export for placed nests.");
    }

    internal static (XDocument Request,string SplitLayer) PrepareRhinoClipboardPaste(RhinoDoc doc,uint sequence,CancellationToken token)
    {
      var targets=doc.Objects.GetObjectList(new ObjectEnumeratorSettings
        {NormalObjects=true,HiddenObjects=true,LockedObjects=true,IdefObjects=false})
        .ToArray();
      if(!targets.Any(obj=>InspectExportObject(doc,obj,0,null)))throw new InvalidOperationException("The Rhino clipboard has no supported flat geometry.");
      RequireWorkspaceClipboardSources(doc,targets);
      var preparation=ReadPreparation();
      string? reason=null;
      var review=vCopyToPatternSmith.BeginCleanupReview(doc,targets,preparation.Export,preparation.Geometry,sequence,message=>reason=message);
      var (result,request)=PreparePatternSmithRequest(doc,targets,preparation,token,review:review);
      token.ThrowIfCancellationRequested();
      if(result!=Result.Success||request==null)
        throw new InvalidOperationException(reason??"Rhino clipboard preparation did not produce any geometry.");
      return (request,preparation.Export.NotchSplitLayer);
    }

    internal static PatternSmithClipboard.PreparedPayload ConvertClipboardCopy(XDocument request,string splitLayer,
      CancellationToken token,Action<int,string>? report=null,bool background=false)
    {
      var root=request.Root!;
      var saved=ToolsOptionStore.Read(nameof(vExportPatternSmith),section=>ToolsOptionStore.Clone(section) as JsonObject??new());
      var materialTables=AssignedMaterialTables(root).ToArray();
      if(materialTables.Length>0)
      {
        var catalog=background?LoadMaterialCatalog(token):ReadCatalogWithUi(token);
        var presets=MaterialLibrary.LoadMaterialPresets().ToDictionary(material=>material.Name,StringComparer.OrdinalIgnoreCase);
        foreach(var table in materialTables)
        {
          string name=(string)table.Attribute("material")!;
          var preference=InitialMaterialPreference(saved,name,presets.GetValueOrDefault(name)?.PatternSmith);
          var material=ExportMappingDialog.MatchMaterial(name,preference,catalog);
          if(ExportMappingDialog.MaterialNeedsReview(name,preference,material,catalog))
            throw new InvalidOperationException($"Review the material mapping for '{name}' with {nameof(vCopyToPatternSmith)}.");
          if(material!=null)ApplyCatalogMaterial(table,material,clipboard:true);
        }
      }
      var configured=ReadConfiguredTools();
      var available=SelectedCutter(configured)?.Tools??configured.Tools;
      ApplyLayerTools(root,splitLayer,(layer,entity)=>ExportMappingDialog.LayerChoices(saved,configured,available,layer,entity).Selected,
        $" with {nameof(vCopyToPatternSmith)}");
      ApplyOutputFormat(root,clipboard:true);
      ValidateToolMappings(root);
      string output=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N")+".xml");
      try
      {
        BuildNativeProject(request,output,token,report,background:background);
        token.ThrowIfCancellationRequested();
        return PatternSmithClipboard.ReadPreparedPayload(output);
      }
      finally{ExportMappingDialog.DeleteNativeProject(output);}
    }

    private static void ApplyCatalogMaterial(XElement table,CatalogMaterial material,bool clipboard)
    {
      table.SetAttributeValue("materialId",material.Id);
      table.SetAttributeValue("material",material.Name);
      if(clipboard)table.SetAttributeValue("clipboardCatalogId",material.Id);
      foreach(var attribute in material.Settings.Attributes())table.SetAttributeValue(attribute.Name,attribute.Value);
    }

    private static ConfiguredCutter? SelectedCutter(ToolConfiguration configured)=>
      configured.Cutters.FirstOrDefault(cutter=>cutter.Id==configured.SelectedCutterId)??configured.Cutters.FirstOrDefault();

    private static IEnumerable<IGrouping<string,XElement>> MappedLayerGroups(XElement root)=>
      root.Elements("Table").Elements("Pattern").Descendants().Where(entity=>entity.Attribute("sourceLayer")!=null)
        .GroupBy(entity=>(string)entity.Attribute("sourceLayer")!,StringComparer.OrdinalIgnoreCase);

    private static void ApplyLayerTools(XElement root,string splitLayer,Func<string,XElement,ConfiguredTool?> select,string hint="")
    {
      foreach(var group in MappedLayerGroups(root))
      {
        Tool tool;
        if(select(group.Key,group.First()) is {} selected)tool=new(selected.Process,selected.Index);
        else if(group.Key.Equals(splitLayer,StringComparison.OrdinalIgnoreCase))
        {
          tool=DefaultToolForLayer(group.Key.Split(new[]{"::"},StringSplitOptions.None).Last(),Color.Black);
          if(tool.Process=="Unmapped")tool=new("Reference",PrimaryToolIndex);
        }
        else throw new InvalidOperationException($"Choose a PatternSmith tool for layer '{group.Key}'{hint}.");
        foreach(var entity in group)SetToolAttributes(entity,tool.Process,tool.Index);
      }
    }

    private static void ApplyOutputFormat(XElement root,bool clipboard,bool legacyTableFonts=false)
    {
      root.SetAttributeValue("legacyTableFontSizing",!clipboard&&legacyTableFonts);
      if(clipboard)root.SetAttributeValue("clipboardPatterns",true);
    }

    private static JsonNode? InitialMaterialPreference(JsonObject saved,string name,MaterialLibrary.PatternSmithReference? presetLink)
    {
      var previous=(saved["materials"] as JsonObject)?.FirstOrDefault(pair=>pair.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value;
      var baseline=MaterialLibrary.ReadPatternSmith((saved["material_baselines"] as JsonObject)?.FirstOrDefault(pair=>pair.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value);
      return presetLink!=null&&presetLink!=baseline?MaterialLibrary.PatternSmithJson(presetLink):previous;
    }
    private static XElement CreateMappingRequest(RhinoDoc doc,IReadOnlyList<RhinoObject> targets)
    {
      var root=new XElement("PatternSmithRequest",new XAttribute("sourceCount",targets.Count));var tables=ReadTables(doc,targets.Select(obj=>obj.Id).ToHashSet());
      foreach(var table in tables)root.Add(new XElement("Table",new XAttribute("material",table.Material),new XAttribute("materialAssigned",true),new XAttribute("name",table.Name),new XAttribute("populated",true)));
      var assignments=new PartMaterials(doc);
      var standalone=targets.Where(obj=>!NestTableMetadata.IsTableStructure(doc,obj)&&!tables.Any(table=>table.Members.Contains(obj.Id)))
        .Where(obj=>InspectExportObject(doc,obj,0,null))
        .Select(obj=>IsTitleObject(obj)||IsDimensionAnnotation(obj)?null:assignments.ForObjects([obj.Id])).ToArray();
      foreach(var material in standalone.Where(material=>material!=null).Select(material=>material!.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        root.Add(new XElement("Table",new XAttribute("material",material),new XAttribute("materialAssigned",true),new XAttribute("name",Config(nameof(StandaloneTableName),StandaloneTableName)),new XAttribute("populated",false)));
      if(!root.HasElements||standalone.Any(material=>material==null))root.Add(new XElement("Table",new XAttribute("materialAssigned",false),new XAttribute("name",Config(nameof(StandaloneTableName),StandaloneTableName)),new XAttribute("populated",false)));
      var pattern=new XElement("Pattern");root.Element("Table")!.Add(pattern);var layers=new Dictionary<string,XElement>(StringComparer.OrdinalIgnoreCase);
      void Visit(RhinoObject obj,int depth,Color? parent=null)
      {
        if(depth>Config(nameof(MaximumBlockDepth),MaximumBlockDepth))throw new InvalidOperationException("Source blocks exceed the supported nesting depth.");
        if(NestTableMetadata.IsTableStructure(doc,obj)||IsTitleObject(obj)||IsDimensionAnnotation(obj))return;
        var color=ObjectColor(doc,obj.Attributes,parent);
        if(obj is InstanceObject block){foreach(var child in block.InstanceDefinition.GetObjects())Visit(child,depth+1,color);return;}
        if(!SupportedExportGeometry(obj.Geometry))return;
        var tool=ResolveTool(doc,obj.Attributes,color);
        if(tool.SourceLayer==null)return;
        bool grain=vExportDXF.IsExportGrain(doc,obj);
        if(layers.TryGetValue(tool.SourceLayer,out var existing))
        {if(!grain)existing.SetAttributeValue("grainOnly",null);return;}
        var entity=WithTool(new XElement("Line"),tool);
        entity.SetAttributeValue("layerColor",unchecked((uint)(doc.Layers.FindIndex(obj.Attributes.LayerIndex)?.Color??Color.Gray).ToArgb()).ToString("X8",CultureInfo.InvariantCulture));
        if(grain)entity.SetAttributeValue("grainOnly",true);
        layers.Add(tool.SourceLayer,entity);pattern.Add(entity);
      }
      foreach(var obj in targets)Visit(obj,0);
      OrderRequestTables(root);return root;
    }

    private static IEnumerable<XElement> AssignedMaterialTables(XElement root)=>root.Elements("Table")
      .Where(table=>(bool?)table.Attribute("materialAssigned")!=false&&!string.IsNullOrWhiteSpace((string?)table.Attribute("material")));
    private static List<ExportTable> ReadTables(RhinoDoc doc,IReadOnlySet<Guid> selected)
    {
      var result=new List<ExportTable>();
      foreach(int group in NestTableMetadata.TableGroups(doc))
      {
        var members=doc.Groups.GroupMembers(group)??[];
        if(!members.Any(obj=>selected.Contains(obj.Id)))continue;
        var borders=members.Where(obj=>NestTableMetadata.IsTableBorder(doc,obj)&&NestTableMetadata.IsBorderGroup(doc,obj,group)).ToArray();
        if(borders.Length!=1)throw new InvalidOperationException("A selected vNest table has no unique material border: '"+doc.Groups.GroupName(group)+"'.");
        if(!vNest.TryGetExportTable(doc,borders[0],out var frame,out double width,out double length,out string material,out string name))
          throw new InvalidOperationException("Could not resolve the material frame of vNest table '"+doc.Groups.GroupName(group)+"'.");
        // vNest packs width along frame.X and length along frame.Y. This proper
        // plane rotation preserves handedness and makes PS X=length, Y=width.
        var psPlane=new Plane(frame.PointAt(width,0),frame.YAxis,-frame.XAxis);
        result.Add(new(group,psPlane,width,length,material,name,members.Select(obj=>obj.Id).ToHashSet(),vNest.ExportTableGap(doc,borders[0],group,material)){FaceDown=vNest.ExportTableFaceDown(borders[0])});
      }
      return result;
    }

    private static bool IsTitleAnnotation(RhinoDoc doc,IReadOnlyList<Guid> ids)=>ids.Count>0&&ids.All(id=>doc.Objects.FindId(id) is {} obj&&IsTitleObject(obj));
    private static bool IsTitleObject(RhinoObject obj)=>obj.Attributes.GetUserString(vTitle.TitleFlagKey)==vTitle.TitleFlagValue||vTitle.IsTitleFrame(obj);
    private static bool IsDimensionAnnotation(RhinoObject obj)=>obj.Geometry is Dimension or Leader;
    private static bool SupportedExportGeometry(GeometryBase geometry)=>geometry is Curve or Rhino.Geometry.Point or TextEntity or Dimension or Leader or InstanceReferenceGeometry;
    private static bool InspectExportObject(RhinoDoc doc,RhinoObject obj,int depth,Dictionary<string,int>? ignored)
    {
      if(depth>Math.Max(1,Config(nameof(MaximumBlockDepth),MaximumBlockDepth)))throw new InvalidOperationException("Source blocks exceed the supported nesting depth.");
      if(ZeroLengthCurve(obj.Geometry))return false;
      if(obj is InstanceObject block)
      {
        bool supported=false;
        foreach(var child in block.InstanceDefinition.GetObjects())supported|=InspectExportObject(doc,child,depth+1,ignored);
        return supported;
      }
      if(SupportedExportGeometry(obj.Geometry))return true;
      if(ignored!=null&&(obj.Geometry is not Hatch||!NestTableMetadata.HasDecorationRole(obj.Attributes)))
      {string type=obj.Geometry.GetType().Name;ignored[type]=ignored.GetValueOrDefault(type)+1;}
      return false;
    }
    private static string UnsupportedMessage(IReadOnlyDictionary<string,int> ignored)=>ignored.Count==0?string.Empty:
      "Ignored "+CountText.Format(ignored.Values.Sum(),"unsupported object")+" ("+string.Join("; ",ignored.OrderBy(pair=>pair.Key,StringComparer.Ordinal).Select(pair=>pair.Key+": "+pair.Value))+").";
    private static GeometryBase[] AnnotationDisplayPieces(RhinoDoc doc,AnnotationBase source)
    {
      using var annotation=(AnnotationBase)source.Duplicate();
      using var style=annotation.GetDimensionStyle(doc.DimStyles.FindId(annotation.DimensionStyleId)??doc.DimStyles.Current);
#pragma warning disable CS0618 // Rhino 8 needs the UnitSystem overload; Rhino 9 retains it for compatibility.
      if(annotation is Dimension dimension)dimension.UpdateDimensionText(style,doc.ModelUnitSystem);
#pragma warning restore CS0618
      return annotation is Dimension resolved?resolved.Explode():((Leader)annotation).Explode();
    }
    private static void CollectUnsupportedHighlights(RhinoDoc doc,RhinoObject obj,Transform transform,int depth,Dictionary<Guid,GeometryBase> highlights)
    {
      if(depth>Math.Max(1,Config(nameof(MaximumBlockDepth),MaximumBlockDepth)))return;
      if(ZeroLengthCurve(obj.Geometry))return;
      if(obj is InstanceObject instance)
      {
        foreach(var child in instance.InstanceDefinition.GetObjects())CollectUnsupportedHighlights(doc,child,transform*instance.InstanceXform,depth+1,highlights);
        return;
      }
      if(obj.Geometry is Hatch&&NestTableMetadata.HasDecorationRole(obj.Attributes))return;
      bool ignored=!SupportedExportGeometry(obj.Geometry)||obj.Geometry is Curve {IsValid:false}&&IsTitleObject(obj);
      if(IsDimensionAnnotation(obj))
      {
        var pieces=AnnotationDisplayPieces(doc,(AnnotationBase)obj.Geometry);
        try
        {
          ignored=pieces.Any(piece=>!SupportedExportGeometry(piece)||piece is Curve {IsValid:false}&&!ZeroLengthCurve(piece));
          foreach(var block in pieces.OfType<InstanceReferenceGeometry>())
            if(doc.InstanceDefinitions.FindId(block.ParentIdefId) is {} definition)
              foreach(var child in definition.GetObjects())CollectUnsupportedHighlights(doc,child,transform*block.Xform,depth+1,highlights);
        }
        finally{foreach(var piece in pieces)piece.Dispose();}
      }
      if(!ignored)return;
      var copy=obj.Geometry.Duplicate();
      try
      {
        if(!copy.Transform(transform))return;
        if(!copy.IsValid)
        {
          var bounds=copy.GetBoundingBox(true);if(!bounds.IsValid)return;
          copy.Dispose();copy=new Rhino.Geometry.Point(bounds.Center);
        }
        highlights.Add(Guid.NewGuid(),copy);copy=null!;
      }
      finally{copy?.Dispose();}
    }
    private static string TitleName(RhinoDoc doc,vExportDXF.PreparedExportEntry entry)=>entry.SourceIds.Select(doc.Objects.FindId)
      .Select(obj=>(obj?.Geometry as TextEntity)?.PlainText.Trim()).FirstOrDefault(text=>!string.IsNullOrEmpty(text))??entry.GroupName??"Title";
    private static string PartName(RhinoDoc doc,vExportDXF.PreparedExportEntry entry,int number)
    {
      foreach(var id in entry.SourceIds)
      {
        if(doc.Objects.FindId(id) is not {} source)continue;
        if(source.Attributes.GetUserString(PartGroupData.SourceSettingsKey) is {Length:>0} key&&
          doc.Strings.GetValue(PartGroupData.NamesSection,key) is {Length:>0} saved)return saved;
        foreach(int group in source.Attributes.GetGroupList()??[])
          if(doc.Groups[group] is {IsDeleted:false} owner&&
            doc.Strings.GetValue(PartGroupData.NamesSection,PartGroupData.GroupKey(owner.Id)) is {Length:>0} name)return name;
      }
      var objects=entry.SourceIds.Select(doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false}).ToArray();
      if(NestWorkflow.PartLabelName(doc,objects.Select(obj=>(obj!.Geometry,obj.Attributes)),Plane.WorldXY) is {} label)return label;
      return string.IsNullOrWhiteSpace(entry.GroupName)||System.Text.RegularExpressions.Regex.IsMatch(entry.GroupName,@"^vNest_[0-9a-f]+_part_\d+$",System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        ?"Part "+number:entry.GroupName;
    }

    private static IEnumerable<XElement> SerializeObject(RhinoDoc doc,RhinoObject obj,Transform transform,
      double tolerance,int depth,Color? parentColor,bool tableAnnotation=false,bool removeGrain=false,CurveConversionSettings? curveSettings=null,Action<string>? ignored=null,
      string? tableLabelFont=null,bool exportOrigin=false)
    {
      if(depth>Math.Max(1,Config(nameof(MaximumBlockDepth),MaximumBlockDepth)))throw new InvalidOperationException("Prepared blocks exceed the supported nesting depth.");
      if(removeGrain&&vExportDXF.IsExportGrain(doc,obj))yield break;
      Color color=ObjectColor(doc,obj.Attributes,parentColor);
      if(obj is InstanceObject instance)
      {
        if(instance.InstanceDefinition is not {} definition)throw new InvalidOperationException("A prepared block definition is missing.");
        foreach(var child in definition.GetObjects())
          foreach(var entity in SerializeObject(doc,child,transform*instance.InstanceXform,tolerance,depth+1,color,tableAnnotation,removeGrain,curveSettings,ignored,tableLabelFont,exportOrigin))yield return entity;
        yield break;
      }
      if(NestTableMetadata.HasDecorationRole(obj.Attributes)&&!tableAnnotation)yield break;
      if(tableAnnotation&&obj.Geometry is Hatch)yield break;
      Tool tool=tableAnnotation?new("Reference",PrimaryToolIndex):ResolveTool(doc,obj.Attributes,color);
      if(tableLabelFont!=null&&obj.Geometry is TextEntity text)
      {
        yield return SerializeTextCore(doc,text,transform,tool,tolerance,tableLabelFont);yield break;
      }
      foreach(var entity in SerializeGeometry(doc,obj.Geometry,transform,tool,tolerance,depth,curveSettings,ignored))
      {
        if(exportOrigin && obj.Geometry is Curve && obj.Attributes.GetUserString(vExportDXF.PreparedSourceKey) is {} id)
          entity.SetAttributeValue("origin",id);
        yield return entity;
      }
    }
    private static IEnumerable<XElement> SerializeGeometry(RhinoDoc doc,GeometryBase source,Transform transform,Tool tool,
      double tolerance,int depth,CurveConversionSettings? curveSettings,Action<string>? ignored)
    {
      if(depth>Math.Max(1,Config(nameof(MaximumBlockDepth),MaximumBlockDepth)))throw new InvalidOperationException("Prepared annotations exceed the supported nesting depth.");
      if(source is TextEntity text)
      {
        yield return SerializeText(doc,text,transform,tool,tolerance);yield break;
      }
      if(source is Dimension or Leader)
      {
        var pieces=AnnotationDisplayPieces(doc,(AnnotationBase)source);
        if(pieces==null||pieces.Length==0)throw new InvalidOperationException("Could not resolve the displayed geometry of "+source.GetType().Name+".");
        try
        {
          var reference=new Tool("Reference",PrimaryToolIndex);
          foreach(var piece in pieces)foreach(var entity in SerializeGeometry(doc,piece,transform,reference,tolerance,depth+1,curveSettings,ignored))yield return entity;
        }
        finally{foreach(var piece in pieces)piece.Dispose();}
        yield break;
      }
      if(source is InstanceReferenceGeometry block)
      {
        var definition=doc.InstanceDefinitions.FindId(block.ParentIdefId)??throw new InvalidOperationException("An annotation arrowhead block is missing.");
        foreach(var child in definition.GetObjects())
          foreach(var entity in SerializeObject(doc,child,transform*block.Xform,tolerance,depth+1,null,tableAnnotation:true,curveSettings:curveSettings,ignored:ignored))yield return entity;
        yield break;
      }
      if(ZeroLengthCurve(source))yield break;
      if(!SupportedExportGeometry(source)){ignored?.Invoke(source.GetType().Name);yield break;}
      if(IgnoreInvalidAnnotationDisplay(source,tool,ignored))yield break;
      using var geometry=source.Duplicate();
      if(!geometry.Transform(transform))throw new InvalidOperationException("Could not transform prepared geometry into PatternSmith coordinates.");
      if(ZeroLengthCurve(geometry)||IgnoreInvalidAnnotationDisplay(geometry,tool,ignored))yield break;
      var bounds=geometry.GetBoundingBox(true);
      if(!bounds.IsValid||Math.Abs(bounds.Min.Z)>tolerance||Math.Abs(bounds.Max.Z)>tolerance)
        throw new InvalidOperationException("Prepared geometry must lie in its table plane (or WorldXY for standalone parts).");
      if(geometry is Curve curve)
      {foreach(var entity in SerializeCurve(curve,tool,tolerance,curveSettings))yield return entity;yield break;}
      if(geometry is Rhino.Geometry.Point point)
      {
        yield return WithTool(new XElement("Point",new XAttribute("x",Number(point.Location.X)),new XAttribute("y",Number(point.Location.Y)),
          new XAttribute("radius",Number(Config(nameof(PointMarkerRadiusInches),PointMarkerRadiusInches))),new XAttribute("rotation","0")),tool);yield break;
      }
      ignored?.Invoke(source.GetType().Name);
    }
    private static bool IgnoreInvalidAnnotationDisplay(GeometryBase geometry,Tool tool,Action<string>? ignored)
    {
      if(geometry is not Curve curve||curve.IsValid||tool.Process!="Reference"||tool.SourceLayer!=null)return false;
      curve.IsValidWithLog(out string reason);
      ignored?.Invoke("invalid "+curve.GetType().Name+" annotation display");
      Log.Write(nameof(vExportPatternSmith),"ignored invalid annotation display curve type={0}: {1}",curve.GetType().Name,reason);
      return true;
    }
    private static bool ZeroLengthCurve(GeometryBase geometry)
    {
      if(geometry is not Curve curve)return false;
      if(curve is PolylineCurve {PointCount:<2} or PolyCurve {SegmentCount:0} or NurbsCurve {Points.Count:<2})return true;
      if(curve is LineCurve)return curve.PointAtStart.IsValid&&curve.PointAtEnd.IsValid&&curve.PointAtStart.DistanceTo(curve.PointAtEnd)<=RhinoMath.ZeroTolerance;
      var bounds=curve.GetBoundingBox(true);
      return bounds.IsValid&&bounds.Diagonal.Length<=RhinoMath.ZeroTolerance; // Kernel-zero spatial extent only; closed curves and invalid nonempty geometry must not be mistaken for empty curves.
    }

    private sealed record NativeNotchCandidate(Guid Source,Guid[] Pieces,XElement Effect);

    private static HashSet<Guid> NativeEffectShapeSources(RhinoDoc doc,RhinoObject[] targets,NotchTrimMode trim)
    {
      var curves=targets.Where(obj=>obj.Geometry is Curve).ToArray();
      var ids=curves.Select(obj=>obj.Id).ToHashSet();
      var preserve=new HashSet<Guid>();
      foreach(var seam in vSeam.ExportAllowances(doc,curves))
      {
        preserve.UnionWith(seam.Outputs);
        preserve.UnionWith(seam.SignedWidths.Keys);
      }
      if(trim!=NotchTrimMode.Split)
        foreach(var obj in curves.Where(obj=>obj.Attributes.GetUserString(MatchingNotches.Prefix+"object_role")=="notch"))
        {
          var sources=NativeNotchReferences(obj.Attributes).Where(ids.Contains).ToArray();
          preserve.Add(obj.Id);
          preserve.UnionWith(sources);
        }
      return preserve;
    }

    private static Guid[] NativeNotchReferences(ObjectAttributes attributes)
    {
      var references=MatchingNotches.ReferenceIds(attributes);
      if(references.Length==0 && Guid.TryParse(attributes.GetUserString(MatchingNotches.Prefix+"curve_id"),out var id))references=[id];
      if(references.Length==0 && attributes.GetUserString(MatchingNotches.SourceCurveIdsKey) is {} savedIds)
        try{references=System.Text.Json.JsonSerializer.Deserialize<Guid[]>(savedIds)??[];}catch(System.Text.Json.JsonException){}
      return references;
    }

    private static void AddNativeEffectCandidates(RhinoDoc doc,XElement pattern,Guid[] sourceIds,Transform transform,
      double scale,double tolerance,NotchTrimMode trim,CurveConversionSettings curves)
    {
      var objects=sourceIds.Select(doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false,Geometry:Curve})
        .Cast<RhinoObject>().DistinctBy(obj=>obj.Id).ToDictionary(obj=>obj.Id);
      var effects=objects.Values.Where(obj=>vSeam.IsAttachedAllowance(obj) ||
        obj.Attributes.GetUserString(MatchingNotches.Prefix+"object_role")=="notch").Select(obj=>obj.Id.ToString("N")).ToArray();
      if(effects.Length>0)pattern.SetAttributeValue("effectOrigins",string.Join(";",effects));
      var prepared=pattern.Elements().Where(entity=>Guid.TryParseExact((string?)entity.Attribute("origin"),"N",out _))
        .ToLookup(entity=>Guid.ParseExact((string)entity.Attribute("origin")!,"N"));
      var notchCandidates=new List<NativeNotchCandidate>();
      if(trim!=NotchTrimMode.Split)
        foreach(var group in objects.Values.Where(obj=>obj.Attributes.GetUserString(MatchingNotches.Prefix+"object_role")=="notch")
          .GroupBy(obj=>obj.Attributes.GetUserString(MatchingNotches.Prefix+"component_set")??obj.Id.ToString("N")))
        {
          var parts=group.ToArray();
          if(parts.All(obj=>prepared.Contains(obj.Id)) && ReadNativeNotchCandidate(doc,objects,parts,transform,scale,tolerance,trim) is {} notch)
          {
            CopyPreparedTool(notch.Effect,prepared[parts[0].Id].First());
            notchCandidates.Add(notch);
          }
        }

      foreach(var seam in vSeam.ExportAllowances(doc,objects.Values.ToArray()))
      {
        if(!seam.Outputs.All(prepared.Contains) || !seam.SignedWidths.Keys.All(prepared.Contains))continue;
        var tools=seam.Outputs.SelectMany(id=>prepared[id]).Select(entity=>
          ((string?)entity.Attribute("sourceLayer"),(string?)entity.Attribute("tool"),(string?)entity.Attribute("index"))).Distinct().ToArray();
        if(tools.Length!=1)continue;
        var allowanceTool=prepared[seam.Outputs[0]].First();
        var notches=notchCandidates.Where(notch=>seam.SignedWidths.ContainsKey(notch.Source)).ToArray();
        var baseSources=new Dictionary<Guid,XElement>();
        foreach(var pair in seam.SignedWidths)
        {
          var source=EffectSource(pair.Key);
          if(source==null)break;
          if(pair.Value!=0)
          {
            var effect=new XElement("Seam",new XAttribute("Width",Number(pair.Value*scale)));
            if(seam.Styles?.GetValueOrDefault(pair.Key) is {} authored)
            {
              effect.SetAttributeValue("StartStyle",authored.Start);
              effect.SetAttributeValue("EndStyle",authored.End);
            }
            CopyPreparedTool(effect,allowanceTool);
            source.Add(effect);
          }
          baseSources.Add(pair.Key,source);
        }
        if(baseSources.Count!=seam.SignedWidths.Count)continue;
        var replaced=seam.Outputs.Concat(baseSources.Keys).ToArray();
        if(notches.Length>0)
        {
          var combined=baseSources.ToDictionary(pair=>pair.Key,pair=>new XElement(pair.Value));
          foreach(var notch in notches)combined[notch.Source].Add(new XElement(notch.Effect));
          AddSet(replaced.Concat(notches.SelectMany(notch=>notch.Pieces)),combined.Values);
        }
        AddSet(replaced,baseSources.Values);
      }
      foreach(var group in notchCandidates.GroupBy(notch=>notch.Source))
      {
        var source=EffectSource(group.Key);
        if(source==null)continue;
        foreach(var notch in group)source.Add(new XElement(notch.Effect));
        AddSet(group.SelectMany(notch=>notch.Pieces).Append(group.Key),[source]);
      }

      void AddSet(IEnumerable<Guid> replaced,IEnumerable<XElement> candidates)=>pattern.Add(new XElement("EffectSet",
        new XAttribute("replace",string.Join(";",replaced.Distinct().Select(id=>id.ToString("N")))),
        new XAttribute("tolerance",Number(tolerance)),candidates));

      XElement? EffectSource(Guid id)
      {
        if(!prepared.Contains(id) || !objects.TryGetValue(id,out var obj) || obj.Geometry is not Curve original)return null;
        var first=prepared[id].First();
        // A single native source cannot represent pieces deliberately moved to different tool layers.
        if(prepared[id].Any(piece=>(string?)piece.Attribute("sourceLayer")!=(string?)first.Attribute("sourceLayer")))return null;
        using var geometry=original.DuplicateCurve();
        if(!geometry.Transform(transform))return null;
        var bounds=geometry.GetBoundingBox(true);
        if(!bounds.IsValid || Math.Abs(bounds.Min.Z)>tolerance || Math.Abs(bounds.Max.Z)>tolerance)return null;
        try
        {
          var entities=SerializeCurve(geometry,new Tool((string)first.Attribute("tool")!,int.Parse((string)first.Attribute("index")!,CultureInfo.InvariantCulture),
            (string?)first.Attribute("sourceLayer")),tolerance,curves).ToArray();
          if(entities.Length==0)return null;
          if(first.Attribute("excludeFromPerimeter") is {} excluded)
            foreach(var entity in entities)entity.SetAttributeValue(excluded.Name,excluded.Value);
          return new XElement("Source",entities);
        }
        catch(InvalidOperationException){return null;}
      }
    }

    private static void CopyPreparedTool(XElement target,XElement source)
    {
      foreach(string name in new[]{"tool","index","sourceLayer"})
        target.SetAttributeValue(name,(string?)source.Attribute(name));
    }

    private static NativeNotchCandidate? ReadNativeNotchCandidate(RhinoDoc doc,IReadOnlyDictionary<Guid,RhinoObject> objects,
      RhinoObject[] pieces,Transform transform,double scale,double tolerance,NotchTrimMode trim)
    {
      string prefix=MatchingNotches.Prefix;
      var attributes=pieces[0].Attributes;
      string? Meta(string name)=>attributes.GetUserString(prefix+name);
      var type=MatchingNotches.CanonicalType(Meta("notch_type"));
      string? style=MatchingNotches.ProfileType(type) switch {"I"=>"Slit","T"=>"T","V"=>"V","U"=>"Castle",_=>null};
      if(style==null || (!MatchingNotches.TryPoint(doc,attributes,"curve_point",out var anchor) &&
        !MatchingNotches.TryPoint(Meta("curve_point"),out anchor)) ||
        !double.TryParse(Meta("notch_length"),NumberStyles.Float,CultureInfo.InvariantCulture,out double depth) ||
        !double.IsFinite(depth) || depth<=0)return null;
      double.TryParse(Meta("notch_width"),NumberStyles.Float,CultureInfo.InvariantCulture,out double width);
      if(!double.IsFinite(width) || width<0 || style!="Slit" && width<=0)return null;
      double.TryParse(Meta("notch_offset"),NumberStyles.Float,CultureInfo.InvariantCulture,out double offset);
      if(offset>0 && style is "Slit" or "T")depth=Math.Min(depth,offset);
      bool NearAnchor(RhinoObject obj,out double parameter)
      {
        parameter=0;
        return obj.Geometry is Curve curve && curve.ClosestPoint(anchor,out parameter) &&
          curve.PointAt(parameter).DistanceTo(anchor)*scale<=tolerance;
      }
      var references=NativeNotchReferences(attributes);
      var sources=references.Distinct().Where(id=>objects.TryGetValue(id,out var obj) && NearAnchor(obj,out _)).Take(2).ToArray();
      // A joined sew curve replaces the old IDs; a later move can also leave the legacy point behind.
      if(sources.Length==0 && references.Length>0 && !references.Any(objects.ContainsKey))
      {
        var joined=objects.Values.Where(vSeam.IsJoinedAllowanceSource).ToArray();
        double cosine=Math.Cos(ConfiguredApproximationAngle());
        if(MatchingNotches.TryPoint(Meta("tangent_hint"),out var hint))
        {
          var direction=new Vector3d(hint.X,hint.Y,hint.Z);
          if(direction.Unitize())
            sources=joined.Where(obj=>NearAnchor(obj,out double parameter) &&
              Math.Abs(((Curve)obj.Geometry).TangentAt(parameter)*direction)>=cosine).Select(obj=>obj.Id).Take(2).ToArray();
        }
        if(sources.Length==0 && style is "V" or "Castle" && pieces.Length==1 &&
          pieces[0].Geometry is Curve stroke && stroke.TryGetPolyline(out var points) && points.Count>=3)
        {
          var chord=points[^1]-points[0];
          var mouth=points[0]+chord*.5;
          if(chord.Unitize())
          {
            var matches=new List<(Guid Id,Point3d Anchor)>();
            foreach(var obj in joined)
            {
              var curve=(Curve)obj.Geometry;
              if(!curve.ClosestPoint(mouth,out double t) || Math.Abs(curve.TangentAt(t)*chord)<cosine)continue;
              var point=curve.PointAt(t);
              if(Math.Abs(point.DistanceTo(mouth)-Math.Abs(offset))*scale<=tolerance)
                matches.Add((obj.Id,point));
            }
            if(matches.Count==1){sources=[matches[0].Id];anchor=matches[0].Anchor;}
          }
        }
      }
      if(sources.Length!=1)return null;
      anchor.Transform(transform);
      var effect=new XElement("Notch",new XAttribute("Style",style),new XAttribute("Depth",Number(depth*scale)),new XAttribute("Width",Number(width*scale)),
        new XAttribute("DistanceUnits",Meta("mode")=="percent"?"Fractional":"Native"),
        new XAttribute("ExecutionStyle",trim==NotchTrimMode.Trim && style is "V" or "Castle"?"Inline":"Discrete"),
        new XAttribute("Side",MatchingNotches.IsExternalType(type)?"External":"Internal"),
        new XAttribute("pieces",string.Join(";",pieces.Select(obj=>obj.Id.ToString("N")))),P(anchor));
      return new(sources[0],pieces.Select(obj=>obj.Id).ToArray(),effect);
    }

    private static IEnumerable<XElement> SerializeCurve(Curve curve,Tool tool,double tolerance,CurveConversionSettings? curveSettings=null)
    {
      if(ZeroLengthCurve(curve))yield break;
      curveSettings??=ReadCurveConversionSettings();
      if(!curve.IsValid)
      {curve.IsValidWithLog(out string reason);throw new InvalidOperationException("A prepared "+curve.GetType().Name+" curve is invalid: "+reason);}
      if(curve.IsLinear())
      {yield return WithTool(new XElement("Line",P(curve.PointAtStart),P(curve.PointAtEnd)),tool);yield break;}
      if(curve.TryGetCircle(out var circle))
      {
        yield return WithTool(new XElement("Circle",new XAttribute("x",Number(circle.Center.X)),new XAttribute("y",Number(circle.Center.Y)),
          new XAttribute("radius",Number(circle.Radius))),tool);yield break;
      }
      if(curve.TryGetArc(out var arc))
      {
        var start=curve.PointAtStart;var radius=start-arc.Center;var tangent=curve.TangentAtStart;
        double angle=Math.Atan2(start.Y-arc.Center.Y,start.X-arc.Center.X);
        double sweep=(radius.X*tangent.Y-radius.Y*tangent.X<0?-1:1)*arc.Angle;
        yield return WithTool(new XElement("Arc",new XAttribute("x",Number(arc.Center.X)),new XAttribute("y",Number(arc.Center.Y)),
          new XAttribute("radius",Number(arc.Radius)),new XAttribute("start",Number(angle)),new XAttribute("sweep",Number(sweep))),tool);yield break;
      }
      if(curve.TryGetPolyline(out var polyline))
      {yield return WithTool(new XElement("Polyline",new XAttribute("closed",curve.IsClosed?"true":"false"),polyline.Select(P)),tool);yield break;}
      XElement BezierRequest(IEnumerable<Point3d> points,bool closed,double approximationError)
      {
        string mode=curveSettings.Mode;
        if(mode is not ("NativeSpline" or "Bezier"))throw new InvalidOperationException("Choose NativeSpline or Bezier in vSettings / vExportPatternSmith / curve_mode.");
        var request=WithTool(new XElement("BezierGroup",new XAttribute("closed",closed),
          new XAttribute("tolerance",Number(tolerance-approximationError)),
          approximationError>0?new XAttribute("approximationError",Number(approximationError)):null,points.Select(P)),tool);
        ApplySplineConversion(request,mode=="NativeSpline",curveSettings.ThinError);
        return request;
      }

      if(curve is PolyCurve)
      {
        var segments=curve.DuplicateSegments();
        try
        {
          double angle=ConfiguredApproximationAngle();
          double cosine=Math.Cos(angle);
          int start=0;
          while(start<segments.Length)
          {
            int end=start+1;
            while(end<segments.Length && CanKeepSmoothJunction(segments[end-1],segments[end],tolerance,cosine))end++;
            if(end-start>1 && TryCombineSmoothControls(segments.AsSpan(start,end-start),tolerance,out var points,out var error))
              yield return BezierRequest(points,curve.IsClosed&&start==0&&end==segments.Length&&
                CanKeepSmoothJunction(segments[^1],segments[0],tolerance,cosine),error);
            else
              for(int i=start;i<end;i++)
                foreach(var entity in SerializeCurve(segments[i],tool,tolerance,curveSettings))yield return entity;
            start=end;
          }
        }
        finally{foreach(var segment in segments)segment.Dispose();}
        yield break;
      }

      var (controls,fitError)=CubicControls(curve,tolerance);
      yield return BezierRequest(controls,curve.IsClosed,fitError);
    }

    private static bool CanKeepSmoothJunction(Curve first,Curve second,double tolerance,double cosine) =>
      !first.IsClosed && !second.IsClosed &&
      first.PointAtEnd.DistanceTo(second.PointAtStart)<=tolerance/CubicFitToleranceDivisor &&
      first.TangentAtEnd*second.TangentAtStart>=cosine;

    private static bool TryCombineSmoothControls(ReadOnlySpan<Curve> segments,double tolerance,
      out List<Point3d> points,out double error)
    {
      points=[];error=0;
      try
      {
        foreach(var segment in segments)
        {
          var (controls,fit)=CubicControls(segment,tolerance);
          if(points.Count==0)points.AddRange(controls);
          else
          {
            fit+=points[^1].DistanceTo(controls[0]);
            points.AddRange(controls.Skip(1));
          }
          error=Math.Max(error,fit);
        }
        return error<tolerance;
      }
      catch(InvalidOperationException){return false;}
    }

    private static (List<Point3d> Controls,double Error) CubicControls(Curve curve,double tolerance)
    {
      using(var nurbs=curve.ToNurbsCurve())
      {
        if(nurbs!=null&&!nurbs.IsRational&&nurbs.Degree<=3&&(nurbs.Degree==3||nurbs.IncreaseDegree(3))&&
          nurbs.MakePiecewiseBezier(false)&&nurbs.Points.Count>=4&&(nurbs.Points.Count-1)%3==0)
        {
          return (nurbs.Points.Select(point=>point.Location).ToList(),0);
        }
      }
      return FitCubicControls(curve,tolerance);
    }

    private static double ConfiguredApproximationAngle()
    {
      double angle=Config(nameof(ApproximationAngleRadians),ApproximationAngleRadians);
      return double.IsFinite(angle)&&angle>0?angle:ApproximationAngleRadians;
    }

    private static (List<Point3d> Controls,double Error) FitCubicControls(Curve curve,double tolerance)
    {
      var failure=new InvalidOperationException("Could not fit a smooth "+curve.GetType().Name+" within the document tolerance.");
      double fit=Math.Max(RhinoMath.SqrtEpsilon,tolerance/CubicFitToleranceDivisor);
      if(fit*2>=tolerance)throw failure;
      var segments=BezierCurve.CreateCubicBeziers(curve,fit,ConfiguredApproximationAngle());
      if(segments==null||segments.Length==0)throw failure;
      var points=new List<Point3d>();
      double adjustment=0;
      try
      {
        foreach(var segment in segments)
        {
          if(segment==null||segment.ControlVertexCount!=4)throw failure;
          var start=segment.GetControlVertex3d(0);
          if(!start.IsValid)throw failure;
          if(points.Count==0)points.Add(start);
          else adjustment=Math.Max(adjustment,start.DistanceTo(points[^1]));
          for(int index=1;index<4;index++)
          {
            var point=segment.GetControlVertex3d(index);
            if(!point.IsValid)throw failure;
            points.Add(point);
          }
        }
      }
      finally{foreach(var segment in segments)segment?.Dispose();}
      var end=curve.IsClosed?curve.PointAtStart:curve.PointAtEnd;
      adjustment=Math.Max(adjustment,Math.Max(points[0].DistanceTo(curve.PointAtStart),points[^1].DistanceTo(end)));
      if(adjustment>fit)throw failure;
      points[0]=curve.PointAtStart;points[^1]=end;
      // Align seams exactly. A Bezier moves by at most the largest displacement of its controls.
      return (points,fit+adjustment);
    }

    private static void ApplySplineConversion(XElement curve,bool native,double thinError)
    {
      curve.SetAttributeValue("nativeSpline",native);
      curve.SetAttributeValue("thinError",Number(Math.Max(0,thinError-((double?)curve.Attribute("approximationError")??0))));
    }

    private static string ConfiguredTableLabelFont()
    {
      string family=Config(nameof(TableLabelFontFamily),TableLabelFontFamily).Trim();
      return family.Length==0?TableLabelFontFamily:family;
    }

    private static XElement SerializeText(RhinoDoc doc,TextEntity text,Transform transform,Tool tool,double tolerance)
      =>SerializeTextCore(doc,text,transform,tool,tolerance,null);

    private static XElement SerializeTextCore(RhinoDoc doc,TextEntity text,Transform transform,Tool tool,double tolerance,string? fontFamily)
    {
      double displayHeight=text.TextHeight*AnnotationTextTransform.ResolveDisplayDimensionScale(doc,text,doc.Views.ActiveView?.ActiveViewport);
      Point3d center=text.Plane.Origin;
      if(text.TextHorizontalAlignment!=TextHorizontalAlignment.Center||text.TextVerticalAlignment!=TextVerticalAlignment.Middle)
      {
        var box=AnnotationTextTransform.TextBounds(text);
        if(!box.IsValid)throw new InvalidOperationException("Could not resolve a prepared text layout.");
        center=text.Plane.PointAt(box.Center.X,box.Center.Y);
      }
      var origin=transform*text.Plane.Origin;var x=transform*text.Plane.PointAt(1,0)-origin;
      var y=transform*text.Plane.PointAt(0,1)-origin;center=transform*center;
      if(Math.Abs(origin.Z)>tolerance||Math.Abs(x.Z)>tolerance||Math.Abs(y.Z)>tolerance)
        throw new InvalidOperationException("Prepared text is not in the export plane.");
      if(x.SquareLength<=0||y.SquareLength<=0)throw new InvalidOperationException("Prepared text has an invalid transform.");
      double textTolerance=Config(nameof(TextTransformRelativeTolerance),TextTransformRelativeTolerance);
      if(Math.Abs(x.Length-y.Length)>textTolerance*Math.Max(x.Length,y.Length)||
        Math.Abs(x*y)>textTolerance*x.Length*y.Length)
        throw new InvalidOperationException("Nonuniformly scaled or skewed text cannot be exported as native PatternSmith text.");
      bool mirrorY=Vector3d.CrossProduct(x,y).Z<0;
      string content=fontFamily==null?text.PlainText.ToUpperInvariant().Replace('\u201c','"').Replace('\u201d','"'):text.PlainText;
      var entity=new XElement("Text",new XAttribute("x",Number(center.X)),new XAttribute("y",Number(center.Y)),
        new XAttribute("rotation",Number(Math.Atan2(x.Y,x.X))),new XAttribute("height",Number(displayHeight*x.Length)),
        new XAttribute("characterRotation","0"),new XAttribute("mirrorY",mirrorY?"true":"false"),content);
      if(fontFamily!=null)entity.SetAttributeValue("fontFamily",fontFamily);
      return WithTool(entity,tool);
    }

    private static Tool ResolveTool(RhinoDoc doc,ObjectAttributes attributes,Color color)
    {
      string layer=attributes.LayerIndex>=0&&attributes.LayerIndex<doc.Layers.Count?doc.Layers[attributes.LayerIndex].Name.Trim():string.Empty;
      string path=attributes.LayerIndex>=0&&attributes.LayerIndex<doc.Layers.Count?doc.Layers[attributes.LayerIndex].FullPath:layer;
      var resolved=DefaultToolForLayer(layer,color);
      return resolved with {SourceLayer=path};
    }

    private static Tool DefaultToolForLayer(string layer,Color color)
    {
      switch(layer.ToUpperInvariant())
      {
        case "CUT":case "CUT1":return new("Cut",PrimaryToolIndex);
        case "CUT2":return new("Cut",PrimaryToolIndex+1);
        case "PLOT":case "MARK":return new("Mark",PrimaryToolIndex);
        case "REF":case "REFERENCE":return new("Reference",PrimaryToolIndex);
        case "PUNCH":return new("Punch",PrimaryToolIndex);
        case "DRILL":return new("Drill",PrimaryToolIndex);
        case "CREASE":return new("Crease",PrimaryToolIndex);
        case "PRINT":return new("Print",PrimaryToolIndex);
      }
      if(color.R==255&&color.G==0&&color.B==0)return new("Cut",PrimaryToolIndex);
      if(color.R==255&&color.G==0&&color.B==255)return new("Cut",PrimaryToolIndex+1);
      if(color.R==0&&color.G==255&&color.B==0)return new("Mark",PrimaryToolIndex);
      if(color.R==255&&color.G==255&&color.B==0)return new("Reference",PrimaryToolIndex);
      return new("Unmapped",PrimaryToolIndex);
    }

    private static Color ObjectColor(RhinoDoc doc,ObjectAttributes attributes,Color? parent)
    {
      if(attributes.ColorSource==ObjectColorSource.ColorFromObject)return attributes.ObjectColor;
      if(attributes.ColorSource==ObjectColorSource.ColorFromParent&&parent.HasValue)return parent.Value;
      return attributes.LayerIndex>=0&&attributes.LayerIndex<doc.Layers.Count?doc.Layers[attributes.LayerIndex].Color:attributes.ObjectColor;
    }
    private static XElement P(Point3d point)=>new("P",new XAttribute("x",Number(point.X)),new XAttribute("y",Number(point.Y)));
    private static void SetToolAttributes(XElement entity,string process,int index)
    {entity.SetAttributeValue("tool",process);entity.SetAttributeValue("index",index);}
    private static XElement WithTool(XElement entity,Tool tool)
    {SetToolAttributes(entity,tool.Process,tool.Index);if(tool.SourceLayer!=null)entity.SetAttributeValue("sourceLayer",tool.SourceLayer);return entity;}
    private static void ValidateToolMappings(XElement root)
    {
      foreach(var entity in root.Elements("Table").Elements("Pattern").Descendants().Where(entity=>entity.Parent?.Name=="Pattern"
        && entity.Name!="EffectSet" || entity.Attribute("tool")!=null))
        if(!ToolProcesses.Contains((string?)entity.Attribute("tool"),StringComparer.Ordinal)||!int.TryParse((string?)entity.Attribute("index"),out int index)||index<0||index>MaximumToolIndex)
          throw new InvalidOperationException("Choose a PatternSmith tool for layer '"+((string?)entity.Attribute("sourceLayer")??"unknown")+"'.");
    }

    private sealed class ExportMappingDialog:Eto.Forms.Dialog<bool>
    {
      private sealed class MaterialMappingRow(string name,XElement[] tables,CatalogMaterial? material,bool needsReview,string previousMapping)
      {
        internal string Name {get;}=name;
        internal XElement[] Tables {get;}=tables;
        internal CatalogMaterial? Material {get;set;}=material;
        internal bool NeedsReview {get;set;}=needsReview;
        internal string PreviousMapping {get;set;}=previousMapping;
        internal string CustomId {get;set;}=string.Empty;
        internal Eto.Forms.GridItem Item {get;}=new();
      }
      private sealed record CatalogFolder(string Path);
      private sealed class ProjectMaterialChoice { }
      private readonly XElement _request;
      private readonly RhinoDoc? _document;
      private CatalogResult _catalog;
      private readonly JsonObject _saved;
      private readonly Dictionary<string,JsonNode?> _selectionPreferences=new(StringComparer.OrdinalIgnoreCase);
      private readonly List<MaterialMappingRow> _materials=[];
      private readonly Eto.Forms.GridView _materialGrid=new(){AllowMultipleSelection=false,AllowEmptySelection=false,ShowHeader=true};
      private readonly Eto.Forms.TreeGridView _catalogTree=new(){AllowMultipleSelection=false,AllowEmptySelection=true,ShowHeader=true};
      private Eto.Forms.TreeGridItem? _displayedCatalogChoice;
      private readonly Eto.Forms.SearchBox _materialSearch=new(){PlaceholderText="Search library"};
      private readonly Eto.Forms.Label _materialSummary=new(){Wrap=Eto.Forms.WrapMode.Word};
      private readonly Eto.Drawing.Icon _folderIcon=Eto.Drawing.SystemIcons.Get(Eto.Drawing.SystemIconType.CloseDirectory,Eto.Drawing.SystemIconSize.Small);
      private readonly Dictionary<CatalogMaterial,Eto.Forms.TreeGridItem> _materialNodes=[];
      private readonly Dictionary<string,Eto.Forms.TreeGridItem> _folderNodes=new(StringComparer.OrdinalIgnoreCase);
      private Eto.Forms.TreeGridItem _treeRoot=new(),_projectMaterialNode=new();
      private bool _syncingMaterials,_treeInitialized;
      private readonly JsonObject _customColors;
      private readonly JsonObject _materialBaselines=new();
      private readonly IReadOnlyDictionary<string,string> _customIds;
      private readonly PreparationControls _preparation;
      internal PreparationConfiguration Preparation=>_preparation.Values;
      private sealed class LayerToolMapping(string layer,XElement[] entities,ToolPicker picker,Eto.Forms.Control label)
      {
        internal readonly string Layer=layer;
        internal readonly XElement[] Entities=entities;
        internal readonly ToolPicker Picker=picker;
        internal readonly Eto.Forms.TableLayout View=LayerMapRow(label,picker.Choice,picker.Details);
        internal readonly Dictionary<string,string?> CutterSelections=new(StringComparer.OrdinalIgnoreCase);
        internal ConfiguredTool? Selected=>Picker.Selected;
      }
      private readonly List<LayerToolMapping> _tools=[];
      private readonly Dictionary<int,Eto.Drawing.Bitmap> _swatches=[];
      private readonly ToolConfiguration _configuredTools;
      private ConfiguredCutter? _activeCutter;
      private IReadOnlyList<ConfiguredTool> ActiveTools=>_activeCutter?.Tools??_configuredTools.Tools;
      private readonly Eto.Forms.DropDown _cutterPicker=new(){ToolTip="Show tools compatible with this configured cutter. This does not change PatternSmith's selected machine."};
      private readonly Eto.Forms.Label _emptyLayerMap=new(){Text="No layers to map."};
      private Eto.Forms.Control? _layerMapHeader;
      private readonly Eto.Forms.TabControl _mappingTabs=new();
      private readonly Eto.Forms.ComboBox _outputPath=new(){ReadOnly=false,AutoComplete=false,ToolTip="Destination .project file. Enter a filename, choose a successful recent export, or browse; source geometry is unchanged."};
      private readonly Eto.Forms.Button _browse=new(){Text="Browse..."};
      private readonly Eto.Forms.DropDown _targetVersion=new(){DataStore=new[]{"PatternSmith 10","PatternSmith 11"},
        ToolTip="Match editable table label sizes to the version you will use to edit the project."};
      private readonly Eto.Forms.Label _catalogSource=new();
      private readonly Eto.Forms.Button _reconnect=new(){Text="Reconnect",ToolTip="Reload the PatternSmith material library without closing this window. Failed connections leave existing choices unchanged."};
      private readonly System.Diagnostics.Stopwatch _catalogRetryClock=System.Diagnostics.Stopwatch.StartNew();
      private Task<CatalogResult>? _catalogTask;
      private CancellationTokenSource? _catalogCancel;
      private bool _catalogManualReconnect,_catalogReconnectNeeded;
      internal string OutputPath=>NormalizeProjectPath(_outputPath.Text);
      private PatternSmithTargetVersion TargetVersion=>_targetVersion.SelectedIndex==0?PatternSmithTargetVersion.PatternSmith10:PatternSmithTargetVersion.PatternSmith11;
      private readonly Eto.Forms.DropDown _curves=new(){DataStore=new[]{"NativeSpline","Bezier"}};
      private readonly Eto.Forms.NumericStepper _curveError=new(){MinValue=0,MaxValue=double.MaxValue,DecimalPlaces=CurveErrorDecimalPlaces,Increment=CurveErrorIncrementInches,Width=MappingWidthColumnWidth,
        ToolTip="PatternSmith Thin maximum error in inches. Zero disables thinning; Bezier output is not thinned."};
      private readonly Eto.Forms.Label _status=new();
      private readonly Eto.Forms.ProgressBar _progress=new(){MinValue=0,MaxValue=100};
      private readonly Eto.Forms.Button _export=new(){Text="Export"},_cancel=new(){Text="Cancel"};
      private readonly Eto.Forms.UITimer _progressTimer=new();
      private readonly System.Diagnostics.Stopwatch _exportClock=new();
      private readonly ExportTimeEstimate _timing=new();
      private CancellationTokenSource? _exportCancel;
      private string _stage="Ready to export";
      private string _statusMessage="Ready to export";
      private bool _busy;
      private Guid[] _unsupportedSources=[];
      private Dictionary<Guid,GeometryBase> _unsupportedGeometry=[];
      private PreviewDisplay.ObjectHighlighter? _unsupportedHighlighter;
      private readonly Eto.Forms.UITimer _conversionTimer=new();
      private readonly System.Diagnostics.Stopwatch _conversionChanged=new(),_conversionClock=new();
      private readonly List<Task> _conversionTasks=[];
      private readonly bool _persistentConversionCache;
      private readonly string _conversionCacheRoot;
      private CancellationTokenSource? _conversionCancel;
      private Task? _conversionTask;
      private string? _conversionFingerprint;
      private string _conversionStage="Preparing curves";
      private int _conversionGeneration,_conversionPercent;
      private bool _conversionPending,_preparingConversion,_conversionNotified,_conversionClosed,_waitingForConversion;
      private bool _conversionOwnsStatus=true;
      private XDocument? _preparedRequest;
      private string? _preparedFingerprint;
      private string? _preparedSourceFingerprint;
      private string? _nativeProject,_nativeProjectFingerprint;
      private sealed class NativeProjectJob(string fingerprint,string path,CancellationTokenSource cancellation)
      {
        internal readonly string Fingerprint=fingerprint,Path=path;
        internal readonly CancellationTokenSource Cancellation=cancellation;
        internal Task Work=Task.CompletedTask;
      }
      private NativeProjectJob? _nativeJob;
      private string? _nativeMappingFingerprint;
      private readonly System.Diagnostics.Stopwatch _nativeChanged=new();
      private bool _nativePending;
      private Exception? _preparationError;
      private readonly System.Diagnostics.Stopwatch _responsiveClock=System.Diagnostics.Stopwatch.StartNew();
      private readonly Eto.Forms.Panel _findings=new(){Visible=false};
      private readonly Eto.Forms.CheckBox _previewDelete=new(){Text="Preview Delete",Enabled=false,ToolTip="Toggle the geometry that would remain after Delete. Originals and the export decision are unchanged; turn Highlight off for an unobstructed result."};
      private readonly Eto.Forms.CheckBox _highlightFindings=new(){Text="Highlight",ToolTip="Show or hide cleanup finding markers, including markers over a deletion preview."};
      private vExportDXF.ExportCleanupFeedback? _cleanupReview;
      private bool _changingFindingDisplay;
      private readonly Eto.Forms.Label _findingCounts=new(),_findingNotice=new(){Wrap=Eto.Forms.WrapMode.Word};
      private readonly Eto.Forms.Button _deleteFindings=new(){Text="Delete",ToolTip="Remove findings from temporary export copies only. Originals remain unchanged."},_ignoreFindings=new(){Text="Ignore"};
      private readonly Eto.Forms.Button _proceedMixedFaces=new(){Text="Proceed",Visible=false,ToolTip="Export this table with its mixed cutting-side instructions. Do not mirror or move its parts."};
      private bool _mixedFacesApproved;
      private bool _waitingForFindings,_findingAnswered;
      private vCleanup.ExportCleanupAction? _findingDecision,_nextCleanupAction;
      private bool _progressTimerDisposed;
      internal Func<ExportMappingDialog,Result>? ExecuteExport {get;set;}
      internal Func<ExportMappingDialog,CancellationToken,Action<int,string>,XDocument>? PrepareConversion {get;set;}
      internal Func<string>? PreparationSourceFingerprint {get;set;}
      internal Func<CancellationToken,CatalogResult> ReadCatalog {get;set;}=LoadMaterialCatalog;
      internal CancellationToken ExportToken=>_exportCancel?.Token??CancellationToken.None;
      internal void SetTimingWorkload(XElement request)=>_timing.SetNativeWorkload(request);
      internal void ReportProgress(int percent,string stage)
      {
        _stage=stage;_progress.Value=Math.Clamp(percent,0,100);
        string Duration(TimeSpan value)=>(int)value.TotalMinutes+":"+value.Seconds.ToString("00",CultureInfo.InvariantCulture);
        _timing.Observe(percent,stage,_exportClock.Elapsed.TotalSeconds);
        SetStatusMessage(stage+Environment.NewLine+"Elapsed "+Duration(_exportClock.Elapsed)+" | Remaining "+_timing.Remaining(_exportClock.Elapsed.TotalSeconds));
      }
      private readonly bool _clipboard;
      internal ExportMappingDialog(XElement request,CatalogResult catalog,IReadOnlyDictionary<string,string>? customIds=null,string? outputPath=null,ToolConfiguration? configuredTools=null,RhinoDoc? doc=null,bool clipboard=false)
      {
        _clipboard=clipboard;
        _request=request;_catalog=catalog;_document=doc;_catalogReconnectNeeded=!catalog.Identity.StartsWith("server:",StringComparison.Ordinal);
        _persistentConversionCache=Config(nameof(CacheSplineConversions),CacheSplineConversions);
        _conversionCacheRoot=_persistentConversionCache
          ?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),RuntimeFolderName,BuilderFolderName,SplineCacheFolderName)
          :Path.Combine(Path.GetTempPath(),RuntimeFolderName,BuilderFolderName,"SplineSession",Guid.NewGuid().ToString("N"));
        _preparation=new(doc,_curves,_curveError);
        _catalogSource.Text="Materials: "+catalog.Source;_reconnect.Click+=(_,_)=>ReconnectCatalog();
        _configuredTools=configuredTools??ReadConfiguredTools();
        _activeCutter=SelectedCutter(_configuredTools);
        _saved=ToolsOptionStore.Read("vExportPatternSmith",section=>ToolsOptionStore.Clone(section) as JsonObject??new());
        _targetVersion.SelectedIndex=(int)ReadTargetVersion(_saved);
        _highlightFindings.Checked=ToolsOptionStore.TryGetBool(_saved,"highlight_findings",out bool showFindings)?showFindings:Config(nameof(DefaultHighlightFindings),DefaultHighlightFindings);
        _outputPath.DataStore=RecentOutputPaths(_saved);
        _outputPath.Text=outputPath??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),DefaultFileStem+".project");
        _browse.Click+=(_,_)=>BrowseDestination();
        _customIds=customIds??PatternSmithMaterialIdentity.Allocate(AssignedMaterialTables(request).Attributes("material").Select(attribute=>attribute.Value),
          catalog.ReservedIds.Concat(catalog.Materials.Select(material=>material.Id)),new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase));
        _customColors=ToolsOptionStore.Clone(_saved["custom_colors"]) as JsonObject??new();
        var globalMaterials=MaterialLibrary.LoadMaterialPresets().ToDictionary(material=>material.Name,StringComparer.OrdinalIgnoreCase);
        Title=clipboard?"Copy to PatternSmith":"PatternSmith Export";Padding=MappingSpacing;Resizable=true;
        if(clipboard)_export.Text="Copy";
        ClientSize=new Eto.Drawing.Size(Math.Max(400,Config(nameof(MappingDialogWidth),MappingDialogWidth)),Math.Max(280,Config(nameof(MappingDialogHeight),MappingDialogHeight)));
        _curves.SelectedIndex=ReadCurveMode()=="Bezier"?1:0;
        _curveError.Value=ReadCurveError();_curveError.Enabled=_curves.SelectedIndex==0;_curves.SelectedIndexChanged+=(_,_)=>_curveError.Enabled=_curves.SelectedIndex==0;
        _materialGrid.Height=Math.Max(1,Config(nameof(MappingOverviewHeight),MappingOverviewHeight));
        _materialGrid.Columns.Add(new Eto.Forms.GridColumn {HeaderText="Rhino material -> PS material",DataCell=new Eto.Forms.TextBoxCell(0),
          Width=Config(nameof(MappingNameColumnWidth),MappingNameColumnWidth)*2+Config(nameof(MappingGroupColumnWidth),MappingGroupColumnWidth),Resizable=true,Expand=true});
        _materialGrid.Columns.Add(new Eto.Forms.GridColumn {HeaderText="ID",DataCell=new Eto.Forms.TextBoxCell(1),Width=Config(nameof(MappingIdColumnWidth),MappingIdColumnWidth),Resizable=true});
        _materialGrid.ToolTip="Select a Rhino material to reveal its current library mapping. Choose a material leaf below to change that row.";
        foreach(var group in AssignedMaterialTables(request).GroupBy(table=>(string)table.Attribute("material")!,StringComparer.OrdinalIgnoreCase))
        {
          string name=group.Key;
          var presetLink=globalMaterials.GetValueOrDefault(name)?.PatternSmith;
          var saved=InitialMaterialPreference(_saved,name,presetLink);
          _materialBaselines[name]=MaterialLibrary.PatternSmithJson(presetLink);
          _selectionPreferences[name]=ToolsOptionStore.Clone(saved);
          var matched=MatchMaterial(name,saved,catalog);
          string previous=saved is JsonObject old?string.Join(" / ",new[]{old["group"]?.GetValue<string>(),old["name"]?.GetValue<string>(),old["id"]?.GetValue<string>()}.Where(value=>!string.IsNullOrEmpty(value))):saved?.ToString()??"";
          var row=new MaterialMappingRow(name,group.ToArray(),matched,MaterialNeedsReview(name,saved,matched,catalog),previous);
          row.CustomId=_customIds[name];
          row.Item.Tag=row;UpdateMappingValues(row);_materials.Add(row);
        }
        _materialGrid.DataStore=_materials.Select(row=>row.Item).ToArray();
        _catalogTree.Columns.Add(new Eto.Forms.GridColumn {HeaderText="Library folders / materials",DataCell=new Eto.Forms.ImageTextCell(0,1),Width=Config(nameof(MappingNameColumnWidth),MappingNameColumnWidth),Resizable=true,Expand=true});
        _catalogTree.Columns.Add(new Eto.Forms.GridColumn {HeaderText="Width (in)",DataCell=new Eto.Forms.TextBoxCell(2),Width=Config(nameof(MappingWidthColumnWidth),MappingWidthColumnWidth),Resizable=true});
        _catalogTree.Columns.Add(new Eto.Forms.GridColumn {HeaderText="ID",DataCell=new Eto.Forms.TextBoxCell(3),Width=Config(nameof(MappingIdColumnWidth),MappingIdColumnWidth),Resizable=true});
        _catalogTree.ToolTip="Folders preserve the library hierarchy. Selecting a material assigns it to the selected Rhino material; selecting a folder does not change the mapping.";
        MaterialLibrary.PatternSmithReference? Reference(CatalogMaterial? material)=>material==null?null:new(material.Id,material.Name,material.Group,_catalog.Identity);
        MaterialAppearance.Grid(_materialGrid,0,item=>item is Eto.Forms.GridItem {Tag:MaterialMappingRow row}?Reference(row.Material):null);
        MaterialAppearance.Grid(_catalogTree,0,item=>item is Eto.Forms.TreeGridItem {Tag:CatalogMaterial material}?Reference(material):null);
        _materialGrid.SelectionChanged+=(_,_)=>CurrentMaterialChanged();
        _catalogTree.SelectedItemChanged+=(_,_)=>CatalogMaterialChanged();
        _materialSearch.TextChanged+=(_,_)=>BuildCatalogTree();
        BuildCatalogTree();if(_materials.Count>0)_materialGrid.SelectedRow=0;CurrentMaterialChanged();
        Shown+=(_,_)=>CurrentMaterialChanged();
        var materials=new Eto.Forms.TableLayout {Padding=MappingSpacing,Spacing=new Eto.Drawing.Size(MappingSpacing,MappingSpacing),Rows={
          new Eto.Forms.TableRow(new Eto.Forms.TableCell(_materialGrid,true)),
          new Eto.Forms.TableRow(new Eto.Forms.TableCell(_materialSummary,true)),
          new Eto.Forms.TableRow(new Eto.Forms.TableCell(_materialSearch,true)),
          new Eto.Forms.TableRow(new Eto.Forms.TableCell(_catalogTree,true)){ScaleHeight=true}}};
        foreach(var group in MappedLayerGroups(request))
        {
          var (available,selected)=LayerChoices(group.Key,group.First());
          var picker=new ToolPicker(available,selected,Swatch);
          var color=uint.TryParse((string?)group.First().Attribute("layerColor"),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var argb)?Eto.Drawing.Color.FromArgb(unchecked((int)argb)):Eto.Drawing.Colors.Gray;
          var layerImage=Swatch(color);
          var layer=new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing/2,Width=MappingNameColumnWidth,Items={new Eto.Forms.ImageView {Image=layerImage},new Eto.Forms.Label {Text=group.Key,ToolTip=group.Key,Wrap=Eto.Forms.WrapMode.Word,Width=Math.Max(1,MappingNameColumnWidth-layerImage.Width-MappingSpacing/2)}}};
          _tools.Add(new LayerToolMapping(group.Key,group.ToArray(),picker,layer));
        }
        var tabs=_mappingTabs;
        tabs.Pages.Add(new Eto.Forms.TabPage {Text="Materials map",Content=materials});
        tabs.Pages.Add(new Eto.Forms.TabPage {Text="Layers map",Content=CreateLayerMapView()});
        tabs.Pages.Add(new Eto.Forms.TabPage {Text="Preparation",Content=_preparation});
        _preparation.RemoveGrainChanged+=(_,_)=>UpdateToolAvailability();UpdateToolAvailability();
        _status.Wrap=Eto.Forms.WrapMode.Word;SetStatusMessage(_stage);
        _status.Load+=(_,_)=>{if(_status.ControlObject is System.Windows.FrameworkElement native)native.MinHeight=Math.Max(1,Config(nameof(ProgressStatusHeight),ProgressStatusHeight));};
        _progressTimer.Interval=Math.Max(1,Config(nameof(ProgressTickMilliseconds),ProgressTickMilliseconds))/1000d;
        _progressTimer.Elapsed+=(_,_)=>{if(_busy)ReportProgress(_progress.Value,_stage);};
        _export.Click+=(_,_)=>ApplyMappings();_cancel.Click+=(_,_)=>{if(_busy){_exportCancel?.Cancel();_conversionCancel?.Cancel();_cancel.Enabled=false;ReportProgress(_progress.Value,"Cancelling export...");}else Close(false);};
        DefaultButton=_export;AbortButton=_cancel;
        _deleteFindings.Click+=(_,_)=>
        {
          if(_busy&&!_waitingForFindings)return;
          if(_waitingForFindings){_findingDecision=vCleanup.ExportCleanupAction.Delete;_findingAnswered=true;}
          else{_nextCleanupAction=vCleanup.ExportCleanupAction.Delete;_preparedRequest=null;_preparedFingerprint=null;_conversionFingerprint=null;CheckConversion(true);}
        };
        _ignoreFindings.Click+=(_,_)=>{_findingDecision=vCleanup.ExportCleanupAction.Ignore;_findingAnswered=true;if(!_waitingForFindings)_findings.Visible=false;};
        _previewDelete.CheckedChanged+=(_,_)=>UpdateFindingDisplay();_highlightFindings.CheckedChanged+=(_,_)=>UpdateFindingDisplay();
        _proceedMixedFaces.Click+=(_,_)=>_mixedFacesApproved=true;
        _findings.Content=new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,VerticalContentAlignment=Eto.Forms.VerticalAlignment.Center,Items={_previewDelete,_highlightFindings,_deleteFindings,_ignoreFindings}};
        Closing+=(_,e)=>{if(_busy){e.Cancel=true;_exportCancel?.Cancel();}};
        Closed+=(_,_)=>{if(_conversionClosed)return;_conversionClosed=true;SetCleanupFeedback(null);StopCatalogReconnect();ClearUnsupportedHighlights();StopConversion();_conversionTimer.Stop();_conversionTimer.Dispose();ReleaseConversionCache();ReleaseNativeProject();_progressTimer.Stop();_progressTimer.Dispose();_progressTimerDisposed=true;foreach(var row in _tools)row.Picker.Release();foreach(var swatch in _swatches.Values)swatch.Dispose();_swatches.Clear();};
        _conversionTimer.Interval=Math.Max(1,Config(nameof(ProgressTickMilliseconds),ProgressTickMilliseconds))/1000d;
        _conversionTimer.Elapsed+=(_,_)=>{CheckCatalogReconnect();CheckConversion();};
        Shown+=(_,_)=>{_conversionTimer.Start();if(PrepareConversion!=null)Eto.Forms.Application.Instance.AsyncInvoke(()=>CheckConversion(true));};
        ExecuteState=busy=>{foreach(var page in tabs.Pages)page.Content.Enabled=!busy;_outputPath.Enabled=_browse.Enabled=_targetVersion.Enabled=_findings.Enabled=!busy;_reconnect.Enabled=!busy&&_catalogTask==null;_curves.Enabled=!busy;_curveError.Enabled=!busy&&_curves.SelectedIndex==0;_export.Enabled=!busy;_cancel.Enabled=true;UpdateToolAvailability();};
        Content=new Eto.Forms.TableLayout {Spacing=new Eto.Drawing.Size(MappingSpacing,MappingSpacing),Rows={
          new Eto.Forms.TableRow(clipboard
            ?new Eto.Forms.TableLayout {Spacing=new Eto.Drawing.Size(MappingSpacing,0),Rows={new Eto.Forms.TableRow(new Eto.Forms.TableCell(_catalogSource,true),_reconnect)}}
            :new Eto.Forms.TableLayout {Spacing=new Eto.Drawing.Size(MappingSpacing,MappingSpacing),Rows={
              new Eto.Forms.TableRow(new Eto.Forms.Label {Text="File",VerticalAlignment=Eto.Forms.VerticalAlignment.Center},
                new Eto.Forms.TableCell(new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,VerticalContentAlignment=Eto.Forms.VerticalAlignment.Center,
                  Items={new Eto.Forms.StackLayoutItem(_outputPath,true),_browse}},true)),
              new Eto.Forms.TableRow(new Eto.Forms.Label {Text="Export to",VerticalAlignment=Eto.Forms.VerticalAlignment.Center},
                new Eto.Forms.TableCell(new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,VerticalContentAlignment=Eto.Forms.VerticalAlignment.Center,
                  Items={_targetVersion,new Eto.Forms.StackLayoutItem(_catalogSource,true),_reconnect}},true))}}),
          new Eto.Forms.TableRow(new Eto.Forms.TableCell(tabs,true)){ScaleHeight=true},
          new Eto.Forms.TableRow(_progress),
          new Eto.Forms.TableRow(new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,VerticalContentAlignment=Eto.Forms.VerticalAlignment.Center,
            Items={new Eto.Forms.StackLayoutItem(_status,true),_findings,_proceedMixedFaces,_export,_cancel}})}};
      }
      internal string ConversionFingerprint()
      {
        return PreparationFingerprint()+"|"+_curves.SelectedIndex+"|"+Number(_curveError.Value);
      }
      private string PreparationFingerprint()=>System.Text.Json.JsonSerializer.Serialize(Preparation);
      internal XDocument PreparedRequest()
      {
        if(_preparedRequest==null||_preparedFingerprint!=PreparationFingerprint())
          throw new InvalidOperationException("Export preparation is not ready: "+(_preparationError?.Message??"wait for preparation to finish"),_preparationError);
        var copy=new XDocument(_preparedRequest);ApplyConversionSettings(copy.Root!);return copy;
      }
      internal string PrepareNativeProject(XDocument request,Action<XDocument,string,CancellationToken,Action<int,string>>? build=null)
      {
        var copy=new XDocument(request);copy.Root!.SetAttributeValue("prepareSplinesOnly",null);
        string fingerprint=copy.ToString(SaveOptions.DisableFormatting);
        CompleteNativeProject();
        if(_nativeProjectFingerprint==fingerprint&&_nativeProject!=null&&File.Exists(_nativeProject))
        {ExportToken.ThrowIfCancellationRequested();ReportProgress(99,"Prepared export ready");return _nativeProject;}
        if(_nativeJob?.Fingerprint!=fingerprint)StartNativeProject(copy,build);
        try
        {
          while(_nativeJob is {Work.IsCompleted:false})
          {ExportToken.ThrowIfCancellationRequested();RhinoApp.Wait();Thread.Sleep(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds)));}
          ExportToken.ThrowIfCancellationRequested();CompleteNativeProject();
          if(_nativeProjectFingerprint!=fingerprint||_nativeProject==null||!File.Exists(_nativeProject))
            throw new IOException("The prepared export file was not created.");
          return _nativeProject;
        }
        catch{ReleaseNativeProject();throw;}
      }
      private string MappingFingerprint()=>System.Text.Json.JsonSerializer.Serialize(new
      {
        target=TargetVersion,
        materials=_materials.Select(row=>new {row.Name,row.CustomId,row.NeedsReview,catalogId=row.Material?.Id,catalogName=row.Material?.Name,settings=row.Material?.Settings.ToString(SaveOptions.DisableFormatting)}),
        cutter=_activeCutter?.Id,tools=_tools.Where(ToolIsRequired).Select(row=>new {row.Layer,process=row.Selected?.Process,index=row.Selected?.Index}),colors=_customColors.ToJsonString()
      });
      private void StartNativeProject(XDocument request,Action<XDocument,string,CancellationToken,Action<int,string>>? build=null)
      {
        ReleaseNativeProject();
        _nativeMappingFingerprint=MappingFingerprint();_nativePending=false;
        string directory=Path.Combine(Path.GetTempPath(),RuntimeFolderName,BuilderFolderName);Directory.CreateDirectory(directory);
        var copy=new XDocument(request);copy.Root!.SetAttributeValue("prepareSplinesOnly",null);
        var job=new NativeProjectJob(copy.ToString(SaveOptions.DisableFormatting),Path.Combine(directory,Guid.NewGuid().ToString("N")+(_clipboard?".xml":".project")),new CancellationTokenSource());
        _nativeJob=job;_conversionClock.Start();_conversionStage="Preparing native project";_conversionPercent=70;
        void Progress(int percent,string stage)=>Eto.Forms.Application.Instance.AsyncInvoke(()=>
        {
          if(_conversionClosed||IsDisposed||!ReferenceEquals(_nativeJob,job))return;
          _conversionPercent=70+percent*30/100;_conversionStage=stage;
          if(_busy)ReportProgress(25+percent*3/4,stage);
        });
        job.Work=Task.Run(()=>
        {
          (build??((input,path,token,progress)=>BuildNativeProject(input,path,token,progress,background:true)))(copy,job.Path,job.Cancellation.Token,Progress);
          job.Cancellation.Token.ThrowIfCancellationRequested();
          if(!File.Exists(job.Path))throw new IOException("The prepared export file was not created.");
        },job.Cancellation.Token);
        _conversionTasks.Add(job.Work);
      }
      private void CompleteNativeProject()
      {
        if(_nativeJob is not {Work.IsCompleted:true} job)return;
        _nativeJob=null;
        try
        {
          job.Work.GetAwaiter().GetResult();_nativeProject=job.Path;_nativeProjectFingerprint=job.Fingerprint;
          _conversionStage=_clipboard?"Ready to copy":"Export ready";_conversionPercent=100;_conversionClock.Stop();
        }
        catch{DeleteNativeProject(job.Path);throw;}
        finally{job.Cancellation.Dispose();}
      }
      private void CheckNativeProject(bool immediate)
      {
        if(_preparedRequest==null||_conversionPending||_conversionTask is not {IsCompleted:true,IsCanceled:false,IsFaulted:false})return;
        string mapping=MappingFingerprint();
        if(mapping!=_nativeMappingFingerprint)
        {ReleaseNativeProject();_nativeMappingFingerprint=mapping;_nativePending=true;_nativeChanged.Restart();if(!_busy)_conversionOwnsStatus=true;}
        if(_nativePending&&(immediate||_nativeChanged.ElapsedMilliseconds>=Math.Max(0,Config(nameof(ConversionRestartDelayMilliseconds),ConversionRestartDelayMilliseconds))))
        {
          _nativePending=false;
          try
          {
            if(_materials.FirstOrDefault(row=>row.NeedsReview) is {} review)
            {_conversionStage="Export blocked: "+MaterialReviewMessage(review);_conversionPercent=0;_conversionClock.Stop();return;}
            var request=PreparedRequest();ApplyResolvedMappings(request.Root!);ValidateToolMappings(request.Root!);StartNativeProject(request);
          }
          catch(InvalidOperationException){_conversionStage="Curves ready; review material and tool mappings";}
          catch(Exception error){_conversionStage="Export preparation failed: "+error.Message;_conversionClock.Stop();Log.Write(nameof(vExportPatternSmith),"background project start failed: {0}",error);}
        }
        try{CompleteNativeProject();}
        catch(Exception error){_conversionStage="Export preparation failed: "+error.Message;_conversionClock.Stop();Log.Write(nameof(vExportPatternSmith),"background project preparation failed: {0}",error);}
      }
      internal static void DeleteNativeProject(string path)
      {
        try{if(File.Exists(path))File.Delete(path);}
        catch(Exception error){Log.Write(nameof(vExportPatternSmith),"prepared project cleanup failed: {0}",error.Message);}
      }
      private void ReleaseNativeProject()
      {
        var job=_nativeJob;_nativeJob=null;_nativeMappingFingerprint=null;_nativePending=false;
        if(job!=null)
        {
          job.Cancellation.Cancel();
          _=job.Work.ContinueWith(task=>{_=task.Exception;DeleteNativeProject(job.Path);job.Cancellation.Dispose();},TaskScheduler.Default);
        }
        string? path=_nativeProject;_nativeProject=null;_nativeProjectFingerprint=null;
        if(path!=null)DeleteNativeProject(path);
      }
      internal vCleanup.ExportCleanupAction TakeCleanupAction(vCleanup.ExportCleanupAction fallback)
      {var action=_nextCleanupAction??fallback;_nextCleanupAction=null;return action;}
      internal bool ApproveMixedFaces(string table)
      {
        _mixedFacesApproved=false;_proceedMixedFaces.Visible=true;_proceedMixedFaces.Enabled=true;
        SetStatusMessage("Table '"+table+"' contains both face-up and face-down parts. Proceed to keep them together, or Cancel. Face-down parts remain identified individually; no geometry is mirrored.");
        try
        {
          while(!_mixedFacesApproved){KeepPreparationResponsive();if(_conversionClosed)return false;Thread.Sleep(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds)));}
          return true;
        }
        finally{if(!IsDisposed)_proceedMixedFaces.Visible=false;}
      }
      internal void KeepPreparationResponsive()
      {
        if(_responsiveClock.ElapsedMilliseconds<Math.Max(1,Config(nameof(ProgressTickMilliseconds),ProgressTickMilliseconds)))return;
        _responsiveClock.Restart();RhinoApp.Wait();
        _conversionCancel?.Token.ThrowIfCancellationRequested();
        if(_conversionClosed||_conversionFingerprint!=ConversionFingerprint())throw new OperationCanceledException("Export settings changed during preparation.");
      }
      internal void ShowFindings(int overlaps,int shorts)
      {
        if(overlaps<=0&&shorts<=0)SetCleanupFeedback(null);
        _findingCounts.Text=string.Join("; ",new[]{overlaps>0?CountText.Format(overlaps,"overlap"):null,shorts>0?CountText.Format(shorts,"short finding"):null}.Where(text=>text!=null));
        _findings.Visible=overlaps>0||shorts>0;
        if(!_findings.Visible){_findingNotice.Text=string.Empty;_deleteFindings.Enabled=true;}
        SetStatusMessage(_statusMessage);
      }
      internal void SetCleanupFeedback(vExportDXF.ExportCleanupFeedback? feedback)
      {
        if(!ReferenceEquals(_cleanupReview,feedback))
        {_cleanupReview?.SetDeletionPreview(false);_changingFindingDisplay=true;try{_previewDelete.Checked=feedback?.CanPreview==true&&Config(nameof(DefaultPreviewDelete),DefaultPreviewDelete);}finally{_changingFindingDisplay=false;}}
        _cleanupReview=feedback;_previewDelete.Enabled=feedback?.CanPreview==true;UpdateFindingDisplay();
      }
      private void UpdateFindingDisplay()
      {
        if(_changingFindingDisplay||IsDisposed)return;
        _cleanupReview?.SetHighlightsVisible(_highlightFindings.Checked==true);
        try{_cleanupReview?.SetDeletionPreview(_previewDelete.Checked==true);}
        catch(Exception error)
        {
          _changingFindingDisplay=true;try{_previewDelete.Checked=false;}finally{_changingFindingDisplay=false;}
          Log.Write(nameof(vExportPatternSmith),"deletion preview failed: {0}",error);
          SetStatusMessage("Deletion preview unavailable: "+error.Message);
        }
      }
      private string _ignoredObjectsMessage=string.Empty;
      internal void SetUnsupportedSources(IEnumerable<Guid> sources)
      {_unsupportedSources=sources.Distinct().ToArray();RefreshUnsupportedHighlights();}
      private void RefreshUnsupportedHighlights()
      {
        if(_document==null||_conversionClosed||IsDisposed)return;
        var next=new Dictionary<Guid,GeometryBase>();
        try
        {
          foreach(var id in _unsupportedSources)
            if(_document.Objects.FindId(id) is {IsDeleted:false} obj)CollectUnsupportedHighlights(_document,obj,Transform.Identity,0,next);
          int marker=Math.Max(1,Config(nameof(UnsupportedHighlightMarkerPixels),UnsupportedHighlightMarkerPixels));
          _unsupportedHighlighter??=new(_document,UnsupportedHighlightStyle with {PointSizePixels=marker})
            {DrawOnTop=true,MinimumCurveMarkerPixels=marker,MinimumObjectMarkerPixels=marker,
              ObjectLocatorPixels=Config(nameof(UnsupportedHighlightBounds),UnsupportedHighlightBounds)?marker:0};
          _unsupportedHighlighter.SetObjects(next.Keys,next);
          foreach(var geometry in _unsupportedGeometry.Values)geometry.Dispose();
          _unsupportedGeometry=next;
        }
        catch(Exception error)
        {_unsupportedHighlighter?.Dispose();_unsupportedHighlighter=null;foreach(var geometry in next.Values)geometry.Dispose();Log.Write(nameof(vExportPatternSmith),"unsupported object highlighting failed: {0}",error);}
      }
      internal void ClearUnsupportedHighlights()
      {
        _unsupportedHighlighter?.Dispose();_unsupportedHighlighter=null;
        foreach(var geometry in _unsupportedGeometry.Values)geometry.Dispose();
        _unsupportedGeometry.Clear();_unsupportedSources=[];
      }
      protected override void Dispose(bool disposing)
      {if(IsDisposed)return;if(disposing){SetCleanupFeedback(null);StopCatalogReconnect();ClearUnsupportedHighlights();}base.Dispose(disposing);}
      internal void ReportUnsupportedObjects(string message)
      {
        if(_ignoredObjectsMessage!=message&&!string.IsNullOrEmpty(message))
        {RhinoApp.WriteLine(nameof(vExportPatternSmith)+": "+message);Log.Write(nameof(vExportPatternSmith),message);}
        _ignoredObjectsMessage=message;SetStatusMessage(_statusMessage);RefreshUnsupportedHighlights();
      }
      private void SetStatusMessage(string text)
      {
        _statusMessage=text;string findings=_findings.Visible?_findingCounts.Text??string.Empty:string.Empty;
        _status.Text=string.Join(Environment.NewLine,new[]{text,_ignoredObjectsMessage,findings}.Where(line=>!string.IsNullOrEmpty(line)));
        _status.ToolTip=_status.Text+(string.IsNullOrEmpty(_findingNotice.Text)?string.Empty:Environment.NewLine+_findingNotice.Text);
      }
      internal void ApplyConversionSettings(XElement root)
      {
        root.SetAttributeValue("splineCacheDirectory",_conversionCacheRoot);
        foreach(var curve in root.Descendants("BezierGroup"))ApplySplineConversion(curve,_curves.SelectedIndex==0,_curveError.Value);
      }
      private void StopConversion()
      {
        ReleaseNativeProject();
        var cancellation=_conversionCancel;_conversionCancel=null;
        if(cancellation!=null)
        {
          cancellation.Cancel();var pending=_conversionTask;
          if(pending==null)cancellation.Dispose();
          else _=pending.ContinueWith(task=>{_ = task.Exception;cancellation.Dispose();},TaskScheduler.Default);
        }
        _conversionTask=null;
      }
      private void CheckConversion(bool immediate=false)
      {
        if(_conversionClosed||IsDisposed||PrepareConversion==null||_preparingConversion||_busy&&!immediate)return;
        string fingerprint=ConversionFingerprint();
        if(fingerprint!=_conversionFingerprint)
        {
          StopConversion();_conversionGeneration++;_conversionFingerprint=fingerprint;
          if(!_busy)_conversionOwnsStatus=true;
          _conversionPending=true;_conversionNotified=false;_conversionChanged.Restart();
        }
        if(_conversionPending&&(immediate||_conversionChanged.ElapsedMilliseconds>=Math.Max(0,Config(nameof(ConversionRestartDelayMilliseconds),ConversionRestartDelayMilliseconds))))
        {
          _conversionPending=false;
          _preparingConversion=true;_export.Enabled=false;_conversionClock.Restart();_conversionPercent=0;_conversionStage="Preparing curves";
          var cancel=new CancellationTokenSource();_conversionCancel=cancel;var token=cancel.Token;int generation=_conversionGeneration;
          void Progress(int percent,string stage)
          {
            Eto.Forms.Application.Instance.AsyncInvoke(()=>
            {
              if(_conversionClosed||IsDisposed||token.IsCancellationRequested||generation!=_conversionGeneration)return;
              _conversionPercent=percent*70/100;_conversionStage=stage;
              if(_waitingForConversion)ReportProgress(percent/4,stage);
            });
          }
          try
          {
            string preparation=PreparationFingerprint();
            string? source=PreparationSourceFingerprint?.Invoke();
            if(_preparedRequest==null||_preparedFingerprint!=preparation||source!=_preparedSourceFingerprint)
            {
              _preparedRequest=null;_preparationError=null;ShowFindings(0,0);
              var prepared=PrepareConversion(this,token,(percent,stage)=>{SetStatusMessage(stage);KeepPreparationResponsive();token.ThrowIfCancellationRequested();});
              token.ThrowIfCancellationRequested();
              if(preparation!=PreparationFingerprint())throw new OperationCanceledException("Export settings changed during preparation.");
              if(source!=PreparationSourceFingerprint?.Invoke())throw new OperationCanceledException("Export sources changed during preparation.");
              _preparedRequest=new XDocument(prepared);_preparedFingerprint=preparation;_preparedSourceFingerprint=source;
            }
            var request=PreparedRequest();
            _conversionTask=request.Descendants("BezierGroup").Any(curve=>(bool?)curve.Attribute("nativeSpline")==true)
              ?Task.Run(()=>BuildNativeProject(request,Path.Combine(Path.GetTempPath(),DefaultFileStem+".project"),token,Progress,true),token):Task.CompletedTask;
            _conversionTasks.RemoveAll(task=>{if(!task.IsCompleted)return false;_=task.Exception;return true;});
            _conversionTasks.Add(_conversionTask);
          }
          catch(Exception ex){_preparationError=ex;Log.Write(nameof(vExportPatternSmith),"background preparation deferred: {0}",ex.Message);_conversionTask=Task.FromException(ex);}
          finally{_preparingConversion=false;if(!IsDisposed)_export.Enabled=!_busy;}
        }
        if(_conversionTask is {IsCompleted:true} finished&&!_conversionNotified)
        {
          _conversionNotified=true;_conversionClock.Stop();
          try{finished.GetAwaiter().GetResult();_conversionStage="Preparation ready";_conversionPercent=100;}
          catch(Exception ex){_conversionStage="Curves will be checked at export";Log.Write(nameof(vExportPatternSmith),"background conversion deferred: {0}",ex.Message);}
        }
        CheckNativeProject(immediate);
        if(_conversionTask!=null&&!_busy&&_conversionOwnsStatus)
        {_progress.Value=Math.Clamp(_conversionPercent,0,100);SetStatusMessage(_conversionStage+" | Elapsed "+((int)_conversionClock.Elapsed.TotalMinutes)+":"+_conversionClock.Elapsed.Seconds.ToString("00",CultureInfo.InvariantCulture));}
      }
      private void WaitForConversion()
      {
        if(_preparedRequest==null&&_preparationError is OperationCanceledException)
        {_conversionFingerprint=null;_preparationError=null;}
        if(_preparedRequest!=null&&_preparedSourceFingerprint!=PreparationSourceFingerprint?.Invoke())
        {_preparedRequest=null;_preparedFingerprint=null;_conversionFingerprint=null;}
        CheckConversion(true);_waitingForConversion=true;
        try
        {
          while(_conversionTask is {IsCompleted:false}){ExportToken.ThrowIfCancellationRequested();RhinoApp.Wait();Thread.Sleep(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds)));}
          if(_conversionTask!=null)try{_conversionTask.GetAwaiter().GetResult();}catch(Exception ex){Log.Write(nameof(vExportPatternSmith),"conversion will be completed during export: {0}",ex.Message);}
          ExportToken.ThrowIfCancellationRequested();
        }
        finally{_waitingForConversion=false;}
      }
      private void ReleaseConversionCache()
      {
        if(_persistentConversionCache)return;
        _=Task.WhenAll(_conversionTasks).ContinueWith(task=>
        {
          _=task.Exception;
          try
          {
            string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),RuntimeFolderName,BuilderFolderName,"SplineSession"))+Path.DirectorySeparatorChar;
            string cache=Path.GetFullPath(_conversionCacheRoot);
            if(cache.StartsWith(root,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(cache))Directory.Delete(cache,true);
          }
          catch(Exception ex){Log.Write(nameof(vExportPatternSmith),"session conversion cache cleanup deferred: {0}",ex.Message);}
        },TaskScheduler.Default);
      }
      private Action<bool>? ExecuteState;
      internal static CatalogMaterial? MatchMaterial(string name,JsonNode? saved,CatalogResult catalog)
      {
        if(saved is JsonObject mapping&&PatternSmithCatalog.SameIdentity(mapping["catalog"]?.GetValue<string>()??"",catalog.Identity))
          return catalog.Materials.FirstOrDefault(item=>item.Id==mapping["id"]?.GetValue<string>()&&item.Name==mapping["name"]?.GetValue<string>()&&
            item.Group.Equals(mapping["group"]?.GetValue<string>()??"",StringComparison.OrdinalIgnoreCase));
        if(saved!=null)return null;
        var exact=catalog.Materials.Where(item=>item.Name.Trim().Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)).ToArray();
        var preferred=exact.Where(item=>PreferredGroup(item.Group)).ToArray();if(preferred.Length==1)exact=preferred;
        return exact.Length==1?exact[0]:null;
      }
      internal static bool MaterialNeedsReview(string name,JsonNode? saved,CatalogMaterial? matched,CatalogResult catalog)
      {
        if(matched!=null||saved==null||saved is JsonValue value&&value.TryGetValue<string>(out var text)&&text.Length==0)return false;
        if(!catalog.Available||saved is not JsonObject mapping||mapping["id"] is not JsonValue idValue||
          !idValue.TryGetValue<string>(out var id)||string.IsNullOrWhiteSpace(id))return true;
        string previousName=mapping["name"] is JsonValue previous&&previous.TryGetValue<string>(out var previousText)?previousText:"";
        // An absent material becomes Custom; an offline catalog or an identity conflict does not prove absence.
        return catalog.ReservedIds.Contains(id,StringComparer.OrdinalIgnoreCase)||catalog.Materials.Any(material=>
          material.Id.Equals(id,StringComparison.OrdinalIgnoreCase)||material.Name.Trim().Equals(name.Trim(),StringComparison.OrdinalIgnoreCase)||
          previousName.Length>0&&material.Name.Trim().Equals(previousName.Trim(),StringComparison.OrdinalIgnoreCase));
      }
      private JsonNode MappingPreference(MaterialMappingRow row)=>row.Material is {} material
        ?new JsonObject {["id"]=material.Id,["name"]=material.Name,["group"]=material.Group,["catalog"]=_catalog.Identity}
        :JsonValue.Create("")!;
      internal void ReconnectCatalog()
        =>CheckCatalogReconnect(force:true);
      private void CheckCatalogReconnect(bool force=false)
      {
        if(_conversionClosed||IsDisposed||_busy||_preparingConversion)return;
        if(_catalogTask is {} pending)
        {
          if(!pending.IsCompleted)return;
          bool manual=_catalogManualReconnect;_catalogTask=null;
          try
          {
            var catalog=pending.GetAwaiter().GetResult();_catalogCancel!.Token.ThrowIfCancellationRequested();
            if(!catalog.Available)throw new IOException("The PS library is unavailable; existing material choices were retained.");
            _catalogReconnectNeeded=!catalog.Identity.StartsWith("server:",StringComparison.Ordinal);
            if(manual||!_catalog.Available||catalog.Identity.StartsWith("server:",StringComparison.Ordinal))ApplyCatalog(catalog);
            _catalogSource.ToolTip=catalog.Identity.StartsWith("server:",StringComparison.Ordinal)?"Connected to the running material-library server.":"Using the read-only library fallback; the server will be retried in the background.";
            if(manual){_conversionOwnsStatus=false;SetStatusMessage("PS library reconnected: "+CountText.Format(catalog.Materials.Count,"material")+".");}
          }
          catch(OperationCanceledException){_catalogReconnectNeeded=true;if(manual)SetStatusMessage("Library reconnection cancelled; existing choices were retained.");}
          catch(Exception ex)
          {
            _catalogReconnectNeeded=true;
            Log.Write(nameof(vExportPatternSmith),"material library reconnection failed: {0}",ex.Message);
            _catalogSource.ToolTip=ex.Message+" Automatic retries do not block this window.";
            if(manual){_conversionOwnsStatus=false;SetStatusMessage("Library reconnection failed: "+ex.Message);}
          }
          finally
          {_catalogCancel?.Dispose();_catalogCancel=null;_catalogRetryClock.Restart();_reconnect.Enabled=true;_catalogSource.Text="Materials: "+_catalog.Source;}
          return;
        }
        int interval=Math.Max(0,Config(nameof(CatalogReconnectIntervalMilliseconds),CatalogReconnectIntervalMilliseconds));
        if(!force&&(interval==0||!_catalogReconnectNeeded||_catalogRetryClock.ElapsedMilliseconds<interval))return;
        _catalogManualReconnect=force;_catalogCancel=new();var token=_catalogCancel.Token;var read=ReadCatalog;
        _reconnect.Enabled=false;_catalogSource.Text="Materials: "+_catalog.Source+" (reconnecting...)";
        _catalogTask=Task.Run(()=>read(token),token);
      }
      private void StopCatalogReconnect()
      {
        var pending=_catalogTask;var cancellation=_catalogCancel;_catalogTask=null;_catalogCancel=null;
        if(cancellation==null)return;
        cancellation.Cancel();
        if(pending==null)cancellation.Dispose();
        else _=pending.ContinueWith(task=>{_=task.Exception;cancellation.Dispose();},TaskScheduler.Default);
      }
      private void ApplyCatalog(CatalogResult catalog)
      {
          foreach(var row in _materials)if(row.Material!=null)_selectionPreferences[row.Name]=MappingPreference(row);
          var ownIds=catalog.Materials.Where(material=>_materials.Any(row=>row.Name.Equals(material.Name,StringComparison.OrdinalIgnoreCase))).Select(material=>material.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
          var customIds=PatternSmithMaterialIdentity.Allocate(_materials.Select(row=>row.Name),catalog.ReservedIds.Except(ownIds,StringComparer.OrdinalIgnoreCase),_materials.ToDictionary(row=>row.Name,row=>row.CustomId,StringComparer.OrdinalIgnoreCase));
          if(_catalog.Materials.Count==0)_treeInitialized=false;
          _catalog=catalog;_catalogSource.Text="Materials: "+catalog.Source;
          foreach(var row in _materials)
          {
            var preference=_selectionPreferences.GetValueOrDefault(row.Name);row.Material=MatchMaterial(row.Name,preference,catalog);
            row.NeedsReview=MaterialNeedsReview(row.Name,preference,row.Material,catalog);row.CustomId=customIds[row.Name];
            if(preference is JsonObject old)row.PreviousMapping=string.Join(" / ",new[]{old["group"]?.GetValue<string>(),old["name"]?.GetValue<string>(),old["id"]?.GetValue<string>()}.Where(value=>!string.IsNullOrEmpty(value)));
            UpdateMappingValues(row);
          }
          for(int index=0;index<_materials.Count;index++)_materialGrid.ReloadData(index);
          BuildCatalogTree();CurrentMaterialChanged();
          Log.Write(nameof(vExportPatternSmith),"material library reconnected source={0} materials={1}",catalog.Source,catalog.Materials.Count);
      }
      private Eto.Drawing.Bitmap Swatch(Eto.Drawing.Color color)
      {int key=color.ToArgb();if(!_swatches.TryGetValue(key,out var image))_swatches[key]=image=LayerSelector.CreateColorSwatch(color);return image;}
      private bool ToolIsRequired(LayerToolMapping row)=>
        !Preparation.Export.RemoveGrain||!row.Entities.All(entity=>(bool?)entity.Attribute("grainOnly")==true);
      private (IReadOnlyList<ConfiguredTool> Tools,ConfiguredTool? Selected) LayerChoices(string layer,XElement entity)=>
        LayerChoices(_saved,_configuredTools,ActiveTools,layer,entity);
      internal static (IReadOnlyList<ConfiguredTool> Tools,ConfiguredTool? Selected) LayerChoices(JsonObject mappings,
        ToolConfiguration configured,IReadOnlyList<ConfiguredTool> available,string layer,XElement entity)
      {
        var saved=(mappings["layers"] as JsonObject)?[layer] as JsonObject;
        string process=saved?["process"]?.GetValue<string>()??(string)entity.Attribute("tool")!;
        int index=saved?["index"]?.GetValue<int>()??(int)entity.Attribute("index")!;
        string? id=saved?["tool_id"]?.GetValue<string>();
        if(configured.Tools.Count==0)
        {
          var defaults=ToolProcesses.Select(role=>new ConfiguredTool("",ToolProcessName(role),role,PrimaryToolIndex)).ToList();
          if(ToolProcesses.Contains(process,StringComparer.Ordinal)&&index>PrimaryToolIndex&&index<=MaximumToolIndex)
            defaults.Add(new("",ToolProcessName(process)+" "+(index+1).ToString(CultureInfo.InvariantCulture),process,index));
          available=defaults;
        }
        else if(!string.IsNullOrEmpty(id))
          return (available,available.FirstOrDefault(tool=>tool.Id.Equals(id,StringComparison.OrdinalIgnoreCase)));
        return (available,available.FirstOrDefault(tool=>tool.Process==process&&tool.Index==index));
      }
      private void SelectCutter()
      {
        int index=_cutterPicker.SelectedIndex;
        if(_busy||index<0||index>=_configuredTools.Cutters.Count)return;
        var cutter=_configuredTools.Cutters[index];var previous=_activeCutter;
        if(cutter==previous)return;
        _activeCutter=cutter;
        foreach(var row in _tools)
        {
          var current=row.Selected;
          if(previous!=null)row.CutterSelections[previous.Id]=current?.Key;
          var (available,initial)=LayerChoices(row.Layer,row.Entities[0]);
          var selected=row.CutterSelections.TryGetValue(cutter.Id,out var saved)
            ?available.FirstOrDefault(tool=>tool.Key==saved)
            :current!=null?available.FirstOrDefault(tool=>tool.Key==current.Key):initial;
          row.Picker.SetTools(available,selected,Swatch);
        }
        UpdateToolAvailability();
      }
      private static Eto.Forms.TableLayout LayerMapRow(Eto.Forms.Control layer,Eto.Forms.Control picker,Eto.Forms.Control? details=null)=>
        new() {Spacing=new Eto.Drawing.Size(MappingSpacing,0),Padding=new Eto.Drawing.Padding(0,0,0,MappingSpacing),
          Rows={new Eto.Forms.TableRow(layer,picker,new Eto.Forms.TableCell(details,true))}};
      private Eto.Forms.Control CreateLayerMapView()
      {
        var rows=new Eto.Forms.StackLayout {HorizontalContentAlignment=Eto.Forms.HorizontalAlignment.Stretch};
        _layerMapHeader=LayerMapRow(new Eto.Forms.Label {Text="Rhino layer",Width=MappingNameColumnWidth},
          new Eto.Forms.Label {Text="PatternSmith tool",Width=Config(nameof(MappingNameColumnWidth),MappingNameColumnWidth)});
        rows.Items.Add(_layerMapHeader);
        foreach(var row in _tools.OrderBy(row=>row.Layer,StringComparer.CurrentCultureIgnoreCase))rows.Items.Add(row.View);
        rows.Items.Add(_emptyLayerMap);
        var content=new Eto.Forms.StackLayout {Spacing=MappingSpacing,Padding=MappingSpacing,
          HorizontalContentAlignment=Eto.Forms.HorizontalAlignment.Stretch};
        if(_configuredTools.Warning.Length>0)content.Items.Add(new Eto.Forms.Label {Text=_configuredTools.Warning,Wrap=Eto.Forms.WrapMode.Word});
        if(_configuredTools.Cutters.Count>1)
        {
          _cutterPicker.DataStore=_configuredTools.Cutters.Select(cutter=>cutter.Name).ToArray();
          _cutterPicker.SelectedIndex=Array.FindIndex(_configuredTools.Cutters.ToArray(),cutter=>cutter==_activeCutter);
          _cutterPicker.SelectedIndexChanged+=(_,_)=>SelectCutter();
          content.Items.Add(new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,
            VerticalContentAlignment=Eto.Forms.VerticalAlignment.Center,Items={new Eto.Forms.Label {Text="Cutter"},_cutterPicker}});
        }
        else if(_configuredTools.Plotter.Length>0)content.Items.Add(new Eto.Forms.Label {Text="Cutter: "+_configuredTools.Plotter});
        content.Items.Add(rows);
        Log.Write(nameof(vExportPatternSmith),"layer map rows={0} active={1}",_tools.Count,_tools.Count(ToolIsRequired));
        return new Eto.Forms.Scrollable {Content=content,ExpandContentWidth=true};
      }
      private void UpdateToolAvailability()
      {
        bool any=false;
        // Collapse the whole row without reparenting its native picker.
        foreach(var row in _tools)
        {
          bool required=ToolIsRequired(row);
          row.Picker.Choice.Enabled=!_busy&&required;
          row.View.Visible=required;any|=required;
        }
        if(_layerMapHeader!=null)_layerMapHeader.Visible=any;
        _emptyLayerMap.Visible=!any;_cutterPicker.Enabled=!_busy;
      }
      private static void UpdateMappingValues(MaterialMappingRow row)
      {
        string target=row.NeedsReview?"Review saved mapping":row.Material is {} material
          ?(material.Group.Length>0?material.Group.Replace('\\','/').TrimEnd('/')+"/":"")+material.Name:"Custom (this project)";
        row.Item.Values=[row.Name+" -> "+target,row.Material?.Id??(row.NeedsReview?"":row.CustomId)];
      }
      private MaterialMappingRow? CurrentMaterial=>_materialGrid.SelectedItem is Eto.Forms.GridItem {Tag:MaterialMappingRow row}?row:null;
      private void BuildCatalogTree()
      {
        var expanded=_folderNodes.Where(pair=>pair.Value.Expanded).Select(pair=>pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string filter=_materialSearch.Text?.Trim()??"";
        bool Matches(string text)=>text.Contains(filter,StringComparison.OrdinalIgnoreCase);
        _syncingMaterials=true;
        try
        {
          _treeRoot=new();_materialNodes.Clear();_folderNodes.Clear();
          _projectMaterialNode=new Eto.Forms.TreeGridItem([null!,"Custom (project material)","",""]){Tag=new ProjectMaterialChoice()};
          _treeRoot.Children.Add(_projectMaterialNode);
          Eto.Forms.TreeGridItem Folder(string path)
          {
            var parent=_treeRoot;string key="";
            foreach(string name in path.Split('\\',StringSplitOptions.RemoveEmptyEntries))
            {
              key=key.Length==0?name:key+"\\"+name;
              if(!_folderNodes.TryGetValue(key,out var node))
              {
                node=new Eto.Forms.TreeGridItem([_folderIcon,name,"",""]){Tag=new CatalogFolder(key),
                  Expanded=filter.Length>0||(_treeInitialized?expanded.Contains(key):PreferredFolder(key))};
                _folderNodes.Add(key,node);parent.Children.Add(node);
              }
              parent=node;
            }
            return parent;
          }
          foreach(string path in _catalog.Groups.Concat(_catalog.Materials.Select(material=>material.Group)).Distinct(StringComparer.OrdinalIgnoreCase))
            if(filter.Length==0||Matches(path))Folder(path);
          foreach(var material in _catalog.Materials.Where(material=>filter.Length==0||Matches(material.Name)||Matches(material.Id)||Matches(material.Group)))
          {
            var node=new Eto.Forms.TreeGridItem([null!,material.Name,material.Width?.ToString("0.###",CultureInfo.CurrentCulture)??"",material.Id]){Tag=material};
            Folder(material.Group).Children.Add(node);_materialNodes.Add(material,node);
          }
          foreach(var node in _folderNodes.Values.Append(_treeRoot))
          {
            var children=node.Children.Cast<Eto.Forms.TreeGridItem>().OrderBy(child=>child.Tag is ProjectMaterialChoice?0:child.Tag is CatalogFolder?1:2)
              .ThenByDescending(child=>child.Tag is CatalogFolder folder&&PreferredFolder(folder.Path))
              .ThenBy(child=>child.GetValue(1)?.ToString(),StringComparer.CurrentCultureIgnoreCase).ThenBy(child=>child.GetValue(3)?.ToString(),StringComparer.Ordinal).ToArray();
            node.Children.Clear();foreach(var child in children)node.Children.Add(child);
          }
          _catalogTree.DataStore=_treeRoot;_treeInitialized=true;
        }
        finally{_syncingMaterials=false;}
        CurrentMaterialChanged();
      }
      private static bool PreferredFolder(string path)
      {
        string preferred=Config(nameof(PreferredMaterialGroup),PreferredMaterialGroup).Trim().Trim('\\');
        return PreferredGroup(path)||preferred.StartsWith(path+"\\",StringComparison.OrdinalIgnoreCase);
      }
      private void CurrentMaterialChanged()
      {
        if(_syncingMaterials)return;
        _syncingMaterials=true;
        try
        {
          var row=CurrentMaterial;_catalogTree.Enabled=row!=null;
          _materialSummary.Text=row==null?"":row.Name+" -> "+(row.NeedsReview?"Review previous mapping: "+row.PreviousMapping:row.Material is {} material?
            material.Name+" ["+material.Id+"]"+(material.Group.Length>0?" / "+material.Group:""):"Custom material in this project");
          _materialSummary.ToolTip=_materialSummary.Text;
          Eto.Forms.TreeGridItem? selected=row==null||row.NeedsReview?null:row.Material==null?_projectMaterialNode:_materialNodes.GetValueOrDefault(row.Material);
          if(selected!=null)
          {
            bool changed=false;
            for(var parent=selected.Parent as Eto.Forms.TreeGridItem;parent!=null;parent=parent.Parent as Eto.Forms.TreeGridItem)
              if(!parent.Expanded){parent.Expanded=true;changed=true;}
            if(changed)_catalogTree.ReloadData();
          }
          _displayedCatalogChoice=selected;
          if(!ReferenceEquals(_catalogTree.SelectedItem,selected))_catalogTree.SelectedItem=selected;
          if(selected!=null&&Visible&&_mappingTabs.SelectedIndex==0)
          {
            var pending=new Stack<Eto.Forms.TreeGridItem>(_treeRoot.Children.Cast<Eto.Forms.TreeGridItem>().Reverse());int index=0;
            while(pending.TryPop(out var node))
            {if(ReferenceEquals(node,selected)){_catalogTree.ScrollToRow(index);break;}index++;if(node.Expanded)foreach(var child in node.Children.Cast<Eto.Forms.TreeGridItem>().Reverse())pending.Push(child);}
          }
        }
        finally{_syncingMaterials=false;}
      }
      private void CatalogMaterialChanged()
      {
        if(_syncingMaterials||CurrentMaterial is not {} row||_catalogTree.SelectedItem is not Eto.Forms.TreeGridItem node||ReferenceEquals(node,_displayedCatalogChoice))return;
        if(node.Tag is not (CatalogMaterial or ProjectMaterialChoice))return;
        row.Material=node.Tag as CatalogMaterial;row.NeedsReview=false;UpdateMappingValues(row);
        _selectionPreferences[row.Name]=MappingPreference(row);
        _materialGrid.ReloadData(_materials.IndexOf(row));CurrentMaterialChanged();
      }
      private string MaterialReviewMessage(MaterialMappingRow row)
      {
        string previous=string.IsNullOrWhiteSpace(row.PreviousMapping)?"saved PS mapping":"saved PS mapping '"+row.PreviousMapping+"'";
        return "Review material '"+row.Name+"'. "+(!_catalog.Available
          ?"The PS library is unavailable, so the "+previous+" cannot be verified. Click Reconnect, or select Custom (project material)."
          :"The "+previous+" cannot be verified in this library. Choose a material below, or select Custom (project material).");
      }
      private void ShowExportBlock(string message)
      {
        _conversionOwnsStatus=false;_progress.Indeterminate=false;_progress.Value=0;
        SetStatusMessage("Export blocked: "+message);_export.ToolTip=_status.Text;
        Log.Write(nameof(vExportPatternSmith),"export blocked: {0}",message);
      }
      private void ApplyMappings()
      {
        if(_busy)return;
        _conversionOwnsStatus=false;
        if(!_clipboard)
        {
          try{_outputPath.Text=OutputPath;}catch(Exception ex) when(ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
          {ShowExportBlock(ex.Message);if(Visible)_outputPath.Focus();return;}
        }
        if(_materials.FirstOrDefault(row=>row.NeedsReview) is {} review)
        {
          _mappingTabs.SelectedIndex=0;_materialGrid.SelectedRow=_materials.IndexOf(review);CurrentMaterialChanged();
          if(Visible){_materialGrid.ScrollToRow(_materials.IndexOf(review));_materialGrid.Focus();}ShowExportBlock(MaterialReviewMessage(review));return;
        }
        var missingTool=_tools.FirstOrDefault(tool=>ToolIsRequired(tool)&&tool.Selected==null);
        if(missingTool!=null)
        {
          _mappingTabs.SelectedIndex=1;if(Visible)missingTool.Picker.Choice.Focus();
          ShowExportBlock("Choose a tool for exported layer '"+missingTool.Layer+"' in Layers map.");return;
        }
        _export.ToolTip=null;
        var materialIds=new JsonObject();var layers=new JsonObject();
        foreach(var row in _materials)
        {
          CatalogMaterial? material=row.Material;
          materialIds[row.Name]=material==null?JsonValue.Create(""):new JsonObject { ["id"]=material.Id,["name"]=material.Name,["group"]=material.Group,["catalog"]=_catalog.Identity };
          foreach(var table in row.Tables)ApplyMaterialMapping(table,row);
        }
        foreach(var row in _tools.Where(ToolIsRequired))
        {
          var selected=row.Selected!;string process=selected.Process;int index=selected.Index;
          layers[row.Layer]=new JsonObject { ["process"]=process,["index"]=index };
          if(selected.Id.Length>0)
          {((JsonObject)layers[row.Layer]!)["tool_id"]=selected.Id;((JsonObject)layers[row.Layer]!)["tool_name"]=selected.Name;}
          foreach(var entity in row.Entities)SetToolAttributes(entity,process,index);
        }
        string mode=_curves.SelectedIndex==1?"Bezier":"NativeSpline";
        ApplyTargetVersion(_request);
        foreach(var curve in _request.Descendants("BezierGroup"))ApplySplineConversion(curve,mode=="NativeSpline",_curveError.Value);
        if(!ToolsOptionStore.Update("vExportPatternSmith",section=>
        {
          if(section["materials"] is not JsonObject savedMaterials)section["materials"]=savedMaterials=new();
          foreach(var row in _materials)savedMaterials[row.Name]=ToolsOptionStore.Clone(materialIds[row.Name]);
          if(section["material_baselines"] is not JsonObject baselines)section["material_baselines"]=baselines=new();
          foreach(var pair in _materialBaselines)baselines[pair.Key]=ToolsOptionStore.Clone(pair.Value);
          if(section["layers"] is not JsonObject savedLayers)section["layers"]=savedLayers=new();
          foreach(var pair in layers)savedLayers[pair.Key]=ToolsOptionStore.Clone(pair.Value);
          if(section["custom_colors"] is not JsonObject colors)section["custom_colors"]=colors=new();
          foreach(var pair in _customColors)colors[pair.Key]=ToolsOptionStore.Clone(pair.Value);
          section["curve_mode"]=mode;
          section["curve_error"]=_curveError.Value;
          if(!_clipboard)section[TargetVersionKey]=TargetVersion.ToString();
          section["highlight_findings"]=_highlightFindings.Checked==true;
          _preparation.Save(section);
        })){ShowExportBlock("Could not save mappings: "+ToolsOptionStore.LastError);return;}
        if(ExecuteExport==null){Close(true);return;}
        using var cancellation=new CancellationTokenSource();_exportCancel=cancellation;_busy=true;ExecuteState?.Invoke(true);_exportClock.Restart();
        _timing.Reset((int?)_request.Attribute("sourceCount")??_request.Elements("Table").Elements("Pattern").Count(),Preparation.Export.Optimize);_progressTimer.Start();
        try
        {
          ReportProgress(0,"Preparing export");
          WaitForConversion();
          if(ExecuteExport(this)==Rhino.Commands.Result.Success){_busy=false;ReportProgress(100,_clipboard?"Copied":"Export complete");Close(true);}
          else ReportProgress(0,"Export cancelled; no destination was replaced");
        }
        catch(OperationCanceledException){ReportProgress(0,"Export cancelled; no destination was replaced");}
        catch(Exception ex){Log.Write(nameof(vExportPatternSmith),"export failed: {0}",ex);ReportProgress(0,"Export failed: "+ex.Message);}
        finally{if(!_progressTimerDisposed)_progressTimer.Stop();_exportClock.Stop();_busy=false;_exportCancel=null;if(!IsDisposed)ExecuteState?.Invoke(false);}
      }
      internal void ApplyResolvedMappings(XElement root)
      {
        foreach(var table in AssignedMaterialTables(root))
        {
          string name=(string)table.Attribute("material")!;
          var row=_materials.SingleOrDefault(item=>item.Name.Equals(name,StringComparison.OrdinalIgnoreCase))
            ??throw new InvalidOperationException("Review the material mapping for '"+name+"'.");
          ApplyMaterialMapping(table,row);
        }
        ApplyLayerTools(root,Preparation.Export.NotchSplitLayer,(layer,_)=>_tools.FirstOrDefault(tool=>tool.Layer.Equals(layer,StringComparison.OrdinalIgnoreCase))?.Selected);
        ApplyConversionSettings(root);
        ApplyTargetVersion(root);
        root.Element("PreviewTools")?.Remove();
        root.Add(new XElement("PreviewTools",ActiveTools.Where(tool=>tool.Color.HasValue).Select(tool=>
          new XElement("Tool",new XAttribute("process",tool.Process),new XAttribute("index",tool.Index),
            new XAttribute("color","#"+unchecked((uint)tool.Color!.Value.ToArgb()).ToString("X8",CultureInfo.InvariantCulture))))));
      }
      private void ApplyTargetVersion(XElement root)=>
        ApplyOutputFormat(root,_clipboard,TargetVersion==PatternSmithTargetVersion.PatternSmith10);

      private void ApplyMaterialMapping(XElement table,MaterialMappingRow row)
      {
        if(row.Material is {} material){ApplyCatalogMaterial(table,material,_clipboard);return;}
        table.SetAttributeValue("materialId",row.CustomId);table.SetAttributeValue("material",row.Name);
        if(_clipboard)table.SetAttributeValue("clipboardCatalogId",null);
        ApplyCustomColor(table,row.Name);
      }

      internal vCleanup.ExportCleanupAction? ReviewFindings(int overlaps,int shortFindings,string? deletionNotice)
      {
        if(overlaps<=0&&shortFindings<=0)return vCleanup.ExportCleanupAction.Ignore;
        ShowFindings(overlaps,shortFindings);_findingNotice.Text=deletionNotice??string.Empty;_findingNotice.Visible=deletionNotice!=null;
        SetStatusMessage(deletionNotice??"Review export findings");
        _findings.Enabled=true;_deleteFindings.Enabled=deletionNotice==null;_ignoreFindings.Enabled=true;_findingAnswered=false;_waitingForFindings=true;
        _exportClock.Stop();
        try
        {
          while(!_findingAnswered){KeepPreparationResponsive();if(_conversionClosed)return null;Thread.Sleep(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds)));}
          return _findingDecision;
        }
        finally{_waitingForFindings=false;if(!IsDisposed){_previewDelete.Checked=false;_cleanupReview?.SetDeletionPreview(false);_findings.Enabled=!_busy;}if(_busy)_exportClock.Start();}
      }
      internal ExistingFileAction DestinationAction {get;private set;}
      internal double MergeSpacingInches {get;private set;}=ToolsOptionStore.Read(nameof(vExportPatternSmith),section=>
        ToolsOptionStore.TryGetDouble(section,"mergeGapInches",out var value)&&double.IsFinite(value)&&value>=0?value:Math.Max(0,Config(nameof(MergeGapInches),MergeGapInches)));
      private ExistingFileAction AskExistingFile(string path)
      {
        _exportClock.Stop();
        try
        {
          using var dialog=new Eto.Forms.Dialog<ExistingFileAction> {Title="Existing PatternSmith project",Padding=MappingSpacing,Resizable=false};
          var overwrite=new Eto.Forms.Button {Text="Overwrite"};var merge=new Eto.Forms.Button {Text="Merge"};var cancel=new Eto.Forms.Button {Text="Cancel"};
          var spacing=new Eto.Forms.NumericStepper {MinValue=0,MaxValue=double.MaxValue,DecimalPlaces=MergeGapDecimalPlaces,Value=MergeSpacingInches,Width=MappingWidthColumnWidth,
            ToolTip="Workspace gap in inches. New patterns are added on the right; existing objects and nested placements do not move."};
          dialog.Content=new Eto.Forms.TableLayout {Spacing=new Eto.Drawing.Size(MappingSpacing,MappingSpacing),Rows={
            new Eto.Forms.TableRow(new Eto.Forms.Label {Text=Path.GetFileName(path)}),
            new Eto.Forms.TableRow(new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,Items={new Eto.Forms.Label {Text="Merge gap (in)"},spacing}}),
            new Eto.Forms.TableRow(new Eto.Forms.StackLayout {Orientation=Eto.Forms.Orientation.Horizontal,Spacing=MappingSpacing,Items={overwrite,merge,cancel}})}};
          dialog.DefaultButton=cancel;dialog.AbortButton=cancel;
          overwrite.Click+=(_,_)=>dialog.Close(ExistingFileAction.Overwrite);merge.Click+=(_,_)=>dialog.Close(ExistingFileAction.Merge);cancel.Click+=(_,_)=>dialog.Close(ExistingFileAction.Cancel);
          var action=dialog.ShowModal(this);
          if(action==ExistingFileAction.Merge)
          {
            MergeSpacingInches=spacing.Value;
            if(!ToolsOptionStore.Update(nameof(vExportPatternSmith),section=>section["mergeGapInches"]=MergeSpacingInches))
              Log.Write(nameof(vExportPatternSmith),"could not save merge spacing: {0}",ToolsOptionStore.LastError);
          }
          return action;
        }
        finally{if(_busy)_exportClock.Start();}
      }
      internal bool ConfirmOverwrite(string path)
      {
        _exportClock.Stop();
        try{return Eto.Forms.MessageBox.Show(this,"Replace the existing file?"+Environment.NewLine+path,"PatternSmith Export",Eto.Forms.MessageBoxButtons.YesNo,Eto.Forms.MessageBoxType.Question,Eto.Forms.MessageBoxDefaultButton.No)==Eto.Forms.DialogResult.Yes;}
        finally{if(_busy)_exportClock.Start();}
      }
      internal bool BrowseDestination(Func<string,string?>? choose=null)
      {
        _exportClock.Stop();
        try
        {
          string? path;
          if(choose!=null)path=choose(_outputPath.Text);
          else
          {
            using var save=new Eto.Forms.SaveFileDialog {Title="Export PatternSmith project",FileName=_outputPath.Text,Filters={new Eto.Forms.FileFilter("PatternSmith project",".project")}};
            path=save.ShowDialog(this)==Eto.Forms.DialogResult.Ok?save.FileName:null;
          }
          if(path==null)return false;
          _outputPath.Text=NormalizeProjectPath(path);return true;
        }
        finally{if(_busy)_exportClock.Start();}
      }
      internal string? ResolveDestination(Func<string,bool>? confirm=null,Func<string,string?>? choose=null)
      {
        DestinationAction=ExistingFileAction.Overwrite;
        if(confirm==null)return ResolveDestinationAction(AskExistingFile);
        string path=OutputPath;
        // Save As owns any further overwrite confirmation; preparation stays alive throughout filename selection.
        if(File.Exists(path)&&!(confirm??ConfirmOverwrite)(path)&&!BrowseDestination(choose))return null;
        return OutputPath;
      }
      internal string? ResolveDestinationAction(Func<string,ExistingFileAction> decide)
      {
        string path=OutputPath;
        DestinationAction=File.Exists(path)?decide(path):ExistingFileAction.Overwrite;
        return DestinationAction==ExistingFileAction.Cancel?null:path;
      }
      internal void RememberSuccessfulOutput(string destination)
      {
        if(_document!=null)RememberSessionOutput(_document,destination);
        try
        {
          if(!ToolsOptionStore.Update("vExportPatternSmith",section=>RememberOutputPath(section,destination)))
            Log.Write(nameof(vExportPatternSmith),"export succeeded, but filename history could not be saved: {0}",ToolsOptionStore.LastError);
        }
        catch(Exception ex){Log.Write(nameof(vExportPatternSmith),"export succeeded, but filename history could not be saved: {0}",ex);}
      }
      private void ApplyCustomColor(XElement table,string name)
      {
        string? saved=_customColors[name] is JsonValue value&&value.TryGetValue<string>(out var text)?text:null;
        Color color;
        if(saved is {Length:9}&&saved[0]=='#'&&uint.TryParse(saved.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out uint argb))color=Color.FromArgb(unchecked((int)argb));
        else
        {
          var random=new Eto.Drawing.ColorHSL((float)(Random.Shared.NextDouble()*360),
            (float)Math.Clamp(Config(nameof(CustomMaterialSaturation),CustomMaterialSaturation),0,1),
            (float)Math.Clamp(Config(nameof(CustomMaterialLightness),CustomMaterialLightness),0,1),1).ToColor();
          color=Color.FromArgb(random.ToArgb());_customColors[name]="#"+unchecked((uint)color.ToArgb()).ToString("X8",CultureInfo.InvariantCulture);
        }
        table.SetAttributeValue("materialHue",Number(color.GetHue()/360.0));table.SetAttributeValue("materialSaturation",Number(color.GetSaturation()));
        table.SetAttributeValue("materialLightness",Number(color.GetBrightness()));table.SetAttributeValue("materialAlpha",Number(color.A/255.0));
      }
    }

    private static PatternSmithTargetVersion ReadTargetVersion(JsonObject? section)
    {
      if(ToolsOptionStore.TryGetString(section,TargetVersionKey,out var name)&&
        Enum.TryParse<PatternSmithTargetVersion>(name,true,out var saved)&&Enum.IsDefined(saved))return saved;
      var fallback=Config(nameof(LegacyTableLabelFontSizing),LegacyTableLabelFontSizing)?PatternSmithTargetVersion.PatternSmith10:DefaultTargetVersion;
      return Config(nameof(DefaultTargetVersion),fallback);
    }
    private static string ReadCurveMode()=>ToolsOptionStore.Read("vExportPatternSmith",section=>ToolsOptionStore.TryGetString(section,"curve_mode",out var value)?value:Config(nameof(DefaultCurveMode),DefaultCurveMode));
    private static double ReadCurveError()=>ToolsOptionStore.Read("vExportPatternSmith",section=>ToolsOptionStore.TryGetDouble(section,"curve_error",out double value)&&double.IsFinite(value)&&value>=0?value:Math.Max(0,Config(nameof(DefaultCurveErrorInches),DefaultCurveErrorInches)));

    private static CatalogResult ReadCatalogWithUi(CancellationToken cancellationToken=default)
    {
      using var cancel=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      EventHandler escape=(_,_)=>cancel.Cancel();RhinoApp.EscapeKeyPressed+=escape;
      try
      {
        var pending=Task.Run(()=>LoadMaterialCatalog(cancel.Token),cancel.Token);
        while(!pending.IsCompleted){RhinoApp.Wait();Thread.Sleep(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds)));}
        return pending.GetAwaiter().GetResult();
      }
      finally{RhinoApp.EscapeKeyPressed-=escape;}
    }

    private static CatalogResult LoadMaterialCatalog(CancellationToken cancellationToken)=>PatternSmithCatalog.Load(cancellationToken);

    private static string Number(double value)
    {if(!double.IsFinite(value))throw new InvalidOperationException("A prepared coordinate is not finite.");return value.ToString("R",CultureInfo.InvariantCulture);}
  }
}

namespace vTools.Commands
{
  using System;
  using System.Linq;

public sealed partial class vExportPatternSmith
{
  private static void BuildNativeProject(System.Xml.Linq.XDocument request, string outputPath,CancellationToken cancellationToken=default,Action<int,string>? report=null,bool prepareOnly=false,bool background=false)
  {
    cancellationToken.ThrowIfCancellationRequested();report?.Invoke(0,"Starting native PatternSmith writer");
    request.Root!.SetAttributeValue("prepareSplinesOnly",prepareOnly?true:null);
    string libraryDirectory = FindPatternSmithLibrary();
    string runtimeRoot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), RuntimeFolderName, BuilderFolderName);
    string runtime = System.IO.Path.Combine(runtimeRoot, Guid.NewGuid().ToString("N"));
    string nativeOutput=System.IO.Path.Combine(runtime,NativeOutputFileName);
    string destination = System.IO.Path.GetFullPath(outputPath);
    string directory = System.IO.Path.GetDirectoryName(destination) ?? throw new System.IO.IOException("The export path has no directory.");
    if (!System.IO.Directory.Exists(directory)) throw new System.IO.DirectoryNotFoundException(directory);
    System.IO.Directory.CreateDirectory(runtime);
    bool cancelled = false;
    System.Diagnostics.Process? process=null;
    EventHandler escape = (_, _) => cancelled = true;
    if(!prepareOnly&&!background)Rhino.RhinoApp.EscapeKeyPressed += escape;
    try
    {
      string helper = System.IO.Path.Combine(runtime, "PatternSmithBuilder.exe");
      using (var resource = typeof(vExportPatternSmith).Assembly.GetManifestResourceStream(NativeBuilderResourceName)
          ?? throw new InvalidOperationException("The PatternSmith exporter resource is missing; rebuild vTools."))
      using (var file = new System.IO.FileStream(helper, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None))
        resource.CopyTo(file);
      string input = System.IO.Path.Combine(runtime, "request.xml");
      request.Save(input);
      var start = new System.Diagnostics.ProcessStartInfo(helper)
      {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtime,
        RedirectStandardOutput = true, RedirectStandardError = true,
      };
      start.ArgumentList.Add(libraryDirectory);
      start.ArgumentList.Add(input);
      start.ArgumentList.Add(nativeOutput);
      process = System.Diagnostics.Process.Start(start)
        ?? throw new InvalidOperationException("Could not start the PatternSmith project builder.");
      var updates=new System.Collections.Concurrent.ConcurrentQueue<string>();
      var stdout = ReadNativeOutput(process.StandardOutput,updates);
      var stderr = process.StandardError.ReadToEndAsync();
      var watch = System.Diagnostics.Stopwatch.StartNew();
      while (!process.WaitForExit(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds))))
      {
        if(!prepareOnly&&!background)Rhino.RhinoApp.Wait();
        while(updates.TryDequeue(out var update))
        {
          var fields=update.Split(new[]{'|'},3);
          if(fields.Length==3&&int.TryParse(fields[1],System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out int percent))report?.Invoke(Math.Clamp(percent,0,100)*96/100,fields[2]);
        }
        if (!cancelled&&!cancellationToken.IsCancellationRequested && watch.ElapsedMilliseconds < Math.Max(1,Config(nameof(BuilderTimeoutMilliseconds),BuilderTimeoutMilliseconds))) continue;
        try { process.Kill(); process.WaitForExit(); } catch (InvalidOperationException) { }
        Log.Write(nameof(vExportPatternSmith),"native writer interrupted: output={0}; diagnostics={1}",stdout.GetAwaiter().GetResult(),stderr.GetAwaiter().GetResult());
        if (cancelled||cancellationToken.IsCancellationRequested) throw new OperationCanceledException("PatternSmith export cancelled.");
        throw new TimeoutException("PatternSmith project building timed out; the destination was not replaced.");
      }
      string output = stdout.GetAwaiter().GetResult(), error = stderr.GetAwaiter().GetResult();
      if(cancelled)cancellationToken=new CancellationToken(true);
      cancellationToken.ThrowIfCancellationRequested();
      if (!string.IsNullOrWhiteSpace(output)) Log.Write(nameof(vExportPatternSmith), "native builder: {0}", output.Trim());
      foreach(string note in output.Split('\n').Where(line=>line.StartsWith("OUTSIDE LABEL|",StringComparison.Ordinal)))
      {
        var fields=note.TrimEnd('\r').Split('|');if(fields.Length<2)continue;
        string message="vExportPatternSmith: "+fields[1]+" has text outside its cut outline; labels retained. PatternSmith may reserve rectangular nesting clearance.";
        if(background)Eto.Forms.Application.Instance.AsyncInvoke(()=>{if(!cancellationToken.IsCancellationRequested)Rhino.RhinoApp.WriteLine(message);});
        else Rhino.RhinoApp.WriteLine(message);
      }
      int flatEffectPatterns=output.Split('\n').Count(line=>line.StartsWith("NATIVE EFFECT FALLBACK|",StringComparison.Ordinal));
      if(flatEffectPatterns>0)
      {
        string message="vExportPatternSmith: kept prepared seam or notch curves in "+CountText.Format(flatEffectPatterns,"pattern")+
          " because the editable effects could not reproduce the export geometry.";
        if(background)Eto.Forms.Application.Instance.AsyncInvoke(()=>{if(!cancellationToken.IsCancellationRequested)Rhino.RhinoApp.WriteLine(message);});
        else Rhino.RhinoApp.WriteLine(message);
      }
      const string openCutPrefix="OPEN CUT PATH|";
      var openOutlines=output.Split('\n').Where(line=>line.StartsWith(openCutPrefix,StringComparison.Ordinal))
        .Select(line=>line[openCutPrefix.Length..].TrimEnd('\r')).ToArray();
      if(openOutlines.Length>0)
      {
        string message="vExportPatternSmith: open cut paths exported with nesting disabled: "+string.Join(", ",openOutlines)+".";
        if(background)Eto.Forms.Application.Instance.AsyncInvoke(()=>{if(!cancellationToken.IsCancellationRequested)Rhino.RhinoApp.WriteLine(message);});
        else Rhino.RhinoApp.WriteLine(message);
      }
      if (!string.IsNullOrWhiteSpace(error)) Log.Write(nameof(vExportPatternSmith), "native builder diagnostics: {0}", error.Trim());
      if (process.ExitCode != 0)
        throw new InvalidOperationException("PatternSmith project building failed: " + (string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim()));
      if(prepareOnly){report?.Invoke(100,"Curves ready");return;}
      report?.Invoke(98,"Verifying exported project");if(!background)Rhino.RhinoApp.Wait();cancellationToken.ThrowIfCancellationRequested();
      if((bool?)request.Root.Attribute("clipboardPatterns")==true)
        PatternSmithClipboard.ReadPreparedPayload(nativeOutput);
      else
      using (var archive = System.IO.Compression.ZipFile.OpenRead(nativeOutput))
      {
        var entry = archive.GetEntry("project.xml") ?? throw new System.IO.InvalidDataException("The generated project has no project.xml.");
        using var stream = entry.Open();
        var project = System.Xml.Linq.XDocument.Load(stream);
        if (project.Root?.Name != System.Xml.Linq.XName.Get("PresentationProject", "http://autometrix.com/xml"))
          throw new System.IO.InvalidDataException("The generated file is not a PatternSmith project.");
      }
      PublishNativeProject(nativeOutput,destination,cancellationToken,()=>cancelled,pumpMessages:!background,notifyFileWatcher:false);report?.Invoke(100,"Export complete");
      Log.Write(nameof(vExportPatternSmith), "exported native project path={0}", destination);
    }
    finally
    {
      if(process!=null)
      {
        try{if(!process.HasExited){process.Kill();process.WaitForExit();}}catch(InvalidOperationException){}
        process.Dispose();
      }
      if(!prepareOnly&&!background)Rhino.RhinoApp.EscapeKeyPressed -= escape;
      try
      {
        string checkedRoot = System.IO.Path.GetFullPath(runtimeRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        string checkedRuntime = System.IO.Path.GetFullPath(runtime);
        if (checkedRuntime.StartsWith(checkedRoot, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(checkedRuntime))
          System.IO.Directory.Delete(checkedRuntime, recursive: true);
      }
      catch (Exception ex) { Log.Write(nameof(vExportPatternSmith), "temporary builder cleanup failed: {0}", ex.Message); }
    }
  }
  private static void PublishNativeProject(string source,string outputPath,CancellationToken cancellationToken,Func<bool>? cancelled=null,bool pumpMessages=true,bool notifyFileWatcher=true)
  {
    cancellationToken.ThrowIfCancellationRequested();
    string destination=System.IO.Path.GetFullPath(outputPath);
    string directory=System.IO.Path.GetDirectoryName(destination)??throw new System.IO.IOException("The export path has no directory.");
    string temporary=System.IO.Path.Combine(directory,"."+System.IO.Path.GetFileNameWithoutExtension(destination)+".vTools-"+Guid.NewGuid().ToString("N")+".project");
    try
    {
      System.IO.File.Copy(source,temporary,overwrite:false);
      if(pumpMessages)Rhino.RhinoApp.Wait();if(cancelled?.Invoke()==true)throw new OperationCanceledException();cancellationToken.ThrowIfCancellationRequested();
      System.IO.File.Move(temporary,destination,overwrite:true);
      if(notifyFileWatcher)
      {
        // PS handles the replacement as a rename, not a content change. Touch only after its rename handler can re-arm the watcher.
        var settle=System.Diagnostics.Stopwatch.StartNew();int delay=Math.Max(0,Config(nameof(PublishTouchDelayMilliseconds),PublishTouchDelayMilliseconds));
        while(settle.ElapsedMilliseconds<delay)
        {if(pumpMessages)Rhino.RhinoApp.Wait();Thread.Sleep((int)Math.Min(Math.Max(1,Config(nameof(BuilderPollMilliseconds),BuilderPollMilliseconds)),Math.Max(1,delay-settle.ElapsedMilliseconds)));}
        try{System.IO.File.SetLastWriteTimeUtc(destination,DateTime.UtcNow);}
        catch(Exception error){Log.Write(nameof(vExportPatternSmith),"project saved, but reload notification failed: {0}",error.Message);}
      }
    }
    finally
    {
      try{if(System.IO.File.Exists(temporary))System.IO.File.Delete(temporary);}
      catch(Exception error){Log.Write(nameof(vExportPatternSmith),"temporary output cleanup failed: {0}",error.Message);}
    }
  }
  private static async Task<string> ReadNativeOutput(System.IO.StreamReader reader,System.Collections.Concurrent.ConcurrentQueue<string> updates)
  {
    var output=new System.Text.StringBuilder();string? line;
    while((line=await reader.ReadLineAsync().ConfigureAwait(false))!=null)
    {output.AppendLine(line);if(line.StartsWith("PROGRESS|",StringComparison.Ordinal))updates.Enqueue(line);}
    return output.ToString();
  }

  internal static string FindPatternSmithLibrary()
  {
    var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
      .Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
    foreach (string root in roots)
    {
      string vendor = System.IO.Path.Combine(root, "Autometrix");
      if (!System.IO.Directory.Exists(vendor)) continue;
      foreach (string candidate in System.IO.Directory.EnumerateDirectories(vendor, "PatternSmith*"))
      {
        string core = System.IO.Path.Combine(candidate, "Autometrix.Core.dll");
        if (!System.IO.File.Exists(core)) continue;
        try
        {
          if (System.Reflection.AssemblyName.GetAssemblyName(core).Version?.ToString() == SupportedPatternSmithVersion &&
              System.IO.File.Exists(System.IO.Path.Combine(candidate, "Autometrix.Conversion.dll"))) return candidate;
        }
        catch (System.IO.IOException) { }
        catch (BadImageFormatException) { }
      }
    }
    throw new InvalidOperationException("Direct project export requires installed PatternSmith " + SupportedPatternSmithVersion + ".");
  }
}

}

#endif

namespace vTools.Commands
{
  internal static class PatternSmithPartContract
  {
    internal const string StateMember="VToolsPartState"; // Opaque JSON data-contract extension on a native Pattern, preserved on save.
    internal const string ConstraintNamespace="http://autometrix.com/xml/nesting"; // Native namespace of the companion constraint record.
    internal const string CollectionNamespace="http://schemas.datacontract.org/2004/07/System.Collections.ObjectModel"; // WPF clipboard read-only collection wrappers.
    internal const string MaterialEnforcedKey="PatternSmith.materialEnforced"; // true preserves native Strict material, false preserves Loose for assigned standalone parts.
    internal const double FreeRotationStep=0.001; // Native rotation step in radians used by PatternSmith to represent unrestricted rotation.
  }

  internal static class PatternSmithMaterialIdentity
  {
    // Project-only IDs, separate from the friendly material name.
    private const string Prefix="c"; // Native project-only business-ID namespace; no friendly name is repeated.
    private const int Capacity=10000; // Maximum distinct custom materials in one project; identifiers start at c1 with no zero padding.
    internal static System.Collections.Generic.Dictionary<string,string> Allocate(System.Collections.Generic.IEnumerable<string> names,System.Collections.Generic.IEnumerable<string> reserved,
      System.Collections.Generic.IDictionary<string,string> saved)
    {
      var used=new System.Collections.Generic.HashSet<string>(reserved,System.StringComparer.OrdinalIgnoreCase);
      var result=new System.Collections.Generic.Dictionary<string,string>(System.StringComparer.OrdinalIgnoreCase);
      var requested=names.Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray();
      foreach(var pair in saved)
      {
        if(requested.Contains(pair.Key,System.StringComparer.OrdinalIgnoreCase)&&System.Text.RegularExpressions.Regex.IsMatch(pair.Value,"^c[1-9][0-9]*$")&&used.Add(pair.Value))result[pair.Key]=pair.Value;
      }
      int next=1;
      foreach(string name in requested)
      {
        if(result.ContainsKey(name))continue;
        bool allocated=false;
        while(next<=Capacity)
        {
          string id=Prefix+(next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
          if(!used.Add(id))continue;result[name]=id;allocated=true;break;
        }
        if(!allocated)throw new System.InvalidOperationException("The project has too many Custom material IDs.");
      }
      return result;
    }
  }
}
