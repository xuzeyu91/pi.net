using System.Security.Cryptography;
using System.Text;

namespace Pi.Ai.Api;

/// <summary>AWS SigV4 签名后的请求要素。</summary>
public sealed record AwsSignedRequest(
    string Authorization,
    string AmzDate,
    string DateStamp,
    string CredentialScope,
    string SignedHeaders,
    byte[] ContentHash);

/// <summary>
/// AWS Signature Version 4 签名（bedrock runtime converse-stream 用）。
/// 对应 TS 侧 @aws-sdk/client-bedrock-runtime 的 SigV4 中间件行为：
/// 服务名 bedrock、X-Amz-Content-Sha256 载荷哈希、x-amz-date/amz-date 双头。
/// </summary>
public static class AwsSigV4
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "bedrock";

    /// <summary>
    /// 签名一个 POST 请求。返回需附加到请求的头（authorization/x-amz-date/x-amz-content-sha256/x-amz-security-token 可选）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> Sign(
        string method,
        string url,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string bodyJson,
        string accessKeyId,
        string secretAccessKey,
        string? sessionToken,
        string region,
        DateTimeOffset now)
    {
        var uri = new Uri(url);
        var host = uri.Host;
        var canonicalUri = uri.AbsolutePath;
        // 查询串按 key 排序（converse-stream 无查询参数时为空串）。
        var canonicalQuery = CanonicalQuery(uri.Query);

        var payloadHash = Sha256Hex(Encoding.UTF8.GetBytes(bodyJson));
        var amzDate = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var dateStamp = now.ToUniversalTime().ToString("yyyyMMdd");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = host,
            ["x-amz-date"] = amzDate,
            ["x-amz-content-sha256"] = payloadHash,
        };
        if (!string.IsNullOrEmpty(sessionToken))
        {
            headers["x-amz-security-token"] = sessionToken;
        }
        if (extraHeaders is not null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                // host/x-amz-*/authorization 由签名与调用方约定保留（对齐 TS 的 isReservedHeader 之外的调用头参与签名）。
                headers[key] = value;
            }
        }

        // 规范头：小写键排序（host 必含）。
        var signedHeadersKeys = headers.Keys
            .Select(key => key.ToLowerInvariant())
            .Distinct()
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        var signedHeaders = string.Join(";", signedHeadersKeys);
        var canonicalHeaders = string.Concat(signedHeadersKeys.Select(key =>
        {
            var value = headers.First(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value.Trim();
            return $"{key}:{value}\n";
        }));

        var canonicalRequest = string.Join("\n",
            method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);

        var credentialScope = $"{dateStamp}/{region}/{Service}/aws4_request";
        var stringToSign = string.Join("\n",
            Algorithm, amzDate, credentialScope, Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest)));

        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretAccessKey}"), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, Service);
        var kSigning = HmacSha256(kService, "aws4_request");
        var signature = ToHex(HmacSha256(kSigning, stringToSign));

        headers["authorization"] =
            $"{Algorithm} Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        return headers;
    }

    private static string CanonicalQuery(string query)
    {
        // URL.Query 带 "?" 前缀；RFC 3986 编码后按 key 排序。
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        if (trimmed.Length == 0) return "";
        return string.Join("&", trimmed.Split('&')
            .Select(pair =>
            {
                var eq = pair.IndexOf('=');
                return eq < 0
                    ? $"{Uri.EscapeDataString(pair)}="
                    : $"{Uri.EscapeDataString(pair[..eq])}={Uri.EscapeDataString(pair[(eq + 1)..])}";
            })
            .OrderBy(s => s, StringComparer.Ordinal));
    }

    private static byte[] HmacSha256(byte[] key, string data)
        => new HMACSHA256(key).ComputeHash(Encoding.UTF8.GetBytes(data));

    private static string Sha256Hex(byte[] data)
        => ToHex(SHA256.HashData(data));

    private static string ToHex(byte[] bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();
}
