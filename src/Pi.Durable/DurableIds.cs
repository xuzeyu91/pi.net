namespace Pi.Durable;

/// <summary>
/// 可信数字分配 / 解码边界上的 ID 品牌施加。对应 TS <c>ids.ts</c>：
/// TS 的 <c>idFromNumber</c> / <c>seqFromNumber</c> 是类型层品牌擦除；
/// C# 的强类型 ID 需要显式构造，故收敛到静态工厂（storage / 反序列化边界使用）。
/// </summary>
public static class DurableIds
{
    /// <summary>从可信数字构造对话 ID。</summary>
    public static Types.ConversationId ConversationId(long value) => Types.ConversationId.From(value);

    /// <summary>从可信数字构造条目 ID。</summary>
    public static Types.EntryId EntryId(long value) => Types.EntryId.From(value);

    /// <summary>从可信数字构造任务 ID。</summary>
    public static Types.TaskId<TResult> TaskId<TResult>(long value) => Types.TaskId<TResult>.From(value);

    /// <summary>从可信数字构造提交 ID。</summary>
    public static Types.SubmissionId SubmissionId(long value) => Types.SubmissionId.From(value);

    /// <summary>从可信数字构造文档 ID。</summary>
    public static Types.DocumentId DocumentId(long value) => Types.DocumentId.From(value);

    /// <summary>从可信数字构造提交序列。</summary>
    public static Types.Seq Seq(long value) => Types.Seq.From(value);
}
