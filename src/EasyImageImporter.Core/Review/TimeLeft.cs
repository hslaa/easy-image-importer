namespace EasyImageImporter.Core.Review;

/// <summary>"omtrent 4 minutter igjen": how long is left at the pace so far.</summary>
public static class TimeLeft
{
    /// <summary>Too early to tell before this: the first frames include loading the models.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(5);

    /// <param name="elapsed">Time spent so far.</param>
    /// <param name="done">Work done in that time (frames, bytes).</param>
    /// <param name="left">Work left, in the same unit.</param>
    /// <returns>The text, or null while there isn't enough to go on yet, or nothing is left.</returns>
    public static string? Estimate(TimeSpan elapsed, double done, double left)
    {
        if (elapsed < SettleTime || done <= 0 || left <= 0) return null;
        var minutes = elapsed.TotalMinutes / done * left;
        if (minutes < 1) return "under ett minutt igjen";
        if (minutes < 60)
        {
            var m = (int)Math.Ceiling(minutes);
            return m == 1 ? "omtrent 1 minutt igjen" : $"omtrent {m} minutter igjen";
        }

        // Over an hour: to the nearest ten minutes is as precise as it gets.
        var rounded = (int)Math.Round(minutes / 10) * 10;
        var (hours, rest) = (rounded / 60, rounded % 60);
        var hourText = hours == 1 ? "1 time" : $"{hours} timer";
        return rest == 0 ? $"omtrent {hourText} igjen" : $"omtrent {hourText} og {rest} minutter igjen";
    }
}
