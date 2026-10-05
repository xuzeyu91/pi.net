using Pi.Ai.Auth;
using Pi.Ai.Compat;

namespace Pi.Ai;

/// <summary>
/// coding-agent 扩展 OAuth 声明的类型兼容入口。对应 TS <c>oauth.ts</c>：
/// 仅重导出 <c>compat/extension-oauth-types.ts</c> 的类型。C# 无类型重导出，
/// 此处以类型别名集中呈现该入口暴露的形状。
/// </summary>
public static class OAuthCompat
{
    /// <summary>对应 TS <c>OAuthCredentials</c>（= <c>auth/types.ts</c> 的 OAuth 凭据）。</summary>
    public static Type Credentials => typeof(Credential.OAuth);

    public static Type Prompt => typeof(OAuthPrompt);

    public static Type AuthInfo => typeof(OAuthAuthInfo);

    public static Type DeviceCodeInfo => typeof(OAuthDeviceCodeInfo);

    public static Type SelectOption => typeof(OAuthSelectOption);

    public static Type SelectPrompt => typeof(OAuthSelectPrompt);

    public static Type LoginCallbacks => typeof(IOAuthLoginCallbacks);
}
