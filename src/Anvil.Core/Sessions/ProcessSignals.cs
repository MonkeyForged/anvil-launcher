using System.Runtime.InteropServices;

namespace Anvil.Core.Sessions;

/// <summary>Die beiden Signale, die ein Stop braucht (Spec §4.3).</summary>
public enum ProcessSignal
{
    /// <summary>
    /// SIGTERM — die hoefliche Bitte. Sie traegt bei nativen Spielen; an einer Wine-Welt
    /// prallt sie ab (Uebergabe §2, an Boltgun gemessen 2026-08-26, 60 s ohne Wirkung).
    /// </summary>
    Terminate = 15,

    /// <summary>SIGKILL — nicht abfangbar, nicht verhandelbar.</summary>
    Kill = 9,
}

/// <summary>
/// Sendet ein Signal an eine PID (Spec §4.3). .NET kann nur SIGKILL
/// (<see cref="System.Diagnostics.Process.Kill()"/>); fuer SIGTERM gibt es keine API, also
/// geht der Weg ueber <c>kill(2)</c> aus der libc — vier Zeilen, keine Abhaengigkeit, kein
/// Hilfsprozess.
///
/// <para>
/// <b>Der Rueckgabewert wird nicht ausgewertet.</b> Eine PID, die zwischen Scan und Signal
/// verschwindet, antwortet mit ESRCH — beim zweiten Schuss ist das der Normalfall und kein
/// Fehler.
/// </para>
/// </summary>
public static partial class ProcessSignals
{
    public static void Send(int pid, ProcessSignal signal) => Kill(pid, (int)signal);

    [LibraryImport("libc", EntryPoint = "kill")]
    private static partial int Kill(int pid, int signal);
}
