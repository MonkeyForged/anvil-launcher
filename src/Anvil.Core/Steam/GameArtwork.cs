namespace Anvil.Core.Steam;

/// <summary>
/// Findet das Steam-Artwork eines Spiels (Spec §5). Gesucht wird das <b>Querformat</b> —
/// <c>library_header.jpg</c> oder <c>header.jpg</c>, 460 × 215 —, weil Karte und Kopf der
/// rechten Spalte im Mockup quer liegen (Spec §4). Die Kapsel im Hochformat wird bewusst
/// nicht gesucht: es gibt in v1 keine Stelle, die sie zeigt.
///
/// <para>
/// Gemessen am 2026-09-04 über den ganzen <c>librarycache</c> (1278 Einträge): Steam benennt
/// dieselbe Rolle unter zwei Namen, legt aber <b>nie beide zugleich</b> ab — 304 Einträge
/// tragen <c>library_header.jpg</c>, 892 <c>header.jpg</c>, 0 beides. Die Rangfolge hier ist
/// deshalb eine Vorsichtsmaßnahme, kein Alltagsfall.
/// </para>
///
/// <para>
/// <b>Die Ablage ist gemischt, und zwar innerhalb eines Spiels:</b> Army of Ruin legt alles
/// lose ab, Blasphemous 2 alles in Hash-Ordner, Vampire Survivors mischt beides. Die
/// Hash-Ordner sind nicht berechenbar — gesucht wird nach <b>Dateinamen</b>, nicht nach
/// Pfaden.
/// </para>
/// </summary>
public static class GameArtwork
{
    /// <summary>
    /// Obergrenze geprüfter Dateien. Das größte gemessene Spielverzeichnis im
    /// <c>librarycache</c> hat sechs Dateien; der Deckel schützt vor Ausreißern, nicht vor
    /// dem Normalfall.
    /// </summary>
    public const int DefaultMaxEntries = 500;

    /// <summary>
    /// Die Namen der Querformat-Rolle, in absteigender Rangfolge. Kein Qualitätsunterschied —
    /// dieselbe Datei, nur zu verschiedenen Zeiten anders benannt.
    /// </summary>
    private static readonly string[] HeaderNames = ["library_header.jpg", "header.jpg"];

    /// <summary>
    /// Liefert den Pfad des Querformat-Artworks, oder <c>null</c>, wenn es keines gibt.
    /// Wirft nie.
    /// </summary>
    /// <param name="steamRoot">
    /// Die Steam-<b>Wurzel</b>, nicht die Bibliothek des Spiels: <c>appcache/librarycache</c>
    /// liegt einmal unter der Wurzel, anders als <c>compatdata</c>, das
    /// <see cref="GamePrefix"/> neben dem Manifest sucht.
    /// </param>
    /// <param name="appId">Die AppId des Spiels; sie ist zugleich der Ordnername.</param>
    /// <param name="warnings">
    /// Nimmt nicht-fatale Befunde entgegen (Ordner unlesbar, Deckel erreicht). Ein Spiel ohne
    /// Artwork ist <b>kein</b> Befund — das ist der Normalfall bei jedem Werkzeug.
    /// </param>
    /// <param name="maxEntries">Nur für Tests herabsetzen.</param>
    public static string? Find(
        string steamRoot,
        uint appId,
        ICollection<string>? warnings = null,
        int maxEntries = DefaultMaxEntries)
    {
        var directory = Path.Combine(
            steamRoot, "appcache", "librarycache", appId.ToString());

        if (!Directory.Exists(directory))
        {
            return null;
        }

        // Je Name der beste Fund. Zwei Namen, also zwei Plätze — mehr Zustand braucht es nicht.
        var best = new string?[HeaderNames.Length];
        var seen = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(
                directory, "*", SearchOption.AllDirectories))
            {
                if (++seen > maxEntries)
                {
                    warnings?.Add(
                        $"Stopped looking for artwork of app {appId} after {maxEntries} files.");
                    break;
                }

                var name = Path.GetFileName(file);
                for (var rank = 0; rank < HeaderNames.Length; rank++)
                {
                    if (!name.Equals(HeaderNames[rank], StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (best[rank] is null || Wins(file, best[rank]!))
                    {
                        best[rank] = file;
                    }

                    break;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            warnings?.Add($"Could not read artwork of app {appId}: {error.Message}");
        }

        return best.FirstOrDefault(found => found is not null);
    }

    /// <summary>
    /// Der Gleichstand-Entscheider: die neuere Schreibzeit gewinnt, bei gleicher Zeit der
    /// alphabetisch kleinere Pfad. Auf der echten Maschine tritt er nie ein — keine AppId
    /// trägt denselben Dateinamen zweimal (Spec §2.3) —, aber ohne ihn entschiede die
    /// Reihenfolge des Dateisystems.
    /// </summary>
    private static bool Wins(string candidate, string current)
    {
        var left = File.GetLastWriteTimeUtc(candidate);
        var right = File.GetLastWriteTimeUtc(current);

        return left != right
            ? left > right
            : string.CompareOrdinal(candidate, current) < 0;
    }
}
