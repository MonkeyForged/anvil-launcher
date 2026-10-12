using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anvil.Core.Settings;

/// <summary>
/// Persistierte Einstellungen — <b>eine</b> Datei mit Abschnitten für App, Spiele und Module
/// (Owner-Vorgabe Kickoff). Felder wachsen mit den Stories, die sie brauchen. Unbekannte
/// Abschnitte und Felder werden über <see cref="JsonExtensionDataAttribute"/> erhalten:
/// Ein Build ohne ein Modul darf dessen Daten beim Speichern nicht vernichten.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>App-weite Einstellungen. Nie null — "app": null in der Datei wird zur Vorgabe.</summary>
    public AppSection App
    {
        get => _app;
        set => _app = value ?? new AppSection();
    }

    private AppSection _app = new();

    /// <summary>Einstellungen je Spiel, Schlüssel ist die Steam-AppId.</summary>
    public Dictionary<uint, GameSection> Games
    {
        get => _games;
        set => _games = value ?? [];
    }

    private Dictionary<uint, GameSection> _games = [];

    /// <summary>Einstellungen je Modul, Schlüssel ist der Modul-Key (S1.4).</summary>
    public Dictionary<string, ModuleSection> Modules
    {
        get => _modules;
        set => _modules = value ?? [];
    }

    private Dictionary<string, ModuleSection> _modules = [];

    /// <summary>
    /// Die Auswahl je <b>geteilter</b> Ablage; Schlüssel ist der Ablagenname (S3.3).
    ///
    /// <para>
    /// <b>Warum hier und nicht im Modulordner:</b> Eine Abhängigkeit gehört der Komponente, nicht
    /// einem Modul (Projektplan M3, Entscheidung 9). Läge die Auswahl beim Modul, hätte die
    /// geteilte Ablage je Modul eine eigene — geteilt wäre dann nur der Ordner, nicht die Sache.
    /// </para>
    ///
    /// <para>
    /// Gespeichert wird ein <b>Dateiname</b>, nie ein Pfad (Spec E-3): Ein absoluter Pfad überlebte
    /// weder einen Wechsel der Datenwurzel noch einen Rechnerwechsel — und er trüge einen
    /// Heimatpfad in eine Datei, die jemand in ein Fehlerprotokoll kopiert.
    /// </para>
    /// </summary>
    public Dictionary<string, string> Packages
    {
        get => _packages;
        set => _packages = value ?? [];
    }

    private Dictionary<string, string> _packages = [];

    /// <summary>
    /// Die gewählte Fassung je <b>versionsfreiem</b> Komponenten-Schlüssel, z. B.
    /// <c>dotnet → 4.8.1</c> (Spec S3.5b §3.2).
    ///
    /// <para>
    /// <b>Global, nicht je Modul oder Spiel:</b> Die Fassung ist eine Eigenschaft der Komponente —
    /// dieselbe Haltung wie bei <see cref="Packages"/>. Geschrieben wird sofort über
    /// <see cref="Prefixes.ComponentInventory.Choose"/>; eine Version, die kein Kandidat mehr ist,
    /// bleibt stehen und wird beim Lesen ignoriert.
    /// </para>
    /// </summary>
    public Dictionary<string, string> Components
    {
        get => _components;
        set => _components = value ?? [];
    }

    private Dictionary<string, string> _components = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}

/// <summary>App-Abschnitt: die Core-Karte der Settings-Seite und der Sidebar-Zustand (S1.6).</summary>
public sealed class AppSection
{
    /// <summary>Schreibt und löscht den Autostart-Eintrag (S3.6, <c>AutostartFile</c>).</summary>
    public bool StartOnBoot { get; set; }

    /// <summary>Schließen versteckt statt zu beenden, wenn ein Tray da ist (S3.5).</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Der Collapse-Knopf der Sidebar schreibt ihn (Spec S1.5 E3, S1.6 E8).</summary>
    public bool SidebarExpanded { get; set; } = true;

    /// <summary>null heißt Vorgabe (<see cref="XdgPaths.DataDirectory"/>) und steht nicht in der Datei.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataLocation { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}

/// <summary>Spiel-Abschnitt; Schlüssel ist die Steam-AppId.</summary>
public sealed class GameSection
{
    /// <summary>
    /// Der Stern auf der Karte (S2.5). <c>false</c> steht nicht in der Datei — sonst trüge
    /// eine Bibliothek mit 200 Spielen 200 Zeilen Vorgabewert. Dieselbe Regel wie bei
    /// <see cref="AppSection.DataLocation"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Favorite { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}

/// <summary>Modul-Abschnitt; weitere Felder folgen mit den Modulen selbst.</summary>
public sealed class ModuleSection
{
    /// <summary>Global aktiv (Modules-Seite); null heißt aktiv (Vorgabe).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }

    /// <summary>
    /// Die AppIds der Spiele, für die dieses Modul aktiv ist (S1.2). <c>null</c> oder leer
    /// heißt <b>für keines</b> — ein Bridge-Modul hängt sich nicht ungefragt an jedes Spiel.
    /// Anders als bei <see cref="Enabled"/> ist die Vorgabe also <i>aus</i>.
    /// Geschrieben wird das Feld ausschließlich von <see cref="Modules.ModuleRegistry"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<uint>? Games { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraData { get; set; }
}
