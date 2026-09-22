namespace EasyImageImporter.Core.Import;

/// <param name="dataRoot">App-private data, e.g. %LOCALAPPDATA%\EasyImageImporterData. Holds the database and staging.</param>
/// <param name="archiveRoot">Where finished imports go, e.g. ...\Pictures\Viltkamera.</param>
public sealed class AppPaths(string dataRoot, string archiveRoot)
{
    public string DataRoot { get; } = dataRoot;

    /// <summary>
    /// Where the next import is saved. The user can change it in settings; photos already saved
    /// stay where they are, and "Mine importer" still opens them by their own stored path.
    /// </summary>
    public string ArchiveRoot { get; set; } = archiveRoot;

    public string DatabasePath => Path.Combine(DataRoot, "easyimageimporter.db");
    public string StagingRoot => Path.Combine(DataRoot, "staging");
    public string StagingDir(long sessionId) => Path.Combine(StagingRoot, sessionId.ToString());
}
