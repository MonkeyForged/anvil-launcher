using Anvil.Core.Packages;
using Microsoft.Extensions.Logging;

namespace Anvil.App.Modules;

/// <summary>
/// Which Wand title a Steam game is, answered from a local copy of Wand's public catalog (story
/// s6.1, UC-3, UC-4, UC-6). Refreshed on attach when missing or old — never on a timer, never at
/// startup (vision §3).
///
/// <para>
/// <b>Nothing here can break an attach</b> (spec E-11): every failure ends as a log line and "no
/// mapping". The refresh task never faults, the lookup never throws.
/// </para>
/// </summary>
public sealed class WandCatalog
{
    /// <summary>Wand's own catalog address, read from its shipped configuration (wand-spielwahl.md §4.6).</summary>
    public static readonly Uri Address = new("https://storage-cdn.wemod.com/catalog.json");

    /// <summary>
    /// A new game in Wand's catalog is mapped by the next day at the latest; a network access
    /// without a click happens at most once a day (spec E-5).
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a lookup waits for a running refresh when there is no copy at all (spec E-7).
    /// Without it, the first attach after installing would never find its game.
    /// </summary>
    public static readonly TimeSpan NoCopyWait = TimeSpan.FromSeconds(3);

    private readonly FetchDocument _fetch;
    private readonly WandCatalogCopy _copy;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly Lock _gate = new();
    private Task _refresh = Task.CompletedTask;

    public WandCatalog(FetchDocument fetch, Func<string> moduleFolder, TimeProvider time, ILogger? logger = null)
    {
        _fetch = fetch;
        _copy = new WandCatalogCopy(moduleFolder);
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Starts a refresh if the copy needs one, or joins the one already running — two attaches at
    /// once share one fetch (spec E-10). The returned task never faults.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_gate)
        {
            if (!_refresh.IsCompleted)
            {
                return _refresh;
            }

            _refresh = Task.Run(RefreshGuardedAsync);
            return _refresh;
        }
    }

    /// <summary>
    /// Looks <paramref name="appId"/> up in the copy that is there right now. Only when there is
    /// none does it wait — at most <see cref="NoCopyWait"/> — for <paramref name="refresh"/>.
    /// </summary>
    public async Task<WandTitle?> LookupAsync(uint appId, Task refresh)
    {
        try
        {
            var index = LoadIndex();
            if (index is null && !refresh.IsCompleted)
            {
                await Task.WhenAny(refresh, Task.Delay(NoCopyWait, _time));
                index = LoadIndex();
            }

            if (index is null)
            {
                _logger?.LogInformation("Wand catalog: no copy, no mapping for {AppId}", appId);
                return null;
            }

            return Report(appId, index.Find(appId));
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Wand catalog: lookup failed, no mapping for {AppId}", appId);
            return null;
        }
    }

    private WandTitle? Report(uint appId, WandLookup lookup)
    {
        switch (lookup.Outcome)
        {
            case WandLookupOutcome.Found:
                _logger?.LogInformation(
                    "Wand catalog: {AppId} is title {TitleId}, game {GameId}",
                    appId, lookup.Title!.TitleId, lookup.Title.GameId);
                return lookup.Title;
            case WandLookupOutcome.Ambiguous:
                _logger?.LogInformation(
                    "Wand catalog: {AppId} is ambiguous ({Count} entries), no mapping", appId, lookup.EntryCount);
                return null;
            default:
                _logger?.LogInformation("Wand catalog: no mapping for {AppId}", appId);
                return null;
        }
    }

    private async Task RefreshGuardedAsync()
    {
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Wand catalog: refresh failed, keeping copy");
        }
    }

    private async Task RefreshCoreAsync()
    {
        // An unreadable copy counts as none (spec E-6): it must not be kept alive by a
        // matching version, so it is fetched without one.
        string? knownVersion = null;
        if (LoadIndex() is null)
        {
            _logger?.LogInformation("Wand catalog: no usable copy, downloading");
        }
        else
        {
            var state = _copy.ReadState();
            if (state is not null && _time.GetUtcNow() - state.CheckedAt < MaxAge)
            {
                _logger?.LogInformation("Wand catalog: copy is fresh, not checked");
                return;
            }

            knownVersion = state?.Version;
        }

        var result = await _fetch(Address, knownVersion, CancellationToken.None);
        var now = _time.GetUtcNow();
        switch (result.Outcome)
        {
            case DocumentFetchOutcome.Unchanged:
                _copy.MarkChecked(now);
                _logger?.LogInformation("Wand catalog: unchanged");
                break;
            case DocumentFetchOutcome.Changed:
                Store(result.Content!, result.Version, now);
                break;
            default:
                // The check time stays: without a network, trying again on the next attach costs
                // nothing (spec E-5).
                _logger?.LogWarning("Wand catalog: refresh failed, keeping copy ({Reason})", result.FailureReason);
                break;
        }
    }

    private void Store(byte[] content, string? version, DateTimeOffset now)
    {
        var parse = WandCatalogIndex.Parse(content);
        if (parse.Index is null)
        {
            // Renewing the check time keeps an unusable answer from being downloaded again on
            // every attach while Wand serves it (spec E-5).
            _copy.MarkChecked(now);
            _logger?.LogWarning("Wand catalog: response unusable, discarded ({Problem})", parse.Problem);
            return;
        }

        _copy.Replace(content, version, now);
        _logger?.LogInformation("Wand catalog: updated ({Count} Steam entries)", parse.Index.SteamEntryCount);
    }

    private WandCatalogIndex? LoadIndex()
    {
        var content = _copy.ReadContent();
        return content is null ? null : WandCatalogIndex.Parse(content).Index;
    }
}
