using FlashSale.Domain.Enums;

namespace FlashSale.Domain.Entities;

/// <summary>
/// Durable record of a buyer's position in the virtual waiting room.
/// The in-memory <c>IQueueStore</c> holds the live ordering, while this table
/// provides the persistent queue that survives an API restart.
/// </summary>
public class QueueEntry
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>One of <see cref="QueueStatus"/>.</summary>
    public string Status { get; set; } = nameof(QueueStatus.Waiting);

    /// <summary>
    /// Monotonic sequence number assigned on arrival. Ordering by this value is
    /// stable and gap-free, which is why it is preferred over an offset-based rank.
    /// </summary>
    public long SequenceNumber { get; set; }

    public DateTimeOffset EnqueuedAtUtc { get; set; }

    public DateTimeOffset? AdmittedAtUtc { get; set; }

    /// <summary>When the granted checkout pass stops being honoured.</summary>
    public DateTimeOffset? CheckoutPassExpiresAtUtc { get; set; }

    public Event Event { get; set; } = null!;

    public User User { get; set; } = null!;
}