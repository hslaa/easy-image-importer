using System.Diagnostics;
using System.Text.Json;
using EasyImageImporter.Core.Recognition;

namespace EasyImageImporter.Core.Tests;

/// <summary>
/// The C# port against SpeciesNet itself: tools/models/verify.py writes, per photo, what the ONNX
/// models answer through SpeciesNet's own Python code (which in turn matches the original PyTorch
/// models). Needs the model files: set EASYIMAGEIMPORTER_MODELS to the folder convert.py wrote.
/// </summary>
public sealed class RecognitionTests
{
    private static string? Models => Environment.GetEnvironmentVariable("EASYIMAGEIMPORTER_MODELS") is { } dir
                                      && File.Exists(Path.Combine(dir, ModelStore.DetectorFile)) ? dir : null;

    [Fact]
    public void Answers_like_speciesnet_on_real_camera_trap_photos()
    {
        Assert.SkipWhen(Models is null, "Set EASYIMAGEIMPORTER_MODELS to run (see tools/models)");
        var golden = Path.Combine(Models!, "golden-reset-clock-NOR.json");
        var card = RealCards.Find("reset-clock");
        Assert.SkipWhen(!File.Exists(golden) || card is null, "Needs golden-reset-clock.json and the reset-clock test card");

        using var recognizer = new Recognizer(Models!, threads: 4);
        var expected = JsonDocument.Parse(File.ReadAllText(golden)).RootElement.EnumerateArray().ToList();
        int identical = 0, sameFamily = 0, emptyFlips = 0;
        var worst = 0.0;
        var clock = Stopwatch.StartNew();
        foreach (var e in expected)
        {
            var result = recognizer.Analyze(Path.Combine(card!, "DCIM", e.GetProperty("path").GetString()!));
            var dets = e.GetProperty("detections").EnumerateArray().ToList();
            var expectedTop = dets.Count > 0 ? dets[0].GetProperty("conf").GetDouble() : 0;
            worst = Math.Max(worst, Math.Abs(expectedTop - result.TopConfidence));
            if (expectedTop >= 0.2 != result.TopConfidence >= 0.2) emptyFlips++;

            var want = e.GetProperty("prediction").GetString()!;
            if (result.Prediction.Label == want) identical++;
            else if (Taxonomy.Parts(result.Prediction.Label)[1..4].SequenceEqual(Taxonomy.Parts(want)[1..4])) sameFamily++;
        }

        TestContext.Current.SendDiagnosticMessage(
            $"{expected.Count} photos: prediction identical {identical}, same family {sameFamily}, " +
            $"different {expected.Count - identical - sameFamily}; empty/animal flips {emptyFlips}; " +
            $"top confidence differs by at most {worst:F3}; {clock.Elapsed.TotalSeconds / expected.Count:F2} s per photo.");

        Assert.True(emptyFlips <= 1, $"{emptyFlips} photos flipped between empty and animal");
        // The few differences are photos right on one of SpeciesNet's thresholds (e.g. detector 0.5).
        Assert.True(identical >= expected.Count * 0.9, $"only {identical} identical");
    }

    [Theory]
    [InlineData("x;aves;passeriformes;corvidae;corvus;corax;common raven", "Ravn")]
    [InlineData("x;aves;passeriformes;corvidae;;;corvidae family", "Kråkefugl")]
    [InlineData("x;aves;passeriformes;thamnophilidae;thamnophilus;;thamnophilus species", "Spurvefugl")]
    [InlineData("x;mammalia;carnivora;canidae;vulpes;vulpes;red fox", "Rev")]
    [InlineData(Taxonomy.Blank, "Tomt bilde")]
    [InlineData(Taxonomy.Unknown, "Usikker")]
    public void Names_are_norwegian_down_to_the_nearest_known_group(string label, string expected) =>
        Assert.Equal(expected, NorwegianNames.For(label));
}

internal static class RealCards
{
    public static string? Find(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EasyImageImporter.sln"))) dir = dir.Parent;
        var card = dir is null ? null : Path.Combine(dir.FullName, "testdata", "cards", name);
        return card is not null && Directory.Exists(card) ? card : null;
    }
}
