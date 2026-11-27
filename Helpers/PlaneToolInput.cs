using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools;

/// <summary>Shared Rhino selection and planar-face/three-point input for plane-based commands.</summary>
internal static class PlaneToolInput
{
  // Defaults
  private const bool DefaultUseThreePoints = false; // true starts with three points; false starts with planar-face picking.
  private const bool DefaultAllowCurvedFaces = false; // true uses a curved face's tangent at the hit; false accepts only planar faces.
  private const ObjectType OccludingTypes = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion |
    ObjectType.Mesh | ObjectType.SubD | ObjectType.InstanceReference; // Geometry that can obscure a picked face.

  internal static Result SelectObjects(string prompt, ObjectType types, ref bool copy, out List<Guid> ids)
  {
    ids = [];
    using var get = new GetObject();
    get.SetCommandPrompt(prompt);
    get.GeometryFilter = types;
    get.GroupSelect = true;
    get.SubObjectSelect = false;
    get.ReferenceObjectSelect = false;
    get.EnablePreSelect(true, true);
    get.EnableClearObjectsOnEntry(false);
    get.DeselectAllBeforePostSelect = false;
    var toggle = AddCopy(get, copy);
    while (true)
    {
      var result = get.GetMultiple(1, 0);
      copy = toggle.CurrentValue;
      if (result == GetResult.Option)
      {
        get.EnablePreSelect(false, true);
        continue;
      }
      if (result != GetResult.Object) return get.CommandResult();
      for (int i = 0; i < get.ObjectCount; i++)
      {
        using var reference = get.Object(i);
        if (reference != null && reference.ObjectId != Guid.Empty) ids.Add(reference.ObjectId);
      }
      ids = ids.Distinct().ToList();
      return ids.Count > 0 ? Result.Success : Result.Nothing;
    }
  }

  internal static OptionToggle AddCopy(GetBaseClass get, bool copy)
  {
    var toggle = new OptionToggle(copy, "No", "Yes");
    get.AddOptionToggle("Copy", ref toggle);
    return toggle;
  }

  internal static Result PickPlane(RhinoDoc doc, string facePrompt, ref bool copy, out Plane plane,
    Func<Plane, bool>? canUseFace = null, IReadOnlyList<Guid>? faceOwners = null, bool allowCurvedFaces = DefaultAllowCurvedFaces)
  {
    plane = Plane.Unset;
    bool threePoints = DefaultUseThreePoints;
    while (true)
    {
      var result = threePoints
        ? PickThreePoints(doc.ModelAbsoluteTolerance, ref copy, out plane)
        : PickFace(doc, facePrompt, ref copy, out plane, canUseFace, faceOwners, allowCurvedFaces);
      if (result != Result.Nothing) return result;
      threePoints = !threePoints;
    }
  }

  private static Result PickFace(RhinoDoc doc, string prompt, ref bool copy, out Plane plane,
    Func<Plane, bool>? canUseFace, IReadOnlyList<Guid>? faceOwners, bool allowCurvedFaces)
  {
    plane = Plane.Unset;
    using var get = new GetOption();
    get.AcceptCustomMessage(true);
    var toggle = AddCopy(get, copy);
    int points = get.AddOption("ThreePoints");
    using var picker = new FacePicker(doc, canUseFace, faceOwners, allowCurvedFaces) { Enabled = true };
    get.SetCommandPrompt(faceOwners != null && !picker.HasFaces
      ? "No eligible faces on the selection. Choose ThreePoints"
      : prompt);
    while (true)
    {
      var result = get.Get();
      copy = toggle.CurrentValue;
      if (result == GetResult.Option)
      {
        if (get.OptionIndex() == points) return Result.Nothing;
        continue;
      }
      if (result != GetResult.CustomMessage) return get.CommandResult();
      if (get.CustomMessage() is not FaceClick click) continue;
      plane = click.Plane;
      return Result.Success;
    }
  }

  private sealed record FaceClick(Plane Plane);

  private sealed class FacePicker : MouseCallback, IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly PlanarFaces _faces;
    private readonly PreviewDisplay.ObjectHighlighter _highlight;
    private readonly Guid _highlightId = Guid.NewGuid();
    private readonly Func<Plane, bool>? _canUseFace;
    private readonly Dictionary<PlanarFaces.Face, bool> _eligibility = [];
    private PlanarFaces.Face? _hovered;
    private Mesh? _preview;

    internal FacePicker(RhinoDoc doc, Func<Plane, bool>? canUseFace, IReadOnlyList<Guid>? faceOwners, bool allowCurvedFaces)
    {
      _doc = doc;
      _canUseFace = canUseFace;
      _faces = new(doc, faceOwners, allowCurvedFaces);
      _highlight = new(doc) { AutomaticForegroundDrawing = false, AutomaticShadedDrawing = true,
        DepthBiasedShading = true, RedrawOnChange = false };
    }

    internal bool HasFaces => _faces.HasFaces;

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      var face = Hit(e.View, e.ViewportPoint, out _);
      if (ReferenceEquals(_hovered, face)) return;
      var next = face == null ? null : BuildFacePreview(face.Surface);
      if (next == null) _highlight.SetObjects([]);
      else _highlight.SetObjects([_highlightId], new Dictionary<Guid, GeometryBase> { [_highlightId] = next });
      _preview?.Dispose();
      _preview = next;
      _hovered = face;
      _doc.Views.Redraw();
    }

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
      if (e.MouseButton != MouseButton.Left || e.View?.Document?.RuntimeSerialNumber != _doc.RuntimeSerialNumber)
        return;
      e.Cancel = true;
      if (Hit(e.View, e.ViewportPoint, out var plane) != null)
        GetBaseClass.PostCustomMessage(new FaceClick(plane));
      else if (_canUseFace != null && plane.IsValid)
        RhinoApp.WriteLine("That face's plane does not split the selection.");
    }

    private PlanarFaces.Face? Hit(RhinoView? view, System.Drawing.Point cursor, out Plane plane)
    {
      plane = Plane.Unset;
      if (view?.Document?.RuntimeSerialNumber != _doc.RuntimeSerialNumber ||
          !TryGetSightLine(view.ActiveViewport, cursor.X, cursor.Y, out var ray)) return null;
      var face = _faces.Hit(ray, view.ActiveViewport.Id, out plane);
      if (face == null || _canUseFace == null) return face;
      if (!_eligibility.TryGetValue(face, out bool canUse))
        _eligibility.Add(face, canUse = _canUseFace(face.Plane));
      return canUse ? face : null;
    }

    public void Dispose()
    {
      Enabled = false;
      _highlight.Dispose();
      _preview?.Dispose();
      _faces.Dispose();
      _doc.Views.Redraw();
    }
  }

  internal static bool TryGetSightLine(RhinoViewport viewport, double x, double y, out Line ray)
  {
    if (!viewport.GetFrustumLine(x, y, out ray) || !ray.IsValid) return false;
    // Rhino can return the frustum segment far-to-near in an orthographic view.
    if (ray.Direction * viewport.CameraDirection < 0) ray = new Line(ray.To, ray.From);
    return true;
  }

  internal static Mesh? BuildFacePreview(Surface surface)
  {
    if (surface is BrepFace face && face.GetMesh(MeshType.Render) is { Faces.Count: > 0 } render)
      return render.DuplicateMesh();
    using var brep = surface is BrepFace trimmed ? trimmed.DuplicateFace(false) : surface.ToBrep();
    if (brep == null) return null;
    var fragments = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
    if (fragments is not { Length: > 0 }) return null;
    if (fragments.Length == 1) return fragments[0];
    var combined = new Mesh();
    foreach (var fragment in fragments) { combined.Append(fragment); fragment.Dispose(); }
    return combined;
  }

  private static Result PickThreePoints(double tolerance, ref bool copy, out Plane plane)
  {
    plane = Plane.Unset;
    var origin = Point3d.Unset;
    var xPoint = Point3d.Unset;
    for (int step = 0; step < 3;)
    {
      using var get = new GetPoint();
      get.SetCommandPrompt(step switch { 0 => "First plane point", 1 => "Second plane point (X direction)", _ => "Third plane point (Y side)" });
      if (step > 0) get.DrawLineFromPoint(step == 1 ? origin : xPoint, true);
      var toggle = AddCopy(get, copy);
      int surface = get.AddOption("Surface");
      while (true)
      {
        var result = get.Get();
        copy = toggle.CurrentValue;
        if (result == GetResult.Option)
        {
          if (get.OptionIndex() == surface) return Result.Nothing;
          continue;
        }
        if (result != GetResult.Point) return get.CommandResult();
        break;
      }
      var pt = get.Point();
      if (step == 0) origin = pt;
      else if (step == 1)
      {
        if (origin.DistanceTo(pt) <= tolerance)
        {
          RhinoApp.WriteLine("The second point must differ from the first.");
          continue;
        }
        xPoint = pt;
      }
      else
      {
        if (new Line(origin, xPoint).DistanceTo(pt, false) <= tolerance ||
            !Commands.OrientCommon.TryBuildPlaneFromThreePoints(origin, xPoint, pt, out plane))
        {
          RhinoApp.WriteLine("The third point must be off the line through the first two.");
          continue;
        }
      }
      step++;
    }
    return Result.Success;
  }

  private sealed class PlanarFaces : IDisposable
  {
    internal sealed record Face(Surface Surface, Plane Plane, BoundingBox Bounds, int Layer);

    private readonly List<Face> _faces = [];
    private readonly List<Brep> _ownedBreps = [];
    private readonly double _tolerance;
    private readonly double _angleTolerance;
    private readonly bool _orientToViewer;
    private readonly bool _allowCurvedFaces;
    private readonly SceneVisibility _scene;

    internal PlanarFaces(RhinoDoc doc, IReadOnlyList<Guid>? faceOwners, bool allowCurvedFaces)
    {
      _tolerance = doc.ModelAbsoluteTolerance;
      _angleTolerance = doc.ModelAngleToleranceRadians;
      _orientToViewer = faceOwners != null;
      _allowCurvedFaces = allowCurvedFaces;
      _scene = new(doc);
      var candidates = faceOwners == null
        ? doc.Objects.GetObjectList(ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion)
        : faceOwners.Select(doc.Objects.FindId).OfType<RhinoObject>();
      foreach (var obj in candidates)
      {
        if (!obj.Visible) continue;
        int layer = obj.Attributes.LayerIndex;
        switch (obj.Geometry)
        {
          case Brep brep: AddBrep(brep, layer); break;
          case Extrusion extrusion when extrusion.ToBrep(splitKinkyFaces: true) is { } converted:
            _ownedBreps.Add(converted);
            AddBrep(converted, layer);
            break;
          case Surface surface: AddSurface(surface, layer); break;
        }
      }
    }

    internal bool HasFaces => _faces.Count > 0;

    private void AddBrep(Brep brep, int layer)
    {
      foreach (var face in brep.Faces)
      {
        if (face.TryGetPlane(out var plane, _tolerance))
        {
          AddSurface(face, layer, plane);
          continue;
        }
        // A straight-sided profile may be stored as one folded face.
        var split = face.DuplicateFace(false);
        if (split == null || !split.Faces.SplitKinkyFaces(_angleTolerance, true) || split.Faces.Count < 2)
        {
          split?.Dispose();
          if (_allowCurvedFaces) AddSurface(face, layer, Plane.Unset);
          continue;
        }
        _ownedBreps.Add(split);
        foreach (var patch in split.Faces) AddSurface(patch, layer);
      }
    }

    private void AddSurface(Surface surface, int layer, Plane? knownPlane = null)
    {
      var plane = knownPlane ?? (surface.TryGetPlane(out var flat, _tolerance) ? flat : Plane.Unset);
      if (!plane.IsValid && !_allowCurvedFaces) return;
      var bounds = surface.GetBoundingBox(false);
      if (!bounds.IsValid) return;
      bounds.Inflate(_tolerance);
      _faces.Add(new(surface, plane, bounds, layer));
    }

    internal Face? Hit(Line ray, Guid viewportId, out Plane plane)
    {
      Face? nearest = null;
      plane = Plane.Unset;
      double depth = 1;
      foreach (var face in _faces)
      {
        double t;
        if (face.Plane.IsValid)
        {
          if (!Intersection.LinePlane(ray, face.Plane, out t) || t < 0 || t > depth) continue;
        }
        else if (!CurvedHit(face, ray, depth, out t)) continue;
        var pt = ray.PointAt(t);
        if (!face.Bounds.Contains(pt) || !_scene.LayerVisible(face.Layer, viewportId) ||
            !face.Surface.ClosestPoint(pt, out double u, out double v) ||
            face.Surface.PointAt(u, v).DistanceTo(pt) > _tolerance) continue;
        if (face.Surface is BrepFace trimmed && trimmed.IsPointOnFace(u, v, _tolerance) == PointFaceRelation.Exterior) continue;
        if (face.Plane.IsValid)
          plane = new Plane(pt, face.Plane.XAxis, face.Plane.YAxis);
        else
        {
          if (!face.Surface.FrameAt(u, v, out var tangent)) continue;
          tangent.Origin = pt;
          plane = tangent;
        }
        depth = t;
        nearest = face;
      }
      if (nearest != null && _scene.Occludes(new Line(ray.From, plane.Origin), viewportId))
      {
        plane = Plane.Unset;
        return null;
      }
      // Which side the user saw is independent of how Rhino stores the surface normal.
      if (nearest != null && _orientToViewer && plane.ZAxis * ray.Direction > 0)
        plane = new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
      return nearest;
    }

    private bool CurvedHit(Face face, Line ray, double maximum, out double parameter)
    {
      parameter = double.PositiveInfinity;
      if (!Intersection.LineBox(ray, face.Bounds, _tolerance, out var span) || span.Max < 0 || span.Min > maximum) return false;
      double best = double.PositiveInfinity;
      using var line = new LineCurve(ray.From, ray.PointAt(maximum));
      if (face.Surface is BrepFace trimmed)
      {
        Intersection.CurveBrepFace(line, trimmed, _tolerance, out var overlaps, out var points);
        foreach (var overlap in overlaps ?? []) overlap.Dispose();
        foreach (var point in points ?? []) Accept(point);
      }
      else
      {
        using var hits = Intersection.CurveSurface(line, face.Surface, _tolerance, _tolerance);
        if (hits != null)
          foreach (var hit in hits)
            if (hit.IsPoint) Accept(hit.PointA);
      }
      parameter = best;
      return best <= maximum;

      void Accept(Point3d point)
      {
        double depth = ray.ClosestParameter(point);
        if (depth >= 0 && depth < best) best = depth;
      }
    }

    public void Dispose()
    {
      foreach (var brep in _ownedBreps) brep.Dispose();
      _scene.Dispose();
    }
  }

  private sealed class SceneVisibility(RhinoDoc doc) : IDisposable
  {
    private sealed class Occluder(GeometryBase geometry, BoundingBox bounds, Transform toLocal, int layer, int parentLayer) : IDisposable
    {
      private GeometryBase? _proxy;
      internal BoundingBox Bounds => bounds;
      internal int Layer => layer;
      internal int ParentLayer => parentLayer;

      internal bool HitsBefore(Line segment, double limit, double tolerance)
      {
        if (toLocal != Transform.Identity) segment.Transform(toLocal);
        var source = geometry switch
        {
          Extrusion extrusion => _proxy ??= extrusion.ToBrep(splitKinkyFaces: true),
          SubD subd => _proxy ??= Mesh.CreateFromSubD(subd, (int)SubDDisplayParameters.Density.DefaultDensity),
          _ => geometry
        };
        if (source is Mesh mesh)
        {
          var direction = segment.Direction;
          if (!direction.Unitize()) return false;
          double distance = Intersection.MeshRay(mesh, new Ray3d(segment.From, direction));
          return distance >= 0 && distance < limit * segment.Length;
        }
        bool IsInFront(Point3d point)
        {
          double parameter = segment.ClosestParameter(point);
          return parameter >= 0 && parameter < limit;
        }
        using var line = new LineCurve(segment);
        if (source is Brep brep)
        {
          Intersection.CurveBrep(line, brep, tolerance, out var overlaps, out var hits);
          foreach (var overlap in overlaps ?? []) overlap.Dispose();
          return hits?.Any(IsInFront) == true;
        }
        if (source is Surface surface)
        {
          using var hits = Intersection.CurveSurface(line, surface, tolerance, tolerance);
          return hits?.Any(hit => hit.IsPoint && IsInFront(hit.PointA)) == true;
        }
        return false;
      }

      public void Dispose() => _proxy?.Dispose();
    }

    private List<Occluder>? _occluders;

    internal bool LayerVisible(int index, Guid viewport)
    {
      var layer = doc.Layers[index];
      while (layer != null)
      {
        if (!layer.PerViewportIsVisible(viewport)) return false;
        layer = layer.ParentLayerId == Guid.Empty ? null : doc.Layers.FindId(layer.ParentLayerId);
      }
      return true;
    }

    internal bool Occludes(Line sightLine, Guid viewport)
    {
      double tolerance = doc.ModelAbsoluteTolerance;
      double length = sightLine.Length;
      if (length <= 2 * tolerance) return false;
      // Compare actual hit depths; Rhino can report the end face even on a shortened segment.
      double limit = 1 - 2 * tolerance / length;
      foreach (var body in _occluders ??= Capture())
      {
        if (!Intersection.LineBox(sightLine, body.Bounds, tolerance, out var span) || span.Max < 0 || span.Min >= limit ||
            !LayerVisible(body.Layer, viewport) || !LayerVisible(body.ParentLayer, viewport)) continue;
        if (body.HitsBefore(sightLine, limit, tolerance)) return true;
      }
      return false;
    }

    private List<Occluder> Capture()
    {
      var bodies = new List<Occluder>();
      var blockStack = new HashSet<Guid>();
      void Add(RhinoObject obj, Transform pose, int parentLayer)
      {
        if (obj is InstanceObject instance)
        {
          if (instance.InstanceDefinition is not { } definition || !blockStack.Add(definition.Id)) return;
          foreach (var member in definition.GetObjects())
            if (!member.IsHidden) Add(member, pose * instance.InstanceXform, parentLayer);
          blockStack.Remove(definition.Id);
        }
        else if (obj.Geometry is { } geometry && pose.TryGetInverse(out var inverse))
        {
          var bounds = pose == Transform.Identity ? geometry.GetBoundingBox(false) : geometry.GetBoundingBox(pose);
          if (bounds.IsValid) bodies.Add(new(geometry, bounds, inverse, obj.Attributes.LayerIndex, parentLayer));
        }
      }

      foreach (var obj in doc.Objects.GetObjectList(new ObjectEnumeratorSettings
        { ObjectTypeFilter = OccludingTypes, LockedObjects = true, ReferenceObjects = true }))
        if (obj.Visible) Add(obj, Transform.Identity, obj.Attributes.LayerIndex);
      return bodies;
    }

    public void Dispose()
    {
      if (_occluders != null) foreach (var body in _occluders) body.Dispose();
    }
  }
}
