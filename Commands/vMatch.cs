namespace vTools.Commands
{
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

  /// <summary>
  /// vMatch — click near an edge mate dot on a flat unrolled part;
  /// the neighbour part is moved and rotated so its mating edge aligns
  /// with the selected edge at the specified gap distance.
  /// Auto sub-mode assembles a whole selection via BFS (with optional
  /// StartFrom / RandNext controls).
  /// </summary>
  public sealed partial class vMatch : vToolsCommand
  {
    // Option defaults
    private const double DefaultDistance = 2.0; // Separation in model units; zero or greater.
    private const AutoStartMode DefaultStartFrom = AutoStartMode.Pick; // Random chooses the first part; Pick requests a stationary center part and alternates outward sides.
    private static readonly string[] StartFromNames = ["Random","Pick"]; // Auto start modes in AutoStartMode enum order.
    private const int DefaultCenteredSide = 1; // Initial preferred root-axis side; +1 then -1 alternate after successful placements.
    private const double CenteredAxisTolerance = 1e-10; // Model-unit threshold for treating root mate-dot positions as coincident.
    private const bool DefaultRandNext = true; // In Random start mode, true randomizes subsequent parts and false is deterministic; Pick always alternates deterministically.
    private const int AutoLiveRefreshMilliseconds = 150; // Minimum milliseconds between automatic-match highlight/display updates; final results always refresh.
    private static readonly PreviewDisplay.ObjectHighlightStyle MiddlePartStyle = new( // Magenta body/dark outline and 0-1 transparency distinguish the fixed middle from selection and cyan matching.
      Color.Magenta,Color.DarkMagenta,Color.White,0.45);

    // ── Constants shared with vUnrollSrf / MultiUnroll2.py ────────────────
    internal const string EdgeMateName        = "MultiUnroll_EdgeMate"; // Object name assigned to generated matching edge dots.
    internal const string EdgeMateIdKey       = "MultiUnrollEdgeMateId"; // User-data key for the shared match identifier.
    internal const string EdgePartNumKey      = "MultiUnrollPartNumber"; // User-data key for the dot's owning part number.
    internal const string EdgeMatePartNumKey  = "MultiUnrollMatePartNumber"; // User-data key for the mating part number.
    internal const string EdgeMateReversedKey = "MultiUnrollMateReversed"; // User-data key for matching-edge direction reversal.

    // ── Persistent settings ───────────────────────────────────────────────
    private const string SectionName   = "vMatch";
    private const string KeyDist       = "distance";
    private const string KeyRandStart  = "randStart";
    private const string KeyStartFrom = "startFrom";
    private const string KeyRandNext   = "randNext";

    private const double EdgeHoverRadiusPixels = 12.0; // Maximum edge hover distance in display pixels; greater than zero.
    private const double CursorReleaseRadiusPixels = 12.0; // Cursor travel in display pixels required before re-enabling edge snap after a match; zero or greater.
    private static readonly Color SourceEdgeHighlightColor = Color.Orange; // Hovered source-edge highlight color.
    private static readonly Color SourceDotHighlightColor = Color.Gold; // Chosen source-dot highlight color.
    private static readonly Color MateDotHighlightColor = Color.Magenta; // Matching destination-dot highlight color.
    private static readonly Color MatePartHighlightColor = Color.Cyan; // Matching destination-part highlight color.

    private static double _distance   = DefaultDistance;
    private static AutoStartMode _startFrom = DefaultStartFrom;
    private static bool   _randNext   = DefaultRandNext;

    private static readonly Random _rng = new Random();
    private enum AutoStartMode { Random,Pick }

    public override string EnglishName => "vMatch";

    // ── Dot record ─────────────────────────────────────────────────────────
    private sealed class Dot
    {
      public Guid    Id       { get; set; }
      public Point3d Position { get; set; }
      public string  MateId   { get; }
      public string  PartNum  { get; }
      public Dot(Guid id, Point3d pos, string mateId, string partNum)
      { Id = id; Position = pos; MateId = mateId; PartNum = partNum; }
    }

    private sealed class MateEdge
    {
      public int GroupIndex { get; }
      public Curve Curve { get; }
      public Point3d[] Samples { get; }
      public List<Dot> Dots { get; } = new List<Dot>();

      public MateEdge(int groupIndex, Curve curve)
      {
        GroupIndex = groupIndex;
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
      private readonly System.Drawing.Point? _releasePoint;
      private MateEdge? _activeEdge;
      private bool _waitingForCursorRelease;

      public MateEdgePicker(
        RhinoDoc doc,
        IReadOnlyList<Dot> dots,
        IReadOnlyList<MateEdge> edges,
        System.Drawing.Point? releasePoint)
      {
        _doc = doc;
        _dots = dots;
        _edges = edges;
        _releasePoint = releasePoint;
        _waitingForCursorRelease = releasePoint.HasValue;
        PermitObjectSnap(false);
        EnableObjectSnapCursors(false);
        PermitOrthoSnap(false);
        PermitTabMode(false);
      }

      public Dot? SourceDot { get; private set; }
      public Dot? MateDot { get; private set; }
      public System.Drawing.Point LastWindowPoint { get; private set; }
      public bool HasWindowPoint { get; private set; }

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
        LastWindowPoint = e.WindowPoint;
        HasWindowPoint = true;

        if (_waitingForCursorRelease && _releasePoint.HasValue)
        {
          double dx = e.WindowPoint.X - _releasePoint.Value.X;
          double dy = e.WindowPoint.Y - _releasePoint.Value.Y;
          if ((dx * dx) + (dy * dy) <= CursorReleaseRadiusPixels * CursorReleaseRadiusPixels)
          {
            ReleaseSnap();
            base.OnMouseMove(e);
            return;
          }

          _waitingForCursorRelease = false;
        }

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
          : FindClosestMate(_doc, _dots, SourceDot);

        if (MateDot == null)
          SourceDot = null;

        base.OnMouseMove(e);
      }

      protected override void OnDynamicDraw(GetPointDrawEventArgs e)
      {
        if (_activeEdge != null && SourceDot != null && MateDot != null)
        {
          DrawMateHighlight(_doc, e.Display, MateDot);
          PreviewDisplay.DrawCurve(e.Display, _activeEdge.Curve, SourceEdgeHighlightColor, 2);
          DrawDotHighlight(_doc, e.Display, SourceDot, SourceDotHighlightColor);
        }

        base.OnDynamicDraw(e);
      }
    }

    // ── Entry point ────────────────────────────────────────────────────────
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      LoadSettings();
      var initialAutoSelection = SelectedAutoPartIds(doc);

      var dots = ScanDots(doc);
      if (dots.Count == 0)
      {
        RhinoApp.WriteLine("vMatch: no edge mate dots found — run vUnrollSrf with EdgeDots=On first");
        return Result.Nothing;
      }

      var undoMoves = new Stack<MatchMove>();
      var redoMoves = new Stack<MatchMove>();
      var mateEdges = BuildMateEdges(doc, dots);
      using var overlapPreview = new MatchOverlapPreview(doc);
      using var autoMembersPreview = new PreviewDisplay.ObjectHighlighter(doc);
      using var middlePreview = new PreviewDisplay.ObjectHighlighter(doc,MiddlePartStyle);
      using var shortcutSession = new LocalUndoRedoShortcutSession(
        "vMatch",
        redo => new MatchHistoryRequest(redo));
      System.Drawing.Point? cursorReleasePoint = null;
      while (true)
      {
          using var gp = new MateEdgePicker(doc, dots, mateEdges, cursorReleasePoint);
          gp.EnableTransparentCommands(true);
          gp.SetCommandPrompt("Click a highlighted edge to match its part");
          int idxDist = gp.AddOption("Distance", $"{_distance:G}");
          int idxAuto = gp.AddOption("Auto");
          int idxOverlaps = gp.AddOption("Overlaps");
          int idxRedo = gp.AddOption("Redo", string.Empty, true);
          gp.AcceptNumber(true, true);
          gp.AcceptNothing(false);
          gp.AcceptUndo(true);
          gp.AcceptCustomMessage(true);

          var res = gp.Get();
          var src = gp.SourceDot;
          var mate = gp.MateDot;
          cursorReleasePoint = res == GetResult.Point && gp.HasWindowPoint
            ? gp.LastWindowPoint
            : null;
          gp.ReleaseSnap();
          if (res != GetResult.Point)
            doc.Views.Redraw();

          if (res == GetResult.CustomMessage &&
              gp.CustomMessage() is MatchHistoryRequest historyRequest)
          {
            if (ApplyMatchHistory(doc, dots, undoMoves, redoMoves, historyRequest.Redo, out int changedGroup))
            {
              RefreshMateEdges(doc, dots, mateEdges, changedGroup);
              overlapPreview.Refresh();
            }
            continue;
          }

          if (res == GetResult.Undo)
          {
            if (ApplyMatchHistory(doc, dots, undoMoves, redoMoves, false, out int changedGroup))
            {
              RefreshMateEdges(doc, dots, mateEdges, changedGroup);
              overlapPreview.Refresh();
            }
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
              if (ApplyMatchHistory(doc, dots, undoMoves, redoMoves, true, out int changedGroup))
              {
                RefreshMateEdges(doc, dots, mateEdges, changedGroup);
                overlapPreview.Refresh();
              }
              continue;
            }
            if (opt != null && opt.Index == idxAuto)
            {
              var autoSelection = SelectedAutoPartIds(doc);
              if(autoSelection.Count==0) autoSelection=new HashSet<Guid>(initialAutoSelection);
              initialAutoSelection.Clear();
              gp.Dispose();
              dots = AutoAlign(doc, dots, _distance, overlapPreview,middlePreview,autoMembersPreview,autoSelection);
              mateEdges = BuildMateEdges(doc, dots);
              undoMoves.Clear();
              redoMoves.Clear();
              continue;
            }
            if (opt != null && opt.Index == idxOverlaps)
            {
              overlapPreview.TrackGroups(dots.Select(dot => GrpOf(doc, dot.Id)));
              RhinoApp.WriteLine($"vMatch: {overlapPreview.OverlappingObjectCount} overlapping surfaces.");
              continue;
            }
            // Distance — sub-prompt (memory rule: no AddOptionDouble)
            using var gs = new GetString();
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
          var moveTimer = System.Diagnostics.Stopwatch.StartNew();

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

          vTools.Log.Write("vMatch",
            $"match id={src.MateId} source_part={src.PartNum} mate_part={mate.PartNum}" +
            $" src_tangent={srcTang.Value} mate_tangent={mateTang.Value}" +
            $" source_out={srcOut} distance={_distance:G}");

          var xf = PlaceXform(doc, srcTang.Value, srcOut, target,
                               mate.Position, mateTang.Value, mateObjs);
          if (!xf.HasValue || !xf.Value.TryGetInverse(out var inverse)) continue;

          var move = new MatchMove(mateObjs, xf.Value, inverse);
          if (!ApplyMatchMove(doc, dots, move, true)) continue;
          long transformMilliseconds = moveTimer.ElapsedMilliseconds;
          undoMoves.Push(move);
          initialAutoSelection.Clear();
          redoMoves.Clear();
          RefreshMateEdges(doc, dots, mateEdges, mateGrp);
          overlapPreview.TrackGroups(new[] { srcGrp, mateGrp });
          vTools.Log.Write("vMatch",
            $"move timing transform={transformMilliseconds}ms" +
            $" refresh={moveTimer.ElapsedMilliseconds - transformMilliseconds}ms" +
            $" total={moveTimer.ElapsedMilliseconds}ms objects={mateObjs.Count}");
      }

      return Result.Success;
    }

    private static bool ApplyMatchHistory(
      RhinoDoc doc,
      IReadOnlyList<Dot> dots,
      Stack<MatchMove> undoMoves,
      Stack<MatchMove> redoMoves,
      bool redo,
      out int changedGroup)
    {
      changedGroup = -1;
      var source = redo ? redoMoves : undoMoves;
      var destination = redo ? undoMoves : redoMoves;
      if (!source.TryPop(out var move))
        return false;

      if (!ApplyMatchMove(doc, dots, move, redo))
      {
        source.Push(move);
        return false;
      }

      destination.Push(move);
      if (move.ObjectIds.Count > 0)
        changedGroup = GrpOf(doc, move.ObjectIds[0]);
      return true;
    }

    private static bool ApplyMatchMove(
      RhinoDoc doc,
      IReadOnlyList<Dot> dots,
      MatchMove move,
      bool forward)
    {
      var transform = forward ? move.Forward : move.Reverse;
      List<Guid> transformedIds;

      doc.Views.RedrawEnabled = false;
      try
      {
        transformedIds = TransformObjectsAndDots(doc, dots, move.ObjectIds, transform);
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

    private static List<Guid> TransformObjectsAndDots(
      RhinoDoc doc,
      IReadOnlyList<Dot> dots,
      IEnumerable<Guid> objectIds,
      Transform transform)
    {
      var ids = objectIds.ToList();
      var transformedIds = new List<Guid>(ids.Count);
      var replacements = new Dictionary<Guid, Guid>(ids.Count);
      foreach (var id in ids)
      {
        var transformedId = doc.Objects.Transform(id, transform, true);
        if (transformedId == Guid.Empty)
          continue;

        transformedIds.Add(transformedId);
        replacements[id] = transformedId;
      }

      foreach (var dot in dots)
      {
        if (!replacements.TryGetValue(dot.Id, out var transformedId))
          continue;

        var position = dot.Position;
        position.Transform(transform);
        dot.Id = transformedId;
        dot.Position = position;
      }

      return transformedIds;
    }

    // ── Auto sub-mode — inner loop with persistent multi-selection ─────────
    private static List<Dot> AutoAlign(RhinoDoc doc, List<Dot> allDots, double distance, MatchOverlapPreview overlapPreview,
      PreviewDisplay.ObjectHighlighter middlePreview,PreviewDisplay.ObjectHighlighter autoMembersPreview,IReadOnlyCollection<Guid> preselection)
    {
      var brepsFilt = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion;
      middlePreview.SetObjects(Array.Empty<Guid>());
      autoMembersPreview.SetObjects(Array.Empty<Guid>());

      while (true)
      {
        var snapGroups=preselection.Select(id=>GrpOf(doc,id)).Where(group=>group>=0).ToHashSet();
        preselection=Array.Empty<Guid>();
        doc.Objects.UnselectAll();
        autoMembersPreview.SetObjects(snapGroups.SelectMany(group=>ObjsInGrp(doc,group)));

        // Pass 2: interactive — add / remove parts, toggle options
        var optRn  = new OptionToggle(_randNext,  "Off", "On");

        using var go = new GetObject();
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
        int idxStart = go.AddOptionList("StartFrom",StartFromNames,(int)_startFrom);
        go.AddOptionToggle("RandNext",  ref optRn);
        go.AcceptNumber(true, true);

        bool goBack = false;
        while (true)
        {
          var ires = go.GetMultiple(0, 0);
          var oldStart = _startFrom;
          if(ires==GetResult.Option&&go.Option()?.Index==idxStart) _startFrom=(AutoStartMode)go.Option()!.CurrentListOptionIndex;
          bool rnChanged = optRn.CurrentValue != _randNext;
          _randNext  = optRn.CurrentValue;
          if (oldStart!=_startFrom || rnChanged) SaveSettings();

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
              using var gs = new GetString();
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

        var clickedGroups=Enumerable.Range(0,go.ObjectCount).Select(index=>GrpOf(doc,go.Object(index).ObjectId)).Where(group=>group>=0);
        var selGrpList=CombineAutoGroups(snapGroups,clickedGroups).ToList();
        if (selGrpList.Count == 0) continue;

        var selGrpSet = new HashSet<int>(selGrpList);
        go.Dispose();
        distance=_distance;
        int rootGrp = _startFrom==AutoStartMode.Pick?PickAutoRoot(doc,selGrpSet,brepsFilt):selGrpList[_rng.Next(selGrpList.Count)];
        if(rootGrp<0) return ScanDots(doc);
        if(_startFrom==AutoStartMode.Pick)
        {
          middlePreview.SetObjects(ObjsInGrp(doc,rootGrp));
          RhinoApp.WriteLine("vMatch: matching outward from the magenta middle part.");
          RhinoApp.Wait();
        }

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
        void RefreshHighlights(IEnumerable<int> groups) => autoMembersPreview.SetObjects(
          groups.Where(group=>_startFrom!=AutoStartMode.Pick||group!=rootGrp).SelectMany(group=>ObjsInGrp(doc,group)));
        doc.Objects.UnselectAll();
        RefreshHighlights(selGrpSet);
        var displayTimer=System.Diagnostics.Stopwatch.StartNew();
        int displayRefreshes=0;
        long displayMilliseconds=0;
        Action placementUpdated=()=>
        {
          if(!ShouldRefreshAutoDisplay(displayTimer.ElapsedMilliseconds)) return;
          var started=System.Diagnostics.Stopwatch.StartNew();
          bool redrawBefore=doc.Views.RedrawEnabled;
          try { doc.Views.EnableRedraw(false,false,false); RefreshHighlights(selGrpSet); }
          finally { doc.Views.EnableRedraw(redrawBefore,false,false); }
          doc.Views.ActiveView?.Redraw(); RhinoApp.Wait();
          displayMilliseconds+=started.ElapsedMilliseconds; displayRefreshes++; displayTimer.Restart();
        };
        if(_startFrom==AutoStartMode.Pick)
          AssembleFromMiddle(doc,allDots,mateMap,selGrpSet,rootGrp,distance,placed,placementUpdated);
        else
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
              var mateInfo = FindClosestMate(doc, mList, src,
                group => selGrpSet.Contains(group) && !placed.Contains(group));
              if (mateInfo == null) continue;

              int mateGrp = GrpOf(doc, mateInfo.Id);
              if (!selGrpSet.Contains(mateGrp) || placed.Contains(mateGrp)) continue;

              if(!TryAutoPlace(doc,allDots,src,mateInfo,currGrp,mateGrp,distance,placementUpdated)) continue;

              placed.Add(mateGrp);
              queue.Add(mateGrp);
            }
          }
        }

        overlapPreview.TrackGroups(placed);
        allDots = ScanDots(doc);

        RefreshHighlights(placed);
        Log.Write("vMatch","auto display refreshes={0} elapsed_ms={1} placed={2}",displayRefreshes,displayMilliseconds,placed.Count);
        return allDots;
      }
    }

    // ── Geometry helpers ───────────────────────────────────────────────────

    private static Dot? FindClosestMate(
      RhinoDoc doc, IEnumerable<Dot> dots, Dot source, Func<int, bool>? eligibleGroup = null)
    {
      int sourceGroup = GrpOf(doc, source.Id);
      return dots.Where(dot => dot.Id != source.Id &&
          string.Equals(dot.MateId, source.MateId, StringComparison.Ordinal))
        .Where(dot =>
        {
          int group = GrpOf(doc, dot.Id);
          return group >= 0 && group != sourceGroup && (eligibleGroup == null || eligibleGroup(group));
        })
        .OrderBy(dot => source.Position.DistanceToSquared(dot.Position))
        .ThenBy(dot => dot.Id)
        .FirstOrDefault();
    }

    private static List<Dot> ScanDots(RhinoDoc doc)
    {
      var result = new List<Dot>();
      var settings = new ObjectEnumeratorSettings
      {
        ObjectTypeFilter = ObjectType.TextDot,
        NormalObjects = true,
        LockedObjects = true,
        HiddenObjects = true,
        DeletedObjects = false
      };
      foreach (var obj in doc.Objects.GetObjectList(settings))
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

    private static List<MateEdge> BuildMateEdges(
      RhinoDoc doc,
      IReadOnlyList<Dot> dots,
      ISet<int>? groupFilter = null)
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
        if (group.Key < 0 || (groupFilter != null && !groupFilter.Contains(group.Key)))
          continue;

        var edges = NakedEdges(doc, ObjsInGrp(doc, group.Key))
          .Select(curve => new MateEdge(group.Key, curve))
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

    private static void RefreshMateEdges(
      RhinoDoc doc,
      IReadOnlyList<Dot> dots,
      List<MateEdge> mateEdges,
      int groupIndex)
    {
      if (groupIndex < 0)
        return;

      mateEdges.RemoveAll(edge => edge.GroupIndex == groupIndex);
      mateEdges.AddRange(BuildMateEdges(doc, dots, new HashSet<int> { groupIndex }));
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
              PreviewDisplay.DrawBrepWires(display, brep, MatePartHighlightColor, 1);
              break;
            case Extrusion extrusion:
              var extrusionBrep = extrusion.ToBrep();
              if (extrusionBrep != null)
              {
                display.DrawBrepShaded(extrusionBrep, material);
                PreviewDisplay.DrawBrepWires(display, extrusionBrep, MatePartHighlightColor, 1);
              }
              break;
            case Surface surface:
              var surfaceBrep = surface.ToBrep();
              if (surfaceBrep != null)
              {
                display.DrawBrepShaded(surfaceBrep, material);
                PreviewDisplay.DrawBrepWires(display, surfaceBrep, MatePartHighlightColor, 1);
              }
              break;
            case Mesh mesh:
              display.DrawMeshShaded(mesh, material);
              PreviewDisplay.DrawMeshWires(display, mesh, MatePartHighlightColor, 1);
              break;
            case Curve curve:
              PreviewDisplay.DrawCurve(display, curve, MatePartHighlightColor, 1);
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
      return (doc.Groups.GroupMembers(grpIdx) ?? Array.Empty<RhinoObject>())
        .Select(obj => obj.Id)
        .ToList();
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

      double tol = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      var sourceIds = srcIds.ToList();
      var box = BoundingBox.Empty;
      bool hasBox = false;
      foreach (var id in sourceIds)
      {
        var obj = doc.Objects.FindId(id);
        var objectBox = obj?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (objectBox.IsValid)
        {
          if (hasBox) box.Union(objectBox);
          else { box = objectBox; hasBox = true; }
        }
      }

      double scale = hasBox ? Math.Max(box.Diagonal.Length, tol * 100.0) : tol * 100.0;
      double start = Math.Max(tol * 2.0, scale * 1.0e-7);
      double limit = Math.Max(start, Math.Min(scale * 0.02, tol * 100.0));
      for (double eps = start; eps <= limit * 1.001; eps *= 2.0)
      {
        var testA = new Point3d(dotPt.X + pa.X * eps, dotPt.Y + pa.Y * eps, 0.0);
        var testB = new Point3d(dotPt.X + pb.X * eps, dotPt.Y + pb.Y * eps, 0.0);
        bool aIn = false;
        bool bIn = false;
        foreach (var id in sourceIds)
        {
          var obj = doc.Objects.FindId(id);
          Brep? brep = null;
          if      (obj?.Geometry is Brep    b) brep = b;
          else if (obj?.Geometry is Extrusion e) brep = e.ToBrep();
          else if (obj?.Geometry is Surface  s) brep = s.ToBrep();
          if (brep == null) continue;

          foreach (var face in brep.Faces)
          {
            TestFacePoint(face, testA, tol, ref aIn);
            TestFacePoint(face, testB, tol, ref bIn);
          }
        }
        if (aIn != bIn)
        {
          var outward = aIn ? pb : pa;
          vTools.Log.Write("vMatch",
            $"outward local point={dotPt} epsilon={eps:G6}" +
            $" a_inside={aIn} b_inside={bIn} result={outward}");
          return outward;
        }
      }

      // Fallback: centroid direction
      var centroid = AreaCentroid2d(doc, sourceIds);
      if (!centroid.HasValue) return pa;
      var diff = dotPt - centroid.Value;
      var fallback = (-ty * diff.X + tx * diff.Y) >= 0.0 ? pa : pb;
      vTools.Log.Write("vMatch",
        $"outward centroid point={dotPt} centroid={centroid.Value} result={fallback}");
      return fallback;
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
    /// src_tang (antiparallel first, then parallel). Picks the rotation whose
    /// moved-part exterior points back toward the source, placing the moved
    /// part's interior on the source edge's outward side.
    /// </summary>
    private static Transform? PlaceXform(
        RhinoDoc doc,
        Vector3d srcTang, Vector3d srcOut, Point3d target,
        Point3d mateDot, Vector3d mateTang, List<Guid> mateIds)
    {
      Transform? best = null;
      double bestSideScore = double.MinValue;
      double bestCentroidScore = double.MinValue;
      var mateOut = Outward2d(doc, mateDot, mateTang, mateIds);
      var mateCentroid = AreaCentroid2d(doc, mateIds);
      int candidateIndex = 0;

      foreach (var (fx, fy) in new[] { (-srcTang.X, -srcTang.Y), (srcTang.X, srcTang.Y) })
      {
        double mx = mateTang.X, my = mateTang.Y;
        double angle = Math.Atan2(mx * fy - my * fx, mx * fx + my * fy);
        var xf = Transform.Translation(target - mateDot)
               * Transform.Rotation(angle, Vector3d.ZAxis, mateDot);

        var transformedOut = mateOut;
        transformedOut.Transform(xf);
        transformedOut.Z = 0.0;
        if (!transformedOut.Unitize())
          transformedOut = mateOut;
        double sideScore = -(transformedOut.X * srcOut.X + transformedOut.Y * srcOut.Y);

        double centroidScore = 0.0;
        if (mateCentroid.HasValue)
        {
          var transformedCentroid = mateCentroid.Value;
          transformedCentroid.Transform(xf);
          var centroidOffset = transformedCentroid - target;
          centroidScore = centroidOffset.X * srcOut.X + centroidOffset.Y * srcOut.Y;
        }

        vTools.Log.Write("vMatch",
          $"candidate={candidateIndex} angle_deg={RhinoMath.ToDegrees(angle):G6}" +
          $" moved_out={transformedOut} side_score={sideScore:G6}" +
          $" centroid_score={centroidScore:G6}");

        if (sideScore > bestSideScore + 1.0e-9 ||
            Math.Abs(sideScore - bestSideScore) <= 1.0e-9 && centroidScore > bestCentroidScore)
        {
          bestSideScore = sideScore;
          bestCentroidScore = centroidScore;
          best = xf;
        }
        candidateIndex++;
      }

      vTools.Log.Write("vMatch",
        $"orientation chosen side_score={bestSideScore:G6}" +
        $" centroid_score={bestCentroidScore:G6} mate_out={mateOut}");
      return best;
    }

    // ── Settings ───────────────────────────────────────────────────────────

    private static void LoadSettings()
    {
      ToolsOptionStore.Read<int>(SectionName, s =>
      {
        if (ToolsOptionStore.TryGetDouble(s, KeyDist, out var d) && d >= 0.0)
          _distance = d;

        if(ToolsOptionStore.TryGetString(s,KeyStartFrom,out var start)&&Enum.TryParse(start,true,out AutoStartMode parsed)&&Enum.IsDefined(parsed))
          _startFrom=parsed;
        else if (ToolsOptionStore.TryGetBool(s, KeyRandStart, out var rs))
          _startFrom = rs?AutoStartMode.Random:AutoStartMode.Pick;
        else if (ToolsOptionStore.TryGetDouble(s, KeyRandStart, out var oldRs))
          _startFrom = oldRs>0.5?AutoStartMode.Random:AutoStartMode.Pick;
        else _startFrom=DefaultStartFrom;

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
        s[KeyStartFrom] = _startFrom.ToString();
        s[KeyRandNext]  = _randNext;
      });
      if (!saved)
        RhinoApp.WriteLine($"vMatch: failed to save options: {ToolsOptionStore.LastError}");
    }
  }
}

namespace vTools.Commands
{
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using Rhino.Input;
  using Rhino.Input.Custom;


  public sealed partial class vMatch
  {
    private static HashSet<Guid> SelectedAutoPartIds(RhinoDoc doc) => doc.Objects.GetSelectedObjects(false,false)
      .Where(obj=>obj.Geometry is Brep or Surface or Extrusion).Select(obj=>obj.Id).ToHashSet();

    private static HashSet<int> CombineAutoGroups(IEnumerable<int> initial,IEnumerable<int> clicked)
    {
      var result=initial.ToHashSet(); result.SymmetricExceptWith(clicked.Distinct()); return result;
    }

    private static bool ShouldRefreshAutoDisplay(long elapsedMilliseconds) => elapsedMilliseconds>=AutoLiveRefreshMilliseconds;

    private sealed record AutoFrontier(Dot Source,int Group,int Side,int Depth);

    private static int PickAutoRoot(RhinoDoc doc,HashSet<int> groups,ObjectType filter)
    {
      var selection=doc.Objects.GetSelectedObjects(false,false).Select(obj=>obj.Id).ToArray();
      try
      {
        doc.Objects.UnselectAll();
        using var members=new PreviewDisplay.ObjectHighlighter(doc);
        members.SetObjects(groups.SelectMany(group=>ObjsInGrp(doc,group)));
        using var getter=new GetObject(); getter.SetCommandPrompt("Pick the middle part to keep fixed");
        getter.EnableHighlight(false);
        getter.EnablePressEnterWhenDonePrompt(false);
        getter.GeometryFilter=filter; getter.GroupSelect=false; getter.SubObjectSelect=false;
        getter.EnablePreSelect(false,true); getter.AlreadySelectedObjectSelect=true;
        getter.EnableClearObjectsOnEntry(false); getter.EnableUnselectObjectsOnExit(false); getter.DeselectAllBeforePostSelect=false;
        getter.SetCustomGeometryFilter((obj,_,_)=>groups.Contains(GrpOf(doc,obj.Id)));
        if(getter.Get()==GetResult.Object)
        {
          int group=GrpOf(doc,getter.Object(0).ObjectId);
          selection=Array.Empty<Guid>();
          return group;
        }
        return -1;
      }
      finally { doc.Objects.UnselectAll(); foreach(var id in selection) doc.Objects.FindId(id)?.Select(true); }
    }

    private static void AssembleFromMiddle(RhinoDoc doc,List<Dot> dots,Dictionary<string,List<Dot>> mates,HashSet<int> groups,
      int root,double distance,HashSet<int> placed,Action? placementUpdated=null)
    {
      var rootDots=dots.Where(dot=>GrpOf(doc,dot.Id)==root).ToArray();
      var bounds=BoundingBox.Empty;
      if(rootDots.Length>1) foreach(var dot in rootDots) bounds.Union(dot.Position);
      else foreach(var id in ObjsInGrp(doc,root)) if(doc.Objects.FindId(id) is {} obj) bounds.Union(obj.Geometry.GetBoundingBox(true));
      var center=bounds.IsValid?bounds.Center:rootDots.Select(dot=>dot.Position).FirstOrDefault();
      var axis=CenteredStartAxis(rootDots.Select(dot=>dot.Position).ToArray());
      var frontier=new List<AutoFrontier>();
      void Add(int group,int side,int depth)
      {
        foreach(var dot in dots.Where(dot=>GrpOf(doc,dot.Id)==group))
        {
          int branch=side==0?((dot.Position-center)*axis<0?-1:1):side;
          frontier.Add(new(dot,group,branch,depth));
        }
      }
      Add(root,0,1); int desired=DefaultCenteredSide;
      Log.Write("vMatch","auto center group={0} axis={1}",root,axis);
      while(frontier.Count>0)
      {
        int index=NextCenteredFrontier(frontier.Select(item=>item.Side).ToArray(),frontier.Select(item=>item.Depth).ToArray(),desired,false);
        var item=frontier[index]; frontier.RemoveAt(index);
        if(!mates.TryGetValue(item.Source.MateId,out var candidates)) continue;
        var mate=FindClosestMate(doc,candidates,item.Source,group=>groups.Contains(group)&&!placed.Contains(group));
        if(mate==null) continue;
        int target=GrpOf(doc,mate.Id);
        if(!TryAutoPlace(doc,dots,item.Source,mate,item.Group,target,distance,placementUpdated)) continue;
        placed.Add(target); Add(target,item.Side,item.Depth+1); desired=-item.Side;
        Log.Write("vMatch","auto middle step group={0} side={1} depth={2}",target,item.Side,item.Depth);
      }
    }

    private static Vector3d CenteredStartAxis(IReadOnlyList<Point3d> points)
    {
      double longest=0,x=1,y=0;
      for(int first=0;first<points.Count;first++) for(int second=first+1;second<points.Count;second++)
      {
        double dx=points[second].X-points[first].X,dy=points[second].Y-points[first].Y,length=dx*dx+dy*dy;
        if(length>longest) { longest=length; x=dx; y=dy; }
      }
      double magnitude=Math.Sqrt(x*x+y*y);
      if(magnitude<=CenteredAxisTolerance) return Vector3d.XAxis;
      if(x<0||Math.Abs(x)<=CenteredAxisTolerance&&y<0) { x=-x; y=-y; }
      return new(x/magnitude,y/magnitude,0);
    }

    private static int NextCenteredFrontier(IReadOnlyList<int> sides,IReadOnlyList<int> depths,int desired,bool random)
    {
      int depth=depths.Min();
      var layer=Enumerable.Range(0,sides.Count).Where(index=>depths[index]==depth).ToArray();
      var preferred=layer.Where(index=>sides[index]==desired).ToArray();
      var available=preferred.Length>0?preferred:layer;
      return available[random?_rng.Next(available.Length):0];
    }

    private static bool TryAutoPlace(RhinoDoc doc,IReadOnlyList<Dot> dots,Dot source,Dot mate,int sourceGroup,int mateGroup,double distance,
      Action? placementUpdated=null)
    {
      var placementTimer=System.Diagnostics.Stopwatch.StartNew();
      var sourceObjects=ObjsInGrp(doc,sourceGroup); var mateObjects=ObjsInGrp(doc,mateGroup);
      if(sourceObjects.Count==0||mateObjects.Count==0) return false;
      var sourceEdges=NakedEdges(doc,sourceObjects); var mateEdges=NakedEdges(doc,mateObjects);
      Vector3d? sourceTangent,mateTangent;
      try { sourceTangent=Tang2d(source.Position,sourceEdges); mateTangent=Tang2d(mate.Position,mateEdges); }
      finally { foreach(var edge in sourceEdges) edge.Dispose(); foreach(var edge in mateEdges) edge.Dispose(); }
      if(sourceTangent==null||mateTangent==null) return false;
      var outward=Outward2d(doc,source.Position,sourceTangent.Value,sourceObjects);
      var target=new Point3d(source.Position.X+outward.X*distance,source.Position.Y+outward.Y*distance,0);
      var transform=PlaceXform(doc,sourceTangent.Value,outward,target,mate.Position,mateTangent.Value,mateObjects);
      if(!transform.HasValue) return false;
      bool redrawBefore=doc.Views.RedrawEnabled;
      List<Guid> moved;
      try { doc.Views.EnableRedraw(false,false,false); moved=TransformObjectsAndDots(doc,dots,mateObjects,transform.Value); }
      finally { doc.Views.EnableRedraw(redrawBefore,false,false); }
      if(moved.Count==0) return false;
      if(placementUpdated!=null) placementUpdated();
      else { doc.Views.Redraw(); RhinoApp.Wait(); }
      Log.Write("vMatch","auto match id={0} source_part={1} mate_part={2} source_group={3} mate_group={4} source_out={5} elapsed_ms={6}",source.MateId,source.PartNum,mate.PartNum,sourceGroup,mateGroup,outward,placementTimer.ElapsedMilliseconds);
      return true;
    }
  }
}

namespace vTools.Commands
{
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;
  using System.Drawing;


  internal sealed class MatchOverlapPreview : IDisposable
  {
    // Defaults and customizable detection tolerances
    private const double FaceCoincidenceToleranceFactor = 5.0; // Document absolute-tolerance multiplier for comparing face planes; one or greater.
    private static readonly PreviewDisplay.ObjectHighlightStyle OverlapHighlightStyle = new( // Overlap-only palette; RGB colors and shading transparency distinguish it from the cyan matching preview.
      BodyColor: Color.FromArgb(255, 128, 0), // RGB orange face/wire overlay color.
      OutlineColor: Color.FromArgb(90, 45, 0), // RGB dark-orange outline around overlapping objects.
      DotBackground: Color.FromArgb(90, 45, 0), // RGB background for highlighted dots, if present.
      Transparency: 0.55); // Shaded transparency from 0.0 opaque through 1.0 invisible.

    private readonly RhinoDoc _doc;
    private readonly HashSet<int> _groups = [];
    private readonly Dictionary<Guid, ObjectState> _objects = [];
    private readonly HashSet<Guid> _changedObjects = [];
    private readonly HashSet<FaceOverlapFinder.FacePair> _overlappingPairs = [];
    private readonly PreviewDisplay.ObjectHighlighter _highlighter;
    private double _tolerance;
    private bool _dirty;
    private bool _disposed;

    private readonly record struct ObjectState(int Group, uint SerialNumber);

    internal int OverlappingObjectCount => OverlappingObjectIds(_overlappingPairs).Count;

    internal MatchOverlapPreview(RhinoDoc doc)
    {
      _doc = doc;
      _highlighter = new PreviewDisplay.ObjectHighlighter(doc, OverlapHighlightStyle);
      RhinoDoc.AddRhinoObject += OnObjectChanged;
      RhinoDoc.DeleteRhinoObject += OnObjectChanged;
      RhinoDoc.UndeleteRhinoObject += OnObjectChanged;
      RhinoDoc.ReplaceRhinoObject += OnObjectReplaced;
      RhinoDoc.ModifyObjectAttributes += OnAttributesChanged;
      RhinoApp.Idle += OnIdle;
    }

    internal void TrackGroups(IEnumerable<int> groups)
    {
      _groups.UnionWith(groups.Where(index => index >= 0));
      Refresh();
    }

    internal void Refresh()
    {
      if (_disposed) return;
      _dirty = false;
      var timer = System.Diagnostics.Stopwatch.StartNew();
      try
      {
        var current = SurfaceObjects(_doc, _groups);
        foreach (var (id, entry) in current)
        {
          var state = new ObjectState(entry.Group, entry.Object.RuntimeSerialNumber);
          if (!_objects.TryGetValue(id, out var previous) || previous != state)
            _changedObjects.Add(id);
        }
        foreach (var id in _objects.Keys)
          if (!current.ContainsKey(id)) _changedObjects.Add(id);

        double tolerance = Math.Max(_doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
        if (tolerance != _tolerance) _changedObjects.UnionWith(current.Keys);
        if (_changedObjects.Count == 0) return;

        // Keep stationary pairs; only a changed endpoint can change their overlap.
        var result = FindPairs(current, tolerance, _changedObjects);
        _overlappingPairs.RemoveWhere(pair =>
          _changedObjects.Contains(pair.First.ObjectId) || _changedObjects.Contains(pair.Second.ObjectId));
        _overlappingPairs.UnionWith(result.OverlappingPairs);
        var overlaps = OverlappingObjectIds(_overlappingPairs);
        _highlighter.SetObjects(overlaps);
        _objects.Clear();
        foreach (var (id, entry) in current)
          _objects.Add(id, new ObjectState(entry.Group, entry.Object.RuntimeSerialNumber));
        _tolerance = tolerance;
        Log.Write("vMatch", $"overlap groups={_groups.Count} changed={_changedObjects.Count} " +
          $"faces={result.FaceCount} checks={result.PairChecks} surfaces={overlaps.Count} elapsed={timer.ElapsedMilliseconds}ms");
        _changedObjects.Clear();
      }
      catch (Exception ex)
      {
        _highlighter.SetObjects(Array.Empty<Guid>());
        _objects.Clear();
        _overlappingPairs.Clear();
        Log.Write("vMatch", $"overlap detection failed: {ex.Message}");
      }
    }

    internal static HashSet<Guid> FindOverlappingObjects(RhinoDoc doc, IReadOnlyCollection<int> groups)
    {
      double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      return OverlappingObjectIds(FindPairs(SurfaceObjects(doc, groups), tolerance).OverlappingPairs);
    }

    private static Dictionary<Guid, (RhinoObject Object, int Group)> SurfaceObjects(
      RhinoDoc doc, IReadOnlyCollection<int> groups)
    {
      var objects = new Dictionary<Guid, (RhinoObject, int)>();
      foreach (int group in groups)
        foreach (var obj in doc.Groups.GroupMembers(group) ?? Array.Empty<RhinoObject>())
          if (!obj.IsDeleted && !obj.IsHidden && obj.IsValid &&
              obj.Geometry is Brep or Extrusion or Surface)
            objects.TryAdd(obj.Id, (obj, group));
      return objects;
    }

    private static HashSet<Guid> OverlappingObjectIds(IEnumerable<FaceOverlapFinder.FacePair> pairs) =>
      pairs.SelectMany(pair => new[] { pair.First.ObjectId, pair.Second.ObjectId }).ToHashSet();

    private static FaceOverlapFinder.Result FindPairs(
      Dictionary<Guid, (RhinoObject Object, int Group)> objects,
      double tolerance,
      ISet<Guid>? changedObjects = null)
    {
      var faces = new List<FaceOverlapFinder.FaceItem>();
      var ownedBreps = new List<Brep>();
      try
      {
        foreach (var (id, entry) in objects)
        {
          Brep? brep = entry.Object.Geometry as Brep;
          if (brep == null)
          {
            brep = entry.Object.Geometry switch
            {
              Extrusion extrusion => extrusion.ToBrep(),
              Surface surface => surface.ToBrep(),
              _ => null
            };
            if (brep == null) continue;
            ownedBreps.Add(brep);
          }
          foreach (var face in brep.Faces)
            faces.Add(new FaceOverlapFinder.FaceItem(new(id, face.FaceIndex), face));
        }

        return FaceOverlapFinder.Find(faces, tolerance * FaceCoincidenceToleranceFactor, tolerance,
          (first, second) => objects[first.ObjectId].Group != objects[second.ObjectId].Group &&
            (changedObjects == null || changedObjects.Contains(first.ObjectId) || changedObjects.Contains(second.ObjectId)));
      }
      finally
      {
        foreach (var brep in ownedBreps) brep.Dispose();
      }
    }

    private bool IsTracked(RhinoObject? obj) =>
      obj?.Document?.RuntimeSerialNumber == _doc.RuntimeSerialNumber &&
      (obj.Attributes.GetGroupList() ?? Array.Empty<int>()).Any(_groups.Contains);

    private void OnObjectChanged(object? sender, RhinoObjectEventArgs e)
    {
      if (IsTracked(e.TheObject))
      {
        _changedObjects.Add(e.TheObject.Id);
        _dirty = true;
      }
    }

    private void OnObjectReplaced(object? sender, RhinoReplaceObjectEventArgs e)
    {
      if (IsTracked(e.OldRhinoObject) || IsTracked(e.NewRhinoObject))
      {
        if (e.OldRhinoObject != null) _changedObjects.Add(e.OldRhinoObject.Id);
        if (e.NewRhinoObject != null) _changedObjects.Add(e.NewRhinoObject.Id);
        _dirty = true;
      }
    }

    private void OnAttributesChanged(object? sender, RhinoModifyObjectAttributesEventArgs e)
    {
      if (e.Document.RuntimeSerialNumber == _doc.RuntimeSerialNumber &&
          ((e.OldAttributes.GetGroupList() ?? Array.Empty<int>()).Any(_groups.Contains) || IsTracked(e.RhinoObject)))
      {
        _changedObjects.Add(e.RhinoObject.Id);
        _dirty = true;
      }
    }

    private void OnIdle(object? sender, EventArgs e)
    {
      if (_dirty) Refresh();
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      RhinoDoc.AddRhinoObject -= OnObjectChanged;
      RhinoDoc.DeleteRhinoObject -= OnObjectChanged;
      RhinoDoc.UndeleteRhinoObject -= OnObjectChanged;
      RhinoDoc.ReplaceRhinoObject -= OnObjectReplaced;
      RhinoDoc.ModifyObjectAttributes -= OnAttributesChanged;
      RhinoApp.Idle -= OnIdle;
      _groups.Clear();
      _objects.Clear();
      _changedObjects.Clear();
      _overlappingPairs.Clear();
      _highlighter.Dispose();
    }
  }
}
