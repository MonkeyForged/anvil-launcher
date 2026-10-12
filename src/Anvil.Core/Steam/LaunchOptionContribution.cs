namespace Anvil.Core.Steam;

/// <summary>
/// Ein benannter Beitrag zu den Startoptionen eines Spiels: <b>wer · welche Art · Wert</b>
/// (Vision §4.4).
///
/// <para>
/// <b>Umgebungsvariablen tragen Name und Wert getrennt</b> (E-1): Als eine Zeichenkette
/// <c>NAME=Wert</c> ließe sich weder erkennen, dass zwei Module dieselbe Variable meinen,
/// noch ließen sich listenartige Werte zusammenführen. Wrapper und Spielargumente haben
/// keinen Namen — bei ihnen ist <see cref="Name"/> <c>null</c>.
/// </para>
///
/// <para>
/// <b>Kein <c>AppId</c>-Feld</b> (E-2): Ein Beitrag entsteht immer für ein bestimmtes Spiel
/// (<c>IModule.LaunchOptionsFor(GameProfile)</c>, S2.2). Das Spiel steht im Aufruf, nicht in
/// der Antwort — sonst gäbe es zwei Stellen, an denen es falsch sein kann.
/// </para>
/// </summary>
/// <param name="ModuleKey">Wer beiträgt — <see cref="Anvil.Core.Modules.IModule.Key"/>.</param>
/// <param name="Kind">Welche Art — daraus folgt die Seite von <c>%command%</c>.</param>
/// <param name="Name">Der Variablenname bei <see cref="LaunchOptionKind.Environment"/>, sonst <c>null</c>.</param>
/// <param name="Value">Der Wert, wörtlich so, wie er in der Zeile landet.</param>
public sealed record LaunchOptionContribution(
    string ModuleKey, LaunchOptionKind Kind, string? Name, string Value)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen.

    /// <summary>Wer beiträgt — <see cref="Anvil.Core.Modules.IModule.Key"/>.</summary>
    public string ModuleKey { get; init; } = !string.IsNullOrWhiteSpace(ModuleKey)
        ? ModuleKey
        : throw new ArgumentException("Module key must not be blank.", nameof(ModuleKey));

    /// <summary>Der Variablenname bei <see cref="LaunchOptionKind.Environment"/>, sonst <c>null</c>.</summary>
    public string? Name { get; init; } = ValidateName(Kind, Name);

    /// <summary>Der Wert, wörtlich so, wie er in der Zeile landet.</summary>
    public string Value { get; init; } = Value ?? throw new ArgumentNullException(nameof(Value));

    // Ein Wrapper oder ein Spielargument mit Namen wäre ein Zustand, den Render nicht abbilden
    // kann, und fiele erst im fertigen String auf — deshalb hier abweisen, nicht erst dort.
    private static string? ValidateName(LaunchOptionKind kind, string? name)
    {
        if (kind == LaunchOptionKind.Environment)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }
        else if (name is not null)
        {
            throw new ArgumentException($"{kind} contributions must not carry a name.", nameof(name));
        }

        return name;
    }

    /// <summary>Eine Umgebungsvariable: <c>NAME=Wert</c>.</summary>
    public static LaunchOptionContribution Environment(string moduleKey, string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new LaunchOptionContribution(moduleKey, LaunchOptionKind.Environment, name, value);
    }

    /// <summary>Ein vorgeschaltetes Programm, etwa <c>mangohud</c>.</summary>
    public static LaunchOptionContribution Wrapper(string moduleKey, string value)
        => new(moduleKey, LaunchOptionKind.Wrapper, null, value);

    /// <summary>Ein Argument für das Spiel, etwa <c>--skip-intro</c>.</summary>
    public static LaunchOptionContribution GameArgument(string moduleKey, string value)
        => new(moduleKey, LaunchOptionKind.GameArgument, null, value);

    /// <summary>Der Beitrag als Text, so wie er in der Zeile steht.</summary>
    public string Render()
        => Kind == LaunchOptionKind.Environment ? $"{Name}={Value}" : Value;
}
