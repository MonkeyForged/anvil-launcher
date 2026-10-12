namespace Anvil.Core.Prefixes;

/// <summary>
/// Wie ein Spiel zu einem Wine-Prefix steht (S3.1). Drei Werte, und keiner davon rät.
/// </summary>
public enum PrefixState
{
    /// <summary>Der Prefix ist da und bewiesen — <c>pfx/system.reg</c> existiert.</summary>
    Present,

    /// <summary>
    /// Es könnte einen geben, es gibt keinen: Das Spiel war noch nie gestartet. Steam legt
    /// <c>compatdata/&lt;appid&gt;</c> schon beim Installieren an; der Beweis ist
    /// <c>pfx/system.reg</c>, und die entsteht erst beim ersten Lauf (Vision §6.3).
    ///
    /// <para>
    /// <b>Hierunter fällt auch das Spiel mit unbekannter Plattform</b> (Spec E-2). Für jeden
    /// Aufrufer bedeutet beides dasselbe — heute ist nichts zu tun.
    /// <see cref="NotPossible"/> zu melden hieße, etwas zu behaupten, das nicht feststeht.
    /// </para>
    /// </summary>
    Missing,

    /// <summary>
    /// Ein natives Linux-Spiel. Kein Prefix, nie einer, nichts vorzubereiten.
    /// </summary>
    NotPossible,
}
