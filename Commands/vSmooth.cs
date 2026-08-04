using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vSmooth : Command
{
  private const string SectionName      = "vSmooth";
  private const string StrengthStartKey = "strengthStart";
  private const string StrengthEndKey   = "strengthEnd";
  private const string CopyKey          = "copy";
  private const string JoinKey          = "join";
  private const string Tag              = "vSmooth";

  private static double _strengthStart = 1.0;
  private static double _strengthEnd   = 1.0;
  private static bool   _copy;
  private static bool   _join;

  public override string EnglishName => "vSmooth";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    double tol = doc.ModelAbsoluteTolerance;
    Log.Write(Tag, $"BEGIN sStart={_strengthStart} sEnd={_strengthEnd} copy={_copy} join={_join}");
    var initialSelection = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();

    // ─ 1. Select target curve ────────────────────────────────────────────────
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select curve to smooth");
    go.GeometryFilter  = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect     = false;
    go.EnablePreSelect(true, true);
    go.EnableUnselectObjectsOnExit(false);
    go.EnableClearObjectsOnEntry(false);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);

    RhinoObject? targetObj = null;
    bool waitEnter = false;
    while (true)
    {
      go.ClearCommandOptions();
      var co = new OptionToggle(_copy, "No", "Yes");
      var jo = new OptionToggle(_join, "No", "Yes");
      go.AddOptionToggle("Copy", ref co);
      go.AddOptionToggle("Join", ref jo);
      var res = go.GetMultiple(1, 1);
      if (co.CurrentValue != _copy || jo.CurrentValue != _join)
      { _copy = co.CurrentValue; _join = jo.CurrentValue; SaveOptions(); }
      if (go.CommandResult() != Result.Success) return go.CommandResult();
      if (res == GetResult.Option) continue;
      if (res == GetResult.Object)
      {
        targetObj = go.Object(0)?.Object();
        if (go.ObjectsWerePreselected && !waitEnter) { waitEnter = true; go.EnablePreSelect(false, true); continue; }
        break;
      }
      return Result.Cancel;
    }
    if (targetObj == null) return Result.Cancel;
    var sourceCurve = targetObj.Geometry as Curve;
    if (sourceCurve == null) return Result.Failure;
    Log.Write(Tag, $"target={targetObj.Id} start={sourceCurve.PointAtStart} end={sourceCurve.PointAtEnd}");

    // ─ 2. Find candidate neighbours ─────────────────────────────────────────
    var candStart = FindAllConnected(doc, targetObj.Id, sourceCurve.PointAtStart, tol);
    var candEnd   = FindAllConnected(doc, targetObj.Id, sourceCurve.PointAtEnd,   tol);
    var all = candStart.Concat(candEnd).GroupBy(o => o.Id).Select(g => g.First()).ToList();
    Log.Write(Tag, $"candidates atStart={candStart.Count} atEnd={candEnd.Count}");

    if (all.Count == 0)
    {
      RhinoApp.WriteLine("vSmooth: no connected curves found.");
      return Result.Nothing;
    }

    // ─ 3–4. Integrated neighbour pick + preview loop ─────────────────────────
    var allIds   = all.Select(o => o.Id).ToHashSet();
    var targetId = targetObj.Id;
    RhinoObject? conn1 = null; // first-picked neighbour (green)
    RhinoObject? conn2 = null; // second-picked neighbour (blue)

    // Map pick-order to geometric start/end slots with their per-pick strengths.
    (RhinoObject? atStart, RhinoObject? atEnd, double sStart, double sEnd) GetGeoMap()
    {
      bool c1s = conn1 != null && candStart.Any(c => c.Id == conn1.Id);
      bool c2s = conn2 != null && candStart.Any(c => c.Id == conn2.Id);
      var atS = c1s ? conn1 : (c2s ? conn2 : null);
      var atE = (conn1 != null && !c1s) ? conn1
              : (conn2 != null && !c2s) ? conn2 : null;
      return (atS, atE,
        atS == null ? 0 : atS == conn1 ? _strengthStart : _strengthEnd,
        atE == null ? 0 : atE == conn1 ? _strengthStart : _strengthEnd);
    }

    var hlConduit = new HighlightConduit() { Enabled = true };
    var preview   = new PreviewConduit { Enabled = true };

    void Refresh()
    {
      hlConduit.SetCurves(conn1?.Geometry as Curve, conn2?.Geometry as Curve);
      var (atStart, atEnd, ss, se) = GetGeoMap();
      var smoothed = (atStart != null || atEnd != null)
        ? ComputeSmoothed(sourceCurve, atStart, atEnd, ss, se, tol)
        : null;
      Log.Write(Tag, $"preview smoothed={smoothed != null} conn1={conn1?.Id} conn2={conn2?.Id}");
      preview.SetCurves(smoothed, sourceCurve);
      doc.Views.Redraw();
    }

    var goNb = new GetObject();
    goNb.EnableTransparentCommands(true);
    goNb.GeometryFilter              = ObjectType.Curve;
    goNb.SubObjectSelect             = false;
    goNb.DeselectAllBeforePostSelect = false;
    goNb.EnablePreSelect(false, true);
    goNb.EnableUnselectObjectsOnExit(false);
    goNb.SetCustomGeometryFilter((obj, _, _) => allIds.Contains(obj.Id) || obj.Id == targetId);
    goNb.AcceptNothing(true);
    goNb.AcceptNumber(true, true);

    bool accepted = false;
    bool restart  = false;
    try
    {
      Refresh(); // draw target in gold before any neighbour is picked
      while (true)
      {
        goNb.ClearCommandOptions();
        var ss = new OptionDouble(_strengthStart, 0.0, 2.0);
        var se = new OptionDouble(_strengthEnd,   0.0, 2.0);
        var co = new OptionToggle(_copy, "No", "Yes");
        var jo = new OptionToggle(_join, "No", "Yes");
        goNb.AddOptionDouble("StrengthStart", ref ss);
        goNb.AddOptionDouble("StrengthEnd",   ref se);
        goNb.AddOptionToggle("Copy", ref co);
        goNb.AddOptionToggle("Join", ref jo);

        bool any = conn1 != null || conn2 != null;
        goNb.SetCommandPrompt(any
          ? "Click neighbour to change, Enter to accept"
          : "Click a connected curve to smooth into");

        // Deselect active neighbours + target before Get so clicking any of them fires GetResult.Object
        doc.Objects.FindId(conn1?.Id ?? Guid.Empty)?.Select(false);
        doc.Objects.FindId(conn2?.Id ?? Guid.Empty)?.Select(false);
        doc.Objects.FindId(targetId)?.Select(false);

        var res = goNb.Get();

        // Re-select target after every pick UNLESS the target itself was just clicked
        bool pickedTarget = res == GetResult.Object && goNb.Object(0)?.ObjectId == targetId;
        if (!pickedTarget)
          doc.Objects.FindId(targetId)?.Select(true);

        bool changed = false;
        if (ss.CurrentValue != _strengthStart) { _strengthStart = ss.CurrentValue; changed = true; }
        if (se.CurrentValue != _strengthEnd)   { _strengthEnd   = se.CurrentValue; changed = true; }
        if (co.CurrentValue != _copy)          { _copy          = co.CurrentValue; changed = true; }
        if (jo.CurrentValue != _join)          { _join          = jo.CurrentValue; changed = true; }
        if (changed) { SaveOptions(); Refresh(); }

        if (res == GetResult.Option) continue;

        if (res == GetResult.Number)
        {
          double v = Math.Clamp(goNb.Number(), 0.0, 2.0);
          _strengthStart = v; _strengthEnd = v;
          SaveOptions(); Refresh();
          continue;
        }

        if (res == GetResult.Object)
        {
          var picked = goNb.Object(0)?.Object();
          if (picked?.Id == targetId)
          {
            // Single click on target always deselects everything and restarts target pick
            Log.Write(Tag, "target clicked → restart");
            doc.Objects.FindId(conn1?.Id ?? Guid.Empty)?.Select(false);
            doc.Objects.FindId(conn2?.Id ?? Guid.Empty)?.Select(false);
            restart = true;
            break;
          }
          else if (picked != null)
          {
            bool isAtStart    = candStart.Any(c => c.Id == picked.Id);
            bool conn1AtStart = conn1 != null && candStart.Any(c => c.Id == conn1.Id);
            Log.Write(Tag, $"picked={picked.Id} isAtStart={isAtStart}");
            if (conn1?.Id == picked.Id)
            {
              // Toggle off first pick; promote second to first
              doc.Objects.FindId(picked.Id)?.Select(false); conn1 = conn2; conn2 = null;
            }
            else if (conn2?.Id == picked.Id)
            {
              doc.Objects.FindId(picked.Id)?.Select(false); conn2 = null;
            }
            else if (conn1 == null)
            {
              conn1 = picked;
            }
            else if (isAtStart == conn1AtStart)
            {
              // Same geometric end as conn1 → replace conn1
              doc.Objects.FindId(conn1.Id)?.Select(false); conn1 = picked;
            }
            else
            {
              // Opposite end → set/replace conn2
              if (conn2 != null) doc.Objects.FindId(conn2.Id)?.Select(false);
              conn2 = picked;
            }
            Refresh();
          }
          continue;
        }

        if (res == GetResult.Nothing)
        {
          if (conn1 == null && conn2 == null) return Result.Cancel;
          accepted = true;
          break;
        }
        break; // Escape
      }
    }
    finally
    {
      hlConduit.Enabled = false;
      preview.Enabled = false;
      // Restore pre-command selection
      doc.Objects.UnselectAll();
      foreach (var id in initialSelection) doc.Objects.FindId(id)?.Select(true);
      doc.Views.Redraw();
    }

    Log.Write(Tag, $"conn1={conn1?.Id} conn2={conn2?.Id}");
    if (restart) return RunCommand(doc, mode); // re-enter so user picks a new target
    if (!accepted) return Result.Cancel;

    // ─ 5. Commit ────────────────────────────────────────────────
    var (cAtStart, cAtEnd, csS, csE) = GetGeoMap();
    var final = ComputeSmoothed(sourceCurve, cAtStart, cAtEnd, csS, csE, tol);
    if (final == null) { RhinoApp.WriteLine("vSmooth: could not compute."); return Result.Failure; }

    if (_copy)
    {
      var newId = doc.Objects.AddCurve(final, targetObj.Attributes.Duplicate());
      if (newId == Guid.Empty) return Result.Failure;
      InheritGroups(doc, targetObj, newId);
      if (_join) JoinNeighbors(doc, newId, final, cAtStart, cAtEnd, tol, copyMode: true);
    }
    else
    {
      doc.Objects.Replace(targetObj.Id, final);
      if (_join) JoinNeighbors(doc, targetObj.Id, final, cAtStart, cAtEnd, tol, copyMode: false);
    }

    Log.Write(Tag, "END");
    doc.Views.Redraw();
    return Result.Success;
  }

  // ─ Smoothing core ──────────────────────────────────────────────────────────
  private static Curve? ComputeSmoothed(
    Curve curve, RhinoObject? connStart, RhinoObject? connEnd, double strengthStart, double strengthEnd, double tol)
  {
    var nurbs = curve.ToNurbsCurve()?.Duplicate() as NurbsCurve;
    if (nurbs == null) return null;
    if (nurbs.Degree < 3) nurbs.IncreaseDegree(3);
    if (nurbs.Points.Count < 4) return nurbs;
    int n = nurbs.Points.Count;

    if (connStart?.Geometry is Curve cS)
    {
      var d = DesiredTangent(curve.PointAtStart, isStart: true, cS, tol);
      Log.Write(Tag, $"  start tangent desired={d} valid={d.IsValid}");
      if (d.IsValid && d.Unitize())
      {
        Point3d P    = nurbs.Points[0].Location;
        Point3d P1   = nurbs.Points[1].Location;
        double  dist = Math.Max(P.DistanceTo(P1) * strengthStart, tol);
        nurbs.Points[1] = new ControlPoint(P + d * dist, nurbs.Points[1].Weight);
      }
    }

    if (connEnd?.Geometry is Curve cE)
    {
      var d = DesiredTangent(curve.PointAtEnd, isStart: false, cE, tol);
      Log.Write(Tag, $"  end tangent desired={d} valid={d.IsValid}");
      if (d.IsValid && d.Unitize())
      {
        Point3d P    = nurbs.Points[n - 1].Location;
        Point3d Pn2  = nurbs.Points[n - 2].Location;
        double  dist = Math.Max(P.DistanceTo(Pn2) * strengthEnd, tol);
        nurbs.Points[n - 2] = new ControlPoint(P - d * dist, nurbs.Points[n - 2].Weight);
      }
    }
    return nurbs;
  }

  // Returns desired tangent of smoothed curve at P (in curve travel direction) for G1.
  private static Vector3d DesiredTangent(Point3d P, bool isStart, Curve nb, double tol)
  {
    bool atNbStart = nb.PointAtStart.DistanceTo(P) <= tol;
    bool atNbEnd   = nb.PointAtEnd  .DistanceTo(P) <= tol;
    Log.Write(Tag, $"  DesiredTangent P={P} isStart={isStart} atNbStart={atNbStart} atNbEnd={atNbEnd}");
    if (!atNbStart && !atNbEnd) { Log.Write(Tag, "  -> Unset (no endpoint match)"); return Vector3d.Unset; }
    Vector3d result;
    if (isStart)
      // chain: nb ---> our curve  — our start tangent should continue from nb
      result = atNbEnd ? nb.TangentAtEnd : -nb.TangentAtStart;
    else
      // chain: our curve ---> nb  — our end tangent should flow into nb
      result = atNbStart ? nb.TangentAtStart : -nb.TangentAtEnd;
    Log.Write(Tag, $"  -> {result}");
    return result;
  }

  // ─ Helpers ─────────────────────────────────────────────────────────────────
  private static List<RhinoObject> FindAllConnected(RhinoDoc doc, Guid excludeId, Point3d P, double tol)
  {
    var result = new List<RhinoObject>();
    foreach (var obj in doc.Objects.GetObjectList(ObjectType.Curve))
    {
      if (obj.Id == excludeId || obj.IsHidden || obj.IsLocked) continue;
      var c = obj.Geometry as Curve;
      if (c == null) continue;
      if (c.PointAtStart.DistanceTo(P) <= tol || c.PointAtEnd.DistanceTo(P) <= tol)
        result.Add(obj);
    }
    return result;
  }

  private static void InheritGroups(RhinoDoc doc, RhinoObject source, Guid newId)
  {
    var groups = source.Attributes.GetGroupList();
    if (groups == null || groups.Length == 0) return;
    var obj = doc.Objects.FindId(newId);
    if (obj == null) return;
    foreach (var g in groups) obj.Attributes.AddToGroup(g);
    obj.CommitChanges();
  }

  private static void JoinNeighbors(RhinoDoc doc, Guid smoothedId, Curve smoothed,
    RhinoObject? cS, RhinoObject? cE, double tol, bool copyMode)
  {
    var pieces = new List<Curve> { smoothed.DuplicateCurve() };
    var del    = new List<Guid>();
    if (!copyMode) del.Add(smoothedId);
    void Add(RhinoObject? n) { if (n?.Geometry is Curve nc) { pieces.Add(nc.DuplicateCurve()); if (!copyMode) del.Add(n.Id); } }
    Add(cS);
    if (cE?.Id != cS?.Id) Add(cE);
    var joined = Curve.JoinCurves(pieces, tol);
    if (joined == null || joined.Length == 0) return;
    foreach (var id in del.Distinct()) doc.Objects.Delete(id, true);
    foreach (var jc in joined) doc.Objects.AddCurve(jc);
  }

  private static void LoadOptions()
  {
    ToolsOptionStore.Read(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, StrengthStartKey, out var ss)) _strengthStart = Math.Clamp(ss, 0.0, 2.0);
      if (ToolsOptionStore.TryGetDouble(section, StrengthEndKey,   out var se)) _strengthEnd   = Math.Clamp(se, 0.0, 2.0);
      if (ToolsOptionStore.TryGetBool  (section, CopyKey,          out var c))  _copy          = c;
      if (ToolsOptionStore.TryGetBool  (section, JoinKey,          out var j))  _join          = j;
      return 0;
    });
  }

  private static void SaveOptions()
  {
    _ = ToolsOptionStore.Update(SectionName, section =>
    {
      section[StrengthStartKey] = _strengthStart;
      section[StrengthEndKey]   = _strengthEnd;
      section[CopyKey]          = _copy;
      section[JoinKey]          = _join;
    });
  }

  // ─ Conduits ─────────────────────────────────────────────────────────────────
  private sealed class HighlightConduit : DisplayConduit
  {
    private Curve? _first, _second;
    public void SetCurves(Curve? first, Curve? second) { _first = first; _second = second; }
    protected override void DrawForeground(DrawEventArgs e)
    {
      if (_first  != null) e.Display.DrawCurve(_first,  Color.FromArgb(0, 210, 90),  3); // green
      if (_second != null) e.Display.DrawCurve(_second, Color.FromArgb(30, 144, 255), 3); // blue
    }
  }

  private sealed class PreviewConduit : DisplayConduit
  {
    private Curve? _smoothed, _original;
    public void SetCurves(Curve? smoothed, Curve? original) { _smoothed = smoothed; _original = original; }
    protected override void DrawForeground(DrawEventArgs e)
    {
      if (_smoothed != null) e.Display.DrawCurve(_smoothed, Color.Cyan, 2);
      if (_original != null)
        // Bright gold before any neighbour picked; faint when preview is shown
        e.Display.DrawCurve(_original, _smoothed != null ? Color.FromArgb(100, 100, 25) : Color.FromArgb(240, 200, 0), _smoothed != null ? 1 : 2);
    }
  }
}
