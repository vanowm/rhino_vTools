using System.Drawing;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace vTools.Commands;

public sealed class vMirror : Rhino.Commands.Command
{
  // Option defaults
  private const bool DefaultCopy = true; // true mirrors copies and keeps originals; false moves originals across the mirror plane.
  private const bool DefaultFlipText = true; // true keeps mirrored text readable; false mirrors text geometry literally.
  private const bool DefaultChangeText = false; // true swaps mirrored vTitle text using the configured replacements; false preserves its text.
  private const double DefaultDistance = 30.0; // Gap between original and directional mirrored bounds in model units; zero or greater.
  private static readonly Color PreviewSelectedColor = // Rhino selection color used for mirrored preview geometry.
    Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
  private static readonly Color PreviewGhostColor = Color.FromArgb(145, 145, 145); // Neutral ARGB color for originals ghosted in move mode.
  private const double PreviewSelectedTransparency = 0.3; // Selected-preview material transparency from 0 (opaque) to 1 (invisible).
  private const double PreviewGhostTransparency = 0.7; // Ghosted-original material transparency from 0 (opaque) to 1 (invisible).
  private const int PreviewSelectedThicknessOffset = 1; // Relative curve/wire thickness added to selected preview geometry; integer zero or greater.
  private const int PreviewGhostThicknessOffset = 0; // Relative curve/wire thickness added to ghosted geometry; integer zero or greater.
  private const PointStyle PreviewPointStyle = PointStyle.RoundSimple; // Plain marker style used for mirrored point-object previews without active-point crosshairs.
  private const int PreviewSelectedPointSize = 3; // Selected preview point diameter in display pixels; positive integer.
  private const int PreviewGhostPointSize = 3; // Ghosted preview point diameter in display pixels; positive integer.
  private static readonly (string A, string B, string Mode, bool PreserveCase, bool WholeWord)[]
    DefaultTextReplacementValues = // Bidirectional default title swaps: non-empty A/B text, literal/wildcard/regex mode, and matching flags.
    [
      ("OUTSIDE", "INSIDE", "literal", true, true),
      ("IN", "OUT", "literal", true, true),
    ];

  // Command-line option names are centralized so Rhino derives the same
  // keyboard accelerators/prefixes everywhere the option is shown.
  // Native _Mirror option names are kept verbatim so their command-line
  // prefixes remain 3 / C / X / Y / Z / O, matching Rhino.
  private const string OptionThreePoint = "3Point";
  private const string OptionCopy = "Copy";
  private const string OptionXAxis = "XAxis";
  private const string OptionYAxis = "YAxis";
  private const string OptionZAxis = "ZAxis";
  private const string OptionObject = "Object";

  // vMirror-only options intentionally avoid the native prefixes above.
  private const string OptionFlipText = "FlipText";
  private const string OptionChangeText = "SwapText";
  private const string OptionEditTextReplacements = "EditTextReplacements";
  private const string OptionLeft = "Left";
  private const string OptionRight = "Right";
  private const string OptionTop = "Top";
  private const string OptionBottom = "Bottom";
  private const string OptionDistance = "Distance";

  private const string OptionsSection = "vMirror";
  private const string CopyKey = "copy";
  private const string FlipTextKey = "flipText";
  private const string ChangeTextKey = "changeText";
  private const string DistanceKey = "distance";
  private const string TextReplacementsKey = "textReplacements";

  public override string EnglishName => "vMirror";

  protected override Result RunCommand(RhinoDoc doc, RunMode mode)
  {
    var (copy, flipText, changeText, distance, textReplacements, replacementsMissing) = LoadOptions();
    if (replacementsMissing)
      EnsureDefaultTextReplacements();

    if (!TrySelectObjects(
          doc,
          ref copy,
          ref flipText,
          ref changeText,
          textReplacements,
          ref distance,
          out var objectIds,
          out var selectionAction))
      return Result.Cancel;

    Plane constructionPlane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane()
      ?? Plane.WorldXY;
    using var preview = new MirrorPreviewConduit(doc, objectIds);

    Plane mirrorPlane;
    if (selectionAction.HasValue)
    {
      bool gotPlane;
      switch (selectionAction.Value)
      {
        case MirrorSelectionAction.Left:
          gotPlane = TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Left, distance, out mirrorPlane);
          break;
        case MirrorSelectionAction.Right:
          gotPlane = TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Right, distance, out mirrorPlane);
          break;
        case MirrorSelectionAction.Top:
          gotPlane = TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Top, distance, out mirrorPlane);
          break;
        case MirrorSelectionAction.Bottom:
          gotPlane = TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Bottom, distance, out mirrorPlane);
          break;
        case MirrorSelectionAction.XAxis:
          mirrorPlane = new Plane(
            constructionPlane.Origin, constructionPlane.XAxis, constructionPlane.Normal);
          gotPlane = mirrorPlane.IsValid;
          break;
        case MirrorSelectionAction.YAxis:
          mirrorPlane = new Plane(
            constructionPlane.Origin, constructionPlane.YAxis, constructionPlane.Normal);
          gotPlane = mirrorPlane.IsValid;
          break;
        case MirrorSelectionAction.ZAxis:
          mirrorPlane = constructionPlane;
          gotPlane = mirrorPlane.IsValid;
          break;
        case MirrorSelectionAction.ThreePoint:
          gotPlane = TryGetThreePointPlane(
            doc, objectIds, preview,
            ref copy, ref flipText, ref changeText,
            textReplacements, out mirrorPlane);
          break;
        case MirrorSelectionAction.Object:
          gotPlane = TryGetObjectPlane(
            doc, ref copy, ref flipText, ref changeText,
            textReplacements, out mirrorPlane);
          break;
        default:
          mirrorPlane = Plane.Unset;
          gotPlane = false;
          break;
      }

      if (!gotPlane)
        return Result.Cancel;
    }
    else if (!TryGetMirrorPlane(
               doc,
               objectIds,
               constructionPlane,
               preview,
               ref copy,
               ref flipText,
               ref changeText,
               textReplacements,
               ref distance,
               out mirrorPlane))
    {
      return Result.Cancel;
    }

    Transform mirror = Transform.Mirror(mirrorPlane);
    if (!mirror.IsValid)
    {
      doc.Views.Redraw();
      RhinoApp.WriteLine("vMirror: could not create the mirror transform.");
      return Result.Failure;
    }

    var outputIds = new List<Guid>();
    var changedTitleIds = new List<Guid>();
    var copiedGroupMembers = new Dictionary<int, List<Guid>>();
    var copiedGroupNames = new Dictionary<int, string?>();
    int failed = 0;

    // Suppress all intermediate viewport work while committing the result.
    // ViewTable.EnableRedraw has existed since Rhino 7.3. The last dynamic
    // preview remains visible until redraw is re-enabled at the end.
    bool redrawWasEnabled = doc.Views.RedrawEnabled;
    if (redrawWasEnabled)
      doc.Views.EnableRedraw(false, false, false);

    try
    {
      // Keep every mirror entry path consistent: originals deselected, final
      // mirrored objects selected.
      doc.Objects.UnselectAll();

      using (vTitle.SuspendAutomaticBoxSync())
      {
        foreach (var objectId in objectIds)
        {
          var source = doc.Objects.FindId(objectId);
          if (source == null)
          {
            failed++;
            continue;
          }

          var sourceGroups = copy
            ? source.Attributes.GetGroupList()
            : null;
          bool hasCopiedGroups =
            sourceGroups != null && sourceGroups.Length > 0;

          bool needsCustomText = GetCustomTextProcessing(
            source,
            flipText,
            changeText,
            textReplacements,
            out string? replacementText);

          Guid outputId;

          // Fastest path: let Rhino's native transform core do the work.
          // This covers all ordinary geometry in move mode and all ungrouped
          // ordinary geometry in copy mode. It avoids managed Duplicate() plus
          // the second duplication performed by ObjectTable.Add().
          if (!needsCustomText &&
              (!copy || !hasCopiedGroups))
          {
            outputId = doc.Objects.Transform(
              source, mirror, deleteOriginal: !copy);
          }
          else if (copy &&
                   hasCopiedGroups &&
                   !needsCustomText &&
                   source is InstanceObject instance)
          {
            // Preserve block instances as instances instead of exploding the
            // instance-reference geometry through the generic Add path.
            var attributes = source.Attributes.Duplicate();
            attributes.RemoveFromAllGroups();
            outputId = doc.Objects.AddInstanceObject(
              instance.InstanceDefinition.Index,
              mirror * instance.InstanceXform,
              attributes);
          }
          else
          {
            // Only text requiring FlipText/ChangeText, or copied objects that
            // need remapped groups, use the managed preparation path.
            if (!TryPrepareMirroredGeometry(
                  source,
                  mirror,
                  flipText,
                  replacementText,
                  out var preparedGeometry))
            {
              failed++;
              continue;
            }

            using (preparedGeometry)
            {
              if (copy)
              {
                var attributes = source.Attributes.Duplicate();
                if (hasCopiedGroups)
                  attributes.RemoveFromAllGroups();

                outputId =
                  doc.Objects.Add(preparedGeometry, attributes);
              }
              else
              {
                bool replaced =
                  doc.Objects.Replace(
                    objectId, preparedGeometry, false);
                outputId =
                  replaced ? objectId : Guid.Empty;
              }
            }
          }

          if (outputId == Guid.Empty)
          {
            failed++;
            continue;
          }

          if (!TryRestoreMirroredOrientation(doc, outputId))
          {
            Log.Write(
              "vMirror",
              $"could not restore mirrored face direction object={outputId}");
            failed++;
          }

          outputIds.Add(outputId);

          if (replacementText != null &&
              source.Attributes.GetUserString(vTitle.TitleFlagKey) ==
                vTitle.TitleFlagValue)
          {
            changedTitleIds.Add(outputId);
          }

          if (copy && hasCopiedGroups && sourceGroups != null)
          {
            RecordCopiedGroupMembership(
              doc,
              sourceGroups,
              outputId,
              copiedGroupMembers,
              copiedGroupNames);
          }
        }

        // Create each copied group once, with all member IDs in one call.
        // This replaces per-object group-attribute bookkeeping.
        if (copy && copiedGroupMembers.Count > 0)
        {
          CreateCopiedGroups(
            doc, copiedGroupMembers, copiedGroupNames);
        }

        // vTitle owns the frame calculation. Only titles whose text actually
        // changed require this extra document mutation.
        foreach (var titleId in changedTitleIds.Distinct())
          vTitle.SyncBoxForTitleNow(doc, titleId);
      }

      // RhinoCommon has had the IEnumerable<Guid> selection overload since
      // Rhino 5, so select the whole result set in one call.
      if (outputIds.Count > 0)
        doc.Objects.Select(outputIds);
    }
    finally
    {
      if (redrawWasEnabled)
        doc.Views.EnableRedraw(true, true, false);
    }
    Log.Write("vMirror",
      $"mirrored={outputIds.Count} failed={failed} copy={copy} flipText={flipText} " +
      $"changeText={changeText} action={selectionAction?.ToString() ?? "PickedPlane"} " +
      $"distance={distance:G17}");
    return outputIds.Count > 0 ? Result.Success : Result.Failure;
  }

  static (bool Copy, bool FlipText, bool ChangeText, double Distance, List<TextReplacementRule> Replacements, bool ReplacementsMissing) LoadOptions() =>
    ToolsOptionStore.Read(
      OptionsSection,
      section =>
      {
        bool copy = DefaultCopy;
        bool flipText = DefaultFlipText;
        bool changeText = DefaultChangeText;
        double distance = DefaultDistance;
        if (ToolsOptionStore.TryGetBool(section, CopyKey, out var savedCopy))
          copy = savedCopy;
        if (ToolsOptionStore.TryGetBool(section, FlipTextKey, out var savedFlip))
          flipText = savedFlip;
        if (ToolsOptionStore.TryGetBool(section, ChangeTextKey, out var savedChangeText))
          changeText = savedChangeText;
        if (ToolsOptionStore.TryGetDouble(section, DistanceKey, out var savedDistance) &&
            savedDistance >= 0.0)
          distance = savedDistance;

        bool replacementsMissing = section?[TextReplacementsKey] == null;
        var replacements = LoadTextReplacements(section?[TextReplacementsKey]);
        if (replacementsMissing)
          replacements = DefaultTextReplacements();

        return (copy, flipText, changeText, distance, replacements, replacementsMissing);
      });

  static void SaveOptions(bool copy, bool flipText, bool changeText)
  {
    if (!ToolsOptionStore.Update(OptionsSection, section =>
      {
        section[CopyKey] = copy;
        section[FlipTextKey] = flipText;
        section[ChangeTextKey] = changeText;
      }))
      Log.Write("vMirror", $"could not save options: {ToolsOptionStore.LastError}");
  }

  static void SaveDistance(double distance)
  {
    if (!ToolsOptionStore.Update(OptionsSection, section =>
      {
        section[DistanceKey] = distance;
      }))
      Log.Write("vMirror", $"could not save distance: {ToolsOptionStore.LastError}");
  }

  static void EnsureDefaultTextReplacements()
  {
    if (!ToolsOptionStore.Update(OptionsSection, section =>
      {
        if (section[TextReplacementsKey] == null)
          section[TextReplacementsKey] = DefaultTextReplacementsJson();
      }))
      Log.Write("vMirror", $"could not save default text replacements: {ToolsOptionStore.LastError}");
  }

  static JsonArray TextReplacementsJson(IEnumerable<TextReplacementRule> rules)
  {
    var array = new JsonArray();
    foreach (var rule in rules)
    {
      array.Add(new JsonObject
      {
        ["a"] = rule.A,
        ["b"] = rule.B,
        ["mode"] = rule.Mode,
        ["preserveCase"] = rule.PreserveCase,
        ["wholeWord"] = rule.WholeWord,
      });
    }
    return array;
  }

  static bool SaveTextReplacements(IReadOnlyList<TextReplacementRule> rules)
  {
    if (ToolsOptionStore.Update(OptionsSection, section =>
      {
        section[TextReplacementsKey] = TextReplacementsJson(rules);
      }))
      return true;

    Log.Write("vMirror", $"could not save text replacements: {ToolsOptionStore.LastError}");
    RhinoApp.WriteLine($"vMirror: could not save text replacements: {ToolsOptionStore.LastError}");
    return false;
  }

  static void EditTextReplacements(List<TextReplacementRule> rules)
  {
    try
    {
      using var dialog = new TextReplacementEditorDialog(rules);
      if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow))
        return;

      var edited = dialog.Rules;
      if (!SaveTextReplacements(edited))
        return;

      rules.Clear();
      rules.AddRange(edited);
    }
    catch (Exception ex)
    {
      Log.Write("vMirror", $"could not show text replacement editor: {ex}");
      RhinoApp.WriteLine($"vMirror: could not show text replacement editor: {ex.Message}");
    }
  }

  static void ReadToggles(
    OptionToggle copyToggle, OptionToggle flipToggle, OptionToggle changeTextToggle,
    ref bool copy, ref bool flipText, ref bool changeText)
  {
    bool nextCopy = copyToggle.CurrentValue;
    bool nextFlip = flipToggle.CurrentValue;
    bool nextChangeText = changeTextToggle.CurrentValue;
    if (nextCopy == copy && nextFlip == flipText && nextChangeText == changeText)
      return;
    copy = nextCopy;
    flipText = nextFlip;
    changeText = nextChangeText;
    SaveOptions(copy, flipText, changeText);
  }

  static void ReadDistance(OptionDouble distanceOption, ref double distance)
  {
    double nextDistance = distanceOption.CurrentValue;
    if (Math.Abs(nextDistance - distance) <= RhinoMath.ZeroTolerance)
      return;
    distance = nextDistance;
    SaveDistance(distance);
  }

  static List<TextReplacementRule> DefaultTextReplacements() =>
    DefaultTextReplacementValues
      .Select(value => new TextReplacementRule(
        value.A,
        value.B,
        value.Mode,
        value.PreserveCase,
        value.WholeWord))
      .ToList();

  static JsonArray DefaultTextReplacementsJson() =>
    TextReplacementsJson(DefaultTextReplacements());

  static List<TextReplacementRule> LoadTextReplacements(JsonNode? node)
  {
    var rules = new List<TextReplacementRule>();
    if (node is not JsonArray array)
      return rules;

    foreach (var item in array)
    {
      if (item is not JsonObject obj)
        continue;

      string a = JsonString(obj["a"]);
      string b = JsonString(obj["b"]);

      // Backward compatibility with the first ChangeText implementation.
      // A one-way find/replace entry becomes one bidirectional pair.
      if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
      {
        a = JsonString(obj["find"]);
        b = JsonString(obj["replace"]);
      }
      if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        continue;

      string mode = NormalizeReplacementMode(JsonString(obj["mode"]));

      bool preserveCase = JsonBool(obj["preserveCase"], true);
      bool wholeWord = JsonBool(obj["wholeWord"], mode == "literal");

      // Old configs may contain both A->B and B->A as separate entries.
      // Collapse those into a single bidirectional rule.
      bool duplicate = rules.Any(rule =>
        rule.Mode == mode &&
        rule.PreserveCase == preserveCase &&
        rule.WholeWord == wholeWord &&
        ((string.Equals(rule.A, a, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(rule.B, b, StringComparison.OrdinalIgnoreCase)) ||
         (string.Equals(rule.A, b, StringComparison.OrdinalIgnoreCase) &&
          string.Equals(rule.B, a, StringComparison.OrdinalIgnoreCase))));
      if (!duplicate)
        rules.Add(new TextReplacementRule(a, b, mode, preserveCase, wholeWord));
    }
    return rules;
  }

  static string NormalizeReplacementMode(string? mode)
  {
    string normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
    return normalized is "literal" or "wildcard" or "regex"
      ? normalized
      : "literal";
  }

  static string JsonString(JsonNode? node)
  {
    try
    {
      return node is JsonValue value && value.TryGetValue<string>(out var text)
        ? text ?? string.Empty
        : string.Empty;
    }
    catch
    {
      return string.Empty;
    }
  }

  static bool JsonBool(JsonNode? node, bool defaultValue)
  {
    try
    {
      return node is JsonValue value && value.TryGetValue<bool>(out var result)
        ? result
        : defaultValue;
    }
    catch
    {
      return defaultValue;
    }
  }

  enum MirrorSelectionAction
  {
    ThreePoint,
    XAxis,
    YAxis,
    ZAxis,
    Object,
    Left,
    Right,
    Top,
    Bottom,
  }

  static bool TrySelectObjects(
    RhinoDoc doc,
    ref bool copy,
    ref bool flipText,
    ref bool changeText,
    List<TextReplacementRule> textReplacements,
    ref double distance,
    out List<Guid> objectIds,
    out MirrorSelectionAction? selectionAction)
  {
    objectIds = [];
    selectionAction = null;

    // Native-style preselection behavior: if vMirror starts with one or more
    // selected objects, accept them immediately and continue directly to
    // "Start of mirror plane". Do not insert another selection/options step.
    foreach (var obj in doc.Objects.GetSelectedObjects(
               includeLights: false, includeGrips: false))
    {
      if (obj != null && obj.Id != Guid.Empty)
        objectIds.Add(obj.Id);
    }

    if (objectIds.Count > 0)
    {
      objectIds = objectIds.Distinct().ToList();
      return true;
    }

    // Phase 1: no objects selected yet. Mirror-action options stay hidden.
    // The remaining options keep the same relative order as the canonical
    // "Start of mirror plane" prompt.
    var firstGet = new GetObject();
    firstGet.SetCommandPrompt("Select objects to mirror");
    firstGet.GeometryFilter = ObjectType.AnyObject;
    firstGet.GroupSelect = true;
    firstGet.SubObjectSelect = false;
    firstGet.EnablePreSelect(false, true);
    firstGet.EnableClearObjectsOnEntry(false);
    firstGet.EnableUnselectObjectsOnExit(false);
    firstGet.DeselectAllBeforePostSelect = false;

    var firstCopyToggle = new OptionToggle(copy, "No", "Yes");
    var firstFlipToggle = new OptionToggle(flipText, "No", "Yes");
    var firstChangeTextToggle = new OptionToggle(changeText, "No", "Yes");
    var firstDistanceOption = new OptionDouble(distance, 0.0, double.MaxValue);

    firstGet.AddOptionToggle(OptionCopy, ref firstCopyToggle);
    firstGet.AddOptionToggle(OptionFlipText, ref firstFlipToggle);
    firstGet.AddOptionToggle(OptionChangeText, ref firstChangeTextToggle);
    firstGet.AddOptionDouble(OptionDistance, ref firstDistanceOption);
    int firstTextReplacementsOption =
      firstGet.AddOption(OptionEditTextReplacements);

    while (firstGet.ObjectCount == 0)
    {
      var firstResult = firstGet.GetMultiple(1, -1);

      ReadToggles(
        firstCopyToggle,
        firstFlipToggle,
        firstChangeTextToggle,
        ref copy,
        ref flipText,
        ref changeText);
      ReadDistance(firstDistanceOption, ref distance);

      if (firstResult == GetResult.Option)
      {
        if (firstGet.OptionIndex() == firstTextReplacementsOption)
          EditTextReplacements(textReplacements);
        continue;
      }

      if (firstResult != GetResult.Object ||
          firstGet.CommandResult() != Result.Success)
        return false;
    }

    // Phase 2: at least one object/group is now selected. Rebuild GetObject so
    // the complete option list can use EXACTLY the same order as
    // "Start of mirror plane":
    // 3Point, Copy, XAxis, YAxis, ZAxis, Object, Left, Right, Top, Bottom,
    // Distance, FlipText, SwapText, EditTextReplacements.
    //
    // The first GetObject leaves its selection selected in the document.
    // This second getter ignores preselection, so it waits for more selections,
    // an option, or Enter instead of immediately consuming those objects.
    var get = new GetObject();
    get.SetCommandPrompt(
      "Select more objects, choose mirror option, or press Enter to pick mirror plane");
    get.GeometryFilter = ObjectType.AnyObject;
    get.GroupSelect = true;
    get.SubObjectSelect = false;
    get.EnablePreSelect(false, true);
    get.EnableClearObjectsOnEntry(false);
    get.EnableUnselectObjectsOnExit(false);
    get.DeselectAllBeforePostSelect = false;
    get.AcceptNothing(true);

    int threePointOption = get.AddOption(OptionThreePoint);

    var copyToggle = new OptionToggle(copy, "No", "Yes");
    get.AddOptionToggle(OptionCopy, ref copyToggle);

    int xAxisOption = get.AddOption(OptionXAxis);
    int yAxisOption = get.AddOption(OptionYAxis);
    int zAxisOption = get.AddOption(OptionZAxis);
    int objectOption = get.AddOption(OptionObject);
    int leftOption = get.AddOption(OptionLeft);
    int rightOption = get.AddOption(OptionRight);
    int topOption = get.AddOption(OptionTop);
    int bottomOption = get.AddOption(OptionBottom);

    var distanceOption =
      new OptionDouble(distance, 0.0, double.MaxValue);
    get.AddOptionDouble(OptionDistance, ref distanceOption);

    var flipToggle = new OptionToggle(flipText, "No", "Yes");
    var changeTextToggle = new OptionToggle(changeText, "No", "Yes");
    get.AddOptionToggle(OptionFlipText, ref flipToggle);
    get.AddOptionToggle(OptionChangeText, ref changeTextToggle);
    int textReplacementsOption =
      get.AddOption(OptionEditTextReplacements);

    while (true)
    {
      var result = get.GetMultiple(1, 0);

      ReadToggles(
        copyToggle,
        flipToggle,
        changeTextToggle,
        ref copy,
        ref flipText,
        ref changeText);
      ReadDistance(distanceOption, ref distance);

      if (result == GetResult.Option)
      {
        int option = get.OptionIndex();

        if (option == textReplacementsOption)
        {
          EditTextReplacements(textReplacements);
          continue;
        }

        selectionAction =
          option == threePointOption ? MirrorSelectionAction.ThreePoint :
          option == xAxisOption ? MirrorSelectionAction.XAxis :
          option == yAxisOption ? MirrorSelectionAction.YAxis :
          option == zAxisOption ? MirrorSelectionAction.ZAxis :
          option == objectOption ? MirrorSelectionAction.Object :
          option == leftOption ? MirrorSelectionAction.Left :
          option == rightOption ? MirrorSelectionAction.Right :
          option == topOption ? MirrorSelectionAction.Top :
          option == bottomOption ? MirrorSelectionAction.Bottom :
          null;

        if (selectionAction.HasValue)
        {
          objectIds = GetCurrentMirrorSelection(doc, get);
          if (objectIds.Count > 0)
            return true;

          selectionAction = null;
        }

        continue;
      }

      if (result == GetResult.Nothing)
      {
        objectIds = GetCurrentMirrorSelection(doc, get);
        return objectIds.Count > 0;
      }

      if (result != GetResult.Object ||
          get.CommandResult() != Result.Success)
        return false;

      objectIds = GetCurrentMirrorSelection(doc, get);
      return objectIds.Count > 0;
    }
  }


  static List<Guid> GetCurrentMirrorSelection(
    RhinoDoc doc, GetObject get)
  {
    var ids = new HashSet<Guid>();

    for (int index = 0; index < get.ObjectCount; index++)
    {
      Guid id = get.Object(index).ObjectId;
      if (id != Guid.Empty)
        ids.Add(id);
    }

    // This also covers preselected objects when the user chooses Left/Right/
    // Top/Bottom immediately, before GetObject has copied them into ObjectCount.
    foreach (var obj in doc.Objects.GetSelectedObjects(
               includeLights: false, includeGrips: false))
    {
      if (obj != null && obj.Id != Guid.Empty)
        ids.Add(obj.Id);
    }

    return ids.ToList();
  }


  static bool TryGetMirrorPlane(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, Plane constructionPlane,
    MirrorPreviewConduit preview, ref bool copy, ref bool flipText, ref bool changeText,
    List<TextReplacementRule> textReplacements, ref double distance, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt("Start of mirror plane");
      int threePointOption = get.AddOption(OptionThreePoint);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      get.AddOptionToggle(OptionCopy, ref copyToggle);
      int xAxisOption = get.AddOption(OptionXAxis);
      int yAxisOption = get.AddOption(OptionYAxis);
      int zAxisOption = get.AddOption(OptionZAxis);
      int objectOption = get.AddOption(OptionObject);
      int leftOption = get.AddOption(OptionLeft);
      int rightOption = get.AddOption(OptionRight);
      int topOption = get.AddOption(OptionTop);
      int bottomOption = get.AddOption(OptionBottom);
      var distanceOption = new OptionDouble(distance, 0.0, double.MaxValue);
      get.AddOptionDouble(OptionDistance, ref distanceOption);
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      var changeTextToggle = new OptionToggle(changeText, "No", "Yes");
      get.AddOptionToggle(OptionFlipText, ref flipToggle);
      get.AddOptionToggle(OptionChangeText, ref changeTextToggle);
      int textReplacementsOption = get.AddOption(OptionEditTextReplacements);

      var result = get.Get();
      ReadToggles(copyToggle, flipToggle, changeTextToggle, ref copy, ref flipText, ref changeText);
      ReadDistance(distanceOption, ref distance);
      if (result == GetResult.Option)
      {
        int option = get.OptionIndex();
        if (option == textReplacementsOption)
        {
          EditTextReplacements(textReplacements);
          continue;
        }
        if (option == threePointOption)
          return TryGetThreePointPlane(
            doc, objectIds, preview, ref copy, ref flipText, ref changeText,
            textReplacements, out mirrorPlane);
        if (option == xAxisOption)
        {
          mirrorPlane = new Plane(
            constructionPlane.Origin, constructionPlane.XAxis, constructionPlane.Normal);
          return mirrorPlane.IsValid;
        }
        if (option == yAxisOption)
        {
          mirrorPlane = new Plane(
            constructionPlane.Origin, constructionPlane.YAxis, constructionPlane.Normal);
          return mirrorPlane.IsValid;
        }
        if (option == zAxisOption)
        {
          mirrorPlane = constructionPlane;
          return mirrorPlane.IsValid;
        }
        if (option == objectOption)
          return TryGetObjectPlane(
            doc, ref copy, ref flipText, ref changeText, textReplacements, out mirrorPlane);
        if (option == leftOption)
          return TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Left, distance, out mirrorPlane);
        if (option == rightOption)
          return TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Right, distance, out mirrorPlane);
        if (option == topOption)
          return TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Top, distance, out mirrorPlane);
        if (option == bottomOption)
          return TryGetDirectionalMirrorPlane(
            doc, objectIds, constructionPlane, MirrorSide.Bottom, distance, out mirrorPlane);
        continue;
      }
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
        return false;

      Point3d firstPoint = get.Point();
      return TryGetMirrorEnd(doc, objectIds, constructionPlane, preview, firstPoint,
        ref copy, ref flipText, ref changeText, textReplacements, out mirrorPlane);
    }
  }

  enum MirrorSide
  {
    Left,
    Right,
    Top,
    Bottom,
  }

  static bool TryGetDirectionalMirrorPlane(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, Plane constructionPlane,
    MirrorSide side, double distance, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    BoundingBox bounds = BoundingBox.Unset;

    foreach (var objectId in objectIds)
    {
      var source = doc.Objects.FindId(objectId);
      if (source == null)
        continue;

      BoundingBox objectBounds = source.Geometry.GetBoundingBox(constructionPlane);
      if (!objectBounds.IsValid)
        continue;

      if (!bounds.IsValid)
        bounds = objectBounds;
      else
        bounds.Union(objectBounds);
    }

    if (!bounds.IsValid)
    {
      RhinoApp.WriteLine("vMirror: could not calculate the selected objects' bounds.");
      return false;
    }

    double halfGap = distance * 0.5;
    switch (side)
    {
      case MirrorSide.Left:
      {
        Point3d origin = constructionPlane.PointAt(bounds.Min.X - halfGap, 0.0);
        mirrorPlane = new Plane(origin, constructionPlane.YAxis, constructionPlane.Normal);
        break;
      }
      case MirrorSide.Right:
      {
        Point3d origin = constructionPlane.PointAt(bounds.Max.X + halfGap, 0.0);
        mirrorPlane = new Plane(origin, constructionPlane.YAxis, constructionPlane.Normal);
        break;
      }
      case MirrorSide.Top:
      {
        Point3d origin = constructionPlane.PointAt(0.0, bounds.Max.Y + halfGap);
        mirrorPlane = new Plane(origin, constructionPlane.XAxis, constructionPlane.Normal);
        break;
      }
      case MirrorSide.Bottom:
      {
        Point3d origin = constructionPlane.PointAt(0.0, bounds.Min.Y - halfGap);
        mirrorPlane = new Plane(origin, constructionPlane.XAxis, constructionPlane.Normal);
        break;
      }
    }

    return mirrorPlane.IsValid;
  }

  static bool TryGetMirrorEnd(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, Plane constructionPlane,
    MirrorPreviewConduit preview, Point3d firstPoint,
    ref bool copy, ref bool flipText, ref bool changeText,
    List<TextReplacementRule> textReplacements, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt("End of mirror plane");
      get.SetBasePoint(firstPoint, true);
      get.DrawLineFromPoint(firstPoint, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      var changeTextToggle = new OptionToggle(changeText, "No", "Yes");
      get.AddOptionToggle(OptionCopy, ref copyToggle);
      get.AddOptionToggle(OptionFlipText, ref flipToggle);
      get.AddOptionToggle(OptionChangeText, ref changeTextToggle);
      int textReplacementsOption = get.AddOption(OptionEditTextReplacements);
      preview.SetGhostOriginals(!copy);
      get.DynamicDraw += (_, e) =>
      {
        if (!TryTwoPointPlane(
          firstPoint, e.CurrentPoint, constructionPlane, out var dynamicPlane))
          return;
        preview.DrawMirrored(
          e.Display,
          Transform.Mirror(dynamicPlane),
          flipToggle.CurrentValue,
          changeTextToggle.CurrentValue,
          textReplacements);
      };

      doc.Objects.UnselectAll();
      preview.Enabled = true;
      doc.Views.Redraw();

      GetResult result;
      try { result = get.Get(); }
      finally
      {
        // Disable the conduit but intentionally do NOT redraw here. On a
        // successful point, the last dynamic-preview pixels remain visible
        // while RunCommand creates the real mirrored objects. The final redraw
        // then swaps preview -> final geometry with no blank-frame flicker.
        preview.Enabled = false;
      }

      ReadToggles(copyToggle, flipToggle, changeTextToggle, ref copy, ref flipText, ref changeText);
      if (result == GetResult.Option)
      {
        doc.Views.Redraw();
        if (get.OptionIndex() == textReplacementsOption)
          EditTextReplacements(textReplacements);
        continue;
      }
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
      {
        doc.Views.Redraw();
        return false;
      }
      if (TryTwoPointPlane(firstPoint, get.Point(), constructionPlane, out mirrorPlane))
        return true;

      doc.Views.Redraw();
      RhinoApp.WriteLine("vMirror: mirror axis is too short.");
    }
  }

  static bool TryTwoPointPlane(
    Point3d firstPoint, Point3d secondPoint, Plane constructionPlane,
    out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    Vector3d axis = secondPoint - firstPoint;
    if (!axis.Unitize())
      return false;
    mirrorPlane = new Plane(firstPoint, axis, constructionPlane.Normal);
    return mirrorPlane.IsValid;
  }

  static bool TryGetThreePointPlane(
    RhinoDoc doc, IReadOnlyList<Guid> objectIds, MirrorPreviewConduit preview,
    ref bool copy, ref bool flipText, ref bool changeText,
    List<TextReplacementRule> textReplacements, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    if (!TryGetPlainPoint(
          "First point of mirror plane", ref copy, ref flipText, ref changeText,
          textReplacements, out var first))
      return false;
    if (!TryGetPlainPoint(
          "Second point of mirror plane", ref copy, ref flipText, ref changeText,
          textReplacements, out var second))
      return false;

    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt("Third point of mirror plane");
      get.SetBasePoint(second, true);
      get.DrawLineFromPoint(second, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      var changeTextToggle = new OptionToggle(changeText, "No", "Yes");
      get.AddOptionToggle(OptionCopy, ref copyToggle);
      get.AddOptionToggle(OptionFlipText, ref flipToggle);
      get.AddOptionToggle(OptionChangeText, ref changeTextToggle);
      int textReplacementsOption = get.AddOption(OptionEditTextReplacements);
      preview.SetGhostOriginals(!copy);
      get.DynamicDraw += (_, e) =>
      {
        var dynamicPlane = new Plane(first, second, e.CurrentPoint);
        if (!dynamicPlane.IsValid)
          return;
        preview.DrawMirrored(
          e.Display,
          Transform.Mirror(dynamicPlane),
          flipToggle.CurrentValue,
          changeTextToggle.CurrentValue,
          textReplacements);
      };

      doc.Objects.UnselectAll();
      preview.Enabled = true;
      doc.Views.Redraw();

      GetResult result;
      try { result = get.Get(); }
      finally
      {
        // Same seamless handoff as the normal 2-point mirror: leave the last
        // dynamic-preview frame on-screen until the final objects are ready.
        preview.Enabled = false;
      }

      ReadToggles(copyToggle, flipToggle, changeTextToggle, ref copy, ref flipText, ref changeText);
      if (result == GetResult.Option)
      {
        doc.Views.Redraw();
        if (get.OptionIndex() == textReplacementsOption)
          EditTextReplacements(textReplacements);
        continue;
      }
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
      {
        doc.Views.Redraw();
        return false;
      }

      mirrorPlane = new Plane(first, second, get.Point());
      if (mirrorPlane.IsValid)
        return true;

      doc.Views.Redraw();
      RhinoApp.WriteLine("vMirror: the three points do not define a plane.");
    }
  }

  static bool TryGetPlainPoint(
    string prompt, ref bool copy, ref bool flipText, ref bool changeText,
    List<TextReplacementRule> textReplacements, out Point3d point)
  {
    point = Point3d.Unset;
    while (true)
    {
      var get = new GetPoint();
      get.SetCommandPrompt(prompt);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      var changeTextToggle = new OptionToggle(changeText, "No", "Yes");
      get.AddOptionToggle(OptionCopy, ref copyToggle);
      get.AddOptionToggle(OptionFlipText, ref flipToggle);
      get.AddOptionToggle(OptionChangeText, ref changeTextToggle);
      int textReplacementsOption = get.AddOption(OptionEditTextReplacements);
      var result = get.Get();
      ReadToggles(copyToggle, flipToggle, changeTextToggle, ref copy, ref flipText, ref changeText);
      if (result == GetResult.Option)
      {
        if (get.OptionIndex() == textReplacementsOption)
          EditTextReplacements(textReplacements);
        continue;
      }
      if (result != GetResult.Point || get.CommandResult() != Result.Success)
        return false;
      point = get.Point();
      return point.IsValid;
    }
  }

  static bool TryGetObjectPlane(
    RhinoDoc doc, ref bool copy, ref bool flipText, ref bool changeText,
    List<TextReplacementRule> textReplacements, out Plane mirrorPlane)
  {
    mirrorPlane = Plane.Unset;
    while (true)
    {
      var get = new GetObject();
      get.SetCommandPrompt("Select planar surface or face for mirror plane");
      get.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
      get.SubObjectSelect = true;
      get.GroupSelect = false;
      get.EnablePreSelect(false, true);
      var copyToggle = new OptionToggle(copy, "No", "Yes");
      var flipToggle = new OptionToggle(flipText, "No", "Yes");
      var changeTextToggle = new OptionToggle(changeText, "No", "Yes");
      get.AddOptionToggle(OptionCopy, ref copyToggle);
      get.AddOptionToggle(OptionFlipText, ref flipToggle);
      get.AddOptionToggle(OptionChangeText, ref changeTextToggle);
      int textReplacementsOption = get.AddOption(OptionEditTextReplacements);
      var result = get.Get();
      ReadToggles(copyToggle, flipToggle, changeTextToggle, ref copy, ref flipText, ref changeText);
      if (result == GetResult.Option)
      {
        if (get.OptionIndex() == textReplacementsOption)
          EditTextReplacements(textReplacements);
        continue;
      }
      if (result != GetResult.Object || get.CommandResult() != Result.Success)
        return false;

      ObjRef objRef = get.Object(0);
      var face = objRef.Face();
      if (face != null && face.TryGetPlane(out mirrorPlane, doc.ModelAbsoluteTolerance))
        return true;
      var surface = objRef.Surface();
      if (surface != null && surface.TryGetPlane(out mirrorPlane, doc.ModelAbsoluteTolerance))
        return true;
      if (objRef.Brep() is { Faces.Count: 1 } brep &&
          brep.Faces[0].TryGetPlane(out mirrorPlane, doc.ModelAbsoluteTolerance))
        return true;
      RhinoApp.WriteLine("vMirror: select a planar surface or face.");
    }
  }

  static string ApplyTextReplacements(string text, IReadOnlyList<TextReplacementRule> rules)
  {
    if (string.IsNullOrEmpty(text) || rules.Count == 0)
      return text;

    // Collect both directions against the ORIGINAL text. Applying the chosen matches
    // only once prevents A -> B from immediately being converted back B -> A.
    var candidates = new List<TextReplacementMatch>();
    for (int ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
    {
      var rule = rules[ruleIndex];
      AddTextReplacementMatches(text, rule, rule.A, rule.B, ruleIndex, 0, candidates);
      AddTextReplacementMatches(text, rule, rule.B, rule.A, ruleIndex, 1, candidates);
    }

    if (candidates.Count == 0)
      return text;

    candidates.Sort((a, b) =>
    {
      int byIndex = a.Index.CompareTo(b.Index);
      if (byIndex != 0) return byIndex;
      int byLength = b.Length.CompareTo(a.Length);
      if (byLength != 0) return byLength;
      int byRule = a.RuleIndex.CompareTo(b.RuleIndex);
      if (byRule != 0) return byRule;
      return a.Direction.CompareTo(b.Direction);
    });

    var result = new StringBuilder(text.Length);
    int position = 0;
    foreach (var candidate in candidates)
    {
      if (candidate.Index < position)
        continue;

      result.Append(text, position, candidate.Index - position);
      result.Append(candidate.Replacement);
      position = candidate.Index + candidate.Length;
    }
    result.Append(text, position, text.Length - position);
    return result.ToString();
  }

  static void AddTextReplacementMatches(
    string text, TextReplacementRule rule, string find, string replace,
    int ruleIndex, int direction, List<TextReplacementMatch> candidates)
  {
    Regex? regex = BuildTextReplacementRegex(find, rule.Mode, rule.WholeWord);
    if (regex == null)
      return;

    try
    {
      foreach (Match match in regex.Matches(text))
      {
        if (!match.Success || match.Length == 0)
          continue;

        string replacement = rule.Mode == "literal"
          ? replace
          : match.Result(replace);
        if (rule.PreserveCase)
          replacement = PreserveReplacementCase(match.Value, replacement);

        candidates.Add(new TextReplacementMatch(
          match.Index, match.Length, replacement, ruleIndex, direction));
      }
    }
    catch (ArgumentException ex)
    {
      Log.Write("vMirror", $"invalid text replacement '{find}': {ex.Message}");
    }
  }

  static Regex? BuildTextReplacementRegex(string find, string mode, bool wholeWord)
  {
    string pattern;
    switch (mode)
    {
      case "regex":
        pattern = find;
        break;
      case "wildcard":
        pattern = WildcardToRegex(find);
        break;
      default:
        pattern = Regex.Escape(find);
        break;
    }

    if (wholeWord && mode != "regex")
      pattern = $@"(?<![\p{{L}}\p{{N}}])(?:{pattern})(?![\p{{L}}\p{{N}}])";

    try
    {
      return new Regex(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    }
    catch (ArgumentException ex)
    {
      Log.Write("vMirror", $"invalid {mode} text replacement '{find}': {ex.Message}");
      return null;
    }
  }

  static string WildcardToRegex(string wildcard)
  {
    var pattern = new StringBuilder();
    foreach (char ch in wildcard)
    {
      switch (ch)
      {
        case '*':
          pattern.Append("(.*)");
          break;
        case '?':
          pattern.Append("(.)");
          break;
        default:
          pattern.Append(Regex.Escape(ch.ToString()));
          break;
      }
    }
    return pattern.ToString();
  }

  static string PreserveReplacementCase(string source, string replacement)
  {
    var letters = source.Where(char.IsLetter).ToArray();
    if (letters.Length == 0)
      return replacement;

    if (letters.All(char.IsUpper))
      return replacement.ToUpperInvariant();
    if (letters.All(char.IsLower))
      return replacement.ToLowerInvariant();

    bool firstLetterSeen = false;
    bool titleCase = true;
    foreach (char ch in source)
    {
      if (!char.IsLetter(ch))
        continue;
      if (!firstLetterSeen)
      {
        if (!char.IsUpper(ch)) titleCase = false;
        firstLetterSeen = true;
      }
      else if (!char.IsLower(ch))
      {
        titleCase = false;
      }
    }
    if (titleCase)
      return ToTitleCase(replacement);

    return replacement;
  }

  static string ToTitleCase(string text)
  {
    var chars = text.ToCharArray();
    bool capitalize = true;
    for (int i = 0; i < chars.Length; i++)
    {
      if (!char.IsLetter(chars[i]))
      {
        capitalize = true;
        continue;
      }
      chars[i] = capitalize
        ? char.ToUpperInvariant(chars[i])
        : char.ToLowerInvariant(chars[i]);
      capitalize = false;
    }
    return new string(chars);
  }

  sealed class TextReplacementEditorDialog : Dialog<bool>
  {
    readonly List<TextReplacementRule> _rules;
    readonly GridView _grid;
    readonly Button _removeButton;
    readonly Button _upButton;
    readonly Button _downButton;
    readonly Button _saveButton;
    readonly Label _status;

    internal TextReplacementEditorDialog(IEnumerable<TextReplacementRule> rules)
    {
      Title = "vMirror Text Replacements";
      Result = false;
      Resizable = true;
      ClientSize = new Eto.Drawing.Size(850, 430);
      MinimumSize = new Eto.Drawing.Size(650, 320);

      _rules = rules.Select(rule => rule.Clone()).ToList();

      _grid = new GridView
      {
        AllowEmptySelection = true,
        AllowMultipleSelection = false,
        ShowHeader = true,
        GridLines = GridLines.Both,
        RowHeight = 26,
        DataStore = _rules
      };

      _grid.Columns.Add(new GridColumn
      {
        HeaderText = "A",
        DataCell = new TextBoxCell
        {
          Binding = Binding.Property<TextReplacementRule, string>(rule => rule.A)
        },
        Editable = true,
        Expand = true,
        Width = 210,
        Resizable = true
      });

      _grid.Columns.Add(new GridColumn
      {
        HeaderText = "B",
        DataCell = new TextBoxCell
        {
          Binding = Binding.Property<TextReplacementRule, string>(rule => rule.B)
        },
        Editable = true,
        Expand = true,
        Width = 210,
        Resizable = true
      });

      _grid.Columns.Add(new GridColumn
      {
        HeaderText = "Mode",
        DataCell = new ComboBoxCell
        {
          DataStore = new[] { "literal", "wildcard", "regex" },
          Binding = Binding.Property<TextReplacementRule, object>(rule => rule.Mode)
        },
        Editable = true,
        Width = 110,
        Resizable = true
      });

      _grid.Columns.Add(new GridColumn
      {
        HeaderText = "Preserve Case",
        DataCell = new CheckBoxCell
        {
          Binding = Binding.Property<TextReplacementRule, bool?>(rule => rule.PreserveCaseCell)
        },
        Editable = true,
        Width = 105,
        Resizable = true
      });

      _grid.Columns.Add(new GridColumn
      {
        HeaderText = "Whole Word",
        DataCell = new CheckBoxCell
        {
          Binding = Binding.Property<TextReplacementRule, bool?>(rule => rule.WholeWordCell)
        },
        Editable = true,
        Width = 95,
        Resizable = true
      });

      _status = new Label { Wrap = WrapMode.Word };
      var addButton = new Button { Text = "Add" };
      _removeButton = new Button { Text = "Remove" };
      _upButton = new Button { Text = "Up" };
      _downButton = new Button { Text = "Down" };
      _saveButton = new Button { Text = "Save" };
      var cancelButton = new Button { Text = "Cancel" };

      addButton.Click += (_, _) => AddRule();
      _removeButton.Click += (_, _) => RemoveRule();
      _upButton.Click += (_, _) => MoveRule(-1);
      _downButton.Click += (_, _) => MoveRule(1);
      _saveButton.Click += (_, _) => SaveAndClose();
      cancelButton.Click += (_, _) => Close();
      _grid.SelectionChanged += (_, _) => UpdateButtons();
      _grid.CellEdited += (_, _) => _status.Text = string.Empty;

      var rowButtons = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        Items = { addButton, _removeButton, _upButton, _downButton }
      };

      var bottomButtons = new StackLayout
      {
        Orientation = Orientation.Horizontal,
        HorizontalContentAlignment = HorizontalAlignment.Right,
        Spacing = 8,
        Items = { cancelButton, _saveButton }
      };

      Content = new StackLayout
      {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Eto.Drawing.Padding(10),
        Spacing = 8,
        Items =
        {
          new Label
          {
            Text = "Edit replacement pairs directly in the table. Each row is bidirectional: A ↔ B.",
            Wrap = WrapMode.Word
          },
          new StackLayoutItem(_grid, true),
          rowButtons,
          new Label
          {
            Text = "Mode: literal, wildcard, or regex. Whole Word is ignored for regex rules.",
            Wrap = WrapMode.Word
          },
          _status,
          bottomButtons
        }
      };

      DefaultButton = _saveButton;
      AbortButton = cancelButton;

      if (_rules.Count > 0)
        _grid.SelectedRow = 0;
      UpdateButtons();
    }

    internal List<TextReplacementRule> Rules =>
      _rules.Select(rule => rule.Clone()).ToList();

    int SelectedIndex => _grid.SelectedRow;

    void RefreshGrid(int selectedIndex)
    {
      _grid.CommitEdit();
      _grid.DataStore = null;
      _grid.DataStore = _rules;

      if (_rules.Count == 0)
        _grid.SelectedRow = -1;
      else
        _grid.SelectedRow = Math.Max(0, Math.Min(selectedIndex, _rules.Count - 1));

      UpdateButtons();
    }

    void UpdateButtons()
    {
      int index = SelectedIndex;
      bool hasRule = index >= 0 && index < _rules.Count;
      _removeButton.Enabled = hasRule;
      _upButton.Enabled = hasRule && index > 0;
      _downButton.Enabled = hasRule && index < _rules.Count - 1;
    }

    void AddRule()
    {
      _grid.CommitEdit();
      _rules.Add(new TextReplacementRule(string.Empty, string.Empty, "literal", true, true));
      int index = _rules.Count - 1;
      RefreshGrid(index);
      _grid.BeginEdit(index, 0);
    }

    void RemoveRule()
    {
      _grid.CommitEdit();
      int index = SelectedIndex;
      if (index < 0 || index >= _rules.Count)
        return;

      _rules.RemoveAt(index);
      _status.Text = string.Empty;
      RefreshGrid(Math.Min(index, _rules.Count - 1));
    }

    void MoveRule(int offset)
    {
      _grid.CommitEdit();
      int index = SelectedIndex;
      int target = index + offset;
      if (index < 0 || index >= _rules.Count || target < 0 || target >= _rules.Count)
        return;

      (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
      _status.Text = string.Empty;
      RefreshGrid(target);
    }

    void SaveAndClose()
    {
      if (!_grid.CommitEdit())
      {
        _status.Text = "Finish editing the current cell before saving.";
        return;
      }

      for (int i = 0; i < _rules.Count; i++)
      {
        var rule = _rules[i];
        rule.A = rule.A?.Trim() ?? string.Empty;
        rule.B = rule.B?.Trim() ?? string.Empty;
        rule.Mode = NormalizeReplacementMode(rule.Mode);

        if (rule.A.Length == 0 || rule.B.Length == 0)
        {
          _status.Text = $"Rule {i + 1}: both A and B are required.";
          _grid.SelectedRow = i;
          _grid.BeginEdit(i, rule.A.Length == 0 ? 0 : 1);
          return;
        }
      }

      Result = true;
      Close();
    }
  }

  sealed class TextReplacementRule
  {
    internal TextReplacementRule(
      string a, string b, string mode, bool preserveCase, bool wholeWord)
    {
      A = a;
      B = b;
      Mode = mode;
      PreserveCase = preserveCase;
      WholeWord = wholeWord;
    }

    internal TextReplacementRule Clone() =>
      new(A, B, Mode, PreserveCase, WholeWord);

    internal string A { get; set; }
    internal string B { get; set; }
    internal string Mode { get; set; }
    internal bool PreserveCase { get; set; }
    internal bool WholeWord { get; set; }

    // CheckBoxCell uses nullable bool values; these adapters keep the rule model non-nullable.
    internal bool? PreserveCaseCell
    {
      get => PreserveCase;
      set => PreserveCase = value == true;
    }

    internal bool? WholeWordCell
    {
      get => WholeWord;
      set => WholeWord = value == true;
    }
  }

  sealed class TextReplacementMatch
  {
    internal TextReplacementMatch(
      int index, int length, string replacement, int ruleIndex, int direction)
    {
      Index = index;
      Length = length;
      Replacement = replacement;
      RuleIndex = ruleIndex;
      Direction = direction;
    }

    internal int Index { get; }
    internal int Length { get; }
    internal string Replacement { get; }
    internal int RuleIndex { get; }
    internal int Direction { get; }
  }

  static bool GetCustomTextProcessing(
    RhinoObject source,
    bool flipText,
    bool changeText,
    IReadOnlyList<TextReplacementRule> textReplacements,
    out string? replacementText)
  {
    replacementText = null;

    if (source.Geometry is not TextEntity text)
      return false;

    bool needsCustom = flipText;

    if (changeText &&
        source.Attributes.GetUserString(vTitle.TitleFlagKey) ==
          vTitle.TitleFlagValue)
    {
      string original = text.PlainText ?? string.Empty;
      string changed = ApplyTextReplacements(
        original, textReplacements);

      if (!string.Equals(
            changed, original, StringComparison.Ordinal))
      {
        replacementText = changed;
        needsCustom = true;
      }
    }

    return needsCustom;
  }

  static bool TryPrepareMirroredGeometry(
    RhinoObject source,
    Transform mirror,
    bool flipText,
    string? replacementText,
    out GeometryBase preparedGeometry)
  {
    preparedGeometry = null!;

    GeometryBase? geometry = source.Geometry.Duplicate();
    if (geometry == null)
      return false;

    if (!geometry.Transform(mirror))
    {
      geometry.Dispose();
      return false;
    }

    if (flipText && geometry is TextEntity mirroredText)
    {
      Transform flip = TextFlipTransform(
        mirroredText, Transform.Identity);
      if (!mirroredText.Transform(flip))
      {
        geometry.Dispose();
        return false;
      }
    }

    if (replacementText != null &&
        geometry is TextEntity titleText)
    {
      titleText.PlainText = replacementText;
    }

    preparedGeometry = geometry;
    return true;
  }

  static bool TryRestoreMirroredOrientation(RhinoDoc doc, Guid objectId)
  {
    var rhinoObject = doc.Objects.FindId(objectId);
    if (rhinoObject == null || !HasDirectionalFaces(rhinoObject.Geometry))
      return true;

    using var geometry = rhinoObject.Geometry.Duplicate();
    if (geometry == null || !RestoreMirroredOrientation(geometry))
      return false;

    return doc.Objects.Replace(objectId, geometry, false);
  }

  static bool HasDirectionalFaces(GeometryBase geometry) =>
    geometry is Brep or Surface or Mesh or SubD;

  static bool RestoreMirroredOrientation(GeometryBase geometry)
  {
    switch (geometry)
    {
      case Brep brep:
        brep.Flip();
        return true;
      case Surface surface:
        return surface.Reverse(direction: 0, inPlace: true) != null;
      case Mesh mesh:
        mesh.Flip(
          vertexNormals: true,
          faceNormals: true,
          faceOrientation: true,
          ngonsBoundaryDirection: true);
        return true;
      case SubD subd:
        return subd.Flip();
      default:
        return true;
    }
  }

  static void RecordCopiedGroupMembership(
    RhinoDoc doc,
    IReadOnlyList<int> sourceGroups,
    Guid outputId,
    Dictionary<int, List<Guid>> copiedGroupMembers,
    Dictionary<int, string?> copiedGroupNames)
  {
    foreach (int sourceGroupIndex in sourceGroups)
    {
      if (!copiedGroupMembers.TryGetValue(
            sourceGroupIndex, out var members))
      {
        members = new List<Guid>();
        copiedGroupMembers[sourceGroupIndex] = members;

        // Capture the source name BEFORE adding any groups. Rhino documents
        // that adding groups can invalidate group indices in some cases.
        copiedGroupNames[sourceGroupIndex] =
          doc.Groups.GroupName(sourceGroupIndex);
      }

      members.Add(outputId);
    }
  }

  static void CreateCopiedGroups(
    RhinoDoc doc,
    Dictionary<int, List<Guid>> copiedGroupMembers,
    Dictionary<int, string?> copiedGroupNames)
  {
    foreach (var pair in copiedGroupMembers)
    {
      var members = pair.Value
        .Where(id => id != Guid.Empty)
        .Distinct()
        .ToList();

      if (members.Count == 0)
        continue;

      copiedGroupNames.TryGetValue(
        pair.Key, out string? sourceName);

      if (string.IsNullOrWhiteSpace(sourceName))
      {
        doc.Groups.Add(members);
      }
      else
      {
        string copiedName = OrientCommon.MakeUniqueGroupName(
          doc, sourceName + "_copy");
        doc.Groups.Add(copiedName, members);
      }
    }
  }

  static Transform TextFlipTransform(TextEntity source, Transform initial)
  {
    using var text = source.Duplicate() as TextEntity;
    if (text == null || !text.Transform(initial))
      return Transform.Identity;
    Plane plane = text.Plane;
    Transform flip = Transform.Rotation(Math.PI, plane.XAxis, plane.Origin);
    text.Transform(flip);
    plane = text.Plane;
    Transform rotate = Transform.Rotation(Math.PI, plane.Normal, plane.Origin);
    return rotate * flip;
  }

  sealed class MirrorPreviewConduit : DisplayConduit, IDisposable
  {
    readonly RhinoDoc _doc;
    readonly HashSet<Guid> _objectIds;
    readonly DisplayMaterial _selectedMaterial = new(PreviewSelectedColor)
    {
      Transparency = PreviewSelectedTransparency,
      BackTransparency = PreviewSelectedTransparency,
    };
    readonly DisplayMaterial _ghostMaterial = new(PreviewGhostColor)
    {
      Transparency = PreviewGhostTransparency,
      BackTransparency = PreviewGhostTransparency,
    };
    bool _ghostOriginals;

    internal MirrorPreviewConduit(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
      _doc = doc;
      _objectIds = objectIds.ToHashSet();
    }

    internal void SetGhostOriginals(bool ghostOriginals) =>
      _ghostOriginals = ghostOriginals;

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
      if (_ghostOriginals && e.RhinoObject != null && _objectIds.Contains(e.RhinoObject.Id))
        e.CullObject = true;
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
      if (!_ghostOriginals)
        return;

      var sources = _objectIds
        .Select(_doc.Objects.FindId)
        .Where(source => source != null)
        .Cast<RhinoObject>()
        .ToList();
      foreach (var source in sources.Where(source => !IsPointMarker(source)))
      {
        DrawGeometry(
          e.Display, source, Transform.Identity,
          flipText: false,
          changeText: false,
          textReplacements: Array.Empty<TextReplacementRule>(),
          selected: false);
      }
      foreach (var source in sources.Where(IsPointMarker))
      {
        DrawGeometry(
          e.Display, source, Transform.Identity,
          flipText: false,
          changeText: false,
          textReplacements: Array.Empty<TextReplacementRule>(),
          selected: false);
      }
    }

    internal void DrawMirrored(
      DisplayPipeline display,
      Transform mirror,
      bool flipText,
      bool changeText,
      IReadOnlyList<TextReplacementRule> textReplacements)
    {
      // If ChangeText changes a vTitle, its stored frame curve no longer matches
      // the final title width. Suppress that stale mirrored frame and draw a
      // transient frame calculated from the changed, fully transformed title.
      var resizedFrameIds = new HashSet<Guid>();
      var changedTitles = new Dictionary<Guid, string>();

      if (changeText)
      {
        foreach (var objectId in _objectIds)
        {
          var source = _doc.Objects.FindId(objectId);
          if (!TryGetChangedPreviewTitle(
                source, textReplacements, out var changedText))
            continue;

          var frameIds = GetSelectedTitleFrameIds(source!);
          if (frameIds.Count == 0)
            continue;

          changedTitles[objectId] = changedText;
          foreach (var frameId in frameIds)
            resizedFrameIds.Add(frameId);
        }
      }

      var previewSources = _objectIds
        .Where(objectId => !resizedFrameIds.Contains(objectId))
        .Select(_doc.Objects.FindId)
        .Where(source => source != null)
        .Cast<RhinoObject>()
        .ToList();
      foreach (var source in previewSources.Where(source => !IsPointMarker(source)))
      {
        DrawGeometry(
          display, source, mirror,
          flipText, changeText, textReplacements, selected: true);

        if (changedTitles.TryGetValue(source.Id, out var changedText))
          DrawChangedTitleFrame(
            display, source, mirror, flipText, changedText, PreviewSelectedColor);
      }
      foreach (var source in previewSources.Where(IsPointMarker))
      {
        DrawGeometry(
          display, source, mirror,
          flipText, changeText, textReplacements, selected: true);
      }
    }

    bool TryGetChangedPreviewTitle(
      RhinoObject? source,
      IReadOnlyList<TextReplacementRule> textReplacements,
      out string changedText)
    {
      changedText = string.Empty;
      if (source?.Geometry is not TextEntity text ||
          source.Attributes.GetUserString(vTitle.TitleFlagKey) !=
            vTitle.TitleFlagValue)
        return false;

      string original = text.PlainText ?? string.Empty;
      changedText = ApplyTextReplacements(original, textReplacements);
      return !string.Equals(changedText, original, StringComparison.Ordinal);
    }

    List<Guid> GetSelectedTitleFrameIds(RhinoObject titleSource)
    {
      var titleGroups = titleSource.Attributes.GetGroupList();
      if (titleGroups == null || titleGroups.Length == 0)
        return [];

      var groupSet = titleGroups.ToHashSet();
      var frameCandidates = new List<RhinoObject>();

      foreach (var objectId in _objectIds)
      {
        if (objectId == titleSource.Id)
          continue;

        var candidate = _doc.Objects.FindId(objectId);
        if (candidate?.Geometry is not PolylineCurve)
          continue;

        var candidateGroups = candidate.Attributes.GetGroupList();
        if (candidateGroups == null ||
            !candidateGroups.Any(groupSet.Contains))
          continue;

        frameCandidates.Add(candidate);
      }

      var taggedFrameIds = frameCandidates
        .Where(vTitle.IsTitleFrame)
        .Select(candidate => candidate.Id)
        .ToList();
      if (taggedFrameIds.Count > 0)
        return taggedFrameIds;

      return frameCandidates.Count == 1
        ? [frameCandidates[0].Id]
        : [];
    }

    static void DrawChangedTitleFrame(
      DisplayPipeline display,
      RhinoObject titleSource,
      Transform mirror,
      bool flipText,
      string changedText,
      Color color)
    {
      if (titleSource.Geometry is not TextEntity sourceText)
        return;

      // Ask vTitle to build the replacement frame from a FRESH TextEntity.
      // Do not mutate a duplicate's PlainText: Rhino can retain stale
      // annotation layout data on an in-memory duplicate until it is inserted
      // into the document, which is why preview and final frame sizes diverged.
      using var frame = vTitle.CreateFrameForTitlePreview(
        titleSource, sourceText, changedText);
      if (frame == null)
        return;

      // The flip transform depends on the title plane, not its string content.
      Transform applied = mirror;
      if (flipText)
        applied = TextFlipTransform(sourceText, mirror) * mirror;

      if (!frame.Transform(applied))
        return;

      PreviewDisplay.DrawCurve(
        display, frame, color, PreviewSelectedThicknessOffset);
    }

    void DrawGeometry(
      DisplayPipeline display,
      RhinoObject source,
      Transform transform,
      bool flipText,
      bool changeText,
      IReadOnlyList<TextReplacementRule> textReplacements,
      bool selected)
    {
      if (source is InstanceObject instance)
      {
        Transform instanceTransform = transform * instance.InstanceXform;
        display.DrawInstanceDefinitionShaded(
          instance.InstanceDefinition, selected ? _selectedMaterial : _ghostMaterial,
          instanceTransform);
        return;
      }

      GeometryBase? geometry = source.Geometry.Duplicate();
      if (geometry == null)
        return;
      using (geometry)
      {
        // Preview title text exactly as vMirror will create it.
        if (changeText &&
            geometry is TextEntity previewText &&
            source.Attributes.GetUserString(vTitle.TitleFlagKey) ==
              vTitle.TitleFlagValue)
        {
          string original = previewText.PlainText ?? string.Empty;
          string changed = ApplyTextReplacements(original, textReplacements);
          if (!string.Equals(changed, original, StringComparison.Ordinal))
            previewText.PlainText = changed;
        }

        Transform applied = transform;
        if (flipText && geometry is TextEntity sourceText)
          applied = TextFlipTransform(sourceText, transform) * transform;
        if (!geometry.Transform(applied))
          return;
        if (applied.Determinant < 0.0 &&
            !RestoreMirroredOrientation(geometry))
          return;

        Color color = selected ? PreviewSelectedColor : PreviewGhostColor;
        DisplayMaterial material = selected ? _selectedMaterial : _ghostMaterial;
        switch (geometry)
        {
          case Curve curve:
            PreviewDisplay.DrawCurve(
              display,
              curve,
              color,
              selected
                ? PreviewSelectedThicknessOffset
                : PreviewGhostThicknessOffset);
            break;
          case Brep brep:
            display.DrawBrepShaded(brep, material);
            PreviewDisplay.DrawBrepWires(
              display,
              brep,
              color,
              selected
                ? PreviewSelectedThicknessOffset
                : PreviewGhostThicknessOffset);
            break;
          case Extrusion extrusion:
            using (var brep = extrusion.ToBrep())
            {
              if (brep == null) break;
              display.DrawBrepShaded(brep, material);
              PreviewDisplay.DrawBrepWires(
                display,
                brep,
                color,
                selected
                  ? PreviewSelectedThicknessOffset
                  : PreviewGhostThicknessOffset);
            }
            break;
          case Surface surface:
            using (var brep = surface.ToBrep())
            {
              if (brep == null) break;
              display.DrawBrepShaded(brep, material);
              PreviewDisplay.DrawBrepWires(
                display,
                brep,
                color,
                selected
                  ? PreviewSelectedThicknessOffset
                  : PreviewGhostThicknessOffset);
            }
            break;
          case Mesh mesh:
            display.DrawMeshShaded(mesh, material);
            PreviewDisplay.DrawMeshWires(
              display,
              mesh,
              color,
              selected
                ? PreviewSelectedThicknessOffset
                : PreviewGhostThicknessOffset);
            break;
          case SubD subd:
            display.DrawSubDShaded(subd, material);
            display.DrawSubDWires(subd, color,
              PreviewDisplay.Thickness(
                display,
                selected
                  ? PreviewSelectedThicknessOffset
                  : PreviewGhostThicknessOffset));
            break;
          case TextEntity text:
            display.DrawAnnotation(text, color);
            break;
          case TextDot dot:
            display.PushDepthTesting(false);
            display.PushDepthWriting(false);
            try
            {
              display.DrawDot(dot, color, Color.Black, color);
            }
            finally
            {
              display.PopDepthWriting();
              display.PopDepthTesting();
            }
            break;
          case Rhino.Geometry.Point point:
            display.PushDepthTesting(false);
            display.PushDepthWriting(false);
            try
            {
              display.DrawPoint(
                point.Location,
                PreviewPointStyle,
                selected ? PreviewSelectedPointSize : PreviewGhostPointSize,
                color);
            }
            finally
            {
              display.PopDepthWriting();
              display.PopDepthTesting();
            }
            break;
          case Hatch hatch:
            display.DrawHatch(hatch, color, color);
            break;
          default:
            display.DrawObject(source, transform);
            break;
        }
      }
    }

    static bool IsPointMarker(RhinoObject source) =>
      source.Geometry is Rhino.Geometry.Point or TextDot;

    public void Dispose()
    {
      Enabled = false;
      _selectedMaterial.Dispose();
      _ghostMaterial.Dispose();
    }
  }
}
