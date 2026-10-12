namespace Anvil.App.Bridges;

/// <summary>
/// Finds where a bridge's program lies inside an installation. Shared by the bridges because
/// both search instead of assuming a fixed layout: a vendor archive nests the program, a
/// hand-packed one may not.
/// </summary>
public static class ProgramLocation
{
    /// <summary>
    /// Level at which <paramref name="programName"/> lies among <paramref name="relativePaths"/>:
    /// the prefix including its trailing '/', "" when it lies at the top, null when it is absent.
    /// Paths use '/' as separator (zip entry names; folder listings are converted by the
    /// caller). The file name compares case-insensitively. Several hits: the shallowest level
    /// wins, among equally deep ones the first in the given order. Directory entries (ending in
    /// '/') are ignored. Never throws.
    /// </summary>
    public static string? LevelOf(IEnumerable<string> relativePaths, string programName)
    {
        string? best = null;
        var bestDepth = int.MaxValue;

        foreach (var path in relativePaths)
        {
            var slash = path.LastIndexOf('/');
            var name = path[(slash + 1)..];
            if (!string.Equals(name, programName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var level = path[..(slash + 1)];
            var depth = level.Count(character => character == '/');

            // Strictly shallower only: on a tie the earlier entry stays.
            if (depth < bestDepth)
            {
                best = level;
                bestDepth = depth;
            }
        }

        return best;
    }
}
