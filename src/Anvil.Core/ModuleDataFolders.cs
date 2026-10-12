using Anvil.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Anvil.Core;

/// <summary>
/// Die Datenwurzel-Naht (S1.3): „gib mir meinen Ordner". Jedes Modul bekommt
/// <c>&lt;Wurzel&gt;/&lt;Modulschlüssel&gt;/</c>, angelegt beim ersten Zugriff.
///
/// <para>
/// <b>Ein Modul bekommt nie diese Klasse und nie einen fertigen Pfad</b>, sondern einen
/// Delegaten <c>Func&lt;string&gt;</c>, gebunden in der Composition Root — dieselbe Bauform
/// wie beim Proton-Katalog (<c>architecture.md</c> §4.1). Die Datenwurzel ist eine Einstellung,
/// die der Nutzer im Betrieb ändern kann; wer den Pfad einmal festhielte, schriebe danach an
/// den alten Ort. Aus demselben Grund merkt sich auch diese Klasse nichts.
/// </para>
///
/// <para>
/// <b>Umstellen verschiebt keine Daten</b> (Vision §4) — eine Entscheidung, kein Versäumnis:
/// Anvil schiebt keine fremden Dateien über Platten. Was am alten Ort liegt, bleibt dort.
/// </para>
///
/// <para>
/// Der Modulschlüssel wird <b>gereicht</b>, nie vom Modul gerechnet, und was der Core über sich
/// selbst schreibt (Log, Vermerk, Sicherungen, Einstellungen) liegt an festen XDG-Orten und
/// wandert nicht mit (<see cref="XdgPaths"/>).
/// </para>
/// </summary>
public sealed class ModuleDataFolders
{
    private readonly AppSettings _settings;
    private readonly string _defaultRoot;
    private readonly ILogger? _logger;

    /// <param name="settings">
    /// Die eine Momentaufnahme im Prozess. Die Einstellungsseite ändert
    /// <see cref="AppSection.DataLocation"/> in genau diesem Objekt — deshalb sieht
    /// <see cref="Root"/> die Änderung beim nächsten Aufruf von selbst (Plan S1.3, E-1).
    /// </param>
    /// <param name="defaultRoot">
    /// Die Wurzel ohne gesetzte Einstellung — im Betrieb
    /// <see cref="XdgPaths.DataDirectory"/>, in Tests ein Temp-Pfad.
    /// </param>
    /// <param name="logger">Kategorie <c>Modules</c>; null heißt stumm.</param>
    public ModuleDataFolders(AppSettings settings, string defaultRoot, ILogger? logger = null)
    {
        _settings = settings;
        _defaultRoot = defaultRoot;
        _logger = logger;
    }

    /// <summary>
    /// Die Datenwurzel, wie sie <i>jetzt</i> gilt: die gewählte Einstellung, sonst die Vorgabe.
    /// Ein leerer String ist keine Wahl, sondern ein Rückstand in der Datei.
    /// </summary>
    public string Root
        => _settings.App.DataLocation is { Length: > 0 } chosen ? chosen : _defaultRoot;

    /// <summary>
    /// Der Ordner dieses Moduls, angelegt, falls er fehlt. Der Pfad gilt für <i>diesen</i>
    /// Aufruf — er wird nicht festgehalten.
    /// </summary>
    public string For(string moduleKey)
    {
        // Ein leerer Schlüssel gäbe die Wurzel selbst zurück und ließe ein Modul in die
        // Ordner aller anderen schreiben — Programmierfehler, früh knallen.
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        var path = Path.Combine(Root, moduleKey);
        if (Directory.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            // Wer keinen Ordner hat, kann nicht arbeiten — die Ausnahme kommt nach oben.
            // Aber unter eigenem Namen: Liegt eine Datei im Weg, meldet das Laufzeitsystem
            // „Could not find a part of the path" (gemessen 2026-09-13) — eine falsche
            // Fährte. Die Log-Zeile steht davor, damit der Pfad auch dann im Log steht,
            // wenn der Aufrufer die Ausnahme schluckt.
            _logger?.LogError(
                exception, "cannot create module data folder: {Path}", path);
            throw new IOException($"cannot create module data folder: {path}", exception);
        }

        _logger?.LogInformation("module data folder created: {Path}", path);
        return path;
    }
}
