using EasyImageImporter.Core.IO;

namespace EasyImageImporter.Core.Import;

public sealed record UndoDecision(bool Allowed, string? Reason)
{
    public static readonly UndoDecision Yes = new(true, null);
    public static UndoDecision No(string reason) => new(false, reason);
}

/// <summary>
/// "Angre hele importen": moves an import's images back into staging and returns the session to
/// "copied, ready to save". Never deletes an image. Works after the card has been erased too, so
/// it uses the same crash-safe move journal as saving, and recovery finishes it if interrupted.
/// </summary>
public sealed class ImportUndo(IFileSystem fs, ImportStore store, AppPaths paths, TimeProvider? time = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SafeMover _mover = new(fs);

    public UndoDecision Evaluate(long importId)
    {
        var import = store.GetImport(importId);
        if (import.UndoneUtc is not null) return UndoDecision.No("Importen er allerede angret.");

        var session = store.GetSession(import.SessionId);
        if (session.State is not (SessionState.Imported or SessionState.CardErased))
            return UndoDecision.No("Importen kan ikke angres akkurat nå.");
        if (store.GetImportForSession(session.Id)?.Id != importId)
            return UndoDecision.No("Bare den siste importen av dette kortet kan angres.");
        if (_time.GetUtcNow().UtcDateTime - import.CreatedUtc > Window)
            return UndoDecision.No("En import kan bare angres det første døgnet.");

        // All or nothing: never leave half an import in each place.
        var missing = store.GetMoves(importId).Count(m => m.Done && !fs.FileExists(m.ToPath));
        if (missing > 0)
            return UndoDecision.No(
                $"{missing} {(missing == 1 ? "bilde er" : "bilder er")} flyttet eller slettet fra mappen, " +
                "så importen kan ikke angres automatisk.");

        return UndoDecision.Yes;
    }

    public void Run(long importId)
    {
        var import = store.GetImport(importId);
        var session = store.GetSession(import.SessionId);
        if (session.State != SessionState.Undoing)
        {
            var decision = Evaluate(importId);
            if (!decision.Allowed) throw new InvalidOperationException(decision.Reason);
            store.BeginUndo(importId, session.Id);
        }

        fs.CreateDirectory(paths.StagingDir(session.Id));
        foreach (var move in store.GetMoves(importId).Where(m => m.Done && !m.Undone))
        {
            _mover.Move(move.ToPath, move.FromPath);
            store.MarkMoveUndone(importId, move.FileId);
        }

        // Our own summary goes; the folder only if nothing else was put in it.
        var summary = Path.Combine(import.FolderPath, Finalizer.SummaryFileName);
        if (fs.FileExists(summary)) fs.Delete(summary);
        fs.DeleteDirectoryIfEmpty(Path.Combine(import.FolderPath, Finalizer.DiscardedFolderName));
        if (fs.DeleteDirectoryIfEmpty(import.FolderPath))
            fs.DeleteDirectoryIfEmpty(Path.GetDirectoryName(import.FolderPath)!); // the year folder

        store.CompleteUndo(importId, session.Id);
    }
}
