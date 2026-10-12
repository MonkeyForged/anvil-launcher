namespace Anvil.App.ViewModels;

/// <summary>
/// Ein Modulabschnitt, der neu lesen will, wenn die Settings-Seite gezeigt wird (Spec S3.5b §3.7,
/// E-15). <b>Die Seite kennt kein Modul</b>: Sie ruft jede Karte, die Karte reicht an ihren
/// Abschnitt weiter, wenn er diese Schnittstelle trägt. Ein Abschnitt ohne sie wird nicht gerufen.
/// </summary>
public interface IShowable
{
    void OnShown();
}
