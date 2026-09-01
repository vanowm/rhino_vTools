using System;
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
/// Finds covered curves and faces that share surface area with other faces.
/// </summary>
public sealed class vOverlaps : vToolsCommand
{
  // Option defaults
  private const double DefaultTolerance = 0.001; // Comparison tolerance in model units; greater than zero.
  private const bool DefaultOverlapSegments = true; // true splits partial findings and selects only their overlapping pieces; false selects the chosen whole source curves.

  // Customizable selection and output behavior
  private const ObjectType SupportedGeometry = ObjectType.Curve | ObjectType.Surface | ObjectType.Brep; // Rhino object and subobject types accepted by the command.
  private static readonly Color OverlapAreaColor = Color.Cyan; // Fill color used to identify shared face area.
  private const double OverlapAreaTransparency = 0.15; // Shared-area fill transparency from 0.0 opaque to 1.0 invisible.
  private const int OverlapValidationSamples = 8; // Equal-length interior samples used to verify that a highlighted interval follows both source edges; integer two or greater.
  private const double FaceCoincidenceToleranceFactor = 5.0; // Multiplier applied to command tolerance only when deciding whether two face planes are close enough to compare; greater than or equal to one.
  private const double MinimumOverlapToleranceFactor = 10.0; // Minimum highlighted overlap length as a multiple of tolerance; rejects point-contact slivers reported as overlaps.
  private const double MinimumOverlapEdgeFraction = 1e-6; // Minimum highlighted overlap length as a fraction of the shorter source edge; positive fraction below one.
  private const int DoubleEscapeIntervalMilliseconds = 600; // Maximum elapsed milliseconds between two Esc presses that clear a persistent overlap highlight; positive integer.

  private const string SectionName = "vOverlaps";
  private const string TolKey = "tolerance";
  private const string LegacyCompareSegmentsKey = "compareSegments";
  private const string LegacySegmentsKey = "segments";
  private const string OverlapSegmentsKey = "overlapSegments";

  private static double _tolerance = DefaultTolerance;
  private static bool _overlapSegments = DefaultOverlapSegments;
  private static OverlapAreaConduit? _activeAreaHighlight;
  private static uint _activeAreaDocumentSerial;
  private static long _lastEscapeTick;

  public override string EnglishName => "vOverlaps";

  private static void LoadOptions() =>
    ToolsOptionStore.Read<int>(SectionName, section =>
    {
      _tolerance = DefaultTolerance;
      _overlapSegments = DefaultOverlapSegments;
      if (ToolsOptionStore.TryGetDouble(section, TolKey, out var tolerance) && tolerance > 0.0)
        _tolerance = tolerance;
      if (ToolsOptionStore.TryGetBool(section, OverlapSegmentsKey, out var overlapSegments))
        _overlapSegments = overlapSegments;
      return 0;
    });

  private static void SaveOptions() =>
    ToolsOptionStore.Update(SectionName, section =>
    {
      section[TolKey] = _tolerance;
      section.Remove(LegacySegmentsKey);
      section.Remove(LegacyCompareSegmentsKey);
      section[OverlapSegmentsKey] = _overlapSegments;
    });

  internal static (double Tolerance, bool OverlapSegments) GetDetectionSettings()
  {
    LoadOptions();
    return (_tolerance, _overlapSegments);
  }

  internal static void SetOverlapSegments(bool overlapSegments)
  {
    LoadOptions();
    _overlapSegments = overlapSegments;
    SaveOptions();
  }

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    DisableAreaHighlight(doc);
    LoadOptions();

    var selectedCurveIds = new HashSet<Guid>();
    var selectedFaces = new HashSet<FaceOverlapFinder.FaceReference>();
    SeedPreselection(doc, selectedCurveIds, selectedFaces);

    var firstPrompt = true;
    while (true)
    {
      SyncWorkingSelection(doc, selectedCurveIds, selectedFaces);
      using var getter = CreateGeometryGetter();
      var toleranceOption = new OptionDouble(_tolerance, 1e-9, 1e6);
      var overlapSegmentsOption = new OptionToggle(_overlapSegments, "No", "Yes");
      var toleranceOptionIndex = getter.AddOptionDouble("Tolerance", ref toleranceOption);
      getter.AddOptionToggle("OverlapSegments", ref overlapSegmentsOption);
      var addOptionIndex = getter.AddOption("AddMore");
      var removeOptionIndex = getter.AddOption("Remove");
      var allOptionIndex = getter.AddOption("AllVisible");

      var selectedCount = selectedCurveIds.Count + selectedFaces.Count;
      if (selectedCount == 0)
        getter.SetCommandPrompt("Select curves or faces (Enter = all visible)");
      else if (firstPrompt)
        getter.SetCommandPrompt($"{SelectionLabel(selectedCurveIds.Count, selectedFaces.Count)} - Enter to find overlaps, or add/remove");
      else
        getter.SetCommandPrompt($"{SelectionLabel(selectedCurveIds.Count, selectedFaces.Count)} - Enter to find overlaps");
      firstPrompt = false;

      var minimumSelectionCount = selectedCount == 0 ? 1 : 0;
      Log.Write(
        "vOverlaps",
        $"selection_prompt selected={selectedCount} minimum={minimumSelectionCount}");
      var getResult = getter.GetMultiple(minimumSelectionCount, 0);
      Log.Write(
        "vOverlaps",
        $"selection_result result={getResult} objects={getter.ObjectCount}");
      _tolerance = toleranceOption.CurrentValue;
      _overlapSegments = overlapSegmentsOption.CurrentValue;

      if (getResult == GetResult.Cancel)
        return Result.Cancel;

      if (getResult == GetResult.Number)
      {
        if (getter.Number() > 0.0)
          _tolerance = getter.Number();
        SaveOptions();
        continue;
      }

      if (getResult == GetResult.Option)
      {
        var optionIndex = getter.Option()?.Index ?? -1;
        SaveOptions();

        if (optionIndex == toleranceOptionIndex)
          continue;

        if (optionIndex == addOptionIndex)
        {
          PickWorkingSet(doc, add: true, selectedCurveIds, selectedFaces);
          continue;
        }

        if (optionIndex == removeOptionIndex)
        {
          PickWorkingSet(doc, add: false, selectedCurveIds, selectedFaces);
          continue;
        }

        if (optionIndex == allOptionIndex)
        {
          ClearWorkingSelection(doc, selectedCurveIds, selectedFaces);
          selectedCurveIds.Clear();
          selectedFaces.Clear();
          continue;
        }

        continue;
      }

      if (getResult == GetResult.Object)
      {
        AddReferences(getter.Objects(), selectedCurveIds, selectedFaces);
        break;
      }

      break;
    }

    SaveOptions();
    BuildInputs(
      doc,
      selectedCurveIds,
      selectedFaces,
      out var inputCurves,
      out var inputFaces);

    if (inputCurves.Count < 2 && inputFaces.Count < 2)
    {
      RhinoApp.WriteLine("vOverlaps: need at least 2 curves or 2 faces.");
      return Result.Nothing;
    }

    var connectivityCurves = doc.Objects
      .GetObjectList(ConnectivityCurveSettings())
      .Where(obj => obj?.Geometry is Curve && obj.IsValid)
      .ToList();
    var curveOverlaps = inputCurves.Count >= 2
      ? OverlapFinder.Find(
        inputCurves,
        _tolerance,
        connectivityCurves)
      : new OverlapFinder.Result([], [], [], inputCurves.Count, 0, 0, 0);

    var remainingPartialIds = new HashSet<Guid>(
      curveOverlaps.PartiallyOverlappingObjectIds);
    var materializedSegments = new OverlapSegmentProcessor.Result([], [], [], 0);
    if (_overlapSegments && curveOverlaps.PartialOverlapSpans.Count > 0)
    {
      var affectedSourceIds = curveOverlaps.PartialOverlapSpans
        .Select(span => span.ObjectId)
        .Where(id => !curveOverlaps.CoveredObjectIds.Contains(id))
        .Distinct()
        .ToList();
      var historyRecords = new HashSet<Guid>();
      foreach (var sourceId in affectedSourceIds)
        historyRecords.UnionWith(
          HistoryBreakWarning.CaptureAffectedRecords(doc, sourceId));
      if (!HistoryBreakWarning.Confirm(doc, EnglishName, historyRecords))
        return Result.Cancel;

      var undoRecord = doc.BeginUndoRecord("vOverlaps Overlap Segments");
      try
      {
        materializedSegments = OverlapSegmentProcessor.Materialize(
          doc,
          curveOverlaps.PartialOverlapSpans,
          curveOverlaps.CoveredObjectIds,
          _tolerance);
      }
      finally
      {
        if (undoRecord != 0)
          doc.EndUndoRecord(undoRecord);
      }
      remainingPartialIds.ExceptWith(
        materializedSegments.ProcessedSourceObjectIds);
      if (remainingPartialIds.Count > 0)
        Log.Write(
          "vOverlaps",
          $"overlap segment isolation skipped sources={remainingPartialIds.Count}");
    }
    var faceCoincidenceTolerance = Math.Max(
      _tolerance * FaceCoincidenceToleranceFactor,
      RhinoMath.ZeroTolerance);
    var faceAreaTolerance = Math.Max(_tolerance, RhinoMath.ZeroTolerance);
    var faceOverlaps = inputFaces.Count >= 2
      ? FaceOverlapFinder.Find(
        inputFaces,
        faceCoincidenceTolerance,
        faceAreaTolerance)
      : new FaceOverlapFinder.Result([], [], inputFaces.Count, 0, 0);

    var overlappingAreas = FaceOverlapFinder.CreateOverlapAreas(
      inputFaces,
      faceOverlaps.OverlappingPairs,
      faceCoincidenceTolerance,
      faceAreaTolerance);

    var resultCurveIds = new HashSet<Guid>(curveOverlaps.CoveredObjectIds);
    resultCurveIds.UnionWith(materializedSegments.OverlapObjectIds);
    if (!_overlapSegments)
      resultCurveIds.UnionWith(remainingPartialIds);
    var highlightedAreaCount = ApplyResults(
      doc,
      inputFaces,
      resultCurveIds,
      overlappingAreas);

    var findings = new List<string>();
    if (curveOverlaps.CoveredObjectIds.Count > 0)
      findings.Add($"selected {curveOverlaps.CoveredObjectIds.Count} covered curve(s)");
    if (curveOverlaps.PartiallyOverlappingObjectIds.Count > 0)
    {
      if (_overlapSegments && materializedSegments.OverlapObjectIds.Count > 0)
        findings.Add(
          $"selected {materializedSegments.OverlapObjectIds.Count} partial-overlap segment(s)");
      if (!_overlapSegments && remainingPartialIds.Count > 0)
        findings.Add(
          $"selected {remainingPartialIds.Count} partially overlapping curve(s)");
      else if (_overlapSegments && remainingPartialIds.Count > 0)
        findings.Add(
          $"skipped {remainingPartialIds.Count} partial curve(s) whose overlap could not be isolated");
    }
    if (faceOverlaps.OverlappingFaces.Count > 0)
      findings.Add(
        $"highlighted {highlightedAreaCount} overlapping face area(s)");

    var resultLabel = findings.Count > 0
      ? string.Join("; ", findings)
      : "no overlaps found";
    RhinoApp.WriteLine(
      $"vOverlaps: {resultLabel} " +
      $"({curveOverlaps.ItemCount} curve items, {curveOverlaps.PairChecks} curve pair checks; " +
      $"{faceOverlaps.FaceCount} faces, {faceOverlaps.PairChecks} face pair checks).");
    Log.Write(
      "vOverlaps",
      $"curves={curveOverlaps.ItemCount} curve_pairs={curveOverlaps.PairChecks} curve_hits={curveOverlaps.CoverHits} " +
      $"curve_partial_hits={curveOverlaps.PartialOverlapHits} " +
      $"overlap_segments={_overlapSegments} split_sources={materializedSegments.SplitSourceCount} " +
      $"faces={faceOverlaps.FaceCount} face_pairs={faceOverlaps.PairChecks} face_hits={faceOverlaps.OverlapHits} " +
      $"face_coincidence_tolerance={faceCoincidenceTolerance:G6} " +
      $"face_area_tolerance={faceAreaTolerance:G6} overlapping_areas={highlightedAreaCount}");

    return Result.Success;
  }

  private static GetObject CreateGeometryGetter()
  {
    var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.GeometryFilter = SupportedGeometry;
    getter.SubObjectSelect = true;
    getter.GroupSelect = false;
    getter.AcceptNothing(true);
    getter.AcceptNumber(true, true);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnablePreSelect(false, true);
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;
    return getter;
  }

  private static void SeedPreselection(
    RhinoDoc doc,
    ISet<Guid> curveIds,
    ISet<FaceOverlapFinder.FaceReference> faces)
  {
    var settings = VisibleObjectSettings();
    foreach (var obj in doc.Objects.GetObjectList(settings))
    {
      if (obj.Geometry is Curve && obj.IsSelected(checkSubObjects: false) != 0)
      {
        curveIds.Add(obj.Id);
        continue;
      }

      if (obj.Geometry is not Brep brep)
        continue;

      var selectedComponents = obj.GetSelectedSubObjects() ?? Array.Empty<ComponentIndex>();
      var addedSubobjects = false;
      foreach (var component in selectedComponents)
      {
        if (component.ComponentIndexType != ComponentIndexType.BrepFace ||
            component.Index < 0 || component.Index >= brep.Faces.Count)
          continue;
        faces.Add(new FaceOverlapFinder.FaceReference(obj.Id, component.Index));
        addedSubobjects = true;
      }

      if (!addedSubobjects && obj.IsSelected(checkSubObjects: false) != 0)
        AddAllFaces(obj.Id, brep, faces);
    }
  }

  private static void PickWorkingSet(
    RhinoDoc doc,
    bool add,
    ISet<Guid> curveIds,
    ISet<FaceOverlapFinder.FaceReference> faces)
  {
    using var getter = CreateGeometryGetter();
    getter.SetCommandPrompt(add
      ? "Add curves or faces to selection"
      : "Remove curves or faces from selection");
    var getResult = getter.GetMultiple(1, 0);
    if (getResult != GetResult.Object)
      return;

    if (add)
    {
      AddReferences(getter.Objects(), curveIds, faces);
      return;
    }

    RemoveReferences(doc, getter.Objects(), curveIds, faces);
  }

  private static void AddReferences(
    IEnumerable<ObjRef> references,
    ISet<Guid> curveIds,
    ISet<FaceOverlapFinder.FaceReference> faces)
  {
    foreach (var objRef in references)
    {
      var obj = objRef.Object();
      if (obj?.Geometry is Curve)
      {
        curveIds.Add(obj.Id);
        continue;
      }

      if (obj?.Geometry is not Brep brep)
        continue;

      var component = objRef.GeometryComponentIndex;
      if (component.ComponentIndexType == ComponentIndexType.BrepFace &&
          component.Index >= 0 && component.Index < brep.Faces.Count)
      {
        faces.Add(new FaceOverlapFinder.FaceReference(obj.Id, component.Index));
      }
      else
      {
        AddAllFaces(obj.Id, brep, faces);
      }
    }
  }

  private static void RemoveReferences(
    RhinoDoc doc,
    IEnumerable<ObjRef> references,
    ISet<Guid> curveIds,
    ISet<FaceOverlapFinder.FaceReference> faces)
  {
    foreach (var objRef in references)
    {
      var obj = objRef.Object();
      if (obj?.Geometry is Curve)
      {
        curveIds.Remove(obj.Id);
        obj.Select(false);
        continue;
      }

      if (obj?.Geometry is not Brep brep)
        continue;

      var component = objRef.GeometryComponentIndex;
      if (component.ComponentIndexType == ComponentIndexType.BrepFace &&
          component.Index >= 0 && component.Index < brep.Faces.Count)
      {
        faces.Remove(new FaceOverlapFinder.FaceReference(obj.Id, component.Index));
        obj.SelectSubObject(component, false, true, false);
        continue;
      }

      for (var faceIndex = 0; faceIndex < brep.Faces.Count; faceIndex++)
      {
        faces.Remove(new FaceOverlapFinder.FaceReference(obj.Id, faceIndex));
        obj.SelectSubObject(FaceComponent(faceIndex), false, true, false);
      }
    }
  }

  private static void AddAllFaces(
    Guid objectId,
    Brep brep,
    ISet<FaceOverlapFinder.FaceReference> faces)
  {
    for (var faceIndex = 0; faceIndex < brep.Faces.Count; faceIndex++)
      faces.Add(new FaceOverlapFinder.FaceReference(objectId, faceIndex));
  }

  private static void SyncWorkingSelection(
    RhinoDoc doc,
    IEnumerable<Guid> curveIds,
    IEnumerable<FaceOverlapFinder.FaceReference> faces)
  {
    foreach (var curveId in curveIds)
      doc.Objects.FindId(curveId)?.Select(true);

    foreach (var face in faces)
      doc.Objects.FindId(face.ObjectId)?.SelectSubObject(
        FaceComponent(face.FaceIndex),
        true,
        true,
        false);
    doc.Views.Redraw();
  }

  private static void ClearWorkingSelection(
    RhinoDoc doc,
    IEnumerable<Guid> curveIds,
    IEnumerable<FaceOverlapFinder.FaceReference> faces)
  {
    foreach (var curveId in curveIds)
      doc.Objects.FindId(curveId)?.Select(false);
    foreach (var face in faces)
      doc.Objects.FindId(face.ObjectId)?.SelectSubObject(
        FaceComponent(face.FaceIndex),
        false,
        true,
        false);
    doc.Views.Redraw();
  }

  private static void BuildInputs(
    RhinoDoc doc,
    IReadOnlyCollection<Guid> selectedCurveIds,
    IReadOnlyCollection<FaceOverlapFinder.FaceReference> selectedFaces,
    out List<RhinoObject> curves,
    out List<FaceOverlapFinder.FaceItem> faces)
  {
    curves = [];
    faces = [];

    if (selectedCurveIds.Count == 0 && selectedFaces.Count == 0)
    {
      foreach (var obj in doc.Objects.GetObjectList(VisibleObjectSettings()))
      {
        if (obj?.Geometry is Curve && obj.IsValid)
          curves.Add(obj);
        else if (obj?.Geometry is Brep brep && obj.IsValid)
          AddFaceInputs(obj.Id, brep, null, faces);
      }
      return;
    }

    foreach (var curveId in selectedCurveIds)
    {
      var obj = doc.Objects.FindId(curveId);
      if (obj?.Geometry is Curve && obj.IsValid)
        curves.Add(obj);
    }

    foreach (var group in selectedFaces.GroupBy(face => face.ObjectId))
    {
      var obj = doc.Objects.FindId(group.Key);
      if (obj?.Geometry is Brep brep && obj.IsValid)
        AddFaceInputs(obj.Id, brep, group.Select(face => face.FaceIndex), faces);
    }
  }

  private static ObjectEnumeratorSettings VisibleObjectSettings() => new()
  {
    IncludeLights = false,
    IncludeGrips = false,
    IncludePhantoms = false,
    NormalObjects = true,
    LockedObjects = false,
    HiddenObjects = false
  };

  private static ObjectEnumeratorSettings ConnectivityCurveSettings() => new()
  {
    IncludeLights = false,
    IncludeGrips = false,
    IncludePhantoms = false,
    NormalObjects = true,
    LockedObjects = true,
    HiddenObjects = false
  };

  private static void AddFaceInputs(
    Guid objectId,
    Brep brep,
    IEnumerable<int>? faceIndices,
    ICollection<FaceOverlapFinder.FaceItem> faces)
  {
    var indices = faceIndices ?? Enumerable.Range(0, brep.Faces.Count);
    foreach (var faceIndex in indices.Distinct())
    {
      if (faceIndex < 0 || faceIndex >= brep.Faces.Count)
        continue;
      faces.Add(new FaceOverlapFinder.FaceItem(
        new FaceOverlapFinder.FaceReference(objectId, faceIndex),
        brep.Faces[faceIndex]));
    }
  }

  private static int ApplyResults(
    RhinoDoc doc,
    IReadOnlyCollection<FaceOverlapFinder.FaceItem> inputFaces,
    IReadOnlyCollection<Guid> selectedCurveIds,
    IReadOnlyCollection<Brep> overlappingAreas)
  {
    doc.Objects.UnselectAll();
    foreach (var obj in inputFaces
               .Select(face => doc.Objects.FindId(face.Reference.ObjectId))
               .Where(obj => obj != null)
               .Distinct())
      obj!.UnhighlightAllSubObjects();

    foreach (var curveId in selectedCurveIds)
      doc.Objects.Select(curveId);

    if (overlappingAreas.Count > 0)
    {
      _activeAreaDocumentSerial = doc.RuntimeSerialNumber;
      _activeAreaHighlight = new OverlapAreaConduit(
        overlappingAreas,
        _activeAreaDocumentSerial)
      {
        Enabled = true
      };
      AttachAreaHighlightEvents();
    }

    doc.Views.Redraw();
    return overlappingAreas.Count;
  }

  private static List<Curve> FindOverlappingEdgeSegments(
    IReadOnlyCollection<FaceOverlapFinder.FaceItem> inputFaces,
    IReadOnlyCollection<FaceOverlapFinder.FacePair> overlappingPairs,
    double tolerance)
  {
    var facesByReference = inputFaces.ToDictionary(face => face.Reference);
    var segments = new List<Curve>();
    foreach (var pair in overlappingPairs)
    {
      if (!facesByReference.TryGetValue(pair.First, out var first) ||
          !facesByReference.TryGetValue(pair.Second, out var second))
        continue;

      foreach (var firstEdgeIndex in first.Face.AdjacentEdges())
      {
        var firstEdge = first.Face.Brep.Edges[firstEdgeIndex];
        using var firstCurve = firstEdge.DuplicateCurve();
        if (firstCurve == null || !firstCurve.IsValid)
          continue;

        var firstBounds = firstCurve.GetBoundingBox(accurate: true);
        foreach (var secondEdgeIndex in second.Face.AdjacentEdges())
        {
          var secondEdge = second.Face.Brep.Edges[secondEdgeIndex];
          using var secondCurve = secondEdge.DuplicateCurve();
          if (secondCurve == null || !secondCurve.IsValid)
            continue;

          if (!BoundingBoxesMeet(
                firstBounds,
                secondCurve.GetBoundingBox(accurate: true),
                tolerance))
            continue;

          if (firstCurve.IsLinear(tolerance) && secondCurve.IsLinear(tolerance))
          {
            if (TryCreateLinearOverlapSegment(
                  firstCurve,
                  secondCurve,
                  tolerance,
                  out var linearSegment,
                  out var linearDiagnostic))
            {
              segments.Add(linearSegment);
              Log.Write(
                "vOverlaps",
                $"linear edge overlap accepted faces={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
                $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} edges={firstEdgeIndex}/{secondEdgeIndex} " +
                linearDiagnostic);
            }
            else
            {
              Log.Write(
                "vOverlaps",
                $"linear edge overlap rejected faces={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
                $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} edges={firstEdgeIndex}/{secondEdgeIndex} " +
                linearDiagnostic);
            }
            continue;
          }

          var endpointSegments = CreateEndpointOverlapSegments(
            firstCurve,
            secondCurve,
            tolerance);
          if (endpointSegments.Count > 0)
          {
            segments.AddRange(endpointSegments);
            Log.Write(
              "vOverlaps",
              $"endpoint edge overlap accepted faces={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
              $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} edges={firstEdgeIndex}/{secondEdgeIndex} " +
              $"segments={endpointSegments.Count}");
            continue;
          }

          var intersections = Intersection.CurveCurve(
            firstCurve,
            secondCurve,
            tolerance,
            tolerance);
          if (intersections == null)
            continue;

          foreach (var intersection in intersections)
          {
            if (!intersection.IsOverlap)
              continue;

            if (TryCreateOverlapSegment(
                  firstCurve,
                  secondCurve,
                  intersection.PointA,
                  intersection.PointA2,
                  tolerance,
                  out var segment,
                  out var diagnostic))
            {
              segments.Add(segment);
              Log.Write(
                "vOverlaps",
                $"edge overlap accepted faces={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
                $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} edges={firstEdgeIndex}/{secondEdgeIndex} " +
                $"event_domain={intersection.OverlapA} {diagnostic}");
            }
            else
            {
              Log.Write(
                "vOverlaps",
                $"edge overlap rejected faces={pair.First.ObjectId}:{pair.First.FaceIndex}/" +
                $"{pair.Second.ObjectId}:{pair.Second.FaceIndex} edges={firstEdgeIndex}/{secondEdgeIndex} " +
                $"event_domain={intersection.OverlapA} {diagnostic}");
            }
          }
        }
      }
    }

    return segments;
  }

  private static bool TryCreateOverlapSegment(
    Curve firstEdge,
    Curve secondEdge,
    Point3d overlapStart,
    Point3d overlapEnd,
    double tolerance,
    out Curve segment,
    out string diagnostic)
  {
    segment = null!;
    diagnostic = string.Empty;
    if (!overlapStart.IsValid || !overlapEnd.IsValid ||
        !firstEdge.ClosestPoint(overlapStart, out var startParameter) ||
        !firstEdge.ClosestPoint(overlapEnd, out var endParameter))
    {
      diagnostic = "reason=invalid_endpoints";
      return false;
    }

    var interval = new Interval(
      Math.Min(startParameter, endParameter),
      Math.Max(startParameter, endParameter));
    if (!interval.IsValid || interval.Length <= RhinoMath.ZeroTolerance)
    {
      diagnostic = $"reason=empty_interval mapped_domain={interval}";
      return false;
    }

    var candidate = firstEdge.Trim(interval);
    if (candidate == null || !candidate.IsValid)
    {
      candidate?.Dispose();
      diagnostic = $"reason=trim_failed mapped_domain={interval}";
      return false;
    }

    var length = candidate.GetLength();
    var validationTolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);
    var firstDeviation = double.PositiveInfinity;
    var secondDeviation = double.PositiveInfinity;
    var minimumLength = MinimumOverlapLength(firstEdge, secondEdge, tolerance);
    var followsFirst = length > minimumLength &&
      CurveFollowsEdge(candidate, firstEdge, validationTolerance, out firstDeviation);
    var followsSecond = followsFirst &&
      CurveFollowsEdge(candidate, secondEdge, validationTolerance, out secondDeviation);
    if (!followsFirst || !followsSecond)
    {
      candidate.Dispose();
      diagnostic =
        $"reason=off_edge mapped_domain={interval} length={length:G6} " +
        $"minimum_length={minimumLength:G6} " +
        $"deviation={firstDeviation:G6}/{secondDeviation:G6}";
      return false;
    }

    segment = candidate;
    diagnostic =
      $"mapped_domain={interval} length={length:G6} " +
      $"deviation={firstDeviation:G6}/{secondDeviation:G6} " +
      $"from={PointText(candidate.PointAtStart)} to={PointText(candidate.PointAtEnd)}";
    return true;
  }

  private static List<Curve> CreateEndpointOverlapSegments(
    Curve firstEdge,
    Curve secondEdge,
    double tolerance)
  {
    var parameters = new List<double>
    {
      firstEdge.Domain.Min,
      firstEdge.Domain.Max
    };
    var validationTolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);
    foreach (var endpoint in new[]
             {
               secondEdge.PointAtStart,
               secondEdge.PointAtEnd
             })
    {
      if (!firstEdge.ClosestPoint(endpoint, out var parameter))
        continue;
      if (endpoint.DistanceTo(firstEdge.PointAt(parameter)) <= validationTolerance)
        parameters.Add(parameter);
    }

    parameters.Sort();
    var parameterTolerance = Math.Max(
      Math.Abs(firstEdge.Domain.Length) * 1e-12,
      RhinoMath.ZeroTolerance);
    var distinct = new List<double>();
    foreach (var parameter in parameters)
    {
      if (distinct.Count == 0 ||
          Math.Abs(parameter - distinct[^1]) > parameterTolerance)
        distinct.Add(parameter);
    }

    var minimumLength = MinimumOverlapLength(firstEdge, secondEdge, tolerance);
    var segments = new List<Curve>();
    for (var index = 0; index < distinct.Count - 1; index++)
    {
      var interval = new Interval(distinct[index], distinct[index + 1]);
      using var candidate = firstEdge.Trim(interval);
      if (candidate == null || !candidate.IsValid ||
          candidate.GetLength() <= minimumLength ||
          !CurveFollowsEdge(
            candidate,
            secondEdge,
            validationTolerance,
            out _))
        continue;

      var duplicate = candidate.DuplicateCurve();
      if (duplicate != null && duplicate.IsValid)
        segments.Add(duplicate);
      else
        duplicate?.Dispose();
    }

    return segments;
  }

  private static bool TryCreateLinearOverlapSegment(
    Curve firstEdge,
    Curve secondEdge,
    double tolerance,
    out Curve segment,
    out string diagnostic)
  {
    segment = null!;
    diagnostic = string.Empty;
    var firstLine = new Line(firstEdge.PointAtStart, firstEdge.PointAtEnd);
    var secondLine = new Line(secondEdge.PointAtStart, secondEdge.PointAtEnd);
    var firstLength = firstLine.Length;
    var secondLength = secondLine.Length;
    var minimumLength = MinimumOverlapLength(firstEdge, secondEdge, tolerance);
    if (firstLength <= minimumLength || secondLength <= minimumLength)
    {
      diagnostic =
        $"reason=edge_too_short first_length={firstLength:G6} " +
        $"second_length={secondLength:G6} minimum_length={minimumLength:G6}";
      return false;
    }

    var validationTolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);
    var maximumLineDeviation = Math.Max(
      Math.Max(
        firstLine.DistanceTo(secondLine.From, limitToFiniteSegment: false),
        firstLine.DistanceTo(secondLine.To, limitToFiniteSegment: false)),
      Math.Max(
        secondLine.DistanceTo(firstLine.From, limitToFiniteSegment: false),
        secondLine.DistanceTo(firstLine.To, limitToFiniteSegment: false)));
    if (maximumLineDeviation > validationTolerance)
    {
      diagnostic =
        $"reason=not_collinear deviation={maximumLineDeviation:G6} " +
        $"tolerance={validationTolerance:G6} " +
        $"first={PointText(firstLine.From)}->{PointText(firstLine.To)} " +
        $"second={PointText(secondLine.From)}->{PointText(secondLine.To)}";
      return false;
    }

    var firstDirection = firstLine.Direction;
    if (!firstDirection.Unitize())
    {
      diagnostic = "reason=invalid_direction";
      return false;
    }

    var secondStart = (secondLine.From - firstLine.From) * firstDirection;
    var secondEnd = (secondLine.To - firstLine.From) * firstDirection;
    var overlapStart = Math.Max(0.0, Math.Min(secondStart, secondEnd));
    var overlapEnd = Math.Min(firstLength, Math.Max(secondStart, secondEnd));
    var overlapLength = overlapEnd - overlapStart;
    if (overlapLength <= minimumLength)
    {
      diagnostic =
        $"reason=no_length_overlap length={overlapLength:G6} " +
        $"minimum_length={minimumLength:G6} " +
        $"first={PointText(firstLine.From)}->{PointText(firstLine.To)} " +
        $"second={PointText(secondLine.From)}->{PointText(secondLine.To)}";
      return false;
    }

    segment = new LineCurve(
      firstLine.From + firstDirection * overlapStart,
      firstLine.From + firstDirection * overlapEnd);
    diagnostic =
      $"length={overlapLength:G6} minimum_length={minimumLength:G6} " +
      $"deviation={maximumLineDeviation:G6} " +
      $"from={PointText(segment.PointAtStart)} to={PointText(segment.PointAtEnd)}";
    return true;
  }

  private static double MinimumOverlapLength(
    Curve firstEdge,
    Curve secondEdge,
    double tolerance) =>
    Math.Max(
      tolerance * MinimumOverlapToleranceFactor,
      Math.Min(firstEdge.GetLength(), secondEdge.GetLength()) *
      MinimumOverlapEdgeFraction);

  private static bool CurveFollowsEdge(
    Curve candidate,
    Curve edge,
    double tolerance,
    out double maximumDeviation)
  {
    maximumDeviation = 0.0;
    for (var sampleIndex = 0;
         sampleIndex <= OverlapValidationSamples;
         sampleIndex++)
    {
      var point = candidate.PointAtNormalizedLength(
        (double)sampleIndex / OverlapValidationSamples);
      if (!point.IsValid || !edge.ClosestPoint(point, out var edgeParameter))
      {
        maximumDeviation = double.PositiveInfinity;
        return false;
      }

      maximumDeviation = Math.Max(
        maximumDeviation,
        point.DistanceTo(edge.PointAt(edgeParameter)));
      if (maximumDeviation > tolerance)
        return false;
    }

    return true;
  }

  private static string PointText(Point3d point) =>
    $"({point.X:G6},{point.Y:G6},{point.Z:G6})";

  private static bool BoundingBoxesMeet(
    BoundingBox first,
    BoundingBox second,
    double tolerance) =>
    first.Min.X <= second.Max.X + tolerance &&
    first.Max.X + tolerance >= second.Min.X &&
    first.Min.Y <= second.Max.Y + tolerance &&
    first.Max.Y + tolerance >= second.Min.Y &&
    first.Min.Z <= second.Max.Z + tolerance &&
    first.Max.Z + tolerance >= second.Min.Z;

  private static void AttachAreaHighlightEvents()
  {
    RhinoApp.EscapeKeyPressed -= OnEscapeKeyPressed;
    RhinoApp.EscapeKeyPressed += OnEscapeKeyPressed;
    RhinoDoc.CloseDocument -= OnDocumentClosed;
    RhinoDoc.CloseDocument += OnDocumentClosed;
    _lastEscapeTick = 0;
  }

  private static void DetachAreaHighlightEvents()
  {
    RhinoApp.EscapeKeyPressed -= OnEscapeKeyPressed;
    RhinoDoc.CloseDocument -= OnDocumentClosed;
    _lastEscapeTick = 0;
  }

  private static void OnEscapeKeyPressed(object? sender, EventArgs e)
  {
    if (_activeAreaHighlight == null)
      return;

    var now = System.Environment.TickCount64;
    if (_lastEscapeTick != 0 &&
        now - _lastEscapeTick <= DoubleEscapeIntervalMilliseconds)
    {
      DisableAreaHighlight(
        RhinoDoc.FromRuntimeSerialNumber(_activeAreaDocumentSerial));
      return;
    }

    _lastEscapeTick = now;
  }

  private static void OnDocumentClosed(
    object? sender,
    DocumentEventArgs e)
  {
    if (e.DocumentSerialNumber == _activeAreaDocumentSerial)
      DisableAreaHighlight(null);
  }

  private static void DisableAreaHighlight(RhinoDoc? doc)
  {
    DetachAreaHighlightEvents();
    if (_activeAreaHighlight == null)
      return;

    _activeAreaHighlight.Enabled = false;
    _activeAreaHighlight.Dispose();
    _activeAreaHighlight = null;
    _activeAreaDocumentSerial = 0;
    doc?.Views.Redraw();
  }

  private sealed class OverlapAreaConduit(
    IReadOnlyCollection<Brep> areas,
    uint documentSerial) : DisplayConduit, IDisposable
  {
    private readonly DisplayMaterial _material = new(OverlapAreaColor)
    {
      Diffuse = Color.Black,
      BackDiffuse = Color.Black,
      Emission = OverlapAreaColor,
      BackEmission = OverlapAreaColor,
      Transparency = OverlapAreaTransparency,
      BackTransparency = OverlapAreaTransparency,
      IsTwoSided = true
    };
    private bool _disposed;

    public void Dispose()
    {
      if (_disposed)
        return;

      _disposed = true;
      foreach (var area in areas)
        area.Dispose();
      _material.Dispose();
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (e.RhinoDoc.RuntimeSerialNumber != documentSerial)
        return;

      e.Display.PushDepthTesting(false);
      e.Display.PushDepthWriting(false);
      try
      {
        foreach (var area in areas)
        {
          e.Display.DrawBrepShaded(area, _material);
          foreach (var edge in area.Edges)
          {
            if (edge.Valence == EdgeAdjacency.Naked)
              PreviewDisplay.DrawOverlapCurve(e.Display, edge);
          }
        }
      }
      finally
      {
        e.Display.PopDepthWriting();
        e.Display.PopDepthTesting();
      }
    }
  }

  private static ComponentIndex FaceComponent(int faceIndex) =>
    new(ComponentIndexType.BrepFace, faceIndex);

  private static string SelectionLabel(int curveCount, int faceCount) =>
    $"{curveCount} curve(s), {faceCount} face(s)";
}
