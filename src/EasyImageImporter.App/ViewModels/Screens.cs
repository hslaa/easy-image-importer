using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EasyImageImporter.App.ViewModels;

/// <summary>One screen = one thing to look at and at most one primary button.</summary>
public abstract class Screen : ObservableObject;

/// <summary>
/// A yes-button that only works after it has been visible for a moment. The confirm button
/// appears where the first button was, so without this a double-click would confirm by accident.
/// </summary>
public sealed partial class Confirmation(Func<Task> onConfirm) : ObservableObject
{
    private static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(800);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private bool _isArmed;

    [ObservableProperty] private bool _isAsking;

    [RelayCommand]
    private async Task Ask()
    {
        IsArmed = false;
        IsAsking = true;
        await Task.Delay(ArmDelay);
        IsArmed = IsAsking;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsAsking = false;
        IsArmed = false;
    }

    [RelayCommand(CanExecute = nameof(IsArmed))]
    private Task Confirm() => onConfirm();
}

public sealed partial class IdleScreen(Func<Task> chooseFolder, Action showImports) : Screen
{
    [RelayCommand]
    private Task ChooseFolder() => chooseFolder();

    [RelayCommand]
    private void ShowImports() => showImports();
}

public sealed partial class WorkingScreen : Screen
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
}

/// <summary>Information, or a problem, with an optional single action.</summary>
public sealed partial class MessageScreen(string title, string body, string? actionText = null, Func<Task>? action = null) : Screen
{
    public string Title { get; } = title;
    public string Body { get; } = body;
    public string? ActionText { get; } = actionText;
    public bool HasAction => action is not null;

    [RelayCommand]
    private Task Act() => action?.Invoke() ?? Task.CompletedTask;
}

public sealed partial class CopiedScreen(int copied, int alreadyImported, int failed, Func<Task> save, Func<Task> retry) : Screen
{
    public string Title { get; } = copied == 1
        ? "1 bilde er kopiert og kontrollert."
        : $"{copied:N0} bilder er kopiert og kontrollert.";

    public string? AlreadyImportedText { get; } = alreadyImported == 0 ? null
        : $"{alreadyImported:N0} av bildene var importert fra før og blir hoppet over.";

    public string? FailedText { get; } = failed == 0 ? null
        : $"{failed:N0} {(failed == 1 ? "bilde" : "bilder")} kunne ikke kopieres trygt. Kortet vil ikke bli slettet før dette er løst.";

    public bool HasAlreadyImported => AlreadyImportedText is not null;
    public bool HasFailed => FailedText is not null;
    public bool HasImagesToSave => copied > 0;

    [RelayCommand]
    private Task Save() => save();

    [RelayCommand]
    private Task Retry() => retry();
}

public sealed partial class DoneScreen : Screen
{
    public DoneScreen(string? folder, int savedCount, int eraseCount, string? eraseRefusal, bool canUndo,
        Func<Task> erase, Func<Task> undo)
    {
        Erase = new Confirmation(erase);
        Undo = new Confirmation(undo);
        CanUndo = canUndo;
        Folder = folder;
        Title = folder is null
            ? "Alle bildene på kortet var lagret fra før."
            : $"Ferdig. {savedCount:N0} {(savedCount == 1 ? "bilde er" : "bilder er")} lagret i:";
        EraseCount = eraseCount;
        EraseRefusal = eraseRefusal;
    }

    public string Title { get; }
    public string? Folder { get; }
    public bool HasFolder => Folder is not null;

    public int EraseCount { get; }
    public string? EraseRefusal { get; }
    /// <summary>The erase step does not exist until everything is verified: absent, not greyed out.</summary>
    public bool CanErase => EraseRefusal is null && EraseCount > 0;
    public bool HasRefusal => EraseRefusal is not null;
    public string EraseQuestion => $"{EraseCount:N0} {(EraseCount == 1 ? "bilde er" : "bilder er")} kopiert og kontrollert. Slett dem fra kortet?";
    public string ConfirmText => $"Ja, slett {EraseCount:N0} {(EraseCount == 1 ? "bilde" : "bilder")} fra kortet";

    public Confirmation Erase { get; }
    public Confirmation Undo { get; }
    public bool CanUndo { get; }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Folder is not null) Platform.OpenFolder(Folder);
    }
}

/// <summary>"Mine importer": every past import, so nobody ever has to remember or type a path.</summary>
public sealed partial class ImportsScreen(IReadOnlyList<ImportRow> rows, Action close) : Screen
{
    public IReadOnlyList<ImportRow> Rows { get; } = rows;
    public bool IsEmpty => Rows.Count == 0;

    [RelayCommand]
    private void Close() => close();
}

public sealed partial class ImportRow : ObservableObject
{
    public ImportRow(string folder, DateTime createdUtc, int imageCount, bool folderExists, bool canUndo, Func<Task> undo)
    {
        Undo = new Confirmation(undo);
        Folder = folder;
        Name = Path.GetFileName(folder);
        Details = $"Lagret {createdUtc.ToLocalTime():d. MMMM yyyy 'kl.' HH:mm} · {imageCount:N0} {(imageCount == 1 ? "bilde" : "bilder")}";
        FolderExists = folderExists;
        CanUndo = canUndo;
    }

    public string Folder { get; }
    public string Name { get; }
    public string Details { get; }
    public bool FolderExists { get; }
    public bool FolderMissing => !FolderExists;
    public bool CanUndo { get; }
    public Confirmation Undo { get; }

    [RelayCommand]
    private void OpenFolder() => Platform.OpenFolder(Folder);
}
