using Microsoft.Data.Sqlite;

namespace EasyImageImporter.Core.Import;

/// <summary>Review data: capture times, visits (sequences) and what to keep.</summary>
public sealed partial class ImportStore
{
    public void SetMediaInfo(IEnumerable<(long FileId, MediaKind Kind, DateTime? TakenAt, string? Source, string? Camera)> infos)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx,
            "UPDATE session_files SET media_kind = $k, taken_at = $t, taken_at_source = $s, camera = $c WHERE id = $id;",
            ("$k", ""), ("$t", null), ("$s", null), ("$c", null), ("$id", 0L));
        foreach (var info in infos)
        {
            cmd.Parameters["$k"].Value = info.Kind.ToString();
            cmd.Parameters["$t"].Value = info.TakenAt is { } t ? FormatLocal(t) : DBNull.Value;
            cmd.Parameters["$s"].Value = (object?)info.Source ?? DBNull.Value;
            cmd.Parameters["$c"].Value = (object?)info.Camera ?? DBNull.Value;
            cmd.Parameters["$id"].Value = info.FileId;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool HasSequences(long sessionId) =>
        Scalar("SELECT EXISTS(SELECT 1 FROM sequences WHERE session_id = $s);", ("$s", sessionId)) is 1L;

    public void CreateSequences(long sessionId, IEnumerable<IReadOnlyList<long>> groups)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var group in groups)
        {
            var id = (long)Scalar(c, tx, "INSERT INTO sequences(session_id) VALUES ($s) RETURNING id;", ("$s", sessionId))!;
            AssignToSequence(c, tx, id, group);
        }
        tx.Commit();
    }

    public void SetKeep(IEnumerable<long> fileIds, bool keep)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = Command(c, tx, "UPDATE session_files SET keep = $k WHERE id = $id;", ("$k", keep ? 1 : 0), ("$id", 0L));
        foreach (var id in fileIds)
        {
            cmd.Parameters["$id"].Value = id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Moves <paramref name="fileIds"/> into a new visit of the same session. Returns its id.</summary>
    public long SplitSequence(long sequenceId, IReadOnlyCollection<long> fileIds)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var id = (long)Scalar(c, tx,
            "INSERT INTO sequences(session_id) SELECT session_id FROM sequences WHERE id = $q RETURNING id;",
            ("$q", sequenceId))!;
        AssignToSequence(c, tx, id, fileIds);
        tx.Commit();
        return id;
    }

    /// <summary>Moves every image of <paramref name="removedId"/> into <paramref name="keptId"/> and drops the empty visit.</summary>
    public void MergeSequences(long keptId, long removedId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx, "UPDATE session_files SET sequence_id = $k WHERE sequence_id = $r;", ("$k", keptId), ("$r", removedId));
        Execute(c, tx, "UPDATE sequences SET label = COALESCE(label, (SELECT label FROM sequences WHERE id = $r)) WHERE id = $k;",
            ("$k", keptId), ("$r", removedId));
        Execute(c, tx, "DELETE FROM sequences WHERE id = $r;", ("$r", removedId));
        tx.Commit();
    }

    public IReadOnlyDictionary<long, string?> GetSequenceLabels(long sessionId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null, "SELECT id, label FROM sequences WHERE session_id = $s;", ("$s", sessionId));
        var result = new Dictionary<long, string?>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetInt64(0)] = r.IsDBNull(1) ? null : r.GetString(1);
        return result;
    }

    private static void AssignToSequence(SqliteConnection c, SqliteTransaction tx, long sequenceId, IEnumerable<long> fileIds)
    {
        using var cmd = Command(c, tx, "UPDATE session_files SET sequence_id = $q WHERE id = $id;", ("$q", sequenceId), ("$id", 0L));
        foreach (var fileId in fileIds)
        {
            cmd.Parameters["$id"].Value = fileId;
            cmd.ExecuteNonQuery();
        }
    }
}
