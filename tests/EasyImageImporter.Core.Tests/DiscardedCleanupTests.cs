using EasyImageImporter.Core.Import;
using Microsoft.Data.Sqlite;

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
        var discarded = Assert.Single(Directory.GetDirectories(_env.Paths.DiscardedRoot));
        Assert.Equal(2, Directory.GetFiles(discarded).Length);
        return (import.FolderPath, discarded);
    }

    [Fact]
    public void Sorted_away_photos_are_kept_30_days_and_then_go_to_the_bin()
    {
        var (folder, discarded) = ImportWithDiscarded();
        var kept = Directory.GetFiles(folder).Length;

        _env.Time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, Cleanup.Run().Files);
        Assert.True(Directory.Exists(discarded));

        _env.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        var moved = Cleanup.Run();

        Assert.Equal(2, moved.Files);
        Assert.True(moved.Bytes > 0);
        Assert.False(Directory.Exists(discarded));
        Assert.Equal(kept, Directory.GetFiles(folder).Length);
        // In the bin as one folder per place, named like the place, ready to be restored.
        Assert.Equal(2, _env.RecycledFiles().Count());
        Assert.All(_env.RecycledFiles(), p => Assert.Equal(Path.GetFileName(discarded),
            Path.GetFileName(Path.GetDirectoryName(p))));
    }

    [Fact]
    public void How_long_they_are_kept_can_be_changed()
    {
        var (_, discarded) = ImportWithDiscarded();
        Settings.DiscardedKeepDays = 7;

        _env.Time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, Cleanup.Run().Files);
        _env.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.Equal(2, Cleanup.Run().Files);
        Assert.False(Directory.Exists(discarded));
    }

    [Fact]
    public void Never_before_the_import_can_no_longer_be_undone()
    {
        var (_, discarded) = ImportWithDiscarded();
        _env.Store.SetSetting("discarded_keep_days", "0"); // nonsense stored somehow: kept for good, not cleared at once
        Assert.Null(Settings.DiscardedKeepDays);
        Settings.DiscardedKeepDays = 1;

        _env.Time.Advance(ImportUndo.Window - TimeSpan.FromMinutes(1));
        Assert.Equal(0, Cleanup.Run().Files);
        Assert.True(Directory.Exists(discarded));
    }

    [Fact]
    public void Nothing_moves_when_the_user_wants_to_keep_them()
    {
        var (_, discarded) = ImportWithDiscarded();
        Settings.DiscardedKeepDays = null;
        _env.Time.Advance(TimeSpan.FromDays(400));

        Assert.Equal(0, Cleanup.Run().Files);
        Assert.Equal(2, Directory.GetFiles(discarded).Length);
        Assert.Empty(_env.RecycledFiles());
    }

    [Fact]
    public void The_old_setting_to_keep_them_for_good_still_counts()
    {
        _env.Store.SetSetting("discarded_to_recycle_bin", "0");
        Assert.Null(Settings.DiscardedKeepDays);

        _env.Store.SetSetting("discarded_to_recycle_bin", "1");
        Assert.Equal(AppSettings.DefaultKeepDays, Settings.DiscardedKeepDays);
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

    [Fact]
    public void Photos_sorted_away_before_they_were_kept_out_of_sight_are_cleared_too()
    {
        // Imports saved by earlier versions have them in "Sortert bort" inside the import's folder.
        var (folder, discarded) = ImportWithDiscarded();
        var legacy = Path.Combine(folder, Finalizer.DiscardedFolderName);
        Directory.Move(discarded, legacy);
        var import = _env.Store.GetImports().Single();
        using (var c = new SqliteConnection($"Data Source={_env.Paths.DatabasePath};Pooling=False"))
        {
            c.Open();
            foreach (var move in _env.Store.GetMoves(import.Id).Where(m => m.ToPath.StartsWith(discarded)))
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE import_moves SET to_path = $t WHERE file_id = $f;";
                cmd.Parameters.AddWithValue("$t", Path.Combine(legacy, Path.GetFileName(move.ToPath)));
                cmd.Parameters.AddWithValue("$f", move.FileId);
                cmd.ExecuteNonQuery();
            }
        }

        Assert.Equal(2, Cleanup.Measure().Files);
        _env.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(2, Cleanup.Run().Files);
        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void A_place_that_happens_to_be_called_sortert_bort_is_not_cleared()
    {
        _env.AddCardImages(4);
        var session = _env.CopyAndPrepare();
        var place = _env.Review.GetOverview(session.Id).Places.Single();
        _env.Store.SetPlaceFolderName(place.Id, Finalizer.DiscardedFolderName);
        _env.Review.SetKeep(place.Visits[0].Frames[0].Id, false);
        var import = _env.Finalizer.Run(session.Id)!;
        Assert.Equal(Finalizer.DiscardedFolderName, Path.GetFileName(import.FolderPath));

        _env.Time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, Cleanup.Run().Files); // only the one sorted away
        Assert.Equal(3, _env.ArchivedImages().Count());
    }
}
