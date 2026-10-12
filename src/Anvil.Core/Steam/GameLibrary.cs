namespace Anvil.Core.Steam;

/// <summary>
/// Die eine Wahrheit über „welche Spiele gibt es" (Vision §3). Hält die aktuelle
/// Momentaufnahme und tauscht sie beim Rescan aus.
///
/// <para>
/// <b>Ein Halter, kein Zwischenspeicher:</b> <see cref="Rescan"/> liest immer wirklich neu.
/// Und bewusst <b>ohne Ereignis</b> — heute löst nur die Library-Seite den Rescan aus und
/// hält das Ergebnis selbst in der Hand. Ein <c>Changed</c> auf Verdacht wäre die Abstraktion,
/// die die Bauprinzipien untersagen; es kommt, wenn ein zweiter Zuhörer da ist.
/// </para>
///
/// <para>
/// <b>Warum ein Delegat und kein <see cref="SteamScanner"/>:</b> dasselbe Muster wie
/// <c>getEnv</c> im Scanner selbst und <c>loadArtwork</c> in der Oberfläche. Die Composition
/// Root reicht <c>scanner.Scan</c>; ein Test reicht seine eigene Momentaufnahme und braucht
/// keinen Steam-Baum.
/// </para>
/// </summary>
public sealed class GameLibrary
{
    private readonly Func<SteamSnapshot> _scan;

    public GameLibrary(Func<SteamSnapshot> scan)
    {
        _scan = scan;
        Current = scan();
    }

    /// <summary>Die zuletzt gelesene Momentaufnahme. Nie null, nie halb.</summary>
    public SteamSnapshot Current { get; private set; }

    /// <summary>Liest die Platte neu und tauscht <see cref="Current"/> aus.</summary>
    public SteamSnapshot Rescan() => Current = _scan();
}
