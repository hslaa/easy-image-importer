using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Viltkamera.App.ViewModels;
using Viltkamera.App.Views;

namespace Viltkamera.App;

/// <summary>
/// Lives in the tray. Closing the window only hides it; card detection keeps running and the
/// window comes back by itself when a card is inserted.
/// </summary>
public partial class App : Application
{
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _viewModel = new MainViewModel();
            _window = new MainWindow { DataContext = _viewModel };
            _viewModel.AttentionNeeded += ShowWindow;
            desktop.MainWindow = _window;
            desktop.Exit += (_, _) => _viewModel.Dispose();
            _viewModel.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = Avalonia.Controls.WindowState.Normal;
        _window.Activate();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();
    private void OnOpenClicked(object? sender, EventArgs e) => ShowWindow();

    private void OnQuitClicked(object? sender, EventArgs e)
    {
        // Safe at any point: every step resumes from the database on next start.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}
