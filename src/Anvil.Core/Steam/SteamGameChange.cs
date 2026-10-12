namespace Anvil.Core.Steam;

/// <summary>
/// Die Proton-Wahl für ein Spiel (Spec S4.3 §4.5): <see cref="Auto"/> heißt „kein Eintrag,
/// Steam wählt selbst"; sonst ein Schlüssel aus <see cref="CompatTool.Key"/>.
/// </summary>
public sealed record ProtonChoice(string? ToolKey)
{
    // Benannt, weil der Record auch einen Kopier-Konstruktor hat: ein blankes null waere
    // zwischen ProtonChoice(string?) und ProtonChoice(ProtonChoice) mehrdeutig.
    public static ProtonChoice Auto { get; } = new(ToolKey: null);

    public bool IsAuto => ToolKey is null;
}

/// <summary>
/// Was ein Modul für ein Spiel in Steams Dateien will (Spec S4.3 §4.5). <b><c>null</c> heißt
/// „nicht anfassen"</b> (E-F): S4.5 sammelt je Spiel einen Wunsch, in dem ein Feld fehlen
/// darf — trüge er immer beide Werte, schriebe er zurück, was Steam beim Beenden gerade
/// geändert hat.
/// </summary>
/// <param name="AppId">Das Spiel.</param>
/// <param name="Proton">Auto oder Schlüssel; <c>null</c> lässt <c>CompatToolMapping</c> in Ruhe.</param>
/// <param name="LaunchOptions">
/// Der Text, wörtlich (E-G) — <c>""</c> leert einen vorhandenen Eintrag und legt keinen an;
/// <c>null</c> lässt <c>LaunchOptions</c> in Ruhe.
/// </param>
public sealed record SteamGameChange(uint AppId, ProtonChoice? Proton, string? LaunchOptions);
