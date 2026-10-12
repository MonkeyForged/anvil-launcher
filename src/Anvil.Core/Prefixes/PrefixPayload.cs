namespace Anvil.Core.Prefixes;

/// <summary>
/// Was eine Komponente im Prefix <b>beansprucht</b> (S3.2).
///
/// <para>
/// <b>Der Anspruch, nicht die Dateiliste des Pakets</b> (Spec E-5). Ein gebautes .NET-Paket trägt
/// über 1500 Dateien und rund 700 MB; keine Deklaration zählt die auf. Hier steht, welche
/// <i>Bereiche</i> des Prefix zu dieser Komponente gehören — daraus entscheidet der
/// Zusammenführer, ob zwei Komponenten einander überschreiben würden, und S3.5, was zu ernten
/// ist.
/// </para>
///
/// <para>
/// <b>Die Formen sind die von <see cref="PrefixMarks"/></b> (S3.1): Dateipfade relativ zu
/// <c>drive_c/</c> mit <c>/</c> getrennt, Registry-Schlüssel mit <b>einfachen</b> Backslashes
/// relativ zu HKLM. Wer die Merkmale lesen kann, kann die Nutzlast lesen.
/// </para>
/// </summary>
/// <param name="Files">Datei <b>oder Verzeichnis</b>, z. B. <c>windows/Microsoft.NET</c>.</param>
/// <param name="RegistryBranches">Zweige, z. B. <c>Software\Microsoft\.NETFramework</c>.</param>
public sealed record PrefixPayload(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> RegistryBranches)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen.

    /// <summary>Datei <b>oder Verzeichnis</b>, z. B. <c>windows/Microsoft.NET</c>.</summary>
    public IReadOnlyList<string> Files { get; init; } = ValidateFiles(Files);

    /// <summary>Zweige, z. B. <c>Software\Microsoft\.NETFramework</c>.</summary>
    public IReadOnlyList<string> RegistryBranches { get; init; } = ValidateRegistryBranches(RegistryBranches);

    // Ein Pfad mit Backslash oder führendem "/" fällt hier auf, nicht erst in S3.6 im fremden
    // Prefix (P-3).
    private static IReadOnlyList<string> ValidateFiles(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        foreach (var file in files)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(file, nameof(files));

            if (file.StartsWith('/') || file.Contains('\\'))
            {
                throw new ArgumentException(
                    $"File paths must be relative to drive_c/ and use '/' as separator, but got '{file}'.",
                    nameof(files));
            }

            if (file.EndsWith('/'))
            {
                throw new ArgumentException(
                    $"File paths must not end with the separator '/', but got '{file}'.",
                    nameof(files));
            }
        }

        return files;
    }

    // Der Vergleich in Aufgabe 2 hängt daran, dass "Software\Microsoft" und
    // "Software\Microsoft\" nicht als zwei Dinge auftreten.
    private static IReadOnlyList<string> ValidateRegistryBranches(IReadOnlyList<string> registryBranches)
    {
        ArgumentNullException.ThrowIfNull(registryBranches);

        foreach (var branch in registryBranches)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(branch, nameof(registryBranches));

            if (branch.Contains('/') || branch.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Registry branches must be relative to HKLM with '\\' as separator and no root, but got '{branch}'.",
                    nameof(registryBranches));
            }

            if (branch.EndsWith('\\'))
            {
                throw new ArgumentException(
                    $"Registry branches must not end with the separator '\\', but got '{branch}'.",
                    nameof(registryBranches));
            }
        }

        return registryBranches;
    }
}
