namespace Anvil.App.ViewModels;

/// <summary>
/// Der Inhalt des Detail-Popups (Spec S4.8 §3.7) — die Vollfassung dessen, was die Zeile kürzt.
///
/// <para>
/// <b>Ein eigenes ViewModel, kein Feldsatz am <see cref="BackupsViewModel"/></b> (E-R): Es hat
/// einen eigenen Lebenszyklus (offen, an einer Zeile hängend) und einen eigenen Inhalt, und ein
/// Test soll die Aufstellung prüfen können, ohne eine Liste zu bauen.
/// </para>
///
/// <para>
/// <b>Hier wird aus einem Datensatz Text für Menschen</b>: Der leere Wert heißt ab hier
/// <c>empty</c> — im Vermerk stünde sonst ein Wort, das niemand geschrieben hat (E-E).
/// </para>
/// </summary>
public sealed class BackupDetailsViewModel
{
    /// <summary>Ein leeres Feld ist ein gültiger Wert, kein fehlender (Design-Antwort zu 26).</summary>
    private const string Empty = "empty";

    public BackupDetailsViewModel(BackupRowViewModel row)
    {
        FileName = row.FileName;
        Meta = $"{row.Time} · {row.Module} · {row.Size}";
        Games = (row.Entry.Note?.Games ?? [])
            .Select(game => new BackupDetailsGame(
                game.Name,
                game.Fields
                    .Select(field => new BackupDetailsField(
                        field.Label, field.Before ?? Empty, field.After ?? Empty))
                    .ToList()))
            .ToList();
    }

    /// <summary>Die gesicherte Datei — <c>config.vdf</c>, nicht der Name der Kopie.</summary>
    public string FileName { get; }

    /// <summary>Zeitpunkt · Modul · Größe, in einer gedämpften Zeile unter dem Namen.</summary>
    public string Meta { get; }

    public IReadOnlyList<BackupDetailsGame> Games { get; }
}

/// <summary>Eine Gruppe des Popups: ein Spiel und seine geänderten Felder.</summary>
public sealed record BackupDetailsGame(string Name, IReadOnlyList<BackupDetailsField> Fields);

/// <summary>
/// Eine Zeile der Gruppe. <paramref name="Before"/> steht im Popup durchgestrichen neben
/// <paramref name="After"/>: Die Frage lautet „welchen Stand hole ich zurück?".
/// </summary>
public sealed record BackupDetailsField(string Label, string Before, string After);
