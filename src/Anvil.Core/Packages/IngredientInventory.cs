using System.Security.Cryptography;
using Anvil.Core.Prefixes;

namespace Anvil.Core.Packages;

/// <summary>
/// Erkennt, welche Zutaten eines Rezepts in einer Ablage liegen — über den Namen, und wenn der
/// fehlt, über Größe und Prüfsumme (S3.5a §3.4). Zustandslos (Plan P-1): eine statische Klasse
/// ohne Zustand, gleiche Eingabe, gleiche Ausgabe, wie <see cref="PrefixComponentResolver"/>
/// daneben.
///
/// <para>
/// <b>Warum zwei Stufen</b> (Spec §3.4): Der Name ist die schnelle Abkürzung — Anvils eigener
/// Bezug legt genau diesen Namen ab, und die Anleitung nennt ihn. Die Prüfsumme ist die
/// Sicherheit: Erkannt wird, was da ist, nicht was heißen soll — eine Datei, die der Nutzer
/// umbenannt hat, ist dieselbe Datei (Spec §1).
/// </para>
///
/// <para>
/// <b>Warum die Größe als Vorfilter:</b> Ohne ihn läse Anvil für eine Frage, die meist schon der
/// Name beantwortet, jede Datei der Ablage — bis zu 160 MB. Mit ihm bleiben in aller Regel null
/// oder eine Datei übrig, und nur von denen wird tatsächlich gelesen.
/// </para>
///
/// <para>
/// <b>Warum kein Zwischenspeicher für Prüfsummen</b> (E-3): Stufe 2 läuft nur, wenn ohnehin etwas
/// fehlt; dann wartet der Nutzer lieber eine Sekunde, als ratlos dazustehen. Ein Zwischenspeicher
/// wäre Zustand, der veralten kann — die erste Frage wäre, wann er ungültig wird. Das baut man,
/// wenn man die Messung hat, nicht auf Verdacht.
/// </para>
/// </summary>
public static class IngredientInventory
{
    /// <summary>
    /// Ordnet jeder Zutat höchstens eine Datei der Ablage zu — der Rest ist Fehlen. Zwei Zutaten
    /// mit derselben Datei wären eine Unwahrheit über den Bestand; die Zuordnung läuft deshalb in
    /// der Reihenfolge von <paramref name="ingredients"/>, und eine bereits vergebene Datei steht
    /// der nächsten Zutat nicht mehr zur Verfügung.
    /// </summary>
    /// <param name="files">Der Bestand der Ablage (<see cref="PackageStore.List"/>).</param>
    /// <param name="ingredients">Die Zutaten, deren Zustand ermittelt werden soll.</param>
    /// <param name="sourceLookup">
    /// Das zweite Manifest (<c>PackageCatalog.SourceFor</c>) — liefert Größe und erwartete
    /// Prüfsumme für Stufe 2, oder <c>null</c> für eine Zutat ohne Quelleneintrag; dann bleibt es
    /// bei Stufe 1.
    /// </param>
    /// <param name="sha256">
    /// Rechnet die SHA-256 einer Datei (voller Pfad → 64 kleine Hexzeichen). Vorgabe: die echte
    /// Summe, in der Form von <see cref="PackageFetcher"/>. Wirft die Datei beim Lesen
    /// (verschwunden, unlesbar), gilt die Kandidatin als nicht passend — geloggt wird nicht,
    /// diese Klasse hat keinen Logger.
    /// </param>
    public static IReadOnlyList<IngredientState> Locate(
        IReadOnlyList<StoredFile> files,
        IReadOnlyList<RecipeIngredient> ingredients,
        Func<string, CatalogEntry?> sourceLookup,
        Func<string, string>? sha256 = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ingredients);
        ArgumentNullException.ThrowIfNull(sourceLookup);

        var hash = sha256 ?? ComputeSha256;
        // Eigene Liste statt der übergebenen: Remove() darf die Liste des Aufrufers nicht
        // verändern, und eine bereits vergebene Datei muss aus den Kandidaten der nächsten
        // Zutat verschwinden.
        var remaining = new List<StoredFile>(files);
        var states = new List<IngredientState>(ingredients.Count);

        foreach (var ingredient in ingredients)
        {
            var match = LocateOne(ingredient, remaining, sourceLookup, hash);
            if (match is not null)
            {
                remaining.Remove(match);
            }

            states.Add(new IngredientState(ingredient, match?.FullPath));
        }

        return states;
    }

    // Stufe 1 (Name), dann — nur wenn Stufe 1 nichts fand — Stufe 2 (Größe, dann Prüfsumme).
    // Beide laufen gegen "remaining", nie gegen die volle Ablage, damit keine Datei zweimal
    // vergeben wird.
    private static StoredFile? LocateOne(
        RecipeIngredient ingredient,
        IReadOnlyList<StoredFile> remaining,
        Func<string, CatalogEntry?> sourceLookup,
        Func<string, string> hash)
    {
        foreach (var file in remaining)
        {
            if (string.Equals(file.Name, ingredient.FileName, StringComparison.Ordinal))
            {
                // Stufe 1 trifft: nichts wird gelesen.
                return file;
            }
        }

        var entry = sourceLookup(ingredient.FileName);
        if (entry is null)
        {
            // Ohne Quelleneintrag gibt es keine zweite Stufe: Größe und Summe kennt niemand
            // (Spec §3.4).
            return null;
        }

        foreach (var file in remaining)
        {
            if (file.SizeBytes != entry.SizeBytes)
            {
                // Der Vorfilter: nur von Dateien passender Größe wird die Summe gerechnet.
                continue;
            }

            // Zeichengenau, wie PackageFetcher vergleicht (Korrektur zum Plan): CatalogEntry.Sha256
            // erzwingt Kleinschreibung, ComputeSha256 liefert sie ebenfalls — ein Unterschied, den
            // OrdinalIgnoreCase verstecken könnte, gibt es nicht.
            if (string.Equals(hash(file.FullPath), entry.Sha256, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    // Folgt der Form aus PackageFetcher.FetchAsync (Plan P-4): SHA-256 über die fertige Datei,
    // in Kleinschreibung. Locate selbst ist synchron, deshalb SHA256.HashData über einen
    // FileStream statt der asynchronen Variante dort.
    private static string ComputeSha256(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Verschwunden oder unlesbar zwischen Auflisten und Lesen. Eine leere Zeichenkette
            // ist kein gültiger Hash und trifft nie auf CatalogEntry.Sha256 (64 Hexzeichen) —
            // die Kandidatin gilt damit als nicht passend, ohne dass diese Klasse einen Logger
            // bräuchte oder werfen müsste.
            return string.Empty;
        }
    }
}
