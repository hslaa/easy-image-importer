using System.Security.Cryptography;
using System.Text;
using Viltkamera.Core.IO;

namespace Viltkamera.Core.Import;

public sealed record CardScan(string Root, string Fingerprint, IReadOnlyList<ScannedFile> Files);

/// <summary>
/// Lists the images and videos on a card and opens (or resumes) the import session for it.
/// Only media files are ever copied or erased; camera settings files and folders are left alone.
/// </summary>
public sealed class CardScanner(IFileSystem fs, ImportStore store)
{
    public static readonly IReadOnlySet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".tif", ".tiff",
        ".avi", ".mp4", ".mov", ".mts", ".m4v", ".3gp", ".wmv",
    };

    public CardScan Scan(string root)
    {
        var files = fs.EnumerateFiles(root)
            .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
            // AppleDouble sidecars that macOS leaves on FAT cards; not images.
            .Where(path => !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal))
            .Select(path =>
            {
                var meta = fs.GetFileMeta(path);
                var rel = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                return new ScannedFile(rel, meta.Size, meta.LastWriteUtc);
            })
            .OrderBy(f => f.RelPath, StringComparer.Ordinal)
            .ToList();

        return new CardScan(root, Fingerprint(files), files);
    }

    /// <summary>
    /// Resumes the unfinished session for this card if there is one (the drive letter may have
    /// changed since), otherwise starts a new one. Returns null if the card has no media files.
    /// </summary>
    public Session? OpenSession(CardScan scan, string? label)
    {
        var existing = store.FindUnfinishedSession(scan.Fingerprint) ?? FindPartlyErasedSession(scan);
        if (existing is not null)
        {
            if (existing.SourceRoot != scan.Root) store.UpdateSourceRoot(existing.Id, scan.Root);
            return store.GetSession(existing.Id);
        }

        return scan.Files.Count == 0 ? null : store.CreateSession(scan.Fingerprint, label, scan.Root, scan.Files);
    }

    /// <summary>
    /// If the app stopped halfway through erasing, the card's listing no longer matches the
    /// fingerprint. Recognise it by the files that are left.
    /// </summary>
    private Session? FindPartlyErasedSession(CardScan scan)
    {
        var onCard = scan.Files.Select(f => (f.RelPath, f.Size)).ToHashSet();
        return store.GetUnfinishedSessions()
            .Where(s => s.State == SessionState.Imported)
            .LastOrDefault(s =>
            {
                var files = store.GetFiles(s.Id);
                return files.Any(f => f.Status == FileStatus.Erased)
                       && files.Where(f => f.Status != FileStatus.Erased).Select(f => (f.RelPath, f.Size)).ToHashSet()
                           .SetEquals(onCard);
            });
    }

    /// <summary>
    /// Identifies a card by its content listing. The card is never written to before erase, so the
    /// listing stays stable while a session is in progress. Modification times are left out on
    /// purpose: FAT stores local time, which shifts with time zone and OS.
    /// </summary>
    internal static string Fingerprint(IEnumerable<ScannedFile> files)
    {
        var listing = new StringBuilder();
        foreach (var f in files) listing.Append(f.RelPath).Append('|').Append(f.Size).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(listing.ToString())));
    }
}
