namespace Pi.Chord;

/// <summary><see cref="Json.CopyJson"/> 的选项。对应 TS <c>CopyJsonOptions</c>（json.ts）。</summary>
public sealed record CopyJsonOptions
{
    /// <summary>
    /// 省略对象里值为「缺省」的条目，同时保持数组的严格语义。
    /// <para>TS 省略的是 <c>undefined</c>；C# 值树里没有 <c>undefined</c>，唯一可用等价物是
    /// 条目值为 <c>null</c>——仅在调用方显式开启时按此处理（默认保留 JSON null）。</para>
    /// </summary>
    public bool OmitUndefinedProperties { get; init; }
}

/// <summary>
/// JSON 值契约。对应 TS <c>json.ts</c>：<c>copyJson</c> 把值复制成一棵调用方拥有的、
/// 无别名（无共享引用）的严格 JSON 树；<c>isJsonValue</c> 判定值是否为有限、无环、
/// 仅含普通对象/稠密数组的严格 JSON。
/// <para>TS 用 <c>Reflect.ownKeys</c>/属性描述符判定「普通对象与稠密数组」；C# 值树里
/// 等价约束是「仅 List/IReadOnlyList 与 Dictionary/IReadOnlyDictionary 容器 + 有限数字」，
/// 非 JSON 类型（自定义类、委托、NaN/Infinity）一律拒绝。</para>
/// </summary>
public static class Json
{
    /// <summary>值树是否为严格 JSON（有限、无环、仅普通容器）。对应 TS <c>isJsonValue</c>。</summary>
    public static bool IsJsonValue(object? value) => Check(value, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static bool Check(object? value, HashSet<object> ancestors)
    {
        switch (value)
        {
            case null or bool or string:
                return true;
            case double number:
                return double.IsFinite(number);
            case float number:
                return float.IsFinite(number);
            case decimal:
            case long or int or short or byte or sbyte or ushort or uint:
                return true;
        }

        if (value is not object node) return false;

        if (TryArray(node, out var items))
        {
            if (!ancestors.Add(node)) return false;
            try
            {
                return items.All(item => Check(item, ancestors));
            }
            finally
            {
                ancestors.Remove(node);
            }
        }

        if (TryObject(node, out var entries))
        {
            if (!ancestors.Add(node)) return false;
            try
            {
                return entries.All(entry => Check(entry.Value, ancestors));
            }
            finally
            {
                ancestors.Remove(node);
            }
        }

        return false;
    }

    /// <summary>
    /// 把值复制成一棵无别名的严格 JSON 树。对应 TS <c>copyJson</c>：
    /// 非有限数字、非 JSON 类型、循环引用、非普通容器都会抛错。
    /// </summary>
    public static object? CopyJson(object? value, CopyJsonOptions? options = null)
        => Copy(value, null, options?.OmitUndefinedProperties == true);

    private static object? Copy(object? value, HashSet<object>? ancestors, bool omitUndefinedProperties)
    {
        switch (value)
        {
            case null or bool or string:
                return value;
            case double number:
                if (double.IsFinite(number)) return number;
                throw new InvalidOperationException("Value contains a non-finite number and is not strict JSON");
            case float number:
                if (float.IsFinite(number)) return number;
                throw new InvalidOperationException("Value contains a non-finite number and is not strict JSON");
            case decimal:
            case long or int or short or byte or sbyte or ushort or uint:
                return value;
        }

        if (value is not object node)
        {
            throw new InvalidOperationException(
                $"Value contains a non-JSON {value?.GetType().Name ?? "unknown"}; expected strict JSON");
        }

        var active = ancestors ?? new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!active.Add(node))
        {
            throw new InvalidOperationException("Value contains cycles and is not strict JSON");
        }
        try
        {
            if (TryArray(node, out var items))
            {
                return items.Select(item => Copy(item, active, omitUndefinedProperties)).ToList();
            }
            if (TryObject(node, out var entries))
            {
                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, entryValue) in entries)
                {
                    if (entryValue is null && omitUndefinedProperties) continue;
                    result[key] = Copy(entryValue, active, omitUndefinedProperties);
                }
                return result;
            }
            throw new InvalidOperationException("Value must contain strict JSON plain objects or arrays");
        }
        finally
        {
            active.Remove(node);
        }
    }

    /// <summary>结构化拷贝别名（对齐 delta 侧的 deep-clone 命名）。</summary>
    public static object? DeepClone(object? value) => CopyJson(value);

    public static Dictionary<string, object?> EmptyObject() => [];

    public static List<object?> EmptyArray() => [];

    private static bool TryArray(object node, out IReadOnlyList<object?> items)
    {
        switch (node)
        {
            case List<object?> list:
                items = list;
                return true;
            case IReadOnlyList<object?> readOnly:
                items = readOnly;
                return true;
            default:
                items = [];
                return false;
        }
    }

    private static bool TryObject(object node, out IEnumerable<KeyValuePair<string, object?>> entries)
    {
        switch (node)
        {
            case Dictionary<string, object?> map:
                entries = map;
                return true;
            case IReadOnlyDictionary<string, object?> readOnly:
                entries = readOnly;
                return true;
            default:
                entries = [];
                return false;
        }
    }
}
