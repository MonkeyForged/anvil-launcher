using Anvil.App.Bridges;
using Anvil.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// The taken-over CE application: the release the user chose in the store becomes a folder every
/// game starts from, <c>&lt;data root&gt;/ce_bridge/app/&lt;release&gt;/</c> (s9.2).
///
/// <para>
/// <b>Once for all games</b>, as with <see cref="WandApplication"/>: one copy per release instead
/// of one per prefix. The store stays what the user placed there and is never written to — the
/// copy lies next to it under the reserved folder (<see cref="CeReleaseStore.ReservedFolder"/>),
/// which can be thrown away as a whole.
/// </para>
///
/// <para>
/// <b>Nothing is ever cleaned up:</b> two games can run from one release at once, and deleting on
/// a release switch would hit a running CE. The price is disk space.
/// </para>
/// </summary>
public sealed class CeApplication
{
    private readonly CeReleaseStore _releases;
    private readonly ILogger? _logger;

    /// <param name="releases">The module's store — the chosen release lies there, and the copy
    /// goes next to it.</param>
    /// <param name="logger">The module's category; null means silent.</param>
    public CeApplication(CeReleaseStore releases, ILogger? logger = null)
    {
        _releases = releases;
        _logger = logger;
    }

    /// <summary>The process images by which CE is recognised in the container.</summary>
    public static IReadOnlyList<string> ProcessImages { get; } = [CeInstallation.ProgramName];

    /// <summary>The sentence when the release could not be copied into the app folder. The cause
    /// is in the log.</summary>
    public static string CopyFailedReason(string release)
        => $"{release} could not be copied — see the log for details.";

    /// <summary>
    /// The sentence for a release folder without the program. Anvil does not delete it (someone
    /// may have put it there), so the sentence has to say what to do.
    /// </summary>
    public static string IncompleteFolderReason(string folder)
        => $"{folder} exists but has no {CeInstallation.ProgramName} — delete the folder and try again.";

    /// <summary>
    /// Copies the chosen release if needed and names the program. Throws only on cancellation;
    /// every other failure is a refusal with a sentence that goes to the user unchanged. The copy
    /// runs through <see cref="ApplicationCopy"/>, which never deletes the store or an app folder.
    /// </summary>
    public AttachDeclaration Prepare(CancellationToken cancellationToken)
    {
        var selected = _releases.Selected;
        var release = selected is null
            ? null
            : _releases.List().FirstOrDefault(entry => entry.Name == selected);
        if (release is null)
        {
            // Selected already reconciles against the list, so null means nothing usable is
            // chosen; only the list tells which sentence helps.
            return AttachDeclaration.NotPossible(
                _releases.List().Count == 0
                    ? CeBridgeModule.NotInstalledReason
                    : CeBridgeModule.NothingSelectedReason);
        }

        var target = Path.Combine(_releases.Folder, CeReleaseStore.ReservedFolder, release.Name);
        var copy = ApplicationCopy.Ensure(
            release.Path, target, CeInstallation.ProgramName, cancellationToken);

        switch (copy.Outcome)
        {
            case ApplicationCopyOutcome.ProgramMissing:
                return AttachDeclaration.NotPossible(CopyFailedReason(release.Name));

            case ApplicationCopyOutcome.Unreadable:
                _logger?.LogError(copy.Error, "cannot copy {Release} from {Path}", release.Name, release.Path);
                return AttachDeclaration.NotPossible(CopyFailedReason(release.Name));

            case ApplicationCopyOutcome.IncompleteTarget:
                // Someone created the folder by hand, or an earlier copy died half-way without
                // the done marker — Anvil says so instead of deleting.
                return AttachDeclaration.NotPossible(IncompleteFolderReason(target));

            case ApplicationCopyOutcome.Copied:
                _logger?.LogInformation("copied {Release} into {Folder}", release.Name, target);
                break;
        }

        return AttachDeclaration.Start(new AttachProgram(target, copy.ExecutablePath, []));
    }
}
