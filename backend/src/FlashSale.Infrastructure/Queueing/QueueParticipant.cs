using System.Collections.Concurrent;

namespace FlashSale.Infrastructure.Queueing;

/// <summary>
/// A single buyer currently holding a place in the in-memory waiting room.
/// Immutable in practice: the store replaces instances rather than mutating them,
/// so readers always observe a consistent snapshot without locking.
/// </summary>
/// <param name="EventId">Sale the buyer is queued for.</param>
/// <param name="UserId">The buyer.</param>
/// <param name="ConnectionId">SignalR connection used to address this buyer's client.</param>
/// <param name="SequenceNumber">Monotonic arrival order; lower means closer to the front.</param>
/// <param name="EnqueuedAtUtc">Arrival timestamp.</param>
public sealed record QueueParticipant(
    Guid EventId,
    Guid UserId,
    string ConnectionId,
    long SequenceNumber,
    DateTimeOffset EnqueuedAtUtc);