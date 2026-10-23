using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Translation.Bridge.Core.Interface;

namespace Translation.Bridge.Core.Services;

/// <summary>目錄連結：Windows 用 junction（mount point reparse point，不需要系統管理員），其他平台用符號連結（讓 Linux 測試能跑）。</summary>
public sealed class DirectoryJunction : IDirectoryJunction
{
    /// <summary>path 是目錄連結時回傳目標的完整路徑；不存在或是一般資料夾時回傳 null。</summary>
    public string? GetTarget(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0 || info.LinkTarget is not { } target)
        {
            return null;
        }

        // junction 的 SubstituteName 會帶 \??\ 前綴（.NET 大多已去掉，保險再清一次）
        if (target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            target = target[4..];
        }

        return Path.GetFullPath(target);
    }

    /// <summary>在 linkPath 建立指向 targetPath 的目錄連結；linkPath 不能已經存在，targetPath 必須已存在。失敗擲例外。</summary>
    public void Create(string linkPath, string targetPath)
    {
        if (OperatingSystem.IsWindows())
        {
            CreateWindowsJunction(linkPath, targetPath);
        }
        else
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
    }

    private const uint GenericWrite = 0x40000000;

    private const uint OpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FileFlagOpenReparsePoint = 0x00200000;

    private const uint FsctlSetReparsePoint = 0x000900A4;

    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>建 Windows junction：建出空資料夾後把 mount point reparse point 寫進去；失敗時刪掉空資料夾再擲 Win32Exception。</summary>
    [SupportedOSPlatform("windows")]
    private static void CreateWindowsJunction(string linkPath, string targetPath)
    {
        Directory.CreateDirectory(linkPath);
        var handle = CreateFileW(linkPath, GenericWrite, 0, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            Directory.Delete(linkPath);
            throw new Win32Exception(error);
        }

        int ioctlError;
        using (handle)
        {
            var buffer = BuildReparseBuffer(Path.GetFullPath(targetPath));
            if (DeviceIoControl(handle, FsctlSetReparsePoint, buffer, (uint)buffer.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                return;
            }

            ioctlError = Marshal.GetLastWin32Error();
        }

        // handle 關閉後才能刪掉空資料夾（開著時刪會變成「被另一個程序使用」，蓋掉真正的錯誤）
        Directory.Delete(linkPath);
        throw new Win32Exception(ioctlError);
    }

    /// <summary>組 REPARSE_DATA_BUFFER（IO_REPARSE_TAG_MOUNT_POINT）：SubstituteName 是 \??\ + 目標完整路徑、PrintName 是目標完整路徑，
    /// 兩個字串都是 UTF-16 且各自結尾有 NUL。</summary>
    private static byte[] BuildReparseBuffer(string targetFullPath)
    {
        var substituteName = Encoding.Unicode.GetBytes(@"\??\" + targetFullPath);
        var printName = Encoding.Unicode.GetBytes(targetFullPath);

        // 標頭 8（Tag、DataLength、Reserved）+ mount point 標頭 8（兩組 offset/length，沒有 Flags）+ 兩個含 NUL 的字串；
        // Length 欄位是不含 NUL 的位元組數
        const int pathBufferOffset = 16;
        var buffer = new byte[pathBufferOffset + substituteName.Length + 2 + printName.Length + 2];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes((ushort)substituteName.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substituteName.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)printName.Length).CopyTo(buffer, 14);
        substituteName.CopyTo(buffer, pathBufferOffset);
        printName.CopyTo(buffer, pathBufferOffset + substituteName.Length + 2);
        return buffer;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
