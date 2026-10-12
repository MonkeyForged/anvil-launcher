namespace Anvil.Core.Installers;

/// <summary>
/// What the cage shows (spec s9.3, the cage section): the host read-only, home and mount folders
/// replaced by empty ones, one writable folder, Proton's folder read-only, and what a window
/// needs.
/// </summary>
/// <param name="WritableFolder">The only folder the cage can write to.</param>
/// <param name="ProtonFolder">Shown read-only, wherever it lies (spec E-6).</param>
/// <param name="HomeFolder"><c>HOME</c> inside the cage; lies inside <paramref name="WritableFolder"/>.</param>
/// <param name="HiddenFolders">Replaced by an empty tmpfs.</param>
/// <param name="DeviceNodes">Graphics devices passed through.</param>
/// <param name="X11SocketFolder">The X11 socket folder, or <c>null</c>.</param>
/// <param name="XAuthorityFile">The X authority file, or <c>null</c>.</param>
public sealed record CageLayout(
    string WritableFolder,
    string ProtonFolder,
    string HomeFolder,
    IReadOnlyList<string> HiddenFolders,
    IReadOnlyList<string> DeviceNodes,
    string? X11SocketFolder,
    string? XAuthorityFile)
{
    private const string HomeFolderName = "home";

    private const string X11Socket = "/tmp/.X11-unix";

    private static readonly string[] HideCandidates = ["/home", "/mnt", "/media", "/run/media", "/srv", "/root"];

    private static readonly string[] DeviceCandidates =
        ["/dev/dri", "/dev/nvidia0", "/dev/nvidiactl", "/dev/nvidia-modeset", "/dev/nvidia-uvm"];

    /// <summary>
    /// The layout for this host, pure given <paramref name="exists"/>: every candidate folder and
    /// device that exists, the X11 socket folder if it exists, and <c>XAUTHORITY</c> from
    /// <paramref name="environment"/> if it is set and exists. <see cref="HomeFolder"/> is
    /// <c>&lt;writableFolder&gt;/home</c>.
    /// </summary>
    public static CageLayout ForHost(
        string writableFolder,
        string protonFolder,
        IReadOnlyDictionary<string, string> environment,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writableFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(protonFolder);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(exists);

        // The authority file usually lies under /run/user, but it may lie in the home folder —
        // there the tmpfs would hide it, so it is bound explicitly whenever it is set.
        var xAuthority = environment.GetValueOrDefault("XAUTHORITY");

        return new CageLayout(
            writableFolder,
            protonFolder,
            Path.Combine(writableFolder, HomeFolderName),
            [.. HideCandidates.Where(exists)],
            [.. DeviceCandidates.Where(exists)],
            exists(X11Socket) ? X11Socket : null,
            !string.IsNullOrEmpty(xAuthority) && exists(xAuthority) ? xAuthority : null);
    }
}
