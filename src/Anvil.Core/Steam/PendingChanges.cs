using Anvil.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Steam;

/// <summary>
/// Die eine Wahrheit über „was ist ungespeichert" (Spec S4.5 §3.1): je AppId ein Wunsch, den
/// die Oberfläche sammelt und die Transaktion aus S4.4 schreibt.
///
/// <para>
/// <b>Zwei Setzer statt eines <c>Set</c></b> (E-A): S4.6 ändert Proton und Startoptionen an zwei
/// Stellen, und <see cref="SteamGameChange"/> benutzt <c>null</c> bereits für „nicht anfassen" —
/// ein kombinierter Setzer müsste damit auch noch „dieses Feld meine ich nicht" ausdrücken.
/// </para>
///
/// <para>
/// <b>Jeder Setzer sortiert sofort aus</b> (Projektplan M4, Entscheidung 9): Was dem Ist gleicht,
/// kommt gar nicht erst hinein — dieselbe <see cref="SteamChangeFilter.Reduce"/>, die die
/// Transaktion nach dem Beenden von Steam ein zweites Mal ruft. Eine Regel, zwei Orte.
/// </para>
///
/// <para>
/// <b>Faden:</b> Gesetzt und geleert wird vom UI-Faden. Nur die Fortsetzung in
/// <see cref="SaveAsync"/> kommt vom Hintergrund-Faden — <b>wer <see cref="Changed"/> hört, postet
/// selbst auf den UI-Faden</b> (E-M). Das tut genau eine Stelle: die Speicherleiste.
/// </para>
///
/// <para>
/// <b>Drei Sammlungen, zwei davon seit S3.8</b> (E-2): Steam-Wünsche (<see cref="Current"/>),
/// Je-Spiel-Schalter (<see cref="Switches"/>) und, seit S5.4, Je-Spiel-Optionen
/// (<see cref="Options"/>) stehen nebeneinander, weil keine von ihnen ein
/// <see cref="SteamGameChange"/> ist — sie treffen keine Steam-Datei. <c>Any</c>, <c>AppIds</c>
/// und <c>Contains</c> zählen alle drei, damit die Leiste ein Spiel nur einmal zeigt, dessen
/// Startoption, dessen Schalter und dessen Option zugleich offen stehen.
/// </para>
///
/// <para>
/// <b>Wer den Wunsch hält, liefert den Schritt</b> (E-1): Weder ein Schalter noch eine Option
/// braucht geschlossenes Steam — <see cref="SaveAsync"/> reicht beide deshalb im selben
/// <see cref="SaveStep"/> ohne <see cref="SaveStep.NeedsSteamClosed"/> an die Transaktion, statt
/// der Registry einen eigenen Weg zum Schreiben zu geben. Ein zweiter Schritt für dieselbe Sorte
/// Arbeit wäre eine zweite Zeile in der Speicherleiste für einen Vorgang, den der Nutzer als
/// einen sieht (Spec S5.4 §3.4).
/// </para>
/// </summary>
public sealed class PendingChanges
{
    /// <summary>Beschriftung des Schritts, der die Je-Spiel-Schalter schreibt (S3.8 E-3).</summary>
    public const string SettingsStep = "Saving settings…";

    private readonly Func<SteamSnapshot> _current;
    private readonly Func<
        IReadOnlyList<SteamGameChange>,
        IReadOnlyList<ChangeOrigin>,
        Action<SaveProgress>?,
        IReadOnlyList<SaveStep>,
        Task<TransactionResult>> _save;

    private readonly Action<IReadOnlyList<ModuleGameSwitch>> _saveSwitches;

    private readonly Action<IReadOnlyList<ModuleGameOptionWish>>? _saveOptions;

    private readonly ILogger? _logger;

    /// <summary>
    /// Liefert je AppId der Menge einen Prefix-Schritt (Spec S3.9 §3.3, E-10) — die Root löst die
    /// AppIds gegen das aktuelle Abbild auf und reicht <see cref="Anvil.Core.Prefixes.PrefixPreparer.StepsFor"/>
    /// mit <c>includingUnsaved: true</c>. <c>null</c> heißt: keine Vorbereitung beim Speichern
    /// (z. B. in älteren Tests).
    /// </summary>
    private readonly Func<IReadOnlyList<uint>, IReadOnlyList<SaveStep>>? _prefixSteps;

    /// <summary>In der Reihenfolge des Eintragens — die Leiste nennt die Namen so.</summary>
    private readonly List<SteamGameChange> _wishes = [];

    /// <summary>
    /// Die vorgemerkten Je-Spiel-Schalter (S3.8), in der Reihenfolge des Eintragens — dieselbe
    /// Begründung wie bei <see cref="_wishes"/>.
    /// </summary>
    private readonly List<ModuleGameSwitch> _switches = [];

    /// <summary>
    /// Die vorgemerkten Je-Spiel-Optionen (S5.4), in der Reihenfolge des Eintragens — dieselbe
    /// Begründung wie bei <see cref="_wishes"/>.
    /// </summary>
    private readonly List<ModuleGameOptionWish> _options = [];

    /// <summary>
    /// Wer welches Feld angefasst hat (S4.8 E-C). Getrennt von den Wünschen, weil
    /// <see cref="SteamChangeFilter.Reduce"/> Felder streicht und der Absender mitgehen muss —
    /// <see cref="Sync"/> hält beides zusammen.
    /// </summary>
    private readonly Dictionary<(uint AppId, SteamField Field), string> _by = [];

    /// <param name="current">Das Ist — die Root reicht <c>() =&gt; library.Current</c>.</param>
    /// <param name="save">
    /// Der Schreibvorgang — die Root reicht <c>SteamTransaction.SaveAsync</c> als Lambda: Der
    /// optionale <c>progress</c>-Parameter macht die Methodengruppe unbrauchbar (Fund aus S4.4).
    /// </param>
    /// <param name="saveSwitches">
    /// Schreibt die Je-Spiel-Schalter (S3.8 E-3) — die Root reicht
    /// <c>ModuleRegistry.SaveEnabledFor</c>.
    /// </param>
    /// <param name="logger">Kategorie <c>Steam</c>; null heißt stumm.</param>
    /// <param name="prefixSteps">Siehe Feld gleichen Namens. Letzter Parameter, optional, damit
    /// bestehende Aufrufe unverändert bleiben.</param>
    /// <param name="saveOptions">
    /// Schreibt die Je-Spiel-Optionen (S5.4 §3.4) — die Root reicht <c>ModuleGameOptions.Save</c>
    /// oder ähnliches. Optionaler letzter Parameter, dieselbe Begründung wie bei
    /// <paramref name="prefixSteps"/> (Spec E-7): die Alternative wären 20 angefasste
    /// Aufrufstellen in Tests, die mit dieser Story nichts zu tun haben.
    /// </param>
    public PendingChanges(
        Func<SteamSnapshot> current,
        Func<
            IReadOnlyList<SteamGameChange>,
            IReadOnlyList<ChangeOrigin>,
            Action<SaveProgress>?,
            IReadOnlyList<SaveStep>,
            Task<TransactionResult>> save,
        Action<IReadOnlyList<ModuleGameSwitch>> saveSwitches,
        ILogger? logger = null,
        Func<IReadOnlyList<uint>, IReadOnlyList<SaveStep>>? prefixSteps = null,
        Action<IReadOnlyList<ModuleGameOptionWish>>? saveOptions = null)
    {
        _current = current;
        _save = save;
        _saveSwitches = saveSwitches;
        _logger = logger;
        _prefixSteps = prefixSteps;
        _saveOptions = saveOptions;
    }

    /// <summary>Jede Änderung der Menge — dazu, weg, geleert. Keine Feinauflösung.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<SteamGameChange> Current => _wishes;

    /// <summary>Die vorgemerkten Je-Spiel-Schalter (S3.8), in der Reihenfolge des Eintragens.</summary>
    public IReadOnlyList<ModuleGameSwitch> Switches => _switches;

    /// <summary>Die vorgemerkten Je-Spiel-Optionen (S5.4), in der Reihenfolge des Eintragens.</summary>
    public IReadOnlyList<ModuleGameOptionWish> Options => _options;

    /// <summary>
    /// Alle AppIds mit offenem Wunsch, ohne Doppelte — erst die Steam-Wünsche in ihrer
    /// Reihenfolge, dann die Schalter- und Options-AppIds, die noch nicht vorkommen (S3.8 E-2).
    /// </summary>
    public IReadOnlyList<uint> AppIds
        => _wishes.Select(wish => wish.AppId)
            .Concat(_switches.Select(wish => wish.AppId))
            .Concat(_options.Select(wish => wish.AppId))
            .Distinct()
            .ToList();

    /// <summary>Ob überhaupt etwas offen steht — Steam-Wunsch, Schalter oder Option (S3.8 E-2).</summary>
    public bool Any => _wishes.Count > 0 || _switches.Count > 0 || _options.Count > 0;

    public bool Contains(uint appId)
        => _wishes.Any(wish => wish.AppId == appId)
            || _switches.Any(wish => wish.AppId == appId)
            || _options.Any(wish => wish.AppId == appId);

    /// <summary>
    /// Braucht das Schreiben geschlossenes Steam? Nur die Steam-Wünsche — ein Schalter ohne
    /// weiteren Steam-Wunsch schreibt bei laufendem Steam (S3.8 E-1).
    /// </summary>
    public bool NeedsSteamClosed => _wishes.Count > 0;

    /// <summary>
    /// Die Absender, nach Spiel und Feld sortiert. Die Reihenfolge trägt keine Bedeutung — sie
    /// macht nur Vermerke und Tests wiederholbar.
    /// </summary>
    public IReadOnlyList<ChangeOrigin> Origins
        => _by
            .OrderBy(pair => pair.Key.AppId)
            .ThenBy(pair => pair.Key.Field)
            .Select(pair => new ChangeOrigin(pair.Key.AppId, pair.Key.Field, pair.Value))
            .ToList();

    /// <summary>Die Proton-Wahl für ein Spiel. <see cref="ProtonChoice.Auto"/> heißt „kein Eintrag".</summary>
    /// <param name="moduleKey">Wer den Wunsch meldet — <see cref="Anvil.Core.Modules.IModule.Key"/>.</param>
    public void SetProton(string moduleKey, uint appId, ProtonChoice choice)
        => Stage(moduleKey, appId, wish => wish with { Proton = choice }, SteamField.Proton);

    /// <summary>Die Startoptionen, wörtlich. <c>""</c> leert einen vorhandenen Eintrag.</summary>
    /// <param name="moduleKey">Wer den Wunsch meldet — <see cref="Anvil.Core.Modules.IModule.Key"/>.</param>
    public void SetLaunchOptions(string moduleKey, uint appId, string options)
        => Stage(moduleKey, appId, wish => wish with { LaunchOptions = options }, SteamField.LaunchOptions);

    /// <summary>
    /// Merkt einen Je-Spiel-Schalter vor (S3.8 E-1) — ablegen oder, bei gleichem Modul und
    /// Spiel, ersetzen. Anders als <see cref="Stage"/> gibt es kein Ist, gegen das ausgesortiert
    /// wird: Ein Schalter ist immer entweder an oder aus, nie „kein Eintrag".
    /// </summary>
    public void SetModuleSwitch(ModuleGameSwitch wish)
    {
        var index = _switches.FindIndex(existing => existing.Key == wish.Key && existing.AppId == wish.AppId);
        if (index < 0)
        {
            _switches.Add(wish);
        }
        else
        {
            _switches[index] = wish;
        }

        _logger?.LogDebug(
            "switch staged: {Key} {State} for appid {AppId}",
            wish.Key, wish.Enabled ? "on" : "off", wish.AppId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Nimmt einen vorgemerkten Schalter zurück. Meldet nur, wenn tatsächlich einer fiel.</summary>
    public void DropModuleSwitch(string key, uint appId)
    {
        var removed = _switches.RemoveAll(wish => wish.Key == key && wish.AppId == appId);
        if (removed == 0)
        {
            return;
        }

        _logger?.LogDebug("switch dropped: {Key} for appid {AppId}", key, appId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Der vorgemerkte Zustand dieses Schalters, oder <c>null</c>, wenn keiner offen steht —
    /// dann gilt der gespeicherte Zustand der Registry unverändert.
    /// </summary>
    public bool? ModuleSwitch(string key, uint appId)
        => _switches.FirstOrDefault(wish => wish.Key == key && wish.AppId == appId)?.Enabled;

    /// <summary>
    /// Merkt eine Je-Spiel-Option vor (S5.4 §3.4) — ablegen oder, bei gleichem Modul, Spiel und
    /// Option, ersetzen. Wörtliches Vorbild <see cref="SetModuleSwitch"/>: auch hier kein Ist,
    /// gegen das ausgesortiert wird — eine Option ist immer entweder an oder aus.
    /// </summary>
    public void SetModuleOption(ModuleGameOptionWish wish)
    {
        var index = _options.FindIndex(
            existing => existing.Key == wish.Key && existing.AppId == wish.AppId && existing.Option == wish.Option);
        if (index < 0)
        {
            _options.Add(wish);
        }
        else
        {
            _options[index] = wish;
        }

        _logger?.LogDebug(
            "option staged: {Key} {Option} {State} for appid {AppId}",
            wish.Key, wish.Option, wish.Enabled ? "on" : "off", wish.AppId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Nimmt eine vorgemerkte Option zurück. Meldet nur, wenn tatsächlich eine fiel.</summary>
    public void DropModuleOption(string key, uint appId, ModuleGameOption option)
    {
        var removed = _options.RemoveAll(
            wish => wish.Key == key && wish.AppId == appId && wish.Option == option);
        if (removed == 0)
        {
            return;
        }

        _logger?.LogDebug("option dropped: {Key} {Option} for appid {AppId}", key, option, appId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Der vorgemerkte Zustand dieser Option, oder <c>null</c>, wenn keine offen steht — dann
    /// gilt der gespeicherte Zustand unverändert.
    /// </summary>
    public bool? ModuleOption(string key, uint appId, ModuleGameOption option)
        => _options
            .FirstOrDefault(wish => wish.Key == key && wish.AppId == appId && wish.Option == option)
            ?.Enabled;

    /// <summary>
    /// Verwirft alle Wünsche, Steam-Wünsche, Schalter und Optionen. Der Vermerk der Transaktion
    /// bleibt unberührt (E-N).
    /// </summary>
    public void Clear()
    {
        if (_wishes.Count == 0 && _switches.Count == 0 && _options.Count == 0)
        {
            return;
        }

        _logger?.LogInformation("changes discarded: {Count} game(s)", AppIds.Count);
        _wishes.Clear();
        _by.Clear();
        _switches.Clear();
        _options.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Schreibt die gesammelten Wünsche über die Transaktion und räumt danach auf.
    ///
    /// <para>
    /// <b>Die Aufräumregel liegt hier</b> (E-B), weil sie ohne Oberfläche prüfbar sein muss:
    /// geschrieben oder gleich dem Ist → die Sammlung ist erledigt; blockiert oder gescheitert →
    /// sie bleibt stehen, damit der Nutzer es erneut versuchen kann.
    /// </para>
    ///
    /// <para>
    /// <b>Schalter und Optionen reichen einen gemeinsamen Schritt</b> (S3.8 E-1, E-3; S5.4 §3.4):
    /// Er braucht kein geschlossenes Steam und läuft deshalb vor jedem Steam-Schritt der
    /// Transaktion. Ist er durch, stehen beide in der Datei — unabhängig davon, wie der Rest
    /// ausgeht (E-8).
    /// </para>
    ///
    /// <para>
    /// <b>Die Prefix-Schritte laufen vor <see cref="SettingsStep"/></b> (S3.9 §3.3, E-10): Scheitert
    /// einer, bricht die Transaktion ab, bevor der Schalter-Schritt überhaupt läuft — so bleiben
    /// Schalter- <b>und</b> Steam-Wünsche stehen, wie es der Fehlersatz "nothing was changed" sagt.
    /// </para>
    /// </summary>
    public async Task<TransactionResult> SaveAsync(Action<SaveProgress>? progress = null)
    {
        var wishes = _wishes.ToList();
        var origins = Origins;
        var switches = _switches.ToList();
        var options = _options.ToList();
        var settingsSaved = false;

        // E-10: Die Prefix-Schritte zuerst — der gezeichnete Fehlersatz "nothing was changed" gilt
        // nur, wenn nach ihrem Scheitern weder Schalter noch Optionen geschrieben wurden.
        var steps = new List<SaveStep>(_prefixSteps?.Invoke(AppIds) ?? []);
        if (switches.Count > 0 || options.Count > 0)
        {
            steps.Add(new SaveStep(SettingsStep, NeedsSteamClosed: false, _ =>
            {
                if (switches.Count > 0)
                {
                    _saveSwitches(switches);
                }

                if (options.Count > 0)
                {
                    _saveOptions?.Invoke(options);
                }

                settingsSaved = true;
                return Task.FromResult(SaveStepOutcome.Done);
            }));
        }

        _logger?.LogInformation("save requested: {Count} game(s)", AppIds.Count);

        var result = await _save(wishes, origins, progress, steps).ConfigureAwait(false);
        _logger?.LogInformation("save finished: {Outcome}", result.Outcome);

        var changed = false;
        if (settingsSaved)
        {
            // E-8: Der Schritt lief vor Steam — was er schrieb, steht in der Datei, gleich wie der Rest ausging.
            _switches.RemoveAll(switches.Contains);
            _options.RemoveAll(options.Contains);
            changed = true;
        }

        if (result.Wrote || result.Outcome == TransactionOutcome.NothingToDo)
        {
            _wishes.Clear();
            _by.Clear();
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    /// <summary>
    /// Mischt den neuen Wert in den Wunsch des Spiels und sortiert das Ergebnis gegen das Ist
    /// aus. Bleibt nichts übrig, fällt der Eintrag heraus — so verschwindet die Leiste von
    /// selbst, wenn der Nutzer zurückstellt.
    /// </summary>
    private void Stage(
        string moduleKey,
        uint appId,
        Func<SteamGameChange, SteamGameChange> apply,
        SteamField field)
    {
        var index = _wishes.FindIndex(wish => wish.AppId == appId);
        var merged = apply(index < 0 ? new SteamGameChange(appId, null, null) : _wishes[index]);
        var kept = SteamChangeFilter.Reduce([merged], _current());

        _by[(appId, field)] = moduleKey;

        if (kept.Count == 0)
        {
            Sync(appId, null);

            if (index < 0)
            {
                _logger?.LogDebug("change dropped: appid {AppId} — matches steam", appId);
                return;
            }

            _wishes.RemoveAt(index);
            _logger?.LogDebug("change dropped: appid {AppId} — matches steam", appId);
        }
        else
        {
            if (index < 0)
            {
                _wishes.Add(kept[0]);
            }
            else
            {
                _wishes[index] = kept[0];
            }

            Sync(appId, kept[0]);
            _logger?.LogDebug(
                "change staged: appid {AppId} ({Field}) by {Module}",
                appId, field.InSentence(), moduleKey);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Wirft die Absender weg, deren Feld nicht mehr gewünscht ist (S4.8 E-C). Das deckt beide
    /// Fälle mit derselben Regel: Ein Feld, das <see cref="SteamChangeFilter.Reduce"/> gestrichen
    /// hat, verliert seinen Absender — und ein ganz herausgefallener Wunsch alle.
    /// </summary>
    private void Sync(uint appId, SteamGameChange? wish)
    {
        if (wish?.Proton is null)
        {
            _by.Remove((appId, SteamField.Proton));
        }

        if (wish?.LaunchOptions is null)
        {
            _by.Remove((appId, SteamField.LaunchOptions));
        }
    }
}
