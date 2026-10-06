using Pi.Durable.Env;
using Pi.Durable.Storage;

namespace Pi.Durable.Testing;

/// <summary>
/// conformance 用例使用的断言面。对应 TS <c>StorageConformanceAssertions</c>：
/// 与测试运行器无关，宿主把任意断言库（TS 的 Vitest/Jest、C# 的 xUnit/自定义）适配到这一层。
/// <para>TS 侧 <c>rejects(operation, messageIncludes)</c> 接收一个 <c>Promise</c>（拒绝在 await 时才发生），
/// C# 的 Task 在创建时即已启动，故直接接收 <see cref="Task"/>；语义与 TS 一致：等待它落定并要求
/// 它以包含 <paramref name="messageIncludes"/> 的消息失败。</para>
/// </summary>
public interface IStorageConformanceAssertions
{
    /// <summary>断言值为真。对应 TS <c>ok(value, message?)</c>。</summary>
    void Ok(bool value, string? message = null);

    /// <summary>断言两者同一（C# 精确相等）。对应 TS <c>strictEqual</c> / <c>toBe</c>。</summary>
    void StrictEqual(object? actual, object? expected);

    /// <summary>断言深相等（TS <c>toEqual</c>，含 undefined 键）。对应 TS <c>deepEqual</c>。</summary>
    void DeepEqual(object? actual, object? expected);

    /// <summary>断言深包含（对象子集 / 数组元素匹配）。对应 TS <c>partialDeepEqual</c> / <c>toMatchObject</c>。</summary>
    void PartialDeepEqual(object? actual, object? expected);

    /// <summary>断言数值严格大于。对应 TS <c>greaterThan</c>。</summary>
    void GreaterThan(double actual, double expected);

    /// <summary>等待操作落定，并要求其以包含给定文本的消息失败。对应 TS <c>rejects</c>。</summary>
    Task Rejects(Task operation, string messageIncludes);

    /// <summary>
    /// 延迟形式的 <see cref="Rejects(Task,string)"/>。C# 的 Task 在创建时即已启动，
    /// 同步抛出（如存储的 ID 冲突检查）会在构造实参时逃逸出 <c>Rejects</c> 之外；
    /// 传入 <see cref="Func{Task}"/> 可捕获这类同步失败，语义与 TS 的 Promise 拒绝一致。
    /// </summary>
    Task Rejects(Func<Task> operation, string messageIncludes);
}

/// <summary>存储 conformance 的 <c>withStorage</c> 提供者：为一个用例提供新存储并恰好调用一次回调。对应 TS <c>StorageConformanceProvider</c>。</summary>
public delegate Task StorageConformanceProvider(Func<IStorage, Task> use);

/// <summary>建立存储 conformance 用例的选项。对应 TS <c>StorageConformanceOptions</c>。</summary>
public sealed record StorageConformanceOptions
{
    public required IStorageConformanceAssertions Assertions { get; init; }

    public required StorageConformanceProvider WithStorage { get; init; }
}

/// <summary>一个与运行器无关的存储 conformance 用例。对应 TS <c>StorageConformanceCase</c>。</summary>
public sealed record StorageConformanceCase
{
    public required string Name { get; init; }

    public required Func<Task> Run { get; init; }
}

/// <summary>环境 conformance 与存储 conformance 共用断言面。对应 TS <c>EnvConformanceAssertions = StorageConformanceAssertions</c>。</summary>
public interface IEnvConformanceAssertions : IStorageConformanceAssertions;

/// <summary>
/// 环境 conformance 的 <c>withEnv</c> 提供者：为一个用例提供 <c>cwd</c> 为全新、可写空目录的环境，
/// 然后清理，恰好调用一次回调。对应 TS <c>EnvConformanceProvider</c>。
/// </summary>
public delegate Task EnvConformanceProvider(Func<IExecutionEnv, Task> use);

/// <summary>建立环境 conformance 用例的选项。对应 TS <c>EnvConformanceOptions</c>。</summary>
public sealed record EnvConformanceOptions
{
    public required IEnvConformanceAssertions Assertions { get; init; }

    public required EnvConformanceProvider WithEnv { get; init; }

    /// <summary>从下一参数运行 POSIX shell 脚本的程序与标志；缺省 <c>["sh", "-c"]</c>。</summary>
    public IReadOnlyList<string>? Shell { get; init; }

    /// <summary>shell 的 <c>ln -s</c> 是否创建符号链接；缺省 true。</summary>
    public bool? Symlinks { get; init; }
}

/// <summary>一个与运行器无关的环境 conformance 用例。对应 TS <c>EnvConformanceCase</c>。</summary>
public sealed record EnvConformanceCase
{
    public required string Name { get; init; }

    /// <summary>等待环境 watch 延迟的用例需要比测试运行器默认超时更长。</summary>
    public int? TimeoutMs { get; init; }

    public required Func<Task> Run { get; init; }
}
