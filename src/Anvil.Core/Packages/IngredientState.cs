using Anvil.Core.Prefixes;

namespace Anvil.Core.Packages;

/// <summary>
/// Ob eine Zutat (<see cref="RecipeIngredient"/>) in der Ablage vorliegt — und wenn ja, wo
/// (S3.5a §3.4).
/// </summary>
/// <param name="Ingredient">Die Zutat, um die es geht.</param>
/// <param name="Path">
/// Der volle Pfad der erkannten Datei, oder <c>null</c>, wenn die Zutat fehlt — genau das, was
/// <c>BuildRequest.IngredientPaths</c> (S3.5) braucht.
/// </param>
public sealed record IngredientState(RecipeIngredient Ingredient, string? Path)
{
    /// <summary>Ob die Zutat in der Ablage liegt.</summary>
    public bool Present => Path is not null;
}
