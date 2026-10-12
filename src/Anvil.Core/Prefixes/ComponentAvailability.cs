using Anvil.Core.Packages;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Ob eine Fassung im Prefix bereitstellbar ist (S3.5a §3.5): keine Zutat, ein Teil, keine oder
/// alle.
/// </summary>
public enum ComponentAvailabilityState
{
    /// <summary>Alle Bestandteile liegen vor.</summary>
    Ready,

    /// <summary>Einige Bestandteile liegen vor, andere fehlen — der Fall, den der Nutzer heute nicht versteht.</summary>
    Incomplete,

    /// <summary>Kein Bestandteil liegt vor.</summary>
    Missing,

    /// <summary>
    /// Die Fassung hat <b>kein Rezept</b> (<see cref="PrefixComponent.Recipe"/> ist <c>null</c>)
    /// — seit S3.5 ein legaler Zustand: Sie kann nur erkannt, nicht hergestellt werden. Es gibt
    /// nichts bereitzustellen, also auch nichts, das fehlen könnte.
    /// </summary>
    NotBuildable,
}

/// <summary>
/// Das ist die Datenzeile, die Design-Anforderung 39 beschreibt — Fassung, Zustand, Bestandteile
/// mit vorhanden/fehlend (S3.5a §3.5). S3.5b zeichnet sie, ohne die Mechanik anzufassen.
///
/// <para>
/// <b>Die Fabrik <see cref="For"/> benutzt <see cref="IngredientInventory.Locate"/></b> — die
/// Erkennung selbst ist dort schon geprüft; dieses Ergebnis rollt sie nur zu einem Zustand auf.
/// Ohne Rezept (<c>Component.Recipe is null</c>) bleiben die Bestandteile leer und der Zustand
/// ist <see cref="ComponentAvailabilityState.NotBuildable"/> — es gibt nichts zu erkennen.
/// </para>
/// </summary>
/// <param name="Component">Die Fassung, um die es geht.</param>
/// <param name="Ingredients">
/// Die Bestandteile mit ihrem Zustand, in Rezeptreihenfolge — leer, wenn die Fassung kein Rezept
/// hat.
/// </param>
public sealed record ComponentAvailability(PrefixComponent Component, IReadOnlyList<IngredientState> Ingredients)
{
    /// <summary>
    /// Baut die Verfügbarkeit einer Fassung gegen den Bestand einer Ablage.
    /// </summary>
    /// <param name="component">Die Fassung, deren Zutaten gesucht werden.</param>
    /// <param name="files">Der Bestand der Ablage (<see cref="PackageStore.List"/>).</param>
    /// <param name="sourceLookup">Das zweite Manifest — siehe <see cref="IngredientInventory.Locate"/>.</param>
    /// <param name="sha256">Die Prüfsumme, siehe <see cref="IngredientInventory.Locate"/>; Vorgabe die echte SHA-256.</param>
    public static ComponentAvailability For(
        PrefixComponent component,
        IReadOnlyList<StoredFile> files,
        Func<string, CatalogEntry?> sourceLookup,
        Func<string, string>? sha256 = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(sourceLookup);

        if (component.Recipe is null)
        {
            // Kein Rezept, keine Zutaten, nichts zu erkennen (Spec §3.5).
            return new ComponentAvailability(component, []);
        }

        var ingredients = IngredientInventory.Locate(
            files, component.Recipe.Ingredients, sourceLookup, sha256);
        return new ComponentAvailability(component, ingredients);
    }

    /// <summary>
    /// Rollt die Bestandteile zu einem Zustand auf. Berechnet bei jedem Zugriff neu — billige
    /// Rechnerei über eine bereits vorliegende Liste, kein Grund für einen Zwischenspeicher
    /// (dieselbe Haltung wie <see cref="IngredientInventory"/>, E-3).
    /// </summary>
    public ComponentAvailabilityState State
    {
        get
        {
            if (Component.Recipe is null)
            {
                return ComponentAvailabilityState.NotBuildable;
            }

            var presentCount = Ingredients.Count(ingredient => ingredient.Present);
            if (presentCount == Ingredients.Count)
            {
                return ComponentAvailabilityState.Ready;
            }

            return presentCount == 0 ? ComponentAvailabilityState.Missing : ComponentAvailabilityState.Incomplete;
        }
    }

    /// <summary>Die fehlenden Bestandteile, in Rezeptreihenfolge.</summary>
    public IReadOnlyList<IngredientState> Missing
        => Ingredients.Where(ingredient => !ingredient.Present).ToList();
}
