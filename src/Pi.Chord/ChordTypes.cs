// ============================================================================
// PORT SKELETON - packages/chord (8.8k lines TS): application-composition
// runtime (services, replicated state deltas, RPC, facets, plugins).
// esbuild-based bundling is redesigned as C# AssemblyLoadContext plugins.
// This file carries the JSON value contract that Pi.Protocol depends on.
// ============================================================================

namespace Pi.Chord;

/// <summary>
/// JSON value contract shared across the monorepo. Mirrors TS <c>isJsonValue</c>
/// and <c>copyJson</c> from packages/chord/src/json.ts.
/// </summary>
public static class Json
{
    /// <summary>True when the value tree contains only JSON-compatible primitives.</summary>
    public static bool IsJsonValue(object? value) => value switch
    {
        null or bool or long or int or short or byte or sbyte or ushort or uint or double or float or string => true,
        decimal => true,
        List<object?> list => list.All(IsJsonValue),
        IReadOnlyList<object?> list => list.All(IsJsonValue),
        Dictionary<string, object?> map => map.Values.All(IsJsonValue),
        IReadOnlyDictionary<string, object?> map => map.Values.All(IsJsonValue),
        _ => false,
    };

    /// <summary>
    /// Deep-copies a JSON value tree. Mirrors TS <c>copyJson</c>: by default objects
    /// are fully rebuilt (no shared references); <paramref name="copyPrototypeBuiltinObjects"/>
    /// has no C# equivalent and is kept for API parity documentation only.
    /// </summary>
    public static object? CopyJson(object? value, bool copyPrototypeBuiltinObjects = true) => value switch
    {
        null or bool or long or int or short or byte or sbyte or ushort or uint or double or float or decimal or string => value,
        List<object?> list => list.Select(v => CopyJson(v)).ToList<object?>(),
        IReadOnlyList<object?> list => list.Select(v => CopyJson(v)).ToList<object?>(),
        IEnumerable<object?> list => list.Select(v => CopyJson(v)).ToList<object?>(),
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => CopyJson(kv.Value)),
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => CopyJson(kv.Value)),
        IEnumerable<KeyValuePair<string, object?>> map => map.ToDictionary(kv => kv.Key, kv => CopyJson(kv.Value)),
        _ => throw new ArgumentException($"Unsupported JSON value type: {value.GetType().Name}", nameof(value)),
    };

    /// <summary>结构化拷贝别名（对齐 delta 侧的 deep-clone 命名）。</summary>
    public static object? DeepClone(object? value) => CopyJson(value);

    public static Dictionary<string, object?> EmptyObject() => [];

    public static List<object?> EmptyArray() => [];
}

/// <summary>


/// <summary>
/// Immutable draft handle over replicated state. Mirrors TS <c>Draft</c> from
/// packages/chord/src/delta/draft.ts; the full delta engine (diff/draft/tracker/
/// revision-validator) lands next session.
/// </summary>
public interface IDraft<TState>
{
    TState Current { get; }

    long Revision { get; }
}

/// <summary>
/// Service/facet composition API. Mirrors TS <c>defineService / defineFacet /
/// createFacetHost / replicatedState</c> from packages/chord/src/api.ts - the
/// provider/consumer/instances service machinery is scheduled next session.
/// </summary>
public static class ChordApi
{
    public static ServiceDefinition<TState> DefineService<TState>(string name) => new(name);

    public sealed record ServiceDefinition<TState>(string Name);
}
