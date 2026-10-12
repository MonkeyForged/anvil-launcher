using Anvil.Core.Packages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// Wand's way of obtaining a vendor build (spec s9.3 E-11), and that of every catalog with
/// checksums: fetch into the store, and nothing else. The behaviour of S3.5b §3.7, moved out of
/// the selection row unchanged — the row's only stage is <see cref="VendorStage.Fetching"/>, and
/// nothing ever blocks it.
///
/// <para>
/// <b>The store travels with the builds</b> instead of being the row's choices: a fetch needs a
/// folder of single files (<see cref="FetchProgressViewModel.RunAsync"/>), while the row may read
/// its choices from anything that implements <see cref="IPackageChoices"/>.
/// </para>
/// </summary>
public sealed partial class FetchedVendorBuilds : ObservableObject, IVendorBuilds
{
    private readonly PackageStore _store;
    private readonly IReadOnlyList<CatalogEntry> _entries;
    private readonly string _folderText;

    /// <summary>The file being fetched right now — for the sentence below the box.</summary>
    private string? _fetchingName;

    /// <param name="store">Where a fetched build lands — for Wand the same store the row reads.</param>
    /// <param name="entries">The builds in catalog order (at runtime <c>PackageCatalog.Wand</c>).</param>
    /// <param name="folderText">The folder as the sentence while fetching names it
    /// (<c>wand_bridge/wand/</c>).</param>
    /// <param name="fetch">The download with bar, cancel and red sentence.</param>
    public FetchedVendorBuilds(
        PackageStore store, IReadOnlyList<CatalogEntry> entries, string folderText,
        FetchProgressViewModel fetch)
    {
        _store = store;
        _entries = entries;
        _folderText = folderText;
        Fetch = fetch;
        Offers = entries.Select(entry => new VendorOffer(entry.FileName, entry.DisplayName)).ToList();

        fetch.PropertyChanged += (_, args) =>
        {
            // Start and end of the download are the only stage changes this way knows.
            if (args.PropertyName == nameof(FetchProgressViewModel.IsRunning))
            {
                OnPropertyChanged(nameof(Stage));
                OnPropertyChanged(nameof(StageNote));
            }
        };
    }

    public IReadOnlyList<VendorOffer> Offers { get; }

    public FetchProgressViewModel Fetch { get; }

    public VendorStage Stage => Fetch.IsRunning ? VendorStage.Fetching : VendorStage.Idle;

    /// <summary>While fetching: <c>Fetching &lt;file&gt; into &lt;folder&gt;.</c> (Spec S3.5b
    /// §3.7).</summary>
    public EmphasizedText StageNote
        => Fetch.IsRunning && _fetchingName is { } name
            ? new EmphasizedText(PackagePickRowViewModel.FetchingNoteFor(name, _folderText))
            : EmphasizedText.Empty;

    /// <summary>A fetch needs nothing the machine could lack.</summary>
    public EmphasizedText BlockedNote => EmphasizedText.Empty;

    /// <summary>There is no stage with its own <c>Cancel</c>; the download's sits next to its
    /// bar.</summary>
    public IRelayCommand CancelStageCommand { get; } = new RelayCommand(() => { });

    /// <summary>Nothing to re-evaluate: <see cref="BlockedNote"/> never changes.</summary>
    public void Refresh()
    {
    }

    public async Task<bool> ObtainAsync(VendorOffer offer)
    {
        var entry = _entries.FirstOrDefault(
            candidate => string.Equals(candidate.FileName, offer.Name, StringComparison.Ordinal));
        if (entry is null)
        {
            return false;
        }

        // Set before the run starts: IsRunning flips inside RunAsync, and the sentence it
        // announces must already name the file.
        _fetchingName = entry.FileName;
        return await Fetch.RunAsync(_store, [entry]) == FetchStatus.Placed;
    }
}
