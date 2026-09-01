using System;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools;

internal static class AnnotationTextTransform
{
  // Customizable annotation transformation constants.
  private const double HalfTurnRadians = Math.PI; // Text rotation in radians used to reverse annotation reading direction.
  private const double FullTurnRadians = Math.PI * 2.0; // Positive radians in one complete rotation, used for normalization.
  private const double DefaultDisplayDimensionScale = 1.0; // Positive fallback annotation display scale when Rhino cannot resolve a viewport scale.

  internal static bool FlipTextFrame(AnnotationBase annotation)
  {
    if (annotation is TextEntity text)
      return text.Transform(TextEntityFlipTransform(text, Transform.Identity));

    if (!TryCreateDefinitionRestorer(annotation, out var restoreDefinition))
      return false;

    var plane = annotation.Plane;
    if (!plane.IsValid)
      return false;

    var drawForward = annotation.DrawForward;
    var flip = Transform.Rotation(
      HalfTurnRadians, plane.YAxis, plane.Origin);
    if (!annotation.Transform(flip) || !restoreDefinition(annotation))
      return false;

    annotation.DrawForward = !drawForward;
    return annotation.IsValid;
  }

  internal static bool MakeMirroredTextReadable(
    RhinoDoc doc,
    AnnotationBase annotation)
  {
    if (annotation is TextEntity text)
    {
      if (!FlipTextFrame(text))
        return false;

      return ApplyTextDisplayOverrides(
        doc,
        text,
        textOrientation: TextOrientation.InPlane);
    }

    if (UsesScreenHorizontalDimensionText(annotation))
    {
      annotation.DrawForward = true;
      return ApplyTextDisplayOverrides(
        doc,
        annotation,
        drawForward: true);
    }

    return FlipTextFrame(annotation);
  }

  internal static bool RotateText(
    RhinoDoc doc,
    AnnotationBase annotation,
    double angleRadians)
  {
    if (annotation is TextEntity text)
    {
      var plane = text.Plane;
      return text.Transform(
        Transform.Rotation(angleRadians, plane.Normal, plane.Origin));
    }

    return RotateStyledText(doc, annotation, angleRadians);
  }

  internal static double ResolveDisplayDimensionScale(
    RhinoDoc doc,
    AnnotationBase annotation,
    RhinoViewport? viewport)
  {
    if (viewport == null)
      return ValidDimensionScale(annotation.DimensionScale);

    var parentStyleId = annotation.DimensionStyleId != Guid.Empty
      ? annotation.DimensionStyleId
      : doc.DimStyles.Current.Id;
    var parentStyle = doc.DimStyles.FindId(parentStyleId) ?? doc.DimStyles.Current;
    using var effectiveStyle = annotation.GetDimensionStyle(parentStyle);
    var scale = AnnotationBase.GetDimensionScale(
      doc,
      effectiveStyle ?? parentStyle,
      viewport);
    return ValidDimensionScale(scale);
  }

  internal static bool ApplyFixedDisplayTextHeight(
    RhinoDoc doc,
    TextEntity annotation,
    RhinoViewport? viewport,
    double targetHeight,
    double targetScale)
  {
    targetHeight = Math.Max(targetHeight, RhinoMath.ZeroTolerance);
    targetScale = ValidDimensionScale(targetScale);
    annotation.TextHeight = targetHeight;
    annotation.DimensionScale = targetScale;

    using var overrideStyle = CreateEffectiveOverrideStyle(doc, annotation);
    overrideStyle.TextHeight = targetHeight;
    overrideStyle.SetFieldOverride(DimensionStyle.Field.TextHeight);

    if (viewport != null)
    {
      var currentScale = AnnotationBase.GetDimensionScale(
        doc,
        overrideStyle,
        viewport);
      currentScale = ValidDimensionScale(currentScale);
      var styleScale = ValidDimensionScale(overrideStyle.DimensionScale);
      overrideStyle.DimensionScale = styleScale * targetScale / currentScale;
      overrideStyle.SetFieldOverride(DimensionStyle.Field.DimensionScale);
    }

    return annotation.SetOverrideDimStyle(overrideStyle);
  }

  internal static Transform TextEntityFlipTransform(
    TextEntity source,
    Transform initial)
  {
    using var text = source.Duplicate() as TextEntity;
    if (text == null || !text.Transform(initial))
      return Transform.Identity;

    var plane = text.Plane;
    var flip = Transform.Rotation(HalfTurnRadians, plane.XAxis, plane.Origin);
    if (!text.Transform(flip))
      return Transform.Identity;

    plane = text.Plane;
    var rotate = Transform.Rotation(HalfTurnRadians, plane.Normal, plane.Origin);
    return rotate * flip;
  }

  private static bool TryCreateDefinitionRestorer(
    AnnotationBase annotation,
    out Func<AnnotationBase, bool> restore)
  {
    var plane = annotation.Plane;
    restore = _ => false;
    if (!plane.IsValid)
      return false;

    switch (annotation)
    {
      case Leader leader:
      {
        var points = leader.Points3D;
        var alignment = leader.LeaderTextHorizontalAlignment;
        restore = candidate =>
          candidate is Leader transformed &&
          RestoreLeader(transformed, points, alignment);
        return true;
      }

      case LinearDimension linear:
      {
        var extension1 = ToWorld(plane, linear.ExtensionLine1End);
        var extension2 = ToWorld(plane, linear.ExtensionLine2End);
        var dimensionLine = ToWorld(plane, linear.DimensionLinePoint);
        var useDefaultText = linear.UseDefaultTextPoint;
        var textPosition = ToWorld(plane, linear.TextPosition);
        restore = candidate =>
          candidate is LinearDimension transformed &&
          RestoreLinearDimension(
            transformed,
            extension1,
            extension2,
            dimensionLine,
            useDefaultText,
            textPosition);
        return true;
      }

      case AngularDimension angular:
      {
        var center = ToWorld(plane, angular.CenterPoint);
        var definition1 = ToWorld(plane, angular.DefPoint1);
        var definition2 = ToWorld(plane, angular.DefPoint2);
        var dimensionLine = ToWorld(plane, angular.DimlinePoint);
        var useDefaultText = angular.UseDefaultTextPoint;
        var textPosition = ToWorld(plane, angular.TextPosition);
        restore = candidate =>
          candidate is AngularDimension transformed &&
          RestoreAngularDimension(
            transformed,
            center,
            definition1,
            definition2,
            dimensionLine,
            useDefaultText,
            textPosition);
        return true;
      }

      case RadialDimension radial:
      {
        var center = ToWorld(plane, radial.CenterPoint);
        var radius = ToWorld(plane, radial.RadiusPoint);
        var dimensionLine = ToWorld(plane, radial.DimlinePoint);
        var useDefaultText = radial.UseDefaultTextPoint;
        var textPosition = ToWorld(plane, radial.TextPosition);
        var alignment = radial.LeaderTextHorizontalAlignment;
        restore = candidate =>
          candidate is RadialDimension transformed &&
          RestoreRadialDimension(
            transformed,
            center,
            radius,
            dimensionLine,
            useDefaultText,
            textPosition,
            alignment);
        return true;
      }

      case OrdinateDimension ordinate:
      {
        var definition = ToWorld(plane, ordinate.DefPoint);
        var leader = ToWorld(plane, ordinate.LeaderPoint);
        var useDefaultText = ordinate.UseDefaultTextPoint;
        var textPosition = ToWorld(plane, ordinate.TextPosition);
        restore = candidate =>
          candidate is OrdinateDimension transformed &&
          RestoreOrdinateDimension(
            transformed,
            definition,
            leader,
            useDefaultText,
            textPosition);
        return true;
      }

      default:
        return false;
    }
  }

  private static bool RestoreLeader(
    Leader leader,
    Point3d[] points,
    TextHorizontalAlignment alignment)
  {
    leader.Points3D = points;
    leader.LeaderTextHorizontalAlignment =
      MirrorHorizontalAlignment(alignment);
    return leader.IsValid;
  }

  private static bool RestoreLinearDimension(
    LinearDimension dimension,
    Point3d extension1,
    Point3d extension2,
    Point3d dimensionLine,
    bool useDefaultText,
    Point3d textPosition)
  {
    var plane = dimension.Plane;
    if (!TryToPlane(plane, extension1, out var extension1Local) ||
        !TryToPlane(plane, extension2, out var extension2Local) ||
        !TryToPlane(plane, dimensionLine, out var dimensionLineLocal) ||
        (!useDefaultText &&
         !TryToPlane(plane, textPosition, out _)))
      return false;

    dimension.SetLocations(
      extension1Local, extension2Local, dimensionLineLocal);
    return RestoreTextPosition(
      dimension, useDefaultText, textPosition);
  }

  private static bool RestoreAngularDimension(
    AngularDimension dimension,
    Point3d center,
    Point3d definition1,
    Point3d definition2,
    Point3d dimensionLine,
    bool useDefaultText,
    Point3d textPosition)
  {
    var plane = dimension.Plane;
    if (!TryToPlane(plane, center, out var centerLocal) ||
        !TryToPlane(plane, definition1, out var definition1Local) ||
        !TryToPlane(plane, definition2, out var definition2Local) ||
        !TryToPlane(plane, dimensionLine, out var dimensionLineLocal))
      return false;

    dimension.CenterPoint = centerLocal;
    dimension.DefPoint1 = definition1Local;
    dimension.DefPoint2 = definition2Local;
    dimension.DimlinePoint = dimensionLineLocal;
    return RestoreTextPosition(
      dimension, useDefaultText, textPosition);
  }

  private static bool RestoreRadialDimension(
    RadialDimension dimension,
    Point3d center,
    Point3d radius,
    Point3d dimensionLine,
    bool useDefaultText,
    Point3d textPosition,
    TextHorizontalAlignment alignment)
  {
    var plane = dimension.Plane;
    if (!TryToPlane(plane, center, out var centerLocal) ||
        !TryToPlane(plane, radius, out var radiusLocal) ||
        !TryToPlane(plane, dimensionLine, out var dimensionLineLocal))
      return false;

    dimension.CenterPoint = centerLocal;
    dimension.RadiusPoint = radiusLocal;
    dimension.DimlinePoint = dimensionLineLocal;
    dimension.LeaderTextHorizontalAlignment =
      MirrorHorizontalAlignment(alignment);
    return RestoreTextPosition(
      dimension, useDefaultText, textPosition);
  }

  private static bool RestoreOrdinateDimension(
    OrdinateDimension dimension,
    Point3d definition,
    Point3d leader,
    bool useDefaultText,
    Point3d textPosition)
  {
    var plane = dimension.Plane;
    if (!TryToPlane(plane, definition, out var definitionLocal) ||
        !TryToPlane(plane, leader, out var leaderLocal))
      return false;

    dimension.DefPoint = definitionLocal;
    dimension.LeaderPoint = leaderLocal;
    return RestoreTextPosition(
      dimension, useDefaultText, textPosition);
  }

  private static bool RestoreTextPosition(
    Dimension dimension,
    bool useDefaultText,
    Point3d textPosition)
  {
    if (!useDefaultText)
    {
      if (!TryToPlane(
            dimension.Plane, textPosition, out var textPositionLocal))
        return false;
      dimension.TextPosition = textPositionLocal;
    }

    dimension.UseDefaultTextPoint = useDefaultText;
    return dimension.IsValid;
  }

  private static TextHorizontalAlignment MirrorHorizontalAlignment(
    TextHorizontalAlignment alignment) =>
    alignment switch
    {
      TextHorizontalAlignment.Left => TextHorizontalAlignment.Right,
      TextHorizontalAlignment.Right => TextHorizontalAlignment.Left,
      _ => alignment,
    };

  private static bool UsesScreenHorizontalDimensionText(
    AnnotationBase annotation) =>
    annotation is Dimension dimension &&
    (dimension.TextAngleType ==
       DimensionStyle.LeaderContentAngleStyle.Horizontal ||
     dimension.TextOrientation == TextOrientation.InView);

  private static bool ApplyTextDisplayOverrides(
    RhinoDoc doc,
    AnnotationBase annotation,
    TextOrientation? textOrientation = null,
    bool? drawForward = null)
  {
    using var overrideStyle = CreateEffectiveOverrideStyle(doc, annotation);
    if (textOrientation.HasValue)
    {
      overrideStyle.TextOrientation = textOrientation.Value;
      overrideStyle.SetFieldOverride(DimensionStyle.Field.TextOrientation);
      if (annotation is TextEntity text)
        text.TextOrientation = textOrientation.Value;
    }

    if (drawForward.HasValue)
    {
      overrideStyle.DrawForward = drawForward.Value;
      overrideStyle.SetFieldOverride(DimensionStyle.Field.DrawForward);
      annotation.DrawForward = drawForward.Value;
    }

    return annotation.SetOverrideDimStyle(overrideStyle) && annotation.IsValid;
  }

  private static Point3d ToWorld(Plane plane, Point2d point) =>
    plane.PointAt(point.X, point.Y);

  private static bool TryToPlane(
    Plane plane,
    Point3d point,
    out Point2d mapped)
  {
    mapped = Point2d.Unset;
    if (!plane.ClosestParameter(point, out var x, out var y))
      return false;
    mapped = new Point2d(x, y);
    return mapped.IsValid;
  }

  private static bool RotateStyledText(
    RhinoDoc doc,
    AnnotationBase annotation,
    double angleRadians)
  {
    using var overrideStyle = CreateEffectiveOverrideStyle(doc, annotation);
    overrideStyle.DrawForward = false;
    overrideStyle.SetFieldOverride(DimensionStyle.Field.DrawForward);

    switch (annotation)
    {
      case RadialDimension:
        overrideStyle.TextRotation = NormalizeRadians(
          overrideStyle.TextRotation + angleRadians);
        overrideStyle.DimRadialTextAngleType =
          DimensionStyle.LeaderContentAngleStyle.Rotated;
        overrideStyle.SetFieldOverride(DimensionStyle.Field.TextRotation);
        overrideStyle.SetFieldOverride(DimensionStyle.Field.DimRadialTextAngleStyle);
        break;

      case Dimension:
        overrideStyle.TextRotation = NormalizeRadians(
          overrideStyle.TextRotation + angleRadians);
        overrideStyle.DimTextAngleType =
          DimensionStyle.LeaderContentAngleStyle.Rotated;
        overrideStyle.SetFieldOverride(DimensionStyle.Field.TextRotation);
        overrideStyle.SetFieldOverride(DimensionStyle.Field.DimTextAngleStyle);
        break;

      case Leader:
        overrideStyle.LeaderTextRotationRadians = NormalizeRadians(
          overrideStyle.LeaderTextRotationRadians + angleRadians);
        overrideStyle.LeaderContentAngleType =
          DimensionStyle.LeaderContentAngleStyle.Rotated;
        overrideStyle.SetFieldOverride(DimensionStyle.Field.LeaderContentAngle);
        overrideStyle.SetFieldOverride(DimensionStyle.Field.LeaderContentAngleStyle);
        break;

      default:
        annotation.TextRotationRadians = NormalizeRadians(
          annotation.TextRotationRadians + angleRadians);
        return true;
    }

    if (!annotation.SetOverrideDimStyle(overrideStyle))
      return false;

    return true;
  }

  private static DimensionStyle CreateEffectiveOverrideStyle(
    RhinoDoc doc,
    AnnotationBase annotation)
  {
    var parentStyleId = annotation.DimensionStyleId != Guid.Empty
      ? annotation.DimensionStyleId
      : doc.DimStyles.Current.Id;
    var parentStyle = doc.DimStyles.FindId(parentStyleId) ?? doc.DimStyles.Current;
    using var effectiveStyle = annotation.GetDimensionStyle(parentStyle);
    var overrideStyle =
      (effectiveStyle ?? annotation.DimensionStyle).Duplicate();
    overrideStyle.Id = Guid.Empty;
    overrideStyle.Index = -1;
    overrideStyle.ParentId = parentStyle.Id;
    return overrideStyle;
  }

  private static double NormalizeRadians(double angleRadians)
  {
    angleRadians %= FullTurnRadians;
    return angleRadians < 0.0
      ? angleRadians + FullTurnRadians
      : angleRadians;
  }

  private static double ValidDimensionScale(double scale) =>
    double.IsFinite(scale) && scale > RhinoMath.ZeroTolerance
      ? scale
      : DefaultDisplayDimensionScale;
}
