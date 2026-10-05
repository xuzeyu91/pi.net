using System.Security.Cryptography;
using System.Text;
using Pi.Ai.Auth.OAuth;
using Xunit;

namespace Pi.Ai.Tests.Auth;

/// <summary>PKCE 工具测试。</summary>
public class PkceTests
{
    [Fact]
    public void VerifierIs43Base64UrlChars()
    {
        var pair = Pkce.Generate();
        Assert.Equal(43, pair.Verifier.Length); // 32 随机字节 → base64url 43 字符
        Assert.DoesNotContain('+', pair.Verifier);
        Assert.DoesNotContain('/', pair.Verifier);
        Assert.DoesNotContain('=', pair.Verifier);
    }

    [Fact]
    public void ChallengeIsSha256OfVerifier()
    {
        var pair = Pkce.Generate();
        var expected = Convert
            .ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Verifier)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(expected, pair.Challenge);
    }

    [Fact]
    public void GeneratesUniqueVerifiers()
    {
        var a = Pkce.Generate();
        var b = Pkce.Generate();
        Assert.NotEqual(a.Verifier, b.Verifier);
        Assert.NotEqual(a.Challenge, b.Challenge);
    }

    [Fact]
    public void Base64UrlEncodeUsesUrlSafeAlphabet()
    {
        // [0xfb,0xff,0xbf,0xdb] 的标准 base64 含 +/=，应产出 URL 安全且无填充的串。
        var encoded = Pkce.Base64UrlEncode([0xfb, 0xff, 0xbf, 0xdb]);
        Assert.Equal("-_-_2w", encoded);
    }
}
