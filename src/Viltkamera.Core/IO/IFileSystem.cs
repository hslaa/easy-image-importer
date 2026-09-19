namespace Viltkamera.Core.IO;

public readonly record struct FileMeta(long Size, DateTime LastWriteUtc);

/// <summary>
/// The only way Core touches the disk. Exists so the safety tests can inject
/// failures (card pulled, disk full, corrupted writes) at any point.
/// </summary>
public interface IFileSystem
{
    /// <summary>All non-hidden, non-system files under <paramref name="root"/>, recursively.</summary>
    IEnumerable<string> EnumerateFiles(string root);

    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    FileMeta GetFileMeta(string path);

    Stream OpenRead(string path);

    /// <summary>
    /// Opens a file for reading such that the bytes come from the storage device, not the OS
    /// page cache. Used to verify what was actually written. Best effort outside Windows.
    /// </summary>
    Stream OpenReadForVerify(string path);

    /// <summary>Creates a new file with write-through. Fails if the file exists.</summary>
    Stream CreateNew(string path);

    /// <summary>Rename/move without overwriting. Fails if <paramref name="to"/> exists.</summary>
    void Move(string from, string to);

    void Delete(string path);

    /// <summary>Writes a small text file, replacing any existing one.</summary>
    void WriteAllText(string path, string contents, System.Text.Encoding encoding);

    long GetAvailableFreeSpace(string path);

    bool IsSameVolume(string pathA, string pathB);
}
