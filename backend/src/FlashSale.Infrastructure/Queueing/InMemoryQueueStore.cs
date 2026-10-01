using System.Collections.Concurrent;

namespace FlashSale.Infrastructure.Queueing;

/// <summary>
/// In-memory, lock-protected implementation of <see cref="IQueueStore"/>.
/// <para>
/// Concurrency model: a single <c>object</c> gate guards a per-event
/// <see cref="SortedDictionary{TKey,TValue}"/> ordered by
/// <see cref="QueueParticipant.SequenceNumber"/>. Ordering by sequence number gives
/// stable FIFO with no rebalancing on removal — cheaper than a linked list under heavy
/// churn, and still O(log n) per operation. Reads take the same lock but never block
/// across I/O, and nothing here awaits, so the lock is never held across a thread hop.
/// </para>
/// Registered as a singleton: the store must be shared by the hub and the background
/// worker inside a single API instance.
/// </summary>
public sealed class InMemoryQueueStore : IQueueStore
{
    private readonly object _gate = new();

    /// <summary>Sale id -> (sequence -> participant), iterated front-of-line first.</summary>
    private readonly Dictionary<Guid, SortedDictionary<long, QueueParticipant>> _queues = [];

    /// <summary>Connection id -> (event id, user id), so disconnects resolve in O(1).</summary>
    private readonly ConcurrentDictionary<string, (Guid EventId, Guid UserId)> _byConnection = new();

    /// <summary>Global monotonic counter giving a total order across all sales.</summary>
    private long _sequence;

    public QueueParticipant Enqueue(Guid eventId, Guid userId, string connectionId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var queue = GetOrCreateQueue(eventId);

            // Idempotent re-join: refresh the socket but keep the original position so a
            // buyer cannot skip the line by reconnecting.
            var existing = queue.Values.FirstOrDefault(p => p.UserId == userId);
            if (existing is not null)
            {
                var refreshed = existing with { ConnectionId = connectionId };
                queue[existing.SequenceNumber] = refreshed;
                TrackConnection(existing.ConnectionId, connectionId, eventId, userId);
                return refreshed;
            }

            var participant = new QueueParticipant(
                eventId,
                userId,
                connectionId,
                Interlocked.Increment(ref _sequence),
                nowUtc);

            queue[participant.SequenceNumber] = participant;
            _byConnection[connectionId] = (eventId, userId);

            return participant;
        }
    }

    public bool RemoveByConnection(string connectionId)
    {
        if (!_byConnection.TryRemove(connectionId, out var key))
        {
            return false;
        }

        lock (_gate)
        {
            return RemoveInternal(key.EventId, key.UserId);
        }
    }

    public bool Remove(Guid eventId, Guid userId)
    {
        lock (_gate)
        {
            return RemoveInternal(eventId, userId);
        }
    }

    public QueueParticipant? Find(Guid eventId, Guid userId)
    {
        lock (_gate)
        {
            return _queues.TryGetValue(eventId, out var queue)
                ? queue.Values.FirstOrDefault(p => p.UserId == userId)
                : null;
        }
    }

    public IReadOnlyList<QueueParticipant> Snapshot(Guid eventId)
    {
        lock (_gate)
        {
            return _queues.TryGetValue(eventId, out var queue) ? queue.Values.ToArray() : [];
        }
    }

    public int Count(Guid eventId)
    {
        lock (_gate)
        {
            return _queues.TryGetValue(eventId, out var queue) ? queue.Count : 0;
        }
    }
    public IReadOnlyList<QueueParticipant> DequeueBatch(Guid eventId, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            if (!_queues.TryGetValue(eventId, out var queue) || queue.Count == 0)
            {
                return [];
            }

            var take = Math.Min(count, queue.Count);
            var admitted = new List<QueueParticipant>(take);

            // SortedDictionary enumerates in SequenceNumber order, so the first `take`
            // entries are exactly the buyers closest to the front of the line.
            foreach (var sequence in queue.Keys.Take(take).ToArray())
            {
                var participant = queue[sequence];
                queue.Remove(sequence);
                _byConnection.TryRemove(participant.ConnectionId, out _);
                admitted.Add(participant);
            }

            if (queue.Count == 0)
            {
                _queues.Remove(eventId);
            }

            return admitted;
        }
    }

    public IReadOnlyCollection<Guid> ActiveEventIds()
    {
        lock (_gate)
        {
            return _queues.Keys.ToArray();
        }
    }

    public int? PositionOf(Guid eventId, Guid userId)
    {
        lock (_gate)
        {
            if (!_queues.TryGetValue(eventId, out var queue))
            {
                return null;
            }

            var index = 0;
            foreach (var participant in queue.Values)
            {
                index++;
                if (participant.UserId == userId)
                {
                    return index;
                }
            }

            return null;
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private SortedDictionary<long, QueueParticipant> GetOrCreateQueue(Guid eventId)
    {
        if (!_queues.TryGetValue(eventId, out var queue))
        {
            queue = new SortedDictionary<long, QueueParticipant>();
            _queues[eventId] = queue;
        }

        return queue;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private bool RemoveInternal(Guid eventId, Guid userId)
    {
        if (!_queues.TryGetValue(eventId, out var queue))
        {
            return false;
        }

        var existing = queue.Values.FirstOrDefault(p => p.UserId == userId);
        if (existing is null)
        {
            return false;
        }

        queue.Remove(existing.SequenceNumber);
        _byConnection.TryRemove(existing.ConnectionId, out _);

        // Drop the now-empty bucket so the dictionary cannot grow unbounded across sales.
        if (queue.Count == 0)
        {
            _queues.Remove(eventId);
        }

        return true;
    }

    /// <summary>Migrates the connection mapping when a buyer reconnects on a new socket.</summary>
    private void TrackConnection(string previousConnection, string currentConnection, Guid eventId, Guid userId)
    {
        if (!string.Equals(previousConnection, currentConnection, StringComparison.Ordinal))
        {
            _byConnection.TryRemove(previousConnection, out _);
            _byConnection[currentConnection] = (eventId, userId);
        }
    }
}