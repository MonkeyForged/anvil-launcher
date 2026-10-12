namespace Anvil.Core.Prefixes;

/// <summary>
/// Ein DllOverride, wie Wine ihn unter <c>HKCU\Software\Wine\DllOverrides</c> in
/// <c>user.reg</c> führt (Spec S3.6 §3.1), z. B. <c>*mscoree</c> = <c>native</c>.
///
/// <para>
/// <b>Ein allgemeiner Wine-Begriff, kein .NET-Wissen.</b> Die Komponente sagt welcher, der Core
/// schreibt (S3.6, <see cref="PrefixPayloadApplier"/>) und prüft (<see cref="PrefixMarkProbe"/>)
/// ihn — dieselbe Arbeitsteilung wie bei Registry-Zweigen.
/// </para>
///
/// <para>
/// <b>Kein <c>"</c>, kein <c>\</c>, kein Zeilenumbruch</b> in Name oder Wert: Beide landen wörtlich
/// in einer erzeugten <c>.reg</c>-Datei. Die Prüfung sitzt hier, damit eine kaputte Deklaration
/// beim Bauen auffällt und nicht erst im Prefix eines Spiels.
/// </para>
/// </summary>
/// <param name="Name">Der Wertname, z. B. <c>*mscoree</c>.</param>
/// <param name="Value">Der Wert, z. B. <c>native</c>. Beim Befund gilt er als Präfix.</param>
public sealed record DllOverride(string Name, string Value)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern (Vorbild: PrefixComponent).

    /// <summary>Der Wertname, z. B. <c>*mscoree</c>.</summary>
    public string Name { get; init; } = Validate(Name, nameof(Name));

    /// <summary>Der Wert, z. B. <c>native</c>. Beim Befund gilt er als Präfix.</summary>
    public string Value { get; init; } = Validate(Value, nameof(Value));

    private static string Validate(string text, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text, parameterName);

        if (text.IndexOfAny(['"', '\\', '\r', '\n']) >= 0)
        {
            throw new ArgumentException(
                $"A DLL override must not contain quotes, backslashes or line breaks, but got '{text}'.",
                parameterName);
        }

        return text;
    }
}
