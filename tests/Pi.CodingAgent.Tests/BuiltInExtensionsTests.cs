using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace Pi.CodingAgent.Tests;

/// <summary>
/// Differential tests for the built-in extension registry (port of <c>extensions/index.ts</c>,
/// batch 4d-3). The TS array is the vector: four entries, llama.cpp first (not replaceable),
/// then the replaceable codemode / tool-search / mcp, all marked builtin.
/// </summary>
public class BuiltInExtensionsTests
{
    [Fact]
    public void All_MatchesTsRegistryShape()
    {
        Assert.Collection(
            BuiltInExtensions.All,
            first => AssertEntry(first, "llama.cpp", replaceable: false, builtin: true),
            second => AssertEntry(second, "codemode", replaceable: true, builtin: true),
            third => AssertEntry(third, "tool-search", replaceable: true, builtin: true),
            fourth => AssertEntry(fourth, "mcp", replaceable: true, builtin: true));
    }

    [Fact]
    public async Task PlaceholderFactory_FailsNamingItsBatch()
    {
        // The factories land with 4d-4 (mcp) / 4d-5 (codemode) / 4d-6 (tool-search) / 4d-7 (llama);
        // until then each placeholder fails loudly instead of registering nothing.
        var llama = Assert.IsType<InlineExtension.Described>(BuiltInExtensions.All[0]);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => llama.Factory(null!));
        Assert.Contains("llama.cpp", error.Message, StringComparison.Ordinal);
        Assert.Contains("4d-7", error.Message, StringComparison.Ordinal);

        var mcp = Assert.IsType<InlineExtension.Described>(BuiltInExtensions.All[3]);
        var mcpError = await Assert.ThrowsAsync<NotSupportedException>(() => mcp.Factory(null!));
        Assert.Contains("mcp", mcpError.Message, StringComparison.Ordinal);
        Assert.Contains("4d-4", mcpError.Message, StringComparison.Ordinal);
    }

    private static void AssertEntry(InlineExtension extension, string name, bool replaceable, bool builtin)
    {
        var described = Assert.IsType<InlineExtension.Described>(extension);
        Assert.Equal(name, described.Name);
        Assert.Equal(replaceable, described.Replaceable);
        Assert.Equal(builtin, described.Builtin);
    }
}
