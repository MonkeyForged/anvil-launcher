using System.Collections.ObjectModel;
using System.ComponentModel;
using Anvil.Core.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The running log, live on the ring buffer (spec S1.6 §3). The view model mirrors the buffer —
/// at most <see cref="LogRingBuffer.Capacity"/> entries — and filters it for display by search,
/// level and module. New entries reach the UI thread through the dispatcher handed in; no
/// Avalonia type here.
/// </summary>
public sealed partial class LogViewModel : PageViewModel
{
    public const string AllLevels = "All";
    public const string AllModules = "All";

    /// <summary>The mockup's six level filters, in order.</summary>
    public static readonly IReadOnlyList<string> LevelFilters =
        ["All", "Trace", "Debug", "Info", "Warning", "Error"];

    private readonly LogRingBuffer _buffer;
    private readonly string _logDirectory;
    private readonly Action<Action> _dispatch;
    private readonly Action<string> _openFolder;
    private readonly ILogger? _logger;
    private readonly List<LogEntry> _all = [];
    private EmptyState? _emptyState;

    public LogViewModel(
        LogRingBuffer buffer, string logDirectory, Action<Action> dispatch,
        Action<string> openFolder, ILogger? logger = null)
    {
        _buffer = buffer;
        _logDirectory = logDirectory;
        _dispatch = dispatch;
        _openFolder = openFolder;
        _logger = logger;

        // Subscribe first, then take the snapshot: this way no entry is lost. An entry from the
        // gap in between stands in the snapshot and comes once more through the event — Accept
        // recognises it by reference and drops it.
        _buffer.EntryAdded += OnEntryAdded;
        _all.AddRange(_buffer.Snapshot());
        Refresh();
    }

    public override string Title => "Log";

    public override string Subtitle => string.Empty;

    public override EmptyState? EmptyState => _emptyState;

    /// <summary>The visible lines — filtered, in buffer order.</summary>
    public ObservableCollection<LogLine> Entries { get; } = [];

    /// <summary>"All" and every category that occurs in the buffer, alphabetically.</summary>
    public ObservableCollection<string> Modules { get; } = [AllModules];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = AllLevels;

    [ObservableProperty]
    private string _selectedModule = AllModules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FollowLabel))]
    private bool _isFollowing = true;

    public string FollowLabel => IsFollowing ? "Follow: on" : "Follow: off";

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedLevelChanged(string value) => Refresh();

    partial void OnSelectedModuleChanged(string value)
    {
        if (value is null)
        {
            // The ComboBox reports null when its list changes — that means "All".
            SelectedModule = AllModules;
            return;
        }

        Refresh();
    }

    /// <summary>Empties buffer and view. Deliberately without a log line (spec E5).</summary>
    [RelayCommand]
    private void Clear()
    {
        _buffer.Clear();
        _all.Clear();
        SelectedModule = AllModules;
        for (var index = Modules.Count - 1; index > 0; index--)
        {
            Modules.RemoveAt(index);
        }

        Refresh();
    }

    [RelayCommand]
    private void ShowAllLevels() => SelectedLevel = AllLevels;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedLevel = AllLevels;
        SelectedModule = AllModules;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            _openFolder(_logDirectory);
            _logger?.LogDebug("log folder opened: {Path}", _logDirectory);
        }
        catch (Exception exception)
            when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            _logger?.LogWarning(exception, "could not open log folder: {Path}", _logDirectory);
        }
    }

    private void OnEntryAdded(LogEntry entry) => _dispatch(() => Accept(entry));

    private void Accept(LogEntry entry)
    {
        if (_all.Exists(known => ReferenceEquals(known, entry)))
        {
            return;
        }

        _all.Add(entry);
        if (_all.Count > _buffer.Capacity)
        {
            var oldest = _all[0];
            _all.RemoveAt(0);
            if (Entries.Count > 0 && ReferenceEquals(Entries[0].Source, oldest))
            {
                Entries.RemoveAt(0);
            }
        }

        AddModule(entry.Category);
        if (Matches(entry))
        {
            Entries.Add(new LogLine(entry));
        }

        UpdateEmptyState();
    }

    private void Refresh()
    {
        Entries.Clear();
        foreach (var entry in _all)
        {
            AddModule(entry.Category);
            if (Matches(entry))
            {
                Entries.Add(new LogLine(entry));
            }
        }

        UpdateEmptyState();
    }

    private void AddModule(string category)
    {
        if (Modules.Contains(category))
        {
            return;
        }

        var index = 1;
        while (index < Modules.Count && string.CompareOrdinal(Modules[index], category) < 0)
        {
            index++;
        }

        Modules.Insert(index, category);
    }

    private bool Matches(LogEntry entry)
        => MatchesLevel(entry.Level, SelectedLevel)
           && (SelectedModule == AllModules || entry.Category == SelectedModule)
           && MatchesSearch(entry, SearchText);

    /// <summary>Exactly this level, as in the mockup; Critical counts as Error (spec E4).</summary>
    private static bool MatchesLevel(LogLevel level, string filter) => filter switch
    {
        AllLevels => true,
        "Error" => level >= LogLevel.Error,
        _ => LogLine.LabelOf(level) == filter,
    };

    private static bool MatchesSearch(LogEntry entry, string query)
        => string.IsNullOrWhiteSpace(query)
           || entry.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
           || entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (entry.ExceptionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    private void UpdateEmptyState()
    {
        EmptyState? next;
        if (Entries.Count > 0)
        {
            next = null;
        }
        else if (_all.Count == 0)
        {
            next = new EmptyState(
                "IconLog", "No entries yet",
                "Anvil and its modules write here as soon as they do something.");
        }
        else if (string.IsNullOrWhiteSpace(SearchText) && SelectedModule == AllModules)
        {
            next = new EmptyState(
                "IconFilter", "Nothing at this level",
                $"No entry has the level {SelectedLevel}. The log itself is not empty.",
                "Show all levels", ShowAllLevelsCommand);
        }
        else
        {
            // Spec E2 / design requirement 8: the general "filtered out" case.
            next = new EmptyState(
                "IconFilter", "Nothing matches",
                "No entry matches the current search and filters. The log itself is not empty.",
                "Clear filters", ClearFiltersCommand);
        }

        if (!Equals(next, _emptyState))
        {
            _emptyState = next;
            OnPropertyChanged(nameof(EmptyState));
        }
    }
}
