using Anvil.App.Modules;
using Anvil.Core.Prefixes;

namespace Anvil.App.Components;

/// <summary>
/// Die Komponente <b>.NET Framework 4.8</b> — in dieser Story nur ihre Befund-Hälfte (S3.1).
///
/// <para>
/// <b>Sie liegt beim Modul, nicht im Core</b> (Projektplan M3, Entscheidung 5): Der Core sieht
/// nach, wonach zu sehen ist sagt die Komponente. S3.2 hebt das hier in
/// <c>PrefixComponent</c> hinein und lässt <see cref="WandBridgeModule"/> die Komponente
/// <b>fordern</b>; gefordert wird sie hier noch von niemandem.
/// </para>
///
/// <para>
/// <b>Sie liegt in <c>Anvil.App.Components</c>, nicht mehr bei <see cref="WandBridgeModule"/></b>
/// (S3.5a): Eine Komponente gehört dem Prefix, nicht dem Modul, das sie fordert — zwei Bridges,
/// die dasselbe .NET 4.8 brauchen, dürfen die Deklaration nicht zweimal tragen (Spec S3.5a §3.1,
/// dieselbe Begründung wie bei der geteilten Ablage, Projektplan M3, Entscheidung 9).
/// </para>
///
/// <para>
/// <b>Warum genau diese drei Merkmale — und nicht die naheliegenden.</b> Gemessen am
/// 2026-09-14 an sieben Prefixen der echten Maschine (Spec §3), mit Gegenprobe:
/// </para>
///
/// <list type="bullet">
/// <item>
/// <b>Der Schlüssel allein zählt nicht.</b> Wine legt die .NET-Registry-Zweige in
/// <i>jeden</i> frischen Prefix — alle sieben trugen sie, ohne dass je ein Installer lief.
/// </item>
/// <item>
/// <b><c>Release</c> ist der falsche Maßstab, und zwar verkehrt herum.</b> Microsofts
/// Schwelle für 4.8 ist 528 040; Wines erfundener Wert ist <b>533 320</b> und damit größer.
/// Die Prüfung „Release ≥ 528040" meldet auf jedem unvorbereiteten Prefix Erfolg.
/// </item>
/// <item>
/// <b><c>mscorlib.dll</c> ist kein Merkmal.</b> Sie liegt in jedem frischen Prefix (752 128
/// Bytes, Wines .NET-Ersatz), in beiden Microsoft.NET-Zweigen. <c>Vision §6.3</c> nannte sie
/// bis zu dieser Messung als Merkmal — korrigiert.
/// </item>
/// <item>
/// <b><c>mscoree.dll</c> ebenfalls nicht.</b> In system32 und syswow64 steht sie als Symlink
/// in Protons geteilte Dateien, in jedem frischen Prefix.
/// </item>
/// </list>
///
/// <para>
/// Übrig bleiben die <c>Version</c>-Zeichenkette mit Präfix <c>4.8</c> (Wine sagt
/// <c>4.7.03190</c>) und <c>clr.dll</c> in beiden Bitbreiten — die eigentliche Laufzeit, die
/// nur aus einer echten Installation kommt. Das sind die „CLR-Dateien" aus Vision §6.3.
/// </para>
///
/// <para>
/// <b>Was hier noch nicht bewiesen ist:</b> gemessen wurde ausschließlich die <i>negative</i>
/// Seite. Dass ein echt vorbereiteter Prefix bei allen drei Merkmalen anschlägt, ist aus dem
/// dokumentierten .NET-Aufbau abgeleitet und fällt als Beweis in <b>S3.5</b>, wo zum ersten
/// Mal einer entsteht. Die teure Hälfte ist die negative: Ein falsch-positiver Befund lässt
/// einen kaputten Prefix als fertig durchgehen, ein falsch-negativer kostet drei Minuten
/// Neubau.
/// </para>
/// </summary>
// public, nicht internal: Es gibt im Repo kein InternalsVisibleTo, Anvil.App.Tests sähe die
// internals von Anvil.App also nicht. Dieselbe Sichtbarkeit wie WandBridgeModule daneben.
public static class Dotnet48
{
    /// <summary>Der stabile, versionsfreie Schlüssel (Spec E-1).</summary>
    public const string ComponentKey = "dotnet";

    /// <summary>Die Version dieser Deklaration.</summary>
    public const string ComponentVersion = "4.8";

    /// <summary>Woran man sie im Prefix erkennt.</summary>
    public static PrefixMarks Marks { get; } = new(
        Files:
        [
            "windows/Microsoft.NET/Framework/v4.0.30319/clr.dll",
            "windows/Microsoft.NET/Framework64/v4.0.30319/clr.dll",
        ],
        Registry:
        [
            new RegistryMark(
                @"Software\Microsoft\NET Framework Setup\NDP\v4\Full", "Version", "4.8"),
        ])
    {
        // Nachgetragen am 2026-09-17 (messungen-anhaengen.md §7.2): Ihre Anwesenheit sagt nichts, ihre
        // Herkunft alles. Ein unter Proton 8 gebautes Paket brachte Wines eigene mscoree.dll mit —
        // der Befund meldete "vorbereitet", der Override zeigte auf Wines Stub, und Wand
        // scheiterte beim Laden. Proton hält dieselbe Kombination für kaputtes .NET und löscht
        // beim nächsten Wechsel der Hauptversion Microsoft.NET (§7.3).
        NativeFiles =
        [
            "windows/system32/mscoree.dll",
            "windows/syswow64/mscoree.dll",
        ],
    };

    /// <summary>
    /// Der DllOverride, ohne den .NET 4.8 im Prefix nichts bewirkt (Spec S3.6 §3.1): Ohne ihn lädt
    /// Wine seinen eigenen <c>mscoree</c>, und Wand liefe auf Wines Ersatz. Er ist <b>nicht
    /// Nutzlast</b>, sondern Teil von Befund und Anwenden — und die letzte Rezeptstufe nimmt Name und
    /// Wert von hier, statt sie ein zweites Mal zu tippen.
    /// </summary>
    public static DllOverride MscoreeOverride { get; } = new("*mscoree", "native");

    /// <summary>Alle DllOverrides dieser Komponente — heute genau einer.</summary>
    public static IReadOnlyList<DllOverride> DllOverrides { get; } = [MscoreeOverride];

    /// <summary>
    /// Was die Komponente im Prefix beansprucht — gemessen im Vorgängerprojekt an der echten
    /// Maschine, übernommen ist das Wissen, nicht der Code (CLAUDE.md, Archivregel).
    ///
    /// <para>
    /// <b>Die Familie <c>*_clr0400.dll</c> steht nicht einzeln darin</b> — sie ist ein Muster,
    /// kein Pfad, und wird beim Ernten über <see cref="PrefixRecipe.HarvestPatterns"/> mitgenommen:
    /// Die Nutzlast beschreibt den <i>Anspruch</i> (S3.2, E-5), das Muster gehört zum
    /// <b>Rezept</b>, nicht zur Nutzlast, und sagt, was beim Ernten zusätzlich mitgeht. Für den
    /// bekannten Fall ist die Lücke nicht erreichbar: Eine zweite Komponente, die solche Dateien
    /// mitbrächte, wäre .NET-nah und stieße längst auf <c>windows/Microsoft.NET</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Der DllOverride <see cref="MscoreeOverride"/> ist nicht Nutzlast</b>, sondern ein Schritt des
    /// Anwendens (S3.6). Er steht in <c>user.reg</c>, nicht in <c>system.reg</c>.
    /// </para>
    /// </summary>
    public static PrefixPayload Payload { get; } = new(
        Files:
        [
            "windows/Microsoft.NET",
            "windows/system32/mscoree.dll",
            "windows/system32/mscorier.dll",
            "windows/system32/dfshim.dll",
            "windows/system32/netfxperf.dll",
            "windows/syswow64/mscoree.dll",
            "windows/syswow64/mscorier.dll",
            "windows/syswow64/dfshim.dll",
            "windows/syswow64/netfxperf.dll",
        ],
        RegistryBranches:
        [
            @"Software\Microsoft\.NETFramework",
            @"Software\Microsoft\NET Framework Setup",
            @"Software\Microsoft\Fusion",
            @"Software\Wow6432Node\Microsoft\.NETFramework",
            @"Software\Wow6432Node\Microsoft\NET Framework Setup",
            @"Software\Wow6432Node\Microsoft\Fusion",
        ]);

    // Der Dateiname kommt aus dem Katalog, nicht ein zweites Mal getippt (Spec §3.4): Ändert
    // sich dort der Name ohne Nachzug hier, findet Single keinen Treffer mehr und wirft beim
    // ersten Zugriff auf Recipe — statt still eine falsche Datei zu erwarten.
    private static string DependencyFileName(string fileName)
        => PackageCatalog.Dependencies.Single(entry => entry.FileName == fileName).FileName;

    /// <summary>
    /// Das Rezept, mit dem der Core diese Komponente aus einem Wegwerf-Prefix baut — wörtlich
    /// das gemessene Rezept aus Vision §6.2, jeder Schritt in der Praxis gefolgt von einem
    /// <c>wineserver -w</c> (Sache des Bauers aus S3.5, nicht des Rezepts selbst).
    ///
    /// <para>
    /// <b><see cref="PrefixRecipe.HarvestPatterns"/> nennt die Familie <c>*_clr0400.dll</c></b>,
    /// die Wine je installierter CLR-Version in beiden Bitbreiten ablegt (S3.5, Spec §3.4). Ein
    /// Muster, kein Pfad: Es gehört zum Rezept, weil es sagt, was beim Ernten zusätzlich
    /// mitgeht — die Nutzlast (<see cref="Payload"/>) beschreibt nur den <i>Anspruch</i>
    /// (S3.2, E-5). Seit S3.9 (E-3) steht es hier statt in einer eigenen Eigenschaft: Ein
    /// Core-Schritt, der beliebige Komponenten vorbereitet, findet es so am Rezept, statt in
    /// einer Klasse, die er nicht kennen darf.
    /// </para>
    /// </summary>
    public static PrefixRecipe Recipe { get; } = new(
        Ingredients:
        [
            new RecipeIngredient(
                "dotnet40",
                DependencyFileName("dotNetFx40_Full_x86_x64.exe"),
                "the .NET 4.0 installer"),
            new RecipeIngredient(
                "dotnet48",
                DependencyFileName("ndp48-x86-x64-allos-enu.exe"),
                "the .NET 4.8 installer"),
        ],
        Stages:
        [
            // mscoree=d verhindert, dass Wine Mono entsteht — sonst hält der 4.0-Installer
            // sich für überflüssig und endet nach neun Sekunden mit Rückgabewert 0, ohne etwas
            // installiert zu haben (Vision §6.2).
            new RecipeStage(
                "creating the prefix",
                ["wineboot", "-u"],
                IngredientSlot: null,
                DllOverrides: "mscoree=d",
                Timeout: TimeSpan.FromMinutes(2)),
            // 4.0 will beim Installieren ein Windows XP sehen.
            new RecipeStage(
                "switching to Windows XP",
                ["winecfg", "-v", "winxp"],
                IngredientSlot: null,
                DllOverrides: null,
                Timeout: TimeSpan.FromMinutes(2)),
            // Die Zehn-Minuten-Grenze ist keine Erwartung, sondern die Reißleine für einen
            // hängenden Prozess — gemessen lief der Installer in 62 s durch (Vision §6.2).
            new RecipeStage(
                "installing .NET 4.0",
                ["/q", "/c:install.exe /q"],
                IngredientSlot: "dotnet40",
                DllOverrides: "fusion=b",
                Timeout: TimeSpan.FromMinutes(10)),
            // 4.8 verweigert die Installation, wenn der Prefix sich als Windows 11 ausgibt.
            new RecipeStage(
                "switching to Windows 7",
                ["winecfg", "-v", "win7"],
                IngredientSlot: null,
                DllOverrides: null,
                Timeout: TimeSpan.FromMinutes(2)),
            // Wie bei Stufe 3: keine Erwartung, sondern die Reißleine — gemessen lief der
            // Installer in 73 s durch (Vision §6.2).
            new RecipeStage(
                "installing .NET 4.8",
                ["/sfxlang:1027", "/q", "/norestart"],
                IngredientSlot: "dotnet48",
                DllOverrides: "fusion=b",
                Timeout: TimeSpan.FromMinutes(10)),
            new RecipeStage(
                "enabling the .NET runtime",
                ["reg", "add", @"HKCU\Software\Wine\DllOverrides", "/v", MscoreeOverride.Name, "/t", "REG_SZ", "/d", MscoreeOverride.Value, "/f"],
                IngredientSlot: null,
                DllOverrides: null,
                Timeout: TimeSpan.FromMinutes(2)),
        ])
    {
        HarvestPatterns =
        [
            "windows/system32/*_clr0400.dll",
            "windows/syswow64/*_clr0400.dll",
        ],
    };

    /// <summary>Die Komponente, die Module fordern.</summary>
    // Achtung Reihenfolge: statische Auto-Eigenschaften laufen in Deklarationsreihenfolge —
    // Component muss deshalb nach Marks, Payload und Recipe stehen, sonst bekämen die
    // Initialisierer null bzw. die Objekt-Initialisierung unten griffe ins Leere.
    public static PrefixComponent Component { get; } = new(
        ComponentKey, ComponentVersion, Marks, Payload, Includes: ["4.0"])
    {
        Recipe = Recipe,
        DllOverrides = DllOverrides,
        DisplayName = ".NET Framework",
    };
}
