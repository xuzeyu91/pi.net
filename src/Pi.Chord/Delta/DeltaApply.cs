using Pi.Chord.Delta;

namespace Pi.Chord.Delta;

/// <summary>
/// 操作应用器：把 op 应用到纯 JSON 值（不可变语义——沿路径重建祖先链，返回新根）。
/// 对应 TS <c>delta/apply-immutable-trusted.ts</c>（"trusted" 版本：操作已在入口通过
/// <see cref="PathSafety.AssertValidOp"/> 校验）。路径拷贝（path copying）是这里的
/// 关键机制：只重建被修改路径上的容器，其余子树保持共享。
/// </summary>
public static class DeltaApply
{
    /// <summary>
    /// 依次应用一批操作，返回最终值。批中 <see cref="DeltaOp.Replace"/> 只能出现在首位。
    /// </summary>
    public static object? Apply(IReadOnlyList<DeltaOp> ops, object? value)
    {
        foreach (var op in ops)
        {
            PathSafety.AssertValidOp(op);
            value = ApplyOne(op, value);
        }
        return value;
    }

    /// <summary>应用单个操作（假设已校验）。</summary>
    public static object? ApplyOne(DeltaOp op, object? value) => op switch
    {
        DeltaOp.Replace replace => Json.DeepClone(replace.Value),
        DeltaOp.Set set => SetAt(value, set.Path, set.Value),
        DeltaOp.Delete delete => DeleteAt(value, delete.Path),
        DeltaOp.Append append => MutateLeaf(value, append.Path, leaf => leaf is string text ? text + append.Text : throw NotSuitable("a")),
        DeltaOp.Truncate truncate => MutateLeaf(value, truncate.Path,
            leaf => leaf is List<object?> list && truncate.Length <= list.Count
                ? list.Take((int)truncate.Length).ToList()
                : throw NotSuitable("t")),
        // p 与 m 允许作用于根（被跟踪的值本身可以是数组）。
        DeltaOp.Splice splice => MutateListLeaf(value, splice.Path,
            leaf => leaf is List<object?> list ? SpliceList(list, splice.Index, splice.Remove, splice.Items) : throw NotSuitable("p")),
        DeltaOp.Move move => MutateListLeaf(value, move.Path,
            leaf => leaf is List<object?> list && move.Permutation.Count == list.Count
                ? move.Permutation.Select(i => list[(int)i]).ToList()
                : throw NotSuitable("m")),
        _ => throw new InvalidOperationException("unknown op"),
    };

    private static InvalidOperationException NotSuitable(string verb)
        => new($"{verb} target has an incompatible shape");

    private static List<object?> SpliceList(List<object?> list, long index, long remove, IReadOnlyList<object?> items)
    {
        var start = (int)index;
        if (start > list.Count)
            throw new InvalidOperationException("p index beyond array length");
        var removeCount = (int)Math.Min(remove, list.Count - start);
        var clone = new List<object?>(list);
        clone.RemoveRange(start, removeCount);
        clone.InsertRange(start, items.Select(Json.DeepClone));
        return clone;
    }

    // ---------- 路径递归下降（不可变路径拷贝） ----------

    /// <summary>沿路径下降并在叶子处替换值；缺失的中间对象键视为 null 容器。</summary>
    private static object? SetAt(object? value, Path path, object? newValue)
    {
        if (path.IsEmpty) return Json.DeepClone(newValue);
        var (seg, restPath) = Split(path);

        if (seg.Value is string key)
        {
            var map = value as Dictionary<string, object?> ?? [];
            var child = map.TryGetValue(key, out var existing) ? existing : null;
            var clone = new Dictionary<string, object?>(map) { [key] = SetAt(child, restPath, newValue) };
            return clone;
        }
        if (value is List<object?> list)
        {
            var index = ToIndex(seg, list.Count);
            var clone = new List<object?>(list) { [index] = SetAt(list[index], restPath, newValue) };
            return clone;
        }
        throw new InvalidOperationException($"cannot descend into {value?.GetType().Name ?? "null"}");
    }

    /// <summary>沿路径下降并在叶子处删除键/元素。</summary>
    private static object? DeleteAt(object? value, Path path)
    {
        var (seg, restPath) = Split(path);

        if (seg.Value is string key)
        {
            if (value is not Dictionary<string, object?> map)
                throw new InvalidOperationException("d target is not an object");
            var clone = new Dictionary<string, object?>(map);
            if (restPath.IsEmpty) clone.Remove(key);
            else clone[key] = DeleteAt(map.TryGetValue(key, out var child) ? child : null, restPath);
            return clone;
        }
        if (value is List<object?> list)
        {
            var index = ToIndex(seg, list.Count);
            var clone = new List<object?>(list);
            if (restPath.IsEmpty) clone.RemoveAt(index);
            else clone[index] = DeleteAt(list[index], restPath);
            return clone;
        }
        throw new InvalidOperationException("d target is not a container");
    }

    /// <summary>允许根级数组：a/t 用 MutateLeaf（禁根），p/m 用本方法（允许根）。</summary>
    private static object? MutateListLeaf(object? value, Path path, Func<object?, object?> mutate)
    {
        if (path.IsEmpty)
        {
            if (value is not List<object?> rootList)
                throw new InvalidOperationException("root-level p/m requires an array");
            return mutate(rootList);
        }
        return MutateLeaf(value, path, mutate);
    }

    /// <summary>沿路径下降到叶子并应用变更函数（a/t/p/m 共用骨架）。</summary>
    private static object? MutateLeaf(object? value, Path path, Func<object?, object?> mutate)
    {
        if (path.IsEmpty)
            throw new InvalidOperationException("this op cannot target the root");
        var (seg, restPath) = Split(path);

        if (seg.Value is string key)
        {
            if (value is not Dictionary<string, object?> map)
                throw new InvalidOperationException("target is not an object");
            var clone = new Dictionary<string, object?>(map);
            if (restPath.IsEmpty) clone[key] = mutate(map.TryGetValue(key, out var leaf) ? leaf : null);
            else clone[key] = MutateLeaf(map.TryGetValue(key, out var child) ? child : null, restPath, mutate);
            return clone;
        }
        if (value is List<object?> list)
        {
            var index = ToIndex(seg, list.Count);
            var clone = new List<object?>(list);
            if (restPath.IsEmpty) clone[index] = mutate(list[index]);
            else clone[index] = MutateLeaf(list[index], restPath, mutate);
            return clone;
        }
        throw new InvalidOperationException("target is not a container");
    }

    // ---------- 辅助 ----------

    private static (Seg Head, Path RestPath) Split(Path path)
    {
        var rest = path.Segments.Count == 1
            ? Path.Root
            : new Path(path.Segments.Skip(1).ToList());
        return (path.Segments[0], rest);
    }

    private static string Key(Seg segment) => segment.Value switch
    {
        string text => text,
        long or int => segment.ToString(),
        _ => throw new InvalidOperationException("object key must be a string"),
    };

    private static int ToIndex(Seg segment, int length)
    {
        var index = segment.Value switch
        {
            long number => (int)number,
            int number => number,
            _ => throw new InvalidOperationException("array index must be a number"),
        };
        if (index < 0 || index >= length)
            throw new InvalidOperationException($"array index out of bounds: {index} of {length}");
        return index;
    }
}
