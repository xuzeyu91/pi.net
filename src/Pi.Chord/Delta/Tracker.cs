using System.Collections;

namespace Pi.Chord.Delta;

/// <summary>
/// 修订跟踪器：对一份严格 JSON 根做事务性变更并产出最小 Op 批。
/// 对应 TS <c>delta/tracker.ts</c> 的公共 API（<c>track</c>/<c>Tracker</c>/<c>Change</c>/<c>Prepared</c>）。
///
/// <para><b>设计差异（C# 无 JS Proxy）</b>：TS 版通过 Proxy 拦截对 Draft 的属性/数组修改再
/// diff 出最小 ops；C# 侧改为显式动词 API（Set/Delete/Append/Truncate/Splice/Move）——
/// 每次调用直接记入 pending ops 并应用到草稿值。核心不变式一致：
/// <c>apply(base, ops) == prepared.Value</c>，且 adopt 是不可失败的指针交换。</para>
/// </summary>
public sealed class Tracker<T> where T : class
{
    /// <summary>单批 ops 上限：防止单个变更产生无界负载。对应 TS <c>MAX_DELTA_OPERATIONS</c>。</summary>
    public const int MaxDeltaOperations = 4_096;

    private readonly List<Change> _openChanges = [];
    private object? _value;
    private long _revision;

    /// <summary>以一份无别名严格 JSON 根开启跟踪（O(1)，不做拷贝）。</summary>
    public Tracker(T initial) => _value = initial;

    /// <summary>当前已采纳的值（只读语义： adopt 前不变）。</summary>
    public T Value => (T)_value!;

    /// <summary>已提交的修订代数（每次 adopt +1）。</summary>
    public long Revision => _revision;

    /// <summary>打开一个变更事务。对应 TS <c>beginChange()</c>。</summary>
    public Change BeginChange()
    {
        var change = new Change(this, _revision, (T)_value!);
        _openChanges.Add(change);
        return change;
    }

    /// <summary>整值替换预备（ops 只含一个 r）。对应 TS <c>prepareReplace()</c>。</summary>
    public Prepared PrepareReplace(T value)
    {
        var change = new Change(this, _revision, (T)_value!);
        change.Replace(value);
        var prepared = change.Prepare();
        return prepared;
    }

    /// <summary>采纳一个 prepared 变更：不可失败的指针交换。对应 TS <c>adopt()</c>。</summary>
    public void Adopt(Prepared prepared)
    {
        if (!ReferenceEquals(prepared.Owner, this))
            throw new InvalidOperationException("Prepared change belongs to a different tracker");
        switch (prepared.Status)
        {
            case ChangeStatus.Consumed:
                throw new InvalidOperationException("Prepared change has already been used");
            case ChangeStatus.Aborted:
                throw new InvalidOperationException("Prepared change has been aborted");
            case ChangeStatus.Stale:
                throw new InvalidOperationException("Prepared change is stale");
            case ChangeStatus.Open:
                throw new InvalidOperationException("Prepared change is not ready");
        }
        if (prepared.BaseRevision != _revision || !Equals(prepared.Base, _value))
        {
            prepared.MarkStale();
            throw new InvalidOperationException("Prepared change is stale");
        }
        // 物化发生在 prepare；采纳是 O(1) 指针交换，存储失败可丢弃候选而不影响权威值。
        _value = prepared.Value;
        prepared.MarkConsumed();
        _revision += 1;
        // 使所有其他打开/已预备的变更失效。
        foreach (var other in _openChanges)
        {
            if (!ReferenceEquals(other, prepared.Source)) other.Invalidate();
        }
        _openChanges.Clear();
    }

    internal void Forget(Change change) => _openChanges.Remove(change);

    /// <summary>
    /// 变更事务：显式动词记录 ops 并同步应用到草稿值。
    /// 对应 TS <c>Change</c>（<c>state</c> 可变视图的 C# 显式替代）。
    /// </summary>
    public sealed class Change
    {
        private readonly Tracker<T> _owner;
        private readonly T _base;
        private object? _draft;
        private readonly List<DeltaOp> _ops = [];
        private ChangeStatus _status = ChangeStatus.Open;
        private bool _replacement;
        private object? _replacementValue;

        internal Change(Tracker<T> owner, long baseRevision, T baseValue)
        {
            _owner = owner;
            BaseRevision = baseRevision;
            _base = baseValue;
            _draft = baseValue;
        }

        /// <summary>变更基于的修订代数。</summary>
        public long BaseRevision { get; }

        internal Tracker<T> Owner => _owner;

        internal ChangeStatus Status => _status;

        /// <summary>当前草稿值（已应用全部 pending ops）。</summary>
        public T Draft => (T)_draft!;

        /// <summary>pending ops 数量。</summary>
        public int OpCount => _ops.Count;

        private void AssertOpen()
        {
            if (_status != ChangeStatus.Open)
                throw new InvalidOperationException("Cannot use a settled change");
        }

        private void Record(DeltaOp op)
        {
            AssertOpen();
            if (_ops.Count >= MaxDeltaOperations)
                throw new InvalidOperationException($"Change exceeds {MaxDeltaOperations} operations");
            _ops.Add(op);
            // 同步应用到草稿值：保证 apply(base, ops) == draft 的不变式可见。
            List<DeltaOp> batch = [op];
            _draft = DeltaApply.Apply(batch, _draft) ?? _draft;
        }

        /// <summary>记录整值替换（仅允许作为唯一 op）。</summary>
        public void Replace(T value)
        {
            AssertOpen();
            if (_ops.Count > 0)
                throw new InvalidOperationException("Replace must be the only operation in a change");
            _replacement = true;
            _replacementValue = value;
            _draft = value;
        }

        /// <summary>路径处设值（r 之外的 JSON 标量/容器）。</summary>
        public void Set(Path path, object? value) => Record(new DeltaOp.Set(path, value));

        /// <summary>删除路径处成员。</summary>
        public void Delete(Path path) => Record(new DeltaOp.Delete(path));

        /// <summary>路径处文本追加。</summary>
        public void Append(Path path, string text) => Record(new DeltaOp.Append(path, text));

        /// <summary>路径处数组截断。</summary>
        public void Truncate(Path path, long length) => Record(new DeltaOp.Truncate(path, length));

        /// <summary>路径处数组 splice。</summary>
        public void Splice(Path path, long index, long remove, IReadOnlyList<object?> items)
            => Record(new DeltaOp.Splice(path, index, remove, items));

        /// <summary>路径处数组重排（置换必须是双射）。</summary>
        public void Move(Path path, IReadOnlyList<long> permutation) => Record(new DeltaOp.Move(path, permutation));

        /// <summary>物化 prepared：ops 与值就此冻结。对应 TS <c>Change.prepare()</c>。</summary>
        public Prepared Prepare()
        {
            if (_status == ChangeStatus.Stale)
                throw new InvalidOperationException("Change is stale");
            if (_status is not ChangeStatus.Open)
                throw new InvalidOperationException("Change has already been settled");
            _status = ChangeStatus.Prepared;
            try
            {
                List<DeltaOp> ops;
                object? value;
                if (_replacement)
                {
                    ops = [new DeltaOp.Replace(_replacementValue)];
                    value = _replacementValue;
                }
                else
                {
                    ops = [.. _ops];
                    value = _draft;
                }
                var prepared = new Prepared(_owner, this, _base, value, ops, BaseRevision);
                _owner.Forget(this);
                return prepared;
            }
            catch
            {
                _status = ChangeStatus.Aborted;
                throw;
            }
        }

        /// <summary>放弃变更（未 prepare）或把 prepared 作废（已 prepare 未采纳）。</summary>
        public void Abort()
        {
            if (_status == ChangeStatus.Open)
            {
                _status = ChangeStatus.Aborted;
                _owner.Forget(this);
            }
            else if (_status == ChangeStatus.Prepared)
            {
                _status = ChangeStatus.Aborted;
            }
        }

        internal void Invalidate()
        {
            if (_status is ChangeStatus.Open or ChangeStatus.Prepared) _status = ChangeStatus.Stale;
        }
    }

    /// <summary>
    /// 已物化的变更候选：base / value / ops 与其基于的修订代数。
    /// 对应 TS <c>Prepared</c>。
    /// </summary>
    public sealed class Prepared
    {
        internal Prepared(Tracker<T> owner, Change source, T baseValue, object? value,
            IReadOnlyList<DeltaOp> ops, long baseRevision)
        {
            Owner = owner;
            Source = source;
            Base = baseValue;
            Value = value;
            Ops = ops;
            BaseRevision = baseRevision;
        }

        internal Tracker<T> Owner { get; }

        internal Change Source { get; }

        internal ChangeStatus Status => Source.Status;

        /// <summary>变更基于的值（采纳前必须仍等于 tracker 当前值）。</summary>
        public T Base { get; }

        /// <summary>采纳后的新值。</summary>
        public object? Value { get; }

        /// <summary>从 base 到 value 的完整 op 批。</summary>
        public IReadOnlyList<DeltaOp> Ops { get; }

        /// <summary>变更基于的修订代数（adopt 时校验）。</summary>
        public long BaseRevision { get; }

        /// <summary>作废这份候选。</summary>
        public void Abort() => Source.Abort();

        internal void MarkConsumed() { }

        internal void MarkStale() => Source.Invalidate();
    }
}

/// <summary>变更状态机。对应 TS <c>Status</c>。</summary>
internal enum ChangeStatus
{
    Open,
    Prepared,
    Consumed,
    Aborted,
    Stale,
}
