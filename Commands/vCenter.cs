using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Color = System.Drawing.Color;
using RhinoPoint = Rhino.Geometry.Point;

namespace vTools.Commands;

/// <summary>
/// Places a point at a configurable center of selected geometry.
/// </summary>
public sealed class vCenter : Command
{
  private const string OptionsSectionName = "vCenter";
  private const string MethodKey = "method";

  // Option defaults
  private const CenterMethod DefaultMethod = CenterMethod.BoundingBox; // CenterMethod enum: BoundingBox, Mass, or Objects.

  private static readonly string[] MethodNames = ["BoundingBox", "Mass", "Objects"]; // Command option names in method-index order.
  private static CenterMethod _method = DefaultMethod;

  private enum CenterMethod
  {
    BoundingBox,
    Mass,
    Objects
  }

  private readonly record struct CenterSample(Point3d Center, double Weight);

  public override string EnglishName => "vCenter";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadPersistedMethod();

    var result = GetGeometry(doc, out var objectIds, out var center, out var method);
    if (result != Result.Success)
      return result;

    _method = method;
    SavePersistedMethod();

    if (!center.IsValid)
    {
      RhinoApp.WriteLine("vCenter: could not calculate a center for the selection.");
      return Result.Failure;
    }

    var pointId = doc.Objects.AddPoint(center);
    if (pointId == Guid.Empty)
    {
      RhinoApp.WriteLine("vCenter: failed to add the center point.");
      return Result.Failure;
    }

    Log.Write(
      "vCenter",
      $"created point={FormatPoint(center)} method={MethodNames[(int)method]} objects={objectIds.Count}");
    doc.Views.Redraw();
    return Result.Success;
  }

  private static Result GetGeometry(
    RhinoDoc doc,
    out List<Guid> objectIds,
    out Point3d center,
    out CenterMethod method)
  {
    objectIds = [];
    center = Point3d.Unset;
    method = _method;
    var currentMethod = method;

    using var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt("Select geometry for center point");
    getter.GeometryFilter = ObjectType.AnyObject;
    getter.SetCustomGeometryFilter(
      (rhinoObject, geometry, _) =>
        geometry != null &&
        rhinoObject.ObjectType != ObjectType.Grip &&
        rhinoObject.ObjectType != ObjectType.Light);
    getter.AcceptNothing(true);
    getter.AcceptString(true);
    getter.EnablePreSelect(true, true);
    getter.AlreadySelectedObjectSelect = true;
    getter.SubObjectSelect = false;
    getter.GroupSelect = true;
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;

    var preview = new CenterPreviewConduit();
    var preselectedWaitingForConfirmation = false;
    var debounceTimer = new System.Windows.Forms.Timer { Interval = 75 };

    void UpdatePreview()
    {
      var selectedIds = CollectSelectedObjectIds(doc);
      preview.Center = TryCalculateCenter(doc, selectedIds, currentMethod, out var previewCenter)
        ? previewCenter
        : Point3d.Unset;
      doc.Views.Redraw();
    }

    debounceTimer.Tick += (_, _) =>
    {
      debounceTimer.Stop();
      UpdatePreview();
    };

    EventHandler<RhinoObjectSelectionEventArgs> onSelectionChanged = (_, _) =>
    {
      debounceTimer.Stop();
      debounceTimer.Start();
    };

    RhinoDoc.SelectObjects += onSelectionChanged;
    RhinoDoc.DeselectObjects += onSelectionChanged;
    preview.Enabled = true;
    UpdatePreview();

    try
    {
      while (true)
      {
        getter.ClearCommandOptions();
        var methodOptionIndex = getter.AddOptionList("Method", MethodNames, (int)currentMethod);

        var getResult = getter.GetMultiple(1, 0);
        debounceTimer.Stop();
        UpdatePreview();

        if (getter.CommandResult() != Result.Success)
          return getter.CommandResult();

        if (getResult == GetResult.Option)
        {
          var option = getter.Option();
          if (option != null && option.Index == methodOptionIndex)
          {
            currentMethod = MethodFromIndex(option.CurrentListOptionIndex);
            _method = currentMethod;
            SavePersistedMethod();
            UpdatePreview();
          }

          continue;
        }

        if (getResult == GetResult.String)
        {
          if (TryParseMethod(getter.StringResult(), out var directMethod))
          {
            currentMethod = directMethod;
            _method = currentMethod;
            SavePersistedMethod();
            UpdatePreview();
          }
          else
          {
            RhinoApp.WriteLine(
              "vCenter: enter BoundingBox, Mass, or Objects to change the center method.");
          }

          continue;
        }

        if (getResult == GetResult.Object && getter.ObjectCount > 0)
        {
          if (getter.ObjectsWerePreselected && !preselectedWaitingForConfirmation)
          {
            preselectedWaitingForConfirmation = true;
            getter.EnablePreSelect(false, true);
            continue;
          }

          objectIds = CollectSelectedObjectIds(doc);
          break;
        }

        if (getResult == GetResult.Nothing)
        {
          objectIds = CollectSelectedObjectIds(doc);
          break;
        }

        return Result.Cancel;
      }

      if (objectIds.Count == 0)
        return Result.Nothing;

      method = currentMethod;
      if (!TryCalculateCenter(doc, objectIds, currentMethod, out center))
      {
        RhinoApp.WriteLine("vCenter: selected objects do not contain usable geometry.");
        return Result.Failure;
      }

      return Result.Success;
    }
    finally
    {
      RhinoDoc.SelectObjects -= onSelectionChanged;
      RhinoDoc.DeselectObjects -= onSelectionChanged;
      debounceTimer.Stop();
      debounceTimer.Dispose();
      preview.Center = Point3d.Unset;
      preview.Enabled = false;
      doc.Views.Redraw();
    }
  }

  private static List<Guid> CollectSelectedObjectIds(RhinoDoc doc)
  {
    return doc.Objects
      .GetSelectedObjects(false, false)
      .Where(obj => obj?.Geometry != null)
      .Select(obj => obj.Id)
      .Where(id => id != Guid.Empty)
      .Distinct()
      .ToList();
  }

  private static bool TryCalculateCenter(
    RhinoDoc doc,
    IEnumerable<Guid> objectIds,
    CenterMethod method,
    out Point3d center)
  {
    var geometries = objectIds
      .Select(id => doc.Objects.FindId(id)?.Geometry)
      .Where(geometry => geometry != null)
      .Cast<GeometryBase>()
      .ToList();

    if (geometries.Count == 0)
    {
      center = Point3d.Unset;
      return false;
    }

    return method switch
    {
      CenterMethod.Mass => TryMassCenter(geometries, out center),
      CenterMethod.Objects => TryObjectAverageCenter(geometries, out center),
      _ => TryBoundingBoxCenter(geometries, out center)
    };
  }

  private static bool TryBoundingBoxCenter(IEnumerable<GeometryBase> geometries, out Point3d center)
  {
    var bounds = BoundingBox.Unset;
    foreach (var geometry in geometries)
    {
      var geometryBounds = geometry.GetBoundingBox(true);
      if (geometryBounds.IsValid)
        bounds.Union(geometryBounds);
    }

    center = bounds.IsValid ? bounds.Center : Point3d.Unset;
    return center.IsValid;
  }

  private static bool TryMassCenter(IReadOnlyList<GeometryBase> geometries, out Point3d center)
  {
    var volumes = new List<CenterSample>();
    var areas = new List<CenterSample>();
    var lengths = new List<CenterSample>();
    var points = new List<CenterSample>();
    var fallbacks = new List<CenterSample>();

    foreach (var geometry in geometries)
    {
      if (TryVolumeSample(geometry, out var volume))
      {
        volumes.Add(volume);
        continue;
      }

      if (TryAreaSample(geometry, out var area))
      {
        areas.Add(area);
        continue;
      }

      if (TryLengthSample(geometry, out var length))
      {
        lengths.Add(length);
        continue;
      }

      if (geometry is RhinoPoint point && point.Location.IsValid)
      {
        points.Add(new CenterSample(point.Location, 1.0));
        continue;
      }

      var bounds = geometry.GetBoundingBox(true);
      if (bounds.IsValid)
        fallbacks.Add(new CenterSample(bounds.Center, 1.0));
    }

    if (volumes.Count > 0)
      return TryWeightedCenter(volumes, out center);
    if (areas.Count > 0)
      return TryWeightedCenter(areas, out center);
    if (lengths.Count > 0)
      return TryWeightedCenter(lengths, out center);
    if (points.Count > 0)
      return TryWeightedCenter(points, out center);

    return TryWeightedCenter(fallbacks, out center);
  }

  private static bool TryObjectAverageCenter(
    IEnumerable<GeometryBase> geometries,
    out Point3d center)
  {
    var samples = new List<CenterSample>();
    foreach (var geometry in geometries)
    {
      if (TryNaturalCenter(geometry, out var objectCenter))
        samples.Add(new CenterSample(objectCenter, 1.0));
    }

    return TryWeightedCenter(samples, out center);
  }

  private static bool TryNaturalCenter(GeometryBase geometry, out Point3d center)
  {
    if (TryVolumeSample(geometry, out var volume))
    {
      center = volume.Center;
      return true;
    }

    if (TryAreaSample(geometry, out var area))
    {
      center = area.Center;
      return true;
    }

    if (TryLengthSample(geometry, out var length))
    {
      center = length.Center;
      return true;
    }

    if (geometry is RhinoPoint point && point.Location.IsValid)
    {
      center = point.Location;
      return true;
    }

    var bounds = geometry.GetBoundingBox(true);
    center = bounds.IsValid ? bounds.Center : Point3d.Unset;
    return center.IsValid;
  }

  private static bool TryVolumeSample(GeometryBase geometry, out CenterSample sample)
  {
    VolumeMassProperties? properties = null;
    Brep? convertedBrep = null;

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
          convertedBrep = extrusion.ToBrep();
          properties = convertedBrep != null
            ? VolumeMassProperties.Compute(convertedBrep)
            : null;
          break;
        case SubD subD:
          convertedBrep = subD.ToBrep();
          if (convertedBrep?.IsSolid == true)
            properties = VolumeMassProperties.Compute(convertedBrep);
          break;
      }

      var weight = Math.Abs(properties?.Volume ?? 0.0);
      if (properties == null || !properties.Centroid.IsValid || !IsUsableWeight(weight))
      {
        sample = default;
        return false;
      }

      sample = new CenterSample(properties.Centroid, weight);
      return true;
    }
    catch
    {
      sample = default;
      return false;
    }
    finally
    {
      properties?.Dispose();
      convertedBrep?.Dispose();
    }
  }

  private static bool TryAreaSample(GeometryBase geometry, out CenterSample sample)
  {
    AreaMassProperties? properties = null;
    Brep? convertedBrep = null;

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
          convertedBrep = extrusion.ToBrep();
          properties = convertedBrep != null
            ? AreaMassProperties.Compute(convertedBrep)
            : null;
          break;
        case SubD subD:
          convertedBrep = subD.ToBrep();
          properties = convertedBrep != null
            ? AreaMassProperties.Compute(convertedBrep)
            : null;
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

      var weight = Math.Abs(properties?.Area ?? 0.0);
      if (properties == null || !properties.Centroid.IsValid || !IsUsableWeight(weight))
      {
        sample = default;
        return false;
      }

      sample = new CenterSample(properties.Centroid, weight);
      return true;
    }
    catch
    {
      sample = default;
      return false;
    }
    finally
    {
      properties?.Dispose();
      convertedBrep?.Dispose();
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
      var weight = Math.Abs(properties?.Length ?? 0.0);
      if (properties == null || !properties.Centroid.IsValid || !IsUsableWeight(weight))
      {
        sample = default;
        return false;
      }

      sample = new CenterSample(properties.Centroid, weight);
      return true;
    }
    catch
    {
      sample = default;
      return false;
    }
  }

  private static bool TryWeightedCenter(IEnumerable<CenterSample> samples, out Point3d center)
  {
    var x = 0.0;
    var y = 0.0;
    var z = 0.0;
    var totalWeight = 0.0;

    foreach (var sample in samples)
    {
      if (!sample.Center.IsValid || !IsUsableWeight(sample.Weight))
        continue;

      x += sample.Center.X * sample.Weight;
      y += sample.Center.Y * sample.Weight;
      z += sample.Center.Z * sample.Weight;
      totalWeight += sample.Weight;
    }

    if (!IsUsableWeight(totalWeight))
    {
      center = Point3d.Unset;
      return false;
    }

    center = new Point3d(x / totalWeight, y / totalWeight, z / totalWeight);
    return center.IsValid;
  }

  private static bool IsUsableWeight(double weight)
  {
    return double.IsFinite(weight) && weight > RhinoMath.ZeroTolerance;
  }

  private static void LoadPersistedMethod()
  {
    _method = ToolsOptionStore.Read(
      OptionsSectionName,
      section =>
      {
        if (!ToolsOptionStore.TryGetString(section, MethodKey, out var savedMethod))
          return CenterMethod.BoundingBox;

        return ParseMethod(savedMethod);
      });
  }

  private static void SavePersistedMethod()
  {
    _ = ToolsOptionStore.Update(
      OptionsSectionName,
      section => section[MethodKey] = MethodNames[(int)_method]);
  }

  private static CenterMethod ParseMethod(string value)
  {
    return TryParseMethod(value, out var method) ? method : CenterMethod.BoundingBox;
  }

  private static bool TryParseMethod(string? value, out CenterMethod method)
  {
    method = CenterMethod.BoundingBox;
    if (string.IsNullOrWhiteSpace(value))
      return false;

    var input = value.Trim();
    var matches = MethodNames
      .Select((name, index) => (name, index))
      .Where(item => item.name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
      .ToList();
    if (matches.Count != 1)
      return false;

    method = (CenterMethod)matches[0].index;
    return true;
  }

  private static CenterMethod MethodFromIndex(int index)
  {
    return index >= 0 && index < MethodNames.Length
      ? (CenterMethod)index
      : CenterMethod.BoundingBox;
  }

  private static string FormatPoint(Point3d point)
  {
    return $"({point.X:F6},{point.Y:F6},{point.Z:F6})";
  }

  private sealed class CenterPreviewConduit : DisplayConduit
  {
    internal Point3d Center = Point3d.Unset;

    protected override void DrawOverlay(DrawEventArgs e)
    {
      if (!Center.IsValid)
        return;

      var radius = 4;
      try
      {
        radius = Math.Max(2, (int)Math.Round(e.Display.DisplayPipelineAttributes.PointRadius));
      }
      catch
      {
      }

      e.Display.DrawPoint(Center, PointStyle.RoundSimple, radius + 2, Color.Black);
      e.Display.DrawPoint(Center, PointStyle.RoundSimple, radius + 1, Color.White);
      e.Display.DrawPoint(Center, PointStyle.RoundSimple, radius, Color.FromArgb(0, 220, 120));
    }
  }
}
