using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.App.ViewModels.Review;

// Lists are split into fixed-width rows so the list can be virtualized: only rows on screen exist.
public sealed record VisitRow(IReadOnlyList<VisitCard> Items);

/// <summary>Heading above a place's visits in the overview.</summary>
public sealed partial class PlaceHeader(Place place, Action? mergeWithAbove)
{
    public string Name { get; } = place.Name;
    public string Details { get; } =
        $"{Rows.DateRangeText(place.Start, place.End)} · {place.Visits.Count:N0} " +
        $"{(place.Visits.Count == 1 ? "bildeserie" : "bildeserier")}, {Rows.Count(place.ImageCount)}";
    public bool CanMerge => mergeWithAbove is not null;

    [RelayCommand]
    private void MergeWithAbove() => mergeWithAbove?.Invoke();
}

public sealed record FrameRow(IReadOnlyList<FrameTile> Items);

internal static class Rows
{
    public static List<TRow> Of<TItem, TRow>(IEnumerable<TItem> items, int perRow, Func<IReadOnlyList<TItem>, TRow> make) =>
        items.Chunk(perRow).Select(chunk => make(chunk)).ToList();

    public static string Count(int n) => n == 1 ? "1 bilde" : $"{n:N0} bilder";

    public static string TimeSpanText(DateTime start, DateTime end) =>
        start.ToString("HH:mm") == end.ToString("HH:mm") ? start.ToString("HH:mm") : $"{start:HH:mm}–{end:HH:mm}";

    /// <summary>A camera whose clock was reset shows year 2000; don't pretend that is a real date.</summary>
    public static string DateText(DateTime t) => t.Year < 2010 ? "Ukjent dato" : t.ToString("d. MMMM yyyy");

    public static string DateRangeText(DateTime start, DateTime end) =>
        start.Year < 2010 ? "Ukjent dato"
        : start.Date == end.Date ? start.ToString("d. MMMM yyyy")
        : start.Year == end.Year && start.Month == end.Month ? $"{start:d.}–{end:d. MMMM yyyy}"
        : start.Year == end.Year ? $"{start:d. MMMM} – {end:d. MMMM yyyy}"
        : $"{start:d. MMMM yyyy} – {end:d. MMMM yyyy}";
}

/// <summary>All visits on the card. "Default is keep everything": the user actively sorts away.</summary>
public sealed partial class ReviewScreen(
    ReviewOverview overview, IReadOnlyList<object> rows, IReadOnlyList<FilterChip> filters, string? filterText,
    AnimalRecognition recognition, string? alreadyImportedText, string? failedText, Action next, Func<Task> retryFailed,
    Action? discardShown = null) : Screen
{
    private ReviewOverview _overview = overview;

    public override int Step => FlowStep.Review;

    /// <summary>A <see cref="PlaceHeader"/> followed by that place's <see cref="VisitRow"/>s, place by place.</summary>
    public IReadOnlyList<object> Rows { get; } = rows;

    /// <summary>Alle · Tomme (12) · Usikre (4) · Ravn (5) … Empty until there is something to filter on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFilters))]
    private IReadOnlyList<FilterChip> _filters = filters;

    public bool HasFilters => Filters.Count > 1;

    /// <summary>A choice was made on a card: new counts, but the list itself stays where it is.</summary>
    public void Changed(ReviewOverview overview, IReadOnlyList<FilterChip> filters, int shownToDiscard)
    {
        _overview = overview;
        Filters = filters;
        _shownToDiscard = shownToDiscard;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DiscardShownText));
        OnPropertyChanged(nameof(CanDiscardShown));
    }

    /// <summary>"Viser 12 av 60 bildeserier" while a filter is on.</summary>
    public string? FilterText { get; } = filterText;

    public AnimalRecognition Recognition { get; } = recognition;

    public string Summary =>
        $"{Review.Rows.Count(_overview.ImageCount)} i {_overview.Visits.Count:N0} " +
        $"{(_overview.Visits.Count == 1 ? "bildeserie" : "bildeserier")}" +
        (_overview.Places.Count > 1 ? $" på {_overview.Places.Count:N0} steder." : ".") +
        (_overview.KeptCount == _overview.ImageCount ? ""
            : $" {_overview.ImageCount - _overview.KeptCount:N0} av dem sorteres bort.");

    /// <summary>Things worth knowing, but nothing to do: skipped duplicates and videos.</summary>
    public string? NoteText { get; } = string.Join(" ", new[]
    {
        alreadyImportedText,
        overview.Videos.Count switch
        {
            0 => null,
            1 => "1 video blir lagret uten gjennomgang.",
            var n => $"{n:N0} videoer blir lagret uten gjennomgang.",
        },
    }.Where(t => t is not null)) is { Length: > 0 } text ? text : null;

    public string? FailedText { get; } = failedText;

    /// <summary>With "Tomme" chosen: sort away every visit shown, in one go (still the user's choice).</summary>
    private int _shownToDiscard;
    public bool CanDiscardShown => discardShown is not null && _shownToDiscard > 0;
    public string DiscardShownText => _shownToDiscard == 1
        ? "Sorter bort den tomme bildeserien"
        : $"Sorter bort alle {_shownToDiscard:N0} tomme bildeserier";

    [RelayCommand]
    private void DiscardShown() => discardShown?.Invoke();

    /// <summary>On to naming the places; saving happens from there.</summary>
    [RelayCommand]
    private void Next() => next();

    [RelayCommand]
    private Task Retry() => retryFailed();
}

public sealed partial class FilterChip(string text, bool selected, Action select) : ObservableObject
{
    public string Text { get; } = text;
    public bool IsSelected { get; } = selected;

    [RelayCommand]
    private void Select() => select();
}

/// <summary>
/// One visit in the overview. Updated in place when the animal recognition has looked at it, so
/// the list doesn't jump while the user is scrolling.
/// </summary>
public sealed partial class VisitCard(
    Visit visit, int number, LazyThumbnail cover, Action<Visit> open, Action<Visit, bool> setKeep,
    Action<Visit> accept, Action<Visit> dismiss) : ObservableObject
{
    private Visit _visit = visit;

    public long VisitId => _visit.Id;
    public LazyThumbnail Cover { get; } = cover;

    /// <summary>What's in it once the user has said so, otherwise just its number.</summary>
    public string Title => _visit.Label ?? $"Bildeserie {number}";
    public string Details =>
        $"{Rows.DateText(_visit.Start)} kl. {Rows.TimeSpanText(_visit.Start, _visit.End)} · {Rows.Count(_visit.Frames.Count)}";

    public bool IsDiscarded => _visit.KeptCount == 0;
    private bool IsAllKept => _visit.KeptCount == _visit.Frames.Count;

    /// <summary>Checked: all kept. Unchecked: sorted away. In between: some of the frames.</summary>
    public bool? KeepState => IsAllKept ? true : IsDiscarded ? false : null;
    public string KeepLabel => IsAllKept || IsDiscarded ? "Behold" : $"Behold ({_visit.KeptCount:N0} av {_visit.Frames.Count:N0})";

    /// <summary>Looks empty: shown a bit faded, but never hidden or sorted away on its own.</summary>
    public bool LooksEmpty => _visit.Suggestion?.Kind == SuggestionKind.Empty
                              && _visit.SuggestionState != SuggestionState.Dismissed && _visit.Label is null;
    public double Opacity => IsDiscarded ? 0.4 : LooksEmpty ? 0.7 : 1;

    public bool HasQuestion => _visit.HasOpenSuggestion && _visit.Suggestion!.Kind != SuggestionKind.Empty;
    public string Question => _visit.Suggestion is { } s
        ? s.Kind == SuggestionKind.Unsure ? $"Kanskje {Lower(s.Name)}?" : $"Er det {Lower(s.Name)}?"
        : "";

    private static string Lower(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    public void Update(Visit visit)
    {
        _visit = visit;
        OnPropertyChanged(string.Empty); // everything may have changed
    }

    [RelayCommand]
    private void Open() => open(_visit);

    /// <summary>Like a checkbox: partly kept or sorted away becomes all kept, all kept becomes sorted away.</summary>
    [RelayCommand]
    private void Toggle() => setKeep(_visit, !IsAllKept);

    [RelayCommand]
    private void Accept() => accept(_visit);

    [RelayCommand]
    private void Dismiss() => dismiss(_visit);
}

/// <summary>One visit: every frame, keep or sort away one by one.</summary>
public sealed partial class VisitScreen : Screen
{
    public override int Step => FlowStep.Review;

    private readonly Visit _visit;
    private readonly ReviewService _review;

    public VisitScreen(Visit visit, int number, int total, string placeName, IReadOnlyList<FrameTile> tiles,
        ReviewService review, Action back, Action? previous, Action? next, Action? startNewPlace)
    {
        _visit = visit;
        _review = review;
        _label = visit.Label ?? "";
        AnimalSuggestions = review.AnimalSuggestions();
        SuggestionText = visit.HasOpenSuggestion && visit.Suggestion is { } s
            ? s.Kind switch
            {
                SuggestionKind.Empty => "Bildene ser tomme ut.",
                SuggestionKind.Unsure => $"Forslag: kanskje {s.Name.ToLowerInvariant()}",
                _ => $"Forslag: {s.Name.ToLowerInvariant()}",
            }
            : null;
        CanUseSuggestion = visit.HasOpenSuggestion && visit.Suggestion?.Kind != SuggestionKind.Empty;
        Tiles = tiles;
        Rows = Review.Rows.Of(tiles, 4, items => new FrameRow(items));
        Title = $"Bildeserie {number} av {total}";
        Details = $"{placeName} · {Review.Rows.DateText(visit.Start)} · {Review.Rows.TimeSpanText(visit.Start, visit.End)} · {Review.Rows.Count(visit.Frames.Count)}";
        StartNewPlaceCommand = new RelayCommand(() => startNewPlace?.Invoke(), () => startNewPlace is not null);
        CanStartNewPlace = startNewPlace is not null;
        BackCommand = new RelayCommand(back);
        PreviousCommand = new RelayCommand(() => previous?.Invoke(), () => previous is not null);
        NextCommand = new RelayCommand(() => next?.Invoke(), () => next is not null);
        foreach (var tile in tiles) tile.Changed += UpdateKeptText;
        UpdateKeptText();
    }

    public IReadOnlyList<FrameTile> Tiles { get; }
    public IReadOnlyList<FrameRow> Rows { get; }
    public string Title { get; }
    public string Details { get; }

    [ObservableProperty] private string _keptText = "";

    /// <summary>What's in the pictures ("Kongeørn"). Optional; used in file names, tags and the folder name.</summary>
    [ObservableProperty] private string _label;

    public IReadOnlyList<string> AnimalSuggestions { get; }

    partial void OnLabelChanged(string value) => _review.SetLabel(_visit, value);

    public string? SuggestionText { get; }
    public bool CanUseSuggestion { get; private set; }

    [RelayCommand]
    private void UseSuggestion()
    {
        _review.AcceptSuggestion(_visit);
        Label = _visit.Suggestion!.Name;
        CanUseSuggestion = false;
        OnPropertyChanged(nameof(CanUseSuggestion));
    }
    [ObservableProperty] private FrameViewer? _viewer;

    public IRelayCommand BackCommand { get; }
    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand StartNewPlaceCommand { get; }
    public bool CanStartNewPlace { get; }

    [RelayCommand]
    private void KeepAll() => SetAll(true);

    [RelayCommand]
    private void DiscardAll() => SetAll(false);

    private void SetAll(bool keep)
    {
        _review.SetKeep(_visit, keep);
        foreach (var tile in Tiles) tile.SetKeepSilently(keep);
        UpdateKeptText();
    }

    private void UpdateKeptText()
    {
        var kept = Tiles.Count(t => t.Keep);
        KeptText = kept == Tiles.Count ? $"Alle {Tiles.Count:N0} bildene beholdes." : kept == 0 ? "Alle bildene sorteres bort."
            : $"{kept:N0} av {Tiles.Count:N0} bilder beholdes.";
    }

    public void OpenViewer(FrameTile tile)
    {
        Viewer?.Dispose();
        Viewer = new FrameViewer(this, Tiles.ToList().IndexOf(tile));
    }

    public void CloseViewer()
    {
        Viewer?.Dispose();
        Viewer = null;
    }
}

public sealed partial class FrameTile : ObservableObject
{
    private readonly ReviewService _review;
    private readonly Func<VisitScreen?> _screen;

    public FrameTile(SessionFile file, string imagePath, LazyThumbnail thumbnail, ReviewService review, Func<VisitScreen?> screen)
    {
        File = file;
        ImagePath = imagePath;
        Thumbnail = thumbnail;
        _review = review;
        _screen = screen;
        _keep = file.Keep;
        TimeText = file.TakenAt?.ToString("HH:mm:ss") ?? "";
    }

    public SessionFile File { get; }
    public string ImagePath { get; }
    public LazyThumbnail Thumbnail { get; }
    public string TimeText { get; }

    internal event Action? Changed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Opacity))]
    private bool _keep;

    public double Opacity => Keep ? 1 : 0.4;

    [RelayCommand]
    public void ToggleKeep() => SetKeep(!Keep);

    [RelayCommand]
    private void MarkKeep() => SetKeep(true);

    [RelayCommand]
    private void MarkDiscard() => SetKeep(false);

    private void SetKeep(bool keep)
    {
        if (Keep == keep) return;
        Keep = keep;
        _review.SetKeep(File.Id, Keep);
        Changed?.Invoke();
    }

    internal void SetKeepSilently(bool keep) => Keep = keep;

    [RelayCommand]
    private void Open() => _screen()?.OpenViewer(this);
}

/// <summary>One frame, large enough to tell a kongeørn from a ravn.</summary>
public sealed partial class FrameViewer : ObservableObject, IDisposable
{
    private const int DisplayWidth = 1600;
    private readonly VisitScreen _screen;
    private int _loadVersion;

    public FrameViewer(VisitScreen screen, int index)
    {
        _screen = screen;
        _index = index;
        Load();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tile), nameof(Position))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand))]
    private int _index;

    [ObservableProperty] private Bitmap? _image;

    public FrameTile Tile => _screen.Tiles[Index];
    public string Position => $"Bilde {Index + 1:N0} av {_screen.Tiles.Count:N0} · {Tile.TimeText}";

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous() => Go(Index - 1);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next() => Go(Index + 1);

    private bool HasPrevious() => Index > 0;
    private bool HasNext() => Index < _screen.Tiles.Count - 1;

    [RelayCommand]
    private void ToggleKeep() => Tile.ToggleKeep();

    [RelayCommand]
    private void Close() => _screen.CloseViewer();

    private void Go(int index)
    {
        Index = index;
        Load();
    }

    private async void Load()
    {
        var version = ++_loadVersion;
        var path = Tile.ImagePath;
        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(() =>
            {
                using var stream = System.IO.File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, DisplayWidth);
            });
        }
        catch (Exception)
        {
            bitmap = null; // not decodable: the thumbnail area just stays empty
        }

        if (version != _loadVersion)
        {
            bitmap?.Dispose(); // the user already moved on
            return;
        }
        var old = Image;
        Image = bitmap;
        old?.Dispose();
    }

    public void Dispose()
    {
        _loadVersion++;
        Image?.Dispose();
        Image = null;
    }
}
