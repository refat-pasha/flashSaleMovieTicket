using System.ComponentModel.DataAnnotations;

namespace FlashSale.Api.Contracts;

/// <summary>Request to claim a seat. Identifies the seat and proves queue admission.</summary>
public sealed class ReserveTicketRequest
{
    /// <summary>Seat the buyer wants.</summary>
    public Guid TicketId { get; set; }

    /// <summary>
    /// Optional alternate seat to try if the primary is already gone. Lets a buyer win
    /// a seat in the same request rather than bouncing back to the event grid.
    /// </summary>
    public Guid? FallbackTicketId { get; set; }
}

/// <summary>Successful reservation result.</summary>
/// <param name="TicketId">The seat actually claimed.</param>
/// <param name="SeatNumber">Its human-readable label.</param>
/// <param name="Status">"Reserved" or "Sold".</param>
/// <param name="HoldExpiresAtUtc">When an unconfirmed hold lapses.</param>
public sealed record ReserveTicketResponse(
    Guid TicketId,
    string SeatNumber,
    string Status,
    DateTimeOffset HoldExpiresAtUtc);

/// <summary>Uniform error body so the Angular client can branch on a stable code.</summary>
/// <param name="Code">Machine-readable discriminator, e.g. "ticket_taken".</param>
/// <param name="Message">Human-readable explanation safe to surface in the UI.</param>
/// <param name="TraceId">Correlation id for log lookup.</param>
public sealed record ApiError(string Code, string Message, string? TraceId = null);

/// <summary>
/// Request body for the confirm / release actions on a held seat.
/// </summary>
public sealed class TicketActionRequest
{
    /// <summary>The seat the buyer currently holds.</summary>
    public Guid TicketId { get; set; }
}

/// <summary>Request to join the virtual waiting room for a sale.</summary>
public sealed class JoinQueueRequest
{
    [Required]
    public Guid EventId { get; set; }
}