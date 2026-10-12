namespace Anvil.Core.Prefixes;

/// <summary>
/// Ein Registry-Wert, dessen Text so beginnen muss (S3.1).
///
/// <para>
/// <b>Präfix, nicht Gleichheit und nicht Größenvergleich</b> (Spec E-5): <c>4.8</c> trifft
/// jede 4.8er-Ausgabe, ohne dass irgendwo ein Versionsobjekt gebaut werden muss. Die Regel
/// steht damit in der Deklaration und nicht im Core — wer eine andere braucht, schreibt sie
/// sich hin.
/// </para>
/// </summary>
/// <param name="Key">Der Schlüssel mit einfachen Backslashes, relativ zu HKLM.</param>
/// <param name="Name">Der Wertname.</param>
/// <param name="ValuePrefix">Womit der Wert beginnen muss.</param>
public sealed record RegistryMark(string Key, string Name, string ValuePrefix);

/// <summary>
/// Woran man erkennt, dass eine Komponente in einem Prefix liegt — die Befund-Hälfte einer
/// Komponentendeklaration (S3.1; die anderen Hälften kommen mit
/// <c>PrefixComponent</c> in S3.2).
///
/// <para>
/// <b>Erkannt, nicht gemerkt</b> (Projektplan M3, Entscheidung 6). Ein fremdvorbereiteter
/// Prefix ist der gemessene Normalfall, nicht der Randfall — deshalb steht hier, wonach man
/// sieht, und nirgends, was wir selbst einmal hingelegt haben.
/// </para>
///
/// <para>
/// <b>Leer heißt „behauptet nichts"</b> und ergibt einen positiven Befund — dieselbe Form wie
/// „leere Liste heißt keine Beiträge" (M2, Entscheidung 5).
/// </para>
/// </summary>
/// <param name="Files">
/// Pfade relativ zu <c>drive_c/</c>, mit <c>/</c> getrennt (P-6), z. B.
/// <c>windows/Microsoft.NET/Framework/v4.0.30319/clr.dll</c>. Geprüft wird die <b>Existenz</b>,
/// nie der Inhalt.
/// </param>
/// <param name="Registry">Werte aus <c>system.reg</c>.</param>
public sealed record PrefixMarks(
    IReadOnlyList<string> Files,
    IReadOnlyList<RegistryMark> Registry)
{
    /// <summary>
    /// Dateien, die da sein <b>und echt</b> sein müssen — also nicht Wines eigene DLL
    /// (<see cref="WineBuiltinDll"/>).
    ///
    /// <para>
    /// <b>Warum ein eigener Merkmalstyp</b> (gemessen 2026-09-17, <c>messungen-anhaengen.md</c> §7.2):
    /// <c>mscoree.dll</c> liegt in <i>jedem</i> Prefix, als Wines Stub — als Existenz-Merkmal
    /// taugt sie deshalb nicht. Ein Paket, das versehentlich Wines Fassung mitbringt, sieht für
    /// <see cref="Files"/> aus wie eine echte Installation. Nur der Blick in die Datei trennt
    /// beides, und ohne ihn galt ein unbrauchbarer Prefix als vorbereitet.
    /// </para>
    ///
    /// <para>Als <c>init</c>-Eigenschaft und nicht als Glied des Konstruktors: Eine Komponente,
    /// die nichts dergleichen fordert, schreibt nichts hin — dieselbe Form wie
    /// <c>PrefixComponent.DllOverrides</c>.</para>
    /// </summary>
    public IReadOnlyList<string> NativeFiles { get; init; } = [];
}
