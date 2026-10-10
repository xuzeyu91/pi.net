using System.Text.RegularExpressions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Pi.CodingAgent.Core.Tools;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace Pi.CodingAgent.Extensions.ToolSearch;

// ============================================================================
// tool-search extension — port of extensions/tool-search/{tool.ts,index.ts} (4d-6)
// ============================================================================

/// <summary>Port of the TS <c>ToolSearchDocument</c>.</summary>
public sealed record ToolSearchDocument(string Name, string Text);

/// <summary>Port of the TS <c>ToolSearchMatch</c>.</summary>
public sealed record ToolSearchMatch(string Name, double Score);

/// <summary>
/// Ranks tools for a query. Port of the TS <c>ToolRanker</c> (BM25 today; a hybrid ranker with
/// embeddings can replace it).
/// </summary>
public interface IToolRanker
{
    IReadOnlyList<ToolSearchMatch> Rank(string query, IReadOnlyList<ToolSearchDocument> documents, int limit);
}

/// <summary>
/// The slice of TS <c>Pick&lt;ExtensionAPI, "getAllTools" | "getActiveTools" | "setActiveTools"&gt;</c>
/// that <c>tool_search</c> reads. Difference C112 (the same minimal-interface device as C84): C# has
/// no structural typing, so callers pass this interface (an <see cref="IExtensionApi"/> fits through
/// <see cref="ExtensionApiToolSearchTools"/>).
/// </summary>
public interface IToolSearchTools
{
    IReadOnlyList<string> GetActiveTools();

    IReadOnlyList<ToolInfo> GetAllTools();

    void SetActiveTools(IReadOnlyList<string> toolNames);
}

/// <summary>Adapts an <see cref="IExtensionApi"/> to <see cref="IToolSearchTools"/>.</summary>
public sealed class ExtensionApiToolSearchTools(IExtensionApi api) : IToolSearchTools
{
    public IReadOnlyList<string> GetActiveTools() => api.GetActiveTools();

    public IReadOnlyList<ToolInfo> GetAllTools() => api.GetAllTools();

    public void SetActiveTools(IReadOnlyList<string> toolNames) => api.SetActiveTools(toolNames);
}

/// <summary>Port of the TS <c>ToolSearchToolOptions</c>.</summary>
public sealed record ToolSearchToolOptions
{
    /// <summary>
    /// The session's tools. <c>tool_search</c> searches the tools that are not declared to the model
    /// and activates the matches. Without it, the tool finds nothing.
    /// </summary>
    public IToolSearchTools? Tools { get; init; }
}

/// <summary>Port of the TS <c>ToolSearchResultTool</c>.</summary>
public sealed record ToolSearchResultTool(string Name, string Description);

/// <summary>Port of the TS <c>ToolSearchToolDetails</c>.</summary>
public sealed record ToolSearchToolDetails
{
    /// <summary>Tools loaded by this call.</summary>
    public required IReadOnlyList<string> Loaded { get; init; }
}

/// <summary>The <c>tool_search</c> tool. Port of <c>extensions/tool-search/tool.ts</c>.</summary>
public static partial class ToolSearch
{
    public const string ToolSearchToolName = "tool_search";

    public const int DefaultToolSearchLimit = 8;

    /// <summary>The TS <c>STOP_WORDS</c>.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on",
        "or", "that", "the", "this", "to", "with",
    };

    /// <summary>The TS <c>createToolSearchDocument</c> description of <c>tool_search</c>.</summary>
    public const string ToolSearchDescription =
        "# Tool discovery\n\nSearches over deferred tool metadata with BM25 and exposes matching tools " +
        "for the next model call.\n\nSome of the tools, such as tools of MCP servers, may not have been " +
        "provided to you upfront, and you should use this tool (`tool_search`) to search for the " +
        "required tools. For MCP tool discovery, always use `tool_search`.";

    /// <summary>The TS <c>toolSearchSchema</c> (a single shared instance, so identity guards work).</summary>
    public static readonly ToolSchema ToolSearchSchema = new(new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>
        {
            ["query"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Search query for deferred tools.",
            },
            ["limit"] = new Dictionary<string, object?>
            {
                ["type"] = "number",
                ["description"] = $"Maximum number of tools to return. Defaults to {DefaultToolSearchLimit}.",
            },
        },
        ["required"] = new[] { "query" },
    });

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex CamelLowerUpper();

    [GeneratedRegex("([A-Z]+)([A-Z][a-z])")]
    private static partial Regex CamelUpperUpperLower();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("(ches|shes|sses|xes|zes)$")]
    private static partial Regex PluralSuffix();

    [GeneratedRegex("\\r?\\n")]
    private static partial Regex LineBreak();

    /// <summary>Naive singular form, so <c>issues</c> matches <c>issue</c>. Port of the TS <c>stem</c>.</summary>
    public static string Stem(string term)
    {
        if (term.Length > 4 && term.EndsWith("ies", StringComparison.Ordinal))
        {
            return string.Concat(term.AsSpan(0, term.Length - 3), "y");
        }

        if (term.Length > 4 && PluralSuffix().IsMatch(term))
        {
            return term[..^2];
        }

        if (term.Length > 3 && term.EndsWith("s", StringComparison.Ordinal) && !term.EndsWith("ss", StringComparison.Ordinal))
        {
            return term[..^1];
        }

        return term;
    }

    /// <summary>
    /// Lowercase terms, split at camelCase boundaries and non-alphanumerics, without stop words.
    /// Port of the TS <c>tokenize</c>.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var spaced = CamelLowerUpper().Replace(text, "$1 $2");
        spaced = CamelUpperUpperLower().Replace(spaced, "$1 $2");
        var lower = spaced.ToLowerInvariant();
        return NonAlphanumeric()
            .Split(lower)
            .Where(term => term.Length > 0 && !StopWords.Contains(term))
            .Select(Stem)
            .ToList();
    }

    /// <summary>The TS <c>isObject</c>: a JSON object, not null, not an array.</summary>
    private static bool TryGetObject(object? value, out IReadOnlyDictionary<string, object?> map)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> readOnly:
                map = readOnly;
                return true;
            case IDictionary<string, object?> mutable:
                map = new Dictionary<string, object?>(mutable);
                return true;
            default:
                map = null!;
                return false;
        }
    }

    /// <summary>The TS <c>Array.isArray</c> over a JSON array, as an object sequence.</summary>
    private static IEnumerable<object?> AsArray(object? value) => value switch
    {
        string => [],
        IReadOnlyDictionary<string, object?> => [],
        System.Collections.IEnumerable items => items.Cast<object?>(),
        _ => [],
    };

    /// <summary>Schema descriptions and property names, recursively. Port of the TS <c>schemaText</c>.</summary>
    private static void SchemaText(object? schema, List<string> parts)
    {
        if (!TryGetObject(schema, out var map))
        {
            return;
        }

        if (map.TryGetValue("description", out var description) && description is string text)
        {
            parts.Add(text);
        }

        if (map.TryGetValue("properties", out var properties) && TryGetObject(properties, out var props))
        {
            foreach (var (name, property) in props)
            {
                parts.Add(name);
                SchemaText(property, parts);
            }
        }

        map.TryGetValue("items", out var items);
        SchemaText(items, parts);

        foreach (var key in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (map.TryGetValue(key, out var variants))
            {
                foreach (var variant in AsArray(variants))
                {
                    SchemaText(variant, parts);
                }
            }
        }
    }

    /// <summary>
    /// Search text of a tool: the name, the name with <c>_</c> as spaces, the description, schema
    /// descriptions and property names, and the namespace with its description and instructions.
    /// Port of the TS <c>createToolSearchDocument</c>.
    /// </summary>
    public static ToolSearchDocument CreateToolSearchDocument(ToolInfo tool, ToolNamespace? ns = null)
    {
        var parts = new List<string> { tool.Name, tool.Name.Replace("_", " "), tool.Description };
        SchemaText(tool.Parameters.JsonSchema, parts);
        if (ns is not null)
        {
            parts.Add(ns.Name);
            parts.Add(ns.Description ?? "");
            parts.Add(ns.Instructions ?? "");
        }

        return new ToolSearchDocument(tool.Name, string.Join(" ", parts.Where(part => part.Trim().Length > 0)));
    }

    /// <summary>Whether the tool is this <c>tool_search</c>, not another extension's tool of the same name.</summary>
    public static bool IsToolSearchTool(ToolInfo tool) =>
        tool.Name == ToolSearchToolName && ReferenceEquals(tool.Parameters, ToolSearchSchema);

    /// <summary>Whether <c>tool_search</c> can load a tool with this exposure.</summary>
    public static bool IsSearchable(string exposure) =>
        exposure is ToolExposure.Codemode or ToolExposure.Deferred;

    /// <summary>The TS <c>tool_search</c> prompt snippet.</summary>
    public const string PromptSnippet = "Search for tools that are not loaded yet and load the matches";

    /// <summary>
    /// Rank the searchable tools that are not active yet and activate the matches, so the next model
    /// call declares them. Port of the TS <c>searchAndLoad</c>.
    /// </summary>
    public static IReadOnlyList<ToolSearchResultTool> SearchAndLoad(IToolSearchTools tools, string query, int limit)
    {
        var active = tools.GetActiveTools();
        var candidates = tools.GetAllTools()
            .Where(tool => IsSearchable(tool.Exposure) && !active.Contains(tool.Name))
            .ToList();
        var documents = candidates.Select(tool => CreateToolSearchDocument(tool, tool.Namespace)).ToList();
        var matches = new Bm25Ranker().Rank(query, documents, limit);
        if (matches.Count > 0)
        {
            tools.SetActiveTools([.. active, .. matches.Select(match => match.Name)]);
        }

        return matches
            .Select(match => new ToolSearchResultTool(
                match.Name,
                candidates.FirstOrDefault(tool => tool.Name == match.Name)?.Description ?? ""))
            .ToList();
    }

    /// <summary>The TS <c>tool.description.trim().split(/\r?\n/)[0]</c>.</summary>
    private static string FirstLine(string description)
    {
        var trimmed = description.Trim();
        var match = LineBreak().Match(trimmed);
        return match.Success ? trimmed[..match.Index] : trimmed;
    }

    /// <summary>The TS <c>createToolSearchToolDefinition(options)</c>.</summary>
    public static ToolDefinition CreateToolSearchToolDefinition(ToolSearchToolOptions? options = null)
    {
        options ??= new ToolSearchToolOptions();
        return new ToolDefinition
        {
            Name = ToolSearchToolName,
            Label = ToolSearchToolName,
            Description = ToolSearchDescription,
            PromptSnippet = PromptSnippet,
            Parameters = ToolSearchSchema,
            // Searching is not something scripts need; it changes what the model sees.
            Exposure = ToolExposure.ModelOnly,
            Execute = (_, args, _, _, _) =>
            {
                var query = ToolArgs.GetString(args, "query") ?? "";
                if (query.Trim().Length == 0)
                {
                    throw new InvalidOperationException("query must not be empty");
                }

                var limitNumber = GetNumber(args, "limit");
                var max = limitNumber ?? DefaultToolSearchLimit;
                var isInteger = !double.IsNaN(max) && !double.IsInfinity(max) && Math.Floor(max) == max;
                if (!isInteger || max <= 0)
                {
                    throw new InvalidOperationException("limit must be a positive integer");
                }

                var tools = options.Tools is not null ? SearchAndLoad(options.Tools, query, (int)max) : [];
                var text = tools.Count == 0
                    ? "No matching tools found."
                    : $"Loaded {tools.Count} tool{(tools.Count == 1 ? "" : "s")}. They are available from " +
                      $"your next call:\n{string.Join("\n", tools.Select(tool => $"- {tool.Name}: {FirstLine(tool.Description)}"))}";
                return Task.FromResult(new AgentToolResult(
                    [new TextContent(text)],
                    Details: new ToolSearchToolDetails { Loaded = tools.Select(tool => tool.Name).ToList() }));
            },
        };
    }

    /// <summary>The TS <c>createToolSearchExtension()</c> factory.</summary>
    public static ExtensionFactory CreateToolSearchExtension() => pi =>
    {
        pi.RegisterTool(CreateToolSearchToolDefinition(new ToolSearchToolOptions
        {
            Tools = new ExtensionApiToolSearchTools(pi),
        }) with { DefaultActive = false });
        return Task.CompletedTask;
    };

    /// <summary>The TS <c>Number.isInteger</c>-style numeric read of an optional argument.</summary>
    private static double? GetNumber(IReadOnlyDictionary<string, object?> args, string key)
    {
        var node = ToolArgs.GetNode(args, key);
        if (node is not System.Text.Json.Nodes.JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<long>(out var l) ? l : null;
    }

    /// <summary>Okapi BM25 with the usual parameters. Ties keep document order. Port of the TS <c>Bm25Ranker</c>.</summary>
    public sealed class Bm25Ranker : IToolRanker
    {
        private readonly double _k1;
        private readonly double _b;

        public Bm25Ranker(double? k1 = null, double? b = null)
        {
            _k1 = k1 ?? 1.2;
            _b = b ?? 0.75;
        }

        public IReadOnlyList<ToolSearchMatch> Rank(string query, IReadOnlyList<ToolSearchDocument> documents, int limit)
        {
            var queryTerms = Tokenize(query).Distinct().ToList();
            if (queryTerms.Count == 0 || documents.Count == 0 || limit <= 0)
            {
                return [];
            }

            var termCounts = documents.Select(document =>
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var term in Tokenize(document.Text))
                {
                    counts[term] = counts.GetValueOrDefault(term) + 1;
                }

                return counts;
            }).ToList();

            var lengths = termCounts.Select(counts => counts.Values.Sum()).ToList();
            var averageLength = (double)lengths.Sum() / documents.Count;
            if (averageLength == 0)
            {
                averageLength = 1;
            }

            var idf = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var term in queryTerms)
            {
                var frequency = termCounts.Count(counts => counts.ContainsKey(term));
                idf[term] = Math.Log(1 + (documents.Count - frequency + 0.5) / (frequency + 0.5));
            }

            var matches = new List<ToolSearchMatch>();
            for (var index = 0; index < documents.Count; index++)
            {
                double score = 0;
                foreach (var term in queryTerms)
                {
                    if (!termCounts[index].TryGetValue(term, out var count) || count == 0)
                    {
                        continue;
                    }

                    var norm = _k1 * (1 - _b + (_b * lengths[index]) / averageLength);
                    score += idf[term] * ((count * (_k1 + 1)) / (count + norm));
                }

                if (score > 0)
                {
                    matches.Add(new ToolSearchMatch(documents[index].Name, score));
                }
            }

            // JS Array.prototype.sort is stable (ES2019); List<T>.Sort is not, so keep OrderByDescending.
            return matches.OrderByDescending(match => match.Score).Take(limit).ToList();
        }
    }
}
