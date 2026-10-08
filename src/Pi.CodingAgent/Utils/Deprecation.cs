namespace Pi.CodingAgent.Utils;

/// <summary>Port of <c>utils/deprecation.ts</c>.</summary>
public static class Deprecation
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Emitted = new(StringComparer.Ordinal);

    /// <summary>
    /// Where warnings go. Defaults to <see cref="Console.Error"/> (the TS version uses
    /// <c>console.warn</c>); tests substitute a writer to observe the line without touching the process.
    /// </summary>
    internal static TextWriter? ErrorWriterOverride { get; set; }

    /// <summary>Emit a deprecation warning once per distinct message.</summary>
    public static void WarnDeprecation(string message)
    {
        lock (Gate)
        {
            if (!Emitted.Add(message))
            {
                return;
            }
        }

        var writer = ErrorWriterOverride ?? Console.Error;
        writer.WriteLine(Chalk.Yellow($"Deprecation warning: {message}"));
    }

    /// <summary>Clear deprecation warning state. Exported for tests.</summary>
    public static void ClearDeprecationWarningsForTests()
    {
        lock (Gate)
        {
            Emitted.Clear();
        }
    }
}
