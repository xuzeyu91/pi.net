// ============================================================================
// Timings — port of core/timings.ts (4d-2b)
// ============================================================================
//
// Startup profiling instrumentation, gated on PI_TIMING=1. The extension loader records two
// labels per extension (module import, factory run); the port keeps the same labels, the same
// per-namespace accumulation and the same stderr report so profiles compare 1:1.

namespace Pi.CodingAgent.Core;

/// <summary>Central timing instrumentation for startup profiling. Port of <c>core/timings.ts</c>.</summary>
public static class Timings
{
    /// <summary>Timing namespaces. TS <c>TimingLabel</c>.</summary>
    public static class Namespaces
    {
        /// <summary>General startup phases.</summary>
        public const string Main = "main";

        /// <summary>Extension loading.</summary>
        public const string Extensions = "extensions";
    }

    /// <summary>
    /// Where the report goes. Defaults to <see cref="Console.Error"/> (the TS version uses
    /// <c>console.error</c>); tests substitute a writer to observe the report (convention C3).
    /// </summary>
    internal static TextWriter? ErrorWriterOverride { get; set; }

    /// <summary>
    /// Overrides the <c>PI_TIMING</c> environment gate. Null reads the environment
    /// (<c>PI_TIMING === "1"</c>); tests set it to exercise the enabled path.
    /// </summary>
    internal static bool? EnabledOverride { get; set; }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, TimingNamespace> NamespaceMap = new(StringComparer.Ordinal);

    private static bool Enabled => EnabledOverride ?? Environment.GetEnvironmentVariable("PI_TIMING") == "1";

    private static TextWriter ErrorWriter => ErrorWriterOverride ?? Console.Error;

    /// <summary>Reset one namespace, restarting its clock. Port of <c>resetTimings</c>.</summary>
    public static void Reset(string @namespace = Namespaces.Main)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            NamespaceMap[@namespace] = new TimingNamespace { LastTime = Environment.TickCount64 };
        }
    }

    /// <summary>
    /// Record the time since the previous entry of <paramref name="namespace"/>. Port of <c>time</c>;
    /// the namespace is created on first use, like the TS version.
    /// </summary>
    public static void Time(string label, string @namespace = Namespaces.Main)
    {
        if (!Enabled) return;
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (!NamespaceMap.TryGetValue(@namespace, out var entry))
            {
                entry = new TimingNamespace { LastTime = now };
                NamespaceMap[@namespace] = entry;
            }

            entry.Timings.Add((label, now - entry.LastTime));
            entry.LastTime = now;
        }
    }

    /// <summary>Print every namespace's report. Port of <c>printTimings</c>.</summary>
    public static void Print()
    {
        if (!Enabled) return;
        List<(string Namespace, TimingNamespace Entry)> snapshot;
        lock (Gate)
        {
            snapshot = NamespaceMap.Select(pair => (pair.Key, pair.Value)).ToList();
        }

        foreach (var (name, entry) in snapshot)
        {
            PrintGroup($"Startup Timings: {name}", entry);
        }
    }

    /// <summary>Clear all recorded state and overrides. Exported for tests.</summary>
    internal static void ClearForTests()
    {
        lock (Gate)
        {
            NamespaceMap.Clear();
        }

        EnabledOverride = null;
    }

    private static void PrintGroup(string title, TimingNamespace entry)
    {
        var printable = entry.Timings.Where(timing => timing.Ms >= 0).ToList();
        if (printable.Count == 0) return;

        var writer = ErrorWriter;
        writer.WriteLine($"\n--- {title} ---");
        foreach (var (label, ms) in printable)
        {
            writer.WriteLine($"  {label}: {ms}ms");
        }

        writer.WriteLine($"  TOTAL: {printable.Sum(timing => timing.Ms)}ms");
        writer.WriteLine($"{new string('-', title.Length + 8)}\n");
    }

    private sealed class TimingNamespace
    {
        public List<(string Label, long Ms)> Timings { get; } = [];

        public long LastTime { get; set; }
    }
}
