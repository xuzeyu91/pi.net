using Pi.Ai.Api;
using Pi.Ai.Types;

namespace Pi.Ai.Providers;

/// <summary>
/// OpenCode 每会话路由头包装。对应 TS <c>providers/opencode-headers.ts</c>：
/// 缺省从 <c>options.sessionId</c> 取 <c>x-opencode-session</c>，已有同名头则不覆盖。
/// </summary>
public static class OpenCodeHeaders
{
    public const string OpenCodeSessionHeader = "x-opencode-session";

    public static ProviderStreams WithOpenCodeSessionHeader(ProviderStreams streams) => new()
    {
        Stream = (model, context, options) => streams.Stream(model, context, WithSessionHeader(options)),
        StreamSimple = (model, context, options) => streams.StreamSimple(model, context, WithSessionHeader(options)),
    };

    private static IReadOnlyDictionary<string, object?>? WithSessionHeader(
        IReadOnlyDictionary<string, object?>? options)
    {
        if (options is null
            || !options.TryGetValue("sessionId", out var sessionIdValue)
            || sessionIdValue is not string sessionId
            || sessionId.Length == 0)
        {
            return options;
        }

        var headers = options.TryGetValue("headers", out var headersValue)
            ? headersValue as IReadOnlyDictionary<string, string?>
            : null;
        if (headers is not null
            && headers.Keys.Any(key => string.Equals(key, OpenCodeSessionHeader, StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        var merged = new Dictionary<string, string?>(headers ?? new Dictionary<string, string?>())
        {
            [OpenCodeSessionHeader] = sessionId,
        };
        return new Dictionary<string, object?>(options) { ["headers"] = merged };
    }
}
