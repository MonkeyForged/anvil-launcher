using Anvil.App.Components;
using Anvil.Core.Packages;

namespace Anvil.App.Modules;

/// <summary>
/// Der Katalog geprüfter Fassungen, den Anvil mitbringt (S3.3a, Aufgabe 3).
///
/// <para>
/// <b>Warum hier und nicht im Core:</b> <c>dotnet</c> und <c>wemod.com</c> sind so wenig
/// Core-Wissen wie <see cref="Dotnet48"/>.<see cref="Dotnet48.Component"/>, das aus demselben
/// Grund nebenan liegt — der Core kennt Ablagen und den Bezug, aber nicht, was konkret bezogen
/// wird.
/// </para>
///
/// <para>
/// <b>Warum Code und keine Datei</b> (Spec E-1): Der Compiler prüft jeden Eintrag beim Bauen
/// (<see cref="CatalogEntry"/> wirft bei einer verformten Adresse oder Summe), und es gibt keine
/// Ladefehlerbehandlung für etwas, das immer da ist — eine Katalogdatei müsste fehlen, kaputt
/// oder veraltet sein können, und für all das bräuchte es einen Ausgang, den niemand braucht.
/// </para>
///
/// <para>
/// <b>Warum es kein Register gibt</b> (Spec E-2): Wer die <see cref="Wand"/>-Einträge braucht,
/// ist das Wand-Modul; es reicht sie seiner Karte, so wie es in S3.4 den Ordnernamen
/// <c>wand</c> reicht. Eine eigene Zuordnungstabelle hier wüsste dasselbe ein zweites Mal.
/// </para>
///
/// <para>
/// <b>Was eine neue Fassung kostet:</b> einen Eintrag hier plus ein Anvil-Update — und die
/// Prüfsumme wird dabei <b>selbst nachgerechnet</b>, nicht aus einer fremden Quelle
/// abgeschrieben. Die Anleitung dafür steht in <c>vendor/README.md</c>.
/// </para>
/// </summary>
public static class PackageCatalog
{
    /// <summary>
    /// Geteilte Abhängigkeiten (Entscheidung 9): liegen unter <c>&lt;Wurzel&gt;/dependencies/</c>,
    /// weil mehr als ein Modul sie braucht.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Dependencies { get; } =
    [
        new CatalogEntry(
            "dotNetFx40_Full_x86_x64.exe",
            ".NET Framework 4.0 (offline installer)",
            new Uri(
                "https://download.microsoft.com/download/9/5/A/95A9616B-7A37-4AF6-BC36-D6EA96C8DAAE/dotNetFx40_Full_x86_x64.exe"),
            "65e064258f2e418816b304f646ff9e87af101e4c9552ab064bb74d281c38659f",
            50449456,
            "winetricks (load_dotnet40 / load_dotnet48); points at Microsoft's own download "
            + "server, no fwlink redirect."),
        new CatalogEntry(
            "ndp48-x86-x64-allos-enu.exe",
            ".NET Framework 4.8 (offline installer)",
            new Uri(
                "https://download.visualstudio.microsoft.com/download/pr/7afca223-55d2-470a-8edc-6a1739ae3252/abd170b4b0ec15ad0222a809b761a036/ndp48-x86-x64-allos-enu.exe"),
            "95889d6de3f2070c07790ad6cf2000d33d9a1bdfc6a381725ab82ab1c314fd53",
            72721568,
            "winetricks (load_dotnet40 / load_dotnet48); points at Microsoft's own download "
            + "server, no fwlink redirect."),
    ];

    /// <summary>
    /// Die Wand-Anwendung (Entscheidung 9): liegt im Modulordner, weil sie nur das
    /// Wand-Modul braucht.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Wand { get; } =
    [
        new CatalogEntry(
            "WeMod-11.6.0-full.nupkg",
            "WeMod 11.6.0",
            new Uri("https://storage-cdn.wemod.com/app/releases/stable/WeMod-11.6.0-full.nupkg"),
            "5b94ae5592e698b13cbc06fae4c096fe2438cbd362daac3f842e13190bf836ba",
            166107939,
            "Scoop manifest Calinou/scoop-games; the vendor publishes only the latest release, "
            + "so a pinned older version has no first-party address. The file itself comes from "
            + "the vendor over TLS (storage-cdn.wemod.com, certificate CN=wemod.com)."),
    ];

    /// <summary>
    /// Das <b>zweite Manifest</b> (Spec S3.5a §3.3), nachgeschlagen: Was gebraucht wird, sagt die
    /// Komponente — jede <see cref="Anvil.Core.Prefixes.RecipeIngredient.FileName"/> eines
    /// Rezepts (<see cref="Anvil.Core.Prefixes.PrefixComponent.Recipe"/>). Woher man es bekommt,
    /// sagt diese Liste hier. Beide werden gegeneinander geprüft (<c>PackageCatalogTests</c>),
    /// statt sich zu vermischen.
    /// </summary>
    /// <param name="fileName">Der Dateiname, wie ihn eine Zutat nennt.</param>
    /// <returns>
    /// Der Quelleneintrag aus <see cref="Dependencies"/> oder <see cref="Wand"/> (in dieser
    /// Reihenfolge gesucht), oder <c>null</c> für einen unbekannten Namen — nie eine Ausnahme.
    /// </returns>
    public static CatalogEntry? SourceFor(string fileName)
    {
        foreach (var entry in Dependencies)
        {
            if (string.Equals(entry.FileName, fileName, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        foreach (var entry in Wand)
        {
            if (string.Equals(entry.FileName, fileName, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }
}
