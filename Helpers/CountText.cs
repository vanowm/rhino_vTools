namespace vTools;

internal static class CountText
{
  // Only one uses the singular form; zero and larger counts use the plural.
  internal static string Form(long count, string singular, string? plural = null) =>
    count == 1 ? singular : plural ?? singular + "s";

  internal static string Format(long count, string singular, string? plural = null) =>
    $"{count} {Form(count, singular, plural)}";
}
