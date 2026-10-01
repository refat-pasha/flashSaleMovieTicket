using System.Security.Claims;
using FlashSale.Api.Contracts;
using FlashSale.Domain.Entities;
using FlashSale.Domain.Enums;
using FlashSale.Infrastructure.Concurrency;
using FlashSale.Infrastructure.Identity;
using FlashSale.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FlashSale.Api.Controllers;

/// <summary>
/// Seat-claim endpoint. This is the contended hot path of the whole system:
/// thousands of admitted buyers race for the same handful of rows.
/// </summary>
[ApiController]
[Route("api/bookings")]
[Authorize]
public sealed class BookingController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly KeyedAsyncLock _keyedLock;
    private readonly ILogger<BookingController> _logger;
    private readonly TimeProvider _timeProvider;

    public BookingController(
        ApplicationDbContext context,
        KeyedAsyncLock keyedLock,
        ILogger<BookingController> logger,
        TimeProvider timeProvider)
    {
        _context = context;
        _keyedLock = keyedLock;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Attempts to reserve a seat for the authenticated buyer.
    /// <para>
    /// <b>409 Conflict</b> is returned when the seat was claimed by someone else in the
    /// meantime — the canonical flash-sale race, detected through the
    /// <c>rowversion</c> concurrency token.
    /// </para>
    /// </summary>
    [HttpPost("reserve")]
    [ProducesResponseType(typeof(ReserveTicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReserveTicketResponse>> Reserve(
        ReserveTicketRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.TicketId == Guid.Empty)
        {
            return ProblemResponse(
                StatusCodes.Status400BadRequest, "invalid_request", "A ticketId is required.");
        }

        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return ProblemResponse(
                StatusCodes.Status401Unauthorized, "unauthenticated", "The token carries no usable subject claim.");
        }

        // ---- Gate 1: the waiting-room claim -------------------------------------
        // Mirrors the Angular route guard. The client-side guard is UX only; this
        // server-side check is the actual security boundary and cannot be bypassed.
        var checkoutAllowed = User.FindFirst(JwtProvider.CheckoutAllowedClaim)?.Value;
        if (!string.Equals(checkoutAllowed, "true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "User {UserId} attempted to reserve ticket {TicketId} without queue admission.",
                userId,
                request.TicketId);

            return ProblemResponse(
                StatusCodes.Status403Forbidden,
                "checkout_not_allowed",
                "You have not been admitted through the waiting room yet.");
        }

        // ---- Gate 2: one live hold per buyer -------------------------------------
        // Without this a buyer could stack unlimited holds (one click per seat),
        // which starves other buyers and made the checkout screen a dead end.
        var existingHold = await FindActiveHoldAsync(request.TicketId, userId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (existingHold is not null)
        {
            return ProblemResponse(
                StatusCodes.Status409Conflict,
                "already_holding_seat",
                $"You already hold seat {existingHold.SeatNumber}. Release it before choosing another.");
        }

        // Try the requested seat, then the optional fallback in the same request.
        var outcome = await TryClaimAsync(request.TicketId, userId.Value, cancellationToken).ConfigureAwait(false);

        if (outcome.Result == ClaimOutcome.Taken && request.FallbackTicketId is { } fallback && fallback != request.TicketId)
        {
            outcome = await TryClaimAsync(fallback, userId.Value, cancellationToken).ConfigureAwait(false);
        }

        switch (outcome.Result)
        {
            case ClaimOutcome.Success:
                return Ok(new ReserveTicketResponse(
                    outcome.Ticket!.Id,
                    outcome.Ticket.SeatNumber,
                    outcome.Ticket.Status,
                    outcome.Ticket.ReservedUntilUtc ?? DateTimeOffset.UtcNow));

            case ClaimOutcome.NotFound:
                return ProblemResponse(
                    StatusCodes.Status404NotFound, "ticket_not_found", "That seat does not exist.");

            case ClaimOutcome.AlreadyOwned:
                return ProblemResponse(
                    StatusCodes.Status409Conflict,
                    "already_reserved_by_you",
                    "You already hold this seat.");

            default:
                // The seat was claimed by another buyer between our read and our write.
                _logger.LogWarning(
                    "Concurrency conflict for ticket {TicketId}: another buyer claimed it first.",
                    request.TicketId);

                return ProblemResponse(
                    StatusCodes.Status409Conflict,
                    "ticket_taken",
                    "That seat was just taken by another buyer. Pick another seat.");
        }
    }
    /// <summary>
    /// Returns the buyer's live hold or purchase for the same sale, or null.
    /// Scoped per event so a buyer may hold a seat on one sale while browsing another.
    /// </summary>
    private async Task<Ticket?> FindActiveHoldAsync(
        Guid ticketId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var eventId = await _context.Tickets
            .AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => (Guid?)t.EventId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (eventId is null)
        {
            return null;
        }

        return await _context.Tickets
            .AsNoTracking()
            .Where(t => t.EventId == eventId.Value
                        && t.AssignedUserId == userId
                        && (t.Status == TicketStatus.Reserved.ToString()
                            || t.Status == TicketStatus.Sold.ToString()))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Releases a held seat back to the pool. This is what lets a buyer change
    /// their mind instead of being stranded on the checkout screen.
    /// </summary>
    [HttpPost("release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Release(
        [FromBody] TicketActionRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.TicketId == Guid.Empty)
        {
            return ProblemResponse(StatusCodes.Status400BadRequest, "invalid_request", "A ticketId is required.");
        }

        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized, "unauthenticated", "No usable subject claim.");
        }

        var ticket = await _context.Tickets
            .SingleOrDefaultAsync(t => t.Id == request.TicketId, cancellationToken)
            .ConfigureAwait(false);

        if (ticket is null || ticket.AssignedUserId != userId)
        {
            return ProblemResponse(StatusCodes.Status409Conflict, "not_your_seat", "You do not hold that seat.");
        }

        if (ticket.Status == TicketStatus.Sold.ToString())
        {
            return ProblemResponse(
                StatusCodes.Status409Conflict, "already_sold", "A confirmed seat cannot be released.");
        }

        ticket.Status = TicketStatus.Available.ToString();
        ticket.AssignedUserId = null;
        ticket.ReservedUntilUtc = null;

        try
        {
            await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ProblemResponse(StatusCodes.Status409Conflict, "ticket_taken", "That seat changed state. Try again.");
        }

        return NoContent();
    }

    /// <summary>
    /// The buyer's current seat for a sale, so the UI can restore state after a
    /// refresh instead of showing an empty map.
    /// </summary>
    [HttpGet("mine")]
    [ProducesResponseType(typeof(ReserveTicketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<ReserveTicketResponse>> MySeat(
        [FromQuery] Guid eventId,
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return ProblemResponse(StatusCodes.Status401Unauthorized, "unauthenticated", "No usable subject claim.");
        }

        var ticket = await _context.Tickets
            .AsNoTracking()
            .Where(t => t.EventId == eventId
                        && t.AssignedUserId == userId
                        && (t.Status == TicketStatus.Reserved.ToString()
                            || t.Status == TicketStatus.Sold.ToString()))
            .OrderByDescending(t => t.Status == TicketStatus.Sold.ToString())
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (ticket is null)
        {
            return NoContent();
        }

        return Ok(new ReserveTicketResponse(
            ticket.Id,
            ticket.SeatNumber,
            ticket.Status,
            ticket.ReservedUntilUtc ?? DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Attempts to claim one seat, guarded by the per-ticket async lock.
    /// <para>
    /// Thread safety is layered:
    /// <list type="number">
    ///   <item><description><see cref="KeyedAsyncLock"/> serialises concurrent attempts on this
    ///   exact seat inside this process, cutting wasted round-trips.</description></item>
    ///   <item><description>The pre-check avoids pointless writes for an obviously-sold seat.</description></item>
    ///   <item><description>Optimistic concurrency (<c>rowversion</c>) is the real guarantee,
    ///   covering other API instances and any writer the lock cannot see.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<ClaimResult> TryClaimAsync(
        Guid ticketId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await _keyedLock.RunAsync(
            KeyedAsyncLock.ForTicket(ticketId),
            async ct =>
            {
                var now = _timeProvider.GetUtcNow();

                // No tracking: we are about to mutate, but we want a single UPDATE
                // statement rather than a graph walk.
                var ticket = await _context.Tickets
                    .SingleOrDefaultAsync(t => t.Id == ticketId, ct)
                    .ConfigureAwait(false);

                if (ticket is null)
                {
                    return new ClaimResult(ClaimOutcome.NotFound, null);
                }

                // Fast-fail on an obviously unavailable seat. Deliberately not authoritative:
                // the truth is whatever the database says when we try to write.
                if (!ticket.IsClaimable)
                {
                    return new ClaimResult(ClaimOutcome.Taken, null);
                }

                if (ticket.AssignedUserId == userId)
                {
                    return new ClaimResult(ClaimOutcome.AlreadyOwned, ticket);
                }

                ticket.Status = TicketStatus.Reserved.ToString();
                ticket.AssignedUserId = userId;
                ticket.ReservedUntilUtc = now.AddSeconds(ReservedHoldSeconds);

                try
                {
                    // The INSERT/UPDATE carries RowVersion in its WHERE clause. If another
                    // transaction updated this row first, zero rows are affected and EF throws.
                    await _context.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // ---- Lost the race. Another buyer claimed this seat milliseconds ago.
                    _logger.LogInformation(
                        ex,
                        "Concurrency conflict on ticket {TicketId} for user {UserId}.",
                        ticketId,
                        userId);

                    // Detach the stale entity so the scoped DbContext stays usable if the
                    // caller (or a retry) continues to use it.
                    _context.Entry(ticket).State = EntityState.Detached;

                    return new ClaimResult(ClaimOutcome.Taken, null);
                }
                catch (DbUpdateException ex)
                {
                    // e.g. the unique (EventId, SeatNumber) index fired.
                    _logger.LogWarning(ex, "Database rejected the claim for ticket {TicketId}.", ticketId);
                    _context.Entry(ticket).State = EntityState.Detached;

                    return new ClaimResult(ClaimOutcome.Taken, null);
                }

                return new ClaimResult(ClaimOutcome.Success, ticket);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How long an unconfirmed hold is held before it can be reclaimed.</summary>
    private const int ReservedHoldSeconds = 120;

    /// <summary>Resolves the caller's id, returning null rather than throwing on a bad token.</summary>
    private Guid? GetCurrentUserId()
    {
        var raw = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(raw, out var userId) && userId != Guid.Empty ? userId : null;
    }

    private ObjectResult ProblemResponse(int statusCode, string code, string message) =>
        StatusCode(statusCode, new ApiError(code, message, HttpContext.TraceIdentifier));

    private enum ClaimOutcome
    {
        Success,
        Taken,
        NotFound,
        AlreadyOwned
    }

    private readonly record struct ClaimResult(ClaimOutcome Result, Ticket? Ticket);
}
