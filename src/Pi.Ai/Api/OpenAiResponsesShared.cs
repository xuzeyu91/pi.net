using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Stream;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>Responses 流处理附加选项。对应 TS <c>OpenAIResponsesStreamOptions</c>。</summary>
public sealed record ResponsesStreamOptions
{
    /// <summary>观察 provider 的原始流事件。</summary>
    public Func<JsonObject, ModelSpec, Task>? OnProviderStreamEvent { get; init; }

    /// <summary>请求的 service tier（flex/priority/fast/default/auto）。</summary>
    public string? ServiceTier { get; init; }

    /// <summary>service tier 解析（响应 tier vs 请求 tier）。对应 TS <c>resolveServiceTier</c>。</summary>
    public Func<string?, string?, string?>? ResolveServiceTier { get; init; }

    /// <summary>工具名 → grammar 输入属性名（custom tool call 的流式输入缓冲）。</summary>
    public IReadOnlyDictionary<string, string>? GrammarToolInputProperties { get; init; }

    /// <summary>用量成本按 service tier 折算（返回折算后的用量）。</summary>
    public Func<Usage, string?, Usage>? ApplyServiceTierPricing { get; init; }
}

/// <summary>消息转换选项。对应 TS <c>ConvertResponsesMessagesOptions</c>。</summary>
public sealed record ConvertResponsesMessagesOptions
{
    public bool? IncludeSystemPrompt { get; init; }

    public IReadOnlyDictionary<string, string>? GrammarToolInputProperties { get; init; }

    /// <summary>后续 system 消息是否就地发送；否则折叠进前导提示。</summary>
    public bool? SupportsMidConvoSystemMessages { get; init; }

    public bool? SupportsAdditionalTools { get; init; }

    public bool? SupportsToolSearch { get; init; }

    public ConvertResponsesToolsOptions? ToolOptions { get; init; }
}

/// <summary>工具转换选项。对应 TS <c>ConvertResponsesToolsOptions</c>。</summary>
public sealed record ConvertResponsesToolsOptions
{
    public bool? Strict { get; init; }

    public bool? SupportsStrictMode { get; init; }

    public bool? SupportsOpenAiGrammarTools { get; init; }

    public bool? ToolSearchResult { get; init; }
}

/// <summary>
/// OpenAI Responses 消息/工具转换与流事件处理。对应 TS
/// <c>api/openai-responses-shared.ts</c>。
/// </summary>
public static class OpenAiResponsesShared
{
    // =============================================================================
    // Utilities
    // =============================================================================

    /// <summary>编码 openai-responses 文本签名（TextSignatureV1）。对应 TS <c>encodeTextSignatureV1</c>。</summary>
    public static string EncodeTextSignatureV1(string id, string? phase = null)
    {
        var payload = new JsonObject { ["v"] = 1, ["id"] = id };
        if (phase is not null) payload["phase"] = phase;
        return payload.ToJsonString();
    }

    /// <summary>解析文本签名（TextSignatureV1 JSON 或旧版纯串）。对应 TS <c>parseTextSignature</c>。</summary>
    public static (string Id, string? Phase)? ParseTextSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        if (signature.StartsWith('{'))
        {
            try
            {
                var parsed = JsonNode.Parse(signature) as JsonObject;
                if (parsed?.Num("v") == 1 && parsed.Str("id") is { } id)
                {
                    var phase = parsed.Str("phase");
                    return phase is "commentary" or "final_answer" ? (id, phase) : (id, null);
                }
            }
            catch (JsonException)
            {
                // 回落到旧版纯串处理。
            }
        }
        return (signature, null);
    }

    /// <summary>工具结果输出：纯文本或文本+图片数组。对应 TS <c>convertToolResultOutput</c>。</summary>
    public static JsonNode ConvertToolResultOutput(ModelSpec model, IReadOnlyList<ContentBlock> content)
    {
        var textResult = string.Join("\n",
            content.OfType<TextContent>().Select(c => c.Text));
        var images = content.OfType<ImageContent>().ToList();
        var hasText = textResult.Length > 0;

        if (images.Count == 0 || !model.Input.Contains("image"))
        {
            var fallback = hasText ? textResult : images.Count > 0 ? "(see attached image)" : "(no tool output)";
            return SanitizeUnicode.SanitizeSurrogates(fallback);
        }

        var output = new JsonArray();
        if (hasText)
        {
            output.Add(new JsonObject { ["type"] = "input_text", ["text"] = SanitizeUnicode.SanitizeSurrogates(textResult) });
        }
        foreach (var image in images)
        {
            output.Add(new JsonObject
            {
                ["type"] = "input_image",
                ["detail"] = "auto",
                ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
            });
        }
        return output;
    }

    // =============================================================================
    // Message conversion
    // =============================================================================

    /// <summary>
    /// 把 transcript 转换为 Responses <c>input</c> 数组。对应 TS
    /// <c>convertResponsesMessages</c>：思维块以签名 JSON 重放为 reasoning 项，
    /// 文本以 msg_pi_* 兜底 id 重放（签名带真实 id），工具调用按
    /// callId|itemId 拆分归一化。
    /// </summary>
    public static JsonArray ConvertResponsesMessages(
        ModelSpec model,
        TranscriptContext context,
        IReadOnlySet<string> allowedToolCallProviders,
        ConvertResponsesMessagesOptions? options = null)
    {
        var normalizedContext = Transcript.ResolveTranscript(context, options?.SupportsMidConvoSystemMessages);
        var messages = new JsonArray();

        static string NormalizeIdPart(string part)
        {
            var sanitized = new string(part.Select(c
                => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
            var normalized = sanitized.Length > 64 ? sanitized[..64] : sanitized;
            return normalized.TrimEnd('_');
        }

        static string BuildForeignResponsesItemId(string itemId)
            => $"fc_{Hash.ShortHash(itemId)}"[..Math.Min(64, $"fc_{Hash.ShortHash(itemId)}".Length)];

        string NormalizeToolCallId(string id, ModelSpec _targetModel, AssistantMessage source)
        {
            if (!allowedToolCallProviders.Contains(model.Provider)) return NormalizeIdPart(id);
            if (!id.Contains('|')) return NormalizeIdPart(id);
            var separator = id.IndexOf('|');
            var callId = id[..separator];
            var itemId = id[(separator + 1)..];
            var normalizedCallId = NormalizeIdPart(callId);
            var isForeignToolCall = source.Provider != model.Provider || source.Api != model.Api;
            var normalizedItemId = isForeignToolCall ? BuildForeignResponsesItemId(itemId) : NormalizeIdPart(itemId);
            // OpenAI Responses 要求 item id 以 "fc" 开头。
            if (!normalizedItemId.StartsWith("fc_", StringComparison.Ordinal))
            {
                normalizedItemId = NormalizeIdPart($"fc_{normalizedItemId}");
            }
            return $"{normalizedCallId}|{normalizedItemId}";
        }

        var transformedMessages = TransformMessages.Transform(
            normalizedContext.Messages, model, NormalizeToolCallId);
        var (requestTools, anchorsAdditions) = Transcript.ResolveTranscriptTools(
            normalizedContext.Messages,
            (options?.SupportsAdditionalTools ?? false) || (options?.SupportsToolSearch ?? false));

        void AppendSystemToolAdditions(SystemMessage message, string seed)
        {
            var tools = anchorsAdditions ? message.ToolsAdded ?? [] : [];
            if (tools.Count == 0) return;
            if (options?.SupportsAdditionalTools == true)
            {
                messages.Add(new JsonObject
                {
                    ["type"] = "additional_tools",
                    ["role"] = "developer",
                    ["tools"] = ConvertResponsesTools(tools, options?.ToolOptions),
                });
                return;
            }
            if (options?.SupportsToolSearch != true) return;
            var names = tools.Select(tool => tool.Name).ToList();
            var callId = $"pi_tool_load_{Hash.ShortHash($"{seed}:{string.Join(",", names)}")}";
            messages.Add(new JsonObject
            {
                ["type"] = "tool_search_call",
                ["call_id"] = callId,
                ["execution"] = "client",
                ["status"] = "completed",
                ["arguments"] = new JsonObject { ["query"] = string.Join(" ", names), ["limit"] = names.Count },
            });
            messages.Add(new JsonObject
            {
                ["type"] = "tool_search_output",
                ["call_id"] = callId,
                ["execution"] = "client",
                ["status"] = "completed",
                ["tools"] = ConvertResponsesTools(tools, new ConvertResponsesToolsOptions
                {
                    SupportsStrictMode = options?.ToolOptions?.SupportsStrictMode,
                    SupportsOpenAiGrammarTools = options?.ToolOptions?.SupportsOpenAiGrammarTools,
                    ToolSearchResult = true,
                }),
            });
        }

        var includeInitialSystemMessage = options?.IncludeSystemPrompt ?? true;
        var supportsDeveloperRole = model.Extra?["compat"]?["supportsDeveloperRole"] is JsonValue { } devRole
            ? devRole.TryGetValue<bool>(out var flag) && flag
            : true;
        var instructionRole = model.Reasoning && supportsDeveloperRole ? "developer" : "system";

        var msgIndex = 0;
        var sourceIndex = 0;
        foreach (var msg in transformedMessages)
        {
            var isLeadingSystemMessage = sourceIndex++ == 0 && msg is SystemMessage;
            switch (msg)
            {
                case SystemMessage system:
                {
                    if (!isLeadingSystemMessage) AppendSystemToolAdditions(system, $"system:{msgIndex}");
                    if (!isLeadingSystemMessage || includeInitialSystemMessage)
                    {
                        var text = isLeadingSystemMessage
                            ? Text.GetSystemMessageText(system)
                            : Text.RenderSystemMessageUpdate(system);
                        if (text.Length > 0)
                        {
                            messages.Add(new JsonObject
                            {
                                ["role"] = instructionRole,
                                ["content"] = SanitizeUnicode.SanitizeSurrogates(text),
                            });
                        }
                    }
                    break;
                }
                case UserMessage user:
                {
                    var content = new JsonArray();
                    foreach (var item in user.Content)
                    {
                        switch (item)
                        {
                            case TextContent text:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "input_text",
                                    ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text ?? ""),
                                });
                                break;
                            case ImageContent image:
                                content.Add(new JsonObject
                                {
                                    ["type"] = "input_image",
                                    ["detail"] = "auto",
                                    ["image_url"] = $"data:{image.MimeType};base64,{image.Data}",
                                });
                                break;
                        }
                    }
                    if (content.Count == 0) { msgIndex++; continue; }
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
                case AssistantMessage assistantMsg:
                {
                    // 用 List 承载再逐个 Add：JsonArray 元素挂父后再 Add 到另一数组会双父报错。
                    var output = new List<JsonObject>();
                    var isSameProviderAndApi =
                        assistantMsg.Provider == model.Provider && assistantMsg.Api == model.Api;
                    var isSameModel = isSameProviderAndApi && assistantMsg.Model == model.Id;
                    var isDifferentModel = isSameProviderAndApi && assistantMsg.Model != model.Id;
                    var textBlockIndex = 0;

                    foreach (var block in assistantMsg.Content)
                    {
                        switch (block)
                        {
                            case ThinkingContent { Signature: not null } thinking:
                            {
                                // 思维签名直接是 reasoning 项的 wire JSON（重放）。
                                if (JsonNode.Parse(thinking.Signature) is JsonObject reasoningItem)
                                {
                                    output.Add(reasoningItem);
                                }
                                break;
                            }
                            case TextContent textBlock:
                            {
                                var parsedSignature = ParseTextSignature(textBlock.TextSignature);
                                var blockText = textBlock.Text ?? "";
                                var fallbackMessageId = textBlockIndex == 0
                                    ? $"msg_pi_{msgIndex}"
                                    : $"msg_pi_{msgIndex}_{textBlockIndex}";
                                textBlockIndex++;
                                // OpenAI 要求 id 最长 64 字符。
                                var msgId = parsedSignature?.Id ?? fallbackMessageId;
                                if (msgId.Length > 64) msgId = $"msg_{Hash.ShortHash(msgId)}";
                                output.Add(new JsonObject
                                {
                                    ["type"] = "message",
                                    ["role"] = "assistant",
                                    ["content"] = new JsonArray(new JsonObject
                                    {
                                        ["type"] = "output_text",
                                        ["text"] = SanitizeUnicode.SanitizeSurrogates(blockText),
                                        ["annotations"] = new JsonArray(),
                                    }),
                                    ["status"] = "completed",
                                    ["id"] = msgId,
                                });
                                if (parsedSignature?.Phase is { } phase && output[^1] is JsonObject messageItem)
                                {
                                    messageItem["phase"] = phase;
                                }
                                break;
                            }
                            case ToolCallContent toolCall:
                            {
                                var separator = toolCall.Id.IndexOf('|');
                                var callId = separator >= 0 ? toolCall.Id[..separator] : toolCall.Id;
                                var itemIdRaw = separator >= 0 ? toolCall.Id[(separator + 1)..] : null;
                                var customInputProperty =
                                    options?.GrammarToolInputProperties?.TryGetValue(toolCall.Name, out var prop) == true
                                        ? prop
                                        : null;
                                string? itemId = itemIdRaw;

                                // 不同模型的消息省略 id 避免配对校验（OpenAI 会追踪
                                // rs_xxx reasoning 项与 item id 的配对）；同时丢弃与
                                // 回放项类型不匹配的 id（function_call 须 fc_*、
                                // custom_tool_call 须 ctc_*）。
                                var itemIdPrefix = customInputProperty is null ? "fc_" : "ctc_";
                                if (isDifferentModel || itemId is null || !itemId.StartsWith(itemIdPrefix, StringComparison.Ordinal))
                                {
                                    itemId = null;
                                }

                                if (customInputProperty is not null)
                                {
                                    output.Add(new JsonObject
                                    {
                                        ["type"] = "custom_tool_call",
                                        ["id"] = itemId,
                                        ["call_id"] = callId,
                                        ["name"] = toolCall.Name,
                                        ["input"] = SanitizeUnicode.SanitizeSurrogates(
                                            ConstrainedSampling.GetGrammarToolInput(
                                                toolCall.Name, toolCall.Arguments, customInputProperty) ?? ""),
                                    });
                                    if (isSameModel && toolCall.Namespace is not null && output[^1] is JsonObject customCall)
                                    {
                                        customCall["namespace"] = toolCall.Namespace;
                                    }
                                }
                                else
                                {
                                    output.Add(new JsonObject
                                    {
                                        ["type"] = "function_call",
                                        ["id"] = itemId,
                                        ["call_id"] = callId,
                                        ["name"] = toolCall.Name,
                                        ["arguments"] = JsonSerializer.Serialize(toolCall.Arguments),
                                    });
                                    if (isSameModel && toolCall.Namespace is not null && output[^1] is JsonObject functionCall)
                                    {
                                        functionCall["namespace"] = toolCall.Namespace;
                                    }
                                }
                                break;
                            }
                        }
                    }
                    if (output.Count == 0) { msgIndex++; continue; }
                    foreach (var item in output) messages.Add(item);
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    var separator = toolResult.ToolCallId.IndexOf('|');
                    var callId = separator >= 0 ? toolResult.ToolCallId[..separator] : toolResult.ToolCallId;
                    var output = ConvertToolResultOutput(model, toolResult.Content);

                    if (options?.GrammarToolInputProperties?.ContainsKey(toolResult.ToolName) == true)
                    {
                        messages.Add(new JsonObject
                        {
                            ["type"] = "custom_tool_call_output",
                            ["call_id"] = callId,
                            ["output"] = output,
                        });
                    }
                    else
                    {
                        messages.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["call_id"] = callId,
                            ["output"] = output,
                        });
                    }
                    break;
                }
            }
            if (!isLeadingSystemMessage) msgIndex++;
        }

        return messages;
    }

    // =============================================================================
    // Tool conversion
    // =============================================================================

    /// <summary>工具声明 → Responses tools 数组。对应 TS <c>convertResponsesTools</c>。</summary>
    public static JsonArray ConvertResponsesTools(
        IReadOnlyList<ToolDefinition> tools, ConvertResponsesToolsOptions? options = null)
    {
        var defaultStrict = options?.Strict ?? false;
        var supportsStrictMode = options?.SupportsStrictMode ?? true;
        var supportsOpenAiGrammarTools = options?.SupportsOpenAiGrammarTools ?? false;

        var result = new JsonArray();
        foreach (var tool in tools)
        {
            var grammar = ConstrainedSampling.ResolveGrammarConstrainedSampling(tool, supportsOpenAiGrammarTools);
            if (grammar is not null)
            {
                var grammarTool = new JsonObject
                {
                    ["type"] = "custom",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["format"] = new JsonObject
                    {
                        ["type"] = "grammar",
                        ["syntax"] = grammar.Format,
                        ["definition"] = grammar.Definition,
                    },
                };
                if (options?.ToolSearchResult == true) grammarTool["defer_loading"] = true;
                result.Add(grammarTool);
                continue;
            }

            var constrainedStrict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var strict = constrainedStrict ?? defaultStrict;
            var functionTool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["parameters"] = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict),
            };
            if (options?.ToolSearchResult == true) functionTool["defer_loading"] = true;
            if (supportsStrictMode) functionTool["strict"] = strict;
            result.Add(functionTool);
        }
        return result;
    }

    // =============================================================================
    // Stream processing
    // =============================================================================

    // =============================================================================
    // Stream processing（可变草稿 + 每事件重建快照模式——对齐 TS 的可变 output 语义）
    // =============================================================================

    /// <summary>
    /// 流式累积的可变助手消息状态。对应 TS 中贯穿流处理的可变 <c>output</c>
    /// 对象——C# record 不可变，事件快照经 <see cref="Snapshot"/> 产出。
    /// </summary>
    public sealed class MutableAssistantMessage
    {
        public List<ContentBlock> Content { get; } = [];

        /// <summary>流处理中为 <see cref="StopReason.Pending"/>，终止事件后更新。</summary>
        public StopReason StopReason { get; set; } = StopReason.Pending;

        public string? ErrorMessage { get; set; }

        public Usage? Usage { get; set; }

        public string? Model { get; }

        public string? Api { get; }

        public string? Provider { get; }

        public long Timestamp { get; }

        public string? ResponseId { get; set; }

        public string? RawStopReason { get; set; }

        /// <summary>模型是否自然说完（codex end_turn）。对应 TS <c>output.endTurn</c>。</summary>
        public bool? EndTurn { get; set; }

        /// <summary>流处理诊断条目（传输降级等）。对应 TS <c>output.diagnostics</c>。</summary>
        public List<Pi.Ai.Utils.AssistantMessageDiagnostic> Diagnostics { get; } = [];

        /// <summary>provider 侧思考档位（pi-messages 回传）。对应 TS <c>providerThinkingLevel</c>。</summary>
        public string? ProviderThinkingLevel { get; set; }

        public MutableAssistantMessage(string model, string api, string provider)
        {
            Model = model;
            Api = api;
            Provider = provider;
            Timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        }

        public AssistantMessage Snapshot()
            => new(Content.ToList(), StopReason, ErrorMessage, Usage, Model, Api, Provider, null, Timestamp)
            {
                RawStopReason = RawStopReason,
                ResponseId = ResponseId,
                EndTurn = EndTurn,
                ProviderThinkingLevel = ProviderThinkingLevel,
                Diagnostics = Diagnostics.Count > 0 ? [.. Diagnostics] : null,
            };
    }

    /// <summary>单个 output item 的流式草稿。</summary>
    private sealed class Slot
    {
        public string Kind = "";            // thinking | text | toolCall
        public int ContentIndex;
        public StringBuilder Text = new();  // thinking/text 的累积文本
        public string? Signature;           // reasoning 项 wire JSON / 文本 TextSignatureV1
        public bool Finalized;

        // toolCall 专用
        public string? CallId;
        public string? ItemId;
        public string? ToolName;
        public StringBuilder Arguments = new();
        public string? Namespace;
        public string? CustomInputProperty;                  // custom tool call 的输入属性
        public GrammarToolInputJsonBuffer? CustomInputBuffer; // custom tool call 的流式 JSON 缓冲
        public bool ArgsFinalized;
    }

    private sealed class StreamState
    {
        public readonly MutableAssistantMessage Owner;
        public readonly Dictionary<int, Slot> Slots = new();          // output_index → 活跃槽
        public readonly List<Slot> History = [];                       // 全部槽（含已终结，保序）
        public readonly Dictionary<string, Slot> ReasoningById = new(); // reasoning id → slot
        public int NextContentIndex;

        public StreamState(MutableAssistantMessage owner) => Owner = owner;

        public AssistantMessage Snapshot() => Owner.Snapshot();

        /// <summary>按 ContentIndex 顺序重建内容块列表（未终结的槽携带流式中间值）。</summary>
        public AssistantMessage Rebuild()
        {
            var blocks = new List<ContentBlock>();
            foreach (var slot in History.OrderBy(slot => slot.ContentIndex))
            {
                switch (slot.Kind)
                {
                    case "thinking":
                        blocks.Add(new ThinkingContent(slot.Text.ToString()) { Signature = slot.Signature });
                        break;
                    case "text":
                        blocks.Add(new TextContent(slot.Text.ToString()) { TextSignature = slot.Signature });
                        break;
                    case "toolCall":
                    {
                        object? arguments;
                        var raw = slot.Arguments.ToString();
                        if (slot.CustomInputProperty is not null)
                        {
                            arguments = new JsonObject { [slot.CustomInputProperty] = raw };
                        }
                        else
                        {
                            try { arguments = JsonNode.Parse(raw.Length > 0 ? raw : "{}"); }
                            catch { arguments = raw; }
                        }
                        var itemId = slot.ItemId;
                        blocks.Add(new ToolCallContent(
                            itemId is null ? slot.CallId ?? "" : $"{slot.CallId}|{itemId}",
                            slot.ToolName ?? "",
                            arguments)
                        {
                            Namespace = slot.Namespace,
                        });
                        break;
                    }
                }
            }
            // 写回可变快照（TS 的 output.content 是持续累积的数组）。
            Owner.Content.Clear();
            Owner.Content.AddRange(blocks);
            return Owner.Snapshot();
        }
    }

    /// <summary>StopReason 映射。对应 TS <c>mapStopReason</c>。</summary>
    private static (StopReason StopReason, string? ErrorMessage) MapStopReason(string? status, string? incompleteReason)
    {
        if (status is null) return (StopReason.Stop, null);
        return status switch
        {
            "completed" => (StopReason.Stop, null),
            "incomplete" => incompleteReason == "max_output_tokens"
                ? (StopReason.Length, null)
                : (StopReason.Error,
                    incompleteReason is not null
                        ? $"Response incomplete: {incompleteReason}"
                        : "Response incomplete without a provider reason"),
            "failed" or "cancelled" => (StopReason.Error, null),
            // 这两个状态很怪，但按完成处理。
            "in_progress" or "queued" => (StopReason.Stop, null),
            _ => throw new InvalidOperationException($"Unhandled stop reason: {status}"),
        };
    }

    /// <summary>
    /// 处理 Responses SSE 事件流，把事件折进 <paramref name="output"/> 并向
    /// <paramref name="stream"/> 推送助手事件。对应 TS <c>processResponsesStream</c>。
    /// </summary>
    public static async Task ProcessResponsesStream(
        IAsyncEnumerable<AiSseEvent> events,
        MutableAssistantMessage output,
        AssistantMessageEventStream stream,
        ModelSpec model,
        ResponsesStreamOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var sawTerminalResponseEvent = false;
        var state = new StreamState(output);

        Slot? CreateSlot(int outputIndex, JsonObject item)
        {
            var type = item.Str("type");
            if (type == "reasoning")
            {
                var slot = new Slot { Kind = "thinking", ContentIndex = state.NextContentIndex++ };
                state.Slots[outputIndex] = slot;
                state.History.Add(slot);
                state.Rebuild();
                stream.Push(new AssistantMessageEvent.ThinkingStart(slot.ContentIndex, state.Snapshot()));
                return slot;
            }
            if (type == "message")
            {
                if (item.Str("phase") == "final_answer") state.Owner.StopReason = StopReason.Stop;
                var slot = new Slot { Kind = "text", ContentIndex = state.NextContentIndex++ };
                state.Slots[outputIndex] = slot;
                state.History.Add(slot);
                state.Rebuild();
                stream.Push(new AssistantMessageEvent.TextStart(slot.ContentIndex, state.Snapshot()));
                return slot;
            }
            if (type == "function_call")
            {
                var slot = new Slot
                {
                    Kind = "toolCall",
                    ContentIndex = state.NextContentIndex++,
                    CallId = item.Str("call_id"),
                    ItemId = item.Str("id"),
                    ToolName = item.Str("name") ?? "",
                    Namespace = item.Str("namespace"),
                    Arguments = new StringBuilder(item.Str("arguments") ?? ""),
                };
                state.Slots[outputIndex] = slot;
                state.History.Add(slot);
                state.Rebuild();
                stream.Push(new AssistantMessageEvent.ToolCallStart(slot.ContentIndex, state.Snapshot()));
                return slot;
            }
            if (type == "custom_tool_call")
            {
                var inputProperty =
                    options?.GrammarToolInputProperties is { } properties
                    && properties.TryGetValue(item.Str("name") ?? "", out var property)
                        ? property
                        : "input";
                var slot = new Slot
                {
                    Kind = "toolCall",
                    ContentIndex = state.NextContentIndex++,
                    CallId = item.Str("call_id"),
                    ItemId = item.Str("id"),
                    ToolName = item.Str("name") ?? "",
                    Namespace = item.Str("namespace"),
                    Arguments = new StringBuilder(item.Str("input") ?? ""),
                    CustomInputProperty = inputProperty,
                    CustomInputBuffer = new GrammarToolInputJsonBuffer(),
                };
                state.Slots[outputIndex] = slot;
                state.History.Add(slot);
                state.Rebuild();
                stream.Push(new AssistantMessageEvent.ToolCallStart(slot.ContentIndex, state.Snapshot()));
                return slot;
            }
            return null;
        }

        // Azure 可能只在终止响应的 output 里给 reasoning.encrypted_content——用终止
        // 响应回填持久化签名，保持 store:false 的多轮重放无状态。（pi#6409）
        void BackfillReasoningSignatures(JsonArray responseOutput)
        {
            foreach (var node in responseOutput)
            {
                if (node is not JsonObject item) continue;
                if (item.Str("type") != "reasoning" || item.Str("encrypted_content") is not { } encrypted) continue;
                if (item.Str("id") is not { } id) continue;
                if (!state.ReasoningById.TryGetValue(id, out var slot) || slot.Signature is null) continue;

                var storedItem = JsonNode.Parse(slot.Signature) as JsonObject;
                if (storedItem?.Str("encrypted_content") is not null) continue;
                storedItem!["encrypted_content"] = encrypted;
                slot.Signature = storedItem.ToJsonString();
                state.Rebuild();
            }
        }

        void FinalizeResponse(JsonObject response)
        {
            sawTerminalResponseEvent = true;
            BackfillReasoningSignatures(response["output"] as JsonArray ?? []);
            if (response.Str("id") is { } responseId)
            {
                state.Owner.ResponseId = responseId;
            }
            if (response.Obj("usage") is { } usage)
            {
                var inputDetails = usage.Obj("input_tokens_details");
                var cachedTokens = (long)(inputDetails?.Num("cached_tokens") ?? 0);
                var cacheWriteTokens = (long)(inputDetails?.Num("cache_write_tokens") ?? 0);
                state.Owner.Usage = new Usage(
                    // OpenAI 的 input_tokens 含缓存读/写，两者都要扣掉。
                    Input: Math.Max(0, (long)(usage.Num("input_tokens") ?? 0) - cachedTokens - cacheWriteTokens),
                    Output: (long)(usage.Num("output_tokens") ?? 0),
                    CacheRead: cachedTokens,
                    CacheWrite: cacheWriteTokens,
                    Reasoning: (long)(usage.Obj("output_tokens_details")?.Num("reasoning_tokens") ?? 0));
            }
            if (state.Owner.Usage is { } stats)
            {
                stats = ModelOperations.CalculateCost(model, stats);
                if (options?.ApplyServiceTierPricing is { } applyPricing)
                {
                    var responseTier = response.Str("service_tier");
                    var serviceTier = options.ResolveServiceTier is { } resolveTier
                        ? resolveTier(responseTier, options.ServiceTier)
                        : responseTier ?? options.ServiceTier;
                    stats = applyPricing(stats, serviceTier);
                }
                state.Owner.Usage = stats;
            }
            // 状态 → 停止原因。不完整响应保留 provider 具体原因，让截断与内容过滤可区分。
            var status = response.Str("status");
            var incompleteReason = response.Obj("incomplete_details")?.Str("reason");
            state.Owner.RawStopReason = incompleteReason is not null ? $"{status}.{incompleteReason}" : status;
            var mappedStop = MapStopReason(status, incompleteReason);
            state.Owner.StopReason = mappedStop.StopReason;
            state.Owner.ErrorMessage = mappedStop.ErrorMessage;
            state.Rebuild(); // 先重建 Content，工具调用存在性检查依赖它
            if (state.Owner.Content.Any(block => block is ToolCallContent) && state.Owner.StopReason == StopReason.Stop)
            {
                state.Owner.StopReason = StopReason.ToolUse;
            }
            state.Rebuild();
        }

        await foreach (var sseEvent in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (JsonNode.Parse(sseEvent.Data) is not JsonObject @event) continue;
            if (options?.OnProviderStreamEvent is { } onProviderStreamEvent)
            {
                await onProviderStreamEvent(@event, model).ConfigureAwait(false);
            }
            var outputIndex = (int)(@event.Num("output_index") ?? 0);
            switch (@event.Str("type"))
            {
                case "response.created":
                    state.Owner.ResponseId = @event.Obj("response")?.Str("id");
                    break;
                case "response.output_item.added":
                    CreateSlot(outputIndex, @event.Obj("item") ?? []);
                    break;
                case "response.reasoning_summary_text.delta":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "thinking") break;
                    var delta = @event.Str("delta") ?? "";
                    slot.Text.Append(delta);
                    state.Rebuild();
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(slot.ContentIndex, delta, 0, null, state.Snapshot()));
                    break;
                }
                case "response.reasoning_summary_part.done":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "thinking") break;
                    slot.Text.Append("\n\n");
                    state.Rebuild();
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(slot.ContentIndex, "\n\n", 0, null, state.Snapshot()));
                    break;
                }
                case "response.reasoning_text.delta":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "thinking") break;
                    var delta = @event.Str("delta") ?? "";
                    slot.Text.Append(delta);
                    state.Rebuild();
                    stream.Push(new AssistantMessageEvent.ThinkingDelta(slot.ContentIndex, delta, 0, null, state.Snapshot()));
                    break;
                }
                case "response.output_text.delta":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "text") break;
                    var delta = @event.Str("delta") ?? "";
                    slot.Text.Append(delta);
                    state.Rebuild();
                    stream.Push(new AssistantMessageEvent.TextDelta(slot.ContentIndex, delta, 0, state.Snapshot()));
                    break;
                }
                case "response.refusal.delta":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot) || slot.Kind != "text") break;
                    var delta = @event.Str("delta") ?? "";
                    slot.Text.Append(delta);
                    state.Rebuild();
                    stream.Push(new AssistantMessageEvent.TextDelta(slot.ContentIndex, delta, 0, state.Snapshot()));
                    break;
                }
                case "response.function_call_arguments.delta":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != "toolCall" || slot.ArgsFinalized) break;
                    var delta = @event.Str("delta") ?? "";
                    slot.Arguments.Append(delta);
                    state.Rebuild();
                    stream.Push(new AssistantMessageEvent.ToolCallDelta(slot.ContentIndex, slot.ContentIndex, delta, state.Snapshot()));
                    break;
                }
                case "response.function_call_arguments.done":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != "toolCall" || slot.ArgsFinalized) break;
                    var previousPartialJson = slot.Arguments.ToString();
                    var arguments = @event.Str("arguments") ?? "";
                    slot.Arguments.Clear();
                    slot.Arguments.Append(arguments);
                    state.Rebuild();

                    if (arguments.StartsWith(previousPartialJson, StringComparison.Ordinal))
                    {
                        var delta = arguments[previousPartialJson.Length..];
                        if (delta.Length > 0)
                        {
                            stream.Push(new AssistantMessageEvent.ToolCallDelta(slot.ContentIndex, slot.ContentIndex, delta, state.Snapshot()));
                        }
                    }
                    break;
                }
                case "response.custom_tool_call_input.delta":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != "toolCall" || slot.CustomInputBuffer is null) break;
                    var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(
                        slot.CustomInputBuffer, slot.CustomInputProperty!,
                        slot.Arguments.ToString() + (@event.Str("delta") ?? ""), close: false);
                    slot.Arguments.Clear();
                    slot.Arguments.Append(slot.CustomInputBuffer.Input);
                    state.Rebuild();
                    if (delta is not null)
                    {
                        stream.Push(new AssistantMessageEvent.ToolCallDelta(slot.ContentIndex, slot.ContentIndex, delta, state.Snapshot()));
                    }
                    break;
                }
                case "response.custom_tool_call_input.done":
                {
                    if (!state.Slots.TryGetValue(outputIndex, out var slot)
                        || slot.Kind != "toolCall" || slot.CustomInputBuffer is null) break;
                    var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(
                        slot.CustomInputBuffer, slot.CustomInputProperty!,
                        @event.Str("input") ?? slot.Arguments.ToString(), close: true);
                    slot.Arguments.Clear();
                    slot.Arguments.Append(slot.CustomInputBuffer.Input);
                    state.Rebuild();
                    if (delta is not null)
                    {
                        stream.Push(new AssistantMessageEvent.ToolCallDelta(slot.ContentIndex, slot.ContentIndex, delta, state.Snapshot()));
                    }
                    break;
                }
                case "response.output_item.done":
                {
                    var item = @event.Obj("item") ?? [];
                    if (item.Str("type") == "message" && item.Str("phase") == "final_answer")
                    {
                        state.Owner.StopReason = StopReason.Stop;
                    }
                    if (!state.Slots.TryGetValue(outputIndex, out var slot))
                    {
                        CreateSlot(outputIndex, item);
                        slot = state.Slots[outputIndex];
                    }
                    state.Rebuild();

                    var itemType = item.Str("type");
                    if (itemType == "reasoning" && slot.Kind == "thinking")
                    {
                        var summaryText = string.Join("\n\n", ((item["summary"] as JsonArray) ?? [])
                            .OfType<JsonObject>()
                            .Select(summary => summary.Str("text") ?? ""));
                        var contentText = string.Join("\n\n", ((item["content"] as JsonArray) ?? [])
                            .OfType<JsonObject>()
                            .Select(content => content.Str("text") ?? ""));
                        // TS：summaryText || contentText || 保留已累积的 delta 文本。
                        if (summaryText.Length > 0 || contentText.Length > 0)
                        {
                            slot.Text.Clear();
                            slot.Text.Append(summaryText.Length > 0 ? summaryText : contentText);
                        }
                        slot.Signature = item.ToJsonString();
                        slot.Finalized = true;
                        if (item.Str("id") is { } itemId)
                        {
                            state.ReasoningById[itemId] = slot;
                        }
                        state.Rebuild();
                        stream.Push(new AssistantMessageEvent.ThinkingEnd(slot.ContentIndex, slot.Text.ToString(), state.Snapshot()));
                        state.Slots.Remove(outputIndex);
                    }
                    else if (itemType == "message" && slot.Kind == "text")
                    {
                        slot.Text.Clear();
                        slot.Text.Append(string.Concat(((item["content"] as JsonArray) ?? [])
                            .OfType<JsonObject>()
                            .Select(content => content.Str("type") == "output_text" ? content.Str("text") ?? "" : content.Str("refusal") ?? "")));
                        slot.Signature = OpenAiResponsesShared.EncodeTextSignatureV1(
                            item.Str("id") ?? "", item.Str("phase"));
                        slot.Finalized = true;
                        state.Rebuild();
                        stream.Push(new AssistantMessageEvent.TextEnd(slot.ContentIndex, slot.Text.ToString(), state.Snapshot()));
                        state.Slots.Remove(outputIndex);
                    }
                    else if (itemType == "function_call" && slot.Kind == "toolCall" && !slot.ArgsFinalized)
                    {
                        var finalArguments = item.Str("arguments")
                            ?? (slot.Arguments.Length > 0 ? slot.Arguments.ToString() : "{}");
                        slot.Arguments.Clear();
                        slot.Arguments.Append(finalArguments);
                        if (item.Str("namespace") is { } ns) slot.Namespace = ns;
                        // 就地终结并剥离草稿缓冲，重放只携带解析后的参数。
                        slot.ArgsFinalized = true;
                        state.Rebuild();
                        stream.Push(new AssistantMessageEvent.ToolCallEnd(slot.ContentIndex, CurrentToolCall(slot), state.Snapshot()));
                        state.Slots.Remove(outputIndex);
                    }
                    else if (itemType == "custom_tool_call" && slot.Kind == "toolCall" && slot.CustomInputBuffer is not null)
                    {
                        var delta = ConstrainedSampling.AppendGrammarToolInputJsonDelta(
                            slot.CustomInputBuffer, slot.CustomInputProperty!,
                            item.Str("input") ?? slot.Arguments.ToString(), close: true);
                        slot.Arguments.Clear();
                        slot.Arguments.Append(slot.CustomInputBuffer.Input);
                        if (item.Str("namespace") is { } customNs) slot.Namespace = customNs;
                        slot.CustomInputBuffer = null;
                        slot.CustomInputProperty = null;
                        slot.ArgsFinalized = true;
                        state.Rebuild();
                        if (delta is not null)
                        {
                            stream.Push(new AssistantMessageEvent.ToolCallDelta(slot.ContentIndex, slot.ContentIndex, delta, state.Snapshot()));
                        }
                        stream.Push(new AssistantMessageEvent.ToolCallEnd(slot.ContentIndex, CurrentToolCall(slot), state.Snapshot()));
                        state.Slots.Remove(outputIndex);
                    }
                    break;
                }
                case "response.completed" or "response.incomplete":
                    FinalizeResponse(@event.Obj("response") ?? []);
                    break;
                case "error":
                    // TS 模板串恒非空，"Unknown error" 分支不可达；保持等价直接抛出。
                    throw new InvalidOperationException(
                        $"Error Code {@event.Str("code")}: {@event.Str("message")}");
                case "response.failed":
                {
                    sawTerminalResponseEvent = true;
                    var failedResponse = @event.Obj("response");
                    state.Owner.RawStopReason = failedResponse?.Str("status");
                    var error = failedResponse?.Obj("error");
                    var details = failedResponse?.Obj("incomplete_details");
                    var message = error is not null
                        ? $"{error.Str("code") ?? "unknown"}: {error.Str("message") ?? "no message"}"
                        : details?.Str("reason") is { } reason
                            ? $"incomplete: {reason}"
                            : "Unknown error (no error details in response)";
                    throw new InvalidOperationException(message);
                }
            }
        }
        if (!sawTerminalResponseEvent)
        {
            throw new InvalidOperationException("OpenAI Responses stream ended before a terminal response event");
        }
        // agent 会在最终消息上执行每个工具调用。output_item.done 未到达的调用
        // 拒绝交付：参数可能被截断或错乱（如不合规服务器省略 output_index）。
        if (state.Owner.StopReason == StopReason.ToolUse && state.Slots.Count > 0)
        {
            var unfinished = state.Slots.Values.FirstOrDefault(slot => slot.Kind == "toolCall");
            if (unfinished is not null)
            {
                throw new InvalidOperationException(
                    $"OpenAI Responses stream completed with an unfinished tool call: {unfinished.ToolName} ({unfinished.CallId})");
            }
        }
    }

    /// <summary>草稿槽的当前工具调用块。</summary>
    private static ToolCallContent CurrentToolCall(Slot slot)
    {
        object? arguments;
        var raw = slot.Arguments.ToString();
        try { arguments = JsonNode.Parse(raw.Length > 0 ? raw : "{}"); }
        catch { arguments = raw; }
        var itemId = slot.ItemId;
        return new ToolCallContent(
            itemId is null ? slot.CallId ?? "" : $"{slot.CallId}|{itemId}",
            slot.ToolName ?? "",
            arguments)
        {
            Namespace = slot.Namespace,
        };
    }
}
