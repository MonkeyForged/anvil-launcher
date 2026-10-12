namespace Anvil.Core.Modules;

/// <summary>
/// Ein vorgemerkter Je-Spiel-Schalter (Spec S3.8 §3.1): welches Modul, welches Spiel, an oder aus.
/// Der Wunsch steht in <see cref="Anvil.Core.Steam.PendingChanges"/>; geschrieben wird er erst beim
/// Speichern, über <see cref="ModuleRegistry.SaveEnabledFor"/>.
/// </summary>
public sealed record ModuleGameSwitch(string Key, uint AppId, bool Enabled);
