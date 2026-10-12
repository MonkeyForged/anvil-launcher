namespace Anvil.Core.Packages;

/// <summary>
/// A download Anvil cannot pin by checksum — for example an installer the vendor rebuilds on every
/// request. <see cref="WhyUnverified"/> names the reason and where the check sits instead; it ends
/// up in the log next to the checksum of what actually arrived.
///
/// <para>
/// Same validation as <see cref="CatalogEntry"/>: a file name without path and without leading
/// dot, an https address, a positive size, no blank texts (<see cref="ArgumentException"/>).
/// </para>
/// </summary>
/// <param name="FileName">The name under which the downloaded file lands in the store.</param>
/// <param name="DisplayName">What the selection row shows.</param>
/// <param name="Address">Where the file is fetched from. Always <c>https</c>.</param>
/// <param name="SizeBytes">The expected total size in bytes, for the progress bar.</param>
/// <param name="WhyUnverified">Why no checksum can be pinned, and what checks the file instead.</param>
public sealed record UnverifiedDownload(
    string FileName,
    string DisplayName,
    Uri Address,
    long SizeBytes,
    string WhyUnverified)
{
    // A record cannot have a constructor body, so the validation sits in initializers that
    // override the generated properties (same pattern as CatalogEntry).

    /// <summary>The name under which the downloaded file lands in the store.</summary>
    public string FileName { get; init; } = ValidateFileName(FileName);

    /// <summary>What the selection row shows.</summary>
    public string DisplayName { get; init; } = !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : throw new ArgumentException("Unverified download display name must not be blank.", nameof(DisplayName));

    /// <summary>Where the file is fetched from. Always <c>https</c>.</summary>
    public Uri Address { get; init; } = ValidateAddress(Address);

    /// <summary>The expected total size in bytes, for the progress bar.</summary>
    public long SizeBytes { get; init; } = SizeBytes > 0
        ? SizeBytes
        : throw new ArgumentException("Unverified download size must be greater than zero.", nameof(SizeBytes));

    /// <summary>Why no checksum can be pinned, and what checks the file instead.</summary>
    public string WhyUnverified { get; init; } = !string.IsNullOrWhiteSpace(WhyUnverified)
        ? WhyUnverified
        : throw new ArgumentException("Unverified download reason must not be blank.", nameof(WhyUnverified));

    // A name with '/' or '..' would write the file outside the store; a leading dot would make it
    // invisible, because PackageStore.List() skips dot files. Mirrors CatalogEntry.
    private static string ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Unverified download file name must not be blank.", nameof(fileName));
        }

        if (Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException(
                $"Unverified download file name '{fileName}' must not contain a path.", nameof(fileName));
        }

        if (fileName.StartsWith('.'))
        {
            throw new ArgumentException(
                $"Unverified download file name '{fileName}' must not start with a dot.", nameof(fileName));
        }

        return fileName;
    }

    // Without TLS nothing at all vouches for the origin — and here there is no checksum either.
    private static Uri ValidateAddress(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Unverified download address '{address}' must use https.", nameof(address));
        }

        return address;
    }
}
