using Pi.Durable.Types;
using Pi.Durable;
using Xunit;

namespace Pi.Durable.Tests;

/// <summary>
/// 基础层测试。truncate 部分对应 TS <c>test/env-truncate.test.ts</c>（无 Buffer 回退用例
/// 不适用——C# <see cref="System.Text.Encoding.UTF8"/> 即权威实现）；documents 部分覆盖
/// <c>documents.ts</c> 的地址解析 / 创建记录 / 校验 / 物化（TS 侧经 session 测试间接覆盖）。
/// </summary>
public class TruncateAndDocumentsTests
{
    private static long ByteLength(string content) => System.Text.Encoding.UTF8.GetByteCount(content);

    [Fact]
    public void ReportsUtf8ByteCountsInTruncationResults()
    {
        var content = "aé🙂\nb";
        var result = Truncate.TruncateHead(content, new Truncate.TruncationOptions { MaxBytes = 100, MaxLines = 10 });

        Assert.False(result.Truncated);
        Assert.Equal(ByteLength(content), result.TotalBytes);
        Assert.Equal(ByteLength(content), result.OutputBytes);
        Assert.Equal(9, result.TotalBytes);
    }

    [Fact]
    public void DoesNotCountTrailingNewlineAsExtraLine()
    {
        var content = $"{string.Join("\n", Enumerable.Repeat("line", 3))}\n";
        var head = Truncate.TruncateHead(content, new Truncate.TruncationOptions { MaxBytes = 100, MaxLines = 3 });

        Assert.False(head.Truncated);
        Assert.Equal(3, head.TotalLines);
        Assert.Equal(3, head.OutputLines);
    }

    [Fact]
    public void TruncatesHeadByLineLimits()
    {
        var result = Truncate.TruncateHead("one\ntwo\nthree\nfour",
            new Truncate.TruncationOptions { MaxBytes = 100, MaxLines = 2 });

        Assert.Equal("one\ntwo", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal(Truncate.TruncatedBy.Lines, result.TruncatedBy);
        Assert.Equal(4, result.TotalLines);
        Assert.Equal(2, result.OutputLines);
    }

    [Fact]
    public void ReportsBytesWhenOnlyTrailingNewlineExceedsLimitsAtLineCap()
    {
        var result = Truncate.TruncateHead("hello\nworld\n",
            new Truncate.TruncationOptions { MaxBytes = 11, MaxLines = 2 });

        Assert.Equal("hello\nworld", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal(Truncate.TruncatedBy.Bytes, result.TruncatedBy);
        Assert.Equal(2, result.TotalLines);
        Assert.Equal(2, result.OutputLines);
    }

    [Fact]
    public void TruncatesHeadOnUtf8ByteLimitsWithoutPartialLines()
    {
        var result = Truncate.TruncateHead("éé\nabc",
            new Truncate.TruncationOptions { MaxBytes = 4, MaxLines = 10 });

        Assert.Equal("éé", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal(Truncate.TruncatedBy.Bytes, result.TruncatedBy);
        Assert.Equal(4, result.OutputBytes);
        Assert.False(result.FirstLineExceedsLimit);
    }

    [Fact]
    public void ReportsHeadTruncationWhenFirstLineExceedsByteLimit()
    {
        var result = Truncate.TruncateHead("éé\nabc",
            new Truncate.TruncationOptions { MaxBytes = 3, MaxLines = 10 });

        Assert.Equal("", result.Content);
        Assert.True(result.Truncated);
        Assert.Equal(Truncate.TruncatedBy.Bytes, result.TruncatedBy);
        Assert.True(result.FirstLineExceedsLimit);
    }

    [Fact]
    public void FormatsSizes()
    {
        Assert.Equal("1023B", Truncate.FormatSize(1023));
        Assert.Equal("1.5KB", Truncate.FormatSize(1536));
        Assert.Equal("3.0MB", Truncate.FormatSize(3 * 1024 * 1024));
    }

    [Fact]
    public void Utf8ByteLengthMatchesEncoding()
    {
        foreach (var input in new[] { "", "ascii", "é", "中", "🙂", "a\U0001F642b", "߿ࠀ￿" })
        {
            Assert.Equal(ByteLength(input), Truncate.Utf8ByteLength(input));
        }
    }

    // ---- documents.ts ----

    private static DurableDocuments.AnyDocDefinition SessionDefinition(bool family = false)
        => new()
        {
            Kind = "doc.session",
            Version = 1,
            Semantics = new DocumentSemantics.SessionScope(),
            Family = family,
            Initial = _ => new Dictionary<string, object?>(),
        };

    [Fact]
    public void ResolveAddressCoversSessionConversationAndTaskScopes()
    {
        var session = DurableDocuments.ResolveAddress(SessionDefinition(), []);
        Assert.Equal("[\"doc.session\",\"session\",null,null]", session.Id);
        Assert.Null(session.Address.Key);
        Assert.Equal(0, session.NextArgument);

        var conversationDefinition = new DurableDocuments.AnyDocDefinition
        {
            Kind = "doc.conversation",
            Version = 1,
            Semantics = new DocumentSemantics.LatestConversationScope(ConversationFork.Current),
            Family = false,
            Initial = _ => new Dictionary<string, object?>(),
        };
        var conversation = DurableDocuments.ResolveAddress(conversationDefinition, [7L]);
        Assert.Equal("[\"doc.conversation\",\"conversation\",7,null]", conversation.Id);
        Assert.Equal(1, conversation.NextArgument);

        var family = DurableDocuments.ResolveAddress(SessionDefinition(family: true), ["member-1"]);
        Assert.Equal("[\"doc.session\",\"session\",null,\"member-1\"]", family.Id);
        Assert.Equal("member-1", family.Address.Key);
        Assert.Equal(1, family.NextArgument);
    }

    [Fact]
    public void ResolveAddressRejectsNonIntegerOwner()
    {
        var definition = new DurableDocuments.AnyDocDefinition
        {
            Kind = "doc.task",
            Version = 1,
            Semantics = new DocumentSemantics.TaskScope(),
            Family = false,
            Initial = _ => new Dictionary<string, object?>(),
        };
        Assert.Throws<ArgumentException>(() => DurableDocuments.ResolveAddress(definition, ["not-an-id"]));
    }

    [Fact]
    public void DocumentCreateStampsHistoryAndForkForConversationScope()
    {
        var definition = new DurableDocuments.AnyDocDefinition
        {
            Kind = "doc.conversation",
            Version = 2,
            Semantics = new DocumentSemantics.RewindableConversationScope(ConversationFork.AsOf),
            Family = false,
            Initial = _ => new Dictionary<string, object?>(),
        };
        var address = new DocumentAddress
        {
            Kind = definition.Kind,
            Scope = new DocumentScope.ConversationScope(ConversationId.From(9)),
        };
        var create = DurableDocuments.DocumentCreate(definition, address, DocumentId.From(3));

        Assert.Equal(DocumentId.From(3), create.Id);
        Assert.Equal(ConversationHistory.Rewindable, create.History);
        Assert.Equal(ConversationFork.AsOf, create.Fork);
    }

    [Fact]
    public void CheckRecordScopeRejectsMismatchedSemantics()
    {
        var definition = SessionDefinition();
        var record = new DocumentRecord
        {
            Id = DocumentId.From(1),
            Kind = definition.Kind,
            Scope = new DocumentScope.TaskScope(TaskId<object?>.From(2)),
            CreatedAt = Seq.From(1),
        };
        Assert.Throws<ArgumentException>(() => DurableDocuments.CheckRecordScope(definition, record));
    }

    [Fact]
    public void CheckRecordVersionRejectsNewerAndUnmigratableOlder()
    {
        var definition = new DurableDocuments.AnyDocDefinition
        {
            Kind = "doc.session",
            Version = 2,
            Semantics = new DocumentSemantics.SessionScope(),
            Family = false,
            Initial = _ => new Dictionary<string, object?>(),
        };
        var record = new DocumentRecord
        {
            Id = DocumentId.From(1),
            Kind = definition.Kind,
            Scope = new DocumentScope.SessionScope(),
            CreatedAt = Seq.From(1),
        };
        Assert.Throws<InvalidOperationException>(() => DurableDocuments.CheckRecordVersion(definition, record, 3));
        Assert.Throws<InvalidOperationException>(() => DurableDocuments.CheckRecordVersion(definition, record, 1));
    }

    [Fact]
    public void MaterializeDocumentValueMigratesOlderVersions()
    {
        var definition = new DurableDocuments.AnyDocDefinition
        {
            Kind = "doc.session",
            Version = 2,
            Semantics = new DocumentSemantics.SessionScope(),
            Family = false,
            Initial = _ => new Dictionary<string, object?>(),
            Migrate = (value, from) => new Dictionary<string, object?>(value) { ["migratedFrom"] = (double)from },
        };
        var record = new DocumentRecord
        {
            Id = DocumentId.From(1),
            Kind = definition.Kind,
            Scope = new DocumentScope.SessionScope(),
            CreatedAt = Seq.From(1),
        };
        var stored = new Dictionary<string, object?> { ["kept"] = true };

        var same = DurableDocuments.MaterializeDocumentValue(definition, record, 2, stored);
        Assert.Same(stored, same);

        var migrated = DurableDocuments.MaterializeDocumentValue(definition, record, 1, stored);
        Assert.Equal(true, migrated["kept"]);
        Assert.Equal(1d, migrated["migratedFrom"]);
    }

    [Fact]
    public void DefineEntryValidatesKindAndGuardsByKind()
    {
        Assert.Throws<ArgumentException>(() => DurableEntries.DefineEntry<object?>(""));
        var entry = DurableEntries.DefineEntry<object?>("custom.kind");

        Assert.True(entry.Is(new EntryRecord
        {
            Id = EntryId.From(1),
            ConversationId = ConversationId.From(1),
            Kind = "custom.kind",
        }));
        Assert.False(entry.Is(new EntryRecord
        {
            Id = EntryId.From(2),
            ConversationId = ConversationId.From(1),
            Kind = "other",
        }));
        Assert.False(entry.Is(null));
    }

    [Fact]
    public void BuiltinEntriesCarryStableKinds()
    {
        Assert.Equal("pi.user", DurableEntries.UserEntry.Kind);
        Assert.Equal("pi.assistant", DurableEntries.AssistantEntry.Kind);
        Assert.Equal("pi.system", DurableEntries.SystemEntry.Kind);
        Assert.Equal("pi.tool-result", DurableEntries.ToolResultEntry.Kind);
        Assert.Equal("pi.reset", DurableEntries.ResetEntry.Kind);
        Assert.Equal("pi.compaction", DurableEntries.CompactionEntry.Kind);
    }

    [Fact]
    public void DefineDocRejectsNonPositiveVersion()
    {
        Assert.Throws<ArgumentException>(() => DurableDocuments.DefineDoc(new DocDefinition<Dictionary<string, object?>>
        {
            Kind = "doc.bad",
            Version = 0,
            Semantics = new DocumentSemantics.SessionScope(),
            Initial = () => [],
        }));
    }

    [Fact]
    public void RootConversationIdIsOne()
    {
        Assert.Equal(1L, DurableIdConstants.RootConversation.Value);
    }
}
