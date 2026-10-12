namespace Anvil.Core.Processes;

/// <summary>
/// Ein einzelner Prozessaufruf: Programm, Argumente, und die Umgebung, mit der er laufen soll.
///
/// <para>
/// <b>Die Umgebung ist Teil des Aufrufs, nicht eine Randnotiz</b> — genau deshalb steht sie hier
/// und nicht als optionaler Parameter bei <see cref="IProcessRunner.RunAsync"/>. Was
/// <see cref="ProcessRunner"/> daraus macht, ist die teuerste Regel dieser Story (P-1, dort
/// begründet).
/// </para>
/// </summary>
/// <param name="FileName">Das Programm, absolut oder über <c>PATH</c> auflösbar. Darf nicht leer sein.</param>
/// <param name="Arguments">Die Argumente, in Reihenfolge. Darf nicht <c>null</c> sein, leer ist erlaubt.</param>
/// <param name="Environment">
/// Die vollständige Umgebung des Kindprozesses (siehe <see cref="ProcessRunner"/>). Darf nicht
/// <c>null</c> sein, leer ist erlaubt.
/// </param>
public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    // Records erlauben keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, wie bei PrefixComponent.

    /// <summary>Das Programm, absolut oder über <c>PATH</c> auflösbar.</summary>
    public string FileName { get; init; } = !string.IsNullOrWhiteSpace(FileName)
        ? FileName
        : throw new ArgumentException("File name must not be blank.", nameof(FileName));

    /// <summary>Die Argumente, in Reihenfolge.</summary>
    public IReadOnlyList<string> Arguments { get; init; }
        = Arguments ?? throw new ArgumentNullException(nameof(Arguments));

    /// <summary>Die vollständige Umgebung des Kindprozesses.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = Environment ?? throw new ArgumentNullException(nameof(Environment));

    /// <summary>
    /// Programmname ohne Pfad, plus Argumente — für das Log. Ein Argument, das wie ein Pfad
    /// aussieht (beginnt mit <c>/</c>), wird auf seinen Dateinamen gekürzt: Sonst landet ein
    /// Heimatpfad im Log, und das verstößt gegen CLAUDE.md „Personenbezug". Keine
    /// Pfaderkennung über Regeln oder Existenzprüfung — ein führendes <c>/</c> genügt und bleibt
    /// einfach.
    /// </summary>
    public string RenderShort()
    {
        var program = Path.GetFileName(FileName);
        if (string.IsNullOrEmpty(program))
        {
            // FileName kann nicht leer sein (Prüfung oben), aber "/" allein hat keinen
            // Dateinamen-Anteil — dann bleibt der volle Wert die ehrlichste Antwort.
            program = FileName;
        }

        var shortArguments = Arguments.Select(ShortenIfPath);
        return string.Join(' ', [program, .. shortArguments]);
    }

    private static string ShortenIfPath(string argument)
        => argument.StartsWith('/') ? Path.GetFileName(argument) : argument;
}
