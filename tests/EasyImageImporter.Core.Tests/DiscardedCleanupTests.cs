using EasyImageImporter.Core.Import;

namespace EasyImageImporter.Core.Tests;

public sealed class DiscardedCleanupTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    private AppSettings Settings => new(_env.Store);
    private DiscardedCleanup Cleanup => new(_env.Fs, _env.Store, Settings, _env.Time);

    /// <summary>Four photos, two of them sorted away, saved at one place.</summary>
    private (string Folder, string Discarded) ImportWithDiscarded()
    {
        _env.AddCardImages(4);
        var session = _env.CopyAndPrepare();
        var visits = _env.Review.GetOverview(session.Id).Visits;
        foreach (var frame in visits[0].Frames.Take(2)) _env.Review.SetKeep(frame.Id, false);
        var import = _env.Finalizer.Run(session.Id)!;
        var folder = import.FolderPath;
        var discarded = Path.Combine(folder, Finalizer.DiscardedFolderName);
        Assert.Equal(2, Directory.GetFiles(discarded).Length);
        return (folder, discarded);
    }

    [Fact]
    public void Sorted_away_photos_go_to_the_bin_once_the_import_cannot_be_undone()
    {
        var (folder, discarded) = ImportWithDiscarded();
        var kept = Directory.GetFiles(folder).Length;

        // While the import can still be undone, they have to stay: an undo puts them back.
        Assert.Equal(0, Cleanup.Run().Files);
        Assert.True(Directory.Exists(discarded));

        _env.Time.Advance(ImportUndo.Window + TimeSpan.FromMinutes(1));
        var moved = Cleanup.Run();

        Assert.Equal(2, moved.Files);
        Assert.True(moved.Bytes > 0);
        Assert.False(Directory.Exists(discarded));
        Assert.Equal(kept, Directory.GetFiles(folder).Length);
        // In the bin as one folder per import, ready to be restored.
        Assert.Equal(2, _env.RecycledFiles().Count());
        Assert.All(_env.RecycledFiles(), p => Assert.Equal(Finalizer.DiscardedFolderName,
            Path.GetFileName(Path.GetDirectoryName(p))));
    }

    [Fact]
    public void Nothing_moves_when_the_user_wants_to_keep_them()
    {
        var (_, discarded) = ImportWithDiscarded();
        Settings.DiscardedToRecycleBin = false;
        _env.Time.Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, Cleanup.Run().Files);
        Assert.Equal(2, Directory.GetFiles(discarded).Length);
        Assert.Empty(_env.RecycledFiles());
    }

    [Fact]
    public void Files_the_user_put_there_are_left_alone()
    {
        var (_, discarded) = ImportWithDiscarded();
        var mine = Path.Combine(discarded, "min-egen-fil.txt");
        File.WriteAllText(mine, "denne er min");

        _env.Time.Advance(TimeSpan.FromDays(31));
        Cleanup.Run();
        Assert.Equal(2, _env.RecycledFiles().Count()); // ours went one by one

        Assert.True(File.Exists(mine));
        Assert.True(Directory.Exists(discarded)); // not empty, so it stays
        Assert.Single(Directory.GetFiles(discarded));
    }

    [Fact]
    public void Measure_counts_what_is_there_and_stops_counting_once_it_is_gone()
    {
        ImportWithDiscarded();
        var before = Cleanup.Measure();
        Assert.Equal(2, before.Files);

        _env.Time.Advance(TimeSpan.FromDays(31));
        Cleanup.Run();

        Assert.Equal(new DiscardedSpace(0, 0), Cleanup.Measure());
    }

    [Fact]
    public void A_folder_the_user_deleted_is_not_a_problem()
    {
        var (_, discarded) = ImportWithDiscarded();
        Directory.Delete(discarded, recursive: true);

        _env.Time.Advance(TimeSpan.FromDays(31));

        Assert.Equal(0, Cleanup.Run().Files);
        Assert.Empty(_env.Store.GetImportsWithDiscarded()); // marked as dealt with, so it isn't looked at again
    }
}
