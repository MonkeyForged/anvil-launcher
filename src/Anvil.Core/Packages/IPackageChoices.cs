namespace Anvil.Core.Packages;

/// <summary>
/// What a selection row reads and writes: a list of names and one choice over it.
///
/// <para>
/// <b>Why an interface here</b> (s9.1 E-6): Wand's store lists single files, CE Bridge's store
/// lists folders and zips — two implementations behind one row, so the row's state logic exists
/// once. A second row for CE would be a second copy of that logic.
/// </para>
/// </summary>
public interface IPackageChoices
{
    /// <summary>The entries now present, in stable display order. Never throws; unreadable =
    /// empty.</summary>
    IReadOnlyList<string> Names();

    /// <summary>The saved choice if it is among <see cref="Names"/>, otherwise null. Reading never
    /// writes.</summary>
    string? Selected { get; }

    /// <summary>Saves <paramref name="name"/> as the choice; null or "" clears it. A name not in
    /// <see cref="Names"/> is ignored (logged, choice unchanged).</summary>
    void Select(string? name);
}
