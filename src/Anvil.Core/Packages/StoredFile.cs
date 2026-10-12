namespace Anvil.Core.Packages;

/// <summary>
/// Eine Datei, die in einer Ablage liegt (S3.3).
///
/// <para>
/// <b>Anvil liest, was da ist — es verwaltet keinen Bestand.</b> Dieser Record entsteht beim
/// Auflisten neu und wird nirgends aufbewahrt; die Wahrheit über „was liegt da" ist der Ordner
/// selbst. Ein Index daneben wäre eine zweite Wahrheit und hätte sofort einen Fall, in dem er
/// lügt: Der Nutzer kopiert eine Datei im Dateimanager hinein, während Anvil läuft.
/// </para>
/// </summary>
/// <param name="Name">Der Dateiname ohne Pfad — was der Nutzer sieht und was gespeichert wird.</param>
/// <param name="FullPath">Für den, der die Datei wirklich öffnet (S3.5).</param>
/// <param name="SizeBytes">
/// Steht dabei, damit die Auswahlzeile in S3.4 einen 69-MB-Installer von einer 4-KB-Fehlkopie
/// unterscheiden kann, ohne die Datei zu öffnen.
/// </param>
public sealed record StoredFile(string Name, string FullPath, long SizeBytes);
