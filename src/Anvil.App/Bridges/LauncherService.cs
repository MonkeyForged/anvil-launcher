using Anvil.Core.Steam;

namespace Anvil.App.Bridges;

/// <summary>
/// The launch option every bridge makes: Steam then starts the game with the launcher service
/// in the container, which attaching uses.
///
/// <para>
/// <b>The same value for every game and every bridge</b> (design answer to 32 a): the bus name
/// is derived in the Core; there is no per-game variable. <b>In the App, not in the Core</b>
/// (Spec E-D) — the Core does not know a module contribution by name; only the module key
/// differs, so the Core can tell whose contribution it is.
/// </para>
/// </summary>
public static class LauncherService
{
    public const string Variable = "STEAM_COMPAT_LAUNCHER_SERVICE";

    public const string Value = "proton";

    /// <summary>The one launch option contribution every bridge makes, bound to the module's key.</summary>
    public static LaunchOptionContribution For(string moduleKey)
        => LaunchOptionContribution.Environment(moduleKey, Variable, Value);
}
