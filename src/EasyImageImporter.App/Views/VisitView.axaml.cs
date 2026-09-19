using Avalonia.Controls;

namespace EasyImageImporter.App.Views;

public partial class VisitView : UserControl
{
    public VisitView()
    {
        InitializeComponent();
        // Keyboard shortcuts need focus inside this view.
        AttachedToVisualTree += (_, _) => Focus();
    }
}
