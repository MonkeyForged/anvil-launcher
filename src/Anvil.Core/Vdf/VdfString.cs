using System.Text;

namespace Anvil.Core.Vdf;

/// <summary>Blattknoten: ein Stringwert, roh und dekodiert.</summary>
public sealed class VdfString : VdfNode
{
    internal VdfString(VdfToken keyToken, VdfToken valueToken)
        : base(keyToken)
    {
        ValueToken = valueToken;
    }

    internal VdfToken ValueToken { get; private set; }

    /// <summary>Der dekodierte Wert (Escapes aufgelöst).</summary>
    public string Value => ValueToken.Value;

    /// <summary>Setzt den Wert neu; nur dieser Knoten verliert damit seinen Original-Rohtext.</summary>
    internal void SetValue(string value)
    {
        ValueToken = ValueToken with { RawText = VdfText.Quote(value), Value = value };
    }

    internal override void WriteTo(StringBuilder builder)
    {
        builder.Append(KeyToken.LeadingTrivia).Append(KeyToken.RawText);
        builder.Append(ValueToken.LeadingTrivia).Append(ValueToken.RawText);
    }
}
