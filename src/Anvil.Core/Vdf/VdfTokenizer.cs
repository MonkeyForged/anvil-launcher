using System.Text;

namespace Anvil.Core.Vdf;

/// <summary>
/// Zerlegt VDF-Text in Token. Verlustfrei: die Konkatenation aller
/// <c>LeadingTrivia + RawText</c> in Token-Reihenfolge ergibt exakt die Eingabe.
/// </summary>
public sealed class VdfTokenizer
{
    private readonly string _text;
    private int _position;
    private int _line = 1;
    private int _column = 1;

    public VdfTokenizer(string text)
    {
        _text = text;
        if (_text.Length > 0 && _text[0] == '﻿')
        {
            throw new VdfParseException("byte order mark is not supported", 1, 1);
        }
    }

    /// <summary>Liefert das nächste Token; am Ende beliebig oft <see cref="VdfTokenKind.EndOfFile"/>.</summary>
    public VdfToken Next()
    {
        var trivia = ReadTrivia();
        var line = _line;
        var column = _column;

        if (_position >= _text.Length)
        {
            return new VdfToken(VdfTokenKind.EndOfFile, trivia, string.Empty, string.Empty, line, column);
        }

        switch (_text[_position])
        {
            case '{':
                Advance();
                return new VdfToken(VdfTokenKind.OpenBrace, trivia, "{", "{", line, column);
            case '}':
                Advance();
                return new VdfToken(VdfTokenKind.CloseBrace, trivia, "}", "}", line, column);
            case '"':
                return ReadQuotedString(trivia, line, column);
            case '#':
                throw new VdfParseException(
                    "directives like #base/#include are not supported", line, column);
            default:
                return ReadUnquotedString(trivia, line, column);
        }
    }

    /// <summary>Leerraum, Kommentare und Conditionals — wörtlich erhalten, nicht ausgewertet.</summary>
    private string ReadTrivia()
    {
        var start = _position;
        while (_position < _text.Length)
        {
            var current = _text[_position];
            if (current is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
            }
            else if (current == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
            {
                while (_position < _text.Length && _text[_position] != '\n')
                {
                    Advance();
                }
            }
            else if (current == '[')
            {
                var line = _line;
                var column = _column;
                while (_position < _text.Length && _text[_position] != ']')
                {
                    Advance();
                }

                if (_position >= _text.Length)
                {
                    throw new VdfParseException("unterminated conditional", line, column);
                }

                Advance();
            }
            else
            {
                break;
            }
        }

        return _text[start.._position];
    }

    /// <summary>Endet am nächsten unmaskierten Anführungszeichen — nie an einer Zeile.</summary>
    private VdfToken ReadQuotedString(string trivia, int line, int column)
    {
        var start = _position;
        Advance();
        var value = new StringBuilder();
        while (true)
        {
            if (_position >= _text.Length)
            {
                throw new VdfParseException("unterminated quoted string", line, column);
            }

            var current = _text[_position];
            if (current == '"')
            {
                Advance();
                break;
            }

            if (current == '\\' && _position + 1 < _text.Length)
            {
                var next = _text[_position + 1];
                switch (next)
                {
                    case '"':
                        value.Append('"');
                        break;
                    case '\\':
                        value.Append('\\');
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    default:
                        // Unbekannte Sequenz bleibt wörtlich — so verhält sich Valve.
                        value.Append(current).Append(next);
                        break;
                }

                Advance();
                Advance();
            }
            else
            {
                value.Append(current);
                Advance();
            }
        }

        return new VdfToken(
            VdfTokenKind.String, trivia, _text[start.._position], value.ToString(), line, column);
    }

    private VdfToken ReadUnquotedString(string trivia, int line, int column)
    {
        var start = _position;
        while (_position < _text.Length)
        {
            if (_text[_position] is ' ' or '\t' or '\r' or '\n' or '"' or '{' or '}')
            {
                break;
            }

            Advance();
        }

        var raw = _text[start.._position];
        return new VdfToken(VdfTokenKind.String, trivia, raw, raw, line, column);
    }

    private void Advance()
    {
        if (_text[_position] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        _position++;
    }
}
