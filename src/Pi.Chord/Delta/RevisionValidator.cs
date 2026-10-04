using System.Collections.Generic;

namespace Pi.Chord.Delta;

/// <summary>
/// 不可变副本修订验证器：跳过之前修订中已验证的容器子树（按引用身份）。
/// 对应 TS <c>revision-validator.ts</c> 的 <c>JsonRevisionValidator</c>。
/// C# 侧值模型约定：对象 = <c>Dictionary&lt;string, object?&gt;</c>，数组 = <c>List&lt;object?&gt;</c>，
/// 标量 = null / bool / long / double / string。
/// </summary>
public sealed class JsonRevisionValidator
{
    private readonly HashSet<object> _validated = new(ReferenceEqualityComparer.Instance);

    /// <summary>验证一份副本值：无环、容器为纯 JSON 容器、数组稠密、键为字符串。原样返回输入。</summary>
    public object? Validate(object? value)
    {
        ValidateCore(value, new HashSet<object>(ReferenceEqualityComparer.Instance),
            new HashSet<object>(ReferenceEqualityComparer.Instance));
        return value;
    }

    private object? ValidateCore(object? value, HashSet<object> ancestors, HashSet<object> finished)
    {
        switch (value)
        {
            case null or bool or string:
            case long or int:
            case double when double.IsFinite((double)value):
                return value;
            case double:
                throw new InvalidOperationException("Replicated state values must be strict JSON");
            case List<object?> array:
            {
                if (_validated.Contains(array) || finished.Contains(array)) return value;
                if (ancestors.Contains(array))
                    throw new InvalidOperationException("Replicated state cannot contain cycles");
                ancestors.Add(array);
                try
                {
                    AssertDenseArray(array);
                    foreach (var item in array) ValidateCore(item, ancestors, finished);
                    finished.Add(array);
                    _validated.Add(array);
                    return value;
                }
                finally
                {
                    ancestors.Remove(array);
                }
            }
            case Dictionary<string, object?> map:
            {
                if (_validated.Contains(map) || finished.Contains(map)) return value;
                if (ancestors.Contains(map))
                    throw new InvalidOperationException("Replicated state cannot contain cycles");
                ancestors.Add(map);
                try
                {
                    foreach (var (key, item) in map) ValidateCore(item, ancestors, finished);
                    finished.Add(map);
                    _validated.Add(map);
                    return value;
                }
                finally
                {
                    ancestors.Remove(map);
                }
            }
            default:
                throw new InvalidOperationException(
                    "Replicated state containers must be plain objects or arrays");
        }
    }

    /// <summary>稠密数组检查：值模型里 C# List 天然稠密，这里验证元素均为已定义值。</summary>
    private static void AssertDenseArray(List<object?> array)
    {
        for (var index = 0; index < array.Count; index++)
        {
            if (array[index] is null)
                throw new InvalidOperationException(
                    "Replicated state arrays must contain enumerable indexed data properties with defined values");
        }
    }
}
