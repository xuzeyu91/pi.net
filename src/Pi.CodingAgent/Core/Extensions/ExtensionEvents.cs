using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.CodingAgent.Core.Tools;
using Pi.Tui;

namespace Pi.CodingAgent.Core.Extensions;

// ============================================================================
// Extension events — port of core/extensions/types.ts (4d-1)
// ============================================================================
//
// TS models the event surface as string-discriminated unions (`type: "session_start"` etc.).
// The port follows the Pi.Agent/Pi.Ai convention (AgentEvent / AssistantMessageEvent): one
// abstract record per union with the variants as nested records, so `switch` pattern matching
// replaces the TS type guards. The wire names live in <see cref="ExtensionEventNames"/> for the
// event bus (4d-2) and for `ExtensionAPI.On` overloads.

/// <summary>TS <c>UIPromptKind</c> 字符串字面量联合。</summary>
public static class UiPromptKind
{
    public const string Select = "select";

    public const string Confirm = "confirm";

    public const string Input = "input";

    public const string Editor = "editor";

    public const string Custom = "custom";
}

/// <summary>TS <c>ModelSelectSource</c> 字符串字面量联合。</summary>
public static class ModelSelectSource
{
    public const string Set = "set";

    public const string Cycle = "cycle";

    public const string Restore = "restore";
}

/// <summary>TS <c>InputSource</c> 字符串字面量联合。</summary>
public static class InputSource
{
    public const string Interactive = "interactive";

    public const string Rpc = "rpc";

    public const string Extension = "extension";
}

/// <summary>TS <c>AgentActivityOutcome</c> 字符串字面量联合。</summary>
public static class AgentActivityOutcome
{
    public const string Completed = "completed";

    public const string Aborted = "aborted";

    public const string Error = "error";
}

/// <summary>TS <c>ProjectTrustEventDecision</c> 字符串字面量联合。</summary>
public static class ProjectTrustDecision
{
    public const string Yes = "yes";

    public const string No = "no";

    public const string Undecided = "undecided";
}

/// <summary>Session 事件 reason 字段的字面量联合（start / shutdown 共用取值）。</summary>
public static class SessionEventReason
{
    public const string Startup = "startup";

    public const string Reload = "reload";

    public const string New = "new";

    public const string Resume = "resume";

    public const string Fork = "fork";

    public const string Quit = "quit";
}

/// <summary>Compaction 触发原因字面量联合。</summary>
public static class CompactReason
{
    public const string Manual = "manual";

    public const string Threshold = "threshold";

    public const string Overflow = "overflow";
}

/// <summary>
/// The wire name of every extension event. TS dispatches on the literal <c>type</c> field; the C#
/// event bus (4d-2) and <see cref="ExtensionApi.On"/> overloads use these constants so extension
/// source stays compatible.
/// </summary>
public static class ExtensionEventNames
{
    public const string ProjectTrust = "project_trust";
    public const string ResourcesDiscover = "resources_discover";
    public const string McpServersChange = "mcp_servers_change";
    public const string SessionStart = "session_start";
    public const string SessionInfoChanged = "session_info_changed";
    public const string SessionBeforeSwitch = "session_before_switch";
    public const string SessionBeforeFork = "session_before_fork";
    public const string SessionBeforeCompact = "session_before_compact";
    public const string SessionCompact = "session_compact";
    public const string SessionCompactFailed = "session_compact_failed";
    public const string SessionShutdown = "session_shutdown";
    public const string SessionBeforeTree = "session_before_tree";
    public const string SessionTree = "session_tree";
    public const string Context = "context";
    public const string ContextWithSystem = "context_with_system";
    public const string CacheWarmingDecision = "cache_warming_decision";
    public const string BeforeProviderRequest = "before_provider_request";
    public const string BeforeProviderHeaders = "before_provider_headers";
    public const string AfterProviderResponse = "after_provider_response";
    public const string ProviderStreamEvent = "provider_stream_event";
    public const string BeforeAgentStart = "before_agent_start";
    public const string AgentStart = "agent_start";
    public const string AgentEnd = "agent_end";
    public const string AgentBeforeSettle = "agent_before_settle";
    public const string AgentSettled = "agent_settled";
    public const string UiPromptStart = "ui_prompt_start";
    public const string UiPromptEnd = "ui_prompt_end";
    public const string TurnStart = "turn_start";
    public const string TurnEnd = "turn_end";
    public const string MessageStart = "message_start";
    public const string MessageUpdate = "message_update";
    public const string MessageEnd = "message_end";
    public const string ToolExecutionStart = "tool_execution_start";
    public const string ToolExecutionUpdate = "tool_execution_update";
    public const string ToolExecutionEnd = "tool_execution_end";
    public const string ModelSelect = "model_select";
    public const string ThinkingLevelSelect = "thinking_level_select";
    public const string UserBash = "user_bash";
    public const string Input = "input";
    public const string ToolCall = "tool_call";
    public const string ToolResult = "tool_result";
}

/// <summary>Marker for the TS <c>SessionEvent</c> subgroup of <see cref="ExtensionEvent"/>.</summary>
public interface ISessionEvent;

/// <summary>Marker for the TS <c>ToolCallEvent</c> subgroup of <see cref="ExtensionEvent"/>.</summary>
public interface IToolCallEvent;

/// <summary>Marker for the TS <c>ToolResultEvent</c> subgroup of <see cref="ExtensionEvent"/>.</summary>
public interface IToolResultEvent;

/// <summary>
/// Union of all extension event types. Port of the TS <c>ExtensionEvent</c> union; the variants
/// are nested records (the Pi.Agent <c>AgentEvent</c> convention).
/// </summary>
public abstract record ExtensionEvent
{
    protected ExtensionEvent()
    {
    }

    // ---- Startup / resource events ----

    /// <summary>TS <c>ProjectTrustEvent</c>.</summary>
    public sealed record ProjectTrust(string Cwd) : ExtensionEvent;

    /// <summary>TS <c>ResourcesDiscoverEvent</c>. <paramref name="Reason"/> is "startup" or "reload".</summary>
    public sealed record ResourcesDiscover(string Cwd, string Reason) : ExtensionEvent;

    /// <summary>TS <c>McpServersChangeEvent</c>.</summary>
    public sealed record McpServersChange(IReadOnlyList<RegisteredMcpServer> Servers) : ExtensionEvent;

    // ---- Session events ----

    /// <summary>TS <c>SessionStartEvent</c>. <paramref name="Reason"/> is a <see cref="SessionEventReason"/> value.</summary>
    public sealed record SessionStart(string Reason, string? PreviousSessionFile = null) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionInfoChangedEvent</c>. <paramref name="Name"/> is null when cleared.</summary>
    public sealed record SessionInfoChanged(string? Name) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionBeforeSwitchEvent</c>. Can be cancelled.</summary>
    public sealed record SessionBeforeSwitch(string Reason, string? TargetSessionFile = null) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionBeforeForkEvent</c>. Can be cancelled.</summary>
    public sealed record SessionBeforeFork(string EntryId, string Position) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionBeforeCompactEvent</c>. Can be cancelled or customized.</summary>
    public sealed record SessionBeforeCompact(
        CompactionPreparation Preparation,
        IReadOnlyList<SessionEntry> BranchEntries,
        string? CustomInstructions,
        string Reason,
        bool WillRetry,
        CancellationToken Signal) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionCompactEvent</c>.</summary>
    public sealed record SessionCompact(
        CompactionEntry CompactionEntry,
        bool FromExtension,
        string Reason,
        bool WillRetry) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionCompactFailedEvent</c>.</summary>
    public sealed record SessionCompactFailed(
        string Reason,
        string? ErrorMessage,
        bool Aborted,
        bool WillRetry,
        bool FromExtension) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionShutdownEvent</c>.</summary>
    public sealed record SessionShutdown(string Reason, string? TargetSessionFile = null) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionBeforeTreeEvent</c>. Can be cancelled.</summary>
    public sealed record SessionBeforeTree(TreePreparation Preparation, CancellationToken Signal) : ExtensionEvent, ISessionEvent;

    /// <summary>TS <c>SessionTreeEvent</c>.</summary>
    public sealed record SessionTree(
        string? NewLeafId,
        string? OldLeafId,
        BranchSummaryEntry? SummaryEntry = null,
        bool FromExtension = false) : ExtensionEvent, ISessionEvent;

    // ---- Agent events ----

    /// <summary>TS <c>ContextEvent</c>. Fired before each LLM call; can modify messages.</summary>
    public sealed record ContextEvent(IReadOnlyList<ChatMessage> Messages) : ExtensionEvent;

    /// <summary>TS <c>ContextWithSystemEvent</c>.</summary>
    public sealed record ContextWithSystemEvent(IReadOnlyList<ChatMessage> Messages) : ExtensionEvent;

    /// <summary>TS <c>CacheWarmingDecisionEvent</c> — 4e 占位，见 <see cref="Types.CacheWarmingDecisionEvent"/>。</summary>
    // (cache-warmer.ts 尚未移植；占位事件记录在 Types/Placeholders.cs)
    /// <summary>TS <c>BeforeProviderRequestEvent</c>. Can replace the payload.</summary>
    public sealed record BeforeProviderRequest(object? Payload) : ExtensionEvent;

    /// <summary>TS <c>BeforeProviderHeadersEvent</c>. Handlers mutate <paramref name="Headers"/> in place.</summary>
    public sealed record BeforeProviderHeaders(ProviderHeaders Headers) : ExtensionEvent;

    /// <summary>TS <c>AfterProviderResponseEvent</c>.</summary>
    public sealed record AfterProviderResponse(int Status, IReadOnlyDictionary<string, string> Headers) : ExtensionEvent;

    /// <summary>TS <c>ProviderStreamEvent</c>.</summary>
    public sealed record ProviderStream(string Provider, string Api, string Model, object? Data) : ExtensionEvent;

    /// <summary>TS <c>BeforeAgentStartEvent</c>.</summary>
    public sealed record BeforeAgentStart(
        string Prompt,
        IReadOnlyList<ImageContent>? Images,
        string SystemPrompt,
        NormalizedBuildSystemPromptOptions SystemPromptOptions) : ExtensionEvent;

    /// <summary>TS <c>AgentStartEvent</c>.</summary>
    public sealed record AgentStart : ExtensionEvent;

    /// <summary>TS <c>AgentEndEvent</c>.</summary>
    public sealed record AgentEnd(IReadOnlyList<ChatMessage> Messages) : ExtensionEvent;

    /// <summary>TS <c>AgentBeforeSettleEvent</c> (extends <see cref="BoundaryState"/>).</summary>
    public sealed record AgentBeforeSettle : BoundaryState;

    /// <summary>TS <c>AgentSettledEvent</c>.</summary>
    public sealed record AgentSettled : ExtensionEvent;

    /// <summary>TS <c>UIPromptStartEvent</c>.</summary>
    public sealed record UiPromptStart(string Kind, string? Title = null) : ExtensionEvent;

    /// <summary>TS <c>UIPromptEndEvent</c>.</summary>
    public sealed record UiPromptEnd(string Kind, string? Title = null) : ExtensionEvent;

    /// <summary>TS <c>TurnStartEvent</c>.</summary>
    public sealed record TurnStart(int TurnIndex, double Timestamp) : ExtensionEvent;

    /// <summary>TS <c>TurnEndEvent</c> (extends <see cref="BoundaryState"/>).</summary>
    public sealed record TurnEnd : BoundaryState
    {
        public int TurnIndex { get; init; }

        public ChatMessage? Message { get; init; }

        public IReadOnlyList<ToolResultMessage> ToolResults { get; init; } = Array.Empty<ToolResultMessage>();

        public string? MessageEntryId { get; init; }

        public IReadOnlyList<string> ToolResultEntryIds { get; init; } = Array.Empty<string>();
    }

    /// <summary>TS <c>MessageStartEvent</c>.</summary>
    public sealed record MessageStart(ChatMessage Message) : ExtensionEvent;

    /// <summary>TS <c>MessageUpdateEvent</c>.</summary>
    public sealed record MessageUpdate(ChatMessage Message, AssistantMessageEvent AssistantMessageEvent) : ExtensionEvent;

    /// <summary>TS <c>MessageEndEvent</c>.</summary>
    public sealed record MessageEnd(ChatMessage Message) : ExtensionEvent;

    /// <summary>TS <c>ToolExecutionStartEvent</c>.</summary>
    public sealed record ToolExecutionStart(
        string ToolCallId,
        string ToolName,
        object? Args,
        string? ParentToolCallId = null) : ExtensionEvent;

    /// <summary>TS <c>ToolExecutionUpdateEvent</c>.</summary>
    public sealed record ToolExecutionUpdate(
        string ToolCallId,
        string ToolName,
        object? Args,
        object? PartialResult,
        string? ParentToolCallId = null) : ExtensionEvent;

    /// <summary>TS <c>ToolExecutionEndEvent</c>.</summary>
    public sealed record ToolExecutionEnd(
        string ToolCallId,
        string ToolName,
        object? Result,
        bool IsError,
        double? DurationMs = null,
        string? ParentToolCallId = null) : ExtensionEvent;

    // ---- Model events ----

    /// <summary>TS <c>ModelSelectEvent</c>. <paramref name="Source"/> is a <see cref="ModelSelectSource"/> value.</summary>
    public sealed record ModelSelect(Model Model, Model? PreviousModel, string Source) : ExtensionEvent;

    /// <summary>TS <c>ThinkingLevelSelectEvent</c>.</summary>
    public sealed record ThinkingLevelSelect(ThinkingLevel Level, ThinkingLevel PreviousLevel) : ExtensionEvent;

    // ---- User bash events ----

    /// <summary>TS <c>UserBashEvent</c>.</summary>
    public sealed record UserBash(string Command, bool ExcludeFromContext, string Cwd) : ExtensionEvent;

    // ---- Input events ----

    /// <summary>TS <c>InputEvent</c> (wire name <c>"input"</c>).</summary>
    public sealed record InputEvent(
        string Text,
        IReadOnlyList<ImageContent>? Images,
        string Source,
        string? StreamingBehavior = null) : ExtensionEvent;

    // ---- Tool call events (TS ToolCallEvent union) ----

    /// <summary>TS <c>BashToolCallEvent</c>.</summary>
    public sealed record BashToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>PowerShellToolCallEvent</c>.</summary>
    public sealed record PowerShellToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>ReadToolCallEvent</c>.</summary>
    public sealed record ReadToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>EditToolCallEvent</c>.</summary>
    public sealed record EditToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>WriteToolCallEvent</c>.</summary>
    public sealed record WriteToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>GrepToolCallEvent</c>.</summary>
    public sealed record GrepToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>FindToolCallEvent</c>.</summary>
    public sealed record FindToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>LsToolCallEvent</c>.</summary>
    public sealed record LsToolCall(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    /// <summary>TS <c>CustomToolCallEvent</c>.</summary>
    public sealed record CustomToolCall(
        string ToolCallId,
        string ToolName,
        IReadOnlyDictionary<string, object?> Input,
        string? ParentToolCallId = null) : ExtensionEvent, IToolCallEvent;

    // ---- Tool result events (TS ToolResultEvent union) ----

    /// <summary>TS <c>BashToolResultEvent</c>.</summary>
    public sealed record BashToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        BashToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>PowerShellToolResultEvent</c>.</summary>
    public sealed record PowerShellToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        BashToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>ReadToolResultEvent</c>.</summary>
    public sealed record ReadToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        ReadToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>EditToolResultEvent</c>.</summary>
    public sealed record EditToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        EditToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>WriteToolResultEvent</c>. TS types <c>details</c> as always-undefined, so the port omits it.</summary>
    public sealed record WriteToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>GrepToolResultEvent</c>.</summary>
    public sealed record GrepToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        GrepToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>FindToolResultEvent</c>.</summary>
    public sealed record FindToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        FindToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>LsToolResultEvent</c>.</summary>
    public sealed record LsToolResult(
        string ToolCallId,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        LsToolDetails? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;

    /// <summary>TS <c>CustomToolResultEvent</c>.</summary>
    public sealed record CustomToolResult(
        string ToolCallId,
        string ToolName,
        IReadOnlyDictionary<string, object?> Input,
        IReadOnlyList<ContentBlock> Content,
        bool IsError,
        object? Details = null,
        JsonNode? StructuredContent = null,
        Usage? UsageStats = null,
        string? ParentToolCallId = null) : ExtensionEvent, IToolResultEvent;
}

/// <summary>Preparation data for tree navigation. TS <c>TreePreparation</c>.</summary>
public sealed record TreePreparation
{
    public required string TargetId { get; init; }

    public string? OldLeafId { get; init; }

    public string? CommonAncestorId { get; init; }

    public IReadOnlyList<SessionEntry> EntriesToSummarize { get; init; } = Array.Empty<SessionEntry>();

    public bool UserWantsSummary { get; init; }

    public string? CustomInstructions { get; init; }

    public bool? ReplaceInstructions { get; init; }

    public string? Label { get; init; }
}

/// <summary>TS <c>CustomEntryDraft</c>.</summary>
public sealed record CustomEntryDraft
{
    public required string CustomType { get; init; }

    public object? Data { get; init; }
}

/// <summary>TS <c>CustomMessageEntryDraft</c>.</summary>
public sealed record CustomMessageEntryDraft
{
    public required string CustomType { get; init; }

    public required MessageContent Content { get; init; }

    public bool Display { get; init; }

    public object? Details { get; init; }
}

/// <summary>TS <c>ContextEditEntryDraft</c>.</summary>
public sealed record ContextEditEntryDraft
{
    public required string TargetId { get; init; }

    public object? Replacement { get; init; }
}

/// <summary>TS <c>CompactionEntryDraft</c>.</summary>
public sealed record CompactionEntryDraft
{
    public required string Summary { get; init; }

    /// <summary>Null creates a self-retaining compaction that keeps no preceding entries.</summary>
    public string? FirstKeptEntryId { get; init; }

    public object? Details { get; init; }

    public Usage? UsageStats { get; init; }
}

/// <summary>TS <c>SessionBoundaryDraft</c> union.</summary>
public abstract record SessionBoundaryDraft
{
    public sealed record Custom(CustomEntryDraft Draft) : SessionBoundaryDraft;

    public sealed record CustomMessage(CustomMessageEntryDraft Draft) : SessionBoundaryDraft;

    public sealed record ContextEdit(ContextEditEntryDraft Draft) : SessionBoundaryDraft;

    public sealed record Compaction(CompactionEntryDraft Draft) : SessionBoundaryDraft;
}

/// <summary>TS <c>BoundaryContextPreview</c>.</summary>
public sealed record BoundaryContextPreview
{
    public IReadOnlyList<ProjectedSessionEntry> ContextEntries { get; init; } = Array.Empty<ProjectedSessionEntry>();

    public IReadOnlyList<ChatMessage> ContextMessages { get; init; } = Array.Empty<ChatMessage>();

    public IReadOnlyList<ChatMessage> LlmMessages { get; init; } = Array.Empty<ChatMessage>();

    public IReadOnlyList<ChatMessage> PendingMessages { get; init; } = Array.Empty<ChatMessage>();

    public bool CanContinue { get; init; }
}

/// <summary>
/// TS <c>BoundaryState</c>. It is itself a member of the <see cref="ExtensionEvent"/> union
/// (<c>AgentBeforeSettleEvent</c> and <c>TurnEndEvent</c> extend it), so the port derives from
/// <see cref="ExtensionEvent"/> to keep those two variants inside the union.
/// </summary>
/// <remarks>
/// The defaults mirror what the TS runtime passes when it builds the state: no entries, continue,
/// and a <c>completed</c> outcome.
/// </remarks>
public record BoundaryState : ExtensionEvent
{
    public IReadOnlyList<SessionBoundaryDraft> Entries { get; init; } = Array.Empty<SessionBoundaryDraft>();

    public bool Continue { get; init; } = true;

    public BoundaryContextPreview? Context { get; init; }

    public string Outcome { get; init; } = AgentActivityOutcome.Completed;
}

/// <summary>TS <c>BoundaryResult</c>.</summary>
public sealed record BoundaryResult
{
    public IReadOnlyList<SessionBoundaryDraft>? Entries { get; init; }

    public bool? Continue { get; init; }
}

// ============================================================================
// Event results — TS "Event Results" section
// ============================================================================

/// <summary>TS <c>ContextEventResult</c>.</summary>
public sealed record ContextEventResult
{
    public IReadOnlyList<ChatMessage>? Messages { get; init; }
}

/// <summary>TS <c>ToolCallEventResult</c>.</summary>
public sealed record ToolCallEventResult
{
    /// <summary>Block tool execution. To modify arguments, mutate the event input in place instead.</summary>
    public bool? Block { get; init; }

    public string? Reason { get; init; }

    /// <summary>Hint that the agent should stop after the current tool batch when this call is blocked.</summary>
    public bool? Terminate { get; init; }
}

/// <summary>TS <c>UserBashEventResult</c> union (operations XOR result).</summary>
public abstract record UserBashEventResult
{
    public sealed record WithOperations(BashOperations Operations) : UserBashEventResult;

    public sealed record WithResult(BashResult Result) : UserBashEventResult;
}

/// <summary>TS <c>ToolResultEventResult</c>.</summary>
public sealed record ToolResultEventResult
{
    public IReadOnlyList<ContentBlock>? Content { get; init; }

    public object? Details { get; init; }

    public JsonNode? StructuredContent { get; init; }

    public bool? IsError { get; init; }

    public Usage? UsageStats { get; init; }
}

/// <summary>TS <c>MessageEndEventResult</c>.</summary>
public sealed record MessageEndEventResult
{
    public ChatMessage? Message { get; init; }
}

/// <summary>TS <c>BeforeAgentStartEventResult</c>.</summary>
public sealed record BeforeAgentStartEventResult
{
    public CustomMessageDraft? Message { get; init; }

    public string? SystemPrompt { get; init; }
}

/// <summary>TS <c>SessionBeforeSwitchResult</c>.</summary>
public sealed record SessionBeforeSwitchResult
{
    public bool? Cancel { get; init; }
}

/// <summary>TS <c>SessionBeforeForkResult</c>.</summary>
public sealed record SessionBeforeForkResult
{
    public bool? Cancel { get; init; }

    public bool? SkipConversationRestore { get; init; }
}

/// <summary>TS <c>SessionBeforeCompactResult</c>.</summary>
public sealed record SessionBeforeCompactResult
{
    public bool? Cancel { get; init; }

    public CompactionResult? Compaction { get; init; }
}

/// <summary>TS <c>SessionBeforeTreeResult</c>.</summary>
public sealed record SessionBeforeTreeResult
{
    public bool? Cancel { get; init; }

    public TreeSummaryOverride? Summary { get; init; }

    public string? CustomInstructions { get; init; }

    public bool? ReplaceInstructions { get; init; }

    public string? Label { get; init; }
}

/// <summary>TS <c>SessionBeforeTreeResult.summary</c>.</summary>
public sealed record TreeSummaryOverride
{
    public required string Summary { get; init; }

    public object? Details { get; init; }

    public Usage? UsageStats { get; init; }
}

/// <summary>TS <c>InputEventResult</c> union.</summary>
public abstract record InputEventResult
{
    public sealed record Continue : InputEventResult;

    public sealed record Transform(string Text, IReadOnlyList<ImageContent>? Images = null) : InputEventResult;

    public sealed record Handled : InputEventResult;
}

/// <summary>TS <c>ProjectTrustEventResult</c>.</summary>
public sealed record ProjectTrustEventResult
{
    /// <summary><see cref="ProjectTrustDecision"/> value.</summary>
    public required string Trusted { get; init; }

    public bool? Remember { get; init; }
}

/// <summary>TS <c>ProjectTrustContext</c>.</summary>
public sealed record ProjectTrustContext
{
    public required string Cwd { get; init; }

    public required string Mode { get; init; }

    public required bool HasUI { get; init; }

    public required IExtensionUIContext Ui { get; init; }
}

/// <summary>TS <c>ProjectTrustHandler</c>.</summary>
public delegate Task<ProjectTrustEventResult> ProjectTrustHandler(ExtensionEvent.ProjectTrust @event, ProjectTrustContext ctx);

/// <summary>TS <c>ResourcesDiscoverResult</c>.</summary>
public sealed record ResourcesDiscoverResult
{
    public IReadOnlyList<string>? SkillPaths { get; init; }

    public IReadOnlyList<string>? PromptPaths { get; init; }

    public IReadOnlyList<string>? ThemePaths { get; init; }
}

// ============================================================================
// Message and entry rendering — TS "Message and Entry Rendering" section
// ============================================================================

/// <summary>TS <c>MessageRenderOptions</c>.</summary>
public sealed record MessageRenderOptions
{
    public bool Expanded { get; init; }

    /// <summary>Horizontal padding configured by the outputPad setting.</summary>
    public int OutputPad { get; init; }
}

/// <summary>TS <c>MarkdownTransformContext</c>.</summary>
public sealed record MarkdownTransformContext
{
    /// <summary>"user", "assistant" or "assistant-thinking".</summary>
    public required string MessageType { get; init; }

    public bool IsStreaming { get; init; }

    public int AvailableWidth { get; init; }
}

/// <summary>TS <c>MarkdownTransformer</c>.</summary>
public delegate string MarkdownTransformer(string markdown, MarkdownTransformContext context);

/// <summary>TS <c>EntryRenderOptions</c>.</summary>
public sealed record EntryRenderOptions
{
    public bool Expanded { get; init; }
}

/// <summary>TS <c>MessageRenderer&lt;T&gt;</c>.</summary>
/// <remarks>
/// TS 的 <c>T</c> 只用于把 <c>message.details</c> 收窄成调用方声明的类型；端口按 C88 的约定
/// 让 <see cref="CustomMessage.Details"/> 承载 <c>object?</c>，故这里去掉该类型参数。
/// </remarks>
public delegate IComponent? MessageRenderer(CustomMessage message, MessageRenderOptions options, Theme theme);

/// <summary>TS <c>EntryRenderer&lt;T&gt;</c>.</summary>
public delegate IComponent? EntryRenderer<T>(CustomEntry<T> entry, EntryRenderOptions options, Theme theme);

// ============================================================================
// Type guards — TS isBashToolResult / isToolCallEventType family
// ============================================================================

/// <summary>
/// Ports of the TS type guards. C# callers normally pattern-match directly
/// (<c>e is ExtensionEvent.BashToolResult</c>); these keep the TS call shape for ported code.
/// </summary>
public static class ExtensionEventGuards
{
    public static bool IsBashToolResult(ExtensionEvent e) => e is ExtensionEvent.BashToolResult;

    public static bool IsPowerShellToolResult(ExtensionEvent e) => e is ExtensionEvent.PowerShellToolResult;

    public static bool IsReadToolResult(ExtensionEvent e) => e is ExtensionEvent.ReadToolResult;

    public static bool IsEditToolResult(ExtensionEvent e) => e is ExtensionEvent.EditToolResult;

    public static bool IsWriteToolResult(ExtensionEvent e) => e is ExtensionEvent.WriteToolResult;

    public static bool IsGrepToolResult(ExtensionEvent e) => e is ExtensionEvent.GrepToolResult;

    public static bool IsFindToolResult(ExtensionEvent e) => e is ExtensionEvent.FindToolResult;

    public static bool IsLsToolResult(ExtensionEvent e) => e is ExtensionEvent.LsToolResult;

    /// <summary>TS <c>isToolCallEventType(toolName, event)</c>.</summary>
    public static bool IsToolCallEventType(string toolName, ExtensionEvent e) => e switch
    {
        ExtensionEvent.BashToolCall => toolName == "bash",
        ExtensionEvent.PowerShellToolCall => toolName == "powershell",
        ExtensionEvent.ReadToolCall => toolName == "read",
        ExtensionEvent.EditToolCall => toolName == "edit",
        ExtensionEvent.WriteToolCall => toolName == "write",
        ExtensionEvent.GrepToolCall => toolName == "grep",
        ExtensionEvent.FindToolCall => toolName == "find",
        ExtensionEvent.LsToolCall => toolName == "ls",
        ExtensionEvent.CustomToolCall custom => custom.ToolName == toolName,
        _ => false,
    };
}
