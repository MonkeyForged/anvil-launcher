namespace Anvil.Core.Steam;

/// <summary>
/// Die zwei Felder, die Anvil in Steams Dateien schreibt (Spec S4.8 §3.1).
///
/// <para>
/// <b>Ein Wert, kein String</b> (E-A): <see cref="PendingChanges"/> führte den Feldnamen bisher
/// als Text mit, aber nur fürs Log. Seit S4.8 hängt eine Zuordnung daran — welches Modul, welche
/// Datei —, und dafür ist ein String zu schwach: Ein Tippfehler fiele erst im fertigen Vermerk
/// auf.
/// </para>
/// </summary>
public enum SteamField
{
    /// <summary>Die Proton-Wahl — <c>CompatToolMapping</c> in <c>config.vdf</c>.</summary>
    Proton,

    /// <summary>Die Startoptionen — <c>LaunchOptions</c> in <c>localconfig.vdf</c>.</summary>
    LaunchOptions,
}

/// <summary>
/// Die zwei Textformen eines Feldes, englisch (Spec S4.8 §3.4). Beide stehen hier, weil sie
/// dasselbe benennen: <see cref="Label"/> als Feldname im Detail-Popup, <see cref="InSentence"/>
/// im laufenden Satz — Kurzfassung und Log.
/// </summary>
public static class SteamFieldText
{
    /// <summary>Der Feldname im Detail-Popup: <c>Proton</c>, <c>Launch options</c>.</summary>
    public static string Label(this SteamField field)
        => field switch
        {
            SteamField.Proton => "Proton",
            SteamField.LaunchOptions => "Launch options",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

    /// <summary>
    /// Der Feldname im Satz: <c>Proton</c> bleibt groß (es ist ein Name), <c>launch options</c>
    /// klein.
    /// </summary>
    public static string InSentence(this SteamField field)
        => field switch
        {
            SteamField.Proton => "Proton",
            SteamField.LaunchOptions => "launch options",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
}
