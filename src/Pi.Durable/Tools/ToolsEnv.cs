using Pi.Durable.Env;
using Pi.Durable.Harness;

namespace Pi.Durable.Tools;

/// <summary>
/// 本次调用的执行环境。对应 TS <c>tools/env.ts</c>：没有环境的工具以普通错误结果失败。
/// </summary>
public static class ToolsEnv
{
    /// <summary>取本次调用的执行环境；未配置时抛出普通异常（由工具任务结算为错误结果）。</summary>
    public static IExecutionEnv RequireEnv(IToolExecutionApi api)
        => api.Env ?? throw new InvalidOperationException("No execution environment is configured");
}
