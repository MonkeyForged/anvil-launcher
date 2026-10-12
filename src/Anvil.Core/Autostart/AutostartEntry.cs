using System.Text;

namespace Anvil.Core.Autostart;

/// <summary>
/// Der Autostart-Eintrag als reine Funktionen (Spec S3.6 §3.1): Exec-Zeile fuer das laufende
/// Programm, Dateiinhalt byte-genau, Quoting nach der Desktop-Entry-Spec. Nichts hier liest
/// oder schreibt — das tut <see cref="AutostartFile"/>.
/// </summary>
public static class AutostartEntry
{
    /// <summary>
    /// Der Startparameter fuer „ohne Fenster ins Tray". Die eine Stelle fuer die
    /// Zeichenkette (E-H): der Eintrag schreibt sie, die App liest sie.
    /// </summary>
    public const string TrayFlag = "--tray";

    /// <summary>
    /// Die Exec-Zeile. Gemessen (Spec, Tabelle): unter <c>dotnet run</c> und ueber den
    /// Apphost ist der Prozess das Programm selbst; nur unter <c>dotnet App.dll</c> ist er
    /// der Host, und dann braucht der Eintrag Host plus Assembly (E-A). Beide Argumente
    /// stehen in Anfuehrungszeichen (E-B).
    /// </summary>
    public static string ExecFor(string processPath, string entryAssemblyPath)
    {
        var builder = new StringBuilder(QuoteExecArgument(processPath));
        if (Path.GetFileName(processPath) == "dotnet")
        {
            builder.Append(' ').Append(QuoteExecArgument(entryAssemblyPath));
        }

        return builder.Append(' ').Append(TrayFlag).ToString();
    }

    /// <summary>
    /// Der Dateiinhalt: sechs Schluessel (E-C), „\n", Schlusszeile.
    /// <c>X-KDE-autostart-after=panel</c> ist der gemessene Plasma-Schluessel — die
    /// Kontrollleiste steht, bevor Anvil sein Zeichen anmeldet.
    /// </summary>
    public static string Render(string exec)
        => "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=Anvil\n"
            + "Exec=" + exec + "\n"
            + "Terminal=false\n"
            + "X-KDE-autostart-after=panel\n";

    /// <summary>
    /// Ein Argument fuer den Exec-Schluessel, immer in doppelten Anfuehrungszeichen. Die
    /// Desktop-Entry-Spec wendet zwei Escape-Regeln nacheinander an: erst die der
    /// Zeichenkette (Backslash verdoppelt), dann die des Quotings (Anfuehrungszeichen,
    /// Backtick, Dollar und Backslash bekommen einen Backslash). Ein Backslash im Pfad wird
    /// deshalb zu vier.
    /// </summary>
    public static string QuoteExecArgument(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append(@"\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '`':
                    builder.Append("\\`");
                    break;
                case '$':
                    builder.Append("\\$");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }
}
