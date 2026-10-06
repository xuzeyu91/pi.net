using Pi.Chord.Context;

namespace Pi.Durable.Harness;

/// <summary>
/// 扩展装配的辅助恒等/构造函数。对应 TS harness <c>define.ts</c>
/// （TS 的泛型推断在 C# 由接口直接表达，这里保留构造与包装目标提取）。
/// </summary>
public static class Define
{
    /// <summary>类型化一个扩展的恒等函数。对应 TS <c>defineExtension</c>。</summary>
    public static IExtension Extension(IExtension extension) => extension;

    /// <summary>类型化一个工具的恒等函数。对应 TS <c>defineTool</c>。</summary>
    public static IToolRegistration Tool(IToolRegistration tool) => tool;

    /// <summary>一个提示段；缺省带 tag 包裹。对应 TS <c>section()</c>。</summary>
    public static IPromptSection Section(
        string key, Func<PromptInput, Context, Task<string?>> render, bool? tag = null)
        => new SimpleSection(key, render, tag);

    /// <summary>按名称匹配任务处理器的钩子注册。对应 TS <c>hook()</c>。</summary>
    public static HookRegistration Hook(string taskName, object handlers) => new(taskName, handlers);

    /// <summary>包装与 <paramref name="tool"/> 同名的工具。对应 TS <c>wrapTool()</c>。</summary>
    public static Wrap.Tool WrapTool(
        IToolRegistration tool, Func<IToolRegistration, IToolRegistration> wrapper)
        => new(tool.Name, wrapper);

    /// <summary>包装段 <paramref name="key"/>。对应 TS <c>wrapSection()</c>。</summary>
    public static Wrap.Section WrapSection(
        string key, Func<IPromptSection, IPromptSection> wrapper)
        => new(key, wrapper);

    /// <summary>缺省不包裹 tag 的简单提示段实现（tag 未设时按缺省 true 包裹）。</summary>
    private sealed class SimpleSection(
        string key, Func<PromptInput, Context, Task<string?>> render, bool? tag) : IPromptSection
    {
        public string Key => key;

        public Task<string?> RenderAsync(PromptInput input, Context context) => render(input, context);

        public bool? Tag { get; } = tag;
    }
}
