using System;
using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace vTools.Commands;

/// <summary>
/// Flips the normal direction of individual surface or polysurface faces.
/// </summary>
[CommandStyle(Style.ScriptRunner)]
public sealed class vDir : Command
{
  // Customizable selection behavior
  private const ObjectType SupportedGeometry = ObjectType.Surface | ObjectType.Brep; // Rhino object types accepted while hovering individual Brep faces.
  private static readonly MeshType[] CachedMeshTypes = [MeshType.Render, MeshType.Analysis, MeshType.Preview]; // Cached face-mesh kinds whose winding must follow the changed Brep face direction.
  private const double FlippedNormalDotMaximum = -0.9; // Largest acceptable dot product between pre-flip and post-flip unit normals; range -1.0 to 1.0.

  public override string EnglishName => "vDir";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var flippedFaces = 0;
    var failedFaces = 0;
    FaceTarget? suppressHoverUntilLeave = null;

    foreach (var target in CapturePreselectedFaces(doc))
    {
      ClearTemporarySelection(doc, target.ObjectId);
      if (TryFlipFace(doc, target))
        flippedFaces++;
      else
        failedFaces++;
    }

    doc.Views.Redraw();
    while (true)
    {
      using var getter = CreateFaceGetter();
      using var hoverTracker = new FaceHoverTracker(
        doc,
        suppressHoverUntilLeave);
      hoverTracker.Enabled = true;
      GetResult getResult;
      try
      {
        getResult = getter.Get();
      }
      finally
      {
        hoverTracker.Enabled = false;
      }
      if (getResult is GetResult.Cancel or GetResult.Nothing)
        break;

      if (getResult != GetResult.Object || getter.ObjectCount < 1 ||
          !TryGetTarget(getter.Object(0), out var target))
        continue;

      ClearTemporarySelection(doc, target.ObjectId);
      doc.Views.Redraw();

      if (TryFlipFace(doc, target))
      {
        flippedFaces++;
        suppressHoverUntilLeave = target;
      }
      else
        failedFaces++;

      doc.Views.Redraw();
    }

    if (flippedFaces == 0)
    {
      if (failedFaces > 0)
      {
        RhinoApp.WriteLine($"vDir: failed to flip {failedFaces} face(s). Check vTools.log.");
        return Result.Failure;
      }

      return Result.Nothing;
    }

    var failureLabel = failedFaces > 0
      ? $"; failed to flip {failedFaces} face(s)"
      : string.Empty;
    RhinoApp.WriteLine($"vDir: flipped {flippedFaces} face(s){failureLabel}.");
    Log.Write("vDir", $"complete flipped_faces={flippedFaces} failed_faces={failedFaces}");
    return Result.Success;
  }

  private static GetObject CreateFaceGetter()
  {
    var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt("Click face to flip direction");
    getter.GeometryFilter = SupportedGeometry;
    getter.SetCustomGeometryFilter(IsFaceInput);
    getter.SubObjectSelect = true;
    getter.BottomObjectPreference = true;
    getter.GroupSelect = false;
    getter.EnableHighlight(true);
    getter.EnablePreSelect(false, true);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(true);
    getter.DeselectAllBeforePostSelect = false;
    getter.AcceptNothing(true);
    return getter;
  }

  private static IReadOnlyCollection<FaceTarget> CapturePreselectedFaces(RhinoDoc doc)
  {
    var targets = new HashSet<FaceTarget>();
    foreach (var rhinoObject in doc.Objects.GetSelectedObjects(false, false))
    {
      if (rhinoObject.Geometry is not Brep brep)
        continue;

      var selectedSubobjects = rhinoObject.GetSelectedSubObjects() ??
                               Array.Empty<ComponentIndex>();
      foreach (var component in selectedSubobjects)
      {
        if (component.ComponentIndexType == ComponentIndexType.BrepFace &&
            component.Index >= 0 && component.Index < brep.Faces.Count)
        {
          targets.Add(new FaceTarget(rhinoObject.Id, component.Index));
        }
      }

      if (selectedSubobjects.Length == 0 && brep.Faces.Count == 1 &&
          rhinoObject.IsSelected(checkSubObjects: false) != 0)
      {
        targets.Add(new FaceTarget(rhinoObject.Id, 0));
      }
    }

    return targets;
  }

  private static bool TryGetTarget(ObjRef objRef, out FaceTarget target)
  {
    target = default;
    var rhinoObject = objRef.Object();
    if (rhinoObject?.Geometry is not Brep brep)
      return false;

    var component = objRef.GeometryComponentIndex;
    var faceIndex = component.ComponentIndexType == ComponentIndexType.BrepFace
      ? component.Index
      : brep.Faces.Count == 1
        ? 0
        : -1;
    if (faceIndex < 0)
    {
      var selectionPoint = objRef.SelectionPoint();
      if (selectionPoint.IsValid &&
          brep.ClosestPoint(
            selectionPoint,
            out _,
            out var closestComponent,
            out _,
            out _,
            maximumDistance: 0.0,
            out _) &&
          closestComponent.ComponentIndexType == ComponentIndexType.BrepFace)
      {
        faceIndex = closestComponent.Index;
      }
    }
    if (faceIndex < 0 || faceIndex >= brep.Faces.Count)
      return false;

    target = new FaceTarget(rhinoObject.Id, faceIndex);
    return true;
  }

  private static bool TryFlipFace(RhinoDoc doc, FaceTarget target)
  {
    var sourceObject = doc.Objects.FindId(target.ObjectId);
    if (sourceObject?.Geometry is not Brep sourceBrep ||
        target.FaceIndex < 0 || target.FaceIndex >= sourceBrep.Faces.Count)
    {
      Log.Write("vDir", $"invalid_target object={target.ObjectId} face={target.FaceIndex}");
      return false;
    }

    var affectedHistory = HistoryBreakWarning.CaptureAffectedRecords(
      doc,
      target.ObjectId);
    if (!HistoryBreakWarning.Confirm(doc, "vDir", affectedHistory))
      return false;

    using var duplicate = sourceBrep.DuplicateBrep();
    if (duplicate == null)
    {
      Log.Write("vDir", $"duplicate_failed object={target.ObjectId} face={target.FaceIndex}");
      return false;
    }

    var before = sourceBrep.Faces[target.FaceIndex].OrientationIsReversed;
    var samplePoint = sourceBrep.Faces[target.FaceIndex].PointAt(
      sourceBrep.Faces[target.FaceIndex].Domain(0).Mid,
      sourceBrep.Faces[target.FaceIndex].Domain(1).Mid);
    var beforeNormal = FaceNormalAtPoint(
      sourceBrep.Faces[target.FaceIndex],
      samplePoint);
    if (duplicate.Faces.Count == 1)
    {
      duplicate.Flip();
    }
    else
    {
      duplicate.Faces.Flip(onlyReversedFaces: false);
      for (var faceIndex = 0; faceIndex < duplicate.Faces.Count; faceIndex++)
      {
        if (faceIndex == target.FaceIndex)
          continue;
        duplicate.Faces[faceIndex].OrientationIsReversed =
          !duplicate.Faces[faceIndex].OrientationIsReversed;
      }
      duplicate.DestroyRegionTopology();
      duplicate.SetTolerancesBoxesAndFlags();
    }
    var flippedMeshCount = SynchronizeCachedFaceMeshes(
      sourceBrep,
      duplicate,
      target.FaceIndex);

    var prepared = duplicate.Faces[target.FaceIndex].OrientationIsReversed;
    var preparedNormal = FaceNormalAtPoint(
      duplicate.Faces[target.FaceIndex],
      samplePoint);
    var preparedDot = UnitNormalDot(beforeNormal, preparedNormal);
    if (preparedDot.HasValue && preparedDot.Value > FlippedNormalDotMaximum)
    {
      Log.Write(
        "vDir",
        $"prepare_failed object={target.ObjectId} face={target.FaceIndex} " +
        $"before_flag={before} prepared_flag={prepared} normal_dot={preparedDot:G6} " +
        $"faces={duplicate.Faces.Count}");
      return false;
    }

    var undoRecord = doc.BeginUndoRecord("vDir");
    var replaced = false;
    try
    {
      replaced = doc.Objects.Replace(target.ObjectId, duplicate);
    }
    finally
    {
      if (undoRecord != 0)
        doc.EndUndoRecord(undoRecord);
    }

    if (!replaced)
    {
      Log.Write("vDir", $"replace_failed object={target.ObjectId} face={target.FaceIndex}");
      return false;
    }

    var storedObject = doc.Objects.FindId(target.ObjectId);
    var storedBrep = storedObject?.Geometry as Brep;
    var stored = storedBrep != null && target.FaceIndex < storedBrep.Faces.Count
      ? storedBrep.Faces[target.FaceIndex].OrientationIsReversed
      : before;
    var storedNormal = storedBrep != null && target.FaceIndex < storedBrep.Faces.Count
      ? FaceNormalAtPoint(storedBrep.Faces[target.FaceIndex], samplePoint)
      : Vector3d.Unset;
    var storedDot = UnitNormalDot(beforeNormal, storedNormal);
    Log.Write(
      "vDir",
      $"flip object={target.ObjectId} face={target.FaceIndex} faces={duplicate.Faces.Count} " +
      $"before_flag={before} prepared_flag={prepared} stored_flag={stored} " +
      $"prepared_normal_dot={DotText(preparedDot)} stored_normal_dot={DotText(storedDot)} " +
      $"cached_meshes_flipped={flippedMeshCount} replaced={replaced}");
    return !storedDot.HasValue || storedDot.Value <= FlippedNormalDotMaximum;
  }

  private static int SynchronizeCachedFaceMeshes(
    Brep source,
    Brep destination,
    int flippedFaceIndex)
  {
    var flippedCount = 0;
    for (var faceIndex = 0; faceIndex < source.Faces.Count; faceIndex++)
    {
      foreach (var meshType in CachedMeshTypes)
      {
        var sourceMesh = source.Faces[faceIndex].GetMesh(meshType);
        if (sourceMesh == null || !sourceMesh.IsValid)
          continue;

        var replacementMesh = sourceMesh.DuplicateMesh();
        if (replacementMesh == null || !replacementMesh.IsValid)
        {
          replacementMesh?.Dispose();
          continue;
        }

        if (faceIndex == flippedFaceIndex)
        {
          replacementMesh.Flip(
            vertexNormals: true,
            faceNormals: true,
            faceOrientation: true,
            ngonsBoundaryDirection: true);
        }

        if (destination.Faces[faceIndex].SetMesh(meshType, replacementMesh))
        {
          if (faceIndex == flippedFaceIndex)
            flippedCount++;
        }
        else
        {
          replacementMesh.Dispose();
        }
      }
    }

    return flippedCount;
  }

  private static Vector3d FaceNormalAtPoint(BrepFace face, Point3d point)
  {
    if (!point.IsValid || !face.ClosestPoint(point, out var u, out var v))
      return Vector3d.Unset;
    var normal = face.NormalAt(u, v);
    return normal.Unitize() ? normal : Vector3d.Unset;
  }

  private static double? UnitNormalDot(Vector3d first, Vector3d second) =>
    first.IsValid && second.IsValid ? first * second : null;

  private static string DotText(double? value) =>
    value.HasValue ? value.Value.ToString("G6") : "unset";

  private static void ClearTemporarySelection(RhinoDoc doc, Guid objectId)
  {
    var rhinoObject = doc.Objects.FindId(objectId);
    rhinoObject?.UnselectAllSubObjects();
    rhinoObject?.Select(false);
  }

  private static bool IsFaceInput(
    RhinoObject rhinoObject,
    GeometryBase geometry,
    ComponentIndex componentIndex)
  {
    if (rhinoObject == null || geometry == null)
      return false;

    if (componentIndex.ComponentIndexType == ComponentIndexType.BrepFace)
      return true;

    return componentIndex.ComponentIndexType is
             ComponentIndexType.InvalidType or ComponentIndexType.NoType &&
           geometry is Brep brep && brep.Faces.Count == 1;
  }

  private sealed class FaceHoverTracker : MouseCallback, IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly FaceHoverConduit _conduit;
    private FaceTarget? _highlighted;
    private FaceTarget? _suppressedUntilLeave;
    private bool _disposed;

    internal FaceHoverTracker(
      RhinoDoc doc,
      FaceTarget? suppressedUntilLeave)
    {
      _doc = doc;
      _suppressedUntilLeave = suppressedUntilLeave;
      _conduit = new FaceHoverConduit(doc) { Enabled = true };
    }

    public void Dispose()
    {
      if (_disposed)
        return;

      _disposed = true;
      Enabled = false;
      _conduit.Dispose();
      _highlighted = null;
      _doc.Views.Redraw();
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      var next = PickFace(e.View, e.ViewportPoint);
      if (_suppressedUntilLeave.HasValue)
      {
        if (next == _suppressedUntilLeave)
          next = null;
        else
          _suppressedUntilLeave = null;
      }

      if (next == _highlighted)
      {
        base.OnMouseMove(e);
        return;
      }

      _highlighted = next;
      _conduit.SetTarget(next);
      _doc.Views.Redraw();
      base.OnMouseMove(e);
    }

    private FaceTarget? PickFace(
      Rhino.Display.RhinoView? view,
      System.Drawing.Point viewportPoint)
    {
      var viewport = view?.ActiveViewport;
      if (view == null || viewport == null ||
          !viewport.GetFrustumLine(
            viewportPoint.X,
            viewportPoint.Y,
            out var pickLine))
        return null;

      using var pickContext = new PickContext
      {
        View = view,
        PickLine = pickLine,
        PickStyle = PickStyle.PointPick,
        PickMode = PickMode.Shaded,
        PickGroupsEnabled = false,
        SubObjectSelectionEnabled = true
      };
      pickContext.SetPickTransform(viewport.GetPickTransform(viewportPoint));
      pickContext.UpdateClippingPlanes();

      var picked = _doc.Objects.PickObjects(pickContext);
      if (picked == null)
        return null;

      foreach (var objRef in picked)
      {
        if (TryGetTarget(objRef, out var target))
          return target;
      }

      return null;
    }
  }

  private sealed class FaceHoverConduit : DisplayConduit, IDisposable
  {
    private readonly RhinoDoc _doc;
    private Guid _objectId;
    private int _faceIndex = -1;
    private Brep? _face;
    private bool _disposed;

    internal FaceHoverConduit(RhinoDoc doc)
    {
      _doc = doc;
    }

    internal void SetTarget(FaceTarget? target)
    {
      _face?.Dispose();
      _face = null;
      _objectId = Guid.Empty;
      _faceIndex = -1;
      if (!target.HasValue ||
          _doc.Objects.FindId(target.Value.ObjectId) is not { Geometry: Brep brep } ||
          target.Value.FaceIndex < 0 ||
          target.Value.FaceIndex >= brep.Faces.Count)
        return;

      _face = brep.Faces[target.Value.FaceIndex].DuplicateFace(
        duplicateMeshes: true);
      if (_face == null)
        return;

      _face.Flip();
      FlipCachedMeshes(_face);
      _objectId = target.Value.ObjectId;
      _faceIndex = target.Value.FaceIndex;
    }

    public void Dispose()
    {
      if (_disposed)
        return;

      _disposed = true;
      Enabled = false;
      _face?.Dispose();
      _face = null;
      _objectId = Guid.Empty;
      _faceIndex = -1;
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (_face == null || _objectId == Guid.Empty ||
          _doc.Objects.FindId(_objectId) is not { } sourceObject)
        return;

      var frontColor = sourceObject.Attributes.DrawColor(_doc, e.Viewport.Id);
      var displayAttributes = e.Display.DisplayPipelineAttributes;
      DisplayMaterial? material = null;
      if (_faceIndex >= 0 && sourceObject.HasSubobjectMaterials)
      {
        var faceMaterial = sourceObject.GetMaterial(FaceComponent(_faceIndex));
        if (faceMaterial != null)
          material = new DisplayMaterial(faceMaterial);
      }

      material ??= e.Display.SetupDisplayMaterial(_doc, sourceObject);
      material ??= new DisplayMaterial(frontColor);
      using (material)
      {
        material.IsTwoSided = true;
        material.BackDiffuse = ResolveBackColor(displayAttributes, frontColor);
        material.Transparency = 0.0;
        material.BackTransparency = 0.0;
        e.Display.DrawBrepShaded(_face, material);
      }
    }

    private static void FlipCachedMeshes(Brep faceBrep)
    {
      if (faceBrep.Faces.Count == 0)
        return;

      foreach (var meshType in CachedMeshTypes)
      {
        faceBrep.Faces[0].GetMesh(meshType)?.Flip(
          vertexNormals: true,
          faceNormals: true,
          faceOrientation: true,
          ngonsBoundaryDirection: true);
      }
    }

    private static Color ResolveBackColor(
      DisplayPipelineAttributes displayAttributes,
      Color frontColor) =>
      displayAttributes.BackfaceDisplayStyle switch
      {
        DisplayPipelineAttributes.BackfaceStyle.UseFrontFaceSettings => frontColor,
        DisplayPipelineAttributes.BackfaceStyle.UseObjectColor => frontColor,
        _ when !displayAttributes.BackMaterialDiffuseColor.IsEmpty =>
          displayAttributes.BackMaterialDiffuseColor,
        _ => frontColor
      };
  }

  private static ComponentIndex FaceComponent(int faceIndex) =>
    new(ComponentIndexType.BrepFace, faceIndex);

  private readonly record struct FaceTarget(Guid ObjectId, int FaceIndex);
}
