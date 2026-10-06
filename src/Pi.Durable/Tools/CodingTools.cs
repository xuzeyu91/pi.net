using Pi.Durable.Harness;

namespace Pi.Durable.Tools;

/// <summary>
/// 内建编码工具集：<c>read</c> / <c>write</c> / <c>edit</c> / <c>bash</c>。
/// 对应 TS <c>tools/index.ts</c> 的 <c>CodingTools</c>（没有任何地方会自动装上它）。
/// </summary>
public static class CodingTools
{
    /// <summary>四个工具扩展。对应 TS <c>CodingTools</c>。</summary>
    public static IExtension Extension { get; } = new CodingToolsExtension();

    private sealed class CodingToolsExtension : IExtension
    {
        public string Name => "coding-tools";

        public IReadOnlyList<IToolRegistration>? Tools =>
        [
            ReadTool.Create(),
            WriteTool.Create(),
            EditTool.Create(),
            BashTool.Create(),
        ];

        public IReadOnlyList<IPromptSection>? Sections => null;

        public IReadOnlyList<HookRegistration>? Hooks => null;

        public IReadOnlyList<Wrap>? Wraps => null;

        public IReadOnlyList<AnyDurableTask>? Tasks => null;
    }
}
