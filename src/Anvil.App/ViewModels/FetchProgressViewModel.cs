using Anvil.Core.Packages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Anvil.App.ViewModels;

/// <summary>
/// A fetch with progress, cancel and red sentence (spec S3.5b §3.5) — the one mechanism held
/// once each by the entry of the <c>Dependencies</c> surface and by the selection rows
/// <c>Wand version</c> and <c>CE version</c>. None of them knows <see cref="PackageFetcher"/>
/// directly.
///
/// <para>
/// <b>The result's message goes to the log, not to the user</b> (spec §3.5): it carries hex
/// sums. The user sees one of three fixed sentences per outcome; a cancel shows none, but the
/// state before.
/// </para>
///
/// <para>
/// <b>The progress reaches the UI thread through <c>post</c>:</b> <see cref="PackageFetcher"/>
/// passes it on synchronously from the download's thread — no bound property may be set there.
/// </para>
/// </summary>
public sealed partial class FetchProgressViewModel : ObservableObject
{
    /// <summary>Red sentence for a wrong checksum (concept).</summary>
    public const string ChecksumText =
        "Checksum did not match — nothing was kept. The file at the vendor address is not the expected one.";

    /// <summary>Red sentence when the network could not be reached (E-13, request 44).</summary>
    public const string NetworkText =
        "Could not reach the vendor — check the network connection. Nothing was kept.";

    /// <summary>Red sentence for every other failure (E-13, request 44).</summary>
    public const string FailedText = "The download failed — nothing was kept. See the log for details.";

    /// <summary>Label of the cancel next to the bar.</summary>
    public const string CancelLabel = "Cancel";

    private readonly PackageFetcher _fetcher;
    private readonly Action<Action> _post;
    private readonly ILogger? _logger;

    /// <summary>The token of the running fetch; <c>null</c> while nothing runs.</summary>
    private CancellationTokenSource? _cancellation;

    /// <summary>Is a fetch running?</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>The progress in percent, 0 to 100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _percent;

    /// <summary>The red sentence; empty means none. Stays until the next fetch (spec §3.6).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    /// <param name="fetcher">The download — one instance for everyone who fetches.</param>
    /// <param name="post">Marshals onto the UI thread.</param>
    /// <param name="logger">The owner decides the category (<c>Modules</c>, or the module's own);
    /// null means silent.</param>
    public FetchProgressViewModel(PackageFetcher fetcher, Action<Action> post, ILogger? logger = null)
    {
        _fetcher = fetcher;
        _post = post;
        _logger = logger;
    }

    /// <summary>The percentage as text, without sizes: <c>40%</c>.</summary>
    public string PercentText => $"{(int)Math.Round(Percent)}%";

    /// <summary>Is a red sentence shown?</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>
    /// Fetches <paramref name="entries"/> into <paramref name="store"/>, setting progress and red
    /// sentence. Never throws: every outcome comes back as a status.
    /// </summary>
    public Task<FetchStatus> RunAsync(PackageStore store, IReadOnlyList<CatalogEntry> entries)
        => RunCoreAsync(async token =>
        {
            var progress = new PostingProgress<double>(value => _post(() => Percent = value * 100));
            var result = await _fetcher.FetchAllAsync(store, entries, progress, token);
            return (result.Status, result.FileName, result.Message);
        });

    /// <summary>
    /// Fetches <paramref name="download"/> into <paramref name="store"/> without a checksum (s9.3
    /// I-1), with the same progress, cancel and red sentence as <see cref="RunAsync"/>. The
    /// percentage follows the bytes over <see cref="UnverifiedDownload.SizeBytes"/>, capped at 100.
    /// Never throws.
    /// </summary>
    public Task<FetchStatus> RunUnverifiedAsync(PackageStore store, UnverifiedDownload download)
        => RunCoreAsync(async token =>
        {
            var progress = new PostingProgress<long>(
                bytes => _post(() => Percent = Math.Min(100, bytes * 100.0 / download.SizeBytes)));
            var result = await _fetcher.FetchUnverifiedAsync(store, download, progress, token);
            return (result.Status, (string?)result.FileName, result.Message);
        });

    /// <summary>Cancels the running fetch; without one, nothing.</summary>
    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    /// <summary>What both ways share: running state, token, and the red sentence per outcome.</summary>
    private async Task<FetchStatus> RunCoreAsync(
        Func<CancellationToken, Task<(FetchStatus Status, string? FileName, string? Message)>> fetch)
    {
        ErrorText = string.Empty;
        Percent = 0;
        IsRunning = true;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var (status, fileName, message) = await fetch(cancellation.Token);

            ErrorText = status switch
            {
                FetchStatus.ChecksumMismatch => ChecksumText,
                FetchStatus.NetworkFailed => NetworkText,
                FetchStatus.Failed => FailedText,
                _ => string.Empty,
            };

            if (ErrorText.Length > 0)
            {
                _logger?.LogWarning("fetch failed ({Status}): {Name}: {Message}", status, fileName, message);
            }

            return status;
        }
        finally
        {
            _cancellation = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Passes on synchronously, like <see cref="PackageFetcher"/> itself — not
    /// <see cref="Progress{T}"/>, whose reports could arrive after the end. The way onto the UI
    /// thread is the delegate's.
    /// </summary>
    private sealed class PostingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
