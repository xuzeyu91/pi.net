using Pi.Chord.Delta;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using Path = Pi.Chord.Delta.Path;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>一个边界读取的位置：<c>postTools</c>（工具结果之后）或 <c>final</c>（生成移交处）。对应 TS <c>at</c> 参数。</summary>
public enum BoundaryAt
{
    PostTools,

    Final,
}

/// <summary>
/// 内建 <c>pi.inbox</c> 文档：一个对话在边界处等待的提交队列（按 ID 序），以及边界的
/// 准备与放置。对应 TS <c>harness/inbox.ts</c> 全量。
/// </summary>
public static class Inbox
{
    /// <summary>一个对话在边界处等待的提交队列，按 ID 序。对应 TS <c>InboxState</c>。</summary>
    public static readonly DocToken<Dictionary<string, object?>> InboxDoc =
        DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "pi.inbox",
            Version = 1,
            Initial = () => new Dictionary<string, object?> { ["items"] = new List<object?>() },
            CheckpointWhen = (value, _, _) => value.TryGetValue("items", out var items)
                && items is IReadOnlyList<object?> list
                && list.Count == 0,
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Initial),
        });

    private static readonly Path ItemsPath = Path.Root.Append(Seg.Key("items"));

    /// <summary>一个边界在提交的首次表写之前读取的值与最新 head。对应 TS <c>Boundary</c>。</summary>
    public sealed record Boundary
    {
        public required ConversationId ConversationId { get; init; }

        /// <summary>收件箱的 Tracker 变更（草稿随放置而变更）。</summary>
        public required TxDocChange Inbox { get; init; }

        public required QueueMode SteeringMode { get; init; }

        public required QueueMode FollowUpMode { get; init; }

        /// <summary>活动范围的起点（最新 head 标记的 head），被本提交写入的 head 前移。</summary>
        public EntryId? Head { get; set; }
    }

    /// <summary>被选中的用户项（按 ID 序），以及是否放置了 <c>head: "self"</c> 写入（一次重置）。对应 TS <c>BoundaryResult</c>。</summary>
    public sealed record BoundaryResult(IReadOnlyList<SubmissionId> Users, bool Reset);

    /// <summary>
    /// 读取一个边界需要的内容。表读必须先于提交的首次表写，因此调用方在其提交开始处准备边界。
    /// 对应 TS <c>prepareBoundary()</c>（C# 以两个 QueueMode 参数承载 <c>QueueModes</c>）。
    /// </summary>
    public static async Task<Boundary> PrepareBoundaryAsync(
        Transaction tx, ConversationId conversationId, QueueMode steeringMode, QueueMode followUpMode)
    {
        var head = (await tx.LatestHeadMarkerAsync(conversationId).ConfigureAwait(false))?.Head;
        var inbox = await tx.DocAsync(InboxDoc, conversationId).ConfigureAwait(false);
        return new Boundary
        {
            ConversationId = conversationId,
            Inbox = inbox,
            SteeringMode = steeringMode,
            FollowUpMode = followUpMode,
            Head = head,
        };
    }

    /// <summary>
    /// 放置边界选中的排队项（spec §6）：每个写入、第一个或全部 steer，以及 <c>final</c> 处
    /// 第一个或全部 follow-up。被选中的重置把 <c>postTools</c> 边界提升为 <c>final</c>。
    /// 写入先放置、用户项在其后，各按 ID 序，使重置之前排队的用户项在新上下文中运行。
    /// head 指向活动范围（含本提交更早写入开启的范围）之前条目的写入是过期的。
    /// 被选中与过期的项按位置移除。对应 TS <c>applyBoundary()</c>。
    /// </summary>
    public static async Task<BoundaryResult> ApplyBoundaryAsync(
        Transaction tx, Boundary boundary, BoundaryAt at, long now)
    {
        var items = Items(boundary.Inbox.Draft);
        var reset = items.Any(item => item is IReadOnlyDictionary<string, object?> dict
            && dict.TryGetValue("mode", out var mode)
            && mode is "write"
            && dict.TryGetValue("entry", out var entry)
            && entry is IReadOnlyDictionary<string, object?> entryJson
            && entryJson.TryGetValue("head", out var headValue)
            && headValue is "self");
        var final = at == BoundaryAt.Final || reset;

        static List<int> Pick(IReadOnlyList<IReadOnlyDictionary<string, object?>> items, string mode, bool all)
        {
            var indexes = new List<int>();
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index].TryGetValue("mode", out var value) && value as string == mode) indexes.Add(index);
            }

            return all ? indexes : indexes.Take(1).ToList();
        }

        var writes = new List<int>();
        var users = new List<int>();
        for (var index = 0; index < items.Count; index++)
        {
            var mode = items[index].TryGetValue("mode", out var value) ? value as string : null;
            if (mode == "write") writes.Add(index);
        }

        users.AddRange(Pick(items, "steer", boundary.SteeringMode == QueueMode.All));
        if (final) users.AddRange(Pick(items, "followUp", boundary.FollowUpMode == QueueMode.All));
        users.Sort();

        // appendEntry() 拷贝草稿的值；项只在其后移除。
        foreach (var index in writes)
        {
            var item = items[index];
            var draft = JsonTrees.ToEntryDraft((IReadOnlyDictionary<string, object?>)item["entry"]!);
            if (IsStale(boundary, draft))
            {
                tx.SettleSubmission(IdOf(item), new SubmissionSettlement.Unanswered("stale"));
                continue;
            }

            var entry = await tx.AppendEntryAsync(boundary.ConversationId, draft).ConfigureAwait(false);
            if (draft.HeadIsSelf) boundary.Head = entry.Id;
            else if (draft.Head is { } head) boundary.Head = head;
            tx.PlaceSubmission(IdOf(item), entry.Id);
        }

        var placed = new List<SubmissionId>();
        foreach (var index in users)
        {
            var item = items[index];
            var content = JsonTrees.ToContent(item.TryGetValue("content", out var contentValue) ? contentValue : null);
            var entry = await tx.AppendEntryAsync(
                DurableEntries.UserEntry,
                boundary.ConversationId,
                new TypedEntryDraft<object?> { Model = [new Pi.Ai.Types.UserMessage(content, now)] })
                .ConfigureAwait(false);
            tx.PlaceSubmission(IdOf(item), entry.Id);
            placed.Add(IdOf(item));
        }

        var removed = writes.Concat(users).OrderByDescending(index => index);
        foreach (var index in removed) boundary.Inbox.Splice(ItemsPath, index, 1, []);
        return new BoundaryResult(placed, reset);
    }

    /// <summary>head 写入是否指向活动范围之前的条目（放置它会带回已剪除的历史）。对应 TS <c>isStale()</c>。</summary>
    public static bool IsStale(Boundary boundary, EntryDraft entry)
        => entry.Head is not null && boundary.Head is { } head && entry.Head.Value < head.Value;

    /// <summary>移除一个被撤回提交的项；提交由调用方结算。对应 TS <c>removeInboxItem()</c>。</summary>
    public static async Task RemoveInboxItemAsync(Transaction tx, ConversationId conversationId, SubmissionId id)
    {
        var inbox = await tx.DocAsync(InboxDoc, conversationId).ConfigureAwait(false);
        var items = Items(inbox.Draft);
        var index = items.FindIndex(item => IdOf(item) == id);
        if (index >= 0) inbox.Splice(ItemsPath, index, 1, []);
    }

    /// <summary>把一个排队项追加到收件箱草稿（以动词记录，直接改 Draft 列表不产生 op）。对应 TS <c>items.push(…)</c>。</summary>
    internal static void AppendItem(TxDocChange inbox, IReadOnlyDictionary<string, object?> item)
        => inbox.Splice(ItemsPath, Items(inbox.Draft).Count, 0, [item]);

    /// <summary>
    /// 撤回一个对话的每个排队输入，如 <c>Conversation.abort()</c> 与中止级联：
    /// 每个以 <c>aborted</c> 结算为 <c>unanswered</c> 并离开收件箱；排队写入保留待后续放置。
    /// 对应 TS <c>withdrawQueuedInputs()</c>。
    /// </summary>
    public static async Task WithdrawQueuedInputsAsync(Transaction tx, ConversationId conversationId)
    {
        var inbox = await tx.DocAsync(InboxDoc, conversationId).ConfigureAwait(false);
        var items = Items(inbox.Draft);
        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            if (item.TryGetValue("mode", out var mode) && mode is "write") continue;
            tx.SettleSubmission(IdOf(item), new SubmissionSettlement.Unanswered("aborted"));
            inbox.Splice(ItemsPath, index, 1, []);
        }
    }

    private static List<IReadOnlyDictionary<string, object?>> Items(IReadOnlyDictionary<string, object?> draft)
        => draft.TryGetValue("items", out var value) && value is IReadOnlyList<object?> list
            ? [.. list.OfType<IReadOnlyDictionary<string, object?>>()]
            : [];

    private static SubmissionId IdOf(IReadOnlyDictionary<string, object?> item)
        => SubmissionId.From(Convert.ToInt64(item["id"]!));
}
