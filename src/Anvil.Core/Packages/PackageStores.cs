using Anvil.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Packages;

/// <summary>
/// Die Fabrik der Ablagen (S3.3) — die <b>eine</b> Stelle, an der die Regel aus Entscheidung 9
/// steht: <i>Abhängigkeiten geteilt, Anwendungen beim Modul.</i>
///
/// <para>
/// <b>Warum es sie gibt:</b> Ohne sie stünde diese Regel n-fach in der Composition Root, einmal
/// je Modul, und das zweite Modul der Bridge-Familie schriebe sie falsch ab. Es ist dieselbe
/// Rolle, die <see cref="ModuleDataFolders"/> für „wo ist mein Ordner" spielt — und dieselbe
/// Bauform: Kein Pfad wird festgehalten, jeder Zugriff schlägt die Datenwurzel neu nach.
/// </para>
///
/// <para>
/// <b>Zwei Besitzer, zwei Orte für die Auswahl</b> (Spec §3.4): Die geteilte Ablage gehört dem
/// Core, ihre Auswahl liegt in <see cref="AppSettings.Packages"/>. Die Anwendungs-Ablage gehört
/// dem Modul, ihre Auswahl liegt in <c>&lt;Modulordner&gt;/settings.json</c> (M1, Entscheidung 4).
/// Die <see cref="ModuleSettings"/> dafür baut diese Klasse selbst (Spec §3.3): Sie hat den
/// Modulordner bereits, und eine gereichte Fabrik wäre eine Naht zwischen zwei Klassen, die im
/// selben Aufruf entstehen.
/// </para>
///
/// <para>
/// <b>Den Unterordnernamen nennt das Modul, nicht der Core</b> (Spec §3.3): <c>wand</c> ist ein
/// Wand-Bridge-Wort, und der Core kennt kein konkretes Modul (Vision §3). Gebunden wird er in der
/// Composition Root, dort, wo konkrete Module ohnehin bekannt sind.
/// </para>
///
/// <para>
/// <b>Eine Fabrik, keine Registratur</b> (Spec E-7): Es gibt keine Aufzählung aller Ablagen. Die
/// Frage „welche gibt es" stellt niemand — S3.4 zeichnet die Zeilen, die das Modul nennt, und
/// S3.5 öffnet die Dateien, die es gewählt hat.
/// </para>
/// </summary>
public sealed class PackageStores
{
    /// <summary>
    /// Der Name der geteilten Ablage: <c>&lt;Datenwurzel&gt;/dependencies/</c> — ein Geschwister
    /// der Modulordner, kein Kind eines von ihnen (Spec E-1).
    ///
    /// <para>
    /// Eine Konstante und keine Zeichenkette an drei Stellen (Plan P-6): Der Name ist zugleich
    /// Ordnername und Schlüssel in <see cref="AppSettings.Packages"/> — dieselbe Doppelrolle wie
    /// beim Modulschlüssel (Ordner, Log-Kategorie, Settings-Abschnitt), und darum am selben Ort
    /// deklariert. Dass kein registriertes Modul so heißt, hält ein Test in
    /// <c>Anvil.App.Tests</c> fest; ein solches Modul bekäme diesen Ordner als seinen Modulordner.
    /// </para>
    /// </summary>
    public const string SharedDependencies = "dependencies";

    /// <summary>
    /// Das Präfix des Auswahl-Schlüssels in der modul-eigenen Datei: <c>selected.&lt;Ordner&gt;</c>.
    /// Ein Präfix, weil neben der Auswahl später anderes im selben flachen Wörterbuch steht — der
    /// Autostart-Schalter aus M5 zum Beispiel (Spec §3.4).
    /// </summary>
    private const string SelectionKeyPrefix = "selected.";

    private readonly ModuleDataFolders _folders;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly ILogger? _logger;

    /// <summary>
    /// Die gebauten Anwendungs-Ablagen, je Modulschlüssel eine (Plan P-5). <b>Wovor das schützt:</b>
    /// Zwei Objekte auf derselben Ablage hielten zwei Auswahlen, und die zweite überschriebe die
    /// erste stillschweigend — die Klasse, die das verhindern soll, führte den Fehler selbst ein.
    /// Der Ordnername steht daneben, damit ein zweiter Aufruf mit einem <i>anderen</i> Ordner nicht
    /// klammheimlich die Ablage des ersten zurückgibt.
    ///
    /// <para>
    /// Ein Pfad steckt hier trotzdem nicht: Er lebt im Delegaten der Ablage und wird bei jedem
    /// Zugriff neu aufgelöst.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, (string FolderName, PackageStore Store)> _applications =
        new(StringComparer.Ordinal);

    private readonly PackageStore _dependencies;

    /// <param name="folders">Die Datenwurzel-Naht (S1.3) — Wurzel und Modulordner.</param>
    /// <param name="settings">
    /// Die eine Momentaufnahme im Prozess; hier liegt die Auswahl der geteilten Ablage.
    /// </param>
    /// <param name="saveSettings">
    /// Schreibt die Core-Einstellungen auf die Platte. Als Delegat, weil diese Klasse den
    /// <see cref="SettingsStore"/> sonst kennen müsste, obwohl sie ihn nur anstößt.
    /// </param>
    /// <param name="logger">Kategorie <c>Modules</c>; null heißt stumm.</param>
    public PackageStores(
        ModuleDataFolders folders,
        AppSettings settings,
        Action saveSettings,
        ILogger? logger = null)
    {
        _folders = folders;
        _settings = settings;
        _saveSettings = saveSettings;
        _logger = logger;

        // Einmal gebaut und gehalten, aus demselben Grund wie die Anwendungs-Ablagen oben: Zwei
        // Objekte auf derselben Ablage hielten zwei Auswahlen. Das Bauen rührt die Platte nicht
        // an — der Ordner entsteht erst beim Zugriff.
        _dependencies = new PackageStore(
            // Als Delegat: Die Datenwurzel ist eine Einstellung, die der Nutzer im Betrieb
            // umstellt, und wer den Pfad festhielte, sähe danach in den alten Ordner.
            () => Path.Combine(_folders.Root, SharedDependencies),
            () => _settings.Packages.GetValueOrDefault(SharedDependencies),
            WriteSharedSelection,
            _logger);
    }

    /// <summary>
    /// <c>&lt;Datenwurzel&gt;/dependencies/</c> — geteilt, dem Core gehörend. Alle Module sehen
    /// dieselbe Ablage und dieselbe Auswahl: Zwei Bridges, die je eine eigene Kopie derselben
    /// Installer verlangten, wären zwei Wahrheiten über eine Sache.
    /// </summary>
    public PackageStore Dependencies => _dependencies;

    /// <summary>
    /// <c>&lt;Datenwurzel&gt;/&lt;Modulschlüssel&gt;/&lt;Ordner&gt;/</c> — dem Modul gehörend.
    /// Derselbe Modulschlüssel gibt dasselbe Objekt zurück.
    /// </summary>
    /// <param name="moduleKey">Der Modulschlüssel; er ist zugleich der Name des Modulordners.</param>
    /// <param name="folderName">
    /// Der Unterordner, den das Modul nennt (für Wand Bridge <c>wand</c>) — der Core erfindet ihn
    /// nicht.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Ein zweiter Aufruf mit demselben Schlüssel, aber einem anderen Ordnernamen: ein
    /// Programmierfehler, denn der zweite Aufrufer bekäme sonst klammheimlich die Ablage des
    /// ersten.
    /// </exception>
    public PackageStore ApplicationFor(string moduleKey, string folderName)
    {
        // Ein leerer Schlüssel gäbe die Datenwurzel selbst als Modulordner zurück, ein leerer
        // Ordnername den Modulordner als Ablage — beides Programmierfehler, und beide fallen
        // ohne diese Prüfung erst auf, wenn Dateien am falschen Ort liegen.
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        if (_applications.TryGetValue(moduleKey, out var existing))
        {
            if (!string.Equals(existing.FolderName, folderName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"module '{moduleKey}' already has the package store '{existing.FolderName}', "
                    + $"cannot also have '{folderName}'");
            }

            return existing.Store;
        }

        // Die modul-eigene Einstellungsdatei liegt im Modulordner, nicht in der Ablage darunter:
        // Sie gehört dem Modul, nicht einer seiner Ablagen — und ein Modul mit zwei Ablagen hätte
        // sonst zwei Dateien. Der Ordner wird auch hier als Delegat gereicht.
        var moduleSettings = new ModuleSettings(() => _folders.For(moduleKey), _logger);
        var selectionKey = SelectionKeyPrefix + folderName;

        var store = new PackageStore(
            // Löst zugleich den Ordner-Punkt aus M1/M2 ein: ModuleDataFolders.For legt
            // <Datenwurzel>/<Modulschlüssel>/ beim ersten Zugriff an, die Ablage ihren eigenen
            // Ordner darunter.
            () => Path.Combine(_folders.For(moduleKey), folderName),
            () => moduleSettings.Get(selectionKey),
            value => moduleSettings.Set(selectionKey, value),
            _logger);

        _applications[moduleKey] = (folderName, store);
        _logger?.LogDebug(
            "package store for module {Module}: {Folder}", moduleKey, folderName);
        return store;
    }

    private void WriteSharedSelection(string? name)
    {
        if (name is null)
        {
            _settings.Packages.Remove(SharedDependencies);
        }
        else
        {
            _settings.Packages[SharedDependencies] = name;
        }

        // Sofort schreiben, wie der Je-Spiel-Schalter (S1.2): Die Auswahl soll einen Neustart
        // überleben, auch einen, der nicht über das Menü ging.
        _saveSettings();
    }
}
