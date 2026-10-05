using System.Text.RegularExpressions;
using Pi.Protocol;

namespace Pi.Server.Transports.Unix;

/// <summary>
/// 由一个逻辑服务器身份推导本地 Unix socket 路径。对应 TS <c>getUnixSocketPath</c>（address.ts）。
/// </summary>
public static partial class UnixSocketAddress
{
    /// <summary>serverId 必须是规范小写 UUIDv4，否则抛错。</summary>
    public static string GetUnixSocketPath(string serverId, string serverDirectory)
    {
        if (!ServerIds.IsServerId(serverId))
        {
            throw new ArgumentException("Unix serverId must be a canonical lowercase UUIDv4");
        }
        return Path.Combine(serverDirectory, $"{serverId}.sock");
    }
}
