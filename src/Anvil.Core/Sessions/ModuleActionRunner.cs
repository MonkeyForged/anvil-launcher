using Anvil.Core.Modules;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>
/// The small Core class above <see cref="ModuleAttacher"/> (Spec S5.4 §3.7): it translates a
/// <see cref="ModuleActionKind"/> into the matching call, catches the exception that an attach
/// does not throw but could throw, and writes <b>the one log line</b> with the outcome and its
/// reason.
///
/// <para>
/// <b>Three callers:</b> the button on the module card (task 8), the two <c>AttachTriggers</c>
/// (task 5), and from S5.5 the context menu.
/// </para>
///
/// <para>
/// <b>Why the log line sits here:</b> until an answer to design requirement 52 exists, a failed
/// attach or detach has no other place where it becomes visible — the card only shows the held
/// state, no reason.
/// </para>
///
/// <para>
/// <b>The <see cref="ModuleAttacher"/> stays Core-internal</b> (foundation §3.2): whoever holds
/// this runner holds the attacher, without passing it on.
/// </para>
///
/// <para>
/// <b>Why the preparation check sits here:</b> the card's button, the two <c>AttachTriggers</c>
/// and the context menu all call <see cref="RunAsync"/> for an attach — it is the one place all
/// three ways through run, so a fresh look at the prefix here covers every caller without a
/// fourth check somewhere else (Spec s8.1 E-6).
/// </para>
/// </summary>
public sealed class ModuleActionRunner(
    ModuleAttacher attacher, ILogger? logger = null, Func<string, GameProfile, bool>? isPrepared = null)
{
    private readonly ModuleAttacher _attacher = attacher;
    private readonly ILogger? _logger = logger;
    private readonly Func<string, GameProfile, bool>? _isPrepared = isPrepared;

    /// <summary>
    /// Raised when an attach was refused because the prefix is not prepared
    /// (Spec s8.1 E-5, E-6).
    /// </summary>
    public event EventHandler<ModuleGameToggle>? AttachRefused;

    /// <summary>
    /// Calls the attacher for <paramref name="kind"/> and reports whether the action went
    /// through. Never throws itself: every exception from the attacher — including a
    /// cancellation — is caught, because behind it sits foreign code and a foreign process; a
    /// propagated exception would take the caller down with it (same stance as
    /// <c>ModuleRegistry.CheckAsync</c>, foundation §3.1).
    /// </summary>
    public async Task<bool> RunAsync(
        GameProfile game, string moduleKey, ModuleActionKind kind, CancellationToken token)
    {
        try
        {
            if (kind == ModuleActionKind.Attach)
            {
                // Spec s8.1 E-5: the remembered state never looks into the prefix, and between two
                // library reads a prefix can vanish and come back empty. Only a look right now sees it.
                if (_isPrepared is { } isPrepared && !isPrepared(moduleKey, game))
                {
                    _logger?.LogInformation(
                        "attach action for {Key} at appid {AppId}: refused — the prefix is not prepared",
                        moduleKey, game.Game.AppId);
                    AttachRefused?.Invoke(this, new ModuleGameToggle(moduleKey, game.Game.AppId));
                    return false;
                }

                var run = await _attacher.AttachAsync(game, moduleKey, token).ConfigureAwait(false);
                _logger?.LogInformation(
                    "attach action for {Key} at appid {AppId}: {Outcome}{Reason}",
                    moduleKey, game.Game.AppId, run.Outcome, Tail(run.Reason));
                return run.Outcome == AttachRunOutcome.Attached;
            }

            var back = await _attacher.DetachAsync(game, moduleKey, token).ConfigureAwait(false);
            _logger?.LogInformation(
                "detach action for {Key} at appid {AppId}: {Outcome}{Reason}",
                moduleKey, game.Game.AppId, back.Outcome, Tail(back.Reason));
            return back.Outcome is DetachRunOutcome.Detached or DetachRunOutcome.NotAttached;
        }
        catch (Exception error)
        {
            _logger?.LogWarning(
                error, "the {Kind} action for {Key} at appid {AppId} failed",
                kind, moduleKey, game.Game.AppId);
            return false;
        }
    }

    /// <summary>
    /// Detaches <b>everything</b> currently attached to this game (Spec S5.4 §8.4, E-12).
    ///
    /// <para>
    /// <b>Why this sits here and not with the caller:</b> the runner already owns the
    /// translation "action → attacher call plus log line" and holds the attacher. A second place
    /// doing the same thing would be a second truth about "what is attached here".
    /// </para>
    ///
    /// <para>
    /// <b>Unconditional, not tied to <c>DetachOnExit</c>:</b> that switch says what should happen
    /// to the module at <i>game end</i>. Here, Anvil tears down the container itself — the module
    /// dies either way, and the only choice is between "harvested" and "lost". A choice between
    /// cleaning up and losing data is not a setting.
    /// </para>
    ///
    /// <para>
    /// Never throws: every individual run goes through <see cref="RunAsync"/>, which catches its
    /// own exceptions.
    /// </para>
    /// </summary>
    public async Task DetachAllAsync(GameProfile game, CancellationToken token)
    {
        var keys = _attacher.AttachedModulesOf(game.Game.AppId);
        if (keys.Count == 0)
        {
            return;
        }

        await Task.WhenAll(
                keys.Select(key => RunAsync(game, key, ModuleActionKind.Detach, token)))
            .ConfigureAwait(false);
    }

    private static string Tail(string? reason) => reason is null ? string.Empty : $" — {reason}";
}
