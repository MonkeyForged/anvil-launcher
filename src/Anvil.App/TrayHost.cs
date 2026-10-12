using Anvil.Core.Settings;
using Anvil.Core.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Anvil.App;

/// <summary>
/// Host of the tray icon (spec S3.5 §3.4): registers Avalonia's <see cref="TrayIcon"/> with the
/// menu <c>Open</c>, separator, <c>Exit</c>, hooks into the window's closing and translates.
/// Nothing is decided here — the latch lives in <see cref="CloseToTrayPolicy"/>, the answer to
/// "is there a tray" in <see cref="StatusNotifierProbe"/>. No view model (E-F): two fixed
/// entries without state. Since S3.6 the host knows the start without a window: the window has
/// then never been assigned, and the first <c>Open</c> assigns it (spec S3.6 §3.5). Accepted
/// live, not tested. Since S3.9 the host also shows the window when a second start asks for it.
/// Since 1.3 the icon is the flat front of the facet anvil in forge (spec s13.1, UC-24).
/// </summary>
internal sealed class TrayHost
{
    private static readonly Uri IconUri = new("avares://Anvil.App/Assets/tray.png");

    /// <summary>Measured, the bus answers within milliseconds; the timeout is only the latch.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Window _window;
    private readonly AppSettings _settings;
    private readonly ILogger _logger;

    /// <summary>
    /// The probe's last answer; null while there is none. Until then "no tray" holds (E-H).
    /// Read and written on the UI thread only.
    /// </summary>
    private bool? _trayAvailable;

    private TrayHost(
        IClassicDesktopStyleApplicationLifetime desktop, Window window, AppSettings settings,
        ILogger logger)
    {
        _desktop = desktop;
        _window = window;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Attaches icon and menu to the application and the closing handler to the window. The
    /// return value holds nothing Avalonia does not hold itself — menu and window carry the
    /// references; Avalonia cleans up the icon on exit.
    /// </summary>
    public static TrayHost Attach(
        Application app, IClassicDesktopStyleApplicationLifetime desktop, Window window,
        AppSettings settings, ILogger logger, bool startInTray)
    {
        var host = new TrayHost(desktop, window, settings, logger);

        var open = new NativeMenuItem("Open");
        open.Click += (_, _) => host.Open("menu");
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => host.Exit();

        // The bitmap is read completely before the stream closes.
        using var stream = AssetLoader.Open(IconUri);
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(new Bitmap(stream)),
            ToolTipText = "Anvil",
            Menu = new NativeMenu { Items = { open, new NativeMenuItemSeparator(), exit } },
            IsVisible = true,
        };
        // A click on the icon brings the window like "Open" (addendum 2026-09-06). When the
        // panel reports it is up to the panel: the StatusNotifier protocol only knows
        // "Activate", Plasma sends it on a single left click; there is no double click.
        // Show + Activate twice is harmless.
        icon.Clicked += (_, _) => host.Open("click");
        TrayIcon.SetIcons(app, [icon]);
        logger.LogInformation("tray icon registered");

        window.Closing += host.OnClosing;

        if (startInTray)
        {
            host.StartInTray();
        }
        else
        {
            host.RefreshTrayAvailability();
        }

        return host;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        var userClosing = e.CloseReason == WindowCloseReason.WindowClosing;
        var minimizeToTray = _settings.App.MinimizeToTray;
        var trayAvailable = _trayAvailable == true;
        if (!CloseToTrayPolicy.ShouldHide(userClosing, minimizeToTray, trayAvailable))
        {
            // All three inputs in the line: "why did it exit?" without a debugger.
            _logger.LogInformation(
                "window closing: exiting (reason {Reason}, minimize to tray {Switch}, tray available {Tray})",
                e.CloseReason, minimizeToTray, trayAvailable);
            return;
        }

        e.Cancel = true;
        _window.Hide();
        _logger.LogInformation("window hidden to tray");
        // If the panel dies while the window is hidden, the old finding would be a lie — so
        // ask again after every hide and restore.
        RefreshTrayAvailability();
    }

    /// <summary>
    /// Brings a hidden window back and a visible one to the front (measured); after a tray
    /// start the first call assigns the window. The log line names the trigger: menu entry or
    /// click on the icon.
    /// </summary>
    private void Open(string source)
    {
        ShowWindow();
        _logger.LogInformation("window restored from tray ({Source})", source);
        RefreshTrayAvailability();
    }

    /// <summary>
    /// A second start asked for the window (S3.9): show it and bring it to the front, in the
    /// same order as "Open" — after a tray start the call assigns the window. Own name and own
    /// line (spec E-I): "restored from tray" would be a lie for a visible window behind others.
    /// </summary>
    public void ShowForSecondStart()
    {
        ShowWindow();
        _logger.LogInformation("window shown for a second start");
        RefreshTrayAvailability();
    }

    /// <summary>
    /// Assign, then show — the order of the spike (spec S3.6 E-K). After a tray start the window
    /// has never been assigned; Avalonia shows an assigned window by itself only at startup,
    /// here Show() has to follow.
    /// </summary>
    private void ShowWindow()
    {
        _desktop.MainWindow ??= _window;
        _window.Show();
        _window.Activate();
    }

    /// <summary>
    /// Exits through the lifetime. The window gets the reason <c>ApplicationShutdown</c>, and
    /// the latch lets it close.
    /// </summary>
    private void Exit()
    {
        _logger.LogInformation("exit requested from tray");
        _desktop.Shutdown();
    }

    /// <summary>
    /// Start without a window (spec S3.6 E-I): unlike closing, the start can wait for the
    /// probe's first answer — until then nothing is visible. If it says "no tray", the latch
    /// takes hold and the window appears; otherwise it stays away until "Open" brings it.
    /// </summary>
    private void StartInTray()
    {
        _ = StartAsync();

        async Task StartAsync()
        {
            var available = await ProbeAsync();
            Dispatcher.UIThread.Post(() =>
            {
                if (available)
                {
                    _logger.LogInformation("started in tray");
                    return;
                }

                _logger.LogInformation("no tray available; showing window");
                ShowWindow();
            });
        }
    }

    /// <summary>
    /// Asks the bus and stores the answer on the UI thread. Fire-and-forget is safe here:
    /// <see cref="StatusNotifierProbe.AskAsync"/> never throws.
    /// </summary>
    private void RefreshTrayAvailability() => _ = ProbeAsync();

    private async Task<bool> ProbeAsync()
    {
        var available = await StatusNotifierProbe.AskAsync(
            StatusNotifierProbe.SessionBusAddress, ProbeTimeout, _logger);
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayAvailable != available)
            {
                _logger.LogInformation("tray available: {Available}", available);
            }

            _trayAvailable = available;
        });
        return available;
    }
}
