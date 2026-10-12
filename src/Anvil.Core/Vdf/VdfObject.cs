using System.Text;

namespace Anvil.Core.Vdf;

/// <summary>
/// Innerer Knoten: geordnete Kinderliste mit case-insensitiver Suche wie in Valves KeyValues.
/// Die Wurzel eines Dokuments ist ein klammerloser Container mit leerem Schlüssel.
/// </summary>
public sealed class VdfObject : VdfNode
{
    private readonly List<VdfNode> _children = [];

    internal VdfObject(VdfToken keyToken, VdfToken? openBraceToken)
        : base(keyToken)
    {
        OpenBraceToken = openBraceToken;
    }

    internal VdfToken? OpenBraceToken { get; }

    internal VdfToken? CloseBraceToken { get; set; }

    public IReadOnlyList<VdfNode> Children => _children;

    /// <summary>Verschachtelungstiefe: -1 für die Wurzel, 0 für deren Kinder.</summary>
    internal int Depth => Parent is null ? -1 : Parent.Depth + 1;

    /// <summary>Erster Treffer, case-insensitiv. Doppelte Schlüssel bleiben alle erhalten.</summary>
    public VdfNode? Find(string key) =>
        _children.Find(child => string.Equals(child.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Folgt einem Pfad von Schlüsseln und liefert das Objekt am Ende — <c>null</c>, sobald ein
    /// Glied fehlt oder kein Objekt ist. Ohne Pfad das Objekt selbst.
    /// </summary>
    public VdfObject? FindObject(params string[] path)
    {
        var current = this;
        foreach (var key in path)
        {
            current = current.Find(key) as VdfObject;
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>Wie <see cref="FindObject"/>, aber das letzte Glied ist ein Wert.</summary>
    public VdfString? FindString(params string[] path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        return FindObject(path[..^1])?.Find(path[^1]) as VdfString;
    }

    internal void AddChild(VdfNode node)
    {
        node.Parent = this;
        _children.Add(node);
    }

    /// <summary>Setzt den Wert eines vorhandenen Blatts; alles daneben bleibt unberührt.</summary>
    public void SetString(string key, string value)
    {
        var node = Find(key) as VdfString
            ?? throw new KeyNotFoundException($"no string entry named '{key}'");
        node.SetValue(value);
    }

    /// <summary>Hängt ein Blatt an, eingerückt wie Steam es schreibt.</summary>
    public VdfString AddString(string key, string value)
    {
        var indent = VdfText.Indent(Depth + 1);
        var node = new VdfString(
            new VdfToken(VdfTokenKind.String, "\n" + indent, VdfText.Quote(key), key, 0, 0),
            new VdfToken(VdfTokenKind.String, "\t\t", VdfText.Quote(value), value, 0, 0));
        AddChild(node);
        return node;
    }

    /// <summary>Hängt ein leeres Objekt an, Klammern auf eigenen Zeilen.</summary>
    public VdfObject AddObject(string key)
    {
        var indent = VdfText.Indent(Depth + 1);
        var node = new VdfObject(
            new VdfToken(VdfTokenKind.String, "\n" + indent, VdfText.Quote(key), key, 0, 0),
            new VdfToken(VdfTokenKind.OpenBrace, "\n" + indent, "{", "{", 0, 0))
        {
            CloseBraceToken = new VdfToken(VdfTokenKind.CloseBrace, "\n" + indent, "}", "}", 0, 0),
        };
        AddChild(node);
        return node;
    }

    /// <summary>
    /// Entfernt den ersten Eintrag mit diesem Schlüssel samt seiner führenden Trivia — damit
    /// verschwindet auch seine Zeile.
    /// </summary>
    public bool Remove(string key)
    {
        var node = Find(key);
        if (node is null)
        {
            return false;
        }

        node.Parent = null;
        return _children.Remove(node);
    }

    internal override void WriteTo(StringBuilder builder)
    {
        if (OpenBraceToken is not null)
        {
            builder.Append(KeyToken.LeadingTrivia).Append(KeyToken.RawText);
            builder.Append(OpenBraceToken.LeadingTrivia).Append(OpenBraceToken.RawText);
        }

        foreach (var child in _children)
        {
            child.WriteTo(builder);
        }

        if (CloseBraceToken is not null)
        {
            builder.Append(CloseBraceToken.LeadingTrivia).Append(CloseBraceToken.RawText);
        }
    }
}
