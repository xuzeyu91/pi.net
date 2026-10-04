using Path = Pi.Chord.Delta.Path;
using Pi.Chord.Delta;
using Xunit;

namespace Pi.Chord.Tests;

/// <summary>delta 引擎核心测试：op 应用、路径安全、diff→apply round-trip。</summary>
public class DeltaTests
{
    [Fact]
    public void AppliesSetDeleteAppendSpliceAndMove()
    {
        var value = new Dictionary<string, object?>
        {
            ["name"] = "pi",
            ["nested"] = new Dictionary<string, object?> { ["count"] = 1L },
            ["items"] = new List<object?> { "a", "b", "c" },
        };

        var itemsPath = Path.Root.Append(Seg.Key("items"));
        var result = DeltaApply.Apply([
            new DeltaOp.Set(Path.Root.Append(Seg.Key("nested")).Append(Seg.Key("count")), 2L),
            new DeltaOp.Append(Path.Root.Append(Seg.Key("name")), ".net"),
            new DeltaOp.Splice(itemsPath, 1, 1, ["x"]),
        ], value);

        var map = (Dictionary<string, object?>)result!;
        var nested = (Dictionary<string, object?>)map["nested"]!;
        Assert.Equal(2L, nested["count"]);
        Assert.Equal("pi.net", map["name"]);
        Assert.Equal(new List<object?> { "a", "x", "c" }, map["items"]);
        // 不可变语义：原值未被修改。
        Assert.Equal(1L, ((Dictionary<string, object?>)value["nested"]!)["count"]);
        Assert.Equal(new List<object?> { "a", "b", "c" }, value["items"]);

        // 再删除 name。
        var deleted = DeltaApply.Apply([new DeltaOp.Delete(Path.Root.Append(Seg.Key("name")))], result);
        Assert.False(((Dictionary<string, object?>)deleted!).ContainsKey("name"));
    }

    [Fact]
    public void RootReplaceAndTruncateAndMove()
    {
        // 根替换。
        var replaced = DeltaApply.Apply([new DeltaOp.Replace(new List<object?> { 1L, 2L, 3L, 4L, 5L })], null);
        // 根级：p（splice）与 m（move）允许作用根（对应 TS 的注释语义）。
        var spliced = DeltaApply.Apply([new DeltaOp.Splice(Path.Root, 0, 2, [])], replaced);
        Assert.Equal(new List<object?> { 3L, 4L, 5L }, spliced);
        var moved = DeltaApply.Apply([new DeltaOp.Move(Path.Root, [2L, 0L, 1L])], spliced);
        Assert.Equal(new List<object?> { 5L, 3L, 4L }, moved);
        // t/s/d/a 不能作用根：嵌套路径截断。
        var nested = new Dictionary<string, object?> { ["items"] = new List<object?> { "a", "b", "c", "d" } };
        var truncated = DeltaApply.Apply(
            [new DeltaOp.Truncate(Path.Root.Append(Seg.Key("items")), 2)], nested);
        Assert.Equal(new List<object?> { "a", "b" }, ((Dictionary<string, object?>)truncated!)["items"]);
    }

    [Fact]
    public void RejectsUnsafePathsAndInvalidOps()
    {
        Assert.Throws<UnsafePathException>(() => DeltaApply.Apply(
            [new DeltaOp.Set(Path.Root.Append(Seg.Key("__proto__")), 1L)],
            new Dictionary<string, object?>()));

        Assert.Throws<InvalidOperationException>(() => DeltaApply.Apply(
            [new DeltaOp.Set(Path.Root, 1L)], // s 不能作用于根
            new Dictionary<string, object?>()));

        Assert.Throws<InvalidOperationException>(() => DeltaApply.Apply(
            [new DeltaOp.Move(Path.Root, [0L, 0L])], // 非法排列
            new List<object?> { 1L, 2L }));
    }

    [Fact]
    public void DiffThenApplyRoundTrips()
    {
        var from = new Dictionary<string, object?>
        {
            ["title"] = "hello",
            ["log"] = "line1\nline2\n",
            ["tags"] = new List<object?> { "a", "b", "c", "d" },
            ["removed"] = true,
            ["nested"] = new Dictionary<string, object?> { ["deep"] = 1L },
        };
        var to = new Dictionary<string, object?>
        {
            ["title"] = "hello world",
            ["log"] = "line1\nline2\nline3\n",
            ["tags"] = new List<object?> { "a", "z", "c" },
            ["nested"] = new Dictionary<string, object?> { ["deep"] = 2L, ["extra"] = "x" },
            ["added"] = 42L,
        };

        var ops = DeltaDiff.DiffRevisions(from, to);
        var applied = DeltaApply.Apply(ops, from);
        Assert.True(DeltaDiff.JsonEquals(to, applied), "diff→apply 应收敛到目标值");
    }

    [Fact]
    public void DiffTextAppendUsesAppendOp()
    {
        // a 不能作用根：根字符串的增长收敛为整体替换（与 TS 一致）。
        var rootOps = DeltaDiff.DiffRevisions("abc", "abcdef");
        Assert.IsType<DeltaOp.Replace>(Assert.Single(rootOps));

        // 非根路径的文本增长优先用追加而非替换。
        var from = new Dictionary<string, object?> { ["text"] = "abc" };
        var to = new Dictionary<string, object?> { ["text"] = "abcdef" };
        var ops = DeltaDiff.DiffRevisions(from, to);
        Assert.Contains(ops, op => op is DeltaOp.Append);
        var applied = DeltaApply.Apply(ops, from);
        Assert.Equal("abcdef", ((Dictionary<string, object?>)applied!)["text"]);
    }

    [Fact]
    public void IdenticalValuesProduceEmptyBatch()
    {
        var value = new Dictionary<string, object?> { ["a"] = 1L };
        Assert.Empty(DeltaDiff.DiffRevisions(value, value));
    }
}
