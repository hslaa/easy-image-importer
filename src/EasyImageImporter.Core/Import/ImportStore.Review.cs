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

    /// <summary>Moves <paramref name="fileIds"/> into a new visit of the same session and place. Returns its id.</summary>
    public long SplitSequence(long sequenceId, IReadOnlyCollection<long> fileIds)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var id = (long)Scalar(c, tx,
            """
            INSERT INTO sequences(session_id, site_group_id)
            SELECT session_id, site_group_id FROM sequences WHERE id = $q RETURNING id;
            """,
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
        Execute(c, tx, "DELETE FROM visit_scenes WHERE sequence_id = $r;", ("$r", removedId));
        Execute(c, tx, "DELETE FROM sequences WHERE id = $r;", ("$r", removedId));
        tx.Commit();
    }

    public IReadOnlyDictionary<long, (string? Label, long? SiteId)> GetSequenceInfo(long sessionId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null, "SELECT id, label, site_group_id FROM sequences WHERE session_id = $s;", ("$s", sessionId));
        var result = new Dictionary<long, (string?, long?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetInt64(0)] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2));
        return result;
    }

    // ---- Places ---------------------------------------------------------------------------

    public bool HasSites(long sessionId) =>
        Scalar("SELECT EXISTS(SELECT 1 FROM site_groups WHERE session_id = $s);", ("$s", sessionId)) is 1L;

    /// <summary>Creates one place per group of visits, named "Sted 1", "Sted 2", … in the given order.</summary>
    public void CreateSites(long sessionId, IEnumerable<IReadOnlyList<long>> groups)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var n = 0;
        foreach (var group in groups)
        {
            var id = (long)Scalar(c, tx, "INSERT INTO site_groups(session_id, name) VALUES ($s, $n) RETURNING id;",
                ("$s", sessionId), ("$n", $"Sted {++n}"))!;
            AssignToSite(c, tx, id, group);
        }
        tx.Commit();
    }

    public IReadOnlyDictionary<long, string> GetSiteNames(long sessionId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null, "SELECT id, name FROM site_groups WHERE session_id = $s;", ("$s", sessionId));
        var result = new Dictionary<long, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result[r.GetInt64(0)] = r.GetString(1);
        return result;
    }

    /// <summary>Moves visits into a new place of the same session. Returns its id.</summary>
    public long SplitSite(long sessionId, IReadOnlyCollection<long> sequenceIds)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var count = (long)Scalar(c, tx, "SELECT COUNT(*) FROM site_groups WHERE session_id = $s;", ("$s", sessionId))!;
        var id = (long)Scalar(c, tx, "INSERT INTO site_groups(session_id, name) VALUES ($s, $n) RETURNING id;",
            ("$s", sessionId), ("$n", $"Sted {count + 1}"))!;
        AssignToSite(c, tx, id, sequenceIds);
        tx.Commit();
        return id;
    }

    public void MergeSites(long keptId, long removedId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx, "UPDATE sequences SET site_group_id = $k WHERE site_group_id = $r;", ("$k", keptId), ("$r", removedId));
        Execute(c, tx, "DELETE FROM site_groups WHERE id = $r;", ("$r", removedId));
        tx.Commit();
    }

    private static void AssignToSite(SqliteConnection c, SqliteTransaction tx, long siteId, IEnumerable<long> sequenceIds)
    {
        using var cmd = Command(c, tx, "UPDATE sequences SET site_group_id = $g WHERE id = $id;", ("$g", siteId), ("$id", 0L));
        foreach (var sequenceId in sequenceIds)
        {
            cmd.Parameters["$id"].Value = sequenceId;
            cmd.ExecuteNonQuery();
        }
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
