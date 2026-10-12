using Anvil.App.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Anvil.App.Views;

public partial class SettingsView : UserControl
{
    private DependenciesViewModel? _dependencies;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Hängt sich an <see cref="DependenciesViewModel.RevealRequested"/> der aktuellen Seite —
    /// und löst sich von der vorigen, falls die Sicht einen neuen Datenkontext bekommt.
    /// </summary>
    private void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (_dependencies is not null)
        {
            _dependencies.RevealRequested -= OnRevealRequested;
        }

        _dependencies = (DataContext as SettingsViewModel)?.Dependencies;

        if (_dependencies is not null)
        {
            _dependencies.RevealRequested += OnRevealRequested;
        }
    }

    /// <summary>
    /// „Show in Dependencies" (Spec S3.5b §3.7): Das ViewModel hat die Karte schon aufgeklappt;
    /// das Rollen gehört der Sicht. Nachgereicht mit <see cref="DispatcherPriority.Loaded"/>, damit
    /// die aufgeklappte Karte schon gemessen ist — sonst rollte die Sicht zu ihrer zugeklappten
    /// Höhe.
    /// </summary>
    private void OnRevealRequested(object? sender, EventArgs args)
        => Dispatcher.UIThread.Post(() => DependenciesCard.BringIntoView(), DispatcherPriority.Loaded);
}
