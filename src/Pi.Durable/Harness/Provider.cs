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
}
