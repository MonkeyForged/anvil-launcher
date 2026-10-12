using System.Text.Json;
using Anvil.Core;

namespace Anvil.App.Modules;

/// <param name="Version">The server's version marker of the stored copy; null when it sent none.</param>
/// <param name="CheckedAt">When the server last answered usefully — the copy's age is measured from here.</param>
public sealed record WandCatalogState(string? Version, DateTimeOffset CheckedAt);

/// <summary>
/// Wand's catalog on disk: the file exactly as it arrived, and a small note beside it (story
/// s6.1, spec E-4).
///
/// <para>
/// <b>Content first, then the note, each written atomically.</b> A half-written copy never
/// exists. If Anvil dies between the two writes, the note belongs to the previous copy; the next
/// check then gets "changed" instead of "unchanged", which is harmless.
/// </para>
///
/// <para>
/// <b>The module folder is a delegate:</b> the user can move the data root at runtime. Nothing
/// moves along; under a new root there is simply no copy yet (spec E-13).
/// </para>
/// </summary>
public sealed class WandCatalogCopy(Func<string> moduleFolder)
{
    private const string Folder = "catalog";
    private const string ContentFile = "catalog.json";
    private const string StateFile = "catalog.state.json";

    public string ContentPath => Path.Combine(moduleFolder(), Folder, ContentFile);

    public string StatePath => Path.Combine(moduleFolder(), Folder, StateFile);

    public byte[]? ReadContent()
        => File.Exists(ContentPath) ? File.ReadAllBytes(ContentPath) : null;

    /// <summary>Null when there is no note or it cannot be read — both mean "never checked".</summary>
    public WandCatalogState? ReadState()
    {
        if (!File.Exists(StatePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<WandCatalogState>(File.ReadAllText(StatePath));
            return state is { CheckedAt: var checkedAt } && checkedAt != default ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Replace(byte[] content, string? version, DateTimeOffset checkedAt)
    {
        AtomicFile.WriteAllBytes(ContentPath, content);
        WriteState(new WandCatalogState(version, checkedAt));
    }

    /// <summary>Records a useful answer that did not replace the copy; the version stays.</summary>
    public void MarkChecked(DateTimeOffset checkedAt)
        => WriteState(new WandCatalogState(ReadState()?.Version, checkedAt));

    private void WriteState(WandCatalogState state)
        => AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(state));
}
