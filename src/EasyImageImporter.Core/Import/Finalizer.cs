using System.Globalization;
using System.Text;
using EasyImageImporter.Core.IO;

namespace EasyImageImporter.Core.Import;

/// <summary>
/// Moves verified files from staging into the archive folder.
/// M1: a flat folder, <c>Viltkamera\yyyy\yyyy-MM-dd Import\</c>, with the camera's own file names.
/// The whole move plan is written to the database before the first file moves, so a crash
/// halfway through is finished by simply running <see cref="Run"/> again.
/// </summary>
public sealed class Finalizer(IFileSystem fs, ImportStore store, AppPaths paths, TimeProvider? time = null)
{
    public const string SummaryFileName = "OM DENNE MAPPEN.txt";

    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly VerifiedCopier _copier = new(fs);

    /// <summary>Returns the import, or null when every file was already archived before (nothing new to store).</summary>
    public ImportRecord? Run(long sessionId)
    {
        var session = store.GetSession(sessionId);
        var import = session.State switch
        {
            SessionState.Copied => Plan(session),
            SessionState.Finalizing => store.GetImportForSession(sessionId),
            _ => throw new InvalidOperationException($"Kan ikke lagre en økt i tilstanden {session.State}."),
        };

        if (import is null)
        {
            store.SetState(sessionId, SessionState.Imported);
            return null;
        }

        fs.CreateDirectory(import.FolderPath);
        foreach (var move in store.GetMoves(import.Id).Where(m => !m.Done))
        {
            MoveOne(move);
            store.MarkMoveDone(move.ImportId, move.FileId);
        }

        WriteSummary(import, session);
        store.CompleteImport(import.Id, sessionId);
        return store.GetImportForSession(sessionId);
    }

    private ImportRecord? Plan(Session session)
    {
        var files = store.GetFiles(session.Id, FileStatus.Verified);
        if (files.Count == 0) return null;

        var today = _time.GetLocalNow();
        var yearDir = Path.Combine(paths.ArchiveRoot, today.ToString("yyyy", CultureInfo.InvariantCulture));
        var folder = UniquePath(Path.Combine(yearDir, $"{today:yyyy-MM-dd} Import"), fs.DirectoryExists);

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = files.Select(f =>
        {
            var from = Path.Combine(paths.StagingDir(session.Id), f.StagingName!);
            var name = UniqueName(f.FileName, taken);
            return (f.Id, from, Path.Combine(folder, name));
        }).ToList();

        return store.CreateImportPlan(session.Id, folder, moves);
    }

    private void MoveOne(ImportMove move)
    {
        // Already moved before a crash, but not yet recorded.
        if (!fs.FileExists(move.FromPath) && fs.FileExists(move.ToPath)) return;

        if (fs.IsSameVolume(move.FromPath, Path.GetDirectoryName(move.ToPath)!))
        {
            fs.Move(move.FromPath, move.ToPath);
            return;
        }

        // Different disk: copy, prove the copy, then remove the staging file.
        var temp = move.ToPath + ".tmp";
        if (fs.FileExists(temp)) fs.Delete(temp);
        var result = _copier.Copy(move.FromPath, temp);
        if (result.Status != CopyStatus.Verified)
            throw new IOException($"Kopien av {Path.GetFileName(move.ToPath)} kunne ikke kontrolleres.");
        fs.Move(temp, move.ToPath);
        fs.Delete(move.FromPath);
    }

    private void WriteSummary(ImportRecord import, Session session)
    {
        var files = store.GetFiles(session.Id, FileStatus.Verified);
        var first = files.Min(f => f.MtimeUtc).ToLocalTime();
        var last = files.Max(f => f.MtimeUtc).ToLocalTime();
        var text = new StringBuilder()
            .AppendLine("Bilder fra viltkamera")
            .AppendLine()
            .AppendLine(string.Create(Norwegian, $"Importert:   {import.CreatedUtc.ToLocalTime():d. MMMM yyyy 'kl.' HH:mm}"))
            .AppendLine(string.Create(Norwegian, $"Antall:      {files.Count:N0} bilder"))
            .AppendLine(string.Create(Norwegian, $"Tatt:        {first:d. MMMM yyyy} – {last:d. MMMM yyyy}"))
            .AppendLine()
            .AppendLine("Denne filen er laget av EasyImageImporter, og kan leses uten programmet.")
            .ToString()
            .ReplaceLineEndings("\r\n");

        // UTF-8 with BOM so old Notepad shows æøå correctly.
        fs.WriteAllText(Path.Combine(import.FolderPath, SummaryFileName), text, new UTF8Encoding(true));
    }

    internal static string UniquePath(string path, Func<string, bool> exists)
    {
        if (!exists(path)) return path;
        for (var i = 2; ; i++)
        {
            var candidate = $"{path} ({i})";
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>Cameras restart their counters, so two folders on one card can both hold IMAG0001.JPG.</summary>
    internal static string UniqueName(string fileName, ISet<string> taken)
    {
        var name = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; !taken.Add(name); i++) name = $"{stem} ({i}){ext}";
        return name;
    }
}
