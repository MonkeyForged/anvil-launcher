namespace Anvil.Core.Steam;

/// <summary>
/// Wer ein Feld eines Spiels angefasst hat (Spec S4.8 §3.1) — der fehlende Absender.
///
/// <para>
/// <b>Warum das nicht im <see cref="SteamGameChange"/> steht</b> (E-C): Der Record trägt je
/// Spiel <i>einen</i> Wunsch mit zwei Feldern. Ein Absender je Wunsch wäre falsch, sobald zwei
/// Module am selben Spiel drehen; ein Absender je Feld <i>im</i> Record verdoppelte die
/// Feldstruktur. Und: Der Vermerk entsteht <b>nach</b> <see cref="SteamChangeFilter.Reduce"/>
/// und schlägt für jedes übrig gebliebene Feld nach — ein gestrichenes Feld kann seinen Absender
/// gar nicht erst mitbringen. Nachschlagen statt Mitschleppen.
/// </para>
/// </summary>
public sealed record ChangeOrigin(uint AppId, SteamField Field, string ModuleKey);
