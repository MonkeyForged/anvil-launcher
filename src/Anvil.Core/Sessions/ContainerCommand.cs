using System.Diagnostics;

namespace Anvil.Core.Sessions;

/// <summary>Was ein abgewarteter Aufruf hinterlaesst.</summary>
/// <param name="Completed">Ob er in der Frist fertig wurde. <c>false</c> heisst: Er laeuft
/// womoeglich noch — ihm ist nichts angetan worden.</param>
/// <param name="ExitCode">Sein Rueckgabewert; <c>null</c>, wenn er nicht fertig wurde.</param>
/// <param name="StandardOutput">Die Ausgabe, leer bei Fristablauf.</param>
/// <param name="StandardError">Die Fehlerausgabe, leer bei Fristablauf.</param>
public sealed record CommandOutput(
    bool Completed, int? ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Ein Aufruf, der <b>abgewartet</b> wird und seine Ausgabe mitbringt (Spec S4.5 §3.2) — anders
/// als das Anhaengen, das losstartet und nur hinsieht.
///
/// <para>
/// <b>Abgewartet ja, abgeraeumt nie</b> (E-5). Es gibt hier kein <c>Kill()</c>, keine
/// Prozessgruppe, kein Signal. Genau ein <c>Kill()</c> auf einen abgewarteten Startclient hat im
/// Vorgaengerprojekt den ganzen Container abgerissen und das Spiel mitgenommen (poc-findings.md
/// §4, Vision §6.1). Laeuft ein Aufruf in die Frist, bleibt er liegen; der Preis ist ein
/// verwaister Prozess, der Gegenwert ein Spiel, das weiterlaeuft.
/// </para>
///
/// <para>
/// <b>Beide Stroeme werden gelesen</b>, sonst blockiert ein voller Puffer den Aufruf, sobald
/// <c>tasklist</c> mehr als ein paar Zeilen schreibt.
/// </para>
/// </summary>
public static class ContainerCommand
{
    public static async Task<CommandOutput> RunAsync(
        ProcessStartInfo info, TimeSpan limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);

        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;

        var process = Process.Start(info)
            ?? throw new InvalidOperationException("The process could not be started.");

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limit);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Die Frist ist abgelaufen. Dem Prozess wird nichts angetan (E-5); nur seine beiden
            // Lesevorgaenge werden noch abgeraeumt, damit keine unbeobachtete Ausnahme liegen
            // bleibt.
            Ignore(output);
            Ignore(error);
            return new CommandOutput(false, null, string.Empty, string.Empty);
        }

        using (process)
        {
            return new CommandOutput(
                true,
                process.ExitCode,
                await output.ConfigureAwait(false),
                await error.ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Nimmt einer verwaisten Leseaufgabe ihre Ausnahme ab. Ohne das stuerbe sie unbeobachtet,
    /// sobald der liegengelassene Prozess seine Stroeme schliesst.
    /// </summary>
    private static void Ignore(Task task)
        => _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
