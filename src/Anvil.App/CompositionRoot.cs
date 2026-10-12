using System.Diagnostics;
using System.Reflection;
using Anvil.App.Components;
using Anvil.App.Modules;
using Anvil.App.ViewModels;
using Anvil.Core;
using Anvil.Core.Autostart;
using Anvil.Core.Backups;
using Anvil.Core.Installers;
using Anvil.Core.Logging;
using Anvil.Core.Modules;
using Anvil.Core.Packages;
using Anvil.Core.Prefixes;
using Anvil.Core.Processes;
using Anvil.Core.Sessions;
using Anvil.Core.Settings;
using Anvil.Core.Steam;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Anvil.App;

/// <summary>
/// The one place that wires Core services and modules together (Vision §3): modules get
/// Core services handed over, never the other way around. New modules are registered here as
/// <see cref="IModule"/> — nowhere else.
/// </summary>
public static class CompositionRoot
{
    public static ServiceProvider Build(StartupOptions options)
    {
        var services = new ServiceCollection();

        // Logging: one factory for everything; the provider owns and disposes it.
        services.AddSingleton(_ => AnvilLogging.Create());
        services.AddSingleton<ILoggerFactory>(
            provider => provider.GetRequiredService<AnvilLogging>().Factory);

        // The command line, read once (Spec S3.9 E-E): App fetches it from here, not from
        // desktop.Args — one truth.
        services.AddSingleton(options);

        // The one instance (S3.9): the socket is both lock and mailbox (Spec E-A). Resolving
        // it either connects or binds — one step, like SettingsStore.Load, not a loop; the
        // loop is started by App after the platform (E-F). The provider disposes the socket
        // at the end of Main like everything else.
        services.AddSingleton(provider => InstanceGuard.Acquire(
            XdgPaths.InstanceSocketPath(Environment.GetEnvironmentVariable),
            requestShow: !options.StartInTray,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.App)));

        // Settings: load once, one truth in the process. The file is only created on the
        // first save.
        services.AddSingleton(provider => new SettingsStore(
            XdgPaths.SettingsFilePath(Environment.GetEnvironmentVariable),
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Settings)));
        services.AddSingleton(
            provider => provider.GetRequiredService<SettingsStore>().Load());

        // Autostart (S3.6): the file mirrors the switch (Spec E-D). Exec points at the
        // program currently running (decision 6) — under 'dotnet App.dll' at the host plus
        // assembly (E-A, measured). Computed once during wiring; ProcessPath is never null
        // on Linux.
        services.AddSingleton(provider => new AutostartFile(
            XdgPaths.AutostartFilePath(Environment.GetEnvironmentVariable),
            AutostartEntry.ExecFor(
                Environment.ProcessPath!, Assembly.GetEntryAssembly()!.Location),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.App)));

        // File backup (S4.1): knows only files, logs under App (decision 13). In v1 it is
        // called only by the Core write path (S4.4); no module calls it itself. A fixed XDG
        // location — Core's own data does not move with the data root (Vision §4).
        services.AddSingleton(provider => new BackupStore(
            XdgPaths.BackupDirectory(Environment.GetEnvironmentVariable),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.App),
            TimeProvider.System));

        // The data root seam (S1.3): "give me my folder". The default root is resolved here
        // once — $XDG_DATA_HOME does not change at runtime; the *setting* does, and the seam
        // reads it fresh from AppSettings on every call.
        // A module never gets this service itself, only a delegate onto it:
        //     () => provider.GetRequiredService<ModuleDataFolders>().For("<key>")
        // Still resolved by no one — the first module that stores anything arrives in M2.
        services.AddSingleton(provider => new ModuleDataFolders(
            provider.GetRequiredService<AppSettings>(),
            XdgPaths.DataDirectory(Environment.GetEnvironmentVariable),
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Modules)));

        // The stores (S3.3): the one place where decision 9 lives — dependencies shared
        // under the data root (<root>/dependencies/), applications in the module folder
        // (<root>/<module key>/<folder>/). If the rule lived here per module, the second
        // module of the bridge family would copy it wrong. Log category like the data root
        // seam, because it is the same thing: where a module's data lives.
        // Saving comes as a delegate: the shared store's selection lives in the Core
        // settings and should survive a restart.
        // Since S3.4 it has consumers: the shared store via Dependencies (since S3.5b in
        // ComponentInventory below), Wand Bridge's own via ApplicationFor with the folder
        // name the module names itself (WandBridgeModule.ApplicationFolder = "wand"); the
        // Core never invents a name for a concrete module.
        services.AddSingleton(provider => new PackageStores(
            provider.GetRequiredService<ModuleDataFolders>(),
            provider.GetRequiredService<AppSettings>(),
            () => provider.GetRequiredService<SettingsStore>()
                .Save(provider.GetRequiredService<AppSettings>()),
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Modules)));

        // The download (S3.3a): runs and checks, but does not itself decide what is
        // downloaded — the catalog says that (Anvil.App.Modules.PackageCatalog). The
        // registration does not touch the network (Plan P-5): HttpPackageDownloader holds
        // its HttpClient in a Lazy<HttpClient> that is only created on the first download.
        // Consumers are the FetchProgressViewModels of the Dependencies page (Spec S3.5b
        // §3.5) — one per entry — and the one of the Wand version selection row (§3.7), all
        // through this one instance.
        services.AddSingleton<IPackageDownloader>(_ => new HttpPackageDownloader());
        services.AddSingleton(provider => new PackageFetcher(
            provider.GetRequiredService<IPackageDownloader>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The second narrow way out (architecture.md §4.3): a conditional document fetch. Lazy
        // like the package download — registering it does not touch the network. Disposed by
        // the container.
        services.AddSingleton(_ => new HttpDocumentFetcher());

        // Modules: the contract is the seam, the registry is the truth.
        services.AddSingleton<IModule, LauncherModule>();

        // Which folds of the right column are open, for as long as Anvil runs (s11.1, TD-32).
        // One instance for the Proton sections and the module cards: a fold opened on one game
        // stands open on the next, wherever it is drawn.
        services.AddSingleton<FoldMemory>();

        // The Core entry for Proton choice and launch options (S4.6). The order of these
        // registrations is the order on the Modules page and in the GAME card — the one place
        // it is set. The catalog comes as a delegate: after a rescan the new one applies.
        services.AddSingleton<IModule>(provider => new ProtonModule(
            provider.GetRequiredService<PendingChanges>(),
            () => provider.GetRequiredService<GameLibrary>().Current.CompatTools,
            // As a delegate, because the registry is only built further down from the
            // modules — it does not exist yet at this module's construction time.
            () => provider.GetRequiredService<ModuleRegistry>(),
            // The log category is the module key (as the contract states it) — this lets a
            // module's lines be filtered on the log page.
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("proton"),
            provider.GetRequiredService<FoldMemory>()));

        // The one truth about the current version and availability of components (Spec S3.5b
        // §3.3): Wand Bridge records its finding there, the Dependencies page reads it. The
        // version choice lives in AppSettings.Components and is saved immediately — like the
        // store selections; null deletes it.
        services.AddSingleton(provider => new ComponentInventory(
            PrefixComponentCatalog.All,
            provider.GetRequiredService<PackageStores>().Dependencies,
            PackageCatalog.SourceFor,
            key => provider.GetRequiredService<AppSettings>().Components.GetValueOrDefault(key),
            (key, version) =>
            {
                var settings = provider.GetRequiredService<AppSettings>();
                if (version is null)
                {
                    settings.Components.Remove(key);
                }
                else
                {
                    settings.Components[key] = version;
                }

                provider.GetRequiredService<SettingsStore>().Save(settings);
            },
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The one place that reads, stages, and writes the per-game options of an attaching
        // module (Spec S5.4 §3.3) — settingsFor follows the same pattern as PackageStores
        // further down (there for ApplicationFor): one ModuleSettings instance per module
        // key, built over the data root seam. A module never gets this service as an object,
        // only the delegates narrowed to its own key (Spec S5.4 §3.2) — below at
        // WandBridgeModule.
        services.AddSingleton(provider => new ModuleGameOptions(
            key => new ModuleSettings(
                () => provider.GetRequiredService<ModuleDataFolders>().For(key),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)),
            provider.GetRequiredService<PendingChanges>(),
            // Options that start out "on" per module (s10.1 I-5): the Core never names a module,
            // so the root fills the table. CE Bridge opens the game by default.
            new Dictionary<string, IReadOnlySet<ModuleGameOption>>
            {
                [CeBridgeModule.ModuleKey] = CeBridgeModule.OptionsOnByDefault,
            },
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The first module that is not Core (S1.1), and since S3.4 the first with a real
        // readiness finding: it gets the inventory and its own store handed over, not found
        // itself (Vision §3) — since S3.5b the inventory instead of the shared store (Spec
        // §3.4); its own store via ApplicationFor with the folder name it names itself. It is
        // registered after Proton, because the order of these registrations is the order on
        // the Modules page.
        services.AddSingleton<IModule>(provider => new WandBridgeModule(
            provider.GetRequiredService<ComponentInventory>(),
            provider.GetRequiredService<PackageStores>().ApplicationFor(
                WandBridgeModule.ModuleKey, WandBridgeModule.ApplicationFolder),
            // The download and the path onto the UI thread for fetching the vendor builds
            // in the Wand version selection row (Spec S3.5b §3.7) — the same instance as in
            // Dependencies.
            provider.GetRequiredService<PackageFetcher>(),
            action => Dispatcher.UIThread.Post(action),
            // The coupling (Spec §3.7): a choice in the settings card triggers exactly this
            // one check, and its event flips the badge and the module card. As a lambda that
            // only fetches the registry on call — it is built further down from the modules,
            // it does not exist yet at this module's construction time; the same cycle
            // resolution as ProtonModule with its Func<ModuleRegistry>.
            // Fire-and-forget is safe here: CheckAsync already catches the module's own
            // exceptions (Spec E-7) and records the finding in the registry; nobody waits
            // for the result, because the path runs through ReadinessChanged.
            () => _ = provider.GetRequiredService<ModuleRegistry>()
                .CheckAsync(WandBridgeModule.ModuleKey),
            // The desktop opens the folder, not the module — as with the log folder (S3.7).
            DesktopShell.OpenFolder,
            // Its own module folder as a delegate (architecture.md §4.1): the module unpacks
            // its application there (Spec S4.4 §3.1). A delegate, because the data root can
            // be changed at runtime — changing it moves no data.
            () => provider.GetRequiredService<ModuleDataFolders>().For(WandBridgeModule.ModuleKey),
            // The three narrowed delegates onto ModuleGameOptions (Spec S5.4 E-8, see the
            // constructor comment of WandBridgeModule): the card section shows the pending
            // choice (Displayed), the chip asks what applies (Saved), checking the box
            // stages a pending choice (Stage) — all three bound fixedly to ModuleKey, so this
            // module never reaches another module's switches.
            (appId, option) => provider.GetRequiredService<ModuleGameOptions>()
                .Displayed(WandBridgeModule.ModuleKey, appId, option),
            (appId, option) => provider.GetRequiredService<ModuleGameOptions>()
                .Saved(WandBridgeModule.ModuleKey, appId, option),
            (appId, option, enabled) => provider.GetRequiredService<ModuleGameOptions>()
                .Stage(WandBridgeModule.ModuleKey, appId, option, enabled),
            // Only the ability to fetch one document conditionally, not the fetcher itself
            // (architecture.md §8, "hand over as little as possible").
            provider.GetRequiredService<HttpDocumentFetcher>().FetchAsync,
            // The log category is the module key (as the contract states it), as with Proton.
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(WandBridgeModule.ModuleKey)));

        // The second bridge module (s9.1), registered right after Wand Bridge: the order of these
        // registrations is the order on the Modules page. Its store and its settings share one
        // folder, <data root>/ce_bridge/, resolved anew on every access (architecture.md §4.1).
        services.AddSingleton<IModule>(provider => new CeBridgeModule(
            new CeReleaseStore(
                () => provider.GetRequiredService<ModuleDataFolders>().For(CeBridgeModule.ModuleKey),
                // Its own ModuleSettings over the same folder: the choice lives in that folder's
                // settings.json, next to the releases, like the stores of Wand Bridge.
                new ModuleSettings(
                    () => provider.GetRequiredService<ModuleDataFolders>().For(CeBridgeModule.ModuleKey),
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(CeBridgeModule.ModuleKey)),
            // The download and the path onto the UI thread for the selection row's progress — the
            // same fetcher instance as in Dependencies and Wand Bridge.
            provider.GetRequiredService<PackageFetcher>(),
            action => Dispatcher.UIThread.Post(action),
            // The coupling, as at Wand Bridge: a choice in the settings card triggers exactly this
            // one check, fire-and-forget because CheckAsync records the finding in the registry
            // and catches the module's own exceptions. A lambda that fetches the registry on call:
            // it is built further down from the modules and does not exist yet here.
            () => _ = provider.GetRequiredService<ModuleRegistry>()
                .CheckAsync(CeBridgeModule.ModuleKey),
            // The desktop opens the folder, not the module — as with the log folder (S3.7).
            DesktopShell.OpenFolder,
            // Getting CE (s9.3 I-3): four narrow delegates instead of the services, each resolved
            // on call. The question goes up on the settings page's one confirmation — the module
            // asks, the Core draws. bwrap is looked up on every call, because the settings card
            // asks again whenever the page is shown (spec E-8). The Proton comes from the current
            // snapshot, which a rescan swaps. The cage is the Core's one instance.
            request => provider.GetRequiredService<SettingsConfirmationViewModel>().AskAsync(request),
            () => Bubblewrap.Find(Environment.GetEnvironmentVariable("PATH")) is not null,
            () => CagedInstaller.ChooseProton(provider.GetRequiredService<GameLibrary>().Current.CompatTools),
            (request, token) => provider.GetRequiredService<CagedInstaller>().RunAsync(request, token),
            // The three narrowed option delegates, as at Wand Bridge: shown (pending choice),
            // saved (what applies), stage — all bound fixedly to this module's key.
            (appId, option) => provider.GetRequiredService<ModuleGameOptions>()
                .Displayed(CeBridgeModule.ModuleKey, appId, option),
            (appId, option) => provider.GetRequiredService<ModuleGameOptions>()
                .Saved(CeBridgeModule.ModuleKey, appId, option),
            (appId, option, enabled) => provider.GetRequiredService<ModuleGameOptions>()
                .Stage(CeBridgeModule.ModuleKey, appId, option, enabled),
            // The log category is the module key, as the contract states it.
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(CeBridgeModule.ModuleKey)));

        services.AddSingleton(provider => new ModuleRegistry(
            provider.GetServices<IModule>(),
            provider.GetRequiredService<AppSettings>(),
            provider.GetRequiredService<SettingsStore>(),
            // S3.8 E-3: the registry stages per-game switches here. No cycle at construction —
            // PendingChanges only resolves the registry in the saveSwitches lambda, i.e. on save.
            provider.GetRequiredService<PendingChanges>(),
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Modules)));

        // The plumbing from S3.5 and S3.6 runs in the application from S3.9 on. Packages live
        // under the one data root (<root>/packages/) — as a delegate, because the user can
        // change it.
        services.AddSingleton(provider => new PrefixPackageStore(
            () => provider.GetRequiredService<ModuleDataFolders>().Root,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The seam to the process, shared by the builder and the applier — both start Wine,
        // neither needs its own runner.
        services.AddSingleton<IProcessRunner>(provider => new ProcessRunner(
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The builder (S3.5) and the applier (S3.6) get the base environment handed over,
        // never read themselves (Plan S3.5 P-1). Taken once on resolution: the own process's
        // environment does not change at runtime.
        services.AddSingleton(provider => new PrefixPackageBuilder(
            provider.GetRequiredService<PrefixPackageStore>(),
            provider.GetRequiredService<IProcessRunner>(),
            ProtonWine.Current(),
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));
        services.AddSingleton(provider => new PrefixPayloadApplier(
            provider.GetRequiredService<IProcessRunner>(),
            ProtonWine.Current(),
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));
        services.AddSingleton(provider => new PrefixPayloadReverter(
            provider.GetRequiredService<IProcessRunner>(),
            ProtonWine.Current(),
            logger: provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The cage for a foreign installer (s9.3, architecture.md §6.2): the same runner and the
        // same base environment as the builder, handed over, never read inside. bwrap is looked
        // up on every run, not once — the user may install it while Anvil runs. A module never
        // gets this service, only its RunAsync as a delegate (above at CE Bridge).
        services.AddSingleton(provider => new CagedInstaller(
            () => Bubblewrap.Find(Environment.GetEnvironmentVariable("PATH")),
            provider.GetRequiredService<IProcessRunner>(),
            ProtonWine.Current(),
            path => File.Exists(path) || Directory.Exists(path),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The prefix step (Spec S3.9 E-1): not a contract member — the Core derives it from
        // the modules' demand. Everything behind delegates is only resolved on call: the
        // registry needs PendingChanges, and PendingChanges needs the steps from here when
        // saving. "Is the game running?" is answered by the watcher (E-8) — one truth per thing.
        services.AddSingleton(provider => new PrefixPreparer(
            (game, includingUnsaved) => includingUnsaved
                ? provider.GetRequiredService<ModuleRegistry>().PrefixComponentRequestsForIncludingUnsaved(game)
                : provider.GetRequiredService<ModuleRegistry>().PrefixComponentRequestsFor(game),
            provider.GetRequiredService<ComponentInventory>(),
            provider.GetRequiredService<PrefixPackageStore>(),
            (request, progress, token) => provider.GetRequiredService<PrefixPackageBuilder>()
                .BuildAsync(request, progress, token),
            (request, token) => provider.GetRequiredService<PrefixPayloadApplier>().ApplyAsync(request, token),
            (request, token) => provider.GetRequiredService<PrefixPayloadReverter>().RevertAsync(request, token),
            appId => provider.GetRequiredService<GameSessionWatcher>().For(appId) is not null,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));

        // The open preparation (Spec S3.9 §3.2), saved state (E-4). Triggered by the library
        // (build, rescan, session end) and from here: new installers in the inventory and the
        // global switch. Fire-and-forget is safe — RefreshAsync never throws. No cycle at
        // construction: the registry needs PendingChanges, and PendingChanges resolves
        // nothing at construction, only inside its delegates.
        services.AddSingleton(provider =>
        {
            var work = new PrefixWork(
                game => provider.GetRequiredService<PrefixPreparer>().MissingFor(game, includingUnsaved: false),
                () => provider.GetRequiredService<GameLibrary>().Current,
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules));
            provider.GetRequiredService<ComponentInventory>().AvailabilityChanged += (_, _) => _ = work.RefreshAsync();
            provider.GetRequiredService<ModuleRegistry>().EnabledChanged += (_, _) => _ = work.RefreshAsync();
            return work;
        });

        // Steam: one truth about "which games exist". GameLibrary holds the snapshot and
        // swaps it on rescan; the scanner comes in as a delegate. A separate SteamSnapshot
        // registration would create a second truth.
        services.AddSingleton(provider => new SteamScanner(
            Environment.GetEnvironmentVariable,
            provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Steam)));
        services.AddSingleton(provider => new GameLibrary(
            provider.GetRequiredService<SteamScanner>().Scan));

        // Run status: the one truth about "what is running right now" (S3.1). The scanner
        // reads /proc, the watcher ticks — Program.cs starts and stops the loop (S3.3).
        services.AddSingleton(_ => GameSessionScanner.ForCurrentSystem());
        services.AddSingleton(provider => new GameSessionWatcher(
            provider.GetRequiredService<GameSessionScanner>().Scan,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Sessions)));

        // The session seam "is attachable" (S4.3). It sits next to the watcher, not inside
        // it: a synchronous /proc tick and an asynchronous bus query do not belong in the
        // same loop (Spec S4.3 E-1). The watcher comes in as a delegate — this does not
        // create a second truth about "what is running".
        services.AddSingleton(provider => new AttachWaiter(
            provider.GetRequiredService<GameSessionWatcher>().IsRunning,
            (appId, cancellationToken) => SessionBusNameProbe.IsOwnedAsync(
                SessionBusNameProbe.SessionBusAddress,
                SteamBusName.ForApp(appId),
                provider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(LogCategories.Sessions),
                cancellationToken),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Sessions)));

        // The attacher (S5.1): a bracket around seeding, attaching, detaching and harvesting,
        // and the one place that knows what is currently attached. Here — and only here —
        // are the four tools from M4 wired together; until S5.1 this wiring only existed in
        // the live test.
        //
        // Nobody calls it yet: the triggers (button, context menu, autostart) are S5.4. It is
        // still registered here, because "four places to forget" arise exactly when every
        // caller wires it up itself.
        services.AddSingleton(provider =>
        {
            var registry = provider.GetRequiredService<ModuleRegistry>();
            var logger = provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Sessions);
            var relay = new AttachStateRelay(logger);

            // Steam's launch client depends on the Steam root, which only the snapshot
            // knows. As a delegate, not looked up once at startup: a rescan can happen
            // between two attaches.
            string? FindLaunchClient()
                => provider.GetRequiredService<GameLibrary>().Current.RootPath is { } root
                    ? SteamLaunchClient.Find(root)
                    : null;

            var starter = new AttachStarter(
                FindLaunchClient,
                provider.GetRequiredService<AttachWaiter>().WaitAsync,
                File.Exists,
                info =>
                {
                    var process = Process.Start(info)
                        ?? throw new InvalidOperationException(
                            "Process.Start returned no process.");
                    return new StartedProcess(
                        process.Id,
                        () => process.HasExited,
                        () => process.HasExited ? process.ExitCode : null);
                },
                logger);

            var detacher = new Detacher(
                FindLaunchClient,
                (info, token) => ContainerCommand.RunAsync(info, Detacher.CommandLimit, token),
                logger);

            // s10.1: the game's pid in the container, asked only when a module needs it. Same
            // launch client and command runner as the detacher next to it. The finder leaves out
            // the programs of all modules, not only the asking one's (s10.2 E-2): another
            // bridge's program may be large and must never become "the game".
            var finder = new GameProcessFinder(
                FindLaunchClient,
                (info, token) => ContainerCommand.RunAsync(info, Detacher.CommandLimit, token),
                provider.GetRequiredService<AttachWaiter>().WaitAsync,
                logger);
            var sessions = provider.GetRequiredService<GameSessionWatcher>();

            return new ModuleAttacher(
                (game, key, findGameProcessId, token)
                    => registry.PrepareAttachAsync(game, key, findGameProcessId, token),
                (appId, _, token) => finder.FindAsync(
                    appId,
                    sessions.For(appId)?.Program?.FileName,
                    registry.AllAttachProcessImages(),
                    token),
                registry.AttachProcessImagesOf,
                registry.AttachStateFoldersOf,
                relay.Seed,
                relay.Harvest,
                starter.StartAsync,
                registry.ClosesOnRequestOf,
                detacher.DetachAsync,
                logger);
        });

        // The one truth about "how does this module stand for this game" (S5.2). The chip
        // (S5.3) and the module card badge (S5.4) read from it instead of judging themselves.
        services.AddSingleton(provider => new ModuleGameStates(
            provider.GetRequiredService<ModuleRegistry>(),
            provider.GetRequiredService<ModuleAttacher>(),
            provider.GetRequiredService<GameSessionWatcher>(),
            // The profile as a delegate: a rescan replaces the library (architecture.md §4.1).
            appId => provider.GetRequiredService<GameLibrary>().Current.Games
                .FirstOrDefault(game => game.Game.AppId == appId)));

        // S5.4: the thin layer over the attacher — it translates an action into the matching
        // call, catches every exception, and writes the one log line with the outcome.
        services.AddSingleton(provider => new ModuleActionRunner(
            provider.GetRequiredService<ModuleAttacher>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules),
            // s8.1: attaching into a prefix without its components breaks the module — the
            // registry answers "prepared" with the same probe the preparer uses.
            provider.GetRequiredService<ModuleRegistry>().IsPreparedFor));

        // S5.4 §3.8: the two triggers — attach on game start, detach on game end. They hook
        // onto the watcher in the constructor; a singleton nobody requests is never built and
        // never listens. It is registered here like any other wiring; it is only requested
        // via ResolveAttachTriggers below, because Build only wires things together and runs
        // nothing (Spec E-A) — Program.Main calls it before the watcher's first pass,
        // otherwise this story would have no effect.
        services.AddSingleton(provider => new AttachTriggers(
            provider.GetRequiredService<GameSessionWatcher>(),
            provider.GetRequiredService<ModuleRegistry>(),
            provider.GetRequiredService<ModuleGameStates>(),
            provider.GetRequiredService<ModuleAttacher>(),
            provider.GetRequiredService<ModuleGameOptions>(),
            provider.GetRequiredService<ModuleActionRunner>(),
            // The profile as a delegate, as with ModuleGameStates above: a rescan replaces
            // the library (architecture.md §4.1).
            appId => provider.GetRequiredService<GameLibrary>().Current.Games
                .FirstOrDefault(game => game.Game.AppId == appId),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Sessions)));

        // The launcher starts and stops a game (S3.3); the watcher remains the one truth
        // about "is it running".
        services.AddSingleton(_ => SteamOverlayFinder.ForCurrentSystem());
        services.AddSingleton(provider => new GameLauncher(
            // The stop needs the full process list — everything hanging off Steam's launch
            // bracket. The watcher above deliberately gets the cheap pass: for "is it
            // running" the bracket itself is enough.
            provider.GetRequiredService<GameSessionScanner>().ScanWithProcessTree,
            // "Is it running?" is answered by the watcher — one truth per thing. Without
            // this, every click would run a full /proc pass on the UI thread.
            provider.GetRequiredService<GameSessionWatcher>().For,
            provider.GetRequiredService<GameSessionWatcher>().WaitForEndAsync,
            provider.GetRequiredService<SteamOverlayFinder>().Pids,
            // Steam's launch client depends on the Steam root, which only the snapshot
            // knows. Looked up once at startup (Spec E-C): if Steam is installed later, the
            // gentle stop path is missing until the next Anvil start — on a machine with
            // Steam autostart, a case that does not occur.
            provider.GetRequiredService<GameLibrary>().Current.RootPath is { } steamRoot
                ? SteamLaunchClient.Find(steamRoot)
                : null,
            // Whether the gentle path can reach the game is known by the library (Spec
            // S3.3b E-A): a native game has no wineserver, even if Steam gives it a launcher
            // service. Looked up at stop time, not at start — after a rescan the new state
            // applies (E-D).
            appId => provider.GetRequiredService<GameLibrary>().Current.Games
                .FirstOrDefault(profile => profile.Game.AppId == appId)?.Platform,
            logger: provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Sessions)));

        // Steam as a process (S4.2): a probe over /proc and the stop/start flow. In v1 only
        // the transaction calls it (S4.4); nobody resolves it at startup. The command is
        // looked up on every call (Spec S4.2 E-J) — it depends on PATH and on the root of the
        // current snapshot, and the lookup costs a few stat calls.
        services.AddSingleton(_ => SteamProcessProbe.ForCurrentSystem());
        services.AddSingleton(provider => new SteamProcess(
            provider.GetRequiredService<SteamProcessProbe>().Look,
            () => SteamCommand.Find(
                Environment.GetEnvironmentVariable,
                provider.GetRequiredService<GameLibrary>().Current.RootPath),
            logger: provider.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.Steam)));

        // Writing Steam's files (S4.3): pending changes into config.vdf and localconfig.vdf.
        // In v1 only the transaction calls it (S4.4), handing it its backup delegate — the
        // journal seam (Spec S4.3 E-I). Nobody resolves it at startup.
        services.AddSingleton(provider => new SteamConfigWriter(
            () => provider.GetRequiredService<GameLibrary>().Current,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Steam)));

        // The write operation as a whole (S4.4): a note on disk plus the flow that chains
        // the four seams in a fixed order. The transaction knows no class, only "stop",
        // "start", "write", "is a game running" and "read fresh". Apply and Rescan come in
        // as method groups; Stop/Start as lambdas, because their optional CancellationToken
        // would otherwise make a method group unusable — the optional parameter belongs to
        // SteamProcess, not to the transaction's contract (E-C).
        services.AddSingleton(provider => new TransactionJournal(
            XdgPaths.TransactionFilePath(Environment.GetEnvironmentVariable),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Steam),
            TimeProvider.System));

        services.AddSingleton(provider => new SteamTransaction(
            provider.GetRequiredService<TransactionJournal>(),
            provider.GetRequiredService<BackupStore>(),
            () => provider.GetRequiredService<SteamProcess>().StopAsync(),
            () => provider.GetRequiredService<SteamProcess>().StartAsync(),
            provider.GetRequiredService<SteamConfigWriter>().Apply,
            // "Is a game running?" is answered by the watcher — one truth per thing
            // (decision 4). Specifically *any* game: steam -shutdown ends them all
            // (decision 5).
            () => provider.GetRequiredService<GameSessionWatcher>().Current.Count > 0,
            provider.GetRequiredService<GameLibrary>().Rescan,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Steam)));

        // The collection of unsaved pending changes (S4.5): the one truth about "what is
        // open". It filters out against the current snapshot while collecting and hands off
        // to the transaction when saving — SaveAsync as a lambda, because the optional
        // progress parameter makes the method group unusable (finding from S4.4). Since S3.8
        // it additionally holds the per-game switches and writes them through the registry.
        services.AddSingleton(provider => new PendingChanges(
            () => provider.GetRequiredService<GameLibrary>().Current,
            (changes, origins, progress, steps) => provider.GetRequiredService<SteamTransaction>()
                .SaveAsync(changes, origins, progress, steps),
            // Deferred through the provider: the registry itself gets PendingChanges in S3.8 (Spec E-3).
            switches => provider.GetRequiredService<ModuleRegistry>().SaveEnabledFor(switches),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Steam),
            // Since S3.9 (§3.3): one prefix step per game in the set, including unsaved ones
            // (E-4) — whoever flips a switch saves the preparation along with it.
            prefixSteps: appIds =>
            {
                var games = provider.GetRequiredService<GameLibrary>().Current.Games;
                return provider.GetRequiredService<PrefixPreparer>().StepsFor(
                    appIds.Select(appId => games.FirstOrDefault(game => game.Game.AppId == appId))
                        .OfType<GameProfile>(),
                    includingUnsaved: true);
            },
            // S5.4 §3.4: the same pending-choice↔service cycle as saveSwitches above —
            // ModuleGameOptions itself gets PendingChanges handed over, so it is only
            // resolved through the provider when saving, not at construction.
            saveOptions: options => provider.GetRequiredService<ModuleGameOptions>().Save(options)));

        // The start and stop phases of the UI (S4.5): factored out of LibraryViewModel. It
        // knows no card — it reports, the library carries the badge.
        services.AddSingleton(provider => new GameRunController(
            provider.GetRequiredService<GameSessionWatcher>(),
            provider.GetRequiredService<GameLauncher>().Start,
            // S5.4 §8.4 (E-12): Anvil never tears down a container it has something attached
            // to itself — detach first, then stop. The stop goes through wineserver -k and
            // takes Wand, the launcher service, and the whole container with it; after that a
            // detach only finds a dead bus name, and this session's WeMod login is lost
            // (measured on 2026-09-19).
            (appId, token) => DetachThenStopAsync(
                id => provider.GetRequiredService<GameLibrary>().Current.Games
                    .FirstOrDefault(game => game.Game.AppId == id),
                provider.GetRequiredService<ModuleActionRunner>().DetachAllAsync,
                provider.GetRequiredService<GameLauncher>().StopAsync,
                appId,
                token),
            action => Dispatcher.UIThread.Post(action),
            TimeProvider.System,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Sessions)));

        // The save bar (S4.5): the one place for everything the write operation has to say.
        // The transaction comes in through three delegates; RollBackAsync as a lambda, for
        // the same reason as SaveAsync above. It depends on the transaction, but not on the
        // library — the rescan runs through the Saved event, otherwise it would be a cycle.
        services.AddSingleton(provider => new SaveBarViewModel(
            provider.GetRequiredService<PendingChanges>(),
            provider.GetRequiredService<GameSessionWatcher>(),
            appId => provider.GetRequiredService<GameLibrary>().Current.Games
                .FirstOrDefault(profile => profile.Game.AppId == appId)?.Game.Name,
            provider.GetRequiredService<SteamTransaction>().PendingRecovery,
            progress => provider.GetRequiredService<SteamTransaction>().RollBackAsync(progress),
            provider.GetRequiredService<SteamTransaction>().Dismiss,
            action => Dispatcher.UIThread.Post(action),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui),
            // Since S3.9 (§3.2): the bar shows the open preparation as its own state, and
            // Prepare saves without pending changes — only the prefix steps, saved state (E-4).
            work: provider.GetRequiredService<PrefixWork>(),
            prepare: progress => provider.GetRequiredService<SteamTransaction>().SaveAsync(
                [],
                [],
                progress,
                provider.GetRequiredService<PrefixPreparer>().StepsFor(
                    provider.GetRequiredService<PrefixWork>().Items.Select(item => item.Game),
                    includingUnsaved: false)),
            // Since S3.9 (E-15): Discard stages "off" for every requesting module — only for
            // modules with a per-game switch (IsPerGame); StageEnabledFor raises
            // EnabledForGameChanged in the process, through which the launch options field
            // synchronously stages the removal of the launch option. The bar itself then
            // saves immediately.
            turnOff: items =>
            {
                var registry = provider.GetRequiredService<ModuleRegistry>();
                foreach (var item in items)
                {
                    foreach (var key in item.Missing.SelectMany(missing => missing.RequestedBy).Distinct())
                    {
                        if (registry.Modules.First(module => module.Key == key).IsPerGame)
                        {
                            registry.StageEnabledFor(key, item.Game.Game.AppId, false);
                        }
                    }
                }
            }));

        // The backups list (S4.7): the backup service, the snapshot as a delegate (after a
        // rescan the new one applies), the restore as a lambda — the optional progress
        // parameter makes the method group unusable (finding from S4.4). It shows the
        // operation in the bar instead of drawing its own progress.
        services.AddSingleton(provider => new BackupsViewModel(
            provider.GetRequiredService<BackupStore>(),
            () => provider.GetRequiredService<GameLibrary>().Current,
            (backup, target, takenAt, progress) => provider.GetRequiredService<SteamTransaction>()
                .RestoreAsync(backup, target, takenAt, progress),
            provider.GetRequiredService<GameSessionWatcher>(),
            provider.GetRequiredService<SaveBarViewModel>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui)));

        // The settings page's confirmation (s9.3, design 61): one instance, drawn by the page; a
        // module that has to ask gets a delegate to AskAsync, never the instance.
        services.AddSingleton<SettingsConfirmationViewModel>();

        // The Dependencies page (Spec S3.5b §3.6): one entry per demand from the registry,
        // each component once. Singleton, because the module cards' demand rows point to
        // exactly this instance (Show in Dependencies). A module's check is triggered after
        // rereading — fire-and-forget as with Wand Bridge above: CheckAsync catches the
        // module's own exceptions, the result runs through ReadinessChanged.
        services.AddSingleton(provider =>
        {
            var registry = provider.GetRequiredService<ModuleRegistry>();
            var inventory = provider.GetRequiredService<ComponentInventory>();
            var entries = registry.ComponentDemands()
                .Select(demand => new DependencyEntryViewModel(
                    demand,
                    demand.ModuleKeys
                        .Select(key => registry.Modules.First(module => module.Key == key).DisplayName)
                        .ToList(),
                    inventory,
                    PackageCatalog.SourceFor,
                    new FetchProgressViewModel(
                        provider.GetRequiredService<PackageFetcher>(),
                        action => Dispatcher.UIThread.Post(action),
                        provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)),
                    action => Dispatcher.UIThread.Post(action),
                    key => _ = registry.CheckAsync(key)))
                .ToList();

            return new DependenciesViewModel(
                entries,
                // Re-resolved on every open: the data root can change at runtime (E-10).
                () => provider.GetRequiredService<PackageStores>().Dependencies.Folder,
                DesktopShell.OpenFolder,
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui));
        });

        // The shell's pages in sidebar order; MainWindowViewModel gets them handed over as a
        // list and looks up nothing itself.
        services.AddSingleton<PageViewModel>(provider =>
        {
            // The loader belongs to the app, not the Core: it turns the found path into an
            // image and catches everything along the way (Spec §7).
            var loader = new ArtworkLoader(
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui));

            return new LibraryViewModel(
                provider.GetRequiredService<GameLibrary>(),
                loader.Load,
                provider.GetRequiredService<AppSettings>(),
                provider.GetRequiredService<SettingsStore>(),
                // The library opens folders *and* Steam addresses (S3.4) — hence Open, not
                // OpenFolder. The log folder below keeps its descriptive name.
                DesktopShell.Open,
                // S3.8: "Show in Steam" raises the window itself afterward — KWin refuses
                // Steam's own request to do so.
                SteamWindow.Raise,
                provider.GetRequiredService<GameSessionWatcher>(),
                provider.GetRequiredService<GameRunController>(),
                provider.GetRequiredService<SaveBarViewModel>(),
                provider.GetRequiredService<ModuleRegistry>(),
                // S5.3: the one truth about "how does this module stand for this game" — the
                // game cards' chips read from it instead of judging themselves.
                provider.GetRequiredService<ModuleGameStates>(),
                // S5.4: the button on the module card does not call the attacher itself —
                // it says "game, module, kind", and the runner knows how (architecture.md §4.2).
                (game, moduleKey, kind) => provider.GetRequiredService<ModuleActionRunner>()
                    .RunAsync(game, moduleKey, kind, CancellationToken.None),
                action => Dispatcher.UIThread.Post(action),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui),
                // S3.9: the library triggers the open preparation where the actual state changes.
                prefixWork: provider.GetRequiredService<PrefixWork>(),
                // s8.1: a refused attach switches the module off, and the page redraws.
                onAttachRefused: handler => provider.GetRequiredService<ModuleActionRunner>().AttachRefused += handler,
                // s11.1: the same fold memory as the Proton sections; the wishes give the options
                // fold of every module card its unsaved dot.
                folds: provider.GetRequiredService<FoldMemory>(),
                changes: provider.GetRequiredService<PendingChanges>());
        });
        services.AddSingleton<PageViewModel>(provider => new ModulesViewModel(
            provider.GetRequiredService<ModuleRegistry>(),
            action => Dispatcher.UIThread.Post(action),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Modules)));
        services.AddSingleton<PageViewModel>(provider => new SettingsViewModel(
            provider.GetRequiredService<AppSettings>(),
            provider.GetRequiredService<SettingsStore>(),
            XdgPaths.DataDirectory(Environment.GetEnvironmentVariable),
            () => FolderPicker.PickAsync("Data location"),
            provider.GetRequiredService<AutostartFile>().Sync,
            provider.GetRequiredService<BackupsViewModel>(),
            provider.GetRequiredService<DependenciesViewModel>(),
            // The Composition Root builds the module cards, not the page (S3.4): the
            // SettingsViewModel knows no module, and the order is that of the registrations —
            // the same as on the Modules page and in the right column. Modules without a
            // section and without demand fall out here. The Core draws the demand rows (Spec
            // S3.5b §3.7, E-5); the link only fetches the Dependencies page on click.
            ModuleSettingsCardViewModel.CardsFor(
                provider.GetRequiredService<ModuleRegistry>().Modules,
                module => module.RequiredComponents
                    .Select(component => new NeedLineViewModel(
                        component,
                        provider.GetRequiredService<ComponentInventory>(),
                        action => Dispatcher.UIThread.Post(action),
                        () => provider.GetRequiredService<DependenciesViewModel>().Reveal()))
                    .ToList(),
                // The card's tag tone reads the same switch as the Modules page: the registry.
                provider.GetRequiredService<ModuleRegistry>().IsEnabled),
            provider.GetRequiredService<SettingsConfirmationViewModel>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Settings)));
        services.AddSingleton<PageViewModel>(provider =>
        {
            var logging = provider.GetRequiredService<AnvilLogging>();
            return new LogViewModel(
                logging.RingBuffer,
                logging.LogDirectory,
                action => Dispatcher.UIThread.Post(action),
                DesktopShell.OpenFolder,
                provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui));
        });
        // About (story s14.1). The clipboard belongs to a window, and the main window is built
        // after this view model — so it is looked up when the user copies, not now.
        services.AddSingleton(provider => new AboutViewModel(
            text => provider.GetRequiredService<MainWindow>().Clipboard?.SetTextAsync(text) ?? Task.CompletedTask,
            DesktopShell.Open,
            ReadLicenceText()));
        services.AddSingleton(provider => new MainWindowViewModel(
            provider.GetServices<PageViewModel>(),
            provider.GetRequiredService<AppSettings>(),
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<SaveBarViewModel>(),
            provider.GetRequiredService<AboutViewModel>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategories.Ui)));

        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Anvil's licence as the app carries it built in (resource <c>Anvil.LICENSE</c>, spec s14.1
    /// O-3), so About shows the same text wherever the archive or a package put the file.
    /// </summary>
    /// <returns>The text of the licence file the build embedded.</returns>
    public static string ReadLicenceText()
    {
        // The build fails without the file (Anvil.App.csproj), so a missing resource is a broken build.
        using var stream = typeof(AboutViewModel).Assembly.GetManifestResourceStream("Anvil.LICENSE")
            ?? throw new InvalidOperationException("the build carries no licence text (resource Anvil.LICENSE)");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Requests <see cref="AttachTriggers"/> so it hooks onto the watcher (Spec S5.4 §3.8).
    /// <see cref="Build"/> itself only registers the factory and runs nothing (Spec E-A) —
    /// Program.Main calls this method before the watcher makes its first pass, because that
    /// pass still runs synchronously and would miss a later registration.
    /// A singleton nobody requests is never built and never listens — the quietest way to
    /// make this story have no effect.
    /// </summary>
    public static void ResolveAttachTriggers(ServiceProvider provider)
        => provider.GetRequiredService<AttachTriggers>();

    /// <summary>
    /// Anvil's own stop, in its order (Spec S5.4 §8.4, E-12): <b>detach first, then
    /// stop.</b>
    ///
    /// <para>
    /// <b>Unconditional, not tied to <c>DetachOnExit</c>:</b> that switch says what should
    /// happen to Wand at <i>game end</i>. Here Anvil tears down the container itself, and Wand
    /// dies either way — the only choice is between "harvested" and "lost".
    /// </para>
    ///
    /// <para>
    /// <b>Its own method instead of a lambda in the registration</b>, so the order is
    /// testable: <c>CompositionRootTests</c> calls it with two fakes and goes red if someone
    /// swaps them or removes the detach.
    /// </para>
    ///
    /// <para>
    /// Without a profile there is nothing to detach — it stops anyway.
    /// </para>
    /// </summary>
    public static async Task<StopOutcome> DetachThenStopAsync(
        Func<uint, GameProfile?> profileOf,
        Func<GameProfile, CancellationToken, Task> detachAll,
        Func<uint, CancellationToken, Task<StopOutcome>> stop,
        uint appId,
        CancellationToken cancellationToken)
    {
        if (profileOf(appId) is { } game)
        {
            await detachAll(game, cancellationToken).ConfigureAwait(false);
        }

        return await stop(appId, cancellationToken).ConfigureAwait(false);
    }
}
