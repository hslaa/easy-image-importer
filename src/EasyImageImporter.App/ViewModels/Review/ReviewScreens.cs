using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.App.ViewModels.Review;

// Lists are split into fixed-width rows so the list can be virtualized: only rows on screen exist.
public sealed record VisitRow(IReadOnlyList<VisitCard> Items);
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
}

/// <summary>All visits on the card. "Default is keep everything": the user actively sorts away.</summary>
public sealed partial class ReviewScreen(
    ReviewOverview overview, IReadOnlyList<VisitCard> cards, string? alreadyImportedText, string? failedText,
    Func<Task> save, Func<Task> retryFailed) : Screen
{
    public IReadOnlyList<VisitRow> Rows { get; } = Review.Rows.Of(cards, 3, items => new VisitRow(items));

    public string Summary { get; } =
        $"{Review.Rows.Count(overview.ImageCount)} i {overview.Visits.Count:N0} " +
        $"{(overview.Visits.Count == 1 ? "hendelse" : "hendelser")}. " +
        (overview.KeptCount == overview.ImageCount
            ? "Alle blir lagret."
            : $"{Review.Rows.Count(overview.KeptCount)} blir lagret, {overview.ImageCount - overview.KeptCount:N0} sorteres bort.");

    public string? VideosText { get; } = overview.Videos.Count switch
    {
        0 => null,
        1 => "1 video blir lagret uten gjennomgang.",
        var n => $"{n:N0} videoer blir lagret uten gjennomgang.",
    };

    public string? AlreadyImportedText { get; } = alreadyImportedText;
    public string? FailedText { get; } = failedText;

    [RelayCommand]
    private Task Save() => save();

    [RelayCommand]
    private Task Retry() => retryFailed();
}

public sealed partial class VisitCard(Visit visit, int number, LazyThumbnail cover, Action<Visit> open, Action<Visit, bool> setKeep)
    : ObservableObject
{
    public LazyThumbnail Cover { get; } = cover;
    public string Title { get; } = $"{number}. {Rows.TimeSpanText(visit.Start, visit.End)}";
    public string Details { get; } = $"{Rows.DateText(visit.Start)} · {Rows.Count(visit.Frames.Count)}";

    public string KeepText { get; } =
        visit.KeptCount == visit.Frames.Count ? "Alle beholdes"
        : visit.KeptCount == 0 ? "Sorteres bort"
        : $"{visit.KeptCount:N0} av {visit.Frames.Count:N0} beholdes";

    public bool IsDiscarded { get; } = visit.KeptCount == 0;
    public double Opacity => IsDiscarded ? 0.45 : 1;
    public string ToggleText => IsDiscarded ? "Behold" : "Sorter bort";

    [RelayCommand]
    private void Open() => open(visit);

    [RelayCommand]
    private void Toggle() => setKeep(visit, IsDiscarded);
}

/// <summary>One visit: every frame, keep or sort away one by one, split and merge.</summary>
public sealed partial class VisitScreen : Screen
{
    private readonly Visit _visit;
    private readonly ReviewService _review;
    private readonly Action<Visit, long> _split;

    public VisitScreen(Visit visit, int number, int total, IReadOnlyList<FrameTile> tiles, ReviewService review,
        Action back, Action? previous, Action? next, Action? mergeWithNext, Action<Visit, long> split)
    {
        _visit = visit;
        _review = review;
        _split = split;
        Tiles = tiles;
        Rows = Review.Rows.Of(tiles, 4, items => new FrameRow(items));
        Title = $"Hendelse {number} av {total}";
        Details = $"{Review.Rows.DateText(visit.Start)} · {Review.Rows.TimeSpanText(visit.Start, visit.End)} · {Review.Rows.Count(visit.Frames.Count)}";
        BackCommand = new RelayCommand(back);
        PreviousCommand = new RelayCommand(() => previous?.Invoke(), () => previous is not null);
        NextCommand = new RelayCommand(() => next?.Invoke(), () => next is not null);
        MergeWithNextCommand = new RelayCommand(() => mergeWithNext?.Invoke(), () => mergeWithNext is not null);
        foreach (var tile in tiles) tile.Changed += UpdateKeptText;
        UpdateKeptText();
    }

    public IReadOnlyList<FrameTile> Tiles { get; }
    public IReadOnlyList<FrameRow> Rows { get; }
    public string Title { get; }
    public string Details { get; }

    [ObservableProperty] private string _keptText = "";
    [ObservableProperty] private FrameViewer? _viewer;

    public IRelayCommand BackCommand { get; }
    public IRelayCommand PreviousCommand { get; }
    public IRelayCommand NextCommand { get; }
    public IRelayCommand MergeWithNextCommand { get; }

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
        KeptText = kept == Tiles.Count ? "Alle beholdes." : kept == 0 ? "Alle sorteres bort." : $"{kept:N0} av {Tiles.Count:N0} beholdes.";
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

    internal void SplitAt(FrameTile tile)
    {
        CloseViewer();
        _split(_visit, tile.File.Id);
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
    [NotifyPropertyChangedFor(nameof(KeepText), nameof(Opacity))]
    private bool _keep;

    public string KeepText => Keep ? "✓ Beholdes" : "Sorteres bort";
    public double Opacity => Keep ? 1 : 0.4;

    [RelayCommand]
    public void ToggleKeep()
    {
        Keep = !Keep;
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
    [NotifyPropertyChangedFor(nameof(Tile), nameof(Position), nameof(CanSplit))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(NextCommand), nameof(SplitHereCommand))]
    private int _index;

    [ObservableProperty] private Bitmap? _image;

    public FrameTile Tile => _screen.Tiles[Index];
    public string Position => $"Bilde {Index + 1:N0} av {_screen.Tiles.Count:N0} · {Tile.TimeText}";
    public bool CanSplit => Index > 0;

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private void Previous() => Go(Index - 1);

    [RelayCommand(CanExecute = nameof(HasNext))]
    private void Next() => Go(Index + 1);

    private bool HasPrevious() => Index > 0;
    private bool HasNext() => Index < _screen.Tiles.Count - 1;

    [RelayCommand]
    private void ToggleKeep() => Tile.ToggleKeep();

    [RelayCommand(CanExecute = nameof(CanSplit))]
    private void SplitHere() => _screen.SplitAt(Tile);

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
