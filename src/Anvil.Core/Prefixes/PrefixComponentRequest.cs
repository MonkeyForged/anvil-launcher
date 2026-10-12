namespace Anvil.Core.Prefixes;

/// <summary>
/// Ein Modul fordert eine Komponente (S3.2). <b>Wer</b> und <b>was</b>, sonst nichts.
///
/// <para>
/// <b>Den Modulschlüssel setzt die Registry daneben, nicht das Modul selbst</b> (Spec E-3) —
/// anders als bei <see cref="Anvil.Core.Steam.LaunchOptionContribution"/>. Ein Modul kann seinen
/// eigenen Schlüssel nur falsch angeben, nicht richtiger; und die Komponente bleibt teilbar.
/// </para>
/// </summary>
/// <param name="ModuleKey">Wer fordert — <see cref="Anvil.Core.Modules.IModule.Key"/>.</param>
/// <param name="Component">Was gefordert wird.</param>
public sealed record PrefixComponentRequest(string ModuleKey, PrefixComponent Component)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen.

    /// <summary>Wer fordert — <see cref="Anvil.Core.Modules.IModule.Key"/>.</summary>
    public string ModuleKey { get; init; } = !string.IsNullOrWhiteSpace(ModuleKey)
        ? ModuleKey
        : throw new ArgumentException("Module key must not be blank.", nameof(ModuleKey));

    /// <summary>Was gefordert wird.</summary>
    public PrefixComponent Component { get; init; } = Component ?? throw new ArgumentNullException(nameof(Component));
}

/// <summary>
/// Eine Komponente, die für dieses Spiel wirklich anzuwenden ist — samt <b>allen</b>, die sie
/// gefordert haben (S3.2).
///
/// <para>
/// <b><see cref="RequestedBy"/> ist Wissen, keine Referenzzählung</b> (Projektplan M3,
/// Entscheidung 11). Es wird nie <b>einzeln</b> zurückgebaut, also stellt sich die Frage „darf ich
/// das wegnehmen" nie; die Liste beantwortet allein, <i>wer das hier wollte</i>, und wandert in
/// S3.6 in den Vermerk im Prefix (Vision §7). Den Rückbau beim Wechsel der Proton-Hauptversion
/// (Projektplan M4) berührt das nicht: Er nimmt die <b>ganze</b> Nutzlast und baut aus den
/// geltenden Forderungen neu auf, statt einen einzelnen Anforderer abzuzählen.
/// </para>
///
/// <para>
/// <b>Wer mit seiner Forderung herausgefallen ist, steht nicht darin.</b> Er hat nicht bekommen,
/// was er wollte — ihn zu nennen wäre eine Unwahrheit im Vermerk.
/// </para>
/// </summary>
/// <param name="Component">Die anzuwendende Komponente.</param>
/// <param name="RequestedBy">Alle Module, die sie (oder eine eingeschlossene Version) gefordert haben.</param>
public sealed record RequiredPrefixComponent(
    PrefixComponent Component,
    IReadOnlyList<string> RequestedBy)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen.

    /// <summary>Die anzuwendende Komponente.</summary>
    public PrefixComponent Component { get; init; } = Component ?? throw new ArgumentNullException(nameof(Component));

    /// <summary>Alle Module, die sie (oder eine eingeschlossene Version) gefordert haben.</summary>
    public IReadOnlyList<string> RequestedBy { get; init; } = ValidateRequestedBy(RequestedBy);

    // Wer mit seiner Forderung herausgefallen ist, steht nicht darin — aber wer drinsteht, muss
    // wirklich jemand sein: eine leere Liste oder ein leerer Eintrag wäre ein Vermerk ohne Wahrheit.
    private static IReadOnlyList<string> ValidateRequestedBy(IReadOnlyList<string> requestedBy)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);

        if (requestedBy.Count == 0)
        {
            throw new ArgumentException(
                "A required component must be requested by at least one module.",
                nameof(requestedBy));
        }

        foreach (var moduleKey in requestedBy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey, nameof(requestedBy));
        }

        return requestedBy;
    }
}
