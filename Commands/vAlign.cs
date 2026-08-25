using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoPoint = Rhino.Geometry.Point;

namespace vTools.Commands;

/// <summary>
/// Aligns selected objects from one curve or edge direction to another in World XY.
/// </summary>
public sealed class vAlign : Command
{
  private const double DefaultDistance = 2.0; // Separation in model units; zero or greater, or None at the prompt.
  private const double KinkAngleRadians = Math.PI / 6.0; // Minimum selectable kink angle in radians; PI/6 equals 30 degrees.
  private const int TargetCurveSampleCount = 32; // Coarse hover samples per target curve; two or greater.
  private const int TargetCurveRefinementIterations = 8; // Local closest-point refinement passes; zero or greater.
  private const double MinimumPickRadiusPixels = 6.0; // Minimum target hover radius in display pixels; greater than zero.
  private const double CursorSideDeadZoneToleranceScale = 2.0; // World XY side dead zone as a multiple of document tolerance.
  private const double OrthoCueCurveLengthScale = 0.25; // Fraction of hovered target length used for the World Ortho direction cue.
  private const double OrthoCueToleranceScale = 20.0; // Minimum Ortho cue length as a multiple of document tolerance.
  private const double PreviewTransparency = 0.65; // Faded-object transparency from zero opaque through one invisible.
  private static readonly Color ReferenceColor = Color.Cyan; // Highlight color for the stationary reference segment.
  private static readonly Color TargetColor = Color.Orange; // Highlight color for the hovered rotating target segment.
  private static readonly Color CueColor = Color.LightGray; // Color of the point-to-point alignment cue.
  private static readonly Color FadedPreviewColor = Color.FromArgb(100, 100, 100); // Neutral preview material color.

  public override string EnglishName => "vAlign";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    double? distance = DefaultDistance;
    var result = SelectMovingObjects(doc, ref distance, out var movingIds);
    if (result != Result.Success)
      return result;

    if (!TryObjectsMassCenter(doc, movingIds, out var massCenter))
    {
      RhinoApp.WriteLine("vAlign: could not determine the selected objects' center of mass.");
      return Result.Failure;
    }
    Log.Write(
      "vAlign",
      $"start moving={movingIds.Count} distance={DistanceLabel(distance)} " +
      $"center=({massCenter.X:G17},{massCenter.Y:G17},{massCenter.Z:G17})");

    result = PickEdge(
      doc,
      "Select reference edge or curve segment, or press Enter for World Ortho",
      movingIds,
      requireMovingObject: false,
      allowNothing: true,
      ref distance,
      out var reference);
    if (result != Result.Success)
      return result;

    try
    {
      if (reference == null)
      {
        Log.Write("vAlign", "reference=None mode=WorldOrtho");
      }
      else
      {
        Log.Write(
          "vAlign",
          $"reference object={reference.ParentId} matchedEnd={reference.MatchedEnd} " +
          $"anchor=({reference.Anchor.X:G17},{reference.Anchor.Y:G17},{reference.Anchor.Z:G17}) " +
          $"direction=({reference.Direction.X:G17},{reference.Direction.Y:G17})");
      }
      RestoreMovingSelection(doc, movingIds);

      result = PickTargetEdge(
        doc,
        movingIds,
        massCenter,
        ref reference,
        ref distance,
        out var target,
        out var candidate);
      if (result != Result.Success || target == null || candidate == null)
        return result;

      using (target)
      {
        var outputIds = ApplyTransform(doc, movingIds, candidate.Transform, out var failed);
        doc.Objects.UnselectAll();
        foreach (var objectId in outputIds)
          doc.Objects.Select(objectId, true);
        doc.Views.Redraw();

        if (outputIds.Count == 0)
        {
          RhinoApp.WriteLine("vAlign: no objects were transformed.");
          return Result.Failure;
        }

        var action = reference == null
          ? $"rotated={outputIds.Count} mode=WorldOrtho"
          : distance.HasValue
            ? $"aligned={outputIds.Count} distance={distance.Value:G17}"
            : $"rotated={outputIds.Count} distance=None";
        Log.Write(
          "vAlign",
          $"{action} failed={failed} " +
          $"reference={(reference == null ? "WorldOrtho" : reference.ParentId)} " +
          $"target={target.ParentId} " +
          $"angle={RhinoMath.ToDegrees(candidate.Angle):G17}");

        if (failed > 0)
          RhinoApp.WriteLine(
            $"vAlign: transformed {outputIds.Count} object(s); {failed} operation(s) failed.");
        else if (reference == null)
          RhinoApp.WriteLine(
            $"vAlign: rotated {outputIds.Count} object(s) to World Ortho.");
        else if (distance.HasValue)
          RhinoApp.WriteLine(
            $"vAlign: aligned {outputIds.Count} object(s) at distance {distance.Value:G}.");
        else
          RhinoApp.WriteLine(
            $"vAlign: rotated {outputIds.Count} object(s) around their center of mass.");
        return Result.Success;
      }
    }
    finally
    {
      reference?.Dispose();
    }
  }

  private static Result SelectMovingObjects(
    RhinoDoc doc,
    ref double? distance,
    out List<Guid> objectIds)
  {
    objectIds = [];
    while (true)
    {
      using var getter = new GetObject();
      getter.SetCommandPrompt("Select objects to rotate");
      getter.GeometryFilter = ObjectType.AnyObject;
      getter.SetCustomGeometryFilter(
        (rhinoObject, geometry, _) =>
          geometry != null &&
          rhinoObject.ObjectType != ObjectType.Grip &&
          rhinoObject.ObjectType != ObjectType.Light);
      getter.GroupSelect = true;
      getter.SubObjectSelect = false;
      getter.EnablePreSelect(true, true);
      getter.AlreadySelectedObjectSelect = true;
      getter.EnableClearObjectsOnEntry(false);
      getter.EnableUnselectObjectsOnExit(false);
      getter.DeselectAllBeforePostSelect = false;
      ConfigureDirectDistanceInput(getter);
      var distanceOption = getter.AddOption("Distance", DistanceLabel(distance));

      var getResult = getter.GetMultiple(1, 0);
      if (HandleDirectDistance(getter, getResult, ref distance))
        continue;
      if (getResult == GetResult.Option && getter.Option()?.Index == distanceOption)
      {
        if (!PromptDistance(ref distance))
          return Result.Cancel;
        continue;
      }
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      if (getResult != GetResult.Object || getter.ObjectCount == 0)
        return Result.Cancel;

      var ids = new HashSet<Guid>();
      for (var index = 0; index < getter.ObjectCount; index++)
      {
        foreach (var objectId in ResolveObjectIds(doc, getter.Object(index)))
          ids.Add(objectId);
      }

      objectIds = ids
        .Where(id => doc.Objects.FindId(id)?.Geometry != null)
        .ToList();
      if (objectIds.Count > 0)
        return Result.Success;
    }
  }

  private static Result PickEdge(
    RhinoDoc doc,
    string prompt,
    IReadOnlyCollection<Guid> movingIds,
    bool requireMovingObject,
    bool allowNothing,
    ref double? distance,
    out EdgePick? edgePick)
  {
    edgePick = null;
    while (true)
    {
      using var getter = CreateEdgeGetter(prompt, allowNothing);
      ConfigureDirectDistanceInput(getter);
      var distanceOption = getter.AddOption("Distance", DistanceLabel(distance));
      var getResult = getter.Get();

      if (HandleDirectDistance(getter, getResult, ref distance))
        continue;
      if (getResult == GetResult.Option && getter.Option()?.Index == distanceOption)
      {
        if (!PromptDistance(ref distance))
          return Result.Cancel;
        continue;
      }
      if (allowNothing && getResult == GetResult.Nothing)
        return Result.Success;
      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();
      if (getResult != GetResult.Object || getter.ObjectCount == 0)
        return Result.Cancel;

      var objRef = getter.Object(0);
      if (!TryCreateEdgePick(doc, objRef, null, out var picked) || picked == null)
      {
        RhinoApp.WriteLine("vAlign: pick a curve or a surface/polysurface edge.");
        continue;
      }

      var belongsToMoving = Intersects(picked.ObjectIds, movingIds);
      if (belongsToMoving != requireMovingObject)
      {
        picked.Dispose();
        RhinoApp.WriteLine(requireMovingObject
          ? "vAlign: the target curve must belong to the selected objects."
          : "vAlign: pick a stationary reference outside the selected objects.");
        continue;
      }

      edgePick = picked;
      return Result.Success;
    }
  }

  private static GetObject CreateEdgeGetter(string prompt, bool allowNothing)
  {
    var getter = new GetObject();
    getter.SetCommandPrompt(prompt);
    getter.GeometryFilter = ObjectType.AnyObject;
    getter.GroupSelect = false;
    getter.SubObjectSelect = true;
    getter.EnablePreSelect(false, true);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;
    getter.AcceptNothing(allowNothing);
    return getter;
  }

  private static Result PickTargetEdge(
    RhinoDoc doc,
    IReadOnlyList<Guid> movingIds,
    Point3d massCenter,
    ref EdgePick? reference,
    ref double? distance,
    out EdgePick? target,
    out TransformCandidate? candidate)
  {
    target = null;
    candidate = null;
    var activeReference = reference;
    using var targetCache = new TargetEdgeCache(doc, movingIds);
    if (targetCache.Count == 0)
    {
      RhinoApp.WriteLine("vAlign: the selected objects do not contain a usable target curve or edge.");
      return Result.Failure;
    }

    Log.Write("vAlign", $"target cache curves={targetCache.Count}");
    using var preview = new AlignPreviewConduit(doc, movingIds, activeReference);
    CachedTargetCurve? referenceCache = null;
    if (activeReference != null &&
        doc.Objects.FindId(activeReference.ParentId) is { } referenceObject)
      referenceCache = new CachedTargetCurve(
        referenceObject,
        activeReference.Curve.DuplicateCurve());
    preview.Enabled = true;

    try
    {
      while (true)
      {
        using var getter = new GetPoint();
        getter.EnableTransparentCommands(true);
        getter.SetCommandPrompt(activeReference == null
          ? "Hover and click target edge; Shift or Ctrl selects horizontal; click stationary geometry to set reference"
          : "Hover and click target edge; Shift or Ctrl reverses side; click stationary geometry to replace reference");
        getter.AcceptNumber(true, false);
        getter.AcceptString(true);
        var distanceOption = activeReference == null
          ? -1
          : getter.AddOption("Distance", DistanceLabel(distance));
        EdgePick? hoveredTarget = null;
        EdgePick? hoveredReference = null;
        TransformCandidate? hoveredCandidate = null;
        string hoverStatus = "no target edge under cursor";
        bool loggedValidHover = false;
        var hoverDistance = distance;
        int cursorSide = 1;
        bool modifierDown = IsAlignModifierDown();
        bool orthoHorizontal = activeReference == null && modifierDown;
        RhinoViewport? lastViewport = null;
        System.Drawing.Point lastWindowPoint = default;
        bool hasWindowPoint = false;

        void RefreshHoveredCandidate(bool modifierPressed)
        {
          modifierDown = modifierPressed;
          orthoHorizontal = activeReference == null && modifierDown;
          hoveredCandidate = hoveredTarget == null || hoveredReference != null
            ? null
            : activeReference == null
              ? BuildOrthoTransformCandidate(
                massCenter,
                hoveredTarget,
                cursorSide,
                orthoHorizontal)
              : BuildTransformCandidate(
                doc,
                movingIds,
                massCenter,
                activeReference,
                hoveredTarget,
                hoverDistance,
                cursorSide,
                modifierDown);
          if (hoveredTarget != null)
          {
            hoverStatus = hoveredCandidate == null
              ? $"target={hoveredTarget.ParentId} has no valid World XY transform"
              : $"target={hoveredTarget.ParentId} candidate ready; " +
                $"cursorSide={CursorSideLabel(cursorSide)}" +
                (activeReference == null
                  ? $" ortho={(orthoHorizontal ? "horizontal" : "vertical")}"
                  : $" modifier={(modifierDown ? "reversed" : "normal")}");
          }
          preview.SetHover(hoveredReference ?? hoveredTarget, hoveredCandidate);
        }

        getter.MouseMove += (_, e) =>
        {
          lastViewport = e.Viewport;
          lastWindowPoint = e.WindowPoint;
          hasWindowPoint = true;

          EdgePick? nextTarget = null;
          bool hasTarget = targetCache.TryPick(
            e.Viewport,
            e.WindowPoint,
            out nextTarget,
            out var targetDistancePixels,
            out var pickDiagnostics);
          EdgePick? nextReference = null;
          double referenceDistancePixels = double.PositiveInfinity;
          if (referenceCache != null)
          {
            var referenceSample = referenceCache.BestScreenPick(
              e.Viewport,
              new Point2d(e.WindowPoint.X, e.WindowPoint.Y));
            referenceDistancePixels = Math.Sqrt(
              referenceSample.DistanceSquared ?? double.PositiveInfinity);
            if (referenceDistancePixels <= PickboxRadiusPixels())
              TryCreateEdgePick(
                doc,
                referenceCache.Parent,
                referenceCache.Curve,
                referenceSample.Point,
                out nextReference);
          }

          if (nextReference != null &&
              (!hasTarget || referenceDistancePixels <= targetDistancePixels))
          {
            nextTarget?.Dispose();
            nextTarget = null;
            hoverStatus =
              $"reference={nextReference.ParentId} endpoint ready; " +
              $"nearestPx={referenceDistancePixels:0.###}";
          }
          else if (hasTarget && nextTarget != null)
          {
            nextReference?.Dispose();
            nextReference = null;
            hoverStatus = $"target={nextTarget.ParentId}; {pickDiagnostics}";
          }
          else
          {
            nextReference?.Dispose();
            nextReference = null;
            hoverStatus = $"no target edge under cursor; {pickDiagnostics}";
          }

          var previousTarget = hoveredTarget;
          var previousReference = hoveredReference;
          hoveredTarget = nextTarget;
          hoveredReference = nextReference;
          if (hoveredTarget != null)
            cursorSide = CursorSideFromWorldXy(
              e.Point,
              hoveredTarget,
              cursorSide,
              doc.ModelAbsoluteTolerance);
          RefreshHoveredCandidate(IsAlignModifierDown());
          previousTarget?.Dispose();
          previousReference?.Dispose();
          if (!loggedValidHover && hoveredTarget != null && hoveredCandidate != null)
          {
            loggedValidHover = true;
            Log.Write(
              "vAlign",
              $"target hover object={hoveredTarget.ParentId} " +
              $"matchedEnd={hoveredTarget.MatchedEnd} " +
              $"angle={RhinoMath.ToDegrees(hoveredCandidate.Angle):G17} " +
              $"cursorSide={CursorSideLabel(cursorSide)}; {hoverStatus}");
          }
          doc.Views.Redraw();
        };

        EventHandler modifierPoll = (_, _) =>
        {
          bool nextModifierDown = IsAlignModifierDown();
          if (nextModifierDown == modifierDown)
            return;
          RefreshHoveredCandidate(nextModifierDown);
          Log.Write(
            "vAlign",
            activeReference == null
              ? $"ortho={(orthoHorizontal ? "horizontal" : "vertical")}"
              : $"offset side modifier={(modifierDown ? "reversed" : "normal")}");
          doc.Views.Redraw();
        };
        RhinoApp.Idle += modifierPoll;

        bool transferTarget = false;
        bool transferReference = false;
        try
        {
          var getResult = getter.Get();
          if (HandleDirectDistance(getter, getResult, ref distance))
            continue;
          if (distanceOption >= 0 &&
              getResult == GetResult.Option &&
              getter.Option()?.Index == distanceOption)
          {
            if (!PromptDistance(ref distance))
              return Result.Cancel;
            continue;
          }
          if (getter.CommandResult() != Result.Success)
            return getter.CommandResult();
          if (getResult != GetResult.Point)
            return Result.Cancel;

          bool clickModifierDown = IsAlignModifierDown();
          if (clickModifierDown != modifierDown)
            RefreshHoveredCandidate(clickModifierDown);

          EdgePick? clickedEdge = null;
          string clickDiagnostics = "screen position unavailable";
          if (hasWindowPoint && lastViewport != null)
          {
            TryPickEdgeAtScreenPoint(
              doc,
              lastViewport,
              lastWindowPoint,
              out clickedEdge,
              out clickDiagnostics);
          }

          if (clickedEdge != null && !Intersects(clickedEdge.ObjectIds, movingIds))
          {
            var previousReference = activeReference;
            activeReference = clickedEdge;
            reference = activeReference;
            referenceCache?.Dispose();
            referenceCache = doc.Objects.FindId(activeReference.ParentId) is { } replacementReferenceObject
              ? new CachedTargetCurve(
                replacementReferenceObject,
                activeReference.Curve.DuplicateCurve())
              : null;
            preview.SetReference(activeReference);
            previousReference?.Dispose();
            Log.Write(
              "vAlign",
              $"reference replaced object={activeReference.ParentId} " +
              $"matchedEnd={activeReference.MatchedEnd} " +
              $"anchor=({activeReference.Anchor.X:G17},{activeReference.Anchor.Y:G17},{activeReference.Anchor.Z:G17}); " +
              clickDiagnostics);
            continue;
          }

          if (clickedEdge != null)
          {
            if (hoveredTarget == null)
            {
              hoveredTarget = clickedEdge;
              clickedEdge = null;
              cursorSide = CursorSideFromWorldXy(
                getter.Point(),
                hoveredTarget,
                cursorSide,
                doc.ModelAbsoluteTolerance);
              RefreshHoveredCandidate(clickModifierDown);
            }
            clickedEdge?.Dispose();
          }

          if (hoveredReference != null)
          {
            var previousReference = activeReference;
            activeReference = hoveredReference;
            reference = activeReference;
            transferReference = true;
            preview.SetReference(activeReference);
            previousReference?.Dispose();
            Log.Write(
              "vAlign",
              $"reference re-anchored object={activeReference.ParentId} " +
              $"matchedEnd={activeReference.MatchedEnd} " +
              $"anchor=({activeReference.Anchor.X:G17},{activeReference.Anchor.Y:G17},{activeReference.Anchor.Z:G17})");
            continue;
          }

          if (hoveredTarget == null || hoveredCandidate == null)
          {
            Log.Write("vAlign", $"target click rejected: {hoverStatus}");
            RhinoApp.WriteLine("vAlign: hover directly over a usable target curve or edge.");
            continue;
          }

          target = hoveredTarget;
          candidate = hoveredCandidate;
          transferTarget = true;
          Log.Write(
            "vAlign",
            $"target click object={target.ParentId} matchedEnd={target.MatchedEnd} " +
            $"angle={RhinoMath.ToDegrees(candidate.Angle):G17} " +
            $"cursorSide={CursorSideLabel(cursorSide)}" +
            (activeReference == null
              ? $" ortho={(orthoHorizontal ? "horizontal" : "vertical")}"
              : $" modifier={(modifierDown ? "reversed" : "normal")}"));
          RestoreMovingSelection(doc, movingIds);
          return Result.Success;
        }
        finally
        {
          RhinoApp.Idle -= modifierPoll;
          preview.ClearHover();
          if (!transferTarget)
            hoveredTarget?.Dispose();
          if (!transferReference)
            hoveredReference?.Dispose();
          doc.Views.Redraw();
        }
      }
    }
    finally
    {
      referenceCache?.Dispose();
      preview.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static void ConfigureDirectDistanceInput(GetObject getter)
  {
    getter.AcceptNumber(true, false);
    getter.AcceptString(true);
  }

  private static bool TryPickEdgeAtScreenPoint(
    RhinoDoc doc,
    RhinoViewport viewport,
    System.Drawing.Point windowPoint,
    out EdgePick? edgePick,
    out string diagnostics)
  {
    edgePick = null;
    if (viewport.ParentView == null ||
        !viewport.GetFrustumLine(windowPoint.X, windowPoint.Y, out var pickLine))
    {
      diagnostics = "no viewport pick line";
      return false;
    }

    using var pickContext = new PickContext
    {
      View = viewport.ParentView,
      PickLine = pickLine,
      PickStyle = PickStyle.PointPick,
      PickMode = PickMode.Wireframe,
      PickGroupsEnabled = false,
      SubObjectSelectionEnabled = true
    };
    pickContext.SetPickTransform(viewport.GetPickTransform(windowPoint));
    pickContext.UpdateClippingPlanes();

    var picked = doc.Objects.PickObjects(pickContext);
    if (picked == null || picked.Length == 0)
    {
      diagnostics = "native pick returned no objects";
      return false;
    }

    foreach (var objRef in picked)
    {
      if (!TryCreateEdgePick(doc, objRef, pickContext, out var candidate) ||
          candidate == null)
        continue;

      edgePick = candidate;
      diagnostics =
        $"native pick count={picked.Length} object={candidate.ParentId} " +
        $"component={objRef.GeometryComponentIndex}";
      return true;
    }

    diagnostics = $"native pick count={picked.Length} contained no usable curve or edge";
    return false;
  }

  private static bool HandleDirectDistance(
    GetObject getter,
    GetResult result,
    ref double? distance)
  {
    if (result == GetResult.Number)
    {
      distance = Math.Max(0.0, getter.Number());
      return true;
    }

    if (result != GetResult.String)
      return false;

    if (!TryParseDistance(getter.StringResult(), out var parsed))
      RhinoApp.WriteLine("vAlign: enter a non-negative distance or None.");
    else
      distance = parsed;
    return true;
  }

  private static bool HandleDirectDistance(
    GetPoint getter,
    GetResult result,
    ref double? distance)
  {
    if (result == GetResult.Number)
    {
      distance = Math.Max(0.0, getter.Number());
      return true;
    }

    if (result != GetResult.String)
      return false;

    if (!TryParseDistance(getter.StringResult(), out var parsed))
      RhinoApp.WriteLine("vAlign: enter a non-negative distance or None.");
    else
      distance = parsed;
    return true;
  }

  private static bool PromptDistance(ref double? distance)
  {
    while (true)
    {
      using var getter = new GetString();
      getter.SetCommandPrompt("Distance (number or None)");
      getter.SetDefaultString(DistanceLabel(distance));
      getter.AcceptNothing(true);
      getter.AcceptNumber(true, false);
      var result = getter.Get();
      if (result == GetResult.Nothing)
        return true;
      if (result == GetResult.Number)
      {
        distance = Math.Max(0.0, getter.Number());
        return true;
      }
      if (result != GetResult.String || getter.CommandResult() != Result.Success)
        return false;
      if (TryParseDistance(getter.StringResult(), out var parsed))
      {
        distance = parsed;
        return true;
      }
      RhinoApp.WriteLine("vAlign: enter a non-negative distance or None.");
    }
  }

  private static bool TryParseDistance(string? text, out double? distance)
  {
    distance = null;
    if (string.IsNullOrWhiteSpace(text))
      return false;
    var value = text.Trim();
    if (value.Equals("None", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("N", StringComparison.OrdinalIgnoreCase))
      return true;
    if (!double.TryParse(
          value,
          System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.CurrentCulture,
          out var number) ||
        !double.IsFinite(number) ||
        number < 0.0)
      return false;
    distance = number;
    return true;
  }

  private static string DistanceLabel(double? distance) =>
    distance.HasValue ? distance.Value.ToString("G") : "None";

  private static bool TryCreateEdgePick(
    RhinoDoc doc,
    ObjRef objRef,
    PickContext? pickContext,
    out EdgePick? edgePick)
  {
    edgePick = null;
    Curve? rawCurve = null;
    try
    {
      var edge = objRef.Edge();
      rawCurve = edge?.DuplicateCurve();
      if (rawCurve == null)
        rawCurve = objRef.Curve()?.DuplicateCurve();
      if (rawCurve == null)
        return false;

      var pickPoint = Point3d.Unset;
      if (pickContext != null)
      {
        try
        {
          using var nurbs = rawCurve.ToNurbsCurve();
          if (nurbs != null &&
              pickContext.PickFrustumTest(nurbs, out var parameter, out _, out _))
            pickPoint = nurbs.PointAt(parameter);
        }
        catch
        {
        }
      }

      if (!pickPoint.IsValid)
        pickPoint = objRef.SelectionPoint();
      if (!pickPoint.IsValid)
        pickPoint = rawCurve.PointAt(rawCurve.Domain.Mid);

      var rhinoObject = objRef.Object();
      return rhinoObject != null &&
        TryCreateEdgePick(doc, rhinoObject, rawCurve, pickPoint, out edgePick);
    }
    catch
    {
      return false;
    }
    finally
    {
      rawCurve?.Dispose();
    }
  }

  private static bool TryCreateEdgePick(
    RhinoDoc doc,
    RhinoObject rhinoObject,
    Curve rawCurve,
    Point3d pickPoint,
    out EdgePick? edgePick)
  {
    edgePick = null;
    Curve? segment = null;
    try
    {
      segment = CurveSegmentAtPick(doc, rawCurve, pickPoint, out var segmentPoint);
      if (segment == null || !segmentPoint.IsValid)
        return false;
      if (!TryEdgeLineData(
            doc,
            segment,
            segmentPoint,
            out var anchor,
            out var direction,
            out var matchedEnd))
        return false;

      var objectIds = ResolveObjectIds(doc, rhinoObject);
      if (objectIds.Count == 0)
        return false;

      edgePick = new EdgePick(
        rhinoObject.Id,
        objectIds,
        segment,
        segmentPoint,
        anchor,
        direction,
        matchedEnd);
      segment = null;
      return true;
    }
    catch
    {
      return false;
    }
    finally
    {
      segment?.Dispose();
    }
  }

  private static Curve? CurveSegmentAtPick(
    RhinoDoc doc,
    Curve source,
    Point3d pickPoint,
    out Point3d closestPoint)
  {
    closestPoint = Point3d.Unset;
    var work = source.DuplicateCurve();
    if (work == null)
      return null;

    if (!work.ClosestPoint(pickPoint, out var closestParameter))
      closestParameter = work.Domain.Mid;
    closestPoint = work.PointAt(closestParameter);

    var kinks = LargeKinkParameters(work);
    if (kinks.Count == 0)
      return work;

    var parameters = new List<double> { work.Domain.T0 };
    parameters.AddRange(kinks);
    parameters.Add(work.Domain.T1);
    Curve? best = null;
    var bestPoint = Point3d.Unset;
    var bestDistance = double.PositiveInfinity;

    for (var index = 0; index + 1 < parameters.Count; index++)
    {
      Curve? segment = null;
      try
      {
        segment = work.Trim(parameters[index], parameters[index + 1]);
        if (segment == null || segment.GetLength() <= doc.ModelAbsoluteTolerance)
        {
          segment?.Dispose();
          continue;
        }
        if (!segment.ClosestPoint(pickPoint, out var parameter))
        {
          segment.Dispose();
          continue;
        }
        var point = segment.PointAt(parameter);
        var distance = point.DistanceToSquared(pickPoint);
        if (distance < bestDistance)
        {
          best?.Dispose();
          best = segment;
          bestPoint = point;
          bestDistance = distance;
          segment = null;
        }
      }
      catch
      {
      }
      finally
      {
        segment?.Dispose();
      }
    }

    work.Dispose();
    closestPoint = bestPoint;
    return best;
  }

  private static List<double> LargeKinkParameters(Curve curve)
  {
    var result = new List<double>();
    var domain = curve.Domain;
    var span = Math.Abs(domain.Length);
    if (span <= RhinoMath.ZeroTolerance)
      return result;

    var step = Math.Max(span * 1e-7, RhinoMath.ZeroTolerance * 10.0);
    var probe = domain.T0;
    for (var iteration = 0; iteration < 1000; iteration++)
    {
      if (!curve.GetNextDiscontinuity(
            Continuity.G1_continuous,
            probe,
            domain.T1,
            out var parameter))
        break;
      if (parameter > domain.T0 + step && parameter < domain.T1 - step &&
          TryTangentNear(curve, parameter, -1, out var before) &&
          TryTangentNear(curve, parameter, 1, out var after) &&
          Vector3d.VectorAngle(before, after) >= KinkAngleRadians &&
          (result.Count == 0 || Math.Abs(parameter - result[^1]) > step * 10.0))
        result.Add(parameter);

      probe = parameter + step;
      if (probe >= domain.T1)
        break;
    }
    return result;
  }

  private static bool TryTangentNear(
    Curve curve,
    double parameter,
    int direction,
    out Vector3d tangent)
  {
    var domain = curve.Domain;
    var step = Math.Max(Math.Abs(domain.Length) * 1e-6, RhinoMath.ZeroTolerance * 10.0);
    var evaluation = Math.Max(
      domain.T0,
      Math.Min(domain.T1, parameter + (direction < 0 ? -step : step)));
    tangent = curve.TangentAt(evaluation);
    return tangent.IsValid && !tangent.IsTiny() && tangent.Unitize();
  }

  private static bool TryEdgeLineData(
    RhinoDoc doc,
    Curve curve,
    Point3d pickPoint,
    out Point3d anchor,
    out Vector3d direction,
    out bool matchedEnd)
  {
    anchor = Point3d.Unset;
    direction = Vector3d.Unset;
    matchedEnd = false;

    var start = curve.PointAtStart;
    var end = curve.PointAtEnd;
    if (!curve.IsClosed && start.DistanceTo(end) > doc.ModelAbsoluteTolerance)
    {
      if (pickPoint.DistanceTo(start) <= pickPoint.DistanceTo(end))
      {
        anchor = start;
        direction = end - start;
      }
      else
      {
        anchor = end;
        direction = start - end;
      }

      if (TryProjectWorldXy(direction, out direction))
      {
        matchedEnd = true;
        return true;
      }
    }

    if (!curve.ClosestPoint(pickPoint, out var parameter))
      parameter = curve.Domain.Mid;
    anchor = curve.PointAt(parameter);
    return TryProjectWorldXy(curve.TangentAt(parameter), out direction);
  }

  private static bool TryProjectWorldXy(Vector3d source, out Vector3d projected)
  {
    projected = new Vector3d(source.X, source.Y, 0.0);
    return projected.IsValid && !projected.IsTiny() && projected.Unitize();
  }

  private static TransformCandidate? BuildTransformCandidate(
    RhinoDoc doc,
    IReadOnlyList<Guid> movingIds,
    Point3d massCenter,
    EdgePick reference,
    EdgePick target,
    double? distance,
    int cursorSide,
    bool reverseOffsetSide)
  {
    if (!TryObjectsBoundingBox(doc, movingIds, out var targetBounds))
      return null;
    var hasReferenceBounds =
      TryObjectsBoundingBox(doc, reference.ObjectIds, out var referenceBounds);

    var referenceDirections = new List<Vector3d> { reference.Direction };
    if (!target.MatchedEnd || !reference.MatchedEnd)
      referenceDirections.Add(-reference.Direction);

    var candidates = new List<TransformCandidate>();
    for (var directionIndex = 0; directionIndex < referenceDirections.Count; directionIndex++)
    {
      var referenceDirection = referenceDirections[directionIndex];
      var angle = SignedWorldXyAngle(target.Direction, referenceDirection);
      if (!angle.HasValue)
        continue;

      if (!distance.HasValue)
      {
        var rotation = Transform.Rotation(angle.Value, Vector3d.ZAxis, massCenter);
        candidates.Add(new TransformCandidate(
          rotation,
          0.0,
          0,
          Math.Abs(angle.Value),
          directionIndex,
          0,
          angle.Value));
        continue;
      }

      if (!hasReferenceBounds || !TryLeftNormal(referenceDirection, out var left))
        continue;

      var rotationAboutTarget = Transform.Rotation(
        angle.Value,
        Vector3d.ZAxis,
        target.Anchor);
      var rotatedAnchor = target.Anchor;
      rotatedAnchor.Transform(rotationAboutTarget);
      var referenceSide = SideValue(
        reference.Anchor,
        referenceDirection,
        referenceBounds.Center);

      for (var normalIndex = 0; normalIndex < 2; normalIndex++)
      {
        var normal = normalIndex == 0 ? left : -left;
        var destination = reference.Anchor + normal * distance.Value;
        destination.Z = reference.Anchor.Z;
        var translation = Transform.Translation(destination - rotatedAnchor);
        var combined = translation * rotationAboutTarget;
        var movedBounds = targetBounds;
        if (!movedBounds.Transform(combined) || !movedBounds.IsValid)
          continue;

        var movedCenter = targetBounds.Center;
        movedCenter.Transform(combined);
        var movedSide = SideValue(reference.Anchor, referenceDirection, movedCenter);
        var sameSidePenalty =
          Math.Abs(referenceSide) > doc.ModelAbsoluteTolerance &&
          referenceSide * movedSide >= 0.0
            ? 1
            : 0;
        candidates.Add(new TransformCandidate(
          combined,
          BoundingBoxOverlapAreaXy(referenceBounds, movedBounds),
          sameSidePenalty,
          Math.Abs(angle.Value),
          directionIndex,
          normalIndex,
          angle.Value));
      }
    }

    if (candidates.Count == 0)
      return null;

    int preferredDirectionIndex =
      referenceDirections.Count > 1 && cursorSide < 0 ? 1 : 0;
    if (!distance.HasValue)
      return candidates
        .OrderBy(item => item.DirectionIndex == preferredDirectionIndex ? 0 : 1)
        .ThenBy(item => item.AbsoluteAngle)
        .First();

    int preferredNormalIndex = referenceDirections.Count > 1
      ? 0
      : cursorSide < 0 ? 1 : 0;
    if (reverseOffsetSide)
      preferredNormalIndex = preferredNormalIndex == 0 ? 1 : 0;
    return candidates.FirstOrDefault(item =>
        item.DirectionIndex == preferredDirectionIndex &&
        item.NormalIndex == preferredNormalIndex)
      ?? candidates
        .OrderBy(item => item.DirectionIndex == preferredDirectionIndex ? 0 : 1)
        .ThenBy(item => item.NormalIndex == preferredNormalIndex ? 0 : 1)
        .ThenBy(item => item.Overlap)
        .First();
  }

  private static TransformCandidate? BuildOrthoTransformCandidate(
    Point3d massCenter,
    EdgePick target,
    int cursorSide,
    bool horizontal)
  {
    var desiredDirection = horizontal ? Vector3d.XAxis : Vector3d.YAxis;
    if (cursorSide < 0)
      desiredDirection.Reverse();
    var angle = SignedWorldXyAngle(target.Direction, desiredDirection);
    if (!angle.HasValue)
      return null;
    return new TransformCandidate(
      Transform.Rotation(angle.Value, Vector3d.ZAxis, massCenter),
      0.0,
      0,
      Math.Abs(angle.Value),
      cursorSide < 0 ? 1 : 0,
      0,
      angle.Value);
  }

  private static int CursorSideFromWorldXy(
    Point3d cursorPoint,
    EdgePick target,
    int fallback,
    double documentTolerance)
  {
    if (!TryProjectWorldXy(target.Direction, out var direction))
      return fallback;
    var cursorVector = cursorPoint - target.PickPoint;
    double cross =
      (direction.X * cursorVector.Y) -
      (direction.Y * cursorVector.X);
    double deadZone = Math.Max(
      RhinoMath.ZeroTolerance,
      documentTolerance * CursorSideDeadZoneToleranceScale);
    if (Math.Abs(cross) <= deadZone)
      return fallback;
    return cross < 0.0 ? 1 : -1;
  }

  private static string CursorSideLabel(int cursorSide) =>
    cursorSide < 0 ? "right" : "left";

  private static bool IsAlignModifierDown()
  {
    var modifiers = System.Windows.Forms.Control.ModifierKeys;
    return (modifiers & (System.Windows.Forms.Keys.Shift | System.Windows.Forms.Keys.Control)) !=
      System.Windows.Forms.Keys.None;
  }

  private static double? SignedWorldXyAngle(Vector3d from, Vector3d to)
  {
    if (!TryProjectWorldXy(from, out var fromDirection) ||
        !TryProjectWorldXy(to, out var toDirection))
      return null;
    var dot = Math.Max(-1.0, Math.Min(1.0, fromDirection * toDirection));
    var cross = Vector3d.CrossProduct(fromDirection, toDirection);
    return Math.Atan2(cross.Z, dot);
  }

  private static bool TryLeftNormal(Vector3d direction, out Vector3d normal)
  {
    normal = new Vector3d(-direction.Y, direction.X, 0.0);
    return normal.IsValid && !normal.IsTiny() && normal.Unitize();
  }

  private static double SideValue(Point3d anchor, Vector3d direction, Point3d point)
  {
    return TryLeftNormal(direction, out var normal)
      ? (point - anchor) * normal
      : 0.0;
  }

  private static double BoundingBoxOverlapAreaXy(BoundingBox first, BoundingBox second)
  {
    var width = Math.Min(first.Max.X, second.Max.X) - Math.Max(first.Min.X, second.Min.X);
    var height = Math.Min(first.Max.Y, second.Max.Y) - Math.Max(first.Min.Y, second.Min.Y);
    return width > 0.0 && height > 0.0 ? width * height : 0.0;
  }

  private static IReadOnlyList<Guid> ApplyTransform(
    RhinoDoc doc,
    IReadOnlyList<Guid> objectIds,
    Transform transform,
    out int failed)
  {
    failed = 0;
    var outputs = new List<Guid>();
    var undoRecord = doc.BeginUndoRecord("vAlign");
    try
    {
      foreach (var objectId in objectIds.Distinct())
      {
        var outputId = doc.Objects.Transform(objectId, transform, deleteOriginal: true);
        if (outputId == Guid.Empty)
          failed++;
        else if (!outputs.Contains(outputId))
          outputs.Add(outputId);
      }
    }
    finally
    {
      if (undoRecord != 0)
        doc.EndUndoRecord(undoRecord);
    }
    return outputs;
  }

  private static void RestoreMovingSelection(RhinoDoc doc, IEnumerable<Guid> movingIds)
  {
    foreach (var objectId in movingIds)
    {
      var rhinoObject = doc.Objects.FindId(objectId);
      rhinoObject?.UnselectAllSubObjects();
      rhinoObject?.Select(true);
    }
    doc.Views.Redraw();
  }

  private static List<Guid> ResolveObjectIds(RhinoDoc doc, ObjRef objRef)
  {
    var rhinoObject = objRef.Object();
    return rhinoObject == null ? [] : ResolveObjectIds(doc, rhinoObject);
  }

  private static List<Guid> ResolveObjectIds(RhinoDoc doc, RhinoObject rhinoObject)
  {
    var groupIndices = rhinoObject.Attributes.GetGroupList() ?? [];
    if (groupIndices.Length == 0)
      return [rhinoObject.Id];

    var groups = new List<List<Guid>>();
    foreach (var groupIndex in groupIndices.Distinct())
    {
      var members = (doc.Groups.GroupMembers(groupIndex) ?? [])
        .Where(member => member?.Geometry != null && doc.Objects.FindId(member.Id) != null)
        .Select(member => member.Id)
        .Distinct()
        .ToList();
      if (members.Count > 0)
        groups.Add(members);
    }

    return groups
      .OrderBy(members => members.Count)
      .ThenBy(members =>
        TryObjectsBoundingBox(doc, members, out var bounds)
          ? bounds.Diagonal.Length
          : 0.0)
      .FirstOrDefault() ?? [rhinoObject.Id];
  }

  private static bool TryObjectsBoundingBox(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds,
    out BoundingBox bounds)
  {
    bounds = BoundingBox.Unset;
    foreach (var objectId in objectIds)
    {
      var geometry = doc.Objects.FindId(objectId)?.Geometry;
      if (geometry == null)
        continue;
      var objectBounds = geometry.GetBoundingBox(true);
      if (objectBounds.IsValid)
        bounds.Union(objectBounds);
    }
    return bounds.IsValid;
  }

  private static bool TryObjectsMassCenter(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds,
    out Point3d center)
  {
    var samples = new List<CenterSample>();
    foreach (var objectId in objectIds)
    {
      var geometry = doc.Objects.FindId(objectId)?.Geometry;
      if (geometry != null && TryMassSample(geometry, out var sample))
        samples.Add(sample);
    }

    var totalWeight = samples.Sum(sample => sample.Weight);
    if (double.IsFinite(totalWeight) && totalWeight > RhinoMath.ZeroTolerance)
    {
      center = new Point3d(
        samples.Sum(sample => sample.Center.X * sample.Weight) / totalWeight,
        samples.Sum(sample => sample.Center.Y * sample.Weight) / totalWeight,
        samples.Sum(sample => sample.Center.Z * sample.Weight) / totalWeight);
      return center.IsValid;
    }

    if (TryObjectsBoundingBox(doc, objectIds, out var bounds))
    {
      center = bounds.Center;
      return true;
    }

    center = Point3d.Unset;
    return false;
  }

  private static bool TryMassSample(GeometryBase geometry, out CenterSample sample)
  {
    if (TryVolumeSample(geometry, out sample) ||
        TryAreaSample(geometry, out sample) ||
        TryLengthSample(geometry, out sample))
      return true;
    if (geometry is RhinoPoint point && point.Location.IsValid)
    {
      sample = new CenterSample(point.Location, 1.0);
      return true;
    }
    sample = default;
    return false;
  }

  private static bool TryVolumeSample(GeometryBase geometry, out CenterSample sample)
  {
    VolumeMassProperties? properties = null;
    Brep? converted = null;
    try
    {
      switch (geometry)
      {
        case Brep brep when brep.IsSolid:
          properties = VolumeMassProperties.Compute(brep);
          break;
        case Mesh mesh when mesh.IsClosed:
          properties = VolumeMassProperties.Compute(mesh);
          break;
        case Extrusion extrusion when extrusion.IsSolid:
          converted = extrusion.ToBrep();
          properties = converted == null ? null : VolumeMassProperties.Compute(converted);
          break;
        case SubD subd:
          converted = subd.ToBrep();
          if (converted?.IsSolid == true)
            properties = VolumeMassProperties.Compute(converted);
          break;
      }
      return TryCenterSample(properties?.Centroid, properties?.Volume, out sample);
    }
    catch
    {
      sample = default;
      return false;
    }
    finally
    {
      properties?.Dispose();
      converted?.Dispose();
    }
  }

  private static bool TryAreaSample(GeometryBase geometry, out CenterSample sample)
  {
    AreaMassProperties? properties = null;
    Brep? converted = null;
    try
    {
      switch (geometry)
      {
        case Brep brep:
          properties = AreaMassProperties.Compute(brep);
          break;
        case Mesh mesh:
          properties = AreaMassProperties.Compute(mesh);
          break;
        case Extrusion extrusion:
          converted = extrusion.ToBrep();
          properties = converted == null ? null : AreaMassProperties.Compute(converted);
          break;
        case SubD subd:
          converted = subd.ToBrep();
          properties = converted == null ? null : AreaMassProperties.Compute(converted);
          break;
        case Surface surface:
          properties = AreaMassProperties.Compute(surface);
          break;
        case Hatch hatch:
          properties = AreaMassProperties.Compute(hatch);
          break;
        case Curve curve when curve.IsClosed:
          properties = AreaMassProperties.Compute(curve);
          break;
      }
      return TryCenterSample(properties?.Centroid, properties?.Area, out sample);
    }
    catch
    {
      sample = default;
      return false;
    }
    finally
    {
      properties?.Dispose();
      converted?.Dispose();
    }
  }

  private static bool TryLengthSample(GeometryBase geometry, out CenterSample sample)
  {
    if (geometry is not Curve curve)
    {
      sample = default;
      return false;
    }
    try
    {
      using var properties = LengthMassProperties.Compute(curve);
      return TryCenterSample(properties?.Centroid, properties?.Length, out sample);
    }
    catch
    {
      sample = default;
      return false;
    }
  }

  private static bool TryCenterSample(
    Point3d? center,
    double? weight,
    out CenterSample sample)
  {
    var absoluteWeight = Math.Abs(weight ?? 0.0);
    if (!center.HasValue || !center.Value.IsValid ||
        !double.IsFinite(absoluteWeight) || absoluteWeight <= RhinoMath.ZeroTolerance)
    {
      sample = default;
      return false;
    }
    sample = new CenterSample(center.Value, absoluteWeight);
    return true;
  }

  private static bool Intersects(
    IEnumerable<Guid> first,
    IEnumerable<Guid> second)
  {
    var firstSet = first.ToHashSet();
    return second.Any(firstSet.Contains);
  }

  private readonly record struct CenterSample(Point3d Center, double Weight);

  private sealed class EdgePick : IDisposable
  {
    internal EdgePick(
      Guid parentId,
      IReadOnlyList<Guid> objectIds,
      Curve curve,
      Point3d pickPoint,
      Point3d anchor,
      Vector3d direction,
      bool matchedEnd)
    {
      ParentId = parentId;
      ObjectIds = objectIds;
      Curve = curve;
      PickPoint = pickPoint;
      Anchor = anchor;
      Direction = direction;
      MatchedEnd = matchedEnd;
    }

    internal Guid ParentId { get; }
    internal IReadOnlyList<Guid> ObjectIds { get; }
    internal Curve Curve { get; }
    internal Point3d PickPoint { get; }
    internal Point3d Anchor { get; }
    internal Vector3d Direction { get; }
    internal bool MatchedEnd { get; }

    public void Dispose() => Curve.Dispose();
  }

  private sealed record TransformCandidate(
    Transform Transform,
    double Overlap,
    int SameSidePenalty,
    double AbsoluteAngle,
    int DirectionIndex,
    int NormalIndex,
    double Angle);

  private sealed class TargetEdgeCache : IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly List<CachedTargetCurve> _curves = [];

    internal TargetEdgeCache(RhinoDoc doc, IEnumerable<Guid> targetObjectIds)
    {
      _doc = doc;
      foreach (var objectId in targetObjectIds.Distinct())
      {
        var rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject == null || !rhinoObject.Visible)
          continue;
        foreach (var curve in DuplicatePickCurves(rhinoObject))
          _curves.Add(new CachedTargetCurve(rhinoObject, curve));
      }
    }

    internal int Count => _curves.Count;

    internal bool TryPick(
      RhinoViewport viewport,
      System.Drawing.Point windowPoint,
      out EdgePick? edgePick,
      out double nearestDistancePixels,
      out string diagnostics)
    {
      edgePick = null;
      var screenPoint = new Point2d(windowPoint.X, windowPoint.Y);
      double radius = PickboxRadiusPixels();
      double bestDistanceSquared = radius * radius;
      Point3d bestPoint = Point3d.Unset;
      CachedTargetCurve? best = null;

      foreach (var candidate in _curves)
      {
        var sample = candidate.BestScreenPick(viewport, screenPoint);
        if (!sample.DistanceSquared.HasValue ||
            sample.DistanceSquared.Value > bestDistanceSquared)
          continue;
        best = candidate;
        bestPoint = sample.Point;
        bestDistanceSquared = sample.DistanceSquared.Value;
      }

      nearestDistancePixels = Math.Sqrt(bestDistanceSquared);
      diagnostics =
        $"cache curves={_curves.Count} nearestPx={nearestDistancePixels:0.###}";
      return best != null && bestPoint.IsValid &&
        TryCreateEdgePick(_doc, best.Parent, best.Curve, bestPoint, out edgePick);
    }

    public void Dispose()
    {
      foreach (var curve in _curves)
        curve.Dispose();
      _curves.Clear();
    }
  }

  private static IEnumerable<Curve> DuplicatePickCurves(RhinoObject rhinoObject)
  {
    if (rhinoObject.Geometry is Curve curve)
    {
      yield return curve.DuplicateCurve();
      yield break;
    }

    Brep? brep = null;
    bool disposeBrep = false;
    try
    {
      switch (rhinoObject.Geometry)
      {
        case Brep objectBrep:
          brep = objectBrep;
          break;
        case Extrusion extrusion:
          brep = extrusion.ToBrep();
          disposeBrep = true;
          break;
        case Surface surface:
          brep = surface.ToBrep();
          disposeBrep = true;
          break;
      }

      if (brep == null)
        yield break;
      foreach (var edge in brep.Edges)
        yield return edge.DuplicateCurve();
    }
    finally
    {
      if (disposeBrep)
        brep?.Dispose();
    }
  }

  private readonly struct ScreenPickSample
  {
    internal Point3d Point { get; init; }
    internal double? DistanceSquared { get; init; }
  }

  private sealed class CachedTargetCurve : IDisposable
  {
    private readonly double[] _parameters;
    private readonly Point3d[] _points;

    internal CachedTargetCurve(RhinoObject parent, Curve curve)
    {
      Parent = parent;
      Curve = curve;
      _parameters = curve.DivideByCount(TargetCurveSampleCount, true) ??
        Enumerable.Range(0, TargetCurveSampleCount + 1)
          .Select(index =>
            curve.Domain.ParameterAt(index / (double)TargetCurveSampleCount))
          .ToArray();
      if (_parameters.Length < 2)
        _parameters = [curve.Domain.T0, curve.Domain.T1];
      _points = _parameters.Select(curve.PointAt).ToArray();
    }

    internal RhinoObject Parent { get; }
    internal Curve Curve { get; }

    internal ScreenPickSample BestScreenPick(
      RhinoViewport viewport,
      Point2d screenPoint)
    {
      if (Curve.IsLinear(RhinoMath.ZeroTolerance))
      {
        var lineStart = Curve.PointAtStart;
        var lineEnd = Curve.PointAtEnd;
        var start = viewport.WorldToClient(lineStart);
        var end = viewport.WorldToClient(lineEnd);
        double vx = end.X - start.X;
        double vy = end.Y - start.Y;
        double denominator = (vx * vx) + (vy * vy);
        double factor = denominator <= 1.0e-12
          ? 0.0
          : Math.Clamp(
            (((screenPoint.X - start.X) * vx) + ((screenPoint.Y - start.Y) * vy)) /
              denominator,
            0.0,
            1.0);
        double x = start.X + (vx * factor);
        double y = start.Y + (vy * factor);
        double dx = x - screenPoint.X;
        double dy = y - screenPoint.Y;
        return new ScreenPickSample
        {
          Point = lineStart + ((lineEnd - lineStart) * factor),
          DistanceSquared = (dx * dx) + (dy * dy)
        };
      }

      int bestIndex = -1;
      double bestDistanceSquared = double.MaxValue;
      for (int index = 0; index < _points.Length; index++)
      {
        double distanceSquared =
          PixelDistanceSquared(viewport, screenPoint, _points[index]);
        if (distanceSquared >= bestDistanceSquared)
          continue;
        bestDistanceSquared = distanceSquared;
        bestIndex = index;
      }
      if (bestIndex < 0)
        return default;

      double left = _parameters[Math.Max(0, bestIndex - 1)];
      double right = _parameters[Math.Min(_parameters.Length - 1, bestIndex + 1)];
      double bestParameter = _parameters[bestIndex];
      for (int iteration = 0;
           iteration < TargetCurveRefinementIterations && right > left;
           iteration++)
      {
        double t1 = left + ((right - left) / 3.0);
        double t2 = right - ((right - left) / 3.0);
        double d1 = PixelDistanceSquared(
          viewport,
          screenPoint,
          Curve.PointAt(t1));
        double d2 = PixelDistanceSquared(
          viewport,
          screenPoint,
          Curve.PointAt(t2));
        if (d1 <= d2)
        {
          right = t2;
          if (d1 < bestDistanceSquared)
          {
            bestDistanceSquared = d1;
            bestParameter = t1;
          }
        }
        else
        {
          left = t1;
          if (d2 < bestDistanceSquared)
          {
            bestDistanceSquared = d2;
            bestParameter = t2;
          }
        }
      }

      return new ScreenPickSample
      {
        Point = Curve.PointAt(bestParameter),
        DistanceSquared = bestDistanceSquared
      };
    }

    public void Dispose() => Curve.Dispose();
  }

  private static double PixelDistanceSquared(
    RhinoViewport viewport,
    Point2d screenPoint,
    Point3d worldPoint)
  {
    var projected = viewport.WorldToClient(worldPoint);
    double dx = projected.X - screenPoint.X;
    double dy = projected.Y - screenPoint.Y;
    return (dx * dx) + (dy * dy);
  }

  private static double PickboxRadiusPixels()
  {
    try
    {
      return Math.Max(
        MinimumPickRadiusPixels,
        Rhino.ApplicationSettings.ModelAidSettings.MousePickboxRadius + 2.0);
    }
    catch
    {
      return MinimumPickRadiusPixels;
    }
  }

  private sealed class AlignPreviewConduit : DisplayConduit, IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly IReadOnlyList<Guid> _movingIds;
    private readonly HashSet<Guid> _movingSet;
    private EdgePick? _reference;
    private readonly DisplayMaterial _previewMaterial = new(FadedPreviewColor)
    {
      Transparency = PreviewTransparency,
      BackTransparency = PreviewTransparency
    };
    private readonly List<GeometryBase> _previewGeometry = [];
    private readonly List<PreviewInstance> _previewInstances = [];
    private readonly HashSet<Guid> _previewedObjectIds = [];
    private EdgePick? _target;
    private TransformCandidate? _candidate;

    internal AlignPreviewConduit(
      RhinoDoc doc,
      IReadOnlyList<Guid> movingIds,
      EdgePick? reference)
    {
      _doc = doc;
      _movingIds = movingIds;
      _movingSet = movingIds.ToHashSet();
      _reference = reference;
    }

    internal void SetReference(EdgePick? reference)
    {
      _reference = reference;
      ClearHover();
    }

    internal void SetHover(EdgePick? target, TransformCandidate? candidate)
    {
      DisposePreviewGeometry();
      _target = target;
      _candidate = candidate;
      if (candidate == null)
        return;

      foreach (var objectId in _movingIds)
      {
        var rhinoObject = _doc.Objects.FindId(objectId);
        if (rhinoObject == null || !rhinoObject.Visible)
          continue;
        if (rhinoObject is InstanceObject instance)
        {
          var bounds = instance.Geometry.GetBoundingBox(true);
          if (bounds.IsValid)
            bounds.Transform(candidate.Transform);
          _previewInstances.Add(new PreviewInstance(
            instance.InstanceDefinition,
            candidate.Transform * instance.InstanceXform,
            bounds));
          _previewedObjectIds.Add(objectId);
          continue;
        }

        var geometry = rhinoObject.Geometry?.Duplicate();
        if (geometry == null)
          continue;
        if (!CanDrawPreviewGeometry(geometry) ||
            !geometry.Transform(candidate.Transform))
        {
          geometry.Dispose();
          continue;
        }
        _previewGeometry.Add(geometry);
        _previewedObjectIds.Add(objectId);
      }
    }

    internal void ClearHover() => SetHover(null, null);

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
      if (_candidate != null && e.RhinoObject != null &&
          _movingSet.Contains(e.RhinoObject.Id) &&
          _previewedObjectIds.Contains(e.RhinoObject.Id))
        e.CullObject = true;
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
      if (_candidate == null)
        return;
      foreach (var geometry in _previewGeometry)
      {
        var bounds = geometry.GetBoundingBox(true);
        if (bounds.IsValid)
          e.IncludeBoundingBox(bounds);
      }
      foreach (var instance in _previewInstances)
      {
        if (instance.Bounds.IsValid)
          e.IncludeBoundingBox(instance.Bounds);
      }
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (_candidate == null)
        return;
      foreach (var geometry in _previewGeometry)
        DrawFadedGeometry(e.Display, geometry);
      foreach (var instance in _previewInstances)
        e.Display.DrawInstanceDefinitionShaded(
          instance.Definition,
          _previewMaterial,
          instance.Transform);
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      if (_reference != null)
        PreviewDisplay.DrawCurve(e.Display, _reference.Curve, ReferenceColor, 1);
      if (_target != null)
      {
        PreviewDisplay.DrawCurve(e.Display, _target.Curve, TargetColor, 2);
        if (_reference != null)
        {
          PreviewDisplay.DrawLine(
            e.Display,
            _reference.PickPoint,
            _target.PickPoint,
            CueColor);
          e.Display.DrawDottedLine(
            _reference.PickPoint,
            _target.PickPoint,
            CueColor);
        }
        else if (_candidate != null)
        {
          var direction = _target.Direction;
          direction.Transform(_candidate.Transform);
          if (direction.Unitize())
          {
            double halfLength = Math.Max(
              _target.Curve.GetLength() * OrthoCueCurveLengthScale * 0.5,
              _doc.ModelAbsoluteTolerance * OrthoCueToleranceScale * 0.5);
            PreviewDisplay.DrawLine(
              e.Display,
              _target.PickPoint - (direction * halfLength),
              _target.PickPoint + (direction * halfLength),
              CueColor);
          }
        }
      }
    }

    private void DrawFadedGeometry(DisplayPipeline display, GeometryBase geometry)
    {
      switch (geometry)
      {
        case Curve curve:
          PreviewDisplay.DrawCurve(display, curve, FadedPreviewColor);
          break;
        case Brep brep:
          display.DrawBrepShaded(brep, _previewMaterial);
          PreviewDisplay.DrawBrepWires(display, brep, FadedPreviewColor);
          break;
        case Extrusion extrusion:
          using (var brep = extrusion.ToBrep())
          {
            if (brep == null)
              break;
            display.DrawBrepShaded(brep, _previewMaterial);
            PreviewDisplay.DrawBrepWires(display, brep, FadedPreviewColor);
          }
          break;
        case Surface surface:
          using (var brep = surface.ToBrep())
          {
            if (brep == null)
              break;
            display.DrawBrepShaded(brep, _previewMaterial);
            PreviewDisplay.DrawBrepWires(display, brep, FadedPreviewColor);
          }
          break;
        case Mesh mesh:
          display.DrawMeshShaded(mesh, _previewMaterial);
          PreviewDisplay.DrawMeshWires(display, mesh, FadedPreviewColor);
          break;
        case SubD subd:
          display.DrawSubDShaded(subd, _previewMaterial);
          display.DrawSubDWires(
            subd,
            FadedPreviewColor,
            PreviewDisplay.Thickness(display));
          break;
        case TextEntity text:
          display.DrawAnnotation(text, FadedPreviewColor);
          break;
        case TextDot dot:
          display.DrawDot(dot, FadedPreviewColor, Color.Black, FadedPreviewColor);
          break;
        case RhinoPoint point:
          display.DrawPoint(
            point.Location,
            PointStyle.ActivePoint,
            3,
            FadedPreviewColor);
          break;
        case Hatch hatch:
          display.DrawHatch(hatch, FadedPreviewColor, FadedPreviewColor);
          break;
      }
    }

    private static bool CanDrawPreviewGeometry(GeometryBase geometry) =>
      geometry is Curve or Brep or Extrusion or Surface or Mesh or SubD or
      TextEntity or TextDot or RhinoPoint or Hatch;

    private void DisposePreviewGeometry()
    {
      foreach (var geometry in _previewGeometry)
        geometry.Dispose();
      _previewGeometry.Clear();
      _previewInstances.Clear();
      _previewedObjectIds.Clear();
    }

    public void Dispose()
    {
      Enabled = false;
      ClearHover();
      _previewMaterial.Dispose();
    }

    private sealed record PreviewInstance(
      InstanceDefinition Definition,
      Transform Transform,
      BoundingBox Bounds);
  }
}
