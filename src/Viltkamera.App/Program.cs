using System.Globalization;
using Avalonia;

namespace Viltkamera.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Norwegian number and date formatting everywhere ("1 247 bilder").
        var norwegian = CultureInfo.GetCultureInfo("nb-NO");
        CultureInfo.DefaultThreadCurrentCulture = norwegian;
        CultureInfo.DefaultThreadCurrentUICulture = norwegian;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, lifetime =>
            lifetime.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
