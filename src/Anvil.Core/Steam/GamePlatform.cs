namespace Anvil.Core.Steam;

/// <summary>
/// Woraufhin ein Spiel läuft (Spec §4). Drei Werte, keine vierten — und
/// <see cref="Unknown"/> ist der sichere Rückfall, nie eine Vermutung.
/// </summary>
public enum GamePlatform
{
    /// <summary>Ein Windows-Programm — Proton-Sache.</summary>
    Windows,

    /// <summary>Ein natives Linux-Programm — läuft ohne Proton.</summary>
    NativeLinux,

    /// <summary>Nicht feststellbar: Ordner leer, fehlend, unlesbar, oder Deckel erreicht.</summary>
    Unknown,
}
