namespace Anvil.Core.Steam;

/// <summary>Was <c>config_info</c> hergibt: die Version, und der Ordner des Werkzeugs.</summary>
/// <param name="Version">Die erste nichtleere Zeile, z. B. <c>11.0-100</c>.</param>
/// <param name="ToolPath">Der Werkzeugordner aus den Pfadzeilen, roh — oder <c>null</c>.</param>
public sealed record ProtonConfigInfoResult(string Version, string? ToolPath);

/// <summary>
/// Liest <c>compatdata/&lt;appid&gt;/config_info</c> (Spec §6.2).
///
/// <para>
/// <b>Kein VDF.</b> Die Datei besteht aus Klartextzeilen — der Leser aus S2.1 hilft hier
/// nicht. Zeile 1 ist die Version, danach folgen Pfade und Zeitstempel. Gemessener Aufbau in
/// Spec §2.2.
/// </para>
///
/// <para>
/// <c>config_info</c> ist die <b>einzige</b> Wahrheit über die benutzte Proton-Version:
/// <c>CompatToolMapping</c> ist auf der Owner-Maschine leer, Steam wählt selbst
/// (Übergabe §2.1).
/// </para>
/// </summary>
public static class ProtonConfigInfo
{
    /// <summary>
    /// Die Unterordner, die Steam an einen Werkzeugpfad anhängt. Der Katalog kennt den
    /// Werkzeugordner selbst, also wird der Anhang wieder abgeschnitten.
    /// </summary>
    private static readonly string[] ToolSubPaths = ["/files/", "/dist/"];

    /// <summary>
    /// Liest die Datei. <c>null</c>, wenn es sie nicht gibt (kein Fehler — der Normalfall bei
    /// nativen und nie gestarteten Spielen) oder wenn sie keine Version hergibt (Warnung).
    /// </summary>
    public static ProtonConfigInfoResult? Read(
        string libraryPath, uint appId, ICollection<string> warnings)
    {
        var path = Path.Combine(
            libraryPath, "steamapps", "compatdata", appId.ToString(), "config_info");

        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not read {path}: {exception.Message}");
            return null;
        }

        var version = Array.Find(lines, line => !string.IsNullOrWhiteSpace(line))?.Trim();
        if (string.IsNullOrEmpty(version))
        {
            warnings.Add($"No Proton version in {path}.");
            return null;
        }

        return new ProtonConfigInfoResult(version, FindToolPath(lines));
    }

    /// <summary>
    /// Steht in <c>config_info</c> heute etwas anderes als beim Einlesen von
    /// <paramref name="profile"/>? Die Datei ändert sich nur, wenn das Spiel startet — nach einem
    /// Proton-Wechsel also erst beim ersten Start danach, lange nach dem Rescan des Speicherns.
    /// Verglichen werden Version und Werkzeugpfad, denn Proton 11.0 und Experimental schreiben
    /// dieselbe Version (Vision Wand Bridge §6.3). Eine unlesbare Datei zählt als geändert: Der
    /// Rescan danach schreibt die Warnung ins Log.
    /// </summary>
    public static bool Differs(GameProfile profile)
    {
        var game = profile.Game;
        var now = Read(game.LibraryPath, game.AppId, []);
        var then = profile.Proton;

        if (now is null || then is null)
        {
            return (now is null) != (then is null);
        }

        return !string.Equals(now.Version, then.Version, StringComparison.Ordinal)
            || !string.Equals(now.ToolPath, then.ToolPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sucht die erste absolute Pfadzeile, die auf einen Werkzeug-Unterordner zeigt, und
    /// schneidet diesen ab. Die Zeitstempelzeilen und die Steam-Wurzel fallen dabei von selbst
    /// heraus — es braucht keine Behandlung nach Zeilennummer (Spec §6.2).
    /// </summary>
    private static string? FindToolPath(string[] lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (!line.StartsWith('/'))
            {
                continue;
            }

            foreach (var marker in ToolSubPaths)
            {
                var cut = line.IndexOf(marker, StringComparison.Ordinal);
                if (cut > 0)
                {
                    return line[..cut];
                }
            }
        }

        return null;
    }
}
