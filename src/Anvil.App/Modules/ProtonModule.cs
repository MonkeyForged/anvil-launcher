using Anvil.App.ViewModels;
using Anvil.Core.Modules;
using Anvil.Core.Prefixes;
using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// The core entry for the Proton choice and launch options per game (Spec S4.6 §3.2). Core and
/// not switchable off — the Modules page shows it as the second entry with the "Core" badge.
///
/// <para>
/// <b>It lives in <c>Anvil.App</c>, not in the core</b> (Spec E-C), although the breakdown
/// recommended the core: no logic is left that would belong there. The list is
/// <see cref="CompatToolCatalog.Selectable"/>, the sorting out is <c>SteamChangeFilter</c>,
/// the collecting is <see cref="PendingChanges"/> — everything is already in the core. What the
/// module does is build a ViewModel.
/// </para>
/// </summary>
public sealed class ProtonModule : IModule
{
    private readonly PendingChanges _changes;
    private readonly Func<CompatToolCatalog> _catalog;
    private readonly Func<ModuleRegistry> _registry;
    private readonly ILogger? _logger;
    private readonly FoldMemory _folds;

    /// <param name="changes">The collection the section reports its wishes to (S4.5).</param>
    /// <param name="catalog">
    /// The catalog of the current snapshot — as a delegate, so that after a rescan the new one
    /// applies: a freshly installed tool is then in the list right away.
    /// </param>
    /// <param name="registry">
    /// The one truth about modules — <b>as a delegate</b>, because the registry is built with the
    /// modules and does not exist yet when this module is built (plan P-1). It is only fetched
    /// when a section is created.
    /// </param>
    /// <param name="logger">Category <see cref="Key"/>; null means silent.</param>
    /// <param name="folds">
    /// Where the open state of the folds is kept. <b>One memory for all sections</b> of this
    /// module, so that an open fold stays open when the user switches to another game. Null means
    /// a private one.
    /// </param>
    public ProtonModule(
        PendingChanges changes,
        Func<CompatToolCatalog> catalog,
        Func<ModuleRegistry> registry,
        ILogger? logger = null,
        FoldMemory? folds = null)
    {
        _changes = changes;
        _catalog = catalog;
        _registry = registry;
        _logger = logger;
        _folds = folds ?? new FoldMemory();
    }

    public string Key => "proton";

    public string DisplayName => "Proton & launch options";

    public string Description
        => "Override Proton version and launch options per game. Part of the base app.";

    public bool IsCore => true;

    /// <summary>
    /// Core and therefore always on. The section <i>acts</i> per game, but the module itself is
    /// not switched per game — that is the distinction S1.1 draws.
    /// </summary>
    public bool IsPerGame => false;

    public object? CreateSection(GameProfile game)
    {
        var registry = _registry();
        return new ProtonSectionViewModel(
            game,
            _catalog(),
            _changes,
            registry.AllLaunchOptionsFor(game),
            () => registry.LaunchOptionsFor(game),
            Key,
            _logger,
            _folds);
    }

    /// <summary>No icon: the module has no card of its own, but the GAME section.</summary>
    public string? IconKey => null;

    /// <summary>
    /// Core modules have nothing to check, and nobody asks them (S3.4, Spec E-9): the Modules
    /// row shows them the "Core" badge instead of a finding, and they have no card in the right
    /// column.
    /// </summary>
    public Task<ModuleReadiness> CheckReadinessAsync(CancellationToken cancellationToken)
        => Task.FromResult(ModuleReadiness.Ready);

    /// <summary>
    /// No settings card of its own: the Proton choice and launch options already live in the
    /// GAME section; a second surface for them would be a second place for the same thing.
    /// </summary>
    public object? CreateSettingsSection() => null;

    /// <summary>
    /// Nothing. The module <b>edits</b> the launch options — it does not contribute to them. What
    /// the user enters in the field is their own input and goes through the composer as such,
    /// not as a module's contribution (S2.1).
    /// </summary>
    public IReadOnlyList<LaunchOptionContribution> LaunchOptionsFor(GameProfile game) => [];

    /// <summary>The module chooses Proton and launch options — it touches no prefix.</summary>
    public IReadOnlyList<PrefixComponent> PrefixComponentsFor(GameProfile game) => [];

    /// <summary>The module needs no prefix component (Spec S3.5b E-1).</summary>
    public IReadOnlyList<PrefixComponent> RequiredComponents => [];

    /// <summary>The module chooses Proton and launch options; it attaches nothing.</summary>
    public Task<AttachDeclaration> PrepareAttachAsync(
        GameProfile game,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken)
        => Task.FromResult(AttachDeclaration.Nothing);

    /// <summary>Proton attaches nothing — it prepares prefixes.</summary>
    public IReadOnlyList<string> AttachProcessImages => [];

    /// <summary>Proton carries no folder per prefix.</summary>
    public IReadOnlyList<AttachStateFolder> AttachStateFolders => [];

    /// <summary>No chip: Proton is a core module and not switchable per game.</summary>
    public ModuleChip? ChipFor(GameProfile game, ModuleGameState state) => null;

    /// <summary>No module without a card has an action — Proton has none.</summary>
    public ModuleAction? ActionFor(GameProfile game, ModuleGameState state) => null;
}
