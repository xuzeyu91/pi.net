using Pi.Ai.Types;
using Pi.Ai.Utils;
using Pi.Chord.Context;
using Pi.Durable.Session;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

using JsonDict = IReadOnlyDictionary<string, object?>;
using TaskStatus = Pi.Durable.Types.TaskStatus;
using TxDocChange = Pi.Chord.Delta.Tracker<IReadOnlyDictionary<string, object?>>.Change;

/// <summary>pi.compaction 任务的检查点（JSON 字典承载，与 TS 形状一致）。</summary>
internal abstract record CompactionCheckpoint
{
    public abstract JsonDict ToJson();

    public static CompactionCheckpoint FromJson(JsonDict json) => json["phase"] switch
    {
        "select" => new Select(),
        "summarize" => Summarize.FromJson(json),
        "retry" => RetryPhase.FromJson(json),
        _ => throw new InvalidOperationException($"Unknown pi.compaction checkpoint phase {json.GetValueOrDefault("phase")}"),
    };

    public sealed record Select : CompactionCheckpoint
    {
        public override JsonDict ToJson() => new Dictionary<string, object?> { ["phase"] = "select" };
    }

    /// <summary>固定的摘要化请求。对应 TS <c>SummaryRequest</c>。</summary>
    public record Summarize : CompactionCheckpoint
    {
        public int Attempt { get; init; }

        public required ModelRef Model { get; init; }

        public required ThinkingLevel ThinkingLevel { get; init; }

        public required ConversationStreamOptions StreamOptions { get; init; }

        public long MaxTokens { get; init; }

        /// <summary>范围选择的上下文中最新条目。</summary>
        public required EntryId Tail { get; init; }

        /// <summary>逐字保留的首个条目；摘要的 <c>head</c>。</summary>
        public required EntryId FirstKept { get; init; }

        public override JsonDict ToJson() => new Dictionary<string, object?>
        {
            ["phase"] = "summarize",
            ["attempt"] = (long)Attempt,
            ["model"] = new Dictionary<string, object?> { ["provider"] = Model.Provider, ["modelId"] = Model.ModelId },
            ["thinkingLevel"] = ThinkingLevel switch
            {
                ThinkingLevel.Off => "off",
                ThinkingLevel.Minimal => "minimal",
                ThinkingLevel.Low => "low",
                ThinkingLevel.Medium => "medium",
                ThinkingLevel.High => "high",
                ThinkingLevel.XHigh => "xhigh",
                ThinkingLevel.Max => "max",
                _ => throw new ArgumentOutOfRangeException(),
            },
            ["streamOptions"] = JsonTrees.ToTree(StreamOptions),
            ["maxTokens"] = MaxTokens,
            ["tail"] = Tail.Value,
            ["firstKept"] = FirstKept.Value,
        };

        public static new Summarize FromJson(JsonDict json)
        {
            var model = (JsonDict)json["model"]!;
            return new Summarize
            {
                Attempt = (int)Convert.ToInt64(json["attempt"]),
                Model = new ModelRef((string)model["provider"]!, (string)model["modelId"]!),
                ThinkingLevel = (string)json["thinkingLevel"]! switch
                {
                    "off" => ThinkingLevel.Off,
                    "minimal" => ThinkingLevel.Minimal,
                    "low" => ThinkingLevel.Low,
                    "medium" => ThinkingLevel.Medium,
                    "high" => ThinkingLevel.High,
                    "xhigh" => ThinkingLevel.XHigh,
                    "max" => ThinkingLevel.Max,
                    var other => throw new InvalidOperationException($"Unknown thinking level {other}"),
                },
                StreamOptions = json.TryGetValue("streamOptions", out var streamOptions) && streamOptions is not null
                    ? JsonTrees.Deserialize<ConversationStreamOptions>(streamOptions)
                    : new ConversationStreamOptions(),
                MaxTokens = Convert.ToInt64(json["maxTokens"]),
                Tail = EntryId.From(Convert.ToInt64(json["tail"]!)),
                FirstKept = EntryId.From(Convert.ToInt64(json["firstKept"]!)),
            };
        }
    }

    public sealed record RetryPhase : Summarize
    {
        public required long Until { get; init; }

        public override JsonDict ToJson()
        {
            var json = new Dictionary<string, object?>(base.ToJson()) { ["phase"] = "retry", ["until"] = Until };
            return json;
        }

        public new static RetryPhase FromJson(JsonDict json)
        {
            var request = Summarize.FromJson(json);
            return From(request, Convert.ToInt64(json["until"]!));
        }

        internal static RetryPhase From(Summarize request, long until)
            => new()
            {
                Attempt = request.Attempt,
                Model = request.Model,
                ThinkingLevel = request.ThinkingLevel,
                StreamOptions = request.StreamOptions,
                MaxTokens = request.MaxTokens,
                Tail = request.Tail,
                FirstKept = request.FirstKept,
                Until = until,
            };
    }
}

/// <summary>
/// 内建压缩任务（spec §8.7）：选择模型上下文的旧前缀，摘要化，并放置 head 指向首个保留条目的摘要条目。
/// 生成拥有的压缩直接阻塞它并直接追加；对话自有的通过写入提交放置摘要。
/// 对应 TS <c>harness/compaction.ts</c> 的 <c>CompactionTask</c>。
/// </summary>
public static partial class Compaction
{
    public const string TaskName = "pi.compaction";

    /// <summary>擦除形状的内建任务定义（输入 / checkpoint / 结果均为 JSON 字典）。</summary>
    public static readonly DurableTask<JsonDict, JsonDict, JsonDict> Instance = DurableTasks.DefineTask(
        new TaskDefinition<JsonDict, JsonDict, JsonDict>
        {
            Name = TaskName,
            Version = 1,
            Initial = _ => new CompactionCheckpoint.Select().ToJson(),
            Phases = new Dictionary<string, object?>
            {
                ["select"] = (TaskPhaseHandler)SelectPhaseAsync,
                ["summarize"] = (TaskPhaseHandler)SummarizePhaseAsync,
                ["retry"] = (TaskPhaseHandler)RetryPhaseAsync,
            },
            Abort = (TaskPhaseHandler)AbortAsync,
        });

    /// <summary>序列化摘要来源保留的最长工具结果文本。</summary>
    private const int ToolResultMaxChars = 2000;

    private const string SummaryPrefix =
        "The conversation history before this point was compacted into the following summary:\n\n<summary>\n";

    private const string SummarySuffix = "\n</summary>";

    private const string SummarizationSystemPrompt =
        "You are a context summarization assistant. Your task is to read a conversation between a user and an AI "
        + "assistant, then produce a structured summary following the exact format specified.\n\n"
        + "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. "
        + "ONLY output the structured summary.";

    private const string SummarizationPrompt =
        "The messages above are a conversation to summarize. Create a structured context checkpoint summary that "
        + "another LLM will use to continue the work. If the conversation starts with an earlier summary, preserve "
        + "its information and fold the newer messages into it.\n\n"
        + "Use this EXACT format:\n\n"
        + "## Goal\n"
        + "[What is the user trying to accomplish? Can be multiple items if the session covers different tasks.]\n\n"
        + "## Constraints & Preferences\n"
        + "- [Any constraints, preferences, or requirements mentioned by user]\n"
        + "- [Or \"(none)\" if none were mentioned]\n\n"
        + "## Progress\n"
        + "### Done\n"
        + "- [x] [Completed tasks/changes]\n\n"
        + "### In Progress\n"
        + "- [ ] [Current work]\n\n"
        + "### Blocked\n"
        + "- [Issues preventing progress, if any]\n\n"
        + "## Key Decisions\n"
        + "- **[Decision]**: [Brief rationale]\n\n"
        + "## Next Steps\n"
        + "1. [Ordered list of what should happen next]\n\n"
        + "## Critical Context\n"
        + "- [Any data, examples, or references needed to continue]\n"
        + "- [Or \"(none)\" if not applicable]\n\n"
        + "Keep each section concise. Preserve exact file paths, function names, and error messages.";

    // ─── 阶段 ───────────────────────────────────────────────────────────────

    private static async Task SelectPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var conversationId = runtime.ConversationId;
        var agent = await runtime.AgentAsync(context).ConfigureAwait(false);
        var settings = runtime.Settings;
        var reference = agent.Model;
        var model = reference is null ? null : runtime.Models.GetModel(reference.Provider, reference.ModelId);
        if (reference is null || model is null)
        {
            await FailNoModelAsync(runtime, reference, context).ConfigureAwait(false);
            return;
        }

        var policy = settings.Compaction;
        var view = await runtime.ContextAsync(conversationId, context).ConfigureAwait(false);
        var cut = SelectCut(view, policy.KeepRecentTokens);
        if (cut is null)
        {
            await CompleteAsync(runtime, context).ConfigureAwait(false);
            return;
        }

        var firstKept = view.Entries[cut.Value].Id;
        var input = ReadInput(task);
        var compaction = new CompactionInput
        {
            Reason = input.Reason,
            Entries = view.Entries.Take(cut.Value).ToList(),
            Messages = SummarizedMessages(view, cut.Value),
            FirstKept = firstKept,
            Instructions = input.Instructions,
        };
        CompactionHookDecision? decision = null;
        await runtime.Hooks.EachAsync("beforeCompact", async handler =>
        {
            if (decision is not null || handler is not ICompactionHooks hooks) return;
            decision = await hooks.BeforeCompact(compaction, runtime, context).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (decision is { Decline: true })
        {
            await CompleteAsync(runtime, context).ConfigureAwait(false);
            return;
        }

        if (decision is { Summary: { Length: > 0 } summary })
        {
            await PlaceAsync(runtime, firstKept, summary, context).ConfigureAwait(false);
            return;
        }

        var request = new CompactionCheckpoint.Summarize
        {
            Attempt = 1,
            Model = reference,
            ThinkingLevel = agent.ThinkingLevel,
            StreamOptions = settings.Stream,
            MaxTokens = Math.Min(
                (long)Math.Floor(0.8 * policy.ReserveTokens),
                model.MaxTokens > 0 ? model.MaxTokens : long.MaxValue),
            Tail = view.Entries.Aggregate(firstKept, (tail, entry) => entry.Id > tail ? entry.Id : tail),
            FirstKept = firstKept,
        };
        await runtime.CommitAsync(
            (_, _) => Task.FromResult<TaskState?>(new TaskState
            {
                Status = TaskStatus.Running, Checkpoint = request.ToJson(),
            }), context).ConfigureAwait(false);
    }

    private static async Task SummarizePhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var request = ReadCheckpoint(task) as CompactionCheckpoint.Summarize
            ?? throw new InvalidOperationException("pi.compaction summarize phase requires a summarize checkpoint");
        var model = runtime.Models.GetModel(request.Model.Provider, request.Model.ModelId);
        if (model is null)
        {
            await FailNoModelAsync(runtime, request.Model, context).ConfigureAwait(false);
            return;
        }

        // `tail` 处的上下文不可变，所以这正是 select 选择的范围。
        var view = await runtime.ContextAsync(runtime.ConversationId, context, request.Tail).ConfigureAwait(false);
                var cut = -1;
        for (var index = 0; index < view.Entries.Count; index++)
        {
            if (view.Entries[index].Id == request.FirstKept)
            {
                cut = index;
                break;
            }
        }

        var now = runtime.Now();
        var input = ReadInput(task);
        var messages = new List<ChatMessage>
        {
            new SystemMessage(SummarizationSystemPrompt, Timestamp: now),
            new UserMessage(
                [new TextContent(SummaryPrompt(SummarizedMessages(view, cut), input.Instructions))],
                now),
        };
        // 延后选项不转发：摘要化不延后。
        var options = await Provider.RequestOptionsAsync(
            runtime, new ConversationStreamOptions { }, request.ThinkingLevel,
            maxTokens: Math.Min(request.MaxTokens, int.MaxValue), cacheRetention: "none").ConfigureAwait(false);
        var message = await runtime.Models.CompleteSimpleAsync(model, messages, options).ConfigureAwait(false);
        // 中止标记或关闭：中止调用或恢复的任务处理已提交状态。
        runtime.Signal.ThrowIfCancellationRequested();
        var summary = SummaryText(message);
        var policy = runtime.Settings.Retry;
        var retry = message.StopReason == StopReason.Error
            && Retry.IsRetryableAssistantError(message)
            && policy.Enabled
            && request.Attempt <= policy.MaxRetries;
        var until = retry ? runtime.Now() + Retry.RetryDelayMs(ToPiRetryPolicy(policy), request.Attempt) : 0L;
        await runtime.CommitAsync(async (tx, current) =>
        {
            await Usage.RecordUsageAsync(
                tx, runtime.ConversationId, "models", $"{message.Provider}/{message.Model}",
                message.UsageStats ?? new Pi.Ai.Types.Usage(0, 0)).ConfigureAwait(false);
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            if (summary is not null)
            {
                return await PlaceSummaryAsync(tx, runtime, current, live, request.FirstKept, summary)
                    .ConfigureAwait(false);
            }

            if (retry)
            {
                var status = Live.FindCompactionStatus(live, runtime.TaskId);
                if (status is not null)
                {
                    Live.ReplaceCompactionStatus(live, status with
                    {
                        RetryBackoff = new CompactionStatus.Retry(until, message.ErrorMessage ?? ""),
                    });
                }

                return new TaskState
                {
                    Status = TaskStatus.Running,
                    Checkpoint = CompactionCheckpoint.RetryPhase.From(request, until).ToJson(),
                };
            }

            Live.RemoveCompactionStatus(live, runtime.TaskId);
            var text = SummaryFailure(message);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError { Message = text, Detail = new Dictionary<string, object?> { ["reason"] = "model_error" } },
                },
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task RetryPhaseAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        var checkpoint = ReadCheckpoint(task) as CompactionCheckpoint.RetryPhase
            ?? throw new InvalidOperationException("pi.compaction retry phase requires a retry checkpoint");
        await runtime.SleepAsync(checkpoint.Until, context).ConfigureAwait(false);
        var attempt = checkpoint.Attempt + 1;
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            var status = Live.FindCompactionStatus(live, runtime.TaskId);
            if (status is not null)
            {
                Live.ReplaceCompactionStatus(live, status with { Attempt = attempt, RetryBackoff = null });
            }

            return new TaskState
            {
                Status = TaskStatus.Running,
                Checkpoint = new CompactionCheckpoint.Summarize
                {
                    Attempt = attempt,
                    Model = checkpoint.Model,
                    ThinkingLevel = checkpoint.ThinkingLevel,
                    StreamOptions = checkpoint.StreamOptions,
                    MaxTokens = checkpoint.MaxTokens,
                    Tail = checkpoint.Tail,
                    FirstKept = checkpoint.FirstKept,
                }.ToJson(),
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task AbortAsync(TaskRecord task, ITaskRuntime runtime, Context context)
    {
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.RemoveCompactionStatus(live, runtime.TaskId);
            return new TaskState
            {
                Status = TaskStatus.Terminal, Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Aborted },
            };
        }, context).ConfigureAwait(false);
    }

    // ─── 创建与放置 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 在本提交里创建压缩任务及其状态。<paramref name="owner"/> 是等待它的生成（阻塞式压缩）；
    /// 无属主时为对话自有，且除手动外为后台。对应 TS <c>createCompaction()</c>。
    /// </summary>
    public static async Task<TaskId<object?>> CreateCompactionAsync(
        Transaction tx, ConversationId conversationId, CompactionReason reason,
        TaskId<object?>? owner = null, string? instructions = null)
    {
        var options = owner is { } ownerTask
            ? new TaskOptions
            {
                Ownership = new TaskOwnership.TaskOwner(ownerTask),
                ConversationId = conversationId,
            }
            : new TaskOptions
            {
                Ownership = new TaskOwnership.ConversationOwner(),
                ConversationId = conversationId,
                Background = reason != CompactionReason.Manual,
            };
        var taskId = await tx.CreateTaskAsync(Instance, InputJson(reason, instructions), options).ConfigureAwait(false);
        Live.AddCompactionStatus(await tx.DocAsync(Live.LiveDoc, conversationId).ConfigureAwait(false),
            new CompactionStatus
            {
                TaskId = TaskId<object?>.From(taskId.Value),
                Reason = reason,
                Blocking = owner is not null,
                Attempt = 1,
                RetryBackoff = null,
            });
        return TaskId<object?>.From(taskId.Value);
    }

    /// <summary>钩子在独立提交里提供的摘要的放置。对应 TS <c>place()</c>。</summary>
    private static async Task PlaceAsync(ITaskRuntime runtime, EntryId firstKept, string summary, Context context)
    {
        await runtime.CommitAsync(async (tx, current) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            return await PlaceSummaryAsync(tx, runtime, current, live, firstKept, summary).ConfigureAwait(false);
        }, context).ConfigureAwait(false);
    }

    /// <summary>
    /// 放置摘要条目并完成（spec §8.7）。生成拥有的阻塞式压缩直接追加：生成持有运行并等待。
    /// 对话自有的把摘要经写入提交受理，空闲时立刻放置，否则下一次边界，或结算 stale。
    /// 对应 TS <c>placeSummary()</c>。
    /// </summary>
    private static async Task<TaskState?> PlaceSummaryAsync(
        Transaction tx, ITaskRuntime runtime, TaskRecord current, TxDocChange live, EntryId firstKept, string summary)
    {
        Live.RemoveCompactionStatus(live, runtime.TaskId);
        var text = $"{SummaryPrefix}{summary}{SummarySuffix}";
        var reason = ReadInput(current).Reason;
        var entry = new EntryDraft
        {
            Kind = DurableEntries.CompactionEntry.Kind,
            Head = firstKept,
            Model = [new UserMessage([new TextContent(text)], runtime.Now())],
            // 数据存严格 JSON 字典（存储深克隆约束）；形状与 CompactionData 一致。
            Data = new Dictionary<string, object?> { ["reason"] = ReasonJson(reason) },
        };
        if (current.Owner is null)
        {
            var settings = runtime.Settings;
            var submission = await Admission.AdmitSubmissionAsync(tx, runtime.ConversationId,
                new SubmissionDraft.Write { RequestId = $"compaction:{runtime.TaskId.Value}", Entry = entry },
                runtime.Now(), settings.SteeringMode, settings.FollowUpMode).ConfigureAwait(false);
            return TerminalCompleted(new Dictionary<string, object?> { ["submissionId"] = submission.Value });
        }

        var placed = await tx.AppendEntryAsync(runtime.ConversationId, entry).ConfigureAwait(false);
        return TerminalCompleted(new Dictionary<string, object?> { ["entryId"] = placed.Id.Value });
    }

    private static TaskState TerminalCompleted(JsonDict result)
        => new()
        {
            Status = TaskStatus.Terminal,
            Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = result },
        };

    /// <summary>移除状态并在无摘要时完成。对应 TS <c>complete()</c>。</summary>
    private static async Task CompleteAsync(ITaskRuntime runtime, Context context)
    {
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.RemoveCompactionStatus(live, runtime.TaskId);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome { Status = TaskOutcomeStatus.Completed, Result = new Dictionary<string, object?>() },
            };
        }, context).ConfigureAwait(false);
    }

    private static async Task FailNoModelAsync(ITaskRuntime runtime, ModelRef? reference, Context context)
    {
        var message = reference is null
            ? "No model is configured"
            : $"Model {reference.Provider}/{reference.ModelId} is not available";
        await runtime.CommitAsync(async (tx, _) =>
        {
            var live = await tx.DocAsync(Live.LiveDoc, runtime.ConversationId).ConfigureAwait(false);
            Live.RemoveCompactionStatus(live, runtime.TaskId);
            return new TaskState
            {
                Status = TaskStatus.Terminal,
                Outcome = new TaskOutcome
                {
                    Status = TaskOutcomeStatus.Failed,
                    Error = new TaskOutcomeError
                    {
                        Message = message, Detail = new Dictionary<string, object?> { ["reason"] = "no_model" },
                    },
                },
            };
        }, context).ConfigureAwait(false);
    }

    // ─── 范围选择与估算 ─────────────────────────────────────────────────────

    /// <summary>
    /// 摘要保留的首个条目在 <c>view.entries</c> 中的下标；无可压缩内容为 null（spec §8.7）。
    /// 从尾部往回保留 <c>keepRecentTokens</c>，再从该条目起取第一个候选：其贡献以用户或助手消息开头的条目，
    /// 绝不是工具结果，也绝不是前一个助手的调用的结果仍跟随其后的用户条目。对应 TS <c>selectCut()</c>。
    /// </summary>
    public static int? SelectCut(ContextView view, long keepRecentTokens)
    {
        var contributions = view.Contributions;
        var start = view.Head is null ? 0 : 1;
        var candidates = new List<int>();
        for (var index = start; index < contributions.Count; index++)
        {
            if (IsCandidate(contributions, index)) candidates.Add(index);
        }

        long kept = 0;
        int? cut = null;
        for (var index = contributions.Count - 1; index >= start; index--)
        {
            foreach (var message in contributions[index]) kept += Estimate.EstimateMessageTokens(message);
            if (kept < keepRecentTokens) continue;
            foreach (var candidate in candidates)
            {
                if (candidate >= index)
                {
                    cut = candidate;
                    break;
                }
            }

            if (cut is null && candidates.Count > 0) cut = candidates[^1];
            break;
        }

        if (cut is null) return null;
        for (var index = start; index < cut; index++)
        {
            if (contributions[index].Count > 0) return cut;
        }

        return null;
    }

    private static bool IsCandidate(IReadOnlyList<IReadOnlyList<ChatMessage>> contributions, int index)
    {
        var first = contributions[index].FirstOrDefault();
        if (first is AssistantMessage) return true;
        if (first is not UserMessage) return false;
        // 前一个助手的调用的、在该条目之后且在下一个助手之前的结果，属于它之前。
        ISet<string> calls = new HashSet<string>(StringComparer.Ordinal);
        for (var before = index - 1; before >= 0; before--)
        {
            var assistant = contributions[before].OfType<AssistantMessage>().LastOrDefault();
            if (assistant is null) continue;
            calls = assistant.ToolCalls.Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
            break;
        }

        if (calls.Count == 0) return true;
        for (var after = index; after < contributions.Count; after++)
        {
            for (var position = 0; position < contributions[after].Count; position++)
            {
                var message = contributions[after][position];
                if (message is AssistantMessage && (after > index || position > 0)) return true;
                if (message is ToolResultMessage result && calls.Contains(result.ToolCallId)) return false;
            }
        }

        return true;
    }

    /// <summary><paramref name="cut"/> 之前条目的模型消息：头标记在前，按模型上下文排序（spec §2.1）。
    /// 对应 TS <c>summarizedMessages()</c>。</summary>
    public static IReadOnlyList<ChatMessage> SummarizedMessages(ContextView view, int cut)
        => ConversationContext.OrderToolResults(view.Contributions.Take(cut).SelectMany(m => m).ToList());

    /// <summary>
    /// 对 <paramref name="view"/> 再附加 <paramref name="extra"/> 的一次请求的大小（spec §8.3）：
    /// 头标记之后最新助手的 usage（其请求包含标记），加上其后消息的估算；没有时全部消息的估算。
    /// 对应 TS <c>estimateContext()</c>。
    /// </summary>
    public static long EstimateContext(ContextView view, IReadOnlyList<ChatMessage> extra)
    {
        AssistantMessage? measured = null;
        var after = view.Head?.Id ?? EntryId.From(long.MinValue);
        for (var index = view.Entries.Count - 1; index >= 0 && measured is null; index--)
        {
            if (view.Entries[index].Id <= after) continue;
            measured = view.Contributions[index].OfType<AssistantMessage>()
                .LastOrDefault(message => Estimate.CalculateContextTokens(message.UsageStats ?? new Pi.Ai.Types.Usage(0, 0)) > 0);
        }

        var from = measured is null
            ? 0
                        : IndexOf(view.Messages, measured) + 1;
        var tokens = measured is null ? 0 : Estimate.CalculateContextTokens(measured.UsageStats ?? new Pi.Ai.Types.Usage(0, 0));
        foreach (var message in view.Messages.Skip(from)) tokens += Estimate.EstimateMessageTokens(message);
        foreach (var message in extra) tokens += Estimate.EstimateMessageTokens(message);
        return tokens;
    }

    // ─── 摘要化 ─────────────────────────────────────────────────────────────

    /// <summary>干净的 stop、有文本且无工具调用的消息才是摘要；其余不是。对应 TS <c>summaryText()</c>。</summary>
    internal static string? SummaryText(AssistantMessage message)
    {
        if (message.StopReason != StopReason.Stop || message.ToolCalls.Any()) return null;
        var text = string.Join("\n", message.Content.OfType<TextContent>().Select(content => content.Text)).Trim();
        return text.Length == 0 ? null : text;
    }

    internal static string SummaryFailure(AssistantMessage message)
    {
        if (message.StopReason is StopReason.Error or StopReason.Aborted)
        {
            return $"Summarization failed: {message.ErrorMessage ?? message.StopReason.ToString().ToLowerInvariant()}";
        }

        if (message.StopReason == StopReason.Length)
        {
            return "Summarization hit the token limit; the summary is incomplete";
        }

        if (message.ToolCalls.Any()) return "Summarization attempted to call a tool";
        return "Summarization produced no text";
    }

    /// <summary>摘要器的用户消息：序列化的对话、提示与可选指令。对应 TS <c>summaryPrompt()</c>。</summary>
    internal static string SummaryPrompt(IReadOnlyList<ChatMessage> messages, string? instructions)
    {
        var focus = instructions is null ? "" : $"\n\nAdditional focus: {instructions}";
        return $"<conversation>\n{SerializeConversation(messages)}\n</conversation>\n\n{SummarizationPrompt}{focus}";
    }

    /// <summary>
    /// 消息的纯文本，让摘要器读 transcript 而非继续对话；系统消息省略。对应 TS <c>serializeConversation()</c>。
    /// </summary>
    public static string SerializeConversation(IReadOnlyList<ChatMessage> messages)
    {
        var parts = new List<string>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case UserMessage user:
                    var text = ContentText(user.Content);
                    if (text.Length > 0) parts.Add($"[User]: {text}");
                    break;
                case AssistantMessage assistant:
                {
                    var thinking = assistant.Content.OfType<ThinkingContent>().Select(block => block.Thinking).ToList();
                    var texts = assistant.Content.OfType<TextContent>().Select(block => block.Text).ToList();
                    var calls = assistant.ToolCalls.Select(call =>
                    {
                        var args = System.Text.Json.JsonSerializer.Serialize(call.Arguments);
                        return $"{call.Name}({args.TrimStart('{').TrimEnd('}')})";
                    }).ToList();
                    if (thinking.Count > 0) parts.Add($"[Assistant thinking]: {string.Join("\n", thinking)}");
                    if (texts.Count > 0) parts.Add($"[Assistant]: {string.Join("\n", texts)}");
                    if (calls.Count > 0) parts.Add($"[Assistant tool calls]: {string.Join("; ", calls)}");
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    var resultText = ContentText(toolResult.Content);
                    if (resultText.Length > 0) parts.Add($"[Tool result]: {Truncate(resultText, ToolResultMaxChars)}");
                    break;
                }
            }
        }

        return string.Join("\n\n", parts);
    }

    private static string ContentText(IReadOnlyList<ContentBlock> content)
        => string.Join("\n", content.OfType<TextContent>().Where(block => block.Text is not null).Select(block => block.Text!));

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars
            ? text
            : $"{text[..maxChars]}\n\n[... {text.Length - maxChars} more characters truncated]";

    // ─── 输入与检查点 ───────────────────────────────────────────────────────

    internal static JsonDict InputJson(CompactionReason reason, string? instructions)
        => new Dictionary<string, object?>
        {
            ["reason"] = reason switch
            {
                CompactionReason.Manual => "manual",
                CompactionReason.Threshold => "threshold",
                CompactionReason.Overflow => "overflow",
                _ => throw new ArgumentOutOfRangeException(nameof(reason)),
            },
            ["instructions"] = instructions,
        };

    internal static CompactionInput ReadInput(TaskRecord task)
    {
        var json = task.Input as JsonDict
            ?? throw new InvalidOperationException("pi.compaction input must be a JSON object");
        return new CompactionInput
        {
            Reason = (string)json["reason"]! switch
            {
                "manual" => CompactionReason.Manual,
                "threshold" => CompactionReason.Threshold,
                "overflow" => CompactionReason.Overflow,
                var other => throw new InvalidOperationException($"Unknown compaction reason {other}"),
            },
            Entries = [],
            Messages = [],
            FirstKept = EntryId.From(0),
            Instructions = json.TryGetValue("instructions", out var value) ? value as string : null,
        };
    }

    internal static CompactionCheckpoint ReadCheckpoint(TaskRecord task)
        => task.State.Checkpoint is JsonDict json
            ? CompactionCheckpoint.FromJson(json)
            : throw new InvalidOperationException("pi.compaction requires a JSON checkpoint");

    private static int IndexOf(IReadOnlyList<ChatMessage> messages, ChatMessage target)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (ReferenceEquals(messages[index], target)) return index;
        }

        return -1;
    }

    internal static string ReasonJson(CompactionReason reason) => reason switch
    {
        CompactionReason.Manual => "manual",
        CompactionReason.Threshold => "threshold",
        CompactionReason.Overflow => "overflow",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    internal static Pi.Ai.Utils.RetryPolicy ToPiRetryPolicy(ConversationRetryPolicy policy) => new()
    {
        Enabled = policy.Enabled,
        MaxRetries = policy.MaxRetries,
        BaseDelayMs = policy.BaseDelayMs,
        MaxAgentDelayMs = policy.MaxAgentDelayMs,
    };
}
