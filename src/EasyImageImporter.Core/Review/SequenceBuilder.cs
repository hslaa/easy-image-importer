namespace EasyImageImporter.Core.Review;

public sealed record SequenceInput(long FileId, DateTime TakenAt, string? Camera, string RelPath);

/// <summary>
/// Splits a card's images into visits ("hendelser"): one camera's images, in time order, with a new
/// visit wherever the camera was quiet for longer than <see cref="DefaultGap"/>.
/// Only gaps matter, so a camera whose clock was reset to 2000-01-01 still groups correctly.
/// </summary>
public static class SequenceBuilder
{
    /// <summary>
    /// Long enough that an animal feeding at the bait with pauses stays one visit. On the real test
    /// cards (tools/testdata) nothing mixes different animals up to 30 minutes; at 60 it starts to.
    /// </summary>
    public static readonly TimeSpan DefaultGap = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<IReadOnlyList<long>> Build(IEnumerable<SequenceInput> images, TimeSpan? gap = null)
    {
        var maxGap = gap ?? DefaultGap;
        var sequences = new List<(DateTime Start, List<long> Files)>();

        foreach (var camera in images.GroupBy(i => i.Camera ?? ""))
        {
            List<long>? current = null;
            DateTime last = default;
            foreach (var image in camera.OrderBy(i => i.TakenAt).ThenBy(i => i.RelPath, StringComparer.Ordinal))
            {
                if (current is null || image.TakenAt - last > maxGap)
                {
                    current = [];
                    sequences.Add((image.TakenAt, current));
                }
                current.Add(image.FileId);
                last = image.TakenAt;
            }
        }

        return sequences.OrderBy(s => s.Start).Select(s => (IReadOnlyList<long>)s.Files).ToList();
    }
}
