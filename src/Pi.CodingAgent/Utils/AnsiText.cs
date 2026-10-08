using System.Text.RegularExpressions;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of <c>utils/ansi.ts</c>, which itself derives from <c>ansi-regex</c> / <c>strip-ansi</c>
/// (MIT, Sindre Sorhus).
/// </summary>
/// <remarks>
/// Renamed from the TS module's <c>stripAnsi</c> home to <c>AnsiText</c> because <see cref="Pi.Tui.Ansi"/>
/// already exists and is imported alongside this namespace; the TS package has the same two
/// independent implementations (tui's <c>utils.ts</c> vs coding-agent's <c>utils/ansi.ts</c>).
/// </remarks>
public static class AnsiText
{
    // Valid string terminator sequences are BEL, ESC\, and 0x9c.
    private const string St = @"(?:\u0007|\u001B\\|\u009C)";

    // OSC sequences only: ESC ] ... ST (non-greedy until the first ST).
    private const string Osc = @"(?:\u001B\][\s\S]*?" + St + ")";

    // CSI and related: ESC/C1, optional intermediates, optional params (supports ; and :) then final byte.
    // JS `\d` is [0-9]; .NET `\d` would also match other Unicode decimal digits.
    private const string Csi =
        @"[\u001B\u009B][\[\]()#;?]*(?:[0-9]{1,4}(?:[;:][0-9]{0,4})*)?[0-9A-PR-TZcf-nq-uy=><~]";

    private static readonly Regex Pattern = new(Osc + "|" + Csi, RegexOptions.Compiled);

    /// <summary>Remove ANSI escape sequences from <paramref name="value"/>.</summary>
    public static string StripAnsi(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Fast path: ANSI codes require an ESC (7-bit) or CSI (8-bit) introducer.
        if (!value.Contains('\u001B') && !value.Contains('\u009B'))
        {
            return value;
        }

        return Pattern.Replace(value, string.Empty);
    }
}
