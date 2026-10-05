using Pi.Ai.Auth;

namespace Pi.Ai.Compat;

/// <summary>旧版扩展 OAuth 提示。对应 TS <c>OAuthPrompt</c>（compat/extension-oauth-types.ts）。</summary>
public sealed record OAuthPrompt(string Message, string? Placeholder = null, bool? AllowEmpty = null);

/// <summary>旧版扩展 OAuth 授权链接。对应 TS <c>OAuthAuthInfo</c>。</summary>
public sealed record OAuthAuthInfo(string Url, string? Instructions = null);

/// <summary>旧版扩展 OAuth 设备码通知。对应 TS <c>OAuthDeviceCodeInfo</c>。</summary>
public sealed record OAuthDeviceCodeInfo(
    string UserCode,
    string VerificationUri,
    int? IntervalSeconds = null,
    int? ExpiresInSeconds = null);

/// <summary>选择项。对应 TS <c>OAuthSelectOption</c>。</summary>
public sealed record OAuthSelectOption(string Id, string Label);

/// <summary>选择提示。对应 TS <c>OAuthSelectPrompt</c>。</summary>
public sealed record OAuthSelectPrompt(string Message, IReadOnlyList<OAuthSelectOption> Options);

/// <summary>
/// 仅为 coding-agent 扩展兼容保留的回调面。对应 TS <c>OAuthLoginCallbacks</c>。
/// </summary>
public interface IOAuthLoginCallbacks
{
    void OnAuth(OAuthAuthInfo info);

    void OnDeviceCode(OAuthDeviceCodeInfo info);

    Task<string> OnPromptAsync(OAuthPrompt prompt, CancellationToken cancellationToken = default);

    void OnProgress(string message);

    Task<string> OnManualCodeInputAsync(CancellationToken cancellationToken = default);

    Task<string?> OnSelectAsync(OAuthSelectPrompt prompt, CancellationToken cancellationToken = default);

    CancellationToken Signal { get; }
}
