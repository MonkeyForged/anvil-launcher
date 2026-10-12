namespace Anvil.Core.Logging;

/// <summary>Thread-sicherer Puffer fester Kapazität; Datenquelle der Log-Seite (S1.6).</summary>
public sealed class LogRingBuffer
{
    private readonly Queue<LogEntry> _entries;
    private readonly Lock _lock = new();

    public LogRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _entries = new Queue<LogEntry>(capacity);
    }

    public int Capacity { get; }

    /// <summary>Wird nach dem Einfügen außerhalb der Sperre ausgelöst (kein Deadlock-Risiko für Abonnenten).</summary>
    public event Action<LogEntry>? EntryAdded;

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == Capacity)
            {
                _entries.Dequeue();
            }

            _entries.Enqueue(entry);
        }

        EntryAdded?.Invoke(entry);
    }

    /// <summary>Leert den Puffer (Clear auf der Log-Seite). Kein Ereignis — der Aufrufer räumt seine Ansicht selbst auf.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            return _entries.ToArray();
        }
    }
}
