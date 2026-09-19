using System.Globalization;
using Microsoft.Data.Sqlite;
using Viltkamera.Core.Storage;

namespace Viltkamera.Core.Import;

public sealed record ScannedFile(string RelPath, long Size, DateTime MtimeUtc);

/// <summary>All SQL for import sessions lives here. Every state change is a single transaction.</summary>
public sealed class ImportStore(Database db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private const string FileColumns =
        "id, session_id, rel_path, size, mtime_utc, sha256, status, attempts, staging_name, last_error";

    // ---- Sessions -------------------------------------------------------------------------

    public Session CreateSession(string fingerprint, string? label, string sourceRoot, IReadOnlyList<ScannedFile> files)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        var id = (long)Scalar(c, tx,
            """
            INSERT INTO sessions(card_fingerprint, card_label, source_root, state, created_utc, updated_utc)
            VALUES ($fp, $label, $root, $state, $now, $now) RETURNING id;
            """,
            ("$fp", fingerprint), ("$label", label), ("$root", sourceRoot),
            ("$state", SessionState.Copying.ToString()), ("$now", now))!;

        using var insert = c.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO session_files(session_id, rel_path, size, mtime_utc, status) VALUES ($s, $p, $size, $m, $status);";
        var pS = insert.Parameters.Add("$s", SqliteType.Integer);
        var pP = insert.Parameters.Add("$p", SqliteType.Text);
        var pSize = insert.Parameters.Add("$size", SqliteType.Integer);
        var pM = insert.Parameters.Add("$m", SqliteType.Text);
        insert.Parameters.AddWithValue("$status", FileStatus.Pending.ToString());
        foreach (var f in files)
        {
            pS.Value = id;
            pP.Value = f.RelPath;
            pSize.Value = f.Size;
            pM.Value = Format(f.MtimeUtc);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        return GetSession(id);
    }

    public Session GetSession(long id) =>
        QuerySessions("WHERE id = $id", ("$id", id)).Single();

    /// <summary>The newest session for this card that still has work to do (copy, import or erase).</summary>
    public Session? FindUnfinishedSession(string fingerprint) =>
        QuerySessions("WHERE card_fingerprint = $fp AND state <> $done ORDER BY id DESC LIMIT 1",
            ("$fp", fingerprint), ("$done", SessionState.CardErased.ToString())).FirstOrDefault();

    public IReadOnlyList<Session> GetUnfinishedSessions() =>
        QuerySessions("WHERE state <> $done ORDER BY id", ("$done", SessionState.CardErased.ToString()));

    public void SetState(long sessionId, SessionState state) =>
        Execute("UPDATE sessions SET state = $state, updated_utc = $now WHERE id = $id;",
            ("$state", state.ToString()), ("$now", Now()), ("$id", sessionId));

    public void UpdateSourceRoot(long sessionId, string sourceRoot) =>
        Execute("UPDATE sessions SET source_root = $root, updated_utc = $now WHERE id = $id;",
            ("$root", sourceRoot), ("$now", Now()), ("$id", sessionId));

    // ---- Files ----------------------------------------------------------------------------

    public IReadOnlyList<SessionFile> GetFiles(long sessionId, params FileStatus[] statuses)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {FileColumns} FROM session_files WHERE session_id = $s";
        cmd.Parameters.AddWithValue("$s", sessionId);
        if (statuses.Length > 0)
        {
            var names = statuses.Select((st, i) => $"$st{i}").ToArray();
            cmd.CommandText += $" AND status IN ({string.Join(", ", names)})";
            for (var i = 0; i < statuses.Length; i++) cmd.Parameters.AddWithValue(names[i], statuses[i].ToString());
        }
        cmd.CommandText += " ORDER BY id;";

        var result = new List<SessionFile>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(ReadFile(r));
        return result;
    }

    public FileCounts GetCounts(long sessionId)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT status, COUNT(*) FROM session_files WHERE session_id = $s GROUP BY status;";
        cmd.Parameters.AddWithValue("$s", sessionId);
        var counts = new Dictionary<FileStatus, int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) counts[Enum.Parse<FileStatus>(r.GetString(0))] = r.GetInt32(1);
        int Get(FileStatus s) => counts.GetValueOrDefault(s);
        return new FileCounts(counts.Values.Sum(), Get(FileStatus.Pending), Get(FileStatus.Verified),
            Get(FileStatus.Duplicate), Get(FileStatus.Failed), Get(FileStatus.Erased));
    }

    public void MarkVerified(long fileId, string sha256, string stagingName, int attempts) =>
        SetFile(fileId, FileStatus.Verified, sha256, stagingName, attempts, null);

    public void MarkDuplicate(long fileId, string sha256, int attempts) =>
        SetFile(fileId, FileStatus.Duplicate, sha256, null, attempts, null);

    public void MarkFailed(long fileId, int attempts, string error) =>
        Execute("UPDATE session_files SET status = $st, attempts = $a, last_error = $e WHERE id = $id;",
            ("$st", FileStatus.Failed.ToString()), ("$a", attempts), ("$e", error), ("$id", fileId));

    public void MarkErased(long fileId) =>
        Execute("UPDATE session_files SET status = $st WHERE id = $id;",
            ("$st", FileStatus.Erased.ToString()), ("$id", fileId));

    /// <summary>"Prøv igjen": failed files go back to pending and the session back to copying.</summary>
    public void RetryFailed(long sessionId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx, "UPDATE session_files SET status = $p, attempts = 0 WHERE session_id = $s AND status = $f;",
            ("$p", FileStatus.Pending.ToString()), ("$s", sessionId), ("$f", FileStatus.Failed.ToString()));
        Execute(c, tx, "UPDATE sessions SET state = $state, updated_utc = $now WHERE id = $s;",
            ("$state", SessionState.Copying.ToString()), ("$now", Now()), ("$s", sessionId));
        tx.Commit();
    }

    /// <summary>True if this content is already archived, or already staged earlier in the same session.</summary>
    public bool IsAlreadyKept(string sha256, long sessionId) =>
        Scalar("""
               SELECT EXISTS(SELECT 1 FROM known_files WHERE sha256 = $h)
                   OR EXISTS(SELECT 1 FROM session_files WHERE sha256 = $h AND session_id = $s AND status = $v);
               """,
            ("$h", sha256), ("$s", sessionId), ("$v", FileStatus.Verified.ToString())) is 1L;

    public KnownFile? GetKnownFile(string sha256)
    {
        using var c = db.Open();
        using var cmd = Command(c, null, "SELECT sha256, size, import_id, final_path FROM known_files WHERE sha256 = $h;",
            ("$h", sha256));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new KnownFile(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3)) : null;
    }

    // ---- Imports --------------------------------------------------------------------------

    /// <summary>Records the full move plan and flips the session to Finalizing in one transaction.</summary>
    public ImportRecord CreateImportPlan(long sessionId, string folderPath, IReadOnlyList<(long FileId, string From, string To)> moves)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var importId = (long)Scalar(c, tx,
            "INSERT INTO imports(session_id, folder_path, created_utc, image_count) VALUES ($s, $f, $now, $n) RETURNING id;",
            ("$s", sessionId), ("$f", folderPath), ("$now", Now()), ("$n", moves.Count))!;

        foreach (var (fileId, from, to) in moves)
            Execute(c, tx, "INSERT INTO import_moves(import_id, file_id, from_path, to_path) VALUES ($i, $f, $from, $to);",
                ("$i", importId), ("$f", fileId), ("$from", from), ("$to", to));

        Execute(c, tx, "UPDATE sessions SET state = $state, updated_utc = $now WHERE id = $s;",
            ("$state", SessionState.Finalizing.ToString()), ("$now", Now()), ("$s", sessionId));
        tx.Commit();
        return GetImportForSession(sessionId)!;
    }

    public ImportRecord? GetImportForSession(long sessionId) =>
        QueryImports("WHERE session_id = $s", ("$s", sessionId)).FirstOrDefault();

    public IReadOnlyList<ImportRecord> GetImports() =>
        QueryImports("WHERE undone_utc IS NULL ORDER BY created_utc DESC");

    public IReadOnlyList<ImportMove> GetMoves(long importId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            "SELECT import_id, file_id, from_path, to_path, done FROM import_moves WHERE import_id = $i ORDER BY file_id;",
            ("$i", importId));
        var result = new List<ImportMove>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new ImportMove(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0));
        return result;
    }

    public void MarkMoveDone(long importId, long fileId) =>
        Execute("UPDATE import_moves SET done = 1 WHERE import_id = $i AND file_id = $f;", ("$i", importId), ("$f", fileId));

    /// <summary>Registers every moved file as known and marks the session Imported, atomically.</summary>
    public void CompleteImport(long importId, long sessionId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx,
            """
            INSERT OR IGNORE INTO known_files(sha256, size, import_id, final_path)
            SELECT f.sha256, f.size, m.import_id, m.to_path
            FROM import_moves m JOIN session_files f ON f.id = m.file_id
            WHERE m.import_id = $i;
            """,
            ("$i", importId));
        Execute(c, tx, "UPDATE sessions SET state = $state, updated_utc = $now WHERE id = $s;",
            ("$state", SessionState.Imported.ToString()), ("$now", Now()), ("$s", sessionId));
        tx.Commit();
    }

    // ---- Helpers --------------------------------------------------------------------------

    private void SetFile(long fileId, FileStatus status, string sha256, string? stagingName, int attempts, string? error) =>
        Execute("""
                UPDATE session_files
                SET status = $st, sha256 = $h, staging_name = $n, attempts = $a, last_error = $e
                WHERE id = $id;
                """,
            ("$st", status.ToString()), ("$h", sha256), ("$n", stagingName), ("$a", attempts), ("$e", error), ("$id", fileId));

    private IReadOnlyList<Session> QuerySessions(string where, params (string, object?)[] args)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            $"SELECT id, card_fingerprint, card_label, source_root, state, created_utc FROM sessions {where};", args);
        var result = new List<Session>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new Session(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                Enum.Parse<SessionState>(r.GetString(4)), Parse(r.GetString(5))));
        return result;
    }

    private IReadOnlyList<ImportRecord> QueryImports(string where, params (string, object?)[] args)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            $"SELECT id, session_id, folder_path, created_utc, image_count, undone_utc FROM imports {where};", args);
        var result = new List<ImportRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new ImportRecord(r.GetInt64(0), r.GetInt64(1), r.GetString(2), Parse(r.GetString(3)), r.GetInt32(4),
                r.IsDBNull(5) ? null : Parse(r.GetString(5))));
        return result;
    }

    private static SessionFile ReadFile(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), Parse(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5), Enum.Parse<FileStatus>(r.GetString(6)), r.GetInt32(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9));

    private void Execute(string sql, params (string, object?)[] args)
    {
        using var c = db.Open();
        Execute(c, null, sql, args);
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var c = db.Open();
        return Scalar(c, null, sql, args);
    }

    private static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteScalar();
    }

    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private string Now() => Format(_time.GetUtcNow().UtcDateTime);
    private static string Format(DateTime utc) => utc.ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Parse(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
