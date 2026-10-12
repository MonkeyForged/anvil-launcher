namespace Anvil.Core.Packages;

/// <summary>How a conditional fetch ended (story s6.1, spec E-1).</summary>
public enum DocumentFetchOutcome
{
    /// <summary>The server confirmed the caller's version; no data was transferred.</summary>
    Unchanged,

    /// <summary>A new document arrived.</summary>
    Changed,

    /// <summary>No usable answer: no network, timeout, server error, or too large.</summary>
    Failed,
}

/// <summary>
/// The answer to "fetch this document, here is my version" — exactly one of three outcomes.
///
/// <para>
/// <b>An outcome, not an exception:</b> For a document that is refreshed in the background, a
/// missing network is an expected path (UC-4), not an exceptional one. The caller handles it
/// with a switch instead of a catch.
/// </para>
/// </summary>
public sealed record DocumentFetch
{
    private DocumentFetch(DocumentFetchOutcome outcome, byte[]? content, string? version, string? failureReason)
    {
        Outcome = outcome;
        Content = content;
        Version = version;
        FailureReason = failureReason;
    }

    public static DocumentFetch Unchanged { get; } = new(DocumentFetchOutcome.Unchanged, null, null, null);

    public DocumentFetchOutcome Outcome { get; }

    /// <summary>The document; set only for <see cref="DocumentFetchOutcome.Changed"/>.</summary>
    public byte[]? Content { get; }

    /// <summary>
    /// The server's version marker for <see cref="Content"/>, opaque to the caller. Null when the
    /// server sent none — the next fetch then asks unconditionally.
    /// </summary>
    public string? Version { get; }

    /// <summary>Why nothing usable arrived; set only for <see cref="DocumentFetchOutcome.Failed"/>.</summary>
    public string? FailureReason { get; }

    public static DocumentFetch Changed(byte[] content, string? version)
        => new(DocumentFetchOutcome.Changed, content, version, null);

    public static DocumentFetch Failed(string reason)
        => new(DocumentFetchOutcome.Failed, null, null, reason);
}

/// <summary>
/// The narrowed network access a module is handed (architecture.md §4.3): fetch this address,
/// my version is <paramref name="knownVersion"/> (or none). Throws only when the caller cancels.
///
/// <para>
/// <b>A delegate, not a service:</b> A module gets exactly this one ability — no client, no
/// headers, no way to reach anything the core has not decided to offer.
/// </para>
/// </summary>
public delegate Task<DocumentFetch> FetchDocument(
    Uri address, string? knownVersion, CancellationToken cancellationToken);
