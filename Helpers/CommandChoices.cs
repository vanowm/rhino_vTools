using System.Collections.Concurrent;
using System.Reflection;

namespace vTools;

/// <summary>Command-owned choices shared by native prompts and the defaults editor.</summary>
[AttributeUsage(AttributeTargets.Field)]
internal sealed class CommandChoicesAttribute(string section, string keys, bool indexed, params string[] names) : Attribute
{
  internal string Section { get; } = section;
  internal string Keys { get; } = keys;
  internal bool Indexed { get; } = indexed;
  internal string[] Names { get; } = names;
}

internal static class CommandChoices
{
  // Fixed metadata conventions, not user-configurable defaults.
  private const char KeySeparator = '|'; // Separates persisted keys that share the same native choice list.
  private static readonly ConcurrentDictionary<(Type Owner, string Field), string[]> Cache = new();
  private static readonly Lazy<Definition[]> Catalog = new(BuildDefinitions);
  internal sealed record Definition(string Section, string Key, bool Indexed, string[] Names);

  internal static string[] Values(Type owner, string field) => Cache.GetOrAdd((owner, field), key =>
    key.Owner.GetField(key.Field, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
      ?.GetCustomAttribute<CommandChoicesAttribute>()?.Names.ToArray()
    ?? throw new InvalidOperationException($"Missing named-choice metadata for {owner.FullName}.{field}."));

  internal static Definition[] All() => Catalog.Value
    .Select(entry => entry with { Names = entry.Names.ToArray() }).ToArray();

  private static Definition[] BuildDefinitions()
  {
    EmbeddedDependencies.EnsureAll();
    return typeof(CommandChoices).Assembly.GetTypes()
      .SelectMany(type => type.GetFields(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
      .Select(field => field.GetCustomAttribute<CommandChoicesAttribute>()).OfType<CommandChoicesAttribute>()
      .SelectMany(entry => entry.Keys.Split(KeySeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(key => new Definition(entry.Section, key, entry.Indexed, entry.Names.ToArray()))).ToArray();
  }
}
