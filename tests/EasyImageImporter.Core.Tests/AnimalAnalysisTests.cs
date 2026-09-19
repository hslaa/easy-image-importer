using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Recognition;
using EasyImageImporter.Core.Review;

namespace EasyImageImporter.Core.Tests;

public sealed class AnimalAnalysisTests : IDisposable
{
    private readonly TestEnv _env = new();
    public void Dispose() => _env.Dispose();

    private const string Raven = "x;aves;passeriformes;corvidae;corvus;corax;common raven";
    private const string Corvids = "x;aves;passeriformes;corvidae;;;corvidae family";

    /// <summary>Answers from a script, keyed by staged file, and counts how often it was asked.</summary>
    private sealed class FakeRecognizer(Func<string, FrameAnalysis> answer) : IRecognizer
    {
        public int Calls;
        public FrameAnalysis Analyze(string imagePath)
        {
            Calls++;
            return answer(imagePath);
        }
        public void Dispose() { }
    }

    private static FrameAnalysis Empty() => new([], new Prediction(Taxonomy.Blank, 0.97, "classifier"), NorwegianNames.Empty);

    private static FrameAnalysis Saw(string label, double score, float confidence = 0.9f) =>
        new([new Detection("animal", confidence, 0.1f, 0.1f, 0.3f, 0.3f)], new Prediction(label, score, "classifier"),
            NorwegianNames.For(label)!);

    private static readonly DateTime Morning = new(2026, 9, 18, 7, 0, 0);

    /// <summary>Visit 1: 10 frames at 07:00; visit 2: 3 frames at 12:00; visit 3: 2 frames at 18:00.</summary>
    private Session ThreeVisits()
    {
        var n = 0;
        void Add(DateTime at, int count)
        {
            for (var i = 0; i < count; i++)
                _env.AddCardFile($"DCIM/100MEDIA/IMAG{++n:0000}.JPG", TestImages.JpegWithExif(at.AddSeconds(i * 10)));
        }
        Add(Morning, 10);
        Add(Morning.AddHours(5), 3);
        Add(Morning.AddHours(11), 2);
        return _env.CopyAndPrepare();
    }

    private AnimalAnalysis Analysis => new(_env.Store, _env.Review);

    [Fact]
    public void Long_visits_are_checked_at_four_spread_frames()
    {
        var session = ThreeVisits();
        var visits = _env.Review.GetOverview(session.Id).Visits;

        var checkedFrames = AnimalAnalysis.FramesToCheck(visits[0]);

        Assert.Equal([0, 3, 6, 9], checkedFrames.Select(f => visits[0].Frames.ToList().IndexOf(f)));
        Assert.Equal(3, AnimalAnalysis.FramesToCheck(visits[1]).Count);
    }

    [Fact]
    public void Suggestions_empty_animal_and_unsure()
    {
        var session = ThreeVisits();
        var visits = _env.Review.GetOverview(session.Id).Visits;
        var byPath = new Dictionary<long, FrameAnalysis>();
        foreach (var f in visits[0].Frames) byPath[f.Id] = Saw(Raven, 0.91);
        foreach (var f in visits[1].Frames) byPath[f.Id] = Empty();
        foreach (var f in visits[2].Frames) byPath[f.Id] = Saw(Corvids, 0.4);
        var staged = visits.SelectMany(v => v.Frames).ToDictionary(f => _env.Review.StagedPath(f), f => f.Id);
        var fake = new FakeRecognizer(path => byPath[staged[path]]);

        Analysis.Run(session.Id, fake);
        var result = _env.Review.GetOverview(session.Id).Visits;

        Assert.Equal(new AnimalSuggestion(SuggestionKind.Animal, "Ravn", 0.91), result[0].Suggestion);
        Assert.Equal(SuggestionKind.Empty, result[1].Suggestion!.Kind);
        Assert.Equal(new AnimalSuggestion(SuggestionKind.Unsure, "Kråkefugl", 0.4), result[2].Suggestion);
        Assert.Equal(4 + 3 + 2, fake.Calls); // only the checked frames
    }

    [Fact]
    public void Analysis_resumes_where_it_stopped_and_suggests_only_when_a_visit_is_done()
    {
        var session = ThreeVisits();
        using var stop = new CancellationTokenSource();
        var fake = new FakeRecognizer(_ =>
        {
            if (stop.IsCancellationRequested) throw new InvalidOperationException("called after cancel");
            return Saw(Raven, 0.9);
        });
        var progress = new SyncProgress<AnalysisProgress>(p => { if (p.VisitsDone == 1) stop.Cancel(); });

        Assert.Throws<OperationCanceledException>(() => Analysis.Run(session.Id, fake, progress, stop.Token));
        var halfway = _env.Review.GetOverview(session.Id).Visits;
        Assert.NotNull(halfway[0].Suggestion);
        Assert.Null(halfway[1].Suggestion);

        var again = new FakeRecognizer(_ => Saw(Raven, 0.9));
        var reports = new List<AnalysisProgress>();
        Analysis.Run(session.Id, again, new SyncProgress<AnalysisProgress>(reports.Add));

        Assert.Equal(3 + 2, again.Calls);
        // Frames count only work done in this run, so the time estimate isn't fooled by the resumed part.
        Assert.Equal([(0, 5), (3, 2), (5, 0)], reports.Select(r => (r.FramesAnalysed, r.FramesLeft)));
        Assert.All(_env.Review.GetOverview(session.Id).Visits, v => Assert.NotNull(v.Suggestion));
    }

    [Fact]
    public void Yes_makes_the_suggestion_the_label_and_no_forgets_it()
    {
        var session = ThreeVisits();
        Analysis.Run(session.Id, new FakeRecognizer(_ => Saw(Raven, 0.9)));
        var visits = _env.Review.GetOverview(session.Id).Visits;

        _env.Review.AcceptSuggestion(visits[0]);
        _env.Review.DismissSuggestion(visits[1]);
        var after = _env.Review.GetOverview(session.Id).Visits;

        Assert.Equal("Ravn", after[0].Label);
        Assert.False(after[0].HasOpenSuggestion);
        Assert.Null(after[1].Label);
        Assert.False(after[1].HasOpenSuggestion);
        Assert.True(after[2].HasOpenSuggestion);
    }

    [Fact]
    public void An_unreadable_photo_counts_as_nothing_found()
    {
        var session = ThreeVisits();
        Analysis.Run(session.Id, new FakeRecognizer(_ => throw new InvalidDataException("broken")));

        Assert.All(_env.Review.GetOverview(session.Id).Visits, v => Assert.Equal(SuggestionKind.Empty, v.Suggestion!.Kind));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
