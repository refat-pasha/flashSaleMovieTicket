namespace FlashSale.Infrastructure.Queueing;

/// <summary>
/// Live, in-memory waiting room shared between <c>QueueHub</c> (which populates it)
/// and <c>QueueBackgroundWorker</c> (which drains it).
/// <para>
/// All members are thread-safe. Implementations must guarantee atomicity for
/// enqueue/dequeue, because the hub is invoked concurrently by every open socket.
/// </para>
/// </summary>
public interface IQueueStore
{
    /// <summary>
    /// Adds (or refreshes) a buyer's place in line. Idempotent per (event, user):
    /// a reconnect from a new connection id reuses the original position rather than
    /// letting a buyer jump the queue by cycling their connection.
    /// </summary>
    QueueParticipant Enqueue(Guid eventId, Guid userId, string connectionId, DateTimeOffset nowUtc);

    /// <summary>Removes a buyer by connection, e.g. on hub disconnect.</summary>
    bool RemoveByConnection(string connectionId);

    /// <summary>Removes a buyer by identity from a specific sale's queue.</summary>
    bool Remove(Guid eventId, Guid userId);

    /// <summary>Current snapshot of the buyer's record, if present.</summary>
    QueueParticipant? Find(Guid eventId, Guid userId);

    /// <summary>All participants for a sale, ordered closest-to-front first.</summary>
    IReadOnlyList<QueueParticipant> Snapshot(Guid eventId);

    /// <summary>Number of buyers currently waiting for a sale.</summary>
    int Count(Guid eventId);

    /// <summary>
    /// Atomically pops up to <paramref name="count"/> participants from the front of
    /// the queue. Returns the admitted batch in order.
    /// </summary>
    IReadOnlyList<QueueParticipant> DequeueBatch(Guid eventId, int count);

    /// <summary>
    /// Sale ids that currently have at least one buyer waiting. Lets the background
    /// worker sweep only the queues that are actually active instead of every event.
    /// </summary>
    IReadOnlyCollection<Guid> ActiveEventIds();

    /// <summary>
    /// 1-based position of a buyer within its sale queue, or null when not queued.
    /// </summary>
    int? PositionOf(Guid eventId, Guid userId);
}