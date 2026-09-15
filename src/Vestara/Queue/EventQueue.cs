using Vestara.Models;
using Vestara.Persistence;

namespace Vestara.Queue;

public sealed class EventQueue
{
    private const int MaxCapacity = 500;
    private readonly List<QueuedEvent> _items = new();
    private readonly object _lock = new();
    private long _revision = 0;

    public long Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public int InFlightCount
    {
        get
        {
            lock (_lock)
            {
                return _items.Count(i => i.IsInFlight);
            }
        }
    }

    public QueuedEvent? Enqueue(VestaraEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        lock (_lock)
        {
            return EnqueueLocked(@event);
        }
    }

    public QueuedEvent? EnqueueFatalAndPersist(VestaraEvent @event, FileEventStorage storage)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(storage);

        lock (_lock)
        {
            var queued = EnqueueLocked(@event);
            if (queued == null)
            {
                return null;
            }

            var snapshot = new List<QueuedEvent>(_items);
            var revision = _revision;

            storage.SaveEvents(snapshot, revision, isFatal: true);
            return queued;
        }
    }

    private QueuedEvent? EnqueueLocked(VestaraEvent @event)
    {
        var isCrash = @event.Origin == EmissionOrigin.Crash ||
                      string.Equals(@event.EventType, "crash", StringComparison.OrdinalIgnoreCase);

        if (_items.Count >= MaxCapacity)
        {
            if (isCrash)
            {
                // 1. Evict oldest non-in-flight non-crash first
                var indexToEvict = _items.FindIndex(i => !i.IsInFlight && !IsCrash(i.Event));
                if (indexToEvict >= 0)
                {
                    _items.RemoveAt(indexToEvict);
                }
                else
                {
                    // 2. If all non-in-flight are crashes, evict oldest non-in-flight crash
                    var crashIndexToEvict = _items.FindIndex(i => !i.IsInFlight && IsCrash(i.Event));
                    if (crashIndexToEvict >= 0)
                    {
                        _items.RemoveAt(crashIndexToEvict);
                    }
                    else
                    {
                        // All 500 items are in-flight, cannot evict without corrupting active upload
                        return null;
                    }
                }
            }
            else
            {
                // Non-crash event: evict oldest non-in-flight non-crash only
                var indexToEvict = _items.FindIndex(i => !i.IsInFlight && !IsCrash(i.Event));
                if (indexToEvict >= 0)
                {
                    _items.RemoveAt(indexToEvict);
                }
                else
                {
                    // No eligible non-crash event exists (all eligible items are crashes or in-flight).
                    // Normal log must NEVER displace a crash. Drop incoming event.
                    return null;
                }
            }
        }

        var queuedEvent = new QueuedEvent
        {
            QueueId = Guid.NewGuid().ToString("n"),
            Event = @event,
            IsInFlight = false
        };

        _items.Add(queuedEvent);
        _revision++;
        return queuedEvent;
    }

    private static bool IsCrash(VestaraEvent e)
    {
        return e.Origin == EmissionOrigin.Crash ||
               string.Equals(e.EventType, "crash", StringComparison.OrdinalIgnoreCase);
    }

    public List<QueuedEvent> GetBatch(int maxBatchSize = 100)
    {
        lock (_lock)
        {
            var batch = new List<QueuedEvent>();
            foreach (var item in _items)
            {
                if (!item.IsInFlight)
                {
                    item.IsInFlight = true;
                    batch.Add(item);
                    if (batch.Count >= maxBatchSize)
                    {
                        break;
                    }
                }
            }

            return batch;
        }
    }

    public void AcknowledgeSuccessful(IEnumerable<string> queueIds)
    {
        var idSet = new HashSet<string>(queueIds, StringComparer.Ordinal);
        lock (_lock)
        {
            var removed = _items.RemoveAll(i => idSet.Contains(i.QueueId));
            if (removed > 0)
            {
                _revision++;
            }
        }
    }

    public void ReleaseInFlight(IEnumerable<string> queueIds)
    {
        var idSet = new HashSet<string>(queueIds, StringComparer.Ordinal);
        lock (_lock)
        {
            bool changed = false;
            foreach (var item in _items)
            {
                if (idSet.Contains(item.QueueId) && item.IsInFlight)
                {
                    item.IsInFlight = false;
                    changed = true;
                }
            }
            if (changed)
            {
                _revision++;
            }
        }
    }

    public void RemovePoison(IEnumerable<string> queueIds)
    {
        var idSet = new HashSet<string>(queueIds, StringComparer.Ordinal);
        lock (_lock)
        {
            var removed = _items.RemoveAll(i => idSet.Contains(i.QueueId));
            if (removed > 0)
            {
                _revision++;
            }
        }
    }

    public List<QueuedEvent> GetAllForPersistence()
    {
        lock (_lock)
        {
            return new List<QueuedEvent>(_items);
        }
    }

    public (List<QueuedEvent> Events, long Revision) GetPersistenceSnapshot()
    {
        lock (_lock)
        {
            return (new List<QueuedEvent>(_items), _revision);
        }
    }

    public void SnapshotAndPersist(FileEventStorage storage, bool isFatal = false)
    {
        List<QueuedEvent> snapshot;
        long revision;
        lock (_lock)
        {
            snapshot = new List<QueuedEvent>(_items);
            revision = _revision;
        }
        storage.SaveEvents(snapshot, revision, isFatal);
    }

    public void MergeRecoveredEvents(IEnumerable<QueuedEvent> recoveredEvents)
    {
        lock (_lock)
        {
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<QueuedEvent>();

            // 1. Add recovered durable events first (FIFO older history), setting IsInFlight = false
            foreach (var item in recoveredEvents)
            {
                if (string.IsNullOrWhiteSpace(item.QueueId) || !seenIds.Add(item.QueueId))
                {
                    continue;
                }
                item.IsInFlight = false;
                merged.Add(item);
            }

            // 2. Add currently live startup events, preserving their queue identity and state
            foreach (var liveItem in _items)
            {
                if (seenIds.Add(liveItem.QueueId))
                {
                    merged.Add(liveItem);
                }
            }

            // 3. Enforce MaxCapacity (500) respecting crash-priority policy
            while (merged.Count > MaxCapacity)
            {
                var idxToEvict = merged.FindIndex(i => !i.IsInFlight && !IsCrash(i.Event));
                if (idxToEvict >= 0)
                {
                    merged.RemoveAt(idxToEvict);
                }
                else
                {
                    var crashIdx = merged.FindIndex(i => !i.IsInFlight && IsCrash(i.Event));
                    if (crashIdx >= 0)
                    {
                        merged.RemoveAt(crashIdx);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            _items.Clear();
            _items.AddRange(merged);
            _revision++;
        }
    }

    public void LoadRecoveredEvents(IEnumerable<QueuedEvent> recoveredEvents)
    {
        MergeRecoveredEvents(recoveredEvents);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _revision++;
        }
    }
}
