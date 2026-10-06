using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Pi.Durable.Testing;

/// <summary>
/// 断言适配。对应 TS <c>assertions.ts</c> 的 <c>createExpectAssertions(expect)</c>：TS 侧把
/// Vitest/Jest 的 <c>expect</c> 适配到 <see cref="IStorageConformanceAssertions"/>；C# 无外部
/// <c>expect</c> 可适配，故此处直接实现该接口，并保持完全相同的比较语义：
/// <list type="bullet">
/// <item><c>ok</c> / <c>toBeTruthy</c>：真值判断。</item>
/// <item><c>strictEqual</c> / <c>toBe</c>：同一性（C# 用值相等 + 类型判定近似 Object.is）。</item>
/// <item><c>deepEqual</c> / <c>toEqual</c>：递归结构相等（忽略 undefined/null 键的差异，对齐 TS 行为）。</item>
/// <item><c>partialDeepEqual</c> / <c>toMatchObject</c>：递归子集匹配（数组按逐元素匹配）。</item>
/// </list>
/// 失败以 <see cref="ConformanceAssertionException"/> 抛出，便于宿主映射到任意测试运行器的失败通道。
/// </summary>
public static class ConformanceAssertions
{
    /// <summary>创建默认断言实现。</summary>
    public static IStorageConformanceAssertions Create() => DefaultAssertions.Instance;

    /// <summary>创建默认断言实现（环境 conformance 面）。与 <see cref="Create"/> 返回同一实例。</summary>
    public static IEnvConformanceAssertions CreateEnv() => DefaultAssertions.Instance;

    /// <summary>默认断言实现（无状态，可复用）。</summary>
    private sealed class DefaultAssertions : IStorageConformanceAssertions, IEnvConformanceAssertions
    {
        public static readonly DefaultAssertions Instance = new();

        public void Ok(bool value, string? message = null)
        {
            if (!value)
            {
                throw new ConformanceAssertionException(message ?? "Expected value to be truthy");
            }
        }

        public void StrictEqual(object? actual, object? expected)
        {
            if (!IsStrictEqual(actual, expected))
            {
                throw new ConformanceAssertionException(
                    $"Expected {Format(expected)} to be strictly equal to {Format(actual)}");
            }
        }

        public void DeepEqual(object? actual, object? expected)
        {
            if (!DeepEquals(actual, expected, partial: false))
            {
                throw new ConformanceAssertionException(
                    $"Expected {Format(expected)} to deeply equal {Format(actual)}");
            }
        }

        public void PartialDeepEqual(object? actual, object? expected)
        {
            if (!DeepEquals(actual, expected, partial: true))
            {
                throw new ConformanceAssertionException(
                    $"Expected {Format(expected)} to match (partial) {Format(actual)}");
            }
        }

        public void GreaterThan(double actual, double expected)
        {
            if (!(actual > expected))
            {
                throw new ConformanceAssertionException(
                    $"Expected {actual.ToString(CultureInfo.InvariantCulture)} to be greater than {expected.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        public async Task Rejects(Task operation, string messageIncludes)
        {
            try
            {
                await operation.ConfigureAwait(false);
            }
            catch (Exception error) when (messageIncludes.Length == 0 || MessageContains(error, messageIncludes))
            {
                return;
            }
            catch (Exception error)
            {
                throw new ConformanceAssertionException(
                    $"Expected rejection message to include {Format(messageIncludes)} but got {Format(error.Message)}", error);
            }

            throw new ConformanceAssertionException(
                $"Expected operation to reject with a message including {Format(messageIncludes)} but it resolved");
        }

        public async Task Rejects(Func<Task> operation, string messageIncludes)
        {
            Task task;
            try
            {
                task = operation();
            }
            catch (Exception error) when (messageIncludes.Length == 0 || MessageContains(error, messageIncludes))
            {
                return;
            }
            catch (Exception error)
            {
                throw new ConformanceAssertionException(
                    $"Expected rejection message to include {Format(messageIncludes)} but got {Format(error.Message)}", error);
            }

            await Rejects(task, messageIncludes).ConfigureAwait(false);
        }

        private static bool MessageContains(Exception error, string needle)
        {
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                if (current.Message.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }

                // TS 断言的 ".name"（如 "StorageRejected"）对应 C# 异常类型名，故一并匹配。
                if (current.GetType().Name.Contains(needle, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>近似 TS <c>Object.is</c>：数值按值比较，其余按相等（含 null）。</summary>
    private static bool IsStrictEqual(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        if (IsNumber(actual) && IsNumber(expected))
        {
            var a = ToDouble(actual);
            var b = ToDouble(expected);
            // NaN：Object.is(NaN, NaN) 为 true。
            if (double.IsNaN(a) && double.IsNaN(b))
            {
                return true;
            }

            return a.Equals(b);
        }

        if (actual.GetType() != expected.GetType())
        {
            // 允许 32/64 位整数与浮点在数值层面互换（TS 只有 number）。
            return false;
        }

        return Equals(actual, expected);
    }

    private static bool IsNumber(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static double ToDouble(object value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// 深比较。<paramref name="partial"/> 为 true 时做子集匹配（TS <c>toMatchObject</c>）；
    /// 数组在两种模式下都要求逐元素相等长度（TS 的 toMatchObject 对数组也是逐元素、且期望长度须匹配）。
    /// </summary>
    private static bool DeepEquals(object? actual, object? expected, bool partial)
    {
        // undefined 期望在 TS 中代表“无此键”，此处以 null 期望放宽（非 partial 时同样放宽，对齐真值差异）。
        if (expected is null)
        {
            return true;
        }

        if (actual is null)
        {
            return false;
        }

        // 列表 / 数组（必须在字典判定之前：集合类型也有公开属性，会被反射误判为对象）。
        if (TryAsList(actual, out var actualList) && TryAsList(expected, out var expectedList))
        {
            if (actualList.Count != expectedList.Count)
            {
                return false;
            }

            for (var index = 0; index < actualList.Count; index++)
            {
                if (!DeepEquals(actualList[index], expectedList[index], partial))
                {
                    return false;
                }
            }

            return true;
        }

        // 字典 / 对象。
        if (TryAsDictionary(actual, out var actualMap) && TryAsDictionary(expected, out var expectedMap))
        {
            if (!partial && actualMap.Count != expectedMap.Count)
            {
                // 未定义的键（值为 null）在 TS 中不计入长度；此处以“非 null 期望键”计数后比较。
                var expectedDefined = expectedMap.Count(kv => kv.Value is not null);
                var actualDefined = actualMap.Count(kv => kv.Value is not null);
                if (actualDefined != expectedDefined)
                {
                    return false;
                }
            }

            foreach (var pair in expectedMap)
            {
                if (!actualMap.TryGetValue(pair.Key, out var actualValue))
                {
                    if (pair.Value is null)
                    {
                        continue;
                    }

                    return false;
                }

                if (!DeepEquals(actualValue, pair.Value, partial))
                {
                    return false;
                }
            }

            if (!partial)
            {
                foreach (var pair in actualMap)
                {
                    if (pair.Value is null)
                    {
                        continue;
                    }

                    if (!expectedMap.ContainsKey(pair.Key))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // 标量。
        return IsStrictEqual(actual, expected);
    }

    private static bool TryAsDictionary(object value, out IReadOnlyDictionary<string, object?> map)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> typed:
                map = typed;
                return true;
            case IDictionary<string, object?> mutable:
                map = new Dictionary<string, object?>(mutable);
                return true;
        }

        // 记录 / 匿名类型 / 普通对象：按公开属性投影为键值图，使 C# 的类型化记录也能参与
        // TS 风格的深比较 / 子集匹配（TS 侧对象天然是键值图）。
        var type = value.GetType();
        if (type.IsPrimitive || value is string || value is decimal || type.IsEnum
            || value is DateTime or DateTimeOffset or Guid or TimeSpan
            || value is IEnumerable || type.IsArray)
        {
            map = null!;
            return false;
        }

        var properties = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (properties.Length == 0)
        {
            map = null!;
            return false;
        }

        var projected = new Dictionary<string, object?>(properties.Length);
        foreach (var property in properties)
        {
            if (property.GetIndexParameters().Length != 0) continue;
            projected[property.Name] = property.GetValue(value);
        }

        map = projected;
        return true;
    }

    private static bool TryAsList(object value, out IReadOnlyList<object?> list)
    {
        if (value is string)
        {
            list = null!;
            return false;
        }

        switch (value)
        {
            case IReadOnlyList<object?> typed:
                list = typed;
                return true;
            case IEnumerable enumerable:
            {
                var items = new List<object?>();
                foreach (var item in enumerable)
                {
                    items.Add(item);
                }

                list = items;
                return true;
            }
            default:
                list = null!;
                return false;
        }
    }

    private static string Format(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return "\"" + text + "\"";
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? value.GetType().Name;
        }
    }
}

/// <summary>conformance 断言失败。对应 TS 中 <c>expect</c> 抛出的断言错误。</summary>
public sealed class ConformanceAssertionException : Exception
{
    public ConformanceAssertionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
