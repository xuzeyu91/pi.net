using System.Text.Json.Serialization;

namespace Pi.Ai.Auth;

/// <summary>select 提示的选项。对应 TS <c>AuthPrompt</c> 的 options 元素。</summary>
public sealed record AuthSelectOption(string Id, string Label, string? Description = null);

/// <summary>登录期间展示给用户的提示（判别联合）。对应 TS <c>AuthPrompt</c>。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AuthPrompt.Text), "text")]
[JsonDerivedType(typeof(AuthPrompt.Secret), "secret")]
[JsonDerivedType(typeof(AuthPrompt.Select), "select")]
[JsonDerivedType(typeof(AuthPrompt.ManualCode), "manual_code")]
public abstract record AuthPrompt
{
    /// <summary>文本输入。对应 TS <c>{ type: "text" }</c>。</summary>
    public sealed record Text(string Message, string? Placeholder = null) : AuthPrompt;

    /// <summary>密文输入（回显遮蔽）。对应 TS <c>{ type: "secret" }</c>。</summary>
    public sealed record Secret(string Message, string? Placeholder = null) : AuthPrompt;

    /// <summary>单选；返回所选 <see cref="AuthSelectOption.Id"/>。对应 TS <c>{ type: "select" }</c>。</summary>
    public sealed record Select(string Message, IReadOnlyList<AuthSelectOption> Options) : AuthPrompt;

    /// <summary>手动粘贴授权码/重定向 URL。对应 TS <c>{ type: "manual_code" }</c>。</summary>
    public sealed record ManualCode(string Message, string? Placeholder = null) : AuthPrompt;
}

/// <summary>info 事件里的链接。对应 TS <c>AuthInfoLink</c>。</summary>
public sealed record AuthInfoLink(string Url, string? Label = null);

/// <summary>登录过程中的通知事件（判别联合）。对应 TS <c>AuthEvent</c>。</summary>
public abstract record AuthEvent
{
    /// <summary>一般信息（可带链接）。对应 TS <c>{ type: "info" }</c>。</summary>
    public sealed record Info(string Message, IReadOnlyList<AuthInfoLink>? Links = null) : AuthEvent;

    /// <summary>浏览器授权地址。对应 TS <c>{ type: "auth_url" }</c>。</summary>
    public sealed record AuthUrl(string Url, string? Instructions = null) : AuthEvent;

    /// <summary>设备码授权信息。对应 TS <c>{ type: "device_code" }</c>。</summary>
    public sealed record DeviceCode(
        string UserCode,
        string VerificationUri,
        int? IntervalSeconds = null,
        int? ExpiresInSeconds = null) : AuthEvent;

    /// <summary>进度更新。对应 TS <c>{ type: "progress" }</c>。</summary>
    public sealed record Progress(string Message) : AuthEvent;
}

/// <summary>
/// 登录交互回调（api-key 与 OAuth 流共用）。对应 TS <c>AuthInteraction</c>。
/// <c>PromptAsync</c> 返回输入/所选字符串；取消/中止时抛 <see cref="OperationCanceledException"/>。
/// 提示级取消用 <c>cancellationToken</c>（对应 TS AuthPrompt.signal，例如 manual_code
/// 与回调服务器竞速、回调先到时中止挂起的提示）；整体流程取消用 <see cref="Signal"/>。
/// </summary>
public interface IAuthInteraction
{
    /// <summary>展示提示并等待输入。</summary>
    Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default);

    /// <summary>推送通知事件（不等待）。</summary>
    void Notify(AuthEvent @event);
}

/// <summary>传给 provider 登录实现的规范化交互（signal 必有）。对应 TS <c>ProviderAuthInteraction</c>。</summary>
public sealed class ProviderAuthInteraction(IAuthInteraction inner, CancellationToken signal = default)
    : IAuthInteraction
{
    /// <summary>整体登录流程的取消令牌。</summary>
    public CancellationToken Signal { get; } = signal;

    /// <summary>流程已被取消。</summary>
    public bool IsAborted => Signal.IsCancellationRequested;

    /// <summary>取消时抛出（对齐 TS <c>signal.throwIfAborted()</c>）。</summary>
    public void ThrowIfAborted() => Signal.ThrowIfCancellationRequested();

    /// <inheritdoc />
    public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken = default)
        => inner.PromptAsync(prompt, cancellationToken);

    /// <inheritdoc />
    public void Notify(AuthEvent @event) => inner.Notify(@event);
}

/// <summary>应用为 <c>Models.Login</c> 提供的上下文。对应 TS <c>LoginOptions</c>。</summary>
public sealed record LoginOptions
{
    /// <summary>
    /// 返回本应用安装的稳定 id（如发给 OpenAI 的 agent host id）。只有需要的登录流程才会
    /// 调用；应用可在首次调用时创建 id，之后必须返回同一个。
    /// </summary>
    public Func<string>? GetDeviceId { get; init; }
}
