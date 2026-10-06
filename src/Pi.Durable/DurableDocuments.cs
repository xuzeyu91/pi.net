using Pi.Chord;
using Pi.Durable.Types;

namespace Pi.Durable;

/// <summary>文档定义与地址解析。对应 TS <c>documents.ts</c>。</summary>
public static class DurableDocuments
{
    /// <summary>定义单例文档。对应 TS <c>defineDoc</c>。</summary>
    public static DocToken<T> DefineDoc<T>(DocDefinition<T> definition)
        where T : class, IReadOnlyDictionary<string, object?>
    {
        ValidateDefinition(definition.Kind, definition.Version);
        return new DocToken<T> { Definition = definition };
    }

    /// <summary>定义键控文档族。对应 TS <c>defineDocFamily</c>。</summary>
    public static DocFamilyToken<T, TSeed> DefineDocFamily<T, TSeed>(
        DocFamilyDefinition<T, TSeed> definition) where T : class, IReadOnlyDictionary<string, object?>
    {
        ValidateDefinition(definition.Kind, definition.Version);
        return new DocFamilyToken<T, TSeed> { Definition = definition };
    }

    private static void ValidateDefinition(string kind, int version)
    {
        // 对应 TS：version 必须是安全正整数（int 范围内恒满足 safe integer 上界）。
        if (version < 1)
            throw new ArgumentException($"Document {kind} version must be a positive integer", nameof(version));
    }

    /// <summary>逻辑地址 + 其字符串身份（map 键）。对应 TS <c>ResolvedAddress</c>。</summary>
    public sealed record ResolvedAddress
    {
        public required DocumentAddress Address { get; init; }

        /// <summary>稳定字符串身份。</summary>
        public required string Id { get; init; }

        /// <summary>所有者与族键之后的参数索引。</summary>
        public required int NextArgument { get; init; }
    }

    /// <summary>解析重载参数列表并返回所有者与族键之后的索引。对应 TS <c>resolveAddress</c>。</summary>
    /// <param name="definition">文档定义（提供 scope / kind / family）。</param>
    /// <param name="args">
    /// 类型化访问的位置参数（C# 侧由调用方显式提供所有者 ID 与族键；
    /// 会话作用域传空数组，对话 / 任务作用域传 <c>[ownerId]</c>，族追加 <c>[key]</c>）。
    /// </param>
    public static ResolvedAddress ResolveAddress(AnyDocDefinition definition, IReadOnlyList<object?> args)
    {
        var index = 0;
        DocumentScope scope;
        switch (definition.Semantics)
        {
            case DocumentSemantics.SessionScope:
                scope = new DocumentScope.SessionScope();
                break;
            case DocumentSemantics.LatestConversationScope:
            case DocumentSemantics.RewindableConversationScope:
                scope = new DocumentScope.ConversationScope(OwnerId<ConversationId>(args[index++], definition));
                break;
            default:
                scope = new DocumentScope.TaskScope(OwnerId<TaskId<object?>>(args[index++], definition));
                break;
        }
        var key = definition.Family ? (string?)args[index++] : null;
        var address = key is null
            ? new DocumentAddress { Kind = definition.Kind, Scope = scope }
            : new DocumentAddress { Kind = definition.Kind, Scope = scope, Key = key };
        return new ResolvedAddress { Address = address, Id = AddressId(address), NextArgument = index };
    }

    private static TId OwnerId<TId>(object? value, AnyDocDefinition definition)
    {
        // 类型擦除边界：按目标品牌类型分派（对应 TS idFromNumber 的 as 擦除）。
        if (value is long id && id is >= -9007199254740991 and <= 9007199254740991)
        {
            if (typeof(TId) == typeof(ConversationId)) return (TId)(object)ConversationId.From(id);
            if (typeof(TId) == typeof(TaskId<object?>)) return (TId)(object)TaskId<object?>.From(id);
        }
        throw new ArgumentException($"Document {definition.Kind} requires a {ScopeName(definition)} ID");
    }

    private static string ScopeName(AnyDocDefinition definition) => definition.Semantics switch
    {
        DocumentSemantics.SessionScope => "session",
        DocumentSemantics.LatestConversationScope or DocumentSemantics.RewindableConversationScope => "conversation",
        _ => "task",
    };

    /// <summary>一个逻辑地址的稳定字符串身份。对应 TS <c>addressId</c>。</summary>
    public static string AddressId(DocumentAddress address) => AddressIdCore(address.Kind, address.Scope, address.Key);

    /// <summary>一个创建记录（新化身）的稳定字符串身份。对应 TS 对 <c>DocumentCreate</c> 调用 <c>addressId</c>。</summary>
    public static string AddressId(DocumentCreate create) => AddressIdCore(create.Kind, create.Scope, create.Key);

    /// <summary>一个记录的稳定字符串身份。对应 TS 对 <c>DocumentRecord</c> 调用 <c>addressId</c>。</summary>
    public static string AddressId(DocumentRecord record) => AddressIdCore(record.Kind, record.Scope, record.Key);

    private static string AddressIdCore(string kind, DocumentScope scope, string? key)
    {
        var owner = scope switch
        {
            DocumentScope.SessionScope => null,
            DocumentScope.ConversationScope conversation => (object?)conversation.ConversationId.Value,
            DocumentScope.TaskScope task => task.TaskId.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };
        // 对齐 TS JSON.stringify([kind, scopeKind, owner, key ?? null]) 的稳定键。
        var ownerText = owner is null ? "null" : System.Text.Json.JsonSerializer.Serialize(owner);
        var keyText = key is null ? "null" : System.Text.Json.JsonSerializer.Serialize(key);
        return $"[{System.Text.Json.JsonSerializer.Serialize(kind)},{System.Text.Json.JsonSerializer.Serialize(ScopeName(scope))},{ownerText},{keyText}]";
    }

    private static string ScopeName(DocumentScope scope) => scope switch
    {
        DocumentScope.SessionScope => "session",
        DocumentScope.ConversationScope => "conversation",
        DocumentScope.TaskScope => "task",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    /// <summary>擦除的定义形状（Session 在重载解析后使用）。对应 TS <c>AnyDocDefinition</c>。</summary>
    public sealed record AnyDocDefinition
    {
        public required string Kind { get; init; }

        public required int Version { get; init; }

        public required DocumentSemantics Semantics { get; init; }

        /// <summary>是否为族定义。</summary>
        public bool Family { get; init; }

        /// <summary>初始值工厂（种子可空；单例忽略）。</summary>
        public required Func<object?, IReadOnlyDictionary<string, object?>> Initial { get; init; }

        public Func<IReadOnlyDictionary<string, object?>, int, IReadOnlyDictionary<string, object?>>? Migrate { get; init; }

        public Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<Pi.Chord.Delta.DeltaOp>, CheckpointInfo, bool>?
            CheckpointWhen { get; init; }
    }

    /// <summary>把类型化定义擦除为 <see cref="AnyDocDefinition"/>（Session / storage 边界使用）。</summary>
    public static AnyDocDefinition Erase<T>(DocDefinition<T> definition)
        where T : class, IReadOnlyDictionary<string, object?>
        => new()
        {
            Kind = definition.Kind,
            Version = definition.Version,
            Semantics = definition.Semantics,
            Family = false,
            Initial = seed => definition.Initial(),
            Migrate = definition.Migrate is null
                ? null
                : (value, from) => definition.Migrate((T)(object)value, from),
            CheckpointWhen = definition.CheckpointWhen is null
                ? null
                : (value, ops, info) => definition.CheckpointWhen((T)(object)value, ops, info),
        };

    /// <summary>把类型化族定义擦除为 <see cref="AnyDocDefinition"/>。</summary>
    public static AnyDocDefinition Erase<T, TSeed>(DocFamilyDefinition<T, TSeed> definition)
        where T : class, IReadOnlyDictionary<string, object?>
        => new()
        {
            Kind = definition.Kind,
            Version = definition.Version,
            Semantics = definition.Semantics,
            Family = true,
            Initial = seed => definition.Initial(seed),
            Migrate = definition.Migrate is null
                ? null
                : (value, from) => definition.Migrate((T)(object)value, from),
            CheckpointWhen = definition.CheckpointWhen is null
                ? null
                : (value, ops, info) => definition.CheckpointWhen((T)(object)value, ops, info),
        };

    /// <summary>构造地址上的新化身存储创建记录。对应 TS <c>documentCreate</c>。</summary>
    public static DocumentCreate DocumentCreate(AnyDocDefinition definition, DocumentAddress address, DocumentId id)
    {
        var create = new DocumentCreate
        {
            Id = id,
            Kind = address.Kind,
            Key = address.Key,
            Scope = address.Scope,
        };
        if (address.Scope is DocumentScope.ConversationScope)
        {
            create = create with
            {
                History = HistoryOf(definition.Semantics),
                Fork = ForkOf(definition.Semantics),
            };
        }
        return create;
    }

    /// <summary>拒绝与持久化 scope / history / fork 语义不符的类型化访问。对应 TS <c>checkRecordScope</c>。</summary>
    public static void CheckRecordScope(AnyDocDefinition definition, DocumentRecord record)
    {
        var scopeMatches = record.Scope switch
        {
            DocumentScope.SessionScope => definition.Semantics is DocumentSemantics.SessionScope,
            DocumentScope.ConversationScope => definition.Semantics
                is DocumentSemantics.LatestConversationScope
                or DocumentSemantics.RewindableConversationScope
                && record.History == HistoryOf(definition.Semantics)
                && record.Fork == ForkOf(definition.Semantics),
            DocumentScope.TaskScope => definition.Semantics is DocumentSemantics.TaskScope,
            _ => false,
        };
        if (!scopeMatches)
            throw new ArgumentException(
                $"Document {record.Id.Value} ({record.Kind}) does not match the supplied definition semantics");
    }

    /// <summary>拒绝访问定义无法使用的已存储版本。对应 TS <c>checkRecordVersion</c>。</summary>
    public static void CheckRecordVersion(AnyDocDefinition definition, DocumentRecord record, int version)
    {
        if (version > definition.Version)
            throw new InvalidOperationException(
                $"Document {record.Id.Value} ({record.Kind}) has newer version {version} than {definition.Version}");
        if (version < definition.Version && definition.Migrate is null)
            throw new InvalidOperationException(
                $"Document {record.Id.Value} ({record.Kind}) requires migration from version {version}");
    }

    /// <summary>验证并物化脱钩的存储值。对应 TS <c>materializeDocument</c>。</summary>
    public static IReadOnlyDictionary<string, object?> MaterializeDocument(
        AnyDocDefinition definition, StoredDocument stored)
        => MaterializeDocumentValue(definition, stored.Record, stored.Version, stored.Value);

    /// <summary>在首个持久化化身前验证并物化一个脱钩值。对应 TS <c>materializeDocumentValue</c>。</summary>
    public static IReadOnlyDictionary<string, object?> MaterializeDocumentValue(
        AnyDocDefinition definition,
        DocumentRecord record,
        int version,
        IReadOnlyDictionary<string, object?> value)
    {
        CheckRecordScope(definition, record);
        CheckRecordVersion(definition, record, version);
        if (version == definition.Version) return value;
        return AsObject(definition.Migrate!(value, version));
    }

    private static ConversationHistory? HistoryOf(DocumentSemantics semantics) => semantics switch
    {
        DocumentSemantics.LatestConversationScope => Types.ConversationHistory.Latest,
        DocumentSemantics.RewindableConversationScope => Types.ConversationHistory.Rewindable,
        _ => null,
    };

    private static ConversationFork? ForkOf(DocumentSemantics semantics) => semantics switch
    {
        DocumentSemantics.LatestConversationScope latest => latest.Fork,
        DocumentSemantics.RewindableConversationScope rewindable => rewindable.Fork,
        _ => null,
    };

    private static IReadOnlyDictionary<string, object?> AsObject(object? value)
        => value as IReadOnlyDictionary<string, object?>
           ?? throw new InvalidOperationException("Document value must be a JSON object");
}
