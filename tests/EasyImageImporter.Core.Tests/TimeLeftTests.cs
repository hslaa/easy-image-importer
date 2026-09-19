using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Tests;

public sealed class TimeLeftTests
{
    [Theory]
    [InlineData(2, 10, 100, null)]                                   // too early to tell
    [InlineData(10, 10, 0, null)]                                    // nothing left
    [InlineData(10, 10, 5, "under ett minutt igjen")]
    [InlineData(60, 60, 60, "omtrent 1 minutt igjen")]
    [InlineData(60, 60, 150, "omtrent 3 minutter igjen")]            // 2.5 min rounds up
    [InlineData(60, 60, 3600, "omtrent 1 time igjen")]
    [InlineData(60, 60, 5000, "omtrent 1 time og 20 minutter igjen")] // 83 min, to the nearest ten
    [InlineData(60, 60, 9000, "omtrent 2 timer og 30 minutter igjen")]
    public void Estimates_from_the_pace_so_far(int seconds, double done, double left, string? expected) =>
        Assert.Equal(expected, TimeLeft.Estimate(TimeSpan.FromSeconds(seconds), done, left));
}
