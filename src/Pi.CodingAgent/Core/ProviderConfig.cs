using System.Text.Json.Nodes;
using Pi.Ai.Auth;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace Pi.CodingAgent.Core;

/// <summary>
/// 扩展用 <c>pi.registerProvider()</c> 声明的一个模型。对应 TS <c>ProviderModelConfig</c>
/// 判别联合（<c>ProviderModelConfigBase</c> + chat/image/classifier 三态）。
/// </summary>
/// <remarks>
/// TS 的 <c>type</c> 判别字段在 C# 落成抽象属性 <see cref="Type"/>：无 <c>type</c> 即 chat，
/// 因此 <see cref="ProviderChatModelConfig"/> 不重复声明它。
/// <para>与 <see cref="ModelSpec"/> 的已知差异：TS <c>cost</c> 是 <c>ModelCost</c>（含 <c>tiers</c>），
/// 图片模型的 <c>output</c> 与 chat 模型的 <c>promptCache</c> 在 <see cref="ModelSpec"/> 里没有具名字段——
/// 三者都沿用本移植既有的处理（<c>tiers</c> 丢弃，<c>output</c>/<c>promptCache</c> 经
/// <see cref="ModelSpec.Extra"/> 原样保留）。</para>
/// </remarks>
public abstract record ProviderModelConfig
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>协议名。chat 缺省时回落 provider 级 <see cref="ProviderConfigInput.Api"/>。</summary>
    public string? Api { get; init; }

    public string? BaseUrl { get; init; }

    /// <summary>输入模态（text/image）。TS 为必填。</summary>
    public required IReadOnlyList<string> Input { get; init; }

    public ModelInputLimits? InputLimits { get; init; }

    /// <summary>成本费率。TS 为必填（<c>AnyModel["cost"]</c>）。</summary>
    public required ModelCostRates Cost { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>模型类别。对应 TS 的 <c>type</c> 判别字段（缺省 chat）。</summary>
    public abstract ModelType Type { get; }
}

/// <summary>chat 模型声明。对应 TS <c>ProviderChatModelConfig</c>。</summary>
public sealed record ProviderChatModelConfig : ProviderModelConfig
{
    public override ModelType Type => ModelType.Chat;

    public required bool Reasoning { get; init; }

    public ThinkingLevelMap? ThinkingLevelMap { get; init; }

    /// <summary>提示缓存保留档位（short/long → 秒）。对应 TS <c>ModelPromptCache</c>。</summary>
    public JsonObject? PromptCache { get; init; }

    public required double ContextWindow { get; init; }

    public required double MaxTokens { get; init; }

    public JsonObject? SamplingParams { get; init; }

    public IReadOnlyDictionary<string, JsonObject>? SamplingParamsByThinkingLevel { get; init; }

    public JsonObject? Compat { get; init; }
}

/// <summary>图片模型声明。对应 TS <c>ProviderImageModelConfig</c>。</summary>
public sealed record ProviderImageModelConfig : ProviderModelConfig
{
    public override ModelType Type => ModelType.Image;

    /// <summary>输出模态；恒含 image，含 text 表示也能返回文本块。</summary>
    public required IReadOnlyList<string> Output { get; init; }
}

/// <summary>分类模型声明。对应 TS <c>ProviderClassifierModelConfig</c>（无 maxTokens）。</summary>
public sealed record ProviderClassifierModelConfig : ProviderModelConfig
{
    public override ModelType Type => ModelType.Classifier;

    public required double ContextWindow { get; init; }
}

/// <summary>
/// 扩展 <c>registerProvider</c> API 的输入。对应 TS <c>ProviderConfigInput</c>。
/// </summary>
/// <remarks>
/// TS 的 <c>streamSimple</c> 与 <c>images</c>/<c>classifiers</c> 是可选成员，其「存在性」决定
/// 组合后的 provider 是否导出对应能力；C# 侧以 null 表达「缺失」。
/// </remarks>
public sealed record ProviderConfigInput
{
    public string? Name { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? Api { get; init; }

    /// <summary>扩展自带的简单流式实现；仅当 <c>model.api == Api</c> 时分流到它。</summary>
    public Func<ModelSpec, TranscriptContext, IReadOnlyDictionary<string, object?>?, IAssistantMessageEventStream>?
        StreamSimple { get; init; }

    /// <summary>按 <c>model.api</c> 分派的图片生成实现。</summary>
    public IReadOnlyDictionary<string, ProviderImages>? Images { get; init; }

    /// <summary>按 <c>model.api</c> 分派的分类器实现。</summary>
    public IReadOnlyDictionary<string, ProviderClassifier>? Classifiers { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>为真时把已解析的 apiKey 写成 <c>Authorization: Bearer …</c>。</summary>
    public bool? AuthHeader { get; init; }

    public ExtensionOAuthConfig? OAuth { get; init; }

    public IReadOnlyList<ProviderModelConfig>? Models { get; init; }

    /// <summary>动态模型刷新钩子；返回值替换 <see cref="Models"/>。</summary>
    public Func<RefreshModelsContext, Task<IReadOnlyList<ProviderModelConfig>>>? RefreshModels { get; init; }
}

/// <summary>
/// 扩展注册的 OAuth 流程。对应 TS <c>ExtensionOAuthConfig</c>。
/// </summary>
/// <remarks>
/// TS 的 <c>login(callbacks: OAuthLoginCallbacks)</c> 接收一套 notify/prompt 风格的回调，
/// 由 <c>adaptOAuth</c> 翻译成规范的 <c>AuthInteraction</c>。C# 侧扩展面向的交互面与规范交互面
/// 是同一个类型 <see cref="ProviderAuthInteraction"/>（<see cref="IAuthInteraction"/> + Signal），
/// 因此这里直接接收它——<c>adaptOAuth</c> 退化为恒等转发（差异 C50）。
/// </remarks>
public sealed record ExtensionOAuthConfig
{
    public required string Name { get; init; }

    /// <summary>该认证方式是否由 provider 订阅支撑。</summary>
    public bool? IsSubscription { get; init; }

    /// <summary>已废弃：保留以兼容扩展源码；规范认证流忽略。</summary>
    public bool? UsesCallbackServer { get; init; }

    public required Func<ProviderAuthInteraction, LoginOptions?, CancellationToken, Task<Credential.OAuth>> Login
    {
        get;
        init;
    }

    public required Func<Credential.OAuth, CancellationToken, Task<Credential.OAuth>> RefreshToken { get; init; }

    public required Func<Credential.OAuth, string> GetApiKey { get; init; }

    /// <summary>chat-only 的模型改写钩子，在 OAuth 凭据可用后应用。</summary>
    public Func<IReadOnlyList<ModelSpec>, Credential.OAuth, IReadOnlyList<ModelSpec>>? ModifyModels { get; init; }
}

/// <summary>
/// 已配置请求认证的状态。对应 TS <c>AuthStatus</c>。
/// </summary>
public sealed record AuthStatus
{
    public required bool Configured { get; init; }

    /// <summary>取值见 <see cref="AuthStatusSource"/>。</summary>
    public string? Source { get; init; }

    public string? Label { get; init; }
}

/// <summary>TS <c>AuthStatus["source"]</c> 的字符串字面量。</summary>
/// <remarks>
/// 本模块只产出 <see cref="Environment"/>、<see cref="ModelsJsonCommand"/>、<see cref="Fallback"/>、
/// <see cref="ModelsJsonKey"/>；<see cref="Stored"/> 与 <see cref="Runtime"/> 由存储/运行时层产出，
/// 一并保留以覆盖 TS 联合的完整取值。
/// </remarks>
public static class AuthStatusSource
{
    public const string Stored = "stored";

    public const string Runtime = "runtime";

    public const string Environment = "environment";

    public const string Fallback = "fallback";

    public const string ModelsJsonKey = "models_json_key";

    public const string ModelsJsonCommand = "models_json_command";
}
