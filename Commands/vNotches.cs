namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Diagnostics;
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


  /// <summary>
  /// Places notches (I, V, open-V, U, and T shapes) on one or more curves with an interactive live panel.
  /// </summary>
  public sealed partial class vNotches : vToolsCommand
  {
    // Window persistence defaults
    const string WindowXKey = "window_x"; // Saved horizontal window position in logical screen pixels; absent uses normal startup placement.
    const string WindowYKey = "window_y"; // Saved vertical window position in logical screen pixels; absent uses normal startup placement.
    // Next-label control defaults
    const int NextLabelButtonSize = 20; // Square icon button size in device-independent pixels; smaller than the 22-pixel label input height.
    const int LabelPrefixLayoutAllowance = 48; // Extra minimum panel width in device-independent pixels for the additional input; actual field minimum is measured from three wide letters.
    const string LabelValueWidthSample = "WWW"; // Three widest Latin capitals used to size the value input at the current UI font.
    const int LabelTextCaretWidth = 1; // Device-independent pixel reserved for the insertion caret after three wide letters.
    const int LabelTextFallbackInsets = 4; // Fallback text-input border/padding width in device-independent pixels when native metrics are unavailable.
    const int LabelInputSpacing = 4; // Horizontal separation between prefix and value inputs, in device-independent pixels.
    const int NextLabelIconSize = 12; // Icon canvas size in device-independent pixels, smaller than its button.
    const double NextLabelIconStroke = 1.3; // Icon stroke thickness in device-independent pixels; positive value.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.NextLabelButtonPadding", "Native icon-button padding on each side in device-independent pixels; zero or greater.")] const double NextLabelButtonPadding = 1.0; // Native icon-button padding on each side in device-independent pixels; zero or greater.

     static readonly double ConfiguredNextLabelButtonPadding = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.NextLabelButtonPadding", NextLabelButtonPadding); // Resolved once from advanced defaults; restart Rhino after changes.
    const string NextLabelIconPath = "M1,6 L8,6 M5,3 L8,6 L5,9 M10.5,2 L10.5,10"; // Forward-to-next icon coordinates on the 12-by-12 canvas.
    const string NextLabelIconFallback = "\u21E5"; // Forward-to-next glyph used if the native vector icon is unavailable.
    [CommandDefault("vNotches","vTools.Commands.vNotches.EditToolButtonSize","Square edit-toolbar button size in device-independent pixels; at least 20.")]
    const int EditToolButtonSize = 26; // Stable edit-toolbar button width and height in device-independent pixels.
    static readonly int ConfiguredEditToolButtonSize=Math.Max(20,CommandDefaults.Get("vNotches","vTools.Commands.vNotches.EditToolButtonSize",EditToolButtonSize)); // Resolved once through vSettings; restart Rhino after changing.
    [CommandDefault("vNotches","vTools.Commands.vNotches.EditToolIconSize","Edit-toolbar icon size in device-independent pixels; positive and smaller than its button.")]
    const double EditToolIconSize = 14; // Glyph height in device-independent pixels.
    static readonly double ConfiguredEditToolIconSize=Math.Clamp(CommandDefaults.Get("vNotches","vTools.Commands.vNotches.EditToolIconSize",EditToolIconSize),1,ConfiguredEditToolButtonSize-4); // Resolved once through vSettings; restart Rhino after changing.
    const string EditToolFont = "Segoe MDL2 Assets"; // Windows system icon font, explicitly selected for private-use glyphs.
    const string AssignSourceToolGlyph="\uE8E7"; // System link-to-source glyph for explicit curve assignment during notch repair.
    const string EditToolGlyph="\uE70F",AddToolGlyph="\uE710",ApplyToolGlyph="\uE73E",LinkToolGlyph="\uE71B",SelectToolGlyph="\uE8B3",UnlinkToolGlyph="\uE8C6",RepairToolGlyph="\uE90F"; // System glyphs: edit, add, check, link, select matches, cut association and repair.
    // ── Constants ────────────────────────────────────────────────────────────

    // Existing-notch editing defaults
    [CommandDefault("vNotches","vTools.Commands.vNotches.EditPickRadiusPixels","Screen-pixel radius for picking an existing notch, independent of zoom; positive.")]
    const int EditPickRadiusPixels=12; // Positive screen-pixel acquisition radius around notch geometry and its marker.
    static readonly int ConfiguredEditPickRadiusPixels=Math.Max(1,CommandDefaults.Get(Section,"vTools.Commands.vNotches.EditPickRadiusPixels",EditPickRadiusPixels)); // Resolved once through vSettings.
    [CommandDefault("vNotches","vTools.Commands.vNotches.EditMarkerPixels","Screen-pixel radius for active and hovered notch rings; positive.")]
    const int EditMarkerPixels=4; // Positive fixed-size active/hover marker radius, independent of model zoom.
    static readonly int ConfiguredEditMarkerPixels=Math.Max(1,CommandDefaults.Get(Section,"vTools.Commands.vNotches.EditMarkerPixels",EditMarkerPixels)); // Resolved once through vSettings.
    [CommandDefault("vNotches","vTools.Commands.vNotches.EditPickSamples","Polyline samples for picking curved notches; at least two.")]
    const int EditPickSamples=16; // Curved-notch sampling divisions; exact polyline vertices are preferred.
    static readonly int ConfiguredEditPickSamples=Math.Max(2,CommandDefaults.Get(Section,"vTools.Commands.vNotches.EditPickSamples",EditPickSamples)); // Resolved once through vSettings.
    [CommandDefault("vNotches","vTools.Commands.vNotches.MovePositionTextPixels","Moving-notch position text height in screen pixels; positive.")]
    const int MovePositionTextPixels=12; // Screen-pixel height of the start/end/displacement readout.
    static readonly int ConfiguredMovePositionTextPixels=Math.Max(1,CommandDefaults.Get(Section,"vTools.Commands.vNotches.MovePositionTextPixels",MovePositionTextPixels)); // Resolved once through vSettings.
    [CommandDefault("vNotches","vTools.Commands.vNotches.MovePositionOffsetPixels","Screen-pixel separation between the moving notch and its position readout; nonnegative.")]
    const int MovePositionOffsetPixels=18; // Nonnegative text offset in screen pixels.
    static readonly int ConfiguredMovePositionOffsetPixels=Math.Max(0,CommandDefaults.Get(Section,"vTools.Commands.vNotches.MovePositionOffsetPixels",MovePositionOffsetPixels)); // Resolved once through vSettings.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.ActiveNotchStyle","Currently edited notch","#FF32CD32","#FF000000","#FF32CD32",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle ActiveNotchStyle=CommandDefaults.Highlight(Section,"vTools.Commands.vNotches.ActiveNotchStyle",new(System.Drawing.Color.LimeGreen,System.Drawing.Color.Black,System.Drawing.Color.LimeGreen,1)); // Green geometry and a small fixed-size ring identify the active physical notch.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.HoverNotchStyle","Hovered notch","#FFFFD700","#FF000000","#FFFFD700",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle HoverNotchStyle=CommandDefaults.Highlight(Section,"vTools.Commands.vNotches.HoverNotchStyle",new(System.Drawing.Color.Gold,System.Drawing.Color.Black,System.Drawing.Color.Gold,1)); // Gold hover feedback remains distinct from the active green notch.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.MatchedNotchStyle","Other matching notches","#FF1E90FF","#FF000000","#FF1E90FF",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle MatchedNotchStyle=CommandDefaults.Highlight(Section,"vTools.Commands.vNotches.MatchedNotchStyle",new(System.Drawing.Color.DodgerBlue,System.Drawing.Color.Black,System.Drawing.Color.DodgerBlue,1)); // Blue identifies the other physical notches in the active matched set.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.AssignedCurveStyle","Assigned notch source curves","#FF00CED1","#FF000000","#FF00CED1",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle AssignedCurveStyle=CommandDefaults.Highlight(Section,"vTools.Commands.vNotches.AssignedCurveStyle",new(System.Drawing.Color.DarkTurquoise,System.Drawing.Color.Black,System.Drawing.Color.DarkTurquoise,1)); // Cyan highlights every source segment assigned to the edit selection.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.HoveredSourceStyle","Source curve under a list row","#FFFFD700","#FF000000","#FF000000",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle HoveredSourceStyle=CommandDefaults.Highlight(Section,"vTools.Commands.vNotches.HoveredSourceStyle",new(System.Drawing.Color.Gold,System.Drawing.Color.Black,System.Drawing.Color.Black,1)); // Gold overrides the cyan assigned-source highlight while a row is hovered.
    const string SourceStartCaption="Start",SourceEndCaption="End"; // Endpoint captions for the hovered logical curve direction; drawing geometry is not changed.
    [CommandDefault("vNotches","vTools.Commands.vNotches.LinkActiveBackground","Active picker-button background in ARGB HEX; used for Link and source/curve picking.","#FFFFD700")]
    static readonly System.Drawing.Color LinkActiveBackground=CommandDefaults.GetColor(Section,"vTools.Commands.vNotches.LinkActiveBackground",System.Drawing.Color.Gold); // ARGB background identifying active picker modes, not the add/edit switch.
    [CommandDefault("vNotches","vTools.Commands.vNotches.LinkActiveForeground","Active mode-button icon/text color in ARGB HEX.","#FF000000")]
    static readonly System.Drawing.Color LinkActiveForeground=CommandDefaults.GetColor(Section,"vTools.Commands.vNotches.LinkActiveForeground",System.Drawing.Color.Black); // ARGB icon color contrasting with the active background.
    [CommandDefault("vNotches","vTools.Commands.vNotches.LinkActiveBorder","Active mode-button border color in ARGB HEX.","#FF9A6700")]
    static readonly System.Drawing.Color LinkActiveBorder=CommandDefaults.GetColor(Section,"vTools.Commands.vNotches.LinkActiveBorder",System.Drawing.Color.FromArgb(154,103,0)); // ARGB outline of active mode buttons.
    [CommandDefault("vNotches","vTools.Commands.vNotches.MovePositionTextColor","Moving-notch readout text color in ARGB HEX.","#FFFFFFFF")]
    static readonly System.Drawing.Color MovePositionTextColor=CommandDefaults.GetColor(Section,"vTools.Commands.vNotches.MovePositionTextColor",System.Drawing.Color.White); // Foreground color for model-unit position feedback.
    [CommandDefault("vNotches","vTools.Commands.vNotches.MovePositionHaloColor","Moving-notch readout shadow color in ARGB HEX.","#FF000000")]
    static readonly System.Drawing.Color MovePositionHaloColor=CommandDefaults.GetColor(Section,"vTools.Commands.vNotches.MovePositionHaloColor",System.Drawing.Color.Black); // One-pixel text shadow for contrast over the drawing.
    [CommandDefault("vNotches","vTools.Commands.vNotches.MoveGuideColor","Free-cursor projection guide color in ARGB HEX, including transparency.","#80FFFFFF")]
    static readonly System.Drawing.Color MoveGuideColor=CommandDefaults.GetColor(Section,"vTools.Commands.vNotches.MoveGuideColor",System.Drawing.Color.FromArgb(128,255,255,255)); // ARGB color of the thin dotted line between the free cursor and the notch on its source.

    const string Section            = "vNotches";
    [CommandDefault("vNotches","vTools.Commands.vNotches.DefaultAssociations","true applies new notches to verified nested copies and highlights their curves; false affects listed curves only.",OptionKeys="associations")]
    const bool DefaultAssociations=true; // true includes verified nest counterparts without extra rows; false uses only the source list.
    [CommandDefault("vNotches","vTools.Commands.vNotches.DefaultOppositeMatches","true links new matching curve families with opposite start/end directions; false keeps their stored directions. Geometry is not reversed.",OptionKeys="opposite_matches")]
    const bool DefaultOppositeMatches=true; // true makes newly linked mates oppose the first curve; false retains existing directions.
    const string DocumentSettingsSection = "vTools"; // Rhino document-string section used for per-document command settings.
    const string DocumentSettingsEntry = "vNotches"; // Rhino document-string entry containing serialized notch and label settings.
    const string SpecialLayerCurrent = MatchingNotches.CurrentLayer; // Layer-selector sentinel that resolves output to Rhino's current layer.
    const string NotchDataPrefix    = MatchingNotches.Prefix; // Prefix for user-data keys written to notch output objects.
    const string NotchDataVersion   = MatchingNotches.Version; // Serialized notch metadata schema version.
    const string OpenVNotchType     = "\\/"; // Stored and displayed code for the Open Vee notch type.
    const string NotchObjectName = "Notch"; // Rhino object name assigned to every created notch component.
    const string OffsetSplitReferenceLayer = "Reference"; // Rhino layer path assigned only to the segment between V/U notch legs.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.OffsetSplitContactToleranceScale", "Multiplier on document absolute tolerance for matching each leg endpoint and any shared curve endpoints.")] const double OffsetSplitContactToleranceScale = 2.0; // Multiplier on document absolute tolerance for matching each leg endpoint and any shared curve endpoints.

     static readonly double ConfiguredOffsetSplitContactToleranceScale = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.OffsetSplitContactToleranceScale", OffsetSplitContactToleranceScale); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.OffsetSplitZeroToleranceScale", "Minimum contact tolerance in Rhino zero-tolerance units.")] const double OffsetSplitZeroToleranceScale = 10.0; // Minimum contact tolerance in Rhino zero-tolerance units.

     static readonly double ConfiguredOffsetSplitZeroToleranceScale = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.OffsetSplitZeroToleranceScale", OffsetSplitZeroToleranceScale); // Resolved once from advanced defaults; restart Rhino after changes.
    const string NotchLabelObjectName = "NotchLabel"; // Rhino object name assigned to every created notch label.
    const string NotchComponentSetKey = NotchDataPrefix + "component_set"; // Metadata key linking one notch's output components.
    const string NotchMatchSetKey = MatchingNotches.MatchSetKey; // GUID linking corresponding output from one placement across curves.
    const string NotchMatchMemberKey = MatchingNotches.MatchMemberKey; // Output object's own GUID; copied metadata cannot link an unrelated duplicate to the original set.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.RemovalModifierPollSeconds", "UI-thread interval checking Ctrl changes without requiring cursor motion.")] const double RemovalModifierPollSeconds = 0.05; // UI-thread interval checking Ctrl changes without requiring cursor motion.

     static readonly double ConfiguredRemovalModifierPollSeconds = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.RemovalModifierPollSeconds", RemovalModifierPollSeconds); // Resolved once from advanced defaults; restart Rhino after changes.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.RemovalHighlightStyle","Notch removal","#FFFF0000","#FF8B0000","#FF8B0000",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle RemovalHighlightStyle = CommandDefaults.Highlight("vNotches","vTools.Commands.vNotches.RemovalHighlightStyle",new(
      System.Drawing.Color.Red, System.Drawing.Color.DarkRed, System.Drawing.Color.DarkRed, 1.0)); // Red removal feedback, with no shaded-body allocation.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.MoveHighlightStyle","Notch movement and matching","#FF00BFFF","#FF00BFFF","#FF00BFFF",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle MoveHighlightStyle = CommandDefaults.Highlight("vNotches","vTools.Commands.vNotches.MoveHighlightStyle",new(
      System.Drawing.Color.DeepSkyBlue,System.Drawing.Color.DeepSkyBlue,System.Drawing.Color.DeepSkyBlue,1.0)); // Cyan hover/move feedback; true geometry colors remain unchanged.
    [CommandHighlightDefault("vNotches","vTools.Commands.vNotches.UnpairedHighlightStyle","Notch without pairing data","#FFFF8C00","#FFFF8C00","#FFFF8C00",0)]
    static readonly PreviewDisplay.ObjectHighlightStyle UnpairedHighlightStyle = CommandDefaults.Highlight("vNotches","vTools.Commands.vNotches.UnpairedHighlightStyle",new(
      System.Drawing.Color.DarkOrange,System.Drawing.Color.DarkOrange,System.Drawing.Color.DarkOrange,1.0)); // Orange legacy/copied-notch warning; no guessed matches are moved.
    [CommandDefault("vNotches","vTools.Commands.vNotches.DefaultHighlightExisting","true highlights existing notches in edit mode at every zoom; false leaves only hover/active feedback.",OptionKeys="highlight_existing")]
    const bool DefaultHighlightExisting = true; // true keeps existing notches visible in edit mode; false shows only active/hover highlights.
    [CommandDefault("vNotches","vTools.Commands.vNotches.ExistingNotchMarkerPixels","Positive screen-pixel radius for existing notches too small to see at the current zoom.")]
    const int ExistingNotchMarkerPixels = 4; // Positive screen-pixel radius of the small-notch visibility cue.
    static readonly int ConfiguredExistingNotchMarkerPixels=CommandDefaults.Get(Section,"vTools.Commands.vNotches.ExistingNotchMarkerPixels",ExistingNotchMarkerPixels);
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelOffIn", "Label offset in inches before document-unit conversion; zero or greater.")] const double DefaultLabelOffIn  = 0.1; // Label offset in inches before document-unit conversion; zero or greater.

     static readonly double ConfiguredDefaultLabelOffIn = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelOffIn", DefaultLabelOffIn); // Resolved once from advanced defaults; restart Rhino after changes.
    const string LabelOffsetXToolTip = "Minimum gap along the curve between the label and notch geometry, in model units; used when notches are enabled."; // Label Offset X caption and input help.
    const string LabelOffsetYToolTip = "Move the label perpendicular to the curve, in model units; positive moves toward the selected notch side, negative away."; // Label Offset Y caption and input help.
    const string LabelDirectionSettingsKey = "label_toward_start"; // Boolean setting: true prefers labels toward the curve start, false away from it.

    // ── Persisted defaults ───────────────────────────────────────────────────

    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchLength", "Notch depth in model units; greater than zero.", OptionKeys = "notch_length")] const double DefaultNotchLength = MatchingNotches.DefaultLength; // Notch depth in model units; greater than zero.

     static readonly double ConfiguredDefaultNotchLength = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchLength", DefaultNotchLength); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchOffset", "Offset-curve distance in model units; zero or greater.", OptionKeys = "notch_offset")] const double DefaultNotchOffset = MatchingLabels.DefaultNotchOffset; // Offset-curve distance in model units; zero or greater.

     static readonly double ConfiguredDefaultNotchOffset = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchOffset", DefaultNotchOffset); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchWidth", "Notch width in model units; greater than zero.", OptionKeys = "notch_width")] const double DefaultNotchWidth = MatchingNotches.DefaultWidth; // Notch width in model units; greater than zero.

     static readonly double ConfiguredDefaultNotchWidth = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchWidth", DefaultNotchWidth); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchType", "Notch code: I, V, \\/, U, or T.", OptionKeys = "notch_type")] const string DefaultNotchType = MatchingNotches.DefaultType; // Notch code: I, V, \/, U, or T.

     static readonly string ConfiguredDefaultNotchType = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchType", DefaultNotchType); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchEnabled", "true creates notch geometry; false creates labels only.", OptionKeys = "notch")] const bool DefaultNotchEnabled = true; // true creates notch geometry; false creates labels only.

     static readonly bool ConfiguredDefaultNotchEnabled = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchEnabled", DefaultNotchEnabled); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultBothSides", "true places matching geometry on both sides of each curve; false uses its selected side.", OptionKeys = "both_sides")] const bool DefaultBothSides = false; // true places matching geometry on both sides of each curve; false uses its selected side.

     static readonly bool ConfiguredDefaultBothSides = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultBothSides", DefaultBothSides); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchTrim", "No leaves offset curves alone; Split moves the between-leg piece; Trim removes it.", OptionKeys = "notch_trim")] const NotchTrimMode DefaultNotchTrim = NotchTrimMode.No; // No leaves offset curves alone; Split moves the between-leg piece; Trim removes it.

     static readonly NotchTrimMode ConfiguredDefaultNotchTrim = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchTrim", DefaultNotchTrim); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchTrimLayer", "Named layer for split between-leg sections; created only when used.", OptionKeys = "notch_trim_layer")] const string DefaultNotchTrimLayer = "Reference"; // Named layer for split between-leg sections; created only when used.

     static readonly string ConfiguredDefaultNotchTrimLayer = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchTrimLayer", DefaultNotchTrimLayer); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandChoicesAttribute("vNotches", "notch_trim", false, "No", "Split", "Trim")] static readonly string[] NotchTrimNames = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vNotches), nameof(NotchTrimNames)); // Command-line choice order matching NotchTrimMode.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultPercent", "true interprets placement as curve percentage; false uses model-unit distance.", OptionKeys = "percent")] const bool DefaultPercent = false; // true interprets placement as curve percentage; false uses model-unit distance.

     static readonly bool ConfiguredDefaultPercent = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultPercent", DefaultPercent); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultGroup", "true groups each result with its landed source; false preserves existing grouping only.", OptionKeys = "group")] const bool DefaultGroup = false; // true groups each result with its landed source; false preserves existing grouping only.

     static readonly bool ConfiguredDefaultGroup = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultGroup", DefaultGroup); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelEnabled", "true creates notch labels; false omits them.", OptionKeys = "label")] const bool DefaultLabelEnabled = false; // true creates notch labels; false omits them.

     static readonly bool ConfiguredDefaultLabelEnabled = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelEnabled", DefaultLabelEnabled); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelValue", "Plain label text.", OptionKeys = "label_value")] const string DefaultLabelValue = MatchingLabels.DefaultValue; // Plain label text shared with mating seam labels.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelPrefix", "Fixed literal notch-label prefix; empty omits it and only the separate value is incremented.", OptionKeys = "label_prefix")] const string DefaultLabelPrefix = MatchingLabels.DefaultPrefix; // Literal text, including intentional spaces; empty disables the prefix.
    static readonly string ConfiguredDefaultLabelPrefix = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelPrefix", DefaultLabelPrefix); // Resolved factory prefix; restart after advanced changes.

     static readonly string ConfiguredDefaultLabelValue = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelValue", DefaultLabelValue); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelSize", "Text height in model units; zero or greater.", OptionKeys = "label_size")] const double DefaultLabelSize = MatchingLabels.DefaultSize; // Shared text height in model units; zero or greater.

     static readonly double ConfiguredDefaultLabelSize = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelSize", DefaultLabelSize); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelSizeAuto", "true derives label size automatically; false uses the configured size.", OptionKeys = "label_size_auto")] const bool DefaultLabelSizeAuto = MatchingLabels.DefaultAutoSize; // true derives label size automatically; false uses the configured size.

     static readonly bool ConfiguredDefaultLabelSizeAuto = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelSizeAuto", DefaultLabelSizeAuto); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelSizePercent", "Auto-size percentage; 20 through 100 in five-point steps.", OptionKeys = "label_size_pct")] const int DefaultLabelSizePercent = MatchingLabels.DefaultSizePercent; // Auto-size percentage; 20 through 100 in five-point steps.

     static readonly int ConfiguredDefaultLabelSizePercent = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelSizePercent", DefaultLabelSizePercent); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultNotchLayer", "Rhino layer path or the current-layer sentinel.", OptionKeys = "notch_layer")] const string DefaultNotchLayer = SpecialLayerCurrent; // Rhino layer path or the current-layer sentinel.

     static readonly string ConfiguredDefaultNotchLayer = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultNotchLayer", DefaultNotchLayer); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelLayer", "Rhino layer name or full layer path.", OptionKeys = "label_layer")] const string DefaultLabelLayer = MatchingLabels.DefaultLayer; // Rhino layer name or full layer path.

     static readonly string ConfiguredDefaultLabelLayer = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelLayer", DefaultLabelLayer); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelOffset", "Model units; NaN requests DefaultLabelOffIn conversion.", OptionKeys = "label_offset")] const double DefaultLabelOffset = double.NaN; // Model units; NaN requests DefaultLabelOffIn conversion.

     static readonly double ConfiguredDefaultLabelOffset = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelOffset", DefaultLabelOffset); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelOffsetY", "Perpendicular label offset in model units.", OptionKeys = "label_offset_y")] const double DefaultLabelOffsetY = 0.0; // Perpendicular label offset in model units.

     static readonly double ConfiguredDefaultLabelOffsetY = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelOffsetY", DefaultLabelOffsetY); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultLabelSideFlip", "true prefers labels toward the curve start; false prefers them away from it, independent of notch side.", OptionKeys = "label_toward_start")] const bool DefaultLabelSideFlip = false; // true prefers labels toward the curve start; false prefers them away from it, independent of notch side.

     static readonly bool ConfiguredDefaultLabelSideFlip = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultLabelSideFlip", DefaultLabelSideFlip); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultKeepSelection", "true retains current curves while selecting; false replaces the selection.", OptionKeys = "keep_selection")] const bool DefaultKeepSelection = false; // true retains current curves while selecting; false replaces the selection.

     static readonly bool ConfiguredDefaultKeepSelection = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultKeepSelection", DefaultKeepSelection); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleStartOffset", "Start clearance in model units; zero or greater.", OptionKeys = "multiple_start_offset")] const double DefaultMultipleStartOffset = MatchingNotches.DefaultEndOffset; // Start clearance in model units; zero or greater.

     static readonly double ConfiguredDefaultMultipleStartOffset = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleStartOffset", DefaultMultipleStartOffset); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleEndOffset", "End clearance in model units; zero or greater.", OptionKeys = "multiple_end_offset")] const double DefaultMultipleEndOffset = MatchingNotches.DefaultEndOffset; // End clearance in model units; zero or greater.

     static readonly double ConfiguredDefaultMultipleEndOffset = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleEndOffset", DefaultMultipleEndOffset); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleStartOffsetEnabled", "true reserves the start offset; false excludes a start-end notch.", OptionKeys = "multiple_start_offset_enabled")] const bool DefaultMultipleStartOffsetEnabled = true; // true reserves the start offset; false excludes a start-end notch.

     static readonly bool ConfiguredDefaultMultipleStartOffsetEnabled = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleStartOffsetEnabled", DefaultMultipleStartOffsetEnabled); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleEndOffsetEnabled", "true reserves the end offset; false excludes an end-end notch.", OptionKeys = "multiple_end_offset_enabled")] const bool DefaultMultipleEndOffsetEnabled = true; // true reserves the end offset; false excludes an end-end notch.

     static readonly bool ConfiguredDefaultMultipleEndOffsetEnabled = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleEndOffsetEnabled", DefaultMultipleEndOffsetEnabled); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleNumber", "Number of notches; one through 10000.", OptionKeys = "multiple_number")] const int DefaultMultipleNumber = 2; // Number of notches; one through 10000.

     static readonly int ConfiguredDefaultMultipleNumber = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleNumber", DefaultMultipleNumber); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleDistance", "Minimum spacing in model units; zero or greater.", OptionKeys = "multiple_distance")] const double DefaultMultipleDistance = 0.0; // Minimum spacing in model units; zero or greater.

     static readonly double ConfiguredDefaultMultipleDistance = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleDistance", DefaultMultipleDistance); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleUseDistance", "true drives spacing by minimum distance; false drives it by notch count.", OptionKeys = "multiple_use_distance")] const bool DefaultMultipleUseDistance = false; // true drives spacing by minimum distance; false drives it by notch count.

     static readonly bool ConfiguredDefaultMultipleUseDistance = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleUseDistance", DefaultMultipleUseDistance); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleAuto", "true uses curvature-aware spacing with Distance as the maximum; false uses uniform spacing.", OptionKeys = "multiple_auto")] const bool DefaultMultipleAuto = false; // true uses curvature-aware spacing with Distance as the maximum; false uses uniform spacing.

     static readonly bool ConfiguredDefaultMultipleAuto = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleAuto", DefaultMultipleAuto); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleCurvatureSensitivity", "Whole-number curvature sensitivity from zero through 1000; ten preserves the original 1.0 curvature multiplier.", OptionKeys = "multiple_curvature_sensitivity")] const int DefaultMultipleCurvatureSensitivity = 10; // Whole-number curvature sensitivity from zero through 1000; ten preserves the original 1.0 curvature multiplier.

     static readonly int ConfiguredDefaultMultipleCurvatureSensitivity = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleCurvatureSensitivity", DefaultMultipleCurvatureSensitivity); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultMultipleSeparate", "true applies the requested multiple layout to each linked physical segment; false treats each linked sequence as one curve.", OptionKeys = "multiple_separate")] const bool DefaultMultipleSeparate = false; // true applies the requested multiple layout to each linked physical segment; false treats each linked sequence as one curve.

     static readonly bool ConfiguredDefaultMultipleSeparate = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultMultipleSeparate", DefaultMultipleSeparate); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleCurvatureSensitivityUnit", "Internal curvature multiplier represented by one whole-number Sensitivity step.")] const double MultipleCurvatureSensitivityUnit = 0.1; // Internal curvature multiplier represented by one whole-number Sensitivity step.

     static readonly double ConfiguredMultipleCurvatureSensitivityUnit = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleCurvatureSensitivityUnit", MultipleCurvatureSensitivityUnit); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleAutoMinimumSamples", "Minimum tangent samples used by curvature-aware spacing.")] const int MultipleAutoMinimumSamples = 64; // Minimum tangent samples used by curvature-aware spacing.

     static readonly int ConfiguredMultipleAutoMinimumSamples = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleAutoMinimumSamples", MultipleAutoMinimumSamples); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleAutoSamplesPerSpacing", "Tangent samples taken per maximum-distance interval.")] const int MultipleAutoSamplesPerSpacing = 16; // Tangent samples taken per maximum-distance interval.

     static readonly int ConfiguredMultipleAutoSamplesPerSpacing = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleAutoSamplesPerSpacing", MultipleAutoSamplesPerSpacing); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleAutoMaximumSamples", "Upper sampling limit protecting interactive preview performance.")] const int MultipleAutoMaximumSamples = 20000; // Upper sampling limit protecting interactive preview performance.

     static readonly int ConfiguredMultipleAutoMaximumSamples = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleAutoMaximumSamples", MultipleAutoMaximumSamples); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleAutoKinkSnapDistanceScale", "Fraction of maximum spacing within which a regular Auto station is replaced by a kink station.")] const double MultipleAutoKinkSnapDistanceScale = 0.5; // Fraction of maximum spacing within which a regular Auto station is replaced by a kink station.

     static readonly double ConfiguredMultipleAutoKinkSnapDistanceScale = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleAutoKinkSnapDistanceScale", MultipleAutoKinkSnapDistanceScale); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleAutoKinkMinimumAngleDegrees", "Minimum joined-segment tangent change treated as a kink candidate, in degrees.")] const double MultipleAutoKinkMinimumAngleDegrees = 1.0; // Minimum joined-segment tangent change treated as a kink candidate, in degrees.

     static readonly double ConfiguredMultipleAutoKinkMinimumAngleDegrees = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleAutoKinkMinimumAngleDegrees", MultipleAutoKinkMinimumAngleDegrees); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.MultipleExistingNotchClearanceScale", "Nonnegative existing-notch proximity multiplier: Number/Auto use notch width capped at half the local station spacing; Distance uses its requested minimum spacing.")] const double MultipleExistingNotchClearanceScale = 1.0; // Nonnegative multiplier of local notch-footprint clearance in Number/Auto, or requested spacing in Distance mode.

     static readonly double ConfiguredMultipleExistingNotchClearanceScale = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.MultipleExistingNotchClearanceScale", MultipleExistingNotchClearanceScale); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultWindowWidth", "Client width in device-independent pixels; 300 or greater.", OptionKeys = "window_width")] const int DefaultWindowWidth = 300; // Client width in device-independent pixels; 300 or greater.

     static readonly int ConfiguredDefaultWindowWidth = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultWindowWidth", DefaultWindowWidth); // Resolved once from advanced defaults; restart Rhino after changes.
    [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.DefaultWindowHeight", "Client height in device-independent pixels; zero auto-sizes to content.", OptionKeys = "window_height")] const int DefaultWindowHeight = 0; // Client height in device-independent pixels; zero auto-sizes to content.

     static readonly int ConfiguredDefaultWindowHeight = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.DefaultWindowHeight", DefaultWindowHeight); // Resolved once from advanced defaults; restart Rhino after changes.

    static double _notchLength = ConfiguredDefaultNotchLength;
    static double _notchOffset = ConfiguredDefaultNotchOffset;
    static double _notchWidth = ConfiguredDefaultNotchWidth;
    static string _notchType = ConfiguredDefaultNotchType;
    static bool _notch = ConfiguredDefaultNotchEnabled;
    static bool _bothSides = ConfiguredDefaultBothSides;
    static NotchTrimMode _notchTrim = ConfiguredDefaultNotchTrim;
    static string _notchTrimLayer = ConfiguredDefaultNotchTrimLayer;
    static bool _percent = ConfiguredDefaultPercent;
    static bool _group = ConfiguredDefaultGroup;
    static bool _associations=CommandDefaults.Get(Section,"vTools.Commands.vNotches.DefaultAssociations",DefaultAssociations);
    static bool _oppositeMatches=CommandDefaults.Get(Section,"vTools.Commands.vNotches.DefaultOppositeMatches",DefaultOppositeMatches);
    static bool _label = ConfiguredDefaultLabelEnabled;
    static string _labelValue = ConfiguredDefaultLabelValue;
    static string _labelPrefix = ConfiguredDefaultLabelPrefix;
    static double _labelSize = ConfiguredDefaultLabelSize;
    static bool _labelSizeAuto = ConfiguredDefaultLabelSizeAuto;
    static int _labelSizePct = ConfiguredDefaultLabelSizePercent;
    static string _notchLayer = ConfiguredDefaultNotchLayer;
    static string _labelLayer = ConfiguredDefaultLabelLayer;
    static double _labelOffset = ConfiguredDefaultLabelOffset; // resolved to model units on first load
    static double _labelOffsetY = ConfiguredDefaultLabelOffsetY;
    static bool _labelSideFlip = ConfiguredDefaultLabelSideFlip;
    static bool _keepSelection = ConfiguredDefaultKeepSelection;
    static double _multipleStartOffset = ConfiguredDefaultMultipleStartOffset;
    static double _multipleEndOffset = ConfiguredDefaultMultipleEndOffset;
    static bool _multipleStartOffsetEnabled = ConfiguredDefaultMultipleStartOffsetEnabled;
    static bool _multipleEndOffsetEnabled = ConfiguredDefaultMultipleEndOffsetEnabled;
    static int _multipleNumber = ConfiguredDefaultMultipleNumber;
    static double _multipleDistance = ConfiguredDefaultMultipleDistance;
    static bool _multipleUseDistance = ConfiguredDefaultMultipleUseDistance;
    static bool _multipleAuto = ConfiguredDefaultMultipleAuto;
    static int _multipleCurvatureSensitivity = ConfiguredDefaultMultipleCurvatureSensitivity;
    static bool _multipleSeparate = ConfiguredDefaultMultipleSeparate;
    static int _windowWidth = ConfiguredDefaultWindowWidth;
    static int _windowHeight = ConfiguredDefaultWindowHeight;
    static Eto.Drawing.Point? _windowLocation;
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
        if (session.RedoBatches.Count == 0 && session.RemovalRedo.Count == 0 && session.MoveRedo.Count == 0)
        {
          RhinoApp.WriteLine("vNotches: nothing to redo.");
          vTools.Log.Write("vNotches", $"{source} redo ignored: empty stack");
          return;
        }
        RedoLastNotch(doc, session);
      }
      else
      {
        if (session.NotchRecords.Count == 0 && session.Removals.Count == 0 && session.Moves.Count == 0)
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
      _notchTrim = ConfiguredDefaultNotchTrim;
      _notchTrimLayer = ConfiguredDefaultNotchTrimLayer;
      ToolsOptionStore.Read<int>(Section, s =>
      {
        ApplyStoredOptions(s, includeUiSettings: true);
        return 0;
      });
      LoadDocumentOptions(doc);
      if (double.IsNaN(_labelOffset))
        _labelOffset = ModelUnitsFromInches(doc, ConfiguredDefaultLabelOffIn);
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
        if (_windowLocation is {} location)
        {
          sec[WindowXKey] = location.X;
          sec[WindowYKey] = location.Y;
        }

        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var b in _curveSides) arr.Add(b);
        sec["curve_sides"] = arr;
      });

      if (!ok)
        RhinoApp.WriteLine($"vNotches: failed to save options: {ToolsOptionStore.LastError}");
      SaveDocumentOptions(s.Doc);
      MatchingLabels.NotifyChanged(s.Doc);
    }

    static void ApplyStoredOptions(
      System.Text.Json.Nodes.JsonObject? s, bool includeUiSettings)
    {
      if (ToolsOptionStore.TryGetDouble(s, "notch_length",    out var v)) _notchLength   = v;
      if (ToolsOptionStore.TryGetDouble(s, "notch_offset",    out v))     _notchOffset   = v;
      if (ToolsOptionStore.TryGetDouble(s, "notch_width",     out v))     _notchWidth    = v;
      if (ToolsOptionStore.TryGetString(s, "notch_type",      out var t)) _notchType     = t;
      if (ToolsOptionStore.TryGetBool  (s, "notch",           out var b)) _notch         = b;
      if (ToolsOptionStore.TryGetBool  (s, "both_sides",      out b))     _bothSides     = b;
      if (ToolsOptionStore.TryGetString(s, "notch_trim", out t) &&
          Enum.TryParse(t, true, out NotchTrimMode trimMode) && Enum.IsDefined(trimMode))
        _notchTrim = trimMode;
      if (ToolsOptionStore.TryGetString(s, "notch_trim_layer", out t) &&
          !string.IsNullOrWhiteSpace(t)) _notchTrimLayer = t;
      if (ToolsOptionStore.TryGetBool  (s, "percent",         out b))     _percent       = b;
      if (ToolsOptionStore.TryGetBool  (s, "group",           out b))     _group         = b;
      if (ToolsOptionStore.TryGetBool(s,"associations",out b)) _associations=b;
      if (ToolsOptionStore.TryGetBool(s,"opposite_matches",out b)) _oppositeMatches=b;
      if (ToolsOptionStore.TryGetBool  (s, "label",           out b))     _label         = b;
      _labelValue = MatchingLabels.ReadText(s, "label_value", _labelValue);
      _labelPrefix = MatchingLabels.ReadText(s, "label_prefix", _labelPrefix);
      if (ToolsOptionStore.TryGetDouble(s, "label_size",      out v))     _labelSize     = v;
      if (ToolsOptionStore.TryGetBool  (s, "label_size_auto", out b))     _labelSizeAuto = b;
      if (ToolsOptionStore.TryGetDouble(s, "label_size_pct",  out var pctv)) _labelSizePct = (int)pctv;
      if (ToolsOptionStore.TryGetString(s, "notch_layer",     out t))     _notchLayer    = t;
      _labelLayer = MatchingLabels.ReadText(s, "label_layer", _labelLayer);
      if (ToolsOptionStore.TryGetDouble(s, "label_offset",    out v))     _labelOffset   = v;
      if (ToolsOptionStore.TryGetDouble(s, "label_offset_y",  out v))     _labelOffsetY  = v;
      _labelSideFlip = ReadLabelDirection(s, _curveSides.FirstOrDefault());
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
      _windowLocation = ReadWindowLocation(s);
      if (ToolsOptionStore.TryGetBool(s, "keep_selection", out b)) _keepSelection = b;
      if (ToolsOptionStore.TryGetDouble(s, "window_width", out v))
        _windowWidth = Math.Max(ConfiguredDefaultWindowWidth, (int)Math.Round(v));
      if (ToolsOptionStore.TryGetDouble(s, "window_height", out v))
        _windowHeight = Math.Max(ConfiguredDefaultWindowHeight, (int)Math.Round(v));
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
      sec["both_sides"] = _bothSides;
      sec["notch_trim"] = _notchTrim.ToString();
      sec["notch_trim_layer"] = _notchTrimLayer;
      sec["percent"] = _percent;
      sec["group"] = _group;
      sec["associations"] = _associations;
      sec["opposite_matches"] = _oppositeMatches;
      sec["label"] = _label;
      sec["label_value"] = _labelValue;
      sec["label_prefix"] = _labelPrefix;
      sec["label_size"] = _labelSize;
      sec["label_size_auto"] = _labelSizeAuto;
      sec["label_size_pct"] = _labelSizePct;
      sec["notch_layer"] = _notchLayer;
      sec["label_layer"] = _labelLayer;
      sec["label_offset"] = _labelOffset;
      sec["label_offset_y"] = _labelOffsetY;
      sec.Remove("label_auto_adv");
      sec[LabelDirectionSettingsKey] = _labelSideFlip;
      sec["label_side_flip"] = _labelSideFlip ^ _curveSides.FirstOrDefault();
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

    static Eto.Drawing.Point? ReadWindowLocation(System.Text.Json.Nodes.JsonObject? options)
    {
      if (ToolsOptionStore.TryGetDouble(options, WindowXKey, out var x) &&
          ToolsOptionStore.TryGetDouble(options, WindowYKey, out var y) &&
          double.IsFinite(x) && double.IsFinite(y) &&
          x >= int.MinValue && x <= int.MaxValue && y >= int.MinValue && y <= int.MaxValue)
        return new Eto.Drawing.Point((int)Math.Round(x), (int)Math.Round(y));
      return null;
    }

    static Eto.Drawing.Point FitWindowLocation(Eto.Drawing.Point location, Eto.Drawing.Size size,
      IReadOnlyList<Eto.Drawing.Rectangle> workAreas)
    {
      var areas = workAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
      if (areas.Length == 0) return location;
      double Overlap(Eto.Drawing.Rectangle area) =>
        Math.Max(0, Math.Min((double)location.X + size.Width, area.Right) - Math.Max(location.X, area.Left)) *
        Math.Max(0, Math.Min((double)location.Y + size.Height, area.Bottom) - Math.Max(location.Y, area.Top));
      double Distance(Eto.Drawing.Rectangle area)
      {
        double x = location.X + size.Width / 2.0, y = location.Y + size.Height / 2.0;
        double dx = x - Math.Clamp(x, area.Left, area.Right), dy = y - Math.Clamp(y, area.Top, area.Bottom);
        return dx * dx + dy * dy;
      }
      var screen = areas.OrderByDescending(Overlap).ThenBy(Distance).First();
      return new Eto.Drawing.Point(
        Math.Clamp(location.X, screen.Left, screen.Right - Math.Min(Math.Max(1, size.Width), screen.Width)),
        Math.Clamp(location.Y, screen.Top, screen.Bottom - Math.Min(Math.Max(1, size.Height), screen.Height)));
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
    _bothSides     = s.BothSidesToggle.CurrentValue;
    _notchTrim     = s.NotchTrim;
    _notchTrimLayer = s.NotchTrimLayer;

    _percent       = s.PercentToggle.CurrentValue;
    _group         = s.GroupToggle.CurrentValue;
    _associations  = s.Associations;
    _oppositeMatches = s.OppositeMatches;
    _label         = s.LabelToggle.CurrentValue;
    _labelValue    = s.LabelValueText;
    _labelPrefix   = s.LabelPrefixText;

    _labelSize     = s.ManualLabelSize;
    _labelSizeAuto = s.LabelSizeAutoToggle.CurrentValue;
    _labelSizePct  = s.LabelSizePctValues[s.LabelSizePctIndex];

    _notchLayer    = s.NotchLayerName;
    _labelLayer    = s.LabelLayerName;
    _labelOffset   = s.LabelOffsetOpt.CurrentValue;
    _labelOffsetY  = s.LabelOffsetYOpt.CurrentValue;

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
      var preselected=doc.Objects.GetSelectedObjects(false,false).ToArray();
      bool editMode=!preselected.Any(obj=>obj.Geometry is Curve&&!IsNotchGeometry(obj));
      var curves=new List<Curve>();var curveIds=new List<Guid>();var curveSourceIds=new List<List<Guid>>();
      var curveSegments=new List<List<Curve>>();var sourceRegistry=new NotchCurveSources();
      if (!editMode&&!TrySelectCurves(doc, out curves, out curveIds,
        out curveSourceIds, out curveSegments, out sourceRegistry))
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
        _notchLength, _notchOffset, _notchWidth, _notchType, _notch, _bothSides,
        _percent, _group, _label, _labelValue,
        _labelSize, _labelSizeAuto, _labelSizePct,
        _notchLayer, _labelLayer, _labelOffset, _labelOffsetY,
        _labelSideFlip, _keepSelection,
        _multipleStartOffset, _multipleEndOffset,
        _multipleStartOffsetEnabled, _multipleEndOffsetEnabled, _multipleNumber,
        _multipleDistance, _multipleUseDistance,
        _multipleAuto, _multipleCurvatureSensitivity, _multipleSeparate, sourceRegistry)
        { LabelPrefixText = _labelPrefix,EditMode=editMode };
      if(editMode&&preselected.FirstOrDefault(IsNotchGeometry) is {} selectedNotch)LoadNotchForEdit(doc,session,selectedNotch.Id);

      // Apply actual source IDs so SelectBothCurves highlights all segments of joined chains.
      for (int i = 0; i < curveSourceIds.Count && i < session.PerCurveSourceIds.Count; i++)
      {
        session.PerCurveSourceIds[i] = curveSourceIds[i];
        session.PerCurveSegments[i] = curveSegments[i]
          .Select(curve => curve.DuplicateCurve())
          .ToList();
        foreach (var sourceId in curveSourceIds[i])
          session.CurveBothSidesBySource.TryAdd(sourceId, session.BothSidesToggle.CurrentValue);
      }
      ApplyAutomaticCurveSides(doc, session);
      session.ResetCurveDisplayNumbers();
      session.NotchTrim = _notchTrim;
      session.NotchTrimLayer = _notchTrimLayer;

      RunLoop(doc, session);
      SaveOptions(session);

      // Deselect whole sources and individually selected parent-curve segments.
      foreach (var sourceId in session.PerCurveSourceIds.SelectMany(ids => ids))
        session.Sources.Select(doc, sourceId, false);
      foreach (var id in OffsetSourceCurveIds(session))
        doc.Objects.FindId(id)?.Select(false);
      doc.Views.Redraw();

      return Result.Success;
    }

    // ── Curve selection ───────────────────────────────────────────────────────

    static bool TrySelectCurves(RhinoDoc doc, out List<Curve> curves,
      out List<Guid> curveIds, out List<List<Guid>> curveSourceIds,
      out List<List<Curve>> curveSegments, out NotchCurveSources sourceRegistry)
    {
      curves         = new List<Curve>();
      curveIds       = new List<Guid>();
      curveSourceIds = new List<List<Guid>>();
      curveSegments  = new List<List<Curve>>();
      sourceRegistry = new NotchCurveSources();
      using var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.SetCommandPrompt("Select one or more curves (near start)");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = true;
      go.SetCustomGeometryFilter((obj, _, _) => obj.Geometry is Curve);
      go.GroupSelect = false;
      go.EnablePreSelect(true, true);
      var res = go.GetMultiple(1, 0);
      if (go.CommandResult() != Result.Success || res != GetResult.Object)
        return false;
      var selectedObjects = sourceRegistry.Capture(go.Objects());
      var selectionPoints = new Dictionary<Guid, Point3d>();
      foreach (var source in selectedObjects)
      {
        if (source.SelectionPoint.IsValid)
          selectionPoints[source.Id] = source.SelectionPoint;
        Log.Write(Section, $"selected source={source.Id:D} parent={source.ParentObject.Id:D} component={source.Component.ComponentIndexType}:{source.Component.Index} length={source.Geometry.GetLength():G17}");
      }
      if (selectedObjects.Count == 0) return false;

      foreach (var logical in BuildLogicalCurveSelections(
        doc, selectedObjects, selectionPoints, existingSession: null))
      {
        curves.Add(logical.Curve);
        curveIds.Add(logical.SourceIds[0]);
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
      IReadOnlyList<NotchCurveSource> selectedObjects,
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
            componentObjects.First(obj => obj.Id == joinedIds[0]).ParentObject,
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
            obj.ParentObject,
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
        .SelectMany(id => RelatedComponentObjects(doc, id).Select(obj => obj.Id))
        .ToHashSet();

      using var go = new GetObject();
      go.EnableTransparentCommands(true);
      go.SetCommandPrompt(keepCurrentSelection
        ? "Add or remove curves. Press Enter when done"
        : "Select replacement curves. Press Enter when done");
      go.GeometryFilter = ObjectType.Curve;
      go.SubObjectSelect = true;
      go.GroupSelect = false;
      go.AcceptNothing(true);
      if (!keepCurrentSelection)
        NotchCurveSources.ClearSelection(doc);
      go.EnablePreSelect(keepCurrentSelection, true);
      go.EnablePostSelect(true);
      go.EnableClearObjectsOnEntry(false);
      go.EnableUnselectObjectsOnExit(false);
      go.DeselectAllBeforePostSelect = false;
      go.AlreadySelectedObjectSelect = true;
      go.SetCustomGeometryFilter((obj, _, _) => obj.Geometry is Curve && !generatedIds.Contains(obj.Id));

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

      // The picker owns the final set, including mixed whole/component selections.
      // Document selection flags may drop components while a whole curve is picked.
      var pickedSources = s.Sources.Capture(go.Objects());
      var selectedPool = (pickedSources.Count > 0 ? pickedSources : s.Sources.SelectedSources(doc))
        .Where(source => !generatedIds.Contains(source.ParentObject.Id))
        .ToList();
      Log.Write(Section, $"selection references: picker={go.ObjectCount} sources={pickedSources.Count} accepted={selectedPool.Count} segments={selectedPool.Count(source => source.Component.Index >= 0)}");
      if (selectedPool.Count == 0)
      {
        RhinoApp.WriteLine("vNotches: keep at least one curve selected.");
        SelectBothCurves(doc, s);
        return false;
      }

      var selectionPoints = new Dictionary<Guid, Point3d>();
      var getObjectOrder = new List<Guid>();
      foreach (var source in pickedSources)
      {
        var point = source.SelectionPoint;
        if (point != Point3d.Unset)
          selectionPoints[source.Id] = point;
        getObjectOrder.Add(source.Id);
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
      var selectedObjects = new List<NotchCurveSource>();

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
          $"added logical curve {s.Curves.Count} from {CountText.Format(desired.SourceIds.Count, "source curve")}");
        changed = true;
      }

      var sidesBeforeRestore = s.CurveSides.ToArray();
      RestoreCurveSides(s, sideSequence, existingSourceSet);
      ApplyAutomaticCurveSides(doc, s, existingSourceSet);

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
        ClearNotchRedo(s);
        vTools.Log.Write("vNotches", $"selection changed; enabled {CountText.Format(s.Curves.Count, "curve")}");
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
      s.ClearPlacementRuns();

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
    }

    static void AddSessionCurve(NotchSession s, RhinoObject rhObj, Curve curve,
      IReadOnlyList<Guid>? allSourceIds = null,
      IReadOnlyList<Curve>? sourceSegments = null,
      bool continuous = true)
    {
      int priorCurveCount = s.Curves.Count;
      bool initialSide = priorCurveCount > 0 && s.CurveSides[^1];

      s.Curves.Add(curve);
      s.CurveIds.Add(allSourceIds?.FirstOrDefault() ?? rhObj.Id);
      s.PerCurveSourceIds.Add(allSourceIds != null
        ? new List<Guid>(allSourceIds)
        : new List<Guid> { rhObj.Id });
      foreach (var sourceId in s.PerCurveSourceIds[^1])
      {
        if (!s.CurveSideBySource.ContainsKey(sourceId))
          s.CurveSideBySource[sourceId] = initialSide;
        s.CurveBothSidesBySource.TryAdd(sourceId, s.BothSidesToggle.CurrentValue);
      }
      s.EnsureCurveDisplayNumbers();
      s.PerCurveSegments.Add(sourceSegments != null
        ? sourceSegments.Select(segment => segment.DuplicateCurve()).ToList()
        : [curve.DuplicateCurve()]);
      s.CurveIsContinuous.Add(continuous);
      s.CurveSides = s.CurveSides.Append(initialSide).ToArray();
      s.CurveEnabled = s.CurveEnabled.Append(true).ToArray();

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
      var sideBySource = new Dictionary<Guid, bool>(s.CurveSideBySource);
      var bothSidesBySource = new Dictionary<Guid, bool>(s.CurveBothSidesBySource);
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
        var primary = s.Sources.FindObject(doc, sourceIds[0]);
        if (primary == null)
          continue;

        AddSessionCurve(s, primary, logicalCurve, sourceIds, segments, continuous);
        int logicalIndex = s.Curves.Count - 1;
        foreach (var sourceId in sourceIds)
        {
          s.CurveSideBySource[sourceId] = sideBySource.GetValueOrDefault(sourceId);
          s.CurveBothSidesBySource[sourceId] = bothSidesBySource.GetValueOrDefault(
            sourceId, s.BothSidesToggle.CurrentValue);
        }
        s.CurveSides[logicalIndex] = sideBySource.GetValueOrDefault(sourceIds[0]);
        s.CurveEnabled[logicalIndex] = enabledBySource.GetValueOrDefault(sourceIds[0], true);
      }

      ClearNotchRedo(s);
      s.PreviewValid = false;
      s.PreviewLengthsFromStart.Clear();
      if(!s.EditMode)SelectBothCurves(doc, s);
      SaveOptions(s);
      vTools.Log.Write("vNotches",
        $"curve rows changed: rows={rows.Count} linkedSequences={s.Curves.Count}");
      return true;
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

      if (!s.ConnectedPlacementRuns.TryGetValue(curveIndex, out var runs))
      {
        runs = new NotchPlacementRuns(s.PerCurveSegments[curveIndex],
          Math.Max(s.Doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance));
        s.ConnectedPlacementRuns[curveIndex] = runs;
        Log.Write(Section, $"connected placement runs: sequence={curveIndex + 1} sources={string.Join(",", s.PerCurveSourceIds[curveIndex].Select(s.CurveDisplayNumber))} runs={runs.Count}");
      }
      (curve, curveLength) = runs.Resolve(curveLength, kinkChoice == KinkTangentChoice.After);
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

    static void ApplyAutomaticCurveSides(RhinoDoc doc, NotchSession s,
      IReadOnlySet<Guid>? retainedSourceIds = null)
    {
      var sources = new List<(Guid Id, Curve Curve)>();
      for (int curveIndex = 0; curveIndex < s.PerCurveSourceIds.Count; curveIndex++)
        for (int segment = 0; segment < s.PerCurveSourceIds[curveIndex].Count &&
             segment < s.PerCurveSegments[curveIndex].Count; segment++)
          sources.Add((s.PerCurveSourceIds[curveIndex][segment], s.PerCurveSegments[curveIndex][segment]));
      var targets = sources.Where(source => retainedSourceIds?.Contains(source.Id) != true).ToArray();
      if (targets.Length == 0) return;
      var automaticSides = new Dictionary<Guid, bool>();
      var groupTargets = new Dictionary<int, List<(Guid Id, Curve Curve)>>();
      var groupCurves = new Dictionary<int, Curve[]>();
      foreach (var source in targets)
      {
        foreach (var groupIndex in s.Sources.FindObject(doc, source.Id)?.Attributes.GetGroupList() ?? [])
        {
          if (!groupTargets.TryGetValue(groupIndex, out var members))
          {
            members = [];
            groupTargets[groupIndex] = members;
            groupCurves[groupIndex] = (doc.Groups.GroupMembers(groupIndex) ?? [])
              .Where(obj => obj.Geometry is Curve &&
                obj.Attributes.GetUserString(NotchDataPrefix + "version") == null)
              .Select(obj => (Curve)obj.Geometry).ToArray();
          }
          members.Add(source);
        }
      }
      // Prefer the smallest usable part group over a broader enclosing assembly group.
      foreach (var group in groupTargets.OrderBy(item => groupCurves[item.Key].Length))
      {
        var unresolved = group.Value.Where(source => !automaticSides.ContainsKey(source.Id)).ToArray();
        if (unresolved.Length == 0) continue;
        var sides = NotchBoundarySides.DetectGroupSides(unresolved, groupCurves[group.Key], doc.ModelAbsoluteTolerance);
        foreach (var (id, side) in sides) automaticSides.TryAdd(id, side);
        Log.Write(Section, $"automatic group sides: group={group.Key} targets={unresolved.Length} curves={groupCurves[group.Key].Length} identified={sides.Count}");
      }
      var fallbackTargets = targets.Where(source => !automaticSides.ContainsKey(source.Id)).ToArray();
      if (fallbackTargets.Length > 0)
      {
        var sourceIds = sources.Where(source => !s.Sources.IsSegment(source.Id))
          .Select(source => s.Sources.ParentId(source.Id)).ToHashSet();
        var fallbackIds = fallbackTargets.Select(source => source.Id).ToHashSet();
        var settings = new ObjectEnumeratorSettings
        {
          ObjectTypeFilter = ObjectType.Curve,
          NormalObjects = true,
          LockedObjects = true,
          HiddenObjects = false,
          IncludeLights = false,
          IncludeGrips = false,
          IncludePhantoms = false
        };
        var context = doc.Objects.GetObjectList(settings)
          .Where(obj => obj.Geometry is Curve && !sourceIds.Contains(obj.Id) &&
            obj.Attributes.GetUserString(NotchDataPrefix + "version") == null)
          .Select(obj => (Curve)obj.Geometry)
          .Concat(sources.Where(source => !fallbackIds.Contains(source.Id))
            .Select(source => source.Curve)).ToArray();
        foreach (var (id, side) in NotchBoundarySides.Detect(fallbackTargets, context, doc.ModelAbsoluteTolerance))
          automaticSides.TryAdd(id, side);
      }
      foreach (var (sourceId, side) in automaticSides)
      {
        s.CurveSideBySource[sourceId] = side;
        Log.Write(Section, $"automatic boundary side: source={sourceId:D} side={(side ? "Left" : "Right")}");
      }
      foreach (var target in targets.Where(source => !automaticSides.ContainsKey(source.Id)))
        Log.Write(Section, $"automatic boundary side unresolved: source={target.Id:D}; retaining stored side");
      for (int curveIndex = 0; curveIndex < s.Curves.Count; curveIndex++)
        UpdateLogicalCurveSide(s, curveIndex);
    }

    static void RefreshPanelForCurves(NotchSession s)
    {
      s.Panel?.RefreshCurveRows();
      SyncPanelFromOptions(s);
    }

    // ── Main interactive loop ─────────────────────────────────────────────────

    static GetPoint CreateNotchPicker()
    {
      var gp = new GetPoint();
      gp.EnableTransparentCommands(true);
      gp.AcceptCustomMessage(true);
      gp.AcceptNothing(true);
      return gp;
    }

    static void RunLoop(RhinoDoc doc, NotchSession s)
    {
      using var gp = CreateNotchPicker();
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
      var panelTimer = Stopwatch.StartNew();
      var panel = new NotchPanel(doc, s);
      double constructMilliseconds = panelTimer.Elapsed.TotalMilliseconds;
      s.Panel = panel;
      panel.Show();
      UpdateDistanceLabels(s, null, null, null, null, null, null);
      Log.Write(Section, $"Panel startup: curves={s.Curves.Count} construct_ms={constructMilliseconds:0.0}" +
        $" show_ms={panelTimer.Elapsed.TotalMilliseconds - constructMilliseconds:0.0}");

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
      using var removalPicking = new NotchRemovalPicking(doc,s,gp);
      gp.DynamicDraw+=(_,e)=>removalPicking.DrawFeedback(e);
      LocalUndoRedoShortcutSession? shortcutSession = null;

      try
      {
        shortcutSession = new LocalUndoRedoShortcutSession(
          "vNotches",
          redo => new NotchHistoryRequest(redo, "shortcut"));
        while (true)
        {
          if (s.PanelClosedExit) { FinalizeBlocks(doc, s); return; }

          ConfigureNotchPicking(gp,s.EditMode);
          gp.SetCommandPrompt(s.EditMode?s.RepairPairing?"Link notches; Enter links the selected set; Enter with fewer than two or Link exits":s.EditTool!=NotchEditTool.None?NotchToolCaption(s.EditTool)+": select a notch; Esc or the active button exits":"Select a notch; drag to move":s.Curves.Count == 1
            ? "Select a point on curve (notch location)"
            : "Select a point on any selected curve (notch location)");
          RefreshCommandOptions(gp, s);
          removalPicking.Active=true;
          GetResult result;
          object? input;
          try
          {
            var pending=s.PendingInput??(object?)removalPicking.TakePendingMove();s.PendingInput=null;
            result=pending!=null?GetResult.CustomMessage:gp.Get();
            input=pending??(result==GetResult.CustomMessage?gp.CustomMessage():null);
            if(result!=GetResult.Cancel&&removalPicking.TakePendingMove() is {} requestedMove)
            {result=GetResult.CustomMessage;input=requestedMove;}
          }
          finally {removalPicking.Active=false;}

          if(result==GetResult.CustomMessage&&input is NotchRemovalRequest removal)
          {RemoveNotchComponent(doc,s,removal.ObjectId);continue;}
          if(result==GetResult.CustomMessage&&input is NotchMoveRequest move)
          {
            if(s.EditMode&&s.RepairPairing){QueueNotchForLink(doc,s,move.ObjectId);continue;}
            if(s.EditTool!=NotchEditTool.None){PickNotchForTool(doc,s,move.ObjectId);continue;}
            if(move.MouseDrag&&(s.EditedNotchId!=move.ObjectId||s.SelectedNotches.Count!=1))removalPicking.SelectForEdit(move.ObjectId,"select");
            else if(!s.EditMode||s.EditedNotchId!=move.ObjectId)LoadNotchForEdit(doc,s,move.ObjectId);
            if(move.MouseDrag&&!removalPicking.PointerDown)continue;
            removalPicking.HideFeedback();s.NotchMouseDrag=move.MouseDrag;s.NotchDragStart=move.StartPixel;
            try{DragExistingNotch(doc,s,move.ObjectId);}finally{s.NotchMouseDrag=false;}
            continue;
          }
          if(result==GetResult.CustomMessage&&input is NotchMateRequest mate)
          {if(s.EditMode)ShowNotchMatches(doc,s,mate.ObjectId);else SelectMatchingNotches(doc,mate.ObjectId);continue;}
          if(result==GetResult.CustomMessage&&input is NotchEditRequest edit)
          {
            if(s.EditMode&&s.RepairPairing&&edit.Action is "select" or "add")QueueNotchForLink(doc,s,edit.ObjectId);
            else if(s.EditTool!=NotchEditTool.None&&edit.Action is "select" or "add")PickNotchForTool(doc,s,edit.ObjectId);
            else if(edit.Action is "select" or "add" or "deselect")removalPicking.SelectForEdit(edit.ObjectId,edit.Action);
            else if(ToolForAction(edit.Action) is {} tool)ToggleNotchTool(doc,s,tool);
            else if(edit.Action=="pairs")PickNotchPairs(doc,s);
            else if(edit.Action=="link-start")SetNotchLinkMode(doc,s,true);
            else if(edit.Action=="link-stop")SetNotchLinkMode(doc,s,false);
            else if(edit.Action=="tool-stop")SetNotchTool(doc,s,NotchEditTool.None);
            else if(edit.Action=="link-complete"){if(s.RepairPairing)CompleteNotchLinkCycle(doc,s);}
            else {s.EditMode=!s.EditMode;s.RepairPairing=false;s.EditTool=NotchEditTool.None;s.EditedNotchId=Guid.Empty;s.SelectedNotches.Clear();s.EditedNotchObjects.Clear();s.Panel?.UpdateEditMode();}
            continue;
          }

          if (result == GetResult.CustomMessage && input is NotchHistoryRequest historyRequest)
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
          if(s.EditTool!=NotchEditTool.None&&result is GetResult.Cancel or GetResult.Nothing)
          {
            if(result==GetResult.Nothing&&s.PanelNumericPending){s.PanelNumericPending=false;continue;}
            if(result==GetResult.Nothing&&s.IgnoreNextNothing){s.IgnoreNextNothing=false;continue;}
            SetNotchTool(doc,s,NotchEditTool.None);continue;
          }
          if(s.RepairPairing&&result is GetResult.Cancel or GetResult.Nothing)
            Log.Write(Section,"link picker result={0} physicalPicks={1}",result,s.SelectedNotches.Count);
          if(s.RepairPairing&&result==GetResult.Cancel){SetNotchLinkMode(doc,s,false);continue;}
          if(s.RepairPairing&&result==GetResult.Nothing)
          {
            if(s.PanelNumericPending){s.PanelNumericPending=false;continue;}
            if(s.IgnoreNextNothing){s.IgnoreNextNothing=false;continue;}
            CompleteNotchLinkCycle(doc,s);continue;
          }

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
          if(!s.EditMode&&s.Curves.Count>0)PlaceNotchFromPreview(doc, gp.Point(), s);
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
        s.ClearPlacementRuns();
        foreach(var move in s.Moves.Concat(s.MoveRedo))move.Dispose();
      }
    }

    // ── Command options ───────────────────────────────────────────────────────

    static void RefreshCommandOptions(GetPoint gp, NotchSession s)
    {
      gp.ClearCommandOptions();
      s.SideOptionIndex        = gp.AddOption("Side");
      s.ReverseOptionIndex     = gp.AddOption("Reverse");
      s.UndoOptionIndex        = s.NotchRecords.Count > 0 || s.Removals.Count > 0 || s.Moves.Count > 0
        ? gp.AddOption("Undo", string.Empty, true)
        : -1;
      s.RedoOptionIndex        = s.RedoBatches.Count > 0 || s.RemovalRedo.Count > 0 || s.MoveRedo.Count > 0
        ? gp.AddOption("Redo", string.Empty, true)
        : -1;
      s.TypeOptionIndex        = gp.AddOptionList("NotchType", s.NotchTypeOptionValues, s.NotchTypeIndex);
      s.NotchTrimIndex         = gp.AddOptionList(
        "NotchTrim", NotchTrimNames, (int)s.NotchTrim);
      s.NotchTrimLayerIndex = gp.AddOption("NotchTrimLayer", s.NotchTrimLayer);
      s.NotchLayerOptionIndex  = gp.AddOption("NotchLayer", s.NotchLayerName);
      s.NotchEnabledIndex      = gp.AddOptionToggle("NotchEnabled", ref s.NotchToggle);
      s.BothSidesIndex          = gp.AddOption("BothSides");
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
      else if (idx == s.NotchTrimIndex)
      {
        SetNotchTrim(doc, s, (NotchTrimMode)opt.CurrentListOptionIndex);
      }
      else if (idx == s.NotchTrimLayerIndex)
      {
        string layer = s.NotchTrimLayer;
        if (RhinoGet.GetString("Notch trim destination layer", false, ref layer) == Result.Success &&
            !string.IsNullOrWhiteSpace(layer))
        {
          string previous = s.NotchTrimLayer;
          s.NotchTrimLayer = layer.Trim();
          if (!RefreshOffsetSplits(doc, s))
            s.NotchTrimLayer = previous;
          else
            SaveOptions(s);
        }
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
      else if (idx == s.BothSidesIndex)
      {
        int ci = 0;
        double length = 0.0;
        if (cursor.HasValue)
          ClosestCurveHit(s, cursor.Value, out ci, out _, out length);
        var sourceId = ResolvePlacementSourceCurveId(doc, s, ci, length, null);
        bool previous = s.CurveBothSidesBySource.GetValueOrDefault(
          sourceId, s.BothSidesToggle.CurrentValue);
        SetCurveBothSides(doc, s, ci, sourceId, !previous);
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

      var boundaryChoice=DisconnectedBoundaryChoice(s,refIdx,lengthFromStart,point);
      PlaceNotchWithLengths(doc, s, lengthsFromStart, s.LastCursorPoint ?? point,preferredKinkChoice:boundaryChoice);
    }
    static void PlaceNotchFromPreview(RhinoDoc doc, Point3d clickedPoint, NotchSession s)
    {
      if (!s.PreviewValid || s.PreviewLengthsFromStart.Count != s.Curves.Count)
      {
        PlaceNotchAtPoint(doc, clickedPoint, s);
        return;
      }

      PlaceNotchWithLengths(doc, s, s.PreviewLengthsFromStart, s.PreviewCursorPoint,preferredKinkChoice:s.PreviewKinkChoice);
    }

    static bool PlaceNotchWithLengths(RhinoDoc doc, NotchSession s,
      List<double> lengthsFromStart, Point3d? cursorPoint,
      bool allowLabel = true, bool manageUndo = true, bool advanceLabel = true,
      bool updateUi = true, bool usePercentMode = true, Guid? batchId = null,
      bool[]? curveEnabledOverride = null,
      KinkTangentChoice? preferredKinkChoice = null)
    {
      int count=s.Curves.Count;
      uint undo=0;
      var lengths=lengthsFromStart.ToList();
      var enabled=(curveEnabledOverride??s.CurveEnabled).ToList();
      var companions=new List<(int Original,int Companion,MatchingNotches.CurveCopy Copy)>();
      var listed=s.PerCurveSourceIds.SelectMany(ids=>ids).Select(s.Sources.ParentId).ToHashSet();
      try
      {
        if(s.Associations&&!s.EditMode)
          foreach(var copy in s.AssociatedCopies)
            for(int index=0;index<count;index++)
            {
              if(index>=lengths.Count||index<enabled.Count&&!enabled[index])continue;
              var ids=s.PerCurveSourceIds[index].Select(s.Sources.ParentId).ToArray();
              if(ids.Length==0||!ids.All(copy.Map.ContainsKey)||ids.Any(id=>listed.Contains(copy.Map[id])))continue;
              var source=doc.Objects.FindId(copy.Map[ids[0]]);
              if(source==null||!source.IsSelectable(true,false,false,false))continue;
              var segments=s.PerCurveSegments[index].Select(segment=>{var curve=segment.DuplicateCurve();curve.Transform(copy.Transform);return curve;}).ToList();
              var logical=BuildLayoutCurve(doc,segments,out bool continuous);
              AddSessionCurve(s,source,logical,ids.Select(id=>copy.Map[id]).ToList(),segments,continuous);
              foreach(var segment in segments)segment.Dispose();
              int added=s.Curves.Count-1;
              for(int j=0;j<ids.Length;j++)
              {
                Guid original=s.PerCurveSourceIds[index][j],target=copy.Map[ids[j]];
                s.CurveSideBySource[target]=s.CurveSideBySource.GetValueOrDefault(original,s.CurveSides[index])^(copy.Transform.Determinant<0);
                s.CurveBothSidesBySource[target]=s.CurveBothSidesBySource.GetValueOrDefault(original,s.BothSidesToggle.CurrentValue);
              }
              UpdateLogicalCurveSide(s,added);lengths.Add(lengths[index]);enabled.Add(true);
              companions.Add((index,added,copy));foreach(var id in ids)listed.Add(copy.Map[id]);
            }
        if(manageUndo){doc.UndoRecordingEnabled=true;undo=doc.BeginUndoRecord("Notch");}
        bool placed=PlaceNotchWithLengthsCore(doc,s,lengths,cursorPoint,allowLabel,false,advanceLabel,false,usePercentMode,batchId,enabled.ToArray(),preferredKinkChoice);
        if(placed&&companions.Count>0)
        {
          var record=s.NotchRecords.Last();
          foreach(var pair in companions)
          {
            var owner=doc.Objects.FindId(pair.Copy.Owner);
            var original=RelatedComponentObjects(doc,s.NotchIdsByCurve[pair.Original].LastOrDefault())
              .Concat(RelatedComponentObjects(doc,s.LabelIdsByCurve[pair.Original].LastOrDefault()??Guid.Empty)).ToArray();
            foreach(var obj in RelatedComponentObjects(doc,s.NotchIdsByCurve[pair.Companion].LastOrDefault())
              .Concat(RelatedComponentObjects(doc,s.LabelIdsByCurve[pair.Companion].LastOrDefault()??Guid.Empty)))
            {
              using var attributes=obj.Attributes.Duplicate();
              foreach(string? key in owner?.Attributes.GetUserStrings().AllKeys??[])
                if(key?.StartsWith("vNest.",StringComparison.Ordinal)==true)attributes.SetUserString(key,owner!.Attributes.GetUserString(key));
              var root=original.FirstOrDefault(item=>item.Attributes.GetUserString(NotchDataPrefix+"object_role")==attributes.GetUserString(NotchDataPrefix+"object_role")&&
                item.Attributes.GetUserString(NotchDataPrefix+"component_index")==attributes.GetUserString(NotchDataPrefix+"component_index"));
              attributes.SetUserString(MatchingNotches.SourceMatchKey,record.MatchSetId.ToString("N"));
              if(root!=null)attributes.SetUserString(MatchingNotches.SourceMemberKey,root.Id.ToString("N"));
              doc.Objects.ModifyAttributes(obj.Id,attributes,true);
            }
          }
        }
        return placed;
      }
      finally
      {
        if(undo!=0)doc.EndUndoRecord(undo);
        while(s.Curves.Count>count)
        {
          int index=s.Curves.Count-1;s.Curves[index].Dispose();
          foreach(var segment in s.PerCurveSegments[index])segment.Dispose();
          RemoveSessionCurve(s,index);
        }
        if(updateUi){s.Panel?.UpdateUndoEnabled();doc.Views.Redraw();}
      }
    }

    static bool PlaceNotchWithLengthsCore(RhinoDoc doc, NotchSession s,
      List<double> lengthsFromStart, Point3d? cursorPoint,
      bool allowLabel, bool manageUndo, bool advanceLabel,
      bool updateUi, bool usePercentMode, Guid? batchId,
      bool[]? curveEnabledOverride, KinkTangentChoice? preferredKinkChoice)
    {
      double notchLen = s.NotchLengthOpt.CurrentValue;
      double notchOff = s.NotchOffsetOpt.CurrentValue;
      string notchTyp = s.NotchTypeValues[s.NotchTypeIndex];
      double notchWid = s.NotchWidthOpt.CurrentValue;
      double resolvedLabelSize = EffectiveLabelSize(s);

      string effectiveNotchLayer = EffectiveLayerName(doc, s.NotchLayerName, s.NotchLayerName);
      string effectiveLabelLayer = EffectiveLayerName(doc, s.LabelLayerName, s.NotchLayerName);

      string labelText = s.LabelValueText.Trim();
      bool canNotch    = s.NotchToggle.CurrentValue;
      bool canLabel    = allowLabel && s.LabelToggle.CurrentValue && HasLabelText(labelText,s.LabelPrefixText);
      if(!canNotch&&!canLabel)return false;
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
        preferredKinkChoice??=DisconnectedBoundaryChoice(s,s.PreviewRefCurveIndex,
          lengthsFromStart[s.PreviewRefCurveIndex],cursorPoint.Value);
        ResolvePlacementCurve(s, s.PreviewRefCurveIndex,
          lengthsFromStart[s.PreviewRefCurveIndex], preferredKinkChoice,
          out var referenceCurve, out double referenceLength);
        referenceKinkChoice = preferredKinkChoice??ResolveKinkChoice(
          referenceCurve, referenceLength, cursorPoint.Value);
      }

      var splitPlans = canNotch
        ? PrepareOffsetSplits(
            doc, s, lengthsFromStart, placementCurveEnabled, notchLen, notchOff,
            notchTyp, notchWid,
            cursorPoint, referenceKinkChoice)
        : [];
      var reuse = PrepareNotchReuse(doc,s,lengthsFromStart,placementCurveEnabled,
        canNotch,canLabel,referenceKinkChoice);
      if (reuse == null)
        return false;
      var affectedHistory = splitPlans
        .SelectMany(plan => HistoryBreakWarning.CaptureAffectedRecords(doc, plan.TargetId))
        .ToHashSet();
      foreach(var original in reuse.Originals)
        affectedHistory.UnionWith(HistoryBreakWarning.CaptureAffectedRecords(doc,original.Attributes.ObjectId));
      if (!HistoryBreakWarning.Confirm(doc, "vNotches", affectedHistory))
        return false;

      uint undoRec = 0;
      bool undoStarted = false;
      if (manageUndo)
      {
        doc.UndoRecordingEnabled = true;
        undoRec = doc.BeginUndoRecord("Notch");
        undoStarted = true;
      }

      List<(Guid notch, Guid? label)>? newIds = null;
      Guid matchSetId = reuse.MatchSetId;
      var splitEdits = new List<OffsetSplitEdit>();
      try
      {
        newIds = AddNotchesPerCurve(doc, s,
          lengthsFromStart, notchLen, notchOff, notchTyp, notchWid,
          canNotch, canLabel, placementLabels, resolvedLabelSize,
          effectiveNotchLayer, effectiveLabelLayer,
          s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue,
          s.LabelSideFlip, cursorPoint, referenceKinkChoice, placementCurveEnabled,
          placementMode, matchSetId);
        if (!ApplyNotchReuse(doc,s,reuse,newIds))
        {
          DeletePlacedNotches(doc,newIds);
          RestoreNotchReuse(doc,s,reuse,false);
          return false;
        }
        if (newIds.Any(item => item.notch != Guid.Empty) &&
            !TryApplyOffsetSplits(doc, splitPlans, newIds, s.NotchTrim,
              s.NotchTrimLayer, splitEdits))
        {
          DeletePlacedNotches(doc, newIds);
          RestoreNotchReuse(doc,s,reuse,false);
          return false;
        }
      }
      finally
      {
        if (undoStarted)
          doc.EndUndoRecord(undoRec);
      }

      if (newIds == null || !newIds.Any(n => n.notch != Guid.Empty || n.label.HasValue))
        return false;

      ClearNotchRedo(s);
      var record = new NotchRecord
      {
        HistoryOrder=++s.HistoryOrder,
        MatchSetId       = matchSetId,
        Reuse            = reuse,
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
        LabelPrefix      = s.LabelPrefixText,
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
      record.OffsetSplits.AddRange(splitEdits);
      for(int curveIndex=0;curveIndex<s.Curves.Count;curveIndex++)
      {
        var source=ResolvePlacementSourceCurveId(doc,s,curveIndex,lengthsFromStart[curveIndex],referenceKinkChoice);
        if(source!=Guid.Empty)record.PlacementSources[s.CurveIds[curveIndex]]=source;
      }

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
        bool stationBlocked = false;
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
          bool tooClose = IsMultiplePositionOccupied(candidateLength,
            existingByCurve[curveIndex], clearance, tolerance);
          stationBlocked |= tooClose;
          curveEnabled[curveIndex] = !tooClose;
        }

        if (!stationBlocked && curveEnabled.Any(enabled => enabled))
          plans.Add(new MultiplePlacementPlan(new List<double>(lengths), curveEnabled));
      }

      return plans;
    }

    static bool IsMultiplePositionOccupied(double candidateLength, IReadOnlyList<double> existingLengths,
      double clearance, double tolerance) => existingLengths.Any(existingLength =>
    {
      double distance = Math.Abs(existingLength - candidateLength);
      return distance <= tolerance || distance < clearance - tolerance;
    });

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
        return s.MultipleDistance * ConfiguredMultipleExistingNotchClearanceScale;

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

      // Number/Auto stations are independent; a middle notch must not reserve the intervals on either side.
      double proximity = Math.Min(localSpacing / 2, Math.Max(tolerance, s.NotchWidthOpt.CurrentValue));
      return Math.Max(tolerance, proximity * ConfiguredMultipleExistingNotchClearanceScale);
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
      bool labelActive = s.LabelToggle.CurrentValue && HasLabelText(originalLabel,s.LabelPrefixText);
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
            allowLabel: !firstPlacementAdded,
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
              s.MultipleCurvatureSensitivity * ConfiguredMultipleCurvatureSensitivityUnit,
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
                  s.MultipleCurvatureSensitivity * ConfiguredMultipleCurvatureSensitivityUnit,
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
          ConfiguredMultipleAutoMaximumSamples,
          (available / maximumDistance) * ConfiguredMultipleAutoSamplesPerSpacing));
      return Math.Clamp(
        Math.Max(ConfiguredMultipleAutoMinimumSamples, proportionalSamples),
        1,
        ConfiguredMultipleAutoMaximumSamples);
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
        ConfiguredMultipleAutoKinkSnapDistanceScale * maximumDistance /
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
            angle >= RhinoMath.ToRadians(ConfiguredMultipleAutoKinkMinimumAngleDegrees))
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

    sealed record NotchRemovalRequest(Guid ObjectId);
    sealed record RemovalSlot(NotchRecord Record,Guid Curve,bool Label,Guid Primary);
    sealed record RemovedObject(Guid Id,uint Serial,uint Crc);
    sealed record NotchRemoval(long Order,RemovedObject[] Objects,RemovalSlot[] Slots);

    sealed record ReusedCurveObjects(RhinoObject[] Notches,RhinoObject[] Labels);
    sealed record MatchAttributeEdit(Guid Id,string Before,string? BeforeValue,string? BeforePrefix,
      TextEntity? BeforeText,string? AfterText);
    sealed record MatchRecordEdit(NotchRecord Record,Guid Before,List<string> BeforeValues,string BeforePrefix);
    sealed class NotchReuse
    {
      public Guid MatchSetId=Guid.NewGuid();
      public string? LabelValue;
      public string LabelPrefix=DefaultLabelPrefix;
      public readonly Dictionary<int,ReusedCurveObjects> Curves=[];
      public readonly List<DocObjectSnapshot> Originals=[];
      public readonly List<RemovalSlot> Superseded=[];
      public readonly List<MatchAttributeEdit> MatchEdits=[];
      public readonly List<MatchRecordEdit> RecordEdits=[];
    }

    static bool OwnedNotchMatch(RhinoObject obj)=>Guid.TryParse(
      obj.Attributes.GetUserString(NotchMatchMemberKey),out var member)&&member==obj.Id;

    static bool TryStoredNotchPoint(string? text,out Point3d point)
    {
      point=Point3d.Unset;
      var values=text?.Split(',');
      if(values?.Length!=3)return false;
      var numbers=new double[3];
      for(int i=0;i<3;i++)
        if(!double.TryParse(values[i],System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,out numbers[i]))return false;
      point=new Point3d(numbers[0],numbers[1],numbers[2]);return point.IsValid;
    }

    static bool SameNotchStation(RhinoObject obj,Curve curve,double length,double tolerance)
    {
      if(obj.Attributes.GetUserString(MatchingNotches.CurveReferenceKey)!=null)
      {
        if(!MatchingNotches.TryResolve(obj.Document,obj.Attributes,out var reference,out double relativeStation))return false;
        using(reference)return MatchingNotches.PointAtLength(reference!,relativeStation).DistanceTo(PointAtCurveLength(curve,length).pt)<=tolerance;
      }
      if(MatchingNotches.TryPoint(obj.Document,obj.Attributes,"curve_point",out var point))
        if(point.DistanceTo(PointAtCurveLength(curve,length).pt)>tolerance)return false;
      if(!MatchingNotches.TryPoint(obj.Document,obj.Attributes,"curve_start",out var start)||
         !MatchingNotches.TryPoint(obj.Document,obj.Attributes,"curve_end",out var end)||
         !double.TryParse(obj.Attributes.GetUserString(NotchDataPrefix+"length_from_start"),
           System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var station))return false;
      if(start.DistanceTo(curve.PointAtStart)<=tolerance&&end.DistanceTo(curve.PointAtEnd)<=tolerance)
        return Math.Abs(station-length)<=tolerance;
      return start.DistanceTo(curve.PointAtEnd)<=tolerance&&end.DistanceTo(curve.PointAtStart)<=tolerance&&
        Math.Abs(curve.GetLength()-station-length)<=tolerance;
    }

    static NotchReuse? PrepareNotchReuse(RhinoDoc doc,NotchSession s,List<double> lengths,
      bool[] enabled,bool notch,bool label,KinkTangentChoice choice)
    {
      var reuse=new NotchReuse{LabelValue=label?s.LabelValueText.Trim():null,LabelPrefix=s.LabelPrefixText};
      var matches=new HashSet<Guid>();
      for(int i=0;i<s.Curves.Count;i++)
      {
        if(i>=lengths.Count||i<enabled.Length&&!enabled[i])continue;
        ResolvePlacementCurve(s,i,lengths[i],choice,out var curve,out var length);
        var source=s.Sources.ParentId(ResolvePlacementSourceCurveId(doc,s,i,lengths[i],choice));
        if(source==Guid.Empty)continue;
        var candidates=(doc.Objects.FindByUserString(NotchDataPrefix+"curve_id",source.ToString(),true)??[])
          .Where(obj=>obj is {IsDeleted:false}&&RemovableNotchObject(obj)&&
            (string.IsNullOrEmpty(obj.Attributes.GetUserString(NotchMatchSetKey))||OwnedNotchMatch(obj))&&
            SameNotchStation(obj,curve,length,doc.ModelAbsoluteTolerance))
          .OrderBy(NotchComponentIndex).ToArray();
        foreach(var obj in candidates)
          if(OwnedNotchMatch(obj)&&Guid.TryParse(obj.Attributes.GetUserString(NotchMatchSetKey),out var match))
          {if(matches.Count==0)reuse.MatchSetId=match;matches.Add(match);}
        var notches=notch?candidates.Where(obj=>obj.Attributes.GetUserString(NotchDataPrefix+"object_role")=="notch").ToArray():[];
        var labels=label?candidates.Where(obj=>obj.Attributes.GetUserString(NotchDataPrefix+"object_role")=="label").ToArray():[];
        if(notches.Length+labels.Length==0)continue;
        reuse.Curves[i]=new(notches,labels);
        foreach(var obj in notches.Concat(labels))
          if(reuse.Originals.All(snapshot=>snapshot.Attributes.ObjectId!=obj.Id)&&CaptureDocObject(doc,obj.Id) is {} snapshot)
            reuse.Originals.Add(snapshot);
      }
      var originalIds=reuse.Originals.Select(snapshot=>snapshot.Attributes.ObjectId).ToHashSet();
      foreach(var match in matches)
        foreach(var obj in doc.Objects.FindByUserString(NotchMatchSetKey,match.ToString("N"),true)??[])
        {
          if(obj.IsDeleted||!OwnedNotchMatch(obj)||originalIds.Contains(obj.Id))continue;
          if(match==reuse.MatchSetId&&!label)continue;
          var text=label?obj.Geometry as TextEntity:null;
          reuse.MatchEdits.Add(new(obj.Id,obj.Attributes.GetUserString(NotchMatchSetKey)!,
            obj.Attributes.GetUserString(NotchDataPrefix+"label_text"),obj.Attributes.GetUserString(NotchDataPrefix+"label_prefix"),
            text?.Duplicate() as TextEntity,text==null?null:MatchedLabelText(doc,obj,text,reuse.LabelValue!,reuse.LabelPrefix)));
        }
      foreach(var record in s.NotchRecords)
        if(matches.Contains(record.MatchSetId)&&(label||record.MatchSetId!=reuse.MatchSetId))
          reuse.RecordEdits.Add(new(record,record.MatchSetId,record.LabelValues.ToList(),record.LabelPrefix));
      for(int record=0;record<s.NotchRecords.Count;record++)
        for(int curve=0;curve<s.CurveIds.Count;curve++)
        {
          Guid notchId=s.PlacementIds[record][curve];var labelId=s.PlacementLabelIds[record][curve];
          if(originalIds.Contains(notchId)&&!s.RemovedSlots.Contains((s.NotchRecords[record],s.CurveIds[curve],false)))
            reuse.Superseded.Add(new(s.NotchRecords[record],s.CurveIds[curve],false,notchId));
          if(labelId.HasValue&&originalIds.Contains(labelId.Value)&&!s.RemovedSlots.Contains((s.NotchRecords[record],s.CurveIds[curve],true)))
            reuse.Superseded.Add(new(s.NotchRecords[record],s.CurveIds[curve],true,labelId.Value));
        }
      if(originalIds.Concat(reuse.MatchEdits.Select(edit=>edit.Id)).Any(id=>
          doc.Objects.FindId(id) is not {} obj||!obj.IsSelectable(true,false,false,false)))
      {
        RhinoApp.WriteLine("vNotches: an existing notch or matching object is locked or unavailable; placement was not changed.");
        return null;
      }
      return reuse;
    }

    static Guid ReuseNotchComponents(RhinoDoc doc,Guid fresh,RhinoObject[] existing,bool label,Guid notchId)
    {
      if(existing.Length==0&&(!label||fresh==Guid.Empty))return fresh;
      var freshObjects=RelatedComponentObjects(doc,fresh).Where(obj=>!obj.IsDeleted).OrderBy(NotchComponentIndex).ToArray();
      if(freshObjects.Length==0)throw new InvalidOperationException("Existing notch could not be updated.");
      var finalIds=new List<Guid>();
      for(int i=0;i<freshObjects.Length;i++)
      {
        var obj=freshObjects[i];Guid id=i<existing.Length?existing[i].Id:obj.Id;
        var attributes=obj.Attributes.Duplicate();attributes.ObjectId=id;
        attributes.SetUserString(NotchMatchMemberKey,id.ToString("N"));
        attributes.SetUserString(NotchDataPrefix+(label?"label_id":"notch_id"),id.ToString());
        if(label)attributes.SetUserString(NotchDataPrefix+"notch_id",notchId==Guid.Empty?string.Empty:notchId.ToString());
        if(i<existing.Length)
        {
          foreach(int group in existing[i].Attributes.GetGroupList()??[])attributes.AddToGroup(group);
          var userStrings=existing[i].Attributes.GetUserStrings();
          foreach(string? key in userStrings.AllKeys)
            if(key!=null&&!key.StartsWith(NotchDataPrefix,StringComparison.Ordinal)&&attributes.GetUserString(key)==null)
              attributes.SetUserString(key,userStrings[key]);
          if(!doc.Objects.Replace(id,obj.Geometry,true)||!doc.Objects.ModifyAttributes(id,attributes,true)||!doc.Objects.Delete(obj.Id,true))
            throw new InvalidOperationException("Existing notch could not be updated.");
        }
        else if(!doc.Objects.ModifyAttributes(id,attributes,true))
          throw new InvalidOperationException("Notch metadata could not be updated.");
        finalIds.Add(id);
      }
      foreach(var surplus in existing.Skip(freshObjects.Length))
        if(!doc.Objects.Delete(surplus.Id,true))throw new InvalidOperationException("Duplicate notch could not be removed.");
      return finalIds[0];
    }

    static int NotchComponentIndex(RhinoObject obj)=>int.TryParse(
      obj.Attributes.GetUserString(NotchDataPrefix+"component_index"),out var index)?index:0;

    static bool ApplyNotchReuse(RhinoDoc doc,NotchSession s,NotchReuse reuse,List<(Guid notch,Guid? label)> ids)
    {
      try
      {
        foreach(var pair in reuse.Curves)
        {
          var current=ids[pair.Key];
          var notch=ReuseNotchComponents(doc,current.notch,pair.Value.Notches,false,Guid.Empty);
          var label=ReuseNotchComponents(doc,current.label??Guid.Empty,pair.Value.Labels,true,notch);
          ids[pair.Key]=(notch,label==Guid.Empty?null:label);
        }
        foreach(var edit in reuse.MatchEdits)
          if(!ApplyMatchedData(doc,reuse,edit,true))throw new InvalidOperationException("Existing match could not be updated.");
        foreach(var edit in reuse.RecordEdits)ApplyMatchedRecord(reuse,edit,true);
        foreach(var slot in reuse.Superseded)s.RemovedSlots.Add((slot.Record,slot.Curve,slot.Label));
        return true;
      }
      catch(Exception ex){Log.Write(Section,"notch reuse failed: {0}",ex);return false;}
    }

    static void RestoreNotchReuse(RhinoDoc doc,NotchSession s,NotchReuse reuse,bool redo)
    {
      foreach(var snapshot in reuse.Originals)
      {
        Guid id=snapshot.Attributes.ObjectId;
        if(redo){if(doc.Objects.FindId(id) is {IsDeleted:false})doc.Objects.Delete(id,true);}
        else if(doc.Objects.FindId(id) is {IsDeleted:false})
        {doc.Objects.Replace(id,snapshot.Geometry,true);doc.Objects.ModifyAttributes(id,snapshot.Attributes.Duplicate(),true);}
        else RestoreDocObject(doc,snapshot);
      }
      foreach(var edit in reuse.MatchEdits)ApplyMatchedData(doc,reuse,edit,redo);
      foreach(var edit in reuse.RecordEdits)ApplyMatchedRecord(reuse,edit,redo);
      foreach(var slot in reuse.Superseded)
      {
        if(redo)s.RemovedSlots.Add((slot.Record,slot.Curve,slot.Label));
        else
        {
          s.RemovedSlots.Remove((slot.Record,slot.Curve,slot.Label));
          int record=s.NotchRecords.IndexOf(slot.Record),curve=s.CurveIds.IndexOf(slot.Curve);
          if(record<0||curve<0)continue;
          if(slot.Label){s.PlacementLabelIds[record][curve]=slot.Primary;s.LabelIdsByCurve[curve][record]=slot.Primary;}
          else{s.PlacementIds[record][curve]=slot.Primary;s.NotchIdsByCurve[curve][record]=slot.Primary;}
        }
      }
    }

    static void ApplyMatchedRecord(NotchReuse reuse,MatchRecordEdit edit,bool redo)
    {
      edit.Record.MatchSetId=redo?reuse.MatchSetId:edit.Before;
      edit.Record.LabelValues=redo&&reuse.LabelValue!=null
        ?edit.BeforeValues.Select(_=>reuse.LabelValue).ToList():edit.BeforeValues.ToList();
      edit.Record.LabelPrefix=redo&&reuse.LabelValue!=null?reuse.LabelPrefix:edit.BeforePrefix;
    }

    static string MatchedLabelText(RhinoDoc doc,RhinoObject obj,TextEntity text,string value,string prefix)
    {
      if(MatchingNotches.TryPoint(doc,obj.Attributes,"curve_point",out var point))
        return FormatNotchLabel(value,prefix,text.Plane,point);
      if(Guid.TryParse(obj.Attributes.GetUserString(NotchDataPrefix+"curve_id"),out var source)&&
        doc.Objects.FindId(source)?.Geometry is Curve curve&&
        double.TryParse(obj.Attributes.GetUserString(NotchDataPrefix+"length_from_start"),
          System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var station))
        return FormatNotchLabel(value,prefix,text.Plane,PointAtCurveLength(curve,station).pt);
      string beforePrefix=obj.Attributes.GetUserString(NotchDataPrefix+"label_prefix")??string.Empty;
      string beforeValue=LabelSequences.FormatDisplayLabel(obj.Attributes.GetUserString(NotchDataPrefix+"label_text")??string.Empty);
      bool valueFirst=text.PlainText==beforeValue+beforePrefix[beforePrefix.TrimEnd().Length..]+beforePrefix.TrimEnd();
      return FormatNotchLabel(value,prefix,text.Plane,text.Plane.Origin+text.Plane.XAxis*(valueFirst?-1:1));
    }

    static bool ApplyMatchedData(RhinoDoc doc,NotchReuse reuse,MatchAttributeEdit edit,bool redo)
    {
      if(doc.Objects.FindId(edit.Id) is not {IsDeleted:false} obj)return false;
      var attributes=obj.Attributes.Duplicate();
      attributes.SetUserString(NotchMatchSetKey,redo?reuse.MatchSetId.ToString("N"):edit.Before);
      if(reuse.LabelValue!=null)
      {
        attributes.SetUserString(NotchDataPrefix+"label_text",redo?reuse.LabelValue:edit.BeforeValue);
        attributes.SetUserString(NotchDataPrefix+"label_prefix",redo?reuse.LabelPrefix:edit.BeforePrefix);
        if(edit.BeforeText!=null)
        {
          using var text=edit.BeforeText.Duplicate() as TextEntity;
          if(text==null)return false;
          if(redo)text.PlainText=edit.AfterText!;
          if(!doc.Objects.Replace(edit.Id,text,true))return false;
        }
      }
      return doc.Objects.ModifyAttributes(edit.Id,attributes,true);
    }

    static bool RemovableNotchObject(RhinoObject obj)=>obj is {IsDeleted:false}&&
      (obj.Geometry is TextEntity or TextDot||obj.Attributes.Name is NotchObjectName or NotchLabelObjectName||
        obj.Attributes.GetUserString(NotchDataPrefix+"object_role") is "notch" or "label");

    static RhinoObject[] RemovalTargets(RhinoDoc doc,Guid id)
    {
      if(doc.Objects.FindId(id) is not {} picked||!RemovableNotchObject(picked))return [];
      var objects=RelatedComponentObjects(doc,id).Where(RemovableNotchObject).ToArray();
      string? matchSet=picked.Attributes.GetUserString(NotchMatchSetKey);
      if(Guid.TryParse(matchSet,out _))
      {
        bool OwnedMatch(RhinoObject obj)=>Guid.TryParse(
          obj.Attributes.GetUserString(NotchMatchMemberKey),out var member)&&member==obj.Id;
        bool Label(RhinoObject obj)=>obj.Geometry is TextEntity or TextDot||
          obj.Attributes.GetUserString(NotchDataPrefix+"object_role")=="label";
        objects=OwnedMatch(picked)
          ?(doc.Objects.FindByUserString(NotchMatchSetKey,matchSet!,true)??[])
            .Where(obj=>RemovableNotchObject(obj)&&OwnedMatch(obj)&&Label(obj)==Label(picked))
            .OrderBy(obj=>obj.Id==id?0:1).ToArray()
          :[picked];
      }
      return objects.All(obj=>obj.IsSelectable(true,false,false,false))?objects:[];
    }

    static bool RemoveNotchComponent(RhinoDoc doc,NotchSession s,Guid id)
    {
      var targets=RemovalTargets(doc,id);
      if(targets.Length==0)return false;
      var objects=targets.Select(obj=>new RemovedObject(obj.Id,obj.RuntimeSerialNumber,obj.Geometry.DataCRC(0))).ToArray();
      var ids=objects.Select(obj=>obj.Id).ToHashSet();
      var slots=new List<RemovalSlot>();
      for(int record=0;record<s.NotchRecords.Count;record++)
        for(int curve=0;curve<s.CurveIds.Count;curve++)
        {
          if(record<s.PlacementIds.Count&&curve<s.PlacementIds[record].Count&&ids.Contains(s.PlacementIds[record][curve]))
            slots.Add(new(s.NotchRecords[record],s.CurveIds[curve],false,s.PlacementIds[record][curve]));
          if(record<s.PlacementLabelIds.Count&&curve<s.PlacementLabelIds[record].Count&&
            s.PlacementLabelIds[record][curve] is {} label&&ids.Contains(label))
            slots.Add(new(s.NotchRecords[record],s.CurveIds[curve],true,label));
        }
      var deleted=new List<RemovedObject>();
      foreach(var obj in objects)
      {
        if(!doc.Objects.Delete(obj.Id,true))
        {
          foreach(var previous in deleted)doc.Objects.Undelete(previous.Serial);
          Log.Write(Section,"Ctrl removal rejected id={0}; deletion rolled back",obj.Id);
          return false;
        }
        deleted.Add(obj);
      }
      ClearNotchRedo(s);
      var removal=new NotchRemoval(++s.HistoryOrder,objects,slots.ToArray());
      s.Removals.Push(removal);
      foreach(var slot in slots)s.RemovedSlots.Add((slot.Record,slot.Curve,slot.Label));
      s.PreviewValid=false;
      s.Panel?.UpdateUndoEnabled();
      Log.Write(Section,"Ctrl removed objects={0} currentSlots={1}",objects.Length,slots.Count);
      doc.Views.Redraw();return true;
    }

    static void MoveRemovalHistory(RhinoDoc doc,NotchSession s,bool redo)
    {
      var from=redo?s.RemovalRedo:s.Removals;var to=redo?s.Removals:s.RemovalRedo;
      if(!from.TryPeek(out var removal))return;
      var changed=new List<RemovedObject>();
      foreach(var item in removal.Objects)
      {
        bool success;
        if(redo)
        {
          var obj=doc.Objects.FindId(item.Id);
          success=obj is {IsDeleted:false}&&obj.Geometry.DataCRC(0)==item.Crc&&
            obj.IsSelectable(true,false,false,false)&&doc.Objects.Delete(item.Id,true);
        }
        else success=doc.Objects.Undelete(item.Serial);
        if(!success)
        {
          foreach(var previous in changed)
            if(redo)doc.Objects.Undelete(previous.Serial);else doc.Objects.Delete(previous.Id,true);
          RhinoApp.WriteLine("vNotches: removed objects changed or are locked; history was not applied.");
          return;
        }
        changed.Add(item);
      }
      foreach(var slot in removal.Slots)
      {
        if(redo)s.RemovedSlots.Add((slot.Record,slot.Curve,slot.Label));
        else
        {
          s.RemovedSlots.Remove((slot.Record,slot.Curve,slot.Label));
          int record=s.NotchRecords.IndexOf(slot.Record),curve=s.CurveIds.IndexOf(slot.Curve);
          if(record<0)continue;
          if(curve<0)
          {
            var detached=slot.Label?slot.Record.DetachedLabelIds:slot.Record.DetachedNotchIds;
            if(!detached.Contains(slot.Primary))detached.Add(slot.Primary);
          }
          else if(slot.Label)
          {s.PlacementLabelIds[record][curve]=slot.Primary;s.LabelIdsByCurve[curve][record]=slot.Primary;}
          else {s.PlacementIds[record][curve]=slot.Primary;s.NotchIdsByCurve[curve][record]=slot.Primary;}
        }
      }
      from.Pop();to.Push(removal);SyncPanelFromOptions(s);doc.Views.Redraw();
    }

    static void ClearNotchRedo(NotchSession s)
    {s.RedoBatches.Clear();s.RemovalRedo.Clear();foreach(var move in s.MoveRedo)move.Dispose();s.MoveRedo.Clear();}

    sealed record NotchMoveRequest(Guid ObjectId,bool MouseDrag=false,System.Drawing.Point StartPixel=default);
    sealed record NotchMateRequest(Guid ObjectId);
    sealed record NotchMoveRecord(NotchRecord Record,List<double> Before,List<double> After,double? BeforePercent,double? AfterPercent);
    sealed record NotchMoveObject(DocObjectSnapshot Before,DocObjectSnapshot After,bool BeforeExists=true,bool AfterExists=true);
    sealed class NotchMove : IDisposable
    {
      public long Order;
      public bool AttributesOnly;
      public bool? MovementChanged; // null denotes a property edit; false denotes a movement below document tolerance.
      public readonly List<NotchMoveObject> Objects=[];
      public readonly List<NotchMoveRecord> Records=[];
      public void Dispose()
      {foreach(var item in Objects){item.Before.Geometry.Dispose();item.Before.Attributes.Dispose();item.After.Geometry.Dispose();item.After.Attributes.Dispose();}}
    }
    static bool HasNotchPairData(RhinoObject obj)=>OwnedNotchMatch(obj)&&Guid.TryParse(obj.Attributes.GetUserString(NotchMatchSetKey),out _);
    static bool IsNotchGeometry(RhinoObject obj)=>obj.Geometry is Curve&&
      (obj.Attributes.Name==NotchObjectName||obj.Attributes.GetUserString(NotchDataPrefix+"object_role")=="notch");
    static RhinoObject[] NotchMoveObjects(RhinoDoc doc,Guid id)
    {
      if(doc.Objects.FindId(id) is not {IsDeleted:false} picked||!RemovableNotchObject(picked))return [];
      if(HasNotchPairData(picked))return (doc.Objects.FindByUserString(NotchMatchSetKey,picked.Attributes.GetUserString(NotchMatchSetKey)!,true)??[])
        .Where(obj=>RemovableNotchObject(obj)&&HasNotchPairData(obj)).OrderBy(obj=>obj.Id==id?0:1).ToArray();
      var local=RelatedComponentObjects(doc,id).Where(obj=>RemovableNotchObject(obj)&&!HasNotchPairData(obj)).ToList();
      var notchIds=local.Select(obj=>obj.Id.ToString()).Append(picked.Attributes.GetUserString(NotchDataPrefix+"notch_id")??string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
      foreach(string notch in notchIds.Where(value=>Guid.TryParse(value,out _)).ToArray())
        local.AddRange((doc.Objects.FindByUserString(NotchDataPrefix+"notch_id",notch,true)??[]).Where(obj=>RemovableNotchObject(obj)&&!HasNotchPairData(obj)));
      return local.DistinctBy(obj=>obj.Id).ToArray();
    }
    static RhinoObject[] MatchingNotchMates(RhinoDoc doc,Guid id)
    {
      if(doc.Objects.FindId(id) is not {} picked||!HasNotchPairData(picked))return [];
      string? point=MatchingNotches.StationKey(picked.Attributes),source=picked.Attributes.GetUserString(NotchDataPrefix+"curve_key");
      return NotchMoveObjects(doc,id).Where(obj=>IsNotchGeometry(obj)&&
        (obj.Attributes.GetUserString(NotchDataPrefix+"curve_key")!=source||MatchingNotches.StationKey(obj.Attributes)!=point)).ToArray();
    }
    static void SelectMatchingNotches(RhinoDoc doc,Guid id)
    {
      var mates=MatchingNotchMates(doc,id);
      if(mates.Length==0){RhinoApp.WriteLine("vNotches: no stored matching notch is available for this notch.");return;}
      doc.Objects.UnselectAll();foreach(var mate in mates)if(mate.IsSelectable(true,false,false,false))doc.Objects.Select(mate.Id,true);
      doc.Views.Redraw();
    }
    static bool TryNotchMoveCurve(RhinoDoc doc,NotchSession s,RhinoObject obj,out Curve? curve,out double station)
    {
      curve=null;station=0;
      if(obj.Attributes.GetUserString(MatchingNotches.CurveReferenceKey)!=null)
        return MatchingNotches.TryResolve(doc,obj.Attributes,out curve,out station);
      if(!TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_point"),out var point)||
        !TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_start"),out var start)||
        !TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_end"),out var end))return false;
      double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
      var candidates=s.Curves.AsEnumerable();
      if(Guid.TryParse(obj.Attributes.GetUserString(NotchDataPrefix+"curve_id"),out var source)&&doc.Objects.FindId(source)?.Geometry is Curve saved)
        candidates=candidates.Prepend(saved);
      foreach(var candidate in candidates)
      {
        bool forward=candidate.PointAtStart.DistanceTo(start)<=tolerance&&candidate.PointAtEnd.DistanceTo(end)<=tolerance;
        bool reverse=candidate.PointAtEnd.DistanceTo(start)<=tolerance&&candidate.PointAtStart.DistanceTo(end)<=tolerance;
        Curve? copy=null;
        if(forward||reverse){copy=candidate.DuplicateCurve();if(reverse&&!forward)copy.Reverse();}
        else if(candidate.ClosestPoint(start,out double from)&&candidate.ClosestPoint(end,out double to)&&
            candidate.PointAt(from).DistanceTo(start)<=tolerance&&candidate.PointAt(to).DistanceTo(end)<=tolerance&&from!=to)
        {copy=candidate.Trim(new Interval(Math.Min(from,to),Math.Max(from,to)));if(copy!=null&&to<from)copy.Reverse();}
        if(copy==null)continue;
        if(!copy.ClosestPoint(point,out double parameter)||copy.PointAt(parameter).DistanceTo(point)>tolerance){copy.Dispose();continue;}
        if(!HasNotchPairData(obj)&&IsNotchGeometry(obj))
        {
          var box=obj.Geometry.GetBoundingBox(true);double reach=box.Diagonal.Length;
          if(double.TryParse(obj.Attributes.GetUserString(NotchDataPrefix+"notch_offset"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double offset))reach+=Math.Abs(offset);
          if(box.ClosestPoint(point).DistanceTo(point)>reach+tolerance){copy.Dispose();continue;}
        }
        curve=copy;station=copy.GetLength(new Interval(copy.Domain.T0,parameter));return true;
      }
      return false;
    }
    static NotchMove? BuildNotchMove(RhinoDoc doc,NotchSession s,Guid id,Point3d destination)
    {
      var objects=EditNotchObjects(doc,s,id);var picked=objects.FirstOrDefault(obj=>obj.Id==id);
      if(picked==null||objects.Any(obj=>!obj.IsSelectable(true,false,false,false))||!TryNotchMoveCurve(doc,s,picked,out var reference,out double oldStation))return null;
      using(reference)
      {
        if(!reference!.ClosestPoint(destination,out double parameter))return null;
        double station=reference.GetLength(new Interval(reference.Domain.T0,parameter)),delta=station-oldStation;
        bool percent=picked.Attributes.GetUserString(NotchDataPrefix+"mode")=="percent";
        double referenceLength=reference.GetLength(),fraction=0;var move=new NotchMove{MovementChanged=false};
        var sources=new List<(RhinoObject[] Objects,Curve Curve,double Before,double Total,double Scale)>();
        try
        {
          if(referenceLength<=doc.ModelAbsoluteTolerance)throw new InvalidOperationException("The notch source is too short to move safely.");
          double minimum=-oldStation,maximum=referenceLength-oldStation;
          foreach(var group in objects.GroupBy(obj=>(obj.Attributes.GetUserString(NotchDataPrefix+"curve_key"),MatchingNotches.StationKey(obj.Attributes))))
          {
            var primary=group.FirstOrDefault(IsNotchGeometry)??group.First();
            if(!TryNotchMoveCurve(doc,s,primary,out var source,out double before))throw new InvalidOperationException("A matching notch source is unavailable.");
            double total=source!.GetLength();
            if(total<=doc.ModelAbsoluteTolerance)throw new InvalidOperationException("A matching source is too short to move safely.");
            double scale=(percent?total/referenceLength:1)*NotchMatchDirection(primary)*NotchMatchDirection(picked);
            sources.Add((group.ToArray(),source,before,total,scale));
            double first=-before/scale,last=(total-before)/scale;
            minimum=Math.Max(minimum,Math.Min(first,last));maximum=Math.Min(maximum,Math.Max(first,last));
          }
          delta=Math.Clamp(delta,minimum,maximum);
          double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
          move.MovementChanged=sources.Any(item=>Math.Abs(delta*item.Scale)>tolerance);
          if(move.MovementChanged==false)delta=0;
          fraction=delta/referenceLength;
          foreach(var item in sources)
          {
            var source=item.Curve;double before=item.Before,total=item.Total;
            {
              double after=Math.Clamp(before+delta*item.Scale,0,total);
              GetCurveTangentAndDirection(source,before,"Left",null,null,out var oldTangent,out var oldDirection);
              GetCurveTangentAndDirection(source,after,"Left",null,null,out var tangent,out var direction);
              var oldPoint=PointAtCurveLength(source,before).pt;var point=PointAtCurveLength(source,after).pt;
              var transform=Transform.PlaneToPlane(new Plane(oldPoint,oldTangent,oldDirection),new Plane(point,tangent,direction));
              foreach(var obj in item.Objects)
              {
                var original=CaptureDocObject(doc,obj.Id)??throw new InvalidOperationException("Notch is unavailable.");
                var geometry=original.Geometry.Duplicate();var attributes=original.Attributes.Duplicate();
                move.Objects.Add(new(original,new(geometry,attributes)));
                if(move.MovementChanged==false)continue;
                if(!geometry.Transform(transform))throw new InvalidOperationException("Notch could not be moved.");
                string Number(double value)=>value.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
                string Point(Point3d value)=>string.Join(",",Number(value.X),Number(value.Y),Number(value.Z));
                attributes.SetUserString(NotchDataPrefix+"length_from_start",Number(after));
                attributes.SetUserString(NotchDataPrefix+"percent",Number(percent?after/total:0));
                attributes.SetUserString(NotchDataPrefix+"curve_point",Point(point));
                attributes.SetUserString(NotchDataPrefix+"tangent_hint",Point(new Point3d(tangent)));
                if(attributes.GetUserString(MatchingNotches.CurveReferenceKey)==null)MatchingNotches.BindReference(doc,attributes,source,before);
                MatchingNotches.UpdateStation(attributes,after,total);
              }
            }
          }
          for(int record=0;move.MovementChanged==true&&record<s.NotchRecords.Count;record++)
          {
            var saved=s.NotchRecords[record];var lengths=saved.LengthsFromStart.ToList();bool changed=false;
            for(int index=0;index<s.Curves.Count&&index<lengths.Count;index++)
            {
              Guid notch=record<s.PlacementIds.Count&&index<s.PlacementIds[record].Count?s.PlacementIds[record][index]:Guid.Empty;
              Guid label=record<s.PlacementLabelIds.Count&&index<s.PlacementLabelIds[record].Count?s.PlacementLabelIds[record][index]??Guid.Empty:Guid.Empty;
              var edit=move.Objects.FirstOrDefault(item=>item.Before.Attributes.ObjectId==notch||item.Before.Attributes.ObjectId==label);
              if(edit==null||!MatchingNotches.TryPoint(doc,edit.After.Attributes,"curve_point",out var point))continue;
              ClosestCurveHit(s,point,out int hit,out _,out double length);
              if(hit==index){lengths[index]=length;changed=true;}
            }
            if(changed)move.Records.Add(new(saved,saved.LengthsFromStart.ToList(),lengths,saved.Percent,saved.Percent.HasValue?saved.Percent+fraction:null));
          }
          foreach(var item in move.Objects)item.After.CaptureUserStrings();
          return move;
        }
        catch(Exception ex){Log.Write(Section,"notch move unavailable: {0}",ex.Message);move.Dispose();return null;}
        finally{foreach(var item in sources)item.Curve.Dispose();}
      }
    }
    static bool ApplyNotchMove(RhinoDoc doc,NotchSession s,NotchMove move,bool forward)
    {
      if(move.MovementChanged==false)return true;
      var expected=move.Objects.Select(item=>(Snapshot:forward?item.Before:item.After,Exists:forward?item.BeforeExists:item.AfterExists)).ToArray();
      if(expected.Any(item=>item.Exists?(doc.Objects.FindId(item.Snapshot.Attributes.ObjectId) is not {IsDeleted:false} obj||
          !obj.IsSelectable(true,false,false,false)||obj.Geometry.DataCRC(0)!=item.Snapshot.Geometry.DataCRC(0)||!item.Snapshot.HasUserStrings(obj.Attributes)):
          doc.Objects.FindId(item.Snapshot.Attributes.ObjectId) is {IsDeleted:false}))return false;
      bool Set(DocObjectSnapshot snapshot,bool exists)
      {
        var id=snapshot.Attributes.ObjectId;var current=doc.Objects.FindId(id);
        if(!exists)return current is not {IsDeleted:false}||doc.Objects.Delete(id,true);
        if(move.AttributesOnly)
        {
          if(current is not {IsDeleted:false})return false;
          // Start from the live attributes so Rhino 9 recognizes the restored user-data edit after Undo.
          using var metadata=current.Attributes.Duplicate();snapshot.RestoreUserStrings(metadata);
          return doc.Objects.ModifyAttributes(id,metadata,true);
        }
        using var attributes=snapshot.RestoreAttributes();using var geometry=snapshot.Geometry.Duplicate();
        if(current is not {IsDeleted:false}&&snapshot.Serial!=0&&doc.Objects.Undelete(snapshot.Serial))current=doc.Objects.FindId(id);
        if(current is not {IsDeleted:false})
        {
          Guid added=geometry switch{TextEntity text=>doc.Objects.AddText(text,attributes),TextDot dot=>doc.Objects.AddTextDot(dot,attributes),Curve curve=>doc.Objects.AddCurve(curve,attributes),_=>Guid.Empty};
          if(added!=id){if(added!=Guid.Empty)doc.Objects.Delete(added,true);return false;}
          snapshot.Serial=doc.Objects.FindId(id).RuntimeSerialNumber;return true;
        }
        bool replaced=geometry switch{Curve curve=>doc.Objects.Replace(id,curve),TextEntity text=>doc.Objects.Replace(id,text),TextDot dot=>doc.Objects.Replace(id,dot),_=>false};
        bool success=replaced&&doc.Objects.ModifyAttributes(id,attributes,true);
        if(success&&doc.Objects.FindId(id) is {} restored&&!snapshot.HasUserStrings(restored.Attributes))
        {
          // Replacing geometry can retain live user data despite a successful attribute restore.
          using var metadata=restored.Attributes.Duplicate();snapshot.RestoreUserStrings(metadata);
          success=doc.Objects.ModifyAttributes(id,metadata,true)&&doc.Objects.FindId(id) is {} verified&&snapshot.HasUserStrings(verified.Attributes);
        }
        if(success)snapshot.Serial=doc.Objects.FindId(id).RuntimeSerialNumber;return success;
      }
      int applied=0;
      foreach(var item in move.Objects)
      {
        applied++;
        if(Set(forward?item.After:item.Before,forward?item.AfterExists:item.BeforeExists))continue;
        foreach(var before in expected.Take(applied))Set(before.Snapshot,before.Exists);return false;
      }
      foreach(var edit in move.Records){edit.Record.LengthsFromStart=(forward?edit.After:edit.Before).ToList();edit.Record.Percent=forward?edit.AfterPercent:edit.BeforePercent;}
      if(move.Records.Any(edit=>edit.Record.OffsetSplits.Count>0)&&!RefreshOffsetSplits(doc,s))
      {
        foreach(var before in expected)Set(before.Snapshot,before.Exists);
        foreach(var edit in move.Records){edit.Record.LengthsFromStart=(forward?edit.Before:edit.After).ToList();edit.Record.Percent=forward?edit.BeforePercent:edit.AfterPercent;}
        return false;
      }
      SyncEditedNotchRecords(doc,s,move);
      if(s.EditMode&&s.EditedNotchObjects.Count>0)
      {
        foreach(var item in move.Objects)
        {
          Guid id=item.Before.Attributes.ObjectId;s.EditedNotchObjects.Remove(id);
          if(doc.Objects.FindId(id) is {IsDeleted:false})s.EditedNotchObjects.Add(id);
          Guid added=item.After.Attributes.ObjectId;
          if(doc.Objects.FindId(added) is {IsDeleted:false})s.EditedNotchObjects.Add(added);
        }
      }
      s.PreviewValid=false;s.Panel?.UpdateUndoEnabled();doc.Views.Redraw();return true;
    }
    static void MoveNotchHistory(RhinoDoc doc,NotchSession s,bool redo)
    {
      var from=redo?s.MoveRedo:s.Moves;var to=redo?s.Moves:s.MoveRedo;
      if(!from.TryPeek(out var move))return;
      if(!ApplyNotchMove(doc,s,move,redo)){RhinoApp.WriteLine("vNotches: moved notches changed or are locked; history was not applied.");return;}
      from.Pop();to.Push(move);
      if(s.EditMode&&doc.Objects.FindId(s.EditedNotchId) is {} current&&move.Objects.Any(item=>
          item.Before.Attributes.GetUserString(NotchDataPrefix+"mode")!=item.After.Attributes.GetUserString(NotchDataPrefix+"mode")))
        s.PercentToggle.CurrentValue=current.Attributes.GetUserString(NotchDataPrefix+"mode")=="percent";
      SyncPanelFromOptions(s);
      if(s.EditMode&&s.EditedNotchId!=Guid.Empty&&move.Objects.Any(item=>
          item.Before.Attributes.GetUserString(NotchMatchSetKey)!=item.After.Attributes.GetUserString(NotchMatchSetKey)))
      {
        var selected=s.SelectedNotches.Where(id=>id!=s.EditedNotchId).ToArray();
        SelectNotchForEdit(doc,s,s.EditedNotchId,"select");foreach(var id in selected)SelectNotchForEdit(doc,s,id,"add");
      }
      RefreshEditedNotchPosition(doc,s);
    }
    sealed class MovingNotchCulling(HashSet<uint> serials):DisplayConduit,IDisposable
    {protected override void ObjectCulling(CullObjectEventArgs e){if(serials.Contains(e.RhinoObjectSerialNumber))e.CullObject=true;}public void Dispose(){Enabled=false;}}
    static void ConfigureNotchPicking(GetPoint getter,bool editing)
    {
      getter.PermitObjectSnap(!editing);getter.EnableObjectSnapCursors(!editing);
      getter.PermitOrthoSnap(!editing);getter.PermitTabMode(!editing);
      getter.SetCursor(Rhino.UI.CursorStyle.CrossHair);
      if(editing)getter.EnableSnapToCurves(false);
    }
    static void ConfigureNotchMoveInput(GetPoint getter)
    {
      getter.ClearConstraints();getter.ClearConstructionPoints();
      getter.PermitObjectSnap(true);getter.EnableObjectSnapCursors(true);
      getter.PermitOrthoSnap(true);getter.PermitTabMode(true);getter.SetCursor(Rhino.UI.CursorStyle.CrossHair);
    }
    static void DragExistingNotch(RhinoDoc doc,NotchSession s,Guid id)
    {
      if(doc.Objects.FindId(id) is not {} picked||!TryNotchMoveCurve(doc,s,picked,out var curve,out double station))
      {Log.Write(Section,"edit move source unavailable: notch={0}",id);RhinoApp.WriteLine("vNotches: the notch's source curve is unavailable; it cannot be dragged safely.");return;}
      using(curve)
      using(var getter=new GetPoint())
      using(var highlight=new PreviewDisplay.ObjectHighlighter(doc,ActiveNotchStyle){RedrawOnChange=false,DrawOnTop=true,AutomaticForegroundDrawing=false})
      using(var relatedHighlight=new PreviewDisplay.ObjectHighlighter(doc,MatchedNotchStyle){RedrawOnChange=false,DrawOnTop=true,AutomaticForegroundDrawing=false})
      using(var hidden=new MovingNotchCulling(EditNotchObjects(doc,s,id).Select(obj=>obj.RuntimeSerialNumber).ToHashSet()))
      {
        NotchMove? preview=null;bool committed=false,valid=false,mouseDrag=s.NotchMouseDrag,moved=!mouseDrag,drawingMove=false;
        var local=PhysicalNotchObjects(EditNotchObjects(doc,s,id),picked).Select(obj=>obj.Id).ToHashSet();
        double total=curve!.GetLength(),current=station;Point3d feedbackPoint=PointAtCurveLength(curve,station).pt,cursorPoint=feedbackPoint;
        int matchedDirection=NotchMatchDirection(picked);
        double DisplayStation(double value)=>matchedDirection<0?total-value:value;
        void Update(Point3d point)
        {
          var next=BuildNotchMove(doc,s,id,point);valid=next!=null;if(next==null)return;
          highlight.SetObjects([]);relatedHighlight.SetObjects([]);preview?.Dispose();preview=next;
          if(next!=null)
          {
            var geometry=next.Objects.Where(item=>local.Contains(item.After.Attributes.ObjectId)).ToDictionary(_=>Guid.NewGuid(),item=>item.After.Geometry);highlight.SetObjects(geometry.Keys,geometry);
            var related=next.Objects.Where(item=>!local.Contains(item.After.Attributes.ObjectId)).ToDictionary(_=>Guid.NewGuid(),item=>item.After.Geometry);relatedHighlight.SetObjects(related.Keys,related);
          }
          var attributes=next!.Objects.First(item=>item.After.Attributes.ObjectId==id).After.Attributes;
          current=NotchNumber(attributes,"length_from_start",station);
          if(MatchingNotches.TryPoint(doc,attributes,"curve_point",out var updated))feedbackPoint=updated;
          s.Panel?.UpdateDragPosition(DisplayStation(current),total,matchedDirection*(current-station));
        }
        s.NotchMovePointerUpdated=false;s.ExistingNotchDragging=true;
        try
        {
          getter.SetCommandPrompt(mouseDrag?"Drag matched notches; release to place, Alt disables snaps, Esc cancels":
            HasNotchPairData(picked)?"Move matched notches; click to place, Esc to cancel":"Move notch (pairing data unavailable); click to place, Esc to cancel");
          ConfigureNotchMoveInput(getter);
          Update(PointAtCurveLength(curve!,station).pt);
          if(preview==null){RhinoApp.WriteLine("vNotches: a matched notch or source is unavailable or locked; no notches were moved.");return;}
          getter.MouseMove+=(_,e)=>
          {
            if(!s.NotchMovePointerUpdated)return;
            cursorPoint=e.Point;s.LastCursorPoint=e.Point;
            moved|=s.NotchDragMoved;if(!moved)return;
            Update(e.Point);
            if(!drawingMove&&valid&&preview?.MovementChanged==true)
            {
              drawingMove=true;hidden.Enabled=true;s.EditPicking?.BeginMoveFeedback();doc.Views.Redraw();
            }
          };
          getter.DynamicDraw+=(_,e)=>
          {
            s.EditPicking?.DrawFeedback(e);
            if(!drawingMove)return;
            highlight.DrawForegroundHighlights(e);relatedHighlight.DrawForegroundHighlights(e);
            if(preview!=null)
              foreach(var item in preview.Objects.Where(item=>item.After.Geometry is Curve&&
                  (item.After.Attributes.Name==NotchObjectName||item.After.Attributes.GetUserString(NotchDataPrefix+"object_role")=="notch")))
                NotchVisibilityCues.Ring(e,item.After.Geometry,local.Contains(item.After.Attributes.ObjectId)?ActiveNotchStyle:MatchedNotchStyle);
            if(feedbackPoint.DistanceTo(cursorPoint)>doc.ModelAbsoluteTolerance)e.Display.DrawDottedLine(feedbackPoint,cursorPoint,MoveGuideColor);
            DrawNotchMovePosition(e,feedbackPoint,DisplayStation(current),total,matchedDirection*(current-station));
          };
          s.EditPicking?.ArmMoveFeedback();
          doc.Views.Redraw();
          if(getter.Get(mouseDrag)==GetResult.Point&&moved&&valid&&preview?.MovementChanged==true&&CommitNotchEdit(doc,s,preview))committed=true;
        }
        finally{highlight.SetObjects([]);relatedHighlight.SetObjects([]);if(!committed){preview?.Dispose();RefreshEditedNotchPosition(doc,s);}s.ExistingNotchDragging=false;hidden.Enabled=false;s.EditPicking?.EndMoveFeedback();doc.Views.Redraw();}
      }
    }
    static string NotchMovePositionText(double current,double total,double delta)=>
      $"Start {FormatPanelNumber(current)}\nEnd {FormatPanelNumber(Math.Max(0,total-current))}\nMove {delta.ToString("+0.###;-0.###;0",System.Globalization.CultureInfo.InvariantCulture)}";
    static void DrawNotchMovePosition(GetPointDrawEventArgs e,Point3d point,double current,double total,double delta)
    {
      string text=NotchMovePositionText(current,total,delta);var screen=e.Viewport.WorldToClient(point);
      var bounds=e.Display.Measure2dText(text,Point2d.Origin,false,0,ConfiguredMovePositionTextPixels,"Arial");
      var size=e.Viewport.Size;
      var origin=new Point2d(Math.Clamp(screen.X+ConfiguredMovePositionOffsetPixels,0,Math.Max(0,size.Width-bounds.Width-1)),
        Math.Clamp(screen.Y+ConfiguredMovePositionOffsetPixels,bounds.Height,Math.Max(bounds.Height,size.Height-1)));
      e.Display.Draw2dText(text,MovePositionHaloColor,new Point2d(origin.X+1,origin.Y+1),false,ConfiguredMovePositionTextPixels);
      e.Display.Draw2dText(text,MovePositionTextColor,origin,false,ConfiguredMovePositionTextPixels);
    }
    static void RefreshEditedNotchPosition(RhinoDoc doc,NotchSession s)
    {
      if(doc.Objects.FindId(s.EditedNotchId) is {} obj&&TryNotchMoveCurve(doc,s,obj,out var source,out double station))
      {using(source){double total=source!.GetLength();s.Panel?.UpdateDragPosition(NotchMatchDirection(obj)<0?total-station:station,total,0);}}
      else UpdateDistanceLabels(s,null,null,null,null,null,null);
    }

    enum NotchEditTool { None,Apply,Matches,Unlink,Repair,Associate,AddLabel,RemoveLabel }
    sealed record NotchEditRequest(string Action,Guid ObjectId=default);
    static NotchEditTool? ToolForAction(string action)=>action switch
    {
      "apply"=>NotchEditTool.Apply,"matches"=>NotchEditTool.Matches,"unlink"=>NotchEditTool.Unlink,
      "repair-nest"=>NotchEditTool.Repair,"assign-source"=>NotchEditTool.Associate,
      "labels-add"=>NotchEditTool.AddLabel,"labels-remove"=>NotchEditTool.RemoveLabel,_=>null
    };
    static string NotchToolCaption(NotchEditTool tool)=>tool switch
    {
      NotchEditTool.Apply=>"Apply notch settings",NotchEditTool.Matches=>"Select matches",NotchEditTool.Unlink=>"Unlink notches",
      NotchEditTool.Repair=>"Repair notch references",NotchEditTool.Associate=>"Associate source curves",
      NotchEditTool.AddLabel=>"Add/update labels",NotchEditTool.RemoveLabel=>"Remove labels",_=>"Edit notches"
    };
    static void SetNotchTool(RhinoDoc doc,NotchSession s,NotchEditTool tool)
    {
      s.EditMode=true;s.EditTool=tool;s.RepairPairing=false;s.LinkAnchor=Guid.Empty;
      Log.Write(Section,"edit tool active={0} current={1}",tool,s.EditedNotchId);
      s.EditPicking?.CancelPendingGesture();s.Panel?.UpdateEditMode();s.EditPicking?.RefreshAssociations();doc.Views.Redraw();
    }
    static void ToggleNotchTool(RhinoDoc doc,NotchSession s,NotchEditTool tool)
    {
      bool stop=s.EditTool==tool;
      SetNotchTool(doc,s,stop?NotchEditTool.None:tool);
      if(!stop&&s.EditedNotchId!=Guid.Empty)RunNotchTool(doc,s);
    }
    static void PickNotchForTool(RhinoDoc doc,NotchSession s,Guid id)
    {
      if(doc.Objects.FindId(id) is not {IsDeleted:false} picked||!IsNotchGeometry(picked))return;
      bool retainStyle=s.EditTool is NotchEditTool.Apply or NotchEditTool.AddLabel or NotchEditTool.RemoveLabel;
      SelectNotchForEditCore(doc,s,id,"select",!retainStyle);
      RunNotchTool(doc,s);
    }
    static void RunNotchTool(RhinoDoc doc,NotchSession s)
    {
      if(s.EditedNotchId==Guid.Empty)return;
      switch(s.EditTool)
      {
        case NotchEditTool.Apply:EditStoredNotches(doc,s);break;
        case NotchEditTool.Matches:ShowNotchMatches(doc,s,s.EditedNotchId);break;
        case NotchEditTool.Unlink:UnlinkEditedNotch(doc,s);break;
        case NotchEditTool.Repair:RepairSelectedNotchLinks(doc,s);break;
        case NotchEditTool.Associate:PickNotchSource(doc,s);break;
        case NotchEditTool.AddLabel:EditStoredNotchLabels(doc,s,true);break;
        case NotchEditTool.RemoveLabel:
          if(EditNotchObjects(doc,s).Any(obj=>obj.Geometry is TextEntity or TextDot))EditStoredNotchLabels(doc,s,false);
          break;
      }
      s.Panel?.UpdateEditMode();s.EditPicking?.RefreshAssociations();doc.Views.Redraw();
    }
    static void ShowNotchMatches(RhinoDoc doc,NotchSession s,Guid id)
    {
      LoadNotchForEdit(doc,s,id);
      var members=EditNotchObjects(doc,s);
      var physical=members.Where(IsNotchGeometry).GroupBy(obj=>(obj.Attributes.GetUserString(NotchDataPrefix+"curve_key"),MatchingNotches.StationKey(obj.Attributes))).Select(group=>group.First().Id).ToArray();
      s.SelectedNotches.Clear();s.SelectedNotches.UnionWith(physical);
      doc.Objects.UnselectAll();s.Panel?.UpdateEditMode();s.EditPicking?.RefreshAssociations();doc.Views.Redraw();
      RhinoApp.WriteLine($"vNotches: {CountText.Format(Math.Max(0,physical.Length-1), "matching notch position")} selected.");
    }

    static Guid[] StoredSourceCurves(RhinoObject obj)
    {
      try
      {
        if(obj.Attributes.GetUserString(MatchingNotches.SourceCurveIdsKey) is {} json&&
            System.Text.Json.JsonSerializer.Deserialize<Guid[]>(json) is {Length:>0} ids)return ids;
      }
      catch(System.Text.Json.JsonException ex){Log.Write(Section,"Invalid linked source inventory: {0}",ex.Message);}
      return Guid.TryParse(obj.Attributes.GetUserString(NotchDataPrefix+"curve_id"),out var id)?[id]:[];
    }

    static Guid[] ResolveNotchSources(RhinoDoc doc,RhinoObject obj)
    {
      var saved=StoredSourceCurves(obj);
      if(obj.Attributes.GetUserString(MatchingNotches.CurveReferenceKey)!=null)return saved;
      if(saved.Length==0||obj.Attributes.GetUserString(MatchingNotches.SourceCurveIdsKey)!=null)return saved;
      if(doc.Objects.FindId(saved[0]) is not {} source||source.Geometry is not Curve curve||
          !TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_start"),out var start)||
          !TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_end"),out var end)||
          !TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_mid"),out var middle)||
          !TryStoredNotchPoint(obj.Attributes.GetUserString(NotchDataPrefix+"curve_point"),out var station))return saved;
      double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
      bool On(Curve candidate,Point3d point)=>candidate.ClosestPoint(point,out double parameter)&&candidate.PointAt(parameter).DistanceTo(point)<=tolerance;
      if(On(curve,start)&&On(curve,end)&&On(curve,station))return saved;
      var groups=(source.Attributes.GetGroupList()??[]).Select(index=>doc.Groups.GroupMembers(index)??[])
        .Where(members=>!members.Any(member=>NestTableMetadata.HasDecorationRole(member.Attributes)))
        .OrderBy(members=>members.Length).ToArray();
      var candidates=(groups.FirstOrDefault()??[source]).Where(member=>member.Geometry is Curve&&
        !MatchingNotches.HasMetadata(member.Attributes)&&!NestTableMetadata.HasDecorationRole(member.Attributes)).ToArray();
      var paths=new List<Guid[]>();var path=new List<RhinoObject>();var visited=new HashSet<Guid>();
      void Walk(Point3d point)
      {
        if(paths.Count>1)return;
        if(path.Count>0&&point.DistanceTo(end)<=tolerance)
        {
          if(visited.Contains(source.Id)&&path.Any(member=>On((Curve)member.Geometry,middle))&&path.Any(member=>On((Curve)member.Geometry,station)))
            paths.Add(path.Select(member=>member.Id).ToArray());
          return;
        }
        foreach(var member in candidates)
        {
          if(visited.Contains(member.Id))continue;
          var segment=(Curve)member.Geometry;Point3d next;
          if(segment.PointAtStart.DistanceTo(point)<=tolerance)next=segment.PointAtEnd;
          else if(segment.PointAtEnd.DistanceTo(point)<=tolerance)next=segment.PointAtStart;
          else continue;
          visited.Add(member.Id);path.Add(member);Walk(next);path.RemoveAt(path.Count-1);visited.Remove(member.Id);
          if(paths.Count>1)return;
        }
      }
      Walk(start);
      return paths.Count==1?paths[0]:[];
    }

    static Guid CanonicalNotchMatch(RhinoDoc doc,RhinoObject obj)
    {
      if(!HasNotchPairData(obj))return Guid.Empty;
      if(Guid.TryParse(obj.Attributes.GetUserString(MatchingNotches.SourceMatchKey),out var root))return root;
      Guid local=Guid.Parse(obj.Attributes.GetUserString(NotchMatchSetKey)!);
      if(obj.Attributes.GetUserString("vNest.sourceObjects") is not {} inventory)return local;
      // Older nests lack provenance. Accept only a unique source-inventory correspondence
      // with the same saved station and congruent source and notch geometry.
      try
      {
        var sources=System.Text.Json.JsonSerializer.Deserialize<Guid[]>(inventory)??[];
        var matches=sources.Select(doc.Objects.FindId).Where(original=>original is {IsDeleted:false}&&HasNotchPairData(original)&&
          original.Attributes.GetUserString(NotchDataPrefix+"object_role")==obj.Attributes.GetUserString(NotchDataPrefix+"object_role")&&
          original.Id!=obj.Id&&VerifiedNotchCopy(doc,original,obj)).Select(original=>
            Guid.TryParse(original.Attributes.GetUserString(MatchingNotches.SourceMatchKey),out var canonical)?canonical:
            Guid.Parse(original.Attributes.GetUserString(NotchMatchSetKey)!)).Where(id=>id!=Guid.Empty).Distinct().ToArray();
        if(matches.Length==1)return matches[0];
      }
      catch(System.Text.Json.JsonException ex){Log.Write(Section,"Invalid nested source inventory: {0}",ex.Message);}
      return local;
    }

    static bool VerifiedNotchCopy(RhinoDoc doc,RhinoObject original,RhinoObject copy)
    {
      double tolerance=Math.Max(doc.ModelAbsoluteTolerance,RhinoMath.ZeroTolerance);
      double station=NotchNumber(original.Attributes,"length_from_start",double.NaN),copyStation=NotchNumber(copy.Attributes,"length_from_start",double.NaN);
      if(!double.IsFinite(station)||!double.IsFinite(copyStation)||Math.Abs(station-copyStation)>tolerance)return false;
      if(!Guid.TryParse(original.Attributes.GetUserString(NotchDataPrefix+"curve_id"),out var sourceId)||
          !Guid.TryParse(copy.Attributes.GetUserString(NotchDataPrefix+"curve_id"),out var targetId)||
          doc.Objects.FindId(sourceId)?.Geometry is not Curve source||doc.Objects.FindId(targetId)?.Geometry is not Curve target||
          Math.Abs(source.GetLength()-target.GetLength())>tolerance||
          !MatchingNotches.TryPoint(doc,original.Attributes,"curve_point",out var from)||
          !MatchingNotches.TryPoint(doc,copy.Attributes,"curve_point",out var to))return false;
      var x=source.TangentAtStart;var y=target.TangentAtStart;
      var normal=source.TryGetPlane(out var plane,tolerance)?plane.Normal:Vector3d.ZAxis;
      var targetNormal=target.TryGetPlane(out plane,tolerance)?plane.Normal:Vector3d.ZAxis;
      var frame=new Plane(source.PointAtStart,x,Vector3d.CrossProduct(normal,x));
      foreach(int sign in new[]{1,-1})
      {
        var destination=new Plane(target.PointAtStart,y,sign*Vector3d.CrossProduct(targetNormal,y));
        var transform=Transform.PlaneToPlane(frame,destination);
        if((transform*from).DistanceTo(to)>tolerance)continue;
        bool same=true;
        for(int index=0;index<=ConfiguredEditPickSamples&&same;index++)
        {
          double fraction=(double)index/ConfiguredEditPickSamples;
          same=(transform*source.PointAtNormalizedLength(fraction)).DistanceTo(target.PointAtNormalizedLength(fraction))<=tolerance;
          if(original.Geometry is Curve a&&copy.Geometry is Curve b)
            same&=(transform*a.PointAtNormalizedLength(fraction)).DistanceTo(b.PointAtNormalizedLength(fraction))<=tolerance;
        }
        if(same)return true;
      }
      return false;
    }

    static RhinoObject[] EditNotchObjects(RhinoDoc doc,NotchSession s,Guid fallback=default)=>
      s.EditMode&&s.EditedNotchObjects.Count>0?s.EditedNotchObjects.Select(doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false}).ToArray():
      NotchMoveObjects(doc,fallback==Guid.Empty?s.EditedNotchId:fallback);

    static RhinoObject[] NotchEditFamilies(RhinoDoc doc,IEnumerable<Guid> selection)
    {
      var selected=selection.Distinct().Where(id=>doc.Objects.FindId(id) is {IsDeleted:false}).ToArray();
      var roots=selected.Select(id=>CanonicalNotchMatch(doc,doc.Objects.FindId(id))).Where(id=>id!=Guid.Empty).ToHashSet();
      var members=selected.SelectMany(id=>NotchMoveObjects(doc,id)).DistinctBy(obj=>obj.Id).ToDictionary(obj=>obj.Id,obj=>obj);
      if(roots.Count>0)
        foreach(var obj in doc.Objects.GetObjectList(ObjectType.AnyObject).Where(obj=>!obj.IsDeleted&&RemovableNotchObject(obj)&&HasNotchPairData(obj)))
          if(roots.Contains(CanonicalNotchMatch(doc,obj)))members[obj.Id]=obj;
      return members.Values.ToArray();
    }

    static void SelectNotchForEdit(RhinoDoc doc,NotchSession s,Guid id,string action)
      =>SelectNotchForEditCore(doc,s,id,action,true);
    static void SelectNotchForEditCore(RhinoDoc doc,NotchSession s,Guid id,string action,bool loadStyle)
    {
      if(doc.Objects.FindId(id) is not {IsDeleted:false} picked)return;
      var family=CanonicalNotchMatch(doc,picked);
      var selected=s.SelectedNotches.Where(key=>doc.Objects.FindId(key) is {IsDeleted:false}).ToList();
      bool Same(Guid key)
      {
        if(key==id)return true;
        if(doc.Objects.FindId(key) is not {} obj)return false;
        if(s.RepairPairing)
          return picked.Attributes.GetUserString(NotchDataPrefix+"curve_key") is {} source&&MatchingNotches.StationKey(picked.Attributes) is {} station&&
            obj.Attributes.GetUserString(NotchDataPrefix+"curve_key")==source&&MatchingNotches.StationKey(obj.Attributes)==station;
        return family!=Guid.Empty&&CanonicalNotchMatch(doc,obj)==family;
      }
      if(action=="deselect")selected.RemoveAll(Same);
      else
      {
        if(action!="add")selected.Clear();
        if(!selected.Any(Same))selected.Add(id);
      }
      s.SelectedNotches.Clear();s.SelectedNotches.UnionWith(selected);
      s.EditedNotchObjects.Clear();
      s.EditedNotchObjects.UnionWith(NotchEditFamilies(doc,selected).Select(obj=>obj.Id));
      s.EditMode=true;s.EditedNotchId=action=="deselect"?selected.LastOrDefault():id;
      while(s.Curves.Count>0)
      {int index=s.Curves.Count-1;s.Curves[index].Dispose();foreach(var segment in s.PerCurveSegments[index])segment.Dispose();RemoveSessionCurve(s,index);}
      s.CurveSideBySource.Clear();s.CurveBothSidesBySource.Clear();s.CurveReversedBySource.Clear();s.ClearPlacementRuns();
      var displayedPaths=new HashSet<string>();
      foreach(var obj in EditNotchObjects(doc,s))
      {
        var parentIds=ResolveNotchSources(doc,obj).Where(key=>doc.Objects.FindId(key)?.Geometry is Curve).Distinct().ToArray();
        if(parentIds.Length==0)continue;
        var ids=parentIds.ToArray();
        var segmentList=new List<Curve>();var displayIds=new List<Guid>();
        bool relative=MatchingNotches.TryReferenceSections(doc,obj.Attributes,out var sections);
        if(!relative&&obj.Attributes.GetUserString(MatchingNotches.CurveReferenceKey)!=null)continue;
        if(!relative&&parentIds.Length==1&&TryNotchMoveCurve(doc,s,obj,out var legacy,out double station))
        {
          using(legacy)using(var view=obj.Attributes.Duplicate())
            if(MatchingNotches.BindReference(doc,view,legacy!,station))relative=MatchingNotches.TryReferenceSections(doc,view,out sections);
        }
        if(relative)
        {
          foreach(var sourceId in parentIds)
          {
            var matching=sections.Where(section=>section.Id==sourceId).ToArray();
            if(matching.Length==0){displayIds.Add(sourceId);segmentList.Add(((Curve)doc.Objects.FindId(sourceId).Geometry).DuplicateCurve());}
            foreach(var section in matching)
            {displayIds.Add(section.Whole?sourceId:s.Sources.RegisterSection(sourceId,section.Key,section.Reversed));segmentList.Add(section.Geometry);}
          }
          foreach(var section in sections.Where(section=>!parentIds.Contains(section.Id)))section.Geometry.Dispose();
          ids=displayIds.ToArray();
        }
        else segmentList.AddRange(ids.Select(key=>((Curve)doc.Objects.FindId(key).Geometry).DuplicateCurve()));
        var segments=segmentList.ToArray();
        string path=string.Join("|",ids);
        if(!displayedPaths.Add(path)){foreach(var segment in segments)segment.Dispose();continue;}
        bool[]? reversed=null;
        try{if(obj.Attributes.GetUserString(NotchDataPrefix+"source_reversed") is {} json)reversed=System.Text.Json.JsonSerializer.Deserialize<bool[]>(json);}
        catch(System.Text.Json.JsonException ex){Log.Write(Section,"Invalid source directions: {0}",ex.Message);}
        if(relative&&reversed?.Length==parentIds.Length)
          for(int index=0;index<ids.Length;index++)
          {
            Guid parent=s.Sources.ParentId(ids[index]);int original=Array.IndexOf(parentIds,parent);
            if(original>=0&&reversed[original]&&!sections.Any(section=>section.Id==parent))segments[index].Reverse();
          }
        if(!relative&&reversed?.Length==segments.Length)
        {for(int i=0;i<segments.Length;i++)if(reversed[i])segments[i].Reverse();}
        else if(!relative)
        {
          if(MatchingNotches.TryPoint(doc,obj.Attributes,"curve_start",out var start)&&
              segments[0].PointAtEnd.DistanceTo(start)<segments[0].PointAtStart.DistanceTo(start))segments[0].Reverse();
          for(int i=1;i<segments.Length;i++)
            if(segments[i].PointAtEnd.DistanceTo(segments[i-1].PointAtEnd)<segments[i].PointAtStart.DistanceTo(segments[i-1].PointAtEnd))segments[i].Reverse();
        }
        bool opposite=NotchMatchDirection(obj)<0;
        if(opposite){Array.Reverse(ids);Array.Reverse(segments);foreach(var segment in segments)segment.Reverse();}
        var curve=BuildLayoutCurve(doc,segments.ToList(),out bool continuous);
        AddSessionCurve(s,s.Sources.FindObject(doc,ids[0])!,curve,ids,segments,continuous);
        for(int index=0;index<ids.Length;index++)
        {
          Guid key=ids[index];s.CurveSideBySource[key]=(obj.Attributes.GetUserString(NotchDataPrefix+"curve_side")=="Left")^opposite;
          s.CurveBothSidesBySource[key]=obj.Attributes.GetUserString(NotchDataPrefix+"both_sides")=="1";
          if(s.Sources.SectionKey(key)!=null)s.CurveReversedBySource[key]=s.Sources.SectionReversed(key)^opposite;
          else if(s.Sources.FindObject(doc,key)?.Geometry is Curve source)s.CurveReversedBySource[key]=source.PointAtEnd.DistanceTo(segments[index].PointAtStart)<source.PointAtStart.DistanceTo(segments[index].PointAtStart);
        }
        foreach(var segment in segments)segment.Dispose();
        UpdateLogicalCurveSide(s,s.Curves.Count-1);
      }
      ClearNotchSourceSelection(doc,s.PerCurveSourceIds.SelectMany(ids=>ids).Select(s.Sources.ParentId));
      s.PreviewValid=false;
      if(s.EditedNotchId!=Guid.Empty&&loadStyle)LoadNotchStyleForEdit(doc,s,s.EditedNotchId);
      else {RefreshPanelForCurves(s);s.Panel?.UpdateEditMode();}
      doc.Views.Redraw();
    }
    static double NotchNumber(ObjectAttributes attributes,string key,double fallback)=>double.TryParse(attributes.GetUserString(NotchDataPrefix+key),
      System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double value)&&double.IsFinite(value)?value:fallback;
    static NotchRecord ReadNotchStyle(ObjectAttributes attributes,int curves)
    {
      string Text(string key,string fallback)=>attributes.GetUserString(NotchDataPrefix+key)??fallback;
      return new NotchRecord
      {
        Mode=Text("mode","distance"),NotchLength=NotchNumber(attributes,"notch_length",ConfiguredDefaultNotchLength),
        NotchOffset=NotchNumber(attributes,"notch_offset",ConfiguredDefaultNotchOffset),NotchWidth=NotchNumber(attributes,"notch_width",ConfiguredDefaultNotchWidth),
        NotchType=Text("notch_type",ConfiguredDefaultNotchType),NotchEnabled=Text("notch_enabled","1")=="1",LabelEnabled=Text("label_enabled","0")=="1",
        LabelValues=Enumerable.Repeat(Text("label_value",Text("label_text","")),curves).ToList(),LabelPrefix=Text("label_prefix",ConfiguredDefaultLabelPrefix),
        LabelSize=NotchNumber(attributes,"label_size",ConfiguredDefaultLabelSize),LabelOffset=NotchNumber(attributes,"label_offset",0),
        LabelOffsetY=NotchNumber(attributes,"label_offset_y",ConfiguredDefaultLabelOffsetY),NotchLayer=Text("notch_layer",SpecialLayerCurrent),
        LabelLayer=Text("label_layer",ConfiguredDefaultLabelLayer),MatchSetId=Guid.TryParse(Text("match_set",""),out var match)?match:Guid.NewGuid(),
        BothSidesOverride=Text("both_sides","0")=="1",LabelDirectionOverride=Text("label_side","Right")=="Left"
      };
    }
    static void LoadNotchForEdit(RhinoDoc doc,NotchSession s,Guid id)
      =>SelectNotchForEdit(doc,s,id,"select");

    static void LoadNotchStyleForEdit(RhinoDoc doc,NotchSession s,Guid id)
    {
      if(doc.Objects.FindId(id) is not {IsDeleted:false} picked)return;
      var objects=EditNotchObjects(doc,s,id);if(objects.Length==0)return;
      s.EditMode=true;s.EditedNotchId=id;s.PreviewValid=false;
      var style=ReadNotchStyle(picked.Attributes,s.Curves.Count);
      s.NotchLengthOpt.CurrentValue=style.NotchLength;s.NotchOffsetOpt.CurrentValue=style.NotchOffset;s.NotchWidthOpt.CurrentValue=style.NotchWidth;
      s.NotchTypeIndex=Math.Max(0,Array.IndexOf(s.NotchTypeValues,style.NotchType));s.NotchToggle.CurrentValue=objects.Any(IsNotchGeometry);
      s.BothSidesToggle.CurrentValue=picked.Attributes.GetUserString(NotchDataPrefix+"both_sides")=="1";
      bool hasLabels=objects.Any(obj=>obj.Geometry is TextEntity or TextDot);
      var labelDefaults=MatchingLabels.Read(doc);
      s.LabelToggle.CurrentValue=hasLabels;s.LabelValueText=style.LabelValues.FirstOrDefault()??"";
      s.LabelPrefixText=style.LabelPrefix;
      if(!hasLabels&&!HasLabelText(s.LabelValueText,s.LabelPrefixText))
      {s.LabelValueText=labelDefaults.Value;s.LabelPrefixText=labelDefaults.Prefix;}
      s.ManualLabelSize=style.LabelSize>doc.ModelAbsoluteTolerance?style.LabelSize:
        !hasLabels?Math.Max(labelDefaults.ManualSize,ConfiguredDefaultLabelSize):style.LabelSize;
      s.LabelSizeAutoToggle.CurrentValue=false;
      s.LabelOffsetOpt.CurrentValue=style.LabelOffset;s.LabelOffsetYOpt.CurrentValue=style.LabelOffsetY;
      s.LabelSideFlip=picked.Attributes.GetUserString(NotchDataPrefix+"label_side")=="Left";
      s.NotchLayerName=style.NotchLayer;s.LabelLayerName=style.LabelLayer;s.PercentToggle.CurrentValue=style.Mode=="percent";
      RefreshPanelForCurves(s);s.Panel?.UpdateEditMode();
      RefreshEditedNotchPosition(doc,s);
    }
    static bool CommitNotchEdit(RhinoDoc doc,NotchSession s,NotchMove change)
    {
      if(change.Objects.Count==0||change.MovementChanged==false){change.Dispose();return false;}
      if(!ApplyNotchMove(doc,s,change,true)){change.Dispose();RhinoApp.WriteLine("vNotches: the notches changed or are locked; no edit was applied.");return false;}
      ClearNotchRedo(s);change.Order=++s.HistoryOrder;s.Moves.Push(change);s.Panel?.UpdateUndoEnabled();return true;
    }
    static bool? EditedNotchPercentState(RhinoDoc doc,NotchSession s)
    {
      if(!s.EditMode||s.EditedNotchId==Guid.Empty)return s.PercentToggle.CurrentValue;
      var values=EditNotchObjects(doc,s).Select(obj=>obj.Attributes.GetUserString(NotchDataPrefix+"mode")=="percent").Distinct().Take(2).ToArray();
      return values.Length==2?null:values.Length==1?values[0]:s.PercentToggle.CurrentValue;
    }
    static bool SetEditedNotchPercent(RhinoDoc doc,NotchSession s,bool enabled)
    {
      if(!s.EditMode||s.EditedNotchId==Guid.Empty){s.PercentToggle.CurrentValue=enabled;return true;}
      var objects=EditNotchObjects(doc,s);
      if(objects.Length==0||objects.Any(obj=>!obj.IsSelectable(true,false,false,false)))return false;
      string mode=enabled?"percent":"distance";
      if(objects.All(obj=>obj.Attributes.GetUserString(NotchDataPrefix+"mode")==mode))
      {s.PercentToggle.CurrentValue=enabled;return true;}
      var edit=new NotchMove{AttributesOnly=true};
      foreach(var group in objects.GroupBy(obj=>(obj.Attributes.GetUserString(NotchDataPrefix+"curve_key"),MatchingNotches.StationKey(obj.Attributes))))
      {
        var primary=group.FirstOrDefault(IsNotchGeometry)??group.First();
        if(!TryNotchMoveCurve(doc,s,primary,out var curve,out double station)){edit.Dispose();return false;}
        using(curve)
        {
          double total=curve!.GetLength();
          if(total<=doc.ModelAbsoluteTolerance){edit.Dispose();return false;}
          foreach(var obj in group)
          {
            var before=CaptureDocObject(doc,obj.Id);if(before==null){edit.Dispose();return false;}
            var attributes=before.Attributes.Duplicate();
            attributes.SetUserString(NotchDataPrefix+"mode",mode);
            attributes.SetUserString(NotchDataPrefix+"length_from_start",station.ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString(NotchDataPrefix+"percent",(enabled?station/total:0).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
            if(attributes.GetUserString(MatchingNotches.CurveReferenceKey)==null&&!MatchingNotches.BindReference(doc,attributes,curve,station))
            {before.Geometry.Dispose();before.Attributes.Dispose();attributes.Dispose();edit.Dispose();return false;}
            MatchingNotches.UpdateStation(attributes,station,total);
            edit.Objects.Add(new(before,new(before.Geometry.Duplicate(),attributes)));
          }
        }
      }
      if(!CommitNotchEdit(doc,s,edit))return false;
      s.PercentToggle.CurrentValue=enabled;
      return true;
    }

    static bool RepairNestedNotchLinks(RhinoDoc doc,NotchSession s)
      =>RepairNotchLinks(doc,s,null);
    static bool RepairSelectedNotchLinks(RhinoDoc doc,NotchSession s)
      =>RepairNotchLinks(doc,s,EditNotchObjects(doc,s).Select(obj=>obj.Id).ToHashSet());
    static bool RepairNotchLinks(RhinoDoc doc,NotchSession s,HashSet<Guid>? selection)
    {
      using var plan=NestWorkflow.PlanNotchLinkRepair(doc);
      var roots=selection==null?null:selection.Select(id=>doc.Objects.FindId(id)?.Attributes.GetUserString(MatchingNotches.SourceMemberKey))
        .Concat(plan.Changes.Where(item=>selection.Contains(item.Id)).Select(item=>item.Attributes.GetUserString(MatchingNotches.SourceMemberKey)))
        .Where(value=>Guid.TryParse(value,out _)).Select(value=>Guid.Parse(value!)).Concat(selection).ToHashSet();
      var matches=selection==null?null:selection.Select(id=>doc.Objects.FindId(id)?.Attributes.GetUserString(MatchingNotches.SourceMatchKey))
        .Concat(plan.Changes.Where(item=>selection.Contains(item.Id)).Select(item=>item.Attributes.GetUserString(MatchingNotches.SourceMatchKey)))
        .Where(value=>Guid.TryParse(value,out _)).Select(value=>Guid.Parse(value!)).ToHashSet();
      var change=new NotchMove{AttributesOnly=true};
      foreach(var item in plan.Changes)
      {
        if(selection!=null&&!selection.Contains(item.Id)&&
            !(Guid.TryParse(item.Attributes.GetUserString(MatchingNotches.SourceMemberKey),out var source)&&roots!.Contains(source))&&
            !(Guid.TryParse(item.Attributes.GetUserString(MatchingNotches.SourceMatchKey),out var match)&&matches!.Contains(match)))continue;
        var before=CaptureDocObject(doc,item.Id);
        if(before==null){change.Dispose();return false;}
        change.Objects.Add(new(before,new DocObjectSnapshot(before.Geometry.Duplicate(),item.Attributes.Duplicate())));
      }
      bool success=change.Objects.Count>0&&CommitNotchEdit(doc,s,change);
      if(change.Objects.Count==0)change.Dispose();
      RhinoApp.WriteLine("vNotches: {0}",selection!=null?success?$"repaired {CountText.Format(change.Objects.Count, "notch/label reference")}.":"No verified source-reference changes were found for the selected notch.":success?plan.Summary.Replace("prepared","repaired"):plan.Changes.Count==0?plan.Summary:"No links changed; repair could not be applied.");
      s.EditMode=true;s.EditedNotchId=Guid.Empty;s.SelectedNotches.Clear();s.EditedNotchObjects.Clear();
      s.Panel?.UpdateEditMode();doc.Views.Redraw();return success;
    }
    static bool AssignNotchSource(RhinoDoc doc,NotchSession s,Guid sourceId)
      =>AssignNotchSourceCurve(doc,s,sourceId,null);
    static bool AssignNotchSourceCurve(RhinoDoc doc,NotchSession s,Guid sourceId,Curve? selectedCurve)
      =>AssignNotchSourcePath(doc,s,sourceId,selectedCurve,null);
    static bool AssignNotchSourcePath(RhinoDoc doc,NotchSession s,Guid sourceId,Curve? selectedCurve,IReadOnlyList<(Guid Id,Curve Segment)>? path)
    {
      if(doc.Objects.FindId(s.EditedNotchId) is not {IsDeleted:false} picked||doc.Objects.FindId(sourceId) is not {IsDeleted:false,Geometry:Curve} source)return false;
      var physical=PhysicalNotchObjects(EditNotchObjects(doc,s),picked);
      picked=physical.Where(IsNotchGeometry).OrderBy(obj=>obj.Attributes.GetUserString(NotchDataPrefix+"component_index")??"0").FirstOrDefault()??picked;
      if(!MatchingNotches.Reassociate(doc,picked,[source],out var repaired,true,selectedCurve,path))return false;
      using(repaired)
      {
        var edit=new NotchMove{AttributesOnly=true};
        foreach(var obj in physical)
        {
          var before=CaptureDocObject(doc,obj.Id);if(before==null){edit.Dispose();return false;}
          var attributes=before.Attributes.Duplicate();MatchingNotches.CopySourceReference(repaired!,attributes);
          if(obj.Id==picked.Id)
            foreach(string key in new[]{NotchMatchSetKey,NotchMatchMemberKey,MatchingNotches.SourceMatchKey,MatchingNotches.SourceMemberKey})attributes.SetUserString(key,repaired!.GetUserString(key));
          var keys=before.Attributes.GetUserStrings().AllKeys.Concat(attributes.GetUserStrings().AllKeys).Where(key=>key!=null).Distinct().ToArray();
          if(keys.All(key=>before.Attributes.GetUserString(key!)==attributes.GetUserString(key!))){before.Geometry.Dispose();before.Attributes.Dispose();attributes.Dispose();continue;}
          edit.Objects.Add(new(before,new DocObjectSnapshot(before.Geometry.Duplicate(),attributes)));
        }
        if(edit.Objects.Count==0){edit.Dispose();return true;}
        if(!CommitNotchEdit(doc,s,edit))return false;
      }
      LoadNotchForEdit(doc,s,picked.Id);return true;
    }
    static void PickNotchSource(RhinoDoc doc,NotchSession s)
    {
      if(s.EditedNotchId==Guid.Empty)return;
      using var getter=CreateNotchSourcePicker();
      using var feedback=new PreviewDisplay.ObjectHighlighter(doc,AssignedCurveStyle){DrawOnTop=true};
      var geometry=new Dictionary<Guid,GeometryBase>();string[] previous=[];
      using var timer=new UITimer{Interval=ConfiguredRemovalModifierPollSeconds};
      void RefreshSelection()
      {
        var references=Enumerable.Range(0,getter.ObjectCount).Select(index=>getter.Object(index)).ToArray();
        try
        {
          var keys=references.Select(item=>$"{item.ObjectId}:{item.GeometryComponentIndex.ComponentIndexType}:{item.GeometryComponentIndex.Index}").ToArray();
          if(keys.SequenceEqual(previous))return;previous=keys;
          feedback.SetObjects([]);foreach(var item in geometry.Values)item.Dispose();geometry.Clear();
          foreach(var item in references)
          {
            var curve=item.GeometryComponentIndex.Index>=0?NotchSourceSection(doc,item):item.Curve()?.DuplicateCurve();
            if(curve!=null)geometry[Guid.NewGuid()]=curve;
          }
          feedback.SetObjects(geometry.Keys,geometry);
        }
        finally{foreach(var item in references)item.Dispose();}
      }
      timer.Elapsed+=(_,_)=>RefreshSelection();
      s.AssigningSource=true;s.Panel?.UpdateEditMode();s.Panel?.ClearCurveRowFeedback();
      s.EditPicking?.RefreshAssociations();
      try
      {
        timer.Start();var result=getter.GetMultiple(1,0);timer.Stop();
        if(result==GetResult.CustomMessage){s.PendingInput=getter.CustomMessage();return;}
        if(result!=GetResult.Object)
        {if(s.EditTool==NotchEditTool.Associate)SetNotchTool(doc,s,NotchEditTool.None);return;}
        var sources=Enumerable.Range(0,getter.ObjectCount).Select(index=>getter.Object(index)).ToArray();
        try{if(!AssignNotchSourceReferences(doc,s,sources))RhinoApp.WriteLine("vNotches: select one connected source path; association could not be verified, so nothing changed.");}
        finally{foreach(var item in sources)item.Dispose();}
      }
      finally
      {
        timer.Stop();feedback.SetObjects([]);foreach(var item in geometry.Values)item.Dispose();geometry.Clear();
        ClearNotchSourceSelection(doc,Enumerable.Range(0,getter.ObjectCount).Select(index=>getter.Object(index).ObjectId));
        s.AssigningSource=false;s.Panel?.UpdateEditMode();s.EditPicking?.RefreshAssociations();doc.Views.Redraw();
      }
    }
    static bool AssignNotchSourceReference(RhinoDoc doc,NotchSession s,ObjRef reference)
    {
      bool section=reference.GeometryComponentIndex.Index>=0;
      using var curve=section?NotchSourceSection(doc,reference):null;
      return (!section||curve!=null)&&AssignNotchSourceCurve(doc,s,reference.ObjectId,curve);
    }
    static bool AssignNotchSourceReferences(RhinoDoc doc,NotchSession s,IReadOnlyList<ObjRef> references)
    {
      if(references.Count==0)return false;
      if(references.Count==1)return AssignNotchSourceReference(doc,s,references[0]);
      var whole=references.Where(item=>item.GeometryComponentIndex.Index<0).Select(item=>item.ObjectId).ToHashSet();
      var pieces=new List<(Guid Id,Curve Segment)>();
      try
      {
        foreach(var item in references.DistinctBy(item=>(item.ObjectId,item.GeometryComponentIndex)))
        {
          if(item.GeometryComponentIndex.Index>=0&&whole.Contains(item.ObjectId))continue;
          var curve=item.GeometryComponentIndex.Index>=0?NotchSourceSection(doc,item):item.Curve()?.DuplicateCurve();
          if(curve==null||!curve.IsValid){curve?.Dispose();return false;}
          pieces.Add((item.ObjectId,curve));
        }
        if(pieces.Count==0)return false;
        var joined=Curve.JoinCurves(pieces.Select(item=>item.Segment),doc.ModelAbsoluteTolerance,false);
        try
        {
          if(joined.Length!=1)return false;
          var reference=joined[0];
          var ordered=pieces.Select(item=>
          {
            var middle=item.Segment.PointAtNormalizedLength(.5);
            reference.ClosestPoint(middle,out double parameter);
            item.Segment.NormalizedLengthParameter(.5,out double local);
            if(Vector3d.Multiply(reference.TangentAt(parameter),item.Segment.TangentAt(local))<0)item.Segment.Reverse();
            return (item.Id,item.Segment,Station:reference.GetLength(new Interval(reference.Domain.T0,parameter)));
          }).OrderBy(item=>item.Station).Select(item=>(item.Id,item.Segment)).ToArray();
          return AssignNotchSourcePath(doc,s,ordered[0].Id,reference,ordered);
        }
        finally{foreach(var curve in joined)curve.Dispose();}
      }
      finally{foreach(var item in pieces)item.Segment.Dispose();}
    }
    static Curve? NotchSourceSection(RhinoDoc doc,ObjRef reference)
    {
      var component=reference.GeometryComponentIndex;
      if(component.ComponentIndexType==ComponentIndexType.PolycurveSegment&&component.Index>=0&&
          doc.Objects.FindId(reference.ObjectId)?.Geometry is Curve parent)
      {
        if(parent is PolyCurve polycurve&&component.Index<polycurve.SegmentCount)
          return polycurve.SegmentCurve(component.Index)?.DuplicateCurve();
        if(parent.TryGetPolyline(out var polyline)&&component.Index+1<polyline.Count)
          return new LineCurve(polyline[component.Index],polyline[component.Index+1]);
      }
      return reference.Curve()?.DuplicateCurve();
    }
    static GetObject CreateNotchSourcePicker()
    {
      var getter=new GetObject();getter.SetCommandPrompt("Select source curves or sections; Enter confirms");
      getter.GeometryFilter=ObjectType.Curve;getter.GroupSelect=false;getter.EnablePreSelect(false,true);
      getter.SubObjectSelect=true;
      getter.EnableHighlight(false);
      getter.AcceptCustomMessage(true);getter.AcceptNothing(true);
      getter.SetCustomGeometryFilter((obj,_,_)=>!MatchingNotches.HasMetadata(obj.Attributes)&&!NestTableMetadata.HasDecorationRole(obj.Attributes));
      return getter;
    }
    static void ClearNotchSourceSelection(RhinoDoc doc,IEnumerable<Guid> ids)
    {
      foreach(var id in ids.Distinct())
        if(doc.Objects.FindId(id) is {} obj){obj.Select(false);obj.UnselectAllSubObjects();}
    }
    static bool EditStoredNotches(RhinoDoc doc,NotchSession s)=>EditStoredNotchesCore(doc,s,false);
    static bool EditStoredNotchLabels(RhinoDoc doc,NotchSession s,bool enabled)
    {
      s.Panel?.CommitPendingValues();
      var objects=EditNotchObjects(doc,s);
      if(objects.Length==0||!objects.Any(IsNotchGeometry))return false;
      if(enabled&&(!HasLabelText(s.LabelValueText,s.LabelPrefixText)||EffectiveLabelSize(s)<=doc.ModelAbsoluteTolerance))
      {RhinoApp.WriteLine("vNotches: enter label text or a prefix and a label size greater than document tolerance.");return false;}
      s.LabelToggle.CurrentValue=enabled;s.Panel?.SyncFromSession();
      if(!enabled)
      {
        var removal=new NotchMove();
        foreach(var obj in objects)
        {
          var before=CaptureDocObject(doc,obj.Id)!;var attributes=before.RestoreAttributes();
          attributes.SetUserString(NotchDataPrefix+"label_enabled","0");attributes.SetUserString(NotchDataPrefix+"label_id",string.Empty);
          bool retained=obj.Geometry is not (TextEntity or TextDot);
          removal.Objects.Add(new(before,new(before.Geometry.Duplicate(),attributes),true,retained));
        }
        bool removed=CommitNotchEdit(doc,s,removal);
        if(!removed){s.LabelToggle.CurrentValue=objects.Any(obj=>obj.Geometry is TextEntity or TextDot);s.Panel?.SyncFromSession();}
        s.Panel?.UpdateEditMode();return removed;
      }
      bool success=EditStoredNotchesCore(doc,s,true);
      s.Panel?.UpdateEditMode();return success;
    }
    static bool EditStoredNotchesCore(RhinoDoc doc,NotchSession s,bool labelsOnly)
    {
      var objects=EditNotchObjects(doc,s);
      if(objects.Length==0||objects.Any(obj=>!obj.IsSelectable(true,false,false,false)))return false;
      s.Panel?.CommitPendingValues();var edit=new NotchMove();
      if(s.LabelToggle.CurrentValue&&(!HasLabelText(s.LabelValueText,s.LabelPrefixText)||EffectiveLabelSize(s)<=doc.ModelAbsoluteTolerance))
      {RhinoApp.WriteLine("vNotches: enter label text or a prefix and a label size greater than document tolerance.");edit.Dispose();return false;}
      using var temporary=RhinoDoc.CreateHeadless(null);temporary.ModelUnitSystem=doc.ModelUnitSystem;temporary.ModelAbsoluteTolerance=doc.ModelAbsoluteTolerance;
      try
      {
        foreach(var group in objects.GroupBy(obj=>(obj.Attributes.GetUserString(NotchDataPrefix+"curve_key"),MatchingNotches.StationKey(obj.Attributes))))
        {
          var primary=group.FirstOrDefault(IsNotchGeometry)??group.First();
          if(!TryNotchMoveCurve(doc,s,primary,out var curve,out double station))throw new InvalidOperationException("A source curve is unavailable.");
          using(curve)
          {
            Guid match=HasNotchPairData(primary)?Guid.Parse(primary.Attributes.GetUserString(NotchMatchSetKey)!):Guid.NewGuid();
            Guid.TryParse(primary.Attributes.GetUserString(NotchDataPrefix+"curve_id"),out var source);
            string side=primary.Attributes.GetUserString(NotchDataPrefix+"curve_side")??"Left";
            var originalStyle=ReadNotchStyle(primary.Attributes,s.Curves.Count);
            bool notchEnabled=labelsOnly?group.Any(IsNotchGeometry):s.NotchToggle.CurrentValue;
            var created=AddNotch(temporary,curve!,station,labelsOnly?originalStyle.NotchLength:s.NotchLengthOpt.CurrentValue,
              labelsOnly?originalStyle.NotchOffset:s.NotchOffsetOpt.CurrentValue,side,false,
              labelsOnly?originalStyle.NotchType:s.NotchTypeValues[s.NotchTypeIndex],labelsOnly?originalStyle.NotchWidth:s.NotchWidthOpt.CurrentValue,
              notchEnabled,labelsOnly?primary.Attributes.GetUserString(NotchDataPrefix+"both_sides")=="1":s.BothSidesToggle.CurrentValue,
              s.LabelToggle.CurrentValue,s.LabelValueText,EffectiveLabelSize(s),s.NotchLayerName,s.LabelLayerName,s.LabelOffsetOpt.CurrentValue,
              s.LabelOffsetYOpt.CurrentValue,ResolvedLabelCurveSide(s.LabelSideFlip),null,null,source,0,
              labelsOnly||EditedNotchPercentState(doc,s)==null?originalStyle.Mode:s.PercentToggle.CurrentValue?"percent":"distance",s.LabelPrefixText,match);
            if(created.notch==Guid.Empty&&!created.label.HasValue)throw new InvalidOperationException("No notch or label could be generated.");
            var fresh=RelatedComponentObjects(temporary,created.notch).Concat(RelatedComponentObjects(temporary,created.label??Guid.Empty)).DistinctBy(obj=>obj.Id).ToArray();
            var mapping=new Dictionary<Guid,Guid>();
            foreach(bool label in new[]{false,true})
            {
              var old=group.Where(obj=>(obj.Geometry is TextEntity or TextDot)==label).OrderBy(NotchComponentIndex).ToArray();
              var added=fresh.Where(obj=>(obj.Geometry is TextEntity or TextDot)==label).OrderBy(NotchComponentIndex).ToArray();
              for(int i=0;i<added.Length;i++)mapping[added[i].Id]=i<old.Length?old[i].Id:Guid.NewGuid();
              if(labelsOnly&&!label)
              {
                var metadata=fresh.First().Attributes;
                foreach(var obj in old)
                {
                  var before=CaptureDocObject(doc,obj.Id)!;
                  var attributes=before.RestoreAttributes();
                  foreach(string key in new[]{"label_enabled","label_text","label_value","label_prefix","label_size","label_offset","label_offset_y","label_side","label_layer"})
                    attributes.SetUserString(NotchDataPrefix+key,metadata.GetUserString(NotchDataPrefix+key));
                  edit.Objects.Add(new(before,new(before.Geometry.Duplicate(),attributes)));
                }
                continue;
              }
              for(int i=0;i<Math.Max(old.Length,added.Length);i++)
              {
                DocObjectSnapshot? before=i<old.Length?CaptureDocObject(doc,old[i].Id):null;
                DocObjectSnapshot? after=null;
                if(i<added.Length)
                {
                  var item=added[i];var attributes=item.Attributes.Duplicate();attributes.ObjectId=mapping[item.Id];
                  foreach(string key in new[]{MatchingNotches.SourceMatchKey,MatchingNotches.SourceMemberKey,MatchingNotches.SourceCurveIdsKey,MatchingNotches.CurveReferenceKey,NotchDataPrefix+"source_reversed"})
                    attributes.SetUserString(key,(i<old.Length?old[i]:primary).Attributes.GetUserString(key));
                  if(attributes.GetUserString(MatchingNotches.CurveReferenceKey)!=null)
                  {attributes.SetUserString(NotchDataPrefix+"version",MatchingNotches.Version);MatchingNotches.UpdateStation(attributes,station,curve!.GetLength());}
                  else MatchingNotches.BindReference(doc,attributes,curve!,station);
                  foreach(string? key in (i<old.Length?old[i]:primary).Attributes.GetUserStrings().AllKeys)
                    if(key!=null&&!key.StartsWith(NotchDataPrefix,StringComparison.Ordinal))attributes.SetUserString(key,(i<old.Length?old[i]:primary).Attributes.GetUserString(key));
                  attributes.SetUserString(NotchMatchMemberKey,attributes.ObjectId.ToString("N"));
                  attributes.SetUserString(NotchDataPrefix+(label?"label_id":"notch_id"),attributes.ObjectId.ToString());
                  attributes.SetUserString(NotchDataPrefix+"notch_enabled",notchEnabled?"1":"0");
                  if(label)attributes.SetUserString(NotchDataPrefix+"notch_id",mapping.GetValueOrDefault(created.notch).ToString());
                  attributes.RemoveFromAllGroups();foreach(int index in (i<old.Length?old[i]:primary).Attributes.GetGroupList()??[])attributes.AddToGroup(index);
                  attributes.LayerIndex=ResolveLayerIndex(doc,label?s.LabelLayerName:s.NotchLayerName);
                  var geometry=item.Geometry.Duplicate();
                  if(geometry is TextEntity text)
                  {
                    double scale=text.DimensionScale;var justification=text.Justification;
                    text.DimensionStyleId=doc.DimStyles.Current.Id;
                    text.TextHeight=EffectiveLabelSize(s);text.DimensionScale=scale;text.Justification=justification;
                  }
                  after=new(geometry,attributes);
                }
                var retained=before??after!;
                edit.Objects.Add(new(before??new(retained.Geometry.Duplicate(),retained.Attributes.Duplicate()),after??new(retained.Geometry.Duplicate(),retained.Attributes.Duplicate()),before!=null,after!=null));
              }
            }
          }
        }
        bool success=CommitNotchEdit(doc,s,edit);
        if(success)
        {
          if(doc.Objects.FindId(s.EditedNotchId) is not {IsDeleted:false})s.EditedNotchId=edit.Objects.First(item=>item.AfterExists).After.Attributes.ObjectId;
          s.SelectedNotches.RemoveWhere(id=>doc.Objects.FindId(id) is not {IsDeleted:false});
          if(s.SelectedNotches.Count==0)s.SelectedNotches.Add(s.EditedNotchId);
          s.Panel?.UpdateEditMode();
        }
        return success;
      }
      catch(Exception ex){edit.Dispose();Log.Write(Section,"existing notch edit failed: {0}",ex);RhinoApp.WriteLine("vNotches: existing notch could not be edited; its source curve may be unavailable.");return false;}
    }
    static void SyncEditedNotchRecords(RhinoDoc doc,NotchSession s,NotchMove move)
    {
      for(int record=0;record<s.NotchRecords.Count;record++)for(int curve=0;curve<s.Curves.Count;curve++)
      {
        Guid notch=s.PlacementIds[record][curve],label=s.PlacementLabelIds[record][curve]??Guid.Empty;
        var touched=move.Objects.FirstOrDefault(item=>item.Before.Attributes.ObjectId==notch||item.Before.Attributes.ObjectId==label);
        if(touched==null)continue;
        var attributes=touched.Before.Attributes;
        var live=move.Objects.Select(item=>doc.Objects.FindId(item.After.Attributes.ObjectId)).Where(obj=>obj is {IsDeleted:false}&&
          obj.Attributes.GetUserString(NotchDataPrefix+"curve_key")==attributes.GetUserString(NotchDataPrefix+"curve_key")).ToArray();
        if(live.Length==0)continue;
        var primary=live.FirstOrDefault(IsNotchGeometry);var text=live.FirstOrDefault(obj=>obj.Geometry is TextEntity or TextDot);
        s.PlacementIds[record][curve]=primary?.Id??Guid.Empty;s.NotchIdsByCurve[curve][record]=primary?.Id??Guid.Empty;
        s.PlacementLabelIds[record][curve]=text?.Id;s.LabelIdsByCurve[curve][record]=text?.Id;
        var style=ReadNotchStyle((primary??text)!.Attributes,s.Curves.Count);
        style.NotchEnabled=primary!=null;style.LabelEnabled=text!=null;
        s.NotchRecords[record].EditOverrides[s.CurveIds[curve]]=style;
      }
      if(s.EditedNotchId!=Guid.Empty&&doc.Objects.FindId(s.EditedNotchId) is not {IsDeleted:false})
        s.EditedNotchId=move.Objects.Select(item=>doc.Objects.FindId(item.Before.Attributes.ObjectId)).FirstOrDefault(obj=>obj is {IsDeleted:false})?.Id??Guid.Empty;
    }
    static bool ReassignNotchPairs(RhinoDoc doc,NotchSession s,IEnumerable<RhinoObject> objects,Guid match)
      =>ReassignNotchPairsCore(doc,s,objects,match,null);
    static int NotchMatchDirection(RhinoObject obj)=>obj.Attributes.GetUserString(MatchingNotches.MatchDirectionKey)=="-1"?-1:1;
    static bool LinkNotchFamilies(RhinoDoc doc,NotchSession s,IEnumerable<RhinoObject> selection,Guid anchorId)
    {
      var objects=selection.DistinctBy(obj=>obj.Id).ToArray();
      if(doc.Objects.FindId(anchorId) is not {} anchor)return false;
      var directions=new Dictionary<Guid,int>();
      if(s.OppositeMatches)
      {
        var existing=NotchEditFamilies(doc,[anchorId]).Select(obj=>obj.Id).ToHashSet();
        int anchorDirection=NotchMatchDirection(anchor);
        foreach(var family in objects.Where(obj=>!existing.Contains(obj.Id)).GroupBy(obj=>
          CanonicalNotchMatch(doc,obj) is var match&&match!=Guid.Empty?match.ToString("N"):
            obj.Attributes.GetUserString(NotchDataPrefix+"curve_key")+"/"+MatchingNotches.StationKey(obj.Attributes)))
        {
          var primary=family.FirstOrDefault(IsNotchGeometry)??family.First();
          int multiplier=-anchorDirection*NotchMatchDirection(primary);
          foreach(var obj in family)directions[obj.Id]=NotchMatchDirection(obj)*multiplier;
        }
      }
      return ReassignNotchPairsCore(doc,s,objects,Guid.NewGuid(),directions);
    }
    static bool ReassignNotchPairsCore(RhinoDoc doc,NotchSession s,IEnumerable<RhinoObject> objects,Guid match,IReadOnlyDictionary<Guid,int>? directions)
    {
      var edit=new NotchMove{AttributesOnly=true};
      var copies=new Dictionary<(string Session,string Copy),Guid>();
      foreach(var obj in objects.DistinctBy(obj=>obj.Id))
      {
        var before=CaptureDocObject(doc,obj.Id);if(before==null)continue;
        var after=new DocObjectSnapshot(before.Geometry.Duplicate(),before.Attributes.Duplicate());
        Guid local=match;
        if(obj.Attributes.GetUserString("vNest.session") is {} session)
        {
          var key=(session,obj.Attributes.GetUserString("vNest.copy")??string.Empty);
          if(!copies.TryGetValue(key,out local))copies[key]=local=Guid.NewGuid();
        }
        after.Attributes.SetUserString(NotchMatchSetKey,local.ToString("N"));after.Attributes.SetUserString(NotchMatchMemberKey,obj.Id.ToString("N"));
        after.Attributes.SetUserString(MatchingNotches.SourceMatchKey,match.ToString("N"));
        if(directions?.TryGetValue(obj.Id,out int direction)==true)after.Attributes.SetUserString(MatchingNotches.MatchDirectionKey,direction.ToString(System.Globalization.CultureInfo.InvariantCulture));
        after.CaptureUserStrings();
        edit.Objects.Add(new(before,after));
      }
      return edit.Objects.Count>0&&CommitNotchEdit(doc,s,edit);
    }
    static void ReverseEditedNotchSource(RhinoDoc doc,NotchSession s,Guid sourceId)
    {
      var members=EditNotchObjects(doc,s);
      string? section=s.Sources.SectionKey(sourceId);Guid parent=s.Sources.ParentId(sourceId);
      bool UsesSource(RhinoObject obj)=>section!=null?MatchingNotches.ReferenceRangeKeys(obj.Attributes).Contains(section):ResolveNotchSources(doc,obj).Contains(parent);
      Guid Root(RhinoObject obj)=>Guid.TryParse(obj.Attributes.GetUserString(MatchingNotches.SourceMemberKey),out var root)?root:obj.Id;
      var roots=members.Where(obj=>UsesSource(obj)&&IsNotchGeometry(obj)).Select(Root).ToHashSet();
      var edit=new NotchMove{AttributesOnly=true};
      foreach(var group in members.GroupBy(obj=>(obj.Attributes.GetUserString(NotchDataPrefix+"curve_key"),MatchingNotches.StationKey(obj.Attributes))))
      {
        var primary=group.FirstOrDefault(IsNotchGeometry)??group.First();
        if(!roots.Contains(Root(primary))&&!group.Any(UsesSource))continue;
        foreach(var obj in group)
        {
          var before=CaptureDocObject(doc,obj.Id);if(before==null){edit.Dispose();return;}
          var attributes=before.Attributes.Duplicate();
          attributes.SetUserString(MatchingNotches.MatchDirectionKey,(-NotchMatchDirection(obj)).ToString(System.Globalization.CultureInfo.InvariantCulture));
          edit.Objects.Add(new(before,new DocObjectSnapshot(before.Geometry.Duplicate(),attributes)));
        }
      }
      if(edit.Objects.Count==0){edit.Dispose();return;}
      if(CommitNotchEdit(doc,s,edit)){LoadNotchForEdit(doc,s,s.EditedNotchId);s.EditPicking?.RefreshAssociations();}
    }
    static void UnlinkEditedNotch(RhinoDoc doc,NotchSession s)
    {
      if(doc.Objects.FindId(s.EditedNotchId) is not {} picked)return;
      var members=EditNotchObjects(doc,s);var physical=PhysicalNotchObjects(members,picked);
      bool CopyOf(RhinoObject original,RhinoObject target)
      {
        if(original.Id==target.Id)return true;
        if(Guid.TryParse(target.Attributes.GetUserString(MatchingNotches.SourceMemberKey),out var source)&&source==original.Id)return true;
        if(target.Attributes.GetUserString("vNest.sourceObjects") is not {} inventory)return false;
        try{return (System.Text.Json.JsonSerializer.Deserialize<Guid[]>(inventory)??[]).Contains(original.Id)&&VerifiedNotchCopy(doc,original,target);}
        catch(System.Text.Json.JsonException){return false;}
      }
      var originals=members.Where(obj=>obj.Attributes.GetUserString("vNest.session")==null&&IsNotchGeometry(obj)&&
        physical.Any(local=>IsNotchGeometry(local)&&CopyOf(obj,local))).ToArray();
      var targets=physical.Concat(members.Where(obj=>originals.Any(original=>CopyOf(original,obj))))
        .SelectMany(obj=>PhysicalNotchObjects(members,obj)).DistinctBy(obj=>obj.Id).ToArray();
      ReassignNotchPairs(doc,s,targets,Guid.NewGuid());
      SelectNotchForEdit(doc,s,picked.Id,"select");
      s.Panel?.UpdateEditMode();
    }
    static void PickNotchPairs(RhinoDoc doc,NotchSession s)
    {
      if(s.RepairPairing){SetNotchLinkMode(doc,s,false);return;}
      SetNotchLinkMode(doc,s,true);
      if(s.SelectedNotches.Count>1)CompleteNotchLinkCycle(doc,s);
    }
    static void QueueNotchForLink(RhinoDoc doc,NotchSession s,Guid id)
    {
      if(doc.Objects.FindId(id) is not {IsDeleted:false} picked||!IsNotchGeometry(picked))return;
      if(s.SelectedNotches.Count==0)s.LinkAnchor=id;
      SelectNotchForEdit(doc,s,id,"add");s.EditPicking?.RefreshAssociations();
      Log.Write(Section,"link pick notch={0} physicalPicks={1}",id,s.SelectedNotches.Count);
    }
    static bool CompleteNotchLinkCycle(RhinoDoc doc,NotchSession s)
    {
      var selected=s.SelectedNotches.Where(id=>doc.Objects.FindId(id) is {IsDeleted:false}).ToArray();
      Log.Write(Section,"link Enter physicalPicks={0} active={1}",selected.Length,s.RepairPairing);
      if(selected.Length<2){SetNotchLinkMode(doc,s,false);return false;}
      Guid anchor=selected.Contains(s.LinkAnchor)?s.LinkAnchor:selected[0];
      var families=selected.Select(id=>CanonicalNotchMatch(doc,doc.Objects.FindId(id))).ToArray();
      bool alreadyLinked=families[0]!=Guid.Empty&&families.All(family=>family==families[0]);
      if(!alreadyLinked&&!LinkNotchFamilies(doc,s,NotchEditFamilies(doc,selected),anchor))return false;
      s.EditedNotchId=s.LinkAnchor=Guid.Empty;s.SelectedNotches.Clear();s.EditedNotchObjects.Clear();s.PreviewValid=false;
      s.Panel?.ClearCurveRowFeedback();
      while(s.Curves.Count>0)
      {
        int index=s.Curves.Count-1;s.Curves[index].Dispose();foreach(var segment in s.PerCurveSegments[index])segment.Dispose();RemoveSessionCurve(s,index);
      }
      s.CurveSideBySource.Clear();s.CurveBothSidesBySource.Clear();s.CurveReversedBySource.Clear();s.ClearPlacementRuns();
      doc.Objects.UnselectAll();RefreshPanelForCurves(s);UpdateDistanceLabels(s,null,null,null,null,null,null);
      s.Panel?.UpdateEditMode();s.EditPicking?.RefreshAssociations();doc.Views.Redraw();return true;
    }
    static void SetNotchLinkMode(RhinoDoc doc,NotchSession s,bool enabled)
    {
      if(enabled&&!s.RepairPairing)s.LinkAnchor=s.SelectedNotches.FirstOrDefault();
      else if(!enabled)s.LinkAnchor=Guid.Empty;
      s.EditMode=true;s.RepairPairing=enabled;if(enabled)s.EditTool=NotchEditTool.None;s.EditPicking?.CancelPendingGesture();
      Log.Write(Section,"link mode active={0} physicalPicks={1}",enabled,s.SelectedNotches.Count);
      s.Panel?.UpdateEditMode();s.EditPicking?.RefreshAssociations();doc.Views.Redraw();
    }

    sealed record NotchPickTarget(RhinoObject Object,Point3d[] Points,Point3d Center);
    static NotchPickTarget[] NotchPickTargets(IEnumerable<RhinoObject> objects)=>objects.Where(obj=>IsNotchGeometry(obj)&&obj.IsSelectable(true,false,false,false)).Select(obj=>
    {
      var curve=(Curve)obj.Geometry;
      var points=curve.TryGetPolyline(out var polyline)?polyline.ToArray():
        (curve.DivideByCount(ConfiguredEditPickSamples,true)??[curve.Domain.T0,curve.Domain.T1]).Select(curve.PointAt).ToArray();
      return new NotchPickTarget(obj,points,curve.GetBoundingBox(true).Center);
    }).ToArray();
    static RhinoObject? PickNotchTarget(IEnumerable<NotchPickTarget> targets,Func<Point3d,Point2d> project,Point2d cursor,double radius)
    {
      RhinoObject? picked=null;double best=radius*radius;
      double Distance(Point2d point)=>cursor.DistanceToSquared(point);
      foreach(var target in targets)
      {
        if(target.Object.IsDeleted||target.Object.IsHidden)continue;
        double distance=Distance(project(target.Center));Point2d? previous=null;
        foreach(var world in target.Points)
        {
          var point=project(world);distance=Math.Min(distance,Distance(point));
          if(previous is {} start)
          {
            var direction=point-start;double squared=direction.SquareLength;
            if(squared>0){double fraction=Math.Clamp(((cursor-start)*direction)/squared,0,1);distance=Math.Min(distance,Distance(start+fraction*direction));}
          }
          previous=point;
        }
        if(distance<=best){best=distance;picked=target.Object;}
      }
      return picked;
    }
    static RhinoObject[] PhysicalNotchObjects(RhinoObject[] objects,RhinoObject picked)
    {
      string? source=picked.Attributes.GetUserString(NotchDataPrefix+"curve_key"),point=MatchingNotches.StationKey(picked.Attributes);
      return objects.Where(obj=>obj.Id==picked.Id||source!=null&&point!=null&&
        obj.Attributes.GetUserString(NotchDataPrefix+"curve_key")==source&&MatchingNotches.StationKey(obj.Attributes)==point).ToArray();
    }
    sealed class NotchVisibilityCues(RhinoDoc doc):DisplayConduit,IDisposable
    {
      internal RhinoObject[] Objects=[];
      internal RhinoObject[] Selected=[],Related=[];
      internal bool Moving;
      internal RhinoObject? Hover;
      static void Ring(DrawEventArgs e,RhinoObject obj,PreviewDisplay.ObjectHighlightStyle style)
      {
        if(obj.IsDeleted||obj.IsHidden||!IsNotchGeometry(obj))return;
        Ring(e,obj.Geometry,style);
      }
      internal static void Ring(DrawEventArgs e,GeometryBase geometry,PreviewDisplay.ObjectHighlightStyle style)
      {
        var point=geometry.GetBoundingBox(true).Center;
        e.Display.DrawPoint(point,PointStyle.Circle,ConfiguredEditMarkerPixels+1,style.OutlineColor);
        e.Display.DrawPoint(point,PointStyle.Circle,ConfiguredEditMarkerPixels,style.BodyColor);
      }
      internal void DrawHover(DrawEventArgs e)
      {if(e.RhinoDoc.RuntimeSerialNumber==doc.RuntimeSerialNumber&&Hover is {} obj)Ring(e,obj,HoverNotchStyle);}
      protected override void DrawForeground(DrawEventArgs e)=>DrawExisting(e);
      internal void DrawExisting(DrawEventArgs e)
      {
        if(e.RhinoDoc.RuntimeSerialNumber!=doc.RuntimeSerialNumber)return;
        foreach(var obj in Objects)
        {
          if(obj.IsDeleted||obj.IsHidden)continue;
          var bounds=obj.Geometry.GetBoundingBox(true);var point=bounds.Center;
          if(!e.Viewport.GetWorldToScreenScale(point,out double scale)||bounds.Diagonal.Length*scale>ConfiguredExistingNotchMarkerPixels*2)continue;
          e.Display.DrawPoint(point,PointStyle.RoundSimple,ConfiguredExistingNotchMarkerPixels,
            HasNotchPairData(obj)?MoveHighlightStyle.BodyColor:UnpairedHighlightStyle.BodyColor);
        }
        if(!Moving)
        {
          foreach(var obj in Related)Ring(e,obj,MatchedNotchStyle);
          foreach(var obj in Selected)Ring(e,obj,ActiveNotchStyle);
        }
      }
      public void Dispose(){Enabled=false;Objects=[];Selected=[];Related=[];Hover=null;}
    }
    sealed class NotchRemovalPicking : Rhino.UI.MouseCallback,IDisposable
    {
      readonly RhinoDoc _doc;
      readonly NotchSession _session;
      readonly PreviewDisplay.ObjectHighlighter _highlight;
      readonly PreviewDisplay.ObjectHighlighter _hoverHighlight;
      readonly PreviewDisplay.ObjectHighlighter _legacyHighlight;
      readonly PreviewDisplay.ObjectHighlighter _existingHighlight;
      readonly PreviewDisplay.ObjectHighlighter _editedHighlight;
      readonly PreviewDisplay.ObjectHighlighter _matchedHighlight;
      readonly PreviewDisplay.ObjectHighlighter _sourceHighlight;
      Guid[] _assignedSources=[];
      readonly Dictionary<Guid,GeometryBase> _assignedSourceGeometry=[];
      readonly HashSet<Guid> _rowHoveredSources=[];
      readonly NotchVisibilityCues _cues;
      readonly GetPoint? _getter;
      readonly NotchDoubleClickInput _doubleClickInput;
      bool _pointerDown;
      Guid _pressedNotch;
      bool _dragRequested;
      internal bool PointerDown=>_pointerDown;
      Guid _snappedNotchId;
      NotchPickTarget[] _pickTargets=[];
      (Guid Id,bool Control,bool Shift)? _feedbackKey;
      bool _showExisting;
      readonly UITimer _timer=new(){Interval=ConfiguredRemovalModifierPollSeconds};
      RhinoView? _view;
      System.Drawing.Point _pixel;
      bool _active,_control,_shift,_captured,_selectionHandoff,_movingFeedback;
      Guid _picked;
      Guid _lastClick;
      NotchMoveRequest? _pendingMove;
      long _lastClickTick,_lastMoveTick;
      System.Drawing.Point _lastClickPixel;
      Guid[] _targets=[];
      public bool Active {get=>_active;set
      {
        _active=value;
        if(!value){SnapToNotch(null);ShowTargets([]);_hoverHighlight.SetObjects([]);_feedbackKey=null;_cues.Hover=null;_session.ExistingNotchHover=false;_picked=Guid.Empty;}
        else {_selectionHandoff=false;RefreshExisting();_doc.Views.Redraw();}
      }}
      internal static bool ControlPressed=>(System.Windows.Forms.Control.ModifierKeys&System.Windows.Forms.Keys.Control)!=0;
      internal NotchRemovalPicking(RhinoDoc doc,NotchSession session):this(doc,session,null){}
      internal NotchRemovalPicking(RhinoDoc doc,NotchSession session,GetPoint? getter)
      {
        _doc=doc;_session=session;_getter=getter;session.EditPicking=this;
        _highlight=new(doc,RemovalHighlightStyle){RedrawOnChange=false,DrawOnTop=true,AutomaticForegroundDrawing=false};
        _hoverHighlight=new(doc,HoverNotchStyle){RedrawOnChange=false,DrawOnTop=true,AutomaticForegroundDrawing=false};
        _legacyHighlight=new(doc,UnpairedHighlightStyle){RedrawOnChange=false,DrawOnTop=true};
        _existingHighlight=new(doc,MoveHighlightStyle){RedrawOnChange=false,DrawOnTop=true};
        _editedHighlight=new(doc,ActiveNotchStyle){RedrawOnChange=false,DrawOnTop=true};
        _matchedHighlight=new(doc,MatchedNotchStyle){RedrawOnChange=false,DrawOnTop=true};
        _sourceHighlight=new(doc,AssignedCurveStyle){RedrawOnChange=false,DrawOnTop=true};
        _cues=new(doc);
        _doubleClickInput=new(HandleDoubleClickMessage,ReleasePointer,TrackPointer);
        _timer.Elapsed+=(_,_)=>PollInput();
        Enabled=true;_timer.Start();
      }
      void PollInput()
      {
          if(_active&&ReferenceEquals(_activeSession,_session)&&RhinoDoc.ActiveDoc==_doc&&
              _showExisting!=(_session.EditMode&&_session.HighlightExisting)){RefreshExisting();_doc.Views.Redraw();}
          if(!CanPick()){ShowTargets([]);ClearHover();return;}
          bool control=ControlPressed,shift=(System.Windows.Forms.Control.ModifierKeys&System.Windows.Forms.Keys.Shift)!=0;
          if(control==_control&&shift==_shift)return;
          _control=control;_shift=shift;UpdateTarget();_doc.Views.Redraw();
      }
      void RefreshExisting()
      {
        _showExisting=_session.EditMode&&_session.HighlightExisting;
        _feedbackKey=null;
        var objects=_doc.Objects.GetObjectList(ObjectType.Curve).Where(obj=>IsNotchGeometry(obj)&&!obj.IsHidden).ToArray();
        _pickTargets=NotchPickTargets(objects);
        var members=_session.EditMode?EditNotchObjects(_doc,_session):[];
        var selected=(_session.SelectedNotches.Count>0?_session.SelectedNotches: new HashSet<Guid>{_session.EditedNotchId})
          .Append(_session.EditedNotchId).Distinct().Select(_doc.Objects.FindId).Where(obj=>obj is {IsDeleted:false})
          .SelectMany(obj=>PhysicalNotchObjects(members,obj)).DistinctBy(obj=>obj.Id).ToArray();
        var local=selected.Select(obj=>obj.Id).ToHashSet();var related=members.Where(obj=>!local.Contains(obj.Id)).ToArray();
        var emphasized=members.Select(obj=>obj.Id).ToHashSet();var ordinary=objects.Where(obj=>!emphasized.Contains(obj.Id)).ToArray();
        _legacyHighlight.SetObjects(!_session.EditMode||_showExisting?ordinary.Where(obj=>!HasNotchPairData(obj)).Select(obj=>obj.Id):[]);
        _existingHighlight.SetObjects(_showExisting?ordinary.Where(HasNotchPairData).Select(obj=>obj.Id):[]);
        _editedHighlight.SetObjects(selected.Select(obj=>obj.Id));_matchedHighlight.SetObjects(related.Select(obj=>obj.Id));
        _sourceHighlight.SetObjects([]);foreach(var geometry in _assignedSourceGeometry.Values)geometry.Dispose();_assignedSourceGeometry.Clear();
        if(_session.EditMode)
        {
          for(int row=0;row<_session.PerCurveSourceIds.Count;row++)
            for(int index=0;index<_session.PerCurveSourceIds[row].Count;index++)
            {
              Guid id=_session.PerCurveSourceIds[row][index];
              if(_session.Sources.IsSegment(id))_assignedSourceGeometry[id]=_session.PerCurveSegments[row][index].DuplicateCurve();
            }
          _assignedSources=_session.PerCurveSourceIds.SelectMany(ids=>ids).Select(id=>SourceFeedbackId(_session,id)).Distinct().ToArray();
        }
        else _assignedSources=_session.Associations?_session.AssociatedCopies.SelectMany(copy=>_session.PerCurveSourceIds.SelectMany(ids=>ids).Select(_session.Sources.ParentId).Where(copy.Map.ContainsKey).Select(id=>copy.Map[id])).Distinct().ToArray():[];
        _sourceHighlight.SetObjects(_session.AssigningSource?[]:_assignedSources.Where(id=>!_rowHoveredSources.Contains(id)),_assignedSourceGeometry);
        _cues.Objects=_showExisting?ordinary:[];_cues.Selected=selected;_cues.Related=related;_cues.Enabled=_showExisting||selected.Length>0;
        if(local.Contains(_picked)){_hoverHighlight.SetObjects([]);_cues.Hover=null;}
      }
      internal void RefreshAssociations()=>RefreshExisting();
      internal void SetRowHoveredSources(IEnumerable<Guid> sources)
      {_rowHoveredSources.Clear();_rowHoveredSources.UnionWith(sources);_sourceHighlight.SetObjects(_session.AssigningSource?[]:_assignedSources.Where(id=>!_rowHoveredSources.Contains(id)),_assignedSourceGeometry);}
      internal void DrawFeedback(GetPointDrawEventArgs e)
      {
        if(!_active&&!_movingFeedback)return;
        _sourceHighlight.DrawForegroundHighlights(e);_existingHighlight.DrawForegroundHighlights(e);_legacyHighlight.DrawForegroundHighlights(e);
        if(!_cues.Moving){_matchedHighlight.DrawForegroundHighlights(e);_editedHighlight.DrawForegroundHighlights(e);}
        _highlight.DrawForegroundHighlights(e);_hoverHighlight.DrawForegroundHighlights(e);_cues.DrawExisting(e);_cues.DrawHover(e);
        _session.Panel?.DrawCurveHover(e);
      }
      internal NotchMoveRequest? TakePendingMove(){var request=_pendingMove;_pendingMove=null;return request;}
      internal void SelectForEdit(Guid id,string action)
      {
        _selectionHandoff=true;var elapsed=Stopwatch.StartNew();
        SelectNotchForEdit(_doc,_session,id,action);
        RefreshExisting();_doc.Views.Redraw();
        Log.Write(Section,"edit selection action={0} notch={1} sources={2} elapsed_ms={3:0.0}",action,id,_session.Curves.Count,elapsed.Elapsed.TotalMilliseconds);
      }
      internal void HideFeedback()
      {
        _selectionHandoff=false;_captured=false;
        SnapToNotch(null);ShowTargets([]);_hoverHighlight.SetObjects([]);_cues.Hover=null;
      }
      internal void BeginMoveFeedback()
      {
        // Only the moving snapshots replace these layers; source and unrelated feedback stay live.
        HideFeedback();_movingFeedback=true;_cues.Moving=true;
        _editedHighlight.AutomaticForegroundDrawing=false;_matchedHighlight.AutomaticForegroundDrawing=false;
      }
      internal void ArmMoveFeedback()
      {
        HideFeedback();_movingFeedback=true;
      }
      internal void EndMoveFeedback()
      {
        _movingFeedback=false;_cues.Moving=false;
        _editedHighlight.AutomaticForegroundDrawing=true;_matchedHighlight.AutomaticForegroundDrawing=true;
        RefreshExisting();
      }
      void RequestMove(Guid id,long now,bool mouseDrag=false)
      {
        _lastClick=Guid.Empty;_lastMoveTick=now;
        _pendingMove=new NotchMoveRequest(id,mouseDrag,_session.NotchDragStart);
        Log.Write(Section,"edit move requested: notch={0} deferred={1}",id,!_active);
        if(_active)GetBaseClass.PostCustomMessage(_pendingMove);
      }
      void ReleasePointer(){_pointerDown=false;_pressedNotch=Guid.Empty;_dragRequested=false;}
      internal void CancelPendingGesture(){ReleasePointer();_pendingMove=null;_lastClick=Guid.Empty;}
      internal object? PressNotch(Guid id,System.Drawing.Point pixel,bool control,bool shift,long now)
      {
        if(_session.RepairPairing||_session.EditTool!=NotchEditTool.None){CancelPendingGesture();return new NotchEditRequest(control?"deselect":"select",id);}
        var size=System.Windows.Forms.SystemInformation.DoubleClickSize;
        bool twice=!control&&!shift&&_lastClick==id&&now-_lastClickTick<=System.Windows.Forms.SystemInformation.DoubleClickTime&&
          Math.Abs(pixel.X-_lastClickPixel.X)<=size.Width&&Math.Abs(pixel.Y-_lastClickPixel.Y)<=size.Height;
        _lastClick=control||shift?Guid.Empty:id;_lastClickTick=now;_lastClickPixel=pixel;
        if(twice){RequestMove(id,now);return null;}
        if(control||shift)return new NotchEditRequest(control?"deselect":"add",id);
        _pointerDown=true;_pressedNotch=id;_dragRequested=false;_session.NotchDragMoved=false;_session.NotchDragStart=pixel;
        return _session.EditedNotchId==id&&_session.SelectedNotches.Count==1?null:new NotchEditRequest("select",id);
      }
      bool HandleDoubleClickMessage(IntPtr window,System.Drawing.Point pixel,uint modifiers)
      {
        bool resume=_selectionHandoff&&_lastClick!=Guid.Empty;
        if((!_active&&!resume)||!_session.EditMode||_session.ExistingNotchDragging||_session.CurveSelectionRequested||
            !ReferenceEquals(_activeSession,_session)||RhinoDoc.ActiveDoc!=_doc||(modifiers&0x0c)!=0)return false; // MK_SHIFT | MK_CONTROL retain selection semantics.
        var view=_doc.Views.GetViewList(ViewTypeFilter.Model|ViewTypeFilter.Page).FirstOrDefault(candidate=>candidate.Handle==window);
        if(view?.ActiveViewport is not {} viewport)return false;
        var target=PickNotchTarget(_pickTargets,viewport.WorldToClient,new Point2d(pixel.X,pixel.Y),ConfiguredEditPickRadiusPixels);
        if(target==null)return false;
        if(_session.RepairPairing||_session.EditTool!=NotchEditTool.None)return true;
        _session.Panel?.SetViewportPointerActive();_view=view;_pixel=pixel;_picked=target.Id;_captured=true;
        long now=System.Environment.TickCount64;
        if(now-_lastMoveTick>System.Windows.Forms.SystemInformation.DoubleClickTime)RequestMove(target.Id,now);
        return true;
      }
      void TrackPointer(IntPtr window,System.Drawing.Point pixel,uint modifiers)
      {
        if(_view?.Handle!=window||!ReferenceEquals(_activeSession,_session)||RhinoDoc.ActiveDoc!=_doc)return;
        _pixel=pixel;
        if(_session.ExistingNotchDragging)_session.NotchMovePointerUpdated=true;
        TrackPressedPointer(pixel,modifiers);
      }
      internal void TrackPressedPointer(System.Drawing.Point pixel,uint modifiers)
      {
        if(!_pointerDown||(modifiers&1)==0)return;
        var size=System.Windows.Forms.SystemInformation.DragSize;
        if(Math.Abs(pixel.X-_session.NotchDragStart.X)>size.Width/2||Math.Abs(pixel.Y-_session.NotchDragStart.Y)>size.Height/2)
        {
          _session.NotchDragMoved=true;
          if(!_dragRequested&&_pressedNotch!=Guid.Empty&&!_session.ExistingNotchDragging&&!_session.RepairPairing&&_session.EditTool==NotchEditTool.None)
          {_dragRequested=true;RequestMove(_pressedNotch,System.Environment.TickCount64,true);}
        }
      }
      bool CanPick()=>_active&&!_session.CurveSelectionRequested&&ReferenceEquals(_activeSession,_session)&&
        RhinoDoc.ActiveDoc==_doc&&_session.Panel?.ViewportPreviewActive!=false;
      void ClearHover()
      {
        bool changed=_picked!=Guid.Empty||_cues.Hover!=null;
        SnapToNotch(null);
        _picked=Guid.Empty;_feedbackKey=null;_cues.Hover=null;_session.ExistingNotchHover=false;_hoverHighlight.SetObjects([]);
        if(changed)_doc.Views.Redraw();
      }
      void SnapToNotch(RhinoObject? obj)
      {
        Guid id=obj!=null&&_session.EditMode&&IsNotchGeometry(obj)?obj.Id:Guid.Empty;
        if(_getter==null||id==_snappedNotchId)return;
        _getter.ClearConstraints();_getter.ClearConstructionPoints();_snappedNotchId=Guid.Empty;
        if(id!=Guid.Empty&&obj!.Geometry is Curve curve&&_getter.Constrain(curve,true))_snappedNotchId=id;
      }
      void ShowTargets(Guid[] ids)
      {
        if(_targets.SequenceEqual(ids))return;
        _targets=ids;_highlight.SetObjects(ids);_doc.Views.Redraw();
      }
      void UpdateTarget()
      {
        if(!CanPick()||_view?.ActiveViewport is not {} viewport){ShowTargets([]);ClearHover();return;}
        try
        {
          if(!viewport.GetFrustumLine(_pixel.X,_pixel.Y,out var ray)){ShowTargets([]);return;}
          using var context=new PickContext{View=_view,PickLine=ray,PickStyle=PickStyle.PointPick,PickMode=PickMode.Wireframe,PickGroupsEnabled=false};
          context.SetPickTransform(viewport.GetPickTransform(_pixel));context.UpdateClippingPlanes();
          var close=PickNotchTarget(_pickTargets,viewport.WorldToClient,new Point2d(_pixel.X,_pixel.Y),ConfiguredEditPickRadiusPixels);
          var references=close==null&&_control?_doc.Objects.PickObjects(context)??[]:[];
          try
          {
            var target=close??references.Select(reference=>reference.Object()).FirstOrDefault(obj=>obj!=null&&RemovableNotchObject(obj));
            _picked=target?.Id??Guid.Empty;_session.ExistingNotchHover=target!=null;
            SnapToNotch(target);
            bool activeNotch=target!=null&&_cues.Selected.Any(obj=>obj.Id==target.Id);
            _cues.Hover=activeNotch&&!_control?null:target;
            var feedback=(_picked,_control,_shift);
            if(_feedbackKey==feedback)return;
            _feedbackKey=feedback;
            if(_session.EditMode&&!_showExisting)_legacyHighlight.SetObjects(target!=null&&!HasNotchPairData(target)?[target.Id]:[]);
            if(target!=null)_session.PreviewValid=false;
            ShowTargets(target!=null&&_control?(_session.EditMode?PhysicalNotchObjects(NotchMoveObjects(_doc,target.Id),target):RemovalTargets(_doc,target.Id)).Select(obj=>obj.Id).ToArray():[]);
            _hoverHighlight.SetObjects(target==null||_control||activeNotch?[]:
              (_shift?MatchingNotchMates(_doc,target.Id):PhysicalNotchObjects(NotchMoveObjects(_doc,target.Id),target)).Select(obj=>obj.Id));
          }
          finally{foreach(var reference in references)reference.Dispose();}
        }
        catch(Exception ex){SnapToNotch(null);ShowTargets([]);_hoverHighlight.SetObjects([]);_cues.Hover=null;_feedbackKey=null;_picked=Guid.Empty;_session.ExistingNotchHover=false;Log.Write(Section,"notch hover failed: {0}",ex.Message);}
      }
      protected override void OnMouseMove(Rhino.UI.MouseCallbackEventArgs e)
      {
        if(_active&&RhinoDoc.ActiveDoc==_doc)_session.Panel?.SetViewportPointerActive();
        _view=e.View;_pixel=e.ViewportPoint;_control=e.CtrlKeyDown;_shift=e.ShiftKeyDown;
        if(_view!=null)TrackPointer(_view.Handle,_pixel,e.MouseButton==Rhino.UI.MouseButton.Left?1u:0u);
        UpdateTarget();base.OnMouseMove(e);
      }
      protected override void OnMouseDown(Rhino.UI.MouseCallbackEventArgs e)
      {
        if(_active&&RhinoDoc.ActiveDoc==_doc)_session.Panel?.SetViewportPointerActive();
        if(e.MouseButton==Rhino.UI.MouseButton.Left&&CanPick())
        {
          _view=e.View;_pixel=e.ViewportPoint;_control=e.CtrlKeyDown;_shift=e.ShiftKeyDown;UpdateTarget();
          if(_picked!=Guid.Empty)
          {
            e.Cancel=true;_captured=true;
            if(_session.EditMode)
            {
              var request=PressNotch(_picked,_pixel,_control,_shift,System.Environment.TickCount64);
              if(request!=null)GetBaseClass.PostCustomMessage(request);
            }
            else GetBaseClass.PostCustomMessage(_control?new NotchRemovalRequest(_picked):_shift?new NotchMateRequest(_picked):new NotchEditRequest("select",_picked));
          }
        }
        base.OnMouseDown(e);
      }
      protected override void OnMouseDoubleClick(Rhino.UI.MouseCallbackEventArgs e)
      {
        if(_active&&RhinoDoc.ActiveDoc==_doc)_session.Panel?.SetViewportPointerActive();
        bool resume=_selectionHandoff&&_session.EditMode&&!_session.ExistingNotchDragging&&!_session.CurveSelectionRequested&&
          ReferenceEquals(_activeSession,_session)&&RhinoDoc.ActiveDoc==_doc&&_lastClick!=Guid.Empty;
        if(e.MouseButton==Rhino.UI.MouseButton.Left&&(CanPick()||resume))
        {
          _view=e.View;_pixel=e.ViewportPoint;_control=e.CtrlKeyDown;_shift=e.ShiftKeyDown;
          if(_active)UpdateTarget();
          else if(_view?.ActiveViewport is {} viewport)
            _picked=PickNotchTarget(_pickTargets,viewport.WorldToClient,new Point2d(_pixel.X,_pixel.Y),ConfiguredEditPickRadiusPixels)?.Id??Guid.Empty;
          if(_picked!=Guid.Empty)
          {
            e.Cancel=true;_captured=true;
            long now=System.Environment.TickCount64;
            if(!_session.RepairPairing&&_session.EditTool==NotchEditTool.None&&!_control&&!_shift&&now-_lastMoveTick>System.Windows.Forms.SystemInformation.DoubleClickTime)
            {RequestMove(_picked,now);}
          }
        }
        base.OnMouseDoubleClick(e);
      }
      protected override void OnMouseUp(Rhino.UI.MouseCallbackEventArgs e)
      {if(e.MouseButton==Rhino.UI.MouseButton.Left){ReleasePointer();if(_captured){e.Cancel=true;_captured=false;}}base.OnMouseUp(e);}
      public void Dispose(){_doubleClickInput.Dispose();Enabled=false;SnapToNotch(null);_timer.Stop();_timer.Dispose();_highlight.Dispose();_hoverHighlight.Dispose();_legacyHighlight.Dispose();_existingHighlight.Dispose();_editedHighlight.Dispose();_matchedHighlight.Dispose();_sourceHighlight.Dispose();foreach(var geometry in _assignedSourceGeometry.Values)geometry.Dispose();_assignedSourceGeometry.Clear();_cues.Dispose();_session.ExistingNotchHover=false;if(ReferenceEquals(_session.EditPicking,this))_session.EditPicking=null;}
    }

    sealed class NotchDoubleClickInput : IDisposable
    {
      const int GetMessageHook=3; // Win32 WH_GETMESSAGE; observes only Rhino's own UI thread.
      const int RemovedMessage=1; // Win32 PM_REMOVE; never act twice on a message merely being inspected.
      const uint DoubleClickMessage=0x0203; // Win32 WM_LBUTTONDBLCLK in a viewport's client coordinates.
      const uint ButtonUpMessage=0x0202; // Win32 WM_LBUTTONUP; ends a press even during selection-panel updates.
      const uint MouseMoveMessage=0x0200; // Win32 WM_MOUSEMOVE; distinguishes real travel from point-getter initialization.
      readonly Func<IntPtr,System.Drawing.Point,uint,bool> _dispatch;
      readonly Action _release;
      readonly Action<IntPtr,System.Drawing.Point,uint> _motion;
      readonly HookProc _callback;
      IntPtr _hook;
      internal NotchDoubleClickInput(Func<IntPtr,System.Drawing.Point,uint,bool> dispatch,Action release,Action<IntPtr,System.Drawing.Point,uint> motion)
      {
        _dispatch=dispatch;_release=release;_motion=motion;_callback=OnMessage;
        var window=RhinoApp.MainWindowHandle();
        if(!OperatingSystem.IsWindows()||window==IntPtr.Zero)return;
        uint thread=GetWindowThreadProcessId(window,out _);
        if(thread!=0)_hook=SetWindowsHookEx(GetMessageHook,_callback,IntPtr.Zero,thread);
        if(_hook==IntPtr.Zero)Log.Write(Section,"Could not install viewport double-click input.");
      }
      IntPtr OnMessage(int code,IntPtr remove,IntPtr pointer)
      {
        try
        {
          if(code>=0&&remove.ToInt64()==RemovedMessage&&pointer!=IntPtr.Zero)
          {
            var message=System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMessage>(pointer);
            if(message.Message==ButtonUpMessage)_release();
            if(message.Message is DoubleClickMessage or MouseMoveMessage)
            {
              long packed=message.LParam.ToInt64();var pixel=new System.Drawing.Point(unchecked((short)packed),unchecked((short)(packed>>16)));
              uint modifiers=unchecked((uint)message.WParam.ToUInt64());
              if(message.Message==MouseMoveMessage)_motion(message.Window,pixel,modifiers);
              else if(_dispatch(message.Window,pixel,modifiers))
                System.Runtime.InteropServices.Marshal.WriteInt32(pointer,IntPtr.Size,0);
            }
          }
        }
        catch(Exception ex){Log.Write(Section,"viewport double-click input failed: {0}",ex.Message);}
        return CallNextHookEx(_hook,code,remove,pointer);
      }
      public void Dispose(){if(_hook!=IntPtr.Zero){UnhookWindowsHookEx(_hook);_hook=IntPtr.Zero;}}
      [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
      readonly struct NativeMessage
      {
        public readonly IntPtr Window;
        public readonly uint Message;
        public readonly UIntPtr WParam;
        public readonly IntPtr LParam;
        public readonly uint Time;
        public readonly int X,Y;
        public readonly uint Private;
      }
      delegate IntPtr HookProc(int code,IntPtr remove,IntPtr message);
      [System.Runtime.InteropServices.DllImport("user32.dll",SetLastError=true)]static extern IntPtr SetWindowsHookEx(int kind,HookProc callback,IntPtr module,uint thread);
      [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool UnhookWindowsHookEx(IntPtr hook);
      [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr remove,IntPtr message);
      [System.Runtime.InteropServices.DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    }

    // ── Undo ──────────────────────────────────────────────────────────────────

    static void UndoLastNotch(RhinoDoc doc, NotchSession s)
    {
      if(s.Moves.TryPeek(out var move)&&move.Order>Math.Max(s.Removals.TryPeek(out var lastRemoval)?lastRemoval.Order:0,s.NotchRecords.LastOrDefault()?.HistoryOrder??0))
      {MoveNotchHistory(doc,s,false);return;}
      if(s.Removals.TryPeek(out var removal)&&removal.Order>(s.NotchRecords.LastOrDefault()?.HistoryOrder??0))
      {MoveRemovalHistory(doc,s,false);return;}
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
          if (id != Guid.Empty) DeleteComponentObjects(doc, id);
        foreach (var id in lastLabelIds)
          if (id.HasValue && id.Value != Guid.Empty) DeleteComponentObjects(doc, id.Value);
      }

      foreach (var record in removedRecords)
      {
        foreach (var id in record.DetachedNotchIds)
          if (id != Guid.Empty) DeleteComponentObjects(doc, id);
        foreach (var id in record.DetachedLabelIds)
          if (id != Guid.Empty) DeleteComponentObjects(doc, id);
      }

      foreach (var record in removedRecords.AsEnumerable().Reverse())
      {
        RestoreOriginalOffsetCurves(doc, record.OffsetSplits);
        if(record.Reuse is {} reuse)RestoreNotchReuse(doc,s,reuse,false);
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
      if(s.MoveRedo.TryPeek(out var move)&&move.Order<Math.Min(s.RemovalRedo.TryPeek(out var nextRemoval)?nextRemoval.Order:long.MaxValue,
          s.RedoBatches.TryPeek(out var nextBatch)?nextBatch.Placements[0].Record.HistoryOrder:long.MaxValue))
      {MoveNotchHistory(doc,s,true);return;}
      if(s.RemovalRedo.TryPeek(out var removal)&&
        (s.RedoBatches.Count==0||removal.Order<s.RedoBatches.Peek().Placements[0].Record.HistoryOrder))
      {MoveRemovalHistory(doc,s,true);return;}
      if (s.RedoBatches.Count == 0) return;

      var batch = s.RedoBatches.Pop();
      int restoredObjects = 0;
      foreach (var placement in batch.Placements)
      {
        if(placement.Record.Reuse is {} reuse)RestoreNotchReuse(doc,s,reuse,true);
        RestoreSplitOffsetPieces(doc, placement.Record.OffsetSplits);
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
          placement.Notches.Add(CaptureComponentObject(doc, notchId));
          placement.Labels.Add(CaptureComponentObject(doc, labelId ?? Guid.Empty));
        }

        foreach (var id in record.DetachedNotchIds)
        {
          var snapshot = CaptureComponentObject(doc, id);
          if (snapshot != null) placement.DetachedNotches.Add(snapshot);
        }
        foreach (var id in record.DetachedLabelIds)
        {
          var snapshot = CaptureComponentObject(doc, id);
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
      return new DocObjectSnapshot(geometry, obj.Attributes.Duplicate()){Serial=obj.RuntimeSerialNumber};
    }

    static DocObjectSnapshot? CaptureComponentObject(RhinoDoc doc, Guid objectId)
    {
      var snapshot = CaptureDocObject(doc, objectId);
      if (snapshot == null)
        return null;

      foreach (var component in RelatedComponentObjects(doc, objectId))
      {
        if (component.Id == objectId)
          continue;
        var componentSnapshot = CaptureDocObject(doc, component.Id);
        if (componentSnapshot != null)
          snapshot.Components.Add(componentSnapshot);
      }
      return snapshot;
    }

    static IReadOnlyList<RhinoObject> RelatedComponentObjects(RhinoDoc doc, Guid objectId)
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

    static void DeleteComponentObjects(RhinoDoc doc, Guid objectId)
    {
      foreach (var obj in RelatedComponentObjects(doc, objectId))
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
      if(s.EditMode||s.Curves.Count==0||s.ExistingNotchHover||s.ExistingNotchDragging||NotchRemovalPicking.ControlPressed){s.PreviewValid=false;return;}
      if (s.Panel != null && !s.Panel.ViewportPreviewActive && !s.MultipleHoverPreviewActive)
      {
        s.PreviewValid = false;
        return;
      }
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
      bool   canLabel = s.LabelToggle.CurrentValue && HasLabelText(ltext,s.LabelPrefixText) && lsize > doc.ModelAbsoluteTolerance;
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

      var boundaryChoice=DisconnectedBoundaryChoice(s,refIdx,lengths[refIdx],snapPoint);
      ResolvePlacementCurve(s, refIdx, lengths[refIdx], boundaryChoice,
        out var referenceCurve, out double referenceLength);
      var snappedKinkChoice = curveSnapActive
        ? ResolveKinkChoice(referenceCurve, referenceLength, snapPoint)
        : KinkTangentChoice.Default;
      bool forceSnappedMiddle = snappedKinkChoice == KinkTangentChoice.Middle;
      var effectiveCursorPoint = forceSnappedMiddle ? snapPoint : cursorPoint;
      s.PreviewCursorPoint = effectiveCursorPoint;
      var referenceKinkChoice = boundaryChoice??(forceSnappedMiddle
        ? KinkTangentChoice.Middle
        : ResolveKinkChoice(referenceCurve, referenceLength, effectiveCursorPoint));
      s.PreviewKinkChoice=referenceKinkChoice;

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
            bool bothSides = PlacementCurveBothSides(s, i, hoverLengths[i], hoverKinkChoice);
            var hgeom = NotchGeometryForSides(
              hoverCurve, hoverLength, nl, no, side, nt, nw,
              bothSides, null, hoverKinkChoice);
            if (hgeom == null) continue;
            if (canNotch) foreach (var c in hgeom) PreviewDisplay.DrawCurve(e.Display, c, System.Drawing.Color.Cyan, 1);
            if (canLabel && firstPos)
            {
              string labelCurveSide = ResolvedLabelCurveSide(s.LabelSideFlip);
              foreach (var (plane, _) in NotchLabelPlacements(
                doc, hoverCurve, hoverLength, side, labelCurveSide, nt, bothSides,
                null, hoverKinkChoice, hgeom, ltext, lsize, no,
                s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue, s.LabelPrefixText))
                DrawLabelPreview(e.Display, plane, ltext, lsize, System.Drawing.Color.Cyan, s.LabelPrefixText, PointAtCurveLength(hoverCurve, hoverLength).pt);
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
        bool bothSides = PlacementCurveBothSides(s, i, lengths[i], kinkChoice);
        var geom = NotchGeometryForSides(placementCurve, placementLength, nl, no, side, nt, nw,
          bothSides, curveCursor, kinkChoice);
        if (geom == null) continue;
        if (canNotch)
        {
          foreach (var component in geom)
            PreviewDisplay.DrawCurve(e.Display, component, System.Drawing.Color.Cyan, 1);
        }

        if (canLabel)
        {
          string labelCurveSide = ResolvedLabelCurveSide(s.LabelSideFlip);
          foreach (var (plane, _) in NotchLabelPlacements(
            doc, placementCurve, placementLength, side, labelCurveSide, nt, bothSides,
            curveCursor, kinkChoice, geom, ltext, lsize, no,
            s.LabelOffsetOpt.CurrentValue, s.LabelOffsetYOpt.CurrentValue, s.LabelPrefixText))
            DrawLabelPreview(e.Display, plane, ltext, lsize, System.Drawing.Color.Cyan, s.LabelPrefixText, PointAtCurveLength(placementCurve, placementLength).pt);
        }
      }
    }

    static bool HasLabelText(string? value,string? prefix)=>!string.IsNullOrWhiteSpace(value)||!string.IsNullOrWhiteSpace(prefix);

    static string FormatNotchLabel(string? value, string prefix, Plane plane, Point3d notchPoint)
      => MatchingLabels.FormatNotchLabel(value,prefix,plane,notchPoint);

    static void DrawLabelPreview(DisplayPipeline display, Plane plane, string text, double size, System.Drawing.Color color,
      string prefix = DefaultLabelPrefix, Point3d? notchPoint = null)
    {
      text = FormatNotchLabel(text, prefix, plane, notchPoint ?? plane.Origin);
      try
      {
        using var te = MatchingLabels.CreateText(plane,text,size);
        te.PlainText = text;
        te.DrawForward = false;
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

    static IReadOnlyList<Curve>? NotchGeometryForSides(Curve curve, double lengthFromStart,
      double notchLength, double notchOffset, string side, string notchType, double notchWidth,
      bool bothSides, Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
      bool logOffsetFit = false)
    {
      var primary = NotchGeometry(curve, lengthFromStart, notchLength, notchOffset,
        side, notchType, notchWidth, cursorPoint, kinkChoice, logOffsetFit);
      if (primary == null || !bothSides) return primary;

      string opposite = side == "Left" ? "Right" : "Left";
      var secondary = NotchGeometry(curve, lengthFromStart, notchLength, notchOffset,
        opposite, notchType, notchWidth, cursorPoint, kinkChoice, logOffsetFit);
      if (secondary == null) return null;
      return primary.Concat(secondary).ToList();
    }

    static IReadOnlyList<(Plane plane, string side)> NotchLabelPlacements(
      RhinoDoc doc, Curve curve, double lengthFromStart, string side,
      string labelCurveSide, string notchType, bool bothSides,
      Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
      IReadOnlyList<Curve> geometry, string labelText, double labelSize,
      double notchOffset, double labelOffset, double labelOffsetY, string prefix = DefaultLabelPrefix)
    {
      var placements = new List<(Plane plane, string side)>();
      int componentsPerSide = CanonicalNotchType(notchType) is "T" or OpenVNotchType ? 2 : 1;
      int sideCount = bothSides ? 2 : 1;
      for (int sideIndex = 0; sideIndex < sideCount; sideIndex++)
      {
        string currentSide = sideIndex == 0 ? side : side == "Left" ? "Right" : "Left";
        string currentLabelSide = labelCurveSide;
        GetCurveTangentAndDirection(curve, lengthFromStart, currentSide,
          cursorPoint, kinkChoice, out var tangent, out var direction);
        if (!tangent.IsValid || !direction.IsValid)
          continue;
        var sideGeometry = geometry.Skip(sideIndex * componentsPerSide)
          .Take(componentsPerSide).ToArray();
        if (sideGeometry.Length != componentsPerSide)
          continue;
        var (plane, _, _) = ComputeLabelLayout(doc, curve, lengthFromStart,
          direction, tangent, notchOffset, sideGeometry, labelText, labelSize,
          labelOffset, labelOffsetY, currentLabelSide, prefix);
        if (plane.IsValid)
          placements.Add((plane, currentLabelSide));
      }
      return placements;
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

      if(curve.IsLinear(RhinoMath.ZeroTolerance))
        return MatchingNotches.StraightGeometry(center,tangent,direction,notchLength,notchOffset,notchType,notchWidth);

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
      if(clamped<=0)return (curve.PointAtStart,curve.Domain.T0);
      if(clamped>=total)return (curve.PointAtEnd,curve.Domain.T1);
      if (curve.LengthParameter(clamped, out double t))
        return (curve.PointAt(t), t);
      if (clamped <= 0.0) return (curve.PointAtStart, curve.Domain.T0);
      return (curve.PointAtEnd, curve.Domain.T1);
    }

    static double LengthFromStart(Curve curve, double t)
    {
      var domain = curve.Domain;
      if(t<=domain.T0)return 0;
      if(t>=domain.T1)return curve.GetLength();
      var interval = new Interval(domain.T0, t);
      return curve.GetLength(interval);
    }

    // ── Label placement ───────────────────────────────────────────────────────

    static Plane BuildReadableTextPlane(RhinoDoc doc, Point3d anchor,
      Vector3d tangent, Vector3d direction, Point3d? curvePoint)
      => MatchingLabels.FacingPlane(doc,anchor,tangent,direction,curvePoint);

    static (Plane plane, bool sideFlipped, BoundingBox bbox) ComputeLabelLayout(
      RhinoDoc doc, Curve curve, double lengthFromStart,
      Vector3d direction, Vector3d tangent, double notchOffset,
      IReadOnlyList<Curve>? notchGeom, string labelText, double labelSize,
      double labelOffset, double labelOffsetY, string curveSide, string prefix = DefaultLabelPrefix)
      => MatchingLabels.Layout(doc,curve,lengthFromStart,direction,tangent,notchOffset,notchGeom,
        labelText,labelSize,labelOffset,labelOffsetY,curveSide,prefix);

    // ── Add notch to doc ──────────────────────────────────────────────────────

    static (Guid notch, Guid? label) AddNotch(RhinoDoc doc,
      Curve curve, double lengthFromStart,
      double notchLength, double notchOffset, string side, bool groupEnabled,
      string notchType, double notchWidth,
      bool notchEnabled, bool bothSides, bool labelEnabled, string labelText, double labelSize,
      string notchLayer, string labelLayer,
      double labelOffset, double labelOffsetY,
      string labelCurveSide,
      Point3d? cursorPoint, KinkTangentChoice? kinkChoice,
      Guid sourceCurveId, int curveIndex, string placementMode, string labelPrefix = DefaultLabelPrefix,
      Guid? matchSetId = null)
    {
      GetCurveTangentAndDirection(curve, lengthFromStart, side, cursorPoint, kinkChoice,
        out var tangent, out var direction);
      notchType = CanonicalNotchType(notchType);

      var geom = NotchGeometryForSides(curve, lengthFromStart, notchLength, notchOffset,
        side, notchType, notchWidth, bothSides, cursorPoint, kinkChoice, logOffsetFit: true);
      if (geom == null) return (Guid.Empty, null);

      var metadataAttributes = CreateNotchAttributes(doc, curve, sourceCurveId, curveIndex,
        placementMode, lengthFromStart, notchLength, notchOffset, notchType,
        notchWidth, side, labelEnabled, labelText, labelSize, labelOffset,
        labelOffsetY, labelCurveSide, notchLayer, labelLayer, tangent);
      metadataAttributes.SetUserString(NotchDataPrefix + "both_sides", bothSides ? "1" : "0");
      metadataAttributes.SetUserString(NotchDataPrefix + "label_prefix", labelPrefix);
      metadataAttributes.SetUserString(NotchMatchSetKey,(matchSetId??Guid.NewGuid()).ToString("N"));

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
        if (HasLabelText(lt,labelPrefix) && labelSize > doc.ModelAbsoluteTolerance)
        {
          var placements = NotchLabelPlacements(doc, curve, lengthFromStart,
            side, labelCurveSide, notchType, bothSides, cursorPoint, kinkChoice,
            geom, lt, labelSize, notchOffset, labelOffset, labelOffsetY, labelPrefix);
          if (placements.Count == (bothSides ? 2 : 1))
          {
            string componentSet = bothSides ? Guid.NewGuid().ToString("N") : string.Empty;
            var addedLabels = new List<Guid>();
            for (int labelIndex = 0; labelIndex < placements.Count; labelIndex++)
            {
              var (labelPlane, currentLabelSide) = placements[labelIndex];
              using var te = MatchingLabels.CreateText(labelPlane,lt,labelSize);
              te.PlainText = FormatNotchLabel(labelText, labelPrefix, labelPlane, PointAtCurveLength(curve, lengthFromStart).pt);
              var la = metadataAttributes.Duplicate();
              la.ObjectId = Guid.NewGuid();
              MatchingNotches.SetMember(la,la.ObjectId,Guid.Parse(la.GetUserString(NotchMatchSetKey)!),"label",notchId);
              la.LayerIndex = ResolveLayerIndex(doc, labelLayer);
              la.Name = NotchLabelObjectName;
              la.SetUserString(NotchDataPrefix + "object_role", "label");
              la.SetUserString(NotchDataPrefix + "label_id", la.ObjectId.ToString());
              la.SetUserString(NotchDataPrefix + "label_side", currentLabelSide);
              la.SetUserString(NotchDataPrefix + "notch_id",
                notchId == Guid.Empty ? string.Empty : notchId.ToString());
              if (bothSides)
              {
                la.SetUserString(NotchComponentSetKey, componentSet);
                la.SetUserString(NotchDataPrefix + "component_index",
                  labelIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                la.SetUserString(NotchDataPrefix + "component_count", "2");
              }
              Guid lid = doc.Objects.AddText(te, la);
              if (lid == Guid.Empty)
                break;
              addedLabels.Add(lid);
            }
            if (addedLabels.Count == placements.Count)
              labelId = addedLabels[0];
            else
              foreach (var id in addedLabels)
                doc.Objects.Delete(id, quiet: true);
          }
        }
      }

      var outputIds = RelatedComponentObjects(doc, notchId)
        .Concat(RelatedComponentObjects(doc, labelId ?? Guid.Empty))
        .Select(obj => obj.Id).Distinct().ToArray();
      if (outputIds.Length > 0)
        DuplicateCommandSupport.ApplySourceGroups(doc, sourceCurveId, outputIds, groupEnabled);
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
        MatchingNotches.SetMember(attrs,attrs.ObjectId,Guid.Parse(attrs.GetUserString(NotchMatchSetKey)!),"notch");
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
      var attributes=MatchingNotches.Attributes(sourceCurve,sourceCurveId,curveIndex,placementMode,lengthFromStart,
        notchLength,notchOffset,notchType,notchWidth,curveSide,labelEnabled,labelText,labelSize,labelOffset,
        labelOffsetY,labelSide,notchLayer,labelLayer,tangent,ResolveLayerIndex(doc,notchLayer));
      MatchingNotches.BindReference(doc,attributes,sourceCurve,lengthFromStart);return attributes;
    }

    static List<(Guid notch, Guid? label)> AddNotchesPerCurve(
      RhinoDoc doc, NotchSession s,
      List<double> lengths, double notchLen, double notchOff,
      string notchTyp, double notchWid,
      bool canNotch, bool canLabel, List<string> labelValues, double labelSize,
      string notchLayer, string labelLayer,
      double labelOffset, double labelOffsetY,
      bool labelSideFlip, Point3d? cursorPoint, KinkTangentChoice referenceKinkChoice,
      bool[] curveEnabled, string placementMode, Guid matchSetId)
    {
      var ids = new List<(Guid, Guid?)>();
      int referenceIndex = cursorPoint.HasValue ? s.PreviewRefCurveIndex : -1;
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
        bool bothSides = PlacementCurveBothSides(s, i, lengths[i], kinkChoice);
        string labelCurveSide = ResolvedLabelCurveSide(labelSideFlip);
        Guid sourceCurveId = ResolvePlacementSourceCurveId(
          doc, s, i, lengths[i], kinkChoice);
        var (nid, lid) = AddNotch(doc, placementCurve, placementLength,
          notchLen, notchOff, side, s.GroupToggle.CurrentValue,
          notchTyp, notchWid,
          canNotch, bothSides, canLabel, lv, labelSize,
          notchLayer, labelLayer,
          labelOffset, labelOffsetY,
          labelCurveSide, curveCursor, kinkChoice,
          s.Sources.ParentId(sourceCurveId), i, placementMode, s.LabelPrefixText, matchSetId);

        StoreLinkedNotchSources(doc,s,i,nid,lid,placementCurve,placementLength);

        if (nid != Guid.Empty || lid.HasValue)
        {
          GetCurveTangentAndDirection(placementCurve, placementLength, side,
            curveCursor, kinkChoice, out var resolvedTangent, out var resolvedDirection);
          vTools.Log.Write("vNotches",
            $"placed curve={i + 1} source={sourceCurveId} side={side} ref={referenceIndex + 1} " +
            $"kink={referenceKinkChoice} group={s.GroupToggle.CurrentValue} " +
            $"sourceGroups={string.Join(",", s.Sources.FindObject(doc, sourceCurveId)?.Attributes.GetGroupList() ?? [])} " +
            $"tangent=({resolvedTangent.X:0.###},{resolvedTangent.Y:0.###}) " +
            $"direction=({resolvedDirection.X:0.###},{resolvedDirection.Y:0.###})");
        }

        ids.Add((nid, lid));
      }
      return ids;
    }

    static void StoreLinkedNotchSources(RhinoDoc doc,NotchSession s,int index,Guid notch,Guid? label,Curve placementCurve,double station)
    {
      if(index<0||index>=s.PerCurveSourceIds.Count)return;
      var ids=s.PerCurveSourceIds[index].Select(s.Sources.ParentId).ToArray();
      var reversed=s.PerCurveSegments[index].Select((segment,i)=>doc.Objects.FindId(ids[i])?.Geometry is Curve source&&
        source.PointAtEnd.DistanceTo(segment.PointAtStart)<source.PointAtStart.DistanceTo(segment.PointAtStart)).ToArray();
      foreach(var obj in RelatedComponentObjects(doc,notch).Concat(RelatedComponentObjects(doc,label??Guid.Empty)).DistinctBy(obj=>obj.Id))
      {
        using var attributes=obj.Attributes.Duplicate();
        attributes.SetUserString(MatchingNotches.SourceCurveIdsKey,System.Text.Json.JsonSerializer.Serialize(ids));
        attributes.SetUserString(NotchDataPrefix+"source_reversed",System.Text.Json.JsonSerializer.Serialize(reversed));
        MatchingNotches.BindReference(doc,attributes,placementCurve,station,s.PerCurveSegments[index].Select((segment,i)=>(ids[i],segment)).ToArray());
        doc.Objects.ModifyAttributes(obj.Id,attributes,true);
      }
    }

    static KinkTangentChoice? DisconnectedBoundaryChoice(NotchSession s,int curveIndex,double length,Point3d cursor)
    {
      if(curveIndex<0||curveIndex>=s.PerCurveSegments.Count)return null;
      var segments=s.PerCurveSegments[curveIndex];double station=0,tolerance=s.Doc.ModelAbsoluteTolerance;
      for(int index=0;index+1<segments.Count;index++)
      {
        station+=segments[index].GetLength();
        if(Math.Abs(length-station)>tolerance||segments[index].PointAtEnd.DistanceTo(segments[index+1].PointAtStart)<=tolerance)continue;
        return cursor.DistanceToSquared(segments[index+1].PointAtStart)<cursor.DistanceToSquared(segments[index].PointAtEnd)
          ?KinkTangentChoice.After:KinkTangentChoice.Before;
      }
      return null;
    }

    static KinkTangentChoice? DisconnectedBoundaryChoice(NotchSession s,int curveIndex,double length,Guid source)
    {
      if(curveIndex<0||curveIndex>=s.PerCurveSourceIds.Count)return null;
      int index=s.PerCurveSourceIds[curveIndex].IndexOf(source);
      if(index<0||index>=s.PerCurveSegments[curveIndex].Count)return null;
      var segment=s.PerCurveSegments[curveIndex][index];
      double start=s.PerCurveSegments[curveIndex].Take(index).Sum(curve=>curve.GetLength());
      var point=Math.Abs(length-start)<=s.Doc.ModelAbsoluteTolerance?segment.PointAtStart:segment.PointAtEnd;
      return DisconnectedBoundaryChoice(s,curveIndex,length,point);
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

    static bool PlacementCurveBothSides(NotchSession s, int curveIndex,
      double lengthFromStart, KinkTangentChoice? kinkChoice)
    {
      if (curveIndex < 0 || curveIndex >= s.Curves.Count)
        return s.BothSidesToggle.CurrentValue;
      Guid sourceId = ResolvePlacementSourceCurveId(
        s.Doc, s, curveIndex, lengthFromStart, kinkChoice);
      return s.CurveBothSidesBySource.GetValueOrDefault(
        sourceId, s.BothSidesToggle.CurrentValue);
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
        if (id != Guid.Empty) DeleteComponentObjects(doc, id);
      }
      while (s.LabelIdsByCurve[curveIndex].Count > 0)
      {
        var id = s.LabelIdsByCurve[curveIndex][^1];
        s.LabelIdsByCurve[curveIndex].RemoveAt(s.LabelIdsByCurve[curveIndex].Count - 1);
        if (id.HasValue && id.Value != Guid.Empty) DeleteComponentObjects(doc, id.Value);
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
      foreach (var record in s.NotchRecords)
      {
        var rec=record.EditOverrides.GetValueOrDefault(s.CurveIds[curveIndex],record);
        bool recordHadCurveEnabled =
        record.CurveEnabled == null ||
        record.CurveEnabled.Count == 0 ||
        (curveIndex < record.CurveEnabled.Count && record.CurveEnabled[curveIndex]);

        if (!recordHadCurveEnabled)
        {
          newIds.Add(Guid.Empty);
          newLabelIds.Add(null);
          continue;
        }
        double d = LengthFromRecord(s, record, curveIndex);
        bool lbl = rec.LabelEnabled&&!s.RemovedSlots.Contains((record,s.CurveIds[curveIndex],true));
        string lv = (rec.LabelValues != null && curveIndex < rec.LabelValues.Count)
          ? rec.LabelValues[curveIndex] : "";
        KinkTangentChoice? kinkChoice = rec.KinkChoice == KinkTangentChoice.Default
          ? null
          : rec.KinkChoice;
        if(record.PlacementSources.TryGetValue(s.CurveIds[curveIndex],out var boundarySource))
          kinkChoice=DisconnectedBoundaryChoice(s,curveIndex,d,boundarySource)??kinkChoice;
        ResolvePlacementCurve(s, curveIndex, d, kinkChoice,
          out var placementCurve, out double placementLength);
        string side = PlacementCurveSide(s, curveIndex, d, kinkChoice);
        string labelCurveSide = ResolvedLabelCurveSide(rec.LabelDirectionOverride??s.LabelSideFlip);
        Guid sourceCurveId = ResolvePlacementSourceCurveId(
          doc, s, curveIndex, d, kinkChoice);
        var (nid, lid) = AddNotch(doc, placementCurve, placementLength,
          rec.NotchLength, rec.NotchOffset, side, rec.GroupEnabled,
          rec.NotchType, rec.NotchWidth,
          rec.NotchEnabled&&!s.RemovedSlots.Contains((record,s.CurveIds[curveIndex],false)), rec.BothSidesOverride??PlacementCurveBothSides(s, curveIndex, d, kinkChoice),
          lbl, lv, rec.LabelSize,
          EffectiveLayerName(doc, rec.NotchLayer, rec.NotchLayer),
          EffectiveLayerName(doc, rec.LabelLayer, rec.NotchLayer),
          rec.LabelOffset, rec.LabelOffsetY, labelCurveSide, null,
          kinkChoice,
          s.Sources.ParentId(sourceCurveId),
          curveIndex, rec.Mode, rec.LabelPrefix, rec.MatchSetId);
        StoreLinkedNotchSources(doc,s,curveIndex,nid,lid,placementCurve,placementLength);
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
      bool oldSide = s.CurveSideBySource.GetValueOrDefault(sourceId, s.CurveSides[idx]);
      s.CurveSideBySource[sourceId] = !oldSide;
      UpdateLogicalCurveSide(s, idx);
      RebuildCurveNotches(doc, s, idx);
      if (!RefreshOffsetSplits(doc, s))
      {
        s.CurveSideBySource[sourceId] = oldSide;
        UpdateLogicalCurveSide(s, idx);
        RebuildCurveNotches(doc, s, idx);
      }
      else
        ClearNotchRedo(s);
      SelectBothCurves(doc, s);
      s.Panel?.UpdateUndoEnabled();
    }

    static bool SetCurveBothSides(RhinoDoc doc, NotchSession s, int curveIndex,
      Guid sourceId, bool enabled)
    {
      if (curveIndex < 0 || curveIndex >= s.Curves.Count || sourceId == Guid.Empty)
        return false;
      bool previous = s.CurveBothSidesBySource.GetValueOrDefault(
        sourceId, s.BothSidesToggle.CurrentValue);
      s.CurveBothSidesBySource[sourceId] = enabled;
      RebuildCurveNotches(doc, s, curveIndex);
      if (!RefreshOffsetSplits(doc, s))
      {
        s.CurveBothSidesBySource[sourceId] = previous;
        RebuildCurveNotches(doc, s, curveIndex);
        return false;
      }
      s.BothSidesToggle.CurrentValue = enabled;
      ClearNotchRedo(s);
      s.Panel?.UpdateUndoEnabled();
      return true;
    }

    static bool SetNotchTrim(RhinoDoc doc, NotchSession s, NotchTrimMode mode)
    {
      if (!Enum.IsDefined(mode))
        return false;
      if (s.NotchTrim == mode)
        return true;
      var previous = s.NotchTrim;
      s.NotchTrim = mode;
      if (!RefreshOffsetSplits(doc, s))
      {
        s.NotchTrim = previous;
        return false;
      }
      SaveOptions(s);
      ClearNotchRedo(s);
      s.Panel?.RefreshNotchTrimIcon();
      s.Panel?.UpdateUndoEnabled();
      return true;
    }

    static void ReverseSourceCurve(RhinoDoc doc, NotchSession s, int idx, Guid sourceId)
    {
      if(s.EditMode){ReverseEditedNotchSource(doc,s,sourceId);return;}
      if (idx < 0 || idx >= s.Curves.Count ||
          idx >= s.PerCurveSourceIds.Count || idx >= s.PerCurveSegments.Count)
        return;
      int sourceIndex = s.PerCurveSourceIds[idx].IndexOf(sourceId);
      if (sourceIndex < 0 || sourceIndex >= s.PerCurveSegments[idx].Count)
        return;
      var previousLengths = s.NotchRecords
        .Select(record => idx < record.LengthsFromStart.Count
          ? (double?)record.LengthsFromStart[idx] : null)
        .ToArray();
      bool wasReversed = s.CurveReversedBySource.GetValueOrDefault(sourceId);
      bool oldSide = s.CurveSideBySource.GetValueOrDefault(sourceId, s.CurveSides[idx]);
      double prefix = s.PerCurveSegments[idx]
        .Take(sourceIndex)
        .Sum(segment => segment.GetLength());
      double sourceLength = s.PerCurveSegments[idx][sourceIndex].GetLength();
      double tolerance = Math.Max(doc.ModelAbsoluteTolerance, RhinoMath.ZeroTolerance);
      foreach (var rec in s.NotchRecords)
      {
        if (rec.LengthsFromStart == null || idx >= rec.LengthsFromStart.Count) continue;
        if(rec.PlacementSources.TryGetValue(s.CurveIds[idx],out var boundarySource)&&boundarySource!=sourceId)continue;
        double old = rec.LengthsFromStart[idx];
        if (old < prefix - tolerance || old > prefix + sourceLength + tolerance)
          continue;
        double local = Clamp(old - prefix, 0.0, sourceLength);
        rec.LengthsFromStart[idx] = prefix + sourceLength - local;
      }
      s.ClearPlacementRuns();
      s.PerCurveSegments[idx][sourceIndex].Reverse();
      s.CurveReversedBySource[sourceId] =
        !s.CurveReversedBySource.GetValueOrDefault(sourceId);
      s.CurveSideBySource[sourceId] = !oldSide;
      s.Curves[idx].Dispose();
      s.Curves[idx] = BuildLayoutCurve(doc, s.PerCurveSegments[idx], out bool continuous);
      s.CurveIsContinuous[idx] = continuous;
      UpdateLogicalCurveSide(s, idx);
      RebuildCurveNotches(doc, s, idx);
      if (!RefreshOffsetSplits(doc, s))
      {
        for (int recordIndex = 0; recordIndex < s.NotchRecords.Count; recordIndex++)
          if (previousLengths[recordIndex].HasValue &&
              idx < s.NotchRecords[recordIndex].LengthsFromStart.Count)
            s.NotchRecords[recordIndex].LengthsFromStart[idx] =
              previousLengths[recordIndex]!.Value;
        s.ClearPlacementRuns();
        s.PerCurveSegments[idx][sourceIndex].Reverse();
        s.CurveReversedBySource[sourceId] = wasReversed;
        s.CurveSideBySource[sourceId] = oldSide;
        s.Curves[idx].Dispose();
        s.Curves[idx] = BuildLayoutCurve(doc, s.PerCurveSegments[idx], out continuous);
        s.CurveIsContinuous[idx] = continuous;
        UpdateLogicalCurveSide(s, idx);
        RebuildCurveNotches(doc, s, idx);
      }
      else
        ClearNotchRedo(s);
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
      s.AssociatedCopies=s.Associations&&!s.EditMode?MatchingNotches.NestedCurveCopies(doc,s.PerCurveSourceIds.SelectMany(ids=>ids).Select(s.Sources.ParentId)):[];
      s.EditPicking?.RefreshAssociations();
      NotchCurveSources.ClearSelection(doc);
      foreach (var sourceId in s.PerCurveSourceIds.SelectMany(ids => ids))
        s.Sources.Select(doc, sourceId, true);
      var toSelect = s.PerCurveSourceIds.Count > 0
        ? OffsetSourceCurveIds(s)
        : (IEnumerable<Guid>)s.CurveIds;
      var parents = s.PerCurveSourceIds.SelectMany(ids => ids).Select(s.Sources.ParentId).ToHashSet();
      foreach (var id in toSelect)
        if (!parents.Contains(id))
          doc.Objects.FindId(id)?.Select(true);
      doc.Views.Redraw();
    }
    static Guid SourceFeedbackId(NotchSession s,Guid source)=>s.Sources.IsSegment(source)?source:s.Sources.ParentId(source);

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

    static string ResolvedLabelCurveSide(bool towardStart) => towardStart ? "Left" : "Right";

    static bool ReadLabelDirection(System.Text.Json.Nodes.JsonObject? settings, bool fallbackFirstLeft)
    {
      if (ToolsOptionStore.TryGetBool(settings, LabelDirectionSettingsKey, out var towardStart))
        return towardStart;
      if (!ToolsOptionStore.TryGetBool(settings, "label_side_flip", out var legacyFlip))
        return ConfiguredDefaultLabelSideFlip;
      bool firstLeft = fallbackFirstLeft;
      if (settings?["curve_sides"] is System.Text.Json.Nodes.JsonArray { Count: > 0 } sides &&
          sides[0] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<bool>(out var savedLeft))
        firstLeft = savedLeft;
      return firstLeft ^ legacyFlip;
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

    static string IncrementLabelValue(string text) => LabelSequences.Increment(text, ConfiguredDefaultLabelValue);

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
      public readonly NotchCurveSources Sources;
      public readonly HashSet<Guid> SelectedNotches=[];
      public readonly HashSet<Guid> EditedNotchObjects=[];
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
      public bool Associations=_associations;
      public List<MatchingNotches.CurveCopy> AssociatedCopies=[];
      public OptionToggle NotchToggle;
      public OptionToggle BothSidesToggle;
      public NotchTrimMode NotchTrim = ConfiguredDefaultNotchTrim;
      public string NotchTrimLayer = ConfiguredDefaultNotchTrimLayer;
      public OptionToggle LabelToggle;
      public OptionToggle LabelSizeAutoToggle;

      public string LabelValueText;
      public string LabelPrefixText = ConfiguredDefaultLabelPrefix;
      public double ManualLabelSize;
      public string NotchLayerName;
      public string LabelLayerName;
      public bool LabelAutoAdv => !string.IsNullOrWhiteSpace(LabelValueText);
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
      [global::vTools.CommandChoicesAttribute("vNotches", "notch_type", false, "I", "V", "\\/", "U", "T")] public readonly string[] NotchTypeValues = global::vTools.CommandChoices.Values(typeof(global::vTools.Commands.vNotches.NotchSession), nameof(NotchTypeValues));
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
      public long HistoryOrder;
      public readonly Stack<NotchRemoval> Removals=[];
      public readonly Stack<NotchRemoval> RemovalRedo=[];
      public readonly Stack<NotchMove> Moves=[];
      public readonly Stack<NotchMove> MoveRedo=[];
      public bool ExistingNotchHover,ExistingNotchDragging;
      public bool NotchMouseDrag;
      public NotchRemovalPicking? EditPicking;
      public bool NotchDragMoved,NotchMovePointerUpdated;
      public System.Drawing.Point NotchDragStart;
      public bool EditMode;
      public bool RepairPairing;
      public NotchEditTool EditTool=NotchEditTool.None;
      public object? PendingInput;
      public bool AssigningSource;
      public Guid LinkAnchor;
      public bool OppositeMatches=_oppositeMatches;
      public Guid EditedNotchId;
      public bool HighlightExisting=ToolsOptionStore.Read(Section,json=>ToolsOptionStore.TryGetBool(json,"highlight_existing",out bool enabled)?enabled:
        CommandDefaults.Get(Section,"vTools.Commands.vNotches.DefaultHighlightExisting",DefaultHighlightExisting));
      public readonly HashSet<(NotchRecord Record,Guid Curve,bool Label)> RemovedSlots=[];

      // Per-curve source IDs — one inner list per curve slot; multiple IDs for joined chains.
      public readonly List<List<Guid>> PerCurveSourceIds;
      // Oriented physical source geometry in the same order as PerCurveSourceIds.
      public readonly List<List<Curve>> PerCurveSegments;
      public readonly List<bool> CurveIsContinuous;
      public readonly Dictionary<int, NotchPlacementRuns> ConnectedPlacementRuns = [];
      public readonly Dictionary<Guid, int> CurveDisplayNumbers = [];
      public readonly Dictionary<Guid, bool> CurveSideBySource = [];
      public readonly Dictionary<Guid, bool> CurveBothSidesBySource = [];
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
      public int TypeOptionIndex, NotchTrimIndex, NotchTrimLayerIndex, NotchLayerOptionIndex, NotchEnabledIndex, BothSidesIndex, LabelEnabledIndex;
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
      public KinkTangentChoice PreviewKinkChoice;
      public bool MultipleHoverPreviewActive;
      public List<MultiplePlacementPlan>? MultipleHoverPlans;
      public readonly Stack<NotchUndoBatch> RedoBatches = [];
      public NotchSession(RhinoDoc doc, List<Curve> curves, List<Guid> curveIds, bool[] sides,
        double notchLength, double notchOffset, double notchWidth, string notchType, bool notch,
        bool bothSides,
        bool percent, bool group, bool label, string labelValue,
        double labelSize, bool labelSizeAuto, int labelSizePct,
        string notchLayer, string labelLayer, double labelOffset, double labelOffsetY,
        bool labelSideFlip, bool keepSelection,
        double multipleStartOffset, double multipleEndOffset,
        bool multipleStartOffsetEnabled, bool multipleEndOffsetEnabled, int multipleNumber,
        double multipleDistance, bool multipleUseDistance,
        bool multipleAuto, int multipleCurvatureSensitivity,
        bool multipleSeparate, NotchCurveSources sourceRegistry)
      {
        Doc      = doc;
        Sources  = sourceRegistry;
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
        BothSidesToggle   = new OptionToggle(bothSides,     "Off", "On");
        LabelToggle       = new OptionToggle(label,         "Off", "On");
        LabelSizeAutoToggle = new OptionToggle(labelSizeAuto, "Manual", "Auto");

        LabelValueText = labelValue ?? "A";
        ManualLabelSize = Math.Max(0.0, labelSize);
        NotchLayerName  = notchLayer ?? SpecialLayerCurrent;
        LabelLayerName  = labelLayer ?? "PLOT";
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

        NotchIdsByCurve = curves.Select(_ => new List<Guid>()).ToList();
        LabelIdsByCurve = curves.Select(_ => new List<Guid?>()).ToList();
        PerCurveSourceIds = curveIds.Select(id => new List<Guid> { id }).ToList();
        PerCurveSegments = curves
          .Select(curve => new List<Curve> { curve.DuplicateCurve() })
          .ToList();
        CurveIsContinuous = Enumerable.Repeat(true, curves.Count).ToList();
        for (int curveIndex = 0; curveIndex < PerCurveSourceIds.Count; curveIndex++)
          foreach (var sourceId in PerCurveSourceIds[curveIndex])
          {
            CurveSideBySource[sourceId] =
              curveIndex < CurveSides.Length && CurveSides[curveIndex];
            CurveBothSidesBySource[sourceId] = bothSides;
          }
        EnsureCurveDisplayNumbers();
      }

      public void ClearPlacementRuns()
      {
        foreach (var runs in ConnectedPlacementRuns.Values) runs.Dispose();
        ConnectedPlacementRuns.Clear();
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
      public long HistoryOrder;
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
      public string         LabelPrefix    = ConfiguredDefaultLabelPrefix;
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
      public readonly Dictionary<Guid,Guid> PlacementSources=[];
      public Guid MatchSetId;
      public NotchReuse? Reuse;
      public readonly Dictionary<Guid,NotchRecord> EditOverrides=[];
      public bool? BothSidesOverride,LabelDirectionOverride;
      public List<OffsetSplitEdit> OffsetSplits = [];
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
        CaptureUserStrings();
      }

      public GeometryBase Geometry { get; }
      public ObjectAttributes Attributes { get; }
      public uint Serial;
      Dictionary<string,string> _userStrings=[];
      public void CaptureUserStrings()
      {var values=Attributes.GetUserStrings();_userStrings=values.AllKeys.Where(key=>key!=null).ToDictionary(key=>key!,key=>values[key!]??string.Empty);}
      public bool HasUserStrings(ObjectAttributes attributes)
      {var values=attributes.GetUserStrings();return values.Count==_userStrings.Count&&_userStrings.All(item=>values[item.Key]==item.Value);}
      public ObjectAttributes RestoreAttributes()
      {
        var attributes=Attributes.Duplicate();
        RestoreUserStrings(attributes,true);return attributes;
      }
      public void RestoreUserStrings(ObjectAttributes attributes,bool replace=false)
      {
        foreach(string? key in attributes.GetUserStrings().AllKeys)
          if(key!=null&&(replace||!_userStrings.ContainsKey(key)))attributes.SetUserString(key,null);
        foreach(var item in _userStrings)
          if(replace||attributes.GetUserString(item.Key)!=item.Value)attributes.SetUserString(item.Key,item.Value);
      }
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
      const int NotchTrimIconWidth = 30; // Compact Type-row toggle width in device-independent pixels.
      const int NotchTrimIconHeight = 20; // Compact Type-row toggle height in device-independent pixels.
      const double NotchTrimCurveStroke = 1.7; // Original curve and notch stroke thickness in device-independent pixels.
      const double NotchTrimSplitStroke = 3.0; // Emphasized between-leg split stroke thickness in device-independent pixels.
      const int CurveRowSpacing = 0; // Vertical space between adjacent curve rows in device-independent pixels.
      const int CurveRowControlSpacing = 3; // Horizontal space between compact curve-row controls in device-independent pixels.
      const int CurveDragHandleWidth = 16; // Width of the only cursor and drag-sensitive handle area; matches link buttons.
      const float CurveDragDotDiameter = 2.0f; // Diameter of each drawn handle dot in device-independent pixels.
      const float CurveDragDotGap = 3.0f; // Equal edge-to-edge spacing between handle dots.
      const int CurveSideButtonWidth = 22; // Width of the borderless up/down side control in device-independent pixels.
      const int CurveBothSidesButtonWidth = 22; // Width of the two-sided notch icon control in device-independent pixels.
      const int CurveReverseButtonWidth = 22; // Width of the borderless single-arrow reverse control in device-independent pixels.
      const int CurveDirectionButtonHeight = 26; // Height of both native direction buttons in device-independent pixels.
      [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveDirectionButtonHorizontalPadding", "Horizontal padding around Side and Reverse arrow glyphs in device-independent pixels.")] const double CurveDirectionButtonHorizontalPadding = 0.0; // Horizontal padding around Side and Reverse arrow glyphs in device-independent pixels.

       static readonly double ConfiguredCurveDirectionButtonHorizontalPadding = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveDirectionButtonHorizontalPadding", CurveDirectionButtonHorizontalPadding); // Resolved once from advanced defaults; restart Rhino after changes.
      [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveDirectionButtonVerticalPadding", "Vertical padding around Side and Reverse arrow glyphs in device-independent pixels.")] const double CurveDirectionButtonVerticalPadding = 1.0; // Vertical padding around Side and Reverse arrow glyphs in device-independent pixels.

       static readonly double ConfiguredCurveDirectionButtonVerticalPadding = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveDirectionButtonVerticalPadding", CurveDirectionButtonVerticalPadding); // Resolved once from advanced defaults; restart Rhino after changes.
      const double CurveDirectionButtonFontSize = 18.0; // Font size of Side and Reverse arrow glyphs in device-independent pixels.
      [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveLengthBadgeHorizontalPadding", "Equal left/right padding inside individual and cumulative colored length badges.")] const int CurveLengthBadgeHorizontalPadding = 2; // Equal left/right padding inside individual and cumulative colored length badges.

       static readonly int ConfiguredCurveLengthBadgeHorizontalPadding = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveLengthBadgeHorizontalPadding", CurveLengthBadgeHorizontalPadding); // Resolved once from advanced defaults; restart Rhino after changes.
      const int CurveLengthBadgeWidthAllowance = 2; // Extra cumulative-badge width protecting the final digit from layout rounding.
      const string CurveSideCheckedGlyph = "🠝"; // Glyph shown when the curve's Side state is enabled.
      const string CurveSideUncheckedGlyph = "🠟"; // Glyph shown when the curve's Side state is disabled.
      const string CurveReverseForwardGlyph = "🠞"; // Glyph shown before the source curve has been reversed.
      const string CurveReverseBackwardGlyph = "🠜"; // Glyph shown after the source curve has been reversed.
      const string CurveBothSidesOffPath = "M11,5 L11,21 M4,8 L9,13 L4,18"; // Compact centerline and one notch mark, in a 22-by-26 icon canvas.
      const string CurveBothSidesOnPath = "M11,5 L11,21 M4,8 L9,13 L4,18 M18,8 L13,13 L18,18"; // Compact centerline and opposing marks, in a 22-by-26 icon canvas.
      [global::vTools.CommandDefault("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveLengthDifferenceToleranceInches", "Smallest longest-to-shortest span considered significant, converted to model units.")] const double CurveLengthDifferenceToleranceInches = 1.0 / 16.0; // Smallest longest-to-shortest span considered significant, converted to model units.

       static readonly double ConfiguredCurveLengthDifferenceToleranceInches = global::vTools.CommandDefaults.Get("vNotches", "vTools.Commands.vNotches.NotchPanel.CurveLengthDifferenceToleranceInches", CurveLengthDifferenceToleranceInches); // Resolved once from advanced defaults; restart Rhino after changes.
      const float CurveLengthDifferenceFontSize = 8.0f; // Font size of the signed longest/shortest superscript delta in device-independent pixels.
      const double CurveLengthDifferenceRaise = 3.0; // Upward superscript shift applied to signed length deltas in device-independent pixels.
      const double CurveDragRowOutlineWidth = 1.0; // Outline thickness around the row currently being dragged.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.CurveLengthLongerColor","ARGB Text color for the positive length difference of the longest linked curve.","#FF0D7A2E")]
      static readonly Eto.Drawing.Color CurveLengthLongerColor = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.CurveLengthLongerColor",new Eto.Drawing.Color(0.05f,0.48f,0.18f)); // ARGB Text color for the positive length difference of the longest linked curve.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.CurveLengthShorterColor","ARGB Text color for the negative length difference of the shortest linked curve.","#FFC7141A")]
      static readonly Eto.Drawing.Color CurveLengthShorterColor = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.CurveLengthShorterColor",new Eto.Drawing.Color(0.78f,0.08f,0.10f)); // ARGB Text color for the negative length difference of the shortest linked curve.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.PercentLengthWarningBackground","ARGB Background of the Percent warning when linked curves have significantly different lengths.","#FFFFD638")]
      static readonly Eto.Drawing.Color PercentLengthWarningBackground = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.PercentLengthWarningBackground",new Eto.Drawing.Color(1.0f,0.84f,0.22f)); // ARGB Background of the Percent warning when linked curves have significantly different lengths.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.PercentLengthWarningForeground","ARGB Text color of the Percent length-difference warning.","#FF000000")]
      static readonly Eto.Drawing.Color PercentLengthWarningForeground = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.PercentLengthWarningForeground",new Eto.Drawing.Color(0,0,0)); // ARGB Text color of the Percent length-difference warning.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup1Color","ARGB background for linked length-group badge 1.","#FFAD141F")]
      static readonly Eto.Drawing.Color LengthGroup1Color = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup1Color",new(0.68f,0.08f,0.12f)); // ARGB first length-group badge color.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup2Color","ARGB background for linked length-group badge 2.","#FF054FA8")]
      static readonly Eto.Drawing.Color LengthGroup2Color = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup2Color",new(0.02f,0.31f,0.66f)); // ARGB second length-group badge color.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup3Color","ARGB background for linked length-group badge 3.","#FF0D7333")]
      static readonly Eto.Drawing.Color LengthGroup3Color = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup3Color",new(0.05f,0.45f,0.20f)); // ARGB third length-group badge color.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup4Color","ARGB background for linked length-group badge 4.","#FF7A299E")]
      static readonly Eto.Drawing.Color LengthGroup4Color = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup4Color",new(0.48f,0.16f,0.62f)); // ARGB fourth length-group badge color.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup5Color","ARGB background for linked length-group badge 5.","#FFB85205")]
      static readonly Eto.Drawing.Color LengthGroup5Color = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup5Color",new(0.72f,0.32f,0.02f)); // ARGB fifth length-group badge color.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup6Color","ARGB background for linked length-group badge 6.","#FF006E73")]
      static readonly Eto.Drawing.Color LengthGroup6Color = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.LengthGroup6Color",new(0.00f,0.43f,0.45f)); // ARGB sixth length-group badge color.
      static readonly Eto.Drawing.Color[] CurveLengthGroupBackgrounds =
        [LengthGroup1Color,LengthGroup2Color,LengthGroup3Color,LengthGroup4Color,LengthGroup5Color,LengthGroup6Color]; // Cyclic ARGB badge palette for different curve lengths.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.CurveLengthGroupForeground","ARGB Text color over linked length-group badges.","#FFFFFFFF")]
      static readonly Eto.Drawing.Color CurveLengthGroupForeground = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.CurveLengthGroupForeground",new Eto.Drawing.Color(1,1,1)); // ARGB Text color over linked length-group badges.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.CurveRowHoverBackground","ARGB Background of the hovered or dragged curve-row handle.","#FFCCE6FF")]
      static readonly Eto.Drawing.Color CurveRowHoverBackground = CommandDefaults.GetEtoColor("vNotches","vTools.Commands.vNotches.NotchPanel.CurveRowHoverBackground",Eto.Drawing.Color.FromArgb(204,230,255)); // ARGB Background of the hovered or dragged curve-row handle.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.BothSidesOnColor","ARGB stroke of the active two-sided notch icon.","#FF0676C9")]
      static readonly System.Drawing.Color BothSidesOnColor = CommandDefaults.GetColor("vNotches","vTools.Commands.vNotches.NotchPanel.BothSidesOnColor",System.Drawing.Color.FromArgb(6,118,201)); // ARGB active two-sided icon stroke.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.BothSidesOffColor","ARGB stroke of the inactive one-sided notch icon.","#FF1E1E1E")]
      static readonly System.Drawing.Color BothSidesOffColor = CommandDefaults.GetColor("vNotches","vTools.Commands.vNotches.NotchPanel.BothSidesOffColor",System.Drawing.Color.FromArgb(30,30,30)); // ARGB inactive one-sided icon stroke.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.BothSidesOnBackgroundColor","ARGB background of the active two-sided notch icon.","#FFD9EBFA")]
      static readonly System.Drawing.Color BothSidesOnBackgroundColor = CommandDefaults.GetColor("vNotches","vTools.Commands.vNotches.NotchPanel.BothSidesOnBackgroundColor",System.Drawing.Color.FromArgb(217,235,250)); // ARGB active two-sided icon fill.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.DragRowHighlightColor","ARGB dragged-row fill and outline; zero alpha follows the native selection color instead.","#00000000")]
      static readonly System.Drawing.Color DragRowHighlightColor = CommandDefaults.GetColor("vNotches","vTools.Commands.vNotches.NotchPanel.DragRowHighlightColor",System.Drawing.Color.Transparent); // Zero-alpha sentinel follows the system selection color; otherwise use custom ARGB.
      [CommandDefault("vNotches","vTools.Commands.vNotches.NotchPanel.DragRowHighlightOpacity","Dragged-row fill opacity from zero through one; the outline remains fully visible.")]
      const double DragRowHighlightOpacity = 0.3; // Fill opacity zero through one, separate from the outline.
      static readonly double ConfiguredDragRowHighlightOpacity = CommandDefaults.Get("vNotches","vTools.Commands.vNotches.NotchPanel.DragRowHighlightOpacity",DragRowHighlightOpacity); // Resolved fill opacity; restart after advanced edits.
      static readonly System.Windows.Media.Brush CurveBothSidesOnBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(BothSidesOnColor.A,BothSidesOnColor.R,BothSidesOnColor.G,BothSidesOnColor.B)); // Active two-sided icon stroke.
      static readonly System.Windows.Media.Brush CurveBothSidesOffBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(BothSidesOffColor.A,BothSidesOffColor.R,BothSidesOffColor.G,BothSidesOffColor.B)); // Inactive one-sided icon stroke.
      static readonly System.Windows.Media.Brush CurveBothSidesOnBackground =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(BothSidesOnBackgroundColor.A,BothSidesOnBackgroundColor.R,BothSidesOnBackgroundColor.G,BothSidesOnBackgroundColor.B)); // Active two-sided icon fill.
      static readonly System.Windows.Media.Color DragRowNativeColor = DragRowHighlightColor.A==0 ? System.Windows.SystemColors.HighlightColor :
        System.Windows.Media.Color.FromArgb(DragRowHighlightColor.A,DragRowHighlightColor.R,DragRowHighlightColor.G,DragRowHighlightColor.B); // Native color for zero-alpha sentinel; otherwise resolved custom ARGB.
      static readonly System.Windows.Media.Brush CurveDragRowHighlightBrush =
        new System.Windows.Media.SolidColorBrush(DragRowNativeColor)
        {
          Opacity = ConfiguredDragRowHighlightOpacity,
        }; // Translucent overlay applied to the row currently being dragged.
      static readonly System.Windows.Media.Pen CurveDragRowHighlightPen =
        new(new System.Windows.Media.SolidColorBrush(DragRowNativeColor), CurveDragRowOutlineWidth); // Outline around the dragged row.
      static readonly System.Windows.Style NotchTypeFocusVisualStyle = CreateOutsideFocusStyle(); // One-pixel outside focus outline for type buttons.
      static readonly System.Windows.Controls.ControlTemplate CurveDirectionButtonTemplate = CreateTransparentButtonTemplate(); // Shared borderless direction-button template with hover and pressed feedback.

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
      readonly Button _notchTrimButton;
      readonly NumericStepper _lengthStepper, _offsetStepper, _widthStepper;
      readonly DropDown    _notchLayerDrop;
      readonly CheckBox    _percentCheck, _groupCheck, _associationsCheck;
      readonly CheckBox    _notchCheck, _labelCheck, _sideFlipCheck;
      System.Windows.Controls.CheckBox? _notchHeaderCheck;
      System.Windows.Controls.CheckBox? _labelHeaderCheck;
      readonly TextBox     _labelValueBox;
      readonly TextBox     _labelPrefixBox;
      readonly DropDown    _labelLayerDrop;
      readonly Button _nextLabelButton;
      ContextMenu? _labelSequenceMenu;
      readonly List<FieldResetOverlay> _fieldResetOverlays = [];
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
      readonly Label _fromPrevCaption=new(){Text="From previous",VerticalAlignment=VerticalAlignment.Center};
      readonly Label       _segmentStartLbl, _segmentEndLbl, _segmentPrevLbl;
      readonly Button      _undoBtn, _redoBtn, _selectCurvesButton;
      readonly Button _editNotchesButton=EditToolButton(EditToolGlyph,"Edit an existing notch and its matched set");
      readonly Button _applyNotchEdit=EditToolButton(ApplyToolGlyph,"Apply current notch and label settings to the edited matched set");
      readonly Button _pickNotchPairs=EditToolButton(LinkToolGlyph,"Associate selected notches with this matched set");
      readonly Button _selectNotchMates=EditToolButton(SelectToolGlyph,"Select matching notches in the drawing");
      readonly Button _unlinkNotch=EditToolButton(UnlinkToolGlyph,"Unlink this physical notch and its labels from the matched set; keep all objects");
      readonly Button _repairNestLinks=EditToolButton(RepairToolGlyph,"Repair links and upgrade verified old notch metadata to curve-relative references without moving geometry. Missing historical pairing data is not invented. Undo is available.");
      readonly Button _assignNotchSource=EditToolButton(AssignSourceToolGlyph,"Assign the clicked notch and its labels to a source curve manually; geometry stays in place. Undo is available.");
      readonly Button _editLabels=new(){Text="Add Labels",ToolTip="Add or update labels on all linked notches using the current text, prefix, size, layer, side and offsets; leave notch geometry unchanged"};
      readonly Button _removeLabels=new(){Text="Remove Labels",ToolTip="Remove labels from all linked notches without deleting or changing notch geometry"};
      readonly CheckBox _highlightExisting=new(){Text="Highlight",ToolTip="Keep existing notches visible at every zoom; orange indicates missing or invalid pairing data"};
      readonly CheckBox _oppositeMatchesCheck=new(){Text="Opposite ends",ToolTip="Newly linked curve families use opposite start/end directions. Reverse an individual curve row to override; drawing curves and notch positions stay unchanged."};
      Control? _multipleEditSection;
      System.Windows.Controls.CheckBox? _keepSelectionCheck;
      Button[]   _sideButtons = [];
      Button[]   _bothSidesButtons = [];
      Button[]   _reverseButtons = [];
      CheckBox[] _enableChecks = [];
      Panel[]    _curveDragHandles = [];
      Label[]    _curveLengthLabels = [];
      Panel[]    _curveLengthBadges = [];
      Label?[]   _curveLengthDifferenceLabels = [];
      Label?[]   _curveTotalLabels = [];
      Panel?[]   _curveTotalBadges = [];
      Label?[]   _curveTotalDifferenceLabels = [];
      int _curveTotalColumnWidth;
      int _curveLengthColumnWidth;
      int _curveDifferenceColumnWidth;
      CurveRowInfo[] _curveRows = [];
      readonly CurveRowHoverConduit _curveHoverConduit;
      readonly Dictionary<Button,(object? Background,object? Foreground,object? Border)> _modeButtonBrushes=[];
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
      Eto.Drawing.Color _percentDefaultBackgroundColor = Colors.Transparent;
      Eto.Drawing.Color _percentDefaultTextColor = SystemColors.ControlText;

      public NotchPanel(RhinoDoc doc, NotchSession s)
      {
        var startupTimer = Stopwatch.StartNew();
        _suppress = true;
        _s = s;
        _curveHoverConduit=new(doc);
        _multipleUseDistanceBeforeAuto = s.MultipleUseDistance;
        _highlightExisting.Checked=s.HighlightExisting;
        _highlightExisting.CheckedChanged+=(_,_)=>
        {if(_suppress)return;s.HighlightExisting=_highlightExisting.Checked==true;ToolsOptionStore.Update(Section,json=>json["highlight_existing"]=s.HighlightExisting);Redraw();};
        void EditAction(string action){RhinoApp.SetFocusToMainWindow(doc);GetBaseClass.PostCustomMessage(new NotchEditRequest(action));}
        _editNotchesButton.Click+=(_,_)=>EditAction("mode");
        _assignNotchSource.Load+=(_,_)=>RefreshLinkButtonState();
        _applyNotchEdit.Click+=(_,_)=>{CommitPendingValues();EditAction("apply");};
        _pickNotchPairs.Click+=(_,_)=>EditAction("pairs");
        _pickNotchPairs.Load+=(_,_)=>RefreshLinkButtonState();
        _selectNotchMates.Click+=(_,_)=>EditAction("matches");
        _unlinkNotch.Click+=(_,_)=>EditAction("unlink");
        _repairNestLinks.Click+=(_,_)=>EditAction("repair-nest");
        _assignNotchSource.Click+=(_,_)=>EditAction("assign-source");
        _oppositeMatchesCheck.Checked=s.OppositeMatches;
        _oppositeMatchesCheck.CheckedChanged+=(_,_)=>{if(_suppress)return;s.OppositeMatches=_oppositeMatchesCheck.Checked==true;Persist();};
        _editLabels.Click+=(_,_)=>{CommitPendingValues();EditAction("labels-add");};
        _removeLabels.Click+=(_,_)=>{CommitPendingValues();EditAction("labels-remove");};
        Title     = "Notches";
        Padding   = new Eto.Drawing.Padding(0);
        Resizable = true;
        Topmost   = true;
        ClientSize = new Eto.Drawing.Size(
          Math.Max(CurveMinimumWidth(), _windowWidth),
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
        _notchTrimButton = new Button
        {
          Width = NotchTrimIconWidth,
          Height = NotchTrimIconHeight,
        };
        _notchTrimButton.Click += (_, __) =>
          SetNotchTrim(doc, s, (NotchTrimMode)(((int)s.NotchTrim + 1) % 3));
        _notchTrimButton.Load += (_, __) => RefreshNotchTrimIcon();

        // Numeric fields
        _lengthStepper = MakeNumberStepper(s.NotchLengthOpt.CurrentValue,
          doc.ModelAbsoluteTolerance, 1e9, 0.1);
        _lengthStepper.ToolTip = "Notch depth measured away from the source curve, in model units";
        _offsetStepper = MakeNumberStepper(s.NotchOffsetOpt.CurrentValue,
          0.0, 1e9, 0.1);
        _offsetStepper.ToolTip = "Distance from the source curve to the offset curve used by the notch, in model units; zero keeps it on the source curve";
        _widthStepper = MakeNumberStepper(s.NotchWidthOpt.CurrentValue,
          doc.ModelAbsoluteTolerance, 1e9, 0.1);
        _widthStepper.ToolTip = "Notch width along the source curve, in model units";

        AttachNumericLive(_lengthStepper, v => s.NotchLengthOpt.CurrentValue = v,
          refreshTypeIcons: true);
        AttachNumericLive(_offsetStepper, v => s.NotchOffsetOpt.CurrentValue = v);
        AttachNumericLive(_widthStepper, v => s.NotchWidthOpt.CurrentValue = v,
          refreshTypeIcons: true);

        // Notch layer dropdown
        _notchLayerDrop = LayerSelector.CreateDropDown(
          doc, s.NotchLayerName, SpecialLayerCurrent);
        _notchLayerDrop.ToolTip = "Layer for new notch geometry; Current uses the active Rhino layer";
        _notchLayerDrop.SelectedIndexChanged += (_, __) =>
        {
          if (_suppress || LayerSelector.IsDropDownUpdating(_notchLayerDrop)) return;
          s.NotchLayerName = LayerSelector.GetDropDownValue(
            _notchLayerDrop, s.NotchLayerName);
          Redraw();
          Persist();
        };

        _notchCheck = new CheckBox
        {
          Text = "", Checked = s.NotchToggle.CurrentValue,
          ToolTip = "Create notch geometry at each placement",
        };
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
          if(!_percentCheck.Checked.HasValue)return;
          if(!SetEditedNotchPercent(doc,s,_percentCheck.Checked==true))
          {
            RefreshPercentCheck();
            RhinoApp.WriteLine("vNotches: percent mode could not be changed; check the selected notches' source associations and locked objects.");
            return;
          }
          RefreshPercentCheck();
          UpdateMultipleState();
          ApplyCurveLengthHighlights();
          Redraw();
          Persist();
        };
        _groupCheck = new CheckBox
        {
          Text = "Group", Checked = s.GroupToggle.CurrentValue,
          ToolTip = "Group new notch and label results with their source curve; existing source groups are always inherited",
        };
        _groupCheck.CheckedChanged += (_, __) =>
        { if (_suppress) return; s.GroupToggle.CurrentValue = _groupCheck.Checked == true; Redraw(); Persist(); };
        _associationsCheck=new CheckBox{Text="Associations",Checked=s.Associations,ToolTip="Apply new notches to verified nested copies and highlight their curves without adding them to this list"};
        _associationsCheck.CheckedChanged+=(_,_)=>{if(_suppress)return;s.Associations=_associationsCheck.Checked==true;SelectBothCurves(doc,s);Redraw();Persist();};
        _selectCurvesButton = new Button
        {
          Text = "Select", Width = 82, Height = 26,
          ToolTip = "Select source curves; the checkbox keeps the current selection",
        };
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
        _labelCheck = new CheckBox
        {
          Text = "", Checked = s.LabelToggle.CurrentValue,
          ToolTip = "Create a text label at each placement",
        };
        _labelValueBox = MakeTextBox(s.LabelValueText);
        int labelInputWidth = LabelInputWidth(_labelValueBox);
        _labelValueBox.Width = labelInputWidth;
        _labelPrefixBox = MakeTextBox(s.LabelPrefixText);
        _labelPrefixBox.Width = labelInputWidth;
        void RefreshLabelInputWidths()
        {
          if (_labelPrefixBox.ControlObject is System.Windows.FrameworkElement nativePrefix)
          {
            nativePrefix.MinWidth = labelInputWidth;
            nativePrefix.Width = double.NaN;
            nativePrefix.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
          }
        }
        _labelPrefixBox.Load += (_, __) => RefreshLabelInputWidths();
        _labelPrefixBox.ToolTip = "Fixed label prefix; the value stays closest to the notch on either side";
        _labelValueBox.ToolTip = "Sequence value, automatically advanced after placement; leave empty to repeat only the prefix";
        _nextLabelButton = new Button
        {
          Text = NextLabelIconFallback,
          Width = NextLabelButtonSize,
          Height = NextLabelButtonSize,
          ToolTip = "Use the next unused notch label in this document; choose a sequence when numbers, letters, or mixed prefixes are present",
        };
        _nextLabelButton.Load += (_, __) => InstallNextLabelIcon();
        _nextLabelButton.Click += (_, __) => SetNextDocumentLabel();
        Closed += (_, __) => _labelSequenceMenu?.Dispose();
        _sideFlipCheck = new CheckBox
        {
          Text = "Side", Checked = s.LabelSideFlip,
          ToolTip = "Checked: labels toward the curve start. Unchecked: labels away from the start. Remembered independently of notch side.",
        };
        _labelCheck.CheckedChanged += (_, __) =>
        {
          if (_suppress) return;
          ApplyFeatureToggle(notch: false, _labelCheck.Checked == true);
        };
        AttachTextLive(_labelValueBox, text => s.LabelValueText = text);
        AttachTextLive(_labelPrefixBox, text => s.LabelPrefixText = text);
        _sideFlipCheck.CheckedChanged += (_, __) =>
        { if (_suppress) return; s.LabelSideFlip = _sideFlipCheck.Checked == true; Redraw(); Persist(); };

        _labelLayerDrop = LayerSelector.CreateDropDown(doc, s.LabelLayerName);
        _labelLayerDrop.ToolTip = "Layer for new text labels";
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
        _labelSizeStepper.ToolTip = "Manual label text height in model units; used when Size Auto is off";
        AttachNumericLive(_labelSizeStepper, v => s.ManualLabelSize = Math.Max(0, v));

        _labelSizeAutoCheck = new CheckBox
        {
          Text = "Auto", Checked = s.LabelSizeAutoToggle.CurrentValue,
          ToolTip = "Calculate label height from the notch offset instead of using manual text height",
        };
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
        _labelSizePctStepper.ToolTip = "Automatic label height as a percentage of the notch offset (20-100%)";
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
        _labelOffsetStepper.ToolTip = LabelOffsetXToolTip;
        _labelOffsetYStepper.ToolTip = LabelOffsetYToolTip;
        AttachNumericLive(_labelOffsetStepper, v => s.LabelOffsetOpt.CurrentValue = v);
        AttachNumericLive(_labelOffsetYStepper, v => s.LabelOffsetYOpt.CurrentValue = v);

        // Multiple notches
        _multipleStartOffsetStepper = MakeNumberStepper(
          s.MultipleStartOffset, 0.0, 1e9, 0.1);
        _multipleEndOffsetStepper = MakeNumberStepper(
          s.MultipleEndOffset, 0.0, 1e9, 0.1);
        _multipleStartOffsetStepper.ToolTip = "Distance from the start to the first multiple notch, in model units";
        _multipleEndOffsetStepper.ToolTip = "Distance from the end to the last multiple notch, in model units";
        _multipleStartOffsetCheck = new CheckBox
        {
          Text = "Start offset",
          Checked = s.MultipleStartOffsetEnabled,
          ToolTip = "Place a multiple notch at the specified distance from the start",
        };
        _multipleEndOffsetCheck = new CheckBox
        {
          Text = "End offset",
          Checked = s.MultipleEndOffsetEnabled,
          ToolTip = "Place a multiple notch at the specified distance from the end",
        };
        _multipleNumberStepper = MakeNumberStepper(
          s.MultipleNumber, 1.0, 10000.0, 1.0, 0);
        _multipleNumberStepper.ToolTip = "Number of evenly spaced notches to add between the start and end offsets";
        _multipleDistanceStepper = MakeNumberStepper(
          s.MultipleDistance, 0.0, 1e9, 1.0);
        _multipleDistanceStepper.ToolTip = "Spacing in model units: minimum in Distance mode, maximum in Auto mode";
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
        _multipleAddButton = new Button
        {
          Text = "Add", Height = 26,
          ToolTip = "Add the multiple-notch layout; the checkbox applies it to each linked segment separately",
        };
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
        _fromStartLbl = new Label { Text = "-", ToolTip = "Distance from the start of the selected curve sequence" };
        _fromEndLbl = new Label { Text = "-", ToolTip = "Distance from the end of the selected curve sequence" };
        _fromPrevLbl = new Label { Text = "-", ToolTip = "Distance from the previous notch on the selected curve sequence" };
        _segmentStartLbl = new Label { Text = "", ToolTip = "Distance from the start of the current linked segment" };
        _segmentEndLbl = new Label { Text = "", ToolTip = "Distance from the end of the current linked segment" };
        _segmentPrevLbl = new Label { Text = "", ToolTip = "Distance from the previous notch on the current linked segment" };

        // History buttons
        _undoBtn = new Button
        {
          Text = "Undo", Width = 54, Height = 24,
          ToolTip = "Undo the last notch placement in this session",
        };
        _undoBtn.Click += (_, __) =>
        {
          RunLocalHistory(doc, redo: false, source: "panel-undo");
        };
        _redoBtn = new Button
        {
          Text = "Redo", Width = 54, Height = 24,
          ToolTip = "Redo the last undone notch placement in this session",
        };
        _redoBtn.Click += (_, __) =>
        {
          RunLocalHistory(doc, redo: true, source: "panel-redo");
        };
        UpdateUndoEnabled();

        // Side/Reverse/Enable per curve
        InstallFieldResets(doc);
        Closed += (_, __) => { foreach (var overlay in _fieldResetOverlays) overlay.Dispose(); };
        Log.Write(Section, $"Panel controls prepared: elapsed_ms={startupTimer.Elapsed.TotalMilliseconds:0.0}");
        CreateCurveRowControls(doc);
        Log.Write(Section, $"Panel curve rows prepared: rows={_curveRows.Length}" +
          $" elapsed_ms={startupTimer.Elapsed.TotalMilliseconds:0.0}");

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
        Log.Write(Section, $"Panel layout prepared: elapsed_ms={startupTimer.Elapsed.TotalMilliseconds:0.0}");
        RestoreWindowLocation();
        Load += (_, __) => RestoreWindowLocation();
        Shown += (_, __) =>
        {
          _windowSizePersistenceReady = false;
          Application.Instance.AsyncInvoke(() =>
          {
            if (IsDisposed || !Visible) return;
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
            CaptureWindowLocation();
          });
        };
        LocationChanged += (_, __) => CaptureWindowLocation();
        Closing += (_, __) =>
        {
          CaptureWindowLocation();
          if (_windowLocation is {} location)
            ToolsOptionStore.Update(Section, options =>
            {
              options[WindowXKey] = location.X;
              options[WindowYKey] = location.Y;
            });
        };
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
          ApplyCurveRowHighlights();
          RefreshMultiplePreview();
        };
        MouseLeave += (_, __) =>
        {
          _viewportPointerActive = true;
          ApplyCurveRowHighlights(_viewportCurveHoverRowIndex);
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
        if (ControlObject is System.Windows.Window nativeWindow)
          nativeWindow.PreviewKeyDown += LinkModeKeyDown;

        Closed += (_, __) =>
        {
          MatchingLabels.Changed -= OnMatchingLabelsChanged;
          ClearMultiplePreview();
          ClearCurveRowHover();
          _curveHoverConduit.Dispose();
          if (!s.SuppressPanelCloseExit)
          {
            CommitPendingValues();
            SaveOptions(s);
            s.PanelClosedExit = true;
            try { RhinoApp.RunScript("_Cancel", false); } catch { }
          }
        };
        MatchingLabels.Changed += OnMatchingLabelsChanged;
        _suppress = false;
        UpdateEditMode();
      }

      void LinkModeKeyDown(object sender,System.Windows.Input.KeyEventArgs e)
      {
        if(_s.EditTool!=NotchEditTool.None&&e.Key==System.Windows.Input.Key.Escape)
        {e.Handled=true;GetBaseClass.PostCustomMessage(new NotchEditRequest("tool-stop"));return;}
        if (!_s.RepairPairing || e.Key != System.Windows.Input.Key.Enter || InputEditorFocused()) return;
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.DependencyObject focused &&
            NativeInputParent(focused)) return;
        e.Handled = true;
        RhinoApp.SetFocusToMainWindow(_s.Doc);
        GetBaseClass.PostCustomMessage(new NotchEditRequest("link-complete"));
      }

      static bool NativeInputParent(System.Windows.DependencyObject element)
      {
        for (var current = element; current != null; current = current is System.Windows.Media.Visual
          ? System.Windows.Media.VisualTreeHelper.GetParent(current) : System.Windows.LogicalTreeHelper.GetParent(current))
          if (current is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox) return true;
        return false;
      }

      void RestoreWindowLocation()
      {
        if (_windowLocation is {} location)
          Location = FitWindowLocation(location, Size, Screen.Screens.Select(screen => screen.WorkingArea)
            .Select(area => new Eto.Drawing.Rectangle((int)area.X, (int)area.Y, (int)area.Width, (int)area.Height)).ToArray());
      }

      void CaptureWindowLocation()
      {
        if (_windowSizePersistenceReady && Visible && WindowState == WindowState.Normal)
          _windowLocation = Location;
      }

      void OnMatchingLabelsChanged(RhinoDoc doc)
      {
        if(doc.RuntimeSerialNumber!=_s.Doc.RuntimeSerialNumber) return;
        var style=MatchingLabels.Read(doc);
        if(LabelSequences.NormalizeValue(_s.LabelValueText)==style.Value&&_s.ManualLabelSize==style.ManualSize&&_s.LabelLayerName==style.Layer&&
          _s.LabelSizeAutoToggle.CurrentValue==style.AutoSize&&
          _s.LabelSizePctValues[_s.LabelSizePctIndex]==style.SizePercent) return;
        _s.LabelValueText=style.Value;_s.ManualLabelSize=style.ManualSize;
        _s.LabelSizeOpt.CurrentValue=style.Height;_s.LabelLayerName=style.Layer;
        _s.LabelSizeAutoToggle.CurrentValue=style.AutoSize;
        _s.LabelSizePctIndex=Enumerable.Range(0,_s.LabelSizePctValues.Length).OrderBy(i=>Math.Abs(_s.LabelSizePctValues[i]-style.SizePercent)).First();
        UpdateStaticDefaultsFromSession(_s);
        SyncFromSession();
      }

      bool InputEditorFocused() =>
        _lengthStepper.HasFocus ||
        _offsetStepper.HasFocus ||
        _widthStepper.HasFocus ||
        _labelValueBox.HasFocus ||
        _labelPrefixBox.HasFocus ||
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
        typeSelector.Items.Add(new StackLayoutItem(_notchTrimButton, false));
        notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Type", "Shape of the notch to create"), new TableCell(typeSelector, true) } });
        notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer", _notchLayerDrop.ToolTip), new TableCell(_notchLayerDrop, true) } });
        notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Length", _lengthStepper.ToolTip), new TableCell(_lengthStepper, true) } });
        notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Width", _widthStepper.ToolTip), new TableCell(_widthStepper, true) } });
        notchTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset", _offsetStepper.ToolTip), new TableCell(_offsetStepper, true) } });
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
        var labelTextFields = new TableLayout { Spacing = new Eto.Drawing.Size(LabelInputSpacing, 0) };
        labelTextFields.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
          new TableCell(_labelPrefixBox, true), new TableCell(_labelValueBox, false) } });
        var labelHeader = new TableLayout { Spacing = new Eto.Drawing.Size(4, 0) };
        labelHeader.Rows.Add(new TableRow { ScaleHeight = false, Cells = {
          new TableCell(labelTextFields, true),
          new TableCell(_nextLabelButton, false),
          new TableCell(_sideFlipCheck, false),
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
        labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL(""), new TableCell(labelHeader, true) } });
        labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Layer", _labelLayerDrop.ToolTip), new TableCell(_labelLayerDrop, true) } });
        labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Size", "Label text height; Auto uses a percentage of the notch offset"), new TableCell(sizeRow, true) } });
        labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset X", LabelOffsetXToolTip), new TableCell(_labelOffsetStepper, true) } });
        labelTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("Offset Y", LabelOffsetYToolTip), new TableCell(_labelOffsetYStepper, true) } });
        labelTable.Rows.Add(new TableRow {ScaleHeight=false,Cells={FL(""),new TableCell(new StackLayout{
          Orientation=Orientation.Horizontal,Spacing=4,Items={_editLabels,_removeLabels}},true)}});
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
        distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From start", _fromStartLbl.ToolTip), new TableCell(_fromStartLbl, false), new TableCell(_segmentStartLbl, false), new TableCell(null, true) } });
        distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { FL("From end", _fromEndLbl.ToolTip), new TableCell(_fromEndLbl, false), new TableCell(_segmentEndLbl, false), new TableCell(null, true) } });
        distTable.Rows.Add(new TableRow { ScaleHeight = false, Cells = { new TableCell(_fromPrevCaption,false), new TableCell(_fromPrevLbl, false), new TableCell(_segmentPrevLbl, false), new TableCell(null, true) } });
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
        var editModeRow=new StackLayout{Orientation=Orientation.Horizontal,Spacing=4,VerticalContentAlignment=VerticalAlignment.Center,
          Items={_editNotchesButton,_applyNotchEdit,_pickNotchPairs,_selectNotchMates,_unlinkNotch,_repairNestLinks,_assignNotchSource,_highlightExisting}};
        _multipleEditSection=multipleGroup;
        root.Items.Add(new StackLayoutItem(editModeRow,false));
        root.Items.Add(new StackLayoutItem(notchGroup, false));
        root.Items.Add(new StackLayoutItem(multipleGroup, false));
        root.Items.Add(new StackLayoutItem(labelGroup, false));
        root.Items.Add(new StackLayoutItem(pgStack,    false));
        root.Items.Add(new StackLayoutItem(new StackLayout{Orientation=Orientation.Horizontal,Spacing=6,Items={_associationsCheck,_oppositeMatchesCheck}},false));
        root.Items.Add(new StackLayoutItem(_curveScrollable, true));
        root.Items.Add(new StackLayoutItem(infoRow,    false));

        return root;
      }

      static Button EditToolButton(string glyph,string tooltip)
      {
        var button=new Button{Width=ConfiguredEditToolButtonSize,Height=ConfiguredEditToolButtonSize,ToolTip=tooltip};
        SetEditToolIcon(button,glyph);
        button.Load+=(_,_)=>SetEditToolIcon(button,button.Tag as string??glyph);
        return button;
      }
      static void SetEditToolIcon(Button button,string glyph)
      {
        button.Tag=glyph;
        if(button.ControlObject is not System.Windows.Controls.Button native)return;
        native.Padding=new System.Windows.Thickness(0);
        var content=new System.Windows.Controls.TextBlock
        {
          Text=glyph,FontFamily=new System.Windows.Media.FontFamily(EditToolFont),FontSize=ConfiguredEditToolIconSize,
          HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center
        };
        content.SetBinding(System.Windows.Controls.TextBlock.ForegroundProperty,new System.Windows.Data.Binding("Foreground"){Source=native});
        native.Content=content;
        System.Windows.Automation.AutomationProperties.SetName(native,button.ToolTip);
      }
      void RefreshLinkButtonState()
      {
        bool active=_s.RepairPairing;
        _pickNotchPairs.ToolTip=active?"Linking: select a set and press Enter to link and clear it. Repeat for the next set. Click this button, or press Enter with fewer than two selected, to exit.":
          "Start repeated notch linking; multiple selected notches link immediately, then selection clears for the next set";
        SetModeButtonActive(_pickNotchPairs,active);
        foreach(var (button,tool) in new[]{(_applyNotchEdit,NotchEditTool.Apply),(_selectNotchMates,NotchEditTool.Matches),
          (_unlinkNotch,NotchEditTool.Unlink),(_repairNestLinks,NotchEditTool.Repair),(_assignNotchSource,NotchEditTool.Associate),
          (_editLabels,NotchEditTool.AddLabel),(_removeLabels,NotchEditTool.RemoveLabel)})
        {
          bool enabled=_s.EditTool==tool;
          SetModeButtonActive(button,enabled);
          button.ToolTip=NotchToolCaption(tool)+(enabled?" is active. Pick a notch; click again or press Esc to exit.":". Click to enter, then pick a notch. Selected notches are processed immediately.");
        }
      }
      void SetModeButtonActive(Button button,bool active)
      {
        if(button.ControlObject is not System.Windows.Controls.Button native)return;
        if(!_modeButtonBrushes.TryGetValue(button,out var normal))
        {
          object? Original(System.Windows.DependencyProperty property)=>
            (object?)System.Windows.Data.BindingOperations.GetBindingBase(native,property)??native.ReadLocalValue(property);
          normal=(Original(System.Windows.Controls.Control.BackgroundProperty),Original(System.Windows.Controls.Control.ForegroundProperty),Original(System.Windows.Controls.Control.BorderBrushProperty));
          _modeButtonBrushes.Add(button,normal);
          var border=new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.Border));
          border.SetValue(System.Windows.Controls.Border.BackgroundProperty,new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
          border.SetValue(System.Windows.Controls.Border.BorderBrushProperty,new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
          border.SetValue(System.Windows.Controls.Border.BorderThicknessProperty,new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderThicknessProperty));
          var content=new System.Windows.FrameworkElementFactory(typeof(System.Windows.Controls.ContentPresenter));
          content.SetValue(System.Windows.Controls.ContentPresenter.ContentProperty,new System.Windows.TemplateBindingExtension(System.Windows.Controls.ContentControl.ContentProperty));
          content.SetValue(System.Windows.FrameworkElement.HorizontalAlignmentProperty,System.Windows.HorizontalAlignment.Center);
          content.SetValue(System.Windows.FrameworkElement.VerticalAlignmentProperty,System.Windows.VerticalAlignment.Center);border.AppendChild(content);
          native.Template=new System.Windows.Controls.ControlTemplate(typeof(System.Windows.Controls.Button)){VisualTree=border};
        }
        System.Windows.Media.Brush Brush(System.Drawing.Color color)=>new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A,color.R,color.G,color.B));
        void Apply(System.Windows.DependencyProperty property,object? original,System.Drawing.Color color)
        {
          if(active)native.SetValue(property,Brush(color));
          else if(original==System.Windows.DependencyProperty.UnsetValue)native.ClearValue(property);
          else if(original is System.Windows.Data.BindingBase binding)System.Windows.Data.BindingOperations.SetBinding(native,property,binding);
          else native.SetValue(property,original);
        }
        Apply(System.Windows.Controls.Control.BackgroundProperty,normal.Background,LinkActiveBackground);
        Apply(System.Windows.Controls.Control.ForegroundProperty,normal.Foreground,LinkActiveForeground);
        Apply(System.Windows.Controls.Control.BorderBrushProperty,normal.Border,LinkActiveBorder);
      }
      void InstallNextLabelIcon()
      {
        if (_nextLabelButton.ControlObject is not System.Windows.Controls.Button button) return;
        var canvas = new System.Windows.Controls.Canvas { Width = NextLabelIconSize, Height = NextLabelIconSize };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
          Data = System.Windows.Media.Geometry.Parse(NextLabelIconPath),
          Stroke = button.Foreground,
          StrokeThickness = NextLabelIconStroke,
          StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
        });
        button.Content = canvas;
        button.Padding = new System.Windows.Thickness(ConfiguredNextLabelButtonPadding);
      }

      void InstallFieldResets(RhinoDoc doc)
      {
        void Number(NumericStepper field, double value, string name)
        {
          value = Math.Clamp(RoundForDisplay(value, field.MaximumDecimalPlaces), field.MinValue, field.MaxValue);
          var defaultValue = value;
          var overlay = new FieldResetOverlay(field,
            () => FieldResetOverlay.NumberChanged(field.Value, defaultValue, field.MaximumDecimalPlaces),
            () => field.Value = defaultValue, $"Reset {name} to {FormatPanelNumber(defaultValue)}");
          _fieldResetOverlays.Add(overlay);
          field.ValueChanged += (_, __) => overlay.Refresh();
        }
        void Layer(DropDown field, string value, Func<string> current, Action<string> apply, string name)
        {
          var overlay = new FieldResetOverlay(field,
            () => !string.Equals(current(), value, StringComparison.OrdinalIgnoreCase),
            () => { apply(value); LayerSelector.SetDropDownValue(field, value); Redraw(); Persist(); },
            $"Reset {name} to {value}");
          _fieldResetOverlays.Add(overlay);
          field.SelectedIndexChanged += (_, __) => overlay.Refresh();
        }
        Number(_lengthStepper, ConfiguredDefaultNotchLength, "notch length");
        Number(_widthStepper, ConfiguredDefaultNotchWidth, "notch width");
        Number(_offsetStepper, ConfiguredDefaultNotchOffset, "notch offset");
        Number(_multipleStartOffsetStepper, ConfiguredDefaultMultipleStartOffset, "start offset");
        Number(_multipleEndOffsetStepper, ConfiguredDefaultMultipleEndOffset, "end offset");
        Number(_multipleNumberStepper, ConfiguredDefaultMultipleNumber, "number");
        Number(_multipleDistanceStepper, ConfiguredDefaultMultipleDistance, "distance");
        Number(_multipleCurvatureSensitivityStepper, ConfiguredDefaultMultipleCurvatureSensitivity, "sensitivity");
        Number(_labelSizeStepper, ConfiguredDefaultLabelSize, "label size");
        Number(_labelSizePctStepper, ConfiguredDefaultLabelSizePercent, "label size percentage");
        Number(_labelOffsetStepper, double.IsNaN(ConfiguredDefaultLabelOffset)
          ? ModelUnitsFromInches(doc, ConfiguredDefaultLabelOffIn) : ConfiguredDefaultLabelOffset, "label offset X");
        Number(_labelOffsetYStepper, ConfiguredDefaultLabelOffsetY, "label offset Y");
        Layer(_notchLayerDrop, ConfiguredDefaultNotchLayer, () => _s.NotchLayerName,
          value => _s.NotchLayerName = value, "notch layer");
        Layer(_labelLayerDrop, ConfiguredDefaultLabelLayer, () => _s.LabelLayerName,
          value => _s.LabelLayerName = value, "label layer");
        var textOverlay = new FieldResetOverlay(_labelValueBox,
          () => !string.Equals(_labelValueBox.Text, ConfiguredDefaultLabelValue, StringComparison.Ordinal),
          () => { _labelValueBox.Text = ConfiguredDefaultLabelValue; Persist(); }, $"Reset label to {ConfiguredDefaultLabelValue}");
        _fieldResetOverlays.Add(textOverlay);
        _labelValueBox.TextChanged += (_, __) => textOverlay.Refresh();
        var prefixOverlay = new FieldResetOverlay(_labelPrefixBox,
          () => !string.Equals(_labelPrefixBox.Text, ConfiguredDefaultLabelPrefix, StringComparison.Ordinal),
          () => { _labelPrefixBox.Text = ConfiguredDefaultLabelPrefix; Persist(); }, "Reset label prefix");
        _fieldResetOverlays.Add(prefixOverlay);
        _labelPrefixBox.TextChanged += (_, __) => prefixOverlay.Refresh();
        Shown += (_, __) => { foreach (var overlay in _fieldResetOverlays) overlay.Refresh(); };
      }

      void SetNextDocumentLabel()
      {
        var settings = new ObjectEnumeratorSettings
        {
          ObjectTypeFilter = ObjectType.Annotation,
          NormalObjects = true,
          LockedObjects = true,
          HiddenObjects = true,
          IncludeLights = false,
          IncludeGrips = false,
          IncludePhantoms = false,
        };
        var labels = _s.Doc.Objects.GetObjectList(settings)
          .Where(obj => obj.Geometry is TextEntity &&
            (string.Equals(obj.Attributes.Name, NotchLabelObjectName, StringComparison.OrdinalIgnoreCase) ||
             obj.Attributes.GetUserString(NotchDataPrefix + "object_role") == "label" ||
             obj.Attributes.GetUserString(NotchDataPrefix + "version") != null))
          .Select(obj => obj.Attributes.GetUserString(NotchDataPrefix + "label_text") ?? ((TextEntity)obj.Geometry).PlainText ?? string.Empty).ToArray();
        var choices = LabelSequences.FindNext(labels);
        Log.Write(Section, $"next label scan: labels={labels.Length} sequences={choices.Count}");
        _labelSequenceMenu?.Dispose();
        _labelSequenceMenu = null;
        if (choices.Count == 0)
        {
          if (string.IsNullOrWhiteSpace(_labelValueBox.Text)) _labelValueBox.Text = ConfiguredDefaultLabelValue;
          return;
        }
        if (choices.Count == 1)
        {
          _labelValueBox.Text = choices[0].Next;
          return;
        }
        _labelSequenceMenu = new ContextMenu();
        foreach (var choice in choices)
        {
          var item = new ButtonMenuItem { Text = choice.Description };
          item.Click += (_, __) => _labelValueBox.Text = choice.Next;
          _labelSequenceMenu.Items.Add(item);
        }
        _labelSequenceMenu.Show(_nextLabelButton, new Eto.Drawing.PointF(0, NextLabelButtonSize));
      }

      void CreateCurveRowControls(RhinoDoc doc)
      {
        SetCurveDragRowHighlight(-1, false);
        _curveRows = BuildCurveRowInfos();
        _sideButtons = new Button[_curveRows.Length];
        _bothSidesButtons = new Button[_curveRows.Length];
        _reverseButtons = new Button[_curveRows.Length];
        _enableChecks = new CheckBox[_curveRows.Length];
        _curveDragHandles = new Panel[_curveRows.Length];
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
            _s.Doc, ConfiguredCurveLengthDifferenceToleranceInches)
          ? $"(+{FormatPanelNumber(logicalSpan)})"
          : "";

        for (int i = 0; i < _curveRows.Length; i++)
        {
          int rowIndex = i;
          int curveIndex = _curveRows[i].LogicalIndex;
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
            ClearNotchRedo(_s);
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

          _bothSidesButtons[i] = new Button
          {
            Width = CurveBothSidesButtonWidth,
            Height = CurveDirectionButtonHeight,
          };
          _bothSidesButtons[i].Load += (_, __) =>
          {
            ConfigureCurveDirectionButton(_bothSidesButtons[rowIndex]);
            UpdateCurveBothSidesButton(rowIndex,
              _s.CurveBothSidesBySource.GetValueOrDefault(
                _curveRows[rowIndex].SourceId, _s.BothSidesToggle.CurrentValue));
          };
          UpdateCurveBothSidesButton(rowIndex,
            _s.CurveBothSidesBySource.GetValueOrDefault(
              _curveRows[rowIndex].SourceId, _s.BothSidesToggle.CurrentValue));
          _bothSidesButtons[i].Click += (_, __) =>
          {
            if (_suppress) return;
            Guid sourceId = _curveRows[rowIndex].SourceId;
            bool previous = _s.CurveBothSidesBySource.GetValueOrDefault(
              sourceId, _s.BothSidesToggle.CurrentValue);
            SetCurveBothSides(doc, _s, curveIndex, sourceId, !previous);
            UpdateCurveBothSidesButton(rowIndex,
              _s.CurveBothSidesBySource.GetValueOrDefault(sourceId, previous));
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
            ToolTip = "Length of this source curve, in model units",
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
          };
          bool linkedSequenceRow = _curveRows[i].LinkedToPrevious ||
            (i + 1 < _curveRows.Length && _curveRows[i + 1].LinkedToPrevious);
          if (!linkedSequenceRow) _curveLengthLabels[i].TextAlignment = TextAlignment.Left;
          if (!linkedSequenceRow)
            _curveLengthDifferenceLabels[i] = CreateCurveLengthDifferenceLabel(actualDifferenceWidthSample);
          _curveLengthBadges[i] = new Panel
          {
            ToolTip = _curveLengthLabels[i].ToolTip,
            Padding = new Eto.Drawing.Padding(
              ConfiguredCurveLengthBadgeHorizontalPadding, 0,
              ConfiguredCurveLengthBadgeHorizontalPadding, 0),
            Content = _curveLengthLabels[i],
          };
          double? totalLength = LinkedSequenceTotalForRow(i);
          if (totalLength.HasValue)
          {
            _curveTotalLabels[i] = new Label
            {
              Text = FormatPanelNumber(totalLength.Value),
              ToolTip = "Total length of the linked curve sequence, in model units",
              VerticalAlignment = VerticalAlignment.Center,
              TextAlignment = TextAlignment.Left,
            };
            _curveTotalDifferenceLabels[i] = CreateCurveLengthDifferenceLabel(actualDifferenceWidthSample);
            _curveTotalBadges[i] = new Panel
            {
              ToolTip = _curveTotalLabels[i]!.ToolTip,
              Padding = new Eto.Drawing.Padding(
                ConfiguredCurveLengthBadgeHorizontalPadding, 0,
                ConfiguredCurveLengthBadgeHorizontalPadding, 0),
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
              ToolTip = "Include this curve when placing notches and labels",
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
        bool LinkedRow(int index) => _curveRows[index].LinkedToPrevious ||
          (index + 1 < _curveRows.Length && _curveRows[index + 1].LinkedToPrevious);
        _curveLengthColumnWidth = Enumerable.Range(0, _curveRows.Length).Where(LinkedRow)
          .Select(index => (int)Math.Ceiling(_curveLengthBadges[index].GetPreferredSize().Width))
          .DefaultIfEmpty(0).Max();
        _curveTotalColumnWidth = Math.Max(_curveTotalColumnWidth,
          Enumerable.Range(0, _curveRows.Length).Where(index => !LinkedRow(index))
            .Select(index => (int)Math.Ceiling(_curveLengthBadges[index].GetPreferredSize().Width))
            .DefaultIfEmpty(0).Max());
        for (int i = 0; i < _curveRows.Length; i++)
          _curveLengthBadges[i].Width = LinkedRow(i) ? _curveLengthColumnWidth : _curveTotalColumnWidth;
        foreach (var badge in _curveTotalBadges.OfType<Panel>())
          badge.Width = _curveTotalColumnWidth;
        var differences = _curveLengthDifferenceLabels.Concat(_curveTotalDifferenceLabels).OfType<Label>().ToArray();
        _curveDifferenceColumnWidth = differences.Select(label => label.Width).DefaultIfEmpty(0).Max();
        foreach (var label in differences) label.Width = _curveDifferenceColumnWidth;
        ApplyCurveLengthHighlights();
      }

      static Label CreateCurveLengthDifferenceLabel(string actualValueSample)
      {
        var label = new Label
        {
          Text = actualValueSample,
          ToolTip = "Difference from the other selected curve lengths, in model units",
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
          using var measure = new Label { Text = actualValueSample, Font = label.Font };
          width = Math.Max(width, (int)Math.Ceiling(measure.GetPreferredSize().Width));
        }
        label.Width = width;
        label.Text = "";
        return label;
      }

      static void ConfigureCurveDirectionButton(Button button)
      {
        if (button.ControlObject is not System.Windows.Controls.Button nativeButton)
          return;
        nativeButton.Template = CurveDirectionButtonTemplate;
        nativeButton.Background = System.Windows.Media.Brushes.Transparent;
        nativeButton.BorderBrush = System.Windows.Media.Brushes.Transparent;
        nativeButton.BorderThickness = new System.Windows.Thickness(0.0);
        nativeButton.Padding = new System.Windows.Thickness(
          ConfiguredCurveDirectionButtonHorizontalPadding,
          ConfiguredCurveDirectionButtonVerticalPadding,
          ConfiguredCurveDirectionButtonHorizontalPadding,
          ConfiguredCurveDirectionButtonVerticalPadding);
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

      void UpdateCurveBothSidesButton(int rowIndex, bool enabled)
      {
        if (rowIndex < 0 || rowIndex >= _bothSidesButtons.Length)
          return;
        var button = _bothSidesButtons[rowIndex];
        button.ToolTip = enabled
          ? "Notch and label on both sides of this curve (click for one side)"
          : "Notch and label on one side of this curve (click for both sides)";
        if (button.ControlObject is not System.Windows.Controls.Button nativeButton)
          return;
        nativeButton.Padding = new System.Windows.Thickness(0.0);
        if (nativeButton.Content is System.Windows.Controls.Canvas existing && Equals(existing.Tag, enabled))
          return;
        var canvas = new System.Windows.Controls.Canvas
        {
          Width = CurveBothSidesButtonWidth,
          Height = CurveDirectionButtonHeight,
          Tag = enabled,
        };
        if (enabled)
        {
          var background = new System.Windows.Shapes.Rectangle
          {
            Width = CurveBothSidesButtonWidth - 4,
            Height = CurveDirectionButtonHeight - 8,
            Fill = CurveBothSidesOnBackground,
          };
          System.Windows.Controls.Canvas.SetLeft(background, 2.0);
          System.Windows.Controls.Canvas.SetTop(background, 4.0);
          canvas.Children.Add(background);
        }
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
          Data = System.Windows.Media.Geometry.Parse(
            enabled ? CurveBothSidesOnPath : CurveBothSidesOffPath),
          Stroke = enabled ? CurveBothSidesOnBrush : CurveBothSidesOffBrush,
          StrokeThickness = 1.8,
          StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
          StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
        });
        nativeButton.Content = canvas;
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
                : _s.Sources.FindObject(_s.Doc, sourceId)?.Geometry as Curve;
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
        for (int start = 0; start < _curveRows.Length;)
        {
          int end = start;
          while (end + 1 < _curveRows.Length && _curveRows[end + 1].LinkedToPrevious) end++;
          bool linked = end > start;
          int groupHeight = (end - start + 1) * CurveRowHeight + (end - start) * CurveRowSpacing;
          var rows = new StackLayout { Orientation = Orientation.Vertical, Spacing = CurveRowSpacing };
          for (int i = start; i <= end; i++)
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
            _curveDragHandles[i] = dragHandle;
            dragHandle.Load += (_, __) => InstallCurveRowDrag(dragHandle, rowIndex);
            row.Items.Add(new StackLayoutItem(dragHandle, false));
            if (_s.Curves.Count > 1 && _enableChecks[i] != null)
              row.Items.Add(new StackLayoutItem(_enableChecks[i], false));
            row.Items.Add(new StackLayoutItem(_sideButtons[i], false));
            row.Items.Add(new StackLayoutItem(_reverseButtons[i], false));
            row.Items.Add(new StackLayoutItem(_bothSidesButtons[i], false));
            var remove=new Button{Text="\u00D7",Width=CurveSideButtonWidth,Height=CurveDirectionButtonHeight,ToolTip="Remove this curve from the list; keep drawing objects and existing notches"};
            remove.Click+=(_,_)=>RemoveCurveRow(rowIndex);
            row.Items.Add(new StackLayoutItem(remove,false));
            row.Items.Add(new StackLayoutItem(null, true));
            if (_curveLengthColumnWidth > 0)
              row.Items.Add(new StackLayoutItem(CurveNumberCell(
                linked ? _curveLengthBadges[i] : null, _curveLengthColumnWidth), false));
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
            rows.Items.Add(new StackLayoutItem(row));
          }
          Panel totalBadge = linked ? _curveTotalBadges[start]! : _curveLengthBadges[start];
          // Span the text badges, leaving the same clear space above and below as each row.
          int textHeight = (int)Math.Ceiling(totalBadge.GetPreferredSize().Height);
          totalBadge.Height = Math.Min(CurveRowHeight, textHeight) +
            (end - start) * (CurveRowHeight + CurveRowSpacing);
          var totalCell = new StackLayout
          {
            Width = _curveTotalColumnWidth,
            Height = groupHeight,
            Orientation = Orientation.Vertical,
            Items = { new StackLayoutItem(null, true), totalBadge, new StackLayoutItem(null, true) },
          };
          int totalRow=start;
          totalCell.MouseEnter+=(_,_)=>SetCurveGroupHover(totalRow);
          totalCell.MouseLeave+=(_,_)=>ClearCurveRowHover();
          var group = new TableLayout { Spacing = new Eto.Drawing.Size(CurveRowControlSpacing, 0) };
          var groupRow = new TableRow { ScaleHeight = false };
          groupRow.Cells.Add(new TableCell(rows, true));
          groupRow.Cells.Add(new TableCell(totalCell, false));
          if (_curveDifferenceColumnWidth > 0)
          {
            Label? difference = _curveTotalDifferenceLabels[start] ?? _curveLengthDifferenceLabels[start];
            var differenceCell = new StackLayout
            {
              Width = _curveDifferenceColumnWidth,
              Height = groupHeight,
              Orientation = Orientation.Vertical,
              Items = { new StackLayoutItem(null, true), difference, new StackLayoutItem(null, true) },
            };
            groupRow.Cells.Add(new TableCell(differenceCell, false));
          }
          group.Rows.Add(groupRow);
          curveStack.Items.Add(new StackLayoutItem(group));
          start = end + 1;
        }
        curveStack.Load += (_, __) =>
        {
          InstallCurveLinkOverlay(curveStack);
          InstallCurveStackDrop(curveStack);
        };
        return curveStack;
      }

      static StackLayout CurveNumberCell(Control? content, int width) => new StackLayout
      {
        Width = width,
        Orientation = Orientation.Horizontal,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items = { new StackLayoutItem(null, true), content },
      };

      static Panel CreateCurveDragHandle()
      {
        var handle = new Panel
        {
          ToolTip = "Drag to reorder curve",
          Width = CurveDragHandleWidth,
          Height = CurveRowHeight,
        };
        void InstallGlyph()
        {
          var native = handle.ControlObject;
          if (native is System.Windows.Controls.Border border)
          {
            border.Background = System.Windows.Media.Brushes.Transparent;
            if (border.Child is not CurveDragHandleGlyph) border.Child = new CurveDragHandleGlyph();
          }
          else if (native is System.Windows.Controls.ContentControl content)
          {
            content.Background = System.Windows.Media.Brushes.Transparent;
            if (content.Content is not CurveDragHandleGlyph) content.Content = new CurveDragHandleGlyph();
          }
        }
        InstallGlyph();
        handle.Load += (_, __) => InstallGlyph();
        return handle;
      }

      sealed class CurveDragHandleGlyph : System.Windows.FrameworkElement
      {
        public CurveDragHandleGlyph()
        {
          Width = CurveDragHandleWidth;
          Height = CurveRowHeight;
          IsHitTestVisible = false;
        }

        protected override void OnRender(System.Windows.Media.DrawingContext drawingContext)
        {
          double clusterWidth = (CurveDragDotDiameter * 2.0) + CurveDragDotGap;
          double clusterHeight = (CurveDragDotDiameter * 3.0) + (CurveDragDotGap * 2.0);
          double left = (CurveDragHandleWidth - clusterWidth) * 0.5;
          double top = (CurveRowHeight - clusterHeight) * 0.5;
          for (int column = 0; column < 2; column++)
            for (int row = 0; row < 3; row++)
              drawingContext.DrawEllipse(System.Windows.SystemColors.ControlTextBrush, null,
                new System.Windows.Point(
                  left + column * (CurveDragDotDiameter + CurveDragDotGap) + CurveDragDotDiameter * 0.5,
                  top + row * (CurveDragDotDiameter + CurveDragDotGap) + CurveDragDotDiameter * 0.5),
                CurveDragDotDiameter * 0.5, CurveDragDotDiameter * 0.5);
        }
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

      void RemoveCurveRow(int index)
      {
        if(index<0||index>=_curveRows.Length)return;
        var rows=CurrentCurveLayout();
        try
        {
          rows[index].Curve.Dispose();rows.RemoveAt(index);
          if(index<rows.Count)rows[index]=rows[index] with {LinkedToPrevious=false};
          if(!ApplyCurveLayout(_s.Doc,_s,rows))return;
          if(!_s.EditMode)
          {
            _s.EditedNotchId=Guid.Empty;_s.SelectedNotches.Clear();_s.EditedNotchObjects.Clear();
            SelectBothCurves(_s.Doc,_s);
          }
          RefreshCurveRows();UpdateEditMode();_s.EditPicking?.RefreshAssociations();Redraw();Persist();
        }
        finally{foreach(var row in rows)row.Curve.Dispose();}
      }

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
        UpdateCurveDragRowHighlight();
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
        UpdateCurveDragRowHighlight();
        _curveDropInsertionIndex = -1;
      }

      void UpdateCurveDragRowHighlight()
      {
        if (_curveDragHighlightAdorner is not { } highlight)
          return;
        // OLE dragging can defer layout; update the row and its overlay together.
        highlight.AdornedElement.UpdateLayout();
        _curveDragHighlightLayer?.Update(highlight.AdornedElement);
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
        return Math.Max(ConfiguredDefaultWindowWidth, DefaultWindowWidth + LabelPrefixLayoutAllowance);
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
        SetModeButtonActive(_selectCurvesButton,_curveSelectionInProgress);
      }

      static TableCell FL(string text, string? toolTip = null) =>
        new TableCell(new Label
        {
          Text = text,
          ToolTip = toolTip,
          VerticalAlignment = VerticalAlignment.Center,
        });

      void Redraw() => _s.Doc.Views.Redraw();

      internal bool ViewportPreviewActive => _viewportPointerActive &&
        (_notchLayerDrop.ControlObject as System.Windows.Controls.ComboBox)?.IsDropDownOpen != true &&
        (_labelLayerDrop.ControlObject as System.Windows.Controls.ComboBox)?.IsDropDownOpen != true;

      void SetCurveRowHover(int curveIndex)
      {
        if (curveIndex < 0 || curveIndex >= _curveRows.Length)
          return;
        _curveHoverConduit.CurveIndex = curveIndex;
        _curveHoverConduit.Curve = _curveRows[curveIndex].Curve;
        _curveHoverConduit.SecondCurve = null;
        _curveHoverConduit.Enabled = true;
        _s.EditPicking?.SetRowHoveredSources([SourceFeedbackId(_s,_curveRows[curveIndex].SourceId)]);
        ApplyCurveRowHighlights(curveIndex);
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
        _s.EditPicking?.SetRowHoveredSources([SourceFeedbackId(_s,_curveRows[boundaryIndex-1].SourceId),SourceFeedbackId(_s,_curveRows[boundaryIndex].SourceId)]);
        ApplyCurveRowHighlights(boundaryIndex - 1, boundaryIndex);
        Redraw();
      }
      void SetCurveGroupHover(int rowIndex)
      {
        if(rowIndex<0||rowIndex>=_curveRows.Length)return;
        int logical=_curveRows[rowIndex].LogicalIndex;
        _curveHoverConduit.CurveIndex=-1;_curveHoverConduit.Curve=_s.Curves[logical];_curveHoverConduit.SecondCurve=null;_curveHoverConduit.Enabled=true;
        _s.EditPicking?.SetRowHoveredSources(_s.PerCurveSourceIds[logical].Select(id=>SourceFeedbackId(_s,id)));
        ApplyCurveRowHighlights(Enumerable.Range(0,_curveRows.Length).Where(index=>_curveRows[index].LogicalIndex==logical).ToArray());Redraw();
      }

      void ClearCurveRowHover()
      {
        _s.EditPicking?.SetRowHoveredSources([]);
        ApplyCurveRowHighlights(
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
      internal void ClearCurveRowFeedback()=>ClearCurveRowHover();

      void ApplyCurveRowHighlights(params int[] rowIndices)
      {
        var highlighted = rowIndices.Where(index =>
          index >= 0 && index < _curveDragHandles.Length).ToHashSet();
        for (int index = 0; index < _curveDragHandles.Length; index++)
        {
          if (_curveDragHandles[index] is not { } handle) continue;
          handle.BackgroundColor = highlighted.Contains(index)
            ? CurveRowHoverBackground
            : Colors.Transparent;
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
          ApplyCurveRowHighlights(rowIndex);
      }

      void ApplyDynamic()
      {
        UpdateMultipleState();
      }

      static TextBox MakeTextBox(string text) =>
        new TextBox { Text = text, Width = 70, Height = 22 };

      static int LabelInputWidth(TextBox input)
      {
        if (input.ControlObject is System.Windows.Controls.TextBox native)
        {
          native.ApplyTemplate();
          var sample = new System.Windows.Media.FormattedText(LabelValueWidthSample,
            System.Globalization.CultureInfo.CurrentUICulture, native.FlowDirection,
            new System.Windows.Media.Typeface(native.FontFamily, native.FontStyle, native.FontWeight, native.FontStretch),
            native.FontSize, System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(native).PixelsPerDip);
          return (int)Math.Ceiling(sample.WidthIncludingTrailingWhitespace + native.Padding.Left + native.Padding.Right +
            native.BorderThickness.Left + native.BorderThickness.Right + FieldResetOverlay.TextReserveWidth + LabelTextCaretWidth);
        }
        using var measure = new Label { Text = LabelValueWidthSample, Font = input.Font };
        return (int)Math.Ceiling(measure.GetPreferredSize().Width + LabelTextFallbackInsets +
          FieldResetOverlay.TextReserveWidth + LabelTextCaretWidth);
      }

      static NumericStepper MakeNumberStepper(double value, double minValue,
        double maxValue, double increment, int maximumDecimalPlaces = 3)
      {
        return new NumericStepper
        {
          MinValue = minValue,
          MaxValue = maxValue,
          Increment = increment,
          DecimalPlaces = 0,
          MaximumDecimalPlaces = maximumDecimalPlaces,
          CultureInfo = System.Globalization.CultureInfo.InvariantCulture,
          Width = 90,
          Height = 22,
          // Rhino 8 coerces Value when precision or bounds change; assign it last.
          Value = Math.Clamp(RoundForDisplay(value, maximumDecimalPlaces), minValue, maxValue),
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
        var key = (_s.NotchTypeValues[typeIndex], active,
          _s.NotchLengthOpt.CurrentValue, _s.NotchWidthOpt.CurrentValue);
        if (native.Content is System.Windows.FrameworkElement existing && Equals(existing.Tag, key))
          return;
        var glyph = CreateNotchTypeGlyph(key.Item1, active, key.Item3, key.Item4);
        glyph.Tag = key;
        native.Content = glyph;
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

      public void RefreshNotchTrimIcon()
      {
        _notchTrimButton.ToolTip = _s.NotchTrim switch
        {
          NotchTrimMode.No => "NotchTrim: No (click for Split)",
          NotchTrimMode.Split => $"NotchTrim: Split to {_s.NotchTrimLayer} (click for Trim)",
          _ => "NotchTrim: Trim (click for No)",
        };
        if (_notchTrimButton.ControlObject is not System.Windows.Controls.Button native)
          return;
        if (native.Content is System.Windows.Controls.Canvas existing && Equals(existing.Tag, _s.NotchTrim))
          return;
        var ink = System.Windows.SystemColors.ControlTextBrush;
        var splitInk = new System.Windows.Media.SolidColorBrush(
          System.Windows.Media.Color.FromRgb(0, 145, 201));
        var drawing = new System.Windows.Controls.Canvas
        {
          Width = NotchTrimIconWidth,
          Height = NotchTrimIconHeight,
          IsHitTestVisible = false,
          Tag = _s.NotchTrim,
        };
        void Line(double x1, double y1, double x2, double y2,
          System.Windows.Media.Brush color, double stroke)
        {
          drawing.Children.Add(new System.Windows.Shapes.Line
          {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = color, StrokeThickness = stroke,
            SnapsToDevicePixels = true,
          });
        }
        Line(2, 14, 10, 14, ink, NotchTrimCurveStroke);
        Line(20, 14, 28, 14, ink, NotchTrimCurveStroke);
        if (_s.NotchTrim != NotchTrimMode.Trim)
          Line(10, 14, 20, 14,
            _s.NotchTrim == NotchTrimMode.Split ? splitInk : ink,
            _s.NotchTrim == NotchTrimMode.Split ? NotchTrimSplitStroke : NotchTrimCurveStroke);
        Line(10, 14, 15, 4, ink, NotchTrimCurveStroke);
        Line(15, 4, 20, 14, ink, NotchTrimCurveStroke);
        native.Content = drawing;
        native.Padding = new System.Windows.Thickness(0);
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
              ToolTip = $"Collapse {title}",
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
                ToolTip = isNotch
                  ? "Create notch geometry at each placement"
                  : "Create a text label at each placement",
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
                ToolTip = title == "Multiple"
                  ? "Controls for placing several notches across the selected curves"
                  : title,
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

      internal void DrawCurveHover(GetPointDrawEventArgs e)=>_curveHoverConduit.Draw(e);
      sealed class CurveRowHoverConduit(RhinoDoc doc) : IDisposable
      {
        public int CurveIndex { get; set; } = -1;
        public Curve? Curve { get; set; }
        public Curve? SecondCurve { get; set; }
        readonly PreviewDisplay.ObjectHighlighter _highlight=new(doc,HoveredSourceStyle){RedrawOnChange=false,DrawOnTop=true};
        readonly List<TextDot> _dots=[];
        bool _enabled;
        public bool Enabled
        {
          get=>_enabled;
          set
          {
            _enabled=value;_highlight.SetObjects([]);foreach(var dot in _dots)dot.Dispose();_dots.Clear();
            if(!value)return;
            var geometry=new Dictionary<Guid,GeometryBase>();
            foreach(var curve in new[]{Curve,SecondCurve}.OfType<Curve>())
            {
              geometry[Guid.NewGuid()]=curve;
              var start=new TextDot(SourceStartCaption,curve.PointAtStart);var end=new TextDot(SourceEndCaption,curve.PointAtEnd);
              _dots.Add(start);_dots.Add(end);geometry[Guid.NewGuid()]=start;geometry[Guid.NewGuid()]=end;
            }
            _highlight.SetObjects(geometry.Keys,geometry);
          }
        }
        internal void Draw(GetPointDrawEventArgs e){if(Enabled)_highlight.DrawForegroundHighlights(e);}
        public void Dispose(){Enabled=false;_highlight.Dispose();}
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
        if(_s.EditMode){ClearMultiplePreview();return;}
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
        bool requested = !_s.EditMode&&!_viewportPointerActive &&
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
        ApplyCurveRowHighlights(_viewportCurveHoverRowIndex);
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
          _s.Doc, ConfiguredCurveLengthDifferenceToleranceInches);
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
        _percentCheck.ToolTip = _s.EditMode&&_percentCheck.Checked==null
          ? "Selected notches use mixed position modes. Check to use proportional movement for all selected matches; uncheck to use equal-distance movement."
          : warning
          ? "Curve lengths differ significantly; enable Percent to align relative positions"
          : "Use the same relative position on curves of different lengths";
      }

      void RefreshPercentCheck()
      {
        bool suppressed=_suppress;_suppress=true;
        try
        {
          bool? state=EditedNotchPercentState(_s.Doc,_s);
          _percentCheck.ThreeState=state==null;
          _percentCheck.Checked=state;
        }
        finally{_suppress=suppressed;}
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
          if(!string.Equals(LayerSelector.GetDropDownValue(_notchLayerDrop,string.Empty),_s.NotchLayerName,StringComparison.OrdinalIgnoreCase))
            LayerSelector.SetDropDownValue(_notchLayerDrop,_s.NotchLayerName);
          if(!string.Equals(LayerSelector.GetDropDownValue(_labelLayerDrop,string.Empty),_s.LabelLayerName,StringComparison.OrdinalIgnoreCase))
            LayerSelector.SetDropDownValue(_labelLayerDrop,_s.LabelLayerName);
          RefreshPercentCheck();
          _groupCheck.Checked            = _s.GroupToggle.CurrentValue;
          _associationsCheck.Checked     = _s.Associations;
          _oppositeMatchesCheck.Checked  = _s.OppositeMatches;
          if (_keepSelectionCheck != null)
            _keepSelectionCheck.IsChecked = _s.KeepCurveSelection;
          _notchCheck.Checked            = _s.NotchToggle.CurrentValue;
          if (_notchHeaderCheck != null)
            _notchHeaderCheck.IsChecked = _s.NotchToggle.CurrentValue;
          _labelCheck.Checked            = _s.LabelToggle.CurrentValue;
          if (_labelHeaderCheck != null)
            _labelHeaderCheck.IsChecked = _s.LabelToggle.CurrentValue;
          _labelValueBox.Text            = _s.LabelValueText;
          _labelPrefixBox.Text           = _s.LabelPrefixText;
          _labelSizeStepper.Value        = _s.ManualLabelSize;
          _labelSizeAutoCheck.Checked    = _s.LabelSizeAutoToggle.CurrentValue;
          UpdateLabelSizeEnabled();
          _labelSizePctStepper.Value     = _s.LabelSizePctValues[Math.Max(0, _s.LabelSizePctIndex)];
          _labelOffsetStepper.Value      = _s.LabelOffsetOpt.CurrentValue;
          _labelOffsetYStepper.Value     = _s.LabelOffsetYOpt.CurrentValue;
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
              UpdateCurveBothSidesButton(i,
                _s.CurveBothSidesBySource.GetValueOrDefault(
                  _curveRows[i].SourceId, _s.BothSidesToggle.CurrentValue));
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
        UpdateEditMode();
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
        if(!_s.EditMode&&_percentCheck.Checked.HasValue)_s.PercentToggle.CurrentValue=_percentCheck.Checked==true;
        _s.GroupToggle.CurrentValue = _groupCheck.Checked == true;
        _s.Associations = _associationsCheck.Checked == true;
        _s.LabelToggle.CurrentValue = _labelCheck.Checked == true;
        if (!_s.NotchToggle.CurrentValue && !_s.LabelToggle.CurrentValue)
          _s.NotchToggle.CurrentValue = true;
        _s.LabelValueText = _labelValueBox.Text;
        _s.LabelPrefixText = _labelPrefixBox.Text;
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
      public void UpdateDragPosition(double current,double total,double delta)
      {
        UpdateDistanceLabels(current,delta,Math.Max(0,total-current),null,null,null);
        _fromPrevLbl.Text=delta.ToString("+0.###;-0.###;0",System.Globalization.CultureInfo.InvariantCulture);
      }

      static string FormatSegmentDistance(double? value) =>
        value.HasValue ? $"({FormatPanelNumber(value.Value)})" : "";

      public void UpdateUndoEnabled()
      {
        _undoBtn.Enabled = _s.NotchRecords.Count > 0 || _s.Removals.Count > 0 || _s.Moves.Count > 0;
        _redoBtn.Enabled = _s.RedoBatches.Count > 0 || _s.RemovalRedo.Count > 0 || _s.MoveRedo.Count > 0;
      }
      public void UpdateEditMode()
      {
        bool editing=_s.EditMode,selected=editing&&_s.EditedNotchId!=Guid.Empty;
        _fromPrevCaption.Text=editing?"Move":"From previous";
        _fromPrevLbl.ToolTip=editing?"Signed movement from the drag starting position, in document units":"Distance from the previous notch";
        _editNotchesButton.ToolTip=editing?"Add new notches":"Edit a notch with its original/nested matches. Click replaces selection; Shift adds; Ctrl deselects.";
        SetEditToolIcon(_editNotchesButton,editing?AddToolGlyph:EditToolGlyph);
        _applyNotchEdit.Visible=_highlightExisting.Visible=editing;
        _applyNotchEdit.Enabled=editing;
        var objects=selected?EditNotchObjects(_s.Doc,_s):[];
        bool hasNotches=objects.Any(IsNotchGeometry),hasLabels=objects.Any(obj=>obj.Geometry is TextEntity or TextDot);
        _editLabels.Visible=_removeLabels.Visible=editing;
        bool plural=objects.Where(IsNotchGeometry).Select(obj=>(obj.Attributes.GetUserString(NotchDataPrefix+"curve_key"),MatchingNotches.StationKey(obj.Attributes))).Distinct().Skip(1).Any();
        _editLabels.Text=hasLabels?(plural?"Update Labels":"Update Label"):(plural?"Add Labels":"Add Label");
        _removeLabels.Text=plural?"Remove Labels":"Remove Label";
        _editLabels.Enabled=_removeLabels.Enabled=editing;
        foreach(var button in new[]{_pickNotchPairs,_selectNotchMates,_unlinkNotch}){button.Visible=editing;button.Enabled=editing;}
        _pickNotchPairs.Enabled=editing;
        _repairNestLinks.Visible=editing;
        _assignNotchSource.Visible=editing;_assignNotchSource.Enabled=editing;
        _oppositeMatchesCheck.Visible=editing;
        RefreshLinkButtonState();
        if(_multipleEditSection!=null)_multipleEditSection.Enabled=!editing;
        _selectCurvesButton.Enabled=!editing;
        _percentCheck.Enabled=true;
        _groupCheck.Enabled=!editing;
        _associationsCheck.Enabled=!editing;
        _associationsCheck.Visible=!editing;
        if(editing)ClearMultiplePreview();
        RefreshPercentCheck();
        Redraw();
      }
    }
  }
}

namespace vTools.Commands
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


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
      var ids = session.PerCurveSourceIds.SelectMany(sourceIds => sourceIds)
        .Select(session.Sources.ParentId).ToHashSet();
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
        doc.ModelAbsoluteTolerance * ConfiguredOffsetSplitContactToleranceScale,
        RhinoMath.ZeroTolerance * ConfiguredOffsetSplitZeroToleranceScale);
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
}

namespace vTools
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using Rhino;
  using Rhino.DocObjects;
  using Rhino.Geometry;


  internal sealed record NotchCurveSource(
    Guid Id, RhinoObject ParentObject, Curve Geometry, ComponentIndex Component,
    Point3d SelectionPoint);

  internal sealed class NotchCurveSources
  {
    private readonly record struct SourceKey(Guid ParentId, ComponentIndexType Type, int Index);
    private readonly Dictionary<SourceKey, Guid> _keys = new();
    private readonly Dictionary<Guid, (Guid ParentId, ComponentIndex Component)> _references = new();
    private readonly Dictionary<string,Guid> _sectionKeys=new();
    private readonly Dictionary<Guid,string> _sections=new();
    private readonly Dictionary<Guid,bool> _sectionReversed=new();
    internal Guid RegisterSection(Guid parentId,string key,bool reversed)
    {
      if(_sectionKeys.TryGetValue(key,out var existing))return existing;
      var id=Guid.NewGuid();_sectionKeys[key]=id;_sections[id]=key;_sectionReversed[id]=reversed;_references[id]=(parentId,ComponentIndex.Unset);return id;
    }
    internal string? SectionKey(Guid id)=>_sections.GetValueOrDefault(id);
    internal bool SectionReversed(Guid id)=>_sectionReversed.GetValueOrDefault(id);

    internal Guid Register(Guid parentId, ComponentIndex component)
    {
      if (component.Index < 0) component = ComponentIndex.Unset;
      var key = new SourceKey(parentId, component.ComponentIndexType, component.Index);
      if (_keys.TryGetValue(key, out var existing)) return existing;
      var id = component.Index < 0 ? parentId : Guid.NewGuid();
      _keys[key] = id;
      _references[id] = (parentId, component);
      return id;
    }

    internal Guid ParentId(Guid id) => _references.TryGetValue(id, out var source) ? source.ParentId : id;
    internal bool IsSegment(Guid id) => _sections.ContainsKey(id)||_references.TryGetValue(id, out var source) && source.Component.Index >= 0;
    internal RhinoObject? FindObject(RhinoDoc doc, Guid id) => doc.Objects.FindId(ParentId(id));

    private NotchCurveSource? Capture(ObjRef reference)
    {
      var parent = reference.Object();
      if (parent?.Geometry is not Curve || reference.Curve() is not Curve curve) return null;
      var component = reference.GeometryComponentIndex;
      return new NotchCurveSource(Register(parent.Id, component), parent,
        curve.DuplicateCurve(), component, reference.SelectionPoint());
    }

    internal List<NotchCurveSource> Capture(IEnumerable<ObjRef> references)
    {
      var sources = references.Select(Capture).Where(source => source != null)
        .Cast<NotchCurveSource>().ToList();
      var wholeParents = sources.Where(source => source.Component.Index < 0)
        .Select(source => source.ParentObject.Id).ToHashSet();
      return sources.Where(source => source.Component.Index < 0 || !wholeParents.Contains(source.ParentObject.Id))
        .DistinctBy(source => source.Id).ToList();
    }

    internal List<NotchCurveSource> SelectedSources(RhinoDoc doc)
    {
      var sources = new List<NotchCurveSource>();
      foreach (var obj in SelectedParents(doc))
      {
        if (obj.Geometry is not Curve) continue;
        if (obj.IsSelected(false) != 0)
        {
          using var reference = new ObjRef(doc, obj.Id);
          var source = Capture(reference);
          if (source != null) sources.Add(source);
        }
        else
          foreach (var component in obj.GetSelectedSubObjects() ?? [])
          {
            using var reference = new ObjRef(doc, obj.Id, component);
            var source = Capture(reference);
            if (source != null) sources.Add(source);
          }
      }
      return sources.DistinctBy(source => source.Id).ToList();
    }

    internal void Select(RhinoDoc doc, Guid id, bool selected)
    {
      var obj = FindObject(doc, id);
      if (obj == null) return;
      if (_references.TryGetValue(id, out var source) && source.Component.Index >= 0)
        obj.SelectSubObject(source.Component, selected, true);
      else
        obj.Select(selected);
    }

    internal static void ClearSelection(RhinoDoc doc)
    {
      var parents = SelectedParents(doc).ToArray();
      doc.Objects.UnselectAll();
      foreach (var parent in parents) parent.UnselectAllSubObjects();
    }

    private static IEnumerable<RhinoObject> SelectedParents(RhinoDoc doc) =>
      doc.Objects.GetObjectList(new ObjectEnumeratorSettings
      {
        SelectedObjectsFilter = true,
        SubObjectSelected = true,
        IncludeLights = false,
        IncludeGrips = false
      });
  }
}

namespace vTools
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using Rhino.Geometry;


  internal sealed class NotchPlacementRuns : IDisposable
  {
    private sealed record Run(Curve Curve, double Start, double Length, double GeometryLength);
    private readonly List<Run> _runs = [];
    private readonly double _tolerance;
    private readonly double[] _ends;

    internal int Count => _runs.Count;

    internal NotchPlacementRuns(IReadOnlyList<Curve> segments, double tolerance)
    {
      _tolerance = tolerance;
      double prefix = 0.0;
      foreach (var (start, count) in ConnectedRanges(
        segments.Select(curve => curve.PointAtStart).ToArray(),
        segments.Select(curve => curve.PointAtEnd).ToArray(), tolerance))
      {
        var sources = segments.Skip(start).Take(count).ToArray();
        var joined = count > 1 ? Curve.JoinCurves(sources, tolerance, preserveDirection: true) : null;
        if (joined is { Length: 1 })
        {
          var length = sources.Sum(curve => curve.GetLength());
          _runs.Add(new Run(joined[0], prefix, length, joined[0].GetLength()));
          prefix += length;
        }
        else
        {
          foreach (var unused in joined ?? []) unused.Dispose();
          foreach (var source in sources)
          {
            var length = source.GetLength();
            _runs.Add(new Run(source.DuplicateCurve(), prefix, length, length));
            prefix += length;
          }
        }
      }
      _ends = _runs.Select(run => run.Start + run.Length).ToArray();
    }

    internal (Curve Curve, double Length) Resolve(double logicalLength, bool preferFollowing)
    {
      if (_runs.Count == 0) throw new InvalidOperationException("No placement curves.");
      int index = SelectRunIndex(_ends,
        logicalLength, preferFollowing, _tolerance);
      var run = _runs[index];
      if(Math.Abs(logicalLength-run.Start)<=_tolerance)return (run.Curve,0);
      if(Math.Abs(logicalLength-run.Start-run.Length)<=_tolerance)return (run.Curve,run.GeometryLength);
      return (run.Curve, Math.Clamp(logicalLength - run.Start, 0.0, run.GeometryLength));
    }

    internal static List<(int Start, int Count)> ConnectedRanges(
      IReadOnlyList<Point3d> starts, IReadOnlyList<Point3d> ends, double tolerance)
    {
      var ranges = new List<(int, int)>();
      int first = 0;
      for (int index = 1; index <= starts.Count; index++)
      {
        if (index < starts.Count && ends[index - 1].DistanceTo(starts[index]) <= tolerance)
          continue;
        ranges.Add((first, index - first));
        first = index;
      }
      return ranges;
    }

    internal static int SelectRunIndex(IReadOnlyList<double> ends, double length,
      bool preferFollowing, double tolerance)
    {
      for (int index = 0; index < ends.Count; index++)
      {
        if (length < ends[index] - tolerance) return index;
        if (Math.Abs(length - ends[index]) <= tolerance)
          return preferFollowing && index + 1 < ends.Count ? index + 1 : index;
      }
      return Math.Max(0, ends.Count - 1);
    }

    public void Dispose()
    {
      foreach (var run in _runs) run.Curve.Dispose();
      _runs.Clear();
    }
  }
}

namespace vTools
{
  using System;
  using System.Collections.Generic;
  using System.Linq;
  using Rhino;
  using Rhino.Geometry;
  using Rhino.Geometry.Intersect;


  internal static class NotchBoundarySides
  {
    // Defaults and customizable constants
    private static readonly double[] DirectionSamples = [0.25, 0.5, 0.75]; // Fractions of each source or overlapping boundary interval used to verify a consistent notch side; between zero and one.
    private const double MinimumTangentAgreement = 0.99; // Absolute dot product of unit XY tangents required to identify matching boundary directions; zero through one.
    private const double MinimumNormalAgreement = 0.05; // Minimum absolute unit-vector dot product toward a group boundary across the source tangent; zero through one avoids nearly tangential choices.

    internal static Dictionary<Guid, bool> DetectGroupSides(
      IReadOnlyList<(Guid Id, Curve Curve)> selected, IReadOnlyList<Curve> groupCurves,
      double tolerance)
    {
      var result = new Dictionary<Guid, bool>();
      tolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);
      var processed = new HashSet<Guid>();
      foreach (var source in selected)
      {
        if (processed.Contains(source.Id)) continue;
        var elevation = source.Curve.GetBoundingBox(false).Center.Z;
        bool Coplanar(Curve curve)
        {
          var bounds = curve.GetBoundingBox(false);
          return bounds.IsValid && Math.Abs(bounds.Min.Z - elevation) <= tolerance &&
            Math.Abs(bounds.Max.Z - elevation) <= tolerance;
        }
        var targets = selected.Where(item => !processed.Contains(item.Id) && Coplanar(item.Curve)).ToArray();
        processed.UnionWith(targets.Select(item => item.Id));
        var plane = new Plane(new Point3d(0.0, 0.0, elevation), Vector3d.ZAxis);
        var boundaries = new List<Curve>();
        try
        {
          using var regions = Curve.CreateBooleanRegions(
            groupCurves.Where(Coplanar), plane, combineRegions: true, tolerance);
          if (regions == null) continue;
          for (var region = 0; region < regions.RegionCount; region++)
            boundaries.AddRange(regions.RegionCurves(region));
          var outerFirst = boundaries.Where(curve => curve.IsClosed)
            .Select(curve =>
            {
              using var area = AreaMassProperties.Compute(curve);
              return (Curve: curve, Area: area == null ? 0.0 : Math.Abs(area.Area));
            }).OrderByDescending(item => item.Area).ToArray();
          foreach (var target in targets)
          {
            foreach (var boundary in outerFirst)
            {
              if (!DirectionSamples.All(fraction => boundary.Curve.Contains(
                    target.Curve.PointAt(target.Curve.Domain.ParameterAt(fraction)), plane, tolerance)
                  is PointContainment.Inside or PointContainment.Coincident))
                continue;
              var side = FindSideTowardBoundary(target.Curve, boundary.Curve, plane, tolerance);
              if (side.HasValue)
              {
                result[target.Id] = side.Value;
                Log.Write("vNotches", $"group boundary side: source={target.Id:D} outer_area={boundary.Area:G17} side={(side.Value ? "Left" : "Right")}");
              }
              break;
            }
          }
        }
        catch (Exception ex)
        {
          Log.Write("vNotches", $"group boundary side unavailable: {ex.Message}");
        }
        finally { foreach (var boundary in boundaries) boundary.Dispose(); }
      }
      return result;
    }

    private static bool? FindSideTowardBoundary(Curve source, Curve boundary, Plane plane, double tolerance)
    {
      bool? side = null;
      foreach (var fraction in DirectionSamples)
      {
        var parameter = source.Domain.ParameterAt(fraction);
        var point = source.PointAt(parameter);
        var containment = boundary.Contains(point, plane, tolerance);
        if (containment is not (PointContainment.Inside or PointContainment.Coincident))
          return null;
        bool? sampleSide;
        if (containment == PointContainment.Coincident)
          sampleSide = FindInteriorSide(source, new[] { boundary }, tolerance);
        else
        {
          if (!boundary.ClosestPoint(point, out var boundaryParameter)) return null;
          var direction = boundary.PointAt(boundaryParameter) - point;
          var tangent = source.TangentAt(parameter);
          sampleSide = ClassifyBoundaryDirection(tangent.X, tangent.Y, direction.X, direction.Y);
        }
        if (!sampleSide.HasValue || side.HasValue && side.Value != sampleSide.Value)
          return null;
        side = sampleSide;
      }
      return side;
    }

    private static bool? ClassifyBoundaryDirection(double tangentX, double tangentY, double directionX, double directionY)
    {
      var magnitude = Math.Sqrt(tangentX * tangentX + tangentY * tangentY) *
        Math.Sqrt(directionX * directionX + directionY * directionY);
      if (!double.IsFinite(magnitude) || magnitude <= RhinoMath.ZeroTolerance) return null;
      var agreement = (tangentX * directionY - tangentY * directionX) / magnitude;
      return Math.Abs(agreement) < MinimumNormalAgreement ? null : agreement > 0.0;
    }

    internal static Dictionary<Guid, bool> Detect(
      IReadOnlyList<(Guid Id, Curve Curve)> selected, IReadOnlyList<Curve> context,
      double tolerance)
    {
      var result = new Dictionary<Guid, bool>();
      if (selected.Count == 0) return result;
      tolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);
      var curves = selected.Select(item => item.Curve).Concat(context).ToArray();
      var bounds = curves.Select(curve => curve.GetBoundingBox(false)).ToArray();
      using var tree = new RTree();
      for (int index = 0; index < curves.Length; index++)
        if (bounds[index].IsValid) tree.Insert(bounds[index], index);

      var processed = new HashSet<int>();
      for (int seed = 0; seed < selected.Count; seed++)
      {
        if (processed.Contains(seed) || !bounds[seed].IsValid) continue;
        double elevation = bounds[seed].Center.Z;
        bool Coplanar(int index) => bounds[index].IsValid &&
          Math.Abs(bounds[index].Min.Z - elevation) <= tolerance &&
          Math.Abs(bounds[index].Max.Z - elevation) <= tolerance;
        if (!Coplanar(seed)) { processed.Add(seed); continue; }

        // Closed curves define their own inside, even when nested in other parts.
        if (curves[seed].IsClosed)
        {
          processed.Add(seed);
          var orientation = curves[seed].ClosedCurveOrientation(Vector3d.ZAxis);
          var interiorLeft = InteriorIsLeft(orientation, 1.0);
          if (interiorLeft.HasValue)
            result[selected[seed].Id] = interiorLeft.Value;
          continue;
        }
        var component = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
          int current = queue.Dequeue();
          var searchBounds = bounds[current];
          searchBounds.Inflate(tolerance);
          var candidates = new List<int>();
          tree.Search(searchBounds, (_, hit) => candidates.Add(hit.Id));
          foreach (int candidate in candidates)
          {
            if (component.Contains(candidate) || !Coplanar(candidate) ||
                !Touch(curves[current], curves[candidate], tolerance)) continue;
            component.Add(candidate);
            queue.Enqueue(candidate);
          }
        }

        var targets = component.Where(index => index < selected.Count &&
          (index == seed || !curves[index].IsClosed)).ToArray();
        processed.UnionWith(targets);
        try
        {
          var plane = new Plane(new Point3d(0.0, 0.0, elevation), Vector3d.ZAxis);
          using var regions = Curve.CreateBooleanRegions(
            component.Select(index => curves[index]), plane, combineRegions: true, tolerance);
          if (regions == null) continue;
          var boundaries = new List<Curve>();
          try
          {
            for (int region = 0; region < regions.RegionCount; region++)
              boundaries.AddRange(regions.RegionCurves(region));
            foreach (int target in targets)
            {
              bool? interiorLeft = FindInteriorSide(curves[target], boundaries, tolerance);
              if (interiorLeft.HasValue) result[selected[target].Id] = interiorLeft.Value;
            }
          }
          finally { foreach (var boundary in boundaries) boundary.Dispose(); }
        }
        catch (Exception ex)
        {
          Log.Write("vNotches", $"automatic boundary side unavailable: {ex.Message}");
        }
      }
      return result;
    }

    private static bool Touch(Curve first, Curve second, double tolerance)
    {
      using var intersections = Intersection.CurveCurve(first, second, tolerance, tolerance);
      if (intersections != null && intersections.Count > 0) return true;
      bool EndTouches(Point3d point, Curve curve) =>
        curve.ClosestPoint(point, out double parameter) &&
        point.DistanceTo(curve.PointAt(parameter)) <= tolerance;
      return EndTouches(first.PointAtStart, second) || EndTouches(first.PointAtEnd, second) ||
        EndTouches(second.PointAtStart, first) || EndTouches(second.PointAtEnd, first);
    }

    private static bool? InteriorIsLeft(CurveOrientation orientation, double tangentAgreement) =>
      orientation == CurveOrientation.Undefined || !double.IsFinite(tangentAgreement) ||
      Math.Abs(tangentAgreement) < MinimumTangentAgreement
        ? null
        : (orientation == CurveOrientation.CounterClockwise) == (tangentAgreement > 0.0);

    private static bool? FindInteriorSide(Curve source, IReadOnlyList<Curve> boundaries, double tolerance)
    {
      bool? result = null;
      foreach (var boundary in boundaries)
      {
        if (!boundary.IsClosed) continue;
        var orientation = boundary.ClosedCurveOrientation(Vector3d.ZAxis);
        if (orientation == CurveOrientation.Undefined) continue;
        using var intersections = Intersection.CurveCurve(source, boundary, tolerance, tolerance);
        if (intersections == null) continue;
        foreach (var contact in intersections)
        {
          if (!contact.IsOverlap) continue;
          foreach (double fraction in DirectionSamples)
          {
            double parameter = contact.OverlapA.ParameterAt(fraction);
            if (!boundary.ClosestPoint(source.PointAt(parameter), out double boundaryParameter)) continue;
            var tangent = source.TangentAt(parameter);
            var boundaryTangent = boundary.TangentAt(boundaryParameter);
            tangent.Z = boundaryTangent.Z = 0.0;
            if (!tangent.Unitize() || !boundaryTangent.Unitize()) continue;
            double agreement = tangent * boundaryTangent;
            var interiorLeft = InteriorIsLeft(orientation, agreement);
            if (!interiorLeft.HasValue) continue;
            // A curve bordering regions on opposite sides has no single inward choice.
            if (result.HasValue && result.Value != interiorLeft.Value) return null;
            result = interiorLeft.Value;
          }
        }
      }
      return result;
    }
  }
}
