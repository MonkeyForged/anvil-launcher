using Anvil.Core.Vdf;

namespace Anvil.Core.Steam;

/// <summary>
/// Wer angemeldet ist (Spec §6.4). Gebraucht wird davon genau eines: die
/// <see cref="SteamUser.AccountId"/>, denn unter ihr liegen die Startoptionen.
///
/// <para>
/// <b>Gerechnet, nicht durchlaufen.</b> Auf der Owner-Maschine liegt neben dem numerischen
/// Konto ein Ordner <c>anonymous</c> — wer über <c>userdata/</c> iteriert, findet ihn. Die
/// AccountId ergibt sich aus der SteamID, also wird nichts aufgelistet.
/// </para>
/// </summary>
public static class SteamUsers
{
    /// <summary>
    /// Der Abstand zwischen SteamID64 und Konto-ID. Valve nennt das Steam3-AccountID; die
    /// Zahl ist die SteamID64 des ersten je vergebenen Kontos.
    /// </summary>
    public const ulong AccountIdBase = 76561197960265728;

    /// <summary>
    /// Das aktive Konto, oder <c>null</c> samt Warnung. Die Auswahlregel in dieser
    /// Reihenfolge: einziges Konto, <c>MostRecent</c>, <c>AutoLogin</c>, neuester
    /// <c>Timestamp</c>. Auf der Owner-Maschine greift bereits die erste Stufe; die übrigen
    /// kosten je zwei Zeilen und sind billiger als ein späterer Fehlerbericht.
    /// </summary>
    public static SteamUser? FindActive(string steamRoot, ICollection<string> warnings)
    {
        var path = Path.Combine(steamRoot, "config", "loginusers.vdf");

        VdfDocument document;
        try
        {
            document = VdfDocument.Load(path);
        }
        catch (Exception exception) when (exception is VdfParseException or IOException)
        {
            warnings.Add($"Could not read {path}: {exception.Message}");
            return null;
        }

        var users = new List<SteamUser>();
        foreach (var child in document.Root.FindObject("users")?.Children ?? [])
        {
            if (child is VdfObject account && ulong.TryParse(account.Key, out var steamId))
            {
                users.Add(ToUser(steamId, account));
            }
        }

        if (users.Count == 0)
        {
            warnings.Add($"No Steam account found in {path}.");
            return null;
        }

        if (users.Count == 1)
        {
            return users[0];
        }

        return users.Find(user => user.IsMostRecent)
            ?? users.Find(user => user.AutoLogin)
            ?? users.OrderByDescending(user => user.Timestamp ?? DateTimeOffset.MinValue).First();
    }

    private static SteamUser ToUser(ulong steamId, VdfObject account)
    {
        var timestamp = long.TryParse((account.Find("Timestamp") as VdfString)?.Value, out var raw)
            && raw > 0
                ? DateTimeOffset.FromUnixTimeSeconds(raw)
                : (DateTimeOffset?)null;

        return new SteamUser(
            steamId,
            (uint)(steamId - AccountIdBase),
            (account.Find("AccountName") as VdfString)?.Value ?? string.Empty,
            (account.Find("PersonaName") as VdfString)?.Value ?? string.Empty,
            (account.Find("AutoLogin") as VdfString)?.Value == "1",
            timestamp)
        {
            IsMostRecent = (account.Find("MostRecent") as VdfString)?.Value == "1",
        };
    }
}
