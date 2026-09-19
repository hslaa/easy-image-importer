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
