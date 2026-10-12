namespace Anvil.Core.Modules;

/// <summary>
/// Ein Ordner, den ein Modul <b>je Prefix</b> gefuehrt haben will (Spec S4.6 §3.1) — fuer Wand
/// Bridge der Datenordner, in dem die Anmeldung liegt.
///
/// <para>
/// <b>Vorlage absolut, Ziel relativ.</b> Die Vorlage liegt im Ordner des Moduls, den nur das Modul
/// kennt; der Prefix gehoert dem Core. Gaebe der Core einen Prefix-Pfad heraus, duerfte ein Modul
/// ueberall dort schreiben — die Naht waere um eine Vollmacht breiter, nicht um ein Glied.
/// </para>
/// </summary>
/// <param name="TemplatePath">
/// Die Vorlage, absolut — im Betrieb <c>&lt;Datenwurzel&gt;/&lt;Modulschluessel&gt;/…</c>. Sie darf
/// fehlen: Dann ist noch nichts zu saeen.
/// </param>
/// <param name="PrefixPath">
/// Das Ziel, <b>relativ zu <c>drive_c/</c></b> und mit <c>/</c> getrennt — die Hausform seit S3.6
/// (<c>PrefixPayload</c>).
/// </param>
/// <param name="Skipped">
/// Namen der <b>ersten Ebene</b>, die nie mitreisen — Caches und Sperrdateien. Leere Liste heisst
/// „alles reist mit".
/// </param>
public sealed record AttachStateFolder(
    string TemplatePath, string PrefixPath, IReadOnlyList<string> Skipped)
{
    /// <summary>
    /// Wo dieser Ordner in <paramref name="prefixPath"/> liegt. Der Prefix-Pfad ist der
    /// <c>pfx</c>-Ordner, wie ihn <c>GamePrefix.Find</c> liefert.
    /// </summary>
    public string PathIn(string prefixPath)
        => Path.Combine([prefixPath, "drive_c", .. PrefixPath.Split('/')]);

    // C# erlaubt Records keinen eigenen Konstruktorkoerper — die Pruefung sitzt deshalb in einem
    // Initialisierer, der die generierte Eigenschaft ueberschreibt. Hausform aus PrefixPayload.

    /// <summary>
    /// Frueh knallen bei einem Pfad, der nicht der Hausform folgt: Ein fuehrender Schraegstrich
    /// machte aus dem Ziel einen absoluten Pfad, ein Backslash landete als Dateiname. Beides ist
    /// ein Programmierfehler im Modul, kein Zustand zur Laufzeit.
    /// </summary>
    public string PrefixPath { get; init; } = Validated(PrefixPath);

    private static string Validated(string prefixPath)
    {
        if (prefixPath.Length == 0
            || prefixPath.StartsWith('/')
            || prefixPath.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The path must be relative to drive_c/ and use '/' as separator, but got "
                + $"'{prefixPath}'.",
                nameof(prefixPath));
        }

        return prefixPath;
    }
}
