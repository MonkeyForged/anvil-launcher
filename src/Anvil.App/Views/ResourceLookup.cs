using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Anvil.App.Views;

/// <summary>
/// Schlägt einen Ressourcen-Schlüssel aus dem ViewModel im Theme nach — eine Geometrie aus
/// <c>Icons.axaml</c> oder einen Brush aus <c>Colors.axaml</c>. So bleibt im ViewModel eine
/// Zeichenkette, und Geometrie wie Farbe bleiben im Theme.
/// </summary>
public sealed class ResourceLookup : IValueConverter
{
    public static readonly ResourceLookup Instance = new();

    public object? Convert(
        object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || Application.Current is null)
        {
            return null;
        }

        return Application.Current.TryFindResource(key, out var resource) ? resource : null;
    }

    public object ConvertBack(
        object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
