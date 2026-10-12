using Anvil.Core.Steam;

namespace Anvil.Core.Modules;

/// <summary>
/// Was ein Modul an einem Spiel anzubieten hat, <b>samt</b> der Frage, ob der Nutzer es
/// gerade auslösen kann (Spec S5.5 §3.4).
/// </summary>
public readonly record struct ModuleActionOffer(ModuleAction Action, bool IsEnabled);

/// <summary>
/// Die Regel, die im <b>Core</b> liegt und nicht im Modul — seit S5.5 an einer Stelle statt in
/// jeder Fläche neu.
///
/// <para>
/// <b>Zwei Regeln in einer Antwort, weil sie nie getrennt gebraucht werden:</b> „Gefragt wird
/// nur bei <see cref="ModuleGameStatus.Ready"/> und <see cref="ModuleGameStatus.Attached"/>"
/// (S5.3 §3.3) und „ob der Knopf bedienbar ist, sagt der Zustand, nicht das Modul" (S5.4).
/// Wer die Aktion zeichnet, braucht beide; wer nur eine hätte, zeichnete einen Knopf, der
/// einen Griff anstoßen könnte, während schon einer läuft.
/// </para>
///
/// <para>
/// <b>Das ist keine gemeinsame „Contribution"-Schicht</b> (Projektplan M5, Entscheidung 5):
/// Der Chip behält sein Vertragsmitglied, die Aktion ihres. Zusammengelegt ist hier nur, was
/// ohnehin Core-Regel ist — zwei Leser vom ersten Tag an: die Modulkarte und das Kontextmenü.
/// </para>
///
/// <para>
/// <b>Das Modul darf trotzdem <c>null</c> liefern.</b> Der Core sagt „hier darf überhaupt eine
/// Aktion stehen", das Modul sagt „ich habe eine".
/// </para>
/// </summary>
public static class ModuleActionRule
{
    /// <summary>
    /// Die Aktion dieses Moduls an diesem Spiel, oder <c>null</c> — dann steht dort keine.
    /// </summary>
    public static ModuleActionOffer? Offer(
        IModule module, GameProfile game, ModuleGameState state)
    {
        if (state.Status is not (ModuleGameStatus.Ready or ModuleGameStatus.Attached))
        {
            return null;
        }

        if (module.ActionFor(game, state) is not { } action)
        {
            return null;
        }

        // Anhängen braucht einen Griff, der reichen könnte; Abkoppeln nur einen, der ruht.
        var enabled = action.Kind == ModuleActionKind.Attach ? state.CanAttach : !state.IsBusy;
        return new ModuleActionOffer(action, enabled);
    }
}
