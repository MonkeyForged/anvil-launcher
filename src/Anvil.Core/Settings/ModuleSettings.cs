using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Settings;

/// <summary>
/// Der Ort für modul-eigene Einstellungen (S3.3): <c>&lt;Modulordner&gt;/settings.json</c>, ein
/// flaches Wörterbuch aus Zeichenketten. Lesen ist fehlertolerant (Vorgabewerte statt Absturz),
/// Schreiben unteilbar über <see cref="AtomicFile"/> — dieselbe Form wie
/// <see cref="SettingsStore"/>, an dem diese Klasse ihr Vorbild hat.
///
/// <para>
/// <b>Warum flach und nicht getypt je Modul</b> (Spec §3.4): Der Core darf kein Feld
/// <c>SelectedWand</c> kennen — das wäre ein Sonderweg für ein einzelnes Modul. Ein flaches
/// Wörterbuch kostet dieselben Zeilen, kennt keinen Modulnamen und trägt den nächsten Fall, der
/// ohnehin kommt: den Autostart-Schalter aus M5, für den Entscheidung 4 ursprünglich geschrieben
/// wurde.
/// </para>
///
/// <para>
/// <b>Warum sie mit der Datenwurzel wandert</b> (Spec E-6): Was der Core über sich selbst
/// schreibt (Log, Vermerk, Sicherungen, seine eigenen Einstellungen), liegt an festen XDG-Orten
/// und wandert <b>nicht</b> mit (<see cref="ModuleDataFolders"/>, S1.3). Diese Datei hier gehört
/// zu den Daten des Moduls; ihr Inhalt ist ohne die Ablage daneben sinnlos. Sie ist deshalb auch
/// keine zweite Einstellungsdatei des Cores.
/// </para>
///
/// <para>
/// <b>Warum kein Zwischenspeicher</b> (Plan P-4): Gelesen wird bei jedem <see cref="Get"/>,
/// geschrieben bei jedem <see cref="Set"/>. Die Datei hat zwei Einträge; ein Zwischenspeicher
/// kostete mehr Code als er spart und brächte die Frage mit, wann er veraltet.
/// </para>
/// </summary>
public sealed class ModuleSettings
{
    private const string FileName = "settings.json";

    /// <summary>
    /// Dieselben Optionen wie in <see cref="SettingsStore"/>: Eine Einstellungsdatei, die ein
    /// Mensch aufmachen kann, ist im ganzen Projekt so gebaut. Die camelCase-Regel betrifft
    /// dabei Eigenschaftsnamen, nicht Wörterbuch-Schlüssel — die Schlüssel eines Moduls stehen
    /// zeichengenau in der Datei, so wie das Modul sie nennt.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<string> _folder;
    private readonly ILogger? _logger;

    /// <param name="folder">
    /// Der Modulordner, bei jedem Zugriff neu aufgelöst — dieselbe Bauform wie bei der Ablage
    /// und beim Proton-Katalog (<c>architecture.md</c> §4.1): Die Datenwurzel ist eine Einstellung,
    /// die der Nutzer im Betrieb umstellt, und wer den Pfad einmal festhielte, schriebe danach
    /// an den alten Ort. <b>Der Delegat wird nur gefragt, nie angelegt:</b> Lesen legt nichts an
    /// (eine fehlende Datei ergibt Vorgabewerte), und beim Schreiben legt
    /// <see cref="AtomicFile.WriteAllText"/> das Zielverzeichnis ohnehin selbst an. Ein
    /// zusätzliches <c>CreateDirectory</c> hier wäre eine zweite Stelle, die dasselbe tut.
    /// </param>
    /// <param name="logger">Die Kategorie bestimmt der Besitzer; null heißt stumm.</param>
    public ModuleSettings(Func<string> folder, ILogger? logger = null)
    {
        _folder = folder;
        _logger = logger;
    }

    /// <summary>Der gespeicherte Wert, oder null, wenn der Schlüssel nicht in der Datei steht.</summary>
    public string? Get(string key)
        => Load().TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Schreibt einen Wert; <c>null</c> entfernt den Eintrag. Geschrieben wird sofort und
    /// unteilbar, wie der Je-Spiel-Schalter (S1.2).
    /// </summary>
    public void Set(string key, string? value)
    {
        var entries = Load();
        if (value is null)
        {
            entries.Remove(key);
        }
        else
        {
            entries[key] = value;
        }

        AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(entries, JsonOptions));
    }

    private string FilePath => Path.Combine(_folder(), FileName);

    private Dictionary<string, string> Load()
    {
        var path = FilePath;

        // Vor dem ersten Set gibt es die Datei nicht — der Normalfall, keine Meldung wert.
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Eine unlesbare Datei ergibt Vorgabewerte, keinen Absturz — Vorbild
            // SettingsStore.Load. Anders als dort wird die kaputte Datei aber nicht als
            // .invalid gesichert: In den Core-Einstellungen steckt der ganze Zustand der
            // Anwendung, hier stehen zwei Dateinamen — eine Sicherung wäre mehr Aufräumarbeit
            // als Gewinn.
            _logger?.LogWarning(
                exception, "module settings file unreadable, using defaults: {Path}", path);
            return [];
        }
    }
}
