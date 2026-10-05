using Pi.Durable.Types;

namespace Pi.Durable;

/// <summary>类型化条目种类 + 窄化守卫。对应 TS <c>entries.ts</c> 的 <c>defineEntry</c>。</summary>
public static class DurableEntries
{
    /// <summary>
    /// 定义类型化条目种类，其 <see cref="Entry{TData}.Is"/> 按 <see cref="EntryRecord.Kind"/> 窄化。
    /// 对应 TS <c>defineEntry&lt;D&gt;(kind)</c>。
    /// </summary>
    public static Entry<TData> DefineEntry<TData>(string kind)
    {
        if (string.IsNullOrEmpty(kind))
            throw new ArgumentException("Entry kind must be a non-empty string", nameof(kind));
        return new Entry<TData> { Kind = kind };
    }

    /// <summary>用户输入：<c>Model</c> 为 <c>[UserMessage]</c>。由提交写入。</summary>
    public static readonly Entry<object?> UserEntry = DefineEntry<object?>("pi.user");

    /// <summary>provider 结果（任意停止原因）：<c>Model</c> 为 <c>[AssistantMessage]</c>。由 generation 写入。</summary>
    public static readonly Entry<object?> AssistantEntry = DefineEntry<object?>("pi.assistant");

    /// <summary>位置性 prompt 与工具变更：<c>Model</c> 为 content 为空的 <c>[SystemMessage]</c>。</summary>
    public static readonly Entry<object?> SystemEntry = DefineEntry<object?>("pi.system");

    /// <summary>
    /// 工具结果：<c>Model</c> 为 <c>[ToolResultMessage]</c>（内容以渲染的诊断块结尾）；
    /// <c>Data</c> 持结构化诊断（可无）。由工具任务与 generation 写入。
    /// </summary>
    public static readonly Entry<ToolResultData> ToolResultEntry = DefineEntry<ToolResultData>("pi.tool-result");

    /// <summary>新上下文起点：始终 <c>HeadIsSelf</c>；<c>Model</c> 缺省为普通重置，或携带交接文本的 <c>[UserMessage]</c>。</summary>
    public static readonly Entry<object?> ResetEntry = DefineEntry<object?>("pi.reset");

    /// <summary>压缩摘要：<c>Model</c> 为包裹摘要的 <c>[UserMessage]</c>，<c>Head</c> 为首个保留条目。</summary>
    public static readonly Entry<CompactionData> CompactionEntry = DefineEntry<CompactionData>("pi.compaction");

    /// <summary><c>pi.tool-result</c> 条目的 data 形状。对应 TS <c>{ diagnostics: ToolDiagnostic[] }</c>。</summary>
    public sealed record ToolResultData(IReadOnlyList<ToolDiagnostic> Diagnostics);

    /// <summary><c>pi.compaction</c> 条目的 data 形状。对应 TS <c>{ reason: CompactionReason }</c>。</summary>
    public sealed record CompactionData(CompactionReason Reason);
}

/// <summary>工具调用备注（面向模型与 UI，如截断或溢出路径）；绝不进入工具数据。对应 TS <c>ToolDiagnostic</c>。</summary>
public sealed record ToolDiagnostic
{
    public required DiagnosticSeverity Severity { get; init; }

    public required string Message { get; init; }

    public string? Code { get; init; }
}

/// <summary>诊断严重级。对应 TS <c>ToolDiagnostic["severity"]</c>。</summary>
public enum DiagnosticSeverity
{
    Info,

    Warn,

    Error,
}

/// <summary>压缩触发原因。对应 TS <c>CompactionReason</c>。</summary>
public enum CompactionReason
{
    /// <summary>显式 compact() 调用。</summary>
    Manual,

    /// <summary>generation 准备阶段的阈值。</summary>
    Threshold,

    /// <summary>上下文溢出。</summary>
    Overflow,
}
