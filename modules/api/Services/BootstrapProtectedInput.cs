using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

internal static class BootstrapProtectedInput
{
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Stat(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx stat);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    public static FileStream OpenRead(string path)
    {
        if (!OperatingSystem.IsLinux()) throw BootstrapException.Conflict();
        // Linux O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC: reject symlinks atomically,
        // and never hang while opening a FIFO before we can verify its type.
        var fd = Open(path, 0x20000 | 0x800 | 0x80000);
        if (fd < 0) throw BootstrapException.Conflict();
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            const uint fields = 1 | 2 | 8; // STATX_TYPE | STATX_MODE | STATX_UID
            if (Stat(fd, "", 0x1000, fields, out var stat) != 0 ||
                (stat.Mask & fields) != fields || (stat.Mode & 0xF000) != 0x8000 ||
                stat.UserId != EffectiveUserId() ||
                (stat.Mode & 0x0FFF & ~0x180) != 0 || (stat.Mode & 0x100) == 0)
                throw BootstrapException.Conflict();
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
