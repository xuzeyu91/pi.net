using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Replays the 4e-2b slice (<c>core/messages.ts</c>) against <c>messages-corpus.json</c>, which was
/// captured by running the original TypeScript module in Node.
/// </summary>
/// <remarks>
/// Covers the four coding-agent message roles, the four summary prefix/suffix constants,
/// <c>bashExecutionToText</c>, the three message factories and <c>convertToLlm</c>. Every comparison
/// goes through <see cref="AgentMessageJson.Options"/>, so the vectors pin the JSON wire shape
/// (role discriminator, camelCase names, null-vs-omitted) as well as the logic.
/// </remarks>
public class MessagesCorpusTests
{
    private static readonly JsonElement Corpus = LoadCorpus();

    private static JsonElement LoadCorpus()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "messages-corpus.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    private static JsonSerializerOptions Wire => AgentMessageJson.Options;

    private static JsonNode Serialize(ChatMessage message)
        => JsonNode.Parse(JsonSerializer.Serialize(message, Wire))!;

    private static JsonNode Expected(JsonElement element) => JsonNode.Parse(element.GetRawText())!;

    // ------------------------------------------------------------------ corpus shape

    [Fact]
    public void Corpus_IsComplete()
    {
        Assert.Equal(4, Corpus.GetProperty("constants").EnumerateObject().Count());
        Assert.Equal(17, Corpus.GetProperty("bashExecutionToText").GetArrayLength());
        Assert.Equal(5, Corpus.GetProperty("createBranchSummaryMessage").GetArrayLength());
        Assert.Equal(5, Corpus.GetProperty("createCompactionSummaryMessage").GetArrayLength());
        Assert.Equal(3, Corpus.GetProperty("createCustomMessage").GetArrayLength());
        Assert.Equal(10, Corpus.GetProperty("convertToLlm").GetArrayLength());
    }

    // ------------------------------------------------------------------ constants

    [Fact]
    public void Constants_MatchTypeScript()
    {
        var constants = Corpus.GetProperty("constants");
        Assert.Equal(AgentMessages.CompactionSummaryPrefix, constants.GetProperty("COMPACTION_SUMMARY_PREFIX").GetString());
        Assert.Equal(AgentMessages.CompactionSummarySuffix, constants.GetProperty("COMPACTION_SUMMARY_SUFFIX").GetString());
        Assert.Equal(AgentMessages.BranchSummaryPrefix, constants.GetProperty("BRANCH_SUMMARY_PREFIX").GetString());
        Assert.Equal(AgentMessages.BranchSummarySuffix, constants.GetProperty("BRANCH_SUMMARY_SUFFIX").GetString());
    }

    // ------------------------------------------------------------------ bashExecutionToText

    [Fact]
    public void BashExecutionToText_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("bashExecutionToText").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var message = BuildBashExecutionMessage(vector.GetProperty("message"));
            var expected = vector.GetProperty("text").GetString()!;
            var actual = AgentMessages.BashExecutionToText(message);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                failures.Add($"{name}:\n  expected {JsonSerializer.Serialize(expected)}\n  actual   {JsonSerializer.Serialize(actual)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    // ------------------------------------------------------------------ factories

    [Fact]
    public void BranchSummaryFactory_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("createBranchSummaryMessage").EnumerateArray())
        {
            var summary = vector.GetProperty("summary").GetString()!;
            var fromId = vector.GetProperty("fromId").GetString();
            var timestamp = vector.GetProperty("timestamp").GetString()!;
            var actual = AgentMessages.CreateBranchSummaryMessage(summary, fromId, timestamp);
            Compare(Expected(vector.GetProperty("message")), Serialize(actual), timestamp, failures);
        }

        var nullFrom = Corpus.GetProperty("createBranchSummaryMessageNullFrom");
        Compare(
            Expected(nullFrom.GetProperty("message")),
            Serialize(AgentMessages.CreateBranchSummaryMessage(
                nullFrom.GetProperty("summary").GetString()!,
                nullFrom.GetProperty("fromId").GetString(),
                nullFrom.GetProperty("timestamp").GetString()!)),
            "null-fromId",
            failures);

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void CompactionSummaryFactory_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("createCompactionSummaryMessage").EnumerateArray())
        {
            var summary = vector.GetProperty("summary").GetString()!;
            var tokensBefore = vector.GetProperty("tokensBefore").GetInt64();
            var timestamp = vector.GetProperty("timestamp").GetString()!;
            var actual = AgentMessages.CreateCompactionSummaryMessage(summary, tokensBefore, timestamp);
            Compare(Expected(vector.GetProperty("message")), Serialize(actual), timestamp, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void CustomFactory_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("createCustomMessage").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            // The explicit-null `details` vector is pinned separately: C# cannot tell "unset" from
            // "explicitly null" for an object property (see CustomFactory_NullDetails_DivergesAsDocumented).
            if (IsNullDetails(vector))
            {
                continue;
            }

            var actual = AgentMessages.CreateCustomMessage(
                vector.GetProperty("customType").GetString()!,
                ReadContent(vector.GetProperty("content")),
                vector.GetProperty("display").GetBoolean(),
                ReadDetails(vector),
                vector.GetProperty("timestamp").GetString()!);
            Compare(Expected(vector.GetProperty("message")), Serialize(actual), name, failures);
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    /// <summary>
    /// TS 的 <c>details?: T</c> 可以显式传 <c>null</c>，此时 JSON 写出 <c>"details": null</c>；
    /// 而 <c>undefined</c> 会被整条省略。C# 的 <c>object?</c> 无法区分这两种情况（<c>null</c> 即「未设置」，
    /// 序列化时省略），故端口在显式 <c>null</c> 时会少写一个字段。真实调用点传的是
    /// <c>entry.details</c>（<c>T | undefined</c>），不会走到显式 <c>null</c>。
    /// </summary>
    [Fact]
    public void CustomFactory_NullDetails_DivergesAsDocumented()
    {
        var vector = Corpus.GetProperty("createCustomMessage").EnumerateArray().Single(IsNullDetails);

        // TS 侧确实写出了 null（而不是省略）。
        using (var document = JsonDocument.Parse(vector.GetProperty("message").GetRawText()))
        {
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("details").ValueKind);
        }

        var actual = Serialize(AgentMessages.CreateCustomMessage(
            vector.GetProperty("customType").GetString()!,
            ReadContent(vector.GetProperty("content")),
            vector.GetProperty("display").GetBoolean(),
            ReadDetails(vector),
            vector.GetProperty("timestamp").GetString()!));

        Assert.False(actual.AsObject().ContainsKey("details"));

        // 除该字段外两者完全一致。
        var expected = Expected(vector.GetProperty("message")).AsObject();
        expected.Remove("details");
        Assert.True(JsonNode.DeepEquals(expected, actual), $"unexpected divergence:\n  {expected}\n  {actual}");
    }

    // ------------------------------------------------------------------ convertToLlm

    [Fact]
    public void ConvertToLlm_MatchesTypeScript()
    {
        var failures = new List<string>();
        foreach (var vector in Corpus.GetProperty("convertToLlm").EnumerateArray())
        {
            var name = vector.GetProperty("name").GetString()!;
            var inputs = vector.GetProperty("messages").EnumerateArray()
                .Select(message => JsonSerializer.Deserialize<ChatMessage>(message.GetRawText(), Wire)!)
                .ToList();

            var converted = AgentMessages.ConvertToLlm(inputs);
            var actual = new JsonArray(converted.Select(message => (JsonNode?)Serialize(message)).ToArray());
            var expected = Expected(vector.GetProperty("converted"));

            if (!JsonNode.DeepEquals(expected, actual))
            {
                failures.Add($"{name}:\n  expected {expected}\n  actual   {actual}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n---\n", failures));
    }

    [Fact]
    public void ConvertToLlm_Passthrough_ReturnsSameInstance()
    {
        // TS 的 `case "system": case "user": ... return m;` 返回原对象本身，不做拷贝。
        var system = new SystemMessage("sys", Timestamp: 1);
        var user = new UserMessage([new TextContent("hi")], 2);
        var assistant = new AssistantMessage([new TextContent("yo")], StopReason.Stop, Timestamp: 3);
        var toolResult = new ToolResultMessage("c1", "read", [new TextContent("f")], Timestamp: 4);

        var converted = AgentMessages.ConvertToLlm([system, user, assistant, toolResult]);

        Assert.Equal(4, converted.Count);
        Assert.Same(system, converted[0]);
        Assert.Same(user, converted[1]);
        Assert.Same(assistant, converted[2]);
        Assert.Same(toolResult, converted[3]);
    }

    // ------------------------------------------------------------------ wire shape

    [Fact]
    public void AgentMessageJson_RoundTripsAllEightRoles()
    {
        var samples = new (ChatMessage Message, string Role)[]
        {
            (new SystemMessage("sys", Timestamp: 1), "system"),
            (new UserMessage([new TextContent("hi")], 2), "user"),
            (new AssistantMessage([new TextContent("yo")], StopReason.Stop, Timestamp: 3), "assistant"),
            (new ToolResultMessage("c1", "read", [new TextContent("f")], Timestamp: 4), "toolResult"),
            (new BashExecutionMessage("ls", "out", 0, false, false, 5), "bashExecution"),
            (new CustomMessage("t", new MessageContent.Text("c"), true, 6), "custom"),
            (new BranchSummaryMessage("s", null, 7), "branchSummary"),
            (new CompactionSummaryMessage("s", 10, 8), "compactionSummary"),
        };

        foreach (var (message, role) in samples)
        {
            var json = JsonSerializer.Serialize(message, Wire);
            Assert.Equal(role, JsonNode.Parse(json)!["role"]!.GetValue<string>());

            var roundTripped = JsonSerializer.Deserialize<ChatMessage>(json, Wire);
            Assert.NotNull(roundTripped);
            Assert.Equal(message.GetType(), roundTripped!.GetType());
            // record 的生成相等性对集合属性是引用比较，故用「再序列化后与原 JSON 完全一致」
            // 表达结构等价。
            Assert.Equal(json, JsonSerializer.Serialize(roundTripped, Wire));
        }
    }

    [Fact]
    public void CustomMessage_BlockContent_RoundTrips()
    {
        var message = new CustomMessage(
            "artifact",
            new MessageContent.Blocks([new TextContent("hello"), new ImageContent("AAAA", "image/png")]),
            Display: false,
            9);

        var json = JsonSerializer.Serialize<ChatMessage>(message, Wire);
        Assert.Contains("\"content\":[", json, StringComparison.Ordinal);

        var roundTripped = Assert.IsType<CustomMessage>(JsonSerializer.Deserialize<ChatMessage>(json, Wire));
        var blocks = Assert.IsType<MessageContent.Blocks>(roundTripped.Content);
        Assert.Equal(2, blocks.Value.Count);
        Assert.Equal("hello", Assert.IsType<TextContent>(blocks.Value[0]).Text);
        Assert.Equal("AAAA", Assert.IsType<ImageContent>(blocks.Value[1]).Data);
    }

    [Fact]
    public void CustomMessage_StringContent_WritesBareString()
    {
        var message = new CustomMessage("artifact", new MessageContent.Text("hello"), Display: true, 10);
        var json = JsonSerializer.Serialize<ChatMessage>(message, Wire);
        Assert.Contains("\"content\":\"hello\"", json, StringComparison.Ordinal);

        var back = Assert.IsType<CustomMessage>(JsonSerializer.Deserialize<ChatMessage>(json, Wire));
        Assert.Equal(message.CustomType, back.CustomType);
        Assert.True(back.Display);
        Assert.Equal(10, back.Timestamp);
        Assert.Equal("hello", Assert.IsType<MessageContent.Text>(back.Content).Value);
    }

    // ------------------------------------------------------------------ helpers

    private static void Compare(JsonNode expected, JsonNode actual, string label, List<string> failures)
    {
        if (!JsonNode.DeepEquals(expected, actual))
        {
            failures.Add($"{label}:\n  expected {expected}\n  actual   {actual}");
        }
    }

    private static bool IsNullDetails(JsonElement vector)
        => vector.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Null;

    private static MessageContent ReadContent(JsonElement content)
        => content.ValueKind == JsonValueKind.String
            ? new MessageContent.Text(content.GetString()!)
            : new MessageContent.Blocks(
                JsonSerializer.Deserialize<List<ContentBlock>>(content.GetRawText(), Wire)!);

    private static object? ReadDetails(JsonElement vector)
        => vector.TryGetProperty("details", out var details) && details.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(details.GetRawText())
            : null;

    private static BashExecutionMessage BuildBashExecutionMessage(JsonElement message)
        => new(
            Command: message.TryGetProperty("command", out var command) ? command.GetString()! : "",
            Output: message.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.String
                ? output.GetString()!
                : "",
            ExitCode: message.TryGetProperty("exitCode", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number
                ? exitCode.GetInt32()
                : null,
            Cancelled: message.TryGetProperty("cancelled", out var cancelled) && cancelled.GetBoolean(),
            Truncated: message.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean(),
            Timestamp: message.TryGetProperty("timestamp", out var timestamp) ? timestamp.GetInt64() : 0)
        {
            FullOutputPath = message.TryGetProperty("fullOutputPath", out var path) ? path.GetString() : null,
            ExcludeFromContext = message.TryGetProperty("excludeFromContext", out var exclude)
                ? exclude.GetBoolean()
                : null,
        };
}
