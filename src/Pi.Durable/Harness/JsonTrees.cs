using System.Text.Json;
using Pi.Durable.Types;

namespace Pi.Durable.Harness;

/// <summary>
/// JSON 值树 ↔ C# 类型（内容块 / 消息 / 条目草稿 / 上下文编辑）的桥接。
/// 文档值树是严格 JSON（Dictionary / List / 基元）；这些辅助把树转换为 pi-ai 的类型化
/// 记录（经 System.Text.Json 的多态 wire 形状，round-trip 稳定），供 inbox 等把
/// 排队 JSON 载荷落到 transcript 时使用。
/// </summary>
internal static class JsonTrees
{
    // camelCase 命名策略对齐 TS wire 形状（"text"/"role"/"stopReason"…）；大小写不敏感使
    // PascalCase（STJ 默认形状）与 camelCase 树均可反序列化。显式 [JsonPropertyName] 不受影响。
    internal static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions Options = WireOptions;

    /// <summary>JSON 树 → 类型化记录（camelCase wire 形状）。</summary>
    public static T Deserialize<T>(object? tree) where T : class
        => tree is null
            ? throw new InvalidOperationException("cannot deserialize null")
            : JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToElement(tree, Options), Options)
              ?? throw new InvalidOperationException("deserialization produced null");

    /// <summary>JsonElement → 严格 JSON 值树（整数取 long，其余数字取 double）。</summary>
    public static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => Enumerable.ToDictionary(
            element.EnumerateObject(),
            property => property.Name,
            property => FromElement(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var value) ? value : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>C# 值 → 严格 JSON 值树（经 wire 序列化；ChatMessage / ContentBlock 用其多态形状）。</summary>
    public static object? ToTree(object? value)
        => value is EntryDraft draft
            ? ToEntryTree(draft)
            : FromElement(JsonSerializer.SerializeToElement(value, Options));

    /// <summary>
    /// <see cref="EntryDraft"/> → 严格 JSON 值树。TS 的条目草稿以 <c>head: EntryId | "self"</c> 表达活动范围，
    /// 因此 <see cref="EntryDraft.HeadIsSelf"/> 必须投影回字面量 <c>"self"</c>（并省略 <c>headIsSelf</c>），
    /// 与 <see cref="ToEntryDraft"/> 的读取相互可逆。
    /// </summary>
    public static Dictionary<string, object?> ToEntryTree(EntryDraft draft)
    {
        var tree = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = draft.Kind,
        };
        if (draft.Model is not null) tree["model"] = ToTree(draft.Model);
        if (draft.Data is not null) tree["data"] = ToTree(draft.Data);
        if (draft.HeadIsSelf) tree["head"] = "self";
        else if (draft.Head is { } head) tree["head"] = head.Value;
        if (draft.Edits is not null) tree["edits"] = EditsToJson(draft.Edits);
        return tree;
    }

    /// <summary>JSON 树 → 内容块列表。对应 TS <c>JsonRepresentation&lt;UserInput&gt;</c> 的还原。</summary>
    public static IReadOnlyList<Pi.Ai.Types.ContentBlock> ToContent(object? tree)
        => tree is null
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<Pi.Ai.Types.ContentBlock>>(
                  JsonSerializer.SerializeToElement(tree, Options), Options) ?? [];

    /// <summary>JSON 树 → 消息列表。</summary>
    public static IReadOnlyList<Pi.Ai.Types.ChatMessage> ToMessages(object? tree)
        => tree is null
            ? []
            : JsonSerializer.Deserialize<IReadOnlyList<Pi.Ai.Types.ChatMessage>>(
                  JsonSerializer.SerializeToElement(tree, Options), Options) ?? [];

    /// <summary>JSON 树 → 助手消息（进行中 partial 的形状）。缺省或形状不符为 null。</summary>
    public static Pi.Ai.Types.AssistantMessage? ToAssistantMessage(IReadOnlyDictionary<string, object?>? tree)
    {
        if (tree is null) return null;
        try
        {
            return JsonSerializer.Deserialize<Pi.Ai.Types.AssistantMessage>(
                JsonSerializer.SerializeToElement(tree, Options), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>消息列表 → JSON 树。</summary>
    public static object? MessagesToJson(IReadOnlyList<Pi.Ai.Types.ChatMessage>? messages)
        => messages is null ? null : ToTree(messages);

    /// <summary>编辑列表 → JSON 树（{target, action, messages?}）。对应 TS <c>ContextEdit</c>。</summary>
    public static object? EditsToJson(IReadOnlyList<ContextEdit>? edits)
    {
        if (edits is null) return null;
        return edits.Select(edit => edit switch
        {
            ContextEdit.Omit omit => (object)new Dictionary<string, object?>
            {
                ["target"] = omit.Target.Value,
                ["action"] = "omit",
            },
            ContextEdit.Replace replace => new Dictionary<string, object?>
            {
                ["target"] = replace.Target.Value,
                ["action"] = "replace",
                ["messages"] = ToTree(replace.Messages),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(edit)),
        }).ToList();
    }

    /// <summary>JSON 树 → 编辑列表。</summary>
    public static IReadOnlyList<ContextEdit>? ToEdits(object? tree)
    {
        if (tree is not IReadOnlyList<object?> items) return null;
        var edits = new List<ContextEdit>();
        foreach (var item in items)
        {
            if (item is not IReadOnlyDictionary<string, object?> edit) continue;
            var target = EntryId.From(Convert.ToInt64(edit["target"]!));
            var action = edit.TryGetValue("action", out var actionValue) ? actionValue as string : null;
            if (action == "replace")
            {
                edits.Add(new ContextEdit.Replace(target, ToMessages(edit.TryGetValue("messages", out var messages) ? messages : null)));
            }
            else
            {
                edits.Add(new ContextEdit.Omit(target));
            }
        }

        return edits;
    }

    /// <summary>排队的条目草稿 JSON（{kind, model?, data?, head?, edits?}）→ <see cref="EntryDraft"/>。
    /// TS 的 <c>head: "self"</c> 由 <see cref="EntryDraft.HeadIsSelf"/> 表达。</summary>
    public static EntryDraft ToEntryDraft(IReadOnlyDictionary<string, object?> json)
    {
        EntryId? head = null;
        var headIsSelf = false;
        if (json.TryGetValue("head", out var headValue))
        {
            if (headValue is string self && self == "self") headIsSelf = true;
            else if (headValue is not null) head = EntryId.From(Convert.ToInt64(headValue));
        }

        return new EntryDraft
        {
            Kind = (string)json["kind"]!,
            Model = json.TryGetValue("model", out var model) ? ToMessages(model) : null,
            Data = json.TryGetValue("data", out var data) ? data : null,
            Head = head,
            HeadIsSelf = headIsSelf,
            Edits = json.TryGetValue("edits", out var edits) ? ToEdits(edits) : null,
        };
    }
}
