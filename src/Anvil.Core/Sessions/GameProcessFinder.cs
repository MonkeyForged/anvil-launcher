using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>
/// Finds the Windows pid of the game inside its container (spec s10.1): asks the container who
/// started whom and how much memory everyone holds, then applies <see cref="GameProcessChoice"/>.
///
/// <para>
/// <b>Waiting comes first</b> (s10.2 correction): a container that has not yet taken its bus
/// name answers nothing — the owner's PC test showed wmic failing with "unable to connect to any
/// of the specified bus names" half a second before the container was attachable. So the finder
/// waits for the same signal the attach starter waits for, and only then asks.
/// </para>
///
/// <para>
/// <b>Two questions, in this order, and the second only after the first answered.</b> A container
/// that cannot answer the first (Proton 8, a game that just ended) will not answer the second.
/// </para>
///
/// <para>
/// <b>"Did not find" is a result, not an error</b> — the shape of <see cref="Detacher"/>: only
/// cancellation throws; every other failure is <c>null</c> and a log line that names the query.
/// </para>
/// </summary>
public sealed class GameProcessFinder
{
    private const string ParentsQuery = "wmic";
    private const string MemoryQuery = "tasklist";

    private readonly Func<string?> _findLaunchClient;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<CommandOutput>> _run;
    private readonly Func<uint, CancellationToken, Task<AttachWait>> _waitUntilAttachable;
    private readonly ILogger? _logger;

    /// <param name="findLaunchClient">
    /// In operation <c>() =&gt; SteamLaunchClient.Find(steamRoot)</c>.
    /// </param>
    /// <param name="run">
    /// In operation <c>(info, token) =&gt; ContainerCommand.RunAsync(info, limit, token)</c>. A
    /// delegate so headless tests never start a process — the shape of <see cref="Detacher"/>.
    /// </param>
    /// <param name="waitUntilAttachable">
    /// In operation <c>AttachWaiter.WaitAsync</c> — the same wait the attach starter runs, so
    /// "the container accepts" has one definition.
    /// </param>
    public GameProcessFinder(
        Func<string?> findLaunchClient,
        Func<ProcessStartInfo, CancellationToken, Task<CommandOutput>> run,
        Func<uint, CancellationToken, Task<AttachWait>> waitUntilAttachable,
        ILogger? logger = null)
    {
        _findLaunchClient = findLaunchClient;
        _run = run;
        _waitUntilAttachable = waitUntilAttachable;
        _logger = logger;
    }

    /// <summary>
    /// The Windows pid of the game in <paramref name="appId"/>'s container, or <c>null</c>.
    /// Waits until the container accepts first; throws only on cancellation.
    /// </summary>
    /// <param name="programFileName">The game's program from Steam's launch; <c>null</c> means
    /// the container is not even asked.</param>
    /// <param name="excludedImages">Images that never count as the game (the module's own
    /// programs).</param>
    public async Task<int?> FindAsync(
        uint appId,
        string? programFileName,
        IReadOnlyList<string> excludedImages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var wait = await _waitUntilAttachable(appId, cancellationToken).ConfigureAwait(false);
        if (wait.Outcome != AttachOutcome.Attachable)
        {
            _logger?.LogWarning(
                "no game pid in appid {AppId}: the container never accepted ({Outcome})",
                appId, wait.Outcome);
            return null;
        }

        if (programFileName is null)
        {
            _logger?.LogInformation(
                "no game pid searched in appid {AppId}: Steam named no program name", appId);
            return null;
        }

        var client = _findLaunchClient();
        if (client is null)
        {
            _logger?.LogWarning(
                "no game pid searched in appid {AppId}: Steam's launch client was not found",
                appId);
            return null;
        }

        var parentsOutput = await AskAsync(
            ParentsQuery, SteamLaunchClient.ProcessParentsInfoFor(client, appId),
            appId, cancellationToken).ConfigureAwait(false);
        if (parentsOutput is null)
        {
            return null;
        }

        var parents = ProcessParentList.Parse(parentsOutput);
        if (parents.Count == 0)
        {
            LogEmptyAnswer(ParentsQuery, appId);
            return null;
        }

        var memoryOutput = await AskAsync(
            MemoryQuery, SteamLaunchClient.ProcessMemoryInfoFor(client, appId),
            appId, cancellationToken).ConfigureAwait(false);
        if (memoryOutput is null)
        {
            return null;
        }

        var memory = ProcessMemoryList.Parse(memoryOutput);
        if (memory.Count == 0)
        {
            LogEmptyAnswer(MemoryQuery, appId);
            return null;
        }

        return Choose(appId, programFileName, excludedImages, parents, memory);
    }

    private int? Choose(
        uint appId,
        string programFileName,
        IReadOnlyList<string> excludedImages,
        IReadOnlyList<(string Image, int Pid, int ParentPid)> parents,
        IReadOnlyList<(string Image, int Pid, long MemoryKilobytes)> memory)
    {
        var pid = GameProcessChoice.Choose(programFileName, excludedImages, parents, memory);
        if (pid is null)
        {
            _logger?.LogWarning(
                "no game pid in appid {AppId}: nothing of the family of {Program} is listed",
                appId, programFileName);
            return null;
        }

        var chosen = parents.First(process => process.Pid == pid);
        var kilobytes = memory.First(process => process.Pid == pid).MemoryKilobytes;
        var familySize = GameProcessChoice.FamilyOf(programFileName, excludedImages, parents).Count;
        _logger?.LogInformation(
            "game pid in appid {AppId}: program {Program}, chosen {Image} with pid {Pid}, "
            + "{Kilobytes} KB, family of {FamilySize}",
            appId, programFileName, chosen.Image, pid, kilobytes, familySize);
        return pid;
    }

    /// <summary>
    /// Runs one query; its output, or <c>null</c> when the question did not get through.
    /// Cancellation is passed through.
    /// </summary>
    private async Task<string?> AskAsync(
        string query, ProcessStartInfo info, uint appId, CancellationToken cancellationToken)
    {
        CommandOutput output;
        try
        {
            output = await _run(info, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "cannot run {Query} for appid {AppId}", query, appId);
            return null;
        }

        if (!output.Completed || output.ExitCode is not 0)
        {
            _logger?.LogWarning(
                "{Query} for appid {AppId} did not answer: completed {Completed}, exit code "
                + "{Code}, stderr {Error}",
                query, appId, output.Completed, output.ExitCode, output.StandardError.Trim());
            return null;
        }

        return output.StandardOutput;
    }

    /// <summary>
    /// Said once per query: a live container is never without processes, so an empty list means
    /// the question did not get through.
    /// </summary>
    private void LogEmptyAnswer(string query, uint appId)
        => _logger?.LogWarning(
            "no game pid in appid {AppId}: {Query} listed no process", appId, query);
}
