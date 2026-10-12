using System.Collections.Specialized;
using System.ComponentModel;
using Anvil.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Anvil.App.Views;

/// <summary>
/// Reine Ansichtslogik: „Follow" hält die Liste am unteren Ende (Spec S1.6 §3.2). Das
/// ViewModel trägt nur den Schalter; das Scrollen kennt nur die View. Die Handler werden beim
/// Verlassen der Seite abgehängt, weil das ViewModel die Sitzung überlebt und die View nicht.
/// </summary>
public partial class LogView : UserControl
{
    private LogViewModel? _viewModel;

    public LogView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Unhook();
        base.OnDetachedFromVisualTree(e);
    }

    private void Hook()
    {
        Unhook();
        _viewModel = DataContext as LogViewModel;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Entries.CollectionChanged += OnEntriesChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        ScrollToEnd();
    }

    private void Unhook()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.IsFollowing == true)
        {
            ScrollToEnd();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.IsFollowing) && _viewModel?.IsFollowing == true)
        {
            ScrollToEnd();
        }
    }

    /// <summary>Nach dem Layout, sonst kennt der ScrollViewer die neue Höhe noch nicht.</summary>
    private void ScrollToEnd()
        => Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Background);
}
