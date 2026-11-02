using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools;

/// <summary>Shared whole-object and joined-curve-segment selection for geometry transforms.</summary>
internal static class ObjectTransformSelection
{
  internal sealed record Item(Guid ObjectId, int[] Segments, uint RuntimeSerial)
  {
    internal bool WholeObject => Segments.Length == 0; // An empty segment list selects the complete parent object.
  }

  internal static List<Item> FromReferences(RhinoDoc doc, IEnumerable<ObjRef> references)
  {
    var whole = new HashSet<Guid>();
    var segments = new Dictionary<Guid, HashSet<int>>();
    foreach (var reference in references)
    {
      if (reference.ObjectId == Guid.Empty) continue;
      var component = reference.GeometryComponentIndex;
      if (component.Index < 0) whole.Add(reference.ObjectId);
      else if (component.ComponentIndexType == ComponentIndexType.PolycurveSegment)
      {
        if (!segments.TryGetValue(reference.ObjectId, out var selected)) segments[reference.ObjectId] = selected = new();
        selected.Add(component.Index);
      }
    }
    return whole.Concat(segments.Keys).Distinct().Select(id =>
      new Item(id, whole.Contains(id) ? [] : segments[id].Order().ToArray(), doc.Objects.FindId(id)?.RuntimeSerialNumber ?? 0)).ToList();
  }

  internal static List<Item> FromDocument(RhinoDoc doc)
  {
    var result = new List<Item>();
    foreach (var obj in doc.Objects.GetObjectList(new ObjectEnumeratorSettings
      { SelectedObjectsFilter = true, SubObjectSelected = true, IncludeLights = false, IncludeGrips = false }))
    {
      if (obj.IsSelected(false) != 0) result.Add(new(obj.Id, [], obj.RuntimeSerialNumber));
      else
      {
        var segments = (obj.GetSelectedSubObjects() ?? []).Where(component =>
          component.ComponentIndexType == ComponentIndexType.PolycurveSegment && component.Index >= 0)
          .Select(component => component.Index).Distinct().Order().ToArray();
        if (segments.Length > 0) result.Add(new(obj.Id, segments, obj.RuntimeSerialNumber));
      }
    }
    return result;
  }

  internal sealed class GeometryCopies : IDisposable
  {
    internal List<GeometryBase> Geometries { get; } = new();
    public void Dispose() { foreach (var geometry in Geometries) geometry.Dispose(); Geometries.Clear(); }
  }

  internal static GeometryCopies CopyGeometry(RhinoDoc doc, IEnumerable<Item> items)
  {
    var copies = new GeometryCopies();
    try
    {
      foreach (var item in items)
      {
        var geometry = doc.Objects.FindId(item.ObjectId)?.Geometry;
        if (geometry == null) throw new InvalidOperationException("Selected object no longer exists.");
        if (item.WholeObject) copies.Geometries.Add(geometry.Duplicate());
        else
        {
          if (geometry is not Curve curve) throw new InvalidOperationException("Selected curve segments no longer exist.");
          var segments = curve.DuplicateSegments();
          try
          {
            if (item.Segments.Any(index => index < 0 || index >= segments.Length))
              throw new InvalidOperationException("Selected curve segments changed.");
            foreach (int index in item.Segments) copies.Geometries.Add(segments[index].DuplicateCurve());
          }
          finally { foreach (var segment in segments) segment.Dispose(); }
        }
      }
      return copies;
    }
    catch { copies.Dispose(); throw; }
  }

  internal static (List<Curve> Selected, List<Curve> Remaining) CurveRuns(Curve curve, IReadOnlyCollection<int> selected)
  {
    var segments = curve.DuplicateSegments();
    var moved = new List<Curve>();
    var kept = new List<Curve>();
    try
    {
      var indices = selected.ToHashSet();
      if (indices.Count == 0 || indices.Any(index => index < 0 || index >= segments.Length))
        throw new InvalidOperationException("Invalid curve segment selection.");
      int first = 0;
      if (curve.IsClosed && indices.Count < segments.Length)
        first = Enumerable.Range(0, segments.Length).First(index => indices.Contains(index) != indices.Contains((index + segments.Length - 1) % segments.Length));
      var run = new List<Curve>();
      bool runSelected = indices.Contains(first);
      void FinishRun()
      {
        if (run.Count == 0) return;
        Curve result;
        if (run.Count == 1) result = run[0].DuplicateCurve();
        else
        {
          var polycurve = new PolyCurve();
          try
          {
            foreach (var segment in run)
              if (!polycurve.Append(segment)) throw new InvalidOperationException("Cannot preserve the selected curve run.");
            result = polycurve;
          }
          catch { polycurve.Dispose(); throw; }
        }
        (runSelected ? moved : kept).Add(result);
        run.Clear();
      }
      for (int offset = 0; offset < segments.Length; offset++)
      {
        int index = (first + offset) % segments.Length;
        bool isSelected = indices.Contains(index);
        if (isSelected != runSelected) { FinishRun(); runSelected = isSelected; }
        run.Add(segments[index]);
      }
      FinishRun();
      return (moved, kept);
    }
    catch { foreach (var piece in moved.Concat(kept)) piece.Dispose(); throw; }
    finally { foreach (var segment in segments) segment.Dispose(); }
  }

  private sealed class Change(RhinoObject source) : IDisposable
  {
    internal Guid Id = source.Id;
    internal uint Serial = source.RuntimeSerialNumber;
    internal GeometryBase Before = source.Geometry.Duplicate();
    internal ObjectAttributes Attributes = source.Attributes.Duplicate();
    internal List<Curve> Moved = new();
    internal List<Curve> Kept = new();
    internal bool SegmentOnly;
    internal bool Replaced;
    internal Guid TransformedId;
    public void Dispose() { Before.Dispose(); foreach (var piece in Moved.Concat(Kept)) piece.Dispose(); }
  }

  internal static bool TryTransform(RhinoDoc doc, IReadOnlyList<Item> items, Transform transform, bool copy,
    out List<Guid> sourceIds, out List<Guid> outputIds)
  {
    sourceIds = new(); outputIds = new();
    var changes = new List<Change>();
    var added = new List<Guid>();
    try
    {
      foreach (var item in items)
      {
        var obj = doc.Objects.FindId(item.ObjectId);
        if (obj == null || obj.IsLocked || obj.IsReference || !obj.IsSelectable(true, false, false, false))
          throw new InvalidOperationException("A selected object is unavailable for transformation.");
        var change = new Change(obj);
        changes.Add(change);
        if (item.WholeObject) continue;
        if (obj.RuntimeSerialNumber != item.RuntimeSerial || obj.Geometry is not Curve curve)
          throw new InvalidOperationException("Selected curve segments changed during point picking.");
        var runs = CurveRuns(curve, item.Segments);
        change.Moved = runs.Selected; change.Kept = runs.Remaining; change.SegmentOnly = true;
        foreach (var piece in change.Moved)
          if (!piece.Transform(transform) || !piece.IsValid) throw new InvalidOperationException("Cannot transform the selected curve segments.");
      }
      // Add detached runs before touching any parent curve. Failed additions
      // therefore leave the original joined curves intact.
      foreach (var change in changes.Where(change => change.SegmentOnly))
      {
        foreach (var piece in change.Moved)
        {
          var attributes = change.Attributes.Duplicate();
          if (copy) attributes.RemoveFromAllGroups();
          Guid id = doc.Objects.AddCurve(piece, attributes);
          if (id == Guid.Empty) throw new InvalidOperationException("Cannot add oriented curve segments.");
          added.Add(id); sourceIds.Add(change.Id); outputIds.Add(id);
        }
        if (!copy)
          foreach (var piece in change.Kept.Skip(1))
          {
            Guid id = doc.Objects.AddCurve(piece, change.Attributes.Duplicate());
            if (id == Guid.Empty) throw new InvalidOperationException("Cannot preserve unselected curve segments.");
            added.Add(id);
          }
      }
      foreach (var change in changes)
      {
        if (change.SegmentOnly)
        {
          if (copy) continue;
          if (change.Kept.Count > 0)
          {
            if (!doc.Objects.Replace(change.Id, change.Kept[0])) throw new InvalidOperationException("Cannot preserve the source curve.");
          }
          else if (!doc.Objects.Delete(change.Id, true)) throw new InvalidOperationException("Cannot remove the selected source segments.");
          change.Replaced = true;
        }
        else
        {
          change.TransformedId = doc.Objects.Transform(change.Id, transform, !copy);
          if (change.TransformedId == Guid.Empty) throw new InvalidOperationException("Cannot transform a selected object.");
          if (copy) added.Add(change.TransformedId); else change.Replaced = true;
          sourceIds.Add(change.Id); outputIds.Add(change.TransformedId);
        }
      }
      return outputIds.Count > 0;
    }
    catch (Exception ex)
    {
      foreach (var id in added.AsEnumerable().Reverse()) doc.Objects.Delete(id, true);
      foreach (var change in changes.Where(change => change.Replaced).Reverse())
      {
        if (change.TransformedId != Guid.Empty && change.TransformedId != change.Id) doc.Objects.Delete(change.TransformedId, true);
        if (doc.Objects.FindId(change.Id) == null) doc.Objects.Undelete(change.Serial);
        bool restored = doc.Objects.Replace(change.Id, change.Before, false);
        restored &= doc.Objects.ModifyAttributes(change.Id, change.Attributes, true);
        if (!restored) Log.Write("ObjectTransformSelection", "Rollback failed source={0}", change.Id);
      }
      sourceIds.Clear(); outputIds.Clear();
      Log.Write("ObjectTransformSelection", "Segment/object transform rejected: {0}", ex.Message);
      return false;
    }
    finally { foreach (var change in changes) change.Dispose(); }
  }
}
