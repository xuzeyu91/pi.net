using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace Pi.Durable.Tools;

/// <summary>
/// <c>edit</c> 与类似工具共用的 diff 计算。对应 TS <c>tools/edit-diff.ts</c>（原版依赖 npm <c>diff</c> 包）。
/// </summary>
/// <remarks>
/// <b>差异（依赖替换）</b>：TS 用 npm <c>diff</c> 的 <c>diffLines</c> / <c>createTwoFilesPatch</c>，两者都走 Myers
/// 差分。C# 侧改用 DiffPlex（同为 Myers 差分），并把行序列重建为 TS <c>diffLines</c> 的「变更块」视图。
/// 行内容与行序与 TS 一致；块边界在等价解之间可能有出入（Myers 的多个最短编辑脚本任选一个），
/// 因此 <c>generateDiffString</c> 的显示文本、以及 <c>createTwoFilesPatch</c> 式的 hunk 头在个别输入上
/// 与 TS 逐字不同——消费者（模型）看到的是语义相同的 diff。
/// </remarks>
public static partial class EditDiff
{
    /// <summary>检出内容的换行风格。对应 TS <c>detectLineEnding</c>。</summary>
    public static string DetectLineEnding(string content)
    {
        var crlfIndex = content.IndexOf("\r\n", StringComparison.Ordinal);
        var lfIndex = content.IndexOf('\n');
        if (lfIndex == -1) return "\n";
        if (crlfIndex == -1) return "\n";
        return crlfIndex < lfIndex ? "\r\n" : "\n";
    }

    /// <summary>把 CRLF 与单独 CR 统统折成 LF。对应 TS <c>normalizeToLF</c>。</summary>
    public static string NormalizeToLf(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>把 LF 还原成给定换行风格。对应 TS <c>restoreLineEndings</c>。</summary>
    public static string RestoreLineEndings(string text, string ending)
        => ending == "\r\n" ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text;

    /// <summary>
    /// 模糊匹配的归一化。逐级应用：NFKC、按行去尾空白、弯引号折成 ASCII、各种破折号折成 <c>-</c>、
    /// 特殊空格折成普通空格。对应 TS <c>normalizeForFuzzyMatch</c>。
    /// </summary>
    public static string NormalizeForFuzzyMatch(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);

        // 按行去尾空白。
        var lines = normalized.Split('\n');
        for (var i = 0; i < lines.Length; i++) lines[i] = lines[i].TrimEnd();
        var joined = string.Join('\n', lines);

        var builder = new StringBuilder(joined.Length);
        foreach (var ch in joined)
        {
            builder.Append(ch switch
            {
                // 智能单引号 → '
                '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
                // 智能双引号 → "
                '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
                // 各种破折号 / 连字符 → -
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
                // 各种特殊空格 → 普通空格
                '\u00A0' or '\u202F' or '\u205F' or '\u3000' => ' ',
                // U+2002..U+200A
                >= '\u2002' and <= '\u200A' => ' ',
                _ => ch,
            });
        }

        return builder.ToString();
    }

    /// <summary>剥掉 UTF-8 BOM（如有），同时返回 BOM 与去掉它的文本。对应 TS <c>stripBom</c>。</summary>
    public static (string Bom, string Text) StripBom(string content)
        => content.StartsWith('\uFEFF') ? ("\uFEFF", content[1..]) : ("", content);

    /// <summary>一次定位替换的匹配结果。对应 TS <c>FuzzyMatchResult</c>。</summary>
    public sealed record FuzzyMatchResult(
        bool Found,
        int Index,
        int MatchLength,
        bool UsedFuzzyMatch,
        string ContentForReplacement);

    /// <summary>一次替换（匹配位置 / 长度 / 新文本）。对应 TS <c>Edit</c>。</summary>
    public sealed record Edit(string OldText, string NewText);

    /// <summary>应用编辑后的新旧内容。对应 TS <c>AppliedEditsResult</c>。</summary>
    public sealed record AppliedEditsResult(string BaseContent, string NewContent);

    /// <summary>
    /// 在内容里找 <paramref name="oldText"/>：先精确匹配，再模糊匹配。模糊匹配时返回的
    /// <see cref="FuzzyMatchResult.ContentForReplacement"/> 是模糊归一化后的内容。
    /// 对应 TS <c>fuzzyFindText</c>。
    /// </summary>
    public static FuzzyMatchResult FuzzyFindText(string content, string oldText)
    {
        var exactIndex = content.IndexOf(oldText, StringComparison.Ordinal);
        if (exactIndex != -1)
            return new FuzzyMatchResult(true, exactIndex, oldText.Length, false, content);

        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        var fuzzyIndex = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal);

        if (fuzzyIndex == -1) return new FuzzyMatchResult(false, -1, 0, false, content);

        return new FuzzyMatchResult(true, fuzzyIndex, fuzzyOldText.Length, true, fuzzyContent);
    }

    /// <summary>一次被接受的编辑。对应 TS <c>MatchedEdit</c>。</summary>
    private sealed record MatchedEdit(int EditIndex, int MatchIndex, int MatchLength, string NewText);

    /// <summary>一个替换范围。对应 TS <c>TextReplacement</c>。</summary>
    public sealed record TextReplacement(int MatchIndex, int MatchLength, string NewText);

    /// <summary>一行的字节区间。对应 TS <c>LineSpan</c>。</summary>
    private readonly record struct LineSpan(int Start, int End);

    /// <summary>
    /// 按「保留换行」的方式切行，等价 TS <c>content.match(/[^\n]*\n|[^\n]+/g)</c>：每段含其结束换行。
    /// </summary>
    internal static List<string> SplitLinesWithEndings(string content)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            lines.Add(content[start..(i + 1)]);
            start = i + 1;
        }

        if (start < content.Length) lines.Add(content[start..]);
        return lines;
    }

    private static List<LineSpan> GetLineSpans(string content)
    {
        var offset = 0;
        var spans = new List<LineSpan>();
        foreach (var line in SplitLinesWithEndings(content))
        {
            spans.Add(new LineSpan(offset, offset + line.Length));
            offset += line.Length;
        }

        return spans;
    }

    private static (int StartLine, int EndLine) GetReplacementLineRange(List<LineSpan> lines, TextReplacement replacement)
    {
        var replacementStart = replacement.MatchIndex;
        var replacementEnd = replacement.MatchIndex + replacement.MatchLength;

        var startLine = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (replacementStart >= line.Start && replacementStart < line.End)
            {
                startLine = i;
                break;
            }
        }

        if (startLine == -1) throw new InvalidOperationException("Replacement range is outside the base content.");

        var endLine = startLine;
        while (endLine < lines.Count && lines[endLine].End < replacementEnd) endLine++;
        if (endLine >= lines.Count) throw new InvalidOperationException("Replacement range is outside the base content.");

        return (startLine, endLine + 1);
    }

    private static string ApplyReplacements(string content, IReadOnlyList<TextReplacement> replacements, int offset = 0)
    {
        var result = content;
        for (var i = replacements.Count - 1; i >= 0; i--)
        {
            var replacement = replacements[i];
            var matchIndex = replacement.MatchIndex - offset;
            result = string.Concat(
                result.AsSpan(0, matchIndex),
                replacement.NewText,
                result.AsSpan(matchIndex + replacement.MatchLength));
        }

        return result;
    }

    /// <summary>
    /// 把「针对 <paramref name="baseContent"/> 匹配到的替换」应用到 <paramref name="originalContent"/>，
    /// 同时保留原内容中未被触及的整行块。对应 TS <c>applyReplacementsPreservingUnchangedLines</c>。
    /// </summary>
    public static string ApplyReplacementsPreservingUnchangedLines(
        string originalContent, string baseContent, IReadOnlyList<TextReplacement> replacements)
    {
        var originalLines = SplitLinesWithEndings(originalContent);
        var baseLines = GetLineSpans(baseContent);
        if (originalLines.Count != baseLines.Count)
            throw new InvalidOperationException(
                "Cannot preserve unchanged lines because the base content has a different line count.");

        var groups = new List<(int StartLine, int EndLine, List<TextReplacement> Replacements)>();
        var sorted = replacements.OrderBy(r => r.MatchIndex).ToList();
        foreach (var replacement in sorted)
        {
            var range = GetReplacementLineRange(baseLines, replacement);
            if (groups.Count > 0 && range.StartLine < groups[^1].EndLine)
            {
                var last = groups[^1];
                last.EndLine = Math.Max(last.EndLine, range.EndLine);
                last.Replacements.Add(replacement);
                continue;
            }

            groups.Add((range.StartLine, range.EndLine, [replacement]));
        }

        var originalLineIndex = 0;
        var result = new StringBuilder();
        foreach (var group in groups)
        {
            for (var i = originalLineIndex; i < group.StartLine; i++) result.Append(originalLines[i]);

            var groupStartOffset = baseLines[group.StartLine].Start;
            var groupEndOffset = baseLines[group.EndLine - 1].End;
            result.Append(ApplyReplacements(
                baseContent[groupStartOffset..groupEndOffset], group.Replacements, groupStartOffset));
            originalLineIndex = group.EndLine;
        }

        for (var i = originalLineIndex; i < originalLines.Count; i++) result.Append(originalLines[i]);
        return result.ToString();
    }

    private static int CountOccurrences(string content, string oldText)
    {
        var fuzzyContent = NormalizeForFuzzyMatch(content);
        var fuzzyOldText = NormalizeForFuzzyMatch(oldText);
        if (fuzzyOldText.Length == 0) return 0;

        var count = 0;
        var index = fuzzyContent.IndexOf(fuzzyOldText, StringComparison.Ordinal);
        while (index != -1)
        {
            count++;
            index = fuzzyContent.IndexOf(fuzzyOldText, index + fuzzyOldText.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static InvalidOperationException NotFoundError(string path, int editIndex, int totalEdits)
        => new(totalEdits == 1
            ? $"Could not find the exact text in {path}. The old text must match exactly including all whitespace and newlines."
            : $"Could not find edits[{editIndex}] in {path}. The oldText must match exactly including all whitespace and newlines.");

    private static InvalidOperationException DuplicateError(string path, int editIndex, int totalEdits, int occurrences)
        => new(totalEdits == 1
            ? $"Found {occurrences} occurrences of the text in {path}. The text must be unique. Please provide more context to make it unique."
            : $"Found {occurrences} occurrences of edits[{editIndex}] in {path}. Each oldText must be unique. Please provide more context to make it unique.");

    private static InvalidOperationException EmptyOldTextError(string path, int editIndex, int totalEdits)
        => new(totalEdits == 1
            ? $"oldText must not be empty in {path}."
            : $"edits[{editIndex}].oldText must not be empty in {path}.");

    private static InvalidOperationException NoChangeError(string path, int totalEdits)
        => new(totalEdits == 1
            ? $"No changes made to {path}. The replacement produced identical content. This might indicate an issue with special characters or the text not existing as expected."
            : $"No changes made to {path}. The replacements produced identical content.");

    /// <summary>
    /// 把一或多个精确文本替换应用到 LF 归一化后的内容。对应 TS <c>applyEditsToNormalizedContent</c>。
    /// </summary>
    /// <remarks>
    /// 所有编辑都针对同一份原内容匹配，随后逆序应用以保持偏移稳定。若任一编辑需要模糊匹配，整个操作在模糊归一化
    /// 空间中运行，再把行级变化覆盖回原内容，从而未变的行块保留原有字节。
    /// </remarks>
    public static AppliedEditsResult ApplyEditsToNormalizedContent(
        string normalizedContent, IReadOnlyList<Edit> edits, string path)
    {
        var normalizedEdits = edits
            .Select(edit => new Edit(NormalizeToLf(edit.OldText), NormalizeToLf(edit.NewText)))
            .ToList();

        for (var i = 0; i < normalizedEdits.Count; i++)
        {
            if (normalizedEdits[i].OldText.Length == 0) throw EmptyOldTextError(path, i, normalizedEdits.Count);
        }

        var initialMatches = normalizedEdits.Select(edit => FuzzyFindText(normalizedContent, edit.OldText)).ToList();
        var usedFuzzyMatch = initialMatches.Any(match => match.UsedFuzzyMatch);
        var replacementBaseContent = usedFuzzyMatch ? NormalizeForFuzzyMatch(normalizedContent) : normalizedContent;

        var matchedEdits = new List<MatchedEdit>();
        for (var i = 0; i < normalizedEdits.Count; i++)
        {
            var edit = normalizedEdits[i];
            var matchResult = FuzzyFindText(replacementBaseContent, edit.OldText);
            if (!matchResult.Found) throw NotFoundError(path, i, normalizedEdits.Count);

            var occurrences = CountOccurrences(replacementBaseContent, edit.OldText);
            if (occurrences > 1) throw DuplicateError(path, i, normalizedEdits.Count, occurrences);

            matchedEdits.Add(new MatchedEdit(i, matchResult.Index, matchResult.MatchLength, edit.NewText));
        }

        matchedEdits.Sort((a, b) => a.MatchIndex.CompareTo(b.MatchIndex));
        for (var i = 1; i < matchedEdits.Count; i++)
        {
            var previous = matchedEdits[i - 1];
            var current = matchedEdits[i];
            if (previous.MatchIndex + previous.MatchLength > current.MatchIndex)
                throw new InvalidOperationException(
                    $"edits[{previous.EditIndex}] and edits[{current.EditIndex}] overlap in {path}. Merge them into one edit or target disjoint regions.");
        }

        var baseContent = normalizedContent;
        var replacements = matchedEdits
            .Select(m => new TextReplacement(m.MatchIndex, m.MatchLength, m.NewText))
            .ToList();
        var newContent = usedFuzzyMatch
            ? ApplyReplacementsPreservingUnchangedLines(normalizedContent, replacementBaseContent, replacements)
            : ApplyReplacements(replacementBaseContent, replacements);

        if (baseContent == newContent) throw NoChangeError(path, normalizedEdits.Count);

        return new AppliedEditsResult(baseContent, newContent);
    }

    /// <summary>
    /// 标准 unified patch。对应 TS <c>generateUnifiedPatch</c>（<c>createTwoFilesPatch</c> +
    /// <c>FILE_HEADERS_ONLY</c>，默认 4 行上下文）。
    /// </summary>
    public static string GenerateUnifiedPatch(string path, string oldContent, string newContent, int contextLines = 4)
    {
        var diff = InlineDiffBuilder.Diff(oldContent, newContent, ignoreWhiteSpace: false, ignoreCase: false);
        var oldLines = SplitLinesCompat(oldContent);
        var newLines = SplitLinesCompat(newContent);
        var hunks = BuildHunks(diff, oldLines, newLines, contextLines);

        var builder = new StringBuilder();
        builder.Append("===================================================================\n");
        builder.Append("--- ").Append(path).Append('\n');
        builder.Append("+++ ").Append(path).Append('\n');
        foreach (var hunk in hunks)
        {
            builder.Append("@@ -").Append(hunk.OldStart).Append(',').Append(hunk.OldCount)
                .Append(" +").Append(hunk.NewStart).Append(',').Append(hunk.NewCount).Append(" @@\n");
            foreach (var line in hunk.Lines) builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>一行 diff（含前缀）。</summary>
    private sealed record DiffLine(char Prefix, string Text);

    /// <summary>一个上下文分组后的 hunk。</summary>
    private sealed record Hunk(int OldStart, int OldCount, int NewStart, int NewCount, List<string> Lines);

    /// <summary>
    /// 把 DiffPlex 的行模型折成「<c>diffLines</c> 式的变更块 + 上下文分组」并生成 hunk。
    /// </summary>
    private static List<Hunk> BuildHunks(
        DiffPaneModel diff, IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, int contextLines)
    {
        // 1) 逐行分类；未变更的行成组。
        var ops = new List<(char Kind, int Count)>(); // Kind: ' ' / '-' / '+'
        foreach (var line in diff.Lines)
        {
            var kind = line.Type switch
            {
                ChangeType.Inserted => '+',
                ChangeType.Deleted => '-',
                ChangeType.Modified => '*',
                _ => ' ',
            };
            if (kind == '*')
            {
                // DiffPlex 的 Modified 表示同一行被替换：先删后增。
                AppendOp(ops, '-', 1);
                AppendOp(ops, '+', 1);
            }
            else
            {
                AppendOp(ops, kind, 1);
            }
        }

        // 2) 展开为带行号的 diff 行。
        var expanded = new List<DiffLine>();
        var oldLine = 1;
        var newLine = 1;
        foreach (var (kind, count) in ops)
        {
            for (var i = 0; i < count; i++)
            {
                switch (kind)
                {
                    case '-':
                        expanded.Add(new DiffLine('-', oldLines.ElementAtOrDefault(oldLine - 1) ?? ""));
                        oldLine++;
                        break;
                    case '+':
                        expanded.Add(new DiffLine('+', newLines.ElementAtOrDefault(newLine - 1) ?? ""));
                        newLine++;
                        break;
                    default:
                        expanded.Add(new DiffLine(' ', oldLines.ElementAtOrDefault(oldLine - 1) ?? ""));
                        oldLine++;
                        newLine++;
                        break;
                }
            }
        }

        // 3) 按上下文把行分组为 hunk。
        var hunks = new List<Hunk>();
        var index = 0;
        while (index < expanded.Count)
        {
            if (expanded[index].Prefix == ' ')
            {
                index++;
                continue;
            }

            // 变更簇的起点；向前收 contextLines 行上下文。
            var start = index;
            var leading = Math.Min(contextLines, start);
            start -= leading;

            // 向后扩展到下一个变更簇，最多并入 contextLines 行未变更行。
            var end = index;
            var trailingUnchanged = 0;
            while (end < expanded.Count)
            {
                if (expanded[end].Prefix == ' ')
                {
                    trailingUnchanged++;
                    if (trailingUnchanged > contextLines * 2) break;
                }
                else
                {
                    trailingUnchanged = 0;
                }

                end++;
            }

            // 收尾：砍掉多余的尾部上下文（保留 contextLines 行）。
            while (end > index && expanded[end - 1].Prefix == ' ')
            {
                var tailCount = 0;
                var probe = end;
                while (probe > index && expanded[probe - 1].Prefix == ' ')
                {
                    tailCount++;
                    probe--;
                }

                if (tailCount <= contextLines) break;
                end--;
            }

            var slice = expanded.GetRange(start, end - start);
            var oldCount = slice.Count(l => l.Prefix is ' ' or '-');
            var newCount = slice.Count(l => l.Prefix is ' ' or '+');
            var oldStart = 1 + expanded.Take(start).Count(l => l.Prefix is ' ' or '-');
            var newStart = 1 + expanded.Take(start).Count(l => l.Prefix is ' ' or '+');
            var normalized = slice.Select(l => $"{l.Prefix}{l.Text}").ToList();
            hunks.Add(new Hunk(oldStart, oldCount, newStart, newCount, normalized));
            index = end;
        }

        if (hunks.Count == 0)
            hunks.Add(new Hunk(1, 0, 1, 0, []));

        return hunks;
    }

    private static void AppendOp(List<(char Kind, int Count)> ops, char kind, int count)
    {
        if (ops.Count > 0 && ops[^1].Kind == kind)
        {
            var last = ops[^1];
            ops[^1] = (last.Kind, last.Count + count);
            return;
        }

        ops.Add((kind, count));
    }

    /// <summary>按 <c>\n</c> 拆行（保留末尾空串），与 TS <c>content.split("\n")</c> 一致。</summary>
    private static List<string> SplitLinesCompat(string content) => [.. content.Split('\n')];

    /// <summary>
    /// 带行号与上下文的展示型 diff。对应 TS <c>generateDiffString</c>（默认 4 行上下文）。
    /// </summary>
    public static (string Diff, int? FirstChangedLine) GenerateDiffString(
        string oldContent, string newContent, int contextLines = 4)
    {
        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        var maxLineNum = Math.Max(oldLines.Length, newLines.Length);
        var lineNumWidth = maxLineNum.ToString(CultureInfo.InvariantCulture).Length;

        // 用同 LCS 的差分取得「变更块 / 未变块」交替序列。
        var parts = DiffParts(oldContent, newContent);

        var output = new List<string>();
        var oldLineNum = 1;
        var newLineNum = 1;
        var lastWasChange = false;
        int? firstChangedLine = null;

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var raw = part.Value.Split('\n').ToList();
            if (raw.Count > 0 && raw[^1] == "") raw.RemoveAt(raw.Count - 1);

            if (part.Added || part.Removed)
            {
                firstChangedLine ??= newLineNum;

                foreach (var line in raw)
                {
                    if (part.Added)
                    {
                        output.Add($"+{newLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                        newLineNum++;
                    }
                    else
                    {
                        output.Add($"-{oldLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                        oldLineNum++;
                    }
                }

                lastWasChange = true;
            }
            else
            {
                var nextPartIsChange = i < parts.Count - 1 && (parts[i + 1].Added || parts[i + 1].Removed);
                var hasLeadingChange = lastWasChange;
                var hasTrailingChange = nextPartIsChange;

                if (hasLeadingChange && hasTrailingChange)
                {
                    if (raw.Count <= contextLines * 2)
                    {
                        foreach (var line in raw)
                        {
                            output.Add($" {oldLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                            oldLineNum++;
                            newLineNum++;
                        }
                    }
                    else
                    {
                        var leadingLines = raw.Take(contextLines).ToList();
                        var trailingLines = raw.Skip(raw.Count - contextLines).ToList();
                        var skippedLines = raw.Count - leadingLines.Count - trailingLines.Count;

                        foreach (var line in leadingLines)
                        {
                            output.Add($" {oldLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                            oldLineNum++;
                            newLineNum++;
                        }

                        output.Add($" {"".PadLeft(lineNumWidth)} ...");
                        oldLineNum += skippedLines;
                        newLineNum += skippedLines;

                        foreach (var line in trailingLines)
                        {
                            output.Add($" {oldLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                            oldLineNum++;
                            newLineNum++;
                        }
                    }
                }
                else if (hasLeadingChange)
                {
                    var shownLines = raw.Take(contextLines).ToList();
                    var skippedLines = raw.Count - shownLines.Count;

                    foreach (var line in shownLines)
                    {
                        output.Add($" {oldLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                        oldLineNum++;
                        newLineNum++;
                    }

                    if (skippedLines > 0)
                    {
                        output.Add($" {"".PadLeft(lineNumWidth)} ...");
                        oldLineNum += skippedLines;
                        newLineNum += skippedLines;
                    }
                }
                else if (hasTrailingChange)
                {
                    var skippedLines = Math.Max(0, raw.Count - contextLines);
                    if (skippedLines > 0)
                    {
                        output.Add($" {"".PadLeft(lineNumWidth)} ...");
                        oldLineNum += skippedLines;
                        newLineNum += skippedLines;
                    }

                    foreach (var line in raw.Skip(skippedLines))
                    {
                        output.Add($" {oldLineNum.ToString(CultureInfo.InvariantCulture).PadLeft(lineNumWidth)} {line}");
                        oldLineNum++;
                        newLineNum++;
                    }
                }
                else
                {
                    oldLineNum += raw.Count;
                    newLineNum += raw.Count;
                }

                lastWasChange = false;
            }
        }

        return (string.Join('\n', output), firstChangedLine);
    }

    /// <summary>一段差分（新增 / 删除 / 未变）及其文本。对应 npm <c>diff</c> 的 <c>Change</c>。</summary>
    internal sealed record DiffPart(string Value, bool Added, bool Removed);

    /// <summary>
    /// <c>diffLines</c> 的等价物：把两份内容折成「变更块 / 未变块」序列，块文本含行尾换行。
    /// </summary>
    internal static List<DiffPart> DiffParts(string oldContent, string newContent)
    {
        var diff = InlineDiffBuilder.Diff(oldContent, newContent, ignoreWhiteSpace: false, ignoreCase: false);
        var newLines = newContent.Split('\n');
        var oldLines = oldContent.Split('\n');

        // 按行聚合：连续的 Inserted 合成一个 Added 块，连续的 Deleted 合成一个 Removed 块。
        var parts = new List<DiffPart>();
        var buffer = new StringBuilder();
        ChangeType? bufferType = null;
        var oldIndex = 0;
        var newIndex = 0;

        void Flush()
        {
            if (buffer.Length == 0) return;
            var added = bufferType == ChangeType.Inserted;
            var removed = bufferType == ChangeType.Deleted;
            parts.Add(new DiffPart(buffer.ToString(), added, removed));
            buffer.Clear();
        }

        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Inserted:
                    if (bufferType != ChangeType.Inserted) Flush();
                    bufferType = ChangeType.Inserted;
                    buffer.Append(newLines[Math.Min(newIndex, newLines.Length - 1)]).Append('\n');
                    newIndex++;
                    break;
                case ChangeType.Deleted:
                    if (bufferType != ChangeType.Deleted) Flush();
                    bufferType = ChangeType.Deleted;
                    buffer.Append(oldLines[Math.Min(oldIndex, oldLines.Length - 1)]).Append('\n');
                    oldIndex++;
                    break;
                case ChangeType.Modified:
                    // 替换：先收尾删除块，再开新增块（与 TS diffLines 的「先 - 后 +」一致）。
                    Flush();
                    bufferType = ChangeType.Deleted;
                    buffer.Append(oldLines[Math.Min(oldIndex, oldLines.Length - 1)]).Append('\n');
                    oldIndex++;
                    Flush();
                    bufferType = ChangeType.Inserted;
                    buffer.Append(newLines[Math.Min(newIndex, newLines.Length - 1)]).Append('\n');
                    newIndex++;
                    break;
                default:
                    Flush();
                    bufferType = ChangeType.Unchanged;
                    buffer.Append(oldLines[Math.Min(oldIndex, oldLines.Length - 1)]).Append('\n');
                    oldIndex++;
                    newIndex++;
                    break;
            }
        }

        Flush();
        return parts;
    }
}
