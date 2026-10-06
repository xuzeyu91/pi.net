using Pi.Durable.Types;

namespace Pi.Durable;

/// <summary>任务定义入口。对应 TS <c>tasks.ts</c>。</summary>
public static class DurableTasks
{
    /// <summary>
    /// 定义一个可执行任务；注册进 registry 供 Harness 运行该种类的任务。
    /// 对应 TS <c>defineTask&lt;I, S, R, H&gt;</c>。
    /// </summary>
    public static DurableTask<TInput, TState, TResult> DefineTask<TInput, TState, TResult>(
        TaskDefinition<TInput, TState, TResult> definition) => new() { Definition = definition };
}
