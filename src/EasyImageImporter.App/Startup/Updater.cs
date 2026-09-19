using System.Reflection;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace EasyImageImporter.App.Startup;

/// <summary>
/// Downloads new versions quietly in the background. Velopack applies a downloaded update the
/// next time the app starts, so an update never lands in the middle of copying or erasing.
/// </summary>
internal static class Updater
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    /// <summary>GitHub repo with the releases, from the UpdateRepoUrl build property. Empty = updates off.</summary>
    private static string? RepoUrl => Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepoUrl")?.Value is { Length: > 0 } url ? url : null;

    public static void Start(CancellationToken ct) => _ = Task.Run(async () =>
    {
        if (RepoUrl is not { } url)
        {
            Log.Information("Automatic updates are off (no UpdateRepoUrl)");
            return;
        }

        var manager = new UpdateManager(new GithubSource(url, accessToken: null, prerelease: false));
        if (!manager.IsInstalled)
        {
            Log.Information("Not installed through Velopack (development run); skipping updates");
            return;
        }

        await Task.Delay(FirstCheckDelay, ct);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var update = await manager.CheckForUpdatesAsync();
                if (update is not null)
                {
                    Log.Information("Downloading update {Version}", update.TargetFullRelease.Version);
                    await manager.DownloadUpdatesAsync(update, cancelToken: ct);
                    Log.Information("Update {Version} will be applied on next start", update.TargetFullRelease.Version);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Offline, GitHub down, etc. Try again later; never bother the user about it.
                Log.Warning(ex, "Update check failed");
            }

            await Task.Delay(CheckInterval, ct);
        }
    }, ct);
}
