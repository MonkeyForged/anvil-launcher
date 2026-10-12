namespace Anvil.Core.Prefixes;

/// <summary>
/// Eine Komponente, die in einem Prefix liegen kann — der Gegenstand von M3 (S3.2).
///
/// <para>
/// <b>Module fordern sie an; sie installieren nichts</b> (Projektplan M3, „Ein Prefix, n
/// Module"). Zwei Module, die dasselbe brauchen, nennen denselben Schlüssel, ohne voneinander zu
/// wissen — deshalb trägt die Komponente <b>keinen Anforderer</b> (Spec E-3): Dieselbe
/// Deklaration muss wörtlich von zweien geteilt werden können.
/// </para>
///
/// <para>
/// <b>Der Schlüssel ist versionsfrei</b> (Spec E-1): <c>dotnet</c>, nicht <c>dotnet48</c>. Sonst
/// wäre 4.0 eine andere Sache als 4.8, und die Regel „derselbe Schlüssel in verschiedenen
/// Versionen" hätte nie einen Fall.
/// </para>
///
/// <para>
/// <b>Das Rezept ist optional</b> (Spec E-1, S3.5): <c>null</c> heißt „nur befundbar, nicht
/// baubar" — ein ehrlicher Zustand für jede Komponente, die im Prefix da sein muss und die Anvil
/// nicht selbst herstellen kann. S3.2 hat dieses Glied absichtlich zurückgestellt; siehe
/// <see cref="Recipe"/>. Der Zusammenführer vergleicht nur Schlüssel, Version und
/// <see cref="Includes"/> und fasst das Rezept nie an.
/// </para>
/// </summary>
/// <param name="Key">Stabil und versionsfrei, z. B. <c>dotnet</c>.</param>
/// <param name="Version">Die Version dieser Deklaration, z. B. <c>4.8</c>.</param>
/// <param name="Marks">Woran man sie im Prefix erkennt (S3.1).</param>
/// <param name="Payload">Was sie beansprucht.</param>
/// <param name="Includes">
/// Welche Versionen <b>desselben Schlüssels</b> diese hier mit abdeckt. <b>Deklariert, nicht
/// gerechnet</b> (Spec E-2): Der Core vergleicht keine Versionen. Für <c>dotnet</c> 4.8 steht hier
/// <c>4.0</c> — unser Paket entsteht, indem erst 4.0 und dann 4.8 in denselben Wegwerf-Prefix
/// installiert werden, es <i>enthält</i> 4.0 also wirklich (Vision §6.2).
/// </param>
public sealed record PrefixComponent(
    string Key,
    string Version,
    PrefixMarks Marks,
    PrefixPayload Payload,
    IReadOnlyList<string> Includes)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen.

    /// <summary>Stabil und versionsfrei, z. B. <c>dotnet</c>.</summary>
    public string Key { get; init; } = !string.IsNullOrWhiteSpace(Key)
        ? Key
        : throw new ArgumentException("Component key must not be blank.", nameof(Key));

    /// <summary>Die Version dieser Deklaration, z. B. <c>4.8</c>.</summary>
    public string Version { get; init; } = !string.IsNullOrWhiteSpace(Version)
        ? Version
        : throw new ArgumentException("Component version must not be blank.", nameof(Version));

    /// <summary>Woran man sie im Prefix erkennt (S3.1).</summary>
    public PrefixMarks Marks { get; init; } = Marks ?? throw new ArgumentNullException(nameof(Marks));

    /// <summary>Was sie beansprucht.</summary>
    public PrefixPayload Payload { get; init; } = Payload ?? throw new ArgumentNullException(nameof(Payload));

    /// <summary>
    /// Welche Versionen <b>desselben Schlüssels</b> diese hier mit abdeckt. <b>Deklariert, nicht
    /// gerechnet</b> (Spec E-2): Der Core vergleicht keine Versionen. Für <c>dotnet</c> 4.8 steht
    /// hier <c>4.0</c> — unser Paket entsteht, indem erst 4.0 und dann 4.8 in denselben
    /// Wegwerf-Prefix installiert werden, es <i>enthält</i> 4.0 also wirklich (Vision §6.2).
    /// </summary>
    public IReadOnlyList<string> Includes { get; init; } = ValidateIncludes(Includes, Version);

    // Eine Komponente, die sich selbst einschließt, macht die Regel in Aufgabe 2 zweideutig.
    private static IReadOnlyList<string> ValidateIncludes(IReadOnlyList<string> includes, string version)
    {
        ArgumentNullException.ThrowIfNull(includes);

        foreach (var include in includes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(include, nameof(includes));

            if (include == version)
            {
                throw new ArgumentException(
                    $"A component must not include its own version '{version}'.",
                    nameof(includes));
            }
        }

        return includes;
    }

    /// <summary>
    /// Das Rezept, mit dem der Core diese Komponente aus einem Wegwerf-Prefix bauen kann, oder
    /// <c>null</c>. <b><c>null</c> heißt „nur befundbar, nicht baubar"</b> (Spec E-1): ein
    /// ehrlicher Zustand für jede Komponente, die im Prefix da sein muss und die Anvil nicht
    /// selbst herstellen kann. Kein Konstruktorparameter, damit jeder vorhandene Aufruf dieses
    /// Records unverändert weiterkompiliert — S3.2 hat dieses Glied ausdrücklich für S3.5
    /// zurückgestellt.
    /// </summary>
    public PrefixRecipe? Recipe { get; init; }

    /// <summary>
    /// Die DllOverrides, ohne die die Komponente im Prefix nichts bewirkt (Spec S3.6 §3.1) — für
    /// .NET 4.8 <c>*mscoree=native</c>, sonst lädt Wine seinen eigenen Ersatz. <b>Teil des
    /// Befunds</b>: <see cref="PrefixMarkProbe.Detect"/> prüft sie in <c>user.reg</c>, und
    /// <see cref="PrefixPayloadApplier"/> schreibt sie. Kein Konstruktorparameter, damit jeder
    /// vorhandene Aufruf unverändert weiterkompiliert — dieselbe Bauform wie <see cref="Recipe"/>.
    /// </summary>
    public IReadOnlyList<DllOverride> DllOverrides { get; init; } = [];

    /// <summary>
    /// Der Anzeigename der Komponente (Spec S3.5b E-4), z. B. <c>.NET Framework</c> — ohne
    /// Version, die steht daneben. Vorgabe ist der Schlüssel, damit jeder vorhandene Aufruf
    /// weiterkompiliert. Englisch, geht unverändert vor den Nutzer (Bedarfseintrag, Bedarfszeile).
    /// </summary>
    public string DisplayName
    {
        get => _displayName ?? Key;
        init => _displayName = !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException("Component display name must not be blank.", nameof(DisplayName));
    }

    private readonly string? _displayName;
}
