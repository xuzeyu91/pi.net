namespace Pi.Ai.Utils;

/// <summary>
/// HTTP(S) 代理解析（含 <c>no_proxy</c> 规则）。对应 TS <c>utils/node-http-proxy.ts</c>：
/// 按 <c>&lt;scheme&gt;_proxy</c> / <c>all_proxy</c> 取代理，SOCKS/PAC 不支持。
/// </summary>
public static class NodeHttpProxy
{
    /// <summary>SOCKS / PAC 代理不支持时的提示。对应 TS <c>UNSUPPORTED_PROXY_PROTOCOL_MESSAGE</c>。</summary>
    public const string UnsupportedProxyProtocolMessage =
        "Unsupported proxy protocol. SOCKS and PAC proxy URLs are not supported; use an HTTP or HTTPS proxy URL.";

    private static readonly IReadOnlyDictionary<string, int> DefaultProxyPorts = new Dictionary<string, int>
    {
        ["ftp"] = 21,
        ["gopher"] = 70,
        ["http"] = 80,
        ["https"] = 443,
        ["ws"] = 80,
        ["wss"] = 443,
    };

    private static string GetProxyEnv(string key, IReadOnlyDictionary<string, string>? env)
    {
        var lowercaseKey = key.ToLowerInvariant();
        var uppercaseKey = key.ToUpperInvariant();
        return (env is not null && env.TryGetValue(lowercaseKey, out var lowerScoped) && lowerScoped.Length > 0
                ? lowerScoped
                : null)
            ?? (env is not null && env.TryGetValue(uppercaseKey, out var upperScoped) && upperScoped.Length > 0
                ? upperScoped
                : null)
            ?? ProviderEnvValue.Get(lowercaseKey)
            ?? ProviderEnvValue.Get(uppercaseKey)
            ?? "";
    }

    private static Uri? ParseProxyTargetUrl(string targetUrl)
        => Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) ? uri : null;

    private static string StripBrackets(string host)
        => host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;

    private static (string Host, int Port)? ParseNoProxyEntry(string entry)
    {
        var trimmed = entry.Trim().ToLowerInvariant();
        if (trimmed.Length == 0) return null;

        if (trimmed.StartsWith('['))
        {
            var closingBracket = trimmed.IndexOf(']');
            if (closingBracket != -1)
            {
                var host = trimmed[1..closingBracket];
                var rest = trimmed[(closingBracket + 1)..];
                if (rest.StartsWith(':'))
                {
                    return int.TryParse(rest[1..], out var port) ? (host, port) : (host, 0);
                }
                return (host, 0);
            }
        }

        // 裸 IPv6（多于一段冒号）→ 视为主机，无端口。
        if (trimmed.Contains(':') && trimmed.Split(':').Length > 2) return (trimmed, 0);

        var colonIndex = trimmed.LastIndexOf(':');
        if (colonIndex != -1 && colonIndex == trimmed.IndexOf(':'))
        {
            var host = trimmed[..colonIndex];
            if (int.TryParse(trimmed[(colonIndex + 1)..], out var port)) return (host, port);
        }

        return (trimmed, 0);
    }

    /// <summary>目标主机是否应走代理（<c>no_proxy</c> 命中则不走）。对应 TS <c>shouldProxyHostname</c>。</summary>
    public static bool ShouldProxyHostname(string hostname, int port, IReadOnlyDictionary<string, string>? env = null)
    {
        var noProxy = GetProxyEnv("no_proxy", env).ToLowerInvariant();
        if (noProxy.Length == 0) return true;
        if (noProxy == "*") return false;

        var normalizedTargetHost = StripBrackets(hostname.ToLowerInvariant());

        return noProxy.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).All(entry =>
        {
            var parsed = ParseNoProxyEntry(entry);
            if (parsed is not { } candidate) return true;
            if (candidate.Port != 0 && candidate.Port != port) return true;

            var domain = StripBrackets(candidate.Host);
            if (domain.StartsWith("*.")) domain = domain[2..];
            else if (domain.StartsWith('.') || domain.StartsWith('*')) domain = domain[1..];

            if (domain.Length == 0) return true;
            if (normalizedTargetHost == domain) return false;
            if (normalizedTargetHost.EndsWith($".{domain}", StringComparison.Ordinal)) return false;
            return true;
        });
    }

    /// <summary>目标 URL 应使用的代理地址（无则空串）。对应 TS <c>getProxyForUrl</c>。</summary>
    public static string GetProxyForUrl(string targetUrl, IReadOnlyDictionary<string, string>? env = null)
    {
        var parsedUrl = ParseProxyTargetUrl(targetUrl);
        if (parsedUrl is null || parsedUrl.Scheme.Length == 0 || parsedUrl.Host.Length == 0) return "";

        var protocol = parsedUrl.Scheme;
        var hostname = StripBrackets(parsedUrl.DnsSafeHost.Length > 0 ? parsedUrl.DnsSafeHost : parsedUrl.Host);
        var port = parsedUrl.Port > 0 ? parsedUrl.Port : DefaultProxyPorts.GetValueOrDefault(protocol, 0);
        if (!ShouldProxyHostname(hostname, port, env)) return "";

        var proxy = GetProxyEnv($"{protocol}_proxy", env);
        if (proxy.Length == 0) proxy = GetProxyEnv("all_proxy", env);
        if (proxy.Length > 0 && !proxy.Contains("://", StringComparison.Ordinal))
        {
            proxy = $"{protocol}://{proxy}";
        }
        return proxy;
    }

    /// <summary>
    /// 解析目标 URL 的代理地址并校验协议（仅 http/https）。对应 TS <c>resolveHttpProxyUrlForTarget</c>。
    /// 代理 URL 非法或协议不支持时抛错。
    /// </summary>
    public static Uri? ResolveHttpProxyUrlForTarget(string targetUrl,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var proxy = GetProxyForUrl(targetUrl, env);
        if (proxy.Length == 0) return null;

        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUrl))
        {
            throw new InvalidOperationException($"Invalid proxy URL {System.Text.Json.JsonSerializer.Serialize(proxy)}");
        }

        if (proxyUrl.Scheme != "http" && proxyUrl.Scheme != "https")
        {
            throw new InvalidOperationException($"{UnsupportedProxyProtocolMessage} Got {proxyUrl.Scheme}:");
        }

        return proxyUrl;
    }
}
