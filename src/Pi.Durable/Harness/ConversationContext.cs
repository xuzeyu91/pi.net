using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// 锁定一个已提交上下文范围的 head 标记与最新可见条目。对应 TS harness <c>context.ts</c>
/// 的 <c>ContextBounds</c>（TS 的 head 是带 head 的条目；C# 以 <see cref="EntryRecord.Head"/>
/// 非空表达同一事实）。
/// </summary>
public sealed record ContextBounds(EntryRecord? Head, EntryId Tail);

/// <summary>
/// 对话上下文的捕获与派生（对应 TS <c>harness/context.ts</c> 全量）：
/// <see cref="CaptureContextBoundsAsync"/> 在 Session 线上两次 O(1) 读锁定范围；
/// <see cref="DeriveContextAsync"/> 可离线扫描派生活动转录与模型上下文；
/// <see cref="OrderToolResults"/> 按调用顺序排放工具结果。
/// </summary>
public static class ConversationContext
{
    private const int ScanPageSize = 256;

    private static readonly HashSet<StopReason> ExcludedStopReasons =
        [StopReason.Aborted, StopReason.Error, StopReason.Deferred];

    private const string MissingResultText =
        "Tool result unavailable: history ends before this call completed.";

    /// <summary>
    /// 捕获当前上下文（或截止到可见条目 <paramref name="at"/> 的上下文）的范围。
    /// 必须运行在 Session 线上；tail 及以下的条目不可变，派生可离线进行。
    /// </summary>
    public static async Task<ContextBounds?> CaptureContextBoundsAsync(
        IStorage storage, ConversationId conversationId, Context context, EntryId? at = null)
    {
        _ = context;
        EntryId? tail;
        if (at is null)
        {
            var page = await storage.ScanEntriesAsync(
                new EntryQuery { ConversationId = conversationId }, 1).ConfigureAwait(false);
            tail = page.Items.Count > 0 ? page.Items[0].Id : null;
            if (tail is null) return null;
        }
        else
        {
            if (await storage.GetEntryAsync(conversationId, at.Value).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException(
                    $"Entry {at.Value} is not visible from conversation {conversationId}");
            }
            tail = at;
        }

        return new ContextBounds(
            await storage.FindLatestHeadMarkerAsync(conversationId, tail).ConfigureAwait(false),
            tail.Value);
    }

    /// <summary>一个对话的已提交上下文：范围在线上捕获，条目离线派生。对应 TS <c>readContext</c>。</summary>
    public static async Task<ContextView> ReadContextAsync(
        DurableSession session, IStorage storage, ConversationId conversationId, Context context,
        EntryId? at = null)
    {
        var bounds = await session.ReadOnLineAsync(
            () => CaptureContextBoundsAsync(storage, conversationId, context, at)).ConfigureAwait(false);
        return await DeriveContextAsync(storage, conversationId, bounds, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 在捕获的范围之内派生活动转录与模型上下文。对应 TS <c>deriveContext</c>。
    /// </summary>
    public static async Task<ContextView> DeriveContextAsync(
        IStorage storage, ConversationId conversationId, ContextBounds? bounds, Context context)
    {
        if (bounds is null)
        {
            return new ContextView
            {
                Entries = [],
                Contributions = [],
                Messages = [],
            };
        }

        var head = bounds.Head;
        var range = await ScanRangeAsync(storage, conversationId, bounds, context).ConfigureAwait(false);
        // 范围内每个条目的编辑都计入，包括 selectActive 丢弃的较旧 head 标记。
        var edits = new Dictionary<EntryId, ContextEdit>();
        foreach (var entry in range)
        {
            if (entry.Edits is null) continue;
            foreach (var edit in entry.Edits)
            {
                // 判别联合的 target 在变体上（Omit/Replace 的位置参数）。
                edits[edit switch
                {
                    ContextEdit.Omit omit => omit.Target,
                    ContextEdit.Replace replace => replace.Target,
                    _ => throw new ArgumentOutOfRangeException(nameof(edit)),
                }] = edit;
            }
        }

        var entries = SelectActive(head, range);
        var contributions = new List<IReadOnlyList<ChatMessage>>();
        foreach (var entry in entries)
        {
            if (edits.TryGetValue(entry.Id, out var edit) && edit is ContextEdit.Omit)
            {
                contributions.Add([]);
                continue;
            }

            var contributed = edit is ContextEdit.Replace replace
                ? replace.Messages
                : entry.Model ?? [];
            var filtered = contributed
                .Where(message => message is not AssistantMessage assistant
                    || !ExcludedStopReasons.Contains(assistant.StopReason))
                .ToList();
            contributions.Add(filtered);
        }

        var messages = OrderToolResults(contributions.SelectMany(m => m).ToList());
        return new ContextView
        {
            Head = head,
            Entries = entries,
            Contributions = contributions,
            Messages = messages,
        };
    }

    /// <summary>捕获范围内的原始活动条目，不派生模型上下文。对应 TS <c>activeEntries</c>。</summary>
    public static async Task<IReadOnlyList<EntryRecord>> ActiveEntriesAsync(
        IStorage storage, ConversationId conversationId, ContextBounds? bounds, Context context)
    {
        if (bounds is null) return [];
        return SelectActive(
            bounds.Head,
            await ScanRangeAsync(storage, conversationId, bounds, context).ConfigureAwait(false));
    }

    /// <summary>从 head 标记的 head（或转录起点）到 tail 的可见条目，最旧在前。</summary>
    private static async Task<List<EntryRecord>> ScanRangeAsync(
        IStorage storage, ConversationId conversationId, ContextBounds bounds, Context context)
    {
        _ = context;
        var head = bounds.Head;
        var range = new List<EntryRecord>();
        IReadOnlyDictionary<string, object?>? cursor = null;
        do
        {
            var query = head is null
                ? new EntryQuery { ConversationId = conversationId, MaxEntryId = bounds.Tail }
                : new EntryQuery
                {
                    ConversationId = conversationId,
                    MinEntryId = head.Head!.Value,
                    MaxEntryId = bounds.Tail,
                };
            var page = await storage.ScanEntriesAsync(query, ScanPageSize, cursor).ConfigureAwait(false);
            range.AddRange(page.Items);
            cursor = page.Next;
        }
        while (cursor is not null);

        // 扫描按最新优先返回；范围内要求最旧在前。
        range.Reverse();
        return range;
    }

    /// <summary>head 标记 + 范围内的非 head 条目；无标记时即整个范围。对应 TS <c>selectActive</c>。</summary>
    private static List<EntryRecord> SelectActive(EntryRecord? head, List<EntryRecord> range)
        => head is null
            ? range
            : [head, .. range.Where(entry => entry.Head is null)];

    /// <summary>
    /// 把每个 assistant 的工具结果按调用顺序直接排在其后。结果取自下一个 assistant 之前的消息；
    /// 缺失的结果合成，未匹配的结果丢弃。对应 TS <c>orderToolResults</c>。
    /// </summary>
    public static IReadOnlyList<ChatMessage> OrderToolResults(IReadOnlyList<ChatMessage> messages)
    {
        var ordered = new List<ChatMessage>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message is ToolResultMessage) continue;
            ordered.Add(message);
            if (message is not AssistantMessage assistant) continue;
            var calls = assistant.Content
                .OfType<ToolCallContent>()
                .ToList();
            if (calls.Count == 0) continue;

            var results = new Dictionary<string, int>();
            for (var next = index + 1;
                 next < messages.Count && messages[next] is not AssistantMessage;
                 next++)
            {
                if (messages[next] is ToolResultMessage candidate
                    && !results.ContainsKey(candidate.ToolCallId))
                {
                    results[candidate.ToolCallId] = next;
                }
            }

            foreach (var call in calls)
            {
                ordered.Add(
                    results.TryGetValue(call.Id, out var resultIndex)
                        ? messages[resultIndex]
                        : MissingResult(call, assistant.Timestamp ?? 0));
            }
        }

        return ordered;
    }

    private static ToolResultMessage MissingResult(ToolCallContent call, long timestamp)
        => new(call.Id, call.Name, [new TextContent(MissingResultText)], IsError: true,
            Details: new Dictionary<string, object?> { ["reason"] = "missing_result" },
            Timestamp: timestamp);
}
