using EasyImageImporter.Core.Import;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;

namespace EasyImageImporter.Core.Review;

public sealed record MediaInfo(MediaKind Kind, DateTime? TakenAt, string Source, string? Camera);

/// <summary>
/// When and with which camera an image was taken. EXIF DateTimeOriginal first; the card's file
/// time as fallback (trail cameras write it at capture, in local time).
/// </summary>
public static class MediaInfoReader
{
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".tif", ".tiff",
    };

    public static MediaKind KindOf(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path)) ? MediaKind.Image : MediaKind.Video;

    /// <param name="path">The staged copy (fast local disk, not the card).</param>
    /// <param name="cardMtimeUtc">Modification time of the original on the card.</param>
    /// <param name="originalName">Name on the card; decides image vs video.</param>
    public static MediaInfo Read(string path, DateTime cardMtimeUtc, string originalName)
    {
        var kind = KindOf(originalName);
        var fallback = new MediaInfo(kind, cardMtimeUtc.ToLocalTime(), "mtime", null);
        if (kind != MediaKind.Image) return fallback;

        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();

            var camera = string.Join(" ", new[]
            {
                ifd0?.GetDescription(ExifDirectoryBase.TagMake),
                ifd0?.GetDescription(ExifDirectoryBase.TagModel),
                sub?.GetDescription(ExifDirectoryBase.TagBodySerialNumber),
            }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

            DateTime taken = default;
            var hasDate = sub?.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out taken) == true
                          || ifd0?.TryGetDateTime(ExifDirectoryBase.TagDateTime, out taken) == true;

            return new MediaInfo(kind,
                hasDate ? DateTime.SpecifyKind(taken, DateTimeKind.Unspecified) : fallback.TakenAt,
                hasDate ? "exif" : "mtime",
                camera.Length > 0 ? camera : null);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException)
        {
            return fallback;
        }
    }
}
