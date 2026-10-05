using System.Text.Json.Nodes;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.Ai.Utils;

namespace Pi.Ai.Api;

/// <summary>
/// Google Generative AI 与 Google Vertex 共享工具。对应 TS <c>api/google-shared.ts</c>：
/// 思考档位映射（Gemini 3 离散 thinkingLevel vs token budget）、thought 签名保留与
/// 校验（base64）、工具调用 ID 要求、Gemini Content[] 转换、函数声明转换与
/// functionCallingConfig 模式解析、finishReason 映射。
/// </summary>
public static class GoogleShared
{
    /// <summary>Gemini 3 的离散思考档位（wire 值）。对应 TS <c>GoogleApiThinkingLevel</c>。</summary>
    public static readonly IReadOnlyList<string> GoogleApiThinkingLevels =
        ["THINKING_LEVEL_UNSPECIFIED", "MINIMAL", "LOW", "MEDIUM", "HIGH"];

    /// <summary>
    /// 解析 pi 档位或模型专属 Google 映射为标准 Google 档位。对应 TS <c>resolveGoogleThinkingLevel</c>。
    /// </summary>
    public static string ResolveGoogleThinkingLevel(ModelSpec model, string level)
    {
        var mapped = model.ThinkingLevelMap?[level];
        var resolvedLevel = mapped is not null ? mapped.ToLowerInvariant() : level;
        return resolvedLevel switch
        {
            "minimal" or "low" or "medium" or "high" => resolvedLevel,
            _ => throw new InvalidOperationException(
                $"Unsupported Google thinking level mapping for {model.Provider}/{model.Id}: {level} -> {mapped ?? "null"}"),
        };
    }

    /// <summary>
    /// 模型是否使用 Gemini 3 的离散 <c>thinkingLevel</c> 控制（而非 token budget）。
    /// 对应 TS <c>usesGoogleThinkingLevel</c>。
    /// </summary>
    public static bool UsesGoogleThinkingLevel(ModelSpec model)
    {
        var id = model.Id.ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(id, @"gemini-3(?:\.\d+)?-(?:pro|flash)")
            || id is "gemini-flash-latest" or "gemini-flash-lite-latest"
            // 托管 Gemma 4 的两种命名：gemma-4-* 与 gemma4-*。
            || System.Text.RegularExpressions.Regex.IsMatch(id, @"gemma-?4");
    }

    /// <summary>pi 档位 → Google wire 档位。对应 TS <c>toGoogleThinkingLevel</c>。</summary>
    public static string ToGoogleThinkingLevel(string level) => level switch
    {
        "minimal" => "MINIMAL",
        "low" => "LOW",
        "medium" => "MEDIUM",
        "high" => "HIGH",
        _ => throw new ArgumentException($"unknown level: {level}"),
    };

    /// <summary>禁用思考的 ThinkingConfig（Gemini 3 走 fallback 档位，其余 thinkingBudget:0）。对应 TS <c>getDisabledGoogleThinkingConfig</c>。</summary>
    public static JsonObject GetDisabledGoogleThinkingConfig(ModelSpec model)
    {
        if (!UsesGoogleThinkingLevel(model)) return new JsonObject { ["thinkingBudget"] = 0 };

        var fallback = ThinkingLevels.Clamp(model, "off");
        if (fallback == "off") return new JsonObject { ["thinkingBudget"] = 0 };

        var apiLevel = ToGoogleThinkingLevel(ResolveGoogleThinkingLevel(model, fallback));
        return new JsonObject { ["thinkingLevel"] = apiLevel };
    }

    /// <summary>
    /// part 是否为思考内容。协议要点：thought:true 是决定性标记；
    /// thoughtSignature 可出现在任意 part 上（不代表该 part 是思考内容）。
    /// 对应 TS <c>isThinkingPart</c>。
    /// </summary>
    public static bool IsThinkingPart(JsonObject part)
        => part["thought"] is System.Text.Json.Nodes.JsonValue { } flag
            && flag.TryGetValue<bool>(out var thought) && thought;

    /// <summary>流式期间保留 thought 签名（后续 delta 可能省略）。对应 TS <c>retainThoughtSignature</c>。</summary>
    public static string? RetainThoughtSignature(string? existing, string? incoming)
        => !string.IsNullOrEmpty(incoming) ? incoming : existing;

    // thought 签名必须是 base64（TYPE_BYTES）。
    private static readonly System.Text.RegularExpressions.Regex Base64SignaturePattern =
        new("^[A-Za-z0-9+/]+={0,2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidThoughtSignature(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        if (signature.Length % 4 != 0) return false;
        return Base64SignaturePattern.IsMatch(signature);
    }

    /// <summary>仅保留同 provider/model 且 base64 合法的签名。对应 TS <c>resolveThoughtSignature</c>。</summary>
    public static string? ResolveThoughtSignature(bool isSameProviderAndModel, string? signature)
        => isSameProviderAndModel && IsValidThoughtSignature(signature) ? signature : null;

    /// <summary>经 Google API 的哪些模型要求 functionCall/functionResponse 显式 ID。对应 TS <c>requiresToolCallId</c>。</summary>
    public static bool RequiresToolCallId(string modelId)
    {
        var geminiMajorVersion = GetGeminiMajorVersion(modelId);
        return modelId.StartsWith("claude-", StringComparison.Ordinal)
            || modelId.StartsWith("gpt-oss-", StringComparison.Ordinal)
            || (geminiMajorVersion is { } version && version >= 3);
    }

    /// <summary>模型 id 的 Gemini 主版本号（无则 null）。对应 TS <c>getGeminiMajorVersion</c>。</summary>
    public static int? GetGeminiMajorVersion(string modelId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            modelId.ToLowerInvariant(), @"^gemini(?:-live)?-(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>是否支持多模态 functionResponse（Gemini 3+；非 Gemini 模型默认支持）。对应 TS <c>supportsMultimodalFunctionResponse</c>。</summary>
    public static bool SupportsMultimodalFunctionResponse(string modelId)
    {
        if (GetGeminiMajorVersion(modelId) is { } version) return version >= 3;
        return true;
    }

    // =============================================================================
    // 消息转换
    // =============================================================================

    /// <summary>
    /// 内部消息 → Gemini Content[]。对应 TS <c>convertMessages</c>：
    /// 首个 system 作为 systemInstruction（调用方剥离）、thought 签名保留（仅同模型）、
    /// functionCall 可带 id（Gemini 3+/Claude/gpt-oss）、functionResponse 合并进单个
    /// user turn、Gemini &lt; 3 的图片走独立 user turn。
    /// </summary>
    public static (string? SystemInstruction, JsonArray Contents) ConvertMessages(
        ModelSpec model, TranscriptContext context)
    {
        var collapsed = Transcript.CollapseSystemMessages(context);
        var conversation = Transcript.WithoutInitialSystemMessage(collapsed.Messages);
        var contents = new JsonArray();

        // Gemini 要求 ^[a-zA-Z0-9_-]+$（最长 64）。
        string NormalizeToolCallId(string id)
        {
            if (!RequiresToolCallId(model.Id)) return id;
            var chars = id.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_').ToArray();
            return chars.Length > 64 ? new string(chars[..64]) : new string(chars);
        }

        var transformedMessages = TransformMessages.Transform(
            conversation, model, (id, _, _) => NormalizeToolCallId(id));

        foreach (var msg in transformedMessages)
        {
            switch (msg)
            {
                case UserMessage user:
                {
                    var parts = new JsonArray();
                    foreach (var item in user.Content)
                    {
                        if (item is TextContent text)
                        {
                            parts.Add(new JsonObject
                            {
                                ["text"] = SanitizeUnicode.SanitizeSurrogates(text.Text ?? ""),
                            });
                        }
                        else if (item is ImageContent image)
                        {
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = image.MimeType,
                                    ["data"] = image.Data,
                                },
                            });
                        }
                    }
                    if (parts.Count == 0) break;
                    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = parts });
                    break;
                }
                case AssistantMessage assistant:
                {
                    var parts = new JsonArray();
                    // 仅同 provider+model 保留 thinking 块，否则转纯文本。
                    var isSameProviderAndModel = assistant.Provider == model.Provider && assistant.Model == model.Id;

                    foreach (var block in assistant.Content)
                    {
                        switch (block)
                        {
                            case TextContent textBlock:
                            {
                                var thoughtSignature = ResolveThoughtSignature(
                                    isSameProviderAndModel, textBlock.TextSignature);
                                // 空 text 块通常丢弃——除非带签名：Gemini 可能把签名挂在
                                // 空文本 part 上并要求原样回传（丢弃会破坏推理链）。
                                if (string.IsNullOrWhiteSpace(textBlock.Text) && thoughtSignature is null) continue;
                                var part = new JsonObject
                                {
                                    ["text"] = SanitizeUnicode.SanitizeSurrogates(textBlock.Text ?? ""),
                                };
                                if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
                                parts.Add(part);
                                break;
                            }
                            case ThinkingContent thinkingBlock:
                            {
                                if (isSameProviderAndModel)
                                {
                                    var thinkingSignature = ResolveThoughtSignature(
                                        isSameProviderAndModel, thinkingBlock.Signature);
                                    if (string.IsNullOrWhiteSpace(thinkingBlock.Thinking) && thinkingSignature is null)
                                    {
                                        continue;
                                    }
                                    var part = new JsonObject
                                    {
                                        ["thought"] = true,
                                        ["text"] = SanitizeUnicode.SanitizeSurrogates(thinkingBlock.Thinking),
                                    };
                                    if (thinkingSignature is not null) part["thoughtSignature"] = thinkingSignature;
                                    parts.Add(part);
                                }
                                else
                                {
                                    // 跨模型：签名不可用，空块丢弃。
                                    if (string.IsNullOrWhiteSpace(thinkingBlock.Thinking)) continue;
                                    parts.Add(new JsonObject
                                    {
                                        ["text"] = SanitizeUnicode.SanitizeSurrogates(thinkingBlock.Thinking),
                                    });
                                }
                                break;
                            }
                            case ToolCallContent toolCall:
                            {
                                var thoughtSignature = ResolveThoughtSignature(
                                    isSameProviderAndModel, toolCall.ThoughtSignature);
                                var functionCall = new JsonObject
                                {
                                    ["name"] = toolCall.Name,
                                    ["args"] = System.Text.Json.JsonSerializer
                                        .Deserialize<JsonObject>(
                                            (toolCall.Arguments as System.Text.Json.Nodes.JsonNode)?.ToJsonString() ?? "{}")
                                        ?? [],
                                };
                                if (RequiresToolCallId(model.Id)) functionCall["id"] = toolCall.Id;
                                var part = new JsonObject { ["functionCall"] = functionCall };
                                if (thoughtSignature is not null) part["thoughtSignature"] = thoughtSignature;
                                parts.Add(part);
                                break;
                            }
                        }
                    }

                    if (parts.Count == 0) break;
                    contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
                    break;
                }
                case ToolResultMessage toolResult:
                {
                    var textResult = string.Join("\n", toolResult.Content
                        .OfType<TextContent>()
                        .Select(content => content.Text ?? ""));
                    var imageContent = model.Input.Contains("image")
                        ? toolResult.Content.OfType<ImageContent>().ToList()
                        : [];

                    var hasText = textResult.Length > 0;
                    var hasImages = imageContent.Count > 0;
                    var modelSupportsMultimodalFunctionResponse = SupportsMultimodalFunctionResponse(model.Id);

                    // 成功用 "output" 键，错误用 "error" 键（SDK 文档约定）。
                    var responseValue = hasText ? SanitizeUnicode.SanitizeSurrogates(textResult)
                        : hasImages ? "(see attached image)"
                        : "";

                    var imageParts = new JsonArray();
                    foreach (var imageBlock in imageContent)
                    {
                        imageParts.Add(new JsonObject
                        {
                            ["inlineData"] = new JsonObject
                            {
                                ["mimeType"] = imageBlock.MimeType,
                                ["data"] = imageBlock.Data,
                            },
                        });
                    }

                    var functionResponse = new JsonObject
                    {
                        ["name"] = toolResult.ToolName ?? "",
                        ["response"] = new JsonObject
                        {
                            [toolResult.IsError ? "error" : "output"] = responseValue,
                        },
                    };
                    if (hasImages && modelSupportsMultimodalFunctionResponse) functionResponse["parts"] = imageParts;
                    if (RequiresToolCallId(model.Id)) functionResponse["id"] = toolResult.ToolCallId;

                    var functionResponsePart = new JsonObject { ["functionResponse"] = functionResponse };

                    // Cloud Code Assist 要求全部 functionResponse 在单个 user turn：能合并就合并。
                    if (contents.Count > 0
                        && contents[contents.Count - 1] is JsonObject lastContent
                        && lastContent.Str("role") == "user"
                        && lastContent["parts"] is JsonArray lastParts
                        && lastParts.OfType<JsonObject>().Any(part => part.Has("functionResponse")))
                    {
                        lastParts.Add(functionResponsePart);
                    }
                    else
                    {
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(functionResponsePart) });
                    }

                    // Gemini < 3：图片放独立 user 消息。
                    if (hasImages && !modelSupportsMultimodalFunctionResponse)
                    {
                        var turnParts = new JsonArray(new JsonObject { ["text"] = "Tool result image:" });
                        foreach (var imagePart in imageParts) turnParts.Add(imagePart?.DeepClone());
                        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = turnParts });
                    }
                    break;
                }
            }
        }

        return (CollapsedSystemInstruction(context), contents);
    }

    /// <summary>折叠后的首个 system 提示（折叠语义与 TS collapseSystemMessages 一致）。</summary>
    private static string? CollapsedSystemInstruction(TranscriptContext context)
    {
        var initial = Transcript.GetInitialSystemMessage(context.Messages);
        return initial is null ? null : Text.GetSystemMessageText(initial);
    }

    // =============================================================================
    // 工具转换
    // =============================================================================

    private static readonly HashSet<string> JsonSchemaMetaDeclarations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "$anchor", "$dynamicAnchor", "$vocabulary", "$comment", "$defs", "definitions",
    };

    /// <summary>剥离 JSON Schema 元声明（OpenAPI 3.03 兼容）。对应 TS <c>sanitizeForOpenApi</c>。</summary>
    public static JsonNode? SanitizeForOpenApi(JsonNode? schema)
    {
        if (schema is not JsonObject obj) return schema?.DeepClone();
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (JsonSchemaMetaDeclarations.Contains(key)) continue;
            result[key] = SanitizeForOpenApi(value);
        }
        return result;
    }

    /// <summary>
    /// 工具 → Gemini 函数声明。缺省用 <c>parametersJsonSchema</c>（完整 JSON Schema）；
    /// <paramref name="useParameters"/> 为 true 时用旧 <c>parameters</c> 字段
    /// （OpenAPI 3.03，Cloud Code Assist + Claude 需要）。对应 TS <c>convertTools</c>。
    /// </summary>
    public static JsonArray? ConvertTools(
        IReadOnlyList<ToolDefinition> tools, bool useParameters = false, bool supportsStrictMode = true)
    {
        if (tools.Count == 0) return null;
        var declarations = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode);
            var parameters = ConstrainedSampling.GetJsonSchemaToolParameters(tool, strict);
            var declaration = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
            };
            if (useParameters)
            {
                declaration["parameters"] = SanitizeForOpenApi(parameters);
            }
            else
            {
                declaration["parametersJsonSchema"] = parameters?.DeepClone();
            }
            declarations.Add(declaration);
        }
        return new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
    }

    /// <summary>Gemini 3+ 在受验工具调用模式下要求必填参数。对应 TS <c>supportsGoogleStrictToolSampling</c>。</summary>
    public static bool SupportsGoogleStrictToolSampling(string modelId)
        => GetGeminiMajorVersion(modelId) is { } version && version >= 3;

    /// <summary>tool choice 字符串 → Gemini mode。对应 TS <c>mapToolChoice</c>。</summary>
    public static string MapToolChoice(string choice) => choice switch
    {
        "auto" => "AUTO",
        "none" => "NONE",
        "any" => "ANY",
        _ => "AUTO",
    };

    /// <summary>
    /// functionCallingConfig 模式解析：none/any 直映射；strict 采样要求 VALIDATED。
    /// 对应 TS <c>resolveGoogleFunctionCallingMode</c>。
    /// </summary>
    public static string? ResolveGoogleFunctionCallingMode(
        IReadOnlyList<ToolDefinition> tools, string? toolChoice, bool supportsStrictMode)
    {
        var useStrictMode = tools.Any(tool => ConstrainedSampling.ResolveJsonSchemaStrictSampling(tool, supportsStrictMode) == true);
        if (toolChoice is "none" or "any") return MapToolChoice(toolChoice);
        if (useStrictMode) return "VALIDATED";
        return toolChoice is not null ? MapToolChoice(toolChoice) : null;
    }

    // =============================================================================
    // 停止原因映射
    // =============================================================================

    private static readonly HashSet<string> ErrorFinishReasons = new(StringComparer.Ordinal)
    {
        "BLOCKLIST", "PROHIBITED_CONTENT", "SPII", "SAFETY", "IMAGE_SAFETY", "IMAGE_PROHIBITED_CONTENT",
        "IMAGE_RECITATION", "IMAGE_OTHER", "RECITATION", "FINISH_REASON_UNSPECIFIED", "OTHER", "LANGUAGE",
        "MALFORMED_FUNCTION_CALL", "UNEXPECTED_TOOL_CALL", "TOO_MANY_TOOL_CALLS", "NO_IMAGE",
    };

    /// <summary>Gemini finishReason → StopReason。对应 TS <c>mapStopReason</c>（字符串形态）。</summary>
    public static StopReason MapStopReason(string? reason)
        => reason switch
        {
            "STOP" => StopReason.Stop,
            "MAX_TOKENS" => StopReason.Length,
            _ when reason is not null && ErrorFinishReasons.Contains(reason) => StopReason.Error,
            _ => throw new InvalidOperationException($"Unhandled stop reason: {reason ?? "null"}"),
        };

    /// <summary>字符串 finishReason → StopReason（原始 API 响应用）。对应 TS <c>mapStopReasonString</c>。</summary>
    public static StopReason MapStopReasonString(string reason)
        => reason switch
        {
            "STOP" => StopReason.Stop,
            "MAX_TOKENS" => StopReason.Length,
            _ => StopReason.Error,
        };
}
