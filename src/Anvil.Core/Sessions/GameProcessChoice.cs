namespace Anvil.Core.Sessions;

/// <summary>
/// The rule of spec s10.1 E-1: which process in the container is the game. Pure — the lists come
/// from <see cref="ProcessParentList"/> and <see cref="ProcessMemoryList"/>.
///
/// <para>
/// <b>Family, then size.</b> Steam names the game's program (the file of its launch entry), but
/// that is often only a launcher: measured on Boltgun the named program holds 42 MB and its
/// child <c>Boltgun-Win64-Shipping.exe</c> 1.4 GB. So the family — the program plus everything it
/// started, over any number of generations — is the candidate set, and the largest member by
/// memory is the game. A program a module attached later (Cheat Engine) is a child of the game
/// and may be large; modules therefore name their images, and those are left out.
/// </para>
/// </summary>
public static class GameProcessChoice
{
    /// <summary>
    /// The family member with the largest memory, or <c>null</c> when there is none.
    /// </summary>
    /// <param name="programFileName">The game's program from Steam's launch; compared without
    /// regard to case.</param>
    /// <param name="excludedImages">Images that never count as the game — their own descendants
    /// still belong to the family.</param>
    /// <param name="parents">Who started whom.</param>
    /// <param name="memory">The memory per pid; family members missing here are skipped.</param>
    public static int? Choose(
        string programFileName,
        IReadOnlyList<string> excludedImages,
        IReadOnlyList<(string Image, int Pid, int ParentPid)> parents,
        IReadOnlyList<(string Image, int Pid, long MemoryKilobytes)> memory)
    {
        var family = FamilyOf(programFileName, excludedImages, parents);
        var memoryOfPid = new Dictionary<int, long>();
        foreach (var (_, pid, kilobytes) in memory)
        {
            memoryOfPid.TryAdd(pid, kilobytes);
        }

        // Ties go to the lower pid: the older process, and the same answer on every run.
        return family
            .Where(member => memoryOfPid.ContainsKey(member.Pid))
            .OrderByDescending(member => memoryOfPid[member.Pid])
            .ThenBy(member => member.Pid)
            .Select(member => (int?)member.Pid)
            .FirstOrDefault();
    }

    /// <summary>
    /// The family without the excluded images: the program's processes and all their descendants.
    /// Walks by parent pid with a visited set — a pid the container reused could otherwise make a
    /// loop.
    /// </summary>
    internal static IReadOnlyList<(string Image, int Pid, int ParentPid)> FamilyOf(
        string programFileName,
        IReadOnlyList<string> excludedImages,
        IReadOnlyList<(string Image, int Pid, int ParentPid)> parents)
    {
        var visited = new HashSet<int>();
        var pending = new Queue<(string Image, int Pid, int ParentPid)>(
            parents.Where(process => IsImage(process.Image, programFileName)));
        var family = new List<(string Image, int Pid, int ParentPid)>();

        while (pending.TryDequeue(out var process))
        {
            if (!visited.Add(process.Pid))
            {
                continue;
            }

            if (!excludedImages.Any(excluded => IsImage(process.Image, excluded)))
            {
                family.Add(process);
            }

            foreach (var child in parents.Where(other => other.ParentPid == process.Pid))
            {
                pending.Enqueue(child);
            }
        }

        return family;
    }

    private static bool IsImage(string image, string expected)
        => string.Equals(image, expected, StringComparison.OrdinalIgnoreCase);
}
