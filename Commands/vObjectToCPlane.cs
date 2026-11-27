using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vObjectToCPlane : vToolsCommand
{
  // Defaults
  [CommandDefault("vObjectToCPlane", "vTools.Commands.vObjectToCPlane.DefaultCopy",
    "True keeps the original objects and places copies; false moves the originals.", OptionKeys = "copy")]
  private const bool DefaultCopy = false; // true places copies; false moves the original objects.
  private static readonly bool ConfiguredDefaultCopy = CommandDefaults.Get(
    "vObjectToCPlane", "vTools.Commands.vObjectToCPlane.DefaultCopy", DefaultCopy); // Initial Copy until a value is saved.

  private const string CopyKey = "copy";
  public override string EnglishName => "vObjectToCPlane";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    bool copy = ToolsOptionStore.Read(EnglishName,
      section => ToolsOptionStore.TryGetBool(section, CopyKey, out bool saved) ? saved : ConfiguredDefaultCopy);
    try
    {
      var result = PlaneToolInput.SelectObjects("Select objects to orient", ObjectType.AnyObject, ref copy, out var ids);
      if (result != Result.Success) return result;
      var objects = ids.Select(doc.Objects.FindId).OfType<RhinoObject>().ToArray();
      var items = objects.Select(obj => new ObjectTransformSelection.Item(obj.Id, [], obj.RuntimeSerialNumber)).ToArray();
      if (items.Length != ids.Count) return Result.Failure;
      var viewport = doc.Views.ActiveView?.ActiveViewport;
      if (viewport == null) return Result.Failure;
      var cplane = viewport.ConstructionPlane();
      result = PlaneToolInput.PickPlane(doc, "Pick a visible face on the selection to put down", ref copy, out var source,
        faceOwners: ids, allowCurvedFaces: true);
      if (result != Result.Success) return result;

      using var get = new GetPoint();
      get.SetCommandPrompt("Place objects on the CPlane");
      get.Constrain(cplane, false);
      get.FullFrameRedrawDuringGet = true;
      using var preview = new PlacementPreview(doc, viewport, objects);
      var toggle = PlaneToolInput.AddCopy(get, copy);
      get.DynamicDraw += (_, e) =>
      {
        if (!e.CurrentPoint.IsValid) return;
        preview.Draw(e.Display, Placement(source, cplane, e.CurrentPoint));
      };
      while (true)
      {
        var picked = get.Get();
        copy = toggle.CurrentValue;
        if (picked == GetResult.Option) continue;
        if (picked != GetResult.Point) return get.CommandResult();
        break;
      }

      if (!ObjectTransformSelection.TryTransform(doc, items, Placement(source, cplane, get.Point()), copy,
            out var sourceIds, out var placedIds))
      {
        RhinoApp.WriteLine("vObjectToCPlane: Could not place the selection. The originals were kept.");
        return Result.Failure;
      }
      if (copy) OrientCommon.RecreateGroupsForCopiedObjects(doc, sourceIds, placedIds);
      doc.Views.Redraw();
      return Result.Success;
    }
    finally
    {
      _ = ToolsOptionStore.Update(EnglishName, section => section[CopyKey] = copy);
    }
  }

  internal static Transform Placement(Plane source, Plane cplane, Point3d point)
  {
    var down = new Plane(cplane.ClosestPoint(point), cplane.XAxis, -cplane.YAxis);
    return Transform.PlaneToPlane(source, down);
  }

  private static Mesh[] PlacementMeshes(RhinoObject obj)
  {
    switch (obj.Geometry)
    {
      case Mesh mesh: return [mesh.DuplicateMesh()];
      case SubD subd:
        return Mesh.CreateFromSubD(subd, (int)SubDDisplayParameters.Density.DefaultDensity) is { } subMesh ? [subMesh] : [];
      case Brep brep:
        return Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? [];
      case Extrusion extrusion:
        return Mesh.CreateFromExtrusion(extrusion, MeshingParameters.FastRenderMesh) is { } extrusionMesh ? [extrusionMesh] : [];
      case Surface surface:
        using (var patch = surface.ToBrep())
          return patch == null ? [] : Mesh.CreateFromBrep(patch, MeshingParameters.FastRenderMesh) ?? [];
      default: return [];
    }
  }

  private static DisplayMaterial PlacementMaterial(RhinoObject obj, RhinoViewport viewport, Color wireColor)
  {
    var appearance = viewport.DisplayMode?.DisplayAttributes;
    if (appearance?.UseCustomObjectColor == true)
    {
      var color = appearance.ObjectColor;
      return new DisplayMaterial(color) { IsTwoSided = true, BackDiffuse = color };
    }
    // The default black wire color is not the object's shaded material.
    if (appearance?.UseAssignedObjectMaterial == true || appearance?.UseCustomObjectMaterial == true ||
        wireColor.ToArgb() == Color.Black.ToArgb())
    {
      var original = obj.GetMaterial(true);
      if (original != null)
        return new DisplayMaterial(original) { IsTwoSided = true, BackDiffuse = original.DiffuseColor };
      return new DisplayMaterial();
    }
    return new DisplayMaterial(wireColor) { IsTwoSided = true, BackDiffuse = wireColor };
  }

  private sealed class PlacementPreview : IDisposable
  {
    private sealed record Body(Transform AssemblyPose, Mesh[] Meshes, DisplayMaterial Material, Curve[] Edges, Color WireColor);
    private readonly List<Body> _bodies = [];
    private readonly List<(RhinoObject Object, Transform AssemblyPose)> _native = [];

    internal PlacementPreview(RhinoDoc doc, RhinoViewport viewport, IEnumerable<RhinoObject> objects)
    {
      var blocks = new HashSet<Guid>();
      void Add(RhinoObject obj, Transform pose)
      {
        if (obj is InstanceObject instance && instance.InstanceDefinition is { } definition &&
            blocks.Add(definition.Id))
        {
          foreach (var member in definition.GetObjects()) Add(member, pose * instance.InstanceXform);
          blocks.Remove(definition.Id);
          return;
        }
        var meshes = PlacementMeshes(obj);
        if (meshes.Length == 0)
        {
          _native.Add((obj, pose));
          return;
        }
        var color = obj.Attributes.DrawColor(doc, viewport.Id);
        foreach (var mesh in meshes)
          if (mesh.Normals.Count != mesh.Vertices.Count) mesh.Normals.ComputeNormals();
        var edges = obj.Geometry is Brep brep ? brep.DuplicateEdgeCurves() : [];
        _bodies.Add(new(pose, meshes, PlacementMaterial(obj, viewport, color), edges, color));
      }
      try { foreach (var obj in objects) Add(obj, Transform.Identity); }
      catch { Dispose(); throw; }
    }

    internal void Draw(DisplayPipeline display, Transform placement)
    {
      var previousBias = display.ZBiasMode;
      var previousDepth = display.DepthMode;
      display.PushDepthTesting(true);
      display.PushDepthWriting(true);
      try
      {
        display.DepthMode = DepthMode.Neutral;
        foreach (var body in _bodies)
        {
          display.PushModelTransform(placement * body.AssemblyPose);
          try
          {
            foreach (var mesh in body.Meshes) display.DrawMeshShaded(mesh, body.Material);
            display.ZBiasMode = ZBiasMode.TowardsCamera;
            try
            {
              foreach (var edge in body.Edges) PreviewDisplay.DrawCurve(display, edge, body.WireColor);
            }
            finally { display.ZBiasMode = previousBias; }
          }
          finally { display.PopModelTransform(); }
        }
      }
      finally
      {
        display.DepthMode = previousDepth;
        display.PopDepthWriting();
        display.PopDepthTesting();
      }
      foreach (var (obj, pose) in _native) display.DrawObject(obj, placement * pose);
    }

    public void Dispose()
    {
      foreach (var body in _bodies)
      {
        foreach (var mesh in body.Meshes) mesh.Dispose();
        foreach (var edge in body.Edges) edge.Dispose();
        body.Material.Dispose();
      }
    }
  }
}
