using Anvil.Core.Processes;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>Was ein Bau werden kann (S3.5, Spec §3.6) — genau ein Ausgang je Lauf.</summary>
public enum BuildOutcome
{
    /// <summary>Das Paket liegt fertig im Speicher.</summary>
    Built,

    /// <summary>Es lag schon eines da; nichts ist passiert.</summary>
    AlreadyPresent,

    /// <summary>Die Komponente ist erkennbar, aber nicht baubar — sie hat kein Rezept.</summary>
    NoRecipe,

    /// <summary>Eine Zutat des Rezepts liegt nicht in der Ablage.</summary>
    IngredientMissing,

    /// <summary>Im Werkzeugordner steckt kein <c>wine</c>.</summary>
    WineMissing,

    /// <summary>Zu wenig Platz auf der Platte.</summary>
    InsufficientSpace,

    /// <summary>Eine Stufe lief nicht zu Ende — oder legte gar keinen Prefix an.</summary>
    StageFailed,

    /// <summary>Die Stufen liefen, aber der Befund im Wegwerf-Prefix ist negativ.</summary>
    NotPrepared,

    /// <summary>Der Nutzer hat abgebrochen.</summary>
    Canceled,

    /// <summary>Etwas anderes ging schief; die Meldung sagt was.</summary>
    Failed,
}

/// <summary>
/// Der Auftrag an den Bauer (S3.5, Spec §3.6).
/// </summary>
/// <param name="Component">Was gebaut werden soll — samt Merkmalen, Nutzlast und Rezept.</param>
/// <param name="ProtonMajor">Die Hauptversion, für die das Paket gilt (Teil des Schlüssels).</param>
/// <param name="Tool">Das Compat-Tool, dessen <c>wine</c> das Rezept ausführt.</param>
/// <param name="IngredientPaths">
/// <c>Slot → Pfad</c>, vom Modul aufgelöst (E-4): Der Core kennt die Ablage eines Moduls nicht und
/// soll sie nicht lernen. Er prüft nur, dass die Dateien existieren.
/// </param>
public sealed record BuildRequest(
    PrefixComponent Component,
    int ProtonMajor,
    CompatTool Tool,
    IReadOnlyDictionary<string, string> IngredientPaths);

/// <summary>
/// Eine Fortschrittsmeldung (S3.5, Spec §3.8) — <b>gezählt werden Stufen, nie Prozente</b>.
/// </summary>
/// <param name="Stage">Die laufende Stufe, ab 1.</param>
/// <param name="StageCount">Rezeptstufen plus Befund plus Ernte (E-5).</param>
/// <param name="Label">Das Label der Stufe, englisch — geht unverändert vor den Nutzer.</param>
/// <param name="Elapsed">Wie lange der Bau insgesamt schon läuft.</param>
public sealed record BuildProgress(int Stage, int StageCount, string Label, TimeSpan Elapsed);

/// <summary>Das Ergebnis eines Baus (S3.5, Spec §3.6).</summary>
/// <param name="Outcome">Welcher der zehn Ausgänge.</param>
/// <param name="Message">Ein englischer Satz, der in S3.9 unverändert vor den Nutzer geht.</param>
/// <param name="Package">Das Paket — bei <see cref="BuildOutcome.Built"/> das neue, bei
/// <see cref="BuildOutcome.AlreadyPresent"/> das vorhandene, sonst <c>null</c>.</param>
/// <param name="Elapsed">Wie lange der Lauf gedauert hat.</param>
public sealed record BuildResult(
    BuildOutcome Outcome, string Message, PrefixPackage? Package, TimeSpan Elapsed);

/// <summary>
/// Der Bauer (S3.5, Spec §3.6): führt das Rezept einer <see cref="PrefixComponent"/> in einem
/// Wegwerf-Prefix aus und erntet daraus ein <see cref="PrefixPackage"/>. Das Herzstück der Story —
/// er benutzt alles, was vor ihm entstanden ist (Ausführer, Rezept, Paketspeicher, Ernte).
///
/// <para>
/// <b>Rückgabewerte sagen nichts.</b> Der teuerste gemessene Satz des Vorgängerprojekts
/// (Vision §6.2): Der 4.8-Aufruf kam nach 36 Sekunden mit 0 zurück, während <c>Setup.exe</c> noch
/// minutenlang arbeitete; der 4.0-Installer endete nach neun Sekunden mit 0, ohne irgendetwas zu
/// installieren. Gewartet wird deshalb nach <b>jeder</b> Stufe auf <c>wineserver -w</c> — auch
/// nach der letzten —, und der <see cref="ProcessOutcome.ExitCode"/> geht ins Log und sonst
/// nirgendwohin. Das einzige Urteil des Ausführers ist <see cref="ProcessOutcome.Completed"/>:
/// „die Zeit ist abgelaufen" (Plan P-2).
/// </para>
///
/// <para>
/// <b>Wine Mono darf nie entstehen.</b> Wine legt in jeden frischen Prefix seinen eigenen
/// .NET-Ersatz, woraufhin der 4.0-Installer sich für überflüssig hält; nachträglich entfernen ließ
/// er sich nicht. Die Gegenmaßnahme ist <c>WINEDLLOVERRIDES=mscoree=d</c> in der ersten Stufe —
/// sie steht im <b>Rezept des Moduls</b>, nicht hier. Was diese Klasse dafür tut: Sie setzt die
/// Variable je Stufe frisch und <b>entfernt</b> sie, wo die Stufe keine nennt. Ein geerbter Wert
/// aus der Umgebung des Nutzers änderte sonst still das Rezept.
/// </para>
///
/// <para>
/// <b>Geurteilt wird nur einmal</b> (E-3): ob im Wegwerf-Prefix etwas Brauchbares steht, sagt
/// <see cref="PrefixMarkProbe"/> — dieselbe Prüfung, die in S3.6 über den Spiel-Prefix
/// entscheidet. Die einzige Ausnahme ist die Bewertung nach Stufe 1: ohne <c>system.reg</c> ist
/// gar kein Prefix entstanden, und dann lohnen die restlichen drei Minuten nicht.
/// </para>
///
/// <para>
/// <b>Billige Prüfungen vor teuren Schritten.</b> Das ganze Rezept dauert knapp drei Minuten. Wer
/// kein Wine hat, keinen Platz oder schon ein Paket, erfährt das in Millisekunden.
/// </para>
///
/// <para>
/// <b>Das <c>finally</c> ist E-1 des Vorgängerprojekts</b>: Ein Fehlschlag darf keine 1,6 GB
/// zurücklassen. Erst <c>wineserver -k</c> — ein überlebender Wineserver hielte Dateien offen —,
/// dann räumt <see cref="PrefixPackageBuild.Dispose"/> Wegwerf-Prefix und halbes Paket weg.
/// </para>
/// </summary>
public sealed class PrefixPackageBuilder
{
    // Zur Spitze liegen Wegwerf-Prefix (~1,6 GB) und Paket (~750 MB) gleichzeitig auf der Platte.
    private const long RequiredFreeBytes = 3L * 1024 * 1024 * 1024;

    // Der Wineserver ist keine Arbeit, sondern das Warten darauf — läuft er nach zwei Minuten
    // noch, ist etwas anderes kaputt als die Zeitgrenze.
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromMinutes(2);

    // Die beiden Installer laufen über eine Minute und melden von sich aus nichts (Spec §3.8).
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(2);

    private readonly PrefixPackageStore _store;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly TimeProvider _time;
    private readonly Func<string, long?> _freeBytes;
    private readonly ILogger? _logger;

    /// <param name="store">Wohin das Paket kommt und wo der Bau stattfindet.</param>
    /// <param name="runner">Die Naht zum Prozess — im Betrieb <see cref="ProcessRunner"/>.</param>
    /// <param name="environment">
    /// Die Ausgangsumgebung jedes Kindprozesses, im Betrieb <see cref="ProtonWine.Current"/>. Sie
    /// wird <b>gereicht</b> und nie selbst gelesen: Ein Bauer, der sich die Umgebung selbst holte,
    /// wäre nicht mehr prüfbar — und die Umgebung ist in dieser Story der Unterschied zwischen
    /// einem Export und einem hängenden Dialog (Plan P-1).
    /// </param>
    /// <param name="time">Eine Uhr für Herzschlag und <c>Elapsed</c> (Plan P-7).</param>
    /// <param name="freeBytes">
    /// Wie viel auf dem Datenträger dieses Pfades frei ist. <c>null</c> heißt „nicht
    /// feststellbar" und blockiert <b>nicht</b> (Plan P-8) — eine Prüfung, die bei Unwissen
    /// abbräche, wäre schlimmer als keine.
    /// </param>
    /// <param name="logger">Null heißt stumm.</param>
    public PrefixPackageBuilder(
        PrefixPackageStore store,
        IProcessRunner runner,
        IReadOnlyDictionary<string, string> environment,
        TimeProvider? time = null,
        Func<string, long?>? freeBytes = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(environment);

        _store = store;
        _runner = runner;
        _environment = environment;
        _time = time ?? TimeProvider.System;
        _freeBytes = freeBytes ?? DefaultFreeBytes;
        _logger = logger;
    }

    /// <summary>
    /// Baut das Paket zu <paramref name="request"/> — oder sagt in einem Satz, warum nicht. Der
    /// Ablauf steht im Klassenkommentar; hier ist er der Reihe nach zu lesen.
    /// </summary>
    public async Task<BuildResult> BuildAsync(
        BuildRequest request,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _time.GetTimestamp();
        var component = request.Component;
        var key = PrefixPackageKey.For(component.Key, component.Version, request.ProtonMajor);

        BuildResult Result(BuildOutcome outcome, string message, PrefixPackage? package = null)
            => new(outcome, message, package, _time.GetElapsedTime(startedAt));

        // 1. Liegt das Paket schon da? Das vorhandene wird mitgegeben: Der Aufrufer (S3.9) will
        //    danach genau dieses anwenden und müsste es sonst ein zweites Mal suchen.
        var existing = _store.Find(key);
        if (existing is not null)
        {
            _logger?.LogInformation("package already present, nothing to build: {Key}", key);
            return Result(BuildOutcome.AlreadyPresent, $"A package for {key} already exists.", existing);
        }

        // 2. Hat die Komponente ein Rezept? Ohne eines ist sie erkennbar, aber nicht baubar
        //    (S3.2 hat dieses Glied ausdrücklich offengelassen).
        var recipe = component.Recipe;
        if (recipe is null)
        {
            return Result(
                BuildOutcome.NoRecipe,
                $"{key} cannot be built — the module did not declare a recipe.");
        }

        // 3. Sind alle Zutaten da? Ein fehlender Slot und eine fehlende Datei sind für den Nutzer
        //    dasselbe Problem und bekommen deshalb denselben Satz.
        foreach (var ingredient in recipe.Ingredients)
        {
            var path = request.IngredientPaths.GetValueOrDefault(ingredient.Slot);
            if (path is null || !File.Exists(path))
            {
                _logger?.LogWarning("recipe ingredient missing: {Slot}", ingredient.Slot);
                return Result(
                    BuildOutcome.IngredientMissing,
                    $"{ingredient.DisplayName} is missing — put it into the dependencies folder.");
            }
        }

        // 4. Gibt es Protons Wine? Ein Rezept startet immer Protons wine, nie ein beliebiges
        //    Programm (E-2).
        var winePath = ProtonWine.Find(request.Tool);
        if (winePath is null)
        {
            return Result(
                BuildOutcome.WineMissing,
                $"{request.Tool.Name} has no Wine — a package cannot be built.");
        }

        // 5. Sind 3 GB frei?
        var freeBytes = _freeBytes(_store.PackagesPath);
        if (freeBytes is not null && freeBytes < RequiredFreeBytes)
        {
            return Result(
                BuildOutcome.InsufficientSpace,
                "Not enough space: building a package needs about 3 GB, "
                    + $"{freeBytes / (1024 * 1024)} MB are free.");
        }

        // Ohne wineserver wird weder gewartet noch aufgeräumt. Das ist schlechter, aber kein
        // Grund, den Bau zu verweigern — die eine Log-Zeile hier erklärt später beides.
        var serverPath = ProtonWine.FindServer(request.Tool);
        if (serverPath is null)
        {
            _logger?.LogWarning(
                "{Tool} has no wineserver: stage waits and cleanup are skipped", request.Tool.Name);
        }

        var stageCount = recipe.Stages.Count + 2;

        // 6. Ab hier räumt Dispose alles weg, was nicht mit Complete abgeschlossen wurde.
        PrefixPackageBuild started;
        try
        {
            started = _store.BeginBuild(key);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Der Bau beginnt mit ein paar Verzeichnissen — auf einer vollen oder
            // schreibgeschützten Platte scheitert schon das. Derselbe Fall wie jeder andere
            // Dateisystemfehler, also derselbe Ausgang: eine Meldung, kein Absturz durch die
            // Oberfläche. Die Platzprüfung oben deckt ihn nicht ab (sie fragt nach Platz, nicht
            // nach Schreibrecht).
            _logger?.LogWarning(exception, "package build could not start: {Key}", key);
            return Result(BuildOutcome.Failed, exception.Message);
        }

        using var build = started;
        using var heartbeat = new BuildHeartbeat(progress, stageCount, _time, startedAt, HeartbeatPeriod);
        try
        {
            // 7. Die Stufen, in der Reihenfolge des Rezepts.
            for (var index = 0; index < recipe.Stages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stage = recipe.Stages[index];

                // Gemeldet wird vor der Stufe: Der Nutzer soll sehen, woran gerade gearbeitet
                // wird, nicht was schon fertig ist.
                heartbeat.Report(index + 1, stage.Label);

                var outcome = await _runner.RunAsync(
                    new ProcessCommand(
                        winePath,
                        StageArguments(stage, request.IngredientPaths),
                        StageEnvironment(stage, build.PrefixPath)),
                    stage.Timeout,
                    cancellationToken).ConfigureAwait(false);

                // Der ExitCode landet hier und nirgendwo sonst (Plan P-2).
                _logger?.LogInformation(
                    "build stage finished: {Label}, completed={Completed}, exitCode={ExitCode}",
                    stage.Label, outcome.Completed, outcome.ExitCode);

                // Vor dem Warten, nicht danach: Eine Stufe, deren Zeit abgelaufen ist, wurde
                // abgeschossen — auf ihren Server zu warten brächte nichts, und das finally
                // schickt gleich ohnehin das schärfere "wineserver -k" hinterher.
                if (!outcome.Completed)
                {
                    return Result(
                        BuildOutcome.StageFailed,
                        $"The step \"{stage.Label}\" did not finish in time.");
                }

                await WaitForServerAsync(serverPath, build.PrefixPath, cancellationToken)
                    .ConfigureAwait(false);

                // Die einzige Bewertung des Bauers, und sie steht hier statt drei Minuten
                // später: Ohne system.reg hat Wine gar keinen Prefix angelegt.
                if (index == 0 && !File.Exists(Path.Combine(build.PrefixPath, "system.reg")))
                {
                    return Result(
                        BuildOutcome.StageFailed,
                        "The throwaway prefix was not created — Wine did not write anything.");
                }
            }

            // 8. Der Befund — hier und nur hier wird über den Inhalt geurteilt (E-3).
            cancellationToken.ThrowIfCancellationRequested();
            heartbeat.Report(recipe.Stages.Count + 1, "checking the prefix");

            var warnings = new List<string>();
            var finding = PrefixMarkProbe.Detect(build.PrefixPath, component, warnings);
            foreach (var warning in warnings)
            {
                _logger?.LogWarning("prefix probe warning: {Warning}", warning);
            }

            if (!finding.Present)
            {
                _logger?.LogWarning(
                    "throwaway prefix does not carry the component: {Missing}",
                    string.Join(", ", finding.Missing));
                return Result(
                    BuildOutcome.NotPrepared,
                    $"The package was not built: {string.Join(", ", finding.Missing)}.");
            }

            // 9. Die Ernte. Task.Run, weil Copy 700 MB synchron kopiert — auf dem Aufruferstrang
            //    stünde währenddessen die Oberfläche (S3.9 hängt eine Leiste daran).
            cancellationToken.ThrowIfCancellationRequested();
            heartbeat.Report(stageCount, "harvesting the files");

            var harvest = await Task.Run(
                () => PrefixHarvest.Copy(
                    build.PrefixPath,
                    component.Payload.Files,
                    recipe.HarvestPatterns,
                    build.FilesPath,
                    cancellationToken,
                    _logger),
                cancellationToken).ConfigureAwait(false);

            // Der Export bekommt die bildschirmlose Umgebung: Findet regedit einen Bildschirm,
            // hängt es an einem unsichtbaren modalen Dialog (Spec §3.2).
            var export = new WineRegistryExport(_runner, _logger);
            var registry = await export.ExportAsync(
                winePath,
                ProtonWine.RegistryEnvironment(_environment, build.PrefixPath),
                component.Payload.RegistryBranches,
                Path.Combine(build.PackagePath, PrefixPackage.RegistryFileName),
                cancellationToken).ConfigureAwait(false);

            if (!registry.Ok)
            {
                return Result(
                    BuildOutcome.Failed,
                    "The registry export produced nothing — no package was built.");
            }

            // 10. Das Manifest, dann der unteilbare Umzug auf den Schlüssel.
            var manifest = new PrefixPackageManifest(
                Key: key,
                ComponentKey: component.Key,
                ComponentVersion: component.Version,
                ProtonMajor: request.ProtonMajor,
                BuiltAtUtc: _time.GetUtcNow().UtcDateTime,
                ToolName: request.Tool.Name,
                FileCount: harvest.FileCount,
                TotalBytes: harvest.TotalBytes,
                RegistryBranches: registry.ExportedBranches);

            build.Complete(manifest);

            var package = new PrefixPackage(key, Path.Combine(_store.PackagesPath, key), manifest);
            return Result(BuildOutcome.Built, $"Package {key} is ready.", package);
        }
        catch (OperationCanceledException)
        {
            // Zuerst, denn sie ist eine Ausnahme und würde sonst vom Fang darunter geschluckt.
            // Der Nutzer hat abgebrochen — kein Fehler.
            _logger?.LogInformation("package build canceled: {Key}", key);
            return Result(BuildOutcome.Canceled, "The build was canceled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nur diese beiden: Sie kommen vom Dateisystem und können jeden treffen, der eine
            // volle oder schreibgeschützte Platte hat. Alles andere wäre ein Programmierfehler
            // und soll auffallen, statt als Meldung im Fenster zu enden.
            _logger?.LogWarning(exception, "package build failed: {Key}", key);
            return Result(BuildOutcome.Failed, exception.Message);
        }
        finally
        {
            await KillServerAsync(serverPath, build.PrefixPath).ConfigureAwait(false);
        }
    }

    // Zutatenpfad voran (falls die Stufe eine nennt), dann die Argumente des Rezepts. Dass der
    // Slot bekannt ist, hat PrefixRecipe beim Bauen geprüft; dass die Datei existiert, Schritt 3.
    private static IReadOnlyList<string> StageArguments(
        RecipeStage stage, IReadOnlyDictionary<string, string> ingredientPaths)
    {
        if (stage.IngredientSlot is null)
        {
            return stage.Arguments;
        }

        return [ingredientPaths[stage.IngredientSlot], .. stage.Arguments];
    }

    // Die Umgebung der Stufe: Protons Wine-Umgebung mit Bildschirm (Installer zeigen Fenster),
    // dazu WINEDLLOVERRIDES der Stufe — oder eben keines. Siehe Klassenkommentar.
    private IReadOnlyDictionary<string, string> StageEnvironment(RecipeStage stage, string prefixPath)
    {
        var environment = new Dictionary<string, string>(
            ProtonWine.Environment(_environment, prefixPath), StringComparer.Ordinal);

        if (stage.DllOverrides is null)
        {
            environment.Remove("WINEDLLOVERRIDES");
        }
        else
        {
            environment["WINEDLLOVERRIDES"] = stage.DllOverrides;
        }

        return environment;
    }

    // Warten ist der Zweck, nicht das Ergebnis: Was dieser Aufruf zurückgibt, wird geloggt und
    // sonst nirgends gelesen (Klassenkommentar).
    private async Task WaitForServerAsync(
        string? serverPath, string prefixPath, CancellationToken cancellationToken)
    {
        if (serverPath is null)
        {
            return;
        }

        var outcome = await _runner.RunAsync(
            new ProcessCommand(serverPath, ["-w"], ProtonWine.Environment(_environment, prefixPath)),
            ServerTimeout,
            cancellationToken).ConfigureAwait(false);

        _logger?.LogDebug(
            "wineserver -w finished: completed={Completed}, exitCode={ExitCode}",
            outcome.Completed, outcome.ExitCode);
    }

    // Bester Aufwand im finally: Ein überlebender Wineserver hielte Dateien offen, und dann
    // scheiterte das Löschen des Wegwerf-Prefix. Fehler werden geschluckt — eine Ausnahme von
    // hier überschriebe die eigentliche Meldung, und genau die soll beim Nutzer ankommen.
    // CancellationToken.None ist Absicht: Beim Abbruch ist der gereichte Token längst gesetzt,
    // und ein Aufräumen, das sich selbst abbricht, räumt nicht auf.
    private async Task KillServerAsync(string? serverPath, string prefixPath)
    {
        if (serverPath is null)
        {
            return;
        }

        try
        {
            await _runner.RunAsync(
                new ProcessCommand(serverPath, ["-k"], ProtonWine.Environment(_environment, prefixPath)),
                ServerTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "wineserver -k failed, cleaning up anyway");
        }
    }

    // Den Paketordner gibt es vor dem ersten Bau noch nicht — gefragt wird deshalb das erste
    // vorhandene Elternverzeichnis. Es liegt auf demselben Datenträger, und genau der ist die
    // Frage. Jeder Fehlschlag ergibt null: "weiß nicht" blockiert nicht (Plan P-8).
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

    /// <summary>
    /// Der Takt der Fortschrittsmeldungen (Spec §3.8): <see cref="Report"/> meldet eine neue
    /// Stufe sofort, und alle zwei Sekunden meldet der Zeitgeber <b>dieselbe</b> Stufe erneut,
    /// mit fortlaufender <see cref="BuildProgress.Elapsed"/>.
    ///
    /// <para>
    /// <b>Warum überhaupt ein Takt:</b> Die beiden Installer laufen über eine Minute und melden
    /// von sich aus nichts. Ohne Herzschlag stünde die Zeile eine Minute unverändert da — und
    /// genau so sieht ein abgestürzter Lauf aus. <b>Erfunden wird nichts</b>: neu ist allein die
    /// Uhr.
    /// </para>
    ///
    /// <para>
    /// Uhr und Zeitgeber kommen aus demselben <see cref="TimeProvider"/> (Plan P-7), sonst tickte
    /// im Test die echte Zeit neben der gestellten. <see cref="Dispose"/> beendet den Takt — im
    /// <c>using</c> des Bauers also auch dann, wenn der Lauf wirft.
    /// </para>
    /// </summary>
    private sealed class BuildHeartbeat : IDisposable
    {
        private readonly IProgress<BuildProgress>? _progress;
        private readonly int _stageCount;
        private readonly TimeProvider _time;
        private readonly long _startedAt;
        private readonly ITimer? _timer;
        private readonly object _gate = new();

        private int _stage;
        private string _label = string.Empty;

        internal BuildHeartbeat(
            IProgress<BuildProgress>? progress,
            int stageCount,
            TimeProvider time,
            long startedAt,
            TimeSpan period)
        {
            _progress = progress;
            _stageCount = stageCount;
            _time = time;
            _startedAt = startedAt;

            // Ohne Zuhörer kein Zeitgeber.
            _timer = progress is null ? null : time.CreateTimer(_ => Beat(), null, period, period);
        }

        /// <summary>Meldet eine neue Stufe — sofort, und ab jetzt ist sie die des Herzschlags.</summary>
        internal void Report(int stage, string label)
        {
            lock (_gate)
            {
                _stage = stage;
                _label = label;
            }

            Beat();
        }

        public void Dispose() => _timer?.Dispose();

        // Der Zeitgeber läuft auf einem eigenen Strang, der Bauer meldet auf seinem — die zwei
        // Felder werden deshalb unter dem Schloss gelesen und geschrieben.
        private void Beat()
        {
            int stage;
            string label;
            lock (_gate)
            {
                stage = _stage;
                label = _label;
            }

            if (stage == 0)
            {
                return;
            }

            _progress?.Report(
                new BuildProgress(stage, _stageCount, label, _time.GetElapsedTime(_startedAt)));
        }
    }
}
