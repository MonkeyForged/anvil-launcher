using System.Globalization;
using System.Text.Json;

namespace Anvil.App.Modules;

/// <summary>
/// How Wand knows a game: its title and its game id. Both stay text — they travel unchanged into
/// Wand's start address in M7 and are never computed with (spec s6.1 E-9).
/// </summary>
public sealed record WandTitle(string TitleId, string GameId);

public enum WandLookupOutcome
{
    Found,
    NotFound,

    /// <summary>More than one catalog entry carries the app id; none is picked (spec E-8).</summary>
    Ambiguous,
}

/// <param name="EntryCount">How many entries carry the app id — 0, 1, or more when ambiguous.</param>
public sealed record WandLookup(WandLookupOutcome Outcome, WandTitle? Title, int EntryCount);

/// <summary>Either a usable index or the reason there is none.</summary>
public sealed record WandCatalogParse(WandCatalogIndex? Index, string? Problem);

/// <summary>
/// Wand's catalog, reduced to the one question Anvil asks it: which Wand title is this Steam app
/// (story s6.1)?
///
/// <para>
/// <b>Tolerant per entry, strict for the whole:</b> The file is nobody's promise and may change
/// shape at any time (UC-6). An entry that does not look as expected is skipped; a file that
/// yields no Steam entry at all is unusable. That makes a changed format look like "no mapping",
/// never like an error.
/// </para>
/// </summary>
public sealed class WandCatalogIndex
{
    private const string SteamPrefix = "steam:";

    private readonly Dictionary<uint, List<WandTitle>> _bySteamAppId;

    private WandCatalogIndex(Dictionary<uint, List<WandTitle>> bySteamAppId, int steamEntryCount)
    {
        _bySteamAppId = bySteamAppId;
        SteamEntryCount = steamEntryCount;
    }

    /// <summary>How many (app id, title) pairs the catalog holds — the log names it after an update.</summary>
    public int SteamEntryCount { get; }

    public static WandCatalogParse Parse(byte[] content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return new WandCatalogParse(null, "not valid JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Object)
            {
                return new WandCatalogParse(null, "no games object");
            }

            var bySteamAppId = new Dictionary<uint, List<WandTitle>>();
            var count = 0;
            foreach (var game in games.EnumerateObject())
            {
                count += AddEntry(game.Value, bySteamAppId);
            }

            return count == 0
                ? new WandCatalogParse(null, "no Steam entries")
                : new WandCatalogParse(new WandCatalogIndex(bySteamAppId, count), null);
        }
    }

    public WandLookup Find(uint appId)
    {
        if (!_bySteamAppId.TryGetValue(appId, out var titles))
        {
            return new WandLookup(WandLookupOutcome.NotFound, null, 0);
        }

        return titles.Count == 1
            ? new WandLookup(WandLookupOutcome.Found, titles[0], 1)
            : new WandLookup(WandLookupOutcome.Ambiguous, null, titles.Count);
    }

    // Returns how many pairs this entry added.
    private static int AddEntry(JsonElement game, Dictionary<uint, List<WandTitle>> bySteamAppId)
    {
        if (game.ValueKind != JsonValueKind.Object
            || !TryGetString(game, "id", out var gameId)
            || !TryGetString(game, "titleId", out var titleId)
            || !game.TryGetProperty("correlationIds", out var ids)
            || ids.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var title = new WandTitle(titleId, gameId);
        var added = 0;
        foreach (var id in ids.EnumerateArray())
        {
            if (!TryGetSteamAppId(id, out var appId))
            {
                continue;
            }

            if (!bySteamAppId.TryGetValue(appId, out var titles))
            {
                titles = [];
                bySteamAppId[appId] = titles;
            }

            // The same app id listed twice on one game is one mapping, not an ambiguity.
            if (!titles.Contains(title))
            {
                titles.Add(title);
                added++;
            }
        }

        return added;
    }

    private static bool TryGetSteamAppId(JsonElement id, out uint appId)
    {
        appId = 0;
        return id.ValueKind == JsonValueKind.String
            && id.GetString() is { } text
            && text.StartsWith(SteamPrefix, StringComparison.Ordinal)
            && uint.TryParse(text.AsSpan(SteamPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out appId);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = "";
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return value.Length > 0;
    }
}
