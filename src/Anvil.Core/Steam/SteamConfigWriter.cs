using System.Globalization;
using Anvil.Core.Vdf;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Steam;

/// <summary>
/// Trägt Wünsche in Steams zwei Konfigurationsdateien ein — und ändert dabei <b>genau</b>
/// die gewünschten Werte; alles andere bleibt Byte für Byte stehen (Spec S4.3 §4.6).
///
/// <para>
/// <b>Reihenfolge je Datei, und warum sie die Sicherheitszusage ist:</b> laden → im Speicher
/// ändern → Text vergleichen (gleich: fertig, kein Byte, keine Sicherung) → sichern über den
/// gereichten Delegaten → atomar schreiben → neu laden und jeden Wunsch prüfen. Alles, was
/// schiefgehen kann, passiert vor dem ersten Byte; <see cref="AtomicFile"/> kennt keinen
/// halben Zustand. Erst <c>config.vdf</c>, dann <c>localconfig.vdf</c>; scheitert die erste,
/// wird die zweite nicht angefasst.
/// </para>
///
/// <para>
/// <b>Der Sicherungs-Delegat kommt je Aufruf, nicht im Konstruktor</b> (E-I): Er ist die
/// Journal-Naht der Transaktion — S4.4 reicht einen, der <c>BackupStore.Create</c> ruft und
/// den Pfad ins Journal schreibt, <i>bevor</i> er zurückkehrt. So steht der Sicherungspfad im
/// Journal, ehe geschrieben wird (Projektplan M4, Korrektur a).
/// </para>
///
/// <para>
/// <b>Ergebnisse sind Werte, nie Ausnahmen im Normalfall</b> (E-J): <c>IOException</c>,
/// <c>UnauthorizedAccessException</c> und <c>VdfParseException</c> — auch aus den Delegaten —
/// werden zu <see cref="SteamFileOutcome.Failed"/> mit Klartext. Alles andere ist ein
/// Programmierfehler und fliegt durch.
/// </para>
///
/// <para>
/// <b>Kein Riegel „läuft Steam".</b> Das ist der Vertrag der Transaktion (S4.4) — hier wäre er
/// die zweite Wahrheit. Steam liest diese Dateien nur beim Start und überschreibt sie bei
/// jedem Spielstart (Projektplan M4, „Es gibt kein Schreiben im Betrieb").
/// </para>
/// </summary>
public sealed class SteamConfigWriter
{
    private const string ConfigRoot = "InstallConfigStore";
    private const string LocalConfigRoot = "UserLocalConfigStore";

    private static readonly string[] MappingPath = ["Software", "Valve", "Steam", "CompatToolMapping"];

    /// <summary>
    /// Der volle Pfad zu den Spiel-Einträgen. Die echte Datei hat einen zweiten Knoten
    /// <c>apps</c> unter <c>WebStorage</c> — deshalb nie „such <c>apps</c>".
    /// </summary>
    private static readonly string[] AppsPath = ["Software", "Valve", "Steam", "apps"];

    private readonly Func<SteamSnapshot> _current;
    private readonly ILogger? _logger;
    private readonly Action<string, string> _write;

    /// <param name="current">Die aktuelle Momentaufnahme — Wurzel und Konto für die Pfade.</param>
    /// <param name="logger">Wohin die Schritte gemeldet werden; <c>null</c> schweigt.</param>
    /// <param name="write">
    /// Test-Naht (E-L), Standard <see cref="AtomicFile.WriteAllText"/>: nur damit ein Test
    /// einen verstümmelten Schreiber einsetzen und die Prüfung nach dem Schreiben belegen kann.
    /// </param>
    public SteamConfigWriter(
        Func<SteamSnapshot> current,
        ILogger? logger = null,
        Action<string, string>? write = null)
    {
        _current = current;
        _logger = logger;
        _write = write ?? AtomicFile.WriteAllText;
    }

    /// <summary>
    /// Wendet die Wünsche an. <paramref name="backup"/> bekommt den Dateipfad und liefert den
    /// Pfad der Sicherung; gerufen wird er je Datei genau dann, wenn sich ihr Text ändert, und
    /// <b>vor</b> dem Schreiben.
    /// </summary>
    public SteamWriteResult Apply(IReadOnlyList<SteamGameChange> changes, Func<string, string> backup)
    {
        var snapshot = _current();
        if (snapshot.RootPath is null)
        {
            const string Error = "No Steam installation found.";
            _logger?.LogWarning("could not write steam config: {Error}", Error);
            return new SteamWriteResult(
                new SteamFileResult(SteamConfigPaths.ConfigFileName, SteamFileOutcome.Failed, null, Error),
                new SteamFileResult(SteamConfigPaths.LocalConfigFileName, SteamFileOutcome.Failed, null, Error));
        }

        var protonChanges = changes.Where(change => change.Proton is not null).ToList();
        var optionChanges = changes.Where(change => change.LaunchOptions is not null).ToList();

        var config = ApplyToFile(
            SteamConfigPaths.Config(snapshot.RootPath),
            ConfigRoot,
            protonChanges,
            ApplyProton,
            VerifyProton,
            backup);

        var localPath = snapshot.User is null
            ? SteamConfigPaths.LocalConfigFileName
            : SteamConfigPaths.LocalConfig(snapshot.RootPath, snapshot.User.AccountId);

        SteamFileResult localConfig;
        if (config.Outcome == SteamFileOutcome.Failed)
        {
            _logger?.LogInformation(
                "{File} skipped — {Previous} failed",
                SteamConfigPaths.LocalConfigFileName, SteamConfigPaths.ConfigFileName);
            localConfig = new SteamFileResult(localPath, SteamFileOutcome.Skipped, null, null);
        }
        else if (optionChanges.Count > 0 && snapshot.User is null)
        {
            const string Error = "No Steam account found — launch options cannot be written.";
            _logger?.LogWarning("could not write {File}: {Error}", SteamConfigPaths.LocalConfigFileName, Error);
            localConfig = new SteamFileResult(localPath, SteamFileOutcome.Failed, null, Error);
        }
        else
        {
            localConfig = ApplyToFile(
                localPath, LocalConfigRoot, optionChanges, ApplyLaunchOptions, VerifyLaunchOptions, backup);
        }

        return new SteamWriteResult(config, localConfig);
    }

    /// <summary>
    /// Der Dateischritt. <paramref name="mutate"/> ändert im Speicher und liefert einen
    /// Fehlertext oder <c>null</c>; <paramref name="verify"/> prüft die neu geladene Datei
    /// gegen einen Wunsch und liefert ebenso Fehlertext oder <c>null</c>.
    /// </summary>
    private SteamFileResult ApplyToFile(
        string path,
        string rootKey,
        IReadOnlyList<SteamGameChange> changes,
        Func<VdfObject, SteamGameChange, string?> mutate,
        Func<VdfObject, SteamGameChange, string?> verify,
        Func<string, string> backup)
    {
        var file = Path.GetFileName(path);
        if (changes.Count == 0)
        {
            _logger?.LogInformation("{File} unchanged", file);
            return new SteamFileResult(path, SteamFileOutcome.Unchanged, null, null);
        }

        string? backupPath = null;
        try
        {
            var document = VdfDocument.Load(path);
            var store = document.Root.FindObject(rootKey);
            if (store is null)
            {
                return Failed(path, null, $"Not a Steam config file (missing '{rootKey}'): {path}");
            }

            var before = document.ToText();
            foreach (var change in changes)
            {
                if (mutate(store, change) is { } problem)
                {
                    return Failed(path, null, $"Cannot change {file}: {problem}");
                }
            }

            var after = document.ToText();
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                _logger?.LogInformation("{File} unchanged", file);
                return new SteamFileResult(path, SteamFileOutcome.Unchanged, null, null);
            }

            _logger?.LogInformation("writing {File}: {Count} change(s)", file, changes.Count);
            backupPath = backup(path);
            _write(path, after);

            var written = VdfDocument.Load(path).Root.FindObject(rootKey);
            var mismatch = written is null
                ? $"'{rootKey}' is gone"
                : changes.Select(change => verify(written, change)).FirstOrDefault(problem => problem is not null);
            if (mismatch is not null)
            {
                return Failed(path, backupPath, mismatch);
            }

            _logger?.LogInformation("wrote {File} (backup: {Backup})", file, backupPath);
            return new SteamFileResult(path, SteamFileOutcome.Written, backupPath, null);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or VdfParseException)
        {
            return Failed(path, backupPath, exception.Message, exception);
        }
    }

    /// <summary>Fehlschlag als Wert. Mit Sicherung heißt es: geschrieben, aber nicht bestätigt.</summary>
    private SteamFileResult Failed(string path, string? backupPath, string problem, Exception? exception = null)
    {
        var file = Path.GetFileName(path);
        if (backupPath is null)
        {
            _logger?.LogWarning(exception, "could not write {File}: {Error}", file, problem);
            return new SteamFileResult(path, SteamFileOutcome.Failed, null, problem);
        }

        _logger?.LogWarning(
            exception, "wrote {File}, but it did not read back — backup: {Backup}: {Error}",
            file, backupPath, problem);
        return new SteamFileResult(
            path, SteamFileOutcome.Failed, backupPath,
            $"Wrote {file}, but {problem} — backup: {backupPath}");
    }

    /// <summary>
    /// Die Proton-Wahl (Spec §4.6.1). Auto entfernt den ganzen Block — auch einen mit leerem
    /// Namen; ein neuer Block bekommt Steams drei Schlüssel aus Messung 1.
    /// </summary>
    private static string? ApplyProton(VdfObject store, SteamGameChange change)
    {
        var key = AppKey(change.AppId);
        var choice = change.Proton!;
        var mapping = store.FindObject(MappingPath);
        var entry = mapping?.FindObject(key);

        if (choice.IsAuto)
        {
            mapping?.Remove(key);
            return null;
        }

        if (entry is not null)
        {
            // Nur setzen, wenn anders (E-H): SetString quotet den Rohtext neu, und ein gleicher
            // Wert mit anderem Original-Escaping ergäbe eine Änderung ohne Änderung.
            if (entry.Find("name") is VdfString name)
            {
                if (!string.Equals(name.Value, choice.ToolKey, StringComparison.Ordinal))
                {
                    entry.SetString("name", choice.ToolKey!);
                }
            }
            else
            {
                entry.AddString("name", choice.ToolKey!);
            }

            return null;
        }

        mapping = EnsurePath(store, MappingPath, out var error);
        if (mapping is null)
        {
            return error;
        }

        var created = mapping.AddObject(key);
        created.AddString("name", choice.ToolKey!);
        created.AddString("config", string.Empty);
        created.AddString("priority", "250");
        return null;
    }

    private static string? VerifyProton(VdfObject store, SteamGameChange change)
    {
        var expected = change.Proton!.ToolKey;
        string[] path = [.. MappingPath, AppKey(change.AppId), "name"];
        var actual = store.FindString(path)?.Value;
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? null
            : $"compat tool for app {change.AppId} did not read back "
                + $"(expected '{expected ?? "none"}', found '{actual ?? "none"}')";
    }

    /// <summary>
    /// Die Startoptionen (Spec §4.6.2), wörtlich (E-G). Ein leerer Wunsch leert einen
    /// vorhandenen Eintrag und lässt den Schlüssel stehen — so führt Steam leere Optionen
    /// selbst —, legt aber keinen an.
    /// </summary>
    private static string? ApplyLaunchOptions(VdfObject store, SteamGameChange change)
    {
        var key = AppKey(change.AppId);
        var desired = change.LaunchOptions!;
        var apps = store.FindObject(AppsPath);
        var entry = apps?.FindObject(key);

        if (entry?.Find("LaunchOptions") is VdfString existing)
        {
            if (!string.Equals(existing.Value, desired, StringComparison.Ordinal))
            {
                entry.SetString("LaunchOptions", desired);
            }

            return null;
        }

        if (desired.Length == 0)
        {
            return null;
        }

        string? error = null;
        apps ??= EnsurePath(store, AppsPath, out error);
        if (apps is null)
        {
            return error;
        }

        entry ??= apps.AddObject(key);
        entry.AddString("LaunchOptions", desired);
        return null;
    }

    private static string? VerifyLaunchOptions(VdfObject store, SteamGameChange change)
    {
        var desired = change.LaunchOptions!;
        var actual = store.FindString([.. AppsPath, AppKey(change.AppId), "LaunchOptions"])?.Value;
        var matches = desired.Length == 0
            ? string.IsNullOrEmpty(actual)
            : string.Equals(actual, desired, StringComparison.Ordinal);
        return matches
            ? null
            : $"launch options for app {change.AppId} did not read back "
                + $"(expected '{desired}', found '{actual ?? "none"}')";
    }

    private static string AppKey(uint appId) => appId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Legt fehlende Glieder des Pfades an (E-K). Wird erst gerufen, wenn feststeht, dass
    /// wirklich ein Wert geschrieben wird — sonst entstünden leere Knoten. Ein Wert, wo ein
    /// Objekt stehen müsste, ist ein Fehler, keine Reparatur.
    /// </summary>
    private static VdfObject? EnsurePath(VdfObject start, string[] keys, out string? error)
    {
        error = null;
        var current = start;
        foreach (var key in keys)
        {
            var child = current.FindObject(key);
            if (child is null)
            {
                if (current.Find(key) is not null)
                {
                    error = $"'{key}' is a value where a section was expected";
                    return null;
                }

                child = current.AddObject(key);
            }

            current = child;
        }

        return current;
    }
}
