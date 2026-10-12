using Anvil.Core.Packages;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Die eine Wahrheit darüber, welche Fassung einer Komponente gilt und wie vollständig sie in der
/// geteilten Ablage liegt (Spec S3.5b §3.3, E-6). Modul, Fläche <c>Dependencies</c> und
/// Bedarfszeile fragen dieselbe Stelle, statt dass jedes privat rechnet.
///
/// <para>
/// <b>Bauform des Modulbefunds</b> (<c>ModuleRegistry.ReadinessOf</c> / <c>CheckAsync</c> /
/// <c>ReadinessChanged</c>): ein abgelegter Befund je Schlüssel, <c>null</c> heißt „wird
/// geprüft", und ein Generationszähler je Schlüssel verhindert, dass ein überholter Lauf ein
/// neueres Ergebnis überschreibt.
/// </para>
///
/// <para>
/// <b>Kein Prüfsummen-Zwischenspeicher</b> (S3.5a E-3, unverändert): Gehalten wird nur das
/// <b>Ergebnis</b> der letzten Prüfung, wie die Registry den Befund hält.
/// </para>
///
/// <para>
/// <b>Mit Sperre, anders als die Registry:</b> Die Erkennung läuft auf dem Thread-Pool, der
/// Abschluss eines Laufs landet also auf einem fremden Faden, während der UI-Faden schon den
/// nächsten anstößt. Die Wörterbücher dürfen dabei nicht gleichzeitig beschrieben werden.
/// </para>
/// </summary>
public sealed class ComponentInventory
{
    private readonly IReadOnlyList<PrefixComponent> _catalog;
    private readonly Func<string, CatalogEntry?> _sourceLookup;
    private readonly Func<string, string?> _readChoice;
    private readonly Action<string, string?> _writeChoice;
    private readonly ILogger? _logger;
    private readonly Func<string, string>? _sha256;

    private readonly object _gate = new();

    /// <summary>
    /// Der zuletzt abgelegte Befund je versionsfreiem Schlüssel; ein fehlender Eintrag zählt wie
    /// <c>null</c> — „wird geprüft".
    /// </summary>
    private readonly Dictionary<string, ComponentAvailability?> _availability = new(StringComparer.Ordinal);

    /// <summary>
    /// Die Nummer des jeweils zuletzt gestarteten Laufs je Schlüssel — der Wächter gegen ein
    /// überholtes Ergebnis (E-6). Ein fehlender Eintrag zählt als 0.
    /// </summary>
    private readonly Dictionary<string, int> _generation = new(StringComparer.Ordinal);

    /// <param name="catalog">Der Komponenten-Katalog, aus dem die Kandidaten stammen.</param>
    /// <param name="store">Die geteilte Ablage, gegen die erkannt wird.</param>
    /// <param name="sourceLookup">Das zweite Manifest (<c>PackageCatalog.SourceFor</c>).</param>
    /// <param name="readChoice">Die gespeicherte Fassungswahl je Schlüssel, oder <c>null</c>.</param>
    /// <param name="writeChoice">Die Fassungswahl zurückschreiben; <c>null</c> löscht sie.</param>
    /// <param name="logger">Die Kategorie bestimmt der Besitzer; <c>null</c> heißt stumm.</param>
    /// <param name="sha256">Die Prüfsumme für Stufe 2; <c>null</c> heißt die echte SHA-256.</param>
    public ComponentInventory(
        IReadOnlyList<PrefixComponent> catalog,
        PackageStore store,
        Func<string, CatalogEntry?> sourceLookup,
        Func<string, string?> readChoice,
        Action<string, string?> writeChoice,
        ILogger? logger = null,
        Func<string, string>? sha256 = null)
    {
        _catalog = catalog;
        Store = store;
        _sourceLookup = sourceLookup;
        _readChoice = readChoice;
        _writeChoice = writeChoice;
        _logger = logger;
        _sha256 = sha256;
    }

    /// <summary>Die geteilte Ablage — für das Holen, das dort ablegt.</summary>
    public PackageStore Store { get; }

    /// <summary>
    /// Der Befund einer Komponente hat sich geändert; das Argument ist der versionsfreie
    /// Schlüssel.
    ///
    /// <para>
    /// <b>Feuert zweimal je Prüfung</b> — beim Verwerfen (<c>null</c>) und beim Ablegen — und
    /// einmal bei <see cref="Choose"/>. Ein überholter Lauf meldet nicht.
    /// </para>
    ///
    /// <para>
    /// <b>Kommt von dem Faden, auf dem der Lauf fertig wird</b>, in der Regel einem Faden des
    /// Thread-Pools. Wer daraus die Oberfläche bedient, marschiert selbst auf den UI-Faden.
    /// </para>
    /// </summary>
    public event EventHandler<string>? AvailabilityChanged;

    /// <summary>Die wählbaren Fassungen für diese Forderung, in Katalogreihenfolge.</summary>
    public IReadOnlyList<PrefixComponent> Candidates(PrefixComponent required)
        => PrefixComponentResolver.Candidates(_catalog, required);

    /// <summary>
    /// Die geltende Fassung für diese Forderung, mit der gespeicherten Wahl (Spec S3.5b §3.2).
    /// Eine Wahl, die kein Kandidat ist, wird ignoriert und bleibt gespeichert.
    /// </summary>
    public PrefixComponent Resolve(PrefixComponent required)
        => PrefixComponentResolver.Resolve(_catalog, required, _readChoice(required.Key));

    /// <summary>
    /// Speichert die Fassungswahl, verwirft den Befund und meldet. Die Generation steigt mit:
    /// Ein Lauf, der noch die alte Fassung prüft, legt sein Ergebnis danach nicht mehr ab.
    /// </summary>
    public void Choose(string key, string version)
    {
        _writeChoice(key, version);

        lock (_gate)
        {
            _generation[key] = _generation.GetValueOrDefault(key) + 1;
            _availability[key] = null;
        }

        _logger?.LogInformation("component version chosen: {Key} {Version}", key, version);
        AvailabilityChanged?.Invoke(this, key);
    }

    /// <summary>
    /// Der zuletzt abgelegte Befund zu diesem Schlüssel, oder <c>null</c> — „wird geprüft" oder
    /// noch nie geprüft.
    /// </summary>
    public ComponentAvailability? AvailabilityOf(string key)
    {
        lock (_gate)
        {
            return _availability.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Löst die Zutaten einer schon aufgelösten Fassung gegen die geteilte Ablage auf: Slot → voller
    /// Pfad, nur für vorhandene Zutaten (Spec S3.9 E-3, vormals <c>WandBridgeModule.ResolveIngredients</c>).
    /// Dieselbe Erkennung wie <see cref="CheckAsync"/> — Name, dann Größe und Prüfsumme —, aber
    /// <b>synchron und ohne abgelegten Befund</b>: Der Prefix-Schritt braucht die Pfade jetzt.
    /// Ohne Rezept leer.
    /// </summary>
    public IReadOnlyDictionary<string, string> IngredientPaths(PrefixComponent resolved)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in ComponentAvailability.For(resolved, Store.List(), _sourceLookup, _sha256).Ingredients)
        {
            if (state.Present)
            {
                result[state.Ingredient.Slot] = state.Path!;
            }
        }

        return result;
    }

    /// <summary>
    /// Prüft die geltende Fassung dieser Forderung gegen die Ablage: Befund auf <c>null</c> und
    /// melden — erkennen — ablegen und melden (Spec S3.5b §3.3).
    ///
    /// <para>
    /// <b>Gibt den Befund auch zurück</b>, wie <c>ModuleRegistry.CheckAsync</c>: Auch ein
    /// überholter Lauf bekommt sein eigenes Ergebnis — er legt es nur nicht ab und meldet nicht.
    /// </para>
    /// </summary>
    public async Task<ComponentAvailability> CheckAsync(
        PrefixComponent required, CancellationToken cancellationToken = default)
    {
        var component = Resolve(required);
        var key = required.Key;

        int generation;
        lock (_gate)
        {
            generation = _generation.GetValueOrDefault(key) + 1;
            _generation[key] = generation;
            _availability[key] = null;
        }

        AvailabilityChanged?.Invoke(this, key);

        // Auf dem Thread-Pool (Spec S3.5b §3.3): Stufe 2 der Erkennung liest bis zu 120 MB, und
        // die Settings-Seite stünde so lange.
        var result = await Task.Run(
            () => ComponentAvailability.For(component, Store.List(), _sourceLookup, _sha256),
            cancellationToken);

        lock (_gate)
        {
            if (_generation.GetValueOrDefault(key) != generation)
            {
                // Überholt (E-6): Ablegen hieße, ein neueres Ergebnis mit einem älteren zu
                // überschreiben — also weder ablegen noch melden.
                return result;
            }

            _availability[key] = result;
        }

        _logger?.LogDebug("component {Key} {Version}: {State}", key, component.Version, result.State);
        AvailabilityChanged?.Invoke(this, key);
        return result;
    }
}
