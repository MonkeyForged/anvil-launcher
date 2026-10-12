using System.IO.Compression;

namespace Anvil.App.Bridges;

/// <summary>What <see cref="ApplicationCopy.Ensure"/> found or did.</summary>
public enum ApplicationCopyOutcome
{
    /// <summary>The target exists with the program; nothing copied.</summary>
    AlreadyThere,

    /// <summary>Copied into the target now.</summary>
    Copied,

    /// <summary>The source holds the program at no level.</summary>
    ProgramMissing,

    /// <summary>The target exists without the program; left untouched.</summary>
    IncompleteTarget,

    /// <summary>An IOException, UnauthorizedAccessException or InvalidDataException.</summary>
    Unreadable,
}

/// <summary>
/// The outcome of <see cref="ApplicationCopy.Ensure"/>. <see cref="ExecutablePath"/> is always
/// the program's path inside the target; <see cref="Error"/> is set only for
/// <see cref="ApplicationCopyOutcome.Unreadable"/>.
/// </summary>
public sealed record ApplicationCopyResult(
    ApplicationCopyOutcome Outcome, string ExecutablePath, Exception? Error)
{
    /// <summary>Whether the program now lies in the target.</summary>
    public bool HasProgram => Outcome is ApplicationCopyOutcome.AlreadyThere
        or ApplicationCopyOutcome.Copied;
}

/// <summary>
/// Brings an installation into its program folder under <c>app/</c>. Shared by the bridges:
/// Wand unpacks its vendor zip, CE takes a zip or a folder the user placed.
///
/// <para>
/// <b>Never deletes the target or the source.</b> A target folder without the program is
/// reported, not replaced: it may be the user's, or a running game may start from it.
/// </para>
/// </summary>
public static class ApplicationCopy
{
    /// <summary>
    /// The folder copied into before renaming. It is the done marker: only the rename makes a
    /// target valid, so an interrupted copy never passes for a finished one.
    /// </summary>
    public const string PartialSuffix = ".partial";

    /// <summary>
    /// Brings the installation at <paramref name="source"/> (a folder, or else a zip file) into
    /// <paramref name="target"/>, taking only the level where <paramref name="programName"/>
    /// lies (<see cref="ProgramLocation.LevelOf"/>; the shallowest wins). An existing target is
    /// judged without opening the source. Entries leading outside the target are skipped.
    /// Throws only <see cref="OperationCanceledException"/>; the .partial folder then stays and
    /// is cleared on the next call.
    /// </summary>
    public static ApplicationCopyResult Ensure(
        string source, string target, string programName, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(target, programName);

        if (Directory.Exists(target))
        {
            var outcome = File.Exists(executable)
                ? ApplicationCopyOutcome.AlreadyThere
                : ApplicationCopyOutcome.IncompleteTarget;
            return new ApplicationCopyResult(outcome, executable, null);
        }

        var partial = target + PartialSuffix;
        try
        {
            if (Directory.Exists(partial))
            {
                // The remainder of an aborted run. It belongs to Anvil, not to the user —
                // deleting is allowed here.
                Directory.Delete(partial, recursive: true);
            }

            var hasProgram = Directory.Exists(source)
                ? CopyFolder(source, partial, programName, cancellationToken)
                : ExtractArchive(source, partial, programName, cancellationToken);
            if (!hasProgram)
            {
                return new ApplicationCopyResult(ApplicationCopyOutcome.ProgramMissing, executable, null);
            }

            Directory.Move(partial, target);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ApplicationCopyResult(ApplicationCopyOutcome.Unreadable, executable, exception);
        }

        return new ApplicationCopyResult(ApplicationCopyOutcome.Copied, executable, null);
    }

    /// <summary>
    /// Copies the program's level of <paramref name="folder"/> into <paramref name="partial"/>.
    /// Returns false, creating nothing, when the folder holds no program.
    /// </summary>
    private static bool CopyFolder(
        string folder, string partial, string programName, CancellationToken cancellationToken)
    {
        // AttributesToSkip = 0: the default skips hidden files, and a copy without them would be
        // incomplete. Inaccessible entries must throw so the folder reads as Unreadable instead
        // of being copied with a part of its tree missing.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        };
        var relativePaths = Directory.EnumerateFiles(folder, "*", options)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .ToList();

        var level = ProgramLocation.LevelOf(relativePaths, programName);
        if (level is null)
        {
            return false;
        }

        var root = Path.GetFullPath(partial);
        Directory.CreateDirectory(root);
        foreach (var relative in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = DestinationOf(root, relative, level);
            if (destination is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(folder, relative), destination);
        }

        return true;
    }

    /// <summary>
    /// Unpacks the program's level of the zip at <paramref name="archivePath"/> into
    /// <paramref name="partial"/>, entry by entry. Returns false, creating nothing, when the
    /// archive holds no program.
    ///
    /// <para>
    /// <b>Not via <c>ZipFile.ExtractToDirectory</c></b> (Wand plan P-1): only this way is
    /// unpacking cancellable, and only this way paperwork outside the level (a .nupkg's) stays
    /// behind without anyone having to list it.
    /// </para>
    /// </summary>
    private static bool ExtractArchive(
        string archivePath, string partial, string programName, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var level = ProgramLocation.LevelOf(
            archive.Entries.Select(entry => entry.FullName), programName);
        if (level is null)
        {
            return false;
        }

        var root = Path.GetFullPath(partial);
        Directory.CreateDirectory(root);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = DestinationOf(root, entry.FullName, level);
            if (destination is null)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }

        return true;
    }

    /// <summary>
    /// Where the file at <paramref name="relativePath"/> ('/'-separated) lands below
    /// <paramref name="root"/>, with <paramref name="level"/> cut off. Null when it lies outside
    /// the level, names no file, or leads outside the root.
    /// </summary>
    private static string? DestinationOf(string root, string relativePath, string level)
    {
        if (!relativePath.StartsWith(level, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = relativePath[level.Length..];

        // A directory entry carries no name; the folder comes into being with its first file.
        // An empty remainder is the level's own entry.
        if (relative.Length == 0 || relative.EndsWith('/'))
        {
            return null;
        }

        var destination = Path.GetFullPath(Path.Combine(root, relative));

        // An entry leading out of the target (Wand plan P-3). A vendor archive has none; one
        // from a foreign hand could.
        return destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? destination
            : null;
    }
}
