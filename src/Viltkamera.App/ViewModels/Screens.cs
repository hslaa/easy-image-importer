using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Viltkamera.App.ViewModels;

/// <summary>One screen = one thing to look at and at most one primary button.</summary>
public abstract class Screen : ObservableObject;

public sealed partial class IdleScreen(Func<Task> chooseFolder) : Screen
{
    [RelayCommand]
    private Task ChooseFolder() => chooseFolder();
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
    private readonly Func<Task> _erase;

    public DoneScreen(string? folder, int savedCount, int eraseCount, string? eraseRefusal, Func<Task> erase)
    {
        _erase = erase;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    private bool _isConfirming;

    public bool IsAsking => CanErase && !IsConfirming;

    [RelayCommand]
    private void OpenFolder()
    {
        if (Folder is not null) Platform.OpenFolder(Folder);
    }

    [RelayCommand]
    private void AskErase() => IsConfirming = true;

    [RelayCommand]
    private void CancelErase() => IsConfirming = false;

    [RelayCommand]
    private Task ConfirmErase() => _erase();
}
