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

  private static double _tolerance = 0.001;

  public override string EnglishName => "vOverlaps";

  // ── Option persistence ────────────────────────────────────────────────────

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, TolKey, out var t) && t > 0.0)
        _tolerance = t;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
      section[TolKey] = _tolerance);

  // ── Command ───────────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();

    // Accept preselection or prompt; fall back to all visible curves.
    var go = new GetObject();
    go.SetCommandPrompt("Select curves to check for overlaps (Enter = all visible)");
    go.GeometryFilter  = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect     = true;
    go.AcceptNothing(true);
    go.EnablePreSelect(true, true);

    var tolOpt = new OptionDouble(_tolerance, 1e-9, 1e6);
    go.AddOptionDouble("Tolerance", ref tolOpt);

    List<RhinoObject> inputObjs;

    while (true)
    {
      var res = go.GetMultiple(1, 0);

      if (res == GetResult.Cancel)
        return Result.Cancel;

      if (res == GetResult.Option)
      {
        _tolerance = tolOpt.CurrentValue;
        SaveOptions();
        continue;
      }

      if (res == GetResult.Nothing || res == GetResult.Object)
      {
        _tolerance = tolOpt.CurrentValue;
        SaveOptions();

        if (res == GetResult.Nothing || go.ObjectCount == 0)
        {
          // Use all visible normal curve objects.
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
          for (int i = 0; i < go.ObjectCount; i++)
          {
            var obj = go.Object(i).Object();
            if (obj != null) inputObjs.Add(obj);
          }
        }
        break;
      }
    }

    if (inputObjs.Count < 2)
    {
      RhinoApp.WriteLine("vOverlaps: need at least 2 visible curves.");
      return Result.Nothing;
    }

    // Build curve cache.
    var curveCache  = new Dictionary<uint, Curve>();
    var lengthCache = new Dictionary<uint, double>();
    var objById     = new Dictionary<uint, RhinoObject>();

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
    var suppress = SuppressOneOriginalPerGroup(groups, objById);

    // Collect serial numbers to select.
    var toSelect = new List<Guid>();
    foreach (var sn in ids)
    {
      if (coveredBy[sn].Count > 0 && !suppress.Contains(sn) && objById.TryGetValue(sn, out var obj))
        toSelect.Add(obj.Id);
    }

    doc.Objects.UnselectAll();
    foreach (var id in toSelect)
      doc.Objects.Select(id);

    doc.Views.Redraw();

    if (toSelect.Count > 0)
      RhinoApp.WriteLine($"vOverlaps: selected {toSelect.Count} covered curve(s) " +
        $"({curveCache.Count} inputs, {pairChecks} pair checks, {coverHits} cover hits).");
    else
      RhinoApp.WriteLine($"vOverlaps: no covered curves found ({curveCache.Count} inputs).");

    return Result.Success;
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
      // Keep the oldest (lowest RuntimeSerialNumber) unselected.
      uint oldest = group[0];
      for (int i = 1; i < group.Count; i++)
        if (group[i] < oldest) oldest = group[i];
      suppress.Add(oldest);
    }
    return suppress;
  }
}
