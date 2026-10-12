using System.Globalization;

namespace Anvil.Core.Sessions;

/// <summary>
/// Reads the output of <c>wine wmic process get Name,ParentProcessId,ProcessId</c> — the only
/// query that tells who started whom inside a game's container (spec s10.1, E-1).
///
/// <para>
/// <b>Fixed-width columns, found by their header.</b> wmic pads every column to a common width,
/// so an image name like <c>Blasphemous 2.exe</c> has spaces inside and cannot be cut at
/// whitespace. The header gives each column's start; a line is sliced at those starts. This also
/// makes an extra column (<c>WorkingSetSize</c>) and another column order harmless.
/// </para>
///
/// <para>
/// <b>Tolerant:</b> the text starts with a UTF-16 byte order mark and ends lines with
/// <c>\r\n</c>; both are ignored. Unreadable lines are skipped, unusable output is an empty
/// list — never an exception.
/// </para>
/// </summary>
public static class ProcessParentList
{
    private const string NameHeader = "Name";
    private const string ParentHeader = "ParentProcessID";
    private const string PidHeader = "ProcessID";

    /// <summary>Parses wmic's output; unusable output gives an empty list.</summary>
    /// <param name="output">The decoded standard output, or <c>null</c>.</param>
    /// <returns>Every readable process: image name, pid and parent pid.</returns>
    public static IReadOnlyList<(string Image, int Pid, int ParentPid)> Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var lines = output.TrimStart('﻿').Split('\n');
        var headerIndex = Array.FindIndex(lines, line => ReadColumns(line) is not null);
        if (headerIndex < 0)
        {
            return [];
        }

        var columns = ReadColumns(lines[headerIndex])!.Value;
        var found = new List<(string Image, int Pid, int ParentPid)>();
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (ReadProcess(line.TrimEnd('\r'), columns) is { } process)
            {
                found.Add(process);
            }
        }

        return found;
    }

    /// <summary>Where each of the three wanted columns starts and where the next column (or the
    /// line) takes over.</summary>
    private readonly record struct Columns(Slice Name, Slice Parent, Slice Pid);

    private readonly record struct Slice(int Start, int End);

    /// <summary>The header's columns, or <c>null</c> when the line is not the header.</summary>
    private static Columns? ReadColumns(string line)
    {
        var starts = new List<int>();
        var named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        foreach (var token in line.TrimEnd('\r').Split(' ', StringSplitOptions.None))
        {
            if (token.Length > 0)
            {
                starts.Add(position);
                named.TryAdd(token, position);
            }

            position += token.Length + 1;
        }

        if (!named.TryGetValue(NameHeader, out var name)
            || !named.TryGetValue(ParentHeader, out var parent)
            || !named.TryGetValue(PidHeader, out var pid))
        {
            return null;
        }

        return new Columns(
            SliceAt(name, starts), SliceAt(parent, starts), SliceAt(pid, starts));
    }

    /// <summary>A column runs up to the start of the next header word; the last one to the line's
    /// end.</summary>
    private static Slice SliceAt(int start, List<int> starts)
    {
        var next = starts.Where(other => other > start).DefaultIfEmpty(int.MaxValue).Min();
        return new Slice(start, next);
    }

    private static (string Image, int Pid, int ParentPid)? ReadProcess(string line, Columns columns)
    {
        var image = Cut(line, columns.Name);
        if (image.Length == 0
            || !int.TryParse(
                Cut(line, columns.Pid), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !int.TryParse(
                Cut(line, columns.Parent),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentPid))
        {
            return null;
        }

        return (image, pid, parentPid);
    }

    private static string Cut(string line, Slice slice)
    {
        if (slice.Start >= line.Length)
        {
            return string.Empty;
        }

        var end = Math.Min(slice.End, line.Length);
        return line[slice.Start..end].Trim();
    }
}
