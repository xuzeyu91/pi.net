using System.Reflection;
using System.Reflection.Emit;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the AssemblyLoadContext module loader (batch 4d-3, decision D2).
/// The TS jiti import path is the vector: a module with a default-export factory loads, a module
/// without one yields the "does not export a valid factory function" case (null), and an import
/// failure surfaces as an exception the loader wraps into "Failed to load extension: …".
/// </summary>
/// <remarks>
/// The single-entry case uses this test assembly itself (<see cref="SampleExtensionEntry"/>); the
/// ambiguous-entry case emits a throwaway assembly with <see cref="PersistedAssemblyBuilder"/>.
/// </remarks>
public class AssemblyLoadContextLoaderTests : IDisposable
{
    private readonly List<string> tempDirs = [];
    private readonly AssemblyLoadContextExtensionLoader loader = new();

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-alc-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        loader.UnloadAll();
        foreach (var dir in tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Loaded assemblies can keep a lock on Windows; best effort cleanup.
            }
        }
    }

    private static ToolDefinition MakeTool(string name) => new()
    {
        Name = name,
        Label = name,
        Description = $"tool {name}",
        Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
        Execute = static (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
    };

    // ------------------------------------------------------------------ loading

    [Fact]
    public async Task LoadAsync_EntryAssembly_ReturnsFactoryThatRegistersThroughLoader()
    {
        var factory = await loader.LoadAsync(typeof(SampleExtensionEntry).Assembly.Location);

        Assert.NotNull(factory);
        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            factory,
            Environment.CurrentDirectory,
            EventBusController.CreateEventBus(),
            ExtensionLoader.CreateExtensionRuntime());

        // End-to-end: the factory from the loaded assembly registers its tool through the real API.
        Assert.True(extension.Tools.ContainsKey("sample"));
    }

    [Fact]
    public async Task LoadAsync_SamePathTwice_ReturnsSameFactory()
    {
        var path = typeof(SampleExtensionEntry).Assembly.Location;
        var first = await loader.LoadAsync(path);
        var second = await loader.LoadAsync(path);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task LoadAsync_AssemblyWithoutEntry_ReturnsNull()
    {
        // Pi.CodingAgent itself carries the contract types but no IExtensionEntry implementation:
        // the TS "module has no default export" case.
        var factory = await loader.LoadAsync(typeof(ExtensionLoader).Assembly.Location);

        Assert.Null(factory);
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ThrowsFileNotFoundException()
    {
        var missing = Path.Combine(NewTempDir(), "missing.dll");

        var error = await Assert.ThrowsAsync<FileNotFoundException>(
            () => loader.LoadAsync(missing).AsTask());

        Assert.Contains("Cannot find extension module", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_InvalidAssembly_ThrowsInvalidOperationException()
    {
        var path = Path.Combine(NewTempDir(), "not-an-assembly.dll");
        await File.WriteAllTextAsync(path, "this is not a .NET assembly");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(path).AsTask());

        Assert.Contains("Could not load extension assembly", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_MultipleEntries_ThrowsInsteadOfPickingOne()
    {
        var path = Path.Combine(NewTempDir(), "PiTestMultiEntry.dll");
        EmitMultiEntryAssembly(path);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => loader.LoadAsync(path).AsTask());

        Assert.Contains("multiple IExtensionEntry implementations", error.Message, StringComparison.Ordinal);
        Assert.Contains("EntryOne", error.Message, StringComparison.Ordinal);
        Assert.Contains("EntryTwo", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ hot reload

    [Fact]
    public async Task Unload_ThenReload_ReturnsNewFactory()
    {
        var path = typeof(SampleExtensionEntry).Assembly.Location;
        var first = await loader.LoadAsync(path);

        loader.Unload(path);
        var second = await loader.LoadAsync(path);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Unload_UnknownPath_IsNoOp()
    {
        loader.Unload(Path.Combine(NewTempDir(), "never-loaded.dll"));

        var factory = await loader.LoadAsync(typeof(SampleExtensionEntry).Assembly.Location);

        Assert.NotNull(factory);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Emit an assembly with two <see cref="IExtensionEntry"/> implementations. The entry getters
    /// return null and are never invoked: the loader rejects the ambiguity before instantiating.
    /// </summary>
    private static void EmitMultiEntryAssembly(string path)
    {
        var assemblyName = new AssemblyName("PiTestMultiEntry");
        var builder = new PersistedAssemblyBuilder(assemblyName, typeof(object).Assembly);
        // The dynamic module name must match the assembly name (no file extension).
        var module = builder.DefineDynamicModule(assemblyName.Name!);
        var interfaceGetter = typeof(IExtensionEntry).GetMethod("get_Factory")!;

        foreach (var typeName in new[] { "EntryOne", "EntryTwo" })
        {
            var type = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class);
            type.AddInterfaceImplementation(typeof(IExtensionEntry));
            var getter = type.DefineMethod(
                "get_Factory",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig
                    | MethodAttributes.NewSlot | MethodAttributes.Final,
                typeof(ExtensionFactory),
                Type.EmptyTypes);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(getter, interfaceGetter);
            type.CreateType();
        }

        builder.Save(path);
    }
}

/// <summary>
/// The single <see cref="IExtensionEntry"/> implementation inside the test assembly; the loader
/// finds it when the test assembly itself is loaded as an extension module.
/// </summary>
public sealed class SampleExtensionEntry : IExtensionEntry
{
    public ExtensionFactory Factory => pi =>
    {
        pi.RegisterTool(new ToolDefinition
        {
            Name = "sample",
            Label = "sample",
            Description = "sample tool",
            Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
            Execute = static (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
        });
        return Task.CompletedTask;
    };
}
