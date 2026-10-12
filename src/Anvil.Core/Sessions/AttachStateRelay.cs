using Anvil.Core.Modules;
using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>Wie eine der beiden Bewegungen ausgegangen ist.</summary>
public enum StateRelayOutcome
{
    /// <summary>Kopiert. <see cref="StateRelay.Count"/> sagt, wie viele Dateien.</summary>
    Copied,

    /// <summary>
    /// Die Quelle war leer oder fehlte. <b>Kein Fehler</b> — beim Saeen ist das der Normalfall vor
    /// der ersten Ernte.
    /// </summary>
    NothingToCopy,

    /// <summary>Im Prefix fehlt der Ordner, in dem das Ziel liegen muesste.</summary>
    NoPrefixFolder,

    /// <summary>Das Kopieren scheiterte; die alte Seite steht unveraendert.</summary>
    CopyFailed,
}

/// <param name="Outcome">Der Ausgang.</param>
/// <param name="Reason">Ein ganzer englischer Satz bei jedem Fehlausgang, <c>null</c> sonst.</param>
/// <param name="Count">Wie viele Dateien kopiert wurden; 0 bei allem ausser
/// <see cref="StateRelayOutcome.Copied"/>.</param>
public readonly record struct StateRelay(StateRelayOutcome Outcome, string? Reason, int Count);

/// <summary>
/// Die zwei Bewegungen des Login-Ordners (Spec S4.6 §3.3): <b>saeen</b> vor dem Anhaengen,
/// <b>ernten</b> nach einem Abkoppeln mit <c>DetachOutcome.Detached</c>.
///
/// <para>
/// <b>Eine leere Vorlage saet nicht.</b> Der Nutzer meldet sich <i>im</i> Prefix an, nie in der
/// Vorlage — vor der ersten Ernte ist die Vorlage leer, und stumpfes Saeen ueberschriebe die
/// einzige vorhandene Anmeldung mit nichts.
/// </para>
///
/// <para>
/// <b>Gesaet wird bei jedem Anhaengen</b>, nicht nur beim ersten. Sonst erreichte ein Abmelden im
/// zweiten Spiel das erste nie.
/// </para>
///
/// <para>
/// <b>Geerntet wird nur nach <c>Detached</c></b> — das entscheidet der Aufrufer. Diese Klasse
/// kennt das Abkoppeln nicht; sie kopiert, wenn sie gerufen wird.
/// </para>
/// </summary>
public sealed class AttachStateRelay
{
    private readonly ILogger? _logger;

    /// <param name="logger">Kategorie des Aufrufers; null heisst stumm.</param>
    public AttachStateRelay(ILogger? logger = null) => _logger = logger;

    /// <summary>
    /// Legt die Vorlage in den Prefix. Wirft nur bei Abbruch; jeder andere Ausgang ist ein Ergebnis
    /// mit Grund.
    /// </summary>
    public StateRelay Seed(
        string prefixPath, AttachStateFolder folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!StateFolderCopy.HasAnythingToCopy(folder.TemplatePath, folder.Skipped))
        {
            // Der Normalfall vor der ersten Ernte. Was im Prefix liegt, bleibt unangetastet.
            _logger?.LogInformation(
                "nothing to seed from {Template} — leaving the prefix copy alone",
                folder.TemplatePath);
            return new StateRelay(StateRelayOutcome.NothingToCopy, null, 0);
        }

        var target = folder.PathIn(prefixPath);
        var parent = Path.GetDirectoryName(target);
        if (parent is null || !Directory.Exists(parent))
        {
            return Refuse(
                StateRelayOutcome.NoPrefixFolder,
                $"{parent} does not exist in the prefix — there is no place for the folder.");
        }

        return Copy(folder.TemplatePath, target, folder.Skipped, "seeded", cancellationToken);
    }

    /// <summary>
    /// Macht die Kopie im Prefix zur neuen Vorlage. <b>Erst rufen, wenn kein eigener Prozess mehr
    /// lebt</b> (Spec S4.6, E-1): Ein Abkoppeln, das fehlschlug, kopiert nicht zurueck.
    /// </summary>
    public StateRelay Harvest(
        string prefixPath, AttachStateFolder folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var source = folder.PathIn(prefixPath);
        if (!StateFolderCopy.HasAnythingToCopy(source, folder.Skipped))
        {
            _logger?.LogInformation(
                "nothing to harvest from {Source} — leaving the template alone", source);
            return new StateRelay(StateRelayOutcome.NothingToCopy, null, 0);
        }

        var parent = Path.GetDirectoryName(folder.TemplatePath);
        if (parent is not null)
        {
            // Anders als im Prefix wird hier angelegt: Der Modulordner gehoert Anvil, und die
            // Vorlage entsteht ueberhaupt erst durch die erste Ernte.
            Directory.CreateDirectory(parent);
        }

        return Copy(source, folder.TemplatePath, folder.Skipped, "harvested", cancellationToken);
    }

    private StateRelay Copy(
        string source,
        string target,
        IReadOnlyList<string> skipped,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            var count = StateFolderCopy.Replace(source, target, skipped, cancellationToken);
            _logger?.LogInformation("{What} {Count} file(s) into {Target}", what, count, target);
            return new StateRelay(StateRelayOutcome.Copied, null, count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogError(exception, "cannot copy {Source} to {Target}", source, target);
            return Refuse(
                StateRelayOutcome.CopyFailed,
                "The folder could not be copied — see the log for details.");
        }
    }

    private StateRelay Refuse(StateRelayOutcome outcome, string reason)
    {
        _logger?.LogWarning("state folder refused: {Outcome} — {Reason}", outcome, reason);
        return new StateRelay(outcome, reason, 0);
    }
}
