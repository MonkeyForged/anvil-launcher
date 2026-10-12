namespace Anvil.Core.Sessions;

/// <summary>
/// Wie Steams Fenster heisst, an einer Stelle — neben <see cref="SteamUrl"/>, das sagt, wie
/// Steams Adressen heissen (Spec S3.8 E-G). <see cref="Raise"/> ist der Delegat, den die
/// Composition Root an die Library reicht.
/// </summary>
public static class SteamWindow
{
    /// <summary>Zweites Feld von <c>WM_CLASS</c>; das erste ist <c>steamwebhelper</c>.</summary>
    public const string ResourceClass = "steam";

    /// <summary>Titel des Hauptfensters. Freundesliste und Chats tragen andere.</summary>
    public const string MainTitle = "Steam";

    /// <summary>Holt Steams Hauptfenster nach vorn (S3.8).</summary>
    public static RaiseOutcome Raise() => X11WindowRaiser.Raise(ResourceClass, MainTitle);
}
