using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;

namespace Pi.Durable.Tools;

/// <summary>
/// <c>write</c> 工具：写入文件（不存在则创建、存在则覆盖，自动建父目录）。对应 TS <c>tools/write.ts</c>。
/// </summary>
public static class WriteTool
{
    /// <summary><c>write</c> 的参数 schema。对应 TS <c>writeSchema</c>。</summary>
    public static ToolSchema Schema { get; } = ToolSchemaBuilder.Object(
        new Dictionary<string, object?>
        {
            ["path"] = ToolSchemaBuilder.String("Path to the file to write (relative or absolute)"),
            ["content"] = ToolSchemaBuilder.String("Content to write to the file"),
        },
        "path",
        "content");

    /// <summary>构建 <c>write</c> 工具注册。对应 TS <c>createWriteTool()</c>。</summary>
    public static IToolRegistration Create() => new Registration();

    private sealed class Registration : IToolRegistration
    {
        public string Name => "write";

        public string Description =>
            "Write content to a file. Creates the file if it doesn't exist, overwrites if it does. " +
            "Automatically creates parent directories.";

        public ToolSchema Parameters => Schema;

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => args;

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var arguments = ToolArgs.Object(args);
            var path = ToolArgs.RequiredString(arguments, "path");
            var content = ToolArgs.RequiredString(arguments, "content");

            var env = ToolsEnv.RequireEnv(api);
            var absolutePath = await ToolPaths.ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
            return await FileMutationQueue.WithAsync(env, absolutePath, async () =>
            {
                if (context.AbortSignal is { IsCancellationRequested: true })
                    throw new InvalidOperationException("Operation aborted");
                (await env.WriteFileAsync(absolutePath, content, context).ConfigureAwait(false)).GetOrThrow();
                if (context.AbortSignal is { IsCancellationRequested: true })
                    throw new InvalidOperationException("Operation aborted");
                return new ToolExecutionResult
                {
                    Content = ToolContent.Text($"Successfully wrote to {path}"),
                };
            }, context).ConfigureAwait(false);
        }
    }
}
