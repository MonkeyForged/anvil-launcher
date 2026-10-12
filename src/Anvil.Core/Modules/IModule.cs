using Anvil.Core.Prefixes;
using Anvil.Core.Steam;

namespace Anvil.Core.Modules;

/// <summary>
/// The module contract v0 (S1.4): identity, the question "may you be switched off?", since S1.1
/// the question "may you be switched per game?", since S2.2 the header icon and the contribution
/// to the launch options, since S3.4 the question "am I globally ready?" and the contribution to
/// the Settings page.
/// The core only knows this contract, never a concrete module (Vision §3). The contract grows
/// only under load — the state badge follows with M4. "Globally active" is deliberately not a
/// member: that is state and lives in the settings (<see cref="ModuleRegistry"/>). The readiness
/// <b>finding</b> does not live here for the same reason, but in the registry as well — the
/// module only answers the question, it does not hold on to the answer.
/// </summary>
public interface IModule
{
    /// <summary>Stable key — at the same time settings section and log category.</summary>
    string Key { get; }

    /// <summary>Display name in the UI (English).</summary>
    string DisplayName { get; }

    /// <summary>One line for the Modules page (English).</summary>
    string Description { get; }

    /// <summary>Core entries carry the "Core" badge and cannot be switched off.</summary>
    bool IsCore { get; }

    /// <summary>
    /// May this module be switched on and off for a single game? (S1.1)
    ///
    /// <para>
    /// A <b>property of the module</b>, not state: it says whether the question "for this
    /// game?" makes sense at all — the answer to it lives in the settings and belongs to the
    /// <see cref="ModuleRegistry"/> (S1.2). Core modules are always on; for them the question
    /// is meaningless, and a per-game switch would be a lie.
    /// </para>
    /// </summary>
    bool IsPerGame { get; }

    /// <summary>
    /// The key of the icon in the header of the module card, or <c>null</c> — no icon (S2.2,
    /// the promise from plan S1.4 E-2 kept).
    ///
    /// <para>
    /// <b>The key, not the geometry</b> (Spec E-A/E-B): a <c>StreamGeometry</c> in the contract
    /// would be Avalonia in the core. The view resolves the key via
    /// <c>Views/ResourceLookup</c> in <c>Icons.axaml</c> — the same shape as
    /// <c>EmptyState.IconKey</c>.
    /// </para>
    ///
    /// <para><c>null</c> is the normal case: a module without a card needs no icon.</para>
    /// </summary>
    string? IconKey { get; }

    /// <summary>
    /// This module's section for a game, or <c>null</c> — the only member the contract gains in
    /// M4 (project plan M4, decision 1).
    ///
    /// <para>
    /// <b><c>object?</c>, not a type of its own</b> (Spec S4.6 E-A): a section <i>is</i> a
    /// ViewModel, and the core must not know any UI. An empty marker interface would only name
    /// the return type; the mapping to the view is done by the <c>DataTemplate</c> over the
    /// concrete type anyway, as with the pages in the main window.
    /// </para>
    ///
    /// <para>
    /// <c>null</c> is the normal case for modules without a row of their own in the GAME card —
    /// the launcher is exactly such a one.
    /// </para>
    /// </summary>
    object? CreateSection(GameProfile game);

    /// <summary>
    /// Is this module <b>globally</b> ready? (S3.4) Independent of the switch: a module that is
    /// switched on can still be unable to work if something is missing — this finding says
    /// what.
    ///
    /// <para>
    /// <b>Asynchronous, because the check may take time</b> (Vision §4.8): today, for Wand
    /// Bridge, it is a look into two folders and therefore fast — the contract must still be
    /// able to be slow, otherwise the first check that really computes rebuilds it.
    /// </para>
    ///
    /// <para>
    /// <b>No <see cref="GameProfile"/>:</b> the question is global, like "globally active".
    /// Whether it works <i>for this game</i> is a different question and is only asked in M4 at
    /// the module card — two forms of the same question in the same contract would be two truths
    /// about the same game.
    /// </para>
    ///
    /// <para>
    /// Who holds the finding and when it is checked is not decided by the module, but by the
    /// <see cref="ModuleRegistry"/> — the same division of labour as for "globally active".
    /// </para>
    /// </summary>
    Task<ModuleReadiness> CheckReadinessAsync(CancellationToken cancellationToken);

    /// <summary>
    /// This module's section for the Settings page, or <c>null</c> — the twin of
    /// <see cref="CreateSection"/> (S3.4, Spec E-8).
    ///
    /// <para>
    /// <b><c>object?</c>, not a type of its own</b>, for the same reason as
    /// <see cref="CreateSection"/>: a section <i>is</i> a ViewModel, and the core must not know
    /// any UI; the mapping to the view is done by the <c>DataTemplate</c> over the concrete
    /// type.
    /// </para>
    ///
    /// <para>
    /// <b>The core draws the card's frame</b> — chevron, title, key, badge —, the module only
    /// delivers the content. The same division of labour as for the module card of the right
    /// column (S1.4).
    /// </para>
    ///
    /// <para>
    /// <c>null</c> is the normal case: a module without settings of its own has no card.
    /// </para>
    /// </summary>
    object? CreateSettingsSection();

    /// <summary>
    /// What this module contributes to the launch options for this game — <b>an empty list
    /// means "nothing"</b> (S2.2).
    ///
    /// <para>
    /// <b>No module computes with <c>%command%</c></b> (Vision §4.4): a contribution only says
    /// <i>what</i> and <i>of which kind</i>; where it ends up in the string is decided solely by
    /// <see cref="Anvil.Core.Steam.LaunchOptionsComposer"/>.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="GameProfile"/>, not just the AppID:</b> the same shape as
    /// <see cref="CreateSection"/>. A module whose contribution depends on platform or compat
    /// tool needs more than the number — and two forms of the same question in the same
    /// contract would be two truths about "which game".
    /// </para>
    ///
    /// <para>
    /// <b>No <c>null</c> as an answer:</b> two ways for the same statement ("nothing") would
    /// have to be told apart by every caller.
    /// </para>
    /// </summary>
    IReadOnlyList<LaunchOptionContribution> LaunchOptionsFor(GameProfile game);

    /// <summary>
    /// Which components this module needs in this game's prefix — <b>an empty list means
    /// "none"</b> (S3.2), the same shape as <see cref="LaunchOptionsFor"/>.
    ///
    /// <para>
    /// <b>The module demands, it does not install.</b> A prefix belongs to a game, not to a
    /// module; the only writer on it is the core (Vision §4.3). Two modules that need the same
    /// thing name the same component without knowing of each other.
    /// </para>
    ///
    /// <para>
    /// <b>Whether a prefix is possible at all is not decided by the module.</b> That is answered
    /// by <c>PrefixReport.Of(game)</c> (S3.1) with <c>NotPossible</c> — a native game never gets
    /// one. Asking the same question here would be two truths.
    /// </para>
    ///
    /// <para><b>No <c>null</c> as an answer</b> — as with the launch options.</para>
    /// </summary>
    IReadOnlyList<PrefixComponent> PrefixComponentsFor(GameProfile game);

    /// <summary>
    /// Which components this module needs at all — globally, without a game (Spec S3.5b §3.1,
    /// E-1). <b>An empty list means "none"</b>, never <c>null</c>.
    ///
    /// <para>
    /// <b>Next to <see cref="PrefixComponentsFor"/>, not instead of it:</b> the
    /// <c>Dependencies</c> surface asks globally, <see cref="PrefixComponentsFor"/> remains the
    /// per-game path for S3.9. Handing an invented <see cref="GameProfile"/> to
    /// <see cref="PrefixComponentsFor"/> would be a lie in the contract (E-1, veto alternative
    /// there).
    /// </para>
    ///
    /// <para>
    /// <see cref="ModuleRegistry.ComponentDemands"/> combines this answer across all modules
    /// into one demand per key.
    /// </para>
    /// </summary>
    IReadOnlyList<PrefixComponent> RequiredComponents { get; }

    /// <summary>
    /// What this module wants started in the running game's container (Spec S4.4 §3.2).
    /// <see cref="AttachDeclaration.Nothing"/> is the answer of most modules.
    ///
    /// <para>
    /// <b>The answer may do work</b> (E-6): Wand Bridge unpacks about 160 MB here the first time.
    /// That a module answer may take time has been decided since S3.4
    /// (<see cref="CheckReadinessAsync"/>) — and exactly this work, which the core does not know,
    /// justifies a contract member (architecture.md §4.1).
    /// </para>
    ///
    /// <para>
    /// <b>The core does not learn what happens behind it.</b> It asks "what shall I start", not
    /// "unpack" — unpacking is the module's knowledge about its own archive.
    /// </para>
    ///
    /// <para>
    /// <b><paramref name="findGameProcessId"/></b> asks the core for the Windows pid of the game
    /// in the container; <c>null</c> = not found (spec s10.1 I-4). A module calls it only when
    /// it needs the pid (spec s10.1 E-3); not calling it costs nothing — the core only looks into
    /// the container when asked.
    /// </para>
    /// </summary>
    Task<AttachDeclaration> PrepareAttachAsync(
        GameProfile game,
        Func<CancellationToken, Task<int?>> findGameProcessId,
        CancellationToken cancellationToken);

    /// <summary>
    /// How this module recognises its <b>own</b> processes in the container (Spec S4.5 §3.5) —
    /// image names as <c>tasklist</c> calls them. <b>An empty list means "nothing of mine runs
    /// there"</b>, never <c>null</c>.
    ///
    /// <para>
    /// <b>A property, not a <c>Task</c></b> (E-4): unlike with
    /// <see cref="PrepareAttachAsync"/> there is nothing to work on here — an image name is a
    /// constant.
    /// </para>
    ///
    /// <para>
    /// <b>And not in <c>AttachProgram</c></b>: detaching happens without a prior attach. After a
    /// restart of Anvil no declaration is left, but the program in the game still is.
    /// </para>
    ///
    /// <para>
    /// <b>Only its own.</b> What comes into being with the program but belongs to the game's Wine
    /// world — under Proton the game itself often runs under <c>explorer.exe</c> — does not
    /// belong here: naming it would be the grip that drags the game down with it.
    /// </para>
    /// </summary>
    IReadOnlyList<string> AttachProcessImages { get; }

    /// <summary>
    /// Whether this module's program closes when asked (<c>taskkill</c> without <c>/F</c>)
    /// (s10.2 E-5). <c>true</c> is the normal case and the default: detaching asks first and
    /// forces only what outlasts the grace. <c>false</c>: the program hides or ignores the request
    /// and keeps running, so asking would only burn the grace — detaching forces it right away.
    ///
    /// <para>
    /// <b>A default member</b>, so a module that does nothing special says nothing. Like
    /// <see cref="AttachProcessImages"/> a constant, not a <c>Task</c>: it is measured once, not
    /// asked of the program.
    /// </para>
    /// </summary>
    bool ClosesOnRequest => true;

    /// <summary>
    /// Which folders this module wants carried <b>per prefix</b> (Spec S4.6 §3.2) — seeded from a
    /// template before attaching, harvested back there after a clean detach.
    /// <b>An empty list means "I carry none"</b>, never <c>null</c>; that is the answer of most
    /// modules.
    ///
    /// <para>
    /// <b>A property, not a <c>Task</c></b>: there is nothing to work on here, only to name —
    /// the same reasoning as for <see cref="AttachProcessImages"/>.
    /// </para>
    ///
    /// <para>
    /// <b>And not in <c>AttachProgram</c></b>: harvesting happens after detaching, and detaching
    /// happens without a prior attach — after a restart of Anvil no declaration is left, but the
    /// folder in the prefix still is.
    /// </para>
    ///
    /// <para>
    /// <b>The core never reads the content</b> (Vision §9). It counts entries and copies bytes.
    /// </para>
    /// </summary>
    IReadOnlyList<AttachStateFolder> AttachStateFolders { get; }

    /// <summary>
    /// This module's chip on the game card, or <c>null</c> — no chip (Spec S5.3 §3.2).
    /// The first of the two seam members earmarked in <c>architecture.md</c> §4.3.
    ///
    /// <para>
    /// <b>The core hands in its verdict, not its source.</b>
    /// <see cref="ModuleGameStates"/> stays core-internal (architecture.md §4.2): a module that
    /// held it itself could ask about foreign games and foreign modules. It gets the answer
    /// about <i>itself at this game</i>, nothing more.
    /// </para>
    ///
    /// <para>
    /// <b>The rule "if the module cannot take hold here, no chip" lives in the core</b>, not
    /// here: it is only asked at <see cref="ModuleGameStatus.Ready"/> and
    /// <see cref="ModuleGameStatus.Attached"/>. It applies to <i>every</i> module — if it lived
    /// in the module, every future one would have to write it anew, and one forgets it. Then a
    /// card would carry a chip for a native game, and its absence would no longer be a
    /// statement.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="GameProfile"/> next to the state</b>, not just the AppId — the same shape as
    /// <see cref="LaunchOptionsFor"/> and <see cref="CreateSection"/>: a module whose chip
    /// depended on the platform needs more than the number.
    /// </para>
    ///
    /// <para><c>null</c> is the normal case: most modules carry no chip.</para>
    /// </summary>
    ModuleChip? ChipFor(GameProfile game, ModuleGameState state);

    /// <summary>
    /// This module's button on the game card, or <c>null</c> — no button (Spec S5.4 §3.1). The
    /// second of the two seam members earmarked in <c>architecture.md</c> §4.3, literally in the
    /// shape of <see cref="ChipFor"/>: the core hands in its verdict, the module only words the
    /// label.
    ///
    /// <para>
    /// <b>Whether it is asked at all is decided by the core</b>, not by this member: it is only
    /// asked at <see cref="ModuleGameStatus.Ready"/> and <see cref="ModuleGameStatus.Attached"/>
    /// — the same filter rule as for <see cref="ChipFor"/> and for the same reason. If it lived
    /// in the module, every future one would have to write it anew, and one forgets it; then a
    /// card would carry a button for a native game.
    /// </para>
    ///
    /// <para>
    /// <b>Whether the button is operable is decided by the core</b>, not by the module: for
    /// <see cref="ModuleActionKind.Attach"/> <see cref="ModuleGameState.CanAttach"/>, for
    /// <see cref="ModuleActionKind.Detach"/> the inverse of <see cref="ModuleGameState.IsBusy"/>.
    /// This answer is not in the return value.
    /// </para>
    ///
    /// <para>
    /// <b>Which tone the button carries is decided by the core</b>, not by the module (E-3):
    /// unlike the chip, there is no tone in the return value here — an action is either the
    /// adding or its opposite, and the element description states the steel blue as a rule
    /// about <i>every</i> release. A module choosing a tone here would break it.
    /// </para>
    ///
    /// <para><c>null</c> is the normal case: the bridge modules answer; core modules return
    /// <c>null</c>.</para>
    /// </summary>
    ModuleAction? ActionFor(GameProfile game, ModuleGameState state);
}
