// ============================================================================
// Prompt templates — port of core/prompt-templates.ts (4e-1)
// ============================================================================
//
// A prompt template is a markdown file with an optional YAML front matter. `/name args` expands it:
// the argument string is split bash-style, then `$1` / `$@` / `${1:-default}` / `${@:2:3}` placeholders
// in the body are substituted. Loading happens from the agent dir, the project dir and explicit paths.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pi.CodingAgent.Utils;
using Pi.Tui;

namespace Pi.CodingAgent.Core;

/// <summary>One prompt template loaded from a markdown file. Port of the TS <c>PromptTemplate</c>.</summary>
public sealed record PromptTemplate
{
    /// <summary>The file's base name with the <c>.md</c> suffix removed.</summary>
    public required string Name { get; init; }

    /// <summary>Front matter <c>description</c>, or the truncated first non-empty body line.</summary>
    public required string Description { get; init; }

    /// <summary>Front matter <c>argument-hint</c>, when present.</summary>
    public string? ArgumentHint { get; init; }

    /// <summary>The body, with the front matter removed and trimmed.</summary>
    public required string Content { get; init; }

    /// <summary>Provenance of the template.</summary>
    public required SourceInfo SourceInfo { get; init; }

    /// <summary>Absolute path to the template file.</summary>
    public required string FilePath { get; init; }
}

/// <summary>Options for <see cref="PromptTemplates.Load"/>. Port of the TS <c>LoadPromptTemplatesOptions</c>.</summary>
public sealed record LoadPromptTemplatesOptions
{
    /// <summary>Working directory for project-local templates.</summary>
    public required string Cwd { get; init; }

    /// <summary>Agent config directory for global templates.</summary>
    public required string AgentDir { get; init; }

    /// <summary>Explicit prompt template paths (files or directories).</summary>
    public required IReadOnlyList<string> PromptPaths { get; init; }

    /// <summary>Include the default prompt directories.</summary>
    public required bool IncludeDefaults { get; init; }
}

/// <summary>Result of <see cref="PromptTemplates.Load"/>. Port of the TS <c>LoadPromptTemplatesResult</c>.</summary>
public sealed record LoadPromptTemplatesResult(
    IReadOnlyList<PromptTemplate> Templates,
    IReadOnlyList<ResourceDiagnostic> Diagnostics);

/// <summary>Port of <c>core/prompt-templates.ts</c>.</summary>
public static partial class PromptTemplates
{
    private const int DescriptionMaxLength = 60;

    /// <summary>
    /// The TS <c>substituteArgs</c> pattern, with <c>\d</c> widened to <c>[0-9]</c> (JS semantics; .NET's
    /// <c>\d</c> would also match non-ASCII digits).
    /// </summary>
    [GeneratedRegex(@"\$\{([0-9]+|ARGUMENTS|@):-([^}]*)\}|\$\{@:([0-9]+)(?::([0-9]+))?\}|\$(ARGUMENTS|@|[0-9]+)")]
    private static partial Regex SubstitutionPattern();

    /// <summary>The TS <c>expandPromptTemplate</c> pattern <c>^\/([^\s]+)(?:\s+([\s\S]*))?$</c>.</summary>
    /// <remarks>
    /// Group 1 is the command name and group 2 the argument string. Both brackets come from
    /// <see cref="JsRegex"/>: JS <c>\s</c> is not .NET <c>\s</c> (JS adds U+FEFF, drops U+0085).
    /// </remarks>
    [GeneratedRegex("^\\/(" + JsRegex.NotWhitespaceClass + "+)(?:" + JsRegex.WhitespaceClass + "+([\\s\\S]*))?$")]
    private static partial Regex ExpansionPattern();

    /// <summary>
    /// Split an argument string on whitespace, honouring single and double quotes (bash-style). Quotes
    /// are removed; they do not group across a mismatched quote character.
    /// </summary>
    public static IReadOnlyList<string> ParseCommandArgs(string argsString)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? inQuote = null;

        foreach (var ch in argsString)
        {
            if (inQuote is { } quote)
            {
                if (ch == quote)
                {
                    inQuote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                inQuote = ch;
            }
            else if (JsString.IsWhitespace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    /// <summary>
    /// Substitute argument placeholders in template content. Supports <c>$1</c>…, <c>$@</c>,
    /// <c>$ARGUMENTS</c>, <c>${N:-default}</c>, <c>${@:-default}</c>, <c>${@:N}</c> and <c>${@:N:L}</c>.
    /// Replacement runs once over the template, so placeholders inside argument or default values are
    /// left alone.
    /// </summary>
    public static string SubstituteArgs(string content, IReadOnlyList<string> args)
    {
        var allArgs = string.Join(' ', args);
        return SubstitutionPattern().Replace(content, match => Substitute(match, allArgs, args));
    }

    /// <summary>Expand a prompt template when <paramref name="text"/> names one; otherwise return it unchanged.</summary>
    public static string ExpandPromptTemplate(string text, IReadOnlyList<PromptTemplate> templates)
    {
        if (!text.StartsWith('/'))
        {
            return text;
        }

        var match = ExpansionPattern().Match(text);
        if (!match.Success)
        {
            return text;
        }

        var templateName = match.Groups[1].Value;
        var argsString = match.Groups[2].Success ? match.Groups[2].Value : "";
        var template = templates.FirstOrDefault(candidate => candidate.Name == templateName);
        if (template is not null)
        {
            return SubstituteArgs(template.Content, ParseCommandArgs(argsString));
        }

        return text;
    }

    /// <summary>
    /// Load prompt templates from the agent dir (<c>agentDir/prompts</c>), the project dir
    /// (<c>cwd/.pi/prompts</c>, when <see cref="LoadPromptTemplatesOptions.IncludeDefaults"/>) and every
    /// explicit path.
    /// </summary>
    public static LoadPromptTemplatesResult Load(LoadPromptTemplatesOptions options)
    {
        var resolvedCwd = Paths.ResolvePath(options.Cwd);
        var resolvedAgentDir = Paths.ResolvePath(options.AgentDir);

        var templates = new List<PromptTemplate>();
        var diagnostics = new List<ResourceDiagnostic>();
        void AddResult(LoadPromptTemplatesResult result)
        {
            templates.AddRange(result.Templates);
            diagnostics.AddRange(result.Diagnostics);
        }

        var globalPromptsDir = NodePath.Join(resolvedAgentDir, "prompts");
        var projectPromptsDir = NodePath.Resolve(resolvedCwd, Config.ConfigDirName, "prompts");

        SourceInfo GetSourceInfo(string resolvedPath)
        {
            if (IsUnderPath(resolvedPath, globalPromptsDir))
            {
                return SourceInfos.CreateSynthetic(
                    resolvedPath, "local", SourceScope.User, baseDir: globalPromptsDir);
            }

            if (IsUnderPath(resolvedPath, projectPromptsDir))
            {
                return SourceInfos.CreateSynthetic(
                    resolvedPath, "local", SourceScope.Project, baseDir: projectPromptsDir);
            }

            return SourceInfos.CreateSynthetic(
                resolvedPath,
                "local",
                baseDir: Directory.Exists(resolvedPath) ? resolvedPath : NodePath.Dirname(resolvedPath));
        }

        if (options.IncludeDefaults)
        {
            AddResult(LoadTemplatesFromDir(globalPromptsDir, GetSourceInfo));
            AddResult(LoadTemplatesFromDir(projectPromptsDir, GetSourceInfo));
        }

        foreach (var rawPath in options.PromptPaths)
        {
            var resolvedPath = Paths.ResolvePath(rawPath, resolvedCwd, new PathInputOptions(Trim: true));
            if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
            {
                continue;
            }

            try
            {
                if (Directory.Exists(resolvedPath))
                {
                    AddResult(LoadTemplatesFromDir(resolvedPath, GetSourceInfo));
                }
                else if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".md", StringComparison.Ordinal))
                {
                    var result = LoadTemplateFromFile(resolvedPath, GetSourceInfo(resolvedPath));
                    if (result.Template is not null)
                    {
                        templates.Add(result.Template);
                    }

                    diagnostics.AddRange(result.Diagnostics);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(ResourceDiagnostic.Warning(exception.Message, resolvedPath));
            }
        }

        return new LoadPromptTemplatesResult(templates, diagnostics);
    }

    /// <summary>The TS <c>isUnderPath</c>: a path is under a root when it is the root or a descendant.</summary>
    private static bool IsUnderPath(string target, string root)
    {
        var normalizedRoot = NodePath.Resolve(root);
        if (target == normalizedRoot)
        {
            return true;
        }

        var separator = NodePath.Separator;
        var prefix = normalizedRoot.EndsWith(separator) ? normalizedRoot : normalizedRoot + separator;
        return target.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Substitute(Match match, string allArgs, IReadOnlyList<string> args)
    {
        // Branch 1: ${N:-default} / ${@:-default} / ${ARGUMENTS:-default}
        if (match.Groups[1].Success)
        {
            var target = match.Groups[1].Value;
            var defaultValue = match.Groups[2].Value;
            var value = target is "@" or "ARGUMENTS"
                ? allArgs
                : ArgAt(args, int.Parse(target, CultureInfo.InvariantCulture) - 1);
            return !string.IsNullOrEmpty(value) ? value : defaultValue;
        }

        // Branch 2: ${@:N} / ${@:N:L}
        if (match.Groups[3].Success)
        {
            var start = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) - 1;
            if (start < 0)
            {
                start = 0;
            }

            if (match.Groups[4].Success)
            {
                var length = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                return string.Join(' ', args.Skip(start).Take(length));
            }

            return string.Join(' ', args.Skip(start));
        }

        // Branch 3: $ARGUMENTS / $@ / $N
        var simple = match.Groups[5].Value;
        if (simple is "ARGUMENTS" or "@")
        {
            return allArgs;
        }

        return ArgAt(args, int.Parse(simple, CultureInfo.InvariantCulture) - 1) ?? "";
    }

    /// <summary>JS <c>args[index]</c>, which is <c>undefined</c> outside the array.</summary>
    private static string? ArgAt(IReadOnlyList<string> args, int index) =>
        index >= 0 && index < args.Count ? args[index] : null;

    private static (PromptTemplate? Template, IReadOnlyList<ResourceDiagnostic> Diagnostics) LoadTemplateFromFile(
        string filePath,
        SourceInfo sourceInfo)
    {
        var diagnostics = new List<ResourceDiagnostic>();

        string rawContent;
        try
        {
            // `readFileSync(path, "utf-8")` decodes the bytes as UTF-8 and keeps a byte-order mark;
            // stripping it is `parseFrontmatter`'s job (`stripBom`), so the port keeps the mark too and
            // leaves that responsibility in the same layer. (`File.ReadAllText` would strip it here,
            // which is unobservable today because `stripBom` runs either way — but it would silently
            // change behaviour if the parser were ever reused without it.)
            rawContent = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetString(File.ReadAllBytes(filePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(ResourceDiagnostic.Warning(exception.Message, filePath));
            return (null, diagnostics);
        }

        ParsedFrontmatter parsed;
        try
        {
            parsed = Frontmatter.Parse(rawContent);
        }
        catch (Exception exception) when (exception is not (IOException or OutOfMemoryException))
        {
            diagnostics.Add(ResourceDiagnostic.Warning(exception.Message, filePath));
            return (null, diagnostics);
        }

        var name = NodePath.Basename(filePath);
        if (name.EndsWith(".md", StringComparison.Ordinal))
        {
            name = name[..^3];
        }

        // Front matter description wins; otherwise the first non-blank body line, truncated to 60 chars.
        var description = Frontmatter.GetString(parsed.Frontmatter, "description") ?? "";
        if (description.Length == 0)
        {
            var firstLine = parsed.Body
                .Split('\n')
                .FirstOrDefault(line => JsString.Trim(line).Length > 0);
            if (firstLine is not null)
            {
                description = JsString.Slice(firstLine, 0, DescriptionMaxLength);
                if (firstLine.Length > DescriptionMaxLength)
                {
                    description += "...";
                }
            }
        }

        // TS spreads `...(argumentHint && { argumentHint })`, so an empty front-matter value leaves the
        // member absent rather than present-and-empty.
        var argumentHint = Frontmatter.GetString(parsed.Frontmatter, "argument-hint") is { Length: > 0 } hint
            ? hint
            : null;

        return (new PromptTemplate
        {
            Name = name,
            Description = description,
            ArgumentHint = argumentHint,
            Content = parsed.Body,
            SourceInfo = sourceInfo,
            FilePath = filePath,
        }, diagnostics);
    }

    /// <summary>
    /// Scan a directory for <c>.md</c> files (non-recursive) and load them. Symlinks are followed for
    /// the file check; a broken symlink is skipped.
    /// </summary>
    private static LoadPromptTemplatesResult LoadTemplatesFromDir(
        string dir,
        Func<string, SourceInfo> getSourceInfo)
    {
        var templates = new List<PromptTemplate>();
        var diagnostics = new List<ResourceDiagnostic>();

        if (!Directory.Exists(dir))
        {
            return new LoadPromptTemplatesResult(templates, diagnostics);
        }

        try
        {
            foreach (var fullPath in Directory.EnumerateFileSystemEntries(dir))
            {
                var entryName = NodePath.Basename(fullPath);

                // TS `entry.isFile()`, following a symlink when the entry is one.
                var isFile = File.Exists(fullPath);
                if (!isFile && IsSymlink(fullPath))
                {
                    isFile = File.Exists(ResolveLink(fullPath));
                }

                if (isFile && entryName.EndsWith(".md", StringComparison.Ordinal))
                {
                    var result = LoadTemplateFromFile(fullPath, getSourceInfo(fullPath));
                    if (result.Template is not null)
                    {
                        templates.Add(result.Template);
                    }

                    diagnostics.AddRange(result.Diagnostics);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new LoadPromptTemplatesResult(templates, diagnostics);
        }

        return new LoadPromptTemplatesResult(templates, diagnostics);
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveLink(string path)
    {
        var info = new FileInfo(path);
        return info.LinkTarget is { } target
            ? NodePath.Resolve(NodePath.Dirname(path), target)
            : path;
    }
}
