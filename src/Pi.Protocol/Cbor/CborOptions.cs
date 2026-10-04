using System.Text;

namespace Pi.Protocol.Cbor;

/// <summary>Safe defaults for untrusted protocol payloads. Mirrors TS <c>CborOptions</c>.</summary>
public sealed record CborOptions(
    long? MaxByteLength = null,
    long? MaxContainerLength = null,
    long? MaxDepth = null);

internal readonly record struct ResolvedCborOptions(long MaxByteLength, long MaxContainerLength, long MaxDepth);

/// <summary>Raised for any CBOR encoding/decoding rule violation.</summary>
public sealed class CborError : Exception
{
    public CborError(string message) : base(message) { }

    public CborError(string message, Exception innerException) : base(message, innerException) { }
}

public static partial class CborCodec
{
    public const long Uint32Base = 0x1_0000_0000L;
    public const long MaxUint32 = 0xffff_ffffL;
    private const long MaxConfiguredDepth = 512;

    /// <summary>Maximum encoded input/output bytes and maximum byte/text string length.</summary>
    public const long DefaultMaxByteLength = 16 * 1024 * 1024;

    /// <summary>Maximum number of elements in an array or entries in a map.</summary>
    public const long DefaultMaxContainerLength = 1_000_000;

    /// <summary>Maximum recursive item depth.</summary>
    public const long DefaultMaxDepth = 64;

    /// <summary>JS <c>Number.MAX_SAFE_INTEGER</c>: the port keeps integer wire compatibility.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static ResolvedCborOptions Resolve(CborOptions? options)
    {
        static long ResolveLimit(string name, long value, long maximum)
        {
            if (value < 0 || value > maximum)
                throw new ArgumentOutOfRangeException(name, $"{name} must be an integer between 0 and {maximum}");
            return value;
        }

        return new ResolvedCborOptions(
            ResolveLimit(nameof(CborOptions.MaxByteLength), options?.MaxByteLength ?? DefaultMaxByteLength, MaxUint32),
            ResolveLimit(nameof(CborOptions.MaxContainerLength), options?.MaxContainerLength ?? DefaultMaxContainerLength, MaxUint32),
            ResolveLimit(nameof(CborOptions.MaxDepth), options?.MaxDepth ?? DefaultMaxDepth, MaxConfiguredDepth));
    }
}
