using EasyImageImporter.Core.IO;

namespace EasyImageImporter.Core.Import;

public sealed record DiscardedSpace(int Files, long Bytes);

/// <summary>
/// Puts the photos the user sorted away in the recycle bin, once the import can no longer be
/// undone. Nothing is deleted: they can be restored from the bin, and the system empties it in
/// its own time. Only files the app itself put in "Sortert bort" are touched — the list of moves
/// says exactly which ones those are — so anything the user put there is left alone, as are the
/// photos they kept.
/// </summary>
public sealed class DiscardedCleanup(IFileSystem fs, ImportStore store, AppSettings settings, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Clears out every import past the undo window. Returns how much was moved.</summary>
    public DiscardedSpace Run()
    {
        if (!settings.DiscardedToRecycleBin) return new DiscardedSpace(0, 0);

        var (files, bytes) = (0, 0L);
        foreach (var import in store.GetImportsWithDiscarded())
        {
            // Never take away what the user could still undo: an undo puts these photos back.
            if (_time.GetUtcNow().UtcDateTime - import.CreatedUtc <= ImportUndo.Window) continue;

            foreach (var folder in DiscardedPaths(import).Where(p => SizeOf(p) is not null)
                         .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
            {
                // One "Sortert bort" in the bin per import reads better than loose photos, but
                // only when everything in the folder is ours.
                if (fs.EnumerateFiles(folder.Key).Count() == folder.Count())
                {
                    var size = folder.Sum(p => SizeOf(p) ?? 0);
                    if (!Move(folder.Key)) continue;
                    files += folder.Count();
                    bytes += size;
                    continue;
                }

                foreach (var path in folder)
                {
                    var size = SizeOf(path) ?? 0;
                    if (!Move(path)) continue;
                    files++;
                    bytes += size;
                }
                fs.DeleteDirectoryIfEmpty(folder.Key);
            }

            if (DiscardedPaths(import).All(p => SizeOf(p) is null)) store.MarkDiscardedCleared(import.Id);
        }
        return new DiscardedSpace(files, bytes);
    }

    /// <summary>How much space the sorted-away photos still take up.</summary>
    public DiscardedSpace Measure()
    {
        var (files, bytes) = (0, 0L);
        foreach (var import in store.GetImportsWithDiscarded())
            foreach (var path in DiscardedPaths(import))
            {
                if (SizeOf(path) is not { } size) continue;
                files++;
                bytes += size;
            }
        return new DiscardedSpace(files, bytes);
    }

    private bool Move(string path)
    {
        try
        {
            return fs.MoveToRecycleBin(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // in use or read-only: leave it, and try again next time
        }
    }

    private long? SizeOf(string path)
    {
        try
        {
            return fs.FileExists(path) ? fs.GetFileMeta(path).Size : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The moves that ended up in a "Sortert bort" folder, and were actually carried out.</summary>
    private IEnumerable<string> DiscardedPaths(ImportRecord import) =>
        store.GetMoves(import.Id)
            .Where(m => m is { Done: true, Undone: false })
            .Select(m => m.ToPath)
            .Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == Finalizer.DiscardedFolderName);
}
