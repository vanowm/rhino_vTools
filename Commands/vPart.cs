using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Captures a closed perimeter (selected curves, optionally gap-bridged) and
/// all visible objects inside it — curves are trimmed at the boundary; text,
/// dots, points, and other types are included whole when their representative
/// point falls inside. The perimeter curves and any gap bridges use the chosen
/// output layer. The user picks a placement point with a full DynamicDraw preview.
/// Originals are not modified; the Part is added as new objects.
/// </summary>
public sealed class vPart : vToolsCommand
{
  public override string EnglishName => "vPart";

  // Option defaults
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultGroup", "true groups created part geometry; false leaves output ungrouped.", OptionKeys = "group")] private const bool DefaultGroup = false; // true groups created part geometry; false leaves output ungrouped.

  private static readonly bool ConfiguredDefaultGroup = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultGroup", DefaultGroup); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultJoinPerimeter", "true joins perimeter curves; false preserves separate boundary curves.", OptionKeys = "joinPerim")] private const bool DefaultJoinPerimeter = false; // true joins perimeter curves; false preserves separate boundary curves.

  private static readonly bool ConfiguredDefaultJoinPerimeter = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultJoinPerimeter", DefaultJoinPerimeter); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultCleanup", "true omits eligible straight interior pieces on actual perimeter layers; false preserves every collected interior curve.", OptionKeys = "cleanup")] private const bool DefaultCleanup = true; // true omits eligible straight interior pieces on actual perimeter layers; false preserves every collected interior curve.

  private static readonly bool ConfiguredDefaultCleanup = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultCleanup", DefaultCleanup); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultLabel", "true prompts for a label after placement; false creates the part without a new label.", OptionKeys = "label")] private const bool DefaultLabel = false; // true prompts for a label after placement; false creates the part without a new label.

  private static readonly bool ConfiguredDefaultLabel = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultLabel", DefaultLabel); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultLabelText", "Last-entered plain label offered for the next part.", OptionKeys = "labelText")] private const string DefaultLabelText = "A"; // Last-entered plain label offered for the next part.

  private static readonly string ConfiguredDefaultLabelText = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultLabelText", DefaultLabelText); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultLabelLayer", "Label layer path or the current-layer sentinel.", OptionKeys = "labelLayer")] private const string DefaultLabelLayer = "Reference"; // Label layer path or the current-layer sentinel.

  private static readonly string ConfiguredDefaultLabelLayer = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultLabelLayer", DefaultLabelLayer); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultLabelSize", "Label height in model units; positive.", OptionKeys = "labelSize")] private const double DefaultLabelSize = 1.0; // Label height in model units; positive.

  private static readonly double ConfiguredDefaultLabelSize = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultLabelSize", DefaultLabelSize); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.LabelInteriorGridSamples", "Grid samples per axis used when a concave part's center lies outside its perimeter; two or greater.")] private const int LabelInteriorGridSamples = 25; // Grid samples per axis used when a concave part's center lies outside its perimeter; two or greater.

  private static readonly int ConfiguredLabelInteriorGridSamples = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.LabelInteriorGridSamples", LabelInteriorGridSamples); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.LabelPointerThresholdPixels", "Minimum cursor movement before moving the initially centered label; positive screen pixels.")] private const int LabelPointerThresholdPixels = 3; // Minimum cursor movement before moving the initially centered label; positive screen pixels.

  private static readonly int ConfiguredLabelPointerThresholdPixels = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.LabelPointerThresholdPixels", LabelPointerThresholdPixels); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.LabelSizePixelsPerUnit", "Upward Shift-movement pixels per one model-unit label-height increment; positive.")] private const double LabelSizePixelsPerUnit = 20.0; // Upward Shift-movement pixels per one model-unit label-height increment; positive.

  private static readonly double ConfiguredLabelSizePixelsPerUnit = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.LabelSizePixelsPerUnit", LabelSizePixelsPerUnit); // Resolved once from advanced defaults; restart Rhino after changes.
  private const string LabelObjectName = "PartLabel"; // Object name for the part's primary identifying label.
  private const string PrimaryLabelLayer = "LABEL"; // Conventional part-name layer preferred over other unmarked text.
  private static readonly string[] DetailLabelNames = ["NotchLabel", "LengthLabel", "PartSplitSeamLabel", "PartSplitReferenceLabel", "ShadeOverlapLabel"]; // Detail annotations that must never be replaced as the part's primary label.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.DefaultLayer", "Rhino layer path or the shared current-layer sentinel.", OptionKeys = "layer")] private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Rhino layer path or the shared current-layer sentinel.

  private static readonly string ConfiguredDefaultLayer = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.DefaultLayer", DefaultLayer); // Resolved once from advanced defaults; restart Rhino after changes.
  private const string SourceLayerOption = "*Source*"; // Sentinel that preserves each perimeter segment's source layer; joined output and bridges use the first actual boundary source layer.

  private const string SectionName  = "vPart";
  private const string GroupKey     = "group";
  private const string JoinPerimKey = "joinPerim";
  private const string CleanupKey = "cleanup";
  private const string LegacyRemoveInteriorLinesKey = "removeInteriorLines"; // Previous persisted key read once for migration to Cleanup.
  private const string LayerKey = "layer";
  private const string NotchObjectName = "Notch"; // Rhino object name used to exempt notch curves from interior line cleanup.
  private const string NotchRoleKey = "notch.object_role"; // Notch metadata key whose value is "notch" for generated notch geometry.
  private const string NotchIdKey = "notch.notch_id"; // Notch metadata key containing a nonempty notch component identifier.
  private const string MultiLayerSourceCleanupMessage = "vPart: Cleanup skipped because Layer=Source perimeter uses multiple layers ({0})."; // string.Format template; {0} is a comma-separated list of source layer paths.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.PerimeterContributionSampleCount", "Number of midpoint samples used to determine whether a selected curve contributes to the perimeter; integer greater than zero.")] private const int PerimeterContributionSampleCount = 17; // Number of midpoint samples used to determine whether a selected curve contributes to the perimeter; integer greater than zero.

  private static readonly int ConfiguredPerimeterContributionSampleCount = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.PerimeterContributionSampleCount", PerimeterContributionSampleCount); // Resolved once from advanced defaults; restart Rhino after changes.
  [global::vTools.CommandDefault("vPart", "vTools.Commands.vPart.InteriorLineBoundaryToleranceScale", "Model-tolerance multiplier used to distinguish interior lines from perimeter-aligned lines; positive number.")] private const double InteriorLineBoundaryToleranceScale = 2.0; // Model-tolerance multiplier used to distinguish interior lines from perimeter-aligned lines; positive number.

  private static readonly double ConfiguredInteriorLineBoundaryToleranceScale = global::vTools.CommandDefaults.Get("vPart", "vTools.Commands.vPart.InteriorLineBoundaryToleranceScale", InteriorLineBoundaryToleranceScale); // Resolved once from advanced defaults; restart Rhino after changes.
  private static readonly double[] InteriorSelectionSamples = [0.25, 0.5, 0.75]; // Fractions of an open selected divider tested for containment in another selected closed perimeter; between zero and one.

  private static bool _group = ConfiguredDefaultGroup;
  private static bool _joinPerim = ConfiguredDefaultJoinPerimeter;
  private static bool _cleanup = ConfiguredDefaultCleanup;
  private static string _layer = ConfiguredDefaultLayer;
  private static bool _label = ConfiguredDefaultLabel;
  private static string _labelText = ConfiguredDefaultLabelText;
  private static string _labelLayer = ConfiguredDefaultLabelLayer;
  private static double _labelSize = ConfiguredDefaultLabelSize;

  // ── Logging ────────────────────────────────────────────────────────────
  private static void L(string message) => vTools.Log.Write("vPart", message);

  private static string Short(Guid id) => id.ToString()[..8];

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      if (ToolsOptionStore.TryGetBool(section, GroupKey,     out var g)) _group    = g;
      if (ToolsOptionStore.TryGetBool(section, JoinPerimKey, out var j)) _joinPerim = j;
      if (ToolsOptionStore.TryGetBool(section, CleanupKey, out var cleanup) ||
          ToolsOptionStore.TryGetBool(
            section, LegacyRemoveInteriorLinesKey, out cleanup))
        _cleanup = cleanup;
      if (ToolsOptionStore.TryGetString(section, LayerKey, out var layer))
        _layer = NormalizePerimeterLayerOption(layer);
      _label = ToolsOptionStore.TryGetBool(section, "label", out var label) ? label : ConfiguredDefaultLabel;
      _labelText = ToolsOptionStore.TryGetString(section, "labelText", out var text) ? text : ConfiguredDefaultLabelText;
      _labelLayer = ToolsOptionStore.TryGetString(section, "labelLayer", out var labelLayer) ? labelLayer : ConfiguredDefaultLabelLayer;
      _labelSize = ToolsOptionStore.TryGetDouble(section, "labelSize", out var size) && double.IsFinite(size) && size > 0
        ? size : ConfiguredDefaultLabelSize;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[GroupKey]     = _group;
      section[JoinPerimKey] = _joinPerim;
      section[CleanupKey] = _cleanup;
      section.Remove(LegacyRemoveInteriorLinesKey);
      section[LayerKey] = _layer;
      section["label"] = _label;
      section["labelText"] = _labelText;
      section["labelLayer"] = _labelLayer;
      section["labelSize"] = _labelSize;
    });

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    using var existing = FindPreselectedPart(doc);
    if (existing != null)
    {
      var id = PlaceOrUpdatePartLabel(doc, mode, existing.Part.Outline, existing.Part.Plane, existing.Label, existing.Groups);
      if (id != Guid.Empty) doc.Objects.Select(existing.Part.Items.Select(item => item.Id).Append(id));
      doc.Views.Redraw();
      return id == Guid.Empty ? Result.Cancel : Result.Success;
    }
    var layerSession = new DuplicateOutputLayerSession(doc, _layer, EnglishName);
    var tol = doc.ModelAbsoluteTolerance;
    L($"=== vPart {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
    L($"  tol={tol:G4}  group={_group}  joinPerim={_joinPerim} " +
      $"cleanup={_cleanup} layer={_layer}");

    // ── 1. Select perimeter curves ─────────────────────────────────────────
    // Dale's pattern: single GetObject instance with EnableClearObjectsOnEntry(false)
    // so the accumulated list persists across GetMultiple calls.
    // AlreadySelectedObjectSelect = true lets the user re-click a deselected object
    // within the same session to add it back.

    var groupToggle    = new OptionToggle(_group,    "No", "Yes");
    var joinPerimToggle = new OptionToggle(_joinPerim, "No", "Yes");
    var cleanupToggle = new OptionToggle(_cleanup, "No", "Yes");
    var labelToggle = new OptionToggle(_label, "No", "Yes");
    var labelSizeOption = new OptionDouble(_labelSize, RhinoMath.ZeroTolerance, double.MaxValue);

    using var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select perimeter curves. Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = true;
    go.SetCustomGeometryFilter((obj, _, _) => obj.Geometry is Curve);
    go.GroupSelect = false;
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.AlreadySelectedObjectSelect = true;
    go.DeselectAllBeforePostSelect = false;
    go.AcceptNothing(true);

    var selIter = 0;
    while (true)
    {
      go.ClearCommandOptions();
      go.AddOptionToggle("Group", ref groupToggle);
      go.AddOptionToggle("JoinPerimeter", ref joinPerimToggle);
      go.AddOptionToggle("Cleanup", ref cleanupToggle);
      go.AddOptionToggle("Label", ref labelToggle);
      go.AddOptionDouble("LabelSize", ref labelSizeOption);
      int labelLayerIndex = go.AddOption("LabelLayer", _labelLayer);
      var layerOptionIndex = go.AddOption("Layer", layerSession.OptionLayerName);
      var selRes = go.GetMultiple(0, 0);
      layerSession.ObserveCurrentLayer(doc);
      L($"sel iter {++selIter}: result={selRes}  cmdResult={go.CommandResult()}  count={go.ObjectCount}  preselected={go.ObjectsWerePreselected}");

      if (selRes == GetResult.Option)
      {
        _group = groupToggle.CurrentValue;
        _joinPerim = joinPerimToggle.CurrentValue;
        _cleanup = cleanupToggle.CurrentValue;
        _label = labelToggle.CurrentValue;
        _labelSize = labelSizeOption.CurrentValue;
        if (go.Option()?.Index == labelLayerIndex) PromptForLabelLayer(doc, mode);
        if (go.Option()?.Index == layerOptionIndex)
          PromptForLayer(doc, mode, layerSession);
        SaveOptions();
        continue;
      }

      if (go.CommandResult() != Result.Success)
      {
        for (var i = 0; i < go.ObjectCount; i++)
        {
          var obj = go.Object(i).Object();
          obj?.Select(false);
          obj?.UnselectAllSubObjects();
        }
        doc.Views.Redraw();
        L("cancelled");
        return Result.Cancel;
      }

      if (go.ObjectsWerePreselected)
      {
        go.EnablePreSelect(false, false);
        continue;
      }

      break;
    }

    var collectedMap = new Dictionary<
      (Guid ObjectId, ComponentIndexType Type, int Index), ObjRef>();
    for (var i = 0; i < go.ObjectCount; i++)
    {
      var r = go.Object(i);
      var component = r.GeometryComponentIndex;
      var sourceKey = (r.ObjectId, component.ComponentIndexType, component.Index);
      if (!collectedMap.ContainsKey(sourceKey) || r.SelectionPoint().IsValid)
        collectedMap[sourceKey] = r;
      L($"  sel[{i}]: {Short(r.ObjectId)} component={component.ComponentIndexType}:{component.Index}");
    }

    L($"final collection: {CountText.Format(collectedMap.Count, "curve reference")}");
    foreach (var key in collectedMap.Keys)
      L($"  collected: {Short(key.ObjectId)} component={key.Type}:{key.Index}");

    // Collect perimeter curves — keep each with its own attributes
    var perimIds  = new HashSet<Guid>();
    var selectedSegmentIndices = new Dictionary<Guid, HashSet<int>>();
    var wholeCurveIds = collectedMap.Keys
      .Where(key => key.Index < 0)
      .Select(key => key.ObjectId)
      .ToHashSet();
    var perimList = new List<(Curve Crv, ObjectAttributes Attr)>();
    var perimPickPoints = new List<Point3d>();

    foreach (var (key, r) in collectedMap)
    {
      if (key.Index >= 0 && wholeCurveIds.Contains(key.ObjectId))
        continue;
      if (r.Curve() is { } crv)
      {
        if (key.Type == ComponentIndexType.PolycurveSegment && key.Index >= 0)
        {
          if (!selectedSegmentIndices.TryGetValue(key.ObjectId, out var indices))
            selectedSegmentIndices[key.ObjectId] = indices = new HashSet<int>();
          indices.Add(key.Index);
        }
        else
          perimIds.Add(key.ObjectId);
        perimList.Add((crv.DuplicateCurve(), r.Object()?.Attributes?.Duplicate() ?? new ObjectAttributes()));
        var pickPoint = r.SelectionPoint();
        if (pickPoint.IsValid && crv.ClosestPoint(pickPoint, out var pickParameter))
          pickPoint = crv.PointAt(pickParameter);
        perimPickPoints.Add(pickPoint);
        L($"  perimeter input: {Short(key.ObjectId)} component={key.Type}:{key.Index} length={crv.GetLength():G17}");
        if (pickPoint.IsValid) L($"  boundary pick: ({pickPoint.X:G17},{pickPoint.Y:G17},{pickPoint.Z:G17})");
      }
    }

    L($"perimList: {CountText.Format(perimList.Count, "curve")}");
    if (perimList.Count == 0)
    {
      RhinoApp.WriteLine("vPart: no curves selected.");
      L("EXIT: no curves");
      return Result.Nothing;
    }

    // Deselect source curves now that we have them captured
    foreach (var r in collectedMap.Values)
    {
      var obj = r.Object();
      obj?.Select(false);
      obj?.UnselectAllSubObjects();
    }
    doc.Views.Redraw();

    // ── 2. View plane (needed for perimeter detection and containment testing) ──

    var vp    = doc.Views.ActiveView?.ActiveViewport;
    var plane = Plane.WorldXY;
    if (vp != null && vp.GetCameraFrame(out var camFrame))
      plane = new Plane(Point3d.Origin, camFrame.XAxis, camFrame.YAxis);

    // ── 3. Build closed perimeter for containment testing ─────────────────────
    //  Uses CreateBooleanRegions first (handles curves extending past corners),
    //  then falls back to endpoint joining + gap bridging.

    var perimLog = new List<string>();
    var (perimeter, bridges, perimeterCurves, boundaryTolerance) = BuildClosedPerimeter(
      perimList.Select(p => p.Crv).ToList(), plane, tol, perimLog, perimPickPoints);
    L($"BuildClosedPerimeter: {(perimeter != null ? "OK" : "FAILED")}  bridges={bridges.Count}  boundaryTol={boundaryTolerance:G6}");
    foreach (var entry in perimLog) L($"  perim: {entry}");
    if (perimeter == null)
    {
      RhinoApp.WriteLine("vPart: could not form a closed perimeter from selected curves.");
      L("EXIT: no closed perimeter");
      return Result.Nothing;
    }

    // Resolve the actual boundary pieces before classifying perimeter layers.
    // Selected interior curves must never contribute their layers to cleanup.
    var effectivePerimeter = perimList
      .Select((item, index) => (
        Crv: index < perimeterCurves.Count ? perimeterCurves[index] : item.Crv,
        item.Attr))
      .ToList();
    var rawTrimmedPerimeter = TrimToPerimeter(
      effectivePerimeter, perimeter!, plane, boundaryTolerance).ToList();

    var boundaryPieces = rawTrimmedPerimeter
      .Where(item => CurveContributesToBoundary(
        item.Crv, perimeter, plane, boundaryTolerance))
      .ToList();
    var sourceBoundaryAttributes = boundaryPieces.Count > 0
      ? boundaryPieces[0].Attr
      : perimList[0].Attr;
    var sourcePerimeterLayerIndices = boundaryPieces
      .Select(item => item.Attr.LayerIndex)
      .Where(layerIndex => layerIndex >= 0)
      .ToHashSet();
    if (sourcePerimeterLayerIndices.Count == 0)
    {
      var fallbackLayerIndex = ClosestSourceLayerIndex(
        perimList, perimeter.PointAtNormalizedLength(0.5));
      if (fallbackLayerIndex >= 0)
        sourcePerimeterLayerIndices.Add(fallbackLayerIndex);
    }
    L("source perimeter layers: " +
      string.Join(",", sourcePerimeterLayerIndices));

    // ── 4. Collect inside objects (all visible types, trimmed for curves) ──

    var allInsideObjects = CollectInsideObjects(
      doc,
      perimIds,
      selectedSegmentIndices,
      perimeter,
      plane,
      boundaryTolerance);
    L($"insideObjects: all={allInsideObjects.Count}");

    // ── 5. Assemble Part items ─────────────────────────────────────────────────────
    //  • Original perimeter curves trimmed to the closed boundary — on the chosen layer
    //  • Bridge segments (gap fillers) — on the chosen layer
    //  • Inside objects — on their original layers

    L($"trimmedPerimeter: all={rawTrimmedPerimeter.Count} " +
      $"from {CountText.Format(effectivePerimeter.Count, "selected curve")}");

    // ── 6. Base point = bounding box center of perimeter ─────────────────

    var bbox = perimeter.GetBoundingBox(true);
    foreach (var (geom, _) in allInsideObjects)
    {
      var b = geom.GetBoundingBox(true);
      if (b.IsValid) bbox = bbox.IsValid ? BoundingBox.Union(bbox, b) : b;
    }
    var basePoint = bbox.IsValid ? bbox.Center : perimeter.PointAtStart;

    // ── 7. DynamicDraw preview + placement ────────────────────────────────

    // Precompute geometry for both join states. Perimeter color is resolved
    // during drawing so Layer and external current-layer changes update live.
    var splitPreview = new List<(
      GeometryBase Geom, ObjectAttributes Attr, bool IsPerimeter)>();
    foreach (var (curve, attributes) in rawTrimmedPerimeter)
      splitPreview.Add((curve.DuplicateCurve(), attributes, true));
    foreach (var bridge in bridges)
      splitPreview.Add((bridge.DuplicateCurve(), new ObjectAttributes(), true));
    foreach (var (geom, attr) in allInsideObjects)
      splitPreview.Add((geom.Duplicate()!, attr, false));

    var joinedPreview = new List<(
      GeometryBase Geom, ObjectAttributes Attr, bool IsPerimeter)>
    {
      (perimeter.DuplicateCurve(), new ObjectAttributes(), true)
    };
    foreach (var (geom, attr) in allInsideObjects)
      joinedPreview.Add((geom.Duplicate()!, attr, false));

    var placementPoint = Point3d.Unset;
    var cleanupSkipMessageShown = false;
    // Destination changes must not silently change the geometry already previewed.
    // Only an explicit Layer option changes the cleanup scope during placement.
    var cleanupLayerIndices = ResolveCleanupLayerIndices(
      doc,
      layerSession,
      sourcePerimeterLayerIndices,
      sourceBoundaryAttributes);
    var cleanedInsideObjects = allInsideObjects;
    var cleanedTrimmedPerimeter = rawTrimmedPerimeter;
    while (true)
    {
      var skipMultiLayerSourceCleanup =
        cleanupToggle.CurrentValue &&
        IsSourceLayerOption(_layer) &&
        sourcePerimeterLayerIndices.Count > 1;
      if (skipMultiLayerSourceCleanup && !cleanupSkipMessageShown)
      {
        RhinoApp.WriteLine(
          MultiLayerSourceCleanupMessage,
          DescribeLayers(doc, sourcePerimeterLayerIndices));
      }
      cleanupSkipMessageShown = skipMultiLayerSourceCleanup;

      cleanedInsideObjects = allInsideObjects
        .Where(item => item.Geom is not Curve curve ||
          !IsRemovableInteriorLine(
            curve,
            item.Attr,
            cleanupLayerIndices,
            perimeter,
            plane,
            boundaryTolerance))
        .ToList();
      cleanedTrimmedPerimeter = rawTrimmedPerimeter
        .Where(item => !IsRemovableInteriorLine(
          item.Crv,
          item.Attr,
          cleanupLayerIndices,
          perimeter,
          plane,
          boundaryTolerance))
        .ToList();

      var cleanedSplitPreview = new List<(
        GeometryBase Geom, ObjectAttributes Attr, bool IsPerimeter)>();
      foreach (var (curve, attributes) in cleanedTrimmedPerimeter)
        cleanedSplitPreview.Add((curve.DuplicateCurve(), attributes, true));
      foreach (var bridge in bridges)
        cleanedSplitPreview.Add((
          bridge.DuplicateCurve(), new ObjectAttributes(), true));
      foreach (var (geom, attr) in cleanedInsideObjects)
        cleanedSplitPreview.Add((geom.Duplicate()!, attr, false));

      var cleanedJoinedPreview = new List<(
        GeometryBase Geom, ObjectAttributes Attr, bool IsPerimeter)>
      {
        (perimeter.DuplicateCurve(), new ObjectAttributes(), true)
      };
      foreach (var (geom, attr) in cleanedInsideObjects)
        cleanedJoinedPreview.Add((geom.Duplicate()!, attr, false));

      L("cleanup target layers: " + string.Join(",", cleanupLayerIndices) +
        $" removedInside={allInsideObjects.Count - cleanedInsideObjects.Count}" +
        $" removedSelected={rawTrimmedPerimeter.Count - cleanedTrimmedPerimeter.Count}");

      using var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt("Pick placement point for Part");
      gp.AddOptionToggle("Group", ref groupToggle);
      gp.AddOptionToggle("JoinPerimeter", ref joinPerimToggle);
      gp.AddOptionToggle("Cleanup", ref cleanupToggle);
      gp.AddOptionToggle("Label", ref labelToggle);
      gp.AddOptionDouble("LabelSize", ref labelSizeOption);
      int labelLayerIndex = gp.AddOption("LabelLayer", _labelLayer);
      var layerOptionIndex = gp.AddOption("Layer", layerSession.OptionLayerName);
      EventHandler<GetPointDrawEventArgs> drawPreview = (_, e) =>
      {
        var xform = Transform.Translation(e.CurrentPoint - basePoint);
        var items = (joinPerimToggle.CurrentValue, cleanupToggle.CurrentValue) switch
        {
          (true, true) => cleanedJoinedPreview,
          (true, false) => joinedPreview,
          (false, true) => cleanedSplitPreview,
          _ => splitPreview
        };
        foreach (var (geom, attr, isPerimeter) in items)
        {
          var draw = geom.Duplicate();
          if (draw == null)
            continue;
          draw.Transform(xform);
          var layerIndex = isPerimeter
            ? ResolvePerimeterLayerIndex(
              doc, layerSession, attr, sourceBoundaryAttributes)
            : attr.LayerIndex;
          var color = doc.Layers[layerIndex]?.Color ?? Color.Cyan;
          DrawPreview(e.Display, draw, color);
        }
      };
      gp.DynamicDraw += drawPreview;
      var gpResult = gp.Get();
      gp.DynamicDraw -= drawPreview;
      layerSession.ObserveCurrentLayer(doc);

      _group = groupToggle.CurrentValue;
      _joinPerim = joinPerimToggle.CurrentValue;
      _cleanup = cleanupToggle.CurrentValue;
      _label = labelToggle.CurrentValue;
      _labelSize = labelSizeOption.CurrentValue;
      if (gpResult == GetResult.Option)
      {
        if (gp.Option()?.Index == labelLayerIndex) PromptForLabelLayer(doc, mode);
        if (gp.Option()?.Index == layerOptionIndex &&
            PromptForLayer(doc, mode, layerSession))
        {
          cleanupLayerIndices = ResolveCleanupLayerIndices(
            doc,
            layerSession,
            sourcePerimeterLayerIndices,
            sourceBoundaryAttributes);
        }
        SaveOptions();
        continue;
      }

      if (gpResult != GetResult.Point)
      {
        L($"EXIT: placement cancelled (gpResult={gpResult})");
        return Result.Cancel;
      }

      placementPoint = gp.Point();
      SaveOptions();
      break;
    }
    L($"placement point: {placementPoint}");

    // ── 8. Commit ─────────────────────────────────────────────────────────

    var translation = Transform.Translation(placementPoint - basePoint);
    var addedIds    = new List<Guid>();

    ObjectAttributes PerimeterAttributes(ObjectAttributes? source = null)
    {
      var attributes = source?.Duplicate() ?? new ObjectAttributes();
      attributes.LayerIndex = ResolvePerimeterLayerIndex(
        doc,
        layerSession,
        source,
        sourceBoundaryAttributes);
      return attributes;
    }

    var activeInsideObjects = _cleanup
      ? cleanedInsideObjects
      : allInsideObjects;
    var activeTrimmedPerimeter = _cleanup
      ? cleanedTrimmedPerimeter
      : rawTrimmedPerimeter;
    var removedInteriorLines = _cleanup
      ? allInsideObjects.Count - cleanedInsideObjects.Count +
        rawTrimmedPerimeter.Count - cleanedTrimmedPerimeter.Count
      : 0;
    var commitItems = new List<(GeometryBase Geom, ObjectAttributes Attr)>();
    if (_joinPerim)
    {
      // Single joined closed perimeter on the chosen layer, then inside objects.
      commitItems.Add((perimeter, PerimeterAttributes(sourceBoundaryAttributes)));
      commitItems.AddRange(activeInsideObjects);
    }
    else
    {
      foreach (var (curve, attributes) in activeTrimmedPerimeter)
        commitItems.Add((curve, PerimeterAttributes(attributes)));
      foreach (var bridge in bridges)
        commitItems.Add((bridge, PerimeterAttributes(sourceBoundaryAttributes)));
      commitItems.AddRange(activeInsideObjects);
    }

    foreach (var (geom, attr) in commitItems)
    {
      var placed = geom.Duplicate();
      if (placed == null) continue;
      placed.Transform(translation);
      attr.RemoveFromAllGroups();
      var id = AddObjectToDoc(doc, placed, attr);
      if (id != Guid.Empty) addedIds.Add(id);
    }

    if (_label && addedIds.Count > 0)
    {
      doc.Views.Redraw();
      using var placedPerimeter = perimeter.DuplicateCurve();
      placedPerimeter.Transform(translation);
      var labelPlane = plane;
      labelPlane.Origin = plane.Origin + (placementPoint - basePoint);
      var labelId = PlacePartLabel(doc, mode, placedPerimeter, labelPlane);
      if (labelId != Guid.Empty) addedIds.Add(labelId);
    }

    if (_group && addedIds.Count > 1)
    {
      var grpIdx = doc.Groups.Add();
      foreach (var id in addedIds)
      {
        var obj = doc.Objects.FindId(id);
        if (obj == null) continue;
        obj.Attributes.AddToGroup(grpIdx);
        obj.CommitChanges();
      }
    }

    var resolvedLayerDescription = IsSourceLayerOption(_layer)
      ? SourceLayerOption
      : layerSession.ResolvedLayerName(doc);
    L($"committed: {CountText.Format(addedIds.Count, "object")}  grouped={_group && addedIds.Count > 1} " +
      $"cleanup={_cleanup} removedLines={removedInteriorLines} " +
      $"layer={resolvedLayerDescription}");
    L("=== done ===");
    doc.Views.Redraw();
    return Result.Success;
  }

  private static bool PromptForLabelLayer(RhinoDoc doc, RunMode mode)
  {
    if (!LayerSelector.TrySelect(doc, _labelLayer, DuplicateCommandSupport.CurrentLayerOption,
      "vPart label layer", mode, allowNewLayer: true, out var selected)) return false;
    _labelLayer = selected;
    SaveOptions();
    return true;
  }

  private static Point3d PartLabelCenter(Curve perimeter, Plane plane, double tolerance)
  {
    var bounds = perimeter.GetBoundingBox(plane);
    var center = plane.PointAt(bounds.Center.X, bounds.Center.Y);
    if (perimeter.Contains(center, plane, tolerance) == PointContainment.Inside) return center;
    var best = Point3d.Unset;
    double bestDistance = double.MaxValue;
    for (int x = 0; x < ConfiguredLabelInteriorGridSamples; x++)
      for (int y = 0; y < ConfiguredLabelInteriorGridSamples; y++)
      {
        var candidate = plane.PointAt(
          bounds.Min.X + (bounds.Max.X - bounds.Min.X) * (x + 0.5) / ConfiguredLabelInteriorGridSamples,
          bounds.Min.Y + (bounds.Max.Y - bounds.Min.Y) * (y + 0.5) / ConfiguredLabelInteriorGridSamples);
        double distance = candidate.DistanceToSquared(center);
        if (distance < bestDistance && perimeter.Contains(candidate, plane, tolerance) == PointContainment.Inside)
        { best = candidate; bestDistance = distance; }
      }
    return best.IsValid ? best : center;
  }

  private sealed record ExistingPart(PlanarPartSelection.Part Part, RhinoObject? Label, int[] Groups) : IDisposable
  {
    public void Dispose() => Part.Dispose();
  }

  private static ExistingPart? FindPreselectedPart(RhinoDoc doc)
  {
    var selected = doc.Objects.GetSelectedObjects(false, false).Where(obj => !obj.IsDeleted && obj.IsSelected(false) != 0).ToArray();
    if (selected.Length == 0) return null;
    var ids = selected.Select(obj => obj.Id).ToHashSet();
    var common = selected.Select(obj => obj.Attributes.GetGroupList() ?? []).Aggregate((first, next) => first.Intersect(next).ToArray());
    foreach (int group in common.OrderBy(doc.Groups.GroupObjectCount))
    {
      if (doc.Groups[group] is not { IsDeleted: false }) continue;
      var members = doc.Groups.GroupMembers(group).Where(obj => !obj.IsDeleted).ToArray();
      if (!members.Any(obj => obj.Geometry is Curve && !IsNotchCurve(obj.Attributes)) ||
          members.Any(obj => NestTableMetadata.IsDecoration(doc, obj)) ||
          members.Any(obj => obj.IsSelectable(true, false, false, false) && !ids.Contains(obj.Id))) continue;
      var parts = PlanarPartSelection.Capture(doc, members.Select(obj => obj.Id).ToArray(), doc.ModelAbsoluteTolerance);
      if (parts.Count != 1) { foreach (var part in parts) part.Dispose(); continue; }
      var label = FindPartLabel(doc, members);
      var groups = members.Select(obj => obj.Attributes.GetGroupList() ?? []).Aggregate((first, next) => first.Intersect(next).ToArray());
      L($"preselected part: group={group} members={members.Length} label={label?.Id}");
      return new(parts[0], label, groups);
    }
    if (selected.All(obj => (obj.Attributes.GetGroupList() ?? []).Length == 0) &&
        selected.Any(obj => obj.Geometry is TextEntity && string.Equals(obj.Attributes.Name, LabelObjectName, StringComparison.OrdinalIgnoreCase)))
    {
      var parts = PlanarPartSelection.Capture(doc, selected.Select(obj => obj.Id).ToArray(), doc.ModelAbsoluteTolerance);
      if (parts.Count == 1) return new(parts[0], FindPartLabel(doc, selected), []);
      foreach (var part in parts) part.Dispose();
    }
    return null;
  }

  private static RhinoObject? FindPartLabel(RhinoDoc doc, IEnumerable<RhinoObject> members) =>
    members.Where(obj => obj.Geometry is TextEntity text && !string.IsNullOrWhiteSpace(text.PlainText) &&
          !DetailLabelNames.Contains(obj.Attributes.Name, StringComparer.OrdinalIgnoreCase) &&
          !string.Equals(obj.Attributes.GetUserString(NotchRoleKey), "label", StringComparison.OrdinalIgnoreCase) &&
          string.IsNullOrEmpty(obj.Attributes.GetUserString("notch.label_id")))
        .OrderByDescending(obj => string.Equals(obj.Attributes.Name, LabelObjectName, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(obj => string.Equals(doc.Layers[obj.Attributes.LayerIndex].Name, PrimaryLabelLayer, StringComparison.OrdinalIgnoreCase))
        .ThenByDescending(obj => ((TextEntity)obj.Geometry).TextHeight).FirstOrDefault();

  private static Guid PlacePartLabel(RhinoDoc doc, RunMode mode, Curve perimeter, Plane plane) =>
    PlaceOrUpdatePartLabel(doc, mode, perimeter, plane, null, []);

  private static Guid PlaceOrUpdatePartLabel(RhinoDoc doc, RunMode mode, Curve perimeter, Plane plane,
    RhinoObject? existing, IReadOnlyList<int> groups)
  {
    if (existing != null && !existing.IsSelectable(true, false, false, false))
    { RhinoApp.WriteLine("vPart: the existing label is hidden or locked; make it editable first."); return Guid.Empty; }
    var original = existing?.Geometry as TextEntity;
    var text = original?.PlainText ?? _labelText;
    if (RhinoGet.GetString("Part label", false, ref text) != Result.Success || string.IsNullOrWhiteSpace(text))
      return Guid.Empty;
    if (original != null)
    {
      _labelSize = original.TextHeight;
      _labelLayer = doc.Layers[existing!.Attributes.LayerIndex].FullPath;
    }
    _labelText = text;
    SaveOptions();
    var position = original?.Plane.Origin ?? PartLabelCenter(perimeter, plane, doc.ModelAbsoluteTolerance);
    double angle = original == null ? 0 : Math.Atan2(original.Plane.XAxis * plane.YAxis, original.Plane.XAxis * plane.XAxis);
    using var entity = original?.Duplicate() as TextEntity ?? new TextEntity
    {
      PlainText = text, Plane = new Plane(position, plane.XAxis, plane.YAxis),
      TextHeight = _labelSize, Justification = TextJustification.MiddleCenter, DrawForward = false,
      DimensionStyleId = doc.DimStyles.Current.Id
    };
    entity.PlainText = text;
    using var visibility = new PartLabelVisibility(doc, existing?.Id ?? Guid.Empty) { Enabled = existing != null };
    if (original == null) using (var style = doc.DimStyles.Current.Duplicate())
    {
      style.TextHeight = _labelSize;
      style.DimensionScale = 1;
      style.TextOrientation = TextOrientation.InPlane;
      style.TextRotation = 0;
      entity.SetOverrideDimStyle(style);
    }
    while (true)
    {
      using var getter = new GetPoint();
      getter.SetCommandPrompt("Confirm label or drag to move; Alt rotates, Shift sizes");
      getter.AcceptNothing(true);
      getter.AcceptCustomMessage(true);
      getter.Constrain(plane, false);
      var size = new OptionDouble(_labelSize, RhinoMath.ZeroTolerance, double.MaxValue);
      int textOption = getter.AddOption("Text", entity.PlainText);
      int sizeOption = getter.AddOptionDouble("LabelSize", ref size);
      int layerOption = getter.AddOption("LabelLayer", _labelLayer);
      var previousMode = -1;
      double rotationStart = 0, initialAngle = angle, initialSize = _labelSize;
      int sizeStartY = 0;
      var dragPosition = position;
      var dragPoint = position;
      Color LabelColor()
      {
        int layerIndex = LayerSelector.IsCurrentLayerValue(_labelLayer, DuplicateCommandSupport.CurrentLayerOption)
          ? doc.Layers.CurrentLayerIndex : doc.Layers.FindByFullPath(_labelLayer, -1);
        return layerIndex >= 0 ? doc.Layers[layerIndex].Color : Color.White;
      }
      doc.Views.Redraw();

      void BeginDrag(Point3d point, System.Drawing.Point cursor)
      {
        previousMode = -1; dragPosition = position; dragPoint = point;
        initialAngle = angle; initialSize = _labelSize; sizeStartY = cursor.Y;
      }
      void UpdateLabel(Point3d point, System.Drawing.Point cursor)
      {
        var modifiers = System.Windows.Forms.Control.ModifierKeys;
        int interaction = modifiers.HasFlag(System.Windows.Forms.Keys.Shift) ? 2
          : modifiers.HasFlag(System.Windows.Forms.Keys.Alt) ? 1 : 0;
        if (interaction == 2)
        {
          if (previousMode >= 0 && previousMode != interaction) { sizeStartY = cursor.Y; initialSize = _labelSize; }
          _labelSize = ShiftLabelSize(initialSize, sizeStartY - cursor.Y);
        }
        else if (interaction == 1)
        {
          var direction = point - position;
          double pointerAngle = Math.Atan2(direction * plane.YAxis, direction * plane.XAxis);
          if (previousMode != interaction)
          {
            var startDirection = (previousMode < 0 ? dragPoint : point) - position;
            rotationStart = Math.Atan2(startDirection * plane.YAxis, startDirection * plane.XAxis);
            initialAngle = angle;
          }
          if (direction.Length > doc.ModelAbsoluteTolerance) angle = initialAngle + pointerAngle - rotationStart;
        }
        else
        {
          if (previousMode > 0) { dragPosition = position; dragPoint = point; }
          position = plane.ClosestPoint(dragPosition + (point - dragPoint));
        }
        previousMode = interaction;
        var labelPlane = new Plane(position, plane.XAxis, plane.YAxis);
        labelPlane.Rotate(angle, plane.Normal, position);
        entity.Plane = labelPlane;
        entity.TextHeight = _labelSize;
      }
      using var drag = new PartLabelDrag(doc, entity, plane, BeginDrag, UpdateLabel) { Enabled = true };
      getter.MouseMove += (_, e) => drag.UpdateModifiers(e.Viewport, e.WindowPoint);
      getter.DynamicDraw += (_, e) =>
      {
        e.Display.PushDepthTesting(false);
        try { e.Display.DrawAnnotation(entity, LabelColor()); }
        finally { e.Display.PopDepthTesting(); }
      };
      var result = getter.Get(onMouseUp: true);
      drag.Enabled = false;
      if (result == GetResult.Point && drag.Moved) { SaveOptions(); continue; }
      if (result == GetResult.Option)
      {
        if (getter.Option()?.Index == textOption)
        {
          var proposed = entity.PlainText;
          if (RhinoGet.GetString("Part label", false, ref proposed) == Result.Success && !string.IsNullOrWhiteSpace(proposed))
          { entity.PlainText = proposed; _labelText = proposed; }
        }
        if (getter.Option()?.Index == sizeOption) _labelSize = size.CurrentValue;
        entity.TextHeight = _labelSize;
        if (getter.Option()?.Index == layerOption) PromptForLabelLayer(doc, mode);
        SaveOptions();
        continue;
      }
      SaveOptions();
      if (result != GetResult.Point && result != GetResult.Nothing &&
        !(result == GetResult.CustomMessage && getter.CustomMessage() is PartLabelConfirm)) return Guid.Empty;
      // Commit the same entity and transform used by the last preview, including Alt/Shift edits.
      int outputLayer = LayerSelector.IsCurrentLayerValue(_labelLayer, DuplicateCommandSupport.CurrentLayerOption)
        ? doc.Layers.CurrentLayerIndex : UzipCommon.EnsureLayer(doc, _labelLayer, Color.White);
      if (outputLayer < 0) return Guid.Empty;
      return CommitPartLabel(doc, entity, outputLayer, existing, groups);
    }
  }

  private static Guid CommitPartLabel(RhinoDoc doc, TextEntity entity, int layer, RhinoObject? existing, IReadOnlyList<int> groups)
  {
    using var attributes = existing?.Attributes.Duplicate() ?? new ObjectAttributes();
    attributes.Name = LabelObjectName;
    attributes.LayerIndex = layer;
    if (existing == null)
    {
      foreach (int group in groups) attributes.AddToGroup(group);
      var added = doc.Objects.AddText(entity, attributes);
      if (added != Guid.Empty) L($"part label added: {added}");
      return added;
    }
    var id = existing.Id;
    using var before = ((TextEntity)existing.Geometry).Duplicate() as TextEntity;
    using var beforeAttributes = existing.Attributes.Duplicate();
    if (before == null || !doc.Objects.Replace(id, entity)) return Guid.Empty;
    if (!doc.Objects.ModifyAttributes(id, attributes, true))
    {
      doc.Objects.Replace(id, before);
      doc.Objects.ModifyAttributes(id, beforeAttributes, true);
      L("part label update rolled back");
      return Guid.Empty;
    }
    L($"part label updated: {id}");
    return id;
  }

  private sealed class PartLabelVisibility(RhinoDoc doc, Guid id) : DisplayConduit, IDisposable
  {
    protected override void ObjectCulling(CullObjectEventArgs e)
    {
      if (e.RhinoObject?.Document == doc && e.RhinoObject.Id == id) e.CullObject = true;
    }
    protected override void PreDrawObject(DrawObjectEventArgs e)
    {
      if (e.RhinoObject?.Document == doc && e.RhinoObject.Id == id) e.DrawObject = false;
    }
    public void Dispose() { Enabled = false; doc.Views.Redraw(); }
  }

  private static double ShiftLabelSize(double startSize, double upwardPixels) =>
    Math.Max(1, Math.Round(startSize + upwardPixels / ConfiguredLabelSizePixelsPerUnit, MidpointRounding.AwayFromZero));

  private sealed record PartLabelConfirm;

  private sealed class PartLabelDrag(RhinoDoc doc, TextEntity entity, Plane plane,
    Action<Point3d, System.Drawing.Point> begin, Action<Point3d, System.Drawing.Point> update) : Rhino.UI.MouseCallback, IDisposable
  {
    private System.Drawing.Point? _start;
    private Guid _viewport;
    private bool _dragged;
    private bool _modifierInteraction;
    internal bool Moved => _dragged;

    private static bool PointAt(Rhino.UI.MouseCallbackEventArgs e, Plane plane, out Point3d point)
    {
      point = Point3d.Unset;
      return e.View?.ActiveViewport is { } viewport && PointAt(viewport, e.ViewportPoint, plane, out point);
    }

    private static bool PointAt(Rhino.Display.RhinoViewport viewport, System.Drawing.Point cursor, Plane plane, out Point3d point)
    {
      point = Point3d.Unset;
      if (!viewport.GetFrustumLine(cursor.X, cursor.Y, out var ray) ||
        !Intersection.LinePlane(ray, plane, out double t)) return false;
      point = ray.PointAt(t); return point.IsValid;
    }

    internal void UpdateModifiers(Rhino.Display.RhinoViewport viewport, System.Drawing.Point cursor)
    {
      if (_start != null) return;
      var modifiers = System.Windows.Forms.Control.ModifierKeys;
      if (!modifiers.HasFlag(System.Windows.Forms.Keys.Alt) && !modifiers.HasFlag(System.Windows.Forms.Keys.Shift))
      { _modifierInteraction = false; return; }
      if (!PointAt(viewport, cursor, plane, out var point)) return;
      if (!_modifierInteraction) { begin(point, cursor); _modifierInteraction = true; }
      update(point, cursor);
    }

    protected override void OnMouseDown(Rhino.UI.MouseCallbackEventArgs e)
    {
      if (RhinoDoc.ActiveDoc != doc || e.MouseButton != Rhino.UI.MouseButton.Left ||
        e.View?.ActiveViewport is not { } viewport || !PointAt(e, plane, out var point)) { base.OnMouseDown(e); return; }
      var bounds = AnnotationTextTransform.TextBounds(entity);
      var corners = bounds.IsValid ? bounds.GetCorners().Select(p => entity.Plane.PointAt(p.X, p.Y, p.Z)).Select(viewport.WorldToClient).ToArray() : [];
      if (corners.Length == 0) { base.OnMouseDown(e); return; }
      var cursor = e.ViewportPoint;
      int padding = ConfiguredLabelPointerThresholdPixels;
      if (cursor.X < corners.Min(p => p.X) - padding || cursor.X > corners.Max(p => p.X) + padding ||
        cursor.Y < corners.Min(p => p.Y) - padding || cursor.Y > corners.Max(p => p.Y) + padding) { base.OnMouseDown(e); return; }
      _start = cursor; _viewport = viewport.Id; _dragged = false; _modifierInteraction = false;
      begin(point, cursor);
      e.Cancel = true;
      base.OnMouseDown(e);
    }

    protected override void OnMouseMove(Rhino.UI.MouseCallbackEventArgs e)
    {
      if (_start is { } start && e.View?.ActiveViewport.Id == _viewport && PointAt(e, plane, out var point))
      {
        _dragged |= Math.Abs(e.ViewportPoint.X - start.X) >= ConfiguredLabelPointerThresholdPixels ||
          Math.Abs(e.ViewportPoint.Y - start.Y) >= ConfiguredLabelPointerThresholdPixels;
        if (_dragged) update(point, e.ViewportPoint);
      }
      base.OnMouseMove(e);
    }

    protected override void OnMouseUp(Rhino.UI.MouseCallbackEventArgs e)
    {
      if (_start != null && e.MouseButton == Rhino.UI.MouseButton.Left)
      {
        e.Cancel = true;
        if (!_dragged) GetBaseClass.PostCustomMessage(new PartLabelConfirm());
        _start = null;
      }
      base.OnMouseUp(e);
    }
    public void Dispose() { Enabled = false; _start = null; _modifierInteraction = false; }
  }

  private static bool PromptForLayer(
    RhinoDoc doc,
    RunMode mode,
    DuplicateOutputLayerSession layerSession)
  {
    if (!LayerSelector.TrySelect(
          doc,
          layerSession.OptionLayerName,
          DuplicateCommandSupport.CurrentLayerOption,
          "vPart perimeter layer",
          mode,
          allowNewLayer: false,
          specialChoices:
          [
            new LayerSelector.SpecialChoice(
              SourceLayerOption,
              SourceLayerOption)
          ],
          out var selectedLayer))
      return false;

    _layer = NormalizePerimeterLayerOption(selectedLayer);
    layerSession.ApplyOption(doc, _layer);
    SaveOptions();
    return true;
  }

  private static string NormalizePerimeterLayerOption(string? layerName)
  {
    var normalized = layerName?.Trim() ?? string.Empty;
    if (string.Equals(
          normalized.Trim('*'),
          SourceLayerOption.Trim('*'),
          StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
          normalized.Trim('*'),
          "Original",
          StringComparison.OrdinalIgnoreCase))
      return SourceLayerOption;

    return DuplicateCommandSupport.NormalizeLayerOption(normalized);
  }

  private static bool IsSourceLayerOption(string? layerName) =>
    string.Equals(
      NormalizePerimeterLayerOption(layerName),
      SourceLayerOption,
      StringComparison.OrdinalIgnoreCase);

  private static int ResolvePerimeterLayerIndex(
    RhinoDoc doc,
    DuplicateOutputLayerSession layerSession,
    ObjectAttributes? sourceAttributes,
    ObjectAttributes fallbackSourceAttributes)
  {
    if (IsSourceLayerOption(_layer))
    {
      var sourceLayerIndex = sourceAttributes?.LayerIndex ?? RhinoMath.UnsetIntIndex;
      if (IsUsableLayer(doc, sourceLayerIndex))
        return sourceLayerIndex;
      if (IsUsableLayer(doc, fallbackSourceAttributes.LayerIndex))
        return fallbackSourceAttributes.LayerIndex;
    }

    return layerSession.CreateAttributes(doc).LayerIndex;
  }

  private static HashSet<int> ResolveCleanupLayerIndices(
    RhinoDoc doc,
    DuplicateOutputLayerSession layerSession,
    IReadOnlySet<int> sourcePerimeterLayerIndices,
    ObjectAttributes fallbackSourceAttributes)
  {
    if (IsSourceLayerOption(_layer))
      return sourcePerimeterLayerIndices.Count == 1
        ? sourcePerimeterLayerIndices.ToHashSet()
        : [];

    var outputLayerIndex = ResolvePerimeterLayerIndex(
      doc,
      layerSession,
      sourceAttributes: null,
      fallbackSourceAttributes);
    return outputLayerIndex >= 0
      ? [outputLayerIndex]
      : [];
  }

  private static string DescribeLayers(
    RhinoDoc doc,
    IEnumerable<int> layerIndices) =>
    string.Join(
      ", ",
      layerIndices
        .OrderBy(layerIndex => layerIndex)
        .Select(layerIndex => IsUsableLayer(doc, layerIndex)
          ? doc.Layers[layerIndex].FullPath
          : layerIndex.ToString()));

  private static bool IsUsableLayer(RhinoDoc doc, int layerIndex) =>
    layerIndex >= 0 &&
    layerIndex < doc.Layers.Count &&
    doc.Layers[layerIndex] is { IsDeleted: false };

  // ── Helpers ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a single closed loop for containment testing. The source document
  /// geometry is untouched; working copies may be extended to selected curves.
  /// Gap-bridging segments are returned separately for the output Part.
  /// </summary>
  private static (Curve? Closed, List<LineCurve> Bridges, List<Curve> PerimeterCurves, double BoundaryTolerance) BuildClosedPerimeter(
    List<Curve> curves, Plane plane, double tol, List<string> log, IReadOnlyList<Point3d>? pickPoints = null)
  {
    var bridges = new List<LineCurve>();
    var workingCurves = curves.Select(c => c.DuplicateCurve()).ToList();

    // Single closed curve → nothing to do
    if (curves.Count == 1 && curves[0].IsClosed)
      return (curves[0].DuplicateCurve(), bridges, workingCurves, tol);

    // Primary: CreateBooleanRegions handles curves that already meet or cross.
    var boundary = TryCreatePickedBoundary(
      workingCurves, pickPoints, plane, tol, "original", log, out var boundaryTolerance, out var preferredPicks);
    if (boundary != null && (preferredPicks.Count == 0 || !workingCurves.Any(curve =>
          !curve.IsClosed && !CurveContributesToBoundary(curve, boundary, plane, boundaryTolerance) &&
          InteriorSelectionSamples.Any(fraction => boundary.Contains(
            curve.PointAtNormalizedLength(fraction), plane, boundaryTolerance) == PointContainment.Inside))))
      return (boundary, bridges, workingCurves, boundaryTolerance);

    // If an open end stops short, extend it in its own end direction until it
    // reaches another selected perimeter curve. Only keep these working copies
    // when they form a genuine Boolean region.
    var extendedEnds = ExtendDisconnectedEndsToSelectedCurves(workingCurves, tol, log);
    if (extendedEnds > 0)
    {
      var extendedBoundary = TryCreatePickedBoundary(
        workingCurves, pickPoints, plane, tol, "extended", log, out var extendedTolerance, out _);
      if (extendedBoundary != null)
      {
        boundary?.Dispose();
        return (extendedBoundary, bridges, workingCurves, extendedTolerance);
      }
    }
    if (boundary != null)
      return (boundary, bridges, workingCurves, boundaryTolerance);

    log.Add("vPart[perim]: no closed Boolean region after end extension — trying endpoint join");

    // Fallback: endpoint joining with gap bridging
    var pieces = workingCurves.Select(c => c.DuplicateCurve()).ToList();

    // Greedily bridge nearest open-endpoint pairs across different curves
    var openEnds = new List<(int CrvIdx, bool IsStart, Point3d Pt)>();
    for (var i = 0; i < pieces.Count; i++)
    {
      if (pieces[i].IsClosed) continue;
      openEnds.Add((i, true,  pieces[i].PointAtStart));
      openEnds.Add((i, false, pieces[i].PointAtEnd));
    }

    // Log gap distances before bridging
    log.Add($"vPart[perim]: {CountText.Format(curves.Count, "curve")} — {CountText.Format(openEnds.Count, "open endpoint")}");
    for (var di = 0; di < openEnds.Count; di++)
      for (var dj = di + 1; dj < openEnds.Count; dj++)
      {
        if (openEnds[dj].CrvIdx == openEnds[di].CrvIdx) continue;
        var gapDist = openEnds[di].Pt.DistanceTo(openEnds[dj].Pt);
        var tag = gapDist <= tol      ? "(coincident)"
                : gapDist < tol * 200 ? "(will bridge)"
                : $"(TOO FAR — {gapDist / tol:F0}× tol, not bridged)";
        log.Add($"  [{openEnds[di].CrvIdx}/{(openEnds[di].IsStart ? "S" : "E")}]↔[{openEnds[dj].CrvIdx}/{(openEnds[dj].IsStart ? "S" : "E")}] dist={gapDist:F4} {tag}");
      }

    var used = new HashSet<int>();
    for (var i = 0; i < openEnds.Count; i++)
    {
      if (used.Contains(i)) continue;
      var bestDist = tol * 200;
      var bestJ    = -1;
      for (var j = i + 1; j < openEnds.Count; j++)
      {
        if (used.Contains(j)) continue;
        if (openEnds[j].CrvIdx == openEnds[i].CrvIdx) continue;
        var d = openEnds[i].Pt.DistanceTo(openEnds[j].Pt);
        if (d > tol && d < bestDist) { bestDist = d; bestJ = j; }
      }
      if (bestJ >= 0)
      {
        var bridge = new LineCurve(openEnds[i].Pt, openEnds[bestJ].Pt);
        bridges.Add(bridge);
        pieces.Add(bridge);
        used.Add(i);
        used.Add(bestJ);
      }
    }

    log.Add($"vPart[perim]: {CountText.Format(bridges.Count, "bridge")} added — {CountText.Format(pieces.Count, "piece")} for join");

    // Join copies (only for containment testing)
    var joined = Curve.JoinCurves(pieces.ToArray(), tol * 10);
    log.Add($"vPart[perim]: join@tol×10 → {(joined == null ? "null" : $"{CountText.Format(joined.Length, "result")}, closed={joined.Any(c => c.IsClosed)}")} ");
    var joinedClosed = joined?.Where(c => c.IsClosed).OrderByDescending(ClosedCurveArea).FirstOrDefault();
    if (joinedClosed != null)
      return (joinedClosed, bridges, workingCurves, tol * 10.0);

    var final = Curve.JoinCurves(pieces.ToArray(), tol * 100);
    log.Add($"vPart[perim]: join@tol×100 → {(final == null ? "null" : $"{CountText.Format(final.Length, "result")}, closed={final.Any(c => c.IsClosed)}")} ");
    var finalClosed = final?.Where(c => c.IsClosed).OrderByDescending(ClosedCurveArea).FirstOrDefault();
    if (finalClosed != null)
      return (finalClosed, bridges, workingCurves, tol * 100.0);

    log.Add("vPart[perim]: FAILED — endpoints not reachable or loop not closed");
    return (null, bridges, workingCurves, tol);
  }

  private static Curve? TryCreateBooleanBoundary(
    List<Curve> curves,
    Plane plane,
    double tol,
    string stage,
    List<string> log,
    out double usedTolerance,
    IReadOnlyList<Point3d>? preferredPicks = null)
  {
    usedTolerance = tol;
    foreach (var multiplier in new[] { 1.0, 10.0, 100.0 })
    {
      var regionTolerance = tol * multiplier;
      using var regions = Curve.CreateBooleanRegions(
        curves.ToArray(), plane, combineRegions: preferredPicks == null || preferredPicks.Count == 0, regionTolerance);
      if (regions == null)
      {
        log.Add($"vPart[perim]: {stage}@tol×{multiplier:G}: CreateBooleanRegions returned null");
        continue;
      }

      Curve? bestBoundary = null;
      var bestArea = double.NegativeInfinity;
      int bestPickRank = int.MaxValue;

      for (var r = 0; r < regions.RegionCount; r++)
      {
        var regionCurves = regions.RegionCurves(r);
        if (regionCurves == null || regionCurves.Length == 0)
          continue;

        bool usesOriginalCurve = regionCurves[0].IsClosed;
        var outerCandidates = usesOriginalCurve
          ? new[] { regionCurves[0] }
          : Curve.JoinCurves(regionCurves, regionTolerance * 10.0) ?? [];
        try
        {
          foreach (var candidate in outerCandidates)
          {
            if (candidate?.IsClosed != true)
              continue;

            var area = ClosedCurveArea(candidate);
            int pickRank = BoundaryPickRank(candidate, preferredPicks, plane, regionTolerance);
            if (pickRank > bestPickRank || pickRank == bestPickRank && area <= bestArea)
              continue;

            bestArea = area;
            bestPickRank = pickRank;
            bestBoundary?.Dispose();
            bestBoundary = candidate.DuplicateCurve();
          }
        }
        finally
        {
          if (!usesOriginalCurve)
            foreach (var candidate in outerCandidates) candidate?.Dispose();
          foreach (var regionCurve in regionCurves) regionCurve?.Dispose();
        }
      }

      log.Add($"vPart[perim]: {stage}@tol×{multiplier:G}: {CountText.Format(regions.RegionCount, "region")}, closed={bestBoundary != null}, area={Math.Max(0.0, bestArea):G6}, clickedSide={bestPickRank < int.MaxValue}");
      if (bestBoundary != null)
      {
        usedTolerance = regionTolerance;
        return bestBoundary;
      }
    }

    return null;
  }

  private static Curve? TryCreatePickedBoundary(List<Curve> curves,
    IReadOnlyList<Point3d>? picks, Plane plane, double tolerance, string stage,
    List<string> log, out double usedTolerance, out List<Point3d> preferredPicks)
  {
    var outerBoundary = TryCreateBooleanBoundary(curves, plane, tolerance, stage, log, out usedTolerance);
    preferredPicks = PreferredBoundaryPicks(curves, picks, plane, usedTolerance, outerBoundary);
    if (preferredPicks.Count == 0) return outerBoundary;
    var pickedBoundary = TryCreateBooleanBoundary(
      curves, plane, tolerance, stage + "-clicked", log, out var pickedTolerance, preferredPicks);
    if (pickedBoundary == null) return outerBoundary;
    outerBoundary?.Dispose();
    usedTolerance = pickedTolerance;
    return pickedBoundary;
  }

  private static List<Point3d> PreferredBoundaryPicks(IReadOnlyList<Curve> curves,
    IReadOnlyList<Point3d>? picks, Plane plane, double tolerance, Curve? assembledBoundary)
  {
    var result = new List<Point3d>();
    if (picks == null || assembledBoundary == null) return result;
    bool hasInnerDivider = curves.Any(curve => !curve.IsClosed &&
      !CurveContributesToBoundary(curve, assembledBoundary, plane, tolerance) &&
      InteriorSelectionSamples.Any(fraction => assembledBoundary.Contains(
        curve.PointAtNormalizedLength(fraction), plane, tolerance) == PointContainment.Inside));
    if (!hasInnerDivider) return result;
    for (int outer = curves.Count - 1; outer >= 0; outer--)
    {
      if (outer >= picks.Count || !picks[outer].IsValid ||
          !CurveContributesToBoundary(curves[outer], assembledBoundary, plane, tolerance) ||
          assembledBoundary.Contains(picks[outer], plane, tolerance) != PointContainment.Coincident) continue;
      result.Add(picks[outer]);
    }
    return result;
  }

  private static int BoundaryPickRank(Curve candidate, IReadOnlyList<Point3d>? picks,
    Plane plane, double tolerance)
  {
    if (picks != null)
      for (int index = 0; index < picks.Count; index++)
        if (candidate.Contains(picks[index], plane, tolerance) is PointContainment.Inside or PointContainment.Coincident)
          return index;
    return int.MaxValue;
  }

  private static int ExtendDisconnectedEndsToSelectedCurves(
    List<Curve> curves, double tol, List<string> log)
  {
    var extendedEnds = 0;
    var touchTolerance = tol * 10.0;
    var bounds = BoundingBox.Empty;
    foreach (var curve in curves)
      bounds.Union(curve.GetBoundingBox(true));
    var probeLength = Math.Max(bounds.IsValid ? bounds.Diagonal.Length * 4.0 : 0.0, tol * 1000.0);

    // Two passes allow one newly extended curve to become a target for another.
    for (var pass = 0; pass < 2; pass++)
    {
      var changed = false;
      for (var i = 0; i < curves.Count; i++)
      {
        if (curves[i].IsClosed)
          continue;

        foreach (var end in new[] { CurveEnd.Start, CurveEnd.End })
        {
          var curve = curves[i];
          var endpoint = end == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;
          if (EndpointTouchesAnotherCurve(endpoint, curves, i, touchTolerance))
            continue;

          var drivers = curves
            .Where((_, index) => index != i)
            .Cast<GeometryBase>()
            .ToArray();
          Curve? extended;
          try
          {
            extended = curve.Extend(end, CurveExtensionStyle.Line, drivers);
          }
          catch (Exception ex)
          {
            extended = null;
            log.Add($"vPart[perim]: curve {i} {end} boundary extension threw {ex.GetType().Name}");
          }

          var extensionMethod = "boundary";
          var extensionDistance = 0.0;
          if (extended != null)
          {
            var extendedEndpoint = end == CurveEnd.Start ? extended.PointAtStart : extended.PointAtEnd;
            if (!EndpointTouchesAnotherCurve(extendedEndpoint, curves, i, touchTolerance))
              extended = null;
          }

          if (extended == null)
          {
            extended = ExtendEndToNearestForwardIntersection(
              curve, end, curves, i, probeLength, touchTolerance, out extensionDistance);
            extensionMethod = "ray";
          }

          if (extended == null)
          {
            log.Add($"vPart[perim]: curve {i} {end} has no forward selected-curve hit within {probeLength:G6}");
            continue;
          }

          var addedLength = extended.GetLength() - curve.GetLength();
          if (addedLength <= tol)
            continue;

          curves[i] = extended;
          extendedEnds++;
          changed = true;
          var hit = extensionDistance > 0.0 ? $" hit={extensionDistance:G6}" : string.Empty;
          log.Add($"vPart[perim]: extended curve {i} {end} by {addedLength:G6} via {extensionMethod}{hit}");
        }
      }

      if (!changed)
        break;
    }

    log.Add($"vPart[perim]: extended {CountText.Format(extendedEnds, "disconnected end")}");
    return extendedEnds;
  }

  private static Curve? ExtendEndToNearestForwardIntersection(
    Curve curve,
    CurveEnd end,
    List<Curve> curves,
    int curveIndex,
    double probeLength,
    double tol,
    out double hitDistance)
  {
    hitDistance = double.PositiveInfinity;
    var bestDistance = double.PositiveInfinity;

    var endpoint = end == CurveEnd.Start ? curve.PointAtStart : curve.PointAtEnd;
    var direction = end == CurveEnd.Start ? -curve.TangentAtStart : curve.TangentAtEnd;
    if (!direction.Unitize())
      return null;

    Curve? probe;
    try
    {
      probe = curve.Extend(end, probeLength, CurveExtensionStyle.Line);
    }
    catch
    {
      probe = null;
    }

    if (probe == null)
      return null;

    var bestPoint = Point3d.Unset;

    void Consider(Point3d point)
    {
      if (!point.IsValid)
        return;

      var delta = point - endpoint;
      var forward = Vector3d.Multiply(delta, direction);
      if (forward <= tol || forward >= bestDistance)
        return;

      // Ignore intersections on the original portion of the curve.
      if (curve.ClosestPoint(point, out var originalT)
          && point.DistanceTo(curve.PointAt(originalT)) <= tol)
        return;

      bestPoint = point;
      bestDistance = forward;
    }

    for (var i = 0; i < curves.Count; i++)
    {
      if (i == curveIndex)
        continue;

      var events = Intersection.CurveCurve(probe, curves[i], tol, tol);
      if (events == null)
        continue;

      foreach (var intersection in events)
      {
        Consider(intersection.PointA);
        if (intersection.IsOverlap)
          Consider(intersection.PointA2);
      }
    }

    if (!bestPoint.IsValid || double.IsPositiveInfinity(bestDistance))
      return null;

    hitDistance = bestDistance;

    try
    {
      var toPoint = curve.Extend(end, CurveExtensionStyle.Line, bestPoint);
      if (toPoint != null)
        return toPoint;
    }
    catch
    {
    }

    if (!probe.ClosestPoint(bestPoint, out var hitT))
      return null;

    var oppositePoint = end == CurveEnd.Start ? curve.PointAtEnd : curve.PointAtStart;
    if (!probe.ClosestPoint(oppositePoint, out var oppositeT))
      return null;

    var interval = new Interval(Math.Min(hitT, oppositeT), Math.Max(hitT, oppositeT));
    return probe.Trim(interval);
  }

  private static bool EndpointTouchesAnotherCurve(
    Point3d endpoint, List<Curve> curves, int curveIndex, double tol)
  {
    for (var i = 0; i < curves.Count; i++)
    {
      if (i == curveIndex || !curves[i].ClosestPoint(endpoint, out var t))
        continue;

      if (endpoint.DistanceTo(curves[i].PointAt(t)) <= tol)
        return true;
    }

    return false;
  }

  private static double ClosedCurveArea(Curve curve)
  {
    using var properties = AreaMassProperties.Compute(curve);
    return properties?.Area ?? 0.0;
  }

  /// <summary>  /// Splits each perimeter curve at its intersections with the other perimeter
  /// curves, then keeps only the segments whose midpoint lies on the closed
  /// boundary (within tol\u00d710).  For already-trimmed curves this returns them
  /// whole; for curves extending past corners, the protruding portions are
  /// discarded.
  /// </summary>
  private static IEnumerable<(Curve Crv, ObjectAttributes Attr)> TrimToPerimeter(
    List<(Curve Crv, ObjectAttributes Attr)> source,
    Curve boundary,
    Plane plane,
    double tol)
  {
    var crvs = source.Select(s => s.Crv).ToList();
    var boundaryList = new List<Curve> { boundary };
    var boundaryMatchTolerance = tol * 2.0;
    for (var si = 0; si < source.Count; si++)
    {
      var (crv, attr) = source[si];
      var splitParams  = new SortedSet<double>();
      var keptCount = 0;
      var parameterTolerance = Math.Max(RhinoMath.ZeroTolerance, tol * 0.1);

      void AddSplitParameter(double parameter)
      {
        if (parameter <= crv.Domain.T0 + parameterTolerance
            || parameter >= crv.Domain.T1 - parameterTolerance)
          return;

        splitParams.Add(parameter);
      }

      for (var oi = 0; oi < crvs.Count; oi++)
      {
        if (oi == si) continue;
        foreach (var parameter in CollectPerimeterSplitParams(crv, crvs[oi], plane, tol))
          AddSplitParameter(parameter);
      }

      foreach (var parameter in CollectPerimeterSplitParams(crv, boundary, plane, tol))
        AddSplitParameter(parameter);

      if (splitParams.Count == 0)
      {
        // Use a broad IsOnCurve check for boundary-hugging curves, but also
        // include curves that are entirely inside the perimeter. The later
        // perimeter-layer cleanup can still omit straight interior pieces.
        var mid = crv.PointAtNormalizedLength(0.5);
        if (IsOnCurve(mid, boundary, boundaryMatchTolerance) || IsInsideOrOn(mid, boundaryList, plane, tol))
        {
          yield return (crv.DuplicateCurve(), attr);
          keptCount++;
        }
      }
      else
      {
        var segs = crv.Split(splitParams);
        if (segs == null) continue;
        foreach (var seg in segs)
        {
          if (seg.GetLength() < tol) continue;
          var mid = seg.PointAtNormalizedLength(0.5);
          if (IsOnCurve(mid, boundary, boundaryMatchTolerance)
              || IsInsideOrOn(mid, boundaryList, plane, tol))
          {
            yield return (seg, attr);
            keptCount++;
          }
        }
      }


      L($"trim source {si}: splitParams={splitParams.Count} kept={keptCount} length={crv.GetLength():G6}");
    }
  }

  private static bool IsOnCurve(Point3d pt, Curve crv, double tol)
  {
    if (!pt.IsValid || !crv.ClosestPoint(pt, out var t)) return false;
    return pt.DistanceTo(crv.PointAt(t)) <= tol;
  }

  /// <summary>  /// Returns all visible objects (excluding selected perimeter IDs) whose
  /// content falls inside the closed perimeter.
  /// • Curves: split at the perimeter boundary, keep inside segments.
  /// • All other types (text, dots, points, hatches, …): included whole when
  ///   the representative point is inside.
  /// </summary>
  private static List<(GeometryBase Geom, ObjectAttributes Attr)> CollectInsideObjects(
    RhinoDoc doc,
    HashSet<Guid> excludeIds,
    IReadOnlyDictionary<Guid, HashSet<int>> selectedSegmentIndices,
    Curve perimeter,
    Plane plane,
    double tol)
  {
    var result   = new List<(GeometryBase, ObjectAttributes)>();
    var boundary = new List<Curve> { perimeter };

    var settings = new ObjectEnumeratorSettings
    {
      ObjectTypeFilter = ObjectType.AnyObject,
      VisibleFilter    = true,
      DeletedObjects   = false,
      IncludeGrips     = false
    };

    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj.ObjectType == ObjectType.Grip) continue;
      if (excludeIds.Contains(obj.Id)) continue;
      var geom = obj.Geometry;
      if (geom == null) continue;

      var attr = obj.Attributes.Duplicate();

      if (geom is Curve crv)
      {
        // Curves: split at perimeter and keep only inside pieces.
        // If there are no split points, include the whole curve only when the
        // whole sampled curve is inside. This prevents a curve with midpoint
        // inside but ends sticking out from being copied untrimmed.
        var segments = selectedSegmentIndices.TryGetValue(obj.Id, out var selectedIndices)
          ? crv.DuplicateSegments()
          : null;
        var candidates = segments == null
          ? new[] { crv }
          : segments.Where((_, index) => !selectedIndices!.Contains(index));
        try
        {
          foreach (var candidate in candidates)
            foreach (var insidePiece in TrimCurveInsidePerimeter(candidate, perimeter, plane, tol))
              result.Add((insidePiece, attr));
        }
        finally
        {
          if (segments != null)
            foreach (var segment in segments)
              segment.Dispose();
        }
      }
      else
      {
        // All other types: test a representative point for containment
        var testPt = RepresentativePoint(geom);
        if (testPt.IsValid && IsInsideOrOn(testPt, boundary, plane, tol))
          result.Add((geom.Duplicate()!, attr));
      }
    }

    return result;
  }

  private static int ClosestSourceLayerIndex(
    IReadOnlyList<(Curve Crv, ObjectAttributes Attr)> sources,
    Point3d boundaryPoint)
  {
    var closestLayerIndex = RhinoMath.UnsetIntIndex;
    var closestDistance = double.PositiveInfinity;
    foreach (var (curve, attributes) in sources)
    {
      if (!curve.ClosestPoint(boundaryPoint, out var parameter))
        continue;

      var distance = boundaryPoint.DistanceTo(curve.PointAt(parameter));
      if (distance >= closestDistance)
        continue;

      closestDistance = distance;
      closestLayerIndex = attributes.LayerIndex;
    }

    return closestLayerIndex;
  }

  private static bool IsNotchCurve(ObjectAttributes attributes) =>
    string.Equals(
      attributes.Name,
      NotchObjectName,
      StringComparison.OrdinalIgnoreCase) ||
    string.Equals(
      attributes.GetUserString(NotchRoleKey),
      "notch",
      StringComparison.OrdinalIgnoreCase) ||
    !string.IsNullOrWhiteSpace(attributes.GetUserString(NotchIdKey));

  private static bool IsRemovableInteriorLine(
    Curve curve,
    ObjectAttributes attributes,
    IReadOnlySet<int> perimeterLayerIndices,
    Curve perimeter,
    Plane plane,
    double tolerance)
  {
    if (!perimeterLayerIndices.Contains(attributes.LayerIndex) ||
        !curve.IsLinear(tolerance) ||
        IsNotchCurve(attributes))
      return false;

    var midpoint = curve.PointAtNormalizedLength(0.5);
    return perimeter.Contains(
      midpoint, plane, tolerance * ConfiguredInteriorLineBoundaryToleranceScale)
      == PointContainment.Inside;
  }

  private static bool CurveContributesToBoundary(
    Curve curve,
    Curve perimeter,
    Plane plane,
    double tolerance)
  {
    var overlapEvents = Intersection.CurveCurve(
      curve,
      perimeter,
      tolerance * ConfiguredInteriorLineBoundaryToleranceScale,
      tolerance * ConfiguredInteriorLineBoundaryToleranceScale);
    if (overlapEvents != null && overlapEvents.Any(intersection =>
          intersection.IsOverlap &&
          Math.Abs(intersection.OverlapA.Length) > RhinoMath.ZeroTolerance))
      return true;

    for (var sampleIndex = 0;
         sampleIndex < ConfiguredPerimeterContributionSampleCount;
         sampleIndex++)
    {
      var normalizedLength =
        (sampleIndex + 0.5) / ConfiguredPerimeterContributionSampleCount;
      if (perimeter.Contains(
            curve.PointAtNormalizedLength(normalizedLength), plane,
            tolerance * ConfiguredInteriorLineBoundaryToleranceScale)
          == PointContainment.Coincident)
        return true;
    }

    return false;
  }

  private static IEnumerable<Curve> TrimCurveInsidePerimeter(Curve crv, Curve perimeter, Plane plane, double tol)
  {
    var boundary = new List<Curve> { perimeter };

    // Fast path: when intersection parameters are available, split directly.
    // The slower sampling fallback is only used when intersections fail.
    var splitParams = CollectPerimeterSplitParams(crv, perimeter, plane, tol);
    if (splitParams.Count > 0)
    {
      var pieces = crv.IsClosed
        ? SplitClosedCurveByParameters(crv, splitParams, tol)
        : crv.Split(splitParams.ToArray()) ?? Enumerable.Empty<Curve>();

      foreach (var piece in pieces)
      {
        if (piece.GetLength() < tol)
          continue;

        if (IsInsideOrOn(
              piece.PointAtNormalizedLength(0.5), boundary, plane,
              RhinoMath.ZeroTolerance))
          yield return piece;
      }

      yield break;
    }

    // No intersection points. Keep/discard most curves from a small sample and
    // only run the expensive bisection sampler for mixed inside/outside curves.
    var quickSamples = SampleCurveInsideState(crv, boundary, plane, tol, 17);
    if (quickSamples.All(s => s.Inside))
    {
      yield return crv.DuplicateCurve();
      yield break;
    }

    if (quickSamples.All(s => !s.Inside))
      yield break;

    // Model-tolerance coincidence at an outside curve's endpoint is only a
    // contact, not a tiny interior interval that should be copied into the part.
    var intervalSamples = SampleCurveInsideState(
      crv, boundary, plane, RhinoMath.ZeroTolerance, 17);
    var intervals = FindInsideCurveIntervals(
      crv, boundary, plane, RhinoMath.ZeroTolerance, intervalSamples);
    foreach (var (a, b) in intervals)
    {
      var piece = TrimCurveInterval(crv, a, b, tol);
      if (piece != null && piece.GetLength() >= tol)
        yield return piece;
    }
  }

  private static List<(double T, bool Inside)> SampleCurveInsideState(
    Curve crv,
    List<Curve> boundary,
    Plane plane,
    double tol,
    int sampleCount)
  {
    var samples = new List<(double T, bool Inside)>();
    var domain = crv.Domain;
    sampleCount = Math.Max(1, sampleCount);

    for (var i = 0; i <= sampleCount; i++)
    {
      var t = domain.ParameterAt(i / (double)sampleCount);
      samples.Add((t, IsInsideOrOn(crv.PointAt(t), boundary, plane, tol)));
    }

    return samples;
  }

  private static List<(double A, double B)> FindInsideCurveIntervals(
    Curve crv,
    List<Curve> boundary,
    Plane plane,
    double tol,
    List<(double T, bool Inside)> initialSamples)
  {
    var result = new List<(double A, double B)>();
    var domain = crv.Domain;
    var parameterSpan = domain.T1 - domain.T0;
    if (parameterSpan <= RhinoMath.ZeroTolerance || initialSamples.Count < 2)
      return result;

    double? insideStart = initialSamples[0].Inside ? domain.T0 : null;

    for (var i = 1; i < initialSamples.Count; i++)
    {
      var prev = initialSamples[i - 1];
      var cur = initialSamples[i];

      if (prev.Inside == cur.Inside)
        continue;

      var crossing = FindContainmentChangeParameter(crv, boundary, plane, tol, prev.T, cur.T, prev.Inside);

      if (!prev.Inside && cur.Inside)
      {
        insideStart = crossing;
      }
      else if (prev.Inside && !cur.Inside && insideStart.HasValue)
      {
        if (crossing - insideStart.Value > RhinoMath.ZeroTolerance)
          result.Add((insideStart.Value, crossing));
        insideStart = null;
      }
    }

    if (insideStart.HasValue && domain.T1 - insideStart.Value > RhinoMath.ZeroTolerance)
      result.Add((insideStart.Value, domain.T1));

    return result;
  }

  private static double FindContainmentChangeParameter(
    Curve crv,
    List<Curve> boundary,
    Plane plane,
    double tol,
    double a,
    double b,
    bool insideAtA)
  {
    for (var i = 0; i < 40; i++)
    {
      var mid = 0.5 * (a + b);
      var insideAtMid = IsInsideOrOn(crv.PointAt(mid), boundary, plane, tol);

      if (insideAtMid == insideAtA)
        a = mid;
      else
        b = mid;
    }

    return 0.5 * (a + b);
  }

  private static Curve? TrimCurveInterval(Curve crv, double a, double b, double tol)
  {
    if (b - a <= RhinoMath.ZeroTolerance)
      return null;

    return crv.IsClosed
      ? TrimClosedInterval(crv, a, b, crv.Domain, tol)
      : crv.Trim(a, b);
  }

  private static List<double> CollectPerimeterSplitParams(Curve crv, Curve perimeter, Plane plane, double tol)
  {
    var splitParams = new List<double>();

    // NURBS projection also handles tilted arcs; map hits back to source parameters.
    using var crv2d = crv.ToNurbsCurve();
    using var perimeter2d = perimeter.ToNurbsCurve();
    var projection = Transform.PlanarProjection(plane);
    if (crv2d == null || perimeter2d == null ||
        !crv2d.Transform(projection) || !perimeter2d.Transform(projection))
      return splitParams;

    var events = Intersection.CurveCurve(crv2d, perimeter2d, tol, tol);
    if (events == null || events.Count == 0)
      events = Intersection.CurveCurve(crv2d, perimeter2d, tol * 10.0, tol * 10.0);

    if (events == null)
      return splitParams;

    void AddProjectedParameter(double parameter)
    {
      if (crv.GetCurveParameterFromNurbsFormParameter(parameter, out var sourceParameter))
        AddCurveSplitParam(crv, splitParams, sourceParameter, tol);
    }

    foreach (var ev in events)
    {
      if (ev.IsOverlap)
      {
        AddProjectedParameter(ev.OverlapA.T0);
        AddProjectedParameter(ev.OverlapA.T1);
      }
      else
      {
        AddProjectedParameter(ev.ParameterA);
      }
    }

    return splitParams.OrderBy(t => t).ToList();
  }

  private static void AddCurveSplitParam(Curve crv, List<double> splitParams, double t, double tol)
  {
    if (crv.IsClosed)
    {
      t = NormalizeClosedParameter(t, crv.Domain);
    }
    else
    {
      var d = crv.Domain;
      var paramTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
      if (t <= d.T0 + paramTol || t >= d.T1 - paramTol)
        return;
    }

    var duplicateTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
    if (!splitParams.Any(existing => Math.Abs(existing - t) <= duplicateTol))
      splitParams.Add(t);
  }

  private static IEnumerable<Curve> SplitClosedCurveByParameters(Curve crv, List<double> splitParams, double tol)
  {
    var domain = crv.Domain;
    var period = domain.T1 - domain.T0;
    if (period <= RhinoMath.ZeroTolerance)
      yield break;

    var paramTol = Math.Max(tol, RhinoMath.ZeroTolerance) * 10.0;
    var parameters = splitParams
      .Select(t => NormalizeClosedParameter(t, domain))
      .OrderBy(t => t)
      .ToList();

    if (parameters.Count < 2)
      yield break;

    for (var i = 0; i < parameters.Count; i++)
    {
      var a = parameters[i];
      var b = i == parameters.Count - 1 ? parameters[0] + period : parameters[i + 1];
      if (b - a <= paramTol)
        continue;

      var piece = TrimClosedInterval(crv, a, b, domain, tol);
      if (piece != null && piece.GetLength() >= tol)
        yield return piece;
    }
  }

  private static Curve? TrimClosedInterval(Curve crv, double a, double b, Interval domain, double tol)
  {
    var period = domain.T1 - domain.T0;

    while (a < domain.T0) a += period;
    while (a > domain.T1) a -= period;
    while (b < domain.T0) b += period;

    if (b <= domain.T1)
      return crv.Trim(a, b);

    var first = crv.Trim(a, domain.T1);
    var second = crv.Trim(domain.T0, b - period);

    if (first == null) return second;
    if (second == null) return first;

    var joined = Curve.JoinCurves(new[] { first, second }, tol);
    if (joined != null && joined.Length == 1)
      return joined[0];

    var pc = new PolyCurve();
    pc.Append(first);
    pc.Append(second);
    return pc;
  }

  private static double NormalizeClosedParameter(double t, Interval domain)
  {
    var period = domain.T1 - domain.T0;
    if (period <= RhinoMath.ZeroTolerance)
      return t;

    while (t < domain.T0) t += period;
    while (t > domain.T1) t -= period;
    return t;
  }

  private static bool AllSamplesInsideOrOn(Curve crv, List<Curve> boundary, Plane plane, double tol)
  {
    foreach (var pt in SampleCurve(crv, 17))
      if (!IsInsideOrOn(pt, boundary, plane, tol))
        return false;
    return true;
  }

  private static IEnumerable<Point3d> SampleCurve(Curve curve, int count)
  {
    count = Math.Max(1, count);

    for (var i = 0; i < count; i++)
    {
      var s = (i + 0.5) / count;
      yield return curve.PointAtNormalizedLength(s);
    }
  }

  /// <summary>Returns the most meaningful single point for containment testing.</summary>
  private static Point3d RepresentativePoint(GeometryBase geom)
  {
    switch (geom)
    {
      case TextEntity te:              return te.Plane.Origin;
      case TextDot td:                 return td.Point;
      case Rhino.Geometry.Point pt:    return pt.Location;
      default:
        var bb = geom.GetBoundingBox(true);
        return bb.IsValid ? bb.Center : Point3d.Unset;
    }
  }

  private static bool IsInsideOrOn(Point3d pt, List<Curve> closed, Plane plane, double tol)
  {
    if (!pt.IsValid) return false;
    foreach (var c in closed)
    {
      var r = c.Contains(pt, plane, tol);
      if (r == PointContainment.Inside || r == PointContainment.Coincident)
        return true;
    }
    return false;
  }

  private static void DrawPreview(DisplayPipeline display, GeometryBase geom, Color color)
  {
    switch (geom)
    {
      case Curve c:                          PreviewDisplay.DrawCurve(display, c, color, 1);        break;
      case TextEntity te:                    display.DrawAnnotation(te, color);                     break;
      case TextDot td:                       display.DrawDot(td, color, Color.Black, color);        break;
      case Rhino.Geometry.Point pt:          display.DrawPoint(pt.Location, color);                 break;
      case Mesh m:                           display.DrawMeshWires(m, color);                       break;
      case Brep b:                           PreviewDisplay.DrawBrepWires(display, b, color);        break;
    }
  }

  private static Guid AddObjectToDoc(RhinoDoc doc, GeometryBase geom, ObjectAttributes attr)
  {
    switch (geom)
    {
      case Curve c:                          return doc.Objects.AddCurve(c, attr);
      case TextEntity te:                    return doc.Objects.AddText(te, attr);
      case TextDot td:                       return doc.Objects.AddTextDot(td, attr);
      case Rhino.Geometry.Point pt:          return doc.Objects.AddPoint(pt.Location, attr);
      case Hatch h:                          return doc.Objects.AddHatch(h, attr);
      case Brep b:                           return doc.Objects.AddBrep(b, attr);
      case Mesh m:                           return doc.Objects.AddMesh(m, attr);
      default:                               return doc.Objects.Add(geom, attr);
    }
  }
}
