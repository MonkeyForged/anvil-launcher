using Anvil.Core.Prefixes;
using Anvil.Core.Sessions;
using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// The save bar (element description §9, spec S4.5 §3.3): <b>the one place for everything the
/// write has to say</b> — no dialog of its own, no second place (project plan M4, decision 7).
/// Several states, one build; only tone, square and labels change.
///
/// <para>
/// <b>During a process it shows the chain</b> (spec S3.7 §3.4): every link with its state, the
/// restart sentence only from the first Steam link on, stage counter, clock and rail only for a
/// long link — and after a failure the chain, until <c>Dismiss</c> takes it away.
/// </para>
///
/// <para>
/// <b>The transaction comes as a delegate</b> — for the same reason as in S4.4: otherwise every
/// test of the bar would depend on journal files and backup folders.
/// </para>
///
/// <para>
/// <b>This class is the only one that posts to the UI thread</b> (E-M). Progress and continuation
/// come from the background thread (S4.4), <c>PendingChanges</c> reports raw — here every path
/// runs through the handed-in <c>dispatch</c>, and everything behind it is on the UI thread.
/// </para>
///
/// <para>
/// <b>Sixth state: <c>needs preparing</c></b> (spec S3.9 §3.4). A game whose prefix lacks a
/// required component, while nothing is unsaved, shows the bar grey — work without a user change
/// is not unsaved (requirement 30 f), but it locks the start (E-16) through
/// <see cref="WorkGames"/>. Appears only when <c>work</c>, <c>prepare</c> <b>and</b>
/// <c>turnOff</c> are handed in — otherwise the bar does not know the state.
/// </para>
///
/// <para>
/// <b>No <c>Later</c> any more</b> (E-15, owner veto against the drawing): <c>Discard</c> stages
/// "off" for every requesting module and saves <b>at once</b> — unlike elsewhere, Discard writes
/// here, because switch and launch option are already on disk. <c>Save</c> runs what
/// <c>Prepare</c> ran: the same process, only without the Steam part. While a game runs, only
/// Discard is locked (E-14: Save needs no closed Steam).
/// </para>
/// </summary>
public sealed partial class SaveBarViewModel : ObservableObject
{
    private const string LockedReason = "Steam cannot be closed while playing";

    private const string RestartNote = "Steam restarts when this is done";

    private const string Discard = "Discard";

    private const string Dismiss = "Dismiss";

    private const string Save = "Save";

    private const string RestoreBackup = "Restore backup";

    private readonly PendingChanges _changes;
    private readonly GameSessionWatcher _watcher;
    private readonly Func<uint, string?> _gameName;
    private readonly Func<TransactionRecord?> _pendingRecovery;
    private readonly Func<Action<SaveProgress>?, Task<TransactionResult>> _rollBack;
    private readonly Action _dismissRecovery;
    private readonly Action<Action> _dispatch;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly PrefixWork? _work;
    private readonly Func<Action<SaveProgress>?, Task<TransactionResult>>? _prepare;
    private readonly Action<IReadOnlyList<PrefixWorkItem>>? _turnOff;

    /// <summary>The open journal, or <c>null</c>. Read at construction and after every process (E-L).</summary>
    private TransactionRecord? _recovery;

    /// <summary>The last result — only a failed or blocked one is shown.</summary>
    private TransactionResult? _result;

    /// <summary>The last report of the chain, or <c>null</c> before the first (spec S3.7 §3.3).</summary>
    private SaveProgress? _progress;

    /// <summary>Which link ran last — position and label; when it changes, the clock starts anew.</summary>
    private string? _runningKey;

    /// <summary>When the running link began — the clock counts from here, not from the start of the process (E-9).</summary>
    private DateTimeOffset _linkStartedAt;

    /// <summary>The clock's one-second tick; only present while a link reports stages.</summary>
    private ITimer? _clock;

    /// <summary>
    /// The secondary text of a process the bar did not start itself (S4.7 E-K) — empty means
    /// "the names of the unsaved games".
    /// </summary>
    private string _note = string.Empty;

    /// <summary>Whether the state shown last was <c>needs preparing</c> — <see cref="LeftAsync"/> and
    /// <see cref="RightAsync"/> read it to tell Discard/Save from the other states.</summary>
    private bool _showingWork;

    /// <summary>The AppIds <c>needs preparing</c> shows right now — empty while the state is not
    /// visible. Set in <see cref="Update"/>, read by <see cref="WorkGames"/> (spec S3.9 E-16).</summary>
    private IReadOnlyList<uint> _workGames = [];

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isWarnTone;

    [ObservableProperty]
    private bool _isOffTone;

    [ObservableProperty]
    private bool _isErrorTone;

    /// <summary>
    /// The quiet tone as sheet 05-speicherleiste draws it (spec s13.1): square ink-muted, title ink
    /// while locked, waiting for preparation or interrupted. The other tone flags group the states
    /// differently (interrupted shares the warn tone with unsaved), so the view reads this one.
    /// </summary>
    [ObservableProperty]
    private bool _isQuietTone;

    [ObservableProperty]
    private bool _isPulsing;

    [ObservableProperty]
    private string _barTitle = string.Empty;

    [ObservableProperty]
    private string _barDetail = string.Empty;

    [ObservableProperty]
    private string _reasonText = string.Empty;

    [ObservableProperty]
    private bool _hasReason;

    /// <summary>
    /// Whether the reason is a <b>lock</b> and not just a note — it then gets the warning tone.
    /// Grey was too quiet: at the acceptance of S4.7 the owner did not see at first <i>why</i> a
    /// button did not react.
    /// </summary>
    [ObservableProperty]
    private bool _isBlocked;

    [ObservableProperty]
    private string _leftLabel = Discard;

    [ObservableProperty]
    private string _rightLabel = Save;

    [ObservableProperty]
    private bool _isRightVisible = true;

    [ObservableProperty]
    private bool _canLeft = true;

    [ObservableProperty]
    private bool _canRight = true;

    /// <summary>The chain to draw — empty when no process runs and none has failed.</summary>
    [ObservableProperty]
    private IReadOnlyList<SaveChainItem> _chain = [];

    [ObservableProperty]
    private bool _hasChain;

    /// <summary>"stage 3 of 6 · 1:48" — requirement 30 b.</summary>
    [ObservableProperty]
    private string _meterText = string.Empty;

    [ObservableProperty]
    private bool _hasMeter;

    /// <summary>How far the rail is filled, 0 to 1 — stage divided by the number of stages.</summary>
    [ObservableProperty]
    private double _railFraction;

    /// <param name="changes">The collection of unsaved wishes (S4.5 §3.1).</param>
    /// <param name="watcher">The one truth about "what is running" — for the lock.</param>
    /// <param name="gameName">Name for an AppID, or <c>null</c>; the root looks it up in the snapshot.</param>
    /// <param name="pendingRecovery">The open journal — <c>SteamTransaction.PendingRecovery</c>.</param>
    /// <param name="rollBack">The way back — <c>SteamTransaction.RollBackAsync</c> as a lambda.</param>
    /// <param name="dismissRecovery">Discards the journal — <c>SteamTransaction.Dismiss</c>.</param>
    /// <param name="dispatch">Onto the UI thread; in tests <c>action =&gt; action()</c>.</param>
    /// <param name="logger">Category <c>Ui</c>; null means silent.</param>
    /// <param name="time">The clock of the stage display; null means <see cref="TimeProvider.System"/>. Faked in tests.</param>
    /// <param name="work">The open preparation (spec S3.9 §3.2); <c>null</c> means "does not know <c>needs preparing</c>" (existing callers).</param>
    /// <param name="prepare">The process behind <c>Save</c> in the state <c>needs preparing</c> — <c>SteamTransaction.SaveAsync</c> without wishes, only with the prefix steps from <paramref name="work"/>.</param>
    /// <param name="turnOff">
    /// The process behind <c>Discard</c> (spec S3.9 E-15) — stages "off" for every requesting
    /// module (the root through <c>ModuleRegistry.StageEnabledFor</c>); the bar then saves at once
    /// itself. <c>null</c> as for <paramref name="work"/> and <paramref name="prepare"/>: only when
    /// all three are handed in does the bar know <c>needs preparing</c>.
    /// </param>
    public SaveBarViewModel(
        PendingChanges changes,
        GameSessionWatcher watcher,
        Func<uint, string?> gameName,
        Func<TransactionRecord?> pendingRecovery,
        Func<Action<SaveProgress>?, Task<TransactionResult>> rollBack,
        Action dismissRecovery,
        Action<Action> dispatch,
        ILogger? logger = null,
        TimeProvider? time = null,
        PrefixWork? work = null,
        Func<Action<SaveProgress>?, Task<TransactionResult>>? prepare = null,
        Action<IReadOnlyList<PrefixWorkItem>>? turnOff = null)
    {
        _changes = changes;
        _watcher = watcher;
        _gameName = gameName;
        _pendingRecovery = pendingRecovery;
        _rollBack = rollBack;
        _dismissRecovery = dismissRecovery;
        _dispatch = dispatch;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _work = work;
        _prepare = prepare;
        _turnOff = turnOff;

        _changes.Changed += (_, _) => _dispatch(Update);
        _watcher.SessionStarted += (_, _) => _dispatch(Update);
        _watcher.SessionEnded += (_, _) => _dispatch(Update);
        if (_work is not null)
        {
            _work.Changed += (_, _) => _dispatch(Update);
        }

        _recovery = ReadRecovery();
        if (_recovery is { } record)
        {
            _logger?.LogWarning(
                "interrupted save found: {Count} file(s)", record.Files.Count);
        }

        Update();
    }

    /// <summary>Something new is on disk — the library must be read again (decision 8).</summary>
    public event EventHandler? Saved;

    /// <summary>
    /// The set of wishes or the save state has changed; reported on the UI thread. The library
    /// updates the cards' locks and the sections' fields from it (S4.6). Called
    /// <c>LocksChanged</c> until S4.6 — the locks were only the first consequence.
    /// </summary>
    public event EventHandler? StateChanged;

    /// <summary>Which games carry unsaved wishes.</summary>
    public IReadOnlyList<uint> ChangedGames => _changes.AppIds;

    /// <summary>Which games the state <c>needs preparing</c> shows right now — empty while it is not
    /// visible. The library locks the start through it (spec S3.9 E-16).</summary>
    public IReadOnlyList<uint> WorkGames => _workGames;

    /// <summary>
    /// The left button: <c>Discard</c> or <c>Dismiss</c>, depending on the state — and
    /// <b><c>Discard</c> never touches the journal, <c>Dismiss</c> never the wishes</b> (E-N).
    ///
    /// <para>
    /// <b>In the state <c>needs preparing</c> Discard is asynchronous</b> (spec S3.9 E-15): through
    /// <c>turnOff</c> it stages "off" for every requesting module and then saves at once — unlike
    /// Discard/Dismiss elsewhere it really writes here, because switch and launch option are
    /// already on disk.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task LeftAsync()
    {
        if (IsSaving)
        {
            return;
        }

        if (IsFailure(_result))
        {
            // Forget only the message; the wishes remain, the bar falls back.
            _result = null;
            Update();
            return;
        }

        if (_recovery is not null)
        {
            _dismissRecovery();
            _logger?.LogInformation("pending transaction dismissed by the user");
            _recovery = ReadRecovery();
            Update();
            return;
        }

        if (_showingWork)
        {
            _turnOff!(_work!.Items);
            _logger?.LogInformation("prefix preparation discarded: {Count} game(s)", _work.Items.Count);
            await RunAsync(_changes.SaveAsync, isRollBack: false).ConfigureAwait(false);
            return;
        }

        _changes.Clear();
    }

    /// <summary>The right button: <c>Save</c> or <c>Restore backup</c>, depending on the state.</summary>
    [RelayCommand]
    private async Task RightAsync()
    {
        if (IsSaving || !CanRight)
        {
            return;
        }

        if (_recovery is { } record)
        {
            _logger?.LogInformation("restore requested: {Count} file(s)", record.Files.Count);
            await RunAsync(_rollBack, isRollBack: true).ConfigureAwait(false);
            return;
        }

        if (_showingWork)
        {
            _logger?.LogInformation("prefix preparation requested: {Count} game(s)", _work!.Items.Count);
            await RunAsync(_prepare!, isRollBack: false, WorkGameNames(_work!.Items)).ConfigureAwait(false);
            return;
        }

        await RunAsync(_changes.SaveAsync, isRollBack: false).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows a process another part of the interface started — the backup list from S4.7.
    /// <b>The bar stays the only place that posts to the UI thread</b> (S4.5 E-M); the overlay
    /// calls in here and draws no progress itself.
    /// </summary>
    /// <param name="run">The process — <c>SteamTransaction.RestoreAsync</c> with a fixed file.</param>
    /// <param name="note">What the secondary text says while it runs — the file name.</param>
    public Task RunRestoreAsync(
        Func<Action<SaveProgress>?, Task<TransactionResult>> run, string note)
        => RunAsync(run, isRollBack: true, note);

    /// <summary>
    /// One process from start to end: lock, report the steps, keep the result, read the journal
    /// again — and nudge the library when something new is on disk.
    /// </summary>
    private async Task RunAsync(
        Func<Action<SaveProgress>?, Task<TransactionResult>> run,
        bool isRollBack,
        string note = "")
    {
        IsSaving = true;
        _progress = null;
        _runningKey = null;
        _note = note;
        _result = null;
        Update();

        var result = await run(report => _dispatch(() =>
        {
            OnReport(report);
            Update();
        })).ConfigureAwait(false);

        _dispatch(() =>
        {
            StopClock();
            _progress = null;
            _runningKey = null;
            IsSaving = false;
            _note = string.Empty;
            _result = result;
            _recovery = ReadRecovery();
            Update();

            if (result.Wrote
                || (isRollBack && result.Outcome == TransactionOutcome.Succeeded))
            {
                Saved?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    /// <summary>
    /// Keeps the report and sets the clock (E-9): it counts from the start <b>of the link</b> and
    /// ticks only while a link reports stages. A snapshot without a running link — between two
    /// links or after the failure — does not reset it; it starts anew only with a newly running
    /// link.
    /// </summary>
    private void OnReport(SaveProgress report)
    {
        _progress = report;

        var index = IndexOf(report.Chain, link => link.State == ChainState.Running);
        if (index >= 0)
        {
            var key = $"{index}:{report.Chain[index].Label}";
            if (key != _runningKey)
            {
                _runningKey = key;
                _linkStartedAt = _time.GetUtcNow();
                StopClock();
            }
        }

        if (report.Stage is null)
        {
            StopClock();
        }
        else if (_clock is null)
        {
            _clock = _time.CreateTimer(
                _ => _dispatch(UpdateMeter), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
    }

    private void StopClock()
    {
        _clock?.Dispose();
        _clock = null;
    }

    private static int IndexOf(IReadOnlyList<ChainLink> chain, Func<ChainLink, bool> match)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (match(chain[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The link title and restart sentence follow: the running one — and when none runs, the one
    /// done or failed last. Between the end of one link and the start of the next the transaction
    /// reports a snapshot without a running link; without this fallback the bar would flicker
    /// empty there.
    /// </summary>
    private static ChainLink? CurrentLink(IReadOnlyList<ChainLink> chain)
    {
        var index = IndexOf(chain, link => link.State == ChainState.Running);
        if (index < 0)
        {
            index = IndexOf(chain, link => link.State is ChainState.Done or ChainState.Failed);
        }

        return index < 0 ? null : chain[index];
    }

    /// <summary>Stage counter, clock and rail (requirement 30 b) — only for the running long link.</summary>
    private void UpdateMeter()
    {
        // The running link's hint stands after the counter — and alone even before the first
        // stage, because the first minute is exactly the one in which nobody knows whether it runs.
        var running = _progress is { } report
            ? report.Chain.FirstOrDefault(link => link.State == ChainState.Running)
            : null;
        var hint = IsSaving ? running?.Hint : null;

        if (IsSaving && _progress?.Stage is { } stage)
        {
            var elapsed = _time.GetUtcNow() - _linkStartedAt;
            MeterText = $"stage {stage.Stage} of {stage.Of} · {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
                + (hint is null ? string.Empty : " · " + hint);
            HasMeter = true;
            RailFraction = stage.Of > 0 ? (double)stage.Stage / stage.Of : 0;
        }
        else if (hint is not null)
        {
            MeterText = hint;
            HasMeter = true;
            RailFraction = 0;
        }
        else
        {
            MeterText = string.Empty;
            HasMeter = false;
            RailFraction = 0;
        }
    }

    /// <summary>
    /// A link names its game only when the process covers several (requirement 30 c) — otherwise the
    /// same name stands right beside it in the secondary text. The core delivers the label itself in English (E-3).
    /// </summary>
    private string LinkLabel(ChainLink link, IReadOnlyList<ChainLink> chain)
    {
        if (link.AppId is not { } appId)
        {
            return link.Label;
        }

        var games = _changes.AppIds
            .Concat(chain.Where(other => other.AppId is not null).Select(other => other.AppId!.Value))
            .Distinct()
            .Count();
        return games > 1 ? $"{link.Label} · {_gameName(appId) ?? $"appid {appId}"}" : link.Label;
    }

    /// <summary>
    /// The whole state in one place — in the order of spec §3.3.1: saving → result → interrupted →
    /// locked → unsaved. At the end it reports <see cref="StateChanged"/>, because each of these
    /// changes can concern the cards.
    /// </summary>
    private void Update()
    {
        var running = _watcher.Current.Count > 0;
        var names = Names();
        _showingWork = false;
        _workGames = [];

        if (IsSaving)
        {
            var links = _progress?.Chain ?? [];
            var current = CurrentLink(links);
            Show(
                warn: false, off: true, error: false, quiet: false, pulsing: true,
                title: current is null ? string.Empty : LinkLabel(current, links),
                detail: _note.Length > 0 ? _note : names,
                // Requirement 30 d: only once a Steam link is due.
                reason: current?.NeedsSteamClosed == true ? RestartNote : string.Empty,
                left: Discard, right: Save, canLeft: false, canRight: false, rightVisible: true);
        }
        else if (IsFailure(_result))
        {
            Show(
                warn: false, off: false, error: true, quiet: false, pulsing: false,
                title: _result!.Title, detail: _result.Detail ?? string.Empty, reason: string.Empty,
                left: Dismiss, right: Save, canLeft: true, canRight: false, rightVisible: false);
        }
        else if (_recovery is { } record)
        {
            Show(
                warn: true, off: false, error: false, quiet: true, pulsing: false,
                title: "An earlier save was interrupted", detail: FileNames(record),
                reason: running ? LockedReason : string.Empty,
                left: Dismiss, right: RestoreBackup,
                canLeft: true, canRight: !running, rightVisible: true);
        }
        // A process of switches alone does not end Steam — the lock applies only when a Steam
        // wish is part of it (spec S3.8 E-9, S3.7 E-5).
        else if (_changes.NeedsSteamClosed && running)
        {
            Show(
                warn: false, off: true, error: false, quiet: true, pulsing: false,
                title: "Not possible right now — a game is running", detail: names,
                reason: LockedReason,
                left: Discard, right: Save, canLeft: true, canRight: false, rightVisible: true);
        }
        else if (_changes.Any)
        {
            Show(
                warn: true, off: false, error: false, quiet: false, pulsing: false,
                title: CountTitle(), detail: names, reason: string.Empty,
                left: Discard, right: Save, canLeft: true, canRight: true, rightVisible: true);
        }
        else if (VisibleWork() is { Count: > 0 } items)
        {
            // Requirement 30 f: grey, because nothing the user touched is open (spec S3.9 §3.4).
            // E-15: Discard/Save instead of Later/Prepare; while a game runs, only Discard locks (E-14).
            _showingWork = true;
            _workGames = items.Select(item => item.Game.Game.AppId).ToList();
            Show(
                warn: false, off: true, error: false, quiet: true, pulsing: false,
                title: WorkTitle(items), detail: WorkNames(items),
                reason: running ? LockedReason : WorkReason(items),
                left: Discard, right: Save, canLeft: !running, canRight: true, rightVisible: true);
        }
        else
        {
            IsVisible = false;
            IsPulsing = false;
        }

        // The chain stands during the process and after a failure, until Dismiss (spec S3.7 §3.4).
        var shown = IsSaving ? _progress?.Chain : IsFailure(_result) ? _result!.Chain : null;
        Chain = shown is null
            ? []
            : shown.Select((link, i) => new SaveChainItem(LinkLabel(link, shown), link.State, i == 0)).ToList();
        HasChain = Chain.Count > 0;
        UpdateMeter();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the whole bar in one go — one call per state, no half-set state.</summary>
    private void Show(
        bool warn, bool off, bool error, bool quiet, bool pulsing,
        string title, string detail, string reason,
        string left, string right, bool canLeft, bool canRight, bool rightVisible)
    {
        IsWarnTone = warn;
        IsOffTone = off;
        IsErrorTone = error;
        IsQuietTone = quiet;
        IsPulsing = pulsing;
        BarTitle = title;
        BarDetail = detail.Length == 0 ? string.Empty : "· " + detail;
        ReasonText = reason;
        HasReason = reason.Length > 0;

        // The reason text carries two very different sentences: a lock ("not possible right
        // now") and a calm announcement ("Steam restarts shortly"). Only the lock gets the
        // warning tone — otherwise the bar would light up at something that is fine. The
        // comparison against the constant is the one truth about which sentence is which (owner
        // decision 2026-09-08 after the live acceptance of S4.7).
        IsBlocked = reason == LockedReason;
        LeftLabel = left;
        RightLabel = right;
        CanLeft = canLeft;
        CanRight = canRight;
        IsRightVisible = rightVisible;
        IsVisible = true;
    }

    /// <summary>
    /// A <c>Blocked</c> shows the same red state as a <c>Failed</c> (E-E): the bar locks its
    /// buttons itself — if one comes back anyway, it is a surprise.
    /// </summary>
    private static bool IsFailure(TransactionResult? result)
        => result is { Outcome: TransactionOutcome.Failed or TransactionOutcome.Blocked };

    private string CountTitle()
    {
        var count = _changes.AppIds.Count;
        return count == 1 ? "1 game unsaved" : $"{count} games unsaved";
    }

    /// <summary>
    /// The work the bar shows — nothing until all three processes are handed in: <c>work</c>,
    /// <c>prepare</c> and <c>turnOff</c> (spec S3.9 E-15). If one is missing, the bar does not
    /// know the state.
    /// </summary>
    private IReadOnlyList<PrefixWorkItem> VisibleWork()
        => _work is null || _prepare is null || _turnOff is null ? [] : _work.Items;

    private static string WorkTitle(IReadOnlyList<PrefixWorkItem> items)
        => items.Count == 1 ? $"{items[0].Game.Game.Name} needs preparing" : $"{items.Count} games need preparing";

    /// <summary>The secondary text at rest (E-17): empty for one game, its name is in the title.</summary>
    private static string WorkNames(IReadOnlyList<PrefixWorkItem> items)
        => items.Count <= 1 ? string.Empty : WorkGameNames(items);

    /// <summary>The game names, untrimmed — the secondary text while <c>Save</c> runs (E-15).</summary>
    private static string WorkGameNames(IReadOnlyList<PrefixWorkItem> items)
        => string.Join(" · ", items.Select(item => item.Game.Game.Name));

    // E-9: the drawn wording of the need line (S3.5b), reported as requirement 47.
    private static string WorkReason(IReadOnlyList<PrefixWorkItem> items)
        => string.Join(" · ", items
            .SelectMany(item => item.Missing)
            .Select(required => $"Needs {required.Component.DisplayName} {required.Component.Version}")
            .Distinct());

    /// <summary>
    /// The names of the games concerned. A game the snapshot does not know appears as
    /// <c>appid 12345</c> — it must not vanish, or the bar would count more than it names.
    /// </summary>
    private string Names()
        => string.Join(" · ", _changes.AppIds.Select(
            appId => _gameName(appId) ?? $"appid {appId}"));

    private static string FileNames(TransactionRecord record)
        => string.Join(" · ", record.Files.Select(file => Path.GetFileName(file.Path)));

    /// <summary>
    /// The journal is read at construction and after every process, never otherwise (E-L) —
    /// nobody else writes it.
    /// </summary>
    private TransactionRecord? ReadRecovery() => _pendingRecovery();
}
