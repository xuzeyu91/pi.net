using Pi.Chord.Services;
using Pi.Chord.Context;

namespace Pi.Chord.Facets;

/// <summary>
/// host 级服务槽：单例槽 + keyed 源登记。对应 TS <c>HostServiceSlots</c>。
/// </summary>
internal sealed class HostServiceSlots
{
    private readonly Dictionary<string, ServiceSlot> _singletons = [];
    private readonly Dictionary<string, IKeyedServiceSource> _keyedSources = [];

    /// <summary>取（或创建）单例槽并返回视图。</summary>
    public T GetSingleton<T>(Service<T> service, Action assertAccess)
    {
        if (!_singletons.TryGetValue(service.Id, out var slot))
        {
            slot = new ServiceSlot(service.Id);
            _singletons[service.Id] = slot;
        }
        return slot.View<T>(assertAccess);
    }

    public bool HasSingleton(string serviceId) => _singletons.ContainsKey(serviceId);

    /// <summary>观察 keyed 服务（源未连接时报错）。</summary>
    public IDisposable Observe<T>(
        Service<T> service, Action assertAccess,
        Func<T, Context.Context, Task?> handler)
    {
        if (!_keyedSources.TryGetValue(service.Id, out var source))
            throw new InvalidOperationException($"Service {service.Id} is disconnected");
        var stopped = false;
        var stop = source.Observe(service, (target, context) =>
        {
            var slot = new ServiceSlot(service.Id);
            slot.Bind(target!);
            return handler(
                slot.View<T>(() =>
                {
                    assertAccess();
                    if (stopped || (context.AbortSignal?.IsCancellationRequested ?? false))
                    {
                        throw new InvalidOperationException($"Keyed service {service.Id} observation is closed");
                    }
                }), context);
        });
        return new DisposalHandle(() =>
        {
            if (stopped) return;
            stopped = true;
            stop.Dispose();
        });
    }

    /// <summary>绑定单例实现（装配阶段；槽不存在则创建——C# 立即解析语义下装配先于 Use）。</summary>
    public void BindSingleton(string serviceId, object target)
    {
        if (!_singletons.TryGetValue(serviceId, out var slot))
        {
            slot = new ServiceSlot(serviceId);
            _singletons[serviceId] = slot;
        }
        slot.Bind(target);
    }

    /// <summary>连接 keyed 源。</summary>
    public void BindKeyed(string serviceId, IKeyedServiceSource source)
        => _keyedSources[serviceId] = source;

    public void Dispose()
    {
        foreach (var slot in _singletons.Values) slot.Unbind();
        _singletons.Clear();
        _keyedSources.Clear();
    }

    private sealed class DisposalHandle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary>keyed 服务源。对应 TS <c>KeyedServiceSource</c>。</summary>
public interface IKeyedServiceSource
{
    IDisposable Observe<T>(Service<T> service, Func<T, Context.Context, Task?> handler);
}

/// <summary>
/// 本地 keyed 服务注册表（单机孵化/观察）。对应 TS <c>LocalKeyedServiceRegistry</c>。
/// </summary>
public sealed class LocalKeyedServiceRegistry : IKeyedServiceSource, IDisposable
{
    private sealed class Registration
    {
        public Dictionary<string, long> Generations { get; } = [];
        public InstanceDirectory<LocalEntry> Directory { get; init; } = default!;
    }

    private sealed class LocalEntry(string key, long generation, object service) : IInstanceDirectoryEntry
    {
        public string Key => key;

        public long Generation => generation;

        public object Service => service;

        public void Deactivate() { }
    }

    private readonly Dictionary<string, Registration> _registrations = [];
    private readonly Action<Exception> _onError;
    private bool _disposed;

    public LocalKeyedServiceRegistry(IEnumerable<string> serviceIds, Action<Exception> onError)
    {
        var ids = serviceIds.ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException("Local keyed service registry has duplicate IDs");
        _onError = onError;
        foreach (var serviceId in ids)
        {
            _registrations[serviceId] = new Registration
            {
                Directory = new InstanceDirectory<LocalEntry>(ready: true, onError),
            };
        }
    }

    /// <summary>孵化一个本地 keyed 实例并返回关闭委托。</summary>
    public Action Spawn<T>(Service<T> service, string key, T implementation)
    {
        AssertActive();
        if (key.Length == 0)
            throw new ArgumentException("Local service instance key must not be empty");
        var registration = GetRegistration(service.Id);
        if (registration.Directory.Get(key) is not null)
            throw new InvalidOperationException($"Local service {service.Id} already has a live instance with key {key}");
        var generation = registration.Generations.GetValueOrDefault(key) + 1;
        registration.Generations[key] = generation;
        var entry = new LocalEntry(key, generation, implementation!);
        registration.Directory.Insert(entry);
        var closed = false;
        return () =>
        {
            if (closed) return;
            closed = true;
            registration.Directory.Remove(entry);
        };
    }

    /// <summary>观察 keyed 实例流。对应 TS <c>observe</c>。</summary>
    public IDisposable Observe<T>(Service<T> service, Func<T, Context.Context, Task?> handler)
    {
        AssertActive();
        return GetRegistration(service.Id).Directory.Observe((target, context) =>
            handler((T)target, context));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var registration in _registrations.Values) registration.Directory.Dispose();
        _registrations.Clear();
    }

    private Registration GetRegistration(string serviceId)
    {
        if (!_registrations.TryGetValue(serviceId, out var registration))
            throw new InvalidOperationException($"Local keyed service {serviceId} is not registered");
        return registration;
    }

    private void AssertActive()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LocalKeyedServiceRegistry));
    }
}

/// <summary>
/// 暂存式孵化器：provider 装配前记录实例，connect 后即时安装（含已暂存的）。
/// 对应 TS <c>StagedServiceSpawner</c>。
/// </summary>
internal sealed class StagedServiceSpawner<T>(
    FacetLifecycleAccessor lifecycle, Action<string, T> validate)
    where T : class
{
    private readonly Dictionary<string, (T Implementation, Action? Release)> _instances = [];
    private Func<string, T, Action>? _installer;

    /// <summary>连接安装器并安装全部暂存实例。</summary>
    public void Connect(Func<string, T, Action> installer)
    {
        if (_installer is not null)
            throw new InvalidOperationException("Facet service provider is already connected");
        _installer = installer;
        foreach (var (key, instance) in _instances)
        {
            _instances[key] = (instance.Implementation, installer(key, instance.Implementation));
        }
    }

    /// <summary>孵化实例（激活期合法；连接前暂存）。返回关闭委托。</summary>
    public Action Spawn(string key, T implementation)
    {
        lifecycle.AssertActive("spawn service instances");
        validate(key, implementation);
        if (_instances.ContainsKey(key))
            throw new InvalidOperationException($"Facet service already has a live instance with key {key}");
        _instances[key] = (implementation, null);
        var installer = _installer;
        if (installer is not null) _instances[key] = (implementation, installer(key, implementation));
        var closed = false;
        return () =>
        {
            if (closed) return;
            closed = true;
            if (!_instances.TryGetValue(key, out var instance)
                || !ReferenceEquals(instance.Implementation, implementation))
            {
                return;
            }
            _instances.Remove(key);
            instance.Release?.Invoke();
        };
    }
}

/// <summary>生命周期激活断言的访问器（解耦 FacetLifecycle internal 可见性）。</summary>
internal sealed class FacetLifecycleAccessor(Action<string> assertActive)
{
    public void AssertActive(string operation) => assertActive(operation);
}
