// ============================================================================
// AssemblyLoadContext extension module loader — port of core/extensions/loader.ts (4d-3)
// ============================================================================
//
// Decision D2 (docs/4d-extension-system-plan.md): the TS loader compiles extension modules with
// jiti at runtime; the .NET port loads them as assemblies. This file lands the real
// IExtensionModuleLoader behind the seam 4d-2b left open (difference C97):
//
//   TS                                | C#
//   ----------------------------------|------------------------------------------------------
//   import(extensionPath)             | collectible AssemblyLoadContext.LoadFromAssemblyPath
//   module.default (a function)       | the assembly's single IExtensionEntry implementation
//   import throws                     | load exception → loader wraps as "Failed to load extension: …"
//   default is not a function         | no entry → null → "does not export a valid factory function: …"
//
// Entry contract: an extension assembly exposes exactly one public concrete type implementing
// IExtensionEntry; its Factory property is the counterpart of the TS default export. Plugins
// reference Pi.CodingAgent and implement the interface, so the contract types stay in the default
// context and keep a single identity across contexts.
//
// Hot reload: every loaded path gets its own collectible context. Unload(path) releases it so a
// changed assembly can be loaded again; the loader-level module cache (ExtensionLoader) still
// decides when a re-import happens — ClearExtensionCache() drops the cached factories.

using System.Reflection;
using System.Runtime.Loader;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// Extension assembly entry contract — the .NET counterpart of a TS module's default export.
/// An extension assembly contains exactly one public concrete implementation of this interface;
/// its <see cref="Factory"/> is used as the <see cref="ExtensionFactory"/>.
/// </summary>
public interface IExtensionEntry
{
    /// <summary>The extension factory. TS <c>export default</c>.</summary>
    ExtensionFactory Factory { get; }
}

/// <summary>
/// Collectible context for one extension assembly. <see cref="Load"/> returns null so dependencies
/// resolve with the default ALC behaviour: the plugin directory first (private copies travel with
/// the plugin), then the default context (host contract assemblies such as Pi.CodingAgent, keeping
/// <see cref="IExtensionEntry"/> / <see cref="ExtensionFactory"/> identity single).
/// </summary>
public sealed class ExtensionAssemblyLoadContext : AssemblyLoadContext
{
    public ExtensionAssemblyLoadContext(string name) : base(name, isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}

/// <summary>
/// Loads extension modules as .NET assemblies (decision D2). Port of the TS jiti import path of
/// <c>core/extensions/loader.ts</c>.
/// </summary>
/// <remarks>
/// Difference C102: the TS import error text ("Cannot find module '…'") comes from Node's ESM
/// loader; the port raises <see cref="FileNotFoundException"/> with the same "Cannot find …"
/// shape. The loader-level wrapper ("Failed to load extension: …") is unchanged.
/// </remarks>
public sealed class AssemblyLoadContextExtensionLoader : IExtensionModuleLoader
{
    private readonly object gate = new();
    private readonly Dictionary<string, ExtensionAssemblyLoadContext> contexts = new(StringComparer.Ordinal);

    /// <summary>
    /// Load the assembly at <paramref name="extensionPath"/> and return the factory its
    /// <see cref="IExtensionEntry"/> exposes, or null when the assembly exports none (the TS
    /// "does not export a valid factory function" case).
    /// </summary>
    public ValueTask<ExtensionFactory?> LoadAsync(string extensionPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(extensionPath);
        if (!File.Exists(fullPath))
        {
            // TS: import() rejects with ERR_MODULE_NOT_FOUND ("Cannot find module '…'").
            throw new FileNotFoundException($"Cannot find extension module '{extensionPath}'.", fullPath);
        }

        Assembly assembly;
        lock (gate)
        {
            var context = GetOrCreateContext(fullPath);
            try
            {
                assembly = context.LoadFromAssemblyPath(fullPath);
            }
            catch (Exception error)
            {
                // A broken assembly must not poison the context: drop it so a retry (fixed file)
                // starts clean, matching TS where a failed import is not cached.
                contexts.Remove(fullPath);
                context.Unload();
                throw new InvalidOperationException(
                    $"Could not load extension assembly '{extensionPath}': {error.Message}", error);
            }
        }

        var factory = FindEntryFactory(GetLoadableTypes(assembly), extensionPath);
        return new ValueTask<ExtensionFactory?>(factory);
    }

    /// <summary>
    /// Unload the context for <paramref name="extensionPath"/> (hot reload: the next
    /// <see cref="LoadAsync"/> loads the file again). No-op when the path is not loaded.
    /// </summary>
    public void Unload(string extensionPath)
    {
        var fullPath = Path.GetFullPath(extensionPath);
        lock (gate)
        {
            if (contexts.Remove(fullPath, out var context))
            {
                context.Unload();
            }
        }
    }

    /// <summary>Unload every extension context.</summary>
    public void UnloadAll()
    {
        lock (gate)
        {
            foreach (var context in contexts.Values)
            {
                context.Unload();
            }

            contexts.Clear();
        }
    }

    private ExtensionAssemblyLoadContext GetOrCreateContext(string fullPath)
    {
        if (!contexts.TryGetValue(fullPath, out var context))
        {
            context = new ExtensionAssemblyLoadContext($"pi-extension:{Path.GetFileName(fullPath)}");
            contexts[fullPath] = context;
        }

        return context;
    }

    /// <summary>
    /// The assembly's types, skipping the ones that failed to load. TS import fails outright on a
    /// broken module; keeping the loadable types lets a partially broken assembly still surface
    /// its entry.
    /// </summary>
    private static Type[] GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error)
        {
            return error.Types.Where(static type => type is not null).Select(static type => type!).ToArray();
        }
    }

    /// <summary>
    /// Find the single <see cref="IExtensionEntry"/> implementation among <paramref name="types"/>
    /// and instantiate it. Returns null when none is found (TS: default export missing/not a
    /// function).
    /// </summary>
    internal static ExtensionFactory? FindEntryFactory(IEnumerable<Type> types, string extensionPath)
    {
        Type? entryType = null;
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !type.IsPublic)
            {
                continue;
            }

            if (!typeof(IExtensionEntry).IsAssignableFrom(type))
            {
                continue;
            }

            if (entryType is not null)
            {
                // TS has exactly one default export; an ambiguous entry is a plugin contract
                // violation, reported instead of silently picking one.
                var names = types
                    .Where(candidate => !candidate.IsAbstract && !candidate.IsInterface && candidate.IsPublic
                        && typeof(IExtensionEntry).IsAssignableFrom(candidate))
                    .Select(candidate => candidate.FullName)
                    .OrderBy(static name => name, StringComparer.Ordinal);
                throw new InvalidOperationException(
                    $"Extension assembly '{extensionPath}' exports multiple IExtensionEntry implementations: " +
                    string.Join(", ", names));
            }

            entryType = type;
        }

        if (entryType is null)
        {
            return null;
        }

        if (Activator.CreateInstance(entryType) is not IExtensionEntry entry)
        {
            throw new InvalidOperationException(
                $"Extension entry type '{entryType.FullName}' in '{extensionPath}' could not be instantiated " +
                "(a public parameterless constructor is required).");
        }

        return entry.Factory;
    }
}
