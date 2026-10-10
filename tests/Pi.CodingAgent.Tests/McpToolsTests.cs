using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Extensions.Mcp;
using Pi.Mcp.Protocol;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the MCP tool adapter (port of <c>extensions/mcp/tools.ts</c>, batch
/// 4d-4b). Vectors are transcribed from the TS behaviour: tool names, the <c>CallToolResult</c>
/// output schema, the 20KB output limit, result conversion (text, images, resource links, binary
/// resources, <c>isError</c>, structured-content fallback), and the MCP content conversion.
/// </summary>
public class McpToolsTests
{
    private static McpTool MakeTool(
        string name = "search",
        string? description = "Search things",
        JsonObject? inputSchema = null,
        JsonObject? outputSchema = null,
        JsonObject? annotations = null) =>
        new(name, inputSchema ?? new JsonObject(), description)
        {
            OutputSchema = outputSchema,
            Annotations = annotations,
        };

    private static McpContentBlock TextBlock(string text) =>
        new(new JsonObject { ["type"] = "text", ["text"] = text });

    private static McpContentBlock ImageBlock(string data = "AAA", string mimeType = "image/png") =>
        new(new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = mimeType });

    // ------------------------------------------------------------------ tool names

    [Fact]
    public void CreateMcpToolName_SanitizesAndPrefixes()
    {
        Assert.Equal("mcp__srv__search", McpTools.CreateMcpToolName("srv", "search"));
        Assert.Equal("mcp__my_srv__a_b", McpTools.CreateMcpToolName("my-srv", "a.b"));
        Assert.Equal("mcp__srv__a_b", McpTools.CreateMcpToolName("srv", "a b"));
    }

    [Fact]
    public void CreateMcpToolName_LongName_GetsHashSuffix()
    {
        var name = McpTools.CreateMcpToolName("server", new string('x', 80));

        Assert.Equal(McpToolLimits.MaxToolNameLength, name.Length);
        Assert.StartsWith("mcp__server__", name, StringComparison.Ordinal);
        // 8 hex characters after the underscore.
        Assert.Matches("^mcp__server__x+_[0-9a-f]{8}$", name);
    }

    [Fact]
    public void CreateMcpToolName_SanitizedCollision_GetsHashSuffix()
    {
        var taken = McpTools.CreateMcpToolName("srv", "a-b");
        var other = McpTools.CreateMcpToolName("srv", "a_b", isTaken: name => name == taken);

        Assert.NotEqual(taken, other);
        Assert.EndsWith("_", other[..^8], StringComparison.Ordinal);
        Assert.Matches("^mcp__srv__a_b_[0-9a-f]{8}$", other);
    }

    [Fact]
    public void CreateMcpToolName_NotTaken_NoHashSuffix()
    {
        Assert.Equal(
            "mcp__srv__search",
            McpTools.CreateMcpToolName("srv", "search", isTaken: _ => false));
    }

    // ------------------------------------------------------------------ exposure

    [Fact]
    public void ToToolExposure_MapsCodemodeToDeferred()
    {
        Assert.Equal(McpServers.Exposures.Deferred, McpTools.ToToolExposure(McpServers.Exposures.Codemode));
        Assert.Equal(McpServers.Exposures.Deferred, McpTools.ToToolExposure(McpServers.Exposures.Deferred));
        Assert.Equal(McpServers.Exposures.Direct, McpTools.ToToolExposure(McpServers.Exposures.Direct));
        Assert.Equal(McpServers.Exposures.Hidden, McpTools.ToToolExposure(McpServers.Exposures.Hidden));
    }

    // ------------------------------------------------------------------ result schema

    [Fact]
    public void CreateMcpResultSchema_RequiresContentAndKeepsStructuredSchema()
    {
        var structured = JsonNode.Parse("""{ "type": "object", "properties": { "hits": { "type": "number" } } }""")!.AsObject();

        var schema = McpTools.CreateMcpResultSchema(structured);

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        var properties = schema["properties"]!.AsObject();
        Assert.Equal("array", properties["content"]!["type"]!.GetValue<string>());
        Assert.Equal("object", properties["content"]!["items"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", properties["isError"]!["type"]!.GetValue<string>());
        Assert.Equal("object", properties["_meta"]!["type"]!.GetValue<string>());
        Assert.NotNull(properties["structuredContent"]);
        Assert.Equal("number", properties["structuredContent"]!["properties"]!["hits"]!["type"]!.GetValue<string>());
        Assert.Equal(["content"], schema["required"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void CreateMcpResultSchema_WithoutStructuredSchema_OmitsKey()
    {
        var schema = McpTools.CreateMcpResultSchema(null);

        Assert.False(schema["properties"]!.AsObject().ContainsKey("structuredContent"));
    }

    // ------------------------------------------------------------------ output limit

    [Fact]
    public async Task LimitMcpContent_UnderLimit_Unchanged()
    {
        var content = new List<ContentBlock> { new TextContent(new string('a', 1000)) };

        var (limited, fullOutputPath) = await McpTools.LimitMcpContentAsync(content);

        Assert.Same(content, limited);
        Assert.Null(fullOutputPath);
    }

    [Fact]
    public async Task LimitMcpContent_OverLimit_TruncatesAndSavesFullText()
    {
        var content = new List<ContentBlock>
        {
            new TextContent(new string('a', 10 * 1024)),
            new TextContent(new string('b', 20 * 1024)),
        };

        var (limited, fullOutputPath) = await McpTools.LimitMcpContentAsync(content);

        var text = Assert.IsType<TextContent>(Assert.Single(limited));
        Assert.Contains("Warning: truncated output", text.Text, StringComparison.Ordinal);
        Assert.Contains("[Full output: ", text.Text, StringComparison.Ordinal);
        Assert.NotNull(fullOutputPath);
        Assert.True(File.Exists(fullOutputPath));
        Assert.Equal(new string('a', 10 * 1024) + "\n" + new string('b', 20 * 1024), File.ReadAllText(fullOutputPath));
        File.Delete(fullOutputPath);
    }

    [Fact]
    public async Task LimitMcpContent_OverLimit_KeepsImagesAfterText()
    {
        var content = new List<ContentBlock>
        {
            new TextContent(new string('a', 30 * 1024)),
            new ImageContent("AAA", "image/png"),
        };

        var (limited, _) = await McpTools.LimitMcpContentAsync(content);

        Assert.Equal(2, limited.Count);
        Assert.IsType<TextContent>(limited[0]);
        Assert.IsType<ImageContent>(limited[1]);
    }

    [Fact]
    public async Task LimitMcpContent_SaveFails_ReportsInsteadOfPath()
    {
        var content = new List<ContentBlock> { new TextContent(new string('a', 30 * 1024)) };

        var (limited, fullOutputPath) = await McpTools.LimitMcpContentAsync(
            content,
            (_, _) => throw new IOException("disk full"));

        var text = Assert.IsType<TextContent>(Assert.Single(limited));
        Assert.Contains("[Could not save the full output: disk full]", text.Text, StringComparison.Ordinal);
        Assert.Null(fullOutputPath);
    }

    // ------------------------------------------------------------------ result conversion

    [Fact]
    public async Task ConvertMcpResult_TextAndImage_PassesThrough()
    {
        var result = new CallToolResult([TextBlock("hello"), ImageBlock()]);

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", result, new ConvertMcpResultOptions());

        var blocks = converted.Content;
        Assert.Equal(2, blocks.Count);
        Assert.Equal("hello", Assert.IsType<TextContent>(blocks[0]).Text);
        Assert.IsType<ImageContent>(blocks[1]);
        var details = Assert.IsType<McpToolDetails>(converted.Details);
        Assert.Equal("srv", details.Server);
        Assert.Equal("search", details.Tool);
        Assert.Null(details.FullOutputPath);
    }

    [Fact]
    public async Task ConvertMcpResult_IsError_KeepsStructuredResultForScripts()
    {
        var result = new CallToolResult([TextBlock("boom")]) { IsError = true };

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", result, new ConvertMcpResultOptions());

        Assert.True(converted.IsError);
        Assert.Equal("boom", Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text);
        var script = Assert.IsType<JsonObject>(converted.StructuredContent);
        Assert.True(script["isError"]!.GetValue<bool>());
        Assert.False(script.AsObject().ContainsKey("_meta"));
        Assert.Equal("boom", script["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConvertMcpResult_IsErrorWithoutText_AddsFallbackText()
    {
        var result = new CallToolResult([]) { IsError = true };

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", result, new ConvertMcpResultOptions());

        Assert.Equal($"MCP tool srv/search returned an error", Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text);
    }

    [Fact]
    public async Task ConvertMcpResult_NoContentButStructuredContent_UsesJson()
    {
        var structured = JsonNode.Parse("""{ "hits": 3 }""")!.AsObject();
        var result = new CallToolResult([], StructuredContent: structured);

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", result, new ConvertMcpResultOptions());

        var text = Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text;
        Assert.Contains("\"hits\": 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertMcpResult_ResourceLink_NamesReadTool()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource_link",
            ["uri"] = "file:///docs/readme.md",
            ["name"] = "readme.md",
            ["mimeType"] = "text/markdown",
            ["size"] = 2048,
            ["description"] = "Project readme",
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]),
            new ConvertMcpResultOptions { ReadableResources = true });

        var text = Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text;
        Assert.Equal(
            "[Resource file:///docs/readme.md \"readme.md\" (text/markdown, 2.0KB): Project readme. " +
            $"Read it with {McpServers.ToolNames.ReadMcpResource} (server \"srv\")]",
            text);
    }

    [Fact]
    public async Task ConvertMcpResult_ResourceLinkWithoutReadableResources_NoReadHint()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource_link",
            ["uri"] = "file:///a.txt",
            ["name"] = "a.txt",
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]), new ConvertMcpResultOptions());

        var text = Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text;
        Assert.Equal("[Resource file:///a.txt \"a.txt\"]", text);
    }

    [Fact]
    public async Task ConvertMcpResult_EmbeddedTextResource_PassesTextThrough()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject { ["uri"] = "file:///a.txt", ["text"] = "file contents" },
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]), new ConvertMcpResultOptions());

        Assert.Equal("file contents", Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text);
    }

    [Fact]
    public async Task ConvertMcpResult_EmbeddedImageResource_BecomesImage()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.png",
                ["mimeType"] = "image/png",
                ["blob"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            },
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]), new ConvertMcpResultOptions());

        var image = Assert.IsType<ImageContent>(Assert.Single(converted.Content));
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("AQID", image.Data);
    }

    [Fact]
    public async Task ConvertMcpResult_BinaryResource_IsSavedToFile()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///dir/archive.zip",
                ["mimeType"] = "application/zip",
                ["blob"] = Convert.ToBase64String(data),
            },
        });

        var saved = string.Empty;
        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]),
            new ConvertMcpResultOptions
            {
                SaveOutput = (bytes, extension) =>
                {
                    saved = extension;
                    return Task.FromResult("/tmp/out" + extension);
                },
            });

        var text = Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text;
        Assert.Equal("[Binary resource file:///dir/archive.zip (application/zip, 4B) saved to /tmp/out.zip]", text);
        Assert.Equal(".zip", saved);
    }

    [Fact]
    public async Task ConvertMcpResult_TextBlobResource_ShownAsText()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.json",
                ["mimeType"] = "application/json",
                ["blob"] = Convert.ToBase64String("""{ "a": 1 }"""u8.ToArray()),
            },
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]), new ConvertMcpResultOptions());

        Assert.Equal("""{ "a": 1 }""", Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text);
    }

    [Fact]
    public async Task ConvertMcpResult_BinaryResourceSaveFails_ReportsError()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.bin",
                ["mimeType"] = "application/octet-stream",
                ["blob"] = Convert.ToBase64String(new byte[] { 1 }),
            },
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]),
            new ConvertMcpResultOptions
            {
                SaveOutput = (_, _) => throw new IOException("nope"),
            });

        var text = Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text;
        Assert.Contains("[Binary resource file:///a.bin (application/octet-stream, 1B) could not be saved: nope]", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertMcpResult_AudioResource_PlaceholderText()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.wav",
                ["mimeType"] = "audio/wav",
                ["blob"] = Convert.ToBase64String(new byte[] { 1 }),
            },
        });

        var converted = await McpTools.ConvertMcpResultAsync(
            "srv", "search", new CallToolResult([block]), new ConvertMcpResultOptions());

        var text = Assert.IsType<TextContent>(Assert.Single(converted.Content)).Text;
        Assert.Contains("[Binary resource file:///a.wav (audio/wav, 1B)", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ tool definition

    [Fact]
    public void CreateMcpToolDefinition_NormalizesSchemaAndMetadata()
    {
        var tool = MakeTool(
            inputSchema: new JsonObject { ["properties"] = new JsonObject { ["q"] = new JsonObject() } },
            outputSchema: new JsonObject { ["type"] = "object" },
            annotations: JsonNode.Parse("""{ "title": "Search", "readOnlyHint": true, "destructiveHint": false }""")!.AsObject());

        var definition = McpTools.CreateMcpToolDefinition(new McpTools.CreateMcpToolOptions
        {
            Server = "srv",
            Tool = tool,
            Name = "mcp__srv__search",
            Exposure = McpServers.Exposures.Codemode,
            Namespace = new ToolNamespace { Name = "srv" },
            TimeoutMs = 60_000,
            GetClient = () => throw new InvalidOperationException("not called"),
        });

        Assert.Equal("mcp__srv__search", definition.Name);
        Assert.Equal("srv/search", definition.Label);
        Assert.Equal("Search things", definition.Description);
        Assert.True(definition.Annotations!.ReadOnlyHint);
        Assert.False(definition.Annotations.DestructiveHint);
        Assert.Equal(McpServers.Exposures.Deferred, definition.Exposure);
        // Missing `type` is added and `properties` is present.
        Assert.Equal("object", ((JsonValue)definition.Parameters.JsonSchema["type"]!).GetValue<string>(), ignoreCase: true);
        Assert.NotNull(definition.Parameters.JsonSchema["properties"]);
        Assert.NotNull(definition.OutputSchema);
        Assert.NotNull(definition.Execute);
    }

    [Fact]
    public void CreateMcpToolDefinition_WithoutDescription_UsesTitle()
    {
        var tool = new McpTool("search", new JsonObject(), null, "Search things")
        {
            Annotations = JsonNode.Parse("""{ "title": "Search" }""")!.AsObject(),
        };

        var definition = McpTools.CreateMcpToolDefinition(new McpTools.CreateMcpToolOptions
        {
            Server = "srv",
            Tool = tool,
            Name = "mcp__srv__search",
            Exposure = McpServers.Exposures.Direct,
            Namespace = new ToolNamespace { Name = "srv" },
            TimeoutMs = 60_000,
            GetClient = () => throw new InvalidOperationException("not called"),
        });

        Assert.Equal("Search", definition.Description);
    }

    // ------------------------------------------------------------------ MCP content conversion

    [Fact]
    public void ToLlmContent_TextAndImage()
    {
        var blocks = new List<McpContentBlock> { TextBlock("hi"), ImageBlock("AAA", "image/png") };

        var content = McpContent.ToLlmContent(blocks, null);

        Assert.Equal(2, content.Count);
        Assert.Equal("hi", Assert.IsType<LlmTextContent>(content[0]).Text);
        var image = Assert.IsType<LlmImageContent>(content[1]);
        Assert.Equal("AAA", image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public void ToLlmContent_NoBlocksButStructuredContent_UsesJson()
    {
        var structured = JsonNode.Parse("""{ "hits": 3 }""")!.AsObject();

        var content = McpContent.ToLlmContent([], structured);

        var text = Assert.IsType<LlmTextContent>(Assert.Single(content)).Text;
        Assert.Contains("\"hits\": 3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToLlmContent_UnsupportedBlock_PlaceholderText()
    {
        var block = new McpContentBlock(new JsonObject { ["type"] = "audio", ["data"] = "AAA", ["mimeType"] = "audio/wav" });

        var content = McpContent.ToLlmContent([block], null);

        Assert.Equal("[audio audio/wav omitted]", Assert.IsType<LlmTextContent>(Assert.Single(content)).Text);
    }

    [Fact]
    public void ToLlmContent_ResourceLink_Text()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource_link",
            ["uri"] = "file:///a.txt",
            ["name"] = "a.txt",
        });

        var content = McpContent.ToLlmContent([block], null);

        Assert.Equal("a.txt: file:///a.txt", Assert.IsType<LlmTextContent>(Assert.Single(content)).Text);
    }

    [Fact]
    public void ToLlmContent_EmbeddedImageResource_BecomesImage()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.png",
                ["mimeType"] = "image/png",
                ["blob"] = "AQID",
            },
        });

        var content = McpContent.ToLlmContent([block], null);

        var image = Assert.IsType<LlmImageContent>(Assert.Single(content));
        Assert.Equal("AQID", image.Data);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public void ToLlmContent_BinaryResource_PlaceholderText()
    {
        var block = new McpContentBlock(new JsonObject
        {
            ["type"] = "resource",
            ["resource"] = new JsonObject
            {
                ["uri"] = "file:///a.bin",
                ["mimeType"] = "application/octet-stream",
                ["blob"] = "AQID",
            },
        });

        var content = McpContent.ToLlmContent([block], null);

        Assert.Equal(
            "[binary resource file:///a.bin (application/octet-stream) omitted]",
            Assert.IsType<LlmTextContent>(Assert.Single(content)).Text);
    }

    [Fact]
    public void ToLlmContent_UnknownType_PlaceholderText()
    {
        var block = new McpContentBlock(new JsonObject { ["type"] = "video" });

        var content = McpContent.ToLlmContent([block], null);

        Assert.Equal("[unsupported MCP content video]", Assert.IsType<LlmTextContent>(Assert.Single(content)).Text);
    }
}
