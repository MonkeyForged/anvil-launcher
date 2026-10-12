namespace Anvil.Core.Packages;

/// <summary>
/// Die Naht zum Netz für den Bezug (S3.3a).
///
/// <para>
/// <b>Warum überhaupt eine Naht:</b> Ein Test, der wirklich lädt, kann weder den Abbruch noch
/// eine falsche Prüfsumme herbeiführen, ohne die geladene Datei zu fälschen. Hinter dieser
/// Schnittstelle steckt im Betrieb <c>HttpPackageDownloader</c> (Aufgabe 3), in
/// <see cref="PackageFetcher"/>s Tests ein gefälschter Lader, der Bytes schreibt, die der Test
/// selbst bestimmt — damit sind Abbruch und Prüfsummenfehler ohne jedes Netz herbeizuführen.
/// </para>
/// </summary>
public interface IPackageDownloader
{
    /// <summary>Lädt <paramref name="address"/> nach <paramref name="target"/>.</summary>
    /// <param name="progress">
    /// Die bisher gelesenen Bytes als <b>laufende Summe</b>, keine Blockgröße (Plan P-6) — sonst
    /// müsste jeder Abonnent selbst mitzählen. Die Gesamtgröße steht nicht hier, sondern im
    /// <see cref="CatalogEntry"/> (E-11).
    /// </param>
    Task DownloadAsync(
        Uri address, Stream target, IProgress<long>? progress, CancellationToken cancellationToken);
}
