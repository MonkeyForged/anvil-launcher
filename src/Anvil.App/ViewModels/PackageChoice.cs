using Anvil.Core.Packages;

namespace Anvil.App.ViewModels;

/// <summary>
/// An entry of the package selection in the settings (S3.4, design follow-up of 2026-09-13):
/// either a file from the store or the topmost entry <c>none selected</c>.
///
/// <para>
/// <b>The twin of <see cref="ProtonChoice"/></b> — the same selection, set one size smaller into
/// the settings row. <see cref="IsNone"/> plays the role of <c>ProtonChoice.IsAuto</c> there: the
/// topmost entry carries the separator line below it, because it is not a file but its absence.
/// </para>
///
/// <para>
/// <b>No shared base type with <see cref="ProtonChoice"/></b>: two occurrences are not a pattern
/// (Plan P-7). The Proton entry carries a source and a tool key, this one a file name — what they
/// share is the shape on screen, not the content.
/// </para>
/// </summary>
/// <param name="Name">
/// The file name from the store — or the text of the topmost entry. It is also the value that
/// goes to <see cref="PackageStore.Select"/>; that is why the name stands here verbatim and not
/// shortened.
/// </param>
/// <param name="IsNone">The topmost entry: no choice, and the separator line below it.</param>
/// <param name="StoreIsEmpty">
/// Is the store empty? Then the topmost entry carries the empty sentence below it (mockup,
/// <c>selEmptyStyle</c>) — only it, because when the store is empty it is the only entry.
///
/// <para>
/// <b>Why this fact about the row hangs on the entry:</b> in the open list, every row's data
/// context is the entry itself. The way to the row above would lead through an ancestor binding,
/// and the open list is a <c>Popup</c> — a tree of its own, in which such a binding silently runs
/// into nothing instead of failing. A <c>bool</c> the row passes along while building the list
/// cannot.
/// </para>
/// </param>
/// <param name="Vendor">
/// The offer of a <b>vendor build that does not lie in the store</b> (Spec S3.5b §3.7, s9.3 I-5)
/// — a click obtains it instead of choosing. <c>null</c> for entries from the store and the
/// topmost entry.
/// </param>
public sealed record PackageChoice(string Name, bool IsNone, bool StoreIsEmpty = false, VendorOffer? Vendor = null)
{
    /// <summary>A vendor build that has to be obtained first.</summary>
    public bool IsVendor => Vendor is not null;

    /// <summary>
    /// What the open list shows: the name, for a vendor build with the suffix <c>  ·  get</c>
    /// (Spec S3.5b §3.7). <see cref="Name"/> stays the choice name, because it is the value that
    /// goes to <see cref="PackageStore.Select"/>.
    /// </summary>
    public string Label => IsVendor ? Name + "  ·  get" : Name;

    /// <summary>The closed field shows the name — a record's default ToString would list all
    /// values, the <c>bool</c>s included.</summary>
    public override string ToString() => Name;
}
