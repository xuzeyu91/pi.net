using System.Globalization;

namespace Pi.CodingAgent.Core;

/// <summary>One entry of the idle-timeout choice list.</summary>
public sealed record HttpIdleTimeoutChoice(string Label, long TimeoutMs);

/// <summary>
/// Port of the provider-neutral parts of <c>core/http-dispatcher.ts</c>.
/// </summary>
/// <remarks>
/// The TS module installs an Undici <c>EnvHttpProxyAgent</c> as the global dispatcher and swaps
/// <c>globalThis.fetch</c>. .NET has neither: <see cref="System.Net.Http.HttpClient"/> owns connection
/// pooling, proxy resolution and timeouts, and the coding agent configures them per client
/// (<c>Utils/ManagementHttp.cs</c> and the Pi.Ai transport layer). So this port keeps the timeout
/// parsing/formatting contract and the proxy environment seeding, and drops the dispatcher installation
/// as a documented divergence (see <c>docs/coding-agent-porting-status.md</c>, difference C32).
/// </remarks>
public static class HttpDispatcher
{
    public const long DefaultHttpIdleTimeoutMs = 300_000;

    // Node's 250ms default can terminate valid connection attempts on high-latency routes.
    private const long DefaultAutoSelectFamilyAttemptTimeoutMs = 2_000;

    public static readonly IReadOnlyList<HttpIdleTimeoutChoice> HttpIdleTimeoutChoices =
    [
        new("30 sec", 30_000),
        new("1 min", 60_000),
        new("2 min", 120_000),
        new("5 min", 300_000),
        new("disabled", 0),
    ];

    /// <summary>
    /// Parse a configured idle timeout. Accepts <c>"disabled"</c> (0), a numeric string, or a
    /// non-negative finite number; anything else is <see langword="null"/>. Port of
    /// <c>parseHttpIdleTimeoutMs</c>.
    /// </summary>
    public static long? ParseHttpIdleTimeoutMs(object? value)
    {
        if (value is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.ToLowerInvariant() == "disabled") return 0;
            if (trimmed.Length == 0) return null;
            return ParseHttpIdleTimeoutMs(JsNumber.Parse(trimmed));
        }

        if (!JsNumber.TryGetFiniteNonNegative(value, out var number)) return null;
        return (long)Math.Floor(number);
    }

    /// <summary>Format an idle timeout with its choice label, or <c>"&lt;n&gt; sec"</c>. Port of <c>formatHttpIdleTimeoutMs</c>.</summary>
    public static string FormatHttpIdleTimeoutMs(long timeoutMs)
    {
        foreach (var choice in HttpIdleTimeoutChoices)
        {
            if (choice.TimeoutMs == timeoutMs) return choice.Label;
        }
        return $"{timeoutMs / 1000.0} sec";
    }

    /// <summary>
    /// Seed <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c> from the configured proxy, leaving existing values
    /// alone. Port of <c>applyHttpProxySettings</c>.
    /// </summary>
    public static void ApplyHttpProxySettings(string? httpProxy)
    {
        var proxy = httpProxy?.Trim();
        if (string.IsNullOrEmpty(proxy)) return;
        SetIfUnset("HTTP_PROXY", proxy);
        SetIfUnset("HTTPS_PROXY", proxy);
    }

    /// <summary>The auto-select-family attempt timeout constant (exposed for tests; TS keeps it private).</summary>
    public static long AutoSelectFamilyAttemptTimeoutMs => DefaultAutoSelectFamilyAttemptTimeoutMs;

    private static void SetIfUnset(string name, string value)
    {
        // Node's `process.env` lookup is case-insensitive on Windows, so `http_proxy` counts as `HTTP_PROXY`.
        if (LookupEnv(name) is not null) return;
        Environment.SetEnvironmentVariable(name, value);
    }

    private static string? LookupEnv(string name)
    {
        var direct = Environment.GetEnvironmentVariable(name);
        if (direct is not null) return direct;
        return Environment.GetEnvironmentVariable(name.ToLowerInvariant());
    }
}

/// <summary>
/// The subset of JS <c>Number()</c> string coercion this port needs. Kept local to the coding agent
/// because the only consumer is the settings parser (TS reaches it through <c>Number(trimmed)</c>).
/// </summary>
internal static class JsNumber
{
    /// <summary>Parse a string the way JS <c>Number(text)</c> does, returning NaN on failure.</summary>
    public static double Parse(string text)
    {
        var trimmed = JsTrim(text);
        if (trimmed.Length == 0) return 0;
        var sign = 1.0;
        var body = trimmed;
        if (body[0] is '+' or '-')
        {
            if (body[0] == '-') sign = -1;
            body = body[1..];
        }
        if (body == "Infinity") return sign * double.PositiveInfinity;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return ParseRadix(body[2..], 16, sign);
        if (body.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) return ParseRadix(body[2..], 2, sign);
        if (body.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) return ParseRadix(body[2..], 8, sign);
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }

    /// <summary>Whether the value is a finite, non-negative JS number.</summary>
    public static bool TryGetFiniteNonNegative(object? value, out double number)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            default:
                number = double.NaN;
                return false;
        }

        return double.IsFinite(number) && number >= 0;
    }

    private static double ParseRadix(string digits, int radix, double sign)
    {
        if (digits.Length == 0) return double.NaN;
        double result = 0;
        foreach (var character in digits)
        {
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix) return double.NaN;
            result = result * radix + digit;
        }
        return sign * result;
    }

    // JS `Number()` strips WhiteSpace and LineTerminator, a superset of char.IsWhiteSpace for \uFEFF.
    private static string JsTrim(string text) => text.Trim().Trim('\uFEFF');
}
