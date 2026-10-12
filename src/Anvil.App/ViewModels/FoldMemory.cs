namespace Anvil.App.ViewModels;

/// <summary>
/// Which folds of the right column are open, for as long as Anvil runs (spec s11.1 E-1).
/// Every fold starts closed after a restart and stays as the user left it across game switches;
/// nothing is persisted. Not thread-safe: only the UI thread touches it.
/// </summary>
public sealed class FoldMemory
{
    public const string ProtonKey = "game.proton";
    public const string CustomLaunchOptionsKey = "game.custom-launch-options";
    public const string GeneratedLaunchOptionsKey = "game.generated-launch-options";

    private readonly HashSet<string> _openKeys = [];

    /// <summary>The key of a module's options fold; the prefix keeps it apart from the game keys.</summary>
    public static string OptionsKeyFor(string moduleKey) => "options." + moduleKey;

    /// <summary>Is the fold open? False for every key that was never opened.</summary>
    public bool IsOpen(string key) => _openKeys.Contains(key);

    /// <summary>Remembers the fold as open or closed.</summary>
    public void SetOpen(string key, bool isOpen)
    {
        if (isOpen)
        {
            _openKeys.Add(key);
        }
        else
        {
            _openKeys.Remove(key);
        }
    }
}
