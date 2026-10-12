namespace Anvil.Core.Prefixes;

/// <summary>
/// Was der Befund ergeben hat (S3.1).
/// </summary>
/// <param name="Present">Liegen alle Merkmale vor?</param>
/// <param name="Missing">
/// Die Merkmale, die fehlen — englisch und für Menschen lesbar, leer bei
/// <paramref name="Present"/>.
///
/// <para>
/// <b>Warum nicht nur <c>false</c></b> (Spec E-3): S3.4 schuldet dem Nutzer bei einem
/// Fehlbefund <b>einen Satz</b> Begründung, und die Log-Zeile schuldet sie dem Owner heute
/// schon (<c>architecture.md</c> §8). Ohne diese Liste stünde in der Oberfläche „not ready" ohne
/// Grund — der lügende Zustand, der in S2.3a teuer war.
/// </para>
/// </param>
public sealed record PrefixFinding(bool Present, IReadOnlyList<string> Missing);
