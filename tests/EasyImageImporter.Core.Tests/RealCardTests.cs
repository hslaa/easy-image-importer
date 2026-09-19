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

    [Theory]
    [MemberData(nameof(Cards))]
    public void Places_match_the_real_camera_placements(string card)
    {
        var (visits, _) = LoadVisits(card, needSites: true);
        var truth = visits.ToDictionary(v => v.Input.VisitId, v => v.Site);

        var places = SiteDetector.Detect(visits.Select(v => v.Input).ToList());

        var impure = places.Count(p => p.Select(id => truth[id]).Distinct().Count() > 1);
        var realPlaces = truth.Values.Distinct().Count();
        TestContext.Current.SendDiagnosticMessage(
            $"{card}: {visits.Count} visits, {realPlaces} real placements → {places.Count} places found: " +
            string.Join(" | ", places.Select(p => string.Join(",", p.Select(id => truth[id]).GroupBy(x => x).Select(g => $"{g.Key}×{g.Count()}")))));

        Assert.Equal(0, impure);
        Assert.Equal(realPlaces, places.Count);
    }

    private (List<(SiteInput Input, string Site)> Visits, string Dir) LoadVisits(string card, bool needSites)
    {
        var dir = CardDirs().FirstOrDefault(d => Path.GetFileName(d) == card);
        Assert.SkipWhen(dir is null, "No generated test cards; run tools/testdata/build_cards.py");
        var expected = JsonSerializer.Deserialize<Expected>(File.ReadAllText(Path.Combine(dir!, "expected.json")), Json)!;
        Assert.SkipWhen(needSites && expected.Files.Any(f => f.Site is null), $"{card} has no place ground truth");

        var inputs = expected.Files.Select((f, i) =>
        {
            var path = Path.Combine(dir!, f.Path);
            var info = MediaInfoReader.Read(path, File.GetLastWriteTimeUtc(path), path);
            return (Input: new SequenceInput(i, info.TakenAt!.Value, info.Camera, f.Path), Path: path);
        }).ToList();
        var visits = SequenceBuilder.Build(inputs.Select(x => x.Input)).Select((ids, n) =>
        {
            var frames = ids.Select(id => inputs[(int)id]).ToList();
            var scene = Scene.FromFrames(ReviewService.SampleFrames(frames.Select(f => f.Path).ToList()));
            return (new SiteInput(n, frames[0].Input.TakenAt, frames[^1].Input.TakenAt, scene), expected.Files[(int)ids[0]].Site!);
        }).ToList();
        return (visits, dir!);
    }

    /// <summary>
    /// Not a pass/fail test: prints how alike visit backgrounds are, same place versus different
    /// places, day and night separately. This is what the place thresholds are chosen from.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cards))]
    public void Scene_similarity_report(string card)
    {
        var dir = CardDirs().FirstOrDefault(d => Path.GetFileName(d) == card);
        Assert.SkipWhen(dir is null, "No generated test cards; run tools/testdata/build_cards.py");
        var expected = JsonSerializer.Deserialize<Expected>(File.ReadAllText(Path.Combine(dir!, "expected.json")), Json)!;
        Assert.SkipWhen(expected.Files.Any(f => f.Site is null), $"{card} has no place ground truth");

        var inputs = expected.Files.Select((f, i) =>
        {
            var path = Path.Combine(dir!, f.Path);
            var info = MediaInfoReader.Read(path, File.GetLastWriteTimeUtc(path), path);
            return new SequenceInput(i, info.TakenAt!.Value, info.Camera, f.Path);
        }).ToList();
        var visits = SequenceBuilder.Build(inputs)
            .Select(ids => (Site: expected.Files[(int)ids[0]].Site!, Scene: Scene.FromFrames(
                ids.Where((_, i) => i % Math.Max(1, ids.Count / 12) == 0).Select(id => Path.Combine(dir!, expected.Files[(int)id].Path)))!))
            .ToList();

        var same = new List<double>();
        var different = new List<double>();
        for (var i = 0; i < visits.Count; i++)
        for (var j = i + 1; j < visits.Count; j++)
        {
            if (visits[i].Scene.IsNight != visits[j].Scene.IsNight) continue;
            (visits[i].Site == visits[j].Site ? same : different).Add(visits[i].Scene.Similarity(visits[j].Scene));
        }

        static string Stats(List<double> v) => v.Count == 0 ? "none" :
            $"n={v.Count} min={v.Min():F2} p5={Pct(v, 5):F2} median={Pct(v, 50):F2} p95={Pct(v, 95):F2} max={v.Max():F2}";
        static double Pct(List<double> v, int p) => v.Order().ElementAt(Math.Min(v.Count - 1, v.Count * p / 100));
        TestContext.Current.SendDiagnosticMessage(
            $"{card}: {visits.Count} visits ({visits.Count(v => v.Scene.IsNight)} night). " +
            $"Same place: {Stats(same)}. Different places: {Stats(different)}.");
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
