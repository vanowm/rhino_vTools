using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

public sealed partial class vNotches
{
  private sealed record OffsetSplitContact(int CurveIndex, double First, double Second);
  private sealed record OffsetCurveHit(RhinoObject Object, double Parameter, double Distance);

  private sealed class OffsetSplitPlan(Guid targetId, bool preserveSourceId)
  {
    public Guid TargetId { get; } = targetId;
    public bool PreserveSourceId { get; } = preserveSourceId;
    public List<OffsetSplitContact> Contacts { get; } = [];
  }

  private sealed class OffsetSplitEdit(DocObjectSnapshot original, bool selected)
  {
    public DocObjectSnapshot Original { get; } = original;
    public bool Selected { get; } = selected;
    public List<DocObjectSnapshot> Pieces { get; } = [];
    public List<Guid> LivePieceIds { get; } = [];
    public Guid RestoredOriginalId { get; set; }
    public bool PreservesOriginalId { get; set; }
  }

  private static HashSet<Guid> OffsetSourceCurveIds(NotchSession session,
    IEnumerable<OffsetSplitEdit>? edits = null)
  {
    var ids = session.PerCurveSourceIds.SelectMany(sourceIds => sourceIds).ToHashSet();
    foreach (var edit in edits ?? session.NotchRecords.SelectMany(record => record.OffsetSplits))
      if (ids.Remove(edit.Original.Attributes.ObjectId))
        ids.UnionWith(edit.LivePieceIds);
    return ids;
  }

  private static List<OffsetSplitPlan> PrepareOffsetSplits(
    RhinoDoc doc, NotchSession session, IReadOnlyList<double> lengths, bool[] curveEnabled,
    double notchLength, double notchOffset, string notchType, double notchWidth,
    Point3d? cursorPoint, KinkTangentChoice referenceKinkChoice,
    IEnumerable<OffsetSplitEdit>? priorEdits = null)
  {
    var plans = new Dictionary<Guid, OffsetSplitPlan>();
    if (session.NotchTrim == NotchTrimMode.No ||
        CanonicalNotchType(notchType) is not ("V" or "U" or OpenVNotchType))
      return [];

    var sourceIds = OffsetSourceCurveIds(session, priorEdits);
    bool onSource = notchOffset <= 0.0;
    var settings = new ObjectEnumeratorSettings
    {
      IncludeLights = false,
      IncludeGrips = false,
      IncludePhantoms = false,
      NormalObjects = true,
      HiddenObjects = false,
      LockedObjects = false
    };
    var candidates = doc.Objects.GetObjectList(settings)
      .Where(obj => obj?.Geometry is Curve && sourceIds.Contains(obj.Id) == onSource &&
                    obj.Attributes.GetUserString(NotchDataPrefix + "version") == null)
      .ToArray();
    double contactTolerance = Math.Max(
      doc.ModelAbsoluteTolerance * OffsetSplitContactToleranceScale,
      RhinoMath.ZeroTolerance * OffsetSplitZeroToleranceScale);
    int referenceIndex = cursorPoint.HasValue ? session.PreviewRefCurveIndex : -1;
    KinkTangentChoice? kinkChoice = referenceKinkChoice == KinkTangentChoice.Default
      ? null : referenceKinkChoice;

    for (int curveIndex = 0; curveIndex < session.Curves.Count; curveIndex++)
    {
      if (curveIndex >= curveEnabled.Length || !curveEnabled[curveIndex] ||
          curveIndex >= lengths.Count)
        continue;
      ResolvePlacementCurve(session, curveIndex, lengths[curveIndex], kinkChoice,
        out var placementCurve, out double placementLength);
      string side = PlacementCurveSide(session, curveIndex, lengths[curveIndex], kinkChoice);
      bool bothSides = PlacementCurveBothSides(
        session, curveIndex, lengths[curveIndex], kinkChoice);
      var sides = bothSides
        ? new[] { side, side == "Left" ? "Right" : "Left" }
        : new[] { side };
      foreach (var currentSide in sides)
      {
        var geometry = NotchGeometry(
          placementCurve, placementLength, notchLength, notchOffset,
          currentSide, notchType, notchWidth,
          curveIndex == referenceIndex ? cursorPoint : null, kinkChoice);
        if (geometry == null || geometry.Count == 0)
          continue;
        try
        {
          var first = geometry[0].PointAtStart;
          var second = geometry[^1].PointAtEnd;
          var firstHits = new List<OffsetCurveHit>();
          var secondHits = new List<OffsetCurveHit>();
          foreach (var candidate in candidates)
          {
            if (candidate.Geometry is not Curve target ||
                !target.ClosestPoint(first, out double firstParameter) ||
                !target.ClosestPoint(second, out double secondParameter))
              continue;
            double firstDistance = first.DistanceTo(target.PointAt(firstParameter));
            double secondDistance = second.DistanceTo(target.PointAt(secondParameter));
            if (firstDistance <= contactTolerance)
              firstHits.Add(new OffsetCurveHit(candidate, firstParameter, firstDistance));
            if (secondDistance <= contactTolerance)
              secondHits.Add(new OffsetCurveHit(candidate, secondParameter, secondDistance));
          }
          firstHits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
          secondHits.Sort((a, b) => a.Distance.CompareTo(b.Distance));

          OffsetCurveHit? firstHit = null;
          OffsetCurveHit? secondHit = null;
          double sharedFirst = 0.0;
          double sharedSecond = 0.0;
          double bestDistance = double.MaxValue;
          foreach (var left in firstHits)
          {
            foreach (var right in secondHits)
            {
              if (left.Object.Id != right.Object.Id ||
                  Math.Abs(left.Parameter - right.Parameter) <= RhinoMath.ZeroTolerance)
                continue;
              double distance = left.Distance + right.Distance;
              if (distance >= bestDistance)
                continue;
              firstHit = left;
              secondHit = right;
              bestDistance = distance;
            }
          }
          if (firstHit != null && secondHit != null)
          {
            AddOffsetSplitContact(plans, firstHit.Object.Id, onSource,
              new OffsetSplitContact(curveIndex, firstHit.Parameter, secondHit.Parameter));
            continue;
          }

          bestDistance = double.MaxValue;
          foreach (var left in firstHits)
          {
            foreach (var right in secondHits)
            {
              if (left.Object.Id == right.Object.Id ||
                  left.Object.Geometry is not Curve leftCurve ||
                  right.Object.Geometry is not Curve rightCurve ||
                  !TryFindSharedOffsetEndpoint(leftCurve, rightCurve, contactTolerance,
                    out double leftEnd, out double rightEnd))
                continue;
              double distance = left.Distance + right.Distance;
              if (distance >= bestDistance)
                continue;
              firstHit = left;
              secondHit = right;
              sharedFirst = leftEnd;
              sharedSecond = rightEnd;
              bestDistance = distance;
            }
          }
          if (firstHit == null || secondHit == null)
          {
            Log.Write("vNotches", $"offset split skipped curve={curveIndex + 1}: " +
              $"firstHits={firstHits.Count} secondHits={secondHits.Count} " +
              "no shared offset curve or connected pair");
            continue;
          }
          AddOffsetSplitContact(plans, firstHit.Object.Id, onSource,
            new OffsetSplitContact(curveIndex, firstHit.Parameter, sharedFirst));
          AddOffsetSplitContact(plans, secondHit.Object.Id, onSource,
            new OffsetSplitContact(curveIndex, sharedSecond, secondHit.Parameter));
          Log.Write("vNotches", $"offset split paired curve={curveIndex + 1} " +
            $"targets={firstHit.Object.Id},{secondHit.Object.Id}");
        }
        finally
        {
          foreach (var curve in geometry)
            curve.Dispose();
        }
      }
    }
    return plans.Values.ToList();
  }

  private static void AddOffsetSplitContact(Dictionary<Guid, OffsetSplitPlan> plans,
    Guid targetId, bool preserveSourceId, OffsetSplitContact contact)
  {
    if (!plans.TryGetValue(targetId, out var plan))
    {
      plan = new OffsetSplitPlan(targetId, preserveSourceId);
      plans.Add(targetId, plan);
    }
    plan.Contacts.Add(contact);
  }

  private static bool TryFindSharedOffsetEndpoint(Curve first, Curve second,
    double tolerance, out double firstParameter, out double secondParameter)
  {
    firstParameter = 0.0;
    secondParameter = 0.0;
    if (first.IsClosed || second.IsClosed)
      return false;

    var firstEnds = new[] { first.Domain.T0, first.Domain.T1 };
    var secondEnds = new[] { second.Domain.T0, second.Domain.T1 };
    double bestDistance = tolerance;
    bool found = false;
    foreach (double firstEnd in firstEnds)
    {
      foreach (double secondEnd in secondEnds)
      {
        double distance = first.PointAt(firstEnd).DistanceTo(second.PointAt(secondEnd));
        if (distance > bestDistance)
          continue;
        firstParameter = firstEnd;
        secondParameter = secondEnd;
        bestDistance = distance;
        found = true;
      }
    }
    return found;
  }

  private static bool TryApplyOffsetSplits(
    RhinoDoc doc, IReadOnlyList<OffsetSplitPlan> plans,
    IReadOnlyList<(Guid notch, Guid? label)> placements,
    NotchTrimMode mode, string layerName, List<OffsetSplitEdit> edits)
  {
    foreach (var plan in plans)
    {
      var contacts = plan.Contacts
        .Where(contact => contact.CurveIndex < placements.Count &&
                          placements[contact.CurveIndex].notch != Guid.Empty)
        .ToArray();
      if (contacts.Length == 0)
        continue;
      if (TrySplitOffsetCurve(doc, plan.TargetId, contacts, mode, layerName,
            plan.PreserveSourceId, out var edit))
      {
        if (edit != null)
          edits.Add(edit);
        continue;
      }
      RestoreOriginalOffsetCurves(doc, edits);
      edits.Clear();
      return false;
    }
    return true;
  }

  private static bool RefreshOffsetSplits(RhinoDoc doc, NotchSession session)
  {
    if (session.NotchRecords.Any(record =>
          record.DetachedNotchIds.Count > 0 && record.OffsetSplits.Count > 0))
    {
      RhinoApp.WriteLine("vNotches: finish and restart to change the side of notches whose source curves were removed from this selection.");
      return false;
    }
    if (!session.NotchRecords.Any(record => record.NotchEnabled &&
          CanonicalNotchType(record.NotchType) is ("V" or "U" or OpenVNotchType)))
      return true;
    var oldEdits = session.NotchRecords
      .Select(record => record.OffsetSplits.ToList())
      .ToArray();
    for (int index = session.NotchRecords.Count - 1; index >= 0; index--)
      RestoreOriginalOffsetCurves(doc, oldEdits[index]);

    var replacementEdits = new List<List<OffsetSplitEdit>>();
    bool completed = true;
    for (int index = 0; index < session.NotchRecords.Count; index++)
    {
      var record = session.NotchRecords[index];
      var edits = new List<OffsetSplitEdit>();
      replacementEdits.Add(edits);
      if (!record.NotchEnabled || index >= session.PlacementIds.Count)
        continue;
      var enabled = Enumerable.Range(0, session.Curves.Count)
        .Select(curveIndex => curveIndex < record.CurveEnabled.Count &&
                              record.CurveEnabled[curveIndex])
        .ToArray();
      var plans = PrepareOffsetSplits(
        doc, session, record.LengthsFromStart, enabled,
        record.NotchLength, record.NotchOffset, record.NotchType, record.NotchWidth,
        null, record.KinkChoice, replacementEdits.SelectMany(previous => previous));
      var affectedHistory = plans
        .SelectMany(plan => HistoryBreakWarning.CaptureAffectedRecords(doc, plan.TargetId))
        .ToHashSet();
      if (!HistoryBreakWarning.Confirm(doc, "vNotches", affectedHistory))
      {
        completed = false;
        break;
      }
      var ids = session.PlacementIds[index]
        .Select(id => (notch: id, label: (Guid?)null))
        .ToArray();
      if (!TryApplyOffsetSplits(doc, plans, ids, session.NotchTrim,
            session.NotchTrimLayer, edits))
      {
        completed = false;
        break;
      }
    }
    if (completed)
    {
      for (int index = 0; index < session.NotchRecords.Count; index++)
        session.NotchRecords[index].OffsetSplits = replacementEdits[index];
      doc.Views.Redraw();
      return true;
    }

    for (int index = replacementEdits.Count - 1; index >= 0; index--)
      RestoreOriginalOffsetCurves(doc, replacementEdits[index]);
    foreach (var old in oldEdits)
      RestoreSplitOffsetPieces(doc, old);
    RhinoApp.WriteLine("vNotches: previous offset-curve splits restored.");
    doc.Views.Redraw();
    return false;
  }

  private static bool TrySplitOffsetCurve(
    RhinoDoc doc, Guid targetId, IReadOnlyList<OffsetSplitContact> contacts,
    NotchTrimMode mode, string layerName, bool preserveSourceId, out OffsetSplitEdit? edit)
  {
    edit = null;
    var target = doc.Objects.FindId(targetId);
    if (target?.Geometry is not Curve original ||
        CaptureDocObject(doc, targetId) is not { } snapshot)
      return false;

    double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    var parameters = contacts.SelectMany(contact => new[] { contact.First, contact.Second })
      .Where(parameter =>
        original.PointAt(parameter).DistanceTo(original.PointAtStart) > tolerance &&
        original.PointAt(parameter).DistanceTo(original.PointAtEnd) > tolerance)
      .Distinct()
      .OrderBy(parameter => parameter)
      .ToArray();
    var pieces = parameters.Length > 0
      ? original.Split(parameters)
      : new[] { original.DuplicateCurve() };
    if (pieces == null || pieces.Length == 0)
    {
      Log.Write("vNotches", $"offset split skipped target={targetId}: no interior pieces");
      if (pieces != null)
        foreach (var piece in pieces) piece.Dispose();
      return true;
    }

    var between = new bool[pieces.Length];
    double totalLength = original.IsClosed ? original.GetLength() : 0.0;
    for (int pieceIndex = 0; pieceIndex < pieces.Length; pieceIndex++)
    {
      if (!original.ClosestPoint(pieces[pieceIndex].PointAtNormalizedLength(0.5),
            out double middleParameter))
        continue;
      foreach (var contact in contacts)
      {
        double first = Math.Min(contact.First, contact.Second);
        double last = Math.Max(contact.First, contact.Second);
        bool inside = middleParameter > first && middleParameter < last;
        if (original.IsClosed)
        {
          using var span = original.Trim(first, last);
          if (span != null && span.GetLength() > totalLength * 0.5)
            inside = !inside;
        }
        if (inside)
        {
          between[pieceIndex] = true;
          break;
        }
      }
    }
    if (!between.Any(value => value))
    {
      foreach (var piece in pieces) piece.Dispose();
      Log.Write("vNotches", $"offset split skipped target={targetId}: no segment between legs");
      return true;
    }

    edit = new OffsetSplitEdit(snapshot, target.IsSelected(false) != 0);
    int referenceLayer = mode == NotchTrimMode.Split
      ? UzipCommon.EnsureLayer(doc, layerName) : -1;
    int preservedPiece = preserveSourceId ? Array.FindIndex(between, value => !value) : -1;
    if (preserveSourceId && preservedPiece < 0 && mode == NotchTrimMode.Split)
      preservedPiece = 0;
    edit.PreservesOriginalId = preservedPiece >= 0;
    try
    {
      if (preservedPiece < 0 && !doc.Objects.Delete(targetId, quiet: true))
        throw new InvalidOperationException("could not remove the offset curve");
      for (int pieceIndex = 0; pieceIndex < pieces.Length; pieceIndex++)
      {
        if (mode == NotchTrimMode.Trim && between[pieceIndex])
          continue;
        var attributes = snapshot.Attributes.Duplicate();
        attributes.ObjectId = pieceIndex == preservedPiece ? targetId : Guid.NewGuid();
        if (mode == NotchTrimMode.Split && between[pieceIndex])
          attributes.LayerIndex = referenceLayer;
        Guid id;
        if (pieceIndex == preservedPiece)
        {
          // Keep the selected source identity while its other pieces remain traceable.
          if (!doc.Objects.Replace(targetId, pieces[pieceIndex]) ||
              !doc.Objects.ModifyAttributes(targetId, attributes, quiet: true))
            throw new InvalidOperationException("could not replace the source-curve segment");
          id = targetId;
        }
        else
          id = doc.Objects.AddCurve(pieces[pieceIndex], attributes);
        if (id == Guid.Empty)
          throw new InvalidOperationException("could not add an offset-curve segment");
        edit.LivePieceIds.Add(id);
        if (edit.Selected)
          doc.Objects.FindId(id)?.Select(true);
        if (CaptureDocObject(doc, id) is not { } pieceSnapshot)
          throw new InvalidOperationException("could not capture an offset-curve segment");
        edit.Pieces.Add(pieceSnapshot);
      }
      Log.Write("vNotches", $"offset {mode.ToString().ToLowerInvariant()} " +
        $"target={targetId} pieces={pieces.Length} between={between.Count(value => value)} " +
        $"retained={edit.LivePieceIds.Count} onSource={preserveSourceId}");
      return true;
    }
    catch (Exception ex)
    {
      Log.Write("vNotches", $"offset split failed target={targetId}: {ex}");
      RestoreOriginalOffsetCurves(doc, [edit]);
      edit = null;
      return false;
    }
    finally
    {
      foreach (var piece in pieces) piece.Dispose();
    }
  }

  private static void RestoreOriginalOffsetCurves(
    RhinoDoc doc, IReadOnlyList<OffsetSplitEdit> edits)
  {
    for (int index = edits.Count - 1; index >= 0; index--)
    {
      var edit = edits[index];
      foreach (var id in edit.LivePieceIds)
        if ((!edit.PreservesOriginalId || id != edit.Original.Attributes.ObjectId) &&
            doc.Objects.FindId(id) != null)
          doc.Objects.Delete(id, quiet: true);
      edit.LivePieceIds.Clear();
      if (edit.PreservesOriginalId)
        edit.RestoredOriginalId = RestoreOffsetCurveSnapshot(doc, edit.Original);
      else if (doc.Objects.FindId(edit.Original.Attributes.ObjectId) == null)
        edit.RestoredOriginalId = RestoreDocObject(doc, edit.Original);
      else
        edit.RestoredOriginalId = edit.Original.Attributes.ObjectId;
      if (edit.Selected && edit.RestoredOriginalId != Guid.Empty)
        doc.Objects.FindId(edit.RestoredOriginalId)?.Select(true);
    }
  }

  private static void RestoreSplitOffsetPieces(
    RhinoDoc doc, IReadOnlyList<OffsetSplitEdit> edits)
  {
    foreach (var edit in edits)
    {
      var originalId = edit.RestoredOriginalId != Guid.Empty
        ? edit.RestoredOriginalId : edit.Original.Attributes.ObjectId;
      if ((!edit.PreservesOriginalId || originalId != edit.Original.Attributes.ObjectId) &&
          doc.Objects.FindId(originalId) != null)
        doc.Objects.Delete(originalId, quiet: true);
      edit.RestoredOriginalId = Guid.Empty;
      edit.LivePieceIds.Clear();
      foreach (var piece in edit.Pieces)
      {
        var id = edit.PreservesOriginalId && piece.Attributes.ObjectId == edit.Original.Attributes.ObjectId
          ? RestoreOffsetCurveSnapshot(doc, piece) : RestoreDocObject(doc, piece);
        if (id != Guid.Empty)
        {
          edit.LivePieceIds.Add(id);
          if (edit.Selected)
            doc.Objects.FindId(id)?.Select(true);
        }
      }
    }
  }

  private static Guid RestoreOffsetCurveSnapshot(RhinoDoc doc, DocObjectSnapshot snapshot)
  {
    Guid id = snapshot.Attributes.ObjectId;
    if (doc.Objects.FindId(id) == null)
      return RestoreDocObject(doc, snapshot);
    return snapshot.Geometry is Curve curve && doc.Objects.Replace(id, curve) &&
           doc.Objects.ModifyAttributes(id, snapshot.Attributes.Duplicate(), quiet: true)
      ? id : Guid.Empty;
  }

  private static void DeletePlacedNotches(
    RhinoDoc doc, IReadOnlyList<(Guid notch, Guid? label)> placements)
  {
    foreach (var placement in placements)
    {
      if (placement.notch != Guid.Empty)
        DeleteComponentObjects(doc, placement.notch);
      if (placement.label.HasValue)
        DeleteComponentObjects(doc, placement.label.Value);
    }
  }
}
