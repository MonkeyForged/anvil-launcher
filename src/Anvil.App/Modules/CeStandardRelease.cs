using Anvil.App.ViewModels;
using Anvil.Core.Packages;

namespace Anvil.App.Modules;

/// <summary>
/// The one CE release Anvil gets from the vendor (spec s9.3): what the row offers, where the
/// installer comes from, which folder of the installed prefix is kept, and what the file-by-file
/// check leaves uncompared. The list of its files is <see cref="CeStandardReleaseFiles"/>.
/// </summary>
public static class CeStandardRelease
{
    /// <summary>The name the release carries in the store once it lies there.</summary>
    public const string Name = "7.7";

    /// <summary>The installed folder, relative to <c>drive_c/</c>, that becomes the release.</summary>
    public const string KeptFolder = "Program Files/Cheat Engine";

    /// <summary>What the selection row offers: <c>Get CE 7.7</c>.</summary>
    public static VendorOffer Offer { get; } = new(Name, "CE 7.7");

    /// <summary>
    /// The vendor's web installer at its fixed address (spec E-2). Without a checksum: measured,
    /// every download is another file (spec E-1).
    /// </summary>
    public static UnverifiedDownload Installer { get; } = new(
        "CheatEngine77Setup.exe",
        "CE 7.7 installer",
        new Uri("https://d3gpt2g9zwv61d.cloudfront.net/dUfJarEHz.exe"),
        6_882_792,
        "The vendor rebuilds and re-signs the web installer for every download (measured 2026-10-01); "
        + "the taken-over program is checked file by file instead.");

    /// <summary>Files compared by name only: the uninstall record describes this one installation
    /// (spec E-5).</summary>
    public static IReadOnlyCollection<string> ContentNotCompared { get; } = ["unins000.dat"];
}
