namespace Viltkamera.Core.IO;

/// <summary>
/// <see cref="DriveInfo.GetDrives"/> is not thread-safe on macOS (getmntinfo shares a static
/// buffer) and crashes the process when called concurrently. The card watcher and the copier
/// both need it, so every call goes through this lock.
/// </summary>
internal static class Drives
{
    private static readonly Lock Gate = new();

    public static DriveInfo[] GetDrives()
    {
        lock (Gate) return DriveInfo.GetDrives();
    }
}
