using System.Reflection;
using System.Runtime.Loader;

namespace vTools;

/// <summary>Loads bundled managed libraries into the same context as the plug-in, without starting any command.</summary>
internal static class EmbeddedDependencies
{
  // Bundled dependency identities; fixed build resources, not user-configurable settings.
  private const string PackingName = "RectpackSharp"; // Exact assembly simple name of the bundled rectangle packer.
  private const string PackingResource = "vTools.Dependencies.RectpackSharp.dll"; // Manifest resource containing the bundled packer.
  private const string PolygonName = "Clipper2Lib"; // Exact assembly simple name of the bundled polygon engine.
  private const string PolygonResource = "vTools.Dependencies.Clipper2Lib.dll"; // Manifest resource containing the bundled polygon engine.
  private static readonly object Sync = new();

  internal static void EnsureAll() { EnsurePacking(); EnsurePolygons(); }
  internal static void EnsurePacking() => Ensure(PackingName, PackingResource);
  internal static void EnsurePolygons() => Ensure(PolygonName, PolygonResource);

  private static void Ensure(string name, string resourceName)
  {
    lock (Sync)
    {
      var owner = typeof(EmbeddedDependencies).Assembly;
      var context = AssemblyLoadContext.GetLoadContext(owner) ?? AssemblyLoadContext.Default;
      var expected = owner.GetReferencedAssemblies().FirstOrDefault(reference => reference.Name == name);
      if (context.Assemblies.Any(assembly => assembly.GetName().Name == name &&
        (expected == null || assembly.GetName().Version == expected.Version))) return;
      using var resource = owner.GetManifestResourceStream(resourceName)
        ?? throw new InvalidOperationException($"The bundled managed library '{name}' is missing from this vTools build.");
      var loaded = context.LoadFromStream(resource);
      if (loaded.GetName().Name != name || expected != null && loaded.GetName().Version != expected.Version)
        throw new InvalidOperationException($"The bundled managed library '{name}' has an unexpected identity.");
    }
  }
}
