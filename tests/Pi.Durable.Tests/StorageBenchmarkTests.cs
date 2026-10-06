using Pi.Durable.Storage;
using Pi.Durable.Testing;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 存储基准移植的冒烟测试。对应 TS <c>testing/storage-benchmark.ts</c>：
/// 只经公开 <see cref="IStorage"/> 契约播种确定性数据，并校验每个读 / 写基准样本的
/// 实际返回值与声明期望值一致（不度量时间）。
/// <para>用最小的自定义规模（而非 1k/10k）以免测试变慢：基准的契约是“播种后读/写结果
/// 与 expected 一致”，规模只影响耗时。</para>
/// </summary>
public class StorageBenchmarkTests
{
    private static readonly StorageBenchmarkScale SmokeScale = new("smoke", EntryCount: 400, TaskCount: 120, DocumentCount: 120);

    [Fact]
    public async Task SeedsDatasetWhoseReadBenchmarksMatchExpectations()
    {
        var storage = new MemoryStorage();
        var dataset = await StorageBenchmark.SeedAsync(storage, SmokeScale);

        foreach (var benchmark in StorageBenchmark.ReadBenchmarks)
        {
            var actual = await benchmark.Run(storage, dataset);
            Assert.Equal(benchmark.Expected(dataset), actual);
        }
    }

    [Fact]
    public async Task SeedsWriteBenchmarkStateWhoseSamplesMatchExpectations()
    {
        var storage = new MemoryStorage();
        await StorageBenchmark.SeedWriteBenchmarkAsync(storage);

        foreach (var benchmark in StorageBenchmark.WriteBenchmarks)
        {
            var actual = await benchmark.Run(storage);
            Assert.Equal(benchmark.Expected, actual);
        }
    }

    [Fact]
    public void PrimaryRecordCountMatchesScale()
    {
        Assert.Equal(1 + 1_000 + 300 + 300 + 4 + 1 + 8 * (1 + 32), StorageBenchmark.PrimaryRecordCount(StorageBenchmark.TimingScale));
    }
}
