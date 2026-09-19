using System.Globalization;
using System.Text;
using EasyImageImporter.Core.IO;
using EasyImageImporter.Core.Metadata;
using EasyImageImporter.Core.Naming;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Import;

public readonly record struct SaveProgress(int Done, int Total);

/// <summary>
/// Saves an import: one folder per place, <c>Viltkamera\2026\Høgfjellåsen Juni 2026 – Kongeørn\</c>,
/// files named <c>2026-06-03_Høgfjellåsen_0712_Kongeørn_001.jpg</c>, sorted-away photos in
/// <c>Sortert bort\</c>, a plain-text summary per folder, and tags Windows Explorer can search.
///
/// The whole move plan is written to the database before the first file moves, so a crash halfway
/// is finished by simply running <see cref="Run"/> again; every later step is safe to repeat.
/// </summary>
public sealed class Finalizer(
    IFileSystem fs, ImportStore store, AppPaths paths, TimeProvider? time = null, Func<ExifTool?>? exifTool = null)
{
    public const string SummaryFileName = "OM DENNE MAPPEN.txt";

    /// <summary>Where discarded images go: inside the import, so they can always be found again.</summary>
    public const string DiscardedFolderName = "Sortert bort";

    /// <summary>Scenes remembered per place per import, for recognising it next season.</summary>
    private const int ScenesToRemember = 12;

    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SafeMover _mover = new(fs);
    private readonly ReviewService _review = new(store, paths);

    /// <summary>Returns the import, or null when every file was already archived before (nothing new to store).</summary>
    public ImportRecord? Run(long sessionId, IProgress<SaveProgress>? progress = null)
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

        var moves = store.GetMoves(import.Id);
        var total = moves.Count * (exifTool is null ? 1 : 2);
        var done = moves.Count(m => m.Done);
        foreach (var move in moves.Where(m => !m.Done))
        {
            _mover.Move(move.FromPath, move.ToPath);
            store.MarkMoveDone(move.ImportId, move.FileId);
            progress?.Report(new SaveProgress(++done, total));
        }

        var overview = _review.GetOverview(sessionId);
        WriteMetadata(moves, overview, progress, done, total);
        foreach (var folder in store.GetImportFolders(import.Id)) WriteSummary(import, folder, overview);

        store.CompleteImport(import.Id, sessionId);
        RememberPlaces(sessionId, overview);
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

        _review.Prepare(session.Id); // normally done by the review already; cheap if so
        var overview = _review.GetOverview(session.Id);
        var placeOf = new Dictionary<long, (Place Place, Visit Visit)>();
        foreach (var place in overview.Places)
        foreach (var visit in place.Visits)
        foreach (var frame in visit.Frames)
            placeOf[frame.Id] = (place, visit);

        var moves = new List<(long FileId, string From, string To)>();
        var folders = new List<ImportFolder>();
        var plannedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        string FolderFor(string name, DateTime start)
        {
            var year = (start.Year >= 2010 ? start.Year : _time.GetLocalNow().Year).ToString(CultureInfo.InvariantCulture);
            var path = UniquePath(Path.Combine(paths.ArchiveRoot, year, name),
                p => fs.DirectoryExists(p) || plannedFolders.Contains(p));
            plannedFolders.Add(path);
            return path;
        }

        void Add(SessionFile file, string folder, string name)
        {
            var dir = file.Keep ? folder : Path.Combine(folder, DiscardedFolderName);
            if (!takenNames.TryGetValue(dir, out var taken)) takenNames[dir] = taken = [];
            moves.Add((file.Id, Path.Combine(paths.StagingDir(session.Id), file.StagingName!),
                Path.Combine(dir, UniqueName(name, taken))));
        }

        foreach (var place in overview.Places)
        {
            var folder = FolderFor(place.FolderName, place.Start);
            foreach (var visit in place.Visits)
                for (var i = 0; i < visit.Frames.Count; i++)
                    Add(visit.Frames[i], folder,
                        Names.FileName(visit.Start, place.Name, visit.Label, i + 1, Path.GetExtension(visit.Frames[i].RelPath)));
            folders.Add(new ImportFolder(0, folder,
                place.Visits.Sum(v => v.KeptCount), place.ImageCount - place.Visits.Sum(v => v.KeptCount), place.Id));
        }

        // Videos (and anything not in a visit) go with the place they were filmed nearest in time to.
        var rest = files.Where(f => !placeOf.ContainsKey(f.Id)).ToList();
        if (rest.Count > 0)
        {
            string? fallback = null;
            foreach (var file in rest.OrderBy(f => f.TakenAt).ThenBy(f => f.RelPath, StringComparer.Ordinal))
            {
                var taken = file.TakenAt ?? file.MtimeUtc.ToLocalTime();
                var place = overview.Places.MinBy(p => taken < p.Start ? p.Start - taken : taken > p.End ? taken - p.End : TimeSpan.Zero);
                string folder, placeName;
                if (place is null)
                {
                    fallback ??= FolderFor($"{_time.GetLocalNow():yyyy-MM-dd} Import", _time.GetLocalNow().DateTime);
                    (folder, placeName) = (fallback, "Import");
                }
                else
                {
                    folder = folders.First(f => f.PlaceId == place.Id).FolderPath;
                    placeName = place.Name;
                }
                Add(file, folder, Names.FileName(taken, placeName, null, 1, Path.GetExtension(file.RelPath)));

                var index = folders.FindIndex(f => f.FolderPath == folder);
                if (index >= 0) folders[index] = folders[index] with { ImageCount = folders[index].ImageCount + 1 };
                else folders.Add(new ImportFolder(0, folder, 1, 0));
            }
        }

        var import = store.CreateImportPlan(session.Id, folders[0].FolderPath, moves,
            discardedCount: files.Count(f => !f.Keep));
        store.AddImportFolders(import.Id, folders.Select(f => f with { ImportId = import.Id }));
        return import;
    }

    /// <summary>
    /// Title, description and tags, so Explorer's own search and Properties panel find them.
    /// Optional: without ExifTool, or for a photo it can't safely tag, the photo is simply left as it was.
    /// </summary>
    private void WriteMetadata(IReadOnlyList<ImportMove> moves, ReviewOverview overview, IProgress<SaveProgress>? progress,
        int done, int total)
    {
        if (exifTool is null) return;
        using var tool = exifTool();
        if (tool is null) return;

        var byFile = overview.Places
            .SelectMany(p => p.Visits.SelectMany(v => v.Frames.Select(f => (f.Id, Place: p, Visit: v))))
            .ToDictionary(x => x.Id);
        foreach (var move in moves)
        {
            progress?.Report(new SaveProgress(++done, total));
            if (!byFile.TryGetValue(move.FileId, out var at)) continue; // videos
            if (Path.GetExtension(move.ToPath).ToLowerInvariant() is not (".jpg" or ".jpeg")) continue;
            var keywords = new List<string> { at.Place.Name };
            if (at.Visit.Label is { } label) keywords.Add(label);
            keywords.AddRange(at.Place.Details.Tags);
            tool.Write(move.ToPath, new PhotoMetadata(at.Place.Name, at.Place.Details.Description, keywords));
        }
    }

    private void WriteSummary(ImportRecord import, ImportFolder folder, ReviewOverview overview)
    {
        var place = overview.Places.FirstOrDefault(p => p.Id == folder.PlaceId);
        var text = new StringBuilder().AppendLine(place is null ? "Bilder fra viltkamera" : place.Name).AppendLine();
        if (place?.Details.Description is { } description) text.AppendLine(description).AppendLine();
        if (place is not null)
            text.AppendLine(string.Create(Norwegian, $"Tatt:         {place.Start:d. MMMM yyyy} – {place.End:d. MMMM yyyy}"));
        text.AppendLine(string.Create(Norwegian, $"Antall:       {folder.ImageCount:N0} bilder"));
        if (folder.DiscardedCount > 0)
            text.AppendLine(string.Create(Norwegian,
                $"Sortert bort: {folder.DiscardedCount:N0} bilder, i mappen «{DiscardedFolderName}». De er ikke slettet."));
        if (place is not null && place.Animals.Count > 0) text.AppendLine($"Dyr:          {string.Join(", ", place.Animals)}");
        if (place is not null && place.Details.Tags.Count > 0) text.AppendLine($"Stikkord:     {string.Join(", ", place.Details.Tags)}");
        text.AppendLine(string.Create(Norwegian, $"Importert:    {import.CreatedUtc.ToLocalTime():d. MMMM yyyy 'kl.' HH:mm}"));
        var contents = text
            .AppendLine()
            .AppendLine("Denne filen er laget av EasyImageImporter, og kan leses uten programmet.")
            .ToString()
            .ReplaceLineEndings("\r\n");

        // UTF-8 with BOM so old Notepad shows æøå correctly.
        fs.CreateDirectory(folder.FolderPath);
        fs.WriteAllText(Path.Combine(folder.FolderPath, SummaryFileName), contents, new UTF8Encoding(true));
    }

    /// <summary>Named places teach the app what they look like, so the next card from there is recognised.</summary>
    private void RememberPlaces(long sessionId, ReviewOverview overview)
    {
        var scenes = store.GetVisitScenes(sessionId).ToDictionary(s => s.SequenceId);
        foreach (var place in overview.Places.Where(p => p.IsNamed))
        {
            var ofPlace = place.Visits.Where(v => scenes.ContainsKey(v.Id)).Select(v => scenes[v.Id]).ToList();
            var sample = ReviewService.SampleFrames(ofPlace.Where(s => !s.Night).ToList(), ScenesToRemember / 2)
                .Concat(ReviewService.SampleFrames(ofPlace.Where(s => s.Night).ToList(), ScenesToRemember / 2));
            store.RememberPlace(place.Name, sample);
        }
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

    /// <summary>Two files may end up with the same name (e.g. two cameras); the second gets " (2)".</summary>
    internal static string UniqueName(string fileName, ISet<string> taken)
    {
        var name = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; !taken.Add(name); i++) name = $"{stem} ({i}){ext}";
        return name;
    }
}
