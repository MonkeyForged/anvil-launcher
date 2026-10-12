using Avalonia;
using Avalonia.Controls;

namespace Anvil.App.Views;

/// <summary>
/// The panel of the game grid: it picks as many columns as fit at a minimum width and
/// <b>stretches the cards to the full width</b>, so no margin is ever left over.
///
/// <para>
/// <b>Why no <c>WrapPanel</c> any more:</b> with a fixed card width, almost a whole card of
/// space stayed unused just before each wrap — 255 px at a window width of 1500 px, 224 px at
/// 1800 px. The mockup is drawn at a fixed 1280 × 800 and happens to fit exactly there; at every
/// width in between a gap opened. The owner reported it on 2026-09-04; the rework replaces
/// decision E4 of the spec.
/// </para>
///
/// <para>
/// <b>At most 460 px wide</b> (<see cref="DefaultMaxItemWidth"/>) and <b>no limit on the number
/// of columns</b> — design team on requirement 12. An interim cap at five columns was
/// withdrawn: it was meant to limit a width and instead created the only unlimited one (at
/// 3840 px it would have been 636 px per card).
/// </para>
///
/// <para>
/// <b>The artwork grows along</b>, in the mockup's aspect ratio — see <see cref="ArtworkRatio"/>
/// and <see cref="AspectPanel"/>. A stretched card therefore looks like the drawn one, only
/// larger; and thanks to the upper limit never blurrier.
/// </para>
/// </summary>
public sealed class CardGrid : Panel
{
    /// <summary>
    /// Below this width the grid wraps to one column fewer. 300 px since s15.3 (280 before): the
    /// owner found the cards too cramped. It is the largest width at which two cards still stay
    /// below <see cref="DefaultMaxItemWidth"/> just before the grid tips to three columns
    /// (<c>(3 · 300 + 2 · 16 − 16) / 2 = 458</c>); more would upscale the artwork or leave a strip
    /// free between two and three columns. Design request 69 carries the number to the design.
    /// </summary>
    public const double DefaultMinItemWidth = 300;

    /// <summary>
    /// Space between the cards, horizontally and vertically: space-4 of the design package
    /// since 1.3 (element description §11; 13 px before).
    /// </summary>
    public const double DefaultItemSpacing = 16;

    /// <summary>
    /// A card grows no wider than this (design team, answer to requirement 12). <b>460 px is the
    /// resolution of <c>library_header.jpg</c></b> — Steam offers no more for the landscape
    /// format, measured over all 1278 entries of the <c>librarycache</c>. Beyond it a wider card
    /// shows nothing more, only the same 460 pixels, softer.
    ///
    /// <para>
    /// <b>The limit applies only with one column.</b> From two columns on, the 300-px rule keeps
    /// the card below 458 px on its own (at most <c>300 + 316 / columns</c>). In the single-column
    /// band the space suffices for one card over 460 px but not for two over 300 px; there the
    /// card stops growing and almost 156 px stay free. It is the only place where empty space is
    /// allowed: beside a single column a strip stands out less than upscaled artwork.
    /// </para>
    /// </summary>
    public const double DefaultMaxItemWidth = 460;

    /// <summary>
    /// Width to height of a card's artwork, taken from the mockup (318 × 132). Deliberately not
    /// the aspect ratio of the source file (460 × 215 ≙ 2.14): the design crops a little flatter,
    /// and that stays.
    /// </summary>
    public const double ArtworkRatio = 318d / 132d;

    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(
            nameof(MinItemWidth), DefaultMinItemWidth);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<CardGrid, double>(
            nameof(ItemSpacing), DefaultItemSpacing);

    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(
            nameof(MaxItemWidth), DefaultMaxItemWidth);

    static CardGrid()
    {
        AffectsMeasure<CardGrid>(
            MinItemWidthProperty, ItemSpacingProperty, MaxItemWidthProperty);
    }

    /// <summary>Below this width a column is taken away.</summary>
    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    /// <summary>Space between the cards.</summary>
    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>Upper limit of the card width — the resolution of the artwork.</summary>
    public double MaxItemWidth
    {
        get => GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    /// <summary>
    /// The grid's whole calculation as a pure function — so it can be checked without a window,
    /// without the Avalonia runtime and without a measure pass.
    /// </summary>
    /// <param name="available">The space in width. May be unusable (see below).</param>
    /// <param name="minItemWidth">The minimum width of a card.</param>
    /// <param name="spacing">The space between two cards.</param>
    /// <param name="maxItemWidth">
    /// Upper limit of the card width. Applies only with one column; above that the minimum
    /// width itself sees to it that no card gets that wide.
    /// </param>
    /// <returns>
    /// Number of columns and the width of <b>one</b> card. Together with the spaces the cards
    /// fill exactly <paramref name="available"/>.
    /// </returns>
    /// <remarks>
    /// A panel is measured with 0, negative or infinite sizes while it is built. In these cases
    /// the answer is one column at the minimum width — not a division by zero.
    /// </remarks>
    public static (int Columns, double ItemWidth) Solve(
        double available,
        double minItemWidth,
        double spacing,
        double maxItemWidth = DefaultMaxItemWidth)
    {
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0)
        {
            return (1, minItemWidth);
        }

        // n columns need n minimum widths and n−1 spaces. Solved for n, and at least one
        // column: a window that fits no whole card still shows one.
        var columns = Math.Max(
            1, (int)Math.Floor((available + spacing) / (minItemWidth + spacing)));

        // More columns are not possible without falling below the minimum width — so the upper
        // limit can only cap, not wrap. With one column this costs some margin and everywhere
        // saves the blur.
        var itemWidth = Math.Min(
            (available - ((columns - 1) * spacing)) / columns, maxItemWidth);

        return (columns, itemWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, itemWidth) = Solve(availableSize.Width, MinItemWidth, ItemSpacing, MaxItemWidth);
        var spacing = ItemSpacing;

        // All cards are built alike and therefore equally tall; the tallest decides the row.
        var rowHeight = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        var rows = (int)Math.Ceiling(Children.Count / (double)columns);
        var height = rows == 0 ? 0 : (rows * rowHeight) + ((rows - 1) * spacing);

        return new Size(
            (columns * itemWidth) + ((columns - 1) * spacing), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Solve(finalSize.Width, MinItemWidth, ItemSpacing, MaxItemWidth);
        var spacing = ItemSpacing;

        // Measure again instead of trusting the desired size from MeasureOverride. The width
        // while arranging is not always the one while measuring: if the grid tips to one column
        // more or fewer, the number of rows changes and with it the height — the vertical scroll
        // bar appears or disappears and takes the width with it. Without this step the cards
        // keep their old, too narrow width and gaps open between them (observed by the owner on
        // 2026-09-04 during the transition).
        var rowHeight = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }

        for (var index = 0; index < Children.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;

            Children[index].Arrange(new Rect(
                column * (itemWidth + spacing),
                row * (rowHeight + spacing),
                itemWidth,
                rowHeight));
        }

        return finalSize;
    }
}
