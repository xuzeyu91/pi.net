using Pi.Chord;
using Pi.Chord.Context;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Session;

/// <summary>随 fork 对话创建的一个无定义文档拷贝。对应 TS <c>ForkDocumentCopy</c>。</summary>
public sealed record ForkDocumentCopy
{
    public required DocumentCreate Record { get; init; }

    public required DocumentCopySource Source { get; init; }
}

/// <summary>fork 的文档拷贝准备。对应 TS <c>session/forks.ts</c>。</summary>
public static class Forks
{
    private const int ScanPageSize = 256;

    /// <summary>文档拷贝继承策略。对应 TS <c>ForkPolicy</c>（"asOf" | "current"）。</summary>
    private enum ForkPolicy
    {
        AsOf,
        Current,
    }

    /// <summary>选择一次 fork 拷贝的每个已持久化对话文档。对应 TS <c>prepareForkDocumentCopies</c>。</summary>
    public static async Task<IReadOnlyList<ForkDocumentCopy>> PrepareForkDocumentCopiesAsync(
        IStorage storage,
        ConversationId parentConversationId,
        EntryId at,
        ConversationId childConversationId,
        Context context)
    {
        var entry = await storage.GetEntryAsync(parentConversationId, at).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Entry {at.Value} is not visible from conversation {parentConversationId.Value}");

        var copies = new List<ForkDocumentCopy>();
        var copiedAddresses = new HashSet<string>();
        await CollectCopiesAsync(
            storage,
            new DocumentScope.ConversationScope(entry.Entry.ConversationId),
            new DocumentPoint.AtSeq(entry.CommitSeq),
            ForkPolicy.AsOf,
            childConversationId,
            copies,
            copiedAddresses).ConfigureAwait(false);
        await CollectCopiesAsync(
            storage,
            new DocumentScope.ConversationScope(parentConversationId),
            new DocumentPoint.Current(),
            ForkPolicy.Current,
            childConversationId,
            copies,
            copiedAddresses).ConfigureAwait(false);
        return copies;
    }

    private static async Task CollectCopiesAsync(
        IStorage storage,
        DocumentScope scope,
        DocumentPoint at,
        ForkPolicy policy,
        ConversationId childConversationId,
        List<ForkDocumentCopy> copies,
        HashSet<string> copiedAddresses)
    {
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var page = await storage.ScanDocumentsAsync(
                new DocumentQuery { Scope = scope, At = at }, ScanPageSize, cursor).ConfigureAwait(false);
            foreach (var source in page.Items)
            {
                // 对应 TS：source.scope.kind !== "conversation" || source.fork !== policy 的跳过。
                if (source.Scope is not DocumentScope.ConversationScope sourceScope
                    || source.Fork != ForkOf(policy))
                {
                    continue;
                }
                var id = await storage.MintIdAsync<DocumentId>().ConfigureAwait(false);
                var record = new DocumentCreate
                {
                    Id = id,
                    Kind = source.Kind,
                    Key = source.Key,
                    Scope = new DocumentScope.ConversationScope(childConversationId),
                    History = source.History,
                    Fork = source.Fork,
                };
                // 对应 TS：history === "latest" ? { history: "latest" } : { history: "rewindable" }（字段值取自 source）。
                var copyAddress = DurableDocuments.AddressId(record);
                if (!copiedAddresses.Add(copyAddress))
                {
                    var member = record.Key is null ? record.Kind : $"{record.Kind}/{record.Key}";
                    throw new InvalidOperationException($"Fork selects multiple source documents for {member}");
                }
                copies.Add(new ForkDocumentCopy
                {
                    Record = record,
                    Source = new DocumentCopySource { Id = source.Id, At = at },
                });
            }
            cursor = page.Next;
        }
        while (cursor is not null);
    }

    private static ConversationFork ForkOf(ForkPolicy policy) => policy switch
    {
        ForkPolicy.AsOf => ConversationFork.AsOf,
        _ => ConversationFork.Current,
    };
}
