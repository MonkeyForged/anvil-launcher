namespace Anvil.Core.Steam;

/// <summary>
/// Zeichnet aus der eigenen Angabe des Nutzers und den Beiträgen der Module den Wert, den
/// Steam bekommt — und erkennt in einem vorgefundenen Wert den eigenen Anteil wieder (S2.3).
/// Beide Richtungen stehen hier, weil es dieselbe Formatregel ist (Vision §4.4). Zustandslos:
/// gleiche Eingabe, gleiche Ausgabe.
///
/// <para>
/// <b>Ohne Beitrag ohne <c>%command%</c> geht die eigene Angabe wörtlich zurück, nie mit
/// angehängtem Platzhalter</b> (E-8). Gibt es dagegen mindestens einen Beitrag und fehlt der
/// Platzhalter in der eigenen Angabe, landet die ganze Angabe <b>hinter</b> <c>%command%</c>,
/// nicht davor — gemessen an der echten Steam-Installation
/// (<c>docs/referenz/poc-findings.md</c> §10: „Fehlt <c>%command%</c> ganz, behandelt Steam
/// den kompletten Inhalt als Argumente für das Spiel."). Die Design-Zeichnung
/// (<c>docs/design/delivery.md</c> 3.2, aus Anforderung 33 (c)) sagt das Gegenteil — die
/// eigene Angabe stünde davor — und ist als offene Design-Anforderung 34 vermerkt. Gebaut
/// wird nach der Messung, nicht nach der Zeichnung: Wer diese Regel umdreht, dreht sie hier
/// um, mit Verweis auf eine neue Messung oder eine beantwortete Anforderung 34 — nicht auf
/// Verdacht.
/// </para>
/// </summary>
public static class LaunchOptionsComposer
{
    // Listenartig ist bewusst nur diese eine Variable (E-4) — die Tabelle wächst, wenn ein
    // Modul es braucht, nicht auf Verdacht (CLAUDE.md).
    private const string ListVariableName = "WINEDLLOVERRIDES";
    private const char ListSeparator = ';';
    private const string Placeholder = "%command%";

    /// <summary>
    /// Zeichnet den Wert für Steams <c>LaunchOptions</c>-Feld.
    /// </summary>
    /// <param name="userOptions">Die eigene Angabe des Nutzers, wörtlich.</param>
    /// <param name="contributions">
    /// Die Beiträge der Module, in der Reihenfolge, in der die Registry sie sammelt
    /// („zuerst registriert" heißt „steht früher in der Liste", E-9).
    /// </param>
    /// <param name="warnings">Sammelstelle für Warnungen, etwa bei einem Widerspruch (E-6).</param>
    public static string Compose(
        string userOptions,
        IReadOnlyList<LaunchOptionContribution> contributions,
        ICollection<string> warnings)
    {
        // Schritt 1: Ohne Beitrag ist die eigene Angabe fremder Besitz, den wir gar nicht
        // erst anfassen — auch nicht zum Trimmen (E-8). Häufigster Fall überhaupt.
        if (contributions.Count == 0)
        {
            return userOptions;
        }

        // Schritt 2: Die eigene Angabe an ihrer Naht teilen. Case-insensitiv, erste
        // Fundstelle gewinnt (E-3); der Platzhalter selbst wird verbraucht.
        var placeholderIndex = userOptions.IndexOf(Placeholder, StringComparison.OrdinalIgnoreCase);
        string userBefore;
        string userAfter;
        if (placeholderIndex >= 0)
        {
            userBefore = userOptions[..placeholderIndex].Trim();
            userAfter = userOptions[(placeholderIndex + Placeholder.Length)..].Trim();
        }
        else
        {
            // Gemessen (§10): fehlt der Platzhalter, gilt die ganze Angabe als Spielargumente,
            // also hinter %command% — nicht davor, wie die Zeichnung nahelegt.
            userBefore = string.Empty;
            userAfter = userOptions.Trim();
        }

        // Schritt 3: Doppelte über (Kind, Name, Value) fallen weg, erste Fundstelle gewinnt
        // (E-7) — der Modulschlüssel zählt bewusst nicht mit.
        var deduplicated = new List<LaunchOptionContribution>();
        var seen = new HashSet<(LaunchOptionKind Kind, string? Name, string Value)>();
        foreach (var contribution in contributions)
        {
            if (seen.Add((contribution.Kind, contribution.Name, contribution.Value)))
            {
                deduplicated.Add(contribution);
            }
        }

        // Schritt 4 und 5: Umgebungsvariablen zusammenführen bzw. Widersprüche auflösen.
        var environmentContributions = ResolveEnvironmentContributions(deduplicated, warnings);

        // Schritt 6: Zusammensetzen, leere Abschnitte ausgelassen, mit einem Leerzeichen
        // verbunden.
        var wrapperSection = string.Join(
            ' ', deduplicated.Where(c => c.Kind == LaunchOptionKind.Wrapper).Select(c => c.Render()));
        var gameArgumentSection = string.Join(
            ' ', deduplicated.Where(c => c.Kind == LaunchOptionKind.GameArgument).Select(c => c.Render()));

        var sections = new[]
        {
            environmentContributions,
            userBefore,
            wrapperSection,
            Placeholder,
            gameArgumentSection,
            userAfter,
        };

        return string.Join(' ', sections.Where(section => section.Length > 0));
    }

    /// <summary>
    /// Die Gegenrichtung zu <see cref="Compose"/>: Aus Steams Wert wird die eigene Angabe des
    /// Nutzers, indem alles herausgenommen wird, was ein registriertes Modul beitragen könnte
    /// (S2.3).
    ///
    /// <para>
    /// <b>Erkannt, nicht gemerkt</b> (Projektplan M2, Entscheidung 2): Es gibt keinen Vermerk
    /// darüber, was wir zuletzt geschrieben haben. Als unser gilt, was ein Modul beitragen
    /// <i>könnte</i> — auch ein gerade abgeschaltetes. Sonst bliebe der Beitrag eines
    /// abgeschalteten Moduls als „eigene Angabe" stehen und wäre nie wieder zu entfernen. Der
    /// Preis: Wer denselben Wert von Hand setzt, bekommt ihn uns zugerechnet.
    /// </para>
    ///
    /// <para>
    /// <b>Zwei bekannte Lücken</b>, beide bewusst (Spec E-G): Zusammengeführte listenartige
    /// Variablen werden nicht wiedererkannt — <c>Compose</c> macht aus zwei Beiträgen ein Feld,
    /// und keines der beiden <c>Render()</c> trifft es. Und ein Beitrag, dessen <c>Render()</c>
    /// ein Leerzeichen enthält, wird nicht wiedererkannt, weil hier an Leerraum getrennt wird.
    /// Beides fällt an, wenn das erste listenbeitragende Modul entsteht (DLL-Mods); in M2 gibt es
    /// genau einen Beitrag, und er hat beide Eigenschaften nicht. Die Regel „streiche alles mit
    /// unserem Variablennamen" wäre die schlechtere Antwort: Sie schluckte auch den von Hand
    /// gesetzten Wert desselben Namens.
    /// </para>
    /// </summary>
    /// <param name="steamValue">Steams <c>LaunchOptions</c>-Feld, wörtlich.</param>
    /// <param name="allContributions">
    /// Die Beiträge <b>aller registrierten</b> Module für dieses Spiel, ohne jeden Schalter —
    /// <c>ModuleRegistry.AllLaunchOptionsFor</c> (S2.2).
    /// </param>
    public static string ExtractUserOptions(
        string steamValue, IReadOnlyList<LaunchOptionContribution> allContributions)
    {
        // Spiegelbild von Schritt 1 in Compose: Ohne Beitrag haben wir hier nie geschrieben, also
        // fassen wir auch beim Lesen nichts an — nicht einmal zum Trimmen.
        if (allContributions.Count == 0)
        {
            return steamValue;
        }

        var ours = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contribution in allContributions)
        {
            ours.Add(contribution.Render());
        }

        // Gestrichen wird jedes Vorkommen, nicht eines je Beitrag: Bliebe eine zweite Kopie
        // unseres eigenen Werts als „eigene Angabe" stehen, schriebe der nächste Durchgang sie
        // wieder mit hinaus.
        var kept = steamValue
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(field => !ours.Contains(field))
            .ToList();

        // Ein Platzhalter ganz vorn stammt aus unserem eigenen Zeichnen: Fehlt er in der eigenen
        // Angabe, setzt Compose ihn vor sie (gemessen, §10). Ihn stehen zu lassen zeigte dem
        // Nutzer einen Platzhalter, den er nie getippt hat — und Compose setzt ihn ohnehin wieder.
        // Weiter hinten trägt er Bedeutung („gamemoderun %command% -x") und bleibt.
        if (kept.Count > 0
            && string.Equals(kept[0], Placeholder, StringComparison.OrdinalIgnoreCase))
        {
            kept.RemoveAt(0);
        }

        return string.Join(' ', kept);
    }

    // Führt gleichnamige Umgebungsvariablen zusammen (listenartig, E-4/E-5) oder löst
    // Widersprüche auf (E-6) und meldet sie. Liefert die fertige Environment-Sektion.
    private static string ResolveEnvironmentContributions(
        List<LaunchOptionContribution> deduplicated, ICollection<string> warnings)
    {
        var byName = new List<(string Name, List<LaunchOptionContribution> Contributions)>();
        foreach (var contribution in deduplicated)
        {
            if (contribution.Kind != LaunchOptionKind.Environment)
            {
                continue;
            }

            var group = byName.Find(g => g.Name == contribution.Name);
            if (group.Contributions is null)
            {
                group = (contribution.Name!, []);
                byName.Add(group);
            }

            group.Contributions.Add(contribution);
        }

        var rendered = new List<string>();
        foreach (var (name, contributionsForName) in byName)
        {
            if (contributionsForName.Count == 1)
            {
                rendered.Add(contributionsForName[0].Render());
                continue;
            }

            if (string.Equals(name, ListVariableName, StringComparison.Ordinal))
            {
                rendered.Add(MergeListVariable(name, contributionsForName));
                continue;
            }

            // Widerspruch: gleicher Name, verschiedener Wert, nicht listenartig. Der erste
            // gewinnt (E-9), jeder weitere abweichende Wert erzeugt genau eine Warnung, die
            // beide beteiligten Module nennt — nicht eine je verworfenem Beitrag.
            var winner = contributionsForName[0];
            rendered.Add(winner.Render());
            foreach (var loser in contributionsForName.Skip(1))
            {
                if (loser.Value != winner.Value)
                {
                    warnings.Add(
                        $"Conflicting launch option {name}: keeping the value from module "
                        + $"'{winner.ModuleKey}', ignoring the value from module '{loser.ModuleKey}'");
                }
            }
        }

        return string.Join(' ', rendered);
    }

    // Führt die Werte einer listenartigen Umgebungsvariable zusammen (E-4/E-5): je ein
    // umschließendes Anführungszeichenpaar entfernen, in Reihenfolge und ohne Doppelte mit
    // ';' verbinden, das Ergebnis immer neu quoten.
    private static string MergeListVariable(string name, List<LaunchOptionContribution> contributions)
    {
        var parts = new List<string>();
        var seenParts = new HashSet<string>();
        foreach (var contribution in contributions)
        {
            var unquoted = Unquote(contribution.Value);
            foreach (var part in unquoted.Split(ListSeparator))
            {
                if (seenParts.Add(part))
                {
                    parts.Add(part);
                }
            }
        }

        return $"{name}=\"{string.Join(ListSeparator, parts)}\"";
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
}
