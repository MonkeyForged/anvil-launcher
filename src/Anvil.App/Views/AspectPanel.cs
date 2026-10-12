using Avalonia;
using Avalonia.Controls;

namespace Anvil.App.Views;

/// <summary>
/// Ein Panel, dessen Höhe der Breite in einem festen Seitenverhältnis folgt. Seine Kinder
/// liegen übereinander und füllen es ganz aus — wie bei <see cref="Panel"/> üblich, nur mit
/// gebundener Höhe.
///
/// <para>
/// Gebraucht für die Bildfläche der Spielkarte, seit die Karten mit dem Fenster mitwachsen
/// (<see cref="CardGrid"/>). Dort liegen Artwork, Abdunklung, Plattform-Symbol und Name
/// übereinander — ein <see cref="Decorator"/> mit nur einem Kind reicht dafür nicht.
/// </para>
///
/// <para>
/// <b>Warum überhaupt:</b> Bei fester Höhe würde eine breiter gewordene Karte das Artwork
/// immer stärker beschneiden — bei 2 Spalten und 1400 px Fensterbreite aus 2,4 : 1 ein
/// 3,0 : 1. Mit mitlaufender Höhe sieht jede Karte aus wie die gezeichnete, nur größer.
/// </para>
///
/// <para>
/// Bewusst ein eigenes Steuerelement statt einer Bindung auf <c>Bounds.Width</c>: Letztere
/// kennt die Breite erst nach dem Anordnen, hinkt also einen Layout-Durchlauf hinterher.
/// Hier steht die Höhe schon beim Messen fest.
/// </para>
/// </summary>
public sealed class AspectPanel : Panel
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectPanel, double>(nameof(Ratio), 1);

    static AspectPanel()
    {
        AffectsMeasure<AspectPanel>(RatioProperty);
    }

    /// <summary>Breite geteilt durch Höhe. Ein Wert ≤ 0 schaltet die Regel ab.</summary>
    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var ratio = Ratio;
        var width = availableSize.Width;

        // Ohne brauchbare Breite oder ohne Verhältnis bleibt es beim gewöhnlichen Messen —
        // ein Panel wird während des Aufbaus mit unendlichen Maßen gemessen.
        if (ratio <= 0 || double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
        {
            return base.MeasureOverride(availableSize);
        }

        var size = new Size(width, width / ratio);
        foreach (var child in Children)
        {
            child.Measure(size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(new Rect(finalSize));
        }

        return finalSize;
    }
}
