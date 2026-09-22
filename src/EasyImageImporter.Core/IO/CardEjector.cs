using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace EasyImageImporter.Core.IO;

public enum EjectResult
{
    /// <summary>Windows is done with the card: it can be taken out.</summary>
    Ejected,
    /// <summary>Something else has the card open (an Explorer window, antivirus). Try again later.</summary>
    InUse,
    /// <summary>It didn't work for another reason; the card is still safe to take out once idle.</summary>
    Failed,
}

public interface ICardEjector
{
    EjectResult Eject(string root);
}

/// <summary>
/// "Løs ut": makes the system finish with a card before it is pulled out. Windows defaults to
/// quick removal for cards, but a PC set to "better performance", or a program still busy with
/// the card, can leave it half written; ejecting settles that either way.
/// </summary>
public sealed class CardEjector : ICardEjector
{
    public EjectResult Eject(string root)
    {
        if (!Directory.Exists(root)) return EjectResult.Ejected; // already taken out
        if (OperatingSystem.IsWindows()) return EjectWindows(root);
        if (OperatingSystem.IsMacOS()) return EjectMac(root);
        return EjectResult.Failed;
    }

    private static EjectResult EjectMac(string root)
    {
        using var diskutil = Process.Start(new ProcessStartInfo("diskutil", ["eject", root])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        diskutil.WaitForExit(TimeSpan.FromSeconds(30));
        var error = diskutil.StandardError.ReadToEnd();
        return diskutil.ExitCode == 0 ? EjectResult.Ejected
            : error.Contains("in use", StringComparison.OrdinalIgnoreCase) || error.Contains("busy", StringComparison.OrdinalIgnoreCase)
                ? EjectResult.InUse
                : EjectResult.Failed;
    }

    // The steps Windows itself takes (KB165721): lock the volume, which only succeeds when
    // nothing else has files open on it, dismount it, which writes out everything cached, then
    // allow removal and eject the media.
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018, FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageMediaRemoval = 0x002D4804, IoctlStorageEjectMedia = 0x002D4808;

    [SupportedOSPlatform("windows")]
    private static EjectResult EjectWindows(string root)
    {
        var drive = Path.GetPathRoot(Path.GetFullPath(root))!.TrimEnd('\\'); // "E:"
        using var volume = CreateFileW(@"\\.\" + drive, GenericRead | GenericWrite, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (volume.IsInvalid) return EjectResult.Failed;

        // Explorer and antivirus let go within moments; wait a little before calling it in use.
        var locked = false;
        for (var attempt = 0; attempt < 20 && !locked; attempt++)
        {
            locked = DeviceIoControl(volume, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!locked) Thread.Sleep(250);
        }
        if (!locked) return EjectResult.InUse;

        if (!DeviceIoControl(volume, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            return EjectResult.Failed;

        // Dismounted means written out and safe to pull. Some card readers can't eject the media
        // themselves; that doesn't matter, so these two are best effort.
        var allowRemoval = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(allowRemoval, 0);
            DeviceIoControl(volume, IoctlStorageMediaRemoval, allowRemoval, 1, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(allowRemoval);
        }
        DeviceIoControl(volume, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        return EjectResult.Ejected;
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs unsafe code; these two calls don't warrant it.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
#pragma warning restore SYSLIB1054
}
