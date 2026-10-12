using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>Wie der Vermerk zu dem Proton steht, unter dem das Spiel jetzt läuft (Spec S4.2 §3.1).</summary>
public enum PayloadStandingKind
{
    /// <summary>Kein Vermerk, oder er ist unlesbar. Niemand handelt.</summary>
    None,

    /// <summary>Der Vermerk nennt dieselbe Hauptversion. Niemand handelt.</summary>
    Current,

    /// <summary>Der Prefix wurde <b>heraufgestuft</b> — zurückbauen und neu vorbereiten.</summary>
    Stale,

    /// <summary>Der Prefix wurde <b>zurückgestuft</b> — nicht handeln, sagen (Projektplan M4, Entscheidung 5).</summary>
    MovedBack,

    /// <summary>
    /// Ein Vermerk ohne Hauptversion (aus S3.6) oder von fremder Hand. <b>Unbekannt, nicht geraten</b>
    /// — dieselbe Haltung wie <see cref="PrefixReport.ProtonMajor"/>.
    /// </summary>
    Unknown,
}

/// <summary>Das Urteil samt dem, was die Warnung beim Zurückstufen daraus zeigt (Spec S4.2 §3.6).</summary>
/// <param name="Kind">Welcher der fünf Fälle.</param>
/// <param name="PreparedToolName">Der Anzeigename aus dem Vermerk, z. B. <c>Proton 11.0</c>; <c>null</c>, wenn keiner drinsteht.</param>
/// <param name="PreparedMajor">Die Hauptversion aus dem Vermerk.</param>
/// <param name="Revertable">Ein Protokoll in Stufe <see cref="RollbackStage.Applied"/> liegt vor.</param>
public sealed record PayloadStanding(
    PayloadStandingKind Kind, string? PreparedToolName, int? PreparedMajor, bool Revertable);

/// <summary>
/// Beantwortet für <b>eine</b> Komponente eines Prefix, ob ihr Vermerk zur jetzigen
/// Proton-Hauptversion passt (Spec S4.2 §3.1).
///
/// <para>
/// <b>Eine Antwort für drei Frager</b> (<c>architecture.md</c> §8): Der Vorbereiter entscheidet danach,
/// ob zurückgebaut wird, der Rückbau, ob er arbeitet, und die Modulkarte, ob sie warnt. Drei eigene
/// Auslegungen derselben Datei wären drei Gelegenheiten, verschieden zu antworten.
/// </para>
///
/// <para>
/// <b>Liest nur und wirft nie</b> — wie <see cref="PrefixPayloadRecords.Read"/>, aus demselben Grund:
/// „unbekannt" ist eine ehrliche Antwort und kein Grund für eine Ausnahme.
/// </para>
/// </summary>
public static class PrefixPayloadStanding
{
    /// <param name="prefixPath">Der <c>pfx</c>-Ordner.</param>
    /// <param name="componentKey">Z. B. <c>dotnet</c>.</param>
    /// <param name="currentMajor">Die Hauptversion des Prefix <b>jetzt</b> (<see cref="PrefixReport.ProtonMajor"/>).</param>
    /// <param name="logger"><c>null</c> heißt stumm.</param>
    public static PayloadStanding Of(
        string prefixPath, string componentKey, int currentMajor, ILogger? logger = null)
    {
        var record = PrefixPayloadRecords.Read(prefixPath, componentKey, logger);
        if (record is null)
        {
            return new PayloadStanding(PayloadStandingKind.None, null, null, Revertable: false);
        }

        // Ein fremder Vermerk trägt weder Werkzeugnamen noch Hauptversion (S4.1: die drei Felder
        // gehören zusammen) — beide Fälle enden deshalb im selben Zweig.
        if (record.Origin != PayloadOrigin.Anvil || record.ProtonMajor is not { } major)
        {
            return new PayloadStanding(
                PayloadStandingKind.Unknown, record.ToolName, record.ProtonMajor, Revertable: false);
        }

        var kind = major == currentMajor
            ? PayloadStandingKind.Current
            : major < currentMajor
                ? PayloadStandingKind.Stale
                : PayloadStandingKind.MovedBack;

        // Nur Stufe `applied` trägt die Prüfsummen, und ohne sie ist die Regel aus Versuch D
        // nicht anwendbar (Spec S4.1 §3.1).
        return new PayloadStanding(
            kind, record.ToolName, major, record.Rollback is { Stage: RollbackStage.Applied });
    }
}
