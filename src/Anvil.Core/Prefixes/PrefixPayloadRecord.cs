namespace Anvil.Core.Prefixes;

/// <summary>Woher eine Nutzlast im Prefix stammt (Spec S3.6 §3.2).</summary>
public enum PayloadOrigin
{
    /// <summary>Anvil hat sie aus einem Paket angewandt.</summary>
    Anvil,

    /// <summary>Sie lag schon da, als der Befund sie fand — von fremder Hand.</summary>
    Foreign,
}

/// <summary>
/// Der Vermerk einer Nutzlast im Prefix (Spec S3.6 §3.2, Vision Wand Bridge §7): welche
/// Komponente, welche Version, woher, aus welchem Paket, und <b>wer sie wollte</b>.
///
/// <para>
/// <b>Wissen, nie Entscheidungsgrundlage</b> (Projektplan M3, Entscheidungen 6 und 11): Ob
/// angewandt wird, sagt der Befund, nicht dieser Vermerk. Er beantwortet allein „wer wollte
/// das" — die Grundlage, falls je jemand fragt, ob etwas weg darf.
/// </para>
/// </summary>
/// <param name="ComponentKey">Z. B. <c>dotnet</c>.</param>
/// <param name="ComponentVersion">Die Version, deren Befund bestätigt wurde.</param>
/// <param name="Origin">Angewandt oder vorgefunden.</param>
/// <param name="PackageKey">Das angewandte Paket; bei <see cref="PayloadOrigin.Foreign"/> <c>null</c>.</param>
/// <param name="RequestedBy">Alle Module, die sie wollten — nie leer.</param>
/// <param name="RecordedAtUtc">Wann der Vermerk geschrieben wurde.</param>
public sealed record PrefixPayloadRecord(
    string ComponentKey,
    string ComponentVersion,
    PayloadOrigin Origin,
    string? PackageKey,
    IReadOnlyList<string> RequestedBy,
    DateTime RecordedAtUtc)
{
    // Pruefung in Initialisierern, wie bei PrefixComponent. Die Reihenfolge der Eigenschaften
    // ist die der Initialisierung — PackageKey steht nach Origin und darf es deshalb lesen.

    /// <summary>Z. B. <c>dotnet</c>.</summary>
    public string ComponentKey { get; init; } = !string.IsNullOrWhiteSpace(ComponentKey)
        ? ComponentKey
        : throw new ArgumentException("Record component key must not be blank.", nameof(ComponentKey));

    /// <summary>Die Version, deren Befund bestätigt wurde.</summary>
    public string ComponentVersion { get; init; } = !string.IsNullOrWhiteSpace(ComponentVersion)
        ? ComponentVersion
        : throw new ArgumentException("Record component version must not be blank.", nameof(ComponentVersion));

    /// <summary>
    /// Das angewandte Paket. Ein <see cref="PayloadOrigin.Anvil"/>-Vermerk ohne Paket wäre eine
    /// Behauptung ohne Herkunft, ein <see cref="PayloadOrigin.Foreign"/>-Vermerk mit Paket eine
    /// falsche.
    /// </summary>
    public string? PackageKey { get; init; } = ValidatePackageKey(Origin, PackageKey);

    /// <summary>Alle Module, die sie wollten — nie leer.</summary>
    public IReadOnlyList<string> RequestedBy { get; init; } = ValidateRequestedBy(RequestedBy);

    // Die drei Felder gehoeren zusammen: Sie beschreiben ein Anwenden durch Anvil. Ein
    // foreign-Vermerk, der eines davon traegt, ist in sich widerspruechlich — er wird beim Bauen
    // abgelehnt, und PrefixPayloadRecords.Read macht daraus "unbekannt" (Spec S4.1 E-6).
    private readonly string? _toolName;
    private readonly int? _protonMajor;
    private readonly PayloadRollback? _rollback;

    /// <summary>
    /// Der Anzeigename des Werkzeugs, mit dem <b>angewandt</b> wurde — also des Werkzeugs, mit dem
    /// das Spiel startet, nicht des Bauwerkzeugs (Spec S4.1 E-3). S4.2 sagt damit
    /// <c>prepared for Proton 11.0</c>; die Angabe entsteht nur hier und ist danach nicht mehr zu
    /// rekonstruieren.
    /// </summary>
    public string? ToolName
    {
        get => _toolName;
        init => _toolName = OnlyForApplied(value, nameof(ToolName));
    }

    /// <summary>Die Proton-Hauptversion des Prefix beim Anwenden (Spec S4.1 E-4).</summary>
    public int? ProtonMajor
    {
        get => _protonMajor;
        init => _protonMajor = OnlyForApplied(value, nameof(ProtonMajor));
    }

    /// <summary>Das Rueckbau-Protokoll, oder <c>null</c> — bei einem Vermerk aus S3.6 fehlt es.</summary>
    public PayloadRollback? Rollback
    {
        get => _rollback;
        init => _rollback = OnlyForApplied(value, nameof(Rollback));
    }

    private T? OnlyForApplied<T>(T? value, string name)
        => value is null || Origin == PayloadOrigin.Anvil
            ? value
            : throw new ArgumentException($"A foreign payload record must not carry {name}.", name);

    private static string? ValidatePackageKey(PayloadOrigin origin, string? packageKey)
    {
        if (origin == PayloadOrigin.Anvil && string.IsNullOrWhiteSpace(packageKey))
        {
            throw new ArgumentException("A record of an applied payload needs its package key.", nameof(PackageKey));
        }

        if (origin == PayloadOrigin.Foreign && packageKey is not null)
        {
            throw new ArgumentException("A record of a foreign payload must not name a package.", nameof(PackageKey));
        }

        return packageKey;
    }

    // Dieselbe Regel wie RequiredPrefixComponent: Ein Vermerk ohne Anforderer waere ohne Wahrheit.
    private static IReadOnlyList<string> ValidateRequestedBy(IReadOnlyList<string> requestedBy)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);

        if (requestedBy.Count == 0)
        {
            throw new ArgumentException("A record must name at least one requesting module.", nameof(RequestedBy));
        }

        foreach (var moduleKey in requestedBy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey, nameof(RequestedBy));
        }

        return requestedBy;
    }
}
