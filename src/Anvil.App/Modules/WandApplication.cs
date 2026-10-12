using Anvil.App.Bridges;
using Anvil.Core.Modules;
using Anvil.Core.Packages;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// The unpacked Wand application (spec S4.4 §3.1): the vendor file in the store becomes a
/// folder every game starts from.
///
/// <para>
/// <b>Once for all games</b> (project plan M3, decision 15): one copy instead of 160 MB per
/// prefix, one place to update, one truth about which version runs. Measured: Wand does not
/// write into its program folder and two games run from the same folder at once
/// (messungen-anhaengen.md §5).
/// </para>
///
/// <para>
/// <b>The store stays what it is:</b> the place of the <i>vendor file</i>. Unpacking happens
/// next to it, under <c>app/</c> — a bracket that can be thrown away as a whole without
/// touching the store (E-1).
/// </para>
///
/// <para>
/// <b>Nothing is ever cleaned up</b> (E-4). Two games can run from one version at once;
/// deleting on a version switch would hit a running Wand. The price is disk space.
/// </para>
/// </summary>
public sealed class WandApplication
{
    /// <summary>
    /// The application itself. <b>Searched for, not assumed</b> (E-2): in the vendor file it
    /// lies under <c>lib/net45/</c>, in a hand-packed zip perhaps at the very top.
    /// </summary>
    public const string ExecutableName = "WeMod.exe";

    /// <summary>
    /// Wand's own service. Measured: it dies with the main process (messungen-anhaengen.md §3);
    /// naming it anyway costs nothing and keeps the check after detaching honest.
    /// </summary>
    public const string AuxiliaryServiceName = "WeModAuxiliaryService.exe";

    /// <summary>
    /// How Wand's processes are recognised in the container (spec S4.5 §3.5). <b>Without
    /// <c>explorer.exe</c> and <c>tabtip.exe</c></b> (E-8): both come into being with Wand but
    /// belong to the game's Wine world — under Proton the game itself often runs under
    /// <c>explorer.exe</c>.
    /// </summary>
    public static IReadOnlyList<string> ProcessImages { get; } = [ExecutableName, AuxiliaryServiceName];

    /// <summary>
    /// The template of the login folder in the module folder: <c>&lt;module folder&gt;/login/</c>.
    /// It does not exist at first — the user logs in <i>inside</i> the prefix, and the template
    /// only comes into being with the first harvest (spec S4.6 §1).
    /// </summary>
    public const string LoginFolder = "login";

    /// <summary>
    /// Where Wand's data folder lies in the prefix, relative to <c>drive_c/</c>. Checked on both
    /// test games on 2026-09-18. <c>steamuser</c> is Proton's user name; a prefix with another
    /// one is caught by the core's guard instead of being written blindly.
    /// </summary>
    public const string LoginPrefixPath = "users/steamuser/AppData/Roaming/WeMod";

    /// <summary>
    /// What never travels along from the login folder — names on the <b>first level</b>.
    ///
    /// <para>
    /// The first five are <b>measured</b> (spec S4.6 §2): 15.9 of 34 MB lie in them. The four
    /// after that are <b>precaution</b> (E-5): Electron's lock files under Wine are unmeasured,
    /// and a lock harvested along from a crashed Wand would poison the template and with it every
    /// future game. A wrongly guessed name hits nothing and costs nothing.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LoginSkipped { get; } =
    [
        "Cache", "Code Cache", "GPUCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile",
    ];

    /// <summary>
    /// The declaration for the core (spec S4.6 §3.4). <b>Rebuilt on every access</b>, because
    /// the module folder is a delegate: if the user moves the data root, the next query points
    /// elsewhere.
    /// </summary>
    public AttachStateFolder LoginState
        => new(Path.Combine(_moduleFolder(), LoginFolder), LoginPrefixPath, LoginSkipped);

    /// <summary>The bracket in the module folder under which each version gets its folder.</summary>
    public const string UnpackFolder = "app";

    private readonly PackageStore _store;
    private readonly Func<string> _moduleFolder;
    private readonly ILogger? _logger;

    /// <param name="store">The module's own store — the selected version lies there.</param>
    /// <param name="moduleFolder">
    /// <c>&lt;data root&gt;/wand_bridge/</c>, as a delegate: the user can move the data root
    /// while running (architecture.md §4.1).
    /// </param>
    /// <param name="logger">The module's category; null means silent.</param>
    public WandApplication(PackageStore store, Func<string> moduleFolder, ILogger? logger = null)
    {
        _store = store;
        _moduleFolder = moduleFolder;
        _logger = logger;
    }

    /// <summary>
    /// The sentence when the archive holds no application. It names the file because the user
    /// may have put it there themselves.
    /// </summary>
    public static string NoExecutableReason(string archiveName)
        => $"{archiveName} does not contain {ExecutableName} — pick a different package in Settings → Wand Bridge.";

    /// <summary>The sentence when the file is not a readable archive.</summary>
    public static string UnreadableReason(string archiveName)
        => $"{archiveName} could not be unpacked — see the log for details.";

    /// <summary>
    /// The sentence for a version folder without an application (plan P-2). Anvil does not
    /// delete it (E-4), so the sentence has to say what to do.
    /// </summary>
    public static string IncompleteFolderReason(string folder)
        => $"{folder} exists but has no {ExecutableName} — delete the folder and try again.";

    /// <summary>
    /// Unpacks the selected version if needed and names the program. Throws only on
    /// cancellation; every other failure is a refusal with a sentence that goes to the user
    /// unchanged. Unpacking runs through <see cref="ApplicationCopy"/>: its .partial folder is
    /// the done marker (E-3), and it deletes neither the store nor a version folder (E-4).
    /// </summary>
    public AttachDeclaration Prepare(CancellationToken cancellationToken)
    {
        var selected = _store.Selected;
        if (selected is null)
        {
            return AttachDeclaration.NotPossible(WandBridgeModule.ApplicationMissingReason);
        }

        var archivePath = Path.Combine(_store.Folder, selected);
        var target = Path.Combine(
            _moduleFolder(), UnpackFolder, Path.GetFileNameWithoutExtension(selected));

        var copy = ApplicationCopy.Ensure(archivePath, target, ExecutableName, cancellationToken);
        switch (copy.Outcome)
        {
            case ApplicationCopyOutcome.ProgramMissing:
                return AttachDeclaration.NotPossible(NoExecutableReason(selected));

            case ApplicationCopyOutcome.IncompleteTarget:
                // Someone created the folder by hand, or an older version without a done marker
                // left it — Anvil says so instead of deleting (plan P-2).
                return AttachDeclaration.NotPossible(IncompleteFolderReason(target));

            case ApplicationCopyOutcome.Unreadable:
                _logger?.LogError(copy.Error, "cannot unpack {Archive}", archivePath);
                return AttachDeclaration.NotPossible(UnreadableReason(selected));

            case ApplicationCopyOutcome.Copied:
                _logger?.LogInformation("unpacked {Archive} into {Folder}", selected, target);
                break;
        }

        return AttachDeclaration.Start(new AttachProgram(target, copy.ExecutablePath, []));
    }
}
