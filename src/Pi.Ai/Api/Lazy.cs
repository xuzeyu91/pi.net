using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// 延迟流：同步返回事件流，异步准备工作（认证解析、模块加载）在其后运行；
/// 准备失败以 error 终态事件终止流。对应 TS <c>api/lazy.ts</c> 的 <c>lazyStream</c>。
/// </summary>
public static class LazyStream
{
    /// <summary>准备失败时构造的错误终态消息（零用量 + StopReason.Error）。</summary>
    public static AssistantMessage SetupErrorMessage(ModelSpec model, Exception error)
        => new(
            Content: [],
            StopReason: StopReason.Error,
            ErrorMessage: error.Message,
            UsageStats: new Usage(0, 0),
            Model: model.Id,
            Api: model.Api,
            Provider: model.Provider,
            Timestamp: DateTimeOffset.Now.ToUnixTimeMilliseconds());

    /// <summary>
    /// 运行延迟准备并把内层事件流转发到外层流。对应 TS <c>lazyStream</c> +
    /// <c>forwardStream</c>：逐事件 push，随后以 <c>WaitForDoneAsync</c> 取终态收尾。
    /// </summary>
    public static IAssistantMessageEventStream Run(
        ModelSpec model, Func<Task<IAssistantMessageEventStream>> setup)
    {
        var outer = new AssistantMessageEventStream();
        _ = Task.Run(async () =>
        {
            try
            {
                var inner = await setup().ConfigureAwait(false);
                await foreach (var @event in inner.ConfigureAwait(false))
                {
                    outer.Push(@event);
                }
                outer.End(await inner.WaitForDoneAsync().ConfigureAwait(false));
            }
            catch (Exception error)
            {
                var message = SetupErrorMessage(model, error);
                outer.Push(new AssistantMessageEvent.Error(message.StopReason, message.ErrorMessage!, message));
                outer.End(message);
            }
        });
        return outer;
    }
}

/// <summary>延迟 API 的能力开关。对应 TS <c>LazyApiCapabilities</c>。</summary>
public sealed record LazyApiCapabilities
{
    public bool FetchDeferred { get; init; }

    public bool CancelDeferred { get; init; }
}

/// <summary>
/// 把「按需加载的 API 实现」包装成 <see cref="ProviderStreams"/>。对应 TS
/// <c>lazyApi</c>：首次 stream 调用时才加载实现模块；加载失败以 error 终态终止流。
/// </summary>
public static class LazyApi
{
    public static ProviderStreams Create(
        Func<Task<ProviderStreams>> load, LazyApiCapabilities? capabilities = null)
    {
        var streams = new ProviderStreams
        {
            Stream = (model, context, options) => LazyStream.Run(model, async () =>
                (await load().ConfigureAwait(false)).Stream(model, context, options)),
            StreamSimple = (model, context, options) => LazyStream.Run(model, async () =>
                (await load().ConfigureAwait(false)).StreamSimple(model, context, options)),
        };

        if (capabilities?.FetchDeferred == true)
        {
            streams = streams with
            {
                FetchDeferred = (model, handle, options) => LazyStream.Run(model, async () =>
                {
                    var implementation = await load().ConfigureAwait(false);
                    if (implementation.FetchDeferred is null)
                    {
                        throw new ModelsError(ModelsErrorCode.Provider,
                            "API does not support deferred responses");
                    }
                    return implementation.FetchDeferred(model, handle, options);
                }),
            };
        }
        if (capabilities?.CancelDeferred == true)
        {
            streams = streams with
            {
                CancelDeferred = async (model, handle, options, cancellationToken) =>
                {
                    var implementation = await load().ConfigureAwait(false);
                    if (implementation.CancelDeferred is null)
                    {
                        throw new ModelsError(ModelsErrorCode.Provider,
                            "API cannot cancel deferred responses");
                    }
                    await implementation.CancelDeferred(model, handle, options, cancellationToken)
                        .ConfigureAwait(false);
                },
            };
        }

        return streams;
    }
}
