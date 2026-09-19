using System.Diagnostics;
using Viltkamera.Core.Import;

namespace Viltkamera.App;

/// <summary>The few things that differ between the user's Windows PC and the Mac it is developed on.</summary>
internal static class Platform
{
    /// <summary>
    /// App data under %LOCALAPPDATA%\Viltkamera, archive under Pictures\Viltkamera.
    /// VILTKAMERA_DATA / VILTKAMERA_ARCHIVE override both, so development runs don't touch the real Pictures folder.
    /// </summary>
    public static AppPaths Paths()
    {
        var data = Environment.GetEnvironmentVariable("VILTKAMERA_DATA")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Viltkamera");
        var archive = Environment.GetEnvironmentVariable("VILTKAMERA_ARCHIVE")
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Viltkamera");
        Directory.CreateDirectory(data);
        return new AppPaths(data, archive);
    }

    public static void OpenFolder(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start("open", [path]);
        else
            Process.Start("xdg-open", [path]);
    }
}
