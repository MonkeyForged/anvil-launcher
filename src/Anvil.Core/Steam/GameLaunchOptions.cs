using Anvil.Core.Vdf;

namespace Anvil.Core.Steam;

/// <summary>
/// Steams Startoptionen je Spiel (Spec §6.5), gelesen aus
/// <c>userdata/&lt;accountId&gt;/config/localconfig.vdf</c>, Schlüsselpfad
/// <c>UserLocalConfigStore/Software/Valve/Steam/apps/&lt;appid&gt;/LaunchOptions</c>.
///
/// <para>
/// <b>Einmal je Durchlauf gelesen, nicht einmal je Spiel.</b> Die Datei ist auf der
/// Owner-Maschine 124 KB groß und enthält alle Spiele auf einmal.
/// </para>
///
/// <para>
/// <b><c>null</c> heißt „steht nicht da", nie „ist leer".</b> Gemessen haben 15 installierte
/// Apps einen Eintrag, zwei davon leer, und sieben gar keinen. Ab M4 ist das der Unterschied
/// zwischen Anlegen und Ändern.
/// </para>
/// </summary>
public sealed class GameLaunchOptions
{
    private readonly Dictionary<uint, string> _byAppId;

    private GameLaunchOptions(Dictionary<uint, string> byAppId) => _byAppId = byAppId;

    /// <summary>Nichts gelesen — jede Abfrage liefert <c>null</c>. Ohne angemeldetes Konto.</summary>
    public static GameLaunchOptions Empty { get; } = new([]);

    /// <summary>
    /// Liest die Datei. Fehler werden zur Warnung; das Ergebnis ist dann leer, nie eine
    /// Ausnahme.
    /// </summary>
    public static GameLaunchOptions Load(
        string steamRoot, uint accountId, ICollection<string> warnings)
    {
        var path = SteamConfigPaths.LocalConfig(steamRoot, accountId);

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

        // Case-insensitiv dank VdfObject.Find (S2.1) — Steam schreibt "Steam" nicht überall gleich.
        var apps = document.Root.FindObject(
            "UserLocalConfigStore", "Software", "Valve", "Steam", "apps");

        // Ein Konto ohne Spiel-Einträge ist kein Fehler.
        if (apps is null)
        {
            return Empty;
        }

        var byAppId = new Dictionary<uint, string>();
        foreach (var child in apps.Children)
        {
            if (child is not VdfObject app || !uint.TryParse(app.Key, out var appId))
            {
                continue;
            }

            // Fehlt LaunchOptions im Block, gilt das Spiel als „steht nicht da" — der Wert
            // wird gar nicht erst eingetragen, damit TryGet null liefert.
            if (app.Find("LaunchOptions") is VdfString launchOptions)
            {
                byAppId[appId] = launchOptions.Value;
            }
        }

        return new GameLaunchOptions(byAppId);
    }

    /// <summary>
    /// Die Startoptionen des Spiels — <c>null</c>, wenn Steam keinen Eintrag hat;
    /// <c>""</c>, wenn der Eintrag da und leer ist.
    /// </summary>
    public string? TryGet(uint appId)
        => _byAppId.TryGetValue(appId, out var value) ? value : null;
}
