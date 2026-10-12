using Anvil.Core;
using Anvil.Core.Logging;
using Anvil.Core.Settings;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Anvil.App;

public class App : Application
{
    /// <summary>
    /// Set by Program.Main before Avalonia starts. Static because Avalonia requires a
    /// parameterless App constructor; null only in the XAML previewer.
    /// </summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Services is null only in the XAML previewer; the window stays empty there.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && Services is not null)
        {
            var logger = Services.GetRequiredService<ILoggerFactory>()
                .CreateLogger(LogCategories.App);
            var options = Services.GetRequiredService<StartupOptions>();
            var window = Services.GetRequiredService<MainWindow>();
            // Before it is first shown, on either path below (spec s15.6).
            window.FitToPrimaryScreen();
            // Avalonia shows an assigned MainWindow on its own after this method (measured,
            // spike 2026-09-05). With --tray it therefore stays unassigned until the host
            // fetches it on the first "Open" (spec S3.6 §3.5).
            if (!options.StartInTray)
            {
                desktop.MainWindow = window;
                logger.LogInformation("main window opened");
            }

            // The tray icon and "close means hide" (S3.5). ShutdownMode stays
            // OnLastWindowClose (project plan decision 5): a hidden window is not closed, one
            // never shown triggers nothing; without a tray the window really closes and ends
            // the app. No field for the host — window and menu hold it; the listener below
            // holds it through the delegate.
            var host = TrayHost.Attach(
                this, desktop, window,
                Services.GetRequiredService<AppSettings>(), logger, options.StartInTray);

            // From here on the instance listens for a second start (S3.9). Only here and not
            // in Program.Main: the request must go to the UI thread, and nobody may touch
            // Dispatcher.UIThread before the platform is initialised — Avalonia would otherwise
            // hold on to a dead dispatcher (spec E-F). A request that arrived earlier waits in
            // the socket's backlog. Fire and forget is safe: ListenAsync does not throw and ends
            // when the provider disposes the guard.
            _ = Services.GetRequiredService<InstanceGuard>()
                .ListenAsync(() => Dispatcher.UIThread.Post(() => host.ShowForSecondStart()));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
