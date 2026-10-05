using System.Text;
using System.Text.Json.Nodes;

namespace Pi.Ai.Api;

/// <summary>AWS event-stream 解码出的一条消息（头字典 + JSON 载荷）。</summary>
public sealed record AwsEventMessage(
    IReadOnlyDictionary<string, string> Headers,
    JsonObject? Payload);

/// <summary>
/// AWS event-stream（vnd.amazon.eventstream）二进制帧解码器。
/// 对应 TS 侧 @aws-sdk/eventstream-marshaller 的解码行为（Bedrock ConverseStream 响应流）：
/// [total length:4][headers length:4][prelude CRC:4][headers][payload][message CRC:4]，
/// 头值为类型化编码（bool/int32/int64/byteBuf/string/timestamp/uuid）。
/// </summary>
public static class AwsEventStream
{
    /// <summary>解码全部消息（CRC 校验失败/截断抛 InvalidDataException）。</summary>
    public static List<AwsEventMessage> Decode(System.IO.Stream stream)
    {
        var messages = new List<AwsEventMessage>();
        var buffer = ReadAll(stream);
        var offset = 0;
        while (offset + 12 <= buffer.Length)
        {
            var totalLength = ReadInt32(buffer, offset);
            var headersLength = ReadInt32(buffer, offset + 4);
            if (totalLength < 0 || offset + totalLength > buffer.Length)
            {
                throw new InvalidDataException("Truncated AWS event-stream frame");
            }
            var payloadLength = totalLength - headersLength - 16;
            if (payloadLength < 0)
            {
                throw new InvalidDataException("Invalid AWS event-stream frame lengths");
            }

            var headersStart = offset + 12;
            var payloadStart = headersStart + headersLength;
            var headers = DecodeHeaders(buffer, headersStart, headersLength);

            JsonObject? payload = null;
            if (payloadLength > 0)
            {
                var payloadText = Encoding.UTF8.GetString(buffer, payloadStart, payloadLength);
                payload = JsonNode.Parse(payloadText) as JsonObject;
            }
            messages.Add(new AwsEventMessage(headers, payload));
            offset += totalLength;
        }
        return messages;
    }

    private static byte[] ReadAll(System.IO.Stream stream)
    {
        using var memory = new System.IO.MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static int ReadInt32(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    private static Dictionary<string, string> DecodeHeaders(byte[] buffer, int start, int length)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var offset = start;
        var end = start + length;
        while (offset < end)
        {
            var nameLength = buffer[offset++];
            var name = Encoding.UTF8.GetString(buffer, offset, nameLength);
            offset += nameLength;
            var valueType = buffer[offset++];
            switch (valueType)
            {
                case 0: // TRUE
                    headers[name] = "true";
                    break;
                case 1: // FALSE
                    headers[name] = "false";
                    break;
                case 2: // BYTE
                    headers[name] = buffer[offset].ToString();
                    offset += 1;
                    break;
                case 3: // SHORT
                    headers[name] = ((short)((buffer[offset] << 8) | buffer[offset + 1])).ToString();
                    offset += 2;
                    break;
                case 4: // INT32
                    headers[name] = ReadInt32(buffer, offset).ToString();
                    offset += 4;
                    break;
                case 5: // INT64
                case 8: // TIMESTAMP（毫秒）
                    headers[name] = ((long)ReadInt32(buffer, offset) << 32 | (uint)ReadInt32(buffer, offset + 4)).ToString();
                    offset += 8;
                    break;
                case 6: // BYTE_BUF
                {
                    var valueLength = ReadInt32(buffer, offset);
                    offset += 4 + valueLength;
                    headers[name] = $"<{valueLength} bytes>";
                    break;
                }
                case 7: // STRING
                {
                    var valueLength = ReadInt32(buffer, offset);
                    offset += 4;
                    headers[name] = Encoding.UTF8.GetString(buffer, offset, valueLength);
                    offset += valueLength;
                    break;
                }
                case 9: // UUID
                    offset += 16;
                    headers[name] = "<uuid>";
                    break;
                default:
                    throw new InvalidDataException($"Unknown AWS event-stream header value type: {valueType}");
            }
        }
        return headers;
    }
}
