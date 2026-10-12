using System.Collections.Concurrent;
using Anvil.Core.Modules;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>What the grip has left at a game and module (Spec S5.1 §3.1).</summary>
public enum AttachmentState
{
    /// <summary>Nothing of this module is attached to this game — the initial state.</summary>
    NotAttached,

    /// <summary>A grip is working on it right now. The button locks on this later.</summary>
    Attaching,

    /// <summary>Attached. <see cref="Attachment.Pid"/> names the launch client.</summary>
    Attached,

    /// <summary>A grip is detaching right now.</summary>
    Detaching,
}

/// <summary>
/// The held state per game and module.
///
/// <para>
/// <b>Four values, no fifth for "failed"</b> (Spec E-4): the state follows the <b>outcome</b>,
/// not the attempt. A failed attach ends on <see cref="AttachmentState.NotAttached"/>, a failed
/// detach stays on <see cref="AttachmentState.Attached"/> — so no later card claims a state it
/// did not bring about. Where a failure becomes visible is design requirement 52.
/// </para>
/// </summary>
/// <param name="Pid">The launch client at <see cref="AttachmentState.Attached"/>, otherwise <c>null</c>.</param>
public readonly record struct Attachment(
    uint AppId, string ModuleKey, AttachmentState State, int? Pid);

/// <summary>How an attach through the grip turned out.</summary>
public enum AttachRunOutcome
{
    /// <summary>Through: seeded (if there was anything to seed) and attached.</summary>
    Attached,

    /// <summary>The module attaches nothing or cannot. <c>Reason</c> is its sentence.</summary>
    NothingToAttach,

    /// <summary>The held state already says "attached".</summary>
    AlreadyAttached,

    /// <summary>A grip is already working on this game and module.</summary>
    Busy,

    /// <summary>The template did not get into the prefix. <b>Nothing was started.</b></summary>
    SeedFailed,

    /// <summary>The starter declined; its reason travels along.</summary>
    AttachFailed,
}

/// <param name="Outcome">The outcome.</param>
/// <param name="Reason">A whole English sentence at every failure outcome, <c>null</c> otherwise.</param>
/// <param name="Seeds">What seeding yielded per folder — empty without a prefix and without folders.</param>
/// <param name="Start">The starter's result, <c>null</c> if it never got that far.</param>
public readonly record struct AttachRun(
    AttachRunOutcome Outcome,
    string? Reason,
    IReadOnlyList<StateRelay> Seeds,
    AttachStart? Start);

/// <summary>How a detach through the grip turned out.</summary>
public enum DetachRunOutcome
{
    /// <summary>Through: detached and harvested (if there was anything to harvest).</summary>
    Detached,

    /// <summary>Nothing of its own ran in the container. <b>No error</b>, but nothing to harvest either.</summary>
    NotAttached,

    /// <summary>A grip is already working on this game and module.</summary>
    Busy,

    /// <summary>The detacher declined. <b>Nothing was harvested.</b></summary>
    DetachFailed,

    /// <summary>Detached, but the template is not written.</summary>
    HarvestFailed,
}

/// <param name="Outcome">The outcome.</param>
/// <param name="Reason">A whole English sentence at every failure outcome, <c>null</c> otherwise.</param>
/// <param name="Detach">The detacher's result, <c>null</c> at <c>Busy</c>.</param>
/// <param name="Harvests">What harvesting yielded per folder — empty if nothing was harvested.</param>
public readonly record struct DetachRun(
    DetachRunOutcome Outcome,
    string? Reason,
    Detach? Detach,
    IReadOnlyList<StateRelay> Harvests);

/// <summary>
/// The grip (Spec S5.1 §3.3): <b>one</b> bracket around seeding, waiting, attaching — and, as a
/// mirror image, around detaching and harvesting. Plus the held state "attached".
///
/// <para>
/// <b>Why this is one class and not four calls at the caller:</b> four steps are four places to
/// forget. The conditions between them — harvesting only after <c>Detached</c>, seeding before
/// waiting, a failed seed starts nothing — existed until S5.1 only as a comment in the live
/// test.
/// </para>
///
/// <para>
/// <b>Whoever holds the state must know the steps</b> (project plan M5, decision 1): the grip
/// alone knows which step a game is in right now. Putting the state next to it would mean
/// reconstructing it from observations.
/// </para>
///
/// <para>
/// <b>Delegates instead of objects</b> (Spec E-9) — the house shape of
/// <see cref="AttachStarter"/> and <c>PrefixPreparer</c>. No core service takes the
/// <c>ModuleRegistry</c> as an object; the binding happens in the composition root.
/// </para>
/// </summary>
public sealed class ModuleAttacher
{
    private const string BusyReason =
        "Another attach or detach for this game is still running — wait for it to finish.";

    private const string AlreadyAttachedReason =
        "The module is already attached to this game.";

    private readonly Func<GameProfile, string, Func<CancellationToken, Task<int?>>, CancellationToken,
        Task<AttachDeclaration>> _prepare;

    private readonly Func<uint, string, CancellationToken, Task<int?>> _findGameProcessId;
    private readonly Func<string, IReadOnlyList<string>> _images;
    private readonly Func<string, IReadOnlyList<AttachStateFolder>> _stateFolders;
    private readonly Func<string, AttachStateFolder, CancellationToken, StateRelay> _seed;
    private readonly Func<string, AttachStateFolder, CancellationToken, StateRelay> _harvest;
    private readonly Func<uint, AttachProgram, CancellationToken, Task<AttachStart>> _start;
    private readonly Func<string, bool> _closesOnRequest;
    private readonly Func<uint, IReadOnlyList<string>, bool, CancellationToken, Task<Detach>> _detach;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<(uint AppId, string Key), Attachment> _held = new();
    private readonly ConcurrentDictionary<(uint AppId, string Key), byte> _running = new();

    /// <summary>
    /// One lock <b>per module</b> around seeding and harvesting (Spec E-2). The template lives in
    /// the module folder, not in the prefix — it is the same for all games. A harvest from game
    /// A that replaces the template while game B seeds from it would copy a half-written login
    /// into a prefix. The lock applies <b>only</b> around the copy operations, never around the
    /// waiting — that may take two minutes.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _templates = new();

    /// <param name="prepare">In the root <c>registry.PrepareAttachAsync</c> (the single-module form).</param>
    /// <param name="findGameProcessId">
    /// In the root <c>GameProcessFinder.FindAsync</c> bound to the game's program and the
    /// images of all modules (spec s10.1 I-4, s10.2 E-2): (appId, moduleKey) → the game's Windows pid or
    /// <c>null</c>. Only called when the module asks.
    /// </param>
    /// <param name="images">In the root <c>registry.AttachProcessImagesOf</c>.</param>
    /// <param name="stateFolders">In the root <c>registry.AttachStateFoldersOf</c>.</param>
    /// <param name="seed">In the root <c>relay.Seed</c>.</param>
    /// <param name="harvest">In the root <c>relay.Harvest</c>.</param>
    /// <param name="start">In the root <c>starter.StartAsync</c>.</param>
    /// <param name="closesOnRequest">In the root <c>registry.ClosesOnRequestOf</c>.</param>
    /// <param name="detach">In the root <c>detacher.DetachAsync</c>.</param>
    public ModuleAttacher(
        Func<GameProfile, string, Func<CancellationToken, Task<int?>>, CancellationToken,
            Task<AttachDeclaration>> prepare,
        Func<uint, string, CancellationToken, Task<int?>> findGameProcessId,
        Func<string, IReadOnlyList<string>> images,
        Func<string, IReadOnlyList<AttachStateFolder>> stateFolders,
        Func<string, AttachStateFolder, CancellationToken, StateRelay> seed,
        Func<string, AttachStateFolder, CancellationToken, StateRelay> harvest,
        Func<uint, AttachProgram, CancellationToken, Task<AttachStart>> start,
        Func<string, bool> closesOnRequest,
        Func<uint, IReadOnlyList<string>, bool, CancellationToken, Task<Detach>> detach,
        ILogger? logger = null)
    {
        _prepare = prepare;
        _findGameProcessId = findGameProcessId;
        _images = images;
        _stateFolders = stateFolders;
        _seed = seed;
        _harvest = harvest;
        _start = start;
        _closesOnRequest = closesOnRequest;
        _detach = detach;
        _logger = logger;
    }

    /// <summary>
    /// Reports every change of the held state, <b>once per change</b>. A step that does not move
    /// the state reports nothing.
    /// </summary>
    public event EventHandler<Attachment>? AttachmentChanged;

    /// <summary>
    /// The held state. A pair never touched is <see cref="AttachmentState.NotAttached"/> — not
    /// <c>null</c>, because "nothing is attached" is an answer, not a missing result.
    ///
    /// <para>
    /// <b>It says what the grip has done, not what is true in the container</b> (Spec E-10). If
    /// the program dies on its own, it is wrong until someone detaches.
    /// </para>
    /// </summary>
    public Attachment AttachmentOf(uint appId, string moduleKey)
        => _held.TryGetValue((appId, moduleKey), out var held)
            ? held
            : new Attachment(appId, moduleKey, AttachmentState.NotAttached, null);

    /// <summary>
    /// Which modules are attached to this game right now (Spec S5.4 §8.4, E-12). Only
    /// <see cref="AttachmentState.Attached"/> counts: a running grip has attached nothing yet, a
    /// detached one nothing any more.
    ///
    /// <para>
    /// The caller is <c>ModuleActionRunner.DetachAllAsync</c> — Anvil's own stop asks through it
    /// what it still has to fetch before tearing down the container.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> AttachedModulesOf(uint appId)
        => [.. _held
            .Where(entry => entry.Key.AppId == appId
                && entry.Value.State == AttachmentState.Attached)
            .Select(entry => entry.Key.Key)];

    /// <summary>
    /// Gives up what the grip holds for this module at this game — <b>without speaking into the
    /// container</b> (Spec S5.4 §8.4, E-11; per module since s10.2 E-1).
    ///
    /// <para>
    /// <b>Why this is not a detach:</b> a held attachment is a statement about a
    /// <i>container</i>. If the container is gone, the statement is void — whatever the reason
    /// and independent of any option. Measured on 2026-09-19: after <c>wineserver -k</c> the
    /// detach found no bus name two milliseconds later, truthfully reported
    /// <c>NoProcessList</c>, and the state fell back to <c>Attached</c> — on a dead PID.
    /// </para>
    ///
    /// <para>
    /// <b>Why per module and not per game:</b> the container is only gone when the last program
    /// in it is. A module that was not asked to detach may still have its program running there,
    /// and releasing it would take the user's only way back to it.
    /// </para>
    ///
    /// <para>
    /// It is reported through the same path as every other change, so that
    /// <see cref="ModuleGameStates"/> and the card follow. A pair that is already
    /// <see cref="AttachmentState.NotAttached"/> or was never touched reports nothing.
    /// </para>
    /// </summary>
    public void Release(uint appId, string moduleKey)
        => Hold(appId, moduleKey, AttachmentState.NotAttached, null);

    /// <summary>
    /// Seeds, attaches, holds the state. Throws only on cancellation; every other outcome is a
    /// result with a reason.
    ///
    /// <para>
    /// The module's question for the game's process id is bound here to this game and module
    /// (spec s10.1 I-4) — the module only hands in its token.
    /// </para>
    /// </summary>
    public async Task<AttachRun> AttachAsync(
        GameProfile game, string moduleKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var appId = game.Game.AppId;
        if (!_running.TryAdd((appId, moduleKey), 0))
        {
            return Refuse(AttachRunOutcome.Busy, BusyReason, [], null);
        }

        var settled = false;
        try
        {
            if (AttachmentOf(appId, moduleKey).State == AttachmentState.Attached)
            {
                settled = true;
                return Refuse(AttachRunOutcome.AlreadyAttached, AlreadyAttachedReason, [], null);
            }

            Hold(appId, moduleKey, AttachmentState.Attaching, null);

            // First ask what is to be started at all: the answer may do work (Wand unpacks
            // about 160 MB behind it the first time), and a seed for a module that attaches
            // nothing would be copied for nothing.
            var declaration = await _prepare(
                    game,
                    moduleKey,
                    token => _findGameProcessId(appId, moduleKey, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (declaration.State != AttachDeclarationState.Program)
            {
                settled = true;
                Hold(appId, moduleKey, AttachmentState.NotAttached, null);
                return Refuse(
                    AttachRunOutcome.NothingToAttach, declaration.Reason, [], null);
            }

            var seeds = new List<StateRelay>();
            if (game.PrefixPath is { } prefixPath)
            {
                foreach (var folder in _stateFolders(moduleKey))
                {
                    var seeded = await UnderTemplateLockAsync(
                            moduleKey, () => _seed(prefixPath, folder, cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    seeds.Add(seeded);

                    if (Failed(seeded))
                    {
                        settled = true;
                        Hold(appId, moduleKey, AttachmentState.NotAttached, null);
                        return Refuse(AttachRunOutcome.SeedFailed, seeded.Reason, seeds, null);
                    }
                }
            }

            var start = await _start(appId, declaration.Program!, cancellationToken)
                .ConfigureAwait(false);
            settled = true;

            if (start.Outcome != AttachStartOutcome.Started)
            {
                Hold(appId, moduleKey, AttachmentState.NotAttached, null);
                return Refuse(AttachRunOutcome.AttachFailed, start.Reason, seeds, start);
            }

            Hold(appId, moduleKey, AttachmentState.Attached, start.Pid);
            return new AttachRun(AttachRunOutcome.Attached, null, seeds, start);
        }
        finally
        {
            // A cancellation or a propagated exception must not leave the state on
            // "Attaching" — otherwise the button from S5.4 would be locked forever.
            if (!settled)
            {
                Hold(appId, moduleKey, AttachmentState.NotAttached, null);
            }

            _running.TryRemove((appId, moduleKey), out _);
        }
    }

    /// <summary>
    /// Detaches and harvests. Throws only on cancellation; every other outcome is a result with
    /// a reason.
    ///
    /// <para>
    /// <b>A detach without a prior attach is not rejected:</b> after a restart of Anvil nobody
    /// knows any more what runs in the container — the <see cref="Detacher"/> is the only place
    /// that can measure it.
    /// </para>
    ///
    /// <para>
    /// <b>No waiting, no second question after detaching</b> (Spec E-3):
    /// <c>DetachOutcome.Detached</c> <i>is</i> the answer to "is the container free" — that is
    /// exactly what the detacher's follow-up check waits for. The 0.2 s from the measurement of
    /// 2026-09-19 are the run time of a <c>tasklist</c> call, not a settling time.
    /// </para>
    /// </summary>
    public async Task<DetachRun> DetachAsync(
        GameProfile game, string moduleKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var appId = game.Game.AppId;
        if (!_running.TryAdd((appId, moduleKey), 0))
        {
            return Refuse(DetachRunOutcome.Busy, BusyReason, null, []);
        }

        var before = AttachmentOf(appId, moduleKey);
        var settled = false;
        try
        {
            Hold(appId, moduleKey, AttachmentState.Detaching, before.Pid);

            var detached = await _detach(
                    appId, _images(moduleKey), _closesOnRequest(moduleKey), cancellationToken)
                .ConfigureAwait(false);

            if (detached.Outcome is not (DetachOutcome.Detached or DetachOutcome.NotAttached))
            {
                settled = true;
                // Back to the state from before, not to "nothing is attached": the card should
                // never claim what it did not bring about (Spec E-4).
                Hold(appId, moduleKey, before.State, before.Pid);
                return Refuse(DetachRunOutcome.DetachFailed, detached.Reason, detached, []);
            }

            settled = true;
            Hold(appId, moduleKey, AttachmentState.NotAttached, null);

            if (detached.Outcome == DetachOutcome.NotAttached)
            {
                // Nothing of its own ran — then there is nothing to harvest either.
                return new DetachRun(DetachRunOutcome.NotAttached, null, detached, []);
            }

            var harvests = new List<StateRelay>();
            if (game.PrefixPath is { } prefixPath)
            {
                foreach (var folder in _stateFolders(moduleKey))
                {
                    var harvested = await UnderTemplateLockAsync(
                            moduleKey, () => _harvest(prefixPath, folder, cancellationToken),
                            cancellationToken)
                        .ConfigureAwait(false);
                    harvests.Add(harvested);

                    if (Failed(harvested))
                    {
                        // Detached is detached — the state stays where it is (Spec E-5).
                        return Refuse(
                            DetachRunOutcome.HarvestFailed, harvested.Reason, detached, harvests);
                    }
                }
            }

            return new DetachRun(DetachRunOutcome.Detached, null, detached, harvests);
        }
        finally
        {
            if (!settled)
            {
                Hold(appId, moduleKey, before.State, before.Pid);
            }

            _running.TryRemove((appId, moduleKey), out _);
        }
    }

    private DetachRun Refuse(
        DetachRunOutcome outcome, string? reason, Detach? detach,
        IReadOnlyList<StateRelay> harvests)
    {
        _logger?.LogWarning("detach run: {Outcome} — {Reason}", outcome, reason);
        return new DetachRun(outcome, reason, detach, harvests);
    }

    /// <summary>A copy step that went wrong — <c>NothingToCopy</c> is not one of them.</summary>
    private static bool Failed(StateRelay relay)
        => relay.Outcome is StateRelayOutcome.NoPrefixFolder or StateRelayOutcome.CopyFailed;

    private async Task<StateRelay> UnderTemplateLockAsync(
        string moduleKey, Func<StateRelay> copy, CancellationToken cancellationToken)
    {
        var gate = _templates.GetOrAdd(moduleKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return copy();
        }
        finally
        {
            gate.Release();
        }
    }

    private void Hold(uint appId, string moduleKey, AttachmentState state, int? pid)
    {
        var attachment = new Attachment(appId, moduleKey, state, pid);
        if (AttachmentOf(appId, moduleKey) == attachment)
        {
            return;
        }

        _held[(appId, moduleKey)] = attachment;
        _logger?.LogInformation(
            "attachment of {Key} at appid {AppId}: {State}", moduleKey, appId, state);
        AttachmentChanged?.Invoke(this, attachment);
    }

    private AttachRun Refuse(
        AttachRunOutcome outcome, string? reason, IReadOnlyList<StateRelay> seeds,
        AttachStart? start)
    {
        _logger?.LogWarning("attach run: {Outcome} — {Reason}", outcome, reason);
        return new AttachRun(outcome, reason, seeds, start);
    }
}
