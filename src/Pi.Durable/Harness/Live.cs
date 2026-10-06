using Pi.Chord.Delta;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>当前一轮一个工具调用的呈现。对应 TS <c>ToolSlot</c>。</summary>
public sealed record ToolSlot
{
    public required string CallId { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// 尚未开始的调用（sequential 轮）与其请求未提供的调用缺省——
    /// 后者以 generation 写入的 entry 进入 <c>done</c>。
    /// </summary>
    public TaskId<object?>? TaskId { get; init; }

    /// <summary>"pending" / "running" / "done"。</summary>
    public required string Status { get; init; }

    /// <summary>保留的运行输出与边界丢弃的部分。</summary>
    public string? Output { get; init; }

    public long? DroppedBytes { get; init; }

    public long? DroppedLines { get; init; }

    /// <summary>最后一次 <c>details()</c> 的值。</summary>
    public object? Details { get; init; }

    /// <summary>经 <c>api.diagnostic()</c> 记录的诊断。</summary>
    public IReadOnlyList<ToolDiagnostic>? Diagnostics { get; init; }

    /// <summary>完成后为结果条目；工具任务 faulted 或 orphaned 时缺省。</summary>
    public EntryId? Entry { get; init; }

    public Dictionary<string, object?> ToJson()
    {
        var json = new Dictionary<string, object?>
        {
            ["callId"] = CallId,
            ["name"] = Name,
            ["status"] = Status,
        };
        if (TaskId is { } taskId) json["taskId"] = taskId.Value;
        if (Output is not null) json["output"] = Output;
        if (DroppedBytes is { } droppedBytes) json["droppedBytes"] = droppedBytes;
        if (DroppedLines is { } droppedLines) json["droppedLines"] = droppedLines;
        if (Details is not null) json["details"] = Details;
        if (Diagnostics is { } diagnostics)
        {
            json["diagnostics"] = diagnostics.Select(diagnostic =>
            {
                var entry = new Dictionary<string, object?> { ["severity"] = diagnostic.Severity, ["message"] = diagnostic.Message };
                if (diagnostic.Code is not null) entry["code"] = diagnostic.Code;
                return (object?)entry;
            }).ToList();
        }

        if (Entry is { } entry) json["entry"] = entry.Value;
        return json;
    }

    public static ToolSlot FromJson(IReadOnlyDictionary<string, object?> json)
    {
        IReadOnlyList<ToolDiagnostic>? diagnostics = null;
        if (json.TryGetValue("diagnostics", out var diagnosticsValue)
            && diagnosticsValue is IReadOnlyList<object?> items)
        {
            diagnostics = items.OfType<IReadOnlyDictionary<string, object?>>().Select(item => new ToolDiagnostic
            {
                Severity = (string)item["severity"]!,
                Message = (string)item["message"]!,
                Code = item.TryGetValue("code", out var code) ? code as string : null,
            }).ToList();
        }

        return new ToolSlot
        {
            CallId = (string)json["callId"]!,
            Name = (string)json["name"]!,
            TaskId = json.TryGetValue("taskId", out var taskId) && taskId is not null
                ? TaskId<object?>.From(Convert.ToInt64(taskId))
                : null,
            Status = (string)json["status"]!,
            Output = json.TryGetValue("output", out var output) ? output as string : null,
            DroppedBytes = json.TryGetValue("droppedBytes", out var droppedBytes) && droppedBytes is not null
                ? Convert.ToInt64(droppedBytes)
                : null,
            DroppedLines = json.TryGetValue("droppedLines", out var droppedLines) && droppedLines is not null
                ? Convert.ToInt64(droppedLines)
                : null,
            Details = json.TryGetValue("details", out var details) ? details : null,
            Diagnostics = diagnostics,
            Entry = json.TryGetValue("entry", out var entry) && entry is not null
                ? EntryId.From(Convert.ToInt64(entry))
                : null,
        };
    }
}

/// <summary>一个活跃压缩任务的呈现（spec §8.7）。对应 TS <c>CompactionStatus</c>。</summary>
public sealed record CompactionStatus
{
    public required TaskId<object?> TaskId { get; init; }

    public required CompactionReason Reason { get; init; }

    /// <summary>一次生成是否在等它：generation 自有的压缩。</summary>
    public required bool Blocking { get; init; }

    public required int Attempt { get; init; }

    /// <summary>下一次总结尝试前的持久化退避。</summary>
    public Retry? RetryBackoff { get; init; }

    /// <summary>对应 TS <c>retry: { at, error }</c>。</summary>
    public sealed record Retry(long At, string Error);

    public Dictionary<string, object?> ToJson()
    {
        var json = new Dictionary<string, object?>
        {
            ["taskId"] = TaskId.Value,
            ["reason"] = Reason.ToString().ToLowerInvariant(),
            ["blocking"] = Blocking,
            ["attempt"] = (long)Attempt,
        };
        if (RetryBackoff is { } retry) json["retry"] = new Dictionary<string, object?> { ["at"] = retry.At, ["error"] = retry.Error };
        return json;
    }

    public static CompactionStatus FromJson(IReadOnlyDictionary<string, object?> json)
    {
        Retry? retry = null;
        if (json.TryGetValue("retry", out var retryValue)
            && retryValue is IReadOnlyDictionary<string, object?> retryJson)
        {
            retry = new Retry(
                Convert.ToInt64(retryJson["at"]!),
                (string)retryJson["error"]!);
        }

        return new CompactionStatus
        {
            TaskId = TaskId<object?>.From(Convert.ToInt64(json["taskId"]!)),
            Reason = Enum.TryParse<CompactionReason>((string)json["reason"]!, ignoreCase: true, out var reason)
                ? reason
                : CompactionReason.Manual,
            Blocking = Convert.ToBoolean(json["blocking"]!),
            Attempt = Convert.ToInt32(json["attempt"]!),
            RetryBackoff = retry,
        };
    }
}

/// <summary>
/// 内建活跃对话状态：运行控制与当前生成、工具轮的呈现。对应 TS <c>harness/live.ts</c>
/// （值是 <c>pi.live</c> 文档的严格 JSON 形状；<c>settleSchedulerOutcome</c> 依赖
/// generation 的 <c>convertPartial</c> 与 scheduler 的 <c>SchedulerOutcome</c>，随
/// generation/scheduler 阶段接入）。
/// </summary>
public static class Live
{
    /// <summary>可拥有 <c>pi.live.run</c> 的内建任务种类。对应 TS <c>RUN_TASK_KINDS</c>。</summary>
    public static readonly IReadOnlySet<string> RunTaskKinds = new HashSet<string>(StringComparer.Ordinal) { "pi.generation" };

    public const string ToolTaskKind = "pi.tool";

    public const string CompactionTaskKind = "pi.compaction";

    public static readonly DocToken<Dictionary<string, object?>> LiveDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "pi.live",
            Version = 1,
            Initial = () => new Dictionary<string, object?>(),
            // 提醒（spec §8.2）：没有任何东西在运行时存完整 base——无生成且无运行中的工具槽。
            // 这在空闲、把生成移交给工具轮的提交与轮内工具之间成立，使 delta 链至多跨一次
            // 生成或一轮工具的重叠执行。不要加 delta 计数上限；工具输出基准检查此规则。
            CheckpointWhen = (value, _, _) => !value.ContainsKey("generation") && !HasRunningTool(value),
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Initial),
        });

    private static bool HasRunningTool(IReadOnlyDictionary<string, object?> value)
        => value.TryGetValue("tools", out var tools)
            && tools is IReadOnlyList<object?> list
            && list.Any(slot => slot is IReadOnlyDictionary<string, object?> slotJson
                && slotJson.TryGetValue("status", out var status)
                && status is "running");

    private static readonly Path RunPath = Path.Root.Append(Seg.Key("run"));
    private static readonly Path GenerationPath = Path.Root.Append(Seg.Key("generation"));
    private static readonly Path ToolsPath = Path.Root.Append(Seg.Key("tools"));
    private static readonly Path CompactionsPath = Path.Root.Append(Seg.Key("compactions"));

    // ─── 运行控制 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 结束 <paramref name="taskId"/> 拥有的运行：结算其每个输入并移除 <c>run</code>。
    /// 总是移除 <c>generation</c> 与 <c>tools</c>——它们的呈现属于结束的运行。
    /// 对应 TS <c>endRun()</c>。
    /// </summary>
    public static void EndRun(Transaction tx, TxDocChange live, TaskId<object?> taskId, SubmissionSettlement settlement)
    {
        if (live.Draft.TryGetValue("run", out var runValue)
            && runValue is IReadOnlyDictionary<string, object?> run
            && run.TryGetValue("taskId", out var runTaskId)
            && Convert.ToInt64(runTaskId) == taskId.Value)
        {
            if (run.TryGetValue("inputs", out var inputsValue) && inputsValue is IReadOnlyList<object?> inputs)
            {
                foreach (var input in inputs)
                {
                    tx.SettleSubmission(SubmissionId.From(Convert.ToInt64(input)), settlement);
                }
            }

            live.Delete(RunPath);
        }

        live.Delete(GenerationPath);
        live.Delete(ToolsPath);
    }

    // ─── 压缩状态 ───────────────────────────────────────────────────────────

    /// <summary>加入本提交创建的压缩任务的状态；状态保持任务 ID 序。对应 TS <c>addCompactionStatus()</c>。</summary>
    public static void AddCompactionStatus(TxDocChange live, CompactionStatus status)
    {
        if (live.Draft.TryGetValue("compactions", out var value) && value is IReadOnlyList<object?> list)
        {
            live.Splice(CompactionsPath, list.Count, 0, [status.ToJson()]);
        }
        else
        {
            live.Set(CompactionsPath, new List<object?> { status.ToJson() });
        }
    }

    /// <summary>压缩任务 <paramref name="taskId"/> 的状态（若已列出）。对应 TS <c>compactionStatus()</c>。</summary>
    public static CompactionStatus? FindCompactionStatus(TxDocChange live, TaskId<object?> taskId)
        => Compactions(live).FirstOrDefault(status => status.TaskId == taskId);

    /// <summary>移除压缩任务的状态；列表为空时移除列表本身。对应 TS <c>removeCompactionStatus()</c>。</summary>
    public static void RemoveCompactionStatus(TxDocChange live, TaskId<object?> taskId)
    {
        if (!live.Draft.TryGetValue("compactions", out var value) || value is not IReadOnlyList<object?> list) return;
        var index = list.OfType<IReadOnlyDictionary<string, object?>>()
            .ToList()
            .FindIndex(status => Convert.ToInt64(status["taskId"]!) == taskId.Value);
        if (index < 0) return;
        if (list.Count == 1) live.Delete(CompactionsPath);
        else live.Splice(CompactionsPath, index, 1, []);
    }

    // ─── 工具槽 ─────────────────────────────────────────────────────────────

    /// <summary>当前轮中工具任务 <paramref name="taskId"/> 的槽（若本轮仍列出它）。对应 TS <c>toolSlot()</c>。</summary>
    public static ToolSlot? ToolSlotOf(TxDocChange live, TaskId<object?> taskId)
        => ToolSlots(live).FirstOrDefault(slot => slot.TaskId == taskId);

    /// <summary>
    /// 标记槽完成：结果条目（若有）此刻携带其运行输出、details 与诊断。
    /// 对应 TS <c>finishSlot()</c>（C# record 不可变：以整体 Set 写回槽）。
    /// </summary>
    public static void FinishSlot(TxDocChange live, ToolSlot slot, EntryId? entry)
    {
        if (!TryFindSlotIndex(live, slot, out var index)) return;
        var finished = slot with { Status = "done", Entry = entry ?? slot.Entry };
        finished = ClearProgress(finished);
        live.Set(ToolsPath.Append(Seg.Index(index)), finished.ToJson());
    }

    /// <summary>移除工具运行期间发布的内容；其结果条目或重跑会替换它。对应 TS <c>clearProgress()</c>。</summary>
    public static ToolSlot ClearProgress(ToolSlot slot)
        => slot with { Output = null, DroppedBytes = null, DroppedLines = null, Details = null, Diagnostics = null };

    private static bool TryFindSlotIndex(TxDocChange live, ToolSlot slot, out int index)
    {
        index = -1;
        if (!live.Draft.TryGetValue("tools", out var value) || value is not IReadOnlyList<object?> list) return false;
        var jsons = list.OfType<IReadOnlyDictionary<string, object?>>().ToList();
        for (var candidate = 0; candidate < jsons.Count; candidate++)
        {
            var current = ToolSlot.FromJson(jsons[candidate]);
            if (current.CallId == slot.CallId && Equals(current.TaskId, slot.TaskId))
            {
                index = candidate;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ToolSlot> ToolSlots(TxDocChange live)
        => live.Draft.TryGetValue("tools", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(ToolSlot.FromJson)]
            : [];

    private static IReadOnlyList<CompactionStatus> Compactions(TxDocChange live)
        => live.Draft.TryGetValue("compactions", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>().Select(CompactionStatus.FromJson)]
            : [];
}
