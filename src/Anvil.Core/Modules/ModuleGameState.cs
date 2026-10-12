namespace Anvil.Core.Modules;

/// <summary>
/// Wie ein Modul an einem Spiel steht (Spec S5.2 §3.1) — die eine Wahrheit, aus der Chip
/// (S5.3) und Modulkarten-Marke (S5.4) lesen.
///
/// <para>
/// <b>Vier statt der sechs aus Vision §8, und das ist Absicht.</b> „Wird vorbereitet" gehört der
/// Speicherleiste als benannter Schritt; es hier zu führen wäre der zweite Ort für einen
/// Zustand, der genau einen haben soll. Die beiden „geht nicht"-Zeilen sind <b>ein</b> Wert mit
/// zwei Gründen (<see cref="ModuleGameBlock"/>).
/// </para>
///
/// <para>
/// <b>Kein Wert „fehlgeschlagen"</b> — wie beim gehaltenen Zustand des Griffs (S5.1): Der Wert
/// folgt dem Ausgang, nicht dem Versuch. Wo ein Fehlschlag sichtbar wird, ist
/// Design-Anforderung 52.
/// </para>
/// </summary>
public enum ModuleGameStatus
{
    /// <summary>Modul global aus, für dieses Spiel aus, oder (noch) nicht einsatzbereit.</summary>
    NotEnabled,

    /// <summary>Das Modul kann an diesem Spiel nicht greifen. <see cref="ModuleGameState.Block"/> sagt warum.</summary>
    NotPossible,

    /// <summary>An, möglich, nichts hängt.</summary>
    Ready,

    /// <summary>Es hängt etwas an, und das Spiel läuft.</summary>
    Attached,
}

/// <summary>
/// Warum ein Modul an einem Spiel nicht greifen kann. <b>Die Art, nicht der Satz</b> (Spec §3.2):
/// Die englischen Sätze stehen in der Modulkarte, die als einzige sie zeigt — so wandert kein
/// Nutzertext in den Core.
/// </summary>
public enum ModuleGameBlock
{
    /// <summary>Natives Linux-Spiel: kein Prefix möglich.</summary>
    NativeGame,

    /// <summary>Die Plattform war nicht feststellbar.</summary>
    UnknownPlatform,

    /// <summary>Windows-Spiel ohne Prefix — einmal starten, dann legt Steam ihn an.</summary>
    NeverStarted,

    /// <summary>Der Prefix wurde auf ein älteres Proton zurückgestuft (S4.2).</summary>
    MovedBack,
}

/// <summary>
/// Der Satz, den <see cref="ModuleGameStates"/> je Spiel und Modul hält.
/// </summary>
/// <param name="Block">Der Grund bei <see cref="ModuleGameStatus.NotPossible"/>, sonst <c>null</c>.</param>
/// <param name="CanAttach">
/// Greifen ginge jetzt: bereit, das Spiel läuft, kein Griff arbeitet. <b>Er ist der Grund, warum
/// sich der Zustand beim Spielstart überhaupt bewegt</b> — die vier Werte allein täten das nicht.
/// </param>
/// <param name="IsBusy">Ein Griff arbeitet gerade. Daran sperrt der Knopf aus S5.4.</param>
/// <param name="Pid">Der Startclient bei <see cref="ModuleGameStatus.Attached"/>, sonst <c>null</c>.</param>
public readonly record struct ModuleGameState(
    uint AppId,
    string ModuleKey,
    ModuleGameStatus Status,
    ModuleGameBlock? Block,
    bool CanAttach,
    bool IsBusy,
    int? Pid);
