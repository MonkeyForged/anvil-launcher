namespace Anvil.Core.Steam;

/// <summary>
/// Ein Objekt aus Steams binären KeyValues (Spec S4.3a §3.1): Schlüssel, Kind-Objekte in
/// Dateireihenfolge, Werte als Text. Kein <c>VdfObject</c>: das Textmodell trägt Token und
/// Trivia für den byte-genauen Roundtrip, hier gibt es nichts zurückzuschreiben (E-N).
/// </summary>
public sealed class AppInfoObject
{
    private readonly Dictionary<string, string> _values;

    internal AppInfoObject(
        string key, IReadOnlyList<AppInfoObject> objects, Dictionary<string, string> values)
    {
        Key = key;
        Objects = objects;
        _values = values;
    }

    /// <summary>Der Schlüssel; leer für den Wurzel-Container eines Eintrags.</summary>
    public string Key { get; }

    /// <summary>Die Kind-Objekte, in Dateireihenfolge.</summary>
    public IReadOnlyList<AppInfoObject> Objects { get; }

    /// <summary>
    /// Ein Wert als Text (int32/int64 invariant, float32 kürzest rund-tripfähig), oder
    /// <c>null</c>. Schlüssel case-insensitiv wie in Valves KeyValues.
    /// </summary>
    public string? Value(string key)
        => _values.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Folgt einem Pfad von Schlüsseln (case-insensitiv) und liefert das Objekt am Ende —
    /// <c>null</c>, sobald ein Glied fehlt. Ohne Pfad das Objekt selbst.
    /// </summary>
    public AppInfoObject? Find(params string[] path)
    {
        var current = this;
        foreach (var key in path)
        {
            current = current.Objects.FirstOrDefault(
                child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }
}
