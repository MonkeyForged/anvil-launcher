using System.Diagnostics;

namespace Anvil.Core;

/// <summary>
/// Reicht Pfade und URLs über <c>xdg-open</c> an das System weiter. In S1.6 für den
/// Log-Ordner, in M2 für „Installation folder" und „Open prefix" (Spec S1.6 E11), ab S3.2 für
/// Steams `steam://`-Adressen (Spec S3.2 §4.2).
/// </summary>
public static class DesktopShell
{
    /// <summary>
    /// Reicht einen Pfad oder eine URL an das System weiter. Ungeprueft — hier wird ein
    /// fremder Prozess gestartet; geprueft wird <see cref="StartInfoFor"/>.
    /// </summary>
    public static void Open(string target)
    {
        using var process = Process.Start(StartInfoFor(target));
    }

    /// <summary>Der sprechende Name fuer den Fall, der es seit S1.6 gibt: ein Ordner.</summary>
    public static void OpenFolder(string path) => Open(path);

    /// <summary>
    /// Wie der Aufruf aussieht. Getrennt vom Start, weil <see cref="OpenFolder"/> selbst einen
    /// fremden Prozess startet und deshalb nicht prüfbar ist — die Übergabe dagegen schon, und
    /// genau dort saß der Fehler vom 2026-09-05.
    ///
    /// <para>
    /// <b>Der Pfad geht über <see cref="ProcessStartInfo.ArgumentList"/>, nie über
    /// <see cref="ProcessStartInfo.Arguments"/>.</b> Die Argumentzeile wird von .NET an
    /// Leerzeichen zerlegt: „All Hail the Orb" kam als vier Argumente an, und <c>xdg-open</c>
    /// antwortete mit <c>unexpected argument 'Hail'</c>. <c>ArgumentList</c> reicht jeden
    /// Eintrag unzerlegt weiter und kümmert sich selbst ums Quoting.
    /// </para>
    ///
    /// <para>
    /// Aufgefallen ist der Fehler erst mit S2.6: Bis dahin war der einzige Aufrufer der
    /// Log-Ordner unter <c>~/.local/state/anvil</c> — ein Pfad ohne Leerzeichen. Steam benennt
    /// Spielordner nach dem Spiel.
    /// </para>
    /// </summary>
    public static ProcessStartInfo StartInfoFor(string path)
    {
        // UseShellExecute bleibt aus: Mit Shell übernähme diese das Zerlegen, und die Falle
        // wäre nur eine Ebene tiefer dieselbe.
        var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        info.ArgumentList.Add(path);
        return info;
    }
}
