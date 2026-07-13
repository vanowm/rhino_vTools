using System;
using System.Collections.Generic;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Finds all curve objects that overlap by following the same path and selects them.
/// For each group of identical duplicates the oldest curve (lowest runtime serial) is
/// left unselected (treated as the original to keep); all others are selected.
/// Curves that are fully covered by a longer curve are also selected.
/// </summary>
public sealed class vOverlaps : Command
{
  private const string SectionName = "vOverlaps";
  private const string TolKey      = "tolerance";
  private const string SegKey      = "segments";

  private static double _tolerance = 0.001;
  private static bool   _segments  = false;

  public override string EnglishName => "vOverlaps";

  // ── Option persistence ────────────────────────────────────────────────────

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, TolKey, out var t) && t > 0.0)
        _tolerance = t;
      if (ToolsOptionStore.TryGetBool(section, SegKey, out var s))
        _segments = s;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[TolKey] = _tolerance;
      section[SegKey] = _segments;
    });

  // ── Command ───────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();

    // ── Selection loop ─────────────────────────────────────────────────────
    // Tracks the working set across AddMore / Remove iterations.
    var selectedIds = new HashSet<Guid>();

    // Seed from preselection.
    foreach (var obj in doc.Objects.GetSelectedObjects(false, false))
      if (obj?.ObjectType == ObjectType.Curve) selectedIds.Add(obj.Id);

    bool firstPrompt = true;

    while (true)
    {
      var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.GeometryFilter    = ObjectType.Curve;
      go.SubObjectSelect   = false;
      go.GroupSelect       = true;
      go.AcceptNothing(true);
      go.AcceptNumber(true, true);
      go.EnablePreSelect(false, true);
      go.DeselectAllBeforePostSelect = false;

      var tolOpt    = new OptionDouble(_tolerance, 1e-9, 1e6);
      var segOpt    = new OptionToggle(_segments, "No", "Yes");
      var idxTol    = go.AddOptionDouble("Tolerance", ref tolOpt);
      var idxSeg    = go.AddOptionToggle("Segments",  ref segOpt);
      var idxAdd    = go.AddOption("AddMore");
      var idxRemove = go.AddOption("Remove");
      var idxAll    = go.AddOption("AllVisible");

      if (selectedIds.Count == 0)
        go.SetCommandPrompt("Select curves (Enter = all visible)");
      else if (firstPrompt)
        go.SetCommandPrompt($"{selectedIds.Count} curves — Enter to find overlaps, or add/remove");
      else
        go.SetCommandPrompt($"{selectedIds.Count} curves — Enter to find overlaps");

      firstPrompt = false;

      // Pre-select objects in the working set so the user can see them.
      foreach (var id in selectedIds)
        doc.Objects.Select(id, true);

      var res = go.GetMultiple(0, 0);

      _tolerance = tolOpt.CurrentValue;
      _segments  = segOpt.CurrentValue;

      if (res == GetResult.Cancel)
        return Result.Cancel;

      if (res == GetResult.Number)
      {
        if (go.Number() > 0.0) _tolerance = go.Number();
        SaveOptions();
        continue;
      }

      if (res == GetResult.Option)
      {
        int idx = go.Option()?.Index ?? -1;
        SaveOptions();

        if (idx == idxTol)
          continue;

        if (idx == idxAdd)
        {
          // Let user pick more curves.
          var goAdd = new GetObject();
          goAdd.EnableTransparentCommands(true);
          goAdd.SetCommandPrompt("Add curves to selection");
          goAdd.GeometryFilter    = ObjectType.Curve;
          goAdd.SubObjectSelect   = false;
          goAdd.GroupSelect       = true;
          goAdd.AcceptNothing(true);
          goAdd.EnablePreSelect(false, true);
          goAdd.DeselectAllBeforePostSelect = false;
          var addRes = goAdd.GetMultiple(1, 0);
          if (addRes == GetResult.Object)
            for (int i = 0; i < goAdd.ObjectCount; i++)
              selectedIds.Add(goAdd.Object(i).ObjectId);
          continue;
        }

        if (idx == idxRemove)
        {
          var goRm = new GetObject();
          goRm.EnableTransparentCommands(true);
          goRm.SetCommandPrompt("Remove curves from selection");
          goRm.GeometryFilter    = ObjectType.Curve;
          goRm.SubObjectSelect   = false;
          goRm.GroupSelect       = true;
          goRm.AcceptNothing(true);
          goRm.EnablePreSelect(false, true);
          goRm.DeselectAllBeforePostSelect = false;
          var rmRes = goRm.GetMultiple(1, 0);
          if (rmRes == GetResult.Object)
            for (int i = 0; i < goRm.ObjectCount; i++)
              selectedIds.Remove(goRm.Object(i).ObjectId);
          continue;
        }

        if (idx == idxAll)
        {
          selectedIds.Clear();
          continue;  // empty set → next loop uses all visible
        }

        continue;
      }

      if (res == GetResult.Object)
      {
        // User picked objects — add them to working set.
        for (int i = 0; i < go.ObjectCount; i++)
          selectedIds.Add(go.Object(i).ObjectId);
        continue;
      }

      // GetResult.Nothing = Enter → run with current set (or all visible).
      break;
    }

    SaveOptions();

    // ── Build input object list ────────────────────────────────────────────
    List<RhinoObject> inputObjs;
    if (selectedIds.Count == 0)
    {
      var settings = new ObjectEnumeratorSettings
      {
        IncludeLights   = false,
        IncludeGrips    = false,
        IncludePhantoms = false,
        NormalObjects   = true,
        LockedObjects   = false,
        HiddenObjects   = false,
      };
      inputObjs = new List<RhinoObject>();
      foreach (var obj in doc.Objects.GetObjectList(settings))
        if (obj?.ObjectType == ObjectType.Curve && obj.IsValid)
          inputObjs.Add(obj);
    }
    else
    {
      inputObjs = new List<RhinoObject>();
      foreach (var id in selectedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj?.ObjectType == ObjectType.Curve) inputObjs.Add(obj);
      }
    }

    if (inputObjs.Count < 2)
    {
      RhinoApp.WriteLine("vOverlaps: need at least 2 visible curves.");
      return Result.Nothing;
    }

    // ── Build curve cache ──────────────────────────────────────────────────
    // In Segments mode each polycurve is exploded; items map to parent objects.
    // In whole-curve mode items map 1:1 to RhinoObjects via RuntimeSerialNumber.
    var curveCache  = new Dictionary<uint, Curve>();
    var lengthCache = new Dictionary<uint, double>();
    var objById     = new Dictionary<uint, RhinoObject>();  // whole-curve mode
    var parentMap   = new Dictionary<uint, RhinoObject>();  // segment mode

    if (_segments)
    {
      uint key = 1;
      foreach (var obj in inputObjs)
      {
        if (obj.Geometry is not Curve crv) continue;
        foreach (var seg in ExplodeSegments(crv))
        {
          var dup = seg.DuplicateCurve();
          if (dup == null) continue;
          double len = dup.GetLength();
          if (len < 1e-12) continue;
          curveCache[key]  = dup;
          lengthCache[key] = len;
          parentMap[key]   = obj;
          key++;
        }
      }
    }
    else
    {
      foreach (var obj in inputObjs)
      {
        if (obj.Geometry is not Curve crv) continue;
        var dup = crv.DuplicateCurve();
        if (dup == null) continue;
        uint sn = obj.RuntimeSerialNumber;
        curveCache[sn]  = dup;
        lengthCache[sn] = dup.GetLength();
        objById[sn]     = obj;
      }
    }

    if (curveCache.Count < 2)
    {
      RhinoApp.WriteLine("vOverlaps: no valid curves to process.");
      return Result.Nothing;
    }

    double tol = _tolerance;
    var ids    = new List<uint>(curveCache.Keys);
    int n      = ids.Count;

    // covered_by[sn] = set of serial numbers whose curve covers sn.
    var coveredBy    = new Dictionary<uint, HashSet<uint>>();
    var dupPairs     = new List<(uint, uint)>();
    int pairChecks   = 0;
    int coverHits    = 0;

    foreach (var sn in ids)
      coveredBy[sn] = new HashSet<uint>();

    for (int i = 0; i < n - 1; i++)
    {
      uint snA   = ids[i];
      var  crvA  = curveCache[snA];
      double lenA = lengthCache[snA];

      for (int j = i + 1; j < n; j++)
      {
        uint snB   = ids[j];
        var  crvB  = curveCache[snB];
        double lenB = lengthCache[snB];

        pairChecks++;

        if (CurvesAreSamePathSameSize(crvA, crvB, lenA, lenB, tol))
        {
          coveredBy[snA].Add(snB);
          coveredBy[snB].Add(snA);
          dupPairs.Add((snA, snB));
          coverHits += 2;
          continue;
        }

        if (lenA <= lenB)
        {
          if (CurveIsFullyCoveredBy(crvA, lenA, crvB, tol))
          { coveredBy[snA].Add(snB); coverHits++; }
        }
        else
        {
          if (CurveIsFullyCoveredBy(crvB, lenB, crvA, tol))
          { coveredBy[snB].Add(snA); coverHits++; }
        }
      }
    }

    // Union-Find: group exact duplicates.
    var groups   = DuplicateGroups(ids, dupPairs);
    // For suppress: in segment mode use parent serial; in whole mode use item serial.
    var suppressParents = new HashSet<uint>();
    foreach (var group in groups)
    {
      if (group.Count < 2) continue;
      // Keep oldest parent (or item in whole mode) unselected.
      uint oldest = group[0];
      uint oldestParent = _segments
          ? (parentMap.TryGetValue(oldest, out var po0) ? po0.RuntimeSerialNumber : oldest)
          : oldest;
      foreach (var sn in group)
      {
        uint parentSn = _segments
            ? (parentMap.TryGetValue(sn, out var po) ? po.RuntimeSerialNumber : sn)
            : sn;
        if (parentSn < oldestParent) { oldest = sn; oldestParent = parentSn; }
      }
      suppressParents.Add(oldestParent);
    }

    // Collect objects to select.
    var toSelect = new HashSet<Guid>();
    foreach (var sn in ids)
    {
      if (coveredBy[sn].Count == 0) continue;
      if (_segments)
      {
        if (!parentMap.TryGetValue(sn, out var parentObj)) continue;
        uint parentSn = parentObj.RuntimeSerialNumber;
        if (!suppressParents.Contains(parentSn))
          toSelect.Add(parentObj.Id);
      }
      else
      {
        if (!objById.TryGetValue(sn, out var obj)) continue;
        if (!suppressParents.Contains(sn))
          toSelect.Add(obj.Id);
      }
    }

    doc.Objects.UnselectAll();
    foreach (var id in toSelect)
      doc.Objects.Select(id);

    doc.Views.Redraw();

    string modeLabel = _segments ? $", segments mode" : "";
    if (toSelect.Count > 0)
      RhinoApp.WriteLine($"vOverlaps: selected {toSelect.Count} covered curve(s) " +
        $"({curveCache.Count} items{modeLabel}, {pairChecks} pair checks, {coverHits} cover hits).");
    else
      RhinoApp.WriteLine($"vOverlaps: no covered curves found ({curveCache.Count} items{modeLabel}).");

    return Result.Success;
  }

  // ── Segment explode ───────────────────────────────────────────────────────

  /// <summary>
  /// Returns the constituent segments of a curve.
  /// PolyCurves are exploded; polylines become individual line segments.
  /// Simple curves (arc, line, NURBS) are returned as-is.
  /// </summary>
  private static IEnumerable<Curve> ExplodeSegments(Curve curve)
  {
    if (curve is PolyCurve pc)
    {
      var segs = pc.Explode();
      if (segs != null && segs.Length > 0)
      {
        foreach (var s in segs)
          foreach (var sub in ExplodeSegments(s))  // recurse for nested PolyCurves
            yield return sub;
        yield break;
      }
    }

    if (curve.TryGetPolyline(out var poly) && poly != null && poly.Count > 1)
    {
      for (int i = 0; i < poly.Count - 1; i++)
        yield return new LineCurve(poly[i], poly[i + 1]);
      yield break;
    }

    yield return curve;
  }

  // ── Geometry helpers ──────────────────────────────────────────────────────

  private static int AdaptiveSampleCount(double length, double tol, bool dense)
  {
    if (tol <= 0.0) return dense ? 90 : 60;
    double mult = dense ? 4.0 : 6.0;
    int    min  = dense ? 70  : 40;
    int    max  = dense ? 280 : 220;
    return Math.Max(min, Math.Min(max, (int)Math.Round(length / Math.Max(tol * mult, 1e-9))));
  }

  private static bool AllSamplesFollowPath(Curve a, Curve b, int samples, double tol)
  {
    for (int i = 0; i <= samples; i++)
    {
      double s  = (double)i / samples;
      Point3d p = a.PointAtNormalizedLength(s);
      if (!p.IsValid) continue;
      if (!b.ClosestPoint(p, out double t)) return false;
      if (p.DistanceTo(b.PointAt(t)) > tol)   return false;
    }
    return true;
  }

  private static bool CurveIsFullyCoveredBy(Curve a, double lenA, Curve b, double tol)
  {
    int samples = AdaptiveSampleCount(lenA, tol, dense: false);
    return AllSamplesFollowPath(a, b, samples, tol);
  }

  private static bool CurvesAreSamePathSameSize(
    Curve a, Curve b, double lenA, double lenB, double tol)
  {
    if (Math.Abs(lenA - lenB) > Math.Max(tol * 2.0, 1e-6)) return false;
    int samples = AdaptiveSampleCount(Math.Max(lenA, lenB), tol, dense: true);
    return AllSamplesFollowPath(a, b, samples, tol)
        && AllSamplesFollowPath(b, a, samples, tol);
  }

  // ── Union-Find ────────────────────────────────────────────────────────────

  private static uint FindRoot(Dictionary<uint, uint> parents, uint x)
  {
    while (parents[x] != x)
    {
      parents[x] = parents[parents[x]];   // path compression
      x = parents[x];
    }
    return x;
  }

  private static void Union(Dictionary<uint, uint> parents, Dictionary<uint, int> rank, uint a, uint b)
  {
    uint ra = FindRoot(parents, a), rb = FindRoot(parents, b);
    if (ra == rb) return;
    if (rank[ra] < rank[rb]) { parents[ra] = rb; }
    else if (rank[ra] > rank[rb]) { parents[rb] = ra; }
    else { parents[rb] = ra; rank[ra]++; }
  }

  private static List<List<uint>> DuplicateGroups(List<uint> ids, List<(uint, uint)> pairs)
  {
    var parents = new Dictionary<uint, uint>();
    var rank    = new Dictionary<uint, int>();
    foreach (var id in ids) { parents[id] = id; rank[id] = 0; }
    foreach (var (a, b) in pairs) Union(parents, rank, a, b);

    var groups = new Dictionary<uint, List<uint>>();
    foreach (var id in ids)
    {
      uint root = FindRoot(parents, id);
      if (!groups.TryGetValue(root, out var g)) { g = new List<uint>(); groups[root] = g; }
      g.Add(id);
    }
    return new List<List<uint>>(groups.Values);
  }

  private static HashSet<uint> SuppressOneOriginalPerGroup(
    List<List<uint>> groups, Dictionary<uint, RhinoObject> objById)
  {
    var suppress = new HashSet<uint>();
    foreach (var group in groups)
    {
      if (group.Count < 2) continue;
      uint oldest = group[0];
      for (int i = 1; i < group.Count; i++)
        if (group[i] < oldest) oldest = group[i];
      suppress.Add(oldest);
    }
    return suppress;
  }
}
