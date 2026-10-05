using System.Text.Json.Nodes;
using Pi.Codemode;
using Pi.Codemode.Runtime;
using Xunit;

namespace Pi.Codemode.Tests;

/// <summary>identifier.ts 测试（工具名 → JavaScript 标识符）。</summary>
public class IdentifierTests
{
    [Fact]
    public void KeepsValidIdentifiersAndReplacesTheRest()
    {
        Assert.Equal("mcp__docs__search", CodemodeIdentifiers.ToCodemodeIdentifier("mcp__docs__search"));
        Assert.Equal("my_tool", CodemodeIdentifiers.ToCodemodeIdentifier("my-tool"));
        Assert.Equal("_abc", CodemodeIdentifiers.ToCodemodeIdentifier("1abc"));
        Assert.Equal("_", CodemodeIdentifiers.ToCodemodeIdentifier(""));
        Assert.Equal("$x", CodemodeIdentifiers.ToCodemodeIdentifier("$x"));
        Assert.Equal("a_b_c", CodemodeIdentifiers.ToCodemodeIdentifier("a.b.c"));
    }
}

/// <summary>source.ts 测试（@options 行解析）。</summary>
public class SourceTests
{
    [Fact]
    public void ParsesPlainSourceAndOptionsLine()
    {
        var plain = CodemodeSource.Parse("const x = 1;");
        Assert.Equal("const x = 1;", plain.Code);
        Assert.Null(plain.Options.MaxOutputTokens);
        Assert.Null(plain.Options.TimeoutMs);

        var withOptions = CodemodeSource.Parse(
            "// @options: {\"max_output_tokens\": 2000, \"timeout_ms\": 30000}\nconst x = 1;");
        Assert.Equal(2000, withOptions.Options.MaxOutputTokens);
        Assert.Equal(30000, withOptions.Options.TimeoutMs);
        // 选项行被替换为空行，行号不变。
        Assert.StartsWith("\nconst x = 1;", withOptions.Code);
    }

    [Fact]
    public void RejectsEmptyInputAndOptionsWithoutCode()
    {
        Assert.Throws<CodemodeSourceError>(() => CodemodeSource.Parse("   "));
        var error = Assert.Throws<CodemodeSourceError>(() => CodemodeSource.Parse("// @options: {}"));
        Assert.Contains("must be followed by JavaScript source", error.Message);
    }

    [Fact]
    public void RejectsUnsupportedFieldsAndInvalidValues()
    {
        Assert.Contains("only supports", Assert.Throws<CodemodeSourceError>(() =>
            CodemodeSource.Parse("// @options: {\"other\": 1}\ncode")).Message);
        Assert.Contains("valid JSON", Assert.Throws<CodemodeSourceError>(() =>
            CodemodeSource.Parse("// @options: {oops\ncode")).Message);
        Assert.Contains("must be a JSON object", Assert.Throws<CodemodeSourceError>(() =>
            CodemodeSource.Parse("// @options: [1]\ncode")).Message);
        Assert.Contains("non-negative safe integer", Assert.Throws<CodemodeSourceError>(() =>
            CodemodeSource.Parse("// @options: {\"max_output_tokens\": -1}\ncode")).Message);
        Assert.Contains("positive integer", Assert.Throws<CodemodeSourceError>(() =>
            CodemodeSource.Parse("// @options: {\"timeout_ms\": 0}\ncode")).Message);
        Assert.Contains("positive integer", Assert.Throws<CodemodeSourceError>(() =>
            CodemodeSource.Parse("// @options: {\"timeout_ms\": 2147483648}\ncode")).Message);
    }

    [Fact]
    public void OptionsLineMustBeOnTheFirstLine()
    {
        var parsed = CodemodeSource.Parse("const x = 1;\n// @options: {}\n");
        Assert.Contains("@options", parsed.Code);
        Assert.Null(parsed.Options.TimeoutMs);
    }
}

/// <summary>declarations.ts 测试（JSON Schema → TypeScript 类型/声明）。</summary>
public class DeclarationTests
{
    private static JsonNode Schema(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void RendersPrimitiveAndContainerTypes()
    {
        Assert.Equal("string", CodemodeDeclarations.SchemaToType(Schema("""{"type":"string"}""")));
        Assert.Equal("number", CodemodeDeclarations.SchemaToType(Schema("""{"type":"integer"}""")));
        Assert.Equal("boolean", CodemodeDeclarations.SchemaToType(Schema("""{"type":"boolean"}""")));
        Assert.Equal("null", CodemodeDeclarations.SchemaToType(Schema("""{"type":"null"}""")));
        Assert.Equal("unknown", CodemodeDeclarations.SchemaToType(Schema("true")));
        Assert.Equal("never", CodemodeDeclarations.SchemaToType(Schema("false")));
        Assert.Equal("unknown", CodemodeDeclarations.SchemaToType(null));
        Assert.Equal("Array<string>", CodemodeDeclarations.SchemaToType(Schema("""{"type":"array","items":{"type":"string"}}""")));
        Assert.Equal("unknown[]", CodemodeDeclarations.SchemaToType(Schema("""{"type":"array"}""")));
        Assert.Equal("[string, number]",
            CodemodeDeclarations.SchemaToType(Schema("""{"type":"array","prefixItems":[{"type":"string"},{"type":"number"}]}""")));
    }

    [Fact]
    public void RendersObjectsWithSortedOptionalProperties()
    {
        var type = CodemodeDeclarations.SchemaToType(Schema("""
        {
          "type": "object",
          "properties": { "b": { "type": "number" }, "a": { "type": "string" } },
          "required": ["b"]
        }
        """));
        Assert.Equal("{ a?: string; b: number; }", type);
        Assert.Equal("{ [key: string]: unknown; }",
            CodemodeDeclarations.SchemaToType(Schema("""{"type":"object","properties":{}}""")));
        // 无 properties 且无 additionalProperties → 开放对象。
        Assert.Equal("{ [key: string]: unknown; }",
            CodemodeDeclarations.SchemaToType(Schema("""{"type":"object"}""")));
        Assert.Equal("{ [key: string]: string; }",
            CodemodeDeclarations.SchemaToType(Schema("""{"type":"object","additionalProperties":{"type":"string"}}""")));
        // additionalProperties: false 不生成索引签名。
        Assert.Equal("{}", CodemodeDeclarations.SchemaToType(
            Schema("""{"type":"object","properties":{},"additionalProperties":false}""")));
    }

    [Fact]
    public void RendersDescriptionsAsLineComments()
    {
        var type = CodemodeDeclarations.SchemaToType(Schema("""
        {
          "type": "object",
          "properties": { "a": { "type": "string", "description": "first line\nsecond line" } }
        }
        """));
        Assert.Equal("{\n  // first line\n  // second line\n  a?: string;\n}", type);
    }

    [Fact]
    public void RendersConstEnumAndComposites()
    {
        Assert.Equal("\"x\"", CodemodeDeclarations.SchemaToType(Schema("""{"const":"x"}""")));
        Assert.Equal("1 | 2", CodemodeDeclarations.SchemaToType(Schema("""{"enum":[1,2]}""")));
        Assert.Equal("string | number",
            CodemodeDeclarations.SchemaToType(Schema("""{"anyOf":[{"type":"string"},{"type":"number"}]}""")));
        // 含 unknown 的联合退化为 unknown。
        Assert.Equal("unknown", CodemodeDeclarations.SchemaToType(Schema("""{"anyOf":[{"type":"string"},{}]}""")));
        Assert.Equal("string & number",
            CodemodeDeclarations.SchemaToType(Schema("""{"allOf":[{"type":"string"},{"type":"number"}]}""")));
        // 联合类型成员在 allOf 中加括号。
        Assert.Equal("(string | number) & boolean", CodemodeDeclarations.SchemaToType(
            Schema("""{"allOf":[{"anyOf":[{"type":"string"},{"type":"number"}]},{"type":"boolean"}]}""")));
        // type 数组 → 联合。
        Assert.Equal("string | null",
            CodemodeDeclarations.SchemaToType(Schema("""{"type":["string","null"]}""")));
    }

    [Fact]
    public void ResolvesLocalRefsAndStopsAtRecursion()
    {
        var withDefs = Schema("""
        {
          "$defs": { "name": { "type": "string" } },
          "type": "object",
          "properties": { "a": { "$ref": "#/$defs/name" } }
        }
        """);
        Assert.Equal("{ a?: string; }", CodemodeDeclarations.SchemaToType(withDefs));

        var recursive = Schema("""
        {
          "$defs": { "node": { "type": "object", "properties": { "next": { "$ref": "#/$defs/node" } } } },
          "$ref": "#/$defs/node"
        }
        """);
        Assert.Equal("{ next?: unknown; }", CodemodeDeclarations.SchemaToType(recursive));

        // 远程引用 → unknown。
        Assert.Equal("unknown", CodemodeDeclarations.SchemaToType(Schema("""{"$ref":"https://example.com/s.json"}""")));
    }

    [Fact]
    public void MaxCharsFallsBackToUnknown()
    {
        var schema = Schema("""{"type":"object","properties":{"a":{"type":"string"}}}""");
        Assert.Equal("{ a?: string; }", CodemodeDeclarations.SchemaToType(schema, 100));
        Assert.Equal("unknown", CodemodeDeclarations.SchemaToType(schema, 3));
    }

    [Fact]
    public void DetectsMcpCallToolResultStructuredContent()
    {
        var schema = Schema("""
        {
          "type": "object",
          "properties": {
            "content": { "type": "array", "items": { "type": "object" } },
            "isError": { "type": "boolean" },
            "_meta": { "type": "object" }
          }
        }
        """);
        // 未声明 structuredContent → true。
        Assert.True(CodemodeDeclarations.McpStructuredContentSchema(schema)!.GetValue<bool>());
        Assert.Equal("CallToolResult", CodemodeDeclarations.RenderToolOutputType(schema));

        var withStructured = Schema("""
        {
          "type": "object",
          "properties": {
            "content": { "type": "array", "items": { "type": "object" } },
            "isError": { "type": "boolean" },
            "_meta": { "type": "object" },
            "structuredContent": { "type": "object", "properties": { "id": { "type": "string" } } }
          }
        }
        """);
        Assert.Equal("CallToolResult<{ id?: string; }>",
            CodemodeDeclarations.RenderToolOutputType(withStructured));

        // 非 CallToolResult → null。
        Assert.Null(CodemodeDeclarations.McpStructuredContentSchema(Schema("""{"type":"object"}""")));
    }

    [Fact]
    public void RendersToolSignaturesSamplesAndDeclarations()
    {
        var tool = new CodemodeTool
        {
            Name = "my-tool",
            Description = "Reads a file",
            InputSchema = Schema("""{"type":"object","properties":{"path":{"type":"string"}}}"""),
            OutputSchema = Schema("""{"type":"string"}"""),
            Execute = (_, _) => Task.FromResult<JsonNode?>(null),
        };
        Assert.Equal("my_tool(args: { path?: string; }): Promise<string>;",
            CodemodeDeclarations.RenderToolSignature(tool));

        var sample = CodemodeDeclarations.RenderToolSample(tool);
        Assert.StartsWith("Reads a file\n\ncodemode tool declaration:", sample);
        Assert.Contains("declare const tools: { my_tool(args: { path?: string; }): Promise<string>; };", sample);

        var declarations = CodemodeDeclarations.RenderDeclarations(new RenderDeclarationsOptions
        {
            Tools = [tool],
            Globals =
            [
                new CodemodeTool
                {
                    Name = "text",
                    Description = "Emits text",
                    Signature = "(value: string): void",
                    Execute = (_, _) => Task.FromResult<JsonNode?>(null),
                },
                new CodemodeTool
                {
                    Name = "images.attach",
                    InputSchema = Schema("""{"type":"string"}"""),
                    OutputSchema = Schema("""{"type":"boolean"}"""),
                    Execute = (_, _) => Task.FromResult<JsonNode?>(null),
                },
            ],
        });
        Assert.Contains("declare const tools: {", declarations);
        Assert.Contains("/** Emits text */\ndeclare function text(value: string): void;", declarations);
        Assert.Contains("declare const images: {\n  attach(args: string): Promise<boolean>;\n};", declarations);
    }

    [Fact]
    public void QuotesNonIdentifierPropertyKeys()
    {
        Assert.Equal("{ \"a-b\"?: string; }",
            CodemodeDeclarations.SchemaToType(Schema("""{"type":"object","properties":{"a-b":{"type":"string"}}}""")));
    }

    [Fact]
    public void McpPreambleIsExportedVerbatim()
    {
        Assert.Contains("type CallToolResult<TStructured = { [key: string]: unknown }> = {", CodemodeDeclarations.McpTypeScriptPreamble);
        Assert.Equal(16_000, CodemodeDeclarations.DefaultInputSchemaMaxChars);
    }
}

/// <summary>runtime/protocol.ts 测试（消息形状判定）。</summary>
public class ProtocolTests
{
    [Fact]
    public void RecognizesMessageShapes()
    {
        Assert.True(CodemodeProtocol.IsWorkerToHostMessage(JsonNode.Parse("""{"type":"call"}""")));
        Assert.True(CodemodeProtocol.IsWorkerToHostMessage(JsonNode.Parse("""{"type":"done"}""")));
        Assert.False(CodemodeProtocol.IsWorkerToHostMessage(JsonNode.Parse("""{"type":"result"}""")));
        Assert.False(CodemodeProtocol.IsWorkerToHostMessage(null));

        Assert.True(CodemodeProtocol.IsHostToWorkerMessage(JsonNode.Parse("""{"type":"result"}""")));
        Assert.False(CodemodeProtocol.IsHostToWorkerMessage(JsonNode.Parse("""{"type":"call"}""")));
    }
}
