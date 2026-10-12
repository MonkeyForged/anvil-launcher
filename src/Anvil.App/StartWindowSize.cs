using Avalonia;

namespace Anvil.App;

/// <summary>
/// The main window's size at start (stories s15.3, s15.6): wide enough for two large game cards
/// side by side, made to fit the primary screen and centred on it. Sizes are those of the window's
/// content, as Avalonia's <c>Width</c> and <c>Height</c> count them; the package's frame lies
/// around it.
/// </summary>
public static class StartWindowSize
{
    /// <summary>
    /// The owner's wish (s15.6). Measured on the rendered main window in the package's frame: the
    /// grid gets the window width less 630 px (navigation 220, right column 352, the grid's margins
    /// 24 and 34), so 1500 gives two cards of about 427 px. The grid tips to three columns of
    /// 300 px at 1562 — 62 px of air, so a pixel more or less of frame or scroll bar does not open
    /// Anvil with three narrow cards.
    /// </summary>
    public const double PreferredWidth = 1500;

    /// <summary>The owner's wish (s15.6).</summary>
    public const double PreferredHeight = 860;

    /// <summary>
    /// What the package's frame adds around a window's content — the main window's and every
    /// dialog's (<see cref="Views.DialogPresenter"/>): 33 px of frame and shadow left and right;
    /// above, the 30-px title bar with frame and shadow (51), below frame and shadow (45).
    /// Measured with HeadlessWindowFrame (stories s14.1, s15.3), which lays the frame as Avalonia
    /// does on X11 — the guide tool's 1400 × 900 window becomes a 1466 × 996 picture. Avalonia
    /// reports no frame size for a drawn frame before the window stands, so it is a measure here.
    /// </summary>
    internal static readonly Size FrameSize = new(66, 96);

    /// <summary>
    /// The space a window's content has on a screen: the working area (panels excluded) in logical
    /// pixels, less the frame around the content.
    /// </summary>
    /// <param name="workingArea">The screen's working area in physical pixels.</param>
    /// <param name="scaling">The screen's scaling: physical pixels per logical pixel.</param>
    /// <returns>The largest content size whose framed window fits the working area.</returns>
    public static Size ContentSpace(PixelRect workingArea, double scaling)
    {
        var logical = workingArea.ToRect(scaling).Size;
        return new Size(logical.Width - FrameSize.Width, logical.Height - FrameSize.Height);
    }

    /// <summary>
    /// The size the window opens with: <paramref name="preferred"/>, made smaller where the screen
    /// offers less, never below <paramref name="minimum"/> — on a screen smaller than the minimum,
    /// the minimum wins.
    /// </summary>
    /// <param name="preferred">The size the window would like, in logical pixels.</param>
    /// <param name="minimum">The window's minimum size, in logical pixels.</param>
    /// <param name="workingArea">
    /// The space on the screen in logical pixels (see <see cref="ContentSpace"/>); null without a
    /// screen, and then <paramref name="preferred"/> stays as it is.
    /// </param>
    /// <returns>The window's start size in logical pixels.</returns>
    public static Size Fit(Size preferred, Size minimum, Size? workingArea)
    {
        if (workingArea is not { } space)
        {
            return preferred;
        }

        return new Size(
            Math.Max(minimum.Width, Math.Min(preferred.Width, space.Width)),
            Math.Max(minimum.Height, Math.Min(preferred.Height, space.Height)));
    }

    /// <summary>
    /// Where a window stands centred in a screen's working area, frame included (s15.6).
    /// </summary>
    /// <param name="workingArea">The screen's working area in physical pixels.</param>
    /// <param name="scaling">The screen's scaling: physical pixels per logical pixel.</param>
    /// <param name="content">The window's content size in logical pixels.</param>
    /// <returns>
    /// The top-left of the frame's outer edge in physical pixels — what Avalonia's <c>Position</c>
    /// means for a window in the package's frame (measured on X11, s15.6).
    /// </returns>
    public static PixelPoint CentredPosition(PixelRect workingArea, double scaling, Size content)
    {
        var framedWidth = (content.Width + FrameSize.Width) * scaling;
        var framedHeight = (content.Height + FrameSize.Height) * scaling;
        return new PixelPoint(
            workingArea.X + (int)Math.Round((workingArea.Width - framedWidth) / 2),
            workingArea.Y + (int)Math.Round((workingArea.Height - framedHeight) / 2));
    }
}
