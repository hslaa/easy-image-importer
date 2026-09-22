using System.Diagnostics;
using EasyImageImporter.Core.Import;

namespace EasyImageImporter.App;

/// <summary>The few things that differ between the user's Windows PC and the Mac it is developed on.</summary>
internal static class Platform
{
    /// <summary>
    /// App data under %LOCALAPPDATA%\EasyImageImporterData, archive under Pictures\Viltkamera.
    /// The data folder must differ from the install folder (%LOCALAPPDATA%\EasyImageImporter):
    /// uninstalling deletes the install folder, and staging holds the only verified copies.
    /// EASYIMAGEIMPORTER_DATA / EASYIMAGEIMPORTER_ARCHIVE override both, so development runs don't touch the real Pictures folder.
    /// </summary>
    public static AppPaths Paths()
    {
        var data = Environment.GetEnvironmentVariable("EASYIMAGEIMPORTER_DATA")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyImageImporterData");
        Directory.CreateDirectory(data);
        return new AppPaths(data, ArchiveOverride ?? DefaultArchive);
    }

    /// <summary>A development run's archive folder; when set, the setting is ignored.</summary>
    public static string? ArchiveOverride => Environment.GetEnvironmentVariable("EASYIMAGEIMPORTER_ARCHIVE");

    public static string DefaultArchive =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Viltkamera");

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
