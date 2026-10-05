namespace Pi.Ai.Utils;

/// <summary>会话资源清理回调。对应 TS <c>SessionResourceCleanup</c>。</summary>
public delegate void SessionResourceCleanup(string? sessionId);

/// <summary>
/// 会话级资源（如 Codex WebSocket 连接）的清理注册表。对应 TS <c>session-resources.ts</c>：
/// 模块级单例集合；进程退出时逐个执行（异常吞掉避免中断其他清理）。
/// </summary>
public static class SessionResources
{
    private static readonly object Gate = new();
    private static readonly List<SessionResourceCleanup> Cleanups = [];

    static SessionResources()
    {
        // 进程退出时清理（对齐 TS 模块加载即注册的行为）。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupSessionResources();
    }

    /// <summary>注册清理回调，返回反注册委托。</summary>
    public static Func<bool> Register(SessionResourceCleanup cleanup)
    {
        lock (Gate) Cleanups.Add(cleanup);
        return () =>
        {
            lock (Gate) return Cleanups.Remove(cleanup);
        };
    }

    /// <summary>执行全部清理（可按 sessionId 定向）。对应 TS <c>cleanupSessionResources</c>。</summary>
    public static void CleanupSessionResources(string? sessionId = null)
    {
        SessionResourceCleanup[] snapshot;
        lock (Gate) snapshot = [.. Cleanups];
        foreach (var cleanup in snapshot)
        {
            try
            {
                cleanup(sessionId);
            }
            catch
            {
                // 清理失败不中断其他回调（对齐 TS 收集 errors 但仅 debug 打印的语义）。
            }
        }
    }
}
