using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// The PowerShell tool. Port of <c>core/tools/powershell.ts</c>: the same shell execution path as
/// bash, with a PowerShell-specific tool description and prompt.
/// </summary>
public static class PowerShellTool
{
    /// <summary>The TS <c>powershellToolSystemPromptContribution</c>.</summary>
    public const string Snippet = "Execute PowerShell commands";

    public static readonly IReadOnlyList<string> Guidelines =
    [
        "Use PowerShell syntax, not bash. For example, use `Get-ChildItem` instead of `ls`, `Select-String` instead of `grep`, and `Copy-Item` instead of `cp`.",
    ];

    private static readonly ShellToolConfig PowerShellToolConfig = new()
    {
        Name = "powershell",
        Label = "powershell",
        ShellName = "PowerShell",
        Prompt = "PS>",
        PromptSnippet = Snippet,
        PromptGuidelines = Guidelines,
        TempFilePrefix = "pi-powershell",
    };

    /// <summary>The TS <c>createPowerShellToolDefinition(cwd, options)</c>.</summary>
    public static ToolDefinition CreatePowerShellToolDefinition(string cwd, BashToolOptions? options = null) =>
        BashTool.CreateShellToolDefinition(cwd, PowerShellToolConfig, options);

    /// <summary>The TS <c>createPowerShellTool(cwd, options)</c>.</summary>
    public static AgentTool CreatePowerShellTool(string cwd, BashToolOptions? options = null)
    {
        var definition = CreatePowerShellToolDefinition(cwd, options);
        return ToolDefinitionWrapper.WrapToolDefinition(definition) with
        {
            PromptSnippet = definition.PromptSnippet,
            PromptGuidelines = definition.PromptGuidelines,
        };
    }
}
