using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EasyImageImporter.App.ViewModels;

/// <summary>One screen = one thing to look at and at most one primary button.</summary>
public abstract class Screen : ObservableObject
{
    /// <summary>Where in the flow this screen is (see <see cref="FlowStep"/>); 0 hides the step indicator.</summary>
    public virtual int Step => 0;
}

/// <summary>The four steps shown at the top: Kopier · Gå gjennom · Navn og merking · Tøm kortet.</summary>
public sealed record FlowStep(int Number, string Label, bool IsDone, bool IsCurrent)
{
    public const int Copy = 1, Review = 2, Naming = 3, Erase = 4, AllDone = 5;

    private static readonly string[] Labels = ["Kopier", "Gå gjennom", "Navn og merking", "Tøm kortet"];

    public bool IsTodo => !IsDone && !IsCurrent;
    public bool IsLast => Number == Labels.Length;

    public static IReadOnlyList<FlowStep> For(int current) =>
        current == 0 ? [] : Labels.Select((label, i) => new FlowStep(i + 1, label, i + 1 < current, i + 1 == current)).ToList();
}

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
    public int InStep { get; init; }
    public override int Step => InStep;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate = true;
}

public enum MessageTone { Info, Success, Problem }

/// <summary>Information, or a problem, with an optional single action.</summary>
public sealed partial class MessageScreen(string title, string body, string? actionText = null, Func<Task>? action = null) : Screen
{
    public MessageTone Tone { get; init; }
    public int InStep { get; init; }
    public override int Step => InStep;
    public bool IsSuccess => Tone == MessageTone.Success;
    public bool IsProblem => Tone == MessageTone.Problem;
    public bool IsInfo => Tone == MessageTone.Info;

    public string Title { get; } = title;
    public string Body { get; } = body;
    public string? ActionText { get; } = actionText;
    public bool HasAction => action is not null;

    [RelayCommand]
    private Task Act() => action?.Invoke() ?? Task.CompletedTask;
}

public sealed partial class CopiedScreen(int copied, int alreadyImported, int failed, Func<Task> save, Func<Task> retry) : Screen
{
    public override int Step => FlowStep.Review;

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

/// <summary>One saved folder, with a button that opens it in Explorer.</summary>
public sealed partial class FolderLink(string path, int imageCount, int discardedCount)
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public string CountText { get; } = $"{imageCount:N0} {(imageCount == 1 ? "bilde" : "bilder")}" +
                                       (discardedCount > 0 ? $", {discardedCount:N0} sortert bort" : "");
    public bool Exists { get; } = Directory.Exists(path);
    public bool Missing => !Exists;

    [RelayCommand]
    private void Open() => Platform.OpenFolder(Path);
}

public sealed partial class DoneScreen : Screen
{
    public override int Step => FlowStep.Erase;

    public DoneScreen(IReadOnlyList<FolderLink> folders, int savedCount, int discardedCount, int eraseCount,
        string? eraseRefusal, bool canUndo, Func<Task> erase, Func<Task> undo)
    {
        DiscardedText = discardedCount == 0 ? null
            : $"{discardedCount:N0} {(discardedCount == 1 ? "bilde" : "bilder")} er sortert bort. " +
              $"De ligger i undermappen «{EasyImageImporter.Core.Import.Finalizer.DiscardedFolderName}», og er ikke slettet.";
        Erase = new Confirmation(erase);
        Undo = new Confirmation(undo);
        CanUndo = canUndo;
        Folders = folders;
        Title = folders.Count == 0
            ? "Alle bildene på kortet var lagret fra før."
            : $"{savedCount:N0} {(savedCount == 1 ? "bilde er" : "bilder er")} lagret.";
        FoldersText = folders.Count == 1 ? "Du finner dem i denne mappen:" : $"Du finner dem i disse {folders.Count} mappene:";
        EraseCount = eraseCount;
        EraseRefusal = eraseRefusal;
    }

    public string Title { get; }
    public string FoldersText { get; }
    public string? DiscardedText { get; }
    public IReadOnlyList<FolderLink> Folders { get; }
    public bool HasFolder => Folders.Count > 0;

    public int EraseCount { get; }
    public string? EraseRefusal { get; }
    /// <summary>The erase step does not exist until everything is verified: absent, not greyed out.</summary>
    public bool CanErase => EraseRefusal is null && EraseCount > 0;
    public bool HasRefusal => EraseRefusal is not null;
    public string EraseQuestion => EraseCount == 1
        ? "Bildet er kopiert og kontrollert. Slett det fra kortet, så er kortet klart til neste tur."
        : $"Alle {EraseCount:N0} bildene er kopiert og kontrollert. Slett dem fra kortet, så er kortet klart til neste tur.";
    public string ConfirmText => $"Ja, slett {EraseCount:N0} {(EraseCount == 1 ? "bilde" : "bilder")} fra kortet";

    public Confirmation Erase { get; }
    public Confirmation Undo { get; }
    public bool CanUndo { get; }
}

/// <summary>"Mine importer": every past import, so nobody ever has to remember or type a path.</summary>
public sealed partial class ImportsScreen(IReadOnlyList<ImportRow> rows, Action close) : Screen
{
    public IReadOnlyList<ImportRow> Rows { get; } = rows;
    public bool IsEmpty => Rows.Count == 0;

    [RelayCommand]
    private void Close() => close();
}

/// <summary>One import (one card): its folders, one per place, and a way back for the first day.</summary>
public sealed partial class ImportRow : ObservableObject
{
    public ImportRow(IReadOnlyList<FolderLink> folders, DateTime createdUtc, bool canUndo, Func<Task> undo)
    {
        Undo = new Confirmation(undo);
        Folders = folders;
        Details = $"Lagret {createdUtc.ToLocalTime():d. MMMM yyyy 'kl.' HH:mm}";
        CanUndo = canUndo;
        UndoText = folders.Count > 1
            ? $"Angre hele importen? Bildene i alle {folders.Count} mappene flyttes tilbake, og du kan lagre dem på nytt. Ingen bilder blir slettet."
            : "Angre denne importen? Bildene flyttes tilbake fra mappen, og du kan lagre dem på nytt. Ingen bilder blir slettet.";
    }

    public IReadOnlyList<FolderLink> Folders { get; }
    public string Details { get; }
    public bool CanUndo { get; }
    public string UndoText { get; }
    public Confirmation Undo { get; }
}
