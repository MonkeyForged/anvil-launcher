using Microsoft.Extensions.Logging;

namespace Anvil.Core.Sessions;

/// <summary>Wie ein Warten auf Anhaengbarkeit ausgegangen ist.</summary>
public enum AttachOutcome
{
    /// <summary>Der Bus-Name steht — ab jetzt kann ein Anhaengen folgen.</summary>
    Attachable,

    /// <summary>Das Spiel war laenger weg als die Kulanzzeit. Es kommt nicht wieder.</summary>
    GameGone,

    /// <summary>Das Spiel laeuft, wird aber nicht anhaengbar. Der Normalfall ist ein natives Spiel.</summary>
    TimedOut,
}

/// <param name="Outcome">Der Ausgang.</param>
/// <param name="Elapsed">
/// Wie lange gewartet wurde. <b>Keine Zierde:</b> Die Live-Abnahme von S4.3 besteht genau
/// darin, dass eine Logzeile diese Zahl fuer beide Testspiele nennt.
/// </param>
public readonly record struct AttachWait(AttachOutcome Outcome, TimeSpan Elapsed);

/// <summary>
/// Der Verstand hinter dem Warten auf „dieses Spiel ist jetzt anhaengbar" (Spec S4.3 §3.3).
///
/// <para>
/// <b>Synchron und ohne Timer</b>, wie <see cref="GameSessionWatcher.PollOnce"/> zur
/// Schleife <c>RunAsync</c>. Der Grund ist Pruefbarkeit (E-7): Ein Test, der eine
/// Warteschleife ueber eine gefaelschte Uhr treiben will, muss zwischen zwei <c>Advance</c>
/// raten, ob die Fortsetzung auf ihrem Thread-Pool-Faden schon angekommen ist. Hier rueckt
/// der Test die Uhr vor und macht selbst den naechsten Zug.
/// </para>
///
/// <para>
/// <b>Ein Zug, drei Fragen, in dieser Reihenfolge.</b> Die Busfrage steht vorn, weil ein Ja
/// jede andere Frage eruebrigt — auch dann, wenn der Waechter im selben Moment nichts mehr
/// fuehrt (E-4).
/// </para>
///
/// <para>
/// <b>Die Kulanzuhr wird verworfen, nicht angehalten</b> (E-5): Ein Spiel, das zurueckkommt,
/// beginnt beim naechsten Verschwinden von vorn. Die Alternative bewirtschaftete ein
/// Kulanzbudget ueber eine ganze Sitzung hinweg, das niemand bestellt hat.
/// </para>
///
/// <para>
/// <b>Die Uhr laeuft auch, bevor je eine Sitzung gesehen wurde.</b> Wer auf ein nicht
/// laufendes Spiel wartet, bekommt nach der Kulanzzeit <see cref="AttachOutcome.GameGone"/> —
/// eine ehrliche Antwort auf eine falsche Frage, statt zwei Minuten Schweigen.
/// </para>
/// </summary>
public sealed class AttachWatch
{
    private readonly TimeProvider _time;
    private readonly uint _appId;
    private readonly TimeSpan _grace;
    private readonly TimeSpan _deadline;
    private readonly ILogger? _logger;
    private readonly long _started;

    private long? _goneSince;

    public AttachWatch(
        TimeProvider time,
        uint appId,
        TimeSpan grace,
        TimeSpan deadline,
        ILogger? logger = null)
    {
        _time = time;
        _appId = appId;
        _grace = grace;
        _deadline = deadline;
        _logger = logger;
        _started = time.GetTimestamp();
    }

    /// <summary>
    /// Ein Zug. <c>null</c> heisst „weiter warten"; alles andere ist das Ergebnis.
    /// </summary>
    /// <param name="attachable">Steht der Bus-Name?</param>
    /// <param name="running">Fuehrt der Waechter dieses Spiel?</param>
    public AttachWait? Step(bool attachable, bool running)
    {
        var elapsed = _time.GetElapsedTime(_started);

        if (attachable)
        {
            return new AttachWait(AttachOutcome.Attachable, elapsed);
        }

        if (running)
        {
            _goneSince = null;
        }
        else
        {
            if (_goneSince is null)
            {
                _goneSince = _time.GetTimestamp();
                _logger?.LogDebug(
                    "appid {AppId} is not running; holding the attach wait for {Grace}",
                    _appId,
                    _grace);
            }

            if (_time.GetElapsedTime(_goneSince.Value) >= _grace)
            {
                return new AttachWait(AttachOutcome.GameGone, elapsed);
            }
        }

        return elapsed >= _deadline ? new AttachWait(AttachOutcome.TimedOut, elapsed) : null;
    }
}
