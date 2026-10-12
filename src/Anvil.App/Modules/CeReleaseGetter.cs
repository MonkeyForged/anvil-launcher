using Anvil.App.ViewModels;
using Anvil.Core.Installers;
using Anvil.Core.Packages;
using Anvil.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// CE Bridge's way of obtaining its standard release (spec s9.3, UC-1 steps 2 to 9): ask, download
/// the vendor's installer, run it caged, check what it installed against the list, place it as
/// <c>ce_bridge/7.7/</c>. The selection row draws the stages; choosing the placed release and
/// triggering the module's check stay with the row, as for Wand.
///
/// <para>
/// <b>Everything happens in one dot folder, <c>ce_bridge/.get/</c></b>, which the store skips.
/// It is the cage's work folder itself: inside the cage home and mount points are empty, so the
/// installer is only visible there if it lies in the work folder. Leftovers of an interrupted run
/// are cleared before the next one, and every way out deletes the folder again — only the
/// renaming of the checked folder to <c>7.7</c> makes anything visible.
/// </para>
///
/// <para>
/// <b>No path of the data root reaches the log</b>: lines name the store's folder relatively
/// (<c>ce_bridge/…</c>); the cage writes its own lines.
/// </para>
/// </summary>
public sealed partial class CeReleaseGetter : ObservableObject, IVendorBuilds
{
    /// <summary>The sentence below the empty row.</summary>
    public const string EmptyNote =
        "Anvil downloads the installer from the vendor. You click through it; Anvil keeps the program.";

    /// <summary>Red: the installer ended without leaving the program folder (TD-16).</summary>
    public const string ClosedEarlyText = "The installer closed before it finished — nothing was kept.";

    /// <summary>Red: the program is missing or a penalty file lies there (TD-7).</summary>
    public const string IncompleteText = "Incomplete build — the 64-bit program is missing. Nothing was kept.";

    /// <summary>Red: a file not on the list, or one with other content (TD-17, TD-5).</summary>
    public const string UnexpectedText =
        "Unexpected files — the installed program has files that are not part of CE 7.7. Nothing was kept.";

    /// <summary>Red: everything technical in between (spec E-9); the reason is in the log.</summary>
    public const string GetFailedText = "Getting CE failed — nothing was kept. See the log for details.";

    private const string WorkFolderName = ".get";
    private const string TakeFolderName = "take";

    private readonly CeReleaseStore _releases;
    private readonly Action<Action> _post;
    private readonly Func<ConfirmationRequest, Task<bool>> _ask;
    private readonly Func<bool> _isBubblewrapAvailable;
    private readonly Func<CompatTool?> _chooseProton;
    private readonly Func<CagedInstallRequest, CancellationToken, Task<CagedInstallResult>> _runCaged;
    private readonly IReadOnlyDictionary<string, string> _expectedFiles;
    private readonly ILogger? _logger;

    private VendorStage _stage;
    private EmphasizedText _blockedNote;

    /// <summary>Set from the click until the end of the run — wider than <see cref="Stage"/>,
    /// which is still idle while the confirmation stands.</summary>
    private bool _isObtaining;

    /// <summary>The cage's token while the installer is open; null otherwise.</summary>
    private CancellationTokenSource? _installerCancellation;

    /// <param name="releases">The store the release lands in; <c>.get/</c> lies inside it.</param>
    /// <param name="fetch">The download with bar, cancel and red sentence.</param>
    /// <param name="post">Marshals the change notifications onto the UI thread.</param>
    /// <param name="ask">The settings page's confirmation (I-3).</param>
    /// <param name="isBubblewrapAvailable">Is an executable <c>bwrap</c> on the search path?</param>
    /// <param name="chooseProton">The Proton whose Wine runs the installer; null = none (TD-23).</param>
    /// <param name="runCaged">The Core's cage (<see cref="CagedInstaller.RunAsync"/>).</param>
    /// <param name="expectedFiles">The list of the standard release; at runtime
    /// <see cref="CeStandardReleaseFiles.Sha256ByPath"/>.</param>
    /// <param name="logger">Category <c>ce_bridge</c>; null means silent.</param>
    public CeReleaseGetter(
        CeReleaseStore releases,
        FetchProgressViewModel fetch,
        Action<Action> post,
        Func<ConfirmationRequest, Task<bool>> ask,
        Func<bool> isBubblewrapAvailable,
        Func<CompatTool?> chooseProton,
        Func<CagedInstallRequest, CancellationToken, Task<CagedInstallResult>> runCaged,
        IReadOnlyDictionary<string, string> expectedFiles,
        ILogger? logger = null)
    {
        _releases = releases;
        Fetch = fetch;
        _post = post;
        _ask = ask;
        _isBubblewrapAvailable = isBubblewrapAvailable;
        _chooseProton = chooseProton;
        _runCaged = runCaged;
        _expectedFiles = expectedFiles;
        _logger = logger;
        _blockedNote = CurrentBlockedNote();
    }

    /// <summary>Why <c>Get</c> cannot run: <c>bubblewrap</c> is missing (TD-15).</summary>
    public static EmphasizedText BlockedText { get; } = new(
        "Getting CE needs bubblewrap. Install it, or put a complete installation into ce_bridge/.", "bubblewrap");

    /// <summary>The sentence while the installer is open.</summary>
    public static EmphasizedText InstallerOpenNote { get; } =
        new("Finish the installer in its own window. Choose Decline on every offer.", "Decline");

    /// <summary>The sentence while the installed program is checked.</summary>
    public static EmphasizedText CheckingNote { get; } = new("Checking that the installed program is complete.");

    /// <summary>The question before anything happens (design 61).</summary>
    public static ConfirmationRequest Confirmation { get; } = new(
        "Open the Cheat Engine installer?",
        new EmphasizedText(
            "The next window is the vendor's installer, not Anvil. It will offer you other software. "
            + "Choose Decline on every offer — none of it works on Linux.",
            "Decline"),
        "Cancel",
        "Open installer");

    public IReadOnlyList<VendorOffer> Offers { get; } = [CeStandardRelease.Offer];

    public FetchProgressViewModel Fetch { get; }

    public VendorStage Stage => _stage;

    /// <summary>While fetching no sentence — the bar says it (spec, row table).</summary>
    public EmphasizedText StageNote => _stage switch
    {
        VendorStage.InstallerOpen => InstallerOpenNote,
        VendorStage.Checking => CheckingNote,
        _ => EmphasizedText.Empty,
    };

    public EmphasizedText BlockedNote => _blockedNote;

    /// <summary>Looks for <c>bwrap</c> again (spec E-8: whenever the settings page is shown).</summary>
    public void Refresh()
    {
        var note = CurrentBlockedNote();
        if (note == _blockedNote)
        {
            return;
        }

        _blockedNote = note;
        _logger?.LogInformation("bubblewrap {State}", note.Text.Length == 0 ? "found" : "missing");
        _post(() => OnPropertyChanged(nameof(BlockedNote)));
    }

    public async Task<bool> ObtainAsync(VendorOffer offer)
    {
        if (offer != CeStandardRelease.Offer || _isObtaining || _blockedNote.Text.Length > 0)
        {
            return false;
        }

        _isObtaining = true;
        try
        {
            if (!await _ask(Confirmation))
            {
                _logger?.LogInformation("CE installer declined at the confirmation");
                return false;
            }

            return await GetAsync();
        }
        finally
        {
            _isObtaining = false;
        }
    }

    /// <summary>The <c>Cancel</c> while the installer is open: ends the cage and every process in
    /// it.</summary>
    [RelayCommand]
    private void CancelStage() => _installerCancellation?.Cancel();

    private EmphasizedText CurrentBlockedNote()
        => _isBubblewrapAvailable() ? EmphasizedText.Empty : BlockedText;

    /// <summary>
    /// The run after the confirmation. <b>Why the broad catch:</b> the row starts it from a click
    /// and from a choice in the list, the latter without awaiting — an exception escaping here
    /// would be lost or take the UI down. Whatever goes wrong becomes the sentence for technical
    /// failures, and its type reaches the log.
    /// </summary>
    private async Task<bool> GetAsync()
    {
        Fetch.ErrorText = string.Empty;
        string? storeFolder = null;
        try
        {
            storeFolder = _releases.Folder;
            var workFolder = Path.Combine(storeFolder, WorkFolderName);
            DeleteWorkFolder(workFolder);
            return await GetIntoAsync(storeFolder, workFolder);
        }
        catch (Exception exception)
        {
            // Without the store's folder the message may name the data root; the store has
            // logged its own failure then.
            _logger?.LogError(
                "getting CE failed: {Type}: {Message}",
                exception.GetType().Name,
                storeFolder is null ? string.Empty : Relative(exception.Message, storeFolder));
            Fetch.ErrorText = GetFailedText;
            return false;
        }
        finally
        {
            SetStage(VendorStage.Idle);
            if (storeFolder is not null)
            {
                DeleteWorkFolder(Path.Combine(storeFolder, WorkFolderName));
            }
        }
    }

    /// <summary>Steps 4 to 8 of I-6, each ending the run with its sentence when it fails.</summary>
    private async Task<bool> GetIntoAsync(string storeFolder, string workFolder)
    {
        if (_chooseProton() is not { } proton)
        {
            Fail(GetFailedText, "no Proton with Wine found");
            return false;
        }

        _logger?.LogInformation("CE installer runs with {Proton}", proton.Name);

        SetStage(VendorStage.Fetching);
        var installerStore = new PackageStore(() => workFolder, () => null, _ => { });
        var status = await Fetch.RunUnverifiedAsync(installerStore, CeStandardRelease.Installer);
        if (status != FetchStatus.Placed)
        {
            // The red sentence is Fetch's; a cancel stays silent.
            _logger?.LogInformation("CE installer not fetched: {Status}", status);
            return false;
        }

        var takeFolder = Path.Combine(workFolder, TakeFolderName);
        var request = new CagedInstallRequest(
            Path.Combine(workFolder, CeStandardRelease.Installer.FileName),
            proton,
            CeStandardRelease.KeptFolder,
            workFolder,
            takeFolder);
        if (!await RunInstallerAsync(request))
        {
            return false;
        }

        SetStage(VendorStage.Checking);
        if (!await IsStandardReleaseAsync(takeFolder, storeFolder))
        {
            return false;
        }

        return Place(takeFolder, Path.Combine(storeFolder, CeStandardRelease.Name));
    }

    /// <summary>Step 6: the installer in the cage, cancelable through
    /// <see cref="CancelStageCommand"/>.</summary>
    private async Task<bool> RunInstallerAsync(CagedInstallRequest request)
    {
        SetStage(VendorStage.InstallerOpen);
        using var cancellation = new CancellationTokenSource();
        _installerCancellation = cancellation;
        CagedInstallResult result;
        try
        {
            result = await _runCaged(request, cancellation.Token);
        }
        finally
        {
            _installerCancellation = null;
        }

        switch (result.Outcome)
        {
            case CagedInstallOutcome.Taken:
                return true;
            case CagedInstallOutcome.Canceled:
                _logger?.LogInformation("CE installer canceled");
                return false;
            case CagedInstallOutcome.FolderMissing:
                Fail(ClosedEarlyText, $"installer left no program folder: {result.Detail}");
                return false;
            default:
                Fail(GetFailedText, $"caged installer ended with {result.Outcome}: {result.Detail}");
                return false;
        }
    }

    /// <summary>Step 7: the taken-over folder against the list. Hashing 94 MB runs on the thread
    /// pool.</summary>
    private async Task<bool> IsStandardReleaseAsync(string takeFolder, string storeFolder)
    {
        var check = await Task.Run(() => CeInstallation.CheckAgainstList(
            takeFolder, _expectedFiles, CeStandardRelease.ContentNotCompared));

        switch (check.Problem)
        {
            case CeInstallationProblem.None:
                _logger?.LogInformation("CE check passed: every file matches the standard release");
                return true;
            case CeInstallationProblem.ProgramMissing or CeInstallationProblem.PenaltyFile:
                Fail(IncompleteText, $"CE check: {check.Problem} {check.Detail}");
                return false;
            case CeInstallationProblem.UnexpectedFiles:
                Fail(UnexpectedText, $"CE check: unexpected files: {check.Detail}");
                return false;
            default:
                Fail(GetFailedText, $"CE check: {check.Problem}: {Relative(check.Detail ?? string.Empty, storeFolder)}");
                return false;
        }
    }

    /// <summary>Step 8: rename into the store — never over anything that lies there (TD-7).</summary>
    private bool Place(string takeFolder, string releaseFolder)
    {
        if (Directory.Exists(releaseFolder) || File.Exists(releaseFolder))
        {
            Fail(GetFailedText, $"{RelativeName(CeStandardRelease.Name)} is already taken; nothing was overwritten");
            return false;
        }

        Directory.Move(takeFolder, releaseFolder);
        _logger?.LogInformation("CE release placed: {Release}", RelativeName(CeStandardRelease.Name));
        return true;
    }

    private void Fail(string sentence, string reason)
    {
        _logger?.LogWarning("getting CE failed: {Reason}", reason);
        Fetch.ErrorText = sentence;
    }

    private void SetStage(VendorStage stage)
    {
        if (_stage == stage)
        {
            return;
        }

        _stage = stage;
        _post(() =>
        {
            OnPropertyChanged(nameof(Stage));
            OnPropertyChanged(nameof(StageNote));
        });
    }

    /// <summary>Deletes the dot folder with everything in it; never throws (a cleanup that throws
    /// would hide the outcome that matters).</summary>
    private void DeleteWorkFolder(string workFolder)
    {
        try
        {
            if (!Directory.Exists(workFolder))
            {
                return;
            }

            Directory.Delete(workFolder, recursive: true);
            _logger?.LogInformation("CE work folder cleared: {Folder}", RelativeName(WorkFolderName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                "cannot clear CE work folder {Folder}: {Type}", RelativeName(WorkFolderName), exception.GetType().Name);
        }
    }

    private static string RelativeName(string name) => $"{CeBridgeModule.ModuleKey}/{name}";

    /// <summary>A message with the store's folder replaced by its relative name, so no path of the
    /// data root reaches the log.</summary>
    private static string Relative(string message, string storeFolder)
        => message.Replace(storeFolder, CeBridgeModule.ModuleKey, StringComparison.Ordinal);
}
