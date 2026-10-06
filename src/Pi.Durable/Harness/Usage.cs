using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 一个对话自身开销的账本：它的条目，以及没有模型开销的压缩总结尝试。对应 TS <c>harness/usage.ts</c> 的
/// <c>UsageState</c>（值是 usage 的 JSON 表示；C# 侧形状随 <see cref="Pi.Ai.Types.Usage"/>：
/// input/output/cacheRead/cacheWrite/cost/reasoning —— TS 另有 totalTokens 与 cost 子对象，见差异记录）。
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

    /// <summary>Usage 的 JSON 表示（与 <see cref="Pi.Ai.Types.Usage"/> 的序列化形状一致，省略 null 开销）。</summary>
    public static Dictionary<string, object?> ToJson(Pi.Ai.Types.Usage usage)
    {
        var json = new Dictionary<string, object?>
        {
            ["input"] = usage.Input,
            ["output"] = usage.Output,
            ["cacheRead"] = usage.CacheRead,
            ["cacheWrite"] = usage.CacheWrite,
        };
        if (usage.Cost is { } cost) json["cost"] = cost;
        if (usage.Reasoning != 0) json["reasoning"] = usage.Reasoning;
        return json;
    }

    public static Pi.Ai.Types.Usage FromJson(IReadOnlyDictionary<string, object?> json) => new(
        GetLong(json, "input"),
        GetLong(json, "output"),
        GetLong(json, "cacheRead"),
        GetLong(json, "cacheWrite"),
        json.TryGetValue("cost", out var cost) && cost is double d ? d : null,
        GetLong(json, "reasoning"));

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

    /// <summary>把 usage 的每个计数加到 total。对应 TS <c>addUsage</c>。</summary>
    public static void AddUsage(Dictionary<string, object?> total, Pi.Ai.Types.Usage usage)
    {
        total["input"] = GetLong(total, "input") + usage.Input;
        total["output"] = GetLong(total, "output") + usage.Output;
        total["cacheRead"] = GetLong(total, "cacheRead") + usage.CacheRead;
        total["cacheWrite"] = GetLong(total, "cacheWrite") + usage.CacheWrite;
        // C# Usage 无独立 totalTokens 计数（计算属性），不存储。
        total["reasoning"] = GetLong(total, "reasoning") + usage.Reasoning;
        if (usage.Cost is { } cost)
            total["cost"] = (total.TryGetValue("cost", out var existing) && existing is double d ? d : 0d) + cost;
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
        json.TryGetValue(key, out var value) && value is long l ? l : 0;
}
