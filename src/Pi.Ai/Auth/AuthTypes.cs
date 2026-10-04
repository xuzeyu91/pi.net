using System.Text.Json.Serialization;

namespace Pi.Ai.Auth;

/// <summary>单次模型请求的认证载荷。对应 TS <c>ModelAuth</c>。</summary>
public sealed record ModelAuth
{
    public string? ApiKey { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public string? BaseUrl { get; init; }
}

/// <summary>凭据类别。对应 TS <c>Credential["type"]</c>。</summary>
public enum CredentialKind
{
    ApiKey,
    OAuth,
}

/// <summary>按 provider 一个类型标记凭据——今日 auth.json 的形状。对应 TS <c>Credential</c>。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ApiKey), "api_key")]
[JsonDerivedType(typeof(OAuth), "oauth")]
public abstract record Credential
{
    [JsonIgnore]
    public abstract CredentialKind Kind { get; }

    /// <summary>存储的 api-key 凭据；Env 持 provider 作用域环境/配置值。对应 TS <c>ApiKeyCredential</c>。</summary>
    public sealed record ApiKey(
        [property: JsonPropertyName("key")] string? Key = null,
        [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env = null) : Credential
    {
        public override CredentialKind Kind => CredentialKind.ApiKey;
    }

    /// <summary>规范 OAuth 凭据。对应 TS <c>OAuthCredential</c>（expires 为 epoch 毫秒）。</summary>
    public sealed record OAuth(
        string Refresh,
        string Access,
        long Expires) : Credential
    {
        public override CredentialKind Kind => CredentialKind.OAuth;
    }
}

/// <summary>非密钥凭据元数据（账号/状态枚举用）。对应 TS <c>CredentialInfo</c>。</summary>
public sealed record CredentialInfo(string ProviderId, CredentialKind Type);

/// <summary>
/// 应用持有的凭据存储（按 Provider.id 键控，每 provider 一个凭据）。
/// 对应 TS <c>CredentialStore</c>：modify 是唯一写路径（串行读改写），
/// read 缺条目返回 null；仅存储故障才抛错。
/// </summary>
public interface ICredentialStore
{
    /// <summary>读取存储凭据（可能过期）；显示/状态用。</summary>
    Task<Credential?> ReadAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>列举凭据元数据（不解析、不暴露密钥）。</summary>
    Task<IReadOnlyList<CredentialInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 串行化写——唯一写路径。fn 看到当前凭据（刷新/登录竞态正确性依赖此）；
    /// 返回新凭据或 null 表示不变。按 provider id 互斥。解析为写后凭据。
    /// </summary>
    Task<Credential?> ModifyAsync(string providerId,
        Func<Credential?, Task<Credential?>> fn, CancellationToken cancellationToken = default);

    /// <summary>移除凭据（登出）；与 Modify 串行化。</summary>
    Task DeleteAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>认证解析的环境访问（可注入）。对应 TS <c>AuthContext</c>。</summary>
public interface IAuthContext
{
    Task<string?> EnvAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>文件存在检查（支持 ~ 前缀）。</summary>
    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>模型认证解析结果。对应 TS <c>AuthResult</c>。</summary>
public sealed record AuthResult
{
    public required ModelAuth Auth { get; init; }

    /// <summary>provider 作用域环境/配置值。</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }

    /// <summary>状态 UI 的人类可读标签："ANTHROPIC_API_KEY"、"OAuth" 等。</summary>
    public string? Source { get; init; }
}
