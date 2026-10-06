namespace Pi.Durable.Testing;

/// <summary>
/// conformance 用例使用的断言门面。对应 TS <c>storage-conformance.ts</c> 内的
/// <c>assertionFacade(assertions)</c> 与 <c>createEnvConformance</c> 内直接调用的 <c>assert.*</c>：
/// TS 返回一个 <c>(actual) =&gt; ({ toBe, toEqual, toMatchObject, … })</c> 动态对象；
/// C# 无动态属性，故改为静态方法集合，语义逐条对齐：
/// <list type="bullet">
/// <item><c>Be(actual, expected)</c> 对应 <c>expect(actual).toBe(expected)</c>（严格相等）。</item>
/// <item><c>Equal(actual, expected)</c> 对应 <c>.toEqual</c>（深相等）。</item>
/// <item><c>MatchObject(actual, expected)</c> 对应 <c>.toMatchObject</c>（深包含）。</item>
/// <item><c>IsUndefined(actual)</c> / <c>IsDefined(actual)</c> / <c>HasLength(actual, n)</c> 对应同名判据。</item>
/// <item><c>GreaterThan(actual, expected)</c> 对应 <c>.toBeGreaterThan</c>。</item>
/// <item><c>Rejects(task, messageIncludes)</c> 对应 <c>await expect(task).rejects.toThrow(...)</c>。</item>
/// <item><c>ResolvesBe(task, expected)</c> 对应 <c>await expect(task).resolves.toBe(...)</c>。</item>
/// </list>
/// <para>C# 无 <c>undefined</c>：TS 用例中的 <c>undefined</c> 一律映射为 <c>null</c>，比较时
/// <c>MatchObject</c> 会跳过“期望侧为 null 的键”，与 TS 忽略 undefined 键的行为一致。</para>
/// </summary>
internal static class Assert
{
    internal static void Ok(IStorageConformanceAssertions assertions, bool value, string? message = null) =>
        assertions.Ok(value, message);

    internal static void StrictEqual(IStorageConformanceAssertions assertions, object? actual, object? expected) =>
        assertions.StrictEqual(actual, expected);

    internal static void DeepEqual(IStorageConformanceAssertions assertions, object? actual, object? expected) =>
        assertions.DeepEqual(actual, expected);

    internal static void PartialDeepEqual(IStorageConformanceAssertions assertions, object? actual, object? expected) =>
        assertions.PartialDeepEqual(actual, expected);

    internal static void GreaterThan(IStorageConformanceAssertions assertions, double actual, double expected) =>
        assertions.GreaterThan(actual, expected);

    internal static Task Rejects(IStorageConformanceAssertions assertions, Task operation, string messageIncludes) =>
        assertions.Rejects(operation, messageIncludes);

    internal static Task Rejects(IStorageConformanceAssertions assertions, Func<Task> operation, string messageIncludes) =>
        assertions.Rejects(operation, messageIncludes);

    // ─── assertionFacade 的等价方法（TS 的 expect(actual).xxx(expected) 形式） ───

    /// <summary>对应 TS <c>expect(actual).toBe(expected)</c>。</summary>
    internal static void Be(IStorageConformanceAssertions assertions, object? actual, object? expected) =>
        assertions.StrictEqual(actual, expected);

    /// <summary>对应 TS <c>expect(actual).toEqual(expected)</c>。</summary>
    internal static void Equal(IStorageConformanceAssertions assertions, object? actual, object? expected) =>
        assertions.DeepEqual(actual, expected);

    /// <summary>对应 TS <c>expect(actual).toMatchObject(expected)</c>。</summary>
    internal static void MatchObject(IStorageConformanceAssertions assertions, object? actual, object? expected) =>
        assertions.PartialDeepEqual(actual, expected);

    /// <summary>对应 TS <c>expect(actual).toBeGreaterThan(expected)</c>。</summary>
    internal static void IsGreaterThan(IStorageConformanceAssertions assertions, double actual, double expected) =>
        assertions.GreaterThan(actual, expected);

    /// <summary>对应 TS <c>expect(actual).toBeUndefined()</c>（C# 中 <c>undefined</c> 即 <c>null</c>）。</summary>
    internal static void IsUndefined(IStorageConformanceAssertions assertions, object? actual) =>
        assertions.StrictEqual(actual, null);

    /// <summary>对应 TS <c>expect(actual).toBeDefined()</c>。</summary>
    internal static void IsDefined(IStorageConformanceAssertions assertions, object? actual, string? message = null) =>
        assertions.Ok(actual is not null, message ?? "Expected value to be defined");

    /// <summary>对应 TS <c>expect(actual).toHaveLength(expected)</c>。</summary>
    internal static void HasLength(IStorageConformanceAssertions assertions, object? actual, long expected)
    {
        var length = actual switch
        {
            System.Collections.ICollection collection => (long)collection.Count,
            string text => text.Length,
            _ => throw new ConformanceAssertionException("Expected a value with a length"),
        };
        assertions.StrictEqual(length, expected);
    }
}
