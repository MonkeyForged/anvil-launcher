namespace Anvil.Core.Steam;

/// <summary>Wo Steam gefunden wurde — und unter welchem Namen gesucht wurde (Spec §9).</summary>
public sealed record SteamRootLocation(string Path, string Candidate);

/// <summary>
/// Findet die Steam-Installation. Kandidaten der Reihe nach, jeder durch Symlinks aufgelöst;
/// der Beweis ist <c>config/config.vdf</c> (Spec §4). Kein Flatpak in v1 (Spec E1).
/// </summary>
public static class SteamRoot
{
    /// <summary>Die geprüften Kandidaten in ihrer Reihenfolge — auch für die Log-Zeile bei Misserfolg.</summary>
    public static IReadOnlyList<string> Candidates(Func<string, string?> getEnv)
    {
        // HOME kommt aus der gereichten Umgebung, nicht aus Environment.GetFolderPath:
        // sonst wäre diese Methode nicht ohne die echte Maschine prüfbar, und ein Test, der
        // „nichts gefunden" erwartet, fände die Steam-Installation des Entwicklers.
        // Environment.GetFolderPath liest unter Linux ohnehin $HOME — der Rückfall ist also
        // dasselbe, nur ohne Naht.
        var home = getEnv("HOME") is { Length: > 0 } fromEnv
            ? fromEnv
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        var dataHome = getEnv("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(dataHome))
        {
            candidates.Add(System.IO.Path.Combine(dataHome, "Steam"));
        }

        candidates.Add(System.IO.Path.Combine(home, ".local", "share", "Steam"));
        candidates.Add(System.IO.Path.Combine(home, ".steam", "steam"));
        candidates.Add(System.IO.Path.Combine(home, ".steam", "root"));
        return candidates;
    }

    /// <summary>Der erste Kandidat, der sich als Steam-Installation ausweist — sonst <c>null</c>.</summary>
    public static SteamRootLocation? Find(Func<string, string?> getEnv)
    {
        foreach (var candidate in Candidates(getEnv))
        {
            var resolved = Resolve(candidate);
            if (resolved is not null
                && File.Exists(System.IO.Path.Combine(resolved, "config", "config.vdf")))
            {
                return new SteamRootLocation(resolved, candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Löst Symlinks auf. <c>Directory.ResolveLinkTarget</c> wirft bei fehlendem Pfad und
    /// liefert <c>null</c> für ein echtes Verzeichnis (beides am 2026-09-03 gemessen) —
    /// deshalb die Existenzprüfung davor und der Rückfall danach.
    /// </summary>
    private static string? Resolve(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName
                ?? System.IO.Path.GetFullPath(path);
        }
        catch (IOException)
        {
            // Kaputter Link oder Rechte-Problem: der Pfad selbst ist die beste Auskunft.
            return System.IO.Path.GetFullPath(path);
        }
    }
}
