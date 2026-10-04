using System.Text.Json.Serialization;

namespace Pi.Chord.Delta;

/// <summary>路径段：对象键（string）或数组索引（long）。对应 TS <c>Seg = string | number</c>。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeltaVerb>))]
public enum DeltaVerb
{
    /// <summary>整体替换（唯一允许作用于根的写操作之一）。</summary>
    Replace,

    /// <summary>设置路径处的值。</summary>
    Set,

    /// <summary>删除路径处的键/元素。</summary>
    Delete,

    /// <summary>向文本追加字符串。</summary>
    Append,

    /// <summary>数组截断到指定长度。</summary>
    Truncate,

    /// <summary>数组 splice：从 index 起删 remove 个并插入 items。</summary>
    Splice,

    /// <summary>数组原地重排：new[i] = old[permutation[i]]。</summary>
    Move,
}

/// <summary>路径段（对象键或数组索引）。对应 TS <c>Seg</c>。</summary>
public readonly record struct Seg(object Value)
{
    public static Seg Key(string key) => new(key);
    public static Seg Index(long index) => new(index);

    public bool IsIndex => Value is long or int;

    public override string ToString() => Value.ToString() ?? "";
}

/// <summary>路径（段序列）。对应 TS <c>Path</c>；空路径表示根。</summary>
public sealed record Path(IReadOnlyList<Seg> Segments)
{
    public static readonly Path Root = new([]);

    public bool IsEmpty => Segments.Count == 0;

    public Path Append(Seg segment) => new([.. Segments, segment]);

    public override string ToString() => string.Join("/", Segments.Select(s => s.ToString()));
}

/// <summary>
/// 操作判别基类：修订跟踪的统一形态（内存、线上、磁盘同构）。对应 TS <c>Op</c> 元组联合。
/// <para>仅 <c>Replace</c> 允许作用于根；其余操作禁止以空路径为目标。</para>
/// </summary>
public abstract record DeltaOp
{
    private DeltaOp() { }

    /// <summary>["r", value]：整体替换。批中至多出现一次且必在首位。</summary>
    public sealed record Replace(object? Value) : DeltaOp;

    /// <summary>["s", path, value]：设置非根路径的值。</summary>
    public sealed record Set(Path Path, object? Value) : DeltaOp;

    /// <summary>["d", path]：删除非根路径处的键/元素。</summary>
    public sealed record Delete(Path Path) : DeltaOp;

    /// <summary>["a", path, text]：向路径处的文本追加。</summary>
    public sealed record Append(Path Path, string Text) : DeltaOp;

    /// <summary>["t", path, length]：数组截断到 length（≥0）。</summary>
    public sealed record Truncate(Path Path, long Length) : DeltaOp;

    /// <summary>["p", path, index, remove, items]：数组 splice。</summary>
    public sealed record Splice(Path Path, long Index, long Remove, IReadOnlyList<object?> Items) : DeltaOp;

    /// <summary>["m", path, permutation]：数组重排，new[i] = old[permutation[i]]。</summary>
    public sealed record Move(Path Path, IReadOnlyList<long> Permutation) : DeltaOp;
}

/// <summary>路径安全：保留段集合。对应 TS <c>RESERVED_SEGMENTS</c>。</summary>
public static class PathSafety
{
    /// <summary>
    /// 可达原型链的危险段。JSON.parse 本身安全（__proto__ 是自有属性），
    /// 但 applier 的 parent[key] = value 写入正是原型污染的入口——而 op 来自
    /// facet、插件隔间或可能回显模型输出的工具，全部按不可信输入处理。
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedSegments =
        new HashSet<string>(StringComparer.Ordinal) { "__proto__", "constructor", "prototype" };

    /// <summary>校验路径不含保留段；违规抛出 <see cref="UnsafePathException"/>。</summary>
    public static void AssertSafePath(Path path)
    {
        foreach (var segment in path.Segments)
        {
            if (segment.Value is string key && ReservedSegments.Contains(key))
                throw new UnsafePathException(key);
        }
    }

    /// <summary>校验操作形状（动词 + 元数 + 载荷）。对应 TS <c>assertValidOp</c>。</summary>
    public static void AssertValidOp(DeltaOp op)
    {
        switch (op)
        {
            case DeltaOp.Replace:
                return;
            case DeltaOp.Set set:
                AssertPathArg(set.Path, nonEmpty: true);
                return;
            case DeltaOp.Delete delete:
                AssertPathArg(delete.Path, nonEmpty: true);
                return;
            case DeltaOp.Append append:
                AssertPathArg(append.Path, nonEmpty: true);
                return;
            case DeltaOp.Truncate truncate:
                if (truncate.Length < 0) throw new InvalidOperationException("t length must be >= 0");
                AssertPathArg(truncate.Path, nonEmpty: true);
                return;
            case DeltaOp.Splice splice:
                if (splice.Index < 0) throw new InvalidOperationException("p index must be >= 0");
                if (splice.Remove < 0) throw new InvalidOperationException("p remove must be >= 0");
                AssertPathArg(splice.Path);
                return;
            case DeltaOp.Move move:
                AssertPermutation(move.Permutation);
                AssertPathArg(move.Path);
                return;
            default:
                throw new InvalidOperationException("unknown op");
        }
    }

    private static void AssertPathArg(Path path, bool nonEmpty = false)
    {
        if (nonEmpty && path.IsEmpty) throw new InvalidOperationException("path is empty");
        AssertSafePath(path);
    }

    private static void AssertPermutation(IReadOnlyList<long> permutation)
    {
        // 排列合法性：恰为 0..n-1 的一个排列（越界或重复都拒绝）。
        var seen = new HashSet<long>();
        foreach (var value in permutation)
        {
            if (value < 0 || value >= permutation.Count || !seen.Add(value))
                throw new InvalidOperationException("m permutation is not a valid permutation");
        }
    }
}

/// <summary>路径含保留段时抛出。对应 TS <c>UnsafePathError</c>。</summary>
public sealed class UnsafePathException(object segment)
    : Exception($"unsafe path segment: {segment}")
{
    public object Segment { get; } = segment;
}
