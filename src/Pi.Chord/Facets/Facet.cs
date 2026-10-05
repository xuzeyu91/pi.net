using Pi.Chord.Services;

namespace Pi.Chord.Facets;

/// <summary>服务契约标识。对应 TS <c>Service&lt;T&gt;</c>（本地服务不发布远程）。</summary>
public sealed record Service<T>(string Id, bool Local = false);

/// <summary>服务引用（require/provide 记录）。对应 TS <c>FacetServiceReference</c>。</summary>
public sealed record FacetServiceReference(string ServiceId, ServiceMode Mode);

/// <summary>
/// Facet 装配环境。对应 TS <c>FacetEnvironment</c>：use/observe 声明依赖，
/// provide/provideMany 声明供给，replicatedState 建复制状态，own/onActivate/onDeactivate 注册生命周期。
/// </summary>
public interface IFacetEnvironment
{
    /// <summary>
    /// 仅声明对服务的依赖（不解析；装配期据此发现外部源）。
    /// 对应 TS <c>use()</c> 的声明侧——TS 的延迟 view 在 C# 拆分为 Require（声明）+ Use（解析）。
    /// </summary>
    void Require<T>(Service<T> service);

    /// <summary>解析单例服务（须在装配后调用，例如 OnActivate 回调中）。</summary>
    T Use<T>(Service<T> service);

    /// <summary>声明对 keyed 服务的依赖并观察每个存活实例。</summary>
    void Observe<T>(Service<T> service, Func<T, Context.Context, Task?> handler);

    /// <summary>声明并安装本 facet 的单例服务实现。</summary>
    void Provide<T>(Service<T> service, T implementation);

    /// <summary>声明多实例服务所有权并返回延迟孵化能力。</summary>
    Action SpawnDeferred<T>(Service<T> service, Func<string, T> factory) where T : class;

    /// <summary>以不可变所有权创建初始化的可变复制状态。</summary>
    MutableReplicatedState<T> ReplicatedState<T>(T initial) where T : class;

    /// <summary>注册资源清理。</summary>
    void Own(Func<Task> disposal);

    /// <summary>注册依赖就绪后的异步初始化。</summary>
    void OnActivate(Func<Task> callback);

    /// <summary>注册最终拆除回调。</summary>
    void OnDeactivate(Func<Task> callback);
}

/// <summary>Facet：一份可安装的功能单元。对应 TS <c>Facet</c>。</summary>
public interface IFacet
{
    string Id { get; }

    /// <summary>同步装配（依赖声明与实现注册）；异步 setup 在 C# 由同步契约排除。</summary>
    void Setup(IFacetEnvironment environment);
}

/// <summary>Host 选项。对应 TS <c>FacetOptions</c>。</summary>
public sealed record FacetOptions
{
    public required IReadOnlyList<IFacet> Facets { get; init; }

    /// <summary>外部远程服务源（目录发现 + 打开）。</summary>
    public IReadOnlyList<IRemoteServiceSource> ServiceSources { get; init; } = [];

    /// <summary>失败上报器（默认忽略）。</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>生命周期状态。对应 TS <c>LifecycleState</c>。</summary>
internal enum LifecycleState { SettingUp, Prepared, Active, Disposing, Dead }

/// <summary>facet 生命周期：prepare → activate → dispose。对应 TS <c>FacetLifecycle</c>。</summary>
internal sealed class FacetLifecycle
{
    private readonly string _facetId;
    private readonly List<Func<Task>> _activateCallbacks = [];
    private readonly List<Func<Task>> _deactivateCallbacks = [];
    private readonly List<Func<Task>> _disposals = [];
    private LifecycleState _state = LifecycleState.SettingUp;

    public FacetLifecycle(string facetId) => _facetId = facetId;

    public LifecycleState State => _state;

    public void AssertPreparable()
    {
        if (_state == LifecycleState.Dead)
            throw new InvalidOperationException($"Facet {_facetId} is dead");
        if (_state == LifecycleState.Disposing)
            throw new InvalidOperationException($"Facet {_facetId} is disposing");
    }

    /// <summary>装配完成（进入 prepared）。</summary>
    public void Prepared()
    {
        AssertPreparable();
        if (_state != LifecycleState.SettingUp)
            throw new InvalidOperationException($"Facet {_facetId} has already been prepared");
        _state = LifecycleState.Prepared;
    }

    public void OnActivate(Func<Task> callback)
    {
        if (_state != LifecycleState.SettingUp && _state != LifecycleState.Prepared)
            throw new InvalidOperationException($"Facet {_facetId} cannot register activation now");
        _activateCallbacks.Add(callback);
    }

    public void OnDeactivate(Func<Task> callback)
    {
        if (_state != LifecycleState.SettingUp && _state != LifecycleState.Prepared)
            throw new InvalidOperationException($"Facet {_facetId} cannot register deactivation now");
        _deactivateCallbacks.Add(callback);
    }

    public void Own(Func<Task> disposal) => _disposals.Add(disposal);

    /// <summary>激活：倒序执行 deactivate 注册的反向序在 dispose 时使用；激活按注册序。</summary>
    public async Task ActivateAsync()
    {
        if (_state != LifecycleState.Prepared)
            throw new InvalidOperationException($"Facet {_facetId} is not prepared");
        foreach (var callback in _activateCallbacks)
        {
            await callback().ConfigureAwait(false);
        }
        _state = LifecycleState.Active;
    }

    /// <summary>拆除：deactivate 回调 → 资源清理（都执行，错误聚合由调用方处理）。</summary>
    public async Task<List<Exception>> DisposeAsync()
    {
        var errors = new List<Exception>();
        if (_state is LifecycleState.Disposing or LifecycleState.Dead) return errors;
        _state = LifecycleState.Disposing;
        for (var index = _deactivateCallbacks.Count - 1; index >= 0; index--)
        {
            try
            {
                await _deactivateCallbacks[index]().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        for (var index = _disposals.Count - 1; index >= 0; index--)
        {
            try
            {
                await _disposals[index]().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }
        _activateCallbacks.Clear();
        _deactivateCallbacks.Clear();
        _disposals.Clear();
        _state = LifecycleState.Dead;
        return errors;
    }
}

/// <summary>
/// 加载完毕的 facets（可拆卸）。对应 TS <c>LoadedFacets</c>（types.ts）。
/// </summary>
public sealed class LoadedFacets
{
    private IReadOnlyList<IFacet> _facets = [];

    /// <summary>加载出的 facets；拆卸后清空（对齐 TS 的 <c>get facets()</c>）。</summary>
    public required IReadOnlyList<IFacet> Facets
    {
        get => _facets;
        init => _facets = value;
    }

    /// <summary>拆卸回调（幂等由实现保证）。对应 TS <c>dispose()</c>。</summary>
    public required Func<Task> Dispose { get; init; }

    public Task DisposeAsync() => Dispose();

    /// <summary>拆卸后清空 facets（由加载器调用）。</summary>
    internal void ClearFacets() => _facets = [];
}

/// <summary>
/// facet 加载器：把外部来源（静态列表 / bundle / 组合加载器）变成
/// <see cref="LoadedFacets"/>。对应 TS <c>FacetLoader</c>（types.ts）。
/// </summary>
public interface IFacetLoader
{
    Task<LoadedFacets> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 已激活的 facet 宿主。对应 TS <c>FacetHost</c>（types.ts）。
/// </summary>
public sealed class FacetHost
{
    public required RemoteServiceProvider Services { get; init; }

    /// <summary>激活并替换同 id 的 facets，不断开消费者服务句柄。对应 TS <c>reload()</c>。</summary>
    public required Func<IReadOnlyList<IFacet>, Task> Reload { get; init; }

    public required Func<Task> Dispose { get; init; }

    public Task ReloadAsync(IReadOnlyList<IFacet> facets) => Reload(facets);

    public Task DisposeAsync() => Dispose();
}
