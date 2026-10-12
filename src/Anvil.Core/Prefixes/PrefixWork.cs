using Anvil.Core.Steam;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Ein Spiel, dessen Prefix eine geforderte Komponente fehlt — mit den Anforderern (Spec S3.9
/// §3.2). Wird nur gebildet, wenn <see cref="Missing"/> nicht leer ist.
/// </summary>
/// <param name="Game">Das betroffene Spiel.</param>
/// <param name="Missing">Die fehlenden, herstellbaren Komponenten — <see cref="PrefixPreparer.MissingFor"/>.</param>
public sealed record PrefixWorkItem(GameProfile Game, IReadOnlyList<RequiredPrefixComponent> Missing);

/// <summary>
/// Die Liste der Spiele, die <c>needs preparing</c> zeigen sollen (Spec S3.9 §3.2). Keine eigene
/// Schleife: Jeder Rescan der Bibliothek, das Ende einer Sitzung und Änderungen an Inventar oder
/// Registry stoßen von außen einen <see cref="RefreshAsync"/> an.
///
/// <para>
/// <b>Gespeicherter Stand, nicht ungespeicherter</b> (E-4): Anders als beim Speichern selbst zählt
/// hier nur, was nach dem Speichern gilt — sonst änderte <c>Discard</c> die Arbeitsliste, und ein
/// ungespeicherter Schalter steht ohnehin in der ungespeicherten Leiste, die Vorrang hat.
/// </para>
///
/// <para>
/// <b>Ein Lauf verwirft den vorherigen, nicht sich selbst</b> (E-6 in <see cref="ComponentInventory"/>,
/// dieselbe Bauform): Eine mitlaufende Nummer je Aufruf lässt einen überholten Lauf sein Ergebnis
/// stillschweigend verwerfen, statt ein neueres zu überschreiben.
/// </para>
/// </summary>
public sealed class PrefixWork
{
    private readonly Func<GameProfile, IReadOnlyList<RequiredPrefixComponent>> _missing;
    private readonly Func<SteamSnapshot> _current;
    private readonly ILogger? _logger;

    private readonly object _gate = new();
    private IReadOnlyList<PrefixWorkItem> _items = [];
    private int _generation;

    /// <param name="missing">
    /// Die fehlenden Komponenten je Spiel, gespeicherter Stand (E-4) — die Root reicht
    /// <c>game =&gt; preparer.MissingFor(game, includingUnsaved: false)</c>.
    /// </param>
    /// <param name="current">Das Ist — die Root reicht <c>() =&gt; library.Current</c>.</param>
    /// <param name="logger">Die Kategorie bestimmt der Besitzer; <c>null</c> heißt stumm.</param>
    public PrefixWork(
        Func<GameProfile, IReadOnlyList<RequiredPrefixComponent>> missing,
        Func<SteamSnapshot> current,
        ILogger? logger = null)
    {
        _missing = missing;
        _current = current;
        _logger = logger;
    }

    /// <summary>Jede Änderung der AppId-Liste — nicht bei bloß neuen Profil-Objekten mit derselben Liste.</summary>
    public event EventHandler? Changed;

    /// <summary>Die Spiele mit fehlenden Komponenten, in Bibliotheksreihenfolge.</summary>
    public IReadOnlyList<PrefixWorkItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items;
            }
        }
    }

    /// <summary>
    /// Rechnet die Liste neu. Wirft nie: Hinter der Frage stecken Registry, Inventar und
    /// Dateisystem — ein Fehler dort darf die Oberfläche nicht mitreißen (dieselbe Haltung wie
    /// beim Befund, S3.4).
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        IReadOnlyList<PrefixWorkItem> items;
        try
        {
            var snapshot = _current();
            items = await Task.Run(() => snapshot.Games
                .Select(game => new PrefixWorkItem(game, _missing(game)))
                .Where(item => item.Missing.Count > 0)
                .ToList()).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "prefix work could not be refreshed");
            return;
        }

        bool changed;
        lock (_gate)
        {
            if (generation != _generation)
            {
                // Überholt (Spec §3.2): Der ältere Lauf endet nach dem neueren und legt nichts ab.
                return;
            }

            changed = !_items.Select(Id).SequenceEqual(items.Select(Id));
            _items = items;
        }

        if (changed)
        {
            _logger?.LogInformation("prefix work: {Count} game(s) need preparing", items.Count);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        static uint Id(PrefixWorkItem item) => item.Game.Game.AppId;
    }
}
