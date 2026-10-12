using System.Globalization;
using Anvil.Core.Backups;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.App.ViewModels;

/// <summary>
/// Eine Zeile der Sicherungsliste (Spec S4.7 §3.3). <b>Zeitpunkt und Größe werden hier
/// formatiert, nicht in der Sicht</b> (E-G): Ein Wertkonverter für zwei Formate wäre eine Naht,
/// die niemand sonst braucht — und was im ViewModel entsteht, ist prüfbar.
/// </summary>
public sealed partial class BackupRowViewModel : ObservableObject
{
    /// <summary>Der Gedankenstrich für eine Sicherung ohne Vermerk.</summary>
    private const string NoModule = "—";

    /// <param name="entry">Die Sicherung, wie der Dienst sie gelesen hat.</param>
    /// <param name="targetPath">
    /// Wohin sie gehört, oder <c>null</c> — dann ist sie nicht zurückspielbar (E-E). Sie bleibt
    /// trotzdem in der Liste: Sie ist da, und löschen darf man sie.
    /// </param>
    public BackupRowViewModel(BackupEntry entry, string? targetPath)
    {
        Entry = entry;
        TargetPath = targetPath;
        Time = entry.Timestamp.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        FileName = entry.FileName;

        // Ganzzahlig in KB, mindestens 1: Steams Dateien liegen bei 38 und 412 KB, und ein
        // „0 KB" sähe nach einer kaputten Sicherung aus.
        var kilobytes = Math.Max(1, entry.Size / 1024);
        Size = kilobytes.ToString(CultureInfo.InvariantCulture) + " KB";

        // Ohne Vermerk ein Gedankenstrich, keine zweite Zeile, kein Popup (Design-Antwort zu 26,
        // Punkt 4): Die Zeile soll nicht erklären, was sie nicht weiß. Dass sie einzeilig ist,
        // macht sie ohne ein Wort als älteren Stand erkennbar.
        var note = entry.Note;
        Module = note?.ModuleKey ?? NoModule;
        Reason = note?.Summary ?? string.Empty;
        CanShowDetails = note is { Games.Count: > 0 };
    }

    public BackupEntry Entry { get; }

    public string? TargetPath { get; }

    public string Time { get; }

    public string FileName { get; }

    public string Size { get; }

    /// <summary>Der auslösende Modulschlüssel, oder ein Gedankenstrich.</summary>
    public string Module { get; }

    /// <summary>Der Anlass in einer Zeile; leer ohne Vermerk.</summary>
    public string Reason { get; }

    /// <summary>Ob überhaupt eine zweite Zeile gezeichnet wird.</summary>
    public bool HasReason => Reason.Length > 0;

    /// <summary>
    /// Ob die zweite Zeile das Popup öffnet. <b>Nur mit Aufstellung</b> (Spec E-O): Die
    /// gepunktete Unterlinie verspricht „hier steht mehr" — bei einem Restore steht nicht mehr da.
    /// </summary>
    public bool CanShowDetails { get; }

    /// <summary>Ein Anlass ohne Popup — als ruhiger Text, ohne Affordanz.</summary>
    public bool HasPlainReason => HasReason && !CanShowDetails;

    /// <summary>Setzt die Liste — sie kennt Sperre und Ziel (E-I).</summary>
    [ObservableProperty]
    private bool _canRestore;
}
