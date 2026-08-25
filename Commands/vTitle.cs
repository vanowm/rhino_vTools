// vTitle — place annotation text with optional bounding box.
// Preview moves live with cursor; text/options persist across sessions.
using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vTitle : Command
{
  // Option defaults
  private const string DefaultText = ""; // Plain title text; empty prompts for text.
  private const double DefaultSize = 20.0; // Text height in model units; greater than zero.
  private const double DefaultPadding = 50.0; // Box padding as a percentage of text height; zero or greater.
  private const bool DefaultBox = true; // true draws a padded title box; false creates text only.
  private const string DefaultLayer = "Reference"; // Rhino layer name or full layer path.
  private const string CurrentLayerOption = "*Current*"; // Sentinel that resolves title output to Rhino's current layer.
  private static readonly Color PreviewTextColor = Color.FromArgb(220, 255, 255, 80); // ARGB color for live title text previews.
  private static readonly Color PreviewFallbackBackgroundColor = Color.FromArgb(200, 60, 60, 60); // ARGB background for dot-based preview fallback.
  private static readonly Color PreviewBoxColor = Color.FromArgb(180, 180, 220, 60); // ARGB color for live title-frame previews.
  private static readonly Color HoverHighlightColor = Color.FromArgb(220, 255, 220, 40); // ARGB color for hovered existing-title frames.
  private static readonly Color PreviewFallbackTextColor = Color.Yellow; // ARGB foreground for text and dot preview fallbacks.
  private const int PreviewLineThicknessOffset = 1; // Relative display thickness offset used for frame and hover lines; integer zero or greater.
  internal const string TitleFlagKey = "vTitle"; // User-string key identifying title annotations; non-empty text.
  internal const string TitleFlagValue = "1"; // User-string value identifying title annotations; non-empty text.
  internal const string PaddingUserStringKey = "vTitlePadding"; // User-string key storing padding percentage; non-empty text.
  private const string FrameFlagKey = "vTitleFrame"; // User-string key identifying the generated title frame; non-empty text.
  private const string FrameFlagValue = "1"; // User-string value identifying the generated title frame; non-empty text.

  private const string SectionName = "vTitle";
  private const string KeyText    = "text";
  private const string KeySize    = "size";
  private const string KeyPadding = "padding";
  private const string KeyBox     = "box";
  private const string KeyLayer   = "layer";

  private static string _text = DefaultText;
  private static double _size = DefaultSize;
  private static double _padding = DefaultPadding; // percent per side
  private static bool _box = DefaultBox;
  private static string _layer = DefaultLayer;

  // ── Active placement tracking (for live update) ───────────────────────
  private static Guid _activeTextId  = Guid.Empty;
  private static Guid _activeBoxId   = Guid.Empty;
  private static int  _activeGrpIdx  = -1;
  private static bool _internalReplace = false;
  private static int _suspendAutoBoxSyncDepth = 0;

  // ── External-edit event subscription ───────────────────────────────
  private static readonly System.Collections.Generic.HashSet<PendingBoxUpdate>
    _pendingBoxUpdates = new();

  private readonly record struct PendingBoxUpdate(
    uint DocumentSerialNumber,
    Guid ObjectId);

  static vTitle()
  {
    RhinoDoc.ReplaceRhinoObject += OnRhinoObjectReplaced;
    RhinoDoc.AddRhinoObject += OnRhinoObjectAdded;
  }

  public override string EnglishName => "vTitle";

  // ── Entry point ───────────────────────────────────────────────────────
  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadSettings();
    _activeTextId  = Guid.Empty;
    _activeBoxId   = Guid.Empty;
    _activeGrpIdx  = -1;

    while (true)
    {
      var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.SetCommandPrompt("Title center");

      int idxText    = gp.AddOption("Text",    string.IsNullOrEmpty(_text) ? "-" : _text);
      int idxSize    = gp.AddOption("Size",    $"{_size:G}");
      int idxPadding = gp.AddOption("Padding", $"{_padding:G}");
      int idxLayer   = gp.AddOption("Layer",   _layer);
      var optBox     = new OptionToggle(_box, "Off", "On");
      gp.AddOptionToggle("Box", ref optBox);
      gp.AcceptNothing(false);

      gp.DynamicDraw += (_, e) =>
      {
        DrawPreview(e, _text, _size, _padding, _box);
        DrawHoverHighlight(doc, e);
      };

      var res = gp.Get();
      _box = optBox.CurrentValue;

      if (gp.CommandResult() == Result.Cancel)
      {
        SelectGroup(doc, _activeGrpIdx, false);
        doc.Views.Redraw();
        break;
      }

      if (res == GetResult.Option)
      {
        var opt = gp.Option();
        if (opt == null) { UpdateActive(doc); SaveSettings(); continue; }

        if (opt.Index == idxText)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("Title text (spaces allowed)");
          gs.SetDefaultString(_text);
          gs.AcceptNothing(true);
          gs.GetLiteralString();
          if (gs.CommandResult() != Result.Cancel)
          {
            string s = gs.StringResult()?.Trim() ?? "";
            if (!string.IsNullOrEmpty(s)) _text = s;
          }
          UpdateActive(doc); SaveSettings();
          continue;
        }

        if (opt.Index == idxSize)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("Text size");
          gs.SetDefaultString($"{_size:G}");
          gs.AcceptNothing(true);
          if (gs.Get() == GetResult.String &&
              double.TryParse(gs.StringResult().Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double sv) && sv > 0)
            _size = sv;
          UpdateActive(doc); SaveSettings();
          continue;
        }

        if (opt.Index == idxPadding)
        {
          var gs = new GetString();
          gs.SetCommandPrompt("Padding % per side");
          gs.SetDefaultString($"{_padding:G}");
          gs.AcceptNothing(true);
          if (gs.Get() == GetResult.String &&
              double.TryParse(gs.StringResult().Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out double pv) && pv >= 0)
            _padding = pv;
          UpdateActive(doc); SaveSettings();
          continue;
        }

        if (opt.Index == idxLayer)
        {
          if (LayerSelector.TrySelect(
                doc,
                _layer,
                CurrentLayerOption,
                "vTitle target layer",
                mode,
                allowNewLayer: true,
                out var selectedLayer))
          {
            _layer = NormalizeLayerOption(selectedLayer);
            SaveSettings();
          }
          continue;
        }

        // Box toggle
        UpdateActive(doc); SaveSettings();
        continue;
      }

      if (res == GetResult.Point)
      {
        var pt = gp.Point();
        var hit = FindVTitleAt(doc, pt);

        if (hit.HasValue)
        {
          if (_activeGrpIdx >= 0 && _activeGrpIdx != hit.Value.grpIdx)
            SelectGroup(doc, _activeGrpIdx, false);

          _activeTextId  = hit.Value.textId;
          _activeBoxId   = hit.Value.boxId;
          _activeGrpIdx  = hit.Value.grpIdx;

          if (doc.Objects.FindId(_activeTextId)?.Geometry is TextEntity et)
          {
            _text = et.PlainText ?? _text;
            _size = et.TextHeight;
            _box  = _activeBoxId != Guid.Empty;
            SaveSettings();
          }
          SelectGroup(doc, _activeGrpIdx, true);
          doc.Views.Redraw();
        }
        else
        {
          SelectGroup(doc, _activeGrpIdx, false);
          if (string.IsNullOrEmpty(_text)) continue;
          PlaceTitle(doc, pt, _text, _size, _padding, _box);
          // New placement does NOT enter edit mode — reset so UpdateActive is a no-op.
          _activeTextId  = Guid.Empty;
          _activeBoxId   = Guid.Empty;
          _activeGrpIdx  = -1;
          doc.Views.Redraw();
        }
      }
    }

    return Result.Success;
  }

  // ── Dynamic preview ───────────────────────────────────────────────────

  private static void DrawPreview(GetPointDrawEventArgs e,
    string text, double size, double padding, bool box)
  {
    if (string.IsNullOrEmpty(text))
      return;

    var pt = e.CurrentPoint;
    var cpNative = e.Viewport.GetConstructionPlane();
    var xAxis = cpNative.Plane.XAxis;
    var yAxis = cpNative.Plane.YAxis;
    var textPlane = new Plane(pt, xAxis, yAxis);

    using var te = new TextEntity
    {
      Plane = textPlane,
      PlainText = text,
      TextHeight = size,
      Justification = TextJustification.MiddleCenter,
      DimensionScale = 1.0,
    };

    try { te.DrawForward = false; } catch { }

    try
    {
      e.Display.DrawAnnotation(
        te, PreviewTextColor);
    }
    catch
    {
      try
      {
        e.Display.Draw3dText(
          new Text3d(text, textPlane, size), PreviewFallbackTextColor);
      }
      catch
      {
        e.Display.DrawDot(
          pt, text,
          PreviewFallbackBackgroundColor,
          PreviewFallbackTextColor);
      }
    }

    if (!box)
      return;

    using var frame = CreateFrameForText(te, padding);
    PreviewDisplay.DrawCurve(
      e.Display,
      frame,
      PreviewBoxColor,
      PreviewLineThicknessOffset);
  }

  private static void PlaceTitle(RhinoDoc doc, Point3d center,
    string text, double size, double padding, bool box)
  {
    var vp = doc.Views.ActiveView?.ActiveViewport;
    var cp = vp?.GetConstructionPlane();
    var xAxis = cp?.Plane.XAxis ?? Vector3d.XAxis;
    var yAxis = cp?.Plane.YAxis ?? Vector3d.YAxis;
    var textPlane = new Plane(center, xAxis, yAxis);

    var te = new TextEntity
    {
      Plane          = textPlane,
      PlainText      = text,
      TextHeight     = size,
      Justification  = TextJustification.MiddleCenter,
      DimensionScale = 1.0,
    };

    int layerIdx = GetTargetLayerIndex(doc);
    var attr = new ObjectAttributes();
    attr.SetUserString(TitleFlagKey, TitleFlagValue);
    attr.SetUserString(
      PaddingUserStringKey,
      padding.ToString(CultureInfo.InvariantCulture));
    attr.LayerIndex = layerIdx;
    _activeTextId  = doc.Objects.AddText(te, attr);
    _activeBoxId   = Guid.Empty;
    _activeGrpIdx  = -1;
    if (_activeTextId == Guid.Empty) return;

    if (box)
    {
      var titleObj = doc.Objects.FindId(_activeTextId);
      if (titleObj?.Geometry is TextEntity placedText)
      {
        using var frame = CreateFrameForTitlePreview(
          titleObj, placedText);
        if (frame != null)
        {
          var boxAttr = new ObjectAttributes { LayerIndex = layerIdx };
          boxAttr.SetUserString(FrameFlagKey, FrameFlagValue);
          _activeBoxId = doc.Objects.AddCurve(frame, boxAttr);
        }
      }
    }

    // Group text + box together
    var toGroup = new System.Collections.Generic.List<Guid> { _activeTextId };
    if (_activeBoxId != Guid.Empty) toGroup.Add(_activeBoxId);
    if (toGroup.Count > 1)
    {
      _activeGrpIdx = doc.Groups.Add();
      foreach (var id in toGroup)
      {
        var obj2 = doc.Objects.FindId(id);
        if (obj2 == null) continue;
        var grpAttr = obj2.Attributes.Duplicate();
        grpAttr.AddToGroup(_activeGrpIdx);
        doc.Objects.ModifyAttributes(obj2, grpAttr, true);
      }
    }
  }

  // ── Find existing vTitle at a point ───────────────────────────────────

  private static (Guid textId, Guid boxId, int grpIdx)? FindVTitleAt(
    RhinoDoc doc, Point3d pt)
  {
    foreach (var obj in doc.Objects)
    {
      if (obj.IsLocked || obj.IsHidden) continue;
      if (obj.Geometry is not TextEntity te) continue;
      if (obj.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue) continue;
      if (!GetTitleHalfExtents(doc, obj, te, out double hw, out double hh)) continue;

      var rel = pt - te.Plane.Origin;
      if (Math.Abs(rel * te.Plane.XAxis) > hw) continue;
      if (Math.Abs(rel * te.Plane.YAxis) > hh) continue;

      var grpList = obj.Attributes.GetGroupList();
      int grpIdx = grpList?.Length > 0 ? grpList[0] : -1;
      Guid boxId = Guid.Empty;
      if (grpIdx >= 0)
      {
        foreach (var other in doc.Objects)
        {
      if (other.Id == obj.Id || other.Geometry is not PolylineCurve) continue;
          var gl = other.Attributes.GetGroupList();
          if (gl != null && Array.IndexOf(gl, grpIdx) >= 0) { boxId = other.Id; break; }
        }
      }
      return (obj.Id, boxId, grpIdx);
    }
    return null;
  }

  /// <summary>Gets half-extents of a title's box in text-plane coordinates.</summary>
  private static bool GetTitleHalfExtents(RhinoDoc doc, RhinoObject textRhObj,
    TextEntity te, out double hw, out double hh)
  {
    hw = hh = 0;
    if (te == null) return false;

    // Try the associated box curve first
    var grpList = textRhObj.Attributes.GetGroupList();
    int grpIdx = grpList?.Length > 0 ? grpList[0] : -1;
    if (grpIdx >= 0)
    {
      var center = te.Plane.Origin;
      foreach (var obj in doc.Objects)
      {
        if (obj.Geometry is not PolylineCurve poly) continue;
        var gl = obj.Attributes.GetGroupList();
        if (gl == null || Array.IndexOf(gl, grpIdx) < 0) continue;
        double maxU = 0, maxV = 0;
        foreach (var corner in poly.ToPolyline())
        {
          var r = corner - center;
          maxU = Math.Max(maxU, Math.Abs(r * te.Plane.XAxis));
          maxV = Math.Max(maxV, Math.Abs(r * te.Plane.YAxis));
        }
        if (maxU > 0 && maxV > 0) { hw = maxU; hh = maxV; return true; }
      }
    }

    // Fallback: approximate from stored padding
    double padding = DefaultPadding;
    if (double.TryParse(textRhObj.Attributes.GetUserString(PaddingUserStringKey),
          NumberStyles.Any, CultureInfo.InvariantCulture, out double sp))
      padding = sp;
    var (tw, th) = ApproxBounds(te.PlainText ?? "", te.TextHeight);
    double padFactor = 1.0 + padding * 2.0 / 100.0;
    hw = tw * padFactor / 2.0;
    hh = th * padFactor / 2.0;
    return true;
  }

  // ── Select / deselect a group ─────────────────────────────────────────

  private static void SelectGroup(RhinoDoc doc, int grpIdx, bool select)
  {
    if (grpIdx < 0) return;
    foreach (var obj in doc.Objects)
    {
      var gl = obj.Attributes.GetGroupList();
      if (gl != null && Array.IndexOf(gl, grpIdx) >= 0)
        obj.Select(select);
    }
  }

  // ── Hover highlight ───────────────────────────────────────────────────────

  private static void DrawHoverHighlight(RhinoDoc doc, GetPointDrawEventArgs e)
  {
    var pt = e.CurrentPoint;
    foreach (var obj in doc.Objects)
    {
      if (obj.IsLocked || obj.IsHidden) continue;
      if (obj.Geometry is not TextEntity te) continue;
      if (obj.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue) continue;
      if (!GetTitleHalfExtents(doc, obj, te, out double hw, out double hh)) continue;

      var rel = pt - te.Plane.Origin;
      if (Math.Abs(rel * te.Plane.XAxis) > hw) continue;
      if (Math.Abs(rel * te.Plane.YAxis) > hh) continue;

      var o  = te.Plane.Origin;
      var xa = te.Plane.XAxis;
      var ya = te.Plane.YAxis;
      PreviewDisplay.DrawLine(e.Display, o + xa*(-hw) + ya*(-hh), o + xa*(hw) + ya*(-hh), HoverHighlightColor, PreviewLineThicknessOffset);
      PreviewDisplay.DrawLine(e.Display, o + xa*( hw) + ya*(-hh), o + xa*(hw) + ya*( hh), HoverHighlightColor, PreviewLineThicknessOffset);
      PreviewDisplay.DrawLine(e.Display, o + xa*( hw) + ya*( hh), o + xa*(-hw) + ya*( hh), HoverHighlightColor, PreviewLineThicknessOffset);
      PreviewDisplay.DrawLine(e.Display, o + xa*(-hw) + ya*( hh), o + xa*(-hw) + ya*(-hh), HoverHighlightColor, PreviewLineThicknessOffset);
    }
  }

  // ── External-edit handler ───────────────────────────────────────────────

  /// <summary>
  /// Temporarily suppresses vTitle's automatic frame-sync handler.
  /// Commands such as vMirror can then update the title and explicitly ask
  /// vTitle to rebuild its frame inside the command's own undo record.
  /// </summary>
  internal static IDisposable SuspendAutomaticBoxSync()
  {
    _suspendAutoBoxSyncDepth++;
    return new AutoBoxSyncScope();
  }

  /// <summary>
  /// Rebuilds one vTitle frame immediately using the current TextEntity and
  /// current vTitlePadding user string.
  /// </summary>
  internal static void SyncBoxForTitleNow(RhinoDoc doc, Guid textId)
  {
    if (doc == null || textId == Guid.Empty)
      return;
    if (doc.UndoActive || doc.RedoActive)
      return;

    _pendingBoxUpdates.Remove(
      new PendingBoxUpdate(doc.RuntimeSerialNumber, textId));
    UpdateBoxForTitle(doc, textId);
  }

  private sealed class AutoBoxSyncScope : IDisposable
  {
    private bool _disposed;

    public void Dispose()
    {
      if (_disposed)
        return;

      _disposed = true;
      if (_suspendAutoBoxSyncDepth > 0)
        _suspendAutoBoxSyncDepth--;
    }
  }

  private static void OnRhinoObjectReplaced(object? sender,
    RhinoReplaceObjectEventArgs e)
  {
    if (_internalReplace || _suspendAutoBoxSyncDepth > 0)
      return;

    var oldObj = e.OldRhinoObject;
    var newObj = e.NewRhinoObject;
    if (oldObj?.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue)
      return;
    if (oldObj.Geometry is not TextEntity oldText ||
        newObj?.Geometry is not TextEntity newText)
      return;

    var doc = newObj.Document ?? oldObj.Document;
    if (doc == null || doc.UndoActive || doc.RedoActive)
      return;

    // Transform-only replacements move the grouped frame with the text.
    // Rebuild only when the text bounds can actually change.
    bool textChanged = !string.Equals(
      oldText.PlainText, newText.PlainText, StringComparison.Ordinal);
    bool heightChanged =
      Math.Abs(oldText.TextHeight - newText.TextHeight) >
      RhinoMath.ZeroTolerance;
    if (!textChanged && !heightChanged)
      return;

    _pendingBoxUpdates.Add(
      new PendingBoxUpdate(doc.RuntimeSerialNumber, e.ObjectId));
  }

  private static void OnRhinoObjectAdded(object? sender,
    RhinoObjectEventArgs e)
  {
    if (_internalReplace || _suspendAutoBoxSyncDepth > 0)
      return;

    var obj = e.TheObject;
    var doc = obj?.Document;
    if (doc == null ||
        !_pendingBoxUpdates.Remove(
          new PendingBoxUpdate(doc.RuntimeSerialNumber, e.ObjectId)))
      return;
    if (obj?.Geometry is not TextEntity ||
        obj.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue ||
        doc.UndoActive ||
        doc.RedoActive)
      return;

    // The replacement TextEntity is now committed to the object table.
    // Rebuild its frame in the same Rhino operation; no separate undo record.
    UpdateBoxForTitle(doc, e.ObjectId);
    doc.Views.Redraw();
  }

  private static void UpdateBoxForTitle(RhinoDoc doc, Guid textId)
  {
    if (textId == Guid.Empty)
      return;

    var textObj = doc.Objects.FindId(textId);
    if (textObj?.Geometry is not TextEntity text ||
        textObj.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue)
      return;

    var groups = textObj.Attributes.GetGroupList();
    int groupIndex = groups?.Length > 0 ? groups[0] : -1;
    if (groupIndex < 0)
      return;

    using var frame = CreateFrameForTitlePreview(textObj, text);
    if (frame == null)
      return;

    var groupedFrames = doc.Objects
      .Where(o => o.Geometry is PolylineCurve)
      .Where(o =>
      {
        var gl = o.Attributes.GetGroupList();
        return gl != null &&
               Array.IndexOf(gl, groupIndex) >= 0;
      })
      .ToList();

    var taggedFrames = groupedFrames
      .Where(o => o.Attributes.GetUserString(FrameFlagKey) == FrameFlagValue)
      .ToList();
    var frameObjects = taggedFrames.Count > 0
      ? taggedFrames
      : groupedFrames.Count == 1
        ? groupedFrames
        : [];

    foreach (var frameObject in frameObjects)
    {
      _internalReplace = true;
      try
      {
        if (!doc.Objects.Replace(frameObject.Id, frame))
          continue;

        var replacedFrame = doc.Objects.FindId(frameObject.Id);
        if (replacedFrame != null &&
            replacedFrame.Attributes.GetUserString(FrameFlagKey) != FrameFlagValue)
        {
          var attributes = replacedFrame.Attributes.Duplicate();
          attributes.SetUserString(FrameFlagKey, FrameFlagValue);
          doc.Objects.ModifyAttributes(replacedFrame, attributes, true);
        }
      }
      finally
      {
        _internalReplace = false;
      }
    }
  }

  // ── Live update of last placed group ─────────────────────────────────

  private static void UpdateActive(RhinoDoc doc)
  {
    if (_activeTextId == Guid.Empty) return;
    var textObj = doc.Objects.FindId(_activeTextId);
    if (textObj?.Geometry is not TextEntity oldTe) { _activeTextId = Guid.Empty; return; }

    // Update text content and size
    using var newTe = (TextEntity)oldTe.Duplicate();
    newTe.PlainText  = _text;
    newTe.TextHeight = _size;
    _internalReplace = true;
    try
    {
      doc.Objects.Replace(_activeTextId, newTe);
    }
    finally
    {
      _internalReplace = false;
    }
    // Keep padding in sync on the text object's attributes
    var tobj0 = doc.Objects.FindId(_activeTextId);
    if (tobj0 != null)
    {
      var ta0 = tobj0.Attributes.Duplicate();
      ta0.SetUserString(
        PaddingUserStringKey,
        _padding.ToString(CultureInfo.InvariantCulture));
      doc.Objects.ModifyAttributes(tobj0, ta0, true);
    }

    if (_box)
    {
      var updatedTitleObj = doc.Objects.FindId(_activeTextId);
      if (updatedTitleObj?.Geometry is not TextEntity updatedText)
        return;

      using var newCurve = CreateFrameForTitlePreview(
        updatedTitleObj, updatedText);
      if (newCurve == null)
        return;

      if (_activeBoxId != Guid.Empty)
      {
        doc.Objects.Replace(_activeBoxId, newCurve);
        var existingFrame = doc.Objects.FindId(_activeBoxId);
        if (existingFrame != null &&
            existingFrame.Attributes.GetUserString(FrameFlagKey) != FrameFlagValue)
        {
          var frameAttributes = existingFrame.Attributes.Duplicate();
          frameAttributes.SetUserString(FrameFlagKey, FrameFlagValue);
          doc.Objects.ModifyAttributes(existingFrame, frameAttributes, true);
        }
      }
      else
      {
        // Box was off — create it and add to the existing group
        var frameAttributes = new ObjectAttributes
        {
          LayerIndex = updatedTitleObj.Attributes.LayerIndex
        };
        frameAttributes.SetUserString(FrameFlagKey, FrameFlagValue);
        _activeBoxId = doc.Objects.AddCurve(newCurve, frameAttributes);
        if (_activeBoxId != Guid.Empty)
        {
          if (_activeGrpIdx < 0)
          {
            // Promote to a group now that there are two objects
            _activeGrpIdx = doc.Groups.Add();
            var tobj = doc.Objects.FindId(_activeTextId);
            if (tobj != null)
            {
              var ta = tobj.Attributes.Duplicate(); ta.AddToGroup(_activeGrpIdx);
              doc.Objects.ModifyAttributes(tobj, ta, true);
            }
          }
          var bobj = doc.Objects.FindId(_activeBoxId);
          if (bobj != null)
          {
            var ba = bobj.Attributes.Duplicate(); ba.AddToGroup(_activeGrpIdx);
            doc.Objects.ModifyAttributes(bobj, ba, true);
          }
        }
      }
    }
    else if (_activeBoxId != Guid.Empty)
    {
      doc.Objects.Delete(_activeBoxId, true);
      _activeBoxId = Guid.Empty;
    }

    doc.Views.Redraw();
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a vTitle frame for the final TextEntity using the title's CURRENT
  /// vTitlePadding user string. vMirror preview and real vTitle sync both call
  /// this exact path.
  /// </summary>
  internal static PolylineCurve? CreateFrameForTitlePreview(
    RhinoObject titleObject,
    TextEntity text)
  {
    if (titleObject == null || text == null)
      return null;
    if (titleObject.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue)
      return null;

    double padding = GetPaddingPercent(titleObject);
    return CreateFrameForText(text, padding);
  }

  internal static bool IsTitleFrame(RhinoObject candidate) =>
    candidate?.Geometry is PolylineCurve &&
    candidate.Attributes.GetUserString(FrameFlagKey) == FrameFlagValue;

  /// <summary>
  /// Builds a preview frame for replacement text from a FRESH TextEntity.
  /// Mutating PlainText on a duplicated TextEntity can leave stale annotation
  /// layout data until Rhino inserts it into the document; a fresh entity
  /// avoids that mismatch and matches vTitle's normal creation path.
  /// </summary>
  internal static PolylineCurve? CreateFrameForTitlePreview(
    RhinoObject titleObject,
    TextEntity sourceText,
    string previewText)
  {
    if (titleObject == null || sourceText == null)
      return null;
    if (titleObject.Attributes.GetUserString(TitleFlagKey) != TitleFlagValue)
      return null;

    using var freshText = new TextEntity
    {
      Plane = sourceText.Plane,
      PlainText = previewText ?? string.Empty,
      TextHeight = sourceText.TextHeight,
      Justification = sourceText.Justification,
      DimensionScale = sourceText.DimensionScale,
    };

    try { freshText.DrawForward = sourceText.DrawForward; }
    catch { }

    double padding = GetPaddingPercent(titleObject);
    return CreateFrameForText(freshText, padding);
  }

  private static double GetPaddingPercent(RhinoObject titleObject)
  {
    double padding = DefaultPadding;
    if (double.TryParse(
          titleObject.Attributes.GetUserString(PaddingUserStringKey),
          NumberStyles.Any,
          CultureInfo.InvariantCulture,
          out double storedPadding))
    {
      padding = storedPadding;
    }

    return padding;
  }

  /// <summary>
  /// Creates the rectangle around the ACTUAL glyph outlines. TextEntity's
  /// annotation bounding box has produced angle-dependent results in this
  /// workflow, so use Explode() curves as the geometry source of truth.
  /// </summary>
  private static PolylineCurve CreateFrameForText(
    TextEntity text,
    double paddingPercent)
  {
    double hw;
    double hh;

    if (!TryGetExplodedTextHalfExtents(text, out hw, out hh))
    {
      var (tw, th) = ApproxBounds(
        text.PlainText ?? string.Empty, text.TextHeight);
      hw = tw / 2.0;
      hh = th / 2.0;
    }

    double pad = text.TextHeight * paddingPercent / 100.0;
    return RectCurve(
      text.Plane.Origin,
      text.Plane.XAxis,
      text.Plane.YAxis,
      Math.Max(hw + pad, RhinoMath.ZeroTolerance),
      Math.Max(hh + pad, RhinoMath.ZeroTolerance));
  }

  private static bool TryGetExplodedTextHalfExtents(
    TextEntity text,
    out double hw,
    out double hh)
  {
    hw = 0.0;
    hh = 0.0;

    Curve[]? curves = null;
    try
    {
      curves = text.Explode();
      if (curves == null || curves.Length == 0)
        return false;

      // Explicitly transform the exploded glyph geometry from the title plane
      // into WorldXY. Unlike GetBoundingBox(Plane), this guarantees that the
      // measured coordinates are local to text.Plane.Origin and independent of
      // the title's model-space position/rotation.
      var toLocal = Transform.PlaneToPlane(
        text.Plane, Plane.WorldXY);

      double minX = double.PositiveInfinity;
      double minY = double.PositiveInfinity;
      double maxX = double.NegativeInfinity;
      double maxY = double.NegativeInfinity;
      bool found = false;

      foreach (var sourceCurve in curves)
      {
        if (sourceCurve == null)
          continue;

        using var localCurve = sourceCurve.DuplicateCurve();
        if (localCurve == null || !localCurve.Transform(toLocal))
          continue;

        var bb = localCurve.GetBoundingBox(true);
        if (!bb.IsValid)
          continue;

        minX = Math.Min(minX, bb.Min.X);
        minY = Math.Min(minY, bb.Min.Y);
        maxX = Math.Max(maxX, bb.Max.X);
        maxY = Math.Max(maxY, bb.Max.Y);
        found = true;
      }

      if (!found)
        return false;

      // vTitle is MiddleCenter-justified, so its frame remains centered on the
      // annotation origin. Use the actual local glyph reach on each axis.
      hw = Math.Max(Math.Abs(minX), Math.Abs(maxX));
      hh = Math.Max(Math.Abs(minY), Math.Abs(maxY));

      return hw > RhinoMath.ZeroTolerance &&
             hh > RhinoMath.ZeroTolerance;
    }
    catch
    {
      return false;
    }
    finally
    {
      if (curves != null)
      {
        foreach (var curve in curves)
          curve?.Dispose();
      }
    }
  }

  private static PolylineCurve RectCurve(
    Point3d center,
    Vector3d xAxis,
    Vector3d yAxis,
    double hw,
    double hh)
  {
    var c0 = center + xAxis * (-hw) + yAxis * (-hh);
    var c1 = center + xAxis * ( hw) + yAxis * (-hh);
    var c2 = center + xAxis * ( hw) + yAxis * ( hh);
    var c3 = center + xAxis * (-hw) + yAxis * ( hh);
    return new PolylineCurve(new[] { c0, c1, c2, c3, c0 });
  }

  private static int GetTargetLayerIndex(RhinoDoc doc)
  {
    if (_layer == "." || _layer == "*" ||
        string.Equals(_layer, CurrentLayerOption, StringComparison.OrdinalIgnoreCase))
      return doc.Layers.CurrentLayerIndex;
    int idx = doc.Layers.FindByFullPath(_layer, -1);
    if (idx >= 0) return idx;
    try
    {
      var layer = new Rhino.DocObjects.Layer { Name = _layer };
      idx = doc.Layers.Add(layer);
      if (idx >= 0) return idx;
    }
    catch { }
    return doc.Layers.CurrentLayerIndex;
  }

  /// <summary>Approximate text extents based on size and character count.</summary>
  private static (double w, double h) ApproxBounds(string text, double size)
  {
    double h = size * 1.4;
    double w = Math.Max(text.Length * size * 0.75, size);
    return (w, h);
  }

  // ── Settings ──────────────────────────────────────────────────────────

  private static void LoadSettings()
  {
    ToolsOptionStore.Read<int>(SectionName, s =>
    {
      if (ToolsOptionStore.TryGetString(s, KeyText,    out var t)) _text    = t;
      if (ToolsOptionStore.TryGetDouble(s, KeySize,    out var v)) _size    = v;
      if (ToolsOptionStore.TryGetDouble(s, KeyPadding, out v))     _padding = v;
      if (ToolsOptionStore.TryGetBool  (s, KeyBox,     out var b)) _box     = b;
      if (ToolsOptionStore.TryGetString(s, KeyLayer,   out var l)) _layer   = NormalizeLayerOption(l);
      return 0;
    });
  }

  private static string NormalizeLayerOption(string? value)
  {
    var layer = value?.Trim() ?? string.Empty;
    return layer == "." || layer == "*" ||
           string.Equals(layer, CurrentLayerOption, StringComparison.OrdinalIgnoreCase)
      ? CurrentLayerOption
      : layer;
  }

  private static void SaveSettings()
  {
    ToolsOptionStore.Update(SectionName, s =>
    {
      s[KeyText]    = _text;
      s[KeySize]    = _size;
      s[KeyPadding] = _padding;
      s[KeyBox]     = _box;
      s[KeyLayer]   = _layer;
    });
  }
}
