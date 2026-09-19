using System.Text.Json;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Tests;

/// <summary>
/// Checks visit grouping against real camera-trap images with known answers (built by
/// tools/testdata/build_cards.py into testdata/, which is not in git). Skipped when absent, e.g. on CI.
///
/// The datasets' "sequences" are single camera triggers (a burst of frames within a second or two),
/// while a visit is one animal's visit and deliberately spans several triggers. So the checks are:
/// a trigger burst is never split, and two different animals are never merged into one visit.
/// </summary>
public sealed class RealCardTests
{
    private sealed record Expected(string Card, List<ExpectedFile> Files);
    private sealed record ExpectedFile(string Path, string? Sequence, string? Site, List<string>? Species);

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
        Assert.SkipWhen(expected.Files.Any(f => f.Sequence is null), $"{card} has no sequence ground truth");
        var files = expected.Files.Select((f, i) =>
        {
            var path = Path.Combine(dir!, f.Path);
            var info = MediaInfoReader.Read(path, File.GetLastWriteTimeUtc(path), path);
            return (Id: (long)i, Truth: f.Sequence!, Species: f.Species ?? [],
                Input: new SequenceInput(i, info.TakenAt!.Value, info.Camera, f.Path));
        }).ToList();

        var predicted = new Dictionary<long, int>();
        var visits = SequenceBuilder.Build(files.Select(f => f.Input));
        for (var v = 0; v < visits.Count; v++)
            foreach (var id in visits[v]) predicted[id] = v;

        // Walk images in time order and compare neighbours with the truth.
        var ordered = files.OrderBy(f => f.Input.TakenAt).ThenBy(f => f.Input.RelPath, StringComparer.Ordinal).ToList();
        int splitBursts = 0, triggersJoined = 0, differentAnimalsMerged = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var (a, b) = (ordered[i - 1], ordered[i]);
            var sameBurst = a.Truth == b.Truth;
            var sameVisit = predicted[a.Id] == predicted[b.Id];
            if (sameBurst && !sameVisit) splitBursts++;
            if (!sameBurst && sameVisit)
            {
                triggersJoined++;
                if (Known(a.Species).Any() && Known(b.Species).Any() && !Known(a.Species).Intersect(Known(b.Species)).Any())
                    differentAnimalsMerged++;
            }
        }

        var bursts = files.Select(f => f.Truth).Distinct().Count();
        TestContext.Current.SendDiagnosticMessage(
            $"{card}: {files.Count} images in {bursts} trigger bursts → {visits.Count} visits. " +
            $"{triggersJoined} bursts joined into a longer visit, {splitBursts} bursts split, " +
            $"{differentAnimalsMerged} times different animals in one visit.");

        Assert.Equal(0, splitBursts);
        // Rare, and every frame is still shown in the visit, but it must stay rare.
        Assert.True(differentAnimalsMerged <= Math.Max(1, ordered.Count / 100),
            $"{differentAnimalsMerged} visits mix different animals");
    }

    /// <summary>"unknown" and "empty" say nothing about which animal it is.</summary>
    private static IEnumerable<string> Known(IEnumerable<string> species) =>
        species.Where(s => s is not ("unknown" or "empty" or "blank"));

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
