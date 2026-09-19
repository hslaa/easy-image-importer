namespace Viltkamera.Core.Import;

/// <param name="DataRoot">App-private data, e.g. %LOCALAPPDATA%\Viltkamera. Holds the database and staging.</param>
/// <param name="ArchiveRoot">Where finished imports go, e.g. ...\Pictures\Viltkamera.</param>
public sealed record AppPaths(string DataRoot, string ArchiveRoot)
{
    public string DatabasePath => Path.Combine(DataRoot, "viltkamera.db");
    public string StagingRoot => Path.Combine(DataRoot, "staging");
    public string StagingDir(long sessionId) => Path.Combine(StagingRoot, sessionId.ToString());
}
