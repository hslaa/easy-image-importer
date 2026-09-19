using System.Text.Json;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Tests;

/// <summary>
/// Scores visit grouping against real camera-trap images with known answers (built by
/// tools/testdata/build_cards.py into testdata/, which is not in git). Skipped when absent, e.g. on CI.
/// </summary>
public sealed class RealCardTests
{
    private sealed record Expected(string Card, List<ExpectedFile> Files);
    private sealed record ExpectedFile(string Path, string Sequence, string? Site);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static TheoryData<string> Cards()
    {
        var data = new TheoryData<string>();
        foreach (var dir in CardDirs()) data.Add(Path.GetFileName(dir));
        if (data.Count == 0) data.Add("(none)");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cards))]
    public void Visit_grouping_matches_the_real_sequences(string card)
    {
        var dir = CardDirs().FirstOrDefault(d => Path.GetFileName(d) == card);
        Assert.SkipWhen(dir is null, "No generated test cards; run tools/testdata/build_cards.py");

        var expected = JsonSerializer.Deserialize<Expected>(File.ReadAllText(Path.Combine(dir!, "expected.json")), Json)!;
        var files = expected.Files.Select((f, i) =>
        {
            var path = Path.Combine(dir!, f.Path);
            var info = MediaInfoReader.Read(path, File.GetLastWriteTimeUtc(path), path);
            return (Id: (long)i, Truth: f.Sequence, Input: new SequenceInput(i, info.TakenAt!.Value, info.Camera, f.Path));
        }).ToList();

        var predicted = new Dictionary<long, int>();
        var visits = SequenceBuilder.Build(files.Select(f => f.Input));
        for (var v = 0; v < visits.Count; v++)
            foreach (var id in visits[v]) predicted[id] = v;

        // Walk images in time order and compare, for each neighbouring pair, "same visit?" with the truth.
        var ordered = files.OrderBy(f => f.Input.TakenAt).ThenBy(f => f.Input.RelPath, StringComparer.Ordinal).ToList();
        int falseSplits = 0, falseMerges = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var sameTruth = ordered[i].Truth == ordered[i - 1].Truth;
            var samePredicted = predicted[ordered[i].Id] == predicted[ordered[i - 1].Id];
            if (sameTruth && !samePredicted) falseSplits++;
            if (!sameTruth && samePredicted) falseMerges++;
        }

        var truthCount = files.Select(f => f.Truth).Distinct().Count();
        var exact = files.GroupBy(f => f.Truth)
            .Count(g => g.Select(f => predicted[f.Id]).Distinct().Count() == 1
                        && visits[predicted[g.First().Id]].Count == g.Count());
        TestContext.Current.SendDiagnosticMessage(
            $"{card}: {files.Count} images, {truthCount} true sequences → {visits.Count} visits. " +
            $"{exact} reproduced exactly. Neighbour pairs: {falseSplits} wrongly split, {falseMerges} wrongly merged.");

        // Merging two animals into one visit is worse than splitting one visit in two (a split
        // costs one extra click; a merge can hide an animal), so that is what we hold tight.
        Assert.True(falseMerges <= Math.Max(1, ordered.Count / 50), $"{falseMerges} wrongly merged neighbours");
    }

    private static IEnumerable<string> CardDirs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EasyImageImporter.sln"))) dir = dir.Parent;
        var cards = dir is null ? null : Path.Combine(dir.FullName, "testdata", "cards");
        return cards is not null && Directory.Exists(cards)
            ? Directory.GetDirectories(cards).Where(d => File.Exists(Path.Combine(d, "expected.json"))).Order()
            : [];
    }
}
