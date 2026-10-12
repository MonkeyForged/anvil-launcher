using System.Text;
using Anvil.Core.Processes;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>Was ein Rückbau werden kann (Spec S4.2 §3.2) — genau ein Ausgang je Lauf.</summary>
public enum RevertOutcome
{
    /// <summary>Zurückgebaut, nachgeprüft, der Vermerk ist weg.</summary>
    Reverted,

    /// <summary>Es gab nichts zurückzubauen — kein Vermerk, dieselbe Hauptversion, oder zurückgestuft.</summary>
    NothingToDo,

    /// <summary>Ein Vermerk ohne vollständiges Protokoll (Spec S4.2 E-5). Es geschieht nichts.</summary>
    NotRevertable,

    /// <summary>Zurückgebaut, aber der Befund ist danach <b>noch immer</b> positiv.</summary>
    StillPresent,

    /// <summary>Im Werkzeug des Spiels steckt kein <c>wine</c>.</summary>
    WineMissing,

    /// <summary>Abgebrochen; ein zweiter Lauf holt den Rest nach.</summary>
    Canceled,

    /// <summary>Ein Dateisystemfehler; die Meldung sagt welcher.</summary>
    Failed,
}

/// <summary>Der Auftrag an den Rückbau (Spec S4.2 §3.2).</summary>
/// <param name="Component">Die Komponente — für Befund, Sicherungsordner und DllOverrides.</param>
/// <param name="PrefixPath">Der <c>pfx</c>-Ordner des Spiels.</param>
/// <param name="Tool">Das Werkzeug, das das Spiel startet — dieselbe Regel wie beim Anwenden (S3.6 E-5).</param>
/// <param name="ProtonMajor">Die Hauptversion des Prefix <b>jetzt</b>; verglichen wird sie mit der im Vermerk.</param>
public sealed record RevertRequest(
    PrefixComponent Component, string PrefixPath, CompatTool Tool, int ProtonMajor);

/// <summary>Was der Rückbau angefasst hat — für das Log und die Live-Messung (Spec S4.2 §5.7).</summary>
public sealed record RevertCounts(
    int Restored,
    int LeftStanding,
    int LinksRestored,
    int DanglingLinks,
    int DirectoriesRemoved,
    int RegistryBranches,
    bool RegistryImported)
{
    /// <summary>Für die Ausgänge, die nichts anfassen.</summary>
    public static RevertCounts None { get; } = new(0, 0, 0, 0, 0, 0, RegistryImported: false);
}

/// <summary>Das Ergebnis eines Rückbaus (Spec S4.2 §3.2).</summary>
/// <param name="Outcome">Welcher der sieben Ausgänge.</param>
/// <param name="Message">Ein englischer Satz, der bei Scheitern unverändert vor den Nutzer geht.</param>
/// <param name="Counts">Was angefasst wurde.</param>
/// <param name="Elapsed">Wie lange der Lauf gedauert hat.</param>
public sealed record RevertResult(
    RevertOutcome Outcome, string Message, RevertCounts Counts, TimeSpan Elapsed);

/// <summary>
/// Baut eine Nutzlast aus dem Prefix eines Spiels zurück — <b>der erste Leser des Protokolls</b>
/// aus S4.1 (Spec S4.2 §3.3). Gegenstück zu <see cref="PrefixPayloadApplier"/> und in derselben
/// Bauform: <see cref="IProcessRunner"/>, bildschirmlose Umgebung ohne <c>WINEDLLOVERRIDES</c>,
/// eine erzeugte <c>.reg</c> außerhalb des Prefix, <c>wineserver -w</c> vor dem Nachprüfen,
/// Ausgänge als Aufzählung statt Ausnahmen.
///
/// <para>
/// <b>Er entscheidet nichts selbst.</b> Ob überhaupt zurückzubauen ist, sagt
/// <see cref="PrefixPayloadStanding"/> — dieselbe Stelle, die auch der Vorbereiter und die
/// Modulkarte fragen. Und <b>die Regel aus Versuch D</b> — zurückgebaut wird nur, was noch die
/// Prüfsumme des Protokolls trägt — wohnt in <see cref="PrefixPayloadRevertFiles"/>, nicht hier.
/// </para>
///
/// <para>
/// <b>Der Befund ist der Beweis</b> (S3.5): Nicht der Rückgabewert von <c>regedit</c> sagt, dass es
/// getragen hat, sondern <see cref="PrefixMarkProbe.Detect"/> nach <c>wineserver -w</c>. Ist er danach
/// <b>noch positiv</b>, ist der Lauf gescheitert (<see cref="RevertOutcome.StillPresent"/>) und der
/// Vermerk bleibt — genau die Gegenprobe aus Versuch D, jetzt im Code statt im Bericht.
/// </para>
///
/// <para>
/// <b>Kein Zurückrollen des Rückbaus</b> (S3.6 E-7), dafür wiederholbar: Was schon zurückgesetzt ist,
/// trägt die Prüfsumme nicht mehr und wird beim zweiten Lauf stehen gelassen.
/// </para>
/// </summary>
public sealed class PrefixPayloadReverter
{
    // Import dauert gemessen Sekundenbruchteile; die Grenze ist eine Reißleine.
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(2);

    // Ein laufendes Spiel hält den Wineserver am Leben — dann wird nach einer Minute weitergemacht
    // und die Nachprüfung entscheidet, wie beim Anwenden.
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(60);

    private static readonly UnicodeEncoding RegistryEncoding = new(bigEndian: false, byteOrderMark: true);

    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;

    /// <param name="runner">Die Naht zum Prozess — im Betrieb <see cref="ProcessRunner"/>.</param>
    /// <param name="environment">Die Ausgangsumgebung, gereicht wie beim Anwender (im Betrieb <see cref="ProtonWine.Current"/>).</param>
    /// <param name="time">Uhr für <c>Elapsed</c>.</param>
    /// <param name="logger">Null heißt stumm.</param>
    public PrefixPayloadReverter(
        IProcessRunner runner,
        IReadOnlyDictionary<string, string> environment,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environment);

        _runner = runner;
        _environment = environment;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Baut zurück — der Ablauf steht in Spec S4.2 §3.3 und hier der Reihe nach.</summary>
    public async Task<RevertResult> RevertAsync(RevertRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _time.GetTimestamp();
        var component = request.Component;
        var prefixPath = request.PrefixPath;
        var label = $"{component.Key} {component.Version}";

        RevertResult Result(RevertOutcome outcome, string message, RevertCounts? counts = null)
            => new(outcome, message, counts ?? RevertCounts.None, _time.GetElapsedTime(startedAt));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Urteil holen — an einer Stelle für drei Frager (Spec S4.2 §3.1). Gearbeitet wird
            //    nur bei `Stale` und `Revertable`.
            var standing = PrefixPayloadStanding.Of(prefixPath, component.Key, request.ProtonMajor, _logger);
            if (standing.Kind != PayloadStandingKind.Stale)
            {
                return Result(RevertOutcome.NothingToDo, NothingToDoMessage(standing.Kind, label));
            }

            if (!standing.Revertable)
            {
                // E-5: nichts tun, nichts behaupten — eine Logzeile ist alles, was hier ehrlich ist.
                _logger?.LogInformation(
                    "payload of {Label} is stale but has no applied rollback protocol — left untouched", label);
                return Result(
                    RevertOutcome.NotRevertable,
                    $"The payload record for {label} has no complete rollback protocol — nothing was changed.");
            }

            // Der Vermerk wird ein zweites Mal gelesen: Das Urteil ist ein Urteil, kein Datenträger,
            // und ein zweiter, teilweiser Leser wäre eine zweite Auslegung derselben Datei (E-6).
            var record = PrefixPayloadRecords.Read(prefixPath, component.Key, _logger);
            if (record?.Rollback is not { } rollback)
            {
                return Result(RevertOutcome.NothingToDo, NothingToDoMessage(PayloadStandingKind.None, label));
            }

            // 2. Wine aus dem Werkzeug des Spiels (S3.6 E-5) — ein fremdes höbe den Prefix beim
            //    ersten Aufruf auf seinen eigenen Stand.
            var winePath = ProtonWine.Find(request.Tool);
            if (winePath is null)
            {
                return Result(
                    RevertOutcome.WineMissing, $"{request.Tool.Name} has no Wine — the payload cannot be reverted.");
            }

            // 3./4. Dateien und Verzeichnisebenen, auf einem Hintergrund-Strang: Das Prüfsummen-Lesen
            //       von rund 700 MB blockiert sonst den Aufrufer (gemessen 422 ms für 682 MB).
            var warnings = new List<string>();
            var files = await Task.Run(
                () => PrefixPayloadRevertFiles.Revert(prefixPath, component.Key, rollback, warnings, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            foreach (var warning in warnings)
            {
                _logger?.LogWarning("revert warning: {Warning}", warning);
            }

            _logger?.LogInformation(
                "payload files reverted: {Restored} restored, {LeftStanding} left standing, {LinksRestored} symlinks restored ({DanglingLinks} dangling), {DirectoriesRemoved} directories removed",
                files.Restored, files.LeftStanding, files.LinksRestored, files.DanglingLinks, files.DirectoriesRemoved);

            // 5. Registry, bildschirmlos und ohne geerbtes WINEDLLOVERRIDES.
            var environment = ImportEnvironment(prefixPath);
            var registryImported = await RevertRegistryAsync(
                winePath, environment, prefixPath, component.Key, rollback, cancellationToken).ConfigureAwait(false);

            var counts = new RevertCounts(
                files.Restored, files.LeftStanding, files.LinksRestored, files.DanglingLinks,
                files.DirectoriesRemoved, rollback.RegistryBranches.Count, registryImported);

            // 6. Flush: Wine schreibt system.reg/user.reg erst, wenn der Server endet.
            await FlushAsync(request.Tool, environment, cancellationToken).ConfigureAwait(false);

            // 7. Nachprüfen — der Beweis. Gefragt wird `Present`, und zwar weil der Anwender an
            //    genau dieser Frage entscheidet, ob er arbeitet: Bei `Present` endet er mit
            //    `AlreadyPresent` und fasst nichts an. Ein einzelnes stehengebliebenes Merkmal ist
            //    dagegen der erwartete Fall aus Versuch D — eine Datei, die Proton seit dem
            //    Anwenden ersetzt hat, muss stehen bleiben, und das folgende Anwenden überschreibt
            //    sie ohnehin. Danach zu scheitern hieße, den Schritt an seinem eigenen Erfolg zu
            //    zerbrechen.
            var after = PrefixMarkProbe.Detect(prefixPath, component, warnings);
            if (after.Present)
            {
                _logger?.LogWarning("revert did not take: {Label} is still detected in this prefix", label);
                return Result(
                    RevertOutcome.StillPresent,
                    $"The rollback did not take: {label} is still detected in this prefix.",
                    counts);
            }

            // 8. Vermerk und Sicherungen weg (E-8): Eine Wahrheit je Nutzlast, und die Nutzlast ist
            //    weg. Ein stehengebliebener Vermerk wäre eine Behauptung über etwas, das es nicht
            //    mehr gibt.
            File.Delete(PrefixPayloadRecords.PathFor(prefixPath, component.Key));
            PrefixPayloadBackup.Clear(prefixPath, component.Key);

            return Result(RevertOutcome.Reverted, $"{label} was removed from this prefix.", counts);
        }
        catch (OperationCanceledException)
        {
            // Kein Zurückrollen des Rückbaus (S3.6 E-7): Der Lauf ist wiederholbar, weil das schon
            // Zurückgesetzte die Prüfsumme des Protokolls nicht mehr trägt.
            _logger?.LogInformation("revert canceled: {Label}", label);
            return Result(RevertOutcome.Canceled, "The rollback was canceled — running it again finishes it.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nur Dateisystemfehler — alles andere wäre ein Programmierfehler und soll auffallen.
            _logger?.LogWarning(exception, "revert failed: {Label}", label);
            return Result(RevertOutcome.Failed, exception.Message);
        }
    }

    // Jeder Fall bekommt seinen eigenen Satz: „nichts zu tun" hat vier Gründe, und der Unterschied
    // zwischen ihnen ist das, was im Log später weiterhilft.
    private static string NothingToDoMessage(PayloadStandingKind kind, string label) => kind switch
    {
        PayloadStandingKind.Current =>
            $"{label} was prepared for the Proton version this game runs — nothing to revert.",
        PayloadStandingKind.MovedBack =>
            $"This prefix was prepared for a newer Proton version than it runs now — Anvil does not revert that.",
        PayloadStandingKind.Unknown =>
            $"The payload record for {label} does not say which Proton version it was prepared for — nothing to revert.",
        _ => $"There is no Anvil payload record for {label} — nothing to revert.",
    };

    private IReadOnlyDictionary<string, string> ImportEnvironment(string prefixPath)
    {
        // Ohne Bildschirm, sonst hängt regedit an einem unsichtbaren Dialog (ProtonWine). Ohne
        // WINEDLLOVERRIDES, sonst liefe ein geerbter Wert aus der Umgebung des Nutzers mit.
        var environment = new Dictionary<string, string>(
            ProtonWine.RegistryEnvironment(_environment, prefixPath), StringComparer.Ordinal);
        environment.Remove("WINEDLLOVERRIDES");
        return environment;
    }

    // Löschen und einspielen (Spec S4.2 §3.3, Punkt 5): Erst verschwinden die deklarierten Zweige
    // samt allem, was das Paket hineingeschrieben hat, dann kommt der Vorzustand aus der Ausfuhr
    // zurück. Ein bloßes Einspielen ließe die Werte des Pakets stehen, die in der Ausfuhr nicht
    // vorkommen. Liefert, ob die Ausfuhr eingespielt wurde.
    private async Task<bool> RevertRegistryAsync(
        string winePath,
        IReadOnlyDictionary<string, string> environment,
        string prefixPath,
        string componentKey,
        PayloadRollback rollback,
        CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("anvil-revert-");
        try
        {
            // Außerhalb des Prefix, wie die Override-Datei des Anwenders: Was Anvil erzeugt, hat im
            // Prefix des Spiels nichts verloren.
            var path = Path.Combine(directory.FullName, "revert.reg");
            await File.WriteAllTextAsync(path, BuildRevertRegistry(rollback), RegistryEncoding, cancellationToken)
                .ConfigureAwait(false);
            await RunAsync(winePath, ["regedit", "/S", path], environment, ImportTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(exception, "revert work directory could not be removed: {Path}", directory.FullName);
            }
        }

        // E-4: `false` heißt laut Protokoll „vorher war dort kein Zweig" — dann ist das Löschen
        // allein schon der Vorzustand.
        if (!rollback.RegistryBackup)
        {
            return false;
        }

        var backupPath = PrefixPayloadBackup.RegistryFilePathFor(prefixPath, componentKey);
        if (!File.Exists(backupPath))
        {
            _logger?.LogWarning(
                "the protocol names a registry backup, but it is gone: {Path} — the branches stay deleted", backupPath);
            return false;
        }

        await RunAsync(winePath, ["regedit", "/S", backupPath], environment, ImportTimeout, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    // Eine erzeugte .reg statt "wine reg delete", aus demselben Grund wie beim Anwenden (S3.6 §3.5):
    // ein Werkzeug für beide Schreibvorgänge, und der Stern in *mscoree durchläuft keine zweite
    // Kommandozeilen-Auswertung.
    private static string BuildRevertRegistry(PayloadRollback rollback)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");

        foreach (var branch in rollback.RegistryBranches)
        {
            text.Append("[-HKEY_LOCAL_MACHINE\\").Append(branch).Append("]\r\n\r\n");
        }

        if (rollback.PriorOverrides.Count > 0)
        {
            text.Append("[HKEY_CURRENT_USER\\").Append(PrefixMarkProbe.DllOverridesKey).Append("]\r\n");
            foreach (var priorOverride in rollback.PriorOverrides)
            {
                // "name"=- entfernt den Wert; das ist der Vorzustand, wenn vorher keiner dastand.
                text.Append('"').Append(priorOverride.Name).Append("\"=");
                text.Append(priorOverride.Value is { } value ? $"\"{value}\"" : "-").Append("\r\n");
            }

            text.Append("\r\n");
        }

        return text.ToString();
    }

    // Wie beim Anwenden: Ohne Wineserver gibt es nichts zu leeren, und das ist kein Fehler.
    private async Task FlushAsync(
        CompatTool tool, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var serverPath = ProtonWine.FindServer(tool);
        if (serverPath is null)
        {
            _logger?.LogWarning("{Tool} has no wineserver: the registry flush is skipped", tool.Name);
            return;
        }

        await RunAsync(serverPath, ["-w"], environment, FlushTimeout, cancellationToken).ConfigureAwait(false);
    }

    // Der ExitCode geht ins Log und nirgendwohin sonst (S3.5, Plan P-2) — die Nachprüfung urteilt.
    private async Task<ProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var outcome = await _runner.RunAsync(new ProcessCommand(fileName, arguments, environment), timeout, cancellationToken)
            .ConfigureAwait(false);
        _logger?.LogDebug(
            "revert call finished: {FileName} {Arguments}, completed={Completed}, exitCode={ExitCode}",
            fileName, string.Join(' ', arguments), outcome.Completed, outcome.ExitCode);
        return outcome;
    }
}
