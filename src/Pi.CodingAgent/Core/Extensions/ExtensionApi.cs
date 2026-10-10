using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.Tui;

namespace Pi.CodingAgent.Core.Extensions;

// ============================================================================
// Extension API — port of core/extensions/types.ts (4d-1)
// ============================================================================

/// <summary>
/// Handler function type for events without a result. TS
/// <c>ExtensionHandler&lt;E, R = undefined&gt;</c> 的无结果半支。
/// </summary>
public delegate Task? ExtensionHandler<in E>(E @event, IExtensionContext ctx);

/// <summary>
/// Handler function type for events with a result. TS <c>ExtensionHandler&lt;E, R&gt;</c>:
/// sync handlers return the result directly, async handlers return a task, void handlers return null.
/// </summary>
public delegate Task<R?>? ExtensionHandler<in E, R>(E @event, IExtensionContext ctx);

/// <summary>TS <c>registerFlag</c> 的 type 参数字面量联合。</summary>
public static class FlagType
{
    public const string Boolean = "boolean";

    public const string String = "string";
}

/// <summary>
/// Registration payload for <c>registerCommand</c>. TS
/// <c>Omit&lt;RegisteredCommand, "name" | "sourceInfo"&gt;</c>.
/// </summary>
public sealed record CommandRegistration
{
    public string? Description { get; init; }

    /// <summary>
    /// Argument completions for the command. TS allows a sync or async return; the port always
    /// returns a task (sync implementations wrap with <see cref="Task.FromResult{TResult}"/>).
    /// </summary>
    public Func<string, Task<IReadOnlyList<AutocompleteItem>?>>? GetArgumentCompletions { get; init; }

    public required Func<string, IExtensionCommandContext, Task> Handler { get; init; }
}

/// <summary>TS <c>RegisteredCommand</c>. Not sealed: <see cref="ResolvedCommand"/> extends it.</summary>
public record RegisteredCommand
{
    public required string Name { get; init; }

    public required SourceInfo SourceInfo { get; init; }

    public string? Description { get; init; }

    public Func<string, Task<IReadOnlyList<AutocompleteItem>?>>? GetArgumentCompletions { get; init; }

    public required Func<string, IExtensionCommandContext, Task> Handler { get; init; }
}

/// <summary>TS <c>ResolvedCommand</c>.</summary>
public sealed record ResolvedCommand : RegisteredCommand
{
    public required string InvocationName { get; init; }
}

/// <summary>Registration payload for <c>registerShortcut</c>. TS inline options type.</summary>
public sealed record ShortcutRegistration
{
    public string? Description { get; init; }

    public required Func<IExtensionContext, Task> Handler { get; init; }
}

/// <summary>Registration payload for <c>registerFlag</c>. TS inline options union.</summary>
public sealed record FlagRegistration
{
    public string? Description { get; init; }

    /// <summary><see cref="FlagType.Boolean"/> or <see cref="FlagType.String"/>.</summary>
    public required string Type { get; init; }

    /// <summary><see cref="bool"/> or <see cref="string"/> depending on <see cref="Type"/>.</summary>
    public object? Default { get; init; }
}

/// <summary>TS <c>RegisteredTool</c>.</summary>
public sealed record RegisteredTool
{
    public required ToolDefinition Definition { get; init; }

    public required SourceInfo SourceInfo { get; init; }
}

/// <summary>TS <c>ExtensionFlag</c>.</summary>
public sealed record ExtensionFlag
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Type { get; init; }

    public object? Default { get; init; }

    public required string ExtensionPath { get; init; }
}

/// <summary>TS <c>ExtensionShortcut</c>. <see cref="Shortcut"/> is the TS <c>KeyId</c> (a string).</summary>
public sealed record ExtensionShortcut
{
    public required string Shortcut { get; init; }

    public string? Description { get; init; }

    public required Func<IExtensionContext, Task> Handler { get; init; }

    public required string ExtensionPath { get; init; }
}

/// <summary>
/// Tool info with name, description, parameter schema, prompt guidelines, and source metadata.
/// TS <c>ToolInfo</c> (<c>Pick&lt;ToolDefinition, ...&gt; &amp; {...}</c>).
/// </summary>
public sealed record ToolInfo
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required ToolSchema Parameters { get; init; }

    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public required string Exposure { get; init; }

    public ToolNamespace? Namespace { get; init; }

    public ToolAnnotations? Annotations { get; init; }

    public required SourceInfo SourceInfo { get; init; }
}

/// <summary>User message content: plain text or text/image blocks. TS <c>string | (TextContent | ImageContent)[]</c>.</summary>
public abstract record UserMessageContent
{
    public sealed record Text(string Content) : UserMessageContent;

    public sealed record Blocks(IReadOnlyList<ContentBlock> Content) : UserMessageContent;
}

/// <summary>TS <c>SendMessageHandler</c>.</summary>
public delegate void SendMessageHandler(CustomMessageDraft message, SendMessageOptions? options);

/// <summary>TS <c>SendUserMessageHandler</c>.</summary>
public delegate void SendUserMessageHandler(UserMessageContent content, SendUserMessageOptions? options);

/// <summary>TS <c>AppendEntryHandler</c>.</summary>
public delegate void AppendEntryHandler(string customType, object? data);

/// <summary>TS <c>SetSessionNameHandler</c>.</summary>
public delegate void SetSessionNameHandler(string name);

/// <summary>TS <c>GetSessionNameHandler</c>.</summary>
public delegate string? GetSessionNameHandler();

/// <summary>TS <c>GetActiveToolsHandler</c>.</summary>
public delegate IReadOnlyList<string> GetActiveToolsHandler();

/// <summary>TS <c>GetAllToolsHandler</c>.</summary>
public delegate IReadOnlyList<ToolInfo> GetAllToolsHandler();

/// <summary>TS <c>GetSettingsHandler</c>.</summary>
public delegate Settings GetSettingsHandler();

/// <summary>TS <c>GetCommandsHandler</c>.</summary>
public delegate IReadOnlyList<SlashCommandInfo> GetCommandsHandler();

/// <summary>TS <c>SetActiveToolsHandler</c>.</summary>
public delegate void SetActiveToolsHandler(IReadOnlyList<string> toolNames);

/// <summary>TS <c>RefreshToolsHandler</c>.</summary>
public delegate void RefreshToolsHandler();

/// <summary>TS <c>SetModelHandler</c>.</summary>
public delegate Task<bool> SetModelHandler(Model model);

/// <summary>TS <c>GetThinkingLevelHandler</c>.</summary>
public delegate ThinkingLevel GetThinkingLevelHandler();

/// <summary>TS <c>SetThinkingLevelHandler</c>.</summary>
public delegate void SetThinkingLevelHandler(ThinkingLevel level);

/// <summary>TS <c>SetLabelHandler</c>.</summary>
public delegate void SetLabelHandler(string entryId, string? label);

/// <summary>
/// Chooses how calls to a tool are drawn, including tools that are not registered. TS
/// <c>ToolRendererResolver</c>: <paramref name="next"/> returns the renderers the remaining
/// resolvers, then the registered tool, would use.
/// </summary>
public delegate ToolRenderers? ToolRendererResolver(string toolName, Func<ToolRenderers?> next);

/// <summary>
/// Extension factory function type. Supports both sync and async initialization. TS
/// <c>ExtensionFactory</c>; sync factories return <see cref="Task.CompletedTask"/>.
/// </summary>
public delegate Task ExtensionFactory(IExtensionApi pi);

/// <summary>
/// An inline extension: either a bare factory or a described one. TS <c>InlineExtension</c>.
/// </summary>
public abstract record InlineExtension
{
    /// <summary>Bare factory form.</summary>
    public sealed record Direct(ExtensionFactory Factory) : InlineExtension;

    /// <summary>
    /// Described form. With <paramref name="Builtin"/> the extension is named <c>builtin:name</c>
    /// and loads as an extension resource; otherwise it shows as <c>&lt;inline:name&gt;</c>.
    /// </summary>
    public sealed record Described(
        string Name,
        ExtensionFactory Factory,
        bool Hidden = false,
        bool Replaceable = false,
        bool Builtin = false) : InlineExtension;
}

/// <summary>
/// A virtual model an extension registers with <c>registerVirtualModel()</c>. TS
/// <c>ExtensionVirtualModel&lt;TState&gt;</c>.
/// </summary>
/// <remarks>
/// TS is generic over the router state; the port carries router state as JSON on
/// <see cref="ModelRouteRequest.State"/> / <see cref="ModelRoute.State"/> (the same shape
/// <see cref="VirtualModelDefinition"/> already uses), so no generic parameter is needed.
/// </remarks>
public sealed record ExtensionVirtualModel
{
    public required string Provider { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public IReadOnlyList<string>? ThinkingLevels { get; init; }

    public double? ContextWindow { get; init; }

    public double? MaxTokens { get; init; }

    public IReadOnlyList<string>? Input { get; init; }

    /// <summary>Like <see cref="VirtualModelDefinition.Route"/>, with an extension context.</summary>
    public required Func<ModelRouteRequest, IExtensionContext, Task<ModelRoute>> Route { get; init; }
}

/// <summary>
/// ExtensionAPI passed to extension factory functions. Port of the TS <c>ExtensionAPI</c>.
/// </summary>
/// <remarks>
/// <para>
/// Difference C86: TS types <c>on()</c> as ~35 literal-name overloads. C# has no string-literal
/// types, so the port exposes one generic pair of overloads; the event name must be one of
/// <see cref="ExtensionEventNames"/> and the handler's event type must match it, exactly as in TS.
/// </para>
/// <para>
/// Difference C87: <c>registerProvider(name, config)</c> reuses the already-ported
/// <see cref="ProviderConfigInput"/> (4b) instead of redeclaring the TS <c>ProviderConfig</c>
/// shape; <c>registerProvider(provider)</c> takes the pi-ai <see cref="Provider"/> placeholder
/// until the native provider registration lands.
/// </para>
/// </remarks>
public interface IExtensionApi
{
    // ---- Event subscription ----

    /// <summary>Subscribe to an event whose handler returns no result.</summary>
    Action On<E>(string eventName, ExtensionHandler<E> handler);

    /// <summary>Subscribe to an event whose handler returns a result.</summary>
    Action On<E, R>(string eventName, ExtensionHandler<E, R> handler);

    // ---- Tool registration ----

    /// <summary>Register a tool that the LLM can call.</summary>
    void RegisterTool(ToolDefinition tool);

    // ---- Command, shortcut, flag registration ----

    /// <summary>Register a custom command.</summary>
    void RegisterCommand(string name, CommandRegistration options);

    /// <summary>Register a keyboard shortcut. <paramref name="shortcut"/> is the TS <c>KeyId</c>.</summary>
    void RegisterShortcut(string shortcut, ShortcutRegistration options);

    /// <summary>Register a CLI flag.</summary>
    void RegisterFlag(string name, FlagRegistration options);

    /// <summary>Get the value of a registered CLI flag (bool, string, or null).</summary>
    object? GetFlag(string name);

    // ---- Message rendering ----

    /// <summary>Register a custom renderer for CustomMessageEntry.</summary>
    void RegisterMessageRenderer<T>(string customType, MessageRenderer<T> renderer);

    /// <summary>Register a transformer for user and assistant Markdown before Pi renders it.</summary>
    void RegisterMarkdownTransformer(MarkdownTransformer transformer);

    /// <summary>Register a custom renderer for CustomEntry. Custom entries do not participate in LLM context.</summary>
    void RegisterEntryRenderer<T>(string customType, EntryRenderer<T> renderer);

    /// <summary>Choose how tool calls are drawn. Resolvers run in extension load order.</summary>
    void RegisterToolRenderer(ToolRendererResolver resolver);

    // ---- Actions ----

    /// <summary>Send a custom message to the session.</summary>
    void SendMessage(CustomMessageDraft message, SendMessageOptions? options = null);

    /// <summary>Send a user message to the agent. Always triggers a turn.</summary>
    void SendUserMessage(string content, SendUserMessageOptions? options = null);

    /// <summary>Send a user message with attached images. Always triggers a turn.</summary>
    void SendUserMessage(IReadOnlyList<ContentBlock> content, SendUserMessageOptions? options = null);

    /// <summary>Append a custom entry to the session for state persistence (not sent to LLM).</summary>
    void AppendEntry(string customType, object? data = null);

    // ---- Session metadata ----

    /// <summary>Set the session display name (shown in session selector).</summary>
    void SetSessionName(string name);

    /// <summary>Get the current session name, if set.</summary>
    string? GetSessionName();

    /// <summary>Set or clear a label on an entry.</summary>
    void SetLabel(string entryId, string? label);

    /// <summary>Execute a shell command.</summary>
    Task<ExecResult> Exec(string command, IReadOnlyList<string> args, ExecOptions? options = null);

    /// <summary>Get the names of the active tools, which are the tools declared to the model.</summary>
    IReadOnlyList<string> GetActiveTools();

    /// <summary>Get all configured tools with parameter schema, prompt guidelines, exposure, and source metadata.</summary>
    IReadOnlyList<ToolInfo> GetAllTools();

    /// <summary>Get a copy of the effective settings (global and project settings merged, with overrides).</summary>
    Settings GetSettings();

    /// <summary>
    /// Set the active tools by name. Unknown and <c>hidden</c> tools are ignored. Tools with
    /// <c>codemode</c> or <c>deferred</c> exposure stay callable from codemode scripts whether
    /// active or not.
    /// </summary>
    void SetActiveTools(IReadOnlyList<string> toolNames);

    /// <summary>Get available slash commands in the current session.</summary>
    IReadOnlyList<SlashCommandInfo> GetCommands();

    // ---- Model and thinking level ----

    /// <summary>
    /// Set the model for the current session without changing the configured default for new
    /// sessions. Returns false if authentication is not configured for the model's provider.
    /// </summary>
    Task<bool> SetModel(Model model);

    /// <summary>Get current thinking level.</summary>
    ThinkingLevel GetThinkingLevel();

    /// <summary>Set the thinking level (clamped to model capabilities) for the current session.</summary>
    void SetThinkingLevel(ThinkingLevel level);

    // ---- Provider registration ----

    /// <summary>Register or override a native pi-ai provider.</summary>
    void RegisterProvider(Provider provider);

    /// <summary>Register or override a provider from extension configuration.</summary>
    void RegisterProvider(string name, ProviderConfigInput config);

    /// <summary>Unregister a previously registered provider.</summary>
    void UnregisterProvider(string name);

    // ---- MCP servers ----

    /// <summary>
    /// Register an MCP server for this session, with the same config as an <c>mcpServers</c> entry
    /// in <c>mcp.json</c>.
    /// </summary>
    void RegisterMcpServer(string name, McpServerConfig config);

    /// <summary>Remove an MCP server this extension registered and close its connection.</summary>
    void UnregisterMcpServer(string name);

    /// <summary>Every MCP server registered by extensions. For extensions that connect MCP servers.</summary>
    IReadOnlyList<RegisteredMcpServer> GetMcpServers();

    // ---- Virtual models ----

    /// <summary>Register a virtual model: a selectable catalog entry that routes each request to a physical model.</summary>
    void RegisterVirtualModel(ExtensionVirtualModel model);

    /// <summary>Remove a virtual model registered with <see cref="RegisterVirtualModel"/>.</summary>
    void UnregisterVirtualModel(string provider, string id);

    // ---- Shared event bus ----

    /// <summary>Shared event bus for extension communication.</summary>
    EventBus Events { get; }
}
