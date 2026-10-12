namespace Anvil.Core.Prefixes;

/// <summary>
/// Sieht im Prefix nach, ob die Merkmale einer Komponente vorliegen (S3.1).
///
/// <para>
/// <b>Der Core sieht nach, das Modul sagt wonach.</b> Diese Klasse kennt <c>dotnet48</c>
/// nicht und darf es nie kennen — ein Prefix gehört einem Spiel, nicht einem Modul
/// (Vision §4.3).
/// </para>
///
/// <para><b>Es wird ausschließlich gelesen.</b> Anwenden ist <c>PrefixPayloadApplier</c> (S3.6).</para>
/// </summary>
public static class PrefixMarkProbe
{
    /// <summary>
    /// Der Schlüssel der DllOverrides in <c>user.reg</c>, relativ zu HKCU (Spec S3.6 §3.1).
    /// Öffentlich, weil der Anwender denselben Schlüssel schreibt, den der Befund liest.
    /// </summary>
    public const string DllOverridesKey = @"Software\Wine\DllOverrides";

    /// <param name="prefixPath">Der <c>pfx</c>-Ordner aus <see cref="PrefixReport"/>.</param>
    /// <param name="component">
    /// Die Komponente — ihre Merkmale <b>und</b> ihre DllOverrides (Spec S3.6 §3.1). Es gibt
    /// bewusst keine Überladung mit bloßen Merkmalen: Zwei Methoden wären zwei Antworten auf
    /// „liegt die Komponente da".
    /// </param>
    /// <param name="warnings">
    /// Nimmt nicht-fatale Befunde des Registry-Lesers entgegen (<c>system.reg</c> oder
    /// <c>user.reg</c> fehlt oder ist unlesbar). Der Sucher wirft nie.
    /// </param>
    public static PrefixFinding Detect(
        string prefixPath, PrefixComponent component, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(component);

        var marks = component.Marks;
        var missing = new List<string>();

        // Kein Abbruch beim ersten Fehlschlag: Die Liste soll vollständig sein, sonst nennt die
        // Begründung in S3.4 beim zweiten Anlauf einen anderen Grund als beim ersten.
        foreach (var relativePath in marks.Files)
        {
            var parts = relativePath.Split('/');
            var fullPath = Path.Combine([prefixPath, "drive_c", .. parts]);

            // Nur File.Exists: Ein Verzeichnis mit dem Namen einer DLL ist kein Merkmal. Ein
            // Symlink zählt als vorhanden — File.Exists folgt ihm, und das ist richtig so: Was
            // dort steht, ist eine Datei, gleich wie sie dorthin kam.
            if (!File.Exists(fullPath))
            {
                missing.Add($"file {relativePath}");
            }
        }

        // Dieselbe Reihenfolge wie oben, nur mit einem Blick in die Datei: Wines eigene DLL
        // zählt nicht als Merkmal, auch wenn sie am richtigen Platz liegt.
        foreach (var relativePath in marks.NativeFiles)
        {
            var parts = relativePath.Split('/');
            var fullPath = Path.Combine([prefixPath, "drive_c", .. parts]);

            if (!File.Exists(fullPath))
            {
                missing.Add($"file {relativePath}");
            }
            else if (WineBuiltinDll.Looks(fullPath))
            {
                missing.Add($"file {relativePath} is Wine's own DLL");
            }
        }

        var systemRegPath = Path.Combine(prefixPath, "system.reg");
        foreach (var mark in marks.Registry)
        {
            var value = WineRegistry.ReadString(systemRegPath, mark.Key, mark.Name, warnings);
            if (value is null
                || !value.StartsWith(mark.ValuePrefix, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add($"registry {mark.Key}\\{mark.Name} is not \"{mark.ValuePrefix}…\"");
            }
        }

        // Die Overrides stehen in user.reg, nicht in system.reg — dasselbe Dateiformat, anderer
        // Hive (HKCU). Präfix statt Gleichheit wie bei RegistryMark: Ein fremdes "native,builtin"
        // besteht "native".
        var userRegPath = Path.Combine(prefixPath, "user.reg");
        foreach (var dllOverride in component.DllOverrides)
        {
            var value = WineRegistry.ReadString(userRegPath, DllOverridesKey, dllOverride.Name, warnings);
            if (value is null
                || !value.StartsWith(dllOverride.Value, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add($"dll override {dllOverride.Name} is not \"{dllOverride.Value}…\"");
            }
        }

        return new PrefixFinding(missing.Count == 0, missing);
    }
}
