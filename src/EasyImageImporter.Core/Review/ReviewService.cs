using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Naming;

namespace EasyImageImporter.Core.Review;

public sealed record Visit(long Id, string? Label, long? SiteId, IReadOnlyList<SessionFile> Frames)
{
    public DateTime Start => Frames[0].TakenAt ?? default;
    public DateTime End => Frames[^1].TakenAt ?? default;
    public int KeptCount => Frames.Count(f => f.Keep);
    public SessionFile Cover => Frames[Frames.Count / 2];
}

/// <summary>A camera placement and its visits, in time order.</summary>
public sealed record Place(long Id, PlaceDetails Details, IReadOnlyList<Visit> Visits)
{
    /// <summary>The user's name, or "Sted 1" until there is one.</summary>
    public string Name => Details.Title ?? Details.DefaultName;
    public bool IsNamed => Details.Title is not null;
    public DateTime Start => Visits[0].Start;
    public DateTime End => Visits[^1].End;
    public int ImageCount => Visits.Sum(v => v.Frames.Count);

    /// <summary>Animals labelled in the visits, most photographed first.</summary>
    public IReadOnlyList<string> Animals => Visits
        .Where(v => v.Label is not null)
        .GroupBy(v => v.Label!, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(g => g.Sum(v => v.Frames.Count))
        .Select(g => g.First().Label!)
        .ToList();

    public string SuggestedFolderName => Names.FolderSuggestion(Name, Start, End, Animals);
    public string FolderName => Names.Clean(Details.FolderName, 120) is { Length: > 0 } custom ? custom : SuggestedFolderName;
}

public sealed record ReviewOverview(IReadOnlyList<Place> Places, IReadOnlyList<SessionFile> Videos)
{
    /// <summary>All visits, place by place: the order the review shows them in.</summary>
    public IReadOnlyList<Visit> Visits { get; } = Places.SelectMany(p => p.Visits).ToList();
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

        if (!store.HasSequences(sessionId))
        {
            var images = staged.Where(f => f.Kind == MediaKind.Image)
                .Select(f => new SequenceInput(f.Id, f.TakenAt ?? f.MtimeUtc.ToLocalTime(), f.Camera, f.RelPath));
            store.CreateSequences(sessionId, SequenceBuilder.Build(images));
        }

        if (!store.HasSites(sessionId))
        {
            DetectPlaces(sessionId);
            RecognisePlaces(sessionId);
        }
    }

    /// <summary>
    /// "Dette ser ut som Høgfjellåsen": a new place whose visits mostly match the remembered scenes
    /// of a place saved before gets that name filled in. The user can always change it.
    /// </summary>
    private void RecognisePlaces(long sessionId)
    {
        var known = store.GetKnownScenes()
            .Select(k => (k.KnownSiteId, k.Name, Scene: Scene.FromBytes(k.Edges, k.Night)))
            .GroupBy(k => k.KnownSiteId)
            .ToList();
        if (known.Count == 0) return;

        var scenes = store.GetVisitScenes(sessionId).ToDictionary(s => s.SequenceId, s => Scene.FromBytes(s.Edges, s.Night));
        foreach (var place in GetOverview(sessionId).Places)
        {
            // A handful of visits is plenty, and keeps this quick with many remembered places.
            var sample = SampleFrames(place.Visits.Where(v => scenes.ContainsKey(v.Id)).ToList(), 6)
                .Select(v => scenes[v.Id]).ToList();
            var best = known
                .Select(site =>
                {
                    var checkable = sample.Where(s => site.Any(k => k.Scene.IsNight == s.IsNight)).ToList();
                    var matching = checkable.Count(s => site
                        .Where(k => k.Scene.IsNight == s.IsNight)
                        .Any(k => k.Scene.Similarity(s) >= SiteDetector.SamePlace));
                    return (Site: site.Key, Share: checkable.Count == 0 ? 0 : (double)matching / checkable.Count);
                })
                .MaxBy(x => x.Share);
            if (best.Share > 0.5) store.SetRecognised(place.Id, best.Site);
        }
    }

    /// <summary>
    /// Groups the visits by camera placement from what the background looks like. Each camera is
    /// handled on its own: two cameras on one card are never the same placement.
    /// </summary>
    private void DetectPlaces(long sessionId)
    {
        var visits = LoadVisits(sessionId);
        var scenes = visits.ToDictionary(v => v.Id, v => Scene.FromFrames(SampleFrames(v.Frames).Select(StagedPath)));
        store.SaveVisitScenes(scenes.Where(s => s.Value is not null)
            .Select(s => new StoredScene(s.Key, s.Value!.IsNight, s.Value.ToBytes())));

        var places = visits
            .GroupBy(v => v.Frames[0].Camera ?? "")
            .SelectMany(camera => SiteDetector.Detect(camera.Select(v => new SiteInput(v.Id, v.Start, v.End, scenes[v.Id])).ToList()))
            .OrderBy(ids => visits.First(v => v.Id == ids[0]).Start)
            .ToList();
        store.CreateSites(sessionId, places);
    }

    public ReviewOverview GetOverview(long sessionId)
    {
        var details = store.GetPlaceDetails(sessionId);
        var places = LoadVisits(sessionId)
            .GroupBy(v => v.SiteId ?? 0)
            .Select(g => new Place(g.Key,
                details.GetValueOrDefault(g.Key) ?? new PlaceDetails(g.Key, "Ukjent sted", null, null, null, null, []),
                g.ToList()))
            .OrderBy(p => p.Start)
            .ToList();
        var videos = store.GetStagedFiles(sessionId).Where(f => f.Kind == MediaKind.Video).ToList();
        return new ReviewOverview(places, videos);
    }

    private List<Visit> LoadVisits(long sessionId)
    {
        var info = store.GetSequenceInfo(sessionId);
        return store.GetStagedFiles(sessionId)
            .Where(f => f.Kind == MediaKind.Image && f.SequenceId is not null)
            .GroupBy(f => f.SequenceId!.Value)
            .Select(g =>
            {
                var (label, site) = info.GetValueOrDefault(g.Key);
                return new Visit(g.Key, label, site, Ordered(g));
            })
            .OrderBy(v => v.Start).ThenBy(v => v.Id)
            .ToList();
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

    public void SetLabel(Visit visit, string? label) => store.SetSequenceLabel(visit.Id, label);

    public IReadOnlyList<string> AnimalSuggestions() =>
        store.GetVocabulary("species").Concat(Species.Common).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> TagSuggestions() => store.GetVocabulary("tag");

    /// <summary>Everything needed before saving: every place has a name.</summary>
    public bool IsReadyToSave(long sessionId) => GetOverview(sessionId).Places.All(p => p.IsNamed);

    /// <summary>"Nytt sted fra denne hendelsen": this visit and the later ones at its place become a new place.</summary>
    public void StartNewPlace(Place place, Visit from)
    {
        var index = place.Visits.ToList().FindIndex(v => v.Id == from.Id);
        if (index <= 0) return;
        store.SplitSite(place.Visits[0].Frames[0].SessionId, place.Visits.Skip(index).Select(v => v.Id).ToList());
    }

    /// <summary>"Samme sted som over": every visit of <paramref name="second"/> joins <paramref name="first"/>.</summary>
    public void MergePlaces(Place first, Place second) => store.MergeSites(first.Id, second.Id);

    /// <summary>Up to 12 frames spread over a visit: enough for the median to remove the animal.</summary>
    public static IEnumerable<T> SampleFrames<T>(IReadOnlyList<T> frames, int max = 12) =>
        frames.Count <= max ? frames : Enumerable.Range(0, max).Select(i => frames[i * frames.Count / max]);

    private static List<SessionFile> Ordered(IEnumerable<SessionFile> frames) =>
        frames.OrderBy(f => f.TakenAt).ThenBy(f => f.RelPath, StringComparer.Ordinal).ToList();
}
