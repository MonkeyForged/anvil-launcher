using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Liest und schreibt die Vermerke im Prefix (Spec S3.6 §3.2, E-3):
/// <c>&lt;pfx&gt;/.anvil/payloads/&lt;Komponentenschlüssel&gt;.json</c>.
///
/// <para>
/// <b>Eine Datei je Nutzlast</b> (E-3): „Zwei Nutzlasten stehen nebeneinander, keine
/// überschreibt die andere" ist damit eine Eigenschaft des Dateisystems, nicht einer
/// Lese-Ändere-Schreibe-Folge auf einer Liste. <b>Im Prefix</b>, weil der Vermerk dem Prefix
/// gehört — wird der Prefix gelöscht, verschwindet der Vermerk mit.
/// </para>
///
/// <para>
/// <b>Lesen wirft nie</b>: Eine fehlende oder kaputte Datei ergibt <c>null</c> (kaputt mit
/// Log-Warnung). Niemand entscheidet nach dem Vermerk, also ist „unbekannt" eine ehrliche
/// Antwort und kein Grund für eine Ausnahme.
/// </para>
/// </summary>
public static class PrefixPayloadRecords
{
    /// <summary>Anvils Ordner im Prefix — ein Punktordner wie <c>.build-</c> im Paketspeicher.</summary>
    public const string FolderName = ".anvil";

    /// <summary>Der Unterordner der Vermerke.</summary>
    public const string PayloadsFolderName = "payloads";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Der Pfad des Vermerks zu <paramref name="componentKey"/>.</summary>
    public static string PathFor(string prefixPath, string componentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefixPath);
        return Path.Combine(prefixPath, FolderName, PayloadsFolderName, EnsurePlainName(componentKey) + ".json");
    }

    /// <summary>
    /// Der Schlüssel wird ein Dateiname — er darf den Ordner nicht verlassen. Geteilt mit
    /// <see cref="PrefixPayloadBackup"/>, damit beide dieselbe Grenze ziehen.
    /// </summary>
    internal static string EnsurePlainName(string componentKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentKey);

        if (componentKey.IndexOfAny(['/', '\\']) >= 0 || componentKey is "." or "..")
        {
            throw new ArgumentException(
                $"A component key must be a plain file name, but got '{componentKey}'.", nameof(componentKey));
        }

        return componentKey;
    }

    /// <summary>Der Vermerk, oder <c>null</c> — fehlt, kaputt oder ungültig.</summary>
    public static PrefixPayloadRecord? Read(string prefixPath, string componentKey, ILogger? logger = null)
    {
        var path = PathFor(prefixPath, componentKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PrefixPayloadRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger?.LogWarning(exception, "payload record unreadable, treated as missing: {Path}", path);
            return null;
        }
    }

    /// <summary>Schreibt den Vermerk unteilbar; legt die Ordner an.</summary>
    public static void Write(string prefixPath, PrefixPayloadRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var path = PathFor(prefixPath, record.ComponentKey);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions));
    }
}
