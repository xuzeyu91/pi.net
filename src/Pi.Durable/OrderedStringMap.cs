namespace Pi.Durable;

using System.Collections;

/// <summary>
/// 插入有序的字符串键映射，语义对齐 JS <c>Map</c>：迭代顺序即插入顺序，且
/// <see cref="Remove"/> 后再 <see cref="Set"/> 同一键会把它移到末尾。
/// <para>为什么需要它：TS 的 <c>replaySections</c> / <c>planSections</c> 依赖 <c>Map</c> 的
/// delete+set 位移语义（例如 <c>pi.system</c> 段先被 <c>null</c> 删除再重加，重放顺序随之后移）。
/// C# 的 <see cref="Dictionary{TKey,TValue}"/> 移除后重新插入会复用空闲槽位，迭代顺序不保证位移，
/// 会导致段/工具顺序与实际不一致——故此处显式维护顺序。</para>
/// <para>实现 <see cref="IReadOnlyDictionary{TKey,TValue}"/> 以便作为只读映射传递；
/// 经该接口迭代时仍按插入顺序。</para>
/// </summary>
public sealed class OrderedStringMap<TValue> : IReadOnlyDictionary<string, TValue>
{
    private readonly Dictionary<string, TValue> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    public int Count => _order.Count;

    /// <summary>按插入顺序的键。</summary>
    public IEnumerable<string> Keys => _order;

    /// <summary>按插入顺序的值。</summary>
    public IEnumerable<TValue> Values => _order.Select(key => _values[key]);

    public bool ContainsKey(string key) => _values.ContainsKey(key);

    public bool TryGetValue(string key, out TValue value) => _values.TryGetValue(key, out value!);

    public TValue this[string key] => _values[key];

    /// <summary>设置键值；已存在则就地覆盖，不存在则追加到末尾。</summary>
    public void Set(string key, TValue value)
    {
        if (!_values.ContainsKey(key)) _order.Add(key);
        _values[key] = value;
    }

    /// <summary>移除键；再次 <see cref="Set"/> 会把它移到末尾（对齐 JS <c>Map</c>）。</summary>
    public bool Remove(string key)
    {
        if (!_values.Remove(key)) return false;
        _order.Remove(key);
        return true;
    }

    /// <summary>按插入顺序迭代键值对。</summary>
    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator()
    {
        foreach (var key in _order) yield return new KeyValuePair<string, TValue>(key, _values[key]);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>转换为普通字典（顺序不保证；仅用于不关心顺序的传递）。</summary>
    public Dictionary<string, TValue> ToDictionary()
    {
        var copy = new Dictionary<string, TValue>(StringComparer.Ordinal);
        foreach (var key in _order) copy[key] = _values[key];
        return copy;
    }
}
