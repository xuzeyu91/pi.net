using Pi.Chord.Delta;
using Pi.Durable.Storage;
using Pi.Durable.Types;
using DurableIds = Pi.Durable.DurableIds;
using Path = Pi.Chord.Delta.Path;
using TaskStatus = Pi.Durable.Types.TaskStatus;

namespace Pi.Durable.Testing;

/// <summary>一组基准规模：条目 / 任务 / 文档计数。对应 TS <c>StorageBenchmarkScale</c>。</summary>
public sealed record StorageBenchmarkScale(string Name, int EntryCount, int TaskCount, int DocumentCount);

/// <summary>
/// 通过公开 <see cref="IStorage"/> 契约播种的确定性代表性数据集句柄。对应 TS <c>StorageBenchmarkDataset</c>。
/// </summary>
public sealed record StorageBenchmarkDataset
{
    public required EntryId FirstEntryId { get; init; }

    public required int FilteredTaskCount { get; init; }

    public required DocumentId ExactDocumentId { get; init; }

    public required string ExactDocumentKey { get; init; }

    public required IReadOnlyDictionary<int, DocumentId> ReplayDocumentIds { get; init; }

    public required DocumentId HistoricalDocumentId { get; init; }

    public required Seq AncientAt { get; init; }

    public required Seq RecentAt { get; init; }

    public required ConversationId DeepestConversationId { get; init; }

    public required EntryId AncestorHeadEntryId { get; init; }
}

/// <summary>一个读基准样本：跑一次并给出期望值。对应 TS <c>StorageReadBenchmark</c>。</summary>
public sealed record StorageReadBenchmark(
    string Name,
    Func<IStorage, StorageBenchmarkDataset, Task<double>> Run,
    Func<StorageBenchmarkDataset, double> Expected);

/// <summary>一个写基准样本：跑一次并给出期望值。对应 TS <c>StorageWriteBenchmark</c>。</summary>
public sealed record StorageWriteBenchmark(
    string Name,
    Func<IStorage, Task<double>> Run,
    double Expected);

/// <summary>
/// 存储基准。对应 TS <c>testing/storage-benchmark.ts</c>：只经公开 <see cref="IStorage"/> 契约
/// 播种确定性代表性数据，并提供读 / 写基准样本表。
/// </summary>
public static class StorageBenchmark
{
    private static Pi.Chord.Context.Context Context => Pi.Chord.Context.Context.Background;

    /// <summary>内存规模（1k / 10k 条目）。对应 TS <c>STORAGE_MEMORY_SCALES</c>。</summary>
    public static readonly IReadOnlyList<StorageBenchmarkScale> MemoryScales =
    [
        new("1k", 1_000, 200, 200),
        new("10k", 10_000, 2_000, 2_000),
    ];

    /// <summary>计时规模（1k 条目 / 300 任务 / 300 文档）。对应 TS <c>TIMING_SCALE</c>。</summary>
    public static readonly StorageBenchmarkScale TimingScale = new("timing", 1_000, 300, 300);

    private static readonly int[] ReplayTails = [0, 16, 128, 1_024];
    private const int HistorySegmentLength = 128;
    private const int ForkDepth = 8;
    private const int EntriesPerFork = 32;
    private const int BatchSize = 100;

    /// <summary>某个规模播种的“主记录”总数。对应 TS <c>storageBenchmarkPrimaryRecordCount</c>。</summary>
    public static int PrimaryRecordCount(StorageBenchmarkScale scale) =>
        1
        + scale.EntryCount
        + scale.TaskCount
        + scale.DocumentCount
        + ReplayTails.Length
        + 1
        + ForkDepth * (1 + EntriesPerFork);

    private static TaskRecord TaskRecord(TaskId<object?> id, int index)
    {
        var statuses = new[] { TaskStatus.Pending, TaskStatus.Running, TaskStatus.Terminal };
        var status = statuses[index % statuses.Length];
        var common = new TaskRecord
        {
            Id = id,
            ConversationId = RootConversationId,
            Kind = index % 4 == 0 ? "benchmark.filtered" : "benchmark.other",
            Version = 1,
            Input = new Dictionary<string, object?> { ["index"] = (long)index },
            Background = index % 5 == 0,
            AbortRequested = index % 7 == 0,
            State = new TaskState { Status = status },
        };
        if (status == TaskStatus.Terminal)
        {
            return common with
            {
                State = new TaskState
                {
                    Status = status,
                    Outcome = new TaskOutcome
                    {
                        Status = TaskOutcomeStatus.Completed,
                        Result = new Dictionary<string, object?> { ["index"] = (long)index },
                    },
                },
            };
        }

        return common with
        {
            State = new TaskState
            {
                Status = status,
                Checkpoint = new Dictionary<string, object?>
                {
                    ["index"] = (long)index,
                    ["payload"] = new string('x', 64),
                },
            },
        };
    }

    /// <summary>只经公开 <see cref="IStorage"/> 契约播种确定性代表性数据。对应 TS <c>seedStorageBenchmark</c>。</summary>
    public static async Task<StorageBenchmarkDataset> SeedAsync(
        IStorage storage,
        StorageBenchmarkScale? scale = null)
    {
        scale ??= TimingScale;
        await storage.CommitAsync([new StorageWrite.Conversation(new ConversationRecord { Id = RootConversationId })]);

        EntryId? firstEntryId = null;
        for (var start = 0; start < scale.EntryCount; start += BatchSize)
        {
            var writes = new List<StorageWrite>();
            for (var index = start; index < Math.Min(start + BatchSize, scale.EntryCount); index++)
            {
                var id = await storage.MintIdAsync<EntryId>();
                if (index == 0) firstEntryId = id;
                writes.Add(new StorageWrite.Entry(new EntryRecord
                {
                    Id = id,
                    ConversationId = RootConversationId,
                    Kind = "benchmark.entry",
                    Head = index == 0 ? id : null,
                    Data = new Dictionary<string, object?>
                    {
                        ["index"] = (long)index,
                        ["text"] = $"entry-{index}-{new string('x', 96)}",
                    },
                }));
            }

            await storage.CommitAsync(writes);
        }

        for (var start = 0; start < scale.TaskCount; start += BatchSize)
        {
            var writes = new List<StorageWrite>();
            for (var index = start; index < Math.Min(start + BatchSize, scale.TaskCount); index++)
            {
                writes.Add(new StorageWrite.Task(TaskRecord(await storage.MintIdAsync<TaskId<object?>>(), index)));
            }

            await storage.CommitAsync(writes);
        }

        DocumentId? exactDocumentId = null;
        for (var start = 0; start < scale.DocumentCount; start += BatchSize)
        {
            var writes = new List<StorageWrite>();
            for (var index = start; index < Math.Min(start + BatchSize, scale.DocumentCount); index++)
            {
                var id = await storage.MintIdAsync<DocumentId>();
                exactDocumentId = id;
                writes.Add(new StorageWrite.DocumentCreateWrite(
                    new DocumentCreate
                    {
                        Id = id,
                        Kind = "benchmark.family",
                        Key = $"key-{index}",
                        Scope = new DocumentScope.SessionScope(),
                    },
                    new DocumentContent.Base(1, new Dictionary<string, object?>
                    {
                        ["index"] = (long)index,
                        ["text"] = new string('x', 128),
                    })));
            }

            await storage.CommitAsync(writes);
        }

        var replayEntries = new List<(int Tail, DocumentId Id)>();
        foreach (var tail in ReplayTails)
        {
            replayEntries.Add((tail, await storage.MintIdAsync<DocumentId>()));
        }

        await storage.CommitAsync(
            replayEntries.Select(entry => (StorageWrite)new StorageWrite.DocumentCreateWrite(
                new DocumentCreate
                {
                    Id = entry.Id,
                    Kind = "benchmark.replay",
                    Key = entry.Tail.ToString(),
                    Scope = new DocumentScope.ConversationScope(RootConversationId),
                    History = ConversationHistory.Rewindable,
                    Fork = ConversationFork.AsOf,
                },
                new DocumentContent.Base(1, new Dictionary<string, object?>
                {
                    ["count"] = 0L,
                    ["text"] = new string('x', 64),
                }))).ToList());

        for (var count = 1; count <= ReplayTails[^1]; count++)
        {
            await storage.CommitAsync(
                replayEntries
                    .Where(entry => count <= entry.Tail)
                    .Select(entry => (StorageWrite)new StorageWrite.DocumentChange(
                        entry.Id,
                        new DocumentContent.Delta(1, [new DeltaOp.Set(P("count"), (long)count)])))
                    .ToList());
        }

        var historicalDocumentId = await storage.MintIdAsync<DocumentId>();
        var historicalRecord = new DocumentCreate
        {
            Id = historicalDocumentId,
            Kind = "benchmark.history",
            Scope = new DocumentScope.ConversationScope(RootConversationId),
            History = ConversationHistory.Rewindable,
            Fork = ConversationFork.AsOf,
        };
        await storage.CommitAsync(
            [
                new StorageWrite.DocumentCreateWrite(
                    historicalRecord,
                    new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = 0L })),
            ]);

        Seq? ancientAt = null;
        for (var count = 1; count <= HistorySegmentLength; count++)
        {
            ancientAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(
                        historicalDocumentId,
                        new DocumentContent.Delta(1, [new DeltaOp.Set(P("count"), (long)count)])),
                ]);
        }

        await storage.CommitAsync(
            [
                new StorageWrite.DocumentChange(
                    historicalDocumentId,
                    new DocumentContent.Base(1, new Dictionary<string, object?> { ["count"] = (long)HistorySegmentLength })),
            ]);
        if (ancientAt is null) throw new InvalidOperationException("Benchmark history seed produced no commits");
        var recentAt = ancientAt.Value;
        for (var count = HistorySegmentLength + 1; count <= HistorySegmentLength * 2; count++)
        {
            recentAt = await storage.CommitAsync(
                [
                    new StorageWrite.DocumentChange(
                        historicalDocumentId,
                        new DocumentContent.Delta(1, [new DeltaOp.Set(P("count"), (long)count)])),
                ]);
        }

        if (firstEntryId is null) throw new InvalidOperationException("Benchmark scale must create entries");
        var parentConversationId = RootConversationId;
        var parentAt = firstEntryId.Value;
        var deepestConversationId = RootConversationId;
        for (var depth = 0; depth < ForkDepth; depth++)
        {
            var conversationId = await storage.MintIdAsync<ConversationId>();
            await storage.CommitAsync(
                [
                    new StorageWrite.Conversation(new ConversationRecord
                    {
                        Id = conversationId,
                        Parent = new ConversationParent { ConversationId = parentConversationId, At = parentAt },
                    }),
                ]);
            var ids = new List<EntryId>();
            for (var i = 0; i < EntriesPerFork; i++)
            {
                ids.Add(await storage.MintIdAsync<EntryId>());
            }

            await storage.CommitAsync(
                ids.Select((id, index) => (StorageWrite)new StorageWrite.Entry(new EntryRecord
                {
                    Id = id,
                    ConversationId = conversationId,
                    Kind = "benchmark.fork",
                    Data = new Dictionary<string, object?>
                    {
                        ["depth"] = (long)depth,
                        ["index"] = (long)index,
                    },
                })).ToList());
            parentConversationId = conversationId;
            parentAt = ids[^1];
            deepestConversationId = conversationId;
        }

        if (exactDocumentId is null) throw new InvalidOperationException("Benchmark scale must create documents");
        return new StorageBenchmarkDataset
        {
            FirstEntryId = firstEntryId.Value,
            FilteredTaskCount = Math.Min(50, (int)Math.Ceiling(scale.TaskCount / 60.0)),
            ExactDocumentId = exactDocumentId.Value,
            ExactDocumentKey = $"key-{scale.DocumentCount - 1}",
            ReplayDocumentIds = replayEntries.ToDictionary(entry => entry.Tail, entry => entry.Id),
            HistoricalDocumentId = historicalDocumentId,
            AncientAt = ancientAt.Value,
            RecentAt = recentAt,
            DeepestConversationId = deepestConversationId,
            AncestorHeadEntryId = firstEntryId.Value,
        };
    }

    /// <summary>读基准样本表。对应 TS <c>STORAGE_READ_BENCHMARKS</c>。</summary>
    public static readonly IReadOnlyList<StorageReadBenchmark> ReadBenchmarks = BuildReadBenchmarks();

    private static List<StorageReadBenchmark> BuildReadBenchmarks()
    {
        var benchmarks = new List<StorageReadBenchmark>
        {
            new(
                "exact entry lookup",
                async (storage, dataset) => (await storage.GetEntryAsync(dataset.FirstEntryId))?.Entry.Id.Value ?? -1,
                dataset => dataset.FirstEntryId.Value),
            new(
                "entry page scan (100)",
                async (storage, _) => (await storage.ScanEntriesAsync(new EntryQuery { ConversationId = RootConversationId }, 100)).Items.Count,
                _ => 100),
            new(
                "filtered task scan (50)",
                async (storage, _) => (await storage.ScanTasksAsync(
                    new TaskQuery { Kind = "benchmark.filtered", Status = TaskStatus.Pending, Background = true }, 50)).Items.Count,
                dataset => dataset.FilteredTaskCount),
            new(
                "exact document address among many",
                async (storage, dataset) => (await storage.FindDocumentAsync(
                    new DocumentAddress { Kind = "benchmark.family", Key = dataset.ExactDocumentKey, Scope = new DocumentScope.SessionScope() },
                    new DocumentPoint.Current()))?.Id.Value ?? -1,
                dataset => dataset.ExactDocumentId.Value),
        };

        foreach (var tail in ReplayTails)
        {
            benchmarks.Add(new StorageReadBenchmark(
                $"document replay tail ({tail})",
                async (storage, dataset) => Convert.ToDouble(
                    (await storage.GetDocumentAsync(dataset.ReplayDocumentIds[tail], new DocumentPoint.Current()))?.Value["count"]),
                _ => tail));
        }

        benchmarks.Add(new StorageReadBenchmark(
            "ancient historical read before newer base",
            async (storage, dataset) => Convert.ToDouble(
                (await storage.GetDocumentAsync(dataset.HistoricalDocumentId, new DocumentPoint.AtSeq(dataset.AncientAt)))?.Value["count"]),
            _ => HistorySegmentLength));
        benchmarks.Add(new StorageReadBenchmark(
            "recent historical read after newer base",
            async (storage, dataset) => Convert.ToDouble(
                (await storage.GetDocumentAsync(dataset.HistoricalDocumentId, new DocumentPoint.AtSeq(dataset.RecentAt)))?.Value["count"]),
            _ => HistorySegmentLength * 2));
        benchmarks.Add(new StorageReadBenchmark(
            "fork-depth history scan (100)",
            async (storage, dataset) => (await storage.ScanEntriesAsync(
                new EntryQuery { ConversationId = dataset.DeepestConversationId }, 100)).Items.Count,
            _ => 100));
        benchmarks.Add(new StorageReadBenchmark(
            "fork-depth head lookup",
            async (storage, dataset) => (await storage.FindLatestHeadMarkerAsync(dataset.DeepestConversationId, null))?.Id.Value ?? -1,
            dataset => dataset.AncestorHeadEntryId.Value));

        return benchmarks;
    }

    /// <summary>播种每个写基准样本都期望的公共状态。对应 TS <c>seedStorageWriteBenchmark</c>。</summary>
    public static async Task SeedWriteBenchmarkAsync(IStorage storage)
    {
        await storage.CommitAsync([new StorageWrite.Conversation(new ConversationRecord { Id = RootConversationId })]);
        var writes = new List<StorageWrite>();
        for (var index = 0; index < 100; index++)
        {
            writes.Add(new StorageWrite.Entry(new EntryRecord
            {
                Id = await storage.MintIdAsync<EntryId>(),
                ConversationId = RootConversationId,
                Kind = "benchmark.baseline",
                Data = new Dictionary<string, object?> { ["index"] = (long)index },
            }));
        }

        await storage.CommitAsync(writes);
    }

    /// <summary>写基准样本表。对应 TS <c>STORAGE_WRITE_BENCHMARKS</c>。</summary>
    public static readonly IReadOnlyList<StorageWriteBenchmark> WriteBenchmarks =
    [
        new(
            "commit one entry",
            async storage =>
            {
                var id = await storage.MintIdAsync<EntryId>();
                await storage.CommitAsync(
                    [
                        new StorageWrite.Entry(new EntryRecord
                        {
                            Id = id,
                            ConversationId = RootConversationId,
                            Kind = "benchmark.write",
                            Data = new Dictionary<string, object?> { ["text"] = new string('x', 128) },
                        }),
                    ]);
                return 1;
            },
            1),
        new(
            "commit 100 entries",
            async storage =>
            {
                var writes = new List<StorageWrite>();
                for (var index = 0; index < 100; index++)
                {
                    writes.Add(new StorageWrite.Entry(new EntryRecord
                    {
                        Id = await storage.MintIdAsync<EntryId>(),
                        ConversationId = RootConversationId,
                        Kind = "benchmark.write",
                        Data = new Dictionary<string, object?>
                        {
                            ["index"] = (long)index,
                            ["text"] = new string('x', 128),
                        },
                    }));
                }

                await storage.CommitAsync(writes);
                return writes.Count;
            },
            100),
        new(
            "commit mixed entry/task/submission/document",
            async storage =>
            {
                var entryId = await storage.MintIdAsync<EntryId>();
                var taskId = await storage.MintIdAsync<TaskId<object?>>();
                var submissionId = await storage.MintIdAsync<SubmissionId>();
                var documentId = await storage.MintIdAsync<DocumentId>();
                IReadOnlyList<StorageWrite> writes =
                [
                    new StorageWrite.Entry(new EntryRecord
                    {
                        Id = entryId,
                        ConversationId = RootConversationId,
                        Kind = "benchmark.mixed",
                    }),
                    new StorageWrite.Task(TaskRecord(taskId, (int)taskId.Value)),
                    new StorageWrite.Submission(new SubmissionRecord.WriteRecord
                    {
                        Id = submissionId,
                        ConversationId = RootConversationId,
                        RequestId = $"benchmark-{submissionId.Value}",
                        Status = SubmissionStatus.Done,
                        Entry = entryId,
                    }),
                    new StorageWrite.DocumentCreateWrite(
                        new DocumentCreate
                        {
                            Id = documentId,
                            Kind = "benchmark.mixed",
                            Key = documentId.Value.ToString(),
                            Scope = new DocumentScope.SessionScope(),
                        },
                        new DocumentContent.Base(1, new Dictionary<string, object?>
                        {
                            ["entryId"] = entryId.Value,
                            ["taskId"] = taskId.Value,
                        })),
                ];
                await storage.CommitAsync(writes);
                return writes.Count;
            },
            4),
    ];

    /// <summary>根对话 ID（TS <c>ROOT_CONVERSATION_ID</c>）。</summary>
    public static ConversationId RootConversationId { get; } = DurableIds.ConversationId(1);

    private static Path P(params object[] segments) =>
        new([.. segments.Select(segment => segment switch
        {
            long index => Seg.Index(index),
            int index => Seg.Index(index),
            _ => Seg.Key((string)segment),
        })]);
}
