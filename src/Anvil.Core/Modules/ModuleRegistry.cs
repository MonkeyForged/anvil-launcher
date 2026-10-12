using Anvil.Core.Prefixes;
using Anvil.Core.Settings;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Modules;

/// <summary>
/// The one truth of "which modules exist and which are globally active". Modules are handed in
/// by the composition root, never sought out themselves. The active state sits in the Modules
/// section of <see cref="AppSettings"/>; the registry is the only place that writes it.
///
/// <para>
/// <b>Only the global switch is saved immediately.</b> The per-game switch has, since S3.8, been
/// a wish in <see cref="PendingChanges"/> (Spec E-3): <see cref="StageEnabledFor"/> stages it,
/// <see cref="SaveEnabledFor"/> writes it — called by the step that <c>PendingChanges</c> hands
/// over on save. Hence there are two questions: saved (<see cref="IsEnabledFor"/>) and including
/// unsaved (<see cref="IsEnabledForIncludingUnsaved"/>).
/// </para>
/// </summary>
public sealed class ModuleRegistry
{
    private readonly Dictionary<string, IModule> _byKey = [];
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly PendingChanges _changes;
    private readonly ILogger? _logger;

    /// <summary>
    /// The last stored finding per module; a missing entry counts as <c>null</c> — "being
    /// checked" (S3.4, Spec E-4). A dictionary of its own instead of a tuple with
    /// <see cref="_generation"/> (Plan P-2): a tuple would force every reader to also read the
    /// number, which is none of its business.
    /// </summary>
    private readonly Dictionary<string, ModuleReadiness?> _readiness = new(StringComparer.Ordinal);

    /// <summary>
    /// The running number of the most recently started check per module — the guard against a
    /// stale result (Spec E-6, Plan P-3). A missing entry counts as 0.
    /// </summary>
    private readonly Dictionary<string, int> _generation = new(StringComparer.Ordinal);

    public ModuleRegistry(
        IEnumerable<IModule> modules, AppSettings settings, SettingsStore store,
        PendingChanges changes, ILogger? logger = null)
    {
        _settings = settings;
        _changes = changes;
        _store = store;
        _logger = logger;
        Modules = modules.ToList();
        foreach (var module in Modules)
        {
            if (!_byKey.TryAdd(module.Key, module))
            {
                throw new ArgumentException(
                    $"duplicate module key: {module.Key}", nameof(modules));
            }

            _logger?.LogInformation(
                "module registered: {Key} ({Name})", module.Key, module.DisplayName);
        }
    }

    public IReadOnlyList<IModule> Modules { get; }

    /// <summary>
    /// A module's global switch has changed; the argument is the key (S1.4).
    ///
    /// <para>
    /// <b>What for:</b> the global switch sits on the Modules page, the module card in the right
    /// column. Without this notification a disabled module's card would keep standing until the
    /// library is rebuilt.
    /// </para>
    ///
    /// <para>
    /// <b>Save first, then notify:</b> a listener reads the registry right back — and must then
    /// already get the new answer. For the per-game switch see
    /// <see cref="EnabledForGameChanged"/>.
    /// </para>
    /// </summary>
    public event EventHandler<string>? EnabledChanged;

    /// <summary>
    /// A module's <b>displayed</b> per-game switch has changed (S2.3, since S3.8 E-5: on
    /// staging).
    ///
    /// <para>
    /// <b>What for:</b> since M2, more hangs on the per-game switch than the card that operates
    /// it — the module's contributions now belong in the game's launch options, and the launch
    /// options field of the same right column must restate its wish.
    /// </para>
    ///
    /// <para>
    /// <b>This repeals plan S1.4 E-4</b>, and with that plan's own reasoning: there, the per-game
    /// switch stayed silent because only the card itself operated it and knew of its own change.
    /// Now a second listener is watching. The seam grows under load.
    /// </para>
    ///
    /// <para>
    /// <b>Fires on staging, not on writing and not on discarding</b> (S3.8 E-5):
    /// <see cref="SaveEnabledFor"/> changes nothing visible and runs on a foreign thread; the
    /// path after <c>Discard</c> is the save bar's <c>StateChanged</c>. <b>Stage first, then
    /// notify</b> — a listener reads <see cref="IsEnabledForIncludingUnsaved"/> right back.
    /// </para>
    /// </summary>
    public event EventHandler<ModuleGameToggle>? EnabledForGameChanged;

    /// <summary>
    /// A module's finding has changed; the argument is the key (S3.4).
    ///
    /// <para>
    /// <b>What for:</b> two surfaces show the finding — the badge on the Modules row
    /// (<c>checking</c>/<c>active</c>/<c>not ready</c>) and the module card in the right column,
    /// which disappears on a failed finding. One notification serves both.
    /// </para>
    ///
    /// <para>
    /// <b>Fires twice per check:</b> once when <see cref="CheckAsync"/> sets the finding to
    /// <c>null</c> (the row goes to <c>checking</c>), once when the result is in. A stale run
    /// does not notify at all (Spec E-6).
    /// </para>
    ///
    /// <para>
    /// <b>Can fire on a foreign thread</b> — a module's check is arbitrary code. Whoever drives
    /// the UI from it needs its own path back to the UI thread, the same pattern as
    /// <c>LogViewModel</c> and <c>LibraryViewModel</c>.
    /// </para>
    ///
    /// <para><b>Store first, then notify</b> — as with <see cref="EnabledChanged"/>.</para>
    /// </summary>
    public event EventHandler<string>? ReadinessChanged;

    public bool IsEnabled(string key)
    {
        var module = Get(key);
        if (module.IsCore)
        {
            return true;
        }

        return !_settings.Modules.TryGetValue(key, out var section)
            || (section.Enabled ?? true);
    }

    public void SetEnabled(string key, bool enabled)
    {
        var module = Get(key);
        if (module.IsCore)
        {
            // The UI does not offer the switch for core entries —
            // arriving here is a programming error and should fail loudly, early.
            throw new InvalidOperationException(
                $"core module cannot be disabled: {key}");
        }

        if (!_settings.Modules.TryGetValue(key, out var section))
        {
            section = new ModuleSection();
            _settings.Modules[key] = section;
        }

        section.Enabled = enabled;
        _store.Save(_settings);
        _logger?.LogInformation(
            "module {Key} {State}", key, enabled ? "enabled" : "disabled");
        EnabledChanged?.Invoke(this, key);

        // Deliberately does not trigger a check (Spec E-5): whoever enables it checks. That is
        // exactly one place in the UI (ModuleItemViewModel, on flipping the switch) and one on
        // startup (ModulesViewModel). A _ = CheckAsync(key) here would be a task that nobody
        // holds, whose exceptions nobody sees, and that no test can await.
    }

    /// <summary>
    /// The last stored finding for this module, or <c>null</c> — "currently being checked" (S3.4,
    /// Spec E-4). No fourth state beside Ready/NotReady: an enabled module either has a result or
    /// a check in progress, never a third case.
    ///
    /// <para>
    /// <b>Via <see cref="Get"/></b>, like <see cref="IsEnabled"/>: an unknown key should fail
    /// loudly, early, not silently return <c>null</c> and thereby fake "being checked".
    /// </para>
    /// </summary>
    public ModuleReadiness? ReadinessOf(string key)
    {
        Get(key);
        return _readiness.GetValueOrDefault(key);
    }

    /// <summary>
    /// Triggers this module's check (S3.4, Spec §3.3): set the finding to <c>null</c> and notify
    /// (the row goes to <c>checking</c>) — ask the module — store the result and notify again.
    ///
    /// <para>
    /// <b>A generation counter per key</b> (Spec E-6, Plan P-3) prevents a stale result from
    /// overwriting a newer one: if the user quickly disables and re-enables, two checks run, and
    /// the older one can finish later. Whoever no longer carries the current number after the
    /// <c>await</c> does not store its result and does not notify.
    /// </para>
    ///
    /// <para>
    /// <b>Still returns the finding</b> (Plan P-4), even though the main path runs through
    /// <see cref="ReadinessChanged"/>: the return value makes tests readable
    /// (<c>var readiness = await registry.CheckAsync("wand_bridge")</c>) and costs nothing. Even
    /// a stale run gets its own result — it just does not store it.
    /// </para>
    ///
    /// <para>
    /// <b>A throwing module yields <c>NotReady</c>, not a crash</b> (Spec E-7, modeled on
    /// <c>PackageFetcher</c> P-1): behind the contract sits foreign code, and a propagated
    /// exception would take the Modules page down with it. No dedicated path for
    /// <see cref="OperationCanceledException"/>: unlike sourcing (S3.3a), there is no cancel
    /// button here — cancellation only happens on shutdown, and by then nobody is drawing
    /// anymore.
    /// </para>
    /// </summary>
    public async Task<ModuleReadiness> CheckAsync(string key, CancellationToken cancellationToken = default)
    {
        var module = Get(key);

        var generation = _generation.GetValueOrDefault(key) + 1;
        _generation[key] = generation;
        _readiness[key] = null;
        ReadinessChanged?.Invoke(this, key);

        ModuleReadiness result;
        try
        {
            result = await module.CheckReadinessAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            result = ModuleReadiness.NotReady(
                "The module could not be checked — see the log for details.");
            _logger?.LogError(exception, "module {Key} readiness check failed", key);
        }

        if (_generation.GetValueOrDefault(key) != generation)
        {
            // Stale: a newer check is already running or already done. Storing would mean
            // overwriting its result with an older one (Spec E-6) — so neither store nor
            // notify, only return this run's own result (Plan P-4).
            return result;
        }

        _readiness[key] = result;
        if (result.IsReady)
        {
            _logger?.LogInformation("module {Key} readiness: {State}", key, "ready");
        }
        else
        {
            _logger?.LogInformation(
                "module {Key} readiness: {State} — {Reason}", key, "not ready", result.Reason);
        }

        ReadinessChanged?.Invoke(this, key);
        return result;
    }

    /// <summary>
    /// Is this module active for this game — <b>per saved state</b>? (S1.2) The runtime
    /// question: what is staged but not saved does not count here (S3.8 §3.2).
    ///
    /// <para>
    /// <b>Globally off overrules per-game.</b> For modules without
    /// <see cref="IModule.IsPerGame"/>, the answer is the global state — the question still
    /// makes sense there, and without this fallback every caller would first have to check
    /// <c>IsPerGame</c> (Plan S1.2, E-1). The default for per-game modules is <b>off</b>: a
    /// module does not attach itself to every game unasked.
    /// </para>
    /// </summary>
    public bool IsEnabledFor(string key, uint appId)
    {
        var module = Get(key);
        if (!IsEnabled(key))
        {
            return false;
        }

        if (!module.IsPerGame)
        {
            return true;
        }

        return IsListedForGame(key, appId);
    }

    /// <summary>
    /// Is this module active for this game — <b>including unsaved</b>? (S3.8 E-3) The UI's
    /// question: module card and launch options field show the value that will hold after
    /// saving.
    ///
    /// <para>
    /// <b>Same rules as <see cref="IsEnabledFor"/></b> — globally off overrules per-game,
    /// modules without <see cref="IModule.IsPerGame"/> fall back to the global switch. The only
    /// difference: a staged wish is asked first. The link sits right here, so that card and
    /// field do not each rebuild it on their own.
    /// </para>
    /// </summary>
    public bool IsEnabledForIncludingUnsaved(string key, uint appId)
    {
        var module = Get(key);
        if (!IsEnabled(key))
        {
            return false;
        }

        if (!module.IsPerGame)
        {
            return true;
        }

        return _changes.ModuleSwitch(key, appId) ?? IsListedForGame(key, appId);
    }

    /// <summary>
    /// Does this game sit on this module's list? <b>The saved entry</b>, without the global
    /// switch and without any wish — unlike <see cref="IsEnabledFor"/> (Plan S2.3a, P-1). This
    /// is the state <see cref="StageEnabledFor"/> stages against.
    /// </summary>
    private bool IsListedForGame(string key, uint appId)
        => _settings.Modules.TryGetValue(key, out var section)
            && section.Games is { } games
            && games.Contains(appId);

    /// <summary>
    /// Stages this module's per-game switch for this game (S3.8 §3.2) — it is only written on
    /// save, via <see cref="SaveEnabledFor"/>.
    ///
    /// <para>
    /// <b>Compared against the saved entry</b>, not against <see cref="IsEnabledFor"/>: if the
    /// module is globally off, that question would read "off" and stage a wish that matches the
    /// saved state. If the value matches the entry, an open wish drops out — whoever flips it
    /// twice no longer has one, and the bar disappears on its own.
    /// </para>
    /// </summary>
    public void StageEnabledFor(string key, uint appId, bool enabled)
    {
        var module = Get(key);
        if (!module.IsPerGame)
        {
            // The UI offers the per-game switch only for IsPerGame modules —
            // arriving here would stage a state that nobody ever reads.
            throw new InvalidOperationException(
                $"module cannot be switched per game: {key}");
        }

        if (enabled == IsListedForGame(key, appId))
        {
            _changes.DropModuleSwitch(key, appId);
        }
        else
        {
            _changes.SetModuleSwitch(new ModuleGameSwitch(key, appId, enabled));
        }

        EnabledForGameChanged?.Invoke(this, new ModuleGameToggle(key, appId));
    }

    /// <summary>
    /// Writes several per-game switches in one go (S3.8 E-1, E-3) — the step that
    /// <see cref="Steam.PendingChanges.SaveAsync"/> hands over before every Steam step.
    ///
    /// <para>
    /// <b>Copy before writing, roll back on failure</b> (E-6): otherwise, after a failed write,
    /// memory would say "on" while disk says "off" — a state that lies.
    /// </para>
    ///
    /// <para>
    /// <b>Notifies nothing</b> (E-5): the step runs on a foreign thread (the save routine), and
    /// the UI only reads the switches back when it redraws on its own — unlike
    /// <see cref="StageEnabledFor"/>, which comes from the UI thread.
    /// </para>
    /// </summary>
    public void SaveEnabledFor(IReadOnlyList<ModuleGameSwitch> switches)
    {
        // E-6: Copy of the affected lists, so a failed write does not leave the store lying.
        var before = switches
            .Select(wish => wish.Key)
            .Distinct()
            .ToDictionary(key => key, key => _settings.Modules.TryGetValue(key, out var section)
                ? (Exists: true, Games: section.Games?.ToList())
                : (Exists: false, Games: null));

        // Check every module first, only change afterward: otherwise, if a second wish throws,
        // the first would already be sitting in memory without anything having been written or
        // rolled back.
        foreach (var wish in switches)
        {
            if (!Get(wish.Key).IsPerGame)
            {
                // Same stance as StageEnabledFor: the UI offers the switch only for
                // IsPerGame modules — arriving here is a programming error.
                throw new InvalidOperationException(
                    $"module cannot be switched per game: {wish.Key}");
            }
        }

        foreach (var wish in switches)
        {
            ApplyToSettings(wish.Key, wish.AppId, wish.Enabled);
        }

        try
        {
            _store.Save(_settings);
        }
        catch
        {
            foreach (var (key, state) in before)
            {
                if (!state.Exists)
                {
                    _settings.Modules.Remove(key);
                }
                else
                {
                    _settings.Modules[key].Games = state.Games;
                }
            }

            throw;
        }

        foreach (var wish in switches)
        {
            _logger?.LogInformation(
                "module {Key} {State} for app {AppId}", wish.Key, wish.Enabled ? "enabled" : "disabled", wish.AppId);
        }
    }

    /// <summary>
    /// Can this module possibly take hold for this game at all? (Spec S3.9 E-12, s9.1 E-10) A
    /// per-game module never takes hold on a native game — every bridge works in the game's Proton
    /// container. A module that demands prefix components additionally needs the prefix to be
    /// present, so it is also not possible for a game never launched or of unknown platform. The
    /// one place for the rule: <see cref="SwitchOffUnprepared"/> switches off based on it, and the
    /// module card draws <c>not possible</c> based on it.
    /// </summary>
    public static bool IsPossibleFor(IModule module, GameProfile game)
    {
        var state = PrefixReport.Of(game).State;
        return state != PrefixState.NotPossible
            && (module.PrefixComponentsFor(game).Count == 0 || state == PrefixState.Present);
    }

    /// <summary>
    /// Is this game's prefix prepared for this module? (Spec s8.1) True when the module demands no
    /// component, or when the prefix is there and every demanded component's marks are in it — the
    /// same probe the preparer asks. Deliberately blind to the Proton major: an upgraded prefix
    /// with .NET works and has its own way back through "needs preparing" (E-1).
    /// </summary>
    public bool IsPreparedFor(string key, GameProfile game) => IsPreparedFor(Get(key), game);

    private static bool IsPreparedFor(IModule module, GameProfile game)
    {
        var components = module.PrefixComponentsFor(game);
        if (components.Count == 0)
        {
            return true;
        }

        return PrefixReport.Of(game) is { State: PrefixState.Present, Path: { } prefixPath }
            && components.All(component => PrefixMarkProbe.Detect(prefixPath, component, []).Present);
    }

    /// <summary>
    /// Was the prefix downgraded to an older Proton? A single requested component is enough —
    /// the statement needs no more than that.
    ///
    /// <para>
    /// <b>Why this does not sit in <see cref="IsPossibleFor"/>:</b> that static rule also
    /// decides which saved switches disappear (S3.9, E-13), and a recoverable state should not
    /// trigger that (S4.2, E-1). Hence a second no, beside it — but since S5.2 in <b>one</b>
    /// place instead of in the UI.
    /// </para>
    /// </summary>
    public static bool IsMovedBackFor(IModule module, GameProfile game)
    {
        var report = PrefixReport.Of(game);
        if (report is not { State: PrefixState.Present, Path: { } prefixPath, ProtonMajor: { } major })
        {
            return false;
        }

        foreach (var component in module.PrefixComponentsFor(game))
        {
            if (PrefixPayloadStanding.Of(prefixPath, component.Key, major).Kind
                == PayloadStandingKind.MovedBack)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Takes every per-game switch out of the saved list whose game the module cannot possibly
    /// reach, and — new in s8.1 — whose prefix is present but not prepared. Drops a staged
    /// switch only in the first case; in the second, the staged switch survives, because it is
    /// the wish that will trigger preparing (Owner decision 2026-09-23, spec s8.1). Returns the
    /// number of (module, game) pairs taken out of the saved list.
    ///
    /// <para>
    /// <b>Why:</b> a saved switch means ready (Owner decision after the acceptance halt, S3.9
    /// E-13). One that stays on for a prefix that cannot work — missing entirely, or present but
    /// never prepared — shows <c>ready</c> while lying. Called when the library is read, before
    /// the cards are built.
    /// </para>
    ///
    /// <para>
    /// <b>Skips a running game</b> via <paramref name="isRunning"/>, <c>null</c> meaning "none is
    /// running" (spec s8.1 E-3): switching a game on while it runs must survive the save's
    /// rescan — preparing is skipped for a running game (S3.9 E-8), and the rescan would
    /// otherwise switch that wish straight back off.
    /// </para>
    ///
    /// <para>
    /// <b>Skips a moved-back prefix</b> (<see cref="IsMovedBackFor"/>, spec s8.1 E-2): that is
    /// its own, already explained state with its own card badge; switching it off too would take
    /// the explanation away from the user.
    /// </para>
    ///
    /// <para>
    /// <b>Saved list, not <see cref="IsEnabledFor"/>:</b> the entry falls out even for a
    /// globally disabled module — otherwise it would reappear once it is re-enabled.
    /// </para>
    ///
    /// <para>
    /// <b>Notifies no <see cref="EnabledForGameChanged"/></b> (E-13): otherwise the launch
    /// options field would stage the removed launch option as an unsaved wish and block exactly
    /// the first launch that creates the prefix. The launch option stays standing in Steam (E-14).
    /// </para>
    ///
    /// <para>
    /// <b>Does not throw:</b> if the write fails, the lists roll back (like
    /// <see cref="SaveEnabledFor"/>, S3.8 E-6) and it is logged — Anvil's startup must not fail
    /// because of it. The next attempt comes on the next read.
    /// </para>
    /// </summary>
    public int SwitchOffUnprepared(IReadOnlyList<GameProfile> games, Func<uint, bool>? isRunning = null)
    {
        var switchedOff = new List<(string Key, uint AppId, string Reason)>();
        var before = new Dictionary<string, List<uint>?>(StringComparer.Ordinal);

        void Remember(string key, uint appId, string reason)
        {
            if (!IsListedForGame(key, appId))
            {
                return;
            }

            if (!before.ContainsKey(key))
            {
                before[key] = _settings.Modules[key].Games?.ToList();
            }

            ApplyToSettings(key, appId, enabled: false);
            switchedOff.Add((key, appId, reason));
        }

        foreach (var module in Modules)
        {
            if (!module.IsPerGame)
            {
                continue;
            }

            foreach (var game in games)
            {
                var appId = game.Game.AppId;

                if (!IsPossibleFor(module, game))
                {
                    _changes.DropModuleSwitch(module.Key, appId);
                    Remember(module.Key, appId, "no prefix");
                    continue;
                }

                if (isRunning?.Invoke(appId) == true)
                {
                    continue;
                }

                if (IsMovedBackFor(module, game))
                {
                    continue;
                }

                if (!IsPreparedFor(module, game))
                {
                    Remember(module.Key, appId, "prefix not prepared");
                }
            }
        }

        if (switchedOff.Count == 0)
        {
            return 0;
        }

        try
        {
            _store.Save(_settings);
        }
        catch (Exception exception)
        {
            // Only lists of existing sections were changed — IsListedForGame presupposes one.
            foreach (var (key, list) in before)
            {
                _settings.Modules[key].Games = list;
            }

            _logger?.LogError(exception, "switching off unprepared modules failed");
            return 0;
        }

        foreach (var (key, appId, reason) in switchedOff)
        {
            _logger?.LogInformation("module {Key} switched off for app {AppId}: {Reason}", key, appId, reason);
        }

        return switchedOff.Count;
    }

    /// <summary>
    /// The list-editing part of switching, without saving or notifying — factored out of
    /// <see cref="SaveEnabledFor"/> (S3.8).
    /// </summary>
    private void ApplyToSettings(string key, uint appId, bool enabled)
    {
        var module = Get(key);
        if (!module.IsPerGame)
        {
            // The UI offers the per-game switch only for IsPerGame modules —
            // arriving here would write a state that nobody ever reads.
            throw new InvalidOperationException(
                $"module cannot be switched per game: {key}");
        }

        if (!_settings.Modules.TryGetValue(key, out var section))
        {
            section = new ModuleSection();
            _settings.Modules[key] = section;
        }

        var games = section.Games ?? [];
        if (enabled)
        {
            if (!games.Contains(appId))
            {
                games.Add(appId);
            }
        }
        else
        {
            games.RemoveAll(id => id == appId);
        }

        // An empty list means the same as "no field" — so none should stand there either.
        section.Games = games.Count > 0 ? games : null;
    }

    /// <summary>
    /// The sections of all active modules <b>without their own card</b> for this game, **in
    /// registration order** (Spec S4.6 E-B). No sort number in the module: the composition root
    /// is the one place where the order is visibly decided — a second one would be a second
    /// truth.
    ///
    /// <para>
    /// <b>Modules with <see cref="IModule.IsPerGame"/> carry their section in their own
    /// card</b> (Spec S5.4 §2, E-1). Without this rule it would land on the GAME card, where it
    /// does not belong — the module card's header line stays untouched by this, it does not
    /// depend on this list.
    /// </para>
    /// </summary>
    public IReadOnlyList<object> SectionsFor(GameProfile game)
    {
        var sections = new List<object>();

        foreach (var module in Modules)
        {
            if (module.IsPerGame || !IsEnabled(module.Key))
            {
                continue;
            }

            if (module.CreateSection(game) is { } section)
            {
                sections.Add(section);
            }
        }

        return sections;
    }

    /// <summary>
    /// The contributions of the modules active for <b>this game</b>, in registration order
    /// (S2.2). This is the answer to "what applies now for this game", and in S2.3 becomes the
    /// value Steam gets.
    ///
    /// <para>
    /// <b>Via <see cref="IsEnabledForIncludingUnsaved"/></b>, not via <see cref="IsEnabled"/>: a
    /// contribution <i>is</i> per game. Globally off overrules per-game — the switch question
    /// already decides that, and this place does not repeat it.
    /// </para>
    ///
    /// <para>
    /// <b>Including unsaved</b> (S3.8 E-4): the only reader is the launch options field, and it
    /// draws the value that will hold after saving. A saved counterpart arises when someone reads
    /// it at runtime (M4).
    /// </para>
    ///
    /// <para>
    /// <b>Raw and unordered:</b> no deduplication, no merging, no sorting. That is the work of
    /// <see cref="Anvil.Core.Steam.LaunchOptionsComposer"/>, and doing it twice would be a second
    /// truth.
    /// </para>
    /// </summary>
    public IReadOnlyList<LaunchOptionContribution> LaunchOptionsFor(GameProfile game)
    {
        var contributions = new List<LaunchOptionContribution>();

        foreach (var module in Modules)
        {
            if (IsEnabledForIncludingUnsaved(module.Key, game.Game.AppId))
            {
                contributions.AddRange(module.LaunchOptionsFor(game));
            }
        }

        return contributions;
    }

    /// <summary>
    /// The requests of the modules active for <b>this game</b>, in registration order (S3.2).
    /// The same seam as <see cref="LaunchOptionsFor"/>, a different subject, <b>raw and
    /// unordered</b> — the reasoning there holds here unchanged.
    ///
    /// <para>
    /// <b>But via <see cref="IsEnabledFor"/>, so saved</b> (S3.8 E-4, Spec S3.9 E-4): this is
    /// the question <c>needs preparing</c> asks. Otherwise <c>Discard</c> would change the work
    /// list — and an unsaved switch sits in the unsaved bar anyway, which takes precedence. On
    /// save, <see cref="PrefixComponentRequestsForIncludingUnsaved"/> applies.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>No counterpart to <see cref="AllLaunchOptionsFor"/>.</b> That one solves "what is our
    /// share at all" — needed because a contribution can stay standing in Steam's launch options
    /// field when its module is disabled. A prefix note is never taken back; the question does
    /// not arise here.
    /// </remarks>
    public IReadOnlyList<PrefixComponentRequest> PrefixComponentRequestsFor(GameProfile game)
        => RequestsFor(game, IsEnabledFor);

    /// <summary>
    /// Like <see cref="PrefixComponentRequestsFor"/>, but <b>including unsaved</b> (Spec S3.9
    /// E-4): the question on save — whoever enables the switch and saves gets the preparation in
    /// the same operation. <c>needs preparing</c> asks the saved form.
    /// </summary>
    public IReadOnlyList<PrefixComponentRequest> PrefixComponentRequestsForIncludingUnsaved(GameProfile game)
        => RequestsFor(game, IsEnabledForIncludingUnsaved);

    /// <summary>
    /// The loop behind both request questions; they differ only in the switch question.
    /// </summary>
    private List<PrefixComponentRequest> RequestsFor(GameProfile game, Func<string, uint, bool> isEnabled)
    {
        var requests = new List<PrefixComponentRequest>();

        foreach (var module in Modules)
        {
            if (!isEnabled(module.Key, game.Game.AppId))
            {
                continue;
            }

            foreach (var component in module.PrefixComponentsFor(game))
            {
                requests.Add(new PrefixComponentRequest(module.Key, component));
            }
        }

        return requests;
    }

    /// <summary>
    /// Asks <b>one</b> module what it wants launched in the container (S5.1 §3.4).
    ///
    /// <para>
    /// <b>Why individually and not filtered out of the all-modules form</b>: the answer is
    /// allowed to work — Wand Bridge unpacks around 160 MB behind it the first time. Whoever
    /// prepares every module for a single module's attach lets foreign modules do work that
    /// nobody ordered.
    /// </para>
    ///
    /// <para>
    /// <b>The per-game switch is not read here</b> — unlike in the all-modules form. Whoever
    /// names a specific module has already answered the question "am I allowed to"; asking it
    /// again here would mean inventing a refusal that the caller could not tell apart from "the
    /// module attaches nothing".
    /// </para>
    ///
    /// <para>
    /// <b>A throwing module yields a refusal, not a crash</b> — the same rule as for the
    /// finding. The caller's <b>cancellation</b>, on the other hand, propagates through.
    /// </para>
    ///
    /// <para>
    /// <paramref name="findGameProcessId"/> is handed to the module as it is (spec s10.1 I-4);
    /// the registry never asks it itself.
    /// </para>
    /// </summary>
    public async Task<AttachDeclaration> PrepareAttachAsync(
        GameProfile game,
        string key,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken)
    {
        var module = Get(key);

        try
        {
            return await module.PrepareAttachAsync(game, findGameProcessId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "module {Key} attach preparation failed", key);
            return AttachDeclaration.NotPossible(
                "The module could not be prepared — see the log for details.");
        }
    }

    /// <summary>
    /// What the modules active for <b>this game</b> want launched in the container (Spec S4.4
    /// §3.2), in registration order. The same seam as
    /// <see cref="PrefixComponentRequestsFor"/>, a different subject.
    ///
    /// <para>
    /// <b>Via <see cref="IsEnabledFor"/>, so saved:</b> attaching happens to a <i>running</i>
    /// game — what sits in an open save bar does not count for that.
    /// </para>
    ///
    /// <para>
    /// <b>Whoever has nothing to attach does not appear</b> (E-5): otherwise the core modules
    /// would fill every list with answers nobody needs.
    /// </para>
    ///
    /// <para>
    /// <b>A throwing module yields a refusal, not a crash</b> — the same rule as for the
    /// finding (<see cref="CheckAsync"/>). Behind the contract sits foreign code, and a
    /// propagated exception would take the whole attach process down with it. The caller's
    /// <b>cancellation</b>, on the other hand, propagates through: it is no statement about a
    /// module.
    /// </para>
    ///
    /// <para>Every module asked gets the same <paramref name="findGameProcessId"/>.</para>
    /// </summary>
    public async Task<IReadOnlyList<ModuleAttachDeclaration>> PrepareAttachAsync(
        GameProfile game,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken)
    {
        var declarations = new List<ModuleAttachDeclaration>();

        foreach (var module in Modules)
        {
            if (!IsEnabledFor(module.Key, game.Game.AppId))
            {
                continue;
            }

            var declaration = await PrepareAttachAsync(
                game, module.Key, findGameProcessId, cancellationToken);
            if (declaration.State == AttachDeclarationState.Nothing)
            {
                continue;
            }

            declarations.Add(new ModuleAttachDeclaration(module.Key, declaration));
        }

        return declarations;
    }

    /// <summary>
    /// This module's process image names (<c>IModule.AttachProcessImages</c>), for attaching
    /// (S5.1).
    ///
    /// <para>
    /// <b>Without a protective shell:</b> this is a field access on a constant, not a call that
    /// does work. A <c>try</c> around it would be precaution without a case.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AttachProcessImagesOf(string key) => Get(key).AttachProcessImages;

    /// <summary>
    /// Whether this module's program closes when asked (<c>IModule.ClosesOnRequest</c>), for
    /// detaching (s10.2 E-5). Without a protective shell, for the same reason as
    /// <see cref="AttachProcessImagesOf"/>.
    /// </summary>
    public bool ClosesOnRequestOf(string key) => Get(key).ClosesOnRequest;

    /// <summary>
    /// The attach process images of all registered modules, in registration order, without
    /// duplicates (compared without regard to case). The game finder leaves all of them out, so
    /// another bridge's program never becomes "the game" (s10.2 E-2).
    ///
    /// <para>
    /// Without a protective shell, for the same reason as <see cref="AttachProcessImagesOf"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AllAttachProcessImages()
        => Modules
            .SelectMany(module => module.AttachProcessImages)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The folders this module wants tracked per prefix (<c>IModule.AttachStateFolders</c>), for
    /// attaching (S5.1). Without a protective shell, for the same reason as
    /// <see cref="AttachProcessImagesOf"/>.
    /// </summary>
    public IReadOnlyList<AttachStateFolder> AttachStateFoldersOf(string key)
        => Get(key).AttachStateFolders;

    /// <summary>
    /// The contributions of <b>all registered</b> modules for this game, independent of any
    /// switch (S2.2).
    ///
    /// <para>
    /// <b>What for:</b> this is the answer to "what is our share at all". S2.3 uses it to take
    /// out of Steam's value what came from us — <b>recognized, not remembered</b>. Whoever
    /// disables a module may still have its contribution standing in the field;
    /// <see cref="LaunchOptionsFor"/> would no longer find it and would leave it there forever.
    /// </para>
    /// </summary>
    public IReadOnlyList<LaunchOptionContribution> AllLaunchOptionsFor(GameProfile game)
    {
        var contributions = new List<LaunchOptionContribution>();

        foreach (var module in Modules)
        {
            contributions.AddRange(module.LaunchOptionsFor(game));
        }

        return contributions;
    }

    /// <summary>
    /// The global demand across <b>all registered</b> modules, exactly one entry per
    /// version-free key, in registration order (Spec S3.5b §3.1) — the inventory behind the
    /// <c>Dependencies</c> surface.
    ///
    /// <para>
    /// <b>Independent of the switch</b> (E-2): <see cref="ModuleKeys" /> names every requesting
    /// module, including a disabled one — unlike <see cref="PrefixComponentRequestsFor"/>, which
    /// goes through <see cref="IsEnabledFor"/> and asks per game.
    /// </para>
    ///
    /// <para>
    /// <b>Conflicting versions: the first registered wins</b> (E-3), the same rule as in
    /// <see cref="Anvil.Core.Steam.LaunchOptionsComposer"/> — the full per-game conflict rule
    /// stays there. A conflict is logged, not thrown: today there is one requesting module, and a
    /// UI for the case is not drawn.
    /// </para>
    /// </summary>
    public IReadOnlyList<ComponentDemand> ComponentDemands()
    {
        var order = new List<string>();
        var required = new Dictionary<string, PrefixComponent>(StringComparer.Ordinal);
        var keys = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var module in Modules)
        {
            foreach (var component in module.RequiredComponents)
            {
                if (!required.TryGetValue(component.Key, out var first))
                {
                    order.Add(component.Key);
                    required[component.Key] = component;
                    keys[component.Key] = [];
                }
                else if (first.Version != component.Version)
                {
                    _logger?.LogWarning(
                        "module {Module} requires {Key} {Version}, but {First} is already required — the first registered wins",
                        module.Key, component.Key, component.Version, first.Version);
                }

                if (!keys[component.Key].Contains(module.Key))
                {
                    keys[component.Key].Add(module.Key);
                }
            }
        }

        return order.Select(key => new ComponentDemand(required[key], keys[key])).ToList();
    }

    private IModule Get(string key)
        => _byKey.TryGetValue(key, out var module)
            ? module
            : throw new ArgumentException($"unknown module key: {key}", nameof(key));
}
