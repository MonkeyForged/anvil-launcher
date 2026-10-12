using Anvil.Core;
using Anvil.Core.Autostart;
using Anvil.Core.Logging;
using Anvil.Core.Modules;
using Anvil.Core.Sessions;
using Anvil.Core.Settings;
using Anvil.Core.Steam;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MonkeyForged.Design.Avalonia;

namespace Anvil.App;

internal static class Program
{
    // Entry point. No Avalonia code may run before BuildAvaloniaApp, or the platform
    // initialisation fails.
    [STAThread]
    public static void Main(string[] args)
    {
        // Read once, handed to the root (spec S3.9 E-E).
        var options = StartupOptions.Parse(args);
        using var services = CompositionRoot.Build(options);
        App.Services = services;
        var log = services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategories.App);
        log.LogInformation("Anvil starting");
        try
        {
            // If an Anvil is already running, it received the request for its window while
            // the services were resolved — nothing is left to do here (S3.9). Before registry,
            // library, autostart sync and watchdog: the second instance touches nothing and
            // writes nothing to the console (spec E-G); the log says what happened. Exit code 0.
            if (services.GetRequiredService<InstanceGuard>().Outcome
                == InstanceOutcome.AlreadyRunning)
            {
                return;
            }

            // Build the registry before the UI starts: wiring errors surface early, and the
            // registration log lines stand before the first window.
            services.GetRequiredService<ModuleRegistry>();
            // The Steam snapshot before the window, for the same reason as the registry: its
            // lines belong to the start, and a read error shows before a surface depends on it
            // (spec S2.2 §11).
            services.GetRequiredService<GameLibrary>();
            // An interrupted write reports itself at start (spec S4.4 §3.4). Here only to the
            // log — the offer "Restore backup / Dismiss" is drawn by the save bar (S4.5). The
            // call also resolves journal and transaction, so a wiring error surfaces before the
            // first window, as with registry and library.
            if (services.GetRequiredService<SteamTransaction>().PendingRecovery() is { } pending)
            {
                log.LogWarning(
                    "an earlier save was interrupted at {StartedAt} — {Count} file(s) can be restored",
                    pending.StartedAt, pending.Files.Count);
            }
            // The autostart entry mirrors the switch on every start (decision 6, spec S3.6
            // E-D): on → renewed with the current program path, off → deleted. Before
            // Avalonia, so the line belongs to the start and not to the window.
            services.GetRequiredService<AutostartFile>()
                .Sync(services.GetRequiredService<AppSettings>().App.StartOnBoot);
            // The watchdog is the application's only polling loop (project plan A9). It starts
            // here and not in the composition root: the root assembles, it runs nothing
            // (spec E-A). The first pass still runs synchronously, because RunAsync looks once
            // before its first await — the Library page, created much later when MainWindow is
            // resolved, therefore finds the marks right from the start.
            // S5.4 §3.8: AttachTriggers subscribes to the watchdog in its constructor —
            // requested before the first pass, which still runs synchronously (spec E-8); a
            // later subscription would miss the very first hit.
            CompositionRoot.ResolveAttachTriggers(services);
            using var sessions = new CancellationTokenSource();
            var watchdog = services.GetRequiredService<GameSessionWatcher>()
                .RunAsync(sessions.Token);
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            // Wait for the loop instead of only cancelling it (spec E-B): otherwise the line
            // below would be a claim about a thread that is still running. RunAsync catches the
            // cancellation itself and ends after at most one pass.
            sessions.Cancel();
            watchdog.GetAwaiter().GetResult();
            log.LogInformation("Anvil exiting");
        }
        catch (Exception exception)
        {
            log.LogCritical(exception, "Anvil crashed");
            throw;
        }
    }

    // The package's frame on every window (story s14.1): on X11 the theme draws title bar,
    // frame and shadow itself; elsewhere the option has no effect.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseMonkeyForgedWindowFrame()
            .LogToTrace();
}
