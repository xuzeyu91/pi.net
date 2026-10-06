using Pi.Ai.Types;
using Pi.Chord;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>ToolTask 的 api 适配器（partial 补充）。</summary>
public static partial class ToolTask
{
    /// <summary>
    /// 为一次调用构建的 <see cref="IToolExecutionApi"/>。对应 TS run() 里的 api 对象：
    /// output / diagnostic / details 在结算后拒绝；details 等待进度提交并随其取消。
    /// </summary>
    private sealed class ToolApi(
        ITaskRuntime runtime,
        ToolCallContent call,
        Reported reported,
        ToolOutput.Progress progress,
        Action assertLive,
        OutputLimits limits) : RuntimeDocumentFaces(runtime, runtime), IToolExecutionApi
    {
        /// <summary>为本次调用构建的环境（执行前注入）；无环境为 null。</summary>
        public IExecutionEnv? Env { get; set; }

        public TaskId<object?> TaskId => runtime.TaskId;

        public ConversationId ConversationId => runtime.ConversationId;

        public string CallId => call.Id;

        public IRegistrySnapshot Registry => runtime.Registry;

        public Task<Agent> AgentAsync(Context context) => runtime.AgentAsync(context);

        public void Output(string chunk, ShellOutputSkip? skipped = null)
        {
            assertLive();
            if (reported.Output.Push(chunk, skipped)) progress.Mark();
        }

        public void Output(byte[] chunk, ShellOutputSkip? skipped = null)
        {
            assertLive();
            if (reported.Output.Push(chunk, skipped)) progress.Mark();
        }

        public ShellOutputWindow? OutputWindow =>
            limits.Retain == OutputRetain.Tail
                ? new ShellOutputWindow
                {
                    MaxBytes = limits.MaxBytes,
                    MaxLines = limits.MaxLines,
                    MinIntervalMs = runtime.Settings.Progress.OutputIntervalMs,
                    BytesPerSecond = ToolOutput.ProgressBytesPerSecond,
                }
                : null;

        public void Diagnostic(ToolDiagnostic diagnostic)
        {
            assertLive();
            reported.Diagnostics.Add(diagnostic);
            progress.Mark();
        }

        public async Task DetailsAsync(object? value, Context context)
        {
            assertLive();
            if (context.AbortSignal is { IsCancellationRequested: true } signal)
                signal.ThrowIfCancellationRequested();
            reported.Details = Json.CopyJson(value, new CopyJsonOptions { OmitUndefinedProperties = true });
            var committed = progress.MarkAndWait();
            // 取消该等待时更新原地保留；提交自身的结局仍被观察（对齐 TS committed.catch(() => {})）。
            _ = committed.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            await ContextSignals.AwaitWithContext(committed, context).ConfigureAwait(false);
        }

        public async Task<T> CommitAsync<T>(Func<ITx, Task<T>> change, Context context)
        {
            T result = default!;
            await runtime.CommitAsync(async (tx, _) =>
            {
                result = await change(tx).ConfigureAwait(false);
                return null;
            }, context).ConfigureAwait(false);
            return result;
        }

        public Task<object?> MemoAsync(string name, Context context) => runtime.MemoAsync(name, context);

        public Task<object> MemoAsync(string name, object candidate, Context context)
            => runtime.MemoAsync(name, candidate, context);

        public Task<TaskId<object?>> CreateTaskAsync(
            AnyDurableTask task, object? input, TaskOptions options, Context context)
            => runtime.CreateTaskAsync(task, input, options, context);

        public Task<TaskRecord?> GetTaskAsync(TaskId<object?> id, Context context)
            => runtime.GetTaskAsync(id, context);

        public Task<SettledTask> WaitForTaskAsync(TaskId<object?> id, Context context)
            => runtime.WaitForTaskAsync(id, context);

        public Task<IConversationHandle?> ConversationAsync(ConversationId id, Context context)
            => runtime.ConversationAsync(id, context);
    }
}
