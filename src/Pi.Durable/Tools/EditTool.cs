using Pi.Ai.Types;
using Pi.Chord.Context;
using Pi.Durable.Env;
using Pi.Durable.Harness;

namespace Pi.Durable.Tools;

/// <summary>
/// <c>edit</c> 工具：对单个文件做精确文本替换。对应 TS <c>tools/edit.ts</c>。
/// </summary>
public static class EditTool
{
    /// <summary>单条替换的 schema。对应 TS <c>replaceEditSchema</c>。</summary>
    private static System.Text.Json.Nodes.JsonObject ReplaceEditSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new System.Text.Json.Nodes.JsonObject
        {
            ["oldText"] = ToolSchemaBuilder.String(
                "Exact text for one targeted replacement. It must be unique in the original file and must not " +
                "overlap with any other edits[].oldText in the same call."),
            ["newText"] = ToolSchemaBuilder.String("Replacement text for this targeted edit."),
        },
        ["required"] = new System.Text.Json.Nodes.JsonArray("oldText", "newText"),
        ["additionalProperties"] = false,
    };

    /// <summary><c>edit</c> 的参数 schema。对应 TS <c>editSchema</c>。</summary>
    public static ToolSchema Schema { get; } = ToolSchemaBuilder.Object(
        new Dictionary<string, object?>
        {
            ["path"] = ToolSchemaBuilder.String("Path to the file to edit (relative or absolute)"),
            ["edits"] = ToolSchemaBuilder.Array(
                ReplaceEditSchema(),
                "One or more targeted replacements. Each edit is matched against the original file, not " +
                "incrementally. Do not include overlapping or nested edits. If two changes touch the same block or " +
                "nearby lines, merge them into one edit instead."),
        },
        "path",
        "edits");

    /// <summary><c>edit</c> 的 details。对应 TS <c>EditToolDetails</c>。</summary>
    public sealed record Details(string Diff, string Patch, long? FirstChangedLine);

    /// <summary>构建 <c>edit</c> 工具注册。对应 TS <c>createEditTool()</c>。</summary>
    public static IToolRegistration Create() => new Registration();

    /// <summary>
    /// 修复模型常发的参数形状：<c>edits</c> 是 JSON 字符串或单个编辑对象，以及顶层的 <c>oldText</c>/<c>newText</c>
    /// 键值对。在副本上操作，调用本身的参数不变。对应 TS <c>prepareEditArguments</c>。
    /// </summary>
    internal static object? PrepareEditArguments(object? input)
    {
        if (input is not IReadOnlyDictionary<string, object?> source) return input;
        var args = new Dictionary<string, object?>(source, StringComparer.Ordinal);

        if (args.TryGetValue("edits", out var edits))
        {
            if (edits is string json)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                        args["edits"] = JsonToObjects(root.EnumerateArray());
                    else if (IsSingleEditInput(root, out var single))
                        args["edits"] = new List<object?> { single };
                }
                catch (System.Text.Json.JsonException)
                {
                    // 解析失败即保持原样，后续校验会给出错误。
                }
            }
            else if (edits is IReadOnlyDictionary<string, object?> editDict && IsSingleEditInput(editDict, out var one))
            {
                args["edits"] = new List<object?> { one };
            }
        }

        var legacyOld = ToolArgs.OptionalString(args, "oldText");
        var legacyNew = ToolArgs.OptionalString(args, "newText");
        if (legacyOld is null || legacyNew is null) return args;

        var list = args.TryGetValue("edits", out var existing) && existing is System.Collections.IEnumerable enumerable
                   && existing is not string
            ? enumerable.Cast<object?>().ToList()
            : [];
        list.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["oldText"] = legacyOld,
            ["newText"] = legacyNew,
        });
        args.Remove("oldText");
        args.Remove("newText");
        args["edits"] = list;
        return args;
    }

    private static List<object?> JsonToObjects(System.Text.Json.JsonElement.ArrayEnumerator array)
    {
        var result = new List<object?>();
        foreach (var element in array) result.Add(JsonElementToObject(element));
        return result;
    }

    private static object? JsonElementToObject(System.Text.Json.JsonElement element) => element.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => JsonElementToObject(property.Value),
            StringComparer.Ordinal),
        System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
        System.Text.Json.JsonValueKind.String => element.GetString(),
        System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };

    private static bool IsSingleEditInput(
        IReadOnlyDictionary<string, object?> value, out Dictionary<string, object?> edit)
        => IsSingleEditInputCore(
            value.TryGetValue("oldText", out var old) ? old : null,
            value.TryGetValue("newText", out var @new) ? @new : null,
            out edit);

    private static bool IsSingleEditInput(System.Text.Json.JsonElement value, out Dictionary<string, object?> edit)
    {
        object? old = value.TryGetProperty("oldText", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.String
            ? o.GetString()
            : null;
        object? @new = value.TryGetProperty("newText", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String
            ? n.GetString()
            : null;
        return IsSingleEditInputCore(old, @new, out edit);
    }

    private static bool IsSingleEditInputCore(object? old, object? @new, out Dictionary<string, object?> edit)
    {
        edit = [];
        if (old is not string oldText || @new is not string newText) return false;
        edit = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["oldText"] = oldText,
            ["newText"] = newText,
        };
        return true;
    }

    /// <summary>校验并取出 <c>path</c> 与 <c>edits</c>。对应 TS <c>validateEditInput</c>。</summary>
    private static (string Path, List<EditDiff.Edit> Edits) ValidateEditInput(object args)
    {
        var arguments = ToolArgs.Object(args);
        var editsArg = ToolArgs.OptionalObjectArray(arguments, "edits");
        if (editsArg is null || editsArg.Count == 0)
            throw new InvalidOperationException(
                "Edit tool input is invalid. edits must contain at least one replacement.");

        var edits = new List<EditDiff.Edit>();
        foreach (var edit in editsArg)
        {
            var oldText = ToolArgs.RequiredString(edit, "oldText");
            var newText = ToolArgs.RequiredString(edit, "newText");
            edits.Add(new EditDiff.Edit(oldText, newText));
        }

        return (ToolArgs.RequiredString(arguments, "path"), edits);
    }

    private static InvalidOperationException AccessError(string path, FileError error)
        => new($"Could not edit file: {path}. Error code: {error.CodeText}.", error);

    private sealed class Registration : IToolRegistration
    {
        public string Name => "edit";

        public string Description =>
            "Edit a single file using exact text replacement. Every edits[].oldText must match a unique, " +
            "non-overlapping region of the original file. If two changes affect the same block or nearby lines, " +
            "merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions " +
            "just to connect distant changes.";

        public ToolSchema Parameters => Schema;

        public string? Replay => null;

        public ToolExecutionMode? ExecutionMode => null;

        public ToolOutputLimits? OutputLimits => null;

        public object? PrepareArguments(object args) => PrepareEditArguments(args);

        public async Task<ToolExecutionResult> ExecuteAsync(object args, IToolExecutionApi api, Context context)
        {
            var (path, edits) = ValidateEditInput(args);
            var env = ToolsEnv.RequireEnv(api);
            var absolutePath = await ToolPaths.ResolveToolPathAsync(env, path, context).ConfigureAwait(false);
            return await FileMutationQueue.WithAsync(env, absolutePath, async () =>
            {
                if (context.AbortSignal is { IsCancellationRequested: true })
                    throw new InvalidOperationException("Operation aborted");

                var info = await env.FileInfoAsync(absolutePath, context).ConfigureAwait(false);
                if (!info.IsOk) throw AccessError(path, info.Error);
                if (info.Value.Kind is not (FileKind.File or FileKind.Symlink))
                    throw new InvalidOperationException($"Could not edit file: {path}. Path is not a file.");

                var readResult = await env.ReadTextFileAsync(absolutePath, context).ConfigureAwait(false);
                if (!readResult.IsOk) throw AccessError(path, readResult.Error);
                if (context.AbortSignal is { IsCancellationRequested: true })
                    throw new InvalidOperationException("Operation aborted");

                var (bom, content) = EditDiff.StripBom(readResult.Value);
                var originalEnding = EditDiff.DetectLineEnding(content);
                var normalizedContent = EditDiff.NormalizeToLf(content);
                var applied = EditDiff.ApplyEditsToNormalizedContent(normalizedContent, edits, path);
                if (context.AbortSignal is { IsCancellationRequested: true })
                    throw new InvalidOperationException("Operation aborted");

                var finalContent = bom + EditDiff.RestoreLineEndings(applied.NewContent, originalEnding);
                var writeResult = await env.WriteFileAsync(absolutePath, finalContent, context).ConfigureAwait(false);
                if (!writeResult.IsOk) throw AccessError(path, writeResult.Error);
                if (context.AbortSignal is { IsCancellationRequested: true })
                    throw new InvalidOperationException("Operation aborted");

                var (diff, firstChangedLine) = EditDiff.GenerateDiffString(applied.BaseContent, applied.NewContent);
                var details = new Details(
                    diff,
                    EditDiff.GenerateUnifiedPatch(path, applied.BaseContent, applied.NewContent),
                    firstChangedLine);
                return new ToolExecutionResult
                {
                    Content = ToolContent.Text($"Successfully replaced {edits.Count} block(s) in {path}."),
                    Details = details,
                };
            }, context).ConfigureAwait(false);
        }
    }
}
