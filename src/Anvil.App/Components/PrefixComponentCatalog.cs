using Anvil.Core.Prefixes;

namespace Anvil.App.Components;

/// <summary>
/// Alle Fassungen, die Anvil kennt — der Gegenstand von S3.5a §3.1.
///
/// <para>
/// <b>Warum es sie mit einem einzigen Eintrag schon gibt:</b> Ohne die Sammlung stünde
/// <see cref="Dotnet48"/>.<see cref="Dotnet48.Component"/> für sich allein, und eine zweite
/// Fassung desselben Schlüssels hätte keinen Ort. Mit der Sammlung kostet sie danach einen
/// Listeneintrag statt einer Änderung an fünf Stellen — und der Vollständigkeitstest über die
/// Quellen (S3.5a, Aufgabe 2) läuft über <b>alle</b> Fassungen, nicht nur über die bekannte.
/// </para>
///
/// <para>
/// <b>Sie liegt in <c>Anvil.App</c>, nie im Core</b> — der Core kennt kein <c>dotnet</c>, nur den
/// <see cref="Anvil.Core.Prefixes.PrefixComponent"/>-Vertrag, mit dem er umgeht.
/// </para>
/// </summary>
public static class PrefixComponentCatalog
{
    /// <summary>
    /// Alle bekannten Fassungen. Die Reihenfolge ist bindend: Sie ist die Katalogreihenfolge,
    /// die <see cref="Anvil.Core.Prefixes.PrefixComponentResolver.Resolve"/> heranzieht, wenn
    /// mehrere Fassungen zur Wahl stehen (S3.5a §3.2).
    /// </summary>
    public static IReadOnlyList<PrefixComponent> All { get; } = [Dotnet48.Component];
}
