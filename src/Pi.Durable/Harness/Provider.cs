using Pi.Ai.Utils;
using Pi.Chord.Context;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 内建 <c>pi.provider</c> 文档：一个对话的稳定 provider 面身份。
/// 对应 TS <c>harness/provider.ts</c>（<c>ensureProviderSessionId()</c> 依赖任务运行时
/// <c>TaskRuntime</c>，随任务执行阶段接入）。
/// </summary>
public static class Provider
{
    public static readonly DocToken<Dictionary<string, object?>> ProviderDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "pi.provider",
            Version = 1,
            Initial = () => new Dictionary<string, object?> { ["sessionId"] = Uuid.Uuidv7() },
            CheckpointWhen = (_, _, _) => true,
            // 每个 fork 以全新身份起步，而不是拷贝其父。
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Initial),
        });

    /// <summary>
    /// 返回持久化身份，常规路径不写入。缺少 <c>pi.provider</c> 的旧对话得到一次迁移提交，
    /// 其 <c>tx.doc()</c> 在 provider 请求开始前运行 <c>initial()</c>。对应 TS <c>ensureProviderSessionId()</c>。
    /// </summary>
    public static async Task<string> EnsureProviderSessionIdAsync(ITaskRuntime runtime, Context context)
    {
        var existing = await runtime.SnapshotAsync(ProviderDoc, runtime.ConversationId, context).ConfigureAwait(false);
        if (existing is not null && existing.TryGetValue("sessionId", out var value) && value is string sessionId)
        {
            return sessionId;
        }

        string? created = null;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var change = await tx.DocAsync(ProviderDoc, runtime.ConversationId).ConfigureAwait(false);
            created = change.Draft.TryGetValue("sessionId", out var fresh) && fresh is string id ? id : null;
            return null;
        }, context).ConfigureAwait(false);
        if (created is null)
        {
            throw new InvalidOperationException(
                $"Conversation {runtime.ConversationId} has no provider session ID");
        }

        return created;
    }

    /// <summary>
    /// 构建一次 provider 请求的选项字典：设置里的请求选项 + 调用信号、provider 会话 ID 与推理档位
    /// （off 省略）。C# 的 Models 门面以字典承载请求选项，信号等非 JSON 值按
    /// <c>ProviderStreamOptions.FromDictionary</c> 的约定原样携带。对应 TS <c>…streamOptions, signal, sessionId, reasoning</c> 展开。
    /// </summary>
    public static async Task<Dictionary<string, object?>> RequestOptionsAsync(
        ITaskRuntime runtime, ConversationStreamOptions streamOptions, Pi.Ai.Types.ThinkingLevel thinkingLevel,
        long? maxTokens = null, string? cacheRetention = null)
    {
        var sessionId = await EnsureProviderSessionIdAsync(runtime, Context.Background).ConfigureAwait(false);
        var options = new Dictionary<string, object?>();
        if (streamOptions.Transport is not null) options["transport"] = streamOptions.Transport;
        if (streamOptions.TimeoutMs is { } timeout) options["timeoutMs"] = (long)timeout;
        if (streamOptions.MaxRetries is { } retries) options["maxRetries"] = (long)retries;
        if (streamOptions.MaxRetryDelayMs is { } retryDelay) options["maxRetryDelayMs"] = (long)retryDelay;
        if (streamOptions.Headers is { } headers)
        {
            options["headers"] = headers.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
        }

        if (streamOptions.Metadata is { } metadata)
        {
            options["metadata"] = System.Text.Json.JsonSerializer.SerializeToNode(metadata);
        }

        if (cacheRetention is not null) options["cacheRetention"] = cacheRetention;
        else if (streamOptions.CacheRetention is not null) options["cacheRetention"] = streamOptions.CacheRetention;
        if (streamOptions.Deferred is { } deferred)
        {
            // 与 TS 一致：deferred 透传布尔或 {window} 形状。
            options["deferred"] = deferred.Enabled
                ? true
                : deferred.Window is { } window
                    ? new Dictionary<string, object?> { ["window"] = window }
                    : null;
        }

        if (maxTokens is { } tokens) options["maxTokens"] = tokens;
        options["signal"] = runtime.Signal;
        options["sessionId"] = sessionId;
        if (thinkingLevel != Pi.Ai.Types.ThinkingLevel.Off)
        {
            options["reasoning"] = thinkingLevel switch
            {
                Pi.Ai.Types.ThinkingLevel.Minimal => "minimal",
                Pi.Ai.Types.ThinkingLevel.Low => "low",
                Pi.Ai.Types.ThinkingLevel.Medium => "medium",
                Pi.Ai.Types.ThinkingLevel.High => "high",
                Pi.Ai.Types.ThinkingLevel.XHigh => "xhigh",
                Pi.Ai.Types.ThinkingLevel.Max => "max",
                _ => null,
            };
        }

        return options;
    }
}
