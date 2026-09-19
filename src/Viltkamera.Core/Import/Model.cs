namespace Viltkamera.Core.Import;

/// <summary>
/// Where an import session is. The UI renders whatever state the session is in, which is
/// what makes "close the app and continue three days later" work.
/// </summary>
public enum SessionState
{
    Copying,
    PausedCardMissing,
    PausedDiskFull,
    Copied,
    Finalizing,
    Imported,
    CardErased,
}

public enum FileStatus
{
    /// <summary>Not yet copied (or a previous attempt was interrupted).</summary>
    Pending,
    /// <summary>Copied to staging and proven byte-identical.</summary>
    Verified,
    /// <summary>Already archived by an earlier import (same content hash). Not copied again.</summary>
    Duplicate,
    /// <summary>Could not be copied and verified after retries. Blocks card erase.</summary>
    Failed,
    /// <summary>Deleted from the card after its archived copy was confirmed.</summary>
    Erased,
}

public sealed record Session(
    long Id,
    string CardFingerprint,
    string? CardLabel,
    string SourceRoot,
    SessionState State,
    DateTime CreatedUtc);

public sealed record SessionFile(
    long Id,
    long SessionId,
    string RelPath,
    long Size,
    DateTime MtimeUtc,
    string? Sha256,
    FileStatus Status,
    int Attempts,
    string? StagingName,
    string? LastError)
{
    public string FileName => RelPath[(RelPath.LastIndexOf('/') + 1)..];
    public string SourcePath(string cardRoot) => Path.Combine(cardRoot, RelPath.Replace('/', Path.DirectorySeparatorChar));
}

public sealed record ImportRecord(long Id, long SessionId, string FolderPath, DateTime CreatedUtc, int ImageCount, DateTime? UndoneUtc);

public sealed record ImportMove(long ImportId, long FileId, string FromPath, string ToPath, bool Done);

public sealed record KnownFile(string Sha256, long Size, long ImportId, string FinalPath);

public readonly record struct FileCounts(int Total, int Pending, int Verified, int Duplicate, int Failed, int Erased);
