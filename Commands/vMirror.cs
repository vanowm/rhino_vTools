using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vMirror : Command
{
  const string OptionsSection = "vMirror";
  const string CopyKey = "copy";
  const string FlipTextKey = "flipText";

  // Option defaults
  const bool DefaultCopy = true; // true mirrors copies and keeps originals; false moves originals across the mirror plane.
  const bool DefaultFlipText = true; // true keeps mirrored text readable; false mirrors text geometry literally.

  public override string EnglishName => "vMirror";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var (copy, flipText) = LoadOptions();
    if (!TrySelectObjects(doc, ref copy, ref flipText, out var objectIds))
      return Result.Cancel;

    doc.Objects.UnselectAll();
    doc.Views.Redraw();

    Plane constructionPlane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()
      ?? Plane.WorldXY;
    using var preview = new MirrorPreviewConduit(doc, objectIds);
    if (!TryGetMirrorPlane(doc, objectIds, constructionPlane, preview,
      ref copy, ref flipText, out var mirrorPlane))
      return Result.Cancel;

    Transform mirror = Transform.Mirror(mirrorPlane);
    if (!mirror.IsValid)
    {
      RhinoApp.WriteLine("vMirror: could not create the mirror transform.");
      return Result.Failure;
    }

    var outputIds = new List<Guid>();
    int failed = 0;
    foreach (var objectId in objectIds)
    {
      var source = doc.Objects.FindId(objectId);
      if (source == null)
      {
        failed++;
        continue;
      }

      bool isText = source.Geometry is TextEntity;
      Guid outputId = doc.Objects.Transform(objectId, mirror, deleteOriginal: !copy);
      if (outputId == Guid.Empty)
      {
        failed++;
        continue;
      }

      if (flipText && isText)
        outputId = FlipMirroredText(doc, outputId);
      if (outputId != Guid.Empty)
        outputIds.Add(outputId);
      else
        failed++;
    }

    foreach (var outputId in outputIds)
      doc.Objects.Select(outputId, true);
    doc.Views.Redraw();
    Log.Write("vMirror",
      $"mirrored={outputIds.Count} failed={failed} copy={copy} flipText={flipText}");
    return outputIds.Count > 0 ? Result.Success : Result.Failure;
  }

  static (bool Copy, bool FlipText) LoadOptions() =>
    ToolsOptionStore.Read(
      OptionsSection,
      section =>
      {
        bool copy = DefaultCopy;
        bool flipText = DefaultFlipText;
        if (ToolsOptionStore.TryGetBool(section, CopyKey, out var savedCopy))
          copy = savedCopy;
        if (ToolsOptionStore.TryGetBool(section, FlipTextKey, out var savedFlip))
          flipText = savedFlip;
        return (copy, flipText);
      });

  static void SaveOptions(bool copy, bool flipText)
  {
    if (!ToolsOptionStore.Update(OptionsSection, section =>
      {
        section[CopyKey] = copy;
        section[FlipTextKey] = flipText;
      }))
      Log.Write("vMirror", $"could not save options: {ToolsOptionStore.LastError}");
  }

  static void ReadToggles(
    OptionToggle copyToggle, OptionToggle flipToggle,
    ref bool copy, ref bool flipText)
  {
    bool nextCopy = copyToggle.CurrentValue;
    bool nextFlip = flipToggle.CurrentValue;
    if (nextCopy == copy && nextFlip == flipText)
      return;
    copy = nextCopy;
    flipText = nextFlip;
    SaveOptions(copy, flipText);
  }

  static bool TrySelectObjects(
    RhinoDoc doc, ref bool copy, ref bool flipText, out List<Guid> objectIds)
  {
    objectIds = [];
    while (true)
    {
      var get = new GetObject();
      get.SetCommandPrompt("Select objects to mirror");
      get.GeometryFilter = ObjectType.AnyObject;
      get.GroupSelect = true;
      get.SubObjectSelect = false;
      get.EnablePreSelect(true, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      get.AddOptionToggle("Copy", ref copyToggle);
      get.AddOptionToggle("FlipText", ref flipToggle);

      var result = get.GetMultiple(1, 0);
      ReadToggles(copyToggle, flipToggle, ref copy, ref flipText);
      if (result == GetResult.Option)
        continue;
      if (result != GetResult.Object || get.CommandResult() != Result.Success)
        return false;

      objectIds = Enumerable.Range(0, get.ObjectCount)
        .Select(index => get.Object(index).ObjectId)
        .Where(id => id != Guid.Empty)
        .Distinct()
        .ToList();
      return objectIds.Count > 0;
    }
  }

  static bool TryGetMirrorPlane(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, Plane constructionPlane,
    MirrorPreviewConduit preview, ref bool copy, ref bool flipText,
    out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt("Start of mirror plane");
      int threePointOption = get.AddOption("3Point");
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      get.AddOptionToggle("Copy", ref copyToggle);
      int xAxisOption = get.AddOption("XAxis");
      int yAxisOption = get.AddOption("YAxis");
      int zAxisOption = get.AddOption("ZAxis");
      int objectOption = get.AddOption("Object");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      get.AddOptionToggle("FlipText", ref flipToggle);

      var result = get.Get();
      ReadToggles(copyToggle, flipToggle, ref copy, ref flipText);
      if (result == GetResult.Option)
      {
        int option = get.OptionIndex();
        if (option == threePointOption)
          return TryGetThreePointPlane(
            doc, objectIds, preview, ref copy, ref flipText, out mirrorPlane);
        if (option == xAxisOption)
        {
          mirrorPlane = new Plane(
            constructionPlane.Origin, constructionPlane.XAxis, constructionPlane.Normal);
          return mirrorPlane.IsValid;
        }
        if (option == yAxisOption)
        {
          mirrorPlane = new Plane(
            constructionPlane.Origin, constructionPlane.YAxis, constructionPlane.Normal);
          return mirrorPlane.IsValid;
        }
        if (option == zAxisOption)
        {
          mirrorPlane = constructionPlane;
          return mirrorPlane.IsValid;
        }
        if (option == objectOption)
          return TryGetObjectPlane(doc, ref copy, ref flipText, out mirrorPlane);
        continue;
      }
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
        return false;

      Point3d firstPoint = get.Point();
      return TryGetMirrorEnd(doc, objectIds, constructionPlane, preview, firstPoint,
        ref copy, ref flipText, out mirrorPlane);
    }
  }

  static bool TryGetMirrorEnd(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, Plane constructionPlane,
    MirrorPreviewConduit preview, Point3d firstPoint,
    ref bool copy, ref bool flipText, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt("End of mirror plane");
      get.SetBasePoint(firstPoint, true);
      get.DrawLineFromPoint(firstPoint, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      get.AddOptionToggle("Copy", ref copyToggle);
      get.AddOptionToggle("FlipText", ref flipToggle);
      preview.SetGhostOriginals(!copy);
      get.DynamicDraw += (_, e) =>
      {
        if (!TryTwoPointPlane(
          firstPoint, e.CurrentPoint, constructionPlane, out var dynamicPlane))
          return;
        preview.DrawMirrored(
          e.Display, Transform.Mirror(dynamicPlane), flipToggle.CurrentValue);
      };

      preview.Enabled = true;
      GetResult result;
      try { result = get.Get(); }
      finally
      {
        preview.Enabled = false;
        doc.Views.Redraw();
      }
      ReadToggles(copyToggle, flipToggle, ref copy, ref flipText);
      if (result == GetResult.Option)
        continue;
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
        return false;
      if (TryTwoPointPlane(firstPoint, get.Point(), constructionPlane, out mirrorPlane))
        return true;
      RhinoApp.WriteLine("vMirror: mirror axis is too short.");
    }
  }

  static bool TryTwoPointPlane(
    Point3d firstPoint, Point3d secondPoint, Plane constructionPlane,
    out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    Vector3d axis = secondPoint - firstPoint;
    if (!axis.Unitize())
      return false;
    mirrorPlane = new Plane(firstPoint, axis, constructionPlane.Normal);
    return mirrorPlane.IsValid;
  }

  static bool TryGetThreePointPlane(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, MirrorPreviewConduit preview,
    ref bool copy, ref bool flipText, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    if (!TryGetPlainPoint("First point of mirror plane", ref copy, ref flipText, out var first))
      return false;
    if (!TryGetPlainPoint("Second point of mirror plane", ref copy, ref flipText, out var second))
      return false;

    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt("Third point of mirror plane");
      get.SetBasePoint(second, true);
      get.DrawLineFromPoint(second, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      get.AddOptionToggle("Copy", ref copyToggle);
      get.AddOptionToggle("FlipText", ref flipToggle);
      preview.SetGhostOriginals(!copy);
      get.DynamicDraw += (_, e) =>
      {
        var dynamicPlane = new Plane(first, second, e.CurrentPoint);
        if (!dynamicPlane.IsValid)
          return;
        preview.DrawMirrored(
          e.Display, Transform.Mirror(dynamicPlane), flipToggle.CurrentValue);
      };

      preview.Enabled = true;
      GetResult result;
      try { result = get.Get(); }
      finally
      {
        preview.Enabled = false;
        doc.Views.Redraw();
      }
      ReadToggles(copyToggle, flipToggle, ref copy, ref flipText);
      if (result == GetResult.Option)
        continue;
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
        return false;
      mirrorPlane = new Plane(first, second, get.Point());
      if (mirrorPlane.IsValid)
        return true;
      RhinoApp.WriteLine("vMirror: the three points do not define a plane.");
    }
  }

  static bool TryGetPlainPoint(
    string prompt, ref bool copy, ref bool flipText, out Point3d point)
  {
    point = Point3d.Unset;
    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt(prompt);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      get.AddOptionToggle("Copy", ref copyToggle);
      get.AddOptionToggle("FlipText", ref flipToggle);
      var result = get.Get();
      ReadToggles(copyToggle, flipToggle, ref copy, ref flipText);
      if (result == GetResult.Option)
        continue;
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
        return false;
      point = get.Point();
      return point.IsValid;
    }
  }

  static bool TryGetObjectPlane(
    RhinoDoc doc, ref bool copy, ref bool flipText, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    while (true)
    {
      var get = new GetObject();
      get.SetCommandPrompt("Select planar surface or face for mirror plane");
      get.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
      get.SubObjectSelect = true;
      get.GroupSelect = false;
      get.EnablePreSelect(false, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      get.AddOptionToggle("Copy", ref copyToggle);
      get.AddOptionToggle("FlipText", ref flipToggle);
      var result = get.Get();
      ReadToggles(copyToggle, flipToggle, ref copy, ref flipText);
      if (result == GetResult.Option)
        continue;
      if (result != GetResult.Object || get.CommandResult() != Result.Success)
        return false;

      ObjRef objRef = get.Object(0);
      var face = objRef.Face();
      if (face != null && face.TryGetPlane(out mirrorPlane, doc.ModelAbsoluteTolerance))
        return true;
      var surface = objRef.Surface();
      if (surface != null && surface.TryGetPlane(out mirrorPlane, doc.ModelAbsoluteTolerance))
        return true;
      if (objRef.Brep() is { Faces.Count: 1 } brep &&
          brep.Faces[0].TryGetPlane(out mirrorPlane, doc.ModelAbsoluteTolerance))
        return true;
      RhinoApp.WriteLine("vMirror: select a planar surface or face.");
    }
  }

  static Guid FlipMirroredText(RhinoDoc doc, Guid objectId)
  {
    var obj = doc.Objects.FindId(objectId);
    if (obj?.Geometry is not TextEntity text)
      return objectId;
    Transform flip = TextFlipTransform(text, Transform.Identity);
    return doc.Objects.Transform(objectId, flip, deleteOriginal: true);
  }

  static Transform TextFlipTransform(TextEntity source, Transform initial)
  {
    var text = source.Duplicate() as TextEntity;
    if (text == null || !text.Transform(initial))
      return Transform.Identity;
    Plane plane = text.Plane;
    Transform flip = Transform.Rotation(Math.PI, plane.XAxis, plane.Origin);
    text.Transform(flip);
    plane = text.Plane;
    Transform rotate = Transform.Rotation(Math.PI, plane.Normal, plane.Origin);
    return rotate * flip;
  }

  sealed class MirrorPreviewConduit : DisplayConduit, IDisposable
  {
    static readonly Color SelectedColor = // Rhino selection color used for mirrored preview geometry.
      Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
    static readonly Color GhostColor = Color.FromArgb(145, 145, 145); // Neutral color for originals ghosted in move mode.
    readonly RhinoDoc _doc;
    readonly HashSet<Guid> _objectIds;
    readonly DisplayMaterial _selectedMaterial = new(SelectedColor)
    {
      Transparency = 0.3,
      BackTransparency = 0.3,
    };
    readonly DisplayMaterial _ghostMaterial = new(GhostColor)
    {
      Transparency = 0.7,
      BackTransparency = 0.7,
    };
    bool _ghostOriginals;

    internal MirrorPreviewConduit(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
      _doc = doc;
      _objectIds = objectIds.ToHashSet();
    }

    internal void SetGhostOriginals(bool ghostOriginals) =>
      _ghostOriginals = ghostOriginals;

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
      if (_ghostOriginals && e.RhinoObject != null && _objectIds.Contains(e.RhinoObject.Id))
        e.CullObject = true;
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (!_ghostOriginals)
        return;
      foreach (var objectId in _objectIds)
      {
        var source = _doc.Objects.FindId(objectId);
        if (source != null)
          DrawGeometry(e.Display, source, Transform.Identity, false, false);
      }
    }

    internal void DrawMirrored(
      DisplayPipeline display, Transform mirror, bool flipText)
    {
      foreach (var objectId in _objectIds)
      {
        var source = _doc.Objects.FindId(objectId);
        if (source != null)
          DrawGeometry(display, source, mirror, flipText, true);
      }
    }

    void DrawGeometry(
      DisplayPipeline display, RhinoObject source, Transform transform,
      bool flipText, bool selected)
    {
      if (source is InstanceObject instance)
      {
        Transform instanceTransform = transform * instance.InstanceXform;
        display.DrawInstanceDefinitionShaded(
          instance.InstanceDefinition, selected ? _selectedMaterial : _ghostMaterial,
          instanceTransform);
        return;
      }

      GeometryBase? geometry = source.Geometry.Duplicate();
      if (geometry == null)
        return;
      using (geometry)
      {
        Transform applied = transform;
        if (flipText && geometry is TextEntity sourceText)
          applied = TextFlipTransform(sourceText, transform) * transform;
        if (!geometry.Transform(applied))
          return;

        Color color = selected ? SelectedColor : GhostColor;
        DisplayMaterial material = selected ? _selectedMaterial : _ghostMaterial;
        switch (geometry)
        {
          case Curve curve:
            PreviewDisplay.DrawCurve(display, curve, color, selected ? 1 : 0);
            break;
          case Brep brep:
            display.DrawBrepShaded(brep, material);
            PreviewDisplay.DrawBrepWires(display, brep, color, selected ? 1 : 0);
            break;
          case Extrusion extrusion:
            using (var brep = extrusion.ToBrep())
            {
              if (brep == null) break;
              display.DrawBrepShaded(brep, material);
              PreviewDisplay.DrawBrepWires(display, brep, color, selected ? 1 : 0);
            }
            break;
          case Surface surface:
            using (var brep = surface.ToBrep())
            {
              if (brep == null) break;
              display.DrawBrepShaded(brep, material);
              PreviewDisplay.DrawBrepWires(display, brep, color, selected ? 1 : 0);
            }
            break;
          case Mesh mesh:
            display.DrawMeshShaded(mesh, material);
            PreviewDisplay.DrawMeshWires(display, mesh, color, selected ? 1 : 0);
            break;
          case SubD subd:
            display.DrawSubDShaded(subd, material);
            display.DrawSubDWires(subd, color,
              PreviewDisplay.Thickness(display, selected ? 1 : 0));
            break;
          case TextEntity text:
            display.DrawAnnotation(text, color);
            break;
          case TextDot dot:
            display.DrawDot(dot, color, Color.Black, color);
            break;
          case Rhino.Geometry.Point point:
            display.DrawPoint(point.Location, PointStyle.ActivePoint,
              selected ? 5 : 3, color);
            break;
          case Hatch hatch:
            display.DrawHatch(hatch, color, color);
            break;
          default:
            display.DrawObject(source, transform);
            break;
        }
      }
    }

    public void Dispose()
    {
      Enabled = false;
      _selectedMaterial.Dispose();
      _ghostMaterial.Dispose();
    }
  }
}
