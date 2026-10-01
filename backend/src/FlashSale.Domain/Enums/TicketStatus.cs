namespace FlashSale.Domain.Enums;

/// <summary>
/// Lifecycle of a single seat in the sale inventory.
/// Persisted as a string so the database stays readable and reorderable.
/// </summary>
public enum TicketStatus
{
    /// <summary>Seat is on sale and can be claimed by exactly one buyer.</summary>
    Available = 0,

    /// <summary>
    /// Seat is held by <see cref="Entities.Ticket.AssignedUserId"/> while checkout completes.
    /// A hold that is never confirmed is reclaimed by the reservation sweeper.
    /// </summary>
    Reserved = 1,

    /// <summary>Payment captured. Terminal state.</summary>
    Sold = 2
}