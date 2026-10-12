namespace Anvil.Core.Prefixes;

/// <summary>
/// Ist diese DLL Wines eigene, oder eine echte?
///
/// <para>
/// <b>Das Verfahren stammt von Proton selbst</b> (nachgelesen im <c>proton</c>-Skript,
/// <c>file_is_wine_builtin_dll</c>): An Offset <c>0x40</c> tragen Wines DLLs die Kennung
/// <c>Wine builtin DLL</c> oder <c>Wine placeholder DLL</c>. Dass Proton dieselbe Prüfung macht,
/// ist kein Zufall — es räumt danach eine .NET-Installation weg, die auf Wines
/// <c>mscoree.dll</c> steht (gemessen 2026-09-17, <c>messungen-anhaengen.md</c> §7.3).
/// </para>
///
/// <para>
/// <b>Gelesen wird der Inhalt, nicht der Pfad.</b> Ein Symlink in Protons Dateien führt zu
/// derselben Antwort wie eine Kopie davon — und genau so kommt Wines DLL in einen Prefix.
/// </para>
/// </summary>
public static class WineBuiltinDll
{
    private static readonly string[] Tags = ["Wine builtin DLL", "Wine placeholder DLL"];

    private const int TagOffset = 0x40;

    /// <summary>
    /// <c>true</c>, wenn dort Wines eigene DLL steht. Eine fehlende oder unlesbare Datei ist
    /// <c>false</c> — „nicht Wines" ist die ehrliche Antwort, die Existenz prüft der Aufrufer.
    /// </summary>
    public static bool Looks(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            stream.Seek(TagOffset, SeekOrigin.Begin);

            var buffer = new byte[24];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var tag = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            return Array.Exists(Tags, candidate => tag.StartsWith(candidate, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
