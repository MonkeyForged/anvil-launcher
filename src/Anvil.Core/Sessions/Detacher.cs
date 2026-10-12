using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>How a detach turned out.</summary>
public enum DetachOutcome
{
    /// <summary>No own process lives any more. <b>The gate for S4.6</b>: only after this may the
    /// login be copied back.</summary>
    Detached,

    /// <summary>The container answered, but nothing of ours ran there. Not an error.</summary>
    NotAttached,

    /// <summary>Steam's launch client was not found.</summary>
    NoLaunchClient,

    /// <summary>The container returned no process list — Proton 8, or the game is gone.</summary>
    NoProcessList,

    /// <summary>Ended, but after the deadline something of ours still lives.</summary>
    StillRunning,

    /// <summary>The <c>taskkill</c> call could not be started.</summary>
    KillFailed,
}

/// <param name="Outcome">The outcome.</param>
/// <param name="Reason">A whole English sentence for every failed outcome, <c>null</c> otherwise —
/// the same shape as <c>AttachStart</c> and <c>ModuleReadiness</c>.</param>
/// <param name="Count">How many own processes were found; for
/// <see cref="DetachOutcome.Detached"/> also how many were ended.</param>
public readonly record struct Detach(DetachOutcome Outcome, string? Reason, int Count);

/// <summary>
/// The bracket of detaching (spec S4.5 §3.4) — the mirror image of <see cref="AttachStarter"/>:
/// list, ask to close, look again, and force only what is still listed. A module whose program
/// does not close on request (<c>IModule.ClosesOnRequest</c>) is not asked: it is forced at once.
///
/// <para>
/// <b>Ours is whatever image name the module names</b> (E-1). The second half of the selection
/// rule is the bus name: the container is per game, and whoever speaks through the bus name of
/// 2114740 cannot reach another game at all. <c>explorer.exe</c> therefore never belongs to it —
/// under Proton the game itself often runs beneath it (E-8).
/// </para>
///
/// <para>
/// <b>Ask first, force later</b> (s9.2). A module is first asked to close (<c>taskkill</c>
/// without <c>/F</c>); only what is still listed after <see cref="CloseGrace"/> is forced.
/// Measured: Cheat Engine closes on the request within 10 s and the game survives; <c>/F</c>
/// with its debugger attached takes the game along.
/// </para>
///
/// <para>
/// <b>The verdict comes from looking again, never from a return value.</b> Ending the main
/// process takes the family along; the other pids report <c>Could not find process</c>
/// afterwards, and <c>taskkill</c> returns an error code for that which says nothing about
/// success (messungen-anhaengen.md §3).
/// </para>
///
/// <para>
/// <b>No signals from outside</b> (project plan M4, decision 6): measured twice, ended
/// differently twice — once the game survived the SIGTERM to Wand, once it ended in the same
/// moment. All work happens inside the container.
/// </para>
/// </summary>
public sealed class Detacher
{
    /// <summary>How long the watch runs after the forced kill (E-9).</summary>
    private static readonly TimeSpan DefaultWatchWindow = TimeSpan.FromSeconds(10);

    /// <summary>The pace of both watches.</summary>
    private static readonly TimeSpan DefaultWatchTick = TimeSpan.FromMilliseconds(500);

    /// <summary>The deadline of a single call in the container.</summary>
    private static readonly TimeSpan DefaultCommandLimit = TimeSpan.FromSeconds(30);

    /// <summary>How long a polite request gets before the force (s9.2).</summary>
    private static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(15);

    private const string NoProcessListReason =
        "Could not read the process list in the game's container — is the game still running?";

    private readonly Func<string?> _findLaunchClient;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CommandOutput>> _run;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly TimeSpan _watchWindow;
    private readonly TimeSpan _watchTick;
    private readonly TimeSpan _closeGrace;

    /// <param name="findLaunchClient">
    /// In operation <c>() =&gt; SteamLaunchClient.Find(steamRoot)</c>.
    /// </param>
    /// <param name="run">
    /// In operation <c>(info, token) =&gt; ContainerCommand.RunAsync(info, limit, token)</c>. A
    /// delegate so headless tests never start a process — the shape of
    /// <see cref="AttachStarter"/>.
    /// </param>
    /// <param name="watchWindow">The watch after the forced kill; default 10 s.</param>
    /// <param name="watchTick">The pace of both watches; default 500 ms.</param>
    /// <param name="closeGrace">The polite stage; default <see cref="CloseGrace"/>.</param>
    public Detacher(
        Func<string?> findLaunchClient,
        Func<ProcessStartInfo, CancellationToken, Task<CommandOutput>> run,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? watchWindow = null,
        TimeSpan? watchTick = null,
        TimeSpan? closeGrace = null)
    {
        _findLaunchClient = findLaunchClient;
        _run = run;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _watchWindow = watchWindow ?? DefaultWatchWindow;
        _watchTick = watchTick ?? DefaultWatchTick;
        _closeGrace = closeGrace ?? DefaultCloseGrace;
    }

    /// <summary>The deadline of a single call — public so operation can pass it on.</summary>
    public static TimeSpan CommandLimit => DefaultCommandLimit;

    /// <summary>How long the polite request gets by default before the force: 15 s.</summary>
    public static TimeSpan CloseGrace => DefaultCloseGrace;

    /// <summary>
    /// Detaches the own processes of <paramref name="appId"/>. Throws only on cancellation; every
    /// other outcome is a result with a reason.
    /// </summary>
    /// <param name="images">The image names belonging to the caller
    /// (<c>IModule.AttachProcessImages</c>). Empty means: nothing to do.</param>
    /// <param name="closesOnRequest"><c>IModule.ClosesOnRequest</c>. <c>false</c> skips the polite
    /// stage and forces at once (s10.2 E-5).</param>
    public async Task<Detach> DetachAsync(
        uint appId,
        IReadOnlyList<string> images,
        bool closesOnRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var client = _findLaunchClient();
        if (client is null)
        {
            return Refuse(
                DetachOutcome.NoLaunchClient,
                "Steam's launch client was not found — is Steam installed?");
        }

        var listing = await ListAsync(client, appId, cancellationToken).ConfigureAwait(false);
        if (listing is null)
        {
            return Refuse(DetachOutcome.NoProcessList, NoProcessListReason);
        }

        var ours = Ours(listing, images);
        if (ours.Count == 0)
        {
            _logger?.LogInformation("nothing of ours runs in appid {AppId}", appId);
            return new Detach(DetachOutcome.NotAttached, null, 0);
        }

        if (!closesOnRequest)
        {
            // Asking would only burn the grace: such a program hides its window and keeps
            // running (Wand, measured in s9.2 and on 2026-10-02).
            _logger?.LogInformation(
                "forcing {Count} process(es) in appid {AppId} without asking: the program does "
                + "not close on request",
                ours.Count, appId);
            return await KillAndWatchAsync(
                    client, appId, images, ours, ours.Count, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await TryKillAsync(client, appId, ours, force: false, cancellationToken)
                .ConfigureAwait(false))
        {
            return RefuseKill();
        }

        _logger?.LogInformation(
            "asked {Count} process(es) in appid {AppId} to close", ours.Count, appId);
        var asked = _time.GetUtcNow();

        var left = await WatchAsync(client, appId, images, _closeGrace, cancellationToken)
            .ConfigureAwait(false);
        if (left is null)
        {
            return Refuse(DetachOutcome.NoProcessList, NoProcessListReason);
        }

        if (left.Count == 0)
        {
            _logger?.LogInformation(
                "closed on request in appid {AppId} after {Seconds:0.0} s",
                appId, (_time.GetUtcNow() - asked).TotalSeconds);
            return new Detach(DetachOutcome.Detached, null, ours.Count);
        }

        _logger?.LogInformation(
            "still running in appid {AppId} after {Grace} s, forcing {Count} process(es)",
            appId, _closeGrace.TotalSeconds, left.Count);
        return await KillAndWatchAsync(
                client, appId, images, left, ours.Count, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The forced stage: ends <paramref name="targets"/> with <c>/F</c> and watches them go.
    /// Reached after the grace, or at once for a program that does not close on request.
    /// </summary>
    /// <param name="targets">The own processes to end — all of them, or what is still listed at
    /// the end of the grace.</param>
    /// <param name="found">How many own processes the first listing found — the reported
    /// count.</param>
    private async Task<Detach> KillAndWatchAsync(
        string client,
        uint appId,
        IReadOnlyList<string> images,
        IReadOnlyList<ContainerProcess> targets,
        int found,
        CancellationToken cancellationToken)
    {
        if (!await TryKillAsync(client, appId, targets, force: true, cancellationToken)
                .ConfigureAwait(false))
        {
            return RefuseKill();
        }

        var survivors = await WatchAsync(client, appId, images, _watchWindow, cancellationToken)
            .ConfigureAwait(false);
        if (survivors is null)
        {
            return Refuse(DetachOutcome.NoProcessList, NoProcessListReason);
        }

        if (survivors.Count > 0)
        {
            return Refuse(
                DetachOutcome.StillRunning,
                $"{survivors.Count} process(es) are still running in the container — "
                + "see the log for details.");
        }

        _logger?.LogInformation("detached {Count} process(es) from appid {AppId}", found, appId);
        return new Detach(DetachOutcome.Detached, null, found);
    }

    /// <summary>
    /// Runs <c>taskkill</c> for <paramref name="processes"/>; <c>false</c> when the call could not
    /// be started. Cancellation is passed through.
    /// </summary>
    private async Task<bool> TryKillAsync(
        string client,
        uint appId,
        IReadOnlyList<ContainerProcess> processes,
        bool force,
        CancellationToken cancellationToken)
    {
        try
        {
            var kill = await _run(
                    SteamLaunchClient.TaskkillInfoFor(
                        client, appId, [.. processes.Select(one => one.Pid)], force),
                    cancellationToken)
                .ConfigureAwait(false);

            // The return value goes to the log and nowhere else: the family already died with
            // the main process, the other pids report "does not exist".
            _logger?.LogInformation(
                "taskkill for appid {AppId} (force {Force}): completed {Completed}, "
                + "exit code {Code}",
                appId, force, kill.Completed, kill.ExitCode);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "cannot run taskkill for appid {AppId}", appId);
            return false;
        }
    }

    /// <summary>
    /// Watches the own processes go, one listing per tick, until none is left or
    /// <paramref name="window"/> has passed. Returns the own processes still listed — empty when
    /// all are gone — or <c>null</c> when a listing failed. Measured, the family is usually gone
    /// on the first look; the window is margin for a hanging Electron process (E-9).
    /// </summary>
    private async Task<IReadOnlyList<ContainerProcess>?> WatchAsync(
        string client,
        uint appId,
        IReadOnlyList<string> images,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + window;
        using var timer = new PeriodicTimer(_watchTick, _time);

        while (true)
        {
            var listing = await ListAsync(client, appId, cancellationToken).ConfigureAwait(false);
            if (listing is null)
            {
                return null;
            }

            var left = Ours(listing, images);
            if (left.Count == 0 || _time.GetUtcNow() >= deadline)
            {
                return left;
            }

            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the container for its process list. <c>null</c> means <b>"the question did not get
    /// through"</b> — not "nothing runs": a live container always carries
    /// <c>services.exe</c>, <c>explorer.exe</c> and the game (E-2).
    /// </summary>
    private async Task<IReadOnlyList<ContainerProcess>?> ListAsync(
        string client, uint appId, CancellationToken cancellationToken)
    {
        CommandOutput output;
        try
        {
            output = await _run(SteamLaunchClient.TasklistInfoFor(client, appId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "cannot run tasklist for appid {AppId}", appId);
            return null;
        }

        if (!output.Completed || output.ExitCode is not 0)
        {
            _logger?.LogWarning(
                "tasklist for appid {AppId} did not answer: completed {Completed}, exit code "
                + "{Code}, stderr {Error}",
                appId, output.Completed, output.ExitCode, output.StandardError.Trim());
            return null;
        }

        var processes = ContainerProcesses.Parse(output.StandardOutput);
        if (processes.Count == 0)
        {
            _logger?.LogWarning(
                "tasklist for appid {AppId} listed no process at all — a live container is never "
                + "empty, so the question did not get through", appId);
            return null;
        }

        return processes;
    }

    private static IReadOnlyList<ContainerProcess> Ours(
        IReadOnlyList<ContainerProcess> processes, IReadOnlyList<string> images)
        => [.. processes.Where(
            process => images.Any(
                image => string.Equals(image, process.Image, StringComparison.OrdinalIgnoreCase)))];

    private Detach RefuseKill()
        => Refuse(
            DetachOutcome.KillFailed,
            "The launch client could not be started — see the log for details.");

    private Detach Refuse(DetachOutcome outcome, string reason)
    {
        _logger?.LogWarning("detach refused: {Outcome} — {Reason}", outcome, reason);
        return new Detach(outcome, reason, 0);
    }
}
