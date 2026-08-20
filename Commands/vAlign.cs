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
      "Select reference edge or curve segment",
      movingIds,
      requireMovingObject: false,
      ref distance,
      out var reference);
    if (result != Result.Success || reference == null)
      return result;

    using (reference)
    {
      Log.Write(
        "vAlign",
        $"reference object={reference.ParentId} matchedEnd={reference.MatchedEnd} " +
        $"anchor=({reference.Anchor.X:G17},{reference.Anchor.Y:G17},{reference.Anchor.Z:G17}) " +
        $"direction=({reference.Direction.X:G17},{reference.Direction.Y:G17})");
      RestoreMovingSelection(doc, movingIds);

      result = PickTargetEdge(
        doc,
        movingIds,
        massCenter,
        reference,
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

        var action = distance.HasValue
          ? $"aligned={outputIds.Count} distance={distance.Value:G17}"
          : $"rotated={outputIds.Count} distance=None";
        Log.Write(
          "vAlign",
          $"{action} failed={failed} reference={reference.ParentId} target={target.ParentId} " +
          $"angle={RhinoMath.ToDegrees(candidate.Angle):G17}");

        if (failed > 0)
          RhinoApp.WriteLine(
            $"vAlign: transformed {outputIds.Count} object(s); {failed} operation(s) failed.");
        else if (distance.HasValue)
          RhinoApp.WriteLine(
            $"vAlign: aligned {outputIds.Count} object(s) at distance {distance.Value:G}.");
        else
          RhinoApp.WriteLine(
            $"vAlign: rotated {outputIds.Count} object(s) around their center of mass.");
        return Result.Success;
      }
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
    ref double? distance,
    out EdgePick? edgePick)
  {
    edgePick = null;
    while (true)
    {
      using var getter = CreateEdgeGetter(prompt);
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

  private static GetObject CreateEdgeGetter(string prompt)
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
    return getter;
  }

  private static Result PickTargetEdge(
    RhinoDoc doc,
    IReadOnlyList<Guid> movingIds,
    Point3d massCenter,
    EdgePick reference,
    ref double? distance,
    out EdgePick? target,
    out TransformCandidate? candidate)
  {
    target = null;
    candidate = null;
    using var targetCache = new TargetEdgeCache(doc, movingIds);
    if (targetCache.Count == 0)
    {
      RhinoApp.WriteLine("vAlign: the selected objects do not contain a usable target curve or edge.");
      return Result.Failure;
    }

    Log.Write("vAlign", $"target cache curves={targetCache.Count}");
    using var preview = new AlignPreviewConduit(doc, movingIds, reference);
    preview.Enabled = true;

    try
    {
      while (true)
      {
        using var getter = new GetPoint();
        getter.EnableTransparentCommands(true);
        getter.SetCommandPrompt(
          "Hover and click target edge or curve segment (hold Shift or Ctrl for opposite offset side)");
        getter.AcceptNumber(true, false);
        getter.AcceptString(true);
        var distanceOption = getter.AddOption("Distance", DistanceLabel(distance));
        EdgePick? hoveredTarget = null;
        TransformCandidate? hoveredCandidate = null;
        string hoverStatus = "no target edge under cursor";
        bool loggedValidHover = false;
        var hoverDistance = distance;
        bool reverseOffsetSide = IsOffsetSideModifierDown();

        void RefreshHoveredCandidate(bool reverseSide)
        {
          reverseOffsetSide = reverseSide;
          hoveredCandidate = hoveredTarget == null
            ? null
            : BuildTransformCandidate(
              doc,
              movingIds,
              massCenter,
              reference,
              hoveredTarget,
              hoverDistance,
              reverseOffsetSide);
          if (hoveredTarget != null)
          {
            hoverStatus = hoveredCandidate == null
              ? $"target={hoveredTarget.ParentId} has no valid World XY transform"
              : $"target={hoveredTarget.ParentId} candidate ready; " +
                $"offsetSide={(reverseOffsetSide ? "opposite" : "automatic")}";
          }
          preview.SetHover(hoveredTarget, hoveredCandidate);
        }

        getter.MouseMove += (_, e) =>
        {
          EdgePick? nextTarget = null;
          if (targetCache.TryPick(
                e.Viewport,
                e.WindowPoint,
                out nextTarget,
                out var pickDiagnostics) &&
              nextTarget != null)
          {
            hoverStatus = $"target={nextTarget.ParentId}; {pickDiagnostics}";
          }
          else
          {
            hoverStatus = $"no target edge under cursor; {pickDiagnostics}";
          }

          var previousTarget = hoveredTarget;
          hoveredTarget = nextTarget;
          RefreshHoveredCandidate(IsOffsetSideModifierDown());
          previousTarget?.Dispose();
          if (!loggedValidHover && hoveredTarget != null && hoveredCandidate != null)
          {
            loggedValidHover = true;
            Log.Write(
              "vAlign",
              $"target hover object={hoveredTarget.ParentId} " +
              $"matchedEnd={hoveredTarget.MatchedEnd} " +
              $"angle={RhinoMath.ToDegrees(hoveredCandidate.Angle):G17}; {hoverStatus}");
          }
          doc.Views.Redraw();
        };

        EventHandler modifierPoll = (_, _) =>
        {
          bool nextReverseOffsetSide = IsOffsetSideModifierDown();
          if (nextReverseOffsetSide == reverseOffsetSide)
            return;
          RefreshHoveredCandidate(nextReverseOffsetSide);
          Log.Write(
            "vAlign",
            $"offset side modifier={(reverseOffsetSide ? "opposite" : "automatic")}");
          doc.Views.Redraw();
        };
        RhinoApp.Idle += modifierPoll;

        bool transferHover = false;
        try
        {
          var getResult = getter.Get();
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
          if (getResult != GetResult.Point)
            return Result.Cancel;

          bool clickReverseOffsetSide = IsOffsetSideModifierDown();
          if (clickReverseOffsetSide != reverseOffsetSide)
            RefreshHoveredCandidate(clickReverseOffsetSide);

          if (hoveredTarget == null || hoveredCandidate == null)
          {
            Log.Write("vAlign", $"target click rejected: {hoverStatus}");
            RhinoApp.WriteLine("vAlign: hover directly over a usable target curve or edge.");
            continue;
          }

          target = hoveredTarget;
          candidate = hoveredCandidate;
          transferHover = true;
          Log.Write(
            "vAlign",
            $"target click object={target.ParentId} matchedEnd={target.MatchedEnd} " +
            $"angle={RhinoMath.ToDegrees(candidate.Angle):G17} " +
            $"offsetSide={(reverseOffsetSide ? "opposite" : "automatic")}");
          RestoreMovingSelection(doc, movingIds);
          return Result.Success;
        }
        finally
        {
          RhinoApp.Idle -= modifierPoll;
          preview.ClearHover();
          if (!transferHover)
            hoveredTarget?.Dispose();
          doc.Views.Redraw();
        }
      }
    }
    finally
    {
      preview.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static void ConfigureDirectDistanceInput(GetObject getter)
  {
    getter.AcceptNumber(true, false);
    getter.AcceptString(true);
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
    bool reverseOffsetSide = false)
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

    var automatic = candidates
      .OrderBy(item => item.Overlap)
      .ThenBy(item => item.SameSidePenalty)
      .ThenBy(item => item.AbsoluteAngle)
      .ThenBy(item => item.DirectionIndex)
      .ThenBy(item => item.NormalIndex)
      .FirstOrDefault();
    if (!reverseOffsetSide || !distance.HasValue || automatic == null)
      return automatic;

    return candidates.FirstOrDefault(item =>
        item.DirectionIndex == automatic.DirectionIndex &&
        item.NormalIndex != automatic.NormalIndex)
      ?? automatic;
  }

  private static bool IsOffsetSideModifierDown()
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

      diagnostics =
        $"cache curves={_curves.Count} nearestPx={Math.Sqrt(bestDistanceSquared):0.###}";
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
    private readonly EdgePick _reference;
    private readonly DisplayMaterial _previewMaterial = new(FadedPreviewColor)
    {
      Transparency = PreviewTransparency,
      BackTransparency = PreviewTransparency
    };
    private readonly List<GeometryBase> _previewGeometry = [];
    private readonly List<PreviewInstance> _previewInstances = [];
    private EdgePick? _target;
    private TransformCandidate? _candidate;

    internal AlignPreviewConduit(
      RhinoDoc doc,
      IReadOnlyList<Guid> movingIds,
      EdgePick reference)
    {
      _doc = doc;
      _movingIds = movingIds;
      _movingSet = movingIds.ToHashSet();
      _reference = reference;
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
          continue;
        }

        var geometry = rhinoObject.Geometry?.Duplicate();
        if (geometry == null)
          continue;
        if (!geometry.Transform(candidate.Transform))
        {
          geometry.Dispose();
          continue;
        }
        _previewGeometry.Add(geometry);
      }
    }

    internal void ClearHover() => SetHover(null, null);

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
      if (_candidate != null && e.RhinoObject != null && _movingSet.Contains(e.RhinoObject.Id))
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
      PreviewDisplay.DrawCurve(e.Display, _reference.Curve, ReferenceColor, 1);
      if (_target != null)
      {
        PreviewDisplay.DrawCurve(e.Display, _target.Curve, TargetColor, 2);
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

    private void DisposePreviewGeometry()
    {
      foreach (var geometry in _previewGeometry)
        geometry.Dispose();
      _previewGeometry.Clear();
      _previewInstances.Clear();
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
