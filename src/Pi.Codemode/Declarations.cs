using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Pi.Codemode;

/// <summary>渲染选项。对应 TS <c>RenderDeclarationsOptions</c>（declarations.ts）。</summary>
public sealed record RenderDeclarationsOptions
{
    public IReadOnlyList<CodemodeTool>? Tools { get; init; }

    public IReadOnlyList<CodemodeTool>? Globals { get; init; }
}

/// <summary>
/// 把 JSON Schema 渲染成 TypeScript 声明。对应 TS <c>declarations.ts</c>：
/// 工具成为 <c>declare const tools</c> 的成员，全局函数成为 <c>declare function</c>，
/// <c>ns.member</c> 形式的全局函数归入 <c>declare const ns</c>。描述变文档注释，schema 变类型。
/// </summary>
public static partial class CodemodeDeclarations
{
    private const string Indent = "  ";

    /// <summary>渲染后的输入类型字符上限，超出则退化为 <c>unknown</c>。对应 TS <c>DEFAULT_INPUT_SCHEMA_MAX_CHARS</c>。</summary>
    public const int DefaultInputSchemaMaxChars = 16_000;

    /// <summary>单个 schema 允许的本地 <c>$ref</c> 展开次数，避免共享定义撑爆输出。</summary>
    private const int MaxRefExpansions = 32;

    /// <summary>
    /// MCP <c>CallToolResult</c> 的 TypeScript 类型前置声明，供 <c>CallToolResult&lt;T&gt;</c> 引用。
    /// 对应 TS <c>MCP_TYPESCRIPT_PREAMBLE</c>。
    /// </summary>
    public const string McpTypeScriptPreamble = """
        type Role = "user" | "assistant";
        type MetaObject = Record<string, unknown>;
        type Annotations = {
          audience?: Role[];
          priority?: number;
          lastModified?: string;
        };
        type Icon = {
          src: string;
          mimeType?: string;
          sizes?: string[];
          theme?: "light" | "dark";
        };
        type TextResourceContents = {
          uri: string;
          mimeType?: string;
          _meta?: MetaObject;
          text: string;
        };
        type BlobResourceContents = {
          uri: string;
          mimeType?: string;
          _meta?: MetaObject;
          blob: string;
        };
        type TextContent = {
          type: "text";
          text: string;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type ImageContent = {
          type: "image";
          data: string;
          mimeType: string;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type AudioContent = {
          type: "audio";
          data: string;
          mimeType: string;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type ResourceLink = {
          icons?: Icon[];
          name: string;
          title?: string;
          uri: string;
          description?: string;
          mimeType?: string;
          annotations?: Annotations;
          size?: number;
          _meta?: MetaObject;
          type: "resource_link";
        };
        type EmbeddedResource = {
          type: "resource";
          resource: TextResourceContents | BlobResourceContents;
          annotations?: Annotations;
          _meta?: MetaObject;
        };
        type ContentBlock =
          | TextContent
          | ImageContent
          | AudioContent
          | ResourceLink
          | EmbeddedResource;
        type CallToolResult<TStructured = { [key: string]: unknown }> = {
          _meta?: MetaObject;
          content: ContentBlock[];
          isError?: boolean;
          structuredContent?: TStructured;
          [key: string]: unknown;
        };
        """;

    private static readonly JsonSerializerOptions JsonStringifyOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>渲染脚本可见 API 的 TypeScript 声明。对应 TS <c>renderDeclarations</c>。</summary>
    public static string RenderDeclarations(RenderDeclarationsOptions options)
    {
        var sections = new List<string>();
        var tools = options.Tools ?? [];
        if (tools.Count > 0)
        {
            var members = tools.Select(tool =>
                $"{DocComment(tool.Description, Indent)}{Indent}{RenderToolSignature(tool)}");
            sections.Add($"declare const tools: {{\n{string.Join("\n", members)}\n}};");
        }

        var namespaces = new List<(string Namespace, List<string> Members)>();
        foreach (var global in options.Globals ?? [])
        {
            var dot = global.Name.IndexOf('.');
            if (dot == -1)
            {
                sections.Add(RenderGlobal($"declare function {global.Name}", global, ""));
                continue;
            }
            var ns = global.Name[..dot];
            var entry = namespaces.FirstOrDefault(candidate => candidate.Namespace == ns);
            if (entry.Members is null)
            {
                entry = (ns, []);
                namespaces.Add(entry);
            }
            entry.Members.Add(RenderGlobal(global.Name[(dot + 1)..], global, Indent));
        }
        foreach (var (ns, members) in namespaces)
        {
            sections.Add($"declare const {ns}: {{\n{string.Join("\n", members)}\n}};");
        }
        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// 单个工具作为 <c>tools</c> 对象的成员：<c>name(args: T): Promise&lt;R&gt;;</c>，
    /// 名字用脚本侧标识符。输入类型超过 <paramref name="inputMaxChars"/> 时渲染为 <c>unknown</c>。
    /// 输出 schema 是 MCP <c>CallToolResult</c> 时渲染为 <c>Promise&lt;CallToolResult&lt;T&gt;&gt;</c>。
    /// </summary>
    public static string RenderToolSignature(CodemodeTool tool, int? inputMaxChars = null)
    {
        var input = tool.InputSchema is null
            ? "unknown"
            : SchemaToType(tool.InputSchema, inputMaxChars ?? DefaultInputSchemaMaxChars);
        return $"{CodemodeIdentifiers.ToCodemodeIdentifier(tool.Name)}(args: {input}): " +
               $"Promise<{RenderToolOutputType(tool.OutputSchema)}>;";
    }

    /// <summary>
    /// 工具的样例文本：描述 + 工具声明。用于工具列表与 <c>ALL_TOOLS</c> 条目。
    /// 对应 TS <c>renderToolSample</c>。
    /// </summary>
    public static string RenderToolSample(CodemodeTool tool, int? inputMaxChars = null)
    {
        var declaration = $"declare const tools: {{ {RenderToolSignature(tool, inputMaxChars)} }};";
        return $"{(tool.Description ?? "").Trim()}\n\ncodemode tool declaration:\n```ts\n{declaration}\n```";
    }

    /// <summary>
    /// MCP <c>CallToolResult</c> 输出 schema 的 <c>structuredContent</c> 子 schema
    /// （以「<c>content</c> 是对象数组、<c>isError</c> 是布尔、<c>_meta</c> 是对象」判定）：
    /// 未声明时为 <c>true</c>，不是 <c>CallToolResult</c> 时为 null。对应 TS <c>mcpStructuredContentSchema</c>。
    /// </summary>
    public static JsonNode? McpStructuredContentSchema(JsonNode? schema)
    {
        if (schema is not JsonObject root || root["properties"] is not JsonObject properties) return null;
        if (properties["content"] is not JsonObject content || StringValue(content["type"]) != "array"
            || content["items"] is not JsonObject items || StringValue(items["type"]) != "object")
        {
            return null;
        }
        if (properties["isError"] is not JsonObject isError || StringValue(isError["type"]) != "boolean"
            || properties["_meta"] is not JsonObject meta || StringValue(meta["type"]) != "object")
        {
            return null;
        }
        var structured = properties["structuredContent"];
        return structured is JsonObject || IsBoolean(structured) ? structured : JsonValue.Create(true);
    }

    /// <summary>
    /// 工具调用的解析类型：MCP 输出 schema 渲染为 <c>CallToolResult&lt;T&gt;</c>，
    /// 否则为 schema 的类型；无 schema 时 <c>unknown</c>。对应 TS <c>renderToolOutputType</c>。
    /// </summary>
    public static string RenderToolOutputType(JsonNode? schema)
    {
        var structured = McpStructuredContentSchema(schema);
        if (structured is not null)
        {
            var type = SchemaToType(structured);
            return type == "unknown" ? "CallToolResult" : $"CallToolResult<{type}>";
        }
        return schema is null ? "unknown" : SchemaToType(schema);
    }

    private static string RenderGlobal(string head, CodemodeTool global, string indent)
    {
        if (global.Signature is not null)
        {
            return $"{DocComment(global.Description, indent)}{indent}{head}{global.Signature};";
        }
        var input = global.InputSchema is null ? "unknown" : SchemaToType(global.InputSchema);
        var output = global.OutputSchema is null ? "unknown" : SchemaToType(global.OutputSchema);
        return $"{DocComment(global.Description, indent)}{indent}{head}(args: {input}): Promise<{output}>;";
    }

    private static string DocComment(string? description, string indent)
    {
        var text = description?.Trim();
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("*/", "*\\/").Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 1) return $"{indent}/** {lines[0]} */\n";
        var body = string.Join("\n", lines.Select(line => $"{indent} *{(line.Length > 0 ? $" {line}" : "")}"));
        return $"{indent}/**\n{body}\n{indent} */\n";
    }

    private static string PropertyKey(string name)
        => IdentifierPattern().IsMatch(name) ? name : JsonSerializer.Serialize(name, JsonStringifyOptions);

    private static bool IsBoolean(JsonNode? value)
        => value is JsonValue primitive
            && primitive.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static string Union(IEnumerable<string> types)
    {
        var unique = new List<string>();
        foreach (var type in types)
        {
            if (!unique.Contains(type)) unique.Add(type);
        }
        if (unique.Contains("unknown")) return "unknown";
        return unique.Count == 0 ? "never" : string.Join(" | ", unique);
    }

    /// <summary>
    /// 把 JSON Schema 转成 TypeScript 类型表达式：对象单行渲染（属性按名排序），
    /// 属性带描述时改为每行一条并加 <c>//</c> 注释；数组渲染为 <c>Array&lt;T&gt;</c>。
    /// 本地引用（<c>#/$defs/...</c>、<c>#/definitions/...</c>）按 <paramref name="schema"/> 解析；
    /// 递归与远程引用渲染为 <c>unknown</c>；结果超过 <paramref name="maxChars"/> 时渲染为 <c>unknown</c>。
    /// 对应 TS <c>schemaToType</c>。
    /// </summary>
    public static string SchemaToType(JsonNode? schema, int? maxChars = null)
    {
        var type = ToType(schema, new SchemaContext { Root = schema, Resolving = [], Expansions = 0 });
        return maxChars is not null && type.Length > maxChars ? "unknown" : type;
    }

    private sealed class SchemaContext
    {
        public required JsonNode? Root { get; init; }

        /// <summary>当前路径上正在展开的引用，用于在递归类型处停下。</summary>
        public required HashSet<string> Resolving { get; init; }

        public int Expansions { get; set; }
    }

    private static JsonNode? ResolveRef(string reference, JsonNode? root)
    {
        if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var current = root;
        var path = reference.Length <= 2 ? "" : reference[2..];
        foreach (var rawSegment in path.Split('/').Where(segment => segment.Length > 0))
        {
            var key = Uri.UnescapeDataString(rawSegment).Replace("~1", "/").Replace("~0", "~");
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(key, out var next)) return null;
            current = next;
        }
        return current is JsonObject || IsBoolean(current) ? current : null;
    }

    private static string ToType(JsonNode? schema, SchemaContext context)
    {
        if (IsBoolean(schema))
        {
            return schema!.GetValue<bool>() ? "unknown" : "never";
        }
        if (schema is not JsonObject obj) return "unknown";

        if (StringValue(obj["$ref"]) is { } reference)
        {
            if (context.Resolving.Contains(reference) || context.Expansions >= MaxRefExpansions) return "unknown";
            var target = ResolveRef(reference, context.Root);
            if (target is null) return "unknown";
            context.Expansions++;
            context.Resolving.Add(reference);
            try
            {
                return ToType(target, context);
            }
            finally
            {
                context.Resolving.Remove(reference);
            }
        }

        if (obj.ContainsKey("const")) return Stringify(obj["const"]);
        if (obj["enum"] is JsonArray enumValues)
        {
            return Union(enumValues.Select(Stringify));
        }

        var variants = obj["anyOf"] as JsonArray ?? obj["oneOf"] as JsonArray;
        if (variants is not null)
        {
            return Union(variants.Select(variant => ToType(variant, context)));
        }
        if (obj["allOf"] is JsonArray allOf)
        {
            var parts = allOf.Select(part => ToType(part, context))
                .Where(part => part != "unknown")
                .ToList();
            return parts.Count == 0
                ? "unknown"
                : string.Join(" & ", parts.Select(part => part.Contains(" | ", StringComparison.Ordinal)
                    ? $"({part})"
                    : part));
        }

        var type = obj["type"];
        if (type is JsonArray typeArray)
        {
            return Union(typeArray.Select(entry =>
            {
                var clone = (JsonObject)obj.DeepClone();
                clone["type"] = entry?.DeepClone();
                return ToType(clone, context);
            }));
        }

        var typeName = StringValue(type);
        switch (typeName)
        {
            case "string":
                return "string";
            case "number":
            case "integer":
                return "number";
            case "boolean":
                return "boolean";
            case "null":
                return "null";
            case "array":
                return ArrayType(obj, context);
            case "object":
                return ObjectType(obj, context);
            case null:
                if (obj.ContainsKey("properties") || obj.ContainsKey("additionalProperties")
                    || obj.ContainsKey("required"))
                {
                    return ObjectType(obj, context);
                }
                if (obj.ContainsKey("items") || obj.ContainsKey("prefixItems")) return ArrayType(obj, context);
                return "unknown";
            default:
                return "unknown";
        }
    }

    private static string ArrayType(JsonObject schema, SchemaContext context)
    {
        if (schema["items"] is { } items && items is not JsonArray)
        {
            return $"Array<{ToType(items, context)}>";
        }
        var tuple = schema["prefixItems"] as JsonArray ?? schema["items"] as JsonArray;
        if (tuple is { Count: > 0 })
        {
            return $"[{string.Join(", ", tuple.Select(item => ToType(item, context)))}]";
        }
        return "unknown[]";
    }

    private static string DescriptionOf(JsonNode? property)
        => property is JsonObject obj && StringValue(obj["description"]) is { } description
            ? description.Trim()
            : "";

    private static string ObjectType(JsonObject schema, SchemaContext context)
    {
        var properties = schema["properties"] as JsonObject ?? [];
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray requiredArray)
        {
            foreach (var item in requiredArray)
            {
                if (StringValue(item) is { } name) required.Add(name);
            }
        }
        var names = properties.Select(entry => entry.Key).OrderBy(name => name, StringComparer.Ordinal).ToList();
        var members = names.Select(name =>
        {
            var optional = required.Contains(name) ? "" : "?";
            return $"{PropertyKey(name)}{optional}: {ToType(properties[name], context)};";
        }).ToList();

        // TS 用 `additionalProperties !== undefined` 区分「缺失」与「JSON null」。
        var hasAdditional = schema.ContainsKey("additionalProperties");
        var additional = schema["additionalProperties"];
        if (hasAdditional && !IsFalse(additional))
        {
            var type = IsTrue(additional) ? "unknown" : ToType(additional, context);
            members.Add($"[key: string]: {type};");
        }
        else if (!hasAdditional && names.Count == 0)
        {
            members.Add("[key: string]: unknown;");
        }

        if (members.Count == 0) return "{}";
        if (!names.Any(name => DescriptionOf(properties[name]).Length > 0))
        {
            return $"{{ {string.Join(" ", members)} }}";
        }

        var lines = new List<string> { "{" };
        for (var index = 0; index < names.Count; index++)
        {
            foreach (var line in DescriptionOf(properties[names[index]]).Split(["\r\n", "\n"], StringSplitOptions.None))
            {
                if (line.Trim().Length > 0) lines.Add($"{Indent}// {line.Trim()}");
            }
            lines.Add($"{Indent}{members[index].Replace("\n", $"\n{Indent}")}");
        }
        for (var index = names.Count; index < members.Count; index++) lines.Add($"{Indent}{members[index]}");
        lines.Add("}");
        return string.Join("\n", lines);
    }

    /// <summary>取 JSON 字符串值；非字符串（含缺省）返回 null（对齐 TS 的 typeof 检查）。</summary>
    private static string? StringValue(JsonNode? value)
        => value is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.String
            ? primitive.GetValue<string>()
            : null;

    private static bool IsTrue(JsonNode? value)
        => value is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.True;

    private static bool IsFalse(JsonNode? value)
        => value is JsonValue primitive && primitive.GetValueKind() == JsonValueKind.False;

    /// <summary>JS <c>JSON.stringify(value) ?? "unknown"</c>：缺省为 <c>unknown</c>，JSON null 为 <c>null</c>。</summary>
    private static string Stringify(JsonNode? value)
    {
        if (value is null) return "null";
        return value.ToJsonString(JsonStringifyOptions);
    }
}
