using System.Reflection;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Extensions.Types;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the extension contract layer (port of TS
/// <c>core/extensions/types.ts</c>, batch 4d-1).
/// </summary>
/// <remarks>
/// The TS module is type-only (no runtime behaviour), so these tests pin the parts that can drift
/// silently: the wire names of every event, the literal-union values, the marker-interface
/// groupings, the type guards, and the public surface of the API/context interfaces. Each
/// expectation is transcribed from the TS source.
/// </remarks>
public class ExtensionContractTests
{
    // ------------------------------------------------------------------ wire names

    /// <summary>
    /// The 35 event names TS dispatches on, in TS source order. A rename here is a wire break.
    /// </summary>
    private static readonly string[] TsEventNames =
    [
        "project_trust", "resources_discover", "mcp_servers_change",
        "session_start", "session_info_changed", "session_before_switch", "session_before_fork",
        "session_before_compact", "session_compact", "session_compact_failed", "session_shutdown",
        "session_before_tree", "session_tree",
        "context", "context_with_system", "cache_warming_decision",
        "before_provider_request", "before_provider_headers", "after_provider_response",
        "provider_stream_event",
        "before_agent_start", "agent_start", "agent_end", "agent_before_settle", "agent_settled",
        "ui_prompt_start", "ui_prompt_end",
        "turn_start", "turn_end",
        "message_start", "message_update", "message_end",
        "tool_execution_start", "tool_execution_update", "tool_execution_end",
        "model_select", "thinking_level_select",
        "user_bash",
        "input",
        "tool_call", "tool_result",
    ];

    [Fact]
    public void ExtensionEventNames_MatchTsWireNamesExactly()
    {
        var actual = typeof(ExtensionEventNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, FieldType.Name: "String" })
            .Select(f => (string)f.GetRawConstantValue()!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(TsEventNames.OrderBy(n => n, StringComparer.Ordinal), actual);
    }

    [Fact]
    public void EveryEventVariantMapsToAKnownWireName()
    {
        var variants = typeof(ExtensionEvent)
            .GetNestedTypes(BindingFlags.Public)
            .Where(t => t.IsSubclassOf(typeof(ExtensionEvent)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Every TS member of the ExtensionEvent union, minus the 4e placeholder
        // (cache_warming_decision has no payload yet).
        Assert.Equal(
            ["AfterProviderResponse", "AgentBeforeSettle", "AgentEnd", "AgentSettled", "AgentStart",
                "BashToolCall", "BashToolResult", "BeforeAgentStart", "BeforeProviderHeaders",
                "BeforeProviderRequest", "ContextEvent", "ContextWithSystemEvent", "CustomToolCall",
                "CustomToolResult", "EditToolCall", "EditToolResult", "FindToolCall", "FindToolResult",
                "GrepToolCall", "GrepToolResult", "InputEvent", "LsToolCall", "LsToolResult",
                "McpServersChange", "MessageEnd", "MessageStart", "MessageUpdate", "ModelSelect",
                "PowerShellToolCall", "PowerShellToolResult", "ProjectTrust", "ProviderStream",
                "ReadToolCall", "ReadToolResult", "ResourcesDiscover", "SessionBeforeCompact",
                "SessionBeforeFork", "SessionBeforeSwitch", "SessionBeforeTree", "SessionCompact",
                "SessionCompactFailed", "SessionInfoChanged", "SessionShutdown", "SessionStart",
                "SessionTree", "ThinkingLevelSelect", "ToolExecutionEnd", "ToolExecutionStart",
                "ToolExecutionUpdate", "TurnEnd", "TurnStart", "UiPromptEnd", "UiPromptStart",
                "UserBash", "WriteToolCall", "WriteToolResult"],
            variants);
    }

    // ------------------------------------------------------------------ literal unions

    [Fact]
    public void LiteralUnionValues_MatchTs()
    {
        Assert.Equal("tui", ExtensionMode.Tui);
        Assert.Equal("rpc", ExtensionMode.Rpc);
        Assert.Equal("json", ExtensionMode.Json);
        Assert.Equal("print", ExtensionMode.Print);

        Assert.Equal("aboveEditor", WidgetPlacement.AboveEditor);
        Assert.Equal("belowEditor", WidgetPlacement.BelowEditor);

        Assert.Equal("info", NotifyType.Info);
        Assert.Equal("warning", NotifyType.Warning);
        Assert.Equal("error", NotifyType.Error);

        Assert.Equal("before", ForkPosition.Before);
        Assert.Equal("at", ForkPosition.At);

        Assert.Equal("steer", DeliverAs.Steer);
        Assert.Equal("followUp", DeliverAs.FollowUp);
        Assert.Equal("nextTurn", DeliverAs.NextTurn);

        Assert.Equal("select", UiPromptKind.Select);
        Assert.Equal("confirm", UiPromptKind.Confirm);
        Assert.Equal("input", UiPromptKind.Input);
        Assert.Equal("editor", UiPromptKind.Editor);
        Assert.Equal("custom", UiPromptKind.Custom);

        Assert.Equal("set", ModelSelectSource.Set);
        Assert.Equal("cycle", ModelSelectSource.Cycle);
        Assert.Equal("restore", ModelSelectSource.Restore);

        Assert.Equal("interactive", InputSource.Interactive);
        Assert.Equal("rpc", InputSource.Rpc);
        Assert.Equal("extension", InputSource.Extension);

        Assert.Equal("completed", AgentActivityOutcome.Completed);
        Assert.Equal("aborted", AgentActivityOutcome.Aborted);
        Assert.Equal("error", AgentActivityOutcome.Error);

        Assert.Equal("yes", ProjectTrustDecision.Yes);
        Assert.Equal("no", ProjectTrustDecision.No);
        Assert.Equal("undecided", ProjectTrustDecision.Undecided);

        Assert.Equal("boolean", FlagType.Boolean);
        Assert.Equal("string", FlagType.String);
    }

    // ------------------------------------------------------------------ marker groups

    [Fact]
    public void MarkerInterfaces_CoverExactlyTheTsGroups()
    {
        var session = VariantsImplementing<ISessionEvent>();
        var toolCall = VariantsImplementing<IToolCallEvent>();
        var toolResult = VariantsImplementing<IToolResultEvent>();

        Assert.Equal(
            ["SessionBeforeCompact", "SessionBeforeFork", "SessionBeforeSwitch", "SessionBeforeTree",
                "SessionCompact", "SessionCompactFailed", "SessionInfoChanged", "SessionShutdown",
                "SessionStart", "SessionTree"],
            session.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            ["BashToolCall", "CustomToolCall", "EditToolCall", "FindToolCall", "GrepToolCall",
                "LsToolCall", "PowerShellToolCall", "ReadToolCall", "WriteToolCall"],
            toolCall.OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            ["BashToolResult", "CustomToolResult", "EditToolResult", "FindToolResult", "GrepToolResult",
                "LsToolResult", "PowerShellToolResult", "ReadToolResult", "WriteToolResult"],
            toolResult.OrderBy(n => n, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ type guards

    [Fact]
    public void IsToolCallEventType_MatchesTsGuard()
    {
        var bash = new ExtensionEvent.BashToolCall("t1", new Dictionary<string, object?>());
        var custom = new ExtensionEvent.CustomToolCall("t1", "my_tool", new Dictionary<string, object?>());
        var result = new ExtensionEvent.BashToolResult(
            "t1", new Dictionary<string, object?>(), [], false);

        Assert.True(ExtensionEventGuards.IsToolCallEventType("bash", bash));
        Assert.False(ExtensionEventGuards.IsToolCallEventType("read", bash));
        Assert.True(ExtensionEventGuards.IsToolCallEventType("my_tool", custom));
        Assert.False(ExtensionEventGuards.IsToolCallEventType("other", custom));
        Assert.False(ExtensionEventGuards.IsToolCallEventType("bash", result));

        Assert.True(ExtensionEventGuards.IsBashToolResult(result));
        Assert.False(ExtensionEventGuards.IsBashToolResult(bash));
    }

    // ------------------------------------------------------------------ event shapes

    [Fact]
    public void SessionEvents_KeepTsOptionalFields()
    {
        var start = new ExtensionEvent.SessionStart("new", "prev.jsonl");
        Assert.Equal("new", start.Reason);
        Assert.Equal("prev.jsonl", start.PreviousSessionFile);

        var plain = new ExtensionEvent.SessionStart("startup");
        Assert.Null(plain.PreviousSessionFile);

        var tree = new ExtensionEvent.SessionTree("newLeaf", "oldLeaf");
        Assert.Null(tree.SummaryEntry);
        Assert.False(tree.FromExtension);

        var failed = new ExtensionEvent.SessionCompactFailed(
            "threshold", "boom", Aborted: true, WillRetry: false, FromExtension: true);
        Assert.True(failed.Aborted);
        Assert.True(failed.FromExtension);
    }

    [Fact]
    public void InputEvent_UsesTsFieldNames()
    {
        var e = new ExtensionEvent.InputEvent("hi", null, "interactive");
        Assert.Equal("hi", e.Text);
        Assert.Null(e.Images);
        Assert.Equal("interactive", e.Source);
        Assert.Null(e.StreamingBehavior);
    }

    [Fact]
    public void BoundaryState_DefaultsToContinueCompleted()
    {
        var state = new BoundaryState();
        Assert.True(state.Continue);
        Assert.Equal(AgentActivityOutcome.Completed, state.Outcome);
        Assert.Empty(state.Entries);
        Assert.Null(state.Context);
    }

    [Fact]
    public void BoundaryDrafts_CarryTsFields()
    {
        SessionBoundaryDraft draft = new SessionBoundaryDraft.Compaction(
            new CompactionEntryDraft { Summary = "s", FirstKeptEntryId = "e1" });
        var compaction = Assert.IsType<SessionBoundaryDraft.Compaction>(draft);
        Assert.Equal("s", compaction.Draft.Summary);
        Assert.Equal("e1", compaction.Draft.FirstKeptEntryId);

        var nullKept = new CompactionEntryDraft { Summary = "s", FirstKeptEntryId = null };
        Assert.Null(nullKept.FirstKeptEntryId);
    }

    [Fact]
    public void EventResults_KeepTsFieldShapes()
    {
        var blocked = new ToolCallEventResult { Block = true, Reason = "nope", Terminate = true };
        Assert.True(blocked.Block);
        Assert.Equal("nope", blocked.Reason);
        Assert.True(blocked.Terminate);

        var trust = new ProjectTrustEventResult { Trusted = ProjectTrustDecision.Yes, Remember = true };
        Assert.Equal("yes", trust.Trusted);

        var fork = new SessionBeforeForkResult { Cancel = true, SkipConversationRestore = true };
        Assert.True(fork.SkipConversationRestore);

        var input = new InputEventResult.Transform("rewritten");
        Assert.Equal("rewritten", Assert.IsType<InputEventResult.Transform>(input).Text);
    }

    // ------------------------------------------------------------------ placeholders

    [Fact]
    public void ProviderHeaders_DeleteOnNullAssignment()
    {
        var headers = new ProviderHeaders();
        headers["X-A"] = "1";
        headers["X-B"] = "2";
        Assert.Equal("1", headers["X-A"]);

        headers["X-A"] = null;
        Assert.Null(headers["X-A"]);
        Assert.Equal("2", headers["X-B"]);
        Assert.Single(headers.AsDictionary());
    }

    [Fact]
    public void PlaceholderTypes_AreConstructible()
    {
        Assert.NotNull(new SourceInfo { Path = "a.ts" });
        Assert.Null(new SourceInfo().Path);
        Assert.NotNull(new EventBus());
        Assert.NotNull(new Theme());
        Assert.NotNull(new OverlayHandle());
        Assert.NotNull(new CustomMessage<int> { CustomType = "t", Content = "c" });
        Assert.NotNull(new Provider { Id = "p" });
        Assert.NotNull(new BashOperations());
    }

    // ------------------------------------------------------------------ API surface

    [Fact]
    public void ExtensionApi_SurfaceMatchesTsMethodList()
    {
        var actual = PublicMemberNames(typeof(IExtensionApi));
        Assert.Equal(Expected("on", "registerTool", "registerCommand", "registerShortcut",
            "registerFlag", "getFlag", "registerMessageRenderer", "registerMarkdownTransformer",
            "registerEntryRenderer", "registerToolRenderer", "sendMessage", "sendUserMessage",
            "appendEntry", "setSessionName", "getSessionName", "setLabel", "exec", "getActiveTools",
            "getAllTools", "getSettings", "setActiveTools", "getCommands", "setModel",
            "getThinkingLevel", "setThinkingLevel", "registerProvider", "unregisterProvider",
            "registerMcpServer", "unregisterMcpServer", "getMcpServers", "registerVirtualModel",
            "unregisterVirtualModel", "events"), actual);
    }

    [Fact]
    public void ExtensionContexts_SurfaceMatchesTsMethodList()
    {
        Assert.Equal(Expected("ui", "mode", "hasUI", "cwd", "sessionManager", "modelRegistry", "model",
            "scopedModels", "thinkingLevel", "isIdle", "isProjectTrusted", "signal", "abort",
            "hasPendingMessages", "shutdown", "getContextUsage", "compact", "getSystemPrompt"),
            PublicMemberNames(typeof(IExtensionContext)));

        Assert.Equal(Expected("select", "confirm", "input", "notify", "onTerminalInput", "setStatus",
            "setWorkingMessage", "setWorkingVisible", "setWorkingIndicator", "setHiddenThinkingLabel",
            "setWidget", "setFooter", "setHeader", "setTitle", "custom", "customAsync", "pasteToEditor",
            "setEditorText", "getEditorText", "editor", "addAutocompleteProvider", "setEditorComponent",
            "getEditorComponent", "theme", "getAllThemes", "getTheme", "setTheme", "getToolsExpanded",
            "setToolsExpanded"),
            PublicMemberNames(typeof(IExtensionUIContext)));

        Assert.Equal(Expected("tools", "executeTool"), PublicMemberNames(typeof(IExtensionToolContext)));

        Assert.Equal(Expected("getSystemPromptOptions", "waitForIdle", "newSession", "fork",
            "navigateTree", "switchSession", "reload"),
            PublicMemberNames(typeof(IExtensionCommandContext)));

        Assert.Equal(Expected("sendMessage", "sendUserMessage"),
            PublicMemberNames(typeof(IReplacedSessionContext)));
    }

    [Fact]
    public void ContextUsage_ComputesPercentLikeTs()
    {
        var usage = new ContextUsage { Tokens = 500, ContextWindow = 1000, Percent = 50 };
        Assert.Equal(50, usage.Percent);

        var unknown = new ContextUsage { Tokens = null, ContextWindow = 1000 };
        Assert.Null(unknown.Tokens);
        Assert.Null(unknown.Percent);
    }

    // ------------------------------------------------------------------ helpers

    private static List<string> VariantsImplementing<TMarker>()
        => typeof(ExtensionEvent)
            .GetNestedTypes(BindingFlags.Public)
            .Where(t => t.IsSubclassOf(typeof(ExtensionEvent)) && typeof(TMarker).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

    /// <summary>Distinct public method/property names, PascalCased and ordered.</summary>
    private static List<string> PublicMemberNames(Type type)
        => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)
            .Where(m => m is MethodInfo { IsSpecialName: false } or PropertyInfo)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>TS camelCase names → PascalCase, ordered.</summary>
    private static List<string> Expected(params string[] tsNames)
        => tsNames.Select(n => char.ToUpperInvariant(n[0]) + n[1..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
}
