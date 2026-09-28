using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.App.ViewModels.Review;

/// <summary>
/// Moves between the overview, a single visit and back. Every choice is written to the database
/// the moment it is made, so each screen is simply rebuilt from there.
/// </summary>
public sealed class ReviewFlow
{
    private const string AllFilter = "Alle", EmptyFilter = "Tomme", UnsureFilter = "Usikre";

    private readonly long sessionId;
    private readonly ImportStore store;
    private readonly ReviewService review;
    private readonly ThumbnailLoader thumbnails;
    private readonly AnimalRecognition recognition;
    private readonly Action<Screen> show;
    private readonly Func<Task> save;
    private readonly Func<Task> retryFailed;
    private readonly Dictionary<long, LazyThumbnail> _thumbs = new();
    private readonly Dictionary<long, VisitCard> _cards = new();
    private string _filter = AllFilter;

    /// <summary>The overview, while it is on screen; analysis results update its cards and chips.</summary>
    private ReviewScreen? _overview;

    public ReviewFlow(long sessionId, ImportStore store, ReviewService review, ThumbnailLoader thumbnails,
        AnimalRecognition recognition, Action<Screen> show, Func<Task> save, Func<Task> retryFailed)
    {
        this.sessionId = sessionId;
        this.store = store;
        this.review = review;
        this.thumbnails = thumbnails;
        this.recognition = recognition;
        this.show = show;
        this.save = save;
        this.retryFailed = retryFailed;
        recognition.VisitAnalysed += OnVisitAnalysed;
    }

    /// <summary>Stop listening for analysis results (this flow is no longer on screen).</summary>
    public void Detach() => recognition.VisitAnalysed -= OnVisitAnalysed;

    /// <summary>Which filter chip a visit belongs to: its label, or what is suggested for it.</summary>
    private static string? FilterOf(Visit v) =>
        v.Label is { } label ? label
        : v.Suggestion is null || v.SuggestionState == SuggestionState.Dismissed ? null
        : v.Suggestion.Kind switch
        {
            SuggestionKind.Empty => EmptyFilter,
            SuggestionKind.Unsure => UnsureFilter,
            _ => v.Suggestion.Name,
        };

    private void OnVisitAnalysed(long visitId) => Refresh();

    /// <summary>
    /// After a choice or an analysis result: update the cards on screen and the counts in place.
    /// The list itself stays put, so the user keeps their place while scrolling.
    /// </summary>
    private void Refresh()
    {
        if (_overview is null) return;
        var overview = review.GetOverview(sessionId);
        foreach (var visit in overview.Visits)
            if (_cards.TryGetValue(visit.Id, out var card)) card.Update(visit);
        _overview.Changed(overview, Filters(overview), Shown(overview));
    }

    private bool IsShown(Visit v) => _filter == AllFilter || FilterOf(v) == _filter;

    /// <summary>The visits the chosen filter shows: all of them with "Alle".</summary>
    private List<Visit> Shown(ReviewOverview overview) => overview.Visits.Where(IsShown).ToList();

    /// <summary>
    /// "Sorter bort alle" / "Behold alle" for what the filter shows: every nøtteskrike, every empty
    /// frame, or the whole card. Still the user's choice, one visit at a time can be changed after.
    /// </summary>
    private void SetShownKeep(bool keep)
    {
        review.SetKeep(Shown(review.GetOverview(sessionId)), keep);
        Refresh();
    }

    /// <summary>On from the review: name the places with photos to keep, or, with nothing kept, save straight away.</summary>
    private void Next()
    {
        if (ReviewService.PlacesToName(review.GetOverview(sessionId)).Count > 0)
        {
            ShowNaming();
            return;
        }
        _overview = null;
        _ = save();
    }

    private IReadOnlyList<FilterChip> Filters(ReviewOverview overview)
    {
        var groups = overview.Visits.Select(FilterOf).Where(f => f is not null).GroupBy(f => f!).ToList();
        if (groups.Count == 0) return [];
        // The ones to deal with first (empty, unsure), then the animals, most common first.
        var named = groups.Where(g => g.Key is not (EmptyFilter or UnsureFilter)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key);
        var chips = new List<FilterChip> { Chip(AllFilter, overview.Visits.Count) };
        foreach (var special in new[] { EmptyFilter, UnsureFilter })
            if (groups.FirstOrDefault(g => g.Key == special) is { } g) chips.Add(Chip(special, g.Count()));
        chips.AddRange(named.Select(g => Chip(g.Key, g.Count())));
        return chips;

        FilterChip Chip(string key, int count) => new($"{key} ({count:N0})", key == _filter, () =>
        {
            _filter = key;
            ShowOverview();
        });
    }

    public bool HasImages => review.GetOverview(sessionId).ImageCount > 0;

    public void ShowOverview()
    {
        var overview = review.GetOverview(sessionId);
        var counts = store.GetCounts(sessionId);
        var filters = Filters(overview);
        if (!filters.Any(f => f.IsSelected)) _filter = AllFilter;

        var rows = new List<object>();
        var number = 0;
        _cards.Clear();
        for (var p = 0; p < overview.Places.Count; p++)
        {
            var place = overview.Places[p];
            var above = p > 0 ? overview.Places[p - 1] : null;
            var cards = new List<VisitCard>();
            foreach (var visit in place.Visits)
            {
                ++number;
                if (!IsShown(visit)) continue;
                var card = new VisitCard(visit, number, Thumb(visit.Cover), OpenVisit, SetVisitKeep, Accept, Dismiss);
                _cards[visit.Id] = card;
                cards.Add(card);
            }
            if (cards.Count == 0) continue;
            rows.Add(new PlaceHeader(place, above is null || _filter != AllFilter ? null : () =>
            {
                review.MergePlaces(above, place);
                ShowOverview();
            }));
            rows.AddRange(Rows.Of(cards, 3, items => new VisitRow(items)));
        }

        var shownCount = overview.Visits.Count(IsShown);
        var screen = new ReviewScreen(overview, rows, filters,
            _filter == AllFilter ? null : $"Viser {shownCount:N0} av {overview.Visits.Count:N0} bildeserier.",
            recognition,
            alreadyImportedText: counts.Duplicate == 0 ? null
                : $"{counts.Duplicate:N0} av bildene var importert fra før og blir hoppet over.",
            failedText: counts.Failed == 0 ? null
                : $"{counts.Failed:N0} {(counts.Failed == 1 ? "bilde" : "bilder")} kunne ikke kopieres trygt. Kortet vil ikke bli slettet før dette er løst.",
            Next, retryFailed, SetShownKeep);
        _overview = screen;
        screen.Changed(overview, filters, Shown(overview));
        show(screen);
    }

    private void Accept(Visit visit)
    {
        review.AcceptSuggestion(visit);
        Refresh();
    }

    private void Dismiss(Visit visit)
    {
        review.DismissSuggestion(visit);
        Refresh();
    }

    /// <summary>"Navn og merking": name each place before saving.</summary>
    public void ShowNaming()
    {
        var overview = review.GetOverview(sessionId);
        var tags = review.TagSuggestions();
        var forms = ReviewService.PlacesToName(overview).Select((place, i) => new PlaceForm(place, i + 1,
            ReviewService.SampleFrames(place.Visits, 4).Select(v => Thumb(v.Cover)).ToList(), store, tags)).ToList();
        _overview = null;
        show(new NamingScreen(forms, back: ShowOverview, save));
    }

    private void SetVisitKeep(Visit visit, bool keep)
    {
        review.SetKeep(visit, keep);
        Refresh();
    }

    private void OpenVisit(Visit visit) => OpenVisit(visit.Id);

    private void OpenVisit(long visitId)
    {
        var overview = review.GetOverview(sessionId);
        var visits = overview.Visits;
        var index = visits.ToList().FindIndex(v => v.Id == visitId);
        if (index < 0)
        {
            ShowOverview();
            return;
        }

        var visit = visits[index];
        var place = overview.Places.First(p => p.Visits.Any(v => v.Id == visit.Id));
        var firstAtPlace = place.Visits[0].Id == visit.Id;
        var previous = index > 0 ? visits[index - 1] : null;
        var next = index < visits.Count - 1 ? visits[index + 1] : null;

        VisitScreen? screen = null;
        var tiles = visit.Frames
            .Select(f => new FrameTile(f, review.StagedPath(f), Thumb(f), review, () => screen))
            .ToList();
        _overview = null;
        screen = new VisitScreen(visit, index + 1, visits.Count, place.Name, tiles, review,
            back: ShowOverview,
            previous: previous is null ? null : () => OpenVisit(previous.Id),
            next: next is null ? null : () => OpenVisit(next.Id),
            startNewPlace: firstAtPlace ? null : () =>
            {
                review.StartNewPlace(place, visit);
                OpenVisit(visit.Id);
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
