using Anvil.Core.Vdf;

namespace Anvil.Core.Steam;

/// <summary>
/// Liest ein einzelnes <c>appmanifest_*.acf</c> und entscheidet Spiel oder Werkzeug
/// (Spec §5, §6). Ein unlesbares Manifest kostet seinen eigenen Eintrag, nie den Lauf.
/// </summary>
public static class AppManifest
{
    /// <summary>
    /// Bekannte Steam-Beigaben, die kein <c>toolmanifest.vdf</c> tragen und trotzdem keine
    /// Spiele sind. Wächst diese Liste über eine Handvoll, ist die Regel falsch — nicht die
    /// Liste zu kurz (Spec §5).
    /// </summary>
    public static readonly IReadOnlySet<uint> KnownNonGameApps = new HashSet<uint>
    {
        228980, // Steamworks Common Redistributables — DirectX und VC-Runtime, kein Spiel.
    };

    /// <summary>
    /// Liest das Manifest. Liefert <c>null</c> und hängt eine Warnung an, wenn es unlesbar
    /// oder unvollständig ist.
    /// </summary>
    public static SteamGame? Read(
        string manifestPath, string libraryPath, ICollection<string> warnings)
    {
        VdfDocument document;
        try
        {
            document = VdfDocument.Load(manifestPath);
        }
        catch (VdfParseException exception)
        {
            warnings.Add($"Skipped {manifestPath}: {exception.Message}");
            return null;
        }
        catch (IOException exception)
        {
            warnings.Add($"Skipped {manifestPath}: {exception.Message}");
            return null;
        }

        var state = document.Root.FindObject("AppState");
        if (state is null)
        {
            warnings.Add($"Skipped {manifestPath}: no \"AppState\" section.");
            return null;
        }

        if (!uint.TryParse((state.Find("appid") as VdfString)?.Value, out var appId))
        {
            warnings.Add($"Skipped {manifestPath}: \"appid\" is missing or not a number.");
            return null;
        }

        var name = (state.Find("name") as VdfString)?.Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            warnings.Add($"Skipped {manifestPath}: \"name\" is missing.");
            return null;
        }

        var installDir = (state.Find("installdir") as VdfString)?.Value;
        if (string.IsNullOrWhiteSpace(installDir))
        {
            warnings.Add($"Skipped {manifestPath}: \"installdir\" is missing.");
            return null;
        }

        return new SteamGame(
            appId,
            name,
            Path.Combine(libraryPath, "steamapps", "common", installDir),
            libraryPath,
            ReadLastPlayed(state));
    }

    /// <summary>
    /// Ein Werkzeug trägt ein <b>positives</b> Zeichen: Steams eigenes
    /// <c>toolmanifest.vdf</c> oder eine bekannte Beigabe-AppId. Alles andere ist ein Spiel —
    /// ein durchgerutschtes Werkzeug fällt auf, ein verschlucktes Spiel nicht (Spec §5).
    /// </summary>
    public static bool IsTool(SteamGame entry)
        => KnownNonGameApps.Contains(entry.AppId)
            || File.Exists(Path.Combine(entry.InstallDirectory, "toolmanifest.vdf"));

    /// <summary>Steam schreibt <c>0</c> für „nie gestartet" — das ist kein Zeitpunkt.</summary>
    private static DateTimeOffset? ReadLastPlayed(VdfObject state)
    {
        var raw = (state.Find("LastPlayed") as VdfString)?.Value;
        if (!long.TryParse(raw, out var seconds) || seconds <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }
}
