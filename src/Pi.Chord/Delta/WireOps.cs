using System.Text.Json;

namespace Pi.Chord.Delta;

/// <summary>
/// 线上格式（wire tuple）：动词元组，其中路径可被压缩为整数 id 或整体省略。
/// 对应 TS <c>WireOp</c>（<c>delta/index.ts</c> 顶部的元组类型）。C# 侧沿用
/// <see cref="List{T}"/> 元组表示（下标 0 是动词字符串），与 CBOR 值模型直接互通。
/// </summary>
public static class DeltaWire
{
    /// <summary>线上格式校验：允许整数路径 id、省略路径与 <c>#</c> 定义。对应 TS <c>assertValidWireOp</c>。</summary>
    public static void AssertValidWireOp(object? op)
    {
        if (op is not List<object?> tuple || tuple.Count == 0)
            throw new ArgumentException("op is not a tuple", nameof(op));
        if (tuple[0] is not string verb)
            throw new ArgumentException($"unknown op verb: {tuple[0]}", nameof(op));

        void OkRef(object? r)
        {
            if (r is long id)
            {
                if (id < 0) throw new ArgumentException("bad path id", nameof(op));
                return;
            }
            if (r is int id32)
            {
                if (id32 < 0) throw new ArgumentException("bad path id", nameof(op));
                return;
            }
            // 字符串不是路径：与 TS 一致，此处不拒绝（解码时会因解析到根而暴露）。
            if (r is List<object?> path) PathSafety.AssertSafePath(FromSegmentList(path));
            else throw new ArgumentException("path is not an array", nameof(op));
        }

        switch (verb)
        {
            case "r":
                if (tuple.Count != 2) throw new ArgumentException("r arity", nameof(op));
                break;
            case "#":
                if (tuple.Count != 3 || tuple[1] is not (long or int) || Convert.ToInt64(tuple[1]) < 0
                    || tuple[2] is not List<object?> defPath)
                    throw new ArgumentException("# shape", nameof(op));
                PathSafety.AssertSafePath(FromSegmentList(defPath));
                break;
            case "s":
                if (tuple.Count == 3) OkRef(tuple[1]);
                else if (tuple.Count != 2) throw new ArgumentException("s arity", nameof(op));
                break;
            case "d":
                if (tuple.Count == 2) OkRef(tuple[1]);
                else if (tuple.Count != 1) throw new ArgumentException("d arity", nameof(op));
                break;
            case "a":
                if (tuple.Count == 3) { OkRef(tuple[1]); if (tuple[2] is not string) throw new ArgumentException("a value", nameof(op)); }
                else if (tuple.Count == 2) { if (tuple[1] is not string) throw new ArgumentException("a value", nameof(op)); }
                else throw new ArgumentException("a arity", nameof(op));
                break;
            case "t":
                if (tuple.Count == 3) { OkRef(tuple[1]); AssertCount(tuple[2]); }
                else if (tuple.Count == 2) AssertCount(tuple[1]);
                else throw new ArgumentException("t arity", nameof(op));
                break;
            case "p":
            {
                object? i, r, items;
                if (tuple.Count == 5) { OkRef(tuple[1]); (i, r, items) = (tuple[2], tuple[3], tuple[4]); }
                else if (tuple.Count == 4) { (i, r, items) = (tuple[1], tuple[2], tuple[3]); }
                else throw new ArgumentException("p arity", nameof(op));
                AssertCount(i); AssertCount(r);
                if (items is not List<object?>) throw new ArgumentException("p items", nameof(op));
                break;
            }
            case "m":
                if (tuple.Count == 3) OkRef(tuple[1]);
                else if (tuple.Count != 2) throw new ArgumentException("m arity", nameof(op));
                AssertPermutation(tuple[^1]);
                break;
        }

        static void AssertCount(object? value)
        {
            if (value is not (long or int) || Convert.ToInt64(value) < 0)
                throw new ArgumentException("count must be a non-negative integer", nameof(op));
        }
    }

    /// <summary>校验置换是双射。对应 TS <c>assertPermutation</c>。</summary>
    private static void AssertPermutation(object? value)
    {
        if (value is not List<object?> list) throw new ArgumentException("m permutation is not an array");
        var seen = new bool[list.Count];
        foreach (var item in list)
        {
            var index = item is long l ? l : item is int i ? i : -1L;
            if (index < 0 || index >= list.Count || seen[index])
                throw new ArgumentException("m permutation is not a bijection");
            seen[index] = true;
        }
    }

    /// <summary>把段列表（string / long）转为 <see cref="Path"/>。</summary>
    private static Path FromSegmentList(List<object?> segments) => new(
        segments.Select<object?, Seg>(s => s is long l ? Seg.Index(l) : s is int i ? Seg.Index(i) : Seg.Key((string)s!)).ToList());

    /// <summary>
    /// 单条线上元组 → Op（无 id 表，只接受内联/省略路径形式）。供服务快照等
    /// 「批内自包含」的场景做已解码 ops 校验。
    /// </summary>
    public static DeltaOp DecodeSingle(List<object?> wire)
    {
        var decoded = new Decoder().Decode([wire]);
        if (decoded.Count != 1) throw new ArgumentException("not a single op", nameof(wire));
        return decoded[0];
    }

    private static List<object?> ToSegmentList(Path path)
    {
        var segments = new List<object?>(path.Segments.Count);
        foreach (var segment in path.Segments)
            segments.Add(segment.Value is long l ? l : segment.Value is int i ? i : segment.Value);
        return segments;
    }

    private static string PathKey(Path path) => JsonSerializer.Serialize(ToSegmentList(path));

    /// <summary>
    /// 路径驻留编码器。对应 TS <c>encoder()</c>：第二次使用才驻留（定义成本高于
    /// 一次内联），同批相邻同路径整体省略引用；<c>r</c>（基础批次）是恢复点，
    /// 其后清空 id 表保证自包含。每条独立状态流一对编解码器。
    /// </summary>
    public sealed class Encoder
    {
        private readonly HashSet<string> _seen = new();
        private readonly Dictionary<string, long> _ids = new();
        private long _nextId;
        private string? _previous;

        /// <summary>把一批 Op 编码为线上元组。</summary>
        public List<List<object?>> Encode(IReadOnlyList<DeltaOp> ops)
        {
            _previous = null;
            var output = new List<List<object?>>(ops.Count);
            foreach (var op in ops)
            {
                if (op is DeltaOp.Replace)
                {
                    output.Add(["r", ((DeltaOp.Replace)op).Value]);
                    // 基础批次是恢复点：其后 id 表必须清空保证自包含。
                    _seen.Clear();
                    _ids.Clear();
                    _nextId = 0;
                    _previous = null;
                    continue;
                }

                var (verb, path) = op switch
                {
                    DeltaOp.Set set => ("s", set.Path),
                    DeltaOp.Delete del => ("d", del.Path),
                    DeltaOp.Append app => ("a", app.Path),
                    DeltaOp.Truncate trunc => ("t", trunc.Path),
                    DeltaOp.Splice splice => ("p", splice.Path),
                    DeltaOp.Move move => ("m", move.Path),
                    _ => throw new ArgumentException("unknown op verb", nameof(ops)),
                };
                var key = PathKey(path);

                // 与上一条同路径：整体省略引用。
                if (key == _previous)
                {
                    output.Add(op switch
                    {
                        DeltaOp.Set s => (List<object?>)["s", s.Value],
                        DeltaOp.Delete => ["d"],
                        DeltaOp.Append a => ["a", a.Text],
                        DeltaOp.Truncate t => ["t", t.Length],
                        DeltaOp.Splice p2 => ["p", p2.Index, p2.Remove, p2.Items.ToList()],
                        DeltaOp.Move m2 => ["m", m2.Permutation.Cast<object?>().ToList()],
                        _ => throw new InvalidOperationException(),
                    });
                    continue;
                }

                object? reference = ToSegmentList(path);
                if (_ids.TryGetValue(key, out var existing))
                {
                    reference = existing;
                }
                else if (_seen.Contains(key))
                {
                    var id = _nextId++;
                    _ids[key] = id;
                    output.Add(["#", id, ToSegmentList(path)]); // 第二次使用：先定义再引用
                    reference = id;
                }
                else
                {
                    _seen.Add(key); // 第一次使用：内联
                }

                output.Add(op switch
                {
                    DeltaOp.Set s => (List<object?>)["s", reference, s.Value],
                    DeltaOp.Delete => ["d", reference],
                    DeltaOp.Append a => ["a", reference, a.Text],
                    DeltaOp.Truncate t => ["t", reference, t.Length],
                    DeltaOp.Splice p3 => ["p", reference, p3.Index, p3.Remove, p3.Items.ToList()],
                    DeltaOp.Move m3 => ["m", reference, m3.Permutation.Cast<object?>().ToList()],
                    _ => throw new InvalidOperationException(),
                });
                _previous = key;
            }
            return output;
        }
    }

    /// <summary>解码器。对应 TS <c>decoder()</c>：还原 id 定义与省略引用。</summary>
    public sealed class Decoder
    {
        private readonly Dictionary<long, Path> _paths = new();

        /// <summary>把一批线上元组还原为 Op。</summary>
        public List<DeltaOp> Decode(IReadOnlyList<List<object?>> wire)
        {
            Path? previous = null; // 批内作用域，与编码器一致
            var output = new List<DeltaOp>(wire.Count);
            foreach (var tuple in wire)
            {
                AssertValidWireOp(tuple);
                var verb = (string)tuple[0]!;

                if (verb == "#")
                {
                    _paths[Convert.ToInt64(tuple[1])] = FromSegmentList((List<object?>)tuple[2]!);
                    continue;
                }
                if (verb == "r")
                {
                    output.Add(new DeltaOp.Replace(tuple[1]));
                    _paths.Clear();
                    previous = null;
                    continue;
                }

                // 由元数判断引用是否存在：短形式省略。
                var isShort = (verb == "d" && tuple.Count == 1)
                    || (verb is not ("d" or "p") && tuple.Count == 2)
                    || (verb == "p" && tuple.Count == 4);

                Path path;
                if (isShort)
                {
                    if (previous is null) throw new KeyNotFoundException("unresolvable path: []");
                    path = previous;
                }
                else
                {
                    var reference = tuple[1];
                    if (reference is long or int)
                    {
                        var id = Convert.ToInt64(reference);
                        if (!_paths.TryGetValue(id, out var resolved))
                            throw new KeyNotFoundException($"unresolvable path id: {id}");
                        path = resolved;
                    }
                    else
                    {
                        path = FromSegmentList((List<object?>)reference!);
                    }
                    previous = path;
                }

                if (verb is not ("p" or "m") && path.IsEmpty)
                    throw new KeyNotFoundException("unresolvable path: []");

                switch (verb)
                {
                    case "s":
                        output.Add(new DeltaOp.Set(path, isShort ? tuple[1] : tuple[2]));
                        break;
                    case "d":
                        output.Add(new DeltaOp.Delete(path));
                        break;
                    case "a":
                        output.Add(new DeltaOp.Append(path, (string)(isShort ? tuple[1] : tuple[2])!));
                        break;
                    case "t":
                        output.Add(new DeltaOp.Truncate(path, Convert.ToInt64(isShort ? tuple[1] : tuple[2])));
                        break;
                    case "p":
                    {
                        var (index, remove, items) = isShort
                            ? (Convert.ToInt64(tuple[1]), Convert.ToInt64(tuple[2]), (IReadOnlyList<object?>)(List<object?>)tuple[3]!)
                            : (Convert.ToInt64(tuple[2]), Convert.ToInt64(tuple[3]), (IReadOnlyList<object?>)(List<object?>)tuple[4]!);
                        output.Add(new DeltaOp.Splice(path, index, remove, items));
                        break;
                    }
                    case "m":
                        output.Add(new DeltaOp.Move(path,
                            ((List<object?>)(isShort ? tuple[1] : tuple[2])!).Select(Convert.ToInt64).ToList()));
                        break;
                }
            }
            return output;
        }
    }
}
