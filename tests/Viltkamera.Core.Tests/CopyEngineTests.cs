using Viltkamera.Core.Import;

namespace Viltkamera.Core.Tests;

public sealed class CopyEngineTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void Copies_and_verifies_every_file()
    {
        _env.AddCardImages(5);
        var session = _env.OpenSession();

        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(CopyOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(5, outcome.Counts.Verified);
        Assert.Equal(SessionState.Copied, _env.Store.GetSession(session.Id).State);
        foreach (var file in _env.Store.GetFiles(session.Id))
        {
            var staged = Path.Combine(_env.Paths.StagingDir(session.Id), file.StagingName!);
            Assert.Equal(File.ReadAllBytes(file.SourcePath(_env.Card)), File.ReadAllBytes(staged));
        }
    }

    [Fact]
    public void Never_writes_to_the_card()
    {
        _env.AddCardImages(3);
        var before = Snapshot(_env.Card);

        var session = _env.OpenSession();
        _env.Copier.Run(session.Id);
        _env.Finalizer.Run(session.Id);

        Assert.Equal(before, Snapshot(_env.Card));
    }

    [Fact]
    public void Corrupted_copy_is_retried_and_then_verified()
    {
        _env.AddCardImages(2);
        var session = _env.OpenSession();
        _env.Fs.CorruptNextWrites = 2;

        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(2, outcome.Counts.Verified);
        Assert.Equal(3, _env.Store.GetFiles(session.Id)[0].Attempts);
    }

    [Fact]
    public void File_that_never_verifies_is_marked_failed_and_nothing_bad_is_staged()
    {
        _env.AddCardImages(2);
        var session = _env.OpenSession();
        _env.Fs.CorruptNextWrites = CopyEngine.MaxAttempts;

        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(CopyOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(1, outcome.Counts.Failed);
        Assert.Equal(1, outcome.Counts.Verified);
        Assert.Single(Directory.GetFiles(_env.Paths.StagingDir(session.Id)));
    }

    [Fact]
    public void Pulling_the_card_pauses_and_reinserting_resumes()
    {
        _env.AddCardImages(6);
        var session = _env.OpenSession();
        _env.Fs.OnSourceRead = (path, bytes) =>
        {
            if (path.EndsWith("IMAG0004.JPG") && bytes > 10_000) _env.PullCard();
        };

        var first = _env.Copier.Run(session.Id);

        Assert.Equal(CopyOutcomeKind.CardMissing, first.Kind);
        Assert.Equal(SessionState.PausedCardMissing, _env.Store.GetSession(session.Id).State);
        Assert.Equal(3, first.Counts.Verified);
        Assert.Equal(0, first.Counts.Failed);
        Assert.Empty(Directory.GetFiles(_env.Paths.StagingDir(session.Id), "*.tmp"));

        _env.Fs.OnSourceRead = null;
        _env.ReinsertCard();
        var resumed = _env.OpenSession();
        Assert.Equal(session.Id, resumed.Id);

        var second = _env.Copier.Run(session.Id);

        Assert.Equal(CopyOutcomeKind.Completed, second.Kind);
        Assert.Equal(6, second.Counts.Verified);
    }

    [Fact]
    public void Card_in_a_different_drive_resumes_the_same_session()
    {
        _env.AddCardImages(3);
        var session = _env.OpenSession();

        var otherLetter = Path.Combine(_env.Root, "card-on-F");
        Directory.Move(_env.Card, otherLetter);
        var resumed = _env.Scanner.OpenSession(_env.Scanner.Scan(otherLetter), "SDCARD")!;

        Assert.Equal(session.Id, resumed.Id);
        Assert.Equal(otherLetter, resumed.SourceRoot);
        Assert.Equal(3, _env.Copier.Run(session.Id).Counts.Verified);
    }

    [Fact]
    public void Leftovers_from_a_crash_are_cleaned_up_and_copied_again()
    {
        _env.AddCardImages(3);
        var session = _env.OpenSession();
        var staging = _env.Paths.StagingDir(session.Id);
        var files = _env.Store.GetFiles(session.Id);
        Directory.CreateDirectory(staging);
        // Crashed mid-write, and crashed between rename and database commit.
        File.WriteAllText(Path.Combine(staging, $"{files[0].Id}.tmp"), "half a file");
        File.WriteAllText(Path.Combine(staging, $"{files[1].Id}.jpg"), "not verified");

        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(3, outcome.Counts.Verified);
        Assert.Empty(Directory.GetFiles(staging, "*.tmp"));
        Assert.Equal(File.ReadAllBytes(files[1].SourcePath(_env.Card)),
            File.ReadAllBytes(Path.Combine(staging, $"{files[1].Id}.jpg")));
    }

    [Fact]
    public void Refuses_to_start_when_the_disk_is_too_full()
    {
        _env.AddCardImages(3);
        var session = _env.OpenSession();
        _env.Fs.FreeSpaceOverride = 1_000;

        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(CopyOutcomeKind.DiskFull, outcome.Kind);
        Assert.Equal(SessionState.PausedDiskFull, _env.Store.GetSession(session.Id).State);
        Assert.Equal(3, outcome.Counts.Pending);
    }

    [Fact]
    public void Disk_filling_up_mid_copy_pauses_without_failing_files()
    {
        _env.AddCardImages(3);
        var session = _env.OpenSession();
        _env.Fs.DiskFullOnNextWrites = 1;

        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(CopyOutcomeKind.DiskFull, outcome.Kind);
        Assert.Equal(0, outcome.Counts.Failed);
        Assert.Equal(3, _env.Copier.Run(session.Id).Counts.Verified);
    }

    [Fact]
    public void Images_already_imported_are_recognised_by_content_not_name()
    {
        _env.AddCardImages(3);
        _env.ImportCard();
        // Not erased: back in the camera, two more images, then imported again.
        _env.AddCardFile("DCIM/100MEDIA/IMAG0004.JPG");
        _env.AddCardFile("DCIM/100MEDIA/IMAG0005.JPG");

        var session = _env.OpenSession();
        var outcome = _env.Copier.Run(session.Id);

        Assert.Equal(3, outcome.Counts.Duplicate);
        Assert.Equal(2, outcome.Counts.Verified);
    }

    private static Dictionary<string, (long, DateTime)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => (new FileInfo(p).Length, File.GetLastWriteTimeUtc(p)));
}
