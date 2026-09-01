using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
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
public sealed class vDir : vToolsCommand
{
  // Defaults and customizable constants
  private const FaceOperation DefaultOperation = FaceOperation.SingleFace; // Initial click mode: SingleFace, FlipAll, or SameDirection.
  private const ObjectType SupportedGeometry = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.SubD; // Rhino object types accepted while hovering face-based geometry.
  private static readonly MeshType[] CachedMeshTypes = [MeshType.Render, MeshType.Analysis, MeshType.Preview]; // Cached face-mesh kinds whose winding must follow the changed Brep face direction.
  private const double FlippedNormalDotMaximum = -0.9; // Largest acceptable dot product between pre-flip and post-flip unit normals; range -1.0 to 1.0.
  private const double PreservedNormalDotMinimum = 0.9; // Smallest acceptable dot product for a face that must retain its direction; range -1.0 to 1.0.
  private const string SingleFaceOptionName = "SingleFace"; // Command-line option restoring one-face-at-a-time flipping.
  private const string FlipAllOptionName = "FlipAll"; // Command-line option reversing every face in the clicked Brep.
  private const string SameDirectionOptionName = "SameDirection"; // Command-line option aligning connected faces to the clicked face's direction.
  private static readonly Color ReferenceFacePreviewColor = Color.FromArgb(255, 154, 48); // RGB color identifying the hovered reference face for the active direction operation.
  private static readonly Color AffectedFacePreviewColor = Color.FromArgb(35, 190, 235); // RGB color identifying additional faces that the active direction operation will reverse.
  private const double FacePreviewTransparency = 0.18; // Display-material transparency for reference and affected face previews; range 0.0 opaque to 1.0 invisible.
  private const double BackFacePreviewBrightness = 0.5; // Multiplier applied to preview RGB channels on the back side; range 0.0 black to 1.0 unchanged.
  private const int ModifierRefreshIntervalMilliseconds = 30; // Polling interval in milliseconds for modifier-only preview and prompt updates; positive integer.
  private const int ShiftVirtualKey = 0x10; // Win32 virtual-key code used to detect either Shift key.
  private const int ControlVirtualKey = 0x11; // Win32 virtual-key code used to detect either Ctrl key.
  private const int KeyPressedMask = 0x8000; // Win32 GetAsyncKeyState mask indicating that a key is currently held.
  private const string SubDSingleFaceMessage = "vDir: Rhino does not support reversing one SubD face independently; use FlipAll."; // Command-line message shown when SingleFace is requested for SubD geometry.
  private const string SubDSameDirectionMessage = "vDir: SubD face directions are already unified; use FlipAll to reverse the whole SubD."; // Command-line message shown when SameDirection is requested for SubD geometry.

  private static readonly Stack<FaceHistoryRecord> UndoHistory = [];
  private static readonly Stack<FaceHistoryRecord> RedoHistory = [];
  private static EventHandler? _pendingHistoryIdleHandler;
  private static PendingHistoryAction? _pendingHistoryAction;
  private static bool _continuingAfterHistory;
  private static FaceOperation _continuedOperation = DefaultOperation;

  public override string EnglishName => "vDir";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var continuingAfterHistory = _continuingAfterHistory;
    _continuingAfterHistory = false;
    var operation = continuingAfterHistory
      ? _continuedOperation
      : DefaultOperation;
    if (!continuingAfterHistory)
    {
      UndoHistory.Clear();
      RedoHistory.Clear();
    }

    var flippedFaces = 0;
    var failedOperations = 0;
    FaceTarget? suppressHoverUntilLeave = null;
    using var shortcutSession = new LocalUndoRedoShortcutSession(
      "vDir",
      redo => new FaceHistoryRequest(redo));

    if (!continuingAfterHistory)
    {
      var preselectedOperation = ResolveModifierOperation(operation);
      var processedPreselectedObjects = new HashSet<Guid>();
      foreach (var target in CapturePreselectedFaces(
                 doc,
                 includeWholePolysurfaces:
                   preselectedOperation != FaceOperation.SingleFace))
      {
        if (preselectedOperation != FaceOperation.SingleFace &&
            !processedPreselectedObjects.Add(target.ObjectId))
          continue;

        ClearTemporarySelection(doc, target.ObjectId);
        var change = TryApplyFaceOperation(doc, target, preselectedOperation);
        if (change.Success)
        {
          flippedFaces += change.FlippedFaceCount;
          TrackHistory(change);
        }
        else
          failedOperations++;
      }
    }

    doc.Views.Redraw();
    while (true)
    {
      using var getter = CreateFaceGetter(
        operation,
        out var singleFaceOption,
        out var flipAllOption,
        out var sameDirectionOption);
      using var hoverTracker = new FaceHoverTracker(
        doc,
        suppressHoverUntilLeave,
        operation);
      hoverTracker.Enabled = true;
      var displayedOperation = ResolveModifierOperation(operation);
      using var modifierTimer = new System.Windows.Forms.Timer
      {
        Interval = ModifierRefreshIntervalMilliseconds
      };
      modifierTimer.Tick += (_, _) =>
      {
        var nextOperation = ResolveModifierOperation(operation);
        if (nextOperation == displayedOperation)
          return;

        displayedOperation = nextOperation;
        hoverTracker.RefreshModifierOperation(nextOperation);
        getter.SetCommandPrompt(CommandPrompt(operation, nextOperation));
        doc.Views.Redraw();
      };
      modifierTimer.Start();
      GetResult getResult;
      try
      {
        getResult = getter.Get();
      }
      finally
      {
        modifierTimer.Stop();
        hoverTracker.Enabled = false;
      }
      var customMessage = getResult == GetResult.CustomMessage
        ? getter.CustomMessage()
        : null;
      if (customMessage is FaceHistoryRequest historyRequest)
      {
        if (QueueHistoryAction(
              doc,
              historyRequest.Redo,
              operation))
          return Result.Success;
        continue;
      }

      var ctrlClickRequest = customMessage as FaceClickRequest;
      if (getResult is GetResult.Cancel or GetResult.Nothing)
        break;

      if (getResult == GetResult.Option)
      {
        var optionIndex = getter.Option()?.Index ?? -1;
        if (optionIndex == singleFaceOption)
          operation = FaceOperation.SingleFace;
        else if (optionIndex == flipAllOption)
          operation = FaceOperation.FlipAll;
        else if (optionIndex == sameDirectionOption)
          operation = FaceOperation.SameDirection;
        continue;
      }

      FaceTarget target;
      FaceOperation effectiveOperation;
      if (ctrlClickRequest != null)
      {
        target = ctrlClickRequest.Target;
        effectiveOperation = ctrlClickRequest.Operation;
      }
      else
      {
        if (getResult != GetResult.Object || getter.ObjectCount < 1 ||
            !TryGetTarget(getter.Object(0), out target))
          continue;

        effectiveOperation = hoverTracker.SelectionOperation;
      }

      ClearTemporarySelection(doc, target.ObjectId);
      doc.Views.Redraw();

      var change = TryApplyFaceOperation(doc, target, effectiveOperation);
      if (change.Success)
      {
        flippedFaces += change.FlippedFaceCount;
        TrackHistory(change);
        suppressHoverUntilLeave = target;
      }
      else
        failedOperations++;

      doc.Views.Redraw();
    }

    if (flippedFaces == 0)
    {
      if (failedOperations > 0)
      {
        RhinoApp.WriteLine(
          $"vDir: failed to apply {failedOperations} operation(s). Check vTools.log.");
        return Result.Failure;
      }

      return Result.Nothing;
    }

    var failureLabel = failedOperations > 0
      ? $"; failed to apply {failedOperations} operation(s)"
      : string.Empty;
    RhinoApp.WriteLine($"vDir: flipped {flippedFaces} face(s){failureLabel}.");
    Log.Write(
      "vDir",
      $"complete flipped_faces={flippedFaces} failed_operations={failedOperations}");
    return Result.Success;
  }

  private static GetObject CreateFaceGetter(
    FaceOperation operation,
    out int singleFaceOption,
    out int flipAllOption,
    out int sameDirectionOption)
  {
    var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt(
      CommandPrompt(operation, ResolveModifierOperation(operation)));
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
    getter.AcceptCustomMessage(true);
    singleFaceOption = getter.AddOption(SingleFaceOptionName);
    flipAllOption = getter.AddOption(FlipAllOptionName);
    sameDirectionOption = getter.AddOption(SameDirectionOptionName);
    return getter;
  }

  private static IReadOnlyCollection<FaceTarget> CapturePreselectedFaces(
    RhinoDoc doc,
    bool includeWholePolysurfaces)
  {
    var targets = new HashSet<FaceTarget>();
    foreach (var rhinoObject in doc.Objects.GetSelectedObjects(false, false))
    {
      var geometry = rhinoObject.Geometry;
      var geometryKind = FaceGeometryKindFor(geometry);
      var faceCount = FaceCount(geometry);
      if (geometryKind == FaceGeometryKind.Unsupported || faceCount == 0)
        continue;

      var selectedSubobjects = rhinoObject.GetSelectedSubObjects() ??
                               Array.Empty<ComponentIndex>();
      foreach (var component in selectedSubobjects)
      {
        if (TryGetComponentFaceIndex(geometry, component, out var faceIndex))
          targets.Add(new FaceTarget(rhinoObject.Id, geometryKind, faceIndex));
      }

      if (selectedSubobjects.Length == 0 &&
          (faceCount == 1 || includeWholePolysurfaces) &&
          rhinoObject.IsSelected(checkSubObjects: false) != 0)
      {
        targets.Add(new FaceTarget(rhinoObject.Id, geometryKind, 0));
      }
    }

    return targets;
  }

  private static bool TryGetTarget(ObjRef objRef, out FaceTarget target)
  {
    target = default;
    var rhinoObject = objRef.Object();
    var geometry = rhinoObject?.Geometry;
    if (rhinoObject == null || geometry == null)
      return false;

    var geometryKind = FaceGeometryKindFor(geometry);
    if (geometryKind == FaceGeometryKind.Unsupported)
      return false;
    var component = objRef.GeometryComponentIndex;
    var faceIndex = TryGetComponentFaceIndex(geometry, component, out var componentFaceIndex)
      ? componentFaceIndex
      : FaceCount(geometry) == 1
        ? 0
        : -1;
    if (faceIndex < 0)
    {
      var selectionPoint = objRef.SelectionPoint();
      if (selectionPoint.IsValid)
        faceIndex = ClosestFaceIndex(geometry, selectionPoint);
    }
    if (faceIndex < 0 || faceIndex >= FaceCount(geometry))
      return false;

    target = new FaceTarget(rhinoObject.Id, geometryKind, faceIndex);
    return true;
  }

  private static FaceGeometryKind FaceGeometryKindFor(GeometryBase geometry) =>
    geometry switch
    {
      Mesh => FaceGeometryKind.Mesh,
      SubD => FaceGeometryKind.SubD,
      Brep or Extrusion or Surface => FaceGeometryKind.BrepForm,
      _ => FaceGeometryKind.Unsupported
    };

  private static int FaceCount(GeometryBase geometry) =>
    geometry switch
    {
      Brep brep => brep.Faces.Count,
      Extrusion extrusion => BrepFormFaceCount(extrusion),
      Mesh mesh => mesh.Faces.Count,
      SubD subd => subd.Faces.Count,
      Surface surface => BrepFormFaceCount(surface),
      _ => 0
    };

  private static int BrepFormFaceCount(GeometryBase geometry)
  {
    using var brep = CreateBrepForm(geometry);
    return brep?.Faces.Count ?? 0;
  }

  private static Brep? CreateBrepForm(GeometryBase geometry) =>
    geometry switch
    {
      Brep brep => brep.DuplicateBrep(),
      Extrusion extrusion => extrusion.ToBrep(splitKinkyFaces: true),
      Surface surface => surface.ToBrep(),
      _ => null
    };

  private static bool TryGetComponentFaceIndex(
    GeometryBase geometry,
    ComponentIndex component,
    out int faceIndex)
  {
    faceIndex = -1;
    switch (geometry)
    {
      case Brep brep when component.ComponentIndexType == ComponentIndexType.BrepFace:
        faceIndex = component.Index;
        return faceIndex >= 0 && faceIndex < brep.Faces.Count;

      case Extrusion extrusion:
      {
        var mapped = component.ComponentIndexType == ComponentIndexType.BrepFace
          ? component
          : extrusion.GetBrepFormComponentIndex(component);
        using var brep = CreateBrepForm(extrusion);
        faceIndex = mapped.ComponentIndexType == ComponentIndexType.BrepFace
          ? mapped.Index
          : -1;
        return brep != null && faceIndex >= 0 && faceIndex < brep.Faces.Count;
      }

      case Mesh mesh when component.ComponentIndexType == ComponentIndexType.MeshFace:
        faceIndex = component.Index;
        return faceIndex >= 0 && faceIndex < mesh.Faces.Count;

      case SubD subd when component.ComponentIndexType == ComponentIndexType.SubdFace:
      {
        var faces = subd.Faces.ToList();
        var componentFace = component.Index >= 0
          ? subd.Faces.Find(component.Index)
          : null;
        faceIndex = componentFace == null
          ? component.Index
          : faces.FindIndex(face => face.Id == componentFace.Id);
        return faceIndex >= 0 && faceIndex < faces.Count;
      }

      case Surface surface when component.ComponentIndexType == ComponentIndexType.BrepFace:
        faceIndex = component.Index;
        return faceIndex >= 0 && faceIndex < BrepFormFaceCount(surface);

      default:
        return false;
    }
  }

  private static int ClosestFaceIndex(GeometryBase geometry, Point3d point)
  {
    switch (geometry)
    {
      case Mesh mesh:
        return mesh.ClosestMeshPoint(point, maximumDistance: 0.0)?.FaceIndex ?? -1;

      case SubD subd:
      {
        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        var faceIndex = 0;
        foreach (var face in subd.Faces)
        {
          var distance = face.LimitSurfaceCenterPoint.DistanceToSquared(point);
          if (distance < bestDistance)
          {
            bestDistance = distance;
            bestIndex = faceIndex;
          }
          faceIndex++;
        }
        return bestIndex;
      }

      default:
      {
        using var brep = CreateBrepForm(geometry);
        if (brep != null &&
            brep.ClosestPoint(
              point,
              out _,
              out var component,
              out _,
              out _,
              maximumDistance: 0.0,
              out _) &&
            component.ComponentIndexType == ComponentIndexType.BrepFace)
          return component.Index;
        return -1;
      }
    }
  }

  private static FaceChangeResult TryApplyFaceOperation(
    RhinoDoc doc,
    FaceTarget target,
    FaceOperation operation)
  {
    var sourceObject = doc.Objects.FindId(target.ObjectId);
    if (sourceObject == null)
    {
      Log.Write("vDir", $"invalid_target object={target.ObjectId} face={target.FaceIndex}");
      return new FaceChangeResult(false, 0);
    }

    return target.GeometryKind switch
    {
      FaceGeometryKind.BrepForm => TryApplyBrepFormOperation(
        doc,
        sourceObject,
        target,
        operation),
      FaceGeometryKind.Mesh => TryApplyMeshOperation(
        doc,
        sourceObject,
        target,
        operation),
      FaceGeometryKind.SubD => TryApplySubDOperation(
        doc,
        sourceObject,
        target,
        operation),
      _ => new FaceChangeResult(false, 0)
    };
  }

  private static FaceChangeResult TryApplyBrepFormOperation(
    RhinoDoc doc,
    RhinoObject sourceObject,
    FaceTarget target,
    FaceOperation operation)
  {
    using var sourceBrep = CreateBrepForm(sourceObject.Geometry);
    if (sourceBrep == null ||
        target.FaceIndex < 0 || target.FaceIndex >= sourceBrep.Faces.Count)
    {
      Log.Write(
        "vDir",
        $"invalid_brep_form object={target.ObjectId} face={target.FaceIndex} " +
        $"type={sourceObject.Geometry.ObjectType}");
      return new FaceChangeResult(false, 0);
    }

    var beforeDirections = BrepFaceDirections(sourceBrep);

    if (!TryGetFaceIndicesToFlip(
          sourceBrep,
          target.FaceIndex,
          operation,
          out var faceIndices))
      return new FaceChangeResult(false, 0);

    if (faceIndices.Count == 0)
    {
      Log.Write(
        "vDir",
        $"no_change object={target.ObjectId} face={target.FaceIndex} " +
        $"operation={OperationLabel(operation)} faces={sourceBrep.Faces.Count}");
      return new FaceChangeResult(true, 0);
    }

    var affectedHistory = HistoryBreakWarning.CaptureAffectedRecords(
      doc,
      target.ObjectId);
    if (!HistoryBreakWarning.Confirm(doc, "vDir", affectedHistory))
      return new FaceChangeResult(false, 0);

    using var duplicate = sourceBrep.DuplicateBrep();
    if (duplicate == null)
    {
      Log.Write("vDir", $"duplicate_failed object={target.ObjectId} face={target.FaceIndex}");
      return new FaceChangeResult(false, 0);
    }

    var samplePoints = Enumerable.Range(0, sourceBrep.Faces.Count).ToDictionary(
      faceIndex => faceIndex,
      faceIndex => FaceSamplePoint(sourceBrep.Faces[faceIndex]));
    ApplyFaceFlips(duplicate, faceIndices);
    var flippedMeshCount = SynchronizeCachedFaceMeshes(
      sourceBrep,
      duplicate,
      faceIndices);

    if (!VerifyFaceDirections(
          sourceBrep,
          duplicate,
          samplePoints,
          faceIndices,
          out var preparedDots))
    {
      Log.Write(
        "vDir",
        $"prepare_failed object={target.ObjectId} face={target.FaceIndex} " +
        $"operation={OperationLabel(operation)} changed_faces={faceIndices.Count} " +
        $"normal_dots={preparedDots} faces={duplicate.Faces.Count}");
      return new FaceChangeResult(false, 0);
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
      return new FaceChangeResult(false, 0);
    }

    var storedObject = doc.Objects.FindId(target.ObjectId);
    using var storedBrep = storedObject == null
      ? null
      : CreateBrepForm(storedObject.Geometry);
    var storedDots = "missing";
    var storedValid = storedBrep != null &&
                      VerifyFaceDirections(
                        sourceBrep,
                        storedBrep,
                        samplePoints,
                        faceIndices,
                        out storedDots);
    var history = storedValid && storedBrep != null
      ? new FaceHistoryRecord(
          target.ObjectId,
          beforeDirections,
          BrepFaceDirections(storedBrep))
      : null;
    Log.Write(
      "vDir",
      $"flip object={target.ObjectId} face={target.FaceIndex} " +
      $"operation={OperationLabel(operation)} changed_faces={faceIndices.Count} " +
      $"faces={duplicate.Faces.Count} prepared_normal_dots={preparedDots} " +
      $"stored_normal_dots={storedDots} " +
      $"cached_meshes_flipped={flippedMeshCount} replaced={replaced}");
    return new FaceChangeResult(
      storedValid,
      storedValid ? faceIndices.Count : 0,
      history);
  }

  private static FaceChangeResult TryApplyMeshOperation(
    RhinoDoc doc,
    RhinoObject sourceObject,
    FaceTarget target,
    FaceOperation operation)
  {
    if (sourceObject.Geometry is not Mesh sourceMesh ||
        target.FaceIndex < 0 || target.FaceIndex >= sourceMesh.Faces.Count ||
        !TryGetMeshFaceIndicesToFlip(
          sourceMesh,
          target.FaceIndex,
          operation,
          out var faceIndices))
    {
      Log.Write(
        "vDir",
        $"invalid_mesh_target object={target.ObjectId} face={target.FaceIndex}");
      return new FaceChangeResult(false, 0);
    }

    if (faceIndices.Count == 0)
      return new FaceChangeResult(true, 0);

    var affectedHistory = HistoryBreakWarning.CaptureAffectedRecords(
      doc,
      target.ObjectId);
    if (!HistoryBreakWarning.Confirm(doc, "vDir", affectedHistory))
      return new FaceChangeResult(false, 0);

    using var duplicate = sourceMesh.DuplicateMesh();
    if (duplicate == null)
      return new FaceChangeResult(false, 0);

    var beforeDirections = MeshFaceDirections(sourceMesh);
    ApplyMeshFaceFlips(duplicate, faceIndices);
    var preparedDirections = MeshFaceDirections(duplicate);
    if (!VerifyDirectionChanges(
          beforeDirections,
          preparedDirections,
          faceIndices,
          out var preparedDots))
    {
      Log.Write(
        "vDir",
        $"mesh_prepare_failed object={target.ObjectId} face={target.FaceIndex} " +
        $"operation={OperationLabel(operation)} normal_dots={preparedDots}");
      return new FaceChangeResult(false, 0);
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

    var storedMesh = doc.Objects.FindId(target.ObjectId)?.Geometry as Mesh;
    var storedDirections = storedMesh == null
      ? null
      : MeshFaceDirections(storedMesh);
    var storedDots = "missing";
    var storedValid = storedDirections != null &&
                      VerifyDirectionChanges(
                        beforeDirections,
                        storedDirections,
                        faceIndices,
                        out storedDots);
    var history = storedValid && storedDirections != null
      ? new FaceHistoryRecord(
          target.ObjectId,
          beforeDirections,
          storedDirections)
      : null;
    Log.Write(
      "vDir",
      $"mesh_flip object={target.ObjectId} face={target.FaceIndex} " +
      $"operation={OperationLabel(operation)} changed_faces={faceIndices.Count} " +
      $"prepared_normal_dots={preparedDots} " +
      $"stored_normal_dots={(storedValid ? storedDots : "invalid")} " +
      $"replaced={replaced}");
    return new FaceChangeResult(
      replaced && storedValid,
      replaced && storedValid ? faceIndices.Count : 0,
      history);
  }

  private static FaceChangeResult TryApplySubDOperation(
    RhinoDoc doc,
    RhinoObject sourceObject,
    FaceTarget target,
    FaceOperation operation)
  {
    if (sourceObject.Geometry is not SubD sourceSubD ||
        target.FaceIndex < 0 || target.FaceIndex >= sourceSubD.Faces.Count)
      return new FaceChangeResult(false, 0);

    if (operation == FaceOperation.SingleFace)
    {
      RhinoApp.WriteLine(SubDSingleFaceMessage);
      return new FaceChangeResult(false, 0);
    }

    if (operation == FaceOperation.SameDirection)
    {
      RhinoApp.WriteLine(SubDSameDirectionMessage);
      return new FaceChangeResult(true, 0);
    }

    var affectedHistory = HistoryBreakWarning.CaptureAffectedRecords(
      doc,
      target.ObjectId);
    if (!HistoryBreakWarning.Confirm(doc, "vDir", affectedHistory))
      return new FaceChangeResult(false, 0);

    using var duplicate = sourceSubD.Duplicate() as SubD;
    if (duplicate == null || !duplicate.Flip())
      return new FaceChangeResult(false, 0);

    var beforeDirections = SubDFaceDirections(sourceSubD);
    var preparedDirections = SubDFaceDirections(duplicate);
    var allFaces = Enumerable.Range(0, sourceSubD.Faces.Count).ToHashSet();
    if (!VerifyDirectionChanges(
          beforeDirections,
          preparedDirections,
          allFaces,
          out var preparedDots))
      return new FaceChangeResult(false, 0);

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

    var storedSubD = doc.Objects.FindId(target.ObjectId)?.Geometry as SubD;
    var storedDirections = storedSubD == null
      ? null
      : SubDFaceDirections(storedSubD);
    var storedDots = "missing";
    var storedValid = storedDirections != null &&
                      VerifyDirectionChanges(
                        beforeDirections,
                        storedDirections,
                        allFaces,
                        out storedDots);
    var history = storedValid && storedDirections != null
      ? new FaceHistoryRecord(
          target.ObjectId,
          beforeDirections,
          storedDirections)
      : null;
    Log.Write(
      "vDir",
      $"subd_flip object={target.ObjectId} faces={allFaces.Count} " +
      $"prepared_normal_dots={preparedDots} " +
      $"stored_normal_dots={(storedValid ? storedDots : "invalid")} " +
      $"replaced={replaced}");
    return new FaceChangeResult(
      replaced && storedValid,
      replaced && storedValid ? allFaces.Count : 0,
      history);
  }

  private static bool TryGetMeshFaceIndicesToFlip(
    Mesh mesh,
    int referenceFaceIndex,
    FaceOperation operation,
    out HashSet<int> faceIndices)
  {
    faceIndices = [];
    if (referenceFaceIndex < 0 || referenceFaceIndex >= mesh.Faces.Count)
      return false;
    if (operation == FaceOperation.SingleFace)
    {
      faceIndices.Add(referenceFaceIndex);
      return true;
    }
    if (operation == FaceOperation.FlipAll)
    {
      for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        faceIndices.Add(faceIndex);
      return true;
    }

    return TryGetUnifiedMeshFaceIndices(
      mesh,
      referenceFaceIndex,
      out faceIndices);
  }

  private static bool TryGetUnifiedMeshFaceIndices(
    Mesh mesh,
    int referenceFaceIndex,
    out HashSet<int> faceIndices)
  {
    faceIndices = [];
    var desiredFlips = new bool?[mesh.Faces.Count];
    var pending = new Queue<int>();
    var conflicts = new HashSet<(int First, int Second)>();

    void TraverseComponent(int rootFaceIndex)
    {
      desiredFlips[rootFaceIndex] = false;
      pending.Enqueue(rootFaceIndex);
      while (pending.Count > 0)
      {
        var faceIndex = pending.Dequeue();
        var faceFlip = desiredFlips[faceIndex] ?? false;
        foreach (var neighborIndex in mesh.Faces.AdjacentFaces(faceIndex))
        {
          if (neighborIndex < 0 || neighborIndex >= mesh.Faces.Count ||
              !TryGetSharedMeshEdgeDirection(
                mesh.Faces[faceIndex],
                mesh.Faces[neighborIndex],
                out var sameDirection))
            continue;

          var neighborFlip = faceFlip ^ sameDirection;
          if (!desiredFlips[neighborIndex].HasValue)
          {
            desiredFlips[neighborIndex] = neighborFlip;
            pending.Enqueue(neighborIndex);
          }
          else if (desiredFlips[neighborIndex] != neighborFlip)
          {
            conflicts.Add((
              Math.Min(faceIndex, neighborIndex),
              Math.Max(faceIndex, neighborIndex)));
          }
        }
      }
    }

    TraverseComponent(referenceFaceIndex);
    for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
    {
      if (!desiredFlips[faceIndex].HasValue)
        TraverseComponent(faceIndex);
    }

    if (conflicts.Count > 0)
    {
      Log.Write(
        "vDir",
        $"mesh_same_direction_conflict reference={referenceFaceIndex} " +
        $"face_pairs={string.Join(',', conflicts.OrderBy(pair => pair.First).ThenBy(pair => pair.Second))}");
      return false;
    }

    for (var faceIndex = 0; faceIndex < desiredFlips.Length; faceIndex++)
    {
      if (desiredFlips[faceIndex] == true)
        faceIndices.Add(faceIndex);
    }
    return true;
  }

  private static bool TryGetSharedMeshEdgeDirection(
    MeshFace first,
    MeshFace second,
    out bool sameDirection)
  {
    foreach (var firstEdge in DirectedMeshFaceEdges(first))
    {
      foreach (var secondEdge in DirectedMeshFaceEdges(second))
      {
        if (firstEdge.From == secondEdge.From &&
            firstEdge.To == secondEdge.To)
        {
          sameDirection = true;
          return true;
        }
        if (firstEdge.From == secondEdge.To &&
            firstEdge.To == secondEdge.From)
        {
          sameDirection = false;
          return true;
        }
      }
    }

    sameDirection = false;
    return false;
  }

  private static IReadOnlyList<(int From, int To)> DirectedMeshFaceEdges(
    MeshFace face) =>
    face.IsQuad
      ? [(face.A, face.B), (face.B, face.C), (face.C, face.D), (face.D, face.A)]
      : [(face.A, face.B), (face.B, face.C), (face.C, face.A)];

  private static void ApplyMeshFaceFlips(
    Mesh mesh,
    IReadOnlySet<int> faceIndices)
  {
    foreach (var faceIndex in faceIndices)
    {
      var face = mesh.Faces[faceIndex];
      face.Flip();
      mesh.Faces.SetFace(faceIndex, face);
    }
    mesh.FaceNormals.ComputeFaceNormals();
    mesh.Normals.ComputeNormals();
  }

  private static Vector3d[] MeshFaceDirections(Mesh mesh) =>
    Enumerable.Range(0, mesh.Faces.Count)
      .Select(index => MeshFaceNormal(mesh, index))
      .ToArray();

  private static Vector3d MeshFaceNormal(Mesh mesh, int faceIndex)
  {
    if (faceIndex < 0 || faceIndex >= mesh.Faces.Count)
      return Vector3d.Unset;
    var face = mesh.Faces[faceIndex];
    Point3d a = mesh.Vertices[face.A];
    Point3d b = mesh.Vertices[face.B];
    Point3d c = mesh.Vertices[face.C];
    var normal = Vector3d.CrossProduct(b - a, c - a);
    if (!normal.Unitize() && face.IsQuad)
    {
      Point3d d = mesh.Vertices[face.D];
      normal = Vector3d.CrossProduct(c - a, d - a);
      normal.Unitize();
    }
    return normal.IsValid ? normal : Vector3d.Unset;
  }

  private static Vector3d[] SubDFaceDirections(SubD subd) =>
    subd.Faces
      .Select(face => Unitized(face.SurfaceCenterNormal))
      .ToArray();

  private static bool VerifyDirectionChanges(
    IReadOnlyList<Vector3d> before,
    IReadOnlyList<Vector3d> after,
    IReadOnlySet<int> flippedFaceIndices,
    out string normalDots)
  {
    if (before.Count != after.Count)
    {
      normalDots = $"count:{before.Count}->{after.Count}";
      return false;
    }

    var valid = true;
    var labels = new List<string>(before.Count);
    for (var faceIndex = 0; faceIndex < before.Count; faceIndex++)
    {
      var dot = UnitNormalDot(before[faceIndex], after[faceIndex]);
      var shouldFlip = flippedFaceIndices.Contains(faceIndex);
      if (!dot.HasValue ||
          (shouldFlip && dot.Value > FlippedNormalDotMaximum) ||
          (!shouldFlip && dot.Value < PreservedNormalDotMinimum))
        valid = false;
      labels.Add($"{faceIndex}:{DotText(dot)}");
    }

    normalDots = string.Join(',', labels);
    return valid;
  }

  private static Vector3d Unitized(Vector3d vector)
  {
    return vector.Unitize() ? vector : Vector3d.Unset;
  }

  private static Vector3d[] BrepFaceDirections(Brep brep) =>
    Enumerable.Range(0, brep.Faces.Count)
      .Select(index =>
        FaceNormalAtPoint(
          brep.Faces[index],
          FaceSamplePoint(brep.Faces[index])))
      .ToArray();

  private static void TrackHistory(FaceChangeResult change)
  {
    if (change.History == null || change.FlippedFaceCount == 0)
      return;

    UndoHistory.Push(change.History);
    RedoHistory.Clear();
  }

  private static bool QueueHistoryAction(
    RhinoDoc doc,
    bool redo,
    FaceOperation operation)
  {
    var history = redo ? RedoHistory : UndoHistory;
    if (history.Count == 0)
    {
      RhinoApp.WriteLine(redo
        ? "vDir: nothing to redo."
        : "vDir: nothing to undo.");
      return false;
    }

    _continuedOperation = operation;
    _pendingHistoryAction = new PendingHistoryAction(
      doc.RuntimeSerialNumber,
      redo);
    _pendingHistoryIdleHandler ??= OnHistoryActionIdle;
    RhinoApp.Idle += _pendingHistoryIdleHandler;
    return true;
  }

  private static void OnHistoryActionIdle(object? sender, EventArgs e)
  {
    if (_pendingHistoryIdleHandler != null)
    {
      RhinoApp.Idle -= _pendingHistoryIdleHandler;
      _pendingHistoryIdleHandler = null;
    }

    var request = _pendingHistoryAction;
    _pendingHistoryAction = null;
    if (request == null)
      return;

    var doc = RhinoDoc.ActiveDoc;
    if (doc == null || doc.RuntimeSerialNumber != request.DocSerial)
      return;

    var source = request.Redo ? RedoHistory : UndoHistory;
    var destination = request.Redo ? UndoHistory : RedoHistory;
    if (!source.TryPeek(out var record))
    {
      RestartAfterHistory();
      return;
    }

    var commandResult = RhinoApp.RunScript(
      request.Redo ? "_Redo" : "_Undo",
      false);
    var expected = request.Redo
      ? record.AfterDirections
      : record.BeforeDirections;
    var stateMatches = FaceDirectionsMatch(doc, record.ObjectId, expected);
    Log.Write(
      "vDir",
      $"{(request.Redo ? "redo" : "undo")} result={commandResult} " +
      $"object={record.ObjectId} state_matches={stateMatches}");
    if (stateMatches)
    {
      source.Pop();
      destination.Push(record);
      RhinoApp.WriteLine(request.Redo
        ? "vDir: direction change redone."
        : "vDir: direction change undone.");
    }
    else
    {
      RhinoApp.WriteLine(request.Redo
        ? "vDir: redo did not restore the expected face directions."
        : "vDir: undo did not restore the expected face directions.");
    }

    doc.Views.Redraw();
    RestartAfterHistory();
  }

  private static bool FaceDirectionsMatch(
    RhinoDoc doc,
    Guid objectId,
    IReadOnlyList<Vector3d> expected)
  {
    var geometry = doc.Objects.FindId(objectId)?.Geometry;
    var actual = geometry == null ? null : CaptureFaceDirections(geometry);
    if (actual == null || actual.Count != expected.Count)
      return false;

    for (var faceIndex = 0; faceIndex < expected.Count; faceIndex++)
    {
      var dot = UnitNormalDot(actual[faceIndex], expected[faceIndex]);
      if (!dot.HasValue || dot.Value < PreservedNormalDotMinimum)
        return false;
    }

    return true;
  }

  private static IReadOnlyList<Vector3d>? CaptureFaceDirections(
    GeometryBase geometry)
  {
    switch (geometry)
    {
      case Mesh mesh:
        return Enumerable.Range(0, mesh.Faces.Count)
          .Select(index => MeshFaceNormal(mesh, index))
          .ToArray();
      case SubD subd:
        return subd.Faces
          .Select(face => Unitized(face.SurfaceCenterNormal))
          .ToArray();
      default:
        using (var brep = CreateBrepForm(geometry))
          return brep == null ? null : BrepFaceDirections(brep);
    }
  }

  private static void RestartAfterHistory()
  {
    _continuingAfterHistory = true;
    _ = RhinoApp.RunScript("_vDir", false);
    _continuingAfterHistory = false;
  }

  private static bool TryGetFaceIndicesToFlip(
    Brep brep,
    int referenceFaceIndex,
    FaceOperation operation,
    out HashSet<int> faceIndices)
  {
    faceIndices = new HashSet<int>();
    if (referenceFaceIndex < 0 || referenceFaceIndex >= brep.Faces.Count)
      return false;

    if (operation == FaceOperation.SingleFace)
    {
      faceIndices.Add(referenceFaceIndex);
      return true;
    }

    if (operation == FaceOperation.FlipAll)
    {
      for (var faceIndex = 0; faceIndex < brep.Faces.Count; faceIndex++)
        faceIndices.Add(faceIndex);
      return true;
    }

    return TryGetUnifiedFaceIndices(
      brep,
      referenceFaceIndex,
      out faceIndices);
  }

  private static bool TryGetUnifiedFaceIndices(
    Brep brep,
    int referenceFaceIndex,
    out HashSet<int> faceIndices)
  {
    faceIndices = new HashSet<int>();
    var desiredFlips = new bool?[brep.Faces.Count];
    var pendingFaces = new Queue<int>();
    var conflictEdges = new HashSet<int>();
    var constraintCount = 0;

    void TraverseComponent(int rootFaceIndex)
    {
      desiredFlips[rootFaceIndex] = false;
      pendingFaces.Enqueue(rootFaceIndex);
      while (pendingFaces.Count > 0)
      {
        var faceIndex = pendingFaces.Dequeue();
        var faceFlip = desiredFlips[faceIndex] ?? false;
        foreach (var edgeIndex in brep.Faces[faceIndex].AdjacentEdges())
        {
          if (edgeIndex < 0 || edgeIndex >= brep.Edges.Count)
            continue;

          var trims = brep.Edges[edgeIndex]
            .TrimIndices()
            .Where(trimIndex =>
              trimIndex >= 0 && trimIndex < brep.Trims.Count)
            .Select(trimIndex => brep.Trims[trimIndex])
            .ToList();
          foreach (var currentTrim in trims.Where(trim =>
                     trim.Face.FaceIndex == faceIndex))
          {
            var currentTraversalReversed =
              currentTrim.IsReversed() ^
              brep.Faces[faceIndex].OrientationIsReversed;
            foreach (var neighborTrim in trims.Where(trim =>
                       trim.Face.FaceIndex != faceIndex))
            {
              var neighborFaceIndex = neighborTrim.Face.FaceIndex;
              if (neighborFaceIndex < 0 ||
                  neighborFaceIndex >= brep.Faces.Count)
                continue;

              constraintCount++;
              var neighborTraversalReversed =
                neighborTrim.IsReversed() ^
                brep.Faces[neighborFaceIndex].OrientationIsReversed;
              var neighborFlip = faceFlip ^
                (currentTraversalReversed == neighborTraversalReversed);
              if (!desiredFlips[neighborFaceIndex].HasValue)
              {
                desiredFlips[neighborFaceIndex] = neighborFlip;
                pendingFaces.Enqueue(neighborFaceIndex);
              }
              else if (desiredFlips[neighborFaceIndex] != neighborFlip)
              {
                conflictEdges.Add(edgeIndex);
              }
            }
          }
        }
      }
    }

    TraverseComponent(referenceFaceIndex);
    for (var faceIndex = 0; faceIndex < brep.Faces.Count; faceIndex++)
    {
      if (!desiredFlips[faceIndex].HasValue)
        TraverseComponent(faceIndex);
    }

    if (conflictEdges.Count > 0)
    {
      Log.Write(
        "vDir",
        $"same_direction_conflict reference={referenceFaceIndex} " +
        $"edges={string.Join(',', conflictEdges.OrderBy(index => index))}");
      return false;
    }

    for (var faceIndex = 0; faceIndex < desiredFlips.Length; faceIndex++)
    {
      if (desiredFlips[faceIndex] == true)
        faceIndices.Add(faceIndex);
    }

    Log.Write(
      "vDir",
      $"same_direction reference={referenceFaceIndex} " +
      $"flipped_faces={string.Join(',', faceIndices.OrderBy(index => index))} " +
      $"edge_constraints={constraintCount}");
    return true;
  }

  private static void ApplyFaceFlips(Brep brep, IReadOnlySet<int> faceIndices)
  {
    foreach (var faceIndex in faceIndices)
    {
      brep.Faces[faceIndex].OrientationIsReversed =
        !brep.Faces[faceIndex].OrientationIsReversed;
    }

    brep.DestroyRegionTopology();
  }

  private static int SynchronizeCachedFaceMeshes(
    Brep source,
    Brep destination,
    IReadOnlySet<int> flippedFaceIndices)
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

        if (flippedFaceIndices.Contains(faceIndex))
        {
          replacementMesh.Flip(
            vertexNormals: true,
            faceNormals: true,
            faceOrientation: true,
            ngonsBoundaryDirection: true);
        }

        if (destination.Faces[faceIndex].SetMesh(meshType, replacementMesh))
        {
          if (flippedFaceIndices.Contains(faceIndex))
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

  private static bool VerifyFaceDirections(
    Brep source,
    Brep candidate,
    IReadOnlyDictionary<int, Point3d> samplePoints,
    IReadOnlySet<int> flippedFaceIndices,
    out string normalDots)
  {
    var valid = true;
    var dotLabels = new List<string>(samplePoints.Count);
    foreach (var pair in samplePoints.OrderBy(pair => pair.Key))
    {
      if (pair.Key < 0 ||
          pair.Key >= source.Faces.Count ||
          pair.Key >= candidate.Faces.Count)
      {
        valid = false;
        dotLabels.Add($"{pair.Key}:missing");
        continue;
      }

      var beforeNormal = FaceNormalAtPoint(
        source.Faces[pair.Key],
        pair.Value);
      var afterNormal = FaceNormalAtPoint(
        candidate.Faces[pair.Key],
        pair.Value);
      var dot = UnitNormalDot(beforeNormal, afterNormal);
      var shouldFlip = flippedFaceIndices.Contains(pair.Key);
      if (!dot.HasValue ||
          (shouldFlip && dot.Value > FlippedNormalDotMaximum) ||
          (!shouldFlip && dot.Value < PreservedNormalDotMinimum))
        valid = false;
      dotLabels.Add($"{pair.Key}:{DotText(dot)}");
    }

    normalDots = string.Join(",", dotLabels);
    return valid;
  }

  private static Vector3d FaceNormalAtPoint(BrepFace face, Point3d point)
  {
    if (!point.IsValid || !face.ClosestPoint(point, out var u, out var v))
      return Vector3d.Unset;
    var normal = face.NormalAt(u, v);
    return normal.Unitize() ? normal : Vector3d.Unset;
  }

  private static Point3d FaceSamplePoint(BrepFace face) =>
    face.PointAt(face.Domain(0).Mid, face.Domain(1).Mid);

  private static double? UnitNormalDot(Vector3d first, Vector3d second) =>
    first.IsValid && second.IsValid ? first * second : null;

  private static string DotText(double? value) =>
    value.HasValue ? value.Value.ToString("G6") : "unset";

  private static FaceOperation ResolveModifierOperation(FaceOperation fallback)
  {
    return ResolveModifierOperation(
      fallback,
      IsVirtualKeyPressed(ControlVirtualKey),
      IsVirtualKeyPressed(ShiftVirtualKey));
  }

  private static FaceOperation ResolveModifierOperation(
    FaceOperation fallback,
    bool controlPressed,
    bool shiftPressed)
  {
    if (controlPressed)
      return ControlOperation(fallback);
    if (shiftPressed)
      return ShiftOperation(fallback);
    return fallback;
  }

  private static FaceOperation ShiftOperation(FaceOperation operation) =>
    operation == FaceOperation.SingleFace
      ? FaceOperation.FlipAll
      : FaceOperation.SingleFace;

  private static FaceOperation ControlOperation(FaceOperation operation) =>
    operation switch
    {
      FaceOperation.SameDirection => FaceOperation.FlipAll,
      _ => FaceOperation.SameDirection
    };

  private static bool IsVirtualKeyPressed(int virtualKey)
  {
    try
    {
      return (GetAsyncKeyState(virtualKey) & KeyPressedMask) != 0;
    }
    catch
    {
      var modifiers = System.Windows.Forms.Control.ModifierKeys;
      var key = virtualKey == ControlVirtualKey
        ? System.Windows.Forms.Keys.Control
        : System.Windows.Forms.Keys.Shift;
      return (modifiers & key) != 0;
    }
  }

  [DllImport("user32.dll")]
  private static extern short GetAsyncKeyState(int virtualKey);

  private static string CommandPrompt(
    FaceOperation selectedOperation,
    FaceOperation effectiveOperation)
  {
    var temporaryLabel = effectiveOperation == selectedOperation
      ? OperationLabel(selectedOperation)
      : $"{OperationLabel(effectiveOperation)} temporary";
    return
      $"Click face to change direction ({temporaryLabel}; " +
      $"Shift={OperationLabel(ShiftOperation(selectedOperation))}, " +
      $"Ctrl={OperationLabel(ControlOperation(selectedOperation))})";
  }

  private static string OperationLabel(FaceOperation operation) =>
    operation switch
    {
      FaceOperation.FlipAll => FlipAllOptionName,
      FaceOperation.SameDirection => SameDirectionOptionName,
      _ => SingleFaceOptionName
    };

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

    var ownerGeometry = rhinoObject.Geometry;
    if (TryGetComponentFaceIndex(ownerGeometry, componentIndex, out _))
      return true;

    return componentIndex.ComponentIndexType is
             ComponentIndexType.InvalidType or ComponentIndexType.NoType &&
           FaceCount(ownerGeometry) == 1;
  }

  private sealed class FaceHoverTracker : MouseCallback, IDisposable
  {
    private readonly RhinoDoc _doc;
    private readonly FaceHoverConduit _conduit;
    private readonly FaceOperation _operation;
    private FaceTarget? _highlighted;
    private FaceOperation _highlightedOperation;
    private FaceOperation? _operationAtClick;
    private FaceTarget? _suppressedUntilLeave;
    private bool _disposed;

    internal FaceHoverTracker(
      RhinoDoc doc,
      FaceTarget? suppressedUntilLeave,
      FaceOperation operation)
    {
      _doc = doc;
      _operation = operation;
      _highlightedOperation = operation;
      _suppressedUntilLeave = suppressedUntilLeave;
      _conduit = new FaceHoverConduit(doc) { Enabled = true };
    }

    internal FaceOperation SelectionOperation =>
      _operationAtClick ?? _highlightedOperation;

    internal void RefreshModifierOperation(FaceOperation operation)
    {
      if (operation == _highlightedOperation)
        return;

      _highlightedOperation = operation;
      _conduit.SetTarget(_highlighted, operation);
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

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
      try
      {
        if (e.MouseButton != Rhino.UI.MouseButton.Left)
        {
          base.OnMouseDown(e);
          return;
        }

        var controlPressed =
          e.CtrlKeyDown || IsVirtualKeyPressed(ControlVirtualKey);
        _operationAtClick = ResolveModifierOperation(
          _operation,
          controlPressed,
          e.ShiftKeyDown || IsVirtualKeyPressed(ShiftVirtualKey));

        if (controlPressed)
        {
          var target = PickFace(e.View, e.ViewportPoint);
          if (target.HasValue)
          {
            e.Cancel = true;
            GetBaseClass.PostCustomMessage(
              new FaceClickRequest(target.Value, _operationAtClick.Value));
            Log.Write(
              "vDir",
              $"ctrl_click object={target.Value.ObjectId} " +
              $"face={target.Value.FaceIndex} " +
              $"operation={OperationLabel(_operationAtClick.Value)}");
          }
        }
      }
      catch
      {
        _operationAtClick = ResolveModifierOperation(_operation);
      }

      base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
      var next = PickFace(e.View, e.ViewportPoint);
      var operation = ResolveModifierOperation(_operation);
      if (_suppressedUntilLeave.HasValue)
      {
        if (next == _suppressedUntilLeave)
          next = null;
        else
          _suppressedUntilLeave = null;
      }

      if (next == _highlighted && operation == _highlightedOperation)
      {
        base.OnMouseMove(e);
        return;
      }

      _highlighted = next;
      _highlightedOperation = operation;
      _conduit.SetTarget(next, operation);
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
    private readonly List<FacePreview> _faces = new();
    private Guid _objectId;
    private bool _disposed;

    internal FaceHoverConduit(RhinoDoc doc)
    {
      _doc = doc;
    }

    internal void SetTarget(FaceTarget? target, FaceOperation operation)
    {
      ClearFaces();
      _objectId = Guid.Empty;
      if (!target.HasValue)
        return;

      var sourceObject = _doc.Objects.FindId(target.Value.ObjectId);
      if (sourceObject == null)
        return;

      switch (target.Value.GeometryKind)
      {
        case FaceGeometryKind.BrepForm:
          SetBrepFormTarget(sourceObject.Geometry, target.Value, operation);
          break;
        case FaceGeometryKind.Mesh when sourceObject.Geometry is Mesh mesh:
          SetMeshTarget(mesh, target.Value, operation);
          break;
        case FaceGeometryKind.SubD when sourceObject.Geometry is SubD subd:
          SetSubDTarget(subd, target.Value, operation);
          break;
      }

      if (_faces.Count > 0)
        _objectId = target.Value.ObjectId;
    }

    private void SetBrepFormTarget(
      GeometryBase geometry,
      FaceTarget target,
      FaceOperation operation)
    {
      using var brep = CreateBrepForm(geometry);
      if (brep == null || target.FaceIndex < 0 ||
          target.FaceIndex >= brep.Faces.Count)
        return;
      if (!TryGetFaceIndicesToFlip(
            brep,
            target.FaceIndex,
            operation,
            out var faceIndices))
        faceIndices = [];

      var previewIndices = faceIndices.ToHashSet();
      previewIndices.Add(target.FaceIndex);
      foreach (var faceIndex in previewIndices.OrderBy(index => index))
      {
        var face = brep.Faces[faceIndex].DuplicateFace(duplicateMeshes: true);
        if (face == null)
          continue;

        var isAffected = faceIndices.Contains(faceIndex);
        if (isAffected)
        {
          face.Flip();
          FlipCachedPreviewMeshes(face);
        }
        _faces.Add(new FacePreview(
          face,
          faceIndex,
          faceIndex == target.FaceIndex,
          isAffected));
      }
    }

    private void SetMeshTarget(
      Mesh mesh,
      FaceTarget target,
      FaceOperation operation)
    {
      if (target.FaceIndex < 0 || target.FaceIndex >= mesh.Faces.Count)
        return;
      if (!TryGetMeshFaceIndicesToFlip(
            mesh,
            target.FaceIndex,
            operation,
            out var faceIndices))
        faceIndices = [];

      var previewIndices = faceIndices.ToHashSet();
      previewIndices.Add(target.FaceIndex);
      foreach (var faceIndex in previewIndices.OrderBy(index => index))
      {
        var faceMesh = CreateMeshFacePreview(mesh, faceIndex);
        if (faceMesh == null)
          continue;
        var isAffected = faceIndices.Contains(faceIndex);
        if (isAffected)
          faceMesh.Flip(true, true, true, true);
        _faces.Add(new FacePreview(
          faceMesh,
          faceIndex,
          faceIndex == target.FaceIndex,
          isAffected));
      }
    }

    private void SetSubDTarget(
      SubD subd,
      FaceTarget target,
      FaceOperation operation)
    {
      var faces = subd.Faces.ToList();
      if (target.FaceIndex < 0 || target.FaceIndex >= faces.Count)
        return;

      var isFlipAll = operation == FaceOperation.FlipAll;
      if (isFlipAll && subd.Duplicate() is SubD previewSubD)
      {
        previewSubD.Flip();
        _faces.Add(new FacePreview(
          previewSubD,
          -1,
          IsReference: false,
          IsAffected: true));
      }

      var referenceMesh = CreateSubDFacePreview(faces[target.FaceIndex]);
      if (referenceMesh == null)
        return;
      if (isFlipAll)
        referenceMesh.Flip(true, true, true, true);
      _faces.Add(new FacePreview(
        referenceMesh,
        target.FaceIndex,
        IsReference: true,
        IsAffected: isFlipAll));
    }

    public void Dispose()
    {
      if (_disposed)
        return;

      _disposed = true;
      Enabled = false;
      ClearFaces();
      _objectId = Guid.Empty;
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (_faces.Count == 0 || _objectId == Guid.Empty ||
          _doc.Objects.FindId(_objectId) == null)
        return;

      foreach (var preview in _faces)
      {
        var previewColor = preview.IsReference
          ? ReferenceFacePreviewColor
          : AffectedFacePreviewColor;
        var material = new DisplayMaterial(previewColor);
        using (material)
        {
          material.IsTwoSided = true;
          material.BackDiffuse = ScaleColor(
            previewColor,
            BackFacePreviewBrightness);
          material.Transparency = FacePreviewTransparency;
          material.BackTransparency = FacePreviewTransparency;
          switch (preview.Geometry)
          {
            case Brep brep:
              e.Display.DrawBrepShaded(brep, material);
              break;
            case Mesh mesh:
              e.Display.DrawMeshShaded(mesh, material);
              break;
            case SubD subd:
              e.Display.DrawSubDShaded(subd, material);
              break;
          }
        }
      }
    }

    private void ClearFaces()
    {
      foreach (var preview in _faces)
        preview.Geometry.Dispose();
      _faces.Clear();
    }

    private static Mesh? CreateMeshFacePreview(Mesh source, int faceIndex)
    {
      if (faceIndex < 0 || faceIndex >= source.Faces.Count)
        return null;
      var sourceFace = source.Faces[faceIndex];
      var sourceIndices = sourceFace.IsQuad
        ? new[] { sourceFace.A, sourceFace.B, sourceFace.C, sourceFace.D }
        : new[] { sourceFace.A, sourceFace.B, sourceFace.C };
      var preview = new Mesh();
      foreach (var sourceIndex in sourceIndices)
        preview.Vertices.Add(source.Vertices[sourceIndex]);
      if (sourceFace.IsQuad)
        preview.Faces.AddFace(0, 1, 2, 3);
      else
        preview.Faces.AddFace(0, 1, 2);
      preview.FaceNormals.ComputeFaceNormals();
      preview.Normals.ComputeNormals();
      return preview;
    }

    private static Mesh? CreateSubDFacePreview(SubDFace face)
    {
      if (face.VertexCount < 3)
        return null;
      var preview = new Mesh();
      for (var vertexIndex = 0; vertexIndex < face.VertexCount; vertexIndex++)
        preview.Vertices.Add(face.VertexAt(vertexIndex).ControlNetPoint);
      for (var vertexIndex = 1;
           vertexIndex < face.VertexCount - 1;
           vertexIndex++)
        preview.Faces.AddFace(0, vertexIndex, vertexIndex + 1);
      preview.FaceNormals.ComputeFaceNormals();
      preview.Normals.ComputeNormals();
      return preview;
    }

    private static void FlipCachedPreviewMeshes(Brep faceBrep)
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

    private static Color ScaleColor(Color color, double scale) =>
      Color.FromArgb(
        color.A,
        (int)Math.Round(Math.Clamp(color.R * scale, 0.0, 255.0)),
        (int)Math.Round(Math.Clamp(color.G * scale, 0.0, 255.0)),
        (int)Math.Round(Math.Clamp(color.B * scale, 0.0, 255.0)));
  }

  private enum FaceOperation
  {
    SingleFace,
    FlipAll,
    SameDirection
  }

  private readonly record struct FaceChangeResult(
    bool Success,
    int FlippedFaceCount,
    FaceHistoryRecord? History = null);

  private sealed record FaceHistoryRecord(
    Guid ObjectId,
    Vector3d[] BeforeDirections,
    Vector3d[] AfterDirections);

  private sealed record FaceHistoryRequest(bool Redo);

  private sealed record FaceClickRequest(
    FaceTarget Target,
    FaceOperation Operation);

  private sealed record PendingHistoryAction(uint DocSerial, bool Redo);

  private readonly record struct FacePreview(
    GeometryBase Geometry,
    int FaceIndex,
    bool IsReference,
    bool IsAffected);

  private enum FaceGeometryKind
  {
    Unsupported,
    BrepForm,
    Mesh,
    SubD
  }

  private readonly record struct FaceTarget(
    Guid ObjectId,
    FaceGeometryKind GeometryKind,
    int FaceIndex);
}
