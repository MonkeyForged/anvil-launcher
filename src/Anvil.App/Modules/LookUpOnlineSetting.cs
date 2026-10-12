using Anvil.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// Whether Anvil may refresh Wand's catalog from the network (story s7.2, vision §3). On unless
/// the user turned it off — a missing entry means on, because the switch ships on.
///
/// <para>
/// <b>In the module's own settings file</b> (spec E-1): the setting belongs to the module, so it
/// travels with the data root and does not stay behind as an orphan in the Core's file when the
/// module is gone.
/// </para>
///
/// <para>
/// <b>Read on every access, no cached value</b> (spec E-4): a change applies from the next attach
/// without anyone having to be told.
/// </para>
/// </summary>
public sealed class LookUpOnlineSetting
{
    public const string Key = "lookUpGamesOnline";

    private readonly ModuleSettings _settings;
    private readonly ILogger? _logger;

    public LookUpOnlineSetting(ModuleSettings settings, ILogger? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsOn => _settings.Get(Key) != "false";

    /// <summary>
    /// Writes the new state. A value that already holds is not written again — the settings page
    /// re-reads the switch when it is shown, and that must not produce a log line.
    /// </summary>
    public void Set(bool on)
    {
        if (on == IsOn)
        {
            return;
        }

        _settings.Set(Key, on ? "true" : "false");
        _logger?.LogInformation("Look up games online: {State}", on ? "on" : "off");
    }
}
