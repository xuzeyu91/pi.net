using Pi.Tui;
using Xunit;

namespace Pi.Tui.Tests;

public class KeysAndUtilTests
{
    [Fact]
    public void MatchesKey_HandlesLegacyControlCharacters()
    {
        Assert.True(Keys.MatchesKey("\x03", "ctrl+c"));
        Assert.False(Keys.MatchesKey("\x03", "ctrl+x"));
    }

    [Fact]
    public void MatchesKey_HandlesSpecialKeys()
    {
        Assert.True(Keys.MatchesKey("\x1b", "escape"));
        Assert.True(Keys.MatchesKey("\t", "tab"));
        Assert.True(Keys.MatchesKey("\x1b[Z", "shift+tab"));
        Assert.True(Keys.MatchesKey("\r", "enter"));
        Assert.True(Keys.MatchesKey("\x7f", "backspace"));
        Assert.True(Keys.MatchesKey(" ", "space"));
    }

    [Fact]
    public void MatchesKey_HandlesArrowKeys()
    {
        Assert.True(Keys.MatchesKey("\x1b[A", "up"));
        Assert.True(Keys.MatchesKey("\x1b[B", "down"));
        Assert.True(Keys.MatchesKey("\x1b[C", "right"));
        Assert.True(Keys.MatchesKey("\x1b[D", "left"));
        Assert.False(Keys.MatchesKey("\x1b[A", "down"));
    }

    [Fact]
    public void MatchesKey_HandlesKittySequences()
    {
        // Ctrl+C via Kitty CSI-u: codepoint 99, modifier 5 (1 + ctrl).
        Assert.True(Keys.MatchesKey("\x1b[99;5u", "ctrl+c"));
        // Shift+Tab via Kitty CSI-u: codepoint 9, modifier 2.
        Assert.True(Keys.MatchesKey("\x1b[9;2u", "shift+tab"));
    }

    [Fact]
    public void ParseKey_RecognisesCommonInput()
    {
        Assert.Equal("up", Keys.ParseKey("\x1b[A"));
        Assert.Equal("ctrl+c", Keys.ParseKey("\x03"));
        Assert.Equal("tab", Keys.ParseKey("\t"));
        Assert.Equal("a", Keys.ParseKey("a"));
        Assert.Equal("shift+tab", Keys.ParseKey("\x1b[Z"));
        // parseKey ignores the kitty event-type sub-parameter: a release still yields the key id
        // (release/repeat filtering happens via isKeyRelease / isKeyRepeat).
        Assert.Equal("ctrl+c", Keys.ParseKey("\x1b[99;5:3u"));
        Assert.Null(Keys.ParseKey("hello"));
    }

    [Fact]
    public void IsKeyRelease_And_IsKeyRepeat()
    {
        Assert.True(Keys.IsKeyRelease("\x1b[97;1:3u"));
        Assert.False(Keys.IsKeyRelease("\x1b[97;1u"));
        Assert.True(Keys.IsKeyRepeat("\x1b[97;1:2u"));
        // Bracketed paste content is never treated as release/repeat.
        Assert.False(Keys.IsKeyRelease("\x1b[200~90:62:3F\x1b[201~"));
    }

    [Fact]
    public void DecodePrintableKey_DecodesKittyAndModifyOtherKeys()
    {
        Assert.Equal("a", Keys.DecodePrintableKey("\x1b[97u"));
        Assert.Equal("A", Keys.DecodePrintableKey("\x1b[97:65;2u"));
        Assert.Equal("a", Keys.DecodePrintableKey("\x1b[27;1;97~"));
        Assert.Null(Keys.DecodePrintableKey("\x1b[97;5u"));
    }

    [Fact]
    public void KeyHelpers_BuildIdentifiers()
    {
        Assert.Equal("ctrl+c", Key.Ctrl("c"));
        Assert.Equal("ctrl+shift+p", Key.CtrlShift("p"));
        Assert.Equal("escape", Key.Escape);
    }

    [Fact]
    public void FuzzyMatch_RewardsConsecutiveAndBoundaryMatches()
    {
        Assert.True(Fuzzy.Match("hw", "hello world").Matches);
        Assert.False(Fuzzy.Match("xyz", "hello").Matches);
        Assert.True(Fuzzy.Match("hello world", "hello world").Score < Fuzzy.Match("hlo", "hello world").Score);
    }

    [Fact]
    public void FuzzyFilter_SortsBestFirstAndSupportsTokens()
    {
        var items = new[] { "hello world", "hello", "world hello" };
        var filtered = Fuzzy.Filter(items, "hello", x => x);
        Assert.Equal(3, filtered.Count);

        var tokens = Fuzzy.Filter(items, "hello world", x => x);
        Assert.Contains("hello world", tokens);
        Assert.Equal(3, Fuzzy.Filter(items, "   ", x => x).Count);
    }

    [Fact]
    public void KillRing_AccumulatesAndRotates()
    {
        var ring = new KillRing();
        ring.Push("a", new KillRingPushOptions { Prepend = false });
        ring.Push("b", new KillRingPushOptions { Prepend = false, Accumulate = true });
        Assert.Equal("ab", ring.Peek());
        ring.Push("c", new KillRingPushOptions { Prepend = true, Accumulate = true });
        Assert.Equal("cab", ring.Peek());
        Assert.Equal(1, ring.Length);

        ring.Push("d", new KillRingPushOptions { Prepend = false });
        Assert.Equal(2, ring.Length);
        Assert.Equal("d", ring.Peek());
        // rotate() moves the newest entry to the front so the next peek yields the previous kill
        // (this is the yank-pop cycle).
        ring.Rotate();
        Assert.Equal("cab", ring.Peek());
    }

    [Fact]
    public void UndoStack_ClonesOnPush()
    {
        var stack = new UndoStack<List<int>>(state => new List<int>(state));
        var state = new List<int> { 1, 2 };
        stack.Push(state);
        state.Add(3);
        var popped = stack.Pop();
        Assert.NotNull(popped);
        Assert.Equal(new[] { 1, 2 }, popped!);
        Assert.Equal(0, stack.Length);
    }

    [Fact]
    public void WordNavigation_MovesOverWords()
    {
        Assert.Equal(6, WordNavigation.FindWordBackward("hello world", 11));
        Assert.Equal(5, WordNavigation.FindWordForward("hello world", 0));
        Assert.Equal(11, WordNavigation.FindWordForward("hello world", 5));
        Assert.Equal(0, WordNavigation.FindWordBackward("hello", 0));
        Assert.Equal(5, WordNavigation.FindWordForward("hello", 10));
    }

    /// <summary>
    /// Pins the documented approximation for CJK text (T22).
    /// </summary>
    /// <remarks>
    /// The reference uses <c>Intl.Segmenter</c> with word granularity, which for Han/Kana/Thai runs
    /// delegates to ICU's <em>dictionary</em> word breaker: it segments <c>"你好世界。你好，世界"</c> as
    /// <c>你好|世界|。|你好|，|世界</c>. .NET exposes no equivalent API, so this port groups
    /// consecutive runes of the same class and therefore treats the whole <c>你好世界</c> run as one
    /// word. ASCII behaviour is unaffected because both sides then split at the punctuation characters
    /// inside a segment (see the upstream Ctrl+W / Alt+D punctuation tests, which pass).
    /// </remarks>
    [Fact]
    public void WordNavigation_ApproximatesIcuDictionarySegmentation()
    {
        const string text = "你好世界。你好，世界"; // 10 chars: 你好世界 | 。 | 你好 | ， | 世界

        // Agrees with the reference: every step that stops at a punctuation boundary or inside a
        // two-character dictionary word.
        Assert.Equal(8, WordNavigation.FindWordBackward(text, 10)); // ICU: 8, skips 世界
        Assert.Equal(7, WordNavigation.FindWordBackward(text, 8)); // ICU: 7, skips ，
        Assert.Equal(5, WordNavigation.FindWordBackward(text, 6)); // ICU: 5, skips 。
        Assert.Equal(4, WordNavigation.FindWordBackward(text, 5)); // ICU: 4, skips 。
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 2)); // ICU: 0, skips 你好
        Assert.Equal(4, WordNavigation.FindWordForward(text, 2)); // ICU: 4, skips 世界
        Assert.Equal(5, WordNavigation.FindWordForward(text, 4)); // ICU: 5, skips 。
        Assert.Equal(7, WordNavigation.FindWordForward(text, 5)); // ICU: 7, skips 你好

        // Diverges: inside a Han run the port sees one word, ICU sees 你好|世界.
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 4)); // ICU: 2
        Assert.Equal(0, WordNavigation.FindWordBackward(text, 3)); // ICU: 2
        Assert.Equal(4, WordNavigation.FindWordForward(text, 0)); // ICU: 2

        // Scripts without dictionary segmentation agree with the reference.
        Assert.Equal(4, WordNavigation.FindWordForward("가나다라", 0));
        Assert.Equal(0, WordNavigation.FindWordBackward("가나다라", 4));

        // Classification uses the JavaScript whitespace set (T15): U+FEFF is whitespace in JS but not
        // in .NET, U+0085 the other way round.
        Assert.Equal(0, WordNavigation.FindWordBackward("abc\ufeff", 4));
        Assert.Equal(3, WordNavigation.FindWordBackward("abc\u0085", 4));
        Assert.Equal(1, WordNavigation.FindWordForward("\u0085abc", 0));
    }

    [Fact]
    public void MouseDispatch_RetargetsAndClassifiesResults()
    {
        var target = new TuiMouseDispatchTarget
        {
            Component = new Container(),
            OriginX = 10,
            OriginY = 4,
            Width = 20,
            Height = 3,
        };
        var retargeted = MouseDispatch.Retarget(
            new TuiMouseEvent { X = 1, Y = 1, ScreenX = 12, ScreenY = 7, Width = 80, Height = 24 },
            target);
        Assert.Equal(2, retargeted.X);
        Assert.Equal(3, retargeted.Y);
        Assert.Equal(20, retargeted.Width);
        Assert.Equal(3, retargeted.Height);
        Assert.Equal(12, retargeted.ScreenX);

        // No interest -> no dispatch result.
        Assert.Null(MouseDispatch.Dispatch(new Container(), new TuiMouseEvent { Width = 10, Height = 1 }));

        // An interested component gets a target with the origin transform.
        var interested = new ProbeComponent { MouseResult = new TuiMouseEventResult { Handled = true } };
        var dispatched = MouseDispatch.Dispatch(interested, new TuiMouseEvent
        {
            X = 3,
            Y = 1,
            ScreenX = 13,
            ScreenY = 5,
            Width = 20,
            Height = 4,
        });
        Assert.NotNull(dispatched);
        Assert.True(dispatched.Handled);
        Assert.Equal(10, dispatched.Target.OriginX);
        Assert.Equal(4, dispatched.Target.OriginY);
        Assert.Same(interested, dispatched.Target.Component);
        Assert.Null(dispatched.FocusTarget);

        // focus implies handled, and a component that overrides HandleInput becomes the focus target.
        var focusable = new ProbeComponent { MouseResult = new TuiMouseEventResult { Focus = true } };
        var focused = MouseDispatch.Dispatch(focusable, new TuiMouseEvent { Width = 5, Height = 1 });
        Assert.NotNull(focused);
        Assert.True(focused.Handled);
        Assert.Same(focusable, focused.FocusTarget);
    }

    private sealed class ProbeComponent : IComponent
    {
        public TuiMouseEventResult? MouseResult { get; set; }

        public string[] Render(int width) => new[] { "" };

        public void HandleInput(string data)
        {
        }

        public TuiMouseEventResult? HandleMouse(TuiMouseEvent @event) => MouseResult;
    }
}
