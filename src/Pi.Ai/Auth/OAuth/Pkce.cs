using System.Security.Cryptography;
using System.Text;

namespace Pi.Ai.Auth.OAuth;

/// <summary>PKCE verifier/challenge 对。</summary>
public sealed record PkcePair(string Verifier, string Challenge);

/// <summary>
/// PKCE 工具。对应 TS <c>generatePKCE</c>（auth/oauth/pkce.ts）：
/// 32 随机字节 base64url 作 verifier，SHA-256 摘要 base64url 作 challenge（S256）。
/// </summary>
public static class Pkce
{
    public static PkcePair Generate()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64UrlEncode(verifierBytes);

        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    /// <summary>base64url（无填充）。对应 TS <c>base64urlEncode</c>。</summary>
    public static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
