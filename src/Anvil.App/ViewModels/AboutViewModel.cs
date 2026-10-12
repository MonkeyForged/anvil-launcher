using System.Reflection;
using Anvil.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Anvil.App.ViewModels;

/// <summary>
/// The <em>About</em> window (UC-22, sheet <c>00f-about</c>): the build's version, where the
/// source lives, the licences of Anvil and of every third-party part it ships, and Anvil's own
/// licence text in a second window. No name, no postal address (spec s14.1, #15), and no address
/// on the owner's domain (spec s15.10).
/// </summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private const string SourceAddress = "github.com/MonkeyForged/anvil-launcher";

    /// <summary>
    /// The parts the release build ships, measured, not copied from the sheet: the shipped
    /// packages of <c>Anvil.App.deps.json</c> must equal the union of the prefixes, and the
    /// fonts must equal the OFL texts inside the design package (<c>ShippedPartsTests</c>). A
    /// new dependency turns that test red until it has its row here.
    /// </summary>
    private static readonly IReadOnlyList<LicenceEntry> ShippedParts =
    [
        new("Avalonia UI", "MIT", ["Avalonia"]),
        new("MicroCom", "MIT", ["MicroCom.Runtime"]),
        // The managed wrapper is MIT; the native Skia library it brings along is not.
        new("SkiaSharp", "MIT (Skia: BSD-3-Clause)", ["SkiaSharp"]),
        new("HarfBuzzSharp", "MIT", ["HarfBuzzSharp"]),
        new("CommunityToolkit.Mvvm", "MIT", ["CommunityToolkit.Mvvm"]),
        new("Microsoft.Extensions", "MIT", ["Microsoft.Extensions"]),
        new("Tmds.DBus.Protocol", "MIT", ["Tmds.DBus.Protocol"]),
        new("MonkeyForged Design", "© MonkeyForged", ["MonkeyForged.Design.Avalonia"]),
        new("Chakra Petch", "SIL OFL 1.1", []),
        new("IBM Plex Sans", "SIL OFL 1.1", []),
        new("JetBrains Mono", "SIL OFL 1.1", []),
        new("Silkscreen", "SIL OFL 1.1", []),
    ];

    private readonly Func<string, Task> _copyText;
    private readonly Action<string> _openUri;
    private bool _isLicenceOpen;

    /// <summary>Builds the window's content; the side effects come in from the composition root.</summary>
    /// <param name="copyText">Puts a text on the clipboard.</param>
    /// <param name="openUri">Hands a URI to the desktop, e.g. the repository to the browser.</param>
    /// <param name="licenceText">Anvil's licence as shipped (the embedded resource <c>Anvil.LICENSE</c>).</param>
    public AboutViewModel(Func<string, Task> copyText, Action<string> openUri, string licenceText)
    {
        _copyText = copyText;
        _openUri = openUri;
        LicenceText = licenceText;
        VersionInfo = string.Join(
            " · ",
            $"Anvil Launcher {Version}",
            $"Avalonia {VersionOf(typeof(Avalonia.Application).Assembly)}",
            $"MonkeyForged Design {VersionOf(typeof(MonkeyForged.Design.Avalonia.SideNav).Assembly)}",
            $".NET {Environment.Version}");
    }

    /// <summary>The version of this build.</summary>
    public string Version => CoreInfo.Version;

    /// <summary>
    /// The one line <c>Copy version info</c> puts on the clipboard (spec s14.1, O-3): versions
    /// only, nothing about the machine, its paths or its user.
    /// </summary>
    public string VersionInfo { get; }

    /// <summary>The repository as the window shows it — without the scheme, it reads as a place.</summary>
    public string SourceText => SourceAddress;

    public IReadOnlyList<LicenceEntry> ThirdParty => ShippedParts;

    public string LicenceText { get; }

    /// <summary>Is the <c>LICENCE</c> window open? Only the two commands change it.</summary>
    public bool IsLicenceOpen
    {
        get => _isLicenceOpen;
        private set => SetProperty(ref _isLicenceOpen, value);
    }

    [RelayCommand]
    private Task CopyVersionInfo() => _copyText(VersionInfo);

    [RelayCommand]
    private void OpenSource() => _openUri("https://" + SourceAddress);

    [RelayCommand]
    private void ViewLicence() => IsLicenceOpen = true;

    [RelayCommand]
    private void CloseLicence() => IsLicenceOpen = false;

    /// <summary>
    /// The version an assembly states for itself, without the build suffix after '+' — the
    /// same reading as <see cref="CoreInfo.Version"/>, here for assemblies other than Anvil's.
    /// </summary>
    private static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "unknown";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
