using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Anvil.Core;

/// <summary>Was der Start ergeben hat — ein Wert, keine Ausnahme (Spec S3.9 E-D).</summary>
public enum InstanceOutcome
{
    /// <summary>Dieser Prozess ist die eine Instanz.</summary>
    Primary,

    /// <summary>Eine andere Instanz läuft; sie hat die Bitte bekommen (oder keine, mit --tray).</summary>
    AlreadyRunning,
}

/// <summary>
/// Genau ein Anvil je Anmeldung (S3.9). Ein Unix-Domain-Socket ist Schloss und Briefkasten
/// zugleich (Spec E-A): Ob jemand lauscht, sagt der Verbindungsversuch; die Bitte „zeig dein
/// Fenster" fährt über dieselbe Verbindung. Eine liegende Datei, mit der sich niemand
/// verbinden lässt, ist eine Leiche vom Absturz und wird weggeräumt.
/// <para>
/// <b>Der Schutz darf nie den Start verhindern:</b> Scheitert er selbst, ist das Ergebnis
/// <see cref="InstanceOutcome.Primary"/> ohne Lauscher, mit einer Warnung — Anvil läuft dann
/// wie vor dieser Story, nur sichtbar.
/// </para>
/// </summary>
public sealed class InstanceGuard : IDisposable
{
    /// <summary>Die einzige Nachricht: „zeig dein Fenster".</summary>
    public const string ShowRequest = "show";

    /// <summary>Mehr als ein Wort kommt nie; wer mehr schickt, wird abgeschnitten und ignoriert.</summary>
    private const int RequestLimit = 64;

    private const int Backlog = 8;

    private readonly string _socketPath;
    private readonly Socket? _listener;
    private readonly ILogger _logger;
    private bool _disposed;

    private InstanceGuard(
        InstanceOutcome outcome, string socketPath, Socket? listener, ILogger logger)
    {
        Outcome = outcome;
        _socketPath = socketPath;
        _listener = listener;
        _logger = logger;
    }

    public InstanceOutcome Outcome { get; }

    /// <summary>
    /// Versucht, die eine Instanz zu werden. <paramref name="requestShow"/> = false lässt die
    /// laufende Instanz in Ruhe (Autostart mit --tray). Immer erst verbinden, nicht erst nach
    /// der Datei sehen (Spec E-C): Ein Versuch beantwortet „gibt es sie" und „lebt sie".
    /// </summary>
    public static InstanceGuard Acquire(string socketPath, bool requestShow, ILogger logger)
    {
        Socket? listener = null;
        try
        {
            var endpoint = new UnixDomainSocketEndPoint(socketPath);
            if (TryHandOver(endpoint, requestShow, logger))
            {
                return new InstanceGuard(InstanceOutcome.AlreadyRunning, socketPath, null, logger);
            }

            if (File.Exists(socketPath))
            {
                logger.LogInformation("removing stale instance socket {Path}", socketPath);
                File.Delete(socketPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endpoint);
            listener.Listen(Backlog);
            logger.LogInformation("instance socket listening at {Path}", socketPath);
            return new InstanceGuard(InstanceOutcome.Primary, socketPath, listener, logger);
        }
        catch (Exception exception) when (exception
            is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            listener?.Dispose();
            logger.LogWarning(
                "single-instance guard unavailable ({Message}); starting without it",
                exception.Message);
            return new InstanceGuard(InstanceOutcome.Primary, socketPath, null, logger);
        }
    }

    /// <summary>
    /// Nimmt Verbindungen an, bis <see cref="Dispose"/> den Socket schließt, und ruft
    /// <paramref name="onShow"/> je „show". Läuft auf dem Thread-Pool — der Aufrufer bringt
    /// die Bitte selbst auf seinen Faden. Bitten, die vor dem ersten Accept ankamen, warten
    /// im Rückstau des Sockets; nichts geht verloren. <paramref name="onShow"/> wird nicht
    /// abgefangen: Die App reicht einen Dispatcher-Post, und der wirft nicht.
    /// </summary>
    public async Task ListenAsync(Action onShow)
    {
        if (_listener is null)
        {
            return;
        }

        while (true)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
            {
                // Dispose hat den Lauscher geschlossen — das Ende der Schleife, kein Fehler.
                return;
            }

            using (client)
            {
                string request;
                try
                {
                    request = await ReadRequestAsync(client).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception
                    is SocketException or IOException or ObjectDisposedException)
                {
                    _logger.LogWarning(
                        "could not read a request from another instance ({Message})",
                        exception.Message);
                    continue;
                }

                if (request == ShowRequest)
                {
                    _logger.LogInformation("show requested by another instance");
                    onShow();
                }
                else if (request.Length == 0)
                {
                    _logger.LogInformation("another instance started and left");
                }
                else
                {
                    _logger.LogInformation(
                        "ignoring unknown request from another instance: {Request}", request);
                }
            }
        }
    }

    /// <summary>
    /// Schließt den Socket und löscht die Datei. <c>Socket.Dispose</c> räumt den gebundenen
    /// Pfad auf dieser Laufzeit zwar selbst weg (gemessen, Spec §4.2); das eigene
    /// <c>File.Delete</c> bleibt, weil es nichts kostet und die Datei sonst nirgends verschwindet.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_listener is null)
        {
            return;
        }

        _listener.Dispose();
        try
        {
            File.Delete(_socketPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Beim Beenden ist nichts mehr zu retten; die nächste Instanz räumt die Leiche weg.
            _logger.LogWarning(
                "could not remove the instance socket ({Message})", exception.Message);
        }
    }

    /// <summary>
    /// Gelingt die Verbindung, lebt jemand — dann bleibt das Ergebnis AlreadyRunning, egal
    /// was das Senden sagt. Scheitert sie (refused, no such file, not a directory), lauscht
    /// niemand.
    /// </summary>
    private static bool TryHandOver(
        UnixDomainSocketEndPoint endpoint, bool requestShow, ILogger logger)
    {
        using var client = new Socket(
            AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            client.Connect(endpoint);
        }
        catch (SocketException)
        {
            return false;
        }

        if (!requestShow)
        {
            logger.LogInformation("Anvil is already running; leaving it alone (tray start)");
            return true;
        }

        try
        {
            client.Send(Encoding.UTF8.GetBytes(ShowRequest + "\n"));
            client.Shutdown(SocketShutdown.Send);
            logger.LogInformation("Anvil is already running; asked it to show its window");
        }
        catch (SocketException exception)
        {
            logger.LogWarning(
                "Anvil is already running, but the show request failed ({Message})",
                exception.Message);
        }

        return true;
    }

    private static async Task<string> ReadRequestAsync(Socket client)
    {
        var buffer = new byte[RequestLimit];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await client
                .ReceiveAsync(buffer.AsMemory(total), SocketFlags.None)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }
}
