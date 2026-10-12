using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// How a selection row obtains a vendor build (spec s9.3 E-11). <b>One row, two ways:</b> Wand
/// fetches with a checksum (<see cref="FetchedVendorBuilds"/>); CE Bridge asks, downloads, runs
/// the vendor's installer caged and checks the result. The row only draws the stages and reacts
/// to the answer — the same split as the store behind
/// <see cref="Anvil.Core.Packages.IPackageChoices"/> (s9.1 E-6).
///
/// <para>
/// <b>Change notification:</b> <see cref="Stage"/>, <see cref="StageNote"/> and
/// <see cref="BlockedNote"/> raise <see cref="INotifyPropertyChanged.PropertyChanged"/>; the row
/// redraws from them.
/// </para>
/// </summary>
public interface IVendorBuilds : INotifyPropertyChanged
{
    /// <summary>The builds on offer, fixed, in catalog order.</summary>
    IReadOnlyList<VendorOffer> Offers { get; }

    /// <summary>The download: bar, percent, <c>Cancel</c> while it runs, and the red sentence
    /// (<see cref="FetchProgressViewModel.ErrorText"/>).</summary>
    FetchProgressViewModel Fetch { get; }

    /// <summary>Where obtaining stands; anything but <see cref="VendorStage.Idle"/> keeps the row
    /// busy.</summary>
    VendorStage Stage { get; }

    /// <summary>The sentence below the box while <see cref="Stage"/> is not idle; empty = none.</summary>
    EmphasizedText StageNote { get; }

    /// <summary>Why <c>Get</c> cannot run on this machine; <c>Text ""</c> = it can. Set, the
    /// button stands disabled and the sentence replaces its missing tooltip (design 21, 65).</summary>
    EmphasizedText BlockedNote { get; }

    /// <summary>The <c>Cancel</c> the row shows while <see cref="Stage"/> is
    /// <see cref="VendorStage.InstallerOpen"/>.</summary>
    IRelayCommand CancelStageCommand { get; }

    /// <summary>Re-evaluates <see cref="BlockedNote"/>. The row calls it on every re-read — the
    /// moment the settings page is shown.</summary>
    void Refresh();

    /// <summary>
    /// Obtains <paramref name="offer"/>. Never throws: every way out is an answer, and the reason
    /// for a failure stands in <see cref="Fetch"/>'s red sentence and in the log.
    /// </summary>
    /// <returns>true = a choice named <see cref="VendorOffer.Name"/> lies in the store.</returns>
    Task<bool> ObtainAsync(VendorOffer offer);
}
