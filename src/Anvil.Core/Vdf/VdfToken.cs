namespace Anvil.Core.Vdf;

/// <summary>Art eines Tokens im VDF-Textstrom.</summary>
public enum VdfTokenKind
{
    String,
    OpenBrace,
    CloseBrace,
    EndOfFile,
}

/// <summary>
/// Ein Token mit <see cref="RawText"/> für den verlustfreien Schreiber und <see cref="Value"/>
/// für Benutzer. <see cref="LeadingTrivia"/> trägt Leerraum, Kommentare und Conditionals, die
/// im Original unmittelbar davor stehen. Trivia und Rohtext aller Token aneinandergehängt
/// ergeben wieder die Eingabe — das ist die Zusage, auf der alles Weitere steht.
/// </summary>
public sealed record VdfToken(
    VdfTokenKind Kind,
    string LeadingTrivia,
    string RawText,
    string Value,
    int Line,
    int Column);
