using Anvil.Core.Autostart;

namespace Anvil.App;

/// <summary>
/// Was die Kommandozeile über den Start sagt (Spec S3.6 §3.4). Ein Flag, eine Stelle:
/// <c>--tray</c> startet ohne Fenster ins Tray. Die Zeichenkette gehört dem Core —
/// <see cref="AutostartEntry"/> schreibt sie in den Autostart-Eintrag, hier wird sie gelesen.
/// Gelesen wird sie einmal in <c>Program.Main</c> und als Singleton in die Composition Root
/// gereicht (Spec S3.9 E-E) — <c>App</c> holt sie von dort, nicht aus <c>desktop.Args</c>.
/// </summary>
public sealed record StartupOptions(bool StartInTray)
{
    public static StartupOptions Parse(IReadOnlyList<string> args)
        => new(args.Contains(AutostartEntry.TrayFlag));
}
