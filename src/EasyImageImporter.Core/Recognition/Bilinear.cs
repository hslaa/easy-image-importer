using SkiaSharp;

namespace EasyImageImporter.Core.Recognition;

/// <summary>
/// Plain bilinear resizing with half-pixel centres and no anti-aliasing: what OpenCV's
/// INTER_LINEAR (detector) and torchvision's resize(antialias=False) (classifier) do. Matching
/// them pixel for pixel keeps the models' answers identical to SpeciesNet's own.
/// </summary>
internal static class Bilinear
{
    /// <summary>Resizes a region of <paramref name="image"/> to width×height, as RGB floats 0–255.</summary>
    public static float[] Resize(SKBitmap image, SKRectI region, int width, int height)
    {
        var src = image.Pixels;
        int srcW = image.Width, rw = region.Width, rh = region.Height;
        var result = new float[width * height * 3];
        double sx = (double)rw / width, sy = (double)rh / height;

        var x0 = new int[width];
        var x1 = new int[width];
        var fx = new float[width];
        for (var x = 0; x < width; x++)
        {
            var fxs = Math.Max(0, (x + 0.5) * sx - 0.5);
            x0[x] = Math.Min((int)fxs, rw - 1);
            x1[x] = Math.Min(x0[x] + 1, rw - 1);
            fx[x] = (float)(fxs - x0[x]);
        }

        for (var y = 0; y < height; y++)
        {
            var fys = Math.Max(0, (y + 0.5) * sy - 0.5);
            var y0 = Math.Min((int)fys, rh - 1);
            var y1 = Math.Min(y0 + 1, rh - 1);
            var fy = (float)(fys - y0);
            int row0 = (region.Top + y0) * srcW + region.Left, row1 = (region.Top + y1) * srcW + region.Left;
            for (var x = 0; x < width; x++)
            {
                SKColor a = src[row0 + x0[x]], b = src[row0 + x1[x]], c = src[row1 + x0[x]], d = src[row1 + x1[x]];
                float w00 = (1 - fx[x]) * (1 - fy), w01 = fx[x] * (1 - fy), w10 = (1 - fx[x]) * fy, w11 = fx[x] * fy;
                var i = (y * width + x) * 3;
                result[i] = a.Red * w00 + b.Red * w01 + c.Red * w10 + d.Red * w11;
                result[i + 1] = a.Green * w00 + b.Green * w01 + c.Green * w10 + d.Green * w11;
                result[i + 2] = a.Blue * w00 + b.Blue * w01 + c.Blue * w10 + d.Blue * w11;
            }
        }
        return result;
    }
}
