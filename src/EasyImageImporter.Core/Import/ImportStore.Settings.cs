namespace EasyImageImporter.Core.Import;

/// <summary>The few things the user can change, and the record of clearing out sorted-away photos.</summary>
public sealed partial class ImportStore
{
    public string? GetSetting(string key) =>
        Scalar("SELECT value FROM settings WHERE key = $k;", ("$k", key)) as string;

    public void SetSetting(string key, string? value)
    {
        if (value is null) Execute("DELETE FROM settings WHERE key = $k;", ("$k", key));
        else Execute("INSERT INTO settings(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v;",
            ("$k", key), ("$v", value));
    }

    /// <summary>Imports whose sorted-away photos are older than the limit and haven't been cleared yet.</summary>
    public IReadOnlyList<ImportRecord> GetImportsToClear(DateTime olderThanUtc) =>
        QueryImports("WHERE undone_utc IS NULL AND discarded_cleared_utc IS NULL AND created_utc < $t ORDER BY created_utc",
            ("$t", Format(olderThanUtc)));

    /// <summary>Imports that still have sorted-away photos on disk, whether or not they are old enough.</summary>
    public IReadOnlyList<ImportRecord> GetImportsWithDiscarded() =>
        QueryImports("WHERE undone_utc IS NULL AND discarded_cleared_utc IS NULL AND discarded_count > 0 ORDER BY created_utc");

    public void MarkDiscardedCleared(long importId) =>
        Execute("UPDATE imports SET discarded_cleared_utc = $t WHERE id = $i;", ("$t", Now()), ("$i", importId));
}
