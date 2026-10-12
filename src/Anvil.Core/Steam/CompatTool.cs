namespace Anvil.Core.Steam;

/// <summary>Woher ein Compat-Tool kommt — die zwei Bauformen aus Spec §2.3.</summary>
public enum CompatToolSource
{
    /// <summary>Von Steam verwaltet, unter <c>steamapps/common</c>, Name aus dem Manifest.</summary>
    SteamApp,

    /// <summary>Selbst installiert, unter <c>compatibilitytools.d</c>, Name aus dessen VDF.</summary>
    External,
}

/// <summary>Ein installiertes Compat-Tool, so viel wie S2.6 und S4.3 davon brauchen.</summary>
/// <param name="Name">Anzeigename: <c>display_name</c>, oder der Manifest-Name.</param>
/// <param name="Path">Der Werkzeugordner, absolut.</param>
/// <param name="Source">Welche der beiden Bauformen.</param>
/// <param name="Key">
/// Der Schlüssel in <c>CompatToolMapping</c> (Spec S4.3 §4.3): für externe Tools der
/// Objektname unter <c>compat_tools</c>, für Valve-Tools aus <see cref="ValveCompatTools"/>
/// (Steams <c>appinfo.vdf</c>, S4.3a).
/// <c>null</c> heißt: bekannt, aber nicht wählbar (Runtimes, unbekannte Valve-Version) — E-C.
/// </param>
public sealed record CompatTool(string Name, string Path, CompatToolSource Source, string? Key);
