namespace Anvil.App.ViewModels;

/// <summary>
/// One build a selection row offers to obtain (spec s9.3 E-11).
/// </summary>
/// <param name="Name">
/// The choice name once the build lies in the store — Wand: the file name; CE: <c>7.7</c>. The
/// row compares it with the store's names to decide whether <c>Get</c> stands.
/// </param>
/// <param name="DisplayName">What follows <c>Get</c> on the button — Wand: <c>WeMod 11.6.0</c>;
/// CE: <c>CE 7.7</c>.</param>
public sealed record VendorOffer(string Name, string DisplayName);

/// <summary>Where obtaining a vendor build stands. Everything but <see cref="Idle"/> makes the
/// row busy: no action, no second run.</summary>
public enum VendorStage
{
    Idle,
    Fetching,
    InstallerOpen,
    Checking,
}

/// <summary>
/// A sentence with one emphasized word (spec s9.3: <c>Decline</c>, <c>bubblewrap</c>). The view
/// sets the first occurrence of <paramref name="Emphasis"/> in <paramref name="Text"/> apart.
/// </summary>
/// <param name="Text">The sentence; "" = no sentence.</param>
/// <param name="Emphasis">The word to set apart; "" = plain.</param>
public sealed record EmphasizedText(string Text, string Emphasis = "")
{
    /// <summary>No sentence.</summary>
    public static EmphasizedText Empty { get; } = new(string.Empty);
}
