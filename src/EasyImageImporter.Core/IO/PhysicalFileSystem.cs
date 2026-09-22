using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EasyImageImporter.Core.IO;

public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    public IEnumerable<string> EnumerateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", Recursive);

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public FileMeta GetFileMeta(string path)
    {
        var info = new FileInfo(path);
        return new FileMeta(info.Length, info.LastWriteTimeUtc);
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

    public Stream OpenReadForVerify(string path) =>
        OperatingSystem.IsWindows()
            ? new UnbufferedReadStream(path)
            // macOS is the dev platform only; a normal read is good enough there.
            : OpenRead(path);

    public Stream CreateNew(string path) =>
        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough);

    public void Move(string from, string to) => File.Move(from, to, overwrite: false);

    public void Delete(string path) => File.Delete(path);

    public bool DeleteDirectoryIfEmpty(string path)
    {
        if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any()) return false;
        Directory.Delete(path, recursive: false);
        return true;
    }

    public void WriteAllText(string path, string contents, System.Text.Encoding encoding) =>
        File.WriteAllText(path, contents, encoding);

    public long GetAvailableFreeSpace(string path)
    {
        // Windows wants a drive root; on Unix any existing path works (statfs).
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()) return new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
        while (!Directory.Exists(full)) full = Path.GetDirectoryName(full)!;
        return new DriveInfo(full).AvailableFreeSpace;
    }

    public bool MoveToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return true;
        if (OperatingSystem.IsWindows()) return RecycleOnWindows(path);
        if (OperatingSystem.IsMacOS()) return MoveToMacTrash(path);
        return false;
    }

    /// <summary>The shell's own delete, with "allow undo": exactly what Explorer's Delete does.</summary>
    [SupportedOSPlatform("windows")]
    private static bool RecycleOnWindows(string path)
    {
        const uint Delete = 0x0003;
        const ushort AllowUndo = 0x0040, NoConfirmation = 0x0010, Silent = 0x0004, NoErrorUi = 0x0400;
        var operation = new ShFileOpStruct
        {
            Func = Delete,
            From = Path.GetFullPath(path) + '\0' + '\0', // the shell takes a double-null-terminated list
            Flags = AllowUndo | NoConfirmation | Silent | NoErrorUi,
        };
        return SHFileOperationW(ref operation) == 0 && !operation.Aborted;
    }

    [SupportedOSPlatform("macos")]
    private static bool MoveToMacTrash(string path)
    {
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        Directory.CreateDirectory(trash);
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        var target = Path.Combine(trash, name);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(name)} {n}{Path.GetExtension(name)}");
        if (Directory.Exists(path)) Directory.Move(path, target);
        else File.Move(path, target);
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Func;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs unsafe code for this struct; one call doesn't warrant it.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);
#pragma warning restore SYSLIB1054

    public bool IsSameVolume(string pathA, string pathB) =>
        string.Equals(MountPointOf(pathA), MountPointOf(pathB),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>The longest mount point that contains <paramref name="path"/>.</summary>
    private static string MountPointOf(string path)
    {
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Drives.GetDrives()
            .Select(d => d.RootDirectory.FullName)
            .Where(root => full.StartsWith(root, comparison)
                           && (full.Length == root.Length
                               || root.EndsWith(Path.DirectorySeparatorChar)
                               || full[root.Length] == Path.DirectorySeparatorChar))
            .MaxBy(root => root.Length)
            ?? Path.GetPathRoot(full)
            ?? full;
    }
}
