using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 一个对话自身开销的账本：它的条目，以及没有模型开销的压缩总结尝试。对应 TS <c>harness/usage.ts</c> 的
/// <c>UsageState</c>（值是 usage 的 JSON 表示，形状即 <see cref="Pi.Ai.Types.Usage"/> 的 wire 形态：
/// input/output/cacheRead/cacheWrite/totalTokens + 可选 cacheWrite1h/reasoning + cost 五桶子对象）。
/// </summary>
public static class Usage
{
    public const string ModelsBucket = "models";

    public const string ToolsBucket = "tools";

    public static readonly DocToken<Dictionary<string, object?>> UsageDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "pi.usage",
            Version = 1,
            Initial = () => new Dictionary<string, object?>
            {
                [ModelsBucket] = new Dictionary<string, object?>(),
                [ToolsBucket] = new Dictionary<string, object?>(),
            },
            // TS checkpointWhen: () => true。
            CheckpointWhen = (_, _, _) => true,
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Initial),
        });

    /// <summary>usage 文档的初值（两个空桶）；事件与快照的退役文档回退。对应 TS <c>UsageDoc.definition.initial()</c>。</summary>
    public static Dictionary<string, object?> InitialUsage() => new()
    {
        [ModelsBucket] = new Dictionary<string, object?>(),
        [ToolsBucket] = new Dictionary<string, object?>(),
    };

    /// <summary>
    /// Usage 的 JSON 表示（与 <see cref="Pi.Ai.Types.Usage"/> 的 wire 形状一致）。
    /// 对应 TS <c>copyJson(usage, { omitUndefinedProperties: true })</c>：可选桶
    /// （cacheWrite1h / reasoning）仅在存在时写入——注意 reasoning 为 0 时**要写**，
    /// 只有缺失（null）才省略；cost 恒为五桶子对象。
    /// </summary>
    public static Dictionary<string, object?> ToJson(Pi.Ai.Types.Usage usage)
    {
        var json = new Dictionary<string, object?>
        {
            ["input"] = usage.Input,
            ["output"] = usage.Output,
            ["cacheRead"] = usage.CacheRead,
            ["cacheWrite"] = usage.CacheWrite,
            ["totalTokens"] = usage.TotalTokens,
        };
        if (usage.CacheWrite1h is { } cacheWrite1h) json["cacheWrite1h"] = cacheWrite1h;
        if (usage.Reasoning is { } reasoning) json["reasoning"] = reasoning;
        json["cost"] = new Dictionary<string, object?>
        {
            ["input"] = usage.Cost.Input,
            ["output"] = usage.Cost.Output,
            ["cacheRead"] = usage.Cost.CacheRead,
            ["cacheWrite"] = usage.Cost.CacheWrite,
            ["total"] = usage.Cost.Total,
        };
        return json;
    }

    public static Pi.Ai.Types.Usage FromJson(IReadOnlyDictionary<string, object?> json)
    {
        var cost = json.TryGetValue("cost", out var costValue)
            ? costValue as IReadOnlyDictionary<string, object?>
            : null;
        return new Pi.Ai.Types.Usage(
            GetLong(json, "input"),
            GetLong(json, "output"),
            GetLong(json, "cacheRead"),
            GetLong(json, "cacheWrite"))
        {
            CacheWrite1h = json.ContainsKey("cacheWrite1h") ? GetLong(json, "cacheWrite1h") : null,
            Reasoning = json.ContainsKey("reasoning") ? GetLong(json, "reasoning") : null,
            TotalTokens = GetLong(json, "totalTokens"),
            Cost = cost is null
                ? Pi.Ai.Types.UsageCost.Zero
                : new Pi.Ai.Types.UsageCost(
                    GetDouble(cost, "input"),
                    GetDouble(cost, "output"),
                    GetDouble(cost, "cacheRead"),
                    GetDouble(cost, "cacheWrite"),
                    GetDouble(cost, "total")),
        };
    }

    /// <summary>
    /// 在记录响应的提交里，把 usage 加进对话 pi.usage 的一个桶。对应 TS <c>recordUsage</c>。
    /// 变更必须经 Tracker 动词（Set）记录：直接改草稿嵌套字典不会产生 ops，
    /// 已加载化身上的变更会被 <c>Prepare</c> 丢弃且不发布（P53 修正）。
    /// </summary>
    public static async Task RecordUsageAsync(
        ITx tx, ConversationId conversationId, string bucket, string key, Pi.Ai.Types.Usage usage)
    {
        var change = await tx.DocAsync(UsageDoc, conversationId).ConfigureAwait(false);
        if (!change.Draft.TryGetValue(bucket, out var bucketValue)
            || bucketValue is not IReadOnlyDictionary<string, object?> totals)
        {
            throw new InvalidOperationException($"pi.usage bucket {bucket} is missing");
        }

        // 只认自己的键：工具可能叫任何名字。合并结果以整体 Set 写回（等价 TS 的 totals[key] = …）。
        var updated = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in totals) updated[name] = value;
        if (updated.TryGetValue(key, out var existing) && existing is IReadOnlyDictionary<string, object?> totalMap)
        {
            var merged = new Dictionary<string, object?>(totalMap);
            AddUsage(merged, usage);
            updated[key] = merged;
        }
        else
        {
            updated[key] = ToJson(usage);
        }

        // updated 是整个桶字典（拷贝自现有 totals 并合并本键）；在桶根整体 Set，
        // 等价 TS 的 totals[key] = …（Proxy 记录的叶子替换值即合并后的字典）。
        change.Set(Pi.Chord.Delta.Path.Root.Append(Pi.Chord.Delta.Seg.Key(bucket)), updated);
    }

    /// <summary>
    /// 把 usage 的每个计数加到 total。对应 TS <c>addUsage</c>：五个必选计数直接相加，
    /// cacheWrite1h / reasoning 仅当本侧上报时才累加（任一侧上报即开始计），cost 五桶逐项相加。
    /// </summary>
    public static void AddUsage(Dictionary<string, object?> total, Pi.Ai.Types.Usage usage)
    {
        total["input"] = GetLong(total, "input") + usage.Input;
        total["output"] = GetLong(total, "output") + usage.Output;
        total["cacheRead"] = GetLong(total, "cacheRead") + usage.CacheRead;
        total["cacheWrite"] = GetLong(total, "cacheWrite") + usage.CacheWrite;
        total["totalTokens"] = GetLong(total, "totalTokens") + usage.TotalTokens;
        if (usage.CacheWrite1h is { } cacheWrite1h)
            total["cacheWrite1h"] = GetLong(total, "cacheWrite1h") + cacheWrite1h;
        if (usage.Reasoning is { } reasoning)
            total["reasoning"] = GetLong(total, "reasoning") + reasoning;

        if (total.TryGetValue("cost", out var costValue) is false
            || costValue is not Dictionary<string, object?> cost)
        {
            cost = new Dictionary<string, object?>(StringComparer.Ordinal);
            total["cost"] = cost;
        }
        cost["input"] = GetDouble(cost, "input") + usage.Cost.Input;
        cost["output"] = GetDouble(cost, "output") + usage.Cost.Output;
        cost["cacheRead"] = GetDouble(cost, "cacheRead") + usage.Cost.CacheRead;
        cost["cacheWrite"] = GetDouble(cost, "cacheWrite") + usage.Cost.CacheWrite;
        cost["total"] = GetDouble(cost, "total") + usage.Cost.Total;
    }

    /// <summary>把 state 的每个桶并入 sum。对应 TS <c>addUsageState</c>。</summary>
    public static void AddUsageState(
        Dictionary<string, object?> sum, IReadOnlyDictionary<string, object?> state)
    {
        foreach (var bucket in new[] { ModelsBucket, ToolsBucket })
        {
            if (sum[bucket] is not Dictionary<string, object?> sumBucket
                || state[bucket] is not IReadOnlyDictionary<string, object?> stateBucket)
            {
                continue;
            }
            foreach (var (key, usage) in stateBucket)
            {
                if (sumBucket.TryGetValue(key, out var total) && total is Dictionary<string, object?> totalMap
                    && usage is IReadOnlyDictionary<string, object?> usageJson)
                {
                    AddUsage(totalMap, FromJson(usageJson));
                }
                else
                {
                    sumBucket[key] = Pi.Chord.Json.DeepClone(usage);
                }
            }
        }
    }

    private static long GetLong(IReadOnlyDictionary<string, object?> json, string key) =>
        json.TryGetValue(key, out var value)
            ? value switch { long l => l, double d => (long)d, _ => 0 }
            : 0;

    private static double GetDouble(IReadOnlyDictionary<string, object?> json, string key) =>
        json.TryGetValue(key, out var value)
            ? value switch { double d => d, long l => l, _ => 0d }
            : 0d;
}
