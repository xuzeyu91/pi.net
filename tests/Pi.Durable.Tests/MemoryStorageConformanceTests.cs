using Pi.Durable.Storage;
using Pi.Durable.Testing;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 存储契约测试。对应 TS <c>storage/memory.test.ts</c> 经
/// <c>registerStorageConformance</c> 注册的全部 <c>testing/storage-conformance.ts</c> 用例。
/// <para>与 <see cref="MemoryStorageTests"/> 的区别：后者是本仓既有的手写单元测试，
/// 本类直接消费移植后的 conformance 套件，保证与 TS 参考实现的用例名与判据逐条一致。</para>
/// </summary>
public class MemoryStorageConformanceTests
{
    private static IReadOnlyList<StorageConformanceCase> Cases() =>
        StorageConformance.CreateStorageConformance(new StorageConformanceOptions
        {
            Assertions = ConformanceAssertions.Create(),
            WithStorage = async use =>
            {
                var storage = new MemoryStorage();
                await use(storage);
            },
        });

    public static IEnumerable<object[]> CaseNames() =>
        Cases().Select(@case => new object[] { @case.Name });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task PassesStorageConformanceSuite(string caseKey)
    {
        var @case = Cases().Single(candidate => candidate.Name == caseKey);
        await @case.Run();
    }
}
