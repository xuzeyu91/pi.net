// ============================================================================
// Extension loader internals — port of core/extensions/loader.ts (4d-2b)
// ============================================================================
//
// The runtime with throwing stubs (TS `createExtensionRuntime`) and the per-extension API object
// (TS `createExtensionAPI`). Both are internal: consumers see `IExtensionRuntime` / `IExtensionApi`
// and create them through `ExtensionLoader`.

using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions.Types;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// The loader's runtime: shared state plus throwing action stubs. The runner replaces the stubs
/// when it binds (sub-phase 4e). Port of the object <c>createExtensionRuntime</c> returns.
/// </summary>
internal sealed class ExtensionRuntimeImpl : IExtensionRuntime
{
    private static InvalidOperationException NotInitialized() => new(
        "Extension runtime not initialized. Action methods cannot be called during extension loading.");

    private string? staleMessage;
    private readonly HashSet<Action> eventBusUnsubscribers = [];

    public IDictionary<string, object> FlagValues { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    public IList<PendingProviderRegistration> PendingProviderRegistrations { get; } = [];

    public IList<PendingNativeProviderRegistration> PendingNativeProviderRegistrations { get; } = [];

    public IList<PendingVirtualModelRegistration> PendingVirtualModelRegistrations { get; } = [];

    public McpServerRegistry McpServers { get; } = new();

    // ---- ExtensionActions: throwing stubs until the runner binds ----

    public void SendMessage(CustomMessageDraft message, SendMessageOptions? options) => throw NotInitialized();

    public void SendUserMessage(UserMessageContent content, SendUserMessageOptions? options) => throw NotInitialized();

    public void AppendEntry(string customType, object? data) => throw NotInitialized();

    public void SetSessionName(string name) => throw NotInitialized();

    public string? GetSessionName() => throw NotInitialized();

    public void SetLabel(string entryId, string? label) => throw NotInitialized();

    public IReadOnlyList<string> GetActiveTools() => throw NotInitialized();

    public IReadOnlyList<ToolInfo> GetAllTools() => throw NotInitialized();

    public Settings GetSettings() => throw NotInitialized();

    public void SetActiveTools(IReadOnlyList<string> toolNames) => throw NotInitialized();

    /// <summary>No-op stub: refreshing is only needed post-bind (TS <c>refreshTools: () =&gt; {}</c>).</summary>
    public void RefreshTools()
    {
    }

    public IReadOnlyList<SlashCommandInfo> GetCommands() => throw NotInitialized();

    /// <summary>TS returns a rejected promise; the port returns a faulted task.</summary>
    public Task<bool> SetModel(Model model) =>
        Task.FromException<bool>(new InvalidOperationException("Extension runtime not initialized"));

    public ThinkingLevel GetThinkingLevel() => throw NotInitialized();

    public void SetThinkingLevel(ThinkingLevel level) => throw NotInitialized();

    // ---- lifecycle ----

    public IExtensionContext CreateContext() => throw NotInitialized();

    public void AssertActive()
    {
        if (staleMessage is not null)
        {
            throw new InvalidOperationException(staleMessage);
        }
    }

    public void Invalidate(string? message = null)
    {
        if (staleMessage is not null) return;
        staleMessage = message ?? ExtensionLoader.StaleMessage;
        foreach (var unsubscribe in eventBusUnsubscribers)
        {
            unsubscribe();
        }

        eventBusUnsubscribers.Clear();
    }

    public Action TrackEventBusSubscription(Action unsubscribe)
    {
        var active = true;
        Action? tracked = null;
        tracked = () =>
        {
            if (!active) return;
            active = false;
            eventBusUnsubscribers.Remove(tracked!);
            unsubscribe();
        };
        eventBusUnsubscribers.Add(tracked!);
        return tracked!;
    }

    // ---- provider / virtual-model registration: queue until the runner binds ----

    public void RegisterProvider(string name, ProviderConfigInput config, string? extensionPath = null) =>
        PendingProviderRegistrations.Add(
            new PendingProviderRegistration(name, config, extensionPath ?? ExtensionLoader.UnknownExtensionPath));

    public void RegisterNativeProvider(Provider provider, string? extensionPath = null) =>
        PendingNativeProviderRegistrations.Add(
            new PendingNativeProviderRegistration(provider, extensionPath ?? ExtensionLoader.UnknownExtensionPath));

    public void UnregisterProvider(string name, string? extensionPath = null)
    {
        // TS filters by name only; the extensionPath parameter exists for signature parity.
        RemoveWhere(PendingProviderRegistrations, registration => registration.Name == name);
        RemoveWhere(PendingNativeProviderRegistrations, registration => registration.Provider.Id == name);
    }

    public void RegisterVirtualModel(VirtualModelDefinition definition, string? extensionPath = null) =>
        PendingVirtualModelRegistrations.Add(
            new PendingVirtualModelRegistration(definition, extensionPath ?? ExtensionLoader.UnknownExtensionPath));

    public void UnregisterVirtualModel(string provider, string id) =>
        RemoveWhere(
            PendingVirtualModelRegistrations,
            registration => registration.Definition.Provider == provider && registration.Definition.Id == id);

    private static void RemoveWhere<T>(IList<T> list, Func<T, bool> predicate)
    {
        for (var index = list.Count - 1; index >= 0; index--)
        {
            if (predicate(list[index]))
            {
                list.RemoveAt(index);
            }
        }
    }
}

/// <summary>
/// The per-extension API object. Port of the object <c>createExtensionAPI</c> returns: registration
/// methods write to the extension, action methods delegate to the runtime, and registrations made
/// while loading are queued until <see cref="Commit"/>.
/// </summary>
internal sealed class ExtensionApiImpl : IExtensionApi
{
    private readonly Extension extension;
    private readonly IExtensionRuntime runtime;
    private readonly string cwd;
    private readonly EventBus eventBus;
    private readonly Dictionary<string, object> pendingFlagValues = new(StringComparer.Ordinal);
    private readonly List<Action> pendingRuntimeChanges = [];
    private readonly List<Action> loadingUnsubscribers = [];
    private ApiState state = ApiState.Loading;

    public ExtensionApiImpl(Extension extension, IExtensionRuntime runtime, string cwd, EventBus eventBus)
    {
        this.extension = extension;
        this.runtime = runtime;
        this.cwd = cwd;
        this.eventBus = eventBus;
        Events = new EventsBus(eventBus, AssertActive, runtime.TrackEventBusSubscription, TrackLoadingSubscription);
    }

    private enum ApiState
    {
        Loading,
        Active,
        Failed,
    }

    public EventBus Events { get; }

    // ---- event subscription ----

    Action IExtensionApi.On<E>(string eventName, ExtensionHandler<E> handler)
    {
        AssertActive();
        ExtensionEventHandler registered = async args =>
        {
            var task = handler((E)args[0]!, (IExtensionContext)args[1]!);
            if (task is not null)
            {
                await task;
            }

            return (object?)null;
        };
        return RegisterHandler(eventName, registered);
    }

    Action IExtensionApi.On<E, R>(string eventName, ExtensionHandler<E, R> handler)
    {
        AssertActive();
        ExtensionEventHandler registered = async args =>
        {
            var task = handler((E)args[0]!, (IExtensionContext)args[1]!);
            return task is null ? null : (object?)await task;
        };
        return RegisterHandler(eventName, registered);
    }

    private Action RegisterHandler(string eventName, ExtensionEventHandler registered)
    {
        if (!extension.Handlers.TryGetValue(eventName, out var handlers))
        {
            handlers = [];
            extension.Handlers[eventName] = handlers;
        }

        handlers.Add(registered);

        return () =>
        {
            if (!extension.Handlers.TryGetValue(eventName, out var current)) return;
            var index = current.IndexOf(registered);
            if (index < 0) return;
            current.RemoveAt(index);
            if (current.Count == 0)
            {
                extension.Handlers.Remove(eventName);
            }
        };
    }

    // ---- registration ----

    public void RegisterTool(ToolDefinition tool)
    {
        AssertActive();
        if (tool.Parameters is null || tool.Parameters.JsonSchema is null)
        {
            throw new InvalidOperationException(
                $"Tool \"{tool.Name}\" registered by extension \"{extension.Path}\" must define an object parameter schema.");
        }

        extension.Tools[tool.Name] = new RegisteredTool { Definition = tool, SourceInfo = extension.SourceInfo };
        runtime.RefreshTools();
    }

    public void RegisterCommand(string name, CommandRegistration options)
    {
        AssertActive();
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException(
                $"Command registered by extension \"{extension.Path}\" must have a non-empty string name. Use pi.registerCommand(\"name\", {{ description, handler }}).");
        }

        if (options.Handler is null)
        {
            throw new InvalidOperationException(
                $"Command \"/{name}\" registered by extension \"{extension.Path}\" must define handler().");
        }

        extension.Commands[name] = new RegisteredCommand
        {
            Name = name,
            SourceInfo = extension.SourceInfo,
            Description = options.Description,
            GetArgumentCompletions = options.GetArgumentCompletions,
            Handler = options.Handler,
        };
    }

    public void RegisterShortcut(string shortcut, ShortcutRegistration options)
    {
        AssertActive();
        extension.Shortcuts[shortcut] = new ExtensionShortcut
        {
            Shortcut = shortcut,
            Description = options.Description,
            Handler = options.Handler,
            ExtensionPath = extension.Path,
        };
    }

    public void RegisterFlag(string name, FlagRegistration options)
    {
        AssertActive();
        if (options.Default is not null && !DefaultMatchesType(options.Default, options.Type))
        {
            throw new InvalidOperationException(
                $"Invalid default for flag \"{name}\": expected {options.Type}, got {JsTypeOf(options.Default)}");
        }

        extension.Flags[name] = new ExtensionFlag
        {
            Name = name,
            Description = options.Description,
            Type = options.Type,
            Default = options.Default,
            ExtensionPath = extension.Path,
        };

        if (options.Default is not null && !runtime.FlagValues.ContainsKey(name))
        {
            if (state == ApiState.Loading)
            {
                if (!pendingFlagValues.ContainsKey(name))
                {
                    pendingFlagValues[name] = options.Default;
                }
            }
            else
            {
                runtime.FlagValues[name] = options.Default;
            }
        }
    }

    public object? GetFlag(string name)
    {
        AssertActive();
        if (!extension.Flags.ContainsKey(name)) return null;
        return runtime.FlagValues.TryGetValue(name, out var value) ? value : pendingFlagValues.GetValueOrDefault(name);
    }

    public void RegisterMessageRenderer<T>(string customType, MessageRenderer<T> renderer)
    {
        AssertActive();
        extension.MessageRenderers[customType] = renderer;
    }

    public void RegisterMarkdownTransformer(MarkdownTransformer transformer)
    {
        AssertActive();
        extension.MarkdownTransformer = transformer;
    }

    public void RegisterEntryRenderer<T>(string customType, EntryRenderer<T> renderer)
    {
        AssertActive();
        extension.EntryRenderers[customType] = renderer;
    }

    public void RegisterToolRenderer(ToolRendererResolver resolver)
    {
        AssertActive();
        extension.ToolRenderers.Add(resolver);
    }

    // ---- actions ----

    public void SendMessage(CustomMessageDraft message, SendMessageOptions? options = null)
    {
        AssertActive();
        runtime.SendMessage(message, options);
    }

    public void SendUserMessage(string content, SendUserMessageOptions? options = null)
    {
        AssertActive();
        runtime.SendUserMessage(new UserMessageContent.Text(content), options);
    }

    public void SendUserMessage(IReadOnlyList<ContentBlock> content, SendUserMessageOptions? options = null)
    {
        AssertActive();
        runtime.SendUserMessage(new UserMessageContent.Blocks(content), options);
    }

    public void AppendEntry(string customType, object? data = null)
    {
        AssertActive();
        runtime.AppendEntry(customType, data);
    }

    public void SetSessionName(string name)
    {
        AssertActive();
        runtime.SetSessionName(name);
    }

    public string? GetSessionName()
    {
        AssertActive();
        return runtime.GetSessionName();
    }

    public void SetLabel(string entryId, string? label)
    {
        AssertActive();
        runtime.SetLabel(entryId, label);
    }

    public Task<ExecResult> Exec(string command, IReadOnlyList<string> args, ExecOptions? options = null)
    {
        AssertActive();
        return Core.Exec.ExecCommand(command, args, options?.Cwd ?? cwd, options);
    }

    public IReadOnlyList<string> GetActiveTools()
    {
        AssertActive();
        return runtime.GetActiveTools();
    }

    public IReadOnlyList<ToolInfo> GetAllTools()
    {
        AssertActive();
        return runtime.GetAllTools();
    }

    public Settings GetSettings()
    {
        AssertActive();
        return runtime.GetSettings();
    }

    public void SetActiveTools(IReadOnlyList<string> toolNames)
    {
        AssertActive();
        runtime.SetActiveTools(toolNames);
    }

    public IReadOnlyList<SlashCommandInfo> GetCommands()
    {
        AssertActive();
        return runtime.GetCommands();
    }

    public Task<bool> SetModel(Model model)
    {
        AssertActive();
        return runtime.SetModel(model);
    }

    public ThinkingLevel GetThinkingLevel()
    {
        AssertActive();
        return runtime.GetThinkingLevel();
    }

    public void SetThinkingLevel(ThinkingLevel level)
    {
        AssertActive();
        runtime.SetThinkingLevel(level);
    }

    // ---- providers ----

    public void RegisterProvider(Provider provider)
    {
        AssertActive();
        ApplyRuntimeChange(() => runtime.RegisterNativeProvider(provider, extension.Path));
    }

    public void RegisterProvider(string name, ProviderConfigInput config)
    {
        AssertActive();
        if (config is null)
        {
            throw new InvalidOperationException("Provider config is required when registering by name");
        }

        ApplyRuntimeChange(() => runtime.RegisterProvider(name, config, extension.Path));
    }

    public void UnregisterProvider(string name)
    {
        AssertActive();
        ApplyRuntimeChange(() => runtime.UnregisterProvider(name, extension.Path));
    }

    // ---- MCP servers ----

    public void RegisterMcpServer(string name, McpServerConfig config)
    {
        AssertActive();
        var validated = McpServers.ValidateMcpServerConfig(name, config.Value);
        if (!validated.Ok)
        {
            throw new InvalidOperationException(
                $"Invalid MCP server registered by extension \"{extension.Path}\": {validated.Error}");
        }

        var owner = runtime.McpServers.Get(name)?.ExtensionPath;
        if (owner is not null && owner != extension.Path)
        {
            throw new InvalidOperationException($"MCP server \"{name}\" is already registered by extension \"{owner}\"");
        }

        // Names that differ only in `-` and `_` would share a namespace.
        var clash = runtime.McpServers.List()
            .FirstOrDefault(server => server.Name != name && McpServers.Namespace(server.Name) == McpServers.Namespace(name));
        if (clash is not null)
        {
            throw new InvalidOperationException($"MCP server \"{name}\" conflicts with registered server \"{clash.Name}\"");
        }

        var server = new RegisteredMcpServer(
            name,
            new McpServerConfig((JsonObject)validated.Config!.Value.DeepClone()),
            extension.Path);
        ApplyRuntimeChange(() => runtime.McpServers.Register(server));
    }

    public void UnregisterMcpServer(string name)
    {
        AssertActive();
        ApplyRuntimeChange(() => runtime.McpServers.Unregister(name, extension.Path));
    }

    public IReadOnlyList<RegisteredMcpServer> GetMcpServers()
    {
        AssertActive();
        return runtime.McpServers.List();
    }

    // ---- virtual models ----

    public void RegisterVirtualModel(ExtensionVirtualModel model)
    {
        AssertActive();
        // Routing runs after the runner binds, so the context is created per request. The state
        // comes from the session branch that this router wrote.
        var definition = new VirtualModelDefinition
        {
            Provider = model.Provider,
            Id = model.Id,
            Name = model.Name,
            ThinkingLevels = model.ThinkingLevels,
            ContextWindow = model.ContextWindow,
            MaxTokens = model.MaxTokens,
            Input = model.Input,
            Route = request => model.Route(request, runtime.CreateContext()),
        };
        ApplyRuntimeChange(() => runtime.RegisterVirtualModel(definition, extension.Path));
    }

    public void UnregisterVirtualModel(string provider, string id)
    {
        AssertActive();
        ApplyRuntimeChange(() => runtime.UnregisterVirtualModel(provider, id));
    }

    // ---- commit / discard (TS commit/discard pair) ----

    public void Commit()
    {
        if (state != ApiState.Loading) return;
        runtime.AssertActive();
        foreach (var (name, value) in pendingFlagValues)
        {
            if (!runtime.FlagValues.ContainsKey(name))
            {
                runtime.FlagValues[name] = value;
            }
        }

        foreach (var apply in pendingRuntimeChanges)
        {
            apply();
        }

        state = ApiState.Active;
        ClearPending();
    }

    public void Discard()
    {
        if (state != ApiState.Loading) return;
        state = ApiState.Failed;
        foreach (var unsubscribe in loadingUnsubscribers)
        {
            unsubscribe();
        }

        ClearPending();
    }

    // ---- internals ----

    private void AssertActive()
    {
        if (state == ApiState.Failed)
        {
            throw new InvalidOperationException(
                $"Extension \"{extension.Path}\" failed to load and its API is no longer active.");
        }

        runtime.AssertActive();
    }

    private void ApplyRuntimeChange(Action change)
    {
        if (state == ApiState.Loading)
        {
            pendingRuntimeChanges.Add(change);
        }
        else
        {
            change();
        }
    }

    private void TrackLoadingSubscription(Action unsubscribe)
    {
        if (state == ApiState.Loading)
        {
            loadingUnsubscribers.Add(unsubscribe);
        }
    }

    private void ClearPending()
    {
        pendingFlagValues.Clear();
        pendingRuntimeChanges.Clear();
        loadingUnsubscribers.Clear();
    }

    private static bool DefaultMatchesType(object? value, string type) => type switch
    {
        FlagType.Boolean => value is bool,
        FlagType.String => value is string,
        _ => false,
    };

    /// <summary>TS <c>typeof</c> for the flag-default error message.</summary>
    private static string JsTypeOf(object? value) => value switch
    {
        null => "undefined",
        bool => "boolean",
        string => "string",
        _ => value.GetType().Name.ToLowerInvariant(),
    };

    /// <summary>
    /// The <c>pi.events</c> facade: the shared bus plus the loader's assertActive guard and
    /// subscription tracking. Port of the TS <c>events</c> object literal.
    /// </summary>
    private sealed class EventsBus(
        EventBus inner,
        Action assertActive,
        Func<Action, Action> track,
        Action<Action> trackLoading) : EventBus
    {
        public void Emit(string channel, object? data)
        {
            assertActive();
            inner.Emit(channel, data);
        }

        public Action On(string channel, Func<object?, Task> handler)
        {
            assertActive();
            var unsubscribe = track(inner.On(channel, handler));
            trackLoading(unsubscribe);
            return unsubscribe;
        }
    }
}
