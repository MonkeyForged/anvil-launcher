using Anvil.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace Anvil.App.Views;

public partial class AboutView : UserControl
{
    public AboutView() => InitializeComponent();

    /// <summary>
    /// A click on the address opens the repository (spec s14.1, O-3; s15.10). The address is
    /// selectable text, and a selectable text takes the press for itself, so no button around it
    /// would see the click; a tap is a press without a drag, and a drag still selects.
    /// </summary>
    private void OpenSourceOnTap(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel shell)
        {
            shell.About.OpenSourceCommand.Execute(null);
        }
    }
}
