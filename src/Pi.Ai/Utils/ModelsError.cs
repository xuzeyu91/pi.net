using System.Text.Json.Serialization;

namespace Pi.Ai.Utils;

/// <summary>错误码。对应 TS <c>ModelsErrorCode</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModelsErrorCode>))]
public enum ModelsErrorCode
{
    ModelSource,
    ModelValidation,
    Provider,
    Stream,
    Auth,
    OAuth,
}

/// <summary>
/// Models 门面错误。对应 TS <c>ModelsError</c>（utils/models-error.ts）。
/// 调用方只看 <c>Message</c>，因此底层原因保持在消息里（withCauseDetail）。
/// </summary>
public sealed class ModelsError(ModelsErrorCode code, string message, Exception? cause = null)
    : Exception(WithCauseDetail(message, cause), cause)
{
    public ModelsErrorCode Code { get; } = code;

    /// <summary>底层原因拼进消息，避免调用方丢失细节。对应 TS <c>withCauseDetail</c>。</summary>
    private static string WithCauseDetail(string message, Exception? cause)
    {
        if (cause is null) return message;
        var detail = cause.Message.Trim();
        if (detail.Length == 0 || message.Contains(detail, StringComparison.Ordinal)) return message;
        return $"{message}: {detail}";
    }
}
