using System.Text;

namespace Anvil.Core.Vdf;

/// <summary>
/// Ein geparstes VDF-Dokument. Unverändert serialisiert ist es byte-identisch zur Eingabe —
/// jeder unangetastete Knoten gibt seinen Rohtext zurück (Spec §3).
/// </summary>
public sealed class VdfDocument
{
    /// <summary>Streng: ungültige Bytes werfen, statt still durch U+FFFD ersetzt zu werden.</summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _trailingTrivia;

    private VdfDocument(VdfObject root, string trailingTrivia)
    {
        Root = root;
        _trailingTrivia = trailingTrivia;
    }

    /// <summary>Der klammerlose Wurzel-Container; sein Schlüssel ist leer.</summary>
    public VdfObject Root { get; }

    public static VdfDocument Parse(string text)
    {
        var tokenizer = new VdfTokenizer(text);
        var root = new VdfObject(
            new VdfToken(VdfTokenKind.String, string.Empty, string.Empty, string.Empty, 0, 0),
            openBraceToken: null);
        return new VdfDocument(root, ParseChildren(tokenizer, root, isRoot: true));
    }

    public static VdfDocument Load(string path)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            // Position unbekannt: der Fehler betrifft die Datei, nicht eine Stelle darin.
            throw new VdfParseException("file is not valid UTF-8", 0, 0);
        }

        return Parse(text);
    }

    /// <summary>Schreibt das Dokument unteilbar — ein Abbruch lässt die Zieldatei unversehrt.</summary>
    public void Save(string path) => AtomicFile.WriteAllText(path, ToText());

    public string ToText()
    {
        var builder = new StringBuilder();
        Root.WriteTo(builder);
        builder.Append(_trailingTrivia);
        return builder.ToString();
    }

    /// <summary>Liest Kinder bis zur schließenden Klammer; liefert für die Wurzel die Schluss-Trivia.</summary>
    private static string ParseChildren(VdfTokenizer tokenizer, VdfObject container, bool isRoot)
    {
        while (true)
        {
            var token = tokenizer.Next();
            switch (token.Kind)
            {
                case VdfTokenKind.EndOfFile when isRoot:
                    return token.LeadingTrivia;
                case VdfTokenKind.EndOfFile:
                    throw new VdfParseException(
                        "unexpected end of file inside object", token.Line, token.Column);
                case VdfTokenKind.CloseBrace when !isRoot:
                    container.CloseBraceToken = token;
                    return string.Empty;
                case VdfTokenKind.CloseBrace:
                    throw new VdfParseException(
                        "unexpected '}' without matching '{'", token.Line, token.Column);
                case VdfTokenKind.OpenBrace:
                    throw new VdfParseException(
                        "unexpected '{' without a key", token.Line, token.Column);
            }

            // Das Token ist ein Schlüssel; danach folgt ein Stringwert oder ein Objekt.
            var next = tokenizer.Next();
            switch (next.Kind)
            {
                case VdfTokenKind.String:
                    container.AddChild(new VdfString(token, next));
                    break;
                case VdfTokenKind.OpenBrace:
                    var child = new VdfObject(token, next);
                    container.AddChild(child);
                    ParseChildren(tokenizer, child, isRoot: false);
                    break;
                default:
                    throw new VdfParseException(
                        "expected a value or '{' after key", next.Line, next.Column);
            }
        }
    }
}
