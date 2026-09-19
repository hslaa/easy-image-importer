using System.Globalization;
using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Recognition;

namespace EasyImageImporter.Core.Review;

public enum SuggestionKind { Empty, Animal, Unsure }

/// <summary>A visit's suggested label. Never acted on without the user: suggest and confirm.</summary>
public sealed record AnimalSuggestion(SuggestionKind Kind, string Name, double Score);

public readonly record struct AnalysisProgress(int VisitsDone, int VisitsTotal, long? VisitId);

/// <summary>
/// Looks at a few frames of every visit with the animal-recognition models, in the background,
/// and turns what it sees into one suggestion per visit.
/// </summary>
public sealed class AnimalAnalysis(ImportStore store, ReviewService review)
{
    public const string ModelVersion = "speciesnet-4.0.3a/onnx-1";

    /// <summary>Anything above this in a checked frame means "not empty" (SpeciesNet uses 0.2 too).</summary>
    public const double SomethingThere = 0.2;

    /// <summary>Below this, a suggestion is shown as unsure.</summary>
    public const double Confident = 0.5;

    /// <summary>
    /// Up to four frames spread over the visit: enough to catch the animal in a burst, without
    /// spending several seconds per frame on all 200 of them on an older PC.
    /// </summary>
    public static IReadOnlyList<SessionFile> FramesToCheck(Visit visit)
    {
        var frames = visit.Frames;
        if (frames.Count <= 4) return frames;
        return new[] { 0, frames.Count / 3, 2 * frames.Count / 3, frames.Count - 1 }.Distinct().Select(i => frames[i]).ToList();
    }

    /// <summary>The visit's suggestion, or null until all its checked frames have been analysed.</summary>
    public static AnimalSuggestion? Suggest(Visit visit, IReadOnlyDictionary<long, FrameResult> results)
    {
        var checkedFrames = FramesToCheck(visit);
        if (checkedFrames.Any(f => !results.ContainsKey(f.Id))) return null;
        var frames = checkedFrames.Select(f => results[f.Id]).ToList();

        var strongest = frames.Max(r => r.TopConfidence);
        if (strongest < SomethingThere) return new AnimalSuggestion(SuggestionKind.Empty, NorwegianNames.Empty, 1 - strongest);

        // The most certain real answer among the frames: not "blank", not "no result".
        var best = frames
            .Where(r => r.Prediction is not (Taxonomy.Blank or Taxonomy.Unknown))
            .MaxBy(r => r.Score);
        if (best is null) return new AnimalSuggestion(SuggestionKind.Unsure, "Dyr", strongest);
        return new AnimalSuggestion(best.Score >= Confident && best.Name != NorwegianNames.Unsure
            ? SuggestionKind.Animal : SuggestionKind.Unsure, best.Name, best.Score);
    }

    /// <summary>
    /// Analyses every visit's checked frames that haven't been analysed yet, in review order.
    /// Stops at cancellation; everything done so far is kept.
    /// </summary>
    public void Run(long sessionId, IRecognizer recognizer, IProgress<AnalysisProgress>? progress = null, CancellationToken ct = default)
    {
        var visits = review.GetOverview(sessionId).Visits;
        var done = store.GetFrameResults(sessionId);
        var finished = 0;
        foreach (var visit in visits)
        {
            foreach (var frame in FramesToCheck(visit).Where(f => !done.ContainsKey(f.Id)))
            {
                ct.ThrowIfCancellationRequested();
                FrameAnalysis analysis;
                try
                {
                    analysis = recognizer.Analyze(review.StagedPath(frame));
                }
                catch (InvalidDataException)
                {
                    // Not decodable: count it as "nothing found" rather than retrying it forever.
                    analysis = new FrameAnalysis([], new Prediction(Taxonomy.Unknown, 0, "unreadable"), NorwegianNames.Unsure);
                }
                var top = analysis.Detections.Count > 0 ? analysis.Detections[0] : null;
                store.SaveFrameResult(new FrameResult(frame.Id, top?.Label, analysis.TopConfidence,
                    top is null ? null : string.Create(CultureInfo.InvariantCulture, $"{top.X:F4} {top.Y:F4} {top.Width:F4} {top.Height:F4}"),
                    analysis.Prediction.Label, analysis.Prediction.Score, analysis.NorwegianName), ModelVersion);
            }
            progress?.Report(new AnalysisProgress(++finished, visits.Count, visit.Id));
        }
    }
}
