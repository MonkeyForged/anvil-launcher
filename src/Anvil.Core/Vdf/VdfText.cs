using System.Text;

namespace Anvil.Core.Vdf;

/// <summary>
/// Formatierung neu gesetzter Einträge im Steam-Stil. Betrifft ausschließlich geänderte und
/// neue Knoten — alles Unangetastete behält seinen Rohtext (Spec §3).
/// </summary>
internal static class VdfText
{
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    internal static string Indent(int depth) => new('\t', depth);
}
