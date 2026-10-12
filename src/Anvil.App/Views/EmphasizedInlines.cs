using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Anvil.App.Views;

/// <summary>One piece of a sentence with an emphasized word.</summary>
/// <param name="Text">The piece's text, never empty.</param>
/// <param name="IsEmphasized">Is it the emphasized word?</param>
public readonly record struct EmphasizedSegment(string Text, bool IsEmphasized);

/// <summary>
/// Draws a sentence with one emphasized word into a <see cref="TextBlock"/> (spec s9.3:
/// <c>Decline</c>, <c>bubblewrap</c>; ViewModel side <c>EmphasizedText</c>). Attached
/// properties <c>Text</c> and <c>Emphasis</c>, plus an optional <c>EmphasisForeground</c> where
/// the drawing also brightens the word (the dialog); on every change the block's inlines are
/// rebuilt from <see cref="Split"/>.
///
/// <para>
/// <b>Attached properties instead of a converter:</b> <see cref="TextBlock.Inlines"/> is a
/// collection, and the sentence and its word come from two bindings — a converter would need a
/// multi-binding that hands out a fresh inline collection on every change. The class styles of
/// the block (<c>dep-note</c>, <c>dialog-text</c>) keep applying: the runs inherit size and
/// colour, only the weight of the word changes.
/// </para>
/// </summary>
public static class EmphasizedInlines
{
    /// <summary>
    /// The weight of the emphasized word: Medium, as the drawing sets it (<c>font-weight:500</c>,
    /// Settings-CeBezug).
    /// </summary>
    public static readonly FontWeight EmphasisWeight = FontWeight.Medium;

    /// <summary>The sentence; null or "" = none.</summary>
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(EmphasizedInlines));

    /// <summary>The word to set apart; null or "" = plain.</summary>
    public static readonly AttachedProperty<string?> EmphasisProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Emphasis", typeof(EmphasizedInlines));

    /// <summary>The colour of the word; null = the block's own (only the weight changes).</summary>
    public static readonly AttachedProperty<IBrush?> EmphasisForegroundProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("EmphasisForeground", typeof(EmphasizedInlines));

    static EmphasizedInlines()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Rebuild(block));
        EmphasisProperty.Changed.AddClassHandler<TextBlock>((block, _) => Rebuild(block));
        EmphasisForegroundProperty.Changed.AddClassHandler<TextBlock>((block, _) => Rebuild(block));
    }

    public static string? GetText(TextBlock block) => block.GetValue(TextProperty);

    public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

    public static string? GetEmphasis(TextBlock block) => block.GetValue(EmphasisProperty);

    public static void SetEmphasis(TextBlock block, string? value) => block.SetValue(EmphasisProperty, value);

    public static IBrush? GetEmphasisForeground(TextBlock block) => block.GetValue(EmphasisForegroundProperty);

    public static void SetEmphasisForeground(TextBlock block, IBrush? value)
        => block.SetValue(EmphasisForegroundProperty, value);

    /// <summary>
    /// Splits <paramref name="text"/> around the <b>first</b> occurrence of
    /// <paramref name="emphasis"/> (ordinal). Without an emphasis, or when the text does not
    /// contain it, the whole sentence is one plain piece; an empty text has no pieces.
    /// </summary>
    public static IReadOnlyList<EmphasizedSegment> Split(string text, string emphasis)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var start = emphasis.Length == 0 ? -1 : text.IndexOf(emphasis, StringComparison.Ordinal);
        if (start < 0)
        {
            return [new EmphasizedSegment(text, IsEmphasized: false)];
        }

        var end = start + emphasis.Length;
        var segments = new List<EmphasizedSegment>(3);
        if (start > 0)
        {
            segments.Add(new EmphasizedSegment(text[..start], IsEmphasized: false));
        }

        segments.Add(new EmphasizedSegment(emphasis, IsEmphasized: true));
        if (end < text.Length)
        {
            segments.Add(new EmphasizedSegment(text[end..], IsEmphasized: false));
        }

        return segments;
    }

    private static void Rebuild(TextBlock block)
    {
        var inlines = new InlineCollection();
        foreach (var segment in Split(GetText(block) ?? string.Empty, GetEmphasis(block) ?? string.Empty))
        {
            var run = new Run(segment.Text);
            if (segment.IsEmphasized)
            {
                run.FontWeight = EmphasisWeight;
                if (GetEmphasisForeground(block) is { } brush)
                {
                    run.Foreground = brush;
                }
            }

            inlines.Add(run);
        }

        block.Inlines = inlines;
    }
}
