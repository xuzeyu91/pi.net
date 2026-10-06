using System.Runtime.CompilerServices;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Harness;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using Xunit;

namespace Pi.Durable.Tests;

using JsonDict = IReadOnlyDictionary<string, object?>;
using DurableHarness = Pi.Durable.Harness.Harness;

/// <summary>
/// 一个确定性的假 provider：每次请求返回一段固定的助手文本，供 Harness 端到端集成测试使用。
/// 对应 TS <c>fauxProvider</c>（chat-support.ts）的最小可用子集。
/// </summary>
internal sealed class FakeProvider : IProvider
{
    private readonly Func<IReadOnlyList<ChatMessage>, AssistantMessage> _respond;

    /// <summary>为 true 时流不结束，直到取消令牌触发（模拟未应答的 provider）。</summary>
    private readonly bool _block;

    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeProvider(string id, Func<IReadOnlyList<ChatMessage>, AssistantMessage> respond, bool block = false)
    {
        Id = id;
        _respond = respond;
        _block = block;
        ModelId = $"{id}-1";
        Models = [new ModelSpec
        {
            Id = ModelId,
            Name = "Fake One",
            Api = "openai-completions",
            Provider = id,
            BaseUrl = "http://fake.local",
            ContextWindow = 128_000,
            MaxTokens = 4_096,
        }];
    }

    /// <summary>未应答模式下：请求已发出的信号。对应 TS <c>unanswered().reached</c>。</summary>
    public Task Reached => _reached.Task;

    public string Id { get; }

    /// <summary>该 provider 暴露的模型 ID（<c>&lt;id&gt;-1</c>）。</summary>
    public string ModelId { get; }

    public string Name => Id;

    public string? BaseUrl => "http://fake.local";

    public IReadOnlyList<ModelSpec> Models { get; }

    public IReadOnlyList<ModelSpec> GetModels() => Models;

    public IAssistantMessageEventStream Stream(
        ModelSpec model, IReadOnlyList<ChatMessage> context, JsonDict? options = null)
        => new FakeStream(_respond(context), _block, _reached);

    public IAssistantMessageEventStream StreamSimple(
        ModelSpec model, IReadOnlyList<ChatMessage> context, JsonDict? options = null)
        => new FakeStream(_respond(context), _block, _reached);

    /// <summary>一次请求的确定性流：start → text_delta… → done；<c>block</c> 时不发 done 直到取消。</summary>
    private sealed class FakeStream(AssistantMessage final, bool block, TaskCompletionSource reached)
        : IAssistantMessageEventStream
    {
        public AssistantMessage? Partial { get; private set; }

        public IAsyncEnumerator<AssistantMessageEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => IterateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        public async Task<AssistantMessage> WaitForDoneAsync(CancellationToken cancellationToken = default)
        {
            var enumerator = IterateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    if (enumerator.Current is AssistantMessageEvent.Done) break;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            return final;
        }

        private async IAsyncEnumerable<AssistantMessageEvent> IterateAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var text = string.Concat(final.Content.OfType<TextContent>().Select(block2 => block2.Text));
            var empty = final with { Content = [] };
            yield return new AssistantMessageEvent.Start(empty);
            var built = "";
            for (var index = 0; index < text.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                built += text[index];
                var partial = final with { Content = [new TextContent(built)] };
                Partial = partial;
                yield return new AssistantMessageEvent.TextDelta(0, text[index].ToString(), index, partial);
                await Task.Yield();
            }

            if (block)
            {
                // 请求已发送；阻塞直到取消（模拟未应答）。
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            Partial = final;
            yield return new AssistantMessageEvent.Done(final.StopReason, final);
        }
    }
}

/// <summary>
/// P56：Harness 装配（harness.ts）的端到端集成测试。以 <see cref="FakeProvider"/> 驱动真实的
/// <c>Harness.OpenAsync</c> → root → submit → 生成，验证整条装配链可用。
/// </summary>
public class HarnessIntegrationTests
{
    private static readonly Context Ctx = Context.Background;

    private static Models NewModels(FakeProvider provider)
    {
        var models = new Models();
        models.SetProvider(provider);
        return models;
    }

    private static HarnessOptions NewOptions(Models models, IRegistry registry, List<object> reports)
        => new()
        {
            Models = models,
            Registry = registry,
            Settings = new HarnessSettings
            {
                Extensions = [],
                Retry = new ConversationRetryPolicy { Enabled = false, MaxRetries = 0, BaseDelayMs = 0 },
            },
            Now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            OnReport = reports.Add,
        };

    private static AssistantMessage Reply(string text)
        => new([new TextContent(text)])
        {
            StopReason = StopReason.Stop,
            Model = "fake-1",
            Provider = "fake",
            UsageStats = new Pi.Ai.Types.Usage(5, 7),
        };

    private static Task<HarnessImpl> OpenAsync(FakeProvider provider, List<object> reports)
        => DurableHarness.OpenAsync(
            new MemoryStorage(), NewOptions(NewModels(provider), Registry.CreateRegistry(), reports), Ctx);

    private static async Task CloseQuietlyAsync(IHarness harness)
    {
        try
        {
            await harness.CloseAsync(Ctx);
        }
        catch
        {
            // 测试收尾不抛。
        }
    }

    [Fact]
    public async Task Open_SucceedsWithBuiltinTasks()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("hi")), []);
        try
        {
            Assert.NotNull(harness);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Open_RejectsRegistryMissingBuiltinTasks()
    {
        var provider = new FakeProvider("fake", _ => Reply("hi"));
        var empty = EmptyRegistry.Create();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DurableHarness.OpenAsync(
            new MemoryStorage(), NewOptions(NewModels(provider), empty, []), Ctx));
        Assert.Contains("built-in tasks", error.Message);
    }

    [Fact]
    public async Task Root_CreatesAndReusesSameConversation()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("hi")), []);
        try
        {
            var first = await harness.RootAsync(Ctx);
            var second = await harness.RootAsync(Ctx);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(DurableIdConstants.RootConversation, first.Id);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Submit_RunsGenerationAndAppendsAssistantEntry()
    {
        var reports = new List<object>();
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("你好，世界")), reports);
        try
        {
            var root = await harness.RootAsync(Ctx, new AgentChange { Model = new ModelRef("fake", "fake-1") });
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("你好")] }, Ctx);
            await submission.WaitAsync(Ctx);
            await root.WaitForIdleAsync(Ctx);

            var view = await root.ContextAsync(Ctx);
            Assert.Contains(view.Entries, entry => entry.Kind == "pi.assistant");
            Assert.Empty(reports);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Configure_AppliesModelToAgent()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("ok")), []);
        try
        {
            var root = await harness.RootAsync(Ctx);
            await root.ConfigureAsync(new AgentChange { Model = new ModelRef("fake", "fake-1") }, Ctx);
            var agent = await root.AgentAsync(Ctx);
            Assert.Equal(new ModelRef("fake", "fake-1"), agent.Model);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Reset_IsAcceptedAsSubmission()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("ok")), []);
        try
        {
            var root = await harness.RootAsync(Ctx);
            await root.ResetAsync("new context", Ctx);
            var view = await root.ContextAsync(Ctx);
            Assert.Contains(view.Entries, entry => entry.Kind == "pi.reset");
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Usage_AggregatesAcrossGeneration()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("counted")), []);
        try
        {
            var root = await harness.RootAsync(Ctx, new AgentChange { Model = new ModelRef("fake", "fake-1") });
            var submission = await root.SubmitAsync(
                new SubmissionDraft.Input { Content = [new TextContent("count")] }, Ctx);
            await submission.WaitAsync(Ctx);
            await root.WaitForIdleAsync(Ctx);

            var usage = await harness.UsageAsync(Ctx);
            Assert.True(usage.Models.ContainsKey("fake/fake-1"));
            Assert.Equal(5, usage.Models["fake/fake-1"].Input);
            Assert.Equal(7, usage.Models["fake/fake-1"].Output);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Inspect_ReportsRunningAfterResume()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("ok")), []);
        try
        {
            await harness.RootAsync(Ctx);
            // open() 之后调度器处于 paused；Resume() 启用调度（对应宿主索取进度）。
            harness.Resume();
            var inspection = await harness.InspectAsync(Ctx);
            Assert.Equal("running", inspection.Scheduling);
            Assert.Empty(inspection.Submissions);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }

    [Fact]
    public async Task Close_ThenOperationsReject()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("ok")), []);
        await harness.CloseAsync(Ctx);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.RootAsync(Ctx));
    }

    [Fact]
    public async Task CreateConversation_IsIndependentlyAddressable()
    {
        var harness = await OpenAsync(new FakeProvider("fake", _ => Reply("ok")), []);
        try
        {
            var created = await harness.CreateConversationAsync(
                new ConversationCreateOptions { Ownership = new ConversationOwnership.Ownerless() }, Ctx);
            Assert.NotEqual(DurableIdConstants.RootConversation, created.Id);
            var found = await harness.ConversationAsync(created.Id, Ctx);
            Assert.NotNull(found);
            Assert.Equal(created.Id, found!.Id);
        }
        finally
        {
            await CloseQuietlyAsync(harness);
        }
    }
}

/// <summary>一个不含任何内建任务的空注册表，用于校验打开时的缺失检查。</summary>
internal static class EmptyRegistry
{
    public static IRegistry Create() => new EmptyRegistryImpl();

    private sealed class EmptyRegistryImpl : IRegistry
    {
        private static readonly IRegistrySnapshot SnapshotValue = new EmptySnapshot();

        public IRegistrySnapshot Snapshot() => SnapshotValue;

        public IDisposable Subscribe(Action listener) => new Nop();

        public void Install(IExtension extension)
        {
        }

        public void Uninstall(IExtension extension)
        {
        }
    }

    private sealed class EmptySnapshot : IRegistrySnapshot
    {
        public IReadOnlyList<IExtension> Installed() => [];

        public IExtension? Extension(string name) => null;

        public IReadOnlyList<(IExtension Extension, IToolRegistration Tool)> Tools() => [];

        public IReadOnlyList<(IExtension Extension, IPromptSection Section)> Sections() => [];

        public IReadOnlyList<AnyDurableTask> Tasks() => [];

        public AnyDurableTask? Task(string name) => null;
    }

    private sealed class Nop : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
