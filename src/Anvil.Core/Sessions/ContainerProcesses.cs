using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>Ein Prozess in der Wine-Welt eines Spiels, so wie <c>tasklist</c> ihn nennt.</summary>
/// <param name="Image">Der Bildname, etwa <c>WeMod.exe</c> — unveraendert aus der Ausgabe.</param>
/// <param name="Pid">Die <b>Windows</b>-PID. Sie gilt nur im Container; von Linux aus bedeutet
/// sie nichts (messungen-anhaengen.md §3).</param>
public readonly record struct ContainerProcess(string Image, int Pid);

/// <summary>
/// Liest die Ausgabe von <c>wine tasklist</c> (Spec S4.5 §3.1).
///
/// <para>
/// <b>Das Vorgabeformat, kein CSV</b> (E-3): <c>/FO CSV /NH</c> waere bequemer zu lesen, aber ob
/// Wines Nachbau die Schalter kennt, ist nicht gemessen — und ein nicht erkannter Schalter saehe
/// aus wie eine leere Liste. Aus der Absage „tasklist liefert nichts" wuerde dann unter Proton 11
/// ein stiller Fehlschlag. Gelesen wird deshalb, was im Vorgaengerprojekt sechs Live-Abnahmen
/// getragen hat.
/// </para>
///
/// <para>
/// <b>Tolerant, nicht spaltengenau:</b> Ein Eintrag ist eine Zeile, deren erstes Feld auf
/// <c>.exe</c> endet und auf die eine Zahl folgt. Kopfzeile, Trennzeile und Fremdtext fallen von
/// selbst heraus. Unlesbares heisst „nichts gefunden", nie eine Ausnahme.
/// </para>
/// </summary>
public static class ContainerProcesses
{
    private const string ExecutableSuffix = ".exe";

    public static IReadOnlyList<ContainerProcess> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var found = new List<ContainerProcess>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split(
                ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 2)
            {
                continue;
            }

            // Der Bildname steht zuerst, die PID dahinter. Alles andere — Session, Speicher —
            // braucht niemand, und je weniger Spalten gelesen werden, desto weniger kann sich
            // zwischen zwei Wine-Fassungen verschieben.
            var image = fields[0];
            if (!image.EndsWith(ExecutableSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!int.TryParse(
                    fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            found.Add(new ContainerProcess(image, pid));
        }

        return found;
    }
}
