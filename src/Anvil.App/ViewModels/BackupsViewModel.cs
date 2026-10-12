using System.Collections.ObjectModel;
using Anvil.Core.Backups;
using Anvil.Core.Sessions;
using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The backups list behind the button <c>Manage</c> of the Settings card (spec S4.7 §3.3).
///
/// <para>
/// <b>A view model of its own, not an extension of <see cref="SettingsViewModel"/></b> (E-F):
/// the overlay's content has dependencies of its own, and a test of the list should need
/// neither a settings file nor a folder dialog.
/// </para>
///
/// <para>
/// <b>The list is read, not watched</b> (E-H): on opening, after a delete and when the bar has
/// reported a run. Only Anvil itself writes into this folder.
/// </para>
///
/// <para>
/// <b>The progress belongs to the bar</b> (E-J). This view model never posts to the UI thread —
/// it hooks into <see cref="SaveBarViewModel.StateChanged"/>, where everything already runs on
/// the UI thread (S4.5 E-M, S4.6 E-G).
/// </para>
/// </summary>
public sealed partial class BackupsViewModel : ObservableObject
{
    private const string GameRunningNote = "Restoring is not possible right now — a game is running";

    private readonly BackupStore _backups;
    private readonly Func<SteamSnapshot> _snapshot;
    private readonly Func<
        string, string, DateTime, Action<SaveProgress>?, Task<TransactionResult>> _restore;
    private readonly GameSessionWatcher _watcher;
    private readonly SaveBarViewModel _saveBar;
    private readonly ILogger? _logger;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _lockNote = string.Empty;

    [ObservableProperty]
    private bool _hasLockNote;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking))]
    [NotifyPropertyChangedFor(nameof(AskTitle))]
    private BackupRowViewModel? _pendingRestore;

    /// <summary>The open detail popup, or <c>null</c> — the third layer (S4.8 §3.7).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowingDetails))]
    private BackupDetailsViewModel? _details;

    /// <param name="backups">The backup service (S4.1).</param>
    /// <param name="snapshot">
    /// The snapshot as a delegate, not a value (E-P): after a rescan the new one applies.
    /// </param>
    /// <param name="restore">
    /// Restoring — the root passes <c>SteamTransaction.RestoreAsync</c> as a lambda; the optional
    /// <c>progress</c> parameter makes the method group unusable (finding from S4.4). The
    /// timestamp is the backup's: its note names it (S4.8 §3.5).
    /// </param>
    /// <param name="watcher">The one truth about "is a game running".</param>
    /// <param name="saveBar">Shows the run and reports when one has ended.</param>
    /// <param name="logger">Category <c>Ui</c>; null means silent.</param>
    public BackupsViewModel(
        BackupStore backups,
        Func<SteamSnapshot> snapshot,
        Func<string, string, DateTime, Action<SaveProgress>?, Task<TransactionResult>> restore,
        GameSessionWatcher watcher,
        SaveBarViewModel saveBar,
        ILogger? logger = null)
    {
        _backups = backups;
        _snapshot = snapshot;
        _restore = restore;
        _watcher = watcher;
        _saveBar = saveBar;
        _logger = logger;

        // One seam for both: it reports on the UI thread as soon as the save state changes —
        // a run in progress as well as a game that has ended.
        _saveBar.StateChanged += (_, _) => UpdateLocks();
        _saveBar.Saved += (_, _) => Reload();
    }

    public ObservableCollection<BackupRowViewModel> Rows { get; } = [];

    /// <summary>
    /// The empty list, drawn by the shared empty-state template (element description §10). No
    /// action: there is nothing to create here, a save takes the backups.
    /// </summary>
    public EmptyState EmptyState { get; } = new(
        "IconBackup",
        "No backups yet",
        "Anvil copies a Steam file before every save.");

    /// <summary>The question's visibility — the view binds to this.</summary>
    public bool IsAsking => PendingRestore is not null;

    /// <summary>The question's title; empty without a question.</summary>
    public string AskTitle => PendingRestore is { } row
        ? $"Restore {row.FileName} from {row.Time}?"
        : string.Empty;

    /// <summary>The popup's visibility — the view binds to this.</summary>
    public bool IsShowingDetails => Details is not null;

    [RelayCommand]
    private void Open()
    {
        Reload();
        IsOpen = true;
        _logger?.LogInformation("backups opened: {Count} entries", Rows.Count);
    }

    [RelayCommand]
    private void Close()
    {
        PendingRestore = null;
        Details = null;
        IsOpen = false;
    }

    /// <summary>Asks first — the whole file goes back, and that deserves a question.</summary>
    [RelayCommand]
    private void Restore(BackupRowViewModel? row)
    {
        if (row is null || !row.CanRestore)
        {
            return;
        }

        PendingRestore = row;
    }

    [RelayCommand]
    private void KeepCurrent() => PendingRestore = null;

    /// <summary>
    /// Opens the full note. <b>Only where there is something to show</b> (E-O) — the guard sits
    /// here and not only in the view, so it can be tested.
    /// </summary>
    [RelayCommand]
    private void ShowDetails(BackupRowViewModel? row)
    {
        if (row is null || !row.CanShowDetails)
        {
            return;
        }

        Details = new BackupDetailsViewModel(row);
        _logger?.LogInformation(
            "backup details opened: {File} from {Time}", row.FileName, row.Time);
    }

    /// <summary>Closes the popup; the overlay below it stays.</summary>
    [RelayCommand]
    private void CloseDetails() => Details = null;

    /// <summary>
    /// The run itself. <b>The overlay closes</b> (E-L): the progress stands in the bar, and a
    /// list over a running transaction would have to lock completely.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (PendingRestore is not { TargetPath: { } target } row)
        {
            return;
        }

        PendingRestore = null;
        Details = null;
        IsOpen = false;

        _logger?.LogInformation(
            "restore requested: {File} from {Time}", row.FileName, row.Time);

        await _saveBar
            .RunRestoreAsync(
                progress => _restore(row.Entry.Path, target, row.Entry.Timestamp, progress),
                row.FileName)
            .ConfigureAwait(false);
    }

    /// <summary>Deletes without a question — it is a copy (design 22).</summary>
    [RelayCommand]
    private void Delete(BackupRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        _backups.Delete(row.Entry.Path);
        Rows.Remove(row);
        IsEmpty = Rows.Count == 0;

        if (PendingRestore == row)
        {
            PendingRestore = null;
        }
    }

    /// <summary>Reads the list afresh and updates the locks.</summary>
    private void Reload()
    {
        var snapshot = _snapshot();

        Rows.Clear();
        foreach (var entry in _backups.List())
        {
            var target = snapshot.RootPath is { } root
                ? SteamConfigPaths.TargetFor(entry.FileName, root, snapshot.User?.AccountId)
                : null;

            Rows.Add(new BackupRowViewModel(entry, target));
        }

        IsEmpty = Rows.Count == 0;
        UpdateLocks();
    }

    /// <summary>
    /// Two reasons lock: a running game and a run in progress. <b>Only the first gets a
    /// sentence in the header</b> — for the second the bar stands right below and says it
    /// already (spec §3.3, design answer to 21).
    /// </summary>
    private void UpdateLocks()
    {
        var gameRunning = _watcher.Current.Count > 0;
        var busy = _saveBar.IsSaving;

        LockNote = gameRunning ? GameRunningNote : string.Empty;
        HasLockNote = LockNote.Length > 0;

        foreach (var row in Rows)
        {
            row.CanRestore = !gameRunning && !busy && row.TargetPath is not null;
        }
    }
}
