using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EasyImageImporter.App.ViewModels;

namespace EasyImageImporter.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.PickFolder = PickFolderAsync;
        };
        Opened += (_, _) => FitScreen();
    }

    /// <summary>
    /// On a small screen (an older 1366×768 laptop has room for about 700 px), the window would
    /// reach under the taskbar: fill the screen instead.
    /// </summary>
    private void FitScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) return;
        var room = screen.WorkingArea.Size.ToSize(screen.Scaling);
        if (room.Height < Height + 40 || room.Width < Width + 40) WindowState = WindowState.Maximized;
    }

    /// <summary>Closing only hides the window; the app keeps watching for cards from the tray.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private async Task<string?> PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Velg mappen med bildene fra viltkameraet",
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
