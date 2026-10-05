namespace Pi.Durable;

/// <summary>事务在首次表写之后读表。对应 TS <c>ReadAfterWrite</c>。</summary>
public sealed class ReadAfterWrite : Exception
{
    public ReadAfterWrite(string method)
        : base($"Tx.{method}() cannot read tables after the first table write")
    {
    }
}

/// <summary>storage 在任何持久化效果前拒绝了批次；所属 Session 可安全继续。对应 TS <c>StorageRejected</c>。</summary>
public sealed class StorageRejected : Exception
{
    public StorageRejected(string message)
        : base(message)
    {
    }

    public StorageRejected(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>提交到达忙碌对话且未被受理。对应 TS <c>ConversationBusy</c>。</summary>
public sealed class ConversationBusy : Exception
{
    /// <summary>忙碌的对话 ID。</summary>
    public Types.ConversationId ConversationId { get; }

    public ConversationBusy(Types.ConversationId conversationId)
        : base($"Conversation {conversationId.Value} is busy")
        => ConversationId = conversationId;
}
