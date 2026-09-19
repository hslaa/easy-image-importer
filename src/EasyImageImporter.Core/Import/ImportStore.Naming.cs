namespace EasyImageImporter.Core.Import;

/// <summary>What the user has told us about a place. Everything is optional until saving.</summary>
public sealed record PlaceDetails(
    long Id, string DefaultName, string? Title, string? Description, string? FolderName,
    string? RecognisedName, IReadOnlyList<string> Tags);

public sealed record StoredScene(long SequenceId, bool Night, byte[] Edges);

public sealed record KnownScene(long KnownSiteId, string Name, bool Night, byte[] Edges);

public sealed record ImportFolder(long ImportId, string FolderPath, int ImageCount, int DiscardedCount, long? PlaceId = null);

/// <summary>Naming, tags, remembered places and per-place folders.</summary>
public sealed partial class ImportStore
{
    /// <summary>Remembered scenes per place; older ones make way so a place can change with the seasons.</summary>
    private const int ScenesPerKnownSite = 24;

    public IReadOnlyDictionary<long, PlaceDetails> GetPlaceDetails(long sessionId)
    {
        using var c = db.Open();
        var tags = new Dictionary<long, List<string>>();
        using (var t = Command(c, null,
                   """
                   SELECT t.site_group_id, t.text FROM site_group_tags t
                   JOIN site_groups g ON g.id = t.site_group_id WHERE g.session_id = $s ORDER BY t.rowid;
                   """, ("$s", sessionId)))
        using (var r = t.ExecuteReader())
            while (r.Read())
            {
                if (!tags.TryGetValue(r.GetInt64(0), out var list)) tags[r.GetInt64(0)] = list = [];
                list.Add(r.GetString(1));
            }

        using var cmd = Command(c, null,
            """
            SELECT g.id, g.name, g.title, g.description, g.folder_name, k.name
            FROM site_groups g LEFT JOIN known_sites k ON k.id = g.recognised_site_id
            WHERE g.session_id = $s;
            """, ("$s", sessionId));
        var result = new Dictionary<long, PlaceDetails>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            var id = rd.GetInt64(0);
            result[id] = new PlaceDetails(id, rd.GetString(1), Text(rd, 2), Text(rd, 3), Text(rd, 4), Text(rd, 5),
                tags.GetValueOrDefault(id) ?? []);
        }
        return result;
    }

    public void SetPlaceTitle(long placeId, string? title) =>
        Execute("UPDATE site_groups SET title = $t WHERE id = $id;", ("$t", Blank(title)), ("$id", placeId));

    public void SetPlaceDescription(long placeId, string? description) =>
        Execute("UPDATE site_groups SET description = $d WHERE id = $id;", ("$d", Blank(description)), ("$id", placeId));

    /// <summary>Null or empty goes back to the suggested folder name.</summary>
    public void SetPlaceFolderName(long placeId, string? folderName) =>
        Execute("UPDATE site_groups SET folder_name = $f WHERE id = $id;", ("$f", Blank(folderName)), ("$id", placeId));

    public void SetPlaceTags(long placeId, IEnumerable<string> tags)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx, "DELETE FROM site_group_tags WHERE site_group_id = $id;", ("$id", placeId));
        foreach (var tag in tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Execute(c, tx, "INSERT INTO site_group_tags(site_group_id, text) VALUES ($id, $t);", ("$id", placeId), ("$t", tag));
            Execute(c, tx, "INSERT OR IGNORE INTO vocabulary(text, kind) VALUES ($t, 'tag');", ("$t", tag));
        }
        tx.Commit();
    }

    /// <summary>The animal in a visit ("Kongeørn"), or null. Remembered for autocomplete.</summary>
    public void SetSequenceLabel(long sequenceId, string? label)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx, "UPDATE sequences SET label = $l WHERE id = $id;", ("$l", Blank(label)), ("$id", sequenceId));
        if (Blank(label) is string text)
            Execute(c, tx, "INSERT OR IGNORE INTO vocabulary(text, kind) VALUES ($t, 'species');", ("$t", text));
        tx.Commit();
    }

    public IReadOnlyList<string> GetVocabulary(string kind)
    {
        using var c = db.Open();
        using var cmd = Command(c, null, "SELECT text FROM vocabulary WHERE kind = $k ORDER BY text;", ("$k", kind));
        var result = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(r.GetString(0));
        return result;
    }

    // ---- Scenes and remembered places -----------------------------------------------------

    public void SaveVisitScenes(IEnumerable<StoredScene> scenes)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var s in scenes)
            Execute(c, tx, "INSERT OR REPLACE INTO visit_scenes(sequence_id, night, edges) VALUES ($q, $n, $e);",
                ("$q", s.SequenceId), ("$n", s.Night ? 1 : 0), ("$e", s.Edges));
        tx.Commit();
    }

    public IReadOnlyList<StoredScene> GetVisitScenes(long sessionId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            """
            SELECT v.sequence_id, v.night, v.edges FROM visit_scenes v
            JOIN sequences q ON q.id = v.sequence_id WHERE q.session_id = $s;
            """, ("$s", sessionId));
        var result = new List<StoredScene>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new StoredScene(r.GetInt64(0), r.GetInt64(1) != 0, (byte[])r[2]));
        return result;
    }

    public IReadOnlyList<KnownScene> GetKnownScenes()
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            "SELECT s.known_site_id, k.name, s.night, s.edges FROM known_site_scenes s JOIN known_sites k ON k.id = s.known_site_id;");
        var result = new List<KnownScene>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(new KnownScene(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, (byte[])r[3]));
        return result;
    }

    /// <summary>"Dette ser ut som Høgfjellåsen": pre-fills the name unless the user already typed one.</summary>
    public void SetRecognised(long placeId, long knownSiteId) =>
        Execute("""
                UPDATE site_groups SET recognised_site_id = $k,
                    title = COALESCE(title, (SELECT name FROM known_sites WHERE id = $k))
                WHERE id = $id;
                """, ("$k", knownSiteId), ("$id", placeId));

    /// <summary>
    /// Remembers a saved place under its name, with some of its visit scenes, so the next card from
    /// the same spot is recognised. Safe to call again for the same visits.
    /// </summary>
    public void RememberPlace(string name, IEnumerable<StoredScene> scenes)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var now = Now();
        Execute(c, tx,
            """
            INSERT INTO known_sites(name, created_utc, last_used_utc) VALUES ($n, $now, $now)
            ON CONFLICT(name COLLATE NOCASE) DO UPDATE SET last_used_utc = $now;
            """, ("$n", name), ("$now", now));
        var siteId = (long)Scalar(c, tx, "SELECT id FROM known_sites WHERE name = $n COLLATE NOCASE;", ("$n", name))!;
        foreach (var s in scenes)
            Execute(c, tx,
                """
                INSERT INTO known_site_scenes(known_site_id, source_sequence_id, night, edges, added_utc)
                VALUES ($k, $q, $n, $e, $now)
                ON CONFLICT(source_sequence_id) DO UPDATE SET known_site_id = $k;
                """, ("$k", siteId), ("$q", s.SequenceId), ("$n", s.Night ? 1 : 0), ("$e", s.Edges), ("$now", now));
        Execute(c, tx,
            """
            DELETE FROM known_site_scenes WHERE known_site_id = $k AND id NOT IN (
                SELECT id FROM known_site_scenes WHERE known_site_id = $k ORDER BY added_utc DESC, id DESC LIMIT $max);
            """, ("$k", siteId), ("$max", ScenesPerKnownSite));
        tx.Commit();
    }

    /// <summary>After an undo, the session's visits no longer teach anything about their places.</summary>
    public void ForgetSessionScenes(long sessionId)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        Execute(c, tx,
            "DELETE FROM known_site_scenes WHERE source_sequence_id IN (SELECT id FROM sequences WHERE session_id = $s);",
            ("$s", sessionId));
        Execute(c, tx,
            """
            DELETE FROM known_sites WHERE id NOT IN (SELECT known_site_id FROM known_site_scenes)
              AND id NOT IN (SELECT recognised_site_id FROM site_groups WHERE recognised_site_id IS NOT NULL);
            """);
        tx.Commit();
    }

    // ---- Folders of an import -------------------------------------------------------------

    public void AddImportFolders(long importId, IEnumerable<ImportFolder> folders)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        foreach (var f in folders)
            Execute(c, tx,
                """
                INSERT OR REPLACE INTO import_folders(import_id, folder_path, site_group_id, image_count, discarded_count)
                VALUES ($i, $p, $g, $n, $d);
                """, ("$i", importId), ("$p", f.FolderPath), ("$g", f.PlaceId), ("$n", f.ImageCount), ("$d", f.DiscardedCount));
        tx.Commit();
    }

    public IReadOnlyList<ImportFolder> GetImportFolders(long importId)
    {
        using var c = db.Open();
        using var cmd = Command(c, null,
            "SELECT import_id, folder_path, image_count, discarded_count, site_group_id FROM import_folders WHERE import_id = $i ORDER BY rowid;",
            ("$i", importId));
        var result = new List<ImportFolder>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new ImportFolder(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt64(4)));
        return result;
    }

    private static string? Text(Microsoft.Data.Sqlite.SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
