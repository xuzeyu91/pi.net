using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pi.Tui;

/// <summary>A single autocomplete suggestion (port of the TS <c>AutocompleteItem</c>).</summary>
public sealed record AutocompleteItem
{
    public required string Value { get; init; }

    public required string Label { get; init; }

    public string? Description { get; init; }
}

/// <summary>Result of a suggestion query (port of the TS <c>AutocompleteSuggestions</c>).</summary>
public sealed record AutocompleteSuggestions
{
    public required IReadOnlyList<AutocompleteItem> Items { get; init; }

    /// <summary>What the items were matched against, e.g. <c>"/"</c> or <c>"src/"</c>.</summary>
    public required string Prefix { get; init; }
}

/// <summary>Outcome of applying a completion (port of the TS inline return type).</summary>
public sealed record CompletionApplication(string[] Lines, int CursorLine, int CursorCol);

/// <summary>
/// Options for <see cref="CombinedAutocompleteProvider.GetSuggestionsAsync"/>. The TS
/// <c>AbortSignal</c> becomes a <see cref="CancellationToken"/> (deviation T17).
/// </summary>
public readonly record struct AutocompleteRequest(CancellationToken Signal, bool Force = false);

/// <summary>
/// Either a <see cref="SlashCommand"/> or a bare <see cref="AutocompleteItem"/>, mirroring the TS
/// <c>SlashCommand | AutocompleteItem</c> union accepted by the provider constructor.
/// </summary>
public abstract class AutocompleteCommand
{
    internal abstract string CommandName { get; }

    internal abstract string? CommandDescription { get; }

    internal abstract string? CommandArgumentHint { get; }

    internal abstract Func<string, Task<IReadOnlyList<AutocompleteItem>?>>? CommandArgumentCompletions { get; }

    public static implicit operator AutocompleteCommand(AutocompleteItem item) => new ItemCommand { Item = item };
}

/// <summary>A slash command with an optional argument-completion callback.</summary>
public sealed class SlashCommand : AutocompleteCommand
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? ArgumentHint { get; init; }

    /// <summary>Returns argument completions, or <c>null</c> when none are available.</summary>
    public Func<string, Task<IReadOnlyList<AutocompleteItem>?>>? GetArgumentCompletions { get; init; }

    internal override string CommandName => Name;

    internal override string? CommandDescription => Description;

    internal override string? CommandArgumentHint => ArgumentHint;

    internal override Func<string, Task<IReadOnlyList<AutocompleteItem>?>>? CommandArgumentCompletions => GetArgumentCompletions;
}

/// <summary>Wraps a bare <see cref="AutocompleteItem"/> so it can be used as a command entry.</summary>
public sealed class ItemCommand : AutocompleteCommand
{
    public required AutocompleteItem Item { get; init; }

    internal override string CommandName => Item.Value;

    internal override string? CommandDescription => Item.Description;

    internal override string? CommandArgumentHint => null;

    internal override Func<string, Task<IReadOnlyList<AutocompleteItem>?>>? CommandArgumentCompletions => null;
}

/// <summary>Port of <c>autocomplete.ts</c>: slash-command and file-path completion.</summary>
public sealed class CombinedAutocompleteProvider
{
    private static readonly HashSet<char> PathDelimiters = [' ', '\t', '"', '\'', '='];

    // Opening wrappers that may precede a path in prose, mapped to their closing counterpart.
    private static readonly Dictionary<char, char> PathWrappers = new()
    {
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
        ['<'] = '>',
        ['`'] = '`',
    };

    private readonly IReadOnlyList<AutocompleteCommand> _commands;
    private readonly string _basePath;
    private readonly string? _fdPath;

    public CombinedAutocompleteProvider(string basePath, IEnumerable<AutocompleteCommand>? commands = null, string? fdPath = null)
    {
        _commands = commands?.ToList() ?? [];
        _basePath = basePath;
        _fdPath = fdPath;
    }

    /// <summary>
    /// Test seam replacing the <c>fd</c> child process (the sandbox equivalent of the harness's
    /// <c>child_process</c> shim). Receives the argument list, returns the exit code and stdout.
    /// </summary>
    internal static Func<IReadOnlyList<string>, (int ExitCode, string Stdout)>? FdProcessOverride { get; set; }

    public async Task<AutocompleteSuggestions?> GetSuggestionsAsync(
        string[] lines,
        int cursorLine,
        int cursorCol,
        AutocompleteRequest options)
    {
        var currentLine = LineAt(lines, cursorLine);
        var textBeforeCursor = JsString.Slice(currentLine, 0, cursorCol);

        var atPrefix = ExtractAtPrefix(textBeforeCursor);
        if (atPrefix is not null)
        {
            var (rawPrefix, _, isQuotedPrefix) = ParsePathPrefix(atPrefix);
            var atSuggestions = await GetFuzzyFileSuggestionsAsync(rawPrefix, isQuotedPrefix, options.Signal).ConfigureAwait(false);
            if (atSuggestions.Count == 0)
            {
                return null;
            }

            return new AutocompleteSuggestions { Items = atSuggestions, Prefix = atPrefix };
        }

        var commandText = JsString.TrimStart(textBeforeCursor);
        if (!options.Force && commandText.StartsWith('/'))
        {
            var spaceIndex = commandText.IndexOf(' ');
            if (spaceIndex == -1)
            {
                var prefix = JsString.Slice(commandText, 1);
                var commandItems = _commands.Select(command =>
                {
                    var name = command.CommandName;
                    var hint = string.IsNullOrEmpty(command.CommandArgumentHint) ? null : command.CommandArgumentHint;
                    var desc = command.CommandDescription ?? "";
                    var fullDesc = hint is not null ? (desc.Length > 0 ? $"{hint} — {desc}" : hint) : desc;
                    return new CommandEntry(name, name, fullDesc.Length > 0 ? fullDesc : null);
                }).ToList();

                // Skills are listed under their bare name as well, so that "idea" matches
                // "skill:research-idea" without the user typing the "skill:" prefix.
                var bareNameMatches = Fuzzy.Filter(
                    commandItems,
                    prefix,
                    item => item.Name.StartsWith("skill:", StringComparison.Ordinal)
                        ? JsString.Slice(item.Name, "skill:".Length)
                        : item.Name);
                var bareNameMatchSet = new HashSet<CommandEntry>(bareNameMatches, ReferenceEqualityComparer.Instance);
                var fullNameOnlyMatches = Fuzzy.Filter(
                    commandItems.Where(item =>
                        item.Name.StartsWith("skill:", StringComparison.Ordinal) && !bareNameMatchSet.Contains(item)),
                    prefix,
                    item => item.Name);

                var filtered = bareNameMatches
                    .Concat(fullNameOnlyMatches)
                    .Select(item => new AutocompleteItem
                    {
                        Value = item.Name,
                        Label = item.Label,
                        Description = string.IsNullOrEmpty(item.Description) ? null : item.Description,
                    })
                    .ToList();

                if (filtered.Count == 0)
                {
                    return null;
                }

                return new AutocompleteSuggestions { Items = filtered, Prefix = commandText };
            }

            var commandName = JsString.Slice(commandText, 1, spaceIndex);
            var argumentText = JsString.Slice(commandText, spaceIndex + 1);

            var command = _commands.FirstOrDefault(c => c.CommandName == commandName);
            var argumentCompletions = command?.CommandArgumentCompletions;
            if (command is null || argumentCompletions is null)
            {
                return null;
            }

            var argumentSuggestions = await argumentCompletions(argumentText).ConfigureAwait(false);
            if (argumentSuggestions is null || argumentSuggestions.Count == 0)
            {
                return null;
            }

            return new AutocompleteSuggestions { Items = argumentSuggestions, Prefix = argumentText };
        }

        var pathMatch = ExtractPathPrefix(textBeforeCursor, options.Force);
        if (pathMatch is null)
        {
            return null;
        }

        var fileSuggestions = GetFileSuggestions(pathMatch);
        if (fileSuggestions.Count == 0)
        {
            return null;
        }

        return new AutocompleteSuggestions { Items = fileSuggestions, Prefix = pathMatch };
    }

    public CompletionApplication ApplyCompletion(
        string[] lines,
        int cursorLine,
        int cursorCol,
        AutocompleteItem item,
        string prefix)
    {
        var currentLine = LineAt(lines, cursorLine);
        var beforePrefix = JsString.Slice(currentLine, 0, cursorCol - prefix.Length);
        var afterCursor = JsString.Slice(currentLine, cursorCol);
        var isQuotedPrefix = prefix.StartsWith('"') || prefix.StartsWith("@\"", StringComparison.Ordinal);
        var hasLeadingQuoteAfterCursor = afterCursor.StartsWith('"');
        var hasTrailingQuoteInItem = item.Value.EndsWith('"');
        var adjustedAfterCursor =
            isQuotedPrefix && hasTrailingQuoteInItem && hasLeadingQuoteAfterCursor
                ? JsString.Slice(afterCursor, 1)
                : afterCursor;

        var isDirectory = item.Label.EndsWith('/');
        var hasTrailingQuote = item.Value.EndsWith('"');
        var cursorOffset = isDirectory && hasTrailingQuote ? item.Value.Length - 1 : item.Value.Length;

        // Slash commands sit at the start of the line and have no path separator after the first "/".
        var isSlashCommand = prefix.StartsWith('/')
            && JsString.Trim(beforePrefix).Length == 0
            && !JsString.Slice(prefix, 1).Contains('/');
        if (isSlashCommand)
        {
            var newLine = $"{beforePrefix}/{item.Value} {adjustedAfterCursor}";
            return new CompletionApplication(
                SetLine(lines, cursorLine, newLine),
                cursorLine,
                beforePrefix.Length + item.Value.Length + 2);
        }

        // File attachment: don't add a space after directories so the user can keep completing.
        if (prefix.StartsWith('@'))
        {
            var suffix = isDirectory ? "" : " ";
            var newLine = $"{beforePrefix}{item.Value}{suffix}{adjustedAfterCursor}";
            return new CompletionApplication(
                SetLine(lines, cursorLine, newLine),
                cursorLine,
                beforePrefix.Length + cursorOffset + suffix.Length);
        }

        var textBeforeCursor = JsString.Slice(currentLine, 0, cursorCol);
        if (textBeforeCursor.Contains('/') && textBeforeCursor.Contains(' '))
        {
            // Likely a command argument completion.
            var newLine = beforePrefix + item.Value + adjustedAfterCursor;
            return new CompletionApplication(
                SetLine(lines, cursorLine, newLine),
                cursorLine,
                beforePrefix.Length + cursorOffset);
        }

        var completedLine = beforePrefix + item.Value + adjustedAfterCursor;
        return new CompletionApplication(
            SetLine(lines, cursorLine, completedLine),
            cursorLine,
            beforePrefix.Length + cursorOffset);
    }

    /// <summary>Whether Tab should trigger file completion at the cursor.</summary>
    public bool ShouldTriggerFileCompletion(string[] lines, int cursorLine, int cursorCol)
    {
        var currentLine = LineAt(lines, cursorLine);
        var textBeforeCursor = JsString.Slice(currentLine, 0, cursorCol);

        // Don't trigger while typing a slash command at the start of the line.
        var trimmed = JsString.Trim(textBeforeCursor);
        if (trimmed.StartsWith('/') && !trimmed.Contains(' '))
        {
            return false;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Module-level helpers (port of the TS module-private functions)
    // ------------------------------------------------------------------

    internal static string ToDisplayPath(string value) => value.Replace('\\', '/');

    internal static string EscapeRegex(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '.' or '*' or '+' or '?' or '^' or '$' or '{' or '}' or '(' or ')' or '|' or '[' or ']' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    internal static string BuildFdPathQuery(string query)
    {
        var normalized = ToDisplayPath(query);
        if (!normalized.Contains('/'))
        {
            return normalized;
        }

        var hasTrailingSeparator = normalized.EndsWith('/');
        var trimmed = normalized.Trim('/');
        if (trimmed.Length == 0)
        {
            return normalized;
        }

        const string separatorPattern = "[\\\\/]";
        var segments = trimmed
            .Split('/')
            .Where(segment => segment.Length > 0)
            .Select(EscapeRegex)
            .ToList();
        if (segments.Count == 0)
        {
            return normalized;
        }

        var pattern = string.Join(separatorPattern, segments);
        if (hasTrailingSeparator)
        {
            pattern += separatorPattern;
        }

        return pattern;
    }

    internal static int FindLastDelimiter(string text)
    {
        var lastDelimiter = -1;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            index += rune.Utf16SequenceLength;
            if (rune.Utf16SequenceLength == 1 && PathDelimiters.Contains((char)rune.Value))
            {
                lastDelimiter = index - 1;
            }
            else if (IsSeparator(rune.Value))
            {
                lastDelimiter = index - 1;
            }
        }

        return lastDelimiter;
    }

    /// <summary>
    /// TS <c>autocompleteSeparatorRegex.test(character)</c>: the JavaScript <c>\s</c> set plus CJK
    /// punctuation. Carried as a generated code-point table because .NET's <c>\s</c> differs and
    /// character classes cannot express astral code points such as U+16FE2.
    /// </summary>
    internal static bool IsSeparator(int codePoint) => InRanges(AutocompleteData.SeparatorRanges, codePoint);

    internal static bool ContainsSeparator(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsSeparator(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>TS <c>tokenStartRegex.test(text)</c> = <c>(?:^|separator)$</c>: empty, or ends with a separator.</summary>
    internal static bool IsTokenBoundary(string text)
    {
        if (text.Length == 0)
        {
            return true;
        }

        var last = text.EnumerateRunes().Last();
        return IsSeparator(last.Value);
    }

    private static bool InRanges((int Start, int End)[] ranges, int codePoint)
    {
        foreach (var (start, end) in ranges)
        {
            if (codePoint >= start && codePoint <= end)
            {
                return true;
            }
        }

        return false;
    }

    internal static string StripLeadingWrappers(string token)
    {
        var result = token;
        while (result.Length > 0)
        {
            if (!PathWrappers.TryGetValue(result[0], out var closer) || result.IndexOf(closer, 1) >= 0)
            {
                break;
            }

            result = result[1..];
        }

        return result;
    }

    internal static int? FindUnclosedQuoteStart(string text)
    {
        var inQuotes = false;
        var quoteStart = -1;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '"')
            {
                continue;
            }

            inQuotes = !inQuotes;
            if (inQuotes)
            {
                quoteStart = i;
            }
        }

        return inQuotes ? quoteStart : null;
    }

    internal static bool IsTokenStart(string text, int index)
    {
        var start = index;
        while (start > 0 && PathWrappers.ContainsKey(JsString.CharAt(text, start - 1)))
        {
            start--;
        }

        return PathDelimiters.Contains(JsString.CharAt(text, start - 1))
            || IsTokenBoundary(JsString.Slice(text, 0, start));
    }

    internal static string? ExtractQuotedPrefix(string text)
    {
        var quoteStart = FindUnclosedQuoteStart(text);
        if (quoteStart is null)
        {
            return null;
        }

        if (quoteStart.Value > 0 && text[quoteStart.Value - 1] == '@')
        {
            return IsTokenStart(text, quoteStart.Value - 1) ? JsString.Slice(text, quoteStart.Value - 1) : null;
        }

        return IsTokenStart(text, quoteStart.Value) ? JsString.Slice(text, quoteStart.Value) : null;
    }

    internal static (string RawPrefix, bool IsAtPrefix, bool IsQuotedPrefix) ParsePathPrefix(string prefix)
    {
        if (prefix.StartsWith("@\"", StringComparison.Ordinal))
        {
            return (JsString.Slice(prefix, 2), true, true);
        }

        if (prefix.StartsWith('"'))
        {
            return (JsString.Slice(prefix, 1), false, true);
        }

        if (prefix.StartsWith('@'))
        {
            return (JsString.Slice(prefix, 1), true, false);
        }

        return (prefix, false, false);
    }

    internal static string BuildCompletionValue(string path, bool isAtPrefix, bool isQuotedPrefix)
    {
        var needsQuotes = isQuotedPrefix || ContainsSeparator(path);
        var prefix = isAtPrefix ? "@" : "";

        return needsQuotes ? $"{prefix}\"{path}\"" : prefix + path;
    }

    // ------------------------------------------------------------------
    // Provider internals
    // ------------------------------------------------------------------

    /// <summary>Extract the <c>@</c> prefix for fuzzy file suggestions.</summary>
    private static string? ExtractAtPrefix(string text)
    {
        var quotedPrefix = ExtractQuotedPrefix(text);
        if (quotedPrefix?.StartsWith("@\"", StringComparison.Ordinal) == true)
        {
            return quotedPrefix;
        }

        var lastDelimiterIndex = FindLastDelimiter(text);
        var token = StripLeadingWrappers(
            lastDelimiterIndex == -1 ? text : JsString.Slice(text, lastDelimiterIndex + 1));

        return token.StartsWith('@') ? token : null;
    }

    /// <summary>Extract a path-like prefix from the text before the cursor.</summary>
    private static string? ExtractPathPrefix(string text, bool forceExtract = false)
    {
        var quotedPrefix = ExtractQuotedPrefix(text);
        if (quotedPrefix is not null)
        {
            return quotedPrefix;
        }

        var lastDelimiterIndex = FindLastDelimiter(text);
        var pathPrefix = StripLeadingWrappers(
            lastDelimiterIndex == -1 ? text : JsString.Slice(text, lastDelimiterIndex + 1));

        if (forceExtract)
        {
            return pathPrefix;
        }

        if (pathPrefix.Contains('/') || pathPrefix.StartsWith('.') || pathPrefix.StartsWith("~/", StringComparison.Ordinal))
        {
            return pathPrefix;
        }

        // An empty prefix after whitespace or CJK punctuation still triggers, but empty text does not.
        if (pathPrefix.Length == 0 && text.Length != 0 && IsTokenBoundary(text))
        {
            return pathPrefix;
        }

        return null;
    }

    /// <summary>Expand a leading <c>~/</c> to the user's home directory.</summary>
    private static string ExpandHomePath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            var expandedPath = NodePath.Join(home, JsString.Slice(path, 2));
            return path.EndsWith('/') && !expandedPath.EndsWith('/') ? $"{expandedPath}/" : expandedPath;
        }

        return path == "~" ? home : path;
    }

    private ScopedQuery? ResolveScopedFuzzyQuery(string rawQuery)
    {
        var normalizedQuery = ToDisplayPath(rawQuery);
        var slashIndex = normalizedQuery.LastIndexOf('/');
        if (slashIndex == -1)
        {
            return null;
        }

        var displayBase = JsString.Slice(normalizedQuery, 0, slashIndex + 1);
        var query = JsString.Slice(normalizedQuery, slashIndex + 1);

        string baseDir;
        if (displayBase.StartsWith("~/", StringComparison.Ordinal))
        {
            baseDir = ExpandHomePath(displayBase);
        }
        else if (displayBase.StartsWith('/'))
        {
            baseDir = displayBase;
        }
        else
        {
            baseDir = NodePath.Join(_basePath, displayBase);
        }

        return Directory.Exists(baseDir) ? new ScopedQuery(baseDir, query, displayBase) : null;
    }

    private static string ScopedPathForDisplay(string displayBase, string relativePath)
    {
        var normalizedRelativePath = ToDisplayPath(relativePath);
        return displayBase == "/"
            ? $"/{normalizedRelativePath}"
            : $"{ToDisplayPath(displayBase)}{normalizedRelativePath}";
    }

    /// <summary>File/directory suggestions for a given path prefix (readdir-based, no <c>fd</c>).</summary>
    private List<AutocompleteItem> GetFileSuggestions(string prefix)
    {
        try
        {
            string searchDir;
            string searchPrefix;
            var (rawPrefix, isAtPrefix, isQuotedPrefix) = ParsePathPrefix(prefix);
            var expandedPrefix = rawPrefix;

            if (expandedPrefix.StartsWith('~'))
            {
                expandedPrefix = ExpandHomePath(expandedPrefix);
            }

            var isRootPrefix = rawPrefix is "" or "./" or "../" or "~" or "~/" or "/"
                || (isAtPrefix && rawPrefix.Length == 0);

            if (isRootPrefix)
            {
                searchDir = rawPrefix.StartsWith('~') || expandedPrefix.StartsWith('/')
                    ? expandedPrefix
                    : NodePath.Join(_basePath, expandedPrefix);
                searchPrefix = "";
            }
            else if (rawPrefix.EndsWith('/'))
            {
                searchDir = rawPrefix.StartsWith('~') || expandedPrefix.StartsWith('/')
                    ? expandedPrefix
                    : NodePath.Join(_basePath, expandedPrefix);
                searchPrefix = "";
            }
            else
            {
                var dir = NodePath.Dirname(expandedPrefix);
                var file = NodePath.Basename(expandedPrefix);
                searchDir = rawPrefix.StartsWith('~') || expandedPrefix.StartsWith('/')
                    ? dir
                    : NodePath.Join(_basePath, dir);
                searchPrefix = file;
            }

            var suggestions = new List<AutocompleteItem>();

            foreach (var fullPath in Directory.EnumerateFileSystemEntries(searchDir))
            {
                var name = Path.GetFileName(fullPath);
                if (!name.ToLowerInvariant().StartsWith(searchPrefix.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    continue;
                }

                // Directory.Exists follows symlinks, which matches the TS "isDirectory(), else
                // follow a symlink with statSync" pair.
                var isDirectory = Directory.Exists(fullPath);

                string relativePath;
                var displayPrefix = rawPrefix;

                if (displayPrefix.EndsWith('/'))
                {
                    relativePath = displayPrefix + name;
                }
                else if (displayPrefix.Contains('/') || displayPrefix.Contains('\\'))
                {
                    if (displayPrefix.StartsWith("~/", StringComparison.Ordinal))
                    {
                        var homeRelativeDir = JsString.Slice(displayPrefix, 2);
                        var dir = NodePath.Dirname(homeRelativeDir);
                        relativePath = $"~/${(dir == "." ? name : NodePath.Join(dir, name))}";
                    }
                    else if (displayPrefix.StartsWith('/'))
                    {
                        var dir = NodePath.Dirname(displayPrefix);
                        relativePath = dir == "/" ? $"/{name}" : $"{dir}/{name}";
                    }
                    else
                    {
                        relativePath = NodePath.Join(NodePath.Dirname(displayPrefix), name);

                        // path.join normalizes away the ./ prefix; preserve it.
                        if (displayPrefix.StartsWith("./", StringComparison.Ordinal)
                            && !relativePath.StartsWith("./", StringComparison.Ordinal))
                        {
                            relativePath = $"./{relativePath}";
                        }
                    }
                }
                else
                {
                    relativePath = displayPrefix.StartsWith('~') ? $"~/{name}" : name;
                }

                relativePath = ToDisplayPath(relativePath);
                var pathValue = isDirectory ? $"{relativePath}/" : relativePath;
                var value = BuildCompletionValue(pathValue, isAtPrefix, isQuotedPrefix);

                suggestions.Add(new AutocompleteItem
                {
                    Value = value,
                    Label = name + (isDirectory ? "/" : ""),
                });
            }

            // Sort directories first, then alphabetically.
            return StableSort(suggestions, (a, b) =>
            {
                var aIsDir = a.Label.EndsWith('/');
                var bIsDir = b.Label.EndsWith('/');
                if (aIsDir && !bIsDir)
                {
                    return -1;
                }

                if (!aIsDir && bIsDir)
                {
                    return 1;
                }

                return LocaleCompare(a.Label, b.Label);
            });
        }
        catch (Exception)
        {
            // Directory doesn't exist or isn't accessible.
            return [];
        }
    }

    /// <summary>Score an entry against the query (higher is better); directories get a bonus.</summary>
    internal static int ScoreEntry(string filePath, string query, bool isDirectory)
    {
        var fileName = NodePath.Basename(filePath);
        var lowerFileName = fileName.ToLowerInvariant();
        var lowerQuery = query.ToLowerInvariant();

        int score;
        if (lowerFileName == lowerQuery)
        {
            score = 100;
        }
        else if (lowerFileName.StartsWith(lowerQuery, StringComparison.Ordinal))
        {
            score = 80;
        }
        else if (lowerFileName.Contains(lowerQuery, StringComparison.Ordinal))
        {
            score = 50;
        }
        else if (filePath.ToLowerInvariant().Contains(lowerQuery, StringComparison.Ordinal))
        {
            score = 30;
        }
        else
        {
            score = 0;
        }

        if (isDirectory && score > 0)
        {
            score += 10;
        }

        return score;
    }

    private async Task<IReadOnlyList<FdEntry>> GetBaseDirSuggestionsAsync(
        string baseDir,
        string query,
        CancellationToken signal)
    {
        if (_fdPath is null || signal.IsCancellationRequested)
        {
            return [];
        }

        return await WalkDirectoryWithFdAsync(baseDir, _fdPath, query, 100, signal, 1).ConfigureAwait(false);
    }

    /// <summary>Fuzzy file search using <c>fd</c> (fast, respects .gitignore).</summary>
    private async Task<IReadOnlyList<AutocompleteItem>> GetFuzzyFileSuggestionsAsync(
        string query,
        bool isQuotedPrefix,
        CancellationToken signal)
    {
        if (_fdPath is null || signal.IsCancellationRequested)
        {
            return [];
        }

        try
        {
            var scopedQuery = ResolveScopedFuzzyQuery(query);
            var fdBaseDir = scopedQuery?.BaseDir ?? _basePath;
            var fdQuery = scopedQuery?.Query ?? query;
            var baseDirEntries = await GetBaseDirSuggestionsAsync(fdBaseDir, fdQuery, signal).ConfigureAwait(false);
            var recursiveEntries = await WalkDirectoryWithFdAsync(fdBaseDir, _fdPath, fdQuery, 100, signal).ConfigureAwait(false);

            var seenPaths = new HashSet<string>(baseDirEntries.Select(entry => entry.Path), StringComparer.Ordinal);
            var entries = new List<FdEntry>(baseDirEntries);
            foreach (var entry in recursiveEntries)
            {
                if (seenPaths.Add(entry.Path))
                {
                    entries.Add(entry);
                }
            }

            if (signal.IsCancellationRequested)
            {
                return [];
            }

            var scoredEntries = entries
                .Select(entry => new ScoredEntry(
                    entry.Path,
                    entry.IsDirectory,
                    fdQuery.Length > 0 ? ScoreEntry(entry.Path, fdQuery, entry.IsDirectory) : 1))
                .Where(entry => entry.Score > 0)
                .ToList();

            var sorted = StableSort(scoredEntries, (a, b) =>
            {
                var scoreDiff = b.Score - a.Score;
                if (scoreDiff != 0)
                {
                    return scoreDiff;
                }

                var aDepth = ToDisplayPath(a.Path).Split('/').Count(segment => segment.Length > 0);
                var bDepth = ToDisplayPath(b.Path).Split('/').Count(segment => segment.Length > 0);
                var depthDiff = aDepth - bDepth;
                if (depthDiff != 0)
                {
                    return depthDiff;
                }

                var lengthDiff = a.Path.Length - b.Path.Length;
                return lengthDiff != 0 ? lengthDiff : LocaleCompare(a.Path, b.Path);
            });

            var topEntries = sorted.Take(20);
            var suggestions = new List<AutocompleteItem>();
            foreach (var entry in topEntries)
            {
                var pathWithoutSlash = entry.IsDirectory ? JsString.Slice(entry.Path, 0, -1) : entry.Path;
                var displayPath = scopedQuery is not null
                    ? ScopedPathForDisplay(scopedQuery.DisplayBase, pathWithoutSlash)
                    : pathWithoutSlash;
                var entryName = NodePath.Basename(pathWithoutSlash);
                var completionPath = entry.IsDirectory ? $"{displayPath}/" : displayPath;
                var value = BuildCompletionValue(completionPath, isAtPrefix: true, isQuotedPrefix: isQuotedPrefix);

                suggestions.Add(new AutocompleteItem
                {
                    Value = value,
                    Label = entryName + (entry.IsDirectory ? "/" : ""),
                    Description = displayPath,
                });
            }

            return suggestions;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<FdEntry>> WalkDirectoryWithFdAsync(
        string baseDir,
        string fdPath,
        string query,
        int maxResults,
        CancellationToken signal,
        int? maxDepth = null)
    {
        var args = new List<string>
        {
            "--base-directory",
            baseDir,
            "--max-results",
            maxResults.ToString(CultureInfo.InvariantCulture),
            "--type",
            "f",
            "--type",
            "d",
            "--follow",
            "--hidden",
            "--exclude",
            ".git",
            "--exclude",
            ".git/*",
            "--exclude",
            ".git/**",
        };

        if (maxDepth is not null)
        {
            args.Add("--max-depth");
            args.Add(maxDepth.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (ToDisplayPath(query).Contains('/'))
        {
            args.Add("--full-path");
        }

        if (query.Length > 0)
        {
            args.Add(BuildFdPathQuery(query));
        }

        if (signal.IsCancellationRequested)
        {
            return [];
        }

        if (FdProcessOverride is not null)
        {
            var (overrideExitCode, overrideStdout) = FdProcessOverride(args);
            return signal.IsCancellationRequested || overrideExitCode != 0 || overrideStdout.Length == 0
                ? []
                : ParseFdOutput(overrideStdout);
        }

        try
        {
            var startInfo = new ProcessStartInfo(fdPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var child = Process.Start(startInfo);
            if (child is null)
            {
                return [];
            }

            using var registration = signal.Register(() =>
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // The process already exited.
                }
            });

            var stdoutTask = child.StandardOutput.ReadToEndAsync();
            var stderrTask = child.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

            var stdout = stdoutTask.Result;
            return signal.IsCancellationRequested || child.ExitCode != 0 || stdout.Length == 0
                ? []
                : ParseFdOutput(stdout);
        }
        catch (Exception)
        {
            // Spawn failure (fd missing, not executable, …): behave like the TS "error" handler.
            return [];
        }
    }

    /// <summary>Parses <c>fd</c>'s stdout into path/directory entries, skipping <c>.git</c>.</summary>
    internal static IReadOnlyList<FdEntry> ParseFdOutput(string stdout)
    {
        var results = new List<FdEntry>();
        foreach (var rawLine in JsString.Trim(stdout).Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                continue;
            }

            var displayLine = ToDisplayPath(rawLine);
            var hasTrailingSeparator = displayLine.EndsWith('/');
            var normalizedPath = hasTrailingSeparator ? JsString.Slice(displayLine, 0, -1) : displayLine;
            if (normalizedPath == ".git"
                || normalizedPath.StartsWith(".git/", StringComparison.Ordinal)
                || normalizedPath.Contains("/.git/", StringComparison.Ordinal))
            {
                continue;
            }

            results.Add(new FdEntry(displayLine, hasTrailingSeparator));
        }

        return results;
    }

    private static string LineAt(string[] lines, int index) =>
        index >= 0 && index < lines.Length ? lines[index] ?? "" : "";

    /// <summary>
    /// Assigns <paramref name="value"/> at <paramref name="index"/>, growing the array when the
    /// index is past the end — JS arrays do the same, and holes read back as <c>null</c>.
    /// </summary>
    private static string[] SetLine(string[] lines, int index, string value)
    {
        if (index >= 0 && index < lines.Length)
        {
            var copy = (string[])lines.Clone();
            copy[index] = value;
            return copy;
        }

        var size = Math.Max(lines.Length, index + 1);
        var grown = new string[size];
        Array.Copy(lines, grown, lines.Length);
        grown[index] = value;
        return grown;
    }

    /// <summary>
    /// <c>String.prototype.localeCompare</c>. Both Node and .NET use ICU for the current culture,
    /// so this matches for the labels the provider produces (verified by the corpus <c>collate</c>
    /// section).
    /// </summary>
    private static int LocaleCompare(string a, string b) =>
        CultureInfo.CurrentCulture.CompareInfo.Compare(a, b, CompareOptions.None);

    /// <summary>Exposed for the differential corpus (the <c>collate</c> section).</summary>
    internal static int Collate(string a, string b) => LocaleCompare(a, b);

    /// <summary>
    /// A stable sort: JS <c>Array.prototype.sort</c> is stable, while <see cref="List{T}.Sort()"/>
    /// is not, and the comparators below can tie.
    /// </summary>
    private static List<T> StableSort<T>(List<T> items, Comparison<T> comparison) =>
        [.. items.OrderBy(item => item, Comparer<T>.Create(comparison))];

    private sealed record CommandEntry(string Name, string Label, string? Description);

    private sealed record ScopedQuery(string BaseDir, string Query, string DisplayBase);

    private sealed record ScoredEntry(string Path, bool IsDirectory, int Score);
}

/// <summary>An entry produced by walking the tree with <c>fd</c>.</summary>
internal sealed record FdEntry(string Path, bool IsDirectory);
