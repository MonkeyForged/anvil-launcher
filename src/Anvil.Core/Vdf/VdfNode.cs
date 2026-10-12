using System.Text;

namespace Anvil.Core.Vdf;

/// <summary>Gemeinsame Basis: Schlüssel-Token und Elternobjekt.</summary>
public abstract class VdfNode
{
    private protected VdfNode(VdfToken keyToken)
    {
        KeyToken = keyToken;
    }

    internal VdfToken KeyToken { get; }

    /// <summary>Der dekodierte Schlüssel (Escapes aufgelöst).</summary>
    public string Key => KeyToken.Value;

    public VdfObject? Parent { get; internal set; }

    internal abstract void WriteTo(StringBuilder builder);
}
