namespace Anvil.Core.Prefixes;

/// <summary>
/// Baut den Schlüssel eines Pakets im Paketspeicher (S3.5, Spec §3.5, Projektplan M3,
/// Entscheidung 8): <c>dotnet-4.8-proton-11</c>.
///
/// <para>
/// <b>Der vordere Teil steht seit S3.2 fest</b>: Komponentenschlüssel und -version identifizieren
/// die Nutzlast. <b>Der hintere Teil hängt an der noch offenen Messung</b>: Ob ein Paket über
/// Proton-Hauptversionen hinweg gilt, ist ungemessen (Spec §7). Bis dahin trägt der Schlüssel den
/// Major — sonst teilten sich zwei Prefix-Aufbauten, die vielleicht nicht zueinander passen, ein
/// Paket. Fällt die Messung positiv aus, fällt <c>-proton-&lt;major&gt;</c> heraus, und das ist
/// eine Zeile, keine Story.
/// </para>
/// </summary>
public static class PrefixPackageKey
{
    /// <summary>Ergibt z. B. <c>dotnet-4.8-proton-11</c> für <c>("dotnet", "4.8", 11)</c>.</summary>
    public static string For(string componentKey, string componentVersion, int protonMajor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(componentVersion);
        return $"{componentKey}-{componentVersion}-proton-{protonMajor}";
    }
}

/// <summary>
/// Was beim Bauen eines Pakets entstand (S3.5, Spec §3.5) — geschrieben als
/// <c>package.json</c>, gelesen beim Anwenden (S3.6) und beim Nachvollziehen eines Baus im Log.
///
/// <para>
/// <b>Keine Zahl ohne Messung</b> (Design-Antwort 33): <see cref="FileCount"/> und
/// <see cref="TotalBytes"/> sind gezählt, nicht geschätzt — der Bauer trägt hier ein, was die
/// Ernte tatsächlich fand, nicht was die Nutzlast-Deklaration erwarten ließ.
/// </para>
///
/// <para>
/// <b>Kein Vermerk über die Installer</b> (E-10): Weder Name noch Prüfsumme der Installer stehen
/// hier. Ob ein Paket taugt, sagt der Befund im Ziel-Prefix (S3.6), nicht seine Herkunft — wer die
/// Herkunft doch braucht, findet sie im Log des Baus.
/// </para>
/// </summary>
/// <param name="Key">Derselbe Schlüssel wie der Ordnername, aus <see cref="PrefixPackageKey"/>.</param>
/// <param name="ComponentKey">Der Schlüssel der gebauten Komponente, z. B. <c>dotnet</c>.</param>
/// <param name="ComponentVersion">Die Version, z. B. <c>4.8</c>.</param>
/// <param name="ProtonMajor">Die Proton-Hauptversion, mit der gebaut wurde.</param>
/// <param name="BuiltAtUtc">Wann der Bau abgeschlossen wurde, in UTC.</param>
/// <param name="ToolName">Name des Compat-Tools, mit dem gebaut wurde, z. B. <c>Proton 11.0</c>.</param>
/// <param name="FileCount">Zahl der geernteten Dateien, gezählt.</param>
/// <param name="TotalBytes">Summe ihrer Größen in Byte, gezählt.</param>
/// <param name="RegistryBranches">
/// Die Registry-Zweige, die <b>tatsächlich</b> exportiert wurden — ein Zweig, der im
/// Wegwerf-Prefix fehlte, steht hier nicht (Spec §3.7).
/// </param>
public sealed record PrefixPackageManifest(
    string Key,
    string ComponentKey,
    string ComponentVersion,
    int ProtonMajor,
    DateTime BuiltAtUtc,
    string ToolName,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> RegistryBranches)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen (Vorbild: PrefixComponent).

    /// <summary>Derselbe Schlüssel wie der Ordnername, aus <see cref="PrefixPackageKey"/>.</summary>
    public string Key { get; init; } = !string.IsNullOrWhiteSpace(Key)
        ? Key
        : throw new ArgumentException("Package manifest key must not be blank.", nameof(Key));

    /// <summary>Der Schlüssel der gebauten Komponente, z. B. <c>dotnet</c>.</summary>
    public string ComponentKey { get; init; } = !string.IsNullOrWhiteSpace(ComponentKey)
        ? ComponentKey
        : throw new ArgumentException(
            "Package manifest component key must not be blank.", nameof(ComponentKey));

    /// <summary>Die Version, z. B. <c>4.8</c>.</summary>
    public string ComponentVersion { get; init; } = !string.IsNullOrWhiteSpace(ComponentVersion)
        ? ComponentVersion
        : throw new ArgumentException(
            "Package manifest component version must not be blank.", nameof(ComponentVersion));

    /// <summary>Name des Compat-Tools, mit dem gebaut wurde, z. B. <c>Proton 11.0</c>.</summary>
    public string ToolName { get; init; } = !string.IsNullOrWhiteSpace(ToolName)
        ? ToolName
        : throw new ArgumentException("Package manifest tool name must not be blank.", nameof(ToolName));

    /// <summary>Zahl der geernteten Dateien, gezählt.</summary>
    public int FileCount { get; init; } = FileCount >= 0
        ? FileCount
        : throw new ArgumentOutOfRangeException(
            nameof(FileCount), FileCount, "File count must not be negative.");

    /// <summary>Summe ihrer Größen in Byte, gezählt.</summary>
    public long TotalBytes { get; init; } = TotalBytes >= 0
        ? TotalBytes
        : throw new ArgumentOutOfRangeException(
            nameof(TotalBytes), TotalBytes, "Total bytes must not be negative.");

    /// <summary>Die Registry-Zweige, die tatsächlich exportiert wurden.</summary>
    public IReadOnlyList<string> RegistryBranches { get; init; } =
        RegistryBranches ?? throw new ArgumentNullException(nameof(RegistryBranches));
}

/// <summary>
/// Ein Paket, wie es im Speicher liegt (S3.5, Spec §3.5): der Ordner unter dem Schlüssel und das
/// Manifest, das <see cref="PrefixPackageStore.Find"/> gelesen hat.
///
/// <para>
/// Trägt zugleich die Namen der drei Teile eines Pakets (siehe Spec §3.5-Diagramm) — deklariert
/// hier, weil <see cref="PrefixPackage"/> der Gegenstand ist, dessen Aufbau sie beschreiben, und
/// von <see cref="PrefixPackageStore"/> beim Bauen und Lesen wiederverwendet, damit der Name
/// <c>files</c> nicht zweimal im Repo steht.
/// </para>
/// </summary>
/// <param name="Key">Der Schlüssel, identisch mit dem Ordnernamen.</param>
/// <param name="Path">Der Ordner des Pakets, absolut.</param>
/// <param name="Manifest">Das gelesene Manifest.</param>
public sealed record PrefixPackage(string Key, string Path, PrefixPackageManifest Manifest)
{
    /// <summary>Der Unterordner der geernteten Nutzlast, Pfade relativ zu <c>drive_c/</c>.</summary>
    public const string FilesFolderName = "files";

    /// <summary>Der Registry-Ausschnitt, UTF-16LE mit BOM.</summary>
    public const string RegistryFileName = "registry.reg";

    /// <summary>Das Manifest, als <c>System.Text.Json</c> mit <c>WriteIndented</c>.</summary>
    public const string ManifestFileName = "package.json";

    /// <summary>Die geerntete Nutzlast — <c>&lt;Paket&gt;/files/</c>.</summary>
    public string FilesPath => System.IO.Path.Combine(Path, FilesFolderName);

    /// <summary>Der Registry-Ausschnitt — <c>&lt;Paket&gt;/registry.reg</c>.</summary>
    public string RegistryFilePath => System.IO.Path.Combine(Path, RegistryFileName);
}
