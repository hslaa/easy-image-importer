using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Tests;

public sealed class SequenceBuilderTests
{
    private static readonly DateTime Morning = new(2026, 9, 18, 7, 12, 0);

    private static SequenceInput At(long id, TimeSpan offset, string? camera = null) =>
        new(id, Morning + offset, camera, $"DCIM/100MEDIA/IMAG{id:0000}.JPG");

    [Fact]
    public void A_burst_is_one_visit_and_a_quiet_spell_starts_the_next()
    {
        var images = Enumerable.Range(1, 200).Select(i => At(i, TimeSpan.FromSeconds(i * 9))) // 07:12–07:42
            .Append(At(201, TimeSpan.FromHours(3)))
            .Append(At(202, TimeSpan.FromHours(3) + TimeSpan.FromSeconds(20)));

        var visits = SequenceBuilder.Build(images);

        Assert.Equal([200, 2], visits.Select(v => v.Count));
    }

    [Fact]
    public void Gap_exactly_at_the_limit_stays_in_the_same_visit()
    {
        var visits = SequenceBuilder.Build([At(1, TimeSpan.Zero), At(2, SequenceBuilder.DefaultGap)]);
        Assert.Single(visits);
    }

    [Fact]
    public void Two_cameras_on_one_card_never_share_a_visit()
    {
        var visits = SequenceBuilder.Build(
        [
            At(1, TimeSpan.Zero, "Browning A"), At(2, TimeSpan.FromSeconds(30), "Browning B"),
            At(3, TimeSpan.FromSeconds(60), "Browning A"),
        ]);

        Assert.Equal([[1L, 3L], [2L]], visits.Select(v => v.ToArray()));
    }

    [Fact]
    public void Reset_clock_groups_by_gaps_just_the_same()
    {
        var reset = new DateTime(2000, 1, 1);
        var visits = SequenceBuilder.Build(
        [
            new SequenceInput(1, reset, null, "a"), new SequenceInput(2, reset.AddSeconds(10), null, "b"),
            new SequenceInput(3, reset.AddHours(5), null, "c"),
        ]);

        Assert.Equal(2, visits.Count);
    }

    [Fact]
    public void Same_second_bursts_keep_the_camera_numbering_order()
    {
        var visits = SequenceBuilder.Build([At(3, TimeSpan.Zero), At(1, TimeSpan.Zero), At(2, TimeSpan.Zero)]);
        Assert.Equal([1L, 2L, 3L], visits.Single());
    }
}

public sealed class ReviewTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    private static readonly DateTime Morning = new(2026, 9, 18, 7, 12, 0);

    /// <summary>A 5-frame nøtteskrike burst at 07:12, then a 2-frame fox visit at 22:40.</summary>
    private void AddTwoVisits()
    {
        for (var i = 0; i < 5; i++)
            _env.AddCardFile($"DCIM/100MEDIA/IMAG{i + 1:0000}.JPG", TestImages.JpegWithExif(Morning.AddSeconds(i * 5)));
        for (var i = 0; i < 2; i++)
            _env.AddCardFile($"DCIM/100MEDIA/IMAG{i + 6:0000}.JPG", TestImages.JpegWithExif(Morning.AddHours(15).AddMinutes(28).AddSeconds(i * 5)));
    }

    [Fact]
    public void Capture_time_and_camera_come_from_exif()
    {
        _env.AddCardFile("DCIM/100MEDIA/IMAG0001.JPG", TestImages.JpegWithExif(Morning));
        var session = _env.CopyAndPrepare();

        var file = _env.Store.GetStagedFiles(session.Id).Single();

        Assert.Equal(Morning, file.TakenAt);
        Assert.Equal("exif", file.TakenAtSource);
        Assert.Equal("Browning BTC-8E", file.Camera);
        Assert.Equal(MediaKind.Image, file.Kind);
    }

    [Fact]
    public void Without_exif_the_card_file_time_is_used()
    {
        var mtime = new DateTime(2026, 9, 18, 5, 0, 0, DateTimeKind.Utc);
        _env.AddCardFile("DCIM/100MEDIA/IMAG0001.JPG", mtimeUtc: mtime); // random bytes: no EXIF
        var session = _env.CopyAndPrepare();

        var file = _env.Store.GetStagedFiles(session.Id).Single();

        Assert.Equal("mtime", file.TakenAtSource);
        Assert.Equal(mtime.ToLocalTime(), file.TakenAt);
    }

    [Fact]
    public void Images_are_grouped_into_visits_and_videos_are_kept_aside()
    {
        AddTwoVisits();
        _env.AddCardFile("DCIM/100MEDIA/IMAG0008.AVI");
        var session = _env.CopyAndPrepare();

        var overview = _env.Review.GetOverview(session.Id);

        Assert.Equal([5, 2], overview.Visits.Select(v => v.Frames.Count));
        Assert.Equal(Morning, overview.Visits[0].Start);
        Assert.Equal(Morning.AddSeconds(20), overview.Visits[0].End);
        Assert.Single(overview.Videos);
        Assert.Equal(7, overview.KeptCount); // default is keep everything
    }

    [Fact]
    public void Discarded_images_are_saved_to_sortert_bort_not_deleted()
    {
        AddTwoVisits();
        var session = _env.CopyAndPrepare();
        var review = _env.Review;
        var burst = review.GetOverview(session.Id).Visits[0];
        review.SetKeep(burst, keep: false);
        review.SetKeep(burst.Frames[2].Id, keep: true);

        var import = _env.Finalizer.Run(session.Id)!;

        Assert.Equal(3, import.ImageCount); // 1 from the burst + the 2 fox frames
        Assert.Equal(4, import.DiscardedCount);
        Assert.Equal(3, Directory.GetFiles(import.FolderPath, "*.JPG").Length);
        var discarded = Path.Combine(import.FolderPath, Finalizer.DiscardedFolderName);
        Assert.Equal(4, Directory.GetFiles(discarded).Length);
        Assert.Contains("Sortert bort: 4 bilder", File.ReadAllText(Path.Combine(import.FolderPath, Finalizer.SummaryFileName)));

        // Discarded images are safely stored too, so the card can be erased.
        Assert.Equal(7, _env.Eraser.Evaluate(session.Id).Count);
    }

    [Fact]
    public void Undo_brings_back_the_discarded_images_and_the_review_choices()
    {
        AddTwoVisits();
        var session = _env.CopyAndPrepare();
        var burst = _env.Review.GetOverview(session.Id).Visits[0];
        _env.Review.SetKeep(burst, keep: false);
        var import = _env.Finalizer.Run(session.Id)!;

        _env.Undo.Run(import.Id);
        _env.Review.Prepare(session.Id);

        Assert.False(Directory.Exists(import.FolderPath));
        var overview = _env.Review.GetOverview(session.Id);
        Assert.Equal(2, overview.KeptCount);
        Assert.Equal(7, overview.ImageCount);
    }

    [Fact]
    public void Split_and_merge_are_kept_when_review_is_resumed()
    {
        AddTwoVisits();
        var session = _env.CopyAndPrepare();
        var review = _env.Review;
        var burst = review.GetOverview(session.Id).Visits[0];

        review.Split(burst, burst.Frames[3].Id);
        Assert.Equal([3, 2, 2], review.GetOverview(session.Id).Visits.Select(v => v.Frames.Count));

        var visits = review.GetOverview(session.Id).Visits;
        review.Merge(visits[1], visits[2]);
        review.Prepare(session.Id); // e.g. the app was restarted three days later

        Assert.Equal([3, 4], review.GetOverview(session.Id).Visits.Select(v => v.Frames.Count));
    }

    [Fact]
    public void Splitting_before_the_first_frame_changes_nothing()
    {
        AddTwoVisits();
        var session = _env.CopyAndPrepare();
        var burst = _env.Review.GetOverview(session.Id).Visits[0];

        _env.Review.Split(burst, burst.Frames[0].Id);

        Assert.Equal(2, _env.Review.GetOverview(session.Id).Visits.Count);
    }
}

public sealed class ThumbnailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "easyimageimporter-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Makes_a_small_jpeg_once_and_reuses_it()
    {
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "big.jpg");
        File.WriteAllBytes(source, TestImages.Jpeg(4000, 3000));
        var cache = new ThumbnailCache(Path.Combine(_dir, "thumbs"), width: 320);

        var thumb = cache.GetOrCreate(source, "ab12cd");
        var written = File.GetLastWriteTimeUtc(thumb!);
        var again = cache.GetOrCreate(source, "ab12cd");

        using var bitmap = SkiaSharp.SKBitmap.Decode(thumb);
        Assert.Equal(320, bitmap.Width);
        Assert.Equal(240, bitmap.Height);
        Assert.Equal(thumb, again);
        Assert.Equal(written, File.GetLastWriteTimeUtc(again!));
    }

    [Fact]
    public void Not_an_image_gives_no_thumbnail()
    {
        Directory.CreateDirectory(_dir);
        var source = Path.Combine(_dir, "clip.avi");
        File.WriteAllBytes(source, [1, 2, 3, 4]);

        Assert.Null(new ThumbnailCache(_dir).GetOrCreate(source, "ffee00"));
    }
}
