namespace EasyImageImporter.Core.Import;

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
    /// <summary>Moving an import back into staging. Finished by recovery if interrupted.</summary>
    Undoing,
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
    string? LastError,
    MediaKind? Kind = null,
    DateTime? TakenAt = null,
    string? TakenAtSource = null,
    string? Camera = null,
    long? SequenceId = null,
    bool Keep = true)
{
    public string FileName => RelPath[(RelPath.LastIndexOf('/') + 1)..];
    public string SourcePath(string cardRoot) => Path.Combine(cardRoot, RelPath.Replace('/', Path.DirectorySeparatorChar));
}

/// <param name="ImageCount">Images kept (in the folder itself).</param>
/// <param name="DiscardedCount">Images in the folder's "Sortert bort" subfolder.</param>
public sealed record ImportRecord(long Id, long SessionId, string FolderPath, DateTime CreatedUtc, int ImageCount,
    DateTime? UndoneUtc, int DiscardedCount = 0);

public enum MediaKind { Image, Video }

public sealed record ImportMove(long ImportId, long FileId, string FromPath, string ToPath, bool Done, bool Undone);

public sealed record KnownFile(string Sha256, long Size, long ImportId, string FinalPath);

public readonly record struct FileCounts(int Total, int Pending, int Verified, int Duplicate, int Failed, int Erased);
