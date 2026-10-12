using System.Diagnostics;

namespace Anvil.Core.Steam;

/// <summary>
/// Wo das Kommando „steam" liegt und wie seine beiden Aufrufe aussehen (Spec S4.2 §4.3).
/// Erst <c>PATH</c> (auf der Owner-Maschine <c>/usr/bin/steam</c>), sonst
/// <c>&lt;root&gt;/steam.sh</c> — der Weg, den das Paket-Skript selbst nimmt. Kein Flatpak.
///
/// <para>
/// <b>Aufruf bauen und ausfuehren sind getrennt</b> (Muster <c>DesktopShell.StartInfoFor</c>):
/// Hier entsteht nur die Uebergabe, und die ist pruefbar. Ausgefuehrt wird in
/// <see cref="SteamProcess"/>.
/// </para>
///
/// <para>
/// <b>Die volle Umgebung ist der Standard von <see cref="ProcessStartInfo"/> — und sie wird
/// ausdruecklich nicht angefasst</b> (Spec E-D): Ohne DISPLAY/WAYLAND_DISPLAY, XDG_RUNTIME_DIR
/// und PATH startet Steam keine Oberflaeche. <b>Keine Umleitung von stdout/stderr</b>
/// (Spec E-E): Eine Umleitung ohne Leser blockiert den Kindprozess, sobald das Rohr voll ist,
/// und Steam schreibt beim Start Hunderte Zeilen. Steams eigenes Log liegt unter
/// <c>&lt;root&gt;/logs/</c>.
/// </para>
/// </summary>
public static class SteamCommand
{
    private const string CommandName = "steam";
    private const string LaunchScriptName = "steam.sh";
    private const string ShutdownArgument = "-shutdown";

    private const UnixFileMode AnyExecuteBit =
        UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Der Pfad zum Kommando, oder null — der Aufrufer meldet das als Wert.</summary>
    public static string? Find(
        Func<string, string?> getEnvironmentVariable, string? steamRootPath)
    {
        var path = getEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, CommandName);
            if (IsExecutable(candidate))
            {
                return candidate;
            }
        }

        if (steamRootPath is not null)
        {
            var script = Path.Combine(steamRootPath, LaunchScriptName);
            if (IsExecutable(script))
            {
                return script;
            }
        }

        return null;
    }

    /// <summary><c>steam -shutdown</c>: der Anstoss zum Beenden. Sein Ausgang entscheidet nichts.</summary>
    public static ProcessStartInfo ShutdownStartInfoFor(string command)
    {
        var info = StartStartInfoFor(command);
        info.ArgumentList.Add(ShutdownArgument);
        return info;
    }

    /// <summary><c>steam</c> ohne Argumente: der Start.</summary>
    public static ProcessStartInfo StartStartInfoFor(string command)
        => new(command) { UseShellExecute = false };

    /// <summary>
    /// Existiert und traegt irgendein Ausfuehrbar-Bit. Die Linux-Abfrage steht hinter
    /// <see cref="OperatingSystem.IsLinux"/> statt hinter einer SupportedOSPlatform-Zusage,
    /// die sich bis in die Composition Root fortpflanzen muesste (Muster GamePlatformProbe).
    /// </summary>
    private static bool IsExecutable(string path)
    {
        try
        {
            return File.Exists(path)
                && OperatingSystem.IsLinux()
                && (File.GetUnixFileMode(path) & AnyExecuteBit) != 0;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nicht pruefbar heisst nicht ausfuehrbar — der naechste Kandidat ist dran.
            return false;
        }
    }
}
