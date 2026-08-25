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
  private const string LogName = "vGroup";
  private const string OptionsSectionName = "vGroup";

  // Option defaults
  private const double DefaultStoredBoundaryTolerance = 0.0; // Model units; zero selects the document-derived tolerance.
  private const bool DefaultFlattenGroups = false; // true replaces nested memberships with one group; false preserves existing groups.

  // Customizable boundary behavior
  private const double ConnectivitySortTolerance = 1.0; // Maximum endpoint gap in model units used only to order source curves before boundary solving; positive value.

  private static readonly HashSet<int> _ourGroupIndices = new();
  private static double _boundaryTolerance = DefaultStoredBoundaryTolerance;
  private static bool _flattenGroups = DefaultFlattenGroups;

  public override string EnglishName => "vGroup";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedOptions();
    var selection = SelectObjects(doc);
    if (selection == null)
      return Result.Cancel;

    if (!selection.HasBoundaryGeometry)
    {
      RhinoApp.WriteLine("vGroup: select at least one curve, surface, polysurface, or face boundary.");
      return Result.Nothing;
    }

    var boundaryTolerance = _boundaryTolerance > 0.0
      ? _boundaryTolerance
      : DefaultBoundaryTolerance(doc);

    // Enable the conduit before the solve so boundaries appear as they are discovered.
    var previewConduit = new BoundaryPreviewConduit();
    previewConduit.Enabled = true;
    BoundarySolve solve;
    try
    {
      solve = SolveBoundaries(doc, selection, boundaryTolerance, log: true, conduit: previewConduit);
    }
    finally
    {
      previewConduit.Enabled = false;
    }
    if (solve.NearMissGap < double.MaxValue)
      Log.Write(LogName, $"  near-miss: open chain with gap={solve.NearMissGap:G4} (raise Tolerance to close)");
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
    go.SetCommandPrompt("Select objects to group by curve or face boundary");
    go.GroupSelect = true;
    go.SubObjectSelect = true;
    go.GetMultiple(1, 0);

    if (go.CommandResult() != Result.Success)
      return null;

    var result = new SelectionData();
    var curvePairs = new List<(Curve Crv, Guid Id)>();
    var faceSources = new HashSet<FaceBoundarySource>();
    foreach (var objRef in go.Objects())
    {
      var id = objRef.ObjectId;
      if (id == Guid.Empty)
        continue;

      if (!result.AllIds.Contains(id))
        result.AllIds.Add(id);
      var obj = doc.Objects.FindId(id);
      if (obj?.Geometry is Curve curve)
      {
        curvePairs.Add((curve, id));
        continue;
      }

      if (obj?.Geometry is not Brep brep)
        continue;

      var component = objRef.GeometryComponentIndex;
      if (component.ComponentIndexType == ComponentIndexType.BrepFace &&
          component.Index >= 0 && component.Index < brep.Faces.Count)
      {
        faceSources.Add(new FaceBoundarySource(id, component.Index));
        continue;
      }

      for (var faceIndex = 0; faceIndex < brep.Faces.Count; faceIndex++)
        faceSources.Add(new FaceBoundarySource(id, faceIndex));
    }

    // Sort by endpoint connectivity so JoinCurves always builds chains in the same order.
    curvePairs.Sort((a, b) => b.Crv.GetLength().CompareTo(a.Crv.GetLength())); // longest first as seed
    var snapTol = ConnectivitySortTolerance;
    var sorted = new List<(Curve Crv, Guid Id)>(curvePairs.Count);
    var used = new bool[curvePairs.Count];
    while (sorted.Count < curvePairs.Count)
    {
      // Find next seed: first unused (longest, since pre-sorted).
      var seedIdx = -1;
      for (var si = 0; si < curvePairs.Count; si++)
        if (!used[si]) { seedIdx = si; break; }
      if (seedIdx < 0) break;
      used[seedIdx] = true;
      sorted.Add(curvePairs[seedIdx]);
      var chainEnd = curvePairs[seedIdx].Crv.PointAtEnd;

      // Greedily extend the chain by the nearest unused endpoint.
      while (true)
      {
        var bestIdx = -1; var bestDist = snapTol; var bestFlip = false;
        for (var ci = 0; ci < curvePairs.Count; ci++)
        {
          if (used[ci]) continue;
          var ds = chainEnd.DistanceTo(curvePairs[ci].Crv.PointAtStart);
          var de = chainEnd.DistanceTo(curvePairs[ci].Crv.PointAtEnd);
          if (ds < bestDist) { bestIdx = ci; bestDist = ds; bestFlip = false; }
          if (de < bestDist) { bestIdx = ci; bestDist = de; bestFlip = true; }
        }
        if (bestIdx < 0) break;
        used[bestIdx] = true;
        var (nextCrv, nextId) = curvePairs[bestIdx];
        if (bestFlip) { nextCrv = nextCrv.DuplicateCurve(); nextCrv.Reverse(); }
        sorted.Add((nextCrv, nextId));
        chainEnd = nextCrv.PointAtEnd;
      }
    }
    foreach (var (crv, id) in sorted)
    {
      result.ExplicitCurves.Add(crv);
      result.ExplicitCurveIds.Add(id);
    }
    result.FaceSources.AddRange(faceSources);

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
        go.AcceptNumber(true, true);
        go.AddOptionDouble("Tolerance", ref toleranceOption);
        var flattenGroupsToggle = new OptionToggle(_flattenGroups, "No", "Yes");
        go.AddOptionToggle("FlattenGroups", ref flattenGroupsToggle);

        var result = go.Get();
        if (flattenGroupsToggle.CurrentValue != _flattenGroups)
        {
          _flattenGroups = flattenGroupsToggle.CurrentValue;
          SavePersistedOptions();
        }
        if (go.CommandResult() != Result.Success)
          return false;

        if (result == GetResult.Nothing)
          return true;

        if (result != GetResult.Option && result != GetResult.Number)
          return false;

        var nextTolerance = result == GetResult.Number
          ? Math.Max(go.Number(), RhinoMath.ZeroTolerance)
          : Math.Max(toleranceOption.CurrentValue, RhinoMath.ZeroTolerance);
        toleranceOption.CurrentValue = nextTolerance;
        if (Math.Abs(nextTolerance - boundaryTolerance) <= RhinoMath.ZeroTolerance)
          continue;

        boundaryTolerance = nextTolerance;
        solve = SolveBoundaries(doc, selection, boundaryTolerance, log: true, conduit: conduit);
        conduit.Solve = solve;
        var nearMissHint = solve.NearMissGap < double.MaxValue
          ? $" | Nearest open-chain gap: {solve.NearMissGap:G4} — raise Tolerance to close it"
          : string.Empty;
        RhinoApp.WriteLine($"vGroup: {solve.Boundaries.Count} boundar{(solve.Boundaries.Count == 1 ? "y" : "ies")} found | Tolerance {boundaryTolerance:G}{nearMissHint}");
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
    bool log,
    BoundaryPreviewConduit? conduit = null)
  {
    var tol = doc.ModelAbsoluteTolerance;
    var solve = new BoundarySolve(boundaryTolerance);
    PrepareBoundaryCurves(doc, selection, tol, boundaryTolerance, log);

    if (log)
    {
      Log.Write(LogName,
        $"--- run start --- tol={tol:G4} boundaryTol={boundaryTolerance:G4} " +
        $"explicitCurves={selection.ExplicitCurves.Count} faces={selection.FaceSources.Count} " +
        $"boundaryCurves={selection.Curves.Count} totalObjects={selection.AllIds.Count}");
      LogInputCurves(selection, tol);
    }

    if (selection.Curves.Count == 0)
      return solve;

    // Exclude very short curves (notches, tick marks) from boundary topology — still used for member detection.
    // Exclude notch objects from boundary topology — still used for member detection.
    var splitParams = CollectSplitParameters(selection, tol, boundaryTolerance, log, doc);
    SplitInputCurves(selection, splitParams, solve.CoreSegments, solve.CoreOriginIndices, log);
    TrimDeadEnds(solve.CoreSegments, solve.CoreOriginIndices, boundaryTolerance, log);
    JoinCoreSegments(solve, doc, tol, boundaryTolerance, log, conduit);
    BuildBoundaryMembers(doc, selection, solve, tol);

    if (log)
      Log.Write(LogName, $"  boundaries found: {solve.Boundaries.Count}");

    return solve;
  }

  private static void PrepareBoundaryCurves(
    RhinoDoc doc,
    SelectionData selection,
    double documentTolerance,
    double boundaryTolerance,
    bool log)
  {
    selection.Curves.Clear();
    selection.CurveSourceIds.Clear();
    for (var index = 0; index < selection.ExplicitCurves.Count; index++)
    {
      selection.Curves.Add(selection.ExplicitCurves[index]);
      selection.CurveSourceIds.Add(
        new HashSet<Guid> { selection.ExplicitCurveIds[index] });
    }

    if (selection.FaceSources.Count == 0)
      return;

    var inputFaces = new List<Brep>();
    var inputSourceIds = new List<Guid>();
    foreach (var source in selection.FaceSources)
    {
      var brep = doc.Objects.FindId(source.ObjectId)?.Geometry as Brep;
      if (brep == null || source.FaceIndex < 0 ||
          source.FaceIndex >= brep.Faces.Count)
        continue;

      var duplicate = brep.Faces[source.FaceIndex].DuplicateFace(
        duplicateMeshes: false);
      if (duplicate == null || !duplicate.IsValid)
      {
        duplicate?.Dispose();
        continue;
      }

      inputFaces.Add(duplicate);
      inputSourceIds.Add(source.ObjectId);
    }

    if (inputFaces.Count == 0)
      return;

    var joinTolerance = Math.Max(documentTolerance, boundaryTolerance);
    Brep[]? joinedFaces = null;
    List<int[]>? indexMap = null;
    try
    {
      joinedFaces = Brep.JoinBreps(
        inputFaces,
        joinTolerance,
        doc.ModelAngleToleranceRadians,
        out indexMap);

      if (joinedFaces == null || joinedFaces.Length == 0 ||
          indexMap == null || indexMap.Count != joinedFaces.Length)
      {
        foreach (var joined in joinedFaces ?? Array.Empty<Brep>())
          joined.Dispose();
        joinedFaces = inputFaces
          .Select(face => face.DuplicateBrep())
          .Where(face => face != null)
          .Cast<Brep>()
          .ToArray();
        indexMap = Enumerable.Range(0, joinedFaces.Length)
          .Select(index => new[] { index })
          .ToList();
      }

      var generatedCurveCount = 0;
      for (var joinedIndex = 0; joinedIndex < joinedFaces.Length; joinedIndex++)
      {
        var sourceIds = indexMap[joinedIndex]
          .Where(index => index >= 0 && index < inputSourceIds.Count)
          .Select(index => inputSourceIds[index])
          .ToHashSet();
        if (sourceIds.Count == 0)
          continue;

        var nakedEdges = joinedFaces[joinedIndex].DuplicateNakedEdgeCurves(
          nakedOuter: true,
          nakedInner: true);
        foreach (var edge in nakedEdges ?? Array.Empty<Curve>())
        {
          if (edge == null || !edge.IsValid)
          {
            edge?.Dispose();
            continue;
          }

          selection.Curves.Add(edge);
          selection.CurveSourceIds.Add(new HashSet<Guid>(sourceIds));
          generatedCurveCount++;
        }
      }

      if (log)
      {
        Log.Write(
          LogName,
          $"  face boundaries: inputs={inputFaces.Count} " +
          $"joinedPatches={joinedFaces.Length} nakedCurves={generatedCurveCount} " +
          $"joinTolerance={joinTolerance:G4}");
      }
    }
    finally
    {
      foreach (var joined in joinedFaces ?? Array.Empty<Brep>())
        joined.Dispose();
      foreach (var input in inputFaces)
        input.Dispose();
    }
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
    bool log,
    RhinoDoc? doc = null)
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
      // Pump the message loop periodically to keep Rhino responsive.
      if (doc != null && i % 20 == 0) { doc.Views.Redraw(); RhinoApp.Wait(); }
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
    bool log,
    BoundaryPreviewConduit? conduit = null)
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
        var len = curve.GetLength();
        var bbox = curve.GetBoundingBox(accurate: false);
        Log.Write(LogName,
          $"  joined[{i}] {curve.GetType().Name} IsClosed={curve.IsClosed} TryGetPlane={hasPlane}" +
          $" len={len:F3}" +
          $" bbox={bbox.Min.X:F1},{bbox.Min.Y:F1}..{bbox.Max.X:F1},{bbox.Max.Y:F1}" +
          $" gap={start.DistanceTo(end):G4}");
      }

      if (!curve.IsClosed || !hasPlane)
      {
        // Track the gap of the largest open chain — most likely the intended outer boundary.
        var gap = curve.PointAtStart.DistanceTo(curve.PointAtEnd);
        if (gap > 0 && curve.GetLength() > (solve.NearMissGap > 0 ? 0 : 0) &&
            (solve.NearMissSourceLength < curve.GetLength()))
        {
          solve.NearMissGap = gap;
          solve.NearMissSourceLength = curve.GetLength();
        }
        continue;
      }

      solve.Boundaries.Add(new BoundaryInfo(curve, plane, BuildHatchLines(curve, plane, doc.ModelAbsoluteTolerance, boundaryTolerance)));
      if (conduit != null) { conduit.Solve = solve; doc.Views.Redraw(); RhinoApp.Wait(); }
    }
  }

  private static Curve TryCloseSmallGap(Curve curve, double boundaryTolerance, bool log, int index)
  {
    if (curve.IsClosed)
      return curve;

    var start = curve.PointAtStart;
    var end = curve.PointAtEnd;
    var gap = start.DistanceTo(end);
    if (gap <= 0.0 || gap >= Math.Max(boundaryTolerance, curve.GetLength() * 0.05))
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

    // Pre-fetch all objects and their bboxes once to avoid repeated FindId calls.
    var allObjects = new (RhinoObject? Obj, BoundingBox Bbox)[selection.AllIds.Count];
    for (var k = 0; k < selection.AllIds.Count; k++)
    {
      var obj = doc.Objects.FindId(selection.AllIds[k]);
      allObjects[k] = (obj, obj?.Geometry?.GetBoundingBox(accurate: false) ?? BoundingBox.Empty);
    }

    var boundaryBboxes = solve.Boundaries.Select(b => b.Curve.GetBoundingBox(false)).ToArray();

    foreach (var (boundary, bndBbox) in solve.Boundaries.Zip(boundaryBboxes, (b, bb) => (b, bb)))
    {
      var members = new HashSet<Guid>();

      for (var i = 0; i < solve.CoreSegments.Count; i++)
      {
        var midpoint = solve.CoreSegments[i].PointAt(solve.CoreSegments[i].Domain.Mid);
        var containment = boundary.Curve.Contains(midpoint, boundary.Plane, tol);
        if (containment == PointContainment.Coincident)
        {
          foreach (var sourceId in selection.CurveSourceIds[solve.CoreOriginIndices[i]])
            members.Add(sourceId);
        }
      }

      for (var k = 0; k < allObjects.Length; k++)
      {
        var id = selection.AllIds[k];
        if (members.Contains(id)) continue;
        var (obj, objBbox) = allObjects[k];
        if (obj == null) continue;

        // Skip objects whose bbox doesn't overlap the boundary bbox at all.
        if (objBbox.IsValid && (objBbox.Min.X > bndBbox.Max.X || objBbox.Max.X < bndBbox.Min.X ||
                                 objBbox.Min.Y > bndBbox.Max.Y || objBbox.Max.Y < bndBbox.Min.Y))
          continue;

        if (AnyPointInsideBoundary(obj, objBbox, bndBbox, boundary.Curve, boundary.Plane, tol))
          members.Add(id);
      }

      Log.Write(LogName, $"  boundary members={members.Count}/{selection.AllIds.Count}");
      solve.BoundaryMembers.Add(members);
    }

    // Propagate members of spatially-nested inner boundaries into their containing outer boundaries.
    for (var inner = 0; inner < solve.BoundaryMembers.Count; inner++)
    {
      for (var outer = 0; outer < solve.BoundaryMembers.Count; outer++)
      {
        if (outer == inner) continue;
        if (!boundaryBboxes[outer].Contains(boundaryBboxes[inner])) continue;
        foreach (var id in solve.BoundaryMembers[inner])
          solve.BoundaryMembers[outer].Add(id);
      }
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
      if (_flattenGroups)
        RemoveExistingGroupMemberships(doc, members);
      var groupIndex = doc.Groups.Add();
      if (groupIndex < 0)
      {
        Log.Write(LogName, $"  boundary[{i}] doc.Groups.Add() failed");
        continue;
      }

      var committed = 0;
      foreach (var id in members)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        var attr = obj.Attributes.Duplicate();
        attr.AddToGroup(groupIndex);
        if (doc.Objects.ModifyAttributes(obj, attr, quiet: true))
          committed++;
        else
          Log.Write(LogName, $"  boundary[{i}] ModifyAttributes failed for {id}");
      }

      if (committed == 0)
      {
        doc.Groups.Delete(groupIndex);
        Log.Write(LogName, $"  boundary[{i}] no members committed — group deleted");
        continue;
      }

      _ourGroupIndices.Add(groupIndex);
      groupCount++;
      Log.Write(LogName, $"  boundary[{i}] group created index={groupIndex} committed={committed}/{members.Count}");
    }

    return groupCount;
  }

  private static void RemoveExistingGroupMemberships(RhinoDoc doc, IEnumerable<Guid> ids)
  {
    var removed = 0;
    foreach (var id in ids)
    {
      var obj = doc.Objects.FindId(id);
      if (obj == null) continue;
      var groups = obj.Attributes.GetGroupList();
      if (groups == null || groups.Length == 0) continue;
      var attr = obj.Attributes.Duplicate();
      attr.RemoveFromAllGroups();
      if (doc.Objects.ModifyAttributes(obj, attr, quiet: true))
        removed++;
    }
    Log.Write(LogName, $"  FlattenGroups: stripped group memberships from {removed} object(s)");
  }

  private static void LoadPersistedOptions()
  {
    _flattenGroups = ToolsOptionStore.Read(
      OptionsSectionName,
      section => ToolsOptionStore.TryGetBool(section, "flattenGroups", out var v)
        ? v
        : DefaultFlattenGroups);
  }

  private static void SavePersistedOptions()
  {
    ToolsOptionStore.Update(OptionsSectionName, section => section["flattenGroups"] = _flattenGroups);
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

  // Returns true if any part of the object is inside or on the boundary.
  private static bool AnyPointInsideBoundary(RhinoObject obj, BoundingBox objBbox,
    BoundingBox bndBbox, Curve boundary, Plane plane, double tol)
  {
    PointContainment Check(Point3d pt) => boundary.Contains(pt, plane, tol);

    if (obj.Geometry is Curve crv)
    {
      foreach (var t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
      {
        var c = Check(crv.PointAtNormalizedLength(t));
        if (c is PointContainment.Inside or PointContainment.Coincident)
          return true;
      }
      // CurveCurve is only needed for straddle: skip when object bbox is fully inside boundary bbox.
      if (objBbox.IsValid && bndBbox.Contains(objBbox))
        return false;
      var events = Intersection.CurveCurve(crv, boundary, tol, 0);
      return events != null && events.Count > 0;
    }

    var p = RepresentativePoint(obj);
    return p.HasValue && Check(p.Value) is PointContainment.Inside or PointContainment.Coincident;
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
    public List<Curve> ExplicitCurves { get; } = new();
    public List<Guid> ExplicitCurveIds { get; } = new();
    public List<FaceBoundarySource> FaceSources { get; } = new();
    public List<Curve> Curves { get; } = new();
    public List<HashSet<Guid>> CurveSourceIds { get; } = new();
    public bool HasBoundaryGeometry =>
      ExplicitCurves.Count > 0 || FaceSources.Count > 0;
  }

  private readonly record struct FaceBoundarySource(Guid ObjectId, int FaceIndex);

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
    // Gap of the nearest open chain that could close into a boundary if tolerance were raised.
    public double NearMissGap { get; set; } = double.MaxValue;
    public double NearMissSourceLength { get; set; }
  }

  private sealed record BoundaryInfo(Curve Curve, Plane Plane, List<Line> HatchLines);

  private sealed class BoundaryPreviewConduit : DisplayConduit
  {
    private static readonly Color HatchColor = Color.FromArgb(199, 148, 228, 255); // Translucent fill for detected closed boundaries.
    private static readonly Color OutlineColor = Color.FromArgb(230, 255, 60, 0); // Outline color for detected closed boundaries.

    public BoundarySolve? Solve { get; set; }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      var solve = Solve;
      if (solve == null)
        return;

      var bboxes = solve.Boundaries.Select(b => b.Curve.GetBoundingBox(false)).ToArray();

      for (var i = 0; i < solve.Boundaries.Count; i++)
      {
        // Skip boundaries whose bbox is contained inside a larger boundary (nested preview).
        var isNested = false;
        for (var j = 0; j < solve.Boundaries.Count; j++)
        {
          if (j == i) continue;
          if (bboxes[j].Contains(bboxes[i]))
          { isNested = true; break; }
        }
        if (isNested) continue;

        var boundary = solve.Boundaries[i];
        foreach (var line in boundary.HatchLines)
          PreviewDisplay.DrawLine(e.Display, line.From, line.To, HatchColor);
        PreviewDisplay.DrawCurve(e.Display, boundary.Curve, OutlineColor, 2);
      }
    }
  }
}
