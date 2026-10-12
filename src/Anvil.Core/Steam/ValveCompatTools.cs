using System.Globalization;

namespace Anvil.Core.Steam;

/// <summary>
/// Die Compat-Tools, die Steam selbst kennt — Mapping-Schlüssel je AppId aus
/// <c>appcache/appinfo.vdf</c>, App 891390 „SteamPlay 2.0 Manifests",
/// <c>appinfo/extended/compat_tools/&lt;schlüssel&gt;/appid</c> (Spec S4.3a §2).
///
/// <para>
/// <b>Warum die Datei:</b> Der Schlüssel (<c>proton_9</c>, <c>proton_experimental</c>) steht in
/// keiner Textdatei, die Anvil liest — nur hier. Steam pflegt die Liste bei jedem Start; eine
/// neue Valve-Version braucht deshalb keinen Anvil-Patch. Die feste Tabelle aus S4.3 (E-B) hat
/// der Owner am 2026-09-07 genau deswegen gekippt.
/// </para>
///
/// <para>
/// <b>Kein Rückfall</b> (Spec S4.3a §4): Ist die Datei weg, abgeschnitten oder in einem
/// fremden Format, gibt es genau eine Warnung und keinen Valve-Schlüssel — externe Werkzeuge
/// bleiben wählbar, Valve-Tools bleiben als Herkunft auflösbar. Eine Tabelle oder eine
/// Namensableitung griffe genau dann, wenn der Leser nichts mehr versteht, und würde raten;
/// ein falscher Schlüssel in <c>config.vdf</c> fällt nicht auf, Steam nimmt still Auto.
/// </para>
///
/// <para>
/// <b>Was einen Schlüssel bekommt</b> (Spec S4.3a §2, §3.2, E-O): ein Werkzeug, das
/// <c>from_oslist</c> <c>"windows"</c> führt — es übersetzt Windows-Spiele nach Linux, genau das,
/// was unter „Compat-Tool wählen" steht. Die Runtimes (<c>from_oslist</c> <c>"linux"</c>) laufen
/// schon nativ und bleiben deshalb ohne Schlüssel, aber im Katalog auflösbar (E-C).
/// <c>unlisted</c> taugt dafür nicht: das Feld ist kein Bool — <c>steamlinuxruntime</c> (1.0)
/// trägt <c>0</c> (Steam listet es für native Linux-Spiele), <c>legacysteamruntime</c>
/// trägt <c>2</c> — „vorhanden" träfe hier nur zufällig.
/// </para>
/// </summary>
public sealed class ValveCompatTools
{
    /// <summary>„SteamPlay 2.0 Manifests" — der Eintrag mit der Liste.</summary>
    public const uint ManifestsAppId = 891390;

    private const string NotSelectable = "Valve's Proton versions cannot be selected";

    private readonly Dictionary<uint, string> _keys;

    private ValveCompatTools(Dictionary<uint, string> keys) => _keys = keys;

    /// <summary>Nichts gelesen — jede Abfrage liefert <c>null</c>.</summary>
    public static ValveCompatTools Empty { get; } = new([]);

    /// <summary>Wie viele Werkzeuge einen Schlüssel tragen.</summary>
    public int Count => _keys.Count;

    /// <summary>
    /// Liest <c>&lt;root&gt;/appcache/appinfo.vdf</c>. Wirft nie: jeder Fehlschlag ist eine
    /// Warnung und liefert <see cref="Empty"/>.
    /// </summary>
    public static ValveCompatTools Load(string steamRoot, ICollection<string> warnings)
    {
        var path = SteamConfigPaths.AppInfo(steamRoot);
        if (!File.Exists(path))
        {
            warnings.Add($"{path} not found — {NotSelectable} until Steam has run once.");
            return Empty;
        }

        AppInfoObject? manifests;
        try
        {
            var data = File.ReadAllBytes(path);

            // Die Version vor dem Lesen: ein fremdes Format verlangt ein Anvil-Update, alles
            // andere nicht — zwei Sätze, weil die Handlung verschieden ist (E-M).
            if (data.Length >= 4 && AppInfoFile.Magic(data) != AppInfoFile.SupportedMagic)
            {
                warnings.Add(
                    $"Could not read {path}: unknown format 0x{AppInfoFile.Magic(data):X8} — "
                    + $"{NotSelectable} until Anvil is updated.");
                return Empty;
            }

            manifests = AppInfoFile.Read(data, ManifestsAppId);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add($"Could not read {path}: {exception.Message} — {NotSelectable}.");
            return Empty;
        }

        if (manifests is null)
        {
            warnings.Add(
                $"{path} has no SteamPlay manifests (app {ManifestsAppId}) — {NotSelectable}.");
            return Empty;
        }

        var tools = manifests.Find("appinfo", "extended", "compat_tools");
        if (tools is null)
        {
            warnings.Add($"{path} lists no compat tools — {NotSelectable}.");
            return Empty;
        }

        var keys = new Dictionary<uint, string>();
        foreach (var tool in tools.Objects)
        {
            // Proton-Wahl heißt: läuft Windows-Spiele unter Linux (from_oslist "windows" →
            // to_oslist "linux"). Die Runtimes sind linux → linux — schon nativ, keine Wahl,
            // aber im Katalog auflösbar (E-C). Nicht über "unlisted" filtern: das Feld ist kein
            // Bool — steamlinuxruntime (1.0) trägt "0" (Steam listet es für native Linux-Spiele),
            // legacysteamruntime trägt "2"; "vorhanden" träfe nur zufällig (E-O).
            if (!string.Equals(
                    tool.Value("from_oslist"), "windows", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Valves Datei, nicht die des Nutzers: ein Kind ohne brauchbare AppId ist keine
            // Warnung, es fällt einfach weg. Erste AppId gewinnt, wie im Scanner.
            if (uint.TryParse(
                    tool.Value("appid"), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var appId))
            {
                keys.TryAdd(appId, tool.Key);
            }
        }

        return new ValveCompatTools(keys);
    }

    /// <summary>Der Schlüssel zur AppId eines Valve-Tools, oder <c>null</c> für alles andere.</summary>
    public string? KeyFor(uint appId)
        => _keys.TryGetValue(appId, out var key) ? key : null;
}
