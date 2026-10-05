using Pi.Ai.Models;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pi.Ai.Types;

/// <summary>结构化分类器的停止原因。对应 TS <c>ClassifierStopReason</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClassifierStopReason>))]
public enum ClassifierStopReason
{
    Stop,
    Error,
    Aborted,
}

/// <summary>选择题：从带说明的选项中选一个。对应 TS <c>ClassifierChoiceQuestion</c>。</summary>
public sealed record ClassifierChoiceQuestion(
    string Question,
    IReadOnlyDictionary<string, string> Criteria) : ClassifierQuestion
{
    public override string Kind => "choice";

    public override string Instructions => Question;
}

/// <summary>评分题：在给定档位上打分。对应 TS <c>ClassifierScoreQuestion</c>。</summary>
public sealed record ClassifierScoreQuestion(
    string Question,
    IReadOnlyList<string> Criteria) : ClassifierQuestion
{
    public override string Kind => "score";

    public override string Instructions => Question;
}

/// <summary>判断题。对应 TS <c>ClassifierBoolQuestion</c>。</summary>
public sealed record ClassifierBoolQuestion(
    string Question,
    string TrueMeaning,
    string FalseMeaning) : ClassifierQuestion
{
    public override string Kind => "bool";

    public override string Instructions => Question;
}

/// <summary>分类问题判别基类。对应 TS <c>ClassifierQuestion</c> 联合。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClassifierChoiceQuestion), "choice")]
[JsonDerivedType(typeof(ClassifierScoreQuestion), "score")]
[JsonDerivedType(typeof(ClassifierBoolQuestion), "bool")]
public abstract record ClassifierQuestion
{
    /// <summary>判别键（choice/score/bool）。</summary>
    [JsonIgnore]
    public abstract string Kind { get; }

    /// <summary>问题指令。</summary>
    [JsonIgnore]
    public abstract string Instructions { get; }
}

/// <summary>分类上下文：状态 + 按 id 索引的问题。对应 TS <c>ClassifierContext</c>。</summary>
public sealed record ClassifierContext
{
    public required JsonObject State { get; init; }

    public required IReadOnlyDictionary<string, ClassifierQuestion> Questions { get; init; }
}

/// <summary>选择题答案。对应 TS <c>ClassifierChoiceAnswer</c>。</summary>
public sealed record ClassifierChoiceAnswer(
    string Choice,
    IReadOnlyDictionary<string, double> Probabilities,
    double Confidence) : ClassifierAnswer
{
    public override string Kind => "choice";
}

/// <summary>评分答案。对应 TS <c>ClassifierScoreAnswer</c>。</summary>
public sealed record ClassifierScoreAnswer(double Score, double Confidence) : ClassifierAnswer
{
    public override string Kind => "score";
}

/// <summary>判断答案。对应 TS <c>ClassifierBoolAnswer</c>。</summary>
public sealed record ClassifierBoolAnswer(double Probability) : ClassifierAnswer
{
    public override string Kind => "bool";
}

/// <summary>答案判别基类。对应 TS <c>ClassifierAnswer</c> 联合。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ClassifierChoiceAnswer), "choice")]
[JsonDerivedType(typeof(ClassifierScoreAnswer), "score")]
[JsonDerivedType(typeof(ClassifierBoolAnswer), "bool")]
public abstract record ClassifierAnswer
{
    [JsonIgnore]
    public abstract string Kind { get; }
}

/// <summary>分类结果。对应 TS <c>ClassifierResult</c>。</summary>
public sealed record ClassifierResult
{
    public required string Api { get; init; }

    public required string Provider { get; init; }

    public required string Model { get; init; }

    public IReadOnlyDictionary<string, ClassifierAnswer> Answers { get; init; }
        = new Dictionary<string, ClassifierAnswer>();

    /// <summary>token 用量与服务报价下的成本（服务报告 token 时才有）。</summary>
    public Usage? Usage { get; init; }

    public ClassifierStopReason StopReason { get; init; } = ClassifierStopReason.Stop;

    public string? ErrorMessage { get; init; }

    public long Timestamp { get; init; } = DateTimeOffset.Now.ToUnixTimeMilliseconds();
}

/// <summary>分类请求选项。对应 TS <c>ClassifierOptions</c>。</summary>
public record ClassifierOptions
{
    public CancellationToken Signal { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string?>? Headers { get; init; }

    public int? TimeoutMs { get; init; }

    public int? MaxRetries { get; init; }

    public int? MaxRetryDelayMs { get; init; }

    /// <summary>
    /// 答案 logit 在归一化为概率前先除以该值：大于 1 变软、小于 1 变锐。
    /// 必须为正；无法应用的 API 忽略之。
    /// </summary>
    public double? Temperature { get; init; }

    public Func<JsonObject, ModelSpec, Task<JsonObject?>>? OnPayload { get; init; }

    public Func<ProviderResponse, ModelSpec, Task>? OnResponse { get; init; }
}

/// <summary>分类 API 实现的函数形状。对应 TS <c>ClassifierFunction</c>。</summary>
public delegate Task<ClassifierResult> ClassifierFunction(
    ModelSpec model, ClassifierContext context, ClassifierOptions? options);
