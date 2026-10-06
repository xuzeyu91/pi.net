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
    /// <c>Data</c> 持 <c>{ diagnostics: [...] }</c> JSON 字典（存储要求严格 JSON；
    /// 诊断经 <see cref="ToolResultDiagnostics"/> 读取）。由工具任务与 generation 写入。
    /// </summary>
    public static readonly Entry<IReadOnlyDictionary<string, object?>> ToolResultEntry =
        DefineEntry<IReadOnlyDictionary<string, object?>>("pi.tool-result");

    /// <summary>新上下文起点：始终 <c>HeadIsSelf</c>；<c>Model</c> 缺省为普通重置，或携带交接文本的 <c>[UserMessage]</c>。</summary>
    public static readonly Entry<object?> ResetEntry = DefineEntry<object?>("pi.reset");

    /// <summary>压缩摘要：<c>Model</c> 为包裹摘要的 <c>[UserMessage]</c>，<c>Head</c> 为首个保留条目。</summary>
    public static readonly Entry<CompactionData> CompactionEntry = DefineEntry<CompactionData>("pi.compaction");

    /// <summary><c>pi.compaction</c> 条目的 data 形状。对应 TS <c>{ reason: CompactionReason }</c>。</summary>
    public sealed record CompactionData(CompactionReason Reason);

    /// <summary>读 pi.tool-result 条目 data 里的结构化诊断（wire 形状 { diagnostics: [...] }）。</summary>
    public static IReadOnlyList<Pi.Durable.Harness.ToolDiagnostic> ToolResultDiagnostics(
        IReadOnlyDictionary<string, object?> data)
    {
        if (!data.TryGetValue("diagnostics", out var value) || value is not IReadOnlyList<object?> items)
        {
            return [];
        }

        return items.OfType<IReadOnlyDictionary<string, object?>>()
            .Select(item => new Pi.Durable.Harness.ToolDiagnostic
            {
                Severity = item.TryGetValue("severity", out var severity) ? severity as string ?? "error" : "error",
                Message = item.TryGetValue("message", out var message) ? message as string ?? "" : "",
                Code = item.TryGetValue("code", out var code) ? code as string : null,
            })
            .ToList();
    }
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
