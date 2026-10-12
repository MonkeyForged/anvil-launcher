namespace Anvil.Core.Prefixes;

/// <summary>
/// Eine Zutat, die ein Rezept (<see cref="PrefixRecipe"/>) aus der Ablage braucht (S3.5, Spec
/// §3.3).
///
/// <para>
/// <b><see cref="Slot"/></b> ist der interne Name, mit dem eine <see cref="RecipeStage"/> auf
/// diese Zutat verweist (<c>dotnet40</c>, <c>dotnet48</c>) — kein Pfad, kein Dateiname.
/// <b><see cref="FileName"/></b> ist der Name, unter dem die Datei in der Ablage liegt; er
/// entsteht nicht neu, sondern kommt aus <c>Anvil.App.Modules.PackageCatalog.Dependencies</c>,
/// damit ein Dateiname genau einmal im Repo steht.
/// </para>
///
/// <para>
/// <b><see cref="DisplayName"/></b> ist der Satzteil für eine Meldung an den Nutzer
/// (<c>the .NET 4.0 installer</c>) — er steht unverändert in <c>IngredientMissing</c> und geht
/// damit ungeprüft vor den Nutzer. Deshalb englisch, und deshalb ein Satzteil, kein Schlüssel.
/// </para>
/// </summary>
/// <param name="Slot">Der interne Name, mit dem eine Stufe diese Zutat anfordert.</param>
/// <param name="FileName">Der Name der Datei in der Ablage.</param>
/// <param name="DisplayName">Der Satzteil für eine Meldung an den Nutzer, englisch.</param>
public sealed record RecipeIngredient(string Slot, string FileName, string DisplayName)
{
    // C# erlaubt Records keinen eigenen Konstruktorkörper — die Prüfung sitzt deshalb in
    // Initialisierern, die die generierten Eigenschaften überschreiben, aber weiterhin auf
    // den Konstruktorparametern der positionellen Deklaration aufbauen (Vorbild: PrefixComponent).

    /// <summary>Der interne Name, mit dem eine Stufe diese Zutat anfordert.</summary>
    public string Slot { get; init; } = !string.IsNullOrWhiteSpace(Slot)
        ? Slot
        : throw new ArgumentException("Recipe ingredient slot must not be blank.", nameof(Slot));

    /// <summary>Der Name der Datei in der Ablage.</summary>
    public string FileName { get; init; } = !string.IsNullOrWhiteSpace(FileName)
        ? FileName
        : throw new ArgumentException("Recipe ingredient file name must not be blank.", nameof(FileName));

    /// <summary>Der Satzteil für eine Meldung an den Nutzer, englisch.</summary>
    public string DisplayName { get; init; } = !string.IsNullOrWhiteSpace(DisplayName)
        ? DisplayName
        : throw new ArgumentException("Recipe ingredient display name must not be blank.", nameof(DisplayName));
}

/// <summary>
/// Eine Stufe eines Rezepts (S3.5, Spec §3.3) — ein einzelner Aufruf von Protons <c>wine</c> in
/// einem Wegwerf-Prefix.
///
/// <para>
/// <b><see cref="Label"/></b> ist englisch und kleingeschrieben (<c>installing .NET 4.8</c>): Er
/// steht unverändert in der Fortschrittsmeldung (<c>stage 5 of 8: installing .NET 4.8</c>) und in
/// der Fehlermeldung, wenn diese Stufe scheitert.
/// </para>
///
/// <para>
/// <b>Der Aufruf ist immer Protons <c>wine</c></b> (E-2) — eine Stufe nennt kein Programm,
/// sondern nur <see cref="Arguments"/>. Der Grund ist Sicherung, nicht Bequemlichkeit: Ein Modul
/// liefert Daten, nie einen Befehl an die Maschine. Wollte ein Rezept ein fremdes Programm
/// starten, gäbe es dafür in dieser Deklaration gar keinen Platz.
/// </para>
///
/// <para>
/// <b><see cref="IngredientSlot"/> setzt den Pfad der Zutat als erstes Argument vor
/// <see cref="Arguments"/></b> — deshalb steht in der Deklaration selbst kein Pfad und kein
/// Platzhalter dafür. <c>null</c> heißt: Diese Stufe braucht keine Zutat (<c>wineboot</c>,
/// <c>winecfg</c>, <c>reg add</c>).
/// </para>
///
/// <para>
/// <b><see cref="DllOverrides"/></b> ist der Wert für <c>WINEDLLOVERRIDES</c> nur dieser Stufe,
/// oder <c>null</c> für keinen. <c>mscoree=d</c> in der ersten Stufe verhindert, dass Wine Mono
/// entsteht — sonst hält der 4.0-Installer sich für überflüssig und endet nach neun Sekunden mit
/// Rückgabewert 0, ohne etwas installiert zu haben (Vision §6.2).
/// </para>
///
/// <para>
/// <b><see cref="Timeout"/></b> ist die Reißleine für einen <i>hängenden</i> Prozess, keine
/// Erwartung: Die beiden Installer-Stufen sind gemessen mit 62 s und 73 s durchgelaufen: die
/// Zehn-Minuten-Grenze fängt nur den Fall ab, in dem ein Installer auf einen Dialog wartet, den
/// niemand beantwortet.
/// </para>
/// </summary>
/// <param name="Label">Englisch, kleingeschrieben — geht unverändert vor den Nutzer.</param>
/// <param name="Arguments">Die Argumente für <c>wine</c>, ohne den Zutat-Pfad.</param>
/// <param name="IngredientSlot">
/// Der Slot der Zutat, deren Pfad als erstes Argument vorangestellt wird, oder <c>null</c>.
/// </param>
/// <param name="DllOverrides">Der Wert für <c>WINEDLLOVERRIDES</c> nur dieser Stufe, oder <c>null</c>.</param>
/// <param name="Timeout">Die Reißleine für einen hängenden Prozess.</param>
public sealed record RecipeStage(
    string Label,
    IReadOnlyList<string> Arguments,
    string? IngredientSlot,
    string? DllOverrides,
    TimeSpan Timeout)
{
    /// <summary>Englisch, kleingeschrieben — geht unverändert vor den Nutzer.</summary>
    public string Label { get; init; } = !string.IsNullOrWhiteSpace(Label)
        ? Label
        : throw new ArgumentException("Recipe stage label must not be blank.", nameof(Label));

    /// <summary>Die Argumente für <c>wine</c>, ohne den Zutat-Pfad.</summary>
    public IReadOnlyList<string> Arguments { get; init; } =
        Arguments ?? throw new ArgumentNullException(nameof(Arguments));

    /// <summary>Die Reißleine für einen hängenden Prozess, keine Erwartung.</summary>
    public TimeSpan Timeout { get; init; } = Timeout > TimeSpan.Zero
        ? Timeout
        : throw new ArgumentException("Recipe stage timeout must be greater than zero.", nameof(Timeout));
}

/// <summary>
/// Das Rezept, mit dem der Core eine <see cref="PrefixComponent"/> aus einem Wegwerf-Prefix
/// bauen kann (S3.5, Spec §3.3).
///
/// <para>
/// <b>Das Modul erklärt, der Core führt aus</b> (Vision §9, Entscheidung 3). Was hier fehlt, ist
/// deshalb Absicht: kein Pfad, keine Umgebung, keine Ablage, kein Wegwerf-Prefix, kein
/// <c>wineserver</c>. All das ist Sache des Ausführenden (S3.5, der Bauer); das Rezept
/// beschreibt nur, was zu tun ist.
/// </para>
///
/// <para>
/// <b>Zwei Prüfungen, die eine kaputte Deklaration sofort abweisen</b>, statt sie bis in einen
/// fremden Prefix durchzureichen: Die <see cref="RecipeIngredient.Slot"/>s müssen eindeutig sein,
/// und jeder <see cref="RecipeStage.IngredientSlot"/> muss einen von ihnen treffen. Ein Rezept,
/// das eine Zutat nennt, die es nicht deklariert, ist ein Programmierfehler und fällt beim Bauen
/// der Klasse auf, nicht drei Minuten später im Wegwerf-Prefix.
/// </para>
/// </summary>
/// <param name="Ingredients">Die Zutaten, die die Stufen aus der Ablage brauchen.</param>
/// <param name="Stages">Die Stufen, in der Reihenfolge, in der sie laufen. Mindestens eine.</param>
public sealed record PrefixRecipe(
    IReadOnlyList<RecipeIngredient> Ingredients,
    IReadOnlyList<RecipeStage> Stages)
{
    /// <summary>Die Zutaten, die die Stufen aus der Ablage brauchen.</summary>
    public IReadOnlyList<RecipeIngredient> Ingredients { get; init; } = ValidateIngredients(Ingredients);

    /// <summary>Die Stufen, in der Reihenfolge, in der sie laufen. Mindestens eine.</summary>
    public IReadOnlyList<RecipeStage> Stages { get; init; } = ValidateStages(Stages, Ingredients);

    /// <summary>
    /// Muster, die beim Ernten zur Nutzlast dazukommen (ein Stern im letzten Segment, S3.5 Plan P-6) —
    /// <b>sie gehören zum Rezept</b> (Spec S3.9 E-3): Sie sagen, was beim Ernten zusätzlich mitgeht,
    /// und ein Core-Schritt, der beliebige Komponenten vorbereitet, findet sie so am Gegenstand statt
    /// in einer Klasse, die er nicht kennen darf. Leer ist der Normalfall.
    /// </summary>
    public IReadOnlyList<string> HarvestPatterns { get; init; } = [];

    // Zwei Zutaten mit demselben Slot wären für eine Stufe nicht mehr unterscheidbar — welche der
    // beiden wäre gemeint?
    private static IReadOnlyList<RecipeIngredient> ValidateIngredients(
        IReadOnlyList<RecipeIngredient> ingredients)
    {
        ArgumentNullException.ThrowIfNull(ingredients);

        var seenSlots = new HashSet<string>();
        foreach (var ingredient in ingredients)
        {
            ArgumentNullException.ThrowIfNull(ingredient, nameof(ingredients));

            if (!seenSlots.Add(ingredient.Slot))
            {
                throw new ArgumentException(
                    $"Duplicate recipe ingredient slot '{ingredient.Slot}'.", nameof(ingredients));
            }
        }

        return ingredients;
    }

    // Läuft nach ValidateIngredients (Deklarationsreihenfolge der Init-Eigenschaften), deshalb
    // dürfen die Slots hier als bekannt gelten.
    private static IReadOnlyList<RecipeStage> ValidateStages(
        IReadOnlyList<RecipeStage> stages, IReadOnlyList<RecipeIngredient> ingredients)
    {
        ArgumentNullException.ThrowIfNull(stages);

        if (stages.Count == 0)
        {
            throw new ArgumentException("A recipe must have at least one stage.", nameof(stages));
        }

        var knownSlots = ingredients.Select(ingredient => ingredient.Slot).ToHashSet();
        foreach (var stage in stages)
        {
            ArgumentNullException.ThrowIfNull(stage, nameof(stages));

            if (stage.IngredientSlot is not null && !knownSlots.Contains(stage.IngredientSlot))
            {
                throw new ArgumentException(
                    $"Stage '{stage.Label}' references unknown ingredient slot '{stage.IngredientSlot}'.",
                    nameof(stages));
            }
        }

        return stages;
    }
}
