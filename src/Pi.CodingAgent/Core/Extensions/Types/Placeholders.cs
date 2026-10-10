using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core.Extensions.Types;

// ============================================================================
// 4e/4f 占位类型（placeholder types）
// ============================================================================
//
// TS `core/extensions/types.ts` 引用了一批尚未移植的模块类型（session-manager / compaction /
// bash-executor / cache-warmer / messages / slash-commands / system-prompt /
// footer-data-provider / theme）。按 4d 执行方案（docs/4d-extension-system-plan.md
// 批次 4d-1），这里为每个类型定义**最小占位**，使契约层可以独立编译；4e/4f 落地时把这些记录
// 换成真实实现并统一收敛（每处均有 `// 4e/4f 接入后替换` 标记）。
//
// 已随 4d-2a/4d-2b 移除的占位：`EventBus`（→ Core/EventBus.cs）、`SourceInfo`
// （→ Core/SourceInfo.cs）、`ExecOptions` / `ExecResult`（→ Core/Exec.cs）。
//
// 占位原则：
// - 只保留契约面用到的成员（事件负载的字段、回调的签名）；
// - 不实现任何行为；
// - 命名空间固定在 Pi.CodingAgent.Core.Extensions.Types，真实类型落地后删除对应占位即可。

// ---------------------------------------------------------------------------
// bash-executor.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>TS <c>BashResult</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record BashResult
{
    public string Output { get; init; } = "";

    public int? ExitCode { get; init; }
}

/// <summary>
/// TS <c>BashOperations</c>（bash 执行的后端操作集）占位。// 4e/4f 接入后替换
/// </summary>
public sealed record BashOperations
{
}

// ---------------------------------------------------------------------------
// footer-data-provider.ts（4f）
// ---------------------------------------------------------------------------

/// <summary>
/// 自定义 footer 的数据来源（TS <c>ReadonlyFooterDataProvider</c>）占位。// 4e/4f 接入后替换
/// </summary>
public interface IReadonlyFooterDataProvider
{
}

// ---------------------------------------------------------------------------
// compaction/index.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>TS <c>CompactionPreparation</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record CompactionPreparation
{
    public IReadOnlyList<string> BranchEntries { get; init; } = Array.Empty<string>();
}

/// <summary>TS <c>CompactionResult</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record CompactionResult
{
    public string Summary { get; init; } = "";

    public bool Aborted { get; init; }
}

// ---------------------------------------------------------------------------
// cache-warmer.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>CacheWarmingDecisionEvent</c> 占位。它是 <see cref="ExtensionEvent"/> 联合的成员，
/// 因此这里派生自占位事件基类。// 4e/4f 接入后替换
/// </summary>
public sealed record CacheWarmingDecisionEvent : ExtensionEvent
{
    private CacheWarmingDecisionEvent()
    {
    }

    /// <summary>占位实例；真实负载随 cache-warmer 移植补齐。</summary>
    public static readonly CacheWarmingDecisionEvent Instance = new();
}

/// <summary>TS <c>CacheWarmingDecisionEventResult</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record CacheWarmingDecisionEventResult
{
}

// ---------------------------------------------------------------------------
// messages.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>CustomMessage&lt;T&gt;</c> 占位（会话中的自定义消息）。// 4e/4f 接入后替换
/// </summary>
public sealed record CustomMessage<T>
{
    public required string CustomType { get; init; }

    public required string Content { get; init; }

    public bool Display { get; init; }

    public T? Details { get; init; }
}

/// <summary>TS <c>CustomEntry&lt;T&gt;</c> 占位（会话中的自定义条目）。// 4e/4f 接入后替换</summary>
public sealed record CustomEntry<T>
{
    public required string CustomType { get; init; }

    public T? Data { get; init; }
}

// ---------------------------------------------------------------------------
// session-manager.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>TS <c>SessionEntry</c> 占位。// 4e/4f 接入后替换</summary>
public abstract record SessionEntry
{
    public required string Id { get; init; }
}

/// <summary>TS <c>ContextEditEntry</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record ContextEditEntry : SessionEntry
{
    /// <summary>TS <c>ContextEditEntry["replacement"]</c> 的占位。</summary>
    public object? Replacement { get; init; }
}

/// <summary>TS <c>CompactionEntry</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record CompactionEntry : SessionEntry
{
    public string Summary { get; init; } = "";

    public string? FirstKeptEntryId { get; init; }
}

/// <summary>TS <c>BranchSummaryEntry</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record BranchSummaryEntry : SessionEntry
{
    public string Summary { get; init; } = "";
}

/// <summary>TS <c>ProjectedSessionEntry</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record ProjectedSessionEntry
{
    public required string Id { get; init; }
}

/// <summary>
/// TS <c>ReadonlySessionManager</c> 占位（扩展上下文里的只读会话视图）。// 4e/4f 接入后替换
/// </summary>
public interface IReadonlySessionManager
{
    /// <summary>TS <c>getSessionId()</c>。</summary>
    string SessionId { get; }

    /// <summary>TS <c>getSessionFile()</c>。</summary>
    string? SessionFile { get; }

    /// <summary>TS <c>getEntries()</c>。</summary>
    IReadOnlyList<SessionEntry> Entries { get; }
}

/// <summary>TS <c>SessionManager</c> 占位（命令上下文里的可写会话管理器）。// 4e/4f 接入后替换</summary>
public interface ISessionManager : IReadonlySessionManager
{
}

// ---------------------------------------------------------------------------
// slash-commands.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>TS <c>SlashCommandInfo</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record SlashCommandInfo
{
    public required string Name { get; init; }

    public string? Description { get; init; }
}

// ---------------------------------------------------------------------------
// system-prompt.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>TS <c>BuildSystemPromptOptions</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record BuildSystemPromptOptions
{
}

/// <summary>TS <c>NormalizedBuildSystemPromptOptions</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record NormalizedBuildSystemPromptOptions
{
}

// ---------------------------------------------------------------------------
// keybindings.ts（4e）
// ---------------------------------------------------------------------------

/// <summary>TS <c>AppKeybinding</c> 占位。// 4e/4f 接入后替换</summary>
public sealed record AppKeybinding
{
    public required string Id { get; init; }
}

// ---------------------------------------------------------------------------
// pi-tui 中尚未落地的类型（4f）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>OverlayHandle</c>（pi-tui）占位。// 4e/4f 接入后替换
/// </summary>
public sealed class OverlayHandle
{
}

/// <summary>
/// TS pi-ai <c>ProviderHeaders</c> 占位：可变的请求头表，null 值表示删除该头。// 4e/4f 接入后替换
/// </summary>
public sealed class ProviderHeaders
{
    private readonly Dictionary<string, string?> _headers = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key]
    {
        // TS semantics: assigning null deletes the header.
        get => _headers.TryGetValue(key, out var value) ? value : null;
        set
        {
            if (value is null)
            {
                _headers.Remove(key);
            }
            else
            {
                _headers[key] = value;
            }
        }
    }

    public IReadOnlyDictionary<string, string?> AsDictionary() => _headers;
}

/// <summary>
/// TS pi-ai <c>Provider</c>（原生 provider 注册单元）占位。// 4e/4f 接入后替换
/// </summary>
public sealed record Provider
{
    public required string Id { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? Api { get; init; }

    public IReadOnlyList<ModelSpec>? Models { get; init; }
}

// ---------------------------------------------------------------------------
// modes/interactive/theme/theme.ts（4f）
// ---------------------------------------------------------------------------

/// <summary>
/// TS <c>Theme</c> 占位。渲染器（renderCall / renderResult / setWidget 组件工厂）与
/// <c>ctx.ui.theme</c> 都消费它；4f 落地后替换为真实 Theme。// 4e/4f 接入后替换
/// </summary>
public sealed class Theme
{
}
