using System.Text.RegularExpressions;

namespace Pi.Tui;

/// <summary>Key event types reported by the Kitty keyboard protocol (flag 2).</summary>
public enum KeyEventType
{
    Press,
    Repeat,
    Release,
}

/// <summary>
/// Port of <c>keys.ts</c>: legacy terminal sequences and Kitty keyboard protocol parsing,
/// <c>matchesKey</c> / <c>parseKey</c>, and printable-key decoding.
/// </summary>
public static partial class Keys
{
    private static bool _kittyProtocolActive;

    public static void SetKittyProtocolActive(bool active) => _kittyProtocolActive = active;

    public static bool IsKittyProtocolActive() => _kittyProtocolActive;

    // Modifier bitmask (Kitty protocol).
    private const int ModShift = 1;
    private const int ModAlt = 2;
    private const int ModCtrl = 4;
    private const int ModSuper = 8;
    private const int LockMask = 64 + 128; // Caps Lock + Num Lock

    private const int CpEscape = 27;
    private const int CpTab = 9;
    private const int CpEnter = 13;
    private const int CpSpace = 32;
    private const int CpBackspace = 127;
    private const int CpKpEnter = 57414;

    private const int CpUp = -1;
    private const int CpDown = -2;
    private const int CpRight = -3;
    private const int CpLeft = -4;

    private const int CpDelete = -10;
    private const int CpInsert = -11;
    private const int CpPageUp = -12;
    private const int CpPageDown = -13;
    private const int CpHome = -14;
    private const int CpEnd = -15;

    private static readonly HashSet<string> SymbolKeys = new()
    {
        "`", "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/", "!", "@", "#", "$", "%", "^",
        "&", "*", "(", ")", "_", "+", "|", "~", "{", "}", ":", "<", ">", "?",
    };

    private static readonly Dictionary<int, int> KittyFunctionalEquivalents = new()
    {
        [57399] = 48, [57400] = 49, [57401] = 50, [57402] = 51, [57403] = 52, [57404] = 53,
        [57405] = 54, [57406] = 55, [57407] = 56, [57408] = 57, [57409] = 46, [57410] = 47,
        [57411] = 42, [57412] = 45, [57413] = 43, [57415] = 61, [57416] = 44,
        [57417] = CpLeft, [57418] = CpRight, [57419] = CpUp, [57420] = CpDown,
        [57421] = CpPageUp, [57422] = CpPageDown, [57423] = CpHome, [57424] = CpEnd,
        [57425] = CpInsert, [57426] = CpDelete,
    };

    private static readonly Dictionary<string, string[]> LegacyKeySequences = new()
    {
        ["up"] = new[] { "\x1b[A", "\x1bOA" },
        ["down"] = new[] { "\x1b[B", "\x1bOB" },
        ["right"] = new[] { "\x1b[C", "\x1bOC" },
        ["left"] = new[] { "\x1b[D", "\x1bOD" },
        ["home"] = new[] { "\x1b[H", "\x1bOH", "\x1b[1~", "\x1b[7~" },
        ["end"] = new[] { "\x1b[F", "\x1bOF", "\x1b[4~", "\x1b[8~" },
        ["insert"] = new[] { "\x1b[2~" },
        ["delete"] = new[] { "\x1b[3~" },
        ["pageUp"] = new[] { "\x1b[5~", "\x1b[[5~" },
        ["pageDown"] = new[] { "\x1b[6~", "\x1b[[6~" },
        ["clear"] = new[] { "\x1b[E", "\x1bOE" },
        ["f1"] = new[] { "\x1bOP", "\x1b[11~", "\x1b[[A" },
        ["f2"] = new[] { "\x1bOQ", "\x1b[12~", "\x1b[[B" },
        ["f3"] = new[] { "\x1bOR", "\x1b[13~", "\x1b[[C" },
        ["f4"] = new[] { "\x1bOS", "\x1b[14~", "\x1b[[D" },
        ["f5"] = new[] { "\x1b[15~", "\x1b[[E" },
        ["f6"] = new[] { "\x1b[17~" },
        ["f7"] = new[] { "\x1b[18~" },
        ["f8"] = new[] { "\x1b[19~" },
        ["f9"] = new[] { "\x1b[20~" },
        ["f10"] = new[] { "\x1b[21~" },
        ["f11"] = new[] { "\x1b[23~" },
        ["f12"] = new[] { "\x1b[24~" },
    };

    private static readonly Dictionary<string, string[]> LegacyShiftSequences = new()
    {
        ["up"] = new[] { "\x1b[a" },
        ["down"] = new[] { "\x1b[b" },
        ["right"] = new[] { "\x1b[c" },
        ["left"] = new[] { "\x1b[d" },
        ["clear"] = new[] { "\x1b[e" },
        ["insert"] = new[] { "\x1b[2$" },
        ["delete"] = new[] { "\x1b[3$" },
        ["pageUp"] = new[] { "\x1b[5$" },
        ["pageDown"] = new[] { "\x1b[6$" },
        ["home"] = new[] { "\x1b[7$" },
        ["end"] = new[] { "\x1b[8$" },
    };

    private static readonly Dictionary<string, string[]> LegacyCtrlSequences = new()
    {
        ["up"] = new[] { "\x1bOa" },
        ["down"] = new[] { "\x1bOb" },
        ["right"] = new[] { "\x1bOc" },
        ["left"] = new[] { "\x1bOd" },
        ["clear"] = new[] { "\x1bOe" },
        ["insert"] = new[] { "\x1b[2^" },
        ["delete"] = new[] { "\x1b[3^" },
        ["pageUp"] = new[] { "\x1b[5^" },
        ["pageDown"] = new[] { "\x1b[6^" },
        ["home"] = new[] { "\x1b[7^" },
        ["end"] = new[] { "\x1b[8^" },
    };

    private static readonly Dictionary<string, string> LegacySequenceKeyIds = new()
    {
        ["\x1bOA"] = "up", ["\x1bOB"] = "down", ["\x1bOC"] = "right", ["\x1bOD"] = "left",
        ["\x1bOH"] = "home", ["\x1bOF"] = "end", ["\x1b[E"] = "clear", ["\x1bOE"] = "clear",
        ["\x1bOe"] = "ctrl+clear", ["\x1b[e"] = "shift+clear", ["\x1b[2~"] = "insert",
        ["\x1b[2$"] = "shift+insert", ["\x1b[2^"] = "ctrl+insert", ["\x1b[3$"] = "shift+delete",
        ["\x1b[3^"] = "ctrl+delete", ["\x1b[[5~"] = "pageUp", ["\x1b[[6~"] = "pageDown",
        ["\x1b[a"] = "shift+up", ["\x1b[b"] = "shift+down", ["\x1b[c"] = "shift+right",
        ["\x1b[d"] = "shift+left", ["\x1bOa"] = "ctrl+up", ["\x1bOb"] = "ctrl+down",
        ["\x1bOc"] = "ctrl+right", ["\x1bOd"] = "ctrl+left", ["\x1b[5$"] = "shift+pageUp",
        ["\x1b[6$"] = "shift+pageDown", ["\x1b[7$"] = "shift+home", ["\x1b[8$"] = "shift+end",
        ["\x1b[5^"] = "ctrl+pageUp", ["\x1b[6^"] = "ctrl+pageDown", ["\x1b[7^"] = "ctrl+home",
        ["\x1b[8^"] = "ctrl+end", ["\x1bOP"] = "f1", ["\x1bOQ"] = "f2", ["\x1bOR"] = "f3",
        ["\x1bOS"] = "f4", ["\x1b[11~"] = "f1", ["\x1b[12~"] = "f2", ["\x1b[13~"] = "f3",
        ["\x1b[14~"] = "f4", ["\x1b[[A"] = "f1", ["\x1b[[B"] = "f2", ["\x1b[[C"] = "f3",
        ["\x1b[[D"] = "f4", ["\x1b[[E"] = "f5", ["\x1b[15~"] = "f5", ["\x1b[17~"] = "f6",
        ["\x1b[18~"] = "f7", ["\x1b[19~"] = "f8", ["\x1b[20~"] = "f9", ["\x1b[21~"] = "f10",
        ["\x1b[23~"] = "f11", ["\x1b[24~"] = "f12", ["\x1bb"] = "alt+left", ["\x1bf"] = "alt+right",
        ["\x1bp"] = "alt+up", ["\x1bn"] = "alt+down",
    };

    private static readonly string[] FunctionKeys = { "f1", "f2", "f3", "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12" };

    [GeneratedRegex(@"^\x1b\[([0-9]+)(?::([0-9]*))?(?::([0-9]+))?(?:;([0-9]+))?(?::([0-9]+))?u$")]
    private static partial Regex CsiURegex();

    [GeneratedRegex(@"^\x1b\[1;([0-9]+)(?::([0-9]+))?([ABCD])$")]
    private static partial Regex ArrowRegex();

    [GeneratedRegex(@"^\x1b\[([0-9]+)(?:;([0-9]+))?(?::([0-9]+))?~$")]
    private static partial Regex FuncRegex();

    [GeneratedRegex(@"^\x1b\[1;([0-9]+)(?::([0-9]+))?([HF])$")]
    private static partial Regex HomeEndRegex();

    [GeneratedRegex(@"^\x1b\[27;([0-9]+);([0-9]+)~$")]
    private static partial Regex ModifyOtherKeysRegex();

    private static int NormalizeKittyFunctionalCodepoint(int codepoint) =>
        KittyFunctionalEquivalents.TryGetValue(codepoint, out var mapped) ? mapped : codepoint;

    private static int NormalizeShiftedLetterIdentityCodepoint(int codepoint, int modifier)
    {
        var effective = modifier & ~LockMask;
        if ((effective & ModShift) != 0 && codepoint >= 65 && codepoint <= 90)
        {
            return codepoint + 32;
        }
        return codepoint;
    }

    /// <summary>Check if input carries a Kitty key-release event (flag 2).</summary>
    public static bool IsKeyRelease(string data)
    {
        if (data.Contains("\x1b[200~"))
        {
            return false;
        }
        return data.Contains(":3u") || data.Contains(":3~") || data.Contains(":3A") || data.Contains(":3B")
            || data.Contains(":3C") || data.Contains(":3D") || data.Contains(":3H") || data.Contains(":3F");
    }

    /// <summary>Check if input carries a Kitty key-repeat event (flag 2).</summary>
    public static bool IsKeyRepeat(string data)
    {
        if (data.Contains("\x1b[200~"))
        {
            return false;
        }
        return data.Contains(":2u") || data.Contains(":2~") || data.Contains(":2A") || data.Contains(":2B")
            || data.Contains(":2C") || data.Contains(":2D") || data.Contains(":2H") || data.Contains(":2F");
    }

    private static KeyEventType ParseEventType(string? eventTypeStr)
    {
        if (string.IsNullOrEmpty(eventTypeStr))
        {
            return KeyEventType.Press;
        }
        return int.TryParse(eventTypeStr, out var v) ? v switch
        {
            2 => KeyEventType.Repeat,
            3 => KeyEventType.Release,
            _ => KeyEventType.Press,
        } : KeyEventType.Press;
    }

    private readonly record struct KittySequence(int Codepoint, int? ShiftedKey, int? BaseLayoutKey, int Modifier, KeyEventType EventType);

    private static KittySequence? ParseKittySequence(string data)
    {
        var m = CsiURegex().Match(data);
        if (m.Success)
        {
            var codepoint = int.Parse(m.Groups[1].Value);
            var shiftedRaw = m.Groups[2].Value;
            int? shiftedKey = m.Groups[2].Success && shiftedRaw.Length > 0 ? int.Parse(shiftedRaw) : null;
            int? baseLayoutKey = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null;
            var modValue = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
            var eventType = ParseEventType(m.Groups[5].Success ? m.Groups[5].Value : null);
            return new KittySequence(codepoint, shiftedKey, baseLayoutKey, modValue - 1, eventType);
        }

        var arrow = ArrowRegex().Match(data);
        if (arrow.Success)
        {
            var modValue = int.Parse(arrow.Groups[1].Value);
            var eventType = ParseEventType(arrow.Groups[2].Success ? arrow.Groups[2].Value : null);
            var code = arrow.Groups[3].Value switch { "A" => CpUp, "B" => CpDown, "C" => CpRight, _ => CpLeft };
            return new KittySequence(code, null, null, modValue - 1, eventType);
        }

        var func = FuncRegex().Match(data);
        if (func.Success)
        {
            var keyNum = int.Parse(func.Groups[1].Value);
            var modValue = func.Groups[2].Success ? int.Parse(func.Groups[2].Value) : 1;
            var eventType = ParseEventType(func.Groups[3].Success ? func.Groups[3].Value : null);
            int? code = keyNum switch
            {
                2 => CpInsert,
                3 => CpDelete,
                5 => CpPageUp,
                6 => CpPageDown,
                7 => CpHome,
                8 => CpEnd,
                _ => null,
            };
            if (code is { } c)
            {
                return new KittySequence(c, null, null, modValue - 1, eventType);
            }
        }

        var homeEnd = HomeEndRegex().Match(data);
        if (homeEnd.Success)
        {
            var modValue = int.Parse(homeEnd.Groups[1].Value);
            var eventType = ParseEventType(homeEnd.Groups[2].Success ? homeEnd.Groups[2].Value : null);
            var code = homeEnd.Groups[3].Value == "H" ? CpHome : CpEnd;
            return new KittySequence(code, null, null, modValue - 1, eventType);
        }

        return null;
    }

    private static bool MatchesKittySequence(string data, int expectedCodepoint, int expectedModifier)
    {
        var parsed = ParseKittySequence(data);
        if (parsed is not { } p)
        {
            return false;
        }
        var actualMod = p.Modifier & ~LockMask;
        var expectedMod = expectedModifier & ~LockMask;
        if (actualMod != expectedMod)
        {
            return false;
        }

        var normalizedCodepoint = NormalizeShiftedLetterIdentityCodepoint(NormalizeKittyFunctionalCodepoint(p.Codepoint), p.Modifier);
        var normalizedExpected = NormalizeShiftedLetterIdentityCodepoint(NormalizeKittyFunctionalCodepoint(expectedCodepoint), expectedModifier);
        if (normalizedCodepoint == normalizedExpected)
        {
            return true;
        }

        if (p.BaseLayoutKey is { } baseLayout && baseLayout == expectedCodepoint)
        {
            var cp = normalizedCodepoint;
            var isLatinLetter = cp is >= 97 and <= 122;
            var isKnownSymbol = SymbolKeys.Contains(((char)cp).ToString());
            if (!isLatinLetter && !isKnownSymbol)
            {
                return true;
            }
        }
        return false;
    }

    private static (int Codepoint, int Modifier)? ParseModifyOtherKeysSequence(string data)
    {
        var m = ModifyOtherKeysRegex().Match(data);
        if (!m.Success)
        {
            return null;
        }
        return (int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value) - 1);
    }

    private static bool MatchesModifyOtherKeys(string data, int expectedKeycode, int expectedModifier)
    {
        var parsed = ParseModifyOtherKeysSequence(data);
        return parsed is { } p && p.Codepoint == expectedKeycode && p.Modifier == expectedModifier;
    }

    private static bool IsWindowsTerminalSession() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT"))
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));

    private static bool MatchesRawBackspace(string data, int expectedModifier)
    {
        if (data == "\x7f")
        {
            return expectedModifier == 0;
        }
        if (data != "\x08")
        {
            return false;
        }
        return IsWindowsTerminalSession() ? expectedModifier == ModCtrl : expectedModifier == 0;
    }

    private static string? RawCtrlChar(string key)
    {
        var ch = char.ToLowerInvariant(key[0]);
        var code = (int)ch;
        if ((code >= 97 && code <= 122) || ch is '[' or '\\' or ']' or '_')
        {
            return ((char)(code & 0x1f)).ToString();
        }
        if (ch == '-')
        {
            return ((char)31).ToString();
        }
        return null;
    }

    private static bool IsDigitKey(string key) => key.Length == 1 && key[0] >= '0' && key[0] <= '9';

    private static bool MatchesPrintableModifyOtherKeys(string data, int expectedKeycode, int expectedModifier)
    {
        if (expectedModifier == 0)
        {
            return false;
        }
        var parsed = ParseModifyOtherKeysSequence(data);
        if (parsed is not { } p || p.Modifier != expectedModifier)
        {
            return false;
        }
        return NormalizeShiftedLetterIdentityCodepoint(p.Codepoint, p.Modifier)
            == NormalizeShiftedLetterIdentityCodepoint(expectedKeycode, expectedModifier);
    }

    private static string? FormatKeyNameWithModifiers(string keyName, int modifier)
    {
        var mods = new List<string>(4);
        var effective = modifier & ~LockMask;
        const int supported = ModShift | ModCtrl | ModAlt | ModSuper;
        if ((effective & ~supported) != 0)
        {
            return null;
        }
        if ((effective & ModShift) != 0) mods.Add("shift");
        if ((effective & ModCtrl) != 0) mods.Add("ctrl");
        if ((effective & ModAlt) != 0) mods.Add("alt");
        if ((effective & ModSuper) != 0) mods.Add("super");
        return mods.Count > 0 ? $"{string.Join("+", mods)}+{keyName}" : keyName;
    }

    private readonly record struct ParsedKeyId(string Key, bool Ctrl, bool Shift, bool Alt, bool Super);

    private static ParsedKeyId? ParseKeyId(string keyId)
    {
        var parts = keyId.ToLowerInvariant().Split('+');
        var key = parts[^1];
        if (key.Length == 0)
        {
            return null;
        }
        return new ParsedKeyId(key, parts.Contains("ctrl"), parts.Contains("shift"), parts.Contains("alt"), parts.Contains("super"));
    }

    private static bool MatchesLegacySequence(string data, string[] sequences) => sequences.Contains(data);

    private static bool MatchesLegacyModifierSequence(string data, string key, int modifier)
    {
        if (modifier == ModShift)
        {
            return LegacyShiftSequences.TryGetValue(key, out var seqs) && seqs.Contains(data);
        }
        if (modifier == ModCtrl)
        {
            return LegacyCtrlSequences.TryGetValue(key, out var seqs) && seqs.Contains(data);
        }
        return false;
    }

    /// <summary>Match raw input data against a key identifier string (e.g. "ctrl+c", "escape").</summary>
    public static bool MatchesKey(string data, string keyId)
    {
        var parsed = ParseKeyId(keyId);
        if (parsed is not { } p)
        {
            return false;
        }

        var key = p.Key;
        var modifier = 0;
        if (p.Shift) modifier |= ModShift;
        if (p.Alt) modifier |= ModAlt;
        if (p.Ctrl) modifier |= ModCtrl;
        if (p.Super) modifier |= ModSuper;

        switch (key)
        {
            case "escape":
            case "esc":
                if (modifier != 0) return false;
                return data == "\x1b" || MatchesKittySequence(data, CpEscape, 0) || MatchesModifyOtherKeys(data, CpEscape, 0);

            case "space":
                if (!_kittyProtocolActive)
                {
                    if (modifier == ModCtrl && data == "\x00") return true;
                    if (modifier == ModAlt && data == "\x1b ") return true;
                }
                if (modifier == 0)
                {
                    return data == " " || MatchesKittySequence(data, CpSpace, 0) || MatchesModifyOtherKeys(data, CpSpace, 0);
                }
                return MatchesKittySequence(data, CpSpace, modifier) || MatchesModifyOtherKeys(data, CpSpace, modifier);

            case "tab":
                if (modifier == ModShift)
                {
                    return data == "\x1b[Z" || MatchesKittySequence(data, CpTab, ModShift) || MatchesModifyOtherKeys(data, CpTab, ModShift);
                }
                if (modifier == 0)
                {
                    return data == "\t" || MatchesKittySequence(data, CpTab, 0);
                }
                return MatchesKittySequence(data, CpTab, modifier) || MatchesModifyOtherKeys(data, CpTab, modifier);

            case "enter":
            case "return":
                if (modifier == ModShift)
                {
                    if (MatchesKittySequence(data, CpEnter, ModShift) || MatchesKittySequence(data, CpKpEnter, ModShift)) return true;
                    if (MatchesModifyOtherKeys(data, CpEnter, ModShift)) return true;
                    if (_kittyProtocolActive) return data == "\x1b\r" || data == "\n";
                    return false;
                }
                if (modifier == ModAlt)
                {
                    if (MatchesKittySequence(data, CpEnter, ModAlt) || MatchesKittySequence(data, CpKpEnter, ModAlt)) return true;
                    if (MatchesModifyOtherKeys(data, CpEnter, ModAlt)) return true;
                    if (!_kittyProtocolActive) return data == "\x1b\r";
                    return false;
                }
                if (modifier == 0)
                {
                    return data == "\r"
                        || (!_kittyProtocolActive && data == "\n")
                        || data == "\x1bOM"
                        || MatchesKittySequence(data, CpEnter, 0)
                        || MatchesKittySequence(data, CpKpEnter, 0);
                }
                return MatchesKittySequence(data, CpEnter, modifier)
                    || MatchesKittySequence(data, CpKpEnter, modifier)
                    || MatchesModifyOtherKeys(data, CpEnter, modifier);

            case "backspace":
                if (modifier == ModAlt)
                {
                    if (data == "\x1b\x7f" || data == "\x1b\b") return true;
                    return MatchesKittySequence(data, CpBackspace, ModAlt) || MatchesModifyOtherKeys(data, CpBackspace, ModAlt);
                }
                if (modifier == ModCtrl)
                {
                    if (MatchesRawBackspace(data, ModCtrl)) return true;
                    return MatchesKittySequence(data, CpBackspace, ModCtrl) || MatchesModifyOtherKeys(data, CpBackspace, ModCtrl);
                }
                if (modifier == 0)
                {
                    return MatchesRawBackspace(data, 0) || MatchesKittySequence(data, CpBackspace, 0) || MatchesModifyOtherKeys(data, CpBackspace, 0);
                }
                return MatchesKittySequence(data, CpBackspace, modifier) || MatchesModifyOtherKeys(data, CpBackspace, modifier);

            case "insert":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["insert"]) || MatchesKittySequence(data, CpInsert, 0);
                }
                return MatchesLegacyModifierSequence(data, "insert", modifier) || MatchesKittySequence(data, CpInsert, modifier);

            case "delete":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["delete"]) || MatchesKittySequence(data, CpDelete, 0);
                }
                return MatchesLegacyModifierSequence(data, "delete", modifier) || MatchesKittySequence(data, CpDelete, modifier);

            case "clear":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["clear"]);
                }
                return MatchesLegacyModifierSequence(data, "clear", modifier);

            case "home":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["home"]) || MatchesKittySequence(data, CpHome, 0);
                }
                return MatchesLegacyModifierSequence(data, "home", modifier) || MatchesKittySequence(data, CpHome, modifier);

            case "end":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["end"]) || MatchesKittySequence(data, CpEnd, 0);
                }
                return MatchesLegacyModifierSequence(data, "end", modifier) || MatchesKittySequence(data, CpEnd, modifier);

            case "pageup":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["pageUp"]) || MatchesKittySequence(data, CpPageUp, 0);
                }
                return MatchesLegacyModifierSequence(data, "pageUp", modifier) || MatchesKittySequence(data, CpPageUp, modifier);

            case "pagedown":
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["pageDown"]) || MatchesKittySequence(data, CpPageDown, 0);
                }
                return MatchesLegacyModifierSequence(data, "pageDown", modifier) || MatchesKittySequence(data, CpPageDown, modifier);

            case "up":
                if (modifier == ModAlt)
                {
                    return data == "\x1bp" || MatchesKittySequence(data, CpUp, ModAlt);
                }
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["up"]) || MatchesKittySequence(data, CpUp, 0);
                }
                return MatchesLegacyModifierSequence(data, "up", modifier) || MatchesKittySequence(data, CpUp, modifier);

            case "down":
                if (modifier == ModAlt)
                {
                    return data == "\x1bn" || MatchesKittySequence(data, CpDown, ModAlt);
                }
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["down"]) || MatchesKittySequence(data, CpDown, 0);
                }
                return MatchesLegacyModifierSequence(data, "down", modifier) || MatchesKittySequence(data, CpDown, modifier);

            case "left":
                if (modifier == ModAlt)
                {
                    return data == "\x1b[1;3D" || (!_kittyProtocolActive && data == "\x1bB") || data == "\x1bb"
                        || MatchesKittySequence(data, CpLeft, ModAlt);
                }
                if (modifier == ModCtrl)
                {
                    return data == "\x1b[1;5D" || MatchesLegacyModifierSequence(data, "left", ModCtrl)
                        || MatchesKittySequence(data, CpLeft, ModCtrl);
                }
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["left"]) || MatchesKittySequence(data, CpLeft, 0);
                }
                return MatchesLegacyModifierSequence(data, "left", modifier) || MatchesKittySequence(data, CpLeft, modifier);

            case "right":
                if (modifier == ModAlt)
                {
                    return data == "\x1b[1;3C" || (!_kittyProtocolActive && data == "\x1bF") || data == "\x1bf"
                        || MatchesKittySequence(data, CpRight, ModAlt);
                }
                if (modifier == ModCtrl)
                {
                    return data == "\x1b[1;5C" || MatchesLegacyModifierSequence(data, "right", ModCtrl)
                        || MatchesKittySequence(data, CpRight, ModCtrl);
                }
                if (modifier == 0)
                {
                    return MatchesLegacySequence(data, LegacyKeySequences["right"]) || MatchesKittySequence(data, CpRight, 0);
                }
                return MatchesLegacyModifierSequence(data, "right", modifier) || MatchesKittySequence(data, CpRight, modifier);
        }

        if (FunctionKeys.Contains(key))
        {
            if (modifier != 0)
            {
                return false;
            }
            return MatchesLegacySequence(data, LegacyKeySequences[key]);
        }

        // Single letter/digit/symbol keys.
        if (key.Length == 1 && ((key[0] >= 'a' && key[0] <= 'z') || IsDigitKey(key) || SymbolKeys.Contains(key)))
        {
            var codepoint = key[0];
            var rawCtrl = RawCtrlChar(key);
            var isLetter = key[0] >= 'a' && key[0] <= 'z';
            var isDigit = IsDigitKey(key);

            if (modifier == ModCtrl + ModAlt && !_kittyProtocolActive && rawCtrl is not null)
            {
                if (data == $"\x1b{rawCtrl}") return true;
            }

            if (modifier == ModAlt && !_kittyProtocolActive && (isLetter || isDigit || SymbolKeys.Contains(key)))
            {
                if (data == $"\x1b{key}") return true;
            }

            if (modifier == ModCtrl)
            {
                if (rawCtrl is not null && data == rawCtrl) return true;
                return MatchesKittySequence(data, codepoint, ModCtrl) || MatchesPrintableModifyOtherKeys(data, codepoint, ModCtrl);
            }

            if (modifier == ModShift + ModCtrl)
            {
                return MatchesKittySequence(data, codepoint, ModShift + ModCtrl)
                    || MatchesPrintableModifyOtherKeys(data, codepoint, ModShift + ModCtrl);
            }

            if (modifier == ModShift)
            {
                if (isLetter && data == key.ToUpperInvariant()) return true;
                return MatchesKittySequence(data, codepoint, ModShift) || MatchesPrintableModifyOtherKeys(data, codepoint, ModShift);
            }

            if (modifier != 0)
            {
                return MatchesKittySequence(data, codepoint, modifier) || MatchesPrintableModifyOtherKeys(data, codepoint, modifier);
            }

            return data == key || MatchesKittySequence(data, codepoint, 0);
        }

        return false;
    }

    private static string? FormatParsedKey(int codepoint, int modifier, int? baseLayoutKey)
    {
        var normalizedCodepoint = NormalizeKittyFunctionalCodepoint(codepoint);
        var identityCodepoint = NormalizeShiftedLetterIdentityCodepoint(normalizedCodepoint, modifier);

        var isLatinLetter = identityCodepoint is >= 97 and <= 122;
        var isDigit = identityCodepoint is >= 48 and <= 57;
        var isKnownSymbol = SymbolKeys.Contains(((char)identityCodepoint).ToString());
        var effectiveCodepoint = isLatinLetter || isDigit || isKnownSymbol ? identityCodepoint : (baseLayoutKey ?? identityCodepoint);

        string? keyName = effectiveCodepoint switch
        {
            CpEscape => "escape",
            CpTab => "tab",
            CpEnter or CpKpEnter => "enter",
            CpSpace => "space",
            CpBackspace => "backspace",
            CpDelete => "delete",
            CpInsert => "insert",
            CpHome => "home",
            CpEnd => "end",
            CpPageUp => "pageUp",
            CpPageDown => "pageDown",
            CpUp => "up",
            CpDown => "down",
            CpLeft => "left",
            CpRight => "right",
            >= 48 and <= 57 => ((char)effectiveCodepoint).ToString(),
            >= 97 and <= 122 => ((char)effectiveCodepoint).ToString(),
            _ => SymbolKeys.Contains(((char)effectiveCodepoint).ToString()) ? ((char)effectiveCodepoint).ToString() : null,
        };

        if (keyName is null)
        {
            return null;
        }
        return FormatKeyNameWithModifiers(keyName, modifier);
    }

    /// <summary>Parse input data and return the key identifier if recognized.</summary>
    public static string? ParseKey(string data)
    {
        var kitty = ParseKittySequence(data);
        if (kitty is { } k)
        {
            return FormatParsedKey(k.Codepoint, k.Modifier, k.BaseLayoutKey);
        }

        var modifyOtherKeys = ParseModifyOtherKeysSequence(data);
        if (modifyOtherKeys is { } mok)
        {
            return FormatParsedKey(mok.Codepoint, mok.Modifier, null);
        }

        if (_kittyProtocolActive)
        {
            if (data == "\x1b\r" || data == "\n") return "shift+enter";
        }

        if (LegacySequenceKeyIds.TryGetValue(data, out var legacyId))
        {
            return legacyId;
        }

        if (data == "\x1b") return "escape";
        if (data == "\x1c") return "ctrl+\\";
        if (data == "\x1d") return "ctrl+]";
        if (data == "\x1f") return "ctrl+-";
        if (data == "\x1b\x1b") return "ctrl+alt+[";
        if (data == "\x1b\x1c") return "ctrl+alt+\\";
        if (data == "\x1b\x1d") return "ctrl+alt+]";
        if (data == "\x1b\x1f") return "ctrl+alt+-";
        if (data == "\t") return "tab";
        if (data == "\r" || (!_kittyProtocolActive && data == "\n") || data == "\x1bOM") return "enter";
        if (data == "\x00") return "ctrl+space";
        if (data == " ") return "space";
        if (data == "\x7f") return "backspace";
        if (data == "\x08") return IsWindowsTerminalSession() ? "ctrl+backspace" : "backspace";
        if (data == "\x1b[Z") return "shift+tab";
        if (!_kittyProtocolActive && data == "\x1b\r") return "alt+enter";
        if (!_kittyProtocolActive && data == "\x1b ") return "alt+space";
        if (data == "\x1b\x7f" || data == "\x1b\b") return "alt+backspace";
        if (!_kittyProtocolActive && data == "\x1bB") return "alt+left";
        if (!_kittyProtocolActive && data == "\x1bF") return "alt+right";
        if (!_kittyProtocolActive && data.Length == 2 && data[0] == '\x1b')
        {
            var code = data[1];
            if (code >= 1 && code <= 26)
            {
                return $"ctrl+alt+{(char)(code + 96)}";
            }
            if ((code >= 97 && code <= 122) || (code >= 48 && code <= 57) || SymbolKeys.Contains(code.ToString()))
            {
                return $"alt+{code}";
            }
        }
        if (data == "\x1b[A") return "up";
        if (data == "\x1b[B") return "down";
        if (data == "\x1b[C") return "right";
        if (data == "\x1b[D") return "left";
        if (data == "\x1b[H" || data == "\x1bOH") return "home";
        if (data == "\x1b[F" || data == "\x1bOF") return "end";
        if (data == "\x1b[3~") return "delete";
        if (data == "\x1b[5~") return "pageUp";
        if (data == "\x1b[6~") return "pageDown";

        if (data.Length == 1)
        {
            var code = data[0];
            if (code >= 1 && code <= 26)
            {
                return $"ctrl+{(char)(code + 96)}";
            }
            if (code >= 32 && code <= 126)
            {
                return data;
            }
        }

        return null;
    }

    private const int KittyPrintableAllowedModifiers = ModShift | LockMask;

    /// <summary>Decode a Kitty CSI-u sequence into a printable character, if applicable.</summary>
    public static string? DecodeKittyPrintable(string data)
    {
        var m = CsiURegex().Match(data);
        if (!m.Success)
        {
            return null;
        }

        if (!int.TryParse(m.Groups[1].Value, out var codepoint))
        {
            return null;
        }

        var shiftedRaw = m.Groups[2].Value;
        int? shiftedKey = m.Groups[2].Success && shiftedRaw.Length > 0 ? int.Parse(shiftedRaw) : null;
        var modValue = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
        var modifier = modValue - 1;

        if ((modifier & ~KittyPrintableAllowedModifiers) != 0)
        {
            return null;
        }
        if ((modifier & (ModAlt | ModCtrl)) != 0)
        {
            return null;
        }

        var effectiveCodepoint = codepoint;
        if ((modifier & ModShift) != 0 && shiftedKey is { } sk)
        {
            effectiveCodepoint = sk;
        }
        effectiveCodepoint = NormalizeKittyFunctionalCodepoint(effectiveCodepoint);
        if (effectiveCodepoint < 32)
        {
            return null;
        }

        try
        {
            return char.ConvertFromUtf32(effectiveCodepoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? DecodeModifyOtherKeysPrintable(string data)
    {
        var parsed = ParseModifyOtherKeysSequence(data);
        if (parsed is not { } p)
        {
            return null;
        }
        var modifier = p.Modifier & ~LockMask;
        if ((modifier & ~ModShift) != 0)
        {
            return null;
        }
        if (p.Codepoint < 32)
        {
            return null;
        }
        try
        {
            return char.ConvertFromUtf32(p.Codepoint);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Decode a printable key from Kitty CSI-u or xterm modifyOtherKeys input.</summary>
    public static string? DecodePrintableKey(string data) =>
        DecodeKittyPrintable(data) ?? DecodeModifyOtherKeysPrintable(data);
}

/// <summary>Typed key-identifier helpers mirroring the TS <c>Key</c> object.</summary>
public static class Key
{
    public const string Escape = "escape";
    public const string Esc = "esc";
    public const string Enter = "enter";
    public const string Return = "return";
    public const string Tab = "tab";
    public const string Space = "space";
    public const string Backspace = "backspace";
    public const string Delete = "delete";
    public const string Insert = "insert";
    public const string Clear = "clear";
    public const string Home = "home";
    public const string End = "end";
    public const string PageUp = "pageUp";
    public const string PageDown = "pageDown";
    public const string Up = "up";
    public const string Down = "down";
    public const string Left = "left";
    public const string Right = "right";
    public const string F1 = "f1";
    public const string F2 = "f2";
    public const string F3 = "f3";
    public const string F4 = "f4";
    public const string F5 = "f5";
    public const string F6 = "f6";
    public const string F7 = "f7";
    public const string F8 = "f8";
    public const string F9 = "f9";
    public const string F10 = "f10";
    public const string F11 = "f11";
    public const string F12 = "f12";

    public static string Ctrl(string key) => $"ctrl+{key}";
    public static string Shift(string key) => $"shift+{key}";
    public static string Alt(string key) => $"alt+{key}";
    public static string Super(string key) => $"super+{key}";
    public static string CtrlShift(string key) => $"ctrl+shift+{key}";
    public static string ShiftCtrl(string key) => $"shift+ctrl+{key}";
    public static string CtrlAlt(string key) => $"ctrl+alt+{key}";
    public static string AltCtrl(string key) => $"alt+ctrl+{key}";
    public static string ShiftAlt(string key) => $"shift+alt+{key}";
    public static string AltShift(string key) => $"alt+shift+{key}";
    public static string CtrlSuper(string key) => $"ctrl+super+{key}";
    public static string SuperCtrl(string key) => $"super+ctrl+{key}";
    public static string ShiftSuper(string key) => $"shift+super+{key}";
    public static string SuperShift(string key) => $"super+shift+{key}";
    public static string AltSuper(string key) => $"alt+super+{key}";
    public static string SuperAlt(string key) => $"super+alt+{key}";
    public static string CtrlShiftAlt(string key) => $"ctrl+shift+alt+{key}";
    public static string CtrlShiftSuper(string key) => $"ctrl+shift+super+{key}";
}
