namespace Anvil.Core.Packages;

/// <summary>
/// The seam to the network at runtime (S3.3a, task 3): loads <see cref="IPackageDownloader"/>
/// over real HTTP.
///
/// <para>
/// <b><see cref="Lazy{T}"/> instead of a field</b> (Plan P-5): registering it in the
/// Composition Root must not touch the network — the <see cref="HttpClient"/> and its
/// <see cref="SocketsHttpHandler"/> are only created on the first <see cref="DownloadAsync"/>.
/// </para>
///
/// <para>
/// <b>No overall timeout, but a connection timeout</b> (Spec E-10): an overall timeout on
/// <see cref="HttpClient"/> would abort a healthy 158 MB download on a narrow line just
/// because it takes longer than the deadline. The price is that a connection which
/// establishes but never delivers data hangs with no time limit — which is why the cancel
/// button in S3.4 is not a nicety but the only brake left at that point. The setup for it,
/// including the connection timeout (15 s) that at least catches the case where the server
/// does not answer at all, lives in <see cref="AnvilHttpClient"/> — shared with the document
/// fetch (s6.1 E-2).
/// </para>
/// </summary>
public sealed class HttpPackageDownloader : IPackageDownloader, IDisposable
{
    // 80 KiB per report (Plan P-6): about 2000 progress reports for 158 MB — enough for a
    // smooth bar, small enough to need no throttling.
    private const int ChunkSize = 80 * 1024;

    private readonly Lazy<HttpClient> _client = new(() => AnvilHttpClient.Create());

    /// <inheritdoc />
    public async Task DownloadAsync(
        Uri address, Stream target, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        using var response = await _client.Value.GetAsync(
            address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[ChunkSize];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await responseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            progress?.Report(totalRead);
        }
    }

    /// <summary>Disposes the client only if it was ever created (Plan P-5).</summary>
    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
