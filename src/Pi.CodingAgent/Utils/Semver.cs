using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Pi.Tui;

namespace Pi.CodingAgent.Utils;

/// <summary>
/// Port of the part of <c>semver</c> 7.8.5 the coding agent needs: <c>valid()</c> and <c>compare()</c>
/// in strict (non-loose) mode.
/// </summary>
/// <remarks>
/// <para>
/// The pattern is transcribed from <c>internal/re.js</c> including the "safe" length bounds semver
/// applies before matching (<c>\d*</c> becomes <c>\d{0,256}</c>, <c>[a-zA-Z0-9-]+</c> becomes
/// <c>{1,250}</c>). Those bounds are observable rather than cosmetic: the class matches against
/// <c>safeRe</c>, not <c>re</c>, so an unbounded run of digits or dashes is rejected.
/// </para>
/// <para>
/// Two engine differences matter and are handled explicitly:
/// </para>
/// <list type="bullet">
/// <item><description>
/// JS <c>$</c> without the <c>m</c> flag anchors only at end of input, whereas .NET <c>$</c> also
/// matches before a trailing <c>\n</c>. Every anchored pattern below ends with <c>\z</c>.
/// </description></item>
/// <item><description>
/// JS <c>\d</c> is <c>[0-9]</c> while .NET <c>\d</c> is <c>\p{Nd}</c>, so the digit runs are written
/// out as <c>[0-9]</c>.
/// </description></item>
/// </list>
/// <para>
/// The range half of semver (<c>satisfies</c>, <c>validRange</c>, <c>maxSatisfying</c>, <c>gt</c>,
/// <c>rcompare</c>) has a single caller, <c>core/package-manager.ts</c>, and is ported alongside that
/// module instead of here.
/// </para>
/// </remarks>
public static class Semver
{
    /// <summary><c>MAX_LENGTH</c>: versions longer than this are rejected before the pattern is tried.</summary>
    public const int MaxLength = 256;

    /// <summary><c>Number.MAX_SAFE_INTEGER</c>.</summary>
    private const long MaxSafeInteger = 9007199254740991L;

    // ---- internal/re.js, with the safe-regex length bounds already applied ----

    private const string NumericIdentifier = "0|[1-9][0-9]{0,256}";
    private const string NonNumericIdentifier = "[0-9]{0,256}[a-zA-Z-][a-zA-Z0-9-]{0,250}";
    private const string PrereleaseIdentifier = "(?:" + NonNumericIdentifier + "|" + NumericIdentifier + ")";
    private const string Prerelease = "(?:-(" + PrereleaseIdentifier + "(?:\\." + PrereleaseIdentifier + ")*))";
    private const string BuildIdentifier = "[a-zA-Z0-9-]{1,250}";
    private const string Build = "(?:\\+(" + BuildIdentifier + "(?:\\." + BuildIdentifier + ")*))";

    private static readonly Regex Full = new(
        "^v?(" + NumericIdentifier + ")\\.(" + NumericIdentifier + ")\\.(" + NumericIdentifier + ")" +
        Prerelease + "?" + Build + "?\\z",
        RegexOptions.Compiled);

    private static readonly Regex NumericOnly = new("^[0-9]+\\z", RegexOptions.Compiled);

    /// <summary>A parsed version, mirroring semver's <c>SemVer</c> instance fields.</summary>
    /// <param name="Major">Major component.</param>
    /// <param name="Minor">Minor component.</param>
    /// <param name="Patch">Patch component.</param>
    /// <param name="Prerelease">Dot-separated pre-release identifiers, empty when there is no pre-release.</param>
    /// <param name="Build">Dot-separated build metadata identifiers, empty when absent.</param>
    /// <param name="Version">The normalized version string, as <c>SemVer#format()</c> produces it.</param>
    public sealed record ParsedVersion(
        long Major,
        long Minor,
        long Patch,
        IReadOnlyList<string> Prerelease,
        IReadOnlyList<string> Build,
        string Version);

    /// <summary>
    /// semver's <c>valid()</c>: the normalized version string, or <see langword="null"/> when the input
    /// is not a valid strict version.
    /// </summary>
    /// <remarks>
    /// Build metadata is dropped from the returned string (<c>valid("1.2.3+a")</c> is <c>"1.2.3"</c>),
    /// because <c>format()</c> only writes major, minor, patch and pre-release.
    /// </remarks>
    public static string? Valid(string? version) => Parse(version)?.Version;

    /// <summary>semver's <c>parse()</c>: the parsed version, or <see langword="null"/> when invalid.</summary>
    public static ParsedVersion? Parse(string? version)
    {
        // `new SemVer(version)` rejects anything that is not a string before it looks at the content.
        if (version is null)
        {
            return null;
        }

        // The length check runs on the raw string, before trim().
        if (version.Length > MaxLength)
        {
            return null;
        }

        var match = Full.Match(JsString.Trim(version));
        if (!match.Success)
        {
            return null;
        }

        if (!TryComponent(match.Groups[1].Value, out var major) ||
            !TryComponent(match.Groups[2].Value, out var minor) ||
            !TryComponent(match.Groups[3].Value, out var patch))
        {
            return null;
        }

        var prerelease = match.Groups[4].Success
            ? match.Groups[4].Value.Split('.')
            : [];

        var build = match.Groups[5].Success
            ? match.Groups[5].Value.Split('.')
            : [];

        var normalized = FormattableString.Invariant($"{major}.{minor}.{patch}");
        if (prerelease.Length > 0)
        {
            normalized += "-" + string.Join('.', prerelease);
        }

        return new ParsedVersion(major, minor, patch, prerelease, build, normalized);
    }

    /// <summary>
    /// semver's <c>compare()</c>: <c>-1</c>, <c>0</c> or <c>1</c>, or <see langword="null"/> when either
    /// side is not a valid strict version.
    /// </summary>
    /// <remarks>
    /// <c>comparePackageVersions</c> in <c>utils/version-check.ts</c> maps <see langword="null"/> to
    /// <c>undefined</c>, which is how an unparseable candidate falls back to a plain string comparison.
    /// </remarks>
    public static int? Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        if (a is null || b is null)
        {
            return null;
        }

        return CompareParsed(a, b);
    }

    /// <summary>semver's <c>SemVer#compare</c> over two already-parsed versions.</summary>
    public static int CompareParsed(ParsedVersion left, ParsedVersion right)
    {
        if (string.Equals(left.Version, right.Version, StringComparison.Ordinal))
        {
            return 0;
        }

        var main = CompareMain(left, right);
        return main != 0 ? main : ComparePrerelease(left, right);
    }

    private static int CompareMain(ParsedVersion left, ParsedVersion right)
    {
        // All three components are JS numbers, so compareIdentifiers takes its numeric branch.
        var major = left.Major.CompareTo(right.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = left.Minor.CompareTo(right.Minor);
        return minor != 0 ? minor : left.Patch.CompareTo(right.Patch);
    }

    private static int ComparePrerelease(ParsedVersion left, ParsedVersion right)
    {
        // "Not having a pre-release" sorts higher than having one.
        if (left.Prerelease.Count > 0 && right.Prerelease.Count == 0)
        {
            return -1;
        }

        if (left.Prerelease.Count == 0 && right.Prerelease.Count > 0)
        {
            return 1;
        }

        if (left.Prerelease.Count == 0)
        {
            return 0;
        }

        for (var index = 0; ; index++)
        {
            var a = index < left.Prerelease.Count ? left.Prerelease[index] : null;
            var b = index < right.Prerelease.Count ? right.Prerelease[index] : null;

            if (a is null && b is null)
            {
                return 0;
            }

            if (b is null)
            {
                return 1;
            }

            if (a is null)
            {
                return -1;
            }

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                continue;
            }

            return CompareIdentifiers(a, b);
        }
    }

    /// <summary>
    /// semver's <c>compareIdentifiers</c>. Numeric identifiers always sort below alphanumeric ones, and
    /// two numeric identifiers compare by value rather than by text.
    /// </summary>
    /// <remarks>
    /// The TS version branches on <c>typeof a === "number"</c> first, but that only ever changes the
    /// path taken: when both values are numeric the pair is coerced to numbers either way, and when
    /// exactly one is numeric the numeric one wins either way. Testing the strings is therefore
    /// equivalent.
    /// </remarks>
    public static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = NumericOnly.IsMatch(left);
        var rightNumeric = NumericOnly.IsMatch(right);

        if (leftNumeric && rightNumeric)
        {
            // Identifiers longer than MAX_SAFE_INTEGER stay strings in semver but are still compared
            // as numbers here; the values fit a double, so this matches the JS comparison exactly.
            var a = double.Parse(left, NumberStyles.None, CultureInfo.InvariantCulture);
            var b = double.Parse(right, NumberStyles.None, CultureInfo.InvariantCulture);
            return a == b ? 0 : a < b ? -1 : 1;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 0;
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        // JS `<` on two strings compares UTF-16 code units, which is what CompareOrdinal does.
        return string.CompareOrdinal(left, right) < 0 ? -1 : 1;
    }

    /// <summary>
    /// <c>+m[n]</c> followed by semver's <c>MAX_SAFE_INTEGER</c> guard. The digit runs are bounded by the
    /// pattern, so parsing as an integer cannot overflow a <see cref="BigInteger"/>; the guard is what
    /// rejects 17-digit components.
    /// </summary>
    private static bool TryComponent(string value, out long component)
    {
        var parsed = BigInteger.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (parsed > MaxSafeInteger)
        {
            component = 0;
            return false;
        }

        component = (long)parsed;
        return true;
    }
}
