using Anvil.Core.Vdf;

namespace Anvil.Core.Steam;

/// <summary>
/// Steams erzwungene Proton-Zuordnung je Spiel (Spec S4.3 §4.2), gelesen aus
/// <c>config/config.vdf</c>, Schlüsselpfad
/// <c>InstallConfigStore/Software/Valve/Steam/CompatToolMapping/&lt;appid&gt;/name</c>.
///
/// <para>
/// <b>Einmal je Durchlauf gelesen</b>, wie <see cref="GameLaunchOptions"/>. Auf der
/// Owner-Maschine war das Mapping bis Messung 1 leer — Steam wählt selbst; das Ist hier ist
/// die <i>Vorgabe</i> des Nutzers, nicht das, was läuft (das sagt <c>config_info</c>).
/// </para>
///
/// <para>
/// <b><c>null</c> heißt „kein Eintrag"</b> — und ein leerer <c>name</c> zählt als keiner
/// (E-A): für die Anzeige ist beides <c>Auto</c>, und der Schreiber entfernt bei
/// <c>Auto</c> den ganzen Block. Der globale Default unter dem Schlüssel <c>0</c> wird
/// nicht gelesen (Spec §7).
/// </para>
/// </summary>
public sealed class CompatToolMapping
{
    private readonly Dictionary<uint, string> _byAppId;

    private CompatToolMapping(Dictionary<uint, string> byAppId) => _byAppId = byAppId;

    /// <summary>Nichts gelesen — jede Abfrage liefert <c>null</c>.</summary>
    public static CompatToolMapping Empty { get; } = new([]);

    /// <summary>Wie viele Spiele eine Zuordnung tragen — für die Log-Zeile des Scanners.</summary>
    public int Count => _byAppId.Count;

    /// <summary>
    /// Liest die Datei. Fehler werden zur Warnung; das Ergebnis ist dann leer, nie eine
    /// Ausnahme. Ein fehlender Abschnitt ist kein Fehler (frisches Steam).
    /// </summary>
    public static CompatToolMapping Load(string steamRoot, ICollection<string> warnings)
    {
        var path = SteamConfigPaths.Config(steamRoot);

        VdfDocument document;
        try
        {
            document = VdfDocument.Load(path);
        }
        catch (Exception exception) when (exception is VdfParseException or IOException)
        {
            warnings.Add($"Could not read {path}: {exception.Message}");
            return Empty;
        }

        var mapping = document.Root.FindObject(
            "InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping");
        if (mapping is null)
        {
            return Empty;
        }

        var byAppId = new Dictionary<uint, string>();
        foreach (var child in mapping.Children)
        {
            if (child is not VdfObject entry || !uint.TryParse(entry.Key, out var appId))
            {
                continue;
            }

            if (entry.Find("name") is VdfString { Value.Length: > 0 } name)
            {
                byAppId[appId] = name.Value;
            }
        }

        return new CompatToolMapping(byAppId);
    }

    /// <summary>Der Schlüssel des Spiels, oder <c>null</c> ohne (oder mit leerem) Eintrag.</summary>
    public string? TryGet(uint appId)
        => _byAppId.TryGetValue(appId, out var value) ? value : null;
}
