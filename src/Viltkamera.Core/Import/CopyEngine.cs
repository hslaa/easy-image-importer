using Viltkamera.Core.IO;

namespace Viltkamera.Core.Import;

public readonly record struct CopyProgress(int Done, int Total);

public enum CopyOutcomeKind { Completed, CardMissing, DiskFull, Cancelled }

public sealed record CopyOutcome(CopyOutcomeKind Kind, FileCounts Counts, long BytesNeeded = 0, long BytesAvailable = 0);

/// <summary>
/// Copies every pending file of a session from the card into staging and verifies it.
/// Safe to stop at any moment and run again: each file is either fully verified in the
/// database or still pending, and a half-written file is only ever a .tmp.
/// </summary>
public sealed class CopyEngine(IFileSystem fs, ImportStore store, AppPaths paths)
{
    public const int MaxAttempts = 3;

    /// <summary>Spare room kept free on the disk, on top of the images themselves.</summary>
    private const long SafetyMarginBytes = 200L * 1024 * 1024;

    private readonly VerifiedCopier _copier = new(fs);

    /// <summary>Pause before retrying a failed read, so a card being pulled has time to disappear.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public CopyOutcome Run(long sessionId, IProgress<CopyProgress>? progress = null, CancellationToken ct = default)
    {
        var session = store.GetSession(sessionId);
        if (session.State is not (SessionState.Copying or SessionState.PausedCardMissing or SessionState.PausedDiskFull))
            return Outcome(sessionId, CopyOutcomeKind.Completed);

        var stagingDir = paths.StagingDir(sessionId);
        fs.CreateDirectory(stagingDir);
        RemoveTempFiles(stagingDir);

        if (!fs.DirectoryExists(session.SourceRoot))
            return Pause(sessionId, SessionState.PausedCardMissing, CopyOutcomeKind.CardMissing);

        var pending = store.GetFiles(sessionId, FileStatus.Pending);
        var needed = pending.Sum(f => f.Size) + pending.Sum(f => f.Size) / 10 + SafetyMarginBytes;
        var available = fs.GetAvailableFreeSpace(stagingDir);
        if (needed > available)
            return Pause(sessionId, SessionState.PausedDiskFull, CopyOutcomeKind.DiskFull, needed, available);

        store.SetState(sessionId, SessionState.Copying);
        var total = store.GetCounts(sessionId).Total;
        var done = total - pending.Count;
        progress?.Report(new CopyProgress(done, total));

        foreach (var file in pending)
        {
            if (ct.IsCancellationRequested) return Outcome(sessionId, CopyOutcomeKind.Cancelled);

            try
            {
                CopyOne(session, file, stagingDir, ct);
            }
            catch (OperationCanceledException)
            {
                return Outcome(sessionId, CopyOutcomeKind.Cancelled);
            }
            catch (IOException) when (!fs.DirectoryExists(session.SourceRoot))
            {
                return Pause(sessionId, SessionState.PausedCardMissing, CopyOutcomeKind.CardMissing);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                return Pause(sessionId, SessionState.PausedDiskFull, CopyOutcomeKind.DiskFull,
                    needed, fs.GetAvailableFreeSpace(stagingDir));
            }

            done++;
            progress?.Report(new CopyProgress(done, total));
        }

        store.SetState(sessionId, SessionState.Copied);
        return Outcome(sessionId, CopyOutcomeKind.Completed);
    }

    private void CopyOne(Session session, SessionFile file, string stagingDir, CancellationToken ct)
    {
        var source = file.SourcePath(session.SourceRoot);
        var stagingName = file.Id + Path.GetExtension(file.RelPath).ToLowerInvariant();
        var staged = Path.Combine(stagingDir, stagingName);
        var temp = Path.Combine(stagingDir, file.Id + ".tmp");

        // A staged file without a Verified row means we stopped between rename and commit.
        // We can't trust it without the source hash, so drop it and copy again.
        if (fs.FileExists(staged)) fs.Delete(staged);

        string? lastError = null;
        for (var attempt = file.Attempts + 1; attempt <= file.Attempts + MaxAttempts; attempt++)
        {
            CopyResult result;
            try
            {
                result = _copier.Copy(source, temp, ct);
            }
            catch (IOException ex) when (fs.DirectoryExists(session.SourceRoot) && !IsDiskFull(ex))
            {
                // A bad sector or flaky reader: worth another try.
                lastError = ex.Message;
                Thread.Sleep(RetryDelay);
                continue;
            }

            if (result.Status == CopyStatus.Mismatch)
            {
                lastError = "Kopien var ikke lik originalen.";
                continue;
            }

            if (store.IsAlreadyKept(result.SourceHash, session.Id))
            {
                fs.Delete(temp);
                store.MarkDuplicate(file.Id, result.SourceHash, attempt);
            }
            else
            {
                fs.Move(temp, staged);
                store.MarkVerified(file.Id, result.SourceHash, stagingName, attempt);
            }
            return;
        }

        // Repeated read errors are often the card on its way out. That is a pause, not a failure.
        if (!fs.DirectoryExists(session.SourceRoot)) throw new IOException("Kortet ble tatt ut.");

        store.MarkFailed(file.Id, file.Attempts + MaxAttempts, lastError ?? "Ukjent feil.");
    }

    private void RemoveTempFiles(string stagingDir)
    {
        foreach (var path in fs.EnumerateFiles(stagingDir).Where(p => p.EndsWith(".tmp", StringComparison.Ordinal)))
            fs.Delete(path);
    }

    private CopyOutcome Pause(long sessionId, SessionState state, CopyOutcomeKind kind, long needed = 0, long available = 0)
    {
        store.SetState(sessionId, state);
        return Outcome(sessionId, kind, needed, available);
    }

    private CopyOutcome Outcome(long sessionId, CopyOutcomeKind kind, long needed = 0, long available = 0) =>
        new(kind, store.GetCounts(sessionId), needed, available);

    // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL on Windows, ENOSPC on Unix.
    internal static bool IsDiskFull(IOException ex) =>
        ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) || (!OperatingSystem.IsWindows() && ex.HResult == 28);
}
