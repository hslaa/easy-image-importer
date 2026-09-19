using EasyImageImporter.Core.IO;

namespace EasyImageImporter.Core.Cards;

public sealed record CardInfo(string Root, string? Label);

public interface IDriveProvider
{
    /// <summary>Mounted volumes that look like a camera card: ready, not the system disk, has a DCIM folder.</summary>
    IReadOnlyList<CardInfo> GetCards();
}

public sealed class SystemDriveProvider : IDriveProvider
{
    public IReadOnlyList<CardInfo> GetCards()
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "/";
        var cards = new List<CardInfo>();
        foreach (var drive in Drives.GetDrives())
        {
            try
            {
                var root = drive.RootDirectory.FullName;
                if (!drive.IsReady || root == systemRoot || root == "/") continue;
                if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram) continue;
                if (!Directory.Exists(Path.Combine(root, "DCIM"))) continue;
                // macOS reports the mount path as the label; only keep a real name.
                var label = drive.VolumeLabel;
                cards.Add(new CardInfo(root, string.IsNullOrWhiteSpace(label) || label == root ? null : label));
            }
            catch (IOException)
            {
                // Drive vanished while we looked at it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return cards;
    }
}

/// <summary>
/// Polls for camera cards. Polling is dull on purpose: it behaves the same on Windows and macOS
/// and has no device-notification plumbing to go wrong. Events fire on a thread-pool thread.
/// </summary>
public sealed class CardWatcher(IDriveProvider drives, TimeSpan? interval = null) : IDisposable
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _stop = new();
    private HashSet<string> _present = [];

    public event Action<CardInfo>? CardInserted;
    public event Action<string>? CardRemoved;

    public IReadOnlyList<CardInfo> Current { get; private set; } = [];

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>One poll. Public for tests.</summary>
    public void Poll()
    {
        var cards = drives.GetCards();
        var roots = cards.Select(c => c.Root).ToHashSet();
        Current = cards;

        foreach (var card in cards.Where(c => !_present.Contains(c.Root))) CardInserted?.Invoke(card);
        foreach (var root in _present.Where(r => !roots.Contains(r))) CardRemoved?.Invoke(root);
        _present = roots;
    }

    private async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                Poll();
            }
            catch (Exception)
            {
                // Never let a flaky drive stop card detection. Next poll tries again.
            }
        } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
