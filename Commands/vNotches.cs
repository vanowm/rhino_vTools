using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

/// <summary>
/// Places notches (I, V, open-V, U, and T shapes) on one or more curves with an interactive live panel.
/// </summary>
public sealed class vNotches : vToolsCommand
{
  // ── Constants ────────────────────────────────────────────────────────────

  const string Section            = "vNotches";
  const string DocumentSettingsSection = "vTools"; // Rhino document-string section used for per-document command settings.
  const string DocumentSettingsEntry = "vNotches"; // Rhino document-string entry containing serialized notch and label settings.
  const string SpecialLayerCurrent = "*[Current]*"; // Layer-selector sentinel that resolves output to Rhino's current layer.
  const string NotchDataPrefix    = "notch."; // Prefix for user-data keys written to notch output objects.
  const string NotchDataVersion   = "1"; // Serialized notch metadata schema version.
  const string OpenVNotchType     = "\\/"; // Stored and displayed code for the Open Vee notch type.
  const string NotchObjectName = "Notch"; // Rhino object name assigned to every created notch component.
  const string NotchLabelObjectName = "NotchLabel"; // Rhino object name assigned to every created notch label.
  const string NotchComponentSetKey = NotchDataPrefix + "component_set"; // Metadata key linking one notch's output components.
  const double LabelWidthMult     = 0.9; // Estimated text-width multiplier applied per character.
  const double DefaultLabelOffIn  = 0.1; // Label offset in inches before document-unit conversion; zero or greater.

  // ── Persisted defaults ───────────────────────────────────────────────────

  const double DefaultNotchLength = 0.18; // Notch depth in model units; greater than zero.
  const double DefaultNotchOffset = 0.5; // Offset-curve distance in model units; zero or greater.
  const double DefaultNotchWidth = 0.18; // Notch width in model units; greater than zero.
  const string DefaultNotchType = "I"; // Notch code: I, V, \/, U, or T.
  const bool DefaultNotchEnabled = true; // true creates notch geometry; false creates labels only.
  const bool DefaultPercent = false; // true interprets placement as curve percentage; false uses model-unit distance.
  const bool DefaultGroup = false; // true groups each result with its landed source; false preserves existing grouping only.
  const bool DefaultLabelEnabled = false; // true creates notch labels; false omits them.
  const string DefaultLabelValue = "A"; // Plain label text.
  const double DefaultLabelSize = 0.3; // Text height in model units; zero or greater.
  const bool DefaultLabelSizeAuto = false; // true derives label size automatically; false uses the configured size.
  const int DefaultLabelSizePercent = 75; // Auto-size percentage; 20 through 100 in five-point steps.
  const string DefaultNotchLayer = SpecialLayerCurrent; // Rhino layer path or the current-layer sentinel.
  const string DefaultLabelLayer = "PLOT"; // Rhino layer name or full layer path.
  const double DefaultLabelOffset = double.NaN; // Model units; NaN requests DefaultLabelOffIn conversion.
  const double DefaultLabelOffsetY = 0.0; // Perpendicular label offset in model units.
  const bool DefaultLabelAutoAdvance = true; // true increments labels after placement; false reuses the current label.
  const bool DefaultLabelSideFlip = false; // true places labels on the opposite side; false uses the notch side.
  const bool DefaultKeepSelection = false; // true retains current curves while selecting; false replaces the selection.
  const double DefaultMultipleStartOffset = 2.0; // Start clearance in model units; zero or greater.
  const double DefaultMultipleEndOffset = 2.0; // End clearance in model units; zero or greater.
  const bool DefaultMultipleStartOffsetEnabled = true; // true reserves the start offset; false excludes a start-end notch.
  const bool DefaultMultipleEndOffsetEnabled = true; // true reserves the end offset; false excludes an end-end notch.
  const int DefaultMultipleNumber = 2; // Number of notches; one through 10000.
  const double DefaultMultipleDistance = 0.0; // Minimum spacing in model units; zero or greater.
  const bool DefaultMultipleUseDistance = false; // true drives spacing by minimum distance; false drives it by notch count.
  const bool DefaultMultipleAuto = false; // true uses curvature-aware spacing with Distance as the maximum; false uses uniform spacing.
  const int DefaultMultipleCurvatureSensitivity = 10; // Whole-number curvature sensitivity from zero through 1000; ten preserves the original 1.0 curvature multiplier.
  const bool DefaultMultipleSeparate = false; // true applies the requested multiple layout to each linked physical segment; false treats each linked sequence as one curve.
  const double MultipleCurvatureSensitivityUnit = 0.1; // Internal curvature multiplier represented by one whole-number Sensitivity step.
  const int MultipleAutoMinimumSamples = 64; // Minimum tangent samples used by curvature-aware spacing.
  const int MultipleAutoSamplesPerSpacing = 16; // Tangent samples taken per maximum-distance interval.
  const int MultipleAutoMaximumSamples = 20000; // Upper sampling limit protecting interactive preview performance.
  const double MultipleAutoKinkSnapDistanceScale = 0.5; // Fraction of maximum spacing within which a regular Auto station is replaced by a kink station.
  const double MultipleAutoKinkMinimumAngleDegrees = 1.0; // Minimum joined-segment tangent change treated as a kink candidate, in degrees.
  const double MultipleExistingNotchClearanceScale = 1.0; // Fraction of the proposed local spacing kept clear around notches already placed on the current curve selection.
  const int DefaultWindowWidth = 300; // Client width in device-independent pixels; 300 or greater.
  const int DefaultWindowHeight = 0; // Client height in device-independent pixels; zero auto-sizes to content.

  static double _notchLength = DefaultNotchLength;
  static double _notchOffset = DefaultNotchOffset;
  static double _notchWidth = DefaultNotchWidth;
  static string _notchType = DefaultNotchType;
  static bool _notch = DefaultNotchEnabled;
  static bool _percent = DefaultPercent;
  static bool _group = DefaultGroup;
  static bool _label = DefaultLabelEnabled;
  static string _labelValue = DefaultLabelValue;
  static double _labelSize = DefaultLabelSize;
  static bool _labelSizeAuto = DefaultLabelSizeAuto;
  static int _labelSizePct = DefaultLabelSizePercent;
  static string _notchLayer = DefaultNotchLayer;
  static string _labelLayer = DefaultLabelLayer;
  static double _labelOffset = DefaultLabelOffset; // resolved to model units on first load
  static double _labelOffsetY = DefaultLabelOffsetY;
  static bool _labelAutoAdv = DefaultLabelAutoAdvance;
  static bool _labelSideFlip = DefaultLabelSideFlip;
  static bool _keepSelection = DefaultKeepSelection;
  static double _multipleStartOffset = DefaultMultipleStartOffset;
  static double _multipleEndOffset = DefaultMultipleEndOffset;
  static bool _multipleStartOffsetEnabled = DefaultMultipleStartOffsetEnabled;
  static bool _multipleEndOffsetEnabled = DefaultMultipleEndOffsetEnabled;
  static int _multipleNumber = DefaultMultipleNumber;
  static double _multipleDistance = DefaultMultipleDistance;
  static bool _multipleUseDistance = DefaultMultipleUseDistance;
  static bool _multipleAuto = DefaultMultipleAuto;
  static int _multipleCurvatureSensitivity = DefaultMultipleCurvatureSensitivity;
  static bool _multipleSeparate = DefaultMultipleSeparate;
  static int _windowWidth = DefaultWindowWidth;
  static int _windowHeight = DefaultWindowHeight;
  static bool[] _curveSides     = Array.Empty<bool>();
  static NotchSession? _activeSession;
  static GetPoint? _activeGetter;

  public override string EnglishName => "vNotches";

  internal static Result RunLocalHistory(RhinoDoc doc, bool redo, string source)
  {
    var session = _activeSession;
    var getter = _activeGetter;
    if (session == null || session.Doc != doc || getter == null)
      return Result.Nothing;

    GetBaseClass.PostCustomMessage(new NotchHistoryRequest(redo, source));
    vTools.Log.Write("vNotches", $"{source} {(redo ? "redo" : "undo")} requested");
    return Result.Success;
  }

  static void ApplyLocalHistory(RhinoDoc doc, NotchSession session, bool redo, string source)
  {
    if (redo)
    {
      if (session.RedoBatches.Count == 0)
      {
        RhinoApp.WriteLine("vNotches: nothing to redo.");
        vTools.Log.Write("vNotches", $"{source} redo ignored: empty stack");
        return;
      }
      RedoLastNotch(doc, session);
    }
    else
    {
      if (session.NotchRecords.Count == 0)
      {
        RhinoApp.WriteLine("vNotches: nothing to undo.");
        vTools.Log.Write("vNotches", $"{source} undo ignored: empty stack");
        return;
      }
      UndoLastNotch(doc, session);
    }

    vTools.Log.Write("vNotches",
      $"{source} {(redo ? "redo" : "undo")} handled records={session.NotchRecords.Count} redo={session.RedoBatches.Count}");
  }

  // ── Settings ─────────────────────────────────────────────────────────────

  static void LoadOptions(RhinoDoc doc)
  {
    ToolsOptionStore.Read<int>(Section, s =>
    {
      ApplyStoredOptions(s, includeUiSettings: true);
      return 0;
    });
    LoadDocumentOptions(doc);
    if (double.IsNaN(_labelOffset))
      _labelOffset = ModelUnitsFromInches(doc, DefaultLabelOffIn);
    if (!_notch && !_label)
      _notch = true;
  }

  static void SaveOptions(NotchSession s)
  {
    UpdateStaticDefaultsFromSession(s);

    bool ok = ToolsOptionStore.Update(Section, sec =>
    {
      WriteBehaviorOptions(sec);
      sec["keep_selection"] = _keepSelection;
      sec["window_width"] = _windowWidth;
      sec["window_height"] = _windowHeight;

      var arr = new System.Text.Json.Nodes.JsonArray();
      foreach (var b in _curveSides) arr.Add(b);
      sec["curve_sides"] = arr;
    });

    if (!ok)
      RhinoApp.WriteLine($"vNotches: failed to save options: {ToolsOptionStore.LastError}");
    SaveDocumentOptions(s.Doc);
  }

  static void ApplyStoredOptions(
    System.Text.Json.Nodes.JsonObject? s, bool includeUiSettings)
  {
    if (ToolsOptionStore.TryGetDouble(s, "notch_length",    out var v)) _notchLength   = v;
    if (ToolsOptionStore.TryGetDouble(s, "notch_offset",    out v))     _notchOffset   = v;
    if (ToolsOptionStore.TryGetDouble(s, "notch_width",     out v))     _notchWidth    = v;
    if (ToolsOptionStore.TryGetString(s, "notch_type",      out var t)) _notchType     = t;
    if (ToolsOptionStore.TryGetBool  (s, "notch",           out var b)) _notch         = b;
    if (ToolsOptionStore.TryGetBool  (s, "percent",         out b))     _percent       = b;
    if (ToolsOptionStore.TryGetBool  (s, "group",           out b))     _group         = b;
    if (ToolsOptionStore.TryGetBool  (s, "label",           out b))     _label         = b;
    if (ToolsOptionStore.TryGetString(s, "label_value",     out t))     _labelValue    = t;
    if (ToolsOptionStore.TryGetDouble(s, "label_size",      out v))     _labelSize     = v;
    if (ToolsOptionStore.TryGetBool  (s, "label_size_auto", out b))     _labelSizeAuto = b;
    if (ToolsOptionStore.TryGetDouble(s, "label_size_pct",  out var pctv)) _labelSizePct = (int)pctv;
    if (ToolsOptionStore.TryGetString(s, "notch_layer",     out t))     _notchLayer    = t;
    if (ToolsOptionStore.TryGetString(s, "label_layer",     out t))     _labelLayer    = t;
    if (ToolsOptionStore.TryGetDouble(s, "label_offset",    out v))     _labelOffset   = v;
    if (ToolsOptionStore.TryGetDouble(s, "label_offset_y",  out v))     _labelOffsetY  = v;
    if (ToolsOptionStore.TryGetBool  (s, "label_auto_adv",  out b))     _labelAutoAdv  = b;
    if (ToolsOptionStore.TryGetBool  (s, "label_side_flip", out b))     _labelSideFlip = b;
    if (ToolsOptionStore.TryGetDouble(s, "multiple_start_offset", out v)) _multipleStartOffset = Math.Max(0.0, v);
    if (ToolsOptionStore.TryGetDouble(s, "multiple_end_offset",   out v)) _multipleEndOffset = Math.Max(0.0, v);
    if (ToolsOptionStore.TryGetBool(s, "multiple_start_offset_enabled", out b)) _multipleStartOffsetEnabled = b;
    if (ToolsOptionStore.TryGetBool(s, "multiple_end_offset_enabled", out b)) _multipleEndOffsetEnabled = b;
    if (ToolsOptionStore.TryGetDouble(s, "multiple_number", out v)) _multipleNumber = Math.Clamp((int)Math.Round(v), 1, 10000);
    if (ToolsOptionStore.TryGetDouble(s, "multiple_distance", out v)) _multipleDistance = Math.Max(0.0, v);
    if (ToolsOptionStore.TryGetBool(s, "multiple_use_distance", out b)) _multipleUseDistance = b;
    if (ToolsOptionStore.TryGetBool(s, "multiple_auto", out b)) _multipleAuto = b;
    if (ToolsOptionStore.TryGetDouble(s, "multiple_curvature_sensitivity", out v))
      _multipleCurvatureSensitivity = Math.Clamp((int)Math.Round(v), 0, 1000);
    if (ToolsOptionStore.TryGetBool(s, "multiple_separate", out b))
      _multipleSeparate = b;

    if (!includeUiSettings)
      return;
    if (ToolsOptionStore.TryGetBool(s, "keep_selection", out b)) _keepSelection = b;
    if (ToolsOptionStore.TryGetDouble(s, "window_width", out v))
      _windowWidth = Math.Max(DefaultWindowWidth, (int)Math.Round(v));
    if (ToolsOptionStore.TryGetDouble(s, "window_height", out v))
      _windowHeight = Math.Max(DefaultWindowHeight, (int)Math.Round(v));
    if (s?["curve_sides"] is System.Text.Json.Nodes.JsonArray arr)
    {
      var sides = new List<bool>();
      foreach (var el in arr)
        if (el is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<bool>(out var bv))
          sides.Add(bv);
      _curveSides = sides.ToArray();
    }
  }

  static void WriteBehaviorOptions(System.Text.Json.Nodes.JsonObject sec)
  {
    sec["notch_length"] = _notchLength;
    sec["notch_offset"] = _notchOffset;
    sec["notch_width"] = _notchWidth;
    sec["notch_type"] = _notchType;
    sec["notch"] = _notch;
    sec["percent"] = _percent;
    sec["group"] = _group;
    sec["label"] = _label;
    sec["label_value"] = _labelValue;
    sec["label_size"] = _labelSize;
    sec["label_size_auto"] = _labelSizeAuto;
    sec["label_size_pct"] = _labelSizePct;
    sec["notch_layer"] = _notchLayer;
    sec["label_layer"] = _labelLayer;
    sec["label_offset"] = _labelOffset;
    sec["label_offset_y"] = _labelOffsetY;
    sec["label_auto_adv"] = _labelAutoAdv;
    sec["label_side_flip"] = _labelSideFlip;
    sec["multiple_start_offset"] = _multipleStartOffset;
    sec["multiple_end_offset"] = _multipleEndOffset;
    sec["multiple_start_offset_enabled"] = _multipleStartOffsetEnabled;
    sec["multiple_end_offset_enabled"] = _multipleEndOffsetEnabled;
    sec["multiple_number"] = _multipleNumber;
    sec["multiple_distance"] = _multipleDistance;
    sec["multiple_use_distance"] = _multipleUseDistance;
    sec["multiple_auto"] = _multipleAuto;
    sec["multiple_curvature_sensitivity"] = _multipleCurvatureSensitivity;
    sec["multiple_separate"] = _multipleSeparate;
  }

  static void LoadDocumentOptions(RhinoDoc doc)
  {
    string? json = doc.Strings.GetValue(DocumentSettingsSection, DocumentSettingsEntry);
    if (string.IsNullOrWhiteSpace(json))
      return;
    try
    {
      ApplyStoredOptions(
        System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject,
        includeUiSettings: false);
      Log.Write("vNotches", "loaded document settings over global defaults");
    }
    catch (Exception ex)
    {
      Log.Write("vNotches", $"document settings load failed: {ex.Message}");
    }
  }

  static void SaveDocumentOptions(RhinoDoc doc)
  {
    try
    {
      var section = new System.Text.Json.Nodes.JsonObject();
      WriteBehaviorOptions(section);
      string json = section.ToJsonString();
      if (!string.Equals(
            doc.Strings.GetValue(DocumentSettingsSection, DocumentSettingsEntry),
            json,
            StringComparison.Ordinal))
        doc.Strings.SetString(DocumentSettingsSection, DocumentSettingsEntry, json);
    }
    catch (Exception ex)
    {
      Log.Write("vNotches", $"document settings save failed: {ex.Message}");
    }
  }

static void UpdateStaticDefaultsFromSession(NotchSession s)
{
  _notchLength   = s.NotchLengthOpt.CurrentValue;
  _notchOffset   = s.NotchOffsetOpt.CurrentValue;
  _notchWidth    = s.NotchWidthOpt.CurrentValue;
  _notchType     = s.NotchTypeValues[s.NotchTypeIndex];
  _notch         = s.NotchToggle.CurrentValue;

  _percent       = s.PercentToggle.CurrentValue;
  _group         = s.GroupToggle.CurrentValue;
  _label         = s.LabelToggle.CurrentValue;
  _labelValue    = s.LabelValueText;

  _labelSize     = s.ManualLabelSize;
  _labelSizeAuto = s.LabelSizeAutoToggle.CurrentValue;
  _labelSizePct  = s.LabelSizePctValues[s.LabelSizePctIndex];

  _notchLayer    = s.NotchLayerName;
  _labelLayer    = s.LabelLayerName;
  _labelOffset   = s.LabelOffsetOpt.CurrentValue;
  _labelOffsetY  = s.LabelOffsetYOpt.CurrentValue;

  _labelAutoAdv  = s.LabelAutoAdv;
  _labelSideFlip = s.LabelSideFlip;
  _keepSelection = s.KeepCurveSelection;
  _multipleStartOffset = s.MultipleStartOffset;
  _multipleEndOffset   = s.MultipleEndOffset;
  _multipleStartOffsetEnabled = s.MultipleStartOffsetEnabled;
  _multipleEndOffsetEnabled   = s.MultipleEndOffsetEnabled;
  _multipleNumber      = s.MultipleNumber;
  _multipleDistance    = s.MultipleDistance;
  _multipleUseDistance = s.MultipleUseDistance;
  _multipleAuto = s.MultipleAuto;
  _multipleCurvatureSensitivity = s.MultipleCurvatureSensitivity;
  _multipleSeparate = s.MultipleSeparate;
  _curveSides    = s.CurveSides.ToArray();
}
  // ── Entry point ───────────────────────────────────────────────────────────

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    LoadOptions(doc);

    if (!TrySelectCurves(doc, out var curves, out var curveIds,
      out var curveSourceIds, out var curveSegments))
      return Result.Cancel;

    var selectedLengths = curveSegments
      .SelectMany(sequence => sequence)
      .Select(curve => curve.GetLength())
      .ToArray();
    if (selectedLengths.Length == 1)
    {
      RhinoApp.WriteLine(
        "Curve length: " + FormatFractionalInches(doc, selectedLengths[0]));
    }
    else
    {
      for (int index = 0; index < selectedLengths.Length; index++)
        RhinoApp.WriteLine(
          $"Curve {index + 1} length: " +
          FormatFractionalInches(doc, selectedLengths[index]));
      if (selectedLengths.Length == 2)
        RhinoApp.WriteLine(
          "Length difference: " +
          FormatFractionalInches(
            doc,
            Math.Abs(selectedLengths[0] - selectedLengths[1])));
    }

    // Build initial curve sides â€” reuse stored per-curve values if count matches
    var initialSides = new bool[curves.Count];
    for (int i = 0; i < curves.Count; i++)
    {
      if (i < _curveSides.Length) initialSides[i] = _curveSides[i];
      else if (_curveSides.Length > 0) initialSides[i] = _curveSides[_curveSides.Length - 1];
      else initialSides[i] = false; // false = Right
    }

    var session = new NotchSession(doc, curves, curveIds, initialSides,
      _notchLength, _notchOffset, _notchWidth, _notchType, _notch,
      _percent, _group, _label, _labelValue,
      _labelSize, _labelSizeAuto, _labelSizePct,
      _notchLayer, _labelLayer, _labelOffset, _labelOffsetY,
      _labelAutoAdv, _labelSideFlip, _keepSelection,
      _multipleStartOffset, _multipleEndOffset,
      _multipleStartOffsetEnabled, _multipleEndOffsetEnabled, _multipleNumber,
      _multipleDistance, _multipleUseDistance,
      _multipleAuto, _multipleCurvatureSensitivity, _multipleSeparate);

    // Apply actual source IDs so SelectBothCurves highlights all segments of joined chains.
    for (int i = 0; i < curveSourceIds.Count && i < session.PerCurveSourceIds.Count; i++)
    {
      session.PerCurveSourceIds[i] = curveSourceIds[i];
      session.PerCurveSegments[i] = curveSegments[i]
        .Select(curve => curve.DuplicateCurve())
        .ToList();
    }
    session.ResetCurveDisplayNumbers();

    RunLoop(doc, session);
    SaveOptions(session);

    // Deselect all segments, including joined chain source segments.
    foreach (var id in session.PerCurveSourceIds.SelectMany(list => list))
      doc.Objects.FindId(id)?.Select(false);
    doc.Views.Redraw();

    return Result.Success;
  }

  // ── Curve selection ───────────────────────────────────────────────────────

  static bool TrySelectCurves(RhinoDoc doc, out List<Curve> curves,
    out List<Guid> curveIds, out List<List<Guid>> curveSourceIds,
    out List<List<Curve>> curveSegments)
  {
    curves         = new List<Curve>();
    curveIds       = new List<Guid>();
    curveSourceIds = new List<List<Guid>>();
    curveSegments  = new List<List<Curve>>();
    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt("Select one or more curves (near start)");
    go.GeometryFilter = ObjectType.Curve;
    go.GroupSelect = false;
    go.EnablePreSelect(true, true);
    var res = go.GetMultiple(1, 0);
    if (go.CommandResult() != Result.Success || res != GetResult.Object)
      return false;
    var selectedObjects = new List<RhinoObject>();
    var selectionPoints = new Dictionary<Guid, Point3d>();
    for (int i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      var obj = objRef?.Object();
      if (obj?.Geometry is not Curve)
        return false;
      selectedObjects.Add(obj);
      var point = objRef!.SelectionPoint();
      if (point.IsValid)
        selectionPoints[obj.Id] = point;
    }
    if (selectedObjects.Count == 0) return false;

    foreach (var logical in BuildLogicalCurveSelections(
      doc, selectedObjects, selectionPoints, existingSession: null))
    {
      curves.Add(logical.Curve);
      curveIds.Add(logical.PrimaryObject.Id);
      curveSourceIds.Add(logical.SourceIds);
      curveSegments.Add(logical.Segments);
    }
    return true;
  }

  sealed record LogicalCurveSelection(
    Curve Curve,
    RhinoObject PrimaryObject,
    List<Guid> SourceIds,
    List<Curve> Segments);

  sealed record CurveLayoutItem(
    Guid SourceId,
    Curve Curve,
    bool LinkedToPrevious);

  static List<LogicalCurveSelection> BuildLogicalCurveSelections(
    RhinoDoc doc,
    IReadOnlyList<RhinoObject> selectedObjects,
    IReadOnlyDictionary<Guid, Point3d> selectionPoints,
    NotchSession? existingSession)
  {
    var sourceCurves = selectedObjects
      .Select(obj => obj.Geometry as Curve)
      .ToList();
    if (sourceCurves.Any(curve => curve == null))
      return [];

    var curves = sourceCurves.Select(curve => curve!).ToList();
    var result = new List<LogicalCurveSelection>();
    foreach (var component in ConnectedCurveComponents(
      curves, doc.ModelAbsoluteTolerance))
    {
      var componentObjects = component.Select(index => selectedObjects[index]).ToList();
      var componentCurves = component.Select(index => curves[index]).ToList();
      var componentIds = componentObjects.Select(obj => obj.Id).ToList();
      string componentKey = CurveSetKey(componentIds);

      Point3d startPick = Point3d.Unset;
      foreach (var obj in componentObjects)
      {
        if (selectionPoints.TryGetValue(obj.Id, out var point) && point.IsValid)
        {
          startPick = point;
          break;
        }
      }

      if (!startPick.IsValid && existingSession != null)
      {
        for (int i = 0; i < existingSession.PerCurveSourceIds.Count; i++)
        {
          if (i >= existingSession.Curves.Count ||
              CurveSetKey(existingSession.PerCurveSourceIds[i]) != componentKey)
            continue;
          startPick = existingSession.Curves[i].PointAtStart;
          break;
        }
      }
      if (!startPick.IsValid)
        startPick = componentCurves[0].PointAtStart;

      if (component.Count > 1 && TryJoinConnectedChain(
        doc,
        componentCurves,
        componentIds,
        startPick,
        out var joined,
        out var joinedIds,
        out var joinedSegments))
      {
        result.Add(new LogicalCurveSelection(
          joined,
          componentObjects.First(obj => obj.Id == joinedIds[0]),
          joinedIds,
          joinedSegments));
        continue;
      }

      foreach (int index in component)
      {
        var obj = selectedObjects[index];
        var source = curves[index].DuplicateCurve();
        Point3d pick;
        if (selectionPoints.TryGetValue(obj.Id, out var point) && point.IsValid)
          pick = point;
        else if (existingSession != null &&
                 TryGetExistingSourceStart(existingSession, obj.Id, out var existingStart))
          pick = existingStart;
        else
          pick = source.PointAtStart;
        var oriented = OrientCurveToPickPoint(source, pick);
        result.Add(new LogicalCurveSelection(
          oriented,
          obj,
          [obj.Id],
          [oriented.DuplicateCurve()]));
      }
    }
    return result;
  }

  static bool TryGetExistingSourceStart(
    NotchSession session, Guid sourceId, out Point3d start)
  {
    for (int curveIndex = 0;
         curveIndex < session.PerCurveSourceIds.Count;
         curveIndex++)
    {
      int sourceIndex = session.PerCurveSourceIds[curveIndex].IndexOf(sourceId);
      if (sourceIndex < 0 ||
          curveIndex >= session.PerCurveSegments.Count ||
          sourceIndex >= session.PerCurveSegments[curveIndex].Count)
        continue;
      start = session.PerCurveSegments[curveIndex][sourceIndex].PointAtStart;
      return true;
    }
    start = Point3d.Unset;
    return false;
  }

  static List<List<int>> ConnectedCurveComponents(
    IReadOnlyList<Curve> curves,
    double tolerance)
  {
    var result = new List<List<int>>();
    var remaining = new HashSet<int>(Enumerable.Range(0, curves.Count));
    while (remaining.Count > 0)
    {
      int seed = remaining.Min();
      remaining.Remove(seed);
      var component = new List<int> { seed };
      var queue = new Queue<int>();
      queue.Enqueue(seed);

      while (queue.Count > 0)
      {
        int current = queue.Dequeue();
        foreach (int candidate in remaining.ToArray())
        {
          if (!CurveEndsTouch(curves[current], curves[candidate], tolerance))
            continue;
          remaining.Remove(candidate);
          component.Add(candidate);
          queue.Enqueue(candidate);
        }
      }

      component.Sort();
      result.Add(component);
    }
    return result;
  }

  static bool CurveEndsTouch(Curve first, Curve second, double tolerance) =>
    first.PointAtStart.DistanceTo(second.PointAtStart) <= tolerance ||
    first.PointAtStart.DistanceTo(second.PointAtEnd) <= tolerance ||
    first.PointAtEnd.DistanceTo(second.PointAtStart) <= tolerance ||
    first.PointAtEnd.DistanceTo(second.PointAtEnd) <= tolerance;

  static string CurveSetKey(IEnumerable<Guid> ids) =>
    string.Join(",", ids.Distinct().OrderBy(id => id).Select(id => id.ToString("N")));

  static bool TryJoinConnectedChain(
    RhinoDoc doc, List<Curve> curves, List<Guid> ids, Point3d startPick,
    out Curve joined, out List<Guid> orderedIds, out List<Curve> orderedSegments)
  {
    joined     = null!;
    orderedIds = new List<Guid>();
    orderedSegments = new List<Curve>();
    double tol = doc.ModelAbsoluteTolerance;

    var endpoints = new List<(int CurveIndex, bool AtEnd, Point3d Point, bool Outer)>();
    for (int i = 0; i < curves.Count; i++)
    {
      foreach (var endpoint in new[]
      {
        (AtEnd: false, Point: curves[i].PointAtStart),
        (AtEnd: true, Point: curves[i].PointAtEnd),
      })
      {
        bool outer = true;
        for (int other = 0; other < curves.Count && outer; other++)
        {
          if (other == i) continue;
          outer = endpoint.Point.DistanceTo(curves[other].PointAtStart) > tol &&
                  endpoint.Point.DistanceTo(curves[other].PointAtEnd) > tol;
        }
        endpoints.Add((i, endpoint.AtEnd, endpoint.Point, outer));
      }
    }

    bool hasOuterEndpoint = endpoints.Any(endpoint => endpoint.Outer);
    var firstEndpoint = (hasOuterEndpoint
        ? endpoints.Where(endpoint => endpoint.Outer)
        : endpoints)
      .OrderBy(endpoint => endpoint.Point.DistanceTo(startPick))
      .First();
    int firstIdx = firstEndpoint.CurveIndex;
    var firstCurve = curves[firstIdx].DuplicateCurve();
    if (firstEndpoint.AtEnd)
      firstCurve.Reverse();
    var orderedCurves = new List<Curve> { firstCurve };
    orderedIds.Add(ids[firstIdx]);

    var remaining = Enumerable.Range(0, curves.Count).Where(i => i != firstIdx).ToList();
    while (remaining.Count > 0)
    {
      Point3d currentEnd = orderedCurves[^1].PointAtEnd;
      var matches = new List<(int Index, bool Flip)>();
      foreach (int ri in remaining)
      {
        if (curves[ri].PointAtStart.DistanceTo(currentEnd) <= tol)
          matches.Add((ri, false));
        if (curves[ri].PointAtEnd.DistanceTo(currentEnd) <= tol)
          matches.Add((ri, true));
      }
      if (matches.Count == 0 || (hasOuterEndpoint && matches.Count > 1))
        return false; // gap or branch — keep curves separate

      var (nextIdx, flip) = matches[0];
      var next = curves[nextIdx].DuplicateCurve();
      if (flip) next.Reverse();
      orderedCurves.Add(next);
      orderedIds.Add(ids[nextIdx]);
      remaining.Remove(nextIdx);
    }

    // PolyCurve.Append preserves kinks at segment junctions.
    var poly = new PolyCurve();
    foreach (var c in orderedCurves)
      poly.Append(c);
    joined = poly;
    orderedSegments = orderedCurves
      .Select(curve => curve.DuplicateCurve())
      .ToList();
    return true;
  }

  static bool TryUpdateCurveSelection(RhinoDoc doc, NotchSession s)
  {
    var sideSequence = s.CurveSides.ToArray();
    bool keepCurrentSelection = s.KeepCurveSelection;
    vTools.Log.Write("vNotches",
      $"curve selection begin: keepCurrent={keepCurrentSelection}; {DescribeCurveSides(s)}");

    var generatedPrimaryIds = s.NotchIdsByCurve
      .SelectMany(ids => ids)
      .Concat(s.NotchRecords.SelectMany(record => record.DetachedNotchIds))
      .Where(id => id != Guid.Empty)
      .ToList();
    var generatedIds = generatedPrimaryIds
      .SelectMany(id => RelatedNotchObjects(doc, id).Select(obj => obj.Id))
      .ToHashSet();

    var go = new GetObject();
    go.EnableTransparentCommands(true);
    go.SetCommandPrompt(keepCurrentSelection
      ? "Add or remove curves. Press Enter when done"
      : "Select replacement curves. Press Enter when done");
    go.GeometryFilter = ObjectType.Curve;
    go.SubObjectSelect = false;
    go.GroupSelect = false;
    go.AcceptNothing(true);
    if (!keepCurrentSelection)
      doc.Objects.UnselectAll();
    go.EnablePreSelect(keepCurrentSelection, true);
    go.EnablePostSelect(true);
    go.EnableClearObjectsOnEntry(false);
    go.EnableUnselectObjectsOnExit(false);
    go.DeselectAllBeforePostSelect = false;
    go.AlreadySelectedObjectSelect = true;
    go.SetCustomGeometryFilter((obj, _, _) => !generatedIds.Contains(obj.Id));

    bool preselectedWaitingForEnter = false;
    while (true)
    {
      var result = go.GetMultiple(0, 0);
      if (result == GetResult.Cancel || go.CommandResult() != Result.Success)
      {
        SelectBothCurves(doc, s);
        return false;
      }

      if (result == GetResult.Object && go.ObjectsWerePreselected && !preselectedWaitingForEnter)
      {
        preselectedWaitingForEnter = true;
        go.EnablePreSelect(false, true);
        continue;
      }

      if (result is GetResult.Object or GetResult.Nothing)
        break;
    }

    var selectedPool = doc.Objects.GetSelectedObjects(false, false)
      .Where(obj => obj.Geometry is Curve && !generatedIds.Contains(obj.Id))
      .ToList();
    if (selectedPool.Count == 0)
    {
      RhinoApp.WriteLine("vNotches: keep at least one curve selected.");
      SelectBothCurves(doc, s);
      return false;
    }

    var selectionPoints = new Dictionary<Guid, Point3d>();
    var getObjectOrder = new List<Guid>();
    for (int i = 0; i < go.ObjectCount; i++)
    {
      var objRef = go.Object(i);
      if (objRef == null || objRef.ObjectId == Guid.Empty)
        continue;
      var point = objRef.SelectionPoint();
      if (point != Point3d.Unset)
        selectionPoints[objRef.ObjectId] = point;
      getObjectOrder.Add(objRef.ObjectId);
    }

    var selectedById = selectedPool.ToDictionary(obj => obj.Id);
    var getSelectionOrder = new List<Guid>();
    var getSelectionIds = new HashSet<Guid>();
    foreach (var id in getObjectOrder)
      if (selectedById.ContainsKey(id) && getSelectionIds.Add(id))
        getSelectionOrder.Add(id);

    var existingSourceOrder = s.PerCurveSourceIds.SelectMany(ids => ids).ToList();
    var existingSourceSet = existingSourceOrder.ToHashSet();
    bool sameSourceSet = selectedById.Count == existingSourceSet.Count &&
      selectedById.Keys.ToHashSet().SetEquals(existingSourceSet);
    bool explicitlyReselected = getSelectionOrder.Count == selectedById.Count &&
      getSelectionOrder.Count > 0 &&
      getSelectionOrder.All(selectionPoints.ContainsKey);

    var orderedIds = new HashSet<Guid>();
    var selectedObjects = new List<RhinoObject>();

    // Retained source curves keep their chain order. A complete explicit reselection
    // deliberately replaces that order and can also change the chain's clicked end.
    if (!explicitlyReselected)
      foreach (var id in existingSourceOrder)
        if (selectedById.TryGetValue(id, out var retained) && orderedIds.Add(id))
          selectedObjects.Add(retained);
    foreach (var id in getSelectionOrder)
      if (selectedById.TryGetValue(id, out var selected) && orderedIds.Add(id))
        selectedObjects.Add(selected);
    foreach (var selected in selectedPool)
      if (orderedIds.Add(selected.Id))
        selectedObjects.Add(selected);

    var desiredCurves = BuildLogicalCurveSelections(
      doc, selectedObjects, selectionPoints, s);
    var existingKeys = s.PerCurveSourceIds.Select(CurveSetKey).ToList();
    var desiredKeys = desiredCurves.Select(curve => CurveSetKey(curve.SourceIds)).ToList();
    bool sequenceChanged = sameSourceSet && explicitlyReselected &&
      !desiredKeys.SequenceEqual(existingKeys);
    bool clickedEndChanged = desiredCurves.Any(desired =>
    {
      int existingIndex = existingKeys.IndexOf(CurveSetKey(desired.SourceIds));
      return existingIndex >= 0 && existingIndex < s.Curves.Count &&
        desired.Curve.PointAtStart.DistanceTo(s.Curves[existingIndex].PointAtStart) >
          doc.ModelAbsoluteTolerance;
    });
    bool selectionDefinitionChanged = sequenceChanged || clickedEndChanged;

    vTools.Log.Write("vNotches", "selection order: " + string.Join(", ",
      selectedObjects.Select((obj, i) => $"{i + 1}:{obj.Id.ToString("N")[..8]}")) +
      $" sameSources={sameSourceSet} reselected={explicitlyReselected} " +
      $"logical={desiredCurves.Count} sequenceChanged={sequenceChanged} " +
      $"clickedEndChanged={clickedEndChanged}");

    var desiredKeySet = desiredKeys.ToHashSet();
    bool changed = false;
    var removedIndices = Enumerable.Range(0, s.CurveIds.Count)
      .Where(i => selectionDefinitionChanged ||
        i >= existingKeys.Count || !desiredKeySet.Contains(existingKeys[i]))
      .ToList();

    for (int removed = removedIndices.Count - 1; removed >= 0; removed--)
    {
      RemoveSessionCurve(s, removedIndices[removed]);
      changed = true;
    }

    var retainedKeys = s.PerCurveSourceIds.Select(CurveSetKey).ToHashSet();
    foreach (var desired in desiredCurves)
    {
      string key = CurveSetKey(desired.SourceIds);
      if (retainedKeys.Contains(key))
        continue;

      AddSessionCurve(s, desired.PrimaryObject, desired.Curve,
        desired.SourceIds, desired.Segments);
      foreach (var sourceId in desired.SourceIds)
        if (!existingSourceSet.Contains(sourceId) ||
            (selectionDefinitionChanged &&
             desired.SourceIds.Any(selectionPoints.ContainsKey)))
          s.CurveReversedBySource[sourceId] = false;
      retainedKeys.Add(key);
      vTools.Log.Write("vNotches",
        $"added logical curve {s.Curves.Count} from {desired.SourceIds.Count} source curve(s)");
      changed = true;
    }

    var sidesBeforeRestore = s.CurveSides.ToArray();
    RestoreCurveSides(s, sideSequence, existingSourceSet);

    var rebuildIndices = new List<int>();
    for (int i = 0; i < s.CurveSides.Length; i++)
      if (i >= sidesBeforeRestore.Length || s.CurveSides[i] != sidesBeforeRestore[i])
        rebuildIndices.Add(i);

    if (rebuildIndices.Count > 0)
    {
      doc.UndoRecordingEnabled = true;
      uint undoRec = doc.BeginUndoRecord("Curve selection");
      try
      {
        foreach (var i in rebuildIndices)
          RebuildCurveNotches(doc, s, i);
      }
      finally
      {
        doc.EndUndoRecord(undoRec);
      }
    }
    if (changed)
    {
      s.CurveEnabled = Enumerable.Repeat(true, s.Curves.Count).ToArray();
      s.RedoBatches.Clear();
      vTools.Log.Write("vNotches", $"selection changed; enabled all {s.Curves.Count} curve(s)");
    }
    SaveOptions(s);
    vTools.Log.Write("vNotches", $"curve selection end: {DescribeCurveSides(s)}");
    SelectBothCurves(doc, s);
    s.PreviewValid = false;
    s.PreviewLengthsFromStart.Clear();
    return changed;
  }

  static void RemoveSessionCurve(NotchSession s, int curveIndex)
  {
    if (curveIndex < 0 || curveIndex >= s.Curves.Count)
      return;

    var notchIds = s.NotchIdsByCurve[curveIndex];
    var labelIds = s.LabelIdsByCurve[curveIndex];
    for (int recordIndex = 0; recordIndex < s.NotchRecords.Count; recordIndex++)
    {
      var record = s.NotchRecords[recordIndex];
      if (recordIndex < notchIds.Count && notchIds[recordIndex] != Guid.Empty)
        record.DetachedNotchIds.Add(notchIds[recordIndex]);
      Guid? labelId = recordIndex < labelIds.Count ? labelIds[recordIndex] : null;
      if (labelId.HasValue && labelId.Value != Guid.Empty)
        record.DetachedLabelIds.Add(labelId.Value);
    }

    s.NotchIdsByCurve.RemoveAt(curveIndex);
    s.LabelIdsByCurve.RemoveAt(curveIndex);
    foreach (var ids in s.PlacementIds)
      if (curveIndex < ids.Count) ids.RemoveAt(curveIndex);
    foreach (var ids in s.PlacementLabelIds)
      if (curveIndex < ids.Count) ids.RemoveAt(curveIndex);
    foreach (var record in s.NotchRecords)
    {
      if (curveIndex < record.LengthsFromStart.Count) record.LengthsFromStart.RemoveAt(curveIndex);
      if (curveIndex < record.CurveEnabled.Count) record.CurveEnabled.RemoveAt(curveIndex);
      if (curveIndex < record.LabelValues.Count) record.LabelValues.RemoveAt(curveIndex);
    }

    s.Curves.RemoveAt(curveIndex);
    s.CurveIds.RemoveAt(curveIndex);
    if (curveIndex < s.PerCurveSourceIds.Count) s.PerCurveSourceIds.RemoveAt(curveIndex);
    if (curveIndex < s.PerCurveSegments.Count) s.PerCurveSegments.RemoveAt(curveIndex);
    if (curveIndex < s.CurveIsContinuous.Count) s.CurveIsContinuous.RemoveAt(curveIndex);
    s.CurveSides = s.CurveSides.Where((_, i) => i != curveIndex).ToArray();
    s.CurveEnabled = s.CurveEnabled.Where((_, i) => i != curveIndex).ToArray();
    s.SessionGroupIndices = s.SessionGroupIndices.Where((_, i) => i != curveIndex).ToArray();
    s.CurveContextGroupIndices = s.CurveContextGroupIndices.Where((_, i) => i != curveIndex).ToArray();
  }

  static void AddSessionCurve(NotchSession s, RhinoObject rhObj, Curve curve,
    IReadOnlyList<Guid>? allSourceIds = null,
    IReadOnlyList<Curve>? sourceSegments = null,
    bool continuous = true)
  {
    int priorCurveCount = s.Curves.Count;
    bool initialSide = priorCurveCount > 0 && s.CurveSides[^1];
    var groups = rhObj.Attributes.GetGroupList();
    int contextGroup = groups != null && groups.Length > 0 ? groups[0] : -1;

    s.Curves.Add(curve);
    s.CurveIds.Add(rhObj.Id);
    s.PerCurveSourceIds.Add(allSourceIds != null
      ? new List<Guid>(allSourceIds)
      : new List<Guid> { rhObj.Id });
    foreach (var sourceId in s.PerCurveSourceIds[^1])
      if (!s.CurveSideBySource.ContainsKey(sourceId))
        s.CurveSideBySource[sourceId] = initialSide;
    s.EnsureCurveDisplayNumbers();
    s.PerCurveSegments.Add(sourceSegments != null
      ? sourceSegments.Select(segment => segment.DuplicateCurve()).ToList()
      : [curve.DuplicateCurve()]);
    s.CurveIsContinuous.Add(continuous);
    s.CurveSides = s.CurveSides.Append(initialSide).ToArray();
    s.CurveEnabled = s.CurveEnabled.Append(true).ToArray();
    s.SessionGroupIndices = s.SessionGroupIndices.Append(-1).ToArray();
    s.CurveContextGroupIndices = s.CurveContextGroupIndices.Append(contextGroup).ToArray();

    int recordCount = s.NotchRecords.Count;
    s.NotchIdsByCurve.Add(Enumerable.Repeat(Guid.Empty, recordCount).ToList());
    s.LabelIdsByCurve.Add(Enumerable.Repeat<Guid?>(null, recordCount).ToList());
    foreach (var ids in s.PlacementIds) ids.Add(Guid.Empty);
    foreach (var ids in s.PlacementLabelIds) ids.Add(null);

    foreach (var record in s.NotchRecords)
    {
      while (record.LengthsFromStart.Count < priorCurveCount) record.LengthsFromStart.Add(0.0);
      while (record.CurveEnabled.Count < priorCurveCount) record.CurveEnabled.Add(false);
      while (record.LabelValues.Count < priorCurveCount) record.LabelValues.Add("");
      record.LengthsFromStart.Add(0.0);
      record.CurveEnabled.Add(false);
      record.LabelValues.Add("");
    }
  }

  static bool ApplyCurveLayout(
    RhinoDoc doc, NotchSession s, IReadOnlyList<CurveLayoutItem> rows)
  {
    if (rows.Count == 0)
      return false;

    var sideBySource = new Dictionary<Guid, bool>(s.CurveSideBySource);
    var enabledBySource = new Dictionary<Guid, bool>();
    for (int curveIndex = 0; curveIndex < s.PerCurveSourceIds.Count; curveIndex++)
    {
      foreach (var sourceId in s.PerCurveSourceIds[curveIndex])
      {
        if (!sideBySource.ContainsKey(sourceId))
          sideBySource[sourceId] = curveIndex < s.CurveSides.Length && s.CurveSides[curveIndex];
        enabledBySource[sourceId] = curveIndex >= s.CurveEnabled.Length || s.CurveEnabled[curveIndex];
      }
    }

    while (s.Curves.Count > 0)
      RemoveSessionCurve(s, s.Curves.Count - 1);

    var groups = new List<List<CurveLayoutItem>>();
    foreach (var row in rows)
    {
      if (groups.Count == 0 || !row.LinkedToPrevious)
        groups.Add([]);
      groups[^1].Add(row);
    }

    foreach (var group in groups)
    {
      var sourceIds = group.Select(row => row.SourceId).ToList();
      var segments = group.Select(row => row.Curve.DuplicateCurve()).ToList();
      var logicalCurve = BuildLayoutCurve(doc, segments, out bool continuous);
      var primary = doc.Objects.FindId(sourceIds[0]);
      if (primary == null)
        continue;

      AddSessionCurve(s, primary, logicalCurve, sourceIds, segments, continuous);
      int logicalIndex = s.Curves.Count - 1;
      foreach (var sourceId in sourceIds)
        s.CurveSideBySource[sourceId] = sideBySource.GetValueOrDefault(sourceId);
      s.CurveSides[logicalIndex] = sideBySource.GetValueOrDefault(sourceIds[0]);
      s.CurveEnabled[logicalIndex] = enabledBySource.GetValueOrDefault(sourceIds[0], true);
    }

    s.RedoBatches.Clear();
    s.PreviewValid = false;
    s.PreviewLengthsFromStart.Clear();
    SelectBothCurves(doc, s);
    SaveOptions(s);
    vTools.Log.Write("vNotches",
      $"curve rows changed: rows={rows.Count} linkedSequences={s.Curves.Count}");
    return s.Curves.Count > 0;
  }

  static Curve BuildLayoutCurve(
    RhinoDoc doc, List<Curve> segments, out bool continuous)
  {
    continuous = segments.Count <= 1;
    if (segments.Count == 0)
      return new LineCurve(Point3d.Origin, Point3d.Origin);
    if (segments.Count == 1)
      return segments[0].DuplicateCurve();

    double tolerance = doc.ModelAbsoluteTolerance;
    continuous = true;
    for (int i = 1; i < segments.Count; i++)
    {
      Point3d previousEnd = segments[i - 1].PointAtEnd;
      double startDistance = previousEnd.DistanceTo(segments[i].PointAtStart);
      if (startDistance > tolerance)
        continuous = false;
    }

    if (!continuous)
      return segments[0].DuplicateCurve();

    var polyCurve = new PolyCurve();
    foreach (var segment in segments)
      if (!polyCurve.Append(segment.DuplicateCurve()))
      {
        continuous = false;
        return segments[0].DuplicateCurve();
      }
    return polyCurve;
  }

  static double PlacementCurveLength(NotchSession s, int curveIndex)
  {
    if (curveIndex >= 0 && curveIndex < s.PerCurveSegments.Count &&
        s.PerCurveSegments[curveIndex].Count > 0)
      return s.PerCurveSegments[curveIndex].Sum(segment => segment.GetLength());
    return curveIndex >= 0 && curveIndex < s.Curves.Count
      ? s.Curves[curveIndex].GetLength()
      : 0.0;
  }

  static void ResolvePlacementCurve(
    NotchSession s, int curveIndex, double logicalLength,
    KinkTangentChoice? kinkChoice,
    out Curve curve, out double curveLength)
  {
    curve = s.Curves[curveIndex];
    curveLength = Clamp(logicalLength, 0.0, PlacementCurveLength(s, curveIndex));
    if (curveIndex < s.CurveIsContinuous.Count && s.CurveIsContinuous[curveIndex])
      return;

    if (curveIndex >= s.PerCurveSegments.Count || s.PerCurveSegments[curveIndex].Count == 0)
      return;

    var segments = s.PerCurveSegments[curveIndex];
    double tolerance = Math.Max(s.Doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    double remaining = curveLength;
    for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
    {
      double segmentLength = segments[segmentIndex].GetLength();
      if (remaining < segmentLength - tolerance)
      {
        curve = segments[segmentIndex];
        curveLength = remaining;
        return;
      }

      if (Math.Abs(remaining - segmentLength) <= tolerance)
      {
        bool useFollowing = kinkChoice == KinkTangentChoice.After &&
          segmentIndex + 1 < segments.Count;
        curve = useFollowing ? segments[segmentIndex + 1] : segments[segmentIndex];
        curveLength = useFollowing ? 0.0 : segmentLength;
        return;
      }

      remaining -= segmentLength;
    }

    curve = segments[^1];
    curveLength = segments[^1].GetLength();
  }

  static bool TryResolvePlacementSegmentStation(
    NotchSession s, int curveIndex, double logicalLength,
    out int segmentIndex, out double segmentLength, out double localLength)
  {
    segmentIndex = -1;
    segmentLength = 0.0;
    localLength = 0.0;
    if (curveIndex < 0 || curveIndex >= s.PerCurveSegments.Count ||
        s.PerCurveSegments[curveIndex].Count == 0)
      return false;

    var segments = s.PerCurveSegments[curveIndex];
    segmentIndex = ResolvePlacementSourceIndex(s, curveIndex, logicalLength, null);
    segmentIndex = Math.Clamp(segmentIndex, 0, segments.Count - 1);
    segmentLength = segments[segmentIndex].GetLength();
    double prefix = segments.Take(segmentIndex).Sum(segment => segment.GetLength());
    localLength = Clamp(logicalLength - prefix, 0.0, segmentLength);
    return true;
  }

  static void RestoreCurveSides(
    NotchSession s,
    IReadOnlyList<bool> sideSequence,
    IReadOnlySet<Guid> retainedSourceIds)
  {
    bool fallback = sideSequence.Count > 0 && sideSequence[^1];
    var restoredSides = new bool[s.Curves.Count];
    for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
    {
      bool sequenceSide = curveIndex < sideSequence.Count
        ? sideSequence[curveIndex]
        : fallback;
      if (curveIndex >= s.PerCurveSourceIds.Count ||
          s.PerCurveSourceIds[curveIndex].Count == 0)
      {
        restoredSides[curveIndex] = sequenceSide;
        continue;
      }

      foreach (var sourceId in s.PerCurveSourceIds[curveIndex])
        if (!retainedSourceIds.Contains(sourceId))
          s.CurveSideBySource[sourceId] = sequenceSide;

      Guid firstSourceId = s.PerCurveSourceIds[curveIndex][0];
      restoredSides[curveIndex] = s.CurveSideBySource.GetValueOrDefault(
        firstSourceId, sequenceSide);
    }
    s.CurveSides = restoredSides;
  }

  static string DescribeCurveSides(NotchSession s)
  {
    var values = new List<string>();
    for (int i = 0; i < s.CurveIds.Count && i < s.CurveSides.Length; i++)
      values.Add($"{i + 1}:{s.CurveIds[i].ToString("N")[..8]}={(s.CurveSides[i] ? "Left" : "Right")}");
    return values.Count > 0 ? string.Join(", ", values) : "none";
  }

  static void RefreshPanelForCurves(NotchSession s)
  {
    s.Panel?.RefreshCurveRows();
    SyncPanelFromOptions(s);
  }

  // ── Main interactive loop ─────────────────────────────────────────────────

  static void RunLoop(RhinoDoc doc, NotchSession s)
  {
    var gp = new GetPoint();
    gp.EnableTransparentCommands(true);
    gp.AcceptCustomMessage(true);
    gp.MouseMove += (_, e) =>
    {
      try
      {
        s.Panel?.SetViewportPointerActive();

        var vp = e.Viewport;
        if (vp == null)
          return;

        if (!vp.GetFrustumLine(e.WindowPoint.X, e.WindowPoint.Y, out var line))
          return;

        var cplane = vp.ConstructionPlane();
        var plane = new Plane(cplane.Origin, cplane.ZAxis);

        if (Rhino.Geometry.Intersect.Intersection.LinePlane(line, plane, out var t))
          s.LastCursorPoint = line.PointAt(t);
      }
      catch
      {
      }
    };
    gp.SetCommandPrompt(s.Curves.Count == 1
      ? "Select a point on curve (notch location)"
      : "Select a point on any selected curve (notch location)");

    // Live preview
    gp.DynamicDraw += (sender, e) => DrawPreview(doc, s, e);

    // Show panel
    var panel = new NotchPanel(doc, s);
    panel.Show();
    s.Panel = panel;
    SyncPanelFromOptions(s);
    UpdateDistanceLabels(s, null, null, null, null, null, null);

    EventHandler<CommandEventArgs> commandEnded = (_, e) =>
    {
      if (!string.Equals(e.CommandEnglishName, "Redo", StringComparison.OrdinalIgnoreCase))
        return;
      if (e.Document != null && e.Document != doc)
        return;
      s.TransparentRedoRequested = true;
      vTools.Log.Write("vNotches",
        $"Rhino Redo ended result={e.CommandResult} localRedo={s.RedoBatches.Count}");
    };
    Rhino.Commands.Command.EndCommand += commandEnded;
    _activeSession = s;
    _activeGetter = gp;
    LocalUndoRedoShortcutSession? shortcutSession = null;

    try
    {
      shortcutSession = new LocalUndoRedoShortcutSession(
        "vNotches",
        redo => new NotchHistoryRequest(redo, "shortcut"));
      while (true)
      {
        if (s.PanelClosedExit) { FinalizeBlocks(doc, s); return; }

        gp.SetCommandPrompt(s.Curves.Count == 1
          ? "Select a point on curve (notch location)"
          : "Select a point on any selected curve (notch location)");
        RefreshCommandOptions(gp, s);
        var result = gp.Get();

        if (result == GetResult.CustomMessage && gp.CustomMessage() is NotchHistoryRequest historyRequest)
        {
          ApplyLocalHistory(doc, s, historyRequest.Redo, historyRequest.Source);
          continue;
        }

        if (s.TransparentRedoRequested)
        {
          s.TransparentRedoRequested = false;
          RedoLastNotch(doc, s);
          continue;
        }

        if (s.CurveSelectionRequested)
        {
          s.CurveSelectionRequested = false;
          try
          {
            if (TryUpdateCurveSelection(doc, s))
              RefreshPanelForCurves(s);
          }
          finally
          {
            s.Panel?.SetCurveSelectionInProgress(false);
          }
          continue;
        }
        if (s.RefreshCommandLine) { s.RefreshCommandLine = false; continue; }
        if (s.PanelClosedExit)    { FinalizeBlocks(doc, s); return; }

        if (result == GetResult.Undo)
        {
          UndoLastNotch(doc, s);
          continue;
        }

        if (gp.CommandResult() != Result.Success) { FinalizeBlocks(doc, s); return; }

        if (result == GetResult.Nothing)
        {
          if (s.PanelNumericPending) { s.PanelNumericPending = false; continue; }
          if (s.IgnoreNextNothing)   { s.IgnoreNextNothing   = false; continue; }
          // Enter pressed on command line â€” done
          FinalizeBlocks(doc, s);
          return;
        }

        if (result == GetResult.Option)
        {
          HandleOption(doc, gp, s);
          SyncPanelFromOptions(s);
          continue;
        }

        if (result != GetResult.Point)
        {
          FinalizeBlocks(doc, s);
          return;
        }

        // Point picked â€” place notch(es)
        PlaceNotchFromPreview(doc, gp.Point(), s);
      }
    }
    finally
    {
      Rhino.Commands.Command.EndCommand -= commandEnded;
      shortcutSession?.Dispose();
      if (ReferenceEquals(_activeSession, s))
        _activeSession = null;
      if (ReferenceEquals(_activeGetter, gp))
        _activeGetter = null;
      FinalizeBlocks(doc, s);
      var currentPanel = s.Panel;
      if (currentPanel != null)
      {
        try { currentPanel.CommitPendingValues(); } catch { }
        s.SuppressPanelCloseExit = true;
        try { currentPanel.Close(); } catch { }
        s.Panel = null;
      }
    }
  }

  // ── Command options ───────────────────────────────────────────────────────

  static void RefreshCommandOptions(GetPoint gp, NotchSession s)
  {
    gp.ClearCommandOptions();
    s.SideOptionIndex        = gp.AddOption("Side");
    s.ReverseOptionIndex     = gp.AddOption("Reverse");
    s.UndoOptionIndex        = s.NotchRecords.Count > 0
      ? gp.AddOption("Undo", string.Empty, true)
      : -1;
    s.RedoOptionIndex        = s.RedoBatches.Count > 0
      ? gp.AddOption("Redo", string.Empty, true)
      : -1;
    s.TypeOptionIndex        = gp.AddOptionList("NotchType", s.NotchTypeOptionValues, s.NotchTypeIndex);
    s.NotchLayerOptionIndex  = gp.AddOption("NotchLayer", s.NotchLayerName);
    s.NotchEnabledIndex      = gp.AddOptionToggle("NotchEnabled", ref s.NotchToggle);
    gp.AddOptionDouble("NotchLength", ref s.NotchLengthOpt);
    gp.AddOptionDouble("NotchWidth", ref s.NotchWidthOpt);
    gp.AddOptionDouble("NotchOffset", ref s.NotchOffsetOpt);
    s.LabelEnabledIndex      = gp.AddOptionToggle("LabelEnabled", ref s.LabelToggle);
    s.LabelValueOptionIndex  = gp.AddOption("Label", s.LabelValueText);
    s.LabelLayerOptionIndex = gp.AddOption("LabelLayer", s.LabelLayerName);
    s.LabelSizeAutoIndex = gp.AddOptionToggle("LabelSizeMode", ref s.LabelSizeAutoToggle);
    s.LabelSizePctIndex2 = gp.AddOptionList("LabelSizePct", s.LabelSizePctTexts, s.LabelSizePctIndex);
    s.LabelSizeOpt.CurrentValue = s.ManualLabelSize;
    gp.AddOptionDouble("LabelSize", ref s.LabelSizeOpt);
    gp.AddOptionDouble("LabelOffsetX", ref s.LabelOffsetOpt);
    gp.AddOptionDouble("LabelOffsetY", ref s.LabelOffsetYOpt);
    gp.AddOptionToggle("NotchPercent", ref s.PercentToggle);
    gp.AddOptionToggle("NotchGroup", ref s.GroupToggle);
  }

  static void HandleOption(RhinoDoc doc, GetPoint gp, NotchSession s)
  {
    var opt = gp.Option();
    if (opt == null) return;
    int idx = opt.Index;

    // Compute which curve is nearest to last preview point for Side/Reverse
    Point3d? cursor = s.LastPreviewPoint;

    if (idx == s.SideOptionIndex)
    {
      int ci = 0;
      double length = 0.0;
      if (cursor.HasValue)
        ClosestCurveHit(s, cursor.Value, out ci, out _, out length);
      ToggleCurveSide(doc, s, ci, ResolvePlacementSourceCurveId(doc, s, ci, length, null));
    }
    else if (idx == s.ReverseOptionIndex)
    {
      int ci = 0;
      double length = 0.0;
      if (cursor.HasValue)
        ClosestCurveHit(s, cursor.Value, out ci, out _, out length);
      ReverseSourceCurve(doc, s, ci, ResolvePlacementSourceCurveId(doc, s, ci, length, null));
    }
    else if (idx == s.UndoOptionIndex)
    {
      UndoLastNotch(doc, s);
    }
    else if (idx == s.RedoOptionIndex)
    {
      RedoLastNotch(doc, s);
    }
    else if (idx == s.TypeOptionIndex)
    {
      s.NotchTypeIndex = opt.CurrentListOptionIndex;
    }
    else if (idx == s.NotchLayerOptionIndex)
    {
      if (RhinoGet.GetString(
            "Notch layer (. = current)",
            false,
            ref s.NotchLayerName) == Result.Success)
      {
        s.NotchLayerName = LayerSelector.NormalizeCurrentLayerValue(
          s.NotchLayerName,
          SpecialLayerCurrent);
      }
    }
    else if (idx == s.NotchEnabledIndex)
    {
      if (!s.NotchToggle.CurrentValue && !s.LabelToggle.CurrentValue)
        s.LabelToggle.CurrentValue = true;
      SaveOptions(s);
    }
    else if (idx == s.LabelEnabledIndex)
    {
      if (!s.LabelToggle.CurrentValue && !s.NotchToggle.CurrentValue)
        s.NotchToggle.CurrentValue = true;
      SaveOptions(s);
    }
    else if (idx == s.LabelValueOptionIndex)
    {
      RhinoGet.GetString("Label value", false, ref s.LabelValueText);
    }
    else if (idx == s.LabelLayerOptionIndex)
    {
      RhinoGet.GetString("Label layer", false, ref s.LabelLayerName);
    }
    else if (idx == s.LabelSizeAutoIndex)
    {
      // already toggled by RhinoCommon option machinery
    }
    else if (idx == s.LabelSizePctIndex2)
    {
      s.LabelSizePctIndex = opt.CurrentListOptionIndex;
    }

    s.ManualLabelSize = s.LabelSizeOpt.CurrentValue;
  }

  // ── Place notch at clicked point ──────────────────────────────────────────

  static void PlaceNotchAtPoint(RhinoDoc doc, Point3d point, NotchSession s)
  {
    ClosestCurveHit(s, point, out int refIdx, out var refCurve, out double lengthFromStart);
    if (refCurve == null) return;

    s.PreviewRefCurveIndex = refIdx;

    List<double> lengthsFromStart;

    if (s.PercentToggle.CurrentValue)
    {
      double refLen = PlacementCurveLength(s, refIdx);
      if (refLen <= 0.0) return;

      double pct = lengthFromStart / refLen;
      lengthsFromStart = Enumerable.Range(0, s.Curves.Count)
        .Select(i => PlacementCurveLength(s, i) * pct).ToList();
    }
    else
    {
      lengthsFromStart = Enumerable.Repeat(lengthFromStart, s.Curves.Count).ToList();
    }

    PlaceNotchWithLengths(doc, s, lengthsFromStart, s.LastCursorPoint ?? point);
  }
  static void PlaceNotchFromPreview(RhinoDoc doc, Point3d clickedPoint, NotchSession s)
  {
    if (!s.PreviewValid || s.PreviewLengthsFromStart.Count != s.Curves.Count)
    {
      PlaceNotchAtPoint(doc, clickedPoint, s);
      return;
    }

    PlaceNotchWithLengths(doc, s, s.PreviewLengthsFromStart, s.PreviewCursorPoint);
  }

  static bool PlaceNotchWithLengths(RhinoDoc doc, NotchSession s,
    List<double> lengthsFromStart, Point3d? cursorPoint,
    bool allowLabel = true, bool manageUndo = true, bool advanceLabel = true,
    bool updateUi = true, bool usePercentMode = true, Guid? batchId = null,
    bool[]? curveEnabledOverride = null,
    KinkTangentChoice? preferredKinkChoice = null)
  {
    double notchLen = s.NotchLengthOpt.CurrentValue;
    double notchOff = s.NotchOffsetOpt.CurrentValue;
    string notchTyp = s.NotchTypeValues[s.NotchTypeIndex];
    double notchWid = s.NotchWidthOpt.CurrentValue;
    double resolvedLabelSize = EffectiveLabelSize(s);

    string effectiveNotchLayer = EffectiveLayerName(doc, s.NotchLayerName, s.NotchLayerName);
    string effectiveLabelLayer = EffectiveLayerName(doc, s.LabelLayerName, s.NotchLayerName);

    var activeGroupIndices = s.GroupToggle.CurrentValue
      ? s.SessionGroupIndices
      : s.CurveContextGroupIndices;

    string labelText = s.LabelValueText.Trim();
    bool canNotch    = s.NotchToggle.CurrentValue;
    bool canLabel    = allowLabel && s.LabelToggle.CurrentValue && labelText.Length > 0;
    bool[] placementCurveEnabled = curveEnabledOverride ?? s.CurveEnabled;
    string nextLabel = labelText;

    var placementLabels = new List<string>();
    if (canLabel)
    {
      foreach (var _ in s.Curves)
        placementLabels.Add(labelText);

      if (s.LabelAutoAdv)
        nextLabel = IncrementLabelValue(labelText);
    }

    double? percent = null;
    if (usePercentMode && s.PercentToggle.CurrentValue &&
        s.PreviewRefCurveIndex >= 0 && s.PreviewRefCurveIndex < s.Curves.Count)
    {
      var refLen = PlacementCurveLength(s, s.PreviewRefCurveIndex);
      if (refLen > 0.0 && s.PreviewRefCurveIndex < lengthsFromStart.Count)
        percent = lengthsFromStart[s.PreviewRefCurveIndex] / refLen;
    }
    string placementMode = usePercentMode && s.PercentToggle.CurrentValue
      ? "percent"
      : "distance";

    var referenceKinkChoice = preferredKinkChoice ?? KinkTangentChoice.Default;
    if (cursorPoint.HasValue &&
        s.PreviewRefCurveIndex >= 0 && s.PreviewRefCurveIndex < s.Curves.Count &&
        s.PreviewRefCurveIndex < lengthsFromStart.Count)
    {
      ResolvePlacementCurve(s, s.PreviewRefCurveIndex,
        lengthsFromStart[s.PreviewRefCurveIndex], null,
        out var referenceCurve, out double referenceLength);
      referenceKinkChoice = ResolveKinkChoice(
        referenceCurve, referenceLength, cursorPoint.Value);
    }

    uint undoRec = 0;
    bool undoStarted = false;
    if (manageUndo)
    {
      doc.UndoRecordingEnabled = true;
      undoRec = doc.BeginUndoRecord("Notch");
      undoStarted = true;
    }

    List<(Guid notch, Guid? label)>? newIds = null;
    try
    {
      newIds = AddNotchesPerCurve(doc, s, activeGroupIndices,
        lengthsFromStart, notchLen, notchOff, notchTyp, notchWid,
        canNotch, canLabel, placementLabels, resolvedLabelSize,
        effectiveNotchLayer, effectiveLabelLayer,
        s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue,
        s.LabelSideFlip, cursorPoint, referenceKinkChoice, placementCurveEnabled,
        placementMode);
    }
    finally
    {
      if (undoStarted)
        doc.EndUndoRecord(undoRec);
    }

    if (newIds == null || !newIds.Any(n => n.notch != Guid.Empty || n.label.HasValue))
      return false;

    s.RedoBatches.Clear();
    var record = new NotchRecord
    {
      BatchId          = batchId ?? Guid.NewGuid(),
      Mode             = placementMode,
      NotchLength      = notchLen,
      NotchOffset      = notchOff,
      NotchType        = notchTyp,
      NotchWidth       = notchWid,
      NotchEnabled     = canNotch,
      GroupEnabled     = s.GroupToggle.CurrentValue,
      LabelEnabled     = canLabel,
      LabelValues      = new List<string>(placementLabels),
      LabelSize        = resolvedLabelSize,
      NotchLayer       = s.NotchLayerName,
      LabelLayer       = s.LabelLayerName,
      LabelOffset      = s.LabelOffsetOpt.CurrentValue,
      LabelOffsetY     = s.LabelOffsetYOpt.CurrentValue,
      LengthsFromStart = new List<double>(lengthsFromStart),
      CurveEnabled     = placementCurveEnabled.ToList(),
      Percent          = percent,
      KinkChoice       = referenceKinkChoice,
    };

    s.NotchRecords.Add(record);

    for (int i = 0; i < newIds.Count; i++)
    {
      if (i < s.NotchIdsByCurve.Count)
        s.NotchIdsByCurve[i].Add(newIds[i].notch);

      if (i < s.LabelIdsByCurve.Count)
        s.LabelIdsByCurve[i].Add(newIds[i].label);
    }

    s.PlacementIds.Add(new List<Guid>(newIds.Select(n => n.notch)));
    s.PlacementLabelIds.Add(new List<Guid?>(newIds.Select(n => n.label)));

    if (canLabel && s.LabelAutoAdv && advanceLabel)
    {
      s.LabelValueText = nextLabel;
      SyncPanelFromOptions(s);
    }

    if (updateUi)
    {
      s.Panel?.UpdateUndoEnabled();
      doc.Views.Redraw();
    }

    return true;
  }

  static List<List<double>>? ComputeMultiplePositions(RhinoDoc doc, NotchSession s)
  {
    double startOffset = EffectiveMultipleStartOffset(s);
    double endOffset   = EffectiveMultipleEndOffset(s);
    var activeCurveIndices = Enumerable.Range(0, s.Curves.Count)
      .Where(i => i >= s.CurveEnabled.Length || s.CurveEnabled[i]).ToList();
    if (activeCurveIndices.Count == 0) return null;
    foreach (int ci in activeCurveIndices)
      if (PlacementCurveLength(s, ci) - startOffset - endOffset <= doc.ModelAbsoluteTolerance)
        return null;
    int baseCurveIndex = MultipleReferenceCurveIndex(s, activeCurveIndices);
    double baseAvailable = PlacementCurveLength(s, baseCurveIndex) - startOffset - endOffset;
    var ratios = BuildMultipleRatiosForSession(
      doc, s, baseCurveIndex, startOffset, baseAvailable);
    bool usePercent = s.PercentToggle.CurrentValue;
    bool mapByRatio = usePercent || s.MultipleSeparate;
    var result = new List<List<double>>();
    foreach (double ratio in ratios)
    {
      double baseLength = startOffset + baseAvailable * ratio;
      result.Add(mapByRatio
        ? Enumerable.Range(0, s.Curves.Count)
          .Select(i => startOffset + (PlacementCurveLength(s, i) - startOffset - endOffset) * ratio)
          .ToList()
        : Enumerable.Repeat(baseLength, s.Curves.Count).ToList());
    }
    return result;
  }

  static List<MultiplePlacementPlan>? ComputeMultiplePlacementPlans(
    RhinoDoc doc, NotchSession s)
  {
    var positions = ComputeMultiplePositions(doc, s);
    return positions == null ? null : BuildMultiplePlacementPlans(doc, s, positions);
  }

  static List<MultiplePlacementPlan> BuildMultiplePlacementPlans(
    RhinoDoc doc, NotchSession s, IReadOnlyList<List<double>> positions)
  {
    var plans = new List<MultiplePlacementPlan>();
    var existingByCurve = Enumerable.Range(0, s.Curves.Count)
      .Select(curveIndex => ExistingNotchLengths(doc, s, curveIndex))
      .ToList();
    double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);

    for (int positionIndex = 0; positionIndex < positions.Count; positionIndex++)
    {
      var lengths = positions[positionIndex];
      var curveEnabled = new bool[s.Curves.Count];
      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        bool active = curveIndex >= s.CurveEnabled.Length || s.CurveEnabled[curveIndex];
        if (!active || curveIndex >= lengths.Count)
          continue;

        if (s.MultipleAuto && s.MultipleSeparate &&
            IsDisconnectedInternalSegmentBoundary(
              s, curveIndex, lengths[curveIndex], tolerance))
          continue;

        if (!s.NotchToggle.CurrentValue)
        {
          curveEnabled[curveIndex] = true;
          continue;
        }

        double clearance = MultipleCandidateClearance(
          s, positions, positionIndex, curveIndex, tolerance);
        double candidateLength = lengths[curveIndex];
        bool tooClose = existingByCurve[curveIndex].Any(existingLength =>
        {
          double distance = Math.Abs(existingLength - candidateLength);
          return distance <= tolerance || distance < clearance - tolerance;
        });
        curveEnabled[curveIndex] = !tooClose;
      }

      if (curveEnabled.Any(enabled => enabled))
        plans.Add(new MultiplePlacementPlan(new List<double>(lengths), curveEnabled));
    }

    return plans;
  }

  static List<double> ExistingNotchLengths(
    RhinoDoc doc, NotchSession s, int curveIndex)
  {
    var lengths = new List<double>();
    if (curveIndex < 0 || curveIndex >= s.NotchIdsByCurve.Count)
      return lengths;

    var notchIds = s.NotchIdsByCurve[curveIndex];
    for (int recordIndex = 0;
         recordIndex < s.NotchRecords.Count && recordIndex < notchIds.Count;
         recordIndex++)
    {
      Guid notchId = notchIds[recordIndex];
      if (notchId == Guid.Empty || doc.Objects.FindId(notchId) == null)
        continue;
      var record = s.NotchRecords[recordIndex];
      if (!record.NotchEnabled ||
          curveIndex >= record.LengthsFromStart.Count ||
          (curveIndex < record.CurveEnabled.Count && !record.CurveEnabled[curveIndex]))
        continue;
      lengths.Add(record.LengthsFromStart[curveIndex]);
    }
    return lengths;
  }

  static double MultipleCandidateClearance(
    NotchSession s,
    IReadOnlyList<List<double>> positions,
    int positionIndex,
    int curveIndex,
    double tolerance)
  {
    if (!s.MultipleAuto && s.MultipleUseDistance && s.MultipleDistance > tolerance)
      return s.MultipleDistance * MultipleExistingNotchClearanceScale;

    if (positionIndex < 0 || positionIndex >= positions.Count ||
        curveIndex < 0 || curveIndex >= positions[positionIndex].Count)
      return tolerance;

    double current = positions[positionIndex][curveIndex];
    double localSpacing = double.PositiveInfinity;
    if (positionIndex > 0 && curveIndex < positions[positionIndex - 1].Count)
      localSpacing = Math.Min(
        localSpacing,
        Math.Abs(current - positions[positionIndex - 1][curveIndex]));
    if (positionIndex + 1 < positions.Count &&
        curveIndex < positions[positionIndex + 1].Count)
      localSpacing = Math.Min(
        localSpacing,
        Math.Abs(positions[positionIndex + 1][curveIndex] - current));

    if (!double.IsFinite(localSpacing) || localSpacing <= tolerance)
    {
      double available = PlacementCurveLength(s, curveIndex) -
        EffectiveMultipleStartOffset(s) - EffectiveMultipleEndOffset(s);
      int intervalCount = Math.Max(1, s.MultipleNumber - 1);
      localSpacing = available > tolerance ? available / intervalCount : tolerance;
      if (s.MultipleDistance > tolerance)
        localSpacing = Math.Min(localSpacing, s.MultipleDistance);
    }

    return Math.Max(tolerance, localSpacing * MultipleExistingNotchClearanceScale);
  }

  static void PlaceMultipleNotches(RhinoDoc doc, NotchSession s)
  {
    double startOffset = EffectiveMultipleStartOffset(s);
    double endOffset = EffectiveMultipleEndOffset(s);
    bool usePercent = s.PercentToggle.CurrentValue;
    var activeCurveIndices = Enumerable.Range(0, s.Curves.Count)
      .Where(i => i >= s.CurveEnabled.Length || s.CurveEnabled[i])
      .ToList();

    if (activeCurveIndices.Count == 0)
    {
      RhinoApp.WriteLine("vNotches: enable at least one curve before adding multiple notches.");
      return;
    }

    foreach (int curveIndex in activeCurveIndices)
    {
      double available = PlacementCurveLength(s, curveIndex) - startOffset - endOffset;
      if (available <= doc.ModelAbsoluteTolerance)
      {
        RhinoApp.WriteLine(
          $"vNotches: start and end offsets leave no usable distance on curve {curveIndex + 1}.");
        return;
      }
    }

    int baseCurveIndex = MultipleReferenceCurveIndex(s, activeCurveIndices);
    double baseAvailable = PlacementCurveLength(s, baseCurveIndex) - startOffset - endOffset;
    var positions = ComputeMultiplePositions(doc, s);
    if (positions == null)
      return;
    var plans = BuildMultiplePlacementPlans(doc, s, positions);
    int count = plans.Count;
    vTools.Log.Write("vNotches",
      $"multiple planned={positions.Count} additions={count} " +
      $"spacingMode={(s.MultipleAuto ? "auto" : s.MultipleUseDistance ? "distance" : "number")} " +
      $"distance={s.MultipleDistance:0.###} sensitivity={s.MultipleCurvatureSensitivity:0.###} " +
      $"percent={usePercent} separate={s.MultipleSeparate} " +
      $"reference={(s.MultipleAuto ? "all" : (baseCurveIndex + 1).ToString())} " +
      $"baseAvailable={baseAvailable:0.###}");

    if (count == 0)
    {
      RhinoApp.WriteLine("vNotches: all multiple positions are already occupied or too close to existing notches.");
      return;
    }

    string originalLabel = s.LabelValueText;
    bool labelActive = s.LabelToggle.CurrentValue && originalLabel.Trim().Length > 0;
    bool firstPlacementAdded = false;
    int placementsAdded = 0;
    var batchId = Guid.NewGuid();

    doc.UndoRecordingEnabled = true;
    uint undoRec = doc.BeginUndoRecord("Multiple notches");
    try
    {
      for (int notchIndex = 0; notchIndex < count; notchIndex++)
      {
        var plan = plans[notchIndex];

        bool added = PlaceNotchWithLengths(doc, s, plan.LengthsFromStart, null,
          allowLabel: notchIndex == 0,
          manageUndo: false,
          advanceLabel: false,
          updateUi: false,
          usePercentMode: usePercent,
          batchId: batchId,
          curveEnabledOverride: plan.CurveEnabled,
          preferredKinkChoice: s.MultipleAuto
            ? KinkTangentChoice.Middle
            : null);
        if (!added)
          continue;

        placementsAdded++;
        if (notchIndex == 0)
          firstPlacementAdded = true;
      }
    }
    finally
    {
      doc.EndUndoRecord(undoRec);
    }

    if (placementsAdded == 0)
      return;

    if (firstPlacementAdded && labelActive && s.LabelAutoAdv)
      s.LabelValueText = IncrementLabelValue(originalLabel.Trim());

    SyncPanelFromOptions(s);
    s.Panel?.UpdateUndoEnabled();
    doc.Views.Redraw();
  }

  static List<double> BuildMultipleCountRatios(
    int requestedCount, bool includeStart, bool includeEnd)
  {
    int count = Math.Clamp(requestedCount, 1, 10000);
    if (count == 1)
    {
      if (includeStart) return [0.0];
      if (includeEnd) return [1.0];
      return [0.5];
    }

    int intervalCount = count + 1 - (includeStart ? 1 : 0) - (includeEnd ? 1 : 0);
    int firstInterval = includeStart ? 0 : 1;
    return Enumerable.Range(0, count)
      .Select(i => (double)(firstInterval + i) / intervalCount)
      .ToList();
  }

  static List<double> BuildMultipleRatios(
    double available, double distance, double tolerance,
    bool includeStart, bool includeEnd)
  {
    var ratios = new List<double>();
    if (available <= tolerance)
      return ratios;

    if (includeStart)
      ratios.Add(0.0);

    if (distance > tolerance)
    {
      double rawIntervalCount = available / distance;
      double ratioTolerance = Math.Max(1e-9, tolerance / Math.Max(distance, tolerance));
      int fullIntervalCount = rawIntervalCount >= 9999.0
        ? 9999
        : Math.Max(1, (int)Math.Floor(rawIntervalCount + ratioTolerance));
      int lastInteriorInterval = includeEnd
        ? fullIntervalCount - 1
        : fullIntervalCount;
      for (int interval = 1; interval <= lastInteriorInterval; interval++)
      {
        if (interval * distance >= available - tolerance)
          break;
        ratios.Add((interval * distance) / available);
      }
    }

    if (includeEnd)
      ratios.Add(1.0);
    return ratios;
  }

  static List<double> BuildMultipleRatiosForSession(
    RhinoDoc doc,
    NotchSession s,
    int baseCurveIndex,
    double startOffset,
    double baseAvailable)
  {
    if (s.MultipleAuto)
    {
      if (s.MultipleDistance <= doc.ModelAbsoluteTolerance)
        return [];
      var activeCurveIndices = Enumerable.Range(0, s.Curves.Count)
        .Where(index => index >= s.CurveEnabled.Length || s.CurveEnabled[index])
        .ToList();
      return s.MultipleSeparate
        ? BuildCombinedSeparatedAutoRatios(doc, s, activeCurveIndices)
        : BuildCombinedCurvatureAwareRatios(
            s,
            activeCurveIndices,
            baseAvailable,
            s.MultipleDistance,
            s.MultipleCurvatureSensitivity * MultipleCurvatureSensitivityUnit,
            doc.ModelAbsoluteTolerance,
            s.MultipleStartOffsetEnabled,
            s.MultipleEndOffsetEnabled);
    }

    if (s.MultipleSeparate)
      return BuildSeparatedMultipleRatios(
        doc, s, baseCurveIndex, startOffset, baseAvailable);

    return s.MultipleUseDistance && s.MultipleDistance > doc.ModelAbsoluteTolerance
      ? BuildMultipleRatios(
          baseAvailable,
          s.MultipleDistance,
          doc.ModelAbsoluteTolerance,
          s.MultipleStartOffsetEnabled,
          s.MultipleEndOffsetEnabled)
      : BuildMultipleCountRatios(
          s.MultipleNumber,
          s.MultipleStartOffsetEnabled,
          s.MultipleEndOffsetEnabled);
  }

  static int MultipleReferenceCurveIndex(
    NotchSession s,
    IReadOnlyCollection<int> activeCurveIndices)
  {
    return s.MultipleSeparate
      ? activeCurveIndices
        .OrderByDescending(index => PlacementSegmentCount(s, index))
        .ThenBy(index => PlacementCurveLength(s, index))
        .First()
      : activeCurveIndices
        .OrderBy(index => PlacementCurveLength(s, index))
        .First();
  }

  static int PlacementSegmentCount(NotchSession s, int curveIndex) =>
    curveIndex >= 0 && curveIndex < s.PerCurveSegments.Count &&
    s.PerCurveSegments[curveIndex].Count > 0
      ? s.PerCurveSegments[curveIndex].Count
      : 1;

  static IReadOnlyList<Curve> PlacementSegments(NotchSession s, int curveIndex) =>
    curveIndex >= 0 && curveIndex < s.PerCurveSegments.Count &&
    s.PerCurveSegments[curveIndex].Count > 0
      ? s.PerCurveSegments[curveIndex]
      : [s.Curves[curveIndex]];

  static bool PlacementSegmentsTouch(
    IReadOnlyList<Curve> segments, int boundaryIndex, double tolerance) =>
    boundaryIndex >= 0 && boundaryIndex + 1 < segments.Count &&
    segments[boundaryIndex].PointAtEnd.DistanceTo(
      segments[boundaryIndex + 1].PointAtStart) <= tolerance;

  static bool IsDisconnectedInternalSegmentBoundary(
    NotchSession s, int curveIndex, double logicalLength, double tolerance)
  {
    var segments = PlacementSegments(s, curveIndex);
    if (segments.Count < 2)
      return false;

    double cumulativeLength = 0.0;
    for (int boundaryIndex = 0; boundaryIndex + 1 < segments.Count; boundaryIndex++)
    {
      cumulativeLength += segments[boundaryIndex].GetLength();
      if (Math.Abs(logicalLength - cumulativeLength) <= tolerance)
        return !PlacementSegmentsTouch(segments, boundaryIndex, tolerance);
    }
    return false;
  }

  static List<double> BuildSeparatedMultipleRatios(
    RhinoDoc doc,
    NotchSession s,
    int referenceCurveIndex,
    double startOffset,
    double referenceAvailable)
  {
    var result = new List<double>();
    var touchingJoinRatios = new List<double>();
    double cumulativeLength = 0.0;
    double ratioTolerance = Math.Max(
      1.0e-9,
      doc.ModelAbsoluteTolerance / Math.Max(referenceAvailable, doc.ModelAbsoluteTolerance));
    var segments = PlacementSegments(s, referenceCurveIndex);
    for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
    {
      var segment = segments[segmentIndex];
      double segmentLength = segment.GetLength();
      bool sequenceStart = segmentIndex == 0;
      bool sequenceEnd = segmentIndex + 1 == segments.Count;
      double localStartOffset = !s.MultipleAuto || sequenceStart
        ? EffectiveMultipleStartOffset(s)
        : 0.0;
      double localEndOffset = !s.MultipleAuto || sequenceEnd
        ? EffectiveMultipleEndOffset(s)
        : 0.0;
      bool includeStart = s.MultipleAuto
        ? sequenceStart && s.MultipleStartOffsetEnabled
        : s.MultipleStartOffsetEnabled;
      bool includeEnd = s.MultipleAuto
        ? sequenceEnd && s.MultipleEndOffsetEnabled
        : s.MultipleEndOffsetEnabled;
      double segmentAvailable = segmentLength - localStartOffset - localEndOffset;
      if (segmentAvailable > doc.ModelAbsoluteTolerance)
      {
        List<double> localRatios;
        if (s.MultipleAuto)
        {
          localRatios = s.MultipleDistance > doc.ModelAbsoluteTolerance
            ? BuildCurvatureAwareCurveRatios(
                segment,
                localStartOffset,
                segmentAvailable,
                s.MultipleDistance,
                s.MultipleCurvatureSensitivity * MultipleCurvatureSensitivityUnit,
                doc.ModelAbsoluteTolerance,
                includeStart,
                includeEnd)
            : [];
        }
        else
        {
          localRatios = s.MultipleUseDistance &&
              s.MultipleDistance > doc.ModelAbsoluteTolerance
            ? BuildMultipleRatios(
                segmentAvailable,
                s.MultipleDistance,
                doc.ModelAbsoluteTolerance,
                s.MultipleStartOffsetEnabled,
                s.MultipleEndOffsetEnabled)
            : BuildMultipleCountRatios(
                s.MultipleNumber,
                s.MultipleStartOffsetEnabled,
                s.MultipleEndOffsetEnabled);
        }

        foreach (double localRatio in localRatios)
        {
          double logicalLength = cumulativeLength +
            localStartOffset + segmentAvailable * localRatio;
          double globalRatio = (logicalLength - startOffset) / referenceAvailable;
          if (globalRatio >= -ratioTolerance && globalRatio <= 1.0 + ratioTolerance)
            result.Add(Math.Clamp(globalRatio, 0.0, 1.0));
          if (result.Count >= 10000)
            return result;
        }
      }
      cumulativeLength += segmentLength;
      if (s.MultipleAuto && segmentIndex + 1 < segments.Count &&
          PlacementSegmentsTouch(segments, segmentIndex, doc.ModelAbsoluteTolerance))
      {
        double joinRatio = (cumulativeLength - startOffset) / referenceAvailable;
        if (joinRatio > ratioTolerance && joinRatio < 1.0 - ratioTolerance)
          touchingJoinRatios.Add(joinRatio);
      }
    }

    return s.MultipleAuto && touchingJoinRatios.Count > 0
      ? PreferKinkRatios(
          result,
          touchingJoinRatios,
          s.MultipleDistance,
          referenceAvailable,
          doc.ModelAbsoluteTolerance)
      : result;
  }

  static List<double> BuildCombinedSeparatedAutoRatios(
    RhinoDoc doc,
    NotchSession s,
    IReadOnlyCollection<int> activeCurveIndices)
  {
    var combined = new List<double>();
    double startOffset = EffectiveMultipleStartOffset(s);
    double longestAvailable = activeCurveIndices
      .Select(index => PlacementCurveLength(s, index) -
        startOffset - EffectiveMultipleEndOffset(s))
      .DefaultIfEmpty(0.0)
      .Max();
    double ratioTolerance = Math.Max(
      1.0e-9,
      doc.ModelAbsoluteTolerance /
        Math.Max(longestAvailable, doc.ModelAbsoluteTolerance));

    foreach (int curveIndex in activeCurveIndices)
    {
      double available = PlacementCurveLength(s, curveIndex) -
        startOffset - EffectiveMultipleEndOffset(s);
      if (available <= doc.ModelAbsoluteTolerance)
        continue;
      foreach (double ratio in BuildSeparatedMultipleRatios(
        doc, s, curveIndex, startOffset, available))
      {
        if (combined.Any(existing => Math.Abs(existing - ratio) <= ratioTolerance))
          continue;
        combined.Add(ratio);
        if (combined.Count >= 10000)
          return combined.OrderBy(value => value).ToList();
      }
    }

    combined.Sort();
    return combined;
  }

  static List<double> BuildCombinedCurvatureAwareRatios(
    NotchSession s,
    IReadOnlyCollection<int> activeCurveIndices,
    double baseAvailable,
    double maximumDistance,
    double sensitivity,
    double tolerance,
    bool includeStart,
    bool includeEnd)
  {
    double startOffset = EffectiveMultipleStartOffset(s);
    double endOffset = EffectiveMultipleEndOffset(s);
    bool mapByRatio = s.PercentToggle.CurrentValue;
    var curveRanges = activeCurveIndices
      .Select(index => new
      {
        Index = index,
        Start = startOffset,
        Available = mapByRatio
          ? PlacementCurveLength(s, index) - startOffset - endOffset
          : baseAvailable,
      })
      .Where(range => range.Available > tolerance)
      .ToList();
    var ratios = new List<double>();
    if (curveRanges.Count == 0 || maximumDistance <= tolerance)
      return ratios;

    if (includeStart)
      ratios.Add(0.0);

    int sampleCount = curveRanges.Max(range =>
      MultipleAutoSampleCount(range.Available, maximumDistance));
    var previousTangents = curveRanges
      .Select(range => TryPlacementTangentAtLength(
        s, range.Index, range.Start, out var tangent)
          ? (Vector3d?)tangent
          : null)
      .ToArray();
    double accumulatedWeight = 0.0;
    double nextNotchWeight = maximumDistance;
    double previousRatio = 0.0;
    double ratioTolerance = Math.Max(
      1.0e-9,
      tolerance / Math.Max(curveRanges.Max(range => range.Available), tolerance));
    int interiorLimit = includeEnd ? 9999 : 10000;

    for (int sampleIndex = 1;
         sampleIndex <= sampleCount && ratios.Count < interiorLimit;
         sampleIndex++)
    {
      double currentRatio = sampleIndex == sampleCount
        ? 1.0
        : (double)sampleIndex / sampleCount;
      double ratioStep = currentRatio - previousRatio;
      double weightedStep = 0.0;

      for (int curveOffset = 0; curveOffset < curveRanges.Count; curveOffset++)
      {
        var range = curveRanges[curveOffset];
        double currentLength = range.Start + range.Available * currentRatio;
        Vector3d? currentTangent = TryPlacementTangentAtLength(
          s, range.Index, currentLength, out var tangent)
            ? tangent
            : null;
        double turnAngle = 0.0;
        if (previousTangents[curveOffset].HasValue && currentTangent.HasValue)
        {
          turnAngle = Vector3d.VectorAngle(
            previousTangents[curveOffset]!.Value,
            currentTangent.Value);
          if (!double.IsFinite(turnAngle))
            turnAngle = 0.0;
        }

        double curveWeight = range.Available * ratioStep +
          Math.Max(0.0, sensitivity) * maximumDistance * Math.Max(0.0, turnAngle);
        weightedStep = Math.Max(weightedStep, curveWeight);
        previousTangents[curveOffset] = currentTangent;
      }

      if (weightedStep > RhinoMath.ZeroTolerance &&
          nextNotchWeight <= accumulatedWeight + weightedStep + tolerance)
      {
        double fraction = Math.Clamp(
          (nextNotchWeight - accumulatedWeight) / weightedStep,
          0.0,
          1.0);
        double ratio = previousRatio + ratioStep * fraction;
        if (ratio > ratioTolerance && ratio < 1.0 - ratioTolerance)
          ratios.Add(ratio);
        do
        {
          nextNotchWeight += maximumDistance;
        }
        while (nextNotchWeight <= accumulatedWeight + weightedStep + tolerance);
      }

      accumulatedWeight += weightedStep;
      previousRatio = currentRatio;
    }

    if (includeEnd)
    {
      if (ratios.Count >= 10000)
        ratios[^1] = 1.0;
      else
        ratios.Add(1.0);
    }
    var kinkRatios = curveRanges.SelectMany(range =>
      PlacementKinkLengths(s, range.Index, tolerance)
        .Where(length =>
          length > range.Start + tolerance &&
          length < range.Start + range.Available - tolerance)
        .Select(length => (length - range.Start) / range.Available));
    return PreferKinkRatios(
      ratios, kinkRatios, maximumDistance, baseAvailable, tolerance);
  }

  static List<double> BuildCurvatureAwareCurveRatios(
    Curve curve,
    double startOffset,
    double available,
    double maximumDistance,
    double sensitivity,
    double tolerance,
    bool includeStart,
    bool includeEnd)
  {
    var ratios = BuildCurvatureAwareRatiosCore(
      available,
      maximumDistance,
      sensitivity,
      tolerance,
      includeStart,
      includeEnd,
      localLength => TryCurveTangentAtLength(
        curve,
        startOffset + localLength,
        out var tangent)
          ? tangent
          : null);
    var kinkRatios = CurveKinkLengths(curve, tolerance)
      .Where(length =>
        length > startOffset + tolerance &&
        length < startOffset + available - tolerance)
      .Select(length => (length - startOffset) / available);
    return PreferKinkRatios(
      ratios, kinkRatios, maximumDistance, available, tolerance);
  }

  static List<double> BuildCurvatureAwareRatiosCore(
    double available,
    double maximumDistance,
    double sensitivity,
    double tolerance,
    bool includeStart,
    bool includeEnd,
    Func<double, Vector3d?> tangentAtLength)
  {
    var ratios = new List<double>();
    if (available <= tolerance || maximumDistance <= tolerance)
      return ratios;

    if (includeStart)
      ratios.Add(0.0);

    int sampleCount = MultipleAutoSampleCount(available, maximumDistance);
    double sampleLength = available / sampleCount;
    double accumulatedWeight = 0.0;
    double nextNotchWeight = maximumDistance;
    double previousLength = 0.0;
    Vector3d? previousTangent = tangentAtLength(previousLength);
    double ratioTolerance = Math.Max(1.0e-9, tolerance / available);
    int interiorLimit = includeEnd ? 9999 : 10000;

    for (int sampleIndex = 1;
         sampleIndex <= sampleCount && ratios.Count < interiorLimit;
         sampleIndex++)
    {
      double currentLength = sampleIndex == sampleCount
        ? available
        : sampleLength * sampleIndex;
      double physicalStep = currentLength - previousLength;
      Vector3d? currentTangent = tangentAtLength(currentLength);
      double turnAngle = 0.0;
      if (previousTangent.HasValue && currentTangent.HasValue)
      {
        turnAngle = Vector3d.VectorAngle(
          previousTangent.Value,
          currentTangent.Value);
        if (double.IsNaN(turnAngle) || double.IsInfinity(turnAngle))
          turnAngle = 0.0;
      }

      double weightedStep = physicalStep +
        Math.Max(0.0, sensitivity) * maximumDistance * Math.Max(0.0, turnAngle);
      if (weightedStep > RhinoMath.ZeroTolerance)
      {
        if (nextNotchWeight <= accumulatedWeight + weightedStep + tolerance &&
            ratios.Count < interiorLimit)
        {
          double fraction = Math.Clamp(
            (nextNotchWeight - accumulatedWeight) / weightedStep,
            0.0,
            1.0);
          double ratio = (previousLength + physicalStep * fraction) / available;
          if (ratio > ratioTolerance && ratio < 1.0 - ratioTolerance)
            ratios.Add(ratio);
          do
          {
            nextNotchWeight += maximumDistance;
          }
          while (nextNotchWeight <= accumulatedWeight + weightedStep + tolerance);
        }
      }

      accumulatedWeight += weightedStep;
      previousLength = currentLength;
      previousTangent = currentTangent;
    }

    if (includeEnd)
    {
      if (ratios.Count >= 10000)
        ratios[^1] = 1.0;
      else
        ratios.Add(1.0);
    }
    return ratios;
  }

  static int MultipleAutoSampleCount(double available, double maximumDistance)
  {
    int proportionalSamples = (int)Math.Ceiling(
      Math.Min(
        MultipleAutoMaximumSamples,
        (available / maximumDistance) * MultipleAutoSamplesPerSpacing));
    return Math.Clamp(
      Math.Max(MultipleAutoMinimumSamples, proportionalSamples),
      1,
      MultipleAutoMaximumSamples);
  }

  static List<double> PreferKinkRatios(
    IReadOnlyCollection<double> regularRatios,
    IEnumerable<double> candidateKinkRatios,
    double maximumDistance,
    double available,
    double tolerance)
  {
    double ratioTolerance = Math.Max(
      1.0e-9, tolerance / Math.Max(available, tolerance));
    double snapTolerance = Math.Max(
      ratioTolerance,
      MultipleAutoKinkSnapDistanceScale * maximumDistance /
      Math.Max(available, tolerance));
    var kinks = candidateKinkRatios
      .Where(ratio =>
        double.IsFinite(ratio) &&
        ratio > ratioTolerance &&
        ratio < 1.0 - ratioTolerance)
      .OrderBy(ratio => ratio)
      .Aggregate(new List<double>(), (result, ratio) =>
      {
        if (result.Count == 0 || ratio - result[^1] > ratioTolerance)
          result.Add(ratio);
        return result;
      });
    if (kinks.Count == 0)
      return regularRatios.OrderBy(ratio => ratio).ToList();

    var combined = regularRatios
      .Where(ratio =>
        ratio <= ratioTolerance ||
        ratio >= 1.0 - ratioTolerance ||
        !kinks.Any(kink => Math.Abs(kink - ratio) <= snapTolerance))
      .Concat(kinks)
      .OrderBy(ratio => ratio)
      .ToList();
    var result = new List<double>(Math.Min(combined.Count, 10000));
    foreach (double ratio in combined)
    {
      if (result.Count > 0 && ratio - result[^1] <= ratioTolerance)
        continue;
      result.Add(ratio);
      if (result.Count >= 10000)
        break;
    }
    return result;
  }

  static List<double> PlacementKinkLengths(
    NotchSession s, int curveIndex, double tolerance)
  {
    var result = new List<double>();
    var segments = PlacementSegments(s, curveIndex);
    double cumulativeLength = 0.0;
    for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
    {
      Curve segment = segments[segmentIndex];
      foreach (double localLength in CurveKinkLengths(segment, tolerance))
        result.Add(cumulativeLength + localLength);
      cumulativeLength += segment.GetLength();

      if (segmentIndex + 1 >= segments.Count ||
          segment.PointAtEnd.DistanceTo(segments[segmentIndex + 1].PointAtStart) > tolerance)
        continue;
      Vector3d before = segment.TangentAtEnd;
      Vector3d after = segments[segmentIndex + 1].TangentAtStart;
      double angle = Vector3d.VectorAngle(before, after);
      if (double.IsFinite(angle) &&
          angle >= RhinoMath.ToRadians(MultipleAutoKinkMinimumAngleDegrees))
        result.Add(cumulativeLength);
    }
    return result;
  }

  static List<double> CurveKinkLengths(Curve curve, double tolerance)
  {
    var result = new List<double>();
    Interval domain = curve.Domain;
    double parameter = domain.T0;
    double parameterTolerance = Math.Max(
      RhinoMath.ZeroTolerance,
      Math.Abs(domain.Length) * 1.0e-12);
    int guard = 0;
    while (guard++ < 10000 &&
           curve.GetNextDiscontinuity(
             Continuity.G1_continuous, parameter, domain.T1, out double kinkParameter))
    {
      if (kinkParameter <= parameter + parameterTolerance ||
          kinkParameter >= domain.T1 - parameterTolerance)
        break;
      double length = curve.GetLength(new Interval(domain.T0, kinkParameter));
      double curveLength = curve.GetLength();
      if (length > tolerance && curveLength - length > tolerance)
        result.Add(length);
      parameter = kinkParameter;
    }
    return result;
  }

  static bool TryPlacementTangentAtLength(
    NotchSession s,
    int curveIndex,
    double logicalLength,
    out Vector3d tangent)
  {
    tangent = Vector3d.Unset;
    if (curveIndex < 0 || curveIndex >= s.Curves.Count)
      return false;
    ResolvePlacementCurve(
      s,
      curveIndex,
      logicalLength,
      null,
      out var curve,
      out double curveLength);
    var (_, parameter) = PointAtCurveLength(curve, curveLength);
    if (!parameter.HasValue)
      return false;
    tangent = curve.TangentAt(parameter.Value);
    return tangent.IsValid && tangent.Unitize();
  }

  static bool TryCurveTangentAtLength(
    Curve curve,
    double length,
    out Vector3d tangent)
  {
    tangent = Vector3d.Unset;
    var (_, parameter) = PointAtCurveLength(curve, length);
    if (!parameter.HasValue)
      return false;
    tangent = curve.TangentAt(parameter.Value);
    return tangent.IsValid && tangent.Unitize();
  }

  static double EffectiveMultipleStartOffset(NotchSession s) =>
    s.MultipleStartOffsetEnabled ? Math.Max(0.0, s.MultipleStartOffset) : 0.0;

  static double EffectiveMultipleEndOffset(NotchSession s) =>
    s.MultipleEndOffsetEnabled ? Math.Max(0.0, s.MultipleEndOffset) : 0.0;

  // ── Undo ──────────────────────────────────────────────────────────────────

  static void UndoLastNotch(RhinoDoc doc, NotchSession s)
  {
    if (s.NotchRecords.Count == 0) return;
    int removeCount = 1;
    if (s.NotchRecords[^1].BatchId != Guid.Empty)
    {
      Guid batchId = s.NotchRecords[^1].BatchId;
      removeCount = 0;
      for (int i = s.NotchRecords.Count - 1; i >= 0 && s.NotchRecords[i].BatchId == batchId; i--)
        removeCount++;
    }
    removeCount = Math.Min(removeCount, s.NotchRecords.Count);
    int firstRecordIndex = s.NotchRecords.Count - removeCount;
    var undoBatch = CaptureUndoBatch(doc, s, firstRecordIndex, removeCount);

    var removedRecords = s.NotchRecords
      .Skip(firstRecordIndex)
      .ToList();
    string restoredLabel = removedRecords
      .Where(rec => rec.LabelEnabled)
      .SelectMany(rec => rec.LabelValues)
      .FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
    if (restoredLabel.Length > 0)
      s.LabelValueText = restoredLabel;

    for (int n = 0; n < removeCount; n++)
    {
      var lastIds = s.PlacementIds.Count > 0 ? s.PlacementIds[^1] : [];
      var lastLabelIds = s.PlacementLabelIds.Count > 0 ? s.PlacementLabelIds[^1] : [];
      if (s.PlacementIds.Count > 0)
        s.PlacementIds.RemoveAt(s.PlacementIds.Count - 1);
      if (s.PlacementLabelIds.Count > 0)
        s.PlacementLabelIds.RemoveAt(s.PlacementLabelIds.Count - 1);

      foreach (var id in lastIds)
        if (id != Guid.Empty) DeleteNotchObjects(doc, id);
      foreach (var id in lastLabelIds)
        if (id.HasValue && id.Value != Guid.Empty) doc.Objects.Delete(id.Value, true);
    }

    foreach (var record in removedRecords)
    {
      foreach (var id in record.DetachedNotchIds)
        if (id != Guid.Empty) DeleteNotchObjects(doc, id);
      foreach (var id in record.DetachedLabelIds)
        if (id != Guid.Empty) doc.Objects.Delete(id, true);
    }

    if (removeCount > 0 && s.NotchRecords.Count >= removeCount)
      s.NotchRecords.RemoveRange(s.NotchRecords.Count - removeCount, removeCount);

    foreach (var ids in s.NotchIdsByCurve)
      if (ids.Count >= removeCount) ids.RemoveRange(ids.Count - removeCount, removeCount);
    foreach (var ids in s.LabelIdsByCurve)
      if (ids.Count >= removeCount) ids.RemoveRange(ids.Count - removeCount, removeCount);

    s.RedoBatches.Push(undoBatch);
    vTools.Log.Write("vNotches",
      $"undo removed={removeCount} remaining={s.NotchRecords.Count} redo={s.RedoBatches.Count} curves={s.Curves.Count}");
    SyncPanelFromOptions(s);
    doc.Views.Redraw();
  }

  static void RedoLastNotch(RhinoDoc doc, NotchSession s)
  {
    if (s.RedoBatches.Count == 0) return;

    var batch = s.RedoBatches.Pop();
    int restoredObjects = 0;
    foreach (var placement in batch.Placements)
    {
      var notchIds = new List<Guid>();
      var labelIds = new List<Guid?>();
      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        Guid notchId = curveIndex < placement.Notches.Count
          ? RestoreDocObject(doc, placement.Notches[curveIndex])
          : Guid.Empty;
        Guid labelId = curveIndex < placement.Labels.Count
          ? RestoreDocObject(doc, placement.Labels[curveIndex])
          : Guid.Empty;
        notchIds.Add(notchId);
        labelIds.Add(labelId == Guid.Empty ? null : labelId);
        if (notchId != Guid.Empty) restoredObjects++;
        if (labelId != Guid.Empty) restoredObjects++;
      }

      placement.Record.DetachedNotchIds.Clear();
      foreach (var snapshot in placement.DetachedNotches)
      {
        Guid id = RestoreDocObject(doc, snapshot);
        if (id == Guid.Empty) continue;
        placement.Record.DetachedNotchIds.Add(id);
        restoredObjects++;
      }

      placement.Record.DetachedLabelIds.Clear();
      foreach (var snapshot in placement.DetachedLabels)
      {
        Guid id = RestoreDocObject(doc, snapshot);
        if (id == Guid.Empty) continue;
        placement.Record.DetachedLabelIds.Add(id);
        restoredObjects++;
      }

      s.NotchRecords.Add(placement.Record);
      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        s.NotchIdsByCurve[curveIndex].Add(notchIds[curveIndex]);
        s.LabelIdsByCurve[curveIndex].Add(labelIds[curveIndex]);
      }
      s.PlacementIds.Add(notchIds);
      s.PlacementLabelIds.Add(labelIds);
    }

    s.LabelValueText = batch.LabelValueAfterRedo;
    vTools.Log.Write("vNotches",
      $"redo restored={batch.Placements.Count} objects={restoredObjects} redo={s.RedoBatches.Count} curves={s.Curves.Count}");
    SyncPanelFromOptions(s);
    doc.Views.Redraw();
  }

  static NotchUndoBatch CaptureUndoBatch(
    RhinoDoc doc, NotchSession s, int firstRecordIndex, int recordCount)
  {
    var batch = new NotchUndoBatch(s.LabelValueText);
    for (int offset = 0; offset < recordCount; offset++)
    {
      int recordIndex = firstRecordIndex + offset;
      var record = s.NotchRecords[recordIndex];
      var placement = new NotchPlacementSnapshot(record);
      var notchIds = recordIndex < s.PlacementIds.Count
        ? s.PlacementIds[recordIndex]
        : [];
      var labelIds = recordIndex < s.PlacementLabelIds.Count
        ? s.PlacementLabelIds[recordIndex]
        : [];

      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
      {
        Guid notchId = curveIndex < notchIds.Count ? notchIds[curveIndex] : Guid.Empty;
        Guid? labelId = curveIndex < labelIds.Count ? labelIds[curveIndex] : null;
        placement.Notches.Add(CaptureNotchObject(doc, notchId));
        placement.Labels.Add(CaptureDocObject(doc, labelId ?? Guid.Empty));
      }

      foreach (var id in record.DetachedNotchIds)
      {
        var snapshot = CaptureNotchObject(doc, id);
        if (snapshot != null) placement.DetachedNotches.Add(snapshot);
      }
      foreach (var id in record.DetachedLabelIds)
      {
        var snapshot = CaptureDocObject(doc, id);
        if (snapshot != null) placement.DetachedLabels.Add(snapshot);
      }

      batch.Placements.Add(placement);
    }
    return batch;
  }

  static DocObjectSnapshot? CaptureDocObject(RhinoDoc doc, Guid objectId)
  {
    if (objectId == Guid.Empty) return null;
    var obj = doc.Objects.FindId(objectId);
    var geometry = obj?.Geometry?.Duplicate();
    if (obj == null || geometry == null) return null;
    return new DocObjectSnapshot(geometry, obj.Attributes.Duplicate());
  }

  static DocObjectSnapshot? CaptureNotchObject(RhinoDoc doc, Guid objectId)
  {
    var snapshot = CaptureDocObject(doc, objectId);
    if (snapshot == null)
      return null;

    foreach (var component in RelatedNotchObjects(doc, objectId))
    {
      if (component.Id == objectId)
        continue;
      var componentSnapshot = CaptureDocObject(doc, component.Id);
      if (componentSnapshot != null)
        snapshot.Components.Add(componentSnapshot);
    }
    return snapshot;
  }

  static IReadOnlyList<RhinoObject> RelatedNotchObjects(RhinoDoc doc, Guid objectId)
  {
    var primary = doc.Objects.FindId(objectId);
    if (primary == null)
      return Array.Empty<RhinoObject>();

    string? componentSet = primary.Attributes.GetUserString(NotchComponentSetKey);
    if (string.IsNullOrWhiteSpace(componentSet))
      return [primary];

    var related = doc.Objects.FindByUserString(
      NotchComponentSetKey, componentSet, true) ?? Array.Empty<RhinoObject>();
    return related
      .Where(obj => obj != null)
      .OrderBy(obj => obj.Id == objectId ? 0 : 1)
      .ToList();
  }

  static void DeleteNotchObjects(RhinoDoc doc, Guid objectId)
  {
    foreach (var obj in RelatedNotchObjects(doc, objectId))
      doc.Objects.Delete(obj.Id, true);
  }

  static Guid RestoreDocObject(RhinoDoc doc, DocObjectSnapshot? snapshot)
  {
    if (snapshot == null) return Guid.Empty;
    var geometry = snapshot.Geometry.Duplicate();
    Guid restoredId = geometry == null
      ? Guid.Empty
      : doc.Objects.Add(geometry, snapshot.Attributes.Duplicate());
    if (restoredId == Guid.Empty)
      return Guid.Empty;

    foreach (var component in snapshot.Components)
      RestoreDocObject(doc, component);
    return restoredId;
  }

  // ── Finalize ──────────────────────────────────────────────────────────────

  static void FinalizeBlocks(RhinoDoc doc, NotchSession s)
  {
    if (s.Finalized) return;
    s.Finalized = true;
    doc.Views.Redraw();
  }

  // ── Dynamic draw preview ──────────────────────────────────────────────────

  static void DrawPreview(RhinoDoc doc, NotchSession s, GetPointDrawEventArgs e)
  {
    var snapPoint = e.CurrentPoint;
    var cursorPoint = s.LastCursorPoint ?? snapPoint;

    s.LastPreviewPoint = snapPoint;
    s.PreviewValid = false;
    s.PreviewSnapPoint = snapPoint;
    s.PreviewCursorPoint = cursorPoint;

    ClosestCurveHit(s, snapPoint, out int refIdx, out var refCurve, out double lfs);
    if (refCurve == null)
    {
      s.Panel?.SetViewportCurveHover(-1, 0.0);
      UpdateDistanceLabels(s, null, null, null, null, null, null);
      return;
    }

    double refTotal = PlacementCurveLength(s, refIdx);
    double otherEnd = Math.Max(0.0, refTotal - lfs);
    double? prevDelta = null;
    if (s.NotchRecords.Count > 0)
    {
      var lastRec = s.NotchRecords[^1];
      double prevLen = LengthFromRecord(s, lastRec, refIdx);
      prevDelta = Math.Abs(lfs - prevLen);
    }

    double? segmentStart = null;
    double? segmentEnd = null;
    double? segmentPrevDelta = null;
    if (PlacementSegmentCount(s, refIdx) > 1 &&
        TryResolvePlacementSegmentStation(
          s, refIdx, lfs, out int segmentIndex, out double segmentLength,
          out double localLength))
    {
      segmentStart = localLength;
      segmentEnd = Math.Max(0.0, segmentLength - localLength);
      for (int recordIndex = s.NotchRecords.Count - 1; recordIndex >= 0; recordIndex--)
      {
        double previousLength = LengthFromRecord(s, s.NotchRecords[recordIndex], refIdx);
        if (!TryResolvePlacementSegmentStation(
              s, refIdx, previousLength, out int previousSegmentIndex,
              out _, out double previousLocalLength) ||
            previousSegmentIndex != segmentIndex)
          continue;

        segmentPrevDelta = Math.Abs(localLength - previousLocalLength);
        break;
      }
    }
    UpdateDistanceLabels(
      s, lfs, prevDelta, otherEnd, segmentStart, segmentPrevDelta, segmentEnd);

    // Compute per-curve positions
    List<double> lengths;
    if (s.PercentToggle.CurrentValue)
    {
      double refLen = refTotal;
      if (refLen <= 0.0) return;
      double pct = lfs / refLen;
      lengths = Enumerable.Range(0, s.Curves.Count)
        .Select(i => PlacementCurveLength(s, i) * pct).ToList();
    }
    else
    {
      lengths = Enumerable.Repeat(lfs, s.Curves.Count).ToList();
    }
    s.PreviewValid = true;
    s.PreviewRefCurveIndex = refIdx;
    s.PreviewLengthsFromStart = new List<double>(lengths);
    s.PreviewSnapPoint = snapPoint;
    double nl    = s.NotchLengthOpt.CurrentValue;
    double no    = s.NotchOffsetOpt.CurrentValue;
    string nt    = s.NotchTypeValues[s.NotchTypeIndex];
    double nw    = s.NotchWidthOpt.CurrentValue;
    double lsize = EffectiveLabelSize(s);
    string ltext = s.LabelValueText.Trim();
    bool   canNotch = s.NotchToggle.CurrentValue;
    bool   canLabel = s.LabelToggle.CurrentValue && ltext.Length > 0 && lsize > doc.ModelAbsoluteTolerance;
    bool curveSnapActive = false;
    try
    {
      curveSnapActive = e.Source.PointOnCurve(out _) != null &&
        snapPoint.DistanceTo(ClosestPointOnPlacementCurve(s, refIdx, snapPoint)) <=
          Math.Max(doc.ModelAbsoluteTolerance * 2.0, RhinoMath.ZeroTolerance * 10.0);
    }
    catch
    {
    }
    s.Panel?.SetViewportCurveHover(curveSnapActive ? refIdx : -1, lfs);

    ResolvePlacementCurve(s, refIdx, lengths[refIdx], null,
      out var referenceCurve, out double referenceLength);
    var snappedKinkChoice = curveSnapActive
      ? ResolveKinkChoice(referenceCurve, referenceLength, snapPoint)
      : KinkTangentChoice.Default;
    bool forceSnappedMiddle = snappedKinkChoice == KinkTangentChoice.Middle;
    var effectiveCursorPoint = forceSnappedMiddle ? snapPoint : cursorPoint;
    s.PreviewCursorPoint = effectiveCursorPoint;
    var referenceKinkChoice = forceSnappedMiddle
      ? KinkTangentChoice.Middle
      : ResolveKinkChoice(referenceCurve, referenceLength, effectiveCursorPoint);

    if (s.KinkCenterSnapActive != forceSnappedMiddle)
    {
      s.KinkCenterSnapActive = forceSnappedMiddle;
      Log.Write("vNotches", forceSnappedMiddle
        ? $"kink center snap locked curve={refIdx + 1}"
        : "kink center snap released");
    }

    // Multiple-add hover preview: draw all positions and suppress the cursor notch.
    if (s.MultipleHoverPreviewActive && s.MultipleHoverPlans != null)
    {
      s.PreviewValid = false;
      for (int hi = 0; hi < s.MultipleHoverPlans.Count; hi++)
      {
        var hoverPlan = s.MultipleHoverPlans[hi];
        var hoverLengths = hoverPlan.LengthsFromStart;
        bool firstPos = hi == 0;
        for (int i = 0; i < s.Curves.Count; i++)
        {
          if (i >= hoverPlan.CurveEnabled.Length || !hoverPlan.CurveEnabled[i]) continue;
          KinkTangentChoice? hoverKinkChoice = s.MultipleAuto
            ? KinkTangentChoice.Middle
            : null;
          ResolvePlacementCurve(s, i, hoverLengths[i], hoverKinkChoice,
            out var hoverCurve, out double hoverLength);
          string side = PlacementCurveSide(s, i, hoverLengths[i], hoverKinkChoice);
          var hgeom = NotchGeometry(
            hoverCurve, hoverLength, nl, no, side, nt, nw,
            null, hoverKinkChoice);
          if (hgeom == null) continue;
          if (canNotch) foreach (var c in hgeom) PreviewDisplay.DrawCurve(e.Display, c, System.Drawing.Color.Cyan, 1);
          if (canLabel && firstPos)
          {
            GetCurveTangentAndDirection(
              hoverCurve, hoverLength, side, null, hoverKinkChoice,
              out var tangent, out var direction);
            if (!tangent.IsValid || !direction.IsValid) continue;
            string firstSide = PlacementCurveSide(s, 0, hoverLengths[0], null);
            string labelCurveSide = ResolvedLabelCurveSide(side, firstSide, i);
            if (s.LabelSideFlip) labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";
            var (previewPlane, _, _) = ComputeLabelLayout(doc, hoverCurve, hoverLength,
              direction, tangent, no, hgeom, ltext, lsize,
              s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue, labelCurveSide);
            if (!previewPlane.IsValid) continue;
            DrawLabelPreview(e.Display, previewPlane, ltext, lsize, System.Drawing.Color.Cyan);
          }
        }
      }
      return;
    }

    for (int i = 0; i < s.Curves.Count; i++)
    {
      if (!s.CurveEnabled[i]) continue;
      Point3d? curveCursor = i == refIdx ? effectiveCursorPoint : null;
      KinkTangentChoice? kinkChoice = referenceKinkChoice == KinkTangentChoice.Default
        ? null
        : referenceKinkChoice;
      ResolvePlacementCurve(s, i, lengths[i], kinkChoice,
        out var placementCurve, out double placementLength);
      string side = PlacementCurveSide(s, i, lengths[i], kinkChoice);
      var geom = NotchGeometry(placementCurve, placementLength, nl, no, side, nt, nw,
        curveCursor, kinkChoice);
      if (geom == null) continue;
      if (canNotch)
      {
        foreach (var component in geom)
          PreviewDisplay.DrawCurve(e.Display, component, System.Drawing.Color.Cyan, 1);
      }

      if (canLabel)
      {
        GetCurveTangentAndDirection(placementCurve, placementLength, side, curveCursor, kinkChoice,
          out var tangent, out var direction);
        if (!tangent.IsValid || !direction.IsValid) continue;

        string firstSide = PlacementCurveSide(s, 0, lengths[0], kinkChoice);
        string labelCurveSide = ResolvedLabelCurveSide(side, firstSide, i);
        if (s.LabelSideFlip)
          labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";

        var (previewPlane, _, _) = ComputeLabelLayout(doc, placementCurve, placementLength,
          direction, tangent, no, geom, ltext, lsize,
          s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue, labelCurveSide);
        if (!previewPlane.IsValid) continue;

        DrawLabelPreview(e.Display, previewPlane, ltext, lsize, System.Drawing.Color.Cyan);
      }
    }
  }

  static void DrawLabelPreview(DisplayPipeline display, Plane plane, string text, double size, System.Drawing.Color color)
  {
    try
    {
      var te = new TextEntity
      {
        Plane         = plane,
        PlainText     = text,
        TextHeight    = size,
        Justification = TextJustification.MiddleCenter,
        DimensionScale= 0.9,
      };
      try { te.DrawForward = false; } catch { }
      display.DrawAnnotation(te, color);
      return;
    }
    catch { }
    // Fallback: 3D text
    try
    {
      var t3d = new Rhino.Display.Text3d(text, plane, size);
      display.Draw3dText(t3d, color);
    }
    catch
    {
      display.DrawDot(plane.Origin, text);
    }
  }

  // ── Notch geometry ────────────────────────────────────────────────────────

  static string CanonicalNotchType(string? notchType)
  {
    string value = (notchType ?? "I").Trim().ToUpperInvariant();
    return value == "OPENV" || value == OpenVNotchType ? OpenVNotchType : value;
  }

  static IReadOnlyList<Curve>? NotchGeometry(Curve curve, double lengthFromStart,
    double notchLength, double notchOffset, string side,
    string notchType, double notchWidth, Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
    bool logOffsetFit = false)
  {
    var (center, t) = PointAtCurveLength(curve, lengthFromStart);
    if (t == null) return null;

    var tangent = curve.TangentAt(t.Value);
    tangent.Z = 0.0;
    if (!tangent.Unitize())
    {
      if (!curve.PerpendicularFrameAt(t.Value, out var frame)) return null;
      tangent = frame.XAxis;
      tangent.Z = 0.0;
      if (!tangent.Unitize()) return null;
    }
    tangent = KinkAwareTangent(curve, t.Value, tangent, cursorPoint, kinkChoice);

    var worldZ   = new Vector3d(0.0, 0.0, 1.0);
    var direction = Vector3d.CrossProduct(worldZ, tangent);
    if (!direction.Unitize()) return null;
    if (side == "Right") direction = -direction;

    notchType = CanonicalNotchType(notchType);

    if (notchType is "I" or "T")
    {
      Point3d start, end;
      if (notchOffset > 0.0)
      {
        var expectedOffsetPoint = center + direction * notchOffset;
        double offsetTolerance = Math.Max(
          RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
          RhinoMath.ZeroTolerance * 10.0);
        var iOffsetCurves = ClosestNotchOffsetCurves(
          curve, expectedOffsetPoint, notchOffset, offsetTolerance);
        try
        {
          bool centerHit = TryFindCenterOffsetContact(
            iOffsetCurves, center, direction, expectedOffsetPoint,
            offsetTolerance, out var centerContact);
          start = centerHit ? centerContact : expectedOffsetPoint;
          end = start - direction * Math.Min(notchLength, notchOffset);

          if (logOffsetFit)
          {
            Log.Write("vNotches",
              $"offset-center type=I hit={centerHit} branches={iOffsetCurves.Count} " +
              $"expectedError={(centerHit ? centerContact.DistanceTo(expectedOffsetPoint) : 0.0):0.######}");
          }
        }
        finally
        {
          foreach (var offsetCurve in iOffsetCurves)
            offsetCurve.Dispose();
        }
      }
      else
      {
        start = center;
        end   = center + direction * notchLength;
      }
      var lc = new LineCurve(start, end);
      if (!lc.IsValid) return null;

      var curves = new List<Curve> { lc };
      if (notchType == "T")
      {
        double halfCap = Math.Max(0.0, notchWidth * 0.5);
        var cap = new LineCurve(end - tangent * halfCap, end + tangent * halfCap);
        if (cap.IsValid)
          curves.Add(cap);
      }
      return curves;
    }

    double totalLength = curve.GetLength();
    double halfWidth   = Math.Max(0.0, notchWidth * 0.5);
    double centerLen   = Clamp(lengthFromStart, 0.0, totalLength);
    double leftRaw     = centerLen - halfWidth;
    double rightRaw    = centerLen + halfWidth;
    if (!TryNotchBase(curve, leftRaw, totalLength, tangent, out var leftBase) ||
        !TryNotchBase(curve, rightRaw, totalLength, tangent, out var rightBase))
      return null;

    var tip = center + direction * notchLength;
    var offsetCurves = new List<Curve>();
    try
    {
      if (notchOffset > 0.0)
      {
        var expectedOffsetPoint = center + direction * notchOffset;
        double offsetTolerance = Math.Max(
          RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
          RhinoMath.ZeroTolerance * 10.0);
        offsetCurves = ClosestNotchOffsetCurves(
          curve, expectedOffsetPoint, notchOffset, offsetTolerance);

        var translation = direction * notchOffset;
        bool centerHit = TryFindCenterOffsetContact(
          offsetCurves, center, direction, expectedOffsetPoint,
          offsetTolerance, out var centerContact);
        if (centerHit)
          translation = centerContact - center;

        leftBase += translation;
        rightBase += translation;
        tip = center - direction * notchLength + translation;

        if (logOffsetFit)
        {
          Log.Write("vNotches",
            $"offset-center type={notchType} hit={centerHit} branches={offsetCurves.Count} " +
            $"expectedError={(centerHit ? centerContact.DistanceTo(expectedOffsetPoint) : 0.0):0.######}");
        }
      }

      if (notchType == "V")
      {
        if (notchOffset > 0.0)
        {
          FitNotchLegsToActualOffset(curve, offsetCurves,
            "V", tip, ref leftBase, tip, ref rightBase, logOffsetFit);
        }
        return [new PolylineCurve(new[] { leftBase, tip, rightBase })];
      }

      if (notchType is "U" or OpenVNotchType)
      {
        // U is a V with its point truncated, not a parallel-sided channel.
        double halfFlat = Math.Max(0.0, notchWidth * 0.25);
        var leftTip  = tip - tangent * halfFlat;
        var rightTip = tip + tangent * halfFlat;
        if (notchOffset > 0.0)
        {
          FitNotchLegsToActualOffset(curve, offsetCurves,
            notchType, leftTip, ref leftBase, rightTip, ref rightBase, logOffsetFit);
        }
        if (notchType == OpenVNotchType)
          return [new LineCurve(leftBase, leftTip), new LineCurve(rightTip, rightBase)];
        return [new PolylineCurve(new[] { leftBase, leftTip, rightTip, rightBase })];
      }

      // Fallback
      return [new LineCurve(center, center + direction * notchLength)];
    }
    finally
    {
      foreach (var offsetCurve in offsetCurves)
        offsetCurve.Dispose();
    }
  }

  static bool TryNotchBase(Curve curve, double rawLength, double totalLength,
    Vector3d fallbackTangent, out Point3d basePoint)
  {
    basePoint = Point3d.Unset;
    if (totalLength <= RhinoMath.ZeroTolerance)
      return false;

    double curveLength;
    double extension = 0.0;
    if (curve.IsClosed)
    {
      curveLength = rawLength % totalLength;
      if (curveLength < 0.0)
        curveLength += totalLength;
    }
    else
    {
      curveLength = Clamp(rawLength, 0.0, totalLength);
      extension = rawLength - curveLength;
    }

    var (point, parameter) = PointAtCurveLength(curve, curveLength);
    if (parameter == null)
      return false;

    var localTangent = curve.TangentAt(parameter.Value);
    localTangent.Z = 0.0;
    if (!localTangent.Unitize())
    {
      localTangent = fallbackTangent;
      localTangent.Z = 0.0;
      if (!localTangent.Unitize())
        return false;
    }

    basePoint = point + localTangent * extension;
    return true;
  }

  static bool TryFindCenterOffsetContact(IEnumerable<Curve> offsetCurves,
    Point3d center, Vector3d direction, Point3d expectedOffsetPoint,
    double tolerance, out Point3d contactPoint)
  {
    contactPoint = Point3d.Unset;
    var centerDirection = new Vector3d(direction);
    if (!centerDirection.Unitize())
      return false;

    double expectedDistance = center.DistanceTo(expectedOffsetPoint);
    double searchLength = Math.Max(expectedDistance * 4.0, tolerance * 100.0);
    using var centerRay = new LineCurve(
      center - centerDirection * tolerance,
      center + centerDirection * searchLength);

    bool found = false;
    double bestScore = double.MaxValue;
    foreach (var offsetCurve in offsetCurves)
    {
      var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(
        centerRay, offsetCurve, tolerance, tolerance);
      if (events == null)
        continue;

      foreach (var intersectionEvent in events)
      {
        if (!intersectionEvent.IsPoint)
          continue;
        var point = intersectionEvent.PointA;
        double along = Vector3d.Multiply(point - center, centerDirection);
        if (along <= tolerance)
          continue;
        double score = point.DistanceTo(expectedOffsetPoint);
        if (score >= bestScore)
          continue;
        found = true;
        bestScore = score;
        contactPoint = point;
      }
    }

    if (found)
      return true;

    foreach (var offsetCurve in offsetCurves)
    {
      if (!offsetCurve.ClosestPoint(expectedOffsetPoint, out double parameter))
        continue;
      var point = offsetCurve.PointAt(parameter);
      double score = point.DistanceTo(expectedOffsetPoint);
      if (score >= bestScore)
        continue;
      found = true;
      bestScore = score;
      contactPoint = point;
    }

    return found;
  }

  static void FitNotchLegsToActualOffset(Curve sourceCurve, List<Curve> offsetCurves,
    string notchType,
    Point3d leftInner, ref Point3d leftOuter,
    Point3d rightInner, ref Point3d rightOuter,
    bool logOffsetFit)
  {
    double tol = Math.Max(
      RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
      RhinoMath.ZeroTolerance * 10.0);
    if (offsetCurves.Count == 0)
    {
      if (logOffsetFit)
        Log.Write("vNotches", $"offset-fit type={notchType} failed: no offset curve");
      return;
    }

    bool leftHit = TryFitNotchLegEndpoint(
      sourceCurve, offsetCurves, leftInner, leftOuter, tol,
      out var fittedLeft, out double leftShift);
    bool rightHit = TryFitNotchLegEndpoint(
      sourceCurve, offsetCurves, rightInner, rightOuter, tol,
      out var fittedRight, out double rightShift);

    if (leftHit)
      leftOuter = fittedLeft;
    if (rightHit)
      rightOuter = fittedRight;

    if (logOffsetFit)
    {
      Log.Write("vNotches",
        $"offset-fit type={notchType} branches={offsetCurves.Count} " +
        $"leftHit={leftHit} leftShift={leftShift:0.######} " +
        $"rightHit={rightHit} rightShift={rightShift:0.######}");
    }
  }

  static List<Curve> ClosestNotchOffsetCurves(Curve sourceCurve,
    Point3d expectedOffsetPoint, double notchOffset, double tolerance)
  {
    Curve[]? generated;
    try
    {
      generated = sourceCurve.Offset(
        expectedOffsetPoint, Vector3d.ZAxis, notchOffset,
        tolerance, CurveOffsetCornerStyle.Sharp);
    }
    catch
    {
      generated = null;
    }

    return generated == null
      ? new List<Curve>()
      : generated.Where(offsetCurve => offsetCurve != null).ToList();
  }

  static bool TryFitNotchLegEndpoint(Curve sourceCurve, IEnumerable<Curve> offsetCurves,
    Point3d innerPoint, Point3d originalOuterPoint, double tolerance,
    out Point3d fittedOuterPoint, out double lengthShift)
  {
    fittedOuterPoint = originalOuterPoint;
    lengthShift = 0.0;

    var legDirection = originalOuterPoint - innerPoint;
    double originalLength = legDirection.Length;
    if (originalLength <= tolerance || !legDirection.Unitize())
      return false;

    foreach (var offsetCurve in offsetCurves)
    {
      if (!offsetCurve.ClosestPoint(originalOuterPoint, out double closestParameter))
        continue;
      if (offsetCurve.PointAt(closestParameter).DistanceTo(originalOuterPoint) <= tolerance)
        return true;
    }

    var sourceBounds = sourceCurve.GetBoundingBox(true);
    double sourceSpan = sourceBounds.IsValid ? sourceBounds.Diagonal.Length : 0.0;
    double searchLength = Math.Max(
      originalLength * 4.0,
      Math.Max(sourceSpan * 2.0, tolerance * 100.0));
    using var legRay = new LineCurve(
      innerPoint - legDirection * tolerance,
      innerPoint + legDirection * searchLength);

    bool found = false;
    double bestScore = double.MaxValue;
    double bestLength = originalLength;
    Point3d bestPoint = originalOuterPoint;

    foreach (var offsetCurve in offsetCurves)
    {
      var events = Rhino.Geometry.Intersect.Intersection.CurveCurve(
        legRay, offsetCurve, tolerance, tolerance);
      if (events == null)
        continue;

      foreach (var intersectionEvent in events)
      {
        if (!intersectionEvent.IsPoint)
          continue;

        var point = intersectionEvent.PointA;
        double legLength = Vector3d.Multiply(point - innerPoint, legDirection);
        if (legLength <= tolerance)
          continue;

        double score = Math.Abs(legLength - originalLength);
        if (score >= bestScore)
          continue;

        found = true;
        bestScore = score;
        bestLength = legLength;
        bestPoint = point;
      }
    }

    if (!found)
      return false;

    fittedOuterPoint = bestPoint;
    lengthShift = bestLength - originalLength;
    return true;
  }

  // ── Kink-aware tangent ────────────────────────────────────────────────────

  enum KinkTangentChoice
  {
    Default,
    Before,
    Middle,
    After,
  }

  static Vector3d KinkAwareTangent(Curve curve, double t, Vector3d defaultTangent,
    Point3d? cursorPoint, KinkTangentChoice? requestedChoice)
  {
    return KinkAwareTangent(curve, t, defaultTangent, cursorPoint, requestedChoice, out _);
  }

  static Vector3d KinkAwareTangent(Curve curve, double t, Vector3d defaultTangent,
    Point3d? cursorPoint, KinkTangentChoice? requestedChoice,
    out KinkTangentChoice resolvedChoice)
  {
    resolvedChoice = KinkTangentChoice.Default;
    var domain = curve.Domain;
    double span = domain.Length;
    if (span <= 0.0) return defaultTangent;

    double eps     = span * 1e-4;
    double tBefore = Math.Max(domain.T0, t - eps);
    double tAfter  = Math.Min(domain.T1, t + eps);
    if (tBefore >= tAfter) return defaultTangent;

    var tanBefore = curve.TangentAt(tBefore);
    var tanAfter  = curve.TangentAt(tAfter);
    tanBefore.Z = 0.0; tanAfter.Z = 0.0;
    if (!tanBefore.Unitize() || !tanAfter.Unitize()) return defaultTangent;

    double dot = Vector3d.Multiply(tanBefore, tanAfter);
    if (dot >= Math.Cos(5.0 * Math.PI / 180.0)) return defaultTangent; // smooth

    var tanMiddle = tanBefore + tanAfter;
    tanMiddle.Z = 0.0;
    bool middleValid = tanMiddle.IsValid && !tanMiddle.IsTiny() && tanMiddle.Unitize();

    if (requestedChoice.HasValue)
    {
      switch (requestedChoice.Value)
      {
        case KinkTangentChoice.Before:
          resolvedChoice = KinkTangentChoice.Before;
          return tanBefore;
        case KinkTangentChoice.Middle when middleValid:
          resolvedChoice = KinkTangentChoice.Middle;
          return tanMiddle;
        case KinkTangentChoice.After:
          resolvedChoice = KinkTangentChoice.After;
          return tanAfter;
      }
    }

    if (cursorPoint.HasValue)
    {
      try
      {
        var kinkPt   = curve.PointAt(t);
        var ptBefore = curve.PointAt(tBefore);
        var ptAfter  = curve.PointAt(tAfter);

        var cursorDir = new Vector3d(
          cursorPoint.Value.X - kinkPt.X,
          cursorPoint.Value.Y - kinkPt.Y,
          0.0);

        var tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
        if (!cursorDir.IsValid || cursorDir.Length <= tol * 2.0)
        {
          if (middleValid)
          {
            resolvedChoice = KinkTangentChoice.Middle;
            return tanMiddle;
          }

          return defaultTangent;
        }

        if (!cursorDir.Unitize())
          return defaultTangent;

        var dirBefore = new Vector3d(ptBefore.X - kinkPt.X, ptBefore.Y - kinkPt.Y, 0.0);
        var dirAfter  = new Vector3d(ptAfter.X  - kinkPt.X, ptAfter.Y  - kinkPt.Y, 0.0);

        if (!dirBefore.Unitize() || !dirAfter.Unitize())
          return defaultTangent;

        var dirMiddle = dirBefore + dirAfter;
        dirMiddle.Z = 0.0;

        if (!dirMiddle.IsValid || dirMiddle.IsTiny() || !dirMiddle.Unitize())
        {
          double beforeScoreFallback = Vector3d.Multiply(cursorDir, dirBefore);
          double afterScoreFallback  = Vector3d.Multiply(cursorDir, dirAfter);
          resolvedChoice = afterScoreFallback >= beforeScoreFallback
            ? KinkTangentChoice.After
            : KinkTangentChoice.Before;
          return resolvedChoice == KinkTangentChoice.After ? tanAfter : tanBefore;
        }

        double beforeScore = Vector3d.Multiply(cursorDir, dirBefore);
        double afterScore  = Vector3d.Multiply(cursorDir, dirAfter);
        double middleToBefore = Math.Acos(Math.Clamp(
          Vector3d.Multiply(dirMiddle, dirBefore), -1.0, 1.0));
        double middleToAfter = Math.Acos(Math.Clamp(
          Vector3d.Multiply(dirMiddle, dirAfter), -1.0, 1.0));
        double cursorToMiddle = Math.Acos(Math.Clamp(
          Vector3d.Multiply(cursorDir, dirMiddle), -1.0, 1.0));

        // Give the center choice a broad angular sector. The side choices
        // remain available only near their respective outgoing directions.
        const double middleSectorFraction = 0.75; // Fraction of kink neighborhood reserved for center snapping; zero through one.
        double middleHalfWidth = middleSectorFraction *
          Math.Min(middleToBefore, middleToAfter);
        if (middleValid && cursorToMiddle <= middleHalfWidth)
        {
          resolvedChoice = KinkTangentChoice.Middle;
          return tanMiddle;
        }

        resolvedChoice = afterScore >= beforeScore
          ? KinkTangentChoice.After
          : KinkTangentChoice.Before;
        return resolvedChoice == KinkTangentChoice.After ? tanAfter : tanBefore;
      }
      catch
      {
        return defaultTangent;
      }
    }
    return defaultTangent;
  }

  static KinkTangentChoice ResolveKinkChoice(Curve curve, double lengthFromStart,
    Point3d cursorPoint)
  {
    GetCurveTangentAndDirection(curve, lengthFromStart, "Left", cursorPoint, null,
      out _, out _, out var resolvedChoice);
    return resolvedChoice;
  }

  // ── Tangent + direction ───────────────────────────────────────────────────

  static void GetCurveTangentAndDirection(Curve curve, double lengthFromStart, string side,
    Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
    out Vector3d tangent, out Vector3d direction)
  {
    GetCurveTangentAndDirection(curve, lengthFromStart, side, cursorPoint, kinkChoice,
      out tangent, out direction, out _);
  }

  static void GetCurveTangentAndDirection(Curve curve, double lengthFromStart, string side,
    Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
    out Vector3d tangent, out Vector3d direction, out KinkTangentChoice resolvedChoice)
  {
    tangent   = Vector3d.Unset;
    direction = Vector3d.Unset;
    resolvedChoice = KinkTangentChoice.Default;
    var (_, t) = PointAtCurveLength(curve, lengthFromStart);
    if (t == null) return;

    tangent = curve.TangentAt(t.Value);
    tangent.Z = 0.0;
    if (!tangent.Unitize())
    {
      if (!curve.PerpendicularFrameAt(t.Value, out var frame)) return;
      tangent = frame.XAxis;
      tangent.Z = 0.0;
      if (!tangent.Unitize()) { tangent = Vector3d.Unset; return; }
    }
    tangent = KinkAwareTangent(curve, t.Value, tangent, cursorPoint, kinkChoice,
      out resolvedChoice);

    var worldZ = new Vector3d(0.0, 0.0, 1.0);
    direction  = Vector3d.CrossProduct(worldZ, tangent);
    if (!direction.Unitize()) { direction = Vector3d.Unset; return; }
    if (side == "Right") direction = -direction;
  }

  // ── Point at curve arc-length ─────────────────────────────────────────────

  static (Point3d pt, double? t) PointAtCurveLength(Curve curve, double lengthFromStart)
  {
    double total = curve.GetLength();
    double clamped = Clamp(lengthFromStart, 0.0, total);
    if (curve.LengthParameter(clamped, out double t))
      return (curve.PointAt(t), t);
    if (clamped <= 0.0) return (curve.PointAtStart, curve.Domain.T0);
    return (curve.PointAtEnd, curve.Domain.T1);
  }

  static double LengthFromStart(Curve curve, double t)
  {
    var domain = curve.Domain;
    var interval = new Interval(domain.T0, t);
    return curve.GetLength(interval);
  }

  // ── Label placement ───────────────────────────────────────────────────────

  static Plane BuildReadableTextPlane(RhinoDoc doc, Point3d anchor,
    Vector3d tangent, Vector3d direction, Point3d? curvePoint)
  {
    var xAxis = new Vector3d(tangent);
    if (!xAxis.Unitize()) return Plane.Unset;

    var view   = doc.Views.ActiveView;
    var upAxis = view != null
      ? view.ActiveViewport.ConstructionPlane().ZAxis
      : new Vector3d(0.0, 0.0, 1.0);
    if (!upAxis.Unitize()) upAxis = new Vector3d(0.0, 0.0, 1.0);

    var yAxis = Vector3d.CrossProduct(upAxis, xAxis);
    if (!yAxis.Unitize())
    {
      yAxis = new Vector3d(-direction.X, -direction.Y, -direction.Z);
      if (!yAxis.Unitize()) return Plane.Unset;
    }

    var plane = new Plane(anchor, xAxis, yAxis);
    if (!plane.IsValid) return Plane.Unset;

    var refX = view != null
      ? view.ActiveViewport.ConstructionPlane().XAxis
      : new Vector3d(1.0, 0.0, 0.0);
    if (!refX.Unitize()) refX = new Vector3d(1.0, 0.0, 0.0);

    if (Vector3d.Multiply(plane.XAxis, refX) < 0.0)
    {
      xAxis = -xAxis; yAxis = -yAxis;
      plane = new Plane(anchor, xAxis, yAxis);
      if (!plane.IsValid) return Plane.Unset;
    }

    if (curvePoint.HasValue)
    {
      var toCurve = new Vector3d(
        curvePoint.Value.X - anchor.X,
        curvePoint.Value.Y - anchor.Y,
        curvePoint.Value.Z - anchor.Z);
      if (Vector3d.Multiply(toCurve, plane.YAxis) < 0.0)
      {
        xAxis = -xAxis; yAxis = -yAxis;
        plane = new Plane(anchor, xAxis, yAxis);
        if (!plane.IsValid) return Plane.Unset;
      }
    }
    return plane;
  }

  static double EstimatedLabelHalfWidth(string text, double height)
  {
    if (height <= 0.0 || string.IsNullOrEmpty(text)) return 0.5 * height * 0.65;
    double units = 0.0;
    foreach (char ch in text)
    {
      if ("ilI1|".IndexOf(ch) >= 0)             units += 0.35;
      else if ("mwMW@#%&".IndexOf(ch) >= 0)     units += 0.95;
      else if (".,;:!`'\"".IndexOf(ch) >= 0)    units += 0.28;
      else if ("-_/\\".IndexOf(ch) >= 0)         units += 0.45;
      else if (ch == ' ')                        units += 0.32;
      else if (char.IsDigit(ch))                 units += 0.62;
      else                                       units += 0.68;
    }
    double w = Math.Max(height * 0.65, units * height);
    return 0.5 * w;
  }

  static double GeometryXInPlane(Plane plane, Point3d pt)
  {
    if (plane.ClosestParameter(pt, out double u, out _)) return u;
    return Vector3d.Multiply(new Vector3d(pt.X, pt.Y, pt.Z), plane.XAxis);
  }

  static (double min, double max) GeometryXRangeInPlane(
    IReadOnlyList<Curve> geometry, Plane plane)
  {
    var values = new List<double>();
    foreach (var geom in geometry)
    {
      if (geom is LineCurve lc)
      {
        values.Add(GeometryXInPlane(plane, lc.Line.From));
        values.Add(GeometryXInPlane(plane, lc.Line.To));
      }
      else if (geom is PolylineCurve plc)
      {
        foreach (var pt in plc.ToPolyline())
          values.Add(GeometryXInPlane(plane, pt));
      }
      else
      {
        var bbox = geom.GetBoundingBox(plane);
        if (bbox.IsValid)
        {
          values.Add(bbox.Min.X);
          values.Add(bbox.Max.X);
        }
      }
    }
    if (values.Count > 0) return (values.Min(), values.Max());
    return (0.0, 0.0);
  }

  static double MeasuredLabelHalfWidth(RhinoDoc doc, string text, double height, Plane plane)
  {
    if (height <= doc.ModelAbsoluteTolerance) return 0.0;
    // Measure in WorldXY for orientation-independent width
    var te = new TextEntity
    {
      Plane         = Plane.WorldXY,
      PlainText     = text,
      TextHeight    = height,
      Justification = TextJustification.MiddleCenter,
      DimensionScale= 0.9,
    };
    var bbox = te.GetBoundingBox(Plane.WorldXY);
    if (bbox.IsValid)
    {
      double w = Math.Max(0.0, bbox.Max.X - bbox.Min.X);
      if (w > doc.ModelAbsoluteTolerance)
        return 0.5 * w * LabelWidthMult;
    }
    return EstimatedLabelHalfWidth(text, height) * LabelWidthMult;
  }

  static double ChooseLabelTangentSide(Curve curve, double lengthFromStart,
    double preferredSign, double requiredOffset, double tol)
  {
    double total = curve.GetLength();
    if (total <= tol) return preferredSign;
    double d    = Clamp(lengthFromStart, 0.0, total);
    double posS = Math.Max(0.0, total - d);
    double negS = Math.Max(0.0, d);
    double prefS = preferredSign >= 0.0 ? posS : negS;
    if (prefS + tol >= requiredOffset) return preferredSign;
    double othS = preferredSign >= 0.0 ? negS : posS;
    if (othS + tol >= requiredOffset) return -preferredSign;
    return preferredSign;
  }

  static (Plane plane, bool sideFlipped, BoundingBox bbox) ComputeLabelLayout(
    RhinoDoc doc, Curve curve, double lengthFromStart,
    Vector3d direction, Vector3d tangent, double notchOffset,
    IReadOnlyList<Curve>? notchGeom, string labelText, double labelSize,
    double labelOffset, double labelOffsetY, string curveSide)
  {
    double tol = doc.ModelAbsoluteTolerance;
    if (labelSize <= tol)
      return (Plane.Unset, false, BoundingBox.Empty);

    var (curvePoint, _) = PointAtCurveLength(curve, lengthFromStart);
    var anchor = curvePoint + direction * Math.Max(0.0, notchOffset) * 0.5;
    if (Math.Abs(labelOffsetY) > tol)
      anchor = anchor + direction * labelOffsetY;

    var plane = BuildReadableTextPlane(doc, anchor, tangent, direction, curvePoint);
    if (!plane.IsValid)
      return (Plane.Unset, false, BoundingBox.Empty);

    double labelHW  = MeasuredLabelHalfWidth(doc, labelText, labelSize, plane);
    var effectiveBbox = new BoundingBox(
      new Point3d(-labelHW, -0.5 * labelSize, 0.0),
      new Point3d( labelHW,  0.5 * labelSize, 0.0));

    if (notchGeom == null)
      return (plane, false, effectiveBbox);

    var (notchMin, notchMax) = GeometryXRangeInPlane(notchGeom, plane);
    double notchCenter  = 0.5 * (notchMin + notchMax);
    double labelCenter  = GeometryXInPlane(plane, plane.Origin);
    double requestedGap = labelOffset;
    double notchHW      = Math.Max(0.0, 0.5 * (notchMax - notchMin));

    double preferredCurveSign = curveSide == "Right" ? 1.0 : -1.0;
    double requiredOffset = notchHW + Math.Max(0.0, requestedGap) + 2.0 * labelHW;
    double sideSignCurve  = ChooseLabelTangentSide(curve, lengthFromStart, preferredCurveSign, requiredOffset, tol);
    bool sideFlipped      = sideSignCurve != preferredCurveSign;

    double tanPlaneDot    = Vector3d.Multiply(plane.XAxis, tangent);
    double curveToPlane   = tanPlaneDot < 0.0 ? -1.0 : 1.0;
    double sideSignPlane  = sideSignCurve * curveToPlane;

    double clearance = sideSignPlane * (labelCenter - notchCenter) - (notchHW + labelHW);
    if (clearance >= requestedGap - tol)
      return (plane, sideFlipped, effectiveBbox);

    double delta = sideSignPlane * (requestedGap - clearance);
    if (Math.Abs(delta) <= tol)
      return (plane, sideFlipped, effectiveBbox);

    var shifted = new Plane(plane);
    shifted.Origin = shifted.Origin + shifted.XAxis * delta;
    return (shifted, sideFlipped, effectiveBbox);
  }

  // ── Add notch to doc ──────────────────────────────────────────────────────

  static (Guid notch, Guid? label) AddNotch(RhinoDoc doc,
    Curve curve, double lengthFromStart,
    double notchLength, double notchOffset, string side, int groupIndex,
    string notchType, double notchWidth,
    bool notchEnabled, bool labelEnabled, string labelText, double labelSize,
    string notchLayer, string labelLayer,
    double labelOffset, double labelOffsetY,
    string labelCurveSide,
    Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
    Guid sourceCurveId, int curveIndex, string placementMode)
  {
    GetCurveTangentAndDirection(curve, lengthFromStart, side, cursorPoint, kinkChoice,
      out var tangent, out var direction);
    notchType = CanonicalNotchType(notchType);

    var geom = NotchGeometry(curve, lengthFromStart, notchLength, notchOffset,
      side, notchType, notchWidth, cursorPoint, kinkChoice, logOffsetFit: true);
    if (geom == null) return (Guid.Empty, null);

    var metadataAttributes = CreateNotchAttributes(doc, curve, sourceCurveId, curveIndex,
      placementMode, lengthFromStart, notchLength, notchOffset, notchType,
      notchWidth, side, labelEnabled, labelText, labelSize, labelOffset,
      labelOffsetY, labelCurveSide, notchLayer, labelLayer, tangent);
    if (groupIndex >= 0)
      metadataAttributes.AddToGroup(groupIndex);

    Guid notchId = Guid.Empty;
    if (notchEnabled)
    {
      notchId = AddNotchComponents(doc, geom, metadataAttributes);
      if (notchId == Guid.Empty)
        return (Guid.Empty, null);
    }

    Guid? labelId = null;
    if (labelEnabled && tangent.IsValid && direction.IsValid)
    {
      string lt = (labelText ?? "").Trim();
      if (lt.Length > 0 && labelSize > doc.ModelAbsoluteTolerance)
      {
        var (labelPlane, _, _) = ComputeLabelLayout(doc, curve, lengthFromStart,
          direction, tangent, notchOffset, geom, lt, labelSize,
          labelOffset, labelOffsetY, labelCurveSide);
        if (labelPlane.IsValid)
        {
          var te = new TextEntity
          {
            Plane         = labelPlane,
            PlainText     = lt,
            TextHeight    = labelSize,
            Justification = TextJustification.MiddleCenter,
            DimensionScale= 0.9,
          };
          var la = metadataAttributes.Duplicate();
          la.ObjectId = Guid.NewGuid();
          la.LayerIndex = ResolveLayerIndex(doc, labelLayer);
          la.Name = NotchLabelObjectName;
          la.SetUserString(NotchDataPrefix + "object_role", "label");
          la.SetUserString(NotchDataPrefix + "label_id", la.ObjectId.ToString());
          la.SetUserString(NotchDataPrefix + "notch_id",
            notchId == Guid.Empty ? string.Empty : notchId.ToString());
          var lid = doc.Objects.AddText(te, la);
          if (lid != Guid.Empty) labelId = lid;
        }
      }
    }

    return (notchId, labelId);
  }

  static Guid AddNotchComponents(RhinoDoc doc, IReadOnlyList<Curve> geometry,
    ObjectAttributes baseAttributes)
  {
    if (geometry.Count == 0)
      return Guid.Empty;

    bool compound = geometry.Count > 1;
    string componentSet = compound ? Guid.NewGuid().ToString("N") : string.Empty;
    int componentGroup = compound ? doc.Groups.Add() : -1;
    var addedIds = new List<Guid>();

    for (int i = 0; i < geometry.Count; i++)
    {
      var attrs = baseAttributes.Duplicate();
      attrs.ObjectId = Guid.NewGuid();
      attrs.SetUserString(NotchDataPrefix + "notch_id", attrs.ObjectId.ToString());
      if (compound)
      {
        attrs.SetUserString(NotchComponentSetKey, componentSet);
        attrs.SetUserString(NotchDataPrefix + "component_index",
          i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        attrs.SetUserString(NotchDataPrefix + "component_count",
          geometry.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (componentGroup >= 0)
          attrs.AddToGroup(componentGroup);
      }

      Guid id = doc.Objects.Add(geometry[i], attrs);
      if (id == Guid.Empty)
      {
        foreach (var addedId in addedIds)
          doc.Objects.Delete(addedId, true);
        if (componentGroup >= 0)
          doc.Groups.Delete(componentGroup);
        return Guid.Empty;
      }
      addedIds.Add(id);
    }

    return addedIds[0];
  }

  static ObjectAttributes CreateNotchAttributes(RhinoDoc doc, Curve sourceCurve,
    Guid sourceCurveId, int curveIndex, string placementMode,
    double lengthFromStart, double notchLength, double notchOffset,
    string notchType, double notchWidth, string curveSide,
    bool labelEnabled, string labelText, double labelSize,
    double labelOffset, double labelOffsetY, string labelSide,
    string notchLayer, string labelLayer, Vector3d tangent)
  {
    var attrs = new ObjectAttributes
    {
      ObjectId = Guid.NewGuid(),
      LayerIndex = ResolveLayerIndex(doc, notchLayer),
      Name = NotchObjectName,
    };

    void Set(string key, string value) =>
      attrs.SetUserString(NotchDataPrefix + key, value ?? string.Empty);
    static string Number(double value) => value.ToString(
      "R", System.Globalization.CultureInfo.InvariantCulture);
    static string PointText(Point3d point) => string.Join(",",
      Number(point.X), Number(point.Y), Number(point.Z));
    static string VectorText(Vector3d vector) => string.Join(",",
      Number(vector.X), Number(vector.Y), Number(vector.Z));

    double sourceLength = sourceCurve.GetLength();
    double percent = string.Equals(placementMode, "percent", StringComparison.OrdinalIgnoreCase) &&
      sourceLength > RhinoMath.ZeroTolerance
        ? lengthFromStart / sourceLength
        : 0.0;
    var (curveMid, _) = PointAtCurveLength(sourceCurve, sourceLength * 0.5);

    Set("version", NotchDataVersion);
    Set("object_role", "notch");
    Set("notch_id", attrs.ObjectId.ToString());
    Set("curve_id", sourceCurveId == Guid.Empty ? string.Empty : sourceCurveId.ToString());
    Set("curve_key", sourceCurveId == Guid.Empty ? string.Empty : $"obj:{sourceCurveId}");
    Set("curve_index", curveIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Set("mode", placementMode);
    Set("length_from_start", Number(lengthFromStart));
    Set("percent", Number(percent));
    Set("notch_length", Number(notchLength));
    Set("notch_offset", Number(notchOffset));
    Set("notch_type", notchType);
    Set("notch_width", Number(notchWidth));
    Set("curve_side", curveSide);
    Set("label_enabled", labelEnabled ? "1" : "0");
    Set("label_text", labelText);
    Set("label_size", Number(labelSize));
    Set("label_offset", Number(labelOffset));
    Set("label_offset_y", Number(labelOffsetY));
    Set("label_side", labelSide);
    Set("notch_layer", notchLayer);
    Set("label_layer", labelLayer);
    Set("tangent_hint", VectorText(tangent));
    Set("curve_start", PointText(sourceCurve.PointAtStart));
    Set("curve_end", PointText(sourceCurve.PointAtEnd));
    Set("curve_mid", PointText(curveMid));
    return attrs;
  }

  static List<(Guid notch, Guid? label)> AddNotchesPerCurve(
    RhinoDoc doc, NotchSession s, int[] groupIndices,
    List<double> lengths, double notchLen, double notchOff,
    string notchTyp, double notchWid,
    bool canNotch, bool canLabel, List<string> labelValues, double labelSize,
    string notchLayer, string labelLayer,
    double labelOffset, double labelOffsetY,
    bool labelSideFlip, Point3d? cursorPoint, KinkTangentChoice referenceKinkChoice,
    bool[] curveEnabled, string placementMode)
  {
    var ids = new List<(Guid, Guid?)>();
    int referenceIndex = cursorPoint.HasValue ? s.PreviewRefCurveIndex : -1;
    KinkTangentChoice? resolvedKinkChoice = referenceKinkChoice == KinkTangentChoice.Default
      ? null
      : referenceKinkChoice;
    string firstSide = lengths.Count > 0
      ? PlacementCurveSide(s, 0, lengths[0], resolvedKinkChoice)
      : "Left";

    for (int i = 0; i < s.Curves.Count; i++)
    {
      if (curveEnabled != null && i < curveEnabled.Length && !curveEnabled[i])
      { ids.Add((Guid.Empty, null)); continue; }

      string lv = (canLabel && i < labelValues.Count) ? labelValues[i] : "";
      Point3d? curveCursor = i == referenceIndex ? cursorPoint : null;
      KinkTangentChoice? kinkChoice = referenceKinkChoice == KinkTangentChoice.Default
        ? null
        : referenceKinkChoice;
      ResolvePlacementCurve(s, i, lengths[i], kinkChoice,
        out var placementCurve, out double placementLength);
      string side = PlacementCurveSide(s, i, lengths[i], kinkChoice);
      string labelCurveSide = ResolvedLabelCurveSide(side, firstSide, i);
      if (labelSideFlip) labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";
      Guid sourceCurveId = ResolvePlacementSourceCurveId(
        doc, s, i, lengths[i], kinkChoice);
      int gi = s.GroupToggle.CurrentValue
        ? (i < groupIndices.Length ? groupIndices[i] : -1)
        : SourceCurveGroupIndex(doc, sourceCurveId);

      var (nid, lid) = AddNotch(doc, placementCurve, placementLength,
        notchLen, notchOff, side, gi,
        notchTyp, notchWid,
        canNotch, canLabel, lv, labelSize,
        notchLayer, labelLayer,
        labelOffset, labelOffsetY,
        labelCurveSide, curveCursor, kinkChoice,
        sourceCurveId, i, placementMode);

      if (nid != Guid.Empty || lid.HasValue)
      {
        GetCurveTangentAndDirection(placementCurve, placementLength, side,
          curveCursor, kinkChoice, out var resolvedTangent, out var resolvedDirection);
        vTools.Log.Write("vNotches",
          $"placed curve={i + 1} source={sourceCurveId} side={side} ref={referenceIndex + 1} " +
          $"kink={referenceKinkChoice} " +
          $"tangent=({resolvedTangent.X:0.###},{resolvedTangent.Y:0.###}) " +
          $"direction=({resolvedDirection.X:0.###},{resolvedDirection.Y:0.###})");
      }

      ids.Add((nid, lid));
    }
    return ids;
  }

  static Guid ResolvePlacementSourceCurveId(
    RhinoDoc doc, NotchSession s, int curveIndex, double lengthFromStart,
    KinkTangentChoice? kinkChoice)
  {
    if (curveIndex < 0 || curveIndex >= s.PerCurveSourceIds.Count)
      return curveIndex >= 0 && curveIndex < s.CurveIds.Count
        ? s.CurveIds[curveIndex]
        : Guid.Empty;

    var sourceIds = s.PerCurveSourceIds[curveIndex];
    if (sourceIds.Count == 0)
      return curveIndex < s.CurveIds.Count ? s.CurveIds[curveIndex] : Guid.Empty;
    int sourceIndex = ResolvePlacementSourceIndex(s, curveIndex, lengthFromStart, kinkChoice);
    return sourceIds[Math.Clamp(sourceIndex, 0, sourceIds.Count - 1)];
  }

  static int ResolvePlacementSourceIndex(
    NotchSession s, int curveIndex, double lengthFromStart,
    KinkTangentChoice? kinkChoice)
  {
    if (curveIndex < 0 || curveIndex >= s.PerCurveSegments.Count ||
        s.PerCurveSegments[curveIndex].Count == 0)
      return 0;

    var segments = s.PerCurveSegments[curveIndex];
    double tolerance = Math.Max(s.Doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    double remaining = Math.Max(0.0, lengthFromStart);
    for (int sourceIndex = 0; sourceIndex < segments.Count; sourceIndex++)
    {
      double sourceLength = segments[sourceIndex].GetLength();
      if (remaining < sourceLength - tolerance)
        return sourceIndex;
      if (Math.Abs(remaining - sourceLength) <= tolerance)
        return kinkChoice == KinkTangentChoice.After && sourceIndex + 1 < segments.Count
          ? sourceIndex + 1
          : sourceIndex;
      remaining -= sourceLength;
    }
    return segments.Count - 1;
  }

  static string PlacementCurveSide(
    NotchSession s, int curveIndex, double lengthFromStart,
    KinkTangentChoice? kinkChoice)
  {
    if (curveIndex >= 0 && curveIndex < s.PerCurveSourceIds.Count &&
        s.PerCurveSourceIds[curveIndex].Count > 0)
    {
      int sourceIndex = ResolvePlacementSourceIndex(s, curveIndex, lengthFromStart, kinkChoice);
      Guid sourceId = s.PerCurveSourceIds[curveIndex][Math.Clamp(
        sourceIndex, 0, s.PerCurveSourceIds[curveIndex].Count - 1)];
      if (s.CurveSideBySource.TryGetValue(sourceId, out bool sourceSide))
        return sourceSide ? "Left" : "Right";
    }
    return curveIndex >= 0 && curveIndex < s.CurveSides.Length && s.CurveSides[curveIndex]
      ? "Left"
      : "Right";
  }

  static int SourceCurveGroupIndex(RhinoDoc doc, Guid sourceCurveId)
  {
    var groups = doc.Objects.FindId(sourceCurveId)?.Attributes.GetGroupList();
    return groups != null && groups.Length > 0 ? groups[0] : -1;
  }

  // ── Rebuild curve notches (after side/reverse change) ─────────────────────

  static void RebuildCurveNotches(RhinoDoc doc, NotchSession s, int curveIndex)
  {
    if (curveIndex < 0 || curveIndex >= s.Curves.Count) return;

    // Delete existing notch + label IDs for this curve
    while (s.NotchIdsByCurve[curveIndex].Count > 0)
    {
      var id = s.NotchIdsByCurve[curveIndex][^1];
      s.NotchIdsByCurve[curveIndex].RemoveAt(s.NotchIdsByCurve[curveIndex].Count - 1);
      if (id != Guid.Empty) DeleteNotchObjects(doc, id);
    }
    while (s.LabelIdsByCurve[curveIndex].Count > 0)
    {
      var id = s.LabelIdsByCurve[curveIndex][^1];
      s.LabelIdsByCurve[curveIndex].RemoveAt(s.LabelIdsByCurve[curveIndex].Count - 1);
      if (id.HasValue && id.Value != Guid.Empty) doc.Objects.Delete(id.Value, true);
    }

    // if (!s.CurveEnabled[curveIndex])
    // {
    //   s.NotchIdsByCurve[curveIndex].AddRange(Enumerable.Repeat(Guid.Empty, s.NotchRecords.Count));
    //   s.LabelIdsByCurve[curveIndex].AddRange(Enumerable.Repeat<Guid?>(null, s.NotchRecords.Count));
    //   // Rebuild placement IDs
    //   RebuildPlacementIds(s);
    //   return;
    // }

    var newIds      = new List<Guid>();
    var newLabelIds = new List<Guid?>();
    foreach (var rec in s.NotchRecords)
    {
      bool recordHadCurveEnabled =
      rec.CurveEnabled == null ||
      rec.CurveEnabled.Count == 0 ||
      (curveIndex < rec.CurveEnabled.Count && rec.CurveEnabled[curveIndex]);

      if (!recordHadCurveEnabled)
      {
        newIds.Add(Guid.Empty);
        newLabelIds.Add(null);
        continue;
      }
      double d = LengthFromRecord(s, rec, curveIndex);
      bool lbl = rec.LabelEnabled;
      string lv = (rec.LabelValues != null && curveIndex < rec.LabelValues.Count)
        ? rec.LabelValues[curveIndex] : "";
      KinkTangentChoice? kinkChoice = rec.KinkChoice == KinkTangentChoice.Default
        ? null
        : rec.KinkChoice;
      ResolvePlacementCurve(s, curveIndex, d, kinkChoice,
        out var placementCurve, out double placementLength);
      string side = PlacementCurveSide(s, curveIndex, d, kinkChoice);
      double firstLength = LengthFromRecord(s, rec, 0);
      string firstSide = PlacementCurveSide(s, 0, firstLength, kinkChoice);
      string labelCurveSide = ResolvedLabelCurveSide(side, firstSide, curveIndex);
      if (s.LabelSideFlip) labelCurveSide = labelCurveSide == "Left" ? "Right" : "Left";
      Guid sourceCurveId = ResolvePlacementSourceCurveId(
        doc, s, curveIndex, d, kinkChoice);
      int groupIdx = rec.GroupEnabled
        ? s.SessionGroupIndices[curveIndex < s.SessionGroupIndices.Length ? curveIndex : 0]
        : SourceCurveGroupIndex(doc, sourceCurveId);

      var (nid, lid) = AddNotch(doc, placementCurve, placementLength,
        rec.NotchLength, rec.NotchOffset, side, groupIdx,
        rec.NotchType, rec.NotchWidth,
        rec.NotchEnabled, lbl, lv, rec.LabelSize,
        EffectiveLayerName(doc, rec.NotchLayer, rec.NotchLayer),
        EffectiveLayerName(doc, rec.LabelLayer, rec.NotchLayer),
        rec.LabelOffset, rec.LabelOffsetY, labelCurveSide, null,
        kinkChoice,
        sourceCurveId,
        curveIndex, rec.Mode);
      newIds.Add(nid);
      newLabelIds.Add(lid);
    }
    s.NotchIdsByCurve[curveIndex].AddRange(newIds);
    s.LabelIdsByCurve[curveIndex].AddRange(newLabelIds);
    RebuildPlacementIds(s);
    doc.Views.Redraw();
  }

  static void RebuildPlacementIds(NotchSession s)
  {
    // Rebuild PlacementIds by transposing NotchIdsByCurve per record index
    s.PlacementIds.Clear();
    s.PlacementLabelIds.Clear();
    for (int r = 0; r < s.NotchRecords.Count; r++)
    {
      var ids   = new List<Guid>();
      var lids  = new List<Guid?>();
      for (int c = 0; c < s.Curves.Count; c++)
      {
        ids.Add(r < s.NotchIdsByCurve[c].Count ? s.NotchIdsByCurve[c][r] : Guid.Empty);
        lids.Add(r < s.LabelIdsByCurve[c].Count ? s.LabelIdsByCurve[c][r] : null);
      }
      s.PlacementIds.Add(ids);
      s.PlacementLabelIds.Add(lids);
    }
  }

  // ── Side / reverse ────────────────────────────────────────────────────────

  static void ToggleCurveSide(RhinoDoc doc, NotchSession s, int idx, Guid sourceId)
  {
    if (idx < 0 || idx >= s.CurveSides.Length) return;
    if (sourceId == Guid.Empty && idx < s.PerCurveSourceIds.Count &&
        s.PerCurveSourceIds[idx].Count > 0)
      sourceId = s.PerCurveSourceIds[idx][0];
    if (sourceId == Guid.Empty) return;
    s.RedoBatches.Clear();
    bool oldSide = s.CurveSideBySource.GetValueOrDefault(sourceId, s.CurveSides[idx]);
    s.CurveSideBySource[sourceId] = !oldSide;
    UpdateLogicalCurveSide(s, idx);
    RebuildCurveNotches(doc, s, idx);
    SelectBothCurves(doc, s);
    s.Panel?.UpdateUndoEnabled();
  }

  static void ReverseSourceCurve(RhinoDoc doc, NotchSession s, int idx, Guid sourceId)
  {
    if (idx < 0 || idx >= s.Curves.Count ||
        idx >= s.PerCurveSourceIds.Count || idx >= s.PerCurveSegments.Count)
      return;
    int sourceIndex = s.PerCurveSourceIds[idx].IndexOf(sourceId);
    if (sourceIndex < 0 || sourceIndex >= s.PerCurveSegments[idx].Count)
      return;
    s.RedoBatches.Clear();
    double prefix = s.PerCurveSegments[idx]
      .Take(sourceIndex)
      .Sum(segment => segment.GetLength());
    double sourceLength = s.PerCurveSegments[idx][sourceIndex].GetLength();
    double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
    foreach (var rec in s.NotchRecords)
    {
      if (rec.LengthsFromStart == null || idx >= rec.LengthsFromStart.Count) continue;
      double old = rec.LengthsFromStart[idx];
      if (old < prefix - tolerance || old > prefix + sourceLength + tolerance)
        continue;
      double local = Clamp(old - prefix, 0.0, sourceLength);
      rec.LengthsFromStart[idx] = prefix + sourceLength - local;
    }
    s.PerCurveSegments[idx][sourceIndex].Reverse();
    s.CurveReversedBySource[sourceId] =
      !s.CurveReversedBySource.GetValueOrDefault(sourceId);
    bool oldSide = s.CurveSideBySource.GetValueOrDefault(sourceId, s.CurveSides[idx]);
    s.CurveSideBySource[sourceId] = !oldSide;
    s.Curves[idx].Dispose();
    s.Curves[idx] = BuildLayoutCurve(doc, s.PerCurveSegments[idx], out bool continuous);
    s.CurveIsContinuous[idx] = continuous;
    UpdateLogicalCurveSide(s, idx);
    RebuildCurveNotches(doc, s, idx);
    SelectBothCurves(doc, s);
    s.Panel?.UpdateUndoEnabled();
  }

  static void UpdateLogicalCurveSide(NotchSession s, int curveIndex)
  {
    if (curveIndex < 0 || curveIndex >= s.CurveSides.Length ||
        curveIndex >= s.PerCurveSourceIds.Count ||
        s.PerCurveSourceIds[curveIndex].Count == 0)
      return;
    Guid firstSourceId = s.PerCurveSourceIds[curveIndex][0];
    s.CurveSides[curveIndex] = s.CurveSideBySource.GetValueOrDefault(
      firstSourceId, s.CurveSides[curveIndex]);
  }

  static void SelectBothCurves(RhinoDoc doc, NotchSession s)
  {
    doc.Objects.UnselectAll();
    var toSelect = s.PerCurveSourceIds.Count > 0
      ? s.PerCurveSourceIds.SelectMany(list => list)
      : (IEnumerable<Guid>)s.CurveIds;
    foreach (var id in toSelect)
      doc.Objects.FindId(id)?.Select(true);
    doc.Views.Redraw();
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  static int ClosestCurveIndex(NotchSession s, Point3d point)
  {
    ClosestCurveHit(s, point, out int idx, out _, out _);
    return idx;
  }

  static void ClosestCurveHit(NotchSession s, Point3d point,
    out int closestIdx, out Curve? closestCurve, out double closestLength)
  {
    closestIdx   = 0;
    closestCurve = s.Curves.Count > 0 ? s.Curves[0] : null;
    closestLength = 0.0;
    double closestDist = double.MaxValue;
    for (int i = 0; i < s.Curves.Count; i++)
    {
      double accumulatedLength = 0.0;
      var segments = i < s.PerCurveSegments.Count && s.PerCurveSegments[i].Count > 0
        ? s.PerCurveSegments[i]
        : [s.Curves[i]];
      foreach (var segment in segments)
      {
        if (segment.ClosestPoint(point, out double t))
        {
          double dist = segment.PointAt(t).DistanceTo(point);
          if (dist < closestDist)
          {
            closestDist = dist;
            closestIdx = i;
            closestCurve = s.Curves[i];
            closestLength = accumulatedLength + LengthFromStart(segment, t);
          }
        }
        accumulatedLength += segment.GetLength();
      }
    }
  }

  static Point3d ClosestPointOnPlacementCurve(
    NotchSession s, int curveIndex, Point3d point)
  {
    Point3d closest = Point3d.Unset;
    double closestDistance = double.MaxValue;
    if (curveIndex < 0 || curveIndex >= s.PerCurveSegments.Count)
      return closest;
    foreach (var segment in s.PerCurveSegments[curveIndex])
    {
      if (!segment.ClosestPoint(point, out double parameter))
        continue;
      Point3d candidate = segment.PointAt(parameter);
      double distance = candidate.DistanceTo(point);
      if (distance < closestDistance)
      {
        closest = candidate;
        closestDistance = distance;
      }
    }
    return closest;
  }

  static double LengthFromRecord(NotchSession s, NotchRecord rec, int curveIndex)
  {
    if (rec.LengthsFromStart != null && curveIndex < rec.LengthsFromStart.Count)
      return rec.LengthsFromStart[curveIndex];
    if (rec.Mode == "percent" && rec.Percent.HasValue)
      return PlacementCurveLength(s, curveIndex) * rec.Percent.Value;
    return rec.LengthsFromStart?.Count > 0 ? rec.LengthsFromStart[0] : 0.0;
  }

  static int ResolveLayerIndex(RhinoDoc doc, string layerName)
  {
    if (string.IsNullOrWhiteSpace(layerName) ||
        LayerSelector.IsCurrentLayerValue(layerName, SpecialLayerCurrent))
      return doc.Layers.CurrentLayerIndex;
    int idx = doc.Layers.FindByFullPath(layerName, RhinoMath.UnsetIntIndex);
    if (idx >= 0) return idx;
    var layer = new Layer { Name = layerName };
    idx = doc.Layers.Add(layer);
    return idx >= 0 ? idx : doc.Layers.CurrentLayerIndex;
  }

  static string EffectiveLayerName(RhinoDoc doc, string layerChoice, string notchLayerChoice)
  {
    if (string.IsNullOrWhiteSpace(layerChoice) ||
        LayerSelector.IsCurrentLayerValue(layerChoice, SpecialLayerCurrent))
    {
      int ci = doc.Layers.CurrentLayerIndex;
      return ci >= 0 && ci < doc.Layers.Count ? doc.Layers[ci].FullPath : "";
    }
    return layerChoice;
  }

  static string ResolvedLabelCurveSide(string side, string firstSide, int index)
  {
    string cur   = side      == "Right" ? "Right" : "Left";
    string first = firstSide == "Right" ? "Right" : "Left";
    if (index > 0 && cur != first) return cur == "Right" ? "Left" : "Right";
    return cur;
  }

  static Curve OrientCurveToPickPoint(Curve curve, Point3d pick)
  {
    return OrientCurveToPickPoint(curve, pick, out _);
  }

  static Curve OrientCurveToPickPoint(Curve curve, Point3d pick, out bool reversed)
  {
    reversed = false;
    if (pick == Point3d.Unset) return curve;
    if (PickTargetsCurveEnd(curve, pick))
    {
      curve = curve.DuplicateCurve();
      curve.Reverse();
      reversed = true;
    }
    return curve;
  }

  static bool PickTargetsCurveEnd(Curve curve, Point3d pick) =>
    pick != Point3d.Unset &&
    pick.DistanceTo(curve.PointAtEnd) < pick.DistanceTo(curve.PointAtStart);

  static double Clamp(double v, double min, double max) =>
    v < min ? min : (v > max ? max : v);

  static double ModelUnitsFromInches(RhinoDoc doc, double inches) =>
    inches * RhinoMath.UnitScale(UnitSystem.Inches, doc.ModelUnitSystem);

  static double EffectiveLabelSize(NotchSession s)
  {
    if (s.LabelSizeAutoToggle.CurrentValue)
    {
      double pct = s.LabelSizePctValues[s.LabelSizePctIndex] * 0.01;
      return Math.Max(0.0, s.NotchOffsetOpt.CurrentValue * pct);
    }
    return Math.Max(0.0, s.ManualLabelSize);
  }

  static string FormatFractionalInches(RhinoDoc doc, double value)
  {
    double toIn = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Inches);
    double absin = Math.Abs(value * toIn);
    int whole    = (int)absin;
    double frac  = absin - whole;
    int num      = (int)Math.Round(frac * 64.0);
    if (num == 64) { whole++; num = 0; }
    string fracPart = num == 0 ? "" : $"{num}\u204464";
    string result   = (whole == 0 && fracPart.Length > 0)
      ? $"{fracPart}\u2033"
      : fracPart.Length > 0 ? $"{whole} {fracPart}\u2033" : $"{whole}\u2033";
    return (value < 0 ? "-" : "") + result;
  }

  static string FormatPanelNumber(double value) =>
    value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

  static string IncrementLabelValue(string text)
  {
    if (string.IsNullOrWhiteSpace(text)) return "A";
    var m = Regex.Match(text, @"([A-Za-z]+|\d+)$");
    if (!m.Success) return text + "1";
    string prefix = text[..m.Index];
    string suffix = m.Value;
    if (suffix.All(char.IsDigit))
    {
      int n = int.Parse(suffix) + 1;
      return prefix + n.ToString().PadLeft(suffix.Length, '0');
    }
    return prefix + IncrementAlpha(suffix.ToUpper());
  }

  static string IncrementAlpha(string s)
  {
    var chars = s.ToCharArray();
    for (int i = chars.Length - 1; i >= 0; i--)
    {
      if (chars[i] == 'Z') { chars[i] = 'A'; continue; }
      chars[i]++;
      return new string(chars);
    }
    return "A" + new string(chars);
  }

  // ── Panel sync ────────────────────────────────────────────────────────────

  static void SyncPanelFromOptions(NotchSession s)
  {
    s.Panel?.SyncFromSession();
  }

  static void UpdateDistanceLabels(
    NotchSession s,
    double? current, double? prevDelta, double? otherEnd,
    double? segmentCurrent, double? segmentPrevDelta, double? segmentOtherEnd)
  {
    s.Panel?.UpdateDistanceLabels(
      current, prevDelta, otherEnd,
      segmentCurrent, segmentPrevDelta, segmentOtherEnd);
  }

  // ── Session state ─────────────────────────────────────────────────────────

  sealed class NotchSession
  {
    public readonly RhinoDoc Doc;
    public readonly List<Curve> Curves;
    public readonly List<Guid>  CurveIds;
    public bool[]   CurveSides;  // true = Left
    public bool[]   CurveEnabled;

    public OptionDouble NotchLengthOpt;
    public OptionDouble NotchOffsetOpt;
    public OptionDouble NotchWidthOpt;
    public OptionDouble LabelSizeOpt;
    public OptionDouble LabelOffsetOpt;
    public OptionDouble LabelOffsetYOpt;
    public OptionToggle PercentToggle;
    public OptionToggle GroupToggle;
    public OptionToggle NotchToggle;
    public OptionToggle LabelToggle;
    public OptionToggle LabelSizeAutoToggle;

    public string LabelValueText;
    public double ManualLabelSize;
    public string NotchLayerName;
    public string LabelLayerName;
    public bool   LabelAutoAdv;
    public bool   LabelSideFlip;
    public double MultipleStartOffset;
    public double MultipleEndOffset;
    public bool   MultipleStartOffsetEnabled;
    public bool   MultipleEndOffsetEnabled;
    public int    MultipleNumber;
    public double MultipleDistance;
    public bool   MultipleUseDistance;
    public bool   MultipleAuto;
    public int MultipleCurvatureSensitivity;
    public bool   MultipleSeparate;
    public readonly string[] NotchTypeValues = ["I", "V", OpenVNotchType, "U", "T"];
    public readonly string[] NotchTypeOptionValues = ["I", "V", "OpenV", "U", "T"];
    public readonly string[] NotchTypeToolTips = ["Slit", "Vee", "Open Vee", "Castle", "Tee"];
    public int NotchTypeIndex;

    public readonly int[] LabelSizePctValues;
    public readonly string[] LabelSizePctTexts;
    public int LabelSizePctIndex;

    // Per-curve tracking
    public readonly List<List<Guid>>   NotchIdsByCurve;
    public readonly List<List<Guid?>>  LabelIdsByCurve;
    public readonly List<List<Guid>>   PlacementIds    = [];
    public readonly List<List<Guid?>>  PlacementLabelIds = [];
    public readonly List<NotchRecord>  NotchRecords    = [];

    // Group indices per curve for session grouping
    public int[] SessionGroupIndices;
    // Context group indices from source curves
    public int[] CurveContextGroupIndices;

    // Per-curve source IDs — one inner list per curve slot; multiple IDs for joined chains.
    public readonly List<List<Guid>> PerCurveSourceIds;
    // Oriented physical source geometry in the same order as PerCurveSourceIds.
    public readonly List<List<Curve>> PerCurveSegments;
    public readonly List<bool> CurveIsContinuous;
    public readonly Dictionary<Guid, int> CurveDisplayNumbers = [];
    public readonly Dictionary<Guid, bool> CurveSideBySource = [];
    public readonly Dictionary<Guid, bool> CurveReversedBySource = [];

    // Loop control
    public bool PanelClosedExit;
    public bool RefreshCommandLine;
    public bool PanelNumericPending;
    public bool IgnoreNextNothing;
    public bool CurveSelectionRequested;
    public bool KeepCurveSelection;
    public bool TransparentRedoRequested;
    public bool SuppressPanelCloseExit;
    public bool Finalized;
    public bool NotchCollapsed;
    public bool LabelCollapsed;
    public bool MultipleCollapsed;

    // Command option indices (set each iteration)
    public int SideOptionIndex, ReverseOptionIndex, UndoOptionIndex, RedoOptionIndex;
    public int TypeOptionIndex, NotchLayerOptionIndex, NotchEnabledIndex, LabelEnabledIndex;
    public int LabelValueOptionIndex, LabelLayerOptionIndex;
    public int LabelSizeAutoIndex, LabelSizePctIndex2;

    public Point3d? LastPreviewPoint;
    public Point3d? LastCursorPoint;
    public NotchPanel? Panel;

    public bool PreviewValid;
    public bool KinkCenterSnapActive;
    public Point3d PreviewSnapPoint;
    public Point3d PreviewCursorPoint;
    public int PreviewRefCurveIndex;
    public List<double> PreviewLengthsFromStart = [];
    public bool MultipleHoverPreviewActive;
    public List<MultiplePlacementPlan>? MultipleHoverPlans;
    public readonly Stack<NotchUndoBatch> RedoBatches = [];
    public NotchSession(RhinoDoc doc, List<Curve> curves, List<Guid> curveIds, bool[] sides,
      double notchLength, double notchOffset, double notchWidth, string notchType, bool notch,
      bool percent, bool group, bool label, string labelValue,
      double labelSize, bool labelSizeAuto, int labelSizePct,
      string notchLayer, string labelLayer, double labelOffset, double labelOffsetY,
      bool labelAutoAdv, bool labelSideFlip, bool keepSelection,
      double multipleStartOffset, double multipleEndOffset,
      bool multipleStartOffsetEnabled, bool multipleEndOffsetEnabled, int multipleNumber,
      double multipleDistance, bool multipleUseDistance,
      bool multipleAuto, int multipleCurvatureSensitivity,
      bool multipleSeparate)
    {
      Doc      = doc;
      Curves   = curves;
      CurveIds = curveIds;

      CurveSides   = sides;
      CurveEnabled = Enumerable.Repeat(true, curves.Count).ToArray();

      double tol = doc.ModelAbsoluteTolerance;
      NotchLengthOpt    = new OptionDouble(notchLength, tol, 1e9);
      NotchOffsetOpt    = new OptionDouble(notchOffset, 0.0, 1e9);
      NotchWidthOpt     = new OptionDouble(notchWidth,  tol, 1e9);
      LabelSizeOpt      = new OptionDouble(Math.Max(0.0, labelSize), 0.0, 1e9);
      LabelOffsetOpt    = new OptionDouble(labelOffset,  -1e9, 1e9);
      LabelOffsetYOpt   = new OptionDouble(labelOffsetY, -1e9, 1e9);
      PercentToggle     = new OptionToggle(percent,       "Off", "On");
      GroupToggle       = new OptionToggle(group,         "Off", "On");
      NotchToggle       = new OptionToggle(notch,         "Off", "On");
      LabelToggle       = new OptionToggle(label,         "Off", "On");
      LabelSizeAutoToggle = new OptionToggle(labelSizeAuto, "Manual", "Auto");

      LabelValueText = labelValue ?? "A";
      ManualLabelSize = Math.Max(0.0, labelSize);
      NotchLayerName  = notchLayer ?? SpecialLayerCurrent;
      LabelLayerName  = labelLayer ?? "PLOT";
      LabelAutoAdv    = labelAutoAdv;
      LabelSideFlip   = labelSideFlip;
      KeepCurveSelection = keepSelection;
      MultipleStartOffset = Math.Max(0.0, multipleStartOffset);
      MultipleEndOffset   = Math.Max(0.0, multipleEndOffset);
      MultipleStartOffsetEnabled = multipleStartOffsetEnabled;
      MultipleEndOffsetEnabled   = multipleEndOffsetEnabled;
      MultipleNumber      = Math.Clamp(multipleNumber, 1, 10000);
      MultipleDistance    = Math.Max(0.0, multipleDistance);
      MultipleUseDistance = multipleAuto || multipleUseDistance;
      MultipleAuto = multipleAuto;
      MultipleCurvatureSensitivity = Math.Clamp(multipleCurvatureSensitivity, 0, 1000);
      MultipleSeparate = multipleSeparate;

      NotchTypeIndex  = Array.IndexOf(NotchTypeValues, CanonicalNotchType(notchType));
      if (NotchTypeIndex < 0) NotchTypeIndex = 0;

      LabelSizePctValues = Enumerable.Range(4, 17).Select(i => i * 5).ToArray(); // 20..100 step 5
      LabelSizePctTexts  = LabelSizePctValues.Select(v => $"{v}%").ToArray();
      LabelSizePctIndex  = Array.FindIndex(LabelSizePctValues, v => v == labelSizePct);
      if (LabelSizePctIndex < 0)
        LabelSizePctIndex = Array.FindIndex(LabelSizePctValues,
          v => v == LabelSizePctValues.OrderBy(x => Math.Abs(x - labelSizePct)).First());

      // Group indices for session â€” only when group=On
      SessionGroupIndices     = Enumerable.Repeat(-1, curves.Count).ToArray();
      CurveContextGroupIndices= new int[curves.Count];
      for (int i = 0; i < curves.Count; i++)
      {
        var rh = doc.Objects.FindId(curveIds[i]);
        var grps = rh?.Attributes.GetGroupList();
        CurveContextGroupIndices[i] = (grps != null && grps.Length > 0) ? grps[0] : -1;
      }

      NotchIdsByCurve = curves.Select(_ => new List<Guid>()).ToList();
      LabelIdsByCurve = curves.Select(_ => new List<Guid?>()).ToList();
      PerCurveSourceIds = curveIds.Select(id => new List<Guid> { id }).ToList();
      PerCurveSegments = curves
        .Select(curve => new List<Curve> { curve.DuplicateCurve() })
        .ToList();
      CurveIsContinuous = Enumerable.Repeat(true, curves.Count).ToList();
      for (int curveIndex = 0; curveIndex < PerCurveSourceIds.Count; curveIndex++)
        foreach (var sourceId in PerCurveSourceIds[curveIndex])
          CurveSideBySource[sourceId] =
            curveIndex < CurveSides.Length && CurveSides[curveIndex];
      EnsureCurveDisplayNumbers();
    }

    public void EnsureCurveDisplayNumbers()
    {
      foreach (var sourceId in PerCurveSourceIds.SelectMany(ids => ids))
        if (!CurveDisplayNumbers.ContainsKey(sourceId))
          CurveDisplayNumbers[sourceId] = CurveDisplayNumbers.Count + 1;
    }

    public void ResetCurveDisplayNumbers()
    {
      CurveDisplayNumbers.Clear();
      EnsureCurveDisplayNumbers();
    }

    public int CurveDisplayNumber(Guid sourceId)
    {
      if (!CurveDisplayNumbers.TryGetValue(sourceId, out int number))
      {
        number = CurveDisplayNumbers.Count + 1;
        CurveDisplayNumbers[sourceId] = number;
      }
      return number;
    }

  }

  // ── Notch record ──────────────────────────────────────────────────────────

  sealed class NotchRecord
  {
    public Guid           BatchId;
    public string         Mode           = "distance";
    public double         NotchLength;
    public double         NotchOffset;
    public string         NotchType      = "I";
    public double         NotchWidth;
    public bool           NotchEnabled   = true;
    public bool           GroupEnabled;
    public bool           LabelEnabled;
    public List<string>   LabelValues    = [];
    public double         LabelSize;
    public string         NotchLayer     = SpecialLayerCurrent;
    public string         LabelLayer     = "PLOT";
    public double         LabelOffset;
    public double         LabelOffsetY;
    public List<double>   LengthsFromStart = [];
    public List<bool>     CurveEnabled = [];
    public List<Guid>     DetachedNotchIds = [];
    public List<Guid>     DetachedLabelIds = [];
    public double?        Percent;
    public KinkTangentChoice KinkChoice;
  }

  sealed record MultiplePlacementPlan(
    List<double> LengthsFromStart,
    bool[] CurveEnabled);

  sealed class NotchUndoBatch
  {
    public NotchUndoBatch(string labelValueAfterRedo)
    {
      LabelValueAfterRedo = labelValueAfterRedo;
    }

    public string LabelValueAfterRedo { get; }
    public List<NotchPlacementSnapshot> Placements { get; } = [];
  }

  sealed class NotchPlacementSnapshot
  {
    public NotchPlacementSnapshot(NotchRecord record)
    {
      Record = record;
    }

    public NotchRecord Record { get; }
    public List<DocObjectSnapshot?> Notches { get; } = [];
    public List<DocObjectSnapshot?> Labels { get; } = [];
    public List<DocObjectSnapshot> DetachedNotches { get; } = [];
    public List<DocObjectSnapshot> DetachedLabels { get; } = [];
  }

  sealed class DocObjectSnapshot
  {
    public DocObjectSnapshot(GeometryBase geometry, ObjectAttributes attributes)
    {
      Geometry = geometry;
      Attributes = attributes;
    }

    public GeometryBase Geometry { get; }
    public ObjectAttributes Attributes { get; }
    public List<DocObjectSnapshot> Components { get; } = [];
  }

  sealed class NotchHistoryRequest
  {
    public NotchHistoryRequest(bool redo, string source)
    {
      Redo = redo;
      Source = source;
    }

    public bool Redo { get; }
    public string Source { get; }
  }

  // ── Eto panel ─────────────────────────────────────────────────────────────

  sealed class NotchPanel : Eto.Forms.Form
  {
    const int CurveRowHeight = 28; // Curve-row and drag-handle height in device-independent pixels.
    const int CurveRowSpacing = 0; // Vertical space between adjacent curve rows in device-independent pixels.
    const int CurveRowControlSpacing = 3; // Horizontal space between compact curve-row controls in device-independent pixels.
    const int CurveDragHandleWidth = 16; // Width of the only cursor and drag-sensitive handle area; matches link buttons.
    const float CurveDragDotDiameter = 2.0f; // Diameter of each drawn handle dot in device-independent pixels.
    const float CurveDragDotGap = 3.0f; // Equal edge-to-edge spacing between handle dots.
    const int CurveIdentityMinimumWidth = 10; // Minimum width of the centered source-curve number in device-independent pixels.
    const double CurveIdentityVerticalOffset = 1.0; // Downward optical adjustment for source-ID glyphs in device-independent pixels.
    const int CurveSideButtonWidth = 22; // Width of the borderless up/down side control in device-independent pixels.
    const int CurveReverseButtonWidth = 22; // Width of the borderless single-arrow reverse control in device-independent pixels.
    const int CurveDirectionButtonHeight = 26; // Height of both native direction buttons in device-independent pixels.
    const double CurveDirectionButtonHorizontalPadding = 0.0; // Horizontal padding around Side and Reverse arrow glyphs in device-independent pixels.
    const double CurveDirectionButtonVerticalPadding = 1.0; // Vertical padding around Side and Reverse arrow glyphs in device-independent pixels.
    const double CurveDirectionButtonFontSize = 18.0; // Font size of Side and Reverse arrow glyphs in device-independent pixels.
    const int CurveLengthBadgeHorizontalPadding = 2; // Equal left/right padding inside individual and cumulative colored length badges.
    const int CurveLengthBadgeWidthAllowance = 2; // Extra cumulative-badge width protecting the final digit from layout rounding.
    const string CurveSideCheckedGlyph = "🠝"; // Glyph shown when the curve's Side state is enabled.
    const string CurveSideUncheckedGlyph = "🠟"; // Glyph shown when the curve's Side state is disabled.
    const string CurveReverseForwardGlyph = "🠞"; // Glyph shown before the source curve has been reversed.
    const string CurveReverseBackwardGlyph = "🠜"; // Glyph shown after the source curve has been reversed.
    const double CurveLengthDifferenceToleranceInches = 1.0 / 16.0; // Smallest longest-to-shortest span considered significant, converted to model units.
    const string CurveLengthWidthSample = "999.999"; // Minimum-width sizing sample; longer displayed curve lengths remain unrestricted.
    const string CurveLengthDifferenceWidthSample = "(+999.999)"; // Minimum-width sizing sample for unrestricted signed superscript differences.
    const float CurveLengthDifferenceFontSize = 8.0f; // Font size of the signed longest/shortest superscript delta in device-independent pixels.
    const double CurveLengthDifferenceRaise = 3.0; // Upward superscript shift applied to signed length deltas in device-independent pixels.
    const double CurveDragRowOutlineWidth = 1.0; // Outline thickness around the row currently being dragged.
    static readonly Eto.Drawing.Color CurveLengthLongerColor =
      new(0.05f, 0.48f, 0.18f); // Text color for a longest-curve positive length delta.
    static readonly Eto.Drawing.Color CurveLengthShorterColor =
      new(0.78f, 0.08f, 0.10f); // Text color for a shortest-curve negative length delta.
    static readonly Eto.Drawing.Color PercentLengthWarningBackground =
      new(1.0f, 0.84f, 0.22f); // Percent checkbox background when absolute placement spans significantly different lengths.
    static readonly Eto.Drawing.Color PercentLengthWarningForeground =
      new(0.0f, 0.0f, 0.0f); // Percent checkbox text color while its length-difference warning is active.
    static readonly Eto.Drawing.Color[] CurveLengthGroupBackgrounds =
    [
      new(0.68f, 0.08f, 0.12f),
      new(0.02f, 0.31f, 0.66f),
      new(0.05f, 0.45f, 0.20f),
      new(0.48f, 0.16f, 0.62f),
      new(0.72f, 0.32f, 0.02f),
      new(0.00f, 0.43f, 0.45f),
    ]; // High-contrast badge colors assigned to significantly different curve-length groups.
    static readonly Eto.Drawing.Color CurveLengthGroupForeground =
      new(1.0f, 1.0f, 1.0f); // Text color shown over curve-length group badges.
    static readonly Eto.Drawing.Color CurveIdentityHoverBackground =
      SystemColors.Highlight; // Background applied to source IDs while their curve or row is hovered.
    static readonly Eto.Drawing.Color CurveIdentityHoverForeground =
      SystemColors.HighlightText; // Source-ID text color while its curve or row is hovered.
    static readonly System.Windows.Media.Brush CurveDragRowHighlightBrush =
      new System.Windows.Media.SolidColorBrush(System.Windows.SystemColors.HighlightColor)
      {
        Opacity = 0.3,
      }; // Translucent overlay applied to the row currently being dragged.
    static readonly System.Windows.Media.Pen CurveDragRowHighlightPen =
      new(System.Windows.SystemColors.HighlightBrush, CurveDragRowOutlineWidth); // Outline around the dragged row.

    sealed record CurveRowInfo(
      int LogicalIndex,
      Guid SourceId,
      Curve Curve,
      bool LinkedToPrevious,
      int DisplayNumber);

    readonly NotchSession _s;
    bool _suppress;
    bool _updatingMultipleControls;
    bool _multipleUseDistanceBeforeAuto;
    // Controls
    readonly Button[] _typeButtons;
    readonly NumericStepper _lengthStepper, _offsetStepper, _widthStepper;
    readonly DropDown    _notchLayerDrop;
    readonly CheckBox    _percentCheck, _groupCheck;
    readonly CheckBox    _notchCheck, _labelCheck, _autoAdvCheck, _sideFlipCheck;
    System.Windows.Controls.CheckBox? _notchHeaderCheck;
    System.Windows.Controls.CheckBox? _labelHeaderCheck;
    readonly TextBox     _labelValueBox;
    readonly DropDown    _labelLayerDrop;
    readonly NumericStepper _labelSizeStepper;
    readonly CheckBox    _labelSizeAutoCheck;
    readonly NumericStepper _labelSizePctStepper;
    readonly NumericStepper _labelOffsetStepper, _labelOffsetYStepper;
    readonly NumericStepper _multipleStartOffsetStepper, _multipleEndOffsetStepper;
    readonly NumericStepper _multipleNumberStepper, _multipleDistanceStepper;
    readonly NumericStepper _multipleCurvatureSensitivityStepper;
    readonly CheckBox _multipleStartOffsetCheck, _multipleEndOffsetCheck, _multipleAutoCheck;
    readonly RadioButton _multipleNumberMode, _multipleDistanceMode;
    readonly Button      _multipleAddButton;
    System.Windows.Controls.CheckBox? _multipleSeparateCheck;
    readonly HashSet<Control> _multipleFocusedInputs = [];
    readonly Label       _fromStartLbl, _fromEndLbl, _fromPrevLbl;
    readonly Label       _segmentStartLbl, _segmentEndLbl, _segmentPrevLbl;
    readonly Button      _undoBtn, _redoBtn, _selectCurvesButton;
    System.Windows.Controls.CheckBox? _keepSelectionCheck;
    Button[]   _sideButtons = [];
    Button[]   _reverseButtons = [];
    CheckBox[] _enableChecks = [];
    Label[]    _curveIdentityLabels = [];
    Label[]    _curveLengthLabels = [];
    Panel[]    _curveLengthBadges = [];
    Label?[]   _curveLengthDifferenceLabels = [];
    Label?[]   _curveTotalLabels = [];
    Panel?[]   _curveTotalBadges = [];
    Label?[]   _curveTotalDifferenceLabels = [];
    int _curveTotalColumnWidth;
    CurveRowInfo[] _curveRows = [];
    readonly CurveRowHoverConduit _curveHoverConduit = new();
    Scrollable? _scrollable;
    Scrollable? _curveScrollable;
    Control? _layoutRoot;
    bool _curveSelectionInProgress;
    bool _windowSizePersistenceReady;
    bool _curveRowDragInProgress;
    int _curveDragSourceIndex = -1;
    int _curveDropInsertionIndex = -1;
    readonly Dictionary<int, System.Windows.FrameworkElement> _nativeCurveRows = [];
    readonly Dictionary<int, System.Windows.Media.Brush?> _nativeCurveRowBackgrounds = [];
    System.Windows.Documents.AdornerLayer? _curveDragHighlightLayer;
    CurveDragRowHighlightAdorner? _curveDragHighlightAdorner;
    int _curveDragHighlightedRowIndex = -1;
    int _viewportCurveHoverRowIndex = -1;
    bool _multipleSectionHovered;
    bool _viewportPointerActive;
    bool _selectButtonBrushesCaptured;
    System.Windows.Media.Brush? _selectButtonBackground;
    System.Windows.Media.Brush? _selectButtonBorder;
    System.Windows.Media.Brush? _selectButtonForeground;
    Eto.Drawing.Color _percentDefaultBackgroundColor = Colors.Transparent;
    Eto.Drawing.Color _percentDefaultTextColor = SystemColors.ControlText;
    static readonly System.Windows.Style NotchTypeFocusVisualStyle = CreateOutsideFocusStyle(); // One-pixel outside focus outline for type buttons.

    public NotchPanel(RhinoDoc doc, NotchSession s)
    {
      _s = s;
      _multipleUseDistanceBeforeAuto = s.MultipleUseDistance;
      Title     = "Notches";
      Padding   = new Eto.Drawing.Padding(0);
      Resizable = true;
      Topmost   = true;
      ClientSize = new Eto.Drawing.Size(
        Math.Max(DefaultWindowWidth, _windowWidth),
        _windowHeight > 0 ? _windowHeight : -1);

      // Type
      _typeButtons = new Button[s.NotchTypeValues.Length];
      for (int i = 0; i < _typeButtons.Length; i++)
      {
        int typeIndex = i;
        _typeButtons[i] = new Button
        {
          ToolTip = s.NotchTypeToolTips[i],
          BackgroundColor = Colors.Transparent,
          Width = 18,
          Height = 18,
        };
        _typeButtons[i].Click += (_, __) => SelectNotchType(typeIndex);
        InstallNotchTypeButtonStyle(_typeButtons[i], typeIndex);
        _typeButtons[i].Load += (_, __) =>
          InstallNotchTypeButtonStyle(_typeButtons[typeIndex], typeIndex);
      }

      // Numeric fields
      _lengthStepper = MakeNumberStepper(s.NotchLengthOpt.CurrentValue,
        doc.ModelAbsoluteTolerance, 1e9, 0.1);
      _offsetStepper = MakeNumberStepper(s.NotchOffsetOpt.CurrentValue,
        0.0, 1e9, 0.1);
      _widthStepper = MakeNumberStepper(s.NotchWidthOpt.CurrentValue,
        doc.ModelAbsoluteTolerance, 1e9, 0.1);

      AttachNumericLive(_lengthStepper, v => s.NotchLengthOpt.CurrentValue = v,
        refreshTypeIcons: true);
      AttachNumericLive(_offsetStepper, v => s.NotchOffsetOpt.CurrentValue = v);
      AttachNumericLive(_widthStepper, v => s.NotchWidthOpt.CurrentValue = v,
        refreshTypeIcons: true);

      // Notch layer dropdown
      _notchLayerDrop = LayerSelector.CreateDropDown(
        doc, s.NotchLayerName, SpecialLayerCurrent);
      _notchLayerDrop.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress || LayerSelector.IsDropDownUpdating(_notchLayerDrop)) return;
        s.NotchLayerName = LayerSelector.GetDropDownValue(
          _notchLayerDrop, s.NotchLayerName);
        Redraw();
        Persist();
      };

      _notchCheck = new CheckBox { Text = "", Checked = s.NotchToggle.CurrentValue };
      _notchCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        ApplyFeatureToggle(notch: true, _notchCheck.Checked == true);
      };

      // Percent / Group
      _percentCheck = new CheckBox { Text = "Percent", Checked = s.PercentToggle.CurrentValue };
      _percentDefaultBackgroundColor = _percentCheck.BackgroundColor;
      _percentDefaultTextColor = _percentCheck.TextColor;
      _percentCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        s.PercentToggle.CurrentValue = _percentCheck.Checked == true;
        UpdateMultipleState();
        ApplyCurveLengthHighlights();
        Redraw();
        Persist();
      };
      _groupCheck   = new CheckBox { Text = "Group",   Checked = s.GroupToggle.CurrentValue };
      _groupCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.GroupToggle.CurrentValue = _groupCheck.Checked == true; Redraw(); Persist(); };
      _selectCurvesButton = new Button { Text = "Select", Width = 82, Height = 26 };
      _selectCurvesButton.Click += (_, __) =>
      {
        CommitPendingValues();
        s.CurveSelectionRequested = true;
        SetCurveSelectionInProgress(true);
        RhinoApp.SetFocusToMainWindow(doc);
        if (!RhinoApp.RunScript("_Enter", false))
        {
          s.CurveSelectionRequested = false;
          SetCurveSelectionInProgress(false);
        }
      };
      InstallSelectButtonContent();
      _selectCurvesButton.Load += (_, __) => InstallSelectButtonContent();

      // Label
      _labelCheck    = new CheckBox { Text = "", Checked = s.LabelToggle.CurrentValue };
      _labelValueBox = MakeTextBox(s.LabelValueText);
      _autoAdvCheck  = new CheckBox { ToolTip = "Auto-advance label", Text = "Auto",Checked = s.LabelAutoAdv };
      _sideFlipCheck = new CheckBox { Text = "Side", Checked = s.LabelSideFlip };
      _labelCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        ApplyFeatureToggle(notch: false, _labelCheck.Checked == true);
      };
      AttachTextLive(_labelValueBox, text => s.LabelValueText = text);
      _autoAdvCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelAutoAdv = _autoAdvCheck.Checked == true; Redraw(); Persist(); };
      _sideFlipCheck.CheckedChanged += (_, __) =>
      { if (_suppress) return; s.LabelSideFlip = _sideFlipCheck.Checked == true; Redraw(); Persist(); };

      _labelLayerDrop = LayerSelector.CreateDropDown(doc, s.LabelLayerName);
      _labelLayerDrop.SelectedIndexChanged += (_, __) =>
      {
        if (_suppress || LayerSelector.IsDropDownUpdating(_labelLayerDrop)) return;
        s.LabelLayerName = LayerSelector.GetDropDownValue(
          _labelLayerDrop, s.LabelLayerName);
        Redraw();
        Persist();
      };

      _labelSizeStepper = MakeNumberStepper(s.ManualLabelSize, 0.0, 1e9, 0.1);
      _labelSizeStepper.Width = 72;
      AttachNumericLive(_labelSizeStepper, v => s.ManualLabelSize = Math.Max(0, v));

      _labelSizeAutoCheck = new CheckBox { Text = "Auto", Checked = s.LabelSizeAutoToggle.CurrentValue };
      _labelSizeAutoCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        s.LabelSizeAutoToggle.CurrentValue = _labelSizeAutoCheck.Checked == true;
        UpdateLabelSizeEnabled();
        ApplyDynamic(); Redraw(); Persist();
      };

      _labelSizePctStepper = MakeNumberStepper(
        s.LabelSizePctValues[Math.Max(0, s.LabelSizePctIndex)], 20.0, 100.0, 5.0, 0);
      _labelSizePctStepper.Width = 60;
      _labelSizePctStepper.ValueChanged += (_, __) =>
      {
        if (_suppress) return;
        int value = Math.Clamp((int)Math.Round(_labelSizePctStepper.Value / 5.0) * 5, 20, 100);
        _suppress = true;
        try { _labelSizePctStepper.Value = value; }
        finally { _suppress = false; }
        s.LabelSizePctIndex = Array.IndexOf(s.LabelSizePctValues, value);
        if (s.LabelSizePctIndex < 0) s.LabelSizePctIndex = 0;
        Redraw();
        Persist();
      };
      UpdateLabelSizeEnabled();

      _labelOffsetStepper = MakeNumberStepper(
        s.LabelOffsetOpt.CurrentValue, -1e9, 1e9, 0.1);
      _labelOffsetYStepper = MakeNumberStepper(
        s.LabelOffsetYOpt.CurrentValue, -1e9, 1e9, 0.1);
      AttachNumericLive(_labelOffsetStepper, v => s.LabelOffsetOpt.CurrentValue = v);
      AttachNumericLive(_labelOffsetYStepper, v => s.LabelOffsetYOpt.CurrentValue = v);

      // Multiple notches
      _multipleStartOffsetStepper = MakeNumberStepper(
        s.MultipleStartOffset, 0.0, 1e9, 0.1);
      _multipleEndOffsetStepper = MakeNumberStepper(
        s.MultipleEndOffset, 0.0, 1e9, 0.1);
      _multipleStartOffsetCheck = new CheckBox
      {
        Text = "Start offset",
        Checked = s.MultipleStartOffsetEnabled,
        ToolTip = "Apply the start offset",
      };
      _multipleEndOffsetCheck = new CheckBox
      {
        Text = "End offset",
        Checked = s.MultipleEndOffsetEnabled,
        ToolTip = "Apply the end offset",
      };
      _multipleNumberStepper = MakeNumberStepper(
        s.MultipleNumber, 1.0, 10000.0, 1.0, 0);
      _multipleDistanceStepper = MakeNumberStepper(
        s.MultipleDistance, 0.0, 1e9, 1.0);
      _multipleCurvatureSensitivityStepper = MakeNumberStepper(
        s.MultipleCurvatureSensitivity, 0.0, 1000.0, 1.0, 0);
      _multipleCurvatureSensitivityStepper.ToolTip =
        "Curvature sensitivity: 0 is uniform; each whole-number step makes a small density adjustment";
      _multipleAutoCheck = new CheckBox
      {
        Text = "Auto",
        Checked = s.MultipleAuto,
        ToolTip = "Use curvature-aware spacing with Distance as the maximum spacing",
      };
      _multipleNumberMode = new RadioButton
      {
        Text = "Number",
        Checked = !s.MultipleUseDistance,
        ToolTip = "Use number to calculate even spacing",
      };
      _multipleDistanceMode = new RadioButton(_multipleNumberMode)
      {
        Text = "Distance",
        Checked = s.MultipleUseDistance,
        ToolTip = "Use distance as the minimum spacing",
      };
      _multipleAddButton = new Button { Text = "Add", Height = 26 };
      InstallMultipleAddButtonContent();
      _multipleAddButton.Load += (_, __) => InstallMultipleAddButtonContent();

      _multipleStartOffsetStepper.ValueChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls) return;
        s.MultipleStartOffset = RoundPanelNumber(_multipleStartOffsetStepper.Value);
        UpdateMultipleState();
        Persist();
      };
      _multipleEndOffsetStepper.ValueChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls) return;
        s.MultipleEndOffset = RoundPanelNumber(_multipleEndOffsetStepper.Value);
        UpdateMultipleState();
        Persist();
      };
      _multipleStartOffsetCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        s.MultipleStartOffsetEnabled = _multipleStartOffsetCheck.Checked == true;
        UpdateMultipleState();
        Persist();
      };
      _multipleEndOffsetCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress) return;
        s.MultipleEndOffsetEnabled = _multipleEndOffsetCheck.Checked == true;
        UpdateMultipleState();
        Persist();
      };
      _multipleNumberStepper.ValueChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls) return;
        s.MultipleNumber = Math.Clamp((int)Math.Round(_multipleNumberStepper.Value), 1, 10000);
        s.MultipleAuto = false;
        s.MultipleUseDistance = false;
        UpdateMultipleModeIndicator();
        ApplyMultipleNumber();
        Persist();
      };
      _multipleDistanceStepper.ValueChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls) return;
        ApplyMultipleDistance(_multipleDistanceStepper.Value);
      };
      _multipleNumberMode.CheckedChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls || _multipleNumberMode.Checked != true) return;
        s.MultipleAuto = false;
        s.MultipleUseDistance = false;
        ApplyMultipleNumber();
        Persist();
      };
      _multipleDistanceMode.CheckedChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls || _multipleDistanceMode.Checked != true) return;
        ApplyMultipleDistance(_multipleDistanceStepper.Value);
      };
      _multipleAutoCheck.CheckedChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls) return;
        bool enableAuto = _multipleAutoCheck.Checked == true;
        if (enableAuto)
        {
          if (!s.MultipleAuto)
            _multipleUseDistanceBeforeAuto = s.MultipleUseDistance;
          s.MultipleAuto = true;
          s.MultipleUseDistance = true;
          ApplyMultipleDistance(_multipleDistanceStepper.Value);
        }
        else
        {
          s.MultipleAuto = false;
          s.MultipleUseDistance = _multipleUseDistanceBeforeAuto;
          ApplySelectedMultipleMode();
        }
      };
      _multipleCurvatureSensitivityStepper.ValueChanged += (_, __) =>
      {
        if (_suppress || _updatingMultipleControls) return;
        s.MultipleCurvatureSensitivity = Math.Clamp(
          (int)Math.Round(_multipleCurvatureSensitivityStepper.Value), 0, 1000);
        if (!s.MultipleAuto)
          _multipleUseDistanceBeforeAuto = s.MultipleUseDistance;
        s.MultipleAuto = true;
        s.MultipleUseDistance = true;
        ApplyMultipleDistance(_multipleDistanceStepper.Value);
      };
      _multipleAddButton.Click += (_, __) =>
      {
        CommitPendingValues();
        SyncFromSession();
        PlaceMultipleNotches(doc, s);
        Persist();
        UpdateMultipleState();
      };
      foreach (var control in new Control[]
      {
        _multipleStartOffsetStepper,
        _multipleEndOffsetStepper,
        _multipleNumberStepper,
        _multipleDistanceStepper,
        _multipleCurvatureSensitivityStepper,
      })
        AttachMultipleInputPreviewFocus(control);

      // Distance labels
      _fromStartLbl = new Label { Text = "-" };
      _fromEndLbl   = new Label { Text = "-" };
      _fromPrevLbl  = new Label { Text = "-" };
      _segmentStartLbl = new Label { Text = "" };
      _segmentEndLbl   = new Label { Text = "" };
      _segmentPrevLbl  = new Label { Text = "" };

      // History buttons
      _undoBtn = new Button { Text = "Undo", Width = 54, Height = 24 };
      _undoBtn.Click += (_, __) =>
      {
        RunLocalHistory(doc, redo: false, source: "panel-undo");
      };
      _redoBtn = new Button { Text = "Redo", Width = 54, Height = 24 };
      _redoBtn.Click += (_, __) =>
      {
        RunLocalHistory(doc, redo: true, source: "panel-redo");
      };
      UpdateUndoEnabled();

      // Side/Reverse/Enable per curve
      CreateCurveRowControls(doc);

      // Layout
      _layoutRoot = BuildLayout();
      _scrollable = new Scrollable
      {
        Border = BorderType.None,
        ExpandContentWidth = true,
        ExpandContentHeight = true,
        Content = _layoutRoot,
      };
      Content = _scrollable;
      MinimumSize = new Eto.Drawing.Size(CurveMinimumWidth(), 0);
      ApplyDynamic();
      Shown += (_, __) => Application.Instance.AsyncInvoke(() =>
      {
        if (_windowHeight > 0)
        {
          ClientSize = new Eto.Drawing.Size(
            Math.Max(CurveMinimumWidth(), _windowWidth),
            _windowHeight);
          _curveScrollable?.UpdateScrollSizes();
          _scrollable?.UpdateScrollSizes();
        }
        else
        {
          ResizePanelToContent();
          _windowWidth = ClientSize.Width;
          _windowHeight = ClientSize.Height;
          SaveOptions(_s);
        }
        _windowSizePersistenceReady = true;
      });
      SizeChanged += (_, __) =>
      {
        if (!_windowSizePersistenceReady ||
            ClientSize.Width <= 0 || ClientSize.Height <= 0)
          return;
        _windowWidth = Math.Max(CurveMinimumWidth(), ClientSize.Width);
        _windowHeight = ClientSize.Height;
        SaveOptions(_s);
      };
      MouseEnter += (_, __) =>
      {
        _viewportPointerActive = false;
        ApplyCurveIdentityHighlights();
        RefreshMultiplePreview();
      };
      MouseLeave += (_, __) =>
      {
        _viewportPointerActive = true;
        ApplyCurveIdentityHighlights(_viewportCurveHoverRowIndex);
        RefreshMultiplePreview();
      };

      KeyDown += (_, e) =>
      {
        if (!e.Control || InputEditorFocused()) return;
        bool redo = e.Key == Keys.Y || (e.Key == Keys.Z && e.Shift);
        bool undo = e.Key == Keys.Z && !e.Shift;
        if (!undo && !redo) return;
        RunLocalHistory(doc, redo, "panel");
        e.Handled = true;
      };

      Closed += (_, __) =>
      {
        ClearMultiplePreview();
        ClearCurveRowHover();
        if (!s.SuppressPanelCloseExit)
        {
          CommitPendingValues();
          SaveOptions(s);
          s.PanelClosedExit = true;
          try { RhinoApp.RunScript("_Cancel", false); } catch { }
        }
      };
    }

    bool InputEditorFocused() =>
      _lengthStepper.HasFocus ||
      _offsetStepper.HasFocus ||
      _widthStepper.HasFocus ||
      _labelValueBox.HasFocus ||
      _labelSizeStepper.HasFocus ||
      _labelSizePctStepper.HasFocus ||
      _labelOffsetStepper.HasFocus ||
      _labelOffsetYStepper.HasFocus ||
      _multipleStartOffsetStepper.HasFocus ||
      _multipleEndOffsetStepper.HasFocus ||
      _multipleNumberStepper.HasFocus ||
      _multipleDistanceStepper.HasFocus ||
      _multipleCurvatureSensitivityStepper.HasFocus;

    Control BuildLayout()
    {
      // ── Notch group ──────────────────────────────────────────────────────
      var notchTable = new TableLayout { Padding = new Eto.Drawing.Padding(6), Spacing = new Eto.Drawing.Size(6, 4) };
      var typeSelector = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 2,
        VerticalContentAlignment = VerticalAlignment.Center,
      };
      foreach (var button in _typeButtons)
        typeSelector.Items.Add(new StackLayoutItem(button, false));
      typeSelector.Items.Add(new StackLayoutItem(null, true));
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Type"),   new TableCell(typeSelector,    true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer"),  new TableCell(_notchLayerDrop, true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Length"), new TableCell(_lengthStepper,  true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Width"),  new TableCell(_widthStepper,   true) } });
      notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset"), new TableCell(_offsetStepper,  true) } });
      var notchGroup = new GroupBox { Text = "", Content = notchTable };
      InstallCollapsibleGroupHeader(notchGroup, notchTable, "Notch",
        () => _s.NotchCollapsed, value => _s.NotchCollapsed = value,
        notchToggle: true);

      // ── Multiple group ───────────────────────────────────────────────────
      var multipleTable = new TableLayout { Padding = new Eto.Drawing.Padding(6), Spacing = new Eto.Drawing.Size(6, 4) };
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { new TableCell(_multipleStartOffsetCheck, false), new TableCell(_multipleStartOffsetStepper, true) } });
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { new TableCell(_multipleEndOffsetCheck, false), new TableCell(_multipleEndOffsetStepper, true) } });
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { new TableCell(_multipleAutoCheck, false), new TableCell(_multipleCurvatureSensitivityStepper, true) } });
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { new TableCell(_multipleNumberMode, false), new TableCell(_multipleNumberStepper, true) } });
      var distanceStack = new StackLayout
      {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        Items =
        {
          new StackLayoutItem(_multipleDistanceMode, false),
          new StackLayoutItem(_multipleDistanceStepper, false),
        },
      };
      multipleTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(distanceStack, false), new TableCell(_multipleAddButton, false) } });
      var multipleGroup = new GroupBox { Text = "", Content = multipleTable };
      InstallCollapsibleGroupHeader(multipleGroup, multipleTable, "Multiple",
        () => _s.MultipleCollapsed, value => _s.MultipleCollapsed = value);
      multipleGroup.MouseEnter += (_, __) =>
      {
        _multipleSectionHovered = true;
        RefreshMultiplePreview();
      };
      multipleGroup.MouseLeave += (_, __) =>
      {
        _multipleSectionHovered = false;
        RefreshMultiplePreview();
      };

      // ── Label group ──────────────────────────────────────────────────────
      var labelHeader = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
      labelHeader.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(_labelValueBox, false),
        new TableCell(_autoAdvCheck,  false),
        new TableCell(_sideFlipCheck, false),
        new TableCell(null,           true),   // filler â€” absorbs extra width
      } });

      var sizeRow = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
      sizeRow.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
        new TableCell(_labelSizeStepper,   false),
        new TableCell(_labelSizeAutoCheck, false),
        new TableCell(_labelSizePctStepper,false),
        new TableCell(null,                true),   // filler
      } });

      var labelTable = new TableLayout
      {
        Padding = new Eto.Drawing.Padding(6),
        Spacing = new Eto.Drawing.Size(6, 4),
      };
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL(""),         new TableCell(labelHeader,       true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer"),    new TableCell(_labelLayerDrop,   true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Size"),     new TableCell(sizeRow,           true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset X"), new TableCell(_labelOffsetStepper,  true) } });
      labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset Y"), new TableCell(_labelOffsetYStepper, true) } });
      var labelGroup = new GroupBox { Text = "", Content = labelTable };
      InstallCollapsibleGroupHeader(labelGroup, labelTable, "Label",
        () => _s.LabelCollapsed, value => _s.LabelCollapsed = value,
        labelToggle: true);

      // ── Percent / Group ──────────────────────────────────────────────────
      var pgStack = new StackLayout { Orientation = Orientation.Horizontal, Spacing = 10,
        VerticalContentAlignment = VerticalAlignment.Center };
      pgStack.Items.Add(new StackLayoutItem(_percentCheck, false));
      pgStack.Items.Add(new StackLayoutItem(_groupCheck,   false));
      pgStack.Items.Add(new StackLayoutItem(_selectCurvesButton, false));
      pgStack.Items.Add(new StackLayoutItem(null,          true));

      // ── Per-curve rows ───────────────────────────────────────────────────
      var curveStack = BuildCurveRows();
      _curveScrollable = new Scrollable
      {
        Border = BorderType.None,
        ExpandContentWidth = true,
        ExpandContentHeight = false,
        Height = CurveViewportHeight(),
        Padding = new Eto.Drawing.Padding(0),
        Content = curveStack,
      };
      _curveScrollable.Load += (_, __) => ConfigureCurveScroller();

      // ── Distance info ────────────────────────────────────────────────────
      var distTable = new TableLayout { Spacing = new Eto.Drawing.Size(6, 2) };
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From start"),    new TableCell(_fromStartLbl, false), new TableCell(_segmentStartLbl, false), new TableCell(null, true) } });
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From end"),      new TableCell(_fromEndLbl,   false), new TableCell(_segmentEndLbl,   false), new TableCell(null, true) } });
      distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From previous"), new TableCell(_fromPrevLbl,  false), new TableCell(_segmentPrevLbl,  false), new TableCell(null, true) } });
      var historyButtons = new StackLayout
      {
        Orientation = Orientation.Vertical,
        Spacing = 2,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items =
        {
          new StackLayoutItem(_undoBtn, false),
          new StackLayoutItem(_redoBtn, false),
        },
      };
      var infoRow = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items =
        {
          new StackLayoutItem(distTable, true),
          new StackLayoutItem(historyButtons, false),
        },
      };

      // ── Root (vertical stack, no bottom spacer) ──────────────────────────
      var root = new StackLayout
      {
        Orientation = Orientation.Vertical,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Spacing = 6,
        Padding = new Eto.Drawing.Padding(6),
      };
      root.Items.Add(new StackLayoutItem(notchGroup, false));
      root.Items.Add(new StackLayoutItem(multipleGroup, false));
      root.Items.Add(new StackLayoutItem(labelGroup, false));
      root.Items.Add(new StackLayoutItem(pgStack,    false));
      root.Items.Add(new StackLayoutItem(_curveScrollable, true));
      root.Items.Add(new StackLayoutItem(infoRow,    false));

      return root;
    }

    void CreateCurveRowControls(RhinoDoc doc)
    {
      SetCurveDragRowHighlight(-1, false);
      _curveRows = BuildCurveRowInfos();
      _sideButtons = new Button[_curveRows.Length];
      _reverseButtons = new Button[_curveRows.Length];
      _enableChecks = new CheckBox[_curveRows.Length];
      _curveIdentityLabels = new Label[_curveRows.Length];
      _curveLengthLabels = new Label[_curveRows.Length];
      _curveLengthBadges = new Panel[_curveRows.Length];
      _curveLengthDifferenceLabels = new Label?[_curveRows.Length];
      _curveTotalLabels = new Label?[_curveRows.Length];
      _curveTotalBadges = new Panel?[_curveRows.Length];
      _curveTotalDifferenceLabels = new Label?[_curveRows.Length];
      _curveTotalColumnWidth = 0;
      _nativeCurveRows.Clear();
      _nativeCurveRowBackgrounds.Clear();
      var logicalLengths = Enumerable.Range(0, _s.Curves.Count)
        .Select(index => PlacementCurveLength(_s, index))
        .ToArray();
      double logicalSpan = logicalLengths.Length > 1
        ? logicalLengths.Max() - logicalLengths.Min()
        : 0.0;
      string actualDifferenceWidthSample = logicalSpan > ModelUnitsFromInches(
          _s.Doc, CurveLengthDifferenceToleranceInches)
        ? $"(+{FormatPanelNumber(logicalSpan)})"
        : "";

      for (int i = 0; i < _curveRows.Length; i++)
      {
        int rowIndex = i;
        int curveIndex = _curveRows[i].LogicalIndex;
        _curveIdentityLabels[i] = new Label
        {
          Text = _curveRows[i].DisplayNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
          ToolTip = $"Curve {_curveRows[i].DisplayNumber}",
          VerticalAlignment = VerticalAlignment.Center,
          TextAlignment = TextAlignment.Center,
        };
        _curveIdentityLabels[i].Width = Math.Max(
          CurveIdentityMinimumWidth,
          (int)Math.Ceiling(_curveIdentityLabels[i].GetPreferredSize().Width));
        _curveIdentityLabels[i].Load += (_, __) =>
          ConfigureCurveIdentityLabel(_curveIdentityLabels[rowIndex]);
        bool initialSide = _s.CurveSideBySource.GetValueOrDefault(
          _curveRows[i].SourceId, _s.CurveSides[curveIndex]);
        _sideButtons[i] = new Button
        {
          Width = CurveSideButtonWidth,
          Height = CurveDirectionButtonHeight,
        };
        _sideButtons[i].Load += (_, __) =>
          ConfigureCurveDirectionButton(_sideButtons[rowIndex]);
        UpdateCurveSideButton(rowIndex, initialSide);
        _sideButtons[i].Click += (_, __) =>
        {
          Log.Write("vNotches",
            $"side control row={rowIndex + 1} sequence={curveIndex + 1} suppress={_suppress}");
          if (_suppress) return;
          _s.RedoBatches.Clear();
          bool newSide = !_s.CurveSideBySource.GetValueOrDefault(
            _curveRows[rowIndex].SourceId,
            _s.CurveSides[curveIndex]);
          _s.CurveSideBySource[_curveRows[rowIndex].SourceId] = newSide;
          UpdateCurveSideButton(rowIndex, newSide);
          UpdateLogicalCurveSide(_s, curveIndex);
          RebuildCurveNotches(doc, _s, curveIndex);
          SelectBothCurves(doc, _s);
          UpdateUndoEnabled();
          Log.Write("vNotches",
            $"side changed: row={rowIndex + 1} source={_curveRows[rowIndex].SourceId} " +
            $"side={newSide}");
          Redraw();
          Persist();
        };

        _reverseButtons[i] = new Button
        {
          Width = CurveReverseButtonWidth,
          Height = CurveDirectionButtonHeight,
        };
        _reverseButtons[i].Load += (_, __) =>
          ConfigureCurveDirectionButton(_reverseButtons[rowIndex]);
        UpdateCurveReverseButton(
          rowIndex,
          _s.CurveReversedBySource.GetValueOrDefault(_curveRows[i].SourceId));
        _reverseButtons[i].Click += (_, __) =>
        {
          Log.Write("vNotches",
            $"reverse control row={rowIndex + 1} sequence={curveIndex + 1} suppress={_suppress}");
          if (_suppress) return;
          ReverseSourceCurve(doc, _s, curveIndex, _curveRows[rowIndex].SourceId);
          Log.Write("vNotches",
            $"reversed row={rowIndex + 1} source={_curveRows[rowIndex].SourceId}");
          RefreshCurveRows();
          Redraw();
          Persist();
        };

        _curveLengthLabels[i] = new Label
        {
          Text = FormatPanelNumber(_curveRows[i].Curve.GetLength()),
          VerticalAlignment = VerticalAlignment.Center,
          TextAlignment = TextAlignment.Center,
        };
        ReserveLabelWidth(_curveLengthLabels[i], CurveLengthWidthSample);
        bool linkedSequenceRow = _curveRows[i].LinkedToPrevious ||
          (i + 1 < _curveRows.Length && _curveRows[i + 1].LinkedToPrevious);
        if (!linkedSequenceRow)
          _curveLengthDifferenceLabels[i] = CreateCurveLengthDifferenceLabel(
            CurveLengthDifferenceWidthSample, actualDifferenceWidthSample);
        _curveLengthBadges[i] = new Panel
        {
          Padding = new Eto.Drawing.Padding(
            CurveLengthBadgeHorizontalPadding, 0,
            CurveLengthBadgeHorizontalPadding, 0),
          Content = _curveLengthLabels[i],
        };
        double? totalLength = LinkedSequenceTotalForRow(i);
        if (totalLength.HasValue)
        {
          _curveTotalLabels[i] = new Label
          {
            Text = FormatPanelNumber(totalLength.Value),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
          };
          ReserveLabelWidth(_curveTotalLabels[i]!, CurveLengthWidthSample);
          _curveTotalDifferenceLabels[i] = CreateCurveLengthDifferenceLabel(
            CurveLengthDifferenceWidthSample, actualDifferenceWidthSample);
          _curveTotalBadges[i] = new Panel
          {
            Padding = new Eto.Drawing.Padding(
              CurveLengthBadgeHorizontalPadding, 0,
              CurveLengthBadgeHorizontalPadding, 0),
            Content = _curveTotalLabels[i],
          };
          _curveTotalColumnWidth = Math.Max(
            _curveTotalColumnWidth,
            (int)Math.Ceiling(_curveTotalBadges[i]!.GetPreferredSize().Width) +
              CurveLengthBadgeWidthAllowance);
        }

        if (_s.Curves.Count > 1)
        {
          _enableChecks[i] = new CheckBox
          {
            Checked = curveIndex < _s.CurveEnabled.Length && _s.CurveEnabled[curveIndex],
            ToolTip = "Enable notch on this curve",
          };
          _enableChecks[i].CheckedChanged += (_, __) =>
          {
            if (_suppress) return;
            _s.CurveEnabled[curveIndex] = _enableChecks[rowIndex].Checked == true;
            SyncLinkedEnableChecks(curveIndex);
            UpdateMultipleState();
            Redraw();
            Persist();
          };
        }
      }
      ApplyCurveLengthHighlights();
    }

    static Label CreateCurveLengthDifferenceLabel(
      string widthSample, string actualValueSample)
    {
      var label = new Label
      {
        Text = widthSample,
        Font = new Eto.Drawing.Font(
          Eto.Drawing.SystemFont.Default,
          CurveLengthDifferenceFontSize,
          Eto.Drawing.FontDecoration.None),
        BackgroundColor = Colors.Transparent,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Left,
      };
      label.Load += (_, __) =>
      {
        if (label.ControlObject is System.Windows.FrameworkElement nativeLabel)
          nativeLabel.RenderTransform = new System.Windows.Media.TranslateTransform(
            0.0, -CurveLengthDifferenceRaise);
      };
      int width = (int)Math.Ceiling(label.GetPreferredSize().Width);
      if (!string.IsNullOrEmpty(actualValueSample))
      {
        label.Text = actualValueSample;
        width = Math.Max(width, (int)Math.Ceiling(label.GetPreferredSize().Width));
      }
      label.Width = width;
      label.Text = "";
      return label;
    }

    static void ReserveLabelWidth(Label label, string widthSample)
    {
      string text = label.Text;
      int width = (int)Math.Ceiling(label.GetPreferredSize().Width);
      label.Text = widthSample;
      width = Math.Max(width, (int)Math.Ceiling(label.GetPreferredSize().Width));
      label.Width = width;
      label.Text = text;
    }

    static void ConfigureCurveDirectionButton(Button button)
    {
      if (button.ControlObject is not System.Windows.Controls.Button nativeButton)
        return;
      nativeButton.Template = CreateTransparentButtonTemplate();
      nativeButton.Background = System.Windows.Media.Brushes.Transparent;
      nativeButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
      nativeButton.BorderThickness = new System.Windows.Thickness(0.0);
      nativeButton.Padding = new System.Windows.Thickness(
        CurveDirectionButtonHorizontalPadding,
        CurveDirectionButtonVerticalPadding,
        CurveDirectionButtonHorizontalPadding,
        CurveDirectionButtonVerticalPadding);
      nativeButton.FontSize = CurveDirectionButtonFontSize;
      nativeButton.MinWidth = 0.0;
      nativeButton.MinHeight = 0.0;
      nativeButton.FocusVisualStyle = null;
      nativeButton.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
      nativeButton.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
    }

    static System.Windows.Controls.ControlTemplate CreateTransparentButtonTemplate()
    {
      var template = new System.Windows.Controls.ControlTemplate(
        typeof(System.Windows.Controls.Button));
      var background = new System.Windows.FrameworkElementFactory(
        typeof(System.Windows.Controls.Border), "ButtonBackground");
      background.SetValue(
        System.Windows.Controls.Border.BackgroundProperty,
        System.Windows.Media.Brushes.Transparent);
      var presenter = new System.Windows.FrameworkElementFactory(
        typeof(System.Windows.Controls.ContentPresenter));
      presenter.SetValue(
        System.Windows.Controls.ContentPresenter.ContentSourceProperty,
        "Content");
      presenter.SetValue(
        System.Windows.FrameworkElement.HorizontalAlignmentProperty,
        System.Windows.HorizontalAlignment.Center);
      presenter.SetValue(
        System.Windows.FrameworkElement.VerticalAlignmentProperty,
        System.Windows.VerticalAlignment.Center);
      background.AppendChild(presenter);
      template.VisualTree = background;

      var hoverTrigger = new System.Windows.Trigger
      {
        Property = System.Windows.UIElement.IsMouseOverProperty,
        Value = true,
      };
      hoverTrigger.Setters.Add(new System.Windows.Setter(
        System.Windows.Controls.Border.BackgroundProperty,
        System.Windows.SystemColors.ControlLightBrush,
        "ButtonBackground"));
      template.Triggers.Add(hoverTrigger);

      var pressedTrigger = new System.Windows.Trigger
      {
        Property = System.Windows.Controls.Button.IsPressedProperty,
        Value = true,
      };
      pressedTrigger.Setters.Add(new System.Windows.Setter(
        System.Windows.Controls.Border.BackgroundProperty,
        System.Windows.SystemColors.ControlDarkBrush,
        "ButtonBackground"));
      template.Triggers.Add(pressedTrigger);
      return template;
    }

    static void ConfigureCurveIdentityLabel(Label label)
    {
      if (label.ControlObject is not System.Windows.FrameworkElement nativeLabel)
        return;
      nativeLabel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
      nativeLabel.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
      nativeLabel.Margin = new System.Windows.Thickness(0.0);
      nativeLabel.RenderTransform = new System.Windows.Media.TranslateTransform(
        0.0, CurveIdentityVerticalOffset);
      if (nativeLabel is System.Windows.Controls.TextBlock textBlock)
      {
        textBlock.TextAlignment = System.Windows.TextAlignment.Center;
        textBlock.Padding = new System.Windows.Thickness(0.0);
      }
    }

    void UpdateCurveSideButton(int rowIndex, bool side)
    {
      if (rowIndex < 0 || rowIndex >= _sideButtons.Length)
        return;
      _sideButtons[rowIndex].Text = side
        ? CurveSideCheckedGlyph
        : CurveSideUncheckedGlyph;
      _sideButtons[rowIndex].ToolTip = side
        ? "Side: up (click to switch down)"
        : "Side: down (click to switch up)";
    }

    void UpdateCurveReverseButton(int rowIndex, bool reversed)
    {
      if (rowIndex < 0 || rowIndex >= _reverseButtons.Length)
        return;
      _reverseButtons[rowIndex].Text = reversed
        ? CurveReverseBackwardGlyph
        : CurveReverseForwardGlyph;
      _reverseButtons[rowIndex].ToolTip = reversed
        ? "Direction: left (click to reverse)"
        : "Direction: right (click to reverse)";
    }

    CurveRowInfo[] BuildCurveRowInfos()
    {
      var rows = new List<CurveRowInfo>();
      for (int curveIndex = 0; curveIndex < _s.Curves.Count; curveIndex++)
      {
        IReadOnlyList<Guid> sourceIds =
          curveIndex < _s.PerCurveSourceIds.Count &&
          _s.PerCurveSourceIds[curveIndex].Count > 0
            ? _s.PerCurveSourceIds[curveIndex]
            : [_s.CurveIds[curveIndex]];
        foreach (var sourceId in sourceIds)
        {
          int sourceIndex = rows.Count(row => row.LogicalIndex == curveIndex);
          Curve? sourceCurve = curveIndex < _s.PerCurveSegments.Count &&
            sourceIndex < _s.PerCurveSegments[curveIndex].Count
              ? _s.PerCurveSegments[curveIndex][sourceIndex]
              : _s.Doc.Objects.FindId(sourceId)?.Geometry as Curve;
          rows.Add(new CurveRowInfo(
            curveIndex,
            sourceId,
            sourceCurve?.DuplicateCurve() ?? _s.Curves[curveIndex].DuplicateCurve(),
            rows.Count > 0 && rows[^1].LogicalIndex == curveIndex,
            _s.CurveDisplayNumber(sourceId)));
        }
      }
      return rows.ToArray();
    }

    void SyncLinkedEnableChecks(int curveIndex)
    {
      _suppress = true;
      try
      {
        for (int i = 0; i < _curveRows.Length; i++)
          if (_curveRows[i].LogicalIndex == curveIndex && _enableChecks[i] != null)
            _enableChecks[i].Checked = _s.CurveEnabled[curveIndex];
      }
      finally { _suppress = false; }
    }

    StackLayout BuildCurveRows()
    {
      var curveStack = new StackLayout
      {
        Orientation = Orientation.Vertical,
        Spacing = CurveRowSpacing,
      };
      for (int i = 0; i < _curveRows.Length; i++)
      {
        int rowIndex = i;
        var row = new StackLayout
        {
          Orientation = Orientation.Horizontal,
          Spacing = CurveRowControlSpacing,
          VerticalContentAlignment = VerticalAlignment.Center,
          Height = CurveRowHeight,
        };
        var dragHandle = CreateCurveDragHandle();
        dragHandle.Load += (_, __) => InstallCurveRowDrag(dragHandle, rowIndex);
        row.Items.Add(new StackLayoutItem(dragHandle, false));
        row.Items.Add(new StackLayoutItem(_curveIdentityLabels[i], false));
        if (_s.Curves.Count > 1 && _enableChecks[i] != null)
          row.Items.Add(new StackLayoutItem(_enableChecks[i], false));
        row.Items.Add(new StackLayoutItem(_sideButtons[i], false));
        row.Items.Add(new StackLayoutItem(_reverseButtons[i], false));
        row.Items.Add(new StackLayoutItem(null, true));
        row.Items.Add(new StackLayoutItem(_curveLengthBadges[i], false));
        if (_curveTotalBadges[i] != null)
        {
          Control totalCell = _curveTotalBadges[i]!;
          totalCell.Width = _curveTotalColumnWidth;
          row.Items.Add(new StackLayoutItem(totalCell, false));
          if (_curveTotalDifferenceLabels[i] != null)
            row.Items.Add(new StackLayoutItem(
              _curveTotalDifferenceLabels[i]!, false));
        }
        else if (_curveLengthDifferenceLabels[i] != null)
        {
          row.Items.Add(new StackLayoutItem(
            _curveLengthDifferenceLabels[i]!, false));
        }
        else if (_curveTotalColumnWidth > 0)
        {
          row.Items.Add(new StackLayoutItem(
            new Panel { Width = _curveTotalColumnWidth }, false));
        }
        row.MouseEnter += (_, __) => SetCurveRowHover(rowIndex);
        row.MouseLeave += (_, __) =>
        {
          if (!_curveRowDragInProgress && _curveHoverConduit.CurveIndex == rowIndex)
            ClearCurveRowHover();
        };
        row.Load += (_, __) =>
        {
          if (row.ControlObject is System.Windows.FrameworkElement nativeRow)
          {
            _nativeCurveRows[rowIndex] = nativeRow;
            if (nativeRow is System.Windows.Controls.Panel nativePanel)
              _nativeCurveRowBackgrounds[rowIndex] = nativePanel.Background;
          }
        };
        curveStack.Items.Add(new StackLayoutItem(row));
      }
      curveStack.Load += (_, __) =>
      {
        InstallCurveLinkOverlay(curveStack);
        InstallCurveStackDrop(curveStack);
      };
      return curveStack;
    }

    static Drawable CreateCurveDragHandle()
    {
      var handle = new Drawable
      {
        ToolTip = "Drag to reorder curve",
        Width = CurveDragHandleWidth,
        Height = CurveRowHeight,
      };
      handle.Paint += (_, e) =>
      {
        float clusterWidth = (CurveDragDotDiameter * 2.0f) + CurveDragDotGap;
        float clusterHeight = (CurveDragDotDiameter * 3.0f) + (CurveDragDotGap * 2.0f);
        float left = (CurveDragHandleWidth - clusterWidth) * 0.5f;
        float top = (CurveRowHeight - clusterHeight) * 0.5f;
        for (int column = 0; column < 2; column++)
        {
          for (int row = 0; row < 3; row++)
          {
            e.Graphics.FillEllipse(
              Eto.Drawing.SystemColors.ControlText,
              left + (column * (CurveDragDotDiameter + CurveDragDotGap)),
              top + (row * (CurveDragDotDiameter + CurveDragDotGap)),
              CurveDragDotDiameter,
              CurveDragDotDiameter);
          }
        }
      };
      return handle;
    }

    void InstallCurveLinkOverlay(StackLayout curveStack)
    {
      if (curveStack.ControlObject is not System.Windows.FrameworkElement nativeStack)
        return;

      var boundaries = Enumerable.Range(1, Math.Max(0, _curveRows.Length - 1))
        .Select(index => new CurveLinkBoundary(
          index,
          _curveRows[index].LinkedToPrevious))
        .ToArray();
      if (boundaries.Length == 0) return;

      nativeStack.Dispatcher.BeginInvoke(new Action(() =>
      {
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(nativeStack);
        if (layer == null)
          return;
        foreach (var existing in layer.GetAdorners(nativeStack)?.OfType<CurveLinkAdorner>() ?? [])
          layer.Remove(existing);
        layer.Add(new CurveLinkAdorner(
          nativeStack,
          boundaries,
          ToggleCurveLink,
          SetCurveLinkHover,
          ClearCurveRowHover));
      }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    double? LinkedSequenceTotalForRow(int rowIndex)
    {
      if (rowIndex < 0 || rowIndex + 1 >= _curveRows.Length ||
          !_curveRows[rowIndex + 1].LinkedToPrevious ||
          (rowIndex > 0 && _curveRows[rowIndex].LinkedToPrevious))
        return null;

      int end = rowIndex + 1;
      while (end + 1 < _curveRows.Length && _curveRows[end + 1].LinkedToPrevious)
        end++;
      return Enumerable.Range(rowIndex, end - rowIndex + 1)
        .Sum(index => _curveRows[index].Curve.GetLength());
    }

    List<CurveLayoutItem> CurrentCurveLayout() =>
      _curveRows.Select(row => new CurveLayoutItem(
        row.SourceId, row.Curve.DuplicateCurve(), row.LinkedToPrevious)).ToList();

    void ToggleCurveLink(int boundaryIndex)
    {
      if (boundaryIndex <= 0 || boundaryIndex >= _curveRows.Length)
        return;
      var rows = CurrentCurveLayout();
      rows[boundaryIndex] = rows[boundaryIndex] with
      {
        LinkedToPrevious = !rows[boundaryIndex].LinkedToPrevious,
      };
      if (!ApplyCurveLayout(_s.Doc, _s, rows))
        return;
      RefreshCurveRows();
      Redraw();
      Persist();
    }

    void ReorderCurveRow(int sourceIndex, int insertionIndex)
    {
      if (sourceIndex < 0 || sourceIndex >= _curveRows.Length)
        return;
      var rows = CurrentCurveLayout();

      int linkedStart = sourceIndex;
      while (linkedStart > 0 && rows[linkedStart].LinkedToPrevious)
        linkedStart--;
      int linkedEnd = sourceIndex;
      while (linkedEnd + 1 < rows.Count && rows[linkedEnd + 1].LinkedToPrevious)
        linkedEnd++;
      var originalLinkedIds = linkedEnd > linkedStart
        ? rows.Skip(linkedStart).Take(linkedEnd - linkedStart + 1)
          .Select(row => row.SourceId).ToHashSet()
        : [];

      insertionIndex = Math.Clamp(insertionIndex, 0, rows.Count);
      if (sourceIndex + 1 < rows.Count)
        rows[sourceIndex + 1] = rows[sourceIndex + 1] with { LinkedToPrevious = false };
      var moved = rows[sourceIndex] with { LinkedToPrevious = false };
      rows.RemoveAt(sourceIndex);
      if (sourceIndex < insertionIndex)
        insertionIndex--;
      insertionIndex = Math.Clamp(insertionIndex, 0, rows.Count);
      if (insertionIndex < rows.Count)
        rows[insertionIndex] = rows[insertionIndex] with { LinkedToPrevious = false };
      rows.Insert(insertionIndex, moved);

      if (originalLinkedIds.Count > 1)
      {
        var linkedPositions = Enumerable.Range(0, rows.Count)
          .Where(index => originalLinkedIds.Contains(rows[index].SourceId))
          .ToArray();
        if (linkedPositions.Length == originalLinkedIds.Count &&
            linkedPositions[^1] - linkedPositions[0] + 1 == linkedPositions.Length)
        {
          for (int index = linkedPositions[0]; index <= linkedPositions[^1]; index++)
            rows[index] = rows[index] with
            {
              LinkedToPrevious = index > linkedPositions[0],
            };
        }
      }

      if (rows.Select(row => row.SourceId).SequenceEqual(
          _curveRows.Select(row => row.SourceId)))
        return;
      if (!ApplyCurveLayout(_s.Doc, _s, rows))
        return;
      RefreshCurveRows();
      Redraw();
      Persist();
    }

    void InstallCurveRowDrag(Control dragSurface, int rowIndex)
    {
      if (dragSurface.ControlObject is not System.Windows.FrameworkElement nativeSurface)
        return;
      nativeSurface.Cursor = System.Windows.Input.Cursors.SizeAll;
      System.Windows.Point dragStart = default;
      bool dragArmed = false;
      nativeSurface.PreviewMouseLeftButtonDown += (_, e) =>
      {
        if (e.OriginalSource is System.Windows.DependencyObject source &&
            FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(source) != null)
          return;
        dragStart = e.GetPosition(nativeSurface);
        dragArmed = true;
        nativeSurface.CaptureMouse();
      };
      nativeSurface.PreviewMouseLeftButtonUp += (_, __) =>
      {
        dragArmed = false;
        if (nativeSurface.IsMouseCaptured)
          nativeSurface.ReleaseMouseCapture();
      };
      nativeSurface.GiveFeedback += (_, e) =>
      {
        if (!_curveRowDragInProgress)
          return;
        System.Windows.Input.Mouse.SetCursor(System.Windows.Input.Cursors.SizeAll);
        e.UseDefaultCursors = false;
        e.Handled = true;
      };
      nativeSurface.PreviewMouseMove += (_, e) =>
      {
        if (_curveRowDragInProgress || !dragArmed)
          return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
          return;
        var current = e.GetPosition(nativeSurface);
        if (Math.Abs(current.X - dragStart.X) < System.Windows.SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - dragStart.Y) < System.Windows.SystemParameters.MinimumVerticalDragDistance)
          return;
        _curveRowDragInProgress = true;
        _curveDragSourceIndex = rowIndex;
        SetCurveRowHover(rowIndex);
        SetCurveDragRowHighlight(rowIndex, true);
        if (nativeSurface.IsMouseCaptured)
          nativeSurface.ReleaseMouseCapture();
        try
        {
          System.Windows.DragDrop.DoDragDrop(
            nativeSurface, rowIndex, System.Windows.DragDropEffects.Move);
        }
        finally
        {
          ClearCurveDragPreview();
          SetCurveDragRowHighlight(rowIndex, false);
          _curveDragSourceIndex = -1;
          _curveRowDragInProgress = false;
          ClearCurveRowHover();
          dragArmed = false;
          dragStart = default;
          if (nativeSurface.IsMouseCaptured)
            nativeSurface.ReleaseMouseCapture();
        }
      };
    }

    void InstallCurveStackDrop(StackLayout curveStack)
    {
      if (curveStack.ControlObject is not System.Windows.FrameworkElement nativeStack)
        return;
      nativeStack.AllowDrop = true;
      nativeStack.PreviewDragEnter += (_, e) =>
      {
        if (!e.Data.GetDataPresent(typeof(int))) return;
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
      };
      nativeStack.PreviewDragOver += (_, e) =>
      {
        if (!e.Data.GetDataPresent(typeof(int))) return;
        int insertionIndex = CurveInsertionIndex(
          e.GetPosition(nativeStack).Y,
          _curveDragSourceIndex);
        ApplyCurveDragPreview(_curveDragSourceIndex, insertionIndex);
        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
      };
      nativeStack.PreviewDrop += (_, e) =>
      {
        if (e.Data.GetData(typeof(int)) is not int sourceIndex) return;
        int insertionIndex = _curveDropInsertionIndex >= 0
          ? _curveDropInsertionIndex
          : CurveInsertionIndex(e.GetPosition(nativeStack).Y, sourceIndex);
        ClearCurveDragPreview();
        ReorderCurveRow(sourceIndex, insertionIndex);
        e.Handled = true;
      };
    }

    int CurveInsertionIndex(double y, int sourceIndex)
    {
      if (_curveRows.Length == 0)
        return 0;
      int rowIndex = Math.Clamp(
        (int)Math.Floor(y / CurveRowHeight),
        0,
        _curveRows.Length - 1);
      if (sourceIndex >= 0 && sourceIndex < _curveRows.Length)
      {
        if (rowIndex > sourceIndex)
          return rowIndex + 1;
        if (rowIndex < sourceIndex)
          return rowIndex;
      }
      double withinRow = y - (rowIndex * CurveRowHeight);
      return Math.Clamp(
        rowIndex + (withinRow >= CurveRowHeight * 0.5 ? 1 : 0),
        0,
        _curveRows.Length);
    }

    void ApplyCurveDragPreview(
      int sourceIndex,
      int insertionIndex)
    {
      if (sourceIndex < 0 || sourceIndex >= _curveRows.Length)
        return;
      insertionIndex = Math.Clamp(insertionIndex, 0, _curveRows.Length);
      if (_curveDropInsertionIndex == insertionIndex)
        return;

      ResetCurveRowTransforms();
      _curveDropInsertionIndex = insertionIndex;
      int adjustedInsertion = insertionIndex;
      if (sourceIndex < adjustedInsertion)
        adjustedInsertion--;
      adjustedInsertion = Math.Clamp(adjustedInsertion, 0, _curveRows.Length - 1);

      if (_nativeCurveRows.TryGetValue(sourceIndex, out var sourceRow))
        sourceRow.RenderTransform = new System.Windows.Media.TranslateTransform(
          0.0,
          (adjustedInsertion - sourceIndex) * CurveRowHeight);
      if (adjustedInsertion > sourceIndex)
      {
        for (int index = sourceIndex + 1; index <= adjustedInsertion; index++)
          if (_nativeCurveRows.TryGetValue(index, out var row))
            row.RenderTransform = new System.Windows.Media.TranslateTransform(0.0, -CurveRowHeight);
      }
      else if (adjustedInsertion < sourceIndex)
      {
        for (int index = adjustedInsertion; index < sourceIndex; index++)
          if (_nativeCurveRows.TryGetValue(index, out var row))
            row.RenderTransform = new System.Windows.Media.TranslateTransform(0.0, CurveRowHeight);
      }
    }

    void ResetCurveRowTransforms()
    {
      foreach (var row in _nativeCurveRows.Values)
      {
        row.Visibility = System.Windows.Visibility.Visible;
        row.RenderTransform = System.Windows.Media.Transform.Identity;
      }
    }

    void ClearCurveDragPreview()
    {
      ResetCurveRowTransforms();
      _curveDropInsertionIndex = -1;
    }

    void SetCurveDragRowHighlight(int rowIndex, bool highlighted)
    {
      if (_curveDragHighlightLayer != null && _curveDragHighlightAdorner != null)
        _curveDragHighlightLayer.Remove(_curveDragHighlightAdorner);
      _curveDragHighlightLayer = null;
      _curveDragHighlightAdorner = null;
      if (_curveDragHighlightedRowIndex >= 0 &&
          _nativeCurveRows.TryGetValue(_curveDragHighlightedRowIndex, out var previousRow) &&
          previousRow is System.Windows.Controls.Panel previousPanel)
        previousPanel.Background = _nativeCurveRowBackgrounds.GetValueOrDefault(
          _curveDragHighlightedRowIndex);
      _curveDragHighlightedRowIndex = -1;

      if (!highlighted || !_nativeCurveRows.TryGetValue(rowIndex, out var nativeRow))
        return;
      _curveDragHighlightedRowIndex = rowIndex;
      var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(nativeRow);
      if (layer != null)
      {
        _curveDragHighlightLayer = layer;
        _curveDragHighlightAdorner = new CurveDragRowHighlightAdorner(nativeRow);
        layer.Add(_curveDragHighlightAdorner);
      }
      else if (nativeRow is System.Windows.Controls.Panel nativePanel)
      {
        nativePanel.Background = CurveDragRowHighlightBrush;
      }
    }

    static T? FindVisualParent<T>(System.Windows.DependencyObject? child)
      where T : System.Windows.DependencyObject
    {
      while (child != null)
      {
        if (child is T match) return match;
        child = System.Windows.Media.VisualTreeHelper.GetParent(child);
      }
      return null;
    }

    sealed class CurveDragRowHighlightAdorner : System.Windows.Documents.Adorner
    {
      public CurveDragRowHighlightAdorner(System.Windows.UIElement adornedElement)
        : base(adornedElement)
      {
        IsHitTestVisible = false;
      }

      protected override void OnRender(System.Windows.Media.DrawingContext drawingContext)
      {
        double inset = CurveDragRowOutlineWidth * 0.5;
        var size = AdornedElement.RenderSize;
        var bounds = new System.Windows.Rect(
          inset,
          inset,
          Math.Max(0.0, size.Width - CurveDragRowOutlineWidth),
          Math.Max(0.0, size.Height - CurveDragRowOutlineWidth));
        drawingContext.DrawRectangle(
          CurveDragRowHighlightBrush,
          CurveDragRowHighlightPen,
          bounds);
      }
    }

    sealed record CurveLinkBoundary(int RowIndex, bool Linked);

    sealed class CurveLinkAdorner : System.Windows.Documents.Adorner
    {
      readonly System.Windows.Media.VisualCollection _visuals;
      readonly List<(System.Windows.Controls.Button Button,
        CurveLinkBoundary Boundary)> _items = [];

      public CurveLinkAdorner(
        System.Windows.UIElement adornedElement,
        IReadOnlyList<CurveLinkBoundary> boundaries,
        Action<int> toggle,
        Action<int> hover,
        Action clearHover)
        : base(adornedElement)
      {
        _visuals = new System.Windows.Media.VisualCollection(this);
        foreach (var boundary in boundaries)
        {
          var button = CreateLinkButton(boundary.Linked);
          button.ToolTip = boundary.Linked ? "Unlink curves" : "Link curves";
          int rowIndex = boundary.RowIndex;
          button.Click += (_, __) => toggle(rowIndex);
          button.MouseEnter += (_, __) => hover(rowIndex);
          button.MouseLeave += (_, __) => clearHover();
          _items.Add((button, boundary));
          _visuals.Add(button);
        }
      }

      static System.Windows.Controls.Button CreateLinkButton(bool linked)
      {
        var canvas = new System.Windows.Controls.Canvas { Width = 12.0, Height = 9.0 };
        var brush = linked
          ? System.Windows.SystemColors.HighlightBrush
          : System.Windows.SystemColors.GrayTextBrush;
        foreach (double left in new[] { 0.0, 5.0 })
        {
          var link = new System.Windows.Controls.Border
          {
            Width = 7.0,
            Height = 5.0,
            CornerRadius = new System.Windows.CornerRadius(2.5),
            BorderThickness = new System.Windows.Thickness(1.25),
            BorderBrush = brush,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new System.Windows.Media.RotateTransform(-35.0),
          };
          System.Windows.Controls.Canvas.SetLeft(link, left);
          System.Windows.Controls.Canvas.SetTop(link, 2.0);
          canvas.Children.Add(link);
        }
        return new System.Windows.Controls.Button
        {
          Width = 16.0,
          Height = 16.0,
          Padding = new System.Windows.Thickness(2.0),
          BorderThickness = new System.Windows.Thickness(0.0),
          BorderBrush = System.Windows.Media.Brushes.Transparent,
          Background = System.Windows.Media.Brushes.Transparent,
          Content = canvas,
          Focusable = true,
        };
      }

      protected override int VisualChildrenCount => _visuals.Count;
      protected override System.Windows.Media.Visual GetVisualChild(int index) => _visuals[index];

      protected override System.Windows.Media.HitTestResult? HitTestCore(
        System.Windows.Media.PointHitTestParameters hitTestParameters) => null;

      protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
      {
        foreach (var (button, boundary) in _items)
        {
          double y = boundary.RowIndex * CurveRowHeight - 9.0;
          button.Arrange(new System.Windows.Rect(0.0, y, 16.0, 16.0));
        }
        return finalSize;
      }
    }

    int CurveViewportHeight()
    {
      int visibleRows = Math.Clamp(_curveRows.Length, 1, 3);
      return visibleRows * CurveRowHeight;
    }

    int CurveMinimumWidth()
    {
      return DefaultWindowWidth;
    }

    void ConfigureCurveScroller()
    {
      var root = _curveScrollable?.ControlObject as System.Windows.DependencyObject;
      var native = FindVisualChild<System.Windows.Controls.ScrollViewer>(root);
      if (native == null)
        return;

      native.HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled;
      native.VerticalScrollBarVisibility = _curveRows.Length <= 3
        ? System.Windows.Controls.ScrollBarVisibility.Hidden
        : System.Windows.Controls.ScrollBarVisibility.Auto;
    }

    static T? FindVisualChild<T>(System.Windows.DependencyObject? root)
      where T : System.Windows.DependencyObject
    {
      if (root == null)
        return null;
      if (root is T match)
        return match;

      int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
      for (int i = 0; i < childCount; i++)
      {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
        var descendant = FindVisualChild<T>(child);
        if (descendant != null)
          return descendant;
      }
      return null;
    }

    public void RefreshCurveRows()
    {
      if (_curveScrollable == null)
        return;

      ClearCurveDragPreview();
      ClearCurveRowHover();
      CreateCurveRowControls(_s.Doc);
      ConfigureCurveScroller();
      _curveScrollable.Content = BuildCurveRows();
      _curveScrollable.Height = CurveViewportHeight();
      ConfigureCurveScroller();
      MinimumSize = new Eto.Drawing.Size(CurveMinimumWidth(), 0);
      if (ClientSize.Width < MinimumSize.Width)
        ClientSize = new Eto.Drawing.Size(MinimumSize.Width, ClientSize.Height);

      SyncFromSession();
      UpdateMultipleState();
      Application.Instance.AsyncInvoke(() =>
      {
        ConfigureCurveScroller();
        ResizePanelToContent(growOnly: true);
      });
    }

    public void SetCurveSelectionInProgress(bool selecting)
    {
      _curveSelectionInProgress = selecting;
      // Opacity = selecting ? 0.72 : 1.0;
      if (_selectCurvesButton.ControlObject is System.Windows.Controls.Button nativeButton)
        ApplySelectButtonState(nativeButton);
    }

    void ApplySelectButtonState(System.Windows.Controls.Button nativeButton)
    {
      if (!_selectButtonBrushesCaptured)
      {
        _selectButtonBackground = nativeButton.Background;
        _selectButtonBorder = nativeButton.BorderBrush;
        _selectButtonForeground = nativeButton.Foreground;
        _selectButtonBrushesCaptured = true;
      }

      nativeButton.Background = _curveSelectionInProgress
        ? System.Windows.SystemColors.HighlightBrush
        : _selectButtonBackground;
      nativeButton.BorderBrush = _curveSelectionInProgress
        ? System.Windows.SystemColors.HighlightBrush
        : _selectButtonBorder;
      nativeButton.Foreground = _curveSelectionInProgress
        ? System.Windows.SystemColors.HighlightTextBrush
        : _selectButtonForeground;
    }

    static TableCell FL(string text) =>
      new TableCell(new Label { Text = text, VerticalAlignment = VerticalAlignment.Center });

    void Redraw() => _s.Doc.Views.Redraw();

    void SetCurveRowHover(int curveIndex)
    {
      if (curveIndex < 0 || curveIndex >= _curveRows.Length)
        return;
      _curveHoverConduit.CurveIndex = curveIndex;
      _curveHoverConduit.Curve = _curveRows[curveIndex].Curve;
      _curveHoverConduit.SecondCurve = null;
      _curveHoverConduit.Enabled = true;
      ApplyCurveIdentityHighlights(curveIndex);
      Redraw();
    }

    void SetCurveLinkHover(int boundaryIndex)
    {
      if (boundaryIndex <= 0 || boundaryIndex >= _curveRows.Length)
        return;
      _curveHoverConduit.CurveIndex = -1;
      _curveHoverConduit.Curve = _curveRows[boundaryIndex - 1].Curve;
      _curveHoverConduit.SecondCurve = _curveRows[boundaryIndex].Curve;
      _curveHoverConduit.Enabled = true;
      ApplyCurveIdentityHighlights(boundaryIndex - 1, boundaryIndex);
      Redraw();
    }

    void ClearCurveRowHover()
    {
      ApplyCurveIdentityHighlights(
        _viewportPointerActive ? _viewportCurveHoverRowIndex : -1);
      if (!_curveHoverConduit.Enabled &&
          _curveHoverConduit.Curve == null &&
          _curveHoverConduit.SecondCurve == null)
        return;
      _curveHoverConduit.Enabled = false;
      _curveHoverConduit.Curve = null;
      _curveHoverConduit.SecondCurve = null;
      _curveHoverConduit.CurveIndex = -1;
      Redraw();
    }

    void ApplyCurveIdentityHighlights(params int[] rowIndices)
    {
      var highlighted = rowIndices.Where(index =>
        index >= 0 && index < _curveIdentityLabels.Length).ToHashSet();
      for (int index = 0; index < _curveIdentityLabels.Length; index++)
      {
        bool active = highlighted.Contains(index);
        _curveIdentityLabels[index].BackgroundColor = active
          ? CurveIdentityHoverBackground
          : Colors.Transparent;
        _curveIdentityLabels[index].TextColor = active
          ? CurveIdentityHoverForeground
          : SystemColors.ControlText;
      }
    }

    public void SetViewportCurveHover(int curveIndex, double lengthFromStart)
    {
      int rowIndex = -1;
      if (curveIndex >= 0 && curveIndex < _s.PerCurveSourceIds.Count)
      {
        int sourceIndex = ResolvePlacementSourceIndex(
          _s, curveIndex, lengthFromStart, null);
        if (sourceIndex >= 0 &&
            sourceIndex < _s.PerCurveSourceIds[curveIndex].Count)
        {
          Guid sourceId = _s.PerCurveSourceIds[curveIndex][sourceIndex];
          rowIndex = Array.FindIndex(
            _curveRows, row => row.SourceId == sourceId);
        }
      }
      _viewportCurveHoverRowIndex = rowIndex;
      if (_viewportPointerActive)
        ApplyCurveIdentityHighlights(rowIndex);
    }

    void ApplyDynamic()
    {
      UpdateMultipleState();
    }

    static TextBox MakeTextBox(string text) =>
      new TextBox { Text = text, Width = 70, Height = 22 };

    static NumericStepper MakeNumberStepper(double value, double minValue,
      double maxValue, double increment, int maximumDecimalPlaces = 3)
    {
      return new NumericStepper
      {
        Value = Math.Clamp(RoundForDisplay(value, maximumDecimalPlaces), minValue, maxValue),
        MinValue = minValue,
        MaxValue = maxValue,
        Increment = increment,
        DecimalPlaces = 0,
        MaximumDecimalPlaces = maximumDecimalPlaces,
        CultureInfo = System.Globalization.CultureInfo.InvariantCulture,
        Width = 90,
        Height = 22,
      };
    }

    static double RoundForDisplay(double value, int decimalPlaces) =>
      Math.Round(value, decimalPlaces, MidpointRounding.AwayFromZero);

    static double RoundPanelNumber(double value) => RoundForDisplay(value, 3);

    static System.Windows.FrameworkElement CreateNotchTypeGlyph(string notchType, bool active,
      double notchLength, double notchWidth)
    {
      const double size = 12.0; // Notch glyph canvas width and height in WPF device-independent pixels.
      const double center = size * 0.5; // Derived glyph center coordinate in WPF device-independent pixels.
      const double available = 11.25; // Maximum scaled notch span inside the glyph canvas.
      double modelHeight = Math.Max(notchLength, RhinoMath.ZeroTolerance);
      double modelWidth = Math.Max(notchWidth, RhinoMath.ZeroTolerance);
      double scale = Math.Min(available / modelWidth, available / modelHeight);
      double width = modelWidth * scale;
      double height = modelHeight * scale;
      double left = center - width * 0.5;
      double right = center + width * 0.5;
      double top = center - height * 0.5;
      double bottom = center + height * 0.5;

      var strokes = new List<System.Windows.Media.PointCollection>();
      switch (CanonicalNotchType(notchType))
      {
        case "V":
          strokes.Add([
            new System.Windows.Point(left, top),
            new System.Windows.Point(center, bottom),
            new System.Windows.Point(right, top),
          ]);
          break;
        case OpenVNotchType:
          double openHalfFlat = width * 0.22;
          strokes.Add([
            new System.Windows.Point(left, top),
            new System.Windows.Point(center - openHalfFlat, bottom),
          ]);
          strokes.Add([
            new System.Windows.Point(center + openHalfFlat, bottom),
            new System.Windows.Point(right, top),
          ]);
          break;
        case "U":
          // Exaggerate the cap in the tiny icon so U remains distinguishable
          // from V even when the configured cap is proportionally very short.
          double halfFlat = width * 0.22;
          strokes.Add([
            new System.Windows.Point(left, top),
            new System.Windows.Point(center - halfFlat, bottom),
            new System.Windows.Point(center + halfFlat, bottom),
            new System.Windows.Point(right, top),
          ]);
          break;
        case "T":
          strokes.Add([
            new System.Windows.Point(center, top),
            new System.Windows.Point(center, bottom),
          ]);
          strokes.Add([
            new System.Windows.Point(left, bottom),
            new System.Windows.Point(right, bottom),
          ]);
          break;
        default:
          strokes.Add([
            new System.Windows.Point(center, center - available * 0.5),
            new System.Windows.Point(center, center + available * 0.5),
          ]);
          break;
      }

      var stroke = active
        ? System.Windows.SystemColors.ControlTextBrush //new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 120, 215))
        : System.Windows.SystemColors.GrayTextBrush;
      var canvas = new System.Windows.Controls.Canvas
      {
        Width = size,
        Height = size,
        SnapsToDevicePixels = true,
        UseLayoutRounding = true,
        IsHitTestVisible = false,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
      };
      foreach (var points in strokes)
      {
        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
          Points = points,
          Stroke = stroke,
          StrokeThickness = 1.5,
          StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
          StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
          SnapsToDevicePixels = true,
          IsHitTestVisible = false,
        });
      }

      const double padding = 3.0; // Padding around each notch glyph in WPF device-independent pixels.
      var content = new System.Windows.Controls.Grid
      {
        Background = active ? System.Windows.SystemColors.ControlDarkBrush: System.Windows.Media.Brushes.Transparent,
        Width = size + (padding * 2.0),
        Height = size + (padding * 2.0),
        SnapsToDevicePixels = true,
        UseLayoutRounding = true,
        IsHitTestVisible = false,
      };
      content.Children.Add(new System.Windows.Controls.Border
      {
        BorderBrush = new System.Windows.Media.SolidColorBrush(
          active ? System.Windows.Media.Color.FromRgb(0, 120, 215)
                  : System.Windows.SystemColors.ControlLightBrush.Color),
        BorderThickness = new System.Windows.Thickness(1.0),
        SnapsToDevicePixels = true,
        IsHitTestVisible = false,
      });
      content.Children.Add(canvas);
      return content;
    }

    void InstallNotchTypeButtonStyle(Button button, int typeIndex)
    {
      if (button.ControlObject is not System.Windows.Controls.Button native)
        return;
      bool active = typeIndex == _s.NotchTypeIndex;
      native.Background = System.Windows.Media.Brushes.Transparent;
      native.BorderBrush = System.Windows.Media.Brushes.Transparent;
      native.Padding = new System.Windows.Thickness(0);
      native.BorderThickness = new System.Windows.Thickness(0);
      native.MinWidth = 0;
      native.MinHeight = 0;
      native.Width = 18;
      native.Height = 18;
      native.Focusable = true;
      native.FocusVisualStyle = NotchTypeFocusVisualStyle;
      native.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
      native.VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
      native.Content = CreateNotchTypeGlyph(
        _s.NotchTypeValues[typeIndex], active,
        _s.NotchLengthOpt.CurrentValue, _s.NotchWidthOpt.CurrentValue);
    }
    static System.Windows.Style CreateOutsideFocusStyle()
    {
      var template = new System.Windows.Controls.ControlTemplate(
        typeof(System.Windows.Controls.Control));

      var outline = new System.Windows.FrameworkElementFactory(
        typeof(System.Windows.Shapes.Rectangle));
      outline.SetValue(
        System.Windows.Shapes.Shape.StrokeProperty,
        System.Windows.SystemColors.ControlTextBrush);
      outline.SetValue(
        System.Windows.Shapes.Shape.StrokeThicknessProperty,
        0.5);
      outline.SetValue(
        System.Windows.Shapes.Shape.StrokeDashArrayProperty,
        new System.Windows.Media.DoubleCollection { 1.0, 2.0 });
      outline.SetValue(
        System.Windows.FrameworkElement.MarginProperty,
        new System.Windows.Thickness(-1.0));
      outline.SetValue(
        System.Windows.FrameworkElement.SnapsToDevicePixelsProperty,
        true);
      outline.SetValue(
        System.Windows.UIElement.IsHitTestVisibleProperty,
        false);
      template.VisualTree = outline;

      var style = new System.Windows.Style(
        typeof(System.Windows.Controls.Control));

      style.Setters.Add(new System.Windows.Setter(
        System.Windows.Controls.Control.TemplateProperty,
        template));

      return style;
    }
    void SelectNotchType(int typeIndex)
    {
      if (_suppress)
        return;

      int selected = Math.Clamp(typeIndex, 0, _typeButtons.Length - 1);
      _suppress = true;
      try
      {
        _s.NotchTypeIndex = selected;
        RefreshNotchTypeIcons();
      }
      finally { _suppress = false; }

      ApplyDynamic();
      Redraw();
      Persist();
    }

    void RefreshNotchTypeIcons()
    {
      for (int i = 0; i < _typeButtons.Length; i++)
        InstallNotchTypeButtonStyle(_typeButtons[i], i);
    }

    void InstallCollapsibleGroupHeader(GroupBox group, Control content, string title,
      Func<bool> getCollapsed, Action<bool> setCollapsed,
      bool notchToggle = false, bool labelToggle = false)
    {
      System.Windows.Controls.StackPanel? headerPanel = null;
      System.Windows.Controls.Button? collapseButton = null;
      System.Windows.Controls.GroupBox? nativeGroup = null;

      static System.Windows.Shapes.Polyline DisclosureChevron(bool collapsed)
      {
        var points = new System.Windows.Media.PointCollection();
        if (collapsed)
        {
          points.Add(new System.Windows.Point(4, 2));
          points.Add(new System.Windows.Point(8, 6));
          points.Add(new System.Windows.Point(4, 10));
        }
        else
        {
          points.Add(new System.Windows.Point(2, 4));
          points.Add(new System.Windows.Point(6, 8));
          points.Add(new System.Windows.Point(10, 4));
        }

        return new System.Windows.Shapes.Polyline
        {
          Points = points,
          Stroke = System.Windows.SystemColors.ControlTextBrush,
          StrokeThickness = 1.5,
          StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
          StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
          Width = 12,
          Height = 12,
        };
      }

      void ApplyCollapsedState()
      {
        bool collapsed = getCollapsed();
        content.Visible = !collapsed;
        if (collapseButton != null)
        {
          collapseButton.Content = DisclosureChevron(collapsed);
          collapseButton.ToolTip = collapsed ? $"Restore {title}" : $"Collapse {title}";
        }
        nativeGroup?.InvalidateMeasure();
        if (Loaded)
          Application.Instance.AsyncInvoke(() => ResizePanelToContent());
      }

      void Install()
      {
        if (group.ControlObject is not System.Windows.Controls.GroupBox native)
          return;
        nativeGroup = native;

        if (headerPanel == null)
        {
          headerPanel = new System.Windows.Controls.StackPanel
          {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
          };

          collapseButton = new System.Windows.Controls.Button
          {
            Content = DisclosureChevron(getCollapsed()),
            Width = 18,
            Height = 18,
            Padding = new System.Windows.Thickness(0),
            Margin = new System.Windows.Thickness(0, 0, 3, 0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = System.Windows.Media.Brushes.Transparent,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
            Focusable = false,
          };
          collapseButton.Click += (_, __) =>
          {
            setCollapsed(!getCollapsed());
            ApplyCollapsedState();
          };
          headerPanel.Children.Add(collapseButton);

          if (notchToggle || labelToggle)
          {
            bool isNotch = notchToggle;
            var headerCheck = new System.Windows.Controls.CheckBox
            {
              Content = title,
              IsChecked = isNotch
                ? _s.NotchToggle.CurrentValue
                : _s.LabelToggle.CurrentValue,
              VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            headerCheck.Checked += (_, __) => SetFeatureEnabledFromHeader(isNotch, true);
            headerCheck.Unchecked += (_, __) => SetFeatureEnabledFromHeader(isNotch, false);
            if (isNotch) _notchHeaderCheck = headerCheck;
            else _labelHeaderCheck = headerCheck;
            headerPanel.Children.Add(headerCheck);
          }
          else
          {
            headerPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
              Text = title,
              VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
          }
        }

        native.Header = headerPanel;
        ApplyCollapsedState();
      }

      content.Visible = !getCollapsed();
      Install();
      group.Load += (_, __) => Install();
    }

    void ResizePanelToContent(bool growOnly = false)
    {
      if (_layoutRoot == null)
        return;
      _layoutRoot.UpdateLayout();
      _curveScrollable?.UpdateScrollSizes();
      _scrollable?.UpdateScrollSizes();
      var preferred = _layoutRoot.GetPreferredSize();
      int requiredHeight = Math.Max(1, (int)Math.Ceiling(preferred.Height));
      int height = growOnly
        ? Math.Max(ClientSize.Height, requiredHeight)
        : requiredHeight;
      ClientSize = new Eto.Drawing.Size(
        Math.Max(CurveMinimumWidth(), ClientSize.Width), height);
    }

    sealed class CurveRowHoverConduit : DisplayConduit
    {
      public int CurveIndex { get; set; } = -1;
      public Curve? Curve { get; set; }
      public Curve? SecondCurve { get; set; }

      protected override void DrawForeground(DrawEventArgs e)
      {
        if (Curve != null)
          PreviewDisplay.DrawCurve(e.Display, Curve, System.Drawing.Color.Black, 3);
        if (SecondCurve != null)
          PreviewDisplay.DrawCurve(e.Display, SecondCurve, System.Drawing.Color.Black, 3);
      }
    }

    void SetFeatureEnabledFromHeader(bool notch, bool enabled)
    {
      if (_suppress)
        return;
      var check = notch ? _notchCheck : _labelCheck;
      if (check.Checked != enabled)
        check.Checked = enabled;
    }

    void InstallSelectButtonContent()
    {
      if (_selectCurvesButton.ControlObject is not System.Windows.Controls.Button nativeButton)
        return;
      if (_keepSelectionCheck != null)
      {
        ApplySelectButtonState(nativeButton);
        return;
      }

      var content = new System.Windows.Controls.StackPanel
      {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
      };
      content.Children.Add(new System.Windows.Controls.TextBlock
      {
        Text = "Select",
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
      });

      _keepSelectionCheck = new System.Windows.Controls.CheckBox
      {
        IsChecked = _s.KeepCurveSelection,
        Margin = new System.Windows.Thickness(7, 0, 0, 0),
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        ToolTip = "Keep current curve selection",
      };
      _keepSelectionCheck.Click += (_, e) =>
      {
        _s.KeepCurveSelection = _keepSelectionCheck.IsChecked == true;
        Persist();
        e.Handled = true;
      };
      content.Children.Add(_keepSelectionCheck);
      nativeButton.Content = content;
      nativeButton.ToolTip = "Select curves; check the box to keep the current selection";
      ApplySelectButtonState(nativeButton);
    }

    void InstallMultipleAddButtonContent()
    {
      if (_multipleAddButton.ControlObject is not System.Windows.Controls.Button nativeButton)
        return;
      if (_multipleSeparateCheck != null)
      {
        _multipleSeparateCheck.IsChecked = _s.MultipleSeparate;
        return;
      }

      var content = new System.Windows.Controls.StackPanel
      {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
      };
      content.Children.Add(new System.Windows.Controls.TextBlock
      {
        Text = "Add",
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
      });
      _multipleSeparateCheck = new System.Windows.Controls.CheckBox
      {
        IsChecked = _s.MultipleSeparate,
        // Content = "Separate",
        Margin = new System.Windows.Thickness(7, 0, 0, 0),
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        ToolTip = "Apply the multiple layout separately to each linked curve segment",
      };
      _multipleSeparateCheck.Click += (_, e) =>
      {
        _s.MultipleSeparate = _multipleSeparateCheck.IsChecked == true;
        UpdateMultipleState();
        Persist();
        e.Handled = true;
      };
      content.Children.Add(_multipleSeparateCheck);
      nativeButton.Content = content;
      nativeButton.ToolTip =
        "Add multiple notches; check Separate to apply the layout to each linked curve segment";
    }

    void ApplyFeatureToggle(bool notch, bool enabled)
    {
      if (_suppress) return;

      _suppress = true;
      try
      {
        if (notch)
        {
          _s.NotchToggle.CurrentValue = enabled;
          _notchCheck.Checked = enabled;
          if (_notchHeaderCheck != null) _notchHeaderCheck.IsChecked = enabled;
          if (!enabled && !_s.LabelToggle.CurrentValue)
          {
            _s.LabelToggle.CurrentValue = true;
            _labelCheck.Checked = true;
            if (_labelHeaderCheck != null) _labelHeaderCheck.IsChecked = true;
          }
        }
        else
        {
          _s.LabelToggle.CurrentValue = enabled;
          _labelCheck.Checked = enabled;
          if (_labelHeaderCheck != null) _labelHeaderCheck.IsChecked = enabled;
          if (!enabled && !_s.NotchToggle.CurrentValue)
          {
            _s.NotchToggle.CurrentValue = true;
            _notchCheck.Checked = true;
            if (_notchHeaderCheck != null) _notchHeaderCheck.IsChecked = true;
          }
        }
      }
      finally { _suppress = false; }

      ApplyDynamic();
      Redraw();
      Persist();
    }

    void AttachNumericLive(NumericStepper stepper, Action<double> apply,
      bool refreshTypeIcons = false)
    {
      stepper.ValueChanged += (_, __) =>
      {
        if (_suppress) return;
        apply(RoundPanelNumber(stepper.Value));
        if (refreshTypeIcons)
          RefreshNotchTypeIcons();
        Redraw();
        Persist();
      };
    }

    void AttachTextLive(TextBox box, Action<string> apply)
    {
      void ApplyPreview()
      {
        if (_suppress) return;
        apply(box.Text);
        Redraw();
      }

      void ApplyCommit()
      {
        if (_suppress) return;
        apply(box.Text);
        Redraw();
        Persist();
      }

      box.TextChanged += (_, __) => ApplyPreview();
      box.LostFocus   += (_, __) => ApplyCommit();
      box.KeyDown     += (_, e) =>
      {
        if (e.Key == Keys.Enter)
        {
          ApplyCommit();
          e.Handled = true;
        }
      };
    }

    void UpdateMultipleState()
    {
      if (_s.MultipleAuto)
        _s.MultipleUseDistance = true;
      UpdateMultipleModeIndicator();
      if (_s.MultipleUseDistance && _s.MultipleDistance > _s.Doc.ModelAbsoluteTolerance)
        ApplyMultipleDistance(_s.MultipleDistance, persist: false);
      else
        ApplyMultipleNumber();
    }

    void UpdateMultipleModeIndicator()
    {
      _updatingMultipleControls = true;
      try
      {
        _multipleNumberMode.Checked = !_s.MultipleUseDistance;
        _multipleDistanceMode.Checked = _s.MultipleUseDistance;
        _multipleAutoCheck.Checked = _s.MultipleAuto;
        _multipleDistanceMode.ToolTip = _s.MultipleAuto
          ? "Use distance as the maximum curvature-aware spacing"
          : "Use distance as the minimum uniform spacing";
      }
      finally { _updatingMultipleControls = false; }
    }

    void AttachMultipleInputPreviewFocus(Control control)
    {
      control.GotFocus += (_, __) =>
      {
        _multipleFocusedInputs.Add(control);
        RefreshMultiplePreview();
      };
      control.LostFocus += (_, __) =>
      {
        _multipleFocusedInputs.Remove(control);
        RefreshMultiplePreview();
      };
    }

    void RefreshMultiplePreview()
    {
      bool requested = !_viewportPointerActive &&
                       (_multipleSectionHovered || _multipleFocusedInputs.Count > 0);
      if (!requested)
      {
        ClearMultiplePreview();
        return;
      }

      var plans = ComputeMultiplePlacementPlans(_s.Doc, _s);
      _s.MultipleHoverPlans = plans;
      _s.MultipleHoverPreviewActive = plans != null;
      Redraw();
    }

    public void SetViewportPointerActive()
    {
      if (_viewportPointerActive)
        return;

      _viewportPointerActive = true;
      ApplyCurveIdentityHighlights(_viewportCurveHoverRowIndex);
      RefreshMultiplePreview();
    }

    void ClearMultiplePreview()
    {
      bool redraw = _s.MultipleHoverPreviewActive ||
                    _s.MultipleHoverPlans != null;
      _s.MultipleHoverPreviewActive = false;
      _s.MultipleHoverPlans = null;
      if (redraw)
        Redraw();
    }

    void UpdateLabelSizeEnabled()
    {
      bool auto = _labelSizeAutoCheck.Checked == true;
      _labelSizeStepper.Enabled    = !auto;
      _labelSizePctStepper.Enabled = auto;
    }

    void ApplyMultipleNumber()
    {
      UpdateMultipleModeIndicator();
      int number = Math.Clamp(_s.MultipleNumber, 1, 10000);
      bool valid = TryGetMultipleBaseAvailable(
        EffectiveMultipleStartOffset(_s), EffectiveMultipleEndOffset(_s), out double available);
      int intervalCount = number + 1 -
        (_s.MultipleStartOffsetEnabled ? 1 : 0) -
        (_s.MultipleEndOffsetEnabled ? 1 : 0);
      double exactDistance = valid && intervalCount > 0 ? available / intervalCount : 0.0;
      _s.MultipleDistance = exactDistance;

      _updatingMultipleControls = true;
      try
      {
        _multipleNumberStepper.Value = number;
        _multipleDistanceStepper.Value = RoundPanelNumber(exactDistance);
      }
      finally { _updatingMultipleControls = false; }
      RefreshMultiplePreview();
    }

    void ApplySelectedMultipleMode()
    {
      if (_s.MultipleUseDistance)
        ApplyMultipleDistance(_multipleDistanceStepper.Value, persist: false);
      else
        ApplyMultipleNumber();
      Persist();
    }

    bool TryGetMultipleBaseAvailable(double startOffset, double endOffset,
      out double available)
    {
      available = 0.0;
      if (!TryGetMultipleBaseCurveLength(out double baseLength))
        return false;
      available = baseLength - startOffset - endOffset;
      return available > _s.Doc.ModelAbsoluteTolerance;
    }

    bool TryGetMultipleBaseCurveLength(out double baseLength)
    {
      baseLength = 0.0;
      var active = Enumerable.Range(0, _s.Curves.Count)
        .Where(i => i >= _s.CurveEnabled.Length || _s.CurveEnabled[i])
        .ToList();
      if (active.Count == 0)
        return false;

      int baseCurveIndex = active.OrderBy(i => PlacementCurveLength(_s, i)).First();
      baseLength = PlacementCurveLength(_s, baseCurveIndex);
      return baseLength > _s.Doc.ModelAbsoluteTolerance;
    }

    void ApplyMultipleDistance(double requestedDistance, bool persist = true)
    {
      double distance = Math.Max(0.0, RoundPanelNumber(requestedDistance));
      _s.MultipleUseDistance = true;
      _s.MultipleDistance = distance;
      UpdateMultipleModeIndicator();

      if (distance <= _s.Doc.ModelAbsoluteTolerance ||
          !TryGetMultipleBaseAvailable(
            EffectiveMultipleStartOffset(_s), EffectiveMultipleEndOffset(_s), out double available))
      {
        if (persist)
          Persist();
        RefreshMultiplePreview();
        return;
      }

      int notchCount = _s.MultipleAuto
        ? ComputeMultiplePositions(_s.Doc, _s)?.Count ?? 0
        : BuildMultipleRatios(
            available, distance, _s.Doc.ModelAbsoluteTolerance,
            _s.MultipleStartOffsetEnabled, _s.MultipleEndOffsetEnabled).Count;
      _s.MultipleNumber = Math.Clamp(notchCount, 1, 10000);

      _updatingMultipleControls = true;
      try
      {
        _multipleDistanceStepper.Value = distance;
        _multipleNumberStepper.Value = _s.MultipleNumber;
      }
      finally { _updatingMultipleControls = false; }

      if (persist)
        Persist();
      RefreshMultiplePreview();
    }

    void ApplyCurveLengthHighlights()
    {
      foreach (var label in _curveLengthDifferenceLabels.Concat(_curveTotalDifferenceLabels))
        if (label != null)
          label.Text = "";

      double tolerance = ModelUnitsFromInches(
        _s.Doc, CurveLengthDifferenceToleranceInches);
      var logicalLengths = Enumerable.Range(0, _s.Curves.Count)
        .Select(index => PlacementCurveLength(_s, index))
        .ToArray();
      double shortest = logicalLengths.Length > 0 ? logicalLengths.Min() : 0.0;
      double longest = logicalLengths.Length > 0 ? logicalLengths.Max() : 0.0;
      double span = longest - shortest;
      bool significantDifference = logicalLengths.Length > 1 && span > tolerance;
      ApplyPercentLengthWarning(significantDifference);

      if (significantDifference)
      {
        double endpointTolerance = Math.Max(
          _s.Doc.ModelAbsoluteTolerance, span * 1.0e-9);
        for (int curveIndex = 0; curveIndex < logicalLengths.Length; curveIndex++)
        {
          int rowIndex = Array.FindIndex(
            _curveRows, row => row.LogicalIndex == curveIndex);
          if (rowIndex < 0)
            continue;
          Label? difference = _curveTotalDifferenceLabels[rowIndex] ??
            _curveLengthDifferenceLabels[rowIndex];
          if (difference == null)
            continue;
          if (Math.Abs(logicalLengths[curveIndex] - longest) <= endpointTolerance)
          {
            difference.Text = $"(+{FormatPanelNumber(span)})";
            difference.TextColor = CurveLengthLongerColor;
          }
          else if (Math.Abs(logicalLengths[curveIndex] - shortest) <= endpointTolerance)
          {
            difference.Text = $"(-{FormatPanelNumber(span)})";
            difference.TextColor = CurveLengthShorterColor;
          }
        }
      }

      var totalValues = Enumerable.Range(0, _curveTotalLabels.Length)
        .Where(index => _curveTotalLabels[index] != null)
        .Select(index => LinkedSequenceTotalForRow(index)!.Value)
        .ToArray();
      var allLengths = _curveRows.Select(row => row.Curve.GetLength())
        .Concat(totalValues)
        .OrderBy(length => length)
        .ToArray();
      if (allLengths.Length < 2)
        return;

      var groupStarts = new List<double>();
      foreach (double length in allLengths)
      {
        int groupIndex = groupStarts.Count - 1;
        if (groupIndex < 0 || length - groupStarts[groupIndex] > tolerance)
          groupStarts.Add(length);
      }

      if (groupStarts.Count < 2)
        return;

      int ColorGroup(double length)
      {
        for (int groupIndex = 0; groupIndex < groupStarts.Count; groupIndex++)
          if (length - groupStarts[groupIndex] <= tolerance)
            return groupIndex;
        return groupStarts.Count - 1;
      }

      for (int i = 0; i < _curveLengthLabels.Length; i++)
      {
        int groupIndex = ColorGroup(_curveRows[i].Curve.GetLength());
        _curveLengthBadges[i].BackgroundColor =
          CurveLengthGroupBackgrounds[groupIndex % CurveLengthGroupBackgrounds.Length];
        _curveLengthLabels[i].TextColor = CurveLengthGroupForeground;
        if (_curveTotalLabels[i] == null || _curveTotalBadges[i] == null)
          continue;
        double totalLength = LinkedSequenceTotalForRow(i)!.Value;
        int totalGroupIndex = ColorGroup(totalLength);
        _curveTotalBadges[i]!.BackgroundColor =
          CurveLengthGroupBackgrounds[totalGroupIndex % CurveLengthGroupBackgrounds.Length];
        _curveTotalLabels[i]!.TextColor = CurveLengthGroupForeground;
      }
    }

    void ApplyPercentLengthWarning(bool active)
    {
      bool warning = active && _percentCheck.Checked != true;
      _percentCheck.BackgroundColor = warning
        ? PercentLengthWarningBackground
        : _percentDefaultBackgroundColor;
      _percentCheck.TextColor = warning
        ? PercentLengthWarningForeground
        : _percentDefaultTextColor;
      _percentCheck.ToolTip = warning
        ? "Curve lengths differ significantly; enable Percent to align relative positions"
        : "Use the same relative position on curves of different lengths";
    }

    public void SyncFromSession()
    {
      _suppress = true;
      try
      {
        RefreshNotchTypeIcons();
        _lengthStepper.Value            = _s.NotchLengthOpt.CurrentValue;
        _offsetStepper.Value            = _s.NotchOffsetOpt.CurrentValue;
        _widthStepper.Value             = _s.NotchWidthOpt.CurrentValue;
        _percentCheck.Checked          = _s.PercentToggle.CurrentValue;
        _groupCheck.Checked            = _s.GroupToggle.CurrentValue;
        if (_keepSelectionCheck != null)
          _keepSelectionCheck.IsChecked = _s.KeepCurveSelection;
        _notchCheck.Checked            = _s.NotchToggle.CurrentValue;
        if (_notchHeaderCheck != null)
          _notchHeaderCheck.IsChecked = _s.NotchToggle.CurrentValue;
        _labelCheck.Checked            = _s.LabelToggle.CurrentValue;
        if (_labelHeaderCheck != null)
          _labelHeaderCheck.IsChecked = _s.LabelToggle.CurrentValue;
        _labelValueBox.Text            = _s.LabelValueText;
        _labelSizeStepper.Value        = _s.ManualLabelSize;
        _labelSizeAutoCheck.Checked    = _s.LabelSizeAutoToggle.CurrentValue;
        UpdateLabelSizeEnabled();
        _labelSizePctStepper.Value     = _s.LabelSizePctValues[Math.Max(0, _s.LabelSizePctIndex)];
        _labelOffsetStepper.Value      = _s.LabelOffsetOpt.CurrentValue;
        _labelOffsetYStepper.Value     = _s.LabelOffsetYOpt.CurrentValue;
        _autoAdvCheck.Checked          = _s.LabelAutoAdv;
        _sideFlipCheck.Checked         = _s.LabelSideFlip;
        _multipleStartOffsetStepper.Value = _s.MultipleStartOffset;
        _multipleEndOffsetStepper.Value   = _s.MultipleEndOffset;
        _multipleStartOffsetCheck.Checked = _s.MultipleStartOffsetEnabled;
        _multipleEndOffsetCheck.Checked   = _s.MultipleEndOffsetEnabled;
        _multipleNumberStepper.Value      = _s.MultipleNumber;
        _multipleDistanceStepper.Value    = RoundPanelNumber(_s.MultipleDistance);
        _multipleCurvatureSensitivityStepper.Value =
          RoundPanelNumber(_s.MultipleCurvatureSensitivity);
        if (_multipleSeparateCheck != null)
          _multipleSeparateCheck.IsChecked = _s.MultipleSeparate;
        UpdateMultipleModeIndicator();
        for (int i = 0; i < _sideButtons.Length; i++)
          if (i < _curveRows.Length && _curveRows[i].LogicalIndex < _s.CurveSides.Length)
          {
            UpdateCurveSideButton(
              i,
              _s.CurveSideBySource.GetValueOrDefault(
                _curveRows[i].SourceId,
                _s.CurveSides[_curveRows[i].LogicalIndex]));
            UpdateCurveReverseButton(
              i,
              _s.CurveReversedBySource.GetValueOrDefault(_curveRows[i].SourceId));
          }
        if (_s.Curves.Count > 1)
          for (int i = 0; i < _enableChecks.Length; i++)
            if (_enableChecks[i] != null && i < _curveRows.Length &&
                _curveRows[i].LogicalIndex < _s.CurveEnabled.Length)
              _enableChecks[i].Checked = _s.CurveEnabled[_curveRows[i].LogicalIndex];
        ApplyCurveLengthHighlights();
        ApplyDynamic();
        UpdateUndoEnabled();
      }
      finally { _suppress = false; }
    }

    public void CommitPendingValues()
    {
      if (_suppress) return;
      _s.NotchLengthOpt.CurrentValue = RoundPanelNumber(_lengthStepper.Value);
      _s.NotchOffsetOpt.CurrentValue = RoundPanelNumber(_offsetStepper.Value);
      _s.NotchWidthOpt.CurrentValue = RoundPanelNumber(_widthStepper.Value);
      _s.NotchLayerName = LayerSelector.GetDropDownValue(
        _notchLayerDrop, _s.NotchLayerName);
      _s.NotchToggle.CurrentValue = _notchCheck.Checked == true;
      _s.PercentToggle.CurrentValue = _percentCheck.Checked == true;
      _s.GroupToggle.CurrentValue = _groupCheck.Checked == true;
      _s.LabelToggle.CurrentValue = _labelCheck.Checked == true;
      if (!_s.NotchToggle.CurrentValue && !_s.LabelToggle.CurrentValue)
        _s.NotchToggle.CurrentValue = true;
      _s.LabelValueText = _labelValueBox.Text;
      _s.LabelAutoAdv = _autoAdvCheck.Checked == true;
      _s.LabelSideFlip = _sideFlipCheck.Checked == true;
      _s.LabelLayerName = LayerSelector.GetDropDownValue(
        _labelLayerDrop, _s.LabelLayerName);
      _s.ManualLabelSize = Math.Max(0, RoundPanelNumber(_labelSizeStepper.Value));
      _s.LabelSizeAutoToggle.CurrentValue = _labelSizeAutoCheck.Checked == true;
      int labelPct = Math.Clamp((int)Math.Round(_labelSizePctStepper.Value / 5.0) * 5, 20, 100);
      _s.LabelSizePctIndex = Array.IndexOf(_s.LabelSizePctValues, labelPct);
      if (_s.LabelSizePctIndex < 0) _s.LabelSizePctIndex = 0;
      _s.LabelOffsetOpt.CurrentValue = RoundPanelNumber(_labelOffsetStepper.Value);
      _s.LabelOffsetYOpt.CurrentValue = RoundPanelNumber(_labelOffsetYStepper.Value);
      _s.MultipleStartOffset = RoundPanelNumber(_multipleStartOffsetStepper.Value);
      _s.MultipleEndOffset = RoundPanelNumber(_multipleEndOffsetStepper.Value);
      _s.MultipleStartOffsetEnabled = _multipleStartOffsetCheck.Checked == true;
      _s.MultipleEndOffsetEnabled = _multipleEndOffsetCheck.Checked == true;
      _s.MultipleNumber = Math.Clamp((int)Math.Round(_multipleNumberStepper.Value), 1, 10000);
      _s.MultipleAuto = _multipleAutoCheck.Checked == true;
      _s.MultipleCurvatureSensitivity = Math.Clamp(
        (int)Math.Round(_multipleCurvatureSensitivityStepper.Value), 0, 1000);
      if (_multipleSeparateCheck != null)
        _s.MultipleSeparate = _multipleSeparateCheck.IsChecked == true;
      if (_s.MultipleAuto || _s.MultipleUseDistance)
        _s.MultipleDistance = RoundPanelNumber(_multipleDistanceStepper.Value);
    }

    void Persist()
    {
      CommitPendingValues();
      SaveOptions(_s);
    }

    public void UpdateDistanceLabels(
      double? current, double? prevDelta, double? otherEnd,
      double? segmentCurrent, double? segmentPrevDelta, double? segmentOtherEnd)
    {
      _fromStartLbl.Text = current.HasValue ? FormatPanelNumber(current.Value) : "-";
      _fromEndLbl.Text = otherEnd.HasValue ? FormatPanelNumber(otherEnd.Value) : "-";
      _fromPrevLbl.Text = prevDelta.HasValue ? FormatPanelNumber(prevDelta.Value) : "-";
      _segmentStartLbl.Text = FormatSegmentDistance(segmentCurrent);
      _segmentEndLbl.Text = FormatSegmentDistance(segmentOtherEnd);
      _segmentPrevLbl.Text = FormatSegmentDistance(segmentPrevDelta);
    }

    static string FormatSegmentDistance(double? value) =>
      value.HasValue ? $"({FormatPanelNumber(value.Value)})" : "";

    public void UpdateUndoEnabled()
    {
      _undoBtn.Enabled = _s.NotchRecords.Count > 0;
      _redoBtn.Enabled = _s.RedoBatches.Count > 0;
    }
  }
}
