using EasyImageImporter.Core.IO;

namespace EasyImageImporter.Core.Import;

public sealed record EraseDecision(bool Allowed, int Count, string? Reason)
{
    public static EraseDecision Refuse(string reason) => new(false, 0, reason);
}

public enum EraseOutcomeKind { Completed, Refused, CardMissing, Cancelled }

public sealed record EraseOutcome(EraseOutcomeKind Kind, int Erased, string? Reason = null);

/// <summary>
/// The only code that deletes images from the card. The erase step is offered only when
/// <see cref="Evaluate"/> allows it, and every single file is checked again right before it
/// is deleted.
/// </summary>
public sealed class CardEraser(IFileSystem fs, ImportStore store)
{
    private readonly VerifiedCopier _hasher = new(fs);

    public EraseDecision Evaluate(long sessionId)
    {
        var session = store.GetSession(sessionId);
        if (session.State != SessionState.Imported)
            return EraseDecision.Refuse("Bildene er ikke lagret ennå.");

        var counts = store.GetCounts(sessionId);
        if (counts.Failed > 0)
            return EraseDecision.Refuse(
                $"{counts.Failed} {(counts.Failed == 1 ? "bilde" : "bilder")} kunne ikke kopieres trygt. " +
                "Kortet blir ikke slettet før alle bildene er kopiert og kontrollert.");
        if (counts.Pending > 0)
            return EraseDecision.Refuse("Noen bilder er ikke kopiert ennå.");

        var remaining = counts.Verified + counts.Duplicate;
        return remaining == 0 ? EraseDecision.Refuse("Det er ingen bilder å slette.") : new EraseDecision(true, remaining, null);
    }

    public EraseOutcome Erase(long sessionId, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var counts = store.GetCounts(sessionId);
        if (store.GetSession(sessionId).State == SessionState.Imported
            && counts.Erased > 0 && counts.Erased == counts.Total)
        {
            // The last file was deleted just before the app stopped.
            store.SetState(sessionId, SessionState.CardErased);
            return new EraseOutcome(EraseOutcomeKind.Completed, 0);
        }

        var decision = Evaluate(sessionId);
        if (!decision.Allowed) return new EraseOutcome(EraseOutcomeKind.Refused, 0, decision.Reason);

        var session = store.GetSession(sessionId);
        var erased = 0;
        foreach (var file in store.GetFiles(sessionId, FileStatus.Verified, FileStatus.Duplicate))
        {
            if (ct.IsCancellationRequested) return new EraseOutcome(EraseOutcomeKind.Cancelled, erased);
            if (!fs.DirectoryExists(session.SourceRoot)) return new EraseOutcome(EraseOutcomeKind.CardMissing, erased);

            var source = file.SourcePath(session.SourceRoot);
            if (!fs.FileExists(source))
            {
                // Deleted just before the app stopped last time.
                store.MarkErased(file.Id);
                continue;
            }

            var refusal = CheckSafeToDelete(file, source, ct);
            if (refusal is not null) return new EraseOutcome(EraseOutcomeKind.Refused, erased, refusal);

            try
            {
                fs.Delete(source);
            }
            catch (IOException) when (!fs.DirectoryExists(session.SourceRoot))
            {
                return new EraseOutcome(EraseOutcomeKind.CardMissing, erased);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new EraseOutcome(EraseOutcomeKind.Refused, erased,
                    "Kortet kan ikke slettes. Sjekk at den lille låsebryteren på siden av kortet ikke står på «Lock».");
            }

            store.MarkErased(file.Id);
            erased++;
            progress?.Report(erased);
        }

        store.SetState(sessionId, SessionState.CardErased);
        return new EraseOutcome(EraseOutcomeKind.Completed, erased);
    }

    /// <summary>Null if deleting is safe; otherwise a plain explanation of why not.</summary>
    private string? CheckSafeToDelete(SessionFile file, string source, CancellationToken ct)
    {
        if (file.Sha256 is null) return $"{file.FileName} mangler kontrollsum.";

        string cardHash;
        try
        {
            cardHash = _hasher.HashForVerify(source, ct);
        }
        catch (IOException)
        {
            return $"{file.FileName} kunne ikke leses fra kortet.";
        }

        if (cardHash != file.Sha256)
            return $"{file.FileName} på kortet er ikke likt bildet som ble kopiert. Ingenting mer blir slettet.";

        var archived = store.GetKnownFile(file.Sha256);
        if (archived is null || !fs.FileExists(archived.FinalPath))
            return $"Finner ikke den lagrede kopien av {file.FileName}. Ingenting mer blir slettet.";

        return null;
    }
}
