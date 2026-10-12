namespace Anvil.Core;

/// <summary>Löst XDG-Basisverzeichnisse auf; die Umgebungsabfrage ist injizierbar (Tests).</summary>
public static class XdgPaths
{
    /// <summary>Basis für App-Zustand: $XDG_STATE_HOME, sonst ~/.local/state.</summary>
    public static string StateHome(Func<string, string?> getEnv)
    {
        var value = getEnv("XDG_STATE_HOME");
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "state");
    }

    /// <summary>Log-Verzeichnis, fest laut Projektplan: &lt;state&gt;/anvil.</summary>
    public static string LogDirectory(Func<string, string?> getEnv)
        => Path.Combine(StateHome(getEnv), "anvil");

    /// <summary>
    /// Die Sicherungen (S4.1): &lt;state&gt;/anvil/backups. Fester XDG-Ort neben dem Log —
    /// Core-Eigenes wandert nicht mit der Datenwurzel (Vision §4).
    /// </summary>
    public static string BackupDirectory(Func<string, string?> getEnv)
        => Path.Combine(LogDirectory(getEnv), "backups");

    /// <summary>
    /// Der Vermerk der Transaktion (S4.4): &lt;state&gt;/anvil/transaction.json — neben Log und
    /// Sicherungen, weil er zum selben Vorgang gehoert und dieselbe Lebensdauer hat.
    /// </summary>
    public static string TransactionFilePath(Func<string, string?> getEnv)
        => Path.Combine(LogDirectory(getEnv), "transaction.json");

    /// <summary>
    /// Laufzeitverzeichnis (S3.9): $XDG_RUNTIME_DIR/anvil — tmpfs, je Anmeldung, 0700. Ohne
    /// die Variable (kein systemd-Login, Tests) das Log-Verzeichnis: Anvils eigener Ort,
    /// schon vorhanden (Spec S3.9 E-B).
    /// </summary>
    public static string RuntimeDirectory(Func<string, string?> getEnv)
    {
        var value = getEnv("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(value)
            ? LogDirectory(getEnv)
            : Path.Combine(value, "anvil");
    }

    /// <summary>Der Instanz-Socket (S3.9): &lt;runtime&gt;/anvil.sock — Schloss und Briefkasten.</summary>
    public static string InstanceSocketPath(Func<string, string?> getEnv)
        => Path.Combine(RuntimeDirectory(getEnv), "anvil.sock");

    /// <summary>Basis für App-Konfiguration: $XDG_CONFIG_HOME, sonst ~/.config.</summary>
    public static string ConfigHome(Func<string, string?> getEnv)
    {
        var value = getEnv("XDG_CONFIG_HOME");
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    /// <summary>Die Settings-Datei, fest laut Projektplan: &lt;config&gt;/anvil/settings.json.</summary>
    public static string SettingsFilePath(Func<string, string?> getEnv)
        => Path.Combine(ConfigHome(getEnv), "anvil", "settings.json");

    /// <summary>
    /// Der Autostart-Eintrag (S3.6): &lt;config&gt;/autostart/anvil.desktop. Plasma startet, was
    /// hier liegt, beim Anmelden.
    /// </summary>
    public static string AutostartFilePath(Func<string, string?> getEnv)
        => Path.Combine(ConfigHome(getEnv), "autostart", "anvil.desktop");

    /// <summary>Basis für App-Daten: $XDG_DATA_HOME, sonst ~/.local/share.</summary>
    public static string DataHome(Func<string, string?> getEnv)
    {
        var value = getEnv("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(value))
        {
            return value;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share");
    }

    /// <summary>
    /// Die Datenwurzel („Data location", Design-Anforderung 24): &lt;data&gt;/anvil. Jedes Modul
    /// bekommt darunter einen Ordner mit seinem Schluessel; was der Core ueber sich selbst
    /// schreibt (Log, Vermerk, Sicherungen, Einstellungen), liegt an festen XDG-Orten und
    /// wandert nicht mit.
    /// </summary>
    public static string DataDirectory(Func<string, string?> getEnv)
        => Path.Combine(DataHome(getEnv), "anvil");
}
