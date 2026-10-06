using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Session;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 移植 <c>packages/durable/test/harness-generation.test.ts</c>：内建 <c>pi.generation</c> 的端到端行为
/// ——应答并结算提交、模型解析失败（no_model / model_error）、重试策略（启用/耗尽/禁用）、
/// settings 经 getter 在每个决策点读取等。
/// <para>
/// 已移植（本期）：<c>AnswersInputAndSettlesItsSubmission</c>（基本生成 happy-path：条目、归属、任务终态）；
/// <c>FailsRetryableErrorWithoutRetryingWhenPolicyDisabled</c>（重试禁用时一次即 model_error）；
/// <c>FailsNonRetryableErrorWithoutRetrying</c>（非重试错误一次即 model_error，任务 failed）。
/// </para>
/// <para>
/// 待续（需扩展 C# 聊天夹具/生成管线，见各 TODO）：
/// 流式限流与 partial 节流（<c>tokensPerSecond</c>/<c>tokenSize</c>/<c>partialIntervalMs</c>）——需支持令牌流的 faux provider；
/// deferred 轮询与取消（<c>pendingFetches</c>/<c>pollAfterMs</c>/<c>cancelDeferred</c>）——需 faux provider 的 deferred 流；
/// section 包装与 <c>input.read</c> 读文档——需 <c>AddSection</c> + <c>wrapSection</c> 扩展；
/// provider session id 转发（#10424）——需 <c>SimpleStreamOptions</c> 捕获 + <c>ProviderDoc</c>；
/// 重试退避后成功（<c>retry</c> 启用）——需可观测的退避时钟与 attempt 计数；
/// 顽固任务孤岛化（<c>task_too_old</c>）——需 <c>commitWith</c> + 版本不匹配任务；
/// <c>resolveSettings</c> 默认值已被 <see cref="HarnessDocsTests"/> 覆盖，不重复移植。
/// </para>
/// </summary>
public sealed class HarnessGenerationTests
{
    private static readonly Context Ctx = HarnessTestSupport.Ctx;

    /// <summary>一个纯文本 assistant 应答。对应 TS <c>fauxAssistantMessage(text)</c>。</summary>
    private static AssistantMessage Answer(string text) => HarnessTestSupport.AssistantReply(text);

    /// <summary>一个以 <c>error</c> 停止原因结束的空应答。对应 TS <c>fauxAssistantMessage([], {stopReason:"error"})</c>。</summary>
    private static AssistantMessage ErrorReply(string message)
        => new([])
        {
            StopReason = StopReason.Error,
            ErrorMessage = message,
            Model = "faux-1",
            Provider = "faux",
            UsageStats = new Pi.Ai.Types.Usage(0, 0),
        };

    /// <summary>某条消息的文本（system 无文本）。对应 TS <c>textOf()</c>。</summary>
    private static string? TextOf(ChatMessage? message) => message switch
    {
        null => null,
        SystemMessage => null,
        UserMessage user => user.Content.OfType<TextContent>().FirstOrDefault()?.Text,
        AssistantMessage assistant => assistant.Content.OfType<TextContent>().FirstOrDefault()?.Text,
        _ => null,
    };

    /// <summary>一次输入提交的持久化记录。对应 TS <c>submission.status()</c>。</summary>
    private static async Task<SubmissionRecord.InputRecord> InputRecordAsync(ISubmission submission)
        => Assert.IsType<SubmissionRecord.InputRecord>(await submission.StatusAsync(Ctx));

    /// <summary>扫描某对话的全部任务。对应 TS <c>harness.commit((tx) => tx.scanTasks(...))</c>。</summary>
    private static async Task<IReadOnlyList<TaskRecord>> TasksAsync(HarnessImpl harness, ConversationId conversationId)
        => (await harness.CommitAsync(
            tx => tx.ScanTasksAsync(new TaskQuery { ConversationId = conversationId }, 10), Ctx)).Items;

    // ─── 1. 基本生成：应答并结算提交 ──────────────────────────────────────────

    [Fact]
    public async Task AnswersInputAndSettlesItsSubmission()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(Answer("Hello there")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            await submission.WaitAsync(Ctx);
            var record = await InputRecordAsync(submission);
            Assert.Equal(SubmissionStatus.Done, record.Status);

            var entries = await HarnessTestSupport.AllEntriesAsync(root);
            Assert.Equal(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind));
            var answer = entries.Single(entry => entry.Id == record.Answer);
            Assert.Equal("Hello there", TextOf(answer.Model?.FirstOrDefault()));

            // 生成写入的 assistant 条目归属于生成任务；被准入的 user 条目不是任务工作。
            var task = Assert.Single(await TasksAsync(harness, root.Id));
            Assert.Equal("pi.generation", task.Kind);
            Assert.Equal(Pi.Durable.Types.TaskStatus.Terminal, task.State.Status);
            Assert.Equal(TaskOutcomeStatus.Completed, task.State.Outcome?.Status);
            Assert.Null(entries[0].ByTaskId);
            Assert.Equal(task.Id, answer.ByTaskId);

            // 生成结束后 live 归空。
            Assert.Empty((await harness.SnapshotAsync(Live.LiveDoc, root.Id, Ctx))!);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 2. 重试禁用：可重试错误一次即 model_error ─────────────────────────────

    [Fact]
    public async Task FailsRetryableErrorWithoutRetryingWhenPolicyDisabled()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        // 默认 NewChatSetup 的 retry 已禁用（Enabled=false, MaxRetries=0）。
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(ErrorReply("503 Service Unavailable")),
            HarnessTestSupport.FakeStep.Of(Answer("never")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            await submission.WaitAsync(Ctx);
            var record = await InputRecordAsync(submission);
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("model_error", record.Reason);
            // 重试禁用：仅一次调用，"never" 未被取用。
            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }

    // ─── 3. 非重试错误：一次即 model_error，任务 failed ─────────────────────────

    [Fact]
    public async Task FailsNonRetryableErrorWithoutRetrying()
    {
        var provider = new HarnessTestSupport.ScriptedProvider();
        var setup = HarnessTestSupport.NewChatSetup(provider);
        provider.SetResponses(HarnessTestSupport.FakeStep.Of(ErrorReply("Invalid request")),
            HarnessTestSupport.FakeStep.Of(Answer("never")));
        var (harness, root) = await HarnessTestSupport.OpenChatAsync(new MemoryStorage(), setup);
        try
        {
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("hi")] }, Ctx);
            await submission.WaitAsync(Ctx);
            var record = await InputRecordAsync(submission);
            Assert.Equal(SubmissionStatus.Unanswered, record.Status);
            Assert.Equal("model_error", record.Reason);
            Assert.Equal(1, provider.CallCount);

            var task = Assert.Single(await TasksAsync(harness, root.Id));
            Assert.Equal(Pi.Durable.Types.TaskStatus.Terminal, task.State.Status);
            Assert.Equal(TaskOutcomeStatus.Failed, task.State.Outcome?.Status);
        }
        finally
        {
            await HarnessTestSupport.CloseQuietlyAsync(harness);
        }
    }
}
