namespace Anvil.Core.Steam;

/// <summary>
/// Der Prefix-Beweis (Spec §6.1). <b>Ein <c>compatdata/&lt;appid&gt;</c> ist noch kein
/// Prefix</b> — Steam Cloud legt das Verzeichnis schon beim Installieren an (Übergabe §2.1).
/// Der Beweis ist <c>pfx/system.reg</c>.
///
/// <para>
/// Gemessen am 2026-09-04: Vier Spiele der Owner-Bibliothek haben einen <c>pfx</c>-Ordner
/// ohne <c>system.reg</c>. Die Ordner-Regel des Vorgängers löge dort — bei vier von
/// siebzehn.
/// </para>
/// </summary>
public static class GamePrefix
{
    /// <summary>
    /// Liefert den <c>pfx</c>-Ordner, wenn er einen echten Prefix enthält, sonst
    /// <c>null</c>. <paramref name="libraryPath"/> ist die Bibliothek des Spiels, nicht die
    /// Steam-Wurzel: bei mehreren Bibliotheken liegt <c>compatdata</c> neben dem Manifest.
    /// </summary>
    public static string? Find(string libraryPath, uint appId)
    {
        var pfx = Path.Combine(
            libraryPath, "steamapps", "compatdata", appId.ToString(), "pfx");

        return File.Exists(Path.Combine(pfx, "system.reg")) ? pfx : null;
    }
}
