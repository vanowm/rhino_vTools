using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace vTools.Commands;

public sealed partial class vExportDXF
{
  // Text layout measurement defaults
  private const double TextBreakSmallCapsScale = 1.0; // Unitless glyph scale; 1 keeps the source character sizes.
  private const double TextBreakGlyphSpacing = 0.0; // Additional model-space glyph spacing; 0 preserves native kerning.
  private const double TextBreakLayoutTolerance = 1e-6; // Relative glyph-position tolerance when checking that a row was not wrapped or reformatted.

  private static Dictionary<Guid, List<TextEntity>> BuildTextBreakOverrides(
    RhinoDoc doc, IReadOnlyList<RhinoObject> targets, bool enabled)
  {
    var overrides = new Dictionary<Guid, List<TextEntity>>();
    if (!enabled)
      return overrides;
    try
    {
      foreach (var source in targets)
      {
        if (source.Geometry is not TextEntity text ||
            text.PlainText.IndexOfAny(['\r', '\n']) < 0)
          continue;
        overrides.Add(source.Id, SplitExportText(doc, text));
      }
      return overrides;
    }
    catch
    {
      DisposeTextBreakOverrides(overrides);
      throw;
    }
  }

  private static List<TextEntity> SplitExportText(RhinoDoc doc, TextEntity source)
  {
    var result = new List<TextEntity>();
    List<Curve[]>? sourceGlyphs = null;
    try
    {
      // RichEdit extracts complete per-row RTF, including fonts and inline formatting.
      using var editor = new RichTextBox();
      if (source.TextHasRtfFormatting)
        editor.Rtf = source.RichText;
      else
        editor.Text = NormalizeTextBreaks(source.PlainText);
      string content = editor.Text;
      if (NormalizeTextBreaks(content) != NormalizeTextBreaks(source.PlainText))
        throw new InvalidOperationException("TextBreak could not preserve this text's formatting. Use TextBreak=No to export it unchanged.");

      using var measurement = source.Duplicate() as TextEntity
        ?? throw new InvalidOperationException("Could not copy multiline text for export.");
      measurement.ParentDimensionStyle = source.ParentDimensionStyle;
      measurement.DimensionScale = AnnotationTextTransform.ResolveDisplayDimensionScale(
        doc, source, doc.Views.ActiveView?.ActiveViewport);
      using var style = measurement.DimensionStyle;
      sourceGlyphs = measurement.CreateCurvesGrouped(
        style, true, TextBreakSmallCapsScale, TextBreakGlyphSpacing);
      int glyphIndex = 0;
      int start = 0;
      while (start < content.Length)
      {
        int end = content.IndexOf('\n', start);
        if (end < 0) end = content.Length;
        int length = end - start;
        if (length > 0 && content[end - 1] == '\r') length--;
        if (!string.IsNullOrWhiteSpace(content.Substring(start, length)))
        {
          editor.Select(start, length);
          var line = measurement.Duplicate() as TextEntity
            ?? throw new InvalidOperationException("Could not copy a text row for export.");
          result.Add(line);
          line.ParentDimensionStyle = measurement.ParentDimensionStyle;
          if (source.TextHasRtfFormatting)
            line.SetRichText(editor.SelectedRtf, style);
          else
            line.PlainText = content.Substring(start, length);
          line.TextIsWrapped = false;
          var glyphs = line.CreateCurvesGrouped(
            style, true, TextBreakSmallCapsScale, TextBreakGlyphSpacing);
          try
          {
            if (glyphs.Count == 0 || glyphIndex + glyphs.Count > sourceGlyphs.Count)
              throw new InvalidOperationException("TextBreak could not measure a text row. Use TextBreak=No to export it unchanged.");
            // Matching native glyphs gives the exact baseline, scale and justification.
            var from = TextGlyphBounds(glyphs[0]);
            var to = TextGlyphBounds(sourceGlyphs[glyphIndex]);
            var translation = to.Center - from.Center;
            double tolerance = TextBreakLayoutTolerance * Math.Max(1.0, source.TextHeight * measurement.DimensionScale);
            for (int index = 0; index < glyphs.Count; index++)
            {
              var actual = TextGlyphBounds(glyphs[index]);
              var expected = TextGlyphBounds(sourceGlyphs[glyphIndex + index]);
              if (!actual.IsValid || !expected.IsValid ||
                  (actual.Min + translation).DistanceTo(expected.Min) > tolerance ||
                  (actual.Max + translation).DistanceTo(expected.Max) > tolerance)
                throw new InvalidOperationException("TextBreak could not preserve a wrapped or formatted row's placement. Use TextBreak=No to export it unchanged.");
            }
            var plane = line.Plane;
            plane.Origin += translation;
            line.Plane = plane;
            glyphIndex += glyphs.Count;
          }
          finally
          {
            DisposeTextGlyphs(glyphs);
          }
        }
        start = end + 1;
      }
      if (glyphIndex != sourceGlyphs.Count)
        throw new InvalidOperationException("TextBreak could not preserve all text rows. Use TextBreak=No to export it unchanged.");
      return result;
    }
    catch
    {
      foreach (var line in result) line.Dispose();
      throw;
    }
    finally
    {
      if (sourceGlyphs != null) DisposeTextGlyphs(sourceGlyphs);
    }
  }

  private static string NormalizeTextBreaks(string text) =>
    text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');

  private static BoundingBox TextGlyphBounds(IEnumerable<Curve> glyph)
  {
    var bounds = BoundingBox.Empty;
    foreach (var curve in glyph) bounds.Union(curve.GetBoundingBox(true));
    return bounds;
  }

  private static void DisposeTextGlyphs(IEnumerable<Curve[]> glyphs)
  {
    foreach (var glyph in glyphs)
      foreach (var curve in glyph) curve.Dispose();
  }

  private static void DisposeTextBreakOverrides(
    IReadOnlyDictionary<Guid, List<TextEntity>> overrides)
  {
    foreach (var lines in overrides.Values)
      foreach (var line in lines) line.Dispose();
  }

  private static IEnumerable<(GeometryBase Geometry, ObjectAttributes Attributes)> PreparedExportPieces(
    RhinoObject source, IReadOnlyDictionary<Guid, List<ExportCurvePiece>> notchTrimOverrides,
    IReadOnlyDictionary<Guid, List<TextEntity>> textBreakOverrides)
  {
    if (textBreakOverrides.TryGetValue(source.Id, out var lines))
    {
      foreach (var line in lines)
        yield return (line, source.Attributes);
    }
    else
    {
      foreach (var piece in ExportPieces(source, notchTrimOverrides))
        yield return piece;
    }
  }
}
