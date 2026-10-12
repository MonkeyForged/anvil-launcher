using System.Globalization;
using Anvil.Core.Backups;

namespace Anvil.Core.Steam;

/// <summary>
/// Baut den Vermerk einer Sicherung (Spec S4.8 §3.3) — was unmittelbar nach ihr in
/// <b>diese eine Datei</b> geschrieben wird.
///
/// <para>
/// <b>Eine eigene Klasse, keine Methode der Transaktion</b>: Es ist reine Rechnung aus Ist, Soll
/// und Absender. So ist sie ohne Dateisystem und ohne Steam-Prozess prüfbar — und die Transaktion
/// bleibt der Ablauf, der sie ist.
/// </para>
/// </summary>
public static class BackupNoteBuilder
{
    /// <summary>Kein Eintrag in <c>CompatToolMapping</c> — Steam wählt selbst.</summary>
    private const string Auto = "Auto";

    /// <summary>Mehr als ein auslösendes Modul (Spec S4.8 E-L).</summary>
    private const string Mixed = "mixed";

    /// <param name="fileName">Die gesicherte Datei, ohne Pfad — <c>config.vdf</c> etwa.</param>
    /// <param name="todo">Die Wünsche <b>nach</b> dem Aussortieren gegen das frische Ist.</param>
    /// <param name="origins">Wer welches Feld gemeldet hat (S4.5/S4.8).</param>
    /// <param name="snapshot">Das frische Ist: Spielnamen, alte Werte, Werkzeugnamen.</param>
    /// <returns>Der Vermerk, oder <c>null</c> — dann gehört zu dieser Datei nichts.</returns>
    public static BackupNote? Build(
        string fileName,
        IReadOnlyList<SteamGameChange> todo,
        IReadOnlyList<ChangeOrigin> origins,
        SteamSnapshot snapshot)
    {
        var games = new List<BackupNoteGame>();
        var summaries = new List<string>();
        var modules = new HashSet<string>(StringComparer.Ordinal);

        foreach (var change in todo)
        {
            var profile = snapshot.Games
                .FirstOrDefault(game => game.Game.AppId == change.AppId);

            var fields = new List<BackupNoteField>();
            var parts = new List<string>();

            foreach (var field in FieldsIn(change, fileName))
            {
                fields.Add(FieldFor(field, change, profile, snapshot));
                parts.Add(SummaryPart(field, change, snapshot));

                if (ModuleFor(origins, change.AppId, field) is { } module)
                {
                    modules.Add(module);
                }
            }

            if (fields.Count == 0)
            {
                continue;
            }

            // Was die Momentaufnahme nicht kennt, heisst nach seiner AppId — ein Wunsch fuer ein
            // deinstalliertes Spiel ist gueltig (dieselbe Haltung wie SteamChangeFilter).
            var name = profile?.Game.Name
                ?? change.AppId.ToString(CultureInfo.InvariantCulture);

            games.Add(new BackupNoteGame(name, fields));

            // Die Verbindung mit ", " ist heute nie mehr als ein Element: Die zwei Felder
            // liegen in zwei Dateien, und der Vermerk gehoert einer davon. Sie bleibt trotzdem
            // stehen — ein drittes Feld in einer schon belegten Datei braucht sie sofort, und
            // string.Join mit einem Element kostet nichts.
            summaries.Add($"{name}: {string.Join(", ", parts)}");
        }

        if (games.Count == 0)
        {
            return null;
        }

        // Genau ein Absender nennt sich; alles andere ist „mixed". Auch der Fall ohne Absender —
        // er entsteht nur in Tests, und ein leeres Feld waere in der Spalte ein zweiter
        // Gedankenstrich mit anderer Bedeutung.
        var moduleKey = modules.Count == 1 ? modules.First() : Mixed;

        return new BackupNote(moduleKey, string.Join(" · ", summaries), games);
    }

    /// <summary>
    /// Die gesetzten Felder dieses Wunsches, die in <b>diese</b> Datei gehoeren (E-J).
    /// </summary>
    private static IEnumerable<SteamField> FieldsIn(SteamGameChange change, string fileName)
    {
        if (change.Proton is not null
            && SteamConfigPaths.FileFor(SteamField.Proton) == fileName)
        {
            yield return SteamField.Proton;
        }

        if (change.LaunchOptions is not null
            && SteamConfigPaths.FileFor(SteamField.LaunchOptions) == fileName)
        {
            yield return SteamField.LaunchOptions;
        }
    }

    private static BackupNoteField FieldFor(
        SteamField field, SteamGameChange change, GameProfile? profile, SteamSnapshot snapshot)
        => field switch
        {
            // Kennt die Momentaufnahme das Spiel nicht, gibt es keinen alten Wert — null, nicht
            // „Auto": „Auto" waere eine Behauptung ueber eine Datei, die niemand gelesen hat.
            SteamField.Proton => new BackupNoteField(
                field.Label(),
                profile is null ? null : ToolName(profile.CompatToolKey, snapshot),
                ToolName(change.Proton!.ToolKey, snapshot)),

            _ => new BackupNoteField(
                field.Label(),
                profile?.LaunchOptions,
                change.LaunchOptions is { Length: > 0 } text ? text : null),
        };

    /// <summary>
    /// Eine Proton-Wahl nennt ihren Zielwert, Startoptionen nur sich (Spec S4.8 §3.4): Eine
    /// Proton-Wahl <i>ist</i> ein kurzer Name, eine Startoption ist beliebig langer Text.
    /// </summary>
    private static string SummaryPart(
        SteamField field, SteamGameChange change, SteamSnapshot snapshot)
        => field == SteamField.Proton
            ? $"{field.InSentence()} → {ToolName(change.Proton!.ToolKey, snapshot)}"
            : field.InSentence();

    /// <summary>
    /// Der Anzeigename zu einem Mapping-Schluessel. Ohne Schluessel „Auto"; ein Schluessel ohne
    /// Werkzeug (deinstalliert) steht fuer sich selbst — die beste verfuegbare Auskunft, wie in
    /// der Herkunftszeile aus S4.6.
    /// </summary>
    private static string ToolName(string? key, SteamSnapshot snapshot)
        => key is null ? Auto : snapshot.CompatTools.FindByKey(key)?.Name ?? key;

    private static string? ModuleFor(
        IReadOnlyList<ChangeOrigin> origins, uint appId, SteamField field)
        => origins
            .FirstOrDefault(origin => origin.AppId == appId && origin.Field == field)
            ?.ModuleKey;
}
