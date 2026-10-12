namespace Anvil.Core.Modules;

/// <summary>Die Art einer Modulaktion — das Hinzufügen oder sein Gegenteil (Spec S5.4 §3.1).</summary>
public enum ModuleActionKind
{
    /// <summary>Anhängen. Der Core zeichnet sie in Ember und sperrt sie, solange das Spiel steht.</summary>
    Attach,

    /// <summary>Abkoppeln — dieselbe Stelle, das Gegenteil. Stahlblau, wie jedes Lösen.</summary>
    Detach,
}

/// <summary>
/// Was ein Modul an einem Spiel anzubieten hat: eine Beschriftung und die Art (Spec S5.4 §3.1).
/// Das zweite Naht-Mitglied nach <see cref="ModuleChip"/> und in derselben Bauform.
/// </summary>
public sealed record ModuleAction(string Label, ModuleActionKind Kind);
