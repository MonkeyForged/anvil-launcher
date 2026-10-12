namespace Anvil.Core.Tray;

/// <summary>
/// „Schliessen heisst Verstecken" als reine Funktion (Projektplan S3.5, Spec S3.5 §3.3). Der
/// Riegel des Vorgaengers: Verstecken nur, wenn der Nutzer selbst schliesst, der Schalter an
/// ist <b>und</b> ein Tray da ist. Jeder andere Fall beendet — auch <c>Exit</c> aus dem Tray,
/// das ueber <c>Shutdown()</c> mit einem anderen Grund hier ankommt.
/// </summary>
public static class CloseToTrayPolicy
{
    /// <param name="userClosing">Der Nutzer hat das Fenster geschlossen (X, Alt+F4) — die
    /// App uebersetzt Avalonias <c>WindowCloseReason.WindowClosing</c>; der Core kennt
    /// Avalonia nicht.</param>
    /// <param name="minimizeToTray">Der Schalter der Settings-Seite.</param>
    /// <param name="trayAvailable">Die letzte Antwort der Sonde; „noch keine" heisst false.</param>
    public static bool ShouldHide(bool userClosing, bool minimizeToTray, bool trayAvailable)
        => userClosing && minimizeToTray && trayAvailable;
}
