using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Adds a chamfer line perpendicular to the angle bisector at a specified cut
/// length across a corner formed by two curves. The virtual corner is the
/// intersection of the tangent extensions from each curve's nearest endpoint -
/// works even when curves were previously chamfered and no longer share a point.
/// If a curve is too short to reach the chamfer point it is extended first.
///
/// Option Trim:
///   No  - only the chamfer line is added; curves are not modified.
///   Yes - both curves are trimmed to the chamfer points and the line is added.
///
/// Workflow:
///   Pick curve 1 - near the corner.
///   Pick curve 2 - near the same corner.
///   Length and Trim options are available at every prompt.
///   Press Enter to apply.
/// </summary>
public sealed class vChamfer : vToolsCommand
{
  private const string SectionName = "vChamfer";
  private const string LengthKey   = "length";
  private const string TrimKey     = "trim";
  private const string JoinKey     = "join";

  // Option defaults
  private const double DefaultLength = 1.0; // Chamfer setback in model units; zero or greater.
  private const bool DefaultTrim = true; // true trims source curves to the chamfer; false adds only the chamfer line.
  private const bool DefaultJoin = true; // true joins trimmed curves with the chamfer; false keeps the results separate.

  private static double _length = DefaultLength;
  private static bool   _trim   = DefaultTrim; // true = trim curves; false = add line only
  private static bool   _join   = DefaultJoin; // only used when _trim = true

  // -- Formatting helpers ---------------------------------------------------
  private static string P(Point3d p)  => $"({p.X:F4},{p.Y:F4},{p.Z:F4})";
  private static string P(Point3d? p) => p.HasValue ? P(p.Value) : "null";

  public override string EnglishName => "vChamfer";

  // -- Option persistence -----------------------------------------------------

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, LengthKey, out var l) && l >= 0.0)
        _length = l;
      if (ToolsOptionStore.TryGetBool(section, TrimKey, out var t))
        _trim = t;
      if (ToolsOptionStore.TryGetBool(section, JoinKey, out var j))
        _join = j;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[LengthKey] = _length;
      section[TrimKey]   = _trim;
      section[JoinKey]   = _join;
    });

  // -- Curve picking with options ---------------------------------------------

  private static (ObjRef? Ref, Curve? Crv) PickCurveWithOptions(string prompt)
  {
    while (true)
    {
      var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.SetCommandPrompt(prompt);
      go.GeometryFilter              = ObjectType.Curve;
      go.SubObjectSelect             = false;
      go.EnablePreSelect(false, true);
      go.DeselectAllBeforePostSelect = false;
      go.AcceptNumber(true, true);
      var lengthOpt = new OptionDouble(_length, 0.0, double.MaxValue);
      var idxLength = go.AddOptionDouble("Length", ref lengthOpt);
      var trimToggle = new OptionToggle(_trim, "No", "Yes");
      go.AddOptionToggle("Trim", ref trimToggle);
      var joinTogglePick = new OptionToggle(_join, "No", "Yes");
      if (_trim) go.AddOptionToggle("Join", ref joinTogglePick);

      var res = go.Get();

      if (res == GetResult.Object && go.ObjectCount >= 1)
      {
        var objRef = go.Object(0);
        if (objRef == null) return (null, null);
        var crv = objRef.Curve() ?? objRef.Geometry() as Curve;
        if (crv == null) return (null, null);
        return (objRef, crv.DuplicateCurve());
      }

      if (res == GetResult.Cancel || go.CommandResult() != Result.Success)
        return (null, null);

      if (res == GetResult.Number)
      {
        var v = go.Number();
        if (TrySetLength(v))
          SaveOptions();
        continue;
      }

      if (res == GetResult.Option)
      {
        if (go.Option()?.Index == idxLength)
          TrySetLength(lengthOpt.CurrentValue);
        _trim = trimToggle.CurrentValue;
        if (_trim) _join = joinTogglePick.CurrentValue;
        SaveOptions();
      }
    }
  }

  private static bool TrySetLength(double value)
  {
    if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
    {
      RhinoApp.WriteLine("vChamfer: length must be zero or greater.");
      return false;
    }

    _length = value;
    return true;
  }

  // -- Corner detection -------------------------------------------------------

  /// <summary>
  /// Finds the closest endpoint pair and computes a virtual corner as the
  /// intersection of the tangent extensions - correct even when the curves do
  /// not share an endpoint (e.g. previously chamfered).
  /// </summary>
  private static (bool C1AtStart, bool C2AtStart, Point3d VirtualCorner) FindCorner(
    Curve c1, Curve c2)
  {
    bool bestC1s = true, bestC2s = true;
    double best = double.MaxValue;
    foreach (bool c1s in new[] { true, false })
    foreach (bool c2s in new[] { true, false })
    {
      double d = (c1s ? c1.PointAtStart : c1.PointAtEnd)
                 .DistanceTo(c2s ? c2.PointAtStart : c2.PointAtEnd);
      if (d < best) { best = d; bestC1s = c1s; bestC2s = c2s; }
    }

    var ep1 = bestC1s ? c1.PointAtStart : c1.PointAtEnd;
    var ep2 = bestC2s ? c2.PointAtStart : c2.PointAtEnd;

    // Tangents pointing toward the virtual corner (past the near endpoint).
    var t1 = bestC1s ? -c1.TangentAtStart : c1.TangentAtEnd;
    var t2 = bestC2s ? -c2.TangentAtStart : c2.TangentAtEnd;
    t1.Unitize(); t2.Unitize();

    var lineA = new Line(ep1, ep1 + t1 * 1e4);
    var lineB = new Line(ep2, ep2 + t2 * 1e4);

    if (Intersection.LineLine(lineA, lineB, out double a, out double b, 1e-6, false))
    {
      var vc = (lineA.PointAt(a) + lineB.PointAt(b)) * 0.5;
      Log.Write("vChamfer", $"FindCorner  ep1={P(ep1)}  ep2={P(ep2)}  corner={P(vc)}");
      return (bestC1s, bestC2s, vc);
    }

    // Parallel tangents - fall back to endpoint midpoint.
    var mid = (ep1 + ep2) * 0.5;
    Log.Write("vChamfer", $"FindCorner  parallel tangents fallback  ep1={P(ep1)}  ep2={P(ep2)}  mid={P(mid)}");
    return (bestC1s, bestC2s, mid);
  }

  private static bool TryPrepareClosedCorner(
    Curve source,
    Point3d pickPoint,
    out Curve side1,
    out Curve side2,
    out Point3d corner,
    out double sourceCornerParameter)
  {
    side1 = null!;
    side2 = null!;
    corner = Point3d.Unset;
    sourceCornerParameter = double.NaN;

    var corners = FindClosedCorners(source);
    if (corners.Count == 0)
      return false;

    var selected = corners
      .OrderBy(candidate => candidate.Point.DistanceTo(pickPoint))
      .First();
    corner = selected.Point;
    sourceCornerParameter = selected.Parameter;

    var seamed = source.DuplicateCurve();
    if (seamed == null)
      return false;

    var sourceDomain = seamed.Domain;
    var seamTolerance = Math.Max(
      sourceDomain.Length * 1.0e-9,
      RhinoMath.ZeroTolerance * 10.0);
    var alreadyAtSeam =
      Math.Abs(sourceCornerParameter - sourceDomain.T0) <= seamTolerance ||
      Math.Abs(sourceCornerParameter - sourceDomain.T1) <= seamTolerance;
    if (!alreadyAtSeam &&
        !seamed.ChangeClosedCurveSeam(sourceCornerParameter))
    {
      seamed.Dispose();
      return false;
    }

    try
    {
      var domain = seamed.Domain;
      var epsilon = Math.Max(
        domain.Length * 1.0e-7,
        RhinoMath.ZeroTolerance * 10.0);
      var internalCorners = new List<double>();
      var seek = domain.T0 + epsilon;
      while (seek < domain.T1 &&
             seamed.GetNextDiscontinuity(
               Continuity.G1_continuous,
               seek,
               domain.T1,
               out var parameter))
      {
        internalCorners.Add(parameter);
        seek = parameter + epsilon;
      }

      double nextParameter;
      double previousParameter;
      if (internalCorners.Count > 0)
      {
        nextParameter = internalCorners.Min();
        previousParameter = internalCorners.Max();
      }
      else
      {
        if (!seamed.LengthParameter(seamed.GetLength() * 0.5, out var midpointParameter))
          return false;
        nextParameter = previousParameter = midpointParameter;
      }

      side1 = seamed.Trim(domain.T0, nextParameter)!;
      side2 = seamed.Trim(previousParameter, domain.T1)!;
      if (side1 == null || side2 == null ||
          side1.GetLength() <= RhinoMath.ZeroTolerance ||
          side2.GetLength() <= RhinoMath.ZeroTolerance)
      {
        side1?.Dispose();
        side2?.Dispose();
        side1 = null!;
        side2 = null!;
        return false;
      }

      corner = side1.PointAtStart;
      Log.Write(
        "vChamfer",
        $"ClosedCorner  click={P(pickPoint)} corner={P(corner)} " +
        $"source_parameter={sourceCornerParameter:G17} candidates={corners.Count}");
      return true;
    }
    finally
    {
      seamed.Dispose();
    }
  }

  private static List<ClosedCornerCandidate> FindClosedCorners(Curve curve)
  {
    var corners = new List<ClosedCornerCandidate>();
    var domain = curve.Domain;
    var epsilon = Math.Max(
      domain.Length * 1.0e-7,
      RhinoMath.ZeroTolerance * 10.0);
    var seek = domain.T0 + epsilon;

    while (seek < domain.T1 &&
           curve.GetNextDiscontinuity(
             Continuity.G1_continuous,
             seek,
             domain.T1,
             out var parameter))
    {
      AddCorner(parameter, false);
      seek = parameter + epsilon;
    }

    AddCorner(domain.T0, true);
    return corners;

    void AddCorner(double parameter, bool seam)
    {
      var beforeParameter = seam
        ? domain.T1 - epsilon
        : Math.Max(domain.T0, parameter - epsilon);
      var afterParameter = seam
        ? domain.T0 + epsilon
        : Math.Min(domain.T1, parameter + epsilon);
      var before = curve.TangentAt(beforeParameter);
      var after = curve.TangentAt(afterParameter);
      if (!before.Unitize() || !after.Unitize())
        return;

      var angle = Vector3d.VectorAngle(before, after);
      if (!RhinoMath.IsValidDouble(angle) ||
          angle < RhinoMath.ToRadians(1.0))
        return;

      if (corners.Any(candidate =>
            Math.Abs(candidate.Parameter - parameter) <= epsilon))
        return;

      corners.Add(new ClosedCornerCandidate(
        parameter,
        curve.PointAt(parameter)));
    }
  }

  private static bool TryBuildClosedChamferReplacement(
    RhinoDoc doc,
    Curve source,
    Point3d corner,
    Point3d point1,
    Point3d point2,
    bool join,
    out Curve replacement)
  {
    replacement = null!;
    if (!source.ClosestPoint(point1, out var parameter1) ||
        !source.ClosestPoint(point2, out var parameter2) ||
        Math.Abs(parameter1 - parameter2) <= RhinoMath.ZeroTolerance)
      return false;

    var pieces = source.Split(new[] { parameter1, parameter2 });
    if (pieces == null || pieces.Length < 2)
    {
      if (pieces != null)
      {
        foreach (var piece in pieces)
          piece?.Dispose();
      }
      return false;
    }

    var cornerTolerance = Math.Max(
      doc.ModelAbsoluteTolerance * 2.0,
      RhinoMath.ZeroTolerance * 10.0);
    var retainedPieces = pieces
      .Where(piece => DistanceToCurve(piece, corner) > cornerTolerance)
      .ToList();
    if (retainedPieces.Count == 0)
    {
      retainedPieces.Add(pieces
        .OrderByDescending(piece => DistanceToCurve(piece, corner))
        .First());
    }

    Curve retained;
    if (retainedPieces.Count == 1)
    {
      retained = retainedPieces[0];
      foreach (var piece in pieces)
      {
        if (!ReferenceEquals(piece, retained))
          piece.Dispose();
      }
    }
    else
    {
      var joinedRemainder = Curve.JoinCurves(
        retainedPieces,
        doc.ModelAbsoluteTolerance);
      foreach (var piece in pieces)
        piece.Dispose();
      if (joinedRemainder == null || joinedRemainder.Length != 1)
      {
        if (joinedRemainder != null)
        {
          foreach (var curve in joinedRemainder)
            curve?.Dispose();
        }
        return false;
      }
      retained = joinedRemainder[0];
    }

    if (!join)
    {
      replacement = retained;
      return true;
    }

    using var chamfer = new LineCurve(point1, point2);
    var joined = Curve.JoinCurves(
      new Curve[] { retained, chamfer },
      doc.ModelAbsoluteTolerance);
    retained.Dispose();
    if (joined == null || joined.Length != 1)
    {
      if (joined != null)
      {
        foreach (var curve in joined)
          curve?.Dispose();
      }
      return false;
    }

    replacement = joined[0];
    return true;

    static double DistanceToCurve(Curve curve, Point3d point)
    {
      return curve.ClosestPoint(point, out var parameter)
        ? curve.PointAt(parameter).DistanceTo(point)
        : double.MaxValue;
    }
  }

  // -- Extension -------------------------------------------------------------

  /// <summary>
  /// Extends the corner end of a working-copy curve to the virtual corner point.
  /// This allows chamfering when the curve is too short to reach the chamfer point.
  /// Returns the (possibly extended) curve.
  /// </summary>
  private static Curve ExtendToCorner(Curve c, bool atStart, Point3d virtualCorner)
  {
    var ep   = atStart ? c.PointAtStart : c.PointAtEnd;
    double d = ep.DistanceTo(virtualCorner);
    if (d < 1e-6) return c;

    var end      = atStart ? CurveEnd.Start : CurveEnd.End;
    var extended = c.Extend(end, d + 1e-3, CurveExtensionStyle.Line);
    return extended ?? c;
  }

  // -- Chamfer computation ----------------------------------------------------

  // Shoot a ray perpendicular to `tangent` in the XY plane from `pt`.
  // Returns (NaN, NaN, Unset) when c2 doesn't extend to this location.
  private static (double Gap, double TB, Point3d PtB) NormalRayHit(
    Point3d pt, Vector3d tangent, Curve c2)
  {
    var normal = Vector3d.CrossProduct(Vector3d.ZAxis, tangent);
    if (!normal.Unitize()) return (double.NaN, double.NaN, Point3d.Unset);

    if (!c2.ClosestPoint(pt, out double tGuess)) return (double.NaN, double.NaN, Point3d.Unset);
    var ptGuess = c2.PointAt(tGuess);
    if ((ptGuess - pt) * normal < 0.0) normal = -normal;

    double span = Math.Max(pt.DistanceTo(ptGuess) * 4.0, c2.GetLength() + 1.0);
    var line   = new Line(pt - normal * 1e-3, pt + normal * span);
    var events = Intersection.CurveLine(c2, line, 1e-6, 1e-6);

    if (events != null && events.Count > 0)
    {
      double bestD = double.MaxValue;
      double bestTB = double.NaN;
      Point3d bestPt = Point3d.Unset;
      for (int i = 0; i < events.Count; i++)
      {
        if (!events[i].IsPoint) continue;
        var hitPt = events[i].PointA;
        if ((hitPt - pt) * normal < -1e-6) continue;
        double d = hitPt.DistanceTo(pt);
        if (d < bestD) { bestD = d; bestTB = events[i].ParameterA; bestPt = hitPt; }
      }
      if (bestPt.IsValid) return (bestD, bestTB, bestPt);
    }
    return (double.NaN, double.NaN, Point3d.Unset);
  }

  // Two-step gap measurement perpendicular to the MIDDLE curve (average tangents):
  // step 1 - c1-perp hit gives c2 tangent; step 2 - re-shoot along average tangent.
  // Step 1 uses ClosestPoint fallback so short-curve geometries don't silently fail.
  private static (double Gap, double TB, Point3d PtB) EquidistantGap(
    Point3d ptA,
    Vector3d tanA,
    bool c1AtStart,
    Curve c2,
    bool c2AtStart)
  {
    var awayTanA = c1AtStart ? tanA : -tanA;

    // Step 1: c1-perp, with ClosestPoint fallback for curves where the ray misses c2.
    var (g1, tB1, ptB1) = NormalRayHit(ptA, awayTanA, c2);
    if (double.IsNaN(g1) || !ptB1.IsValid)
    {
      // Fallback: use ClosestPoint as the initial ptB estimate.
      if (!c2.ClosestPoint(ptA, out tB1)) return (double.NaN, double.NaN, Point3d.Unset);
      ptB1 = c2.PointAt(tB1);
      g1   = ptA.DistanceTo(ptB1);
    }

    var awayTanB = c2.TangentAt(tB1);
    if (!c2AtStart)
      awayTanB = -awayTanB;

    var avgTan = awayTanA + awayTanB;
    if (!avgTan.Unitize()) return (g1, tB1, ptB1);

    var (g2, tB2, ptB2) = NormalRayHit(ptA, avgTan, c2);
    return (!double.IsNaN(g2) && ptB2.IsValid) ? (g2, tB2, ptB2) : (g1, tB1, ptB1);
  }

  // length = desired chamfer line length.
  // Binary-searches c1 (c1-perp gap, monotone) for the position where gap ? targetGap,
  // then places ptB at exactly targetGap in the middle-curve-perpendicular direction.
  private static bool ComputeChamfer(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    double targetGap,
    out Point3d ptA, out Point3d ptB,
    out double  tA,  out double  tB)
  {
    ptA = ptB = Point3d.Unset;
    tA  = tB  = double.NaN;

    if (targetGap < 0.0) return false;

    if (targetGap <= RhinoMath.ZeroTolerance)
    {
      tA  = c1AtStart ? c1.Domain.Min : c1.Domain.Max;
      ptA = c1.PointAt(tA);
      if (!c2.ClosestPoint(ptA, out tB)) return false;
      ptB = c2.PointAt(tB);
      return ptA.IsValid && ptB.IsValid;
    }

    // Binary search using EquidistantGap (middle-curve-perp) so it converges
    // where the actual chamfer line = targetGap AND angle is correct.
    double len1 = c1.GetLength();
    double maxS = Math.Min(len1, c2.GetLength());
    double lo = 0.0, hi = maxS;
    for (int i = 0; i < 52; i++)
    {
      double s   = 0.5 * (lo + hi);
      double seg = c1AtStart ? s : (len1 - s);
      if (!c1.LengthParameter(seg, out double tMid)) break;
      var ptMid  = c1.PointAt(tMid);
      var tanMid = c1.TangentAt(tMid);
      var (gap, _, _) = EquidistantGap(
        ptMid, tanMid, c1AtStart, c2, c2AtStart);
      if (double.IsNaN(gap)) { hi = s; continue; }
      if (gap < targetGap) lo = s; else hi = s;
      if (hi - lo < 1e-9) break;
    }

    double sA   = 0.5 * (lo + hi);
    double segA = c1AtStart ? sA : (len1 - sA);
    if (!c1.LengthParameter(segA, out tA)) return false;
    ptA = c1.PointAt(tA);
    if (!ptA.IsValid) return false;

    var tanA = c1.TangentAt(tA);
    var (finalGap, tBfinal, ptBfinal) = EquidistantGap(
      ptA, tanA, c1AtStart, c2, c2AtStart);
    if (double.IsNaN(tBfinal) || !ptBfinal.IsValid)
    {
      Log.Write("vChamfer", $"ComputeChamfer  no c2 hit  sA={sA:G4}");
      return false;
    }
    if (Math.Abs(finalGap - targetGap) > targetGap * 0.1 + 1e-3)
    {
      Log.Write("vChamfer", $"ComputeChamfer  targetGap={targetGap:G4} not achieved  finalGap={finalGap:G4}");
      return false;
    }

    tB  = tBfinal;
    ptB = ptBfinal;
    Log.Write("vChamfer", $"ComputeChamfer  OK  gap={ptA.DistanceTo(ptB):G4}  ptA={P(ptA)}  ptB={P(ptB)}");
    return true;
  }

  private static bool TryEvaluatePointStation(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    double station,
    out Point3d ptA, out Point3d ptB,
    out double tA, out double tB)
  {
    ptA = ptB = Point3d.Unset;
    tA = tB = double.NaN;

    double len1 = c1.GetLength();
    station = Math.Max(0.0, Math.Min(station, len1));
    double curveLength = c1AtStart ? station : len1 - station;
    if (!c1.LengthParameter(curveLength, out tA))
      return false;

    ptA = c1.PointAt(tA);
    var tanA = c1.TangentAt(tA);
    var (gap, candidateTB, candidatePtB) = EquidistantGap(
      ptA, tanA, c1AtStart, c2, c2AtStart);
    if (double.IsNaN(gap) || double.IsNaN(candidateTB) || !candidatePtB.IsValid)
      return false;

    tB = candidateTB;
    ptB = candidatePtB;
    return true;
  }

  private static bool ComputeChamferFromPoint(
    Curve c1, bool c1AtStart,
    Curve c2, bool c2AtStart,
    Point3d pickedPoint, double offset,
    out Point3d ptA, out Point3d ptB,
    out double tA, out double tB)
  {
    ptA = ptB = Point3d.Unset;
    tA = tB = double.NaN;
    if (!pickedPoint.IsValid || offset < 0.0)
    {
      Log.Write("vChamfer", $"PointOffset  invalid input point={P(pickedPoint)} offset={offset:G6}");
      return false;
    }

    double length = c1.GetLength();
    const int sampleCount = 48; // Samples used to find the nearest closed-curve corner; four or greater.
    double bestStation = double.NaN;
    double bestDistance = double.MaxValue;
    double sampleStep = length / sampleCount;

    double MidpointDistance(double station)
    {
      if (!TryEvaluatePointStation(
            c1, c1AtStart, c2, c2AtStart, station,
            out var a, out var b, out _, out _))
        return double.MaxValue;
      return pickedPoint.DistanceTo((a + b) * 0.5);
    }

    for (int i = 0; i <= sampleCount; i++)
    {
      double station = length * i / sampleCount;
      double distance = MidpointDistance(station);
      if (distance < bestDistance)
      {
        bestDistance = distance;
        bestStation = station;
      }
    }

    if (double.IsNaN(bestStation))
    {
      Log.Write("vChamfer", "PointOffset  no valid reference station");
      return false;
    }

    double refineLo = Math.Max(0.0, bestStation - sampleStep);
    double refineHi = Math.Min(length, bestStation + sampleStep);
    for (int i = 0; i < 28; i++)
    {
      double left = (2.0 * refineLo + refineHi) / 3.0;
      double right = (refineLo + 2.0 * refineHi) / 3.0;
      if (MidpointDistance(left) <= MidpointDistance(right))
        refineHi = right;
      else
        refineLo = left;
    }

    double referenceStation = 0.5 * (refineLo + refineHi);
    if (!TryEvaluatePointStation(
          c1, c1AtStart, c2, c2AtStart, referenceStation,
          out var refA, out var refB, out _, out _))
    {
      Log.Write("vChamfer", $"PointOffset  reference evaluation failed station={referenceStation:G6}");
      return false;
    }
    var referenceMidpoint = (refA + refB) * 0.5;

    double targetStation = referenceStation;
    var targetDirection = "reference";
    if (offset > RhinoMath.ZeroTolerance)
    {
      bool TryFindOffsetStation(double endStation, out double stationAtOffset)
      {
        stationAtOffset = double.NaN;
        double nearStation = referenceStation;
        double farStation = double.NaN;
        const int offsetSamples = 96; // Samples used to locate point-driven chamfer offsets; four or greater.

        for (int i = 1; i <= offsetSamples; i++)
        {
          double station = referenceStation +
            ((endStation - referenceStation) * i / offsetSamples);
          if (!TryEvaluatePointStation(
                c1, c1AtStart, c2, c2AtStart, station,
                out var candidateA, out var candidateB, out _, out _))
            continue;

          double distance = referenceMidpoint.DistanceTo((candidateA + candidateB) * 0.5);
          if (distance >= offset)
          {
            farStation = station;
            break;
          }
          nearStation = station;
        }

        if (double.IsNaN(farStation))
          return false;

        for (int i = 0; i < 44; i++)
        {
          double station = 0.5 * (farStation + nearStation);
          if (!TryEvaluatePointStation(
                c1, c1AtStart, c2, c2AtStart, station,
                out var candidateA, out var candidateB, out _, out _))
          {
            farStation = station;
            continue;
          }

          double distance = referenceMidpoint.DistanceTo((candidateA + candidateB) * 0.5);
          if (distance >= offset)
            farStation = station;
          else
            nearStation = station;
        }

        stationAtOffset = 0.5 * (farStation + nearStation);
        return true;
      }

      if (TryFindOffsetStation(0.0, out targetStation))
      {
        targetDirection = "toward-corner";
      }
      else if (TryFindOffsetStation(length, out targetStation))
      {
        targetDirection = "away-from-corner";
      }
      else
      {
        Log.Write("vChamfer",
          $"PointOffset  no offset solution referenceStation={referenceStation:G6} " +
          $"length={length:G6} offset={offset:G6}");
        return false;
      }
    }

    if (!TryEvaluatePointStation(
          c1, c1AtStart, c2, c2AtStart, targetStation,
          out ptA, out ptB, out tA, out tB))
    {
      Log.Write("vChamfer", $"PointOffset  target evaluation failed station={targetStation:G6}");
      return false;
    }

    var targetMidpoint = (ptA + ptB) * 0.5;
    Log.Write("vChamfer",
      $"PointOffset  referenceStation={referenceStation:G6} " +
      $"targetStation={targetStation:G6} direction={targetDirection} offset={offset:G6} " +
      $"actual={referenceMidpoint.DistanceTo(targetMidpoint):G6} " +
      $"projection={pickedPoint.DistanceTo(referenceMidpoint):G6} " +
      $"ptA={P(ptA)} ptB={P(ptB)}");
    return true;
  }


  // -- Preview conduit --------------------------------------------------------

  private sealed class ChamferPreviewConduit : DisplayConduit
  {
    public Line?  ChamferLine { get; set; }
    /// <summary>Straight extension added to work1 to reach virtual corner.</summary>
    public Line?  Ext1        { get; set; }
    /// <summary>Straight extension added to work2 to reach virtual corner.</summary>
    public Line?  Ext2        { get; set; }
    /// <summary>Corner piece trimmed from work1 (corner end ? chamfer point).</summary>
    public Curve? CutOff1     { get; set; }
    /// <summary>Corner piece trimmed from work2 (corner end ? chamfer point).</summary>
    public Curve? CutOff2     { get; set; }
    /// <summary>Whether to draw cut-off geometry in red (Trim=Yes).</summary>
    public bool   ShowTrim    { get; set; }

    public void Clear(bool showTrim)
    {
      ChamferLine = null;
      Ext1 = null;
      Ext2 = null;
      CutOff1 = null;
      CutOff2 = null;
      ShowTrim = showTrim;
    }

    protected override void DrawOverlay(DrawEventArgs e)
    {
      // Extension stubs that survive trimming (cyan - part of the kept curve)
      if (Ext1 is { } ext1)
        PreviewDisplay.DrawLine(e.Display, ext1, Color.Cyan, 1);
      if (Ext2 is { } ext2)
        PreviewDisplay.DrawLine(e.Display, ext2, Color.Cyan, 1);

      // Corner pieces removed by trim - red
      if (ShowTrim)
      {
        if (CutOff1 != null)
          PreviewDisplay.DrawCurve(e.Display, CutOff1, Color.Red, 1);
        if (CutOff2 != null)
          PreviewDisplay.DrawCurve(e.Display, CutOff2, Color.Red, 1);
      }

      // Chamfer line - cyan, drawn on top
      if (ChamferLine is { } line)
        PreviewDisplay.DrawLine(e.Display, line, Color.Cyan, 1);
    }
  }


  // -- Conduit update helper --------------------------------------------------

  /// <summary>
  /// Recomputes all conduit preview geometry from current state.
  /// </summary>
  private static void UpdateConduit(
    ChamferPreviewConduit conduit,
    Curve crv1, Curve work1, bool c1AtStart,
    Curve crv2, Curve work2, bool c2AtStart,
    double tA, double tB, Point3d ptA, Point3d ptB)
  {
    // Cut-off curve pieces (red when Trim=Yes): original curve corner end ? chamfer point.
    // Computed first so extension-line gating can reference them.
    conduit.CutOff1 = null;
    if (crv1.ClosestPoint(ptA, out var tAorig))
    {
      // Only show cut-off when ptA is on the curve body, not when it's in the extension
      // zone (where the closest point on the original curve is its corner endpoint).
      bool atEndpoint1 = c1AtStart
        ? tAorig <= crv1.Domain.Min + 1e-6
        : tAorig >= crv1.Domain.Max - 1e-6;
      if (!atEndpoint1)
        conduit.CutOff1 = c1AtStart
          ? crv1.Trim(crv1.Domain.Min, tAorig)
          : crv1.Trim(tAorig, crv1.Domain.Max);
    }

    conduit.CutOff2 = null;
    if (crv2.ClosestPoint(ptB, out var tBorig))
    {
      bool atEndpoint2 = c2AtStart
        ? tBorig <= crv2.Domain.Min + 1e-6
        : tBorig >= crv2.Domain.Max - 1e-6;
      if (!atEndpoint2)
        conduit.CutOff2 = c2AtStart
          ? crv2.Trim(crv2.Domain.Min, tBorig)
          : crv2.Trim(tBorig, crv2.Domain.Max);
    }

    // Extension lines:
    //   Trim=No : show full extension (crv1End ? virtual corner) - it will be added to the doc
    //   Trim=Yes, ptA in extension zone (CutOff==null): show stub crv1End?ptA - it stays in result
    //   Trim=Yes, ptA in original body  (CutOff!=null): hide - extension is trimmed off
    var crv1End  = c1AtStart ? crv1.PointAtStart : crv1.PointAtEnd;
    var work1End = c1AtStart ? work1.PointAtStart : work1.PointAtEnd;
    conduit.Ext1 = crv1End.DistanceTo(work1End) > 1e-6
      ? !_trim
          ? crv1End.DistanceTo(ptA) > 1e-6 ? new Line(crv1End, ptA) : (Line?)null
          : conduit.CutOff1 == null && crv1End.DistanceTo(ptA) > 1e-6
              ? new Line(crv1End, ptA)
              : (Line?)null
      : (Line?)null;

    var crv2End  = c2AtStart ? crv2.PointAtStart : crv2.PointAtEnd;
    var work2End = c2AtStart ? work2.PointAtStart : work2.PointAtEnd;
    conduit.Ext2 = crv2End.DistanceTo(work2End) > 1e-6
      ? !_trim
          ? crv2End.DistanceTo(ptB) > 1e-6 ? new Line(crv2End, ptB) : (Line?)null
          : conduit.CutOff2 == null && crv2End.DistanceTo(ptB) > 1e-6
              ? new Line(crv2End, ptB)
              : (Line?)null
      : (Line?)null;

    conduit.ChamferLine = ptA.DistanceTo(ptB) > 1e-10 ? new Line(ptA, ptB) : (Line?)null;
    conduit.ShowTrim    = _trim;
  }
  // -- Command ----------------------------------------------------------------

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();

    var (ref1, pickedCurve1) = PickCurveWithOptions("Select first curve at corner");
    if (ref1 == null || pickedCurve1 == null) return Result.Cancel;

    ObjRef ref2;
    Curve crv1;
    Curve crv2;
    Curve? closedSourceCurve = null;
    var click1 = ref1.SelectionPoint();
    var click2 = Point3d.Unset;
    bool c1AtStart;
    bool c2AtStart;
    Point3d corner;

    if (pickedCurve1.IsClosed)
    {
      var cornerHint = click1.IsValid
        ? click1
        : pickedCurve1.PointAtStart;
      if (!TryPrepareClosedCorner(
            pickedCurve1,
            cornerHint,
            out crv1,
            out crv2,
            out corner,
            out _))
      {
        RhinoApp.WriteLine("vChamfer: the closed curve has no corner near the pick point.");
        return Result.Failure;
      }

      ref2 = ref1;
      click2 = click1;
      c1AtStart = true;
      c2AtStart = false;
      closedSourceCurve = pickedCurve1;
    }
    else
    {
      var secondPick = PickCurveWithOptions("Select second curve at corner");
      if (secondPick.Ref == null || secondPick.Crv == null)
        return Result.Cancel;

      ref2 = secondPick.Ref;
      crv1 = pickedCurve1;
      crv2 = secondPick.Crv;
      click2 = ref2.SelectionPoint();

      if (ref1.ObjectId == ref2.ObjectId)
      {
        RhinoApp.WriteLine("vChamfer: select two different curves.");
        return Result.Failure;
      }

      if (crv2.IsClosed)
      {
        RhinoApp.WriteLine("vChamfer: select a closed curve as the first curve to chamfer its nearest corner.");
        return Result.Failure;
      }

      (c1AtStart, c2AtStart, corner) = FindCorner(crv1, crv2);
    }

    Log.Write("vChamfer", $"RunCommand  click1={P(click1.IsValid ? (Point3d?)click1 : null)}  click2={P(click2.IsValid ? (Point3d?)click2 : null)}");
    Log.Write("vChamfer", $"RunCommand  corner={P(corner)}  c1AtStart={c1AtStart}  c2AtStart={c2AtStart} closed={closedSourceCurve != null}");

    // Extend working copies to the virtual corner so chamfering always works
    // even when the curves are too short (e.g. previously chamfered corner).
    Curve work1 = ExtendToCorner(crv1, c1AtStart, corner);
    Curve work2 = ExtendToCorner(crv2, c2AtStart, corner);

    // Initial length: use stored _length. Click positions are only used for corner detection above.
    double runLength = _length;
    Log.Write("vChamfer", $"RunCommand  runLength={runLength:G4}");

    ComputeChamfer(work1, c1AtStart, work2, c2AtStart, runLength,
      out var ptA, out var ptB, out var tA, out var tB);

    var conduit = new ChamferPreviewConduit();
    if (ptA.IsValid && ptB.IsValid)
      UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
    else
    {
      conduit.ShowTrim = _trim;
      RhinoApp.WriteLine("vChamfer: length too large - adjust the Length option.");
    }
    conduit.Enabled = true;
    doc.Views.Redraw();

    bool pointActive = false;
    Point3d pickedReferencePoint = Point3d.Unset;

    try
    {
      while (true)
      {
        var get = new GetPoint();
        get.EnableTransparentCommands(true);
        get.SetCommandPrompt(pointActive
          ? "Chamfer placed at point - Enter to apply"
          : "Press Enter to apply chamfer; pick a point to place at Length distance from point");
        get.AcceptNothing(true);
        get.AcceptNumber(true, true);
        var lengthOpt = new OptionDouble(runLength, 0.0, double.MaxValue);
        var idxLength = get.AddOptionDouble("Length", ref lengthOpt);
        var trimOpt = new OptionToggle(_trim, "No", "Yes");
        get.AddOptionToggle("Trim", ref trimOpt);
        var joinOpt = new OptionToggle(_join, "No", "Yes");
        if (_trim) get.AddOptionToggle("Join", ref joinOpt);
        int idxClearPoint = pointActive ? get.AddOption("ClearPoint") : -1;

        var res = get.Get();

        if (res == GetResult.Cancel)
          return Result.Cancel;

        if (res == GetResult.Point)
        {
          var pickedPt = get.Point();
          Log.Write("vChamfer", $"PointPick  click={P(pickedPt)}  currentPtA={P(ptA.IsValid?(Point3d?)ptA:null)}");

          pickedReferencePoint = pickedPt;
          pointActive = true;

          if (!ComputeChamferFromPoint(
                work1, c1AtStart, work2, c2AtStart,
                pickedPt, runLength,
                out ptA, out ptB, out tA, out tB))
          {
            conduit.Clear(_trim);
            doc.Views.Redraw();
            RhinoApp.WriteLine("vChamfer: cannot place the chamfer that far from the point.");
            continue;
          }

          UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
          doc.Views.Redraw();
          continue;
        }

        if (res == GetResult.Nothing)
        {
          if (!ptA.IsValid || !ptB.IsValid)
          {
            RhinoApp.WriteLine("vChamfer: no valid chamfer was created.");
            return Result.Nothing;
          }
          break;
        }

        if (res == GetResult.Option && idxClearPoint >= 0 && get.Option()?.Index == idxClearPoint)
        {
          pointActive = false;
          pickedReferencePoint = Point3d.Unset;
          if (ComputeChamfer(work1, c1AtStart, work2, c2AtStart, runLength,
                out ptA, out ptB, out tA, out tB))
            UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
          else
            conduit.Clear(_trim);
          doc.Views.Redraw();
          continue;
        }

        if (res == GetResult.Number)
        {
          var v = get.Number();
          if (TrySetLength(v))
          {
            runLength = _length;
            if (!pointActive) pointActive = false;  // stay active if offset was set
            SaveOptions();
          }
        }
        else if (res == GetResult.Option)
        {
          var option = get.Option();
          _trim = trimOpt.CurrentValue;
          if (_trim) _join = joinOpt.CurrentValue;

          if (option?.Index == idxLength && TrySetLength(lengthOpt.CurrentValue))
            runLength = _length;  // keep pointActive as-is

          SaveOptions();
        }

        if (res == GetResult.Number || res == GetResult.Option)
        {
          if (pointActive && pickedReferencePoint.IsValid)
          {
            if (ComputeChamferFromPoint(
                  work1, c1AtStart, work2, c2AtStart,
                  pickedReferencePoint, runLength,
                  out ptA, out ptB, out tA, out tB))
            {
              UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
            }
            else
            {
              conduit.Clear(_trim);
              RhinoApp.WriteLine("vChamfer: cannot place the chamfer that far from the point.");
            }
          }
          else
          {
            if (ComputeChamfer(work1, c1AtStart, work2, c2AtStart, runLength,
                  out ptA, out ptB, out tA, out tB))
              UpdateConduit(conduit, crv1, work1, c1AtStart, crv2, work2, c2AtStart, tA, tB, ptA, ptB);
            else
            {
              conduit.Clear(_trim);
              RhinoApp.WriteLine("vChamfer: length too large for this corner.");
            }
          }

          doc.Views.Redraw();
        }
      }
    }
    finally
    {
      conduit.Enabled = false;
      doc.Views.Redraw();
    }

    // Apply.
    if (!ptA.IsValid || !ptB.IsValid) return Result.Cancel;

    // If input curves share a group, new geometry (chamfer line, extension stubs) inherits it.
    var groupList1 = ref1.Object()?.Attributes.GetGroupList() ?? Array.Empty<int>();
    var groupList2 = ref2.Object()?.Attributes.GetGroupList() ?? Array.Empty<int>();
    int sharedGroup = -1;
    foreach (var g in groupList1)
      if (Array.IndexOf(groupList2, g) >= 0) { sharedGroup = g; break; }
    if (sharedGroup < 0 && groupList1.Length > 0) sharedGroup = groupList1[0];

    var hasChamferLine = ptA.DistanceTo(ptB) > doc.ModelAbsoluteTolerance;

    if (closedSourceCurve != null)
    {
      Curve? replacement = null;
      if (_trim && hasChamferLine &&
          !TryBuildClosedChamferReplacement(
            doc,
            closedSourceCurve,
            corner,
            ptA,
            ptB,
            _join,
            out replacement))
      {
        RhinoApp.WriteLine("vChamfer: closed curve trim failed.");
        return Result.Failure;
      }

      var closedChamferLineId = hasChamferLine && (!_trim || !_join)
        ? doc.Objects.AddLine(ptA, ptB)
        : Guid.Empty;
      if (hasChamferLine && (!_trim || !_join) && closedChamferLineId == Guid.Empty)
      {
        replacement?.Dispose();
        RhinoApp.WriteLine("vChamfer: failed to create the chamfer line.");
        return Result.Failure;
      }

      if (replacement != null)
      {
        if (!doc.Objects.Replace(ref1.ObjectId, replacement))
        {
          if (closedChamferLineId != Guid.Empty)
            doc.Objects.Delete(closedChamferLineId, quiet: true);
          replacement.Dispose();
          RhinoApp.WriteLine("vChamfer: failed to replace the closed curve.");
          return Result.Failure;
        }
        replacement.Dispose();
      }

      if (sharedGroup >= 0 && closedChamferLineId != Guid.Empty)
        doc.Groups.AddToGroup(sharedGroup, closedChamferLineId);

      Log.Write(
        "vChamfer",
        $"ClosedCorner applied source={ref1.ObjectId} trim={_trim} join={_join} " +
        $"line={closedChamferLineId} pt1={P(ptA)} pt2={P(ptB)}");
      SaveOptions();
      doc.Views.Redraw();
      return Result.Success;
    }

    var chamferLineId = hasChamferLine ? doc.Objects.AddLine(ptA, ptB) : Guid.Empty;

    // With Trim=No, add extension lines for any gap between curve ends and virtual corner.
    var ext1Id = Guid.Empty;
    var ext2Id = Guid.Empty;
    if (!_trim)
    {
      var c1CornerEnd = c1AtStart ? crv1.PointAtStart : crv1.PointAtEnd;
      var w1CornerEnd = c1AtStart ? work1.PointAtStart : work1.PointAtEnd;
      if (c1CornerEnd.DistanceTo(w1CornerEnd) > doc.ModelAbsoluteTolerance
          && c1CornerEnd.DistanceTo(ptA) > doc.ModelAbsoluteTolerance)
        ext1Id = doc.Objects.AddLine(c1CornerEnd, ptA);

      var c2CornerEnd = c2AtStart ? crv2.PointAtStart : crv2.PointAtEnd;
      var w2CornerEnd = c2AtStart ? work2.PointAtStart : work2.PointAtEnd;
      if (c2CornerEnd.DistanceTo(w2CornerEnd) > doc.ModelAbsoluteTolerance
          && c2CornerEnd.DistanceTo(ptB) > doc.ModelAbsoluteTolerance)
        ext2Id = doc.Objects.AddLine(c2CornerEnd, ptB);
    }

    if (_trim)
    {
      var trimmedC1 = c1AtStart
        ? work1.Trim(tA, work1.Domain.Max)
        : work1.Trim(work1.Domain.Min, tA);

      var trimmedC2 = c2AtStart
        ? work2.Trim(tB, work2.Domain.Max)
        : work2.Trim(work2.Domain.Min, tB);

      if (trimmedC1 == null || trimmedC2 == null)
      {
        RhinoApp.WriteLine("vChamfer: curve trim failed.");
        return Result.Failure;
      }

      doc.Objects.Replace(ref1.ObjectId, trimmedC1);
      doc.Objects.Replace(ref2.ObjectId, trimmedC2);

      if (_join)
      {
        var tol = doc.ModelAbsoluteTolerance;
        var joinCurves = hasChamferLine
          ? new Curve[] { trimmedC1, new LineCurve(ptA, ptB), trimmedC2 }
          : new Curve[] { trimmedC1, trimmedC2 };
        var joined = Curve.JoinCurves(joinCurves, tol);
        if (joined != null && joined.Length == 1)
        {
          // Replace the chamfer line and both trimmed curves with the single joined result.
          if (hasChamferLine)
            doc.Objects.Delete(chamferLineId, quiet: true);
          doc.Objects.Replace(ref1.ObjectId, joined[0]);
          doc.Objects.Delete(ref2.ObjectId, quiet: true);
          chamferLineId = Guid.Empty;  // consumed by join
        }
        // If join failed (not contiguous), leave the three separate objects.
      }
    }

    // If input curves were in a group, add any new standalone geometry to the same group.
    if (sharedGroup >= 0)
    {
      if (chamferLineId != Guid.Empty)
        doc.Groups.AddToGroup(sharedGroup, chamferLineId);
      if (ext1Id != Guid.Empty) doc.Groups.AddToGroup(sharedGroup, ext1Id);
      if (ext2Id != Guid.Empty) doc.Groups.AddToGroup(sharedGroup, ext2Id);
    }

    SaveOptions();
    doc.Views.Redraw();
    return Result.Success;
  }

  private readonly record struct ClosedCornerCandidate(
    double Parameter,
    Point3d Point);
}
