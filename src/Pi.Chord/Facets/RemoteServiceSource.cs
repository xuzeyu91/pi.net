using Pi.Chord.Services;

namespace Pi.Chord.Facets;

/// <summary>
/// 已打开的远程服务组：就绪门与实例门面。对应 TS <c>RemoteServices</c>。
/// </summary>
public interface IRemoteServices
{
    /// <summary>等待组内服务就绪（连接握手等）。</summary>
    Task ReadyAsync(CancellationToken cancellationToken = default);

    /// <summary>取服务的本地门面实例。</summary>
    object? UseRaw(string serviceId);
}

/// <summary>打开选项。对应 TS <c>open()</c> 的参数。</summary>
public sealed record RemoteServicesOptions
{
    public required IReadOnlyList<string> Services { get; init; }

    /// <summary>访问门面时的检查（host 未激活时拒绝）。</summary>
    public required Action AssertAccess { get; init; }

    public required Action<Exception> OnError { get; init; }
}

/// <summary>
/// 外部远程服务源：host 启动时按目录发现并打开。对应 TS <c>RemoteServiceSource</c>。
/// </summary>
public interface IRemoteServiceSource
{
    /// <summary>目录列举（serviceId + 模式）。</summary>
    Task<IReadOnlyList<ServiceCatalogueEntry>> CatalogueAsync(CancellationToken cancellationToken = default);

    /// <summary>打开一组服务（返回就绪门与门面）。</summary>
    IRemoteServices Open(RemoteServicesOptions options);

    /// <summary>未在目录中列出的服务也可延迟绑定（每个 host 至多一个此类源）。</summary>
    bool AcceptsUnavailableServices => false;
}

/// <summary>外部服务解析结果。对应 TS <c>ExternalService</c>。</summary>
public sealed record ExternalServiceBinding(string ServiceId, ServiceMode Mode, IRemoteServiceSource Source);
