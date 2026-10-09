using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Tui;

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// How the model reaches a tool. TS models this as the literal union
/// <c>"direct" | "model-only" | "codemode" | "deferred" | "hidden"</c>; the port keeps
/// <see cref="string"/> with these constants (difference C80's neighbour).
/// </summary>
/// <remarks>
/// <c>direct</c> is declared to the model while active and callable while active; <c>model-only</c> is
/// declared but never callable; <c>codemode</c> is callable whenever registered but only declared when
/// explicitly activated; <c>deferred</c> is like <c>codemode</c> but hidden from codemode listings;
/// <c>hidden</c> is registered but unreachable. <c>direct</c> and <c>model-only</c> activate on
/// registration, the others do not.
/// </remarks>
public static class ToolExposure
{
    public const string Direct = "direct";

    public const string ModelOnly = "model-only";

    public const string Codemode = "codemode";

    public const string Deferred = "deferred";

    public const string Hidden = "hidden";

    /// <summary>The TS default: <c>direct</c> when the member is absent.</summary>
    public const string Default = Direct;
}

/// <summary>Which framing the TUI draws around a tool row. TS: <c>renderShell</c>.</summary>
public static class RenderShellMode
{
    /// <summary>The standard colored shell.</summary>
    public const string Default = "default";

    /// <summary>The tool renders its own framing.</summary>
    public const string Self = "self";
}

/// <summary>
/// Hints about what a tool does, with the meaning of MCP tool annotations. Port of the TS
/// <c>ToolAnnotations</c>.
/// </summary>
public sealed record ToolAnnotations
{
    /// <summary>The tool does not modify its environment.</summary>
    public bool? ReadOnlyHint { get; init; }

    /// <summary>Meaningful when not read-only.</summary>
    public bool? DestructiveHint { get; init; }

    /// <summary>Meaningful when not read-only.</summary>
    public bool? IdempotentHint { get; init; }

    /// <summary>The tool reaches an open world of external entities rather than a closed domain.</summary>
    public bool? OpenWorldHint { get; init; }
}

/// <summary>A group of related tools, such as the tools of one MCP server. Port of the TS <c>ToolNamespace</c>.</summary>
public sealed record ToolNamespace
{
    /// <summary>For example <c>mcp__docs</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Short summary shown once with the group in model-facing tool listings.</summary>
    public string? Description { get; init; }

    /// <summary>Longer usage guidance, such as MCP server instructions. Not part of tool listings.</summary>
    public string? Instructions { get; init; }
}

/// <summary>
/// The tools of a session as <see cref="ToolDefinition.PrepareLoadout"/> sees them. Port of the TS
/// <c>ToolLoadout</c>.
/// </summary>
public interface IToolLoadout
{
    /// <summary>Tools declared to the model (the active tools), in order, with their original descriptions.</summary>
    IReadOnlyList<AgentTool> Declared { get; }

    /// <summary>Tools callable through the nested-tool entry point.</summary>
    IReadOnlyList<AgentTool> Callable { get; }

    /// <summary>Every registered tool.</summary>
    IReadOnlyList<AgentTool> Registered { get; }

    string GetExposure(string name);

    ToolNamespace? GetNamespace(string name);
}

/// <summary>Changes <see cref="ToolDefinition.PrepareLoadout"/> makes to what the model sees.</summary>
public sealed record ToolLoadoutChanges
{
    /// <summary>Model-facing descriptions of declared tools, by tool name.</summary>
    public IReadOnlyDictionary<string, string>? Descriptions { get; init; }

    /// <summary>
    /// Declared tools whose declarations requests leave out. They stay active and callable, and the
    /// transcript still declares them, so the active set survives <c>/tree</c> and resume.
    /// </summary>
    public IReadOnlyList<string>? HiddenDeclarations { get; init; }
}

/// <summary>Rendering options for tool results. Port of the TS <c>ToolRenderResultOptions</c>.</summary>
public sealed record ToolRenderResultOptions(bool Expanded, bool IsPartial);

/// <summary>Context passed to tool renderers. Port of the TS <c>ToolRenderContext</c>.</summary>
/// <remarks>
/// The TS is generic over <c>TState</c>/<c>TArgs</c>; the port carries the shared renderer state as
/// <see cref="object"/> and the raw arguments as a JSON-shaped dictionary, since C# has no structural
/// typing to keep a renderer's state type aligned with its tool (the same trade-off as C62).
/// </remarks>
public sealed record ToolRenderContext
{
    /// <summary>Current tool call arguments. Shared across call/result renders for the same call.</summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; } = new Dictionary<string, object?>();

    /// <summary>Unique id for this tool execution. Stable across call/result renders.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Invalidate just this tool execution component for redraw.</summary>
    public required Action Invalidate { get; init; }

    /// <summary>Previously returned component for this render slot, if any.</summary>
    public IComponent? LastComponent { get; init; }

    /// <summary>Shared renderer state for this tool row.</summary>
    public object? State { get; init; }

    /// <summary>Working directory for this tool execution.</summary>
    public required string Cwd { get; init; }

    public bool ExecutionStarted { get; init; }

    public bool ArgsComplete { get; init; }

    public bool IsPartial { get; init; }

    public bool Expanded { get; init; }

    /// <summary>Whether inline images are currently shown in the TUI.</summary>
    public bool ShowImages { get; init; }

    public bool IsError { get; init; }
}

/// <summary>
/// The slice of TS <c>ExtensionToolContext</c> that the built-in tools actually read. Difference C84.
/// </summary>
/// <remarks>
/// <para>
/// The full context (<c>core/extensions/types.ts</c>) carries the UI surface, the session manager, the
/// model registry and the nested-tool entry point, all of which belong to later subphases (4d/4e/4f).
/// The tools need exactly five members — <c>cwd</c>, <c>model</c>, <c>thinkingLevel</c>, and the session
/// id/file pair — so the port declares them here and lets the full context implement the interface later,
/// the same device as <c>IModelResolverRuntime</c> (difference C81). It also keeps the tool tests free
/// of the session runtime.
/// </para>
/// <para>
/// TS passes <c>ctx</c> as <c>ExtensionToolContext</c> but every built-in tool writes it optional
/// (<c>ctx?:</c>) because <c>wrapToolDefinition</c> may have no context factory. The port therefore
/// passes <see cref="IToolContext"/>? and each tool falls back to its own <c>cwd</c>.
/// </para>
/// </remarks>
public interface IToolContext
{
    /// <summary>Current working directory.</summary>
    string Cwd { get; }

    /// <summary>Current model, when the session has one.</summary>
    ModelSpec? Model { get; }

    /// <summary>Current thinking level, when provided by the session runtime.</summary>
    string? ThinkingLevel { get; }

    /// <summary>The TS <c>sessionManager.getSessionId()</c>.</summary>
    string SessionId { get; }

    /// <summary>The TS <c>sessionManager.getSessionFile()</c>.</summary>
    string? SessionFile { get; }
}

/// <summary>Helpers shared by tools that read an optional <see cref="IToolContext"/>.</summary>
public static class ToolContexts
{
    /// <summary>
    /// The TS <c>ctx?.cwd || cwd</c>: an absent context <em>or an empty cwd</em> falls back to the
    /// tool's own working directory, because JS <c>||</c> is a truthiness test.
    /// </summary>
    public static string ResolveCwd(IToolContext? context, string fallback) =>
        !string.IsNullOrEmpty(context?.Cwd) ? context.Cwd : fallback;
}

/// <summary>
/// Tool definition for <c>registerTool()</c>. Port of the TS <c>ToolDefinition</c>
/// (<c>core/extensions/types.ts</c>).
/// </summary>
/// <remarks>
/// <para>
/// The TS is generic over the TypeBox parameter schema, the details payload and the renderer state;
/// the port collapses the first two (parameters travel as a JSON schema, details as <see cref="object"/>)
/// for the same reason as C62, and carries the renderer state as <see cref="object"/> (see
/// <see cref="ToolRenderContext"/>).
/// </para>
/// <para>
/// Difference C85: <c>renderCall</c> / <c>renderResult</c> are not declared yet. Both take a
/// <c>Theme</c> (<c>modes/interactive/theme/theme.ts</c>), which belongs to 4f, so the renderer half of
/// the tool definitions lands with the renderers themselves. <c>renderShell</c> is declarable today and
/// is kept.
/// </para>
/// </remarks>
public sealed record ToolDefinition
{
    /// <summary>Tool name (used in LLM tool calls).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable label for UI.</summary>
    public required string Label { get; init; }

    /// <summary>Description for LLM.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Optional one-line snippet for the Available tools section in the default system prompt. Custom
    /// tools are omitted from that section when this is not provided.
    /// </summary>
    public string? PromptSnippet { get; init; }

    /// <summary>Guideline bullets appended to the default system prompt while this tool is active.</summary>
    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    /// <summary>Parameter schema.</summary>
    public required ToolSchema Parameters { get; init; }

    /// <summary>
    /// Provider-side constrained sampling request. TS also allows <c>false</c> to explicitly disable it,
    /// which the docs call equivalent to leaving it undefined, so the port collapses both to null.
    /// </summary>
    public JsonObject? ConstrainedSampling { get; init; }

    /// <summary><see cref="RenderShellMode.Default"/> or <see cref="RenderShellMode.Self"/>.</summary>
    public string? RenderShell { get; init; }

    /// <summary>
    /// Compatibility shim to prepare raw tool call arguments before schema validation. Must return an
    /// object conforming to <see cref="Parameters"/>.
    /// </summary>
    public Func<object?, object?>? PrepareArguments { get; init; }

    /// <summary>
    /// JSON Schema of <c>structuredContent</c> in successful results. Tools that declare it should always
    /// set <c>structuredContent</c>; codemode scripts then receive it instead of the text content.
    /// </summary>
    public ToolSchema? OutputSchema { get; init; }

    /// <summary>How the model reaches the tool. Default: <see cref="ToolExposure.Direct"/>.</summary>
    public string? Exposure { get; init; }

    /// <summary>Group the tool belongs to, for example its MCP server.</summary>
    public ToolNamespace? Namespace { get; init; }

    /// <summary>Hints about what the tool does, for example from an MCP server.</summary>
    public ToolAnnotations? Annotations { get; init; }

    /// <summary>
    /// Whether registering the tool activates it. TS default: true for <c>direct</c> and
    /// <c>model-only</c> tools; other exposures are never activated on registration.
    /// </summary>
    public bool? DefaultActive { get; init; }

    /// <summary>
    /// Adjust how the loadout is presented to the model while this tool is active. Called whenever the
    /// active tools change.
    /// </summary>
    public Func<IToolLoadout, ToolLoadoutChanges?>? PrepareLoadout { get; init; }

    /// <summary>Per-tool execution mode override. Null means the default mode applies.</summary>
    public ToolExecutionMode? ExecutionMode { get; init; }

    /// <summary>Executes the tool.</summary>
    public required Func<
        string,
        IReadOnlyDictionary<string, object?>,
        CancellationToken,
        AgentToolUpdateCallback?,
        IToolContext?,
        Task<AgentToolResult>> Execute { get; init; }
}

/// <summary>
/// The renderer triple a tool may supply. Port of the TS <c>ToolRenderers</c>
/// (<c>Pick&lt;AnyToolDefinition, "renderShell" | "renderCall" | "renderResult"&gt;</c>).
/// </summary>
/// <remarks>Only <see cref="RenderShell"/> exists until 4f; see difference C85.</remarks>
public sealed record ToolRenderers
{
    public string? RenderShell { get; init; }
}

/// <summary>Creates the context for one tool call. Port of the TS <c>ToolContextFactory</c>.</summary>
public delegate IToolContext ToolContextFactory(string toolCallId, CancellationToken signal);
