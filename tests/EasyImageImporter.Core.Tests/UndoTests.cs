using EasyImageImporter.Core.Import;

namespace EasyImageImporter.Core.Tests;

public sealed class UndoTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void Undo_moves_everything_back_and_the_session_can_be_saved_again()
    {
        _env.AddCardImages(3);
        var (session, import) = _env.ImportCard();

        _env.Undo.Run(import!.Id);

        Assert.Equal(SessionState.Copied, _env.Store.GetSession(session.Id).State);
        Assert.False(Directory.Exists(import.FolderPath));
        Assert.Equal(3, Directory.GetFiles(_env.Paths.StagingDir(session.Id)).Length);
        Assert.Null(_env.Store.GetImportForSession(session.Id));
        Assert.Empty(_env.Store.GetImports());

        var again = _env.Finalizer.Run(session.Id);

        Assert.Equal(import.FolderPath, again!.FolderPath);
        Assert.Equal(4, Directory.GetFiles(again.FolderPath).Length); // 3 images + summary
        Assert.Equal(SessionState.Imported, _env.Store.GetSession(session.Id).State);
        Assert.Single(_env.Store.GetImports());
    }

    [Fact]
    public void Undo_blocks_erase_until_saved_again()
    {
        _env.AddCardImages(2);
        var (session, import) = _env.ImportCard();

        _env.Undo.Run(import!.Id);

        Assert.False(_env.Eraser.Evaluate(session.Id).Allowed);
        Assert.Equal(EraseOutcomeKind.Refused, _env.Eraser.Erase(session.Id).Kind);
        Assert.Equal(2, Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Undo_after_the_card_was_erased_keeps_the_only_copies_safe()
    {
        _env.AddCardImages(3);
        var originals = Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).Select(Convert.ToBase64String).ToHashSet();
        var (session, import) = _env.ImportCard();
        _env.Eraser.Erase(session.Id);

        _env.Undo.Run(import!.Id);
        Assert.Equal(3, _env.Store.GetStagedFiles(session.Id).Count); // what the "ready to save" screen counts
        var again = _env.Finalizer.Run(session.Id);

        var archived = Directory.GetFiles(again!.FolderPath, "*.JPG")
            .Select(File.ReadAllBytes).Select(Convert.ToBase64String).ToHashSet();
        Assert.Equal(originals, archived);
        // Nothing left on the card, so the session is complete rather than stuck at "erase",
        // and there is no erase step, not even a refusal to explain.
        Assert.Equal(SessionState.CardErased, _env.Store.GetSession(session.Id).State);
        Assert.Equal(new EraseDecision(false, 0, null), _env.Eraser.Evaluate(session.Id));
    }

    [Fact]
    public void Undo_is_only_offered_for_24_hours()
    {
        _env.AddCardImages(1);
        var (_, import) = _env.ImportCard();

        _env.Time.Now += TimeSpan.FromHours(23);
        Assert.True(_env.Undo.Evaluate(import!.Id).Allowed);

        _env.Time.Now += TimeSpan.FromHours(2);
        Assert.False(_env.Undo.Evaluate(import.Id).Allowed);
        Assert.Throws<InvalidOperationException>(() => _env.Undo.Run(import.Id));
    }

    [Fact]
    public void Undo_refuses_rather_than_half_undo_when_images_were_moved_away()
    {
        _env.AddCardImages(3);
        var (session, import) = _env.ImportCard();
        File.Delete(Path.Combine(import!.FolderPath, "IMAG0002.JPG"));

        var decision = _env.Undo.Evaluate(import.Id);

        Assert.False(decision.Allowed);
        Assert.Contains("1 bilde er flyttet eller slettet", decision.Reason);
        Assert.Throws<InvalidOperationException>(() => _env.Undo.Run(import.Id));
        Assert.Equal(3, Directory.GetFiles(import.FolderPath).Length); // 2 images + summary, untouched
        Assert.Equal(SessionState.Imported, _env.Store.GetSession(session.Id).State);
    }

    [Fact]
    public void Undo_leaves_files_the_user_added_to_the_folder()
    {
        _env.AddCardImages(2);
        var (_, import) = _env.ImportCard();
        var note = Path.Combine(import!.FolderPath, "mine notater.txt");
        File.WriteAllText(note, "fin morgen");

        _env.Undo.Run(import.Id);

        Assert.True(File.Exists(note));
        Assert.Single(Directory.GetFiles(import.FolderPath));
    }

    [Fact]
    public void Crash_halfway_through_undo_is_finished_by_recovery()
    {
        _env.AddCardImages(5);
        var (session, import) = _env.ImportCard();
        _env.Fs.CrashOnMove = 3;

        Assert.Throws<SimulatedCrashException>(() => _env.Undo.Run(import!.Id));
        Assert.Equal(SessionState.Undoing, _env.Store.GetSession(session.Id).State);
        // Mid-undo, nothing on the card may be erased on the strength of the half-moved archive.
        Assert.False(_env.Eraser.Evaluate(session.Id).Allowed);

        _env.Recovery.Run();

        Assert.Equal(SessionState.Copied, _env.Store.GetSession(session.Id).State);
        Assert.Equal(5, Directory.GetFiles(_env.Paths.StagingDir(session.Id)).Length);
        Assert.False(Directory.Exists(import!.FolderPath));
    }
}
