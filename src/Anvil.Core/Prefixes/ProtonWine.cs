using Anvil.Core.Processes;
using Anvil.Core.Steam;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Wo Protons eigenes Wine liegt, und die zwei Umgebungen, mit denen es laufen muss.
///
/// <para>
/// Gemessen am 2026-09-15 auf der Owner-Maschine: <c>&lt;Werkzeugordner&gt;/files/bin/wine</c>
/// und <c>…/wineserver</c> liegen dort und sind ausführbar. <b>Protons Wine läuft direkt auf dem
/// Host</b>, ohne den Steam-Linux-Runtime-Container — ein Rezept braucht also keinen
/// <c>steam-runtime-launch-client</c>, um es zu starten.
/// </para>
///
/// <para>
/// <b>Proton 8.0 trägt <c>dist/</c> statt <c>files/</c></b> (gemessen am 2026-09-16, Spec S3.6
/// §2, Fund 4). Beide Orte werden gesucht, <c>files/</c> zuerst. <b>Ob das 8er-Wine direkt auf
/// dem Host läuft, ist nicht gemessen</b> — S3.5 hat das nur für <c>files/</c> gezeigt.
/// </para>
///
/// <para>
/// <b>Die zwei Umgebungen sind die teuerste Kleinigkeit dieser Story</b> (Spec §3.2), und beide
/// Hälften der Regel hat der Vorgänger an der echten Maschine bezahlt:
/// </para>
/// <para>
/// <b>Installer brauchen einen erreichbaren Bildschirm.</b> Fehlen ihm <c>DISPLAY</c>,
/// <c>WAYLAND_DISPLAY</c> oder <c>XDG_RUNTIME_DIR</c>, zieht der .NET-4.0-Installer einen
/// Wine-Desktop hoch und wartet darauf <b>unbegrenzt</b> — gemessen mehrere Minuten ohne eine
/// Sekunde Rechenzeit, wo sonst rund eine Minute reicht. Deshalb behält
/// <see cref="Environment"/> den Bildschirm.
/// </para>
/// <para>
/// <b><c>regedit</c> darf dagegen keinen Bildschirm finden.</b> Läuft <c>regedit /E</c> auf einen
/// Registry-Zweig, den es im Prefix nicht gibt, öffnet es sonst einen <b>modalen Dialog</b> und
/// kehrt nie zurück. Und <c>WAYLAND_DISPLAY</c> allein zu entfernen genügt <b>nicht</b>: Fehlt nur
/// diese Variable, fällt die Wayland-Bibliothek auf den Namen <c>wayland-0</c> zurück und findet
/// den Socket trotzdem über <c>XDG_RUNTIME_DIR</c>. Weg müssen alle drei zugleich — genau das tut
/// <see cref="RegistryEnvironment"/>. Das ist zugleich der Grund, warum <see cref="ProcessRunner"/>
/// die gereichte Umgebung als vollständig behandelt: Nur eine geleerte und neu befüllte Umgebung
/// entfernt eine Variable wirklich, statt sie nur zu überschreiben.
/// </para>
/// </summary>
public static class ProtonWine
{
    private const string WineFileName = "wine";
    private const string Wine64FileName = "wine64";
    private const string WineServerFileName = "wineserver";

    /// <summary>
    /// Der Pfad zu Protons <c>wine64</c>, sonst zu <c>wine</c>, oder <c>null</c>. Existenz ist der
    /// einzige Beweis — jede <see cref="IOException"/> beim Nachsehen ergibt ebenfalls
    /// <c>null</c>, kein Wurf.
    ///
    /// <para>
    /// <b>Warum <c>wine64</c> zuerst</b> (Spec S3.6 §2, Fund 6): Proton 8 und 9 tragen ein
    /// <b>32-Bit</b>-<c>wine</c> neben <c>wine64</c>; ab Proton 11 ist <c>wine</c> selbst 64 Bit
    /// und <c>wine64</c> fehlt. Ein <c>regedit</c> über das 32-Bit-<c>wine</c> legt HKLM-Schlüssel
    /// unter <c>Software\Wow6432Node</c> ab, nicht unter <c>Software</c> — gemessen am 2026-09-16
    /// mit Gegenprobe über <c>wine64</c>. Die erste Live-Abnahme von S3.6 scheiterte genau daran:
    /// Dateien und der HKCU-Override kamen an, die .NET-Version nicht. Proton selbst startet
    /// Spiele ebenfalls über <c>wine64</c>, 32-Bit-Programme reicht Wine von dort weiter.
    /// </para>
    /// </summary>
    public static string? Find(CompatTool tool)
        => FindExecutable(tool, Wine64FileName) ?? FindExecutable(tool, WineFileName);

    /// <summary>Derselbe Weg, für <c>wineserver</c>.</summary>
    public static string? FindServer(CompatTool tool) => FindExecutable(tool, WineServerFileName);

    // Proton ab 9.0 legt Wine unter files/ ab, Proton 8.0 unter dist/ (gemessen am 2026-09-16,
    // Spec S3.6 §2, Fund 4). files/ zuerst: Das ist die Bauform, mit der S3.5 live abgenommen wurde.
    private static readonly string[] BinParents = ["files", "dist"];

    private static string? FindExecutable(CompatTool tool, string fileName)
    {
        ArgumentNullException.ThrowIfNull(tool);

        foreach (var parent in BinParents)
        {
            var path = Path.Combine(tool.Path, parent, "bin", fileName);
            try
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch (IOException)
            {
                // Ein Werkzeugordner, der gerade unerreichbar ist (z. B. eine ausgehängte externe
                // Platte), ist kein Absturzgrund — "nicht gefunden" ist die ehrliche Antwort.
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Die Umgebung für Installer-Stufen: eine Kopie von <paramref name="baseEnvironment"/> <b>mit
    /// erreichbarem Bildschirm</b>, dazu <c>WINEPREFIX</c> und <c>WINEDEBUG=-all</c>. Der
    /// übergebene Ausgangswert bleibt unverändert — es wird eine neue Sammlung geliefert.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(
        IReadOnlyDictionary<string, string> baseEnvironment, string prefixPath)
    {
        ArgumentNullException.ThrowIfNull(baseEnvironment);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);

        var result = new Dictionary<string, string>(baseEnvironment, StringComparer.Ordinal)
        {
            ["WINEPREFIX"] = prefixPath,
            ["WINEDEBUG"] = "-all",
        };
        return result;
    }

    /// <summary>
    /// Dieselbe Umgebung, aber für <c>regedit</c>: <b>ohne</b> <c>DISPLAY</c>,
    /// <c>WAYLAND_DISPLAY</c> und <c>XDG_RUNTIME_DIR</c> — alle drei, sonst findet Wayland den
    /// Socket trotzdem (Klassenkommentar). Der übergebene Ausgangswert bleibt unverändert.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RegistryEnvironment(
        IReadOnlyDictionary<string, string> baseEnvironment, string prefixPath)
    {
        var result = new Dictionary<string, string>(
            Environment(baseEnvironment, prefixPath), StringComparer.Ordinal);
        result.Remove("DISPLAY");
        result.Remove("WAYLAND_DISPLAY");
        result.Remove("XDG_RUNTIME_DIR");
        return result;
    }

    /// <summary>
    /// Die Umgebung des laufenden Prozesses, als frische Sammlung. Einträge mit einem
    /// <c>null</c>-Schlüssel oder -Wert werden übersprungen — <see cref="Dictionary{TKey,TValue}"/>
    /// erlaubt beides nicht.
    /// </summary>
    public static Dictionary<string, string> Current()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in
            System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
