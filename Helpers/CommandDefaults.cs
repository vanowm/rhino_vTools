using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using vTools.Commands;

namespace vTools;

/// <summary>Marks an editable factory value without changing its compile-time identity.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class CommandDefaultAttribute(string section, string key, string description, string? factoryColor = null) : Attribute
{
  internal string Section { get; } = section;
  internal string Key { get; } = key;
  internal string Description { get; } = description;
  internal string? FactoryColor { get; } = factoryColor;
  public string? OptionKeys { get; set; } // Pipe-separated saved option keys using this factory and explanation; null means advanced-only metadata.
  public string? OptionText { get; set; } // Explicit saved choice name when its persisted string encoding differs from the advanced constant's type; null uses the constant.
}

/// <summary>Cached advanced overrides shared by command implementations and vSettings.</summary>
internal static class CommandDefaults
{
  // Defaults and customizable constants
  internal const string Section = "CommandDefaults"; // JSON section containing advanced overrides, grouped by command/service.
  private const string DefaultsResource = "vTools.Configuration.Defaults.json"; // Embedded repository defaults, never a user's installed profile.
  private sealed class Cache
  {
    internal readonly Lazy<JsonObject> Overrides = new(() => ToolsOptionStore.Read(Section,
      value => ToolsOptionStore.Clone(value) as JsonObject ?? new JsonObject()));
    internal readonly ConcurrentDictionary<(Type Type, string Section, string Key), object> Values = new();
  }
  private static Cache _cache = new();

  internal sealed record Entry(string Section, string Key, string Description, Type Type, object? Factory, string? OptionKeys = null, string? OptionText = null)
  {
    internal bool DescribesOption(string key) => OptionKeys?.Split('|', StringSplitOptions.RemoveEmptyEntries).Contains(key, StringComparer.Ordinal) == true;
    internal JsonNode? FactoryNode => Factory == null || Factory is double d && !double.IsFinite(d)
      || Factory is float f && !float.IsFinite(f) ? null
      : Type == typeof(System.Drawing.Color) ? JsonValue.Create((string)Factory)
      : Type.IsEnum ? JsonValue.Create(Enum.ToObject(Type, Factory).ToString())
      : JsonSerializer.SerializeToNode(Factory);
  }

  internal static Entry[] Entries()
  {
    // GetTypes also resolves base types in command-private packing types, even before vNest has run.
    EmbeddedDependencies.EnsureAll();
    return typeof(CommandDefaults).Assembly.GetTypes()
    .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
    .Where(field => field.GetCustomAttribute<CommandDefaultAttribute>() is { } metadata && (field.IsLiteral || metadata.FactoryColor != null))
    .Select(field =>
    {
      var metadata = field.GetCustomAttribute<CommandDefaultAttribute>()!;
      return new Entry(metadata.Section, metadata.Key, metadata.Description, field.FieldType,
        metadata.FactoryColor != null ? metadata.FactoryColor : field.GetRawConstantValue(), metadata.OptionKeys, metadata.OptionText);
    }).OrderBy(entry => entry.Section, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Key).ToArray();
  }

  internal static JsonObject FactoryOptions()
  {
    using var stream = typeof(CommandDefaults).Assembly.GetManifestResourceStream(DefaultsResource)
      ?? throw new InvalidDataException("The built-in command defaults resource is missing.");
    var options = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("Invalid built-in command defaults.");
    var choices = CommandChoices.All();
    foreach (var entry in Entries().Where(entry => entry.OptionKeys != null))
    {
      if (options[entry.Section] is not JsonObject section) options[entry.Section] = section = new JsonObject();
      foreach (string key in entry.OptionKeys!.Split('|', StringSplitOptions.RemoveEmptyEntries))
      {
        var previous = section[key];
        if (previous is JsonArray or JsonObject) continue; // Structured output recipes must never be replaced by a similarly named scalar preference.
        var value = entry.OptionText != null ? JsonValue.Create(entry.OptionText) : entry.FactoryNode;
        var definition = choices.FirstOrDefault(choice => choice.Section == entry.Section && choice.Key == key);
        if (entry.Type == typeof(int) && definition is { Indexed: false } && entry.Factory is int index && index >= 0 && index < definition.Names.Length)
          value = JsonValue.Create(definition.Names[index]);
        else if (entry.Type.IsEnum && (definition?.Indexed == true || previous is JsonValue number && number.TryGetValue<double>(out _)))
          value = JsonValue.Create(Convert.ToInt32(entry.Factory, System.Globalization.CultureInfo.InvariantCulture));
        section[key] = ToolsOptionStore.Clone(value);
      }
    }
    return options;
  }

  internal static (double, double) Bounds(Entry entry)
  {
    double minimum = entry.Type == typeof(int) ? int.MinValue : -double.MaxValue;
    double maximum = entry.Type == typeof(int) ? int.MaxValue : double.MaxValue;
    string description = entry.Description.ToLowerInvariant();
    if (description.Contains("zero or greater") || description.Contains("zero or more") || description.Contains("non-negative") ||
      description.Contains("zero through") || description.Contains("0 through") || description.Contains("from 0") || description.Contains("from zero")) minimum = 0;
    if (description.Contains("positive") || description.Contains("greater than zero") || description.Contains("greater than 0") ||
      description.Contains("one or greater")) minimum = entry.Type == typeof(int) ? 1 : double.Epsilon;
    if (description.Contains("two or greater") || description.Contains("2 or greater")) minimum = 2;
    if (description.Contains("zero to one") || description.Contains("zero through one") || Regex.IsMatch(description, @"\b0 through 1\b") ||
      description.Contains("0..1") || Regex.IsMatch(description, @"\b0-1\b") || description.Contains("from 0 (opaque) to 1") ||
      description.Contains("less than one") || Regex.IsMatch(description, @"less than 1\b")) { minimum = Math.Max(0, minimum); maximum = 1; }
    var range = Regex.Match(description, @"(?:from\s+|between\s+|range\s+is\s+)(-?\d+(?:\.\d+)?)\s+(?:through|to|and)\s+(-?\d+(?:\.\d+)?)");
    if (range.Success && double.TryParse(range.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double low) &&
      double.TryParse(range.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double high) && low <= high)
    { minimum = low; maximum = high; }
    return (minimum, maximum);
  }

  internal static bool Valid(Entry entry, JsonNode? value, out string error)
  {
    error = "";
    if (value == null) return true; // null removes an advanced override and uses its built-in factory value.
    try
    {
      if (entry.Type == typeof(System.Drawing.Color))
      {
        if (value is not JsonValue color || !color.TryGetValue<string>(out var text) || !Regex.IsMatch(text, "^#[0-9A-Fa-f]{8}$"))
          throw new FormatException("Color must be #AARRGGBB.");
        return true;
      }
      if (entry.Type.IsEnum)
      {
        if (value is not JsonValue item || !item.TryGetValue<string>(out var name) ||
          !Enum.TryParse(entry.Type, name, true, out var parsed) || parsed == null || !Enum.IsDefined(entry.Type, parsed))
          throw new FormatException("Choose a defined option.");
        return true;
      }
      var result = value.Deserialize(entry.Type);
      if (entry.Type == typeof(int) || entry.Type == typeof(double) || entry.Type == typeof(float))
      {
        double number = Convert.ToDouble(result, System.Globalization.CultureInfo.InvariantCulture);
        var (minimum, maximum) = Bounds(entry);
        if (!double.IsFinite(number) || number < minimum || number > maximum)
          throw new FormatException($"Value must be finite and between {minimum:G6} and {maximum:G6}.");
      }
      return true;
    }
    catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException)
    { error = ex.Message; return false; }
  }

  internal static void Reload()
  {
    Interlocked.Exchange(ref _cache, new Cache());
  }

  internal static System.Drawing.Color GetColor(string section, string key, System.Drawing.Color factory)
  {
    string original = $"#{factory.ToArgb():X8}";
    string value = Get(section, key, original);
    if (string.Equals(value, original, StringComparison.OrdinalIgnoreCase)) return factory;
    return value.Length == 9 && value[0] == '#' && uint.TryParse(value[1..],
      System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint argb)
      ? System.Drawing.Color.FromArgb(unchecked((int)argb)) : factory;
  }

  internal static T Get<T>(string section, string key, T factory)
  {
    var cache = Volatile.Read(ref _cache);
    return (T)cache.Values.GetOrAdd((typeof(T), section, key), _ => Read(cache.Overrides.Value, section, key, factory)!);
  }

  private static T Read<T>(JsonObject values, string section, string key, T factory)
  {
    if (values[section] is not JsonObject command || command[key] is not JsonValue value) return factory;
    try
    {
      if (typeof(T).IsEnum)
      {
        string? text = value.TryGetValue<string>(out var name) ? name : value.ToJsonString();
        return Enum.TryParse(typeof(T), text, true, out var parsed) && parsed != null && Enum.IsDefined(typeof(T), parsed)
          ? (T)parsed : factory;
      }
      var result = value.Deserialize<T>();
      if (result is double d && !double.IsFinite(d) || result is float f && !float.IsFinite(f)) return factory;
      return result is null ? factory : result;
    }
    catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException)
    {
      return factory;
    }
  }
}
