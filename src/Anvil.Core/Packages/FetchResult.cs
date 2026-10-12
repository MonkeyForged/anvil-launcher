namespace Anvil.Core.Packages;

/// <summary>Der Ausgang eines Bezugs (S3.3a, Spec §3.4).</summary>
public enum FetchStatus
{
    /// <summary>Geladen, geprüft, umbenannt — die Datei liegt jetzt unter dem Zielnamen.</summary>
    Placed,

    /// <summary>
    /// Unter dem Zielnamen lag schon eine Datei. Sie gehört dem Nutzer; weder geladen noch
    /// geprüft (E-7).
    /// </summary>
    AlreadyPresent,

    /// <summary>Die errechnete Prüfsumme stimmt nicht mit der erwarteten überein.</summary>
    ChecksumMismatch,

    /// <summary>Der Nutzer hat abgebrochen.</summary>
    Canceled,

    /// <summary>Laden, Schreiben oder Prüfen ist fehlgeschlagen.</summary>
    Failed,

    /// <summary>
    /// Das Netz war nicht erreichbar oder brach weg — auch das Verbindungs-Timeout, das als
    /// <see cref="OperationCanceledException"/> ohne abgebrochenes Token kommt (Spec S3.5b E-9).
    /// Getrennt von <see cref="Failed"/>, weil die Oberfläche dafür einen eigenen Satz zeigt.
    /// </summary>
    NetworkFailed,
}

/// <summary>
/// Das Ergebnis eines Bezugs (<see cref="PackageFetcher.FetchAsync"/>).
///
/// <para>
/// <b>Warum <see cref="FetchStatus.Canceled"/> ein Status ist und keine geworfene
/// <see cref="OperationCanceledException"/></b> (Spec E-9): Der Nutzer hat auf Abbrechen
/// gedrückt, das ist kein Fehler. Die Oberfläche (S3.4) soll den Abbruch über denselben Schalter
/// zeichnen wie jeden anderen Ausgang, statt eigens ein <c>try/catch</c> um den Aufruf zu legen.
/// </para>
/// </summary>
/// <param name="Status">Wie der Bezug ausgegangen ist.</param>
/// <param name="FileName">Der Zielname, unabhängig vom Ausgang.</param>
/// <param name="Message">
/// Nur bei <see cref="FetchStatus.ChecksumMismatch"/>, <see cref="FetchStatus.Failed"/> und
/// <see cref="FetchStatus.NetworkFailed"/> gefüllt. Englisch, ein ganzer Satz ohne Fachjargon —
/// sie geht in S3.4 unverändert vor den Nutzer.
/// </param>
public sealed record FetchResult(FetchStatus Status, string FileName, string? Message);

/// <summary>
/// Das Ergebnis eines Bezugs mehrerer Einträge (<see cref="PackageFetcher.FetchAllAsync"/>,
/// Spec S3.5b §3.5).
/// </summary>
/// <param name="Status">
/// <see cref="FetchStatus.Placed"/>, wenn jeder Eintrag danach vorliegt — auch wenn einzelne
/// schon <see cref="FetchStatus.AlreadyPresent"/> waren. Sonst der Ausgang des ersten Eintrags, der
/// nicht gelang; die Folge endet dort.
/// </param>
/// <param name="FileName">Der Zielname des gescheiterten Eintrags; bei Erfolg null.</param>
/// <param name="Message">Die Meldung des gescheiterten Eintrags, wie bei <see cref="FetchResult"/>.</param>
public sealed record FetchBatchResult(FetchStatus Status, string? FileName, string? Message);
