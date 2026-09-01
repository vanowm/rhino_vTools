using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Duplicates selected object boundaries and carries source grouping to the output.
/// </summary>
public sealed class vDupBorder : vToolsCommand
{
  private const string SectionName = "vDupBorder";
  private const string GroupIfNoneKey = "groupIfNone";
  private const string RemoveSourceKey = "removeSource";
  private const string LayerKey = "layer";

  // Option defaults
  private const bool DefaultGroupIfNone = false; // true groups output with an ungrouped source; false leaves both ungrouped.
  private const bool DefaultRemoveSource = false; // true deletes the source after duplication; false keeps it.
  private const string DefaultLayer = DuplicateCommandSupport.CurrentLayerOption; // Rhino layer path or the shared current-layer sentinel.

  private static bool _groupIfNone = DefaultGroupIfNone;
  private static bool _removeSource = DefaultRemoveSource;
  private static string _layer = DefaultLayer;

  private sealed record BorderCopy(Guid SourceId, List<Curve> Curves);

  public override string EnglishName => "vDupBorder";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var layerSession = new DuplicateOutputLayerSession(doc, _layer, EnglishName);
    var selectionResult = SelectBorders(doc, mode, layerSession, out var borderCopies);
    if (selectionResult != Result.Success)
      return selectionResult;

    if (_removeSource)
    {
      var affectedHistoryRecords = borderCopies
        .SelectMany(copy =>
          HistoryBreakWarning.CaptureAffectedRecords(doc, copy.SourceId))
        .ToHashSet();
      if (!HistoryBreakWarning.Confirm(
            doc,
            "DupBorder",
            affectedHistoryRecords))
      {
        foreach (var copy in borderCopies)
          DuplicateCommandSupport.DisposeCurves(copy.Curves);
        return Result.Cancel;
      }
    }

    var createdIds = new List<Guid>();
    var completedSources = new HashSet<Guid>();
    try
    {
      foreach (var copy in borderCopies)
      {
        var sourceIds = DuplicateCommandSupport.AddCurves(
          doc,
          copy.SourceId,
          copy.Curves,
          layerSession,
          _groupIfNone);
        if (sourceIds.Count == 0)
          continue;

        createdIds.AddRange(sourceIds);
        completedSources.Add(copy.SourceId);
      }
    }
    finally
    {
      foreach (var copy in borderCopies)
        DuplicateCommandSupport.DisposeCurves(copy.Curves);
    }

    if (createdIds.Count == 0)
    {
      RhinoApp.WriteLine("vDupBorder: the selected geometry has no duplicable border.");
      return Result.Nothing;
    }

    var removedSourceCount = 0;
    if (_removeSource)
    {
      foreach (var sourceId in completedSources)
      {
        if (doc.Objects.Delete(sourceId, quiet: true))
        {
          removedSourceCount++;
        }
        else
        {
          RhinoApp.WriteLine($"vDupBorder: could not remove source {sourceId}.");
          Log.Write(EnglishName, $"could not remove source={sourceId}");
        }
      }
    }

    DuplicateCommandSupport.SelectCreated(doc, createdIds);
    Log.Write(
      EnglishName,
      $"created={createdIds.Count} sources={completedSources.Count} " +
      $"removeSource={_removeSource} removed={removedSourceCount} " +
      $"groupIfNone={_groupIfNone} " +
      $"layer={layerSession.ResolvedLayerName(doc)}");
    return Result.Success;
  }

  private static Result SelectBorders(
    RhinoDoc doc,
    RunMode mode,
    DuplicateOutputLayerSession layerSession,
    out List<BorderCopy> borderCopies)
  {
    borderCopies = [];
    using var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt("Select objects or faces whose borders to duplicate");
    getter.GeometryFilter =
      ObjectType.Surface |
      ObjectType.Brep |
      ObjectType.Extrusion |
      ObjectType.Mesh |
      ObjectType.SubD |
      ObjectType.Hatch;
    getter.SetCustomGeometryFilter(IsBorderInput);
    getter.SubObjectSelect = true;
    getter.GroupSelect = false;
    getter.EnablePreSelect(true, true);
    getter.AlreadySelectedObjectSelect = true;
    getter.EnableClearObjectsOnEntry(false);
    getter.EnableUnselectObjectsOnExit(false);
    getter.DeselectAllBeforePostSelect = false;
    getter.AcceptNothing(true);

    var preselectedWaitingForConfirmation = false;
    while (true)
    {
      getter.ClearCommandOptions();
      var groupIfNone = new OptionToggle(_groupIfNone, "No", "Yes");
      var removeSource = new OptionToggle(_removeSource, "No", "Yes");
      getter.AddOptionToggle("GroupIfNone", ref groupIfNone);
      getter.AddOptionToggle("RemoveSource", ref removeSource);
      var layerOptionIndex = getter.AddOption("Layer", layerSession.OptionLayerName);

      var getResult = getter.GetMultiple(1, 0);
      layerSession.ObserveCurrentLayer(doc);

      var optionsChanged = false;
      if (groupIfNone.CurrentValue != _groupIfNone)
      {
        _groupIfNone = groupIfNone.CurrentValue;
        optionsChanged = true;
      }
      if (removeSource.CurrentValue != _removeSource)
      {
        _removeSource = removeSource.CurrentValue;
        optionsChanged = true;
      }
      if (optionsChanged)
        SaveOptions();

      if (getter.CommandResult() != Result.Success)
        return getter.CommandResult();

      if (getResult == GetResult.Option)
      {
        if (getter.Option()?.Index == layerOptionIndex)
          PromptForLayer(doc, mode, layerSession);
        continue;
      }

      if (getResult == GetResult.Object && getter.ObjectsWerePreselected &&
          !preselectedWaitingForConfirmation)
      {
        preselectedWaitingForConfirmation = true;
        getter.EnablePreSelect(false, true);
        continue;
      }

      if (getResult is not (GetResult.Object or GetResult.Nothing))
        return getResult == GetResult.Cancel ? Result.Cancel : Result.Failure;

      var references = Enumerable.Range(0, getter.ObjectCount)
        .Select(getter.Object)
        .Where(objRef => objRef != null && objRef.ObjectId != Guid.Empty)
        .Cast<ObjRef>()
        .GroupBy(objRef => objRef.ObjectId);

      foreach (var sourceReferences in references)
      {
        var curves = DuplicateSelectedBorders(
          sourceReferences.ToList(),
          doc.ModelAbsoluteTolerance);
        if (curves.Count > 0)
          borderCopies.Add(new BorderCopy(sourceReferences.Key, curves));
      }

      return borderCopies.Count > 0 ? Result.Success : Result.Nothing;
    }
  }

  private static bool IsBorderInput(
    RhinoObject rhinoObject,
    GeometryBase geometry,
    ComponentIndex componentIndex)
  {
    if (rhinoObject == null || geometry == null)
      return false;

    return componentIndex.ComponentIndexType switch
    {
      ComponentIndexType.InvalidType or ComponentIndexType.NoType =>
        geometry is Brep or Surface or Extrusion or Mesh or SubD or Hatch,
      ComponentIndexType.BrepFace or
      ComponentIndexType.MeshFace or
      ComponentIndexType.SubdFace or
      ComponentIndexType.ExtrusionWallSurface or
      ComponentIndexType.ExtrusionCapSurface => true,
      _ => false
    };
  }

  private static List<Curve> DuplicateSelectedBorders(
    IReadOnlyList<ObjRef> references,
    double tolerance)
  {
    var wholeReference = references.FirstOrDefault(reference =>
      reference.GeometryComponentIndex.ComponentIndexType is
        ComponentIndexType.InvalidType or ComponentIndexType.NoType);
    if (wholeReference != null)
      return DuplicateWholeBorder(wholeReference.Object()?.Geometry, tolerance);

    var result = new List<Curve>();

    foreach (var reference in references)
    {
      var face = reference.Face();
      if (face == null)
        continue;

      using var faceBrep = face.DuplicateFace(duplicateMeshes: false);
      if (faceBrep == null)
        continue;

      result.AddRange(DuplicateCommandSupport.JoinBorderCurves(
        faceBrep.DuplicateNakedEdgeCurves(nakedOuter: true, nakedInner: true),
        tolerance));
    }

    var firstObject = references[0].Object();
    if (firstObject?.Geometry is Mesh mesh)
    {
      var faceIndices = references
        .Select(reference => reference.GeometryComponentIndex)
        .Where(component =>
          component.ComponentIndexType == ComponentIndexType.MeshFace)
        .Select(component => component.Index)
        .Distinct();
      result.AddRange(DuplicateMeshFaceBoundary(mesh, faceIndices, tolerance));
    }

    if (firstObject?.Geometry is SubD subD)
    {
      var selectedFaces = references
        .Select(reference => reference.SubDFace())
        .Where(face => face != null)
        .Cast<SubDFace>()
        .GroupBy(face => face.Id)
        .Select(group => group.First())
        .ToList();
      result.AddRange(DuplicateSubDFaceBoundary(selectedFaces, tolerance));
    }

    return result;
  }

  private static List<Curve> DuplicateWholeBorder(
    GeometryBase? geometry,
    double tolerance)
  {
    switch (geometry)
    {
      case Brep brep:
        return DuplicateCommandSupport.JoinBorderCurves(
          brep.DuplicateNakedEdgeCurves(nakedOuter: true, nakedInner: true),
          tolerance);
      case Extrusion extrusion:
      {
        using var brep = extrusion.ToBrep();
        return brep == null
          ? []
          : DuplicateCommandSupport.JoinBorderCurves(
            brep.DuplicateNakedEdgeCurves(nakedOuter: true, nakedInner: true),
            tolerance);
      }
      case Surface surface:
      {
        using var brep = surface.ToBrep();
        return brep == null
          ? []
          : DuplicateCommandSupport.JoinBorderCurves(
            brep.DuplicateNakedEdgeCurves(nakedOuter: true, nakedInner: true),
            tolerance);
      }
      case Mesh mesh:
      {
        var curves = (mesh.GetNakedEdges() ?? Array.Empty<Polyline>())
          .Where(polyline => polyline.IsValid && polyline.Count >= 2)
          .Select(polyline => (Curve)new PolylineCurve(polyline));
        return DuplicateCommandSupport.JoinBorderCurves(curves, tolerance);
      }
      case SubD subD:
        return DuplicateCommandSupport.JoinBorderCurves(
          subD.DuplicateEdgeCurves(
            boundaryOnly: true,
            interiorOnly: false,
            smoothOnly: false,
            sharpOnly: false,
            creaseOnly: false,
            clampEnds: true),
          tolerance);
      case Hatch hatch:
      {
        var curves = hatch.Get3dCurves(outer: true)
          .Concat(hatch.Get3dCurves(outer: false));
        return DuplicateCommandSupport.JoinBorderCurves(curves, tolerance);
      }
      default:
        return [];
    }
  }

  private static List<Curve> DuplicateMeshFaceBoundary(
    Mesh mesh,
    IEnumerable<int> faceIndices,
    double tolerance)
  {
    var selectedFaces = faceIndices
      .Where(index => index >= 0 && index < mesh.Faces.Count)
      .ToHashSet();
    var edgeIndices = new HashSet<int>();

    foreach (var faceIndex in selectedFaces)
    {
      foreach (var edgeIndex in mesh.TopologyEdges.GetEdgesForFace(faceIndex))
      {
        var selectedFaceCount = mesh.TopologyEdges
          .GetConnectedFaces(edgeIndex)
          .Count(selectedFaces.Contains);
        if (selectedFaceCount == 1)
          edgeIndices.Add(edgeIndex);
      }
    }

    var curves = edgeIndices.Select(edgeIndex =>
      (Curve)new LineCurve(mesh.TopologyEdges.EdgeLine(edgeIndex)));
    return DuplicateCommandSupport.JoinBorderCurves(curves, tolerance);
  }

  private static List<Curve> DuplicateSubDFaceBoundary(
    IReadOnlyCollection<SubDFace> selectedFaces,
    double tolerance)
  {
    var selectedFaceIds = selectedFaces.Select(face => face.Id).ToHashSet();
    var edges = new Dictionary<uint, SubDEdge>();

    foreach (var face in selectedFaces)
    {
      for (var edgeIndex = 0; edgeIndex < face.EdgeCount; edgeIndex++)
      {
        var edge = face.EdgeAt(edgeIndex);
        if (edge == null || edges.ContainsKey(edge.Id))
          continue;

        var selectedFaceCount = 0;
        for (var adjacentIndex = 0; adjacentIndex < edge.FaceCount; adjacentIndex++)
        {
          if (selectedFaceIds.Contains(edge.FaceAt(adjacentIndex).Id))
            selectedFaceCount++;
        }

        if (selectedFaceCount == 1)
          edges.Add(edge.Id, edge);
      }
    }

    var curves = edges.Values
      .Select(edge => edge.ToNurbsCurve(clampEnds: true))
      .Where(curve => curve != null)
      .Cast<Curve>();
    return DuplicateCommandSupport.JoinBorderCurves(curves, tolerance);
  }

  private static void PromptForLayer(
    RhinoDoc doc,
    RunMode mode,
    DuplicateOutputLayerSession layerSession)
  {
    if (!LayerSelector.TrySelect(
          doc,
          layerSession.OptionLayerName,
          DuplicateCommandSupport.CurrentLayerOption,
          "vDupBorder target layer",
          mode,
          allowNewLayer: false,
          out var selectedLayer))
      return;

    _layer = DuplicateCommandSupport.NormalizeLayerOption(selectedLayer);
    layerSession.ApplyOption(doc, _layer);
    SaveOptions();
  }

  private static void LoadOptions()
  {
    var options = ToolsOptionStore.Read(
      SectionName,
      section =>
      {
        var groupIfNone = ToolsOptionStore.TryGetBool(
          section, GroupIfNoneKey, out var savedGroupIfNone) && savedGroupIfNone;
        var removeSource = ToolsOptionStore.TryGetBool(
          section, RemoveSourceKey, out var savedRemoveSource) && savedRemoveSource;
        var layer = ToolsOptionStore.TryGetString(section, LayerKey, out var savedLayer)
          ? DuplicateCommandSupport.NormalizeLayerOption(savedLayer)
          : DuplicateCommandSupport.CurrentLayerOption;
        return (groupIfNone, removeSource, layer);
      });
    _groupIfNone = options.groupIfNone;
    _removeSource = options.removeSource;
    _layer = options.layer;
  }

  private static void SaveOptions()
  {
    if (!ToolsOptionStore.Update(
          SectionName,
          section =>
          {
            section[GroupIfNoneKey] = _groupIfNone;
            section[RemoveSourceKey] = _removeSource;
            section[LayerKey] = _layer;
          }))
    {
      Log.Write("vDupBorder", $"could not save options: {ToolsOptionStore.LastError}");
    }
  }
}
