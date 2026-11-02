using Rhino;
using Rhino.Geometry;

namespace vTools;

internal static class PlanarFit
{
  // Shared vFitBox planar-search defaults and numeric limits
  internal const double CoarseStepDegrees = 0.25; // Maximum planar coarse yaw step in degrees; positive.
  private const double MinimumCoarseStepDegrees = 0.1; // Minimum accepted coarse yaw step in degrees; positive.
  private const double MinimumLocalStepDegrees = 0.01; // Minimum local evaluation increment in degrees, matching vFitBox.
  private const double FinalStepDegrees = 0.001; // Final requested planar refinement increment in degrees; positive.
  private const double InitialRefineFactor = 0.25; // Fraction of the coarse increment used for the first local pass; zero through one.
  private const double MinimumWindowDegrees = 0.5; // Minimum half-window for the first local search, in degrees.
  private const double InitialWindowFactor = 1.5; // Coarse-increment multiplier for the first half-window; positive.
  private const double RefineStepFactor = 0.2; // Increment reduction per local pass; greater than zero and less than one.
  private const double RefineWindowFactor = 3.0; // Refined-increment multiplier for subsequent half-windows; positive.
  private const double ScoreTolerance = 1e-12; // Absolute score comparison tolerance; nonnegative.
  private const double HalfTurnDegrees = 180.0; // Unique planar rectangle orientations span one half-turn in degrees.

  internal sealed record Fit(Plane Plane,BoundingBox Bounds,double Angle)
  {
    internal double Width => Bounds.Max.X-Bounds.Min.X;
    internal double Height => Bounds.Max.Y-Bounds.Min.Y;
    internal double Area => Width*Height;
  }

  internal static T? Refine<T>(Func<double,T?> evaluate,Func<T,T,bool> better,Func<T,double> angle,
    double stepDegrees,out int tested,T? seed=null) where T:class
  {
    double coarse=Math.Clamp(Math.Min(stepDegrees,CoarseStepDegrees),MinimumCoarseStepDegrees,CoarseStepDegrees);
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
    double step=Math.Max(MinimumLocalStepDegrees,Math.Min(CoarseStepDegrees,coarse*InitialRefineFactor));
    double window=Math.Max(MinimumWindowDegrees,coarse*InitialWindowFactor);
    while(true)
    {
      double center=angle(best);
      double localStep=Math.Max(MinimumLocalStepDegrees,step),span=Math.Max(localStep,window);
      for(double offset=-span;offset<=span+0.5*localStep;offset+=localStep)Evaluate(center+offset);
      if(step<=FinalStepDegrees+ScoreTolerance)break;
      step=Math.Max(FinalStepDegrees,step*RefineStepFactor);
      window=Math.Max(step*RefineWindowFactor,MinimumLocalStepDegrees);
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
  {
    Fit? Evaluate(double angle)
    {
      var frame=basePlane;
      if(!frame.Rotate(RhinoMath.ToRadians(angle),basePlane.Normal)||!TryBounds(curve,frame,out var bounds))return null;
      var fit=new Fit(frame,bounds,((angle%HalfTurnDegrees)+HalfTurnDegrees)%HalfTurnDegrees);
      return Math.Min(fit.Width,fit.Height)<=maximumWidth+tolerance?fit:null;
    }
    bool Better(Fit candidate,Fit current)
    {
      if(candidate.Area<current.Area-ScoreTolerance)return true;
      if(candidate.Area>current.Area+ScoreTolerance)return false;
      return Math.Max(candidate.Width,candidate.Height)<Math.Max(current.Width,current.Height)-ScoreTolerance;
    }
    return Refine(Evaluate,Better,fit=>fit.Angle,CoarseStepDegrees,out _,Evaluate(0));
  }
}
