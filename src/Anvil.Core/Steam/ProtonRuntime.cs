namespace Anvil.Core.Steam;

/// <summary>
/// Welches Proton für dieses Spiel läuft (Spec §5). Die Quelle ist <c>config_info</c> und nur
/// die: <c>CompatToolMapping</c> ist auf der Owner-Maschine leer, Steam wählt selbst
/// (Übergabe §2.1).
/// </summary>
/// <param name="Version">Die Version, z. B. <c>11.0-100</c>.</param>
/// <param name="Tool">Das aufgelöste Werkzeug — <c>null</c>, wenn keines passt.</param>
/// <param name="ToolPath">
/// Der Werkzeugordner aus <c>config_info</c>, roh. Bleibt gefüllt, auch wenn
/// <paramref name="Tool"/> <c>null</c> ist: zeigt die Datei auf ein deinstalliertes Werkzeug,
/// ist der Pfad die beste verfügbare Auskunft (Spec §7, E7).
/// </param>
public sealed record ProtonRuntime(string Version, CompatTool? Tool, string? ToolPath);
