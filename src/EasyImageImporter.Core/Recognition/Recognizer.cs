using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace EasyImageImporter.Core.Recognition;

/// <summary>What the models saw in one photo.</summary>
/// <param name="Detections">Animals, people and vehicles found, most certain first.</param>
/// <param name="Prediction">SpeciesNet's final answer: species, a group (family, …), blank, or unknown.</param>
/// <param name="NorwegianName">The answer in Norwegian ("Kongeørn", "Kråkefugl", "Tomt bilde").</param>
public sealed record FrameAnalysis(IReadOnlyList<Detection> Detections, Prediction Prediction, string NorwegianName)
{
    /// <summary>The detector's confidence that there is an animal (or person, or vehicle) at all.</summary>
    public double TopConfidence => Detections.Count > 0 ? Detections[0].Confidence : 0;
}

public interface IRecognizer : IDisposable
{
    FrameAnalysis Analyze(string imagePath);
}

/// <summary>
/// Animal recognition with SpeciesNet (MegaDetector + species classifier), converted to ONNX by
/// tools/models/convert.py and run on the CPU. Geofenced to Norway.
/// </summary>
public sealed class Recognizer : IRecognizer
{
    private readonly Detector _detector;
    private readonly Classifier _classifier;
    private readonly Ensemble _ensemble;

    public Recognizer(string modelsDir, int threads = 0)
    {
        var options = new SessionOptions
        {
            // Leave room for the UI: the analysis runs in the background while the user reviews.
            IntraOpNumThreads = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        var taxonomy = Taxonomy.Load(Path.Combine(modelsDir, ModelStore.TaxonomyFile));
        _detector = new Detector(new InferenceSession(Path.Combine(modelsDir, ModelStore.DetectorFile), options));
        _classifier = new Classifier(new InferenceSession(Path.Combine(modelsDir, ModelStore.ClassifierFile), options), taxonomy);
        _ensemble = new Ensemble(taxonomy);
    }

    public FrameAnalysis Analyze(string imagePath)
    {
        using var image = SKBitmap.Decode(imagePath) ?? throw new InvalidDataException($"Not an image: {imagePath}");
        var detections = _detector.Detect(image);
        var (classes, scores) = _classifier.Classify(image, detections.Count > 0 ? detections[0] : null);
        var prediction = _ensemble.Combine(classes, scores, detections);
        return new FrameAnalysis(detections, prediction, NorwegianNames.For(prediction.Label) ?? NorwegianNames.Unsure);
    }

    public void Dispose()
    {
        _detector.Dispose();
        _classifier.Dispose();
    }
}

public readonly record struct DownloadProgress(long Bytes, long Total);

/// <summary>
/// The model files (~390 MB): downloaded once, on first use, from the app's GitHub release, and
/// only used if their SHA-256 matches what this version of the app expects.
/// </summary>
public sealed class ModelStore(string modelsDir, HttpClient? http = null)
{
    public const string DetectorFile = "detector.onnx", ClassifierFile = "classifier.onnx", TaxonomyFile = "norway.json";
    public const string DefaultReleaseUrl = "https://github.com/hslaa/easy-image-importer/releases/download/models-1/";

    /// <summary>Where the files come from. EASYIMAGEIMPORTER_MODELS_URL overrides it (testing with a local server).</summary>
    public static string ReleaseUrl =>
        Environment.GetEnvironmentVariable("EASYIMAGEIMPORTER_MODELS_URL") is { Length: > 0 } url
            ? url.TrimEnd('/') + "/"
            : DefaultReleaseUrl;

    /// <summary>Pinned: a new model version means a new release tag and new hashes here.</summary>
    public static readonly IReadOnlyList<(string Name, long Bytes, string Sha256)> Files =
    [
        (DetectorFile, 280291609, "b6cdb23f63ff505568330dec3545724c1e72882324d901e5b409f0eb9ac414cf"),
        (ClassifierFile, 112320393, "936099ded55af155ef4963314b8cc347344b94b9d48dd83cadf8eaeffc92d774"),
        (TaxonomyFile, 769238, "cb974577e7b2a4bf19dcfdfd12eca7f6627bb1ef1b5dd169e5a3e7f32bd04aea"),
    ];

    public string Folder => modelsDir;
    public static long TotalBytes => Files.Sum(f => f.Bytes);

    /// <summary>All files present with the right size (hashes are checked when downloading).</summary>
    public bool IsInstalled => Files.All(f => new FileInfo(Path.Combine(modelsDir, f.Name)) is { Exists: true } info && info.Length == f.Bytes);

    public async Task DownloadAsync(IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(modelsDir);
        using var client = http is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : null;
        var web = http ?? client!;
        long done = 0;
        foreach (var (name, bytes, sha256) in Files)
        {
            var target = Path.Combine(modelsDir, name);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == bytes)
            {
                done += bytes;
                continue;
            }

            var part = target + ".part";
            using (var response = await web.GetAsync(ReleaseUrl + name, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(part);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new DownloadProgress(done, TotalBytes));
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal))
                {
                    output.Close();
                    File.Delete(part);
                    throw new InvalidDataException("Nedlastingen ble skadet underveis. Prøv igjen.");
                }
            }
            File.Move(part, target, overwrite: true);
        }
    }
}
