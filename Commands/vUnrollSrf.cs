using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.ApplicationSettings;
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
  [CommandStyle(Style.ScriptRunner)]
  public sealed class vUnrollSrf : UnrollSrfCommandBase
  {
    private const string NativeCommandName = "_-UnrollSrf"; // Scriptable Rhino command used for developable-surface unrolling.

    public override string EnglishName => "vUnrollSrf";

    protected override string NativeUnrollCommand => NativeCommandName;
  }

  [CommandStyle(Style.ScriptRunner)]
  public sealed class vUnrollSrfUV : UnrollSrfCommandBase
  {
    private const string NativeCommandName = "_-UnrollSrfUV"; // Scriptable Rhino command used for UV-preserving surface unrolling.

    public override string EnglishName => "vUnrollSrfUV";

    protected override string NativeUnrollCommand => NativeCommandName;
  }

  public abstract class UnrollSrfCommandBase : vToolsCommand
  {
    // Defaults and customizable constants
    private const string SettingsSection = "vUnrollSrf";
    private const string CurrentLayerOption = "*Current*"; // Layer-selector sentinel that resolves output to Rhino's current layer.
    private const string OutputLayerAnchor = "Surface"; // Top-level layer used as the ordering anchor for default output layers.
    private const string DefaultSurfaceLayerName = "Unrolled_surface"; // Rhino layer name for flat surfaces.
    private const string DefaultLabelLayerName = "Unrolled_label"; // Rhino layer name for flat labels.
    private const string DefaultDotLayerName = "Unrolled_dot"; // Rhino layer name for matching dots.
    private const string DefaultSurfaceLayerPath = DefaultSurfaceLayerName; // Rhino layer name or full layer path.
    private const string DefaultLabelLayerPath = DefaultLabelLayerName; // Rhino layer name or full layer path.
    private const string DefaultDotLayerPath = DefaultDotLayerName; // Rhino layer name or full layer path.

    // Option defaults
    private const LabelMode DefaultLabelMode = LabelMode.Text; // LabelMode enum: Text, Dots, or None.
    private const bool DefaultRotateFlatParts = true; // true aligns flat parts to source orientation; false keeps unroller orientation.
    private const bool DefaultExplode = false; // true explodes flat breps into faces; false keeps each unrolled brep joined.
    private const bool DefaultKeepPropSurface = false; // true inherits source surface properties; false uses output-layer properties.
    private const bool DefaultKeepPropFollowing = true; // true inherits properties for following geometry; false uses output layers.
    private const double DefaultLayoutSpacing = 1.0; // Gap between flat parts in model units; zero or greater.
    private const double DefaultXExtents = 0.0; // Row width in model units; zero disables row wrapping.
    private const bool DefaultEdgeDots = true; // true creates matching shared-edge dots; false omits them.
    private const bool DefaultSplitFaces = false; // true unrolls polysurface faces separately; false keeps polysurfaces together.
    private const string TextObjectName = "MultiUnroll_NumberLabel"; // Object name assigned to generated part-number labels.
    private const string FailureMarkerName = "MultiUnroll_FailedMarker"; // Object name assigned to failed-unroll markers.
    private const string LabelNumberKey = "MultiUnrollLabelNumber"; // User-data key storing a reusable part number.
    private const string FailedUnrollKey = "MultiUnrollFailed"; // User-data key identifying failed-unroll markers.
    private const string FlatGroupPrefix = "MultiUnroll_Flat"; // Prefix for generated flat-part group names.
    private const string OriginalGroupPrefix = "MultiUnroll_Original"; // Prefix for source-part group names.
    private const string FailureMarkerText = "X"; // Text displayed on surfaces that could not be unrolled.
    private const string LabelHelperDotPrefix     = "__vTools_vUnrollSrf_LabelHelper__"; // Internal following-dot name prefix for label frames.
    private const string EdgeMateHelperDotPrefix  = "__vTools_vUnrollSrf_EdgeHelper__"; // Internal following-dot name prefix for edge mates.
    private const string UserPointHelperDotPrefix = "__vTools_vUnrollSrf_UserPointHelper__"; // Internal following-dot name prefix for preserving selected point identity across duplicated face output.
    private const string UserDotHelperDotPrefix   = "__vTools_vUnrollSrf_UserDotHelper__"; // Internal following-dot name prefix for preserving selected text dots through native unroll.
    private const string NativeFaceHelperDotPrefix = "__vTools_vUnrollSrf_FaceHelper__"; // Internal following-dot name prefix used to map each source face to its native flat output.
    private const string CurveHelperDotPrefix     = "__vTools_vUnrollSrf_CurveHelper__"; // Internal following-dot name prefix for curves.

    private const string TextFont = "Arial"; // Installed font family used for generated number labels and failure markers.
    private const double TextHeightScale = 1.5; // Label-height multiplier relative to the part-derived base height.
    private const double FlatTextLiftRatio = 0.0; // Unrolled-label normal lift as a fraction of text height; zero keeps text coplanar.
    private const double SurfaceTextLiftRatio = 0.001; // Original-surface label normal lift as a fraction of text height; zero disables lift.
    private const double MinimumTextLiftToleranceFactor = 2.0; // Document-tolerance multiplier used as minimum lift when the selected ratio is positive.
    private const double TextUpStepRatio = 2.5; // Label up-direction probe distance as a fraction of text height.
    private const int TextBoundarySamples = 7; // Samples per boundary curve used for label fitting; two or greater.
    private const bool TextMarkSixNine = true; // true underlines all-6/9 labels for orientation; false leaves them plain.

    private const double FollowingTolFactor = 100.0; // Document-tolerance multiplier for associating following geometry.
    private const double FollowingDiagFactor = 1.0e-4; // Geometry-diagonal fraction added to following-object tolerance.
    private const int FollowingCurveSamples = 9; // Samples per following curve used for surface association; two or greater.
    private const double SharedPointToleranceFactor = 1.0; // Document-tolerance multiplier for treating projected points as the same shared edge/vertex location.
    private const double NativeLabelEdgeToleranceFactor = 50.0; // Document-tolerance multiplier for associating native unroll labels with flat edges.
    private const double NativeSeamMaxRelativeLengthError = 0.01; // Maximum 0..1 relative edge-length error accepted by source-topology fallback seam matching.
    private const double NativeJoinedSeamToleranceFactor = 10.0; // Document-tolerance multiplier for recognizing an aligned seam as an interior edge after face joining.
    private const bool NativeForceExplodePolysurfaces = true; // true temporarily separates multi-face native output for source-topology reconstruction; false honors the visible Explode option directly.
    private const bool NativeUseNoEcho = true; // true runs the internal Rhino macro with NoEcho; false relies only on RunScript's silent flag.
    private const int NativeCapturedLogLimit = 12; // Maximum captured native command lines written to the shared diagnostic log per source object; zero disables captured-line logging.

    // Edge-mate dot constants (match MultiUnroll2.py / vMatch.cs)
    private const string EdgeMateName        = vMatch.EdgeMateName; // Shared output object name for matching edge dots.
    private const string EdgeMateIdKey       = vMatch.EdgeMateIdKey; // Shared user-data key for the match identifier.
    private const string EdgePartNumKey      = vMatch.EdgePartNumKey; // Shared user-data key for the owning part number.
    private const string EdgeMatePartNumKey  = vMatch.EdgeMatePartNumKey; // Shared user-data key for the mating part number.
    private const string EdgeMateReversedKey = vMatch.EdgeMateReversedKey; // Shared user-data key for edge-direction reversal.
    private const string EdgeIndexKey        = "MultiUnrollEdgeIndex"; // User-data key for a source topology-edge index.
    private const string MateEdgeIndexKey    = "MultiUnrollMateEdgeIndex"; // User-data key for the matching topology-edge index.
    private const string EdgeIndexModeKey    = "MultiUnrollEdgeIndexMode"; // User-data key describing how edge indexes were assigned.
    private const string TopologyEdgeMode    = "Topology"; // Stored edge-index mode for topology-derived matches.
    private const string EdgeMatePrefix      = "M"; // Prefix displayed before matching edge-dot numbers.
    private const int    EdgeMateDotSize     = 10; // Text-dot font height for edge mates in display pixels.
    private const double EdgeMateTolFactor   = 25.0; // Document-tolerance multiplier for shared-edge matching.
    private const double EdgeMateDiagFactor  = 1.0e-4; // Edge-length fraction added to shared-edge tolerance.
    private const int    EdgeMateSamples     = 7; // Interior samples used to validate an edge match; two or greater.
    private const int    LabelInteriorSamples = 17; // UV grid resolution used to find interior label positions.

    private static readonly string[] LabelModeNames = { "Text", "Dots", "None" }; // Command option names in LabelMode enum order.

    private enum LabelMode
    {
      Text = 0,
      Dots = 1,
      None = 2
    }

    private static LabelMode _labelMode = DefaultLabelMode;
    private static bool _rotateFlatParts = DefaultRotateFlatParts;
    private static bool _explode = DefaultExplode;
    private static bool _keepPropSurface = DefaultKeepPropSurface;
    private static bool _keepPropFollowing = DefaultKeepPropFollowing;
    private static double _layoutSpacing = DefaultLayoutSpacing;
    private static double _xExtents = DefaultXExtents;
    private static bool _edgeDots = DefaultEdgeDots;
    private static bool _splitFaces = DefaultSplitFaces;
    private static string _surfaceLayer = DefaultSurfaceLayerPath;
    private static string _labelLayer = DefaultLabelLayerPath;
    private static string _dotLayer = DefaultDotLayerPath;

    protected abstract string NativeUnrollCommand { get; }

    // ── Debug logging ─────────────────────────────────────────────────────
    private static void Dbg(string msg) => vTools.Log.Write("vUnrollSrf", msg);

    private static string P(Point3d? p)  => p.HasValue  ? $"({p.Value.X:G6}, {p.Value.Y:G6}, {p.Value.Z:G6})" : "None";
    private static string P(Point3d p)   => $"({p.X:G6}, {p.Y:G6}, {p.Z:G6})";
    private static string V(Vector3d? v) => v.HasValue  ? $"({v.Value.X:G6}, {v.Value.Y:G6}, {v.Value.Z:G6})" : "None";
    private static string V(Vector3d v)  => $"({v.X:G6}, {v.Y:G6}, {v.Z:G6})";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
      LoadSettings();
      Dbg($"run start command={EnglishName} native={NativeUnrollCommand}" +
          $" model_tol={doc.ModelAbsoluteTolerance:G} doc={doc.Path}");
      var startIds = SelectedIds(doc);
      var surfaceIds = GetSurfaceIds(
        doc, startIds.Where(IsSurfaceLikeId).ToList(), mode, EnglishName);
      if (surfaceIds == null || surfaceIds.Count == 0)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      var followingIds = GetFollowingIds(
        doc, startIds.Where(IsFollowingLikeId).ToList(), surfaceIds, mode, EnglishName);
      if (followingIds == null)
      {
        RestoreSelection(doc, startIds);
        return Result.Cancel;
      }

      var options = GetLayoutOptions(
        doc, "Start point for unrolls - press Enter for world 0", mode, EnglishName);
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
      _surfaceLayer = options.SurfaceLayer;
      _labelLayer = options.LabelLayer;
      _dotLayer = options.DotLayer;

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
      var nativeMateIds = new NativeMateIdAllocator(doc, edgePairs);
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
          Dbg($"part={number} source object_type={src.Geometry.ObjectType}" +
              $" brep_is_surface={src.Brep.IsSurface} faces={src.Brep.Faces.Count}" +
              $" edges={src.Brep.Edges.Count} unroll_input=brep" +
              $" abs_tol={doc.ModelAbsoluteTolerance:G6}" +
              $" rel_tol={doc.ModelRelativeTolerance:G6}");
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

          // Edge-mate positions use uniquely named hidden dots. Their names survive
          // unrolling, unlike curve output order, so they cannot be confused with
          // label helpers or similarly sized boundary curves.
          var edgeMateRecords = (edgePairs != null && i < edgePairs.Count)
            ? edgePairs[i]
            : new List<EdgeMateRecord>();
          var edgeMateHelperDots = new Dictionary<string, EdgeMateRecord>(StringComparer.Ordinal);
          var userPointHelperDots = new Dictionary<string, Guid>(StringComparer.Ordinal);
          var userDotHelperDots = new Dictionary<string, (TextDot Dot, Guid SourceId)>(StringComparer.Ordinal);
          var nativeFaceHelperDots = new Dictionary<string, int>(StringComparer.Ordinal);
          var curveHelperDots = new Dictionary<string, int>(StringComparer.Ordinal);
          var followingDots = new List<TextDot>();
          foreach (var rec in UniqueEdgeMateRecords(edgeMateRecords))
          {
            string helperText = EdgeMateHelperDotText(src.Id, rec);
            edgeMateHelperDots[helperText] = rec;
            var helperDot = new TextDot(helperText, rec.Marker);
            followingDots.Add(helperDot);
          }
          Dbg($"part={number} edge_mates records={edgeMateRecords.Count} helpers={edgeMateHelperDots.Count}");

          if (src.Brep.Faces.Count > 1)
          {
            foreach (var face in src.Brep.Faces)
            {
              var helperPoint = FaceInteriorPoint(face, tol);
              if (!helperPoint.HasValue)
              {
                Dbg($"part={number} face_helper source_face={face.FaceIndex} point=None");
                continue;
              }

              string helperText = NativeFaceHelperDotPrefix +
                $"{src.Id:N}:{number}:{face.FaceIndex}";
              nativeFaceHelperDots[helperText] = face.FaceIndex;
              followingDots.Add(new TextDot(helperText, helperPoint.Value));
              Dbg($"part={number} face_helper source_face={face.FaceIndex}" +
                  $" point={P(helperPoint.Value)}");
            }
          }

          for (int curveIndex = 0; curveIndex < curves.Count; curveIndex++)
          {
            var markerPoint = curves[curveIndex].PointAtNormalizedLength(0.5);
            if (!markerPoint.IsValid)
              continue;
            string markerText = CurveHelperDotPrefix +
              $"{src.Id:N}:{number}:{curveIndex}";
            curveHelperDots[markerText] = curveIndex;
            followingDots.Add(new TextDot(markerText, markerPoint));
          }

          for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
          {
            string helperText =
              $"{UserPointHelperDotPrefix}{src.Id:N}:{number}:{pointIndex}";
            userPointHelperDots[helperText] = pointSourceIds[pointIndex];
            followingDots.Add(new TextDot(helperText, points[pointIndex].Location));
          }

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
          }

          for (int dotIndex = 0; dotIndex < dots.Count; dotIndex++)
          {
            string helperText = $"{UserDotHelperDotPrefix}{src.Id:N}:{number}:{dotIndex}";
            userDotHelperDots[helperText] = (dots[dotIndex], dotSourceIds[dotIndex]);
            followingDots.Add(new TextDot(helperText, dots[dotIndex].Point));
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
              unrolledPoints = Array.Empty<Point3d>();
              unrolledDots = followingDots.Select(dot => TransformTextDotCopy(dot, planarTransform)).ToArray();
              Dbg($"part={number} unroll_method=planar_exact");
            }
            else
            {
              if (TryPerformNativeCommandUnroll(
                    doc, src, NativeUnrollCommand, _explode,
                    curves, Array.Empty<Point>(), followingDots,
                    out unrolledBreps, out unrolledCurves,
                    out unrolledPoints, out unrolledDots,
                    out string nativeDetails))
              {
                Dbg($"part={number} unroll_method=native_command {nativeDetails}");
              }
              else
              {
                // Retain an API fallback only if the delegated native command fails.
                unrolledBreps = unroller.PerformUnroll(out _, out _, out _);
                Dbg($"part={number} unroll_method=rhino_unroller_fallback" +
                    $" reason={nativeDetails}");
                // UV-project following items only when the native command was unavailable.
                if (unrolledBreps?.Length > 0 && src.Brep.Faces.Count == unrolledBreps[0].Faces.Count)
                {
                  var mc = new List<Curve>(curves.Count);
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
                  }

                  foreach (var dot in followingDots)
                  {
                    int? preferredEdgeIndex = edgeMateHelperDots.TryGetValue(
                      dot.Text ?? string.Empty, out var edgeRecord)
                      ? edgeRecord.EdgeIndex
                      : null;
                    if (!TryMapPointToUnrolledBrep(
                      src.Brep, unrolledBreps[0], dot.Point, preferredEdgeIndex,
                      out var mappedPoint, out int mappedFace))
                      continue;

                    int mappedFlatEdge = -1;
                    if (preferredEdgeIndex.HasValue &&
                        TryMapPointToFlatEdge(
                          src.Brep, unrolledBreps[0], dot.Point,
                          preferredEdgeIndex.Value, mappedFace, mappedPoint,
                          out var edgePoint, out mappedFlatEdge))
                      mappedPoint = edgePoint;

                    var copy = dot.Duplicate() as TextDot ??
                      new TextDot(dot.Text ?? string.Empty, dot.Point);
                    copy.Point = mappedPoint;
                    md.Add(copy);
                    if (preferredEdgeIndex.HasValue)
                      Dbg($"part={number} edge_helper_map id={edgeRecord!.MateId}" +
                          $" edge={preferredEdgeIndex.Value} face={mappedFace}" +
                          $" flat_edge={mappedFlatEdge}" +
                          $" point={P(mappedPoint)}");
                  }
                  unrolledCurves = mc.ToArray();
                  unrolledPoints = Array.Empty<Point3d>();
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
          LogUnrolledEdges(number, unrolledBreps);

          done++;

          var outputIds = new List<Guid>();
          var surfaceOutputIds = new List<Guid>();
          var followingOutputPairs = new List<(Guid srcId, Guid outId)>(); // source-ID → flat output-ID for KeepPropFollowing
          var curveOutputIds    = new List<Guid>();
          var curveFlatMidpoints = new Dictionary<int, Point3d>();
          var nativeInternalMates = unrolledBreps.Length > 1
            ? ReconstructNativeFlatFaces(
                src.Brep,
                unrolledBreps,
                unrolledCurves ?? Array.Empty<Curve>(),
                unrolledPoints ?? Array.Empty<Point3d>(),
                unrolledDots ?? Array.Empty<TextDot>(),
                nativeFaceHelperDots,
                nativeMateIds,
                number,
                tol,
                reconstructFaces: !_explode)
            : new List<NativeInternalMate>();
          var finalBreps = !_explode && unrolledBreps.Length > 1
            ? (Brep.JoinBreps(unrolledBreps, tol) ?? unrolledBreps)
            : unrolledBreps;
          var visibleNativeInternalMates = _explode
            ? nativeInternalMates
            : nativeInternalMates
              .Where(mate => !NativeMateWasJoined(finalBreps, mate, tol))
              .ToList();
          foreach (var brep in finalBreps)
          {
            var surfaceId = doc.Objects.AddBrep(
              brep,
              new ObjectAttributes { LayerIndex = SurfaceOutputLayerIndex(doc) });
            AddValid(outputIds, surfaceId);
            AddValid(surfaceOutputIds, surfaceId);
          }

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
            if (unrolledPoints.Length > 0)
              Dbg($"part={number} unexpected_native_points removed={unrolledPoints.Length}");
          }

          if (unrolledDots != null)
          {
            foreach (var dot in unrolledDots)
            {
              var dotText = dot.Text ?? string.Empty;
              if (curveHelperDots.TryGetValue(dotText, out int curveIndex))
              {
                curveFlatMidpoints[curveIndex] = dot.Point;
                continue;
              }

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

              if (nativeFaceHelperDots.ContainsKey(dotText))
              {
                Dbg($"part={number} hidden_face_dot text={dotText}" +
                    $" point={P(dot.Point)}");
                continue;
              }

              if (userPointHelperDots.TryGetValue(dotText, out Guid pointSourceId))
              {
                var pointId = doc.Objects.AddPoint(dot.Point);
                AddValid(outputIds, pointId);
                if (IsValidId(pointId))
                  followingOutputPairs.Add((pointSourceId, pointId));
                Dbg($"part={number} user_point source={pointSourceId}" +
                    $" point={P(dot.Point)} output={pointId}");
                continue;
              }

              if (userDotHelperDots.TryGetValue(dotText, out var userDot))
              {
                var copy = userDot.Dot.Duplicate() as TextDot ??
                  new TextDot(userDot.Dot.Text ?? string.Empty, dot.Point);
                copy.Point = dot.Point;
                var dotId = doc.Objects.AddTextDot(copy);
                AddValid(outputIds, dotId);
                if (IsValidId(dotId))
                  followingOutputPairs.Add((userDot.SourceId, dotId));
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

              Dbg($"part={number} unknown_helper text={dotText}" +
                  $" point={P(dot.Point)} removed=true");
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
          double finalLabelHeight = frame?.Height ?? 1.0;
          if (frame != null && labelPoint.HasValue)
          {
            finalLabelHeight = FitFlatLabelHeight(
              unrolledBreps, display, labelPoint.Value, unrolledY,
              frame.Height, doc.ModelAbsoluteTolerance);
            Dbg($"part={number} label_fit requested={frame.Height:G6}" +
                $" fitted={finalLabelHeight:G6}");
            if (addText)
            {
              // Keep the raw unrolled up direction as the orientation marker, but do not use
              // the raw frame normal for flat labels. If the unrolled helper frame lands with
              // a -Z normal, annotation text becomes mirrored in Top view. World +Z keeps the
              // text readable while preserving the same unrolled Y/up direction.
              AddValid(unrolledLabelIds, AddFlatText(
                doc, display, labelPoint.Value, unrolledY, Vector3d.ZAxis,
                finalLabelHeight, FlatTextLiftRatio, src.Id, _keepPropSurface));
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
            PutOnLayer(doc, surfaceOutputIds, SurfaceOutputLayerIndex(doc));
            PutOnLayer(doc, unrolledLabelIds, LabelOutputLayerIndex(doc));
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
            EnsureOriginalLabel(doc, src.Id, number, display, frame, finalLabelHeight, addText, addDots, _keepPropSurface);
          }

          // Place edge mate dots on flat output
          if (edgeFlatPoints.Count > 0)
          {
            int dotLayerIdx = -1;
            foreach (var rec in UniqueEdgeMateRecords(edgeMateRecords))
            {
              var key = (rec.MateId, rec.EdgeIndex, rec.MatePartIndex);
              if (!edgeFlatPoints.TryGetValue(key, out var flatPt)) continue;
              if (dotLayerIdx < 0)
                dotLayerIdx = DotOutputLayerIndex(doc);
              var dotId = AddEdgeMateDot(doc, rec, flatPt, number, dotLayerIdx);
              if (IsValidId(dotId))
                outputIds.Add(dotId);
            }
          }

          if (visibleNativeInternalMates.Count > 0)
          {
            int dotLayerIdx = DotOutputLayerIndex(doc);
            foreach (var mate in visibleNativeInternalMates)
            {
              var first = new EdgeMateRecord
              {
                MateId = mate.MateId,
                EdgeIndex = -1,
                MatePartIndex = -1,
                MatePartNumber = number,
                MateEdgeIndex = -1,
                Reversed = mate.Reversed
              };
              var second = new EdgeMateRecord
              {
                MateId = mate.MateId,
                EdgeIndex = -1,
                MatePartIndex = -2,
                MatePartNumber = number,
                MateEdgeIndex = -1,
                Reversed = !mate.Reversed
              };
              AddValid(outputIds, AddEdgeMateDot(doc, first, mate.PointA, number, dotLayerIdx));
              AddValid(outputIds, AddEdgeMateDot(doc, second, mate.PointB, number, dotLayerIdx));
            }
          }
          if (nativeInternalMates.Count > visibleNativeInternalMates.Count)
          {
            Dbg($"part={number} native_seam_dots omitted=" +
                $"{nativeInternalMates.Count - visibleNativeInternalMates.Count}" +
                " reason=faces_joined");
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

          LogFlatOutput(doc, number, outputIds);
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
      SaveSettings(EnglishName);

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

    private static List<Guid>? GetSurfaceIds(
      RhinoDoc doc,
      List<Guid> preselected,
      RunMode runMode,
      string commandName)
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
          HandleSharedOption(doc, go, shared, runMode, commandName);
          continue;
        }
        if (go.CommandResult() != Result.Success)
          return null;
        return Unique(Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i).ObjectId).Where(IsSurfaceLikeId));
      }
    }

    private static List<Guid>? GetFollowingIds(
      RhinoDoc doc,
      List<Guid> seedIds,
      List<Guid> surfaceIds,
      RunMode runMode,
      string commandName)
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
            HandleSharedOption(doc, go, shared, runMode, commandName);
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

    private static LayoutOptions? GetLayoutOptions(
      RhinoDoc doc,
      string prompt,
      RunMode runMode,
      string commandName)
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
        HandleSharedOption(doc, gp, shared, runMode, commandName);
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
        XExtents       = _xExtents,
        SurfaceLayer   = _surfaceLayer,
        LabelLayer     = _labelLayer,
        DotLayer       = _dotLayer
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
      public int SurfaceLayerIndex = -1;
      public int LabelLayerIndex = -1;
      public int DotLayerIndex = -1;
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
      state.SurfaceLayerIndex = getter.AddOption("SurfaceLayer", _surfaceLayer);
      state.LabelLayerIndex = getter.AddOption("LabelLayer", _labelLayer);
      state.DotLayerIndex = getter.AddOption("DotLayer", _dotLayer);
      return state;
    }

    private static void HandleSharedOption(
      RhinoDoc doc,
      GetBaseClass getter,
      SharedOptions state,
      RunMode runMode,
      string commandName)
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
          case "SurfaceLayer":       RhinoApp.WriteLine("SurfaceLayer: layer for generated flat surfaces."); break;
          case "LabelLayer":         RhinoApp.WriteLine("LabelLayer: layer for number labels and failed-unroll markers."); break;
          case "DotLayer":           RhinoApp.WriteLine("DotLayer: layer for shared-edge match dots."); break;
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

      if (option != null && option.Index == state.SurfaceLayerIndex)
      {
        SelectOutputLayer(
          doc, runMode, $"{commandName} surface layer", _surfaceLayer,
          selected => _surfaceLayer = NormalizeOutputLayer(selected, DefaultSurfaceLayerName, DefaultSurfaceLayerPath));
      }
      if (option != null && option.Index == state.LabelLayerIndex)
      {
        SelectOutputLayer(
          doc, runMode, $"{commandName} label layer", _labelLayer,
          selected => _labelLayer = NormalizeOutputLayer(selected, DefaultLabelLayerName, DefaultLabelLayerPath));
      }
      if (option != null && option.Index == state.DotLayerIndex)
      {
        SelectOutputLayer(
          doc, runMode, $"{commandName} dot layer", _dotLayer,
          selected => _dotLayer = NormalizeOutputLayer(selected, DefaultDotLayerName, DefaultDotLayerPath));
      }

      if (option != null)
        SaveSettings(commandName);
    }

    private static void SelectOutputLayer(
      RhinoDoc doc,
      RunMode runMode,
      string title,
      string currentValue,
      Action<string> assign)
    {
      if (LayerSelector.TrySelect(
            doc,
            currentValue,
            CurrentLayerOption,
            title,
            runMode,
            allowNewLayer: true,
            out var selectedLayer))
      {
        assign(selectedLayer);
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

    private static bool TryPerformNativeCommandUnroll(
      RhinoDoc doc,
      SourceSurface source,
      string nativeUnrollCommand,
      bool explode,
      IReadOnlyList<Curve> curves,
      IReadOnlyList<Point> points,
      IReadOnlyList<TextDot> dots,
      out Brep[] flatBreps,
      out Curve[] flatCurves,
      out Point3d[] flatPoints,
      out TextDot[] flatDots,
      out string details)
    {
      flatBreps = Array.Empty<Brep>();
      flatCurves = Array.Empty<Curve>();
      flatPoints = Array.Empty<Point3d>();
      flatDots = Array.Empty<TextDot>();
      details = "not_attempted";

      var selectedBefore = SelectedIds(doc);
      var temporaryInputIds = new List<Guid>();
      var temporaryOutputIds = new List<Guid>();
      try
      {
        var commandSourceId = doc.Objects.AddBrep(source.Brep.DuplicateBrep());
        if (!IsValidId(commandSourceId))
        {
          details = "temporary_source_failed";
          return false;
        }
        temporaryInputIds.Add(commandSourceId);

        foreach (var curve in curves)
          AddValid(temporaryInputIds, doc.Objects.AddCurve(curve.DuplicateCurve()));
        foreach (var point in points)
          AddValid(temporaryInputIds, doc.Objects.AddPoint(point.Location));
        foreach (var dot in dots)
        {
          var copy = dot.Duplicate() as TextDot ??
            new TextDot(dot.Text ?? string.Empty, dot.Point);
          AddValid(temporaryInputIds, doc.Objects.AddTextDot(copy));
        }

        var followingIds = temporaryInputIds.Skip(1).ToList();
        var objectIdsBefore = doc.Objects.Select(obj => obj.Id).ToHashSet();
        SelectOnly(doc, Array.Empty<Guid>());
        bool nativeExplode = explode ||
          NativeForceExplodePolysurfaces && source.Brep.Faces.Count > 1;
        bool nativeLabels = source.Brep.Faces.Count > 1;
        string command = (NativeUseNoEcho ? "_NoEcho " : string.Empty) +
          nativeUnrollCommand +
          $" _Explode=_{(nativeExplode ? "Yes" : "No")}" +
          $" _Labels=_{(nativeLabels ? "Yes" : "No")}" +
          $" _SelId {commandSourceId:D}" +
          " _Enter" +
          string.Concat(followingIds.Select(id => $" _SelId {id:D}")) +
          " _Enter";
        bool captureStartedHere = !RhinoApp.CommandWindowCaptureEnabled;
        bool echoPrompts = AppearanceSettings.EchoPromptsToHistoryWindow;
        bool echoCommands = AppearanceSettings.EchoCommandsToHistoryWindow;
        string[] capturedOutput = Array.Empty<string>();
        bool ran;
        if (captureStartedHere)
        {
          RhinoApp.CommandWindowCaptureEnabled = true;
          RhinoApp.CapturedCommandWindowStrings(true);
        }
        try
        {
          AppearanceSettings.EchoPromptsToHistoryWindow = false;
          AppearanceSettings.EchoCommandsToHistoryWindow = false;
          ran = RhinoApp.RunScript(command, false);
        }
        finally
        {
          AppearanceSettings.EchoPromptsToHistoryWindow = echoPrompts;
          AppearanceSettings.EchoCommandsToHistoryWindow = echoCommands;
          if (captureStartedHere)
          {
            capturedOutput = RhinoApp.CapturedCommandWindowStrings(true);
            RhinoApp.CommandWindowCaptureEnabled = false;
          }
        }

        foreach (string line in capturedOutput
          .SelectMany(line => line.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
          .Select(line => line.Trim())
          .Where(line => line.Length > 0)
          .Take(NativeCapturedLogLimit))
          Dbg($"native_output {line}");

        Dbg($"native_options command={nativeUnrollCommand}" +
            $" explode={nativeExplode} labels={nativeLabels}");

        var created = doc.Objects
          .Where(obj => obj != null && !objectIdsBefore.Contains(obj.Id))
          .OrderBy(obj => obj.RuntimeSerialNumber)
          .ToList();
        temporaryOutputIds.AddRange(created.Select(obj => obj.Id));
        flatBreps = created
          .Select(obj => obj.Geometry as Brep)
          .Where(brep => brep != null)
          .Select(brep => brep!.DuplicateBrep())
          .Where(brep => brep != null && brep.IsValid)
          .ToArray();
        flatCurves = created
          .Select(obj => obj.Geometry as Curve)
          .Where(curve => curve != null)
          .Select(curve => curve!.DuplicateCurve())
          .ToArray();
        flatPoints = created
          .Select(obj => obj.Geometry as Point)
          .Where(point => point != null)
          .Select(point => point!.Location)
          .ToArray();
        flatDots = created
          .Select(obj => obj.Geometry as TextDot)
          .Where(dot => dot != null)
          .Select(dot => dot!.Duplicate() as TextDot ??
            new TextDot(dot.Text ?? string.Empty, dot.Point))
          .ToArray();

        details = $"ran={ran} inputs={followingIds.Count}" +
          $" requested_explode={explode} native_explode={nativeExplode}" +
          $" created={created.Count} breps={flatBreps.Length}" +
          $" curves={flatCurves.Length} points={flatPoints.Length}" +
          $" dots={flatDots.Length} hidden_output={capturedOutput.Length}";
        return ran && flatBreps.Length > 0;
      }
      catch (Exception ex)
      {
        details = $"error={ex.Message}";
        flatBreps = Array.Empty<Brep>();
        flatCurves = Array.Empty<Curve>();
        flatPoints = Array.Empty<Point3d>();
        flatDots = Array.Empty<TextDot>();
        return false;
      }
      finally
      {
        foreach (var id in temporaryOutputIds.Concat(temporaryInputIds))
        {
          if (doc.Objects.FindId(id) != null)
            doc.Objects.Delete(id, true);
        }
        RestoreSelection(doc, selectedBefore);
      }
    }

    private static void LogUnrolledEdges(int partNumber, IReadOnlyList<Brep> breps)
    {
      var edges = new List<(int Brep, int Edge, double Length, int Degree, int Cvs, int Spans)>();
      for (int brepIndex = 0; brepIndex < breps.Count; brepIndex++)
      {
        var brep = breps[brepIndex];
        for (int edgeIndex = 0; edgeIndex < brep.Edges.Count; edgeIndex++)
        {
          var edge = brep.Edges[edgeIndex];
          var nurbs = edge.ToNurbsCurve();
          edges.Add((
            brepIndex,
            edgeIndex,
            edge.GetLength(),
            nurbs?.Degree ?? -1,
            nurbs?.Points.Count ?? 0,
            nurbs?.SpanCount ?? 0));
        }
      }

      Dbg($"part={partNumber} flat_topology breps={breps.Count}" +
          $" faces={breps.Sum(brep => brep.Faces.Count)} edges={edges.Count}");
      foreach (var edge in edges.OrderByDescending(item => item.Length).Take(8))
      {
        Dbg($"part={partNumber} flat_edge brep={edge.Brep} edge={edge.Edge}" +
            $" length={edge.Length:G8} degree={edge.Degree}" +
            $" cvs={edge.Cvs} spans={edge.Spans}");
      }
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
        using var area = AreaMassProperties.Compute(brep);
        var target = area?.Centroid ?? brep.GetBoundingBox(true).Center;
        var interior = InteriorLabelPoint(brep, target, doc.ModelAbsoluteTolerance);
        if (interior.HasValue)
          return interior;
        try { return brep.ClosestPoint(target); }
        catch { return target; }
      }
      if (brepHint != null) return null;
      var obj = doc.Objects.FindId(objId);
      var bbox = obj?.Geometry.GetBoundingBox(true) ?? BoundingBox.Empty;
      return bbox.IsValid ? bbox.Center : (Point3d?)null;
    }

    private static Point3d? InteriorLabelPoint(
      Brep brep,
      Point3d target,
      double tolerance)
    {
      Point3d? best = null;
      double bestClearance = -1.0;
      double bestTargetDistance = double.PositiveInfinity;

      void Consider(BrepFace face, double u, double v)
      {
        try
        {
          if (face.IsPointOnFace(u, v) != PointFaceRelation.Interior)
            return;
        }
        catch
        {
        }

        var candidate = face.PointAt(u, v);
        if (!candidate.IsValid)
          return;
        double clearance = BoundaryClearance(brep, candidate);
        double targetDistance = candidate.DistanceToSquared(target);
        if (clearance < bestClearance - tolerance ||
            (Math.Abs(clearance - bestClearance) <= tolerance &&
             targetDistance >= bestTargetDistance))
          return;

        best = candidate;
        bestClearance = clearance;
        bestTargetDistance = targetDistance;
      }

      foreach (var face in brep.Faces)
      {
        if (face.ClosestPoint(target, out double targetU, out double targetV))
          Consider(face, targetU, targetV);

        var uDomain = face.Domain(0);
        var vDomain = face.Domain(1);
        for (int uIndex = 0; uIndex < LabelInteriorSamples; uIndex++)
        {
          double u = uDomain.ParameterAt((uIndex + 0.5) / LabelInteriorSamples);
          for (int vIndex = 0; vIndex < LabelInteriorSamples; vIndex++)
          {
            double v = vDomain.ParameterAt((vIndex + 0.5) / LabelInteriorSamples);
            Consider(face, u, v);
          }
        }
      }

      return best;
    }

    private static double BoundaryClearance(Brep brep, Point3d point)
    {
      var edges = brep.Edges
        .Where(edge => edge.Valence == EdgeAdjacency.Naked)
        .ToList();
      if (edges.Count == 0)
        edges = brep.Edges.ToList();

      double clearance = double.PositiveInfinity;
      foreach (var edge in edges)
      {
        if (!edge.ClosestPoint(point, out double parameter))
          continue;
        clearance = Math.Min(clearance, point.DistanceTo(edge.PointAt(parameter)));
      }
      return double.IsFinite(clearance) ? clearance : 0.0;
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
      if (brep != null)
      {
        double clearance = BoundaryClearance(brep, point.Value);
        if (clearance > tol * 2.0)
          step = Math.Min(step, Math.Max(clearance * 0.5, tol * 20.0));
      }
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

    private static double TextLift(RhinoDoc doc, double height, double liftRatio)
    {
      if (liftRatio <= 0.0)
        return 0.0;

      return Math.Max(
        height * liftRatio,
        doc.ModelAbsoluteTolerance * MinimumTextLiftToleranceFactor);
    }

    private static Guid AddFlatText(
      RhinoDoc doc,
      string text,
      Point3d point,
      Vector3d yDirection,
      Vector3d normal,
      double height,
      double liftRatio,
      Guid sourceId,
      bool transfer)
    {
      var n = Unit(normal, doc.ModelAbsoluteTolerance) ?? Vector3d.ZAxis;
      var lift = TextLift(doc, height, liftRatio);
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
      var lift = TextLift(doc, height, SurfaceTextLiftRatio);
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
      attrs.LayerIndex = LabelOutputLayerIndex(doc);
      attrs.SetUserString(LabelNumberKey, BaseNumber(labelText));
      return attrs;
    }

    private static ObjectAttributes FailureMarkerAttributes(RhinoDoc doc, Guid sourceId)
    {
      var attrs = doc.Objects.FindId(sourceId)?.Attributes.Duplicate() ?? new ObjectAttributes();
      attrs.Name = FailureMarkerName;
      attrs.LayerIndex = LabelOutputLayerIndex(doc);
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

    private static int SurfaceOutputLayerIndex(RhinoDoc doc)
    {
      return EnsureOutputLayer(doc, _surfaceLayer, DefaultSurfaceLayerPath);
    }

    private static int LabelOutputLayerIndex(RhinoDoc doc)
    {
      return EnsureOutputLayer(doc, _labelLayer, DefaultLabelLayerPath);
    }

    private static int DotOutputLayerIndex(RhinoDoc doc)
    {
      return EnsureOutputLayer(doc, _dotLayer, DefaultDotLayerPath);
    }

    private static string NormalizeOutputLayer(
      string? value,
      string defaultName,
      string defaultPath)
    {
      var normalized = value?.Trim() ?? string.Empty;
      var legacyDefaultPath = OutputLayerAnchor + "::" + defaultName;
      if (string.IsNullOrWhiteSpace(normalized) ||
          string.Equals(normalized, defaultName, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(normalized, defaultPath, StringComparison.OrdinalIgnoreCase) ||
          string.Equals(normalized, legacyDefaultPath, StringComparison.OrdinalIgnoreCase))
      {
        return defaultPath;
      }

      if (LayerSelector.IsCurrentLayerValue(normalized, CurrentLayerOption))
      {
        return CurrentLayerOption;
      }

      return normalized;
    }

    private static int EnsureOutputLayer(
      RhinoDoc doc,
      string configuredLayer,
      string defaultPath)
    {
      var layerPath = string.IsNullOrWhiteSpace(configuredLayer)
        ? defaultPath
        : configuredLayer.Trim();
      if (LayerSelector.IsCurrentLayerValue(layerPath, CurrentLayerOption))
      {
        return doc.Layers.CurrentLayerIndex;
      }

      int existingIndex = doc.Layers.FindByFullPath(
        layerPath, RhinoMath.UnsetIntIndex);
      if (existingIndex >= 0)
      {
        PlaceDefaultOutputLayersBelowSurface(doc, layerPath);
        return existingIndex;
      }

      if (!layerPath.Contains("::", StringComparison.Ordinal))
      {
        var matching = doc.Layers
          .Where(layer => layer != null && !layer.IsDeleted &&
            layer.ParentLayerId == Guid.Empty &&
            string.Equals(layer.Name, layerPath, StringComparison.OrdinalIgnoreCase))
          .ToList();
        if (matching.Count == 1)
        {
          PlaceDefaultOutputLayersBelowSurface(doc, layerPath);
          return matching[0].Index;
        }

        if (IsDefaultOutputLayerName(layerPath))
        {
          var surfaceParent = doc.Layers.FirstOrDefault(layer =>
            layer != null && !layer.IsDeleted &&
            layer.ParentLayerId == Guid.Empty &&
            string.Equals(layer.Name, OutputLayerAnchor, StringComparison.OrdinalIgnoreCase));
          var legacyLayer = surfaceParent == null
            ? null
            : doc.Layers.FirstOrDefault(layer =>
              layer != null && !layer.IsDeleted &&
              layer.ParentLayerId == surfaceParent.Id &&
              string.Equals(layer.Name, layerPath, StringComparison.OrdinalIgnoreCase));
          if (legacyLayer != null)
          {
            legacyLayer.ParentLayerId = Guid.Empty;
            if (doc.Layers.Modify(legacyLayer, legacyLayer.Index, true))
            {
              Dbg($"layer migrated path={OutputLayerAnchor}::{layerPath}" +
                  $" to={layerPath} index={legacyLayer.Index}");
              PlaceDefaultOutputLayersBelowSurface(doc, layerPath);
              return legacyLayer.Index;
            }
          }
        }
      }

      Guid parentId = Guid.Empty;
      string builtPath = string.Empty;
      foreach (var segment in layerPath
        .Split(new[] { "::" }, StringSplitOptions.RemoveEmptyEntries)
        .Select(segment => segment.Trim())
        .Where(segment => segment.Length > 0))
      {
        builtPath = builtPath.Length == 0 ? segment : builtPath + "::" + segment;
        int index = doc.Layers.FindByFullPath(
          builtPath, RhinoMath.UnsetIntIndex);
        if (index < 0)
        {
          var layer = new Layer { Name = segment, ParentLayerId = parentId };
          index = doc.Layers.Add(layer);
          if (index < 0)
          {
            Dbg($"layer create failed path={builtPath}");
            return doc.Layers.CurrentLayerIndex;
          }
          Dbg($"layer created path={builtPath} index={index}");
        }

        parentId = doc.Layers[index].Id;
      }

      int resolved = doc.Layers.FindByFullPath(
        layerPath, RhinoMath.UnsetIntIndex);
      PlaceDefaultOutputLayersBelowSurface(doc, layerPath);
      return resolved >= 0 ? resolved : doc.Layers.CurrentLayerIndex;
    }

    private static void PlaceDefaultOutputLayersBelowSurface(RhinoDoc doc, string layerPath)
    {
      var defaultNames = new[]
      {
        DefaultSurfaceLayerName,
        DefaultLabelLayerName,
        DefaultDotLayerName
      };
      if (!defaultNames.Any(name =>
            string.Equals(name, layerPath, StringComparison.OrdinalIgnoreCase)))
        return;

      var activeLayers = doc.Layers
        .Where(layer => layer != null && !layer.IsDeleted)
        .OrderBy(layer => layer.SortIndex)
        .ThenBy(layer => layer.Index)
        .ToList();
      var surfaceLayer = activeLayers.FirstOrDefault(layer =>
        layer.ParentLayerId == Guid.Empty &&
        string.Equals(layer.Name, OutputLayerAnchor, StringComparison.OrdinalIgnoreCase));
      if (surfaceLayer == null)
      {
        var surfaceIndex = doc.Layers.Add(new Layer { Name = OutputLayerAnchor });
        if (surfaceIndex < 0)
          return;
        surfaceLayer = doc.Layers[surfaceIndex];
        activeLayers = doc.Layers
          .Where(layer => layer != null && !layer.IsDeleted)
          .OrderBy(layer => layer.SortIndex)
          .ThenBy(layer => layer.Index)
          .ToList();
      }

      var outputLayers = defaultNames
        .Select(name => activeLayers.FirstOrDefault(layer =>
          layer.ParentLayerId == Guid.Empty &&
          string.Equals(layer.Name, name, StringComparison.OrdinalIgnoreCase)))
        .Where(layer => layer != null)
        .Select(layer => layer!)
        .ToList();
      if (outputLayers.Count == 0)
        return;

      var outputIndices = outputLayers.Select(layer => layer.Index).ToHashSet();
      var sortedIndices = activeLayers
        .Where(layer => !outputIndices.Contains(layer.Index))
        .Select(layer => layer.Index)
        .ToList();
      var anchorPosition = sortedIndices.IndexOf(surfaceLayer.Index);
      if (anchorPosition < 0)
        return;

      sortedIndices.InsertRange(
        anchorPosition + 1,
        outputLayers.Select(layer => layer.Index));
      try
      {
        doc.Layers.Sort(sortedIndices);
        Dbg($"layer order anchor={OutputLayerAnchor}" +
            $" outputs={string.Join(",", outputLayers.Select(layer => layer.Name))}");
      }
      catch (Exception ex)
      {
        Dbg($"layer order failed error={ex.Message}");
      }
    }

    private static bool IsDefaultOutputLayerName(string layerPath)
    {
      return string.Equals(layerPath, DefaultSurfaceLayerName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(layerPath, DefaultLabelLayerName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(layerPath, DefaultDotLayerName, StringComparison.OrdinalIgnoreCase);
    }

    private static void PutOnLayer(RhinoDoc doc, IEnumerable<Guid> ids, int layer)
    {
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
      IList<Guid> outputIds,
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
        TransformObjects(doc, outputIds, transform);
        method = $"markers:{markerCount}";
        return true;
      }

      if (TryTextPlacementTransform(doc, oldIds, newIds, out transform))
      {
        TransformObjects(doc, outputIds, transform);
        method = "text";
        return true;
      }

      if (TryGeometryPlacementTransform(doc, oldIds, newIds, out transform))
      {
        TransformObjects(doc, outputIds, transform);
        method = "geometry";
        return true;
      }

      var oldBox = BoundingBoxOfObjects(doc, oldIds);
      var newBox = BoundingBoxOfObjects(doc, newIds);
      if (!oldBox.HasValue || !oldBox.Value.IsValid ||
          !newBox.HasValue || !newBox.Value.IsValid)
        return false;

      TransformObjects(doc, outputIds,
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
      double height,
      bool addText,
      bool addDots,
      bool transferProperties)
    {
      int groupIndex = FindOriginalGroupIndex(doc, sourceId, partNumber);
      var existingLabels = groupIndex >= 0
        ? (doc.Groups.GroupMembers(groupIndex) ?? Array.Empty<RhinoObject>())
          .Where(obj => obj.Attributes.Name == TextObjectName)
          .ToList()
        : new List<RhinoObject>();
      if (groupIndex >= 0)
      {
        var existing = existingLabels.FirstOrDefault();
        if (addText && existing?.Geometry is TextEntity)
        {
          var n = Unit(frame.Normal, doc.ModelAbsoluteTolerance) ?? Vector3d.ZAxis;
          var lift = TextLift(doc, height, SurfaceTextLiftRatio);
          var replacement = new TextEntity
          {
            PlainText = display,
            Plane = TextPlane(frame.Point + n * lift, frame.Y, n, doc.ModelAbsoluteTolerance),
            TextHeight = height,
            Justification = TextJustification.MiddleCenter,
            Font = Font.FromQuartetProperties(TextFont, false, false)
          };
          if (doc.Objects.Replace(existing.Id, replacement))
          {
            SetMatchNumber(doc, new[] { sourceId, existing.Id }, partNumber);
            PutOnLayer(doc, new[] { existing.Id }, LabelOutputLayerIndex(doc));
            foreach (var extra in existingLabels.Skip(1))
              doc.Objects.Delete(extra.Id, true);
            Dbg($"part={partNumber} original_group reused group={groupIndex}" +
                $" label=updated height={height:G6}");
            return;
          }
        }
        else if (addDots && existing?.Geometry is TextDot)
        {
          if (doc.Objects.Replace(existing.Id, new TextDot(display, frame.Point)))
          {
            SetMatchNumber(doc, new[] { sourceId, existing.Id }, partNumber);
            PutOnLayer(doc, new[] { existing.Id }, LabelOutputLayerIndex(doc));
            foreach (var extra in existingLabels.Skip(1))
              doc.Objects.Delete(extra.Id, true);
            Dbg($"part={partNumber} original_group reused group={groupIndex} label=updated");
            return;
          }
        }

        foreach (var existingLabel in existingLabels)
          doc.Objects.Delete(existingLabel.Id, true);
      }

      Guid labelId = Guid.Empty;
      if (addText)
        labelId = AddFlatText(
          doc, display, frame.Point, frame.Y, frame.Normal,
          height, SurfaceTextLiftRatio, sourceId, transferProperties);
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
        var followingPoint = FollowingPoint(item);
        var candidates = new List<(
          int SurfaceIndex,
          Tuple<double, double> Score,
          Point3d? ClosestPoint)>();

        for (int i = 0; i < surfaces.Count; i++)
        {
          Tuple<double, double>? score;
          Point3d? closestPoint = null;
          if (item.Kind == FollowingKind.Curve)
          {
            score = item.Geometry is Curve curve
              ? CurveScore(surfaces[i].Brep, curve)
              : null;
          }
          else if (followingPoint.HasValue &&
                   TryPointScore(
                     surfaces[i].Brep,
                     followingPoint.Value,
                     out var projectedPoint,
                     out var pointDistance))
          {
            closestPoint = projectedPoint;
            score = Tuple.Create(pointDistance, pointDistance);
          }
          else
          {
            score = null;
          }

          if (score == null)
            continue;
          var limit = AssignTolerance(
            doc,
            surfaces[i].Geometry,
            item.Geometry);
          if (score.Item1 <= limit)
            candidates.Add((i, score, closestPoint));
        }

        if (candidates.Count == 0)
        {
          result.Skipped++;
          result.SkippedIds.Add(item.Id);
          continue;
        }

        candidates.Sort((first, second) =>
          CompareScore(first.Score, second.Score));
        var best = candidates[0];

        if (item.Kind == FollowingKind.Point &&
            best.ClosestPoint.HasValue)
        {
          var sharedTolerance = Math.Max(
            RhinoMath.ZeroTolerance,
            doc.ModelAbsoluteTolerance * SharedPointToleranceFactor);
          var touchingCandidates = candidates
            .Where(candidate =>
              candidate.ClosestPoint.HasValue &&
              candidate.Score.Item1 <= best.Score.Item1 + sharedTolerance &&
              candidate.ClosestPoint.Value.DistanceTo(
                best.ClosestPoint.Value) <= sharedTolerance)
            .ToList();

          foreach (var candidate in touchingCandidates)
          {
            result.Buckets[candidate.SurfaceIndex].Add(
              new FollowingItem(
                item.Id,
                item.Kind,
                new Point(candidate.ClosestPoint!.Value)));
          }

          Dbg($"following_point source={item.Id} input={P(followingPoint!.Value)}" +
              $" assigned_faces={touchingCandidates.Count}" +
              $" surfaces=[{string.Join(",", touchingCandidates.Select(candidate => candidate.SurfaceIndex))}]");
          continue;
        }

        var assigned = item;
        if (item.Kind == FollowingKind.Dot && best.ClosestPoint.HasValue)
          assigned = new FollowingItem(
            item.Id,
            item.Kind,
            DuplicateDotAt((TextDot)item.Geometry, best.ClosestPoint.Value));

        result.Buckets[best.SurfaceIndex].Add(assigned);
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

    private static bool TryPointScore(
      Brep brep,
      Point3d point,
      out Point3d closestPoint,
      out double distance)
    {
      closestPoint = Point3d.Unset;
      distance = double.PositiveInfinity;
      if (brep == null || !point.IsValid)
        return false;

      try
      {
        closestPoint = brep.ClosestPoint(point);
        if (!closestPoint.IsValid)
          return false;
        distance = point.DistanceTo(closestPoint);
        return true;
      }
      catch
      {
        return false;
      }
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

    private static void RotateObjectsToTextUp(RhinoDoc doc, IList<Guid> ids, Point3d center, Vector3d textUp)
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

    private static TextDot TransformTextDotCopy(TextDot dot, Transform transform)
    {
      var copy = dot.Duplicate() as TextDot ?? new TextDot(dot.Text ?? string.Empty, dot.Point);
      copy.Transform(transform);
      return copy;
    }

    private static bool TryMapPointToUnrolledBrep(
      Brep sourceBrep,
      Brep flatBrep,
      Point3d sourcePoint,
      int? preferredEdgeIndex,
      out Point3d flatPoint,
      out int faceIndex)
    {
      flatPoint = Point3d.Unset;
      faceIndex = -1;
      int faceCount = Math.Min(sourceBrep.Faces.Count, flatBrep.Faces.Count);
      if (faceCount == 0) return false;

      IEnumerable<int> candidateFaces = Enumerable.Range(0, faceCount);
      if (preferredEdgeIndex.HasValue &&
          preferredEdgeIndex.Value >= 0 &&
          preferredEdgeIndex.Value < sourceBrep.Edges.Count)
      {
        var adjacent = sourceBrep.Edges[preferredEdgeIndex.Value]
          .AdjacentFaces()
          .Where(index => index >= 0 && index < faceCount)
          .Distinct()
          .ToArray();
        if (adjacent.Length > 0) candidateFaces = adjacent;
      }

      double bestDistance = double.PositiveInfinity;
      bool bestIsOnFace = false;
      foreach (int index in candidateFaces)
      {
        var sourceFace = sourceBrep.Faces[index];
        if (!sourceFace.ClosestPoint(sourcePoint, out double u, out double v))
          continue;

        var sourceCandidate = sourceFace.PointAt(u, v);
        if (!sourceCandidate.IsValid) continue;
        bool isOnFace;
        try
        {
          isOnFace = sourceFace.IsPointOnFace(u, v) != PointFaceRelation.Exterior;
        }
        catch
        {
          isOnFace = true;
        }

        double distance = sourceCandidate.DistanceToSquared(sourcePoint);
        if (faceIndex >= 0 &&
            (bestIsOnFace && !isOnFace || bestIsOnFace == isOnFace && distance >= bestDistance))
          continue;

        var flatSurface = flatBrep.Faces[index].UnderlyingSurface();
        if (flatSurface == null) continue;
        var candidate = flatSurface.PointAt(u, v);
        if (!candidate.IsValid) continue;

        flatPoint = candidate;
        faceIndex = index;
        bestDistance = distance;
        bestIsOnFace = isOnFace;
      }

      return faceIndex >= 0 && flatPoint.IsValid;
    }

    private static bool TryMapPointToFlatEdge(
      Brep sourceBrep,
      Brep flatBrep,
      Point3d sourcePoint,
      int sourceEdgeIndex,
      int mappedFaceIndex,
      Point3d provisionalPoint,
      out Point3d flatPoint,
      out int flatEdgeIndex)
    {
      flatPoint = Point3d.Unset;
      flatEdgeIndex = -1;
      if (sourceEdgeIndex < 0 || sourceEdgeIndex >= sourceBrep.Edges.Count)
        return false;

      var sourceEdge = sourceBrep.Edges[sourceEdgeIndex];
      if (!sourceEdge.ClosestPoint(sourcePoint, out double sourceParameter))
        return false;

      double sourceLength = sourceEdge.GetLength();
      if (sourceLength <= RhinoMath.ZeroTolerance)
        return false;

      double fraction;
      try
      {
        fraction = sourceEdge.GetLength(new Interval(sourceEdge.Domain.T0, sourceParameter)) /
                   sourceLength;
      }
      catch
      {
        fraction = sourceEdge.Domain.NormalizedParameterAt(sourceParameter);
      }
      fraction = Math.Max(0.0, Math.Min(1.0, fraction));

      var candidateIndices = CorrespondingFlatEdgeIndices(
        sourceBrep, flatBrep, sourceEdgeIndex, mappedFaceIndex);
      bool hasTopologyMatch = candidateIndices.Count > 0;
      if (!hasTopologyMatch)
      {
        candidateIndices = Enumerable.Range(0, flatBrep.Edges.Count)
          .Where(index => mappedFaceIndex < 0 ||
            flatBrep.Edges[index].AdjacentFaces().Contains(mappedFaceIndex))
          .ToList();
      }
      if (candidateIndices.Count == 0)
        candidateIndices = Enumerable.Range(0, flatBrep.Edges.Count).ToList();

      double scale = Math.Max(flatBrep.GetBoundingBox(true).Diagonal.Length, sourceLength);
      double bestScore = double.PositiveInfinity;
      foreach (int index in candidateIndices)
      {
        var edge = flatBrep.Edges[index];
        double edgeLength = edge.GetLength();
        if (edgeLength <= RhinoMath.ZeroTolerance)
          continue;

        double lengthError = Math.Abs(edgeLength - sourceLength) / sourceLength;
        if (!hasTopologyMatch && lengthError > 0.05)
          continue;

        Point3d forward = PointAtNormalizedEdgeLength(edge, fraction);
        Point3d reverse = PointAtNormalizedEdgeLength(edge, 1.0 - fraction);
        Point3d candidate = forward.DistanceToSquared(provisionalPoint) <=
                            reverse.DistanceToSquared(provisionalPoint)
          ? forward
          : reverse;
        double score = candidate.DistanceTo(provisionalPoint) + lengthError * scale * 5.0;
        if (!hasTopologyMatch && index == sourceEdgeIndex)
          score *= 0.1;
        if (score >= bestScore)
          continue;

        bestScore = score;
        flatPoint = candidate;
        flatEdgeIndex = index;
      }

      return flatEdgeIndex >= 0 && flatPoint.IsValid;
    }

    private static List<int> CorrespondingFlatEdgeIndices(
      Brep sourceBrep,
      Brep flatBrep,
      int sourceEdgeIndex,
      int mappedFaceIndex)
    {
      var result = new List<int>();
      if (mappedFaceIndex < 0 ||
          mappedFaceIndex >= sourceBrep.Faces.Count ||
          mappedFaceIndex >= flatBrep.Faces.Count)
        return result;

      var sourceFace = sourceBrep.Faces[mappedFaceIndex];
      var flatFace = flatBrep.Faces[mappedFaceIndex];
      int loopCount = Math.Min(sourceFace.Loops.Count, flatFace.Loops.Count);
      for (int loopIndex = 0; loopIndex < loopCount; loopIndex++)
      {
        var sourceLoop = sourceFace.Loops[loopIndex];
        var flatLoop = flatFace.Loops[loopIndex];
        int trimCount = Math.Min(sourceLoop.Trims.Count, flatLoop.Trims.Count);
        for (int trimIndex = 0; trimIndex < trimCount; trimIndex++)
        {
          if (sourceLoop.Trims[trimIndex].Edge?.EdgeIndex != sourceEdgeIndex)
            continue;
          var flatEdge = flatLoop.Trims[trimIndex].Edge;
          if (flatEdge != null && !result.Contains(flatEdge.EdgeIndex))
            result.Add(flatEdge.EdgeIndex);
        }
      }
      return result;
    }

    private static bool IsNativeUnrollLabel(TextDot dot)
    {
      string text = dot.Text ?? string.Empty;
      return !string.IsNullOrWhiteSpace(text) &&
             !text.StartsWith(LabelHelperDotPrefix, StringComparison.Ordinal) &&
             !text.StartsWith(EdgeMateHelperDotPrefix, StringComparison.Ordinal) &&
             !text.StartsWith(CurveHelperDotPrefix, StringComparison.Ordinal) &&
             !text.StartsWith(UserPointHelperDotPrefix, StringComparison.Ordinal) &&
             !text.StartsWith(UserDotHelperDotPrefix, StringComparison.Ordinal) &&
             !text.StartsWith(NativeFaceHelperDotPrefix, StringComparison.Ordinal);
    }

    private static Point3d? FaceInteriorPoint(BrepFace face, double tolerance)
    {
      var faceBrep = face.DuplicateFace(false);
      if (faceBrep == null || !faceBrep.IsValid)
        return null;
      using var area = AreaMassProperties.Compute(faceBrep);
      var target = area?.Centroid ?? faceBrep.GetBoundingBox(true).Center;
      return InteriorLabelPoint(faceBrep, target, tolerance);
    }

    private static List<NativeInternalMate> ReconstructNativeFlatFaces(
      Brep sourceBrep,
      Brep[] flatBreps,
      Curve[] flatCurves,
      Point3d[] flatPoints,
      TextDot[] flatDots,
      IReadOnlyDictionary<string, int> faceHelperDots,
      NativeMateIdAllocator mateIds,
      int partNumber,
      double tolerance,
      bool reconstructFaces)
    {
      var faceToBrep = MapSourceFacesToFlatBreps(
        sourceBrep, flatBreps, flatDots, faceHelperDots, partNumber);
      var seamPairs = NativeLabelSeamPairs(
        flatBreps, flatDots, mateIds, partNumber, tolerance);
      foreach (var sourceEdge in sourceBrep.Edges)
      {
        var adjacentFaces = sourceEdge.AdjacentFaces().Distinct().ToArray();
        if (adjacentFaces.Length != 2)
          continue;
        var first = TryMappedSourceFaceEdge(
          sourceBrep,
          adjacentFaces[0],
          sourceEdge.EdgeIndex,
          flatBreps,
          faceToBrep);
        var second = TryMappedSourceFaceEdge(
          sourceBrep,
          adjacentFaces[1],
          sourceEdge.EdgeIndex,
          flatBreps,
          faceToBrep);
        if (first == null || second == null || first.BrepIndex == second.BrepIndex)
        {
          Dbg($"part={partNumber} native_seam source_edge={sourceEdge.EdgeIndex}" +
              $" faces={adjacentFaces[0]},{adjacentFaces[1]} mapped=false");
          continue;
        }

        bool alreadyMapped = seamPairs.Any(pair =>
          IsSameFlatEdgePair(pair.First, pair.Second, first, second));
        if (alreadyMapped)
          continue;

        double longest = Math.Max(first.Length, second.Length);
        double lengthError = longest > RhinoMath.ZeroTolerance
          ? Math.Abs(first.Length - second.Length) / longest
          : 0.0;
        seamPairs.Add(new NativeSeamPair(
          $"E{sourceEdge.EdgeIndex}",
          mateIds.Next(partNumber),
          first,
          second,
          lengthError));
      }

      if (seamPairs.Count == 0)
      {
        Dbg($"part={partNumber} native_seams count=0" +
            $" source_faces={sourceBrep.Faces.Count}" +
            $" mapped_faces={faceToBrep.Count}");
        return new List<NativeInternalMate>();
      }

      Dbg($"part={partNumber} native_seams count={seamPairs.Count}" +
          $" source_faces={sourceBrep.Faces.Count}" +
          $" mapped_faces={faceToBrep.Count}");

      var transforms = Enumerable.Repeat(Transform.Identity, flatBreps.Length).ToArray();
      if (reconstructFaces)
      {
        var placed = new bool[flatBreps.Length];
        for (int root = 0; root < flatBreps.Length; root++)
        {
          if (placed[root])
            continue;
          placed[root] = true;
          var queue = new Queue<int>();
          queue.Enqueue(root);

          while (queue.Count > 0)
          {
            int fixedBrepIndex = queue.Dequeue();
            foreach (var pair in seamPairs)
            {
              NativeLabelEdge fixedEdge;
              NativeLabelEdge movingEdge;
              if (pair.First.BrepIndex == fixedBrepIndex)
              {
                fixedEdge = pair.First;
                movingEdge = pair.Second;
              }
              else if (pair.Second.BrepIndex == fixedBrepIndex)
              {
                fixedEdge = pair.Second;
                movingEdge = pair.First;
              }
              else
              {
                continue;
              }

              if (placed[movingEdge.BrepIndex])
                continue;
              var fixedWorld = TransformNativeLabelEdge(
                fixedEdge, transforms[fixedBrepIndex]);
              bool reverseMoving = ResolveCornerReversal(fixedWorld, movingEdge);
              var targetStart = reverseMoving ? fixedWorld.End : fixedWorld.Start;
              var targetEnd = reverseMoving ? fixedWorld.Start : fixedWorld.End;
              if (!TryPlanarEdgeTransform(
                    movingEdge.Start,
                    movingEdge.End,
                    targetStart,
                    targetEnd,
                    out var transform))
                continue;

              transforms[movingEdge.BrepIndex] = transform;
              placed[movingEdge.BrepIndex] = true;
              pair.Reversed = reverseMoving;
              queue.Enqueue(movingEdge.BrepIndex);
              Dbg($"part={partNumber} native_reconstruct label={pair.Label}" +
                  $" fixed={fixedBrepIndex}:{fixedEdge.EdgeIndex}" +
                  $" moved={movingEdge.BrepIndex}:{movingEdge.EdgeIndex}" +
                  $" reversed={reverseMoving}");
            }
          }
        }
      }
      else
      {
        foreach (var pair in seamPairs)
          pair.Reversed = ResolveCornerReversal(pair.First, pair.Second);
      }

      var curveOwners = flatCurves
        .Select(curve => ClosestFlatBrep(
          flatBreps, curve.PointAtNormalizedLength(0.5)))
        .ToArray();
      var pointOwners = flatPoints
        .Select(point => ClosestFlatBrep(flatBreps, point))
        .ToArray();
      var dotOwners = flatDots
        .Select(dot => ClosestFlatBrep(flatBreps, dot.Point))
        .ToArray();

      if (reconstructFaces)
      {
        for (int brepIndex = 0; brepIndex < flatBreps.Length; brepIndex++)
          flatBreps[brepIndex].Transform(transforms[brepIndex]);
        for (int curveIndex = 0; curveIndex < flatCurves.Length; curveIndex++)
        {
          int owner = curveOwners[curveIndex];
          if (owner >= 0)
            flatCurves[curveIndex].Transform(transforms[owner]);
        }
        for (int pointIndex = 0; pointIndex < flatPoints.Length; pointIndex++)
        {
          int owner = pointOwners[pointIndex];
          if (owner < 0)
            continue;
          var point = flatPoints[pointIndex];
          point.Transform(transforms[owner]);
          flatPoints[pointIndex] = point;
        }
        for (int dotIndex = 0; dotIndex < flatDots.Length; dotIndex++)
        {
          int owner = dotOwners[dotIndex];
          if (owner >= 0)
            flatDots[dotIndex].Transform(transforms[owner]);
        }
      }

      var result = new List<NativeInternalMate>();
      foreach (var pair in seamPairs)
      {
        var firstEdge = flatBreps[pair.First.BrepIndex].Edges[pair.First.EdgeIndex];
        var secondEdge = flatBreps[pair.Second.BrepIndex].Edges[pair.Second.EdgeIndex];
        result.Add(new NativeInternalMate(
          pair.MateId,
          PointAtNormalizedEdgeLength(firstEdge, 0.5),
          PointAtNormalizedEdgeLength(secondEdge, 0.5),
          pair.Reversed));
        Dbg($"part={partNumber} native_seam label={pair.Label} id={pair.MateId}" +
            $" first={pair.First.BrepIndex}:{pair.First.EdgeIndex}" +
            $" second={pair.Second.BrepIndex}:{pair.Second.EdgeIndex}" +
            $" length_error={pair.LengthError:G6} reversed={pair.Reversed}");
      }
      return result;
    }

    private static bool NativeMateWasJoined(
      IEnumerable<Brep> joinedBreps,
      NativeInternalMate mate,
      double tolerance)
    {
      double maximumDistance = Math.Max(
        tolerance * NativeJoinedSeamToleranceFactor,
        RhinoMath.ZeroTolerance);
      foreach (var edge in joinedBreps
        .Where(brep => brep != null)
        .SelectMany(brep => brep.Edges)
        .Where(edge => edge.Valence == EdgeAdjacency.Interior))
      {
        if (!edge.ClosestPoint(mate.PointA, out double firstParameter) ||
            mate.PointA.DistanceTo(edge.PointAt(firstParameter)) > maximumDistance ||
            !edge.ClosestPoint(mate.PointB, out double secondParameter) ||
            mate.PointB.DistanceTo(edge.PointAt(secondParameter)) > maximumDistance)
          continue;
        return true;
      }
      return false;
    }

    private static List<NativeSeamPair> NativeLabelSeamPairs(
      IReadOnlyList<Brep> flatBreps,
      IEnumerable<TextDot> flatDots,
      NativeMateIdAllocator mateIds,
      int partNumber,
      double tolerance)
    {
      var nativeDots = flatDots.Where(IsNativeUnrollLabel).ToList();
      Dbg($"part={partNumber} native_labels count={nativeDots.Count}" +
          $" texts=[{string.Join(",", nativeDots.Select(dot => dot.Text ?? string.Empty))}]");
      var result = new List<NativeSeamPair>();
      foreach (var labelGroup in nativeDots
        .GroupBy(dot => dot.Text ?? string.Empty, StringComparer.Ordinal)
        .OrderBy(group => group.Key, StringComparer.Ordinal))
      {
        var candidates = new List<(NativeLabelEdge Edge, double Distance)>();
        foreach (var dot in labelGroup)
        {
          if (TryNativeLabelEdge(
                flatBreps, dot.Point, tolerance,
                out var edge, out double distance))
            candidates.Add((edge, distance));
        }

        var unused = new HashSet<int>(Enumerable.Range(0, candidates.Count));
        while (unused.Count >= 2)
        {
          (int First, int Second, double Score)? best = null;
          foreach (int firstIndex in unused)
          {
            foreach (int secondIndex in unused)
            {
              if (secondIndex <= firstIndex ||
                  candidates[firstIndex].Edge.BrepIndex ==
                    candidates[secondIndex].Edge.BrepIndex)
                continue;
              double candidateLongest = Math.Max(
                candidates[firstIndex].Edge.Length,
                candidates[secondIndex].Edge.Length);
              if (candidateLongest <= RhinoMath.ZeroTolerance)
                continue;
              double candidateLengthError = Math.Abs(
                candidates[firstIndex].Edge.Length -
                candidates[secondIndex].Edge.Length) / candidateLongest;
              double score = candidates[firstIndex].Distance +
                candidates[secondIndex].Distance +
                candidateLengthError * candidateLongest;
              if (!best.HasValue || score < best.Value.Score)
                best = (firstIndex, secondIndex, score);
            }
          }

          if (!best.HasValue)
            break;
          unused.Remove(best.Value.First);
          unused.Remove(best.Value.Second);
          var first = candidates[best.Value.First].Edge;
          var second = candidates[best.Value.Second].Edge;
          double pairLongest = Math.Max(first.Length, second.Length);
          double pairLengthError = pairLongest > RhinoMath.ZeroTolerance
            ? Math.Abs(first.Length - second.Length) / pairLongest
            : 0.0;
          result.Add(new NativeSeamPair(
            labelGroup.Key,
            mateIds.Next(partNumber),
            first,
            second,
            pairLengthError));
          Dbg($"part={partNumber} native_label_pair label={labelGroup.Key}" +
              $" first={first.BrepIndex}:{first.EdgeIndex}" +
              $" second={second.BrepIndex}:{second.EdgeIndex}" +
              $" score={best.Value.Score:G6}");
        }
      }
      return result;
    }

    private static bool IsSameFlatEdgePair(
      NativeLabelEdge firstA,
      NativeLabelEdge secondA,
      NativeLabelEdge firstB,
      NativeLabelEdge secondB)
    {
      bool Same(NativeLabelEdge first, NativeLabelEdge second) =>
        first.BrepIndex == second.BrepIndex &&
        first.EdgeIndex == second.EdgeIndex;
      return Same(firstA, firstB) && Same(secondA, secondB) ||
             Same(firstA, secondB) && Same(secondA, firstB);
    }

    private static Dictionary<int, int> MapSourceFacesToFlatBreps(
      Brep sourceBrep,
      IReadOnlyList<Brep> flatBreps,
      IEnumerable<TextDot> flatDots,
      IReadOnlyDictionary<string, int> faceHelperDots,
      int partNumber)
    {
      var result = new Dictionary<int, int>();
      var usedBreps = new HashSet<int>();
      foreach (var dot in flatDots)
      {
        string text = dot.Text ?? string.Empty;
        if (!faceHelperDots.TryGetValue(text, out int sourceFaceIndex))
          continue;
        int flatBrepIndex = ClosestFlatBrep(flatBreps, dot.Point);
        if (flatBrepIndex < 0)
          continue;
        if (result.TryGetValue(sourceFaceIndex, out int existing) &&
            existing == flatBrepIndex)
          continue;
        if (usedBreps.Contains(flatBrepIndex))
        {
          Dbg($"part={partNumber} face_map source_face={sourceFaceIndex}" +
              $" flat_brep={flatBrepIndex} collision=true");
          continue;
        }
        result[sourceFaceIndex] = flatBrepIndex;
        usedBreps.Add(flatBrepIndex);
        Dbg($"part={partNumber} face_map source_face={sourceFaceIndex}" +
            $" flat_brep={flatBrepIndex} method=helper");
      }

      foreach (var sourceFace in sourceBrep.Faces)
      {
        if (result.ContainsKey(sourceFace.FaceIndex))
          continue;
        int bestBrep = -1;
        double bestScore = double.PositiveInfinity;
        for (int flatBrepIndex = 0; flatBrepIndex < flatBreps.Count; flatBrepIndex++)
        {
          if (usedBreps.Contains(flatBrepIndex))
            continue;
          double score = FaceTopologyScore(sourceFace, flatBreps[flatBrepIndex]);
          if (score >= bestScore)
            continue;
          bestScore = score;
          bestBrep = flatBrepIndex;
        }
        if (bestBrep < 0)
          continue;
        result[sourceFace.FaceIndex] = bestBrep;
        usedBreps.Add(bestBrep);
        Dbg($"part={partNumber} face_map source_face={sourceFace.FaceIndex}" +
            $" flat_brep={bestBrep} method=topology score={bestScore:G6}");
      }

      return result;
    }

    private static double FaceTopologyScore(BrepFace sourceFace, Brep flatBrep)
    {
      var sourceLengths = sourceFace.Loops
        .SelectMany(loop => loop.Trims)
        .Select(trim => trim.Edge?.GetLength() ?? 0.0)
        .Where(length => length > RhinoMath.ZeroTolerance)
        .OrderBy(length => length)
        .ToArray();
      var flatLengths = flatBrep.Faces
        .SelectMany(face => face.Loops)
        .SelectMany(loop => loop.Trims)
        .Select(trim => trim.Edge?.GetLength() ?? 0.0)
        .Where(length => length > RhinoMath.ZeroTolerance)
        .OrderBy(length => length)
        .ToArray();
      if (sourceLengths.Length == 0 || flatLengths.Length == 0)
        return double.PositiveInfinity;

      double score = Math.Abs(sourceLengths.Length - flatLengths.Length) * 10.0;
      int count = Math.Min(sourceLengths.Length, flatLengths.Length);
      for (int index = 0; index < count; index++)
      {
        double longest = Math.Max(sourceLengths[index], flatLengths[index]);
        if (longest > RhinoMath.ZeroTolerance)
          score += Math.Abs(sourceLengths[index] - flatLengths[index]) / longest;
      }
      return score;
    }

    private static NativeLabelEdge? TryMappedSourceFaceEdge(
      Brep sourceBrep,
      int sourceFaceIndex,
      int sourceEdgeIndex,
      IReadOnlyList<Brep> flatBreps,
      IReadOnlyDictionary<int, int> faceToBrep)
    {
      if (sourceFaceIndex < 0 || sourceFaceIndex >= sourceBrep.Faces.Count ||
          !faceToBrep.TryGetValue(sourceFaceIndex, out int flatBrepIndex) ||
          flatBrepIndex < 0 || flatBrepIndex >= flatBreps.Count)
        return null;

      var sourceFace = sourceBrep.Faces[sourceFaceIndex];
      var flatBrep = flatBreps[flatBrepIndex];
      if (flatBrep.Faces.Count == 0)
        return null;
      if (sourceEdgeIndex < 0 || sourceEdgeIndex >= sourceBrep.Edges.Count)
        return null;
      double sourceLength = sourceBrep.Edges[sourceEdgeIndex].GetLength();
      var flatFace = flatBrep.Faces[0];
      int loopCount = Math.Min(sourceFace.Loops.Count, flatFace.Loops.Count);
      for (int loopIndex = 0; loopIndex < loopCount; loopIndex++)
      {
        var sourceLoop = sourceFace.Loops[loopIndex];
        var flatLoop = flatFace.Loops[loopIndex];
        int trimCount = Math.Min(sourceLoop.Trims.Count, flatLoop.Trims.Count);
        for (int trimIndex = 0; trimIndex < trimCount; trimIndex++)
        {
          if (sourceLoop.Trims[trimIndex].Edge?.EdgeIndex != sourceEdgeIndex)
            continue;
          var flatEdge = flatLoop.Trims[trimIndex].Edge;
          if (flatEdge != null && RelativeLengthError(
                sourceLength, flatEdge.GetLength()) <=
              NativeSeamMaxRelativeLengthError)
            return NativeFlatEdge(flatBrepIndex, flatBrep, flatEdge);
        }
      }

      var fallback = flatFace.Loops
        .SelectMany(loop => loop.Trims)
        .Select(trim => trim.Edge)
        .Where(edge => edge != null)
        .Cast<BrepEdge>()
        .Distinct()
        .OrderBy(edge => Math.Abs(edge.GetLength() - sourceLength))
        .FirstOrDefault();
      return fallback == null ||
             RelativeLengthError(sourceLength, fallback.GetLength()) >
               NativeSeamMaxRelativeLengthError
        ? null
        : NativeFlatEdge(flatBrepIndex, flatBrep, fallback);
    }

    private static double RelativeLengthError(double first, double second)
    {
      double longest = Math.Max(Math.Abs(first), Math.Abs(second));
      return longest > RhinoMath.ZeroTolerance
        ? Math.Abs(first - second) / longest
        : 0.0;
    }

    private static NativeLabelEdge NativeFlatEdge(
      int flatBrepIndex,
      Brep flatBrep,
      BrepEdge flatEdge)
    {
      using var area = AreaMassProperties.Compute(flatBrep);
      var center = area?.Centroid ?? flatBrep.GetBoundingBox(true).Center;
      return new NativeLabelEdge(
        flatBrepIndex,
        flatEdge.EdgeIndex,
        flatEdge.PointAtStart,
        flatEdge.PointAtEnd,
        center,
        flatEdge.GetLength());
    }

    private static NativeLabelEdge TransformNativeLabelEdge(
      NativeLabelEdge edge,
      Transform transform)
    {
      var start = edge.Start;
      var end = edge.End;
      var center = edge.Center;
      start.Transform(transform);
      end.Transform(transform);
      center.Transform(transform);
      return new NativeLabelEdge(
        edge.BrepIndex,
        edge.EdgeIndex,
        start,
        end,
        center,
        edge.Length);
    }

    private static int ClosestFlatBrep(
      IReadOnlyList<Brep> flatBreps,
      Point3d point)
    {
      int bestIndex = -1;
      double bestDistance = double.PositiveInfinity;
      for (int brepIndex = 0; brepIndex < flatBreps.Count; brepIndex++)
      {
        Point3d closest;
        try
        {
          closest = flatBreps[brepIndex].ClosestPoint(point);
        }
        catch
        {
          continue;
        }
        double distance = point.DistanceToSquared(closest);
        if (distance >= bestDistance)
          continue;
        bestDistance = distance;
        bestIndex = brepIndex;
      }
      return bestIndex;
    }

    private static bool TryNativeLabelEdge(
      IReadOnlyList<Brep> flatBreps,
      Point3d labelPoint,
      double tolerance,
      out NativeLabelEdge edgeResult,
      out double distanceResult)
    {
      edgeResult = null!;
      distanceResult = double.PositiveInfinity;
      NativeLabelEdge? best = null;
      for (int brepIndex = 0; brepIndex < flatBreps.Count; brepIndex++)
      {
        var brep = flatBreps[brepIndex];
        foreach (var edge in brep.Edges)
        {
          if (!edge.ClosestPoint(labelPoint, out double parameter))
            continue;
          double distance = labelPoint.DistanceTo(edge.PointAt(parameter));
          if (distance >= distanceResult)
            continue;
          distanceResult = distance;
          best = NativeFlatEdge(brepIndex, brep, edge);
        }
      }

      if (best == null)
        return false;
      double scale = flatBreps[best.BrepIndex].GetBoundingBox(true).Diagonal.Length;
      double limit = Math.Max(
        tolerance * NativeLabelEdgeToleranceFactor,
        scale * EdgeMateDiagFactor);
      if (distanceResult > limit)
        return false;
      edgeResult = best;
      return true;
    }

    private static bool ResolveCornerReversal(
      NativeLabelEdge fixedEdge,
      NativeLabelEdge movingEdge)
    {
      double sameScore = CornerAlignmentScore(
        fixedEdge,
        movingEdge,
        reverseMoving: false);
      double reversedScore = CornerAlignmentScore(
        fixedEdge,
        movingEdge,
        reverseMoving: true);
      return reversedScore < sameScore;
    }

    private static double CornerAlignmentScore(
      NativeLabelEdge fixedEdge,
      NativeLabelEdge movingEdge,
      bool reverseMoving)
    {
      var targetStart = reverseMoving ? fixedEdge.End : fixedEdge.Start;
      var targetEnd = reverseMoving ? fixedEdge.Start : fixedEdge.End;
      if (!TryPlanarEdgeTransform(
            movingEdge.Start,
            movingEdge.End,
            targetStart,
            targetEnd,
            out var transform))
        return double.PositiveInfinity;

      var movedStart = movingEdge.Start;
      var movedEnd = movingEdge.End;
      var movedCenter = movingEdge.Center;
      movedStart.Transform(transform);
      movedEnd.Transform(transform);
      movedCenter.Transform(transform);

      double endpointError =
        movedStart.DistanceTo(targetStart) + movedEnd.DistanceTo(targetEnd);
      double fixedSide = PlanarSide(
        fixedEdge.Start, fixedEdge.End, fixedEdge.Center);
      double movingSide = PlanarSide(
        fixedEdge.Start, fixedEdge.End, movedCenter);
      double sidePenalty = fixedSide * movingSide < 0.0
        ? 0.0
        : Math.Max(fixedEdge.Length, movingEdge.Length);
      return endpointError + sidePenalty;
    }

    private static bool TryPlanarEdgeTransform(
      Point3d movingStart,
      Point3d movingEnd,
      Point3d targetStart,
      Point3d targetEnd,
      out Transform transform)
    {
      transform = Transform.Identity;
      var movingDirection = movingEnd - movingStart;
      var targetDirection = targetEnd - targetStart;
      movingDirection.Z = 0.0;
      targetDirection.Z = 0.0;
      if (!movingDirection.Unitize() || !targetDirection.Unitize())
        return false;

      double angle = Math.Atan2(
        movingDirection.X * targetDirection.Y -
          movingDirection.Y * targetDirection.X,
        movingDirection.X * targetDirection.X +
          movingDirection.Y * targetDirection.Y);
      var rotation = Transform.Rotation(angle, Vector3d.ZAxis, movingStart);
      var rotatedStart = movingStart;
      rotatedStart.Transform(rotation);
      transform = Transform.Translation(targetStart - rotatedStart) * rotation;
      return transform.IsValid;
    }

    private static double PlanarSide(
      Point3d edgeStart,
      Point3d edgeEnd,
      Point3d point)
    {
      return (edgeEnd.X - edgeStart.X) * (point.Y - edgeStart.Y) -
             (edgeEnd.Y - edgeStart.Y) * (point.X - edgeStart.X);
    }
    private static Point3d PointAtNormalizedEdgeLength(Curve edge, double fraction)
    {
      fraction = Math.Max(0.0, Math.Min(1.0, fraction));
      try
      {
        if (edge.NormalizedLengthParameter(fraction, out double parameter))
          return edge.PointAt(parameter);
      }
      catch
      {
      }
      return edge.PointAt(edge.Domain.ParameterAt(fraction));
    }

    private static void TransformObjects(RhinoDoc doc, IList<Guid> ids, Transform transform)
    {
      for (int index = 0; index < ids.Count; index++)
      {
        var transformedId = doc.Objects.Transform(ids[index], transform, true);
        if (transformedId != Guid.Empty)
          ids[index] = transformedId;
      }
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

        if (brep != null)
        {
          double clearance = BoundaryClearance(brep, pt.Value);
          double halfWidthFactor = Math.Max(0.5, display.Length * 0.65 * 0.5);
          if (clearance > tol)
            caps.Add(clearance * 0.80 / (TextHeightScale * halfWidthFactor));
        }
      }

      if (caps.Count > 0) height = Math.Min(height, caps.Min());
      return Math.Max(height, tol * 8.0) * TextHeightScale;
    }

    private static double FitFlatLabelHeight(
      IEnumerable<Brep> flatBreps,
      string display,
      Point3d point,
      Vector3d yDirection,
      double requestedHeight,
      double tolerance)
    {
      var breps = flatBreps?.Where(brep => brep != null).ToList() ?? new List<Brep>();
      if (breps.Count == 0 || requestedHeight <= tolerance)
        return requestedHeight;

      var y = yDirection;
      y.Z = 0.0;
      if (!y.Unitize()) y = Vector3d.YAxis;
      var x = Vector3d.CrossProduct(y, Vector3d.ZAxis);
      if (!x.Unitize()) x = Vector3d.XAxis;

      var unitBounds = UnitTextBounds(display);
      double minimum = Math.Max(tolerance * 8.0, RhinoMath.ZeroTolerance * 10.0);
      bool Fits(double height)
      {
        const int divisions = 6; // Candidate subdivisions per text-placement search axis; one or greater.
        const double margin = 0.88; // Fraction of the usable interior region searched for label placement.
        double minX = unitBounds.Min.X * height / margin;
        double maxX = unitBounds.Max.X * height / margin;
        double minY = unitBounds.Min.Y * height / margin;
        double maxY = unitBounds.Max.Y * height / margin;
        for (int i = 0; i <= divisions; i++)
        {
          double f = i / (double)divisions;
          double sampleX = minX + (maxX - minX) * f;
          double sampleY = minY + (maxY - minY) * f;
          var samples = new[]
          {
            point + x * sampleX + y * minY,
            point + x * sampleX + y * maxY,
            point + x * minX + y * sampleY,
            point + x * maxX + y * sampleY
          };
          if (samples.Any(sample => !FlatBrepsContainPoint(breps, sample, tolerance)))
            return false;
        }
        return FlatBrepsContainPoint(breps, point, tolerance);
      }

      if (Fits(requestedHeight))
        return requestedHeight;
      if (!Fits(minimum))
        return minimum;

      double low = minimum;
      double high = requestedHeight;
      for (int i = 0; i < 24; i++)
      {
        double mid = (low + high) * 0.5;
        if (Fits(mid)) low = mid;
        else high = mid;
      }
      return low;
    }

    private static BoundingBox UnitTextBounds(string display)
    {
      try
      {
        using var text = new TextEntity
        {
          PlainText = display,
          Plane = Plane.WorldXY,
          TextHeight = 1.0,
          Justification = TextJustification.MiddleCenter,
          Font = Font.FromQuartetProperties(TextFont, false, false)
        };
        var bounds = text.GetBoundingBox(true);
        if (bounds.IsValid && bounds.Diagonal.Length > RhinoMath.ZeroTolerance)
          return bounds;
      }
      catch
      {
      }

      double halfWidth = Math.Max(0.65, display.Length * 0.65) * 0.5;
      return new BoundingBox(
        new Point3d(-halfWidth, -0.5, 0.0),
        new Point3d(halfWidth, 0.5, 0.0));
    }

    private static bool FlatBrepsContainPoint(
      IEnumerable<Brep> breps,
      Point3d point,
      double tolerance)
    {
      double distanceTolerance = Math.Max(tolerance * 10.0, RhinoMath.ZeroTolerance);
      foreach (var brep in breps)
      {
        foreach (var face in brep.Faces)
        {
          if (!face.ClosestPoint(point, out double u, out double v))
            continue;
          if (face.PointAt(u, v).DistanceTo(point) > distanceTolerance)
            continue;
          try
          {
            if (face.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
              return true;
          }
          catch
          {
            return true;
          }
        }
      }
      return false;
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

    private sealed class NativeInternalMate
    {
      public string MateId { get; }
      public Point3d PointA { get; }
      public Point3d PointB { get; }
      public bool Reversed { get; }

      public NativeInternalMate(
        string mateId,
        Point3d pointA,
        Point3d pointB,
        bool reversed)
      {
        MateId = mateId;
        PointA = pointA;
        PointB = pointB;
        Reversed = reversed;
      }
    }

    private sealed class NativeSeamPair
    {
      public string Label { get; }
      public string MateId { get; }
      public NativeLabelEdge First { get; }
      public NativeLabelEdge Second { get; }
      public double LengthError { get; }
      public bool Reversed { get; set; }

      public NativeSeamPair(
        string label,
        string mateId,
        NativeLabelEdge first,
        NativeLabelEdge second,
        double lengthError)
      {
        Label = label;
        MateId = mateId;
        First = first;
        Second = second;
        LengthError = lengthError;
      }
    }

    private sealed class NativeLabelEdge
    {
      public int BrepIndex { get; }
      public int EdgeIndex { get; }
      public Point3d Start { get; }
      public Point3d End { get; }
      public Point3d Center { get; }
      public double Length { get; }

      public NativeLabelEdge(
        int brepIndex,
        int edgeIndex,
        Point3d start,
        Point3d end,
        Point3d center,
        double length)
      {
        BrepIndex = brepIndex;
        EdgeIndex = edgeIndex;
        Start = start;
        End = end;
        Center = center;
        Length = length;
      }
    }

    private sealed class NativeMateIdAllocator
    {
      private readonly Dictionary<int, Queue<string>> _reusableByPart;
      private readonly HashSet<string> _used;
      private int _nextSequence;

      public NativeMateIdAllocator(
        RhinoDoc doc,
        IReadOnlyList<List<EdgeMateRecord>>? edgePairs)
      {
        var existing = ExistingEdgeMates(doc);
        _used = new HashSet<string>(
          edgePairs?.SelectMany(records => records).Select(record => record.MateId) ??
            Enumerable.Empty<string>(),
          StringComparer.OrdinalIgnoreCase);
        _nextSequence = existing
          .Select(mate => MateSequence(mate.MateId))
          .Concat(_used.Select(MateSequence))
          .DefaultIfEmpty(0)
          .Max() + 1;
        _reusableByPart = existing
          .Where(mate =>
            mate.PartNumber == mate.MatePartNumber &&
            !_used.Contains(mate.MateId))
          .GroupBy(mate => mate.PartNumber)
          .ToDictionary(
            group => group.Key,
            group => new Queue<string>(
              group
                .GroupBy(mate => mate.MateId, StringComparer.OrdinalIgnoreCase)
                .OrderBy(idGroup => MateSequence(idGroup.Key))
                .Select(idGroup => idGroup.Key)));
      }

      public string Next(int partNumber)
      {
        if (_reusableByPart.TryGetValue(partNumber, out var reusable))
        {
          while (reusable.Count > 0)
          {
            string mateId = reusable.Dequeue();
            if (_used.Add(mateId))
              return mateId;
          }
        }

        string created;
        do
        {
          created = $"{EdgeMatePrefix}{_nextSequence++:D2}";
        }
        while (!_used.Add(created));
        return created;
      }
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
      public bool UsesTopologyEdgeIndices;
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
          UsesTopologyEdgeIndices = string.Equals(
            obj.Attributes.GetUserString(EdgeIndexModeKey),
            TopologyEdgeMode,
            StringComparison.OrdinalIgnoreCase),
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
        UsesTopologyEdgeIndices = mate.UsesTopologyEdgeIndices,
        RuntimeSerialNumber = mate.RuntimeSerialNumber
      };
    }

    private static (int idx, Curve c)[] NakedTopologyEdges(Brep brep, double tolerance)
    {
      return brep.Edges
        .Where(edge => edge.Valence == EdgeAdjacency.Naked)
        .Select(edge => (idx: edge.EdgeIndex, c: edge.DuplicateCurve()))
        .Where(item => item.c != null && item.c.GetLength() > tolerance)
        .ToArray();
    }

    private static int? LegacyNakedOrdinalToTopologyIndex(Brep brep, int? ordinal)
    {
      if (!ordinal.HasValue || ordinal.Value < 0)
        return ordinal;
      var nakedIndices = brep.Edges
        .Where(edge => edge.Valence == EdgeAdjacency.Naked)
        .Select(edge => edge.EdgeIndex)
        .ToList();
      return ordinal.Value < nakedIndices.Count ? nakedIndices[ordinal.Value] : (int?)null;
    }

    private static void NormalizeExistingMateEdgeIndices(
      RhinoDoc doc,
      IReadOnlyList<SourceSurface> sources,
      IReadOnlyList<int> partNumbers,
      IEnumerable<ExistingEdgeMate> existingMates)
    {
      var selectedBreps = partNumbers
        .Select((partNumber, index) => (partNumber, brep: sources[index].Brep))
        .GroupBy(item => item.partNumber)
        .ToDictionary(group => group.Key, group => group.First().brep);
      var excludedIds = new HashSet<Guid>(sources.Select(source => source.Id));

      Brep? BrepForPart(int partNumber)
      {
        return selectedBreps.TryGetValue(partNumber, out var selected)
          ? selected
          : FindOriginalBrepForPart(doc, partNumber, excludedIds);
      }

      foreach (var mate in existingMates.Where(mate => !mate.UsesTopologyEdgeIndices))
      {
        var partBrep = BrepForPart(mate.PartNumber);
        var mateBrep = BrepForPart(mate.MatePartNumber);
        if (partBrep == null || mateBrep == null)
          continue;

        var edgeIndex = LegacyNakedOrdinalToTopologyIndex(partBrep, mate.EdgeIndex);
        var mateEdgeIndex = LegacyNakedOrdinalToTopologyIndex(mateBrep, mate.MateEdgeIndex);
        if ((mate.EdgeIndex.HasValue && !edgeIndex.HasValue) ||
            (mate.MateEdgeIndex.HasValue && !mateEdgeIndex.HasValue))
          continue;

        Dbg($"edge_mate migrate id={mate.MateId} part={mate.PartNumber}" +
            $" edge={mate.EdgeIndex}->{edgeIndex} mate_part={mate.MatePartNumber}" +
            $" mate_edge={mate.MateEdgeIndex}->{mateEdgeIndex}");
        mate.EdgeIndex = edgeIndex;
        mate.MateEdgeIndex = mateEdgeIndex;
        mate.UsesTopologyEdgeIndices = true;
      }
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
          var mateEdges = NakedTopologyEdges(mateBrep, tolerance);

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

    private static void LogFlatOutput(RhinoDoc doc, int partNumber, IEnumerable<Guid> outputIds)
    {
      foreach (var id in Unique(outputIds))
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null)
          continue;

        string groups = string.Join(",", obj.Attributes.GetGroupList() ?? Array.Empty<int>());
        if (obj.Attributes.Name == EdgeMateName && obj.Geometry is TextDot edgeDot)
        {
          Dbg($"part={partNumber} final_marker id={edgeDot.Text}" +
              $" attr_part={obj.Attributes.GetUserString(EdgePartNumKey)}" +
              $" point={P(edgeDot.Point)} groups={groups}");
        }
        else if (obj.Attributes.Name == TextObjectName && obj.Geometry is TextEntity text)
        {
          Dbg($"part={partNumber} final_label text={text.PlainText}" +
              $" height={text.TextHeight:G6} point={P(text.Plane.Origin)}" +
              $" x={V(text.Plane.XAxis)} y={V(text.Plane.YAxis)}" +
              $" groups={groups}");
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
      NormalizeExistingMateEdgeIndices(doc, sources, partNumbers, existingMates);
      var usedMateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      int nextMateSequence = existingMates
        .Select(mate => MateSequence(mate.MateId))
        .DefaultIfEmpty(0)
        .Max() + 1;

      // Collect naked edges per source, filtering out degenerate ones
      var allEdges = sources
        .Select(source => NakedTopologyEdges(source.Brep, tol))
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
      attr.SetUserString(EdgeIndexModeKey,      TopologyEdgeMode);
      return doc.Objects.AddTextDot(dot, attr);
    }

    private static void LoadSettings()
    {
      ToolsOptionStore.Read<int>(SettingsSection, section =>
      {
        if (ToolsOptionStore.TryGetString(section, "labelMode", out var labelMode) &&
            Enum.TryParse(labelMode, true, out LabelMode parsedLabelMode))
          _labelMode = parsedLabelMode;
        if (ToolsOptionStore.TryGetBool(section, "rotateFlatParts", out var boolValue))
          _rotateFlatParts = boolValue;
        if (ToolsOptionStore.TryGetBool(section, "edgeDots", out boolValue))
          _edgeDots = boolValue;
        if (ToolsOptionStore.TryGetBool(section, "explode", out boolValue))
          _explode = boolValue;
        if (ToolsOptionStore.TryGetBool(section, "splitFaces", out boolValue))
          _splitFaces = boolValue;
        if (ToolsOptionStore.TryGetBool(section, "keepPropSurface", out boolValue))
          _keepPropSurface = boolValue;
        if (ToolsOptionStore.TryGetBool(section, "keepPropFollowing", out boolValue))
          _keepPropFollowing = boolValue;
        if (ToolsOptionStore.TryGetDouble(section, "layoutSpacing", out var doubleValue) && doubleValue >= 0.0)
          _layoutSpacing = doubleValue;
        if (ToolsOptionStore.TryGetDouble(section, "xExtents", out doubleValue) && doubleValue >= 0.0)
          _xExtents = doubleValue;
        if (ToolsOptionStore.TryGetString(section, "surfaceLayer", out var layer))
          _surfaceLayer = NormalizeOutputLayer(layer, DefaultSurfaceLayerName, DefaultSurfaceLayerPath);
        if (ToolsOptionStore.TryGetString(section, "labelLayer", out layer))
          _labelLayer = NormalizeOutputLayer(layer, DefaultLabelLayerName, DefaultLabelLayerPath);
        if (ToolsOptionStore.TryGetString(section, "dotLayer", out layer))
          _dotLayer = NormalizeOutputLayer(layer, DefaultDotLayerName, DefaultDotLayerPath);
        return 0;
      });
    }

    private static void SaveSettings(string commandName)
    {
      var saved = ToolsOptionStore.Update(SettingsSection, section =>
      {
        section["labelMode"] = _labelMode.ToString();
        section["rotateFlatParts"] = _rotateFlatParts;
        section["edgeDots"] = _edgeDots;
        section["explode"] = _explode;
        section["splitFaces"] = _splitFaces;
        section["keepPropSurface"] = _keepPropSurface;
        section["keepPropFollowing"] = _keepPropFollowing;
        section["layoutSpacing"] = _layoutSpacing;
        section["xExtents"] = _xExtents;
        section["surfaceLayer"] = _surfaceLayer;
        section["labelLayer"] = _labelLayer;
        section["dotLayer"] = _dotLayer;
      });
      if (!saved)
        RhinoApp.WriteLine($"{commandName}: failed to save options: {ToolsOptionStore.LastError}");
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
      public string SurfaceLayer = DefaultSurfaceLayerPath;
      public string LabelLayer = DefaultLabelLayerPath;
      public string DotLayer = DefaultDotLayerPath;
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
