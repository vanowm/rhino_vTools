using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

// Native RhinoCommon C# conversion of MultiUnroll_preselect_other_v26_compact_rewrite.py
// Drop this file into an existing Rhino plug-in project and change the namespace if needed.
// Command name: vUnrollSrf
// Modified 2026.06.29: nullable-warning cleanup and Rhino 8 TextEntity API update.
// Modified 2026.06.29: fixed layout advance so unrolled parts move along row X baseline instead of diagonal top-right drift.
// Modified 2026.06.29: restores the exact pre-command selection after finish/cancel, without changing the working object-add path.
// Modified 2026.06.30 13:04:31: label up helper now stays on the same Brep face using local UV tangent stepping, avoiding upside-down flat labels from ClosestPoint jumping to another face/edge.
// Modified 2026.06.30 17:08:45: hidden same-face orientation curves are unrolled for the label frame, reducing 180-degree flat-text direction ambiguity from standalone helper points.
// Modified 2026.06.30 17:17:28: unrolled flat text keeps the raw unrolled up direction but forces the text plane normal to World +Z, preventing mirrored text while preserving orientation-marker direction.
// Modified 2026.07.01: per-part label height, edge mate dots, and flat label boundary fallback.

namespace vTools.Commands
{
  public class vUnrollSrf : Command
  {
    public override string EnglishName => "vUnrollSrf";

    private const string TextLayerName = "Reference";
    private const string TextObjectName = "MultiUnroll_NumberLabel";
    private const string FailureMarkerName = "MultiUnroll_FailedMarker";
    private const string LabelNumberKey = "MultiUnrollLabelNumber";
    private const string FailedUnrollKey = "MultiUnrollFailed";
    private const string FlatGroupPrefix = "MultiUnroll_Flat";
    private const string OriginalGroupPrefix = "MultiUnroll_Original";
    private const string FailureMarkerText = "X";
    private const string LabelHelperDotPrefix     = "__vTools_vUnrollSrf_LabelHelper__";
    private const string EdgeMateHelperDotPrefix  = "__vTools_vUnrollSrf_EdgeHelper__";

    private const string TextFont = "Arial";
    private const double TextHeightScale = 1.5;
    private const double TextLiftRatio = 0.001;
    private const double TextUpStepRatio = 2.5;
    private const int TextBoundarySamples = 7;
    private const bool TextMarkSixNine = true;

    private const double FollowingTolFactor = 100.0;
    private const double FollowingDiagFactor = 1.0e-4;
    private const int FollowingCurveSamples = 9;

    private enum LabelMode
    {
      Text = 0,
      Dots = 1,
      None = 2
    }

    private static readonly string[] LabelModeNames = { "Text", "Dots", "None" };

    // Session-sticky settings. If you want persistence across Rhino restarts, move these into your plug-in settings.
    private static LabelMode _labelMode = LabelMode.Text;
    private static bool _rotateFlatParts = true;
    private static bool _explode = false;
    private static bool _keepPropSurface  = false;
    private static bool _keepPropFollowing = true;
    private static double _layoutSpacing = 1.0;
    private static double _xExtents = 0.0;
    private static bool   _edgeDots = true;
    private static bool   _splitFaces = false;

    // Edge-mate dot constants (match MultiUnroll2.py / vMatch.cs)
    private const string EdgeMateName        = vMatch.EdgeMateName;
    private const string EdgeMateIdKey       = vMatch.EdgeMateIdKey;
    private const string EdgePartNumKey      = vMatch.EdgePartNumKey;
    private const string EdgeMatePartNumKey  = vMatch.EdgeMatePartNumKey;
    private const string EdgeMateReversedKey = vMatch.EdgeMateReversedKey;
    private const string EdgeIndexKey        = "MultiUnrollEdgeIndex";
    private const string MateEdgeIndexKey    = "MultiUnrollMateEdgeIndex";
    private const string EdgeMatePrefix      = "M";
    private const int    EdgeMateDotSize     = 10;
    private const double EdgeMateTolFactor   = 25.0;
    private const double EdgeMateDiagFactor  = 1.0e-4;
    private const int    EdgeMateSamples     = 7;

    // ── Debug logging ─────────────────────────────────────────────────────
    private static void Dbg(string msg) => vTools.Log.Write("vUnrollSrf", msg);

    private static string P(Point3d? p)  => p.HasValue  ? $"({p.Value.X:G6}, {p.Value.Y:G6}, {p.Value.Z:G6})" : "None";
    private static string P(Point3d p)   => $"({p.X:G6}, {p.Y:G6}, {p.Z:G6})";
    private static string V(Vector3d? v) => v.HasValue  ? $"({v.Value.X:G6}, {v.Value.Y:G6}, {v.Value.Z:G6})" : "None";
    private static string V(Vector3d v)  => $"({v.X:G6}, {v.Y:G6}, {v.Z:G6})";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      Dbg($"run start model_tol={doc.ModelAbsoluteTolerance:G} doc={doc.Path}");
      var startIds = SelectedIds(doc);
      var surfaceIds = GetSurfaceIds(doc, startIds.Where(IsSurfaceLikeId).ToList());
      if (surfaceIds == null || surfaceIds.Count == 0)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      var followingIds = GetFollowingIds(doc, startIds.Where(IsFollowingLikeId).ToList(), surfaceIds);
      if (followingIds == null)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      var options = GetLayoutOptions(doc, "Start point for unrolls - press Enter for world 0");
      if (options == null)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      _labelMode = options.LabelMode;
      _rotateFlatParts = options.RotateFlatParts;
      _explode = options.Explode;
      _keepPropSurface  = options.KeepPropSurface;
      _keepPropFollowing = options.KeepPropFollowing;
      _layoutSpacing = options.LayoutSpacing;
      _xExtents = options.XExtents;
      _splitFaces = options.SplitFaces;

      var sources = new List<SourceSurface>();
      foreach (var id in surfaceIds)
      {
        var rhObj = doc.Objects.FindId(id);
        if (rhObj == null)
          continue;
        var brep = BrepFromGeometry(rhObj.Geometry);
        if (brep == null)
          continue;
        sources.Add(new SourceSurface(id, rhObj.Geometry, brep, PartNumberOf(rhObj)));
      }

      if (sources.Count == 0)
      {
        RestoreSelection(doc, startIds);
        return Result.Nothing;
      }

      // SplitFaces: explode each multi-face brep into single-face breps so the
      // unroller handles each face independently, eliminating overlap in output.
      if (_splitFaces)
      {
        var split = new List<SourceSurface>();
        foreach (var src in sources)
        {
          if (src.Brep.Faces.Count <= 1) { split.Add(src); continue; }
          bool keepExistingNumber = true;
          foreach (var face in src.Brep.Faces)
          {
            var fb = face.DuplicateFace(false);
            if (fb != null)
            {
              split.Add(new SourceSurface(src.Id, src.Geometry, fb,
                keepExistingNumber ? src.PreferredPartNumber : null));
              keepExistingNumber = false;
            }
          }
        }
        sources = split;
      }

      var addText = _labelMode == LabelMode.Text;
      var addDots = _labelMode == LabelMode.Dots;
      var addLabels = _labelMode != LabelMode.None;

      var followingItems = MakeFollowingItems(doc, followingIds);
      var assignment = followingItems.Count > 0 ? AssignFollowing(doc, followingItems, sources) : new AssignmentResult(sources.Count);

      double tol = doc.ModelAbsoluteTolerance;
      var partNumbers = AssignPartNumbers(doc, sources);
      var priorOutputs = partNumbers.Select(number => FindPriorFlatOutput(doc, number)).ToList();

      // Build edge-mate pairs before unrolling (once for all source surfaces)
      var edgePairs = _edgeDots
        ? BuildEdgeMates(doc, sources, partNumbers, tol)
        : (List<List<EdgeMateRecord>>?)null;
      double xLimit = _xExtents;
      double xOrigin = options.StartPoint.X;
      double rowY = options.StartPoint.Y;
      double rowHeight = 0.0;
      var nextPoint = options.StartPoint;
      bool exceeded = false;
      int done = 0;
      int failed = 0;
      var failedSourceIds = new List<Guid>();

      doc.Views.RedrawEnabled = false;
      try
      {
        for (int i = 0; i < sources.Count; i++)
        {
          var src = sources[i];
          var unroller = new Unroller(src.Brep)
          {
            ExplodeOutput = _explode,
            AbsoluteTolerance = doc.ModelAbsoluteTolerance,
            RelativeTolerance = doc.ModelRelativeTolerance
          };
          bool usePlanarTransform = TryGetSingleFacePlanarTransform(src.Brep, tol, out var planarTransform);

          int number = partNumbers[i];
          var priorOutput = priorOutputs[i];
          string display = LabelText(number);
          var frame = (addLabels || _rotateFlatParts)
            ? SurfaceLabelFrame(doc, src.Id, ItemTextHeight(doc, src.Id, display, src.Brep), src.Brep)
            : null;

          if (frame != null)
            Dbg($"part={number} original_frame label_height={frame.Height:G6} point={P(frame.Point)} y={V(frame.Y)} x={V(frame.X)} step={frame.Step:G6} up_pt={P(frame.UpPoint)} right_pt={P(frame.RightPoint)}");
          else
            Dbg($"part={number} original_frame None");

          var surfaceItems = i < assignment.Buckets.Count ? assignment.Buckets[i] : new List<FollowingItem>();
          var curveSourceIds = surfaceItems.Where(x => x.Kind == FollowingKind.Curve).Select(x => x.Id).ToList();
          var pointSourceIds = surfaceItems.Where(x => x.Kind == FollowingKind.Point).Select(x => x.Id).ToList();
          var dotSourceIds   = surfaceItems.Where(x => x.Kind == FollowingKind.Dot)  .Select(x => x.Id).ToList();
          var curves = surfaceItems.Where(x => x.Kind == FollowingKind.Curve).Select(x => x.Geometry).OfType<Curve>().ToList();
          var points = surfaceItems.Where(x => x.Kind == FollowingKind.Point).Select(x => x.Geometry).OfType<Point>().ToList();
          var dots = surfaceItems.Where(x => x.Kind == FollowingKind.Dot).Select(x => x.Geometry).OfType<TextDot>().ToList();
          Dbg($"part={number} following_input curves={curves.Count} points={points.Count} dots={dots.Count}");

          foreach (var curve in curves)
            unroller.AddFollowingGeometry(curve);

          // Edge-mate positions use uniquely named hidden dots. Their names survive
          // unrolling, unlike curve output order, so they cannot be confused with
          // label helpers or similarly sized boundary curves.
          var edgeMateRecords = (edgePairs != null && i < edgePairs.Count)
            ? edgePairs[i]
            : new List<EdgeMateRecord>();
          var edgeMateHelperDots = new Dictionary<string, EdgeMateRecord>(StringComparer.Ordinal);
          var followingDots = new List<TextDot>();
          foreach (var rec in UniqueEdgeMateRecords(edgeMateRecords))
          {
            string helperText = EdgeMateHelperDotText(src.Id, rec);
            edgeMateHelperDots[helperText] = rec;
            var helperDot = new TextDot(helperText, rec.Marker);
            followingDots.Add(helperDot);
            unroller.AddFollowingGeometry(helperDot);
          }
          Dbg($"part={number} edge_mates records={edgeMateRecords.Count} helpers={edgeMateHelperDots.Count}");

          foreach (var point in points)
            unroller.AddFollowingGeometry(point.Location);

          string? labelPointDotText = null;
          string? labelUpDotText = null;
          string? labelRightDotText = null;
          if (frame != null)
          {
            // Named dots keep label helpers separate from selected user points and
            // cannot leak visible helper geometry into the flattened output.
            labelPointDotText = LabelHelperDotText(src.Id, number, "point");
            labelUpDotText = LabelHelperDotText(src.Id, number, "up");
            labelRightDotText = LabelHelperDotText(src.Id, number, "right");
            var pointDot = new TextDot(labelPointDotText, frame.Point);
            var upDot = new TextDot(labelUpDotText, frame.UpPoint);
            var rightDot = new TextDot(labelRightDotText, frame.RightPoint);
            followingDots.Add(pointDot);
            followingDots.Add(upDot);
            followingDots.Add(rightDot);
            unroller.AddFollowingGeometry(pointDot);
            unroller.AddFollowingGeometry(upDot);
            unroller.AddFollowingGeometry(rightDot);
          }

          foreach (var dot in dots)
          {
            followingDots.Add(dot);
            unroller.AddFollowingGeometry(dot);
          }

          Curve[] unrolledCurves;
          Point3d[] unrolledPoints;
          TextDot[] unrolledDots;
          Brep[] unrolledBreps;
          try
          {
            if (usePlanarTransform)
            {
              var flatBrep = src.Brep.DuplicateBrep();
              flatBrep.Transform(planarTransform);
              unrolledBreps = new[] { flatBrep };
              unrolledCurves = curves.Select(curve => TransformCurveCopy(curve, planarTransform)).ToArray();
              unrolledPoints = points.Select(point => TransformPoint(point.Location, planarTransform)).ToArray();
              unrolledDots = followingDots.Select(dot => TransformTextDotCopy(dot, planarTransform)).ToArray();
              Dbg($"part={number} unroll_method=planar_exact");
            }
            else if (TryPerformRuledUvUnroll(
              src.Brep, curves, points, followingDots, tol,
              out unrolledBreps, out unrolledCurves, out unrolledPoints, out unrolledDots,
              out string uvDetails))
            {
              Dbg($"part={number} unroll_method=ruled_uv {uvDetails}");
            }
            else
            {
              // Unroll surface only — no following geometry added; prevents boundary-dot triangulation distortion.
              unrolledBreps = unroller.PerformUnroll(out _, out _, out _);
              Dbg($"part={number} unroll_method=rhino_unroller");

              // UV-project following items onto the flat surface (same as TryPerformRuledUvUnroll).
              if (unrolledBreps?.Length > 0 && src.Brep.Faces.Count == unrolledBreps[0].Faces.Count)
              {
                var mc = new List<Curve>(curves.Count);
                var mp = new List<Point3d>(points.Count);
                var md = new List<TextDot>(followingDots.Count);
                for (int fi = 0; fi < src.Brep.Faces.Count; fi++)
                {
                  var sf = src.Brep.Faces[fi];
                  var ff = unrolledBreps[0].Faces[fi].UnderlyingSurface();
                  if (ff == null) continue;
                  foreach (var c in curves)
                  {
                    var uv = sf.Pullback(c, tol);
                    var flat = uv != null ? ff.Pushup(uv, tol) : null;
                    if (flat != null) mc.Add(flat);
                  }
                  foreach (var p in points)
                    if (sf.ClosestPoint(p.Location, out double u, out double v))
                      mp.Add(ff.PointAt(u, v));
                  foreach (var dot in followingDots)
                  {
                    if (!sf.ClosestPoint(dot.Point, out double u, out double v)) continue;
                    var copy = dot.Duplicate() as TextDot ?? new TextDot(dot.Text ?? "", dot.Point);
                    copy.Point = ff.PointAt(u, v);
                    md.Add(copy);
                  }
                }
                unrolledCurves = mc.ToArray();
                unrolledPoints = mp.ToArray();
                unrolledDots   = md.ToArray();
              }
              else
              {
                unrolledCurves = Array.Empty<Curve>();
                unrolledPoints = Array.Empty<Point3d>();
                unrolledDots   = Array.Empty<TextDot>();
              }
            }
          }
          catch (Exception ex)
          {
            Dbg($"part={number} unroll_failed error={ex.Message}");
            AddFailureMarker(doc, src, frame);
            AddValid(failedSourceIds, src.Id);
            failed++;
            continue;
          }
          if (unrolledBreps == null || unrolledBreps.Length == 0)
          {
            Dbg($"part={number} unroll_failed no_breps");
            AddFailureMarker(doc, src, frame);
            AddValid(failedSourceIds, src.Id);
            failed++;
            continue;
          }

          Dbg($"part={number} unroll_output breps={unrolledBreps.Length} curves={unrolledCurves?.Length ?? 0} points={unrolledPoints?.Length ?? 0} dots={unrolledDots?.Length ?? 0}");

          done++;

          var outputIds = new List<Guid>();
          var followingOutputPairs = new List<(Guid srcId, Guid outId)>(); // source-ID → flat output-ID for KeepPropFollowing
          var curveOutputIds    = new List<Guid>();
          // Compute flat midpoints via UV projection (source face → flat face) — no extra geometry added to unroller.
          var curveFlatMidpoints = new Dictionary<int, Point3d>();
          for (int ci = 0; ci < curves.Count; ci++)
          {
            var mid3d = curves[ci].PointAtNormalizedLength(0.5);
            for (int fi = 0; fi < src.Brep.Faces.Count && fi < unrolledBreps[0].Faces.Count; fi++)
            {
              if (!src.Brep.Faces[fi].ClosestPoint(mid3d, out double u, out double v)) continue;
              var check = src.Brep.Faces[fi].PointAt(u, v);
              if (!check.IsValid || check.DistanceTo(mid3d) > tol * 100) continue;
              var flatMid = unrolledBreps[0].Faces[fi].PointAt(u, v);
              if (flatMid.IsValid) { curveFlatMidpoints[ci] = flatMid; break; }
            }
          }
          var finalBreps = !_explode && unrolledBreps.Length > 1
            ? (Brep.JoinBreps(unrolledBreps, tol) ?? unrolledBreps)
            : unrolledBreps;
          foreach (var brep in finalBreps)
            AddValid(outputIds, doc.Objects.AddBrep(brep));

          var edgeFlatPoints = new Dictionary<(string, int, int), Point3d>();
          if (unrolledCurves != null)
          {
            for (int j = 0; j < unrolledCurves.Length; j++)
            {
              var cid = doc.Objects.AddCurve(unrolledCurves[j]);
              AddValid(outputIds, cid);
              curveOutputIds.Add(cid);
            }
            Dbg($"part={number} following_curve_output selected={curves.Count}" +
                $" returned={unrolledCurves.Length} added={unrolledCurves.Length}");
          }

          Point3d? labelPoint = null;
          Point3d? labelUp = null;
          Point3d? labelRight = null;
          if (unrolledPoints != null)
          {
            for (int p = 0; p < unrolledPoints.Length; p++)
            {
              var pid = doc.Objects.AddPoint(unrolledPoints[p]);
              AddValid(outputIds, pid);
              if (IsValidId(pid) && p < pointSourceIds.Count)
                followingOutputPairs.Add((pointSourceIds[p], pid));
            }
          }

          if (unrolledDots != null)
          {
            foreach (var dot in unrolledDots)
            {
              var dotText = dot.Text ?? string.Empty;
              if (dotText.StartsWith(LabelHelperDotPrefix, StringComparison.Ordinal))
              {
                if (dotText == labelPointDotText && !labelPoint.HasValue)
                  labelPoint = dot.Point;
                else if (dotText == labelUpDotText && !labelUp.HasValue)
                  labelUp = dot.Point;
                else if (dotText == labelRightDotText && !labelRight.HasValue)
                  labelRight = dot.Point;

                Dbg($"part={number} hidden_label_dot text={dotText} point={P(dot.Point)}");
                continue;
              }

              if (edgeMateHelperDots.TryGetValue(dotText, out var edgeRecord))
              {
                var key = (edgeRecord.MateId, edgeRecord.EdgeIndex, edgeRecord.MatePartIndex);
                edgeFlatPoints[key] = dot.Point;
                Dbg($"part={number} edge_marker id={edgeRecord.MateId}" +
                    $" edge={edgeRecord.EdgeIndex} mate_part={edgeRecord.MatePartNumber}" +
                    $" point={P(dot.Point)}");
                continue;
              }

              var dotId = doc.Objects.AddTextDot(dot);
              AddValid(outputIds, dotId);
              if (IsValidId(dotId))
              {
                int userIdx = followingOutputPairs.Count(p => dotSourceIds.Contains(p.srcId));
                if (userIdx < dotSourceIds.Count)
                  followingOutputPairs.Add((dotSourceIds[userIdx], dotId));
              }
            }
          }

          // Spatial curve matching: always use proximity to handle splits, drops, and reorderings.
          if (unrolledCurves != null && curveFlatMidpoints.Count > 0)
          {
            for (int j = 0; j < curveOutputIds.Count && j < unrolledCurves.Length; j++)
            {
              if (!IsValidId(curveOutputIds[j])) continue;
              var outMid = unrolledCurves[j].PointAtNormalizedLength(0.5);
              int bestIdx = -1;
              double bestDist = double.MaxValue;
              foreach (var kvp in curveFlatMidpoints)
              {
                double d = outMid.DistanceTo(kvp.Value);
                if (d < bestDist) { bestDist = d; bestIdx = kvp.Key; }
              }
              if (bestIdx >= 0)
              {
                Dbg($"part={number} curve_spatial j={j} src_idx={bestIdx} dist={bestDist:G3}");
                followingOutputPairs.Add((curveSourceIds[bestIdx], curveOutputIds[j]));
              }
            }
          }

          Vector3d unrolledY = Vector3d.YAxis;
          Vector3d? unrolledX = null;
          // The named unrolled frame dots define the flat label direction.
          Vector3d? pointY = null;
          if (labelPoint.HasValue && labelUp.HasValue)
          {
            var raw = new Vector3d(
              labelUp.Value.X - labelPoint.Value.X,
              labelUp.Value.Y - labelPoint.Value.Y, 0.0);
            if (raw.Unitize()) pointY = raw;
          }
          else if (frame != null)
          {
            pointY = new Vector3d(frame.Y.X, frame.Y.Y, 0.0);
            if (pointY.Value.Length > RhinoMath.ZeroTolerance)
            { var tmp = pointY.Value; tmp.Unitize(); pointY = tmp; }
          }
          Vector3d chosen = Vector3d.YAxis;
          if (pointY.HasValue && pointY.Value.Length > RhinoMath.ZeroTolerance)
            chosen = pointY.Value;
          unrolledY = chosen;

          Dbg($"part={number} label_unrolled" +
              $" point={P(labelPoint)} up={P(labelUp)} right={P(labelRight)}" +
              $" point_y={V(pointY)} chosen_y={V(unrolledY)}" +
              $" label_pt={P(labelPoint)}");

          if (labelPoint.HasValue && labelRight.HasValue)
            unrolledX = labelRight.Value - labelPoint.Value;

          if (unrolledX.HasValue)
          {
            double handedness = Vector3d.CrossProduct(unrolledX.Value, unrolledY).Z;
            Dbg($"part={number} flat_frame handedness={handedness:G6}");
          }

          var unrolledLabelIds = new List<Guid>();
          if (frame != null && labelPoint.HasValue)
          {
            if (addText)
            {
              // Keep the raw unrolled up direction as the orientation marker, but do not use
              // the raw frame normal for flat labels. If the unrolled helper frame lands with
              // a -Z normal, annotation text becomes mirrored in Top view. World +Z keeps the
              // text readable while preserving the same unrolled Y/up direction.
              AddValid(unrolledLabelIds, AddFlatText(doc, display, labelPoint.Value, unrolledY, Vector3d.ZAxis, frame.Height, src.Id, _keepPropSurface));
            }
            else if (addDots)
            {
              AddValid(unrolledLabelIds, AddDot(doc, display, labelPoint.Value, outputIds.FirstOrDefault(), _keepPropSurface));
            }
            outputIds.AddRange(unrolledLabelIds.Where(IsValidId));
          }

          if (_keepPropSurface)
          {
            TransferAttributes(doc, outputIds, src.Id);
            PutOnReferenceLayer(doc, unrolledLabelIds);
          }
          if (_keepPropFollowing && followingOutputPairs.Count > 0)
          {
            foreach (var (srcId, outId) in followingOutputPairs)
            {
              var srcObj = doc.Objects.FindId(srcId);
              var outObj = doc.Objects.FindId(outId);
              if (srcObj == null || outObj == null) continue;
              var attrs = outObj.Attributes.Duplicate();
              attrs.LayerIndex  = srcObj.Attributes.LayerIndex;
              attrs.ObjectColor = srcObj.Attributes.ObjectColor;
              attrs.ColorSource = srcObj.Attributes.ColorSource;
              doc.Objects.ModifyAttributes(outId, attrs, true);
            }
          }

          if (addLabels && frame != null && (priorOutput == null || !priorOutput.MemberIds.Contains(src.Id)))
          {
            EnsureOriginalLabel(doc, src.Id, number, display, frame, addText, addDots, _keepPropSurface);
          }

          // Place edge mate dots on flat output
          if (edgeFlatPoints.Count > 0)
          {
            int refLayerIdx = ReferenceLayerIndex(doc);
            foreach (var rec in UniqueEdgeMateRecords(edgeMateRecords))
            {
              var key = (rec.MateId, rec.EdgeIndex, rec.MatePartIndex);
              if (!edgeFlatPoints.TryGetValue(key, out var flatPt)) continue;
              var dotId = AddEdgeMateDot(doc, rec, flatPt, number, refLayerIdx);
              if (IsValidId(dotId))
                outputIds.Add(dotId);
            }
          }

          if (_rotateFlatParts && frame != null && labelPoint.HasValue)
            RotateObjectsToTextUp(doc, outputIds, labelPoint.Value, unrolledY);

          bool keptPriorPlacement = false;
          if (priorOutput != null && !options.PlacementSpecified)
          {
            keptPriorPlacement = TryRestorePriorFlatPlacement(
              doc, priorOutput, outputIds, out string placementMethod);
            Dbg($"part={number} placement=prior restored={keptPriorPlacement}" +
                $" method={placementMethod}");
          }

          if (priorOutput != null)
            ReplacePriorFlatGroup(doc, priorOutput, outputIds, number);
          else
            GroupObjects(doc, outputIds, FlatGroupPrefix, number);

          var bbox = BoundingBoxOfObjects(doc, outputIds);
          if (!keptPriorPlacement && bbox.HasValue && bbox.Value.IsValid)
          {
            var box = bbox.Value;
            double width = box.Max.X - box.Min.X;
            double height = box.Max.Y - box.Min.Y;

            if (xLimit > 0)
            {
              if (width > xLimit)
              {
                xLimit = width;
                exceeded = true;
              }

              bool rowHasObjects = nextPoint.X > xOrigin + RhinoMath.ZeroTolerance;
              if (rowHasObjects && nextPoint.X + width > xOrigin + xLimit)
              {
                rowY += rowHeight + _layoutSpacing;
                rowHeight = 0.0;
                nextPoint = new Point3d(xOrigin, rowY, options.StartPoint.Z);
              }

              rowHeight = Math.Max(rowHeight, height);
            }

            var target = new Point3d(nextPoint.X, rowY, options.StartPoint.Z);
            var move = target - box.Min;
            TransformObjects(doc, outputIds, Transform.Translation(move));

            // Advance along the row baseline only. Do not use box.Max.Y here, or every part
            // starts from the previous part's top-right corner and the layout drifts diagonally.
            nextPoint = new Point3d(target.X + width + _layoutSpacing, rowY, options.StartPoint.Z);
          }
        }
      }
      finally
      {
        doc.Views.RedrawEnabled = true;
        var problemIds = Unique(failedSourceIds.Concat(assignment.SkippedIds)).ToList();
        RestoreSelection(doc, problemIds.Count > 0 ? problemIds : startIds);
        doc.Views.Redraw();
      }

      _xExtents = xLimit;

      if (exceeded)
      {
        RhinoApp.WriteLine("At least one unrolled object exceeded the X extents limit. Limit extended to {0:0.##} {1}.",
          xLimit, doc.ModelUnitSystem);
      }

      var msg = $"Successfully unrolled {done} objects";
      if (failed > 0)
        msg += $" | Unable to unroll {failed} objects";
      if (assignment.Skipped > 0)
        msg += $" | Skipped {assignment.Skipped} following object(s) not close to selected surfaces";
      msg += $" | Labels {_labelMode} | RotateFlatParts {(_rotateFlatParts ? "Yes" : "No")} | EdgeDots {(_edgeDots ? "On" : "Off")}";
      RhinoApp.WriteLine(msg);

      return done > 0 ? Result.Success : Result.Nothing;
    }

    private static List<Guid>? GetSurfaceIds(RhinoDoc doc, List<Guid> preselected)
    {
      preselected = Unique(preselected.Where(IsSurfaceLikeId));
      if (preselected.Count > 0)
        return preselected;

      var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.SetCommandPrompt("Select surface/polysurface objects to unroll");
      go.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
      go.SubObjectSelect = false;
      go.GroupSelect = true;
      go.EnablePreSelect(true, true);
      go.EnablePostSelect(true);
      var shared = AddSharedOptions(go);

      while (true)
      {
        var rc = go.GetMultiple(1, 0);
        if (rc == GetResult.Cancel || go.CommandResult() == Result.Cancel)
          return null;
        if (rc == GetResult.Option)
        {
          HandleSharedOption(go, shared);
          continue;
        }
        if (go.CommandResult() != Result.Success)
          return null;
        return Unique(Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId).Where(IsSurfaceLikeId));
      }
    }

    private static List<Guid>? GetFollowingIds(RhinoDoc doc, List<Guid> seedIds, List<Guid> surfaceIds)
    {
      seedIds = Unique(seedIds.Where(IsFollowingLikeId));
      var surfaceSet = new HashSet<Guid>(surfaceIds);

      SelectOnly(doc, seedIds);
      Highlight(doc, surfaceIds, true);

      var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.SetCommandPrompt("Select curves, points, or dots to unroll with highlighted surfaces. Press Enter when done");
      go.GeometryFilter = ObjectType.Curve | ObjectType.Point | ObjectType.TextDot;
      go.SubObjectSelect = false;
      go.GroupSelect = true;
      go.AcceptNothing(true);
      go.EnablePreSelect(true, true);
      go.EnablePostSelect(true);
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;
      go.AlreadySelectedObjectSelect = true;
      var shared = AddSharedOptions(go);

      try
      {
        while (true)
        {
          var rc = go.GetMultiple(0, 0);
          if (rc == GetResult.Cancel || go.CommandResult() == Result.Cancel)
            return null;
          if (rc == GetResult.Option)
          {
            HandleSharedOption(go, shared);
            continue;
          }
          if (go.ObjectsWerePreselected)
          {
            go.EnablePreSelect(false, true);
            continue;
          }
          break;
        }

        for (int i = 0; i < go.ObjectCount; i++)
        {
          var obj = go.Object(i).Object();
          obj?.Select(true);
        }

        return Unique(SelectedIds(doc).Where(id => !surfaceSet.Contains(id) && IsFollowingLikeId(id)));
      }
      finally
      {
        Highlight(doc, surfaceIds, false);
      }
    }

    private static LayoutOptions? GetLayoutOptions(RhinoDoc doc, string prompt)
    {
      var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt(prompt);
      var shared = AddSharedOptions(gp);
      gp.AcceptNothing(true);

      var point = Point3d.Origin;
      bool placementSpecified = false;
      while (true)
      {
        var rc = gp.Get();
        HandleSharedOption(gp, shared);
        if (gp.CommandResult() == Result.Cancel)
          return null;
        if (gp.CommandResult() == Result.Nothing)
          break;
        if (rc == GetResult.Point)
        {
          point = gp.Point();
          placementSpecified = true;
          break;
        }
        if (rc == GetResult.Option)
          continue;
        break;
      }

      return new LayoutOptions
      {
        StartPoint     = point,
        PlacementSpecified = placementSpecified,
        LabelMode      = _labelMode,
        RotateFlatParts= _rotateFlatParts,
        Explode        = _explode,
        SplitFaces     = _splitFaces,
        KeepPropSurface  = _keepPropSurface,
        KeepPropFollowing = _keepPropFollowing,
        LayoutSpacing  = _layoutSpacing,
        XExtents       = _xExtents
      };
    }

    private class SharedOptions
    {
      public int LabelIndex = -1;
      public OptionToggle? RotateOption;
      public OptionToggle? EdgeDotsOption;
      public OptionToggle? ExplodeOption;
      public OptionToggle? SplitFacesOption;
      public OptionToggle? SurfacePropsOption;
      public OptionToggle? FollowingPropsOption;
      public int SpacingIndex = -1;
      public int XExtentsIndex = -1;
    }

    private static SharedOptions AddSharedOptions(GetBaseClass getter)
    {
      var state = new SharedOptions();
      state.LabelIndex = getter.AddOptionList("Labels", LabelModeNames, (int)_labelMode);
      state.RotateOption = new OptionToggle(_rotateFlatParts, "No", "Yes");
      getter.AddOptionToggle("RotateFlatParts", ref state.RotateOption);
      state.EdgeDotsOption = new OptionToggle(_edgeDots, "Off", "On");
      getter.AddOptionToggle("EdgeDots", ref state.EdgeDotsOption);
      state.ExplodeOption = new OptionToggle(_explode, "No", "Yes");
      getter.AddOptionToggle("Explode", ref state.ExplodeOption);
      state.SplitFacesOption = new OptionToggle(_splitFaces, "No", "Yes");
      getter.AddOptionToggle("SplitFaces", ref state.SplitFacesOption);
      state.SurfacePropsOption = new OptionToggle(_keepPropSurface, "No", "Yes");
      getter.AddOptionToggle("KeepPropSurface", ref state.SurfacePropsOption);
      state.FollowingPropsOption = new OptionToggle(_keepPropFollowing, "No", "Yes");
      getter.AddOptionToggle("KeepPropFollowing", ref state.FollowingPropsOption);
      state.SpacingIndex  = getter.AddOption("Spacing",  $"{_layoutSpacing:G}");
      state.XExtentsIndex = getter.AddOption("XExtents", $"{_xExtents:G}");
      return state;
    }

    private static void HandleSharedOption(GetBaseClass getter, SharedOptions state)
    {
      if (state == null) return;

      if (state.RotateOption     != null) _rotateFlatParts  = state.RotateOption.CurrentValue;
      if (state.EdgeDotsOption   != null) _edgeDots         = state.EdgeDotsOption.CurrentValue;
      if (state.ExplodeOption    != null) _explode          = state.ExplodeOption.CurrentValue;
      if (state.SplitFacesOption != null) _splitFaces       = state.SplitFacesOption.CurrentValue;
      if (state.SurfacePropsOption   != null) _keepPropSurface  = state.SurfacePropsOption.CurrentValue;
      if (state.FollowingPropsOption != null) _keepPropFollowing = state.FollowingPropsOption.CurrentValue;

      var option = getter.Option();

      // Print a one-line description for the option that just changed (no tooltip API exists in Rhino).
      if (option != null)
      {
        switch (option.EnglishName)
        {
          case "RotateFlatParts":    RhinoApp.WriteLine("RotateFlatParts: rotate each flat part so its label text faces up."); break;
          case "EdgeDots":           RhinoApp.WriteLine("EdgeDots: place numbered match dots on shared edges between adjacent parts."); break;
          case "Explode":            RhinoApp.WriteLine("Explode: keep each brep face as a separate surface instead of joining them."); break;
          case "SplitFaces":         RhinoApp.WriteLine("SplitFaces: split polysurfaces into individual faces before unrolling."); break;
          case "KeepPropSurface":    RhinoApp.WriteLine("KeepPropSurface: flat brep inherits layer and colour of its source surface."); break;
          case "KeepPropFollowing":  RhinoApp.WriteLine("KeepPropFollowing: each flat curve/point/dot inherits layer and colour of its original object."); break;
          case "Labels":             RhinoApp.WriteLine("Labels: Text = annotation text, Dots = text dots, None = no label on flat output."); break;
          case "Spacing":            RhinoApp.WriteLine("Spacing: gap between flat parts in the layout row."); break;
          case "XExtents":           RhinoApp.WriteLine("XExtents: maximum row width before wrapping to a new row (0 = unlimited)."); break;
        }
      }

      if (option != null && option.Index == state.LabelIndex)
      {
        int idx = option.CurrentListOptionIndex;
        if (idx >= 0 && idx < LabelModeNames.Length)
          _labelMode = (LabelMode)idx;
      }
      if (option != null && option.Index == state.SpacingIndex)
      {
        var gs = new GetString();
        gs.SetCommandPrompt("Layout spacing");
        gs.SetDefaultString($"{_layoutSpacing:G}");
        gs.AcceptNothing(true);
        if (gs.Get() == GetResult.String &&
            double.TryParse(gs.StringResult().Trim(),
              System.Globalization.NumberStyles.Any,
              System.Globalization.CultureInfo.InvariantCulture, out double sv) && sv >= 0)
          _layoutSpacing = sv;
      }
      if (option != null && option.Index == state.XExtentsIndex)
      {
        var gs = new GetString();
        gs.SetCommandPrompt("X extents limit (0 = unlimited)");
        gs.SetDefaultString($"{_xExtents:G}");
        gs.AcceptNothing(true);
        if (gs.Get() == GetResult.String &&
            double.TryParse(gs.StringResult().Trim(),
              System.Globalization.NumberStyles.Any,
              System.Globalization.CultureInfo.InvariantCulture, out double xv) && xv >= 0)
          _xExtents = xv;
      }
    }

    private static List<Guid> SelectedIds(RhinoDoc doc)
    {
      return doc.Objects.GetSelectedObjects(false, false).Select(o => o.Id).ToList();
    }

    private static void SelectOnly(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      doc.Objects.UnselectAll();
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        obj?.Select(true);
      }
      doc.Views.Redraw();
    }

    private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      doc.Objects.UnselectAll();
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        obj?.Select(true);
      }
    }

    private static void Highlight(RhinoDoc doc, IEnumerable<Guid> ids, bool state)
    {
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        obj?.Highlight(state);
      }
      doc.Views.Redraw();
    }

    private static bool IsSurfaceLikeId(Guid id)
    {
      var obj = RhinoDoc.ActiveDoc?.Objects.FindId(id);
      return obj != null && IsSurfaceLike(obj.ObjectType);
    }

    private static bool IsFollowingLikeId(Guid id)
    {
      var obj = RhinoDoc.ActiveDoc?.Objects.FindId(id);
      return obj != null && IsFollowingLike(obj.ObjectType);
    }

    private static bool IsSurfaceLike(ObjectType type)
    {
      return type == ObjectType.Surface || type == ObjectType.Brep || type == ObjectType.Extrusion;
    }

    private static bool IsFollowingLike(ObjectType type)
    {
      return type == ObjectType.Curve || type == ObjectType.Point || type == ObjectType.TextDot;
    }

    private static bool IsValidId(Guid id)
    {
      return id != Guid.Empty;
    }

    private static void AddValid(List<Guid> ids, Guid id)
    {
      if (IsValidId(id))
        ids.Add(id);
    }

    private static List<T> Unique<T>(IEnumerable<T> values)
    {
      var seen = new HashSet<T>();
      var output = new List<T>();
      if (values == null)
        return output;
      foreach (var value in values)
      {
        if (seen.Add(value))
          output.Add(value);
      }
      return output;
    }

    private static Brep? BrepFromGeometry(GeometryBase? geometry)
    {
      if (geometry is Brep brep)
        return brep;
      if (geometry is Extrusion extrusion)
        return extrusion.ToBrep();
      if (geometry is Surface surface)
        return surface.ToBrep();
      return null;
    }

    private static double VectorLength(Vector3d v)
    {
      return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
    }

    private static Vector3d? Unit(Vector3d v, double tol)
    {
      if (VectorLength(v) <= tol)
        return null;
      if (v.Unitize())
        return v;
      return null;
    }

    private static Vector3d? ProjectToPlane(Vector3d v, Vector3d normal, double tol)
    {
      var n = Unit(normal, tol);
      if (!n.HasValue)
        return Unit(v, tol);
      var projected = v - n.Value * (v * n.Value);
      return Unit(projected, tol);
    }

    private static Vector3d ClosestNormal(Brep brep, Point3d point, Vector3d fallback, double tol)
    {
      if (brep == null)
        return fallback;

      Vector3d? best = null;
      double bestDistance = double.MaxValue;
      foreach (var face in brep.Faces)
      {
        double u, v;
        if (!face.ClosestPoint(point, out u, out v))
          continue;
        var facePoint = face.PointAt(u, v);
        var distance = point.DistanceToSquared(facePoint);
        var normal = Unit(face.NormalAt(u, v), tol);
        if (normal.HasValue && distance < bestDistance)
        {
          best = normal.Value;
          bestDistance = distance;
        }
      }
      return best ?? fallback;
    }

    private static Vector3d NormalFromOutputBreps(IEnumerable<Brep> breps, Point3d point)
    {
      var tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? RhinoMath.ZeroTolerance;
      Vector3d bestNormal = Vector3d.ZAxis;
      double bestDistance = double.MaxValue;
      foreach (var brep in breps ?? Enumerable.Empty<Brep>())
      {
        var normal = ClosestNormal(brep, point, Vector3d.ZAxis, tol);
        double distance;
        try { distance = point.DistanceToSquared(brep.ClosestPoint(point)); }
        catch { distance = 0.0; }
        if (distance < bestDistance)
        {
          bestDistance = distance;
          bestNormal = normal;
        }
      }
      return bestNormal;
    }

    private static Point3d? LabelPoint(RhinoDoc doc, Guid objId, Brep? brepHint = null)
    {
      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      if (brep != null)
      {
        var area = AreaMassProperties.Compute(brep);
        Point3d point;
        if (area != null) point = area.Centroid;
        else point = brep.GetBoundingBox(true).Center;
        try { return brep.ClosestPoint(point); }
        catch { return point; }
      }
      if (brepHint != null) return null;
      var obj = doc.Objects.FindId(objId);
      var bbox = obj?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
      return bbox.IsValid ? bbox.Center : (Point3d?)null;
    }

    private static IEnumerable<Point3d> CurveSamples(Curve curve, int count)
    {
      var pts = new List<Point3d>();
      if (curve == null)
        return pts;
      try
      {
        var dom = curve.Domain;
        int n = Math.Max(count, 2);
        for (int i = 0; i < n; i++)
        {
          double t = dom.T0 + (dom.T1 - dom.T0) * i / (double)(n - 1);
          pts.Add(curve.PointAt(t));
        }
      }
      catch
      {
        pts.Add(curve.PointAtStart);
        pts.Add(curve.PointAtEnd);
      }
      return pts;
    }

    private static List<Point3d> BoundaryPoints(RhinoDoc doc, Guid objId, Brep? brepHint = null)
    {
      var pts = new List<Point3d>();
      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      if (brep != null)
      {
        pts.AddRange(brep.Vertices.Select(v => v.Location));
        foreach (var curve in brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          pts.AddRange(CurveSamples(curve, TextBoundarySamples));
      }
      if (pts.Count == 0)
      {
        var geom = (GeometryBase?)brepHint ?? doc.Objects.FindId(objId)?.Geometry;
        var bbox = geom?.GetBoundingBox(true) ?? BoundingBox.Empty;
        if (bbox.IsValid) pts.AddRange(bbox.GetCorners());
      }
      return pts;
    }

    private static LabelFrame? SurfaceLabelFrame(RhinoDoc doc, Guid objId, double height, Brep? brepHint = null)
    {
      var point = LabelPoint(doc, objId, brepHint);
      if (!point.HasValue)
        return null;

      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      var tol = doc.ModelAbsoluteTolerance;
      var faceHit = brep != null ? ClosestFaceHit(brep, point.Value, tol) : null;
      var normal = faceHit?.Normal ?? (brep != null ? ClosestNormal(brep, point.Value, Vector3d.ZAxis, tol) : Vector3d.ZAxis);
      var y = ProjectToPlane(Vector3d.ZAxis, normal, tol)
              ?? ProjectToPlane(Vector3d.YAxis, normal, tol)
              ?? Vector3d.YAxis;
      var x = Unit(Vector3d.CrossProduct(y, normal), tol) ?? Vector3d.XAxis;
      y = Unit(y, tol) ?? Vector3d.YAxis;
      normal = Unit(normal, tol) ?? Vector3d.ZAxis;

      var step = Math.Max(height * TextUpStepRatio, tol * 20.0);
      var upPoint = faceHit != null
        ? SameFaceStepPoint(faceHit, y, step, tol)
        : point.Value + y * step;
      var rightPoint = faceHit != null
        ? SameFaceStepPoint(faceHit, x, step, tol)
        : point.Value + x * step;

      var actualY = Unit(upPoint - point.Value, tol) ?? y;
      var actualX = Unit(rightPoint - point.Value, tol) ?? x;
      return new LabelFrame(point.Value, upPoint, rightPoint, actualX, actualY, normal, height, step);
    }

    private class FaceHit
    {
      public BrepFace Face { get; }
      public double U { get; }
      public double V { get; }
      public Point3d Point { get; }
      public Vector3d Normal { get; }

      public FaceHit(BrepFace face, double u, double v, Point3d point, Vector3d normal)
      {
        Face = face;
        U = u;
        V = v;
        Point = point;
        Normal = normal;
      }
    }

    private static FaceHit? ClosestFaceHit(Brep brep, Point3d point, double tol)
    {
      FaceHit? best = null;
      double bestDistance = double.MaxValue;
      foreach (var face in brep.Faces)
      {
        double u, v;
        if (!face.ClosestPoint(point, out u, out v))
          continue;
        var facePoint = face.PointAt(u, v);
        var distance = point.DistanceToSquared(facePoint);
        var normal = Unit(face.NormalAt(u, v), tol) ?? Vector3d.ZAxis;
        if (distance < bestDistance)
        {
          bestDistance = distance;
          best = new FaceHit(face, u, v, facePoint, normal);
        }
      }
      return best;
    }

    private static Point3d SameFaceStepPoint(FaceHit hit, Vector3d direction, double step, double tol)
    {
      var face = hit.Face;
      var uDomain = face.Domain(0);
      var vDomain = face.Domain(1);
      var epsU = Math.Max(uDomain.Length * 1.0e-6, RhinoMath.ZeroTolerance);
      var epsV = Math.Max(vDomain.Length * 1.0e-6, RhinoMath.ZeroTolerance);
      var u0 = Math.Max(uDomain.T0, hit.U - epsU);
      var u1 = Math.Min(uDomain.T1, hit.U + epsU);
      var v0 = Math.Max(vDomain.T0, hit.V - epsV);
      var v1 = Math.Min(vDomain.T1, hit.V + epsV);

      var su = u1 > u0 ? (face.PointAt(u1, hit.V) - face.PointAt(u0, hit.V)) / (u1 - u0) : Vector3d.XAxis;
      var sv = v1 > v0 ? (face.PointAt(hit.U, v1) - face.PointAt(hit.U, v0)) / (v1 - v0) : Vector3d.YAxis;
      var target = (Unit(direction, tol) ?? Vector3d.YAxis) * step;

      double a = su * su;
      double b = su * sv;
      double c = sv * sv;
      double d = su * target;
      double e = sv * target;
      double det = a * c - b * b;
      if (Math.Abs(det) <= 1.0e-16)
        return hit.Point + (Unit(direction, tol) ?? Vector3d.YAxis) * step;

      double du = (d * c - b * e) / det;
      double dv = (a * e - b * d) / det;

      for (int i = 0; i < 8; i++)
      {
        double scale = 1.0 / Math.Pow(2.0, i);
        double u = Math.Max(uDomain.T0, Math.Min(uDomain.T1, hit.U + du * scale));
        double v = Math.Max(vDomain.T0, Math.Min(vDomain.T1, hit.V + dv * scale));
        try
        {
          var relation = face.IsPointOnFace(u, v);
          if (relation != PointFaceRelation.Exterior)
          {
            var point = face.PointAt(u, v);
            if (point.DistanceTo(hit.Point) > tol * 2.0)
              return point;
          }
        }
        catch
        {
          var point = face.PointAt(u, v);
          if (point.DistanceTo(hit.Point) > tol * 2.0)
            return point;
        }
      }

      return hit.Point + (Unit(direction, tol) ?? Vector3d.YAxis) * step;
    }

    private static double CenteredSpan(IEnumerable<double> values)
    {
      var list = values.ToList();
      if (list.Count == 0)
        return 0.0;
      double min = list.Min();
      double max = list.Max();
      if (min < 0.0 && max > 0.0)
        return 2.0 * Math.Min(Math.Abs(min), Math.Abs(max));
      return max - min;
    }

    private static double HeightCandidate(RhinoDoc doc, Guid objId, Brep? brepHint = null)
    {
      var point = LabelPoint(doc, objId, brepHint);
      var pts   = BoundaryPoints(doc, objId, brepHint);
      if (!point.HasValue || pts.Count == 0)
      {
        var bbox = brepHint?.GetBoundingBox(true)
          ?? doc.Objects.FindId(objId)?.Geometry.GetBoundingBox(true)
          ?? BoundingBox.Empty;
        if (!bbox.IsValid)
          return 1.0;
        var spans = new[] { bbox.Max.X - bbox.Min.X, bbox.Max.Y - bbox.Min.Y, bbox.Max.Z - bbox.Min.Z }
          .Where(s => s > doc.ModelAbsoluteTolerance).ToList();
        return spans.Count > 0 ? spans.Min() * 0.04 : 1.0;
      }

      var frame = SurfaceLabelFrame(doc, objId, 1.0, brepHint);
      if (frame == null)
        return 1.0;
      var ys = pts.Select(p => (p - point.Value) * frame.Y);
      var span = CenteredSpan(ys);
      return span > doc.ModelAbsoluteTolerance ? span * 0.55 : 1.0;
    }

    private static double SharedTextHeight(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var values = Unique(ids).Select(id => HeightCandidate(doc, id)).Where(v => v > doc.ModelAbsoluteTolerance).ToList();
      var baseHeight = values.Count > 0 ? values.Min() : 1.0;
      return Math.Max(baseHeight * TextHeightScale, doc.ModelAbsoluteTolerance * 10.0);
    }

    private static Plane TextPlane(Point3d origin, Vector3d yDirection, Vector3d normal, double tol)
    {
      normal = Unit(normal, tol) ?? Vector3d.ZAxis;
      var y = ProjectToPlane(yDirection, normal, tol) ?? Vector3d.YAxis;
      var x = Unit(Vector3d.CrossProduct(y, normal), tol) ?? Vector3d.XAxis;
      return new Plane(origin, x, y);
    }

    private static Guid AddFlatText(RhinoDoc doc, string text, Point3d point, Vector3d yDirection, Vector3d normal, double height, Guid sourceId, bool transfer)
    {
      var n = Unit(normal, doc.ModelAbsoluteTolerance) ?? Vector3d.ZAxis;
      var lift = Math.Max(height * TextLiftRatio, doc.ModelAbsoluteTolerance * 2.0);
      var plane = TextPlane(point + n * lift, yDirection, n, doc.ModelAbsoluteTolerance);
      var attrs = LabelAttributes(doc, sourceId, transfer, text);

      try
      {
        var te = new TextEntity
        {
          PlainText = text,
          Plane = plane,
          TextHeight = height,
          Justification = TextJustification.MiddleCenter,
          Font = Font.FromQuartetProperties(TextFont, false, false)
        };
        return doc.Objects.AddText(te, attrs);
      }
      catch
      {
        // Older RhinoCommon fallback.
        return doc.Objects.AddText(text, plane, height, TextFont, false, false, attrs);
      }
    }

    private static Guid AddDot(RhinoDoc doc, string text, Point3d point, Guid sourceId, bool transfer)
    {
      var attrs = LabelAttributes(doc, sourceId, transfer, text);
      return doc.Objects.AddTextDot(text, point, attrs);
    }

    private static Guid AddFailureMarker(RhinoDoc doc, SourceSurface src, LabelFrame? frame)
    {
      frame ??= SurfaceLabelFrame(doc, src.Id, ItemTextHeight(doc, src.Id, FailureMarkerText, src.Brep), src.Brep);

      if (frame != null)
      {
        var height = Math.Max(frame.Height, doc.ModelAbsoluteTolerance * 10.0);
        return AddFailureText(doc, frame.Point, frame.Y, frame.Normal, height, src.Id);
      }

      var point = LabelPoint(doc, src.Id, src.Brep);
      if (!point.HasValue)
      {
        var bbox = src.Brep.GetBoundingBox(true);
        if (bbox.IsValid)
          point = bbox.Center;
      }
      if (!point.HasValue)
        return Guid.Empty;

      var fallbackHeight = Math.Max(ItemTextHeight(doc, src.Id, FailureMarkerText, src.Brep), doc.ModelAbsoluteTolerance * 10.0);
      var normal = ClosestNormal(src.Brep, point.Value, Vector3d.ZAxis, doc.ModelAbsoluteTolerance);
      var up = ProjectToPlane(Vector3d.YAxis, normal, doc.ModelAbsoluteTolerance) ?? Vector3d.YAxis;
      return AddFailureText(doc, point.Value, up, normal, fallbackHeight, src.Id);
    }

    private static Guid AddFailureText(RhinoDoc doc, Point3d point, Vector3d yDirection, Vector3d normal, double height, Guid sourceId)
    {
      var n = Unit(normal, doc.ModelAbsoluteTolerance) ?? Vector3d.ZAxis;
      var lift = Math.Max(height * TextLiftRatio, doc.ModelAbsoluteTolerance * 2.0);
      var plane = TextPlane(point + n * lift, yDirection, n, doc.ModelAbsoluteTolerance);
      var attrs = FailureMarkerAttributes(doc, sourceId);

      try
      {
        var te = new TextEntity
        {
          PlainText = FailureMarkerText,
          Plane = plane,
          TextHeight = height,
          Justification = TextJustification.MiddleCenter,
          Font = Font.FromQuartetProperties(TextFont, false, false)
        };
        return doc.Objects.AddText(te, attrs);
      }
      catch
      {
        return doc.Objects.AddText(FailureMarkerText, plane, height, TextFont, false, false, attrs);
      }
    }

    private static ObjectAttributes LabelAttributes(RhinoDoc doc, Guid sourceId, bool transfer, string labelText)
    {
      ObjectAttributes? attrs = null;
      if (transfer && IsValidId(sourceId))
        attrs = doc.Objects.FindId(sourceId)?.Attributes.Duplicate();
      attrs ??= new ObjectAttributes();
      attrs.Name = TextObjectName;
      attrs.LayerIndex = ReferenceLayerIndex(doc);
      attrs.SetUserString(LabelNumberKey, BaseNumber(labelText));
      return attrs;
    }

    private static ObjectAttributes FailureMarkerAttributes(RhinoDoc doc, Guid sourceId)
    {
      var attrs = doc.Objects.FindId(sourceId)?.Attributes.Duplicate() ?? new ObjectAttributes();
      attrs.Name = FailureMarkerName;
      attrs.LayerIndex = ReferenceLayerIndex(doc);
      attrs.ObjectColor = System.Drawing.Color.Red;
      attrs.ColorSource = ObjectColorSource.ColorFromObject;
      attrs.SetUserString(FailedUnrollKey, "true");
      return attrs;
    }

    private static string BaseNumber(object? text)
    {
      var s = text?.ToString()?.Trim() ?? string.Empty;
      if (s.EndsWith(".", StringComparison.Ordinal))
        s = s.Substring(0, s.Length - 1).Trim();
      return s;
    }

    private static string LabelText(int number)
    {
      var text = number.ToString();
      if (TextMarkSixNine && text.Length > 0 && text.All(ch => ch == '6' || ch == '9'))
      {
        var chars = text.Reverse().Select(ch => ch == '6' ? '9' : '6').ToArray();
        var rotated = new string(chars);
        if (rotated != text)
          return text + ".";
      }
      return text;
    }

    private static int ReferenceLayerIndex(RhinoDoc doc)
    {
      var existing = doc.Layers.FindName(TextLayerName);
      if (existing != null)
        return existing.Index;
      var layer = new Layer { Name = TextLayerName };
      int index = doc.Layers.Add(layer);
      return index >= 0 ? index : doc.Layers.CurrentLayerIndex;
    }

    private static void PutOnReferenceLayer(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      int layer = ReferenceLayerIndex(doc);
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var attrs = obj.Attributes.Duplicate();
        attrs.LayerIndex = layer;
        doc.Objects.ModifyAttributes(id, attrs, true);
      }
    }

    private static void TransferAttributes(RhinoDoc doc, IEnumerable<Guid> targetIds, Guid sourceId)
    {
      var source = doc.Objects.FindId(sourceId);
      if (source == null)
        return;
      foreach (var id in Unique(targetIds))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var attrs = obj.Attributes.Duplicate();
        attrs.LayerIndex = source.Attributes.LayerIndex;
        attrs.ObjectColor = source.Attributes.ObjectColor;
        attrs.ColorSource = source.Attributes.ColorSource;
        doc.Objects.ModifyAttributes(id, attrs, true);
      }
    }

    private static void SetMatchNumber(RhinoDoc doc, IEnumerable<Guid> ids, object? number)
    {
      var value = BaseNumber(number);
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var attrs = obj.Attributes.Duplicate();
        attrs.SetUserString(LabelNumberKey, value);
        doc.Objects.ModifyAttributes(id, attrs, true);
      }
    }

    private static int? PartNumberOf(RhinoObject? obj)
    {
      if (obj == null)
        return null;

      var text = obj.Attributes.GetUserString(LabelNumberKey);
      if (string.IsNullOrWhiteSpace(text))
        text = obj.Attributes.GetUserString(EdgePartNumKey);
      return int.TryParse(BaseNumber(text), out int number) && number > 0 ? number : (int?)null;
    }

    private static List<int> AssignPartNumbers(RhinoDoc doc, IReadOnlyList<SourceSurface> sources)
    {
      var documentNumbers = new HashSet<int>(doc.Objects
        .Select(PartNumberOf)
        .Where(number => number.HasValue)
        .Select(number => number!.Value));
      var assigned = new HashSet<int>();
      int next = documentNumbers.Count > 0 ? documentNumbers.Max() + 1 : 1;
      var result = new List<int>(sources.Count);

      foreach (var source in sources)
      {
        int number;
        if (source.PreferredPartNumber.HasValue && source.PreferredPartNumber.Value > 0 &&
            assigned.Add(source.PreferredPartNumber.Value))
        {
          number = source.PreferredPartNumber.Value;
        }
        else
        {
          while (documentNumbers.Contains(next) || assigned.Contains(next))
            next++;
          number = next++;
          assigned.Add(number);
        }
        result.Add(number);
      }

      Dbg($"part_numbers={string.Join(",", result)}");
      return result;
    }

    private static PriorFlatOutput? FindPriorFlatOutput(RhinoDoc doc, int partNumber)
    {
      PriorFlatOutput? best = null;
      int bestScore = int.MinValue;

      for (int groupIndex = 0; groupIndex < doc.Groups.Count; groupIndex++)
      {
        var group = doc.Groups.FindIndex(groupIndex);
        if (group == null || group.IsDeleted)
          continue;

        var matchingMembers = (doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<RhinoObject>())
          .Where(obj => PartNumberOf(obj) == partNumber)
          .ToList();
        if (!matchingMembers.Any(obj => IsSurfaceLike(obj.ObjectType)))
          continue;

        bool namedFlatGroup = GroupNameMatches(group.Name, FlatGroupPrefix, partNumber);
        bool hasEdgeDots = matchingMembers.Any(obj => obj.Attributes.Name == EdgeMateName);
        bool hasFlatLabel = matchingMembers.Any(obj => obj.Attributes.Name == TextObjectName);
        if (!namedFlatGroup && !hasEdgeDots)
          continue;

        int score = groupIndex * 1000 + (namedFlatGroup ? 100 : 0) +
                    (hasEdgeDots ? 10 : 0) + (hasFlatLabel ? 1 : 0);
        if (score <= bestScore)
          continue;

        bestScore = score;
        best = new PriorFlatOutput(groupIndex, matchingMembers.Select(obj => obj.Id));
      }

      return best;
    }

    private static bool TryRestorePriorFlatPlacement(
      RhinoDoc doc,
      PriorFlatOutput priorOutput,
      IEnumerable<Guid> outputIds,
      out string method)
    {
      method = "none";
      var oldIds = priorOutput.MemberIds
        .Where(id => doc.Objects.FindId(id) != null)
        .ToList();
      var newIds = Unique(outputIds)
        .Where(id => doc.Objects.FindId(id) != null)
        .ToList();
      if (oldIds.Count == 0 || newIds.Count == 0)
        return false;

      if (TryMarkerPlacementTransform(doc, oldIds, newIds, out var transform, out int markerCount))
      {
        TransformObjects(doc, newIds, transform);
        method = $"markers:{markerCount}";
        return true;
      }

      if (TryTextPlacementTransform(doc, oldIds, newIds, out transform))
      {
        TransformObjects(doc, newIds, transform);
        method = "text";
        return true;
      }

      if (TryGeometryPlacementTransform(doc, oldIds, newIds, out transform))
      {
        TransformObjects(doc, newIds, transform);
        method = "geometry";
        return true;
      }

      var oldBox = BoundingBoxOfObjects(doc, oldIds);
      var newBox = BoundingBoxOfObjects(doc, newIds);
      if (!oldBox.HasValue || !oldBox.Value.IsValid ||
          !newBox.HasValue || !newBox.Value.IsValid)
        return false;

      TransformObjects(doc, newIds,
        Transform.Translation(oldBox.Value.Center - newBox.Value.Center));
      method = "center";
      return true;
    }

    private static bool TryMarkerPlacementTransform(
      RhinoDoc doc,
      IReadOnlyList<Guid> oldIds,
      IReadOnlyList<Guid> newIds,
      out Transform transform,
      out int markerCount)
    {
      transform = Transform.Identity;
      markerCount = 0;
      var oldMarkers = PlacementMarkers(doc, oldIds);
      var newMarkers = PlacementMarkers(doc, newIds);
      var pairs = oldMarkers.Keys
        .Where(newMarkers.ContainsKey)
        .Select(key => (Old: oldMarkers[key], New: newMarkers[key]))
        .ToList();
      markerCount = pairs.Count;
      if (pairs.Count < 2)
        return false;

      var oldCenter = new Point3d(
        pairs.Average(pair => pair.Old.X),
        pairs.Average(pair => pair.Old.Y),
        pairs.Average(pair => pair.Old.Z));
      var newCenter = new Point3d(
        pairs.Average(pair => pair.New.X),
        pairs.Average(pair => pair.New.Y),
        pairs.Average(pair => pair.New.Z));

      double dot = 0.0;
      double cross = 0.0;
      foreach (var pair in pairs)
      {
        double sx = pair.New.X - newCenter.X;
        double sy = pair.New.Y - newCenter.Y;
        double tx = pair.Old.X - oldCenter.X;
        double ty = pair.Old.Y - oldCenter.Y;
        dot += sx * tx + sy * ty;
        cross += sx * ty - sy * tx;
      }

      if (Math.Abs(dot) + Math.Abs(cross) <= RhinoMath.ZeroTolerance)
        return false;

      double angle = Math.Atan2(cross, dot);
      transform = Transform.Translation(oldCenter - newCenter) *
                  Transform.Rotation(angle, Vector3d.ZAxis, newCenter);
      return transform.IsValid;
    }

    private static Dictionary<string, Point3d> PlacementMarkers(
      RhinoDoc doc,
      IEnumerable<Guid> ids)
    {
      var markers = new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);
      foreach (var id in ids)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        if (obj.Attributes.Name == EdgeMateName && obj.Geometry is TextDot edgeDot)
        {
          string mateId = obj.Attributes.GetUserString(EdgeMateIdKey) ?? edgeDot.Text ?? string.Empty;
          if (!string.IsNullOrWhiteSpace(mateId))
            markers[$"edge:{mateId}"] = edgeDot.Point;
        }
        else if (obj.Attributes.Name == TextObjectName)
        {
          if (obj.Geometry is TextEntity text)
            markers["label"] = text.Plane.Origin;
          else if (obj.Geometry is TextDot labelDot)
            markers["label"] = labelDot.Point;
        }
      }
      return markers;
    }

    private static bool TryTextPlacementTransform(
      RhinoDoc doc,
      IEnumerable<Guid> oldIds,
      IEnumerable<Guid> newIds,
      out Transform transform)
    {
      transform = Transform.Identity;
      var oldText = oldIds
        .Select(doc.Objects.FindId)
        .FirstOrDefault(obj => obj?.Attributes.Name == TextObjectName && obj.Geometry is TextEntity)
        ?.Geometry as TextEntity;
      var newText = newIds
        .Select(doc.Objects.FindId)
        .FirstOrDefault(obj => obj?.Attributes.Name == TextObjectName && obj.Geometry is TextEntity)
        ?.Geometry as TextEntity;
      if (oldText == null || newText == null ||
          !TryPlanarAngle(newText.Plane.YAxis, oldText.Plane.YAxis, out double angle))
        return false;

      transform = Transform.Translation(oldText.Plane.Origin - newText.Plane.Origin) *
                  Transform.Rotation(angle, Vector3d.ZAxis, newText.Plane.Origin);
      return transform.IsValid;
    }

    private static bool TryGeometryPlacementTransform(
      RhinoDoc doc,
      IReadOnlyList<Guid> oldIds,
      IReadOnlyList<Guid> newIds,
      out Transform transform)
    {
      transform = Transform.Identity;
      if (!TryGeometryPlacementFrame(doc, oldIds, out var oldAnchor, out var oldDirection, out var oldCenter) ||
          !TryGeometryPlacementFrame(doc, newIds, out var newAnchor, out var newDirection, out var newCenter) ||
          !TryPlanarAngle(newDirection, oldDirection, out double angle))
        return false;

      var oldRadial = oldCenter - oldAnchor;
      var newRadial = newCenter - newAnchor;
      oldRadial.Z = 0.0;
      newRadial.Z = 0.0;
      if (oldRadial.Unitize() && newRadial.Unitize())
      {
        var rotated = newRadial;
        rotated.Rotate(angle, Vector3d.ZAxis);
        var reversed = -rotated;
        if (reversed * oldRadial > rotated * oldRadial)
          angle += Math.PI;
      }

      transform = Transform.Translation(oldAnchor - newAnchor) *
                  Transform.Rotation(angle, Vector3d.ZAxis, newAnchor);
      return transform.IsValid;
    }

    private static bool TryGeometryPlacementFrame(
      RhinoDoc doc,
      IReadOnlyList<Guid> ids,
      out Point3d anchor,
      out Vector3d direction,
      out Point3d center)
    {
      anchor = Point3d.Unset;
      direction = Vector3d.Unset;
      center = Point3d.Unset;
      Curve? longest = null;
      double longestLength = 0.0;
      foreach (var id in ids)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null || !IsSurfaceLike(obj.ObjectType))
          continue;
        var brep = BrepFromGeometry(obj.Geometry);
        if (brep == null)
          continue;
        foreach (var edge in brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
        {
          double length = edge.GetLength();
          if (length > longestLength)
          {
            longestLength = length;
            longest = edge;
          }
        }
      }

      var box = BoundingBoxOfObjects(doc, ids);
      if (longest == null || !box.HasValue || !box.Value.IsValid)
        return false;

      double parameter = longest.Domain.Mid;
      longest.LengthParameter(longestLength * 0.5, out parameter);
      anchor = longest.PointAt(parameter);
      direction = longest.TangentAt(parameter);
      direction.Z = 0.0;
      center = box.Value.Center;
      return anchor.IsValid && direction.Unitize() && center.IsValid;
    }

    private static bool TryPlanarAngle(Vector3d from, Vector3d to, out double angle)
    {
      angle = 0.0;
      from.Z = 0.0;
      to.Z = 0.0;
      if (!from.Unitize() || !to.Unitize())
        return false;
      angle = Math.Atan2(from.X * to.Y - from.Y * to.X, from.X * to.X + from.Y * to.Y);
      return true;
    }

    private static bool GroupNameMatches(string? name, string prefix, int partNumber)
    {
      if (string.IsNullOrWhiteSpace(name))
        return false;
      string baseName = $"{prefix}_{partNumber}";
      return string.Equals(name, baseName, StringComparison.Ordinal) ||
             name.StartsWith(baseName + "_", StringComparison.Ordinal);
    }

    private static int FindOriginalGroupIndex(RhinoDoc doc, Guid sourceId, int partNumber)
    {
      var source = doc.Objects.FindId(sourceId);
      foreach (int groupIndex in source?.Attributes.GetGroupList() ?? Array.Empty<int>())
      {
        var group = doc.Groups.FindIndex(groupIndex);
        if (group != null && !group.IsDeleted && GroupNameMatches(group.Name, OriginalGroupPrefix, partNumber))
          return groupIndex;
      }
      return -1;
    }

    private static void EnsureOriginalLabel(
      RhinoDoc doc,
      Guid sourceId,
      int partNumber,
      string display,
      LabelFrame frame,
      bool addText,
      bool addDots,
      bool transferProperties)
    {
      int groupIndex = FindOriginalGroupIndex(doc, sourceId, partNumber);
      if (groupIndex >= 0)
      {
        bool hasLabel = (doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<RhinoObject>())
          .Any(obj => obj.Attributes.Name == TextObjectName && PartNumberOf(obj) == partNumber);
        if (hasLabel)
        {
          Dbg($"part={partNumber} original_group reused group={groupIndex} label=reused");
          return;
        }
      }

      Guid labelId = Guid.Empty;
      if (addText)
        labelId = AddFlatText(doc, display, frame.Point, frame.Y, frame.Normal, frame.Height, sourceId, transferProperties);
      else if (addDots)
        labelId = AddDot(doc, display, frame.Point, sourceId, transferProperties);
      if (!IsValidId(labelId))
        return;

      if (groupIndex >= 0)
      {
        SetMatchNumber(doc, new[] { sourceId, labelId }, partNumber);
        doc.Groups.AddToGroup(groupIndex, labelId);
        Dbg($"part={partNumber} original_group reused group={groupIndex} label=created");
      }
      else
      {
        GroupObjects(doc, new[] { sourceId, labelId }, OriginalGroupPrefix, partNumber);
      }
    }

    private static void ReplacePriorFlatGroup(
      RhinoDoc doc,
      PriorFlatOutput priorOutput,
      IEnumerable<Guid> outputIds,
      int partNumber)
    {
      var newIds = Unique(outputIds).Where(IsValidId).ToList();
      SetMatchNumber(doc, newIds, partNumber);
      doc.Groups.AddToGroup(priorOutput.GroupIndex, newIds);
      bool allGrouped = newIds.All(id =>
        (doc.Objects.FindId(id)?.Attributes.GetGroupList() ?? Array.Empty<int>())
          .Contains(priorOutput.GroupIndex));
      if (!allGrouped)
      {
        GroupObjects(doc, newIds, FlatGroupPrefix, partNumber);
        Dbg($"part={partNumber} flat_group reuse failed group={priorOutput.GroupIndex}");
        return;
      }

      int removed = 0;
      foreach (var oldId in priorOutput.MemberIds)
      {
        if (newIds.Contains(oldId) || doc.Objects.FindId(oldId) == null)
          continue;
        if (doc.Objects.Delete(oldId, true))
          removed++;
      }
      Dbg($"part={partNumber} flat_group reused group={priorOutput.GroupIndex}" +
          $" removed={removed} added={newIds.Count}");
    }

    private static string UniqueGroupName(RhinoDoc doc, string prefix, object? number)
    {
      string baseName = $"{prefix}_{BaseNumber(number)}";
      string name = baseName;
      int index = 2;
      while (doc.Groups.FindName(name) != null)
        name = $"{baseName}_{index++}";
      return name;
    }

    private static int GroupObjects(RhinoDoc doc, IEnumerable<Guid> ids, string prefix, object? number)
    {
      var list = Unique(ids).Where(IsValidId).ToList();
      if (list.Count == 0)
        return -1;
      SetMatchNumber(doc, list, number);
      int groupIndex = doc.Groups.Add(UniqueGroupName(doc, prefix, number));
      if (groupIndex >= 0)
      {
        foreach (var id in list)
          doc.Groups.AddToGroup(groupIndex, id);
      }
      return groupIndex;
    }

    private static List<FollowingItem> MakeFollowingItems(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var items = new List<FollowingItem>();
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        if (obj.ObjectType == ObjectType.Curve && obj.Geometry is Curve curve)
          items.Add(new FollowingItem(id, FollowingKind.Curve, curve));
        else if (obj.ObjectType == ObjectType.Point && obj.Geometry is Point point)
          items.Add(new FollowingItem(id, FollowingKind.Point, point));
        else if (obj.ObjectType == ObjectType.TextDot && obj.Geometry is TextDot dot)
          items.Add(new FollowingItem(id, FollowingKind.Dot, dot));
      }
      return items;
    }

    private static AssignmentResult AssignFollowing(RhinoDoc doc, List<FollowingItem> items, List<SourceSurface> surfaces)
    {
      var result = new AssignmentResult(surfaces.Count);
      foreach (var item in items)
      {
        int bestIndex = -1;
        Tuple<double, double>? bestScore = null;
        double bestLimit = 0.0;

        for (int i = 0; i < surfaces.Count; i++)
        {
          Tuple<double, double>? score = item.Kind == FollowingKind.Curve
            ? (item.Geometry is Curve curve ? CurveScore(surfaces[i].Brep, curve) : null)
            : PointScore(surfaces[i].Brep, FollowingPoint(item));

          if (score == null)
            continue;
          if (bestScore == null || CompareScore(score, bestScore) < 0)
          {
            bestIndex = i;
            bestScore = score;
            bestLimit = AssignTolerance(doc, surfaces[i].Geometry, item.Geometry);
          }
        }

        if (bestIndex < 0 || bestScore == null || bestScore.Item1 > bestLimit)
        {
          result.Skipped++;
          result.SkippedIds.Add(item.Id);
          continue;
        }

        var assigned = item;
        if (item.Kind == FollowingKind.Point || item.Kind == FollowingKind.Dot)
        {
          var p = FollowingPoint(item);
          if (p.HasValue)
          {
            try
            {
              var cp = surfaces[bestIndex].Brep.ClosestPoint(p.Value);
              assigned = item.Kind == FollowingKind.Point
                ? new FollowingItem(item.Id, item.Kind, new Point(cp))
                : new FollowingItem(item.Id, item.Kind, DuplicateDotAt((TextDot)item.Geometry, cp));
            }
            catch { }
          }
        }

        result.Buckets[bestIndex].Add(assigned);
      }
      return result;
    }

    private static int CompareScore(Tuple<double, double> a, Tuple<double, double> b)
    {
      int primary = a.Item1.CompareTo(b.Item1);
      return primary != 0 ? primary : a.Item2.CompareTo(b.Item2);
    }

    private static double AssignTolerance(RhinoDoc doc, GeometryBase surfaceGeometry, GeometryBase followingGeometry)
    {
      return Math.Max(doc.ModelAbsoluteTolerance * FollowingTolFactor,
        Math.Max(GeometryDiagonal(surfaceGeometry), GeometryDiagonal(followingGeometry)) * FollowingDiagFactor);
    }

    private static double GeometryDiagonal(GeometryBase geometry)
    {
      if (geometry == null)
        return 0.0;
      var bbox = geometry.GetBoundingBox(true);
      return bbox.IsValid ? bbox.Diagonal.Length : 0.0;
    }

    private static Point3d? FollowingPoint(FollowingItem item)
    {
      if (item.Geometry is Point point)
        return point.Location;
      if (item.Geometry is TextDot dot)
        return dot.Point;
      return null;
    }

    private static Tuple<double, double>? PointScore(Brep brep, Point3d? point)
    {
      if (brep == null || !point.HasValue)
        return null;
      try
      {
        double d = point.Value.DistanceTo(brep.ClosestPoint(point.Value));
        return Tuple.Create(d, d);
      }
      catch { return null; }
    }

    private static Tuple<double, double>? CurveScore(Brep brep, Curve? curve)
    {
      if (brep == null || curve == null)
        return null;
      var distances = new List<double>();
      foreach (var p in CurveSamples(curve, FollowingCurveSamples))
      {
        try { distances.Add(p.DistanceTo(brep.ClosestPoint(p))); }
        catch { return null; }
      }
      return distances.Count > 0 ? Tuple.Create(distances.Max(), distances.Average()) : null;
    }

    private static TextDot DuplicateDotAt(TextDot? dot, Point3d point)
    {
      return new TextDot(dot?.Text ?? string.Empty, point);
    }

    private static string LabelHelperDotText(Guid sourceId, int partNumber, string kind)
    {
      return $"{LabelHelperDotPrefix}{partNumber}:{sourceId:N}:{kind}";
    }

    private static string EdgeMateHelperDotText(Guid sourceId, EdgeMateRecord record)
    {
      return $"{EdgeMateHelperDotPrefix}{sourceId:N}:{record.MateId}:" +
             $"{record.EdgeIndex}:{record.MatePartIndex}";
    }

    private static double? AngleToPageUp(Vector3d vector, double tol)
    {
      var v = new Vector3d(vector.X, vector.Y, 0.0);
      if (VectorLength(v) <= tol)
        return null;
      v.Unitize();
      var target = new Vector3d(0.0, 1.0, 0.0);
      return Math.Atan2(v.X * target.Y - v.Y * target.X, v.X * target.X + v.Y * target.Y);
    }

    private static void RotateObjectsToTextUp(RhinoDoc doc, IEnumerable<Guid> ids, Point3d center, Vector3d textUp)
    {
      var angle = AngleToPageUp(textUp, doc.ModelAbsoluteTolerance);
      if (!angle.HasValue || Math.Abs(angle.Value) <= 1.0e-9)
        return;
      TransformObjects(doc, ids, Transform.Rotation(angle.Value, Vector3d.ZAxis, center));
    }

    private static bool TryGetSingleFacePlanarTransform(Brep brep, double tolerance, out Transform transform)
    {
      transform = Transform.Identity;
      if (brep == null || brep.Faces.Count != 1 ||
          !brep.Faces[0].TryGetPlane(out var plane, tolerance) || !plane.IsValid)
        return false;

      if (brep.Faces[0].OrientationIsReversed)
        plane.Flip();

      transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
      return transform.IsValid;
    }

    private static bool TryPerformRuledUvUnroll(
      Brep sourceBrep,
      IReadOnlyList<Curve> curves,
      IReadOnlyList<Point> points,
      IReadOnlyList<TextDot> dots,
      double tolerance,
      out Brep[] unrolledBreps,
      out Curve[] unrolledCurves,
      out Point3d[] unrolledPoints,
      out TextDot[] unrolledDots,
      out string details)
    {
      unrolledBreps = Array.Empty<Brep>();
      unrolledCurves = Array.Empty<Curve>();
      unrolledPoints = Array.Empty<Point3d>();
      unrolledDots = Array.Empty<TextDot>();
      details = string.Empty;

      if (sourceBrep == null || sourceBrep.Faces.Count != 1)
        return false;

      var sourceFace = sourceBrep.Faces[0];
      var sourceSurface = sourceFace.UnderlyingSurface().ToNurbsSurface();
      if (sourceSurface == null ||
          !TryFlattenRuledControlNet(sourceSurface, tolerance, out var flatSurface, out int linearDirection))
        return false;

      Brep? flatBrep = sourceBrep.IsSurface
        ? Brep.CreateFromSurface(flatSurface)
        : Brep.CreateTrimmedSurface(sourceFace, flatSurface, tolerance);
      if (flatBrep == null || !flatBrep.IsValid || flatBrep.Faces.Count != 1)
        return false;

      if (flatBrep.Faces[0].OrientationIsReversed != sourceFace.OrientationIsReversed)
        flatBrep.Flip();

      var mappedCurves = new List<Curve>(curves.Count);
      foreach (var curve in curves)
      {
        var uvCurve = sourceFace.Pullback(curve, tolerance);
        if (uvCurve == null)
          return false;
        var mapped = flatSurface.Pushup(uvCurve, tolerance);
        if (mapped == null || !mapped.IsValid)
          return false;
        mappedCurves.Add(mapped);
      }

      var mappedPoints = new List<Point3d>(points.Count);
      foreach (var point in points)
      {
        if (!TryMapUvPoint(sourceFace, flatSurface, point.Location, out var mapped))
          return false;
        mappedPoints.Add(mapped);
      }

      var mappedDots = new List<TextDot>(dots.Count);
      foreach (var dot in dots)
      {
        if (!TryMapUvPoint(sourceFace, flatSurface, dot.Point, out var mapped))
          return false;
        var copy = dot.Duplicate() as TextDot ?? new TextDot(dot.Text ?? string.Empty, mapped);
        copy.Point = mapped;
        mappedDots.Add(copy);
      }

      unrolledBreps = new[] { flatBrep };
      unrolledCurves = mappedCurves.ToArray();
      unrolledPoints = mappedPoints.ToArray();
      unrolledDots = mappedDots.ToArray();
      details = $"linear={(linearDirection == 0 ? "U" : "V")}" +
                $" cvs={sourceSurface.Points.CountU}x{sourceSurface.Points.CountV}";
      return true;
    }

    private static bool TryFlattenRuledControlNet(
      NurbsSurface source,
      double tolerance,
      out NurbsSurface flat,
      out int linearDirection)
    {
      flat = null!;
      linearDirection = -1;

      if (source.OrderU == 2 && source.Points.CountU == 2)
        linearDirection = 0;
      else if (source.OrderV == 2 && source.Points.CountV == 2)
        linearDirection = 1;
      else
        return false;

      int curvedCount = linearDirection == 0 ? source.Points.CountV : source.Points.CountU;
      if (curvedCount < 2)
        return false;

      // Kinks in the curved direction can't be flattened correctly by the circle-intersection method;
      // fall back to Rhino's native unroller which handles them accurately.
      int curvedDir = linearDirection == 0 ? 1 : 0;
      var curvedDomain = source.Domain(curvedDir);
      if (source.GetNextDiscontinuity(curvedDir, Continuity.G1_locus_continuous,
          curvedDomain.T0, curvedDomain.T1, out _))
        return false;

      var sourcePoints = new Point3d[curvedCount, 2];
      var weights = new double[curvedCount, 2];
      for (int i = 0; i < curvedCount; i++)
      {
        for (int side = 0; side < 2; side++)
        {
          int u = linearDirection == 0 ? side : i;
          int v = linearDirection == 0 ? i : side;
          var controlPoint = source.Points.GetControlPoint(u, v);
          sourcePoints[i, side] = controlPoint.Location;
          weights[i, side] = controlPoint.Weight;
          if (!sourcePoints[i, side].IsValid || !RhinoMath.IsValidDouble(weights[i, side]))
            return false;
        }
      }

      double firstWidth = sourcePoints[0, 0].DistanceTo(sourcePoints[0, 1]);
      if (firstWidth <= Math.Max(tolerance, RhinoMath.ZeroTolerance))
        return false;

      var flatPoints = new Point2d[curvedCount, 2];
      flatPoints[0, 0] = Point2d.Origin;
      flatPoints[0, 1] = new Point2d(firstWidth, 0.0);

      for (int i = 1; i < curvedCount; i++)
      {
        var previousA = flatPoints[i - 1, 0];
        var previousB = flatPoints[i - 1, 1];
        if (!TryCircleIntersections(
              previousA,
              sourcePoints[i - 1, 0].DistanceTo(sourcePoints[i, 0]),
              previousB,
              sourcePoints[i - 1, 1].DistanceTo(sourcePoints[i, 0]),
              tolerance,
              out var a0,
              out var a1))
          return false;

        Point2d currentA;
        if (i == 1)
          currentA = linearDirection == 0
            ? (a0.Y >= a1.Y ? a0 : a1)
            : (a0.Y <= a1.Y ? a0 : a1);
        else
          currentA = NearestPoint(a0, a1, Extrapolate(flatPoints[i - 2, 0], previousA));
        flatPoints[i, 0] = currentA;

        if (!TryCircleIntersections(
              currentA,
              sourcePoints[i, 0].DistanceTo(sourcePoints[i, 1]),
              previousB,
              sourcePoints[i - 1, 1].DistanceTo(sourcePoints[i, 1]),
              tolerance,
              out var b0,
              out var b1))
          return false;

        var translatedPrediction = Add(previousB, Subtract(currentA, previousA));
        var prediction = i == 1
          ? translatedPrediction
          : Midpoint(translatedPrediction, Extrapolate(flatPoints[i - 2, 1], previousB));
        flatPoints[i, 1] = NearestPoint(b0, b1, prediction);
      }

      flat = NurbsSurface.Create(
        3,
        source.IsRational,
        source.OrderU,
        source.OrderV,
        source.Points.CountU,
        source.Points.CountV);
      if (flat == null)
        return false;

      for (int i = 0; i < source.KnotsU.Count; i++)
        flat.KnotsU[i] = source.KnotsU[i];
      for (int i = 0; i < source.KnotsV.Count; i++)
        flat.KnotsV[i] = source.KnotsV[i];

      for (int i = 0; i < curvedCount; i++)
      {
        for (int side = 0; side < 2; side++)
        {
          int u = linearDirection == 0 ? side : i;
          int v = linearDirection == 0 ? i : side;
          var point = flatPoints[i, side];
          if (!flat.Points.SetControlPoint(
                u, v, new ControlPoint(new Point3d(point.X, point.Y, 0.0), weights[i, side])))
            return false;
        }
      }

      return flat.IsValid;
    }

    private static bool TryMapUvPoint(
      BrepFace sourceFace,
      Surface flatSurface,
      Point3d sourcePoint,
      out Point3d mappedPoint)
    {
      mappedPoint = Point3d.Unset;
      if (!sourceFace.ClosestPoint(sourcePoint, out double u, out double v))
        return false;
      mappedPoint = flatSurface.PointAt(u, v);
      return mappedPoint.IsValid;
    }

    private static bool TryCircleIntersections(
      Point2d center0,
      double radius0,
      Point2d center1,
      double radius1,
      double tolerance,
      out Point2d point0,
      out Point2d point1)
    {
      point0 = Point2d.Unset;
      point1 = Point2d.Unset;
      double dx = center1.X - center0.X;
      double dy = center1.Y - center0.Y;
      double distance = Math.Sqrt(dx * dx + dy * dy);
      if (distance <= Math.Max(tolerance, RhinoMath.ZeroTolerance) || radius0 < 0.0 || radius1 < 0.0)
        return false;

      double along = (radius0 * radius0 - radius1 * radius1 + distance * distance) / (2.0 * distance);
      double heightSquared = radius0 * radius0 - along * along;
      double scale = Math.Max(Math.Max(radius0, radius1), distance);
      if (heightSquared < -Math.Max(tolerance * tolerance, scale * scale * 1.0e-12))
        return false;
      double height = Math.Sqrt(Math.Max(0.0, heightSquared));
      double ux = dx / distance;
      double uy = dy / distance;
      double baseX = center0.X + along * ux;
      double baseY = center0.Y + along * uy;
      point0 = new Point2d(baseX - height * uy, baseY + height * ux);
      point1 = new Point2d(baseX + height * uy, baseY - height * ux);
      return point0.IsValid && point1.IsValid;
    }

    private static Point2d NearestPoint(Point2d a, Point2d b, Point2d target)
    {
      return DistanceSquared(a, target) <= DistanceSquared(b, target) ? a : b;
    }

    private static double DistanceSquared(Point2d a, Point2d b)
    {
      double dx = a.X - b.X;
      double dy = a.Y - b.Y;
      return dx * dx + dy * dy;
    }

    private static Point2d Add(Point2d point, Vector2d vector)
    {
      return new Point2d(point.X + vector.X, point.Y + vector.Y);
    }

    private static Vector2d Subtract(Point2d a, Point2d b)
    {
      return new Vector2d(a.X - b.X, a.Y - b.Y);
    }

    private static Point2d Midpoint(Point2d a, Point2d b)
    {
      return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
    }

    private static Point2d Extrapolate(Point2d before, Point2d current)
    {
      return new Point2d(current.X * 2.0 - before.X, current.Y * 2.0 - before.Y);
    }

    private static Curve TransformCurveCopy(Curve curve, Transform transform)
    {
      var copy = curve.DuplicateCurve();
      copy.Transform(transform);
      return copy;
    }

    private static Point3d TransformPoint(Point3d point, Transform transform)
    {
      point.Transform(transform);
      return point;
    }

    private static TextDot TransformTextDotCopy(TextDot dot, Transform transform)
    {
      var copy = dot.Duplicate() as TextDot ?? new TextDot(dot.Text ?? string.Empty, dot.Point);
      copy.Transform(transform);
      return copy;
    }

    private static void TransformObjects(RhinoDoc doc, IEnumerable<Guid> ids, Transform transform)
    {
      foreach (var id in Unique(ids))
        doc.Objects.Transform(id, transform, true);
    }

    private static BoundingBox? BoundingBoxOfObjects(RhinoDoc doc, IEnumerable<Guid> ids)
    {
      var bbox = BoundingBox.Empty;
      bool hasBox = false;
      foreach (var id in Unique(ids))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;
        var b = obj.Geometry.GetBoundingBox(true);
        if (!b.IsValid)
          continue;
        if (!hasBox)
        {
          bbox = b;
          hasBox = true;
        }
        else
          bbox.Union(b);
      }
      return hasBox ? bbox : (BoundingBox?)null;
    }

    // ── Per-part label height ──────────────────────────────────────────────────
    private static double ItemTextHeight(RhinoDoc doc, Guid objId, string display, Brep? brepHint = null)
    {
      double tol = doc.ModelAbsoluteTolerance;
      double baseH = HeightCandidate(doc, objId, brepHint);
      double height = Math.Max(baseH, tol * 8.0);
      var caps = new List<double>();

      // Edge cap: shortest meaningful naked edge * 0.50
      var brep = brepHint ?? BrepFromGeometry(doc.Objects.FindId(objId)?.Geometry);
      if (brep != null)
      {
        var lengths = brep.DuplicateEdgeCurves(true)?
          .Where(c => c != null)
          .Select(c => c!.GetLength())
          .Where(l => l > tol)
          .ToList() ?? new List<double>();
        if (lengths.Count > 0)
        {
          double longest = lengths.Max();
          double minMean = Math.Max(tol * 20.0, longest * 0.08);
          var meaningful = lengths.Where(l => l >= minMean).ToList();
          if (meaningful.Count > 0) caps.Add(meaningful.Min() * 0.50);
        }
      }

      // Span caps: x_span * 0.45 / width_factor and y_span * 0.28
      var pt = LabelPoint(doc, objId, brepHint);
      var pts = BoundaryPoints(doc, objId, brepHint);
      if (pt.HasValue && pts.Count > 0)
      {
        var frame = SurfaceLabelFrame(doc, objId, 1.0, brepHint);
        if (frame != null)
        {
          double wf = Math.Max(1.0, display.Length * 0.65);
          var xs = pts.Select(p => (p - pt.Value) * frame.X);
          var ys = pts.Select(p => (p - pt.Value) * frame.Y);
          double xSpan = CenteredSpan(xs);
          double ySpan = CenteredSpan(ys);
          if (xSpan > tol) caps.Add(xSpan * 0.45 / wf);
          if (ySpan > tol) caps.Add(ySpan * 0.28);
        }
      }

      if (caps.Count > 0) height = Math.Min(height, caps.Min());
      return Math.Max(height, tol * 8.0) * TextHeightScale;
    }

    // ── Orientation curve resolution ───────────────────────────────────────────
    // ── Edge mate data types ───────────────────────────────────────────────────
    private class EdgeMateRecord
    {
      public string  MateId        = "";
      public int     EdgeIndex;
      public Curve?  Curve;
      public Point3d Marker;
      public int     MatePartIndex;
      public int     MatePartNumber;
      public int     MateEdgeIndex;
      public bool    Reversed;
    }

    private class EdgePairResult
    {
      public double  MaxDist;
      public double  AvgDist;
      public bool    Reversed;
      public bool    Full;        // edge lengths are equal within matching tolerance
      public bool    ShortFirst;  // ea is the shorter curve
      public Point3d Point1;
      public Point3d Point2;
    }

    // ── Edge mate system ───────────────────────────────────────────────────────
    private class ExistingEdgeMate
    {
      public string MateId = "";
      public int PartNumber;
      public int MatePartNumber;
      public int? EdgeIndex;
      public int? MateEdgeIndex;
      public bool Reversed;
      public uint RuntimeSerialNumber;
    }

    private static List<ExistingEdgeMate> ExistingEdgeMates(RhinoDoc doc)
    {
      var result = new List<ExistingEdgeMate>();
      foreach (var obj in doc.Objects)
      {
        if (obj.Attributes.Name != EdgeMateName)
          continue;

        string mateId = obj.Attributes.GetUserString(EdgeMateIdKey) ?? "";
        if (string.IsNullOrWhiteSpace(mateId) && obj.Geometry is TextDot dot)
          mateId = dot.Text ?? "";
        if (string.IsNullOrWhiteSpace(mateId) ||
            !TryAttributeInt(obj.Attributes, EdgePartNumKey, out int partNumber) ||
            !TryAttributeInt(obj.Attributes, EdgeMatePartNumKey, out int matePartNumber))
          continue;

        result.Add(new ExistingEdgeMate
        {
          MateId = mateId,
          PartNumber = partNumber,
          MatePartNumber = matePartNumber,
          EdgeIndex = TryAttributeInt(obj.Attributes, EdgeIndexKey, out int edgeIndex) ? edgeIndex : (int?)null,
          MateEdgeIndex = TryAttributeInt(obj.Attributes, MateEdgeIndexKey, out int mateEdgeIndex) ? mateEdgeIndex : (int?)null,
          Reversed = string.Equals(obj.Attributes.GetUserString(EdgeMateReversedKey), "true", StringComparison.OrdinalIgnoreCase),
          RuntimeSerialNumber = obj.RuntimeSerialNumber
        });
      }
      return result;
    }

    private static bool TryAttributeInt(ObjectAttributes attributes, string key, out int value)
    {
      return int.TryParse(attributes.GetUserString(key), out value);
    }

    private static int MateSequence(string mateId)
    {
      if (mateId.StartsWith(EdgeMatePrefix, StringComparison.OrdinalIgnoreCase) &&
          int.TryParse(mateId.Substring(EdgeMatePrefix.Length), out int sequence))
        return sequence;
      return 0;
    }

    private static string? ReusableMateId(
      IEnumerable<ExistingEdgeMate> existing,
      int partNumber,
      int edgeIndex,
      int matePartNumber,
      int mateEdgeIndex,
      HashSet<string> usedMateIds)
    {
      return existing
        .Where(mate => !usedMateIds.Contains(mate.MateId) &&
          ((mate.PartNumber == partNumber && mate.MatePartNumber == matePartNumber) ||
           (mate.PartNumber == matePartNumber && mate.MatePartNumber == partNumber)))
        .GroupBy(mate => mate.MateId, StringComparer.OrdinalIgnoreCase)
        .Select(group => new
        {
          MateId = group.Key,
          Exact = group.Any(mate =>
            mate.PartNumber == partNumber && mate.MatePartNumber == matePartNumber &&
            mate.EdgeIndex == edgeIndex && mate.MateEdgeIndex == mateEdgeIndex) ||
            group.Any(mate =>
              mate.PartNumber == matePartNumber && mate.MatePartNumber == partNumber &&
              mate.EdgeIndex == mateEdgeIndex && mate.MateEdgeIndex == edgeIndex),
          Serial = group.Max(mate => mate.RuntimeSerialNumber)
        })
        .OrderByDescending(candidate => candidate.Exact)
        .ThenByDescending(candidate => candidate.Serial)
        .ThenBy(candidate => MateSequence(candidate.MateId))
        .Select(candidate => candidate.MateId)
        .FirstOrDefault();
    }

    private static ExistingEdgeMate NormalizeMateForPart(ExistingEdgeMate mate, int partNumber)
    {
      if (mate.PartNumber == partNumber)
        return mate;
      return new ExistingEdgeMate
      {
        MateId = mate.MateId,
        PartNumber = partNumber,
        MatePartNumber = mate.PartNumber,
        EdgeIndex = mate.MateEdgeIndex,
        MateEdgeIndex = mate.EdgeIndex,
        Reversed = !mate.Reversed,
        RuntimeSerialNumber = mate.RuntimeSerialNumber
      };
    }

    private static void RecoverExistingMatesFromUnselectedParts(
      RhinoDoc doc,
      IReadOnlyList<SourceSurface> sources,
      IReadOnlyList<int> partNumbers,
      IReadOnlyList<(int idx, Curve c)[]> allEdges,
      IReadOnlyList<List<EdgeMateRecord>> result,
      IReadOnlyList<ExistingEdgeMate> existingMates,
      HashSet<string> usedMateIds,
      double tolerance)
    {
      var selectedPartNumbers = new HashSet<int>(partNumbers);
      var selectedIds = new HashSet<Guid>(sources.Select(source => source.Id));

      for (int i = 0; i < sources.Count; i++)
      {
        int partNumber = partNumbers[i];
        var priorMates = existingMates
          .Where(mate => mate.PartNumber == partNumber || mate.MatePartNumber == partNumber)
          .Select(mate => NormalizeMateForPart(mate, partNumber))
          .GroupBy(mate => mate.MateId, StringComparer.OrdinalIgnoreCase)
          .Select(group => group.OrderByDescending(mate => mate.RuntimeSerialNumber).First())
          .OrderBy(mate => MateSequence(mate.MateId))
          .ToList();

        foreach (var priorMate in priorMates)
        {
          if (usedMateIds.Contains(priorMate.MateId) ||
              selectedPartNumbers.Contains(priorMate.MatePartNumber))
            continue;

          var mateBrep = FindOriginalBrepForPart(doc, priorMate.MatePartNumber, selectedIds);
          if (mateBrep == null)
            continue;
          var mateEdges = (mateBrep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
            .Select((curve, index) => (idx: index, c: curve))
            .Where(item => item.c != null && item.c.GetLength() > tolerance)
            .ToArray();

          (int edgeIndex, Curve edge, int mateEdgeIndex, EdgePairResult score)? best = null;
          foreach (var sourceEdge in allEdges[i])
          {
            if (priorMate.EdgeIndex.HasValue && priorMate.EdgeIndex.Value != sourceEdge.idx)
              continue;
            foreach (var mateEdge in mateEdges)
            {
              if (priorMate.MateEdgeIndex.HasValue && priorMate.MateEdgeIndex.Value != mateEdge.idx)
                continue;
              var score = TestEdgePair(sourceEdge.c, mateEdge.c, tolerance);
              if (score == null)
                continue;
              if (!best.HasValue || score.MaxDist < best.Value.score.MaxDist ||
                  (Math.Abs(score.MaxDist - best.Value.score.MaxDist) <= RhinoMath.ZeroTolerance &&
                   score.AvgDist < best.Value.score.AvgDist))
                best = (sourceEdge.idx, sourceEdge.c, mateEdge.idx, score);
            }
          }

          if (!best.HasValue)
            continue;

          result[i].Add(new EdgeMateRecord
          {
            MateId = priorMate.MateId,
            EdgeIndex = best.Value.edgeIndex,
            Curve = best.Value.edge,
            Marker = best.Value.score.Point1,
            MatePartIndex = -priorMate.MatePartNumber - 1,
            MatePartNumber = priorMate.MatePartNumber,
            MateEdgeIndex = best.Value.mateEdgeIndex,
            Reversed = best.Value.score.Reversed
          });
          usedMateIds.Add(priorMate.MateId);
          Dbg($"edge_mate reuse id={priorMate.MateId} part={partNumber}" +
              $" edge={best.Value.edgeIndex} mate_part={priorMate.MatePartNumber}" +
              $" mate_edge={best.Value.mateEdgeIndex}");
        }
      }
    }

    private static Brep? FindOriginalBrepForPart(
      RhinoDoc doc, int partNumber, HashSet<Guid> excludedIds)
    {
      foreach (var obj in doc.Objects)
      {
        if (excludedIds.Contains(obj.Id) || PartNumberOf(obj) != partNumber ||
            !IsSurfaceLike(obj.ObjectType))
          continue;

        bool inOriginalGroup = (obj.Attributes.GetGroupList() ?? Array.Empty<int>())
          .Select(groupIndex => doc.Groups.FindIndex(groupIndex))
          .Any(group => group != null && !group.IsDeleted &&
                        GroupNameMatches(group.Name, OriginalGroupPrefix, partNumber));
        if (!inOriginalGroup)
          continue;

        var brep = BrepFromGeometry(obj.Geometry);
        if (brep != null)
          return brep;
      }
      return null;
    }

    private static List<List<EdgeMateRecord>> BuildEdgeMates(
      RhinoDoc doc, List<SourceSurface> sources, IReadOnlyList<int> partNumbers, double tol)
    {
      var result = new List<List<EdgeMateRecord>>(sources.Count);
      for (int i = 0; i < sources.Count; i++)
        result.Add(new List<EdgeMateRecord>());

      var existingMates = ExistingEdgeMates(doc);
      var usedMateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      int nextMateSequence = existingMates
        .Select(mate => MateSequence(mate.MateId))
        .DefaultIfEmpty(0)
        .Max() + 1;

      // Collect naked edges per source, filtering out degenerate ones
      var allEdges = sources
        .Select(s => (s.Brep.DuplicateEdgeCurves(true) ?? Array.Empty<Curve>())
          .Select((c, idx) => (idx, c))
          .Where(x => x.c != null && x.c.GetLength() > tol)
          .ToArray())
        .ToArray();

      // Build and score all candidate pairs
      var candidates = new List<(double maxD, double avgD, int i, int ei, int j, int ej, EdgePairResult score)>();
      for (int i = 0; i < sources.Count; i++)
        for (int j = i + 1; j < sources.Count; j++)
          foreach (var (ei, ci) in allEdges[i])
            foreach (var (ej, cj) in allEdges[j])
            {
              var score = TestEdgePair(ci, cj, tol);
              if (score != null)
                candidates.Add((score.MaxDist, score.AvgDist, i, ei, j, ej, score));
            }

      // Sort best matches first, then deduplicate using full/short tracking
      candidates.Sort((a, b) => {
        int c = a.maxD.CompareTo(b.maxD);
        return c != 0 ? c : a.avgD.CompareTo(b.avgD);
      });

      var usedFull  = new HashSet<(int, int)>();
      var usedShort = new HashSet<(int, int)>();
      foreach (var (_, __, i, ei, j, ej, score) in candidates)
      {
        var key1     = (i, ei);
        var key2     = (j, ej);
        var shortKey = score.ShortFirst ? key1 : key2;

        if (score.Full)
        {
          if (usedFull.Contains(key1) || usedFull.Contains(key2) ||
              usedShort.Contains(key1) || usedShort.Contains(key2)) continue;
          usedFull.Add(key1);
          usedFull.Add(key2);
        }
        else
        {
          if (usedFull.Contains(shortKey) || usedShort.Contains(shortKey)) continue;
          usedShort.Add(shortKey);
        }

        string? mateId = ReusableMateId(
          existingMates, partNumbers[i], ei, partNumbers[j], ej, usedMateIds);
        mateId ??= $"{EdgeMatePrefix}{nextMateSequence++:D2}";
        usedMateIds.Add(mateId);
        Dbg($"edge_mate choose id={mateId} part={partNumbers[i]} edge={ei}" +
            $" mate_part={partNumbers[j]} mate_edge={ej} max={score.MaxDist:G6}" +
            $" avg={score.AvgDist:G6} full={score.Full} reversed={score.Reversed}" +
            $" point1={P(score.Point1)} point2={P(score.Point2)}");
        var edgeCi = allEdges[i].First(x => x.idx == ei).c;
        var edgeCj = allEdges[j].First(x => x.idx == ej).c;
        result[i].Add(new EdgeMateRecord
        {
          MateId = mateId, EdgeIndex = ei, Curve = edgeCi, Marker = score.Point1,
          MatePartIndex = j, MatePartNumber = partNumbers[j], MateEdgeIndex = ej, Reversed = score.Reversed
        });
        result[j].Add(new EdgeMateRecord
        {
          MateId = mateId, EdgeIndex = ej, Curve = edgeCj, Marker = score.Point2,
          MatePartIndex = i, MatePartNumber = partNumbers[i], MateEdgeIndex = ei, Reversed = !score.Reversed
        });
      }

      RecoverExistingMatesFromUnselectedParts(
        doc, sources, partNumbers, allEdges, result, existingMates, usedMateIds, tol);
      return result;
    }

    /// <summary>
    /// Port of Python edge_pair_score: samples the shorter curve and projects each point
    /// onto the longer curve via ClosestPoint. Handles partial edge overlaps.
    /// </summary>
    private static EdgePairResult? TestEdgePair(Curve ea, Curve eb, double absTol)
    {
      if (ea == null || eb == null) return null;
      double lenA = ea.GetLength(); if (lenA <= absTol) return null;
      double lenB = eb.GetLength(); if (lenB <= absTol) return null;

      bool   shortFirst = lenA <= lenB;
      var    shortCrv   = shortFirst ? ea : eb;
      var    longCrv    = shortFirst ? eb : ea;
      double shortLen   = Math.Min(lenA, lenB);
      double longLen    = Math.Max(lenA, lenB);

      double matchTol = Math.Max(absTol * EdgeMateTolFactor,
                                 Math.Max(longLen, shortLen) * EdgeMateDiagFactor);
      bool full = Math.Abs(longLen - shortLen) <= matchTol * 2.0;

      var distances = new List<double>(EdgeMateSamples * 2);

      // Sample SHORT → project onto LONG
      for (int k = 0; k < EdgeMateSamples; k++)
      {
        var p = shortCrv.PointAt(shortCrv.Domain.ParameterAt((k + 0.5) / EdgeMateSamples));
        if (!longCrv.ClosestPoint(p, out double tl)) return null;
        distances.Add(p.DistanceTo(longCrv.PointAt(tl)));
      }
      // For full-length pairs, also sample LONG → project onto SHORT
      if (full)
      {
        for (int k = 0; k < EdgeMateSamples; k++)
        {
          var p = longCrv.PointAt(longCrv.Domain.ParameterAt((k + 0.5) / EdgeMateSamples));
          if (!shortCrv.ClosestPoint(p, out double ts)) return null;
          distances.Add(p.DistanceTo(shortCrv.PointAt(ts)));
        }
      }

      double maxD = distances.Max();
      double avgD = distances.Average();
      if (maxD > matchTol) return null;

      // Verify both endpoints of the short edge project onto the long edge
      foreach (var ep in new[] { shortCrv.PointAtStart, shortCrv.PointAtEnd })
      {
        if (!longCrv.ClosestPoint(ep, out double tl)) return null;
        if (ep.DistanceTo(longCrv.PointAt(tl)) > matchTol) return null;
      }

      // Marker: arc-length midpoint of short, closest point on long
      var shortMid = CurveMidpoint(shortCrv);
      longCrv.ClosestPoint(shortMid, out double tlMid);
      var longMid = longCrv.PointAt(tlMid);
      var pt1 = shortFirst ? shortMid : longMid;
      var pt2 = shortFirst ? longMid  : shortMid;

      // Reversed: compare tangent directions at the marker points
      bool reversed = false;
      if (ea.ClosestPoint(pt1, out double ta) && eb.ClosestPoint(pt2, out double tb))
      {
        var t1 = ea.TangentAt(ta);
        var t2 = eb.TangentAt(tb);
        if (!t1.IsZero && !t2.IsZero) reversed = t1 * t2 < 0.0;
      }

      return new EdgePairResult
      {
        MaxDist = maxD, AvgDist = avgD, Reversed = reversed,
        Full = full, ShortFirst = shortFirst,
        Point1 = pt1, Point2 = pt2
      };
    }

    private static Point3d CurveMidpoint(Curve c)
    {
      if (c.LengthParameter(c.GetLength() * 0.5, out double t))
        return c.PointAt(t);
      return c.PointAt(c.Domain.Mid);
    }

    private static IEnumerable<EdgeMateRecord> UniqueEdgeMateRecords(
      IEnumerable<EdgeMateRecord> records)
    {
      var seen = new HashSet<(string, int, int)>();
      foreach (var record in records)
      {
        if (seen.Add((record.MateId, record.EdgeIndex, record.MatePartIndex)))
          yield return record;
      }
    }

    private static Guid AddEdgeMateDot(
      RhinoDoc doc, EdgeMateRecord rec, Point3d position, int partNumber, int layerIdx)
    {
      var dot  = new TextDot(rec.MateId, position) { FontHeight = EdgeMateDotSize };
      var attr = new ObjectAttributes();
      attr.Name = EdgeMateName;
      if (layerIdx >= 0) attr.LayerIndex = layerIdx;
      attr.SetUserString(EdgeMateIdKey,      rec.MateId);
      attr.SetUserString(EdgePartNumKey,      partNumber.ToString());
      attr.SetUserString(EdgeMatePartNumKey,  rec.MatePartNumber.ToString());
      attr.SetUserString(EdgeMateReversedKey, rec.Reversed ? "true" : "false");
      attr.SetUserString(EdgeIndexKey,         rec.EdgeIndex.ToString());
      attr.SetUserString(MateEdgeIndexKey,     rec.MateEdgeIndex.ToString());
      return doc.Objects.AddTextDot(dot, attr);
    }

    private class LayoutOptions
    {
      public Point3d StartPoint;
      public bool PlacementSpecified;
      public LabelMode LabelMode;
      public bool RotateFlatParts;
      public bool Explode;
      public bool SplitFaces;
      public bool KeepPropSurface;
      public bool KeepPropFollowing;
      public double LayoutSpacing;
      public double XExtents;
    }

    private class SourceSurface
    {
      public Guid Id { get; }
      public GeometryBase Geometry { get; }
      public Brep Brep { get; }
      public int? PreferredPartNumber { get; }

      public SourceSurface(Guid id, GeometryBase geometry, Brep brep, int? preferredPartNumber)
      {
        Id = id;
        Geometry = geometry;
        Brep = brep;
        PreferredPartNumber = preferredPartNumber;
      }
    }

    private class PriorFlatOutput
    {
      public int GroupIndex { get; }
      public HashSet<Guid> MemberIds { get; }

      public PriorFlatOutput(int groupIndex, IEnumerable<Guid> memberIds)
      {
        GroupIndex = groupIndex;
        MemberIds = new HashSet<Guid>(memberIds);
      }
    }

    private class LabelFrame
    {
      public Point3d Point { get; }
      public Point3d UpPoint { get; }
      public Point3d RightPoint { get; }
      public Vector3d X { get; }
      public Vector3d Y { get; }
      public Vector3d Normal { get; }
      public double Height { get; }
      public double Step { get; }

      public LabelFrame(Point3d point, Point3d upPoint, Point3d rightPoint, Vector3d x, Vector3d y, Vector3d normal, double height, double step)
      {
        Point = point;
        UpPoint = upPoint;
        RightPoint = rightPoint;
        X = x;
        Y = y;
        Normal = normal;
        Height = height;
        Step = step;
      }
    }

    private enum FollowingKind
    {
      Curve,
      Point,
      Dot
    }

    private class FollowingItem
    {
      public Guid Id { get; }
      public FollowingKind Kind { get; }
      public GeometryBase Geometry { get; }

      public FollowingItem(Guid id, FollowingKind kind, GeometryBase geometry)
      {
        Id = id;
        Kind = kind;
        Geometry = geometry;
      }
    }

    private class AssignmentResult
    {
      public List<List<FollowingItem>> Buckets { get; }
      public List<Guid> SkippedIds { get; } = new List<Guid>();
      public int Skipped { get; set; }

      public AssignmentResult(int count)
      {
        Buckets = new List<List<FollowingItem>>();
        for (int i = 0; i < count; i++)
          Buckets.Add(new List<FollowingItem>());
      }
    }
  }
}
