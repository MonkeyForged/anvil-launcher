using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.App.ViewModels;

/// <summary>
/// The shared blueprint of the four pages: the navigation entry, the page head and what to read
/// while the page is empty.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    /// <summary>Navigation entry and heading of the page head.</summary>
    public abstract string Title { get; }

    /// <summary>The line below the heading; empty where the concept has none.</summary>
    public abstract string Subtitle { get; }

    /// <summary>The page's empty state; <c>null</c> as soon as it has content.</summary>
    public abstract EmptyState? EmptyState { get; }

    /// <summary>
    /// The page is shown (Spec S3.5b E-15) — the moment the user looks and it should read anew.
    /// <c>MainWindowViewModel</c> calls it on every page change; an event in the code-behind
    /// would be out of reach of any test.
    /// </summary>
    public virtual void OnShown()
    {
    }
}
