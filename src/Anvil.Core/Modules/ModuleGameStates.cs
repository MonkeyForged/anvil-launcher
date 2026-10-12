using Anvil.Core.Prefixes;
using Anvil.Core.Sessions;
using Anvil.Core.Steam;

namespace Anvil.Core.Modules;

/// <summary>
/// The one truth about "how does this module stand on this game" (Spec S5.2).
///
/// <para>
/// <b>Why one place:</b> the chip (S5.3) and the module card's badge (S5.4) answer the same
/// question. Two places each judging on their own would be two answers — and they would diverge
/// exactly when it matters. The finding in <see cref="ModuleRegistry"/> carries the same
/// reasoning.
/// </para>
///
/// <para>
/// <b>No clock of its own.</b> Every ingredient reports itself: the registry its switches and
/// its finding, the attacher its attaching, the watcher a session's start and end. The profile
/// comes as a delegate, because a rescan replaces it — what it changes arrives via
/// <see cref="Refresh"/>.
/// </para>
/// </summary>
public sealed class ModuleGameStates
{
    private readonly ModuleRegistry _registry;
    private readonly ModuleAttacher _attacher;
    private readonly GameSessionWatcher _watcher;
    private readonly Func<uint, GameProfile?> _profileOf;
    private readonly Dictionary<string, IModule> _modules;

    // The lock covers the state, never the notifying: a foreign listener under the lock would
    // hold up the next change. Same pattern as in GameSessionWatcher.
    private readonly Lock _gate = new();
    private readonly Dictionary<(uint AppId, string Key), ModuleGameState> _held = [];

    /// <param name="profileOf">
    /// The profile for an app id, or <c>null</c>. <b>Delegate, not an object</b>
    /// (architecture.md §4.1): a rescan replaces the library, and whoever held it as an object
    /// would show stale values afterward.
    /// </param>
    public ModuleGameStates(
        ModuleRegistry registry,
        ModuleAttacher attacher,
        GameSessionWatcher watcher,
        Func<uint, GameProfile?> profileOf)
    {
        _registry = registry;
        _attacher = attacher;
        _watcher = watcher;
        _profileOf = profileOf;
        _modules = registry.Modules.ToDictionary(module => module.Key, StringComparer.Ordinal);

        // No second loop, no clock of its own: every ingredient reports itself.
        // Never unsubscribed — the source lives as long as the application does (E-9).
        registry.EnabledChanged += (_, key) => RecomputeModule(key);
        registry.ReadinessChanged += (_, key) => RecomputeModule(key);
        registry.EnabledForGameChanged += (_, toggle) => RecomputeOne(toggle.AppId, toggle.Key);
        attacher.AttachmentChanged += (_, attachment)
            => RecomputeOne(attachment.AppId, attachment.ModuleKey);
        watcher.SessionStarted += (_, session) => RecomputeGame(session.AppId);
        watcher.SessionEnded += (_, session) => RecomputeGame(session.AppId);
    }

    /// <summary>
    /// The verdict for a pair. Computed and remembered on first ask; after that it comes from
    /// memory, until a source reports in.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The key belongs to no registered module, or to one that does not apply per game. Both are
    /// programming errors: a core module has neither card nor chip, and a made-up answer would
    /// shift the error into the UI.
    /// </exception>
    public ModuleGameState StateOf(uint appId, string moduleKey)
    {
        var module = ModuleFor(moduleKey);

        lock (_gate)
        {
            if (_held.TryGetValue((appId, moduleKey), out var known))
            {
                return known;
            }
        }

        // Computed outside the lock: the verdict reads a file (task 2), and no other thread may
        // wait while that happens. If two compute at once, the same result comes out twice.
        var state = Compute(appId, module);
        lock (_gate)
        {
            _held[(appId, moduleKey)] = state;
        }

        return state;
    }

    /// <summary>
    /// Notifies <b>every real change once</b>. Whoever recomputes and finds the same value
    /// notifies nothing.
    ///
    /// <para>
    /// Notified on the thread of the triggering source — switching to the UI thread is the UI's
    /// own business (house style since S3.3).
    /// </para>
    /// </summary>
    public event EventHandler<ModuleGameState>? StateChanged;

    /// <summary>
    /// Recomputes every remembered verdict and notifies what changed.
    /// <b>What for:</b> after a rescan of the library (from "launch once" to <c>Ready</c>, once
    /// the first launch has created the prefix) and after saving (a prepared prefix changes the
    /// mark on disk). Nobody notifies of either today; whoever rescans calls this (Spec §7, E-1).
    /// </summary>
    public void Refresh() => Recompute(_ => true);

    private void RecomputeOne(uint appId, string moduleKey)
        => Recompute(pair => pair.AppId == appId && pair.Key == moduleKey);

    private void RecomputeModule(string moduleKey)
        => Recompute(pair => pair.Key == moduleKey);

    private void RecomputeGame(uint appId) => Recompute(pair => pair.AppId == appId);

    /// <summary>
    /// Recompute, store, notify. <b>Only remembered pairs</b>: what nobody has ever asked also
    /// has no listener.
    /// </summary>
    private void Recompute(Func<(uint AppId, string Key), bool> touched)
    {
        (uint AppId, string Key)[] pairs;
        lock (_gate)
        {
            pairs = [.. _held.Keys.Where(touched)];
        }

        var changed = new List<ModuleGameState>();
        foreach (var pair in pairs)
        {
            // A module that no longer exists cannot be sitting in memory: the list of modules
            // has been fixed since the registry was built.
            var state = Compute(pair.AppId, _modules[pair.Key]);
            lock (_gate)
            {
                if (_held.TryGetValue(pair, out var known) && known == state)
                {
                    continue;
                }

                _held[pair] = state;
            }

            changed.Add(state);
        }

        // Notified outside the lock: a foreign listener under the lock would hold up the next
        // change.
        foreach (var state in changed)
        {
            StateChanged?.Invoke(this, state);
        }
    }

    private IModule ModuleFor(string moduleKey)
    {
        if (!_modules.TryGetValue(moduleKey, out var module))
        {
            throw new ArgumentException(
                $"No module is registered under the key '{moduleKey}'.", nameof(moduleKey));
        }

        if (!module.IsPerGame)
        {
            throw new ArgumentException(
                $"Module '{moduleKey}' does not apply per game.", nameof(moduleKey));
        }

        return module;
    }

    /// <summary>
    /// The rule, in one piece (Spec §3.5) — first matching line wins: profile unknown,
    /// <see cref="ModuleRegistry.IsPossibleFor"/>, downgraded, readiness, switch, attached, ready.
    ///
    /// <para>
    /// <b>Structure before readiness:</b> "cannot work" (native, no prefix, downgraded) is a
    /// property of game and module and stays true whether or not the module is currently ready —
    /// "not ready", by contrast, is a property of the module alone. The card has judged this the
    /// same way since S3.9: <c>IsPossible</c> does not ask readiness at all.
    /// </para>
    /// </summary>
    private ModuleGameState Compute(uint appId, IModule module)
    {
        var key = module.Key;
        var attachment = _attacher.AttachmentOf(appId, key);
        var busy = attachment.State
            is AttachmentState.Attaching or AttachmentState.Detaching;

        ModuleGameState Blocked(ModuleGameBlock block)
            => new(appId, key, ModuleGameStatus.NotPossible, block, false, busy, null);

        ModuleGameState Off()
            => new(appId, key, ModuleGameStatus.NotEnabled, null, false, busy, null);

        if (_profileOf(appId) is not { } game)
        {
            return Off();
        }

        if (!ModuleRegistry.IsPossibleFor(module, game))
        {
            return Blocked(BlockOf(game));
        }

        if (ModuleRegistry.IsMovedBackFor(module, game))
        {
            return Blocked(ModuleGameBlock.MovedBack);
        }

        // null means "currently being checked" — not a fourth state, but the absence of a
        // result. As long as nothing is settled, the card carries no chip.
        if (_registry.ReadinessOf(key) is not { IsReady: true })
        {
            return Off();
        }

        if (!_registry.IsEnabled(key) || !_registry.IsEnabledFor(key, appId))
        {
            return Off();
        }

        var running = _watcher.IsRunning(appId);
        var attached = attachment.State
            is AttachmentState.Attached or AttachmentState.Detaching;

        // "Something is attached" is a statement about the attach, not about the game (Spec
        // S5.4a §3.6). Until S5.4a the two coincided; since the game's end depends on the
        // game's own process, the container may still be standing after the end — and then Wand
        // really is still attached, and the card must offer the way back.
        if (attached)
        {
            return new(appId, key, ModuleGameStatus.Attached, null, false, busy, attachment.Pid);
        }

        return new(
            appId, key, ModuleGameStatus.Ready, null, running && !busy, busy, null);
    }

    /// <summary>
    /// The reason, literally the module card's since S3.9: <c>NotPossible</c> is native,
    /// <c>Missing</c> on an unknown platform is unknown, otherwise never launched.
    /// </summary>
    private static ModuleGameBlock BlockOf(GameProfile game)
        => PrefixReport.Of(game).State == PrefixState.NotPossible
            ? ModuleGameBlock.NativeGame
            : game.Platform == GamePlatform.Unknown
                ? ModuleGameBlock.UnknownPlatform
                : ModuleGameBlock.NeverStarted;
}
