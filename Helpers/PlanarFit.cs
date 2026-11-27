using Rhino;
using Rhino.Geometry;

namespace vTools;

internal static class PlanarFit
{
  // Shared vFitBox planar-search defaults and numeric limits
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.CoarseStepDegrees", "Maximum planar coarse yaw step in degrees; positive.")] internal const double CoarseStepDegrees = 0.25; // Maximum planar coarse yaw step in degrees; positive.

  internal static readonly double ConfiguredCoarseStepDegrees = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.CoarseStepDegrees", CoarseStepDegrees); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.MinimumCoarseStepDegrees", "Minimum accepted coarse yaw step in degrees; positive.")] private const double MinimumCoarseStepDegrees = 0.1; // Minimum accepted coarse yaw step in degrees; positive.

  private static readonly double ConfiguredMinimumCoarseStepDegrees = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.MinimumCoarseStepDegrees", MinimumCoarseStepDegrees); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.MinimumLocalStepDegrees", "Minimum local evaluation increment in degrees, matching vFitBox.")] private const double MinimumLocalStepDegrees = 0.01; // Minimum local evaluation increment in degrees, matching vFitBox.

  private static readonly double ConfiguredMinimumLocalStepDegrees = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.MinimumLocalStepDegrees", MinimumLocalStepDegrees); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.FinalStepDegrees", "Final requested planar refinement increment in degrees; positive.")] private const double FinalStepDegrees = 0.001; // Final requested planar refinement increment in degrees; positive.

  private static readonly double ConfiguredFinalStepDegrees = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.FinalStepDegrees", FinalStepDegrees); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.InitialRefineFactor", "Fraction of the coarse increment used for the first local pass; zero through one.")] private const double InitialRefineFactor = 0.25; // Fraction of the coarse increment used for the first local pass; zero through one.

  private static readonly double ConfiguredInitialRefineFactor = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.InitialRefineFactor", InitialRefineFactor); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.MinimumWindowDegrees", "Minimum half-window for the first local search, in degrees.")] private const double MinimumWindowDegrees = 0.5; // Minimum half-window for the first local search, in degrees.

  private static readonly double ConfiguredMinimumWindowDegrees = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.MinimumWindowDegrees", MinimumWindowDegrees); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.InitialWindowFactor", "Coarse-increment multiplier for the first half-window; positive.")] private const double InitialWindowFactor = 1.5; // Coarse-increment multiplier for the first half-window; positive.

  private static readonly double ConfiguredInitialWindowFactor = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.InitialWindowFactor", InitialWindowFactor); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.RefineStepFactor", "Increment reduction per local pass; greater than zero and less than one.")] private const double RefineStepFactor = 0.2; // Increment reduction per local pass; greater than zero and less than one.

  private static readonly double ConfiguredRefineStepFactor = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.RefineStepFactor", RefineStepFactor); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.RefineWindowFactor", "Refined-increment multiplier for subsequent half-windows; positive.")] private const double RefineWindowFactor = 3.0; // Refined-increment multiplier for subsequent half-windows; positive.

  private static readonly double ConfiguredRefineWindowFactor = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.RefineWindowFactor", RefineWindowFactor); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarFit", "vTools.PlanarFit.ScoreTolerance", "Absolute score comparison tolerance; nonnegative.")] private const double ScoreTolerance = 1e-12; // Absolute score comparison tolerance; nonnegative.

  private static readonly double ConfiguredScoreTolerance = global::vTools.CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.ScoreTolerance", ScoreTolerance); // Resolved once from advanced defaults; restart Rhino after changes.
  private const double HalfTurnDegrees = 180.0; // Unique planar rectangle orientations span one half-turn in degrees.
  [CommandDefault("PlanarFit", "vTools.PlanarFit.CurveWitnessIntervals", "Parameter intervals sampled to reject impossible curve fits before measuring exact bounds; 4 through 128.")]
  private const int CurveWitnessIntervals = 32; // On-curve witness intervals, including both curve endpoints; 4 through 128.
  private static readonly int ConfiguredCurveWitnessIntervals = CommandDefaults.Get("PlanarFit", "vTools.PlanarFit.CurveWitnessIntervals", CurveWitnessIntervals); // Resolved from advanced defaults; restart Rhino after changes.
  private const int MinimumCurveWitnessIntervals = 4; // Smallest supported count for the conservative outline sampling.
  private const int MaximumCurveWitnessIntervals = 128; // Largest supported count to bound per-angle projection work.

  internal sealed record Fit(Plane Plane,BoundingBox Bounds,double Angle)
  {
    internal double Width => Bounds.Max.X-Bounds.Min.X;
    internal double Height => Bounds.Max.Y-Bounds.Min.Y;
    internal double Area => Width*Height;
  }

  internal static T? Refine<T>(Func<double,T?> evaluate,Func<T,T,bool> better,Func<T,double> angle,
    double stepDegrees,out int tested,T? seed=null) where T:class
  {
    double coarse=Math.Clamp(Math.Min(stepDegrees,ConfiguredCoarseStepDegrees),ConfiguredMinimumCoarseStepDegrees,ConfiguredCoarseStepDegrees);
    T? best=seed;tested=0;int testedCount=0;
    void Evaluate(double yaw)
    {
      var candidate=evaluate(yaw);
      if(candidate==null)return;
      testedCount++;
      if(best==null||better(candidate,best))best=candidate;
    }
    for(double yaw=0;yaw<HalfTurnDegrees+0.5*coarse;yaw+=coarse)Evaluate(yaw);
    if(best==null){tested=testedCount;return null;}
    double step=Math.Max(ConfiguredMinimumLocalStepDegrees,Math.Min(ConfiguredCoarseStepDegrees,coarse*ConfiguredInitialRefineFactor));
    double window=Math.Max(ConfiguredMinimumWindowDegrees,coarse*ConfiguredInitialWindowFactor);
    while(true)
    {
      double center=angle(best);
      double localStep=Math.Max(ConfiguredMinimumLocalStepDegrees,step),span=Math.Max(localStep,window);
      for(double offset=-span;offset<=span+0.5*localStep;offset+=localStep)Evaluate(center+offset);
      if(step<=ConfiguredFinalStepDegrees+ConfiguredScoreTolerance)break;
      step=Math.Max(ConfiguredFinalStepDegrees,step*ConfiguredRefineStepFactor);
      window=Math.Max(step*ConfiguredRefineWindowFactor,ConfiguredMinimumLocalStepDegrees);
    }
    tested=testedCount;return best;
  }

  internal static bool TryBounds(GeometryBase geometry,Plane plane,out BoundingBox bounds)
  {
    bounds=BoundingBox.Empty;
    if(!plane.IsValid)return false;
    if(geometry is not AnnotationBase)
      try
      {
        using var copy=geometry.Duplicate();
        if(copy!=null&&copy.Transform(Transform.PlaneToPlane(plane,Plane.WorldXY)))
        { bounds=copy.GetBoundingBox(true);if(bounds.IsValid)return true; }
      }
      catch { }
    try
    {
      var world=geometry.GetBoundingBox(true);
      if(!world.IsValid)return false;
      foreach(var point in world.GetCorners())
      {
        var vector=point-plane.Origin;
        bounds.Union(new Point3d(vector*plane.XAxis,vector*plane.YAxis,vector*plane.ZAxis));
      }
    }
    catch { return false; }
    return bounds.IsValid;
  }

  internal static Fit? FitCurve(Curve curve,Plane basePlane,double maximumWidth,double tolerance)
    =>FitCurveCore(curve,basePlane,maximumWidth,tolerance,false);

  internal static Fit? FitCurveByRollLength(Curve curve,Plane basePlane,double maximumWidth,double tolerance)
    =>FitCurveCore(curve,basePlane,maximumWidth,tolerance,true);

  private static Fit? FitCurveCore(Curve curve,Plane basePlane,double maximumWidth,double tolerance,bool minimizeRollLength)
  {
    double Score(double x,double y)=>!minimizeRollLength?x*y:Math.Max(x,y)<=maximumWidth+tolerance?Math.Min(x,y):Math.Max(x,y);
    bool exactPolyline = curve.TryGetPolyline(out var polyline) && polyline.Count > 1;
    int intervals = Math.Clamp(ConfiguredCurveWitnessIntervals, MinimumCurveWitnessIntervals, MaximumCurveWitnessIntervals);
    IEnumerable<Point3d> points = exactPolyline ? polyline : Enumerable.Range(0,intervals+1)
      .Select(i=>curve.PointAt(curve.Domain.ParameterAt((double)i/intervals)));
    var coordinates = points.Where(point=>point.IsValid).Select(point=>
    {
      var offset=point-basePlane.Origin;
      return new Point3d(offset*basePlane.XAxis,offset*basePlane.YAxis,offset*basePlane.Normal);
    }).ToArray();
    if(coordinates.Length==0)return null;
    double minZ=coordinates.Min(point=>point.Z),maxZ=coordinates.Max(point=>point.Z);
    double projectionPad=RhinoMath.SqrtEpsilon*Math.Max(1,coordinates.Max(point=>Math.Abs(point.X)+Math.Abs(point.Y)));
    Fit? measuredBest=null;
    Fit? Evaluate(double angle)
    {
      double radians=RhinoMath.ToRadians(angle);
      var (sine,cosine)=Math.SinCos(radians);
      double minX=double.PositiveInfinity,minY=double.PositiveInfinity,maxX=double.NegativeInfinity,maxY=double.NegativeInfinity;
      foreach(var point in coordinates)
      {
        double x=point.X*cosine+point.Y*sine,y=-point.X*sine+point.Y*cosine;
        minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
      }
      var lowerWidth=Math.Max(0,maxX-minX-2*projectionPad);
      var lowerHeight=Math.Max(0,maxY-minY-2*projectionPad);
      // Witnesses are on the outline, so their extents can only underestimate the exact bounds.
      if(Math.Min(lowerWidth,lowerHeight)>maximumWidth+tolerance ||
        (measuredBest!=null&&Score(lowerWidth,lowerHeight)>Score(measuredBest.Width,measuredBest.Height)+ConfiguredScoreTolerance))return null;
      var frame=basePlane;
      var bounds=new BoundingBox(new Point3d(minX,minY,minZ),new Point3d(maxX,maxY,maxZ));
      if(!frame.Rotate(radians,basePlane.Normal)||(!exactPolyline&&!TryBounds(curve,frame,out bounds)))return null;
      var fit=new Fit(frame,bounds,((angle%HalfTurnDegrees)+HalfTurnDegrees)%HalfTurnDegrees);
      if(Math.Min(fit.Width,fit.Height)>maximumWidth+tolerance)return null;
      if(measuredBest==null||Better(fit,measuredBest))measuredBest=fit;
      return fit;
    }
    bool Better(Fit candidate,Fit current)
    {
      if(minimizeRollLength)
      {
        double difference=Score(candidate.Width,candidate.Height)-Score(current.Width,current.Height);
        if(difference < -ConfiguredScoreTolerance)return true;
        if(difference > ConfiguredScoreTolerance)return false;
      }
      if(candidate.Area<current.Area-ConfiguredScoreTolerance)return true;
      if(candidate.Area>current.Area+ConfiguredScoreTolerance)return false;
      return Math.Max(candidate.Width,candidate.Height)<Math.Max(current.Width,current.Height)-ConfiguredScoreTolerance;
    }
    return Refine(Evaluate,Better,fit=>fit.Angle,ConfiguredCoarseStepDegrees,out _,Evaluate(0));
  }
}
