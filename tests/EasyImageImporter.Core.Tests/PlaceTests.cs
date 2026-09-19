using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Tests;

public sealed class PlaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "easyimageimporter-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 6, 1, 8, 0, 0);
    private int _visitId;

    public PlaceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A visit of 4 frames at <paramref name="place"/>, <paramref name="hours"/> after the start.</summary>
    private SiteInput Visit(int place, double hours, bool night = false)
    {
        var id = ++_visitId;
        var frames = Enumerable.Range(0, 4).Select(f =>
        {
            var path = Path.Combine(_dir, $"v{id}-{f}.jpg");
            File.WriteAllBytes(path, TestImages.Scene(place, id * 10 + f, night));
            return path;
        }).ToList();
        var start = Start.AddHours(hours);
        return new SiteInput(id, start, start.AddMinutes(1), Scene.FromFrames(frames));
    }

    [Fact]
    public void The_same_place_looks_alike_and_another_place_does_not()
    {
        var a1 = Visit(1, 0).Scene!;
        var a2 = Visit(1, 5).Scene!;
        var b = Visit(2, 10).Scene!;

        Assert.True(a1.Similarity(a2) > SiteDetector.SamePlace, $"same place: {a1.Similarity(a2):F2}");
        Assert.True(a1.Similarity(b) < SiteDetector.SamePlace, $"different places: {a1.Similarity(b):F2}");
    }

    [Fact]
    public void Infrared_frames_are_recognised_as_night()
    {
        Assert.True(Visit(1, 0, night: true).Scene!.IsNight);
        Assert.False(Visit(1, 1).Scene!.IsNight);
    }

    [Fact]
    public void A_moved_camera_starts_a_new_place_and_a_return_joins_the_first()
    {
        var visits = new[] { Visit(1, 0), Visit(1, 6), Visit(1, 30), Visit(2, 80), Visit(2, 90), Visit(1, 150), Visit(1, 160) };

        var places = SiteDetector.Detect(visits);

        Assert.Equal([[1L, 2, 3, 6, 7], [4L, 5]], places.Select(p => p.ToArray()));
    }

    [Fact]
    public void Night_visits_after_a_move_follow_the_longest_quiet_spell()
    {
        // Day and night at place 1; moved during a two-day pause; the first photos at place 2 are
        // at night, so nothing can tell they are new until the first day photo there.
        var visits = new[]
        {
            Visit(1, 0), Visit(1, 13, night: true), Visit(1, 24),
            Visit(2, 72, night: true), Visit(2, 84),
        };

        var places = SiteDetector.Detect(visits);

        Assert.Equal([[1L, 2, 3], [4L, 5]], places.Select(p => p.ToArray()));
    }

    [Fact]
    public void One_odd_visit_does_not_start_a_new_place()
    {
        var visits = new[] { Visit(1, 0), Visit(1, 5), Visit(3, 10), Visit(1, 15), Visit(1, 20) };

        var places = SiteDetector.Detect(visits);

        Assert.Single(places);
    }
}
