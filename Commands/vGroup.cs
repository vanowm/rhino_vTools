using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;
using Color = System.Drawing.Color;

namespace vTools.Commands;

/// <summary>
/// Groups selected objects by closed-curve boundaries.
/// </summary>
public sealed class vGroup : Command
{
  private static readonly HashSet<int> _ourGroupIndices = new();
  private static double _boundaryTolerance;
  private const string LogName = "vGroup";

  public override string EnglishName => "vGroup";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var selection = SelectObjects(doc);
    if (selection == null)
      return Result.Cancel;

    if (selection.Curves.Count == 0)
    {
      RhinoApp.WriteLine("vGroup: select at least one curve boundary.");
      return Result.Nothing;
    }

    var boundaryTolerance = _boundaryTolerance > 0.0
      ? _boundaryTolerance
      : DefaultBoundaryTolerance(doc);

    var solve = SolveBoundaries(doc, selection, boundaryTolerance, log: true);
    if (!ConfirmBoundaryTolerance(doc, selection, ref boundaryTolerance, ref solve))
      return Result.Cancel;

    _boundaryTolerance = boundaryTolerance;

    if (solve.Boundaries.Count == 0)
    {
      Log.Write(LogName, "No closed planar boundary found - see details above.");
      RhinoApp.WriteLine("vGroup: no closed planar boundary found in selection.");
      return Result.Nothing;
    }

    ClearPreviousGroups(doc);
    var groupCount = CreateGroups(doc, selection, solve);

    if (groupCount == 0)
      RhinoApp.WriteLine("vGroup: no enclosed objects found.");
    else
      RhinoApp.WriteLine($"vGroup: created {groupCount} group{(groupCount == 1 ? "" : "s")}.");

    doc.Views.Redraw();
    return Result.Success;
  }

  private static SelectionData? SelectObjects(RhinoDoc doc)
  {
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select objects to group by closed curve boundary");
    go.GroupSelect = true;
    go.SubObjectSelect = false;
    go.GetMultiple(1, 0);

    if (go.CommandResult() != Result.Success)
      return null;

    var result = new SelectionData();
    foreach (var objRef in go.Objects())
    {
      var id = objRef.ObjectId;
      if (id == Guid.Empty)
        continue;

      result.AllIds.Add(id);
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is not Curve curve)
        continue;

      result.Curves.Add(curve);
      result.CurveIds.Add(id);
    }

    return result;
  }

  private static bool ConfirmBoundaryTolerance(
    RhinoDoc doc,
    SelectionData selection,
    ref double boundaryTolerance,
    ref BoundarySolve solve)
  {
    var conduit = new BoundaryPreviewConduit { Solve = solve };
    conduit.Enabled = true;
    doc.Views.Redraw();

    try
    {
      var toleranceOption = new OptionDouble(boundaryTolerance, RhinoMath.ZeroTolerance, double.MaxValue);
      while (true)
      {
        var go = new GetOption();
        go.SetCommandPrompt("Adjust boundary tolerance. Press Enter to create groups");
        go.AcceptNothing(true);
        go.AddOptionDouble("Tolerance", ref toleranceOption);

        var result = go.Get();
        if (go.CommandResult() != Result.Success)
          return false;

        if (result == GetResult.Nothing)
          return true;

        if (result != GetResult.Option)
          return false;

        var nextTolerance = Math.Max(toleranceOption.CurrentValue, RhinoMath.ZeroTolerance);
        if (Math.Abs(nextTolerance - boundaryTolerance) <= RhinoMath.ZeroTolerance)
          continue;

        boundaryTolerance = nextTolerance;
        solve = SolveBoundaries(doc, selection, boundaryTolerance, log: true);
        conduit.Solve = solve;
        RhinoApp.WriteLine($"vGroup: {solve.Boundaries.Count} boundar{(solve.Boundaries.Count == 1 ? "y" : "ies")} found | Tolerance {boundaryTolerance:G}");
        doc.Views.Redraw();
      }
    }
    finally
    {
      conduit.Enabled = false;
      conduit.Solve = null;
      doc.Views.Redraw();
    }
  }

  private static BoundarySolve SolveBoundaries(
    RhinoDoc doc,
    SelectionData selection,
    double boundaryTolerance,
    bool log)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var solve = new BoundarySolve(boundaryTolerance);

    if (log)
    {
      Log.Write(LogName, $"--- run start --- tol={tol:G4} boundaryTol={boundaryTolerance:G4} curves={selection.Curves.Count} totalObjects={selection.AllIds.Count}");
      LogInputCurves(selection, tol);
    }

    if (selection.Curves.Count == 0)
      return solve;

    var splitParams = CollectSplitParameters(selection, tol, boundaryTolerance, log);
    SplitInputCurves(selection, splitParams, solve.CoreSegments, solve.CoreOriginIndices, log);
    TrimDeadEnds(solve.CoreSegments, solve.CoreOriginIndices, boundaryTolerance, log);
    JoinCoreSegments(solve, doc, tol, boundaryTolerance, log);
    BuildBoundaryMembers(doc, selection, solve, tol);

    if (log)
      Log.Write(LogName, $"  boundaries found: {solve.Boundaries.Count}");

    return solve;
  }

  private static void LogInputCurves(SelectionData selection, double tol)
  {
    for (var i = 0; i < selection.Curves.Count; i++)
    {
      var curve = selection.Curves[i];
      var start = curve.PointAtStart;
      var end = curve.PointAtEnd;
      Log.Write(LogName,
        $"  curve[{i}] {curve.GetType().Name} IsClosed={curve.IsClosed}" +
        $" TryGetPlane={curve.TryGetPlane(out _, tol)}" +
        $" start=({start.X:F3},{start.Y:F3},{start.Z:F3})" +
        $" end=({end.X:F3},{end.Y:F3},{end.Z:F3})" +
        $" gap={start.DistanceTo(end):G4}");
    }
  }

  private static Dictionary<int, List<double>> CollectSplitParameters(
    SelectionData selection,
    double tol,
    double boundaryTolerance,
    bool log)
  {
    var bboxes = new BoundingBox[selection.Curves.Count];
    for (var i = 0; i < selection.Curves.Count; i++)
    {
      bboxes[i] = selection.Curves[i].GetBoundingBox(false);
      bboxes[i].Inflate(boundaryTolerance);
    }

    var splitParams = new Dictionary<int, List<double>>();
    for (var i = 0; i < selection.Curves.Count; i++)
    {
      for (var j = i + 1; j < selection.Curves.Count; j++)
      {
        var a = bboxes[i];
        var b = bboxes[j];
        if (a.Max.X < b.Min.X || b.Max.X < a.Min.X ||
            a.Max.Y < b.Min.Y || b.Max.Y < a.Min.Y ||
            a.Max.Z < b.Min.Z || b.Max.Z < a.Min.Z)
          continue;

        var events = Intersection.CurveCurve(selection.Curves[i], selection.Curves[j], tol, tol);
        var pointCount = 0;
        var overlapCount = 0;
        if (events != null)
        {
          foreach (var ev in events)
          {
            if (ev.IsPoint)
            {
              pointCount++;
              AddSplitParam(splitParams, i, ev.ParameterA);
              AddSplitParam(splitParams, j, ev.ParameterB);
            }
            else
            {
              overlapCount++;
            }
          }
        }

        if (log && (pointCount > 0 || overlapCount > 0))
          Log.Write(LogName, $"  intersect[{i},{j}] pointEvents={pointCount} overlapEvents={overlapCount}");
      }
    }

    return splitParams;
  }

  private static void AddSplitParam(Dictionary<int, List<double>> splitParams, int curveIndex, double parameter)
  {
    if (!splitParams.TryGetValue(curveIndex, out var list))
    {
      list = new List<double>();
      splitParams[curveIndex] = list;
    }

    list.Add(parameter);
  }

  private static void SplitInputCurves(
    SelectionData selection,
    Dictionary<int, List<double>> splitParams,
    List<Curve> segments,
    List<int> segmentOriginIndices,
    bool log)
  {
    for (var i = 0; i < selection.Curves.Count; i++)
    {
      if (!splitParams.TryGetValue(i, out var parameters) || parameters.Count == 0)
      {
        segments.Add(selection.Curves[i]);
        segmentOriginIndices.Add(i);
        if (log)
          Log.Write(LogName, $"  split[{i}] no intersections -> kept as-is");
        continue;
      }

      var split = selection.Curves[i].Split(parameters);
      if (split != null && split.Length > 0)
      {
        foreach (var segment in split)
        {
          if (segment == null)
            continue;
          segments.Add(segment);
          segmentOriginIndices.Add(i);
        }

        if (log)
          Log.Write(LogName, $"  split[{i}] {parameters.Count} params -> {split.Length} segments");
      }
      else
      {
        segments.Add(selection.Curves[i]);
        segmentOriginIndices.Add(i);
        if (log)
          Log.Write(LogName, $"  split[{i}] Split() returned null/empty -> kept as-is");
      }
    }
  }

  private static void TrimDeadEnds(
    List<Curve> segments,
    List<int> segmentOriginIndices,
    double boundaryTolerance,
    bool log)
  {
    if (segments.Count == 0)
      return;

    var nodePositions = new List<Point3d>();
    var startNodes = new int[segments.Count];
    var endNodes = new int[segments.Count];

    int GetOrAddNode(Point3d point)
    {
      for (var i = 0; i < nodePositions.Count; i++)
      {
        if (point.DistanceTo(nodePositions[i]) <= boundaryTolerance)
          return i;
      }

      nodePositions.Add(point);
      return nodePositions.Count - 1;
    }

    for (var i = 0; i < segments.Count; i++)
    {
      startNodes[i] = GetOrAddNode(segments[i].PointAtStart);
      endNodes[i] = GetOrAddNode(segments[i].PointAtEnd);
    }

    var removed = new HashSet<int>();
    var trimRounds = 0;
    var anyChange = true;
    while (anyChange)
    {
      anyChange = false;
      var degree = new Dictionary<int, int>();
      for (var i = 0; i < segments.Count; i++)
      {
        if (removed.Contains(i))
          continue;

        degree.TryGetValue(startNodes[i], out var startDegree);
        degree[startNodes[i]] = startDegree + 1;
        degree.TryGetValue(endNodes[i], out var endDegree);
        degree[endNodes[i]] = endDegree + 1;
      }

      for (var i = 0; i < segments.Count; i++)
      {
        if (removed.Contains(i))
          continue;
        if (startNodes[i] == endNodes[i])
          continue;
        if (degree.GetValueOrDefault(startNodes[i]) != 1 &&
            degree.GetValueOrDefault(endNodes[i]) != 1)
          continue;

        removed.Add(i);
        anyChange = true;
      }

      trimRounds++;
    }

    if (removed.Count > 0)
    {
      for (var i = segments.Count - 1; i >= 0; i--)
      {
        if (!removed.Contains(i))
          continue;
        segments.RemoveAt(i);
        segmentOriginIndices.RemoveAt(i);
      }
    }

    if (log)
    {
      Log.Write(LogName,
        $"  dead-end trimming: {trimRounds} rounds, {removed.Count} removed," +
        $" {segments.Count} core segments remain");
      if (segments.Count == 0)
        Log.Write(LogName, "  no core segments after trimming -> no boundaries");
    }
  }

  private static void JoinCoreSegments(
    BoundarySolve solve,
    RhinoDoc doc,
    double tol,
    double boundaryTolerance,
    bool log)
  {
    if (solve.CoreSegments.Count == 0)
      return;

    if (log)
      Log.Write(LogName, $"  joining {solve.CoreSegments.Count} core segments...");

    var joined = Curve.JoinCurves(solve.CoreSegments.ToArray(), boundaryTolerance);
    if (joined == null || joined.Length == 0)
    {
      if (log)
        Log.Write(LogName, "  JoinCurves returned null/empty");
      return;
    }

    for (var i = 0; i < joined.Length; i++)
    {
      var curve = joined[i];
      if (curve == null)
      {
        if (log)
          Log.Write(LogName, $"  joined[{i}] null");
        continue;
      }

      curve = TryCloseSmallGap(curve, boundaryTolerance, log, i);
      var start = curve.PointAtStart;
      var end = curve.PointAtEnd;
      var hasPlane = curve.TryGetPlane(out var plane, tol);

      if (log)
      {
        Log.Write(LogName,
          $"  joined[{i}] {curve.GetType().Name} IsClosed={curve.IsClosed} TryGetPlane={hasPlane}" +
          $" start=({start.X:F3},{start.Y:F3},{start.Z:F3})" +
          $" end=({end.X:F3},{end.Y:F3},{end.Z:F3})" +
          $" gap={start.DistanceTo(end):G4}");
      }

      if (!curve.IsClosed || !hasPlane)
        continue;

      solve.Boundaries.Add(new BoundaryInfo(curve, plane, BuildHatchLines(curve, plane, doc.ModelAbsoluteTolerance, boundaryTolerance)));
    }
  }

  private static Curve TryCloseSmallGap(Curve curve, double boundaryTolerance, bool log, int index)
  {
    if (curve.IsClosed)
      return curve;

    var start = curve.PointAtStart;
    var end = curve.PointAtEnd;
    var gap = start.DistanceTo(end);
    if (gap <= 0.0 || gap >= boundaryTolerance)
      return curve;

    var bridge = new LineCurve(end, start);
    var reclosed = Curve.JoinCurves(new Curve[] { curve, bridge }, boundaryTolerance);
    if (reclosed?.Length == 1 && reclosed[0] != null && reclosed[0].IsClosed)
    {
      if (log)
        Log.Write(LogName, $"  joined[{index}] gap={gap:G4} < closingTol={boundaryTolerance:G4} -> bridged and closed");
      return reclosed[0];
    }

    return curve;
  }

  private static void BuildBoundaryMembers(
    RhinoDoc doc,
    SelectionData selection,
    BoundarySolve solve,
    double tol)
  {
    solve.BoundaryMembers.Clear();
    foreach (var boundary in solve.Boundaries)
    {
      var members = new HashSet<Guid>();

      for (var i = 0; i < solve.CoreSegments.Count; i++)
      {
        var midpoint = solve.CoreSegments[i].PointAt(solve.CoreSegments[i].Domain.Mid);
        if (boundary.Curve.Contains(midpoint, boundary.Plane, tol) == PointContainment.Coincident)
          members.Add(selection.CurveIds[solve.CoreOriginIndices[i]]);
      }

      foreach (var id in selection.AllIds)
      {
        if (members.Contains(id))
          continue;

        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        var point = RepresentativePoint(obj);
        if (!point.HasValue)
          continue;

        if (boundary.Curve.Contains(point.Value, boundary.Plane, tol) is PointContainment.Inside or PointContainment.Coincident)
          members.Add(id);
      }

      solve.BoundaryMembers.Add(members);
    }
  }

  private static int CreateGroups(RhinoDoc doc, SelectionData selection, BoundarySolve solve)
  {
    var groupCount = 0;
    for (var i = 0; i < solve.Boundaries.Count; i++)
    {
      var members = solve.BoundaryMembers[i];
      if (members.Count < 2)
        continue;

      var isSubset = false;
      for (var j = 0; j < solve.Boundaries.Count; j++)
      {
        if (j == i)
          continue;

        var otherMembers = solve.BoundaryMembers[j];
        if (otherMembers.Count <= members.Count)
          continue;

        if (members.IsSubsetOf(otherMembers))
        {
          isSubset = true;
          break;
        }
      }

      if (isSubset)
        continue;

      Log.Write(LogName, $"  boundary[{i}] members={members.Count} -> group");
      var groupIndex = doc.Groups.Add();
      if (groupIndex < 0)
        continue;

      foreach (var id in members)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        obj.Attributes.AddToGroup(groupIndex);
        obj.CommitChanges();
      }

      _ourGroupIndices.Add(groupIndex);
      groupCount++;
    }

    return groupCount;
  }

  private static void ClearPreviousGroups(RhinoDoc doc)
  {
    if (_ourGroupIndices.Count == 0)
      return;

    foreach (RhinoObject obj in doc.Objects)
    {
      var groups = obj.Attributes.GetGroupList();
      if (groups == null || groups.Length == 0)
        continue;

      var dirty = false;
      foreach (var groupIndex in groups)
      {
        if (!_ourGroupIndices.Contains(groupIndex))
          continue;

        obj.Attributes.RemoveFromGroup(groupIndex);
        dirty = true;
      }

      if (dirty)
        obj.CommitChanges();
    }

    foreach (var index in _ourGroupIndices)
      doc.Groups.Delete(index);
    _ourGroupIndices.Clear();
  }

  private static Point3d? RepresentativePoint(RhinoObject obj)
  {
    var geometry = obj.Geometry;
    if (geometry == null)
      return null;

    if (geometry is Point point)
      return point.Location;

    if (geometry is Curve curve)
      return curve.PointAt(curve.Domain.Mid);

    var bbox = geometry.GetBoundingBox(accurate: true);
    return bbox.IsValid ? bbox.Center : null;
  }

  private static double DefaultBoundaryTolerance(RhinoDoc doc)
  {
    return Math.Max(doc.ModelAbsoluteTolerance * 100.0, 1.0e-6);
  }

  private static List<Line> BuildHatchLines(Curve boundary, Plane plane, double tol, double boundaryTolerance)
  {
    var samples = CurveSamples(boundary, 96);
    if (samples.Count == 0)
      return new List<Line>();

    var first = true;
    var minU = 0.0;
    var maxU = 0.0;
    var minV = 0.0;
    var maxV = 0.0;
    foreach (var point in samples)
    {
      if (!plane.ClosestParameter(point, out var u, out var v))
        continue;

      if (first)
      {
        minU = maxU = u;
        minV = maxV = v;
        first = false;
      }
      else
      {
        minU = Math.Min(minU, u);
        maxU = Math.Max(maxU, u);
        minV = Math.Min(minV, v);
        maxV = Math.Max(maxV, v);
      }
    }

    if (first)
      return new List<Line>();

    var width = maxU - minU;
    var height = maxV - minV;
    var diagonal = Math.Sqrt(width * width + height * height);
    if (diagonal <= tol)
      return new List<Line>();

    var centerU = 0.5 * (minU + maxU);
    var centerV = 0.5 * (minV + maxV);
    var spacing = Math.Max(diagonal / 32.0, Math.Max(boundaryTolerance, tol) * 2.0);
    var halfLength = diagonal * 0.75;
    var stepCount = Math.Max(80, Math.Min(220, (int)Math.Ceiling(diagonal / Math.Max(spacing * 0.25, tol))));
    var step = (halfLength * 2.0) / stepCount;

    const double invSqrt2 = 0.7071067811865475;
    var dirU = invSqrt2;
    var dirV = invSqrt2;
    var normU = -invSqrt2;
    var normV = invSqrt2;

    var corners = new[]
    {
      (U: minU - spacing, V: minV - spacing),
      (U: maxU + spacing, V: minV - spacing),
      (U: maxU + spacing, V: maxV + spacing),
      (U: minU - spacing, V: maxV + spacing)
    };

    var minOffset = double.MaxValue;
    var maxOffset = double.MinValue;
    foreach (var corner in corners)
    {
      var offset = (corner.U - centerU) * normU + (corner.V - centerV) * normV;
      minOffset = Math.Min(minOffset, offset);
      maxOffset = Math.Max(maxOffset, offset);
    }

    var result = new List<Line>();
    for (var offset = minOffset; offset <= maxOffset + spacing * 0.5; offset += spacing)
    {
      Point3d? runStart = null;
      Point3d previous = Point3d.Unset;

      for (var i = 0; i <= stepCount; i++)
      {
        var along = -halfLength + step * i;
        var u = centerU + dirU * along + normU * offset;
        var v = centerV + dirV * along + normV * offset;
        var point = plane.PointAt(u, v);
        var containment = boundary.Contains(point, plane, tol);
        var inside = containment is PointContainment.Inside or PointContainment.Coincident;

        if (inside)
        {
          runStart ??= point;
          previous = point;
          continue;
        }

        if (runStart.HasValue && previous.IsValid && runStart.Value.DistanceTo(previous) > tol)
          result.Add(new Line(runStart.Value, previous));

        runStart = null;
        previous = point;
      }

      if (runStart.HasValue && previous.IsValid && runStart.Value.DistanceTo(previous) > tol)
        result.Add(new Line(runStart.Value, previous));
    }

    return result;
  }

  private static List<Point3d> CurveSamples(Curve curve, int count)
  {
    var points = new List<Point3d>();
    if (curve == null || !curve.IsValid)
      return points;

    var parameters = curve.DivideByCount(Math.Max(4, count), true);
    if (parameters != null && parameters.Length > 0)
    {
      foreach (var parameter in parameters)
        points.Add(curve.PointAt(parameter));
      return points;
    }

    points.Add(curve.PointAtStart);
    points.Add(curve.PointAtEnd);
    return points;
  }

  private sealed class SelectionData
  {
    public List<Guid> AllIds { get; } = new();
    public List<Curve> Curves { get; } = new();
    public List<Guid> CurveIds { get; } = new();
  }

  private sealed class BoundarySolve
  {
    public BoundarySolve(double tolerance)
    {
      Tolerance = tolerance;
    }

    public double Tolerance { get; }
    public List<BoundaryInfo> Boundaries { get; } = new();
    public List<Curve> CoreSegments { get; } = new();
    public List<int> CoreOriginIndices { get; } = new();
    public List<HashSet<Guid>> BoundaryMembers { get; } = new();
  }

  private sealed record BoundaryInfo(Curve Curve, Plane Plane, List<Line> HatchLines);

  private sealed class BoundaryPreviewConduit : DisplayConduit
  {
    private static readonly Color HatchColor = Color.FromArgb(120, 173, 216, 230);
    private static readonly Color OutlineColor = Color.FromArgb(230, 65, 175, 230);

    public BoundarySolve? Solve { get; set; }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      var solve = Solve;
      if (solve == null)
        return;

      foreach (var boundary in solve.Boundaries)
      {
        foreach (var line in boundary.HatchLines)
          e.Display.DrawLine(line.From, line.To, HatchColor, 1);

        e.Display.DrawCurve(boundary.Curve, OutlineColor, 2);
      }
    }
  }
}
