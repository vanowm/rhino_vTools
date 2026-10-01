using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed partial class vLine
{
  // 3Point solver defaults and interaction limits.
  private const int ThreePointPreviewSamples = 9; // Parameter samples per unconstrained curve during live preview; at least 2.
  private const int ThreePointFinalSamples = 17; // Parameter samples per unconstrained curve on click; at least 2.
  private const int ThreePointPreviewSeeds = 3; // Distinct low-error seeds refined during preview; one or greater.
  private const int ThreePointFinalSeeds = 9; // Distinct low-error seeds refined on click; one or greater.
  private const int ThreePointPreviewLevels = 12; // Successive local step halvings during preview; one or greater.
  private const int ThreePointFinalLevels = 16; // Successive local step halvings on click; one or greater.
  private const int ThreePointPreviewIntervalMs = 45; // Minimum interval between cursor-dependent numeric preview solves, in milliseconds.
  private const double ThreePointMinimumTolerance = 1e-6; // Model-unit floor for 3D line/curve contact validation.
  private const double ThreePointSeedSeparation = 0.07; // Minimum normalized parameter separation for independent refinement seeds.
  private static readonly Color ThreePointLinePreviewColor = Color.FromArgb(255, 255, 205, 65); // Temporary solved-line color.
  private static readonly Color ThreePointContactPreviewColor = Color.White; // Temporary 3D crossing point color.

  private readonly record struct ThreePointConstraint(
    Curve? Curve,
    Point3d Point,
    Guid ObjectId,
    ComponentIndex ComponentIndex,
    Point3d PickPoint)
  {
    public bool IsCurve => Curve != null;

    public static ThreePointConstraint FromPoint(Point3d point) =>
      new(null, point, Guid.Empty, ComponentIndex.Unset, point);

    public static ThreePointConstraint FromCurve(
      Curve curve, ScreenCurvePick pick) =>
      new(curve, Point3d.Unset, pick.ObjectId, pick.ComponentIndex, pick.PickPoint);
  }

  private readonly record struct ThreePointSolution(
    Point3d Start, Point3d End, Point3d Through, double Gap);

  private readonly record struct ThreePointCandidate(
    double StartRatio, double EndRatio, double TouchRatio,
    ThreePointSolution Solution, double CursorDistance);

  private static bool RunThreePoint(
    RhinoDoc doc, LineLayerSession layerSession, Point3d? fixedStart = null)
  {
    var selected = new List<ThreePointConstraint>(3);
    if (fixedStart.HasValue)
      selected.Add(ThreePointConstraint.FromPoint(fixedStart.Value));
    ThreePointSolution? finalSolution = null;
    using var highlighter = new PreviewDisplay.ObjectHighlighter(doc);
    try
    {
      while (selected.Count < 3)
      {
        var picked = PickThreePointConstraint(
          doc, layerSession, selected, highlighter, out var acceptedSolution);
        if (!picked.HasValue)
          return false;
        selected.Add(picked.Value);
        highlighter.SetObjects(selected.Where(item => item.IsCurve)
          .Select(item => item.ObjectId));
        Log.Write("vLine.3Point",
          $"constraint={selected.Count} kind={(picked.Value.IsCurve ? "curve" : "point")} " +
          $"id={picked.Value.ObjectId} pick={picked.Value.PickPoint}");
        if (selected.Count == 3)
          finalSolution = acceptedSolution;
      }

      var cursor = selected[2].PickPoint;
      var needsOrientation = !selected[2].IsCurve &&
        (selected[0].IsCurve && selected[1].IsCurve ||
         !selected[0].IsCurve && selected[1].IsCurve &&
           selected[0].Point.DistanceTo(cursor) <= doc.ModelAbsoluteTolerance ||
         !selected[1].IsCurve && selected[0].IsCurve &&
           selected[1].Point.DistanceTo(cursor) <= doc.ModelAbsoluteTolerance);
      var solution = needsOrientation
        ? PickThreePointOrientation(doc, layerSession,
          selected[0], selected[1], cursor)
        : finalSolution ?? SolveThreePoint(doc,
          selected[0], selected[1], selected[2], cursor, preview: false);
      if (!solution.HasValue ||
          solution.Value.Start.DistanceTo(solution.Value.End) <=
            Math.Max(doc.ModelAbsoluteTolerance, ThreePointMinimumTolerance))
      {
        RhinoApp.WriteLine("vLine: no valid 3D line satisfies those three constraints.");
        return false;
      }

      var line = new Line(solution.Value.Start, solution.Value.End);
      var id = doc.Objects.AddLine(line, layerSession.CreateAttributes(doc));
      if (id == Guid.Empty)
        return false;
      Log.Write("vLine.3Point",
        $"created id={id} start={line.From} end={line.To} " +
        $"through={solution.Value.Through} gap={solution.Value.Gap:G6}");
      doc.Views.Redraw();
      return true;
    }
    finally
    {
      highlighter.SetObjects(Array.Empty<Guid>());
      foreach (var constraint in selected)
        constraint.Curve?.Dispose();
    }
  }

  private static ThreePointConstraint? PickThreePointConstraint(
    RhinoDoc doc,
    LineLayerSession layerSession,
    IReadOnlyList<ThreePointConstraint> selected,
    PreviewDisplay.ObjectHighlighter highlighter,
    out ThreePointSolution? acceptedSolution)
  {
    acceptedSolution = null;
    using var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    var pointMode = false;
    ScreenCurvePick? hovered = null;
    Curve? hoveredCurve = null;
    List<ThreePointSolution>? cachedFixedSolutions = null;
    ThreePointSolution? previewSolution = null;
    var lastPreviewUtc = DateTime.MinValue;
    var selectedIds = selected.Where(item => item.IsCurve)
      .Select(item => item.ObjectId).ToArray();

    void SetHover(ScreenCurvePick? pick)
    {
      if (hovered?.ObjectId == pick?.ObjectId &&
          hovered?.ComponentIndex == pick?.ComponentIndex)
      {
        hovered = pick;
        return;
      }

      hovered = pick;
      hoveredCurve = pick.HasValue ? CurveFromScreenPick(doc, pick.Value) : null;
      cachedFixedSolutions = null;
      previewSolution = null;
      lastPreviewUtc = DateTime.MinValue;
      if (selected.Count == 2 && hoveredCurve != null &&
          selected.Count(item => !item.IsCurve) == 1)
      {
        var fixedPoint = selected[0].IsCurve ? selected[1].Point : selected[0].Point;
        var endCurve = (selected[0].IsCurve ? selected[0] : selected[1]).Curve!;
        var watch = Stopwatch.StartNew();
        cachedFixedSolutions = FixedPointCrossingSolutions(
          doc, fixedPoint, endCurve, hoveredCurve,
          fixedIsStart: !selected[0].IsCurve);
        Log.Write("vLine.3Point",
          $"hover solve curve={pick?.ObjectId} solutions={cachedFixedSolutions.Count} " +
          $"elapsedMs={watch.ElapsedMilliseconds}");
      }
      highlighter.SetObjects(pick.HasValue
        ? selectedIds.Append(pick.Value.ObjectId)
        : selectedIds);
    }

    getPoint.MouseMove += (_, e) =>
    {
      if (pointMode)
        return;
      var pick = PickCurveAtScreenPoint(
        doc, e.Viewport, e.WindowPoint, out var diagnostic);
      SetHover(pick);
    };

    getPoint.DynamicDraw += (_, e) =>
    {
      DrawHiddenLayerWarning(e, doc, layerSession);
      if (selected.Count == 0)
        return;

      if (selected.Count == 1)
      {
        var first = selected[0].IsCurve
          ? ClosestPointOnThreePointCurve(selected[0].Curve!, e.CurrentPoint)
          : selected[0].Point;
        var second = pointMode ? e.CurrentPoint : hovered?.PickPoint ?? Point3d.Unset;
        if (first.IsValid && second.IsValid)
          PreviewDisplay.DrawLine(e.Display, new Line(first, second),
            ThreePointLinePreviewColor);
        return;
      }

      var cursor = pointMode ? e.CurrentPoint : hovered?.PickPoint ?? e.CurrentPoint;
      if (pointMode)
      {
        if (DateTime.UtcNow - lastPreviewUtc >=
            TimeSpan.FromMilliseconds(ThreePointPreviewIntervalMs))
        {
          previewSolution = SolveThreePoint(doc, selected[0], selected[1],
            ThreePointConstraint.FromPoint(cursor), cursor, preview: true);
          lastPreviewUtc = DateTime.UtcNow;
        }
      }
      else if (hoveredCurve != null && hovered.HasValue)
      {
        if (cachedFixedSolutions != null)
        {
          if (cachedFixedSolutions.Count > 0)
            previewSolution = ClosestThreePointSolution(cachedFixedSolutions, cursor);
          else if (DateTime.UtcNow - lastPreviewUtc >=
                   TimeSpan.FromMilliseconds(ThreePointPreviewIntervalMs))
          {
            previewSolution = NumericThreePointSolution(doc,
              selected[0], selected[1],
              ThreePointConstraint.FromCurve(hoveredCurve, hovered.Value),
              cursor, preview: true);
            lastPreviewUtc = DateTime.UtcNow;
          }
        }
        else if (DateTime.UtcNow - lastPreviewUtc >=
                 TimeSpan.FromMilliseconds(ThreePointPreviewIntervalMs))
        {
          previewSolution = SolveThreePoint(doc, selected[0], selected[1],
            ThreePointConstraint.FromCurve(hoveredCurve, hovered.Value),
            cursor, preview: true);
          lastPreviewUtc = DateTime.UtcNow;
        }
      }

      if (previewSolution.HasValue)
      {
        var solution = previewSolution.Value;
        PreviewDisplay.DrawLine(e.Display,
          new Line(solution.Start, solution.End), ThreePointLinePreviewColor);
        e.Display.DrawPoint(solution.Through, ThreePointContactPreviewColor);
      }
    };

    while (true)
    {
      var ordinal = selected.Count == 0 ? "first end" :
        selected.Count == 1 ? "second end" : "crossing";
      getPoint.SetCommandPrompt(layerSession.DecoratePrompt(doc,
        pointMode ? $"Pick exact {ordinal} point" :
          $"Select {ordinal} curve or choose Point"));
      getPoint.ClearCommandOptions();
      var toggle = getPoint.AddOption(pointMode ? "Curve" : "Point");
      var result = getPoint.Get();
      layerSession.ObserveCurrentLayer(doc);
      if (result == GetResult.Option &&
          getPoint.Option()?.Index == toggle)
      {
        pointMode = !pointMode;
        if (pointMode)
          SetHover(null);
        previewSolution = null;
        lastPreviewUtc = DateTime.MinValue;
        continue;
      }
      if (result != GetResult.Point)
        return null;

      if (pointMode)
        return ThreePointConstraint.FromPoint(getPoint.Point());

      if (!hovered.HasValue || hoveredCurve == null)
      {
        RhinoApp.WriteLine("vLine: move over a curve to select it.");
        continue;
      }

      var curve = hoveredCurve.DuplicateCurve();
      if (curve == null)
        continue;
      var candidate = ThreePointConstraint.FromCurve(curve, hovered.Value);
      if (selected.Count == 2)
      {
        acceptedSolution = cachedFixedSolutions is { Count: > 0 }
          ? ClosestThreePointSolution(cachedFixedSolutions, hovered.Value.PickPoint)
          : SolveThreePoint(doc, selected[0], selected[1], candidate,
            hovered.Value.PickPoint, preview: false);
        if (!acceptedSolution.HasValue)
        {
          curve.Dispose();
          RhinoApp.WriteLine("vLine: no valid 3D line there; try another curve or point.");
          continue;
        }
      }
      return candidate;
    }
  }

  private static Point3d ClosestPointOnThreePointCurve(Curve curve, Point3d hint) =>
    curve.ClosestPoint(hint, out var parameter)
      ? curve.PointAt(parameter)
      : curve.PointAtStart;

  private static ThreePointSolution? ClosestThreePointSolution(
    IReadOnlyList<ThreePointSolution> solutions, Point3d cursor)
  {
    if (solutions.Count == 0)
      return null;
    return solutions.OrderBy(item => item.Through.DistanceToSquared(cursor))
      .First();
  }

  private static ThreePointSolution? SolveThreePoint(
    RhinoDoc doc,
    ThreePointConstraint start,
    ThreePointConstraint end,
    ThreePointConstraint touch,
    Point3d cursor,
    bool preview)
  {
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance,
      ThreePointMinimumTolerance);
    if (!start.IsCurve && !end.IsCurve)
    {
      if (touch.Curve == null)
      {
        var gap = PointToThreePointSegmentGap(
          touch.Point, start.Point, end.Point, tolerance);
        return gap <= tolerance
          ? new ThreePointSolution(start.Point, end.Point, touch.Point, gap)
          : null;
      }
      return ClosestThreePointSolution(
        FixedEndpointsCrossingSolutions(start.Point, end.Point,
          touch.Curve, tolerance), cursor);
    }

    if (touch.Curve == null && start.IsCurve != end.IsCurve)
    {
      var fixedPoint = start.IsCurve ? end.Point : start.Point;
      var endCurve = (start.IsCurve ? start : end).Curve!;
      return ClosestThreePointSolution(
        FixedPointThroughPointSolutions(fixedPoint, endCurve,
          touch.Point, !start.IsCurve, tolerance), cursor);
    }

    if (touch.Curve != null && start.IsCurve != end.IsCurve)
    {
      var fixedPoint = start.IsCurve ? end.Point : start.Point;
      var endCurve = (start.IsCurve ? start : end).Curve!;
      var solutions = FixedPointCrossingSolutions(doc,
        fixedPoint, endCurve, touch.Curve, !start.IsCurve);
      var exact = ClosestThreePointSolution(solutions, cursor);
      if (exact.HasValue || preview)
        return exact;
    }

    var numeric = NumericThreePointSolution(doc,
      start, end, touch, cursor, preview);
    if (numeric.HasValue && !preview)
      Log.Write("vLine.3Point",
        $"numeric solve preview={preview} gap={numeric.Value.Gap:G6}");
    return numeric;
  }

  private static List<ThreePointSolution> FixedEndpointsCrossingSolutions(
    Point3d start, Point3d end, Curve touchCurve, double tolerance)
  {
    var solutions = new List<ThreePointSolution>();
    if (start.DistanceTo(end) <= tolerance)
      return solutions;
    var hits = Intersection.CurveLine(touchCurve,
      new Line(start, end), tolerance, tolerance);
    if (hits == null)
      return solutions;
    foreach (var hit in hits)
    {
      void Accept(Point3d through)
      {
        var gap = PointToThreePointSegmentGap(through,
          start, end, tolerance);
        if (gap <= tolerance && !solutions.Any(item =>
            item.Through.DistanceTo(through) <= tolerance))
          solutions.Add(new ThreePointSolution(start, end, through, gap));
      }
      Accept(hit.PointA);
      if (hit.IsOverlap)
        Accept(hit.PointA2);
    }
    return solutions;
  }

  private static List<ThreePointSolution> FixedPointThroughPointSolutions(
    Point3d fixedPoint, Curve endCurve, Point3d through,
    bool fixedIsStart, double tolerance)
  {
    var solutions = new List<ThreePointSolution>();
    if (fixedPoint.DistanceTo(through) <= tolerance)
      return solutions;
    var hits = Intersection.CurveLine(endCurve,
      new Line(fixedPoint, through), tolerance, tolerance);
    if (hits == null)
      return solutions;
    foreach (var hit in hits)
    {
      void Accept(Point3d endPoint)
      {
        var gap = PointToThreePointSegmentGap(through,
          fixedPoint, endPoint, tolerance);
        if (gap > tolerance || solutions.Any(item =>
            (fixedIsStart ? item.End : item.Start)
              .DistanceTo(endPoint) <= tolerance))
          return;
        solutions.Add(fixedIsStart
          ? new ThreePointSolution(fixedPoint, endPoint, through, gap)
          : new ThreePointSolution(endPoint, fixedPoint, through, gap));
      }
      Accept(hit.PointA);
      if (hit.IsOverlap)
        Accept(hit.PointA2);
    }
    return solutions;
  }

  private static ThreePointSolution? PickThreePointOrientation(
    RhinoDoc doc, LineLayerSession layerSession,
    ThreePointConstraint start, ThreePointConstraint end,
    Point3d through)
  {
    using var getPoint = new GetPoint();
    getPoint.EnableTransparentCommands(true);
    getPoint.SetBasePoint(through, true);
    getPoint.DrawLineFromPoint(through, true);
    getPoint.SetCommandPrompt(layerSession.DecoratePrompt(doc,
      "Pick a direction through the crossing point"));
    getPoint.DynamicDraw += (_, e) =>
    {
      DrawHiddenLayerWarning(e, doc, layerSession);
      var preview = SolveThreePointDirection(doc,
        start, end, through, e.CurrentPoint);
      if (preview.HasValue)
        PreviewDisplay.DrawLine(e.Display,
          new Line(preview.Value.Start, preview.Value.End),
          ThreePointLinePreviewColor);
    };
    while (true)
    {
      if (getPoint.Get() != GetResult.Point)
        return null;
      var solution = SolveThreePointDirection(doc,
        start, end, through, getPoint.Point());
      if (solution.HasValue)
        return solution;
      RhinoApp.WriteLine("vLine: no line through those constraints in that direction.");
    }
  }

  private static ThreePointSolution? SolveThreePointDirection(
    RhinoDoc doc, ThreePointConstraint start,
    ThreePointConstraint end, Point3d through, Point3d directionPoint)
  {
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance,
      ThreePointMinimumTolerance);
    if (through.DistanceTo(directionPoint) <= tolerance)
      return null;
    var line = new Line(through, directionPoint);
    var startPoints = ThreePointDirectionHits(start, line, tolerance);
    var endPoints = ThreePointDirectionHits(end, line, tolerance);
    var solutions = new List<ThreePointSolution>();
    foreach (var a in startPoints)
      foreach (var b in endPoints)
      {
        var gap = PointToThreePointSegmentGap(through,
          a, b, tolerance);
        if (gap > tolerance)
          continue;
        var fixedAtThrough = (!start.IsCurve &&
            start.Point.DistanceTo(through) <= tolerance) ||
          (!end.IsCurve && end.Point.DistanceTo(through) <= tolerance);
        if (fixedAtThrough && Vector3d.Multiply(
              (start.IsCurve ? a : b) - through,
              directionPoint - through) <= 0)
          continue;
        solutions.Add(new ThreePointSolution(a, b, through, gap));
      }
    return solutions.Count == 0 ? null : solutions
      .OrderBy(item => Math.Min(
        item.Start.DistanceToSquared(directionPoint),
        item.End.DistanceToSquared(directionPoint)))
      .First();
  }

  private static IReadOnlyList<Point3d> ThreePointDirectionHits(
    ThreePointConstraint constraint, Line line, double tolerance)
  {
    if (constraint.Curve == null)
      return [constraint.Point];
    var points = new List<Point3d>();
    var hits = Intersection.CurveLine(constraint.Curve, line,
      tolerance, tolerance);
    if (hits == null)
      return points;
    foreach (var hit in hits)
    {
      points.Add(hit.PointA);
      if (hit.IsOverlap)
        points.Add(hit.PointA2);
    }
    return points;
  }

  private static List<ThreePointSolution> FixedPointCrossingSolutions(
    RhinoDoc doc, Point3d fixedPoint, Curve endCurve,
    Curve touchCurve, bool fixedIsStart)
  {
    var solutions = new List<ThreePointSolution>();
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance,
      ThreePointMinimumTolerance);
    using var fan = Surface.CreateExtrusionToPoint(endCurve, fixedPoint);
    if (fan == null)
      return solutions;
    var events = Intersection.CurveSurface(touchCurve, fan,
      tolerance, tolerance);
    if (events == null)
      return solutions;

    void AddFromTouchPoint(Point3d through)
    {
      if (!through.IsValid ||
          through.DistanceTo(fixedPoint) <= tolerance)
        return;
      var ray = new Line(fixedPoint, through);
      var hits = Intersection.CurveLine(endCurve, ray,
        tolerance, tolerance);
      if (hits == null)
        return;
      foreach (var hit in hits)
      {
        void Accept(Point3d endPoint)
        {
          var gap = PointToThreePointSegmentGap(
            through, fixedPoint, endPoint, tolerance);
          if (gap > tolerance || solutions.Any(item =>
              (fixedIsStart ? item.End : item.Start)
                .DistanceTo(endPoint) <= tolerance &&
              item.Through.DistanceTo(through) <= tolerance))
            return;
          solutions.Add(fixedIsStart
            ? new ThreePointSolution(fixedPoint, endPoint, through, gap)
            : new ThreePointSolution(endPoint, fixedPoint, through, gap));
        }

        Accept(hit.PointA);
        if (hit.IsOverlap)
          Accept(hit.PointA2);
      }
    }

    foreach (var intersection in events)
    {
      AddFromTouchPoint(intersection.PointA);
      if (intersection.IsOverlap)
      {
        AddFromTouchPoint(touchCurve.PointAt(intersection.OverlapA.Mid));
        AddFromTouchPoint(intersection.PointA2);
      }
    }
    return solutions;
  }

  private static ThreePointSolution? NumericThreePointSolution(
    RhinoDoc doc,
    ThreePointConstraint start,
    ThreePointConstraint end,
    ThreePointConstraint touch,
    Point3d cursor,
    bool preview)
  {
    var tolerance = Math.Max(doc.ModelAbsoluteTolerance,
      ThreePointMinimumTolerance);
    var sampleCount = preview
      ? ThreePointPreviewSamples : ThreePointFinalSamples;
    var starts = ThreePointSampleRatios(start, sampleCount, start.PickPoint);
    var ends = ThreePointSampleRatios(end, sampleCount, end.PickPoint);
    var touches = ThreePointSampleRatios(touch, sampleCount, cursor);
    var startPoints = starts.Select(ratio => ThreePointAtRatio(start, ratio)).ToArray();
    var endPoints = ends.Select(ratio => ThreePointAtRatio(end, ratio)).ToArray();
    var touchPoints = touches.Select(ratio => ThreePointAtRatio(touch, ratio)).ToArray();
    var seeds = new List<ThreePointCandidate>(
      starts.Length * ends.Length * touches.Length);
    for (var i = 0; i < starts.Length; i++)
      for (var j = 0; j < ends.Length; j++)
        for (var k = 0; k < touches.Length; k++)
        {
          var candidate = EvaluateThreePointPoints(
            starts[i], ends[j], touches[k],
            startPoints[i], endPoints[j], touchPoints[k],
            touch.IsCurve, cursor, tolerance);
          if (candidate.HasValue)
            seeds.Add(candidate.Value);
        }
    if (seeds.Count == 0)
      return null;
    seeds.Sort((a, b) =>
    {
      var gap = a.Solution.Gap.CompareTo(b.Solution.Gap);
      return gap != 0 ? gap : a.CursorDistance.CompareTo(b.CursorDistance);
    });

    var selectedSeeds = new List<ThreePointCandidate>();
    var seedLimit = preview ? ThreePointPreviewSeeds : ThreePointFinalSeeds;
    foreach (var seed in seeds)
    {
      if (selectedSeeds.Any(existing =>
          Math.Abs(seed.StartRatio - existing.StartRatio) < ThreePointSeedSeparation &&
          Math.Abs(seed.EndRatio - existing.EndRatio) < ThreePointSeedSeparation &&
          Math.Abs(seed.TouchRatio - existing.TouchRatio) < ThreePointSeedSeparation))
        continue;
      selectedSeeds.Add(seed);
      if (selectedSeeds.Count == seedLimit)
        break;
    }

    var levels = preview ? ThreePointPreviewLevels : ThreePointFinalLevels;
    ThreePointCandidate? best = null;
    foreach (var seed in selectedSeeds)
    {
      var refined = RefineThreePointCandidate(start, end, touch,
        seed, cursor, tolerance, sampleCount, levels);
      if (!best.HasValue || BetterThreePointCandidate(refined, best.Value, tolerance))
        best = refined;
    }
    return best.HasValue && best.Value.Solution.Gap <= tolerance
      ? best.Value.Solution : null;
  }

  private static double[] ThreePointSampleRatios(
    ThreePointConstraint constraint, int count, Point3d hint)
  {
    if (constraint.Curve == null)
      return [0.0];
    var samples = new List<double>(count + 1);
    for (var index = 0; index < count; index++)
      samples.Add((double)index / (count - 1));
    if (constraint.Curve.ClosestPoint(hint, out var parameter))
    {
      var domain = constraint.Curve.Domain;
      if (domain.Length > 0)
        samples.Add(Math.Clamp((parameter - domain.T0) / domain.Length, 0.0, 1.0));
    }
    return samples.Distinct().ToArray();
  }

  private static Point3d ThreePointAtRatio(
    ThreePointConstraint constraint, double ratio)
  {
    if (constraint.Curve == null)
      return constraint.Point;
    var domain = constraint.Curve.Domain;
    return constraint.Curve.PointAt(
      domain.T0 + (domain.T1 - domain.T0) * ratio);
  }

  private static ThreePointCandidate? EvaluateThreePointCandidate(
    ThreePointConstraint start, ThreePointConstraint end,
    ThreePointConstraint touch, double startRatio, double endRatio,
    double touchRatio, Point3d cursor, double tolerance)
  {
    var a = ThreePointAtRatio(start, startRatio);
    var b = ThreePointAtRatio(end, endRatio);
    var r = ThreePointAtRatio(touch, touchRatio);
    return EvaluateThreePointPoints(startRatio, endRatio, touchRatio,
      a, b, r, touch.IsCurve, cursor, tolerance);
  }

  private static ThreePointCandidate? EvaluateThreePointPoints(
    double startRatio, double endRatio, double touchRatio,
    Point3d a, Point3d b, Point3d r,
    bool touchIsCurve, Point3d cursor, double tolerance)
  {
    if (!a.IsValid || !b.IsValid || !r.IsValid ||
        a.DistanceToSquared(b) <= tolerance * tolerance)
      return null;
    var gap = PointToThreePointSegmentGap(r, a, b, tolerance);
    var cursorDistance = touchIsCurve
      ? cursor.DistanceTo(r)
      : Math.Min(cursor.DistanceTo(a), cursor.DistanceTo(b));
    return new ThreePointCandidate(startRatio, endRatio, touchRatio,
      new ThreePointSolution(a, b, r, gap), cursorDistance);
  }

  private static double PointToThreePointSegmentGap(
    Point3d point, Point3d start, Point3d end, double tolerance)
  {
    var segment = end - start;
    var squared = segment.SquareLength;
    if (squared <= tolerance * tolerance)
      return double.PositiveInfinity;
    var position = Vector3d.Multiply(point - start, segment) / squared;
    var closest = start + segment * Math.Clamp(position, 0.0, 1.0);
    return point.DistanceTo(closest);
  }

  private static bool BetterThreePointCandidate(
    ThreePointCandidate candidate, ThreePointCandidate best, double tolerance)
  {
    if (candidate.Solution.Gap <= tolerance &&
        best.Solution.Gap <= tolerance)
      return candidate.CursorDistance < best.CursorDistance;
    if (candidate.Solution.Gap < best.Solution.Gap - tolerance * 1e-3)
      return true;
    return Math.Abs(candidate.Solution.Gap - best.Solution.Gap) <=
      tolerance * 1e-3 && candidate.CursorDistance < best.CursorDistance;
  }

  private static ThreePointCandidate RefineThreePointCandidate(
    ThreePointConstraint start, ThreePointConstraint end,
    ThreePointConstraint touch, ThreePointCandidate seed,
    Point3d cursor, double tolerance, int sampleCount, int levels)
  {
    var best = seed;
    var step = 1.0 / (sampleCount - 1);
    for (var level = 0; level < levels; level++)
    {
      for (var move = 0; move < 2; move++)
      {
        var next = best;
        for (var da = -1; da <= 1; da++)
          for (var db = -1; db <= 1; db++)
            for (var dr = -1; dr <= 1; dr++)
            {
              if (da == 0 && db == 0 && dr == 0 ||
                  start.Curve == null && da != 0 ||
                  end.Curve == null && db != 0 ||
                  touch.Curve == null && dr != 0)
                continue;
              var candidate = EvaluateThreePointCandidate(
                start, end, touch,
                ThreePointStepRatio(start, best.StartRatio, da * step),
                ThreePointStepRatio(end, best.EndRatio, db * step),
                ThreePointStepRatio(touch, best.TouchRatio, dr * step),
                cursor, tolerance);
              if (candidate.HasValue &&
                  BetterThreePointCandidate(candidate.Value, next, tolerance))
                next = candidate.Value;
            }
        if (next.Equals(best))
          break;
        best = next;
      }
      step *= 0.5;
    }
    return best;
  }

  private static double ThreePointStepRatio(
    ThreePointConstraint constraint, double ratio, double offset)
  {
    if (constraint.Curve == null)
      return 0.0;
    var result = ratio + offset;
    return constraint.Curve.IsClosed
      ? (result % 1.0 + 1.0) % 1.0
      : Math.Clamp(result, 0.0, 1.0);
  }
}
