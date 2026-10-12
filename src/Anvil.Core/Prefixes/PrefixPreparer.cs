using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Turns the modules' component requirements into a save-sequence step that prepares a game's
/// prefix (Spec S3.9 §3.1).
///
/// <para>
/// <b>The Core derives, the module only requests</b> (E-1): what a game needs already sits in
/// <c>PrefixComponentsFor</c>; building and applying are Core services (S3.5, S3.6). There is
/// therefore no contract member that modules would have to fill with exactly this Core code.
/// </para>
///
/// <para>
/// <b>One step per game, not per component</b> (E-2): the mockup shows a single link
/// <c>Preparing prefix</c>; two components in the same prefix would be two identically labeled
/// links.
/// </para>
///
/// <para>
/// <b>Only what can be provided is reported</b> (E-6): a package for the major version, or a
/// recipe with all ingredients. Otherwise <c>Prepare</c> would show a bar that is guaranteed to
/// fail; the reason already sits on the Modules page and in <c>Dependencies</c>.
/// </para>
///
/// <para>
/// <b>Running games are excluded</b> (E-8): a running game holds its prefix's Wine server.
/// </para>
///
/// <para>
/// <b>No cancellation signal</b> (E-7): builder and applier get <see cref="CancellationToken.None"/>;
/// cancellation happens by quitting Anvil, and leftover build folders are cleaned up by the next
/// step before it builds.
/// </para>
/// </summary>
public sealed class PrefixPreparer
{
    /// <summary>The link's label — without <c>…</c>, as drawn in the mockup.</summary>
    public const string StepLabel = "Preparing prefix";

    private readonly Func<GameProfile, bool, IReadOnlyList<PrefixComponentRequest>> _requests;
    private readonly ComponentInventory _inventory;
    private readonly PrefixPackageStore _packages;
    private readonly Func<BuildRequest, IProgress<BuildProgress>?, CancellationToken, Task<BuildResult>> _build;
    private readonly Func<ApplyRequest, CancellationToken, Task<ApplyResult>> _apply;
    private readonly Func<RevertRequest, CancellationToken, Task<RevertResult>> _revert;
    private readonly Func<uint, bool> _isRunning;
    private readonly ILogger? _logger;

    // MissingFor runs on every refresh of the work list — without this set the same line
    // would fill the log (Spec s8.1 E-9). Refreshes run on the thread pool, hence the lock.
    private readonly HashSet<uint> _reportedUnplaceable = [];
    private readonly Lock _reportedGate = new();

    /// <param name="requests">
    /// The requests for a game; the second argument means "including unsaved" (E-4) — in the
    /// composition root <c>ModuleRegistry.PrefixComponentRequestsFor</c> resp. <c>…IncludingUnsaved</c>.
    /// </param>
    /// <param name="inventory">The current version and ingredient paths.</param>
    /// <param name="packages">Existing packages, and cleanup of leftover builds.</param>
    /// <param name="build">In the composition root, <c>PrefixPackageBuilder.BuildAsync</c>.</param>
    /// <param name="apply">In the composition root, <c>PrefixPayloadApplier.ApplyAsync</c>.</param>
    /// <param name="revert">In the composition root, <c>PrefixPayloadReverter.RevertAsync</c> (Spec S4.2 §3.4).</param>
    /// <param name="isRunning">Is the game with this app id currently running (E-8)?</param>
    /// <param name="logger">The owner determines the category; <c>null</c> means silent.</param>
    public PrefixPreparer(
        Func<GameProfile, bool, IReadOnlyList<PrefixComponentRequest>> requests,
        ComponentInventory inventory,
        PrefixPackageStore packages,
        Func<BuildRequest, IProgress<BuildProgress>?, CancellationToken, Task<BuildResult>> build,
        Func<ApplyRequest, CancellationToken, Task<ApplyResult>> apply,
        Func<RevertRequest, CancellationToken, Task<RevertResult>> revert,
        Func<uint, bool> isRunning,
        ILogger? logger = null)
    {
        _requests = requests;
        _inventory = inventory;
        _packages = packages;
        _build = build;
        _apply = apply;
        _revert = revert;
        _isRunning = isRunning;
        _logger = logger;
    }

    /// <summary>
    /// The missing, providable components of this game, each <b>in resolved form</b> and with
    /// its requesters. Empty means "nothing to do" — also without a prefix: a game never
    /// launched reports nothing.
    /// </summary>
    public IReadOnlyList<RequiredPrefixComponent> MissingFor(GameProfile game, bool includingUnsaved)
    {
        ArgumentNullException.ThrowIfNull(game);

        // E-8: A prefix in use is not written to.
        if (_isRunning(game.Game.AppId))
        {
            return [];
        }

        var report = PrefixReport.Of(game);
        var tool = game.Proton?.Tool;
        if (report.State != PrefixState.Present || report.Path is null
            || tool is null || ProtonWine.Find(tool) is null)
        {
            return [];
        }

        var requests = _requests(game, includingUnsaved);
        if (requests.Count == 0)
        {
            return [];
        }

        if (report.ProtonMajor is not { } major)
        {
            ReportUnplaceable(game);
            return [];
        }

        var warnings = new List<string>();
        var composed = PrefixComponentComposer.Compose(requests, warnings);
        foreach (var warning in warnings)
        {
            _logger?.LogWarning("prefix components for appid {AppId}: {Warning}", game.Game.AppId, warning);
        }

        var missing = new List<RequiredPrefixComponent>();
        foreach (var required in composed)
        {
            var component = _inventory.Resolve(required.Component);

            // S4.2 §3.5: The verdict comes before the finding. A downgraded prefix is not a
            // work order (project plan M4, decision 5), and an upgraded one is, even though the
            // finding still locates the component.
            var standing = PrefixPayloadStanding.Of(report.Path, component.Key, major, _logger);
            if (standing.Kind == PayloadStandingKind.MovedBack)
            {
                _logger?.LogInformation(
                    "prefix of appid {AppId} was prepared for proton {Prepared} and now runs on {Current} — Anvil does not act",
                    game.Game.AppId, standing.PreparedMajor, major);
                continue;
            }

            var mustRebuild = standing is { Kind: PayloadStandingKind.Stale, Revertable: true };
            if (!mustRebuild && PrefixMarkProbe.Detect(report.Path, component, []).Present)
            {
                // Preparation by someone else counts as done; warnings from the registry reader do not change that.
                if (standing.Kind == PayloadStandingKind.Stale)
                {
                    _logger?.LogWarning(
                        "prefix of appid {AppId} was prepared for proton {Prepared} without a complete rollback protocol — it cannot be rebuilt",
                        game.Game.AppId, standing.PreparedMajor);
                }

                continue;
            }

            if (!CanProvide(component, major))
            {
                // E-6: The reason already sits on the Modules page and in Dependencies.
                _logger?.LogInformation(
                    "prefix of appid {AppId} lacks {Key} {Version}, but it cannot be provided",
                    game.Game.AppId, component.Key, component.Version);
                continue;
            }

            missing.Add(required with { Component = component });
        }

        return missing;
    }

    /// <summary>
    /// Logs that this prefix's Proton version could not be parsed into a major, so Anvil does not
    /// prepare it — once per app id, since <see cref="MissingFor"/> runs on every refresh (Spec s8.1 E-9).
    /// </summary>
    private void ReportUnplaceable(GameProfile game)
    {
        lock (_reportedGate)
        {
            if (!_reportedUnplaceable.Add(game.Game.AppId))
            {
                return;
            }
        }

        _logger?.LogWarning(
            "prefix of appid {AppId} has proton version '{Version}', which Anvil cannot place — it does not prepare this prefix",
            game.Game.AppId, game.Proton?.Version);
    }

    /// <summary>
    /// The step for this game: build each component (or take the existing package), then apply
    /// it. The English sentence from builder or applier goes into the log; a failure's title and
    /// subtext come from the transaction.
    /// </summary>
    /// <param name="game">A game for which <see cref="MissingFor"/> reported something — so with
    /// a prefix, major version and tool.</param>
    /// <param name="missing">The result of <see cref="MissingFor"/>.</param>
    /// <summary>The sentence announcing the three minutes of the first build (design requirement 49).</summary>
    public const string LongRunHint = "first run, about three minutes";

    public SaveStep StepFor(GameProfile game, IReadOnlyList<RequiredPrefixComponent> missing)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(missing);

        var appId = game.Game.AppId;
        var report = PrefixReport.Of(game);
        var tool = game.Proton!.Tool!;

        var hint = HintFor(missing, report.ProtonMajor!.Value);

        return new SaveStep(StepLabel, NeedsSteamClosed: false, RunAsync: async reportStage =>
        {
            // E-7: A quit Anvil no longer runs any finally — the next build cleans up.
            _packages.RemoveStaleBuilds();

            foreach (var required in missing)
            {
                var component = required.Component;
                var built = await _build(
                    new BuildRequest(component, report.ProtonMajor!.Value, tool, _inventory.IngredientPaths(component)),
                    new StageReporter(reportStage),
                    CancellationToken.None).ConfigureAwait(false);
                if (built.Outcome is not (BuildOutcome.Built or BuildOutcome.AlreadyPresent) || built.Package is null)
                {
                    _logger?.LogWarning(
                        "prefix of appid {AppId}: build {Outcome} — {Message}", appId, built.Outcome, built.Message);
                    return SaveStepOutcome.Failed(Subject(component), built.Message);
                }

                // S4.2 §3.4: Only once the package is in hand is anything torn down — a build takes up to
                // three minutes and can fail. For a component without a change this is a NothingToDo.
                var reverted = await _revert(
                    new RevertRequest(component, report.Path!, tool, report.ProtonMajor!.Value), CancellationToken.None)
                    .ConfigureAwait(false);
                if (reverted.Outcome is not (RevertOutcome.Reverted or RevertOutcome.NothingToDo or RevertOutcome.NotRevertable))
                {
                    _logger?.LogWarning(
                        "prefix of appid {AppId}: revert {Outcome} — {Message}", appId, reverted.Outcome, reverted.Message);
                    return SaveStepOutcome.Failed(Subject(component), reverted.Message);
                }

                var applied = await _apply(
                    new ApplyRequest(required, report.Path!, built.Package, tool, report.ProtonMajor!.Value),
                    CancellationToken.None)
                    .ConfigureAwait(false);
                if (applied.Outcome is not (ApplyOutcome.Applied or ApplyOutcome.AlreadyPresent))
                {
                    _logger?.LogWarning(
                        "prefix of appid {AppId}: apply {Outcome} — {Message}", appId, applied.Outcome, applied.Message);
                    return SaveStepOutcome.Failed(Subject(component), applied.Message);
                }

                _logger?.LogInformation(
                    "prefix of appid {AppId} prepared: {Key} {Version} ({Outcome})",
                    appId, component.Key, component.Version, applied.Outcome);
            }

            return SaveStepOutcome.Done;
        }, appId, hint);
    }

    /// <summary>
    /// The hint for the progress bar — only when no package is yet ready for a requested
    /// component. Then the recipe's six stages lie ahead, measured at just under three minutes
    /// (Vision Wand Bridge §6.2). If every package is already there, applying takes one to two
    /// seconds, and there is nothing to announce.
    /// </summary>
    private string? HintFor(IReadOnlyList<RequiredPrefixComponent> missing, int protonMajor)
        => missing.Any(required => _packages.Find(
            PrefixPackageKey.For(required.Component.Key, required.Component.Version, protonMajor)) is null)
            ? LongRunHint
            : null;

    /// <summary>
    /// What the step failed on, for the progress bar's header line: <c>Preparing prefix failed ·
    /// dotnet 4.8</c>. The reason below it comes from the sentence from builder or applier — it
    /// is written for humans and names the thing plainly ("the package was not built: …").
    /// The wording is set out as design requirement 48.
    /// </summary>
    private static string Subject(PrefixComponent component)
        => $"{component.Key} {component.Version}";

    /// <summary>One step per game with missing components (E-2), in the games' order.</summary>
    public IReadOnlyList<SaveStep> StepsFor(IEnumerable<GameProfile> games, bool includingUnsaved)
    {
        ArgumentNullException.ThrowIfNull(games);

        var steps = new List<SaveStep>();
        foreach (var game in games)
        {
            var missing = MissingFor(game, includingUnsaved);
            if (missing.Count > 0)
            {
                steps.Add(StepFor(game, missing));
            }
        }

        return steps;
    }

    /// <summary>
    /// Providable means (E-6): a package for this major version is already there, or the
    /// version has a recipe and all ingredients are present.
    /// </summary>
    private bool CanProvide(PrefixComponent component, int major)
    {
        if (_packages.Find(PrefixPackageKey.For(component.Key, component.Version, major)) is not null)
        {
            return true;
        }

        if (component.Recipe is not { } recipe)
        {
            return false;
        }

        var paths = _inventory.IngredientPaths(component);
        return recipe.Ingredients.All(ingredient => paths.ContainsKey(ingredient.Slot));
    }

    /// <summary>
    /// Forwards the builder's stages as <see cref="StageProgress"/> — synchronously, without a
    /// SynchronizationContext, unlike <see cref="Progress{T}"/>.
    /// </summary>
    private sealed class StageReporter(Action<StageProgress> report) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value) => report(new StageProgress(value.Stage, value.StageCount));
    }
}
