namespace Pi.Client;

/// <summary>
/// 显式的 promise 分解器。对应 TS <c>createPromiseResolvers</c>（promise.ts）。
/// <para>TS 需要这个辅助是因为 ES2024 之前没有 <c>Promise.withResolvers()</c>；
/// C# 的 <see cref="TaskCompletionSource{TResult}"/> 本身就是该物，故此处只做薄包装以保持结构对应。</para>
/// </summary>
public static class PromiseResolvers
{
    public static TaskCompletionSource<T> Create<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
