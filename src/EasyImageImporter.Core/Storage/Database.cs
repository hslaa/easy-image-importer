using Microsoft.Data.Sqlite;

namespace EasyImageImporter.Core.Storage;

/// <summary>
/// Opens connections to the app's SQLite file and applies schema migrations.
/// Migrations are append-only: never edit one that has shipped, add a new one.
/// </summary>
public sealed class Database
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE sessions(
            id               INTEGER PRIMARY KEY,
            card_fingerprint TEXT NOT NULL,
            card_label       TEXT,
            source_root      TEXT NOT NULL,
            state            TEXT NOT NULL,
            created_utc      TEXT NOT NULL,
            updated_utc      TEXT NOT NULL
        );
        CREATE INDEX ix_sessions_fingerprint ON sessions(card_fingerprint);

        CREATE TABLE session_files(
            id           INTEGER PRIMARY KEY,
            session_id   INTEGER NOT NULL REFERENCES sessions(id),
            rel_path     TEXT NOT NULL,
            size         INTEGER NOT NULL,
            mtime_utc    TEXT NOT NULL,
            sha256       TEXT,
            status       TEXT NOT NULL,
            attempts     INTEGER NOT NULL DEFAULT 0,
            staging_name TEXT,
            last_error   TEXT,
            UNIQUE(session_id, rel_path)
        );
        CREATE INDEX ix_session_files_sha256 ON session_files(sha256);

        CREATE TABLE imports(
            id          INTEGER PRIMARY KEY,
            session_id  INTEGER NOT NULL UNIQUE REFERENCES sessions(id),
            folder_path TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            image_count INTEGER NOT NULL DEFAULT 0,
            undone_utc  TEXT
        );

        CREATE TABLE import_moves(
            import_id INTEGER NOT NULL REFERENCES imports(id),
            file_id   INTEGER NOT NULL REFERENCES session_files(id),
            from_path TEXT NOT NULL,
            to_path   TEXT NOT NULL,
            done      INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(import_id, file_id)
        );

        -- Every image ever archived, keyed by the hash of the original card file.
        CREATE TABLE known_files(
            sha256     TEXT PRIMARY KEY,
            size       INTEGER NOT NULL,
            import_id  INTEGER NOT NULL REFERENCES imports(id),
            final_path TEXT NOT NULL
        );
        """,
    ];

    private readonly string _connectionString;

    public Database(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        Migrate();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(read.ExecuteScalar());

        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            using var migrate = connection.CreateCommand();
            migrate.Transaction = tx;
            migrate.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
            migrate.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
