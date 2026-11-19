using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace vTools.Commands;

/// <summary>
/// Shared persisted option storage backed by vTools.config.json.
/// </summary>
internal static class ToolsOptionStore
{
  private const string ToolsConfigFileName = "vTools.config.json"; // Relative shared configuration filename beside the DLL.
  private static readonly object Sync = new();

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver()
  };

  /// <summary>
  /// Reads one command section and converts it to the requested value.
  /// </summary>
  internal static T Read<T>(string sectionName, Func<JsonObject?, T> reader)
  {
    lock (Sync)
    {
      try
      {
        LastError = "";
        var root = LoadRoot();
        var section = root[sectionName] as JsonObject;
        return reader(section);
      }
      catch (Exception ex)
      {
        LastError = ex.Message;
        return reader(null);
      }
    }
  }

internal static string LastError { get; private set; } = "";

  internal static bool TrySnapshot(out JsonObject root)
  {
    lock (Sync)
    {
      try { LastError = ""; root = LoadRoot(); return true; }
      catch (Exception ex) { LastError = ex.Message; root = new JsonObject(); return false; }
    }
  }

  internal sealed record Change(string[] Path, bool Existed, JsonNode? Before, JsonNode? After);

  internal static bool SameValue(JsonNode? left, JsonNode? right)
  {
    if (ReferenceEquals(left, right) || left?.ToJsonString() == right?.ToJsonString()) return true;
    if (left is JsonValue && right is JsonValue &&
      decimal.TryParse(left.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var first) &&
      decimal.TryParse(right.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var second)) return first == second;
    if (left is JsonObject a && right is JsonObject b)
      return a.Count == b.Count && a.All(pair => b.ContainsKey(pair.Key) && SameValue(pair.Value, b[pair.Key]));
    if (left is JsonArray x && right is JsonArray y)
      return x.Count == y.Count && x.Zip(y).All(pair => SameValue(pair.First, pair.Second));
    return false;
  }

  internal static JsonNode? Clone(JsonNode? value) => value == null ? null : JsonNode.Parse(value.ToJsonString());

  // Apply only edited leaves to the latest profile, never a stale full-document snapshot.
  internal static bool ApplyChanges(IReadOnlyList<Change> changes)
  {
    lock (Sync)
    {
      try
      {
        LastError = "";
        var root = LoadRoot();
        foreach (var change in changes)
        {
          if (change.Path.Length < 2) throw new InvalidDataException("A setting requires a section and key.");
          JsonObject? parent = root;
          foreach (var segment in change.Path[..^1])
          {
            if (parent == null) break;
            if (parent[segment] is JsonObject child) parent = child;
            else if (!parent.ContainsKey(segment)) parent = null;
            else throw new InvalidDataException($"Setting structure changed: {string.Join(" / ", change.Path)}. Reopen vSettings.");
          }
          bool exists = parent?.ContainsKey(change.Path[^1]) == true;
          var current = parent?[change.Path[^1]];
          if ((exists != change.Existed || !SameValue(current, change.Before)) && !SameValue(current, change.After))
            throw new InvalidDataException($"Setting changed since opening: {string.Join(" / ", change.Path)}. Reopen vSettings.");
        }
        foreach (var change in changes)
        {
          var parent = root;
          foreach (var segment in change.Path[..^1])
          {
            if (parent[segment] is not JsonObject child) parent[segment] = child = new JsonObject();
            parent = child;
          }
          parent[change.Path[^1]] = Clone(change.After);
        }
        if (changes.Count == 0) return true;
        if (!SaveRoot(root)) return false;
        CommandDefaults.Reload();
        return true;
      }
      catch (Exception ex) { LastError = ex.Message; return false; }
    }
  }

  /// <summary>
  /// Updates one command section and saves the shared config.
  /// </summary>
internal static bool Update(string sectionName, Action<JsonObject> updater)
{
  lock (Sync)
  {
    try
    {
      LastError = "";

      var root = LoadRoot();

      var section = root[sectionName] as JsonObject;
      var previous = section?.ToJsonString();
      if (section == null)
      {
        section = new JsonObject();
        root[sectionName] = section;
      }

      updater(section);
      if (previous != null && string.Equals(previous, section.ToJsonString(), StringComparison.Ordinal))
        return true;
      return SaveRoot(root);
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      return false;
    }
  }
}
  /// <summary>
  /// Attempts to read a string key from a command section.
  /// </summary>
  internal static bool TryGetString(JsonObject? section, string key, out string value)
  {
    value = string.Empty;

    try
    {
      if (section?[key] is not JsonValue jsonValue)
        return false;

      if (jsonValue.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
      {
        value = s.Trim();
        return true;
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Attempts to read a bool key from a command section.
  /// </summary>
  internal static bool TryGetBool(JsonObject? section, string key, out bool value)
  {
    value = false;

    try
    {
      if (section?[key] is not JsonValue jsonValue)
        return false;

      if (jsonValue.TryGetValue<bool>(out var b))
      {
        value = b;
        return true;
      }

      if (jsonValue.TryGetValue<int>(out var i))
      {
        value = i != 0;
        return true;
      }

      if (jsonValue.TryGetValue<string>(out var s) && bool.TryParse(s, out b))
      {
        value = b;
        return true;
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Attempts to read a double key from a command section.
  /// </summary>
  internal static bool TryGetDouble(JsonObject? section, string key, out double value)
  {
    value = 0.0;

    try
    {
      if (section?[key] is not JsonValue jsonValue)
        return false;

      if (jsonValue.TryGetValue<double>(out var d))
      {
        value = d;
        return true;
      }

      if (jsonValue.TryGetValue<int>(out var i))
      {
        value = i;
        return true;
      }

      if (jsonValue.TryGetValue<string>(out var s))
      {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ||
            double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
        {
          value = d;
          return true;
        }
      }
    }
    catch
    {
    }

    return false;
  }

  /// <summary>
  /// Loads root config object from vTools.config.json.
  /// </summary>
  private static JsonObject LoadRoot()
  {
    var path = GetToolsConfigPath();
    if (!File.Exists(path))
      return new JsonObject();

    var json = File.ReadAllText(path);
    if (string.IsNullOrWhiteSpace(json))
      throw new InvalidDataException($"Config file is empty: {path}");

    var node = JsonNode.Parse(
      json,
      nodeOptions: null,
      documentOptions: new JsonDocumentOptions
      {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
      });

    return node as JsonObject ??
      throw new InvalidDataException($"Config root must be a JSON object: {path}");
  }

  /// <summary>
  /// Saves root config object to vTools.config.json atomically.
  /// </summary>
  private static bool SaveRoot(JsonObject root)
  {
    try
    {
      var path = GetToolsConfigPath();
      var parent = Path.GetDirectoryName(path);
      if (!string.IsNullOrWhiteSpace(parent))
        Directory.CreateDirectory(parent);

      var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
      try
      {
        File.WriteAllText(tmp, root.ToJsonString(JsonOptions));
        File.Move(tmp, path, true);
      }
      finally
      {
        if (File.Exists(tmp)) File.Delete(tmp);
      }
      return true;
    }
    catch (Exception ex)
    {
      LastError = ex.Message;
      return false;
    }
}

  /// <summary>
  /// Resolves config path next to the plug-in DLL.
  /// </summary>
  private static string GetToolsConfigPath()
  {
    return PluginPaths.ResolveFile(ToolsConfigFileName);
  }
}
