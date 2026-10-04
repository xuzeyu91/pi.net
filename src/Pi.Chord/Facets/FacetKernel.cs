using Pi.Chord.Services;

namespace Pi.Chord.Facets;

/// <summary>facet 运行时记录（依赖/供给/生命周期）。对应 TS <c>FacetRuntime</c>。</summary>
internal sealed class FacetRuntime
{
    public string FacetId { get; }
    public List<FacetServiceReference> Requires { get; } = [];
    public List<FacetServiceReference> Provides { get; } = [];
    public FacetLifecycle Lifecycle { get; }
    public List<object> Implementations { get; } = [];

    public FacetRuntime(string facetId)
    {
        FacetId = facetId;
        Lifecycle = new FacetLifecycle(facetId);
    }
}

/// <summary>
/// Facet 内核：原子启动/热重载/拆除的依赖与生命周期管理。
/// 对应 TS <c>FacetKernel</c>（facets/host.ts）的核心层：
/// 依赖图校验（validateFacets + 拓扑排序 + 环检测）、按序激活、失败反向清理、
/// reload 形状保持校验。服务槽与外部源的装配（HostServiceSlots/RemoteServiceSource）
/// 将在 provider 集成阶段接通。
/// </summary>
public sealed class FacetKernel
{
    private readonly IReadOnlyList<IFacet> _initialFacets;
    private readonly Action<Exception> _onError;
    private readonly Dictionary<string, FacetRuntime> _facets = [];
    private IReadOnlyList<string> _activationOrder = [];
    private bool _active;

    public FacetKernel(FacetOptions options)
    {
        var ids = options.Facets.Select(facet => facet.Id).ToList();
        if (ids.Any(id => id.Length == 0)) throw new ArgumentException("Facet ID must not be empty");
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Facet IDs must be unique within a generation");
        _initialFacets = options.Facets;
        _onError = options.OnError ?? (_ => { });
    }

    /// <summary>原子启动：装配 → 依赖校验 → 拓扑序激活；失败反向清理。对应 TS <c>activate()</c>。</summary>
    public async Task ActivateAsync()
    {
        var records = new List<FacetRuntime>();
        try
        {
            foreach (var facet in _initialFacets)
            {
                var record = new FacetRuntime(facet.Id);
                _facets[facet.Id] = record;
                SetupFacet(facet, record);
                records.Add(record);
            }

            _activationOrder = ValidateFacets(records, new Dictionary<string, ServiceMode>());
            foreach (var id in _activationOrder)
            {
                await _facets[id].Lifecycle.ActivateAsync().ConfigureAwait(false);
            }
            _active = true;
        }
        catch (Exception error)
        {
            var cleanupErrors = DisposeFacetRecords(records.AsEnumerable().Reverse());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Facet generation startup and cleanup failed",
                    cleanupErrors.Prepend(error).ToArray());
            }
            throw;
        }
    }

    /// <summary>
    /// 热重载：形状必须保持（requires/provides 引用集合一致），新候选按旧激活序激活，
    /// 失败反向清理。对应 TS <c>reload()</c>。
    /// </summary>
    public async Task ReloadAsync(IReadOnlyList<IFacet> facets)
    {
        if (!_active) throw new InvalidOperationException("Facet host cannot reload before activation");
        var ids = facets.Select(facet => facet.Id).ToList();
        if (ids.Any(id => id.Length == 0)) throw new ArgumentException("Facet ID must not be empty");
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Reloaded facet IDs must be unique");
        foreach (var id in ids)
        {
            if (!_facets.ContainsKey(id)) throw new InvalidOperationException($"Facet {id} is not active");
        }

        var staged = new List<FacetRuntime>();
        var candidates = new List<FacetRuntime>();
        try
        {
            foreach (var facet in facets)
            {
                var record = new FacetRuntime(facet.Id);
                staged.Add(record);
                SetupFacet(facet, record);
                var previous = _facets[facet.Id];
                if (!SameFacetShape(previous, record))
                {
                    throw new InvalidOperationException(
                        $"Reloaded facet {facet.Id} must preserve its service requirements and provisions");
                }
                candidates.Add(record);
            }
        }
        catch (Exception error)
        {
            var cleanupErrors = DisposeFacetRecords(staged.AsEnumerable().Reverse());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Facet reload setup and cleanup failed",
                    cleanupErrors.Prepend(error).ToArray());
            }
            throw;
        }

        var replacements = candidates.ToDictionary(record => record.FacetId, record => record);
        var candidateOrder = _activationOrder
            .Where(replacements.ContainsKey)
            .Select(id => replacements[id])
            .ToList();
        try
        {
            foreach (var candidate in candidateOrder)
            {
                await candidate.Lifecycle.ActivateAsync().ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            var cleanupErrors = DisposeFacetRecords(candidateOrder.AsEnumerable().Reverse());
            if (cleanupErrors.Count > 0)
            {
                throw new AggregateException("Facet reload activation and cleanup failed",
                    cleanupErrors.Prepend(error).ToArray());
            }
            throw;
        }

        // 原子换入：旧记录 dispose。
        var replaced = candidates.Select(record => _facets[record.FacetId]).ToList();
        foreach (var record in candidates) _facets[record.FacetId] = record;
        var disposeErrors = DisposeFacetRecords(replaced);
        if (disposeErrors.Count > 0)
        {
            foreach (var error in disposeErrors) Report(error);
        }
    }

    /// <summary>拆除全部 facet（激活序的逆序）。对应 TS <c>dispose()</c>。</summary>
    public async Task<List<Exception>> DisposeAsync()
    {
        if (!_active && _facets.Count == 0) return [];
        _active = false;
        var errors = DisposeFacetRecords(_activationOrder
            .Select(id => _facets.GetValueOrDefault(id))
            .OfType<FacetRuntime>()
            .Reverse());
        _facets.Clear();
        return errors;
    }

    private void SetupFacet(IFacet facet, FacetRuntime record)
    {
        var environment = new Environment(record);
        facet.Setup(environment);
        record.Lifecycle.Prepared();
    }

    private static List<Exception> DisposeFacetRecords(IEnumerable<FacetRuntime> records)
    {
        var errors = new List<Exception>();
        foreach (var record in records)
        {
            errors.AddRange(record.Lifecycle.DisposeAsync().GetAwaiter().GetResult());
        }
        return errors;
    }

    private void Report(Exception error)
    {
        try
        {
            _onError(error);
        }
        catch (Exception reportError)
        {
            ReplicatedStates.ReportErrorAsync(reportError);
        }
    }

    // ─── 依赖图校验（完整移植） ──────────────────────────────────────────

    /// <summary>供给唯一性 + 依赖存在性 + 模式匹配 + 拓扑排序。对应 TS <c>validateFacets</c>。</summary>
    private static IReadOnlyList<string> ValidateFacets(
        IReadOnlyList<FacetRuntime> records,
        IReadOnlyDictionary<string, ServiceMode> externalServices)
    {
        var providers = new Dictionary<string, (string? FacetId, ServiceMode? Mode)>();
        foreach (var (serviceId, mode) in externalServices)
        {
            providers[serviceId] = (null, mode);
        }
        foreach (var record in records)
        {
            foreach (var provision in record.Provides)
            {
                if (providers.TryGetValue(provision.ServiceId, out var existing))
                {
                    if (existing.Mode is { } existingMode && existingMode != provision.Mode)
                    {
                        throw new InvalidOperationException(
                            $"Service {provision.ServiceId} is provided as both singleton and keyed");
                    }
                    if (existing.FacetId is null)
                    {
                        throw new InvalidOperationException(
                            $"Service {provision.ServiceId} is provided by both the host and {record.FacetId}");
                    }
                    throw new InvalidOperationException(
                        $"Service {provision.ServiceId} is provided by both {existing.FacetId} and {record.FacetId}");
                }
                providers[provision.ServiceId] = (record.FacetId, provision.Mode);
            }
        }

        var dependencies = records.ToDictionary(record => record.FacetId, _ => new HashSet<string>());
        var dependents = records.ToDictionary(record => record.FacetId, _ => new HashSet<string>());
        foreach (var record in records)
        {
            foreach (var requirement in record.Requires)
            {
                if (!providers.TryGetValue(requirement.ServiceId, out var provider))
                {
                    throw new InvalidOperationException(
                        $"Facet {record.FacetId} requires local/{requirement.ServiceId}/{requirement.Mode}, but no facet provides it");
                }
                if (provider.Mode is { } providerMode && providerMode != requirement.Mode)
                {
                    throw new InvalidOperationException(
                        $"Facet {record.FacetId} requires {requirement.ServiceId} as {requirement.Mode}, but {provider.FacetId ?? "the host"} provides it as {providerMode}");
                }
                if (provider.FacetId is null || provider.FacetId == record.FacetId) continue;
                dependencies[record.FacetId].Add(provider.FacetId);
                dependents[provider.FacetId].Add(record.FacetId);
            }
        }
        return TopologicalOrder(records, dependencies, dependents);
    }

    /// <summary>Kahn 拓扑排序；环依赖报错。对应 TS <c>topologicalOrder</c>。</summary>
    private static IReadOnlyList<string> TopologicalOrder(
        IReadOnlyList<FacetRuntime> records,
        IReadOnlyDictionary<string, HashSet<string>> dependencies,
        IReadOnlyDictionary<string, HashSet<string>> dependents)
    {
        var remaining = dependencies.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var ready = records.Select(record => record.FacetId).Where(id => remaining.GetValueOrDefault(id) == 0).ToList();
        var order = new List<string>(records.Count);
        while (ready.Count > 0)
        {
            var id = ready[0];
            ready.RemoveAt(0);
            order.Add(id);
            foreach (var dependent in dependents[id])
            {
                var count = remaining[dependent] - 1;
                remaining[dependent] = count;
                if (count == 0) ready.Add(dependent);
            }
        }
        if (order.Count != records.Count)
        {
            var cycle = records.Select(record => record.FacetId).Where(id => remaining[id] > 0);
            throw new InvalidOperationException($"Facet dependency cycle: {string.Join(", ", cycle)}");
        }
        return order;
    }

    /// <summary>reload 形状保持：requires/provides 引用集合一致。对应 TS <c>sameFacetShape</c>。</summary>
    private static bool SameFacetShape(FacetRuntime left, FacetRuntime right)
        => SameReferences(left.Requires, right.Requires) && SameReferences(left.Provides, right.Provides);

    private static bool SameReferences(
        IReadOnlyList<FacetServiceReference> left, IReadOnlyList<FacetServiceReference> right)
        => left.Count == right.Count
            && left.All(reference => right.Any(other =>
                other.ServiceId == reference.ServiceId && other.Mode == reference.Mode));

    /// <summary>
    /// 装配环境实现：记录 requires/provides、注册生命周期、创建复制状态。
    /// 服务槽解析（Use/Observe/ProvideMany 的实例接线）在 provider 集成阶段接通。
    /// </summary>
    private sealed class Environment(FacetRuntime record) : IFacetEnvironment
    {
        private readonly HashSet<(string ServiceId, ServiceMode Mode)> _declared = [];

        private void RecordReference(List<FacetServiceReference> target, string serviceId, ServiceMode mode)
        {
            if (target.Any(reference => reference.ServiceId == serviceId && reference.Mode == mode)) return;
            target.Add(new FacetServiceReference(serviceId, mode));
        }

        public T Use<T>(Service<T> service)
        {
            RecordReference(record.Requires, service.Id, ServiceMode.Singleton);
            // 实例解析在服务槽接线阶段完成；当前返回委托句柄的占位由依赖图校验保证可满足。
            throw new InvalidOperationException(
                $"Service slot resolution for {service.Id} is wired at provider integration stage");
        }

        public void Observe<T>(Service<T> service, Func<T, Context.Context, Task?> handler)
            => RecordReference(record.Requires, service.Id, ServiceMode.Keyed);

        public void Provide<T>(Service<T> service, T implementation)
        {
            RecordReference(record.Provides, service.Id, ServiceMode.Singleton);
            record.Implementations.Add(implementation!);
        }

        public Action SpawnDeferred<T>(Service<T> service, Func<string, T> factory)
        {
            RecordReference(record.Provides, service.Id, ServiceMode.Keyed);
            return () => { };
        }

        public MutableReplicatedState<T> ReplicatedState<T>(T initial) where T : class
        {
            var state = new MutableReplicatedState<T>(initial);
            record.Lifecycle.Own(() => Task.CompletedTask);
            return state;
        }

        public void Own(Func<Task> disposal) => record.Lifecycle.Own(disposal);

        public void OnActivate(Func<Task> callback) => record.Lifecycle.OnActivate(callback);

        public void OnDeactivate(Func<Task> callback) => record.Lifecycle.OnDeactivate(callback);
    }
}
