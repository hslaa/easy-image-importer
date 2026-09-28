using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.IO;
using Serilog;

namespace EasyImageImporter.App.ViewModels;

/// <summary>
/// The two things worth changing: where the photos are saved, and how long the ones sorted away
/// are kept. Both are saved the moment they are changed.
/// </summary>
public sealed partial class SettingsScreen : Screen
{
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly DiscardedCleanup _cleanup;
    private readonly IFileSystem _fs;
    private readonly Func<Task<string?>> _pickFolder;

    public SettingsScreen(AppSettings settings, AppPaths paths, DiscardedCleanup cleanup, IFileSystem fs,
        Func<Task<string?>> pickFolder, Action close)
    {
        _settings = settings;
        _paths = paths;
        _cleanup = cleanup;
        _fs = fs;
        _pickFolder = pickFolder;
        CloseCommand = new RelayCommand(close);
        _folder = paths.ArchiveRoot;
        var days = settings.DiscardedKeepDays;
        KeepChoices = KeepChoice.All.Any(c => c.Days == days) ? KeepChoice.All : [.. KeepChoice.All, new KeepChoice(days, $"i {days} dager")];
        _keep = KeepChoices.First(c => c.Days == days);
        Locked = Platform.ArchiveOverride is not null;
        Refresh();
    }

    public IRelayCommand CloseCommand { get; }

    /// <summary>A development run points the folder somewhere else; don't pretend it can be changed.</summary>
    public bool Locked { get; }
    public bool CanChangeFolder => !Locked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDefaultFolder))]
    private string _folder;

    [ObservableProperty] private string _folderNote = "";
    [ObservableProperty] private string? _folderProblem;
    [ObservableProperty] private string _discardedNote = "";

    public bool IsDefaultFolder => string.Equals(Folder, Platform.DefaultArchive, StringComparison.OrdinalIgnoreCase);

    /// <summary>How long sorted-away photos are kept before they go to the recycle bin.</summary>
    public IReadOnlyList<KeepChoice> KeepChoices { get; }

    [ObservableProperty] private KeepChoice _keep;

    partial void OnKeepChanged(KeepChoice value)
    {
        _settings.DiscardedKeepDays = value.Days;
        Log.Information("Sorted-away photos are kept for {Days} days", value.Days?.ToString() ?? "ever");
        Refresh();
    }

    [ObservableProperty] private bool _hasDiscarded;

    [RelayCommand]
    private void OpenDiscarded() => Platform.OpenFolder(_paths.DiscardedRoot);

    [RelayCommand]
    private async Task ChooseFolder()
    {
        if (await _pickFolder() is not { } chosen) return;
        if (Problem(chosen) is { } problem)
        {
            FolderProblem = problem;
            return;
        }

        _settings.ArchiveRoot = chosen;
        _paths.ArchiveRoot = chosen;
        Folder = chosen;
        FolderProblem = null;
        Log.Information("Photos will be saved in {Folder}", chosen);
        Refresh();
    }

    [RelayCommand]
    private void UseDefaultFolder()
    {
        _settings.ArchiveRoot = null;
        _paths.ArchiveRoot = Platform.DefaultArchive;
        Folder = Platform.DefaultArchive;
        FolderProblem = null;
        Refresh();
    }

    /// <summary>Why this folder won't do, in words that say what to do about it.</summary>
    private string? Problem(string folder)
    {
        try
        {
            _fs.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".easyimageimporter-test");
            using (var stream = _fs.CreateNew(probe)) stream.WriteByte(0);
            _fs.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Cannot use {Folder} for photos", folder);
            return "Appen får ikke lagret i denne mappen. Velg en annen, for eksempel en mappe under Bilder.";
        }
    }

    private void Refresh()
    {
        FolderNote = Free() is { } free
            ? $"Det er {free} ledig på denne disken. Nye importer havner her; bilder som alt er lagret, blir liggende der de er."
            : "Nye importer havner her. Bilder som alt er lagret, blir liggende der de er.";

        var space = _cleanup.Measure();
        DiscardedNote = space.Files == 0
            ? "Ingen bortsorterte bilder er tatt vare på nå."
            : $"{space.Files:N0} bortsorterte bilder er tatt vare på nå, og bruker {Size(space.Bytes)}.";
        HasDiscarded = space.Files > 0 && _fs.DirectoryExists(_paths.DiscardedRoot);
    }

    private string? Free()
    {
        try
        {
            return _fs.DirectoryExists(Folder) ? Size(_fs.GetAvailableFreeSpace(Folder)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Size(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000.0:N1} GB"
        : $"{bytes / 1_000_000.0:N0} MB";
}

/// <summary>"i 30 dager": one choice of how long sorted-away photos are kept. Null days: for good.</summary>
public sealed record KeepChoice(int? Days, string Text)
{
    public static readonly IReadOnlyList<KeepChoice> All =
    [
        new(7, "i 7 dager"),
        new(AppSettings.DefaultKeepDays, "i 30 dager"),
        new(90, "i 90 dager"),
        new(365, "i ett år"),
        new(null, "for alltid"),
    ];

    public override string ToString() => Text;
}
