using System.Globalization;
using System.Text.RegularExpressions;
using Anvil.Core.Steam;

namespace Anvil.Core.Prefixes;

/// <summary>
/// How this prefix stands (S3.1) — the question every further story of M3 asks first.
///
/// <para>
/// <b>Computed, not read</b> (Spec E-1). Everything here already sits in the
/// <see cref="GameProfile"/> that <c>SteamScanner</c> built. A second reader that reaches into
/// <c>compatdata</c> on its own again would be a second source for "does this prefix exist" —
/// and stale after the next rescan (<c>architecture.md</c> §8: one truth per thing).
/// </para>
/// </summary>
/// <param name="State">Present, Missing, or NotPossible.</param>
/// <param name="Path">The <c>pfx</c> folder, or <c>null</c> without a prefix.</param>
/// <param name="ProtonMajor">
/// The major version from <c>config_info</c>, or <c>null</c> — <b>never guessed</b>.
///
/// <para>
/// <b>What actually sits there</b> (Spec §3.1, measured 2026-09-14): the first line of
/// <c>config_info</c> is Proton's <c>CURRENT_PREFIX_VERSION</c> — its statement of when a
/// prefix must be rebuilt — and <b>not</b> the tool's release number. Proton 11.0 and Proton
/// Experimental both write <c>11.0-100</c>. Which tool ran is told by the path lines of the
/// same file (<c>ProtonConfigInfo.FindToolPath</c>).
/// </para>
///
/// <para>
/// <b>For the package key, that is exactly the right number</b> (project plan M3,
/// decision 8): a package holds for a prefix build, not for a tool release. Whether it also
/// carries across major versions is answered by the measurement before S3.5 — this number
/// gives that measurement its basis but does not use it itself.
/// </para>
/// </param>
public sealed partial record PrefixReport(PrefixState State, string? Path, int? ProtonMajor)
{
    public static PrefixReport Of(GameProfile game)
    {
        if (game.PrefixPath is not null)
        {
            return new PrefixReport(
                PrefixState.Present, game.PrefixPath, MajorOf(game.Proton?.Version));
        }

        // Without a prefix there is also no config_info to yield a major version.
        return new PrefixReport(
            game.Platform == GamePlatform.NativeLinux
                ? PrefixState.NotPossible
                : PrefixState.Missing,
            null,
            MajorOf(game.Proton?.Version));
    }

    /// <summary>
    /// The major version from a prefix version: <c>11.0-100</c> becomes <c>11</c>.
    ///
    /// <para>
    /// <b>A fork may put its name in front</b> (measured 2026-09-17,
    /// <c>messungen-anhaengen.md</c> §7.4): proton-cachyos writes <c>CachyOS-11.0-100</c>. The
    /// search therefore looks for the form <c>&lt;number&gt;.&lt;number&gt;-&lt;number&gt;</c> at
    /// the <b>end</b> of the line — the name sits before it, and a number within it does not win.
    /// </para>
    ///
    /// <para>
    /// <b>GE-Proton writes its own form</b> (measured 2026-09-23,
    /// <c>befund-neuinstallation-ge-proton.md</c> §4): <c>GE-Proton11-7</c> instead of the
    /// dotted format. The major version sits directly after the name.
    /// </para>
    ///
    /// <para>
    /// If no form matches, the answer is <c>null</c> — <b>unknown, not guessed</b>, the same
    /// stance as <see cref="GamePlatform.Unknown"/>.
    /// </para>
    /// </summary>
    private static int? MajorOf(string? prefixVersion)
    {
        if (prefixVersion is null)
        {
            return null;
        }

        var match = PrefixVersionPattern.Match(prefixVersion);
        if (!match.Success)
        {
            match = GeProtonPattern.Match(prefixVersion);
        }

        return match.Success
            && int.TryParse(
                match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }

    [GeneratedRegex(@"(\d+)\.\d+-\d+$")]
    private static partial Regex PrefixVersionPattern { get; }

    /// <summary>
    /// GE-Proton's form, <c>GE-Proton11-7</c>: the major is the number right after the name
    /// (measured 2026-09-23). Anchored at both ends — a looser pattern would guess, and a wrongly
    /// guessed package is worse than none (Spec s8.1 E-8).
    /// </summary>
    [GeneratedRegex(@"^GE-Proton(\d+)-\d+$")]
    private static partial Regex GeProtonPattern { get; }
}
