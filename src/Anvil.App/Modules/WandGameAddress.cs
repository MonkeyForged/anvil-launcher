namespace Anvil.App.Modules;

/// <summary>
/// The address that opens Wand on a game and attaches it there — measured on 2026-09-20
/// (wand-spielwahl.md §4.3, §4.4).
///
/// <para>
/// <b>Only ASCII digits pass</b> (spec s7.1 E-4): both numbers come from a file nobody promised
/// us, and here text becomes part of a program call. Digits are what Wand serves today; an
/// <c>&amp;</c> or a space would add to the call instead of naming a game, and anything else would
/// be a guess. <see cref="char.IsAsciiDigit"/>, not <see cref="char.IsDigit"/>, which also
/// accepts full-width and other scripts' digits.
/// </para>
/// </summary>
public static class WandGameAddress
{
    /// <summary>The address for <paramref name="title"/>, or null when a number is not plain digits.</summary>
    public static string? For(WandTitle title)
        => IsNumber(title.TitleId) && IsNumber(title.GameId)
            ? $"wemod://play?titleId={title.TitleId}&gameId={title.GameId}"
            : null;

    private static bool IsNumber(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);
}
