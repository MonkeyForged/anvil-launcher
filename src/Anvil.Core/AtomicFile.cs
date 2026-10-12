namespace Anvil.Core;

/// <summary>
/// Ersetzt eine Datei unteilbar: erst vollständig in eine Temp-Datei im <b>selben</b>
/// Verzeichnis schreiben, dann umbenennen. Umbenennen innerhalb eines Verzeichnisses ist ein
/// einziger Dateisystem-Schritt — es gibt keinen Zeitpunkt, zu dem die Zieldatei halb
/// geschrieben ist: Abbruch vorher lässt sie unverändert, Abbruch nachher fertig zurück.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
        => Write(path, tempPath => File.WriteAllText(tempPath, text));

    /// <summary>Die Byte-Variante (S4.1): Sicherungen werden so zurückgespielt.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
        => Write(path, tempPath => File.WriteAllBytes(tempPath, bytes));

    private static void Write(string path, Action<string> writeTemp)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            writeTemp(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Aufräumen ist best effort — die Zieldatei ist in jedem Fall unversehrt.
        }
    }
}
