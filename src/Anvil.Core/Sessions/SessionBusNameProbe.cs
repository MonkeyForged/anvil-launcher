using Microsoft.Extensions.Logging;
using Tmds.DBus.Protocol;

namespace Anvil.Core.Sessions;

/// <summary>
/// Does anyone own this name on the session bus? For s4.3 that is the question "can this game
/// be attached now" — measured, the bus name is the <b>first</b> signal an attach can follow,
/// about 0.9 s after the service shows up in the environment (messungen-anhaengen.md §2,
/// project plan M4, decision 3).
///
/// <para>
/// <b>Tested against the real bus with the app's library</b> (DBusLibraryTests in
/// Anvil.App.Tests, spec s15.2): the core tests load the library version the core was compiled
/// against, the app may load another one. The layer above (<see cref="AttachWaiter"/>) is tested
/// with a fake probe; the live test with its counter-check covers the attach flow (spec s4.3 §5.5).
/// </para>
///
/// <para>
/// <b>One connection per question</b>, like the tray probe — the simplest solution that holds.
/// Over the full deadline that is about 240 connection setups on a local Unix socket. Should
/// that show in a live run, a later version keeps one connection for the whole run (spec s4.3 §7).
/// </para>
///
/// <para>
/// <b>Every uncertainty means "no"</b> (E-2): missing address, unreachable bus, exception — all
/// become <c>false</c>, never an exception. The caller only needs the answer as input for its
/// next move.
/// </para>
/// </summary>
public static class SessionBusNameProbe
{
    /// <summary>The session bus address; null without a session bus.</summary>
    public static string? SessionBusAddress => DBusAddress.Session;

    /// <summary>
    /// Whether <paramref name="name"/> has an owner on the bus at <paramref name="busAddress"/>;
    /// false for every failure, only cancellation by the caller is passed on.
    /// </summary>
    public static async Task<bool> IsOwnedAsync(
        string? busAddress,
        string name,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(busAddress))
        {
            logger?.LogDebug("no session bus address; {Name} cannot be owned", name);
            return false;
        }

        try
        {
            return await AskBusAsync(busAddress, name, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger?.LogDebug(exception, "session bus not reachable; assuming {Name} is not owned", name);
            return false;
        }
    }

    /// <summary>One call: <c>NameHasOwner</c> at the bus service.</summary>
    private static async Task<bool> AskBusAsync(
        string busAddress, string name, CancellationToken cancellationToken)
    {
        using var connection = new DBusConnection(busAddress);
        // ConnectAsync returns a ValueTask without a cancellation token; only as a Task can it be
        // bound to the token (as in StatusNotifierProbe).
        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .CallMethodAsync(
                NameHasOwner(connection, name),
                static (Message message, object? _) => message.GetBodyReader().ReadBool(),
                null)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static MessageBuffer NameHasOwner(DBusConnection connection, string name)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus",
            path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus",
            member: "NameHasOwner",
            signature: "s");
        writer.WriteString(name);
        return writer.CreateMessage();
    }
}
