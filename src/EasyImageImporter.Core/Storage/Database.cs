using Microsoft.Data.Sqlite;

namespace EasyImageImporter.Core.Storage;

/// <summary>
/// Opens connections to the app's SQLite file and applies schema migrations.
/// Migrations are append-only: never edit one that has shipped, add a new one.
/// </summary>
public sealed class Database
{
    internal static readonly string[] Migrations =
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
        // 2: an import can be undone and the session saved again, so a session may have several
        // imports (only one not undone). Rebuilds imports without UNIQUE(session_id).
        """
        CREATE TABLE imports_new(
            id          INTEGER PRIMARY KEY,
            session_id  INTEGER NOT NULL REFERENCES sessions(id),
            folder_path TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            image_count INTEGER NOT NULL DEFAULT 0,
            undone_utc  TEXT
        );
        INSERT INTO imports_new(id, session_id, folder_path, created_utc, image_count, undone_utc)
            SELECT id, session_id, folder_path, created_utc, image_count, undone_utc FROM imports;
        DROP TABLE imports;
        ALTER TABLE imports_new RENAME TO imports;
        CREATE INDEX ix_imports_session ON imports(session_id);

        ALTER TABLE import_moves ADD COLUMN undone INTEGER NOT NULL DEFAULT 0;
        """,
        // 3: review. Files get their capture time, the visit (sequence) they belong to, and a keep
        // flag. Everything is stored per click, so a review can be resumed days later.
        """
        CREATE TABLE sequences(
            id         INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            label      TEXT
        );
        CREATE INDEX ix_sequences_session ON sequences(session_id);

        ALTER TABLE session_files ADD COLUMN media_kind TEXT;
        ALTER TABLE session_files ADD COLUMN taken_at TEXT;          -- camera local time, no zone
        ALTER TABLE session_files ADD COLUMN taken_at_source TEXT;   -- exif | mtime
        ALTER TABLE session_files ADD COLUMN camera TEXT;
        ALTER TABLE session_files ADD COLUMN sequence_id INTEGER REFERENCES sequences(id);
        ALTER TABLE session_files ADD COLUMN keep INTEGER NOT NULL DEFAULT 1;
        CREATE INDEX ix_session_files_sequence ON session_files(sequence_id);

        ALTER TABLE imports ADD COLUMN discarded_count INTEGER NOT NULL DEFAULT 0;
        """,
        // 4: places ("steder"). Visits are grouped by camera placement, found from the background.
        """
        CREATE TABLE site_groups(
            id         INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id),
            name       TEXT NOT NULL
        );
        CREATE INDEX ix_site_groups_session ON site_groups(session_id);
        ALTER TABLE sequences ADD COLUMN site_group_id INTEGER REFERENCES site_groups(id);
        """,
        // 5: naming. Places get the user's name, description, tags and folder name; an import can
        // produce one folder per place; and named places are remembered between seasons by what
        // their background looks like, so the next card from the same spot is recognised.
        """
        CREATE TABLE known_sites(
            id            INTEGER PRIMARY KEY,
            name          TEXT NOT NULL,
            created_utc   TEXT NOT NULL,
            last_used_utc TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_known_sites_name ON known_sites(name COLLATE NOCASE);

        CREATE TABLE known_site_scenes(
            id                 INTEGER PRIMARY KEY,
            known_site_id      INTEGER NOT NULL REFERENCES known_sites(id),
            source_sequence_id INTEGER NOT NULL UNIQUE,
            night              INTEGER NOT NULL,
            edges              BLOB NOT NULL,
            added_utc          TEXT NOT NULL
        );
        CREATE INDEX ix_known_site_scenes_site ON known_site_scenes(known_site_id);

        -- The background of each visit, kept so places can be remembered without re-reading images.
        CREATE TABLE visit_scenes(
            sequence_id INTEGER PRIMARY KEY REFERENCES sequences(id),
            night       INTEGER NOT NULL,
            edges       BLOB NOT NULL
        );

        ALTER TABLE site_groups ADD COLUMN title TEXT;                 -- the user's name for the place
        ALTER TABLE site_groups ADD COLUMN description TEXT;
        ALTER TABLE site_groups ADD COLUMN folder_name TEXT;           -- null: use the suggestion
        ALTER TABLE site_groups ADD COLUMN recognised_site_id INTEGER REFERENCES known_sites(id);

        CREATE TABLE site_group_tags(
            site_group_id INTEGER NOT NULL REFERENCES site_groups(id),
            text          TEXT NOT NULL,
            PRIMARY KEY (site_group_id, text)
        );

        -- Everything ever typed, for autocomplete. kind: tag | species.
        CREATE TABLE vocabulary(
            text TEXT NOT NULL,
            kind TEXT NOT NULL,
            PRIMARY KEY (text, kind)
        );

        CREATE TABLE import_folders(
            import_id       INTEGER NOT NULL REFERENCES imports(id),
            folder_path     TEXT NOT NULL,
            site_group_id   INTEGER REFERENCES site_groups(id),
            image_count     INTEGER NOT NULL,
            discarded_count INTEGER NOT NULL,
            PRIMARY KEY (import_id, folder_path)
        );
        INSERT INTO import_folders(import_id, folder_path, image_count, discarded_count)
            SELECT id, folder_path, image_count, discarded_count FROM imports;
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
        // Foreign keys must be off while tables are rebuilt (SQLite's documented procedure),
        // and can only be switched outside a transaction. Checked again before committing.
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var off = connection.CreateCommand())
        {
            off.CommandText = "PRAGMA foreign_keys=OFF;";
            off.ExecuteNonQuery();
        }

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

            using var check = connection.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "PRAGMA foreign_key_check;";
            using (var problems = check.ExecuteReader())
            {
                if (problems.Read()) throw new InvalidOperationException($"Migration {i + 1} broke a foreign key.");
            }
            tx.Commit();
        }
    }
}
