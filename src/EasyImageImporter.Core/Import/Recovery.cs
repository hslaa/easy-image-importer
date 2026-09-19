namespace EasyImageImporter.Core.Import;

/// <summary>
/// Runs at startup. Finishes whatever doesn't need the card (an interrupted save or undo) and
/// returns the sessions that are waiting for something (the card, or the user).
/// Copying and erasing resume by themselves when the card is inserted again.
/// </summary>
public sealed class Recovery(ImportStore store, Finalizer finalizer, ImportUndo undo)
{
    public IReadOnlyList<Session> Run()
    {
        foreach (var session in store.GetUnfinishedSessions())
        {
            if (session.State == SessionState.Finalizing) finalizer.Run(session.Id);
            else if (session.State == SessionState.Undoing) undo.Run(store.GetImportForSession(session.Id)!.Id);
        }

        return store.GetUnfinishedSessions();
    }
}
