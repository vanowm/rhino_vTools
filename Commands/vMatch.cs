using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands
{
  /// <summary>
  /// vMatch — click near an edge mate dot on a flat unrolled part;
  /// the neighbour part is moved and rotated so its mating edge aligns
  /// with the selected edge at the specified gap distance.
  /// Auto sub-mode assembles a whole selection via BFS (with optional
  /// RandStart / RandNext randomisation).
  /// </summary>
  public sealed class vMatch : Command
  {
    // ── Constants shared with vUnrollSrf / MultiUnroll2.py ────────────────
    internal const string EdgeMateName        = "MultiUnroll_EdgeMate";
    internal const string EdgeMateIdKey       = "MultiUnrollEdgeMateId";
    internal const string EdgePartNumKey      = "MultiUnrollPartNumber";
    internal const string EdgeMatePartNumKey  = "MultiUnrollMatePartNumber";
    internal const string EdgeMateReversedKey = "MultiUnrollMateReversed";

    // ── Persistent settings ───────────────────────────────────────────────
    private const string SectionName   = "vMatch";
    private const string KeyDist       = "distance";
    private const string KeyRandStart  = "randStart";
    private const string KeyRandNext   = "randNext";

    private static double _distance   = 2.0;
    private static bool   _randStart  = false;
    private static bool   _randNext   = false;

    private static readonly Random _rng = new Random();

    private const double EdgeHoverRadiusPixels = 12.0;
    private static readonly Color SourceEdgeHighlightColor = Color.Orange;
    private static readonly Color SourceDotHighlightColor = Color.Gold;
    private static readonly Color MateDotHighlightColor = Color.Magenta;
    private static readonly Color MatePartHighlightColor = Color.Cyan;

    public override string EnglishName => "vMatch";

    // ── Dot record ─────────────────────────────────────────────────────────
    private sealed class Dot
    {
      public Guid    Id       { get; }
      public Point3d Position { get; set; }
      public string  MateId   { get; }
      public string  PartNum  { get; }
      public Dot(Guid id, Point3d pos, string mateId, string partNum)
      { Id = id; Position = pos; MateId = mateId; PartNum = partNum; }
    }

    private sealed class MateEdge
    {
      public Curve Curve { get; }
      public Point3d[] Samples { get; }
      public List<Dot> Dots { get; } = new List<Dot>();

      public MateEdge(Curve curve)
      {
        Curve = curve;
        Samples = CurveScreenSamples(curve);
      }
    }

    private sealed class MatchMove
    {
      public List<Guid> ObjectIds { get; set; }
      public Transform Forward { get; }
      public Transform Reverse { get; }

      public MatchMove(IEnumerable<Guid> objectIds, Transform forward, Transform reverse)
      {
        ObjectIds = objectIds.ToList();
        Forward = forward;
        Reverse = reverse;
      }
    }

    private sealed class MatchHistoryRequest
    {
      public bool Redo { get; }
      public MatchHistoryRequest(bool redo) { Redo = redo; }
    }

    private sealed class MateEdgePicker : GetPoint
    {
      private readonly RhinoDoc _doc;
      private readonly IReadOnlyList<Dot> _dots;
      private readonly IReadOnlyList<MateEdge> _edges;
      private MateEdge? _activeEdge;

      public MateEdgePicker(
        RhinoDoc doc,
        IReadOnlyList<Dot> dots,
        IReadOnlyList<MateEdge> edges)
      {
        _doc = doc;
        _dots = dots;
        _edges = edges;
        PermitObjectSnap(false);
        EnableObjectSnapCursors(false);
        PermitOrthoSnap(false);
        PermitTabMode(false);
      }

      public Dot? SourceDot { get; private set; }
      public Dot? MateDot { get; private set; }

      public void ReleaseSnap()
      {
        ClearConstraints();
        ClearSnapPoints();
        _activeEdge = null;
        SourceDot = null;
        MateDot = null;
      }

      protected override void OnMouseMove(GetPointMouseEventArgs e)
      {
        var nextEdge = FindHoveredEdge(
          _edges, e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, out var edgePoint);

        if (!ReferenceEquals(nextEdge, _activeEdge))
        {
          ClearConstraints();
          _activeEdge = nextEdge;
          if (_activeEdge != null)
            Constrain(_activeEdge.Curve, true);
        }

        SourceDot = _activeEdge?.Dots
          .OrderBy(dot => dot.Position.DistanceToSquared(edgePoint))
          .FirstOrDefault();
        MateDot = SourceDot == null
          ? null
          : _dots.FirstOrDefault(dot =>
              dot.Id != SourceDot.Id &&
              string.Equals(dot.MateId, SourceDot.MateId, StringComparison.Ordinal));

        if (MateDot == null)
          SourceDot = null;

        base.OnMouseMove(e);
      }

      protected override void OnDynamicDraw(GetPointDrawEventArgs e)
      {
        if (_activeEdge != null && SourceDot != null && MateDot != null)
        {
          DrawMateHighlight(_doc, e.Display, MateDot);
          e.Display.DrawCurve(_activeEdge.Curve, SourceEdgeHighlightColor, 3);
          DrawDotHighlight(_doc, e.Display, SourceDot, SourceDotHighlightColor);
        }

        base.OnDynamicDraw(e);
      }
    }

    // ── Entry point ────────────────────────────────────────────────────────
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      LoadSettings();

      var dots = ScanDots(doc);
      if (dots.Count == 0)
      {
        RhinoApp.WriteLine("vMatch: no edge mate dots found — run vUnrollSrf with EdgeDots=On first");
        return Result.Nothing;
      }

      var undoMoves = new Stack<MatchMove>();
      var redoMoves = new Stack<MatchMove>();
      while (true)
      {
        var mateEdges = BuildMateEdges(doc, dots);
        var gp = new MateEdgePicker(doc, dots, mateEdges);
        gp.EnableTransparentCommands(true);
        gp.SetCommandPrompt("Click a highlighted edge to match its part");
        int idxDist = gp.AddOption("Distance", $"{_distance:G}");
        int idxAuto = gp.AddOption("Auto");
        int idxRedo = gp.AddOption("Redo", string.Empty, true);
        gp.AcceptNumber(true, true);
        gp.AcceptNothing(false);
        gp.AcceptUndo(true);

        var res = gp.Get();
        var src = gp.SourceDot;
        var mate = gp.MateDot;
        gp.ReleaseSnap();

        if (res == GetResult.Undo)
        {
          ApplyMatchHistory(doc, undoMoves, redoMoves, false);
          dots = ScanDots(doc);
          continue;
        }

        if (gp.CommandResult() == Result.Cancel) break;

        if (res == GetResult.Number)
        {
          double v = gp.Number();
          if (v >= 0.0) { _distance = v; SaveSettings(); }
          continue;
        }

        if (res == GetResult.Option)
        {
          var opt = gp.Option();
          if (opt != null && opt.Index == idxRedo)
          {
            ApplyMatchHistory(doc, undoMoves, redoMoves, true);
            dots = ScanDots(doc);
            continue;
          }
          if (opt != null && opt.Index == idxAuto)
          {
            dots = AutoAlign(doc, dots, _distance);
            undoMoves.Clear();
            redoMoves.Clear();
            continue;
          }
          // Distance — sub-prompt (memory rule: no AddOptionDouble)
          var gs = new GetString();
          gs.SetCommandPrompt($"Gap distance");
          gs.SetDefaultString($"{_distance:G}");
          gs.AcceptNothing(true);
          if (gs.Get() == GetResult.String &&
              double.TryParse(gs.StringResult().Trim(),
                              NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
              && v >= 0.0)
          {
            _distance = v;
            SaveSettings();
          }
          continue;
        }

        if (gp.CommandResult() != Result.Success) break;

        SaveSettings();
        if (src == null || mate == null) continue;

        int srcGrp  = GrpOf(doc, src.Id);
        int mateGrp = GrpOf(doc, mate.Id);
        if (mateGrp < 0) continue;

        var srcObjs  = srcGrp >= 0 ? ObjsInGrp(doc, srcGrp) : new List<Guid>();
        var mateObjs = ObjsInGrp(doc, mateGrp);
        if (mateObjs.Count == 0) continue;

        var srcTang  = Tang2d(src.Position,  NakedEdges(doc, srcObjs));
        var mateTang = Tang2d(mate.Position, NakedEdges(doc, mateObjs));
        if (srcTang == null || mateTang == null) continue;

        var srcOut = Outward2d(doc, src.Position, srcTang.Value, srcObjs);
        var target = new Point3d(src.Position.X + srcOut.X * _distance,
                                 src.Position.Y + srcOut.Y * _distance, 0.0);

        var xf = PlaceXform(doc, srcTang.Value, srcOut, target,
                             mate.Position, mateTang.Value, mateObjs);
        if (!xf.HasValue || !xf.Value.TryGetInverse(out var inverse)) continue;

        var move = new MatchMove(mateObjs, xf.Value, inverse);
        if (!ApplyMatchMove(doc, move, true)) continue;
        undoMoves.Push(move);
        redoMoves.Clear();
        dots = ScanDots(doc);
      }

      return Result.Success;
    }

    private static bool ApplyMatchHistory(
      RhinoDoc doc,
      Stack<MatchMove> undoMoves,
      Stack<MatchMove> redoMoves,
      bool redo)
    {
      var source = redo ? redoMoves : undoMoves;
      var destination = redo ? undoMoves : redoMoves;
      if (!source.TryPop(out var move))
        return false;

      if (!ApplyMatchMove(doc, move, redo))
      {
        source.Push(move);
        return false;
      }

      destination.Push(move);
      return true;
    }

    private static bool ApplyMatchMove(RhinoDoc doc, MatchMove move, bool forward)
    {
      var transform = forward ? move.Forward : move.Reverse;
      var transformedIds = new List<Guid>(move.ObjectIds.Count);

      doc.Views.RedrawEnabled = false;
      try
      {
        foreach (var id in move.ObjectIds)
        {
          var transformedId = doc.Objects.Transform(id, transform, true);
          if (transformedId != Guid.Empty)
            transformedIds.Add(transformedId);
        }
      }
      finally
      {
        doc.Views.RedrawEnabled = true;
      }

      if (transformedIds.Count == 0)
        return false;

      move.ObjectIds = transformedIds;
      doc.Views.Redraw();
      return true;
    }

    // ── Auto sub-mode — inner loop with persistent multi-selection ─────────
    private static List<Dot> AutoAlign(RhinoDoc doc, List<Dot> allDots, double distance)
    {
      var brepsFilt = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion;

      while (true)
      {
        // Pass 1: snapshot whatever is currently selected (instant, no prompt)
        var goPre = new GetObject();
        goPre.EnableTransparentCommands(true);
        goPre.GeometryFilter  = brepsFilt;
        goPre.SubObjectSelect = false;
        goPre.GroupSelect     = true;
        goPre.EnablePreSelect(true, true);
        goPre.EnablePostSelect(false);
        goPre.GetMultiple(0, 0);
        var snapIds = new HashSet<Guid>(
            Enumerable.Range(0, goPre.ObjectCount).Select(i => goPre.Object(i).ObjectId));

        // Pass 2: interactive — add / remove parts, toggle options
        var optRs  = new OptionToggle(_randStart, "Off", "On");
        var optRn  = new OptionToggle(_randNext,  "Off", "On");

        var go = new GetObject();
        go.EnableTransparentCommands(true);
        go.SetCommandPrompt("Add/remove parts, Enter=run");
        go.GeometryFilter            = brepsFilt;
        go.SubObjectSelect           = false;
        go.GroupSelect               = true;
        go.EnablePreSelect(false, false);
        go.EnablePostSelect(true);
        go.AcceptNothing(true);
        go.EnableClearObjectsOnEntry(false);
        go.EnableUnselectObjectsOnExit(false);
        go.DeselectAllBeforePostSelect = false;
        go.AlreadySelectedObjectSelect = true;

        int idxBack = go.AddOption("Back");
        int idxDist = go.AddOption("Distance", $"{_distance:G}");
        go.AddOptionToggle("RandStart", ref optRs);
        go.AddOptionToggle("RandNext",  ref optRn);
        go.AcceptNumber(true, true);

        bool goBack = false;
        while (true)
        {
          var ires = go.GetMultiple(0, 0);
          bool rsChanged = optRs.CurrentValue != _randStart;
          bool rnChanged = optRn.CurrentValue != _randNext;
          _randStart = optRs.CurrentValue;
          _randNext  = optRn.CurrentValue;
          if (rsChanged || rnChanged) SaveSettings();

          if (go.CommandResult() == Result.Cancel)
          {
            doc.Objects.UnselectAll();
            doc.Views.Redraw();
            SaveSettings();
            return ScanDots(doc);
          }
          if (ires == GetResult.Number)
          {
            double v = go.Number();
            if (v >= 0.0)
            {
              _distance = v;
              SaveSettings();
            }
            continue;
          }
          if (ires == GetResult.Option)
          {
            var opt = go.Option();
            if (opt != null && opt.Index == idxBack) { goBack = true; break; }
            if (opt != null && opt.Index == idxDist)
            {
              var gs = new GetString();
              gs.SetCommandPrompt("Gap distance");
              gs.SetDefaultString($"{_distance:G}");
              gs.AcceptNothing(true);
              if (gs.Get() == GetResult.String &&
                  double.TryParse(gs.StringResult().Trim(),
                                  NumberStyles.Any, CultureInfo.InvariantCulture, out double dv)
                  && dv >= 0.0)
              {
                _distance = dv;
                SaveSettings();
              }
            }
            continue;
          }
          break;
        }

        SaveSettings();

        if (goBack)
        {
          doc.Objects.UnselectAll();
          doc.Views.Redraw();
          return ScanDots(doc);
        }

        // XOR: objects clicked in both passes = user toggled them off
        var interIds = new HashSet<Guid>(
            Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId));
        var finalIds = new HashSet<Guid>(snapIds);
        finalIds.SymmetricExceptWith(interIds);

        // Sync Rhino selection to the XOR result (deselect the toggled-off ones)
        foreach (var id in snapIds.Intersect(interIds))
          doc.Objects.FindId(id)?.Select(false);

        if (finalIds.Count == 0) continue;

        // Collect group indices in first-seen order
        var selGrpList = new List<int>();
        var seenGrps   = new HashSet<int>();
        foreach (var id in finalIds)
        {
          int g = GrpOf(doc, id);
          if (g >= 0 && seenGrps.Add(g)) selGrpList.Add(g);
        }
        if (selGrpList.Count == 0) continue;

        var selGrpSet = new HashSet<int>(selGrpList);
        int rootGrp   = _randStart
          ? selGrpList[_rng.Next(selGrpList.Count)]
          : selGrpList[0];

        // mate_id → dots lookup
        var mateMap = new Dictionary<string, List<Dot>>();
        foreach (var d in allDots)
        {
          if (!mateMap.TryGetValue(d.MateId, out var lst))
            mateMap[d.MateId] = lst = new List<Dot>();
          lst.Add(d);
        }

        var placed = new HashSet<int> { rootGrp };
        var queue  = new List<int>    { rootGrp };
        // Working cached dot positions updated after each move
        var dotPts = allDots.ToDictionary(d => d.Id, d => d.Position);

        doc.Views.RedrawEnabled = false;
        try
        {
          while (queue.Count > 0)
          {
            int qi      = _randNext && queue.Count > 1 ? _rng.Next(queue.Count) : 0;
            int currGrp = queue[qi];
            queue.RemoveAt(qi);

            var currDots = allDots.Where(d => GrpOf(doc, d.Id) == currGrp).ToList();
            foreach (var src in currDots)
            {
              if (!mateMap.TryGetValue(src.MateId, out var mList)) continue;
              var mateInfo = mList.FirstOrDefault(m => m.Id != src.Id);
              if (mateInfo == null) continue;

              int mateGrp = GrpOf(doc, mateInfo.Id);
              if (!selGrpSet.Contains(mateGrp) || placed.Contains(mateGrp)) continue;

              var srcDotPt  = dotPts[src.Id];
              var srcObjs   = ObjsInGrp(doc, currGrp);
              var mateObjs  = ObjsInGrp(doc, mateGrp);
              if (srcObjs.Count == 0 || mateObjs.Count == 0) continue;

              var srcTang  = Tang2d(srcDotPt,           NakedEdges(doc, srcObjs));
              var mateTang = Tang2d(dotPts[mateInfo.Id], NakedEdges(doc, mateObjs));
              if (srcTang == null || mateTang == null) continue;

              var srcOut = Outward2d(doc, srcDotPt, srcTang.Value, srcObjs);
              var target = new Point3d(srcDotPt.X + srcOut.X * distance,
                                       srcDotPt.Y + srcOut.Y * distance, 0.0);

              var xf = PlaceXform(doc, srcTang.Value, srcOut, target,
                                   dotPts[mateInfo.Id], mateTang.Value, mateObjs);
              if (!xf.HasValue) continue;

              foreach (var id in mateObjs)
                doc.Objects.Transform(id, xf.Value, true);

              // Update cached positions for moved group's dots
              foreach (var d in allDots.Where(d2 => GrpOf(doc, d2.Id) == mateGrp))
              {
                var pt = dotPts[d.Id];
                pt.Transform(xf.Value);
                dotPts[d.Id] = pt;
              }

              placed.Add(mateGrp);
              queue.Add(mateGrp);
            }
          }
        }
        finally { doc.Views.RedrawEnabled = true; }

        doc.Views.Redraw();
        allDots = ScanDots(doc);

        // Reselect all assembled parts so the user can see what moved
        foreach (var grp in selGrpSet)
          foreach (var id in ObjsInGrp(doc, grp))
            doc.Objects.FindId(id)?.Select(true);
        doc.Views.Redraw();
      }
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    private static List<Dot> ScanDots(RhinoDoc doc)
    {
      var result = new List<Dot>();
      foreach (var obj in doc.Objects)
      {
        if (obj.ObjectType != ObjectType.TextDot) continue;
        if (obj.Attributes.Name != EdgeMateName) continue;
        string mateId  = obj.Attributes.GetUserString(EdgeMateIdKey)  ?? string.Empty;
        string partNum = obj.Attributes.GetUserString(EdgePartNumKey) ?? string.Empty;
        if (string.IsNullOrEmpty(mateId)) continue;
        if (obj.Geometry is TextDot td)
          result.Add(new Dot(obj.Id, td.Point, mateId, partNum));
      }
      return result;
    }

    private static List<MateEdge> BuildMateEdges(RhinoDoc doc, IReadOnlyList<Dot> dots)
    {
      var validDots = dots
        .Where(dot => dots.Any(other =>
          other.Id != dot.Id &&
          string.Equals(other.MateId, dot.MateId, StringComparison.Ordinal)))
        .ToList();
      var result = new List<MateEdge>();
      double associationTolerance = Math.Max(doc.ModelAbsoluteTolerance * 100.0, 0.05);

      foreach (var group in validDots.GroupBy(dot => GrpOf(doc, dot.Id)))
      {
        if (group.Key < 0)
          continue;

        var edges = NakedEdges(doc, ObjsInGrp(doc, group.Key))
          .Select(curve => new MateEdge(curve))
          .Where(edge => edge.Samples.Length >= 2)
          .ToList();

        foreach (var dot in group)
        {
          MateEdge? closest = null;
          double closestDistance = double.PositiveInfinity;
          foreach (var edge in edges)
          {
            if (!edge.Curve.ClosestPoint(dot.Position, out double parameter))
              continue;
            double distance = dot.Position.DistanceTo(edge.Curve.PointAt(parameter));
            if (distance < closestDistance)
            {
              closestDistance = distance;
              closest = edge;
            }
          }

          if (closest != null && closestDistance <= associationTolerance)
            closest.Dots.Add(dot);
        }

        result.AddRange(edges.Where(edge => edge.Dots.Count > 0));
      }

      return result;
    }

    private static Point3d[] CurveScreenSamples(Curve curve)
    {
      if (curve.TryGetPolyline(out var polyline) && polyline.Count >= 2)
        return polyline.ToArray();

      try
      {
        var parameters = curve.DivideByCount(96, true);
        if (parameters != null && parameters.Length >= 2)
          return parameters.Select(curve.PointAt).ToArray();
      }
      catch
      {
      }

      return new[] { curve.PointAtStart, curve.PointAtEnd };
    }

    private static MateEdge? FindHoveredEdge(
      IReadOnlyList<MateEdge> edges,
      RhinoViewport viewport,
      int clientX,
      int clientY,
      out Point3d edgePoint)
    {
      MateEdge? bestEdge = null;
      edgePoint = Point3d.Unset;
      double bestDistanceSquared = EdgeHoverRadiusPixels * EdgeHoverRadiusPixels;

      foreach (var edge in edges)
      {
        if (!TryScreenCurvePoint(
              viewport, edge.Samples, clientX, clientY,
              out var candidatePoint, out var distanceSquared) ||
            distanceSquared > bestDistanceSquared)
          continue;

        bestDistanceSquared = distanceSquared;
        bestEdge = edge;
        edgePoint = candidatePoint;
      }

      return bestEdge;
    }

    private static bool TryScreenCurvePoint(
      RhinoViewport viewport,
      IReadOnlyList<Point3d> samples,
      int clientX,
      int clientY,
      out Point3d edgePoint,
      out double distanceSquared)
    {
      edgePoint = Point3d.Unset;
      distanceSquared = double.PositiveInfinity;
      if (samples.Count < 2)
        return false;

      Point2d previousClient;
      try { previousClient = viewport.WorldToClient(samples[0]); }
      catch { return false; }

      for (int i = 1; i < samples.Count; i++)
      {
        Point2d currentClient;
        try { currentClient = viewport.WorldToClient(samples[i]); }
        catch
        {
          try { previousClient = viewport.WorldToClient(samples[i]); }
          catch { }
          continue;
        }

        double segmentX = currentClient.X - previousClient.X;
        double segmentY = currentClient.Y - previousClient.Y;
        double segmentLengthSquared = (segmentX * segmentX) + (segmentY * segmentY);
        double u = segmentLengthSquared <= 1.0e-12
          ? 0.0
          : (((clientX - previousClient.X) * segmentX) +
             ((clientY - previousClient.Y) * segmentY)) / segmentLengthSquared;
        u = Math.Max(0.0, Math.Min(1.0, u));

        double projectedX = previousClient.X + (segmentX * u);
        double projectedY = previousClient.Y + (segmentY * u);
        double dx = clientX - projectedX;
        double dy = clientY - projectedY;
        double candidateDistanceSquared = (dx * dx) + (dy * dy);
        if (candidateDistanceSquared < distanceSquared)
        {
          distanceSquared = candidateDistanceSquared;
          edgePoint = samples[i - 1] + ((samples[i] - samples[i - 1]) * u);
        }

        previousClient = currentClient;
      }

      return edgePoint.IsValid;
    }

    private static void DrawMateHighlight(
      RhinoDoc doc,
      DisplayPipeline display,
      Dot mate)
    {
      int groupIndex = GrpOf(doc, mate.Id);
      if (groupIndex >= 0)
      {
        var material = new DisplayMaterial(MatePartHighlightColor)
        {
          Transparency = 0.55,
          BackTransparency = 0.55
        };

        foreach (var id in ObjsInGrp(doc, groupIndex))
        {
          if (id == mate.Id)
            continue;

          var geometry = doc.Objects.FindId(id)?.Geometry;
          switch (geometry)
          {
            case Brep brep:
              display.DrawBrepShaded(brep, material);
              display.DrawBrepWires(brep, MatePartHighlightColor, 2);
              break;
            case Extrusion extrusion:
              var extrusionBrep = extrusion.ToBrep();
              if (extrusionBrep != null)
              {
                display.DrawBrepShaded(extrusionBrep, material);
                display.DrawBrepWires(extrusionBrep, MatePartHighlightColor, 2);
              }
              break;
            case Surface surface:
              var surfaceBrep = surface.ToBrep();
              if (surfaceBrep != null)
              {
                display.DrawBrepShaded(surfaceBrep, material);
                display.DrawBrepWires(surfaceBrep, MatePartHighlightColor, 2);
              }
              break;
            case Mesh mesh:
              display.DrawMeshShaded(mesh, material);
              display.DrawMeshWires(mesh, MatePartHighlightColor, 2);
              break;
            case Curve curve:
              display.DrawCurve(curve, MatePartHighlightColor, 2);
              break;
          }
        }
      }

      DrawDotHighlight(doc, display, mate, MateDotHighlightColor);
    }

    private static void DrawDotHighlight(
      RhinoDoc doc,
      DisplayPipeline display,
      Dot dot,
      Color color)
    {
      if (doc.Objects.FindId(dot.Id)?.Geometry is TextDot textDot)
        display.DrawDot(textDot, Color.Black, color, color);
    }

    private static int GrpOf(RhinoDoc doc, Guid id)
    {
      var grps = doc.Objects.FindId(id)?.Attributes.GetGroupList();
      return grps != null && grps.Length > 0 ? grps[0] : -1;
    }

    private static List<Guid> ObjsInGrp(RhinoDoc doc, int grpIdx)
    {
      var ids = new List<Guid>();
      foreach (var obj in doc.Objects)
      {
        var grps = obj.Attributes.GetGroupList();
        if (grps != null && Array.IndexOf(grps, grpIdx) >= 0)
          ids.Add(obj.Id);
      }
      return ids;
    }

    private static List<Curve> NakedEdges(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var curves = new List<Curve>();
      foreach (var id in ids)
      {
        var obj  = doc.Objects.FindId(id);
        Brep? brep = null;
        if      (obj?.Geometry is Brep    b) brep = b;
        else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
        else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
        if (brep == null) continue;
        foreach (var c in brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          if (c != null) curves.Add(c);
      }
      return curves;
    }

    private static Point3d? AreaCentroid2d(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      double area = 0, wx = 0, wy = 0;
      foreach (var id in ids)
      {
        var obj  = doc.Objects.FindId(id);
        Brep? brep = null;
        if      (obj?.Geometry is Brep    b) brep = b;
        else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
        else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
        if (brep == null) continue;
        var amp = AreaMassProperties.Compute(brep);
        if (amp == null || amp.Area <= 1e-12) continue;
        area += amp.Area;
        wx   += amp.Centroid.X * amp.Area;
        wy   += amp.Centroid.Y * amp.Area;
      }
      if (area > 1e-12)
        return new Point3d(wx / area, wy / area, 0.0);
      // Fallback: bbox average
      var bbox = BoundingBox.Empty;
      bool hasBox = false;
      foreach (var id in ids)
      {
        var bb = doc.Objects.FindId(id)?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (!bb.IsValid) continue;
        bbox.Union(bb);
        hasBox = true;
      }
      return hasBox ? bbox.Center : (Point3d?)null;
    }

    private static Vector3d? Tang2d(Point3d pt, IEnumerable<Curve> edges)
    {
      Curve? best = null;
      double bestT = 0, bestD = double.MaxValue;
      foreach (var crv in edges)
      {
        if (!crv.ClosestPoint(pt, out double t)) continue;
        double d = pt.DistanceTo(crv.PointAt(t));
        if (d < bestD) { bestD = d; best = crv; bestT = t; }
      }
      if (best == null) return null;
      var tang = best.TangentAt(bestT);
      double mag = Math.Sqrt(tang.X * tang.X + tang.Y * tang.Y);
      return mag > 1e-12 ? new Vector3d(tang.X / mag, tang.Y / mag, 0.0) : (Vector3d?)null;
    }

    /// <summary>
    /// Returns the perpendicular to <paramref name="tang"/> that points AWAY
    /// from the source brep interior.  Uses brep face containment as primary
    /// test; falls back to centroid direction.
    /// </summary>
    private static Vector3d Outward2d(RhinoDoc doc, Point3d dotPt, Vector3d tang, IEnumerable<Guid> srcIds)
    {
      double tx = tang.X, ty = tang.Y;
      var pa = new Vector3d(-ty,  tx, 0.0); // 90° CCW
      var pb = new Vector3d( ty, -tx, 0.0); // 90° CW

      double tol = doc.ModelAbsoluteTolerance;
      double eps = Math.Max(tol * 20.0, 2.0);
      var testA  = new Point3d(dotPt.X + pa.X * eps, dotPt.Y + pa.Y * eps, 0.0);
      var testB  = new Point3d(dotPt.X + pb.X * eps, dotPt.Y + pb.Y * eps, 0.0);

      foreach (var id in srcIds)
      {
        var obj = doc.Objects.FindId(id);
        Brep? brep = null;
        if      (obj?.Geometry is Brep    b) brep = b;
        else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
        else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
        if (brep == null) continue;

        bool aIn = false, bIn = false;
        foreach (var face in brep.Faces)
        {
          TestFacePoint(face, testA, tol, ref aIn);
          TestFacePoint(face, testB, tol, ref bIn);
        }
        if (aIn && !bIn) return pb;
        if (bIn && !aIn) return pa;
      }

      // Fallback: centroid direction
      var centroid = AreaCentroid2d(doc, srcIds);
      if (!centroid.HasValue) return pa;
      var diff = dotPt - centroid.Value;
      return (-ty * diff.X + tx * diff.Y) >= 0.0 ? pa : pb;
    }

    private static void TestFacePoint(BrepFace face, Point3d pt, double tol, ref bool inside)
    {
      if (inside) return;
      if (!face.ClosestPoint(pt, out double u, out double v)) return;
      if (face.PointAt(u, v).DistanceTo(pt) > tol * 50.0) return;
      try
      {
        if (face.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
          inside = true;
      }
      catch { }
    }

    /// <summary>
    /// Builds the rigid transform that moves <paramref name="mateDot"/> to
    /// <paramref name="target"/> and aligns the mate edge tangent to
    /// src_tang (antiparallel first, then parallel).  Picks the rotation
    /// that places the most of the mate's bbox volume on the outward side.
    /// </summary>
    private static Transform? PlaceXform(
        RhinoDoc doc,
        Vector3d srcTang, Vector3d srcOut, Point3d target,
        Point3d mateDot, Vector3d mateTang, List<Guid> mateIds)
    {
      Transform? best = null;
      double bestScore = double.MinValue;

      foreach (var (fx, fy) in new[] { (-srcTang.X, -srcTang.Y), (srcTang.X, srcTang.Y) })
      {
        double mx = mateTang.X, my = mateTang.Y;
        double angle = Math.Atan2(mx * fy - my * fx, mx * fx + my * fy);
        var xf = Transform.Translation(target - mateDot)
               * Transform.Rotation(angle, Vector3d.ZAxis, mateDot);

        double score = 0.0;
        foreach (var id in mateIds)
        {
          var bb = doc.Objects.FindId(id)?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
          if (!bb.IsValid) continue;
          foreach (var corner in bb.GetCorners())
          {
            var pt = corner;
            pt.Transform(xf);
            var d = pt - target;
            score += d.X * srcOut.X + d.Y * srcOut.Y;
          }
        }
        if (score > bestScore) { bestScore = score; best = xf; }
      }
      return best;
    }

    // ── Settings ───────────────────────────────────────────────────────────

    private static void LoadSettings()
    {
      ToolsOptionStore.Read<int>(SectionName, s =>
      {
        if (ToolsOptionStore.TryGetDouble(s, KeyDist, out var d) && d >= 0.0)
          _distance = d;

        if (ToolsOptionStore.TryGetBool(s, KeyRandStart, out var rs))
          _randStart = rs;
        else if (ToolsOptionStore.TryGetDouble(s, KeyRandStart, out var oldRs))
          _randStart = oldRs > 0.5;

        if (ToolsOptionStore.TryGetBool(s, KeyRandNext, out var rn))
          _randNext = rn;
        else if (ToolsOptionStore.TryGetDouble(s, KeyRandNext, out var oldRn))
          _randNext = oldRn > 0.5;

        return 0;
      });
    }

    private static void SaveSettings()
    {
      var saved = ToolsOptionStore.Update(SectionName, s =>
      {
        s[KeyDist]      = _distance;
        s[KeyRandStart] = _randStart;
        s[KeyRandNext]  = _randNext;
      });
      if (!saved)
        RhinoApp.WriteLine($"vMatch: failed to save options: {ToolsOptionStore.LastError}");
    }
  }
}
