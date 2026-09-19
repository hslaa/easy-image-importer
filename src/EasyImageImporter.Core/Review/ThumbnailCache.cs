using SkiaSharp;

namespace EasyImageImporter.Core.Review;

/// <summary>
/// Small JPEG previews on disk, named by content hash so they stay valid wherever the image moves.
/// JPEGs are decoded at 1/2, 1/4 or 1/8 size directly by the decoder, which keeps this fast on
/// an old CPU: a 12 MP trail-camera image never has to be decoded at full size for a thumbnail.
/// </summary>
public sealed class ThumbnailCache(string cacheDir, int width = 320)
{
    public int Width { get; } = width;

    /// <summary>Path to the thumbnail, creating it if needed. Null if the file isn't a readable image.</summary>
    public string? GetOrCreate(string imagePath, string sha256)
    {
        var path = Path.Combine(cacheDir, sha256[..2], sha256 + ".jpg");
        if (File.Exists(path)) return path;

        using var bitmap = Decode(imagePath, Width);
        if (bitmap is null) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 82))
        using (var file = File.Create(temp))
        {
            data.SaveTo(file);
        }

        // Another thread may have made the same thumbnail meanwhile; either copy is fine.
        try { File.Move(temp, path, overwrite: true); }
        catch (IOException) { File.Delete(temp); }
        return path;
    }

    /// <summary>Decodes an image to about <paramref name="targetWidth"/> pixels wide, cheaply.</summary>
    public static SKBitmap? Decode(string imagePath, int targetWidth)
    {
        using var codec = SKCodec.Create(imagePath);
        if (codec is null) return null;

        var full = codec.Info;
        var scale = Math.Min(1f, (float)targetWidth / full.Width);
        var decodeSize = codec.GetScaledDimensions(scale); // the decoder picks the nearest size it supports
        using var decoded = SKBitmap.Decode(codec, full.WithSize(decodeSize));
        if (decoded is null) return null;

        var height = (int)Math.Round((double)full.Height * targetWidth / full.Width);
        if (decoded.Width <= targetWidth) return decoded.Copy();
        return decoded.Resize(new SKImageInfo(targetWidth, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }
}
