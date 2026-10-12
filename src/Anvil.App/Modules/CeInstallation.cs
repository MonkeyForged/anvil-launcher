using System.IO.Compression;
using System.Security.Cryptography;
using Anvil.App.Bridges;

namespace Anvil.App.Modules;

public enum CeInstallationProblem { None, ProgramMissing, PenaltyFile, Unreadable, UnexpectedFiles }

/// <summary>The outcome of a completeness check. Detail: the penalty file found (relative path),
/// the exception message, or the files that deviate from the list; null otherwise.</summary>
public sealed record CeInstallationCheck(CeInstallationProblem Problem, string? Detail)
{
    public bool IsComplete => Problem == CeInstallationProblem.None;
}

/// <summary>
/// Decides whether a placed Cheat Engine installation (a folder or a zip) is complete: the
/// 64-bit program is there and none of the files that make the program refuse to work is. The
/// decision is pure on a list of relative paths, so a folder and a zip go through the same rule.
/// </summary>
public static class CeInstallation
{
    public const string ProgramName = "cheatengine-x86_64.exe";

    /// <summary>Files whose presence makes a release unusable; relative to the program's level.</summary>
    public static IReadOnlyList<string> PenaltyFiles { get; } =
        ["autorun/eatme.lua", "autorun/soundextension.lua", "autorun/dlls/dnd.dat"];

    /// <summary>
    /// Pure: finds the program's level via <see cref="ProgramLocation.LevelOf"/>, then requires
    /// that no <see cref="PenaltyFiles"/> lie below that level (case-insensitive). A penalty file
    /// elsewhere in the tree is not part of the installation and does not count.
    /// </summary>
    public static CeInstallationCheck Check(IReadOnlyCollection<string> relativePaths)
    {
        var level = ProgramLocation.LevelOf(relativePaths, ProgramName);
        if (level is null)
        {
            return new CeInstallationCheck(CeInstallationProblem.ProgramMissing, null);
        }

        foreach (var penaltyFile in PenaltyFiles)
        {
            var expected = level + penaltyFile;
            var found = relativePaths.FirstOrDefault(
                path => string.Equals(path, expected, StringComparison.OrdinalIgnoreCase));
            if (found is not null)
            {
                return new CeInstallationCheck(CeInstallationProblem.PenaltyFile, found);
            }
        }

        return new CeInstallationCheck(CeInstallationProblem.None, null);
    }

    /// <summary>Lists every file below <paramref name="folder"/> (relative, '/'), then checks.
    /// A folder that cannot be read is <see cref="CeInstallationProblem.Unreadable"/>.</summary>
    public static CeInstallationCheck CheckFolder(string folder)
    {
        try
        {
            return Check(ListRelativePaths(folder));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CeInstallationCheck(CeInstallationProblem.Unreadable, exception.Message);
        }
    }

    /// <summary>
    /// Like <see cref="CheckFolder"/> (ProgramMissing, PenaltyFile and Unreadable win), then
    /// compares every file below <paramref name="folder"/> with <paramref name="sha256ByPath"/>
    /// (paths relative to the folder itself, case-insensitive): a file not on the list, or one on
    /// the list with another SHA-256, is a deviation. Files whose name is in
    /// <paramref name="contentNotCompared"/> are compared by presence only. Files of the list
    /// that are missing are no problem. Detail: all deviations, sorted, joined with "; ".
    /// </summary>
    public static CeInstallationCheck CheckAgainstList(
        string folder,
        IReadOnlyDictionary<string, string> sha256ByPath,
        IReadOnlyCollection<string> contentNotCompared)
    {
        try
        {
            var relativePaths = ListRelativePaths(folder);
            var basic = Check(relativePaths);
            if (!basic.IsComplete)
            {
                return basic;
            }

            // The caller's dictionary may compare case-sensitively; the list is about Windows names.
            var listed = new Dictionary<string, string>(sha256ByPath, StringComparer.OrdinalIgnoreCase);
            var deviations = relativePaths
                .Select(path => DescribeDeviation(folder, path, listed, contentNotCompared))
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList();
            return deviations.Count == 0
                ? basic
                : new CeInstallationCheck(CeInstallationProblem.UnexpectedFiles, string.Join("; ", deviations));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new CeInstallationCheck(CeInstallationProblem.Unreadable, exception.Message);
        }
    }

    private static string? DescribeDeviation(
        string folder,
        string relativePath,
        Dictionary<string, string> listed,
        IReadOnlyCollection<string> contentNotCompared)
    {
        if (!listed.TryGetValue(relativePath, out var expectedSum))
        {
            return $"{relativePath} (not on the list)";
        }

        var isContentNotCompared = contentNotCompared.Contains(
            Path.GetFileName(relativePath), StringComparer.OrdinalIgnoreCase);
        if (isContentNotCompared)
        {
            return null;
        }

        using var stream = File.OpenRead(Path.Combine(folder, relativePath));
        var actualSum = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(actualSum, expectedSum, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{relativePath} (content differs)";
    }

    private static List<string> ListRelativePaths(string folder)
    {
        // AttributesToSkip = 0: the default skips hidden files, but the listing must be whole
        // to judge the tree. Inaccessible entries must throw so the folder reads as Unreadable
        // instead of passing with a part of its tree unseen.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        };
        return Directory.EnumerateFiles(folder, "*", options)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .ToList();
    }

    /// <summary>Reads the zip's entry names only (nothing is extracted), then checks. A file that
    /// is no readable zip is <see cref="CeInstallationProblem.Unreadable"/>.</summary>
    public static CeInstallationCheck CheckArchive(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
            return Check(entryNames);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new CeInstallationCheck(CeInstallationProblem.Unreadable, exception.Message);
        }
    }
}
