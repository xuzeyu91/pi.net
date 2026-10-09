using System.Text.Json.Nodes;
using Pi.Agent.Types;
using Pi.CodingAgent.Core.Extensions;

namespace Pi.CodingAgent.Core.Tools;

/// <summary>
/// Typed accessors for raw tool call arguments. The agent runtime hands tools the parsed JSON
/// arguments as a <see cref="JsonObject"/>; the port's <see cref="ToolDefinition.Execute"/> takes
/// them as <see cref="IReadOnlyDictionary{TKey, TValue}"/>, and these helpers read the individual
/// members the way the TS destructuring does (missing and JSON-null both read as absent).
/// </summary>
public static class ToolArgs
{
    /// <summary>The TS <c>typeof value === "string" ? value : undefined</c>.</summary>
    public static string? GetString(IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = GetValue(args, key);
        return value switch
        {
            null => null,
            string text => text,
            JsonValue json when json.TryGetValue<string>(out var text) => text,
            _ => null,
        };
    }

    /// <summary>
    /// The TS numeric read. JSON numbers arrive as <see cref="JsonValue"/>; a fractional value is
    /// truncated toward zero, matching how JS array slicing coerces it.
    /// </summary>
    public static int? GetInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = GetValue(args, key);
        return value switch
        {
            null => null,
            int i => i,
            double d => (int)d,
            JsonValue json when json.TryGetValue<int>(out var i) => i,
            JsonValue json when json.TryGetValue<double>(out var d) => (int)d,
            _ => null,
        };
    }

    /// <summary>The TS <c>typeof value === "boolean" ? value : undefined</c>.</summary>
    public static bool? GetBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        var value = GetValue(args, key);
        return value switch
        {
            null => null,
            bool b => b,
            JsonValue json when json.TryGetValue<bool>(out var b) => b,
            _ => null,
        };
    }

    private static object? GetValue(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value : null;

    /// <summary>The raw JSON node for a member, for callers that need the structured value.</summary>
    public static JsonNode? GetNode(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value as JsonNode : null;
}

/// <summary>
/// Wrap a <see cref="ToolDefinition"/> into an <see cref="AgentTool"/> for the core runtime. Port of
/// <c>core/tools/tool-definition-wrapper.ts</c>.
/// </summary>
public static class ToolDefinitionWrapper
{
    /// <summary>
    /// The TS <c>wrapToolDefinition</c>. The agent runtime has already applied
    /// <see cref="AgentTool.PrepareArguments"/> by the time <see cref="AgentTool.Execute"/> runs, so
    /// the bridge only coerces the JSON arguments into the dictionary shape and supplies the
    /// per-call context from the factory (the TS <c>ctx ?? ctxFactory?.(...)</c>; the runtime's
    /// <c>AgentTool</c> has no context parameter, so the factory path is the one taken).
    /// </summary>
    public static AgentTool WrapToolDefinition(ToolDefinition definition, ToolContextFactory? ctxFactory = null)
    {
        return new AgentTool(
            definition.Name,
            definition.Description,
            definition.Parameters,
            definition.Label,
            (toolCallId, args, signal, onUpdate) => definition.Execute(
                toolCallId,
                CoerceArgs(args),
                signal,
                onUpdate,
                ctxFactory?.Invoke(toolCallId, signal)),
            definition.ExecutionMode,
            definition.PrepareArguments)
        {
            OutputSchema = definition.OutputSchema,
            ConstrainedSampling = definition.ConstrainedSampling,
            PromptSnippet = definition.PromptSnippet,
            PromptGuidelines = definition.PromptGuidelines,
        };
    }

    /// <summary>The TS <c>wrapToolDefinitions</c>.</summary>
    public static List<AgentTool> WrapToolDefinitions(
        IReadOnlyList<ToolDefinition> definitions,
        ToolContextFactory? ctxFactory = null) =>
        definitions.Select(definition => WrapToolDefinition(definition, ctxFactory)).ToList();

    /// <summary>
    /// Synthesize a minimal <see cref="ToolDefinition"/> from an <see cref="AgentTool"/>. The TS
    /// keeps AgentSession's internal registry definition-first even when a caller provides plain
    /// AgentTool overrides that do not include prompt metadata or renderers.
    /// </summary>
    public static ToolDefinition CreateToolDefinitionFromAgentTool(AgentTool tool) => new()
    {
        Name = tool.Name,
        Label = tool.Label,
        Description = tool.Description,
        Parameters = tool.Parameters,
        OutputSchema = tool.OutputSchema,
        ConstrainedSampling = tool.ConstrainedSampling,
        PrepareArguments = tool.PrepareArguments,
        ExecutionMode = tool.ExecutionMode,
        Execute = (toolCallId, args, signal, onUpdate, _) => tool.Execute(toolCallId, args, signal, onUpdate),
    };

    private static IReadOnlyDictionary<string, object?> CoerceArgs(object? args) => args switch
    {
        null => new Dictionary<string, object?>(),
        IReadOnlyDictionary<string, object?> dictionary => dictionary,
        JsonObject json => json.ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
        JsonValue json => new Dictionary<string, object?> { ["value"] = json },
        // The agent runtime hands tools the parsed JSON arguments, so the common shape is a
        // JsonObject. Callers that build arguments programmatically (tests, in-process tools) pass
        // plain dictionaries and lists; those are normalized to JSON nodes so the typed accessors
        // in ToolArgs read them the same way.
        _ => NormalizeToJsonNode(args) is JsonObject normalized
            ? normalized.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
            : new Dictionary<string, object?>(),
    };

    private static JsonNode? NormalizeToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int or long or double or float => JsonValue.Create(Convert.ToDouble(value)),
        IDictionary<string, object?> dictionary => new JsonObject(
            dictionary.ToDictionary(pair => pair.Key, pair => NormalizeToJsonNode(pair.Value))),
        System.Collections.IEnumerable enumerable => new JsonArray(
            enumerable.Cast<object?>().Select(NormalizeToJsonNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}
