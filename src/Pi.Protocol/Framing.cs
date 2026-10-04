using System.Buffers.Binary;

namespace Pi.Protocol;

/// <summary>Raised for any framing rule violation.</summary>
public sealed class FrameError(string message) : Exception(message);

/// <summary>Options for incremental frame decoding.</summary>
public sealed record FrameDecoderOptions(long? MaxFrameLength = null);

/// <summary>Frame framing helpers: unsigned 32-bit big-endian length prefix.</summary>
public static class Frame
{
    /// <summary>Default upper bound for one framed CBOR payload.</summary>
    public const long DefaultMaxFrameLength = 16 * 1024 * 1024;

    public const int FrameHeaderLength = 4;
    private const uint MaxUint32 = 0xffff_ffff;
    private const int PayloadBlockSize = 64 * 1024;

    /// <summary>Prefixes a payload with its unsigned 32-bit big-endian byte length.</summary>
    public static byte[] EncodeFrame(byte[] payload)
    {
        if (payload.LongLength > MaxUint32)
            throw new ArgumentOutOfRangeException(nameof(payload), "Frame payload exceeds the unsigned 32-bit length limit");
        var frame = new byte[FrameHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        Array.Copy(payload, 0, frame, FrameHeaderLength, payload.Length);
        return frame;
    }

    internal static long ResolveMaxFrameLength(FrameDecoderOptions? options)
    {
        var value = options?.MaxFrameLength ?? DefaultMaxFrameLength;
        if (value < 0 || value > MaxUint32)
            throw new ArgumentOutOfRangeException(nameof(FrameDecoderOptions.MaxFrameLength),
                $"maxFrameLength must be an integer between 0 and {MaxUint32}");
        return value;
    }

    /// <summary>
    /// Incrementally splits arbitrary byte chunks into length-prefixed payloads.
    /// Mirrors TS <c>FrameDecoder</c> including its open/ended/failed state machine
    /// and block-based payload assembly.
    /// </summary>
    public sealed class Decoder
    {
        private enum DecoderState { Open, Ended, Failed }

        private readonly byte[] _header = new byte[FrameHeaderLength];
        private int _headerLength;
        private readonly long _maxFrameLength;
        private List<byte[]> _payloadBlocks = [];
        private byte[]? _currentPayloadBlock;
        private int _currentPayloadBlockLength;
        private long? _expectedPayloadLength;
        private long _payloadLength;
        private DecoderState _state = DecoderState.Open;

        public Decoder(FrameDecoderOptions? options = null)
            => _maxFrameLength = ResolveMaxFrameLength(options);

        /// <summary>Consumes a chunk and returns any complete payloads.</summary>
        public IReadOnlyList<byte[]> Push(byte[] chunk)
        {
            switch (_state)
            {
                case DecoderState.Ended: throw new FrameError("Frame decoder has ended");
                case DecoderState.Failed: throw new FrameError("Frame decoder has failed");
            }

            var frames = new List<byte[]>();
            var chunkOffset = 0;
            while (chunkOffset < chunk.Length)
            {
                if (_expectedPayloadLength is null)
                {
                    var headerBytes = Math.Min(FrameHeaderLength - _headerLength, chunk.Length - chunkOffset);
                    Array.Copy(chunk, chunkOffset, _header, _headerLength, headerBytes);
                    _headerLength += headerBytes;
                    chunkOffset += headerBytes;
                    if (_headerLength < FrameHeaderLength) continue;

                    var frameLength = (long)BinaryPrimitives.ReadUInt32BigEndian(_header);
                    _headerLength = 0;
                    if (frameLength > _maxFrameLength)
                        Fail($"Frame length {frameLength} exceeds configured limit of {_maxFrameLength}");
                    if (frameLength == 0)
                    {
                        frames.Add([]);
                        continue;
                    }
                    _expectedPayloadLength = frameLength;
                    _payloadBlocks = [];
                    _currentPayloadBlock = null;
                    _currentPayloadBlockLength = 0;
                    _payloadLength = 0;
                }

                var expected = _expectedPayloadLength!.Value;
                while (chunkOffset < chunk.Length && _payloadLength < expected)
                {
                    var block = _currentPayloadBlock;
                    if (block is null || _currentPayloadBlockLength == block.Length)
                    {
                        block = new byte[(int)Math.Min(PayloadBlockSize, expected - _payloadLength)];
                        _payloadBlocks.Add(block);
                        _currentPayloadBlock = block;
                        _currentPayloadBlockLength = 0;
                    }
                    var payloadBytes = Math.Min(block.Length - _currentPayloadBlockLength, chunk.Length - chunkOffset);
                    Array.Copy(chunk, chunkOffset, block, _currentPayloadBlockLength, payloadBytes);
                    _currentPayloadBlockLength += payloadBytes;
                    _payloadLength += payloadBytes;
                    chunkOffset += payloadBytes;
                }

                if (_payloadLength == expected)
                {
                    frames.Add(_payloadBlocks.Count == 1 ? _payloadBlocks[0] : Concat(_payloadBlocks, expected));
                    _payloadBlocks = [];
                    _currentPayloadBlock = null;
                    _currentPayloadBlockLength = 0;
                    _expectedPayloadLength = null;
                    _payloadLength = 0;
                }
            }
            return frames;
        }

        /// <summary>Verifies the stream ended on a frame boundary.</summary>
        public void End()
        {
            switch (_state)
            {
                case DecoderState.Ended: throw new FrameError("Frame decoder has ended");
                case DecoderState.Failed: throw new FrameError("Frame decoder has failed");
            }
            if (_headerLength != 0 || _expectedPayloadLength is not null)
                Fail("Truncated frame at end of stream");
            _state = DecoderState.Ended;
        }

        private static byte[] Concat(List<byte[]> blocks, long totalLength)
        {
            var payload = new byte[totalLength];
            var offset = 0;
            foreach (var block in blocks)
            {
                Array.Copy(block, 0, payload, offset, block.Length);
                offset += block.Length;
            }
            return payload;
        }

        private void Fail(string message)
        {
            _state = DecoderState.Failed;
            _headerLength = 0;
            _payloadBlocks = [];
            _currentPayloadBlock = null;
            _currentPayloadBlockLength = 0;
            _expectedPayloadLength = null;
            _payloadLength = 0;
            throw new FrameError(message);
        }
    }
}