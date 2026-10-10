using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions.Types;
using Pi.Tui;
using Pi.Tui.Components;

namespace Pi.CodingAgent.Core.Extensions;

// ============================================================================
// Extension contexts — port of core/extensions/types.ts (4d-1)
// ============================================================================

/// <summary>TS <c>ExtensionMode</c> 字符串字面量联合。</summary>
public static class ExtensionMode
{
    /// <summary>交互式 TUI。</summary>
    public const string Tui = "tui";

    /// <summary>RPC 服务模式。</summary>
    public const string Rpc = "rpc";

    /// <summary>JSON 行模式。</summary>
    public const string Json = "json";

    /// <summary>一次性打印模式。</summary>
    public const string Print = "print";
}

/// <summary>TS <c>WidgetPlacement</c> 字符串字面量联合。</summary>
public static class WidgetPlacement
{
    public const string AboveEditor = "aboveEditor";

    public const string BelowEditor = "belowEditor";

    /// <summary>TS 默认值。</summary>
    public const string Default = AboveEditor;
}

/// <summary>TS <c>notify</c> 的 type 参数字面量联合。</summary>
public static class NotifyType
{
    public const string Info = "info";

    public const string Warning = "warning";

    public const string Error = "error";
}

/// <summary>TS <c>fork</c> 的 position 参数字面量联合。</summary>
public static class ForkPosition
{
    public const string Before = "before";

    public const string At = "at";
}

/// <summary>TS 消息投递方式字面量联合（<c>deliverAs</c>）。</summary>
public static class DeliverAs
{
    public const string Steer = "steer";

    public const string FollowUp = "followUp";

    public const string NextTurn = "nextTurn";
}

/// <summary>Options for extension UI dialogs. TS <c>ExtensionUIDialogOptions</c>.</summary>
public sealed record ExtensionUIDialogOptions
{
    /// <summary>Programmatically dismiss the dialog.</summary>
    public CancellationToken? Signal { get; init; }

    /// <summary>Timeout in milliseconds. The dialog auto-dismisses with a live countdown.</summary>
    public int? TimeoutMs { get; init; }
}

/// <summary>Options for extension widgets. TS <c>ExtensionWidgetOptions</c>.</summary>
public sealed record ExtensionWidgetOptions
{
    /// <summary>Where the widget is rendered. Defaults to <see cref="WidgetPlacement.AboveEditor"/>.</summary>
    public string? Placement { get; init; }
}

/// <summary>
/// Raw terminal input listener result. TS <c>{ consume?: boolean; data?: string } | undefined</c>.
/// </summary>
public readonly record struct TerminalInputResult(bool Consume, string? Data = null);

/// <summary>Raw terminal input listener for extensions. TS <c>TerminalInputHandler</c>.</summary>
public delegate TerminalInputResult? TerminalInputHandler(string data);

/// <summary>Working indicator configuration. TS <c>WorkingIndicatorOptions</c>.</summary>
public sealed record WorkingIndicatorOptions
{
    /// <summary>
    /// Animation frames. An empty array hides the indicator entirely; custom frames render verbatim.
    /// </summary>
    public IReadOnlyList<string>? Frames { get; init; }

    /// <summary>Frame interval in milliseconds for animated indicators.</summary>
    public int? IntervalMs { get; init; }
}

/// <summary>Wrap the current autocomplete provider. TS <c>AutocompleteProviderFactory</c>.</summary>
public delegate IAutocompleteProvider AutocompleteProviderFactory(IAutocompleteProvider current);

/// <summary>Custom editor component factory. TS <c>EditorFactory</c>.</summary>
public delegate IEditorComponent EditorFactory(TuiBase tui, EditorTheme theme, KeybindingsManager keybindings);

/// <summary>Theme list entry. TS <c>{ name: string; path: string | undefined }</c>.</summary>
public sealed record ThemeInfo(string Name, string? Path);

/// <summary>Result of <c>setTheme</c>. TS <c>{ success: boolean; error?: string }</c>.</summary>
public sealed record SetThemeResult(bool Success, string? Error = null);

/// <summary>Overlay options for <c>ui.custom</c>. TS inline options type.</summary>
public sealed record ExtensionCustomOptions
{
    public bool? Overlay { get; init; }

    /// <summary>Overlay positioning/sizing; static or a factory for dynamic updates.</summary>
    public OverlayOptions? OverlayOptions { get; init; }

    /// <summary>Called with the overlay handle after the overlay is shown.</summary>
    public Action<OverlayHandle>? OnHandle { get; init; }
}

/// <summary>
/// UI context for extensions to request interactive UI. TS <c>ExtensionUIContext</c>.
/// Each mode (interactive, RPC, print) provides its own implementation.
/// </summary>
public interface IExtensionUIContext
{
    /// <summary>Show a selector and return the user's choice.</summary>
    Task<string?> Select(string title, IReadOnlyList<string> options, ExtensionUIDialogOptions? opts = null);

    /// <summary>Show a confirmation dialog.</summary>
    Task<bool> Confirm(string title, string message, ExtensionUIDialogOptions? opts = null);

    /// <summary>Show a text input dialog.</summary>
    Task<string?> Input(string title, string? placeholder = null, ExtensionUIDialogOptions? opts = null);

    /// <summary>Show a notification to the user.</summary>
    void Notify(string message, string? type = null);

    /// <summary>Listen to raw terminal input (interactive mode only). Returns an unsubscribe function.</summary>
    Action OnTerminalInput(TerminalInputHandler handler);

    /// <summary>Set status text in the footer/status bar. Pass null to clear.</summary>
    void SetStatus(string key, string? text);

    /// <summary>Set the working/loading message shown during streaming. Call with no argument to restore the default.</summary>
    void SetWorkingMessage(string? message = null);

    /// <summary>Show or hide the built-in interactive working loader row during streaming.</summary>
    void SetWorkingVisible(bool visible);

    /// <summary>Configure the interactive working indicator shown during streaming.</summary>
    void SetWorkingIndicator(WorkingIndicatorOptions? options = null);

    /// <summary>Set the label shown for hidden thinking blocks. Call with no argument to restore the default.</summary>
    void SetHiddenThinkingLabel(string? label = null);

    /// <summary>Set a string-array widget above or below the editor.</summary>
    void SetWidget(string key, IReadOnlyList<string>? content, ExtensionWidgetOptions? options = null);

    /// <summary>Set a component-factory widget above or below the editor.</summary>
    void SetWidget(string key, Func<TuiBase, Theme, IComponent>? factory, ExtensionWidgetOptions? options = null);

    /// <summary>Set a custom footer component, or null to restore the built-in footer.</summary>
    void SetFooter(Func<TuiBase, Theme, IReadonlyFooterDataProvider, IComponent>? factory);

    /// <summary>Set a custom header component, or null to restore the built-in header.</summary>
    void SetHeader(Func<TuiBase, Theme, IComponent>? factory);

    /// <summary>Set the terminal window/tab title.</summary>
    void SetTitle(string title);

    /// <summary>Show a custom component with keyboard focus (synchronous factory).</summary>
    Task<T> Custom<T>(
        Func<TuiBase, Theme, KeybindingsManager, Action<T>, IComponent> factory,
        ExtensionCustomOptions? options = null);

    /// <summary>Show a custom component with keyboard focus (asynchronous factory).</summary>
    Task<T> CustomAsync<T>(
        Func<TuiBase, Theme, KeybindingsManager, Action<T>, Task<IComponent>> factory,
        ExtensionCustomOptions? options = null);

    /// <summary>Paste text into the editor, triggering paste handling.</summary>
    void PasteToEditor(string text);

    /// <summary>Set the text in the core input editor.</summary>
    void SetEditorText(string text);

    /// <summary>Get the current text from the core input editor.</summary>
    string GetEditorText();

    /// <summary>Show a multi-line editor for text editing.</summary>
    Task<string?> Editor(string title, string? prefill = null);

    /// <summary>Stack additional autocomplete behavior on top of the built-in provider.</summary>
    void AddAutocompleteProvider(AutocompleteProviderFactory factory);

    /// <summary>Set a custom editor component via factory function. Pass null to restore the default editor.</summary>
    void SetEditorComponent(EditorFactory? factory);

    /// <summary>Get the currently configured custom editor factory, or null when using the default editor.</summary>
    EditorFactory? GetEditorComponent();

    /// <summary>Get the current theme for styling.</summary>
    Theme Theme { get; }

    /// <summary>Get all available themes with their names and file paths.</summary>
    IReadOnlyList<ThemeInfo> GetAllThemes();

    /// <summary>Load a theme by name without switching to it. Returns null if not found.</summary>
    Theme? GetTheme(string name);

    /// <summary>Set the current theme by name or Theme object.</summary>
    SetThemeResult SetTheme(string name);

    /// <summary>Set the current theme by name or Theme object.</summary>
    SetThemeResult SetTheme(Theme theme);

    /// <summary>Get current tool output expansion state.</summary>
    bool GetToolsExpanded();

    /// <summary>Set tool output expansion state.</summary>
    void SetToolsExpanded(bool expanded);
}

/// <summary>Current context usage for the active model. TS <c>ContextUsage</c>.</summary>
public sealed record ContextUsage
{
    /// <summary>Estimated context tokens, or null if unknown.</summary>
    public double? Tokens { get; init; }

    public required double ContextWindow { get; init; }

    /// <summary>Context usage as percentage of the context window, or null when tokens is unknown.</summary>
    public double? Percent { get; init; }
}

/// <summary>Options for <c>ctx.compact()</c>. TS <c>CompactOptions</c>.</summary>
public sealed record CompactOptions
{
    public string? CustomInstructions { get; init; }

    public Action<CompactionResult>? OnComplete { get; init; }

    public Action<Exception>? OnError { get; init; }
}

/// <summary>Options for <c>ctx.executeTool()</c>. TS <c>ExecuteToolOptions</c>.</summary>
public sealed record ExecuteToolOptions
{
    /// <summary>Defaults to the calling tool's signal.</summary>
    public CancellationToken? Signal { get; init; }

    /// <summary>Receives partial results of the nested tool.</summary>
    public AgentToolUpdateCallback? OnUpdate { get; init; }
}

/// <summary>
/// Context passed to extension event handlers. TS <c>ExtensionContext</c>.
/// </summary>
public interface IExtensionContext
{
    /// <summary>UI methods for user interaction.</summary>
    IExtensionUIContext Ui { get; }

    /// <summary>Current run mode. Use <see cref="ExtensionMode.Tui"/> to guard terminal-only UI.</summary>
    string Mode { get; }

    /// <summary>Whether dialog-capable UI is available (true in TUI and RPC modes).</summary>
    bool HasUI { get; }

    /// <summary>Current working directory.</summary>
    string Cwd { get; }

    /// <summary>Session manager (read-only).</summary>
    IReadonlySessionManager SessionManager { get; }

    /// <summary>Model registry for API key resolution.</summary>
    ModelRegistry ModelRegistry { get; }

    /// <summary>Current model (may be null).</summary>
    Model? Model { get; }

    /// <summary>Models scoped to this session. Empty when no scoping is configured.</summary>
    IReadOnlyList<ScopedModel> ScopedModels { get; }

    /// <summary>Current thinking level, when provided by the session runtime.</summary>
    ThinkingLevel? ThinkingLevel { get; }

    /// <summary>Whether the agent is idle (not streaming).</summary>
    bool IsIdle();

    /// <summary>Whether project-local trust is active for this context.</summary>
    bool IsProjectTrusted();

    /// <summary>The current abort signal, or null when the agent is not streaming.</summary>
    CancellationToken? Signal { get; }

    /// <summary>Abort the current agent operation.</summary>
    void Abort();

    /// <summary>Whether there are queued messages waiting.</summary>
    bool HasPendingMessages();

    /// <summary>Gracefully shutdown pi and exit. Available in all contexts.</summary>
    void Shutdown();

    /// <summary>Get current context usage for the active model.</summary>
    ContextUsage? GetContextUsage();

    /// <summary>Trigger compaction without awaiting completion.</summary>
    void Compact(CompactOptions? options = null);

    /// <summary>Get the current effective system prompt.</summary>
    string GetSystemPrompt();
}

/// <summary>
/// Context passed to tool <c>execute()</c> in a session: the extension context plus
/// <see cref="ExecuteTool"/> for running other tools through the same validation, hooks and
/// permission checks as model-issued calls. TS <c>ExtensionToolContext</c>.
/// </summary>
public interface IExtensionToolContext : IExtensionContext
{
    /// <summary>Tools <see cref="ExecuteTool"/> can call.</summary>
    IReadOnlyList<AgentTool> Tools { get; }

    /// <summary>
    /// Run another tool. Never rejects for tool failures: unknown tools, validation errors,
    /// blocked calls and thrown errors come back as <see cref="AgentToolCallOutcome.IsError"/>.
    /// </summary>
    Task<AgentToolCallOutcome> ExecuteTool(string name, object? args, ExecuteToolOptions? options = null);
}

/// <summary>Options for <c>newSession</c>. TS inline options type.</summary>
public sealed record NewSessionOptions
{
    public string? ParentSession { get; init; }

    public Func<ISessionManager, Task>? Setup { get; init; }

    public Func<IReplacedSessionContext, Task>? WithSession { get; init; }
}

/// <summary>Options for <c>fork</c>. TS inline options type.</summary>
public sealed record ForkOptions
{
    /// <summary><see cref="ForkPosition.Before"/> or <see cref="ForkPosition.At"/>.</summary>
    public string? Position { get; init; }

    public Func<IReplacedSessionContext, Task>? WithSession { get; init; }
}

/// <summary>Options for <c>navigateTree</c>. TS inline options type.</summary>
public sealed record NavigateTreeOptions
{
    public bool? Summarize { get; init; }

    public string? CustomInstructions { get; init; }

    public bool? ReplaceInstructions { get; init; }

    public string? Label { get; init; }
}

/// <summary>Options for <c>switchSession</c>. TS inline options type.</summary>
public sealed record SwitchSessionOptions
{
    public Func<IReplacedSessionContext, Task>? WithSession { get; init; }
}

/// <summary>
/// Extended context for command handlers. Includes session control methods only safe in
/// user-initiated commands. TS <c>ExtensionCommandContext</c>.
/// </summary>
public interface IExtensionCommandContext : IExtensionContext
{
    /// <summary>Get the current base system-prompt construction options.</summary>
    BuildSystemPromptOptions GetSystemPromptOptions();

    /// <summary>Wait for the agent to finish streaming.</summary>
    Task WaitForIdle();

    /// <summary>Start a new session, optionally with initialization. Returns whether it was cancelled.</summary>
    Task<bool> NewSession(NewSessionOptions? options = null);

    /// <summary>Fork from a specific entry, creating a new session file.</summary>
    Task<bool> Fork(string entryId, ForkOptions? options = null);

    /// <summary>Navigate to a different point in the session tree.</summary>
    Task<bool> NavigateTree(string targetId, NavigateTreeOptions? options = null);

    /// <summary>Switch to a different session file.</summary>
    Task<bool> SwitchSession(string sessionPath, SwitchSessionOptions? options = null);

    /// <summary>Reload extensions, skills, prompts, themes, and context files.</summary>
    Task Reload();
}

/// <summary>
/// Draft of a custom message. TS <c>Pick&lt;CustomMessage&lt;T&gt;, "customType" | "content" |
/// "display" | "details"&gt;</c>.
/// </summary>
public sealed record CustomMessageDraft
{
    public required string CustomType { get; init; }

    public required MessageContent Content { get; init; }

    public bool Display { get; init; }

    public object? Details { get; init; }
}

/// <summary>Options for <c>sendMessage</c>. TS inline options type.</summary>
public sealed record SendMessageOptions
{
    public bool? TriggerTurn { get; init; }

    /// <summary><see cref="DeliverAs"/> value.</summary>
    public string? DeliverAs { get; init; }
}

/// <summary>Options for <c>sendUserMessage</c>. TS inline options type.</summary>
public sealed record SendUserMessageOptions
{
    /// <summary><see cref="DeliverAs.Steer"/> or <see cref="DeliverAs.FollowUp"/>.</summary>
    public string? DeliverAs { get; init; }

    public bool? ExpandPromptTemplates { get; init; }
}

/// <summary>
/// Fresh command-capable context bound to the replacement session after a session switch.
/// TS <c>ReplacedSessionContext</c>.
/// </summary>
public interface IReplacedSessionContext : IExtensionCommandContext
{
    /// <summary>Send a custom message to the session.</summary>
    Task SendMessage(CustomMessageDraft message, SendMessageOptions? options = null);

    /// <summary>Send a user message to the agent. Always triggers a turn.</summary>
    Task SendUserMessage(string content, SendUserMessageOptions? options = null);

    /// <summary>Send a user message with attached images. Always triggers a turn.</summary>
    Task SendUserMessage(IReadOnlyList<ContentBlock> content, SendUserMessageOptions? options = null);
}
