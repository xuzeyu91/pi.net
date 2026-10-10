// ============================================================================
// Built-in extension registry — port of extensions/index.ts (4d-3)
// ============================================================================
//
// TS registers the four built-in extensions as InlineExtension descriptions; the CLI prepends
// them to the user's extension factories (main.ts: `[...builtInExtensions, ...options.extensionFactories]`).
// The replaceable ones (codemode / tool-search / mcp) are dropped by the resource loader (4e,
// omitReplacedExtensions) when another extension registers the same tool, command, or flag name;
// llama.cpp is not replaceable.
//
// The factories themselves land with the per-extension batches — mcp with 4d-4, codemode with
// 4d-5, tool-search with 4d-6, llama (non-UI) with 4d-7. Until then each entry carries a
// placeholder factory that fails with a clear error naming its batch, the same placeholder
// strategy the 4d-1 contract layer used for 4e/4f types (Types/Placeholders.cs). A placeholder
// that runs surfaces as one aggregated load error, never a silent no-op.

namespace Pi.CodingAgent.Core.Extensions;

/// <summary>
/// The built-in extensions, in load order. Port of <c>builtInExtensions</c> from
/// <c>extensions/index.ts</c>.
/// </summary>
public static class BuiltInExtensions
{
    /// <summary>
    /// The four built-in extensions. Order matches the TS array: llama.cpp first (not
    /// replaceable), then the replaceable codemode / tool-search / mcp.
    /// </summary>
    public static IReadOnlyList<InlineExtension> All { get; } = new InlineExtension[]
    {
        new InlineExtension.Described("llama.cpp", PlaceholderFactory("llama.cpp", "4d-7"), Builtin: true),
        new InlineExtension.Described(
            "codemode", PlaceholderFactory("codemode", "4d-5"), Replaceable: true, Builtin: true),
        new InlineExtension.Described(
            "tool-search", PlaceholderFactory("tool-search", "4d-6"), Replaceable: true, Builtin: true),
        new InlineExtension.Described(
            "mcp", PlaceholderFactory("mcp", "4d-4"), Replaceable: true, Builtin: true),
    };

    private static ExtensionFactory PlaceholderFactory(string name, string batch) =>
        _ => throw new NotSupportedException(
            $"The built-in '{name}' extension factory lands with batch {batch} of the 4d porting plan " +
            $"(docs/4d-extension-system-plan.md). It is registered here as a placeholder so the " +
            $"registry shape (name, replaceable, builtin) is final; replace this factory when porting " +
            $"the extension entry.");
}
