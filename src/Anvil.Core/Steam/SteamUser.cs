namespace Anvil.Core.Steam;

/// <summary>Ein Konto aus <c>config/loginusers.vdf</c> (Spec §6.4).</summary>
/// <param name="SteamId64">Die volle SteamID, wie sie als Schlüssel in der Datei steht.</param>
/// <param name="AccountId">Der Ordnername unter <c>userdata/</c>.</param>
/// <param name="AccountName">Der Anmeldename.</param>
/// <param name="PersonaName">Der angezeigte Name.</param>
/// <param name="AutoLogin">Ob Steam sich hier von selbst anmeldet.</param>
/// <param name="Timestamp">Wann zuletzt angemeldet; <c>null</c>, wenn nichts dasteht.</param>
public sealed record SteamUser(
    ulong SteamId64,
    uint AccountId,
    string AccountName,
    string PersonaName,
    bool AutoLogin,
    DateTimeOffset? Timestamp)
{
    /// <summary>
    /// Steams <c>MostRecent</c>-Marke. Kein Konstruktor-Parameter: sie dient nur der Auswahl
    /// in <see cref="SteamUsers.FindActive"/> und ist für S2.6 ohne Belang.
    /// </summary>
    public bool IsMostRecent { get; init; }
}
