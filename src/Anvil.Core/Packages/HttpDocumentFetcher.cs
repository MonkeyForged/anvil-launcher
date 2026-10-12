using System.Net;

namespace Anvil.Core.Packages;

/// <summary>
/// Fetches a document only when it changed, over real HTTP (story s6.1, spec E-1 to E-3). Its
/// <see cref="FetchAsync"/> is what the composition root hands to a module as
/// <see cref="FetchDocument"/>.
///
/// <para>
/// <b>A second narrow way next to the package download, not an extension of it</b>
/// (architecture.md §4.3): A package has a fixed checksum and a target store; a document that
/// changes all the time has neither. One call with optional checksum, optional store and
/// optional name would be three switches every caller can set wrong.
/// </para>
/// </summary>
public sealed class HttpDocumentFetcher : IDisposable
{
    /// <summary>
    /// Bounds a whole request. Unlike a package, a document fetch runs without a cancel button,
    /// so without a bound a connection that opens but never delivers would hang forever.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Guards memory against a stray answer; Wand's catalog is about 5 MB.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    private const int ChunkSize = 80 * 1024;

    // Lazy: registering this in the composition root must not touch the network.
    private readonly Lazy<HttpClient> _client;
    private readonly TimeSpan _timeout;
    private readonly long _maxBytes;

    public HttpDocumentFetcher()
        : this(() => AnvilHttpClient.Create(), DefaultTimeout, DefaultMaxBytes)
    {
    }

    public HttpDocumentFetcher(Func<HttpClient> createClient, TimeSpan timeout, long maxBytes)
    {
        _client = new Lazy<HttpClient>(createClient);
        _timeout = timeout;
        _maxBytes = maxBytes;
    }

    public async Task<DocumentFetch> FetchAsync(
        Uri address, string? knownVersion, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            if (knownVersion is not null)
            {
                // Sent back exactly as the server gave it; the caller treats it as opaque.
                request.Headers.TryAddWithoutValidation("If-None-Match", knownVersion);
            }

            using var response = await _client.Value.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return DocumentFetch.Unchanged;
            }

            if (!response.IsSuccessStatusCode)
            {
                return DocumentFetch.Failed($"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > _maxBytes)
            {
                return TooLarge();
            }

            var content = await ReadWithinLimitAsync(response.Content, deadline.Token);
            return content is null
                ? TooLarge()
                : DocumentFetch.Changed(content, response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DocumentFetch.Failed($"no answer within {_timeout.TotalSeconds:0.#} s");
        }
        catch (HttpRequestException exception)
        {
            return DocumentFetch.Failed(exception.Message);
        }
        catch (IOException exception)
        {
            return DocumentFetch.Failed(exception.Message);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    // A declared length can be missing or wrong, so the limit also holds while reading.
    private async Task<byte[]?> ReadWithinLimitAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[ChunkSize];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > _maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private DocumentFetch TooLarge() => DocumentFetch.Failed($"larger than {_maxBytes} bytes");
}
