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
/// Duplicates selected object edges and carries source grouping to the output.
/// </summary>
public sealed class vDupEdge : Command
{
  private const string SectionName = "vDupEdge";
  private const string GroupIfNoneKey = "groupIfNone";
  private const string LayerKey = "layer";

  private static bool _groupIfNone;
  private static string _layer = DuplicateCommandSupport.CurrentLayerOption;

  private readonly record struct EdgeCopy(Guid SourceId, Curve Curve);

  public override string EnglishName => "vDupEdge";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions();
    var layerSession = new DuplicateOutputLayerSession(doc, _layer, EnglishName);
    var selectionResult = SelectEdges(doc, mode, layerSession, out var edgeCopies);
    if (selectionResult != Result.Success)
      return selectionResult;

    var createdIds = new List<Guid>();
    try
    {
      foreach (var sourceGroup in edgeCopies.GroupBy(copy => copy.SourceId))
      {
        createdIds.AddRange(DuplicateCommandSupport.AddCurves(
          doc,
          sourceGroup.Key,
          sourceGroup.Select(copy => copy.Curve),
          layerSession,
          _groupIfNone));
      }
    }
    finally
    {
      DuplicateCommandSupport.DisposeCurves(edgeCopies.Select(copy => copy.Curve));
    }

    if (createdIds.Count == 0)
    {
      RhinoApp.WriteLine("vDupEdge: no edge curves could be created.");
      return Result.Failure;
    }

    DuplicateCommandSupport.SelectCreated(doc, createdIds);
    Log.Write(
      EnglishName,
      $"created={createdIds.Count} sources={edgeCopies.Select(copy => copy.SourceId).Distinct().Count()} " +
      $"groupIfNone={_groupIfNone} layer={layerSession.ResolvedLayerName(doc)}");
    return Result.Success;
  }

  private static Result SelectEdges(
    RhinoDoc doc,
    RunMode mode,
    DuplicateOutputLayerSession layerSession,
    out List<EdgeCopy> edgeCopies)
  {
    edgeCopies = [];
    using var getter = new GetObject();
    getter.EnableTransparentCommands(true);
    getter.SetCommandPrompt("Select edges to duplicate");
    getter.GeometryFilter = ObjectType.EdgeFilter | ObjectType.MeshEdge;
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
      getter.AddOptionToggle("GroupIfNone", ref groupIfNone);
      var layerOptionIndex = getter.AddOption("Layer", layerSession.OptionLayerName);

      var getResult = getter.GetMultiple(1, 0);
      layerSession.ObserveCurrentLayer(doc);

      if (groupIfNone.CurrentValue != _groupIfNone)
      {
        _groupIfNone = groupIfNone.CurrentValue;
        SaveOptions();
      }

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

      var seen = new HashSet<(Guid SourceId, ComponentIndexType Type, int Index)>();
      for (var i = 0; i < getter.ObjectCount; i++)
      {
        var objRef = getter.Object(i);
        if (objRef == null || objRef.ObjectId == Guid.Empty)
          continue;

        var component = objRef.GeometryComponentIndex;
        if (!seen.Add((objRef.ObjectId, component.ComponentIndexType, component.Index)))
          continue;

        var curve = DuplicateEdgeCurve(objRef);
        if (curve != null && curve.IsValid)
          edgeCopies.Add(new EdgeCopy(objRef.ObjectId, curve));
        else
          curve?.Dispose();
      }

      return edgeCopies.Count > 0 ? Result.Success : Result.Nothing;
    }
  }

  private static Curve? DuplicateEdgeCurve(ObjRef objRef)
  {
    var brepEdge = objRef.Edge();
    if (brepEdge != null)
      return brepEdge.DuplicateCurve();

    var subDEdge = objRef.SubDEdge();
    if (subDEdge != null)
      return subDEdge.ToNurbsCurve(clampEnds: true);

    var component = objRef.GeometryComponentIndex;
    var mesh = objRef.Mesh();
    if (mesh != null &&
        component.ComponentIndexType == ComponentIndexType.MeshTopologyEdge &&
        component.Index >= 0 && component.Index < mesh.TopologyEdges.Count)
    {
      return new LineCurve(mesh.TopologyEdges.EdgeLine(component.Index));
    }

    return objRef.Curve()?.DuplicateCurve();
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
          "vDupEdge target layer",
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
        var layer = ToolsOptionStore.TryGetString(section, LayerKey, out var savedLayer)
          ? DuplicateCommandSupport.NormalizeLayerOption(savedLayer)
          : DuplicateCommandSupport.CurrentLayerOption;
        return (groupIfNone, layer);
      });
    _groupIfNone = options.groupIfNone;
    _layer = options.layer;
  }

  private static void SaveOptions()
  {
    if (!ToolsOptionStore.Update(
          SectionName,
          section =>
          {
            section[GroupIfNoneKey] = _groupIfNone;
            section[LayerKey] = _layer;
          }))
    {
      Log.Write("vDupEdge", $"could not save options: {ToolsOptionStore.LastError}");
    }
  }
}
