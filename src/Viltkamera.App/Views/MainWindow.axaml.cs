using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Viltkamera.App.ViewModels;

namespace Viltkamera.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.PickFolder = PickFolderAsync;
        };
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
