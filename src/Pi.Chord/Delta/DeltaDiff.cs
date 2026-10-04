using Pi.Chord.Delta;

namespace Pi.Chord.Delta;

/// <summary>
/// 修订差异生成器：对比两个 JSON 值生成最小 op 批。
/// 对应 TS <c>delta/diff.ts</c> 的 <c>diffRevisions</c>（本移植实现对象键差 /
/// 数组 splice / 文本追加的核心语义；tracker 的滑动窗口与 chunk 压缩随后续会话）。
/// </summary>
public static class DeltaDiff
{
    /// <summary>
    /// 生成把 <paramref name="from"/> 变换为 <paramref name="to"/> 的 op 批。
    /// 值整体相等时返回空批；类型或形状差异过大时以单个 Replace 收敛。
    /// </summary>
    public static IReadOnlyList<DeltaOp> DiffRevisions(object? from, object? to)
    {
        var ops = new List<DeltaOp>();
        DiffValue(from, to, Path.Root, ops);
        return ops;
    }

    private static void DiffValue(object? from, object? to, Path path, List<DeltaOp> ops)
    {
        // 相等：无操作。
        if (JsonEquals(from, to)) return;

        // 对象 → 对象：键级差分。
        if (from is Dictionary<string, object?> fromMap && to is Dictionary<string, object?> toMap)
        {
            DiffMaps(fromMap, toMap, path, ops);
            return;
        }

        // 列表 → 列表：最小 splice。
        if (from is List<object?> fromList && to is List<object?> toList)
        {
            DiffLists(fromList, toList, path, ops);
            return;
        }

        // 字符串 → 字符串：优先追加（a），不适用时回落 set。
        if (from is string fromText && to is string toText && !path.IsEmpty
            && toText.StartsWith(fromText, StringComparison.Ordinal))
        {
            ops.Add(new DeltaOp.Append(path, toText[fromText.Length..]));
            return;
        }

        // 其余：类型变化或标量变化 → set（根 → replace 由调用方处理）。
        AddSetOrReplace(path, to, ops);
    }

    private static void DiffMaps(
        Dictionary<string, object?> from, Dictionary<string, object?> to, Path path, List<DeltaOp> ops)
    {
        foreach (var (key, fromValue) in from)
        {
            var childPath = path.Append(Seg.Key(key));
            if (to.TryGetValue(key, out var toValue))
                DiffValue(fromValue, toValue, childPath, ops);
            else
                ops.Add(new DeltaOp.Delete(childPath));
        }
        foreach (var (key, toValue) in to)
        {
            if (!from.ContainsKey(key))
                ops.Add(new DeltaOp.Set(path.Append(Seg.Key(key)), toValue));
        }
    }

    private static void DiffLists(List<object?> from, List<object?> to, Path path, List<DeltaOp> ops)
    {
        // 公共前缀：逐元素递归。
        var common = Math.Min(from.Count, to.Count);
        var prefix = 0;
        while (prefix < common && JsonEquals(from[prefix], to[prefix])) prefix++;

        // 尾部对齐（公共后缀），中间段用一次 splice 收敛。
        var suffixFrom = from.Count;
        var suffixTo = to.Count;
        while (suffixFrom > prefix && suffixTo > prefix && JsonEquals(from[suffixFrom - 1], to[suffixTo - 1]))
        {
            suffixFrom--;
            suffixTo--;
        }

        for (var i = 0; i < prefix; i++)
            DiffValue(from[i], to[i], path.Append(Seg.Index(i)), ops);

        // 中间差异段：删除多余 + 替换/插入。
        var removeCount = suffixFrom - prefix;
        var insertItems = to.Skip(prefix).Take(suffixTo - prefix).ToList();
        if (removeCount > 0 || insertItems.Count > 0)
        {
            ops.Add(new DeltaOp.Splice(path, prefix, removeCount, insertItems));
        }
    }

    private static void AddSetOrReplace(Path path, object? to, List<DeltaOp> ops)
    {
        if (path.IsEmpty)
            ops.Add(new DeltaOp.Replace(to));
        else
            ops.Add(new DeltaOp.Set(path, to));
    }

    /// <summary>JSON 值的深相等（对象/列表递归，标量 Equals）。</summary>
    public static bool JsonEquals(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (Dictionary<string, object?> l, Dictionary<string, object?> r) => l.Count == r.Count
            && l.All(kv => r.TryGetValue(kv.Key, out var value) && JsonEquals(kv.Value, value)),
        (List<object?> l, List<object?> r) => l.Count == r.Count
            && l.Zip(r).All(pair => JsonEquals(pair.First, pair.Second)),
        (string, string) or (bool, bool) or (long, long) or (double, double) => Equals(left, right),
        _ => Equals(left, right),
    };
}
