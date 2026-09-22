using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.IO;
using Serilog;

namespace EasyImageImporter.App.ViewModels;

/// <summary>
/// The two things worth changing: where the photos are saved, and what happens to the ones
/// sorted away. Both are saved the moment they are changed.
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
        _toRecycleBin = settings.DiscardedToRecycleBin;
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

    /// <summary>On: sorted-away photos go to the recycle bin a day after the import, still restorable.</summary>
    [ObservableProperty] private bool _toRecycleBin;

    partial void OnToRecycleBinChanged(bool value)
    {
        _settings.DiscardedToRecycleBin = value;
        Log.Information("Sorted-away photos go to the recycle bin: {Value}", value);
        Refresh();
    }

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
            ? "Ingen bortsorterte bilder ligger lagret nå."
            : $"{space.Files:N0} bortsorterte bilder ligger lagret nå, og bruker {Size(space.Bytes)}." +
              (ToRecycleBin ? " De flyttes til papirkurven ett døgn etter importen." : "");
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
