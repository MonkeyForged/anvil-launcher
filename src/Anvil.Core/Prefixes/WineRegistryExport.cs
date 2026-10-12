using System.Text;
using Anvil.Core.Processes;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Prefixes;

/// <summary>
/// Exportiert die deklarierten Registry-Zweige einer Komponente aus einem fertigen
/// Wegwerf-Prefix zu einer einzigen <c>.reg</c>-Datei (S3.5, Spec §3.7) — die zweite Hälfte der
/// Ernte, die andere ist <see cref="PrefixHarvest"/>.
///
/// <para>
/// <b>Der Rückgabewert von <c>regedit</c> sagt hier gar nichts</b> — gemessen (Spec §3.2, §3.7)
/// ist er 0, gleich ob der Zweig exportiert wurde oder nicht: Läuft <c>regedit /E</c> auf einen
/// Zweig, den es im Prefix nicht gibt, endet der Prozess mit Erfolg und schreibt einfach keine
/// Datei. <b>Das einzige Signal ist die entstandene Teildatei mit Inhalt.</b> Ein Zweig ohne
/// Inhalt ist deshalb kein Fehler dieser Klasse — <c>Fusion</c> kann in einem frischen Prefix
/// fehlen —, sondern wandert in <see cref="RegistryExportResult.MissingBranches"/>. Erst wenn
/// <b>kein</b> Zweig etwas hergibt, ist die Ernte gescheitert (<c>Ok = false</c>), und es wird
/// keine Datei geschrieben.
/// </para>
///
/// <para>
/// <b>Die Umgebung kommt gereicht, unverändert.</b> Der Bauer reicht die bildschirmlose Umgebung
/// aus <see cref="ProtonWine.RegistryEnvironment"/> — das ist hier lebenswichtig: Findet
/// <c>regedit /E</c> beim Export eines fehlenden Zweigs einen erreichbaren Bildschirm, öffnet es
/// einen <b>modalen Dialog</b> und kehrt nie zurück. Diese Klasse setzt keine eigene Umgebung
/// und prüft auch nicht, ob sie bildschirmlos ist — das zu sichern ist Sache des Aufrufers, hier
/// stünde jede eigene Prüfung doppelt.
/// </para>
///
/// <para>
/// <b>Teildateien landen außerhalb des Baus</b>, in einem eigenen
/// <see cref="Directory.CreateTempSubdirectory(string?)"/>, der in einem <c>finally</c> wieder
/// verschwindet — sonst lägen sechs Teildateien im fertigen Paket, wo sie niemand erwartet.
/// </para>
/// </summary>
public sealed class WineRegistryExport
{
    private const string RegistryFileHeader = "Windows Registry Editor Version 5.00";

    // Ein Export ist normalerweise augenblicklich; die Grenze ist eine Reißleine gegen einen
    // hängenden Prozess, keine Erwartung — dieselbe kurze Grenze wie bei den kurzen
    // Rezeptstufen (PrefixRecipe).
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(2);

    private static readonly UnicodeEncoding FileEncoding = new(bigEndian: false, byteOrderMark: true);

    private readonly IProcessRunner _runner;
    private readonly ILogger? _logger;

    /// <param name="runner">Führt jeden <c>regedit /E</c>-Aufruf aus.</param>
    /// <param name="logger"><c>null</c> heißt stumm.</param>
    public WineRegistryExport(IProcessRunner runner, ILogger? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger;
    }

    /// <summary>
    /// Exportiert <paramref name="branches"/> in der angegebenen Reihenfolge, je Zweig ein
    /// eigener Aufruf <c>wine regedit /E &lt;Teildatei&gt; HKEY_LOCAL_MACHINE\&lt;Zweig&gt;</c>,
    /// und setzt die gefundenen Teile zu <paramref name="targetFilePath"/> zusammen.
    /// </summary>
    /// <param name="winePath">Protons <c>wine</c> (<see cref="ProtonWine.Find"/>).</param>
    /// <param name="env">
    /// Die vollständige Umgebung des Aufrufs — vom Aufrufer bildschirmlos gereicht
    /// (Klassenkommentar). Wird unverändert an jeden Aufruf weitergegeben.
    /// </param>
    /// <param name="branches">
    /// Zweige relativ zu HKLM, einfache Backslashes (<see cref="PrefixPayload.RegistryBranches"/>
    /// validiert die Form). Diese Methode stellt <c>HKEY_LOCAL_MACHINE\</c> selbst voran.
    /// </param>
    /// <param name="targetFilePath">Wo die zusammengesetzte <c>.reg</c>-Datei entsteht.</param>
    /// <param name="cancellationToken">Wird vor jedem Zweig geprüft.</param>
    public async Task<RegistryExportResult> ExportAsync(
        string winePath,
        IReadOnlyDictionary<string, string> env,
        IReadOnlyList<string> branches,
        string targetFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(winePath);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);

        var tempDir = Directory.CreateTempSubdirectory("anvil-registry-export-");
        try
        {
            var exportedBranches = new List<string>();
            var missingBranches = new List<string>();
            var bodies = new List<string>();

            var index = 0;
            foreach (var branch in branches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var partFilePath = Path.Combine(tempDir.FullName, $"branch-{index}.reg");
                index++;

                var command = new ProcessCommand(
                    winePath,
                    ["regedit", "/E", partFilePath, $"HKEY_LOCAL_MACHINE\\{branch}"],
                    env);
                var outcome = await _runner.RunAsync(command, ExportTimeout, cancellationToken)
                    .ConfigureAwait(false);
                _logger?.LogDebug(
                    "registry export call finished: {Branch}, completed={Completed}, exitCode={ExitCode}",
                    branch,
                    outcome.Completed,
                    outcome.ExitCode);

                // Weder Completed noch ExitCode sagen etwas (Klassenkommentar) — geprüft wird
                // ausschließlich, ob eine Teildatei mit Inhalt entstanden ist.
                if (!File.Exists(partFilePath) || new FileInfo(partFilePath).Length == 0)
                {
                    missingBranches.Add(branch);
                    _logger?.LogInformation("registry branch not present in prefix, skipped: {Branch}", branch);
                    continue;
                }

                exportedBranches.Add(branch);
                bodies.Add(StripHeader(await File.ReadAllTextAsync(partFilePath, cancellationToken)
                    .ConfigureAwait(false)));
            }

            if (exportedBranches.Count == 0)
            {
                _logger?.LogWarning("registry export produced no branch, no file written");
                return new RegistryExportResult(Ok: false, [], missingBranches);
            }

            WriteCombinedFile(targetFilePath, bodies);
            return new RegistryExportResult(Ok: true, exportedBranches, missingBranches);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // Eine Teildatei aus "regedit /E" trägt selbst schon Kopf und Leerzeile
    // ("Windows Registry Editor Version 5.00", Leerzeile, Zweig-Inhalt) — die Zusammensetzung
    // braucht nur den Zweig-Inhalt, sonst stünde der Kopf sechsmal in der fertigen Datei.
    private static string StripHeader(string content)
    {
        var firstNewline = content.IndexOf('\n');
        if (firstNewline < 0)
        {
            return string.Empty;
        }

        var afterHeader = content[(firstNewline + 1)..];
        if (afterHeader.StartsWith("\r\n", StringComparison.Ordinal))
        {
            afterHeader = afterHeader[2..];
        }
        else if (afterHeader.StartsWith('\n'))
        {
            afterHeader = afterHeader[1..];
        }

        return afterHeader.TrimEnd('\r', '\n');
    }

    // Kopf, Leerzeile, dann die Teile ohne ihren eigenen Kopf, durch je eine Leerzeile getrennt
    // (Spec §3.7). UTF-16LE mit BOM, \r\n-Zeilenenden — eine Windows-Registrierungsdatei.
    private static void WriteCombinedFile(string targetFilePath, IReadOnlyList<string> bodies)
    {
        var builder = new StringBuilder();
        builder.Append(RegistryFileHeader).Append("\r\n\r\n");
        for (var i = 0; i < bodies.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("\r\n\r\n");
            }

            builder.Append(bodies[i]);
        }

        builder.Append("\r\n");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFilePath))!);
        File.WriteAllText(targetFilePath, builder.ToString(), FileEncoding);
    }
}

/// <summary>Was der Export tatsächlich zusammengetragen hat (S3.5, Spec §3.7).</summary>
/// <param name="Ok">
/// <c>false</c>, wenn <b>kein</b> Zweig etwas hergab — dann wurde keine Datei geschrieben. Ein
/// einzelner fehlender Zweig ist normal und macht daraus kein <c>false</c>.
/// </param>
/// <param name="ExportedBranches">Die Zweige, die tatsächlich in der Datei stehen, in Reihenfolge.</param>
/// <param name="MissingBranches">Die Zweige ohne Inhalt, in Reihenfolge.</param>
public sealed record RegistryExportResult(
    bool Ok, IReadOnlyList<string> ExportedBranches, IReadOnlyList<string> MissingBranches);
