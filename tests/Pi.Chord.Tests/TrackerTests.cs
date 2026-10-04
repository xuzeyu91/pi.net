using Pi.Chord.Delta;
using Xunit;
using Path = Pi.Chord.Delta.Path;

namespace Pi.Chord.Tests;

/// <summary>Tracker 事务语义测试（显式动词 API 版本）。</summary>
public class TrackerTests
{
    private static Dictionary<string, object?> Doc() => new()
    {
        ["name"] = "pi",
        ["nested"] = new Dictionary<string, object?> { ["count"] = 1L },
    };

    [Fact]
    public void ChangeRecordsOpsAndAdoptSwapsValue()
    {
        var tracker = new Tracker<Dictionary<string, object?>>(Doc());
        var change = tracker.BeginChange();
        change.Set(Path.Root.Append(Seg.Key("name")), "pi.net");
        change.Set(Path.Root.Append(Seg.Key("nested")).Append(Seg.Key("count")), 2L);

        var prepared = change.Prepare();
        Assert.Equal(2, prepared.Ops.Count);
        // 核心不变式：apply(base, ops) == prepared.Value。
        var applied = DeltaApply.Apply(prepared.Ops, prepared.Base);
        Assert.Equal(((Dictionary<string, object?>)prepared.Value!)["name"],
            ((Dictionary<string, object?>)applied!)["name"]);
        Assert.Equal(0L, tracker.Revision);

        tracker.Adopt(prepared);
        Assert.Equal(1L, tracker.Revision);
        Assert.Equal("pi.net", tracker.Value["name"]);
        Assert.Equal(2L, ((Dictionary<string, object?>)tracker.Value["nested"]!)["count"]);
    }

    [Fact]
    public void PrepareReplaceEmitsRootReplacement()
    {
        var tracker = new Tracker<Dictionary<string, object?>>(Doc());
        var replacement = new Dictionary<string, object?> { ["fresh"] = true };
        var prepared = tracker.PrepareReplace(replacement);

        var op = Assert.IsType<DeltaOp.Replace>(Assert.Single(prepared.Ops));
        Assert.Same(replacement, op.Value);
        tracker.Adopt(prepared);
        Assert.Same(replacement, tracker.Value);
        Assert.Equal(1L, tracker.Revision);
    }

    [Fact]
    public void AdoptInvalidatesCompetingChanges()
    {
        var tracker = new Tracker<Dictionary<string, object?>>(Doc());
        var first = tracker.BeginChange();
        first.Set(Path.Root.Append(Seg.Key("name")), "a");
        var second = tracker.BeginChange();
        second.Set(Path.Root.Append(Seg.Key("name")), "b");

        tracker.Adopt(first.Prepare());
        // second 基于旧修订：adopt 使其失效（open → stale），再 prepare 直接报 stale。
        Assert.Throws<InvalidOperationException>(() => second.Prepare());
        // prepared 后被竞争采纳作废的路径：先 prepare 再让 adopt 失效。
        var fourth = tracker.BeginChange();
        fourth.Set(Path.Root.Append(Seg.Key("name")), "z");
        var stalePrepared = fourth.Prepare(); // fourth 是当前修订，先 prepare
        var fifth = tracker.BeginChange();
        fifth.Set(Path.Root.Append(Seg.Key("name")), "w");
        tracker.Adopt(fifth.Prepare()); // fifth 采纳后 fourth 的 prepared 变 stale
        Assert.Throws<InvalidOperationException>(() => tracker.Adopt(stalePrepared));

        // 新变更基于新修订。
        var third = tracker.BeginChange();
        third.Set(Path.Root.Append(Seg.Key("name")), "c");
        tracker.Adopt(third.Prepare());
        Assert.Equal("c", tracker.Value["name"]);
    }

    [Fact]
    public void SettledChangeRejectsFurtherUse()
    {
        var tracker = new Tracker<Dictionary<string, object?>>(Doc());
        var change = tracker.BeginChange();
        change.Set(Path.Root.Append(Seg.Key("name")), "x");
        var prepared = change.Prepare();

        // 已 prepare 的 change 不能再记录 op、不能重复 prepare。
        Assert.Throws<InvalidOperationException>(() =>
            change.Set(Path.Root.Append(Seg.Key("name")), "y"));
        Assert.Throws<InvalidOperationException>(() => change.Prepare());

        // abort 后 prepared 不可采纳。
        change.Abort();
        Assert.Throws<InvalidOperationException>(() => tracker.Adopt(prepared));
        // tracker 值未受影响。
        Assert.Equal("pi", tracker.Value["name"]);
    }

    [Fact]
    public void AbortDiscardsOpenChange()
    {
        var tracker = new Tracker<Dictionary<string, object?>>(Doc());
        var change = tracker.BeginChange();
        change.Set(Path.Root.Append(Seg.Key("name")), "x");
        change.Abort();
        Assert.Equal(0L, tracker.Revision);
        Assert.Equal("pi", tracker.Value["name"]);
    }

    [Fact]
    public void ArrayMutationsProduceSpliceAndMove()
    {
        var tracker = new Tracker<Dictionary<string, object?>>(new Dictionary<string, object?>
        {
            ["items"] = new List<object?> { "a", "b", "c" },
        });
        var change = tracker.BeginChange();
        var itemsPath = Path.Root.Append(Seg.Key("items"));
        change.Splice(itemsPath, 3, 0, ["d"]); // 追加
        change.Move(itemsPath, [3L, 0L, 1L, 2L]); // d 移到最前

        tracker.Adopt(change.Prepare());
        Assert.Equal(new List<object?> { "d", "a", "b", "c" }, tracker.Value["items"]);
    }

    [Fact]
    public void RejectsForeignPreparedAndOperationFlood()
    {
        var trackerA = new Tracker<Dictionary<string, object?>>(Doc());
        var trackerB = new Tracker<Dictionary<string, object?>>(Doc());
        var preparedB = trackerB.PrepareReplace([]);

        // 他人的 prepared 不可采纳。
        Assert.Throws<InvalidOperationException>(() => trackerA.Adopt(preparedB));

        // ops 洪水防护。
        var tracker = new Tracker<Dictionary<string, object?>>(Doc());
        var change = tracker.BeginChange();
        var namePath = Path.Root.Append(Seg.Key("name"));
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (var i = 0; i <= Tracker<Dictionary<string, object?>>.MaxDeltaOperations; i++)
                change.Set(namePath, i);
        });
    }
}
