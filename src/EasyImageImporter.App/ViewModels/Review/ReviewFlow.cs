using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.App.ViewModels.Review;

/// <summary>
/// Moves between the overview, a single visit and back. Every choice is written to the database
/// the moment it is made, so each screen is simply rebuilt from there.
/// </summary>
public sealed class ReviewFlow(
    long sessionId, ImportStore store, ReviewService review, ThumbnailLoader thumbnails,
    Action<Screen> show, Func<Task> save, Func<Task> retryFailed)
{
    private readonly Dictionary<long, LazyThumbnail> _thumbs = new();

    public bool HasImages => review.GetOverview(sessionId).ImageCount > 0;

    public void ShowOverview()
    {
        var overview = review.GetOverview(sessionId);
        var counts = store.GetCounts(sessionId);
        var cards = overview.Visits.Select((visit, i) =>
            new VisitCard(visit, i + 1, Thumb(visit.Cover), OpenVisit, SetVisitKeep)).ToList();

        show(new ReviewScreen(overview, cards,
            alreadyImportedText: counts.Duplicate == 0 ? null
                : $"{counts.Duplicate:N0} av bildene var importert fra før og blir hoppet over.",
            failedText: counts.Failed == 0 ? null
                : $"{counts.Failed:N0} {(counts.Failed == 1 ? "bilde" : "bilder")} kunne ikke kopieres trygt. Kortet vil ikke bli slettet før dette er løst.",
            save, retryFailed));
    }

    private void SetVisitKeep(Visit visit, bool keep)
    {
        review.SetKeep(visit, keep);
        ShowOverview();
    }

    private void OpenVisit(Visit visit) => OpenVisit(visit.Id);

    private void OpenVisit(long visitId)
    {
        var visits = review.GetOverview(sessionId).Visits;
        var index = visits.ToList().FindIndex(v => v.Id == visitId);
        if (index < 0)
        {
            ShowOverview();
            return;
        }

        var visit = visits[index];
        var previous = index > 0 ? visits[index - 1] : null;
        var next = index < visits.Count - 1 ? visits[index + 1] : null;

        VisitScreen? screen = null;
        var tiles = visit.Frames
            .Select(f => new FrameTile(f, review.StagedPath(f), Thumb(f), review, () => screen))
            .ToList();
        screen = new VisitScreen(visit, index + 1, visits.Count, tiles, review,
            back: ShowOverview,
            previous: previous is null ? null : () => OpenVisit(previous.Id),
            next: next is null ? null : () => OpenVisit(next.Id),
            mergeWithNext: next is null ? null : () =>
            {
                review.Merge(visit, next);
                OpenVisit(visit.Id);
            },
            split: (v, firstOfNew) =>
            {
                review.Split(v, firstOfNew);
                OpenVisit(v.Id);
            });
        show(screen);
    }

    private LazyThumbnail Thumb(SessionFile file)
    {
        if (!_thumbs.TryGetValue(file.Id, out var thumb))
            _thumbs[file.Id] = thumb = thumbnails.For(review.StagedPath(file), file.Sha256!);
        return thumb;
    }
}
