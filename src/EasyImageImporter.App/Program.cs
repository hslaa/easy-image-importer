using System.Globalization;
using Avalonia;
using Serilog;
using Velopack;
using EasyImageImporter.App.Startup;

namespace EasyImageImporter.App;

internal sealed class Program
{
    /// <summary>True when started by Windows at login: stay in the tray until a card shows up.</summary>
    public static bool StartedInTray { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles install/update/uninstall hooks, and exits the process during them.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopack
                .OnAfterInstallFastCallback(_ => Autostart.Enable())
                .OnAfterUpdateFastCallback(_ => Autostart.Enable())
                .OnBeforeUninstallFastCallback(_ => Autostart.Disable());
        }
        velopack.Run();

        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;

        var paths = Platform.Paths();
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(paths.DataRoot, "logs", "easyimageimporter-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60)
            .CreateLogger();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error(e.Exception, "Unobserved task exception");
        Log.Information("Starting {Version}", typeof(Program).Assembly.GetName().Version);

        // Norwegian number and date formatting everywhere ("1 247 bilder").
        var norwegian = CultureInfo.GetCultureInfo("nb-NO");
        CultureInfo.DefaultThreadCurrentCulture = norwegian;
        CultureInfo.DefaultThreadCurrentUICulture = norwegian;

        StartedInTray = args.Contains(Autostart.TrayArgument);
        App.SingleInstance = instance;

        using var stopUpdates = new CancellationTokenSource();
        Updater.Start(stopUpdates.Token);
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, lifetime =>
                lifetime.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        finally
        {
            stopUpdates.Cancel();
            Log.Information("Stopped");
            Log.CloseAndFlush();
        }
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
