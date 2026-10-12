namespace Anvil.Core.Modules;

/// <summary>
/// The per-game options of an attaching module (Spec S5.4 §3.2, s7.1 E-1).
///
/// <para>
/// <b>The Core keeps the options; the meaning belongs to whoever reads them.</b> The Core acts
/// on <see cref="AttachOnLaunch"/> (the trigger) and <see cref="DetachOnExit"/> (the watcher);
/// <see cref="OpenOnGame"/> it only stages and stores, and the module reads it. The words —
/// "Attach on game launch", "Open this game in Wand", "Quit Wand with the game" — always belong
/// to the module, because they name the application.
/// </para>
///
/// <para>
/// <b>Why a module-only option sits in this Core list</b> (s7.1 E-1): the other two already go
/// through the pending changes and the save bar; a third box with a different rule would be
/// three boxes with two behaviours. Options a module registers under its own name are the
/// cleaner form once a second module needs one (<c>roadmap.md</c>).
/// </para>
/// </summary>
public enum ModuleGameOption
{
    /// <summary>Attach by itself when the game starts. Default: <b>off</b>.</summary>
    AttachOnLaunch,

    /// <summary>Detach by itself when the session ends. Default: <b>on</b>.</summary>
    DetachOnExit,

    /// <summary>
    /// Open the attached application on this game's page. Default: <b>off</b>. Read only by
    /// the module (s7.1).
    /// </summary>
    OpenOnGame,
}

/// <summary>A staged wish for an option — the building block of <see cref="ModuleGameSwitch"/>.</summary>
public sealed record ModuleGameOptionWish(
    string Key, uint AppId, ModuleGameOption Option, bool Enabled);
