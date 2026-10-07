using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public class KeybindingsTests
{
    [Fact]
    public void Defaults_ResolveFromTheDefinitionTable()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions);

        Assert.Equal(new[] { "up" }, manager.GetKeys(TuiKeybindingIds.EditorCursorUp));
        Assert.Equal(new[] { "left", "ctrl+b" }, manager.GetKeys(TuiKeybindingIds.EditorCursorLeft));
        Assert.Empty(manager.GetKeys(TuiKeybindingIds.AltScreenLineUp));
        Assert.Equal("Move cursor up", manager.GetDefinition(TuiKeybindingIds.EditorCursorUp).Description);
    }

    [Fact]
    public void Matches_DispatchesThroughTheKeyMatcher()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions);

        Assert.True(manager.Matches("\x1b[A", TuiKeybindingIds.EditorCursorUp));
        Assert.False(manager.Matches("\x1b[B", TuiKeybindingIds.EditorCursorUp));
        Assert.True(manager.Matches("\x1b[B", TuiKeybindingIds.EditorCursorDown));

        // ctrl+b is an alternative binding for cursor-left.
        Assert.True(manager.Matches("\x02", TuiKeybindingIds.EditorCursorLeft));
    }

    [Fact]
    public void UserBindings_OverrideDefaults()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions, new KeybindingsConfig
        {
            [TuiKeybindingIds.SelectUp] = "ctrl+p",
        });

        Assert.Equal(new[] { "ctrl+p" }, manager.GetKeys(TuiKeybindingIds.SelectUp));
        Assert.True(manager.Matches("\x10", TuiKeybindingIds.SelectUp));
        Assert.False(manager.Matches("\x1b[A", TuiKeybindingIds.SelectUp));
    }

    [Fact]
    public void UserBindings_IgnoreUnknownIdsAndDeduplicateKeys()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions, new KeybindingsConfig
        {
            ["not.a.binding"] = "ctrl+p",
            [TuiKeybindingIds.SelectDown] = new[] { "ctrl+n", "ctrl+n", "ctrl+p" },
        });

        Assert.Equal(new[] { "ctrl+n", "ctrl+p" }, manager.GetKeys(TuiKeybindingIds.SelectDown));
        Assert.Empty(manager.GetConflicts());
    }

    [Fact]
    public void Conflicts_ReportKeysClaimedTwice()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions, new KeybindingsConfig
        {
            [TuiKeybindingIds.SelectUp] = "ctrl+p",
            [TuiKeybindingIds.SelectDown] = "ctrl+p",
        });

        var conflicts = manager.GetConflicts();
        Assert.Single(conflicts);
        Assert.Equal("ctrl+p", conflicts[0].Key);
        Assert.Equal(new[] { TuiKeybindingIds.SelectUp, TuiKeybindingIds.SelectDown }, conflicts[0].Keybindings);
    }

    [Fact]
    public void ResolvedBindings_KeepTheSingleVersusArrayShape()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions);
        var resolved = manager.GetResolvedBindings();

        Assert.True(resolved[TuiKeybindingIds.EditorCursorUp].IsSingle);
        Assert.Equal("up", resolved[TuiKeybindingIds.EditorCursorUp].Single);
        Assert.False(resolved[TuiKeybindingIds.EditorCursorLeft].IsSingle);
        Assert.Equal(new[] { "left", "ctrl+b" }, resolved[TuiKeybindingIds.EditorCursorLeft].Values);

        // An unbound action resolves to an empty list.
        Assert.False(resolved[TuiKeybindingIds.AltScreenLineUp].IsSingle);
        Assert.Empty(resolved[TuiKeybindingIds.AltScreenLineUp].Values);
    }

    [Fact]
    public void SetUserBindings_RebuildsAndGetUserBindingsReturnsACopy()
    {
        var manager = new KeybindingsManager(TuiKeybindings.Definitions);
        manager.SetUserBindings(new KeybindingsConfig { [TuiKeybindingIds.SelectConfirm] = "ctrl+enter" });
        Assert.Equal(new[] { "ctrl+enter" }, manager.GetKeys(TuiKeybindingIds.SelectConfirm));

        var copy = manager.GetUserBindings();
        copy[TuiKeybindingIds.SelectConfirm] = "ctrl+space";
        Assert.Equal(new[] { "ctrl+enter" }, manager.GetKeys(TuiKeybindingIds.SelectConfirm));

        manager.SetUserBindings(new KeybindingsConfig());
        Assert.Equal(new[] { "enter" }, manager.GetKeys(TuiKeybindingIds.SelectConfirm));
    }

    [Fact]
    public void GlobalRegistry_LazilyCreatesTheDefaultManager()
    {
        var original = GlobalKeybindings.Get();
        try
        {
            GlobalKeybindings.Set(new KeybindingsManager(TuiKeybindings.Definitions, new KeybindingsConfig
            {
                [TuiKeybindingIds.InputSubmit] = "ctrl+enter",
            }));
            Assert.True(GlobalKeybindings.Get().Matches("\x1b[13;5u", TuiKeybindingIds.InputSubmit));
        }
        finally
        {
            GlobalKeybindings.Set(original);
        }

        Assert.Same(original, GlobalKeybindings.Get());

        // Every id constant has a definition, and there are no stray definitions.
        var ids = typeof(TuiKeybindingIds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(field => (string)field.GetValue(null)!)
            .ToArray();
        Assert.Equal(ids.Length, TuiKeybindings.Definitions.Count);
        Assert.All(ids, id => Assert.True(TuiKeybindings.Definitions.ContainsKey(id), $"missing definition for {id}"));
    }

    [Fact]
    public void KeybindingKeys_ConvertsFromStringAndArray()
    {
        KeybindingKeys single = "up";
        Assert.True(single.IsSingle);
        Assert.Equal("up", single.Single);
        Assert.Throws<InvalidOperationException>(() => new KeybindingKeys(new[] { "a", "b" }).Single);

        KeybindingKeys many = new[] { "a", "b" };
        Assert.False(many.IsSingle);
        Assert.Equal(new[] { "a", "b" }, many.Values);
        Assert.Equal(many, new KeybindingKeys(new[] { "a", "b" }));
        Assert.NotEqual(many, new KeybindingKeys(new[] { "b", "a" }));
    }
}

public class StdinBufferTests
{
    private static (StdinBuffer Buffer, List<string> Data, List<string> Pastes) Create(StdinBufferOptions? options = null)
    {
        var buffer = new StdinBuffer(options);
        var data = new List<string>();
        var pastes = new List<string>();
        var gate = new object();
        buffer.Data += value => { lock (gate) { data.Add(value); } };
        buffer.Paste += value => { lock (gate) { pastes.Add(value); } };
        return (buffer, data, pastes);
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(5);
        }
        return condition();
    }

    [Fact]
    public void CompleteSequence_IsEmittedAsOneEvent()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[A");
        Assert.Equal(new[] { "\x1b[A" }, data);
    }

    [Fact]
    public void PlainCharacters_AreEmittedIndividually()
    {
        var (buffer, data, _) = Create();
        buffer.Process("ab");
        Assert.Equal(new[] { "a", "b" }, data);
    }

    [Fact]
    public void SplitCsiSequence_IsBufferedUntilComplete()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[");
        Assert.Empty(data);
        Assert.Equal("\x1b[", buffer.GetBuffer());

        buffer.Process("A");
        Assert.Equal(new[] { "\x1b[A" }, data);
        Assert.Equal("", buffer.GetBuffer());
    }

    [Fact]
    public void SplitSgrMouseSequence_IsBufferedUntilComplete()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[<35");
        Assert.Empty(data);
        buffer.Process(";20;5m");
        Assert.Equal(new[] { "\x1b[<35;20;5m" }, data);
    }

    [Fact]
    public void OldStyleMouse_NeedsSixBytes()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[M ");
        Assert.Empty(data);
        buffer.Process("!!");
        Assert.Equal(new[] { "\x1b[M !!" }, data);
    }

    [Fact]
    public void WezTermEscapePair_SplitsIntoSeparateSequences()
    {
        var (buffer, data, _) = Create();
        // Raw Escape key press followed by the release's Kitty CSI-u sequence.
        buffer.Process("\x1b\x1b[27;2u");
        Assert.Equal(new[] { "\x1b", "\x1b[27;2u" }, data);
    }

    [Fact]
    public void LoneEscapeMetaPair_StaysTogether()
    {
        var (buffer, data, _) = Create();
        // ESC + 'a' is a complete meta sequence and must not be split.
        buffer.Process("\x1ba");
        Assert.Equal(new[] { "\x1ba" }, data);
    }

    [Fact]
    public void BracketedPaste_EmitsPasteNotData()
    {
        var (buffer, data, pastes) = Create();
        buffer.Process("\x1b[200~hello\x1b[201~");
        Assert.Equal(new[] { "hello" }, pastes);
        Assert.Empty(data);
        Assert.False(buffer.InPasteMode);
    }

    [Fact]
    public void BracketedPaste_HandlesSplitChunksAndTrailingInput()
    {
        var (buffer, data, pastes) = Create();
        buffer.Process("\x1b[200~hel");
        Assert.Empty(pastes);
        Assert.True(buffer.InPasteMode);

        buffer.Process("lo\x1b[201~x");
        Assert.Equal(new[] { "hello" }, pastes);
        Assert.Equal(new[] { "x" }, data);
    }

    [Fact]
    public void TextBeforePasteStart_IsEmittedFirst()
    {
        var (buffer, data, pastes) = Create();
        buffer.Process("a\x1b[200~x\x1b[201~");
        Assert.Equal(new[] { "a" }, data);
        Assert.Equal(new[] { "x" }, pastes);
    }

    [Fact]
    public void EmptyInput_EmitsAnEmptySequence()
    {
        var (buffer, data, _) = Create();
        buffer.Process("");
        Assert.Equal(new[] { "" }, data);
    }

    [Fact]
    public void KittyPrintable_SuppressesTheDuplicatedRawCharacter()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[97u");
        Assert.Equal(new[] { "\x1b[97u" }, data);

        // The terminal may also send the plain character; it is dropped as a duplicate.
        buffer.Process("a");
        Assert.Equal(new[] { "\x1b[97u" }, data);

        // A different character is not suppressed.
        buffer.Process("b");
        Assert.Equal(new[] { "\x1b[97u", "b" }, data);
    }

    [Fact]
    public void LoneEscape_FlushesAfterTheEscapeTimeout()
    {
        var (buffer, data, _) = Create(new StdinBufferOptions { EscapeTimeout = 20, Timeout = 5000 });
        buffer.Process("\x1b");
        Assert.Empty(data);
        Assert.True(WaitFor(() => data.Count > 0));
        Assert.Equal(new[] { "\x1b" }, data);
    }

    [Fact]
    public void IncompleteSequence_FlushesAfterTheSequenceTimeout()
    {
        var (buffer, data, _) = Create(new StdinBufferOptions { Timeout = 20, EscapeTimeout = 5000 });
        buffer.Process("\x1b[");
        Assert.Empty(data);
        Assert.True(WaitFor(() => data.Count > 0));
        Assert.Equal(new[] { "\x1b[" }, data);
    }

    [Fact]
    public void Flush_ReturnsAndClearsThePendingBuffer()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[");
        Assert.Equal(new[] { "\x1b[" }, buffer.Flush());
        Assert.Equal("", buffer.GetBuffer());
        Assert.Empty(buffer.Flush());
        Assert.Empty(data);
    }

    [Fact]
    public void Clear_DropsPendingInput()
    {
        var (buffer, data, _) = Create();
        buffer.Process("\x1b[200~partial");
        Assert.True(buffer.InPasteMode);
        buffer.Clear();
        Assert.False(buffer.InPasteMode);
        Assert.Equal("", buffer.GetBuffer());

        buffer.Process("z");
        Assert.Equal(new[] { "z" }, data);
    }

    [Fact]
    public void HighByteBuffer_BecomesEscapePlusCharacter()
    {
        var (buffer, data, _) = Create();
        // 200 - 128 = 72 = 'H', so this is the meta sequence ESC H.
        buffer.Process(new byte[] { 200 });
        Assert.Equal(new[] { "\x1bH" }, data);
    }

    [Fact]
    public void Utf8Buffer_IsDecoded()
    {
        var (buffer, data, _) = Create();
        buffer.Process(System.Text.Encoding.UTF8.GetBytes("你"));
        Assert.Equal(new[] { "你" }, data);
    }
}
