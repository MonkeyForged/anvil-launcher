using System.Diagnostics;
using Anvil.Core.Prefixes;
using Anvil.Core.Processes;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Installers;

/// <summary>
/// Runs a foreign installer with Proton's Wine inside <c>bubblewrap</c>, in a throwaway prefix,
/// and hands out the one folder it was asked for (spec s9.3, E-7; architecture §6.2, stage 1).
///
/// <para>
/// <b>Two cages, one after the other.</b> The first creates the prefix (<c>wineboot -i</c>) and
/// waits for the wineserver; only a <c>system.reg</c> afterwards proves a prefix exists. The
/// second runs the installer and again waits for the wineserver. Exit codes say nothing — a Wine
/// installer may return 0 while its setup keeps working — so they reach the log and nowhere else;
/// what counts is what lies in the prefix afterwards.
/// </para>
///
/// <para>
/// <b>The wait happens inside the cage.</b> The cage has its own process namespace, so a
/// <c>wineserver -w</c> started outside could not see the server inside; the shell in the cage
/// runs both. When the shell ends, the namespace ends, and every Wine process with it.
/// </para>
///
/// <para>
/// <b>Nothing is left behind.</b> Prefix and cage home are deleted in the <c>finally</c>, whatever
/// the outcome; a copy that fails halfway removes the destination again.
/// </para>
/// </summary>
public sealed class CagedInstaller
{
    private const string Shell = "/bin/sh";
    private const string PrefixFolderName = "prefix";

    // "$0" is wine, "$1" the wineserver, "$2" the installer.
    private const string CreatePrefixScript = "\"$0\" wineboot -i; \"$1\" -w";
    private const string RunInstallerScript = "\"$0\" \"$2\"; \"$1\" -w";

    private static readonly TimeSpan PrefixTimeout = TimeSpan.FromMinutes(5);

    private readonly Func<string?> _findBubblewrap;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Func<string, bool> _exists;
    private readonly ILogger? _logger;

    /// <param name="findBubblewrap">At runtime <c>() =&gt; Bubblewrap.Find(PATH)</c>.</param>
    /// <param name="runner">Starts the cages.</param>
    /// <param name="environment">
    /// The base environment of every cage, at runtime <see cref="ProtonWine.Current"/>. Handed in,
    /// never read here, so the tests control it.
    /// </param>
    /// <param name="exists">File or folder existence, for <see cref="CageLayout.ForHost"/>.</param>
    /// <param name="logger">Null means silent.</param>
    public CagedInstaller(
        Func<string?> findBubblewrap,
        IProcessRunner runner,
        IReadOnlyDictionary<string, string> environment,
        Func<string, bool> exists,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(findBubblewrap);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(exists);

        _findBubblewrap = findBubblewrap;
        _runner = runner;
        _environment = environment;
        _exists = exists;
        _logger = logger;
    }

    /// <summary>
    /// Runs the installer of <paramref name="request"/> caged and copies the kept folder to its
    /// destination. Never throws, except <see cref="ArgumentException"/> for a malformed request.
    /// </summary>
    public async Task<CagedInstallResult> RunAsync(
        CagedInstallRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        var bubblewrap = _findBubblewrap();
        if (bubblewrap is null)
        {
            return Report(new(CagedInstallOutcome.NoBubblewrap, "bwrap was not found on PATH."));
        }

        var wine = ProtonWine.Find(request.Proton);
        var server = ProtonWine.FindServer(request.Proton);
        if (wine is null || server is null)
        {
            return Report(new(
                CagedInstallOutcome.NoWine, $"{request.Proton.Name} has no wine or no wineserver."));
        }

        _logger?.LogInformation("caged install with {Proton}", request.Proton.Name);

        var prefix = Path.Combine(request.WorkFolder, PrefixFolderName);
        var layout = CageLayout.ForHost(request.WorkFolder, request.Proton.Path, _environment, _exists);
        var cage = new Cage(
            bubblewrap, Bubblewrap.CageArguments(layout), ProtonWine.Environment(_environment, prefix));

        try
        {
            // A prefix left by a crashed earlier run would pass the system.reg check unearned.
            RemoveThrowawayFolders(prefix, layout.HomeFolder);
            Directory.CreateDirectory(layout.HomeFolder);

            return Report(await InstallAsync(request, cage, wine, server, prefix, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return Report(new(CagedInstallOutcome.Canceled, string.Empty));
        }
        catch (Exception exception)
        {
            // The type only: an exception message may carry an absolute home path.
            return Report(new(
                CagedInstallOutcome.Failed,
                $"The caged install failed with {exception.GetType().Name}."));
        }
        finally
        {
            RemoveThrowawayFolders(prefix, layout.HomeFolder);
            _logger?.LogInformation("caged install cleaned up");
        }
    }

    /// <summary>
    /// The first selectable tool of <paramref name="catalog"/> (sorted by name) that carries
    /// Proton's Wine; <c>null</c> if none does (spec E-6).
    /// </summary>
    public static CompatTool? ChooseProton(CompatToolCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return catalog.Selectable.FirstOrDefault(tool => ProtonWine.Find(tool) is not null);
    }

    private static void Validate(CagedInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Proton, nameof(request));

        if (string.IsNullOrWhiteSpace(request.KeepFolder)
            || Path.IsPathRooted(request.KeepFolder)
            || request.KeepFolder.Split('/').Contains(".."))
        {
            throw new ArgumentException(
                "The kept folder must be a relative path inside drive_c without \"..\".", nameof(request));
        }

        if (File.Exists(request.Destination) || Directory.Exists(request.Destination))
        {
            throw new ArgumentException("The destination already exists.", nameof(request));
        }

        if (!File.Exists(request.InstallerPath))
        {
            throw new ArgumentException("The installer file does not exist.", nameof(request));
        }
    }

    private async Task<CagedInstallResult> InstallAsync(
        CagedInstallRequest request,
        Cage cage,
        string wine,
        string server,
        string prefix,
        CancellationToken cancellationToken)
    {
        var created = await RunCageAsync(
            cage, [CreatePrefixScript, wine, server], PrefixTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!created.Completed)
        {
            return NotFinished("creating the prefix", cancellationToken);
        }

        if (!File.Exists(Path.Combine(prefix, "system.reg")))
        {
            return new(
                CagedInstallOutcome.PrefixNotCreated,
                $"wineboot left no system.reg in the prefix (exit code {created.ExitCode}).");
        }

        _logger?.LogInformation("prefix created");

        // No time limit: the user clicks through the installer, and Cancel is the way out.
        var startedAt = Stopwatch.GetTimestamp();
        var installed = await RunCageAsync(
            cage,
            [RunInstallerScript, wine, server, request.InstallerPath],
            Timeout.InfiniteTimeSpan,
            cancellationToken).ConfigureAwait(false);
        if (!installed.Completed)
        {
            return NotFinished("the installer", cancellationToken);
        }

        _logger?.LogInformation(
            "installer ended after {Seconds:F0} s, exit code {ExitCode} (information only)",
            Stopwatch.GetElapsedTime(startedAt).TotalSeconds,
            installed.ExitCode);

        // Copying a program folder is synchronous file work; it stays off the caller's thread.
        return await Task.Run(() => TakeFolder(prefix, request), CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<ProcessOutcome> RunCageAsync(
        Cage cage, string[] script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var command = new ProcessCommand(
            cage.Bubblewrap, [.. cage.Arguments, Shell, "-c", .. script], cage.Environment);
        _logger?.LogInformation("cage started: {Command}", command.RenderShort());

        return await _runner.RunAsync(command, timeout, cancellationToken).ConfigureAwait(false);
    }

    // A cage that did not end on its own was killed — by Cancel, by its time limit, or it never
    // started. Only the first is the user's choice.
    private static CagedInstallResult NotFinished(string step, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? new(CagedInstallOutcome.Canceled, string.Empty)
            : new(
                CagedInstallOutcome.Failed,
                $"The cage for {step} did not finish: it timed out or could not start.");

    private CagedInstallResult TakeFolder(string prefix, CagedInstallRequest request)
    {
        var source = Path.Combine(prefix, "drive_c", request.KeepFolder);
        var content = FolderContent.Read(source);
        if (content is null || content.Files.Count == 0)
        {
            return new(
                CagedInstallOutcome.FolderMissing,
                $"drive_c/{request.KeepFolder} is missing or holds no file.");
        }

        foreach (var link in content.Links)
        {
            _logger?.LogWarning("symbolic link not copied: {Link}", link);
        }

        try
        {
            var bytes = content.CopyTo(source, request.Destination);
            _logger?.LogInformation(
                "copied {Files} files, {Bytes} bytes", content.Files.Count, bytes);
            return new(CagedInstallOutcome.Taken, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(request.Destination);
            return new(
                CagedInstallOutcome.Failed,
                $"Copying the kept folder failed with {exception.GetType().Name}.");
        }
    }

    private CagedInstallResult Report(CagedInstallResult result)
    {
        var isExpected = result.Outcome is CagedInstallOutcome.Taken or CagedInstallOutcome.Canceled;
        _logger?.Log(
            isExpected ? LogLevel.Information : LogLevel.Warning,
            "caged install ended: {Outcome}. {Detail}",
            result.Outcome,
            result.Detail);
        return result;
    }

    private static void RemoveThrowawayFolders(string prefix, string home)
    {
        TryDeleteFolder(prefix);
        TryDeleteFolder(home);
    }

    // Cleanup never throws: an exception from here would replace the outcome the caller needs.
    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort; a leftover is removed by the next run's cleanup at the start.
        }
    }

    private sealed record Cage(
        string Bubblewrap,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    /// <summary>
    /// The folders, files and symbolic links below a folder, as paths relative to it. Links are
    /// listed, never followed: inside a Wine prefix a link may point anywhere on the host
    /// (<c>dosdevices/z:</c> is <c>/</c>).
    /// </summary>
    private sealed class FolderContent
    {
        public List<string> Folders { get; } = [];

        public List<string> Files { get; } = [];

        public List<string> Links { get; } = [];

        /// <summary>The content of <paramref name="root"/>; null if it is missing or itself a link.</summary>
        public static FolderContent? Read(string root)
        {
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists || rootInfo.LinkTarget is not null)
            {
                return null;
            }

            var content = new FolderContent();
            var pending = new Stack<DirectoryInfo>([rootInfo]);
            while (pending.TryPop(out var folder))
            {
                foreach (var entry in folder.EnumerateFileSystemInfos())
                {
                    content.Add(root, entry, pending);
                }
            }

            return content;
        }

        /// <summary>Copies folders and files (not links) below <paramref name="destination"/>; returns the bytes copied.</summary>
        public long CopyTo(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var folder in Folders)
            {
                Directory.CreateDirectory(Path.Combine(destination, folder));
            }

            long bytes = 0;
            foreach (var file in Files)
            {
                var target = Path.Combine(destination, file);
                File.Copy(Path.Combine(source, file), target);
                bytes += new FileInfo(target).Length;
            }

            return bytes;
        }

        private void Add(string root, FileSystemInfo entry, Stack<DirectoryInfo> pending)
        {
            var relative = Path.GetRelativePath(root, entry.FullName);
            if (entry.LinkTarget is not null)
            {
                Links.Add(relative);
            }
            else if (entry is DirectoryInfo folder)
            {
                Folders.Add(relative);
                pending.Push(folder);
            }
            else
            {
                Files.Add(relative);
            }
        }
    }
}
