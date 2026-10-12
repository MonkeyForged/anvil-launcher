using System.Globalization;
using System.Text;

namespace Anvil.Core.Sessions;

/// <summary>
/// Reads the output of <c>wine tasklist /fo csv /nh</c> — the memory of every process in a game's
/// container (spec s10.1, E-1). Next to <see cref="ProcessParentList"/> it tells which member of
/// the game's family is the real game.
///
/// <para>
/// <b>CSV, because names contain commas</b> (<c>Warhammer 40,000 Boltgun.exe</c>) and the memory
/// carries thousands separators (<c>"1,391,516 K"</c>): a line is cut at the commas <i>outside</i>
/// the quotes. The memory is every digit of the last field, in kilobytes.
/// </para>
///
/// <para>
/// This is the second reader of <c>tasklist</c>, next to <see cref="ContainerProcesses"/> (default
/// format, used by detaching). That one stays as it is: it carries six live acceptances.
/// </para>
/// </summary>
public static class ProcessMemoryList
{
    private const char Quote = '"';
    private const int FieldCount = 5;

    /// <summary>Parses tasklist's CSV output; unusable output gives an empty list.</summary>
    /// <param name="output">The decoded standard output, or <c>null</c>.</param>
    /// <returns>Every readable process: image name, pid and memory in KB.</returns>
    public static IReadOnlyList<(string Image, int Pid, long MemoryKilobytes)> Parse(
        string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var found = new List<(string Image, int Pid, long MemoryKilobytes)>();
        foreach (var line in output.Split('\n'))
        {
            if (ReadProcess(line.TrimEnd('\r')) is { } process)
            {
                found.Add(process);
            }
        }

        return found;
    }

    private static (string Image, int Pid, long MemoryKilobytes)? ReadProcess(string line)
    {
        var fields = SplitFields(line);
        if (fields.Count < FieldCount
            || fields[0].Length == 0
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return null;
        }

        var digits = new string([.. fields[4].Where(char.IsAsciiDigit)]);
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var memory)
            ? (fields[0], pid, memory)
            : null;
    }

    /// <summary>Splits at the commas outside of quotes and drops the quotes.</summary>
    private static List<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var isQuoted = false;
        foreach (var character in line)
        {
            if (character == Quote)
            {
                isQuoted = !isQuoted;
            }
            else if (character == ',' && !isQuoted)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        fields.Add(current.ToString());
        return fields;
    }
}
