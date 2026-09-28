using EasyImageImporter.Core.IO;

namespace EasyImageImporter.Core.Import;

public sealed record DiscardedSpace(int Files, long Bytes);

/// <summary>
/// Puts the photos the user sorted away in the recycle bin once they have been kept long enough
/// (<see cref="AppSettings.DiscardedKeepDays"/>). Nothing is deleted: they can be restored from the
/// bin, and the system empties it in its own time. Only files the app itself sorted away are
/// touched — the list of moves says exactly which ones those are — so anything the user put there
/// is left alone, as are the photos they kept.
/// </summary>
public sealed class DiscardedCleanup(IFileSystem fs, ImportStore store, AppSettings settings, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How long an import's sorted-away photos are kept, or null for good.</summary>
    public TimeSpan? KeepFor => settings.DiscardedKeepDays is { } days
        // Never take away what the user could still undo: an undo puts these photos back.
        ? TimeSpan.FromDays(days) > ImportUndo.Window ? TimeSpan.FromDays(days) : ImportUndo.Window
        : null;

    /// <summary>Clears out every import that has kept them long enough. Returns how much was moved.</summary>
    public DiscardedSpace Run()
    {
        if (KeepFor is not { } keepFor) return new DiscardedSpace(0, 0);

        var (files, bytes) = (0, 0L);
        foreach (var import in store.GetImportsWithDiscarded())
        {
            if (_time.GetUtcNow().UtcDateTime - import.CreatedUtc <= keepFor) continue;

            foreach (var folder in DiscardedPaths(import).Where(p => SizeOf(p) is not null)
                         .GroupBy(p => Path.GetDirectoryName(p)!, StringComparer.OrdinalIgnoreCase))
            {
                // One folder per place in the bin reads better than loose photos, but only when
                // everything in the folder is ours.
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
        {
            var space = Measure(import);
            files += space.Files;
            bytes += space.Bytes;
        }
        return new DiscardedSpace(files, bytes);
    }

    /// <summary>What is still kept of one import's sorted-away photos.</summary>
    public DiscardedSpace Measure(ImportRecord import)
    {
        var (files, bytes) = (0, 0L);
        foreach (var path in DiscardedPaths(import))
        {
            if (SizeOf(path) is not { } size) continue;
            files++;
            bytes += size;
        }
        return new DiscardedSpace(files, bytes);
    }

    /// <summary>The folders one import's sorted-away photos are kept in, while they are there.</summary>
    public IReadOnlyList<string> Folders(ImportRecord import) =>
        DiscardedPaths(import).Where(p => SizeOf(p) is not null)
            .Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>When one import's sorted-away photos go to the bin, or null if they are kept for good.</summary>
    public DateTime? ClearedAfterUtc(ImportRecord import) => KeepFor is { } keepFor ? import.CreatedUtc + keepFor : null;

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

    /// <summary>The moves of sorted-away photos that were actually carried out.</summary>
    private IEnumerable<string> DiscardedPaths(ImportRecord import)
    {
        var discarded = store.GetDiscardedFileIds(import.SessionId);
        return store.GetMoves(import.Id)
            .Where(m => m is { Done: true, Undone: false } && discarded.Contains(m.FileId))
            .Select(m => m.ToPath);
    }
}
