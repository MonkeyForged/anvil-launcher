using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Packages;

/// <summary>
/// The fetch (S3.3a): puts a <see cref="CatalogEntry"/> into a <see cref="PackageStore"/> through
/// <see cref="IPackageDownloader"/> — download, verify, only then rename to the target name. The
/// heart of the story.
///
/// <para>
/// <b>Verify first, then rename.</b> As long as the file sits under <c>.&lt;target name&gt;.part</c>
/// (E-5), it is invisible to <see cref="PackageStore.List"/> — S3.3 skips dot files when listing,
/// which is exactly what the part file is for. Only <see cref="File.Move(string, string)"/> as the
/// last step makes the file visible and selectable; nothing that failed the checksum may ever lie
/// under the target name.
/// </para>
///
/// <para>
/// <b>Every outcome except success leaves nothing behind</b> (spec §3.4): cancel, network error,
/// wrong checksum — the part file is gone afterwards. The cleanup sits in
/// <see cref="TryDeletePartFile"/>, which never throws itself (plan P-2): a cleanup step that
/// raises a second exception hides the first — and the first is the one that interests the user.
/// </para>
///
/// <para>
/// <b>Why <c>catch (Exception)</c> is caught broadly here</b> (plan P-1, otherwise frowned upon):
/// behind <see cref="IPackageDownloader"/> sits foreign code — in operation the HTTP stack, from
/// S3.4 on a click handler above it. An exception escaping from there uncaught would take the UI
/// down with it; the user would see a crash instead of a message. Excepted is
/// <see cref="OperationCanceledException"/> with a canceled token: it is the cancel and gets its
/// own outcome (E-9), which is why its <c>catch</c> comes first — otherwise the broad catch below
/// would swallow it. Network errors and the connection timeout (the same exception, but without a
/// canceled token) are caught by the second catch as <see cref="FetchStatus.NetworkFailed"/> (spec
/// S3.5b E-9). An <see cref="HttpRequestException"/> that carries a status code is not one of
/// them: the server answered, so it falls through to <see cref="FetchStatus.Failed"/> (spec s10.5).
/// </para>
///
/// <para>
/// <b>Two ways in, one core.</b> <see cref="FetchAsync"/> compares the result with the checksum of
/// a <see cref="CatalogEntry"/>; <see cref="FetchUnverifiedAsync"/> serves downloads that cannot be
/// pinned by checksum and only logs the checksum of what arrived. Everything else — the already-
/// present check, the part file, the rename, every failure outcome — is shared in
/// <see cref="FetchCoreAsync"/>.
/// </para>
/// </summary>
public sealed class PackageFetcher
{
    private readonly IPackageDownloader _downloader;
    private readonly ILogger? _logger;

    /// <param name="downloader">The seam to the network — HTTP in operation, a fake in tests.</param>
    /// <param name="logger">The owner decides the category; null means silent.</param>
    public PackageFetcher(IPackageDownloader downloader, ILogger? logger = null)
    {
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>
    /// Puts <paramref name="entry"/> into <paramref name="store"/>. If the file already lies under
    /// its target name, nothing is downloaded and nothing is verified — the store belongs to the
    /// user (E-7). The fetch does not select the file (E-8); that stays a line at the caller.
    /// </summary>
    public async Task<FetchResult> FetchAsync(
        PackageStore store,
        CatalogEntry entry,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entry);

        return await FetchCoreAsync(
            store, entry.FileName, entry.Address, entry.Sha256, unverifiedReason: null, progress, cancellationToken);
    }

    /// <summary>
    /// Like <see cref="FetchAsync"/>, but without a checksum comparison — for a download that cannot
    /// be pinned (<see cref="UnverifiedDownload.WhyUnverified"/> says why). Never returns
    /// <see cref="FetchStatus.ChecksumMismatch"/>. The checksum of what arrived is computed and only
    /// logged, together with the reason.
    /// </summary>
    public async Task<FetchResult> FetchUnverifiedAsync(
        PackageStore store,
        UnverifiedDownload download,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(download);

        return await FetchCoreAsync(
            store, download.FileName, download.Address, expectedSha256: null, download.WhyUnverified, progress,
            cancellationToken);
    }

    /// <summary>
    /// The flow both public ways share. <paramref name="expectedSha256"/> null means "unverified":
    /// the checksum is then only logged, together with <paramref name="unverifiedReason"/>.
    /// </summary>
    private async Task<FetchResult> FetchCoreAsync(
        PackageStore store,
        string fileName,
        Uri address,
        string? expectedSha256,
        string? unverifiedReason,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        // Step 1: character-exact like PackageStore.Contains (private there, hence replicated
        // here) — no network, no verification if the user already has something under this name.
        if (store.List().Any(file => string.Equals(file.Name, fileName, StringComparison.Ordinal)))
        {
            _logger?.LogInformation("package already present, nothing to fetch: {Name}", fileName);
            return new FetchResult(FetchStatus.AlreadyPresent, fileName, null);
        }

        string? partPath = null;
        try
        {
            // Step 2: creates the folder if it is missing.
            var folder = store.Folder;
            partPath = Path.Combine(folder, $".{fileName}.part");

            // Step 3+4: overwrite the part file, never append (E-6), download, then close —
            // only afterwards is the checksum computed, otherwise one verifies what the buffer has
            // not written yet.
            await using (var partStream = new FileStream(
                partPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _downloader.DownloadAsync(address, partStream, progress, cancellationToken);
            }

            // Step 5: over the finished file on disk (E-4), in blocks (P-3) — never
            // File.ReadAllBytes, which would pull 158 MB into memory at once.
            string actual;
            await using (var readStream = new FileStream(
                partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var hash = await SHA256.HashDataAsync(readStream, cancellationToken);
                actual = Convert.ToHexStringLower(hash);
            }

            // Character-exact comparison without IgnoreCase (P-4): CatalogEntry.Sha256 enforces
            // lower case, ToHexStringLower yields it too — there is no difference that IgnoreCase
            // could hide.
            if (expectedSha256 is not null && !string.Equals(actual, expectedSha256, StringComparison.Ordinal))
            {
                TryDeletePartFile(partPath);
                var message =
                    "The downloaded file does not match the expected checksum. "
                    + $"Expected {expectedSha256}, got {actual}.";
                _logger?.LogWarning(
                    "package checksum mismatch: {Name} (expected {Expected}, got {Actual})",
                    fileName, expectedSha256, actual);
                return new FetchResult(FetchStatus.ChecksumMismatch, fileName, message);
            }

            // Step 6: only here does the file become visible and selectable.
            var targetPath = Path.Combine(folder, fileName);
            var sizeBytes = new FileInfo(partPath).Length;
            File.Move(partPath, targetPath);
            if (expectedSha256 is null)
            {
                _logger?.LogInformation(
                    "package fetched without checksum: {Name} ({Bytes} bytes, sha256 {Actual}) — {WhyUnverified}",
                    fileName, sizeBytes, actual, unverifiedReason);
            }
            else
            {
                _logger?.LogInformation("package fetched: {Name} ({Bytes} bytes)", fileName, sizeBytes);
            }

            return new FetchResult(FetchStatus.Placed, fileName, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // First, because it is an Exception and would otherwise be swallowed by the broad
            // catch below (plan requirement). The user pressed cancel — not an error (E-9). Only
            // if the token really is canceled: spec S3.5b E-9.
            if (partPath is not null)
            {
                TryDeletePartFile(partPath);
            }

            return new FetchResult(FetchStatus.Canceled, fileName, null);
        }
        catch (Exception exception) when (exception is HttpRequestException { StatusCode: null }
            or OperationCanceledException
            or IOException { InnerException: System.Net.Sockets.SocketException })
        {
            // Spec S3.5b E-9: no network, or the connection dropped while reading. The connection
            // timeout arrives as an OperationCanceledException whose token nobody canceled — the
            // UI would otherwise show a cancel that nobody pressed. Spec s10.5 E-1: only an
            // HttpRequestException without a status code is a network failure; EnsureSuccessStatusCode
            // sets the code on a 404 or 500, and telling the user to check a working network sends
            // them the wrong way.
            if (partPath is not null)
            {
                TryDeletePartFile(partPath);
            }

            _logger?.LogWarning(exception, "package fetch failed, network unavailable: {Name}", fileName);
            return new FetchResult(FetchStatus.NetworkFailed, fileName, exception.Message);
        }
        catch (Exception exception)
        {
            if (partPath is not null)
            {
                TryDeletePartFile(partPath);
            }

            _logger?.LogWarning(exception, "package fetch failed: {Name}", fileName);
            return new FetchResult(FetchStatus.Failed, fileName, exception.Message);
        }
    }

    /// <summary>
    /// Fetches <paramref name="entries"/> one after the other through <see cref="FetchAsync"/> (spec
    /// S3.5b §3.5, E-8: one bar, one cancel, one error). Progress arrives as a fraction 0…1 over the
    /// sum of the catalog sizes.
    ///
    /// <para>
    /// <b>The sequence ends at the first entry that does not succeed.</b> If it was a cancel, the
    /// run additionally deletes what <i>it</i> already placed and so restores the state before
    /// (E-7) — never a file that was there before. On an error the verified files stay: they are
    /// correct, and would cost the download again on the next try.
    /// </para>
    /// </summary>
    public async Task<FetchBatchResult> FetchAllAsync(
        PackageStore store,
        IReadOnlyList<CatalogEntry> entries,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(entries);

        var total = entries.Sum(entry => entry.SizeBytes);
        long done = 0;
        var placed = new List<string>();

        foreach (var entry in entries)
        {
            var perEntry = progress is null ? null : new ForwardingProgress(progress, done, total);
            var result = await FetchAsync(store, entry, perEntry, cancellationToken);

            if (result.Status == FetchStatus.Placed)
            {
                placed.Add(Path.Combine(store.Folder, entry.FileName));
            }
            else if (result.Status != FetchStatus.AlreadyPresent)
            {
                if (result.Status == FetchStatus.Canceled)
                {
                    // E-7: only what this run placed.
                    foreach (var path in placed)
                    {
                        TryDeleteFile(path);
                    }
                }

                return new FetchBatchResult(result.Status, result.FileName, result.Message);
            }

            done += entry.SizeBytes;
            progress?.Report(Fraction(done, total));
        }

        return new FetchBatchResult(FetchStatus.Placed, null, null);
    }

    /// <summary>
    /// Fraction, capped at 1 — a download that delivers more bytes than the catalog states must
    /// not let the bar overflow. An empty sum counts as done.
    /// </summary>
    private static double Fraction(long bytes, long total)
        => total <= 0 ? 1.0 : Math.Min(1.0, bytes / (double)total);

    /// <summary>
    /// Deletes the part file if present — and never throws itself (P-2). A cleanup step that raises
    /// a second exception hides the first.
    /// </summary>
    private void TryDeletePartFile(string partPath)
    {
        try
        {
            File.Delete(partPath);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "cannot delete leftover part file: {Path}", partPath);
        }
    }

    /// <summary>
    /// The rollback on cancel (E-7): deletes a file this run placed — and, like
    /// <see cref="TryDeletePartFile"/>, never throws (P-2).
    /// </summary>
    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            _logger?.LogInformation("fetch canceled, removed file placed by this run: {Path}", path);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "cannot remove file placed by canceled fetch: {Path}", path);
        }
    }

    /// <summary>
    /// Converts the bytes of one entry into the fraction of the whole run and passes it on
    /// <b>synchronously</b>. Not <see cref="Progress{T}"/>: that posts to the
    /// <see cref="SynchronizationContext"/> or the thread pool, and reports might then arrive
    /// after the end or out of order. The way onto the UI thread is the caller's choice through
    /// their own <see cref="IProgress{T}"/>.
    /// </summary>
    private sealed class ForwardingProgress(IProgress<double> target, long offset, long total) : IProgress<long>
    {
        public void Report(long value) => target.Report(Fraction(offset + value, total));
    }
}
