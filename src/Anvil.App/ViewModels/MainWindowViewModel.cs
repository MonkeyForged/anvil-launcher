using Anvil.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The state of the shell: which page is shown, whether the navigation is collapsed and whether
/// About is open. The package's navigation takes its entries as texts and its selection as an
/// index (spec s14.1, decision 3), so <see cref="SelectedPageIndex"/> and
/// <see cref="CurrentPage"/> are two views of one selection and follow each other. The collapsed
/// state lives in <see cref="AppSection.SidebarExpanded"/> and is saved on every change (Spec S1.6
/// E8, TD-41) — through the one AppSettings instance of the composition root.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly ILogger? _logger;
    private readonly List<PageViewModel> _pages;

    public MainWindowViewModel(
        IEnumerable<PageViewModel> pages, AppSettings settings, SettingsStore store,
        SaveBarViewModel saveBar, AboutViewModel about, ILogger? logger = null)
    {
        _settings = settings;
        _store = store;
        SaveBar = saveBar;
        About = about;
        _logger = logger;
        _pages = pages.ToList();
        if (_pages.Count == 0)
        {
            throw new ArgumentException("at least one page is required", nameof(pages));
        }

        NavigationEntries = _pages.Select(page => page.Title).ToList();
        _currentPage = _pages[0];
        _isSidebarCollapsed = !settings.App.SidebarExpanded;
    }

    public IReadOnlyList<PageViewModel> Pages => _pages;

    /// <summary>The navigation's entries: the page titles in page order.</summary>
    public IReadOnlyList<string> NavigationEntries { get; }

    /// <summary>
    /// The save bar (element description §9). The window holds and shows it — <b>saving is
    /// global</b>, so it belongs to the shell and not to a page. The window has no state of its
    /// own here; everything stands in the bar itself.
    /// </summary>
    public SaveBarViewModel SaveBar { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>The navigation's selection: the index of <see cref="CurrentPage"/> in <see cref="Pages"/>.</summary>
    [ObservableProperty]
    private int _selectedPageIndex;

    /// <summary>Applies to all pages; starts from the settings, expanded by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSidebarExpanded))]
    private bool _isSidebarCollapsed;

    /// <summary>
    /// The inverse of <see cref="IsSidebarCollapsed"/>, as the settings store it. Not a second
    /// state: the navigation binds the collapsed form, and an inverted value does not bind back.
    /// </summary>
    public bool IsSidebarExpanded
    {
        get => !IsSidebarCollapsed;
        set => IsSidebarCollapsed = !value;
    }

    /// <summary>The content of the About window, which the navigation's foot opens.</summary>
    public AboutViewModel About { get; }

    private bool _isAboutOpen;

    /// <summary>Whether About is shown; the navigation's foot stays raised meanwhile.</summary>
    public bool IsAboutOpen
    {
        get => _isAboutOpen;
        private set => SetProperty(ref _isAboutOpen, value);
    }

    /// <summary>Opens About — a click on the navigation's foot.</summary>
    [RelayCommand]
    private void ShowAbout() => IsAboutOpen = true;

    /// <summary>Closes About — its CLOSE button or the frame's cross.</summary>
    [RelayCommand]
    private void CloseAbout() => IsAboutOpen = false;

    /// <summary>
    /// Shows the page of type <paramref name="pageType"/>, as a click on its navigation entry does —
    /// for a link from inside a page to another one (MANAGE MODULES in the right column, story
    /// s13.1). By type, because the page that links knows the other page's type, not its instance.
    /// Without such a page nothing changes.
    /// </summary>
    [RelayCommand]
    private void ShowPage(Type pageType)
    {
        if (Pages.FirstOrDefault(pageType.IsInstanceOfType) is { } page)
        {
            CurrentPage = page;
        }
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        SelectedPageIndex = _pages.IndexOf(value);
        _logger?.LogDebug("page shown: {Page}", value.Title);
        // E-15: the page reads anew when the user looks at it.
        value.OnShown();
    }

    partial void OnSelectedPageIndexChanged(int value)
    {
        // A list reports -1 when its selection is cleared; the page stays, and so does its entry.
        if (value < 0 || value >= Pages.Count)
        {
            SelectedPageIndex = _pages.IndexOf(CurrentPage);
            return;
        }

        CurrentPage = Pages[value];
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _settings.App.SidebarExpanded = !value;
        _store.Save(_settings);
    }
}
