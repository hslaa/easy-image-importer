using SkiaSharp;

namespace EasyImageImporter.Core.Review;

/// <summary>
/// What a camera placement looks like without the animal: the per-pixel median of several small
/// frames from one visit, reduced to its edges (tree trunks, horizon, stones), which survive
/// changing light and weather far better than brightness does.
/// </summary>
public sealed class Scene
{
    public const int Width = 128, Height = 96;

    // Trail cameras burn a date/temperature strip into the top or bottom of every frame. It would
    // look the same everywhere and make different places seem alike, so it is cut away.
    private const double CropTop = 0.08, CropBottom = 0.12;

    private Scene(float[] edges, bool night)
    {
        Edges = edges;
        IsNight = night;
    }

    /// <summary>Edge strength per pixel, normalized to mean 0 and length 1 for correlation.</summary>
    public float[] Edges { get; }

    /// <summary>Infrared night frames are grey; they only ever get compared with other night frames.</summary>
    public bool IsNight { get; }

    /// <summary>Builds the scene of one visit from (up to) a handful of its frames. Null if none decode.</summary>
    public static Scene? FromFrames(IEnumerable<string> imagePaths)
    {
        var frames = new List<float[]>();
        int nightVotes = 0;
        foreach (var path in imagePaths)
        {
            var frame = LoadGray(path, out var night);
            if (frame is null) continue;
            frames.Add(frame);
            if (night) nightVotes++;
        }
        if (frames.Count == 0) return null;

        var median = Median(frames);
        return new Scene(Normalize(Sobel(median)), nightVotes * 2 > frames.Count);
    }

    /// <summary>
    /// 1 = identical edges, around 0 = unrelated. The best match over small shifts, so a camera that
    /// was nudged a little still matches itself.
    /// </summary>
    public double Similarity(Scene other, int maxShift = 6)
    {
        var best = double.MinValue;
        for (var dy = -maxShift; dy <= maxShift; dy += 2)
        for (var dx = -maxShift; dx <= maxShift; dx += 2)
            best = Math.Max(best, Correlation(Edges, other.Edges, dx, dy));
        return best;
    }

    private static double Correlation(float[] a, float[] b, int dx, int dy)
    {
        double sum = 0;
        int x0 = Math.Max(0, dx), x1 = Math.Min(Width, Width + dx);
        int y0 = Math.Max(0, dy), y1 = Math.Min(Height, Height + dy);
        for (var y = y0; y < y1; y++)
        {
            int rowA = y * Width, rowB = (y - dy) * Width - dx;
            for (var x = x0; x < x1; x++) sum += a[rowA + x] * b[rowB + x];
        }
        // Scale up for the part that fell outside, so shifted and unshifted scores compare fairly.
        var overlap = (double)(x1 - x0) * (y1 - y0) / (Width * Height);
        return sum / overlap;
    }

    private static float[]? LoadGray(string path, out bool night)
    {
        night = false;
        using var decoded = ThumbnailCache.Decode(path, Width * 2);
        if (decoded is null) return null;

        var top = (int)(decoded.Height * CropTop);
        var height = decoded.Height - top - (int)(decoded.Height * CropBottom);
        using var cropped = new SKBitmap();
        if (!decoded.ExtractSubset(cropped, new SKRectI(0, top, decoded.Width, top + height))) return null;
        using var small = cropped.Resize(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        if (small is null) return null;

        var pixels = small.Pixels;
        var gray = new float[Width * Height];
        double colourfulness = 0;
        for (var i = 0; i < gray.Length; i++)
        {
            var c = pixels[i];
            gray[i] = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
            colourfulness += Math.Max(c.Red, Math.Max(c.Green, c.Blue)) - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
        }
        night = colourfulness / gray.Length < 6;
        return gray;
    }

    private static float[] Median(List<float[]> frames)
    {
        var result = new float[frames[0].Length];
        var values = new float[frames.Count];
        for (var i = 0; i < result.Length; i++)
        {
            for (var f = 0; f < frames.Count; f++) values[f] = frames[f][i];
            Array.Sort(values);
            result[i] = values[values.Length / 2];
        }
        return result;
    }

    private static float[] Sobel(float[] g)
    {
        var e = new float[g.Length];
        for (var y = 1; y < Height - 1; y++)
        for (var x = 1; x < Width - 1; x++)
        {
            int i = y * Width + x;
            float gx = g[i - Width + 1] + 2 * g[i + 1] + g[i + Width + 1] - g[i - Width - 1] - 2 * g[i - 1] - g[i + Width - 1];
            float gy = g[i + Width - 1] + 2 * g[i + Width] + g[i + Width + 1] - g[i - Width - 1] - 2 * g[i - Width] - g[i - Width + 1];
            e[i] = MathF.Sqrt(gx * gx + gy * gy);
        }
        return e;
    }

    private static float[] Normalize(float[] v)
    {
        var mean = v.Average();
        double sq = 0;
        for (var i = 0; i < v.Length; i++)
        {
            v[i] -= mean;
            sq += v[i] * v[i];
        }
        var norm = (float)Math.Sqrt(sq);
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }
}
