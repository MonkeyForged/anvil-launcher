namespace Anvil.Core.Installers;

/// <summary>
/// Where <c>bwrap</c> lives and how it is called (architecture §6.2, stage 1). The arguments are
/// the ones measured in the proof of concept's sandbox script (feasibility study §9.7 and
/// §10.4), plus the read-only Proton folder and the X authority file.
/// </summary>
public static class Bubblewrap
{
    public const string ExecutableName = "bwrap";

    private const UnixFileMode AnyExecute =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>
    /// The first executable file named <c>bwrap</c> in a ':'-separated PATH value; <c>null</c> if
    /// there is none or <paramref name="pathVariable"/> is null or empty. Never throws: a folder
    /// that cannot be read is skipped.
    /// </summary>
    public static string? Find(string? pathVariable)
    {
        if (string.IsNullOrEmpty(pathVariable))
        {
            return null;
        }

        foreach (var folder in pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, ExecutableName);
            if (IsExecutableFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// The bwrap arguments for <paramref name="layout"/>, in the order bwrap applies them. The
    /// command to run inside follows them; the caller appends it.
    /// </summary>
    public static IReadOnlyList<string> CageArguments(CageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var arguments = new List<string> { "--ro-bind", "/", "/", "--dev", "/dev" };

        // Proton's Wine initialises the GPU for its desktop window; without the device nodes no
        // window opens (feasibility §10.4).
        foreach (var device in layout.DeviceNodes)
        {
            arguments.AddRange(["--dev-bind", device, device]);
        }

        arguments.AddRange(["--proc", "/proc"]);

        foreach (var hidden in layout.HiddenFolders)
        {
            arguments.AddRange(["--tmpfs", hidden]);
        }

        arguments.AddRange(["--tmpfs", "/tmp"]);

        // Every bind below comes after the tmpfs mounts on purpose: bwrap applies its arguments
        // in order, so a folder under /home or /tmp becomes visible again (TD-19).
        if (layout.X11SocketFolder is not null)
        {
            arguments.AddRange(["--ro-bind", layout.X11SocketFolder, layout.X11SocketFolder]);
        }

        if (layout.XAuthorityFile is not null)
        {
            arguments.AddRange(["--ro-bind", layout.XAuthorityFile, layout.XAuthorityFile]);
        }

        arguments.AddRange(["--ro-bind", layout.ProtonFolder, layout.ProtonFolder]);
        arguments.AddRange(["--bind", layout.WritableFolder, layout.WritableFolder]);
        arguments.AddRange(["--unshare-pid", "--die-with-parent", "--new-session"]);
        arguments.AddRange(["--setenv", "HOME", layout.HomeFolder]);
        return arguments;
    }

    private static bool IsExecutableFile(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetUnixFileMode(path) & AnyExecute) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
