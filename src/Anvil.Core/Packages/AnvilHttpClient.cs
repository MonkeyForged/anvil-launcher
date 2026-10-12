using System.Net.Http.Headers;

namespace Anvil.Core.Packages;

/// <summary>
/// The one place that decides how Anvil talks HTTP, shared by both ways out — the package
/// download and the document fetch (spec s6.1 E-2). Two setups would drift apart the first time
/// one of them is changed.
/// </summary>
public static class AnvilHttpClient
{
    /// <summary>Catches a server that does not answer at all; a slow but live one is not affected.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    public static HttpClient Create()
        => Create(new SocketsHttpHandler { ConnectTimeout = ConnectTimeout });

    /// <summary>With a caller's handler — tests answer in place of a server this way.</summary>
    public static HttpClient Create(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler)
        {
            // No overall timeout here: a 158 MB package on a slow line would be cut off although
            // it is healthy. Each caller bounds its own requests through its cancellation token.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // Some CDNs refuse requests without a user agent; the version tells requests apart in
        // server logs.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(CoreInfo.ProductName, CoreInfo.Version));

        return client;
    }
}
