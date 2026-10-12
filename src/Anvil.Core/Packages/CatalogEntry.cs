namespace Anvil.Core.Packages;

/// <summary>
/// Ein geprüfter Eintrag aus dem Katalog, den Anvil mitbringt (S3.3a).
///
/// <para>
/// <b>Anvil liefert Adressen und Prüfsummen, keine Dateien</b> (Vision §5.1). Dieser Eintrag ist
/// das Wissen, das ein Anvil-Update transportiert: wo eine geprüfte Fassung liegt, wie groß sie
/// ist und woran man sie nach dem Laden wiedererkennt.
/// </para>
///
/// <para>
/// <b>Mitgeliefert statt abgerufen</b> (Entscheidung 12): Der Vorgänger zog Adresse und Datei aus
/// zwei fremden Stellen, jede für sich änderbar. Mitgeliefert heißt: Was Anvil anbietet, hat
/// jemand geprüft.
/// </para>
///
/// <para>
/// <b><see cref="AddressOrigin"/> ist kein Papierkram.</b> Bei WeMod 11.6.0 fallen Datei und
/// Adresse auseinander — die Datei kam über TLS vom Hersteller, die Adresse von einem Dritten,
/// weil der Hersteller nur die jeweils neueste Fassung veröffentlicht. Ohne diese Angabe lässt
/// sich später nicht mehr unterscheiden, welchem Eintrag man wie weit traut.
/// </para>
/// </summary>
/// <param name="FileName">Der Name, unter dem die geladene Datei in der Ablage landet.</param>
/// <param name="DisplayName">Was in der Auswahlzeile steht, z. B. <c>Wand 11.6.0</c> (S3.4).</param>
/// <param name="Address">Die Adresse, von der geladen wird. Immer <c>https</c>.</param>
/// <param name="Sha256">Die erwartete Prüfsumme, 64 kleine Hexzeichen.</param>
/// <param name="SizeBytes">Die Gesamtgröße in Bytes, für den Fortschrittsbalken.</param>
/// <param name="AddressOrigin">Woher die Adresse stammt — Fließtext, englisch.</param>
public sealed record CatalogEntry(
    string FileName,
    string DisplayName,
    Uri Address,
    string Sha256,
    long SizeBytes,
    string AddressOrigin)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen (Vorbild: PrefixComponent).

    /// <summary>Der Name, unter dem die geladene Datei in der Ablage landet.</summary>
    public string FileName { get; init; } = ValidateFileName(FileName);

    /// <summary>Was in der Auswahlzeile steht, z. B. <c>Wand 11.6.0</c> (S3.4).</summary>
    public string DisplayName { get; init; } = !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : throw new ArgumentException("Catalog entry display name must not be blank.", nameof(DisplayName));

    /// <summary>Die Adresse, von der geladen wird. Immer <c>https</c>.</summary>
    public Uri Address { get; init; } = ValidateAddress(Address);

    /// <summary>Die erwartete Prüfsumme, 64 kleine Hexzeichen.</summary>
    public string Sha256 { get; init; } = ValidateSha256(Sha256);

    /// <summary>Die Gesamtgröße in Bytes, für den Fortschrittsbalken.</summary>
    public long SizeBytes { get; init; } = SizeBytes > 0
        ? SizeBytes
        : throw new ArgumentException("Catalog entry size must be greater than zero.", nameof(SizeBytes));

    /// <summary>Woher die Adresse stammt — Fließtext, englisch.</summary>
    public string AddressOrigin { get; init; } = !string.IsNullOrWhiteSpace(AddressOrigin)
        ? AddressOrigin
        : throw new ArgumentException("Catalog entry address origin must not be blank.", nameof(AddressOrigin));

    // Der wichtigste Wächter: Ein Name mit '/' oder '..' schriebe die geladene Datei außerhalb
    // der Ablage. Zusätzlich verboten: ein führender Punkt, sonst wäre die Datei sofort
    // unsichtbar, weil PackageStore.List() Punktdateien übergeht (S3.3).
    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Catalog entry file name must not be blank.", nameof(fileName));
        }

        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException(
                $"Catalog entry file name '{fileName}' must not contain a path.", nameof(fileName));
        }

        if (fileName.StartsWith('.'))
        {
            throw new ArgumentException(
                $"Catalog entry file name '{fileName}' must not start with a dot.", nameof(fileName));
        }

        return fileName;
    }

    // Eine Adresse ohne TLS trägt die Echtheit nicht — die Prüfsumme beweist nur, dass die
    // Datei die erwartete ist, nicht dass sie vom Hersteller kommt (vendor/README.md, „Herkunft").
    private static Uri ValidateAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Catalog entry address '{address}' must use https.", nameof(address));
        }

        return address;
    }

    // Kleinschreibung erzwungen, damit der Vergleich beim Bezug zeichengenau sein kann, ohne
    // IgnoreCase, das einen Unterschied verstecken könnte, den es nicht gibt (Plan P-4).
    private static string ValidateSha256(string sha256)
    {
        if (sha256 is not { Length: 64 } || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException(
                "Catalog entry checksum must be exactly 64 lowercase hex characters.", nameof(sha256));
        }

        return sha256;
    }
}
