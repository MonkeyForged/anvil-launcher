using Anvil.App.ViewModels;
using Avalonia;
using Avalonia.Controls;

namespace Anvil.App;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Fits the start size to the primary screen and centres the framed window on it (story s15.6).
    /// Called once before the window is first shown: a window placed by Anvil keeps its place under
    /// KDE, while one left unplaced lands on the screen with the mouse. Fitting after opening does
    /// not work either — at Opened the window still stands at 0,0, on whichever screen is there
    /// (both measured on two screens, s15.6). Without a primary screen nothing changes.
    /// </summary>
    public void FitToPrimaryScreen()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var space = StartWindowSize.ContentSpace(screen.WorkingArea, screen.Scaling);
        var size = StartWindowSize.Fit(new Size(Width, Height), new Size(MinWidth, MinHeight), space);
        Width = size.Width;
        Height = size.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = StartWindowSize.CentredPosition(screen.WorkingArea, screen.Scaling, size);
    }
}
