using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using EasyImageImporter.Core.Cards;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.IO;
using EasyImageImporter.Core.Storage;

namespace EasyImageImporter.App.ViewModels;

/// <summary>
/// Drives the linear flow: card in → copy + verify → save → erase. Every decision is taken from
/// the session state in the database, so the app picks up wherever it was, whenever it starts.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ImportStore _store;
    private readonly CardScanner _scanner;
    private readonly CopyEngine _copier;
    private readonly Finalizer _finalizer;
    private readonly CardEraser _eraser;
    private readonly Recovery _recovery;
    private readonly CardWatcher _watcher = new(new SystemDriveProvider());

    private bool _busy;
    private Session? _current;

    /// <summary>Set by the window: shows a folder picker and returns the chosen path.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>Raised when something needs the user's attention, so the app can bring its window forward.</summary>
    public event Action? AttentionNeeded;

    [ObservableProperty] private Screen _screen;

    public MainViewModel()
    {
        var paths = Platform.Paths();
        IFileSystem fs = new PhysicalFileSystem();
        _store = new ImportStore(new Database(paths.DatabasePath));
        _scanner = new CardScanner(fs, _store);
        _copier = new CopyEngine(fs, _store, paths);
        _finalizer = new Finalizer(fs, _store, paths);
        _eraser = new CardEraser(fs, _store);
        _recovery = new Recovery(_store, _finalizer);
        Screen = Idle();
    }

    public void Start()
    {
        _watcher.CardInserted += card => Dispatcher.UIThread.Post(() => _ = OnCardAsync(card.Root, card.Label));
        _watcher.CardRemoved += root => Dispatcher.UIThread.Post(() => OnCardRemoved(root));
        _ = RunGuardedAsync(ResumeAtStartupAsync);
        _watcher.Start();
    }

    private async Task ResumeAtStartupAsync()
    {
        Show(Working("Starter…"));
        var waiting = await Task.Run(_recovery.Run);
        var session = waiting.LastOrDefault();
        if (session is null)
        {
            Show(Idle());
            return;
        }

        // Something was left unfinished: show it, even if we started quietly in the tray.
        Log.Information("Resuming session {Session} in state {State}", session.Id, session.State);
        _current = session;
        AttentionNeeded?.Invoke();
        await ContinueAsync(session);
    }

    private async Task OnCardAsync(string root, string? label)
    {
        if (_busy) return;
        await RunGuardedAsync(async () =>
        {
            Log.Information("Card inserted at {Root}", root);
            Show(Working("Leser kortet…"));
            AttentionNeeded?.Invoke();
            var session = await Task.Run(() => _scanner.OpenSession(_scanner.Scan(root), label));
            if (session is null)
            {
                Show(new MessageScreen("Fant ingen bilder på kortet.", "Det er ingen bilder eller videoer på dette kortet."));
                return;
            }

            _current = session;
            await ContinueAsync(session);
        });
    }

    private void OnCardRemoved(string root)
    {
        // After the card is emptied, pulling it out returns to the start screen.
        if (!_busy && _current is { State: SessionState.CardErased } && _current.SourceRoot == root)
        {
            _current = null;
            Show(Idle());
        }
    }

    /// <summary>Takes the next step for a session based on where it is.</summary>
    private Task ContinueAsync(Session session)
    {
        session = _store.GetSession(session.Id);
        _current = session;
        return session.State switch
        {
            SessionState.Copying or SessionState.PausedCardMissing or SessionState.PausedDiskFull => CopyAsync(session),
            SessionState.Copied => ShowCopiedAsync(session),
            SessionState.Finalizing => SaveAsync(session),
            SessionState.Imported => ShowDoneAsync(session),
            _ => ShowIdleAsync(),
        };

        Task ShowIdleAsync()
        {
            Show(Idle());
            return Task.CompletedTask;
        }
    }

    private async Task CopyAsync(Session session)
    {
        var total = _store.GetCounts(session.Id).Total;
        var screen = Working($"Fant {total:N0} bilder på kortet.", "Kopierer…");
        screen.IsIndeterminate = false;
        Show(screen);

        var progress = new Progress<CopyProgress>(p =>
        {
            screen.Detail = $"Kopierer bilde {Math.Min(p.Done + 1, p.Total):N0} av {p.Total:N0}…";
            screen.Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
        });
        var outcome = await Task.Run(() => _copier.Run(session.Id, progress));
        Log.Information("Copy for session {Session}: {Outcome} {@Counts}", session.Id, outcome.Kind, outcome.Counts);

        switch (outcome.Kind)
        {
            case CopyOutcomeKind.Completed:
                await ShowCopiedAsync(session);
                break;
            case CopyOutcomeKind.CardMissing:
                Show(new MessageScreen("Kortet ble tatt ut.",
                    "Sett kortet inn igjen, så fortsetter kopieringen der den stoppet. Ingen bilder er borte."));
                break;
            case CopyOutcomeKind.DiskFull:
                Show(new MessageScreen("Det er ikke nok plass på datamaskinen.",
                    $"Bildene trenger omtrent {Gigabytes(outcome.BytesNeeded)} GB, men det er bare {Gigabytes(outcome.BytesAvailable)} GB ledig. " +
                    "Frigjør plass, og trykk så «Prøv igjen». Ingenting er slettet fra kortet.",
                    "Prøv igjen", () => RunGuardedAsync(() => CopyAsync(session))));
                break;
            case CopyOutcomeKind.Cancelled:
                Show(Idle());
                break;
        }
    }

    private Task ShowCopiedAsync(Session session)
    {
        var counts = _store.GetCounts(session.Id);
        Show(new CopiedScreen(counts.Verified, counts.Duplicate, counts.Failed,
            save: () => RunGuardedAsync(() => SaveAsync(session)),
            retry: () => RunGuardedAsync(() =>
            {
                _store.RetryFailed(session.Id);
                return CopyAsync(session);
            })));
        return Task.CompletedTask;
    }

    private async Task SaveAsync(Session session)
    {
        Show(Working("Lagrer bildene…"));
        var import = await Task.Run(() => _finalizer.Run(session.Id));
        Log.Information("Session {Session} saved to {Folder}", session.Id, import?.FolderPath ?? "(nothing new)");
        await ShowDoneAsync(session);
    }

    private Task ShowDoneAsync(Session session)
    {
        var import = _store.GetImportForSession(session.Id);
        var decision = _eraser.Evaluate(session.Id);
        Show(new DoneScreen(import?.FolderPath, import?.ImageCount ?? 0, decision.Count, decision.Reason,
            erase: () => RunGuardedAsync(() => EraseAsync(session))));
        return Task.CompletedTask;
    }

    private async Task EraseAsync(Session session)
    {
        var count = _eraser.Evaluate(session.Id).Count;
        var screen = Working("Sletter bildene fra kortet…");
        screen.IsIndeterminate = false;
        Show(screen);

        var progress = new Progress<int>(done => screen.Progress = 100.0 * done / Math.Max(count, 1));
        var outcome = await Task.Run(() => _eraser.Erase(session.Id, progress));
        Log.Information("Erase for session {Session}: {Outcome}, {Erased} erased, {Reason}",
            session.Id, outcome.Kind, outcome.Erased, outcome.Reason);
        _current = _store.GetSession(session.Id);

        Show(outcome.Kind switch
        {
            EraseOutcomeKind.Completed => new MessageScreen("Kortet er klart for neste tur.",
                "Alle bildene er trygt lagret på datamaskinen. Du kan ta ut kortet."),
            EraseOutcomeKind.CardMissing => new MessageScreen("Kortet ble tatt ut.",
                "Sett kortet inn igjen for å slette resten av bildene. Alle bildene er trygt lagret."),
            _ => new MessageScreen("Kortet ble ikke slettet helt.",
                (outcome.Reason ?? "Noe gikk galt.") + " Bildene som er igjen på kortet, blir ikke rørt."),
        });
    }

    /// <summary>Runs one step at a time, and turns surprises into a calm message instead of a crash.</summary>
    private async Task RunGuardedAsync(Func<Task> step)
    {
        _busy = true;
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Step failed");
            Show(new MessageScreen("Noe gikk galt.",
                "Ingen bilder er slettet. Ta ut kortet og sett det inn igjen for å prøve på nytt.\n\n" + ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show(Screen screen) => Screen = screen;

    private static WorkingScreen Working(string title, string detail = "") => new() { Title = title, Detail = detail };

    private IdleScreen Idle() => new(ChooseFolderAsync);

    private async Task ChooseFolderAsync()
    {
        if (PickFolder is null) return;
        var folder = await PickFolder();
        if (folder is not null) await OnCardAsync(folder, null);
    }

    private static string Gigabytes(long bytes) => (bytes / 1_000_000_000.0).ToString("N1");

    public void Dispose() => _watcher.Dispose();
}
