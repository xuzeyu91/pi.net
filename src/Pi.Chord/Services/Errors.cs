namespace Pi.Chord.Services;

/// <summary>远程服务错误码。对应 TS <c>REMOTE_SERVICE_ERROR_CODES</c>（errors.ts）。</summary>
public static class RemoteServiceErrorCodes
{
    public static readonly IReadOnlyList<string> All =
    [
        "service_not_allowed",
        "service_not_found",
        "service_mode_mismatch",
        "service_member_not_found",
        "service_member_mismatch",
        "service_instance_not_found",
        "service_stale_instance",
        "service_invalid_value",
    ];

    public static bool IsRemoteServiceErrorCode(object? value)
        => value is string text && All.Contains(text);
}

/// <summary>远程服务调用失败。对应 TS <c>RemoteServiceError</c>。</summary>
public sealed class RemoteServiceError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
