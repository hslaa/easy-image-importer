using EasyImageImporter.Core.Import;

namespace EasyImageImporter.Core.Review;

public sealed record Visit(long Id, string? Label, IReadOnlyList<SessionFile> Frames)
{
    public DateTime Start => Frames[0].TakenAt ?? default;
    public DateTime End => Frames[^1].TakenAt ?? default;
    public int KeptCount => Frames.Count(f => f.Keep);
    public SessionFile Cover => Frames[Frames.Count / 2];
}

public sealed record ReviewOverview(IReadOnlyList<Visit> Visits, IReadOnlyList<SessionFile> Videos)
{
    public int ImageCount => Visits.Sum(v => v.Frames.Count);
    public int KeptCount => Visits.Sum(v => v.KeptCount);
}

/// <summary>
/// Everything the review screens do. Nothing is ever deleted here: "discard" only clears a keep
/// flag, and discarded images are saved to "Sortert bort" when the import is saved.
/// </summary>
public sealed class ReviewService(ImportStore store, AppPaths paths)
{
    /// <summary>
    /// Reads capture times for staged files that don't have one yet, and groups the images into visits
    /// the first time. Existing visits (and any manual splits and merges) are never recomputed.
    /// </summary>
    public void Prepare(long sessionId)
    {
        var staged = store.GetStagedFiles(sessionId);
        var missing = staged.Where(f => f.Kind is null).ToList();
        if (missing.Count > 0)
        {
            store.SetMediaInfo(missing.Select(f =>
            {
                var info = MediaInfoReader.Read(StagedPath(f), f.MtimeUtc, f.FileName);
                return (f.Id, info.Kind, info.TakenAt, (string?)info.Source, info.Camera);
            }).ToList());
            staged = store.GetStagedFiles(sessionId);
        }

        if (store.HasSequences(sessionId)) return;
        var images = staged.Where(f => f.Kind == MediaKind.Image)
            .Select(f => new SequenceInput(f.Id, f.TakenAt ?? f.MtimeUtc.ToLocalTime(), f.Camera, f.RelPath));
        store.CreateSequences(sessionId, SequenceBuilder.Build(images));
    }

    public ReviewOverview GetOverview(long sessionId)
    {
        var staged = store.GetStagedFiles(sessionId);
        var labels = store.GetSequenceLabels(sessionId);
        var visits = staged
            .Where(f => f.Kind == MediaKind.Image && f.SequenceId is not null)
            .GroupBy(f => f.SequenceId!.Value)
            .Select(g => new Visit(g.Key, labels.GetValueOrDefault(g.Key), Ordered(g)))
            .OrderBy(v => v.Start).ThenBy(v => v.Id)
            .ToList();
        var videos = staged.Where(f => f.Kind == MediaKind.Video).ToList();
        return new ReviewOverview(visits, videos);
    }

    public string StagedPath(SessionFile file) => Path.Combine(paths.StagingDir(file.SessionId), file.StagingName!);

    public void SetKeep(long fileId, bool keep) => store.SetKeep([fileId], keep);

    public void SetKeep(Visit visit, bool keep) => store.SetKeep(visit.Frames.Select(f => f.Id), keep);

    /// <summary>Splits the visit so that <paramref name="firstOfNew"/> and everything after it becomes a new visit.</summary>
    public void Split(Visit visit, long firstOfNew)
    {
        var index = visit.Frames.ToList().FindIndex(f => f.Id == firstOfNew);
        if (index <= 0) return; // splitting before the first frame changes nothing
        store.SplitSequence(visit.Id, visit.Frames.Skip(index).Select(f => f.Id).ToList());
    }

    public void Merge(Visit first, Visit second) => store.MergeSequences(first.Id, second.Id);

    private static List<SessionFile> Ordered(IEnumerable<SessionFile> frames) =>
        frames.OrderBy(f => f.TakenAt).ThenBy(f => f.RelPath, StringComparer.Ordinal).ToList();
}
