using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using vTools.Commands;

namespace vTools;

/// <summary>
/// Samples active viewport rendering and renders a per-viewport FPS overlay.
/// </summary>
internal static class FpsDisplay
{
  private const string SettingsSection = "vFPS";
  private const string EnabledKey = "enabled";

  // Option defaults
  private const bool DefaultEnabled = false; // true shows the FPS overlay at startup; false keeps it hidden.

  private static FpsDisplayConduit? _conduit;
  private static bool _samplingRedraw;
  private static Guid _sampleViewportId;
  private static bool _settingsLoaded;
  private static bool _configuredEnabled = DefaultEnabled;

  internal static void Start()
  {
    EnsureSettingsLoaded();
    if (_configuredEnabled)
      EnableRuntime();
  }

  internal static bool Toggle()
  {
    EnsureSettingsLoaded();

    if (_conduit?.Enabled == true)
    {
      DisableRuntime();
      _configuredEnabled = false;
    }
    else
    {
      EnableRuntime();
      _configuredEnabled = true;
    }

    SaveEnabledSetting();
    return _configuredEnabled;
  }

  internal static void Stop() => DisableRuntime();

  private static void EnableRuntime()
  {
    _conduit ??= new FpsDisplayConduit();
    _conduit.Reset();
    _conduit.Enabled = true;
    _sampleViewportId = Guid.Empty;
    RhinoApp.Idle -= OnIdle;
    RhinoApp.Idle += OnIdle;
  }

  private static void DisableRuntime()
  {
    RhinoApp.Idle -= OnIdle;
    _samplingRedraw = false;
    _sampleViewportId = Guid.Empty;

    if (_conduit != null)
    {
      _conduit.Enabled = false;
      _conduit.Reset();
      _conduit = null;
    }
  }

  private static void EnsureSettingsLoaded()
  {
    if (_settingsLoaded)
      return;

    _configuredEnabled = ToolsOptionStore.Read(
      SettingsSection,
      section => ToolsOptionStore.TryGetBool(section, EnabledKey, out var enabled)
        ? enabled
        : DefaultEnabled);
    _settingsLoaded = true;
  }

  private static void SaveEnabledSetting()
  {
    if (ToolsOptionStore.Update(
          SettingsSection,
          section => section[EnabledKey] = _configuredEnabled))
    {
      return;
    }

    Log.Write("vFPS", $"could not save enabled state: {ToolsOptionStore.LastError}");
  }

  private static void OnIdle(object? sender, EventArgs e)
  {
    if (_conduit?.Enabled != true || _samplingRedraw)
      return;

    if (System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.None)
    {
      _sampleViewportId = Guid.Empty;
      return;
    }

    var view = RhinoDoc.ActiveDoc?.Views.ActiveView;
    if (view == null)
      return;

    var viewportId = view.ActiveViewport.Id;
    if (_sampleViewportId != viewportId)
    {
      _sampleViewportId = viewportId;
      _conduit.BeginSample(viewportId);
    }

    try
    {
      _samplingRedraw = true;
      view.Redraw();
    }
    catch (Exception ex)
    {
      Log.Write("vFPS", $"sampling redraw failed: {ex.Message}");
      RhinoApp.Idle -= OnIdle;
    }
    finally
    {
      _samplingRedraw = false;
    }
  }

  private sealed class FpsDisplayConduit : DisplayConduit
  {
    private const double SampleWindowSeconds = 0.5; // Rolling FPS measurement window in seconds; greater than zero.
    private const double RefreshSeconds = 0.2; // Minimum overlay refresh interval in seconds; greater than zero.
    private const double NaturalFrameGapSeconds = 0.1; // Largest render gap treated as continuous motion, in seconds.
    private const int FontSize = 12; // FPS label font height in display pixels; greater than zero.
    private const double TopBaseline = 6.0; // Label offset below the viewport title, in display pixels.
    private const double RightMargin = 6.0; // Label inset from the right viewport edge, in display pixels.
    private const string MaximumLabel = "999"; // Widest reserved three-character FPS label.
    private const string FontFace = "Consolas"; // Installed font family used for the stationary-width FPS label.

    private readonly Dictionary<Guid, ViewFrameRate> _viewRates = new();
    private bool _drawFailureLogged;

    internal void Reset()
    {
      _viewRates.Clear();
      _drawFailureLogged = false;
    }

    internal void BeginSample(Guid viewportId)
    {
      if (_viewRates.TryGetValue(viewportId, out var rate))
        rate.BeginSample();
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
      try
      {
        var viewId = e.Viewport.Id;
        if (!_viewRates.TryGetValue(viewId, out var rate))
        {
          rate = new ViewFrameRate();
          _viewRates.Add(viewId, rate);
        }

        var now = Stopwatch.GetTimestamp();
        var activelySampling = _samplingRedraw ||
                               System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None;
        var fps = rate.RecordFrame(now, activelySampling);
        var roundedFps = fps.HasValue
          ? Math.Clamp((int)Math.Round(fps.Value, MidpointRounding.AwayFromZero), 0, 999)
          : 0;
        var label = fps.HasValue ? $"{roundedFps,3}" : "--";
        DrawLabel(e, label);
      }
      catch (Exception ex)
      {
        if (!_drawFailureLogged)
        {
          _drawFailureLogged = true;
          Log.Write("vFPS", $"draw failed: {ex.Message}");
        }
      }
    }

    private static void DrawLabel(DrawEventArgs e, string label)
    {
      var measured = e.Display.Measure2dText(
        MaximumLabel,
        new Point2d(0, 0),
        false,
        0.0,
        FontSize,
        FontFace);
      var x = Math.Max(RightMargin, e.Viewport.Size.Width - measured.Width - RightMargin);
      var textPosition = new Point2d(x, TopBaseline);

      for (var dx = -1; dx <= 1; dx++)
      {
        for (var dy = -1; dy <= 1; dy++)
        {
          if (dx == 0 && dy == 0)
            continue;

          var position = new Point2d(textPosition.X + dx, textPosition.Y + dy);
          e.Display.Draw2dText(label, Color.Black, position, false, FontSize, FontFace);
        }
      }

      e.Display.Draw2dText(label, Color.White, textPosition, false, FontSize, FontFace);
    }

    private sealed class ViewFrameRate
    {
      private readonly Queue<long> _frames = new();
      private long _lastObservedFrame;
      private long _lastDisplayUpdate;
      private double? _displayedRate;
      private bool _naturalSequence;

      internal void BeginSample()
      {
        _frames.Clear();
        _lastObservedFrame = 0;
        _lastDisplayUpdate = 0;
        _naturalSequence = false;
      }

      internal double? RecordFrame(long now, bool activelySampling)
      {
        var sampleTicks = (long)(SampleWindowSeconds * Stopwatch.Frequency);
        if (!activelySampling)
        {
          var previousFrame = _lastObservedFrame;
          _lastObservedFrame = now;
          var naturalGapTicks = (long)(NaturalFrameGapSeconds * Stopwatch.Frequency);
          if (previousFrame == 0 || now - previousFrame > naturalGapTicks)
          {
            _naturalSequence = false;
            return _displayedRate;
          }

          if (!_naturalSequence)
          {
            _frames.Clear();
            _frames.Enqueue(previousFrame);
            _lastDisplayUpdate = 0;
            _naturalSequence = true;
          }
        }
        else
        {
          _lastObservedFrame = now;
          _naturalSequence = true;
        }

        while (_frames.Count > 0 && now - _frames.Peek() > sampleTicks)
          _frames.Dequeue();

        _frames.Enqueue(now);

        if (_frames.Count < 2)
          return _displayedRate;

        var refreshTicks = (long)(RefreshSeconds * Stopwatch.Frequency);
        if (_displayedRate.HasValue && now - _lastDisplayUpdate < refreshTicks)
          return _displayedRate;

        var elapsed = (now - _frames.Peek()) / (double)Stopwatch.Frequency;
        _displayedRate = elapsed > 0.0 ? (_frames.Count - 1) / elapsed : null;
        _lastDisplayUpdate = now;
        return _displayedRate;
      }
    }
  }
}
