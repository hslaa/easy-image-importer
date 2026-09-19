using System.Globalization;

namespace EasyImageImporter.Core.Import;

/// <summary>What the models saw in one checked photo.</summary>
public sealed record FrameResult(
    long FileId, string? TopLabel, double TopConfidence, string? Box, string Prediction, double Score, string Name);

public enum SuggestionState { Open, Accepted, Dismissed }

/// <summary>Animal recognition results and what the user did with the suggestions.</summary>
public sealed partial class ImportStore
{
    public void SaveFrameResult(FrameResult r, string model) =>
        Execute("""
                INSERT OR REPLACE INTO frame_analyses(file_id, top_label, top_conf, box, prediction, prediction_score, name, model, analysed_utc)
                VALUES ($f, $l, $c, $b, $p, $s, $n, $m, $now);
                """,
            ("$f", r.FileId), ("$l", r.TopLabel), ("$c", r.TopConfidence), ("$b", r.Box), ("$p", r.Prediction),
            ("$s", r.Score), ("$n", r.Name), ("$m", model), ("$now", Now()));

    public IReadOnlyDictionary<long, FrameResult> GetFrameResults(long sessionId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            """
            SELECT a.file_id, a.top_label, a.top_conf, a.box, a.prediction, a.prediction_score, a.name
            FROM frame_analyses a JOIN session_files f ON f.id = a.file_id WHERE f.session_id = $s;
            """, ("$s", sessionId));
        var result = new Dictionary<long, FrameResult>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetInt64(0)] = new FrameResult(r.GetInt64(0), Text(r, 1), r.GetDouble(2), Text(r, 3), r.GetString(4),
                r.GetDouble(5), r.GetString(6));
        return result;
    }

    public IReadOnlyDictionary<long, SuggestionState> GetSuggestionStates(long sessionId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            "SELECT id, suggestion_state FROM sequences WHERE session_id = $s AND suggestion_state IS NOT NULL;", ("$s", sessionId));
        var result = new Dictionary<long, SuggestionState>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetInt64(0)] = Enum.Parse<SuggestionState>(r.GetString(1), ignoreCase: true);
        return result;
    }

    public void SetSuggestionState(long sequenceId, SuggestionState state) =>
        Execute("UPDATE sequences SET suggestion_state = $s WHERE id = $id;",
            ("$s", state == SuggestionState.Open ? null : state.ToString().ToLower(CultureInfo.InvariantCulture)), ("$id", sequenceId));
}
