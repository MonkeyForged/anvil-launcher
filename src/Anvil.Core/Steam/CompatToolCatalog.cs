using Anvil.Core.Vdf;

namespace Anvil.Core.Steam;

/// <summary>
/// Alle installierten Compat-Tools und die Auflösung eines Pfades zu einem davon (Spec §6.3).
///
/// <para>
/// Drei Quellen, weil die Werkzeuge in zwei Bauformen und an drei Orten liegen. Der
/// systemweite Ort ist auf der Owner-Maschine der einzige mit einem externen Werkzeug:
/// <c>proton-cachyos-slr</c> liegt unter <c>/usr/share/steam/</c>, und
/// <c>~/.local/share/Steam/compatibilitytools.d/</c> existiert dort gar nicht. Ohne diesen Ort
/// bliebe die Herkunft namenlos, sobald der Owner umstellt (Spec §2.3).
/// </para>
/// </summary>
public sealed class CompatToolCatalog
{
    /// <summary>Der systemweite Ort — gemessen, nicht geraten (Spec §2.3).</summary>
    public const string SystemWideDirectory = "/usr/share/steam/compatibilitytools.d";

    private readonly List<CompatTool> _tools;
    private readonly List<CompatTool> _selectable;

    private CompatToolCatalog(List<CompatTool> tools)
    {
        // Absteigend nach Pfadlänge: beim Auflösen gewinnt der längste Treffer, sonst
        // schluckt ein Elternordner jeden Unterordner.
        tools.Sort((left, right) => right.Path.Length.CompareTo(left.Path.Length));
        _tools = tools;

        // Die Anzeige-Reihenfolge (Spec S4.3 §4.3): nur wählbare Werkzeuge, nach Name.
        // OrdinalIgnoreCase, nicht Kultur — ICU stuft Bindestriche als ignorierbar ein und
        // schöbe „proton-cachyos" vor „Proton Experimental", abhängig von der Locale.
        _selectable = tools
            .Where(tool => tool.Key is not null)
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Ein Katalog ohne Werkzeuge — für die Momentaufnahme einer Maschine ohne Steam.</summary>
    public static CompatToolCatalog Empty { get; } = new([]);

    /// <summary>
    /// Ein Katalog aus fertigen Werkzeugen, ohne die Platte zu lesen. <b>Für Tests</b> — und für
    /// den einen Fall, in dem ein Aufrufer die Werkzeuge schon hat: <see cref="Build"/> liest
    /// Manifeste und Steams App-Cache, was in einem ViewModel-Test nichts zu suchen hat (dasselbe
    /// Motiv wie der Parameter <c>extraDirectories</c>).
    /// </summary>
    public static CompatToolCatalog Of(params CompatTool[] tools) => new([.. tools]);

    /// <summary>Die gefundenen Werkzeuge, längster Pfad zuerst.</summary>
    public IReadOnlyList<CompatTool> Tools => _tools;

    /// <summary>Die wählbaren Werkzeuge (mit Schlüssel), nach Name — die Liste hinter „Auto" (S4.6).</summary>
    public IReadOnlyList<CompatTool> Selectable => _selectable;

    /// <summary>
    /// Baut den Katalog. <paramref name="tools"/> sind die Werkzeuge aus der Momentaufnahme —
    /// für sie wird <b>nichts neu gelesen</b>, S2.2 hat ihre Manifeste schon verarbeitet.
    /// </summary>
    /// <param name="extraDirectories">
    /// Ersetzt den systemweiten Ort. Nur für Tests — sonst liest jeder Test die echte
    /// CachyOS-Installation mit.
    /// </param>
    public static CompatToolCatalog Build(
        string steamRoot,
        IReadOnlyList<SteamGame> tools,
        ICollection<string> warnings,
        IReadOnlyList<string>? extraDirectories = null)
    {
        // Die Valve-Schlüssel kommen aus Steams App-Cache (S4.3a, E-P). Build liest ohnehin die
        // Platte — der Katalog ist die eine Stelle, die aus Werkzeugen und Dateien Schlüssel macht.
        var valve = ValveCompatTools.Load(steamRoot, warnings);

        var found = new List<CompatTool>();

        foreach (var tool in tools)
        {
            found.Add(new CompatTool(
                tool.Name, tool.InstallDirectory, CompatToolSource.SteamApp,
                valve.KeyFor(tool.AppId)));
        }

        var directories = new List<string> { Path.Combine(steamRoot, "compatibilitytools.d") };
        directories.AddRange(extraDirectories ?? [SystemWideDirectory]);

        foreach (var directory in directories)
        {
            ReadDirectory(directory, found, warnings);
        }

        return new CompatToolCatalog(found);
    }

    /// <summary>
    /// Das Werkzeug, in dessen Ordner <paramref name="path"/> liegt — oder <c>null</c>. Der
    /// längste passende Pfad gewinnt.
    /// </summary>
    public CompatTool? Resolve(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var normalized = path.TrimEnd('/');
        return _tools.Find(tool =>
            normalized.Equals(tool.Path.TrimEnd('/'), StringComparison.Ordinal)
            || normalized.StartsWith(tool.Path.TrimEnd('/') + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Das Werkzeug zu einem Mapping-Schlüssel (Ordinal — Steam schreibt ihn, wie er ist), oder
    /// <c>null</c>: ein Schlüssel ohne Werkzeug heißt „deinstalliert", S4.6 zeigt dann den
    /// Schlüssel selbst.
    /// </summary>
    public CompatTool? FindByKey(string key)
        => _tools.Find(tool => string.Equals(tool.Key, key, StringComparison.Ordinal));

    private static void ReadDirectory(
        string directory, List<CompatTool> found, ICollection<string> warnings)
    {
        // Ein fehlender Ordner ist keine Warnung: auf der Owner-Maschine gibt es den
        // Nutzer-Ordner gar nicht (Spec §2.3, §8).
        if (!Directory.Exists(directory))
        {
            return;
        }

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not list {directory}: {exception.Message}");
            return;
        }

        Array.Sort(entries, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            ReadManifest(entry, found, warnings);
        }
    }

    private static void ReadManifest(
        string folder, List<CompatTool> found, ICollection<string> warnings)
    {
        var manifest = Path.Combine(folder, "compatibilitytool.vdf");
        if (!File.Exists(manifest))
        {
            return;
        }

        VdfDocument document;
        try
        {
            document = VdfDocument.Load(manifest);
        }
        catch (Exception exception) when (exception is VdfParseException or IOException)
        {
            warnings.Add($"Skipped {manifest}: {exception.Message}");
            return;
        }

        var section = document.Root.FindObject("compatibilitytools", "compat_tools");
        if (section is null)
        {
            warnings.Add($"Skipped {manifest}: no \"compat_tools\" section.");
            return;
        }

        foreach (var child in section.Children)
        {
            if (child is not VdfObject tool)
            {
                continue;
            }

            // Fehlt der Anzeigename, dient der Schlüssel — ein namenloses Werkzeug wäre
            // schlechter als ein technisch benanntes.
            var name = (tool.Find("display_name") as VdfString)?.Value;
            var installPath = (tool.Find("install_path") as VdfString)?.Value ?? ".";

            found.Add(new CompatTool(
                string.IsNullOrWhiteSpace(name) ? tool.Key : name,
                Path.GetFullPath(Path.Combine(folder, installPath)),
                CompatToolSource.External,
                tool.Key));
        }
    }
}
