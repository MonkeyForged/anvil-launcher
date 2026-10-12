using Anvil.Core.Prefixes;
using Anvil.Core.Steam;

namespace Anvil.Core.Modules;

/// <summary>
/// The core entry of the launcher itself. As an ordinary registry member, so the Modules page
/// (S1.6) draws from one source without a special case; the second core entry
/// ("Proton &amp; launch options") follows in M4.
/// </summary>
public sealed class LauncherModule : IModule
{
    /// <summary>
    /// The key as a constant (Spec S4.8 E-P): the transaction writes it into the note of a
    /// restore. A second <c>"launcher"</c> in the code would be a second truth about the same
    /// name, which is at the same time settings section, log category and folder name (design
    /// requirement 24).
    /// </summary>
    public const string KeyValue = "launcher";

    public string Key => KeyValue;

    public string DisplayName => "Launcher";

    public string Description => "Finds, starts and watches your Steam games.";

    public bool IsCore => true;

    /// <summary>The launcher is the base surface — switchable off per game would make no sense.</summary>
    public bool IsPerGame => false;

    /// <summary>The launcher has no section: it <i>is</i> the base surface.</summary>
    public object? CreateSection(GameProfile game) => null;

    /// <summary>
    /// Core modules have nothing to check, and nobody asks them (S3.4, Spec E-9): the Modules
    /// row shows them the "Core" badge instead of a finding, and they have no card in the right
    /// column (<see cref="IsPerGame"/> is <c>false</c>).
    /// </summary>
    public Task<ModuleReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
        => Task.FromResult(ModuleReadiness.Ready);

    /// <summary>The launcher has no settings card: it <i>is</i> the base surface.</summary>
    public object? CreateSettingsSection() => null;

    /// <summary>The launcher has no card and therefore no icon.</summary>
    public string? IconKey => null;

    /// <summary>
    /// The launcher contributes nothing: it <i>is</i> the base surface, and whoever starts a
    /// game does not change its launch options.
    /// </summary>
    public IReadOnlyList<LaunchOptionContribution> LaunchOptionsFor(GameProfile game) => [];

    /// <summary>The launcher touches no prefix: it starts games, it does not patch them.</summary>
    public IReadOnlyList<PrefixComponent> PrefixComponentsFor(GameProfile game) => [];

    /// <summary>The launcher needs no prefix component (Spec S3.5b E-1).</summary>
    public IReadOnlyList<PrefixComponent> RequiredComponents => [];

    /// <summary>The launcher starts games; it attaches itself to none.</summary>
    public Task<AttachDeclaration> PrepareAttachAsync(
        GameProfile game,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken)
        => Task.FromResult(AttachDeclaration.Nothing);

    /// <summary>The launcher attaches nothing — so it detaches nothing either.</summary>
    public IReadOnlyList<string> AttachProcessImages => [];

    /// <summary>The launcher carries no folder per prefix.</summary>
    public IReadOnlyList<AttachStateFolder> AttachStateFolders => [];

    /// <summary>
    /// No chip: the launcher is a core module for which <see cref="ModuleGameStates.StateOf"/>
    /// does not even answer — it is never asked.
    /// </summary>
    public ModuleChip? ChipFor(GameProfile game, ModuleGameState state) => null;

    /// <summary>No module without a card has an action — the launcher has none.</summary>
    public ModuleAction? ActionFor(GameProfile game, ModuleGameState state) => null;
}
