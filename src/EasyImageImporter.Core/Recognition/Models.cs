using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace EasyImageImporter.Core.Recognition;

/// <summary>
/// MegaDetector v5a, as used by SpeciesNet: finds animals, people and vehicles.
/// Image preparation and box handling follow speciesnet/detector.py and YOLOv5 exactly.
/// </summary>
public sealed class Detector(InferenceSession session) : IDisposable
{
    private const int Size = 1280, Stride = 64;
    private const float ConfidenceThreshold = 0.01f, IouThreshold = 0.45f, ClassOffset = 7680;
    private const int MaxDetections = 300;
    private static readonly string[] ClassNames = ["animal", "human", "vehicle"];

    public IReadOnlyList<Detection> Detect(SKBitmap image)
    {
        var (input, gain, padX, padY) = Letterbox(image);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor("images", input)]);
        var raw = results[0].AsTensor<float>();
        return Boxes(raw, image.Width, image.Height, (float)gain, padX, padY);
    }

    /// <summary>
    /// YOLOv5 letterbox with auto=True: scale the long side to 1280, then pad each side with grey
    /// (114) so both sides are multiples of 64. Returns a 1×3×H×W tensor with values 0–1.
    /// </summary>
    private static (DenseTensor<float> Tensor, double Gain, float PadX, float PadY) Letterbox(SKBitmap image)
    {
        var r = Math.Min((double)Size / image.Height, (double)Size / image.Width);
        var newW = (int)Math.Round(image.Width * r, MidpointRounding.ToEven);
        var newH = (int)Math.Round(image.Height * r, MidpointRounding.ToEven);
        var dw = (Size - newW) % Stride / 2.0;
        var dh = (Size - newH) % Stride / 2.0;
        var top = (int)Math.Round(dh - 0.1, MidpointRounding.ToEven);
        var bottom = (int)Math.Round(dh + 0.1, MidpointRounding.ToEven);
        var left = (int)Math.Round(dw - 0.1, MidpointRounding.ToEven);
        var right = (int)Math.Round(dw + 0.1, MidpointRounding.ToEven);
        int width = newW + left + right, height = newH + top + bottom;

        // OpenCV resizes 8-bit images and rounds the result back to 8 bits.
        var resized = Bilinear.Resize(image, new SKRectI(0, 0, image.Width, image.Height), newW, newH);
        var tensor = new DenseTensor<float>([1, 3, height, width]);
        var span = tensor.Buffer.Span;
        span.Fill(114f / 255f);
        var plane = height * width;
        for (var y = 0; y < newH; y++)
        for (var x = 0; x < newW; x++)
        {
            var s = (y * newW + x) * 3;
            var i = (y + top) * width + x + left;
            span[i] = MathF.Round(resized[s]) / 255f;
            span[plane + i] = MathF.Round(resized[s + 1]) / 255f;
            span[2 * plane + i] = MathF.Round(resized[s + 2]) / 255f;
        }

        // scale_boxes recomputes the padding from the gain rather than using the letterbox's own.
        var gain = Math.Min((double)height / image.Height, (double)width / image.Width);
        return (tensor, gain, (float)((width - image.Width * gain) / 2), (float)((height - image.Height * gain) / 2));
    }

    /// <summary>YOLOv5 non_max_suppression + scale_boxes, then MegaDetector's [x, y, w, h] in 0–1.</summary>
    private static IReadOnlyList<Detection> Boxes(Tensor<float> raw, int imageW, int imageH, float gain, float padX, float padY)
    {
        var candidates = new List<(float X1, float Y1, float X2, float Y2, float Conf, int Cls)>();
        var count = raw.Dimensions[1];
        for (var i = 0; i < count; i++)
        {
            var objectness = raw[0, i, 4];
            if (objectness <= ConfidenceThreshold) continue;
            var cls = 0;
            var conf = raw[0, i, 5] * objectness;
            for (var k = 1; k < ClassNames.Length; k++)
            {
                var c = raw[0, i, 5 + k] * objectness;
                if (c > conf) (conf, cls) = (c, k);
            }
            if (conf <= ConfidenceThreshold) continue;
            float cx = raw[0, i, 0], cy = raw[0, i, 1], w = raw[0, i, 2], h = raw[0, i, 3];
            candidates.Add((cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, conf, cls));
        }

        var kept = new List<(float X1, float Y1, float X2, float Y2, float Conf, int Cls)>();
        foreach (var box in candidates.OrderByDescending(b => b.Conf))
        {
            // Boxes of different classes never suppress each other (the class offset trick).
            if (kept.Any(k => k.Cls == box.Cls && Iou(k, box) > IouThreshold)) continue;
            kept.Add(box);
            if (kept.Count == MaxDetections) break;
        }

        return kept.Select(b =>
        {
            float Clip(float v, float max) => Math.Clamp(v, 0, max);
            var x1 = MathF.Round(Clip((b.X1 - padX) / gain, imageW));
            var y1 = MathF.Round(Clip((b.Y1 - padY) / gain, imageH));
            var x2 = MathF.Round(Clip((b.X2 - padX) / gain, imageW));
            var y2 = MathF.Round(Clip((b.Y2 - padY) / gain, imageH));
            return new Detection(ClassNames[b.Cls], b.Conf, x1 / imageW, y1 / imageH, (x2 - x1) / imageW, (y2 - y1) / imageH);
        }).OrderByDescending(d => d.Confidence).ToList();
    }

    private static float Iou((float X1, float Y1, float X2, float Y2, float, int) a, (float X1, float Y1, float X2, float Y2, float, int) b)
    {
        var w = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        var h = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        var inter = w * h;
        var union = (a.X2 - a.X1) * (a.Y2 - a.Y1) + (b.X2 - b.X1) * (b.Y2 - b.Y1) - inter;
        return union <= 0 ? 0 : inter / union;
    }

    public void Dispose() => session.Dispose();
}

/// <summary>
/// SpeciesNet's species classifier ("always crop"): looks at the top detection's box, resized to
/// 480×480, and scores ~2 500 labels. Follows speciesnet/classifier.py.
/// </summary>
public sealed class Classifier(InferenceSession session, Taxonomy taxonomy) : IDisposable
{
    private const int Size = 480;

    public (IReadOnlyList<string> Classes, IReadOnlyList<double> Scores) Classify(SKBitmap image, Detection? box, int top = 5)
    {
        var rect = box is null
            ? new SKRectI(0, 0, image.Width, image.Height)
            : SKRectI.Create((int)(box.X * image.Width), (int)(box.Y * image.Height),
                Math.Max(1, (int)(box.Width * image.Width)), Math.Max(1, (int)(box.Height * image.Height)));
        rect.Intersect(new SKRectI(0, 0, image.Width, image.Height));

        if (rect.Width <= 0 || rect.Height <= 0) rect = new SKRectI(0, 0, image.Width, image.Height);

        // torchvision resizes floats, then convert_image_dtype truncates x·255.999 back to 8 bits.
        var resized = Bilinear.Resize(image, rect, Size, Size);
        var tensor = new DenseTensor<float>([1, Size, Size, 3]);
        var span = tensor.Buffer.Span;
        for (var i = 0; i < resized.Length; i++)
            span[i] = MathF.Floor(resized[i] / 255f * 255.999f) / 255f;

        using var results = session.Run([NamedOnnxValue.CreateFromTensor("crops", tensor)]);
        var logits = results[0].AsEnumerable<float>().ToArray();
        var max = logits.Max();
        var exp = logits.Select(l => Math.Exp(l - max)).ToArray();
        var sum = exp.Sum();
        var best = Enumerable.Range(0, exp.Length).OrderByDescending(i => exp[i]).Take(top).ToList();
        return (best.Select(i => taxonomy.Labels[i]).ToList(), best.Select(i => exp[i] / sum).ToList());
    }

    public void Dispose() => session.Dispose();
}
