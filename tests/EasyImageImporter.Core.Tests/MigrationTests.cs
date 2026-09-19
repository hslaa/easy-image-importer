using EasyImageImporter.Core.Import;
using EasyImageImporter.Core.Storage;
using Microsoft.Data.Sqlite;

namespace EasyImageImporter.Core.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "easyimageimporter-tests", Guid.NewGuid().ToString("N"));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Database_from_version_0_1_0_upgrades_and_keeps_its_imports()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "old.db");
        using (var c = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = Database.Migrations[0] + """
                PRAGMA user_version = 1;
                INSERT INTO sessions VALUES (1, 'fp', NULL, '/card', 'Imported', '2026-09-19T08:00:00.0000000Z', '2026-09-19T08:00:00.0000000Z');
                INSERT INTO session_files VALUES (1, 1, 'DCIM/A.JPG', 10, '2026-09-19T08:00:00.0000000Z', 'abc', 'Verified', 1, '1.jpg', NULL);
                INSERT INTO imports VALUES (1, 1, '/pics/2026-09-19 Import', '2026-09-19T08:00:00.0000000Z', 1, NULL);
                INSERT INTO import_moves VALUES (1, 1, '/staging/1.jpg', '/pics/2026-09-19 Import/A.JPG', 1);
                INSERT INTO known_files VALUES ('abc', 10, 1, '/pics/2026-09-19 Import/A.JPG');
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new ImportStore(new Database(path));

        var import = Assert.Single(store.GetImports());
        Assert.Equal("/pics/2026-09-19 Import", import.FolderPath);
        var move = Assert.Single(store.GetMoves(import.Id));
        Assert.False(move.Undone);
        Assert.NotNull(store.GetKnownFile("abc"));
    }
}
