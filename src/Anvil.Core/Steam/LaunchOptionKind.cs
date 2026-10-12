namespace Anvil.Core.Steam;

/// <summary>
/// Die drei Arten von Startoptionen (Vision §4.4). <b>Aus der Art folgt die Seite von
/// <c>%command%</c></b> — kein Modul rechnet mit dem Platzhalter, und keines kann ihn
/// falsch setzen.
/// </summary>
public enum LaunchOptionKind
{
    /// <summary>Eine Umgebungsvariable, <c>NAME=Wert</c>. Steht vor <c>%command%</c>.</summary>
    Environment,

    /// <summary>Ein vorgeschaltetes Programm wie <c>mangohud</c>. Steht vor <c>%command%</c>.</summary>
    Wrapper,

    /// <summary>Ein Argument für das Spiel wie <c>--skip-intro</c>. Steht dahinter.</summary>
    GameArgument,
}
