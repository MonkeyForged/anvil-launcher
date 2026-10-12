using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Processes;

/// <summary>
/// Die einzige Stelle im Core, die <see cref="Process.Start(ProcessStartInfo)"/> für Rezeptstufen
/// aufruft.
///
/// <para>
/// <b>Die gereichte Umgebung ist die vollständige Umgebung des Kindprozesses</b> (Plan P-1):
/// <see cref="ProcessStartInfo.Environment"/> wird zuerst geleert, dann werden nur die Paare aus
/// <see cref="ProcessCommand.Environment"/> eingetragen. Das ist keine Vorsichtsmaßnahme, sondern
/// eine gemessene Notwendigkeit — beide Hälften hat der Vorgänger an der echten Maschine bezahlt
/// (Spec §3.2):
/// </para>
/// <para>
/// Eine geerbte <c>XDG_RUNTIME_DIR</c>, von der man annimmt, sie sei entfernt, lässt
/// <c>regedit /E</c> auf einen fehlenden Registry-Zweig mit einem unsichtbaren modalen Dialog
/// hängen bleiben, der nie zurückkehrt. Umgekehrt lässt eine <b>fehlende</b> Bildschirmvariable
/// den .NET-4.0-Installer unbegrenzt auf einen Wine-Desktop warten, den niemand öffnet. Ein
/// Ausführer, der die geerbte Umgebung nur überschreibt statt sie zu leeren, baut den ersten
/// Fehler wieder ein — und kein Test außer dem Umgebungstest dieser Aufgabe würde ihn finden.
/// </para>
///
/// <para>
/// <b>Ausgabe wird verworfen.</b> Beide Ströme werden umgeleitet und leer gelesen
/// (<see cref="StreamReader.ReadToEndAsync(CancellationToken)"/>), damit kein Puffer volläuft und
/// der Prozess daran hängen bleibt — der Inhalt selbst wird nicht gebraucht. Was gebraucht wird
/// (Kommando, <see cref="ProcessOutcome.Completed"/>, <see cref="ProcessOutcome.ExitCode"/>),
/// steht im Log des Aufrufers.
/// </para>
///
/// <para>
/// <b>Ein Abbruch tötet den Prozessbaum</b> (Spec E-6): Zeitüberschreitung und
/// <see cref="CancellationToken"/> enden beide in <c>Kill(entireProcessTree: true)</c> und
/// <c>Completed = false</c>. Es gibt keine dritte Ausprägung.
/// </para>
///
/// <para>
/// <b>Ein fehlendes Programm wirft nicht.</b> Startet der Prozess gar nicht erst (z. B. weil
/// <c>wine</c> fehlt), wird das geloggt und als <c>Completed = false</c> gemeldet. Ein Bauer, der
/// wegen einer durchgereichten Ausnahme abstürzt, reißt in S3.9 die Speicherleiste mit.
/// </para>
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    private readonly ILogger? _logger;

    /// <param name="logger">Die Kategorie bestimmt der Besitzer; <c>null</c> heißt stumm.</param>
    public ProcessRunner(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ProcessOutcome> RunAsync(
        ProcessCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // P-1: erst leeren, dann nur die gereichten Paare eintragen — die gereichte Sammlung
        // ist die vollständige Umgebung, nichts wird geerbt.
        startInfo.Environment.Clear();
        foreach (var (key, value) in command.Environment)
        {
            startInfo.Environment[key] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            // Programm nicht gefunden oder nicht ausführbar — kein Absturz, nur ein ehrliches
            // "nicht geschafft" (siehe Klassenkommentar).
            _logger?.LogWarning(
                exception, "process could not be started: {Command}", command.RenderShort());
            return new ProcessOutcome(Completed: false, ExitCode: null);
        }

        // Beide Ströme leer lesen, damit kein Puffer volläuft; der Inhalt wird verworfen.
        var readStdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var readStderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token, cancellationToken);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var reason = timeoutSource.IsCancellationRequested ? "timeout" : "canceled";
            _logger?.LogWarning(
                "process killed ({Reason}): {Command}", reason, command.RenderShort());

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Zwischen Zeitablauf und Kill von selbst beendet — nichts mehr zu tun.
            }

            try
            {
                // Nach dem Kill räumen: Sonst hängt eine unbeobachtete Ausnahme an den
                // Lese-Tasks, sobald "using process" die Ströme gleich darauf schließt.
                await Task.WhenAll(readStdout, readStderr).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Der Inhalt wird ohnehin verworfen (siehe Klassenkommentar) — nur das
                // Aufräumen zählt.
            }

            return new ProcessOutcome(Completed: false, ExitCode: null);
        }

        // Nach dem Ende laufen die Leser ohne eigenes Zeitlimit aus — der Prozess ist bereits
        // fertig, es geht nur noch darum, die Puffer zu räumen.
        await Task.WhenAll(readStdout, readStderr).ConfigureAwait(false);

        _logger?.LogDebug(
            "process finished with exit code {ExitCode}: {Command}",
            process.ExitCode,
            command.RenderShort());
        return new ProcessOutcome(Completed: true, ExitCode: process.ExitCode);
    }
}
