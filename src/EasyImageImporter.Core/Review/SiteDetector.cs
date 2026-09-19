namespace EasyImageImporter.Core.Review;

public sealed record SiteInput(long VisitId, DateTime Start, DateTime End, Scene? Scene);

/// <summary>
/// Groups visits by camera placement ("sted"). One card normally comes from one camera that is
/// moved between placements, so places are stretches of time: walk the visits in order and start a
/// new place where the background stops matching. Afterwards, stretches that look like the same
/// place (A → B → A) are joined.
///
/// Thresholds come from the real test cards (RealCardTests.Scene_similarity_report): different
/// places never scored above 0.23, while a visit nearly always matches some earlier visit at its own
/// place well above that.
/// </summary>
public static class SiteDetector
{
    public const double SamePlace = 0.30;

    /// <summary>How many recent visits of the same kind (day/night) a new visit is compared with.</summary>
    private const int Memory = 8;

    public static IReadOnlyList<IReadOnlyList<long>> Detect(IReadOnlyList<SiteInput> visits)
    {
        var ordered = visits.OrderBy(v => v.Start).ToList();
        var stretches = new List<List<SiteInput>>();

        for (var i = 0; i < ordered.Count; i++)
        {
            var visit = ordered[i];
            var current = stretches.LastOrDefault();
            if (current is null || Matches(visit, current) != false)
            {
                // Matches, or can't tell yet (first night photo after days, or no usable scene).
                (current ?? Add(stretches)).Add(visit);
                continue;
            }

            // One odd visit (snow, an animal right in front of the lens) is not a move if the next
            // visit of the same kind matches the old place again.
            var next = ordered.Skip(i + 1).FirstOrDefault(v => v.Scene?.IsNight == visit.Scene!.IsNight);
            if (next is not null && Matches(next, current) == true)
            {
                current.Add(visit);
                continue;
            }

            MoveTail(current, visit, Add(stretches));
        }

        return JoinRevisits(stretches);
    }

    /// <summary>
    /// The camera was moved somewhere between the last visit that verifiably belonged here and this one.
    /// Visits in between couldn't be checked (other kind); the move most likely happened in the
    /// longest quiet period, so everything after that goes to the new place.
    /// </summary>
    private static void MoveTail(List<SiteInput> current, SiteInput visit, List<SiteInput> next)
    {
        var lastSameKind = current.FindLastIndex(v => v.Scene?.IsNight == visit.Scene!.IsNight);
        var candidates = current.Skip(lastSameKind).Append(visit).ToList();
        var cut = Enumerable.Range(1, candidates.Count - 1)
            .MaxBy(k => candidates[k].Start - candidates[k - 1].End);
        var moving = candidates.Skip(cut).Where(v => v != visit).ToList();
        current.RemoveAll(moving.Contains);
        next.AddRange(moving);
        next.Add(visit);
    }

    /// <summary>True/false if this visit does/doesn't look like the place; null if there is nothing to compare with.</summary>
    private static bool? Matches(SiteInput visit, IEnumerable<SiteInput> place)
    {
        if (visit.Scene is null) return null;
        var recent = place.Where(v => v.Scene?.IsNight == visit.Scene.IsNight).TakeLast(Memory).ToList();
        if (recent.Count == 0) return null;
        return recent.Max(v => visit.Scene.Similarity(v.Scene!)) >= SamePlace;
    }

    private static IReadOnlyList<IReadOnlyList<long>> JoinRevisits(List<List<SiteInput>> stretches)
    {
        var places = new List<List<SiteInput>>();
        foreach (var stretch in stretches)
        {
            // Compare with places before the previous stretch (the previous one was just left on purpose).
            var earlier = places.Take(Math.Max(0, places.Count - 1))
                .FirstOrDefault(p => LooksAlike(p, stretch));
            if (earlier is not null) earlier.AddRange(stretch);
            else places.Add(stretch);
        }
        return places.Select(p => (IReadOnlyList<long>)p.OrderBy(v => v.Start).Select(v => v.VisitId).ToList()).ToList();
    }

    /// <summary>Two stretches are the same place if most of one's visits match the other.</summary>
    private static bool LooksAlike(List<SiteInput> a, List<SiteInput> b)
    {
        var checkable = b.Where(v => v.Scene is not null && a.Any(x => x.Scene?.IsNight == v.Scene.IsNight)).ToList();
        if (checkable.Count == 0) return false;
        var matching = checkable.Count(v => Matches(v, a) == true);
        return matching * 2 > checkable.Count;
    }

    private static List<SiteInput> Add(List<List<SiteInput>> stretches)
    {
        var list = new List<SiteInput>();
        stretches.Add(list);
        return list;
    }
}
