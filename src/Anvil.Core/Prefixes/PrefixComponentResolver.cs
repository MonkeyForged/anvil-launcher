namespace Anvil.Core.Prefixes;

/// <summary>
/// Welche Fassung eines Katalogs eine Forderung erfüllen kann, und welche davon gilt — der
/// Gegenstand von S3.5a §3.2. Zustandslos (P-1): gleiche Eingabe, gleiche Ausgabe, wie
/// <see cref="PrefixComponentComposer"/> nebenan. Der Zusammenführer löst das benachbarte
/// Problem — die Forderungen mehrerer Module gegeneinander abgleichen; dieser hier beantwortet
/// die einfachere Frage, welche Fassungen <b>eine einzelne</b> Forderung erfüllen könnten. Der
/// Auflöser kennt keine konkrete Komponente, nur den Katalog, der ihm gereicht wird.
///
/// <para>
/// <b>Warum nirgends verglichen wird</b> (Spec S3.2, E-2): Anvil kennt fremde Versionsschemata
/// nicht und wäre beim ersten <c>4.8.1-preview</c> falsch. Einschluss wird <b>deklariert</b>,
/// nicht gerechnet — dieselbe Regel wie im Zusammenführer, hier nur auf eine einzelne Forderung
/// angewendet statt auf mehrere gegeneinander.
/// </para>
///
/// <para>
/// <b><see cref="Resolve"/> ist die Naht für S3.5b:</b> Die Wahl des Nutzers tritt an genau diese
/// Stelle (E-4) — als <c>chosenVersion</c>; ohne sie wählt die Methode selbst.
/// </para>
/// </summary>
public static class PrefixComponentResolver
{
    /// <summary>
    /// Die Fassungen aus <paramref name="catalog"/>, die <paramref name="required"/> erfüllen
    /// könnten. Reihenfolge des Katalogs bleibt erhalten.
    /// </summary>
    /// <param name="catalog">Der Katalog, über den gesucht wird.</param>
    /// <param name="required">Die geforderte Fassung.</param>
    public static IReadOnlyList<PrefixComponent> Candidates(
        IReadOnlyList<PrefixComponent> catalog, PrefixComponent required)
    {
        var candidates = new List<PrefixComponent>();
        foreach (var entry in catalog)
        {
            if (IsCandidate(entry, required))
            {
                candidates.Add(entry);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Die Fassung, die für <paramref name="required"/> gilt. Nie <c>null</c>, nie eine
    /// Ausnahme: Ein Modul, dessen Fassung nicht im Katalog steht, ist ein Programmierfehler —
    /// aber er darf die Modules-Seite nicht mitreißen.
    /// </summary>
    /// <param name="catalog">Der Katalog, über den gesucht wird.</param>
    /// <param name="required">Die geforderte Fassung.</param>
    /// <param name="chosenVersion">
    /// Die gespeicherte Wahl des Nutzers (Spec S3.5b §3.2), oder <c>null</c>. Sie gilt nur, wenn
    /// sie die Version eines Kandidaten ist; sonst wird sie ignoriert — nicht gelöscht, Lesen
    /// schreibt nicht — und die Regel ohne Wahl greift.
    /// </param>
    public static PrefixComponent Resolve(
        IReadOnlyList<PrefixComponent> catalog, PrefixComponent required, string? chosenVersion = null)
    {
        var candidates = Candidates(catalog, required);

        if (chosenVersion is not null)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Version == chosenVersion)
                {
                    // Die Wahl des Nutzers (Spec S3.5b §3.2) — nur unter den Kandidaten, nie
                    // verglichen. Trifft sie die geforderte Version, gilt die geforderte Instanz
                    // selbst, wie in der Regel ohne Wahl.
                    return candidate.Version == required.Version ? required : candidate;
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Version == required.Version)
            {
                // Die geforderte Fassung steht selbst im Katalog — sie gilt, unverändert.
                return required;
            }
        }

        // Kein Kandidat trifft die geforderte Version genau: der erste Kandidat in
        // Katalogreihenfolge gilt (eine gültige Wahl des Nutzers griff schon oben, E-4). Ist die
        // Liste leer, bleibt nur die geforderte Fassung selbst — nie null, nie eine Ausnahme.
        return candidates.Count > 0 ? candidates[0] : required;
    }

    // Kandidat ist eine Fassung mit gleichem Schlüssel, deren Version die geforderte ist oder
    // die geforderte in ihren Includes deklariert. Kein Zahlenvergleich, kein Sortieren nach
    // Version (Spec S3.2, E-2) — nur Zeichenkettengleichheit und eine Mitgliedschaftsprüfung in
    // einer deklarierten Liste.
    private static bool IsCandidate(PrefixComponent entry, PrefixComponent required)
        => entry.Key == required.Key
            && (entry.Version == required.Version || entry.Includes.Contains(required.Version));
}
