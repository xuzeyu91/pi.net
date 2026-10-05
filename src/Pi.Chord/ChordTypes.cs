// ============================================================================
// packages/chord (8.8k lines TS): application-composition runtime
// (services, replicated state deltas, RPC, facets, plugins).
// esbuild-based bundling is redesigned as C# AssemblyLoadContext plugins.
//
// 本文件承载 chord 的零散公共契约（types.ts / api.ts / delta 侧句柄）；
// JSON 值契约见 Json.cs（json.ts），其余类型按关注点落在 Facets/、Services/、Delta/。
// ============================================================================

namespace Pi.Chord;

/// <summary>
/// 复制状态的不可变草稿句柄。对应 TS <c>Draft</c>（delta/draft.ts，经 delta/index.ts 导出）。
/// </summary>
public interface IDraft<TState>
{
    TState Current { get; }

    long Revision { get; }
}

/// <summary>
/// Service/facet 组合 API 的骨架入口。对应 TS <c>api.ts</c> 的 <c>defineService</c>；
/// 其余成员（createFacetHost / createStaticFacetLoader / combineFacetLoaders /
/// createRemoteServiceBinding / replicatedState）待 consumer.ts 与 api.ts 完整迁移时补齐。
/// </summary>
public static class ChordApi
{
    public static ServiceDefinition<TState> DefineService<TState>(string name) => new(name);

    public sealed record ServiceDefinition<TState>(string Name);
}
