using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using vTools.Commands;

namespace vTools;

/// <summary>Owned cleanup overlays shared by cleanup and export, with read-only edit tracking.</summary>
internal sealed class CleanupFindingDisplay : IDisposable
{
  // Shared finding appearance and dismissal defaults; legacy cleanup keys retain saved preferences.
  [CommandDefault("vCleanup", "vTools.Commands.vCleanup.ShortHighlightColor", "Shared ARGB color for short findings in cleanup and both exporters.", "#FFFF00FF")]
  internal static readonly Color ShortHighlightColor = CommandDefaults.GetColor("vCleanup", "vTools.Commands.vCleanup.ShortHighlightColor", Color.Magenta); // ARGB short-finding color shared by cleanup, DXF and PatternSmith export.
  [CommandDefault("vCleanup", "vTools.Commands.vCleanup.OverlapHighlightColor", "Shared ARGB color for overlap findings in cleanup and both exporters.", "#FF00FFFF")]
  private static readonly Color OverlapHighlightColor = CommandDefaults.GetColor("vCleanup", "vTools.Commands.vCleanup.OverlapHighlightColor", Color.Cyan); // ARGB overlap-finding color shared by all cleanup displays.
  [CommandDefault("vCleanup", "vTools.Commands.vCleanup.ShortHighlightThicknessPixels", "Shared colored finding stroke width in screen pixels; integer one or greater.")]
  private const int ShortHighlightThicknessPixels = 7; // Positive screen-pixel finding stroke width, independent of zoom.
  private static readonly int ConfiguredShortHighlightThicknessPixels = Math.Max(1, CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.ShortHighlightThicknessPixels", ShortHighlightThicknessPixels)); // Reuses saved cleanup width in all exporters.
  [CommandDefault("vCleanup", "vTools.Commands.vCleanup.CandidatePointSizePixels", "Shared control-point finding marker diameter in screen pixels; integer one or greater.")]
  private const int CandidatePointSizePixels = 10; // Positive screen-pixel diameter of a highlighted control-point candidate.
  private static readonly int ConfiguredCandidatePointSizePixels = Math.Max(1, CommandDefaults.Get("vCleanup", "vTools.Commands.vCleanup.CandidatePointSizePixels", CandidatePointSizePixels)); // Reuses saved cleanup point size in all exporters.
  [CommandDefault("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.HighlightOutlineColor", "Shared ARGB contrast outline around finding curves and screen markers.", "#FF000000")]
  private static readonly Color HighlightOutlineColor = CommandDefaults.GetColor("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.HighlightOutlineColor", Color.Black); // ARGB outline color shared by short and overlap findings.
  [CommandDefault("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.HighlightOutlineExtraPixels", "Extra outline width beyond the colored finding stroke or marker, in screen pixels; zero or greater.")]
  private const int HighlightOutlineExtraPixels = 2; // Nonnegative outline pixels added beyond each colored curve/marker diameter.
  private static readonly int ConfiguredHighlightOutlineExtraPixels = Math.Max(0, CommandDefaults.Get("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.HighlightOutlineExtraPixels", HighlightOutlineExtraPixels)); // Shared curve and point contrast border width.
  [CommandDefault("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.MinimumFindingMarkerPixels", "Minimum on-screen cue for findings too small to see as curves, in screen pixels; zero disables the extra cue.")]
  private const int MinimumFindingMarkerPixels = 12; // Nonnegative screen-pixel diameter; keeps subpixel findings visible at distant zoom levels.
  private static readonly int ConfiguredMinimumFindingMarkerPixels = Math.Max(0, CommandDefaults.Get("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.MinimumFindingMarkerPixels", MinimumFindingMarkerPixels)); // Shared screen-space visibility floor for all cleanup/export findings.
  private static readonly PreviewDisplay.ObjectHighlightStyle ShortFindingStyle = new(ShortHighlightColor, HighlightOutlineColor, Color.Transparent, 1)
  { StrokeEmphasis = ConfiguredShortHighlightThicknessPixels - 1, OutlineEmphasis = ConfiguredShortHighlightThicknessPixels - 1 + ConfiguredHighlightOutlineExtraPixels,
    PointSizePixels = Math.Max(ConfiguredCandidatePointSizePixels, ConfiguredMinimumFindingMarkerPixels), PointOutlineExtraPixels = ConfiguredHighlightOutlineExtraPixels,
    UseObjectCurveColor = false }; // Shared configured magenta short findings with a contrast border and fixed-pixel point cues.
  private static readonly PreviewDisplay.ObjectHighlightStyle OverlapFindingStyle = ShortFindingStyle with
  { BodyColor = OverlapHighlightColor }; // Same rendering as short findings, using the shared overlap color.
  [CommandDefault("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.EscapeIntervalMilliseconds", "Maximum positive milliseconds between consecutive Esc presses when dismissing cleanup/export highlights.")]
  private const int EscapeIntervalMilliseconds = 600; // Positive milliseconds allowed between consecutive presses in the triple-Esc gesture.
  private static readonly int ConfiguredEscapeIntervalMilliseconds = Math.Max(1,
    CommandDefaults.Get("CleanupFindingDisplay", "vTools.CleanupFindingDisplay.EscapeIntervalMilliseconds", EscapeIntervalMilliseconds)); // Resolved once from vSettings; restart Rhino after changes.
  private const int EscapePressCount = 3; // Exactly three consecutive Esc presses dismiss shared cleanup/export feedback.

  private static readonly HashSet<CleanupFindingDisplay> Displays = [];
  private static long _lastEscapeTick;
  private static int _escapeCount;
  private readonly PreviewDisplay.ObjectHighlighter _overlaps;
  private readonly PreviewDisplay.ObjectHighlighter _shorts;
  private readonly RhinoDoc _doc;
  private Dictionary<Guid, GeometryBase> _geometry = [];
  private Guid[] _overlapSources = [];
  private Guid[] _shortSources = [];
  private vCleanup.FindingTracker? _tracker;
  private bool _disposed;
  private bool _visible = true;
  internal bool HasFindings { get; private set; }
  internal event Action? Disposed;

  internal CleanupFindingDisplay(RhinoDoc doc)
  {
    _doc = doc;
    _overlaps = new(doc, OverlapFindingStyle) { DrawOnTop = true, MinimumCurveMarkerPixels = ConfiguredMinimumFindingMarkerPixels };
    _shorts = new(doc, ShortFindingStyle) { DrawOnTop = true, MinimumCurveMarkerPixels = ConfiguredMinimumFindingMarkerPixels };
    if (Displays.Count == 0) RhinoApp.EscapeKeyPressed += EscapePressed;
    Displays.Add(this);
  }

  internal static void ClearAll()
  {
    _escapeCount = 0;
    _lastEscapeTick = 0;
    foreach (var display in Displays.ToArray()) display.Dispose();
  }

  private static void EscapePressed(object? sender, EventArgs e)
  {
    try { HandleEscape(Environment.TickCount64); }
    catch (Exception ex) { Log.Write("CleanupFindingDisplay", $"highlight dismissal failed: {ex}"); }
  }

  internal static void HandleEscape(long tick)
  {
    if (Displays.Count == 0) return;
    _escapeCount = _escapeCount > 0 && tick >= _lastEscapeTick && tick - _lastEscapeTick <= ConfiguredEscapeIntervalMilliseconds
      ? _escapeCount + 1 : 1;
    _lastEscapeTick = tick;
    if (_escapeCount >= EscapePressCount) ClearAll();
  }

  internal void SetFindings(IEnumerable<Guid> overlapIds,
    IReadOnlyCollection<(Guid SourceId, Curve Curve, Point3d Point)> shortFindings)
  {
    if (_disposed) return;
    var geometry = new Dictionary<Guid, GeometryBase>();
    try
    {
      foreach (var finding in shortFindings)
      {
        geometry.Add(Guid.NewGuid(), finding.Curve.DuplicateCurve());
        if (finding.Point.IsValid) geometry.Add(Guid.NewGuid(), new Rhino.Geometry.Point(finding.Point));
      }
    }
    catch
    {
      foreach (var item in geometry.Values) item.Dispose();
      throw;
    }
    _overlapSources = overlapIds.Distinct().ToArray();
    _shortSources = shortFindings.Select(finding => finding.SourceId).Distinct().ToArray();
    HasFindings = _overlapSources.Length > 0 || geometry.Count > 0;
    bool overlapRedraw=_overlaps.RedrawOnChange,shortRedraw=_shorts.RedrawOnChange;
    _overlaps.RedrawOnChange=_shorts.RedrawOnChange=false;
    _overlaps.SetObjects(_overlapSources);
    _shorts.SetObjects(geometry.Keys, geometry);
    if(!_visible){_overlaps.Enabled=false;_shorts.Enabled=false;}
    _overlaps.RedrawOnChange=overlapRedraw;_shorts.RedrawOnChange=shortRedraw;
    foreach (var item in _geometry.Values) item.Dispose();
    _geometry = geometry;
    if(overlapRedraw||shortRedraw)_doc.Views.Redraw();
  }

  internal void SetVisible(bool visible)
  {
    if(_disposed||_visible==visible)return;
    _visible=visible;_overlaps.Enabled=visible&&_overlapSources.Length>0;_shorts.Enabled=visible&&_geometry.Count>0;
    _doc.Views.Redraw();
  }

  internal void Track(IEnumerable<Guid> scope, vCleanup.LiveFindingSettings settings)
  {
    if (_disposed) return;
    _tracker?.Dispose();
    _tracker = new vCleanup.FindingTracker(_doc, scope, _overlapSources, _shortSources,
      settings, SetFindings, Closed);
  }

  private void Closed()
  {
    _overlaps.RedrawOnChange = false;
    _shorts.RedrawOnChange = false;
    Dispose();
  }

  public void Dispose()
  {
    if (_disposed) return;
    _disposed = true;
    Displays.Remove(this);
    if (Displays.Count == 0)
    {
      RhinoApp.EscapeKeyPressed -= EscapePressed;
      _escapeCount = 0;
      _lastEscapeTick = 0;
    }
    _tracker?.Dispose();
    _overlaps.Dispose();
    _shorts.Dispose();
    foreach (var item in _geometry.Values) item.Dispose();
    _geometry.Clear();
    HasFindings = false;
    Disposed?.Invoke();
  }
}
