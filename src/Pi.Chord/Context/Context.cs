using System.Diagnostics.CodeAnalysis;
namespace Pi.Chord.Context;

/// <summary>上下文键：token 即键本身（引用相等）。对应 TS <c>ContextKey</c>（symbol token）。</summary>
public sealed class ContextKey<T>
{
    /// <summary>创建带描述的键（描述仅用于诊断输出）。</summary>
    public ContextKey(string description) => Description = description;

    public string Description { get; }
}

/// <summary>不可变的调用作用域值链，随操作显式传递。对应 TS <c>Context</c>。</summary>
public abstract class Context
{
    /// <summary>取消信号（约定键 chord.abortSignal）。</summary>
    public abstract CancellationToken? AbortSignal { get; }

    /// <summary>按键查值；无则 undefined。</summary>
    [return: MaybeNull]
    public abstract T Value<T>(ContextKey<T> key);

    /// <summary>派生一个携带新值的子上下文。</summary>
    public Context WithValue<T>(ContextKey<T> key, T value) => new ValueContext<T>(this, key, value);

    /// <summary>空上下文（只有名字）。对应 TS <c>EmptyContext</c>。</summary>
    private sealed class EmptyContext : Context
    {
        private readonly string _name;

        public EmptyContext(string name) => _name = name;

        public override CancellationToken? AbortSignal => null;

        [return: MaybeNull]
        public override T Value<T>(ContextKey<T> key) => default;

        public override string ToString() => _name;
    }

    /// <summary>单值链节点。对应 TS <c>ContextValue</c>。</summary>
    private sealed class ValueContext<TValue> : Context
    {
        private readonly Context _parent;
        private readonly ContextKey<TValue> _key;
        private readonly TValue _value;

        public ValueContext(Context parent, ContextKey<TValue> key, TValue value)
            => (_parent, _key, _value) = (parent, key, value);

        public override CancellationToken? AbortSignal => _parent.AbortSignal;

        [return: MaybeNull]
        public override TOther Value<TOther>(ContextKey<TOther> key)
        {
            if (ReferenceEquals(key, (object?)_key)) return (TOther)(object?)_value!;
            return _parent.Value(key);
        }

        public override string ToString() => $"{_parent}.WithValue({_key.Description})";
    }

    /// <summary>后台上下文：无调用者的合成交付用。对应 TS <c>BACKGROUND_CONTEXT</c>。</summary>
    public static readonly Context Background = new EmptyContext("[Context BACKGROUND_CONTEXT]");

    /// <summary>开发期占位上下文。对应 TS <c>TODO_CONTEXT</c>。</summary>
    public static readonly Context Todo = new EmptyContext("[Context TODO_CONTEXT]");

    /// <summary>创建取消信号键。对应 TS <c>ABORT_SIGNAL_CONTEXT_KEY</c>。</summary>
    public static readonly ContextKey<CancellationToken?> AbortSignalKey = new("chord.abortSignal");
}
