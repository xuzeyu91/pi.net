namespace Pi.Ai.Utils;

/// <summary>
/// provider 请求头合并工具。对应 TS <c>headersToRecord</c>/<c>providerHeadersToRecord</c>
/// （utils/headers.ts）：多源按序合并，键统一小写，后源覆盖先源；值为 null 表示
/// 抑制（删除）同名默认头；全部为空时返回 null。
/// </summary>
public static class Headers
{
    /// <summary>
    /// 合并多组请求头。对应 TS <c>providerHeadersToRecord(...sources)</c>：
    /// 键小写化；某源中值为 null 时删除该头；结果为空返回 null。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Merge(
        params IReadOnlyDictionary<string, string?>?[]? sources)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources ?? [])
        {
            if (source is null) continue;
            foreach (var (name, value) in source)
            {
                var normalizedName = name.ToLowerInvariant();
                merged.Remove(normalizedName);
                if (value is not null) merged[normalizedName] = value;
            }
        }
        return merged.Count > 0 ? merged : null;
    }
}
