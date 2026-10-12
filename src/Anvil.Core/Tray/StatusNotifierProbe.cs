using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Anvil.Core.Tray;

/// <summary>The raw answer of the session bus, before it is judged.</summary>
/// <param name="WatcherOwned">The service <c>org.kde.StatusNotifierWatcher</c> exists.</param>
/// <param name="HostRegistered">A host (the panel) has registered with it.</param>
public readonly record struct WatcherAnswer(bool WatcherOwned, bool HostRegistered);

/// <summary>
/// Does the panel have a place for the icon? Avalonia stays silent on that — it throws nothing
/// when nobody listens, and <c>TrayIcon.IsVisible</c> only reports what we set ourselves. So the
/// session bus is asked (spec s3.5 §3.2, procedure from the predecessor).
/// <para>
/// Every uncertainty means "no": better that closing quits once too often than that the app
/// keeps running invisibly and only ends with <c>kill</c>.
/// </para>
/// </summary>
public static class StatusNotifierProbe
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";
    private const string WatcherPath = "/StatusNotifierWatcher";
    private const string HostRegisteredProperty = "IsStatusNotifierHostRegistered";

    /// <summary>The session bus address; null without a session bus.</summary>
    public static string? SessionBusAddress => DBusAddress.Session;

    /// <summary>
    /// The judgement, apart from the bus: only service <b>and</b> host together mean "there is a
    /// place". No answer (null) means no.
    /// </summary>
    public static bool Interpret(WatcherAnswer? answer)
        => answer is { WatcherOwned: true, HostRegistered: true };

    /// <summary>
    /// Asks the bus within a deadline. Missing address, unreachable bus, exception, cancellation
    /// and deadline all end as false with a log line — never as an exception, because the caller
    /// only needs the answer as input for the latch.
    /// </summary>
    public static async Task<bool> AskAsync(
        string? busAddress, TimeSpan timeout, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(busAddress))
        {
            logger.LogInformation("no session bus address; assuming no tray");
            return false;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            return Interpret(await AskBusAsync(busAddress, deadline.Token).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            logger.LogInformation(
                exception, "status notifier watcher not reachable; assuming no tray");
            return false;
        }
    }

    /// <summary>
    /// Two calls: <c>NameHasOwner</c> at the bus service, then the property
    /// <c>IsStatusNotifierHostRegistered</c> at the watcher. Without the service the second call
    /// is not made. Tested against the real bus in DBusLibraryTests (Anvil.App.Tests).
    /// </summary>
    private static async Task<WatcherAnswer> AskBusAsync(
        string busAddress, CancellationToken cancellationToken)
    {
        using var connection = new DBusConnection(busAddress);
        // ConnectAsync returns a ValueTask without a cancellation token; only as a Task can it be
        // bound to the deadline.
        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);

        var owned = await connection
            .CallMethodAsync(
                NameHasOwner(connection),
                static (Message message, object? _) => message.GetBodyReader().ReadBool(),
                null)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!owned)
        {
            return new WatcherAnswer(WatcherOwned: false, HostRegistered: false);
        }

        var hostRegistered = await connection
            .CallMethodAsync(
                GetHostRegistered(connection),
                static (Message message, object? _) =>
                    message.GetBodyReader().ReadVariantValue().GetBool(),
                null)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new WatcherAnswer(WatcherOwned: true, HostRegistered: hostRegistered);
    }

    private static MessageBuffer NameHasOwner(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus",
            path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus",
            member: "NameHasOwner",
            signature: "s");
        writer.WriteString(WatcherService);
        return writer.CreateMessage();
    }

    private static MessageBuffer GetHostRegistered(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: WatcherService,
            path: WatcherPath,
            @interface: "org.freedesktop.DBus.Properties",
            member: "Get",
            signature: "ss");
        writer.WriteString(WatcherService);
        writer.WriteString(HostRegisteredProperty);
        return writer.CreateMessage();
    }
}
