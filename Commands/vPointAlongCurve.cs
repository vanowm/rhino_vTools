namespace vTools.Commands
{
  using Rhino;
  using Rhino.Commands;


  /// <summary>Places a point at an arc-length distance from a picked curve position.</summary>
  public sealed class vPointAlongCurve : vToolsCommand
  {
    public override string EnglishName => "vPointAlongCurve";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) =>
      PointAlongCurveWorkflow.Run(doc);
  }
}

namespace vTools.Commands
{
  using System;
  using System.Drawing;
  using Rhino;
  using Rhino.Commands;
  using Rhino.DocObjects;
  using Rhino.Display;
  using Rhino.Geometry;
  using Rhino.Input;
  using Rhino.Input.Custom;


  internal static class PointAlongCurveWorkflow
  {
    // Defaults and customizable constants
    private const double DefaultDistance = 1.0; // Non-negative arc length in document units; zero places the point at the picked start.
    private const int DefaultDirection = 1; // +1 follows the curve parameter direction; -1 travels in reverse until the cursor chooses a side.
    private const ProjectionMode DefaultProjection = ProjectionMode.Skip; // Skip omits out-of-range points; End clamps; Straight follows the end tangent; Smooth follows a smooth curve extension.
    private static readonly string[] ProjectionNames = ["Skip", "End", "Straight", "Smooth"]; // Command-line Project choices in ProjectionMode enum order.
    private const double SmoothExtensionPadding = 1.01; // Factor above the missing arc length used for smooth extension before solving the exact point; must be greater than one.
    private const int StartPointSize = 2; // Starting-position marker diameter in display pixels; positive integer.
    private const int StartPointOutlineExtra = 1; // Black outline pixels added to the starting-position marker.
    private const int PreviewPointSize = 2; // Result-preview marker diameter in display pixels; positive integer, independent of document point display size.
    private static readonly Color StartPointColor = Color.White; // RGB fill color of the fixed starting-position marker.
    private static readonly Color StartPointOutlineColor = Color.Black; // RGB outline color of the fixed starting-position marker.
    private const int EndWarningFontSize = 12; // Endpoint-shortfall preview text height in display pixels; positive integer.
    private const int EndWarningOffset = 14; // Endpoint-shortfall text offset from the result marker in display pixels.
    private const int EndWarningHaloOffset = 1; // Endpoint-shortfall text shadow offset in display pixels; positive integer.
    private static readonly Color EndWarningColor = Color.DarkRed; // RGB foreground color of the endpoint-shortfall preview warning.
    private static readonly Color EndWarningHaloColor = Color.White; // RGB one-pixel shadow for readable warning text on dark viewports.

    private const string Tag = "vPointAlongCurve";
    private const string DistanceKey = "distance";
    private const string ProjectKey = "project";

    internal enum ProjectionMode { Skip, End, Straight, Smooth }

    internal static Result Run(RhinoDoc doc)
    {
      var saved = ToolsOptionStore.Read(Tag, section =>
      {
        var value = ToolsOptionStore.TryGetDouble(section, DistanceKey, out var number) && ValidDistance(number)
          ? number : DefaultDistance;
        var projection = DefaultProjection;
        if (ToolsOptionStore.TryGetString(section, ProjectKey, out var text)
          && Enum.TryParse<ProjectionMode>(text, true, out var parsed) && Enum.IsDefined(typeof(ProjectionMode), parsed))
          projection = parsed;
        return (Distance: value, Projection: projection);
      });
      var project = saved.Projection;
      var distance = new OptionDouble(saved.Distance, true, 0.0);
      try
      {
        using var selector = new GetObject();
        selector.SetCommandPrompt("Select curve");
        selector.GeometryFilter = ObjectType.Curve;
        selector.SubObjectSelect = false;
        selector.EnablePreSelect(true, true);
        selector.EnableHighlight(false);
        selector.EnableClearObjectsOnEntry(false);
        selector.EnableUnselectObjectsOnExit(false);
        selector.DeselectAllBeforePostSelect = false;
        while (true)
        {
          var projectOption = Configure(selector, ref distance, project);
          var result = selector.Get();
          if (UpdateOptions(selector, result, distance, projectOption, ref project))
            continue;
          if (result != GetResult.Object)
            return selector.CommandResult();
          break;
        }

        using var source = selector.Object(0);
        using var curve = source.Curve()?.DuplicateCurve();
        if (curve == null || !curve.IsValid || curve.GetLength() <= RhinoMath.ZeroTolerance)
        {
          RhinoApp.WriteLine("vPointAlongCurve: select a valid curve with non-zero length.");
          return Result.Failure;
        }
        using var highlight = new PreviewDisplay.ObjectHighlighter(doc);
        highlight.SetObjects([source.ObjectId]);
        using var startPicker = new GetPoint();
        startPicker.SetCommandPrompt("Starting position on curve");
        startPicker.Constrain(curve, true);
        double startParameter;
        while (true)
        {
          var projectOption = Configure(startPicker, ref distance, project);
          var result = startPicker.Get();
          if (UpdateOptions(startPicker, result, distance, projectOption, ref project))
            continue;
          if (result != GetResult.Point)
            return startPicker.CommandResult();
          if (curve.ClosestPoint(startPicker.Point(), out startParameter))
            break;
        }

        var position = new CurvePosition(curve, startParameter);
        var direction = DefaultDirection;
        using var directionPicker = new GetPoint();
        directionPicker.SetCommandPrompt("Move cursor to choose direction; click to place point");
        directionPicker.DynamicDraw += (_, e) =>
        {
          direction = position.ResolveDirection(e.CurrentPoint, distance.CurrentValue, project, direction, e.Display.Viewport);
          e.Display.DrawPoint(position.StartPoint, PointStyle.RoundSimple,
            StartPointSize + StartPointOutlineExtra, StartPointOutlineColor);
          e.Display.DrawPoint(position.StartPoint, PointStyle.RoundSimple,
            StartPointSize, StartPointColor);
          if (position.TryPoint(distance.CurrentValue, direction, project, out var previewPoint, out var actualDistance))
          {
            PreviewDisplay.DrawAddedPoint(e.Display, previewPoint, PreviewPointSize);
            if (actualDistance < distance.CurrentValue)
              DrawEndWarning(doc, e, previewPoint, actualDistance, distance.CurrentValue);
          }
        };
        while (true)
        {
          var projectOption = Configure(directionPicker, ref distance, project);
          var result = directionPicker.Get();
          if (UpdateOptions(directionPicker, result, distance, projectOption, ref project))
            continue;
          if (result != GetResult.Point)
            return directionPicker.CommandResult();
          direction = position.ResolveDirection(directionPicker.Point(), distance.CurrentValue,
            project, direction, directionPicker.View()?.ActiveViewport ?? doc.Views.ActiveView?.ActiveViewport);
          if (!position.TryPoint(distance.CurrentValue, direction, project, out var point, out var actualDistance))
          {
            RhinoApp.WriteLine("vPointAlongCurve: no valid point at this distance. Reduce Distance or change Project.");
            continue;
          }

          var attributes = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };
          foreach (var group in source.Object()?.Attributes.GetGroupList() ?? Array.Empty<int>())
            attributes.AddToGroup(group);
          var pointId = doc.Objects.AddPoint(point, attributes);
          if (pointId == Guid.Empty)
            return Result.Failure;
          SaveDistance(distance.CurrentValue);
          if (actualDistance < distance.CurrentValue)
            RhinoApp.WriteLine($"vPointAlongCurve: point placed at curve end, {doc.FormatNumber(actualDistance)} from start, not the requested {doc.FormatNumber(distance.CurrentValue)}.");
          Log.Write(Tag, $"curve={source.ObjectId} start={startParameter:G17} distance={distance.CurrentValue:G17} actual={actualDistance:G17} project={project} direction={direction} point={pointId}");
          doc.Views.Redraw();
          return Result.Success;
        }
      }
      catch (Exception ex)
      {
        Log.Write(Tag, $"failed: {ex}");
        RhinoApp.WriteLine("vPointAlongCurve: could not evaluate or place the point.");
        return Result.Failure;
      }
      finally { distance.Dispose(); }
    }

    private static int Configure(GetBaseClass getter, ref OptionDouble distance, ProjectionMode project)
    {
      getter.EnableTransparentCommands(true);
      getter.AcceptNumber(true, true);
      getter.ClearCommandOptions();
      getter.AddOptionDouble("Distance", ref distance);
      return getter.AddOptionList("Project", ProjectionNames, (int)project);
    }

    private static bool UpdateOptions(GetBaseClass getter, GetResult result, OptionDouble distance,
      int projectOption, ref ProjectionMode project)
    {
      if (result != GetResult.Number && result != GetResult.Option)
        return false;
      if (result == GetResult.Option && getter.OptionIndex() == projectOption)
      {
        var index = getter.Option().CurrentListOptionIndex;
        if (index >= 0 && index < ProjectionNames.Length)
        {
          project = (ProjectionMode)index;
          var name = project.ToString();
          ToolsOptionStore.Update(Tag, section => section[ProjectKey] = name);
        }
        return true;
      }
      var value = result == GetResult.Number ? getter.Number() : distance.CurrentValue;
      if (!ValidDistance(value))
      {
        RhinoApp.WriteLine("vPointAlongCurve: Distance must be a finite, non-negative number.");
        return true;
      }
      distance.CurrentValue = value;
      SaveDistance(value);
      return true;
    }

    private static bool ValidDistance(double distance) => double.IsFinite(distance) && distance >= 0.0;

    private static void SaveDistance(double distance) =>
      ToolsOptionStore.Update(Tag, section => section[DistanceKey] = distance);

    private static void DrawEndWarning(RhinoDoc doc, GetPointDrawEventArgs e, Point3d point,
      double actualDistance, double requestedDistance)
    {
      var pixel = e.Display.Viewport.WorldToClient(point);
      var origin = new Point2d(pixel.X + EndWarningOffset, pixel.Y + EndWarningOffset);
      var text = $"Curve end: {doc.FormatNumber(actualDistance)}; requested {doc.FormatNumber(requestedDistance)}";
      e.Display.Draw2dText(text, EndWarningHaloColor,
        new Point2d(origin.X + EndWarningHaloOffset, origin.Y + EndWarningHaloOffset), false, EndWarningFontSize);
      e.Display.Draw2dText(text, EndWarningColor, origin, false, EndWarningFontSize);
    }

    // Both distance points are cached; hover compares their visible positions rather than curve parameters.
    internal sealed class CurvePosition
    {
      private readonly Curve _curve;
      private readonly double _startLength;
      private readonly double _totalLength;
      private double _cachedDistance = double.NaN;
      private ProjectionMode _cachedProject = DefaultProjection;
      private Point3d _forwardPoint = Point3d.Unset;
      private Point3d _backwardPoint = Point3d.Unset;
      private double _forwardDistance;
      private double _backwardDistance;

      internal CurvePosition(Curve curve, double startParameter)
      {
        _curve = curve;
        startParameter = curve.IsClosed && startParameter == curve.Domain.T1
          ? curve.Domain.T0 : startParameter;
        StartPoint = curve.PointAt(startParameter);
        _totalLength = curve.GetLength();
        _startLength = startParameter <= curve.Domain.T0 ? 0.0
          : curve.GetLength(new Interval(curve.Domain.T0, startParameter));
      }

      internal Point3d StartPoint { get; }

      internal int ResolveDirection(Point3d cursor, double distance, ProjectionMode project,
        int previousDirection, RhinoViewport? viewport)
      {
        if (!cursor.IsValid)
          return previousDirection;
        var hasForward = TryPoint(distance, 1, project, out var forwardPoint, out _);
        var hasBackward = TryPoint(distance, -1, project, out var backwardPoint, out _);
        if (!hasForward)
          return hasBackward ? -1 : previousDirection;
        if (!hasBackward)
          return 1;

        var forwardDistance = viewport == null
          ? cursor.DistanceToSquared(forwardPoint)
          : viewport.WorldToClient(cursor).DistanceToSquared(viewport.WorldToClient(forwardPoint));
        var backwardDistance = viewport == null
          ? cursor.DistanceToSquared(backwardPoint)
          : viewport.WorldToClient(cursor).DistanceToSquared(viewport.WorldToClient(backwardPoint));
        return forwardDistance < backwardDistance ? 1
          : backwardDistance < forwardDistance ? -1 : previousDirection;
      }

      internal bool TryPoint(double distance, int direction, ProjectionMode project,
        out Point3d point, out double actualDistance)
      {
        if (!ValidDistance(distance))
        {
          point = Point3d.Unset;
          actualDistance = double.NaN;
          return false;
        }
        if (distance != _cachedDistance || project != _cachedProject)
        {
          _cachedDistance = distance;
          _cachedProject = project;
          _forwardPoint = Evaluate(distance, 1, project, out _forwardDistance);
          _backwardPoint = Evaluate(distance, -1, project, out _backwardDistance);
        }
        point = direction > 0 ? _forwardPoint : _backwardPoint;
        actualDistance = direction > 0 ? _forwardDistance : _backwardDistance;
        return point.IsValid;
      }

      private Point3d Evaluate(double distance, int direction, ProjectionMode project, out double actualDistance)
      {
        actualDistance = distance;
        if (distance == 0.0)
          return StartPoint;
        var length = _startLength + direction * (_curve.IsClosed ? distance % _totalLength : distance);
        if (_curve.IsClosed)
          length = (length % _totalLength + _totalLength) % _totalLength;
        else if (length < 0.0 || length > _totalLength)
        {
          var available = direction > 0 ? _totalLength - _startLength : _startLength;
          var end = direction > 0 ? _curve.PointAtEnd : _curve.PointAtStart;
          if (project == ProjectionMode.End)
          {
            actualDistance = available;
            return end;
          }
          if (project == ProjectionMode.Straight)
          {
            var tangent = _curve.TangentAt(direction > 0 ? _curve.Domain.T1 : _curve.Domain.T0);
            return tangent.Unitize() ? end + tangent * ((distance - available) * direction) : Point3d.Unset;
          }
          if (project == ProjectionMode.Smooth)
          {
            using var extended = _curve.Extend(direction > 0 ? CurveEnd.End : CurveEnd.Start,
              (distance - available) * SmoothExtensionPadding, CurveExtensionStyle.Smooth);
            if (extended == null)
              return Point3d.Unset;
            var fromExtendedStart = direction > 0 ? length
              : extended.GetLength() - _totalLength + length;
            return extended.LengthParameter(fromExtendedStart, out var t) ? extended.PointAt(t) : Point3d.Unset;
          }
          return Point3d.Unset;
        }
        if (length == 0.0)
          return _curve.PointAtStart;
        if (length == _totalLength)
          return _curve.PointAtEnd;
        return _curve.LengthParameter(length, out var parameter)
          ? _curve.PointAt(parameter) : Point3d.Unset;
      }
    }
  }
}
