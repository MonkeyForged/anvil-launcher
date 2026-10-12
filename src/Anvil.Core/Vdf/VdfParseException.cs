namespace Anvil.Core.Vdf;

/// <summary>
/// Parse-Fehler mit Position. VDF wird nie „best effort" gelesen: ein halb gelesenes Dokument
/// wäre eine falsche Auskunft über den Zustand der Maschine (Spec §6).
/// </summary>
public sealed class VdfParseException : Exception
{
    public VdfParseException(string reason, int line, int column)
        : base($"{reason} (line {line}, column {column})")
    {
        Line = line;
        Column = column;
    }

    /// <summary>Zeile ab 1; <c>0</c>, wenn der Fehler die ganze Datei betrifft.</summary>
    public int Line { get; }

    /// <summary>Spalte ab 1; <c>0</c>, wenn der Fehler die ganze Datei betrifft.</summary>
    public int Column { get; }
}
