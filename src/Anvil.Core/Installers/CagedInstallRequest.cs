using Anvil.Core.Steam;

namespace Anvil.Core.Installers;

/// <summary>
/// What <see cref="CagedInstaller.RunAsync"/> is asked to do: run a foreign installer caged and
/// hand out one folder it leaves behind (spec s9.3, "Die Naht").
/// </summary>
/// <param name="InstallerPath">
/// Absolute path of an existing file. Wine must see it inside the cage, so it lies inside
/// <paramref name="WorkFolder"/> or in a place the cage does not hide.
/// </param>
/// <param name="Proton">The compat tool whose Wine runs the installer.</param>
/// <param name="KeepFolder">
/// Relative to <c>drive_c/</c>, '/'-separated, e.g. <c>Program Files/Example</c>.
/// </param>
/// <param name="WorkFolder">Absolute; created if missing; the only writable place in the cage.</param>
/// <param name="Destination">Absolute; must not exist; receives the kept folder on success.</param>
public sealed record CagedInstallRequest(
    string InstallerPath,
    CompatTool Proton,
    string KeepFolder,
    string WorkFolder,
    string Destination);

/// <summary>Exactly one of these ends every caged run.</summary>
public enum CagedInstallOutcome
{
    /// <summary>The kept folder was copied to the destination.</summary>
    Taken,

    /// <summary>The installer ended, but <c>drive_c/&lt;KeepFolder&gt;</c> is missing or holds no file.</summary>
    FolderMissing,

    /// <summary>The token was canceled; every process in the cage is gone.</summary>
    Canceled,

    /// <summary><c>bwrap</c> was not found.</summary>
    NoBubblewrap,

    /// <summary>The chosen Proton has no <c>wine</c> or no <c>wineserver</c>.</summary>
    NoWine,

    /// <summary>After the first cage, <c>prefix/system.reg</c> is missing.</summary>
    PrefixNotCreated,

    /// <summary>Anything else (copy failed, process could not start); the detail says what.</summary>
    Failed,
}

/// <summary>The result of a caged run.</summary>
/// <param name="Outcome">Which of the outcomes.</param>
/// <param name="Detail">
/// One English sentence for the log, never shown to the user. Empty for
/// <see cref="CagedInstallOutcome.Taken"/> and <see cref="CagedInstallOutcome.Canceled"/>.
/// </param>
public sealed record CagedInstallResult(CagedInstallOutcome Outcome, string Detail);
