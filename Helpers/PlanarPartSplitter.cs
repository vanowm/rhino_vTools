using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace vTools;

internal static class PlanarPartSplitter
{
  // Planner defaults and customizable limits
  private const int ScorePrecision = 6; // Decimal places retained by polygon clipping for approximate disconnected-panel scoring; zero through eight.
  [global::vTools.CommandDefault("PlanarPartSplitter", "vTools.PlanarPartSplitter.EvenBalanceWeight", "Nonnegative weight for balancing longest panel dimensions in Even mode; zero scores material length only.")]
  private const double EvenBalanceWeight = 1.0; // Model-unit length-imbalance penalty multiplier; zero disables physical-length balancing.
  private static readonly double ConfiguredEvenBalanceWeight = global::vTools.CommandDefaults.Get("PlanarPartSplitter", "vTools.PlanarPartSplitter.EvenBalanceWeight", EvenBalanceWeight); // Resolved nonnegative imbalance multiplier; restart Rhino after advanced changes.
  internal const double DefaultAngleStep = 5.0; // Degrees between automatic orientation candidates; greater than zero through 90.
  internal const int DefaultSamples = 256; // Boundary subdivisions for approximate material-length scoring; three or greater.
  internal const int DefaultMaxParts = 32; // Maximum number of automatically proposed bands; positive integer.
  internal const int DefaultRefinementSteps = 16; // Candidate seam positions per refinement pass; two or greater.
  internal const int DefaultRefinementPasses = 3; // Number of seam-position optimization passes; zero or greater.
  internal const double DefaultGap = 1.0; // Model-unit space between laid-out copies; zero or greater.
  internal const double DefaultReserve = 0.0; // Additional unused material width in model units; zero or greater.
  internal const SpacingMode DefaultSpacing = SpacingMode.Optimize; // Optimize minimizes estimated material length; Even balances physical piece lengths; MaxWidth fills each available band.
  [global::vTools.CommandDefault("PlanarPartSplitter", "vTools.PlanarPartSplitter.DefaultDirection", "Start/End fills from one end; Inward fills from both ends; Outward fills from the center.")] internal const SplitDirection DefaultDirection = SplitDirection.Start; // Start/End fills from one end; Inward fills from both ends; Outward fills from the center.

  internal static readonly SplitDirection ConfiguredDefaultDirection = global::vTools.CommandDefaults.Get("PlanarPartSplitter", "vTools.PlanarPartSplitter.DefaultDirection", DefaultDirection); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("PlanarPartSplitter", "vTools.PlanarPartSplitter.ProbePadding", "Minimum model-unit extension beyond a boundary's bounding box for clipping probes.")] private const double ProbePadding = 1.0; // Minimum model-unit extension beyond a boundary's bounding box for clipping probes.

  private static readonly double ConfiguredProbePadding = global::vTools.CommandDefaults.Get("PlanarPartSplitter", "vTools.PlanarPartSplitter.ProbePadding", ProbePadding); // Resolved once from advanced defaults; restart Rhino after changes.

  internal enum SpacingMode { Optimize, Even, MaxWidth }
  internal enum SplitDirection { Start, End, Inward, Outward }
  internal sealed record Settings(double Width, double Allowance, double Reserve = DefaultReserve,
    double Gap = DefaultGap, double AngleStep = DefaultAngleStep, int Samples = DefaultSamples,
    int MaxParts = DefaultMaxParts, int RefinementSteps = DefaultRefinementSteps,
    int RefinementPasses = DefaultRefinementPasses, SpacingMode Spacing = DefaultSpacing)
  {
    internal SplitDirection Direction { get; init; } = ConfiguredDefaultDirection;
  }
  internal sealed record Proposal(Plane Frame, double MinY, double MaxY, List<double> Seams, double MaterialLength);

  private static bool ValidSettings(Settings settings) => double.IsFinite(settings.Width) &&
    double.IsFinite(settings.Allowance) && double.IsFinite(settings.Reserve) &&
    settings.Allowance >= 0 && settings.Reserve >= 0 && settings.Width > settings.Reserve+2*settings.Allowance &&
    settings.AngleStep > 0 && settings.AngleStep <= 90 && settings.Samples >= 3 && settings.MaxParts > 0 &&
    settings.RefinementSteps >= 2 && settings.RefinementPasses >= 0 && settings.Gap >= 0 && Enum.IsDefined(settings.Direction);

  internal static bool Valid(Proposal proposal, Settings settings, double tolerance)
  {
    if (!double.IsFinite(proposal.MinY) || !double.IsFinite(proposal.MaxY) ||
        !double.IsFinite(settings.Width) || !double.IsFinite(settings.Allowance) ||
        settings.Allowance < 0 || settings.Width <= settings.Reserve ||
        proposal.Seams.Count + 1 > settings.MaxParts) return false;
    var previous = proposal.MinY;
    for (var index = 0; index <= proposal.Seams.Count; index++)
    {
      var next = index == proposal.Seams.Count ? proposal.MaxY : proposal.Seams[index];
      var span = next - previous + (index > 0 ? settings.Allowance : 0) +
        (index < proposal.Seams.Count ? settings.Allowance : 0);
      if (!double.IsFinite(next) || next <= previous + tolerance ||
          span > settings.Width - settings.Reserve + tolerance) return false;
      previous = next;
    }
    return true;
  }

  internal static bool TryPropose(Curve source, Plane plane, Settings settings, out Proposal proposal)
  {
    proposal = null!;
    if (!ValidSettings(settings)) return false;
    var parameters = source.DivideByCount(settings.Samples, true);
    if (parameters == null || parameters.Length < 3) return false;
    var points = parameters.Select(source.PointAt).ToArray();
    var bestScore = double.PositiveInfinity;
    (double X, double Y)[]? bestPoints = null;
    for (double degrees = 0; degrees < 180; degrees += settings.AngleStep)
    {
      var frame = RotatedFrame(plane, RhinoMath.ToRadians(degrees));
      var bounds = source.GetBoundingBox(frame);
      if (!bounds.IsValid) continue;
      var xy = points.Select(point => ((point - frame.Origin) * frame.XAxis,
        (point - frame.Origin) * frame.YAxis)).ToArray();
      var candidate = AtPosition(frame, bounds.Min.Y, bounds.Max.Y, settings, null);
      if (candidate == null) continue;
      var maxCount = settings.Spacing == SpacingMode.Optimize ? settings.MaxParts : candidate.Seams.Count + 1;
      for (int count = candidate.Seams.Count + 1; count <= maxCount; count++)
      {
        var seams = SegmentSeams(bounds.Min.Y, bounds.Max.Y, count, settings, false);
        var test = new Proposal(frame, bounds.Min.Y, bounds.Max.Y, seams, 0);
        if (!Valid(test, settings, RhinoMath.ZeroTolerance)) continue;
        var score = CandidateScore(xy, test, settings, out double materialLength);
        if (score >= bestScore) continue;
        bestScore = score;
        proposal = test with { MaterialLength = materialLength };
        bestPoints = xy;
      }
    }
    if (bestPoints == null || !double.IsFinite(bestScore)) return false;
    if (settings.Spacing != SpacingMode.MaxWidth) proposal = Refine(bestPoints, proposal, settings);
    return true;
  }

  internal static Plane RotatedFrame(Plane plane, double radians) => new(plane.Origin,
    Math.Cos(radians) * plane.XAxis + Math.Sin(radians) * plane.YAxis,
    -Math.Sin(radians) * plane.XAxis + Math.Cos(radians) * plane.YAxis);

  internal static bool TryAtPivot(Curve source, Plane plane, Settings settings,
    double radians, Point3d pivot, out Proposal proposal, out int seamIndex)
  {
    var frame = RotatedFrame(plane, radians);
    var bounds = source.GetBoundingBox(frame);
    proposal = bounds.IsValid
      ? AtPosition(frame, bounds.Min.Y, bounds.Max.Y, settings, FrameY(frame, pivot))! : null!;
    seamIndex = proposal == null ? -1 : proposal.Seams.FindIndex(y => Math.Abs(y - FrameY(frame, pivot)) <= RhinoMath.ZeroTolerance);
    return proposal != null && seamIndex >= 0;
  }

  internal static Proposal? AtPosition(Plane frame, double minY, double maxY, Settings settings, double? fixedSeam)
  {
    var usable = settings.Width - settings.Reserve;
    var capacity = usable - 2 * settings.Allowance;
    if (!ValidSettings(settings) || !double.IsFinite(minY) || !double.IsFinite(maxY) || maxY <= minY || capacity <= RhinoMath.ZeroTolerance)
      return null;
    if (!fixedSeam.HasValue)
    {
      var minimum = Math.Max(1, Math.Ceiling((maxY - minY - 2 * settings.Allowance) / capacity));
      if (minimum > settings.MaxParts) return null;
      for (int count = (int)minimum; count <= settings.MaxParts; count++)
      {
        var seams = SegmentSeams(minY, maxY, count, settings, false);
        var proposal = new Proposal(frame, minY, maxY, seams, 0);
        if (Valid(proposal, settings, RhinoMath.ZeroTolerance)) return proposal;
      }
      return null;
    }
    var y = fixedSeam.Value;
    if (!double.IsFinite(y) || y <= minY + RhinoMath.ZeroTolerance || y >= maxY - RhinoMath.ZeroTolerance) return null;
    var left = Math.Max(1, Math.Ceiling((y - minY - settings.Allowance) / capacity));
    var right = Math.Max(1, Math.Ceiling((maxY - y - settings.Allowance) / capacity));
    if (settings.Spacing != SpacingMode.MaxWidth)
    {
      if (left > 1) left = Math.Max(left, Math.Ceiling((y-minY)/capacity));
      if (right > 1) right = Math.Max(right, Math.Ceiling((maxY-y)/capacity));
    }
    if (left + right > settings.MaxParts) return null;
    var cuts = SegmentSeams(minY, y, (int)left, settings, false, true);
    cuts.Add(y);
    cuts.AddRange(SegmentSeams(y, maxY, (int)right, settings, true));
    var result = new Proposal(frame, minY, maxY, cuts, 0);
    return Valid(result, settings, RhinoMath.ZeroTolerance) ? result : null;
  }

  private static List<double> SegmentSeams(double lower, double upper, int count, Settings settings, bool lowerIsSeam, bool upperIsSeam = false)
  {
    if (settings.Spacing != SpacingMode.MaxWidth)
      return Enumerable.Range(1, count-1).Select(i => lower+(upper-lower)*i/count).ToList();
    var seams = new List<double>();
    if(count<=1) return seams;
    double usable=settings.Width-settings.Reserve,capacity=usable-2*settings.Allowance;
    if(settings.Direction==SplitDirection.Inward)
    {
      // Full outer pairs leave one or two symmetric center pieces, never crossing cuts.
      int pairs=(count-1)/2;
      double left=lower,right=upper;
      for(int index=0;index<pairs;index++)
      {
        left+=usable-settings.Allowance-(index>0||lowerIsSeam?settings.Allowance:0);
        right-=usable-settings.Allowance-(index>0||upperIsSeam?settings.Allowance:0);
        seams.Add(left); seams.Add(right);
      }
      if(count%2==0)
      {
        double leftAllowance=pairs>0||lowerIsSeam?settings.Allowance:0;
        double rightAllowance=pairs>0||upperIsSeam?settings.Allowance:0;
        seams.Add((left+right+rightAllowance-leftAllowance)/2);
      }
      return seams.OrderBy(value=>value).ToList();
    }
    if(settings.Direction==SplitDirection.Outward)
    {
      // Match the two outer remainders' final widths even beside a fixed seam.
      double middle=(lower+upper+(upperIsSeam?settings.Allowance:0)-(lowerIsSeam?settings.Allowance:0))/2;
      if(count%2==0)
      {
        seams.Add(middle);
        for(int index=1;index<count/2;index++)
        { seams.Add(middle-index*capacity); seams.Add(middle+index*capacity); }
      }
      else
        for(int index=0;index<count/2;index++)
        { double offset=(index+0.5)*capacity; seams.Add(middle-offset); seams.Add(middle+offset); }
      return seams.OrderBy(value=>value).ToList();
    }
    if(settings.Direction==SplitDirection.End)
    {
      double end=upper;
      for(int index=0;index<count-1;index++)
      {
        end-=usable-settings.Allowance-(index>0||upperIsSeam?settings.Allowance:0);
        seams.Add(end);
      }
      seams.Reverse(); return seams;
    }
    var y = lower;
    for (int i = 0; i < count-1; i++)
    {
      y += settings.Width-settings.Reserve-settings.Allowance-
        (i > 0 || lowerIsSeam ? settings.Allowance : 0);
      seams.Add(y);
    }
    return seams;
  }

  internal static double EstimateLength(IReadOnlyList<(double X, double Y)> points, Proposal proposal, Settings settings)
  {
    EmbeddedDependencies.EnsurePolygons();
    return EstimateLengthLoaded(points, proposal, settings, out _);
  }

  private static double CandidateScore(IReadOnlyList<(double X, double Y)> points, Proposal proposal, Settings settings, out double materialLength)
  {
    EmbeddedDependencies.EnsurePolygons();
    materialLength = EstimateLengthLoaded(points, proposal, settings, out double imbalance);
    return materialLength + (settings.Spacing == SpacingMode.Even ? imbalance * ConfiguredEvenBalanceWeight : 0);
  }

  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
  private static double EstimateLengthLoaded(IReadOnlyList<(double X, double Y)> points, Proposal proposal, Settings settings, out double imbalance)
  {
    imbalance = 0;
    if (points.Count < 3) return double.PositiveInfinity;
    double length = 0;
    int panels = 0;
    var lengths = new List<double>();
    var path = new Clipper2Lib.PathD(points.Select(point => new Clipper2Lib.PointD(point.X, point.Y)));
    double left = points.Min(point => point.X) - ConfiguredProbePadding;
    double right = points.Max(point => point.X) + ConfiguredProbePadding;
    for (int index = 0; index <= proposal.Seams.Count; index++)
    {
      var (lower, upper) = Band(proposal, index, settings.Allowance);
      var strip = new Clipper2Lib.PathD { new(left, lower), new(right, lower), new(right, upper), new(left, upper) };
      var pieces = Clipper2Lib.Clipper.Intersect(new Clipper2Lib.PathsD { path }, new Clipper2Lib.PathsD { strip }, Clipper2Lib.FillRule.NonZero, ScorePrecision);
      if (pieces.Count == 0) return double.PositiveInfinity;
      // A concave band's disconnected arms become separate parts, not one bounding box spanning their empty gap.
      foreach (var piece in pieces)
      {
        double span = piece.Max(point => point.x) - piece.Min(point => point.x);
        if (span <= RhinoMath.ZeroTolerance) continue;
        length += span + (panels++ == 0 ? 0 : settings.Gap);
        lengths.Add(Math.Max(span, piece.Max(point => point.y) - piece.Min(point => point.y)));
      }
    }
    if (lengths.Count > 0) { double average = lengths.Average(); imbalance = lengths.Sum(value => Math.Abs(value - average)); }
    return panels > 0 && panels <= settings.MaxParts ? length : double.PositiveInfinity;
  }

  internal static Proposal Refine(IReadOnlyList<(double X, double Y)> points, Proposal proposal, Settings settings)
  {
    for (int pass = 0; pass < settings.RefinementPasses; pass++)
      for (int index = 0; index < proposal.Seams.Count; index++)
      {
        double lower = index == 0 ? proposal.MinY : proposal.Seams[index - 1];
        double upper = index == proposal.Seams.Count - 1 ? proposal.MaxY : proposal.Seams[index + 1];
        var best = proposal;
        double bestScore = CandidateScore(points, best, settings, out _);
        for (int step = 1; step < settings.RefinementSteps; step++)
        {
          var seams = proposal.Seams.ToList();
          seams[index] = lower + (upper - lower) * step / settings.RefinementSteps;
          var candidate = proposal with { Seams = seams };
          if (!Valid(candidate, settings, RhinoMath.ZeroTolerance)) continue;
          double score = CandidateScore(points, candidate, settings, out double length);
          if (score + RhinoMath.ZeroTolerance < bestScore)
          { best = candidate with { MaterialLength = length }; bestScore = score; }
        }
        proposal = best;
      }
    return proposal;
  }

  internal static bool BandBounds(IReadOnlyList<(double X, double Y)> points, double lower, double upper,
    out double minX, out double maxX)
  {
    minX = double.PositiveInfinity;
    maxX = double.NegativeInfinity;
    for (int index = 0; index < points.Count; index++)
    {
      var a = points[index];
      var b = points[(index + 1) % points.Count];
      if (a.Y >= lower && a.Y <= upper) { minX = Math.Min(minX, a.X); maxX = Math.Max(maxX, a.X); }
      if (Math.Abs(b.Y - a.Y) <= RhinoMath.ZeroTolerance) continue;
      foreach (var edge in new[] { lower, upper })
      {
        var fraction = (edge - a.Y) / (b.Y - a.Y);
        if (fraction < 0 || fraction > 1) continue;
        var x = a.X + fraction * (b.X - a.X);
        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
      }
    }
    return double.IsFinite(minX) && maxX > minX;
  }

  internal static (double Lower, double Upper) Band(Proposal proposal, int index, double allowance) => (
    index == 0 ? proposal.MinY : proposal.Seams[index - 1] - allowance,
    index == proposal.Seams.Count ? proposal.MaxY : proposal.Seams[index] + allowance);

  internal static double FrameY(Plane frame, Point3d point) => (point - frame.Origin) * frame.YAxis;

  internal static Curve[] ClipBand(Curve source, Plane frame, double lower, double upper, double tolerance)
  {
    var bounds = source.GetBoundingBox(frame);
    var padding = Math.Max(ConfiguredProbePadding, bounds.Diagonal.Length);
    // Keep the clipping rectangle away from tangent contacts at the source extrema.
    if(lower<=bounds.Min.Y) lower=bounds.Min.Y-padding;
    if(upper>=bounds.Max.Y) upper=bounds.Max.Y+padding;
    using var rectangle = new PolylineCurve(new[] {
      frame.PointAt(bounds.Min.X-padding, lower), frame.PointAt(bounds.Max.X+padding, lower),
      frame.PointAt(bounds.Max.X+padding, upper), frame.PointAt(bounds.Min.X-padding, upper),
      frame.PointAt(bounds.Min.X-padding, lower) });
    return Curve.CreateBooleanIntersection(source, rectangle, tolerance) ?? [];
  }

  internal static Curve? Seam(Curve source, Plane frame, double y, double tolerance)
  {
    var bounds = source.GetBoundingBox(frame);
    var padding = Math.Max(ConfiguredProbePadding, bounds.Diagonal.Length);
    using var probe = new LineCurve(frame.PointAt(bounds.Min.X-padding, y), frame.PointAt(bounds.Max.X+padding, y));
    using var hits = Intersection.CurveCurve(source, probe, tolerance, tolerance);
    var parameters = hits?.Where(hit => hit.IsPoint).Select(hit => hit.ParameterB).OrderBy(t => t).ToArray() ?? [];
    if (parameters.Length < 2 || parameters[^1]-parameters[0] <= RhinoMath.ZeroTolerance) return null;
    return new LineCurve(probe.PointAt(parameters[0]), probe.PointAt(parameters[^1]));
  }

  internal static List<Curve> ClipCurve(Curve detail, Curve clip, Plane frame, double tolerance)
  {
    using var hits = Intersection.CurveCurve(detail, clip, tolerance, tolerance);
    var parameters = hits?.SelectMany(hit => hit.IsPoint ? new[] { hit.ParameterA } :
      hit.IsOverlap ? new[] { hit.OverlapA.T0, hit.OverlapA.T1 } : Array.Empty<double>())
      .Where(t => t > detail.Domain.T0 && t < detail.Domain.T1).Distinct().OrderBy(t => t).ToArray() ?? [];
    var pieces = parameters.Length == 0 ? new[] { detail.DuplicateCurve() } : detail.Split(parameters) ?? [];
    var retained = new List<Curve>();
    foreach (var piece in pieces)
      if (clip.Contains(piece.PointAtNormalizedLength(0.5), frame, tolerance) is PointContainment.Inside or PointContainment.Coincident)
        retained.Add(piece);
      else piece.Dispose();
    return retained;
  }
}
