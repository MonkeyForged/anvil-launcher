using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace Anvil.App;

/// <summary>
/// Macht aus dem Pfad, den <see cref="Anvil.Core.Steam.GameArtwork"/> gefunden hat, ein Bild
/// (Spec §7). Die Trennung ist Absicht: der Core kennt kein Avalonia, und die Oberfläche
/// kennt keine Suchregeln.
///
/// <para>
/// <b>Er wirft nie.</b> Ein Pfad aus einer Momentaufnahme kann veraltet sein — Steam räumt
/// seinen Cache auf, während Anvil läuft —, und eine halb heruntergeladene Datei ist kein
/// Bild. Beides ergibt <c>null</c> und eine Warnung; die Karte zeigt dann ihre neutrale
/// Fläche.
/// </para>
///
/// <para>
/// <b>Bewusst ohne Verkleinern und ohne Zwischenspeicher:</b> 17 Bilder à 460 × 215 sind
/// entpackt rund 6,4 MB, und die Quelle ist nur 1,4-mal so breit wie die Anzeige. Es gäbe
/// weder Speicher noch Schärfe zu gewinnen.
/// </para>
/// </summary>
public sealed class ArtworkLoader
{
    private readonly ILogger? _logger;

    public ArtworkLoader(ILogger? logger = null) => _logger = logger;

    /// <summary>Lädt das Bild, oder liefert <c>null</c> — und wirft dabei nie.</summary>
    public IImage? Load(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        // Der weite catch ist hier Absicht: Avalonias Bitmap meldet je nach Ursache
        // IOException, ArgumentException oder eine Skia-eigene Ausnahme. Der Vertrag lautet
        // "wirft nie" - eine Liste erlaubter Ausnahmen wäre irgendwann unvollständig und würde
        // dann das Fenster mitreißen.
        catch (Exception error)
        {
            _logger?.LogWarning("could not load artwork {Path}: {Reason}", path, error.Message);
            return null;
        }
    }
}
