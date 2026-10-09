using Pi.Ai.Auth;

namespace Pi.Ai.Models;

/// <summary>
/// 一次 provider 目录发布。对应 TS <c>ModelsPublication</c>。
/// </summary>
/// <remarks>
/// TS 用 <c>persist</c> 的三态表达「不动存储 / 删除 / 写入」（<c>undefined</c>/<c>null</c>/对象）；
/// C# 用两个成员表达同一语义：<see cref="PersistDeleted"/> 优先，其次 <see cref="Persist"/>，
/// 两者都缺省即「不动存储」。
/// </remarks>
public sealed record ModelsPublication
{
    /// <summary>删除该 provider 的持久目录（TS <c>persist: null</c>）。</summary>
    public bool PersistDeleted { get; init; }

    /// <summary>写入该目录（TS <c>persist: entry</c>）。</summary>
    public ModelsStoreEntry? Persist { get; init; }

    /// <summary>存储改动之后同步执行的 provider 私有内存状态更新。</summary>
    public Action? Update { get; init; }
}

/// <summary>动态 provider 的刷新上下文。对应 TS <c>RefreshModelsContext</c>。</summary>
public sealed record RefreshModelsContext
{
    /// <summary>生效的已配置凭据；OAuth 凭据在联网前已刷新。</summary>
    public Credential? Credential { get; init; }

    /// <summary>本阶段之前捕获的 provider 作用域目录快照。</summary>
    public ModelsStoreEntry? Stored { get; init; }

    /// <summary>
    /// 带代次校验的发布。持久化策略由 provider 决定；<c>update</c> 只在选中的持久化改动
    /// 完成后同步执行。返回 false 表示发布被更新的刷新取代。
    /// </summary>
    public required Func<ModelsPublication, Task<bool>> Publish { get; init; }

    /// <summary>离线/仅缓存初始化时为 false。</summary>
    public required bool AllowNetwork { get; init; }

    /// <summary>跳过 provider 新鲜度检查、在允许联网时立即抓取。</summary>
    public bool? Force { get; init; }

    /// <summary>恒存在——即便公开的 refresh 调用方省略了 signal。</summary>
    public required CancellationToken Signal { get; init; }
}

/// <summary>刷新选项。对应 TS <c>ModelsRefreshOptions</c>。</summary>
public sealed record ModelsRefreshOptions
{
    public bool? AllowNetwork { get; init; }

    /// <summary>只刷新这些 provider id；未知与静态 provider 被忽略。</summary>
    public IReadOnlyList<string>? Providers { get; init; }

    /// <summary>跳过 provider 新鲜度检查、在允许联网时立即抓取。</summary>
    public bool? Force { get; init; }

    public CancellationToken Signal { get; init; }
}

/// <summary>刷新结果。对应 TS <c>ModelsRefreshResult</c>（provider 错误与取消都不抛出）。</summary>
public sealed record ModelsRefreshResult(bool Aborted, IReadOnlyDictionary<string, Exception> Errors)
{
    public static ModelsRefreshResult Empty { get; } =
        new(false, new Dictionary<string, Exception>(StringComparer.Ordinal));
}
