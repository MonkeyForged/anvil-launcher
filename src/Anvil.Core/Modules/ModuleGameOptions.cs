using Anvil.Core.Settings;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Modules;

/// <summary>
/// The one place that reads, stages and writes the per-game options of an attaching module
/// (Spec S5.4 §3.3) — a Core service alongside <see cref="ModuleGameStates"/>.
///
/// <para>
/// <b>Stored in the module folder</b>, through <see cref="ModuleSettings"/>
/// (<c>&lt;module folder&gt;/settings.json</c>): the options are the module's data, not the
/// Core's (project plan M5, decision 4). One entry per game and option, not a dictionary per
/// option (Spec E-5) — an option with default "on" could only be kept as a list of exceptions,
/// and no one reads that correctly.
/// </para>
///
/// <para>
/// <b>No cache</b> — the same reasoning as in the model this follows, <see cref="ModuleSettings"/>
/// itself (Plan S3.3, P-4): the file holds a handful of entries, and a cache would bring the
/// question of when it goes stale.
/// </para>
///
/// <para>
/// <b>A module never receives this object</b> — only narrowed delegates, bound to its own key.
/// A module holding this service as an object could read and flip other modules' switches; the
/// same reasoning that keeps <see cref="ModuleGameStates"/> Core-internal (architecture.md §4.2).
/// </para>
/// </summary>
public sealed class ModuleGameOptions
{
    private readonly Func<string, ModuleSettings> _settingsFor;
    private readonly PendingChanges _changes;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<ModuleGameOption>> _onByDefault;
    private readonly ILogger? _logger;

    /// <param name="settingsFor">
    /// A module's settings, by its key — the same shape as in <c>PackageStores</c>:
    /// <c>new ModuleSettings(() =&gt; folders.For(key), logger)</c>.
    /// </param>
    /// <param name="changes">Where wishes are staged and how they appear once saved.</param>
    /// <param name="onByDefault">
    /// Per module key, the options that start out "on" for that module — on top of
    /// <see cref="ModuleGameOption.DetachOnExit"/>, which is on for everyone. Null or a missing
    /// key means no extra option is on. The Core never names a module: the composition root
    /// fills this table.
    /// </param>
    /// <param name="logger">The category is the caller's choice; null means silent.</param>
    public ModuleGameOptions(
        Func<string, ModuleSettings> settingsFor,
        PendingChanges changes,
        IReadOnlyDictionary<string, IReadOnlySet<ModuleGameOption>>? onByDefault = null,
        ILogger? logger = null)
    {
        _settingsFor = settingsFor;
        _changes = changes;
        _onByDefault = onByDefault ?? new Dictionary<string, IReadOnlySet<ModuleGameOption>>();
        _logger = logger;
    }

    /// <summary>What holds — the value on disk, without the wish. This is what the chip reads.</summary>
    public bool Saved(string key, uint appId, ModuleGameOption option)
        => _settingsFor(key).Get(EntryKey(option, appId)) switch
        {
            "true" => true,
            "false" => false,
            _ => Default(key, option),
        };

    /// <summary>What the UI shows — the wish, else the saved value.</summary>
    public bool Displayed(string key, uint appId, ModuleGameOption option)
        => _changes.ModuleOption(key, appId, option) ?? Saved(key, appId, option);

    /// <summary>
    /// Stages a wish — modelled on <see cref="ModuleRegistry.StageEnabledFor"/>: if the value
    /// matches the saved one, an open wish drops out instead of being kept.
    /// </summary>
    public void Stage(string key, uint appId, ModuleGameOption option, bool enabled)
    {
        if (enabled == Saved(key, appId, option))
        {
            _changes.DropModuleOption(key, appId, option);
            return;
        }

        _changes.SetModuleOption(new ModuleGameOptionWish(key, appId, option, enabled));
    }

    /// <summary>Writes the wishes into each module's own settings file.</summary>
    public void Save(IReadOnlyList<ModuleGameOptionWish> wishes)
    {
        foreach (var wish in wishes)
        {
            _settingsFor(wish.Key).Set(
                EntryKey(wish.Option, wish.AppId),
                wish.Enabled ? "true" : "false");
            _logger?.LogInformation(
                "option {Option} for appid {AppId} of {Key}: {State}",
                wish.Option, wish.AppId, wish.Key, wish.Enabled ? "on" : "off");
        }
    }

    /// <summary>
    /// The key in <c>&lt;module folder&gt;/settings.json</c>: <c>attachOnLaunch.2114740</c>.
    /// <b>One entry per game, not a list per option</b> (Spec E-5): an option with default "on"
    /// could only be kept as a list of exceptions, and no one reads that correctly.
    /// </summary>
    private static string EntryKey(ModuleGameOption option, uint appId)
        => $"{Name(option)}.{appId}";

    private static string Name(ModuleGameOption option) => option switch
    {
        ModuleGameOption.AttachOnLaunch => "attachOnLaunch",
        ModuleGameOption.DetachOnExit => "detachOnExit",
        ModuleGameOption.OpenOnGame => "openOnGame",
        _ => throw new ArgumentOutOfRangeException(nameof(option)),
    };

    private bool Default(string key, ModuleGameOption option)
        => option == ModuleGameOption.DetachOnExit
            || (_onByDefault.TryGetValue(key, out var options) && options.Contains(option));
}
