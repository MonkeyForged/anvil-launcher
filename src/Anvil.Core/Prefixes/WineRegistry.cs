using System.Text;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Liest einen Zeichenkettenwert aus Wines <c>system.reg</c> (S3.1, Spec §3.1 und §4.3).
///
/// <para>
/// <b>Kein VDF und kein Windows-<c>.reg</c>.</b> Wine schreibt ein eigenes Textformat:
/// Kopfzeile <c>WINE REGISTRY Version 2</c>, danach Blöcke, deren Schlüsselzeile in eckigen
/// Klammern steht und die Backslashes <b>verdoppelt</b> trägt, gefolgt von einem Zeitstempel.
/// Alle Schlüssel sind relativ zu <c>HKEY_LOCAL_MACHINE</c> — das sagt die zweite Zeile der
/// Datei selbst.
/// </para>
///
/// <para>
/// <b>Nur Zeichenketten</b> (Spec E-4). <c>dword:</c>-Werte werden bewusst nicht gelesen. Der
/// einzige Fall, für den man das bauen wollte, ist der gemessen falsche: Wines erfundener
/// <c>Release</c>-Wert unter <c>NDP\v4\Full</c> ist <b>533 320</b> und damit größer als
/// Microsofts 4.8-Schwelle 528 040 — ein Zahlenvergleich meldete auf jedem frischen Prefix
/// Erfolg (Spec §3.2).
/// </para>
///
/// <para>
/// <b>Gelesen wird zeilenweise mit Abbruch beim Fund.</b> Ein frischer Prefix bringt 4,1 MB und
/// 17 497 Schlüssel mit; die Datei für eine einzige Frage vollständig in den Speicher zu holen,
/// wäre Verschwendung.
/// </para>
/// </summary>
public static class WineRegistry
{
    /// <summary>
    /// Der Wert <paramref name="name"/> unter <paramref name="key"/>, oder <c>null</c> — der
    /// Schlüssel fehlt, der Wert fehlt, oder er ist keine Zeichenkette.
    /// </summary>
    /// <param name="systemRegPath">
    /// Die Datei <c>system.reg</c> im Prefix — oder <c>user.reg</c>, dasselbe Format (S3.6).
    /// </param>
    /// <param name="key">
    /// Der Schlüssel mit <b>einfachen</b> Backslashes, z. B.
    /// <c>Software\Microsoft\NET Framework Setup\NDP\v4\Full</c>. Die Verdopplung macht der
    /// Leser; wer sie an den Aufrufer weitergäbe, hätte Wines Dateiformat in der
    /// Komponentendeklaration stehen.
    /// </param>
    /// <param name="name">Der Wertname ohne Anführungszeichen, z. B. <c>Version</c>.</param>
    /// <param name="warnings">
    /// Nimmt nicht-fatale Befunde entgegen (Datei fehlt, Datei unlesbar). Der Leser wirft nie —
    /// dieselbe Form wie <see cref="Anvil.Core.Steam.ProtonConfigInfo"/>.
    /// </param>
    public static string? ReadString(
        string systemRegPath, string key, string name, ICollection<string> warnings)
    {
        if (!File.Exists(systemRegPath))
        {
            warnings.Add($"No Wine registry at {systemRegPath}.");
            return null;
        }

        var keyHeader = "[" + key.Replace("\\", "\\\\") + "]";
        var valuePrefix = "\"" + name + "\"=";

        try
        {
            var inBlock = false;
            foreach (var line in File.ReadLines(systemRegPath))
            {
                if (!inBlock)
                {
                    if (line.StartsWith(keyHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        inBlock = true;
                    }

                    continue;
                }

                // Eine neue Schlüsselzeile beendet den Block, bevor der Wert gefunden wurde.
                if (line.StartsWith('['))
                {
                    return null;
                }

                if (line.StartsWith(valuePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return ParseStringValue(line);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read {systemRegPath}: {exception.Message}");
            return null;
        }

        return null;
    }

    /// <summary>
    /// Nimmt den Teil einer Treffer-Zeile hinter dem ersten <c>=</c>. Beginnt er nicht mit einem
    /// Anführungszeichen, ist es kein Zeichenkettenwert (z. B. <c>dword:…</c>).
    /// </summary>
    private static string? ParseStringValue(string line)
    {
        var raw = line[(line.IndexOf('=') + 1)..];
        if (!raw.StartsWith('"'))
        {
            return null;
        }

        var closingQuote = raw.LastIndexOf('"');
        return Unescape(raw[1..closingQuote]);
    }

    /// <summary>Wandelt Wines Escapes zurück: <c>\\</c> → <c>\</c> und <c>\"</c> → <c>"</c>.</summary>
    private static string Unescape(string content)
    {
        var builder = new StringBuilder(content.Length);
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\\' && i + 1 < content.Length)
            {
                i++;
            }

            builder.Append(content[i]);
        }

        return builder.ToString();
    }
}
