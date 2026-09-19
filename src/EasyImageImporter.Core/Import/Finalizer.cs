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

    /// <summary>Where discarded images go: inside the import, so they can always be found again.</summary>
    public const string DiscardedFolderName = "Sortert bort";

    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SafeMover _mover = new(fs);

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
            MarkErasedIfCardIsEmpty(sessionId);
            return null;
        }

        fs.CreateDirectory(import.FolderPath);
        foreach (var move in store.GetMoves(import.Id).Where(m => !m.Done))
        {
            _mover.Move(move.FromPath, move.ToPath);
            store.MarkMoveDone(move.ImportId, move.FileId);
        }

        WriteSummary(import, session);
        store.CompleteImport(import.Id, sessionId);
        MarkErasedIfCardIsEmpty(sessionId);
        return store.GetImportForSession(sessionId);
    }

    /// <summary>Saved again after an undo, with the card already erased: nothing is left to do.</summary>
    private void MarkErasedIfCardIsEmpty(long sessionId)
    {
        var counts = store.GetCounts(sessionId);
        if (counts.Erased > 0 && counts.Erased == counts.Total) store.SetState(sessionId, SessionState.CardErased);
    }

    private ImportRecord? Plan(Session session)
    {
        var files = store.GetStagedFiles(session.Id);
        if (files.Count == 0) return null;

        var today = _time.GetLocalNow();
        var yearDir = Path.Combine(paths.ArchiveRoot, today.ToString("yyyy", CultureInfo.InvariantCulture));
        var folder = UniquePath(Path.Combine(yearDir, $"{today:yyyy-MM-dd} Import"), fs.DirectoryExists);

        var discardedFolder = Path.Combine(folder, DiscardedFolderName);
        var takenKept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenDiscarded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = files.Select(f =>
        {
            var from = Path.Combine(paths.StagingDir(session.Id), f.StagingName!);
            var to = f.Keep
                ? Path.Combine(folder, UniqueName(f.FileName, takenKept))
                : Path.Combine(discardedFolder, UniqueName(f.FileName, takenDiscarded));
            return (f.Id, from, to);
        }).ToList();

        return store.CreateImportPlan(session.Id, folder, moves, discardedCount: files.Count(f => !f.Keep));
    }

    private void WriteSummary(ImportRecord import, Session session)
    {
        var files = store.GetStagedFiles(session.Id);
        var first = files.Min(f => f.TakenAt ?? f.MtimeUtc.ToLocalTime());
        var last = files.Max(f => f.TakenAt ?? f.MtimeUtc.ToLocalTime());
        var text = new StringBuilder()
            .AppendLine("Bilder fra viltkamera")
            .AppendLine()
            .AppendLine(string.Create(Norwegian, $"Importert:   {import.CreatedUtc.ToLocalTime():d. MMMM yyyy 'kl.' HH:mm}"))
            .AppendLine(string.Create(Norwegian, $"Antall:      {import.ImageCount:N0} bilder"))
            .AppendLine(string.Create(Norwegian, $"Tatt:        {first:d. MMMM yyyy} – {last:d. MMMM yyyy}"));
        if (import.DiscardedCount > 0)
            text.AppendLine(string.Create(Norwegian,
                $"Sortert bort: {import.DiscardedCount:N0} bilder, i mappen «{DiscardedFolderName}». De er ikke slettet."));
        var contents = text
            .AppendLine()
            .AppendLine("Denne filen er laget av EasyImageImporter, og kan leses uten programmet.")
            .ToString()
            .ReplaceLineEndings("\r\n");

        // UTF-8 with BOM so old Notepad shows æøå correctly.
        fs.WriteAllText(Path.Combine(import.FolderPath, SummaryFileName), contents, new UTF8Encoding(true));
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
