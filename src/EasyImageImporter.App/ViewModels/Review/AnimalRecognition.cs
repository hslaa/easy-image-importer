using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Recognition;
using EasyImageImporter.Core.Review;
using Serilog;

namespace EasyImageImporter.App.ViewModels.Review;

public enum RecognitionStatus { NotInstalled, Downloading, DownloadFailed, Analysing, Done }

/// <summary>
/// Finds empty frames and suggests animals, in the background while the user reviews: downloads
/// the models once (on request), then analyses visit by visit on a low-priority thread.
/// </summary>
public sealed partial class AnimalRecognition : ObservableObject, IDisposable
{
    private readonly ModelStore _models;
    private readonly ImportStore _store;
    private readonly ReviewService _review;
    private Recognizer? _recognizer;
    private CancellationTokenSource? _run;
    private long? _sessionId;

    public AnimalRecognition(string modelsDir, ImportStore store, ReviewService review)
    {
        _models = new ModelStore(modelsDir);
        _store = store;
        _review = review;
        _status = _models.IsInstalled ? RecognitionStatus.Done : RecognitionStatus.NotInstalled;
    }

    /// <summary>A visit has been analysed (raised on the UI thread).</summary>
    public event Action<long>? VisitAnalysed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffered), nameof(IsBusy), nameof(IsFailed), nameof(IsVisible), nameof(HasNote))]
    private RecognitionStatus _status;

    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _text = "";

    public string DownloadSize => $"{ModelStore.TotalBytes / 1_000_000:N0} MB";
    public bool IsOffered => Status == RecognitionStatus.NotInstalled;
    public bool IsBusy => Status is RecognitionStatus.Downloading or RecognitionStatus.Analysing;
    public bool IsFailed => Status == RecognitionStatus.DownloadFailed;
    /// <summary>The panel: offering the download, or at work.</summary>
    public bool IsVisible => Status != RecognitionStatus.Done;
    /// <summary>Finished, with a word about it ("Alle hendelsene er sjekket").</summary>
    public bool HasNote => Status == RecognitionStatus.Done && Text.Length > 0;

    [RelayCommand]
    private async Task Download()
    {
        Status = RecognitionStatus.Downloading;
        Progress = 0;
        Text = "Laster ned dyregjenkjenning …";
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = 100.0 * p.Bytes / p.Total;
                Text = $"Laster ned dyregjenkjenning … {p.Bytes / 1_000_000:N0} av {p.Total / 1_000_000:N0} MB";
            });
            await Task.Run(() => _models.DownloadAsync(progress));
            Log.Information("Animal recognition models downloaded");
            Status = RecognitionStatus.Done;
            Text = "";
            if (_sessionId is { } session) Start(session);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
        {
            Log.Warning(ex, "Model download failed");
            Status = RecognitionStatus.DownloadFailed;
            Text = ex is InvalidDataException ? ex.Message : "Nedlastingen stoppet. Sjekk at maskinen er koblet til internett, og prøv igjen.";
        }
    }

    /// <summary>Starts (or resumes) analysing a session's visits, if the models are installed.</summary>
    public void Start(long sessionId)
    {
        _sessionId = sessionId;
        if (!_models.IsInstalled || Status is RecognitionStatus.Downloading) return;
        Stop();

        var run = _run = new CancellationTokenSource();
        Status = RecognitionStatus.Analysing;
        Text = "Ser etter dyr …";
        var thread = new Thread(() => Analyse(sessionId, run.Token))
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal, // reviewing must stay smooth on an older PC
            Name = "Animal recognition",
        };
        thread.Start();
    }

    public void Stop() => _run?.Cancel();

    private void Analyse(long sessionId, CancellationToken ct)
    {
        try
        {
            _recognizer ??= new Recognizer(_models.Folder);
            var progress = new SyncProgress<AnalysisProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                Progress = 100.0 * p.VisitsDone / Math.Max(1, p.VisitsTotal);
                Text = $"Ser etter dyr … {p.VisitsDone:N0} av {p.VisitsTotal:N0} hendelser";
                if (p.VisitId is { } id) VisitAnalysed?.Invoke(id);
            }));
            new AnimalAnalysis(_store, _review).Run(sessionId, _recognizer, progress, ct);
            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                Status = RecognitionStatus.Done;
                Text = "Alle hendelsene er sjekket for dyr. Forslagene er bare forslag – du bestemmer.";
            });
        }
        catch (OperationCanceledException)
        {
            // Stopped (e.g. saving started); results so far are kept and it resumes next time.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Animal recognition failed");
            Dispatcher.UIThread.Post(() =>
            {
                Status = RecognitionStatus.Done;
                Text = "Dyregjenkjenningen stoppet. Du kan gå gjennom bildene som vanlig.";
            });
        }
    }

    public void Dispose()
    {
        Stop();
        _recognizer?.Dispose();
    }

    /// <summary>Reports straight away on the analysing thread (Progress&lt;T&gt; would post to the wrong context).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
