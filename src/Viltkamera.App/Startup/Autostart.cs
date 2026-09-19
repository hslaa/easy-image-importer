using Microsoft.Win32;

namespace Viltkamera.App.Startup;

/// <summary>
/// Starts the app (hidden, in the tray) when the user logs in to Windows, so a card is picked up
/// without him having to open anything. Written by the installer hooks, removed on uninstall.
/// </summary>
internal static class Autostart
{
    public const string TrayArgument = "--tray";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ViltkameraImport";

    public static void Enable()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        // Velopack keeps the app in a stable "current" folder across updates, so this path stays valid.
        key.SetValue(ValueName, $"\"{exe}\" {TrayArgument}");
    }

    public static void Disable()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
