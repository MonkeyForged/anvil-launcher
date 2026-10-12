using System.Globalization;
using Anvil.Core.Backups;
using Anvil.Core.Modules;
using Anvil.Core.Vdf;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Steam;

/// <summary>
/// Der Schreibvorgang als Ganzes (Spec S4.4 §3.3). Seit S3.7 ist er <b>eine Folge von Schritten</b>
/// (<see cref="SaveChain"/>): Riegel → Schritte ohne Steam → Vermerk und Steam beenden → Schritte
/// mit geschlossenem Steam (darunter das Schreiben der Steam-Wuensche) → Steam starten. Schritte
/// ohne Steam laufen <b>vor</b> dem Beenden, ganz gleich, wo sie gereicht werden (Spec S3.7 §3.2).
///
/// <para>
/// <b>Die zwei Zusagen, um die es geht:</b> Ab dem ersten geschriebenen Byte gibt es keinen Weg
/// mehr, der Steam beendet zuruecklaesst. Und kein Byte wird geschrieben, bevor sein
/// Sicherungspfad auf der Platte steht (Projektplan M4, Korrektur a).
/// </para>
///
/// <para>
/// <b>Vier Naehte sind Delegaten, zwei Ablagen sind Objekte.</b> Beenden, Starten, Schreiben und
/// „laeuft ein Spiel" kommen als Delegat — sonst haenge jeder Test an der Innenmechanik von
/// <see cref="SteamProcess"/> (zwei leere Messungen im 500-ms-Takt). Journal und Sicherung sind
/// echte Objekte: Sie kennen nur Dateien, und der Beweis fuer Korrektur (a) verlangt, dass ein
/// Test den Vermerk auf der Platte liest, waehrend geschrieben wird.
/// </para>
///
/// <para>
/// <b>Kein <c>CancellationToken</c></b> (E-C): Es gibt keinen Abbrecher — die Leiste sperrt
/// ihre Knoepfe, und ab dem ersten Byte darf ohnehin nichts mehr abbrechen. Wer die App
/// waehrenddessen beendet, unterbricht wie ein Stromausfall; dafuer ist der Vermerk da.
/// </para>
/// </summary>
public sealed class SteamTransaction
{
    private readonly TransactionJournal _journal;
    private readonly BackupStore _backups;
    private readonly Func<Task<SteamStopOutcome>> _stopSteam;
    private readonly Func<Task<SteamStartOutcome>> _startSteam;
    private readonly Func<IReadOnlyList<SteamGameChange>, Func<string, string>, SteamWriteResult> _applyChanges;
    private readonly Func<bool> _isGameRunning;
    private readonly Func<SteamSnapshot> _rescan;
    private readonly ILogger? _logger;

    /// <summary>0 heisst frei, 1 heisst „ein Vorgang laeuft" (E-B).</summary>
    private int _running;

    /// <param name="journal">Der Vermerk auf der Platte.</param>
    /// <param name="backups">Die Dateisicherung (S4.1).</param>
    /// <param name="stopSteam">Steam beenden — die Root reicht <c>steam.StopAsync</c>.</param>
    /// <param name="startSteam">Steam starten — <c>steam.StartAsync</c>.</param>
    /// <param name="applyChanges">Der Schreiber — <c>writer.Apply</c>.</param>
    /// <param name="isGameRunning">
    /// Laeuft <b>irgendein</b> Spiel (Entscheidungen 4 und 5)? Die Root reicht den Waechter:
    /// <c>() =&gt; watcher.Current.Count &gt; 0</c>. Eine zweite Sonde gaebe eine zweite Wahrheit.
    /// </param>
    /// <param name="rescan">Frisch von der Platte lesen — <c>library.Rescan</c>.</param>
    /// <param name="logger">Kategorie <c>Steam</c>; null heisst stumm.</param>
    public SteamTransaction(
        TransactionJournal journal,
        BackupStore backups,
        Func<Task<SteamStopOutcome>> stopSteam,
        Func<Task<SteamStartOutcome>> startSteam,
        Func<IReadOnlyList<SteamGameChange>, Func<string, string>, SteamWriteResult> applyChanges,
        Func<bool> isGameRunning,
        Func<SteamSnapshot> rescan,
        ILogger? logger = null)
    {
        _journal = journal;
        _backups = backups;
        _stopSteam = stopSteam;
        _startSteam = startSteam;
        _applyChanges = applyChanges;
        _isGameRunning = isGameRunning;
        _rescan = rescan;
        _logger = logger;
    }

    /// <summary>
    /// Der unterbrochene Vorgang, oder <c>null</c>. Nur ein Vermerk <b>mit</b> Sicherungen ist
    /// einer: Ohne sie kam Anvil nicht bis zum ersten Byte, es gibt nichts zurueckzuspielen.
    /// </summary>
    public TransactionRecord? PendingRecovery()
        => _journal.Read() is { Files.Count: > 0 } record ? record : null;

    /// <summary>
    /// Wendet die Wuensche an und laeuft die beigesteuerten Schritte ab. <paramref name="progress"/>
    /// wird aus dem Hintergrund-Faden gerufen — <b>die App postet selbst auf den UI-Faden</b> (S4.5).
    /// </summary>
    /// <param name="origins">
    /// Wer welches Feld gemeldet hat (S4.8 §3.3). Gebraucht erst <b>nach</b> dem Rescan: Der
    /// Vermerk geht von den Feldern aus, die uebrig geblieben sind, und schlaegt fuer sie nach.
    /// </param>
    /// <param name="progress">Je Zustandswechsel die ganze Folge als Momentaufnahme (Spec S3.7 §3.3).</param>
    /// <param name="steps">
    /// Beigesteuerte Schritte (Spec S3.7 §3.2). Heute reicht niemand welche (E-1); die Naht steht
    /// fuer die Module, die sie brauchen.
    /// </param>
    public async Task<TransactionResult> SaveAsync(
        IReadOnlyList<SteamGameChange> changes,
        IReadOnlyList<ChangeOrigin> origins,
        Action<SaveProgress>? progress = null,
        IReadOnlyList<SaveStep>? steps = null)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Blocked("A save is already running", "wait for it to finish");
        }

        try
        {
            return await RunSaveAsync(changes, origins, steps ?? [], progress).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<TransactionResult> RunSaveAsync(
        IReadOnlyList<SteamGameChange> changes,
        IReadOnlyList<ChangeOrigin> origins,
        IReadOnlyList<SaveStep> contributed,
        Action<SaveProgress>? progress)
    {
        // Das Schreiben der Steam-Wuensche ist selbst ein Schritt (Spec S3.7 §3.2) — ohne Wuensche
        // gibt es ihn nicht. Sein Ergebnis faengt die Lambda, weil der Schritt nur Erfolg meldet.
        SteamWriteResult? write = null;
        var writeStep = changes.Count > 0
            ? new SaveStep(
                SaveChain.Writing,
                NeedsSteamClosed: true,
                _ => Task.FromResult(Write(changes, origins, result => write = result)))
            : null;

        var chain = new SaveChain(writeStep is null ? contributed : [writeStep, .. contributed]);

        // E-5: Der Riegel begruendet sich mit Steam — ohne Steam-Schritt gibt es ihn nicht.
        if (chain.HasSteamPart && _isGameRunning())
        {
            return Blocked("Steam cannot be closed while a game is running", "close the game first");
        }

        if (PendingRecovery() is not null)
        {
            // Korrektur (b): Ein neuer Anlauf ueberschreibt nie einen Vermerk, der noch
            // Sicherungen fuehrt — sonst waere der Rueckweg des alten Vorgangs verloren.
            return Blocked(
                "An earlier save was interrupted", "restore the backup or dismiss it first");
        }

        if (chain.Links.Count == 0)
        {
            return NothingToDo();
        }

        _logger?.LogInformation(
            "save: {Count} change(s), {Steps} contributed step(s)", changes.Count, contributed.Count);

        foreach (var step in chain.BeforeSteam)
        {
            if (!(await RunStepAsync(chain, step, isOurs: false, progress).ConfigureAwait(false)).Succeeded)
            {
                chain.Fail(steamWillRestart: false);
                Report(chain, progress);
                return Failed(
                    $"{SaveChain.Bare(step.Label)} failed",
                    "nothing was changed — no Steam file was touched", chain);
            }
        }

        if (!chain.HasSteamPart)
        {
            _logger?.LogInformation("save complete — no step needed steam closed");
            return new TransactionResult(TransactionOutcome.Succeeded, "Saved", null) { Chain = chain.Links };
        }

        chain.Advance();
        Report(chain, progress);

        try
        {
            _journal.Begin();
        }
        catch (Exception exception) when (IsFileTrouble(exception))
        {
            _logger?.LogWarning(exception, "could not open the journal: {Path}", _journal.FilePath);
            chain.Fail(steamWillRestart: false);
            Report(chain, progress);
            return Failed("Closing Steam failed", "nothing was changed", chain);
        }

        var stop = await _stopSteam().ConfigureAwait(false);
        if (stop is SteamStopOutcome.TimedOut or SteamStopOutcome.CommandMissing)
        {
            _logger?.LogWarning("steam could not be closed — transaction aborted, backup intact");
            _journal.Clear();
            chain.Fail(steamWillRestart: false);
            Report(chain, progress);
            return Failed("Closing Steam failed", "nothing was changed", chain);
        }

        var weStoppedSteam = stop == SteamStopOutcome.Stopped;
        if (weStoppedSteam)
        {
            _logger?.LogInformation("steam closed — starting transaction");
            chain.Complete();
        }
        else
        {
            _logger?.LogInformation("steam was not running — writing without a restart");
            chain.DropSteamBracket();
        }

        Report(chain, progress);

        foreach (var step in chain.WithSteamClosed)
        {
            var outcome = await RunStepAsync(chain, step, ReferenceEquals(step, writeStep), progress)
                .ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                chain.Fail(steamWillRestart: weStoppedSteam);
                Report(chain, progress);

                // Steam kommt in jedem Fall zurueck (S4.4) — auch nach einem gescheiterten Schritt (E-6).
                await BringSteamBackAsync(weStoppedSteam, chain, progress).ConfigureAwait(false);

                var title = $"{SaveChain.Bare(step.Label)} failed"
                    + (outcome.Subject is { } subject ? " · " + subject : string.Empty);
                return new TransactionResult(TransactionOutcome.Failed, title, outcome.Detail, write)
                {
                    Chain = chain.Links,
                };
            }
        }

        // Idempotent: Hat das Schreiben den Vermerk schon geschlossen, ist das hier ein Nichts.
        ClearJournalQuietly();

        // Erst hier: `write` setzt der Schreib-Schritt waehrend der Schleife.
        var wroteSomething = write is not null || contributed.Count > 0;
        var start = await BringSteamBackAsync(weStoppedSteam, chain, progress).ConfigureAwait(false);
        if (!Came(start))
        {
            if (!wroteSomething)
            {
                return Failed("Starting Steam failed", "nothing was changed — start it yourself", chain);
            }

            _logger?.LogWarning("files written, but steam did not start");
            return new TransactionResult(
                TransactionOutcome.WrittenButSteamNotStarted,
                "Written, but Steam could not be started", "start it yourself", write)
            {
                Chain = chain.Links,
            };
        }

        if (!wroteSomething)
        {
            return NothingToDo();
        }

        _logger?.LogInformation(start is null
            ? "transaction complete — steam was not restarted"
            : "steam restarted — transaction complete");
        return new TransactionResult(
            TransactionOutcome.Succeeded, "Saved",
            start is null ? "Steam was not running" : "Steam restarted", write)
        {
            Chain = chain.Links,
        };
    }

    /// <summary>
    /// Ein Glied von Anfang bis Ende. <b>Nur beigesteuerte Schritte werden gefangen</b>: Hinter ihnen
    /// steckt fremder Code, und eine Ausnahme soll ein Fehlschlag sein, kein Absturz (wie beim Befund,
    /// S3.4). Das eigene Schreiben wirft weiter — ein Absturz dort ist genau der Fall, fuer den der
    /// Vermerk da ist.
    /// </summary>
    private async Task<SaveStepOutcome> RunStepAsync(
        SaveChain chain, SaveStep step, bool isOurs, Action<SaveProgress>? progress)
    {
        chain.Advance();
        Report(chain, progress);

        SaveStepOutcome outcome;
        try
        {
            outcome = await step.RunAsync(stage => progress?.Invoke(new SaveProgress(chain.Links, stage)))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (!isOurs)
        {
            _logger?.LogWarning(exception, "save step threw: {Step}", step.Label);
            outcome = SaveStepOutcome.Failed();
        }

        if (outcome.Succeeded)
        {
            chain.Complete();
            Report(chain, progress);
        }
        else
        {
            _logger?.LogWarning(
                "save step failed: {Step} {Subject} {Detail}", step.Label, outcome.Subject, outcome.Detail);
        }

        return outcome;
    }

    /// <summary>Meldet die Folge, wie sie gerade steht (Spec S3.7 §3.3).</summary>
    private static void Report(SaveChain chain, Action<SaveProgress>? progress)
        => progress?.Invoke(new SaveProgress(chain.Links));

    private static TransactionResult Failed(string title, string detail, SaveChain chain)
        => new(TransactionOutcome.Failed, title, detail) { Chain = chain.Links };

    /// <summary>
    /// Das Schreiben als Schritt (Spec S3.7 §3.2): Ist lesen → aussortieren → sichern → schreiben →
    /// bei Fehler zurueckspielen. Korrekturen (a) bis (c) aus S4.4 gelten unveraendert.
    ///
    /// <para>
    /// <b>Beim Zurueckspielen ist die Quelle der Vermerk, nicht das Schreib-Ergebnis</b> — eine
    /// Datei kann <c>Failed</c> sein <i>und</i> eine Sicherung haben (die Pruefung nach dem
    /// Schreiben schlug fehl, das Byte ist womoeglich draussen). Genau die muss zurueck.
    /// </para>
    /// </summary>
    /// <param name="wrote">Bekommt das Ergebnis des Schreibers, sobald er lief.</param>
    private SaveStepOutcome Write(
        IReadOnlyList<SteamGameChange> changes,
        IReadOnlyList<ChangeOrigin> origins,
        Action<SteamWriteResult> wrote)
    {
        // Korrektur (c): Erst jetzt lesen. Die Platte kann gegenueber Steams Speicher
        // veraltet gewesen sein, solange Steam lief.
        SteamSnapshot snapshot;
        try
        {
            snapshot = _rescan();
        }
        catch (Exception exception) when (IsFileTrouble(exception) || exception is VdfParseException)
        {
            _logger?.LogWarning(exception, "could not read steam's files after closing");
            _journal.Clear();
            return SaveStepOutcome.Failed(detail: "nothing was changed");
        }

        var todo = SteamChangeFilter.Reduce(changes, snapshot);
        if (todo.Count == 0)
        {
            _logger?.LogInformation("nothing left to write after rescan");
            _journal.Clear();
            return SaveStepOutcome.Done;
        }

        // Der Vermerk entsteht je Datei, aus dem, was in *sie* geschrieben wird (S4.8 E-J).
        var write = _applyChanges(
            todo,
            path => BackupAndRecord(
                path,
                BackupNoteBuilder.Build(Path.GetFileName(path), todo, origins, snapshot)));
        wrote(write);

        if (write.Succeeded)
        {
            // Sobald die letzte Datei steht, ist der Vorgang zu Ende — was jetzt noch schiefgeht,
            // ist kein halber Zustand mehr.
            ClearJournalQuietly();
            return SaveStepOutcome.Done;
        }

        var rollbackError = RestoreFromJournal();
        if (rollbackError is null)
        {
            ClearJournalQuietly();
        }

        var failed = write.Config.Outcome == SteamFileOutcome.Failed ? write.Config : write.LocalConfig;
        _logger?.LogWarning("writing failed: {File}: {Error}", failed.Path, failed.Error);

        // E-7: Der Klartext steht im Log; in der Leiste nur, wenn das Zurueckspielen scheiterte.
        return SaveStepOutcome.Failed(
            failed.FileName,
            rollbackError is null ? "restored from backup" : $"backup could not be restored — {rollbackError}");
    }

    /// <summary>
    /// Der Sicherungs-Delegat, den der Schreiber je Datei ruft — und die ganze Korrektur (a):
    /// Wenn er zurueckkehrt, steht der Pfad auf der Platte. Seit S4.8 legt er den Vermerk gleich
    /// mit daneben.
    /// </summary>
    private string BackupAndRecord(string path, BackupNote? note)
    {
        var backup = _backups.Create(path, note);
        _journal.Add(path, backup);
        return backup;
    }

    /// <summary>
    /// Der Vermerk eines Restores (S4.8 §3.5): Modul <c>launcher</c>, ein Satz, <b>keine
    /// Aufstellung</b> — was nach dieser Sicherung geschrieben wird, ist das Zurueckspielen
    /// selbst. Die leere Liste ist das Zeichen fuer die Oberflaeche, keine Affordanz zu zeigen.
    /// </summary>
    private static BackupNote RestoreNote(string fileName, DateTime takenAt)
        => new(
            LauncherModule.KeyValue,
            $"Restored {fileName} from "
                + takenAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            []);

    /// <summary>
    /// Bringt Steam zurueck, wenn <b>wir</b> es beendet haben (Entscheidung 6) — als Glied
    /// <c>Starting Steam…</c> der Folge. <c>null</c> heisst „nicht versucht" — Steam lief nicht,
    /// und wer es bewusst geschlossen hatte, soll nicht ueberrascht werden.
    /// </summary>
    private async Task<SteamStartOutcome?> BringSteamBackAsync(
        bool weStoppedSteam, SaveChain chain, Action<SaveProgress>? progress)
    {
        if (!weStoppedSteam)
        {
            return null;
        }

        chain.Advance();
        Report(chain, progress);
        var start = await _startSteam().ConfigureAwait(false);
        if (Came(start))
        {
            chain.Complete();
        }
        else
        {
            chain.Fail(steamWillRestart: false);
        }

        Report(chain, progress);
        return start;
    }

    /// <summary>Steam ist da — oder es wurde gar nicht erst versucht.</summary>
    private static bool Came(SteamStartOutcome? start)
        => start is null or SteamStartOutcome.Started or SteamStartOutcome.AlreadyRunning;

    private static bool IsFileTrouble(Exception exception)
        => exception is IOException or UnauthorizedAccessException;

    private TransactionResult Blocked(string title, string detail)
    {
        _logger?.LogInformation("save blocked: {Reason}", title);
        return new TransactionResult(TransactionOutcome.Blocked, title, detail);
    }

    private static TransactionResult NothingToDo()
        => new(TransactionOutcome.NothingToDo, "Nothing to save", "Steam already has these values");

    /// <summary>
    /// Der Vermerk ist erledigt; scheitert das Loeschen, meldet der naechste Start eine
    /// Unterbrechung, die keine ist — laestig, aber ungefaehrlich (Spec §7). Ihn im Zweifel zu
    /// behalten waere schlimmer, als ihn zu verlieren: Die Dateien sind gueltig.
    /// </summary>
    private void ClearJournalQuietly()
    {
        try
        {
            _journal.Clear();
        }
        catch (Exception exception) when (IsFileTrouble(exception))
        {
            _logger?.LogWarning(exception, "could not clear the journal: {Path}", _journal.FilePath);
        }
    }

    /// <summary>
    /// Spielt einen unterbrochenen Vorgang zurueck: Steam beenden, Dateien aus dem Vermerk
    /// zuruecklegen, Steam starten. <b>Kein <c>Resume</c></b> (Entscheidung 7) — wer
    /// weitermachen will, stellt die Aenderung erneut ein und speichert. Meldet dieselbe feste
    /// Folge wie ein Save ohne beigesteuerte Schritte (Spec S3.7 E-8).
    /// </summary>
    public async Task<TransactionResult> RollBackAsync(Action<SaveProgress>? progress = null)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Blocked("A save is already running", "wait for it to finish");
        }

        try
        {
            return await RunRollBackAsync(progress).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<TransactionResult> RunRollBackAsync(Action<SaveProgress>? progress)
    {
        if (_isGameRunning())
        {
            return Blocked("Steam cannot be closed while a game is running", "close the game first");
        }

        if (PendingRecovery() is null)
        {
            // Ein Vermerk ohne Sicherungen ist kein Vorgang — er darf trotzdem weg.
            ClearJournalQuietly();
            return new TransactionResult(
                TransactionOutcome.NothingToDo, "Nothing to restore", "there is no interrupted save");
        }

        var chain = SaveChain.ForSteamWrite();
        chain.Advance();
        Report(chain, progress);

        var stop = await _stopSteam().ConfigureAwait(false);
        if (stop is SteamStopOutcome.TimedOut or SteamStopOutcome.CommandMissing)
        {
            _logger?.LogWarning("steam could not be closed — rollback aborted, backup intact");
            chain.Fail(steamWillRestart: false);
            Report(chain, progress);
            return Failed("Closing Steam failed", "nothing was changed", chain);
        }

        var weStoppedSteam = CloseBracket(stop, chain, progress);

        chain.Advance();
        Report(chain, progress);
        var error = RestoreFromJournal();
        if (error is null)
        {
            ClearJournalQuietly();
            chain.Complete();
        }
        else
        {
            chain.Fail(steamWillRestart: weStoppedSteam);
        }

        Report(chain, progress);

        var start = await BringSteamBackAsync(weStoppedSteam, chain, progress).ConfigureAwait(false);

        if (error is not null)
        {
            return Failed("Backup could not be restored", error, chain);
        }

        if (!Came(start))
        {
            return Failed("Backup restored, but Steam could not be started", "start it yourself", chain);
        }

        _logger?.LogInformation("rollback complete");
        return new TransactionResult(
            TransactionOutcome.Succeeded, "Backup restored",
            start is null ? "Steam was not running" : "Steam restarted")
        {
            Chain = chain.Links,
        };
    }

    /// <summary>
    /// Steam ist beendet oder lief nicht: <c>Closing Steam…</c> ist erledigt — oder die Klammer
    /// verschwindet aus der Folge (E-4). Liefert, ob <b>wir</b> Steam beendet haben.
    /// </summary>
    private static bool CloseBracket(SteamStopOutcome stop, SaveChain chain, Action<SaveProgress>? progress)
    {
        var weStoppedSteam = stop == SteamStopOutcome.Stopped;
        if (weStoppedSteam)
        {
            chain.Complete();
        }
        else
        {
            chain.DropSteamBracket();
        }

        Report(chain, progress);
        return weStoppedSteam;
    }

    /// <summary>
    /// Spielt <b>eine beliebige</b> Sicherung an ihren Platz zurueck (Spec S4.7 §3.1) — die
    /// Naht, die S4.4 offengelassen hat: <see cref="RollBackAsync"/> fasst nur an, was im
    /// Vermerk steht.
    ///
    /// <para>
    /// <b>Der Stand von jetzt wird zuerst selbst zur Sicherung</b> (Owner-Vorgabe A1, Spec E-B):
    /// Damit ist kein einzelner Restore endgueltig, und ein Absturz mittendrin trifft auf
    /// denselben Wiederherstellungsweg wie ein abgestuerzter Save.
    /// </para>
    /// </summary>
    /// <param name="backupPath">Die Sicherung, die zurueck soll.</param>
    /// <param name="targetPath">Wohin sie gehoert — <see cref="SteamConfigPaths.TargetFor"/>.</param>
    /// <param name="takenAt">
    /// Wann die Sicherung entstand (S4.8 §3.5) — der Vermerk des Restores nennt <b>diesen</b>
    /// Zeitpunkt, nicht den von jetzt: Die Frage lautet „welcher Stand kam hier herein?".
    /// </param>
    public async Task<TransactionResult> RestoreAsync(
        string backupPath, string targetPath, DateTime takenAt,
        Action<SaveProgress>? progress = null)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Blocked("A save is already running", "wait for it to finish");
        }

        try
        {
            return await RunRestoreAsync(backupPath, targetPath, takenAt, progress)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<TransactionResult> RunRestoreAsync(
        string backupPath, string targetPath, DateTime takenAt,
        Action<SaveProgress>? progress)
    {
        if (_isGameRunning())
        {
            return Blocked("Steam cannot be closed while a game is running", "close the game first");
        }

        if (PendingRecovery() is not null)
        {
            return Blocked(
                "An earlier save was interrupted", "restore the backup or dismiss it first");
        }

        // Vor dem Beenden geprueft: Es gibt keinen Grund, Steam fuer eine Datei zu schliessen,
        // die nicht mehr da ist.
        if (!File.Exists(backupPath))
        {
            _logger?.LogWarning("backup is gone: {Path}", backupPath);
            return new TransactionResult(TransactionOutcome.Failed, "Backup is gone", backupPath);
        }

        _logger?.LogInformation("restore: {Backup} -> {Target}", backupPath, targetPath);

        // Der Vermerk gehoert zum Glied `Closing Steam…`, wie beim Save (Spec S3.7 §3.2).
        var chain = SaveChain.ForSteamWrite();
        chain.Advance();
        Report(chain, progress);

        try
        {
            _journal.Begin();
        }
        catch (Exception exception) when (IsFileTrouble(exception))
        {
            _logger?.LogWarning(exception, "could not open the journal: {Path}", _journal.FilePath);
            chain.Fail(steamWillRestart: false);
            Report(chain, progress);
            return Failed("Closing Steam failed", "nothing was changed", chain);
        }

        var stop = await _stopSteam().ConfigureAwait(false);
        if (stop is SteamStopOutcome.TimedOut or SteamStopOutcome.CommandMissing)
        {
            _logger?.LogWarning("steam could not be closed — restore aborted");
            ClearJournalQuietly();
            chain.Fail(steamWillRestart: false);
            Report(chain, progress);
            return Failed("Closing Steam failed", "nothing was changed", chain);
        }

        var weStoppedSteam = CloseBracket(stop, chain, progress);

        chain.Advance();
        Report(chain, progress);
        string? error = null;
        try
        {
            // Fehlt das Ziel, gibt es nichts zu sichern — das Zurueckspielen legt es an.
            if (File.Exists(targetPath))
            {
                BackupAndRecord(targetPath, RestoreNote(Path.GetFileName(targetPath), takenAt));
            }

            _backups.Restore(backupPath, targetPath);
        }
        catch (Exception exception) when (IsFileTrouble(exception))
        {
            error = exception.Message;
            _logger?.LogWarning(exception, "restore failed: {Error}", exception.Message);
        }

        // Geglueckt: der Vermerk ist erledigt. Gescheitert: er bleibt nur dann liegen, wenn er
        // etwas zu melden hat — ein Vermerk ohne Sicherung heisst „es ist nichts passiert"
        // (Spec E-C).
        if (error is null || _journal.Read() is not { Files.Count: > 0 })
        {
            ClearJournalQuietly();
        }

        if (error is null)
        {
            chain.Complete();
        }
        else
        {
            chain.Fail(steamWillRestart: weStoppedSteam);
        }

        Report(chain, progress);

        var start = await BringSteamBackAsync(weStoppedSteam, chain, progress).ConfigureAwait(false);

        if (error is not null)
        {
            return Failed("Backup could not be restored", error, chain);
        }

        if (!Came(start))
        {
            return Failed("Backup restored, but Steam could not be started", "start it yourself", chain);
        }

        _logger?.LogInformation("restore complete");
        return new TransactionResult(
            TransactionOutcome.Succeeded, "Backup restored",
            start is null ? "Steam was not running" : "Steam restarted")
        {
            Chain = chain.Links,
        };
    }

    /// <summary>
    /// Verwirft den Vermerk. <b>Die Sicherungen bleiben liegen</b> — sie gehoeren dem Nutzer
    /// und stehen in S4.7 zur Verfuegung.
    /// </summary>
    public void Dismiss()
    {
        ClearJournalQuietly();
        _logger?.LogInformation("pending transaction dismissed");
    }

    /// <summary>
    /// Spielt alles zurueck, was im Vermerk steht, in Vermerk-Reihenfolge. <c>null</c> heisst
    /// „alles zurueck"; sonst der Klartext des ersten Fehlschlags — und dann bleibt der
    /// <b>ganze</b> Vermerk liegen (E-E): Einen Vermerk zu loeschen, dessen Arbeit nicht getan
    /// ist, waere die einzige Art, hier Daten zu verlieren.
    /// </summary>
    private string? RestoreFromJournal()
    {
        var record = _journal.Read();
        if (record is null || record.Files.Count == 0)
        {
            return null;
        }

        _logger?.LogInformation("rolling back {Count} file(s)", record.Files.Count);
        foreach (var file in record.Files)
        {
            try
            {
                _backups.Restore(file.BackupPath, file.Path);
            }
            catch (Exception exception) when (IsFileTrouble(exception))
            {
                // FileNotFoundException ist eine IOException — die fehlende Sicherung faellt
                // hier mit hinein, und ihre Meldung traegt den Pfad (S4.1).
                _logger?.LogWarning(exception, "rollback failed: {Error}", exception.Message);
                return exception.Message;
            }
        }

        return null;
    }
}
