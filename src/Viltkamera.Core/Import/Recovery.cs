namespace Viltkamera.Core.Import;

/// <summary>
/// Runs at startup. Finishes whatever doesn't need the card (an interrupted finalize) and
/// returns the sessions that are waiting for something (the card, or the user).
/// Copying and erasing resume by themselves when the card is inserted again.
/// </summary>
public sealed class Recovery(ImportStore store, Finalizer finalizer)
{
    public IReadOnlyList<Session> Run()
    {
        foreach (var session in store.GetUnfinishedSessions().Where(s => s.State == SessionState.Finalizing))
            finalizer.Run(session.Id);

        return store.GetUnfinishedSessions();
    }
}
