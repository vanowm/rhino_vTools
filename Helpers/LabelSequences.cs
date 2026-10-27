using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace vTools;

internal static class LabelSequences
{
  // Defaults and customizable constants
  private const int MenuValueCharacterLimit = 32; // Maximum displayed menu-value characters; actual label values remain unrestricted.
  private const string NumberHeading = "Numbers"; // Menu heading for plain decimal integer labels.
  private const string LetterHeading = "Letters"; // Menu heading for plain alphabetic labels.
  private const string MixedHeading = "Mixed"; // Menu heading for prefixed numeric or alphabetic labels.
  private const string OrientationMark = "."; // Trailing display-only marker when a numeric label reads as a different number after a half-turn.
  private static readonly Regex SuffixPattern = new(@"([A-Za-z]+|[0-9]+)$", RegexOptions.CultureInvariant); // Incrementable trailing ASCII letter or digit run; prefixes are preserved.

  internal sealed record Choice(string Description, string Latest, string Next);
  private sealed record Value(string Text, string Prefix, string Suffix, bool Numeric, BigInteger Rank, int Kind);

  internal static string Increment(string text, string emptyValue)
  {
    if (string.IsNullOrWhiteSpace(text)) return emptyValue;
    var value = Parse(NormalizeValue(text));
    if (value.Numeric)
      return value.Prefix + (value.Rank + BigInteger.One).ToString(CultureInfo.InvariantCulture)
        .PadLeft(value.Suffix.Length, '0');
    var rank = value.Rank + BigInteger.One;
    var letters = new List<char>();
    while (rank > BigInteger.Zero)
    {
      rank = BigInteger.DivRem(rank - BigInteger.One, 26, out var remainder);
      letters.Add((char)('A' + (int)remainder));
    }
    letters.Reverse();
    return value.Prefix + new string(letters.ToArray());
  }

  internal static List<Choice> FindNext(IEnumerable<string> labels)
  {
    return labels.Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => Parse(NormalizeValue(text)))
      .GroupBy(value => (value.Kind, Prefix: value.Prefix.ToUpperInvariant(), value.Numeric))
      .OrderBy(group => group.Key.Kind).ThenBy(group => group.Key.Prefix, StringComparer.Ordinal)
      .Select(group =>
      {
        var latest = group.OrderBy(value => value.Rank).ThenBy(value => value.Suffix.Length)
          .ThenBy(value => value.Text, StringComparer.Ordinal).Last();
        string next = Increment(latest.Text, latest.Text);
        string heading = latest.Kind == 0 ? NumberHeading : latest.Kind == 1 ? LetterHeading
          : $"{MixedHeading} ({Display(latest.Prefix)})";
        return new Choice($"{heading}: {Display(latest.Text)} -> {Display(next)}", latest.Text, next);
      }).ToList();
  }

  internal static string NormalizeValue(string text)
  {
    var value = text.Trim();
    if (value.Length > OrientationMark.Length && value.EndsWith(OrientationMark, StringComparison.Ordinal) &&
        value[..^OrientationMark.Length].All(ch => ch is >= '0' and <= '9'))
      return value[..^OrientationMark.Length];
    return value;
  }

  internal static string FormatDisplayLabel(string text)
  {
    if (text.Length == 0) return text;
    var rotated = new char[text.Length];
    for (var index = 0; index < text.Length; index++)
    {
      rotated[index] = text[text.Length - index - 1] switch
      {
        '0' => '0', '6' => '9', '8' => '8', '9' => '6', _ => '\0'
      };
      if (rotated[index] == '\0') return text;
    }
    // Multi-digit labels never start with zero, so that reading is not ambiguous.
    if (rotated.Length > 1 && rotated[0] == '0') return text;
    return new string(rotated) == text ? text : text + OrientationMark;
  }

  private static Value Parse(string text)
  {
    var match = SuffixPattern.Match(text);
    if (!match.Success) return new Value(text, text, string.Empty, true, BigInteger.Zero, 2);
    string prefix = text[..match.Index];
    string suffix = match.Value;
    bool numeric = suffix[0] is >= '0' and <= '9';
    var rank = BigInteger.Zero;
    if (numeric) rank = BigInteger.Parse(suffix, CultureInfo.InvariantCulture);
    else
      foreach (char letter in suffix.ToUpperInvariant()) rank = rank * 26 + letter - 'A' + 1;
    return new Value(text, prefix, suffix, numeric, rank, prefix.Length > 0 ? 2 : numeric ? 0 : 1);
  }

  private static string Display(string value)
  {
    value = value.Replace('\r', ' ').Replace('\n', ' ');
    return value.Length <= MenuValueCharacterLimit ? value : value[..MenuValueCharacterLimit] + "...";
  }
}
