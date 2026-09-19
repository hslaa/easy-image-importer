namespace EasyImageImporter.Core.Recognition;

public sealed record Detection(string Label, float Confidence, float X, float Y, float Width, float Height);

public sealed record Prediction(string Label, double Score, string Source);

/// <summary>
/// SpeciesNet's decision logic, ported line by line (speciesnet/ensemble_prediction_combiner.py
/// and geofence_utils.py, v5.0.5): combines the detector and the classifier into one answer, rolls
/// uncertain species up to genus, family, … and drops anything not found in Norway.
/// </summary>
public sealed class Ensemble(Taxonomy taxonomy)
{
    public Prediction Combine(IReadOnlyList<string> classes, IReadOnlyList<double> scores, IReadOnlyList<Detection> detections)
    {
        var topClass = classes[0];
        var topScore = scores[0];
        var topDetection = detections.Count > 0 ? detections[0].Label : "animal";
        var topDetectionScore = detections.Count > 0 ? detections[0].Confidence : 0.0;

        if (topDetection == "human")
        {
            if (topDetectionScore > 0.7) return new(Taxonomy.Human, topDetectionScore, "detector");
            if (topDetectionScore > 0.2 && topClass is Taxonomy.Human or Taxonomy.Vehicle && topScore > 0.5)
                return new(Taxonomy.Human, topScore, "classifier");
        }
        if (topDetection == "vehicle")
        {
            if (topDetectionScore > 0.2 && topClass == Taxonomy.Human && topScore > 0.5)
                return new(Taxonomy.Human, topScore, "classifier");
            if (topDetectionScore > 0.7) return new(Taxonomy.Vehicle, topDetectionScore, "detector");
            if (topDetectionScore > 0.2 && topClass == Taxonomy.Vehicle && topScore > 0.4)
                return new(Taxonomy.Vehicle, topScore, "classifier");
        }
        if (topDetectionScore < 0.2 && topClass == Taxonomy.Blank && topScore > 0.5)
            return new(Taxonomy.Blank, topScore, "classifier");
        if (topClass == Taxonomy.Blank && topScore > 0.99)
            return new(Taxonomy.Blank, topScore, "classifier");

        if (topClass is not (Taxonomy.Blank or Taxonomy.Human or Taxonomy.Vehicle))
        {
            if (topScore > 0.8) return Geofence(classes, scores);
            if (topScore > 0.65 && topDetection == "animal" && topDetectionScore > 0.2) return Geofence(classes, scores);
        }

        var rollup = RollUp(classes, scores, ["genus", "family", "order", "class", "kingdom"], 0.65);
        if (rollup is not null) return rollup;
        if (topDetection == "animal" && topDetectionScore > 0.5) return new(Taxonomy.Animal, topDetectionScore, "detector");
        return new(Taxonomy.Unknown, topScore, "classifier");
    }

    private Prediction Geofence(IReadOnlyList<string> classes, IReadOnlyList<double> scores)
    {
        if (!Blocked(classes[0])) return new(classes[0], scores[0], "classifier");
        var rollup = RollUp(classes, scores, ["family", "order", "class", "kingdom"], scores[0] - 1e-10);
        return rollup is null
            ? new(Taxonomy.Unknown, scores[0], "classifier+geofence+rollup_failed")
            : rollup with { Source = "classifier+geofence+" + rollup.Source["classifier+".Length..] };
    }

    private Prediction? RollUp(IReadOnlyList<string> classes, IReadOnlyList<double> scores, string[] levels, double threshold)
    {
        foreach (var level in levels)
        {
            var accumulated = new Dictionary<string, double>();
            var order = new List<string>(); // Python dicts keep insertion order; ties go to the first
            for (var i = 0; i < classes.Count; i++)
            {
                if (taxonomy.Ancestor(classes[i], level) is not { } ancestor) continue;
                if (!accumulated.ContainsKey(ancestor)) order.Add(ancestor);
                accumulated[ancestor] = accumulated.GetValueOrDefault(ancestor) + scores[i];
            }

            string? best = null;
            var bestScore = 0.0;
            foreach (var label in order)
                if (accumulated[label] > bestScore && !Blocked(label))
                {
                    best = label;
                    bestScore = accumulated[label];
                }
            if (bestScore > threshold && best is not null) return new(best, bestScore, $"classifier+rollup_to_{level}");
        }
        return null;
    }

    private bool Blocked(string label) => Taxonomy.Parts(label)[1] != "" && taxonomy.IsBlocked(label);
}
