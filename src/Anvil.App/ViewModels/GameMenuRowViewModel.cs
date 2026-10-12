using System.Windows.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// One row of a game card's context menu (Spec S5.5 §3.1). <b>Since S5.5 the whole menu is a
/// list of rows</b>, no longer a handful of hard-written entries: a <c>ContextMenu</c> that
/// carries fixed entries and an <c>ItemsSource</c> at the same time shows the fixed ones and
/// <b>silently</b> drops the bound list (measured 2026-09-19).
///
/// <para>
/// <b>Command, target and operability sit on the base</b>, not only on the action row. A style
/// that targets <c>MenuItem</c> hits every row; a binding that fails on a caption greys out
/// <b>every</b> entry after the finding of the live acceptance on 2026-09-06 — "Start"
/// included. The base closes the trap structurally instead of working around it: no
/// <c>$parent</c> is left in the whole menu, every row carries its own command.
/// </para>
///
/// <para>
/// <b>Immutable</b>, like <see cref="ModuleChipViewModel"/>: when something changes, the card
/// rebuilds its rows — clear and refill.
/// </para>
/// </summary>
public abstract class GameMenuRowViewModel
{
    /// <summary>What the row triggers, or <c>null</c> — then it triggers nothing.</summary>
    public virtual ICommand? Command => null;

    /// <summary>What is passed to the command, or <c>null</c>.</summary>
    public virtual object? Parameter => null;

    /// <summary>
    /// Is the row operable? <b>Locked means visible and greyed out</b> (element description
    /// §9), not absent — and without extra text.
    /// </summary>
    public virtual bool IsEnabled => false;

    /// <summary>Is the row the caption of a module group? The view switches its style on this.</summary>
    public virtual bool IsCaption => false;

    /// <summary>Is the row the divider between two groups?</summary>
    public virtual bool IsRule => false;
}

/// <summary>
/// An action row: the base action of the game, the action of a module, or "Show in Steam".
/// </summary>
/// <param name="isRelease">
/// Is this the opposite action (Stop, DETACH …)? In the view it carries the <b>steel blue</b>
/// the element description states for every release — the tone lives in the Core, not in the
/// module (Spec S5.4 E-3).
/// </param>
/// <param name="isStart">
/// Is this the starting action (Start, ATTACH …)? It carries the <b>ember</b> text — "ember
/// starts, steel blue ends" (design delivery §2). "Show in Steam" is neither.
/// </param>
public sealed class GameMenuActionViewModel(
    string label, ICommand? command, object? parameter, bool isEnabled, bool isRelease,
    bool isStart = false)
    : GameMenuRowViewModel
{
    /// <summary>English, like the whole UI.</summary>
    public string Label => label;

    public override ICommand? Command => command;

    public override object? Parameter => parameter;

    public override bool IsEnabled => isEnabled;

    /// <summary>Ember text: Start, ATTACH …</summary>
    public bool IsStart => isStart;

    /// <summary>Steel blue text: Stop, DETACH …</summary>
    public bool IsRelease => isRelease;
}

/// <summary>
/// The caption of a module group — <c>WAND BRIDGE</c>, the display name of the module in
/// capitals (mockup <c>menuGroups</c>).
///
/// <para>
/// <b>It names the module, the chip the application</b> (Spec S5.5 E-2): the chip stands next
/// to others on a narrow card and therefore says "Wand"; the caption assigns a group to its
/// module. The Core builds the text without asking a module.
/// </para>
/// </summary>
public sealed class GameMenuCaptionViewModel(string title) : GameMenuRowViewModel
{
    public string Title => title;

    public override bool IsCaption => true;
}

/// <summary>
/// The divider. <b>A row in the data stream, no special case in the style</b> (Spec S5.5 E-4):
/// a row that says "here is a line" can be checked in the probe; the price is one inert
/// <c>MenuItem</c> per line.
/// </summary>
public sealed class GameMenuRuleViewModel : GameMenuRowViewModel
{
    public override bool IsRule => true;
}
