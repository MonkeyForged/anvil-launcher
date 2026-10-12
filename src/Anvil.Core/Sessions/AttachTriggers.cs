using Anvil.Core.Modules;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>
/// The two triggers (Spec S5.4 §3.8): they turn the two per-game switches into a promise that
/// holds. The service hangs on the <b>one</b> truth about "what is running" — no second loop,
/// no clock of its own (<c>architecture.md</c> §8), the same shape as
/// <see cref="ModuleGameStates"/>: subscribed in the constructor, never unsubscribed, because
/// the sources live as long as the application.
///
/// <para><b>The rule in two lines:</b></para>
/// <list type="table">
///   <listheader>
///     <term>Event</term>
///     <description>Condition → consequence</description>
///   </listheader>
///   <item>
///     <term><see cref="GameSessionWatcher.SessionStarted"/></term>
///     <description>
///       <c>AttachOnLaunch</c> saved on <b>and</b> <c>StateOf(appId, key).Status ==
///       Ready</c> → <c>RunAsync(…, Attach)</c>
///     </description>
///   </item>
///   <item>
///     <term><see cref="GameSessionWatcher.SessionEnded"/></term>
///     <description>
///       <c>DetachOnExit</c> saved on <b>and</b> <c>AttachmentOf(appId, key).State ==
///       Attached</c> → <c>RunAsync(…, Detach)</c>; <b>afterwards</b>
///       <see cref="ModuleAttacher.Release"/> for each module the detach was tried for
///     </description>
///   </item>
/// </list>
///
/// <para>
/// <b>Why <c>Status == Ready</c> and not <c>CanAttach</c></b> (Spec E-9): <c>CanAttach</c>
/// contains "the game is running", and whether the state source has already processed the same
/// watcher message depends on the order of the listeners. <c>Ready</c> does <b>not</b> depend on
/// the session — it means "on, possible, ready to go, nothing attached" and already holds
/// before the game starts.
/// </para>
///
/// <para>
/// <b>Why the <see cref="ModuleAttacher"/> is asked when detaching, not the state</b> (Spec
/// E-9): at the end of the game <c>Attached</c> falls back to <c>Ready</c> in the verdict as
/// soon as the source has processed the message — the answer would again depend on the order.
/// The held state of the grip is the source <see cref="ModuleGameStates"/> itself reads from;
/// asking it is not a second truth but the first.
/// </para>
///
/// <para>
/// <b>Why not on <see cref="ModuleGameStates.StateChanged"/></b>: after a detach triggered by
/// hand, <c>CanAttach</c> becomes true again while the game keeps running. A trigger on that
/// edge would attach again at once and make the <c>DETACH WAND</c> button unusable.
/// </para>
///
/// <para>
/// <b>Why releasing hangs on detaching and not on the session end</b> (Spec S5.4a §3.5): until
/// S5.4a "when the session ends, the container is gone" held — E-11, the unconditional release,
/// followed from it. Since the end of the game hangs on the game's program, the session ends
/// <i>while</i> the bracket stands and the attached program runs. A released grip would then
/// take the user's only way back. If the switch is set, the module is detached and released —
/// also when the detach failed: the state a failed attempt restores has to go (the core of
/// E-11, unchanged).
/// </para>
///
/// <para>
/// The case E-11 was built for — Anvil's stop tears the container down with
/// <c>wineserver -k</c> — stays covered: there the stop detaches by itself <b>before</b> the
/// teardown since E-12, and the grip is free before the session end is even reported.
/// </para>
/// </summary>
public sealed class AttachTriggers
{
    private readonly ModuleRegistry _registry;
    private readonly ModuleGameStates _states;
    private readonly ModuleAttacher _attacher;
    private readonly ModuleGameOptions _options;
    private readonly ModuleActionRunner _runner;
    private readonly Func<uint, GameProfile?> _profileOf;
    private readonly ILogger? _logger;

    /// <param name="profileOf">
    /// The profile for an app id, or <c>null</c>. <b>A delegate, not an object</b>
    /// (<c>architecture.md</c> §4.1): a rescan replaces the library, and whoever held it as an
    /// object would send the grip off with a stale profile afterwards.
    /// </param>
    /// <param name="logger">The caller decides the category; <c>null</c> means silent.</param>
    public AttachTriggers(
        GameSessionWatcher watcher,
        ModuleRegistry registry,
        ModuleGameStates states,
        ModuleAttacher attacher,
        ModuleGameOptions options,
        ModuleActionRunner runner,
        Func<uint, GameProfile?> profileOf,
        ILogger? logger = null)
    {
        _registry = registry;
        _states = states;
        _attacher = attacher;
        _options = options;
        _runner = runner;
        _profileOf = profileOf;
        _logger = logger;

        watcher.SessionStarted += (_, session)
            => Pending = Fire(session.AppId, ModuleActionKind.Attach);
        watcher.SessionEnded += (_, session) => Pending = EndSession(session.AppId);
    }

    /// <summary>
    /// The run sent off last, or <see cref="Task.CompletedTask"/>. <b>It is not awaited</b> —
    /// waiting inside the event would stop the watcher's clock.
    ///
    /// <para>
    /// <b>Not a test hook:</b> the trigger sends off and does not watch, so "is anything of mine
    /// running right now" is a real question that only this class can answer. That the tests ask
    /// it does not make it an invention for the tests.
    /// </para>
    /// </summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Collects the modules that want this kind at this game and sends them off together.
    /// Returns the keys of the modules that were sent off — who may be released at the end of
    /// the game depends on exactly this set.
    ///
    /// <para>
    /// <b>What the grip throws is caught by the <see cref="ModuleActionRunner"/></b> — that is
    /// where foreign code and a foreign process sit. An exception passed through would tear the
    /// watcher's clock down with it, because an event runs on its thread.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> Fire(uint appId, ModuleActionKind kind)
    {
        if (_profileOf(appId) is not { } game)
        {
            return [];
        }

        // Only modules that apply per game: a core module has neither switch nor state, and
        // ModuleGameStates.StateOf would reject it as a programming error.
        var due = _registry.Modules
            .Where(module => module.IsPerGame && Wants(appId, module.Key, kind))
            .ToList();

        if (due.Count == 0)
        {
            return [];
        }

        _logger?.LogDebug(
            "the session trigger fires {Kind} at appid {AppId} for {Keys}",
            kind, appId, string.Join(", ", due.Select(module => module.Key)));

        await Task.WhenAll(due.Select(module =>
            _runner.RunAsync(game, module.Key, kind, CancellationToken.None)))
            .ConfigureAwait(false);

        return [.. due.Select(module => module.Key)];
    }

    /// <summary>
    /// The end of the game: first the attempt to detach, then — <b>per module</b>, for exactly
    /// the modules the detach was tried for — the release (Spec S5.4a §3.5, E-11; per module
    /// since s10.2 E-1).
    ///
    /// <para>
    /// <b>Per module because another module's program can keep the container alive:</b> a
    /// module that was not asked to detach stays held, and the user keeps their way back to it.
    /// </para>
    ///
    /// <para>
    /// <b>Released is whatever was detached</b> — even if the attempt fails: the state a failed
    /// detach restores has to go, otherwise the card would stay on a dead pid and the autostart
    /// would no longer take hold at the next game start (measurement of 2026-09-19, 15:25:44).
    /// If the switch is not set, nothing is touched and the grip stays as it was — Wand keeps
    /// hanging while the bracket still stands.
    /// </para>
    ///
    /// <para>
    /// <b>The order is intentional:</b> a detach that still succeeds after the session end is
    /// not prevented by it — that case is <b>unmeasured</b>, and a rule that excluded it would
    /// be a guess. The release has the last word, though: it only runs once the attempt is
    /// through, and it also clears the state a failed detach restored.
    /// </para>
    /// </summary>
    private async Task EndSession(uint appId)
    {
        var detached = await Fire(appId, ModuleActionKind.Detach).ConfigureAwait(false);
        foreach (var key in detached)
        {
            _attacher.Release(appId, key);
        }
    }

    /// <summary>
    /// The two conditions from the table. Both are insensitive to the delivery order of the
    /// events — exactly the trap a trigger otherwise steps into: silently, without an error
    /// message (Spec E-9).
    /// </summary>
    private bool Wants(uint appId, string key, ModuleActionKind kind) => kind switch
    {
        ModuleActionKind.Attach =>
            _options.Saved(key, appId, ModuleGameOption.AttachOnLaunch)
            && _states.StateOf(appId, key).Status == ModuleGameStatus.Ready,

        _ => _options.Saved(key, appId, ModuleGameOption.DetachOnExit)
            && _attacher.AttachmentOf(appId, key).State == AttachmentState.Attached,
    };
}
