namespace Pi.CodingAgent.Utils.Image;

/// <summary>
/// The JS <c>Buffer.from(value, "base64")</c> decoder: it silently skips characters outside the
/// base64 alphabet (including whitespace) instead of throwing, drops a dangling trailing
/// character when the cleaned length is 1 mod 4, and tolerates missing padding. .NET's
/// <see cref="Convert.FromBase64String"/> throws on any of that, so the port reproduces the
/// lenient front half and then hands a clean string to the BCL decoder.
/// </summary>
internal static class LenientBase64
{
    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        Span<char> cleaned = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        int length = 0;
        foreach (char c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/')
            {
                cleaned[length++] = c;
            }
        }

        // A 1-mod-4 tail cannot form a byte triple; JS drops it.
        if (length % 4 == 1)
        {
            length--;
        }

        string padded = (length % 4) switch
        {
            2 => new string(cleaned[..length]) + "==",
            3 => new string(cleaned[..length]) + "=",
            _ => new string(cleaned[..length]),
        };
        return padded.Length == 0 ? [] : Convert.FromBase64String(padded);
    }
}
