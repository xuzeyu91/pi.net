using System.Reflection;

namespace Pi.Chord.Services;

/// <summary>解析出的服务成员值与其接收者。对应 TS <c>ResolvedValue</c>（services/handle.ts）。</summary>
public sealed record ResolvedServiceMember(object? Value, object Receiver);

/// <summary>
/// Host 拥有的可变目标 + 消费者拥有的守卫视图。对应 TS <c>ServiceSlot</c>（services/handle.ts）。
/// <para>TS 的 <c>view()</c> 返回 Proxy，逐成员延迟解析并每次访问都跑 <c>assertAccess</c>；
/// C# 无 Proxy，<see cref="View{T}"/> 为**立即解析**（已记入设计差异），
/// 需要逐成员守卫/动态成员访问时用 <see cref="Resolve"/>。</para>
/// </summary>
public sealed class ServiceSlot
{
    private readonly string _serviceId;
    private object? _implementation;

    public ServiceSlot(string serviceId, bool wrapObjects = false)
    {
        _serviceId = serviceId;
        WrapObjects = wrapObjects;
    }

    public string ServiceId => _serviceId;

    /// <summary>对应 TS <c>wrapObjects</c>：视图是否包装非函数对象成员。</summary>
    public bool WrapObjects { get; }

    public bool IsBound => _implementation is not null;

    /// <summary>取绑定实现（立即解析）。未绑定或访问被拒时抛错。</summary>
    public T View<T>(Action assertAccess)
    {
        assertAccess();
        if (_implementation is null)
        {
            throw new InvalidOperationException($"Service {_serviceId} is not bound yet");
        }
        return (T)_implementation;
    }

    /// <summary>绑定实现（装配阶段调用）。</summary>
    public void Bind(object implementation) => _implementation = implementation;

    /// <summary>解除绑定。</summary>
    public void Unbind() => _implementation = null;

    /// <summary>
    /// 解析一个成员并返回其值与接收者。对应 TS <c>resolve(property, assertAccess)</c>：
    /// 先查成员字典（C# 的服务实现模型），再退回公有属性/字段反射。
    /// </summary>
    public ResolvedServiceMember Resolve(string member, Action assertAccess)
    {
        assertAccess();
        var implementation = _implementation
            ?? throw new InvalidOperationException($"Service {_serviceId} is disconnected");
        return ResolveFrom(implementation, member);
    }

    /// <summary>
    /// 调用一个成员方法。对应 TS <c>ValueView</c> 的 <c>Reflect.apply(value, receiver, args)</c>：
    /// 成员为 <c>Func&lt;object?[], object?&gt;</c> 时直接调用，否则用 <c>Delegate.DynamicInvoke</c>。
    /// </summary>
    public object? InvokeMember(string member, object?[] args, Action assertAccess)
    {
        var resolved = Resolve(member, assertAccess);
        return InvokeValue(resolved, member, args);
    }

    /// <summary>调用已解析的成员值（供门面在解析后调用，避免重复解析）。</summary>
    public static object? InvokeValue(ResolvedServiceMember resolved, string member, object?[] args)
    {
        switch (resolved.Value)
        {
            // Func<object?[], Task<object?>> 经返回值协变已归入此分支（返回 Task 值，由调用方等待）。
            case Func<object?[], object?> direct:
                return direct(args);
            case Delegate @delegate:
                return @delegate.DynamicInvoke(args);
            default:
                throw new InvalidOperationException($"Service member {member} is not callable");
        }
    }

    /// <summary>按成员字典 → 公有属性 → 公有字段的顺序解析成员。</summary>
    public static ResolvedServiceMember ResolveFrom(object implementation, string member)
    {
        if (implementation is IReadOnlyDictionary<string, object?> map)
        {
            return new ResolvedServiceMember(map.GetValueOrDefault(member), implementation);
        }

        var type = implementation.GetType();
        var property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
        if (property is not null)
        {
            return new ResolvedServiceMember(property.GetValue(implementation), implementation);
        }
        var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
        if (field is not null)
        {
            return new ResolvedServiceMember(field.GetValue(implementation), implementation);
        }
        return new ResolvedServiceMember(null, implementation);
    }
}
