using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;

namespace Pi.CodingAgent.Core;

/// <summary>
/// MCP server configuration and the servers extensions register with <c>pi.registerMcpServer()</c>.
/// Port of the TS <c>core/mcp-servers.ts</c>.
/// </summary>
/// <remarks>
/// The core only validates and stores registrations. The MCP extension (built in, or another extension
/// that handles <c>mcp_servers_change</c>) connects them next to the servers from <c>mcp.json</c>.
/// </remarks>
public static partial class McpServers
{
    /// <summary>How a server's tools reach the model.</summary>
    public static class Exposures
    {
        /// <summary>Tools are callable from codemode scripts but neither declared to the model nor listed.</summary>
        public const string Codemode = "codemode";

        /// <summary>Not declared to the model until the <c>tool_search</c> tool loads them.</summary>
        public const string Deferred = "deferred";

        /// <summary>Tools are declared to the model like any other tool.</summary>
        public const string Direct = "direct";

        /// <summary>Tools are registered but unreachable.</summary>
        public const string Hidden = "hidden";
    }

    /// <summary>Every accepted exposure, in the order error messages list them.</summary>
    public static readonly IReadOnlyList<string> AllExposures =
        [Exposures.Codemode, Exposures.Deferred, Exposures.Direct, Exposures.Hidden];

    /// <summary>Names of the tools the MCP extension registers for a server's resources.</summary>
    public static class ToolNames
    {
        /// <summary>Lists a server's resources.</summary>
        public const string ListMcpResources = "list_mcp_resources";

        /// <summary>Lists a server's resource templates.</summary>
        public const string ListMcpResourceTemplates = "list_mcp_resource_templates";

        /// <summary>Reads one resource of a server.</summary>
        public const string ReadMcpResource = "read_mcp_resource";
    }

    /// <summary>Older exposure names, accepted in configs and replaced by their current name when validated.</summary>
    private static readonly Dictionary<string, string> ExposureAliases = new(StringComparer.Ordinal)
    {
        ["codemode-deferred"] = Exposures.Codemode,
    };

    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>Names of servers whose tools a request may reach.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex ServerNamePattern();

    /// <summary>Namespace of a server's tools: <c>mcp__&lt;server&gt;</c> with <c>-</c> replaced by <c>_</c>.</summary>
    public static string Namespace(string server) => $"mcp__{server.Replace('-', '_')}";

    /// <summary>Whether a redirect URI can be served by pi's loopback callback server.</summary>
    public static bool IsLoopbackRedirectUri(string value)
    {
        if (JsUrl.TryParse(value) is not { } url) return false;
        return url.Protocol == "http:"
            && LoopbackHosts.Contains(Hostname(url))
            && url.Query.Length == 0
            && url.Hash.Length == 0;
    }

    /// <summary>JS <c>url.hostname</c> keeps the brackets of an IPv6 literal; <see cref="JsUrlValue"/> does not.</summary>
    private static string Hostname(JsUrlValue url) => url.IsIpv6 ? $"[{url.Hostname}]" : url.Hostname;

    /// <summary>Exposure of one tool of a server: its <c>toolExposure</c> entry, else the server's <c>exposure</c>.</summary>
    public static string GetMcpToolExposure(McpServerConfig config, string toolName)
    {
        var overrides = config.ToolExposure;
        if (overrides is null) return config.Exposure ?? Exposures.Codemode;

        if (overrides.TryGetPropertyValue(toolName, out var exact)
            && exact is JsonValue value
            && value.TryGetValue<string>(out var exactExposure))
        {
            return exactExposure;
        }

        foreach (var (pattern, entry) in overrides)
        {
            if (!pattern.Contains('*')) continue;
            if (entry is JsonValue patternValue
                && patternValue.TryGetValue<string>(out var exposure)
                && ToolPatternRegExp(pattern).IsMatch(toolName))
            {
                return exposure;
            }
        }

        return config.Exposure ?? Exposures.Codemode;
    }

    /// <summary>Glob where <c>*</c> matches any characters, anchored at both ends.</summary>
    private static Regex ToolPatternRegExp(string pattern)
    {
        var source = string.Join(".*", pattern.Split('*').Select(Regex.Escape));
        return new Regex($"^{source}$", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Validate one server entry of the <c>mcpServers</c> shape. Returns the config with exposure aliases
    /// resolved, or an error message.
    /// </summary>
    public static McpServerValidation ValidateMcpServerConfig(string name, JsonNode? raw)
    {
        if (!ServerNamePattern().IsMatch(name))
        {
            return McpServerValidation.Failed($"invalid server name \"{name}\" (use letters, digits, \"_\" and \"-\")");
        }

        if (raw is not JsonObject value)
        {
            return McpServerValidation.Failed($"server \"{name}\" must be an object");
        }

        value = ResolveExposureAliases(value);
        var type = ReadString(value, "type");
        var exposure = ReadString(value, "exposure");
        var timeout = value["timeout"];
        var toolExposure = value["toolExposure"];
        var description = value["description"];
        var exposures = string.Join(", ", AllExposures.Select(entry => $"\"{entry}\""));

        if (value.ContainsKey("exposure") && !IsExposure(exposure))
        {
            return McpServerValidation.Failed($"server \"{name}\": exposure must be one of {exposures}");
        }

        if (value.ContainsKey("toolExposure"))
        {
            if (toolExposure is not JsonObject overrides)
            {
                return McpServerValidation.Failed(
                    $"server \"{name}\": toolExposure must map tool names to exposures");
            }

            foreach (var (tool, entry) in overrides)
            {
                if (!IsExposure(ReadStringValue(entry)))
                {
                    return McpServerValidation.Failed(
                        $"server \"{name}\": toolExposure \"{tool}\" must be one of {exposures}");
                }
            }
        }

        if (value.ContainsKey("enabled") && !IsBoolean(value["enabled"]))
        {
            return McpServerValidation.Failed($"server \"{name}\": enabled must be a boolean");
        }

        if (value.ContainsKey("description") && !IsString(description))
        {
            return McpServerValidation.Failed($"server \"{name}\": description must be a string");
        }

        if (value.ContainsKey("timeout") && !IsPositiveNumber(timeout))
        {
            return McpServerValidation.Failed($"server \"{name}\": timeout must be a positive number of seconds");
        }

        if (type == "sse")
        {
            return McpServerValidation.Failed(
                $"server \"{name}\": legacy SSE transport is not supported; use the streamable HTTP URL");
        }

        var url = ReadString(value, "url");
        if (url is not null && (type is null || type == "http" || type == "streamable-http"))
        {
            var parsed = JsUrl.TryParse(url);
            if (parsed is null || (parsed.Protocol != "http:" && parsed.Protocol != "https:"))
            {
                return McpServerValidation.Failed($"server \"{name}\": url must be an http or https URL");
            }

            if (value.ContainsKey("headers") && !IsStringRecord(value["headers"]))
            {
                return McpServerValidation.Failed($"server \"{name}\": headers must map names to strings");
            }

            if (ValidateOAuth(value["oauth"]) is { } oauthError)
            {
                return McpServerValidation.Failed($"server \"{name}\": {oauthError}");
            }

            if (value.ContainsKey("auth"))
            {
                var provider = value["auth"] is JsonObject auth ? ReadString(auth, "provider") : null;
                if (provider is null || provider.Length == 0)
                {
                    return McpServerValidation.Failed(
                        $"server \"{name}\": auth.provider must be a provider name");
                }

                if (parsed.Protocol != "https:" && !LoopbackHosts.Contains(Hostname(parsed)))
                {
                    return McpServerValidation.Failed(
                        $"server \"{name}\": auth requires an https URL, or http on localhost, 127.0.0.1, or [::1]");
                }
            }

            return McpServerValidation.Succeeded(new McpServerConfig(value));
        }

        var command = ReadString(value, "command");
        if (command is not null && (type is null || type == "stdio"))
        {
            if (value.ContainsKey("args") && !IsStringArray(value["args"]))
            {
                return McpServerValidation.Failed($"server \"{name}\": args must be an array of strings");
            }

            if (value.ContainsKey("env") && !IsStringRecord(value["env"]))
            {
                return McpServerValidation.Failed($"server \"{name}\": env must map names to strings");
            }

            if (value.ContainsKey("cwd") && !IsString(value["cwd"]))
            {
                return McpServerValidation.Failed($"server \"{name}\": cwd must be a string");
            }

            return McpServerValidation.Succeeded(new McpServerConfig(value));
        }

        return McpServerValidation.Failed(
            $"server \"{name}\" needs either \"command\" (stdio) or \"url\" (streamable HTTP)");
    }

    private static string? ValidateOAuth(JsonNode? value)
    {
        if (value is null) return null;
        if (value is not JsonObject oauth) return "oauth must be an object";

        var clientId = ReadString(oauth, "clientId");
        if (oauth.ContainsKey("clientId") && clientId is null) return "oauth.clientId must be a string";
        if (oauth.ContainsKey("clientSecret") && ReadString(oauth, "clientSecret") is null)
        {
            return "oauth.clientSecret must be a string";
        }

        var port = ReadNumber(oauth, "callbackPort");
        if (oauth.ContainsKey("callbackPort")
            && (port is null || Math.Floor(port.Value) != port.Value || port < 1 || port > 65535))
        {
            return "oauth.callbackPort must be a port number";
        }

        var callbackUrl = ReadString(oauth, "callbackUrl");
        if (oauth.ContainsKey("callbackUrl"))
        {
            if (callbackUrl is null || !IsLoopbackRedirectUri(callbackUrl))
            {
                return "oauth.callbackUrl must be an http URI on localhost, 127.0.0.1, or [::1] without query or fragment";
            }

            var urlPort = JsUrl.TryParse(callbackUrl)?.Port;
            if (!string.IsNullOrEmpty(urlPort) && port is not null
                && double.Parse(urlPort, System.Globalization.CultureInfo.InvariantCulture) != port)
            {
                return "oauth.callbackUrl and oauth.callbackPort name different ports";
            }
        }

        if (oauth.ContainsKey("scope") && ReadString(oauth, "scope") is null) return "oauth.scope must be a string";

        var clientName = ReadString(oauth, "clientName");
        if (oauth.ContainsKey("clientName") && (clientName is null || clientName.Trim().Length == 0))
        {
            return "oauth.clientName must be a non-empty string";
        }

        var clientRegistration = ReadString(oauth, "clientRegistration");
        if (oauth.ContainsKey("clientRegistration") && clientRegistration != "dcr")
        {
            if (clientRegistration != "cimd") return "oauth.clientRegistration must be \"dcr\" or \"cimd\"";
            if (oauth.ContainsKey("clientId") || oauth.ContainsKey("clientName"))
            {
                return "oauth.clientRegistration \"cimd\" cannot be combined with oauth.clientId or oauth.clientName";
            }

            var callback = callbackUrl is not null ? JsUrl.TryParse(callbackUrl) : null;
            if (callback is not null && (Hostname(callback) == "[::1]" || callback.Pathname != "/callback"))
            {
                return "oauth.clientRegistration \"cimd\" requires oauth.callbackUrl on localhost or 127.0.0.1 with path /callback";
            }
        }

        var metadataUrl = ReadString(oauth, "authServerMetadataUrl");
        if (oauth.ContainsKey("authServerMetadataUrl"))
        {
            var parsed = metadataUrl is not null ? JsUrl.TryParse(metadataUrl) : null;
            if (parsed is null
                || (parsed.Protocol != "https:"
                    && !(parsed.Protocol == "http:" && LoopbackHosts.Contains(Hostname(parsed)))))
            {
                return "oauth.authServerMetadataUrl must be an https URL, or http on localhost, 127.0.0.1, or [::1]";
            }
        }

        return null;
    }

    private static bool IsExposure(string? value) => value is not null && AllExposures.Contains(value);

    /// <summary>A copy of the server entry with exposure aliases replaced by their current names.</summary>
    private static JsonObject ResolveExposureAliases(JsonObject value)
    {
        var resolved = (JsonObject)value.DeepClone();
        if (resolved["exposure"] is JsonValue exposure
            && exposure.TryGetValue<string>(out var exposureName)
            && ExposureAliases.TryGetValue(exposureName, out var alias))
        {
            resolved["exposure"] = alias;
        }

        if (resolved["toolExposure"] is JsonObject toolExposure)
        {
            var replaced = new JsonObject();
            foreach (var (tool, entry) in toolExposure)
            {
                var name = ReadStringValue(entry);
                replaced[tool] = name is not null && ExposureAliases.TryGetValue(name, out var toolAlias)
                    ? toolAlias
                    : entry?.DeepClone();
            }
            resolved["toolExposure"] = replaced;
        }

        return resolved;
    }

    private static bool IsString(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<string>(out _);

    private static bool IsBoolean(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<bool>(out _);

    private static bool IsPositiveNumber(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<double>(out var number) && number > 0;

    private static bool IsStringArray(JsonNode? value)
        => value is JsonArray array && array.All(IsString);

    private static bool IsStringRecord(JsonNode? value)
        => value is JsonObject map && map.All(entry => IsString(entry.Value));

    private static string? ReadString(JsonObject owner, string key) => ReadStringValue(owner[key]);

    private static string? ReadStringValue(JsonNode? value)
        => value is JsonValue candidate && candidate.TryGetValue<string>(out var text) ? text : null;

    private static double? ReadNumber(JsonObject owner, string key)
        => owner[key] is JsonValue candidate && candidate.TryGetValue<double>(out var number) ? number : null;
}

/// <summary>
/// One validated MCP server entry. The TS config is a structural JSON object, so this wraps the
/// aliases-resolved JSON and projects the fields the core reads (difference C41) — no configured key is
/// dropped on the way through validation.
/// </summary>
public sealed class McpServerConfig(JsonObject value)
{
    /// <summary>The aliases-resolved config as configured.</summary>
    public JsonObject Value { get; } = value;

    public string? Type => Read("type");

    /// <summary>Defaults to <c>codemode</c>.</summary>
    public string? Exposure => Read("exposure");

    public string? Description => Read("description");

    public bool? Enabled => ReadBool("enabled");

    /// <summary>Per-request timeout in seconds. Default: 60.</summary>
    public double? Timeout => Value["timeout"] is JsonValue value && value.TryGetValue<double>(out var number)
        ? number
        : null;

    public JsonObject? ToolExposure => Value["toolExposure"] as JsonObject;

    public string? Command => Read("command");

    public IReadOnlyList<string>? Args => Value["args"] is JsonArray array
        ? array.OfType<JsonValue>().Select(entry => entry.GetValue<string>()).ToList()
        : null;

    public IReadOnlyDictionary<string, string>? Env => ReadStringMap("env");

    /// <summary>Relative paths resolve against the session working directory.</summary>
    public string? Cwd => Read("cwd");

    public string? Url => Read("url");

    public IReadOnlyDictionary<string, string>? Headers => ReadStringMap("headers");

    public JsonObject? OAuth => Value["oauth"] as JsonObject;

    /// <summary>Provider whose token is sent instead of using OAuth.</summary>
    public string? AuthProvider => (Value["auth"] as JsonObject)?["provider"] is JsonValue value
        && value.TryGetValue<string>(out var text)
        ? text
        : null;

    /// <summary>Whether the entry is a stdio server.</summary>
    public bool IsStdio => Command is not null;

    /// <summary>Whether the entry is a streamable-HTTP server.</summary>
    public bool IsHttp => Url is not null;

    private string? Read(string key)
        => Value[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private bool? ReadBool(string key)
        => Value[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private IReadOnlyDictionary<string, string>? ReadStringMap(string key)
    {
        if (Value[key] is not JsonObject map) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, entry) in map)
        {
            if (entry is JsonValue value && value.TryGetValue<string>(out var text)) result[name] = text;
        }
        return result;
    }
}

/// <summary>Result of <see cref="McpServers.ValidateMcpServerConfig"/> (TS returns <c>config | string</c>).</summary>
public sealed record McpServerValidation
{
    public McpServerConfig? Config { get; init; }

    public string? Error { get; init; }

    public bool Ok => Error is null;

    internal static McpServerValidation Succeeded(McpServerConfig config) => new() { Config = config };

    internal static McpServerValidation Failed(string error) => new() { Error = error };
}

/// <summary>A server an extension registered with <c>pi.registerMcpServer()</c>.</summary>
public sealed record RegisteredMcpServer(string Name, McpServerConfig Config, string ExtensionPath);

/// <summary>Servers registered by the extensions of one runtime.</summary>
public sealed class McpServerRegistry
{
    private readonly Dictionary<string, RegisteredMcpServer> _servers = new(StringComparer.Ordinal);
    private Action? _changeListener;

    /// <summary>Register or replace a server. The caller checks ownership.</summary>
    public void Register(RegisteredMcpServer server)
    {
        _servers[server.Name] = server;
        _changeListener?.Invoke();
    }

    /// <summary>Remove a server registered by <paramref name="extensionPath"/>. Servers of others are left alone.</summary>
    public void Unregister(string name, string extensionPath)
    {
        if (_servers.GetValueOrDefault(name)?.ExtensionPath != extensionPath) return;
        _servers.Remove(name);
        _changeListener?.Invoke();
    }

    public RegisteredMcpServer? Get(string name) => _servers.GetValueOrDefault(name);

    /// <summary>Copies of the registered servers, in registration order.</summary>
    public IReadOnlyList<RegisteredMcpServer> List() => _servers.Values
        .Select(server => server with { Config = new McpServerConfig((JsonObject)server.Config.Value.DeepClone()) })
        .ToList();

    /// <summary>Called after every change. The runner sets it when it binds, to emit <c>mcp_servers_change</c>.</summary>
    public void SetChangeListener(Action? listener) => _changeListener = listener;
}
