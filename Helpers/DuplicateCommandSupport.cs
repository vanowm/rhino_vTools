using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

internal static class DuplicateCommandSupport
{
  internal const string CurrentLayerOption = "*Current*";

  internal static string NormalizeLayerOption(string? layerName)
  {
    var value = layerName?.Trim();
    return string.IsNullOrWhiteSpace(value) || value is "." or "*" ||
           string.Equals(
             value,
             CurrentLayerOption,
             StringComparison.OrdinalIgnoreCase)
      ? CurrentLayerOption
      : value;
  }

  internal static List<Guid> AddCurves(
    RhinoDoc doc,
    Guid sourceId,
    IEnumerable<Curve> curves,
    DuplicateOutputLayerSession layerSession,
    bool groupIfNone)
  {
    var validCurves = curves
      .Where(curve => curve != null && curve.IsValid)
      .ToList();
    var ids = new List<Guid>();
    foreach (var curve in validCurves)
    {
      var id = doc.Objects.AddCurve(curve, layerSession.CreateAttributes(doc));
      if (id != Guid.Empty)
        ids.Add(id);
    }

    if (ids.Count != validCurves.Count)
    {
      foreach (var id in ids)
        doc.Objects.Delete(id, quiet: true);
      return [];
    }

    if (ids.Count > 0)
      ApplySourceGroups(doc, sourceId, ids, groupIfNone);

    return ids;
  }

  internal static List<Curve> JoinBorderCurves(
    IEnumerable<Curve> sourceCurves,
    double tolerance)
  {
    var source = sourceCurves.ToList();
    var curves = source.Where(curve => curve != null && curve.IsValid).ToList();
    foreach (var invalidCurve in source.Except(curves))
      invalidCurve?.Dispose();
    if (curves.Count <= 1)
      return curves;

    var joined = Curve.JoinCurves(curves, tolerance, preserveDirection: false);
    if (joined is not { Length: > 0 })
      return curves;

    foreach (var curve in curves)
      curve.Dispose();
    return joined.ToList();
  }

  internal static void DisposeCurves(IEnumerable<Curve> curves)
  {
    foreach (var curve in curves)
      curve?.Dispose();
  }

  internal static void SelectCreated(RhinoDoc doc, IEnumerable<Guid> ids)
  {
    doc.Objects.UnselectAll();
    foreach (var id in ids)
      doc.Objects.Select(id);
    doc.Views.Redraw();
  }

  private static void ApplySourceGroups(
    RhinoDoc doc,
    Guid sourceId,
    IReadOnlyCollection<Guid> outputIds,
    bool groupIfNone)
  {
    var source = doc.Objects.FindId(sourceId);
    if (source == null)
      return;

    var groupIndices = source.Attributes.GetGroupList() ?? Array.Empty<int>();
    if (groupIndices.Length > 0)
    {
      foreach (var groupIndex in groupIndices.Distinct())
        doc.Groups.AddToGroup(groupIndex, outputIds);
      return;
    }

    if (groupIfNone)
      doc.Groups.Add(new[] { sourceId }.Concat(outputIds));
  }
}

internal sealed class DuplicateOutputLayerSession
{
  private readonly string _logTag;
  private int _observedCurrentLayerIndex;
  private int? _externalLayerOverride;

  internal DuplicateOutputLayerSession(
    RhinoDoc doc,
    string optionLayerName,
    string logTag)
  {
    OptionLayerName = DuplicateCommandSupport.NormalizeLayerOption(optionLayerName);
    _observedCurrentLayerIndex = doc.Layers.CurrentLayerIndex;
    _logTag = logTag;
  }

  internal string OptionLayerName { get; private set; }

  internal void ApplyOption(RhinoDoc doc, string optionLayerName)
  {
    OptionLayerName = DuplicateCommandSupport.NormalizeLayerOption(optionLayerName);
    _externalLayerOverride = null;
    _observedCurrentLayerIndex = doc.Layers.CurrentLayerIndex;
  }

  internal void ObserveCurrentLayer(RhinoDoc doc)
  {
    var currentLayerIndex = doc.Layers.CurrentLayerIndex;
    if (currentLayerIndex == _observedCurrentLayerIndex)
      return;

    _observedCurrentLayerIndex = currentLayerIndex;
    _externalLayerOverride = IsUsableLayer(doc, currentLayerIndex)
      ? currentLayerIndex
      : null;

    var layerName = _externalLayerOverride.HasValue
      ? doc.Layers[_externalLayerOverride.Value].FullPath
      : "<invalid>";
    Log.Write(_logTag, $"current layer changed; session target={layerName}");
  }

  internal ObjectAttributes CreateAttributes(RhinoDoc doc)
  {
    return new ObjectAttributes { LayerIndex = ResolveLayerIndex(doc) };
  }

  internal string ResolvedLayerName(RhinoDoc doc)
  {
    var index = ResolveLayerIndex(doc);
    return IsUsableLayer(doc, index)
      ? doc.Layers[index].FullPath
      : "<invalid>";
  }

  private int ResolveLayerIndex(RhinoDoc doc)
  {
    ObserveCurrentLayer(doc);

    if (_externalLayerOverride.HasValue &&
        IsUsableLayer(doc, _externalLayerOverride.Value))
    {
      return _externalLayerOverride.Value;
    }

    if (OptionLayerName != DuplicateCommandSupport.CurrentLayerOption)
    {
      var configuredIndex = doc.Layers.FindByFullPath(
        OptionLayerName,
        RhinoMath.UnsetIntIndex);
      if (IsUsableLayer(doc, configuredIndex))
        return configuredIndex;
    }

    var currentLayerIndex = doc.Layers.CurrentLayerIndex;
    return IsUsableLayer(doc, currentLayerIndex) ? currentLayerIndex : 0;
  }

  private static bool IsUsableLayer(RhinoDoc doc, int layerIndex)
  {
    if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
      return false;

    var layer = doc.Layers[layerIndex];
    return layer != null && !layer.IsDeleted;
  }
}
