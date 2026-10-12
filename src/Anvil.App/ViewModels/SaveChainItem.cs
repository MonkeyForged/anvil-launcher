using Anvil.Core.Steam;

namespace Anvil.App.ViewModels;

/// <summary>
/// Ein Glied der Folge, wie die Speicherleiste es zeichnet (Spec S3.7 §3.4). Die fünf Zustände aus
/// Anforderung 30 a werden zu vier Schaltern: <see cref="IsPending"/> fasst kommend und übersprungen
/// zusammen, weil beide gleich aussehen — das Glied ist (noch) nicht dran.
/// </summary>
/// <param name="Label">Die Beschriftung, bei mehreren Spielen mit dem Spielnamen (Anforderung 30 c).</param>
/// <param name="State">Der Zustand aus der Momentaufnahme des Core.</param>
/// <param name="IsFirst">Das erste Glied bekommt keinen Verbinder davor.</param>
public sealed record SaveChainItem(string Label, ChainState State, bool IsFirst)
{
    public bool IsDone => State == ChainState.Done;

    public bool IsRunning => State == ChainState.Running;

    public bool IsFailed => State == ChainState.Failed;

    public bool IsPending => State is ChainState.Coming or ChainState.Skipped;

    public bool HasConnector => !IsFirst;
}
