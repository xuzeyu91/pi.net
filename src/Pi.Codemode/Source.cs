namespace Pi.Codemode;

/// <summary>解析出的 <c>@options</c> 选项。对应 TS <c>CodemodeSourceOptions</c>（source.ts）。</summary>
public sealed record CodemodeSourceOptions
{
    /// <summary>脚本输出的 token 预算。</summary>
    public long? MaxOutputTokens { get; init; }

    /// <summary>整个脚本的硬截止时间（毫秒，含工具调用）。</summary>
    public long? TimeoutMs { get; init; }
}

/// <summary>解析结果。对应 TS <c>ParsedCodemodeSource</c>。</summary>
public sealed record ParsedCodemodeSource(string Code, CodemodeSourceOptions Options);

/// <summary>源码格式错误。对应 TS <c>CodemodeSourceError</c>。</summary>
public sealed class CodemodeSourceError(string message) : Exception(message);

/// <summary>
/// codemode 源码格式：JavaScript，可选地以一行 options 开头。对应 TS <c>source.ts</c>。
/// <code>
/// // @options: {"max_output_tokens": 2000, "timeout_ms": 30000}
/// const text = await tools.read({ path: "package.json" });
/// </code>
/// </summary>
public static class CodemodeSource
{
    /// <summary>options 行前缀。对应 TS <c>CODEMODE_OPTIONS_PREFIX</c>。</summary>
    public const string OptionsPrefix = "// @options:";

    private static readonly IReadOnlyList<string> SupportedFields = ["max_output_tokens", "timeout_ms"];
    private const string SupportedFieldsText = "`max_output_tokens` and `timeout_ms`";

    /// <summary><c>setTimeout</c> 支持的最大延迟，用于界定 <c>timeout_ms</c>。</summary>
    private const long MaxTimeoutMs = 2_147_483_647;

    /// <summary>
    /// 供语法约束型 provider 使用的 Lark 语法：只固定 options 行的形状，
    /// options JSON 与代码由 <see cref="Parse"/> 检查。对应 TS <c>CODEMODE_SOURCE_GRAMMAR</c>。
    /// </summary>
    public const string Grammar = """
        start: options_source | plain_source
        options_source: OPTIONS_LINE NEWLINE SOURCE
        plain_source: SOURCE

        OPTIONS_LINE: /[ \t]*\/\/ @options:[^\r\n]*/
        NEWLINE: /\r?\n/
        SOURCE: /[\s\S]+/
        """;

    /// <summary>
    /// 拆分可选的首页 <c>// @options: {...}</c> 与脚本。空输入或非法选项抛
    /// <see cref="CodemodeSourceError"/>。对应 TS <c>parseCodemodeSource</c>。
    /// </summary>
    public static ParsedCodemodeSource Parse(string input)
    {
        if (input.Trim().Length == 0)
        {
            throw new CodemodeSourceError(
                "Expected JavaScript source text (non-empty). Provide JS only, optionally with a first line " +
                "`// @options: {\"max_output_tokens\": 1000}`.");
        }

        var newline = input.IndexOf('\n');
        var firstLine = (newline == -1 ? input : input[..newline]).TrimEnd('\r');
        var trimmed = firstLine.TrimStart();
        if (!trimmed.StartsWith(OptionsPrefix, StringComparison.Ordinal)) return new ParsedCodemodeSource(input, new());

        var code = newline == -1 ? "" : input[newline..];
        if (code.Trim().Length == 0)
        {
            throw new CodemodeSourceError(
                "The @options line must be followed by JavaScript source on subsequent lines");
        }
        return new ParsedCodemodeSource(code, ParseOptions(trimmed[OptionsPrefix.Length..].Trim()));
    }

    private static bool IsSafeInteger(long? value) => value is >= 0;

    private static CodemodeSourceOptions ParseOptions(string directive)
    {
        if (directive.Length == 0)
        {
            throw new CodemodeSourceError(
                $"@options must be a JSON object with supported fields {SupportedFieldsText}");
        }

        System.Text.Json.Nodes.JsonNode? value;
        try
        {
            value = System.Text.Json.Nodes.JsonNode.Parse(directive);
        }
        catch (Exception error)
        {
            throw new CodemodeSourceError(
                $"@options must be valid JSON with supported fields {SupportedFieldsText}: {error.Message}");
        }
        if (value is not System.Text.Json.Nodes.JsonObject fields)
        {
            throw new CodemodeSourceError(
                $"@options must be a JSON object with supported fields {SupportedFieldsText}");
        }

        foreach (var (key, _) in fields)
        {
            if (!SupportedFields.Contains(key))
            {
                throw new CodemodeSourceError($"@options only supports {SupportedFieldsText}; got `{key}`");
            }
        }

        long? maxOutputTokens = null;
        if (fields.TryGetPropertyValue("max_output_tokens", out var rawMaxOutputTokens)
            && rawMaxOutputTokens is not null)
        {
            maxOutputTokens = AsSafeInteger(rawMaxOutputTokens);
            if (!IsSafeInteger(maxOutputTokens))
            {
                throw new CodemodeSourceError(
                    "@options field `max_output_tokens` must be a non-negative safe integer");
            }
        }

        long? timeoutMs = null;
        if (fields.TryGetPropertyValue("timeout_ms", out var rawTimeoutMs) && rawTimeoutMs is not null)
        {
            timeoutMs = AsSafeInteger(rawTimeoutMs);
            if (timeoutMs is null or 0 || timeoutMs > MaxTimeoutMs)
            {
                throw new CodemodeSourceError(
                    $"@options field `timeout_ms` must be a positive integer up to {MaxTimeoutMs}");
            }
        }

        return new CodemodeSourceOptions { MaxOutputTokens = maxOutputTokens, TimeoutMs = timeoutMs };
    }

    private static long? AsSafeInteger(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is not System.Text.Json.Nodes.JsonValue primitive) return null;
        if (primitive.TryGetValue<long>(out var value)) return value;
        if (primitive.TryGetValue<double>(out var number) && number == Math.Floor(number)
            && number >= long.MinValue && number <= long.MaxValue)
        {
            return (long)number;
        }
        return null;
    }
}
