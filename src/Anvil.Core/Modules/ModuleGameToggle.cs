namespace Anvil.Core.Modules;

/// <summary>
/// Ein Je-Spiel-Schalter wurde umgelegt: welches Modul, welches Spiel (S2.3).
///
/// <para>
/// <b>Ein benannter Typ, kein Tupel</b> (Spec E-E): Ein
/// <c>EventHandler&lt;(string, uint)&gt;</c> in einer öffentlichen Naht zwingt jeden Hörer,
/// sich die Reihenfolge zu merken. <b>Ob ein- oder ausgeschaltet, steht bewusst nicht darin:</b>
/// Wer hört, fragt die Registry — sie ist die eine Wahrheit, und eine Kopie des Zustands in der
/// Meldung wäre die zweite.
/// </para>
/// </summary>
/// <param name="Key">Das umgeschaltete Modul — <see cref="IModule.Key"/>.</param>
/// <param name="AppId">Das Spiel, für das geschaltet wurde.</param>
public readonly record struct ModuleGameToggle(string Key, uint AppId);
