using System.Text;
using Anvil.Core.Processes;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>Was ein Anwenden werden kann (Spec S3.6 §3.3) — genau ein Ausgang je Lauf.</summary>
public enum ApplyOutcome
{
    /// <summary>Angewandt, befundet, vermerkt.</summary>
    Applied,

    /// <summary>Lag schon da; an der Nutzlast wurde nichts angefasst, nur vermerkt.</summary>
    AlreadyPresent,

    /// <summary>Kein <c>system.reg</c> — das Spiel lief noch nie.</summary>
    PrefixMissing,

    /// <summary>Das Paket gehört zu einer anderen Komponente oder Version.</summary>
    PackageMismatch,

    /// <summary>Das Paket ist unvollständig, leer oder bringt etwas unter <c>users/</c> mit.</summary>
    PackageInvalid,

    /// <summary>Im Werkzeug steckt kein <c>wine</c>.</summary>
    WineMissing,

    /// <summary>Zu wenig Platz.</summary>
    InsufficientSpace,

    /// <summary>Geschrieben, aber der Befund danach ist negativ.</summary>
    NotApplied,

    /// <summary>Abgebrochen; der Prefix ist unvollständig, ein zweiter Lauf holt es nach.</summary>
    Canceled,

    /// <summary>Ein Dateisystemfehler; die Meldung sagt welcher.</summary>
    Failed,
}

/// <summary>Der Auftrag an den Anwender (Spec S3.6 §3.3).</summary>
/// <param name="Required">Die Komponente samt allen Anforderern — für Befund und Vermerk.</param>
/// <param name="PrefixPath">Der <c>pfx</c>-Ordner des Spiels.</param>
/// <param name="Package">Das anzuwendende Paket — vom Aufrufer gewählt (E-1).</param>
/// <param name="Tool">
/// <b>Das Werkzeug, das das Spiel startet</b> (E-5), aus <c>config_info</c>. Ein fremdes Wine
/// könnte den Prefix beim ersten Aufruf auf seinen eigenen Stand heben.
/// </param>
/// <param name="ProtonMajor">
/// Die Hauptversion des Prefix (<see cref="PrefixReport.ProtonMajor"/>) — nicht die des Pakets
/// (Spec S4.1 E-4). S4.2 vergleicht sie mit der, unter der das Spiel dann läuft.
/// </param>
public sealed record ApplyRequest(
    RequiredPrefixComponent Required, string PrefixPath, PrefixPackage Package, CompatTool Tool, int ProtonMajor);

/// <summary>Das Ergebnis eines Anwendens (Spec S3.6 §3.6).</summary>
/// <param name="Outcome">Welcher der zehn Ausgänge.</param>
/// <param name="Message">Ein englischer Satz, der in S3.9 unverändert vor den Nutzer geht.</param>
/// <param name="Record">Der geschriebene Vermerk bei <c>Applied</c> und <c>AlreadyPresent</c>, sonst <c>null</c>.</param>
/// <param name="Elapsed">Wie lange der Lauf gedauert hat.</param>
public sealed record ApplyResult(ApplyOutcome Outcome, string Message, PrefixPayloadRecord? Record, TimeSpan Elapsed);

/// <summary>
/// Bringt ein gebautes Paket in den Prefix eines Spiels — <b>additiv, nie ersetzend</b>
/// (Spec S3.6, Projektplan M3, Entscheidung 10) — und vermerkt danach, was hineinkam.
///
/// <para>
/// <b>Ein Spiel-Prefix ist kein Wegwerf-Prefix.</b> Jeder Schritt schützt etwas: Befund zuerst
/// (fremde Vorbereitung), Pakete mit <c>users/</c> abgelehnt (Spielstände),
/// <see cref="PrefixPayloadFiles"/> (Protons geteilte Dateien), <c>regedit /S</c> statt Schreiben
/// an <c>system.reg</c> (die Registry des Spiels), das Werkzeug des Spiels (E-5, der Prefix-Aufbau).
/// </para>
///
/// <para>
/// <b>Rückgabewerte sagen nichts</b> (S3.5): Ob es getragen hat, sagt der Befund nach
/// <c>wineserver -w</c>. Der Vermerk wird seit S4.1 <b>zweimal</b> geschrieben — vor dem ersten
/// Byte in Stufe <see cref="RollbackStage.Applying"/>, damit der Vorzustand nicht verlorengeht, und
/// nach positivem Befund in Stufe <see cref="RollbackStage.Applied"/>. <b>Nur die zweite Stufe
/// belegt ein gelungenes Anwenden</b>; die erste sagt bloß, dass ein Lauf angefangen hat.
/// </para>
///
/// <para>
/// <b>Nicht hier</b>: ob gebaut werden muss (E-1), ob das Spiel läuft (E-6), Zurückrollen (E-7),
/// Fortschritt (E-8).
/// </para>
/// </summary>
public sealed class PrefixPayloadApplier
{
    // Etwas Luft über der Paketgröße — ein vollgelaufener Datenträger mitten im Kopieren
    // hinterließe einen halb beschriebenen Prefix.
    private const long ReserveBytes = 64L * 1024 * 1024;

    private const long BytesPerMegabyte = 1024L * 1024;

    // Import dauert gemessen Sekundenbruchteile; die Grenze ist eine Reißleine.
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(2);

    // Ein laufendes Spiel hält den Wineserver des Prefixes am Leben (E-6) — dann wird nach einer
    // Minute weitergemacht, und die Meldung nennt genau diesen Grund.
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(60);

    private static readonly UnicodeEncoding RegistryEncoding = new(bigEndian: false, byteOrderMark: true);

    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _freeBytes;
    private readonly ILogger? _logger;

    /// <param name="runner">Die Naht zum Prozess — im Betrieb <see cref="ProcessRunner"/>.</param>
    /// <param name="environment">Die Ausgangsumgebung, gereicht wie beim Bauer (im Betrieb <see cref="ProtonWine.Current"/>).</param>
    /// <param name="time">Uhr für <c>Elapsed</c> und den Zeitpunkt des Vermerks.</param>
    /// <param name="freeBytes">Freier Platz auf dem Datenträger des Pfades; <c>null</c> blockiert nicht.</param>
    /// <param name="logger">Null heißt stumm.</param>
    public PrefixPayloadApplier(
        IProcessRunner runner,
        IReadOnlyDictionary<string, string> environment,
        TimeProvider? time = null,
        Func<string, long?>? freeBytes = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environment);

        _runner = runner;
        _environment = environment;
        _time = time ?? TimeProvider.System;
        _freeBytes = freeBytes ?? DefaultFreeBytes;
        _logger = logger;
    }

    /// <summary>Wendet an — der Ablauf steht in Spec S3.6 §3.3 und hier der Reihe nach.</summary>
    public async Task<ApplyResult> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _time.GetTimestamp();
        var component = request.Required.Component;
        var package = request.Package;
        var prefixPath = request.PrefixPath;
        var label = $"{component.Key} {component.Version}";

        ApplyResult Result(ApplyOutcome outcome, string message, PrefixPayloadRecord? record = null)
            => new(outcome, message, record, _time.GetElapsedTime(startedAt));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 1. Ist das ein Prefix? Der Beweis ist system.reg (Vision Wand Bridge §6.3).
            if (!File.Exists(Path.Combine(prefixPath, "system.reg")))
            {
                return Result(ApplyOutcome.PrefixMissing, "There is no Wine prefix here yet — start the game once.");
            }

            // 2. Befund vorher (E-2): Liegt die Komponente schon da, wird an der Nutzlast nichts
            //    angefasst — auch nicht, wenn jemand anderes sie hingelegt hat.
            if (Probe(prefixPath, component).Present)
            {
                var present = RecordPresent(request);
                return Result(ApplyOutcome.AlreadyPresent, $"{label} is already in this prefix — nothing was changed.", present);
            }

            // 3. Passt das Paket? Schlüssel und Version ja, die Proton-Hauptversion nicht (E-4).
            var manifest = package.Manifest;
            if (manifest.ComponentKey != component.Key || manifest.ComponentVersion != component.Version)
            {
                return Result(
                    ApplyOutcome.PackageMismatch,
                    $"Package {package.Key} holds {manifest.ComponentKey} {manifest.ComponentVersion}, not {label}.");
            }

            // 4. Vollständig und zulässig? Die Dateiliste entsteht hier einmal und wird in 7 benutzt.
            if (!Directory.Exists(package.FilesPath) || !File.Exists(package.RegistryFilePath))
            {
                return Result(ApplyOutcome.PackageInvalid, $"Package {package.Key} is incomplete — it has to be built again.");
            }

            var plan = PrefixPayloadFiles.Plan(package.FilesPath);
            if (plan.RelativePaths.Count == 0)
            {
                return Result(ApplyOutcome.PackageInvalid, $"Package {package.Key} is incomplete — it has to be built again.");
            }

            if (PrefixPayloadFiles.TouchesUsers(plan))
            {
                return Result(
                    ApplyOutcome.PackageInvalid,
                    $"Package {package.Key} contains files under users/ — it was not applied, because saved games live there.");
            }

            // 5. Wine aus dem Werkzeug des Spiels (E-5).
            var winePath = ProtonWine.Find(request.Tool);
            if (winePath is null)
            {
                return Result(ApplyOutcome.WineMissing, $"{request.Tool.Name} has no Wine — the package cannot be applied.");
            }

            // 6. Platz.
            var required = plan.TotalBytes + ReserveBytes;
            var free = _freeBytes(prefixPath);
            if (free is not null && free < required)
            {
                return Result(
                    ApplyOutcome.InsufficientSpace,
                    $"Not enough space: applying needs about {required / BytesPerMegabyte} MB, {free / BytesPerMegabyte} MB are free.");
            }

            // 6a.–6e. Das Rückbau-Protokoll (Spec S4.1 §3.5) — vor dem ersten Byte. Was jetzt nicht
            //         aufgeschrieben ist, ist nach dem Kopieren nicht mehr zu erfahren.
            var environment = ImportEnvironment(prefixPath);

            PrefixPayloadBackup.Clear(prefixPath, component.Key);

            var priorEntries = await Task.Run(
                () => PrefixPayloadBackup.Survey(prefixPath, component.Key, plan, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            var registryBackup = await ExportPriorRegistryAsync(
                winePath, environment, prefixPath, component.Key, manifest.RegistryBranches, cancellationToken)
                .ConfigureAwait(false);

            var rollback = new PayloadRollback(
                PayloadRollback.CurrentFormatVersion,
                RollbackStage.Applying,
                priorEntries,
                manifest.RegistryBranches,
                registryBackup,
                ReadPriorOverrides(prefixPath, component.DllOverrides));

            var record = new PrefixPayloadRecord(
                component.Key, component.Version, PayloadOrigin.Anvil, package.Key,
                request.Required.RequestedBy, _time.GetUtcNow().UtcDateTime)
            {
                ToolName = request.Tool.Name,
                ProtonMajor = request.ProtonMajor,
                Rollback = rollback,
            };
            PrefixPayloadRecords.Write(prefixPath, record);

            // 7. Dateien, auf einem Hintergrund-Strang — rund 700 MB blockieren sonst den Aufrufer.
            var files = await Task.Run(
                () => PrefixPayloadFiles.Apply(package.FilesPath, prefixPath, plan, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            _logger?.LogInformation(
                "payload files applied: {FileCount} files, {TotalBytes} bytes, {ReplacedLinks} symlinks replaced, {SkippedLinks} package symlinks skipped",
                files.FileCount, files.TotalBytes, files.ReplacedLinks, plan.SkippedLinks);

            // 8. Registry, bildschirmlos und ohne geerbtes WINEDLLOVERRIDES.
            await RunAsync(winePath, ["regedit", "/S", package.RegistryFilePath], environment, ImportTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (component.DllOverrides.Count > 0)
            {
                await ImportOverridesAsync(winePath, environment, component.DllOverrides, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 9. Flush: Wine schreibt system.reg/user.reg erst, wenn der Server endet.
            var flushed = await FlushAsync(request.Tool, environment, cancellationToken).ConfigureAwait(false);

            // 10. Befund nachher — derselbe wie vorher.
            var after = Probe(prefixPath, component);
            if (!after.Present)
            {
                var missing = string.Join(", ", after.Missing);
                _logger?.LogWarning("payload did not take: flushed={Flushed}, missing: {Missing}", flushed, missing);
                return Result(
                    ApplyOutcome.NotApplied,
                    flushed
                        ? $"The package was applied, but the prefix still lacks: {missing}."
                        : $"Wine did not finish writing the registry — is the game still running? Still missing: {missing}.");
            }

            // 11. Erst jetzt ist der Vermerk ein Beleg: Stufe `applied`, mit den Prüfsummen dessen,
            //     was wirklich im Prefix liegt (Spec S4.1 §3.1).
            var finished = record with
            {
                Rollback = rollback with
                {
                    Stage = RollbackStage.Applied,
                    Entries = [.. priorEntries.Select(entry => files.Sha256ByPath.TryGetValue(entry.Path, out var sha)
                        ? entry with { AppliedSha256 = sha }
                        : entry)],
                },
            };
            PrefixPayloadRecords.Write(prefixPath, finished);

            return Result(ApplyOutcome.Applied, $"{label} was applied from package {package.Key}.", finished);
        }
        catch (OperationCanceledException)
        {
            // Kein Zurückrollen (E-7): Der Befund bleibt negativ, und der Vermerk steht — wenn
            // überhaupt — in Stufe `applying`, ist also ein Hinweis und keine Handlungsgrundlage
            // (Spec S4.1 §3.1).
            _logger?.LogInformation("applying canceled: {Label}", label);
            return Result(ApplyOutcome.Canceled, "Applying was canceled — the prefix is incomplete; applying again finishes it.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nur Dateisystemfehler — alles andere wäre ein Programmierfehler und soll auffallen.
            _logger?.LogWarning(exception, "applying failed: {Label}", label);
            return Result(ApplyOutcome.Failed, exception.Message);
        }
    }

    private PrefixFinding Probe(string prefixPath, PrefixComponent component)
    {
        var warnings = new List<string>();
        var finding = PrefixMarkProbe.Detect(prefixPath, component, warnings);
        foreach (var warning in warnings)
        {
            _logger?.LogWarning("prefix probe warning: {Warning}", warning);
        }

        return finding;
    }

    // AlreadyPresent: Ein eigener Vermerk derselben Version bekommt die neuen Anforderer dazu und
    // behält Herkunft und Paket; sonst entsteht ein foreign-Vermerk. Unverändertes wird nicht
    // neu geschrieben.
    private PrefixPayloadRecord RecordPresent(ApplyRequest request)
    {
        var component = request.Required.Component;
        var existing = PrefixPayloadRecords.Read(request.PrefixPath, component.Key, _logger);

        if (existing is not null && existing.ComponentVersion == component.Version)
        {
            var requestedBy = existing.RequestedBy.ToList();
            foreach (var moduleKey in request.Required.RequestedBy)
            {
                if (!requestedBy.Contains(moduleKey))
                {
                    requestedBy.Add(moduleKey);
                }
            }

            if (requestedBy.Count == existing.RequestedBy.Count)
            {
                return existing;
            }

            var merged = existing with { RequestedBy = requestedBy };
            PrefixPayloadRecords.Write(request.PrefixPath, merged);
            return merged;
        }

        var foreign = new PrefixPayloadRecord(
            component.Key, component.Version, PayloadOrigin.Foreign, null,
            request.Required.RequestedBy, _time.GetUtcNow().UtcDateTime);
        PrefixPayloadRecords.Write(request.PrefixPath, foreign);
        return foreign;
    }

    // E-8: Der Export ist ein Lesevorgang vor dem Schreiben. Sein Ausfall kostet die
    // Rückbaubarkeit der Registry, nicht den Prefix — die Dateien bleiben rückbaubar, und das ist
    // der Teil, der in Versuch D getragen hat.
    private async Task<bool> ExportPriorRegistryAsync(
        string winePath,
        IReadOnlyDictionary<string, string> environment,
        string prefixPath,
        string componentKey,
        IReadOnlyList<string> branches,
        CancellationToken cancellationToken)
    {
        if (branches.Count == 0)
        {
            return false;
        }

        try
        {
            var result = await new WineRegistryExport(_runner, _logger).ExportAsync(
                winePath,
                environment,
                branches,
                PrefixPayloadBackup.RegistryFilePathFor(prefixPath, componentKey),
                cancellationToken).ConfigureAwait(false);

            if (!result.Ok)
            {
                _logger?.LogInformation("no registry branch of the package exists yet — nothing to back up");
            }

            return result.Ok;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                exception, "the prior registry export failed — the registry will not be rollbackable");
            return false;
        }
    }

    // Aus user.reg gelesen statt als Zweig ausgeführt (E-9): WineRegistryExport kann nur HKLM, und
    // die Overrides stehen in HKCU. null heißt, der Wert fehlte — der Rückbau entfernt ihn dann.
    private IReadOnlyList<PriorOverride> ReadPriorOverrides(
        string prefixPath, IReadOnlyList<DllOverride> dllOverrides)
    {
        var userRegPath = Path.Combine(prefixPath, "user.reg");
        var warnings = new List<string>();
        var values = dllOverrides
            .Select(dllOverride => new PriorOverride(
                dllOverride.Name,
                WineRegistry.ReadString(userRegPath, PrefixMarkProbe.DllOverridesKey, dllOverride.Name, warnings)))
            .ToList();

        foreach (var warning in warnings)
        {
            _logger?.LogWarning("prior override read warning: {Warning}", warning);
        }

        return values;
    }

    private IReadOnlyDictionary<string, string> ImportEnvironment(string prefixPath)
    {
        // Ohne Bildschirm, sonst hängt regedit an einem unsichtbaren Dialog (ProtonWine). Ohne
        // WINEDLLOVERRIDES, sonst liefe ein geerbter Wert aus der Umgebung des Nutzers mit.
        var environment = new Dictionary<string, string>(
            ProtonWine.RegistryEnvironment(_environment, prefixPath), StringComparer.Ordinal);
        environment.Remove("WINEDLLOVERRIDES");
        return environment;
    }

    // Eine erzeugte .reg statt "wine reg add" (Spec S3.6 §3.5): ein Werkzeug für beide
    // Schreibvorgänge, und der Stern in *mscoree durchläuft keine zweite Kommandozeilen-Auswertung.
    // Die Datei liegt außerhalb des Prefixes und verschwindet im finally.
    private async Task ImportOverridesAsync(
        string winePath,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<DllOverride> dllOverrides,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .Append("Windows Registry Editor Version 5.00\r\n\r\n")
            .Append("[HKEY_CURRENT_USER\\")
            .Append(PrefixMarkProbe.DllOverridesKey)
            .Append("]\r\n");
        foreach (var dllOverride in dllOverrides)
        {
            text.Append('"').Append(dllOverride.Name).Append("\"=\"").Append(dllOverride.Value).Append("\"\r\n");
        }

        text.Append("\r\n");

        var directory = Directory.CreateTempSubdirectory("anvil-dll-overrides-");
        try
        {
            var path = Path.Combine(directory.FullName, "overrides.reg");
            await File.WriteAllTextAsync(path, text.ToString(), RegistryEncoding, cancellationToken).ConfigureAwait(false);
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
                _logger?.LogWarning(exception, "override work directory could not be removed: {Path}", directory.FullName);
            }
        }
    }

    // true, wenn der Server sich beendet hat — oder wenn es keinen gibt: Dann ist ein negativer
    // Befund nicht dem laufenden Spiel zuzuschreiben, und die Meldung soll das nicht behaupten.
    private async Task<bool> FlushAsync(
        CompatTool tool, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var serverPath = ProtonWine.FindServer(tool);
        if (serverPath is null)
        {
            _logger?.LogWarning("{Tool} has no wineserver: the registry flush is skipped", tool.Name);
            return true;
        }

        var outcome = await RunAsync(serverPath, ["-w"], environment, FlushTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Completed;
    }

    // Der ExitCode geht ins Log und nirgendwohin sonst (S3.5, Plan P-2).
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
            "apply call finished: {FileName} {Arguments}, completed={Completed}, exitCode={ExitCode}",
            fileName, string.Join(' ', arguments), outcome.Completed, outcome.ExitCode);
        return outcome;
    }

    // Dieselbe Regel wie beim Bauer: das erste vorhandene Elternverzeichnis fragen; jeder
    // Fehlschlag ergibt null, und null blockiert nicht.
    private static long? DefaultFreeBytes(string path)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            while (directory is not null && !directory.Exists)
            {
                directory = directory.Parent;
            }

            return directory is null ? null : new DriveInfo(directory.FullName).AvailableFreeSpace;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
