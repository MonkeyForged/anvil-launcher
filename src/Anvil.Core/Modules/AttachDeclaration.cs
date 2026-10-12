namespace Anvil.Core.Modules;

/// <summary>Die drei Ausgaenge der Frage „was soll ich fuer dich starten?" (Spec S4.4 E-5).</summary>
public enum AttachDeclarationState
{
    /// <summary>Dieses Modul haengt nichts an — der Normalfall fuer Core-Module.</summary>
    Nothing,

    /// <summary>Es wuerde, kann aber nicht. <see cref="AttachDeclaration.Reason"/> sagt warum.</summary>
    NotPossible,

    /// <summary>Hier ist das Programm.</summary>
    Program,
}

/// <summary>
/// Die Antwort eines Moduls auf die Frage, was im Container gestartet werden soll (Spec §3.2).
/// Bauform von <see cref="ModuleReadiness"/>: privater Konstruktor, Fabriken fuer die sinnvollen
/// Faelle — ein Datenbuendel mit drei freien Feldern liesse Unsinn baubar.
///
/// <para>
/// <b>Warum drei Ausgaenge und nicht zwei</b> (E-5): „Ich haenge nichts an" und „ich wuerde,
/// kann aber nicht" sind verschiedene Aussagen. Mit zwei Ausgaengen braeuchte der zweite Fall
/// einen leeren Grund — dieselbe Zweideutigkeit, die das Projekt bei
/// <c>RegistryBackup == false</c> schon einmal Arbeit gekostet hat.
/// </para>
/// </summary>
public sealed record AttachDeclaration
{
    private AttachDeclaration(AttachDeclarationState state, AttachProgram? program, string? reason)
    {
        State = state;
        Program = program;
        Reason = reason;
    }

    /// <summary>
    /// Nichts anzuhaengen — eine geteilte Instanz, denn dieser Ausgang traegt keinen Zustand.
    /// </summary>
    public static AttachDeclaration Nothing { get; } = new(AttachDeclarationState.Nothing, null, null);

    /// <summary>
    /// Geht nicht, mit <paramref name="reason"/> als ganzem englischem Satz: Er geht
    /// <b>unveraendert</b> vor den Nutzer, wie <see cref="ModuleReadiness.Reason"/>. Ein leerer
    /// Satz waere eine leere Zeile und wirft deshalb.
    /// </summary>
    public static AttachDeclaration NotPossible(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Attach refusal reason must not be blank.", nameof(reason));
        }

        return new AttachDeclaration(AttachDeclarationState.NotPossible, null, reason);
    }

    /// <summary>
    /// Das Programm, das gestartet werden soll. <b>Hier</b> werden seine Pfade geprueft und
    /// nicht im Record selbst (siehe <see cref="AttachProgram"/>): Ein Aufruf mit leerem Pfad
    /// scheiterte sonst erst im Container, wo ihn niemand sieht.
    /// </summary>
    public static AttachDeclaration Start(AttachProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        if (string.IsNullOrWhiteSpace(program.ExecutablePath))
        {
            throw new ArgumentException(
                "Attach program must name an executable.", nameof(program));
        }

        if (string.IsNullOrWhiteSpace(program.WorkingDirectory))
        {
            throw new ArgumentException(
                "Attach program must name a working directory.", nameof(program));
        }

        return new AttachDeclaration(AttachDeclarationState.Program, program, null);
    }

    /// <summary>Welcher der drei Ausgaenge.</summary>
    public AttachDeclarationState State { get; }

    /// <summary>Das Programm — nur bei <see cref="AttachDeclarationState.Program"/> gesetzt.</summary>
    public AttachProgram? Program { get; }

    /// <summary>Der Grund — nur bei <see cref="AttachDeclarationState.NotPossible"/> gesetzt.</summary>
    public string? Reason { get; }
}

/// <summary>
/// Die Antwort eines Moduls <b>mit seinem Schluessel</b>, wie <c>PrefixComponentRequest</c> die
/// Forderung mit ihrem Anforderer traegt (S3.2). Ohne den Schluessel liesse sich eine Absage
/// nicht zuordnen, sobald zwei Bridges nebeneinander stehen.
/// </summary>
public sealed record ModuleAttachDeclaration(string ModuleKey, AttachDeclaration Declaration);
