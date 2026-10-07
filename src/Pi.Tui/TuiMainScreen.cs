using System.Text;

namespace Pi.Tui;

/// <summary>Captured render state for <see cref="TuiMainScreen"/> (used by render-state swapping).</summary>
public sealed class TuiMainScreenRenderState
{
    public required string[] PreviousLines { get; init; }
    public required int PreviousWidth { get; init; }
    public required int PreviousHeight { get; init; }
    public required int CursorRow { get; init; }
    public required int HardwareCursorRow { get; init; }
    public required int MaxLinesRendered { get; init; }
    public required int PreviousViewportTop { get; init; }
}

/// <summary>
/// TUI implementation that renders into the terminal's main screen and scrollback (port of
/// <c>tui-main-screen.ts</c>).
///
/// Deviation from TS: the file-based debug/crash logging (<c>PI_DEBUG_REDRAW</c>, <c>PI_TUI_DEBUG</c>,
/// <c>pi-crash.log</c>) is not ported; the over-wide-line guard still throws.
/// </summary>
public sealed class TuiMainScreen : TuiBase
{
    private const string KittySequencePrefix = "\x1b_G";

    private string[] _previousLines = Array.Empty<string>();
    private HashSet<int> _previousKittyImageIds = new();
    private int _previousWidth;
    private int _previousHeight;
    private int _cursorRow;
    private int _hardwareCursorRow;
    private int _maxLinesRendered;
    private int _previousViewportTop;

    public TuiMainScreen(ITerminal terminal, bool? showHardwareCursor = null) : base(terminal, showHardwareCursor)
    {
    }

    public override TuiMode Mode => TuiMode.Regular;

    private sealed record KittyImageHeader(int[] Ids, int Rows);

    private static KittyImageHeader? ParseKittyImageHeader(string line)
    {
        var sequenceStart = line.IndexOf(KittySequencePrefix, StringComparison.Ordinal);
        if (sequenceStart == -1)
        {
            return null;
        }
        var paramsStart = sequenceStart + KittySequencePrefix.Length;
        var paramsEnd = line.IndexOf(';', paramsStart);
        if (paramsEnd == -1)
        {
            return null;
        }

        var ids = new List<int>();
        var rows = 1;
        foreach (var param in line.Substring(paramsStart, paramsEnd - paramsStart).Split(','))
        {
            var kv = param.Split('=', 2);
            if (kv.Length < 2)
            {
                continue;
            }
            if (!int.TryParse(kv[1], out var numberValue) || numberValue <= 0)
            {
                continue;
            }
            if (kv[0] == "i")
            {
                ids.Add(numberValue);
            }
            else if (kv[0] == "r")
            {
                rows = numberValue;
            }
        }
        return new KittyImageHeader(ids.ToArray(), rows);
    }

    private static int[] ExtractKittyImageIds(string line) => ParseKittyImageHeader(line)?.Ids ?? Array.Empty<int>();

    private static int ExtractKittyImageRows(string line) => ParseKittyImageHeader(line)?.Rows ?? 1;

    private static bool IsTermuxSession() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TERMUX_VERSION"));

    public TuiMainScreenRenderState CaptureRenderState() => new()
    {
        PreviousLines = _previousLines.ToArray(),
        PreviousWidth = _previousWidth,
        PreviousHeight = _previousHeight,
        CursorRow = _cursorRow,
        HardwareCursorRow = _hardwareCursorRow,
        MaxLinesRendered = _maxLinesRendered,
        PreviousViewportTop = _previousViewportTop,
    };

    public void RestoreRenderState(TuiMainScreenRenderState state)
    {
        _previousLines = state.PreviousLines.Select(line => TerminalImage.IsImageLine(line) ? "" : line).ToArray();
        _previousKittyImageIds = new HashSet<int>();
        _previousWidth = state.PreviousWidth;
        _previousHeight = state.PreviousHeight;
        _cursorRow = state.CursorRow;
        _hardwareCursorRow = state.HardwareCursorRow;
        _maxLinesRendered = state.MaxLinesRendered;
        _previousViewportTop = state.PreviousViewportTop;
    }

    protected override void ResetRenderState()
    {
        _previousLines = Array.Empty<string>();
        _previousWidth = -1;
        _previousHeight = -1;
        _cursorRow = 0;
        _hardwareCursorRow = 0;
        _maxLinesRendered = 0;
        _previousViewportTop = 0;
    }

    protected override void BeforeTerminalStop(TuiStopOptions options)
    {
        if (options.PreserveScreen || _previousLines.Length == 0)
        {
            return;
        }
        Terminal.Write(" ");
        var targetRow = _previousLines.Length;
        var lineDiff = targetRow - _hardwareCursorRow;
        if (lineDiff > 0)
        {
            Terminal.Write($"\x1b[{lineDiff}B");
        }
        else if (lineDiff < 0)
        {
            Terminal.Write($"\x1b[{-lineDiff}A");
        }
        Terminal.Write("\r\n");
    }

    private HashSet<int> CollectKittyImageIds(string[] lines)
    {
        var ids = new HashSet<int>();
        foreach (var line in lines)
        {
            foreach (var id in ExtractKittyImageIds(line))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private string DeleteKittyImages(IEnumerable<int> ids)
    {
        var buffer = new StringBuilder();
        foreach (var id in ids)
        {
            buffer.Append(TerminalImage.DeleteKittyImage(id));
        }
        return buffer.ToString();
    }

    private int GetKittyImageReservedRows(string[] lines, int index, int? maxIndex = null)
    {
        var rows = ExtractKittyImageRows(index < lines.Length ? lines[index] : "");
        if (rows <= 1)
        {
            return 1;
        }

        var maxRows = Math.Min(Math.Min(rows, (maxIndex ?? lines.Length - 1) - index + 1), lines.Length - index);
        var reservedRows = 1;
        while (reservedRows < maxRows)
        {
            var line = index + reservedRows < lines.Length ? lines[index + reservedRows] : "";
            if (TerminalImage.IsImageLine(line) || UnicodeWidth.VisibleWidth(line) > 0)
            {
                break;
            }
            reservedRows++;
        }
        return reservedRows;
    }

    private (int FirstChanged, int LastChanged) ExpandChangedRangeForKittyImages(int firstChanged, int lastChanged, string[] newLines)
    {
        var expandedFirst = firstChanged;
        var expandedLast = lastChanged;

        void ExpandForLines(string[] lines)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (ExtractKittyImageIds(lines[i]).Length == 0)
                {
                    continue;
                }
                var blockEnd = i + GetKittyImageReservedRows(lines, i) - 1;
                if (i >= firstChanged || (i <= lastChanged && blockEnd >= firstChanged))
                {
                    expandedFirst = Math.Min(expandedFirst, i);
                    expandedLast = Math.Max(expandedLast, blockEnd);
                }
            }
        }

        ExpandForLines(_previousLines);
        ExpandForLines(newLines);
        return (expandedFirst, expandedLast);
    }

    private string DeleteChangedKittyImages(int firstChanged, int lastChanged)
    {
        if (firstChanged < 0 || lastChanged < firstChanged)
        {
            return "";
        }

        var ids = new HashSet<int>();
        var maxLine = Math.Min(lastChanged, _previousLines.Length - 1);
        for (var i = firstChanged; i <= maxLine; i++)
        {
            foreach (var id in ExtractKittyImageIds(i < _previousLines.Length ? _previousLines[i] : ""))
            {
                ids.Add(id);
            }
        }
        return DeleteKittyImages(ids);
    }

    protected override void DoRender()
    {
        if (Stopped)
        {
            return;
        }
        var width = Terminal.Columns;
        var height = Terminal.Rows;
        var widthChanged = _previousWidth != 0 && _previousWidth != width;
        var heightChanged = _previousHeight != 0 && _previousHeight != height;
        var previousBufferLength = _previousHeight > 0 ? _previousViewportTop + _previousHeight : height;
        var prevViewportTop = heightChanged ? Math.Max(0, previousBufferLength - height) : _previousViewportTop;
        var viewportTop = prevViewportTop;
        var hardwareCursorRow = _hardwareCursorRow;

        int ComputeLineDiff(int targetRow)
        {
            var currentScreenRow = hardwareCursorRow - prevViewportTop;
            var targetScreenRow = targetRow - viewportTop;
            return targetScreenRow - currentScreenRow;
        }

        var newLines = Render(width);

        if (HasOverlayEntries)
        {
            newLines = CompositeOverlays(newLines, width, height);
        }

        var cursorPos = ExtractCursorPosition(newLines, height);
        newLines = ApplyLineResets(newLines);

        void FullRender(bool clear)
        {
            FullRedrawCount++;
            var buffer = new StringBuilder("\x1b[?2026h");
            if (clear)
            {
                buffer.Append(DeleteKittyImages(_previousKittyImageIds));
                buffer.Append("\x1b[2J\x1b[H\x1b[3J");
            }
            for (var i = 0; i < newLines.Length; i++)
            {
                if (i > 0)
                {
                    buffer.Append("\r\n");
                }
                var line = newLines[i];
                var isImage = TerminalImage.IsImageLine(line);
                var imageReservedRows = isImage ? GetKittyImageReservedRows(newLines, i) : 1;
                if (imageReservedRows > 1 && imageReservedRows <= height)
                {
                    for (var row = 1; row < imageReservedRows; row++)
                    {
                        buffer.Append("\r\n");
                    }
                    buffer.Append($"\x1b[{imageReservedRows - 1}A");
                    buffer.Append(line);
                    buffer.Append($"\x1b[{imageReservedRows - 1}B");
                    i += imageReservedRows - 1;
                    continue;
                }
                buffer.Append(line);
            }
            buffer.Append("\x1b[?2026l");
            Terminal.Write(buffer.ToString());
            _cursorRow = Math.Max(0, newLines.Length - 1);
            _hardwareCursorRow = _cursorRow;
            _maxLinesRendered = clear ? newLines.Length : Math.Max(_maxLinesRendered, newLines.Length);
            var bufferLength = Math.Max(height, newLines.Length);
            _previousViewportTop = Math.Max(0, bufferLength - height);
            PositionHardwareCursor(cursorPos, newLines.Length);
            _previousLines = newLines;
            _previousKittyImageIds = CollectKittyImageIds(newLines);
            _previousWidth = width;
            _previousHeight = height;
        }

        if (_previousLines.Length == 0 && !widthChanged && !heightChanged)
        {
            FullRender(false);
            return;
        }

        if (widthChanged)
        {
            FullRender(true);
            return;
        }

        if (heightChanged && !IsTermuxSession())
        {
            FullRender(true);
            return;
        }

        if (GetClearOnShrink() && newLines.Length < _maxLinesRendered && !HasOverlayEntries)
        {
            FullRender(true);
            return;
        }

        var firstChanged = -1;
        var lastChanged = -1;
        var maxLines = Math.Max(newLines.Length, _previousLines.Length);
        for (var i = 0; i < maxLines; i++)
        {
            var oldLine = i < _previousLines.Length ? _previousLines[i] : "";
            var newLine = i < newLines.Length ? newLines[i] : "";
            if (oldLine != newLine)
            {
                if (firstChanged == -1)
                {
                    firstChanged = i;
                }
                lastChanged = i;
            }
        }
        var appendedLines = newLines.Length > _previousLines.Length;
        if (appendedLines)
        {
            if (firstChanged == -1)
            {
                firstChanged = _previousLines.Length;
            }
            lastChanged = newLines.Length - 1;
        }
        if (firstChanged != -1)
        {
            var expanded = ExpandChangedRangeForKittyImages(firstChanged, lastChanged, newLines);
            firstChanged = expanded.FirstChanged;
            lastChanged = expanded.LastChanged;
        }
        var appendStart = appendedLines && firstChanged == _previousLines.Length && firstChanged > 0;

        if (firstChanged == -1)
        {
            PositionHardwareCursor(cursorPos, newLines.Length);
            _previousViewportTop = prevViewportTop;
            _previousHeight = height;
            return;
        }

        if (firstChanged >= newLines.Length)
        {
            if (_previousLines.Length > newLines.Length)
            {
                var buffer = new StringBuilder("\x1b[?2026h");
                buffer.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
                var targetRow = Math.Max(0, newLines.Length - 1);
                if (targetRow < prevViewportTop)
                {
                    FullRender(true);
                    return;
                }
                var lineDiff = ComputeLineDiff(targetRow);
                if (lineDiff > 0)
                {
                    buffer.Append($"\x1b[{lineDiff}B");
                }
                else if (lineDiff < 0)
                {
                    buffer.Append($"\x1b[{-lineDiff}A");
                }
                buffer.Append('\r');
                var extraLines = _previousLines.Length - newLines.Length;
                if (extraLines > height)
                {
                    FullRender(true);
                    return;
                }
                var clearStartOffset = newLines.Length == 0 ? 0 : 1;
                if (extraLines > 0 && clearStartOffset > 0)
                {
                    buffer.Append($"\x1b[{clearStartOffset}B");
                }
                for (var i = 0; i < extraLines; i++)
                {
                    buffer.Append("\r\x1b[2K");
                    if (i < extraLines - 1)
                    {
                        buffer.Append("\x1b[1B");
                    }
                }
                var moveBack = Math.Max(0, extraLines - 1 + clearStartOffset);
                if (moveBack > 0)
                {
                    buffer.Append($"\x1b[{moveBack}A");
                }
                buffer.Append("\x1b[?2026l");
                Terminal.Write(buffer.ToString());
                _cursorRow = targetRow;
                _hardwareCursorRow = targetRow;
            }
            PositionHardwareCursor(cursorPos, newLines.Length);
            _previousLines = newLines;
            _previousKittyImageIds = CollectKittyImageIds(newLines);
            _previousWidth = width;
            _previousHeight = height;
            _previousViewportTop = prevViewportTop;
            return;
        }

        if (firstChanged < prevViewportTop)
        {
            FullRender(true);
            return;
        }

        var output = new StringBuilder("\x1b[?2026h");
        output.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
        var prevViewportBottom = prevViewportTop + height - 1;
        var moveTargetRow = appendStart ? firstChanged - 1 : firstChanged;
        if (moveTargetRow > prevViewportBottom)
        {
            var currentScreenRow = Math.Max(0, Math.Min(height - 1, hardwareCursorRow - prevViewportTop));
            var moveToBottom = height - 1 - currentScreenRow;
            if (moveToBottom > 0)
            {
                output.Append($"\x1b[{moveToBottom}B");
            }
            var scroll = moveTargetRow - prevViewportBottom;
            output.Append(string.Concat(Enumerable.Repeat("\r\n", scroll)));
            prevViewportTop += scroll;
            viewportTop += scroll;
            hardwareCursorRow = moveTargetRow;
        }

        var moveDiff = ComputeLineDiff(moveTargetRow);
        if (moveDiff > 0)
        {
            output.Append($"\x1b[{moveDiff}B");
        }
        else if (moveDiff < 0)
        {
            output.Append($"\x1b[{-moveDiff}A");
        }

        output.Append(appendStart ? "\r\n" : "\r");

        var renderEnd = Math.Min(lastChanged, newLines.Length - 1);
        for (var i = firstChanged; i <= renderEnd; i++)
        {
            if (i > firstChanged)
            {
                output.Append("\r\n");
            }
            var line = newLines[i];
            var isImage = TerminalImage.IsImageLine(line);
            var imageReservedRows = isImage ? GetKittyImageReservedRows(newLines, i, renderEnd) : 1;
            if (imageReservedRows > 1)
            {
                var imageStartScreenRow = i - viewportTop;
                if (imageStartScreenRow < 0 || imageStartScreenRow + imageReservedRows > height)
                {
                    FullRender(true);
                    return;
                }

                output.Append("\x1b[2K");
                for (var row = 1; row < imageReservedRows; row++)
                {
                    output.Append("\r\n\x1b[2K");
                }
                output.Append($"\x1b[{imageReservedRows - 1}A");
                output.Append(line);
                output.Append($"\x1b[{imageReservedRows - 1}B");
                i += imageReservedRows - 1;
                continue;
            }

            output.Append("\x1b[2K");
            if (!isImage && UnicodeWidth.VisibleWidth(line) > width)
            {
                Stop();
                throw new InvalidOperationException(
                    $"Rendered line {i} exceeds terminal width ({UnicodeWidth.VisibleWidth(line)} > {width}).\n\n" +
                    "This is likely caused by a custom TUI component not truncating its output.\n" +
                    "Use VisibleWidth() to measure and TruncateToWidth() to truncate lines.");
            }
            output.Append(line);
        }

        var finalCursorRow = renderEnd;

        if (_previousLines.Length > newLines.Length)
        {
            if (renderEnd < newLines.Length - 1)
            {
                var moveDown = newLines.Length - 1 - renderEnd;
                output.Append($"\x1b[{moveDown}B");
                finalCursorRow = newLines.Length - 1;
            }
            var extraLines = _previousLines.Length - newLines.Length;
            for (var i = newLines.Length; i < _previousLines.Length; i++)
            {
                output.Append("\r\n\x1b[2K");
            }
            output.Append($"\x1b[{extraLines}A");
        }

        output.Append("\x1b[?2026l");
        Terminal.Write(output.ToString());

        _cursorRow = Math.Max(0, newLines.Length - 1);
        _hardwareCursorRow = finalCursorRow;
        _maxLinesRendered = Math.Max(_maxLinesRendered, newLines.Length);
        _previousViewportTop = Math.Max(prevViewportTop, finalCursorRow - height + 1);

        PositionHardwareCursor(cursorPos, newLines.Length);

        _previousLines = newLines;
        _previousKittyImageIds = CollectKittyImageIds(newLines);
        _previousWidth = width;
        _previousHeight = height;
    }

    private void PositionHardwareCursor((int Row, int Col)? cursorPos, int totalLines)
    {
        if (cursorPos is not { } pos || totalLines <= 0)
        {
            Terminal.HideCursor();
            return;
        }

        var targetRow = Math.Max(0, Math.Min(pos.Row, totalLines - 1));
        var targetCol = Math.Max(0, pos.Col);

        var rowDelta = targetRow - _hardwareCursorRow;
        var buffer = new StringBuilder();
        if (rowDelta > 0)
        {
            buffer.Append($"\x1b[{rowDelta}B");
        }
        else if (rowDelta < 0)
        {
            buffer.Append($"\x1b[{-rowDelta}A");
        }
        buffer.Append($"\x1b[{targetCol + 1}G");

        if (buffer.Length > 0)
        {
            Terminal.Write(buffer.ToString());
        }

        _hardwareCursorRow = targetRow;
        if (GetShowHardwareCursor())
        {
            Terminal.ShowCursor();
        }
        else
        {
            Terminal.HideCursor();
        }
    }
}
