// ============================================================================
// Slash commands — port of core/slash-commands.ts (4e-1)
// ============================================================================
//
// The built-in command list the interactive mode and the command palette read. Commands contributed
// by extensions, prompt templates and skills are represented by <see cref="SlashCommandInfo"/>, which
// carries where the command came from.

namespace Pi.CodingAgent.Core;

/// <summary>Where a slash command came from. TS <c>SlashCommandSource</c>.</summary>
public static class SlashCommandSource
{
    /// <summary>Registered by an extension.</summary>
    public const string Extension = "extension";

    /// <summary>A prompt template file.</summary>
    public const string Prompt = "prompt";

    /// <summary>A skill.</summary>
    public const string Skill = "skill";
}

/// <summary>One available slash command and its provenance. Port of the TS <c>SlashCommandInfo</c>.</summary>
public sealed record SlashCommandInfo
{
    /// <summary>The command name, without the leading slash.</summary>
    public required string Name { get; init; }

    /// <summary>The one-line description shown in the palette.</summary>
    public string? Description { get; init; }

    /// <summary>One of <see cref="SlashCommandSource"/>.</summary>
    public required string Source { get; init; }

    /// <summary>Provenance of the command's definition.</summary>
    public required SourceInfo SourceInfo { get; init; }
}

/// <summary>A command the CLI implements itself. Port of the TS <c>BuiltinSlashCommand</c>.</summary>
public sealed record BuiltinSlashCommand(string Name, string Description)
{
    /// <summary>The argument placeholder shown after the command name, when it takes one.</summary>
    public string? ArgumentHint { get; init; }
}

/// <summary>Port of <c>core/slash-commands.ts</c>.</summary>
public static class SlashCommands
{
    /// <summary>The built-in commands, in the order the palette lists them.</summary>
    public static readonly IReadOnlyList<BuiltinSlashCommand> BuiltinSlashCommands =
    [
        new("settings", "Open settings menu"),
        new("model", "Select model (opens selector UI)") { ArgumentHint = "<provider/model>" },
        new("tree", "Navigate session tree (switch branches)"),
        new("thinking", "Set thinking level") { ArgumentHint = "<level>" },
        new("scoped-models", "Enable/disable models for Ctrl+P cycling"),
        new("export", "Export session (HTML default, or specify path: .html/.jsonl)"),
        new("import", "Import and resume a session from a JSONL file"),
        new("share", "Share session as a secret GitHub gist"),
        new("bug", "Report a bug to the Pi developers") { ArgumentHint = "<description>" },
        new("copy", "Copy last agent message to clipboard"),
        new("name", "Set session display name"),
        new("session", "Show session info and stats"),
        new("changelog", "Show changelog entries"),
        new("hotkeys", "Show all keyboard shortcuts"),
        new("fork", "Create a new fork from a previous user message"),
        new("clone", "Duplicate the current session at the current position"),
        new("trust", "Save project trust decision for future sessions"),
        new("login", "Configure provider authentication") { ArgumentHint = "<provider>" },
        new("logout", "Remove provider authentication"),
        new("new", "Start a new session"),
        new("compact", "Manually compact the session context"),
        new("resume", "Resume a different session"),
        new("reload", "Reload keybindings, extensions, skills, prompts, themes, and context files"),
        new("quit", $"Quit {Config.AppName}"),
    ];
}
