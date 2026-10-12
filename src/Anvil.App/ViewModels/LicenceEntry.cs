namespace Anvil.App.ViewModels;

/// <summary>
/// One third-party part Anvil ships, as the licence list in <em>About</em> names it (UC-22).
/// </summary>
/// <param name="Name">What the list shows, e.g. <c>Avalonia UI</c>.</param>
/// <param name="Licence">The licence as the list shows it, e.g. <c>MIT</c>.</param>
/// <param name="PackageIdPrefixes">
/// The NuGet package ids this row stands for: a shipped package belongs to the row when its id
/// equals a prefix or continues it after a dot (<c>Avalonia</c> covers <c>Avalonia.X11</c>).
/// Empty for parts that are no package of their own, such as the fonts inside the design
/// package. A test compares these with the packages the build ships.
/// </param>
public sealed record LicenceEntry(string Name, string Licence, IReadOnlyList<string> PackageIdPrefixes);
