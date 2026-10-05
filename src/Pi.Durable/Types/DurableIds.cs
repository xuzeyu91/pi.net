namespace Pi.Durable.Types;

/// <summary>
/// 持久化记录的数字 ID 族。对应 TS <c>types.ts</c> 的 <c>Id&lt;Kind, Type&gt;</c> 名义品牌：
/// TS 用 <c>number &amp; brand</c> 区分 ID 种类；C# 用强类型 <see cref="long"/> 包装，
/// storage 边界经 <see cref="DurableIds"/>（对应 TS <c>ids.ts</c>）显式转换。
/// </summary>
/// <param name="Value">底层数字（TS 侧为 safe integer）。</param>
public readonly record struct ConversationId(long Value)
{
    /// <summary>从可信数字构造（storage / 反序列化边界）。</summary>
    public static ConversationId From(long value) => new(value);

    /// <summary>隐式转数字（JSON wire / 日志）。</summary>
    public static implicit operator long(ConversationId id) => id.Value;
}

/// <summary>Transcript 条目 ID。</summary>
public readonly record struct EntryId(long Value)
{
    public static EntryId From(long value) => new(value);
    public static implicit operator long(EntryId id) => id.Value;
}

/// <summary>
/// 任务 ID。<typeparamref name="TResult"/> 承载任务结果类型（对应 TS <c>TaskId&lt;Result&gt;</c>
/// 的第二品牌参数）；非泛型用法取 <see cref="object"/>。
/// </summary>
public readonly record struct TaskId<TResult>(long Value)
{
    public static TaskId<TResult> From(long value) => new(value);
    public static implicit operator long(TaskId<TResult> id) => id.Value;
}

/// <summary>提交（用户输入 / 被动写入）ID。</summary>
public readonly record struct SubmissionId(long Value)
{
    public static SubmissionId From(long value) => new(value);
    public static implicit operator long(SubmissionId id) => id.Value;
}

/// <summary>文档化身（incarnation）ID；同一逻辑文档重建不复用。</summary>
public readonly record struct DocumentId(long Value)
{
    public static DocumentId From(long value) => new(value);
    public static implicit operator long(DocumentId id) => id.Value;
}

/// <summary>原子存储提交的严格递增序列（允许空洞）。对应 TS <c>Seq</c>。</summary>
public readonly record struct Seq(long Value)
{
    public static Seq From(long value) => new(value);
    public static implicit operator long(Seq seq) => seq.Value;
}

/// <summary>ID 常量。对应 TS <c>types.ts</c> 的 <c>ROOT_CONVERSATION_ID</c>。</summary>
public static class DurableIdConstants
{
    /// <summary>根对话的保留 ID。</summary>
    public static readonly ConversationId RootConversation = ConversationId.From(1);
}
