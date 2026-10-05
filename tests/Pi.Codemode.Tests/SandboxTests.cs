using System.Text.Json.Nodes;
using Pi.Codemode;
using Pi.Codemode.Runtime;
using Xunit;

namespace Pi.Codemode.Tests;

/// <summary>
/// runtime/host.ts 测试（沙箱编排：调用中继 / 截止 / 中止 / 结果装配）。<para/>
/// TS 侧用真实 QuickJS VM 跑脚本；C# 的 VM 执行是注入点（<see cref="ICodemodeJsEngine"/>），
/// 故这里用按 protocol 回放的假引擎测 host 编排逻辑——prelude 与 JavaScript 语义由注入的
/// 引擎负责，不在本层重复。
/// </summary>
public class SandboxTests
{
    private static CodemodeTool Tool(string name, Func<JsonNode?, CodemodeToolContext, Task<JsonNode?>> execute)
        => new() { Name = name, Execute = execute };

    private static CodemodeSandbox Sandbox(ICodemodeJsEngine engine, params CodemodeTool[] tools)
        => new(new CodemodeSandboxOptions { Tools = tools, Engine = engine });

    [Fact]
    public async Task RelaysToolCallsAndAssemblesTheResult()
    {
        var seen = new List<JsonNode?>();
        var engine = new FakeEngine(async vm =>
        {
            var reply = await CallAsync(vm, 1, "tool", "echo", """{"a":1}""");
            Assert.True(reply.Ok);
            Assert.Equal("""{"a":1}""", reply.Payload);
            await vm.SendAsync(new WorkerToHostMessage.Output(new CodemodeOutputItem.Text("hello")));
            await vm.SendAsync(new WorkerToHostMessage.Done(true, """{"ok":true}""", "[]", null));
        });
        var sandbox = Sandbox(engine, Tool("echo", (args, _) =>
        {
            seen.Add(args);
            return Task.FromResult(args);
        }));

        var result = await sandbox.ExecuteAsync("return 1");

        var success = Assert.IsType<CodemodeResult.Success>(result);
        Assert.Equal("""{"ok":true}""", success.Value?.ToJsonString());
        var item = Assert.IsType<CodemodeOutputItem.Text>(Assert.Single(success.Output));
        Assert.Equal("hello", item.Value);
        var call = Assert.Single(success.Calls);
        Assert.Equal("echo", call.Name);
        Assert.Equal(CodemodeCallStatus.Ok, call.Status);
        Assert.True(call.DurationMs >= 0);
        Assert.Equal("""{"a":1}""", Assert.Single(seen)?.ToJsonString());
        Assert.Empty(success.StoreWrites.Set);
        Assert.Empty(success.StoreWrites.Delete);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task PassesUndefinedArgumentsAndResultsThrough()
    {
        var engine = new FakeEngine(async vm =>
        {
            var reply = await CallAsync(vm, 1, "tool", "noop", null);
            Assert.True(reply.Ok);
            Assert.Null(reply.Payload);
            await vm.SendAsync(new WorkerToHostMessage.Done(true, null, "[]", null));
        });
        var sandbox = Sandbox(engine, Tool("noop", (args, _) =>
        {
            Assert.Null(args);
            return Task.FromResult<JsonNode?>(null);
        }));

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("return 1"));
        Assert.Null(success.Value);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task SerializesResultsWithoutEscapingNonAscii()
    {
        var engine = new FakeEngine(async vm =>
        {
            var reply = await CallAsync(vm, 1, "tool", "greet", null);
            // JS JSON.stringify 不转义非 ASCII；默认 JsonSerializer 会转义为 \uXXXX。
            Assert.Equal("\"中文\"", reply.Payload);
            await vm.SendAsync(new WorkerToHostMessage.Done(true, null, "[]", null));
        });
        var sandbox = Sandbox(engine, Tool("greet", (_, _) =>
            Task.FromResult<JsonNode?>(JsonNode.Parse("\"中文\"")!)));

        await sandbox.ExecuteAsync("return 1");
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task RepliesWithAnErrorForUnknownToolsAndToolFailures()
    {
        var engine = new FakeEngine(async vm =>
        {
            var missing = await CallAsync(vm, 1, "tool", "missing", null);
            Assert.False(missing.Ok);
            Assert.Equal("Unknown tool \"missing\"", missing.Payload);

            var boom = await CallAsync(vm, 2, "tool", "boom", null);
            Assert.False(boom.Ok);
            Assert.Equal("tool exploded", boom.Payload);

            var broken = await CallAsync(vm, 3, "tool", "echo", "{oops");
            Assert.False(broken.Ok);

            await vm.SendAsync(new WorkerToHostMessage.Done(false, null, null, new CodemodeScriptError("script failed")));
        });
        var sandbox = Sandbox(
            engine,
            Tool("boom", (_, _) => throw new InvalidOperationException("tool exploded")),
            Tool("echo", (args, _) => Task.FromResult(args)));

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("x"));
        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        Assert.Equal("script failed", failure.Error.Message);
        Assert.Equal(
            [CodemodeCallStatus.Error, CodemodeCallStatus.Error, CodemodeCallStatus.Error],
            failure.Calls.Select(call => call.Status));
        Assert.Equal(["missing", "boom", "echo"], failure.Calls.Select(call => call.Name));
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task RunsGlobalCallsWithoutRecordingThem()
    {
        var engine = new FakeEngine(async vm =>
        {
            var reply = await CallAsync(vm, 1, "global", "attach", """{"ref":1}""");
            Assert.True(reply.Ok);
            await vm.SendAsync(new WorkerToHostMessage.Done(true, null, "[]", null));
        });
        var seen = new List<JsonNode?>();
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Globals = [Tool("attach", (args, _) =>
            {
                seen.Add(args);
                return Task.FromResult<JsonNode?>(null);
            })],
            Engine = engine,
        });

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("x"));
        Assert.Empty(success.Calls);
        Assert.Equal("""{"ref":1}""", Assert.Single(seen)?.ToJsonString());
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task RepliesWithAnErrorForUnknownGlobals()
    {
        var engine = new FakeEngine(async vm =>
        {
            var reply = await CallAsync(vm, 1, "global", "missing", null);
            Assert.False(reply.Ok);
            Assert.Equal("Unknown global \"missing\"", reply.Payload);
            await vm.SendAsync(new WorkerToHostMessage.Done(true, null, "[]", null));
        });
        var sandbox = Sandbox(engine);

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("x"));
        Assert.Empty(success.Calls);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task RunsConcurrentCallsWithoutBlockingMessageDelivery()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine(async vm =>
        {
            // 若 host 串行处理消息（await 完一个调用才交付下一个），gated 会永远阻塞 open。
            var gated = CallAsync(vm, 1, "tool", "gate", null);
            var opened = CallAsync(vm, 2, "tool", "open", null);
            await Task.WhenAll(gated, opened);
            await vm.SendAsync(new WorkerToHostMessage.Done(true, """["g","o"]""", "[]", null));
        });
        var sandbox = Sandbox(
            engine,
            Tool("gate", async (_, _) =>
            {
                await gate.Task;
                return JsonNode.Parse("\"g\"")!;
            }),
            Tool("open", (_, _) =>
            {
                gate.TrySetResult();
                return Task.FromResult<JsonNode?>(JsonNode.Parse("\"o\"")!);
            }));

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("x"));
        Assert.Equal("""["g","o"]""", success.Value?.ToJsonString());
        Assert.Equal(2, success.Calls.Count);
        Assert.All(success.Calls, call => Assert.Equal(CodemodeCallStatus.Ok, call.Status));
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task CollectsOutputInOrderAndKeepsItAfterAFailure()
    {
        var engine = new FakeEngine(async vm =>
        {
            await vm.SendAsync(new WorkerToHostMessage.Output(new CodemodeOutputItem.Text("a")));
            await vm.SendAsync(new WorkerToHostMessage.Output(new CodemodeOutputItem.Image("QUJD", "image/webp")));
            await vm.SendAsync(new WorkerToHostMessage.Output(new CodemodeOutputItem.Text("b")));
            await vm.SendAsync(new WorkerToHostMessage.Done(false, null, null,
                new CodemodeScriptError("boom") { Name = "TypeError", Stack = "TypeError: boom\n    at codemode.js:2" }));
        });
        var sandbox = Sandbox(engine);

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("x"));
        Assert.Equal(CodemodeErrorKind.Script, failure.Error.Kind);
        Assert.Equal("boom", failure.Error.Message);
        Assert.Equal("TypeError", failure.Error.Name);
        Assert.Contains("codemode.js:2", failure.Error.Stack);
        Assert.Equal(3, failure.Output.Count);
        Assert.IsType<CodemodeOutputItem.Text>(failure.Output[0]);
        var image = Assert.IsType<CodemodeOutputItem.Image>(failure.Output[1]);
        Assert.Equal("QUJD", image.Data);
        Assert.Equal("image/webp", image.MimeType);
        Assert.IsType<CodemodeOutputItem.Text>(failure.Output[2]);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ReportsAVmCrashAsASandboxError()
    {
        var engine = new FakeEngine(async vm =>
        {
            await vm.SendAsync(new WorkerToHostMessage.Crash("QuickJS: trap: out of memory"));
        });
        var sandbox = Sandbox(engine);

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("x"));
        Assert.Equal(CodemodeErrorKind.Sandbox, failure.Error.Kind);
        Assert.Equal("QuickJS: trap: out of memory", failure.Error.Message);
        Assert.True(engine.Vm!.Stopped);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task ReportsEngineStartFailureAsASandboxError()
    {
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions { Engine = new ThrowingEngine() });

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("return 1"));
        Assert.Equal(CodemodeErrorKind.Sandbox, failure.Error.Kind);
        Assert.Equal("engine exploded", failure.Error.Message);
        Assert.Empty(failure.Calls);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task FailsWithGuidanceWhenNoEngineIsInjected()
    {
        var sandbox = new CodemodeSandbox();

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("return 1"));
        Assert.Equal(CodemodeErrorKind.Sandbox, failure.Error.Kind);
        Assert.Contains("requires a JavaScript engine", failure.Error.Message, StringComparison.Ordinal);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task SerializesTheStoreSnapshotAndParsesWrites()
    {
        var engine = new FakeEngine(async vm =>
        {
            await vm.SendAsync(new WorkerToHostMessage.Done(
                true, null, """[["counter","42"],["list","[1,{\"a\":null}]"],["old"]]""", null));
        });
        var sandbox = Sandbox(engine);

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("x",
            new CodemodeExecuteOptions
            {
                Store = new Dictionary<string, JsonNode?>
                {
                    ["counter"] = JsonNode.Parse("41")!,
                    ["obj"] = JsonNode.Parse("""{"a":[1,null]}""")!,
                    ["skip"] = null,
                },
            }));

        var data = engine.Data!;
        Assert.Equal("41", data.Store["counter"]);
        Assert.Equal("""{"a":[1,null]}""", data.Store["obj"]);
        Assert.False(data.Store.ContainsKey("skip"));
        Assert.Equal("42", success.StoreWrites.Set["counter"]?.ToJsonString());
        Assert.Equal("""[1,{"a":null}]""", success.StoreWrites.Set["list"]?.ToJsonString());
        Assert.Equal(["old"], success.StoreWrites.Delete);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task BuildsWorkerDataForTheEngine()
    {
        var engine = new FakeEngine(async vm =>
        {
            await vm.SendAsync(new WorkerToHostMessage.Done(true, null, "[]", null));
        });
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Tools = [Tool("my-tool", (_, _) => Task.FromResult<JsonNode?>(null)) with { Description = "Dashes" }],
            Globals = [Tool("models.list", (_, _) => Task.FromResult<JsonNode?>(null)) with { Spread = true }],
            MemoryLimitBytes = 1024,
            Wasm = "engine-specific-payload",
            Engine = engine,
        });

        await sandbox.ExecuteAsync("const x = 1;");

        var data = engine.Data!;
        Assert.Equal("const x = 1;", data.Code);
        var tool = Assert.Single(data.Tools);
        Assert.Equal("my-tool", tool.Name);
        Assert.Equal("my_tool", tool.JsName);
        Assert.Equal("Dashes", tool.Description);
        var global = Assert.Single(data.Globals);
        Assert.Equal("models.list", global.Name);
        Assert.True(global.Spread);
        Assert.Equal(1024, data.MemoryLimitBytes);
        Assert.Equal("engine-specific-payload", data.Wasm);
        Assert.Empty(data.Store);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task TimesOutAndStopsTheVm()
    {
        var engine = new FakeEngine(vm => Task.Delay(TimeSpan.FromSeconds(30)));
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions { TimeoutMs = 50, Engine = engine });

        var failure = Assert.IsType<CodemodeResult.Failure>(await sandbox.ExecuteAsync("while (true) {}"));
        Assert.Equal(CodemodeErrorKind.Timeout, failure.Error.Kind);
        Assert.Equal("Execution timed out after 50 ms", failure.Error.Message);
        Assert.True(engine.Vm!.Stopped);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task RunsWithoutADeadlineWhenTimeoutIsDisabled()
    {
        var engine = new FakeEngine(async vm =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(60));
            await vm.SendAsync(new WorkerToHostMessage.Done(true, "\"late\"", "[]", null));
        });
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            TimeoutMs = long.MaxValue,
            Engine = engine,
        });

        var success = Assert.IsType<CodemodeResult.Success>(await sandbox.ExecuteAsync("x"));
        Assert.Equal("\"late\"", success.Value?.ToJsonString());
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task AbortsViaSignalAndCancelsInFlightCalls()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource();
        var toolSignal = default(CancellationToken);
        var engine = new FakeEngine(async vm =>
        {
            _ = CallAsync(vm, 1, "tool", "hang", null);
            await entered.Task;
            cts.Cancel();
            // host 已结束后到达的消息被忽略：结果保持 aborted，不产出 value。
            await vm.SendAsync(new WorkerToHostMessage.Done(true, "\"late\"", "[]", null));
        });
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Tools = [Tool("hang", (_, ctx) =>
            {
                toolSignal = ctx.Signal;
                entered.TrySetResult();
                return new TaskCompletionSource<JsonNode?>().Task;
            })],
            Engine = engine,
        });

        var failure = Assert.IsType<CodemodeResult.Failure>(
            await sandbox.ExecuteAsync("x", new CodemodeExecuteOptions { Signal = cts.Token }));
        Assert.Equal(CodemodeErrorKind.Aborted, failure.Error.Kind);
        Assert.Equal("Execution aborted", failure.Error.Message);
        var call = Assert.Single(failure.Calls);
        Assert.Equal("hang", call.Name);
        Assert.Equal(CodemodeCallStatus.Cancelled, call.Status);
        Assert.True(call.DurationMs >= 0);
        Assert.True(toolSignal.IsCancellationRequested);
        Assert.True(engine.Vm!.Stopped);
        await sandbox.CloseAsync();
    }

    [Fact]
    public async Task CloseAbortsInFlightExecutionsAndRejectsNewOnes()
    {
        var entered = new TaskCompletionSource();
        var engine = new FakeEngine(async vm =>
        {
            _ = CallAsync(vm, 1, "tool", "hang", null);
            await entered.Task;
        });
        var sandbox = new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Tools = [Tool("hang", (_, _) =>
            {
                entered.TrySetResult();
                return new TaskCompletionSource<JsonNode?>().Task;
            })],
            Engine = engine,
        });

        var execute = sandbox.ExecuteAsync("x");
        await entered.Task;
        await sandbox.CloseAsync();

        var failure = Assert.IsType<CodemodeResult.Failure>(await execute);
        Assert.Equal(CodemodeErrorKind.Aborted, failure.Error.Kind);
        Assert.Equal("Sandbox closed", failure.Error.Message);
        Assert.Equal(CodemodeCallStatus.Cancelled, Assert.Single(failure.Calls).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sandbox.ExecuteAsync("return 1"));
    }

    [Fact]
    public void RejectsInvalidReservedAndDuplicateGlobalNames()
    {
        foreach (var name in new[] { "a.b.c", "a.", ".a", "tools.x", "store.x", "a.not-valid", "not-valid", "tools", "console" })
        {
            var error = Assert.Throws<ArgumentException>(() => new CodemodeSandbox(new CodemodeSandboxOptions
            {
                Globals = [Tool(name, (_, _) => Task.FromResult<JsonNode?>(null))],
            }));
            Assert.Contains("Invalid global", error.Message, StringComparison.Ordinal);
        }

        var duplicate = Assert.Throws<ArgumentException>(() => new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Globals =
            [
                Tool("attach", (_, _) => Task.FromResult<JsonNode?>(null)),
                Tool("attach", (_, _) => Task.FromResult<JsonNode?>(null)),
            ],
        }));
        Assert.Contains("already registered", duplicate.Message, StringComparison.Ordinal);

        var conflict = Assert.Throws<ArgumentException>(() => new CodemodeSandbox(new CodemodeSandboxOptions
        {
            Globals =
            [
                Tool("models", (_, _) => Task.FromResult<JsonNode?>(null)),
                Tool("models.list", (_, _) => Task.FromResult<JsonNode?>(null)),
            ],
        }));
        Assert.Contains("conflicts with the namespace", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsRegisterAndUnregisterBetweenExecutions()
    {
        var sandbox = new CodemodeSandbox();
        var echo = Tool("echo", (_, _) => Task.FromResult<JsonNode?>(null));

        sandbox.RegisterTool(echo);
        var error = Assert.Throws<ArgumentException>(() => sandbox.RegisterTool(echo));
        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
        Assert.Equal("echo", Assert.Single(sandbox.Tools).Name);

        Assert.True(sandbox.UnregisterTool("echo"));
        Assert.False(sandbox.UnregisterTool("echo"));
        Assert.Empty(sandbox.Tools);
    }

    [Fact]
    public void RejectsDuplicateToolsInOptions()
    {
        var echo = Tool("echo", (_, _) => Task.FromResult<JsonNode?>(null));
        var error = Assert.Throws<ArgumentException>(() => new CodemodeSandbox(
            new CodemodeSandboxOptions { Tools = [echo, echo] }));
        Assert.Contains("already registered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExposesPreludeConstantsAndSubstitutedSource()
    {
        Assert.Equal(256 * 1024, CodemodePrelude.MaxStoreValueChars);
        Assert.Equal(1024 * 1024, CodemodePrelude.MaxStoreTotalChars);
        Assert.Equal(16 * 1024 * 1024, CodemodePrelude.MaxOutputChars);
        Assert.Equal(100_000, CodemodePrelude.MaxOutputItems);

        var source = CodemodePrelude.Source;
        Assert.StartsWith("(function (bridge, toolsJson, globalsJson, storeJson) {", source, StringComparison.Ordinal);
        Assert.EndsWith("})", source, StringComparison.Ordinal);
        Assert.Contains("codemode-prelude.js", source, StringComparison.Ordinal);
        Assert.Contains("ALL_TOOLS", source, StringComparison.Ordinal);
        Assert.Contains("more than the limit of 262144", source, StringComparison.Ordinal);
        Assert.Contains("image expects a non-empty image URL string", source, StringComparison.Ordinal);
        // TS 模板的插值已全部代入。
        Assert.DoesNotContain("${", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadsAndCachesQuickJSWasmPerPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-codemode-{Guid.NewGuid():n}.wasm");
        try
        {
            File.WriteAllBytes(path, [0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]);

            var first = CodemodeWasm.LoadQuickJSWasm(path);
            var second = CodemodeWasm.LoadQuickJSWasm(path);

            Assert.Same(first, second);
            Assert.Equal(path, first.Path);
            Assert.Equal(new byte[] { 0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00 }, first.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RetriesAFailedWasmLoadOnTheNextCall()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-codemode-{Guid.NewGuid():n}.wasm");
        Assert.Throws<FileNotFoundException>(() => CodemodeWasm.LoadQuickJSWasm(path));
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, CodemodeWasm.LoadQuickJSWasm(path).Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>发起一次调用并等待 host 回复（对应脚本里的 <c>await tools.x(...)</c>）。</summary>
    private static async Task<HostToWorkerMessage.Result> CallAsync(
        FakeVm vm, long id, string target, string name, string? args)
    {
        var reply = vm.WaitForResultAsync(id);
        await vm.SendAsync(new WorkerToHostMessage.Call(id, target, name, args));
        return await reply;
    }

    /// <summary>启动即抛异常的引擎（对应 worker 启动失败）。</summary>
    private sealed class ThrowingEngine : ICodemodeJsEngine
    {
        public ICodemodeVm Run(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage)
            => throw new InvalidOperationException("engine exploded");
    }

    /// <summary>假 VM：记录 host 发来的消息；停止后丢弃（对应 TS 中向已终止 worker 发送即丢弃）。</summary>
    private sealed class FakeVm : ICodemodeVm
    {
        private readonly Func<WorkerToHostMessage, Task> deliver;
        private readonly object gate = new();
        private readonly List<HostToWorkerMessage> posted = [];
        private readonly Dictionary<long, TaskCompletionSource<HostToWorkerMessage.Result>> waiters = [];
        private bool stopped;

        internal FakeVm(Func<WorkerToHostMessage, Task> deliver) => this.deliver = deliver;

        internal bool Stopped
        {
            get { lock (this.gate) return this.stopped; }
        }

        /// <summary>脚本 → host 消息（对应 worker 的 parentPort.postMessage）。</summary>
        internal Task SendAsync(WorkerToHostMessage message) => this.deliver(message);

        internal Task<HostToWorkerMessage.Result> WaitForResultAsync(long id)
        {
            var completion = new TaskCompletionSource<HostToWorkerMessage.Result>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (this.gate)
            {
                if (!this.waiters.TryAdd(id, completion)) throw new InvalidOperationException($"duplicate call id {id}");
            }
            return completion.Task;
        }

        public void Post(HostToWorkerMessage message)
        {
            HostToWorkerMessage.Result? result = null;
            TaskCompletionSource<HostToWorkerMessage.Result>? completion = null;
            lock (this.gate)
            {
                if (this.stopped) return;
                this.posted.Add(message);
                if (message is HostToWorkerMessage.Result posted)
                {
                    result = posted;
                    this.waiters.Remove(posted.Id, out var removed);
                    completion = removed;
                }
            }
            if (completion is not null && result is not null) completion.TrySetResult(result);
        }

        public Task StopAsync()
        {
            lock (this.gate) this.stopped = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>按脚本回放的假引擎：不执行 JavaScript，只按 protocol 与 host 对话。</summary>
    private sealed class FakeEngine : ICodemodeJsEngine
    {
        private readonly Func<FakeVm, Task> script;

        internal FakeEngine(Func<FakeVm, Task> script) => this.script = script;

        internal CodemodeWorkerData? Data { get; private set; }

        internal FakeVm? Vm { get; private set; }

        public ICodemodeVm Run(CodemodeWorkerData data, Func<WorkerToHostMessage, Task> onMessage)
        {
            this.Data = data;
            var vm = new FakeVm(onMessage);
            this.Vm = vm;
            // 对应 worker 线程：脚本在后台运行，进度经消息送达 host。
            _ = Task.Run(async () =>
            {
                try
                {
                    await this.script(vm);
                }
                catch
                {
                    // 脚本自身的失败由各测试断言，这里只保证不炸掉测试运行器。
                }
            });
            return vm;
        }
    }
}
