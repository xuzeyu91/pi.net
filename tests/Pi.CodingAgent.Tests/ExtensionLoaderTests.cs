using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the extension loader (port of TS <c>core/extensions/loader.ts</c>, batch 4d-2b).
/// </summary>
/// <remarks>
/// <para>
/// Test vectors are transcribed from the TS suite: <c>8423-extension-factory-failure.test.ts</c>
/// (inline factory load, error aggregation, registration API), <c>extension-factory-cache.test.ts</c>
/// (module cache vs factory re-run, cwd invalidation) and <c>extensions-discovery.test.ts</c>
/// (file/directory discovery, index files, package.json manifests, load order).
/// </para>
/// <para>
/// Module loading sits behind <see cref="IExtensionModuleLoader"/> until batch 4d-3 lands the
/// AssemblyLoadContext route, so the tests drive it with a fake loader — the same seam the TS tests
/// use with jiti.
/// </para>
/// </remarks>
public class ExtensionLoaderTests : IDisposable
{
    private readonly List<string> tempDirs = [];

    // ------------------------------------------------------------------ helpers

    private static ToolDefinition MakeTool(string name) => new()
    {
        Name = name,
        Label = name,
        Description = $"tool {name}",
        Parameters = new ToolSchema(new Dictionary<string, object?> { ["type"] = "object" }),
        Execute = static (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
    };

    private static McpServerConfig StdioConfig() => new(new JsonObject
    {
        ["command"] = "echo",
        ["args"] = new JsonArray("hi"),
    });

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-ext-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(dir);
        tempDirs.Add(dir);
        return dir;
    }

    private static void WriteFile(string path, string content = "")
    {
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        ExtensionLoader.ClearExtensionCache();
        foreach (var dir in tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup.
            }
        }
    }

    /// <summary>Fake module loader: maps resolved paths to factories and counts imports.</summary>
    private sealed class FakeModuleLoader : IExtensionModuleLoader
    {
        private readonly Dictionary<string, ExtensionFactory> factories;

        public FakeModuleLoader(params (string Path, ExtensionFactory Factory)[] entries)
        {
            factories = new Dictionary<string, ExtensionFactory>(StringComparer.Ordinal);
            foreach (var (path, factory) in entries)
            {
                factories[path] = factory;
            }
        }

        public int ImportCount { get; private set; }

        public List<string> ImportedPaths { get; } = [];

        public ValueTask<ExtensionFactory?> LoadAsync(string extensionPath, CancellationToken cancellationToken = default)
        {
            ImportCount++;
            ImportedPaths.Add(extensionPath);
            return new ValueTask<ExtensionFactory?>(factories.GetValueOrDefault(extensionPath));
        }
    }

    // ------------------------------------------------------------------ inline factory loading (8423)

    [Fact]
    public async Task LoadExtensionFromFactory_RegistersToolsCommandsFlagsAndShortcuts()
    {
        var bus = EventBusController.CreateEventBus();
        var runtime = ExtensionLoader.CreateExtensionRuntime();

        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterTool(MakeTool("greet"));
                pi.RegisterCommand("hello", new CommandRegistration
                {
                    Description = "say hi",
                    Handler = static (_, _) => Task.CompletedTask,
                });
                pi.RegisterFlag("verbose", new FlagRegistration { Type = FlagType.Boolean, Default = true, Description = "v" });
                pi.RegisterShortcut("ctrl+k", new ShortcutRegistration
                {
                    Description = "clear",
                    Handler = static _ => Task.CompletedTask,
                });
                return Task.CompletedTask;
            },
            NewTempDir(),
            bus,
            runtime);

        Assert.Equal(ExtensionLoader.InlineExtensionPath, extension.Path);
        Assert.Equal(ExtensionLoader.InlineExtensionPath, extension.ResolvedPath);
        Assert.False(extension.Hidden);
        Assert.False(extension.Replaceable);
        Assert.Contains("greet", extension.Tools.Keys);
        Assert.Contains("hello", extension.Commands.Keys);
        Assert.Contains("verbose", extension.Flags.Keys);
        Assert.Contains("ctrl+k", extension.Shortcuts.Keys);
        Assert.Equal("say hi", extension.Commands["hello"].Description);
        Assert.Equal(true, extension.Flags["verbose"].Default);

        // Synthetic paths get a temporary-scope source info with no base dir.
        Assert.Equal(SourceScope.Temporary, extension.SourceInfo.Scope);
        Assert.Equal(SourceOrigin.TopLevel, extension.SourceInfo.Origin);
        Assert.Null(extension.SourceInfo.BaseDir);
    }

    [Fact]
    public async Task LoadExtensionFromFactory_StampsFileSourceInfo_ForRealPaths()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        WriteFile(path);

        var extension = await ExtensionLoader.LoadExtensionFromFactory(
            _ => Task.CompletedTask,
            dir,
            EventBusController.CreateEventBus(),
            ExtensionLoader.CreateExtensionRuntime(),
            extensionPath: path);

        Assert.Equal(path, extension.Path);
        Assert.Equal(dir, extension.SourceInfo.BaseDir);
        Assert.Equal("local", extension.SourceInfo.Source);
    }

    [Fact]
    public async Task LoadExtensions_CollectsError_WhenFactoryThrows()
    {
        var dir = NewTempDir();
        var failing = Path.Combine(dir, "failing.ts");
        var loader = new FakeModuleLoader((
            failing,
            _ => throw new InvalidOperationException("boom in factory")));

        var result = await ExtensionLoader.LoadExtensions([failing], dir, moduleLoader: loader);

        var error = Assert.Single(result.Errors);
        Assert.Equal(failing, error.Path);
        Assert.Contains("Failed to load extension", error.Error, StringComparison.Ordinal);
        Assert.Contains("boom in factory", error.Error, StringComparison.Ordinal);
        Assert.Empty(result.Extensions);
    }

    [Fact]
    public async Task LoadExtensions_ContinuesPastFailures_AndKeepsOrder()
    {
        var dir = NewTempDir();
        var good = Path.Combine(dir, "good.ts");
        var bad = Path.Combine(dir, "bad.ts");
        var alsoGood = Path.Combine(dir, "also-good.ts");
        var loader = new FakeModuleLoader(
            (good, pi => { pi.RegisterTool(MakeTool("one")); return Task.CompletedTask; }),
            (bad, _ => throw new InvalidOperationException("nope")),
            (alsoGood, pi => { pi.RegisterTool(MakeTool("two")); return Task.CompletedTask; }));

        var result = await ExtensionLoader.LoadExtensions([good, bad, alsoGood], dir, moduleLoader: loader);

        Assert.Equal(2, result.Extensions.Count);
        Assert.Equal("one", Assert.Single(result.Extensions[0].Tools.Keys));
        Assert.Equal("two", Assert.Single(result.Extensions[1].Tools.Keys));
        var error = Assert.Single(result.Errors);
        Assert.Equal(bad, error.Path);
    }

    [Fact]
    public async Task LoadExtensions_ReportsMissingFactory()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "no-factory.ts");
        var loader = new FakeModuleLoader(); // no entry → LoadAsync returns null

        var result = await ExtensionLoader.LoadExtensions([path], dir, moduleLoader: loader);

        var error = Assert.Single(result.Errors);
        Assert.Equal($"Extension does not export a valid factory function: {path}", error.Error);
    }

    [Fact]
    public async Task LoadExtensions_WithoutModuleLoader_ReportsClearError()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");

        var result = await ExtensionLoader.LoadExtensions([path], dir);

        var error = Assert.Single(result.Errors);
        Assert.Contains("no IExtensionModuleLoader was supplied", error.Error, StringComparison.Ordinal);
        Assert.Contains("4d-3", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadExtensions_ResolvesRelativePathsAgainstCwd()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "rel.ts");
        WriteFile(path);
        var loader = new FakeModuleLoader((path, _ => Task.CompletedTask));

        var result = await ExtensionLoader.LoadExtensions(["rel.ts"], dir, moduleLoader: loader);

        Assert.Empty(result.Errors);
        Assert.Single(loader.ImportedPaths, path);
    }

    // ------------------------------------------------------------------ registration API

    [Fact]
    public async Task RegisterTool_RejectsMissingParameterSchema()
    {
        var dir = NewTempDir();
        var error = await CaptureFactoryError(
            dir,
            pi => pi.RegisterTool(new ToolDefinition
            {
                Name = "broken",
                Label = "broken",
                Description = "no schema",
                Parameters = null!,
                Execute = static (_, _, _, _, _) => Task.FromResult(new AgentToolResult([])),
            }));

        Assert.Contains("must define an object parameter schema", error, StringComparison.Ordinal);
        Assert.Contains("broken", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterCommand_RejectsEmptyName()
    {
        var error = await CaptureFactoryError(
            NewTempDir(),
            pi => pi.RegisterCommand("", new CommandRegistration
            {
                Handler = static (_, _) => Task.CompletedTask,
            }));

        Assert.Contains("must have a non-empty string name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterFlag_RejectsMismatchedDefault()
    {
        var error = await CaptureFactoryError(
            NewTempDir(),
            pi => pi.RegisterFlag("count", new FlagRegistration { Type = FlagType.Boolean, Default = "yes" }));

        Assert.Contains("Invalid default for flag \"count\"", error, StringComparison.Ordinal);
        Assert.Contains("expected boolean, got string", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFlag_ReturnsRegisteredDefault()
    {
        object? seen = "unset";
        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterFlag("mode", new FlagRegistration { Type = FlagType.String, Default = "fast" });
                seen = pi.GetFlag("mode");
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            ExtensionLoader.CreateExtensionRuntime());

        Assert.Equal("fast", seen);
    }

    [Fact]
    public async Task GetFlag_ReturnsNull_ForUnregisteredFlag()
    {
        object? seen = "unset";
        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                seen = pi.GetFlag("nope");
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            ExtensionLoader.CreateExtensionRuntime());

        Assert.Null(seen);
    }

    [Fact]
    public async Task Commit_AppliesFlagDefaultsToRuntime()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterFlag("verbose", new FlagRegistration { Type = FlagType.Boolean, Default = true });
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            runtime);

        Assert.Equal(true, runtime.FlagValues["verbose"]);
    }

    [Fact]
    public async Task Commit_DoesNotOverrideExistingFlagValues()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        runtime.FlagValues["verbose"] = false; // CLI value set before loading

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterFlag("verbose", new FlagRegistration { Type = FlagType.Boolean, Default = true });
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            runtime);

        Assert.Equal(false, runtime.FlagValues["verbose"]);
    }

    [Fact]
    public async Task RegisterProvider_QueuesUntilCommit_WithExtensionPath()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterProvider("my-provider", new ProviderConfigInput());
                return Task.CompletedTask;
            },
            dir,
            EventBusController.CreateEventBus(),
            runtime,
            extensionPath: path);

        var pending = Assert.Single(runtime.PendingProviderRegistrations);
        Assert.Equal("my-provider", pending.Name);
        Assert.Equal(path, pending.ExtensionPath);
    }

    [Fact]
    public async Task RegisterProvider_WithoutName_QueuesNativeProvider()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterProvider(new Provider { Id = "test-provider" });
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            runtime);

        var pending = Assert.Single(runtime.PendingNativeProviderRegistrations);

        // The inline factory's path ("<inline>") is stamped, not the runtime's "<unknown>" default:
        // the API passes its own extension path, exactly like the TS closure.
        Assert.Equal(ExtensionLoader.InlineExtensionPath, pending.ExtensionPath);
    }

    [Fact]
    public async Task Discard_RollsBackQueuedRegistrations_WhenFactoryFails()
    {
        var dir = NewTempDir();
        var bus = EventBusController.CreateEventBus();
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var path = Path.Combine(dir, "ext.ts");
        ExtensionFactory factory = pi =>
        {
            pi.RegisterProvider("queued", new ProviderConfigInput());
            pi.RegisterFlag("f", new FlagRegistration { Type = FlagType.String, Default = "d" });
            pi.Events.On("session_start", _ => Task.CompletedTask);
            throw new InvalidOperationException("late failure");
        };
        var loader = new FakeModuleLoader((path, factory));

        var result = await ExtensionLoader.LoadExtensions([path], dir, bus, runtime, loader);

        Assert.Single(result.Errors);
        Assert.Empty(runtime.PendingProviderRegistrations); // queued change discarded
        Assert.Empty(runtime.FlagValues); // flag default discarded
    }

    // ------------------------------------------------------------------ MCP registration

    [Fact]
    public async Task RegisterMcpServer_RegistersOwnedServer()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.RegisterMcpServer("docs", StdioConfig());
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            runtime);

        var server = runtime.McpServers.Get("docs");
        Assert.NotNull(server);
        Assert.Equal("echo", server.Config.Value["command"]?.GetValue<string>());
    }

    [Fact]
    public async Task RegisterMcpServer_RejectsInvalidConfig()
    {
        var error = await CaptureFactoryError(
            NewTempDir(),
            pi => pi.RegisterMcpServer("bad name!", StdioConfig()));

        Assert.Contains("Invalid MCP server", error, StringComparison.Ordinal);
        Assert.Contains("invalid server name", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterMcpServer_RejectsDuplicateOwner()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var bus = EventBusController.CreateEventBus();
        var dir = NewTempDir();
        var first = Path.Combine(dir, "first.ts");
        var second = Path.Combine(dir, "second.ts");
        var loader = new FakeModuleLoader(
            (first, pi => { pi.RegisterMcpServer("docs", StdioConfig()); return Task.CompletedTask; }),
            (second, pi => { pi.RegisterMcpServer("docs", StdioConfig()); return Task.CompletedTask; }));

        var result = await ExtensionLoader.LoadExtensions([first, second], dir, bus, runtime, loader);

        Assert.Single(result.Extensions);
        var error = Assert.Single(result.Errors);
        Assert.Contains("already registered by extension", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterMcpServer_RejectsNamespaceClash()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var bus = EventBusController.CreateEventBus();
        var dir = NewTempDir();
        var first = Path.Combine(dir, "first.ts");
        var second = Path.Combine(dir, "second.ts");
        var loader = new FakeModuleLoader(
            (first, pi => { pi.RegisterMcpServer("my-server", StdioConfig()); return Task.CompletedTask; }),
            (second, pi => { pi.RegisterMcpServer("my_server", StdioConfig()); return Task.CompletedTask; }));

        var result = await ExtensionLoader.LoadExtensions([first, second], dir, bus, runtime, loader);

        Assert.Single(result.Extensions);
        var error = Assert.Single(result.Errors);
        Assert.Contains("conflicts with registered server", error.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnregisterMcpServer_RemovesOwnedServer()
    {
        // The same extension path registers on the first load and unregisters on the second, so the
        // owner check passes (TS: `unregisterMcpServer` only removes servers the caller owns).
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var bus = EventBusController.CreateEventBus();
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        var runs = 0;
        var loader = new FakeModuleLoader((path, pi =>
        {
            runs++;
            if (runs == 1)
            {
                pi.RegisterMcpServer("docs", StdioConfig());
            }
            else
            {
                pi.UnregisterMcpServer("docs");
            }

            return Task.CompletedTask;
        }));

        await ExtensionLoader.LoadExtensions([path], dir, bus, runtime, loader);
        Assert.NotNull(runtime.McpServers.Get("docs"));

        await ExtensionLoader.LoadExtensions([path], dir, bus, runtime, loader);
        Assert.Null(runtime.McpServers.Get("docs"));
    }

    [Fact]
    public async Task UnregisterMcpServer_IgnoresServersOfOtherExtensions()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var bus = EventBusController.CreateEventBus();
        var dir = NewTempDir();
        var first = Path.Combine(dir, "first.ts");
        var second = Path.Combine(dir, "second.ts");
        var loader = new FakeModuleLoader(
            (first, pi => { pi.RegisterMcpServer("docs", StdioConfig()); return Task.CompletedTask; }),
            (second, pi => { pi.UnregisterMcpServer("docs"); return Task.CompletedTask; }));

        await ExtensionLoader.LoadExtensions([first, second], dir, bus, runtime, loader);

        // The second extension does not own "docs", so the server survives.
        Assert.NotNull(runtime.McpServers.Get("docs"));
    }

    // ------------------------------------------------------------------ events facade

    [Fact]
    public async Task Events_On_RegistersHandler_OnTheSharedBus()
    {
        var bus = EventBusController.CreateEventBus();
        var seen = new List<object?>();

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                pi.Events.On("session_start", data => { seen.Add(data); return Task.CompletedTask; });
                return Task.CompletedTask;
            },
            NewTempDir(),
            bus,
            ExtensionLoader.CreateExtensionRuntime());

        bus.Emit("session_start", "payload");

        Assert.Equal(["payload"], seen);
    }

    [Fact]
    public async Task Events_On_Unsubscribe_StopsDispatch()
    {
        var bus = EventBusController.CreateEventBus();
        var calls = 0;
        Action? unsubscribe = null;

        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                unsubscribe = pi.Events.On("tick", _ => { calls++; return Task.CompletedTask; });
                return Task.CompletedTask;
            },
            NewTempDir(),
            bus,
            ExtensionLoader.CreateExtensionRuntime());

        bus.Emit("tick", null);
        unsubscribe!();
        bus.Emit("tick", null);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Events_Emit_IsVisibleToOtherExtensions()
    {
        var bus = EventBusController.CreateEventBus();
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var dir = NewTempDir();
        var emitter = Path.Combine(dir, "emitter.ts");
        var listener = Path.Combine(dir, "listener.ts");
        var received = new List<object?>();
        var loader = new FakeModuleLoader(
            (emitter, pi => { pi.Events.On("ping", data => { received.Add(data); return Task.CompletedTask; }); return Task.CompletedTask; }),
            (listener, pi => { pi.Events.Emit("ping", 42); return Task.CompletedTask; }));

        await ExtensionLoader.LoadExtensions([emitter, listener], dir, bus, runtime, loader);

        Assert.Equal([42], received);
    }

    // ------------------------------------------------------------------ runtime stubs and staleness

    [Fact]
    public async Task ActionMethods_ThrowDuringLoading()
    {
        var error = await CaptureFactoryError(
            NewTempDir(),
            pi =>
            {
                _ = pi.GetSettings();
            });

        Assert.Contains("Extension runtime not initialized", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetModel_ReturnsFaultedTask_DuringLoading()
    {
        Task<bool>? captured = null;
        await ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                captured = pi.SetModel(new Model("test-model", "Test Model", "openai-completions", "test"));
                return Task.CompletedTask;
            },
            NewTempDir(),
            EventBusController.CreateEventBus(),
            ExtensionLoader.CreateExtensionRuntime());

        Assert.NotNull(captured);
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await captured!);
        Assert.Contains("Extension runtime not initialized", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalidate_MakesTheApiStale()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var bus = EventBusController.CreateEventBus();
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        IExtensionApi? captured = null;
        var loader = new FakeModuleLoader((path, pi => { captured = pi; return Task.CompletedTask; }));

        var result = await ExtensionLoader.LoadExtensions([path], dir, bus, runtime, loader);
        runtime.Invalidate();

        var extension = Assert.Single(result.Extensions);
        Assert.NotNull(captured);
        var thrown = Assert.Throws<InvalidOperationException>(() => captured!.GetSettings());
        Assert.Contains("stale after session replacement or reload", thrown.Message, StringComparison.Ordinal);
        Assert.NotNull(extension);
    }

    [Fact]
    public async Task Invalidate_UnsubscribesEventBusHandlers()
    {
        var runtime = ExtensionLoader.CreateExtensionRuntime();
        var bus = EventBusController.CreateEventBus();
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        var calls = 0;
        var loader = new FakeModuleLoader((
            path,
            pi =>
            {
                pi.Events.On("tick", _ => { calls++; return Task.CompletedTask; });
                return Task.CompletedTask;
            }));

        await ExtensionLoader.LoadExtensions([path], dir, bus, runtime, loader);
        runtime.Invalidate();
        bus.Emit("tick", null);

        Assert.Equal(0, calls);
    }

    // ------------------------------------------------------------------ module cache

    [Fact]
    public async Task LoadExtensionsCached_ImportsOnce_ButRunsFactoryEveryTime()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        var factoryRuns = 0;
        var loader = new FakeModuleLoader((
            path,
            _ =>
            {
                factoryRuns++;
                return Task.CompletedTask;
            }));

        var first = await ExtensionLoader.LoadExtensionsCached([path], dir, moduleLoader: loader);
        var second = await ExtensionLoader.LoadExtensionsCached([path], dir, moduleLoader: loader);

        Assert.Equal(1, loader.ImportCount);
        Assert.Equal(2, factoryRuns);
        Assert.Single(first.Extensions);
        Assert.Single(second.Extensions);
    }

    [Fact]
    public async Task LoadExtensions_DoesNotCacheModules()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        var loader = new FakeModuleLoader((path, _ => Task.CompletedTask));

        await ExtensionLoader.LoadExtensions([path], dir, moduleLoader: loader);
        await ExtensionLoader.LoadExtensions([path], dir, moduleLoader: loader);

        Assert.Equal(2, loader.ImportCount);
    }

    [Fact]
    public async Task ClearExtensionCache_ForcesReimport()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "ext.ts");
        var loader = new FakeModuleLoader((path, _ => Task.CompletedTask));

        await ExtensionLoader.LoadExtensionsCached([path], dir, moduleLoader: loader);
        ExtensionLoader.ClearExtensionCache();
        await ExtensionLoader.LoadExtensionsCached([path], dir, moduleLoader: loader);

        Assert.Equal(2, loader.ImportCount);
    }

    [Fact]
    public async Task ExtensionCache_InvalidatesOnCwdChange()
    {
        var first = NewTempDir();
        var second = NewTempDir();
        var path = Path.Combine(first, "ext.ts");
        var loader = new FakeModuleLoader((path, _ => Task.CompletedTask));

        await ExtensionLoader.LoadExtensionsCached([path], first, moduleLoader: loader);
        await ExtensionLoader.LoadExtensionsCached([path], second, moduleLoader: loader);

        Assert.Equal(2, loader.ImportCount);
    }

    // ------------------------------------------------------------------ discovery

    [Fact]
    public void DiscoverExtensionsInDir_FindsDirectTsAndJsFiles()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "a.ts"));
        WriteFile(Path.Combine(dir, "b.js"));
        WriteFile(Path.Combine(dir, "notes.txt"));

        var discovered = ExtensionLoader.DiscoverExtensionsInDir(dir);

        Assert.Equal(
            [Path.Combine(dir, "a.ts"), Path.Combine(dir, "b.js")],
            discovered);
    }

    [Fact]
    public void DiscoverExtensionsInDir_FindsIndexTs_InSubdirectory()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "sub", "index.ts"));

        var discovered = ExtensionLoader.DiscoverExtensionsInDir(dir);

        Assert.Equal([Path.Combine(dir, "sub", "index.ts")], discovered);
    }

    [Fact]
    public void DiscoverExtensionsInDir_FindsIndexJs_WhenIndexTsIsAbsent()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "sub", "index.js"));

        var discovered = ExtensionLoader.DiscoverExtensionsInDir(dir);

        Assert.Equal([Path.Combine(dir, "sub", "index.js")], discovered);
    }

    [Fact]
    public void DiscoverExtensionsInDir_UsesPackageJsonManifest()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "pkg", "package.json"), """{"pi":{"extensions":["entry.ts","missing.ts"]}}""");
        WriteFile(Path.Combine(dir, "pkg", "entry.ts"));

        var discovered = ExtensionLoader.DiscoverExtensionsInDir(dir);

        // The manifest wins over index files, and missing entries are skipped.
        Assert.Equal([Path.Combine(dir, "pkg", "entry.ts")], discovered);
    }

    [Fact]
    public void DiscoverExtensionsInDir_ManifestWinsOverIndexFiles()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "pkg", "package.json"), """{"pi":{"extensions":["entry.ts"]}}""");
        WriteFile(Path.Combine(dir, "pkg", "entry.ts"));
        WriteFile(Path.Combine(dir, "pkg", "index.ts"));

        var discovered = ExtensionLoader.DiscoverExtensionsInDir(dir);

        Assert.Equal([Path.Combine(dir, "pkg", "entry.ts")], discovered);
    }

    [Fact]
    public void DiscoverExtensionsInDir_FallsBackToIndex_WhenManifestEntriesAreMissing()
    {
        var dir = NewTempDir();
        WriteFile(Path.Combine(dir, "pkg", "package.json"), """{"pi":{"extensions":["gone.ts"]}}""");
        WriteFile(Path.Combine(dir, "pkg", "index.ts"));

        var discovered = ExtensionLoader.DiscoverExtensionsInDir(dir);

        Assert.Equal([Path.Combine(dir, "pkg", "index.ts")], discovered);
    }

    [Fact]
    public void DiscoverExtensionsInDir_MissingDirectory_ReturnsEmpty()
    {
        Assert.Empty(ExtensionLoader.DiscoverExtensionsInDir(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("n")[..8])));
    }

    [Fact]
    public void ResolveExtensionEntries_ReturnsNull_ForPlainDirectory()
    {
        var dir = NewTempDir();

        Assert.Null(ExtensionLoader.ResolveExtensionEntries(dir));
    }

    [Fact]
    public async Task DiscoverAndLoadExtensions_LoadsProjectThenGlobalThenConfigured_WithoutDuplicates()
    {
        var cwd = NewTempDir();
        var agentDir = NewTempDir();
        var projectExtension = Path.Combine(cwd, ".pi", "extensions", "project.ts");
        var globalExtension = Path.Combine(agentDir, "extensions", "global.ts");
        var configuredExtension = Path.Combine(cwd, "configured.ts");
        WriteFile(projectExtension);
        WriteFile(globalExtension);
        WriteFile(configuredExtension);

        var loader = new FakeModuleLoader(
            (projectExtension, _ => Task.CompletedTask),
            (globalExtension, _ => Task.CompletedTask),
            (configuredExtension, _ => Task.CompletedTask));

        var result = await ExtensionLoader.DiscoverAndLoadExtensions(
            [projectExtension, configuredExtension],
            cwd,
            agentDir,
            moduleLoader: loader);

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Extensions.Count);
        Assert.Equal(
            [projectExtension, globalExtension, configuredExtension],
            loader.ImportedPaths);
    }

    [Fact]
    public async Task DiscoverAndLoadExtensions_ResolvesConfiguredDirectoryToItsIndex()
    {
        var cwd = NewTempDir();
        var agentDir = NewTempDir();
        var index = Path.Combine(cwd, "bundle", "index.ts");
        WriteFile(index);
        WriteFile(Path.Combine(cwd, "bundle", "other.ts")); // must not be loaded

        var loader = new FakeModuleLoader((index, _ => Task.CompletedTask));

        var result = await ExtensionLoader.DiscoverAndLoadExtensions(
            [Path.Combine(cwd, "bundle")],
            cwd,
            agentDir,
            moduleLoader: loader);

        Assert.Empty(result.Errors);
        Assert.Single(loader.ImportedPaths, index);
    }

    [Fact]
    public async Task DiscoverAndLoadExtensions_ResolvesRelativeConfiguredPathsAgainstCwd()
    {
        var cwd = NewTempDir();
        var agentDir = NewTempDir();
        var configured = Path.Combine(cwd, "rel.ts");
        WriteFile(configured);
        var loader = new FakeModuleLoader((configured, _ => Task.CompletedTask));

        var result = await ExtensionLoader.DiscoverAndLoadExtensions(["rel.ts"], cwd, agentDir, moduleLoader: loader);

        Assert.Empty(result.Errors);
        Assert.Single(loader.ImportedPaths, configured);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Runs a factory that is expected to throw and returns the exception message. Mirrors the TS
    /// 8423 test, which calls <c>loadExtensionFromFactory</c> and expects the registration error to
    /// propagate (the loader only aggregates errors for path-based loads).
    /// </summary>
    private static async Task<string> CaptureFactoryError(string cwd, Action<IExtensionApi> register)
    {
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ExtensionLoader.LoadExtensionFromFactory(
            pi =>
            {
                register(pi);
                return Task.CompletedTask;
            },
            cwd,
            EventBusController.CreateEventBus(),
            ExtensionLoader.CreateExtensionRuntime()));

        return thrown.Message;
    }
}
