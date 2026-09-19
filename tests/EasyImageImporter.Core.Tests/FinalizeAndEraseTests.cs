using System.Text;
using EasyImageImporter.Core.Import;

namespace EasyImageImporter.Core.Tests;

public sealed class FinalizeAndEraseTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void Import_lands_in_a_dated_folder_with_a_readable_summary()
    {
        _env.AddCardImages(3);

        var (session, import) = _env.ImportCard();

        Assert.Equal(SessionState.Imported, session.State);
        var expected = Path.Combine(_env.Paths.ArchiveRoot, "2026", "2026-09-19 Import");
        Assert.Equal(expected, import!.FolderPath);
        Assert.True(File.Exists(Path.Combine(expected, "IMAG0001.JPG")));
        var summary = File.ReadAllBytes(Path.Combine(expected, Finalizer.SummaryFileName));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, summary[..3]);
        Assert.Contains("3 bilder", Encoding.UTF8.GetString(summary));
        Assert.Empty(Directory.GetFiles(_env.Paths.StagingDir(session.Id)));
    }

    [Fact]
    public void Same_file_name_from_two_camera_folders_does_not_collide()
    {
        var a = _env.AddCardFile("DCIM/100MEDIA/IMAG0001.JPG");
        var b = _env.AddCardFile("DCIM/101MEDIA/IMAG0001.JPG");

        var (_, import) = _env.ImportCard();

        Assert.Equal(a, File.ReadAllBytes(Path.Combine(import!.FolderPath, "IMAG0001.JPG")));
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(import.FolderPath, "IMAG0001 (2).JPG")));
    }

    [Fact]
    public void Second_import_on_the_same_day_gets_its_own_folder()
    {
        _env.AddCardImages(1);
        var (first, firstImport) = _env.ImportCard();
        _env.Eraser.Erase(first.Id);
        _env.AddCardImages(1, "DCIM/101MEDIA");

        var (_, secondImport) = _env.ImportCard();

        Assert.EndsWith("2026-09-19 Import (2)", secondImport!.FolderPath);
        Assert.NotEqual(firstImport!.FolderPath, secondImport.FolderPath);
    }

    [Fact]
    public void Crash_halfway_through_finalize_is_finished_by_recovery()
    {
        _env.AddCardImages(5);
        var session = _env.OpenSession();
        _env.Copier.Run(session.Id);
        _env.Fs.CrashOnMove = 3;

        Assert.Throws<SimulatedCrashException>(() => _env.Finalizer.Run(session.Id));
        Assert.Equal(SessionState.Finalizing, _env.Store.GetSession(session.Id).State);

        _env.Recovery.Run();

        Assert.Equal(SessionState.Imported, _env.Store.GetSession(session.Id).State);
        Assert.Equal(6, _env.ArchivedFiles().Count()); // 5 images + summary
    }

    [Fact]
    public void Erase_is_not_offered_before_import()
    {
        _env.AddCardImages(2);
        var session = _env.OpenSession();
        _env.Copier.Run(session.Id);

        var decision = _env.Eraser.Evaluate(session.Id);

        Assert.False(decision.Allowed);
        Assert.Equal(EraseOutcomeKind.Refused, _env.Eraser.Erase(session.Id).Kind);
        Assert.Equal(2, Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Erase_is_refused_if_even_one_file_failed()
    {
        _env.AddCardImages(3);
        var session = _env.OpenSession();
        _env.Fs.CorruptNextWrites = CopyEngine.MaxAttempts;
        _env.Copier.Run(session.Id);
        _env.Finalizer.Run(session.Id);

        var decision = _env.Eraser.Evaluate(session.Id);

        Assert.False(decision.Allowed);
        Assert.Contains("1 bilde kunne ikke", decision.Reason);
        Assert.Equal(3, Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Erase_deletes_exactly_the_imported_images_and_leaves_camera_files()
    {
        _env.AddCardImages(4);
        _env.AddCardFile("SETUP.TXT", 100);
        _env.AddCardFile("DCIM/100MEDIA/.hidden.jpg", 100);
        var hidden = Path.Combine(_env.Card, "DCIM", "100MEDIA", ".hidden.jpg");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var (session, _) = _env.ImportCard();

        var decision = _env.Eraser.Evaluate(session.Id);
        var outcome = _env.Eraser.Erase(session.Id);

        Assert.True(decision.Allowed);
        Assert.Equal(4, decision.Count);
        Assert.Equal(EraseOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(4, outcome.Erased);
        Assert.Equal(SessionState.CardErased, _env.Store.GetSession(session.Id).State);
        var left = Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order();
        Assert.Equal([".hidden.jpg", "SETUP.TXT"], left);
        Assert.True(Directory.Exists(Path.Combine(_env.Card, "DCIM", "100MEDIA")));
    }

    [Fact]
    public void Erase_stops_if_a_card_file_no_longer_matches_what_was_copied()
    {
        _env.AddCardImages(3);
        var (session, _) = _env.ImportCard();
        var changed = Path.Combine(_env.Card, "DCIM", "100MEDIA", "IMAG0002.JPG");
        File.WriteAllBytes(changed, [1, 2, 3]);

        var outcome = _env.Eraser.Erase(session.Id);

        Assert.Equal(EraseOutcomeKind.Refused, outcome.Kind);
        Assert.Equal(1, outcome.Erased);
        Assert.True(File.Exists(changed));
        Assert.True(File.Exists(Path.Combine(_env.Card, "DCIM", "100MEDIA", "IMAG0003.JPG")));
    }

    [Fact]
    public void Erase_stops_if_the_archived_copy_has_gone_missing()
    {
        _env.AddCardImages(2);
        var (session, import) = _env.ImportCard();
        File.Delete(Path.Combine(import!.FolderPath, "IMAG0001.JPG"));

        var outcome = _env.Eraser.Erase(session.Id);

        Assert.Equal(EraseOutcomeKind.Refused, outcome.Kind);
        Assert.Equal(0, outcome.Erased);
        Assert.Equal(2, Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Erase_interrupted_by_pulling_the_card_resumes_on_reinsert()
    {
        _env.AddCardImages(4);
        var (session, _) = _env.ImportCard();
        var deleted = 0;
        var progress = new SyncProgress<int>(_ => { if (++deleted == 2) _env.PullCard(); });

        var first = _env.Eraser.Erase(session.Id, progress);

        Assert.Equal(EraseOutcomeKind.CardMissing, first.Kind);
        _env.ReinsertCard();
        var resumed = _env.OpenSession();
        Assert.Equal(session.Id, resumed.Id);
        Assert.Equal(EraseOutcomeKind.Completed, _env.Eraser.Erase(session.Id).Kind);
        Assert.Empty(Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Reinserting_an_imported_card_that_was_not_erased_goes_straight_to_erase()
    {
        _env.AddCardImages(2);
        var (first, _) = _env.ImportCard();

        var resumed = _env.OpenSession();
        _env.Copier.Run(resumed.Id);

        Assert.Equal(first.Id, resumed.Id);
        Assert.Equal(SessionState.Imported, _env.Store.GetSession(resumed.Id).State);
        Assert.Equal(2, _env.Eraser.Evaluate(resumed.Id).Count);
    }

    [Fact]
    public void Card_with_only_already_imported_images_can_still_be_erased()
    {
        _env.AddCardImages(2);
        _env.ImportCard();
        // Same images, but in another folder (copied around by hand): a different card listing.
        Directory.Move(Path.Combine(_env.Card, "DCIM", "100MEDIA"), Path.Combine(_env.Card, "DCIM", "COPY"));

        var (session, import) = _env.ImportCard();

        Assert.Null(import);
        Assert.Equal(SessionState.Imported, session.State);
        Assert.Equal(2, _env.Store.GetCounts(session.Id).Duplicate);
        Assert.Equal(2, _env.Eraser.Evaluate(session.Id).Count);
        Assert.Equal(2, _env.Eraser.Erase(session.Id).Erased);
    }

    [Fact]
    public void No_image_is_ever_lost_across_copy_import_and_erase()
    {
        _env.AddCardImages(8);
        var originals = Directory.GetFiles(_env.Card, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).Select(Convert.ToBase64String).ToHashSet();

        var session = _env.OpenSession();
        _env.Fs.CorruptNextWrites = 2;
        _env.Fs.OnSourceRead = (path, _) => { if (path.EndsWith("IMAG0005.JPG")) _env.PullCard(); };
        _env.Copier.Run(session.Id);
        _env.Fs.OnSourceRead = null;
        _env.ReinsertCard();
        _env.Copier.Run(session.Id);
        _env.Fs.CrashOnMove = 4;
        Assert.Throws<SimulatedCrashException>(() => _env.Finalizer.Run(session.Id));
        _env.Recovery.Run();
        _env.Eraser.Erase(session.Id);

        var archived = _env.ArchivedFiles().Where(p => p.EndsWith(".JPG"))
            .Select(File.ReadAllBytes).Select(Convert.ToBase64String).ToHashSet();
        Assert.Equal(originals, archived);
        Assert.Equal(SessionState.CardErased, _env.Store.GetSession(session.Id).State);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
