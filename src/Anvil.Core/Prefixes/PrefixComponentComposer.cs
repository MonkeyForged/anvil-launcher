namespace Anvil.Core.Prefixes;

/// <summary>
/// Macht aus den Forderungen aller für ein Spiel aktiven Module die Liste, die wirklich
/// anzuwenden ist (S3.2). Zustandslos: gleiche Eingabe, gleiche Ausgabe.
///
/// <para>
/// <b>Drei Schritte:</b> nach Schlüssel gruppieren; je Gruppe die geltende Version bestimmen
/// und alle Anforderer sammeln; zuletzt die Ergebnisse gegeneinander prüfen, damit nicht zwei
/// verschiedene Komponenten denselben Bereich des Prefix beanspruchen.
/// </para>
///
/// <para>
/// <b>Einschluss schlägt Reihenfolge</b> — und nur er. Sonst gilt „zuerst registriert gewinnt",
/// wie überall. Nur wenn eine spätere Forderung die Version des Anwärters <i>deklariert</i>
/// einschließt, wechselt der Anwärter: Dann bekommen beide Module, was sie wollten. Der Core
/// vergleicht dabei nie Versionen (Spec E-2), er liest nur die Deklaration.
/// </para>
///
/// <para>
/// <b>Widersprüche werden gemeldet, nicht geraten</b> (Spec E-8). Was herausfällt, fällt ganz
/// heraus und erzeugt genau eine Warnung, die beide beteiligten Module nennt; geworfen wird
/// nie. Dieselbe Haltung wie <see cref="Anvil.Core.Steam.LaunchOptionsComposer"/> bei anderem
/// Gegenstand — bis hin zur linearen Suche über eine Liste von Gruppen: Es geht um eine
/// Handvoll Module, und ein Dictionary verlöre die Reihenfolge, die hier die Entscheidung
/// trägt.
/// </para>
/// </summary>
public static class PrefixComponentComposer
{
    /// <summary>
    /// Die Liste, die für dieses Spiel wirklich anzuwenden ist.
    /// </summary>
    /// <param name="requests">
    /// Die Forderungen der für dieses Spiel aktiven Module, in Registrierungsreihenfolge —
    /// <c>ModuleRegistry.PrefixComponentRequestsFor</c>. Roh und ungeordnet.
    /// </param>
    /// <param name="warnings">Sammelstelle für Widersprüche. Es wird nie geworfen.</param>
    public static IReadOnlyList<RequiredPrefixComponent> Compose(
        IReadOnlyList<PrefixComponentRequest> requests, ICollection<string> warnings)
    {
        var groups = GroupByKey(requests);

        var candidates = new List<RequiredPrefixComponent>();
        foreach (var (_, groupRequests) in groups)
        {
            candidates.Add(ResolveVersion(groupRequests, warnings));
        }

        return ResolveOverlaps(candidates, warnings);
    }

    // Schritt 1: nach Schlüssel gruppieren. Die Reihenfolge der Gruppen ist die des ersten
    // Vorkommens ihres Schlüssels, innerhalb der Gruppe die Registrierungsreihenfolge — beides
    // trägt in Schritt 2 und 3 die Entscheidung, deshalb eine Liste und kein Dictionary.
    private static List<(string Key, List<PrefixComponentRequest> Requests)> GroupByKey(
        IReadOnlyList<PrefixComponentRequest> requests)
    {
        var groups = new List<(string Key, List<PrefixComponentRequest> Requests)>();
        foreach (var request in requests)
        {
            var group = groups.Find(g => g.Key == request.Component.Key);
            if (group.Requests is null)
            {
                group = (request.Component.Key, []);
                groups.Add(group);
            }

            group.Requests.Add(request);
        }

        return groups;
    }

    // Schritt 2: je Gruppe die geltende Version. Der erste Anforderer stellt den Anwärter; jede
    // weitere Forderung fällt in genau einen der vier Fälle. Die Reihenfolge der Fälle ist
    // bindend: Schließen zwei Komponenten sich gegenseitig ein, gewinnt der Anwärter —
    // deterministisch, wie überall.
    private static RequiredPrefixComponent ResolveVersion(
        List<PrefixComponentRequest> groupRequests, ICollection<string> warnings)
    {
        var candidate = groupRequests[0].Component;
        var requestedBy = new List<string> { groupRequests[0].ModuleKey };

        foreach (var request in groupRequests.Skip(1))
        {
            var contender = request.Component;

            if (contender.Version == candidate.Version)
            {
                // Dieselbe Version: genau der Fall, für den es die Anforderer-Liste gibt.
                AddRequester(requestedBy, request.ModuleKey);
            }
            else if (candidate.Includes.Contains(contender.Version))
            {
                // Der Anwärter deckt die geforderte Version mit ab — das Modul bekommt, was es
                // wollte, ohne dass sich etwas ändert.
                AddRequester(requestedBy, request.ModuleKey);
            }
            else if (contender.Includes.Contains(candidate.Version))
            {
                // Einschluss schlägt Reihenfolge: Die neue Komponente deckt auch den bisherigen
                // Anwärter ab, also gewinnt sie — und die bisherigen Anforderer wandern mit,
                // denn auch sie bekommen, was sie wollten.
                candidate = contender;
                AddRequester(requestedBy, request.ModuleKey);
            }
            else
            {
                // Niemand deckt den anderen ab. Der Anwärter bleibt (zuerst registriert
                // gewinnt), die neue Forderung fällt heraus — und ihr Modul wird nicht
                // Anforderer: Es hat nicht bekommen, was es wollte.
                warnings.Add(
                    $"Conflicting prefix component '{candidate.Key}': keeping version "
                    + $"{candidate.Version} requested by module '{requestedBy[0]}', ignoring "
                    + $"version {contender.Version} requested by module '{request.ModuleKey}'");
            }
        }

        return new RequiredPrefixComponent(candidate, requestedBy);
    }

    // Zweimal dasselbe Modul in derselben Gruppe steht trotzdem nur einmal im Vermerk.
    private static void AddRequester(List<string> requestedBy, string moduleKey)
    {
        if (!requestedBy.Contains(moduleKey))
        {
            requestedBy.Add(moduleKey);
        }
    }

    // Schritt 3: Überschneidungen der Nutzlast. Jedes Ergebnis wird gegen die bereits
    // angenommenen geprüft; wer sich mit einem überschneidet, fällt ganz heraus (Spec E-8) —
    // ein halb angewandtes Paket wäre schlimmer als keines.
    private static List<RequiredPrefixComponent> ResolveOverlaps(
        List<RequiredPrefixComponent> candidates, ICollection<string> warnings)
    {
        var accepted = new List<RequiredPrefixComponent>();
        foreach (var candidate in candidates)
        {
            var clash = FindClash(accepted, candidate);
            if (clash is null)
            {
                accepted.Add(candidate);
                continue;
            }

            // Genau eine Warnung je herausgefallener Komponente — die des ersten gefundenen
            // Zusammenstoßes, nicht eine je Pfad.
            var (kept, claim) = clash.Value;
            warnings.Add(
                $"Prefix components '{kept.Component.Key}' and '{candidate.Component.Key}' both "
                + $"claim {claim}: keeping '{kept.Component.Key}' requested by module "
                + $"'{kept.RequestedBy[0]}', ignoring '{candidate.Component.Key}' requested by "
                + $"module '{candidate.RequestedBy[0]}'");
        }

        return accepted;
    }

    // Der erste Zusammenstoß mit einer bereits angenommenen Komponente, samt deren
    // beanspruchtem Pfad für die Warnung. Null heißt: keine Überschneidung.
    private static (RequiredPrefixComponent Kept, string Claim)? FindClash(
        List<RequiredPrefixComponent> accepted, RequiredPrefixComponent candidate)
    {
        foreach (var kept in accepted)
        {
            foreach (var keptFile in kept.Component.Payload.Files)
            {
                foreach (var file in candidate.Component.Payload.Files)
                {
                    if (Overlaps(keptFile, file, '/'))
                    {
                        return (kept, keptFile);
                    }
                }
            }

            foreach (var keptBranch in kept.Component.Payload.RegistryBranches)
            {
                foreach (var branch in candidate.Component.Payload.RegistryBranches)
                {
                    if (Overlaps(keptBranch, branch, '\\'))
                    {
                        return (kept, keptBranch);
                    }
                }
            }
        }

        return null;
    }

    // Zwei Ansprüche stoßen zusammen, wenn sie derselbe Pfad sind oder einer unter dem anderen
    // liegt. Privat, weil es genau einen Aufrufer gibt (P-4); einmal mit '/' für Dateien,
    // einmal mit '\' für Registry-Zweige.
    private static bool Overlaps(string a, string b, char separator)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || IsUnder(a, b, separator)
            || IsUnder(b, a, separator);

    // Die Stelle, an der ein Vergleich still falsch wird: Ohne die Trennzeichenprüfung gälte
    // "windows/Microsoft.NETx" als Kind von "windows/Microsoft.NET". OrdinalIgnoreCase, weil
    // Windows-Pfade und Registry-Schlüssel das so sehen — dieselbe Wahl wie in WineRegistry
    // (S3.1).
    private static bool IsUnder(string parent, string child, char separator)
        => child.Length > parent.Length
            && child[parent.Length] == separator
            && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
}
