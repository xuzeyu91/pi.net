using Pi.Durable.Env;
using Pi.Durable.Storage;

namespace Pi.Durable.Testing;

/// <summary>
/// 与运行器无关的注册面。对应 TS <c>runner.ts</c> 的 <c>StorageConformanceRunner</c>：
/// TS 让 conformance 直接向 Vitest/Jest 的 <c>describe</c>/<c>it</c> 注册；C# 的 xUnit 在
/// <c>[Fact]</c> 或 <c>[Theory]</c> 数据上静态发现用例，无法在运行时向运行器注册。
/// 因此这里保留同名入口，但返回可被 xUnit <c>[Theory(MemberData)]</c> 消费的用例清单，
/// 由宿主的测试类负责把单个用例派发为一次 xUnit 测试。
/// </summary>
public interface IStorageConformanceRunner
{
    /// <summary>注册一个描述块。对应 TS <c>describe</c>。</summary>
    void Describe(string name, Action suite);

    /// <summary>注册一个用例。对应 TS <c>it</c>。</summary>
    void It(string name, Func<Task> test, int? timeoutMs = null);
}

/// <summary>
/// conformance 用例的收集式运行器：把 <c>describe</c> 里的 <c>it</c> 收进清单，
/// 供宿主测试类按用例逐个派发（xUnit 的 <c>[Theory]</c> 数据源）。
/// </summary>
public sealed class CollectedConformanceRunner : IStorageConformanceRunner
{
    private readonly List<(string Suite, string Name, Func<Task> Test, int? TimeoutMs)> _cases = [];
    private string _current = string.Empty;

    /// <summary>收集到的用例：<c>(suite, name, test, timeoutMs)</c>。</summary>
    public IReadOnlyList<(string Suite, string Name, Func<Task> Test, int? TimeoutMs)> Cases => _cases;

    /// <inheritdoc />
    public void Describe(string name, Action suite)
    {
        var previous = _current;
        _current = previous.Length == 0 ? name : $"{previous} > {name}";
        try
        {
            suite();
        }
        finally
        {
            _current = previous;
        }
    }

    /// <inheritdoc />
    public void It(string name, Func<Task> test, int? timeoutMs = null) =>
        _cases.Add((_current, name, test, timeoutMs));
}

/// <summary>conformance 注册辅助。对应 TS <c>registerStorageConformance</c> / <c>registerEnvConformance</c>。</summary>
public static class ConformanceRunner
{
    /// <summary>把与运行器无关的存储用例注册给一个运行器。对应 TS <c>registerStorageConformance</c>。</summary>
    public static void RegisterStorageConformance(
        IStorageConformanceRunner runner,
        string name,
        StorageConformanceProvider withStorage,
        IStorageConformanceAssertions? assertions = null)
    {
        var cases = StorageConformance.CreateStorageConformance(new StorageConformanceOptions
        {
            Assertions = assertions ?? ConformanceAssertions.Create(),
            WithStorage = withStorage,
        });
        runner.Describe(name, () =>
        {
            foreach (var testCase in cases)
            {
                runner.It(testCase.Name, testCase.Run);
            }
        });
    }

    /// <summary>把与运行器无关的环境用例注册给一个运行器。对应 TS <c>registerEnvConformance</c>。</summary>
    public static void RegisterEnvConformance(
        IStorageConformanceRunner runner,
        string name,
        EnvConformanceProvider withEnv,
        IReadOnlyList<string>? shell = null,
        bool? symlinks = null,
        IEnvConformanceAssertions? assertions = null)
    {
        var cases = EnvConformance.CreateEnvConformance(new EnvConformanceOptions
        {
            Assertions = assertions ?? DefaultEnvAssertions(),
            WithEnv = withEnv,
            Shell = shell,
            Symlinks = symlinks,
        });
        runner.Describe(name, () =>
        {
            foreach (var testCase in cases)
            {
                runner.It(testCase.Name, testCase.Run, testCase.TimeoutMs);
            }
        });
    }

    private static IEnvConformanceAssertions DefaultEnvAssertions() => ConformanceAssertions.CreateEnv();
}
