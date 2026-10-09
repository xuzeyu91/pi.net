namespace Pi.CodingAgent.Core.Tools;

/// <summary>One part of a diff result. Port of the jsdiff change object.</summary>
public sealed record DiffPart(string Value, bool Added = false, bool Removed = false);

/// <summary>Header options for <see cref="JsDiff.FormatPatch"/>. Port of the jsdiff header option sets.</summary>
public sealed record HeaderOptions(bool IncludeIndex, bool IncludeUnderline, bool IncludeFileHeaders)
{
    public static readonly HeaderOptions IncludeHeaders = new(true, true, true);

    public static readonly HeaderOptions FileHeadersOnly = new(false, false, true);

    public static readonly HeaderOptions OmitHeaders = new(false, false, false);
}

/// <summary>Options for <see cref="JsDiff.DiffLines"/>. Port of the jsdiff line-diff options.</summary>
public sealed record DiffOptions
{
    public bool IgnoreCase { get; init; }

    public bool OneChangePerToken { get; init; }

    public bool IgnoreWhitespace { get; init; }

    public bool IgnoreNewlineAtEof { get; init; }

    public bool NewlineIsToken { get; init; }

    public bool StripTrailingCr { get; init; }
}

/// <summary>
/// Line diff and unified patch generation, ported line-by-line from <c>diff@8.0.4</c>
/// (<c>dist/diff.js</c>): the Myers O(ND) core with the jsdiff edge-pruning optimizations, the line
/// tokenizer that merges separators into their line, and the structured-patch builder.
/// </summary>
/// <remarks>
/// <para>
/// The TS edit tool renders <c>details.diff</c> / <c>details.patch</c> from jsdiff, and both are
/// model- and user-visible, so a third-party diff library (DiffPlex) with different hunk headers or
/// grouping would silently change that output. The port therefore vendors the algorithm instead.
/// </para>
/// <para>
/// Only what <c>core/tools/edit-diff.ts</c> reaches is ported: <c>diffLines</c> and
/// <c>createTwoFilesPatch</c> (with <c>FILE_HEADERS_ONLY</c>). The callback/async modes, the other
/// diff flavors and <c>applyPatch</c> are left out.
/// </para>
/// </remarks>
public static class JsDiff
{
    // ---------- public API ----------

    /// <summary>Drop empty tokens. The jsdiff <c>removeEmpty</c>.</summary>
    private static List<string> RemoveEmpty(List<string> array)
    {
        var ret = new List<string>();
        foreach (var item in array)
        {
            if (!string.IsNullOrEmpty(item))
            {
                ret.Add(item);
            }
        }

        return ret;
    }

    /// <summary>The jsdiff <c>diffLines(oldStr, newStr, options)</c>.</summary>
    public static List<DiffPart> DiffLines(string oldStr, string newStr, DiffOptions? options = null)
    {
        var lineDiff = new LineDiff();
        var oldTokens = RemoveEmpty(lineDiff.Tokenize(oldStr, options ?? new DiffOptions()));
        var newTokens = RemoveEmpty(lineDiff.Tokenize(newStr, options ?? new DiffOptions()));
        return lineDiff.DiffWithOptionsObj(oldTokens, newTokens, options ?? new DiffOptions());
    }

    /// <summary>
    /// The jsdiff <c>createTwoFilesPatch(oldFileName, newFileName, oldStr, newStr, oldHeader,
    /// newHeader, options)</c>. The TS passes <c>undefined</c> headers and
    /// <c>{ context, headerOptions: FILE_HEADERS_ONLY }</c>.
    /// </summary>
    public static string CreateTwoFilesPatch(
        string oldFileName,
        string newFileName,
        string oldStr,
        string newStr,
        string? oldHeader = null,
        string? newHeader = null,
        int context = 4,
        HeaderOptions? headerOptions = null)
    {
        var patchObj = StructuredPatch(oldFileName, newFileName, oldStr, newStr, oldHeader, newHeader, context);
        return FormatPatch(patchObj, headerOptions ?? HeaderOptions.FileHeadersOnly);
    }

    // ---------- structured patch ----------

    private sealed record Hunk(int OldStart, int OldLines, int NewStart, int NewLines, List<string> Lines);

    private sealed record StructuredPatchObj(
        string OldFileName,
        string NewFileName,
        string? OldHeader,
        string? NewHeader,
        List<Hunk> Hunks);

    private static StructuredPatchObj StructuredPatch(
        string oldFileName,
        string newFileName,
        string oldStr,
        string newStr,
        string? oldHeader,
        string? newHeader,
        int context)
    {
        // The jsdiff default is context 4; the TS always passes an explicit context.
        return DiffLinesResultToPatch(DiffLines(oldStr, newStr), oldFileName, newFileName, oldHeader, newHeader, context);
    }

    private static StructuredPatchObj DiffLinesResultToPatch(
        List<DiffPart> diff,
        string oldFileName,
        string newFileName,
        string? oldHeader,
        string? newHeader,
        int context)
    {
        // STEP 1: build the patch with no "\ No newline at end of file" lines and with the arrays
        // of lines containing trailing newline characters. Tidy up later.
        var parts = new List<DiffPart>(diff) { new DiffPart("") }; // Append an empty value to make cleanup easier
        static List<string> ContextLines(List<string> lines) => lines.Select(entry => " " + entry).ToList();

        // The JS caches each part's lines on the (mutable) part itself; the port keeps a parallel
        // list so the previous context lines can be replayed the same way.
        var linesCache = new List<List<string>>();

        var hunks = new List<Hunk>();
        var oldRangeStart = 0;
        var newRangeStart = 0;
        List<string> curRange = [];
        var oldLine = 1;
        var newLine = 1;

        for (var i = 0; i < parts.Count; i++)
        {
            var current = parts[i];
            var lines = SplitLines(current.Value);
            linesCache.Add(lines);
            if (current.Added || current.Removed)
            {
                // If we have previous context, start with that.
                if (oldRangeStart == 0)
                {
                    var prevLines = i > 0 ? linesCache[i - 1] : null;
                    oldRangeStart = oldLine;
                    newRangeStart = newLine;
                    if (prevLines is not null)
                    {
                        curRange = context > 0
                            ? ContextLines(prevLines.GetRange(Math.Max(0, prevLines.Count - context), Math.Min(context, prevLines.Count)))
                            : [];
                        oldRangeStart -= curRange.Count;
                        newRangeStart -= curRange.Count;
                    }
                }

                // Output our changes.
                foreach (var line in lines)
                {
                    curRange.Add((current.Added ? "+" : "-") + line);
                }

                // Track the updated file position.
                if (current.Added)
                {
                    newLine += lines.Count;
                }
                else
                {
                    oldLine += lines.Count;
                }
            }
            else
            {
                // Identical context lines. Track line changes.
                if (oldRangeStart != 0)
                {
                    // Close out any changes that have been output (or join overlapping).
                    if (lines.Count <= context * 2 && i < parts.Count - 2)
                    {
                        // Overlapping.
                        foreach (var line in ContextLines(lines))
                        {
                            curRange.Add(line);
                        }
                    }
                    else
                    {
                        // End the range and output.
                        var contextSize = Math.Min(lines.Count, context);
                        foreach (var line in ContextLines(lines.GetRange(0, contextSize)))
                        {
                            curRange.Add(line);
                        }

                        var hunk = new Hunk(
                            oldRangeStart,
                            oldLine - oldRangeStart + contextSize,
                            newRangeStart,
                            newLine - newRangeStart + contextSize,
                            curRange);
                        hunks.Add(hunk);
                        oldRangeStart = 0;
                        newRangeStart = 0;
                        curRange = [];
                    }
                }

                oldLine += lines.Count;
                newLine += lines.Count;
            }
        }

        // STEP 2: eliminate the trailing \n from each line of each hunk, and, where needed, add
        // "\ No newline at end of file".
        foreach (var hunk in hunks)
        {
            for (var i = 0; i < hunk.Lines.Count; i++)
            {
                if (hunk.Lines[i].EndsWith('\n'))
                {
                    hunk.Lines[i] = hunk.Lines[i][..^1];
                }
                else
                {
                    hunk.Lines.Insert(i + 1, "\\ No newline at end of file");
                    i++; // Skip the line we just added, then continue iterating.
                }
            }
        }

        return new StructuredPatchObj(oldFileName, newFileName, oldHeader, newHeader, hunks);
    }

    /// <summary>The jsdiff <c>formatPatch(patch, headerOptions)</c>.</summary>
    private static string FormatPatch(StructuredPatchObj patch, HeaderOptions headerOptions)
    {
        var ret = new List<string>();
        if (headerOptions.IncludeIndex && patch.OldFileName == patch.NewFileName)
        {
            ret.Add("Index: " + patch.OldFileName);
        }

        if (headerOptions.IncludeUnderline)
        {
            ret.Add("===================================================================");
        }

        if (headerOptions.IncludeFileHeaders)
        {
            ret.Add("--- " + patch.OldFileName + (patch.OldHeader is null ? "" : "\t" + patch.OldHeader));
            ret.Add("+++ " + patch.NewFileName + (patch.NewHeader is null ? "" : "\t" + patch.NewHeader));
        }

        foreach (var hunk in patch.Hunks)
        {
            var oldStart = hunk.OldStart;
            var newStart = hunk.NewStart;
            var oldLines = hunk.OldLines;
            var newLines = hunk.NewLines;

            // Unified Diff Format quirk: if the chunk size is 0, the first number is one lower than
            // one would expect.
            if (oldLines == 0)
            {
                oldStart -= 1;
            }

            if (newLines == 0)
            {
                newStart -= 1;
            }

            ret.Add($"@@ -{oldStart},{oldLines} +{newStart},{newLines} @@");
            foreach (var line in hunk.Lines)
            {
                ret.Add(line);
            }
        }

        return string.Join("\n", ret) + "\n";
    }

    /// <summary>
    /// Split text into lines including the trailing newline character (where present). The jsdiff
    /// <c>splitLines</c>; the empty-string case keeps the upstream's two-token result verbatim.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var hasTrailingNl = text.EndsWith('\n');
        var result = text.Split('\n').Select(line => line + "\n").ToList();
        if (hasTrailingNl)
        {
            result.RemoveAt(result.Count - 1);
        }
        else
        {
            var last = result[^1];
            result.RemoveAt(result.Count - 1);
            result.Add(last.Length > 0 ? last[..^1] : last);
        }

        return result;
    }

    // ---------- Myers core (Diff base class) ----------

    private sealed class DiffPath
    {
        public int OldPos { get; set; }

        public Component? LastComponent { get; set; }

        public DiffPath(int oldPos, Component? lastComponent)
        {
            OldPos = oldPos;
            LastComponent = lastComponent;
        }
    }

    private sealed class Component
    {
        public int Count { get; init; }

        public bool Added { get; init; }

        public bool Removed { get; init; }

        public Component? PreviousComponent { get; set; }

        public string? Value { get; set; }
    }

    /// <summary>Port of the jsdiff <c>Diff</c> base class (the line-diff slice).</summary>
    private abstract class DiffBase
    {
        public virtual List<string> Tokenize(string value, DiffOptions options) =>
            [.. value.Select(character => character.ToString())];

        protected virtual bool Equals(string left, string right, DiffOptions options) =>
            left == right || (options.IgnoreCase && left.ToLowerInvariant() == right.ToLowerInvariant());

        protected virtual string Join(List<string> chars) => string.Join("", chars);

        public List<DiffPart> DiffWithOptionsObj(List<string> oldTokens, List<string> newTokens, DiffOptions options)
        {
            var newLen = newTokens.Count;
            var oldLen = oldTokens.Count;
            var editLength = 1;
            var maxEditLength = newLen + oldLen;

            // Seed editLength = 0, i.e. the content starts with the same values (diagonal 0).
            var seed = new DiffPath(-1, null);
            var bestPath = new Dictionary<int, DiffPath> { [0] = seed };
            var newPos = ExtractCommon(seed, newTokens, oldTokens, 0, options);
            if (seed.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
            {
                // Identity per the equality and tokenizer.
                return BuildValues(seed.LastComponent, newTokens, oldTokens);
            }

            var minDiagonalToConsider = int.MinValue;
            var maxDiagonalToConsider = int.MaxValue;

            // Main worker method: checks all permutations of a given edit length for acceptance.
            // Returns the finished parts when the end of both strings is reached, null otherwise.
            List<DiffPart>? ExecEditLength()
            {
                for (var diagonalPath = Math.Max(minDiagonalToConsider, -editLength);
                     diagonalPath <= Math.Min(maxDiagonalToConsider, editLength);
                     diagonalPath += 2)
                {
                    DiffPath basePath;
                    var removePath = bestPath.TryGetValue(diagonalPath - 1, out var rp) ? rp : null;
                    var addPath = bestPath.TryGetValue(diagonalPath + 1, out var ap) ? ap : null;
                    if (removePath is not null)
                    {
                        // No one else is going to attempt to use this value, clear it.
                        bestPath[diagonalPath - 1] = null!;
                    }

                    var canAdd = false;
                    if (addPath is not null)
                    {
                        // What newPos will be after we do an insertion.
                        var addPathNewPos = addPath.OldPos - diagonalPath;
                        canAdd = 0 <= addPathNewPos && addPathNewPos < newLen;
                    }

                    var canRemove = removePath is not null && removePath.OldPos + 1 < oldLen;
                    if (!canAdd && !canRemove)
                    {
                        // If this path is terminal then prune.
                        bestPath[diagonalPath] = null!;
                        continue;
                    }

                    // Select the diagonal that we want to branch from: the prior path whose position
                    // in the old string is the farthest from the origin and does not pass the bounds.
                    if (!canRemove || (canAdd && removePath!.OldPos < addPath!.OldPos))
                    {
                        basePath = AddToPath(addPath!, true, false, 0, options);
                    }
                    else
                    {
                        basePath = AddToPath(removePath!, false, true, 1, options);
                    }

                    newPos = ExtractCommon(basePath, newTokens, oldTokens, diagonalPath, options);
                    if (basePath.OldPos + 1 >= oldLen && newPos + 1 >= newLen)
                    {
                        // If we have hit the end of both strings, then we are done.
                        return BuildValues(basePath.LastComponent, newTokens, oldTokens);
                    }

                    bestPath[diagonalPath] = basePath;
                    if (basePath.OldPos + 1 >= oldLen)
                    {
                        maxDiagonalToConsider = Math.Min(maxDiagonalToConsider, diagonalPath - 1);
                    }

                    if (newPos + 1 >= newLen)
                    {
                        minDiagonalToConsider = Math.Max(minDiagonalToConsider, diagonalPath + 1);
                    }
                }

                editLength++;
                return null;
            }

            while (editLength <= maxEditLength)
            {
                var finished = ExecEditLength();
                if (finished is not null)
                {
                    return finished;
                }
            }

            return [];
        }

        private DiffPath AddToPath(DiffPath path, bool added, bool removed, int oldPosInc, DiffOptions options)
        {
            var last = path.LastComponent;
            if (last is not null && !options.OneChangePerToken && last.Added == added && last.Removed == removed)
            {
                return new DiffPath(
                    path.OldPos + oldPosInc,
                    new Component
                    {
                        Count = last.Count + 1,
                        Added = added,
                        Removed = removed,
                        PreviousComponent = last.PreviousComponent,
                    });
            }

            return new DiffPath(
                path.OldPos + oldPosInc,
                new Component { Count = 1, Added = added, Removed = removed, PreviousComponent = last });
        }

        private int ExtractCommon(DiffPath basePath, List<string> newTokens, List<string> oldTokens, int diagonalPath, DiffOptions options)
        {
            var newLen = newTokens.Count;
            var oldLen = oldTokens.Count;
            var oldPos = basePath.OldPos;
            var newPos = oldPos - diagonalPath;
            var commonCount = 0;
            while (newPos + 1 < newLen && oldPos + 1 < oldLen &&
                   Equals(oldTokens[oldPos + 1], newTokens[newPos + 1], options))
            {
                newPos++;
                oldPos++;
                commonCount++;
                if (options.OneChangePerToken)
                {
                    basePath.LastComponent = new Component
                    {
                        Count = 1,
                        PreviousComponent = basePath.LastComponent,
                        Added = false,
                        Removed = false,
                    };
                }
            }

            if (commonCount != 0 && !options.OneChangePerToken)
            {
                basePath.LastComponent = new Component
                {
                    Count = commonCount,
                    PreviousComponent = basePath.LastComponent,
                    Added = false,
                    Removed = false,
                };
            }

            basePath.OldPos = oldPos;
            return newPos;
        }

        private List<DiffPart> BuildValues(Component? lastComponent, List<string> newTokens, List<string> oldTokens)
        {
            // Convert the linked list of components, in reverse order, to an array in the right order.
            var components = new List<Component>();
            Component? nextComponent;
            while (lastComponent is not null)
            {
                components.Add(lastComponent);
                nextComponent = lastComponent.PreviousComponent;
                lastComponent.PreviousComponent = null;
                lastComponent = nextComponent;
            }

            components.Reverse();

            var componentLen = components.Count;
            var componentPos = 0;
            var newPos = 0;
            var oldPos = 0;
            for (; componentPos < componentLen; componentPos++)
            {
                var component = components[componentPos];
                if (!component.Removed)
                {
                    component.Value = Join(newTokens.GetRange(newPos, component.Count));
                    newPos += component.Count;
                    // Common case.
                    if (!component.Added)
                    {
                        oldPos += component.Count;
                    }
                }
                else
                {
                    component.Value = Join(oldTokens.GetRange(oldPos, component.Count));
                    oldPos += component.Count;
                }
            }

            return components.ConvertAll(component => new DiffPart(component.Value ?? "", component.Added, component.Removed));
        }
    }

    /// <summary>Port of the jsdiff <c>LineDiff</c>.</summary>
    private sealed class LineDiff : DiffBase
    {
        public override List<string> Tokenize(string value, DiffOptions options)
        {
            if (options.StripTrailingCr)
            {
                // Remove one \r before \n to match GNU diff's --strip-trailing-cr behavior.
                value = value.Replace("\r\n", "\n");
            }

            var retLines = new List<string>();
            var linesAndNewlines = SplitKeepingSeparators(value);

            // Ignore the final empty token that occurs if the string ends with a new line.
            if (linesAndNewlines.Count > 0 && linesAndNewlines[^1].Length == 0)
            {
                linesAndNewlines.RemoveAt(linesAndNewlines.Count - 1);
            }

            // Merge the content and line separators into single tokens.
            for (var i = 0; i < linesAndNewlines.Count; i++)
            {
                var line = linesAndNewlines[i];
                if (i % 2 == 1 && !options.NewlineIsToken)
                {
                    retLines[^1] += line;
                }
                else
                {
                    retLines.Add(line);
                }
            }

            return retLines;
        }

        protected override bool Equals(string left, string right, DiffOptions options)
        {
            // If we're ignoring whitespace, normalize lines by stripping whitespace before checking
            // equality (with the newlineIsToken interaction the JS documents).
            if (options.IgnoreWhitespace)
            {
                if (!options.NewlineIsToken || !left.Contains('\n'))
                {
                    left = left.Trim();
                }

                if (!options.NewlineIsToken || !right.Contains('\n'))
                {
                    right = right.Trim();
                }
            }
            else if (options.IgnoreNewlineAtEof && !options.NewlineIsToken)
            {
                if (left.EndsWith('\n'))
                {
                    left = left[..^1];
                }

                if (right.EndsWith('\n'))
                {
                    right = right[..^1];
                }
            }

            return base.Equals(left, right, options);
        }

        /// <summary>The JS <c>value.split(/(\n|\r\n)/)</c>: separators are captured as their own tokens.</summary>
        private static List<string> SplitKeepingSeparators(string value)
        {
            // JS split keeps every element, including empty content tokens between consecutive
            // separators and the trailing empty string after a final separator.
            var result = new List<string>();
            var start = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\n')
                {
                    continue;
                }

                // The regex alternation tries \n first but a preceding \r makes \r\n the match.
                var separatorLength = i > start && value[i - 1] == '\r' ? 2 : 1;
                var separatorStart = i - (separatorLength - 1);
                result.Add(value[start..separatorStart]);
                result.Add(value.Substring(separatorStart, separatorLength));
                start = i + 1;
            }

            result.Add(value[start..]);
            return result;
        }
    }
}
