using System.Text;
using SkiaSharp;

namespace EasyImageImporter.Core.Tests;

/// <summary>Real JPEGs for tests, optionally with a minimal hand-written EXIF block.</summary>
public static class TestImages
{
    public static byte[] Jpeg(int width = 640, int height = 480, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(color ?? new SKColor(60, 90, 50));
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawCircle(width / 3f, height / 2f, height / 5f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    /// <summary>A JPEG with EXIF Make, Model and DateTimeOriginal, as a trail camera writes them.</summary>
    public static byte[] JpegWithExif(DateTime takenAt, string make = "Browning", string model = "BTC-8E")
    {
        var jpeg = Jpeg();
        var tiff = Tiff(takenAt, make, model);
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var length = 2 + 6 + tiff.Length;
        app1.Add((byte)(length >> 8));
        app1.Add((byte)length);
        app1.AddRange("Exif\0\0"u8.ToArray());
        app1.AddRange(tiff);
        // SOI, then our APP1, then the rest of the original JPEG.
        return [.. jpeg[..2], .. app1, .. jpeg[2..]];
    }

    private static byte[] Tiff(DateTime takenAt, string make, string model)
    {
        // Little-endian TIFF: header, IFD0 (Make, Model, Exif pointer), Exif IFD (DateTimeOriginal), then strings.
        var makeBytes = Encoding.ASCII.GetBytes(make + "\0");
        var modelBytes = Encoding.ASCII.GetBytes(model + "\0");
        var dateBytes = Encoding.ASCII.GetBytes(takenAt.ToString("yyyy:MM:dd HH:mm:ss") + "\0");

        const int ifd0 = 8, ifd0Size = 2 + 3 * 12 + 4;
        const int exifIfd = ifd0 + ifd0Size, exifIfdSize = 2 + 12 + 4;
        var makeAt = exifIfd + exifIfdSize;
        var modelAt = makeAt + makeBytes.Length;
        var dateAt = modelAt + modelBytes.Length;

        var w = new BinaryWriter(new MemoryStream());
        w.Write("II"u8); w.Write((ushort)42); w.Write(ifd0);
        w.Write((ushort)3);
        Entry(w, 0x010F, 2, makeBytes.Length, makeAt);
        Entry(w, 0x0110, 2, modelBytes.Length, modelAt);
        Entry(w, 0x8769, 4, 1, exifIfd);
        w.Write(0);
        w.Write((ushort)1);
        Entry(w, 0x9003, 2, dateBytes.Length, dateAt);
        w.Write(0);
        w.Write(makeBytes); w.Write(modelBytes); w.Write(dateBytes);
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    private static void Entry(BinaryWriter w, ushort tag, ushort type, int count, int valueOrOffset)
    {
        w.Write(tag); w.Write(type); w.Write(count); w.Write(valueOrOffset);
    }
}
