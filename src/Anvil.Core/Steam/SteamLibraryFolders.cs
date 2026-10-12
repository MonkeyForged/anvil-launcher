using Anvil.Core.Vdf;

namespace Anvil.Core.Steam;

/// <summary>
/// Liest <c>steamapps/libraryfolders.vdf</c>. Ist die Datei weg oder unbrauchbar, gilt die
/// Wurzel selbst als einzige Bibliothek — auf der echten Maschine ist sie die Bibliothek 0,
/// der Rückfall ist also der Normalfall und nicht geraten (Spec §8).
/// </summary>
public static class SteamLibraryFolders
{
    public static IReadOnlyList<string> Read(string root, ICollection<string> warnings)
    {
        var path = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(path))
        {
            warnings.Add($"No library list at {path} — using the Steam folder itself.");
            return [root];
        }

        VdfDocument document;
        try
        {
            document = VdfDocument.Load(path);
        }
        catch (VdfParseException exception)
        {
            warnings.Add($"Cannot read {path}: {exception.Message} — using the Steam folder itself.");
            return [root];
        }
        catch (IOException exception)
        {
            warnings.Add($"Cannot read {path}: {exception.Message} — using the Steam folder itself.");
            return [root];
        }

        var folders = document.Root.FindObject("libraryfolders");
        var paths = new List<string>();
        if (folders is not null)
        {
            foreach (var child in folders.Children)
            {
                if (child is VdfObject entry
                    && entry.Find("path") is VdfString value
                    && !string.IsNullOrWhiteSpace(value.Value))
                {
                    paths.Add(value.Value);
                }
            }
        }

        if (paths.Count == 0)
        {
            warnings.Add($"No library paths in {path} — using the Steam folder itself.");
            return [root];
        }

        return paths;
    }
}
