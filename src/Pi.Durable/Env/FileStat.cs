using System.Runtime.InteropServices;

namespace Pi.Durable.Env;

/// <summary>
/// 文件元数据读取。TS 侧用 <c>lstat</c>/<c>stat</c>；Unix 侧经由 libc 的 <c>lstat</c>/<c>stat</c> 原始缓冲区取
/// kind/size/mtime（不跟随/跟随符号链接），Windows 侧用托管 API（末级为符号链接时 .NET 会跟随目标取
/// size/mtime，kind 仍判为 Symlink —— 已记录为移植差异）。
/// </summary>
internal static class FileStat
{
    public enum EntryKind { File, Directory, Symlink, Other }

    public readonly record struct Entry(EntryKind Kind, long Size, double MtimeMs);

    /// <summary>0 = 直接 stat/lstat 符号（glibc ≥ 2.33、musl、macOS），1 = __xstat/__lxstat 回退，-1 = 已定论。</summary>
    private static int _mode = 0;
    private static bool _resolved;

    /// <summary>不跟随末级符号链接的元数据（等价 TS lstat）。</summary>
    public static Entry? Lstat(string path) => StatCore(path, follow: false);

    /// <summary>跟随符号链接的元数据（等价 TS stat）。</summary>
    public static Entry? Follow(string path) => StatCore(path, follow: true);

    private static Entry? StatCore(string path, bool follow)
    {
        if (!OperatingSystem.IsWindows())
        {
            var unix = UnixStat(path, follow);
            return unix ?? ManagedFallback(path, follow);
        }
        return ManagedWindows(path);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "lstat")]
    private static extern int LstatRaw(string path, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "stat")]
    private static extern int StatRaw(string path, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "__lxstat")]
    private static extern int LXStat(int version, string path, byte[] buf);

    [DllImport("libc", SetLastError = true, EntryPoint = "__xstat")]
    private static extern int XStat(int version, string path, byte[] buf);

    private static Entry? UnixStat(string path, bool follow)
    {
        try
        {
            var buf = new byte[256];
            int rc;
            var macOs = OperatingSystem.IsMacOS();
            if (!_resolved)
            {
                rc = TryCall(path, buf, follow, macOs);
            }
            else if (_mode == 0)
            {
                rc = follow ? StatRaw(path, buf) : LstatRaw(path, buf);
            }
            else
            {
                rc = follow ? XStat(1, path, buf) : LXStat(1, path, buf);
            }
            return rc == 0 ? FromUnixBuf(buf, macOs) : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        catch (TypeLoadException) { return null; }
    }

    private static int TryCall(string path, byte[] buf, bool follow, bool macOs)
    {
        try
        {
            var rc = follow ? StatRaw(path, buf) : LstatRaw(path, buf);
            if (rc == 0 || (rc == -1 && Marshal.GetLastWin32Error() is not (38 or 61)))
            {
                _mode = 0;
                _resolved = true;
                return rc;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // 继续走 __xstat 回退。
        }
        try
        {
            var rc = follow ? XStat(1, path, buf) : LXStat(1, path, buf);
            if (rc == -1 && Marshal.GetLastWin32Error() == 22 /* EINVAL：版本号不对 */)
                rc = follow ? XStat(3, path, buf) : LXStat(3, path, buf);
            _mode = 1;
            _resolved = true;
            return rc;
        }
        catch (EntryPointNotFoundException)
        {
            _mode = -1;
            _resolved = true;
            return -1;
        }
    }

    private static Entry FromUnixBuf(byte[] buf, bool macOs)
    {
        long size, mtimSec, mtimNsec;
        int mode;
        if (macOs)
        {
            // Darwin struct stat：dev@0(4) ino@8(8) mode@16(2) atim@32 mtim@48 ctim@64 birth@80 size@96。
            mode = BitConverter.ToUInt16(buf, 16);
            mtimSec = BitConverter.ToInt64(buf, 48);
            mtimNsec = BitConverter.ToInt64(buf, 56);
            size = BitConverter.ToInt64(buf, 96);
        }
        else
        {
            // Linux x64/arm64 struct stat：dev@0 ino@8 nlink@16 mode@24 uid@28 gid@32 pad@36 rdev@40
            // size@48 blksize@56 blocks@64 atim@72 mtim@88 ctim@104。
            mode = BitConverter.ToInt32(buf, 24);
            size = BitConverter.ToInt64(buf, 48);
            mtimSec = BitConverter.ToInt64(buf, 88);
            mtimNsec = BitConverter.ToInt64(buf, 96);
        }
        var kind = (mode & 0xF000) switch
        {
            0x8000 => EntryKind.File,
            0x4000 => EntryKind.Directory,
            0xA000 => EntryKind.Symlink,
            _ => EntryKind.Other,
        };
        return new Entry(kind, size, mtimSec * 1000.0 + mtimNsec / 1_000_000.0);
    }

    // ---------- 托管回退 / Windows ----------

    private static Entry? ManagedFallback(string path, bool follow) => ManagedWindows(path);

    private static Entry? ManagedWindows(string path)
    {
        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }

            var isSymlink = (attributes & FileAttributes.ReparsePoint) != 0;
            if (isSymlink)
            {
                // Windows 上 .NET 对符号链接的 size/mtime 来自目标；kind 仍判为 Symlink（移植差异已记录）。
                return new Entry(EntryKind.Symlink, 0, MtimeOf(path));
            }
            var isDir = (attributes & FileAttributes.Directory) != 0;
            return new Entry(
                isDir ? EntryKind.Directory : EntryKind.File,
                isDir ? 0 : new System.IO.FileInfo(path).Length,
                MtimeOf(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static double MtimeOf(string path)
    {
        var utc = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds();
    }
}
