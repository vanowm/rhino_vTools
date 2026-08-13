using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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
  private const string SmoothAllKey     = "smoothAll";
  private const string Tag              = "vSmooth";

  private static double _strengthStart = 1.0;
  private static double _strengthEnd   = 1.0;
  private static bool   _copy;
  private static bool   _join;
  private static bool   _smoothAll;

  public override string EnglishName => "vSmooth";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    double tol = doc.ModelAbsoluteTolerance;
    Log.Write(Tag,
      $"BEGIN sStart={_strengthStart} sEnd={_strengthEnd}" +
      $" copy={_copy} join={_join} smoothAll={_smoothAll}");
    var initialSelection = doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();
    var preselectedCurves = doc.Objects.GetSelectedObjects(false, false)
      .Where(obj => obj.Geometry is Curve)
      .ToList();

    // ─ 1. Select target curve ────────────────────────────────────────────────
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select curve to smooth");
    go.GeometryFilter  = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect     = false;
    go.EnablePreSelect(preselectedCurves.Count == 0, true);
    go.AlreadySelectedObjectSelect = true;
    go.EnableUnselectObjectsOnExit(false);
    go.EnableClearObjectsOnEntry(false);
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);
    go.AcceptNumber(true, true);
    go.AcceptString(true);

    RhinoObject? targetObj = ChoosePreselectedTarget(preselectedCurves, tol);
    if (targetObj != null)
      Log.Write(Tag, $"using preselected target={targetObj.Id}");

    while (targetObj == null)
    {
      go.ClearCommandOptions();
      var ss = new OptionDouble(_strengthStart, 0.0, 2.0);
      var se = new OptionDouble(_strengthEnd, 0.0, 2.0);
      var co = new OptionToggle(_copy, "No", "Yes");
      var jo = new OptionToggle(_join, "No", "Yes");
      var ao = new OptionToggle(_smoothAll, "No", "Yes");
      go.AddOptionDouble("StrengthStart", ref ss);
      go.AddOptionDouble("StrengthEnd", ref se);
      go.AddOptionToggle("Copy", ref co);
      go.AddOptionToggle("Join", ref jo);
      go.AddOptionToggle("SmoothAll", ref ao);
      var res = go.GetMultiple(1, 1);
      if (ss.CurrentValue != _strengthStart || se.CurrentValue != _strengthEnd ||
          co.CurrentValue != _copy || jo.CurrentValue != _join ||
          ao.CurrentValue != _smoothAll)
      {
        _strengthStart = ss.CurrentValue;
        _strengthEnd = se.CurrentValue;
        _copy = co.CurrentValue;
        _join = jo.CurrentValue;
        _smoothAll = ao.CurrentValue;
        SaveOptions();
      }
      if (go.CommandResult() != Result.Success) return go.CommandResult();
      if (res == GetResult.Option) continue;
      if (res == GetResult.Number)
      {
        _strengthStart = Math.Clamp(go.Number(), 0.0, 2.0);
        SaveOptions();
        continue;
      }
      if (res == GetResult.String)
      {
        if (TryApplyStrengthInput(go.StringResult(), out var error))
          SaveOptions();
        else
          RhinoApp.WriteLine($"vSmooth: {error}");
        continue;
      }
      if (res == GetResult.Object)
      {
        targetObj = go.Object(0)?.Object();
        Log.Write(Tag,
          $"target pick id={targetObj?.Id} preselected={go.ObjectsWerePreselected}" +
          $" initialCurveCount={preselectedCurves.Count}");
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
    var explicitlyDeselected = new HashSet<Guid>();

    foreach (var preselected in preselectedCurves)
    {
      if (preselected.Id == targetId || !allIds.Contains(preselected.Id)) continue;
      bool atStart = candStart.Any(candidate => candidate.Id == preselected.Id);
      bool conn1AtStart = conn1 != null && candStart.Any(candidate => candidate.Id == conn1.Id);
      if (conn1 == null) conn1 = preselected;
      else if (atStart != conn1AtStart && conn2 == null) conn2 = preselected;
    }
    Log.Write(Tag, $"seeded preselection conn1={conn1?.Id} conn2={conn2?.Id}");

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
        ? ComputeSmoothed(sourceCurve, atStart, atEnd, ss, se, tol, _smoothAll)
        : null;
      Log.Write(Tag, $"preview smoothed={smoothed != null} conn1={conn1?.Id} conn2={conn2?.Id}");
      preview.SetCurves(smoothed?.ChangedCurves, sourceCurve);
      doc.Views.Redraw();
    }

    var goNb = new GetObject();
    goNb.EnableTransparentCommands(true);
    goNb.GeometryFilter              = ObjectType.Curve;
    goNb.SubObjectSelect             = false;
    goNb.DeselectAllBeforePostSelect = false;
    goNb.EnablePreSelect(false, false);
    goNb.AlreadySelectedObjectSelect = true;
    goNb.EnableClearObjectsOnEntry(false);
    goNb.EnableUnselectObjectsOnExit(false);
    goNb.SetCustomGeometryFilter((obj, _, _) => allIds.Contains(obj.Id) || obj.Id == targetId);
    goNb.AcceptNothing(true);
    goNb.AcceptNumber(true, true);
    goNb.AcceptString(true);

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
        var ao = new OptionToggle(_smoothAll, "No", "Yes");
        goNb.AddOptionDouble("StrengthStart", ref ss);
        goNb.AddOptionDouble("StrengthEnd",   ref se);
        goNb.AddOptionToggle("Copy", ref co);
        goNb.AddOptionToggle("Join", ref jo);
        goNb.AddOptionToggle("SmoothAll", ref ao);

        bool any = conn1 != null || conn2 != null;
        goNb.SetCommandPrompt(any
          ? "Click neighbour to change, Enter to accept"
          : "Click a connected curve to smooth into");

        var res = goNb.Get();

        bool changed = false;
        if (ss.CurrentValue != _strengthStart) { _strengthStart = ss.CurrentValue; changed = true; }
        if (se.CurrentValue != _strengthEnd)   { _strengthEnd   = se.CurrentValue; changed = true; }
        if (co.CurrentValue != _copy)          { _copy          = co.CurrentValue; changed = true; }
        if (jo.CurrentValue != _join)          { _join          = jo.CurrentValue; changed = true; }
        if (ao.CurrentValue != _smoothAll)     { _smoothAll     = ao.CurrentValue; changed = true; }
        if (changed) { SaveOptions(); Refresh(); }

        if (res == GetResult.Option) continue;

        if (res == GetResult.Number)
        {
          _strengthStart = Math.Clamp(goNb.Number(), 0.0, 2.0);
          SaveOptions(); Refresh();
          continue;
        }

        if (res == GetResult.String)
        {
          if (TryApplyStrengthInput(goNb.StringResult(), out var error))
          {
            SaveOptions();
            Refresh();
          }
          else
          {
            RhinoApp.WriteLine($"vSmooth: {error}");
          }
          continue;
        }

        if (res == GetResult.Object)
        {
          var picked = goNb.Object(0)?.Object();
          if (picked?.Id == targetId)
          {
            Log.Write(Tag, "target clicked: deselect and restart target pick");
            explicitlyDeselected.Add(targetId);
            if (conn1 != null) explicitlyDeselected.Add(conn1.Id);
            if (conn2 != null) explicitlyDeselected.Add(conn2.Id);
            initialSelection.Remove(targetId);
            doc.Objects.FindId(targetId)?.Select(false);
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
              explicitlyDeselected.Add(picked.Id);
              doc.Objects.FindId(picked.Id)?.Select(false); conn1 = conn2; conn2 = null;
            }
            else if (conn2?.Id == picked.Id)
            {
              explicitlyDeselected.Add(picked.Id);
              doc.Objects.FindId(picked.Id)?.Select(false); conn2 = null;
            }
            else if (conn1 == null)
            {
              explicitlyDeselected.Remove(picked.Id);
              conn1 = picked;
            }
            else if (isAtStart == conn1AtStart)
            {
              // Same geometric end as conn1 → replace conn1
              explicitlyDeselected.Add(conn1.Id);
              explicitlyDeselected.Remove(picked.Id);
              doc.Objects.FindId(conn1.Id)?.Select(false); conn1 = picked;
            }
            else
            {
              // Opposite end → set/replace conn2
              if (conn2 != null)
              {
                explicitlyDeselected.Add(conn2.Id);
                doc.Objects.FindId(conn2.Id)?.Select(false);
              }
              explicitlyDeselected.Remove(picked.Id);
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
      doc.Objects.UnselectAll();
      foreach (var id in initialSelection.Where(id => !explicitlyDeselected.Contains(id)))
        doc.Objects.FindId(id)?.Select(true);
      doc.Views.Redraw();
    }

    Log.Write(Tag, $"conn1={conn1?.Id} conn2={conn2?.Id}");
    if (restart) return RunCommand(doc, mode); // re-enter so user picks a new target
    if (!accepted) return Result.Cancel;

    // ─ 5. Commit ────────────────────────────────────────────────
    var (cAtStart, cAtEnd, csS, csE) = GetGeoMap();
    var final = ComputeSmoothed(sourceCurve, cAtStart, cAtEnd, csS, csE, tol, _smoothAll);
    if (final == null) { RhinoApp.WriteLine("vSmooth: could not compute."); return Result.Failure; }

    if (_join)
    {
      JoinNeighbors(doc, targetObj.Id, final.Target, cAtStart, cAtEnd,
        final.Neighbors, tol, copyMode: _copy);
    }
    else if (_copy)
    {
      if (!AddCurveCopy(doc, targetObj, final.Target)) return Result.Failure;
      foreach (var pair in final.Neighbors)
      {
        var source = doc.Objects.FindId(pair.Key);
        if (source != null && !AddCurveCopy(doc, source, pair.Value)) return Result.Failure;
      }
    }
    else
    {
      if (!doc.Objects.Replace(targetObj.Id, final.Target)) return Result.Failure;
      foreach (var pair in final.Neighbors)
      {
        if (!doc.Objects.Replace(pair.Key, pair.Value)) return Result.Failure;
      }
    }

    Log.Write(Tag, "END");
    doc.Views.Redraw();
    return Result.Success;
  }

  // ─ Smoothing core ──────────────────────────────────────────────────────────
  private sealed class SmoothResult
  {
    public SmoothResult(Curve target)
    {
      Target = target;
    }

    public Curve Target { get; }
    public Dictionary<Guid, Curve> Neighbors { get; } = new();
    public IEnumerable<Curve> ChangedCurves => new[] { Target }.Concat(Neighbors.Values);
  }

  private static SmoothResult? ComputeSmoothed(
    Curve curve, RhinoObject? connStart, RhinoObject? connEnd,
    double strengthStart, double strengthEnd, double tol, bool smoothAll)
  {
    var nurbs = ToEditableNurbs(curve);
    if (nurbs == null) return null;
    var result = new SmoothResult(nurbs);

    SmoothJunction(result, curve, nurbs, connStart, targetAtStart: true,
      strengthStart, tol, smoothAll);
    SmoothJunction(result, curve, nurbs, connEnd, targetAtStart: false,
      strengthEnd, tol, smoothAll);
    return result;
  }

  private static void SmoothJunction(
    SmoothResult result, Curve originalTarget, NurbsCurve target,
    RhinoObject? neighborObject, bool targetAtStart, double strength,
    double tol, bool smoothAll)
  {
    if (neighborObject?.Geometry is not Curve neighbor) return;

    Point3d junction = targetAtStart ? originalTarget.PointAtStart : originalTarget.PointAtEnd;
    if (!TryGetConnectedEnd(junction, targetAtStart, neighbor, tol, out bool neighborAtStart))
      return;

    var desired = DesiredTangent(junction, targetAtStart, neighbor, tol);
    if (!desired.IsValid || !desired.Unitize()) return;

    var tangent = desired;
    if (smoothAll)
    {
      var current = targetAtStart ? originalTarget.TangentAtStart : originalTarget.TangentAtEnd;
      if (current.IsValid && current.Unitize())
      {
        var shared = current + desired;
        if (shared.IsValid && shared.Unitize()) tangent = shared;
      }
    }

    SetEndpointTangent(target, targetAtStart, tangent, strength, tol);
    Log.Write(Tag,
      $"  junction targetStart={targetAtStart} neighbor={neighborObject.Id}" +
      $" neighborStart={neighborAtStart} smoothAll={smoothAll} tangent={tangent}");

    if (!smoothAll) return;

    if (!result.Neighbors.TryGetValue(neighborObject.Id, out var adjustedCurve))
    {
      adjustedCurve = ToEditableNurbs(neighbor);
      if (adjustedCurve == null) return;
      result.Neighbors[neighborObject.Id] = adjustedCurve;
    }

    if (adjustedCurve is NurbsCurve neighborNurbs)
    {
      var neighborTravel = targetAtStart == neighborAtStart ? -tangent : tangent;
      SetEndpointTangent(neighborNurbs, neighborAtStart, neighborTravel, strength, tol);
    }
  }

  private static NurbsCurve? ToEditableNurbs(Curve curve)
  {
    var nurbs = curve.ToNurbsCurve()?.Duplicate() as NurbsCurve;
    if (nurbs == null) return null;
    if (nurbs.Degree < 3) nurbs.IncreaseDegree(3);
    return nurbs;
  }

  private static void SetEndpointTangent(
    NurbsCurve nurbs, bool atStart, Vector3d tangent, double strength, double tol)
  {
    if (nurbs.Points.Count < 2 || !tangent.IsValid || !tangent.Unitize()) return;
    int last = nurbs.Points.Count - 1;

    if (atStart)
    {
      Point3d point = nurbs.Points[0].Location;
      double distance = Math.Max(point.DistanceTo(nurbs.Points[1].Location) * strength, tol);
      nurbs.Points[1] = new ControlPoint(
        point + tangent * distance, nurbs.Points[1].Weight);
    }
    else
    {
      Point3d point = nurbs.Points[last].Location;
      double distance = Math.Max(point.DistanceTo(nurbs.Points[last - 1].Location) * strength, tol);
      nurbs.Points[last - 1] = new ControlPoint(
        point - tangent * distance, nurbs.Points[last - 1].Weight);
    }
  }

  // Returns desired tangent of smoothed curve at P (in curve travel direction) for G1.
  private static Vector3d DesiredTangent(Point3d P, bool isStart, Curve nb, double tol)
  {
    if (!TryGetConnectedEnd(P, isStart, nb, tol, out bool atNbStart))
    {
      Log.Write(Tag, "  DesiredTangent -> Unset (no endpoint match)");
      return Vector3d.Unset;
    }
    Vector3d result;
    if (isStart)
      result = atNbStart ? -nb.TangentAtStart : nb.TangentAtEnd;
    else
      result = atNbStart ? nb.TangentAtStart : -nb.TangentAtEnd;
    Log.Write(Tag, $"  -> {result}");
    return result;
  }

  private static bool TryGetConnectedEnd(
    Point3d point, bool targetAtStart, Curve neighbor, double tol, out bool neighborAtStart)
  {
    bool atStart = neighbor.PointAtStart.DistanceTo(point) <= tol;
    bool atEnd = neighbor.PointAtEnd.DistanceTo(point) <= tol;
    if (!atStart && !atEnd)
    {
      neighborAtStart = false;
      return false;
    }

    neighborAtStart = atStart && (!atEnd || !targetAtStart);
    return true;
  }

  // ─ Helpers ─────────────────────────────────────────────────────────────────
  private static RhinoObject? ChoosePreselectedTarget(
    IReadOnlyList<RhinoObject> preselected, double tol)
  {
    if (preselected.Count == 0) return null;
    if (preselected.Count == 1) return preselected[0];

    RhinoObject? best = null;
    int bestConnectedEnds = -1;
    int bestConnections = -1;
    foreach (var candidate in preselected)
    {
      if (candidate.Geometry is not Curve curve) continue;
      int atStart = 0;
      int atEnd = 0;
      foreach (var other in preselected)
      {
        if (other.Id == candidate.Id || other.Geometry is not Curve otherCurve) continue;
        if (otherCurve.PointAtStart.DistanceTo(curve.PointAtStart) <= tol ||
            otherCurve.PointAtEnd.DistanceTo(curve.PointAtStart) <= tol)
          atStart++;
        if (otherCurve.PointAtStart.DistanceTo(curve.PointAtEnd) <= tol ||
            otherCurve.PointAtEnd.DistanceTo(curve.PointAtEnd) <= tol)
          atEnd++;
      }

      int connectedEnds = (atStart > 0 ? 1 : 0) + (atEnd > 0 ? 1 : 0);
      int connections = atStart + atEnd;
      if (connectedEnds > bestConnectedEnds ||
          connectedEnds == bestConnectedEnds && connections > bestConnections)
      {
        best = candidate;
        bestConnectedEnds = connectedEnds;
        bestConnections = connections;
      }
    }

    Log.Write(Tag,
      $"preselected target={best?.Id} count={preselected.Count}" +
      $" connectedEnds={bestConnectedEnds} connections={bestConnections}");
    return best ?? preselected[0];
  }

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

  private static bool AddCurveCopy(RhinoDoc doc, RhinoObject source, Curve curve)
  {
    var id = doc.Objects.AddCurve(curve, source.Attributes.Duplicate());
    if (id == Guid.Empty) return false;
    InheritGroups(doc, source, id);
    return true;
  }

  private static void JoinNeighbors(RhinoDoc doc, Guid smoothedId, Curve smoothed,
    RhinoObject? cS, RhinoObject? cE, IReadOnlyDictionary<Guid, Curve> adjustedNeighbors,
    double tol, bool copyMode)
  {
    var pieces = new List<Curve> { smoothed.DuplicateCurve() };
    var del    = new List<Guid>();
    if (!copyMode) del.Add(smoothedId);
    void Add(RhinoObject? n)
    {
      if (n?.Geometry is not Curve nc) return;
      if (adjustedNeighbors.TryGetValue(n.Id, out var adjusted)) nc = adjusted;
      pieces.Add(nc.DuplicateCurve());
      if (!copyMode) del.Add(n.Id);
    }
    Add(cS);
    if (cE?.Id != cS?.Id) Add(cE);
    var joined = Curve.JoinCurves(pieces, tol);
    if (joined == null || joined.Length == 0) return;
    foreach (var id in del.Distinct()) doc.Objects.Delete(id, true);
    foreach (var jc in joined) doc.Objects.AddCurve(jc);
  }

  private static bool TryApplyStrengthInput(string? input, out string error)
  {
    error = "enter strength as start, start,end, or ,end.";
    var value = input?.Trim() ?? string.Empty;
    if (value.Length == 0)
      return false;

    var parts = value.Split(',');
    if (parts.Length > 2)
      return false;

    if (parts.Length == 1)
    {
      if (!TryParseStrength(parts[0], out var start))
        return false;
      _strengthStart = start;
      return true;
    }

    var hasStart = !string.IsNullOrWhiteSpace(parts[0]);
    var hasEnd = !string.IsNullOrWhiteSpace(parts[1]);
    if (!hasStart && !hasEnd)
      return false;

    double? parsedStart = null;
    double? parsedEnd = null;
    if (hasStart)
    {
      if (!TryParseStrength(parts[0], out var start))
        return false;
      parsedStart = start;
    }
    if (hasEnd)
    {
      if (!TryParseStrength(parts[1], out var end))
        return false;
      parsedEnd = end;
    }

    if (parsedStart.HasValue)
      _strengthStart = parsedStart.Value;
    if (parsedEnd.HasValue)
      _strengthEnd = parsedEnd.Value;

    return true;
  }

  private static bool TryParseStrength(string input, out double strength)
  {
    if (!double.TryParse(
          input.Trim(),
          NumberStyles.Float,
          CultureInfo.CurrentCulture,
          out strength) &&
        !double.TryParse(
          input.Trim(),
          NumberStyles.Float,
          CultureInfo.InvariantCulture,
          out strength))
    {
      return false;
    }

    strength = Math.Clamp(strength, 0.0, 2.0);
    return true;
  }

  private static void LoadOptions()
  {
    ToolsOptionStore.Read(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetDouble(section, StrengthStartKey, out var ss)) _strengthStart = Math.Clamp(ss, 0.0, 2.0);
      if (ToolsOptionStore.TryGetDouble(section, StrengthEndKey,   out var se)) _strengthEnd   = Math.Clamp(se, 0.0, 2.0);
      if (ToolsOptionStore.TryGetBool  (section, CopyKey,          out var c))  _copy          = c;
      if (ToolsOptionStore.TryGetBool  (section, JoinKey,          out var j))  _join          = j;
      if (ToolsOptionStore.TryGetBool  (section, SmoothAllKey,     out var a))  _smoothAll     = a;
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
      section[SmoothAllKey]     = _smoothAll;
    });
  }

  // ─ Conduits ─────────────────────────────────────────────────────────────────
  private sealed class HighlightConduit : DisplayConduit
  {
    private Curve? _first, _second;
    public void SetCurves(Curve? first, Curve? second) { _first = first; _second = second; }
    protected override void DrawForeground(DrawEventArgs e)
    {
      if (_first  != null) PreviewDisplay.DrawCurve(e.Display, _first,  Color.FromArgb(0, 210, 90),  2); // green
      if (_second != null) PreviewDisplay.DrawCurve(e.Display, _second, Color.FromArgb(30, 144, 255), 2); // blue
    }
  }

  private sealed class PreviewConduit : DisplayConduit
  {
    private List<Curve> _smoothed = new();
    private Curve? _original;
    public void SetCurves(IEnumerable<Curve>? smoothed, Curve? original)
    {
      _smoothed = smoothed?.ToList() ?? new List<Curve>();
      _original = original;
    }
    protected override void DrawForeground(DrawEventArgs e)
    {
      foreach (var curve in _smoothed) PreviewDisplay.DrawCurve(e.Display, curve, Color.Cyan, 1);
      if (_original != null)
        // Bright gold before any neighbour picked; faint when preview is shown
        PreviewDisplay.DrawCurve(
          e.Display,
          _original,
          _smoothed.Count > 0 ? Color.FromArgb(100, 100, 25) : Color.FromArgb(240, 200, 0),
          _smoothed.Count > 0 ? 0 : 1);
    }
  }
}
