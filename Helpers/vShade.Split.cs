using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed partial class vShade
{
  private sealed class ShadeUndoRecords(RhinoDoc doc) : IDisposable
  {
    private uint _record;

    public bool BeginShade()
    {
      if (!doc.UndoRecordingEnabled)
        return true;
      _record = doc.BeginUndoRecord("vShade");
      Log.Write("vShade", $"shade undo record={_record} commands={doc.InCommand(true)}");
      return _record != 0;
    }

    public void BeginSplit()
    {
      EndRecord();
      if (!doc.UndoRecordingEnabled)
        return;
      _record = doc.BeginUndoRecord("vShade Split");
      Log.Write("vShade", $"split undo record={_record} commands={doc.InCommand(true)}");
      if (_record == 0)
        throw new InvalidOperationException("could not start the separate split undo record");
    }

    private void EndRecord()
    {
      if (_record == 0)
        return;
      if (doc.CurrentUndoRecordSerialNumber != _record || !doc.EndUndoRecord(_record))
        throw new InvalidOperationException("could not finish the shade's owned undo record");
      Log.Write("vShade", $"finished undo record={_record}");
      _record = 0;
    }

    public void Dispose() => EndRecord();
  }

  private sealed record SplitProposal(Plane Frame, double MinY, double MaxY, List<double> Seams, double MaterialLength);

  private sealed class SplitPanel(Curve outline, BoundingBox bounds, Brep? face) : IDisposable
  {
    public Curve Outline { get; } = outline;
    public BoundingBox Bounds { get; } = bounds;
    public Brep? Face { get; } = face;
    public List<(GeometryBase Geometry, Color Color)> PreviewDetails { get; } = [];
    public void Dispose()
    {
      Outline.Dispose();
      Face?.Dispose();
      foreach (var detail in PreviewDetails)
        detail.Geometry.Dispose();
    }
  }

  private static bool RunSplit(
    RhinoDoc doc, RunMode mode, Guid sessionId, IReadOnlyList<Guid> outputIds,
    Action beginSplitUndo)
  {
    var sourceObject = FindSplitSource(doc, outputIds);
    var source = sourceObject?.Geometry as Curve;
    if (source == null)
    {
      RhinoApp.WriteLine("vShade: Split needs a closed cut or shade boundary.");
      return false;
    }
    var sourceColor = doc.Layers[sourceObject!.Attributes.LayerIndex]?.Color ?? Color.Black;
    Log.Write("vShade", $"split source={sourceObject.Id} role=" +
      $"{sourceObject.Attributes.GetUserString(MetadataPrefix + "role")} " +
      $"layer={doc.Layers[sourceObject.Attributes.LayerIndex]?.FullPath}");

    var cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;
    if (!source.TryGetPlane(out var sourcePlane, doc.ModelAbsoluteTolerance))
    {
      RhinoApp.WriteLine("vShade: Split needs a planar shade boundary.");
      return false;
    }
    if (sourcePlane.Normal * cplane.Normal < 0.0)
      sourcePlane = new Plane(sourcePlane.Origin, sourcePlane.XAxis, -sourcePlane.YAxis);
    if (!TryProposeSplit(source, sourcePlane, _splitWidth, out var proposal))
    {
      RhinoApp.WriteLine("vShade: could not fit the closed planar shade within SplitWidth.");
      return false;
    }

    var tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    var seamHover = -1;
    var seamMoving = -1;
    var dragPivot = Point3d.Unset;
    var dragAxis = Vector3d.Unset;
    var lastDragRefresh = 0L;
    var dragging = false;
    var panels = new List<SplitPanel>();

    bool UpdateDrag(Point3d cursor)
    {
      if (seamMoving < 0 || !dragPivot.IsValid)
        return false;
      var direction = sourcePlane.ClosestPoint(cursor) - dragPivot;
      if (direction.Length <= tolerance)
        return false;
      var angle = SplitDragAngle(sourcePlane, direction, dragAxis);
      if (!TryProposeSplitAtPivot(source, sourcePlane, _splitWidth, angle, dragPivot,
            out var next, out var pivotSeamIndex) ||
          !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
        return false;
      proposal = next with { MaterialLength = SplitMaterialLength(panels) };
      seamMoving = pivotSeamIndex;
      return true;
    }

    try
    {
      if (!TryBuildSplitPanels(doc, outputIds, source, proposal, tolerance, panels))
      {
        RhinoApp.WriteLine("vShade: could not form closed split parts.");
        return false;
      }
      proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };

      RhinoApp.WriteLine($"vShade: proposed {panels.Count} part(s), {proposal.MaterialLength:G5} material length. Drag an orange cut to adjust it; Enter accepts the cuts.");
      while (true)
      {
        using var getter = new GetPoint();
        getter.EnableTransparentCommands(true);
        getter.PermitObjectSnap(AllowSplitDragObjectSnaps);
        getter.EnableObjectSnapCursors(AllowSplitDragObjectSnaps);
        getter.SetCommandPrompt("Drag split cuts; Enter to place ghosted parts");
        getter.AcceptNothing(true);
        getter.AcceptNumber(true, false);
        var widthOption = new OptionDouble(_splitWidth, SplitJointOverlap * 2 + tolerance, double.MaxValue);
        getter.AddOptionDouble("SplitWidth", ref widthOption);
        var resetIndex = getter.AddOption("Auto");
        getter.MouseMove += (_, e) =>
        {
          dragging = seamMoving >= 0 && e.LeftButtonDown;
          if (!e.LeftButtonDown)
            seamHover = FindSplitSeam(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, source, proposal);
        };
        getter.MouseDown += (_, e) =>
        {
          seamMoving = FindSplitSeam(e.Viewport, e.WindowPoint.X, e.WindowPoint.Y, source, proposal);
          if (seamMoving >= 0)
          {
            using var seam = BuildSplitSeam(
              source, proposal.Frame, proposal.Seams[seamMoving], tolerance);
            if (seam == null)
            {
              seamMoving = -1;
              return;
            }
            var down = sourcePlane.ClosestPoint(e.Point);
            dragPivot = down.DistanceTo(seam.PointAtStart) <= down.DistanceTo(seam.PointAtEnd)
              ? seam.PointAtEnd : seam.PointAtStart;
            dragAxis = proposal.Frame.XAxis;
            getter.SetBasePoint(dragPivot, false);
            getter.Constrain(source, true);
            lastDragRefresh = 0;
            dragging = true;
          }
        };
        getter.DynamicDraw += (_, e) =>
        {
          // DynamicDraw receives the point after Rhino resolves object snaps.
          var now = System.Environment.TickCount64;
          if (dragging && e.CurrentPoint.IsValid &&
              now - lastDragRefresh >= SplitDragRefreshMilliseconds)
          {
            lastDragRefresh = now;
            UpdateDrag(e.CurrentPoint);
          }
          DrawSplitPreview(e.Display, doc, source, proposal, panels, cplane,
            DefaultSplitPreviewAnchor(source, cplane),
            seamMoving >= 0 ? seamMoving : seamHover, sourceColor);
        };
        var result = getter.Get(onMouseUp: true);
        if (result == GetResult.Option)
        {
          if (getter.Option()?.Index == resetIndex ||
              Math.Abs(_splitWidth - widthOption.CurrentValue) > tolerance)
          {
            if (!TryProposeSplit(source, sourcePlane, widthOption.CurrentValue, out var next) ||
                !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
            {
              RhinoApp.WriteLine("vShade: no valid layout for that width.");
              continue;
            }
            _splitWidth = widthOption.CurrentValue;
            SaveOptions();
            proposal = next;
            proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };
          }
          continue;
        }
        if (result == GetResult.Number)
        {
          var newWidth = getter.Number();
          if (!double.IsFinite(newWidth) || newWidth <= SplitJointOverlap * 2 + tolerance ||
              !TryProposeSplit(source, sourcePlane, newWidth, out var next) ||
              !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
            RhinoApp.WriteLine("vShade: enter a larger valid SplitWidth.");
          else
          {
            _splitWidth = newWidth;
            SaveOptions();
            proposal = next;
            proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };
          }
          continue;
        }
        if (result == GetResult.Nothing)
          break;
        if (result != GetResult.Point)
          return false;
        if (seamMoving < 0)
          continue;

        var updated = UpdateDrag(getter.Point());
        seamMoving = -1;
        dragging = false;
        dragPivot = Point3d.Unset;
        if (!updated)
          RhinoApp.WriteLine("vShade: that pivot cannot form valid parts within SplitWidth; last valid preview kept.");
      }

      var placementAnchor = DefaultSplitPreviewAnchor(source, cplane);
      while (true)
      {
        using var getter = new GetPoint();
        getter.EnableTransparentCommands(true);
        getter.SetCommandPrompt("Place split parts; Enter uses the shown location");
        getter.AcceptNothing(true);
        getter.AcceptNumber(true, false);
        var widthOption = new OptionDouble(_splitWidth, SplitJointOverlap * 2 + tolerance, double.MaxValue);
        getter.AddOptionDouble("SplitWidth", ref widthOption);
        getter.MouseMove += (_, e) => placementAnchor = e.Point;
        getter.DynamicDraw += (_, e) => DrawSplitPreview(
          e.Display, doc, source, proposal, panels, cplane, placementAnchor, -1, sourceColor);
        var result = getter.Get();
        if (result == GetResult.Option || result == GetResult.Number)
        {
          var newWidth = result == GetResult.Number ? getter.Number() : widthOption.CurrentValue;
          if (!double.IsFinite(newWidth) || newWidth <= SplitJointOverlap * 2 + tolerance ||
              !TryProposeSplit(source, sourcePlane, newWidth, out var next) ||
              !TryReplaceSplitPanels(doc, outputIds, source, next, tolerance, panels))
          {
            RhinoApp.WriteLine("vShade: enter a larger valid SplitWidth.");
            continue;
          }
          _splitWidth = newWidth;
          SaveOptions();
          proposal = next;
          proposal = proposal with { MaterialLength = SplitMaterialLength(panels) };
          continue;
        }
        if (result is not (GetResult.Point or GetResult.Nothing))
          return false;
        if (result == GetResult.Point)
          placementAnchor = getter.Point();
        if (CommitSplit(doc, source, sessionId,
              outputIds, proposal, panels, cplane, placementAnchor, tolerance, beginSplitUndo))
        {
          RhinoApp.WriteLine($"vShade: created {panels.Count} split part(s) within width {_splitWidth:G5}; material length {proposal.MaterialLength:G5}.");
          return true;
        }
        return false;
      }
    }
    finally
    {
      foreach (var panel in panels)
        panel.Dispose();
    }
  }

  private static RhinoObject? FindSplitSource(RhinoDoc doc, IReadOnlyList<Guid> outputIds) =>
    outputIds.Select(doc.Objects.FindId)
      .Where(obj => obj?.Geometry is Curve curve && curve.IsClosed)
      .Where(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") is "cut" or "boundary")
      .OrderBy(obj => obj!.Attributes.GetUserString(MetadataPrefix + "role") == "cut" ? 0 : 1)
      .FirstOrDefault();

  private static double PlaneAngle(Plane plane, Vector3d direction) =>
    Math.Atan2(direction * plane.YAxis, direction * plane.XAxis);

  private static double SplitDragAngle(Plane plane, Vector3d direction, Vector3d previousAxis)
  {
    // Either endpoint can move without reversing the laid-out parts' frame.
    if (direction * previousAxis < 0.0) direction = -direction;
    return PlaneAngle(plane, direction);
  }

  private static bool TryProposeSplitAtPivot(
    Curve source,
    Plane sourcePlane,
    double width,
    double angle,
    Point3d pivot,
    out SplitProposal proposal,
    out int pivotSeamIndex)
  {
    proposal = null!;
    pivotSeamIndex = -1;
    var xAxis = Math.Cos(angle) * sourcePlane.XAxis + Math.Sin(angle) * sourcePlane.YAxis;
    var yAxis = -Math.Sin(angle) * sourcePlane.XAxis + Math.Cos(angle) * sourcePlane.YAxis;
    var frame = new Plane(sourcePlane.Origin, xAxis, yAxis);
    var bounds = source.GetBoundingBox(frame);
    var pivotY = FrameY(frame, pivot);
    var spacing = width - 2 * SplitJointOverlap;
    if (!bounds.IsValid || spacing <= RhinoMath.ZeroTolerance ||
        pivotY <= bounds.Min.Y + RhinoMath.ZeroTolerance ||
        pivotY >= bounds.Max.Y - RhinoMath.ZeroTolerance)
      return false;

    var leftSpan = pivotY - bounds.Min.Y;
    var rightSpan = bounds.Max.Y - pivotY;
    var leftParts = Math.Max(1, (int)Math.Ceiling((leftSpan - SplitJointOverlap / 2) / spacing));
    var rightParts = Math.Max(1, (int)Math.Ceiling((rightSpan - SplitJointOverlap / 2) / spacing));
    if (leftParts + rightParts > SplitMaxParts)
      return false;

    var seams = new List<double>();
    for (var index = 1; index < leftParts; index++)
      seams.Add(bounds.Min.Y + leftSpan * index / leftParts);
    pivotSeamIndex = seams.Count;
    seams.Add(pivotY);
    for (var index = 1; index < rightParts; index++)
      seams.Add(pivotY + rightSpan * index / rightParts);
    proposal = new SplitProposal(frame, bounds.Min.Y, bounds.Max.Y, seams, 0.0);
    return ValidSplitSeams(proposal, width, RhinoMath.ZeroTolerance);
  }

  private static bool TryProposeSplit(Curve source, Plane cplane, double width, out SplitProposal proposal)
  {
    proposal = null!;
    var parameters = source.DivideByCount(SplitBoundarySamples, includeEnds: true);
    if (parameters == null || parameters.Length < 3)
      return false;
    var points = parameters.Select(source.PointAt).ToArray();
    var bestScore = double.PositiveInfinity;
    (double X, double Y)[]? bestPoints = null;
    for (var degrees = 0.0; degrees < 180.0; degrees += SplitAngleStepDegrees)
    {
      var radians = RhinoMath.ToRadians(degrees);
      var xAxis = Math.Cos(radians) * cplane.XAxis + Math.Sin(radians) * cplane.YAxis;
      var yAxis = -Math.Sin(radians) * cplane.XAxis + Math.Cos(radians) * cplane.YAxis;
      var frame = new Plane(cplane.Origin, xAxis, yAxis);
      var xy = points.Select(point =>
      {
        var displacement = point - frame.Origin;
        return (X: displacement * frame.XAxis, Y: displacement * frame.YAxis);
      }).ToArray();
      var minY = xy.Min(point => point.Y);
      var maxY = xy.Max(point => point.Y);
      var span = maxY - minY;
      var usable = width - 2 * SplitJointOverlap;
      if (usable <= RhinoMath.ZeroTolerance)
        continue;
      var minParts = Math.Max(1, (int)Math.Ceiling((span - SplitJointOverlap) / usable));
      if (minParts > SplitMaxParts)
        continue;
      for (var count = minParts; count <= SplitMaxParts; count++)
      {
        var seams = Enumerable.Range(1, count - 1)
          .Select(index => minY + span * index / count).ToList();
        var candidate = new SplitProposal(frame, minY, maxY, seams, 0.0);
        if (!ValidSplitSeams(candidate, width, RhinoMath.ZeroTolerance))
          continue;
        var score = EstimateSplitLength(xy, candidate);
        if (score >= bestScore)
          continue;
        bestScore = score;
        proposal = candidate with { MaterialLength = score };
        bestPoints = xy;
      }
    }
    if (!double.IsFinite(bestScore) || bestPoints == null)
      return false;
    proposal = RefineSplitSeams(bestPoints, proposal, width);
    return true;
  }

  private static double EstimateSplitLength(
    IReadOnlyList<(double X, double Y)> points, SplitProposal proposal)
  {
    var length = 0.0;
    for (var index = 0; index <= proposal.Seams.Count; index++)
    {
      var lower = index == 0 ? proposal.MinY : proposal.Seams[index - 1] - SplitJointOverlap / 2;
      var upper = index == proposal.Seams.Count ? proposal.MaxY : proposal.Seams[index] + SplitJointOverlap / 2;
      if (!TrySplitBandBounds(points, lower, upper, out var minX, out var maxX))
        return double.PositiveInfinity;
      length += maxX - minX + (index == 0 ? 0.0 : SplitLayoutGap);
    }
    return length;
  }

  private static SplitProposal RefineSplitSeams(
    IReadOnlyList<(double X, double Y)> points, SplitProposal proposal, double width)
  {
    if (proposal.Seams.Count == 0)
      return proposal;
    for (var pass = 0; pass < SplitSeamRefinementPasses; pass++)
    for (var index = 0; index < proposal.Seams.Count; index++)
    {
      var lower = index == 0 ? proposal.MinY : proposal.Seams[index - 1];
      var upper = index == proposal.Seams.Count - 1 ? proposal.MaxY : proposal.Seams[index + 1];
      var best = proposal;
      for (var step = 1; step < SplitSeamRefinementSteps; step++)
      {
        var seams = proposal.Seams.ToList();
        seams[index] = lower + (upper - lower) * step / SplitSeamRefinementSteps;
        var candidate = proposal with { Seams = seams };
        if (!ValidSplitSeams(candidate, width, RhinoMath.ZeroTolerance))
          continue;
        var length = EstimateSplitLength(points, candidate);
        if (length + RhinoMath.ZeroTolerance >= best.MaterialLength)
          continue;
        best = candidate with { MaterialLength = length };
      }
      proposal = best;
    }
    return proposal;
  }

  private static bool TrySplitBandBounds(
    IReadOnlyList<(double X, double Y)> points,
    double lower,
    double upper,
    out double minX,
    out double maxX)
  {
    minX = double.PositiveInfinity;
    maxX = double.NegativeInfinity;
    for (var index = 0; index < points.Count; index++)
    {
      var a = points[index];
      var b = points[(index + 1) % points.Count];
      if (a.Y >= lower && a.Y <= upper)
      {
        minX = Math.Min(minX, a.X);
        maxX = Math.Max(maxX, a.X);
      }
      if (Math.Abs(b.Y - a.Y) <= RhinoMath.ZeroTolerance)
        continue;
      for (var edgeIndex = 0; edgeIndex < 2; edgeIndex++)
      {
        var edgeY = edgeIndex == 0 ? lower : upper;
        var fraction = (edgeY - a.Y) / (b.Y - a.Y);
        if (fraction < 0.0 || fraction > 1.0)
          continue;
        var x = a.X + fraction * (b.X - a.X);
        minX = Math.Min(minX, x);
        maxX = Math.Max(maxX, x);
      }
    }
    return double.IsFinite(minX) && maxX > minX;
  }

  private static bool ValidSplitSeams(SplitProposal proposal, double width, double tolerance)
  {
    var previous = proposal.MinY;
    for (var index = 0; index <= proposal.Seams.Count; index++)
    {
      var next = index == proposal.Seams.Count ? proposal.MaxY : proposal.Seams[index];
      var panelWidth = next - previous +
        (index > 0 ? SplitJointOverlap / 2 : 0.0) +
        (index < proposal.Seams.Count ? SplitJointOverlap / 2 : 0.0);
      if (next <= previous + tolerance || panelWidth > width - SplitJointOverlap + tolerance)
        return false;
      previous = next;
    }
    return true;
  }

  private static bool TryReplaceSplitPanels(
    RhinoDoc doc, IReadOnlyList<Guid> outputIds,
    Curve source, SplitProposal proposal, double tolerance, List<SplitPanel> panels)
  {
    var replacement = new List<SplitPanel>();
    if (!TryBuildSplitPanels(doc, outputIds, source, proposal, tolerance, replacement))
      return false;
    foreach (var panel in panels)
      panel.Dispose();
    panels.Clear();
    panels.AddRange(replacement);
    return true;
  }

  private static double SplitMaterialLength(IReadOnlyList<SplitPanel> panels) =>
    panels.Sum(panel => panel.Bounds.Max.X - panel.Bounds.Min.X) +
    Math.Max(0, panels.Count - 1) * SplitLayoutGap;

  private static bool TryBuildSplitPanels(
    RhinoDoc doc, IReadOnlyList<Guid> outputIds,
    Curve source, SplitProposal proposal, double tolerance, List<SplitPanel> panels)
  {
    try
    {
      for (var index = 0; index <= proposal.Seams.Count; index++)
      {
        var lower = index == 0 ? proposal.MinY : proposal.Seams[index - 1] - SplitJointOverlap / 2;
        var upper = index == proposal.Seams.Count ? proposal.MaxY : proposal.Seams[index] + SplitJointOverlap / 2;
        var outline = BuildExactSplitOutline(
          source, proposal.Frame, lower, upper,
          index > 0, index < proposal.Seams.Count, tolerance);
        if (outline == null)
        {
          throw new InvalidOperationException($"part {index + 1} could not be assembled from the original cut boundary");
        }
        var faces = Brep.CreatePlanarBreps(outline, tolerance);
        var face = faces?.FirstOrDefault();
        if (faces != null)
          foreach (var extra in faces.Skip(1))
            extra.Dispose();
        panels.Add(new SplitPanel(
          outline, outline.GetBoundingBox(proposal.Frame), face));
      }
      for (var index = 0; index < panels.Count; index++)
      {
        var panel = panels[index];
        var firstY = index == 0 ? proposal.MinY : proposal.Seams[index - 1];
        var lastY = index == panels.Count - 1 ? proposal.MaxY : proposal.Seams[index];
        foreach (var sourceId in outputIds)
        {
          var obj = doc.Objects.FindId(sourceId);
          if (obj == null || obj.Attributes.LayerIndex < 0 ||
              obj.Attributes.LayerIndex >= doc.Layers.Count)
            continue;
          var color = doc.Layers[obj.Attributes.LayerIndex]?.Color ?? Color.Black;
          if (obj.Geometry is Curve detail && !ReferenceEquals(detail, source) &&
              obj.Attributes.GetUserString(MetadataPrefix + "role") is not ("bisector" or "cut"))
          {
            bool boundary = obj.Attributes.GetUserString(MetadataPrefix + "role") == "boundary";
            foreach (var clipped in ClipSplitPartDetail(
              detail, panel, proposal.Frame, tolerance, boundary))
              panel.PreviewDetails.Add((clipped, color));
          }
          else if (obj.Geometry is TextEntity label)
          {
            var y = FrameY(proposal.Frame, label.Plane.Origin);
            if (y >= firstY - tolerance && y <= lastY + tolerance &&
                panel.Outline.Contains(label.Plane.Origin, proposal.Frame, tolerance) != PointContainment.Outside &&
                label.Duplicate() is TextEntity labelCopy)
              panel.PreviewDetails.Add((labelCopy, color));
          }
        }
      }
      return true;
    }
    catch (Exception ex)
    {
      foreach (var panel in panels)
        panel.Dispose();
      panels.Clear();
      Log.Write("vShade", $"split layout rejected: {ex.Message}");
      return false;
    }
  }

  private static Curve? BuildExactSplitOutline(
    Curve source, Plane frame, double lower, double upper,
    bool cutLower, bool cutUpper, double tolerance)
  {
    var seams = new List<Curve>();
    var pieces = new List<Curve>();
    try
    {
      if (cutLower)
      {
        var seam = BuildSplitSeam(source, frame, lower, tolerance);
        if (seam == null) return null;
        seams.Add(seam);
      }
      if (cutUpper)
      {
        var seam = BuildSplitSeam(source, frame, upper, tolerance);
        if (seam == null) return null;
        seams.Add(seam);
      }

      var parameters = new List<double>();
      foreach (var seam in seams)
      {
        if (!source.ClosestPoint(seam.PointAtStart, out double first) ||
            !source.ClosestPoint(seam.PointAtEnd, out double last) ||
            source.PointAt(first).DistanceTo(seam.PointAtStart) > tolerance ||
            source.PointAt(last).DistanceTo(seam.PointAtEnd) > tolerance)
          return null;
        parameters.Add(first);
        parameters.Add(last);
      }
      var splitParameters = parameters
        .Where(t => t > source.Domain.T0 + RhinoMath.ZeroTolerance &&
                    t < source.Domain.T1 - RhinoMath.ZeroTolerance)
        .Distinct()
        .OrderBy(t => t)
        .ToArray();
      var split = splitParameters.Length == 0
        ? new[] { source.DuplicateCurve() }
        : source.Split(splitParameters);
      if (split == null || split.Length == 0)
        return null;
      foreach (var piece in split)
      {
        var y = FrameY(frame, piece.PointAtNormalizedLength(0.5));
        if (y >= lower - tolerance && y <= upper + tolerance)
          pieces.Add(piece);
        else
          piece.Dispose();
      }
      if (pieces.Count == 0)
        return null;
      pieces.AddRange(seams.Select(seam => seam.DuplicateCurve()));
      var joined = Curve.JoinCurves(pieces, tolerance, preserveDirection: false);
      if (joined.Length != 1 || !joined[0].IsClosed)
      {
        foreach (var curve in joined) curve.Dispose();
        return null;
      }
      return joined[0];
    }
    finally
    {
      foreach (var seam in seams) seam.Dispose();
      foreach (var piece in pieces) piece.Dispose();
    }
  }

  private static double FrameY(Plane frame, Point3d point) => (point - frame.Origin) * frame.YAxis;

  private static int FindSplitSeam(
    Rhino.Display.RhinoViewport viewport, int x, int y, Curve source, SplitProposal proposal)
  {
    if (!viewport.GetFrustumLine(x, y, out var ray) ||
        !Intersection.LinePlane(ray, proposal.Frame, out var parameter))
      return -1;
    var cursorY = FrameY(proposal.Frame, ray.PointAt(parameter));
    var cursorX = (ray.PointAt(parameter) - proposal.Frame.Origin) * proposal.Frame.XAxis;
    var bounds = source.GetBoundingBox(proposal.Frame);
    if (cursorX < bounds.Min.X || cursorX > bounds.Max.X)
      return -1;
    var nearest = TunePickRadiusPixels;
    var result = -1;
    for (var index = 0; index < proposal.Seams.Count; index++)
    {
      var point = ray.PointAt(parameter) + proposal.Frame.YAxis * (proposal.Seams[index] - cursorY);
      var pixel = viewport.WorldToClient(point);
      var dx = pixel.X - x;
      var dy = pixel.Y - y;
      var distance = Math.Sqrt(dx * dx + dy * dy);
      if (distance >= nearest)
        continue;
      nearest = distance;
      result = index;
    }
    return result;
  }

  private static Point3d DefaultSplitPreviewAnchor(Curve source, Plane plane)
  {
    var bounds = source.GetBoundingBox(plane);
    return plane.PointAt(bounds.Max.X + SplitLayoutGap * 4, bounds.Min.Y);
  }

  private static Transform PanelTransform(
    Plane frame, Plane cplane, Point3d anchor, BoundingBox bounds, double xOffset)
  {
    var target = new Plane(anchor, cplane.XAxis, cplane.YAxis);
    return Transform.Translation(target.XAxis * (xOffset - bounds.Min.X) -
                                 target.YAxis * bounds.Min.Y) *
           Transform.PlaneToPlane(frame, target);
  }

  private static void DrawSplitPreview(
    Rhino.Display.DisplayPipeline display,
    RhinoDoc doc,
    Curve source,
    SplitProposal proposal,
    IReadOnlyList<SplitPanel> panels,
    Plane cplane,
    Point3d anchor,
    int highlightedSeam,
    Color sourceColor)
  {
    var cutLayerIndex = doc.Layers.FindByFullPath(_cutLayer, -1);
    var cutColor = cutLayerIndex >= 0 ? doc.Layers[cutLayerIndex].Color : sourceColor;
    var fadedCut = FadedSplitColor(cutColor);
    var plotLayerIndex = doc.Layers.FindByFullPath(SplitOverlapLayerName, -1);
    var plotColor = plotLayerIndex >= 0
      ? doc.Layers[plotLayerIndex].Color
      : DefaultSplitPlotColor;
    var fadedPlot = FadedSplitColor(plotColor);
    for (var index = 0; index < proposal.Seams.Count; index++)
    {
      using var seam = BuildSplitSeam(
        source, proposal.Frame, proposal.Seams[index], doc.ModelAbsoluteTolerance);
      if (seam != null)
      {
        if (index == highlightedSeam)
          PreviewDisplay.DrawOutlinedCurve(display, seam, SplitSeamColor);
        else
          PreviewDisplay.DrawCurve(display, seam, SplitSeamColor);
      }
    }
    var xOffset = 0.0;
    using var ghostMaterial = new Rhino.Display.DisplayMaterial(sourceColor)
    {
      Transparency = SplitGhostTransparency,
      BackTransparency = SplitGhostTransparency
    };
    for (var panelIndex = 0; panelIndex < panels.Count; panelIndex++)
    {
      var panel = panels[panelIndex];
      var transform = PanelTransform(proposal.Frame, cplane, anchor, panel.Bounds, xOffset);
      if (panel.Face != null)
      {
        using var face = panel.Face.DuplicateBrep();
        face.Transform(transform);
        display.DrawBrepShaded(face, ghostMaterial);
      }
      foreach (var detail in panel.PreviewDetails)
      {
        using var copy = detail.Geometry.Duplicate();
        if (copy == null)
          continue;
        copy.Transform(transform);
        var color = FadedSplitColor(detail.Color);
        if (copy is Curve curve)
          PreviewDisplay.DrawCurve(display, curve, color);
        else if (copy is TextEntity label)
          display.DrawAnnotation(label, color);
      }
      foreach (var seamIndex in new[] { panelIndex - 1, panelIndex })
      {
        if (seamIndex < 0 || seamIndex >= proposal.Seams.Count)
          continue;
        var overlapY = OverlapMarkY(proposal.Seams[seamIndex], panelIndex, seamIndex);
        using var overlapLine = BuildSplitSeam(
          source, proposal.Frame, overlapY, doc.ModelAbsoluteTolerance);
        if (overlapLine == null)
          continue;
        using var overlapLabel = CreateOverlapLabel(
          doc, source, proposal.Frame, overlapLine, proposal.Seams[seamIndex], overlapY,
          seamIndex + 1);
        overlapLine.Transform(transform);
        overlapLabel.Transform(transform);
        PreviewDisplay.DrawCurve(display, overlapLine, fadedPlot);
        display.DrawAnnotation(overlapLabel, fadedPlot);
      }
      using var preview = panel.Outline.DuplicateCurve();
      preview.Transform(transform);
      PreviewDisplay.DrawCurve(display, preview, fadedCut);
      xOffset += panel.Bounds.Max.X - panel.Bounds.Min.X + SplitLayoutGap;
    }
  }

  private static Color FadedSplitColor(Color color) =>
    Color.FromArgb(SplitPreviewAlpha, color.R, color.G, color.B);

  private static Curve? BuildSplitSeam(Curve source, Plane frame, double y, double tolerance)
  {
    var bounds = source.GetBoundingBox(frame);
    var padding = Math.Max(1.0, bounds.Diagonal.Length);
    using var probe = new LineCurve(
      frame.PointAt(bounds.Min.X - padding, y),
      frame.PointAt(bounds.Max.X + padding, y));
    using var hits = Intersection.CurveCurve(source, probe, tolerance, tolerance);
    if (hits == null || hits.Count < 2)
      return null;
    var parameters = hits.Where(hit => hit.IsPoint).Select(hit => hit.ParameterB).OrderBy(t => t).ToArray();
    if (parameters.Length < 2 || parameters[^1] - parameters[0] <= RhinoMath.ZeroTolerance)
      return null;
    return new LineCurve(probe.PointAt(parameters[0]), probe.PointAt(parameters[^1]));
  }

  private static double OverlapMarkY(double seamY, int partIndex, int seamIndex) =>
    seamY + (seamIndex == partIndex ? -SplitJointOverlap / 2 : SplitJointOverlap / 2);

  private static TextEntity CreateOverlapLabel(
    RhinoDoc doc,
    Curve source,
    Plane frame,
    Curve overlapLine,
    double seamY,
    double overlapY,
    int seamNumber)
  {
    var cutY = 2.0 * seamY - overlapY;
    using var cutLine = BuildSplitSeam(
      source, frame, cutY, doc.ModelAbsoluteTolerance);
    var overlapBounds = overlapLine.GetBoundingBox(frame);
    var cutBounds = cutLine?.GetBoundingBox(frame) ?? overlapBounds;
    double minX = Math.Max(overlapBounds.Min.X, cutBounds.Min.X);
    double maxX = Math.Min(overlapBounds.Max.X, cutBounds.Max.X);
    if (maxX <= minX)
    {
      minX = overlapBounds.Min.X;
      maxX = overlapBounds.Max.X;
    }
    var origin = frame.PointAt((minX + maxX) / 2.0, seamY);
    var text = new TextEntity
    {
      DimensionStyleId = doc.DimStyles.Current.Id,
      Plane = new Plane(origin, frame.XAxis, frame.YAxis),
      TextHeight = SplitOverlapLabelHeight,
      Justification = TextJustification.BottomLeft,
      DimensionScale = DefaultLabelDimensionScale,
      TextOrientation = TextOrientation.InPlane,
      DrawForward = false,
      PlainText = seamNumber.ToString(CultureInfo.InvariantCulture)
    };
    if (!AnnotationTextTransform.ApplyFixedDisplayTextHeight(
      doc, text, doc.Views.ActiveView?.ActiveViewport,
      SplitOverlapLabelHeight, DefaultLabelDimensionScale))
      Log.Write("vShade", $"split seam text height override failed scale={doc.ModelSpaceTextScale:G17}");
    var bounds = OverlapLabelGlyphBounds(doc, text, frame);
    if (bounds.IsValid)
    {
      text.Transform(Transform.Translation(
        frame.XAxis * (((minX + maxX) / 2.0) - bounds.Center.X) +
        frame.YAxis * (seamY - bounds.Center.Y)));
    }
    return text;
  }

  private static BoundingBox OverlapLabelGlyphBounds(RhinoDoc doc, TextEntity text, Plane frame)
  {
    var parentStyle = doc.DimStyles.FindId(text.DimensionStyleId) ?? doc.DimStyles.Current;
    using var measurement = (TextEntity)text.Duplicate();
    measurement.ParentDimensionStyle = parentStyle;
    measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(
      doc, text, doc.Views.ActiveView?.ActiveViewport);
    using var style = measurement.GetDimensionStyle(parentStyle);
    var outlines = measurement.CreateCurves(style ?? parentStyle, true,
      SplitLabelGlyphScale, SplitLabelGlyphSpacing);
    if (outlines == null || outlines.Length == 0)
      return text.GetBoundingBox(frame);
    var bounds = BoundingBox.Empty;
    foreach (var outline in outlines)
    {
      try { bounds.Union(outline.GetBoundingBox(frame)); }
      finally { outline.Dispose(); }
    }
    return bounds.IsValid ? bounds : text.GetBoundingBox(frame);
  }

  private static bool CommitSplit(
    RhinoDoc doc, Curve source, Guid sessionId, IReadOnlyList<Guid> outputIds,
    SplitProposal proposal, IReadOnlyList<SplitPanel> panels,
    Plane cplane, Point3d anchor, double tolerance, Action beginSplitUndo)
  {
    var created = new List<Guid>();
    var sourceSeams = new List<Guid>();
    var createdGroups = new List<int>();
    var splitId = Guid.NewGuid();
    try
    {
      beginSplitUndo();
      var referenceLayer = UzipCommon.EnsureLayer(doc, ReferenceLayerName, ReferenceLayerColor);
      var cutLayer = UzipCommon.EnsureLayer(doc, _cutLayer);
      var overlapLayer = proposal.Seams.Count > 0
        ? UzipCommon.EnsureLayer(doc, SplitOverlapLayerName)
        : -1;
      foreach (var y in proposal.Seams)
      {
        using var seam = BuildSplitSeam(source, proposal.Frame, y, tolerance);
        if (seam == null)
          throw new InvalidOperationException("a cut line no longer crosses the shade twice");
        var attributes = new ObjectAttributes { LayerIndex = referenceLayer, Name = SplitSeamObjectName };
        attributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
        attributes.SetUserString(MetadataPrefix + "source_session", sessionId.ToString());
        var id = doc.Objects.AddCurve(seam, attributes);
        if (id == Guid.Empty)
          throw new InvalidOperationException("could not create a split cut line");
        created.Add(id);
        sourceSeams.Add(id);
      }

      var xOffset = 0.0;
      for (var index = 0; index < panels.Count; index++)
      {
        var partIds = new List<Guid>();
        var panel = panels[index];
        var transform = PanelTransform(proposal.Frame, cplane, anchor, panel.Bounds, xOffset);
        foreach (var seamIndex in new[] { index - 1, index })
        {
          if (seamIndex < 0 || seamIndex >= proposal.Seams.Count)
            continue;
          var overlapY = OverlapMarkY(proposal.Seams[seamIndex], index, seamIndex);
          using var overlapLine = BuildSplitSeam(source, proposal.Frame, overlapY, tolerance);
          if (overlapLine == null)
            throw new InvalidOperationException("could not mark the overlap on a split part");
          using var overlapLabel = CreateOverlapLabel(
            doc, source, proposal.Frame, overlapLine, proposal.Seams[seamIndex], overlapY,
            seamIndex + 1);
          overlapLine.Transform(transform);
          overlapLabel.Transform(transform);
          var overlapAttributes = new ObjectAttributes
          {
            LayerIndex = overlapLayer,
            Name = SplitOverlapLineObjectName
          };
          overlapAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
          overlapAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
          var overlapId = doc.Objects.AddCurve(overlapLine, overlapAttributes);
          if (overlapId == Guid.Empty)
            throw new InvalidOperationException("could not add an overlap line");
          created.Add(overlapId);
          partIds.Add(overlapId);

          var labelAttributes = new ObjectAttributes
          {
            LayerIndex = overlapLayer,
            Name = SplitOverlapLabelObjectName
          };
          labelAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
          labelAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
          var overlapLabelId = doc.Objects.AddText(overlapLabel, labelAttributes);
          if (overlapLabelId == Guid.Empty)
            throw new InvalidOperationException("could not add an overlap label");
          created.Add(overlapLabelId);
          partIds.Add(overlapLabelId);
          var placedFrame = proposal.Frame;
          placedFrame.Transform(transform);
          var placedLabel = doc.Objects.FindId(overlapLabelId)?.Geometry as TextEntity ?? overlapLabel;
          var glyphBounds = OverlapLabelGlyphBounds(doc, placedLabel, placedFrame);
          var cutY = 2.0 * proposal.Seams[seamIndex] - overlapY;
          var clearance = Math.Min(
            glyphBounds.Min.Y - Math.Min(cutY, overlapY),
            Math.Max(cutY, overlapY) - glyphBounds.Max.Y);
          Log.Write("vShade", $"split label={overlapLabelId} part={index + 1} " +
            $"seam={seamIndex + 1} cutY={cutY:G17} " +
            $"plotY={overlapY:G17} height={overlapLabel.TextHeight:G6} " +
            $"glyphY=[{glyphBounds.Min.Y:G6},{glyphBounds.Max.Y:G6}] " +
            $"clearance={clearance:G6} fits={glyphBounds.IsValid && clearance >= SplitOverlapLabelMargin} " +
            $"displayScale={AnnotationTextTransform.ResolveDisplayDimensionScale(doc, overlapLabel, doc.Views.ActiveView?.ActiveViewport):G6} " +
            $"modelScale={doc.ModelSpaceTextScale:G6} layer={doc.Layers[overlapLayer]?.FullPath}");
        }

        foreach (var sourceId in outputIds)
        {
          var obj = doc.Objects.FindId(sourceId);
          if (obj?.Geometry is not Curve detail || ReferenceEquals(detail, source) ||
              obj.Attributes.GetUserString(MetadataPrefix + "role") is "bisector" or "cut")
            continue;
          bool boundary = obj.Attributes.GetUserString(MetadataPrefix + "role") == "boundary";
          foreach (var clipped in ClipSplitPartDetail(
            detail, panel, proposal.Frame, tolerance, boundary))
          {
            using (clipped)
            {
              clipped.Transform(transform);
              var detailAttributes = new ObjectAttributes
              {
                LayerIndex = obj.Attributes.LayerIndex,
                Name = SplitPartObjectName
              };
              detailAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
              detailAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
              var detailId = doc.Objects.AddCurve(clipped, detailAttributes);
              if (detailId == Guid.Empty)
                throw new InvalidOperationException("could not copy split detail");
              created.Add(detailId);
              partIds.Add(detailId);
            }
          }
        }
        var firstY = index == 0 ? proposal.MinY : proposal.Seams[index - 1];
        var lastY = index == panels.Count - 1 ? proposal.MaxY : proposal.Seams[index];
        foreach (var sourceId in outputIds)
        {
          var obj = doc.Objects.FindId(sourceId);
          if (obj?.Geometry is not TextEntity label)
            continue;
          var y = FrameY(proposal.Frame, label.Plane.Origin);
          if (y < firstY - tolerance || y > lastY + tolerance ||
              panel.Outline.Contains(label.Plane.Origin, proposal.Frame, tolerance) == PointContainment.Outside)
            continue;
          using var copy = label.Duplicate() as TextEntity;
          if (copy == null)
            continue;
          copy.Transform(transform);
          var labelAttributes = new ObjectAttributes
          {
            LayerIndex = obj.Attributes.LayerIndex,
            Name = SplitPartObjectName
          };
          labelAttributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
          labelAttributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
          var labelId = doc.Objects.AddText(copy, labelAttributes);
          if (labelId == Guid.Empty)
            throw new InvalidOperationException("could not copy a reinforcement label");
          created.Add(labelId);
          partIds.Add(labelId);
        }
        using var outline = panel.Outline.DuplicateCurve();
        outline.Transform(transform);
        var attributes = new ObjectAttributes
        {
          LayerIndex = cutLayer,
          Name = SplitPartObjectName
        };
        attributes.SetUserString(MetadataPrefix + "split", splitId.ToString());
        attributes.SetUserString(MetadataPrefix + "source_session", sessionId.ToString());
        attributes.SetUserString(MetadataPrefix + "part", (index + 1).ToString(CultureInfo.InvariantCulture));
        var id = doc.Objects.AddCurve(outline, attributes);
        if (id == Guid.Empty)
          throw new InvalidOperationException($"could not create part {index + 1}");
        created.Add(id);
        partIds.Add(id);
        Log.Write("vShade", $"split part={index + 1} cut={id} layer={doc.Layers[cutLayer]?.FullPath}");
        var partGroup = doc.Groups.Add(
          "vShadeSplit_" + splitId.ToString("N") + "_" + (index + 1).ToString(CultureInfo.InvariantCulture));
        if (partGroup < 0)
          throw new InvalidOperationException($"could not create group for part {index + 1}");
        createdGroups.Add(partGroup);
        if (!doc.Groups.AddToGroup(partGroup, partIds))
          throw new InvalidOperationException($"could not group part {index + 1}");
        xOffset += panel.Bounds.Max.X - panel.Bounds.Min.X + SplitLayoutGap;
      }

      if (sourceSeams.Count > 0)
      {
        var shadeGroupName = ShadeGroupPrefix + sessionId.ToString("N");
        var shadeGroup = doc.Groups.FindName(shadeGroupName)?.Index;
        if (!shadeGroup.HasValue)
        {
          shadeGroup = doc.Groups.Add(shadeGroupName);
          if (shadeGroup.Value >= 0)
            createdGroups.Add(shadeGroup.Value);
        }
        if (!shadeGroup.HasValue || shadeGroup.Value < 0 ||
            !doc.Groups.AddToGroup(shadeGroup.Value, sourceSeams))
          throw new InvalidOperationException("could not add split cuts to the original shade group");
      }
      doc.Views.Redraw();
      Log.Write("vShade", $"split source={sessionId} parts={panels.Count} width={_splitWidth:G17} cuts={proposal.Seams.Count}");
      return true;
    }
    catch (Exception ex)
    {
      foreach (var id in created)
        doc.Objects.Delete(id, quiet: true);
      foreach (var groupIndex in createdGroups)
        doc.Groups.Delete(groupIndex);
      doc.Views.Redraw();
      Log.Write("vShade", $"split failed: {ex}");
      RhinoApp.WriteLine($"vShade: split failed: {ex.Message}");
      return false;
    }
  }

  private static IEnumerable<Curve> ClipSplitDetail(Curve detail, Curve clip, Plane frame, double tolerance)
  {
    // Keep only original curve fragments; a region Boolean would add artificial closing edges.
    using var hits = Intersection.CurveCurve(detail, clip, tolerance, tolerance);
    var parameters = hits?.SelectMany(hit => hit.IsPoint
        ? new[] { hit.ParameterA }
        : hit.IsOverlap ? new[] { hit.OverlapA.T0, hit.OverlapA.T1 } : Array.Empty<double>())
      .Where(t => t > detail.Domain.T0 && t < detail.Domain.T1)
      .Distinct().OrderBy(t => t).ToArray() ?? [];
    var pieces = parameters.Length == 0
      ? new[] { detail.DuplicateCurve() }
      : detail.Split(parameters) ?? [];
    var retained = new List<Curve>();
    foreach (var piece in pieces)
    {
      var middle = piece.PointAtNormalizedLength(0.5);
      if (clip.Contains(middle, frame, tolerance) is PointContainment.Inside or PointContainment.Coincident)
        retained.Add(piece);
      else
        piece.Dispose();
    }
    return retained;
  }

  private static IEnumerable<Curve> ClipSplitPartDetail(
    Curve detail, SplitPanel panel, Plane frame, double tolerance, bool boundary)
  {
    if (!boundary)
      return ClipSplitDetail(detail, panel.Outline, frame, tolerance);
    var segments = detail.DuplicateSegments();
    if (segments == null || segments.Length <= 1)
    {
      if (segments != null)
        foreach (var segment in segments) segment.Dispose();
      return ClipSplitDetail(detail, panel.Outline, frame, tolerance)
        .Where(KeepDistinctCutDetail).ToArray();
    }
    var retained = new List<Curve>();
    try
    {
      foreach (var segment in segments)
        foreach (var clipped in ClipSplitDetail(segment, panel.Outline, frame, tolerance))
          if (KeepDistinctCutDetail(clipped))
            retained.Add(clipped);
      return retained;
    }
    finally
    {
      foreach (var segment in segments) segment.Dispose();
    }

    bool KeepDistinctCutDetail(Curve candidate)
    {
      foreach (double fraction in new[] { 0.0, 0.5, 1.0 })
      {
        var point = candidate.PointAtNormalizedLength(fraction);
        if (!panel.Outline.ClosestPoint(point, out double parameter) ||
            point.DistanceTo(panel.Outline.PointAt(parameter)) >
              tolerance * SplitCoincidentBoundaryToleranceMultiplier)
          return true;
      }
      candidate.Dispose();
      return false;
    }
  }
}
